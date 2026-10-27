using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

public class SubtitleWatcherTests
{
    static Bitmap Frame(string text)
    {
        var bmp = new Bitmap(800, 100);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        using var font = new Font("Segoe UI", 24);
        g.DrawString(text, font, Brushes.White, new PointF(20, 30));
        return bmp;
    }

    [Fact]
    public void Frame_signature_is_stable_for_identical_frames_and_changes_with_text()
    {
        using var a = Frame("Welcome, traveller.");
        using var b = Frame("Welcome, traveller.");
        using var c = Frame("The road ahead is dangerous.");

        var sa = SubtitleWatcher.FrameSignature(a);
        var sb = SubtitleWatcher.FrameSignature(b);
        var sc = SubtitleWatcher.FrameSignature(c);

        Assert.Equal(0.0, SubtitleWatcher.MeanAbsDiff(sa, sb));
        Assert.True(SubtitleWatcher.MeanAbsDiff(sa, sc) >= 1.0);
    }

    [Fact]
    public void The_change_signature_samples_only_the_region()
    {
        // A line swap read from a 2560 px strip: averaged over the whole strip it is diluted by the strip/region width.
        using var yes = new Bitmap(2560, 200, PixelFormat.Format32bppArgb);
        using var no = new Bitmap(2560, 200, PixelFormat.Format32bppArgb);
        using var font = new Font("Segoe UI", 30, GraphicsUnit.Pixel);
        foreach (var (bmp, text) in new[] { (yes, "Where is Yennefer?"), (no, "Where is Ciri?") })
        {
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(40, 44, 50));
            g.DrawString(text, font, Brushes.White, 1150, 120);
        }
        var region = new Rectangle(880, 0, 800, 200);

