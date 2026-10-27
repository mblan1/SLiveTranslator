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

public class OpenAIProviderTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    static string Models(params (string Id, long Created, string? Shutdown)[] models) => JsonSerializer.Serialize(new
    {
        @object = "list",
        data = models.Select(m => new { id = m.Id, @object = "model", created = m.Created, owned_by = "openai", shutdown_date = m.Shutdown }),
    });

    static readonly (string, long, string?)[] Catalog =
    [
        ("gpt-6-astra", 1_790_000_000, null),
        ("gpt-6.1-sol", 1_790_000_100, null),
        ("gpt-6.1-terra", 1_790_000_200, null),
        ("gpt-6-luna", 1_780_000_000, null),
        ("gpt-5.5-pro", 1_770_000_000, null),
        ("gpt-5-mini", 1_700_000_000, "2027-03-01"), // announced: still usable, marked deprecated
        ("gpt-4o", 1_600_000_000, "2026-06-01"), // already shut down
        ("text-embedding-3-large", 1_700_000_000, null),
        ("gpt-image-2", 1_780_000_000, null),
        ("gpt-realtime-whisper", 1_780_000_000, null),
        ("gpt-audio-1.5", 1_780_000_000, null),
        ("gpt-4o-mini-tts", 1_700_000_000, null),
        ("gpt-5-search-api", 1_700_000_000, null),
        ("gpt-5.5-chat-latest", 1_780_000_000, null),
        ("gpt-daybreak-red-latest", 1_780_000_000, null),
        ("gpt-6-deep-research", 1_780_000_000, null),
        ("gpt-realtime-translate", 1_780_000_000, null),
        ("omni-moderation-latest", 1_700_000_000, null),
        ("o3", 1_700_000_000, null),
    ];

    static OpenAIProvider Provider(FakeHandler handler) => new(new HttpClient(handler), new TestClock(Now));

    static string Response(string text, string status = "completed", string extra = "") => $$"""
        {"id":"resp_1","object":"response","status":"{{status}}"{{extra}},"output":[
          {"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"x"},
          {"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":{{JsonSerializer.Serialize(text)}},"annotations":[]}]}]}
        """;

    const string Answer = """{"items":[{"id":0,"source":"Ciri, get down here!","text":"Ciri, xuống đây!"}]}""";

    [Fact]
    public void Only_current_text_models_are_offered_and_terra_is_recommended_and_luna_fastest()
    {
        var options = OpenAIProvider.Options(Catalog.Select(m => new OpenAIModel(m.Item1, DateTimeOffset.FromUnixTimeSeconds(m.Item2),
            m.Item3 is null ? null : DateTimeOffset.Parse(m.Item3))).ToList(), Now);

        Assert.Equal(["gpt-6.1-terra", "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna", "gpt-5-mini"], options.Select(o => o.Id));
        Assert.Equal(ModelTags.Recommended, options[0].Tags);
        Assert.Equal(ModelTags.Fastest, options[3].Tags);
        Assert.Equal(ModelTags.Deprecated, options[4].Tags);
        Assert.Equal("gpt-5-mini (deprecated)", options[4].Label);
        Assert.Equal("$1 / $5 per 1M tokens (cached $0.05)", options[0].CostHint);
    }

    [Fact]
    public void Without_the_known_models_the_newest_terra_is_recommended_and_the_newest_mini_fastest()
    {
        var options = OpenAIProvider.Options(
        [
            new OpenAIModel("gpt-6.2-terra", Now, null),
            new OpenAIModel("gpt-7-terra", Now, null),
            new OpenAIModel("gpt-6.5-mini", Now, null),
            new OpenAIModel("gpt-5-mini", Now, null),
        ], Now);

        Assert.Equal("gpt-7-terra", options.Single(o => o.Tags.HasFlag(ModelTags.Recommended)).Id);
        Assert.Equal("gpt-6.5-mini", options.Single(o => o.Tags.HasFlag(ModelTags.Fastest)).Id);
    }

    [Fact]
    public async Task The_check_lists_the_models_and_a_test_answer_on_the_fastest_proves_the_credit()
    {
        var sent = new List<HttpRequestMessage>();
        var handler = new FakeHandler((request, _) =>
        {
            sent.Add(request);
            return request.Method == HttpMethod.Get ? FakeHandler.Json(Models(Catalog)) : FakeHandler.Json(Response("Hello"));
        });

        var check = await Provider(handler).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal(5, check.Options.Count);
        Assert.Equal("https://api.openai.com/v1/models", sent[0].RequestUri!.ToString());
        Assert.Equal("Bearer " + ProviderKit.TestKey, ProviderKit.Header(sent[0], "Authorization"));
        Assert.Equal("https://api.openai.com/v1/responses", sent[1].RequestUri!.ToString());
        Assert.Contains("\"model\":\"gpt-6-luna\"", handler.Bodies[1]);
        Assert.Contains("\"store\":false", handler.Bodies[1]);
    }

    [Theory]
    [InlineData(401, "invalid_api_key", "invalid_request_error", CheckStatus.InvalidKey)]
    [InlineData(403, "unsupported_country_region_territory", "request_forbidden", CheckStatus.NoPermission)]
    [InlineData(429, "insufficient_quota", "insufficient_quota", CheckStatus.NoCredit)]
    [InlineData(429, "credit_balance_exhausted", "insufficient_quota", CheckStatus.NoCredit)]
    [InlineData(429, "project_spend_limit_exceeded", "insufficient_quota", CheckStatus.NoCredit)]
    [InlineData(429, "rate_limit_exceeded", "requests", CheckStatus.RateLimited)]
    [InlineData(503, "server_is_overloaded", "service_unavailable_error", CheckStatus.Failed)]
    public async Task A_failed_list_says_why(int status, string code, string type, CheckStatus expected)
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(status, $$$"""{"error":{"message":"The API's own words.","type":"{{{type}}}","param":null,"code":"{{{code}}}"}}"""));

        var check = await Provider(handler).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(expected, check.Status);
        Assert.Equal($"OpenAI: HTTP {status} {code}: The API's own words.", check.Detail);
    }

    [Fact]
    public async Task A_listed_key_without_credit_is_told_by_its_test_answer()
    {
        var handler = new FakeHandler((request, _) => request.Method == HttpMethod.Get
            ? FakeHandler.Json(Models(Catalog))
            : ProviderKit.Answer(429, """{"error":{"message":"Your credit balance is exhausted.","type":"insufficient_quota","code":"credit_balance_exhausted"}}"""));

        var check = await Provider(handler).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(CheckStatus.NoCredit, check.Status);
        Assert.Equal(5, check.Options.Count);
    }

    [Fact]
    public async Task A_listed_key_that_may_not_create_answers_has_no_permission()
    {
        // A restricted key with Models: Read lists the models and is refused every answer.
        var handler = new FakeHandler((request, _) => request.Method == HttpMethod.Get
            ? FakeHandler.Json(Models(Catalog))
            : ProviderKit.Answer(401, """{"error":{"message":"You have insufficient permissions for this operation. Missing scopes: api.responses.write.","type":"invalid_request_error","param":null,"code":null}}"""));

        var check = await Provider(handler).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(CheckStatus.NoPermission, check.Status);
        Assert.Equal(5, check.Options.Count);
        Assert.StartsWith("OpenAI: HTTP 401 invalid_request_error: You have insufficient permissions", check.Detail);
    }

    [Fact]
    public async Task A_line_goes_out_as_a_strict_schema_response_without_reasoning_or_storage()
    {
        HttpRequestMessage? sent = null;
        var handler = new FakeHandler((request, _) =>
        {
            sent = request;
            return FakeHandler.Json(Response(Answer));
        });
        var translator = Provider(handler).Create(ProviderKit.Config("gpt-6.1-terra"));

        var result = await translator.TranslateAsync(ProviderKit.Request("Cirl, get down here!"), CancellationToken.None);

        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.Equal(["Ciri, get down here!"], result.FixedSources);
        Assert.Equal("openai/gpt-6.1-terra", translator.Name);
        Assert.Equal("https://api.openai.com/v1/responses", sent!.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var root = body.RootElement;
        Assert.Equal("gpt-6.1-terra", root.GetProperty("model").GetString());
        Assert.Contains("game_profile", root.GetProperty("instructions").GetString());
        Assert.StartsWith("{\"context\":[{\"source\":\"Hm.\"", root.GetProperty("input").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("none", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("low", root.GetProperty("text").GetProperty("verbosity").GetString());
        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
        Assert.True(root.GetProperty("max_output_tokens").GetInt32() > 0);
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task A_model_that_rejects_effort_none_gets_low_from_then_on()
    {
        var handler = new FakeHandler((_, body) => body.Contains("\"effort\":\"none\"")
            ? ProviderKit.Answer(400, """{"error":{"message":"Unsupported value: 'none' is not supported with this model.","type":"invalid_request_error","param":"reasoning.effort","code":"unsupported_value"}}""")
            : FakeHandler.Json(Response(Answer)));
        var provider = Provider(handler);

        await provider.Create(ProviderKit.Config("gpt-6.1-sol")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);
        await provider.Create(ProviderKit.Config("gpt-6.1-sol")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains("\"effort\":\"low\"", handler.Bodies[2]);
    }

    [Fact]
    public async Task A_model_before_gpt_5_gets_neither_reasoning_nor_verbosity_from_then_on()
    {
        // gpt-4.1 is deprecated without a shutdown date, so the list still offers it.
        var handler = new FakeHandler((_, body) =>
            body.Contains("\"effort\":\"none\"") ? ProviderKit.Answer(400, """{"error":{"message":"Unsupported parameter: 'reasoning.effort' is not supported with this model.","type":"invalid_request_error","param":"reasoning.effort","code":"unsupported_parameter"}}""")
            : body.Contains("\"verbosity\"") ? ProviderKit.Answer(400, """{"error":{"message":"Unsupported value: 'low' is not supported with the 'gpt-4.1' model. Supported values are: 'medium'.","type":"invalid_request_error","param":"text.verbosity","code":"unsupported_value"}}""")
            : FakeHandler.Json(Response(Answer)));
        var provider = Provider(handler);

        var result = await provider.Create(ProviderKit.Config("gpt-4.1")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);
        await provider.Create(ProviderKit.Config("gpt-4.1")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);

        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.Equal(3 + 1, handler.Bodies.Count); // none, low (its verbosity rejected), neither; then neither at once
        using var last = JsonDocument.Parse(handler.Bodies[3]);
        Assert.False(last.RootElement.TryGetProperty("reasoning", out _));
        Assert.False(last.RootElement.GetProperty("text").TryGetProperty("verbosity", out _));
        Assert.Equal("json_schema", last.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_400_that_names_neither_field_is_not_sent_again()
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(400, """{"error":{"message":"Invalid schema for response_format 'translation'.","type":"invalid_request_error","param":"text.format.schema","code":"invalid_json_schema"}}"""));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() =>
            Provider(handler).Create(ProviderKit.Config("gpt-6.1-terra")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Single(handler.Bodies);
        Assert.StartsWith("OpenAI: HTTP 400 invalid_json_schema", ex.Message);
    }

    [Theory]
    [InlineData("""{"id":"r","status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"I can't help with that."}]}]}""", "declined")]
    [InlineData("""{"id":"r","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""", "cut off")]
    [InlineData("""{"id":"r","status":"incomplete","incomplete_details":{"reason":"content_filter"},"output":[]}""", "declined")]
    [InlineData("""<html>proxy</html>""", "unexpected response")]
    public async Task A_refusal_an_incomplete_answer_or_a_strange_one_hands_the_line_on(string answer, string message)
    {
        var translator = Provider(new FakeHandler((_, _) => FakeHandler.Json(answer))).Create(ProviderKit.Config("gpt-6.1-terra"));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Contains(message, ex.Message);
    }

    [Theory]
    [InlineData(429, "insufficient_quota", null, 600)] // no credit: it does not pass by waiting
    [InlineData(429, "rate_limit_exceeded", "20", 20)] // the server says how long
    [InlineData(401, "invalid_api_key", null, 600)]
    public async Task A_failure_while_translating_pauses_openai_as_long_as_it_lasts(int status, string code, string? retryAfter, int seconds)
    {
        var translator = Provider(new FakeHandler((_, _) => ProviderKit.Answer(status, $$$"""{"error":{"message":"m","type":"t","code":"{{{code}}}"}}""", retryAfter)))
            .Create(ProviderKit.Config("gpt-6.1-terra"));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(seconds), ex.RetryAfter);
        Assert.Equal($"OpenAI: HTTP {status} {code}: m", ex.Message);
    }

    [Fact]
    public async Task A_stalled_request_is_given_up_after_its_deadline()
    {
        var translator = new OpenAITranslator(ProviderKit.Config("gpt-6.1-terra"), new HttpClient(new StalledHandler()), new ConcurrentDictionary<string, OpenAIReasoning>(),
            _ => TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Equal("OpenAI did not answer within 0 seconds.", ex.Message);
    }

    [Fact]
    public async Task An_empty_key_is_invalid_without_a_request()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("no request expected"));

        Assert.Equal(CheckStatus.InvalidKey, (await Provider(handler).CheckAsync(ProviderKit.Key(""), CancellationToken.None)).Status);
        Assert.Throws<ArgumentException>(() => Provider(handler).Create(ProviderKit.Config("")));
    }
}
