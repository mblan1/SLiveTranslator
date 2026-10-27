using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation.Providers;

/// <summary>A model as GET /v1/models lists it.</summary>
/// <param name="ShutdownDate">Null when no shutdown is announced.</param>
internal sealed record OpenAIModel(string Id, DateTimeOffset? Created, DateTimeOffset? ShutdownDate);

/// <summary>
/// OpenAI through the Responses API. The model list has no capability field, so text models are told by their id. A
/// listed key may still have no credit (billing problems show only on a generation), so the check ends with one tiny
/// test answer.
/// </summary>
public sealed partial class OpenAIProvider(HttpClient? http = null, TimeProvider? time = null) : ITranslationProvider
{
    internal const string BaseUrl = "https://api.openai.com/v1";

    /// <summary>Recommended in this order when the key lists them: fast, natural writing first.</summary>
    static readonly string[] Preferred = ["gpt-6.1-terra", "gpt-6-luna", "gpt-6.1-sol"];

    static readonly string[] NotForText =
        ["embedding", "image", "tts", "transcribe", "realtime", "audio", "moderation", "search", "-pro", "chat-latest", "daybreak", "deep-research", "translate"];

    /// <summary>429 codes that mean no credit, not a rate limit: they do not pass by waiting a minute.</summary>
    static readonly string[] BillingCodes =
        ["insufficient_quota", "credit_balance_exhausted", "organization_spend_limit_exceeded", "project_spend_limit_exceeded", "organization_usage_limit_exceeded"];

    static readonly Dictionary<string, string> Prices = new()
    {
        ["gpt-6.1-terra"] = "$1 / $5 per 1M tokens (cached $0.05)",
        ["gpt-6-luna"] = "$0.10 / $0.50 per 1M tokens (cached $0.01)",
        ["gpt-6.1-sol"] = "$2 / $10 per 1M tokens",
        ["gpt-6-astra"] = "$10 / $50 per 1M tokens",
    };

    readonly HttpClient _http = http ?? ProviderHttp.Shared;
    readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The reasoning step each model took this run (see <see cref="OpenAIReasoning"/>).</summary>
    readonly ConcurrentDictionary<string, OpenAIReasoning> _reasoning = new();

    public string Id => ProviderIds.OpenAI;
    public string DisplayName => "OpenAI";
    public ProviderKind Kind => ProviderKind.LLM;
    public IReadOnlyList<ProviderFieldSpec> Fields { get; } = [new(ProviderField.ApiKey, Required: true)];
    public ProfileHandling Profile => ProfileHandling.Prompt;
    public string DefaultModel => "";

    public ITranslator Create(ProviderConfig config) => new OpenAITranslator(config, _http, _reasoning);

    public async Task<ProviderCheck> CheckAsync(ProviderCredentials credentials, CancellationToken ct)
    {
        string key = credentials.ApiKey.Trim();
        if (key.Length == 0) return ProviderCheck.Fail(CheckStatus.InvalidKey, "No API key.");
        IReadOnlyList<ModelOption> options;
        try
        {
            var response = await ProviderHttp.SendAsync(_http, Request(HttpMethod.Get, "/models", key), ProviderHttp.CheckTimeout, DisplayName, ct);
            if (!response.Ok) throw Error(response);
            options = Options(ParseModels(response.Body), _time.GetUtcNow());
        }
        catch (ProviderException ex)
        {
            return ProviderCheck.Fail(ex.Status, ex.Message);
        }

        var probe = options.FirstOrDefault(o => o.Tags.HasFlag(ModelTags.Fastest)) ?? options.FirstOrDefault(o => o.Tags.HasFlag(ModelTags.Recommended)) ?? options.FirstOrDefault();
        if (probe is null) return new ProviderCheck(CheckStatus.Ok, options, null, "");
        try
        {
            var response = await ProviderHttp.SendAsync(_http, Request(HttpMethod.Post, "/responses", key,
                new { model = probe.Id, input = "Hi", max_output_tokens = 16, store = false }), ProviderHttp.CheckTimeout, DisplayName, ct);
            if (!response.Ok) throw Error(response);
            return new ProviderCheck(CheckStatus.Ok, options, null, "");
        }
        catch (ProviderException ex)
        {
            // A 401 here is the key, not the model: one restricted to reading the model list may not create answers.
            if (ex.Status == CheckStatus.InvalidKey) return new ProviderCheck(CheckStatus.NoPermission, options, null, ex.Message);
            return ex.Status is CheckStatus.NoCredit or CheckStatus.RateLimited
                ? new ProviderCheck(ex.Status, options, null, ex.Message)
                : new ProviderCheck(CheckStatus.Ok, options, null, "The test translation failed: " + ex.Message);
        }
    }

