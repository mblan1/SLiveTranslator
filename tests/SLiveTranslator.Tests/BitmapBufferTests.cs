using System.Drawing;
using System.Drawing.Imaging;
using SLiveTranslator.Capture;
using Xunit;

namespace SLiveTranslator.Tests;

public class BitmapBufferTests
{
    [Fact]
    public void The_same_size_gets_the_same_bitmap_and_another_size_a_new_one()
    {
        using var buffer = new BitmapBuffer();
        var first = buffer.Get(200, 50);

        Assert.Same(first, buffer.Get(200, 50));
        var other = buffer.Get(100, 50);
        Assert.NotSame(first, other);
        Assert.Equal(new Size(100, 50), other.Size);
        Assert.Equal(new Size(300, 80), buffer.Get(300, 80).Size); // grows past the first size
    }

    [Fact]
    public void Rows_share_the_pixels_of_the_bitmap()
    {
        using var buffer = new BitmapBuffer();
        var full = buffer.Get(40, 30);
        using (var g = Graphics.FromImage(full))
        {
            g.Clear(Color.Black);
            g.FillRectangle(Brushes.White, 0, 10, 40, 5);
        }

        var rows = buffer.Rows(10, 5);

        Assert.Equal(new Size(40, 5), rows.Size);
        Assert.Equal(Color.White.ToArgb(), rows.GetPixel(20, 0).ToArgb());
        Assert.Same(full, buffer.Rows(0, 30));
        full.SetPixel(3, 12, Color.Red);
        Assert.Equal(Color.Red.ToArgb(), rows.GetPixel(3, 2).ToArgb());
    }

    [Fact]
    public void Copied_rows_hold_the_pixels_of_the_source()
    {
        using var source = new Bitmap(60, 40, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(source))
        {
            g.Clear(Color.Black);
            g.FillRectangle(Brushes.White, 0, 20, 60, 3);
        }
        using var buffer = new BitmapBuffer();

        var copy = buffer.CopyRows(source, 19, 5);

        Assert.Equal(new Size(60, 5), copy.Size);
        Assert.Equal(Color.Black.ToArgb(), copy.GetPixel(30, 0).ToArgb());
        Assert.Equal(Color.White.ToArgb(), copy.GetPixel(30, 1).ToArgb());
        Assert.Equal(Color.White.ToArgb(), copy.GetPixel(59, 3).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), copy.GetPixel(30, 4).ToArgb());
        var taller = buffer.CopyRows(source, 0, 40); // the room for it was made by the first copy
        Assert.Equal(Color.White.ToArgb(), taller.GetPixel(59, 22).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), taller.GetPixel(59, 39).ToArgb());
    }

    [Fact]
    public void A_grown_buffer_holds_every_pixel_of_its_bitmap()
    {
        using var buffer = new BitmapBuffer();
        buffer.Get(20, 10);
        var grown = buffer.Get(640, 212);
        using (var g = Graphics.FromImage(grown)) g.Clear(Color.White);
        grown.SetPixel(639, 211, Color.Red);

        Assert.Equal(PixelFormat.Format32bppArgb, grown.PixelFormat);
        Assert.Equal(Color.Red.ToArgb(), grown.GetPixel(639, 211).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), buffer.Rows(200, 12).GetPixel(639, 11).ToArgb());
        Assert.Equal(Color.White.ToArgb(), buffer.Rows(200, 12).GetPixel(0, 0).ToArgb());
    }
}
