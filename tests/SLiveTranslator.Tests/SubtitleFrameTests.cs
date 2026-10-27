using System;
using System.Linq;
using System.Threading.Tasks;
using SLiveTranslator.Diagnostics;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using Xunit;
using Xunit.Abstractions;

namespace SLiveTranslator.Tests;

/// <summary>
/// Plays scripted Witcher-style conversations through <see cref="SubtitleWatcher.Session.ProcessFrameAsync"/>:
/// frames drawn in memory (white subtitles with a dark shadow over a moving or a still scene, as wide as a 2560 px
/// monitor) read by the real Windows OCR. Measures how many lines and words come out, how many come out twice, and
/// how much OCR it took.
/// </summary>
[Trait("Category", "Frames")]
public class SubtitleFrameTests(ITestOutputHelper output)
{
    async Task<ReplayOutcome> Play(SubtitleScript script, bool still, int stillScene = 0)
    {
        var ocr = WindowsOcrEngine.Create("en-US");
        // SLT_FRAMES_DUMP=1 prints every frame's reading, to see where a word was lost.
        Action<string>? trace = Environment.GetEnvironmentVariable("SLT_FRAMES_DUMP") == "1" ? output.WriteLine : null;
        var o = await SubtitleReplay.RunAsync(script, still, ocr, trace, stillScene);

        int wider;
        using (var painter = new SubtitlePainter())
            wider = script.Shots.Count(shot => shot is not null && painter.Measure(shot.Expected, shot.Size) > SubtitleReplay.Region.Width);
        var c = o.Cost;
        output.WriteLine($"{(still ? $"still scene {stillScene}" : "moving scene")}: lines {o.Caught}/{o.Lines} ({o.LineRate:P1}), words {o.Found}/{o.Words} ({o.WordRate:P1}), " +
                         $"extra words {o.ExtraWords}, extra lines {o.Unmatched.Count}, {wider} lines wider than the region");
        output.WriteLine($"frames {c.Frames}, OCR passes {c.ChangedFrames} ({(double)c.ChangedFrames / c.Frames:F2} per frame), " +
                         $"{c.RecognitionsPerChangedFrame:F2} recognitions per pass, {c.MsPerChangedFrame:F1} ms per pass, " +
                         $"{c.MsPerUnchangedFrame:F1} ms per unchanged frame, {c.UnchangedReadsPerSecond:F2} passes/s while a line stays, " +
                         $"{c.BytesPerFrame / 1024} KB allocated per frame");
        foreach (var line in o.Report) output.WriteLine(line);
        return o;
    }

    static void AssertComplete(ReplayOutcome o)
    {
        Assert.True(o.LineRate >= 0.98, $"caught {o.Caught} of {o.Lines} lines");
        Assert.True(o.WordRate >= 0.97, $"found {o.Found} of {o.Words} words");
        Assert.True(o.ExtraWords <= Math.Max(2, o.Words / 50), $"{o.ExtraWords} words that the subtitles do not have");
        Assert.True(o.Unmatched.Count <= Math.Max(1, o.Lines / 60), $"{o.Unmatched.Count} extra lines: {string.Join(" / ", o.Unmatched)}");
        Assert.DoesNotContain(o.Reported, l => l.Contains("Skip", StringComparison.Ordinal));
        Assert.Empty(o.Notices);
    }

    [Fact]
    public async Task A_scripted_conversation_comes_through_with_every_line_and_word()
    {
        var script = SubtitleScript.Conversation();
        using (var painter = new SubtitlePainter())
        {
            int wider = script.Shots.Count(shot => shot is not null && painter.Measure(shot.Expected, shot.Size) > SubtitleReplay.Region.Width);
            Assert.True(wider >= 5, $"only {wider} lines are wider than the region");
        }

        var o = await Play(script, still: false);

        AssertComplete(o);
        // The target is at most 1.3 OCR calls per changed frame, against 2 before. The camera pans, so every frame
        // changes: M1 recognized each one twice. The mask skips frames whose text did not change, and once one image
        // keeps winning only it is recognized. Measured: 0.60 (161 recognitions in 268 frames); the bound is just
        // above, so a drift shows.
        double perFrame = (double)o.Cost.Recognitions / o.Cost.Frames;
        Assert.True(perFrame <= 0.65, $"{perFrame:F2} recognitions per frame");
        // Per frame that was read it is higher, as each new line is read both ways and the lines here stay about a
        // second. It is not the cost: reading fewer frames raises it. Measured: 1.32 (122 passes); a guard against drift.
        Assert.True(o.Cost.RecognitionsPerChangedFrame <= 1.35, $"{o.Cost.RecognitionsPerChangedFrame:F2} recognitions per OCR pass");
    }

    [Fact]
    public async Task On_a_still_scene_short_answers_and_swapped_lines_are_all_read()
    {
        // Most frames look unchanged here, so the watcher reuses its last reading; it must still notice every new line.
        AssertComplete(await Play(SubtitleScript.StillConversation(), still: true));
    }

    [Fact]
    public async Task Over_a_still_bright_scene_the_conversation_comes_through_without_scene_noise()
    {
        // Pale sky and bright patches as light as the text: the mask keeps some of them, and once it is the only image
        // read (a still camera gives it time to win five times), that noise must not pass for a line growing.
        AssertComplete(await Play(SubtitleScript.Conversation(), still: true, SubtitlePainter.BrightScene));
    }

    [Theory]
    [InlineData(28f)]
    [InlineData(30f)]
    public async Task A_long_two_line_sentence_on_a_still_scene_is_read_whole(float size)
    {
        // Reported read as "•tillA'long and cpmplicated story, He We haven't..." at 30 px; kept as a guard for both sizes.
        var script = new SubtitleScript()
            .Say("Did anyone see where it nests?", size: size)
            .Say("A long and complicated story, one we haven't the time for now,\nso I'll tell it to you on the road.", size: size)
            .Gap();

        var o = await Play(script, still: true);

        Assert.Equal(2, o.Caught);
        Assert.Equal(o.Words, o.Found);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_line_that_stays_on_screen_is_read_less_than_once_a_second(bool still)
    {
        // The lines of a conversation stay about a second, too short to measure the steady state.
        var o = await Play(SubtitleScript.Hold(), still);

        Assert.Equal(1, o.Caught);
        Assert.True(o.Cost.UnchangedReadsPerSecond < 1.0, $"{o.Cost.UnchangedReadsPerSecond:F2} OCR passes per second while the line stays");
        // On the still scene the plain frame reads "You.p,VLéfrijSéd Ciri..." every time; the clean mask reading must win.
        Assert.True(o.Found == o.Words, string.Join("\n", o.Report));
    }
}
