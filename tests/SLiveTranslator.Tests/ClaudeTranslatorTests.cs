using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>What Claude is sent and how its answer is read; no request leaves the PC.</summary>
public class ClaudeTranslatorTests
{
    [Fact]
    public void The_profile_follows_the_instructions_and_the_cache_mark_is_on_the_last_block()
    {
        var request = new TranslateRequest(["Ciri!"], "en", "vi", [], TestProfiles.Witcher3());

        var blocks = ClaudeTranslator.SystemBlocks(request);

        Assert.Equal(2, blocks.Count);
        Assert.StartsWith("You translate live video-game subtitles from 'en' into natural spoken 'vi'", blocks[0].Text);
        Assert.Equal(TestProfiles.Witcher3().ToPrompt(), blocks[1].Text);
        Assert.Null(blocks[0].CacheControl);
        Assert.NotNull(blocks[1].CacheControl);
        Assert.NotNull(Assert.Single(ClaudeTranslator.SystemBlocks(TranslateRequest.Plain(["Ciri!"], "en", "vi"))).CacheControl);
    }

    [Fact]
    public void The_conversation_and_the_new_lines_go_in_the_user_message()
    {
        var request = new TranslateRequest(["But you were asleep, Uncle Vesemir.", "Space Skip"], "en", "vi",
            [new ContextLine("Ciri, get down here!", "Ciri, xuống đây!")], null);

        string message = ClaudeTranslator.UserMessage(request);

        Assert.Equal(
            """{"context":[{"source":"Ciri, get down here!","text":"Ciri, xuống đây!"}],"lines":[{"id":0,"text":"But you were asleep, Uncle Vesemir."},{"id":1,"text":"Space Skip"}]}""",
            message);
    }

    [Fact]
    public void The_answer_is_read_by_id_with_the_fixed_source()
    {
        string answer = """
            ```json
            {"items":[{"id":1,"source":"Space Skip","text":"[Space] Bỏ qua"},{"id":0,"source":"But you were asleep, Uncle Vesemir.","text":"Nhưng chú đã ngủ rồi, chú Vesemir."}]}
            ```
            """;

        var (texts, sources) = ClaudeTranslator.ParseItems(answer, ["Butyou were asleep, Uncle Vesemir.", "Space Skip"]);

        Assert.Equal(["Nhưng chú đã ngủ rồi, chú Vesemir.", "[Space] Bỏ qua"], texts);
        Assert.Equal(["But you were asleep, Uncle Vesemir.", "Space Skip"], sources);
    }

    [Fact]
    public void A_line_the_answer_leaves_out_or_leaves_empty_keeps_its_original()
    {
        var (texts, _) = ClaudeTranslator.ParseItems("""{"items":[{"id":0,"source":"x","text":""},{"id":7,"text":"stray"}]}""", ["HUD 7:09", "Geralt."]);

        Assert.Equal(["HUD 7:09", "Geralt."], texts);
    }

    [Fact]
    public void An_id_written_as_a_string_is_read_and_one_that_is_no_number_is_skipped()
    {
        var (texts, _) = ClaudeTranslator.ParseItems("""{"items":[{"id":"1","text":"Hai"},{"id":"zero","text":"stray"},{"id":0.5,"text":"stray"}]}""", ["One", "Two"]);

        Assert.Equal(["One", "Hai"], texts);
    }

    [Fact]
    public void An_answer_that_is_no_item_list_lets_the_next_service_translate()
    {
        var ex = Assert.Throws<TranslatorUnavailableException>(() => ClaudeTranslator.ParseItems("I can't help with that.", ["x"]));

        Assert.Contains("unexpected response", ex.Message);
    }

    [Fact]
    public void The_cache_key_names_the_model_and_an_empty_key_is_refused()
    {
        Assert.Equal("claude/claude-opus-5-5", new ClaudeTranslator("sk-ant-test", "").Name);
        Assert.Equal("claude/claude-haiku-5-5", new ClaudeTranslator("sk-ant-test", " claude-haiku-5-5 ").Name);
        // An empty key would make the SDK fall back to ANTHROPIC_API_KEY, which may bill someone else.
        Assert.Throws<ArgumentException>(() => new ClaudeTranslator("  ", "claude-opus-5-5"));
    }

