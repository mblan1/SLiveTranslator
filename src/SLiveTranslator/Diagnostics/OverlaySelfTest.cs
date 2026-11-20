using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SLiveTranslator.Capture;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Overlay;
using SLiveTranslator.UI.Pages;
using SLiveTranslator.Updates;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.Diagnostics;

/// <summary>
/// --selftest: shows every overlay window with sample content, saves a screen capture and a rendering of each window,
/// and checks that each one is hidden from screen capture, has a transparent background, and lies on the monitor.
/// Then it opens the main window, shows each of its pages and each step of the welcome wizard, checks that it is
/// hidden from capture and took WPF-UI's theme, and checks the overlays again after it closed: the theme applied to it
/// must not reach them.
/// </summary>
internal static class OverlaySelfTest
{
    public const string ReportFile = "selftest.txt";

    public static async Task<int> RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var overlay = new OverlayHost();
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
            new SubtitleLineView("Con đường phía trước rất nguy hiểm.", false, LowConfidence: true),
            new SubtitleLineView("Take this sword with you. ↻", false, Failed: true),
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
            shot.Save(Path.Combine(outDir, "selftest_screen.png"), System.Drawing.Imaging.ImageFormat.Png);

        var report = new List<string> { $"screen={area}" };
        var failures = new List<string>();
        Check(overlay, area, "shown", outDir, report, failures);

        await CheckPickerAsync(subtitleRegion, outDir, report, failures);
        await CheckSharedPickerAsync(subtitleRegion, outDir, report, failures);
        await CheckMenuAsync(outDir, report, failures);
        await CheckMainWindowAsync(outDir, report, failures);
        Check(overlay, area, "after the main window", null, report, failures);
        await CheckUpdateWindowAsync(outDir, report, failures);

