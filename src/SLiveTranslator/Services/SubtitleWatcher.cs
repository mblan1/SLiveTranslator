using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Capture;
using SLiveTranslator.Models;
using SLiveTranslator.Ocr;
using SLiveTranslator.Translation;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.Services;

/// <summary>Receives dialog lines from <see cref="SubtitleWatcher"/>. Called from background threads.</summary>
public interface ISubtitleSink
{
    /// <summary>A new line appeared (or a shown line grew); its translation follows later.</summary>
    /// <param name="lowConfidence">OCR never read the line cleanly; it is shown anyway so it is not lost.</param>
    void LineDetected(int id, string original, bool lowConfidence);
    /// <summary>
    /// A translation of the line. It may be of an earlier reading when the line grew meanwhile (shown until the
    /// newer one arrives); <paramref name="lowConfidence"/> belongs to the reading that was translated.
    /// </summary>
    void LineTranslated(int id, string translated, bool lowConfidence);
    /// <summary>Translating the line failed; it is retried once, so a translation may still follow.</summary>
    void LineFailed(int id, string original, string error);
    /// <summary>The game's subtitle disappeared.</summary>
    void Cleared();
    void Error(string message);
    /// <summary>A hint about the setup, e.g. the region cuts the subtitles off.</summary>
    void Notice(string message);
}

/// <summary>
/// Watches the subtitle region and reports every dialog line. Reading the screen never waits for translation:
/// lines are translated in the background (several at once), so fast dialog is not skipped.
/// </summary>
public sealed class SubtitleWatcher : IDisposable
{
    const int ConfirmDelayMs = 100;
    const int MaxParallelTranslations = 3;
    const int ErrorsRetriedAtInterval = 2;
    const double MinGapRatio = 0.7, MaxGapRatio = 2.5;
    const int EdgeMarginPx = 2;
    const int ForcedReadMs = 900; // at least once a second at the default 300 ms poll
    const int LinesKept = 8;
    static readonly TimeSpan TranslateRetryDelay = TimeSpan.FromSeconds(6);

    readonly Func<IOcrEngine> _ocr;
    readonly Func<ITranslator> _translator;
    readonly Func<(string Src, string Tgt, int IntervalMs, double GapRatio)> _config;
    readonly ISubtitleSink _sink;
    readonly SemaphoreSlim _translateGate = new(MaxParallelTranslations);
    readonly ConcurrentDictionary<int, LineState> _lines = new();
    CancellationTokenSource? _cts;
    int _session;