    [Fact]
    public void The_instructions_ask_for_the_items_format()
    {
        string instructions = ClaudeTranslator.Instructions("auto", "vi");

        Assert.Contains("detect it", instructions);
        Assert.Contains("{\"items\":[{\"id\":0,\"source\":\"...\",\"text\":\"...\"}]}", instructions);
        Assert.DoesNotContain("<game_profile>", instructions); // the profile is its own block
        Assert.Equal(instructions, ClaudeTranslator.Instructions("auto", "vi")); // the cached prefix stays the same
    }

    // ---- through the SDK, against a fake API ----

    const string TestKey = "sk-ant-test-not-a-real-key";

    static ClaudeTranslator Claude(HttpMessageHandler handler, Func<int, TimeSpan>? timeout = null) =>
        new(TestKey, "claude-opus-5-5", LlmEffort.Low, new HttpClient(handler), timeout);

    static HttpResponseMessage Message(string text) => FakeHandler.Json(JsonSerializer.Serialize(new
    {
        id = "msg_test",
        type = "message",
        role = "assistant",
        model = "claude-opus-5-5",
        content = new[] { new { type = "text", text } },
        stop_reason = "end_turn",
        stop_sequence = (string?)null,
        usage = new { input_tokens = 12, output_tokens = 8 },
    }));

    static HttpResponseMessage ApiError(int status, string type, string message) => new((HttpStatusCode)status)
    {
        // Every answer has its own request id, as the real API's do.
        Content = new StringContent($$"""{"type":"error","error":{"type":"{{type}}","message":"{{message}}"},"request_id":"req_{{Guid.NewGuid():N}}"}""",
            Encoding.UTF8, "application/json"),
    };

    static Task<TranslateResult> Translate(ClaudeTranslator claude, CancellationToken ct = default) =>
        claude.TranslateAsync(new TranslateRequest(["Cirl, get down here!"], "en", "vi", [], TestProfiles.Witcher3()), ct);

    [Fact]
    public async Task A_line_goes_out_with_the_profile_and_comes_back_by_id()
    {
        HttpRequestMessage? sent = null;
        var handler = new FakeHandler((request, _) =>
        {
            sent = request;
            return Message("""{"items":[{"id":"0","source":"Ciri, get down here!","text":"Ciri, xuống đây!"}]}""");
        });

        var result = await Translate(Claude(handler));

        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.Equal(["Ciri, get down here!"], result.FixedSources);
        Assert.Equal("/v1/messages", sent!.RequestUri!.AbsolutePath);
        Assert.Contains(TestKey, sent.Headers.ToString());
        Assert.Equal("server-side-fallback-2026-07-01", sent.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal("0", sent.Headers.GetValues("x-stainless-retry-count").Single());
        string body = handler.Bodies.Single();
        Assert.Contains("\"model\":\"claude-opus-5-5\"", body);
        Assert.Contains("game_profile", body);
    }

    [Theory]
    [InlineData(400, "invalid_request_error", "Your credit balance is too low to access the Anthropic API.", false)]
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
        Assert.Equal(account ? ClaudeTranslator.AccountProblemPause : null, first.RetryAfter); // a key or credit problem stays
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
    public async Task A_stalled_request_is_given_up_after_its_deadline()
    {
        var claude = Claude(new StalledHandler(), _ => TimeSpan.FromMilliseconds(100));

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

    [Theory]
    [InlineData(1, 10)] // a subtitle line
    [InlineData(2, 14)] // a line with a key hint
    [InlineData(16, 70)] // a translate-screen chunk
    [InlineData(100, 120)]
    public void The_deadline_grows_with_the_lines_of_the_request(int lines, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ClaudeTranslator.RequestTimeout(lines));

    /// <summary>A connection that never answers.</summary>
    sealed class StalledHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
