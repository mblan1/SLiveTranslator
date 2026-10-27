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

namespace SLiveTranslator.Services;

/// <summary>Receives dialog lines from <see cref="SubtitleWatcher"/>. Called from background threads.</summary>
public interface ISubtitleSink
{
    /// <summary>A new line appeared (or a shown line grew); its translation follows later.</summary>
    void LineDetected(int id, string original);
    void LineTranslated(int id, string translated);
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

    readonly Func<IOcrEngine> _ocr;
    readonly Func<ITranslator> _translator;
    readonly Func<(string Src, string Tgt, int IntervalMs)> _config;
    readonly ISubtitleSink _sink;
    readonly SemaphoreSlim _translateGate = new(MaxParallelTranslations);
    readonly ConcurrentDictionary<int, int> _versions = new();
    CancellationTokenSource? _cts;
    int _session;

    public SubtitleWatcher(Func<IOcrEngine> ocr, Func<ITranslator> translator, Func<(string Src, string Tgt, int IntervalMs)> config, ISubtitleSink sink)
    {
        _ocr = ocr;
        _translator = translator;
        _config = config;
        _sink = sink;
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public Rectangle Region { get; private set; }

    public void Start(Rectangle region)
    {
        Stop();
        Region = region;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int session = ++_session;
        _ = Task.Run(() => LoopAsync(region, session, ct), ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void Dispose() => Stop();

    async Task LoopAsync(Rectangle region, int session, CancellationToken ct)
    {
        var tracker = new SubtitleTracker();
        string lastOcr = "";
        bool lastTouchesEdge = false;
        byte[]? lastSignature = null;
        double upscale = 1.0;
        int failures = 0, edgeLines = 0;

        while (!ct.IsCancellationRequested)
        {
            long started = Environment.TickCount64;
            var (src, tgt, interval) = _config();
            try
            {
                string text;
                using (var frame = ScreenCapture.Capture(region))
                {
                    var signature = FrameSignature(frame);
                    if (lastSignature is not null && MeanAbsDiff(signature, lastSignature) < 1.0)
                    {
                        text = lastOcr; // pixels did not change, reuse the last OCR result
                    }
                    else
                    {
                        lastSignature = signature;
                        var (lines, reading) = await ReadAsync(frame, upscale, ct);
                        upscale = NextUpscale(lines, upscale);
                        text = reading;
                        lastOcr = text;
                        lastTouchesEdge = lines.Any(l => l.Bounds.Left <= 2 || l.Bounds.Right >= frame.Width - 3);
                    }
                }

                switch (tracker.Observe(text, Environment.TickCount64))
                {
                    case SubtitleTracker.NewLine(var id, var line):
                        Emit(session, id, line, src, tgt, ct);
                        if (lastTouchesEdge && ++edgeLines == 2)
                            _sink.Notice("Subtitles are cut off at the side of the region, so words are lost. Select a wider region.");
                        break;
                    case SubtitleTracker.LineUpdated(var id, var line):
                        Emit(session, id, line, src, tgt, ct);
                        break;
                    case SubtitleTracker.Cleared:
                        _sink.Cleared();
                        break;
                }
                failures = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warn("subtitle loop: " + ex.Message);
                _sink.Error(ex.Message);
            }

            // A changed line is confirmed quickly; after errors wait 2s, 4s, 8s ... up to 30s.
            int wait = failures > 0 ? Math.Min(30_000, 2_000 << Math.Min(failures - 1, 4))
                : tracker.AwaitingConfirmation ? Math.Min(ConfirmDelayMs, interval)
                : interval - (int)(Environment.TickCount64 - started);
            try { await Task.Delay(Math.Max(20, wait), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Reads the frame as it is and as a white-text mask, and keeps the cleaner reading. The mask removes the
    /// moving scene behind white subtitles; the plain reading covers games whose subtitles are not white.
    /// Separate text blocks (subtitle, a dialog choice, a key hint) are kept on separate lines.
    /// </summary>
    async Task<(List<OcrLine> Lines, string Text)> ReadAsync(Bitmap frame, double upscale, CancellationToken ct)
    {
        var ocr = _ocr();
        var plain = (await ocr.RecognizeAsync(frame, ct, upscale)).Where(IsReadable).ToList();
        List<OcrLine> masked;
        using (var mask = ImageFilters.WhiteTextMask(frame))
            masked = (await ocr.RecognizeAsync(mask, ct, upscale)).Where(IsReadable).ToList();

        string plainText = ToText(plain), maskedText = ToText(masked);
        return TextUtil.ReadingScore(maskedText) >= TextUtil.ReadingScore(plainText) ? (masked, maskedText) : (plain, plainText);
    }

    static string ToText(List<OcrLine> lines) =>
        string.Join("\n", TextUtil.GroupParagraphs(lines).Select(p => TextUtil.Normalize(p.Text)).Where(t => t.Length > 0));

    void Emit(int session, int localId, string text, string src, string tgt, CancellationToken ct)
    {
        int id = session * 1_000_000 + localId; // unique across start/stop
        int version = _versions.AddOrUpdate(id, 1, (_, v) => v + 1);
        _sink.LineDetected(id, text);
        _ = TranslateLineAsync(id, version, text, src, tgt, ct);
    }

    async Task TranslateLineAsync(int id, int version, string text, string src, string tgt, CancellationToken ct)
    {
        try
        {
            await _translateGate.WaitAsync(ct);
            try
            {
                // Each text block is translated on its own, so a key hint never gets mixed into a sentence.
                var result = await _translator().TranslateAsync(text.Split('\n'), src, tgt, ct);
                if (IsCurrent(id, version)) _sink.LineTranslated(id, string.Join("\n", result));
            }
            finally
            {
                _translateGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // subtitles were turned off
        }
        catch (Exception ex)
        {
            // The same failure repeats on every line while a service is paused: one log line a minute is enough.
            string line = "subtitle translate: " + ex.Message;
            Log.Once(line, line, TimeSpan.FromMinutes(1), LogLevel.Warn);
            if (IsCurrent(id, version)) _sink.LineFailed(id, text, ex.Message);
        }
    }

    bool IsCurrent(int id, int version) => _versions.TryGetValue(id, out var v) && v == version;

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
    internal static byte[] FrameSignature(Bitmap frame)
    {
        const int W = 64, H = 16;
        using var small = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(frame, new Rectangle(0, 0, W, H));
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
}
