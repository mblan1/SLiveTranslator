using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
}
