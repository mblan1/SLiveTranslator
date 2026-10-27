using System;
using System.Linq;
using System.Net.Http;
using System.Text;
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