    internal static HttpRequestMessage Request(HttpMethod method, string path, string key, object? body = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        if (body is not null) request.Content = ProviderHttp.Json(body);
        return request;
    }

    /// <summary>The API's error {"error":{"message","type","param","code"}} as a status and a short message.</summary>
    internal static ProviderException Error(ProviderHttp.Response response)
    {
        using var doc = JsonRead.Parse(response.Body);
        var error = doc?.RootElement.Get("error");
        string code = error?.Str("code") ?? "";
        string type = error?.Str("type") ?? "";
        var status = (int)response.Status == 429 && (BillingCodes.Contains(code) || BillingCodes.Contains(type))
            ? CheckStatus.NoCredit
            : ProviderHttp.StatusOf(response.Status);
        return new ProviderException(status, ProviderHttp.Summary("OpenAI", response.Status, code.Length > 0 ? code : type, error?.Str("message") ?? ""),
            response.RetryAfter);
    }

    static List<OpenAIModel> ParseModels(string body)
    {
        using var doc = JsonRead.Parse(body);
        if (doc is null) throw new ProviderException(CheckStatus.Failed, "OpenAI returned an unexpected model list.");
        return doc.RootElement.Items("data")
            .Where(m => m.Str("id") is not null)
            .Select(m => new OpenAIModel(m.Str("id")!, m.Date("created"), m.Date("shutdown_date")))
            .ToList();
    }

    /// <summary>
    /// The text models, newest first: gpt-* without embedding, image, audio, realtime, search, -pro and similar ids,
    /// and without those past their shutdown date (one with a date still ahead is marked deprecated). Recommended is
    /// the first of <see cref="Preferred"/> the key lists, else the newest -terra, -luna or -mini; Fastest the newest
    /// -luna, else -mini. Neither is ever a deprecated model.
    /// </summary>
    internal static IReadOnlyList<ModelOption> Options(IReadOnlyList<OpenAIModel> models, DateTimeOffset now)
    {
        var usable = models
            .Where(m => m.Id.StartsWith("gpt-", StringComparison.Ordinal) && !NotForText.Any(m.Id.Contains) && !(m.ShutdownDate <= now))
            .OrderByDescending(m => Version(m.Id)).ThenByDescending(m => m.Created)
            .ToList();
        string? Newest(string family) => usable.FirstOrDefault(m => m.ShutdownDate is null && m.Id.Contains(family, StringComparison.Ordinal))?.Id;
        string? recommended = Preferred.FirstOrDefault(p => usable.Any(m => m.Id == p && m.ShutdownDate is null))
                              ?? Newest("-terra") ?? Newest("-luna") ?? Newest("-mini");
        string? fastest = Newest("-luna") ?? Newest("-mini");
        return usable.Select(m =>
        {
            bool deprecated = m.ShutdownDate is not null;
            var tags = (m.Id == recommended ? ModelTags.Recommended : ModelTags.None)
                       | (m.Id == fastest ? ModelTags.Fastest : ModelTags.None)
                       | (deprecated ? ModelTags.Deprecated : ModelTags.None);
            return new ModelOption(m.Id, deprecated ? m.Id + " (deprecated)" : m.Id, tags, Prices.GetValueOrDefault(m.Id));
        }).ToList();
    }

    static double Version(string id) =>
        VersionPattern().Match(id) is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

    [GeneratedRegex(@"^gpt-(\d+(?:\.\d+)?)")]
    private static partial Regex VersionPattern();
}

/// <summary>How a model is kept from reasoning, best first; a model that rejects one step gets the next for the run.</summary>
internal enum OpenAIReasoning
{
    /// <summary>Effort "none" and verbosity "low".</summary>
    None,

    /// <summary>Effort "low": a reasoning model that cannot turn reasoning off.</summary>
    Low,

    /// <summary>Neither reasoning nor verbosity: a model before GPT-5 (gpt-4.1, gpt-4o) takes neither.</summary>
    Omitted,
}

/// <summary>
/// Translation with OpenAI's Responses API: the system block as instructions, the lines as input, a strict JSON
/// schema for the answer, no reasoning (<see cref="OpenAIReasoning"/>) and nothing stored on OpenAI's side.
/// </summary>
public sealed class OpenAITranslator : ITranslator
{
    readonly HttpClient _http;
    readonly string _key;
    readonly string _model;
    readonly ConcurrentDictionary<string, OpenAIReasoning> _reasoning;
    readonly Func<int, TimeSpan> _timeout;