    /// <param name="config">
    /// Languages, poll interval, and the largest gap between two lines of one subtitle in line heights
    /// (<see cref="SubtitleSettings.ParagraphGapRatio"/>).
    /// </param>
    public SubtitleWatcher(Func<IOcrEngine> ocr, Func<ITranslator> translator, Func<(string Src, string Tgt, int IntervalMs, double GapRatio)> config,
        ISubtitleSink sink)
    {
        _ocr = ocr;
        _translator = translator;
        _config = config;
        _sink = sink;
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>The region the user selected; the overlay is anchored to it. A wider strip is read, see <see cref="CaptureRect"/>.</summary>
    public Rectangle Region { get; private set; }

    /// <summary>Waits before a translation retry or a debounced update; tests replace it to run without real delays.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Lines whose translation state is kept; older lines can no longer change.</summary>
    internal int TrackedLines => _lines.Count;

    public void Start(Rectangle region)
    {
        Stop();
        Region = region;
        _lines.Clear();
        var session = CreateSession(region, CaptureRect(region, Forms.Screen.FromRectangle(region).Bounds));
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => LoopAsync(session, ct), ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The full monitor width at the region's height. Subtitle lines are centred and often wider than the region the
    /// user drew; reading the whole width and keeping the lines that overlap the region never cuts a line off.
    /// </summary>
    internal static Rectangle CaptureRect(Rectangle region, Rectangle screen)
    {
        int left = Math.Min(region.Left, screen.Left), right = Math.Max(region.Right, screen.Right);
        return new Rectangle(left, region.Top, right - left, region.Height);
    }

    internal Session CreateSession(Rectangle region, Rectangle capture) => new(this, Interlocked.Increment(ref _session), region, capture);

    async Task LoopAsync(Session session, CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            long started = Environment.TickCount64;
            int interval = _config().IntervalMs;
            try
            {
                using (var frame = ScreenCapture.Capture(session.Capture))
                    await session.ProcessFrameAsync(frame, Environment.TickCount64, ct);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warn("subtitle loop: " + ex.Message);
                if (failures > ErrorsRetriedAtInterval) _sink.Error(ex.Message); // one OCR hiccup is not worth a message
            }

            try { await Task.Delay(NextDelayMs(failures, session.AwaitingConfirmation, interval, (int)(Environment.TickCount64 - started)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// A changed line is confirmed quickly. The first errors in a row retry at the normal interval (a single OCR
    /// failure must not cost a dialog line); after that wait 2s, 4s, 8s ... up to 30s.
    /// </summary>
    internal static int NextDelayMs(int failures, bool awaitingConfirmation, int intervalMs, int elapsedMs)
    {
        int wait = failures > ErrorsRetriedAtInterval ? Math.Min(30_000, 2_000 << Math.Min(failures - ErrorsRetriedAtInterval - 1, 4))
            : failures == 0 && awaitingConfirmation ? Math.Min(ConfirmDelayMs, intervalMs)
            : intervalMs - elapsedMs;
        return Math.Max(20, wait);
    }

    /// <param name="Event">What the tracker made of the frame.</param>
    /// <param name="Reading">The reading used for this frame (the last OCR result when the frame did not change).</param>
    /// <param name="OcrRan">False when the frame looked unchanged and the last reading was reused.</param>
    /// <param name="TouchesEdge">Text runs into the top or bottom of the captured strip.</param>
    internal sealed record FrameResult(SubtitleTracker.Event? Event, string Reading, bool OcrRan, bool TouchesEdge);

    sealed record Reading(List<OcrLine> Lines, string Text, bool TouchesEdge);

    /// <summary>State of one subtitle session: everything the loop does with a frame, minus capture and timing.</summary>
    internal sealed class Session
    {
        readonly SubtitleWatcher _owner;
        readonly SubtitleTracker _tracker = new();
        readonly Rectangle _regionInCapture;
        byte[]? _lastSignature;
        long _lastReadAt;
        Reading _last = new(new List<OcrLine>(), "", false);
        double _upscale = 1.0;
        int _edgeLines;

        internal Session(SubtitleWatcher owner, int id, Rectangle region, Rectangle capture)
        {
            _owner = owner;
            Id = id;
            Capture = capture;
            _regionInCapture = new Rectangle(region.X - capture.X, region.Y - capture.Y, region.Width, region.Height);
        }

        internal int Id { get; }

        /// <summary>The screen rectangle to capture for each frame.</summary>
        internal Rectangle Capture { get; }

        internal bool AwaitingConfirmation => _tracker.AwaitingConfirmation;

        /// <param name="frame">A capture of <see cref="Capture"/>.</param>
        internal async Task<FrameResult> ProcessFrameAsync(Bitmap frame, long nowMs, CancellationToken ct)
        {
            // Only the user's region decides whether the frame changed: a centred subtitle always changes pixels
            // there, and motion beside it costs no OCR. A small change ("Yes." → "No.") on a still scene can stay
            // below the thresholds, so text on screen is read again every second.
            var signature = FrameSignature(frame, _regionInCapture);
            bool ocrRan = _lastSignature is null || Changed(signature, _lastSignature)
                          || (_last.Text.Length > 0 && nowMs - _lastReadAt >= ForcedReadMs);
            if (ocrRan)
            {
                _last = await _owner.ReadAsync(frame, _regionInCapture, _upscale, ct);
                _lastSignature = signature; // only after a successful read, so a failed frame is read again
                _lastReadAt = nowMs;
                _upscale = NextUpscale(_last.Lines, _upscale);
            }

            var e = _tracker.Observe(_last.Text, nowMs, fresh: ocrRan);
            switch (e)
            {
                case SubtitleTracker.NewLine line:
                    _owner.Emit(Id, line.Id, line.Text, line.LowConfidence, update: false, ct);
                    if (_last.TouchesEdge && ++_edgeLines == 2)
                        _owner._sink.Notice("Subtitles are cut off at the top or bottom of the subtitle region, so lines are lost. Select the subtitle region again.");
                    break;
                case SubtitleTracker.LineUpdated line:
                    _owner.Emit(Id, line.Id, line.Text, line.LowConfidence, update: true, ct);
                    break;
                case SubtitleTracker.Cleared:
                    _owner._sink.Cleared();
                    break;
            }
            return new FrameResult(e, _last.Text, ocrRan, _last.TouchesEdge);
        }
    }

    /// <summary>
    /// Reads the frame as it is and as a white-text mask, and keeps the better reading. The mask removes the
    /// moving scene behind white subtitles; the plain reading covers games whose subtitles are not white.
    /// Only lines over the user's region count, so key hints beside it ("Space Skip") are left out.
    /// Separate text blocks (subtitle, a dialog choice, a key hint) are kept on separate lines.
    /// </summary>
    async Task<Reading> ReadAsync(Bitmap frame, Rectangle region, double upscale, CancellationToken ct)
    {
        var ocr = _ocr();
        var plain = OverRegion(await ocr.RecognizeAsync(frame, ct, upscale), region);
        List<OcrLine> masked;
        using (var mask = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb))
        {
            ImageFilters.WhiteTextMask(frame, mask);
            masked = OverRegion(await ocr.RecognizeAsync(mask, ct, upscale), region);
        }
        (plain, masked) = (WithoutStrayNoise(plain, masked), WithoutStrayNoise(masked, plain));

        double gapRatio = Math.Clamp(_config().GapRatio, MinGapRatio, MaxGapRatio);
        string plainText = ToText(plain, gapRatio), maskedText = ToText(masked, gapRatio);
        bool useMask = TextUtil.PickReading(maskedText, plainText) == maskedText;
        var lines = useMask ? masked : plain;
        return new Reading(lines, useMask ? maskedText : plainText, TouchesEdge(lines, frame.Height));
    }

    static List<OcrLine> OverRegion(IReadOnlyList<OcrLine> lines, Rectangle region) =>
        lines.Where(l => IsReadable(l) && l.Bounds.Left < region.Right && l.Bounds.Right > region.Left).ToList();

    /// <summary>
    /// Drops the lines that only this reading has and that look like noise, as long as the other reading saw clean
    /// text: scene texture one image shows and the other does not. Otherwise the reading with the extra noise line
    /// would win for being longer ("Yes." against "Yes." plus "uams@Utothe").
    /// </summary>
    static List<OcrLine> WithoutStrayNoise(List<OcrLine> lines, List<OcrLine> other)
    {
        if (!other.Any(o => !LooksLikeNoise(o.Text))) return lines;
        return lines.Where(l => !LooksLikeNoise(l.Text) || other.Any(o => o.Bounds.IntersectsWith(l.Bounds))).ToList();
    }

    static bool LooksLikeNoise(string text) => text.Any(TextUtil.IsJunk) || !SubtitleTracker.LooksReadable(text);

    static string ToText(List<OcrLine> lines, double gapRatio) =>
        string.Join("\n", TextUtil.GroupParagraphs(lines, gapRatio, keepNumberedApart: true)
            .Select(p => TextUtil.RepairContractions(TextUtil.Normalize(p.Text)))
            .Where(t => t.Length > 0));

    /// <summary>
    /// Text cut by the top or bottom of the capture: a line box that reaches the edge. The capture spans the
    /// monitor's width, so a centred subtitle is never cut at the sides; a region drawn too short cuts the first or
    /// last line of a two-line subtitle, which selecting the region again fixes.
    /// </summary>
    internal static bool TouchesEdge(IReadOnlyList<OcrLine> lines, int frameHeight) =>
        lines.Any(l => l.Bounds.Top <= EdgeMarginPx || l.Bounds.Bottom >= frameHeight - EdgeMarginPx);

    sealed class LineState
    {
        public int Version;
        public string Text = "";
        public string? Sent;
        /// <summary>Version whose translation the sink got last; an older translation arriving later is stale.</summary>
        public int Delivered;
    }

    /// <summary>Reports a line and translates it in the background; an update waits briefly for the line to settle.</summary>
    internal void Emit(int session, int localId, string text, bool lowConfidence, bool update, CancellationToken ct)
    {
        int id = session * 1_000_000 + localId; // unique across start/stop
        var state = _lines.GetOrAdd(id, _ => new LineState());
        // Only the newest line can still grow; a running translation keeps its own reference to the state.
        if (!update) _lines.TryRemove(id - LinesKept, out _);
        int version;
        lock (state)
        {
            version = ++state.Version;
            state.Text = text;
        }
        _sink.LineDetected(id, text, lowConfidence);
        var (src, tgt, intervalMs, _) = _config();
        _ = TranslateLineAsync(id, state, version, text, lowConfidence, update ? UpdateDebounce(intervalMs) : null, src, tgt, ct);
    }

    /// <summary>
    /// A grown line is reported again one poll and its confirming frame later at the earliest; waiting a little
    /// longer than that translates only the reading a line growing step by step settles on.
    /// </summary>
    internal static TimeSpan UpdateDebounce(int intervalMs) => TimeSpan.FromMilliseconds(Math.Max(250, intervalMs + 2 * ConfirmDelayMs));

    async Task TranslateLineAsync(int id, LineState state, int version, string text, bool lowConfidence, TimeSpan? debounce,
        string src, string tgt, CancellationToken ct)
    {
        try
        {
            if (debounce is { } wait) await Delay(wait, ct);
            lock (state)
            {
                if (state.Version != version || state.Sent == text) return; // a newer reading follows, or nothing changed
                state.Sent = text;
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var result = await TranslateAsync(text, src, tgt, ct);
                    // An earlier reading's translation is still shown until the newer one arrives, never after it.
                    if (MarkDelivered(state, version)) _sink.LineTranslated(id, result, lowConfidence);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) // a timeout is a failure, not "subtitles off"
                {
                    // The same failure repeats on every line while a service is paused: one log line a minute is enough.
                    string line = "subtitle translate: " + ex.Message;
                    Log.Once(line, line, TimeSpan.FromMinutes(1), LogLevel.Warn);
                    if (!IsShown(state, text)) return;
                    _sink.LineFailed(id, text, ex.Message);
                    if (attempt == 2) return;
                }
                await Delay(TranslateRetryDelay, ct);
                if (!IsShown(state, text)) return;
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // subtitles were turned off
        }
    }

    static bool MarkDelivered(LineState state, int version)
    {
        lock (state)
        {
            if (version <= state.Delivered) return false;
            state.Delivered = version;
            return true;
        }
    }

    async Task<string> TranslateAsync(string text, string src, string tgt, CancellationToken ct)
    {
        await _translateGate.WaitAsync(ct);
        try
        {
            // Each text block is translated on its own, so a key hint never gets mixed into a sentence.
            return string.Join("\n", await _translator().TranslateAsync(text.Split('\n'), src, tgt, ct));
        }
        finally
        {
            _translateGate.Release();
        }
    }

    /// <summary>The line still shows this text, so its translation is wanted (an update may have replaced it).</summary>
    static bool IsShown(LineState state, string text)
    {
        lock (state) return state.Text == text;
    }

    /// <summary>Drops OCR noise from the game scene: needs at least two letters and mostly letters.</summary>
    internal static bool IsReadable(OcrLine line)
    {
        int letters = line.Text.Count(char.IsLetter);
        int visible = line.Text.Count(c => !char.IsWhiteSpace(c));
        return letters >= 2 && letters >= visible * 0.5;
    }

    /// <summary>Subtitles are usually big enough to read at 1x (much faster); small text gets 2x.</summary>
    static double NextUpscale(List<OcrLine> lines, double current)
    {
        if (lines.Count == 0) return current;
        var heights = lines.Select(l => l.Bounds.Height).OrderBy(h => h).ToList();
        int median = heights[heights.Count / 2];
        return median < 16 ? 2.0 : median > 24 ? 1.0 : current;
    }

    /// <summary>Tiny grayscale thumbnail used to detect whether the region changed since the last frame.</summary>
    /// <param name="area">The part of the frame to sample; the whole frame when omitted.</param>
    internal static byte[] FrameSignature(Bitmap frame, Rectangle? area = null)
    {
        const int W = 64, H = 16;
        using var small = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(frame, new Rectangle(0, 0, W, H), area ?? new Rectangle(0, 0, frame.Width, frame.Height), GraphicsUnit.Pixel);
        }
        var data = small.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * H];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            var signature = new byte[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int o = y * data.Stride + x * 4;
                    signature[y * W + x] = (byte)((pixels[o] * 114 + pixels[o + 1] * 587 + pixels[o + 2] * 299) / 1000);
                }
            }
            return signature;
        }
        finally
        {
            small.UnlockBits(data);
        }
    }

    internal static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    /// <summary>
    /// The region changed: on average (the scene moved), or strongly in one cell (a line appeared or changed on a
    /// still scene, which moves the average of the whole region by a fraction of a level).
    /// </summary>
    internal static bool Changed(byte[] a, byte[] b)
    {
        if (MeanAbsDiff(a, b) >= 1.0) return true;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 12) return true;
        return false;
    }
}
