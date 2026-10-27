using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.UI;
using Xunit;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.Tests;

/// <summary>Records what the notifier shows, plays and hides.</summary>
sealed class FakeNoticeSurface : INoticeSurface
{
    public bool HasOverlay { get; set; } = true;
    public List<ToastRequest> Toasts { get; } = new();
    public int ToastsHidden { get; private set; }
    public List<(string Title, string Text, bool Warning)> Balloons { get; } = new();
    public List<NoticeSound> Sounds { get; } = new();
    public List<string> Badge { get; } = new(); // "show: text" or "hide"

    public void ShowToast(ToastRequest toast) => Toasts.Add(toast);
    public void HideToast() => ToastsHidden++;
    public void ShowBalloon(string title, string text, bool warning) => Balloons.Add((title, text, warning));
    public void Play(NoticeSound sound) => Sounds.Add(sound);
    public void ShowBadge(DRect region, string text) => Badge.Add("show: " + text);
    public void HideBadge() => Badge.Add("hide");
}

public class NotifierTests
{
    static readonly DRect Region = new(400, 900, 1200, 120);

    sealed class Harness
    {
        public FakeNoticeSurface Surface { get; } = new();
        public NotificationSettings Settings { get; } = new();
        public TestClock Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public DRect? KnownRegion { get; set; }
        public List<TimeSpan> Waits { get; } = new();
        public Notifier Notifier { get; }

        public Harness() => Notifier = new Notifier(Surface, () => Settings, () => KnownRegion, Clock, wait =>
        {
            Waits.Add(wait);
            Clock.Advance(wait);
            return Task.CompletedTask;
        });

        public List<string> Texts => Surface.Toasts.Select(t => t.Text).ToList();
    }

    [Fact]
    public void Each_category_shows_with_its_seconds_accent_and_the_chosen_position()
    {
        var h = new Harness();
        h.Settings.Position = NoticePosition.TopRight;
        h.Settings.Quota.Seconds = 11;
        var anchor = new DRect(0, 0, 1920, 1080);

        Assert.True(h.Notifier.Notify(NoticeCategory.Status, "Subtitle region saved", anchor));
        h.Notifier.Notify(NoticeCategory.Warning, "Region cut off");
        h.Notifier.Notify(NoticeCategory.Error, "OCR is not available.");
        h.Notifier.Notify(NoticeCategory.Quota, "MyMemory: 80%");
        h.Notifier.Notify(NoticeCategory.Status, "Subtitles ON", kind: StatusKind.On);

        Assert.Equal(
        [
            new ToastRequest("Subtitle region saved", StatusKind.Info, anchor, NoticePosition.TopRight, null, 2.5),
            new ToastRequest("Region cut off", StatusKind.Warning, null, NoticePosition.TopRight, null, 8),
            new ToastRequest("OCR is not available.", StatusKind.Error, null, NoticePosition.TopRight, null, 6),
            new ToastRequest("MyMemory: 80%", StatusKind.Warning, null, NoticePosition.TopRight, null, 11),
            new ToastRequest("Subtitles ON", StatusKind.On, null, NoticePosition.TopRight, null, 2.5),
        ], h.Surface.Toasts);
        Assert.Empty(h.Surface.Sounds); // sound is off by default
    }

    [Fact]
    public void A_switched_off_category_shows_nothing()
    {
        var h = new Harness();
        h.Settings.Warning.Enabled = false;

        Assert.False(h.Notifier.Notify(NoticeCategory.Warning, "Region cut off"));
        Assert.False(h.Notifier.Feedback(NoticeCategory.Warning, "Subtitle region is almost the whole screen"));
        Assert.True(h.Notifier.Notify(NoticeCategory.Status, "Subtitles ON"));
        h.Settings.Status.Enabled = false;
        Assert.False(h.Notifier.Progress("Translating screen…", null, 30));

        Assert.Equal(["Subtitles ON"], h.Texts);
    }

    [Fact]
    public void A_repeat_within_ten_seconds_shows_once_and_a_changed_text_shows()
    {
        var h = new Harness();
        const string down = "No translation service is working right now.";

        h.Notifier.Notify(NoticeCategory.Error, down);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(h.Notifier.Notify(NoticeCategory.Error, down)); // the next subtitle line
        h.Notifier.Notify(NoticeCategory.Warning, down); // another category
        h.Clock.Advance(Notifier.RepeatWindows[0]);
        h.Notifier.Notify(NoticeCategory.Error, down); // ten seconds later
        h.Notifier.Notify(NoticeCategory.Status, "Claude is answering again.");
        h.Notifier.Notify(NoticeCategory.Status, "Google is answering again.");
        h.Notifier.Notify(NoticeCategory.Status, "Claude is answering again."); // the category's last notice changed in between

        Assert.Equal([down, down, down, "Claude is answering again.", "Google is answering again.", "Claude is answering again."], h.Texts);
        Assert.Equal([StatusKind.Error, StatusKind.Warning, StatusKind.Error], h.Surface.Toasts.Take(3).Select(t => t.Kind));
    }

