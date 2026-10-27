using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation.Providers;

/// <summary>A model as GET /v1beta/models lists it; <see cref="Id"/> is its name without "models/".</summary>
internal sealed record GeminiModel(string Id, string DisplayName, IReadOnlyList<string> Methods);

/// <summary>
/// Gemini (an AI Studio key) through generateContent. The key check lists the models; the list has no deprecation
/// field, so the highest version of a family is the one to pick. Gemini has a free tier, whose prompts Google may use.
/// </summary>
public sealed partial class GeminiProvider(HttpClient? http = null) : ITranslationProvider
{
    internal const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    static readonly string[] NotForText = ["preview", "exp", "latest", "tts", "live", "image", "embedding", "omni", "computer-use", "robotics", "aqa"];

    static readonly Dictionary<string, string> Prices = new()
    {
        ["gemini-3.8-flash"] = "$0.75 / $3.75 per 1M tokens (until 2026-12-31)",
        ["gemini-3.6-flash"] = "$0.75 / $3.75 per 1M tokens",
        ["gemini-3.5-flash-lite"] = "$0.30 / $2.50 per 1M tokens",
        ["gemini-3.1-flash-lite"] = "$0.25 / $1.50 per 1M tokens",
    };

    readonly HttpClient _http = http ?? ProviderHttp.Shared;

    /// <summary>What each model rejected this run.</summary>
    readonly ConcurrentDictionary<string, GeminiFeatures> _rejected = new();

    public string Id => ProviderIds.Gemini;
    public string DisplayName => "Gemini";
    public ProviderKind Kind => ProviderKind.LLM;

    /// <summary>The project id is optional and only noted for the user: an AI Studio key already belongs to a project, so it is not sent.</summary>
    public IReadOnlyList<ProviderFieldSpec> Fields { get; } = [new(ProviderField.ApiKey, Required: true), new(ProviderField.ProjectId, Required: false)];

    public ProfileHandling Profile => ProfileHandling.Prompt;
    public string DefaultModel => "";

    public ITranslator Create(ProviderConfig config) => new GeminiTranslator(config, _http, _rejected);

    public async Task<ProviderCheck> CheckAsync(ProviderCredentials credentials, CancellationToken ct)
    {
        string key = credentials.ApiKey.Trim();
        if (key.Length == 0) return ProviderCheck.Fail(CheckStatus.InvalidKey, "No API key.");
        try
        {
            var models = new List<GeminiModel>();
            string? page = null;
            do
            {
                string path = "/models?pageSize=1000" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
                var response = await ProviderHttp.SendAsync(_http, Request(HttpMethod.Get, path, key), ProviderHttp.CheckTimeout, DisplayName, ct);
                if (!response.Ok) throw Error(response);
                using var doc = JsonRead.Parse(response.Body) ?? throw new ProviderException(CheckStatus.Failed, "Gemini returned an unexpected model list.");
                models.AddRange(doc.RootElement.Items("models").Where(m => m.Str("name") is not null).Select(m => new GeminiModel(
                    m.Str("name")!.StartsWith("models/", StringComparison.Ordinal) ? m.Str("name")!["models/".Length..] : m.Str("name")!,
                    m.Str("displayName") ?? "", m.Strings("supportedGenerationMethods").ToList())));
                page = doc.RootElement.Str("nextPageToken") is { Length: > 0 } next ? next : null;
            } while (page is not null);
            return new ProviderCheck(CheckStatus.Ok, Options(models), null, "");
        }
        catch (ProviderException ex)
        {
            return ProviderCheck.Fail(ex.Status, ex.Message);
        }
    }

    internal static HttpRequestMessage Request(HttpMethod method, string path, string key, object? body = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", key.Trim());
        if (body is not null) request.Content = ProviderHttp.Json(body);
        return request;
    }

