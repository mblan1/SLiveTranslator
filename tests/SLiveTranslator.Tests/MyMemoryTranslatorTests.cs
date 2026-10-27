using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

public class MyMemoryTranslatorTests
{
    [Fact]
    public void Splits_long_text_under_the_byte_limit_without_losing_words()
    {
        var text = string.Join(" ", Enumerable.Repeat("The ancient blade hums with power.", 40));
        var pieces = MyMemoryTranslator.SplitByBytes(text, 480).ToList();

        Assert.True(pieces.Count > 1);
        Assert.All(pieces, p => Assert.True(Encoding.UTF8.GetByteCount(p) <= 480));
        Assert.Equal(text, string.Join(" ", pieces));
    }

    [Fact]
    public void Splits_cjk_text_without_spaces()
    {
        var text = string.Concat(Enumerable.Repeat("古老的剑散发着力量。", 30));
        var pieces = MyMemoryTranslator.SplitByBytes(text, 480).ToList();

        Assert.True(pieces.Count > 1);
        Assert.All(pieces, p => Assert.True(Encoding.UTF8.GetByteCount(p) <= 480));
        Assert.Equal(text, string.Concat(pieces));
    }

    [Fact]
    public async Task Detects_the_daily_quota_message()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(
            """{"responseData":{"translatedText":"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY."},"quotaFinished":true,"responseStatus":429}"""));
        var translator = new MyMemoryTranslator(null, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(["x"], "en", "vi", CancellationToken.None));
        Assert.Equal(TimeSpan.FromHours(1), ex.RetryAfter);
    }

    [Fact]
    public async Task Prefers_the_machine_translation_over_junk_corpus_matches()
    {
        // Real response shape for "Enough. Get down.": the top match is an unrelated crowd-sourced movie line.
        var handler = new FakeHandler((_, _) => FakeHandler.Json(
            """
            {"responseData":{"translatedText":"Tao yêu mày mà Linh Linh!","match":0.94},"quotaFinished":false,"responseStatus":200,
             "matches":[{"translation":"Tao yêu mày mà Linh Linh!","quality":0,"created-by":"Public_Corpora","match":0.94},
                        {"translation":"Đủ rồi. Cúi xuống.","quality":70,"created-by":"MT!","match":0.85}]}
            """));
        var translator = new MyMemoryTranslator(null, new HttpClient(handler));

        var result = await translator.TranslateAsync(["Enough. Get down."], "en", "vi", CancellationToken.None);

        Assert.Equal("Đủ rồi. Cúi xuống.", result[0]);
    }

    static HttpResponseMessage Answer(string translatedText, string? machine) => FakeHandler.Json(JsonSerializer.Serialize(new
    {
        responseData = new { translatedText, match = 0.9 },
        quotaFinished = false,
        responseStatus = 200,
        matches = machine is null
            ? new object[] { new Dictionary<string, object> { ["translation"] = translatedText, ["created-by"] = "Public_Corpora" } }
            : new object[] { new Dictionary<string, object> { ["translation"] = translatedText, ["created-by"] = "Public_Corpora" }, new Dictionary<string, object> { ["translation"] = machine, ["created-by"] = "MT!" } },
    }));

    [Fact]
    public async Task Each_line_says_whether_machine_translation_or_the_corpus_answered()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.Query.Contains("dream")
            ? Answer("Đó chỉ là một giấc mơ.", machine: null)
            : Answer("Tao yêu mày mà Linh Linh!", machine: "Đủ rồi. Cúi xuống."));
        var translator = new MyMemoryTranslator(null, new HttpClient(handler));

        var result = await ((ITranslator)translator).TranslateAsync(TranslateRequest.Plain(["Enough. Get down.", "It was just a dream."], "en", "vi"), CancellationToken.None);

        Assert.Equal(["Đủ rồi. Cúi xuống.", "Đó chỉ là một giấc mơ."], result.Texts);
        Assert.Equal([MyMemoryTranslator.AnsweredByMachine, MyMemoryTranslator.AnsweredByCorpus], result.LineAnsweredBy);
        Assert.Equal(MyMemoryTranslator.AnsweredByCorpus, result.AnsweredBy); // not every line is machine translation
    }

    [Fact]
    public async Task A_corpus_match_that_adds_a_dialog_dash_is_rejected()
    {
        // Seen in the user's cache: a subtitle-corpus line for an unrelated sentence, starting with "- ".
        var handler = new FakeHandler((_, _) => Answer("- Lão phù thủy già đang ngủ say...", machine: null));
        var translator = new MyMemoryTranslator(null, new HttpClient(handler));

        var result = await ((ITranslator)translator).TranslateAsync(TranslateRequest.Plain(["The old witcher's sleeping."], "en", "vi"), CancellationToken.None);

        Assert.Equal(["The old witcher's sleeping."], result.Texts); // untranslated, so the cache never keeps it
        Assert.Equal(MyMemoryTranslator.AnsweredByCorpus, result.AnsweredByAt(0));
    }

    [Theory]
    [InlineData("Hello", "- Xin chào", true)]
    [InlineData("Hello", "– Xin chào", true)]
    [InlineData("- Hello", "- Xin chào", false)] // the source is a dialog line itself
    [InlineData("Hello", "-5 độ", false)]
    [InlineData("Hello", "Xin chào - bạn", false)]
    public void A_dialog_dash_counts_only_at_the_start_of_the_answer(string source, string translated, bool adds) =>
        Assert.Equal(adds, MyMemoryTranslator.AddsDialogDash(source, translated));

    [Fact]
    public async Task Characters_sent_are_counted_per_local_day_and_kept_on_disk()
    {
        using var dir = new TempDir();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 28, 16, 0, 0, TimeSpan.Zero)); // 23:00 at UTC+7
        var usage = new DailyCharCounter(dir.File("mymemory-usage.json"), clock);
        var handler = new FakeHandler((_, _) => Answer("x", machine: "Thanh kiếm"));
        var translator = new MyMemoryTranslator("player@example.com", new HttpClient(handler), usage);

        await translator.TranslateAsync(["Sword", "Shield"], "en", "vi", CancellationToken.None);

        Assert.Equal(11, translator.CharsToday);
        Assert.Equal(50_000, translator.DailyLimit);
        usage.Flush(); // as on exit
        Assert.Equal(11, new DailyCharCounter(dir.File("mymemory-usage.json"), clock).Today); // after a restart
        clock.Advance(TimeSpan.FromHours(1.5)); // past local midnight
        Assert.Equal(0, translator.CharsToday);
        Assert.Equal(0, new DailyCharCounter(dir.File("mymemory-usage.json"), clock).Today);
        Assert.Equal(5_000, new MyMemoryTranslator().DailyLimit);
    }

    [Fact]
    public async Task Counting_never_waits_for_the_disk_and_a_burst_is_written_once_a_while_later()
    {
        using var dir = new TempDir();
        string file = dir.File("mymemory-usage.json");
        var usage = new DailyCharCounter(file, null, TimeSpan.FromMilliseconds(100));

        for (int i = 0; i < 40; i++) usage.Add(10); // a translate-screen pass of 40 labels
        Assert.False(File.Exists(file));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(file) && clock.ElapsedMilliseconds < 5_000) await Task.Delay(10);
        Assert.Equal(400, new DailyCharCounter(file).Today);
    }

    [Fact]
    public void Flush_writes_only_what_changed()
    {
        using var dir = new TempDir();
        string file = dir.File("mymemory-usage.json");
        var usage = new DailyCharCounter(file, null, TimeSpan.FromHours(1));

        usage.Flush();
        Assert.False(File.Exists(file));
        usage.Add(25);
        usage.Flush();

        Assert.Equal(25, new DailyCharCounter(file).Today);
    }

    [Fact]
    public async Task A_refused_request_is_not_counted()
    {
        using var dir = new TempDir();
        var usage = new DailyCharCounter(dir.File("mymemory-usage.json"));
        var handler = new FakeHandler((_, _) => FakeHandler.Json(
            """{"responseData":{"translatedText":"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY."},"quotaFinished":true,"responseStatus":429}"""));
        var translator = new MyMemoryTranslator(null, new HttpClient(handler), usage);

        await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(["Sword"], "en", "vi", CancellationToken.None));

        Assert.Equal(0, usage.Today);
        Assert.False(File.Exists(dir.File("mymemory-usage.json")));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Translates_from_english_live()
    {
        if (!TestEnvironment.LiveTestsEnabled) return; // calls the real MyMemory API, whose free daily quota the app shares
        var translator = new MyMemoryTranslator();
        var result = await translator.TranslateAsync(["Health Potion"], "en", "vi", CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result[0]));
        Assert.NotEqual("Health Potion", result[0]);
    }
}
