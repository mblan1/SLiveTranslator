using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation.Providers;

/// <summary>Request options a model may reject.</summary>
[Flags]
internal enum ClaudeFeatures { None = 0, Fallbacks = 1, Format = 2, Thinking = 4, Effort = 8 }

/// <summary>What each model rejected with a 400, for the whole run: it is left out of every later request to that model.</summary>
internal sealed class ClaudeModelMemory
{
    readonly ConcurrentDictionary<string, ClaudeFeatures> _rejected = new();

    public ClaudeFeatures Rejected(string model) => _rejected.TryGetValue(model, out var features) ? features : ClaudeFeatures.None;

    public void Reject(string model, ClaudeFeatures feature) => _rejected.AddOrUpdate(model, feature, (_, features) => features | feature);
}

/// <summary>
/// Translation with Claude. The instructions and the game profile are one cached system block (<see cref="LlmPrompt"/>);
/// the request holds the recent conversation and the new lines as JSON, and the answer is one item per line, held to
/// the schema by structured output.
/// <para>
/// Per model: the server-side refusal fallback ("default" form) only where the model has one (see
/// <see cref="FallbackModels"/>), never temperature, thinking off where the model allows it (Haiku 5.5 "disabled", Sonnet 5.5 "between_tools"; Opus
/// 5.5 and Fable cannot turn it off), and the effort always set (Opus 5.5 and Haiku 5.5 default to medium). A 400 that
/// names one of these options sends the request again without it, and the model is not sent it again this run.
/// </para>
/// <para>
/// Whatever goes wrong (key, credit, rate limit, network, a stalled request, a refusal, an answer of the wrong shape)
/// is reported as <see cref="TranslatorUnavailableException"/> with a short message, so the next service of the chain
/// translates the line and the chain passes Claude over for a while. The SDK does not retry: for a live subtitle the
/// chain is the retry.
/// </para>
/// </summary>
public sealed class ClaudeTranslator : ITranslator
{
    internal const string FallbackBeta = "server-side-fallback-2026-07-01";

    readonly AnthropicClient _client;
    readonly string _model;
    readonly Effort _effort;
    readonly ClaudeModelMemory _memory;
    readonly Func<int, TimeSpan> _timeout;

    /// <param name="http">Replaces the shared connection pool, <see cref="ProviderHttp.Shared"/> (tests).</param>
    /// <param name="timeout">Replaces <see cref="ProviderHttp.Deadline"/> (tests).</param>
    internal ClaudeTranslator(ProviderConfig config, ClaudeModelMemory memory, HttpClient? http = null, Func<int, TimeSpan>? timeout = null)
    {
        // An empty key would make the SDK use ANTHROPIC_API_KEY or an "ant auth login" profile, which may bill someone else.
        if (string.IsNullOrWhiteSpace(config.Credentials.ApiKey)) throw new ArgumentException("A Claude API key is needed.", nameof(config));
        if (string.IsNullOrWhiteSpace(config.Model)) throw new ArgumentException("A Claude model is needed.", nameof(config));
        // The deadline is set per request; the client's own only backs it up. The client is never disposed (see ClaudeProvider).
        _client = ClaudeProvider.Client(config.Credentials.ApiKey, ProviderHttp.Deadline(int.MaxValue), maxRetries: 0, http ?? ProviderHttp.Shared);
        _model = config.Model.Trim();
        _effort = config.Effort == LlmEffort.Medium ? Effort.Medium : Effort.Low;
        _memory = memory;
        _timeout = timeout ?? ProviderHttp.Deadline;
    }

    public string Name => ProviderKeys.For(ProviderIds.Claude, _model);

    /// <summary>The token usage of the last answer; the live test checks the prompt cache with it.</summary>
    internal BetaUsage? LastUsage { get; private set; }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public async Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
    {
        var timeout = _timeout(request.Lines.Count);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var retryAfter = ClaudeRetryAfter.Watch();
        try
        {
            var response = await Send(_client, _model, Features(_model), _memory, features => Params(_model, request, features, _effort), deadline.Token);
            LastUsage = response.Usage;
            if (response.StopReason is { } stop && stop == BetaStopReason.Refusal) throw ProviderHttp.Declined("Claude");
            if (response.StopReason is { } cut && cut == BetaStopReason.MaxTokens) throw ProviderHttp.CutOff("Claude");

            var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            return LlmPrompt.Parse(text, request.Lines, "Claude", Name);
        }
        catch (Exception ex) when (ex is not TranslatorUnavailableException && !ct.IsCancellationRequested)
        {
            throw ClaudeProvider.Error(ex, timeout, retryAfter.Value).Unavailable();
        }
    }

    /// <summary>
    /// Sends the request with the <paramref name="features"/> the model has not rejected. A 400 that names one of them
    /// sends it again without it, and the model is not sent it again this run (a translation and the key check's test
    /// answer alike).
    /// </summary>
    internal static async Task<BetaMessage> Send(AnthropicClient client, string model, ClaudeFeatures features, ClaudeModelMemory memory,
        Func<ClaudeFeatures, MessageCreateParams> build, CancellationToken ct)
    {
        features &= ~memory.Rejected(model);
        while (true)
        {
            try
            {
                return await client.Beta.Messages.Create(build(features), ct);
            }
            catch (AnthropicBadRequestException ex) when (Rejected(ClaudeProvider.ErrorOf(ex).Message, features) is { } feature)
            {
                // Each pass leaves one more option out, so this ends.
                features &= ~feature;
                memory.Reject(model, feature);
                Log.Once($"claude rejected {model} {feature}", $"claude: {model} rejected {feature} ({ClaudeProvider.ErrorOf(ex).Message}); it is left out from now on",
                    TimeSpan.FromHours(1), LogLevel.Warn);
            }
        }
    }

