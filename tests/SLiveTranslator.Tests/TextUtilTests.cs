using System.Drawing;
using System.Linq;
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

public class ReadingScoreTests
{
    [Theory]
    [InlineData("Hello there", 10)]
    [InlineData("why,you", 6)] // split at the stray comma
    [InlineData(".Wrong:Footwork!", 13)]
    [InlineData("courtyardsShe", 13)] // split at the case change
    [InlineData("uams@Utothe", 8)] // a junk character costs 2, not the whole token
    [InlineData("pr4ctice", -4)] // a digit/letter mix costs half its length
    [InlineData("11m", -1)]
    [InlineData("1. Yes.", 4)]
    [InlineData("古老的剑散发着力量。", 9)]
    public void Scores_letters_in_words_and_charges_only_junk(string text, int expected) =>
        Assert.Equal(expected, TextUtil.ReadingScore(text));

    [Theory]
    [InlineData(".Wrong:Footwork!")]
    [InlineData("Now I see why,you were so eager to practice.tStrike!")]
    [InlineData("Hmm.¯So you didread the chapter. Still; youshouldlve asked if...")]
    public void A_line_with_a_stray_mark_still_looks_readable(string text) => Assert.True(SubtitleTracker.LooksReadable(text));

    [Fact]
    public void A_complete_reading_with_slips_beats_a_clean_one_that_dropped_words()
    {
        const string complete = "But you weretaßleep/Uncle Vesemir.", dropped = "Butyou were emir.";

        Assert.Equal(complete, TextUtil.PickReading(complete, dropped));
        Assert.Equal(complete, TextUtil.PickReading(dropped, complete));
    }

    [Fact]
    public void For_English_letters_english_does_not_have_count_as_junk()
    {
        // Real readings of one still frame: the plain image garbles a word into accented letters.
        const string garbled = "You.p,VLéfrijSéd Ciri you'd train with hernand a witcher keeps his word.";
        const string clean = "You promised Ciri you'd train with her, and a witcher keeps his word.";

        Assert.Equal(clean, TextUtil.PickReading(clean, garbled, "en"));
        Assert.True(TextUtil.ReadingScore("yoå'd taßleep", "en") < TextUtil.ReadingScore("you'd asleep", "en"));
        // Other languages keep their letters: French and Vietnamese words are full of them.
        Assert.Equal(TextUtil.ReadingScore("préféré"), TextUtil.ReadingScore("préféré", "fr"));
        Assert.Equal(7, TextUtil.ReadingScore("préféré", "fr"));
        Assert.Equal(TextUtil.ReadingScore("古老的剑散发着力量。"), TextUtil.ReadingScore("古老的剑散发着力量。", "en"));
    }

    [Fact]
    public void PickReading_prefers_the_longer_reading_of_a_grown_line_and_the_cleaner_of_equal_ones()
    {
        Assert.Equal("Well, yes, but... that book was horribly dull!",
            TextUtil.PickReading("Well, yes, but... that book was horribly", "Well, yes, but... that book was horribly dull!"));
        Assert.Equal("She wants to practice? She'll get to practice.",
            TextUtil.PickReading("She$/ahts to practice? She!ll get to practice.", "She wants to practice? She'll get to practice."));
        Assert.Equal("Where is the griffin?", TextUtil.PickReading("Where is the griffin?", "Where is the griffln?")); // a tie keeps the first
    }

    [Theory]
    [InlineData("1. Wonlt weasel out of this.", "1. Won't weasel out of this.")]
    [InlineData("Still, you shouldlve asked.", "Still, you should've asked.")]
    [InlineData("Couldtve been worse.", "Could've been worse.")]
    [InlineData("11m not going.", "I'm not going.")]
    [InlineData("Fine. 11m going.", "Fine. I'm going.")]
    [InlineData("l'm sure. Ilm sure.", "I'm sure. I'm sure.")]
    [InlineData("Won't you stay? Pm sure she will. See you at 5 PM.", "Won't you stay? I'm sure she will. See you at 5 PM.")]
    [InlineData("Sheld have told you.", "She'd have told you.")]
    [InlineData("It's not that I didnlt try. Theylll say welre fools.", "It's not that I didn't try. They'll say we're fools.")]
    [InlineData("Youlll see, Letls go, it1s late.", "You'll see, Let's go, it's late.")]
    [InlineData("dont, cant, isnt", "don't, can't, isn't")]
    [InlineData("WONLT", "WON'T")]
    public void RepairContractions_puts_back_misread_apostrophes(string text, string expected) =>
        Assert.Equal(expected, TextUtil.RepairContractions(text));

    [Theory]
    [InlineData("Howls in the night. She tells the shells on Hills.")]
    [InlineData("Well, we were wed. He held its hell of a shed. It lets us.")]
    [InlineData("The ill-fated wells. Ill news. 11 men.")]
    [InlineData("She'll go. I'd say it's fine.")]
    public void RepairContractions_leaves_real_words_alone(string text)
    {
        string expected = text.Replace("Ill news", "I'll news"); // a capital "Ill" on its own is read as "I'll"
        Assert.Equal(expected, TextUtil.RepairContractions(text));
    }

    [Fact]
    public void RepairContractions_fixes_possessives_of_known_names_only()
    {
        Assert.Equal("Ciri's sword and Geralt's horse.", TextUtil.RepairContractions("Cirils sword and Geralt1s horse.", ["Ciri", "Geralt"]));
        Assert.Equal("Cirils sword.", TextUtil.RepairContractions("Cirils sword."));
        Assert.Equal("The witchers came.", TextUtil.RepairContractions("The witchers came.", ["witcher"])); // a plural keeps its s
    }

    [Fact]
    public void RepairContractions_uses_the_game_profile_names()
    {
        var names = TestProfiles.Witcher3().Names;

        Assert.Equal("Ciri's gone and Yennefer's here.", TextUtil.RepairContractions("Cirils gone and Yennefer!s here.", names));
        Assert.Equal("Annals and Vests.", TextUtil.RepairContractions("Annals and Vests.", names)); // Anna and Ves are names too
        Assert.Equal("Kaer Morhen's walls.", TextUtil.RepairContractions("Kaer Morhenls walls.", names));
    }

    [Fact]
    public void Subtitle_grouping_merges_wider_spaced_lines_but_keeps_numbered_choices_apart()
    {
        // Line boxes 24 px high, 26 px apart (1.08 line heights): one two-line subtitle.
        var subtitle = new[]
        {
            new OcrLine("You promised Ciri you'd train with her.", new Rectangle(700, 40, 900, 24)),
            new OcrLine("Go on, then.", new Rectangle(1000, 90, 260, 24)),
        };
        Assert.Equal(2, TextUtil.GroupParagraphs(subtitle).Count); // translate-screen default is unchanged
        var merged = Assert.Single(TextUtil.GroupParagraphs(subtitle, 1.3, keepNumberedApart: true));
        Assert.Equal("You promised Ciri you'd train with her. Go on, then.", merged.Text);

        var choices = new[]
        {
            new OcrLine("1. Wanna stay with you a little longer.", new Rectangle(700, 20, 800, 24)),
            new OcrLine("2. You're right. I should go see Ciri.", new Rectangle(700, 54, 780, 24)),
            new OcrLine("3. Tell me about the Wild Hunt.", new Rectangle(700, 88, 640, 24)),
        };
        Assert.Equal(["1. Wanna stay with you a little longer.", "2. You're right. I should go see Ciri.", "3. Tell me about the Wild Hunt."],
            TextUtil.GroupParagraphs(choices, 1.3, keepNumberedApart: true).Select(p => p.Text));
    }
}
