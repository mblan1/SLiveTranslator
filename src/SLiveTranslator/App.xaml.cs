using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using SLiveTranslator.Capture;
using SLiveTranslator.Diagnostics;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Overlay;
using SLiveTranslator.UI.Pages;
using Drawing = System.Drawing;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator;

public partial class App : Application, ISettingsHost
{
    /// <summary>How long "Preview on screen" shows its sample subtitle.</summary>
    static readonly TimeSpan PreviewTime = TimeSpan.FromSeconds(5);

    /// <summary>How long the minimized main window gets to hand the foreground to the window behind it.</summary>
    static readonly TimeSpan MainWindowAsideTime = TimeSpan.FromMilliseconds(300);

    const string InstanceMutexName = @"Local\SLiveTranslator.Instance";

    readonly SettingsStore _store = new(DataPaths.Current.SettingsFile);
    readonly DailyCharCounter _myMemoryUsage = new(DataPaths.Current.MyMemoryUsageFile);
    readonly ProviderRegistry _providers;
    readonly GameProfileStore _profiles = new(DataPaths.Current.ProfilesDir);
    readonly Notifier _notifier;
    readonly AlertRules _alerts;
    readonly StartupRegistration _startup = new(new RunKey(), Environment.ProcessPath);
    readonly DataFolderMover _dataFolders = new(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.GetDirectoryName(Environment.ProcessPath));
    AppSettings _settings = new();
    OverlayHost? _overlay;
    SubtitlePresenter? _presenter;
    Forms.NotifyIcon? _tray;
    Forms.ContextMenuStrip? _trayMenu;
    TrayIcon? _trayIcons;
    HotkeyManager? _hotkeys;
    bool _hotkeyTrial;
    IOcrEngine? _ocr;
    CachedTranslator? _translator;
    SubtitleWatcher? _subtitles;
    ScreenTranslator? _screenTranslator;
    CancellationTokenSource? _screenCts;
    DispatcherTimer? _clearTimer;
    DispatcherTimer? _previewTimer;
    Mutex? _singleInstance;

    /// <summary>The main window while it is open. Typed as a plain Window: a run that never opens it never loads WPF-UI.</summary>
    Window? _mainWindow;

    /// <summary>Why the selected provider does not translate right now (no key, not answering); null while it does.</summary>
    string? _providerProblem;

    public App()
    {
        _providers = new ProviderRegistry(DataPaths.Current.CacheFile, _myMemoryUsage);
        _notifier = new Notifier(new AppNoticeSurface(Dispatcher, () => _overlay, () => _tray), () => _settings.Notifications,
            () => _settings.Subtitles.Region?.ToRectangle());
        _alerts = new AlertRules(_notifier);
    }

    void OnStartup(object sender, StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("unhandled: " + args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("dispatcher: " + args.Exception);
            args.Handled = true;
            ShowError(Loc.F("Notice.Unexpected", args.Exception.Message));
        };
        _settings = _store.Load();
        Loc.Instance.SetLanguage(_settings.Ui.Language);
        Log.Level = _settings.Storage.LogLevel;
        Log.MaxBytes = Math.Max(64, _settings.Storage.LogMaxKB) * 1024L;
        var problems = SettingsProblems();

        // "--startup" (Start with Windows) is taken out before the "--" CLI branch, which would refuse it.
        var start = StartupArgs.Parse(e.Args);
        if (start.Cli is { } cli)
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            foreach (var problem in problems) Console.Error.WriteLine(problem);
            _ = RunCliAsync(cli);
            return;
        }

        // A second copy cannot get the hotkeys and only causes confusion.
        _singleInstance = new Mutex(true, InstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show(Loc.Instance["App.AlreadyRunning"], "SLiveTranslator", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Stores a version-1 file as schema 2 right away, so a plain-text API key gets encrypted (the original stays as .bak-v1).
        if (_store.MigratedFromV1) TrySaveSettings();

        problems.AddRange(BuildServices());
        problems.AddRange(_profiles.Problems);
        _overlay = new OverlayHost();
        _providers.ServiceSwitched += OnServiceSwitched;
        _myMemoryUsage.Counted += OnMyMemoryCounted;

        _presenter = new SubtitlePresenter(Dispatcher, _overlay, () => _subtitles?.Region ?? default, () => _settings.Overlay,
            () => Hotkey(HotkeyAction.History), ShowError,
            notice => _notifier.Notify(NoticeCategory.Warning, Loc.F("Notice.FixRegion", notice, Hotkey(HotkeyAction.SelectRegion)), _subtitles?.Region),
            ShowLiveBadge);
        _screenTranslator = new ScreenTranslator(() => _ocr!, () => _translator!, Langs, ForegroundProfile);
        _subtitles = new SubtitleWatcher(() => _ocr!, () => _translator!, SubtitleConfig, _presenter) { Profiles = ProfileFor };

        _clearTimer = new DispatcherTimer();
        _clearTimer.Tick += (_, _) =>
        {
            _clearTimer.Stop();
            _overlay.ClearLabels();
        };

        CreateTray();
        Loc.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == Binding.IndexerName) RebuildTray();
        };
        if (RegisterHotkeys() is { } hotkeyProblem) problems.Add(hotkeyProblem);