    [Fact]
    public void A_notice_that_keeps_coming_back_waits_longer_each_time_and_then_shows_for_its_category_seconds()
    {
        var h = new Harness();
        const string failed = "A subtitle line could not be translated:\nNo translation service is working right now. "
                              + "Google is blocked for 30 minutes. MyMemory: the free daily quota is used up.";
        var shownAt = new List<int>();

        for (int second = 0; second <= 900; second += 5) // a subtitle line fails every 5 s for 15 minutes
        {
            if (h.Notifier.Notify(NoticeCategory.Error, failed)) shownAt.Add(second);
            h.Clock.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Equal([0, 10, 40, 160, 760], shownAt); // windows of 10 s, 30 s, 2 min, then 10 min
        Assert.Equal(1 + failed.Length / 15.0, h.Surface.Toasts[0].Seconds); // read in full the first time
        Assert.All(h.Surface.Toasts.Skip(1), t => Assert.Equal(6, t.Seconds)); // the Error category's seconds, no reading time
    }

    [Fact]
    public void After_a_quiet_while_a_repeat_counts_as_new()
    {
        var h = new Harness();

        h.Notifier.Notify(NoticeCategory.Error, "Reading the subtitles failed");
        h.Clock.Advance(Notifier.RepeatWindows[0]);
        Assert.True(h.Notifier.Notify(NoticeCategory.Error, "Reading the subtitles failed")); // the next window is 30 s
        h.Clock.Advance(Notifier.QuietReset); // nothing failed for two minutes
        Assert.True(h.Notifier.Notify(NoticeCategory.Error, "Reading the subtitles failed"));
        h.Clock.Advance(Notifier.RepeatWindows[0]);

        Assert.True(h.Notifier.Notify(NoticeCategory.Error, "Reading the subtitles failed")); // the windows start again at 10 s
    }

    [Fact]
    public void Feedback_shows_every_time_and_leaves_the_repeat_check_alone()
    {
        var h = new Harness();
        h.Settings.Position = NoticePosition.TopRight;

        h.Notifier.Feedback(NoticeCategory.Status, "Subtitles ON", kind: StatusKind.On);
        h.Notifier.Feedback(NoticeCategory.Status, "Subtitles OFF", kind: StatusKind.Off);
        h.Notifier.Feedback(NoticeCategory.Status, "Subtitles ON", kind: StatusKind.On);
        h.Notifier.Feedback(NoticeCategory.Status, "Overlay cleared");
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Notifier.Feedback(NoticeCategory.Status, "Overlay cleared"); // the key pressed again
        h.Notifier.Notify(NoticeCategory.Error, "A subtitle line could not be translated");
        h.Notifier.Feedback(NoticeCategory.Error, "Translate screen failed");

        Assert.False(h.Notifier.Notify(NoticeCategory.Error, "A subtitle line could not be translated")); // still a repeat
        Assert.Equal(
        [
            "Subtitles ON", "Subtitles OFF", "Subtitles ON", "Overlay cleared", "Overlay cleared",
            "A subtitle line could not be translated", "Translate screen failed",
        ], h.Texts);
        Assert.Equal([StatusKind.On, StatusKind.Off, StatusKind.On, StatusKind.Info], h.Surface.Toasts.Take(4).Select(t => t.Kind));
        Assert.All(h.Surface.Toasts, t => Assert.Equal(NoticePosition.TopRight, t.Position));
    }

    [Theory]
    [InlineData(10.2)] // the second progress notice is past the window, its result would not be
    [InlineData(9.5)] // both would be repeats
    public void Translating_the_screen_again_soon_after_shows_its_progress_and_its_result_again(double secondsLater)
    {
        var h = new Harness();
        void Press()
        {
            h.Notifier.Progress("Translating screen…", null, 30);
            h.Clock.Advance(TimeSpan.FromSeconds(0.6));
            h.Notifier.Feedback(NoticeCategory.Warning, "No text found in the game window");
            h.Notifier.EndProgress();
        }

        Press();
        h.Clock.Advance(TimeSpan.FromSeconds(secondsLater - 0.6));
        Press();

        Assert.Equal(["Translating screen…", "No text found in the game window", "Translating screen…", "No text found in the game window"], h.Texts);
        Assert.Equal(0, h.Surface.ToastsHidden); // the result replaced the progress notice each time
    }

    [Fact]
    public void A_progress_notice_goes_away_when_its_result_shows_nothing_and_stays_replaced_when_another_notice_came()
    {
        var h = new Harness();
        h.Settings.Warning.Enabled = false;

        Assert.True(h.Notifier.Progress("Translating screen…", null, 30));
        Assert.False(h.Notifier.Feedback(NoticeCategory.Warning, "No text found in the game window"));
        h.Notifier.EndProgress();
        h.Notifier.EndProgress();
        Assert.Equal(1, h.Surface.ToastsHidden);

        h.Notifier.Progress("Translating screen…", null, 30);
        h.Notifier.Notify(NoticeCategory.Error, "A subtitle line could not be translated"); // meanwhile, from the subtitles
        h.Notifier.EndProgress();

        Assert.Equal(1, h.Surface.ToastsHidden);
        Assert.Equal([30.0, 30.0, 6.0], h.Surface.Toasts.Select(t => t.Seconds));
    }

    [Fact]
    public void Mute_hides_everything_but_errors_and_every_sound()
    {
        var h = new Harness();
        h.Settings.Sound = true;
        h.Notifier.Muted = true;

        h.Notifier.Notify(NoticeCategory.Status, "Subtitles ON");
        h.Notifier.Feedback(NoticeCategory.Status, "Subtitles ON");
        h.Notifier.Progress("Translating screen…", null, 30);
        h.Notifier.Notify(NoticeCategory.Warning, "Region cut off");
        h.Notifier.Notify(NoticeCategory.Quota, "MyMemory: 80%");
        h.Notifier.Notify(NoticeCategory.Error, "Settings could not be saved");
        h.Notifier.StartupBalloon("SLiveTranslator is running", "keys");

        Assert.True(h.Settings.Muted); // kept in the settings
        Assert.Equal(["Settings could not be saved"], h.Texts);
        Assert.Empty(h.Surface.Sounds);
        Assert.Empty(h.Surface.Balloons);

        h.Notifier.Muted = false;
        h.Notifier.Notify(NoticeCategory.Status, "Subtitles ON");
        Assert.Equal(["Settings could not be saved", "Subtitles ON"], h.Texts);
    }

    [Fact]
    public void Sounds_follow_the_category_and_never_come_closer_than_two_seconds()
    {
        var h = new Harness();
        h.Settings.Sound = true;

        h.Notifier.Notify(NoticeCategory.Status, "a");
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Notifier.Notify(NoticeCategory.Error, "b"); // shown, but too soon for a sound
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Notifier.Notify(NoticeCategory.Error, "c");
        h.Clock.Advance(Notifier.SoundGap);
        h.Notifier.Notify(NoticeCategory.Warning, "d");
        h.Clock.Advance(Notifier.SoundGap);
        h.Notifier.Notify(NoticeCategory.Quota, "e");

        Assert.Equal(5, h.Surface.Toasts.Count);
        Assert.Equal([NoticeSound.Asterisk, NoticeSound.Hand, NoticeSound.Exclamation, NoticeSound.Exclamation], h.Surface.Sounds);
    }

    [Fact]
    public void Near_the_region_goes_to_the_subtitle_region_when_one_is_known_and_else_to_the_top_centre()
    {
        var h = new Harness();
        h.Settings.Position = NoticePosition.NearRegion;

        h.Notifier.Notify(NoticeCategory.Status, "first");
        h.KnownRegion = Region;
        h.Notifier.Notify(NoticeCategory.Status, "second");

        Assert.Equal((NoticePosition.TopCenter, (DRect?)null), (h.Surface.Toasts[0].Position, h.Surface.Toasts[0].Region));
        Assert.Equal((NoticePosition.NearRegion, (DRect?)Region), (h.Surface.Toasts[1].Position, h.Surface.Toasts[1].Region));
    }

    [Fact]
    public void Before_the_overlay_exists_only_errors_reach_the_tray_and_only_when_balloons_are_on()
    {
        var h = new Harness { Surface = { HasOverlay = false } };

        h.Notifier.Notify(NoticeCategory.Warning, "Region cut off");
        h.Notifier.Notify(NoticeCategory.Error, "Settings could not be saved");
        h.Settings.TrayBalloons = false;
        h.Notifier.Notify(NoticeCategory.Error, "Something else failed");
        h.Notifier.StartupBalloon("SLiveTranslator is running", "keys");

        Assert.Empty(h.Surface.Toasts);
        Assert.Equal([("SLiveTranslator", "Settings could not be saved", true)], h.Surface.Balloons);
    }

    [Fact]
    public void The_startup_balloon_shows_when_tray_balloons_are_on()
    {
        var h = new Harness();

        h.Notifier.StartupBalloon("SLiveTranslator is running", "Ctrl+Shift+S: subtitles");

        Assert.Equal([("SLiveTranslator is running", "Ctrl+Shift+S: subtitles", false)], h.Surface.Balloons);
    }

    [Fact]
    public void The_live_badge_shows_only_when_switched_on()
    {
        var h = new Harness();

        h.Notifier.ShowLiveBadge(Region, "Subtitles on");
        h.Settings.LiveBadge = false;
        h.Notifier.ShowLiveBadge(Region, "Subtitles on");
        h.Notifier.HideLiveBadge();

        Assert.Equal(["show: Subtitles on", "hide", "hide"], h.Surface.Badge);
    }

    [Fact]
    public void A_long_text_stays_long_enough_to_read_and_a_progress_notice_keeps_its_own_seconds()
    {
        var h = new Harness();
        h.Settings.Error.Seconds = 20;
        const string ready = "SLiveTranslator ready  ·  Ctrl+Shift+S: subtitles   Ctrl+Shift+T: translate screen   Ctrl+Shift+H: history";
        const string problems = "settings.json is damaged (unexpected end of data); defaults are in use. The file was kept as settings.json.corrupt.\n"
                                + "Some hotkeys do not work. Change them in Settings:\nCtrl+Shift+T (Translate screen): used by another program";

        h.Notifier.Feedback(NoticeCategory.Status, ready);
        h.Notifier.Feedback(NoticeCategory.Warning, problems);
        h.Notifier.Feedback(NoticeCategory.Status, "Subtitles OFF");
        h.Notifier.Feedback(NoticeCategory.Error, "OCR is not available.");
        h.Notifier.Notify(NoticeCategory.Quota, new string('x', 600));
        h.Notifier.Progress("Translating screen…", null, 30);

        // 1 s and 15 characters a second, when that is longer than the category's seconds; 30 s at most.
        Assert.Equal([1 + ready.Length / 15.0, 1 + problems.Length / 15.0, 2.5, 20, 30, 30], h.Surface.Toasts.Select(t => t.Seconds));
    }

    [Fact]
    public async Task The_test_shows_each_enabled_category_a_sound_apart_ignoring_mute_and_repeats()
    {
        var h = new Harness();
        h.Settings.Sound = true;
        h.Settings.Quota.Enabled = false;
        h.Notifier.Muted = true;

        await h.Notifier.SendTestAsync();
        await h.Notifier.SendTestAsync();

        string status = Loc.Instance["Notice.Test.Status"], warning = Loc.Instance["Notice.Test.Warning"], error = Loc.Instance["Notice.Test.Error"];
        Assert.Equal([status, warning, error, status, warning, error], h.Texts);
        Assert.Equal([StatusKind.Info, StatusKind.Warning, StatusKind.Error], h.Surface.Toasts.Take(3).Select(t => t.Kind));
        Assert.Equal([2.5, 8, 6], h.Surface.Toasts.Take(3).Select(t => t.Seconds)); // the user's seconds, as set
        Assert.Equal([NoticeSound.Asterisk, NoticeSound.Exclamation, NoticeSound.Hand], h.Surface.Sounds.Take(3));
        Assert.All(h.Waits.Take(2), wait => Assert.Equal(Notifier.SoundGap, wait));
    }

    [Fact]
    public async Task The_test_plays_every_sound_even_when_its_wait_ends_a_timer_tick_early()
    {
        var surface = new FakeNoticeSurface();
        var settings = new NotificationSettings { Sound = true };
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var notifier = new Notifier(surface, () => settings, clock: clock, delay: wait =>
        {
            clock.Advance(wait - TimeSpan.FromMilliseconds(16)); // Windows timers run on a 15.6 ms tick
            return Task.CompletedTask;
        });

        await notifier.SendTestAsync();

        Assert.Equal([NoticeSound.Asterisk, NoticeSound.Exclamation, NoticeSound.Hand, NoticeSound.Exclamation], surface.Sounds);
    }
}
