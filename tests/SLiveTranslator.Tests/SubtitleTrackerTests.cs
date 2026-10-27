using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using Xunit;

namespace SLiveTranslator.Tests;

public class SubtitleTrackerTests
{
    /// <summary>Feeds one frame every 150 ms and collects the events.</summary>
    sealed class Clock(SubtitleTracker tracker)
    {
        long _now;
        public List<SubtitleTracker.Event> Events { get; } = new();

        public void Feed(params string[] frames)
        {
            foreach (var frame in frames)
            {
                _now += 150;
                if (tracker.Observe(frame, _now) is { } e) Events.Add(e);
            }
        }

        public void Wait(long ms) => _now += ms;
    }

    [Fact]
    public void A_line_is_reported_on_its_second_reading_even_with_ocr_noise()
    {
        var tracker = new SubtitleTracker();

        Assert.Null(tracker.Observe("Where is the griffin?", 0));
        Assert.True(tracker.AwaitingConfirmation);
        var line = Assert.IsType<SubtitleTracker.NewLine>(tracker.Observe("Where is the griffln?", 150)); // one letter misread
        Assert.Equal(1, line.Id);
        Assert.Null(tracker.Observe("Where is the griffin?", 300));
    }

    [Fact]
    public void Real_misreadings_of_one_line_from_a_cutscene_are_reported_once()
    {
        // OCR readings of a single Witcher 3 subtitle over a moving scene, taken from a real session.
        var clock = new Clock(new SubtitleTracker());
        foreach (var reading in new[]
        {
            "Ing our young damsel to the lowercourtyard. Sheyants topractice? She'll getto practice.",
            "Bring our young damsel to the lower courtyard. She wants to practice? She'll getto practice.",
            "ing ouqyoungdamsel to the lower courtyårdsShe wants to practice? She'll geryo practice.",
            "-J uams@Utothe lowéncourtyard. She wants to practice'She'll getto practice:——",
            "Ing ougyoung damsel to the lower courtyard. She wants to practice? She'll get to practice,—•",
            "ing our young damsel to the lowercoyrtyaicr She Wants to practice? She'll getto practice.",
            "ing our young damsel to the lower courtyard. Shéewants to practice? She'll-get to practice.",
            "ing.ourgyoung\"damsel to the lower courtyard.dShe wantsto practice? She'll get to practice.",
            "ing our young damsel to the lower courtyard. She$/ahts to practice? She!ll get to practice.",
        })
        {
            clock.Feed(reading, reading);
        }

        var line = Assert.IsType<SubtitleTracker.NewLine>(Assert.Single(clock.Events, e => e is SubtitleTracker.NewLine));
        Assert.Equal(1, line.Id);
        Assert.All(clock.Events, e => Assert.True(e is SubtitleTracker.NewLine or SubtitleTracker.LineUpdated { Id: 1 }));
    }

    [Fact]
    public void A_line_that_flickers_off_for_a_moment_is_not_reported_twice()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed("You promised Ciri you'd train with her.", "You promised Ciri you'd train with her.");
        clock.Feed("", "", "", ""); // bright frame: OCR read nothing
        clock.Feed("You promised Cirl you'd train with her.", "You promised Ciri you'd train with her.");