        report.Add(failures.Count == 0 ? "PASS" : $"FAIL ({failures.Count})");
        report.AddRange(failures);
        File.WriteAllLines(Path.Combine(outDir, ReportFile), report);
        foreach (var line in report) Console.WriteLine(line);
        overlay.Close();
        return failures.Count == 0 ? 0 : 3;
    }

    /// <summary>
    /// The region picker on the primary monitor, both steps, with a sample read region and a pretend live check: each
    /// step is saved as a picture, and the picker must stay hidden from screen capture (its live check reads under it).
    /// </summary>
    static async Task CheckPickerAsync(DRect read, string outDir, List<string> report, List<string> failures)
    {
        var look = new OverlaySettings { DisplayMode = DisplayMode.Shared };
        var picker = new RegionSelectWindow(Forms.Screen.PrimaryScreen!, RegionPickMode.Both, read, look,
            (_, _) => Task.FromResult<ReadPreview?>(new ReadPreview(2, "English", "Welcome, traveller.")));
        try
        {
            picker.Show();
            await Task.Delay(900); // the live check waits for the box to settle
            SaveRendering(picker, Path.Combine(outDir, "selftest_picker_1.png"));
            picker.Confirm();
            await Task.Delay(400);
            SaveRendering(picker, Path.Combine(outDir, "selftest_picker_2.png"));
            report.Add($"picker: visible={picker.IsVisible} pick={picker.Pick}");
            if (picker.Pick is not null) failures.Add("the picker ended after step 1 instead of going on to the display region");
        }
        catch (Exception ex)
        {
            failures.Add("picker: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            picker.Close();
        }
    }

    /// <summary>
    /// Step 2 alone in Shared on screen, started from a saved custom box that covers the read region: the box must start
    /// off the region (and so be saved off it), and "over the original" must not be offered.
    /// </summary>
    static async Task CheckSharedPickerAsync(DRect read, string outDir, List<string> report, List<string> failures)
    {
        var look = new OverlaySettings
        {
            DisplayMode = DisplayMode.Shared,
            Position = SubtitlePosition.Custom,
            DisplayRegion = RegionSettings.From(new DRect(read.X + 60, read.Y + 20, read.Width / 2, read.Height)),
        };
        var picker = new RegionSelectWindow(Forms.Screen.PrimaryScreen!, RegionPickMode.DisplayOnly, read, look, null);
        try
        {
            picker.Show();
            await Task.Delay(500);
            SaveRendering(picker, Path.Combine(outDir, "selftest_picker_shared.png"));
            var custom = picker.PendingPick?.Display?.Custom;
            bool off = custom is { } box && !Edges.Of(box).Overlaps(Edges.Of(read));
            report.Add($"shared picker: custom={custom} keptOff={off} overOffered={picker.OverPresetEnabled}");
            if (!off) failures.Add($"in Shared mode the picker kept a display box on the read region ({custom})");
            if (picker.OverPresetEnabled) failures.Add("in Shared mode the picker offers \"over the original\"");
        }
        catch (Exception ex)
        {
            failures.Add("shared picker: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            picker.Close();
        }
    }

    /// <summary>
    /// The in-game menu over the primary monitor: saved as a picture; it must be hidden from capture, take clicks without
    /// being activated, and leave the window in front where it was.
    /// </summary>
    static async Task CheckMenuAsync(string outDir, List<string> report, List<string> failures)
    {
        var menu = new MenuOverlay();
        try
        {
            var before = Native.GetForegroundWindow();
            var items = GameMenu.Build(new MenuState(true, false, false, false, OverlaySettings.DefaultFontSize, new HotkeySettings()));
            menu.ShowMenu(Loc.Instance["GameMenu.Title"], items, 2, Loc.F("GameMenu.Keys", HotkeySettings.Defaults[HotkeyAction.Menu]),
                Forms.Screen.PrimaryScreen!.Bounds);
            await Task.Delay(300);
            SaveRendering(menu, Path.Combine(outDir, "selftest_menu.png"));
            long ex = Native.GetWindowLongPtr(menu.Handle, Native.GWL_EXSTYLE).ToInt64();
            bool noActivate = (ex & Native.WS_EX_NOACTIVATE) != 0, clickThrough = (ex & Native.WS_EX_TRANSPARENT) != 0;
            bool keptFront = Native.GetForegroundWindow() == before;
            report.Add($"menu: visible={menu.IsVisible} excludedFromCapture={menu.ExcludedFromCapture} noActivate={noActivate} clickThrough={clickThrough} keptFront={keptFront} bounds={menu.BoundsPx}");
            if (!menu.IsVisible) failures.Add("the menu did not show");
            if (!menu.ExcludedFromCapture) failures.Add("the menu is not hidden from screen capture");
            if (!noActivate || clickThrough) failures.Add("the menu window would be activated, or does not take clicks");
            if (!keptFront) failures.Add("showing the menu took the foreground from the window in front");
        }
        catch (Exception ex)
        {
            failures.Add("menu: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            menu.Close();
        }
    }

    /// <summary>The main window on default settings (see <see cref="SelfTestHost"/>), every page shown once, then closed.</summary>
    static async Task CheckMainWindowAsync(string outDir, List<string> report, List<string> failures)
    {
        var errors = new List<string>();
        void OnError(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            errors.Add(e.Exception.GetType().Name + ": " + e.Exception.Message);
            e.Handled = true;
        }
        var bindings = new BindingErrors();
        PresentationTraceSources.Refresh(); // without a debugger WPF traces nothing until asked
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
        Application.Current.DispatcherUnhandledException += OnError;
        try
        {
            var vm = new SettingsViewModel(new SelfTestHost(outDir));
            // A provider card on the Translation page, so its bindings are shown too (no key: nothing is checked).
            vm.Translation.SelectedProvider = vm.Translation.ProviderChoices.First(c => c.Value == ProviderIds.Claude);
            var edited = vm.PendingChanges; // that edit is the self-test's own
            var main = MainWindow.Open(vm, AppTheme.System);
            await Task.Delay(800);
            bool themed = Wpf.Ui.UiApplication.Current.IsApplication && ReferenceEquals(Application.Current.MainWindow, main);
            report.Add($"main window: visible={main.IsVisible} excludedFromCapture={main.ExcludedFromCapture} themed={themed}");
            if (!main.ExcludedFromCapture) failures.Add("the main window is not hidden from screen capture");
            if (!themed) failures.Add("WPF-UI's theme does not reach the main window (it is not the app's MainWindow, or the dictionaries came too late)");
            foreach (var page in new[]
                     {
                         typeof(TranslationPage), typeof(GameProfilePage), typeof(RegionsPage), typeof(DisplayPage), typeof(TimingPage),
                         typeof(SubtitlesPage), typeof(OverlayPage), typeof(HotkeysPage),
                         typeof(NotificationsPage), typeof(StoragePage), typeof(GeneralPage), typeof(HistoryPage), typeof(HomePage),
                     })
            {
                bool shown = main.RootNavigation.Navigate(page);
                await Task.Delay(500);
                report.Add($"main window: page {page.Name} shown={shown}");
                if (!shown) failures.Add($"the main window did not show the page {page.Name}");
                SaveRendering(main, Path.Combine(outDir, $"selftest_main_{page.Name}.png"));
                SavePageRendering(main, page, Path.Combine(outDir, $"selftest_page_{page.Name}.png"));
                // A tab's content is made only when it is selected: each one is shown, so its bindings are checked too.
                foreach (var tabs in VisualDescendants(main).OfType<TabControl>().ToList())
                {
                    for (int tab = 1; tab < tabs.Items.Count; tab++)
                    {
                        tabs.SelectedIndex = tab;
                        await Task.Delay(300);
                        SaveRendering(main, Path.Combine(outDir, $"selftest_main_{page.Name}_tab{tab + 1}.png"));
                    }
                    tabs.SelectedIndex = 0;
                }
            }
            // Showing a page must not change a setting (a box or slider that rounds the value it shows, say).
            if (vm.PendingChanges != edited) failures.Add("showing the pages changed a setting: " + (vm.PendingChanges & ~edited));
            // Each theme switch repaints the app's MainWindow: it must land on the main window, never on an overlay.
            main.Show(Loc.Instance["Main.Saved"], "", MessageKind.Success); // the snackbar a save shows
            await Task.Delay(700);
            SaveRendering(main, Path.Combine(outDir, "selftest_main_snackbar.png"));
            vm.Welcome.Open();
            for (int step = 0; step < WelcomeViewModel.StepCount; step++)
            {
                vm.Welcome.Step = step;
                await Task.Delay(300);
                report.Add($"main window: welcome step {step + 1} shown={vm.Welcome.IsOpen}");
                SaveRendering(main, Path.Combine(outDir, $"selftest_welcome_{step + 1}.png"));
            }
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.System })
            {
                main.ApplyTheme(theme);
                await Task.Delay(200);
            }
            report.Add($"main window: theme after Dark, Light, System = {Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()}");
            main.ApplyTheme(AppTheme.Dark); // the overlays are checked next, after the theme that paints a window opaque dark
            await Task.Delay(200);
            vm.Discard();
            main.Close();
            await Task.Delay(300);
            if (ReferenceEquals(Application.Current.MainWindow, main)) failures.Add("the closed main window is still the app's MainWindow");
        }
        finally
        {
            Application.Current.DispatcherUnhandledException -= OnError;
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindings);
        }
        foreach (var error in errors) failures.Add("main window: " + error);
        report.Add($"main window: {bindings.Messages.Count} binding errors");
        report.AddRange(bindings.Messages.Distinct().Select(m => "  binding: " + m));
    }

    /// <summary>
    /// The update window with a sample release, dark and light, in each of its looks: ready, downloading and failed. Each
    /// is saved as a picture, and none of its bindings may fail.
    /// </summary>
    static async Task CheckUpdateWindowAsync(string outDir, List<string> report, List<string> failures)
    {
        var bindings = new BindingErrors();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
        try
        {
            var update = new UpdateInfo(new Version(2, 1, 0, 0), "v2.1.0",
                "## New\n* **Updates** – the app checks for a new version and installs it in a minute.\n* Faster OCR on bright scenes.\n\n" +
                "| Hotkey | Action |\n| --- | --- |\n| `Ctrl+Shift+S` | Subtitles on/off |",
                new Uri("https://github.com/" + UpdateFeed.Repository + "/releases/download/v2.1.0/" + UpdateFeed.AssetName), 200_482_816, null,
                UpdateFeed.ReleasesPage, DateTimeOffset.Now);
            foreach (bool dark in new[] { true, false })
            {
                string theme = dark ? "dark" : "light";
                var vm = new UpdateViewModel(update, "2.0.0", new NoUpdateActions());
                var window = new UpdateWindow(vm, dark) { ShowActivated = false };
                try
                {
                    window.Show();
                    await Task.Delay(300);
                    SaveRendering(window, Path.Combine(outDir, $"selftest_update_{theme}_ready.png"));
                    vm.ShowSample(new DownloadProgress(87_400_000, update.Size, 3_600_000));
                    await Task.Delay(200);
                    SaveRendering(window, Path.Combine(outDir, $"selftest_update_{theme}_downloading.png"));
                    vm.ShowSampleProblem(UpdateProblem.Network, "No such host is known.");
                    await Task.Delay(200);
                    SaveRendering(window, Path.Combine(outDir, $"selftest_update_{theme}_failed.png"));
                    report.Add($"update window ({theme}): visible={window.IsVisible} size={window.ActualWidth:0}x{window.ActualHeight:0}");
                    if (!window.IsVisible) failures.Add($"the update window ({theme}) did not show");
                }
                finally
                {
                    window.Close();
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add("update window: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindings);
        }
        report.Add($"update window: {bindings.Messages.Count} binding errors");
        foreach (var message in bindings.Messages.Distinct()) failures.Add("update window binding: " + message);
    }

    sealed class NoUpdateActions : IUpdateActions
    {
        public Task DownloadAsync(UpdateInfo update, IProgress<DownloadProgress> progress, System.Threading.CancellationToken ct) => Task.CompletedTask;

        public void InstallAndRestart()
        {
        }

        public void OpenPage(Uri page)
        {
        }

        public void Quit()
        {
        }
    }

    static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var below in VisualDescendants(child)) yield return below;
        }
    }

    static void SaveRendering(Window window, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(window)));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>
    /// The whole page, also what is scrolled out of the window: its content is laid out at its full height inside the
    /// page's scroll viewer, and is drawn here on the window's background.
    /// </summary>
    static void SavePageRendering(Window window, Type pageType, string path)
    {
        if (VisualDescendants(window).OfType<Page>().FirstOrDefault(p => p.GetType() == pageType) is not { Content: FrameworkElement content }
            || content.ActualWidth < 1 || content.ActualHeight < 1) return;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var size = new Size(content.ActualWidth, content.ActualHeight);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(window.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }
        var render = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        render.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(render));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>What WPF reports about bindings that do not work, for the report.</summary>
    sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = new();

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null) Messages.Add(message);
        }
    }

    static void Check(OverlayHost overlay, DRect screen, string phase, string? saveDir, List<string> report, List<string> failures)
    {
        int count = 0;
        foreach (var (name, window) in overlay.Windows)
        {
            count++;
            var render = Render(window);
            bool transparent = window.AllowsTransparency && window.Background is SolidColorBrush { Color.A: 0 } && TopLeftAlpha(render) == 0;
            var bounds = BoundsPx(window);
            report.Add($"{phase}: {name,-9} visible={window.IsVisible} excludedFromCapture={window.ExcludedFromCapture} transparent={transparent} bounds={bounds}");
            if (!window.ExcludedFromCapture) failures.Add($"{phase}: the {name} window is not hidden from screen capture");
            if (!transparent) failures.Add($"{phase}: the {name} window's background is not transparent");
            if (!window.IsVisible || !screen.Contains(bounds)) failures.Add($"{phase}: the {name} window is not shown on the monitor ({bounds})");
            if (window.SizeToContent != SizeToContent.Manual && window.Content is FrameworkElement content)
            {
                double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
                if (Math.Abs(bounds.Width - content.ActualWidth * scale) > 1 || Math.Abs(bounds.Height - content.ActualHeight * scale) > 1)
                    failures.Add($"{phase}: the {name} window ({bounds.Size}) is larger than its content ({content.ActualWidth * scale}x{content.ActualHeight * scale})");
            }
            if (saveDir is null) continue;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(render));
            using var file = File.Create(Path.Combine(saveDir, $"selftest_{name}.png"));
            encoder.Save(file);
        }
        if (count != 5) failures.Add($"{phase}: {count} overlay windows instead of 5");
    }

    static RenderTargetBitmap Render(Window window)
    {
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var render = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scale)), Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        render.Render(window);
        return render;
    }

    /// <summary>The corner outside every rounded box (and outside the labels): only the window background is drawn there.</summary>
    static byte TopLeftAlpha(BitmapSource bitmap)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    static DRect BoundsPx(ClickThroughWindow window) =>
        Native.DwmGetWindowAttribute(window.Handle, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<Native.RECT>()) == 0 ? r.ToRectangle() : DRect.Empty;
}
