using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Ocr;
using Xunit;

namespace SLiveTranslator.Tests;

public class OcrTests
{
    [Fact]
    public async Task Windows_ocr_reads_rendered_game_text_with_positions()
    {
        using var bmp = new Bitmap(640, 220);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(20, 24, 30));
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var font = new Font("Segoe UI", 28, FontStyle.Bold);
            g.DrawString("Health Potion", font, Brushes.White, new PointF(40, 40));
            g.DrawString("Restores 50 HP", font, Brushes.LightGray, new PointF(40, 120));
        }

        var engine = WindowsOcrEngine.Create("");
        var lines = await engine.RecognizeAsync(bmp, CancellationToken.None);
        var text = string.Join(" | ", lines.Select(l => l.Text));

        Assert.Contains("Health Potion", text);
        Assert.Contains("Restores 50 HP", text);

        var first = lines.First(l => l.Text.Contains("Health"));
        Assert.InRange(first.Bounds.Left, 30, 70);
        Assert.InRange(first.Bounds.Top, 35, 75);
        Assert.InRange(first.Bounds.Height, 20, 60);
    }

    [Fact]
    public async Task Concurrent_calls_on_one_engine_all_succeed()
    {
        // Subtitle mode and the translate-screen hotkey share one engine; Windows OCR itself allows only one call at a time.
        using var bmp = new Bitmap(480, 120);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            using var font = new Font("Segoe UI", 28, FontStyle.Bold);
            g.DrawString("Iron Shield", font, Brushes.White, new PointF(20, 30));
        }

        var engine = WindowsOcrEngine.Create("");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => engine.RecognizeAsync(bmp, CancellationToken.None)));

        Assert.All(results, lines => Assert.Contains("Iron Shield", string.Join(" ", lines.Select(l => l.Text))));
    }

    [Fact]
    public void Lists_installed_languages()
    {
        var languages = WindowsOcrEngine.AvailableLanguages();
        Assert.NotEmpty(languages);
        Assert.All(languages, l => Assert.False(string.IsNullOrWhiteSpace(l.Tag)));
    }
}
