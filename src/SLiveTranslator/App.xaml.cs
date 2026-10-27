using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using SLiveTranslator.UI;
using SLiveTranslator.UI.Overlay;
using Drawing = System.Drawing;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator;

public partial class App : Application
{
    readonly SettingsStore _store = new(DataPaths.Current.SettingsFile);
    readonly DailyCharCounter _myMemoryUsage = new(DataPaths.Current.MyMemoryUsageFile);
    readonly ProviderRegistry _providers;
    readonly GameProfileStore _profiles = new(DataPaths.Current.ProfilesDir);
    readonly Notifier _notifier;
    readonly AlertRules _alerts;
    readonly List<(Forms.ToolStripItem Item, string Key)> _trayTexts = new();
    AppSettings _settings = new();
    OverlayHost? _overlay;
    SubtitlePresenter? _presenter;
    Forms.NotifyIcon? _tray;
    Drawing.Icon? _iconIdle, _iconLive;
    Forms.ToolStripMenuItem? _subtitleMenu, _historyMenu;
    HotkeyManager? _hotkeys;
    IOcrEngine? _ocr;
    CachedTranslator? _translator;
    SubtitleWatcher? _subtitles;
    ScreenTranslator? _screenTranslator;
    CancellationTokenSource? _screenCts;
    DispatcherTimer? _clearTimer;
    Mutex? _singleInstance;

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

        if (e.Args.Length > 0 && e.Args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            foreach (var problem in problems) Console.Error.WriteLine(problem);
            _ = RunCliAsync(e.Args);
            return;
        }

        // A second copy cannot get the hotkeys and only causes confusion.
        _singleInstance = new Mutex(true, @"Local\SLiveTranslator.Instance", out bool firstInstance);
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

        _presenter = new SubtitlePresenter(Dispatcher, _overlay, () => _subtitles?.Region ?? default, () => _settings.Overlay.FontSize,
            () => _settings.Overlay.MaxLines, () => Hotkey(HotkeyAction.History), ShowError,
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
            if (args.PropertyName == Binding.IndexerName) LocalizeTray();
        };
        if (RegisterHotkeys() is { } hotkeyProblem) problems.Add(hotkeyProblem);

        // Shown before the first-run settings window, so a reset to defaults is explained before the user saves over it.
        ShowProblems(problems);
        if (!_settings.General.FirstRunDone)
        {
            ShowSettings();
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
        var problems = new List<string>();
        try
        {
            _ocr = WindowsOcrEngine.Create(_settings.General.OcrLanguage);
        }
        catch (Exception ex)
        {
            _ocr = null;
            Log.Error("ocr init: " + ex);
            problems.Add(ex.Message);
        }

        // The selected provider, then the free services (alone while its key is missing), behind the one cache of the run.
        var notice = _providers.Apply(_settings).Notice;
        if (notice is not null) problems.Add(notice); // the registry logged it
        _translator = _providers.Cache;
        return problems;
    }

    /// <summary>The selected provider gave way to the free services, or came back (see <see cref="AlertRules.ServiceSwitched"/>).</summary>
    void OnServiceSwitched(object? sender, ServiceSwitch e) => Dispatcher.BeginInvoke(() =>
    {
        if (_translator is null) return;
        string id = _settings.Translation.Provider;
        string name = _providers.Providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? e.From;
        _alerts.ServiceSwitched(e, _translator.Name, name, _subtitles?.Region);
    });

    /// <summary>MyMemory counted characters on a translation thread; its quota is checked on the UI thread, where the settings live.</summary>
    void OnMyMemoryCounted(long today) => Dispatcher.BeginInvoke(() =>
    {
        string email = _settings.Translation.Free.MyMemoryEmail;
        _alerts.MyMemoryUsage(today, MyMemoryTranslator.DailyLimitFor(email), hasEmail: !string.IsNullOrWhiteSpace(email));
    });

    bool IsOwnWindow(IntPtr hwnd) =>
        Windows.OfType<Window>().Any(w => new WindowInteropHelper(w).Handle == hwnd);

    DRect GameArea() => ScreenCapture.ForegroundWindowRect(IsOwnWindow);

