using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>What Claude is sent and how its answer is read, through the SDK against a fake API; no request leaves the PC.</summary>
/// <remarks>
/// The prompt and parser tests of M3 moved to <see cref="LlmPromptTests"/>, which every LLM shares; the M3 cases on the
/// SDK path stay here with the same expectations.
/// </remarks>
public class ClaudeTranslatorTests
{
    const string TestKey = "sk-ant-test-not-a-real-key";

    static ClaudeTranslator Claude(HttpMessageHandler handler, string model = "claude-opus-5-5", Func<int, TimeSpan>? timeout = null,
        ClaudeModelMemory? memory = null, LlmEffort effort = LlmEffort.Low) =>
        new(new ProviderConfig(new ProviderCredentials(TestKey), model, effort), memory ?? new ClaudeModelMemory(), new HttpClient(handler), timeout);

    internal static HttpResponseMessage Message(string text, string stopReason = "end_turn") => FakeHandler.Json(JsonSerializer.Serialize(new
    {
        id = "msg_test",
        type = "message",
        role = "assistant",
        model = "claude-opus-5-5",
        content = new[] { new { type = "text", text } },
        stop_reason = stopReason,
        stop_sequence = (string?)null,
        usage = new { input_tokens = 12, output_tokens = 8 },
    }));

    internal static HttpResponseMessage ApiError(int status, string type, string message) => new((HttpStatusCode)status)
    {
        // Every answer has its own request id, as the real API's do.
        Content = new StringContent($$"""{"type":"error","error":{"type":"{{type}}","message":"{{message}}"},"request_id":"req_{{Guid.NewGuid():N}}"}""",
            Encoding.UTF8, "application/json"),
    };

    const string Answer = """{"items":[{"id":"0","source":"Ciri, get down here!","text":"Ciri, xuống đây!"}]}""";

    static Task<TranslateResult> Translate(ClaudeTranslator claude, CancellationToken ct = default) =>
        claude.TranslateAsync(new TranslateRequest(["Cirl, get down here!"], "en", "vi", [], TestProfiles.Witcher3()), ct);

    [Fact]
    public async Task A_line_goes_out_with_the_profile_and_comes_back_by_id()
    {
        HttpRequestMessage? sent = null;
        var handler = new FakeHandler((request, _) =>
        {
            sent = request;
            return Message(Answer);
        });

        var result = await Translate(Claude(handler));

        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.Equal(["Ciri, get down here!"], result.FixedSources);
        Assert.Equal("/v1/messages", sent!.RequestUri!.AbsolutePath);
        Assert.Contains(TestKey, sent.Headers.ToString());
        Assert.Equal("server-side-fallback-2026-07-01", sent.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal("0", sent.Headers.GetValues("x-stainless-retry-count").Single());
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var root = body.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        var system = Assert.Single(root.GetProperty("system").EnumerateArray());
        Assert.Contains("game_profile", system.GetProperty("text").GetString());
        Assert.Equal("ephemeral", system.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("object", root.GetProperty("output_config").GetProperty("format").GetProperty("schema").GetProperty("type").GetString());
        // Opus 5.5 cannot turn thinking off, and no model takes sampling parameters any more.
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("top_p", out _));
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("claude-opus-5-5", true, null)]
    [InlineData("claude-fable-5-1", true, null)]
    [InlineData("claude-opus-5", true, null)]
    [InlineData("claude-sonnet-5-5", true, "between_tools")]
    [InlineData("claude-haiku-5-5", false, "disabled")] // no server-side fallback: the API docs say never send it
    [InlineData("claude-opus-4-8", false, null)] // an older or unknown model is not sent it either
    public async Task Each_model_gets_the_fallback_and_the_thinking_it_takes(string model, bool fallbacks, string? thinking)
    {
        HttpRequestMessage? sent = null;
        var handler = new FakeHandler((request, _) =>
        {
            sent = request;
            return Message(Answer);
        });

        await Translate(Claude(handler, model, effort: LlmEffort.Medium));

        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal(fallbacks, body.RootElement.TryGetProperty("fallbacks", out _));
        Assert.Equal(fallbacks, sent!.Headers.Contains("anthropic-beta"));
        Assert.Equal(thinking, body.RootElement.TryGetProperty("thinking", out var t) ? t.GetProperty("type").GetString() : null);
        Assert.Equal("medium", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task A_400_about_fallbacks_is_sent_again_without_them_and_the_model_is_not_sent_them_again()
    {
        var memory = new ClaudeModelMemory();
        var handler = new FakeHandler((_, body) => body.Contains("\"fallbacks\"")
            ? ApiError(400, "invalid_request_error", "fallbacks: this model does not support server-side fallback.")
            : Message(Answer));

        var first = await Translate(Claude(handler, memory: memory));
        var second = await Translate(Claude(handler, memory: memory)); // a settings save makes a new translator

        Assert.Equal(["Ciri, xuống đây!"], first.Texts);
        Assert.Equal(["Ciri, xuống đây!"], second.Texts);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.DoesNotContain("\"fallbacks\"", handler.Bodies[2]);
        Assert.Contains("\"format\"", handler.Bodies[2]);
        Assert.Equal(ClaudeFeatures.Fallbacks, memory.Rejected("claude-opus-5-5"));
    }

    [Fact]
    public async Task Structured_output_that_cannot_go_with_fallbacks_gives_way_to_the_tolerant_reading()
    {
        var memory = new ClaudeModelMemory();
        var handler = new FakeHandler((_, body) => body.Contains("\"format\"")
            ? ApiError(400, "invalid_request_error", "output_config.format: structured outputs cannot be combined with fallbacks.")
            : Message("```json\n" + Answer + "\n```"));

        var result = await Translate(Claude(handler, memory: memory));

        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"fallbacks\"", handler.Bodies[1]); // the fallback stays; only the format goes
        Assert.Equal(ClaudeFeatures.Format, memory.Rejected("claude-opus-5-5"));
    }

    [Fact]
    public async Task A_400_that_names_nothing_sent_is_not_sent_again()
    {
        var handler = new FakeHandler((_, _) => ApiError(400, "invalid_request_error", "messages: at least one message is required"));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => Translate(Claude(handler)));

        Assert.Single(handler.Bodies);
        Assert.Equal("Claude: HTTP 400 invalid_request_error: messages: at least one message is required", ex.Message);
    }

    [Fact]
    public async Task A_refusal_hands_this_text_on_without_pausing_claude()
    {
        var claude = Claude(new FakeHandler((_, _) => Message("", "refusal")));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => Translate(claude));

        Assert.Contains("declined", ex.Message);
        Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
        Assert.True(ex.AboutText); // no outage: the chain reports no switch
    }

