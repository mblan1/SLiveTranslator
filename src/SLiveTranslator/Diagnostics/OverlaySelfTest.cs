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
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.Diagnostics;

/// <summary>
/// --selftest: shows every overlay window with sample content, saves a screen capture and a rendering of each window,
/// and checks that each one is hidden from screen capture, has a transparent background, and lies on the monitor.
/// Then it opens the main window, shows each of its pages, checks that it is hidden from capture and took WPF-UI's
/// theme, and checks the overlays again after it closed: the theme applied to it must not reach them. Last, the classic
/// settings window, whose text must stay readable with WPF-UI's dark styles in the app's resources.
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

        await CheckMainWindowAsync(outDir, report, failures);
        Check(overlay, area, "after the main window", null, report, failures);
        await CheckClassicWindowAsync(outDir, report, failures);

        report.Add(failures.Count == 0 ? "PASS" : $"FAIL ({failures.Count})");
        report.AddRange(failures);
        File.WriteAllLines(Path.Combine(outDir, ReportFile), report);
        foreach (var line in report) Console.WriteLine(line);
        overlay.Close();
        return failures.Count == 0 ? 0 : 3;
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
            var main = MainWindow.Open(vm, AppTheme.System);
            await Task.Delay(800);
            bool themed = Wpf.Ui.UiApplication.Current.IsApplication && ReferenceEquals(Application.Current.MainWindow, main);
            report.Add($"main window: visible={main.IsVisible} excludedFromCapture={main.ExcludedFromCapture} themed={themed}");
            if (!main.ExcludedFromCapture) failures.Add("the main window is not hidden from screen capture");
            if (!themed) failures.Add("WPF-UI's theme does not reach the main window (it is not the app's MainWindow, or the dictionaries came too late)");
            foreach (var page in new[]
                     {
                         typeof(TranslationPage), typeof(GameProfilePage), typeof(SubtitlesPage), typeof(OverlayPage), typeof(HotkeysPage),
                         typeof(NotificationsPage), typeof(StoragePage), typeof(GeneralPage), typeof(HomePage),
                     })
            {
                bool shown = main.RootNavigation.Navigate(page);
                await Task.Delay(300);
                report.Add($"main window: page {page.Name} shown={shown}");
                if (!shown) failures.Add($"the main window did not show the page {page.Name}");
                SaveRendering(main, Path.Combine(outDir, $"selftest_main_{page.Name}.png"));
            }
            // Each theme switch repaints the app's MainWindow: it must land on the main window, never on an overlay.
            main.Show(Loc.Instance["Main.Saved"], "", MessageKind.Success); // the snackbar a save shows
            await Task.Delay(700);
            SaveRendering(main, Path.Combine(outDir, "selftest_main_snackbar.png"));
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.System })
            {
                main.ApplyTheme(theme);
                await Task.Delay(200);
            }
            report.Add($"main window: theme after Dark, Light, System = {Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()}");
            main.ApplyTheme(AppTheme.Dark); // the classic window is checked next in the theme whose text is white
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
    /// The classic settings window after the main window closed in the dark theme: WPF-UI's implicit styles stay in the
    /// app's resources, white text included, and its buttons and list items must keep the plain look on its white background.
    /// </summary>
    static async Task CheckClassicWindowAsync(string outDir, List<string> report, List<string> failures)
    {
        var classic = new SettingsWindow(new AppSettings(), () => null, () => { }) { Topmost = false, ShowActivated = false };
        classic.Show();
        await Task.Delay(400);
        SaveRendering(classic, Path.Combine(outDir, "selftest_classic.png"));
        var backdrop = (classic.Background as SolidColorBrush)?.Color ?? Colors.White;
        int count = 0;
        foreach (var control in LogicalDescendants(classic).OfType<Control>().Where(c => c is Button or ComboBoxItem))
        {
            count++;
            if (Contrast(control.Foreground, control.Background, backdrop) is { } contrast && contrast < 3)
                failures.Add($"classic window: the {control.GetType().Name} '{(control as ContentControl)?.Content}' is unreadable (contrast {contrast:0.0}:1)");
        }
        report.Add($"classic window: {count} buttons and list items checked");
        if (count == 0) failures.Add("classic window: no button or list item found to check");
        classic.Close();
    }

    static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var below in LogicalDescendants(child)) yield return below;
        }
    }

    /// <summary>The WCAG contrast of the text on its background laid over <paramref name="backdrop"/>; null for brushes that are not plain colours.</summary>
    static double? Contrast(Brush? foreground, Brush? background, Color backdrop)
    {
        if (foreground is not SolidColorBrush fg) return null;
        var under = background switch
        {
            null => backdrop,
            SolidColorBrush bg => Over(bg.Color, backdrop),
            _ => (Color?)null,
        };
        if (under is not { } back) return null;
        double a = Luminance(Over(fg.Color, back)), b = Luminance(back);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    static Color Over(Color top, Color bottom)
    {
        double t = top.A / 255.0;
        byte Mix(byte x, byte y) => (byte)Math.Round(x * t + y * (1 - t));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    static double Luminance(Color c)
    {
        static double Channel(byte v) => v / 255.0 <= 0.03928 ? v / 255.0 / 12.92 : Math.Pow((v / 255.0 + 0.055) / 1.055, 2.4);
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    static void SaveRendering(Window window, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(window)));
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
