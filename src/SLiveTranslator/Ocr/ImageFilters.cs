using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace SLiveTranslator.Ocr;

/// <summary>What <see cref="ImageFilters.WhiteTextMask(Bitmap, Bitmap)"/> found, measured in the same pass.</summary>
/// <param name="WhitePixels">Pixels that are text in the mask (darker than mid-grey).</param>
/// <param name="RowHistogram">Text pixels per row.</param>
/// <param name="Signature"><see cref="SignatureWidth"/> x <see cref="SignatureHeight"/> greyscale thumbnail of the mask, row by row.</param>
public sealed record MaskStats(int WhitePixels, int[] RowHistogram, byte[] Signature)
{
    public const int SignatureWidth = 128, SignatureHeight = 32;
}

public static class ImageFilters
{
    // Soft threshold on the darkest channel of low-colour pixels: 140 and below is background, 200 and above full text.
    const int InkFloor = 140, InkRange = 60, MaxSpread = 60;

    /// <summary>
    /// Dark text on white from only the bright, low-colour pixels. Most games draw subtitles in white; this removes
    /// the moving scene behind them, which is what makes OCR misread a line differently every frame. The grey ramp
    /// keeps the anti-aliased edges of thin glyphs, so apostrophes and commas survive ("Won't", not "Wonlt").
    /// Partial ink is kept only next to full ink: an edge always touches its glyph, while a pale sky or stone wall
    /// would otherwise turn into a grey background that OCR cannot read through.
    /// It does not help white text on snow or sky; the caller also reads the plain frame and keeps the cleaner result.
    /// </summary>
    public static Bitmap WhiteTextMask(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        try
        {
            WhiteTextMask(src, dst);
            return dst;
        }
        catch
        {
            dst.Dispose();
            throw;
        }
    }

    /// <summary>Writes the mask of <paramref name="src"/> into <paramref name="dst"/> (same size) and measures it.</summary>
    public static unsafe MaskStats WhiteTextMask(Bitmap src, Bitmap dst)
    {
        int w = src.Width, h = src.Height;
        if (dst.Width != w || dst.Height != h) throw new ArgumentException("The mask must have the size of the source image.", nameof(dst));

        const int SW = MaskStats.SignatureWidth, SH = MaskStats.SignatureHeight;
        var rows = new int[h];
        var cellSum = new long[SW * SH];
        var cellCount = new int[SW * SH];
        var cellX = new int[w];
        for (int x = 0; x < w; x++) cellX[x] = (int)((long)x * SW / w);
        int white = 0;

        // Ink of the rows above, at and below the one being written: partial ink needs full ink among its 8 neighbours.
        var above = new byte[w];
        var current = new byte[w];
        var below = new byte[w];

        var rect = new Rectangle(0, 0, w, h);
        var s = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var d = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (h > 0) InkRow((byte*)s.Scan0, w, current);
                for (int y = 0; y < h; y++)
                {
                    bool hasBelow = y + 1 < h;
                    if (hasBelow) InkRow((byte*)s.Scan0 + (long)(y + 1) * s.Stride, w, below);
                    uint* dp = (uint*)((byte*)d.Scan0 + (long)y * d.Stride);
                    int cellRow = (int)((long)y * SH / h) * SW;
                    for (int x = 0; x < w; x++)
                    {
                        int ink = current[x];
                        if (ink is > 0 and < 255 && !NextToFullInk(x, w, y > 0 ? above : null, current, hasBelow ? below : null)) ink = 0;
                        uint v = (uint)(255 - ink);
                        dp[x] = 0xFF000000u | v << 16 | v << 8 | v;

                        int cell = cellRow + cellX[x];
                        cellSum[cell] += v;
                        cellCount[cell]++;
                        if (ink < 128) continue;
                        white++;
                        rows[y]++;
                    }
                    (above, current, below) = (current, below, above);
                }
            }
            finally
            {
                dst.UnlockBits(d);
            }
        }
        finally
        {
            src.UnlockBits(s);
        }

        var signature = new byte[SW * SH];
        for (int i = 0; i < signature.Length; i++)
            signature[i] = cellCount[i] == 0 ? (byte)255 : (byte)(cellSum[i] / cellCount[i]);
        return new MaskStats(white, rows, signature);
    }

    /// <summary>Ink (0 = background, 255 = full text) of one row of 32bpp pixels.</summary>
    static unsafe void InkRow(byte* row, int width, byte[] ink)
    {
        for (int x = 0; x < width; x++, row += 4)
        {
            int b = row[0], g = row[1], r = row[2];
            int min = Math.Min(r, Math.Min(g, b)), max = Math.Max(r, Math.Max(g, b));
            ink[x] = max - min <= MaxSpread ? (byte)Math.Clamp((min - InkFloor) * 255 / InkRange, 0, 255) : (byte)0;
        }
    }

    static bool NextToFullInk(int x, int width, byte[]? above, byte[] current, byte[]? below)
    {
        int from = Math.Max(0, x - 1), to = Math.Min(width - 1, x + 1);
        for (int i = from; i <= to; i++)
        {
            if (current[i] == 255 || above?[i] == 255 || below?[i] == 255) return true;
        }
        return false;
    }
}
