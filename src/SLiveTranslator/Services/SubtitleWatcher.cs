using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
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
    /// <param name="provider">The provider key the translation came from (<see cref="ProviderKeys"/>); "user" for a hand fix.</param>
    void LineTranslated(int id, string translated, bool lowConfidence, string provider);
    /// <summary>Translating the line failed; it is retried once, so a translation may still follow.</summary>
    void LineFailed(int id, string original, string error);
    /// <summary>The game's subtitle disappeared.</summary>
    void Cleared();
    /// <summary>Reading stopped because the game is not in front (<see cref="WatcherConfig.PauseWhenGameUnfocused"/>), or started again.</summary>
    void Paused(bool paused);
    void Error(string message);
    /// <summary>A hint about the setup, e.g. the region cuts the subtitles off.</summary>
    void Notice(string message);
}

/// <summary>The settings the watcher reads on every poll, so a change applies without a restart.</summary>
/// <param name="GapRatio">
/// The largest gap between two lines of one subtitle in line heights (<see cref="SubtitleSettings.ParagraphGapRatio"/>).
/// </param>
public sealed record WatcherConfig(string Src, string Tgt, int IntervalMs, double GapRatio)
{
    public OcrMode OcrMode { get; init; } = OcrMode.Auto;

    /// <summary>Read only while the game (the program that shows the region) is in front; see <see cref="PollingPolicy"/>.</summary>
    public bool PauseWhenGameUnfocused { get; init; }

    /// <summary>Run at below-normal priority while subtitles are on; read at start.</summary>
    public bool LowImpact { get; init; }

    /// <summary>Earlier lines sent along with each new one (<see cref="TranslationSettings.ContextLines"/>); 0 sends none.</summary>
    public int ContextLines { get; init; } = 6;
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
    const int ForcedReadMs = 1000; // text on screen is read again at least this often
    const int LinesKept = 8;
    /// <summary>Fewer white pixels in the region than one letter has: the mask shows no text.</summary>
    const int MinTextPixels = 24;
    /// <summary>Rows kept above and below the text, in line heights.</summary>
    const double CropMargin = 0.6;
    /// <summary>Empty rows a run of text rows may have inside one line (the dot of an i, a thin stroke).</summary>
    const int MaxGapInLine = 3;
    static readonly TimeSpan TranslateRetryDelay = TimeSpan.FromSeconds(6);

    readonly Func<IOcrEngine> _ocr;
    readonly Func<ITranslator> _translator;
    readonly Func<WatcherConfig> _config;
    readonly ISubtitleSink _sink;
    readonly SemaphoreSlim _translateGate = new(MaxParallelTranslations);
    readonly ConcurrentDictionary<int, LineState> _lines = new();
    readonly ContextWindow _context = new();
    CancellationTokenSource? _cts;
    LowPriorityScope? _lowPriority;
    PollingPolicy? _polling;
    (int Id, string? Name) _gameProcess;
    int _session;

    public SubtitleWatcher(Func<IOcrEngine> ocr, Func<ITranslator> translator, Func<WatcherConfig> config, ISubtitleSink sink)
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

    /// <summary>Tells which window is in front, for <see cref="WatcherConfig.PauseWhenGameUnfocused"/>.</summary>
    internal IWindowProbe Windows { get; init; } = SystemWindowProbe.Instance;

    /// <summary>Which window is the game, and whether it is in front; made by <see cref="Start"/>, or by a test.</summary>
    internal PollingPolicy? Polling
    {
        get => _polling;
        init => _polling = value;
    }

    /// <summary>The game profile for the game's process name (null while the game is not known); null for none.</summary>
    public Func<string?, GameProfile?> Profiles { get; init; } = _ => null;

    /// <summary>Milliseconds on a steady clock, for the silences that end a conversation; tests replace it.</summary>
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;

    /// <summary>Lines whose translation state is kept; older lines can no longer change.</summary>
    internal int TrackedLines => _lines.Count;

