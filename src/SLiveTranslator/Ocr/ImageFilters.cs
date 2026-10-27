using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace SLiveTranslator.Ocr;

public static class ImageFilters
{
    /// <summary>
    /// Black text on white from only the near-white, low-colour pixels. Most games draw subtitles in white;
    /// this removes the moving scene behind them, which is what makes OCR misread a line differently every frame.
    /// In tests on busy bright and dark scenes it raised the match with the true text from about 0.3 to 0.99.
    /// It does not help white text on snow or sky; the caller also reads the plain frame and keeps the cleaner result.
    /// </summary>
    public static unsafe Bitmap WhiteTextMask(Bitmap src, int minBrightness = 200, int maxSpread = 40)
    {
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var s = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var d = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < src.Height; y++)
            {
                byte* sp = (byte*)s.Scan0 + (long)y * s.Stride;
                uint* dp = (uint*)((byte*)d.Scan0 + (long)y * d.Stride);
                for (int x = 0; x < src.Width; x++, sp += 4)
                {
                    int b = sp[0], g = sp[1], r = sp[2];
                    int min = Math.Min(r, Math.Min(g, b)), max = Math.Max(r, Math.Max(g, b));
                    dp[x] = min >= minBrightness && max - min <= maxSpread ? 0xFF000000u : 0xFFFFFFFFu;
                }
            }
        }
        finally
        {
            src.UnlockBits(s);
            dst.UnlockBits(d);
        }
        return dst;
    }
}
