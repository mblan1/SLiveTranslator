using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Overlay;
using Xunit;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.Tests;

/// <summary>The presenter with real overlay windows on a thread of their own, following the overlay settings in use.</summary>
public class SubtitlePresenterTests
{
    static DRect Region => new(Forms.Screen.PrimaryScreen!.Bounds.Left + 400, Forms.Screen.PrimaryScreen.Bounds.Top + 400, 800, 100);

    static void OnStaThread(OverlaySettings look, Action<OverlayHost, SubtitlePresenter> test, TimeSpan? linger = null)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var overlay = new OverlayHost();
            var presenter = new SubtitlePresenter(Dispatcher.CurrentDispatcher, overlay, () => Region, () => look, () => "Ctrl+Shift+H",
                _ => { }, _ => { }, _ => { }, linger);
            try
            {
                test(overlay, presenter);
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

    /// <summary>Runs what the presenter queued on the UI thread.</summary>
    static void Flush() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

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

    static void Say(SubtitlePresenter presenter, int lines)
    {
        for (int i = 1; i <= lines; i++)
        {
            presenter.LineDetected(i, $"Line {i}.", false);
            presenter.LineTranslated(i, $"Dòng {i}.", false, "test");
        }
        Flush();
    }

    static ClickThroughWindow Window(OverlayHost overlay, string name) => overlay.Windows.Single(w => w.Name == name).Window;

    static TextBlock[] SubtitleBlocks(OverlayHost overlay) =>
        ((StackPanel)((Border)Window(overlay, "subtitles").Content).Child).Children.OfType<TextBlock>().Where(b => b.Visibility == Visibility.Visible).ToArray();

    static string Text(TextBlock block) => new TextRange(block.ContentStart, block.ContentEnd).Text;

    static string[] HistoryTexts(OverlayHost overlay)
    {
        var panel = (StackPanel)((Border)Window(overlay, "history").Content).Child;
        return ((StackPanel)panel.Children[2]).Children.OfType<StackPanel>().Where(i => i.Visibility == Visibility.Visible)
            .Select(i => ((TextBlock)i.Children[0]).Text).ToArray();
    }

    [Fact]
    public void The_box_and_the_history_show_as_many_lines_as_the_settings_ask() =>
        OnStaThread(new OverlaySettings { MaxLines = 2, HistoryLines = 3 }, (overlay, presenter) =>
        {
            Say(presenter, 5);
            presenter.ToggleHistory();

            Assert.Equal(["Dòng 4.", "Dòng 5."], SubtitleBlocks(overlay).Select(b => b.Text));
            Assert.Equal(["Dòng 3.", "Dòng 4.", "Dòng 5."], HistoryTexts(overlay));
        });

    [Fact]
    public void Restyle_draws_the_lines_on_screen_again_in_the_saved_look()
    {
        var look = new OverlaySettings { FontSize = 22, HistoryLines = 3 };
        OnStaThread(look, (overlay, presenter) =>
        {
            Say(presenter, 4);
            presenter.ToggleHistory();
            Assert.Equal(22, SubtitleBlocks(overlay)[^1].FontSize);

            look.FontSize = 30;
            look.ShowOriginal = true;
            look.HistoryLines = 4;
            presenter.Restyle();
            Flush();

            var newest = SubtitleBlocks(overlay)[^1];
            Assert.Equal(30, newest.FontSize);
            Assert.Contains("Line 4.", Text(newest)); // the game's text under the translation
            Assert.StartsWith("Dòng 4.", Text(newest));
            Assert.Equal(4, HistoryTexts(overlay).Length);
        });
    }

    [Fact]
    public void Restyle_after_the_box_lingered_out_brings_nothing_back() =>
        OnStaThread(new OverlaySettings(), (overlay, presenter) =>
        {
            Say(presenter, 2);
            Assert.True(Window(overlay, "subtitles").IsVisible);

            presenter.Cleared(); // the game hid its subtitle; the lines stay for the linger, then go
            Flush();
            Pump(TimeSpan.FromMilliseconds(300));
            Assert.False(Window(overlay, "subtitles").IsVisible);

            presenter.Restyle(); // a save of the overlay look, or the end of "Preview on screen"
            Flush();
            Assert.False(Window(overlay, "subtitles").IsVisible);
        }, linger: TimeSpan.FromMilliseconds(50));
}
