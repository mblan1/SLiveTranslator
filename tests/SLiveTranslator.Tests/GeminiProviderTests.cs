using System;
using System.Collections.Concurrent;
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

public class GeminiProviderTests
{
    static string Model(string id, params string[] methods) => JsonSerializer.Serialize(new
    {
        name = "models/" + id, displayName = id.Replace('-', ' '), supportedGenerationMethods = methods.Length > 0 ? methods : ["generateContent", "countTokens"],
    });

    static readonly string[] Catalog =
    [
        Model("gemini-3.5-flash"), // deprecated by Google, but the list cannot tell: the highest version wins
        Model("gemini-3.8-flash"),
        Model("gemini-3.6-flash"),
        Model("gemini-3.5-flash-lite"),
        Model("gemini-3.1-flash-lite"),
        Model("gemini-4-pro"),
        Model("gemini-3.8-flash-preview-09-2026"),
        Model("gemini-3-flash-exp"),
        Model("gemini-flash-latest"),
        Model("gemini-3.8-flash-lite-tts"),
        Model("gemini-3.5-live-translate-preview"),
        Model("gemini-3.8-flash-image"),
        Model("gemini-omni-1.1-flash"),
        Model("gemini-2.0-flash-001"),
        Model("gemini-embedding-2", "embedContent"),
        Model("gemma-3-27b-it"),
    ];