    /// <param name="keepGame">
    /// Subtitles were on before a dialog (settings, region select): the game found then is kept while it runs, since
    /// whatever window the closing dialog left in front tells nothing about it.
    /// </param>
    public void Start(Rectangle region, bool keepGame = false)
    {
        var known = keepGame ? _polling?.Game : null;
        Stop();
        if (region != Region) _context.Clear(); // another place on screen: another conversation
        Region = region;
        _lines.Clear();
        var polling = _polling = new PollingPolicy(Windows, Environment.ProcessId, region, known);
        if (_config().LowImpact) _lowPriority = new LowPriorityScope();
        var session = CreateSession(region, CaptureRect(region, Forms.Screen.FromRectangle(region).Bounds));
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => LoopAsync(session, polling, ct), CancellationToken.None); // always runs, so the session is disposed
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _lowPriority?.Dispose();
        _lowPriority = null;
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

    /// <summary>The profile for the game that shows the region, or the one the settings name.</summary>
    GameProfile? Profile()
    {
        string? name = null;
        if (_polling?.Game is { } game)
        {
            var known = _gameProcess;
            if (known.Id != game.ProcessId) _gameProcess = known = (game.ProcessId, Windows.ProcessName(game.ProcessId));
            name = known.Name;
        }
        return Profiles(name);
    }

