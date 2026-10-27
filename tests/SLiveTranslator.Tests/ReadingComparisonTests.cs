using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using Xunit;

namespace SLiveTranslator.Tests;

public class ReadingComparisonTests
{
    // Real OCR output from a Witcher 3 session: several readings of the same line.
    public static TheoryData<string, string> SameLinePairs => new()
    {
        { "Bring our young damsel to the lower courtyard. She wants to practice? She'll getto practice.", "-J uams@Utothe lowéncourtyard. She wants to practice'She'll getto practice:——" },
        { "Bring our young damsel to the lower courtyard. She wants to practice? She'll getto practice.", "ing ouqyoungdamsel to the lower courtyårdsShe wants to practice? She'll geryo practice." },
        { "Mmm. So you did read the chapter. Still, you should've asked if...", "Hmm.¯So you didread the chapter. Still; youshouldlve asked if..." },
        { "Why the hell not?kThe whippersnapper rejuses todo as she's told.", "•Why:the hell not? The whippersnapper refuses to do as shelstold.n" },
        { "Take a trophy from the griffin's corpse.", "Take a trophy from the griffin's corpse. @" },
        { "Now I see why,you were so eager to practice.tStrike!", "Now I see why, you were so eager to practice. Strike!" },
    };

    [Theory]
    [MemberData(nameof(SameLinePairs))]
    public void Misreadings_of_one_line_count_as_the_same_line(string a, string b) => Assert.True(TextUtil.SameLine(a, b));

    public static TheoryData<string, string> DifferentLinePairs => new()
    {
        { "Well, yes, but... that book was horribly dull!", "Yes." },
        { "You promised Ciri you'd train with her.", "Go and train with her. Then come back. It'll give me a chance to put my face on." },
        { "Wrong.", "Enough. Get down." },
        { "1. Wanna stay with you a little longer.", "3. You're right. I should go see Ciri." },
        { "Sorry's not good enough.", "Anything to say for yourself, young lady?" },
        { "The griffin has been hunting here.", "The griffin has been hunting here. We must stop it before nightfall, or more will die." },
    };

    [Theory]
    [MemberData(nameof(DifferentLinePairs))]
    public void Different_lines_are_kept_apart(string a, string b) => Assert.False(TextUtil.SameLine(a, b));

    [Fact]
    public void Reading_score_prefers_clean_text_over_garbled_text()
    {
        Assert.True(TextUtil.ReadingScore("Bring our young damsel to the lower courtyard. She wants to practice?")
                    > TextUtil.ReadingScore("youjlOC) dlå)lnnsell She —e?"));
        Assert.True(TextUtil.ReadingScore("She wants to practice? She'll get to practice.")
                    > TextUtil.ReadingScore("She$/ahts to practice? She!ll get to practice."));
        Assert.True(TextUtil.ReadingScore("古老的剑散发着力量。") > 0);
    }

    [Fact]
    public async Task White_text_mask_reads_subtitles_over_a_bright_busy_scene()
    {
        const string line = "Bring our young damsel to the lower courtyard. She wants to practice?";
        using var frame = new Bitmap(1300, 120);
        using (var g = Graphics.FromImage(frame))
        {
            var rnd = new System.Random(7);
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 1300, 120), Color.FromArgb(170, 185, 200), Color.FromArgb(150, 140, 100), 30f))
                g.FillRectangle(bg, 0, 0, 1300, 120);
            for (int i = 0; i < 160; i++)
            {
                int v = rnd.Next(120, 256);
                using var b = new SolidBrush(Color.FromArgb(rnd.Next(80, 200), v, System.Math.Max(0, v - rnd.Next(0, 60)), System.Math.Max(0, v - rnd.Next(0, 90))));
                g.FillEllipse(b, rnd.Next(-20, 1300), rnd.Next(-20, 120), rnd.Next(6, 70), rnd.Next(4, 40));
            }
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 25f, GraphicsUnit.Pixel);
            using var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            foreach (var (dx, dy) in new[] { (1, 1), (2, 2), (-1, 1), (1, -1) }) g.DrawString(line, font, shadow, 60 + dx, 40 + dy);
            g.DrawString(line, font, Brushes.White, 60, 40);
        }

        var engine = WindowsOcrEngine.Create("");
        using var mask = ImageFilters.WhiteTextMask(frame);
        var text = TextUtil.Normalize(string.Join(" ", (await engine.RecognizeAsync(mask, CancellationToken.None, 1.0)).Select(l => l.Text)));

        Assert.True(TextUtil.Similarity(text, line) > 0.9, text);
    }
}
