using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>DeepL, Azure Translator and Google Cloud Translation: no model list, a fixed engine after the key check.</summary>
public class MachineTranslationProviderTests
{
    // ---- DeepL ----

    [Theory]
    [InlineData("abc-123:fx", "https://api-free.deepl.com/v2/usage")]
    [InlineData("abc-123", "https://api.deepl.com/v2/usage")]
    public async Task A_deepl_free_key_goes_to_the_free_api_with_the_key_in_the_header(string key, string url)
    {
        var sent = new List<HttpRequestMessage>();
        var handler = new FakeHandler((request, _) =>
        {
            sent.Add(request);
            return request.RequestUri!.AbsolutePath == "/v2/usage"
                ? FakeHandler.Json("""{"character_count":180000,"character_limit":500000}""")
                : FakeHandler.Json("""[{"language":"en","name":"English"},{"language":"vi","name":"Vietnamese"}]""");
        });

        var check = await new DeepLProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key(key), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal("", check.Detail);
        Assert.Equal(url, sent[0].RequestUri!.ToString());
        Assert.Equal("DeepL-Auth-Key " + key, ProviderKit.Header(sent[0], "Authorization"));
        Assert.Equal("/v3/languages", sent[1].RequestUri!.AbsolutePath);
        Assert.Equal("?resource=translate_text", sent[1].RequestUri!.Query);
        Assert.Equal(new ProviderUsage(180000, 500000, "characters"), check.Usage);
        Assert.Equal([DeepLProvider.LatencyOptimized, DeepLProvider.QualityOptimized], check.Options.Select(o => o.Id));
        Assert.Equal(ModelTags.Recommended | ModelTags.Fastest, check.Options[0].Tags);
    }

    [Theory]
    [InlineData("""{"character_count":123,"character_limit":1000000000000}""", "[]", CheckStatus.Ok, null, true)]
    [InlineData("""{"character_count":500000,"character_limit":500000}""", """[{"language":"vi"}]""", CheckStatus.NoCredit, 500000.0, false)]
    // A Pro key with a limit of its own, used up, on an account without one.
    [InlineData("""{"character_count":2000000,"character_limit":1000000000000,"api_key_character_count":500000,"api_key_character_limit":500000}""",
        """[{"language":"vi"}]""", CheckStatus.NoCredit, 500000.0, false, 500000.0)]
    // The account has less left than the key: the account's quota runs out first.
    [InlineData("""{"character_count":450000,"character_limit":500000,"api_key_character_count":10,"api_key_character_limit":1000000}""",
        """[{"language":"vi"}]""", CheckStatus.Ok, 500000.0, false, 450000.0)]
    [InlineData("""{"character_count":7,"character_limit":1000000000000,"api_key_character_count":7,"api_key_character_limit":1000000000000}""",
        """[{"language":"vi"}]""", CheckStatus.Ok, null, false, 7.0)]
    public async Task Deepl_usage_shows_no_limit_as_unlimited_and_a_full_quota_as_no_credit(string usage, string languages, CheckStatus status, double? limit, bool noVietnamese,
        double? used = null)
    {
        var handler = new FakeHandler((request, _) => FakeHandler.Json(request.RequestUri!.AbsolutePath == "/v2/usage" ? usage : languages));

        var check = await new DeepLProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key("k:fx"), CancellationToken.None);

