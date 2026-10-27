using System.Collections.Generic;
using System.Drawing;
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
    public void Ocr_noise_from_the_game_scene_is_dropped()
    {
        Assert.False(SubtitleWatcher.IsReadable(new OcrLine("-,'", Rectangle.Empty)));
        Assert.False(SubtitleWatcher.IsReadable(new OcrLine("0 1 ||", Rectangle.Empty)));
        Assert.False(SubtitleWatcher.IsReadable(new OcrLine("e", Rectangle.Empty)));
        Assert.True(SubtitleWatcher.IsReadable(new OcrLine("Geralt: Where is it?", Rectangle.Empty)));
        Assert.True(SubtitleWatcher.IsReadable(new OcrLine("狮鹫在哪里？", Rectangle.Empty)));
    }
}