    /// <param name="reasoning">The step each model took, shared by the provider's translators.</param>
    /// <param name="timeout">Replaces <see cref="ProviderHttp.Deadline"/> (tests).</param>
    internal OpenAITranslator(ProviderConfig config, HttpClient http, ConcurrentDictionary<string, OpenAIReasoning> reasoning, Func<int, TimeSpan>? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(config.Credentials.ApiKey)) throw new ArgumentException("An OpenAI API key is needed.", nameof(config));
        if (string.IsNullOrWhiteSpace(config.Model)) throw new ArgumentException("An OpenAI model is needed.", nameof(config));
        _http = http;
        _key = config.Credentials.ApiKey.Trim();
        _model = config.Model.Trim();
        _reasoning = reasoning;
        _timeout = timeout ?? ProviderHttp.Deadline;
    }

    public string Name => ProviderKeys.For(ProviderIds.OpenAI, _model);

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct) =>
        ProviderHttp.WithDeadline("OpenAI", _timeout(request.Lines.Count), ct, async deadline =>
        {
            var step = _reasoning.GetValueOrDefault(_model, OpenAIReasoning.None);
            while (true)
            {
                var response = await SendAsync(request, step, deadline);
                if ((int)response.Status == 400 && step != OpenAIReasoning.Omitted && Rejected(response.Body) is { } rejected)
                {
                    // Each pass takes a later step, so this ends. A rejected verbosity means a model before GPT-5.
                    step = rejected == "verbosity" ? OpenAIReasoning.Omitted : step + 1;
                    _reasoning[_model] = step;
                    Log.Once($"openai reasoning {_model} {step}", $"openai: {_model} rejected {rejected}; it gets reasoning {step} from now on", TimeSpan.FromHours(1), LogLevel.Warn);
                    continue;
                }
                if (!response.Ok) throw OpenAIProvider.Error(response);
                return Read(response.Body, request.Lines);
            }
        });

    Task<ProviderHttp.Response> SendAsync(TranslateRequest request, OpenAIReasoning step, CancellationToken ct) =>
        ProviderHttp.SendAsync(_http, OpenAIProvider.Request(HttpMethod.Post, "/responses", _key, Body(_model, request, step)), Timeout.InfiniteTimeSpan, "OpenAI", ct);

    internal static Dictionary<string, object> Body(string model, TranslateRequest request, OpenAIReasoning step)
    {
        var format = new { type = "json_schema", name = "translation", strict = true, schema = LlmPrompt.Schema };
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["instructions"] = LlmPrompt.System(request),
            ["input"] = LlmPrompt.UserMessage(request),
            ["store"] = false,
            ["text"] = step == OpenAIReasoning.Omitted ? new { format } : (object)new { verbosity = "low", format },
            ["max_output_tokens"] = ProviderHttp.MaxOutputTokens(request.Lines.Count),
        };
        if (step != OpenAIReasoning.Omitted) body["reasoning"] = new { effort = step == OpenAIReasoning.None ? "none" : "low" };
        return body;
    }

    /// <summary>"reasoning" or "verbosity" when a 400 says the model does not take that field or value; null for any other 400.</summary>
    static string? Rejected(string body)
    {
        using var doc = JsonRead.Parse(body);
        var error = doc?.RootElement.Get("error");
        string param = error?.Str("param") ?? "";
        string message = error?.Str("message") ?? "";
        if (param.StartsWith("text.verbosity", StringComparison.Ordinal) || message.Contains("verbosity", StringComparison.OrdinalIgnoreCase)) return "verbosity";
        if (param.StartsWith("reasoning", StringComparison.Ordinal) || message.Contains("reasoning.effort", StringComparison.OrdinalIgnoreCase)) return "reasoning";
        return null;
    }

    /// <summary>The text of output[] message items; a refusal or an answer cut short fails the lines.</summary>
    TranslateResult Read(string body, IReadOnlyList<string> lines)
    {
        string text;
        using (var doc = JsonRead.Parse(body))
        {
            if (doc is null) throw new TranslatorUnavailableException("OpenAI returned an unexpected response: " + ProviderHttp.Shorten(body));
            var root = doc.RootElement;
            var parts = root.Items("output").Where(o => o.Str("type") == "message").SelectMany(o => o.Items("content")).ToList();
            if (parts.Any(p => p.Str("type") == "refusal")) throw ProviderHttp.Declined("OpenAI");
            if (root.Str("status") == "incomplete")
                throw root.Get("incomplete_details")?.Str("reason") == "content_filter" ? ProviderHttp.Declined("OpenAI") : ProviderHttp.CutOff("OpenAI");
            text = string.Concat(parts.Where(p => p.Str("type") == "output_text").Select(p => p.Str("text")));
        }
        return LlmPrompt.Parse(text, lines, "OpenAI", Name);
    }
}
