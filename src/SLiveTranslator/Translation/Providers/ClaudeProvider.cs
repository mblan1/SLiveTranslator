using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models;
using Anthropic.Models.Models;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation.Providers;

/// <summary>A model as the Models API lists it.</summary>
/// <param name="Lifecycle">"active", "deprecated" or "retired".</param>
/// <param name="Line">"opus", "sonnet", "haiku", "fable" …; null when the model belongs to no line (never guessed from the id).</param>
internal sealed record ClaudeModel(string Id, string DisplayName, string Lifecycle, string? Line);

/// <summary>
/// Claude through the Anthropic SDK. The key check lists the models the key can use (the Models API, newest first)
/// and then sends one tiny test translation, because a valid key may have no credit.
/// <para>
/// Every client runs on the shared connection pool (<see cref="ProviderHttp.Shared"/>), so a line after a quiet
/// stretch finds its connection open, and a settings save that makes a new translator opens no new pool. Such a
/// client is never disposed: that would dispose the shared HTTP client with it.
/// </para>
/// </summary>
/// <param name="http">Replaces the shared HTTP client (tests).</param>
public sealed class ClaudeProvider(HttpClient? http = null) : ITranslationProvider
{
    readonly HttpClient _http = http ?? ProviderHttp.Shared;

    /// <summary>Pre-selected after a check when nothing was saved; never switched to on the user's behalf.</summary>
    public const string RecommendedModel = "claude-opus-5-5";

    static readonly Dictionary<string, string> Prices = new()
    {
        ["claude-fable-5-1"] = "$10 / $50 per 1M tokens",
        ["claude-opus-5-5"] = "$4 / $20 per 1M tokens (cache read $0.20)",
        ["claude-sonnet-5-5"] = "$2 / $10 per 1M tokens (cache read $0.20)",
        ["claude-haiku-5-5"] = "$0.10 / $0.50 per 1M tokens",
    };

    /// <summary>Kept with the provider for the whole run, so a settings save does not try a rejected option again.</summary>
    readonly ClaudeModelMemory _memory = new();

    public string Id => ProviderIds.Claude;
    public string DisplayName => "Claude";
    public ProviderKind Kind => ProviderKind.LLM;
    public IReadOnlyList<ProviderFieldSpec> Fields { get; } = [new(ProviderField.ApiKey, Required: true)];
    public ProfileHandling Profile => ProfileHandling.Prompt;
    public string DefaultModel => "";

    public ITranslator Create(ProviderConfig config) => new ClaudeTranslator(config, _memory, _http);

    public async Task<ProviderCheck> CheckAsync(ProviderCredentials credentials, CancellationToken ct)
    {
        // The SDK would use ANTHROPIC_API_KEY or a login profile for an empty key: the check tests only what the user typed.
        if (credentials.ApiKey.Trim().Length == 0) return ProviderCheck.Fail(CheckStatus.InvalidKey, "No API key.");
        var client = Client(credentials.ApiKey, ProviderHttp.CheckTimeout, maxRetries: 1, _http);
        IReadOnlyList<ModelOption> options;
        try
        {
            var page = await client.Models.List(new ModelListParams { Limit = 100 }, ct);
            var models = new List<ClaudeModel>();
            await foreach (var m in page.Paginate(ct)) models.Add(new ClaudeModel(m.ID, m.DisplayName, m.Lifecycle.Raw(), m.Line?.Raw()));
            options = Options(models);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var error = Error(ex, ProviderHttp.CheckTimeout);
            return ProviderCheck.Fail(error.Status, error.Message);
        }

        var probe = options.FirstOrDefault(o => o.Tags.HasFlag(ModelTags.Fastest)) ?? options.FirstOrDefault(o => o.Tags.HasFlag(ModelTags.Recommended)) ?? options.FirstOrDefault();
        if (probe is null) return new ProviderCheck(CheckStatus.Ok, options, null, "");
        try
        {
            // Through the same 400 handling as a translation: an older model listed here (Haiku 4.5) rejects the effort.
            await ClaudeTranslator.Send(client, probe.Id, ClaudeTranslator.ProbeFeatures(probe.Id), _memory,
                features => ClaudeTranslator.ProbeParams(probe.Id, features), ct);
            return new ProviderCheck(CheckStatus.Ok, options, null, "");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var error = Error(ex, ProviderHttp.CheckTimeout);
            return error.Status is CheckStatus.NoCredit or CheckStatus.RateLimited
                ? new ProviderCheck(error.Status, options, null, error.Message)
                : new ProviderCheck(CheckStatus.Ok, options, null, "The test translation failed: " + error.Message);
        }
    }