        Assert.Equal(
        [
            new SubtitleTracker.NewLine(1, "You promised Ciri you'd train with her."),
            new SubtitleTracker.Cleared(),
        ], clock.Events);
    }

    [Fact]
    public void Back_to_back_dialog_without_gaps_reports_every_line_in_order()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed(
            "Geralt: Where is the griffin?", "Geralt: Where is the griffin?",
            "Peasant: Out by the old mill, sir.", "Peasant: Out by the old mill, sir.", "Peasant: Out by the old mill, sir.",
            "Geralt: How many has it taken?", "Geralt: How many has it taken?",
            "Peasant: Three cows and the miller's boy.", "Peasant: Three cows and the miller's boy.",
            "Wrong.", "Wrong.",
            "Enough. Get down.", "Enough. Get down.");

        Assert.Equal(
        [
            new SubtitleTracker.NewLine(1, "Geralt: Where is the griffin?"),
            new SubtitleTracker.NewLine(2, "Peasant: Out by the old mill, sir."),
            new SubtitleTracker.NewLine(3, "Geralt: How many has it taken?"),
            new SubtitleTracker.NewLine(4, "Peasant: Three cows and the miller's boy."),
            new SubtitleTracker.NewLine(5, "Wrong."),
            new SubtitleTracker.NewLine(6, "Enough. Get down."),
        ], clock.Events);
    }

    [Fact]
    public void Fast_typewriter_reveal_reports_only_the_full_line()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed("Geralt: The", "Geralt: The griffin", "Geralt: The griffin has been", "Geralt: The griffin has been hunting here.",
            "Geralt: The griffin has been hunting here.");

        Assert.Equal([new SubtitleTracker.NewLine(1, "Geralt: The griffin has been hunting here.")], clock.Events);
    }

    [Fact]
    public void Slow_typewriter_reveal_updates_the_same_line_instead_of_adding_new_ones()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed(
            "Geralt: The griffin", "Geralt: The griffin",
            "Geralt: The griffin has been hunting", "Geralt: The griffin has been hunting",
            "Geralt: The griffin has been hunting here for weeks.", "Geralt: The griffin has been hunting here for weeks.");

        Assert.Equal(
        [
            new SubtitleTracker.NewLine(1, "Geralt: The griffin"),
            new SubtitleTracker.LineUpdated(1, "Geralt: The griffin has been hunting"),
            new SubtitleTracker.LineUpdated(1, "Geralt: The griffin has been hunting here for weeks."),
        ], clock.Events);
    }

    [Fact]
    public void A_line_repeated_later_in_the_conversation_is_reported_again()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed("Yes.", "Yes.", "", "", "");
        clock.Wait(15_000);
        clock.Feed("Yes.", "Yes.");

        Assert.Equal(
        [
            new SubtitleTracker.NewLine(1, "Yes."),
            new SubtitleTracker.Cleared(),
            new SubtitleTracker.NewLine(2, "Yes."),
        ], clock.Events);
    }

    [Fact]
    public void A_garbled_reading_is_held_back_until_a_clean_one_arrives()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed("-J uams@Utothe lowén$ourtyard. Sh@ wan/s to pr4ctice'She'll", "-J uams@Utothe lowén$ourtyard. Sh@ wan/s to pr4ctice'She'll");
        Assert.Empty(clock.Events);
        clock.Feed("Bring our young damsel to the lower courtyard. She wants to practice?", "Bring our young damsel to the lower courtyard. She wants to practice?");

        Assert.Equal([new SubtitleTracker.NewLine(1, "Bring our young damsel to the lower courtyard. She wants to practice?")], clock.Events);
    }

    [Fact]
    public void A_few_letters_read_late_update_the_shown_line()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed("Well, yes, but... that book was horribly", "Well, yes, but... that book was horribly",
            "Well, yes, but... that book was horribly dull!", "Well, yes, but... that book was horribly dull!");

        Assert.Equal(
        [
            new SubtitleTracker.NewLine(1, "Well, yes, but... that book was horribly"),
            new SubtitleTracker.LineUpdated(1, "Well, yes, but... that book was horribly dull!"),
        ], clock.Events);
    }

    [Fact]
    public void A_word_missing_from_a_long_line_is_restored()
    {
        const string full = "Go and train with her. Then come back, it'll give me a chance to put my lovely face on.";
        const string missing = "Go and train with her. Then come back, it'll give me a chance to put my face on.";
        var clock = new Clock(new SubtitleTracker());

        clock.Feed(missing, missing, full, missing, full, missing);

        Assert.Equal([new SubtitleTracker.NewLine(1, missing), new SubtitleTracker.LineUpdated(1, full)], clock.Events);
    }

    [Fact]
    public void A_reading_with_fewer_letters_or_more_junk_does_not_replace_the_shown_line()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed("Bring our young damsel to the lower courtyard.", "Bring our young damsel to the lower courtyard.",
            "Bring our young damsel to the lowercourtyard.", "Bring our young damsel to the lowercourtyard.",
            "Bring our young damsel to the lower courtyard. @@ $$", "Bring our young damsel to the lower courtyard. @@ $$ nn");

        Assert.Equal([new SubtitleTracker.NewLine(1, "Bring our young damsel to the lower courtyard.")], clock.Events);
    }

    [Fact]
    public void Alternating_readings_of_one_subtitle_are_confirmed_once()
    {
        // The second line of the subtitle is read on every other frame only.
        const string first = "Go and train with her. Then come back.";
        const string both = "Go and train with her. Then come back.\nIt'll give me a chance to put my face on.";
        var clock = new Clock(new SubtitleTracker());

        clock.Feed(first, both, first, both, first, both, first, both);

        Assert.Equal([new SubtitleTracker.NewLine(1, first), new SubtitleTracker.LineUpdated(1, both)], clock.Events);
    }

    [Fact]
    public void Alternating_readings_that_start_with_the_full_subtitle_are_confirmed_once()
    {
        // Which reading OCR returns first is chance: here the full subtitle comes first, then the first line alone.
        const string first = "Go and train with her. Then come back.";
        const string both = "Go and train with her. Then come back.\nIt'll give me a chance to put my face on.";
        var clock = new Clock(new SubtitleTracker());

        clock.Feed(both, first, both, first, both, first, both, first);

        Assert.Equal([new SubtitleTracker.NewLine(1, both)], clock.Events);
    }

    [Fact]
    public void An_unchanged_frame_does_not_vote_for_a_one_off_misreading()
    {
        // OCR read the line cleanly once and misread it once; the misread frame then stayed unchanged, so the caller
        // passed that reading again without running OCR. It is still one misreading, not three.
        var tracker = new SubtitleTracker();
        var events = new List<SubtitleTracker.Event?>
        {
            tracker.Observe("Where is the griffin, Geralt?", 150),
            tracker.Observe("Where is the griffln, Geralt?", 300),
            tracker.Observe("Where is the griffln, Geralt?", 450, fresh: false),
            tracker.Observe("Where is the griffln, Geralt?", 600, fresh: false),
        };

        Assert.Equal(new SubtitleTracker.Event?[] { null, new SubtitleTracker.NewLine(1, "Where is the griffin, Geralt?"), null, null }, events);
    }

    [Fact]
    public void A_garbled_line_on_a_still_scene_is_still_force_shown()
    {
        // The scene does not move, so OCR ran once; the reading staying on screen is what counts.
        const string garbled = "Wh@t d0 y0u w@nt from m3, witcher? Sp3ak.";
        var tracker = new SubtitleTracker();

        Assert.Null(tracker.Observe(garbled, 150));
        Assert.Null(tracker.Observe(garbled, 250, fresh: false));
        Assert.Null(tracker.Observe(garbled, 550, fresh: false));
        var shown = Assert.IsType<SubtitleTracker.NewLine>(tracker.Observe(garbled, 850, fresh: false));
        Assert.True(shown.LowConfidence);
    }

    [Fact]
    public void A_line_that_never_reads_cleanly_is_shown_with_low_confidence_after_four_frames()
    {
        const string garbled = "Wh@t d0 y0u w@nt from m3, witcher? Sp3ak.";
        const string clean = "What do you want from me, witcher? Speak.";
        var clock = new Clock(new SubtitleTracker());

        clock.Feed(garbled, garbled, garbled);
        Assert.Empty(clock.Events);
        clock.Feed(garbled);
        var shown = Assert.IsType<SubtitleTracker.NewLine>(Assert.Single(clock.Events));
        Assert.True(shown.LowConfidence);

        clock.Feed(clean, clean);
        Assert.Equal(new SubtitleTracker.LineUpdated(1, clean), clock.Events[^1]); // replaces it, no score margin needed
        Assert.False(((SubtitleTracker.LineUpdated)clock.Events[^1]).LowConfidence);
    }

    [Fact]
    public void Scene_noise_is_not_force_shown()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed("|| 0 1/ ,- @#", "|| 0 1/ ,- @#", "|| 0 1/ ,- @#", "|| 0 1/ ,- @#", "|| 0 1/ ,- @#", "|| 0 1/ ,- @#");

        Assert.Empty(clock.Events);
    }

    [Theory]
    [InlineData("rn 1l ~ lI")]
    [InlineData("ffl 1i ~ wv")]
    [InlineData("Ww 1i | rn")]
    [InlineData("l1 rn ii u")]
    public void Letter_heavy_noise_from_a_still_scene_is_not_force_shown(string noise)
    {
        // Each of these passes the per-line check (mostly letters) and a still scene reads it the same every frame.
        Assert.True(SubtitleWatcher.IsReadable(new OcrLine(noise, Rectangle.Empty)));
        var clock = new Clock(new SubtitleTracker());

        clock.Feed(noise, noise, noise, noise, noise, noise);

        Assert.Empty(clock.Events);
    }

    [Fact]
    public void A_line_repeated_a_few_seconds_after_the_subtitle_cleared_is_reported_again()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed("Yes.", "Yes.", "", "", "");
        clock.Wait(3_000);
        clock.Feed("Yes.", "Yes.");

        Assert.Equal(
        [
            new SubtitleTracker.NewLine(1, "Yes."),
            new SubtitleTracker.Cleared(),
            new SubtitleTracker.NewLine(2, "Yes."),
        ], clock.Events);
    }

    [Fact]
    public void Without_a_clear_a_repeated_reading_within_the_window_is_the_same_line()
    {
        var clock = new Clock(new SubtitleTracker());

        clock.Feed("Yes.", "Yes.", "No.", "No.");
        clock.Wait(3_000);
        clock.Feed("Yes.", "Yes.");

        Assert.Equal([new SubtitleTracker.NewLine(1, "Yes."), new SubtitleTracker.NewLine(2, "No.")], clock.Events);
    }

    [Fact]
    public void Ocr_noise_from_the_game_scene_is_dropped()
    {
        Assert.False(SubtitleWatcher.IsReadable(new OcrLine("-,'", Rectangle.Empty)));
        Assert.False(SubtitleWatcher.IsReadable(new OcrLine("0 1 ||", Rectangle.Empty)));
        Assert.False(SubtitleWatcher.IsReadable(new OcrLine("e", Rectangle.Empty)));
        Assert.True(SubtitleWatcher.IsReadable(new OcrLine("Geralt: Where is it?", Rectangle.Empty)));
        Assert.True(SubtitleWatcher.IsReadable(new OcrLine("狮鹫在哪里？", Rectangle.Empty)));
    }

    [Fact]
    public void Leaving_clears_a_shown_line_and_resets_the_readings()
    {
        var tracker = new SubtitleTracker();
        tracker.Observe("Where is the griffin?", 0);
        tracker.Observe("Where is the griffin?", 150);

        Assert.IsType<SubtitleTracker.Cleared>(tracker.Leave(300));
        Assert.Null(tracker.Leave(450)); // nothing shown any more
        Assert.False(tracker.Unsettled);
        Assert.Null(tracker.Observe("Out by the old mill, sir.", 3_500)); // a fresh start: needs its confirming frame again
        Assert.IsType<SubtitleTracker.NewLine>(tracker.Observe("Out by the old mill, sir.", 3_650));
    }

    [Fact]
    public void A_line_still_on_screen_after_leaving_stays_the_same_line_for_the_usual_twelve_seconds()
    {
        // Leaving is not the game clearing the line: the game may pause with it on screen while the player is away.
        var tracker = new SubtitleTracker();
        tracker.Observe("Where is the griffin?", 0);
        tracker.Observe("Where is the griffin?", 150);
        tracker.Leave(10_000);

        Assert.Null(tracker.Observe("Where is the griffin?", 15_000));
        Assert.Null(tracker.Observe("Where is the griffin?", 15_150)); // five seconds away, 15 s after it was first read

        var later = new SubtitleTracker();
        later.Observe("Where is the griffin?", 0);
        later.Observe("Where is the griffin?", 150);
        later.Leave(300);
        later.Observe("Where is the griffin?", 12_500);
        Assert.IsType<SubtitleTracker.NewLine>(later.Observe("Where is the griffin?", 12_650)); // away longer: a repeat
    }

    [Fact]
    public void A_line_stays_unsettled_only_for_its_first_few_readings()
    {
        // Readings that keep disagreeing (bright pixels behind the text) rarely settle with more of them.
        var tracker = new SubtitleTracker();
        string[] readings =
        [
            "Where is the griffin, Geralt?", "Where is the griffln, Geralt?", "Wnere is the griffin, Geralt?",
            "Where is the grilfin, Geralt?", "Where is the griffin, Gerait?", "Where is tne griffin, Geralt?",
            "Where ls the griffin, Geralt?",
        ];
        tracker.Observe(readings[0], 0);
        tracker.Observe(readings[1], 150);

        var unsettled = readings.Skip(2).Select((r, i) =>
        {
            tracker.Observe(r, 300 + i * 150);
            return tracker.Unsettled;
        }).ToList();

        Assert.Equal([true, true, true, false, false], unsettled);
    }

    [Fact]
    public void A_line_is_unsettled_until_two_fresh_readings_agree_exactly()
    {
        var tracker = new SubtitleTracker();
        tracker.Observe("Eskers gone to the mountains again, hasn't he?", 0);
        Assert.True(tracker.Unsettled); // waits for its confirming frame
        Assert.IsType<SubtitleTracker.NewLine>(tracker.Observe("Eskells gone to the mountains again, hasn't he?", 150));
        Assert.True(tracker.Unsettled); // shown, but the readings disagree
        tracker.Observe("Eskells gone to the mountains again, hasn't he?", 300, fresh: false);
        Assert.True(tracker.Unsettled); // an unchanged frame is no new reading

        tracker.Observe("Eskells gone to the mountains again, hasn't he?", 450);

        Assert.False(tracker.Unsettled);
    }
}
