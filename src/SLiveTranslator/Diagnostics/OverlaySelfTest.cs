using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SLiveTranslator.Capture;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Overlay;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.Diagnostics;

/// <summary>
/// --selftest: shows every overlay window with sample content, saves a screen capture and a rendering of each window,
/// and checks that each one is hidden from screen capture, has a transparent background, and lies on the monitor,
/// also after the settings window was opened and closed (a theme applied to the app's windows must not reach them).
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

        var settings = new SettingsWindow(new AppSettings(), () => null, () => { });
        settings.Show();
        await Task.Delay(300);
        settings.Close();
        await Task.Delay(200);
        Check(overlay, area, "after the settings window", null, report, failures);

        report.Add(failures.Count == 0 ? "PASS" : $"FAIL ({failures.Count})");
        report.AddRange(failures);
        File.WriteAllLines(Path.Combine(outDir, ReportFile), report);
        foreach (var line in report) Console.WriteLine(line);
        overlay.Close();
        return failures.Count == 0 ? 0 : 3;
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