    /// <summary>
    /// The models the key lists, newest first, without retired ones. Recommended is Opus 5.5 (else the newest active
    /// Opus), Balanced the newest active Sonnet, Fastest the newest active Haiku, by the model's line.
    /// </summary>
    internal static IReadOnlyList<ModelOption> Options(IReadOnlyList<ClaudeModel> models)
    {
        var listed = models.Where(m => m.Lifecycle != "retired").ToList();
        string? NewestActive(string line) => listed.FirstOrDefault(m => m.Lifecycle == "active" && m.Line == line)?.Id;
        string? recommended = listed.Any(m => m.Id == RecommendedModel && m.Lifecycle == "active") ? RecommendedModel : NewestActive("opus");
        string? balanced = NewestActive("sonnet");
        string? fastest = NewestActive("haiku");
        return listed.Select(m =>
        {
            bool deprecated = m.Lifecycle == "deprecated";
            string name = m.DisplayName.Length > 0 ? m.DisplayName : m.Id;
            var tags = (m.Id == recommended ? ModelTags.Recommended : ModelTags.None)
                       | (m.Id == balanced ? ModelTags.Balanced : ModelTags.None)
                       | (m.Id == fastest ? ModelTags.Fastest : ModelTags.None)
                       | (deprecated ? ModelTags.Deprecated : ModelTags.None);
            return new ModelOption(m.Id, deprecated ? name + " (deprecated)" : name, tags, Prices.GetValueOrDefault(m.Id));
        }).ToList();
    }

    /// <summary>A client on <paramref name="http"/>; its own timeout bounds each request below the HTTP client's.</summary>
    internal static AnthropicClient Client(string apiKey, TimeSpan timeout, int maxRetries, HttpClient http) =>
        new() { ApiKey = apiKey.Trim(), Timeout = timeout, MaxRetries = maxRetries, HttpClient = http, Handlers = [new ClaudeRetryAfter()] };

    /// <summary>A short message that is the same for every line the problem fails; the SDK's full one is logged once.</summary>
    /// <param name="retryAfter">The answer's Retry-After (<see cref="ClaudeRetryAfter"/>), for how long translation passes Claude over.</param>
    internal static ProviderException Error(Exception ex, TimeSpan timeout, TimeSpan? retryAfter = null)
    {
        if (ex is OperationCanceledException)
            return new ProviderException(CheckStatus.Timeout, $"Claude did not answer within {timeout.TotalSeconds:0} seconds.");
        if (ex is AnthropicApiException api)
        {
            var (type, message) = ErrorOf(api);
            Log.Once($"claude {(int)api.StatusCode} {type}", "claude: " + api.Message, ProviderException.AccountProblemPause, LogLevel.Warn);
            // An empty balance has come as a billing_error and as a 400 invalid_request_error "Your credit balance is too low".
            var status = api.ErrorType is ErrorType.BillingError || type == "billing_error" || api.StatusCode == HttpStatusCode.PaymentRequired
                         || message.Contains("credit balance", StringComparison.OrdinalIgnoreCase) ? CheckStatus.NoCredit
                : api.StatusCode == HttpStatusCode.Unauthorized || api.ErrorType is ErrorType.AuthenticationError || type == "authentication_error" ? CheckStatus.InvalidKey
                : api.StatusCode == HttpStatusCode.Forbidden || api.ErrorType is ErrorType.PermissionError || type == "permission_error" ? CheckStatus.NoPermission
                : api.StatusCode == HttpStatusCode.TooManyRequests ? CheckStatus.RateLimited
                : CheckStatus.Failed;
            return new ProviderException(status, ProviderHttp.Summary("Claude", api.StatusCode, type, message), retryAfter);
        }
        Log.Once("claude " + ex.GetType().Name, "claude: " + ex, ProviderException.AccountProblemPause, LogLevel.Warn);
        return ex is AnthropicIOException or HttpRequestException
            ? new ProviderException(CheckStatus.Network, ProviderHttp.Unreachable("Claude", ex.InnerException as HttpRequestException ?? ex))
            : new ProviderException(CheckStatus.Failed, $"Claude failed ({ProviderHttp.Shorten(ProviderHttp.FirstLine(ex.Message))}).");
    }

    /// <summary>The error's type and message from the API's body {"type":"error","error":{"type","message"},"request_id"}.</summary>
    internal static (string Type, string Message) ErrorOf(AnthropicApiException api)
    {
        using var doc = JsonRead.Parse(api.ResponseBody ?? "");
        // Not the API's error body (a proxy's page): the status says enough.
        if (doc?.RootElement.Get("error") is not { ValueKind: JsonValueKind.Object } error) return ("", "");
        return (error.Str("type") ?? "", error.Str("message") ?? "");
    }
}