        double whole = SubtitleWatcher.MeanAbsDiff(SubtitleWatcher.FrameSignature(yes), SubtitleWatcher.FrameSignature(no));
        double inRegion = SubtitleWatcher.MeanAbsDiff(SubtitleWatcher.FrameSignature(yes, region), SubtitleWatcher.FrameSignature(no, region));
        Assert.True(inRegion > whole * 2.5, $"region {inRegion:F2}, whole strip {whole:F2}");
        Assert.True(SubtitleWatcher.Changed(SubtitleWatcher.FrameSignature(yes, region), SubtitleWatcher.FrameSignature(no, region)));
        Assert.False(SubtitleWatcher.Changed(SubtitleWatcher.FrameSignature(yes, region), SubtitleWatcher.FrameSignature(yes, region)));
    }

    [Fact]
    public void Capture_spans_the_monitor_width_at_the_region_height()
    {
        var screen = new Rectangle(0, 0, 2560, 1440);

        Assert.Equal(new Rectangle(0, 1094, 2560, 212), SubtitleWatcher.CaptureRect(new Rectangle(327, 1094, 1825, 212), screen));
        // A region that hangs over the monitor edge is still captured whole.
        Assert.Equal(new Rectangle(0, 900, 2700, 100), SubtitleWatcher.CaptureRect(new Rectangle(2000, 900, 700, 100), screen));
        Assert.Equal(new Rectangle(-1920, 800, 1920, 120), SubtitleWatcher.CaptureRect(new Rectangle(-1500, 800, 900, 120), new Rectangle(-1920, 0, 1920, 1080)));
    }

    [Theory]
    [InlineData(0, false, 300, 40, 260)]
    [InlineData(0, true, 300, 40, 100)] // confirm a changed line quickly
    [InlineData(1, false, 300, 40, 260)] // the first errors retry at the normal interval
    [InlineData(2, true, 300, 40, 260)]
    [InlineData(3, false, 300, 40, 2_000)]
    [InlineData(4, false, 300, 40, 4_000)]
    [InlineData(9, false, 300, 40, 30_000)]
    [InlineData(0, false, 300, 500, 20)]
    public void Loop_delay_backs_off_only_after_repeated_errors(int failures, bool awaiting, int interval, int elapsed, int expected) =>
        Assert.Equal(expected, SubtitleWatcher.NextDelayMs(failures, awaiting, interval, elapsed));

    // ---- one frame at a time, with scripted OCR ----

    // A 1000 px capture strip; the user's region is its middle 600 px.
    static readonly Rectangle Capture = new(0, 500, 1000, 100);
    static readonly Rectangle Region = new(200, 500, 600, 100);

    sealed class Harness
    {
        int _frame;
        long _now;

        public Harness(Func<TimeSpan, CancellationToken, Task>? delay = null, ITranslator? translator = null, double gapRatio = 1.3)
        {
            Watcher = new SubtitleWatcher(() => Ocr, () => translator ?? Translator, () => ("en", "vi", 300, gapRatio), Sink)
            {
                Delay = delay ?? ((span, _) =>
                {
                    lock (Delays) Delays.Add(span);
                    return Task.CompletedTask;
                }),
            };
            Session = Watcher.CreateSession(Region, Capture);
        }

        public ScriptedOcr Ocr { get; } = new();
        public EchoTranslator Translator { get; } = new();
        public RecordingSink Sink { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        public SubtitleWatcher Watcher { get; }
        public SubtitleWatcher.Session Session { get; }
        public int Id(int localId) => Session.Id * 1_000_000 + localId;

        /// <summary>Shows one frame whose OCR result is the given lines; the pixels change every frame.</summary>
        public SubtitleWatcher.FrameResult Show(params OcrLine[] lines)
        {
            Ocr.Lines = lines;
            _frame++;
            return Process();
        }

        /// <summary>Shows the last frame again, pixel for pixel.</summary>
        public SubtitleWatcher.FrameResult Repeat() => Process();

        SubtitleWatcher.FrameResult Process()
        {
            using var frame = new Bitmap(Capture.Width, Capture.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(frame)) g.Clear(_frame % 2 == 0 ? Color.FromArgb(20, 20, 20) : Color.FromArgb(70, 70, 70));
            _now += 300;
            return Session.ProcessFrameAsync(frame, _now, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    static OcrLine Line(string text, int left, int width, int top = 40) => new(text, new Rectangle(left, top, width, 28));

    static bool IsTranslated(object e) => e is ValueTuple<string, int, string> { Item1: "translated" };
    static bool IsFailed(object e) => e is ValueTuple<string, int, string> { Item1: "failed" };

    [Fact]
    public void A_key_hint_beside_the_region_is_dropped_and_a_line_wider_than_the_region_is_kept_whole()
    {
        var h = new Harness();
        var subtitle = Line("Geralt: Where is the griffin? Out by the old mill, sir, past the bridge.", 120, 760);
        var hint = Line("Space Skip", 850, 120, top: 70);

        h.Show(subtitle, hint);
        var result = h.Show(subtitle, hint);

        Assert.Equal(subtitle.Text, result.Reading);
        var line = Assert.IsType<SubtitleTracker.NewLine>(result.Event);
        Assert.Equal(subtitle.Text, line.Text);
        Assert.Equal([new RecordingSink.Detected(h.Id(1), subtitle.Text, false)], h.Sink.DetectedLines);
    }

    [Fact]
    public void Two_stacked_subtitle_lines_become_one_paragraph_and_apostrophes_are_repaired()
    {
        var h = new Harness();
        var top = Line("You promised Ciri you'd train with her. Wonlt", 250, 500, top: 20);
        var bottom = Line("you go? 11m sure she will.", 330, 340, top: 76); // 28 px boxes, 28 px apart

        var result = h.Show(top, bottom);

        Assert.Equal("You promised Ciri you'd train with her. Won't you go? I'm sure she will.", result.Reading);
    }

    [Fact]
    public void The_paragraph_gap_comes_from_the_settings()
    {
        // 20 px boxes 28 px apart: 1.4 line heights.
        OcrLine[] lines = [new("Go and train with her.", new Rectangle(350, 10, 300, 20)), new("Then come back.", new Rectangle(400, 58, 200, 20))];

        Assert.Equal("Go and train with her.\nThen come back.", new Harness().Show(lines).Reading);
        Assert.Equal("Go and train with her. Then come back.", new Harness(gapRatio: 2.0).Show(lines).Reading);
        Assert.Equal("Go and train with her.\nThen come back.", new Harness(gapRatio: 0).Show(lines).Reading); // clamped to 0.7
    }

    [Fact]
    public void A_noise_line_only_the_plain_frame_has_does_not_beat_the_clean_mask_reading()
    {
        var h = new Harness();
        var answer = Line("Yes.", 470, 60);
        h.Ocr.MaskLines = [answer];

        h.Ocr.Lines = [answer, Line("uams@Utothe", 230, 160, top: 4)];
        Assert.Equal("Yes.", h.Show(h.Ocr.Lines.ToArray()).Reading);
        h.Ocr.Lines = [Line("Where is the griffin?", 350, 300), Line("rn 1l ~ lI", 360, 120, top: 4)];
        h.Ocr.MaskLines = [Line("Where is the griffin?", 350, 300)];
        Assert.Equal("Where is the griffin?", h.Show(h.Ocr.Lines.ToArray()).Reading);
    }

    [Fact]
    public void A_clean_line_only_the_plain_frame_read_is_kept()
    {
        // A part of the subtitle the mask lost (a pale background behind it), and white text over snow (the mask reads noise only).
        var h = new Harness();
        h.Ocr.MaskLines = [Line("Out by the old mill, sir.", 320, 360, top: 60)];
        Assert.Equal("Geralt: Where is the griffin? Out by the old mill, sir.",
            h.Show(Line("Geralt: Where is the griffin?", 300, 400, top: 20), Line("Out by the old mill, sir.", 320, 360, top: 60)).Reading);

        h.Ocr.MaskLines = [Line("ll1 rn ~", 250, 90, top: 10)];
        Assert.Equal("Wh@t do you want, witcher?", h.Show(Line("Wh@t do you want, witcher?", 330, 340)).Reading);
    }

    [Fact]
    public async Task An_unchanged_frame_reuses_the_last_reading_without_ocr()
    {
        var h = new Harness();
        using var frame = new Bitmap(Capture.Width, Capture.Height, PixelFormat.Format32bppArgb);
        h.Ocr.Lines = [Line("Where is the griffin?", 350, 300)];

        var first = await h.Session.ProcessFrameAsync(frame, 300, CancellationToken.None);
        var second = await h.Session.ProcessFrameAsync(frame, 400, CancellationToken.None);

        Assert.True(first.OcrRan);
        Assert.False(second.OcrRan);
        Assert.Equal(2, h.Ocr.Calls); // the plain frame and the mask, once
        Assert.IsType<SubtitleTracker.NewLine>(second.Event);
    }

    [Fact]
    public void Text_on_an_unchanged_frame_is_read_again_every_second()
    {
        // "Yes." → "No." on a still scene can change the pixels too little to notice.
        var h = new Harness();
        h.Show(Line("Yes.", 470, 60));
        h.Ocr.Lines = [Line("No.", 475, 50)];

        var reads = Enumerable.Range(0, 4).Select(_ => h.Repeat().OcrRan).ToList();

        Assert.Equal([false, false, true, false], reads); // 300 ms polls: read again 900 ms after the last read
        Assert.Contains(h.Sink.DetectedLines, d => d.Text == "No.");
    }

    [Fact]
    public void An_unchanged_empty_frame_is_not_read_again()
    {
        var h = new Harness();
        h.Show();

        Assert.All(Enumerable.Range(0, 6), _ => Assert.False(h.Repeat().OcrRan));
    }

    [Fact]
    public void A_misreading_whose_frame_then_stays_unchanged_does_not_replace_the_clean_line()
    {
        var h = new Harness();
        h.Show(Line("Where is the griffin, Geralt?", 330, 340));
        h.Show(Line("Where is the griffln, Geralt?", 330, 340));
        Assert.False(h.Repeat().OcrRan);
        Assert.False(h.Repeat().OcrRan);

        Assert.Equal([new RecordingSink.Detected(h.Id(1), "Where is the griffin, Geralt?", false)], h.Sink.DetectedLines);
    }

    [Fact]
    public async Task A_frame_whose_ocr_failed_is_read_again()
    {
        var h = new Harness();
        using var frame = new Bitmap(Capture.Width, Capture.Height, PixelFormat.Format32bppArgb);
        h.Ocr.Lines = [Line("Where is the griffin?", 350, 300)];
        h.Ocr.Failure = new COMException("Operation aborted");

        await Assert.ThrowsAsync<COMException>(() => h.Session.ProcessFrameAsync(frame, 300, CancellationToken.None));
        h.Ocr.Failure = null;
        var retry = await h.Session.ProcessFrameAsync(frame, 400, CancellationToken.None);

        Assert.True(retry.OcrRan);
        Assert.Equal("Where is the griffin?", retry.Reading);
    }

    [Fact]
    public void Lines_cut_by_the_top_of_the_region_raise_one_notice_after_the_second_line()
    {
        var h = new Harness();
        foreach (var text in new[] { "ing our young damsel to the lower courtyard.", "She wants to practice? She'll get to practice.", "Then let her practice." })
        {
            h.Show(Line(text, 300, 400, top: 0));
            Assert.True(h.Show(Line(text, 300, 400, top: 0)).TouchesEdge);
            h.Show();
            h.Show();
            h.Show();
        }

        Assert.Single(h.Sink.Notices);
        Assert.Contains("top or bottom", h.Sink.Notices[0]);
        Assert.Equal(3, h.Sink.DetectedLines.Count);
    }

    [Fact]
    public async Task Bright_scenery_at_the_side_of_the_monitor_raises_no_notice()
    {
        // Snow at the monitor's edges, and a line reaching the side: the capture spans the monitor, nothing is cut.
        var h = new Harness();
        using var snow = new SolidBrush(Color.FromArgb(232, 236, 242));
        long now = 0;
        foreach (var text in new[] { "Where is the griffin?", "Out by the old mill, sir.", "How many has it taken?" })
        {
            OcrLine[] line = [Line(text, 3, 990)];
            foreach (var lines in new[] { line, line, [], [], [] })
            {
                h.Ocr.Lines = lines;
                using var frame = new Bitmap(Capture.Width, Capture.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(frame))
                {
                    g.Clear(Color.FromArgb(30, 34, 40 + (int)(now / 300 % 2) * 40));
                    g.FillRectangle(snow, 0, 0, 40, Capture.Height);
                    g.FillRectangle(snow, Capture.Width - 40, 0, 40, Capture.Height);
                }
                now += 300;
                await h.Session.ProcessFrameAsync(frame, now, CancellationToken.None);
            }
        }

        Assert.Equal(3, h.Sink.DetectedLines.Count);
        Assert.Empty(h.Sink.Notices);
    }

    [Fact]
    public void Edge_detection_looks_for_a_line_cut_by_the_top_or_bottom()
    {
        Assert.False(SubtitleWatcher.TouchesEdge([Line("Where is the griffin?", 350, 300)], 100));
        Assert.False(SubtitleWatcher.TouchesEdge([Line("ing our young damsel to the lower courtyard", 0, 1000)], 100)); // sides cannot cut
        Assert.True(SubtitleWatcher.TouchesEdge([Line("our young damsel", 350, 300, top: 0)], 100));
        Assert.True(SubtitleWatcher.TouchesEdge([Line("to the lower courtyard", 350, 300, top: 71)], 100)); // ends at row 99
    }

    // ---- translation ----

    [Fact]
    public void A_failed_translation_is_retried_once_after_six_seconds()
    {
        var h = new Harness();
        h.Translator.FailuresLeft = 1;

        h.Watcher.Emit(1, 1, "Where is the griffin?", false, update: false, CancellationToken.None);

        Assert.True(h.Sink.WaitFor(e => e.Any(IsTranslated)));
        Assert.Equal([TimeSpan.FromSeconds(6)], h.Delays);
        Assert.Equal(
        [
            new RecordingSink.Detected(1_000_001, "Where is the griffin?", false),
            ("failed", 1_000_001, "Where is the griffin?"),
            ("translated", 1_000_001, "vi:Where is the griffin?"),
        ], h.Sink.Events);
    }

    [Fact]
    public void A_translation_timeout_is_a_failure_that_is_retried()
    {
        // HttpClient and the Claude SDK report a timeout as a TaskCanceledException, though nobody cancelled.
        var h = new Harness();
        h.Translator.FailuresLeft = 1;
        h.Translator.Failure = () => new TaskCanceledException("The request was canceled due to the configured timeout.", new TimeoutException());

        h.Watcher.Emit(1, 1, "Where is the griffin?", false, update: false, CancellationToken.None);

        Assert.True(h.Sink.WaitFor(e => e.Any(IsTranslated)));
        Assert.Equal([TimeSpan.FromSeconds(6)], h.Delays);
        Assert.Single(h.Sink.Events, IsFailed);
    }

    [Fact]
    public void A_line_that_fails_twice_stays_failed()
    {
        var h = new Harness();
        h.Translator.FailuresLeft = 5;

        h.Watcher.Emit(1, 1, "Where is the griffin?", false, update: false, CancellationToken.None);

        Assert.True(h.Sink.WaitFor(e => e.Count(IsFailed) == 2));
        Thread.Sleep(50);
        Assert.Equal(2, h.Translator.Requests.Count);
        Assert.DoesNotContain(h.Sink.Events, IsTranslated);
    }

    [Fact]
    public void A_growing_line_is_translated_again_only_once_it_settles_and_only_when_its_text_changed()
    {
        var settle = new TaskCompletionSource();
        var debounces = new List<TimeSpan>();
        var h = new Harness((span, _) =>
        {
            lock (debounces) debounces.Add(span);
            return settle.Task;
        });

        h.Watcher.Emit(1, 1, "Geralt: The griffin", false, update: false, CancellationToken.None);
        Assert.True(h.Sink.WaitFor(e => e.Any(IsTranslated)));
        h.Watcher.Emit(1, 1, "Geralt: The griffin has been", false, update: true, CancellationToken.None);
        h.Watcher.Emit(1, 1, "Geralt: The griffin has been hunting here.", false, update: true, CancellationToken.None);
        h.Watcher.Emit(1, 1, "Geralt: The griffin has been hunting here.", true, update: true, CancellationToken.None); // only the flag changed
        settle.SetResult();

        Assert.True(h.Sink.WaitFor(e => e.Contains(("translated", 1_000_001, "vi:Geralt: The griffin has been hunting here."))));
        Thread.Sleep(50);
        Assert.Equal(["Geralt: The griffin", "Geralt: The griffin has been hunting here."], h.Translator.Requests);
        Assert.Equal(3, debounces.Count);
        Assert.All(debounces, d => Assert.Equal(TimeSpan.FromMilliseconds(500), d));
        Assert.Equal(4, h.Sink.DetectedLines.Count); // the reading itself is reported at once; only translation waits
    }

    [Theory]
    [InlineData(100, 300)] // a fast poll still waits the minimum
    [InlineData(300, 500)] // the default: one poll and its confirming frame, with room for the OCR
    [InlineData(1000, 1200)]
    public void The_update_debounce_outlasts_the_next_grown_reading(int intervalMs, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), SubtitleWatcher.UpdateDebounce(intervalMs));

    [Fact]
    public void A_grown_line_shows_the_earlier_translation_until_its_own_arrives()
    {
        var gated = new GatedTranslator();
        var h = new Harness(translator: gated);

        h.Watcher.Emit(1, 1, "Geralt: The griffin", true, update: false, CancellationToken.None);
        h.Watcher.Emit(1, 1, "Geralt: The griffin has been hunting here.", false, update: true, CancellationToken.None);
        Assert.True(h.Sink.WaitFor(_ => gated.Count == 2));
        gated.Answer(0);
        Assert.True(h.Sink.WaitFor(e => e.Any(IsTranslated)));
        gated.Answer(1);
        Assert.True(h.Sink.WaitFor(e => e.Count(IsTranslated) == 2));

        Assert.Equal(
        [
            ("translated", 1_000_001, "vi:Geralt: The griffin"),
            ("translated", 1_000_001, "vi:Geralt: The griffin has been hunting here."),
        ], h.Sink.Events.Where(IsTranslated));
        Assert.Equal([true, false], h.Sink.TranslatedLowConfidence); // each translation keeps the flag of the reading it came from
    }

    [Fact]
    public void An_earlier_reading_s_translation_arriving_after_the_newer_one_is_dropped()
    {
        var gated = new GatedTranslator();
        var h = new Harness(translator: gated);

        h.Watcher.Emit(1, 1, "Geralt: The griffin", false, update: false, CancellationToken.None);
        h.Watcher.Emit(1, 1, "Geralt: The griffin has been hunting here.", false, update: true, CancellationToken.None);
        Assert.True(h.Sink.WaitFor(_ => gated.Count == 2));
        gated.Answer(1);
        Assert.True(h.Sink.WaitFor(e => e.Any(IsTranslated)));
        gated.Answer(0);
        Thread.Sleep(50);

        Assert.Equal([("translated", 1_000_001, "vi:Geralt: The griffin has been hunting here.")], h.Sink.Events.Where(IsTranslated));
    }

    [Fact]
    public void Translation_state_is_kept_only_for_the_latest_lines()
    {
        var h = new Harness();
        for (int localId = 1; localId <= 40; localId++)
            h.Watcher.Emit(1, localId, $"Line number {localId}.", false, update: false, CancellationToken.None);
        Assert.True(h.Sink.WaitFor(e => e.Count(IsTranslated) == 40));

        Assert.True(h.Watcher.TrackedLines <= 8, $"{h.Watcher.TrackedLines} lines tracked");
        h.Watcher.Emit(1, 40, "Line number 40, the last one.", false, update: true, CancellationToken.None);
        Assert.True(h.Sink.WaitFor(e => e.Contains(("translated", 1_000_040, "vi:Line number 40, the last one."))));
    }
}
