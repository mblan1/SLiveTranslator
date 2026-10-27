using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation.Providers;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The Claude key check and model list, through the SDK against a fake API.</summary>
public class ClaudeProviderTests
{
    static string Model(string id, string name, string lifecycle, string? line) => JsonSerializer.Serialize(new
    {
        type = "model", id, display_name = name, created_at = "2026-09-01T00:00:00Z", lifecycle, line,
        max_input_tokens = 1_000_000, max_tokens = 128_000,
    });

    static string Page(bool hasMore, params string[] models) =>
        $$"""{"data":[{{string.Join(",", models)}}],"has_more":{{(hasMore ? "true" : "false")}},"first_id":"a","last_id":"{{(hasMore ? "page-1-last" : "z")}}"}""";

    static readonly string[] Catalog =
    [
        Model("claude-fable-5-1", "Claude Fable 5.1", "active", "fable"),
        Model("claude-opus-5-5", "Claude Opus 5.5", "active", "opus"),
        Model("claude-sonnet-5-5", "Claude Sonnet 5.5", "active", "sonnet"),
        Model("claude-haiku-5-5", "Claude Haiku 5.5", "active", "haiku"),
        Model("claude-opus-4-1", "Claude Opus 4.1", "deprecated", "opus"),
    ];

    [Fact]
    public async Task A_valid_key_lists_its_models_newest_first_with_the_tags_and_a_test_answer_proves_the_credit()
    {
        var sent = new List<HttpRequestMessage>();
        var handler = new FakeHandler((request, _) =>
        {
            sent.Add(request);
            return request.RequestUri!.AbsolutePath == "/v1/models" ? FakeHandler.Json(Page(false, Catalog)) : ClaudeTranslatorTests.Message("Chào");
        });

        var check = await new ClaudeProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal("", check.Detail);
        Assert.Equal(["claude-fable-5-1", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-4-1"], check.Options.Select(o => o.Id));
        Assert.Equal(ModelTags.Recommended, check.Options[1].Tags);
        Assert.Equal(ModelTags.Balanced, check.Options[2].Tags);
        Assert.Equal(ModelTags.Fastest, check.Options[3].Tags);
        Assert.Equal(ModelTags.Deprecated, check.Options[4].Tags);
        Assert.Equal("Claude Opus 4.1 (deprecated)", check.Options[4].Label);
        Assert.Equal("$4 / $20 per 1M tokens (cache read $0.20)", check.Options[1].CostHint);
        Assert.Equal("?limit=100", sent[0].RequestUri!.Query);
        // The test answer goes to the cheapest model, as small as can be.
        using var probe = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("claude-haiku-5-5", probe.RootElement.GetProperty("model").GetString());
        Assert.Equal(16, probe.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(probe.RootElement.TryGetProperty("system", out _));
    }

    [Fact]
    public async Task Every_page_of_the_list_is_read()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath != "/v1/models" ? ClaudeTranslatorTests.Message("Chào")
            : request.RequestUri.Query.Contains("after_id=page-1-last") ? FakeHandler.Json(Page(false, Catalog[2..]))
            : FakeHandler.Json(Page(true, Catalog[..2])));

        var provider = new ClaudeProvider(new HttpClient(handler));
        var check = await provider.CheckAsync(ProviderKit.Key(), CancellationToken.None);
        var again = await provider.CheckAsync(ProviderKit.Key(), CancellationToken.None); // the typed key changed: a new check

        Assert.Equal(5, check.Options.Count);
        Assert.Equal(5, again.Options.Count);
    }

    [Theory]
    [InlineData(401, "authentication_error", CheckStatus.InvalidKey)]
    [InlineData(403, "permission_error", CheckStatus.NoPermission)]
    [InlineData(400, "billing_error", CheckStatus.NoCredit)]
    [InlineData(429, "rate_limit_error", CheckStatus.RateLimited)]
    [InlineData(500, "api_error", CheckStatus.Failed)]
    public async Task A_failed_list_says_why_with_the_api_message(int status, string type, CheckStatus expected)
    {
        var handler = new FakeHandler((_, _) => ClaudeTranslatorTests.ApiError(status, type, "the API's own words"));

        var check = await new ClaudeProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(expected, check.Status);
        Assert.Empty(check.Options);
        Assert.Equal($"Claude: HTTP {status} {type}: the API's own words", check.Detail);
    }

    [Theory]
    [InlineData(400, "billing_error", CheckStatus.NoCredit, "")]
    [InlineData(429, "rate_limit_error", CheckStatus.RateLimited, "")]
    [InlineData(500, "api_error", CheckStatus.Ok, "The test translation failed: ")]
    public async Task A_listed_key_whose_test_answer_fails_says_so_with_its_models(int status, string type, CheckStatus expected, string detailStart)
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/v1/models"
            ? FakeHandler.Json(Page(false, Catalog))
            : ClaudeTranslatorTests.ApiError(status, type, "no"));