    static string Answer(string text, string finish = "STOP") => JsonSerializer.Serialize(new
    {
        candidates = new[]
        {
            new
            {
                content = new { role = "model", parts = new object[] { new { text = "thinking about it", thought = true }, new { text } } },
                finishReason = finish,
            },
        },
        usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 5, thoughtsTokenCount = 3 },
    });

    const string Items = """{"items":[{"id":0,"source":"Ciri, get down here!","text":"Ciri, xuống đây!"}]}""";

    static string Error(int code, string status, string message, string details = "") =>
        $$$"""{"error":{"code":{{{code}}},"message":"{{{message}}}","status":"{{{status}}}","details":[{{{details}}}]}}""";

    [Fact]
    public void Only_stable_text_models_are_offered_and_the_highest_flash_is_recommended_and_flash_lite_fastest()
    {
        var options = GeminiProvider.Options(Catalog.Select(json =>
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            return new GeminiModel(m.GetProperty("name").GetString()!["models/".Length..], m.GetProperty("displayName").GetString()!,
                m.GetProperty("supportedGenerationMethods").EnumerateArray().Select(e => e.GetString()!).ToList());
        }).ToList());

        Assert.Equal(["gemini-4-pro", "gemini-3.8-flash", "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite"],
            options.Select(o => o.Id));
        Assert.Equal("gemini-3.8-flash", options.Single(o => o.Tags == ModelTags.Recommended).Id);
        Assert.Equal("gemini-3.5-flash-lite", options.Single(o => o.Tags == ModelTags.Fastest).Id);
    }

    [Fact]
    public async Task The_check_reads_every_page_with_the_key_in_a_header()
    {
        var sent = new List<HttpRequestMessage>();
        var handler = new FakeHandler((request, _) =>
        {
            sent.Add(request);
            return request.RequestUri!.Query.Contains("pageToken=next%2F1")
                ? FakeHandler.Json($$"""{"models":[{{string.Join(",", Catalog[3..])}}]}""")
                : FakeHandler.Json($$"""{"models":[{{string.Join(",", Catalog[..3])}}],"nextPageToken":"next/1"}""");
        });

        var check = await new GeminiProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal(6, check.Options.Count);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000", sent[0].RequestUri!.ToString());
        Assert.Equal(ProviderKit.TestKey, ProviderKit.Header(sent[0], "x-goog-api-key"));
        Assert.DoesNotContain(ProviderKit.TestKey, sent[0].RequestUri!.ToString()); // never in the URL
        Assert.Equal(2, sent.Count);
    }

    [Theory]
    [InlineData(400, "INVALID_ARGUMENT", "API key not valid. Please pass a valid API key.", """{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID"}""", CheckStatus.InvalidKey)]
    [InlineData(403, "PERMISSION_DENIED", "Your API key was reported as leaked.", "", CheckStatus.InvalidKey)]
    [InlineData(400, "FAILED_PRECONDITION", "User location is not supported for the API use without a billing account linked.", "", CheckStatus.NoPermission)]
    [InlineData(402, "RESOURCE_EXHAUSTED", "Prepay credits depleted.", "", CheckStatus.NoCredit)]
    [InlineData(429, "RESOURCE_EXHAUSTED", "Quota exceeded.", "", CheckStatus.RateLimited)]
    [InlineData(500, "INTERNAL", "Internal error.", "", CheckStatus.Failed)]
    public async Task A_failed_list_says_why(int code, string status, string message, string details, CheckStatus expected)
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(code, Error(code, status, message, details)));

        var check = await new GeminiProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(expected, check.Status);
        Assert.StartsWith($"Gemini: HTTP {code} ", check.Detail);
        Assert.EndsWith(message, check.Detail);
    }

    [Fact]
    public async Task A_line_goes_out_with_the_system_instruction_json_output_little_thinking_and_no_storage()
    {
        HttpRequestMessage? sent = null;
        var handler = new FakeHandler((request, _) =>
        {
            sent = request;
            return FakeHandler.Json(Answer(Items));
        });
        var translator = new GeminiProvider(new HttpClient(handler)).Create(ProviderKit.Config("gemini-3.8-flash"));

        var result = await translator.TranslateAsync(ProviderKit.Request("Cirl, get down here!"), CancellationToken.None);

        Assert.Equal(["Ciri, xuống đây!"], result.Texts); // the thought part is skipped
        Assert.Equal(["Ciri, get down here!"], result.FixedSources);
        Assert.Equal("gemini/gemini-3.8-flash", translator.Name);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent", sent!.RequestUri!.ToString());
        Assert.Equal(ProviderKit.TestKey, ProviderKit.Header(sent, "x-goog-api-key"));
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var root = body.RootElement;
        Assert.Contains("game_profile", root.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("user", root.GetProperty("contents")[0].GetProperty("role").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        var generation = root.GetProperty("generationConfig");
        Assert.Equal("low", generation.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        Assert.Equal("APPLICATION_JSON", generation.GetProperty("responseFormat").GetProperty("text").GetProperty("mimeType").GetString());
        Assert.Equal("object", generation.GetProperty("responseFormat").GetProperty("text").GetProperty("schema").GetProperty("type").GetString());
        Assert.False(generation.TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData("gemini-3.8-flash", "low", null)]
    [InlineData("gemini-3.6-flash", "minimal", null)]
    [InlineData("gemini-3.5-flash-lite", "minimal", null)]
    [InlineData("gemini-2.5-flash", null, 0)]
    [InlineData("gemini-4-pro", "low", null)]
    public void Thinking_is_kept_as_low_as_the_family_allows(string model, string? level, int? budget)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(GeminiTranslator.Thinking(model)));

        Assert.Equal(level, doc.RootElement.TryGetProperty("thinkingLevel", out var l) ? l.GetString() : null);
        Assert.Equal(budget, doc.RootElement.TryGetProperty("thinkingBudget", out var b) ? b.GetInt32() : null);
    }

    [Fact]
    public async Task An_api_without_responseFormat_gets_the_older_json_fields_and_keeps_them()
    {
        var handler = new FakeHandler((_, body) => body.Contains("\"responseFormat\"")
            ? ProviderKit.Answer(400, Error(400, "INVALID_ARGUMENT", "Invalid JSON payload received. Unknown name \\\"responseFormat\\\" at 'generation_config': Cannot find field."))
            : FakeHandler.Json(Answer(Items)));
        var provider = new GeminiProvider(new HttpClient(handler));

        await provider.Create(ProviderKit.Config("gemini-3.8-flash")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);
        await provider.Create(ProviderKit.Config("gemini-3.8-flash")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);

        Assert.Equal(3, handler.Bodies.Count);
        using var body = JsonDocument.Parse(handler.Bodies[2]);
        var generation = body.RootElement.GetProperty("generationConfig");
        Assert.Equal("application/json", generation.GetProperty("responseMimeType").GetString());
        Assert.Equal("object", generation.GetProperty("responseJsonSchema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_model_that_rejects_the_thinking_level_is_sent_none()
    {
        var handler = new FakeHandler((_, body) => body.Contains("thinkingConfig")
            ? ProviderKit.Answer(400, Error(400, "INVALID_ARGUMENT", "Thinking level minimal is not supported for this model."))
            : FakeHandler.Json(Answer(Items)));

        var result = await new GeminiProvider(new HttpClient(handler)).Create(ProviderKit.Config("gemini-3.6-flash")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);

        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Theory]
    [InlineData("SAFETY", "declined")]
    [InlineData("PROHIBITED_CONTENT", "declined")]
    [InlineData("MAX_TOKENS", "cut off")]
    [InlineData("MALFORMED_RESPONSE", "MALFORMED_RESPONSE")]
    [InlineData("LANGUAGE", "LANGUAGE")]
    [InlineData("OTHER", "OTHER")]
    public async Task Any_finish_but_stop_hands_the_line_on(string finish, string message)
    {
        var translator = new GeminiProvider(new HttpClient(new FakeHandler((_, _) => FakeHandler.Json(Answer(Items, finish))))).Create(ProviderKit.Config("gemini-3.8-flash"));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Contains(message, ex.Message);
        Assert.True(ex.AboutText); // this answer's content (an OCR-garbage line), not an outage: Gemini is not passed over
    }

    [Fact]
    public async Task An_answer_without_a_candidate_is_a_failure_of_gemini()
    {
        var translator = new GeminiProvider(new HttpClient(new FakeHandler((_, _) => FakeHandler.Json("""{"candidates":[]}""")))).Create(ProviderKit.Config("gemini-3.8-flash"));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Contains("no candidate", ex.Message);
        Assert.False(ex.AboutText);
    }

    [Fact]
    public async Task A_blocked_prompt_is_declined()
    {
        var translator = new GeminiProvider(new HttpClient(new FakeHandler((_, _) => FakeHandler.Json("""{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"}}"""))))
            .Create(ProviderKit.Config("gemini-3.8-flash"));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Contains("declined", ex.Message);
        Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
    }

    [Fact]
    public async Task A_rate_limit_pauses_gemini_for_the_delay_it_names()
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(429, Error(429, "RESOURCE_EXHAUSTED", "Quota exceeded for metric requests per minute.",
            """{"@type":"type.googleapis.com/google.rpc.QuotaFailure"},{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"37s"}""")));
        var translator = new GeminiTranslator(ProviderKit.Config("gemini-3.8-flash"), new HttpClient(handler), new ConcurrentDictionary<string, GeminiFeatures>());

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(37), ex.RetryAfter);
        Assert.Equal("Gemini: HTTP 429 RESOURCE_EXHAUSTED: Quota exceeded for metric requests per minute.", ex.Message);
    }

    [Theory]
    [InlineData("1e300s")]
    [InlineData("NaNs")]
    [InlineData("-5s")]
    public async Task A_delay_no_wait_can_be_is_no_delay_and_the_check_still_answers(string delay)
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(429, Error(429, "RESOURCE_EXHAUSTED", "Quota exceeded.",
            $$"""{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"{{delay}}"}""")));

        var check = await new GeminiProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(CheckStatus.RateLimited, check.Status);
    }

    [Fact]
    public void The_project_id_is_an_optional_field()
    {
        var provider = new GeminiProvider();

        Assert.Equal([new ProviderFieldSpec(ProviderField.ApiKey, true), new ProviderFieldSpec(ProviderField.ProjectId, false)], provider.Fields);
        Assert.Equal(ProviderKind.LLM, provider.Kind);
        Assert.Equal("", provider.DefaultModel);
    }
}
