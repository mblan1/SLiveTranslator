using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

public class ProviderRegistryTests
{
    /// <summary>Google answers "gg:" plus the text sent, MyMemory is blocked; no request leaves the PC.</summary>
    static ProviderRegistry Offline(List<string>? sentToGoogle = null, string? cachePath = null) => new(cachePath,
        google: new GoogleTranslator(new HttpClient(new FakeHandler((_, body) =>
        {
            string text = Uri.UnescapeDataString(body.Split("&q=")[1].Replace('+', ' '));
            sentToGoogle?.Add(text);
            return FakeHandler.Json($$"""[[["gg:{{text}}","{{text}}",null,null,3]]]""");
        }))),
        newMyMemory: email => new MyMemoryTranslator(email, new HttpClient(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)))));

    static TranslationSettings Claude(string key)
    {
        var settings = new TranslationSettings { Provider = ProviderIds.Claude };
        settings.Claude.ApiKey = key;
        return settings;
    }

    [Fact]
    public void The_free_services_answer_under_their_own_key()
    {
        var chain = Offline().Build(new TranslationSettings());

        Assert.Equal(ProviderKeys.Free, chain.Translator.Name);
        Assert.Null(chain.Notice);
        Assert.IsType<ProfileTranslator>(chain.Translator);
    }

    [Fact]
    public void A_provider_without_its_key_runs_on_the_free_services_with_a_notice()
    {
        var chain = Offline().Build(Claude(""));

        Assert.Equal(ProviderKeys.Free, chain.Translator.Name);
        Assert.Contains("Claude is selected but has no API key", chain.Notice);
    }

    [Fact]
    public void A_key_this_windows_user_cannot_decrypt_runs_on_the_free_services_with_a_notice()
    {
        var settings = new TranslationSettings { Provider = ProviderIds.Claude };
        settings.Claude.ApiKeyProtected = Convert.ToBase64String(new byte[64]); // from another user or PC

        var chain = Offline().Build(settings);

        Assert.Equal(ProviderKeys.Free, chain.Translator.Name);
        Assert.Contains("cannot be decrypted", chain.Notice);
    }

    [Fact]
    public void Claude_comes_first_and_the_free_services_last()
    {
        var settings = Claude("sk-ant-test-not-a-real-key");
        settings.Claude.Model = "claude-sonnet-5-5";

        var chain = Offline().Build(settings);

        Assert.Null(chain.Notice);
        Assert.Equal("claude/claude-sonnet-5-5", chain.Translator.Name); // the model is part of the cache key
        Assert.IsType<FallbackTranslator>(chain.Translator);
    }

    [Fact]
    public void Every_chain_goes_behind_the_one_cache_of_the_run()
    {
        using var dir = new TempDir();
        var registry = new ProviderRegistry(dir.File("cache.json"));
        var settings = new AppSettings();
        settings.Translation.Provider = ProviderIds.Claude;

        var first = registry.Apply(settings);
        settings.Translation.Claude.ApiKey = "sk-ant-test-not-a-real-key";
        settings.Storage.CacheMaxEntries = 500;
        var second = registry.Apply(settings);

        Assert.Same(registry.Cache, first.Translator);
        Assert.Same(registry.Cache, second.Translator);
        Assert.NotNull(first.Notice);
        Assert.Null(second.Notice);
        Assert.Equal("claude/claude-opus-5-5", registry.Cache!.Name);
        Assert.Equal(500, registry.Cache.MaxEntries);
    }

    [Fact]
    public void An_unknown_provider_runs_on_the_free_services()
    {
        var chain = Offline().Build(new TranslationSettings { Provider = "something-new" });

        Assert.Equal(ProviderKeys.Free, chain.Translator.Name);
        Assert.Null(chain.Notice);
    }

    [Fact]
    public async Task A_registered_provider_that_fails_hands_the_line_to_the_free_services_and_says_so_once()
    {
        var sent = new List<string>();
        var registry = Offline(sent);
        var engine = new FailingEngine();
        // A new provider is one registration; an engine without instructions gets the profile applied for it.
        registry.Register(new ProviderRegistration("deepl", "DeepL", TakesInstructions: false, s => null, _ => engine));
        var switches = new List<ServiceSwitch>();
        registry.ServiceSwitched += (_, e) => switches.Add(e);
        var chain = registry.Build(new TranslationSettings { Provider = "deepl" });
        var request = new TranslateRequest(["Ciri, get down here!"], "en", "vi", [], TestProfiles.Witcher3());

        var first = await chain.Translator.TranslateAsync(request, CancellationToken.None);
        await chain.Translator.TranslateAsync(request, CancellationToken.None);

        Assert.Equal(["QZX1, get down here!"], engine.Sent); // placeholders for the engine too; then it is passed over for a while
        Assert.Equal(["gg:Ciri, get down here!"], first.Texts);
        Assert.Equal(ProviderKeys.Free, first.Provider);
        Assert.Equal([new ServiceSwitch("deepl/test", ProviderKeys.Free, "DeepL quota exceeded.")], switches);
        // MyMemory is never asked while Google answers
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task Behind_the_cache_the_free_services_answer_a_line_they_translated_before_while_the_provider_is_down()
    {
        using var dir = new TempDir();
        var sent = new List<string>();
        var registry = Offline(sent, dir.File("cache.json"));
        var engine = new FailingEngine();
        registry.Register(new ProviderRegistration("deepl", "DeepL", TakesInstructions: false, s => null, _ => engine));
        var settings = new AppSettings();
        settings.Translation.Provider = "deepl";
        var cache = registry.Apply(settings).Translator;
        var request = new TranslateRequest(["Where is the griffin?"], "en", "vi", [], TestProfiles.Witcher3());

        await cache.TranslateAsync(request, CancellationToken.None);
        registry.Apply(settings); // a settings save: a new chain, so the engine is asked again
        var again = await cache.TranslateAsync(request, CancellationToken.None);

        Assert.Equal(["gg:Where is the griffin?"], again.Texts);
        Assert.Equal(2, engine.Sent.Count);
        Assert.Single(sent); // MyMemory's quota, or Google's patience, is not spent on it twice
        registry.Flush();
        Assert.Equal(["free-mt|en|vi|Where is the griffin?"], CachedTranslatorTests.FileKeys(dir));
    }

    [Fact]
    public async Task The_free_services_report_their_own_switches_through_the_registry()
    {
        var registry = new ProviderRegistry(
            google: new GoogleTranslator(new HttpClient(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)))),
            newMyMemory: email => new MyMemoryTranslator(email, new HttpClient(new FakeHandler((_, _) => FakeHandler.Json(
                """{"responseData":{"translatedText":"Thanh kiếm"},"responseStatus":200,"matches":[{"translation":"Thanh kiếm","created-by":"MT!"}]}""")))));
        var switches = new List<ServiceSwitch>();
        registry.ServiceSwitched += (_, e) => switches.Add(e);

        var result = await registry.Build(new TranslationSettings()).Translator.TranslateAsync(TranslateRequest.Plain(["Sword"], "en", "vi"), CancellationToken.None);

        Assert.Equal(["Thanh kiếm"], result.Texts);
        Assert.Equal("google", Assert.Single(switches).From);
        Assert.Equal("mymemory", switches[0].To);
        // a settings save builds the chain again, and the switch is not reported twice
        await registry.Build(new TranslationSettings()).Translator.TranslateAsync(TranslateRequest.Plain(["Sword"], "en", "vi"), CancellationToken.None);
        Assert.Single(switches);
    }

    sealed class FailingEngine : ITranslator
    {
        public List<string> Sent { get; } = new();
        public string Name => "deepl/test";

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
        {
            Sent.AddRange(texts);
            throw new TranslatorUnavailableException("DeepL quota exceeded.");
        }
    }
}
