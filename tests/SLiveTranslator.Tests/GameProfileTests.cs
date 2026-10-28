using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The profiles shipped with the app, read fresh for each test (a profile is mutable).</summary>
static class TestProfiles
{
    public static byte[] Witcher3Bytes => GameProfileStore.EmbeddedSeeds()["witcher3.json"];

    public static GameProfile Witcher3() => GameProfileStore.Parse(Witcher3Bytes);
}

public class GameProfileTests
{
    [Fact]
    public void The_witcher_3_seed_is_shipped_with_every_section()
    {
        var p = TestProfiles.Witcher3();

        Assert.Equal("witcher3", p.Id);
        Assert.Equal(["witcher3"], p.ProcessNames);
        Assert.Equal(("en", "vi"), (p.SourceLanguage, p.TargetLanguage));
        Assert.Equal(107, p.Keep.Count);
        Assert.Equal(109, p.Glossary.Count);
        Assert.Equal(33, p.Characters.Count);
        Assert.Equal(123, p.Characters.Sum(c => c.Address.Count));
        Assert.Equal(15, p.Style.Count);
        Assert.Equal(96, p.FixRules.Count); // the 79 reviewed rules, 4 replaced by guarded forms, and 17 guarded ones added
        Assert.Equal(26, p.FixRules.Count(r => r.Unless is not null));
    }

    [Fact]
    public void The_seed_has_no_key_the_model_does_not_know()
    {
        // Unknown keys would be kept (ProfilePart.Unknown) but never used: a typo in the seed must fail here.
        var p = TestProfiles.Witcher3();
        IEnumerable<ProfilePart> parts = [p, .. p.Glossary, .. p.Characters, .. p.Characters.SelectMany(c => c.Address), .. p.FixRules];

        Assert.All(parts, part => Assert.Null(part.Unknown));
    }

    [Fact]
    public void The_seed_is_consistent()
    {
        var p = TestProfiles.Witcher3();
        string text = Encoding.UTF8.GetString(TestProfiles.Witcher3Bytes);

        Assert.Equal(text.Normalize(NormalizationForm.FormC), text);
        AssertNoDuplicates(p.Keep);
        AssertNoDuplicates(p.Glossary.Select(g => g.En));
        AssertNoDuplicates(p.Characters.Select(c => c.Name));
        var names = p.Characters.Select(c => c.Name).ToHashSet();
        foreach (var c in p.Characters)
        {
            Assert.Contains(c.Gender, new[] { "male", "female" });
            AssertNoDuplicates(c.Address.Select(a => a.To));
            Assert.All(c.Address, a => Assert.True(names.Contains(a.To) || GameCharacter.Groups.Contains(a.To), $"{c.Name} -> {a.To}"));
            Assert.All(c.Address, a => Assert.NotEqual(c.Name, a.To));
        }
        Assert.All(p.FixRules, r => Assert.False(string.IsNullOrWhiteSpace(r.Trigger) || string.IsNullOrWhiteSpace(r.Wrong), r.Trigger));

        static void AssertNoDuplicates(IEnumerable<string> items)
        {
            var dupes = items.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dupes.Count == 0, "duplicates: " + string.Join(", ", dupes));
        }
    }

    [Fact]
    public void The_prompt_block_holds_keep_glossary_characters_and_style_and_is_stable()
    {
        var p = TestProfiles.Witcher3();

        string prompt = p.ToPrompt();

        Assert.Equal(prompt, TestProfiles.Witcher3().ToPrompt()); // the cached prefix must not change from call to call
        Assert.StartsWith("<game_profile>\nGame: The Witcher 3: Wild Hunt\n", prompt);
        Assert.EndsWith("</game_profile>", prompt);
        Assert.Contains("never translate: Geralt, Gwynbleidd, Ciri,", prompt);
        Assert.Contains("\nwitcher = witcher (lowercase common noun;", prompt);
        Assert.Contains("\nthe Continent = Lục Địa (only with a capital C)", prompt); // "LLM only:" is for the app, not the model
        Assert.DoesNotContain("LLM only", prompt);
        Assert.Contains("to Ciri ta/con; to Yennefer anh/em;", prompt);
        Assert.Contains("to children chú/cháu; to enemies ta/ngươi", prompt);
        Assert.Contains("\n- Never use bạn as a pronoun between characters", prompt);
        Assert.DoesNotContain("cá viền đỏ", prompt); // fix rules are for machine translation only
        int keep = prompt.IndexOf("Keep exactly", StringComparison.Ordinal), glossary = prompt.IndexOf("Glossary", StringComparison.Ordinal);
        int characters = prompt.IndexOf("Characters,", StringComparison.Ordinal), style = prompt.IndexOf("Style:", StringComparison.Ordinal);
        Assert.True(keep < glossary && glossary < characters && characters < style);
    }

    [Fact]
    public void Names_are_the_capitalized_keep_entries()
    {
        var p = TestProfiles.Witcher3();

        Assert.Contains("Ciri", p.Names);
        Assert.Contains("Kaer Morhen", p.Names);
        Assert.DoesNotContain("witcher", p.Names);
        Assert.Same(p.Names, p.Names); // one list while the profile is unchanged
        p.Keep.Add("Jaskier");
        p.Version++;
        Assert.Contains("Jaskier", p.Names);
    }

    [Theory]
    [InlineData("en", "vi", true)]
    [InlineData("auto", "vi", true)]
    [InlineData("EN", "VI", true)]
    [InlineData("en", "ja", false)]
    [InlineData("de", "vi", false)]
    public void A_profile_applies_only_to_its_language_pair(string src, string tgt, bool fits) =>
        Assert.Equal(fits, TestProfiles.Witcher3().Fits(src, tgt));

    [Theory]
    [InlineData("witcher3", true)]
    [InlineData("Witcher3.exe", true)]
    [InlineData("witcher3launcher", false)]
    public void The_game_is_known_by_its_process_name(string process, bool matches) =>
        Assert.Equal(matches, TestProfiles.Witcher3().MatchesProcess(process));
}