    void CreateTray()
    {
        _iconIdle = TrayIcon.Create(TrayIcon.Idle);
        _iconLive = TrayIcon.Create(TrayIcon.Live);

        var menu = new Forms.ContextMenuStrip();
        Forms.ToolStripMenuItem Item(string key, Action onClick)
        {
            var item = new Forms.ToolStripMenuItem(Loc.Instance[key], null, (_, _) => onClick());
            _trayTexts.Add((item, key));
            menu.Items.Add(item);
            return item;
        }
        Item("Tray.TranslateScreen", TranslateScreen);
        _subtitleMenu = Item("Tray.Subtitles", ToggleSubtitles);
        _historyMenu = Item("Tray.History", ToggleHistory);
        Item("Tray.SelectRegion", () => SelectRegion());
        Item("Tray.Clear", ClearOverlay);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Item("Tray.Settings", ShowSettings);
        Item("Tray.OpenDataFolder", () =>
        {
            Directory.CreateDirectory(DataPaths.Current.Root);
            Process.Start("explorer.exe", DataPaths.Current.Root);
        });
        Item("Tray.Exit", Shutdown);

        _tray = new Forms.NotifyIcon
        {
            Icon = _iconIdle,
            Text = Loc.Instance["Tray.Tooltip"],
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    /// <summary>The tray in the language just chosen.</summary>
    void LocalizeTray()
    {
        foreach (var (item, key) in _trayTexts) item.Text = Loc.Instance[key];
        SetLive(_subtitles?.IsRunning == true);
    }

    /// <returns>A problem to show the user, or null when every enabled hotkey is registered.</returns>
    string? RegisterHotkeys()
    {
        _hotkeys ??= new HotkeyManager();
        var results = _hotkeys.RegisterAll(_settings.Hotkeys, new Dictionary<HotkeyAction, Action>
        {
            [HotkeyAction.TranslateScreen] = TranslateScreen,
            [HotkeyAction.ToggleSubtitles] = ToggleSubtitles,
            [HotkeyAction.SelectRegion] = () => SelectRegion(),
            [HotkeyAction.Clear] = ClearOverlay,
            [HotkeyAction.History] = ToggleHistory,
        });
        if (HotkeyText.Problems(results, _settings.Hotkeys) is not { } problem) return null;
        Log.Warn("hotkeys: " + string.Join(", ", results.Select(r => $"{r.Key}={_settings.Hotkeys[r.Key]}:{r.Value}")));
        return problem;
    }

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
        _presenter!.Start();
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
        if (_subtitleMenu is not null) _subtitleMenu.Checked = on;
        if (_tray is null) return;
        _tray.Icon = on ? _iconLive : _iconIdle;
        _tray.Text = Loc.Instance[on ? "Tray.TooltipLive" : "Tray.Tooltip"];
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
        bool visible = _presenter.ToggleHistory();
        if (_historyMenu is not null) _historyMenu.Checked = visible;
    }

    DRect? SelectRegion()
    {
        bool wasRunning = _subtitles?.IsRunning == true;
        StopSubtitles();
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var dialog = new RegionSelectWindow(screen);
        if (dialog.ShowDialog() != true || dialog.Result is not { } rect)
        {
            if (wasRunning) StartSubtitles(restart: true);
            return null;
        }
        _settings.Subtitles.Region = RegionSettings.From(rect);
        TrySaveSettings();
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
        if (_historyMenu is not null) _historyMenu.Checked = false;
        _notifier.Feedback(NoticeCategory.Status, Loc.Instance[wasRunning ? "Notice.ClearedSubtitlesOff" : "Notice.Cleared"], GameArea(), StatusKind.Off);
    }

    void ShowSettings()
    {
        if (Windows.OfType<SettingsWindow>().FirstOrDefault() is { } open)
        {
            open.Activate();
            return;
        }
        var dialog = new SettingsWindow(_settings, SelectRegion, () => _store.Save(_settings));
        if (dialog.ShowDialog() != true) return;

        Loc.Instance.SetLanguage(_settings.Ui.Language);
        bool restart = _subtitles?.IsRunning == true;
        StopSubtitles();
        var problems = BuildServices();
        if (RegisterHotkeys() is { } hotkeyProblem) problems.Add(hotkeyProblem);
        if (restart) StartSubtitles(restart: true);
        if (problems.Count > 0) ShowProblems(problems);
        else if (!restart) _notifier.Feedback(NoticeCategory.Status, Loc.Instance["Notice.SettingsSaved"], Forms.Screen.PrimaryScreen?.Bounds);
    }

    /// <summary>
    /// An error that arose by itself (a subtitle line, an unexpected failure), on the in-game overlay: Windows suppresses
    /// tray notifications while a game is in front. A failure of what the user just did is Feedback instead.
    /// </summary>
    void ShowError(string message) => _notifier.Notify(NoticeCategory.Error, message);

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