    /// <summary>
    /// The models with a server-side refusal fallback in the "default" form. Claude Haiku 5.5 has none and must not be
    /// sent the parameter (the API docs: a declined request stays declined, a list is a 400); an older or unknown model
    /// is not sent it either.
    /// </summary>
    static readonly HashSet<string> FallbackModels = new(StringComparer.Ordinal)
    {
        "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5",
    };

    /// <summary>What a request to the model carries before it rejected anything.</summary>
    internal static ClaudeFeatures Features(string model) =>
        ClaudeFeatures.Format | ClaudeFeatures.Effort
        | (FallbackModels.Contains(model) ? ClaudeFeatures.Fallbacks : ClaudeFeatures.None)
        | (ThinkingOff(model) is null ? ClaudeFeatures.None : ClaudeFeatures.Thinking);

    /// <summary>The option a 400's message says the model does not take, among those sent; null when it names none.</summary>
    internal static ClaudeFeatures? Rejected(string message, ClaudeFeatures sent)
    {
        string m = message.ToLowerInvariant();
        // "output_config.format cannot be combined with fallbacks": the answer is parsed tolerantly instead.
        if (sent.HasFlag(ClaudeFeatures.Format) && (m.Contains("output_config.format") || m.Contains("output format") || m.Contains("structured output") || m.Contains("json_schema")))
            return ClaudeFeatures.Format;
        if (sent.HasFlag(ClaudeFeatures.Fallbacks) && m.Contains("fallback")) return ClaudeFeatures.Fallbacks;
        if (sent.HasFlag(ClaudeFeatures.Thinking) && m.Contains("thinking")) return ClaudeFeatures.Thinking;
        if (sent.HasFlag(ClaudeFeatures.Effort) && m.Contains("effort")) return ClaudeFeatures.Effort;
        return null;
    }

    /// <summary>Thinking off for a live subtitle, where the model allows it at low and medium effort; null leaves the model's default.</summary>
    static BetaThinkingConfigParam? ThinkingOff(string model) => model switch
    {
        "claude-haiku-5-5" => new BetaThinkingConfigDisabled(),
        "claude-sonnet-5-5" => new BetaThinkingConfigBetweenTools(),
        _ => null,
    };

    internal static MessageCreateParams Params(string model, TranslateRequest request, ClaudeFeatures features, Effort effort) => With(new MessageCreateParams
    {
        Model = model,
        MaxTokens = ProviderHttp.MaxOutputTokens(request.Lines.Count),
        System = new List<BetaTextBlockParam> { new(LlmPrompt.System(request)) { CacheControl = new BetaCacheControlEphemeral() } },
        Messages = [new BetaUserMessageParam(LlmPrompt.UserMessage(request))],
    }, model, features, effort);

    /// <summary>What the key check's test answer carries before the model rejected anything: no format, no fallback.</summary>
    internal static ClaudeFeatures ProbeFeatures(string model) => Features(model) & (ClaudeFeatures.Thinking | ClaudeFeatures.Effort);

    /// <summary>A tiny request that costs next to nothing and tells whether the account can pay for answers.</summary>
    internal static MessageCreateParams ProbeParams(string model, ClaudeFeatures features) => With(new MessageCreateParams
    {
        Model = model,
        MaxTokens = 16,
        Messages = [new BetaUserMessageParam("Hi")],
    }, model, features & ProbeFeatures(model), Effort.Low);

    /// <summary>Adds only the options that are sent: an option that is not sent is not in the request at all.</summary>
    static MessageCreateParams With(MessageCreateParams p, string model, ClaudeFeatures features, Effort effort)
    {
        bool format = features.HasFlag(ClaudeFeatures.Format);
        bool withEffort = features.HasFlag(ClaudeFeatures.Effort);
        if (format || withEffort)
        {
            var output = !format ? new BetaOutputConfig { Effort = effort }
                : withEffort ? new BetaOutputConfig { Effort = effort, Format = JsonFormat() }
                : new BetaOutputConfig { Format = JsonFormat() };
            p = new MessageCreateParams(p) { OutputConfig = output };
        }
        if (features.HasFlag(ClaudeFeatures.Thinking) && ThinkingOff(model) is { } thinking) p = new MessageCreateParams(p) { Thinking = thinking };
        if (features.HasFlag(ClaudeFeatures.Fallbacks))
            p = new MessageCreateParams(p) { Betas = [FallbackBeta], Fallbacks = new Default() }; // a declined request is re-served by a fallback model in the same call
        return p;
    }

    static BetaJsonOutputFormat JsonFormat() => new() { Schema = LlmPrompt.SchemaFields };
}

/// <summary>
/// Keeps the Retry-After of the API's answers for the call that sent them, since the SDK's exceptions carry no
/// headers and the translator does not let the SDK retry. A client takes a handler of its own (the SDK attaches a
/// handler to one client only).
/// </summary>
internal sealed class ClaudeRetryAfter : DelegatingHandler
{
    static readonly AsyncLocal<StrongBox<TimeSpan?>?> Current = new();

    /// <summary>From now on in this call (and only in it: parallel lines each have their own), the last answer's Retry-After.</summary>
    public static StrongBox<TimeSpan?> Watch() => Current.Value = new StrongBox<TimeSpan?>();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (Current.Value is { } box) box.Value = ProviderHttp.RetryAfter(response);
        return response;
    }
}
