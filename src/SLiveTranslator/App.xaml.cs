using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    AppSettings _settings = new();
    OverlayHost? _overlay;
    SubtitlePresenter? _presenter;
    Forms.NotifyIcon? _tray;
    Drawing.Icon? _iconIdle, _iconLive;
    Forms.ToolStripMenuItem? _subtitleMenu, _historyMenu;
    HotkeyManager? _hotkeys;
    IOcrEngine? _ocr;
    CachedTranslator? _translator;
    readonly ProviderRegistry _providers = new(DataPaths.Current.CacheFile, new DailyCharCounter(DataPaths.Current.MyMemoryUsageFile));
    readonly GameProfileStore _profiles = new(DataPaths.Current.ProfilesDir);
    SubtitleWatcher? _subtitles;
    ScreenTranslator? _screenTranslator;
    CancellationTokenSource? _screenCts;
    DispatcherTimer? _clearTimer;
    Mutex? _singleInstance;
    string _lastError = "";
    DateTime _lastErrorAt;

    void OnStartup(object sender, StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("unhandled: " + args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("dispatcher: " + args.Exception);
            args.Handled = true;
            ShowError(args.Exception.Message);
        };
        _settings = _store.Load();
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
            MessageBox.Show("SLiveTranslator is already running.\n\nUse the \"T\" icon in the system tray, or exit it there before starting it again.",
                "SLiveTranslator", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Stores a version-1 file as schema 2 right away, so a plain-text API key gets encrypted (the original stays as .bak-v1).
        if (_store.MigratedFromV1) TrySaveSettings();

        problems.AddRange(BuildServices());
        problems.AddRange(_profiles.Problems);
        _overlay = new OverlayHost();
        _providers.ServiceSwitched += OnServiceSwitched;

        _presenter = new SubtitlePresenter(Dispatcher, _overlay, () => _subtitles?.Region ?? default, () => _settings.Overlay.FontSize,
            () => _settings.Overlay.MaxLines, () => Hotkey(HotkeyAction.History), ShowError,
            notice => _overlay.ShowStatus($"{notice} Press {Hotkey(HotkeyAction.SelectRegion)}.", StatusKind.Warning, _subtitles?.Region, 8),
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
        if (RegisterHotkeys() is { } hotkeyProblem) problems.Add(hotkeyProblem);

        // One toast holds every startup problem (a later toast would replace an earlier one), shown before the
        // first-run settings window so a reset to defaults is explained before the user saves over it.
        if (problems.Count > 0)
            _overlay.ShowStatus(string.Join("\n", problems), StatusKind.Warning, Forms.Screen.PrimaryScreen?.Bounds, 12);
        if (!_settings.General.FirstRunDone)
        {
            ShowSettings();
            return;
        }
        string keys = $"{Hotkey(HotkeyAction.ToggleSubtitles)}: subtitles   {Hotkey(HotkeyAction.TranslateScreen)}: translate screen   {Hotkey(HotkeyAction.History)}: history";
        Balloon("SLiveTranslator is running", keys);
        if (problems.Count == 0)
            _overlay.ShowStatus("SLiveTranslator ready  ·  " + keys, StatusKind.Info, Forms.Screen.PrimaryScreen?.Bounds, 5);
    }

    /// <summary>Problems with the data folder or settings.json found while loading; logged here, shown once the overlay is up.</summary>
    List<string> SettingsProblems()
    {
        var problems = new List<string>();
        if (DataPaths.Current.UnavailableLocation is { } missing)
            problems.Add($"The data folder {missing} is not available, so the default folder {DataPaths.Current.Root} is in use. Connect the drive and restart SLiveTranslator.");
        if (_settings.IsNewerSchema)
            problems.Add($"settings.json comes from a newer SLiveTranslator (schema {_settings.SchemaVersion}): the settings this version does not know are kept but not used.");
        foreach (var problem in problems) Log.Warn("startup: " + problem);
        if (_store.LoadProblem is { } loadProblem) problems.Add(loadProblem); // the store logged it
        return problems;
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
            ShowError("Settings could not be saved: " + ex.Message);
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

    /// <summary>
    /// The provider the user selected stopped answering and the free services translate instead: said once per switch,
    /// so a paid provider out of credit is not replaced unnoticed. The free services' own switch (Google blocked, so
    /// MyMemory) and the way back are only logged.
    /// </summary>
    void OnServiceSwitched(object? sender, ServiceSwitch e)
    {
        if (e.Reason.Length == 0 || e.From != _translator?.Name) return;
        Dispatcher.BeginInvoke(() => _overlay?.ShowStatus($"{e.From} is not answering, so the free services translate for now. {e.Reason}",
            StatusKind.Warning, _subtitles?.Region, 8));
    }

    bool IsOwnWindow(IntPtr hwnd) =>
        Windows.OfType<Window>().Any(w => new WindowInteropHelper(w).Handle == hwnd);

    DRect GameArea() => ScreenCapture.ForegroundWindowRect(IsOwnWindow);

    void CreateTray()
    {
        _iconIdle = TrayIcon.Create(TrayIcon.Idle);
        _iconLive = TrayIcon.Create(TrayIcon.Live);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Translate screen now", null, (_, _) => TranslateScreen());
        _subtitleMenu = new Forms.ToolStripMenuItem("Subtitle mode", null, (_, _) => ToggleSubtitles());
        menu.Items.Add(_subtitleMenu);
        _historyMenu = new Forms.ToolStripMenuItem("Dialog history", null, (_, _) => ToggleHistory());
        menu.Items.Add(_historyMenu);
        menu.Items.Add("Select subtitle region…", null, (_, _) => SelectRegion());
        menu.Items.Add("Clear overlay", null, (_, _) => ClearOverlay());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Open data folder", null, (_, _) =>
        {
            Directory.CreateDirectory(DataPaths.Current.Root);
            Process.Start("explorer.exe", DataPaths.Current.Root);
        });
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = _iconIdle,
            Text = "SLiveTranslator",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    /// <returns>A problem to show the user, or null when every enabled hotkey is registered.</returns>
    string? RegisterHotkeys()
    {
        _hotkeys ??= new HotkeyManager();
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        void Register(HotkeyAction hotkey, Action action)
        {
            if (_settings.Hotkeys.Disabled.Contains(hotkey)) return;
            if (!_hotkeys.Register(Hotkey(hotkey), action)) failed.Add(Hotkey(hotkey));
        }
        Register(HotkeyAction.TranslateScreen, TranslateScreen);
        Register(HotkeyAction.ToggleSubtitles, ToggleSubtitles);
        Register(HotkeyAction.SelectRegion, () => SelectRegion());
        Register(HotkeyAction.Clear, ClearOverlay);
        Register(HotkeyAction.History, ToggleHistory);
        if (failed.Count == 0) return null;
        string problem = "Could not register hotkeys: " + string.Join(", ", failed) + ". Another program uses them; change them in Settings.";
        Log.Error(problem);
        return problem;
    }

    async void TranslateScreen()
    {
        if (_ocr is null || _screenTranslator is null || _overlay is null)
        {
            ShowError("OCR is not available.");
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
            _overlay.ShowStatus("Translating screen…", StatusKind.Info, area, 30);
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
            if (result.Count == 0) _overlay.ShowStatus("No text found in the game window", StatusKind.Warning, area);
            else _overlay.ShowStatus($"Translated {result.Count} text blocks", StatusKind.Info, area, 1.5);
            StartClearTimer();
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer request or cleared
        }
        catch (Exception ex)
        {
            Log.Error("translate screen: " + ex);
            ShowError(ex.Message);
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
            _overlay.ShowStatus("Subtitles OFF", StatusKind.Off, _subtitles.Region);
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
            ShowError("OCR is not available.");
            return;
        }
        if (_settings.Subtitles.Region is null && SelectRegion() is null) return;

        var region = _settings.Subtitles.Region!.ToRectangle();
        _presenter!.Start();
        _subtitles.Start(region, keepGame: restart);
        ShowLiveBadge(paused: false);
        SetLive(true);
        if (!WarnIfLargeRegion(region))
            _overlay.ShowStatus($"Subtitles ON  ·  {Hotkey(HotkeyAction.ToggleSubtitles)} to stop  ·  {Hotkey(HotkeyAction.History)} for history",
                StatusKind.On, region, 3);
    }

    /// <summary>The marker at the region; while the game is not in front it says why nothing is translated.</summary>
    void ShowLiveBadge(bool paused)
    {
        if (_subtitles?.IsRunning != true) return; // a late message from a session that was stopped meanwhile
        _overlay?.ShowLiveBadge(_subtitles.Region, paused ? "Subtitles paused · game not in front" : "Subtitles on");
    }

    void StopSubtitles()
    {
        if (_subtitles?.IsRunning != true) return;
        _subtitles.Stop();
        _presenter?.Stop();
        _overlay?.HideLiveBadge();
        SetLive(false);
    }

    void SetLive(bool on)
    {
        if (_subtitleMenu is not null) _subtitleMenu.Checked = on;
        if (_tray is null) return;
        _tray.Icon = on ? _iconLive : _iconIdle;
        _tray.Text = on ? "SLiveTranslator – subtitles ON" : "SLiveTranslator";
    }

    /// <summary>A whole-screen "subtitle" re-translates every HUD change, is slow, and burns the free daily quota in minutes.</summary>
    bool WarnIfLargeRegion(DRect region)
    {
        var screen = Forms.Screen.FromRectangle(region).Bounds;
        if ((double)region.Width * region.Height <= 0.35 * screen.Width * screen.Height) return false;
        _overlay?.ShowStatus($"Subtitle region is almost the whole screen: slow and misses lines. Press {Hotkey(HotkeyAction.SelectRegion)} and select only the subtitle strip.",
            StatusKind.Warning, region, 8);
        return true;
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
        if (!WarnIfLargeRegion(rect)) _overlay?.ShowStatus("Subtitle region saved", StatusKind.Info, rect);
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
        _overlay?.ShowStatus(wasRunning ? "Overlay cleared  ·  subtitles OFF" : "Overlay cleared", StatusKind.Off, GameArea());
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

        bool restart = _subtitles?.IsRunning == true;
        StopSubtitles();
        var problems = BuildServices();
        if (RegisterHotkeys() is { } hotkeyProblem) problems.Add(hotkeyProblem);
        if (restart) StartSubtitles(restart: true);
        if (problems.Count > 0) _overlay?.ShowStatus(string.Join("\n", problems), StatusKind.Warning, Forms.Screen.PrimaryScreen?.Bounds, 12);
        else if (!restart) _overlay?.ShowStatus("Settings saved", StatusKind.Info, Forms.Screen.PrimaryScreen?.Bounds);
    }

    /// <summary>Errors go to the in-game overlay: Windows suppresses tray notifications while a game is in front.</summary>
    void ShowError(string message)
    {
        // A failure that repeats on every subtitle line (both free services paused) would otherwise fill the log.
        Log.Once("shown: " + message, message, TimeSpan.FromMinutes(1), LogLevel.Error);
        if (message == _lastError && DateTime.UtcNow - _lastErrorAt < TimeSpan.FromSeconds(10)) return;
        _lastError = message;
        _lastErrorAt = DateTime.UtcNow;
        if (_overlay is not null) _overlay.ShowStatus(message, StatusKind.Error, null, 6);
        else Balloon("SLiveTranslator", message, Forms.ToolTipIcon.Warning);
    }

    void Balloon(string title, string text, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        if (_tray is null) return;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(4000);
    }

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
