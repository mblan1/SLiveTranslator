using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using SLiveTranslator.Ocr;
using SLiveTranslator.Services;
using Xunit;

namespace SLiveTranslator.Tests;

public class OcrSourcePolicyTests
{
    const string Griffin = "Where is the griffin, Geralt?";

    static OcrLine Box(string text, int top = 40) => new(text, new Rectangle(330, top, 340, 28));

    /// <summary>One line box per text line, as OCR returns a subtitle.</summary>
    static List<OcrLine> Boxes(string text) => text.Length == 0 ? [] : text.Split('\n').Select((t, i) => Box(t, 40 + i * 34)).ToList();

    static OcrSourcePolicy Settled(OcrSource winner, string text = Griffin)
    {
        var policy = new OcrSourcePolicy();
        for (int i = 0; i < OcrSourcePolicy.WinsToSettle; i++) policy.Won(winner, text, Boxes(text).Count);
        return policy;
    }

    [Fact]
    public void Both_are_read_until_one_source_wins_five_times_in_a_row()
    {
        var policy = new OcrSourcePolicy();
        for (int i = 0; i < OcrSourcePolicy.WinsToSettle - 1; i++)
        {
            policy.Won(OcrSource.Mask, Griffin, 1);
            Assert.Null(policy.Single);
        }

        policy.Won(OcrSource.Mask, Griffin, 1);

        Assert.Equal(OcrSource.Mask, policy.Single);
    }

    [Fact]
    public void A_win_of_the_other_source_starts_the_count_again()
    {
        var policy = new OcrSourcePolicy();
        for (int i = 0; i < 4; i++) policy.Won(OcrSource.Mask, Griffin, 1);
        policy.Won(OcrSource.Plain, Griffin, 1);
        for (int i = 0; i < 3; i++) policy.Won(OcrSource.Mask, Griffin, 1);
        Assert.Null(policy.Single);

        policy = Settled(OcrSource.Mask);
        policy.Won(OcrSource.Plain, Griffin, 1);
        Assert.Null(policy.Single);

        Assert.Equal(OcrSource.Plain, Settled(OcrSource.Plain).Single);
    }

    [Theory]
    [InlineData(Griffin, true)]
    [InlineData("Where is the griffln, Geralt?", true)] // one misread letter
    [InlineData("Where is the griffin, Geralt? Out by the old mill, sir.", true)] // the line grew
    [InlineData("Out by the old mill, sir.", false)] // another line
    [InlineData("Wh~re |s the gr@ff|n, Ger~lt?", false)] // not readable
    [InlineData("Where is the griffin, Gera1t?", false)] // the same line, but the score dropped
    [InlineData("Where is the griffin, Ger@lt?", false)] // a junk character: scene noise the other image may not show
    [InlineData("Where is the", false)] // much shorter: not the same reading
    [InlineData("", false)] // the text went
    public void A_settled_reading_is_used_alone_only_while_it_stays_the_same_line_and_as_clean(string text, bool accepted) =>
        Assert.Equal(accepted, Settled(OcrSource.Mask).Accepts(text, Boxes(text)));

    [Fact]
    public void Reading_nothing_neither_settles_the_policy_nor_breaks_a_run_of_wins()
    {
        // Snow or white clothes in the region without a subtitle: both images read nothing, which says nothing about
        // which one reads the subtitles. Settling on it would leave the other image unread while that lasts.
        var empty = new OcrSourcePolicy();
        for (int i = 0; i < 2 * OcrSourcePolicy.WinsToSettle; i++) empty.Won(OcrSource.Mask, "", 0);
        Assert.Null(empty.Single);

        var settled = Settled(OcrSource.Mask);
        settled.Won(OcrSource.Mask, "", 0);
        Assert.Equal(OcrSource.Mask, settled.Single);
    }

    [Fact]
    public void Nothing_read_or_text_appearing_where_there_was_none_is_compared()
    {
        var policy = Settled(OcrSource.Mask);
        policy.Won(OcrSource.Mask, "", 0); // the line went

        Assert.False(policy.Accepts("", [])); // the mask sees white but reads nothing: the plain frame may read a line
        Assert.False(policy.Accepts(Griffin, Boxes(Griffin)));
    }

    [Fact]
    public void A_line_box_more_than_before_is_compared_even_when_it_looks_like_growth()
    {
        // Scene noise read as a line of its own above the subtitle: the reading still holds the old line and is longer.
        var policy = Settled(OcrSource.Mask, "Where is the griffin?");
        List<OcrLine> withNoise = [Box("rn 1l ~ lI", top: 4), Box("Where is the griffin?")];
        List<OcrLine> withLine = [Box("Where is the griffin?"), Box("Out by the old mill, sir.", top: 74)];

        Assert.False(policy.Accepts("rn 1l ~ lI Where is the griffin?", withNoise));
        Assert.False(policy.Accepts("Where is the griffin?\nOut by the old mill, sir.", withLine)); // the second line appeared
        Assert.True(policy.Accepts("Where is the griffin?", [Box("Where is the griffin?")]));
    }

    [Fact]
    public void A_noise_line_is_compared_even_without_a_line_box_more()
    {
        var policy = Settled(OcrSource.Mask, "Yes.\nWhere is the griffin?");

        Assert.False(policy.Accepts("uams@Utothe\nWhere is the griffin?", [Box("uams@Utothe", top: 4), Box("Where is the griffin?")]));
        Assert.True(policy.Accepts("Yes.\nWhere is the griffin?", Boxes("Yes.\nWhere is the griffin?")));
    }

    [Fact]
    public void The_score_is_measured_against_the_best_reading_of_the_line()
    {
        // Readings that each lose a little must not walk the score down: 33, then 31 is fine, then 29 is not.
        var policy = Settled(OcrSource.Mask, "Then we ride at dawn, before the fog lifts.");
        Assert.True(policy.Accepts("Then we ride at dawn, before the fog lif.", Boxes("Then we ride at dawn, before the fog lif.")));
        policy.Kept("Then we ride at dawn, before the fog lif.", 1);

        Assert.False(policy.Accepts("Then we ride at dawn, befor the fo lif.", Boxes("Then we ride at dawn, befor the fo lif.")));
    }
}
