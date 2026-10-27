using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Capture;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;

namespace SLiveTranslator.Diagnostics;

/// <summary>
/// Plays a <see cref="SubtitleScript"/> through <see cref="SubtitleWatcher.Session.ProcessFrameAsync"/> with frames
/// drawn in memory and a real OCR engine, and measures what came out (lines and words caught, lines reported twice)
/// and what it cost (recognitions, time and memory per frame). Shared by the frame tests and --bench-subtitles.
/// </summary>
internal static class SubtitleReplay
{
    public const int IntervalMs = 300;

    /// <summary>The capture: a 2560 px wide strip at the bottom of the monitor.</summary>
    public static readonly Rectangle Capture = new(0, 1094, SubtitlePainter.Width, SubtitlePainter.Height);

    /// <summary>The user's region is narrower than many subtitle lines; the watcher reads the whole monitor width.</summary>
    public static readonly Rectangle Region = new(880, 1094, 800, SubtitlePainter.Height);

    /// <summary>Plays the script and scores it.</summary>
    /// <param name="trace">Called with one line per frame (its reading and what the watcher made of it).</param>
    /// <param name="stillScene">The scene a still camera shows (see <see cref="SubtitlePainter.SceneOf"/>).</param>
    public static async Task<ReplayOutcome> RunAsync(SubtitleScript script, bool still, IOcrEngine ocr, Action<string>? trace = null, int stillScene = 0)
    {
        var expected = script.Shots.Where(s => s is not null).Select(s => s!).ToList();
        var sink = new ReplaySink();
        var watcher = new SubtitleWatcher(() => ocr, () => new EchoTranslator(), () => new WatcherConfig("en", "vi", IntervalMs, 1.3), sink)
        {
            Delay = (_, _) => Task.CompletedTask,
        };
        using var session = watcher.CreateSession(Region, Capture);
        using var painter = new SubtitlePainter();
        using var frames = new BitmapBuffer();
        var frame = frames.Get(SubtitlePainter.Width, SubtitlePainter.Height);

        var cost = new ReplayCost();
        long now = 0;
        int frameNo = 0;
        string? previous = null;
        var clock = new Stopwatch();
        for (int s = 0; s < script.Shots.Count; s++)
        {
            if (script.WaitBefore.TryGetValue(s, out int wait)) now += wait;
            var shot = script.Shots[s];
            foreach (var text in shot?.Frames ?? [null!])
            {
                string? shown = shot is null ? null : text;
                painter.Draw(frame, frameNo, shown, shot?.Size ?? 28f, still, stillScene);
                // The frame that confirms a new reading belongs to reading the change, not to the line staying.
                bool unchanged = shown is not null && shown == previous && !session.AwaitingConfirmation;
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                clock.Restart();
                var result = await session.ProcessFrameAsync(frame, now, CancellationToken.None);
                clock.Stop();
                cost.Bytes += GC.GetTotalAllocatedBytes(precise: true) - allocated;
                trace?.Invoke($"#{frameNo} scene{SubtitlePainter.SceneOf(frameNo, still, stillScene)} {(result.OcrRan ? $"ocr{result.Recognitions}" : "same")} [{result.Event}] {result.Reading.Replace("\n", " | ")}");

                int elapsed = session.AwaitingConfirmation ? 100 : IntervalMs;
                cost.Add(result, clock.Elapsed.TotalMilliseconds, unchanged, elapsed);
                previous = shown;
                now += elapsed;
                frameNo++;
            }
        }
        return Score(expected, sink, cost);
    }

    static ReplayOutcome Score(List<SubtitleShot> expected, ReplaySink sink, ReplayCost cost)
    {
        // The final reading of every reported line, in the order the lines appeared.
        var detected = sink.Detected;
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
        return new ReplayOutcome(expected.Count, caught, found, total, extraWords, unmatched, lines, report, sink.Notices, cost);
    }

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

    /// <summary>Records the lines and notices; the translations are not looked at.</summary>
    sealed class ReplaySink : ISubtitleSink
    {
        readonly object _gate = new();
        readonly List<(int Id, string Text)> _detected = new();
        readonly List<string> _notices = new();

        public List<(int Id, string Text)> Detected
        {
            get { lock (_gate) return _detected.ToList(); }
        }

        public List<string> Notices
        {
            get { lock (_gate) return _notices.ToList(); }
        }

        public void LineDetected(int id, string original, bool lowConfidence)
        {
            lock (_gate) _detected.Add((id, original));
        }

        public void Notice(string message)
        {
            lock (_gate) _notices.Add(message);
        }

        public void LineTranslated(int id, string translated, bool lowConfidence, string provider) { }
        public void LineFailed(int id, string original, string error) { }
        public void Cleared() { }
        public void Paused(bool paused) { }
        public void Error(string message) { }
    }

    /// <summary>Answers at once with the source text, so translation costs nothing and never fails.</summary>
    sealed class EchoTranslator : ITranslator
    {
        public string Name => "echo";

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            Task.FromResult(texts);
    }
}

/// <summary>What one replay cost.</summary>
internal sealed class ReplayCost
{
    public int Frames { get; private set; }
    public int ChangedFrames { get; private set; }
    public int Recognitions { get; private set; }
    public double ChangedMs { get; private set; }
    public double UnchangedMs { get; private set; }
    public long Bytes { get; set; }

    /// <summary>
    /// Frames that showed the same subtitle as the frame before, once it was confirmed (the steady state of a line on
    /// screen), and how many of them were read again.
    /// </summary>
    public int UnchangedFrames { get; private set; }
    public int UnchangedReads { get; private set; }
    public double UnchangedSeconds { get; private set; }

    public void Add(SubtitleWatcher.FrameResult result, double ms, bool unchanged, int elapsedMs)
    {
        Frames++;
        Recognitions += result.Recognitions;
        if (result.OcrRan)
        {
            ChangedFrames++;
            ChangedMs += ms;
        }
        else
        {
            UnchangedMs += ms;
        }
        if (!unchanged) return;
        UnchangedFrames++;
        UnchangedSeconds += elapsedMs / 1000.0;
        if (result.OcrRan) UnchangedReads++;
    }

    public double RecognitionsPerChangedFrame => (double)Recognitions / Math.Max(1, ChangedFrames);
    public double MsPerChangedFrame => ChangedMs / Math.Max(1, ChangedFrames);
    public double MsPerUnchangedFrame => UnchangedMs / Math.Max(1, Frames - ChangedFrames);

    /// <summary>OCR passes per second while the same subtitle stays on screen.</summary>
    public double UnchangedReadsPerSecond => UnchangedReads / Math.Max(0.001, UnchangedSeconds);

    public long BytesPerFrame => Bytes / Math.Max(1, Frames);
}

/// <summary>What came out of one replay.</summary>
/// <param name="ExtraWords">Words in the caught lines that the subtitles do not have: OCR noise that got through.</param>
/// <param name="Unmatched">Reported lines that match no subtitle.</param>
/// <param name="Reported">The final reading of every reported line.</param>
/// <param name="Report">Missed lines, lines with missing or extra words, and extra lines.</param>
internal sealed record ReplayOutcome(int Lines, int Caught, int Found, int Words, int ExtraWords, List<string> Unmatched,
    List<string> Reported, List<string> Report, List<string> Notices, ReplayCost Cost)
{
    public double LineRate => (double)Caught / Lines;
    public double WordRate => (double)Found / Math.Max(1, Words);
}