        Assert.Equal(status, check.Status);
        Assert.Equal(limit, check.Usage!.Limit);
        if (used is not null) Assert.Equal(used, check.Usage.Used);
        Assert.Equal(noVietnamese, check.Detail.Contains("does not list Vietnamese"));
    }

    [Theory]
    [InlineData(403, CheckStatus.InvalidKey)]
    [InlineData(456, CheckStatus.NoCredit)]
    [InlineData(429, CheckStatus.RateLimited)]
    [InlineData(500, CheckStatus.Failed)]
    public async Task A_failed_deepl_check_says_why(int status, CheckStatus expected)
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(status, """{"message":"Quota exceeded"}"""));

        var check = await new DeepLProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key("k"), CancellationToken.None);

        Assert.Equal(expected, check.Status);
        Assert.Equal($"DeepL: HTTP {status}: Quota exceeded", check.Detail);
    }

    [Fact]
    public async Task Deepl_gets_the_lines_with_the_context_and_its_names_in_ignored_tags()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(
            """{"translations":[{"detected_source_language":"EN","text":"Thả <keep>Roach</keep> ra &amp; đi"},{"detected_source_language":"EN","text":"Không."}]}"""));
        var translator = new DeepLProvider(new HttpClient(handler)).Create(ProviderKit.Config(DeepLProvider.QualityOptimized));

        var result = await translator.TranslateAsync(ProviderKit.Request("Let Roach go & leave", "No."), CancellationToken.None);

        Assert.Equal(["Thả Roach ra & đi", "Không."], result.Texts);
        Assert.Equal("deepl/quality_optimized", translator.Name);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var root = body.RootElement;
        Assert.Equal(["Let <keep>Roach</keep> go &amp; leave", "No."], root.GetProperty("text").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("VI", root.GetProperty("target_lang").GetString());
        Assert.Equal("EN", root.GetProperty("source_lang").GetString());
        Assert.Equal("Hm.", root.GetProperty("context").GetString());
        Assert.Equal("quality_optimized", root.GetProperty("model_type").GetString());
        Assert.Equal("xml", root.GetProperty("tag_handling").GetString());
        Assert.Equal(["keep"], root.GetProperty("ignore_tags").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Deepl_lines_without_names_go_as_plain_text()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"translations":[{"text":"A & B"}]}"""));

        var result = await new DeepLProvider(new HttpClient(handler)).Create(ProviderKit.Config("")).TranslateAsync(
            TranslateRequest.Plain(["A & B"], "auto", "en"), CancellationToken.None);

        Assert.Equal(["A & B"], result.Texts);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.False(body.RootElement.TryGetProperty("tag_handling", out _));
        Assert.False(body.RootElement.TryGetProperty("source_lang", out _));
        Assert.False(body.RootElement.TryGetProperty("context", out _));
        Assert.Equal("EN-US", body.RootElement.GetProperty("target_lang").GetString());
        Assert.Equal("latency_optimized", body.RootElement.GetProperty("model_type").GetString()); // the default engine
    }

    [Theory]
    [InlineData(456, null, 600)]
    [InlineData(429, "5", 5)]
    [InlineData(403, null, 600)]
    public async Task A_deepl_failure_while_translating_pauses_it_as_long_as_it_lasts(int status, string? retryAfter, int seconds)
    {
        var translator = new DeepLProvider(new HttpClient(new FakeHandler((_, _) => ProviderKit.Answer(status, """{"message":"m"}""", retryAfter))))
            .Create(ProviderKit.Config(DeepLProvider.LatencyOptimized));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(seconds), ex.RetryAfter);
    }

    [Theory]
    [InlineData("vi", "VI")]
    [InlineData("en", "EN-US")]
    [InlineData("pt", "PT-BR")]
    [InlineData("zh-CN", "ZH-HANS")]
    [InlineData("zh-TW", "ZH-HANT")]
    [InlineData("ja", "JA")]
    public void Deepl_target_codes_name_a_region_where_deepl_needs_one(string tgt, string code) => Assert.Equal(code, DeepLTranslator.Target(tgt));

    // ---- Azure Translator ----

    [Fact]
    public async Task Azure_gets_the_key_the_region_and_utf8_json_with_names_as_dictionary_entries()
    {
        HttpRequestMessage? sent = null;
        var handler = new FakeHandler((request, _) =>
        {
            sent = request;
            return FakeHandler.Json("""[{"translations":[{"text":"Thả Roach ra","to":"vi"}]},{"translations":[{"text":"Không.","to":"vi"}]}]""");
        });
        var translator = new AzureProvider(new HttpClient(handler)).Create(new ProviderConfig(new ProviderCredentials("az-key", "southeastasia"), ""));

        var result = await translator.TranslateAsync(ProviderKit.Request("Let Roach go", "No."), CancellationToken.None);

        Assert.Equal(["Thả Roach ra", "Không."], result.Texts);
        Assert.Equal("azure/general", translator.Name);
        Assert.Equal("https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from=en&to=vi", sent!.RequestUri!.ToString());
        Assert.Equal("az-key", ProviderKit.Header(sent, "Ocp-Apim-Subscription-Key"));
        Assert.Equal("southeastasia", ProviderKit.Header(sent, "Ocp-Apim-Subscription-Region"));
        Assert.Equal("application/json; charset=UTF-8", ProviderKit.Header(sent, "Content-Type"));
        Assert.Equal("""[{"Text":"Let <mstrans:dictionary translation=\"Roach\">Roach</mstrans:dictionary> go"},{"Text":"No."}]""", handler.Bodies.Single());
    }

    [Fact]
    public async Task Azure_without_a_region_sends_none_and_an_unknown_source_gets_no_markup()
    {
        HttpRequestMessage? sent = null;
        var handler = new FakeHandler((request, _) =>
        {
            sent = request;
            return FakeHandler.Json("""[{"translations":[{"text":"Thả Roach ra","to":"vi"}]}]""");
        });

        await new AzureProvider(new HttpClient(handler)).Create(ProviderKit.Config("general"))
            .TranslateAsync(new TranslateRequest(["Let Roach go"], "auto", "vi", [], TestProfiles.Witcher3()), CancellationToken.None);

        Assert.Null(ProviderKit.Header(sent!, "Ocp-Apim-Subscription-Region"));
        Assert.Equal("https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=vi", sent!.RequestUri!.ToString());
        Assert.Equal("""[{"Text":"Let Roach go"}]""", handler.Bodies.Single()); // the dictionary needs a known source
    }

    [Fact]
    public async Task An_azure_check_translates_one_character_and_offers_the_general_engine()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""[{"translations":[{"text":"một","to":"vi"}]}]"""));

        var check = await new AzureProvider(new HttpClient(handler)).CheckAsync(new ProviderCredentials("az-key", "westeurope"), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal("""[{"Text":"a"}]""", handler.Bodies.Single());
        Assert.Equal(new ModelOption(AzureProvider.General, "General (neural)", ModelTags.Recommended | ModelTags.Fastest), Assert.Single(check.Options));
        Assert.Null(check.Usage);
    }

    [Theory]
    [InlineData(401, 401000, CheckStatus.InvalidKey)]
    [InlineData(401, 401015, CheckStatus.InvalidKey)] // a Speech key
    [InlineData(403, 403001, CheckStatus.NoCredit)] // the free quota is used up
    [InlineData(403, 403000, CheckStatus.NoPermission)]
    [InlineData(429, 429001, CheckStatus.RateLimited)]
    [InlineData(408, 408001, CheckStatus.Failed)]
    [InlineData(400, 400036, CheckStatus.Failed)]
    public async Task A_failed_azure_check_says_why_by_the_error_code(int status, int code, CheckStatus expected)
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(status, $$$"""{"error":{"code":{{{code}}},"message":"The service's own words."}}"""));

        var check = await new AzureProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key("k"), CancellationToken.None);

        Assert.Equal(expected, check.Status);
        Assert.Equal($"Azure Translator: HTTP {status} {code}: The service's own words.", check.Detail);
    }

    [Fact]
    public async Task An_azure_system_still_being_prepared_is_asked_again_in_a_few_minutes()
    {
        var translator = new AzureProvider(new HttpClient(new FakeHandler((_, _) => ProviderKit.Answer(408, """{"error":{"code":408001,"message":"preparing"}}"""))))
            .Create(ProviderKit.Config("general"));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Equal(TimeSpan.FromMinutes(2), ex.RetryAfter);
    }

    // ---- Google Cloud Translation ----

    static readonly DateTimeOffset Afternoon = new(2026, 10, 28, 20, 0, 0, TimeSpan.Zero); // 13:00 in Los Angeles (PDT)

    [Fact]
    public async Task Google_cloud_gets_plain_text_with_the_source_and_its_key_in_the_query()
    {
        HttpRequestMessage? sent = null;
        var handler = new FakeHandler((request, _) =>
        {
            sent = request;
            return FakeHandler.Json("""{"data":{"translations":[{"translatedText":"Thả QZX1 ra"}]}}""");
        });
        var translator = new GoogleCloudProvider(new HttpClient(handler)).Create(ProviderKit.Config(""));

        var result = await translator.TranslateAsync(TranslateRequest.Plain(["Let QZX1 go"], "en", "vi"), CancellationToken.None);

        Assert.Equal(["Thả QZX1 ra"], result.Texts);
        Assert.Equal("googlecloud/nmt", translator.Name);
        Assert.Equal($"https://translation.googleapis.com/language/translate/v2?key={ProviderKit.TestKey}", sent!.RequestUri!.ToString());
        Assert.Equal("""{"q":["Let QZX1 go"],"target":"vi","format":"text","model":"nmt","source":"en"}""", handler.Bodies.Single());
    }

    [Fact]
    public async Task Google_cloud_sends_at_most_128_lines_a_request()
    {
        var handler = new FakeHandler((_, body) =>
        {
            int count = JsonDocument.Parse(body).RootElement.GetProperty("q").GetArrayLength();
            return FakeHandler.Json(JsonSerializer.Serialize(new { data = new { translations = Enumerable.Range(0, count).Select(i => new { translatedText = "t" }) } }));
        });

        var result = await new GoogleCloudProvider(new HttpClient(handler)).Create(ProviderKit.Config("nmt"))
            .TranslateAsync(TranslateRequest.Plain(Enumerable.Range(0, 130).Select(i => "line " + i).ToList(), "en", "vi"), CancellationToken.None);

        Assert.Equal(130, result.Texts.Count);
        Assert.Equal([128, 2], handler.Bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("q").GetArrayLength()));
    }

    [Theory]
    [InlineData(400, "keyInvalid", "API key not valid. Please pass a valid API key.", CheckStatus.InvalidKey)]
    [InlineData(403, "dailyLimitExceeded", "Daily Limit Exceeded", CheckStatus.RateLimited)]
    [InlineData(403, "userRateLimitExceeded", "User Rate Limit Exceeded", CheckStatus.RateLimited)]
    [InlineData(403, "accessNotConfigured", "Cloud Translation API has not been used in project 123 before or it is disabled.", CheckStatus.NoPermission)]
    [InlineData(429, "rateLimitExceeded", "Rate Limit Exceeded", CheckStatus.RateLimited)]
    [InlineData(500, "backendError", "Internal error", CheckStatus.Failed)]
    public async Task A_failed_google_cloud_check_says_why(int status, string reason, string message, CheckStatus expected)
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(status,
            $$$"""{"error":{"code":{{{status}}},"message":"{{{message}}}","errors":[{"message":"{{{message}}}","domain":"global","reason":"{{{reason}}}"}]}}"""));

        var check = await new GoogleCloudProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(expected, check.Status);
        Assert.Equal($"Google Cloud Translation: HTTP {status} {reason}: {message}", check.Detail);
        Assert.DoesNotContain(ProviderKit.TestKey, check.Detail);
    }

    [Theory]
    [InlineData("dailyLimitExceeded", "Daily Limit Exceeded", 11 * 3600)] // until midnight in Los Angeles
    [InlineData("userRateLimitExceeded", "User Rate Limit Exceeded", 10)] // per minute: it passes quickly
    public async Task A_google_cloud_quota_pauses_it_until_the_quota_is_back(string reason, string message, int seconds)
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(403, $$$"""{"error":{"code":403,"message":"{{{message}}}","errors":[{"reason":"{{{reason}}}"}]}}"""));
        var translator = new GoogleCloudProvider(new HttpClient(handler), new TestClock(Afternoon)).Create(ProviderKit.Config("nmt"));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(TranslateRequest.Plain(["x"], "en", "vi"), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(seconds), ex.RetryAfter);
    }

    [Fact]
    public void Each_engine_provider_says_how_it_keeps_names_and_which_engine_runs_without_a_pick()
    {
        Assert.Equal((ProfileHandling.Markup, "latency_optimized", ProviderKind.MT), (new DeepLProvider().Profile, new DeepLProvider().DefaultModel, new DeepLProvider().Kind));
        Assert.Equal((ProfileHandling.Markup, "general"), (new AzureProvider().Profile, new AzureProvider().DefaultModel));
        Assert.Equal((ProfileHandling.Placeholders, "nmt"), (new GoogleCloudProvider().Profile, new GoogleCloudProvider().DefaultModel));
        Assert.Equal([new ProviderFieldSpec(ProviderField.ApiKey, true), new ProviderFieldSpec(ProviderField.Region, false)], new AzureProvider().Fields);
    }
}