    [Fact]
    public async Task An_answer_cut_off_at_its_limit_hands_the_text_on()
    {
        var claude = Claude(new FakeHandler((_, _) => Message("""{"items":[{"id":0,"text":"Ci""", "max_tokens")));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => Translate(claude));

        Assert.Contains("cut off", ex.Message);
        Assert.True(ex.AboutText);
    }

    [Fact]
    public async Task A_line_that_is_not_language_comes_back_empty_and_one_left_out_comes_back_failed()
    {
        var claude = Claude(new FakeHandler((_, _) => Message("""{"items":[{"id":0,"source":"HUD 7:09","text":""}]}""")));

        var result = await claude.TranslateAsync(new TranslateRequest(["HUD 7:09", "Geralt, wait."], "en", "vi", [], null), CancellationToken.None);

        Assert.Equal(["", "Geralt, wait."], result.Texts);
        Assert.Equal([1], result.FailedLines);
    }

    [Theory]
    [InlineData(400, "invalid_request_error", "Your credit balance is too low to access the Anthropic API.", true)] // how the API reports an empty balance
    [InlineData(400, "invalid_request_error", "max_tokens: 99999999 is greater than the maximum", false)]
    [InlineData(401, "authentication_error", "invalid x-api-key", true)]
    [InlineData(403, "permission_error", "Your API key does not have permission to use the specified resource.", true)]
    [InlineData(400, "billing_error", "Your account has no credit.", true)]
    [InlineData(429, "rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit.", false)]
    [InlineData(529, "overloaded_error", "Overloaded", false)]
    public async Task An_api_error_hands_the_line_on_with_a_short_message_that_is_the_same_for_every_line(int status, string type, string message, bool account)
    {
        var claude = Claude(new FakeHandler((_, _) => ApiError(status, type, message)));

        var first = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => Translate(claude));
        var second = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => Translate(claude));

        Assert.Equal($"Claude: HTTP {status} {type}: {message}", first.Message); // no request id, no raw body
        Assert.Equal(first.Message, second.Message); // so a pause is logged once, not once a line
        Assert.Equal(account ? ProviderException.AccountProblemPause : null, first.RetryAfter); // a key or credit problem stays
    }

    [Theory]
    [InlineData(429, "rate_limit_error")]
    [InlineData(529, "overloaded_error")]
    public async Task A_rate_limit_passes_claude_over_for_as_long_as_the_api_says(int status, string type)
    {
        var claude = Claude(new FakeHandler((_, body) => body.Contains("Cirl, get down")
            ? ProviderKit.Answer(status, $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"slow down"}}""", retryAfter: "120")
            : ProviderKit.Answer(status, $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"slow down"}}""")));

        var limited = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => Translate(claude));
        var unsaid = await Assert.ThrowsAsync<TranslatorUnavailableException>(() =>
            claude.TranslateAsync(new TranslateRequest(["Geralt, wait."], "en", "vi", [], null), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(120), limited.RetryAfter);
        Assert.Null(unsaid.RetryAfter); // the next call does not inherit it: the chain's default pause
    }

    [Theory]
    [InlineData("""{"id":"msg_test","type":"message","role":"assistant","content":"not a list","stop_reason":"end_turn"}""")]
    [InlineData("""<html><body>Bad gateway</body></html>""")]
    [InlineData("""{"id":"msg_test","type":"message","role":"assistant","content":[{"type":"text","text":"Sorry, no JSON."}],"stop_reason":"end_turn"}""")]
    public async Task An_answer_of_the_wrong_shape_hands_the_line_on(string answer)
    {
        var claude = Claude(new FakeHandler((_, _) => FakeHandler.Json(answer)));

        await Assert.ThrowsAsync<TranslatorUnavailableException>(() => Translate(claude));
    }

    [Fact]
    public async Task An_unreachable_api_hands_the_line_on()
    {
        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => Translate(Claude(new RefusedHandler())));

        Assert.StartsWith("Claude", ex.Message);
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task A_stalled_request_is_given_up_after_its_deadline()
    {
        var claude = Claude(new StalledHandler(), timeout: _ => TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => Translate(claude));

        Assert.Contains("did not answer", ex.Message);
    }

    [Fact]
    public async Task Subtitles_turned_off_cancel_the_request_and_nothing_else_is_tried()
    {
        var claude = Claude(new StalledHandler());
        using var off = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Translate(claude, off.Token));
    }

    [Fact]
    public async Task Translators_made_by_each_settings_save_share_the_connection_pool_and_leave_it_as_it_was()
    {
        var http = new HttpClient(new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/v1/models"
            ? FakeHandler.Json("""{"data":[],"has_more":false,"first_id":null,"last_id":null}""")
            : Message(Answer)));
        var provider = new ClaudeProvider(http);
        var before = (ClaudeTranslator)provider.Create(ProviderKit.Config("claude-opus-5-5", TestKey));
        var after = (ClaudeTranslator)provider.Create(ProviderKit.Config("claude-opus-5-5", TestKey));

        Assert.Equal(["Ciri, xuống đây!"], (await Translate(after)).Texts);
        Assert.Equal(["Ciri, xuống đây!"], (await Translate(before)).Texts); // the client was not disposed with an older translator
        Assert.Empty(http.DefaultRequestHeaders); // the key goes on each request, never on the shared client
        await provider.CheckAsync(ProviderKit.Key(), CancellationToken.None);
        Assert.Equal(["Ciri, xuống đây!"], (await Translate(after)).Texts); // nor with a key check
    }

    [Fact]
    public void The_cache_key_names_the_model_and_an_empty_key_or_model_is_refused()
    {
        var memory = new ClaudeModelMemory();

        Assert.Equal("claude/claude-haiku-5-5", new ClaudeTranslator(new ProviderConfig(new ProviderCredentials("sk-ant-test"), " claude-haiku-5-5 "), memory).Name);
        // An empty key would make the SDK fall back to ANTHROPIC_API_KEY, which may bill someone else.
        Assert.Throws<ArgumentException>(() => new ClaudeTranslator(new ProviderConfig(new ProviderCredentials("  "), "claude-opus-5-5"), memory));
        // Replaces M3's default model: the user picks one after a key check, nothing is picked for them.
        Assert.Throws<ArgumentException>(() => new ClaudeTranslator(new ProviderConfig(new ProviderCredentials("sk-ant-test"), ""), memory));
    }

    [Theory]
    [InlineData("output_config.format: not supported with fallbacks", "Format")]
    [InlineData("fallbacks: this model does not support fallbacks", "Fallbacks")]
    [InlineData("anthropic-beta: server-side-fallback-2026-07-01 is not enabled", "Fallbacks")]
    [InlineData("thinking.type: disabled is not supported for this model", "Thinking")]
    [InlineData("output_config.effort: not supported", "Effort")]
    [InlineData("max_tokens: too large", null)]
    public void A_400_names_the_option_the_model_does_not_take(string message, string? expected) =>
        Assert.Equal(expected is null ? null : Enum.Parse<ClaudeFeatures>(expected), ClaudeTranslator.Rejected(message, ClaudeFeatures.Fallbacks | ClaudeFeatures.Format | ClaudeFeatures.Thinking | ClaudeFeatures.Effort));

    [Fact]
    public void An_option_not_sent_is_never_blamed()
    {
        Assert.Null(ClaudeTranslator.Rejected("fallbacks: not supported", ClaudeFeatures.Format | ClaudeFeatures.Effort));
        Assert.Equal(ClaudeFeatures.Fallbacks, ClaudeTranslator.Rejected("output_config.format cannot be combined with fallbacks", ClaudeFeatures.Fallbacks));
    }
}
