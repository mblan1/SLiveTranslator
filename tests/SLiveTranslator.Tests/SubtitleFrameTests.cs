using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using Xunit;
using Xunit.Abstractions;

namespace SLiveTranslator.Tests;

/// <summary>
/// Plays scripted Witcher-style conversations through <see cref="SubtitleWatcher.Session.ProcessFrameAsync"/>:
/// frames drawn in memory (white subtitles with a dark shadow over a moving or a still scene, as wide as a 2560 px
/// monitor) read by the real Windows OCR. Measures how many lines and words come out, and how many come out twice.
/// </summary>
[Trait("Category", "Frames")]
public class SubtitleFrameTests(ITestOutputHelper output)
{
    const int Width = 2560, Height = 212, CentreX = Width / 2;
    const int IntervalMs = 300;

    // The user's region is narrower than many subtitle lines; the watcher reads the whole monitor width.
    static readonly Rectangle Capture = new(0, 1094, Width, Height);
    static readonly Rectangle Region = new(880, 1094, 800, Height);

    /// <summary>One subtitle as the game shows it. Frames are the readings shown in turn, each for one poll.</summary>
    sealed record Shot(string Expected, List<string> Frames, float Size);

    sealed class Script
    {
        static readonly float[] Sizes = [28f, 25f, 32f, 30f, 26f, 29f];
        public List<Shot?> Shots { get; } = new(); // null = no subtitle for a poll
        public Dictionary<int, int> WaitBefore { get; } = new(); // shot index → extra silent time

        float NextSize() => Sizes[Shots.Count(s => s is not null) % Sizes.Length];

        public Script Say(string text, int frames = 5)
        {
            Shots.Add(new Shot(text, Enumerable.Repeat(text, frames).ToList(), NextSize()));
            return this;
        }

        /// <summary>Words appear a few at a time, then the full line stays.</summary>
        public Script Typewriter(string text, int steps = 3, int fullFrames = 4)
        {
            var words = text.Split(' ');
            var frames = Enumerable.Range(1, steps).Select(i => string.Join(" ", words.Take(Math.Max(1, words.Length * i / (steps + 1))))).ToList();
            frames.AddRange(Enumerable.Repeat(text, fullFrames));
            Shots.Add(new Shot(text, frames, NextSize()));
            return this;
        }

        /// <summary>The line is shown, then a last word is added to it.</summary>
        public Script Append(string text, string tail, int frames = 4)
        {
            var frameTexts = Enumerable.Repeat(text, frames).Concat(Enumerable.Repeat(text + tail, frames)).ToList();
            Shots.Add(new Shot(text + tail, frameTexts, NextSize()));
            return this;
        }

        public Script Gap(int polls = 4, int extraMs = 0)
        {
            for (int i = 0; i < polls; i++) Shots.Add(null);
            if (extraMs > 0) WaitBefore[Shots.Count] = extraMs;
            return this;
        }
    }

