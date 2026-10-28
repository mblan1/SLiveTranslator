using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SLiveTranslator.Capture;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Overlay;
using Xunit;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.Tests;

/// <summary>The overlay windows, on a thread of their own (WPF needs STA); each test closes what it showed.</summary>
public class OverlayHostTests
{
    static void OnStaThread(Action<OverlayHost> test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var overlay = new OverlayHost();
            try
            {
                test(overlay);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                overlay.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    static DRect Region => new(Forms.Screen.PrimaryScreen!.Bounds.Left + 400, Forms.Screen.PrimaryScreen.Bounds.Bottom - 300, 800, 100);

    static TextBlock[] Lines(OverlayHost overlay)
    {
        var window = overlay.Windows.Single(w => w.Name == "subtitles").Window;
        return ((StackPanel)((Border)window.Content).Child).Children.OfType<TextBlock>().ToArray();
    }

    [Fact]
    public void Windows_are_created_when_first_needed_and_each_is_hidden_from_capture() => OnStaThread(overlay =>
    {
        Assert.Empty(overlay.Windows);

        overlay.ShowSubtitleLines([new SubtitleLineView("Chào mừng, lữ khách.", false)], Region, 22);
        overlay.ShowStatus("Subtitles ON", StatusKind.On);

        Assert.Equal(["subtitles", "toast"], overlay.Windows.Select(w => w.Name));
        Assert.All(overlay.Windows, w => Assert.True(w.Window.ExcludedFromCapture));
        Assert.True(overlay.ExcludedFromCapture);
    });

    [Fact]
    public void The_subtitle_lines_reuse_their_text_blocks() => OnStaThread(overlay =>
    {
        overlay.ShowSubtitleLines(
        [
            new SubtitleLineView("Chào mừng, lữ khách.", false),
            new SubtitleLineView("Con đường phía trước rất nguy hiểm.", false, Failed: true),
            new SubtitleLineView("…", true, LowConfidence: true),
        ], Region, 22);
        var first = Lines(overlay);

        overlay.ShowSubtitleLines([new SubtitleLineView("Cầm lấy thanh kiếm này.", false)], Region, 22);
        var second = Lines(overlay);

        Assert.Equal(first, second);
        Assert.Equal("Cầm lấy thanh kiếm này.", second[0].Text);
        Assert.All(second.Skip(1), block => Assert.Equal(Visibility.Collapsed, block.Visibility));
        Assert.Equal(FontStyles.Normal, second[0].FontStyle);

        overlay.ShowSubtitleLines([], Region, 22);
        Assert.False(overlay.Windows.Single(w => w.Name == "subtitles").Window.IsVisible);
    });

    [Fact]
    public void A_failed_line_is_dimmed_and_a_low_confidence_line_is_in_italics() => OnStaThread(overlay =>
    {
        overlay.ShowSubtitleLines(
        [
            new SubtitleLineView("Take this sword. ↻", false, Failed: true),
            new SubtitleLineView("Cầm lấy thanh kiếm này.", false, LowConfidence: true),
        ], Region, 22);
        var lines = Lines(overlay);

        Assert.NotEqual(lines[1].Foreground, lines[0].Foreground);
        Assert.Equal(FontStyles.Italic, lines[1].FontStyle);
        Assert.Equal(FontStyles.Normal, lines[0].FontStyle);
    });

    [Fact]
    public void The_labels_window_exists_only_while_labels_are_shown() => OnStaThread(overlay =>
    {
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        overlay.ShowLabels([(new DRect(screen.Left + 100, screen.Top + 100, 300, 40), "Bình thuốc hồi máu", 1)], screen, 22);
        var labels = overlay.Windows.Single(w => w.Name == "labels").Window;
        Assert.Equal(SizeToContent.Manual, labels.SizeToContent);

        overlay.ClearLabels();

        Assert.DoesNotContain(overlay.Windows, w => w.Name == "labels");
    });

    [Fact]
    public void Hiding_the_overlay_keeps_what_was_shown_for_the_next_show() => OnStaThread(overlay =>
    {
        overlay.ShowSubtitleLines([new SubtitleLineView("Chào mừng, lữ khách.", false)], Region, 22);
        overlay.ShowLiveBadge(Region, "Subtitles on");

        overlay.Hide();
        Assert.All(overlay.Windows, w => Assert.False(w.Window.IsVisible));
        overlay.ShowStatus("Translating screen…", StatusKind.Info);

        Assert.All(overlay.Windows, w => Assert.True(w.Window.IsVisible));
    });

    [Fact]
    public void The_toast_goes_to_the_top_right_or_above_the_subtitle_region() => OnStaThread(overlay =>
    {
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        overlay.ShowStatus("Subtitle region saved", StatusKind.Info, screen, 5, NoticePosition.TopRight);
        Pump(TimeSpan.FromMilliseconds(100)); // the window takes the size of its new text on the next layout pass
        var toast = overlay.Windows.Single(w => w.Name == "toast").Window;
        Assert.True(Native.GetWindowRect(toast.Handle, out var right));
        Assert.InRange(right.Right, screen.Right - 120, screen.Right);
        Assert.InRange(right.Top, screen.Top, screen.Top + 120);

        overlay.ShowStatus("Region cut off", StatusKind.Warning, screen, 5, NoticePosition.NearRegion, Region);
        Pump(TimeSpan.FromMilliseconds(100));
        Assert.True(Native.GetWindowRect(toast.Handle, out var near));
        Assert.True(near.Bottom <= Region.Top, $"toast {near.ToRectangle()} overlaps the region {Region}");
        Assert.InRange((near.Left + near.Right) / 2, (Region.Left + Region.Right) / 2 - 3, (Region.Left + Region.Right) / 2 + 3);
    });

    [Fact]
    public void A_toast_near_a_region_at_the_bottom_stays_off_the_lines_drawn_above_it() => OnStaThread(overlay =>
    {
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        var region = new DRect(screen.Left + 300, screen.Bottom - 120, 1000, 110); // no room for the lines below it
        overlay.ShowSubtitleLines(
        [
            new SubtitleLineView("Ta đã bảo cháu rồi, Ciri.", false),
            new SubtitleLineView("Con đường phía trước rất nguy hiểm.", false),
            new SubtitleLineView("Cầm lấy thanh kiếm này.", false),
        ], region, 22);
        Pump(TimeSpan.FromMilliseconds(100));
        overlay.ShowStatus("A subtitle line could not be translated", StatusKind.Error, screen, 5, NoticePosition.NearRegion, region);
        Pump(TimeSpan.FromMilliseconds(100));

        var subtitles = overlay.Windows.Single(w => w.Name == "subtitles").Window;
        var toast = overlay.Windows.Single(w => w.Name == "toast").Window;
        Assert.True(Native.GetWindowRect(subtitles.Handle, out var lines));
        Assert.True(Native.GetWindowRect(toast.Handle, out var notice));
        Assert.True(lines.Bottom <= region.Top, $"lines {lines.ToRectangle()} are not above the region {region}");
        Assert.True(notice.Bottom <= lines.Top, $"toast {notice.ToRectangle()} covers the lines {lines.ToRectangle()}");
    });

    /// <summary>Runs the thread's dispatcher for a while, so its timers fire.</summary>
    static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void What_hid_itself_while_the_overlay_was_hidden_does_not_come_back() => OnStaThread(overlay =>
    {
        overlay.ShowSubtitleLines([new SubtitleLineView("Chào mừng, lữ khách.", false)], Region, 22);
        overlay.ShowLiveBadge(Region, "Subtitles on");
        overlay.ShowStatus("Translating screen…", StatusKind.Info, null, seconds: 0.05);
        ClickThroughWindow Window(string name) => overlay.Windows.Single(w => w.Name == name).Window;

        overlay.Hide();
        overlay.HideLiveBadge(); // subtitles turned off meanwhile
        Pump(TimeSpan.FromMilliseconds(300)); // the toast expired
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        overlay.ShowLabels([(new DRect(screen.Left + 100, screen.Top + 100, 300, 40), "Bình thuốc hồi máu", 1)], screen, 22);

        Assert.True(Window("subtitles").IsVisible);
        Assert.True(Window("labels").IsVisible);
        Assert.False(Window("badge").IsVisible);
        Assert.False(Window("toast").IsVisible);
    });

    [Fact]
    public void The_subtitle_box_takes_the_look_of_the_overlay_settings() => OnStaThread(overlay =>
    {
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        var region = new DRect(screen.Left + 400, screen.Top + 500, 800, 100);
        SubtitleLineView[] lines = [new("Con đường phía trước rất nguy hiểm.", false, Original: "The road ahead is dangerous.")];

        overlay.ShowSubtitleLines(lines, region, 22, new SubtitleStyle(0.5, 0.25, SubtitlePosition.BottomCenter, ShowOriginal: true));
        Pump(TimeSpan.FromMilliseconds(100));
        var window = overlay.Windows.Single(w => w.Name == "subtitles").Window;
        var line = Lines(overlay)[0];
        Assert.Equal(0.5, line.Opacity, 3);
        Assert.Equal(64, ((System.Windows.Media.SolidColorBrush)((Border)window.Content).Background).Color.A);
        Assert.Contains("The road ahead is dangerous.", new System.Windows.Documents.TextRange(line.ContentStart, line.ContentEnd).Text);
        Assert.True(Native.GetWindowRect(window.Handle, out var bottom));
        Assert.InRange(bottom.Bottom, screen.Bottom - screen.Height / 10, screen.Bottom - screen.Height / 25);
        Assert.InRange((bottom.Left + bottom.Right) / 2, (screen.Left + screen.Right) / 2 - 3, (screen.Left + screen.Right) / 2 + 3);

        overlay.ShowSubtitleLines(lines, region, 22, new SubtitleStyle(1, 0.8, SubtitlePosition.AboveRegion, ShowOriginal: false));
        Pump(TimeSpan.FromMilliseconds(100));
        Assert.Equal("Con đường phía trước rất nguy hiểm.", Lines(overlay)[0].Text);
        Assert.True(Native.GetWindowRect(window.Handle, out var above));
        Assert.True(above.Bottom <= region.Top, $"lines {above.ToRectangle()} are not above the region {region}");
    });
}
