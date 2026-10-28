using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
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
        settings.Translation.Claude.Model = "claude-opus-5-5"; // no model is picked until a key check
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
    public async Task A_test_chain_reports_no_switch_and_leaves_the_free_services_in_use_alone()
    {
        var registry = Offline();
        var engine = new FailingEngine();
        registry.Register(new ProviderRegistration("deepl", "DeepL", TakesInstructions: false, s => null, _ => engine));
        var switches = new List<ServiceSwitch>();
        registry.ServiceSwitched += (_, e) => switches.Add(e);
        var freeInUse = registry.Free("");
        var myMemoryInUse = registry.MyMemory(""); // it remembers when its quota ran out: a test must not replace it
        var settings = new TranslationSettings { Provider = "deepl" };
        settings.Free.MyMemoryEmail = "typed-not-saved@example.com";
        var request = new TranslateRequest(["Ciri, get down here!"], "en", "vi", [], TestProfiles.Witcher3());

        var tested = await registry.BuildDetached(settings).Translator.TranslateAsync(request, CancellationToken.None);

        Assert.Equal(["gg:Ciri, get down here!"], tested.Texts);
        Assert.Single(engine.Sent);
        Assert.Empty(switches); // the provider in use did not fail: no alert, no "provider down" on Home
        Assert.Same(myMemoryInUse, registry.MyMemory(""));
        Assert.Same(freeInUse, registry.Free(""));

        settings.Provider = ProviderIds.Free;
        settings.Free.MyMemoryEmail = " ";
        Assert.Same(freeInUse, registry.BuildDetached(settings).Translator); // the email in use: the same services
        Assert.Same(myMemoryInUse, registry.MyMemory(""));
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

    static ProviderSettings Section(TranslationSettings settings, string id) => id switch
    {
        ProviderIds.Claude => settings.Claude,
        ProviderIds.OpenAI => settings.OpenAI,
        ProviderIds.Gemini => settings.Gemini,
        ProviderIds.Compat => settings.Compat,
        ProviderIds.DeepL => settings.DeepL,
        ProviderIds.Azure => settings.Azure,
        ProviderIds.GoogleCloud => settings.GoogleCloud,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    static TranslationSettings Keyed(string id, string model = "")
    {
        var settings = new TranslationSettings { Provider = id };
        Section(settings, id).ApiKey = "test-key-not-a-real-one";
        Section(settings, id).Model = model;
        return settings;
    }

    [Fact]
    public void Every_provider_is_registered_once_with_its_key_check_fields_and_kind()
    {
        var registry = Offline();

        Assert.Equal([ProviderIds.Claude, ProviderIds.OpenAI, ProviderIds.Gemini, ProviderIds.Compat, ProviderIds.DeepL, ProviderIds.Azure, ProviderIds.GoogleCloud],
            registry.Providers.Select(p => p.Id));
        Assert.All(registry.Providers, p => Assert.Same(p.Provider, registry.Provider(p.Id)));
        Assert.All(registry.Providers, p => Assert.Equal(p.Provider!.DisplayName, p.DisplayName));
        Assert.Equal([ProviderKind.LLM, ProviderKind.LLM, ProviderKind.LLM, ProviderKind.LLM, ProviderKind.MT, ProviderKind.MT, ProviderKind.MT],
            registry.Providers.Select(p => p.Provider!.Kind));
        Assert.Equal([true, true, true, true, false, false, false], registry.Providers.Select(p => p.TakesInstructions));
        Assert.Equal([false, false, false, false, true, true, false], registry.Providers.Select(p => p.MarksKeepEntries));
        Assert.Null(registry.Provider(ProviderIds.Free));
    }

    [Theory]
    [InlineData(ProviderIds.Claude)]
    [InlineData(ProviderIds.OpenAI)]
    [InlineData(ProviderIds.Gemini)]
    public void A_keyed_llm_without_a_model_runs_on_the_free_services_with_a_notice(string id)
    {
        var chain = Offline().Build(Keyed(id));

        Assert.Equal(ProviderKeys.Free, chain.Translator.Name);
        Assert.Contains("is selected but no model is picked", chain.Notice);
    }

    [Theory]
    [InlineData(ProviderIds.DeepL, "deepl/latency_optimized")]
    [InlineData(ProviderIds.Azure, "azure/general")]
    [InlineData(ProviderIds.GoogleCloud, "googlecloud/nmt")]
    public void An_engine_provider_runs_on_its_default_engine_until_one_is_picked(string id, string name)
    {
        var chain = Offline().Build(Keyed(id));

        Assert.Null(chain.Notice);
        Assert.Equal(name, chain.Translator.Name);
    }

    [Fact]
    public void A_saved_model_is_used_as_it_is_even_one_no_check_listed()
    {
        var chain = Offline().Build(Keyed(ProviderIds.OpenAI, "gpt-5-mini"));

        Assert.Equal("openai/gpt-5-mini", chain.Translator.Name);
    }

    [Theory]
    [InlineData("", "the free services")]
    [InlineData("localhost:11434", "the free services")] // no scheme: not an address
    [InlineData("http://localhost:11434/v1", null)]
    public void The_compatible_provider_needs_a_valid_address_but_no_key(string baseUrl, string? notice)
    {
        var settings = new TranslationSettings { Provider = ProviderIds.Compat };
        settings.Compat.BaseUrl = baseUrl;
        settings.Compat.Model = "qwen3:8b";

        var chain = Offline().Build(settings);

        if (notice is null)
        {
            Assert.Null(chain.Notice);
            Assert.Equal("compat/qwen3:8b", chain.Translator.Name);
        }
        else
        {
            Assert.Contains("has no valid server address", chain.Notice);
        }
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", "", true)]
    [InlineData("https://api.groq.com/openai/v1", " ", true)]
    [InlineData("https://openrouter.ai/api/v1", "sk-or-test", false)]
    [InlineData("http://192.168.1.5:8080/v1", "", false)] // a server typed by hand may take no key
    public void A_compatible_cloud_preset_without_its_key_runs_on_the_free_services_with_a_notice(string baseUrl, string key, bool notice)
    {
        var settings = new TranslationSettings { Provider = ProviderIds.Compat };
        settings.Compat.BaseUrl = baseUrl;
        settings.Compat.ApiKey = key;
        settings.Compat.Model = "openai/gpt-6-luna";

        var chain = Offline().Build(settings);

        Assert.Equal(notice ? ProviderKeys.Free : "compat/openai/gpt-6-luna", chain.Translator.Name);
        if (notice) Assert.Contains("OpenAI-compatible is selected but has no API key", chain.Notice);
        else Assert.Null(chain.Notice);
    }

    [Fact]
    public async Task DeepL_keeps_names_with_its_own_tags_and_google_cloud_with_placeholders()
    {
        var deepl = new List<string>();
        var cloud = new List<string>();
        var registry = Offline();
        registry.Register(ProviderRegistration.For(new DeepLProvider(new HttpClient(new FakeHandler((_, body) =>
        {
            deepl.Add(body);
            return FakeHandler.Json("""{"translations":[{"text":"Thả <keep>Roach</keep> ra"}]}""");
        }))), s => s.DeepL));
        registry.Register(ProviderRegistration.For(new GoogleCloudProvider(new HttpClient(new FakeHandler((_, body) =>
        {
            cloud.Add(body);
            return FakeHandler.Json("""{"data":{"translations":[{"translatedText":"Thả QZX1 ra"}]}}""");
        }))), s => s.GoogleCloud));
        var request = new TranslateRequest(["Let Roach go"], "en", "vi", [], TestProfiles.Witcher3());

        var viaDeepl = await registry.Build(Keyed(ProviderIds.DeepL)).Translator.TranslateAsync(request, CancellationToken.None);
        var viaCloud = await registry.Build(Keyed(ProviderIds.GoogleCloud)).Translator.TranslateAsync(request, CancellationToken.None);

        Assert.Equal(["Thả Roach ra"], viaDeepl.Texts);
        Assert.Contains("<keep>Roach</keep>", deepl.Single());
        Assert.Equal(["Thả Roach ra"], viaCloud.Texts);
        Assert.Contains("QZX1", cloud.Single());
    }

    [Fact]
    public async Task Behind_the_cache_a_line_the_llm_left_out_is_translated_by_the_free_services_and_kept_under_their_key()
    {
        using var dir = new TempDir();
        var sent = new List<string>();
        var registry = Offline(sent, dir.File("cache.json"));
        var llm = new LeavesOutLastLine();
        registry.Register(new ProviderRegistration("llm", "LLM", TakesInstructions: true, s => null, _ => llm));
        var settings = new AppSettings();
        settings.Translation.Provider = "llm";
        var cache = registry.Apply(settings).Translator;
        var request = new TranslateRequest(["Where is Ciri?", "Hm?"], "en", "vi", [], null);

        var first = await cache.TranslateAsync(request, CancellationToken.None);
        var second = await cache.TranslateAsync(request, CancellationToken.None);

        Assert.Equal(["llm:Where is Ciri?", "gg:Hm?"], first.Texts);
        Assert.Equal(first.Texts, second.Texts);
        Assert.Null(first.FailedLines);
        Assert.Equal(["Hm?"], sent); // the free services' answer is kept under their key and answers again
        Assert.Equal(2, llm.Calls); // the LLM is asked again for the line it left out, and only for that one
        registry.Flush();
        Assert.Equal(["free-mt|en|vi|Hm?", "llm/test|en|vi|Where is Ciri?"], CachedTranslatorTests.FileKeys(dir).Order());
    }

    sealed class LeavesOutLastLine : ITranslator
    {
        public int Calls { get; private set; }
        public string Name => "llm/test";

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            throw new InvalidOperationException();

        public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
        {
            Calls++;
            int last = request.Lines.Count - 1;
            var texts = request.Lines.Select((l, i) => i == last ? l : "llm:" + l).ToList();
            return Task.FromResult(new TranslateResult(texts, request.Lines, Name) { FailedLines = [last] });
        }
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