public class GameProfileStoreTests
{
    static GameProfileStore Store(TempDir dir, byte[]? seed = null) =>
        new(dir.Path, new Dictionary<string, byte[]> { ["witcher3.json"] = seed ?? TestProfiles.Witcher3Bytes });

    static byte[] SeedWithVersion(int version) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(TestProfiles.Witcher3Bytes).Replace("\"version\": 1,", $"\"version\": {version},"));

    [Fact]
    public void The_shipped_profile_is_written_on_first_use_byte_for_byte()
    {
        using var dir = new TempDir();
        var store = Store(dir);
        Assert.False(File.Exists(dir.File("witcher3.json"))); // nothing happens until a profile is asked for

        var profile = Assert.Single(store.Profiles);

        Assert.Equal("witcher3", profile.Id);
        Assert.Equal(TestProfiles.Witcher3Bytes, File.ReadAllBytes(dir.File("witcher3.json")));
        Assert.True(File.Exists(dir.File(GameProfileStore.SeedStateFile)));
        Assert.Empty(store.Problems);
    }

    [Fact]
    public void A_newer_shipped_version_replaces_a_file_the_user_never_changed()
    {
        using var dir = new TempDir();
        _ = Store(dir).Profiles;

        var updated = Store(dir, SeedWithVersion(2));

        Assert.Equal(2, Assert.Single(updated.Profiles).Version);
        Assert.Equal(SeedWithVersion(2), File.ReadAllBytes(dir.File("witcher3.json")));
    }

    [Fact]
    public void A_file_the_user_changed_is_never_overwritten()
    {
        using var dir = new TempDir();
        _ = Store(dir).Profiles;
        string edited = File.ReadAllText(dir.File("witcher3.json")).Replace("\"Geralt\", \"Gwynbleidd\"", "\"Geralt\", \"Gwynbleidd\", \"Jaskier\"");
        File.WriteAllText(dir.File("witcher3.json"), edited);

        var updated = Store(dir, SeedWithVersion(5));

        Assert.Contains("Jaskier", Assert.Single(updated.Profiles).Keep);
        Assert.Equal(edited, File.ReadAllText(dir.File("witcher3.json")));
    }

    [Fact]
    public void Without_the_record_of_what_was_written_a_file_counts_as_changed()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("witcher3.json"), TestProfiles.Witcher3Bytes); // a copy from another PC

        var store = Store(dir, SeedWithVersion(2));

        Assert.Equal(1, Assert.Single(store.Profiles).Version);
    }

    [Fact]
    public void Saving_an_edit_raises_the_version_and_keeps_fields_this_version_does_not_know()
    {
        using var dir = new TempDir();
        string json = Encoding.UTF8.GetString(TestProfiles.Witcher3Bytes).Replace("\"version\": 1,", "\"version\": 1, \"futureField\": { \"a\": 1 },");
        File.WriteAllText(dir.File("witcher3.json"), json);
        var store = Store(dir);
        var profile = store.Find("WITCHER3")!;

        profile.Keep.Add("Jaskier");
        store.Save(profile);

        var reread = Store(dir).Find("witcher3")!;
        Assert.Equal(2, reread.Version);
        Assert.Contains("Jaskier", reread.Keep);
        Assert.Equal(1, reread.Unknown!["futureField"].GetProperty("a").GetInt32());
        Assert.Contains("\"processNames\"", File.ReadAllText(dir.File("witcher3.json"))); // camelCase, like the seed
        Assert.Contains("Nữ Thần Hồ", File.ReadAllText(dir.File("witcher3.json"))); // Vietnamese stays readable

        var updated = Store(dir, SeedWithVersion(9)); // an edited profile is the user's from now on
        Assert.Equal(2, updated.Find("witcher3")!.Version);
    }

    [Fact]
    public void A_new_profile_gets_its_own_file()
    {
        using var dir = new TempDir();
        var store = Store(dir);
        var profile = new GameProfile { Id = "skyrim", Name = "Skyrim", ProcessNames = ["SkyrimSE"], Keep = ["Whiterun"] };

        store.Save(profile);

        Assert.Equal(2, profile.Version);
        Assert.True(File.Exists(dir.File("skyrim.json")));
        Assert.Equal(["skyrim", "witcher3"], Store(dir).Profiles.Select(p => p.Id).Order());
        Assert.Throws<ArgumentException>(() => store.Save(new GameProfile { Id = "a/b" }));
    }

    [Fact]
    public void The_profile_is_picked_by_the_game_process_unless_the_settings_name_one()
    {
        using var dir = new TempDir();
        var store = Store(dir);

        Assert.Equal("witcher3", store.Resolve(GameProfileStore.Auto, "witcher3", "en", "vi")?.Id);
        Assert.Null(store.Resolve(GameProfileStore.Auto, "notepad", "en", "vi"));
        Assert.Null(store.Resolve(GameProfileStore.Auto, null, "en", "vi")); // the game is not known yet
        Assert.Equal("witcher3", store.Resolve("witcher3", "notepad", "en", "vi")?.Id);
        Assert.Null(store.Resolve(GameProfileStore.Off, "witcher3", "en", "vi"));
        Assert.Null(store.Resolve("missing", "witcher3", "en", "vi"));
        Assert.Null(store.Resolve(GameProfileStore.Auto, "witcher3", "en", "ja")); // a Vietnamese glossary is no use for Japanese
    }

    [Fact]
    public void A_profile_saved_with_a_byte_order_mark_is_read()
    {
        // Notepad, Visual Studio and PowerShell 5 "Set-Content -Encoding UTF8" write one.
        using var dir = new TempDir();
        _ = Store(dir).Profiles;
        string edited = File.ReadAllText(dir.File("witcher3.json")).Replace("\"Geralt\", \"Gwynbleidd\"", "\"Geralt\", \"Gwynbleidd\", \"Jaskier\"");
        File.WriteAllText(dir.File("witcher3.json"), edited, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var store = Store(dir, SeedWithVersion(2));

        Assert.Empty(store.Problems);
        Assert.Contains("Jaskier", Assert.Single(store.Profiles).Keep);
        Assert.Equal(0xEF, File.ReadAllBytes(dir.File("witcher3.json"))[0]); // the user's file: never replaced by the newer seed
    }

    [Fact]
    public async Task Nulls_in_a_hand_edited_profile_read_as_empty()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("mine.json"), """
            { "id": "mine", "name": null, "processNames": ["mygame", null], "keep": null, "glossary": [null, { "en": "sword", "vi": null }],
              "characters": [{ "name": "Ana", "gender": null, "address": null }], "style": null,
              "fixRules": [{ "trigger": "Roach", "wrong": "cá viền đỏ", "right": null, "unless": [null, "cockroach"] }, null] }
            """);
        var store = Store(dir);

        var profile = store.Find("mine")!;
        var translated = await new ProfileTranslator(new EchoTranslator()).TranslateAsync(
            new TranslateRequest(["Roach, stay."], "en", "vi", [], profile), CancellationToken.None);

        Assert.Empty(store.Problems);
        Assert.Equal(("", 0, 0), (profile.Name, profile.Keep.Count, profile.Style.Count));
        Assert.Equal(["mygame"], profile.ProcessNames);
        Assert.Equal("", Assert.Single(profile.Glossary).Vi);
        Assert.Empty(Assert.Single(profile.Characters).Address);
        Assert.Equal(["cockroach"], Assert.Single(profile.FixRules).Unless);
        Assert.Equal(["vi:Roach, stay."], translated.Texts);
        Assert.Contains("<game_profile>", profile.ToPrompt());
    }

    [Fact]
    public void A_profile_whose_id_is_null_is_reported_and_the_others_still_load()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("noid.json"), """{ "id": null, "keep": ["Ana"] }""");

        var store = Store(dir);

        Assert.Equal("witcher3", Assert.Single(store.Profiles).Id);
        Assert.Contains(store.Problems, p => p.Contains("noid.json"));
    }

    [Fact]
    public void A_damaged_profile_is_reported_and_left_out()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("broken.json"), "{ \"id\": \"broken\", ");
        File.WriteAllText(dir.File("copy.json"), Encoding.UTF8.GetString(TestProfiles.Witcher3Bytes));

        var store = Store(dir);

        Assert.Equal("witcher3", Assert.Single(store.Profiles).Id);
        Assert.Equal(2, store.Problems.Count);
        Assert.Contains(store.Problems, p => p.Contains("broken.json"));
        Assert.Contains(store.Problems, p => p.Contains("same") || p.Contains("another profile"));
    }

    [Fact]
    public void A_new_profile_never_writes_over_a_file_that_could_not_be_read_or_holds_another_profile()
    {
        using var dir = new TempDir();
        const string typo = "{ \"id\": \"custom\", \"keep\": [\"Roach\" }"; // a hand edit with a typo, reported at startup
        File.WriteAllText(dir.File("custom.json"), typo);
        File.WriteAllBytes(dir.File("skyrim.json"), TestProfiles.Witcher3Bytes); // a copy to start a variant from: it loads as witcher3
        var store = Store(dir);
        Assert.Contains(store.Problems, p => p.Contains("custom.json"));

        Assert.Equal("custom-2", store.NewId("custom"));
        Assert.Equal("skyrim-2", store.NewId("skyrim"));
        Assert.Equal("gwent", store.NewId("gwent"));
        // An import of these ids, or a profile made in code, keeps clear of the two files all the same.
        store.Save(new GameProfile { Id = "custom", Name = "New profile" });
        store.Save(new GameProfile { Id = "skyrim", Name = "Skyrim" });

        Assert.Equal(typo, File.ReadAllText(dir.File("custom.json")));
        Assert.Equal(TestProfiles.Witcher3Bytes, File.ReadAllBytes(dir.File("skyrim.json")));
        Assert.Equal("custom", GameProfileStore.Parse(File.ReadAllBytes(dir.File("custom-2.json"))).Id);
        Assert.Equal("skyrim", GameProfileStore.Parse(File.ReadAllBytes(dir.File("skyrim-2.json"))).Id);
        Assert.Equal(["custom", "skyrim", "witcher3"], Store(dir).Profiles.Select(p => p.Id).Order());
    }

    [Fact]
    public void A_copy_edited_apart_replaces_the_profile_with_its_id_in_its_own_file()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("w3.json"), TestProfiles.Witcher3Bytes); // a profile the user named differently
        var store = Store(dir);
        var inUse = store.Find("witcher3")!;
        var copy = GameProfileStore.Copy(inUse);

        copy.Keep.Add("Jaskier");
        store.Save(copy);

        Assert.Same(copy, store.Find("witcher3"));
        Assert.Equal(2, copy.Version);
        Assert.Equal(1, inUse.Version);
        Assert.DoesNotContain("Jaskier", inUse.Keep); // translations running meanwhile never see the edit half done
        Assert.Contains("Jaskier", GameProfileStore.Parse(File.ReadAllBytes(dir.File("w3.json"))).Keep);
        Assert.Single(Store(dir).Profiles, p => p.Id == "witcher3");

        var older = TestProfiles.Witcher3(); // an import with an older version still goes above the one it replaces
        store.Save(older);
        Assert.Equal(3, store.Find("witcher3")!.Version);
    }
}
