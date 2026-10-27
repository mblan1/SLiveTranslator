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

public class CachedTranslatorTests
{
    /// <summary>Saves only when flushed: a debounce long enough that a test never sees a background save.</summary>
    static readonly TimeSpan NoBackgroundSave = TimeSpan.FromHours(1);

    static CachedTranslator Cache(TempDir dir, ITranslator? inner = null, int maxEntries = 100, TimeSpan? saveDelay = null) =>
        new(inner ?? new EchoTranslator(), dir.File("cache.json"), maxEntries, saveDelay ?? NoBackgroundSave);

    static Task<IReadOnlyList<string>> Translate(CachedTranslator cache, params string[] texts) =>
        cache.TranslateAsync(texts, "en", "vi", CancellationToken.None);

    static List<string> FileKeys(TempDir dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(dir.File("cache.json")));
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    [Fact]
    public async Task A_cached_line_is_not_translated_again_and_survives_a_restart()
    {
        using var dir = new TempDir();
        var echo = new EchoTranslator();
        var cache = Cache(dir, echo);
        await Translate(cache, "Health Potion");
        await Translate(cache, "Health Potion");
        Assert.Single(echo.Requests);
        cache.Flush();

        var again = new EchoTranslator();
        Assert.Equal(["vi:Health Potion"], await Translate(Cache(dir, again), "Health Potion"));
        Assert.Empty(again.Requests);
    }

    [Fact]
    public async Task The_current_file_format_is_read()
    {
        // As written before: one flat object, non-ASCII escaped by the default encoder.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("cache.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["echo|en|vi|Enough. Get down."] = "Đủ rồi. Cúi xuống.",
            ["free|en|vi|Old entry"] = "Mục cũ",
        }));
        var echo = new EchoTranslator();

        var result = await Translate(Cache(dir, echo), "Enough. Get down.");

        Assert.Equal(["Đủ rồi. Cúi xuống."], result);
        Assert.Empty(echo.Requests);
    }

    [Fact]
    public async Task Above_the_cap_the_least_recently_used_entries_go()
    {
        using var dir = new TempDir();
        var cache = Cache(dir, maxEntries: 3);
        await Translate(cache, "a");
        await Translate(cache, "b");
        await Translate(cache, "c");
        await Translate(cache, "a"); // used again: now the newest
        await Translate(cache, "d");

        cache.Flush();

        Assert.Equal(["echo|en|vi|c", "echo|en|vi|a", "echo|en|vi|d"], FileKeys(dir)); // least recently used first
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public async Task The_order_of_use_survives_a_restart()
    {
        using var dir = new TempDir();
        var first = Cache(dir, maxEntries: 3);
        await Translate(first, "a");
        await Translate(first, "b");
        await Translate(first, "c");
        await Translate(first, "a");
        first.Flush();

        var second = Cache(dir, maxEntries: 3);
        await Translate(second, "d"); // evicts b, the least recently used before the restart
        second.Flush();

        Assert.Equal(["echo|en|vi|c", "echo|en|vi|a", "echo|en|vi|d"], FileKeys(dir));
    }

    [Fact]
    public async Task Hand_fixed_entries_are_never_evicted()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("cache.json"), """{"user|en|vi|Roach":"Roach","echo|en|vi|x":"vi:x"}""");
        var cache = Cache(dir, maxEntries: 2);
        await Translate(cache, "y");
        await Translate(cache, "z");

        cache.Flush();

        Assert.Equal(["user|en|vi|Roach", "echo|en|vi|z"], FileKeys(dir)); // the oldest that may go went
    }

    [Fact]
    public async Task A_save_that_was_cut_off_does_not_lose_the_cache()
    {
        // A crash during a save leaves a half-written temporary file; cache.json itself is only ever replaced whole.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("cache.json"), """{"echo|en|vi|Health Potion":"Bình thuốc hồi máu"}""");
        File.WriteAllText(dir.File("cache.json.tmp"), """{"echo|en|vi|Health Potion":"Bình thu""");
        var echo = new EchoTranslator();
        var cache = Cache(dir, echo);

        Assert.Equal(["Bình thuốc hồi máu"], await Translate(cache, "Health Potion"));
        await Translate(cache, "Iron Shield");
        cache.Flush();

        Assert.False(File.Exists(dir.File("cache.json.tmp")));
        Assert.Equal(["echo|en|vi|Health Potion", "echo|en|vi|Iron Shield"], FileKeys(dir));
        Assert.Equal(["Iron Shield"], echo.Requests);
    }

    [Fact]
    public async Task Nothing_is_written_until_a_line_was_added()
    {
        using var dir = new TempDir();
        var cache = Cache(dir);
        cache.Flush();
        Assert.False(File.Exists(dir.File("cache.json")));

        await Translate(cache, "a");
        Assert.False(File.Exists(dir.File("cache.json"))); // the save waits for the debounce
        cache.Flush();

        Assert.Equal(["echo|en|vi|a"], FileKeys(dir));
    }

    [Fact]
    public async Task The_cache_saves_itself_in_the_background_after_a_change()
    {
        using var dir = new TempDir();
        var cache = Cache(dir, saveDelay: TimeSpan.FromMilliseconds(50));

        await Translate(cache, "a", "b");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(dir.File("cache.json")) && clock.ElapsedMilliseconds < 5_000) await Task.Delay(10);
        Assert.Equal(["echo|en|vi|a", "echo|en|vi|b"], FileKeys(dir));
    }

    [Fact]
    public async Task Translating_while_saving_loses_nothing()
    {
        using var dir = new TempDir();
        var cache = Cache(dir, maxEntries: 10_000, saveDelay: TimeSpan.FromMilliseconds(1));
        using var done = new CancellationTokenSource();
        var saver = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                cache.Flush();
                await Task.Yield();
            }
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (int i = 0; i < 150; i++) await Translate(cache, $"line {worker}-{i}", $"line {worker}-{i % 10}");
        })));
        done.Cancel();
        await saver;
        cache.Flush();

        var keys = FileKeys(dir);
        Assert.Equal(8 * 150, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task Another_provider_gets_its_own_entries_without_reloading_the_cache()
    {
        using var dir = new TempDir();
        var cache = Cache(dir);
        await Translate(cache, "a");
        var other = new NamedTranslator("other");

        cache.Inner = other;
        var result = await Translate(cache, "a");

        Assert.Equal(["other:a"], result);
        Assert.Equal("other", cache.Name);
    }

    /// <summary>Answers at once with its name before the text.</summary>
    sealed class NamedTranslator(string name) : ITranslator
    {
        public string Name => name;

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(texts.Select(t => $"{name}:{t}").ToList());
    }
}