        // Shown before the first-run window, so a reset to defaults is explained before the user saves over it.
        ShowProblems(problems);
        if (start.Quiet) return; // started with Windows: in the tray, without the "running" notices
        if (!_settings.General.FirstRunDone)
        {
            ShowMainWindow(welcome: true);
            return;
        }
        string keys = Loc.F("Notice.Keys", Hotkey(HotkeyAction.ToggleSubtitles), Hotkey(HotkeyAction.TranslateScreen), Hotkey(HotkeyAction.History));
        _notifier.StartupBalloon(Loc.Instance["Notice.Running"], keys);
        if (problems.Count == 0) _notifier.Feedback(NoticeCategory.Status, Loc.F("Notice.Ready", keys), Forms.Screen.PrimaryScreen?.Bounds);
    }

    /// <summary>Problems with the data folder or settings.json found while loading; logged here, shown once the overlay is up.</summary>
    List<string> SettingsProblems()
    {
        var problems = new List<string>();
        if (DataPaths.Current.UnavailableLocation is { } missing)
            problems.Add(Loc.F("Startup.DataFolderUnavailable", missing, DataPaths.Current.Root));
        if (_settings.IsNewerSchema)
            problems.Add(Loc.F("Startup.NewerSchema", _settings.SchemaVersion));
        foreach (var problem in problems) Log.Warn("startup: " + problem);
        if (_store.LoadProblem is { } loadProblem) problems.Add(loadProblem); // the store logged it
        return problems;
    }

    /// <summary>Every problem in one warning: a later toast would replace an earlier one.</summary>
    void ShowProblems(List<string> problems)
    {
        if (problems.Count > 0) _notifier.Feedback(NoticeCategory.Warning, string.Join("\n", problems), Forms.Screen.PrimaryScreen?.Bounds);
    }

    string Hotkey(HotkeyAction action) => _settings.Hotkeys[action];

    (string Src, string Tgt) Langs() =>
        (LangCodes.FromOcrTag(_ocr?.LanguageTag), string.IsNullOrWhiteSpace(_settings.General.TargetLanguage) ? "en" : _settings.General.TargetLanguage);

    WatcherConfig SubtitleConfig()
    {
        var (src, tgt) = Langs();
        var subtitles = _settings.Subtitles;
        var translation = _settings.Translation;
        return new WatcherConfig(src, tgt, subtitles.IntervalMs, subtitles.ParagraphGapRatio)
        {
            OcrMode = subtitles.OcrMode,
            PauseWhenGameUnfocused = subtitles.PauseWhenGameUnfocused,
            LowImpact = subtitles.LowImpact,
            ContextLines = translation.UseContext ? Math.Clamp(translation.ContextLines, 0, ContextWindow.MaxLines) : 0,
        };
    }

    /// <summary>The game profile the settings ask for, for the game with this process name (null when not known).</summary>
    GameProfile? ProfileFor(string? processName)
    {
        var (src, tgt) = Langs();
        return _profiles.Resolve(_settings.Translation.ProfileId, processName, src, tgt);
    }

    /// <summary>The profile for the program in front: translate-screen reads the game the hotkey was pressed in.</summary>
    GameProfile? ForegroundProfile() => ProfileFor(SystemWindowProbe.Instance.ProcessName(SystemWindowProbe.Instance.Foreground().ProcessId));

    void TrySaveSettings()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifier.Feedback(NoticeCategory.Error, Loc.F("Notice.SettingsNotSaved", ex.Message));
        }
    }

    /// <returns>Problems for the user, already logged. The caller shows them: at startup the overlay does not exist yet.</returns>
    List<string> BuildServices()
    {
        var problems = BuildOcr();
        problems.AddRange(ApplyProviders());
        return problems;
    }

    List<string> BuildOcr()
    {
        try
        {
            _ocr = WindowsOcrEngine.Create(_settings.General.OcrLanguage);
            return [];
        }
        catch (Exception ex)
        {
            _ocr = null;
            Log.Error("ocr init: " + ex);
            return [ex.Message];
        }
    }

    /// <summary>The selected provider, then the free services (alone while its key is missing), behind the one cache of the run.</summary>
    List<string> ApplyProviders()
    {
        var notice = _providers.Apply(_settings).Notice; // the registry logged it
        _translator = _providers.Cache;
        _providerProblem = notice;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return notice is null ? [] : [notice];
    }

    /// <summary>The selected provider gave way to the free services, or came back (see <see cref="AlertRules.ServiceSwitched"/>).</summary>
    void OnServiceSwitched(object? sender, ServiceSwitch e) => Dispatcher.BeginInvoke(() =>
    {
        if (_translator is null) return;
        string id = _settings.Translation.Provider;
        string name = _providers.Providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? e.From;
        _alerts.ServiceSwitched(e, _translator.Name, name, _subtitles?.Region);
        if (e.From == _translator.Name && e.Reason.Length > 0) _providerProblem = Loc.F("Alert.ProviderDown", name, AlertRules.Reason(e.Status));
        else if (e.To == _translator.Name && e.Reason.Length == 0) _providerProblem = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>MyMemory counted characters on a translation thread; its quota is checked on the UI thread, where the settings live.</summary>
    void OnMyMemoryCounted(long today) => Dispatcher.BeginInvoke(() =>
    {
        string email = _settings.Translation.Free.MyMemoryEmail;
        _alerts.MyMemoryUsage(today, MyMemoryTranslator.DailyLimitFor(email), hasEmail: !string.IsNullOrWhiteSpace(email));
        StateChanged?.Invoke(this, EventArgs.Empty);
    });

    bool IsOwnWindow(IntPtr hwnd) =>
        Windows.OfType<Window>().Any(w => new WindowInteropHelper(w).Handle == hwnd);

    DRect GameArea() => ScreenCapture.ForegroundWindowRect(IsOwnWindow);

    void CreateTray()
    {
        _trayIcons = TrayIcon.Load();
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Opening += (_, _) => RebuildTray(); // the state of the moment: subtitles, provider, mute, hotkeys
        RebuildTray();
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcons.Idle,
            Text = Loc.Instance["Tray.Tooltip"],
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    /// <summary>
    /// The tray menu for the app's state and language now (see <see cref="TrayMenu"/>). Made again when it opens and
    /// when the language changes, never from one of its own items: an item must not be thrown away while it is clicked.
    /// </summary>
    void RebuildTray()
    {
        if (_trayMenu is null) return;
        var items = TrayMenu.Build(new TrayState(HomeViewModel.ProviderLineOf(_settings.Translation, UiProviders()), _subtitles?.IsRunning == true,
            _presenter?.HistoryVisible == true, _settings.Notifications.Muted, _settings.Hotkeys, TrayProviders(), _settings.Translation.Provider));
        foreach (var old in _trayMenu.Items.Cast<Forms.ToolStripItem>().ToList()) old.Dispose();
        _trayMenu.Items.Clear();
        foreach (var item in items) _trayMenu.Items.Add(MenuItem(item));
        if (_tray is not null) _tray.Text = Loc.Instance[_subtitles?.IsRunning == true ? "Tray.TooltipLive" : "Tray.Tooltip"];
    }

    Forms.ToolStripItem MenuItem(TrayItem item)
    {
        if (item.Command == TrayCommand.Separator) return new Forms.ToolStripSeparator();
        var menu = new Forms.ToolStripMenuItem(item.Text) { Checked = item.Checked, Enabled = item.Enabled, ShortcutKeyDisplayString = item.Gesture };
        if (item.Items is { } children)
            foreach (var child in children) menu.DropDownItems.Add(MenuItem(child));
        else
            menu.Click += (_, _) => OnTrayCommand(item);
        return menu;
    }

    void OnTrayCommand(TrayItem item)
    {
        switch (item.Command)
        {
            case TrayCommand.Subtitles: ToggleSubtitles(); break;
            case TrayCommand.TranslateScreen: TranslateScreen(); break;
            case TrayCommand.History: ToggleHistory(); break;
            case TrayCommand.SelectRegion: SelectRegion(); break;
            case TrayCommand.Clear: ClearOverlay(); break;
            case TrayCommand.Provider: PickProvider(item.Value!); break;
            case TrayCommand.Mute: ToggleMute(); break;
            case TrayCommand.Open: ShowMainWindow(); break;
            case TrayCommand.OpenDataFolder: OpenFolder(DataPaths.Current.Root); break;
            case TrayCommand.Exit: ExitApp(); break;
        }
    }

    IReadOnlyList<ITranslationProvider> UiProviders() => _providers.Providers.Select(p => p.Provider).OfType<ITranslationProvider>().ToList();

    /// <summary>The tray's providers: the free services, each one the settings can run, and the selected one even when it cannot.</summary>
    IReadOnlyList<Choice<string>> TrayProviders() =>
        new[] { new Choice<string>(ProviderIds.Free, Loc.Instance["Translation.FreeProvider"]) }
            .Concat(_providers.Providers
                .Where(p => p.Unusable(_settings.Translation) is null || p.Id.Equals(_settings.Translation.Provider, StringComparison.OrdinalIgnoreCase))
                .Select(p => new Choice<string>(p.Id, p.DisplayName)))
            .ToList();

    /// <summary>A provider picked in the tray: saved, and the chain is built again.</summary>
    void PickProvider(string id)
    {
        if (id.Equals(_settings.Translation.Provider, StringComparison.OrdinalIgnoreCase)) return;
        _settings.Translation.Provider = id;
        TrySaveSettings();
        var problems = ApplyProviders();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (problems.Count > 0) ShowProblems(problems);
        else _notifier.Feedback(NoticeCategory.Status, Loc.F("Notice.ProviderPicked", HomeViewModel.ProviderLineOf(_settings.Translation, UiProviders())),
            Forms.Screen.PrimaryScreen?.Bounds);
    }

    void ToggleMute()
    {
        _notifier.Muted = !_notifier.Muted;
        TrySaveSettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Log.Warn($"open folder {path}: {ex.Message}");
            _notifier.Feedback(NoticeCategory.Error, Loc.F("Notice.FolderNotOpened", path, ex.Message));
        }
    }

    /// <returns>A problem to show the user, or null when every enabled hotkey is registered.</returns>
    string? RegisterHotkeys()
    {
        _hotkeys ??= new HotkeyManager();
        var results = _hotkeys.RegisterAll(_settings.Hotkeys, new Dictionary<HotkeyAction, Action>
        {
            [HotkeyAction.TranslateScreen] = OnHotkey(HotkeyAction.TranslateScreen, TranslateScreen),
            [HotkeyAction.ToggleSubtitles] = OnHotkey(HotkeyAction.ToggleSubtitles, ToggleSubtitles),
            [HotkeyAction.SelectRegion] = OnHotkey(HotkeyAction.SelectRegion, () => SelectRegion()),
            [HotkeyAction.Clear] = OnHotkey(HotkeyAction.Clear, ClearOverlay),
            [HotkeyAction.History] = OnHotkey(HotkeyAction.History, ToggleHistory),
        });
        if (HotkeyText.Problems(results, _settings.Hotkeys) is not { } problem) return null;
        Log.Warn("hotkeys: " + string.Join(", ", results.Select(r => $"{r.Key}={_settings.Hotkeys[r.Key]}:{r.Value}")));
        return problem;
    }

    /// <summary>
    /// A hotkey press is told to the main window (the wizard's try-out), and done unless the try-out is on in the window
    /// the user is looking at: from the game (the window behind it or minimized) every hotkey works as always.
    /// </summary>
    Action OnHotkey(HotkeyAction action, Action run) => () =>
    {
        HotkeyPressed?.Invoke(this, action);
        if (!(_hotkeyTrial && _mainWindow is { IsActive: true })) run();
    };

    async void TranslateScreen()
    {
        if (_ocr is null || _screenTranslator is null || _overlay is null)
        {
            _notifier.Feedback(NoticeCategory.Error, Loc.Instance["Notice.OcrUnavailable"]);
            return;
        }
        _screenCts?.Cancel();
        var cts = _screenCts = new CancellationTokenSource();
        try
        {
            var area = GameArea();
            var mouse = Forms.Cursor.Position;
            _overlay.ClearLabels();
            _clearTimer?.Stop();
            _notifier.Progress(Loc.Instance["Notice.TranslatingScreen"], area, seconds: 30); // until the result replaces it
            if (!_overlay.ExcludedFromCapture)
            {
                _overlay.Hide();
                await Task.Delay(80, cts.Token);
            }

            void Draw(IReadOnlyList<ScreenTranslator.Item> items) =>
                _overlay.ShowLabels(items.Select(i => (i.Source.Bounds, i.Translated, i.Source.LineCount)), area, _settings.Overlay.FontSize);

            // Text nearest the mouse (the hovered item) is translated first and shown as soon as it is ready.
            var result = await _screenTranslator.TranslateAsync(area, cts.Token, area.Contains(mouse) ? mouse : null,
                partial => Dispatcher.BeginInvoke(() =>
                {
                    if (!cts.IsCancellationRequested) Draw(partial);
                }));
            if (cts.IsCancellationRequested) return;

            Draw(result);
            if (result.Count == 0) _notifier.Feedback(NoticeCategory.Warning, Loc.Instance["Notice.NoTextFound"], area);
            else _notifier.Feedback(NoticeCategory.Status, Loc.F("Notice.TranslatedBlocks", result.Count), area);
            _notifier.EndProgress(); // with that category off, the progress notice must not stay for its 30 s
            StartClearTimer();
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer request or cleared
        }
        catch (Exception ex)
        {
            Log.Error("translate screen: " + ex);
            _notifier.Feedback(NoticeCategory.Error, Loc.F("Notice.TranslateScreenFailed", ex.Message));
            _notifier.EndProgress();
        }
    }

    void StartClearTimer()
    {
        if (_clearTimer is null) return;
        _clearTimer.Stop();
        if (_settings.Overlay.LabelSeconds <= 0) return;
        _clearTimer.Interval = TimeSpan.FromSeconds(_settings.Overlay.LabelSeconds);
        _clearTimer.Start();
    }

    void ToggleSubtitles()
    {
        if (_subtitles is null || _overlay is null) return;
        if (_subtitles.IsRunning)
        {
            StopSubtitles();
            _notifier.Feedback(NoticeCategory.Status, Loc.Instance["Notice.SubtitlesOff"], _subtitles.Region, StatusKind.Off);
            return;
        }
        StartSubtitles(restart: false);
    }

    /// <param name="restart">Subtitles were on before a dialog: the game found then is kept (see <see cref="SubtitleWatcher.Start"/>).</param>
    void StartSubtitles(bool restart)
    {
        if (_subtitles is null || _overlay is null) return;
        if (_ocr is null)
        {
            _notifier.Feedback(NoticeCategory.Error, Loc.Instance["Notice.OcrUnavailable"]);
            return;
        }
        if (_settings.Subtitles.Region is null && SelectRegion() is null) return;

        var region = _settings.Subtitles.Region!.ToRectangle();
        _presenter!.Languages = Langs();
        _presenter.Start();
        _subtitles.Start(region, keepGame: restart);
        ShowLiveBadge(paused: false);
        SetLive(true);
        if (!WarnIfLargeRegion(region))
            _notifier.Feedback(NoticeCategory.Status, Loc.F("Notice.SubtitlesOn", Hotkey(HotkeyAction.ToggleSubtitles), Hotkey(HotkeyAction.History)),
                region, StatusKind.On);
    }

    /// <summary>The marker at the region; while the game is not in front it says why nothing is translated.</summary>
    void ShowLiveBadge(bool paused)
    {
        if (_subtitles?.IsRunning != true) return; // a late message from a session that was stopped meanwhile
        _notifier.ShowLiveBadge(_subtitles.Region, Loc.Instance[paused ? "Badge.Paused" : "Badge.On"]);
    }

    void StopSubtitles()
    {
        if (_subtitles?.IsRunning != true) return;
        _subtitles.Stop();
        _presenter?.Stop();
        _notifier.HideLiveBadge();
        SetLive(false);
    }

    void SetLive(bool on)
    {
        if (_tray is null) return;
        _tray.Icon = on ? _trayIcons!.Live : _trayIcons!.Idle;
        _tray.Text = Loc.Instance[on ? "Tray.TooltipLive" : "Tray.Tooltip"];
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A whole-screen "subtitle" re-translates every HUD change, is slow, and burns the free daily quota in minutes.</summary>
    /// <returns>Whether the warning was shown; when it was not (warnings off, muted), the caller shows its own notice.</returns>
    bool WarnIfLargeRegion(DRect region)
    {
        var screen = Forms.Screen.FromRectangle(region).Bounds;
        if ((double)region.Width * region.Height <= 0.35 * screen.Width * screen.Height) return false;
        return _notifier.Feedback(NoticeCategory.Warning, Loc.F("Notice.LargeRegion", Hotkey(HotkeyAction.SelectRegion)), region);
    }

    void ToggleHistory()
    {
        if (_presenter is null) return;
        _presenter.ToggleHistory();
    }

    DRect? SelectRegion()
    {
        bool wasRunning = _subtitles?.IsRunning == true;
        StopSubtitles();
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var dialog = new RegionSelectWindow(screen);
        // Started from the main window, which would cover the game where the subtitles are: it steps aside while the
        // user drags and comes back after. Started in the game (the hotkey) it is left as it is: bringing it back
        // would take the focus from the game, and the subtitles would start paused.
        var main = _mainWindow is { IsActive: true } active ? active : null;
        var mainState = main?.WindowState;
        if (main is not null) main.WindowState = WindowState.Minimized;
        bool picked = dialog.ShowDialog() == true;
        if (main is not null && mainState is { } state)
        {
            main.WindowState = state;
            main.Activate();
        }
        if (!picked || dialog.Result is not { } rect)
        {
            if (wasRunning) StartSubtitles(restart: true);
            return null;
        }
        _settings.Subtitles.Region = RegionSettings.From(rect);
        TrySaveSettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (!WarnIfLargeRegion(rect)) _notifier.Feedback(NoticeCategory.Status, Loc.Instance["Notice.RegionSaved"], rect);
        if (wasRunning) StartSubtitles(restart: true);
        return rect;
    }

    void ClearOverlay()
    {
        _screenCts?.Cancel();
        _clearTimer?.Stop();
        _overlay?.ClearLabels();
        bool wasRunning = _subtitles?.IsRunning == true;
        StopSubtitles();
        _presenter?.HideHistory();
        _notifier.Feedback(NoticeCategory.Status, Loc.Instance[wasRunning ? "Notice.ClearedSubtitlesOff" : "Notice.Cleared"], GameArea(), StatusKind.Off);
    }

    /// <param name="welcome">Opens the first-run wizard over it.</param>
    void ShowMainWindow(bool welcome = false)
    {
        if (_mainWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var window = _mainWindow = OpenMainWindow(welcome);
        window.Closed += (_, _) =>
        {
            _mainWindow = null;
            // The window gave them back already; whatever happened, the hotkeys must work in the game.
            if (_hotkeys?.Suspended == true) _hotkeys.Resume();
            _hotkeyTrial = false;
            if (_settings.General.FirstRunDone) return;
            _settings.General.FirstRunDone = true; // the first run showed the window; the next run starts in the tray
            TrySaveSettings();
        };
    }

    /// <summary>Kept out of <see cref="ShowMainWindow"/>: WPF-UI and the view models load only when this runs.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    Window OpenMainWindow(bool welcome)
    {
        var vm = new SettingsViewModel(this);
        if (welcome) vm.Welcome.Open();
        return UI.MainWindow.Open(vm, _settings.General.Theme);
    }

    /// <summary>Exit from the tray: unsaved edits in the main window are dropped without asking (WPF closes it on shutdown).</summary>
    void ExitApp()
    {
        if (_mainWindow is not null) SkipUnsavedQuestion(_mainWindow);
        Shutdown();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SkipUnsavedQuestion(Window window) => ((UI.MainWindow)window).SkipUnsavedQuestion = true;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ApplyTheme(Window window, AppTheme theme) => ((UI.MainWindow)window).ApplyTheme(theme);

    /// <summary>
    /// An error that arose by itself (a subtitle line, an unexpected failure), on the in-game overlay: Windows suppresses
    /// tray notifications while a game is in front. A failure of what the user just did is Feedback instead.
    /// </summary>
    void ShowError(string message) => _notifier.Notify(NoticeCategory.Error, message);

    // ---- the main window's view of the app (ISettingsHost) ----

    public event EventHandler? SettingsChanged;
    public event EventHandler? StateChanged;

    AppSettings ISettingsHost.Current => _settings;

    SaveResult ISettingsHost.Save(AppSettings settings, SettingsChange changes)
    {
        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("settings: " + ex.Message);
            return new SaveResult(false, [Loc.F("Notice.SettingsNotSaved", ex.Message)]);
        }
        _settings = settings;
        return new SaveResult(true, ApplyChanges(changes));
    }

    /// <summary>Redoes what the saved settings changed, and only that (see <see cref="SaveActions"/>).</summary>
    /// <returns>Problems for the user, already logged.</returns>
    List<string> ApplyChanges(SettingsChange changes)
    {
        var problems = new List<string>();
        var redo = SaveActions.For(changes, _subtitles?.IsRunning == true);
        if (redo.Language) Loc.Instance.SetLanguage(_settings.Ui.Language);
        if (redo.Log)
        {
            Log.Level = _settings.Storage.LogLevel;
            Log.MaxBytes = Math.Max(64, _settings.Storage.LogMaxKB) * 1024L;
        }
        if (redo.RestartSubtitles) StopSubtitles();
        if (redo.RebuildOcr) problems.AddRange(BuildOcr());
        if (redo.RebuildProviders) problems.AddRange(ApplyProviders());
        if (redo.RegisterHotkeys && RegisterHotkeys() is { } hotkeyProblem) problems.Add(hotkeyProblem);
        if (redo.Restyle) _presenter?.Restyle();
        if (changes.HasFlag(SettingsChange.Notifications) && !_settings.Notifications.LiveBadge) _notifier.HideLiveBadge();
        if (redo.Theme && _mainWindow is not null) ApplyTheme(_mainWindow, _settings.General.Theme);
        if (redo.RestartSubtitles) StartSubtitles(restart: true);
        Log.Write($"settings: saved ({changes})");
        StateChanged?.Invoke(this, EventArgs.Empty);
        return problems;
    }

    IReadOnlyList<ITranslationProvider> ISettingsHost.Providers =>
        _providers.Providers.Select(p => p.Provider).OfType<ITranslationProvider>().ToList();

    GameProfileStore ISettingsHost.Profiles => _profiles;

    async Task<TestTranslation> ISettingsHost.TestTranslationAsync(AppSettings settings, string line, CancellationToken ct)
    {
        // Built apart from the chain in use and without the cache: the answer comes from the service itself, and a
        // failure of the tested key raises no alert about the provider in use.
        var chain = _providers.BuildDetached(settings.Translation);
        string src = LangCodes.FromOcrTag(_ocr?.LanguageTag);
        string tgt = string.IsNullOrWhiteSpace(settings.General.TargetLanguage) ? "en" : settings.General.TargetLanguage;
        var profile = _profiles.Resolve(settings.Translation.ProfileId, "witcher3", src, tgt); // the sample is a Witcher line
        var result = await Task.Run(() => chain.Translator.TranslateAsync(new TranslateRequest([line], src, tgt, [], profile), ct), ct);
        return new TestTranslation(result.Texts[0], result.ProviderAt(0) ?? chain.Translator.Name, chain.Notice);
    }

    bool ISettingsHost.SubtitlesOn => _subtitles?.IsRunning == true;

    void ISettingsHost.SetSubtitles(bool on)
    {
        if (on != (_subtitles?.IsRunning == true)) ToggleSubtitles();
    }

    ProviderStatus ISettingsHost.ProviderStatus =>
        _providerProblem is { } problem ? new ProviderStatus(problem, false) : new ProviderStatus(Loc.Instance["Home.StatusReady"], true);

    FreeServiceStatus ISettingsHost.FreeServices =>
        new(_providers.Google.BlockedUntilUtc?.ToLocalTime(), _myMemoryUsage.Today, MyMemoryTranslator.DailyLimitFor(_settings.Translation.Free.MyMemoryEmail));

    HotkeyResult? ISettingsHost.HotkeyState(HotkeyAction action) => _hotkeys?.State(action);

    void ISettingsHost.SelectRegion() => SelectRegion();

    /// <summary>
    /// The Home page's "Translate screen": the main window steps aside first, so what is read is the window behind it
    /// (the game), as with the hotkey, and not the screen behind the settings with the labels drawn over them.
    /// </summary>
    async void ISettingsHost.TranslateScreen()
    {
        if (_mainWindow is { IsActive: true } main)
        {
            main.WindowState = WindowState.Minimized; // Windows activates the window behind it
            await Task.Delay(MainWindowAsideTime);
        }
        TranslateScreen();
    }

    void ISettingsHost.ToggleHistory() => ToggleHistory();

    /// <summary>A sample subtitle in this look at the subtitle region (or where one would be), for a few seconds.</summary>
    void ISettingsHost.PreviewOverlay(OverlaySettings look)
    {
        if (_overlay is null) return;
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        var region = _settings.Subtitles.Region?.ToRectangle()
                     ?? new DRect(screen.Left + screen.Width / 5, screen.Bottom - screen.Height / 4, screen.Width * 3 / 5, screen.Height / 12);
        var lines = PreviewLines().TakeLast(Math.Clamp(look.MaxLines, 1, 5)).ToList();
        _overlay.ShowSubtitleLines(lines, region, look.FontSize, SubtitleStyle.From(look));
        if (_previewTimer is null)
        {
            _previewTimer = new DispatcherTimer { Interval = PreviewTime };
            _previewTimer.Tick += (_, _) =>
            {
                _previewTimer.Stop();
                // The sample goes; running subtitles get their own lines back.
                _overlay?.HideSubtitle();
                if (_subtitles?.IsRunning == true) _presenter?.Restyle();
            };
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    /// <summary>Two earlier lines and the newest, each with the game's English under it when the look shows the original.</summary>
    static IReadOnlyList<SubtitleLineView> PreviewLines() =>
    [
        new(Loc.Instance["Overlay.Sample1"], false, Original: "Wind's howling."),
        new(Loc.Instance["Overlay.Sample2"], false, Original: "We should find shelter before nightfall."),
        new(Loc.Instance["Overlay.Sample3"], false, Original: "Roach is tired, and so am I."),
    ];

    // ---- hotkeys (the Hotkeys page and the wizard) ----

    public event EventHandler<HotkeyAction>? HotkeyPressed;

    void ISettingsHost.SuspendHotkeys() => _hotkeys?.Suspend();

    void ISettingsHost.ResumeHotkeys()
    {
        if (_hotkeys?.Suspended == true) _hotkeys.Resume();
    }

    HotkeyResult ISettingsHost.ProbeHotkey(string gesture, HotkeyAction action) =>
        _hotkeys?.Probe(gesture, action) ?? HotkeyManager.Validate(gesture).Result;

    bool ISettingsHost.HotkeyTrial
    {
        get => _hotkeyTrial;
        set => _hotkeyTrial = value;
    }

    Task ISettingsHost.SendTestNotices(NotificationSettings look) => _notifier.SendTestAsync(look);

    // ---- the data folder ----

    DataPaths ISettingsHost.DataPaths => DataPaths.Current;
    DataFolderMover ISettingsHost.DataFolders => _dataFolders;
    CachedTranslator? ISettingsHost.Cache => _providers.Cache;

    void ISettingsHost.FlushData()
    {
        TrySaveSettings();
        _providers.Flush();
    }

    /// <summary>Points every file of the app at the copied folder; the log follows <see cref="DataPaths.Current"/> by itself.</summary>
    void ISettingsHost.UseDataFolder(DataPaths paths)
    {
        _store.Relocate(paths.SettingsFile);
        _providers.Cache?.Relocate(paths.CacheFile);
        _myMemoryUsage.Relocate(paths.MyMemoryUsageFile);
        _profiles.Relocate(paths.ProfilesDir);
        DataPaths.Use(paths);
        Log.Write($"data folder: now {paths.Root}{(paths.IsPortable ? " (portable)" : "")}");
        TrySaveSettings();
        _providers.Flush();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The new copy needs the hotkeys and the single-instance mutex, so this one lets go of them before starting it.</summary>
    void ISettingsHost.Restart()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _singleInstance = null;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error("restart: " + ex.Message);
            _singleInstance = new Mutex(true, InstanceMutexName, out _);
            RegisterHotkeys();
            _notifier.Feedback(NoticeCategory.Error, Loc.F("Notice.RestartFailed", ex.Message));
            return;
        }
        ExitApp();
    }

    void ISettingsHost.OpenFolder(string path) => OpenFolder(path);

    // ---- General & About ----

    StartupRegistration ISettingsHost.Startup => _startup;

    void ISettingsHost.SetStartWithWindows(bool on)
    {
        _startup.SetEnabled(on);
        Log.Write("startup: " + (on ? "starts with Windows" : "no longer starts with Windows"));
        _settings.General.StartWithWindows = on;
        TrySaveSettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    IReadOnlyList<string> ISettingsHost.OcrLanguages
    {
        get
        {
            try
            {
                return WindowsOcrEngine.AvailableLanguages().Select(l => l.Tag).ToList();
            }
            catch (Exception ex)
            {
                Log.Warn("ocr languages: " + ex.Message);
                return [];
            }
        }
    }

    void ISettingsHost.ApplyLanguage(string setting) => Loc.Instance.SetLanguage(setting);

    // ---- history ----

    IReadOnlyList<SubtitleRecord> ISettingsHost.HistoryRecords(int lines) => _presenter?.Records(lines) ?? [];
    (string Src, string Tgt) ISettingsHost.Languages => Langs();
    void ISettingsHost.ShowFix(string source, string translation) => _presenter?.ApplyFix(source, translation);

    void OnExit(object sender, ExitEventArgs e)
    {
        _subtitles?.Dispose();
        _providers.Flush();
        _hotkeys?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _trayIcons?.Dispose();
    }

    // ---- command-line modes used for diagnostics and automated checks ----

    async Task RunCliAsync(string[] args)
    {
        int code = 1;
        try
        {
            string outDir = args.Length > 1 ? args[1] : DataPaths.Current.Root;
            switch (args[0])
            {
                case "--selftest":
                    code = await OverlaySelfTest.RunAsync(outDir);
                    break;
                case "--translate-screen":
                    code = await TranslateScreenCliAsync(outDir);
                    break;
                case "--bench-subtitles":
                    code = await SubtitleBench.RunAsync(outDir);
                    break;
                default:
                    Console.Error.WriteLine("Unknown option " + args[0] + ". Use --selftest [dir], --translate-screen [dir] or --bench-subtitles [dir].");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Log.Error("cli: " + ex);
        }
        Shutdown(code);
    }

    /// <summary>Runs the capture → OCR → translate pipeline on the primary monitor and writes the results plus an annotated screenshot.</summary>
    async Task<int> TranslateScreenCliAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var problem in BuildServices()) Console.Error.WriteLine(problem);
        if (_ocr is null || _translator is null) return 2;

        var translator = new ScreenTranslator(() => _ocr, () => _translator, Langs, ForegroundProfile);
        var area = Forms.Screen.PrimaryScreen!.Bounds;
        var watch = Stopwatch.StartNew();
        var items = await translator.TranslateAsync(area, CancellationToken.None);
        watch.Stop();

        var lines = new List<string>
        {
            $"ocr={_ocr.LanguageTag} provider={_translator.Name} langs={Langs()} items={items.Count} elapsed={watch.ElapsedMilliseconds}ms",
        };
        lines.AddRange(items.Select(i => $"[{i.Source.Bounds.X},{i.Source.Bounds.Y} {i.Source.Bounds.Width}x{i.Source.Bounds.Height} lines={i.Source.LineCount}] {i.Source.Text}  =>  {i.Translated}"));
        File.WriteAllLines(Path.Combine(outDir, "translate_screen.txt"), lines);
        foreach (var line in lines) Console.WriteLine(line);

        using var shot = ScreenCapture.Capture(area);
        using (var g = Drawing.Graphics.FromImage(shot))
        using (var font = new Drawing.Font("Segoe UI", 12))
        using (var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xD8, 0x10, 0x14, 0x18)))
        {
            foreach (var item in items)
            {
                var r = item.Source.Bounds;
                r.Offset(-area.Left, -area.Top);
                var size = g.MeasureString(item.Translated, font, Math.Max(r.Width + 24, 240));
                var box = new Drawing.RectangleF(r.Left - 6, r.Top - 2, size.Width + 12, size.Height + 4);
                g.FillRectangle(fill, box);
                g.DrawString(item.Translated, font, Drawing.Brushes.White, new Drawing.RectangleF(r.Left, r.Top, size.Width, size.Height));
            }
        }
        shot.Save(Path.Combine(outDir, "translate_screen.png"), Drawing.Imaging.ImageFormat.Png);
        _providers.Flush();
        return 0;
    }
}