        var check = await new ClaudeProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(expected, check.Status);
        Assert.Equal(5, check.Options.Count); // the UI decides what to show; the list was read
        Assert.StartsWith(detailStart + "Claude: HTTP", check.Detail);
    }

    [Fact]
    public async Task A_listed_key_whose_test_answer_says_the_credit_balance_is_too_low_has_no_credit()
    {
        // The API's answer for an empty balance: a 400 invalid_request_error, not a billing_error.
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/v1/models"
            ? FakeHandler.Json(Page(false, Catalog))
            : ClaudeTranslatorTests.ApiError(400, "invalid_request_error",
                "Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."));

        var check = await new ClaudeProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(CheckStatus.NoCredit, check.Status);
        Assert.Equal(5, check.Options.Count);
        Assert.StartsWith("Claude: HTTP 400 invalid_request_error: Your credit balance is too low", check.Detail);
    }

    [Fact]
    public async Task A_test_answer_on_a_model_without_effort_is_sent_again_without_it_and_the_model_is_not_sent_it_again()
    {
        // The key cannot use Haiku 5.5, so the fastest it lists is Haiku 4.5, which takes no effort parameter.
        string[] catalog = [Catalog[1], Model("claude-haiku-4-5", "Claude Haiku 4.5", "active", "haiku")];
        var handler = new FakeHandler((request, body) => request.RequestUri!.AbsolutePath == "/v1/models" ? FakeHandler.Json(Page(false, catalog))
            : body.Contains("\"effort\"") ? ClaudeTranslatorTests.ApiError(400, "invalid_request_error", "output_config.effort: This model does not support the effort parameter.")
            : !body.Contains("\"system\"") ? ClaudeTranslatorTests.ApiError(400, "invalid_request_error", "Your credit balance is too low to access the Anthropic API.") // the test answer
            : ClaudeTranslatorTests.Message("""{"items":[{"id":0,"source":"Hm.","text":"Hừm."}]}"""));
        var provider = new ClaudeProvider(new HttpClient(handler));

        var check = await provider.CheckAsync(ProviderKit.Key(), CancellationToken.None);
        await provider.Create(ProviderKit.Config("claude-haiku-4-5")).TranslateAsync(ProviderKit.Request("Hm."), CancellationToken.None);

        Assert.Equal(CheckStatus.NoCredit, check.Status); // what the test answer is for, not "Ok" with a warning about effort
        Assert.Equal(2, check.Options.Count);
        Assert.Equal(4, handler.Bodies.Count); // the list, the test answer twice, one translation
        Assert.Contains("\"claude-haiku-4-5\"", handler.Bodies[1]);
        Assert.DoesNotContain("\"effort\"", handler.Bodies[2]);
        Assert.DoesNotContain("\"effort\"", handler.Bodies[3]); // remembered for the translations of the run
    }

    [Fact]
    public async Task An_empty_key_is_invalid_without_a_request()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("no request expected"));

        var check = await new ClaudeProvider(new HttpClient(handler)).CheckAsync(ProviderKit.Key("  "), CancellationToken.None);

        Assert.Equal(CheckStatus.InvalidKey, check.Status);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task An_unreachable_api_is_a_network_problem_and_a_stalled_one_a_timeout()
    {
        var refused = await new ClaudeProvider(new HttpClient(new RefusedHandler())).CheckAsync(ProviderKit.Key(), CancellationToken.None);

        Assert.Equal(CheckStatus.Network, refused.Status);
        Assert.Equal(CheckStatus.Timeout, ClaudeProvider.Error(new TaskCanceledException(), TimeSpan.FromSeconds(15)).Status);
    }

    [Fact]
    public async Task A_cancelled_check_is_cancelled_not_failed()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ClaudeProvider(new HttpClient(new StalledHandler())).CheckAsync(ProviderKit.Key(), cancelled.Token));
    }

    [Fact]
    public void Without_opus_5_5_the_newest_active_opus_is_recommended_and_lines_are_never_guessed_from_ids()
    {
        var options = ClaudeProvider.Options(
        [
            new ClaudeModel("claude-opus-5-5", "Claude Opus 5.5", "deprecated", "opus"),
            new ClaudeModel("claude-opus-5", "Claude Opus 5", "active", "opus"),
            new ClaudeModel("claude-haiku-like", "Mystery", "active", null),
            new ClaudeModel("claude-sonnet-4", "Claude Sonnet 4", "retired", "sonnet"),
        ]);

        Assert.Equal(["claude-opus-5-5", "claude-opus-5", "claude-haiku-like"], options.Select(o => o.Id)); // retired models are gone
        Assert.Equal(ModelTags.Deprecated, options[0].Tags);
        Assert.Equal(ModelTags.Recommended, options[1].Tags);
        Assert.Equal(ModelTags.None, options[2].Tags);
    }
}
