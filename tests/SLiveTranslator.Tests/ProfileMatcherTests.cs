using System;
using System.Linq;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The keep and glossary half of the profile matching contract, with the reference validator's cases.</summary>
public class ProfileMatcherTests
{
    static readonly ProfileMatcher Witcher = ProfileMatcher.For(TestProfiles.Witcher3());

    // validate-profile.js, keepCases
    [Theory]
    [InlineData("Ghouls! Kill them.", new[] { "Ghouls" })]
    [InlineData("ROACH", new[] { "ROACH" })]
    [InlineData("WHITE ORCHARD", new[] { "WHITE ORCHARD" })]
    [InlineData("Yennefer is here.", new[] { "Yennefer" })]
    [InlineData("Alternatively, use the altar.", new string[0])]
    [InlineData("There's a roach in the stew.", new string[0])]
    [InlineData("Witchers never die in bed.", new[] { "Witchers" })]
    [InlineData("An alghoul, not a ghoul.", new[] { "alghoul", "ghoul" })]
    [InlineData("Spaces between the stones.", new string[0])]
    [InlineData("Back to Crow’s Perch.", new[] { "Crow’s Perch" })]
    [InlineData("Ride to Kaer\nMorhen.", new[] { "Kaer\nMorhen" })]
    [InlineData("Cerys an Craite and Crach an Craite.", new[] { "Cerys", "an Craite", "Crach an Craite" })]
    [InlineData("Nekkers! Strigas, leshens.", new[] { "Nekkers", "Strigas", "leshens" })]
    [InlineData("Space Continue", new[] { "Space" })]
    // free MT residual: keep has no context, so the title's "Space" is frozen too (glossary engines treat the phrase as one unit)
    [InlineData("Lady of Space and Time", new[] { "Space" })]
    public void Keep_entries_match_whole_words_longest_first_by_their_case_rule(string source, string[] expected) =>
        Assert.Equal(expected, Witcher.KeepSpans(source).Select(m => m.Text));

    [Fact]
    public void Positions_point_into_the_source_as_written()
    {
        var span = Assert.Single(Witcher.KeepSpans("Back to Crow’s Perch."));

        Assert.Equal((8, 12), (span.Index, span.Length));
        Assert.Null(span.Translation);
    }

    [Fact]
    public void A_glossary_phrase_that_holds_a_keep_entry_is_one_term()
    {
        var terms = Witcher.Terms("We sail to the Skellige Isles with silver swords.");

        Assert.Equal(["Skellige Isles", "silver swords"], terms.Select(t => t.Text));
        Assert.Equal(["Quần đảo Skellige", "kiếm bạc"], terms.Select(t => t.Translation));
    }

    [Theory]
    [InlineData("Two drowners and a sorceress.", "sorceress", "nữ pháp sư")]
    [InlineData("Lodge of Sorceresses", "Lodge of Sorceresses", "Hội Nữ Pháp Sư")]
    [InlineData("TROPHIES", "TROPHIES", "chiến lợi phẩm")] // y → ies, any case
    public void Glossary_terms_match_in_any_case_and_as_plurals(string source, string text, string translation)
    {
        var term = Assert.Single(Witcher.Terms(source), t => t.Translation is not null);

        Assert.Equal((text, translation), (term.Text, term.Translation));
    }

    [Fact]
    public void A_term_only_an_llm_can_apply_is_left_to_the_llm()
    {
        // "the Continent" is "LLM only": a capital C decides, which a mechanical glossary cannot tell.
        Assert.DoesNotContain(Witcher.Terms("Across the Continent, sir."), t => t.Translation is not null);
    }

    [Fact]
    public void A_term_in_both_lists_is_kept_as_it_is()
    {
        var term = Assert.Single(Witcher.Terms("A witcher."));

        Assert.Null(term.Translation);
    }

    [Fact]
    public void The_machine_stamp_holds_only_the_keep_entries_and_fix_rules_of_the_line()
    {
        var profile = TestProfiles.Witcher3();
        string roach = ProfileMatcher.For(profile).MachineStamp("Roach, stay."), crowns = ProfileMatcher.For(profile).MachineStamp("Two hundred crowns.");

        Assert.Equal("", ProfileMatcher.For(profile).MachineStamp("The weather is fine.")); // the same as without a profile
        Assert.NotEqual("", roach);
        Assert.NotEqual("", crowns); // a fix rule applies, though nothing is kept
        Assert.Equal(16, roach.Length);

        profile.Characters.Clear();
        profile.Style.Clear();
        profile.Glossary.Clear();
        profile.Keep.Add("Jaskier");
        profile.Version++;
        Assert.Equal(roach, ProfileMatcher.For(profile).MachineStamp("Roach, stay."));
        Assert.NotEqual("", ProfileMatcher.For(profile).MachineStamp("Where is Jaskier?"));

        profile.FixRules.RemoveAll(r => r.Trigger.Contains("crown", StringComparison.OrdinalIgnoreCase));
        profile.Version++;
        Assert.Equal("", ProfileMatcher.For(profile).MachineStamp("Two hundred crowns."));
    }

    [Fact]
    public void The_prompt_stamp_follows_the_characters_and_style_and_the_terms_of_the_line()
    {
        var profile = TestProfiles.Witcher3();
        string before = ProfileMatcher.For(profile).PromptStamp("Hello there.");
        string withTerm = ProfileMatcher.For(profile).PromptStamp("Kill the drowners.");

        profile.Glossary.Add(new GlossaryEntry { En = "griffin", Vi = "sư tử đầu chim" });
        profile.Version++;
        Assert.Equal(before, ProfileMatcher.For(profile).PromptStamp("Hello there.")); // a term the line does not hold
        Assert.Equal(withTerm, ProfileMatcher.For(profile).PromptStamp("Kill the drowners."));
        Assert.NotEqual(before, ProfileMatcher.For(profile).PromptStamp("The griffin is near."));

        profile.Characters[0].Address[0].Self = "tớ";
        profile.Version++;
        Assert.NotEqual(before, ProfileMatcher.For(profile).PromptStamp("Hello there.")); // pronouns matter for any line
    }

    [Fact]
    public void The_matcher_follows_edits_of_the_profile()
    {
        var profile = TestProfiles.Witcher3();
        var before = ProfileMatcher.For(profile);
        Assert.Same(before, ProfileMatcher.For(profile));

        profile.Keep.Add("Jaskier");
        profile.Version++;

        Assert.Equal(["Jaskier"], ProfileMatcher.For(profile).KeepSpans("Jaskier sings.").Select(m => m.Text));
    }
}