    /// <summary>
    /// Google's error {"error":{"code","message","status","details":[{"reason"},{"retryDelay"}]}}: an invalid key is a
    /// 400 API_KEY_INVALID (or a 401/403), a region without the free tier a 400 FAILED_PRECONDITION, no prepaid credit
    /// a 402; a 429's RetryInfo says how long to wait.
    /// </summary>
    internal static ProviderException Error(ProviderHttp.Response response)
    {
        var error = GoogleApiError.Read(response);
        var status = error.Reason == "API_KEY_INVALID" || error.Message.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) ? CheckStatus.InvalidKey
            : (int)response.Status == 400 && error.Status == "FAILED_PRECONDITION" ? CheckStatus.NoPermission
            : (int)response.Status is 401 or 403 ? CheckStatus.InvalidKey
            : ProviderHttp.StatusOf(response.Status);
        return new ProviderException(status, error.Summary("Gemini", response), error.RetryAfter);
    }

    /// <summary>
    /// The text models (generateContent, no preview, experimental, -latest, audio, image or dated ids), highest
    /// version first. Recommended is the highest *-flash (not lite, not pro), Fastest the highest *-flash-lite.
    /// </summary>
    internal static IReadOnlyList<ModelOption> Options(IReadOnlyList<GeminiModel> models)
    {
        var usable = models
            .Where(m => m.Methods.Contains("generateContent") && m.Id.StartsWith("gemini-", StringComparison.Ordinal)
                        && !NotForText.Any(m.Id.Contains) && !DatedId().IsMatch(m.Id))
            .OrderByDescending(m => Version(m.Id)).ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
        string? recommended = usable.FirstOrDefault(m => Flash().IsMatch(m.Id))?.Id;
        string? fastest = usable.FirstOrDefault(m => FlashLite().IsMatch(m.Id))?.Id;
        return usable.Select(m => new ModelOption(m.Id, m.DisplayName.Length > 0 ? m.DisplayName : m.Id,
            (m.Id == recommended ? ModelTags.Recommended : ModelTags.None) | (m.Id == fastest ? ModelTags.Fastest : ModelTags.None),
            Prices.GetValueOrDefault(m.Id))).ToList();
    }

    static double Version(string id) =>
        VersionPattern().Match(id) is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

    [GeneratedRegex(@"^gemini-(\d+(?:\.\d+)?)-")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^gemini-\d+(?:\.\d+)?-flash$")]
    private static partial Regex Flash();

    [GeneratedRegex(@"^gemini-\d+(?:\.\d+)?-flash-lite$")]
    private static partial Regex FlashLite();

    /// <summary>A dated or numbered snapshot ("-001", "-05-20") of a model the list also has under its plain id.</summary>
    [GeneratedRegex(@"-\d{3,}$|-\d{2}-\d{2}$")]
    private static partial Regex DatedId();
}

/// <summary>Request fields a model may reject.</summary>
[Flags]
internal enum GeminiFeatures { None = 0, ResponseFormat = 1, Thinking = 2, Store = 4 }

/// <summary>
/// Translation with Gemini's generateContent: the system block as system_instruction, the lines as the user turn, JSON
/// held to the schema (responseFormat; a model that rejects the field gets responseMimeType + responseJsonSchema),
/// thinking kept low by family, and the request not stored. Any finish other than STOP fails the lines.
/// </summary>
public sealed class GeminiTranslator : ITranslator
{
    readonly HttpClient _http;
    readonly string _key;
    readonly string _model;
    readonly ConcurrentDictionary<string, GeminiFeatures> _rejected;
    readonly Func<int, TimeSpan> _timeout;

