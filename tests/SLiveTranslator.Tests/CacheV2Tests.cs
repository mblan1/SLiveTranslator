using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>Cache version 2: provenance, hand fixes, junk that is never kept, profile versions, and the cache manager API.</summary>
public class CacheV2Tests
{
    static readonly TimeSpan NoBackgroundSave = TimeSpan.FromHours(1);

    static CachedTranslator Cache(TempDir dir, ITranslator inner, int maxEntries = 1000, TimeProvider? time = null) =>
        new(inner, dir.File("cache.json"), maxEntries, NoBackgroundSave, time);

    static Task<TranslateResult> Translate(CachedTranslator cache, GameProfile? profile, params string[] lines) =>
        cache.TranslateAsync(new TranslateRequest(lines, "en", "vi", [], profile), CancellationToken.None);

    static JsonElement FileRoot(TempDir dir) => JsonDocument.Parse(File.ReadAllText(dir.File("cache.json"))).RootElement;

    /// <summary>Answers what the test sets for each line, and counts what it was asked.</summary>
    sealed class Scripted(string name, Func<string, (string Text, string AnsweredBy)> answer) : ITranslator
    {
        public List<string> Asked { get; } = new();
        public string Name => name;

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            throw new InvalidOperationException("the cache sends requests");

        public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
        {
            Asked.AddRange(request.Lines);
            var answers = request.Lines.Select(answer).ToList();
            return Task.FromResult(new TranslateResult(answers.Select(a => a.Text).ToList(), null, answers[0].AnsweredBy)
            {
                LineAnsweredBy = answers.Select(a => a.AnsweredBy).ToList(),
            });
        }
    }

    static Scripted Vi(string name = "free-mt") => new(name, line => ("vi:" + line, "google"));

    /// <summary>
    /// The shape of the user's cache.json (version 1, 278 entries): 192 orphaned "free|" entries, "free-mt|" entries
    /// of which some only repeat their source, a hand fix, and lines with real Vietnamese.
    /// </summary>
    static Dictionary<string, string> UserShapedV1()
    {
        var v1 = new Dictionary<string, string>();
        for (int i = 0; i < 192; i++) v1[$"free|en|vi|Old line {i}."] = $"Dòng cũ {i}.";
        for (int i = 0; i < 70; i++) v1[$"free-mt|en|vi|Line {i}."] = $"Dòng {i}.";
        for (int i = 0; i < 13; i++) v1[$"free-mt|en|vi|Name{i}"] = $"Name{i}"; // the free services answered with the source
        v1["free-mt|en|vi|Space Skip"] = " ";
        v1["free-mt|en|vi|Ciri, get down here!"] = "Ciri, xuống đây!";
        v1["user|en|vi|Roach"] = "Roach"; // a hand fix may keep the source on purpose
        return v1;
    }

