using System.Drawing;
using System.IO;
using System.Resources;
using SLiveTranslator.UI;
using Xunit;

namespace SLiveTranslator.Tests;

public class TrayIconTests
{
    /// <summary>The icon as the running app reads it: the WPF resource behind pack://application:,,,/Assets/app.ico.</summary>
    static Stream AppIcon()
    {
        var assembly = typeof(TrayIcon).Assembly;
        var resources = new ResourceManager(assembly.GetName().Name + ".g", assembly);
        var stream = resources.GetStream("assets/app.ico");
        Assert.NotNull(stream);
        return stream!;
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    public void The_tray_gets_the_app_icon_at_the_small_icon_size_and_a_green_dot_while_live(int size)
    {
        using var icons = TrayIcon.From(AppIcon(), new Size(size, size));

        Assert.Equal(size, icons.Idle.Width);
        Assert.Equal(size, icons.Live.Width);
        using var idle = icons.Idle.ToBitmap();
        using var live = icons.Live.ToBitmap();
        int c = size - 1 - (int)System.Math.Round(size * 0.21); // the middle of the dot
        var dot = live.GetPixel(c, c);
        Assert.True(dot.G > 140 && dot.R < 100 && dot.B < 100, $"dot pixel {dot}");
        Assert.NotEqual(dot.ToArgb(), idle.GetPixel(c, c).ToArgb());
        for (int y = 0; y < size / 2; y++) // away from the dot it is the app icon; half-transparent edge pixels may round by a step
            for (int x = 0; x < size / 2; x++)
                Assert.True(Close(idle.GetPixel(x, y), live.GetPixel(x, y)), $"pixel {x},{y}: {idle.GetPixel(x, y)} vs {live.GetPixel(x, y)}");
    }

    static bool Close(Color a, Color b) =>
        System.Math.Abs(a.A - b.A) <= 2 && (a.A < 8 || System.Math.Abs(a.R - b.R) <= 24 && System.Math.Abs(a.G - b.G) <= 24 && System.Math.Abs(a.B - b.B) <= 24);
}