    static Script Conversation() => new Script()
        .Typewriter("Geralt: The griffin has been hunting near the old mill for weeks.")
        .Say("Three cows and the miller's boy, sir. Gone, all of them.")
        .Say("Did anyone see where it nests?")
        .Say("Yes.")
        .Say("No.")
        .Say("Well, make up your mind, man.")
        .Append("Well, yes, but... that book was horribly", " dull!")
        .Say("You promised Ciri you'd train with her, and a witcher keeps his word even when the weather turns foul.")
        .Say("Won't you stay a while? I'm sure she'd want you to.")
        .Say("You should've asked before you took it. It's not yours.")
        .Gap()
        .Say("They'll come at dawn, and we're not ready.")
        .Say("Go and train with her. Then come back,\nit'll give me a chance to put my face on.")
        .Say("Where is Yennefer?")
        .Gap(4, extraMs: 3_000)
        .Say("Where is Yennefer?")
        .Say("Don't worry, it'll be fine. Ciri's waiting at the gate.")
        .Typewriter("The road to Novigrad is long, and the bandits know every bend of it.")
        .Say("Hmm. So you did read the chapter after all.")
        .Say("Still, you should've asked if you could borrow the book.")
        .Say("Why the hell not? The girl refuses to do as she's told.")
        .Gap()
        .Say("1. I'll stay a little longer.\n2. You're right, I should go see Ciri.")
        .Say("Take a trophy from the griffin's corpse.")
        .Say("Vesemir taught me everything I know about monsters.")
        .Append("We'll leave at first light, before the", " fog lifts.")
        .Say("Ghouls feed on the dead. Necrophages, all of them.")
        .Say("I've never seen a wraith this strong in daylight.")
        .Say("Roach needs new shoes before we ride to Oxenfurt.")
        .Say("That's the second contract this week, and nobody has paid me yet.")
        .Gap()
        .Say("Who's there? Show yourself!")
        .Say("Easy. I'm only here for the notice board.")
        .Typewriter("The alderman says the beast came from the swamp beyond the crossroads.")
        .Say("Couldn't you have waited until morning?")
        .Say("There's a storm coming. We'd best find shelter.")
        .Say("Lambert, put down the bottle and help me with this.")
        .Say("Eskel's gone to the mountains again, hasn't he?")
        .Gap()
        .Say("It's an old elven ruin, older than the city itself.")
        .Say("If you're lying to me, I'll know.")
        .Say("A long and complicated story, one we haven't the time for now,\nso I'll tell it to you on the road.")
        .Say("What's that smell? Something's rotting under the floor.")
        .Say("Wouldn't be the first time a witcher got paid in onions.")
        .Append("Keep your sword close and your", " silver closer.")
        .Gap()
        .Say("We should've left when we had the chance.")
        .Say("Triss? What are you doing in Velen?")
        .Say("Long story. I'll explain later, I promise.")
        .Say("Thank you, Geralt. I won't forget this.")
        .Say("Farewell, then. And watch your back.")
        .Gap();

    /// <summary>A still camera: lines replace each other with no gap, and short answers change only a few pixels.</summary>
    static Script StillConversation() => new Script()
        .Say("Did anyone see where it nests?")
        .Say("Yes.")
        .Say("No.")
        .Say("Three cows and the miller's boy, sir. Gone, all of them.")
        .Say("Never.")
        .Say("Where is Yennefer?")
        .Say("She left for Vengerberg.")
        .Say("A long and complicated story, one we haven't the time for now,\nso I'll tell it to you on the road.")
        .Say("Fine.")
        .Say("Wouldn't be the first time a witcher got paid in onions.")
        .Say("Hurry!")
        .Say("Then we ride at dawn.")
        // Each pair shares most of its letters; only one word differs.
        .Say("Where is the griffin?")
        .Say("Where is the wyvern?")
        .Say("What do you want?")
        .Say("What do you need?")
        .Gap();

    // ---- drawing ----

    sealed class Painter : IDisposable
    {
        readonly Bitmap[] _scenes;
        readonly Dictionary<float, Font> _fontCache = new();

        public Painter()
        {
            _scenes = [Scene(7, bright: false), Scene(11, bright: true), Scene(13, bright: false)];
        }

