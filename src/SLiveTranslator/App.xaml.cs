using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SLiveTranslator.Capture;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;
using SLiveTranslator.UI;
using Drawing = System.Drawing;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator;

public partial class App : Application
{
    AppSettings _settings = new();
    OverlayWindow? _overlay;
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
    DispatcherTimer? _saveTimer;
    Mutex? _singleInstance;
    string _lastError = "";
    DateTime _lastErrorAt;

    void OnStartup(object sender, StartupEventArgs e)
    {
        _settings = AppSettings.Load();
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("unhandled: " + args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("dispatcher: " + args.Exception);
            args.Handled = true;
            ShowError(args.Exception.Message);
        };

        if (e.Args.Length > 0 && e.Args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
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

        BuildServices();
        _overlay = new OverlayWindow();
        _overlay.Show();

        _presenter = new SubtitlePresenter(Dispatcher, _overlay, () => _subtitles?.Region ?? default, () => _settings.FontSize,
            () => _settings.HotkeyHistory, ShowError,
            notice => _overlay.ShowStatus($"{notice} Press {_settings.HotkeySelectRegion}.", StatusKind.Warning, _subtitles?.Region, 8));
        _screenTranslator = new ScreenTranslator(() => _ocr!, () => _translator!, Langs);
        _subtitles = new SubtitleWatcher(() => _ocr!, () => _translator!,
            () => (Langs().Src, Langs().Tgt, _settings.SubtitleIntervalMs), _presenter);

        _clearTimer = new DispatcherTimer();
        _clearTimer.Tick += (_, _) =>
        {
            _clearTimer.Stop();
            _overlay.ClearLabels();
        };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _saveTimer.Tick += (_, _) => _translator?.Save();
        _saveTimer.Start();

        CreateTray();
        RegisterHotkeys();

        if (!_settings.FirstRunDone)
        {
            ShowSettings();
        }
        else
        {
            string keys = $"{_settings.HotkeyToggleSubtitles}: subtitles   {_settings.HotkeyTranslateScreen}: translate screen   {_settings.HotkeyHistory}: history";
            Balloon("SLiveTranslator is running", keys);
            _overlay.ShowStatus("SLiveTranslator ready  ·  " + keys, StatusKind.Info, Forms.Screen.PrimaryScreen?.Bounds, 5);
        }
    }

    (string Src, string Tgt) Langs() =>
        (LangCodes.FromOcrTag(_ocr?.LanguageTag), string.IsNullOrWhiteSpace(_settings.TargetLanguage) ? "en" : _settings.TargetLanguage);

    void BuildServices()
    {
        try
        {
            _ocr = WindowsOcrEngine.Create(_settings.OcrLanguage);
        }
        catch (Exception ex)
        {
            _ocr = null;
            Log.Write("ocr init: " + ex);
            ShowError(ex.Message);
        }

        ITranslator inner = _settings.Provider == "claude"
            ? new ClaudeTranslator(_settings.ClaudeApiKey, _settings.ClaudeModel)
            // "free-mt": cache entries made before MyMemory preferred machine translation held junk corpus matches.
            : new FallbackTranslator("free-mt", new GoogleTranslator(), new MyMemoryTranslator(_settings.MyMemoryEmail));
        _translator?.Save();
        _translator = new CachedTranslator(inner, Path.Combine(AppSettings.Dir, "cache.json"));
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
            Directory.CreateDirectory(AppSettings.Dir);
            Process.Start("explorer.exe", AppSettings.Dir);
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

    void RegisterHotkeys()
    {
        _hotkeys ??= new HotkeyManager();
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        void Register(string hotkey, Action action)
        {
            if (!_hotkeys.Register(hotkey, action)) failed.Add(hotkey);
        }
        Register(_settings.HotkeyTranslateScreen, TranslateScreen);
        Register(_settings.HotkeyToggleSubtitles, ToggleSubtitles);
        Register(_settings.HotkeySelectRegion, () => SelectRegion());
        Register(_settings.HotkeyClear, ClearOverlay);
        Register(_settings.HotkeyHistory, ToggleHistory);
        if (failed.Count > 0)
            ShowError("Could not register hotkeys: " + string.Join(", ", failed) + ". Another program uses them; change them in Settings.");
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
                _overlay.ShowLabels(items.Select(i => (i.Source.Bounds, i.Translated, i.Source.LineCount)), area, _settings.FontSize);

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
            Log.Write("translate screen: " + ex);
            ShowError(ex.Message);
        }
    }

    void StartClearTimer()
    {
        if (_clearTimer is null) return;
        _clearTimer.Stop();
        if (_settings.ScreenOverlaySeconds <= 0) return;
        _clearTimer.Interval = TimeSpan.FromSeconds(_settings.ScreenOverlaySeconds);
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
        if (_ocr is null)
        {
            ShowError("OCR is not available.");
            return;
        }
        if (_settings.SubtitleRegion is null && SelectRegion() is null) return;

        var region = _settings.SubtitleRegion!.ToRectangle();
        _presenter!.Start();
        _subtitles.Start(region);
        _overlay.ShowLiveBadge(region, "Subtitles on");
        SetLive(true);
        if (!WarnIfLargeRegion(region))
            _overlay.ShowStatus($"Subtitles ON  ·  {_settings.HotkeyToggleSubtitles} to stop  ·  {_settings.HotkeyHistory} for history",
                StatusKind.On, region, 3);
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
        _overlay?.ShowStatus($"Subtitle region is almost the whole screen: slow and misses lines. Press {_settings.HotkeySelectRegion} and select only the subtitle strip.",
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
            if (wasRunning) ToggleSubtitles();
            return null;
        }
        _settings.SubtitleRegion = RegionSettings.From(rect);
        _settings.Save();
        if (!WarnIfLargeRegion(rect)) _overlay?.ShowStatus("Subtitle region saved", StatusKind.Info, rect);
        if (wasRunning) ToggleSubtitles();
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
        var dialog = new SettingsWindow(_settings, SelectRegion);
        if (dialog.ShowDialog() != true) return;

        bool restart = _subtitles?.IsRunning == true;
        StopSubtitles();
        BuildServices();
        RegisterHotkeys();
        if (restart) ToggleSubtitles();
        else _overlay?.ShowStatus("Settings saved", StatusKind.Info, Forms.Screen.PrimaryScreen?.Bounds);
    }

    /// <summary>Errors go to the in-game overlay: Windows suppresses tray notifications while a game is in front.</summary>
    void ShowError(string message)
    {
        Log.Write("error: " + message);
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
        _translator?.Save();
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
            string outDir = args.Length > 1 ? args[1] : AppSettings.Dir;
            switch (args[0])
            {
                case "--selftest":
                    code = await SelfTestAsync(outDir);
                    break;
                case "--translate-screen":
                    code = await TranslateScreenCliAsync(outDir);
                    break;
                default:
                    Console.Error.WriteLine("Unknown option " + args[0] + ". Use --selftest [dir] or --translate-screen [dir].");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Log.Write("cli: " + ex);
        }
        Shutdown(code);
    }

    /// <summary>Shows every overlay element with sample content, then saves the overlay rendering and a screen capture for inspection.</summary>
    async Task<int> SelfTestAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var overlay = new OverlayWindow();
        overlay.Show();
        var area = Forms.Screen.PrimaryScreen!.Bounds;
        var subtitleRegion = new DRect(area.Left + area.Width / 5, area.Bottom - 230, area.Width * 3 / 5, 90);

        overlay.ShowLabels(new[]
        {
            (new DRect(area.Left + 1500, area.Top + 300, 300, 40), "Bình thuốc hồi máu", 1),
            (new DRect(area.Left + 1500, area.Top + 380, 520, 90), "Kiếm cổ. +12 Tấn công. Hiếm. Bán được 240 vàng.", 2),
        }, area, 22);
        overlay.ShowSubtitleLines(
        [
            new SubtitleLineView("Chào mừng, lữ khách.", false),
            new SubtitleLineView("Con đường phía trước rất nguy hiểm.", false),
            new SubtitleLineView("…", true),
        ], subtitleRegion, 22);
        overlay.ShowLiveBadge(subtitleRegion, "Subtitles on");
        overlay.ShowHistory("Dialog history (3 lines)  ·  Ctrl+Shift+H to close",
        [
            new HistoryLineView("Chào mừng, lữ khách.", "Welcome, traveller.", false),
            new HistoryLineView("Con đường phía trước rất nguy hiểm.", "The road ahead is dangerous.", false),
            new HistoryLineView("", "Take this sword with you.", true),
        ], area);
        overlay.ShowStatus("Subtitles ON  ·  Ctrl+Shift+S to stop  ·  Ctrl+Shift+H for history", StatusKind.On, area, 30);
        await Task.Delay(800);

        using (var shot = ScreenCapture.Capture(area))
            shot.Save(Path.Combine(outDir, "selftest_screen.png"), Drawing.Imaging.ImageFormat.Png);

        double dpi = VisualTreeHelper.GetDpi(overlay).DpiScaleX;
        var render = new RenderTargetBitmap((int)(overlay.ActualWidth * dpi), (int)(overlay.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        render.Render(overlay.Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(render));
        using (var file = File.Create(Path.Combine(outDir, "selftest_overlay.png")))
            encoder.Save(file);

        string report = $"excludedFromCapture={overlay.ExcludedFromCapture}\nscreen={area}\ndpi={dpi}\noverlaySize={overlay.ActualWidth}x{overlay.ActualHeight}\n";
        File.WriteAllText(Path.Combine(outDir, "selftest.txt"), report);
        Console.WriteLine(report);
        overlay.Close();
        return 0;
    }

    /// <summary>Runs the capture → OCR → translate pipeline on the primary monitor and writes the results plus an annotated screenshot.</summary>
    async Task<int> TranslateScreenCliAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        BuildServices();
        if (_ocr is null || _translator is null) return 2;

        var translator = new ScreenTranslator(() => _ocr, () => _translator, Langs);
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
        _translator.Save();
        return 0;
    }
}