    async Task LoopAsync(Session session, PollingPolicy polling, CancellationToken ct)
    {
        using var frames = new BitmapBuffer(); // one capture bitmap for the whole session
        using (session)
        {
            int failures = 0;
            bool away = false;
            while (!ct.IsCancellationRequested)
            {
                long started = Environment.TickCount64;
                var config = _config();
                int delay;
                if (polling.GameAway(config.PauseWhenGameUnfocused))
                {
                    // Nothing to read while another program is in front; only look whether the game is back.
                    if (!away)
                    {
                        session.Pause(started);
                        _sink.Paused(true);
                    }
                    away = true;
                    delay = PollingPolicy.AwayIntervalMs;
                }
                else
                {
                    if (away) _sink.Paused(false);
                    away = false;
                    try
                    {
                        var frame = ScreenCapture.Capture(session.Capture, frames);
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
                    delay = NextDelayMs(failures, session.AwaitingConfirmation, config.IntervalMs, (int)(Environment.TickCount64 - started));
                }

                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
            }
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
    /// <param name="Recognitions">OCR calls this frame cost: 0 to 2.</param>
    internal sealed record FrameResult(SubtitleTracker.Event? Event, string Reading, bool OcrRan, bool TouchesEdge, int Recognitions);

    sealed record Reading(IReadOnlyList<OcrLine> Lines, string Text, bool TouchesEdge)
    {
        public static readonly Reading Empty = new(Array.Empty<OcrLine>(), "", false);
    }

    /// <summary>
    /// State of one subtitle session: everything the loop does with a frame, minus capture and timing. Owns the
    /// buffers of the mask and the crop, so a frame costs no new pixel memory.
    /// </summary>
    internal sealed class Session : IDisposable
    {
        readonly SubtitleWatcher _owner;
        readonly SubtitleTracker _tracker = new();
        readonly OcrSourcePolicy _sources = new();
        readonly Rectangle _regionInCapture;
        readonly BitmapBuffer _mask = new(), _crop = new();
        bool _readNext = true; // nothing read yet, or the game was away: the next frame is read whatever it shows
        byte[]? _lastMask, _lastPixels; // the signatures of the last frame that was read (null: not taken)
        long _lastReadAt;
        Reading _last = Reading.Empty;
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
            var config = _owner._config();
            // The mask comes first: it keeps only the white text, so it changes when the subtitle does and not when
            // the scene behind it moves. The region's pixels decide where the mask cannot tell: without white text in
            // the region, and while the white it shows reads as nothing (scenery, or a subtitle it cannot read), since
            // text of another colour changes only the pixels. Once the mask has shown that it reads these subtitles
            // (it kept winning, or Mask mode), it alone decides, so a moving scene costs no OCR with or without a
            // subtitle. Only the region counts, so motion beside it costs nothing. A change too small to notice on a
            // still scene, or a line of another colour changing beside a white one, is caught by reading text on
            // screen again every second.
            bool maskSettled = config.OcrMode == OcrMode.Mask || _sources.Single == OcrSource.Mask;
            MaskStats? stats = config.OcrMode == OcrMode.Plain ? null
                : ImageFilters.WhiteTextMask(frame, _mask.Get(frame.Width, frame.Height), _regionInCapture.Left, _regionInCapture.Right);
            bool maskHasText = stats is not null && HasText(stats);
            byte[]? mask = stats is not null && (maskSettled || maskHasText) ? stats.Signature : null;
            byte[]? pixels = !maskSettled && (!maskHasText || _last.Text.Length == 0) ? FrameSignature(frame, _regionInCapture) : null;
            bool maskChanged = SignatureChanged(mask, _lastMask), pixelsChanged = pixels is not null && SignatureChanged(pixels, _lastPixels);
            bool ocrRan = _readNext || maskChanged || pixelsChanged
                          || (_last.Text.Length > 0 && nowMs - _lastReadAt >= ForcedReadMs)
                          // Until two readings of a line agree, it is read again whenever anything moved at all: on a
                          // moving scene that is another independent reading; on a still one it would repeat the last.
                          || (_tracker.Unsettled && (SignatureDiffers(mask, _lastMask) || (pixels is not null && SignatureDiffers(pixels, _lastPixels))));
            int recognitions = 0;
            if (ocrRan)
            {
                // Only the pixels changed: what changed is not white text, so it may be outside the rows of the white.
                (_last, recognitions) = await ReadAsync(frame, stats, config, wholePlain: pixelsChanged && !maskChanged, ct);
                // Only after a successful read, so a failed frame is read again. The pixels are kept even while the
                // mask alone decides, to compare with if it stops winning.
                _readNext = false;
                _lastMask = mask;
                _lastPixels = pixels ?? (config.OcrMode == OcrMode.Mask ? null : FrameSignature(frame, _regionInCapture));
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
            return new FrameResult(e, _last.Text, ocrRan, _last.TouchesEdge, recognitions);
        }

        /// <summary>The game went to the background: what it showed counts as gone, and its first frame back is read anew.</summary>
        internal void Pause(long nowMs)
        {
            _readNext = true;
            _last = Reading.Empty;
            if (_tracker.Leave(nowMs) is SubtitleTracker.Cleared) _owner._sink.Cleared();
        }

        static bool HasText(MaskStats stats) => stats.WhitePixels >= MinTextPixels;

        /// <summary>
        /// Recognizes the frame as it is and as its white-text mask, or only the one that keeps winning (see
        /// <see cref="OcrSourcePolicy"/>), and keeps the better reading. The mask removes the moving scene behind white
        /// subtitles; the plain frame covers subtitles of another colour. Only the rows with white text are read, at the
        /// full width: OCR takes longer and misses more on a bigger, busier image. Only lines over the user's region
        /// count, so key hints beside it ("Space Skip") are left out. Separate text blocks (subtitle, a dialog choice,
        /// a key hint) are kept on separate lines.
        /// </summary>
        /// <param name="wholePlain">Read all rows of the plain frame: the change was not in the white text.</param>
        async Task<(Reading Reading, int Recognitions)> ReadAsync(Bitmap frame, MaskStats? stats, WatcherConfig config, bool wholePlain, CancellationToken ct)
        {
            var ocr = _owner._ocr();
            var names = _owner.Profile()?.Names; // their possessives ("Cirils") are repaired
            bool maskHasText = stats is not null && HasText(stats);
            var (top, height) = maskHasText ? TextRows(stats!.RowHistogram) : (0, frame.Height);
            Bitmap? mask = maskHasText ? _mask.Rows(top, height) : null;
            int plainTop = wholePlain ? 0 : top;
            // Once the mask keeps winning, a frame without white text has no text: the plain frame would read the scene.
            Bitmap? plain = config.OcrMode == OcrMode.Mask || (mask is null && _sources.Single == OcrSource.Mask) ? null
                : wholePlain || height == frame.Height ? frame : _crop.CopyRows(frame, top, height);
            double gapRatio = Math.Clamp(config.GapRatio, MinGapRatio, MaxGapRatio);
            int recognitions = 0;

            async Task<List<OcrLine>> Recognize(Bitmap image)
            {
                recognitions++;
                return OverRegion(await ocr.RecognizeAsync(image, ct, _upscale), image == mask ? top : plainTop);
            }

            Reading Result(List<OcrLine> lines, string text) => new(lines, text, TouchesEdge(lines, frame.Height));

            if (mask is null || plain is null)
            {
                var lines = (mask ?? plain) is { } only ? await Recognize(only) : new List<OcrLine>();
                return (Result(lines, ToText(lines, gapRatio, names)), recognitions);
            }

            List<OcrLine>? plainLines = null, maskLines = null;
            if (_sources.Single is { } single)
            {
                var lines = await Recognize(single == OcrSource.Mask ? mask : plain);
                string text = ToText(lines, gapRatio, names);
                if (_sources.Accepts(text, lines))
                {
                    _sources.Kept(text, lines.Count);
                    return (Result(lines, text), recognitions);
                }
                if (single == OcrSource.Mask) maskLines = lines;
                else plainLines = lines;
            }
            plainLines ??= await Recognize(plain);
            maskLines ??= await Recognize(mask);

            (plainLines, maskLines) = (WithoutStrayNoise(plainLines, maskLines), WithoutStrayNoise(maskLines, plainLines));
            string plainText = ToText(plainLines, gapRatio, names), maskText = ToText(maskLines, gapRatio, names);
            bool useMask = TextUtil.PickReading(maskText, plainText, config.Src) == maskText;
            var (winner, winnerText, winnerLines) = useMask ? (OcrSource.Mask, maskText, maskLines) : (OcrSource.Plain, plainText, plainLines);
            _sources.Won(winner, winnerText, winnerLines.Count);
            return (Result(winnerLines, winnerText), recognitions);
        }

        /// <summary>The readable lines over the user's region, moved from the rows that were read back into the capture.</summary>
        List<OcrLine> OverRegion(IReadOnlyList<OcrLine> lines, int top)
        {
            var result = new List<OcrLine>(lines.Count);
            foreach (var line in lines)
            {
                if (!IsReadable(line) || line.Bounds.Left >= _regionInCapture.Right || line.Bounds.Right <= _regionInCapture.Left) continue;
                result.Add(top == 0 ? line : line with { Bounds = line.Bounds with { Y = line.Bounds.Y + top } });
            }
            return result;
        }

        public void Dispose()
        {
            _mask.Dispose();
            _crop.Dispose();
        }
    }

    /// <summary>
    /// The rows to recognize: from the first to the last row with white text, widened by
    /// <see cref="CropMargin"/> of a line height so the glyphs keep some background around them. OCR time grows with
    /// the pixels, and a subtitle fills only part of the strip. The line height is the tallest run of rows with text.
    /// </summary>
    internal static (int Top, int Height) TextRows(int[] rowInk)
    {
        int first = -1, last = -1, runStart = -1, tallest = 0;
        for (int y = 0; y < rowInk.Length; y++)
        {
            if (rowInk[y] == 0) continue;
            if (first < 0) first = y;
            if (runStart < 0 || y - last > MaxGapInLine + 1) runStart = y;
            last = y;
            tallest = Math.Max(tallest, last - runStart + 1);
        }
        if (first < 0) return (0, rowInk.Length);
        int margin = (int)Math.Ceiling(tallest * CropMargin);
        int top = Math.Max(0, first - margin), bottom = Math.Min(rowInk.Length, last + 1 + margin);
        return (top, bottom - top);
    }

    /// <summary>
    /// Drops the lines that only this reading has and that look like noise, as long as the other reading saw clean
    /// text: scene texture one image shows and the other does not. Otherwise the reading with the extra noise line
    /// would win for being longer ("Yes." against "Yes." plus "uams@Utothe").
    /// </summary>
    static List<OcrLine> WithoutStrayNoise(List<OcrLine> lines, List<OcrLine> other)
    {
        if (!other.Any(o => !OcrSourcePolicy.LooksLikeNoise(o.Text))) return lines;
        return lines.Where(l => !OcrSourcePolicy.LooksLikeNoise(l.Text) || other.Any(o => o.Bounds.IntersectsWith(l.Bounds))).ToList();
    }

    static string ToText(List<OcrLine> lines, double gapRatio, IReadOnlyList<string>? names) =>
        string.Join("\n", TextUtil.GroupParagraphs(lines, gapRatio, keepNumberedApart: true)
            .Select(p => TextUtil.RepairContractions(TextUtil.Normalize(p.Text), names))
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
        _context.LineSeen(id, Clock());
        var config = _config();
        var job = new TranslationJob(id, text, lowConfidence, config.Src, config.Tgt, Profile(), config.ContextLines);
        _ = TranslateLineAsync(job, state, version, update ? UpdateDebounce(config.IntervalMs) : null, ct);
    }

    /// <summary>One reading of a line to translate, with what it is translated with.</summary>
    sealed record TranslationJob(int Id, string Text, bool LowConfidence, string Src, string Tgt, GameProfile? Profile, int ContextLines);

    /// <summary>
    /// A grown line is reported again one poll and its confirming frame later at the earliest; waiting a little
    /// longer than that translates only the reading a line growing step by step settles on.
    /// </summary>
    internal static TimeSpan UpdateDebounce(int intervalMs) => TimeSpan.FromMilliseconds(Math.Max(250, intervalMs + 2 * ConfirmDelayMs));

    async Task TranslateLineAsync(TranslationJob job, LineState state, int version, TimeSpan? debounce, CancellationToken ct)
    {
        var (id, text, lowConfidence) = (job.Id, job.Text, job.LowConfidence);
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
                    var (translated, provider) = await TranslateAsync(job, ct);
                    // An earlier reading's translation is still shown until the newer one arrives, never after it.
                    if (MarkDelivered(state, version))
                    {
                        _context.Shown(id, text, translated);
                        _sink.LineTranslated(id, translated, lowConfidence, provider);
                    }
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

    /// <returns>The translation and the provider key it came from.</returns>
    async Task<(string Text, string Provider)> TranslateAsync(TranslationJob job, CancellationToken ct)
    {
        await _translateGate.WaitAsync(ct);
        try
        {
            // Each text block is translated on its own, so a key hint never gets mixed into a sentence. The context is
            // taken now, after the wait, so it holds every line shown meanwhile.
            var translator = _translator();
            var request = new TranslateRequest(job.Text.Split('\n'), job.Src, job.Tgt, _context.Before(job.Id, job.ContextLines), job.Profile);
            var result = await translator.TranslateAsync(request, ct);
            return (string.Join("\n", result.Texts), result.Provider ?? translator.Name);
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
    static double NextUpscale(IReadOnlyList<OcrLine> lines, double current)
    {
        if (lines.Count == 0) return current;
        var heights = lines.Select(l => l.Bounds.Height).OrderBy(h => h).ToList();
        int median = heights[heights.Count / 2];
        return median < 16 ? 2.0 : median > 24 ? 1.0 : current;
    }

    /// <summary>Tiny greyscale thumbnail (the mean of each cell) used to detect whether the area changed since the last frame.</summary>
    /// <param name="area">The part of the frame to sample; the whole frame when omitted.</param>
    internal static unsafe byte[] FrameSignature(Bitmap frame, Rectangle? area = null)
    {
        const int W = 64, H = 16;
        var whole = new Rectangle(0, 0, frame.Width, frame.Height);
        var a = Rectangle.Intersect(area ?? whole, whole);
        var signature = new byte[W * H];
        if (a.Width == 0 || a.Height == 0) return signature;

        Span<long> sums = stackalloc long[W * H];
        Span<int> counts = stackalloc int[W * H];
        var data = frame.LockBits(a, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < a.Height; y++)
            {
                byte* p = (byte*)data.Scan0 + (long)y * data.Stride;
                int cellRow = y * H / a.Height * W;
                for (int x = 0; x < a.Width; x++, p += 4)
                {
                    int cell = cellRow + x * W / a.Width;
                    sums[cell] += p[0] * 114 + p[1] * 587 + p[2] * 299;
                    counts[cell]++;
                }
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }
        for (int i = 0; i < signature.Length; i++) signature[i] = (byte)(sums[i] / (1000L * Math.Max(1, counts[i])));
        return signature;
    }

    internal static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    /// <summary>
    /// The area changed: on average (the scene moved), or strongly in one cell (a line appeared or changed on a
    /// still scene, which moves the average of the whole area by a fraction of a level).
    /// </summary>
    internal static bool Changed(byte[] a, byte[] b)
    {
        if (MeanAbsDiff(a, b) >= 1.0) return true;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 12) return true;
        return false;
    }

    /// <summary>A signature taken on one frame and not on the other is a change: white text appeared or went.</summary>
    static bool SignatureChanged(byte[]? now, byte[]? last) => now is null || last is null ? now != last : Changed(now, last);

    static bool SignatureDiffers(byte[]? now, byte[]? last) => now is null || last is null ? now != last : !now.AsSpan().SequenceEqual(last);
}
