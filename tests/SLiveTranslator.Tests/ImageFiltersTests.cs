using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using SLiveTranslator.Ocr;
using Xunit;

namespace SLiveTranslator.Tests;

public class ImageFiltersTests
{
    static Bitmap Dark(int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(30, 34, 40));
        return bmp;
    }

    static Bitmap Subtitle(string text, int width = 600, int height = 70, float size = 28f, float x = 40)
    {
        var bmp = Dark(width, height);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", size, GraphicsUnit.Pixel);
        using var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
        foreach (var (dx, dy) in new[] { (1, 1), (2, 2) }) g.DrawString(text, font, shadow, x + dx, 15 + dy);
        g.DrawString(text, font, Brushes.White, x, 15);
        return bmp;
    }

    static int Gray(Bitmap bmp, int x, int y) => bmp.GetPixel(x, y).R;

    [Fact]
    public void Stats_count_text_pixels_per_row()
    {
        using var frame = Dark(300, 60);
        using (var g = Graphics.FromImage(frame))
        {
            g.FillRectangle(Brushes.White, 100, 20, 100, 10); // 1000 text pixels in rows 20..29
            g.FillRectangle(Brushes.White, 0, 40, 5, 4);
        }
        using var mask = new Bitmap(300, 60, PixelFormat.Format32bppArgb);

        var stats = ImageFilters.WhiteTextMask(frame, mask);

        Assert.Equal(1020, stats.WhitePixels);
        Assert.All(Enumerable.Range(20, 10), y => Assert.Equal(100, stats.RowHistogram[y]));
        Assert.All(Enumerable.Range(40, 4), y => Assert.Equal(5, stats.RowHistogram[y]));
        Assert.Equal(1020, stats.RowHistogram.Sum());
        Assert.Equal(MaskStats.SignatureWidth * MaskStats.SignatureHeight, stats.Signature.Length);
        Assert.Equal(0, Gray(mask, 150, 25)); // dark text on white
        Assert.Equal(255, Gray(mask, 250, 10));
    }

    [Fact]
    public void The_bitmap_overload_returns_the_same_mask()
    {
        using var frame = Subtitle("Won't you stay?");
        using var mask = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        ImageFilters.WhiteTextMask(frame, mask);
        using var simple = ImageFilters.WhiteTextMask(frame);

        for (int y = 0; y < frame.Height; y += 3)
            for (int x = 0; x < frame.Width; x += 3)
                Assert.Equal(mask.GetPixel(x, y), simple.GetPixel(x, y));
    }

    [Fact]
    public void Soft_mask_keeps_the_anti_aliased_pixels_of_an_apostrophe()
    {
        using var frame = Subtitle("' ' '", width: 200, size: 25f);
        using var mask = ImageFilters.WhiteTextMask(frame);

        int hard = 0, kept = 0, partial = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                var c = frame.GetPixel(x, y);
                int min = Math.Min(c.R, Math.Min(c.G, c.B));
                if (min >= 200) hard++; // what the old binary mask kept
                int v = Gray(mask, x, y);
                if (v < 255) kept++;
                if (v is > 0 and < 255) partial++;
            }
        }

        Assert.True(hard > 0);
        Assert.True(partial > 0, "the edges of the glyphs become grey instead of disappearing");
        Assert.True(kept >= hard * 1.3, $"kept {kept} pixels, the binary mask {hard}");
    }

    [Fact]
    public void A_pale_background_without_text_stays_white()
    {
        // Pale stone or sky is bright and grey too, but no part of it is full white: no grey veil for OCR to read through.
        using var frame = new Bitmap(200, 40, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame)) g.Clear(Color.FromArgb(175, 180, 185));
        using var mask = new Bitmap(200, 40, PixelFormat.Format32bppArgb);

        var stats = ImageFilters.WhiteTextMask(frame, mask);

        Assert.Equal(0, stats.WhitePixels);
        Assert.All(stats.Signature, v => Assert.Equal(255, v));
        Assert.Equal(255, Gray(mask, 100, 20));
    }

    [Fact]
    public void Signature_shows_a_short_answer_changing()
    {
        // "Yes." → "No." moves the mean of a full-frame thumbnail by a fraction of a level; one cell changes a lot.
        using var yes = Subtitle("Yes.", width: 1800, height: 200, x: 860);
        using var no = Subtitle("No.", width: 1800, height: 200, x: 860);
        using var scratch = new Bitmap(1800, 200, PixelFormat.Format32bppArgb);

        var a = ImageFilters.WhiteTextMask(yes, scratch).Signature;
        var b = ImageFilters.WhiteTextMask(no, scratch).Signature;

        Assert.True(a.Zip(b, (p, q) => Math.Abs(p - q)).Max() > 12);
    }
}
