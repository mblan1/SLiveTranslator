using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Capture;
using SLiveTranslator.Ocr;
using SLiveTranslator.Translation;

namespace SLiveTranslator.Services;

/// <summary>One-shot: capture an area, OCR everything in it, translate per paragraph, return items with absolute screen bounds.</summary>
public sealed class ScreenTranslator
{
    const int FirstChunk = 4, Chunk = 16, ParallelChunks = 2;

    readonly Func<IOcrEngine> _ocr;
    readonly Func<ITranslator> _translator;
    readonly Func<(string Src, string Tgt)> _langs;
    readonly Func<GameProfile?> _profile;

    /// <param name="profile">The game profile for the game in front; no context is sent, the screen is no conversation.</param>
    public ScreenTranslator(Func<IOcrEngine> ocr, Func<ITranslator> translator, Func<(string Src, string Tgt)> langs, Func<GameProfile?>? profile = null)
    {
        _ocr = ocr;
        _translator = translator;
        _langs = langs;
        _profile = profile ?? (() => null);
    }

    /// <param name="Source">Recognized text; Bounds are absolute screen pixels.</param>
    public sealed record Item(OcrLine Source, string Translated);

    /// <param name="focus">Screen point (usually the mouse): text closest to it is translated first.</param>
    /// <param name="progress">Called with everything translated so far, after each batch (any thread).</param>
    public async Task<IReadOnlyList<Item>> TranslateAsync(Rectangle area, CancellationToken ct, Point? focus = null, Action<IReadOnlyList<Item>>? progress = null)
    {
        IReadOnlyList<OcrLine> lines;
        using (var frame = ScreenCapture.Capture(area))
            lines = await _ocr().RecognizeAsync(frame, ct);

        var paragraphs = TextUtil.GroupParagraphs(lines)
            .Where(p => p.Text.Length >= 2 && p.Text.Any(char.IsLetter))
            .Select(p =>
            {
                var bounds = p.Bounds;
                bounds.Offset(area.Left, area.Top);
                return p with { Bounds = bounds };
            })
            .ToList();
        if (paragraphs.Count == 0) return Array.Empty<Item>();
        if (focus is { } f) paragraphs = paragraphs.OrderBy(p => DistanceSquared(p.Bounds, f)).ToList();

        var (src, tgt) = _langs();
        var profile = _profile();
        var done = new List<Item>(paragraphs.Count);
        using var gate = new SemaphoreSlim(ParallelChunks);
        await Task.WhenAll(Chunks(paragraphs).Select(async chunk =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var request = new TranslateRequest(chunk.Select(p => p.Text).ToList(), src, tgt, Array.Empty<ContextLine>(), profile);
                var result = await _translator().TranslateAsync(request, ct);
                IReadOnlyList<Item> snapshot;
                lock (done)
                {
                    done.AddRange(Labels(chunk, result));
                    snapshot = done.ToList();
                }
                progress?.Invoke(snapshot);
            }
            finally
            {
                gate.Release();
            }
        }));
        return done;
    }

    /// <summary>
    /// The labels of a translated chunk. A paragraph an LLM found not to be language comes back empty, and one no
    /// service could translate comes back as its source (<see cref="TranslateResult.FailedLines"/>): neither gets a label.
    /// </summary>
    internal static IEnumerable<Item> Labels(IReadOnlyList<OcrLine> chunk, TranslateResult result) =>
        Enumerable.Range(0, chunk.Count)
            .Where(i => !string.IsNullOrWhiteSpace(result.Texts[i]) && result.FailedLines?.Contains(i) != true)
            .Select(i => new Item(chunk[i], result.Texts[i]));

    /// <summary>A small first batch so the nearest text shows up fast, then bigger batches.</summary>
    static IEnumerable<List<OcrLine>> Chunks(List<OcrLine> items)
    {
        int index = 0, size = FirstChunk;
        while (index < items.Count)
        {
            yield return items.Skip(index).Take(size).ToList();
            index += size;
            size = Chunk;
        }
    }

    static long DistanceSquared(Rectangle r, Point p)
    {
        long dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right));
        long dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
        return dx * dx + dy * dy;
    }
}