    [Fact]
    public async Task A_version_1_cache_loses_orphans_and_junk_and_is_kept_as_a_backup_before_the_first_save()
    {
        using var dir = new TempDir();
        string original = JsonSerializer.Serialize(UserShapedV1());
        File.WriteAllText(dir.File("cache.json"), original);
        var inner = Vi();
        var cache = Cache(dir, inner);

        Assert.Equal(["Dòng 3.", "Ciri, xuống đây!"], (await Translate(cache, null, "Line 3.", "Ciri, get down here!")).Texts);
        Assert.Empty(inner.Asked);
        Assert.Equal(72, cache.Count);
        await Translate(cache, null, "Old line 3.", "Name3");
        Assert.Equal(["Old line 3.", "Name3"], inner.Asked); // the orphan and the junk went
        Assert.False(File.Exists(cache.V1BackupPath)); // nothing is written before a save

        cache.Flush();

        Assert.Equal(original, File.ReadAllText(cache.V1BackupPath));
        var root = FileRoot(dir);
        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal(74, root.GetProperty("entries").GetArrayLength());
        var fix = root.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("p").GetString() == "user");
        Assert.True(fix.GetProperty("edited").GetBoolean());
        Assert.Equal("Roach", fix.GetProperty("tr").GetString());
    }

    [Fact]
    public async Task A_version_2_file_needs_no_backup_and_keeps_every_field()
    {
        using var dir = new TempDir();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        var first = Cache(dir, new Scripted("free-mt", line => ("vi:" + line, "mymemory-mt")), time: clock);
        var profile = TestProfiles.Witcher3();
        await Translate(first, profile, "Where is Ciri?");
        clock.Advance(TimeSpan.FromMinutes(5));
        await Translate(first, profile, "Where is Ciri?");
        first.Flush();

        var entry = Assert.Single(Cache(dir, Vi(), time: clock).Snapshot());

        Assert.False(File.Exists(first.V1BackupPath));
        Assert.Equal(new CacheEntry("free-mt", "en", "vi", "Where is Ciri?", "vi:Where is Ciri?", clock.Now.AddMinutes(-5), clock.Now, 1, false, 1, "mymemory-mt"), entry);
    }

    [Fact]
    public async Task A_hand_fix_answers_first_whatever_the_provider()
    {
        using var dir = new TempDir();
        var free = Vi();
        var cache = Cache(dir, free);
        await Translate(cache, null, "Roach");

        cache.SetUserOverride("en", "vi", "Roach", "Roach");
        var claude = Vi("claude/claude-opus-5-5");
        var fromFree = await Translate(cache, null, "Roach");
        cache.Inner = claude;
        var fromClaude = await Translate(cache, null, "  Roach ");

        Assert.Equal(["Roach"], fromFree.Texts);
        Assert.Equal(["Roach"], fromClaude.Texts);
        Assert.Equal("user", fromClaude.Provider);
        Assert.Equal(CachedTranslator.AnsweredFromCache, fromClaude.AnsweredByAt(0));
        Assert.Empty(claude.Asked);
        Assert.Single(free.Asked);
    }

    [Fact]
    public async Task A_fix_of_a_line_with_a_key_hint_is_stored_line_by_line_and_an_empty_fix_removes_it()
    {
        using var dir = new TempDir();
        var inner = Vi();
        var cache = Cache(dir, inner);

        cache.SetUserOverride("en", "vi", "Call Horse\nSpace Skip", "Gọi ngựa\n[Space] Bỏ qua");
        Assert.Equal(["Gọi ngựa", "[Space] Bỏ qua"], (await Translate(cache, null, "Call Horse", "Space Skip")).Texts);
        Assert.Throws<ArgumentException>(() => cache.SetUserOverride("en", "vi", "Call Horse\nSpace Skip", "Gọi ngựa"));

        cache.SetUserOverride("en", "vi", "Call Horse", "");
        Assert.Equal(["vi:Call Horse"], (await Translate(cache, null, "Call Horse")).Texts);
    }

    [Theory]
    [InlineData("", "google")]
    [InlineData("   ", "google")]
    [InlineData("Chúng ta là dòng họ nấu mì.", MyMemoryTranslator.AnsweredByCorpus)] // a film line from MyMemory's corpus
    [InlineData("Geralt!", MyMemoryTranslator.AnsweredByCorpus)] // the corpus had no match: a later try may translate it
    public async Task Junk_answers_are_never_kept(string answer, string answeredBy)
    {
        using var dir = new TempDir();
        var inner = new Scripted("free-mt", _ => (answer, answeredBy));
        var cache = Cache(dir, inner);

        await Translate(cache, null, "Geralt!");
        await Translate(cache, null, "Geralt!");

        Assert.Equal(2, inner.Asked.Count);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task An_answer_that_repeats_its_source_is_never_kept_on_disk_but_is_not_asked_for_again_this_run()
    {
        // "Hah!", "Hm. Mhm." and "Vesemir!" are their own translation: asking again only spends the free quota.
        using var dir = new TempDir();
        var inner = new Scripted("free-mt", line => (line, "mymemory-mt"));
        var cache = Cache(dir, inner);

        Assert.Equal(["Hah!"], (await Translate(cache, null, "Hah!")).Texts);
        var again = await Translate(cache, null, " Hah! ");
        cache.Flush();

        Assert.Equal(["Hah!"], again.Texts);
        Assert.Equal(CachedTranslator.AnsweredFromCache, again.AnsweredByAt(0));
        Assert.Single(inner.Asked);
        Assert.Equal(0, cache.Count);
        Assert.False(File.Exists(dir.File("cache.json")));
        var restarted = new Scripted("free-mt", line => (line, "mymemory-mt"));
        await Translate(Cache(dir, restarted), null, "Hah!");
        Assert.Single(restarted.Asked); // a new run asks once more
    }

    [Fact]
    public async Task A_batch_keeps_its_machine_translated_lines_and_drops_its_corpus_match()
    {
        using var dir = new TempDir();
        var inner = new Scripted("free-mt", line => line == "It was just a dream." ? ("Chúng ta là dòng họ nấu mì.", MyMemoryTranslator.AnsweredByCorpus) : ("vi:" + line, MyMemoryTranslator.AnsweredByMachine));
        var cache = Cache(dir, inner);

        await Translate(cache, null, "It was just a dream.", "Enough. Get down.");

        Assert.Equal(["Enough. Get down."], cache.Snapshot().Select(e => e.Source));
    }

    /// <summary>The free services as the registry builds them: the profile applied by placeholders and fix rules.</summary>
    static (CachedTranslator Cache, Scripted Mt) FreeServices(TempDir dir)
    {
        int round = 0;
        var mt = new Scripted("free-mt", line => ($"{line} #{++round}", "google"));
        return (Cache(dir, new ProfileTranslator(mt)), mt);
    }

    [Fact]
    public async Task A_profile_edit_translates_again_only_the_lines_it_can_change()
    {
        using var dir = new TempDir();
        var (cache, mt) = FreeServices(dir);
        var profile = TestProfiles.Witcher3();
        string[] lines = ["Where is Jaskier?", "The weather is fine.", "Ciri, get down here!"];
        await Translate(cache, profile, lines);
        cache.Set("free-mt", "en", "vi", "Ciri, get down here!", "Ciri, xuống đây ngay!");

        // Characters, style and glossary never reach free machine translation, and neither does a version number.
        profile.Characters[0].About += " Tired.";
        profile.Style.Add("Keep lines short.");
        profile.Glossary.Add(new GlossaryEntry { En = "weather", Vi = "thời tiết" });
        profile.Version += 3;
        Assert.Equal(["Where is Jaskier? #1", "The weather is fine. #2", "Ciri, xuống đây ngay!"], (await Translate(cache, profile, lines)).Texts);
        Assert.Equal(3, mt.Asked.Count);

        profile.Keep.Add("Jaskier"); // now a placeholder in the first line only
        profile.Version++;
        var again = await Translate(cache, profile, lines);

        Assert.Equal(["Where is Jaskier? #4", "The weather is fine. #2", "Ciri, xuống đây ngay!"], again.Texts); // the edit stays
        Assert.Equal("Where is QZX1?", mt.Asked[^1]);
        Assert.Equal(4, mt.Asked.Count);
        Assert.Equal(["The weather is fine. #2"], (await Translate(cache, null, "The weather is fine.")).Texts); // the profile does nothing to it
        Assert.Equal(4, mt.Asked.Count);
    }

    [Fact]
    public async Task A_fix_rule_edit_translates_again_the_lines_it_applies_to()
    {
        using var dir = new TempDir();
        var (cache, mt) = FreeServices(dir);
        var profile = TestProfiles.Witcher3();
        await Translate(cache, profile, "Two hundred crowns.", "The weather is fine.");

        profile.FixRules.Add(new FixRule { Trigger = "crowns", Wrong = "trăm", Right = "trăm đồng" });
        profile.Version++;
        await Translate(cache, profile, "Two hundred crowns.", "The weather is fine.");

        Assert.Equal(["Two hundred crowns.", "The weather is fine.", "Two hundred crowns."], mt.Asked);
    }

    [Fact]
    public async Task Another_profile_never_answers_with_an_entry_made_for_its_names()
    {
        // A profile's version says nothing about another profile: a line is current for the profile parts it holds.
        using var dir = new TempDir();
        var (cache, mt) = FreeServices(dir);
        var witcher = TestProfiles.Witcher3();
        var edited = TestProfiles.Witcher3();
        edited.Keep.Add("Jaskier");
        edited.Version = 7;

        await Translate(cache, edited, "Where is Jaskier?");
        var other = await Translate(cache, witcher, "Where is Jaskier?");

        Assert.Equal(["Where is Jaskier? #2"], other.Texts);
        Assert.Equal(["Where is QZX1?", "Where is Jaskier?"], mt.Asked);
    }

    [Fact]
    public async Task A_version_1_entry_stays_in_use_unless_the_profile_changes_its_line()
    {
        // The user's 86 free-mt entries were made without a profile: only the lines with names are translated again.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("cache.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["free-mt|en|vi|The weather is fine."] = "Thời tiết đẹp.",
            ["free-mt|en|vi|Roach, stay."] = "Con gián, ở lại.",
        }));
        var (cache, mt) = FreeServices(dir);

        var result = await Translate(cache, TestProfiles.Witcher3(), "The weather is fine.", "Roach, stay.");

        Assert.Equal(["Thời tiết đẹp.", "Roach, stay. #1"], result.Texts);
        Assert.Equal(["QZX1, stay."], mt.Asked);
    }

    [Fact]
    public async Task While_nothing_can_translate_a_line_its_translation_from_before_a_profile_edit_answers()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("cache.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["free-mt|en|vi|Roach, stay."] = "Roach, ở lại.",
        }));
        var down = new Scripted("free-mt", _ => throw new TranslatorUnavailableException("MyMemory free daily limit reached."));
        var cache = Cache(dir, new ProfileTranslator(down));

        var result = await Translate(cache, TestProfiles.Witcher3(), "Roach, stay.");

        Assert.Equal(["Roach, ở lại."], result.Texts);
        Assert.Equal(CachedTranslator.AnsweredFromCache, result.AnsweredByAt(0));
        Assert.Single(down.Asked); // it was asked first: the profile changed the line
        // A line with nothing to stand in still fails, so the watcher shows it as failed and retries.
        await Assert.ThrowsAsync<TranslatorUnavailableException>(() => Translate(cache, TestProfiles.Witcher3(), "Roach, stay.", "Where is the griffin?"));
    }

    /// <summary>Claude selected, the free services after it in their own bucket, as the registry builds them.</summary>
    static (CachedTranslator Cache, Scripted Claude, Scripted Free) ClaudeThenFree(TempDir dir, TimeProvider? time = null)
    {
        var claude = new Scripted("claude/claude-opus-5-5", _ => throw new TranslatorUnavailableException("credit balance too low"));
        var free = Vi();
        var cache = Cache(dir, free);
        cache.Inner = new FallbackTranslator("claude/claude-opus-5-5", claude, cache.Bucket(free)) { MembersAreProviders = true, Time = time ?? TimeProvider.System };
        return (cache, claude, free);
    }

    [Fact]
    public async Task An_answer_is_kept_under_the_provider_that_gave_it()
    {
        // Claude is selected but out of credit: the free services answer, and their answer must not pose as Claude's.
        using var dir = new TempDir();
        var (cache, _, _) = ClaudeThenFree(dir);

        var result = await Translate(cache, null, "Wind's howling.");

        Assert.Equal("free-mt", result.Provider);
        Assert.Equal("free-mt", cache.Snapshot().Single().Provider);
    }

    [Fact]
    public async Task While_the_selected_provider_fails_the_free_services_answer_a_line_again_from_their_own_entries()
    {
        using var dir = new TempDir();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var (cache, claude, free) = ClaudeThenFree(dir, clock);
        await Translate(cache, null, "Wind's howling.");

        await Translate(cache, null, "Wind's howling."); // Claude is passed over for a while
        clock.Advance(FallbackTranslator.DefaultPause + TimeSpan.FromSeconds(1));
        var again = await Translate(cache, null, "Wind's howling."); // Claude is asked again: maybe it has credit now

        Assert.Equal(["vi:Wind's howling."], again.Texts);
        Assert.Equal(2, claude.Asked.Count);
        Assert.Single(free.Asked); // the free services' entry answered, so MyMemory's quota is not spent twice
    }

    [Fact]
    public async Task A_hand_edit_of_the_free_services_shows_while_the_selected_provider_fails_and_is_never_replaced()
    {
        using var dir = new TempDir();
        var (cache, _, free) = ClaudeThenFree(dir);
        cache.Set("free-mt", "en", "vi", "Wind's howling.", "Gió đang hú.");

        var result = await Translate(cache, null, "Wind's howling.");

        Assert.Equal(["Gió đang hú."], result.Texts);
        Assert.Empty(free.Asked);
        Assert.Equal("Gió đang hú.", cache.Snapshot(new CacheFilter(Provider: "free-mt")).Single().Translation);
    }

    [Fact]
    public async Task A_line_the_selected_provider_translated_answers_from_its_entry()
    {
        using var dir = new TempDir();
        var free = Vi();
        var claude = new Scripted("claude/claude-opus-5-5", line => ("claude:" + line, "claude/claude-opus-5-5"));
        var cache = Cache(dir, free);
        cache.Inner = new FallbackTranslator("claude/claude-opus-5-5", claude, cache.Bucket(free)) { MembersAreProviders = true };

        await Translate(cache, null, "Wind's howling.");
        var again = await Translate(cache, null, "Wind's howling.");

        Assert.Equal(["claude:Wind's howling."], again.Texts);
        Assert.Single(claude.Asked);
        Assert.Empty(free.Asked);
        Assert.Equal("claude/claude-opus-5-5", cache.Snapshot().Single().Provider);
    }

    /// <summary>
    /// Answers as an LLM does (with the fixed sources): the text the test sets for each line, where null leaves the
    /// line out of the answer (a failed line, its text the source).
    /// </summary>
    sealed class Llm(string name, Func<string, string?> answer) : ITranslator
    {
        public List<string> Asked { get; } = new();
        public string Name => name;

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            throw new InvalidOperationException("the cache sends requests");

        public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
        {
            Asked.AddRange(request.Lines);
            var answers = request.Lines.Select(answer).ToList();
            var failed = Enumerable.Range(0, answers.Count).Where(i => answers[i] is null).ToList();
            return Task.FromResult(new TranslateResult(answers.Select((a, i) => a ?? request.Lines[i]).ToList(), request.Lines, name)
            {
                FailedLines = failed.Count > 0 ? failed : null,
            });
        }
    }

    [Fact]
    public async Task A_line_the_llm_left_out_is_neither_kept_nor_remembered_and_its_index_is_the_request_s()
    {
        using var dir = new TempDir();
        var llm = new Llm("claude/x", line => line == "Wolven Storm" ? null : "llm:" + line);
        var cache = Cache(dir, llm);
        await Translate(cache, null, "Ciri!");

        var result = await Translate(cache, null, "Ciri!", "Wolven Storm", "Geralt.");
        var again = await Translate(cache, null, "Wolven Storm");
        cache.Flush();

        Assert.Equal(["llm:Ciri!", "Wolven Storm", "llm:Geralt."], result.Texts);
        Assert.Equal([1], result.FailedLines); // the line's place in this request, though the first came from the cache
        Assert.Equal(["Ciri!", "Wolven Storm", "Geralt.", "Wolven Storm"], llm.Asked); // asked again: it is no answer that repeats its source
        Assert.Equal([0], again.FailedLines);
        Assert.Equal(["claude/x|en|vi|Ciri!", "claude/x|en|vi|Geralt."], CachedTranslatorTests.FileKeys(dir));
    }

    [Fact]
    public async Task An_llm_s_not_language_answer_is_not_kept_on_disk_but_is_not_asked_for_again_this_run()
    {
        // "Ilrn ooe" is OCR noise; asking again only costs another paid call with the same verdict.
        using var dir = new TempDir();
        var llm = new Llm("claude/x", line => line == "Ilrn ooe" ? "" : "llm:" + line);
        var cache = Cache(dir, llm);

        Assert.Equal(["", "llm:Hm."], (await Translate(cache, null, "Ilrn ooe", "Hm.")).Texts);
        var again = await Translate(cache, null, "Ilrn ooe");
        cache.Flush();

        Assert.Equal([""], again.Texts);
        Assert.Equal(CachedTranslator.AnsweredFromCache, again.AnsweredByAt(0));
        Assert.Equal(["Ilrn ooe", "Hm."], llm.Asked);
        Assert.Equal(["claude/x|en|vi|Hm."], CachedTranslatorTests.FileKeys(dir));
    }

    [Fact]
    public async Task The_cache_manager_lists_filters_and_counts_entries()
    {
        using var dir = new TempDir();
        var cache = Cache(dir, Vi());
        await Translate(cache, null, "Where is Yennefer?", "Health Potion");
        cache.Inner = Vi("claude/claude-opus-5-5");
        await Translate(cache, null, "Where is Yennefer?");
        cache.SetUserOverride("en", "vi", "Roach", "Roach");

        Assert.Equal(["Roach", "Where is Yennefer?", "Health Potion", "Where is Yennefer?"], cache.Snapshot().Select(e => e.Source)); // most recent first
        Assert.Equal(2, cache.Snapshot(new CacheFilter(Text: "YENNEFER")).Count);
        Assert.Single(cache.Snapshot(new CacheFilter(Text: "vi:health")));
        Assert.Equal(["Where is Yennefer?"], cache.Snapshot(new CacheFilter(Provider: "claude/claude-opus-5-5")).Select(e => e.Source));
        Assert.Equal(["Roach"], cache.Snapshot(new CacheFilter(Edited: true)).Select(e => e.Source));

        var stats = cache.Stats();
        Assert.Equal(4, stats.Entries);
        Assert.Equal([("free-mt", 2, 0), ("claude/claude-opus-5-5", 1, 0), ("user", 1, 1)], stats.Providers.Select(p => (p.Provider, p.Entries, p.Edited)).OrderByDescending(p => p.Entries).ThenBy(p => p.Edited));
        Assert.True(stats.Providers.All(p => p.Bytes > 100));
        Assert.Equal(stats.Bytes, stats.Providers.Sum(p => p.Bytes));
    }

    [Fact]
    public async Task Clear_removes_only_its_scope()
    {
        using var dir = new TempDir();
        var cache = Cache(dir, Vi());
        await Translate(cache, null, "a", "b");
        cache.Inner = Vi("claude");
        await Translate(cache, null, "a"); // a key from before keys named the model
        cache.Set("claude/claude-opus-5-5", "en", "vi", "c", "edited c");
        cache.SetUserOverride("en", "vi", "Roach", "Roach");

        Assert.Equal(1, cache.Clear(CacheScope.Orphans));
        Assert.Equal(2, cache.Clear(CacheScope.Provider, "free-mt"));
        Assert.Equal(0, cache.Clear(CacheScope.NonEdited));
        Assert.Equal(["Roach", "c"], cache.Snapshot().Select(e => e.Source));
        Assert.Throws<ArgumentNullException>(() => cache.Clear(CacheScope.Provider));
        Assert.Equal(2, cache.Clear(CacheScope.All));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.Remove("free-mt", "en", "vi", "a"));
    }

    [Fact]
    public async Task Export_and_import_merge_and_a_hand_fix_is_never_replaced_by_a_plain_entry()
    {
        using var dir = new TempDir();
        using var other = new TempDir();
        var source = Cache(dir, Vi());
        await Translate(source, null, "Where is Ciri?", "Roach");
        source.SetUserOverride("en", "vi", "Kill the ghouls.", "Giết lũ ghoul.");
        string export = dir.File("export.json");
        source.Export(export);

        var target = Cache(other, Vi());
        target.SetUserOverride("en", "vi", "Roach", "Roach"); // the user's own fix
        await Translate(target, null, "Kill the ghouls."); // a plain entry: the imported fix answers before it
        target.Set("free-mt", "en", "vi", "Where is Ciri?", "Ciri đâu rồi?"); // edited here: kept

        Assert.Equal(2, target.Import(export));

        Assert.Equal(["Ciri đâu rồi?", "Roach", "Giết lũ ghoul."], (await Translate(target, null, "Where is Ciri?", "Roach", "Kill the ghouls.")).Texts);
        Assert.Equal(5, target.Count);
    }

    [Fact]
    public async Task Importing_an_older_export_keeps_the_fixes_made_here_since_and_counts_them()
    {
        using var dir = new TempDir();
        var cache = Cache(dir, Vi());
        cache.SetUserOverride("en", "vi", "Roach", "Roach");
        cache.SetUserOverride("en", "vi", "Kill the ghouls.", "Giết lũ ghoul.");
        string export = dir.File("old-export.json");
        cache.Export(export);

        cache.SetUserOverride("en", "vi", "Roach", "Con Roach"); // fixed again today

        Assert.Equal(0, cache.Import(export, out int kept));
        Assert.Equal(1, kept); // the unchanged fix is the same text: nothing to keep
        Assert.Equal(["Con Roach"], (await Translate(cache, null, "Roach")).Texts);
    }

    [Fact]
    public async Task A_version_1_file_can_be_imported()
    {
        using var dir = new TempDir();
        string v1 = dir.File("old.json");
        File.WriteAllText(v1, JsonSerializer.Serialize(UserShapedV1()));
        var cache = Cache(dir, Vi());

        Assert.Equal(72, cache.Import(v1));
        Assert.Equal(["Dòng 5."], (await Translate(cache, null, "Line 5.")).Texts);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_kept_before_it_is_replaced()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("cache.json"), "{\"version\":2,\"entries\":[{\"p\":");
        var cache = Cache(dir, Vi());

        await Translate(cache, null, "a");
        cache.Flush();

        Assert.Equal("{\"version\":2,\"entries\":[{\"p\":", File.ReadAllText(cache.CorruptCopyPath));
        Assert.Equal(["free-mt|en|vi|a"], CachedTranslatorTests.FileKeys(dir));
    }

    [Fact]
    public async Task A_file_of_a_newer_version_is_kept_before_the_first_save_replaces_it()
    {
        // A newer build wrote it; this one drops what it cannot read, so going back must not lose the original.
        using var dir = new TempDir();
        string newer = """{"version":3,"entries":[{"p":"free-mt","s":"en","t":"vi","src":"a","tr":"vi:a","edited":true,"future":1}],"index":{}}""";
        File.WriteAllText(dir.File("cache.json"), newer);
        var cache = Cache(dir, Vi());

        Assert.Equal(["vi:a"], (await Translate(cache, null, "a")).Texts);
        await Translate(cache, null, "b");
        cache.Flush();

        Assert.Equal(newer, File.ReadAllText(cache.BackupPath(3)));
        Assert.Equal(["free-mt|en|vi|a", "free-mt|en|vi|b"], CachedTranslatorTests.FileKeys(dir));
    }

    [Fact]
    public async Task A_file_without_entries_counts_as_unreadable_and_is_kept()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("cache.json"), """{"version":2,"items":[]}""");
        var cache = Cache(dir, Vi());

        await Translate(cache, null, "a");
        cache.Flush();

        Assert.Equal("""{"version":2,"items":[]}""", File.ReadAllText(cache.CorruptCopyPath));
    }

    [Fact]
    public async Task A_run_of_hits_only_still_saves_the_order_of_use_and_the_hit_counts()
    {
        using var dir = new TempDir();
        var first = Cache(dir, Vi());
        await Translate(first, null, "a", "b");
        first.Flush();

        var second = Cache(dir, Vi());
        await Translate(second, null, "a");
        second.Flush(); // as on exit

        Assert.Equal(["free-mt|en|vi|b", "free-mt|en|vi|a"], CachedTranslatorTests.FileKeys(dir)); // least recently used first
        var hits = FileRoot(dir).GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("src").GetString()!, e => e.GetProperty("hits").GetInt32());
        Assert.Equal(1, hits["a"]);
        Assert.Equal(0, hits["b"]);
    }

    [Fact]
    public async Task Setting_an_empty_translation_removes_the_entry()
    {
        using var dir = new TempDir();
        var inner = Vi();
        var cache = Cache(dir, inner);
        await Translate(cache, null, "Wind's howling.");

        cache.Set("free-mt", "en", "vi", "Wind's howling.", "  ");

        Assert.Equal(0, cache.Count);
        Assert.Equal(["vi:Wind's howling."], (await Translate(cache, null, "Wind's howling.")).Texts); // translated again, never a blank line
        Assert.Equal(2, inner.Asked.Count);
    }

    [Fact]
    public async Task Edited_entries_are_never_evicted()
    {
        using var dir = new TempDir();
        var cache = Cache(dir, Vi(), maxEntries: 2);
        cache.Set("free-mt", "en", "vi", "x", "edited x");
        await Translate(cache, null, "y");
        await Translate(cache, null, "z");

        cache.Flush();

        Assert.Equal(["free-mt|en|vi|x", "free-mt|en|vi|z"], CachedTranslatorTests.FileKeys(dir));
    }
}