        /// <summary>A wide busy texture; frames show a window of it that slides, like a camera pan in a cutscene.</summary>
        static Bitmap Scene(int seed, bool bright)
        {
            var scene = new Bitmap(Width + 800, Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(scene);
            var rnd = new Random(seed);
            var (a, b) = bright
                ? (Color.FromArgb(150, 165, 185), Color.FromArgb(120, 110, 80))
                : (Color.FromArgb(35, 40, 48), Color.FromArgb(85, 70, 55));
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, scene.Width, Height), a, b, 20f))
                g.FillRectangle(bg, 0, 0, scene.Width, Height);
            for (int i = 0; i < 420; i++)
            {
                int v = rnd.Next(bright ? 100 : 20, bright ? 256 : 190);
                using var brush = new SolidBrush(Color.FromArgb(rnd.Next(70, 200), v, Math.Max(0, v - rnd.Next(0, 60)), Math.Max(0, v - rnd.Next(0, 90))));
                g.FillEllipse(brush, rnd.Next(-20, scene.Width), rnd.Next(-20, Height), rnd.Next(6, 90), rnd.Next(4, 46));
            }
            return scene;
        }

        Font FontOf(float size)
        {
            if (!_fontCache.TryGetValue(size, out var font)) _fontCache[size] = font = new Font("Segoe UI", size, GraphicsUnit.Pixel);
            return font;
        }

        /// <summary>Width of the widest line of the text as drawn.</summary>
        public float Measure(string text, float size)
        {
            using var probe = new Bitmap(1, 1);
            using var g = Graphics.FromImage(probe);
            return text.Split('\n').Max(line => g.MeasureString(line, FontOf(size)).Width);
        }

        /// <param name="still">The camera does not move: every frame shows the same part of the first scene.</param>
        public Bitmap Draw(int frame, string? text, float size, bool still)
        {
            var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            var scene = _scenes[still ? 0 : frame / 40 % _scenes.Length];
            int pan = still ? 0 : frame * 11 % 800;
            g.DrawImage(scene, new Rectangle(0, 0, Width, Height), new Rectangle(pan, 0, Width, Height), GraphicsUnit.Pixel);

            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            DrawShadowed(g, "Space", FontOf(22f), 2270, 170);
            DrawShadowed(g, "Skip", FontOf(22f), 2350, 170);
            if (text is null) return bmp;

            var font = FontOf(size);
            var lines = text.Split('\n');
            float lineHeight = size * 1.32f;
            float top = Height - 40 - lines.Length * lineHeight;
            for (int i = 0; i < lines.Length; i++)
            {
                float w = g.MeasureString(lines[i], font).Width;
                DrawShadowed(g, lines[i], font, CentreX - w / 2, top + i * lineHeight);
            }
            return bmp;
        }

        static void DrawShadowed(Graphics g, string text, Font font, float x, float y)
        {
            using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
            foreach (var (dx, dy) in new[] { (1, 1), (2, 2), (-1, 1), (1, -1), (0, 2) }) g.DrawString(text, font, shadow, x + dx, y + dy);
            g.DrawString(text, font, Brushes.White, x, y);
        }

        public void Dispose()
        {
            foreach (var s in _scenes) s.Dispose();
            foreach (var f in _fontCache.Values) f.Dispose();
        }
    }

    // ---- scoring ----

    static List<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Regex.Replace(w, @"[^\p{L}]", "").ToLowerInvariant())
            .Where(w => w.Length > 0)
            .ToList();

    /// <summary>Share of the expected words found in the reading (letters only, each word counted once).</summary>
    static (int Found, int Total) Recall(string expected, string actual)
    {
        var have = Words(actual).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        int found = 0;
        var want = Words(expected);
        foreach (var w in want)
        {
            if (have.TryGetValue(w, out int n) && n > 0)
            {
                found++;
                have[w] = n - 1;
            }
        }
        return (found, want.Count);
    }

    /// <summary>What came out of one conversation.</summary>
    /// <param name="ExtraWords">Words in the caught lines that the subtitle does not have: OCR noise that got through.</param>
    sealed record Outcome(int Lines, int Caught, int Found, int Words, int ExtraWords, List<string> Unmatched, List<string> Reported, RecordingSink Sink)
    {
        public double LineRate => (double)Caught / Lines;
        public double WordRate => (double)Found / Math.Max(1, Words);
    }

    async Task<Outcome> Play(Script script, bool still)
    {
        var expected = script.Shots.Where(s => s is not null).Select(s => s!).ToList();
        var ocr = WindowsOcrEngine.Create("en-US");
        var sink = new RecordingSink();
        var watcher = new SubtitleWatcher(() => ocr, () => new EchoTranslator(), () => ("en", "vi", IntervalMs, 1.3), sink)
        {
            Delay = (_, _) => Task.CompletedTask,
        };
        var session = watcher.CreateSession(Region, Capture);

        using var painter = new Painter();
        int wider = expected.Count(shot => painter.Measure(shot.Expected, shot.Size) > Region.Width);
        long now = 0;
        int frameNo = 0, ocrRuns = 0;
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        for (int s = 0; s < script.Shots.Count; s++)
        {
            if (script.WaitBefore.TryGetValue(s, out int wait)) now += wait;
            var shot = script.Shots[s];
            foreach (var text in shot?.Frames ?? [null!])
            {
                using var frame = painter.Draw(frameNo++, shot is null ? null : text, shot?.Size ?? 28f, still);
                var result = await session.ProcessFrameAsync(frame, now, CancellationToken.None);
                if (result.OcrRan) ocrRuns++;
                // SLT_FRAMES_DUMP=1 prints every frame's reading, to see where a word was lost.
                if (Environment.GetEnvironmentVariable("SLT_FRAMES_DUMP") == "1")
                    output.WriteLine($"#{frameNo - 1} scene{(still ? 0 : (frameNo - 1) / 40 % 3)} {(result.OcrRan ? "ocr" : "same")} [{result.Event}] {result.Reading.Replace("\n", " | ")}");
                now += session.AwaitingConfirmation ? 100 : IntervalMs;
            }
        }
        clock.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        // The final reading of every reported line, in the order the lines appeared.
        var detected = sink.DetectedLines;
        var lines = detected.Select(d => d.Id).Distinct().Select(id => detected.Last(d => d.Id == id).Text).ToList();

        int caught = 0, found = 0, total = 0, extraWords = 0, next = 0;
        var unmatched = new List<string>();
        var report = new List<string>();
        foreach (var shot in expected)
        {
            int match = -1;
            for (int j = next; j < Math.Min(lines.Count, next + 4) && match < 0; j++)
                if (Recall(shot.Expected, lines[j]) is var r && r.Found * 2 >= r.Total) match = j;
            if (match < 0)
            {
                report.Add("MISSED  " + shot.Expected.Replace('\n', ' '));
                continue;
            }
            unmatched.AddRange(lines.Skip(next).Take(match - next));
            next = match + 1;
            caught++;
            var (f, t) = Recall(shot.Expected, lines[match]);
            found += f;
            total += t;
            int extra = Words(lines[match]).Count - f;
            extraWords += extra;
            if (f < t || extra > 0) report.Add($"{f}/{t} +{extra}  {shot.Expected.Replace('\n', ' ')}  =>  {lines[match].Replace("\n", " | ")}");
        }
        unmatched.AddRange(lines.Skip(next));
        report.AddRange(unmatched.Select(u => "EXTRA   " + u.Replace("\n", " | ")));

        var outcome = new Outcome(expected.Count, caught, found, total, extraWords, unmatched, lines, sink);
        output.WriteLine($"{(still ? "still" : "moving")} scene: lines {caught}/{expected.Count} ({outcome.LineRate:P1}), words {found}/{total} ({outcome.WordRate:P1}), " +
                         $"extra words {extraWords}, extra lines {unmatched.Count}, {wider} lines wider than the region");
        output.WriteLine($"frames {frameNo}, OCR passes {ocrRuns} ({(double)ocrRuns / frameNo:F2} per frame, 2 recognitions each), " +
                         $"{clock.Elapsed.TotalMilliseconds / frameNo:F1} ms per frame, {allocated / frameNo / 1024} KB allocated per frame");
        foreach (var line in report) output.WriteLine(line);
        return outcome;
    }

    static void AssertComplete(Outcome o)
    {
        Assert.True(o.LineRate >= 0.98, $"caught {o.Caught} of {o.Lines} lines");
        Assert.True(o.WordRate >= 0.97, $"found {o.Found} of {o.Words} words");
        Assert.True(o.ExtraWords <= Math.Max(2, o.Words / 50), $"{o.ExtraWords} words that the subtitles do not have");
        Assert.True(o.Unmatched.Count <= Math.Max(1, o.Lines / 60), $"{o.Unmatched.Count} extra lines: {string.Join(" / ", o.Unmatched)}");
        Assert.DoesNotContain(o.Reported, l => l.Contains("Skip", StringComparison.Ordinal));
        Assert.Empty(o.Sink.Notices);
    }

    [Fact]
    public async Task A_scripted_conversation_comes_through_with_every_line_and_word()
    {
        var script = Conversation();
        using (var painter = new Painter())
        {
            int wider = script.Shots.Count(shot => shot is not null && painter.Measure(shot.Expected, shot.Size) > Region.Width);
            Assert.True(wider >= 5, $"only {wider} lines are wider than the region");
        }

        AssertComplete(await Play(script, still: false));
    }

    [Fact]
    public async Task On_a_still_scene_short_answers_and_swapped_lines_are_all_read()
    {
        // Most frames look unchanged here, so the watcher reuses its last reading; it must still notice every new line.
        AssertComplete(await Play(StillConversation(), still: true));
    }
}
