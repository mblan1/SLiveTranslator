using System.Drawing;
using SLiveTranslator.Services;
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
}
