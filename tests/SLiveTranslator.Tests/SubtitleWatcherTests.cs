using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;
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

        public Harness(Func<TimeSpan, CancellationToken, Task>? delay = null, ITranslator? translator = null, double gapRatio = 1.3,
            OcrMode mode = OcrMode.Auto)
        {
            Watcher = new SubtitleWatcher(() => Ocr, () => translator ?? Translator, () => new WatcherConfig("en", "vi", 300, gapRatio) { OcrMode = mode }, Sink)
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

        /// <summary>
        /// Shows one frame whose OCR result is the given lines. Its white mark (the "text" of the mask, all rows high, so
        /// nothing is cropped) moves every frame, so the frame always counts as changed.
        /// </summary>
        public SubtitleWatcher.FrameResult Show(params OcrLine[] lines)
        {
            Ocr.Lines = lines;
            _frame++;
            return Process();
        }

        /// <summary>Shows the last frame again, pixel for pixel.</summary>
        public SubtitleWatcher.FrameResult Repeat() => Process();

        /// <summary>The time of the next poll, for a frame the test draws itself.</summary>
        public long Tick() => _now += 300;

        public long Now => _now;

        public void Wait(long ms) => _now += ms;

        SubtitleWatcher.FrameResult Process()
        {
            using var frame = new Bitmap(Capture.Width, Capture.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(_frame % 2 == 0 ? Color.FromArgb(20, 20, 20) : Color.FromArgb(70, 70, 70));
                g.FillRectangle(Brushes.White, 210 + _frame % 2 * 6, 0, 4, Capture.Height);
            }
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
        int calls = h.Ocr.Calls;
        var second = await h.Session.ProcessFrameAsync(frame, 400, CancellationToken.None);

        Assert.True(first.OcrRan);
        Assert.False(second.OcrRan);
        Assert.Equal(1, calls); // no white text in the frame: only the plain frame is read
        Assert.Equal(calls, h.Ocr.Calls);
        Assert.Equal(0, second.Recognitions);
        Assert.IsType<SubtitleTracker.NewLine>(second.Event);
    }

    [Fact]
    public void Text_on_an_unchanged_frame_is_read_again_every_second()
    {
        // "Yes." → "No." on a still scene can change the pixels too little to notice.
        var h = new Harness();
        h.Show(Line("Yes.", 470, 60));
        h.Ocr.Lines = [Line("No.", 475, 50)];

        var reads = Enumerable.Range(0, 5).Select(_ => h.Repeat().OcrRan).ToList();

        Assert.Equal([false, false, false, true, false], reads); // 300 ms polls: read again once a second has passed, 1.2 s after the last read
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

    // ---- what is read, and how often ----

    /// <summary>A frame of the capture strip: a still or moving grey scene, with white bars standing in for text.</summary>
    static Bitmap Strip(int sceneGrey, params Rectangle[] whiteText)
    {
        var bmp = new Bitmap(Capture.Width, Capture.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(sceneGrey, sceneGrey + 4, sceneGrey + 8));
        foreach (var r in whiteText) g.FillRectangle(Brushes.White, r);
        return bmp;
    }

    static async Task<SubtitleWatcher.FrameResult> Process(Harness h, Bitmap frame, long now)
    {
        using (frame) return await h.Session.ProcessFrameAsync(frame, now, CancellationToken.None);
    }

    [Fact]
    public async Task A_moving_scene_behind_unchanged_white_text_costs_no_ocr()
    {
        // The mask keeps only the white text, so a scene that moves behind it does not count as a change.
        var h = new Harness();
        h.Ocr.Lines = [Line("Where is the griffin?", 350, 300)];
        var text = new Rectangle(350, 40, 300, 28);

        var first = await Process(h, Strip(30, text), 300);
        var second = await Process(h, Strip(90, text), 600);
        var third = await Process(h, Strip(60, text), 900);

        Assert.True(first.OcrRan);
        Assert.False(second.OcrRan);
        Assert.False(third.OcrRan);
    }

    [Fact]
    public async Task While_the_white_in_the_region_reads_as_nothing_the_region_pixels_decide_too()
    {
        // A white icon or snow and no subtitle: a line of another colour would change only the pixels, so a moving
        // scene is read, as it was before the mask came first.
        var h = new Harness();
        var icon = new Rectangle(250, 6, 12, 12);

        var first = await Process(h, Strip(30, icon), 300);
        var moved = await Process(h, Strip(90, icon), 600);
        var still = await Process(h, Strip(90, icon), 900);

        Assert.Equal([true, true, false], new[] { first.OcrRan, moved.OcrRan, still.OcrRan });
    }

    static Bitmap Painted(Bitmap frame, Color colour, Rectangle area)
    {
        using var g = Graphics.FromImage(frame);
        using var brush = new SolidBrush(colour);
        g.FillRectangle(brush, area);
        return frame;
    }

    [Fact]
    public async Task A_line_of_another_colour_beside_white_scenery_is_read_from_the_whole_plain_frame()
    {
        // A white icon in the region and a yellow dialog choice appearing below it on a still scene: the mask does not
        // change, the region's pixels do. The plain frame is read whole, not only the rows of the icon.
        var h = new Harness();
        var icon = new Rectangle(250, 6, 12, 12);
        await Process(h, Strip(40, icon), 300);
        h.Ocr.Lines = [Line("1. I'll stay a little longer.", 330, 340, top: 60)];
        h.Ocr.MaskLines = [];

        var choice = await Process(h, Painted(Strip(40, icon), Color.Gold, new Rectangle(330, 60, 340, 28)), 600);

        Assert.True(choice.OcrRan);
        Assert.Equal(2, choice.Recognitions);
        Assert.Equal("1. I'll stay a little longer.", choice.Reading);
        Assert.Contains(new Size(Capture.Width, Capture.Height), h.Ocr.Images);
    }

    [Fact]
    public async Task Once_the_mask_keeps_winning_a_frame_without_white_text_costs_no_recognition_even_while_the_scene_moves()
    {
        // Exploring with no subtitle: the plain frame would only read the scene, on every poll.
        var h = new Harness();
        var griffin = Line("Where is the griffin?", 350, 300);
        for (int i = 0; i < OcrSourcePolicy.WinsToSettle; i++) h.Show(griffin);
        int calls = h.Ocr.Calls;

        var gone = await Process(h, Strip(30), h.Tick());
        var moved = await Process(h, Strip(90), h.Tick());
        var movedAgain = await Process(h, Strip(60), h.Tick());

        Assert.Equal("", gone.Reading);
        Assert.Equal(0, gone.Recognitions);
        Assert.False(moved.OcrRan);
        Assert.False(movedAgain.OcrRan);
        Assert.Equal(calls, h.Ocr.Calls);
    }

    [Fact]
    public async Task A_short_answer_swapped_on_a_still_scene_is_read_at_once()
    {
        // "Yes." → "No.": a few changed cells of the mask, read on the next poll instead of a second later.
        var h = new Harness();
        h.Ocr.Lines = [Line("Yes.", 470, 60)];
        await Process(h, Strip(40, new Rectangle(470, 40, 60, 28)), 300);
        h.Ocr.Lines = [Line("No.", 475, 50)];

        var swapped = await Process(h, Strip(40, new Rectangle(475, 40, 18, 28), new Rectangle(500, 40, 20, 28)), 600);

        Assert.True(swapped.OcrRan);
        Assert.Equal("No.", swapped.Reading);
    }

    [Fact]
    public async Task Without_white_text_the_region_pixels_decide_and_only_the_plain_frame_is_read()
    {
        // Subtitles that are not white: the mask is empty, so the plain frame is read whenever the region changes.
        var h = new Harness();
        h.Ocr.Lines = [Line("Where is the griffin?", 350, 300)];

        var first = await Process(h, Strip(30), 300);
        var same = await Process(h, Strip(30), 600);
        var changed = await Process(h, Strip(90), 900);

        Assert.Equal([true, false, true], new[] { first.OcrRan, same.OcrRan, changed.OcrRan });
        Assert.Equal(1, first.Recognitions);
        Assert.Equal(0, h.Ocr.MaskCalls);
    }

    [Fact]
    public async Task Only_the_rows_with_text_are_read_and_line_positions_come_back_in_capture_rows()
    {
        var h = new Harness();
        h.Ocr.Lines = [Line("Where is the griffin?", 350, 300, top: 1)]; // at the top of what OCR is given

        var result = await Process(h, Strip(30, new Rectangle(350, 50, 300, 20)), 300);

        // 20 text rows and 12 rows around them on each side.
        Assert.All(h.Ocr.Images, size => Assert.Equal(new Size(Capture.Width, 44), size));
        Assert.False(result.TouchesEdge); // row 1 of the crop is row 39 of the capture
    }

    [Theory]
    [InlineData(new[] { 50, 77 }, 33, 62)] // one 28 px line: 17 rows of margin on each side
    [InlineData(new[] { 0, 27 }, 0, 45)] // text at the top edge stays at the edge
    [InlineData(new[] { 10, 37, 46, 73 }, 0, 91)] // two lines; the margin comes from one line's height
    public void Text_rows_get_a_margin_of_six_tenths_of_a_line(int[] runs, int top, int height)
    {
        var rows = new int[100];
        for (int i = 0; i < runs.Length; i += 2)
            for (int y = runs[i]; y <= runs[i + 1]; y++) rows[y] = 40;

        Assert.Equal((top, height), SubtitleWatcher.TextRows(rows));
    }

    [Fact]
    public void Without_text_rows_the_whole_strip_is_read() => Assert.Equal((0, 100), SubtitleWatcher.TextRows(new int[100]));

    [Fact]
    public void Once_one_reading_keeps_winning_only_it_is_recognized()
    {
        var h = new Harness();
        var griffin = Line("Where is the griffin, Geralt?", 330, 340);

        var counts = Enumerable.Range(0, 7).Select(_ => h.Show(griffin).Recognitions).ToList();
        Assert.Equal([2, 2, 2, 2, 2, 1, 1], counts); // the mask won 5 times in a row (equal readings go to the mask)

        var mill = Line("Out by the old mill, sir.", 320, 360);
        Assert.Equal(2, h.Show(mill).Recognitions); // another line: both are read, and the mask wins again
        Assert.Equal(1, h.Show(mill).Recognitions);
    }

    [Fact]
    public void A_settled_reading_that_turns_worse_is_compared_with_the_other_again()
    {
        var h = new Harness();
        var clean = Line("Where is the griffin, Geralt?", 330, 340);
        for (int i = 0; i < 5; i++) h.Show(clean);
        h.Ocr.MaskLines = [Line("Wh~re |s the gr@ff|n, Ger~lt?", 330, 340)]; // the mask loses the line over a bright patch

        var result = h.Show(clean);

        Assert.Equal(2, result.Recognitions);
        Assert.Equal(clean.Text, result.Reading);
        Assert.Equal(2, h.Show(clean).Recognitions); // the plain frame won: both are compared until one settles again
    }

    [Fact]
    public void Reading_nothing_beside_white_scenery_never_leaves_the_plain_frame_unread()
    {
        // Snow in the region and no subtitle: both images read nothing, frame after frame. Then a subtitle the mask
        // cannot read (white on snow, or another colour) appears: the plain frame must still be read.
        var h = new Harness();
        h.Ocr.MaskLines = [];
        for (int i = 0; i < 2 * OcrSourcePolicy.WinsToSettle; i++) Assert.Equal(2, h.Show().Recognitions);

        var line = Line("Where is the griffin, Geralt?", 330, 340);
        var result = h.Show(line);

        Assert.Equal(2, result.Recognitions);
        Assert.Equal(line.Text, result.Reading);
    }

    [Fact]
    public void A_settled_mask_reading_nothing_while_it_sees_white_is_checked_against_the_plain_frame()
    {
        var h = new Harness();
        var griffin = Line("Where is the griffin, Geralt?", 330, 340);
        for (int i = 0; i < OcrSourcePolicy.WinsToSettle + 1; i++) h.Show(griffin);
        h.Ocr.MaskLines = [];

        var result = h.Show(Line("Out by the old mill, sir.", 320, 360));

        Assert.Equal(2, result.Recognitions);
        Assert.Equal("Out by the old mill, sir.", result.Reading);
    }

    [Theory]
    [InlineData("uams@Utothe")]
    [InlineData("rn 1l ~ lI")]
    public void Scene_noise_the_settled_image_adds_to_a_line_is_dropped_against_the_other_image(string noise)
    {
        // Read alone, the noise above the line would count as the line growing, and be translated as an update.
        var h = new Harness();
        var clean = Line("Where is the griffin, Geralt?", 330, 340);
        for (int i = 0; i < OcrSourcePolicy.WinsToSettle + 1; i++) h.Show(clean);
        h.Ocr.MaskLines = [clean, Line(noise, 230, 160, top: 4)];

        var results = Enumerable.Range(0, 3).Select(_ => h.Show(clean)).ToList();

        Assert.All(results, r => Assert.Equal(clean.Text, r.Reading));
        Assert.All(results, r => Assert.Equal(2, r.Recognitions));
        Assert.Equal([new RecordingSink.Detected(h.Id(1), clean.Text, false)], h.Sink.DetectedLines);
    }

    /// <summary>The white line of text, and one pale pixel of the scene leaking into the mask next to it at a place that moves.</summary>
    static Bitmap Leaking(int frame)
    {
        var bmp = Strip(40, new Rectangle(350, 40, 300, 28));
        bmp.SetPixel(360 + frame * 37 % 280, 39, Color.FromArgb(160, 160, 160));
        return bmp;
    }

    /// <summary>The line with one letter misread, at a different letter in every frame: no two readings are alike.</summary>
    static string Misread(string text, int frame)
    {
        var letters = Enumerable.Range(0, text.Length).Where(i => char.IsLetter(text[i])).ToList();
        var chars = text.ToCharArray();
        int at = letters[frame % letters.Count];
        chars[at] = chars[at] == 'q' ? 'z' : 'q';
        return new string(chars);
    }

    [Fact]
    public async Task A_line_whose_readings_never_agree_is_read_about_once_a_second_after_its_first_readings()
    {
        // Bright pixels behind the text change the mask a little every frame, and OCR reads the line a little
        // differently each time. A few extra readings may settle which is right; after that, more rarely do.
        var h = new Harness();
        const string text = "Where is the griffin, Geralt? Out by the old mill, sir.";
        var readAt = new List<long>();
        for (int i = 0; i < 24; i++)
        {
            h.Ocr.Lines = [Line(Misread(text, i), 300, 400)];
            long now = h.Tick();
            if ((await Process(h, Leaking(i), now)).OcrRan) readAt.Add(now);
        }

        Assert.True(readAt.Count(t => t <= 2_000) >= 5, $"read at {string.Join(", ", readAt)}"); // its first readings, at every change
        double perSecond = readAt.Count(t => t > 2_000) / ((h.Now - 2_000) / 1000.0);
        Assert.True(perSecond < 1.0, $"{perSecond:F2} OCR passes per second after the first two seconds; read at {string.Join(", ", readAt)}");
        Assert.Single(h.Sink.DetectedLines);
    }

    [Theory]
    [InlineData(OcrMode.Mask, 0, 2)]
    [InlineData(OcrMode.Plain, 2, 0)]
    public void The_ocr_mode_setting_reads_only_that_image(OcrMode mode, int plainCalls, int maskCalls)
    {
        var h = new Harness(mode: mode);
        h.Show(Line("Where is the griffin?", 350, 300));
        h.Show(Line("Out by the old mill, sir.", 320, 360));

        Assert.Equal(plainCalls, h.Ocr.PlainCalls);
        Assert.Equal(maskCalls, h.Ocr.MaskCalls);
    }

    [Fact]
    public async Task In_mask_mode_a_frame_without_white_text_needs_no_ocr()
    {
        var h = new Harness(mode: OcrMode.Mask);
        h.Ocr.Lines = [Line("Where is the griffin?", 350, 300)];

        var first = await Process(h, Strip(30), 300);
        var moved = await Process(h, Strip(90), 600);

        Assert.Equal("", first.Reading);
        Assert.Equal(0, h.Ocr.Calls);
        Assert.False(moved.OcrRan);
    }

    [Fact]
    public void A_game_going_to_the_background_clears_its_line_and_the_next_frame_is_read_fresh()
    {
        var h = new Harness();
        var line = Line("Where is the griffin?", 350, 300);
        h.Show(line);
        h.Show(line);

        h.Session.Pause(1_000);
        var back = h.Repeat();

        Assert.Contains("cleared", h.Sink.Events);
        Assert.True(back.OcrRan);
    }

    [Fact]
    public void A_line_still_on_screen_when_the_game_comes_back_is_not_reported_again()
    {
        // The game paused with the line on screen while the player read a wiki for a few seconds.
        var h = new Harness();
        var line = Line("Where is the griffin?", 350, 300);
        h.Show(line);
        h.Show(line);

        h.Session.Pause(h.Now);
        h.Wait(5_000);
        for (int i = 0; i < 4; i++) h.Show(line);

        Assert.Equal([new RecordingSink.Detected(h.Id(1), line.Text, false)], h.Sink.DetectedLines);
        var mill = Line("Out by the old mill, sir.", 320, 360);
        h.Show(mill);
        Assert.IsType<SubtitleTracker.NewLine>(h.Show(mill).Event); // the next line is new as usual
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
