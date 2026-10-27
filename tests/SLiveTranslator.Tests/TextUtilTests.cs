using System.Drawing;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using Xunit;

namespace SLiveTranslator.Tests;

public class TextUtilTests
{
    [Fact]
    public void Similarity_detects_jitter_versus_new_text()
    {
        Assert.Equal(1.0, TextUtil.Similarity("Hello there", "Hello there"));
        Assert.True(TextUtil.Similarity("Hello there, traveller.", "Hello there, traveIler.") > 0.92);
        Assert.True(TextUtil.Similarity("Hello there, traveller.", "Take this sword with you.") < 0.5);
    }

    [Fact]
    public void Normalize_collapses_whitespace()
    {
        Assert.Equal("a b c", TextUtil.Normalize("  a \n b\t\tc "));
    }

    [Fact]
    public void GroupParagraphs_merges_stacked_lines_and_keeps_distant_ones_apart()
    {
        var lines = new[]
        {
            new OcrLine("Ancient Sword", new Rectangle(100, 100, 200, 24)),
            new OcrLine("+12 Attack", new Rectangle(100, 128, 120, 24)),
            new OcrLine("Rare item", new Rectangle(102, 156, 140, 24)),
            new OcrLine("Inventory", new Rectangle(800, 100, 120, 24)),
            new OcrLine("Gold: 240", new Rectangle(100, 400, 120, 24)),
        };

        var paragraphs = TextUtil.GroupParagraphs(lines);

        Assert.Equal(3, paragraphs.Count);
        var tooltip = Assert.Single(paragraphs, p => p.Text.StartsWith("Ancient"));
        Assert.Equal("Ancient Sword +12 Attack Rare item", tooltip.Text);
        Assert.Equal(3, tooltip.LineCount);
        Assert.Equal(Rectangle.FromLTRB(100, 100, 300, 180), tooltip.Bounds);
        Assert.Contains(paragraphs, p => p.Text == "Inventory");
        Assert.Contains(paragraphs, p => p.Text == "Gold: 240");
    }
}
