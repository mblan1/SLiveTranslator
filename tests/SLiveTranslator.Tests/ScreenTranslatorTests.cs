using System.Drawing;
using System.Linq;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

public class ScreenTranslatorTests
{
    static OcrLine Paragraph(string text, int top) => new(text, new Rectangle(100, top, 400, 28));

    [Fact]
    public void A_paragraph_found_to_be_noise_or_translated_by_no_service_gets_no_label()
    {
        OcrLine[] chunk = [Paragraph("Swallow potion", 0), Paragraph("12/40", 40), Paragraph("Meditate until dawn", 80)];
        // Claude left the last paragraph out and the free services could not answer it: it comes back as its source.
        var result = new TranslateResult(["Thuốc Swallow", "", "Meditate until dawn"], null, "claude/x") { FailedLines = [2] };

        var labels = ScreenTranslator.Labels(chunk, result).ToList();

        Assert.Equal([new ScreenTranslator.Item(chunk[0], "Thuốc Swallow")], labels);
    }
}