    /// <param name="timeout">Replaces <see cref="ProviderHttp.Deadline"/> (tests).</param>
    internal GeminiTranslator(ProviderConfig config, HttpClient http, ConcurrentDictionary<string, GeminiFeatures> rejected, Func<int, TimeSpan>? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(config.Credentials.ApiKey)) throw new ArgumentException("A Gemini API key is needed.", nameof(config));
        if (string.IsNullOrWhiteSpace(config.Model)) throw new ArgumentException("A Gemini model is needed.", nameof(config));
        _http = http;
        _key = config.Credentials.ApiKey.Trim();
        _model = config.Model.Trim();
        _rejected = rejected;
        _timeout = timeout ?? ProviderHttp.Deadline;
    }

    public string Name => ProviderKeys.For(ProviderIds.Gemini, _model);

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct) =>
        ProviderHttp.WithDeadline("Gemini", _timeout(request.Lines.Count), ct, async deadline =>
        {
            var features = (GeminiFeatures.ResponseFormat | GeminiFeatures.Thinking | GeminiFeatures.Store) & ~_rejected.GetValueOrDefault(_model);
            while (true)
            {
                var response = await ProviderHttp.SendAsync(_http,
                    GeminiProvider.Request(HttpMethod.Post, $"/models/{Uri.EscapeDataString(_model)}:generateContent", _key, Body(_model, request, features)),
                    Timeout.InfiniteTimeSpan, "Gemini", deadline);
                if (response.Ok) return Read(response.Body, request.Lines);
                if ((int)response.Status == 400 && Rejected(response.Body, features) is { } feature)
                {
                    // Each pass leaves one more field out, so this ends.
                    features &= ~feature;
                    _rejected.AddOrUpdate(_model, feature, (_, f) => f | feature);
                    Log.Once($"gemini rejected {_model} {feature}", $"gemini: {_model} rejected {feature}; it is left out from now on", TimeSpan.FromHours(1), LogLevel.Warn);
                    continue;
                }
                throw GeminiProvider.Error(response);
            }
        });

    internal static Dictionary<string, object> Body(string model, TranslateRequest request, GeminiFeatures features)
    {
        var generation = new Dictionary<string, object> { ["maxOutputTokens"] = ProviderHttp.MaxOutputTokens(request.Lines.Count) };
        if (features.HasFlag(GeminiFeatures.Thinking)) generation["thinkingConfig"] = Thinking(model);
        if (features.HasFlag(GeminiFeatures.ResponseFormat))
        {
            generation["responseFormat"] = new { text = new { mimeType = "APPLICATION_JSON", schema = LlmPrompt.Schema } };
        }
        else
        {
            generation["responseMimeType"] = "application/json";
            generation["responseJsonSchema"] = LlmPrompt.Schema;
        }
        var body = new Dictionary<string, object>
        {
            ["system_instruction"] = new { parts = new[] { new { text = LlmPrompt.System(request) } } },
            ["contents"] = new[] { new { role = "user", parts = new[] { new { text = LlmPrompt.UserMessage(request) } } } },
            ["generationConfig"] = generation,
        };
        if (features.HasFlag(GeminiFeatures.Store)) body["store"] = false;
        return body;
    }

    /// <summary>
    /// As little thinking as the family allows: 2.5 models turn it off with a zero budget, Flash-Lite and 3.6 Flash
    /// take "minimal", other Flash models (3.8 rejects "minimal") and anything unknown "low".
    /// </summary>
    internal static object Thinking(string model) =>
        model.StartsWith("gemini-2.5-", StringComparison.Ordinal) ? new { thinkingBudget = 0 }
        : model.Contains("flash-lite", StringComparison.Ordinal) || model.StartsWith("gemini-3.6-flash", StringComparison.Ordinal) ? new { thinkingLevel = "minimal" }
        : new { thinkingLevel = "low" };

    /// <summary>The field a 400 says the model or API version does not take, among those sent.</summary>
    internal static GeminiFeatures? Rejected(string body, GeminiFeatures sent)
    {
        using var doc = JsonRead.Parse(body);
        string m = (doc?.RootElement.Get("error")?.Str("message") ?? "").ToLowerInvariant();
        if (sent.HasFlag(GeminiFeatures.ResponseFormat) && (m.Contains("responseformat") || m.Contains("response_format"))) return GeminiFeatures.ResponseFormat;
        if (sent.HasFlag(GeminiFeatures.Thinking) && m.Contains("thinking")) return GeminiFeatures.Thinking;
        if (sent.HasFlag(GeminiFeatures.Store) && m.Contains("unknown name") && m.Contains("store")) return GeminiFeatures.Store;
        return null;
    }

    TranslateResult Read(string body, IReadOnlyList<string> lines)
    {
        string text;
        using (var doc = JsonRead.Parse(body))
        {
            if (doc is null) throw new TranslatorUnavailableException("Gemini returned an unexpected response: " + ProviderHttp.Shorten(body));
            var root = doc.RootElement;
            if (root.Get("promptFeedback")?.Str("blockReason") is not null) throw ProviderHttp.Declined("Gemini");
            var candidate = root.Items("candidates").FirstOrDefault();
            switch (candidate.Str("finishReason"))
            {
                case "STOP":
                    break;
                case "MAX_TOKENS":
                    throw ProviderHttp.CutOff("Gemini");
                case "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or "RECITATION":
                    throw ProviderHttp.Declined("Gemini");
                case null:
                    throw new TranslatorUnavailableException("Gemini stopped without an answer (no candidate).");
                case var other:
                    // LANGUAGE, MALFORMED_RESPONSE, OTHER …: this candidate's content, like a refusal; the next line goes to Gemini again.
                    throw new TranslatorUnavailableException($"Gemini stopped without an answer ({other}).", TimeSpan.Zero) { AboutText = true };
            }
            var parts = candidate.Get("content")?.Items("parts") ?? Enumerable.Empty<JsonElement>();
            text = string.Concat(parts.Where(p => p.Bool("thought") != true).Select(p => p.Str("text")));
        }
        return LlmPrompt.Parse(text, lines, "Gemini", Name);
    }
}
