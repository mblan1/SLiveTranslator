using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation.Providers;

/// <summary>A model as an OpenAI-compatible GET /models lists it, with the capability fields some servers add.</summary>
/// <param name="Chat">Mistral's capabilities.completion_chat; null where the server does not say.</param>
/// <param name="Archived">Mistral's archived flag, or a deprecation date that has passed.</param>
/// <param name="OutputModalities">OpenRouter's architecture.output_modalities; null where the server does not say.</param>
/// <param name="PromptPrice">OpenRouter's price per token, in USD.</param>
internal sealed record CompatModel(string Id, DateTimeOffset? Created, bool? Chat = null, bool Archived = false,
    IReadOnlyList<string>? OutputModalities = null, IReadOnlyList<string>? SupportedParameters = null,
    double? PromptPrice = null, double? CompletionPrice = null, double? Context = null);

/// <summary>
/// Any server with OpenAI's chat/completions: OpenRouter, DeepSeek, Groq, Mistral, xAI, Ollama, LM Studio (see
/// <see cref="CompatPresets"/>). The key is optional (local servers take none). The check lists the models; OpenRouter's
/// list does not prove the key, so its key endpoint is asked too, and a local server tells which model is loaded.
/// </summary>
public sealed partial class CompatProvider(HttpClient? http = null, TimeProvider? time = null) : ITranslationProvider
{
    static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(8);
    static readonly TimeSpan LocalStateTimeout = TimeSpan.FromSeconds(3);

    static readonly string[] NotForChat = ["embed", "whisper", "tts", "audio", "speech", "image", "moderation", "guard", "rerank", "ocr", "orpheus"];

    /// <summary>Where "created" is a release date; Ollama's is the last pull and DeepSeek has none.</summary>
    static readonly HashSet<string> CreatedIsRelease = ["openrouter", "xai", "mistral"];

    readonly HttpClient _http = http ?? ProviderHttp.Shared;
    readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The JSON mode that works, per base URL and model, for the whole run.</summary>
    readonly ConcurrentDictionary<string, CompatJsonMode> _modes = new();

    public string Id => ProviderIds.Compat;
    public string DisplayName => "OpenAI-compatible";
    public ProviderKind Kind => ProviderKind.LLM;
    public IReadOnlyList<ProviderFieldSpec> Fields { get; } = [new(ProviderField.BaseUrl, Required: true), new(ProviderField.ApiKey, Required: false)];
    public ProfileHandling Profile => ProfileHandling.Prompt;
    public string DefaultModel => "";

    public ITranslator Create(ProviderConfig config) => new CompatTranslator(config, _http, _modes);

    /// <summary>A cloud preset (OpenRouter, Groq …) needs a key; a local server or one typed by hand may take none.</summary>
    public bool NeedsKey(ProviderCredentials credentials) => CompatPresets.Of(credentials.BaseUrl) is { NeedsKey: true };

    public async Task<ProviderCheck> CheckAsync(ProviderCredentials credentials, CancellationToken ct)
    {
        if (Normalize(credentials.BaseUrl) is not { } baseUrl)
            return ProviderCheck.Fail(CheckStatus.Failed, "Enter the server's address, e.g. http://localhost:11434/v1.");
        var preset = CompatPresets.Of(baseUrl);
        string service = ServiceName(baseUrl);
        string key = credentials.ApiKey.Trim();
        if (key.Length == 0 && NeedsKey(credentials)) return ProviderCheck.Fail(CheckStatus.InvalidKey, $"{service} needs an API key.");
        try
        {
            var response = await ProviderHttp.SendAsync(_http, Request(HttpMethod.Get, baseUrl + "/models", key), ListTimeout, service, ct);
            if (!response.Ok) throw Error(response, service);
            var models = ParseModels(response.Body, _time.GetUtcNow()) ?? throw new ProviderException(CheckStatus.Failed, $"{service} returned an unexpected model list.");

            var (status, usage, detail) = (CheckStatus.Ok, (ProviderUsage?)null, "");
            if (preset?.Id == "openrouter")
            {
                (usage, bool noCredit) = await OpenRouterKeyAsync(baseUrl, key, service, ct);
                if (noCredit) (status, detail) = (CheckStatus.NoCredit, "OpenRouter: no credit left on this key.");
            }
            var local = preset is { Local: true } ? await LocalStateAsync(preset, baseUrl, ct) : new LocalState();
            if (local.Contexts.FirstOrDefault(c => c.Value < MinLocalContext) is { Key: { } small, Value: var context })
                detail = $"{preset!.Name} runs {small} with a context of {context} tokens; the translation prompt needs at least {MinLocalContext}, "
                         + "or the server cuts off its instructions. Raise the context length (Ollama: OLLAMA_CONTEXT_LENGTH or num_ctx; "
                         + "LM Studio: the model's context length) and load the model again.";
            return new ProviderCheck(status, Options(models, preset, local.Loaded, local.NotChat), usage, detail);
        }
        catch (ProviderException ex)
        {
            return ProviderCheck.Fail(ex.Status, ex.Message);
        }
    }

    /// <summary>
    /// The context a local model needs, in tokens. The system block with a game profile is about 6-7k tokens (the
    /// Witcher 3 profile makes it 19.5k characters, part Vietnamese), and the answer and the conversation come on top;
    /// a server with less (Ollama and LM Studio often load a model with 2-4k) silently drops the start of the prompt.
    /// </summary>
    internal const int MinLocalContext = 8192;

    /// <summary>What a local server's own API says about its models.</summary>
    sealed class LocalState
    {
        public HashSet<string> Loaded { get; } = new();
        public HashSet<string> NotChat { get; } = new();

        /// <summary>The context length each loaded model runs with, where the server says.</summary>
        public Dictionary<string, int> Contexts { get; } = new();
    }

    /// <summary>OpenRouter's GET /key: the key is valid, how much it has used and may use.</summary>
    async Task<(ProviderUsage Usage, bool NoCredit)> OpenRouterKeyAsync(string baseUrl, string key, string service, CancellationToken ct)
    {
        var response = await ProviderHttp.SendAsync(_http, Request(HttpMethod.Get, baseUrl + "/key", key), ListTimeout, service, ct);
        if (!response.Ok) throw Error(response, service);
        using var doc = JsonRead.Parse(response.Body);
        var data = doc?.RootElement.Get("data");
        double? remaining = data?.Num("limit_remaining");
        return (new ProviderUsage(data?.Num("usage") ?? 0, data?.Num("limit"), "USD"), remaining <= 0);
    }

    /// <summary>
    /// What a local server's own API says: the loaded models and the context each runs with (Ollama /api/ps
    /// "context_length", LM Studio /api/v0/models "loaded_context_length"), and LM Studio's embedding models. Best
    /// effort: an older server without these endpoints or fields still lists its models.
    /// </summary>
    async Task<LocalState> LocalStateAsync(CompatPreset preset, string baseUrl, CancellationToken ct)
    {
        var state = new LocalState();
        string root = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^3] : baseUrl;
        try
        {
            var response = await ProviderHttp.SendAsync(_http, Request(HttpMethod.Get, root + (preset.Id == "ollama" ? "/api/ps" : "/api/v0/models"), ""),
                LocalStateTimeout, preset.Name, ct);
            using var doc = response.Ok ? JsonRead.Parse(response.Body) : null;
            if (doc is null) return state;
            bool ollama = preset.Id == "ollama";
            foreach (var m in doc.RootElement.Items(ollama ? "models" : "data"))
            {
                if ((ollama ? m.Str("name") ?? m.Str("model") : m.Str("id")) is not { } id) continue;
                if (!ollama && m.Str("type") == "embeddings") state.NotChat.Add(id);
                if (!ollama && m.Str("state") != "loaded") continue;
                state.Loaded.Add(id);
                if (m.Num(ollama ? "context_length" : "loaded_context_length") is double context and > 0 and < int.MaxValue) state.Contexts[id] = (int)context;
            }
        }
        catch (ProviderException)
        {
            // the model list answered; this is extra
        }
        return state;
    }

    /// <summary>The address without a trailing slash; null when it is no http(s) URL.</summary>
    internal static string? Normalize(string baseUrl) =>
        Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? baseUrl.Trim().TrimEnd('/') : null;

    /// <summary>The preset's name, or the server's host and port, for messages.</summary>
    internal static string ServiceName(string baseUrl) => CompatPresets.Of(baseUrl)?.Name ?? new Uri(baseUrl).Authority;

    internal static HttpRequestMessage Request(HttpMethod method, string url, string key, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) request.Content = ProviderHttp.Json(body);
        return request;
    }

    /// <summary>
    /// The server's error ({"error":{"message","type"|"code"}}, {"error":"..."}, {"message"} or {"detail"}): a
    /// 401/403 is the key, 402 no credit, 429 a rate limit, and a 404 without a message the wrong base URL.
    /// </summary>
    internal static ProviderException Error(ProviderHttp.Response response, string service)
    {
        using var doc = JsonRead.Parse(response.Body);
        var root = doc?.RootElement;
        var error = root?.Get("error");
        string message = (error?.ValueKind == JsonValueKind.String ? error.Value.GetString() : error?.Str("message"))
                         ?? root?.Str("message") ?? root?.Str("detail") ?? "";
        string type = error?.Str("type") ?? error?.Str("code") ?? "";
        if (response.Status == HttpStatusCode.NotFound && message.Length == 0) message = "nothing at this address; check the base URL (it usually ends in /v1)";
        var status = response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? CheckStatus.InvalidKey : ProviderHttp.StatusOf(response.Status);
        return new ProviderException(status, ProviderHttp.Summary(service, response.Status, type, message), response.RetryAfter);
    }

    static List<CompatModel>? ParseModels(string body, DateTimeOffset now)
    {
        using var doc = JsonRead.Parse(body);
        if (doc is null) return null;
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : doc.RootElement.Items("data");
        return items.Where(m => m.Str("id") is not null).Select(m => new CompatModel(
            m.Str("id")!,
            m.Date("created"),
            m.Get("capabilities")?.Bool("completion_chat"),
            m.Bool("archived") == true || m.Date("deprecation") <= now,
            m.Get("architecture")?.Get("output_modalities") is not null ? m.Get("architecture")!.Value.Strings("output_modalities").ToList()
                : m.Get("output_modalities") is not null ? m.Strings("output_modalities").ToList() : null,
            m.Strings("supported_parameters").ToList(),
            m.Get("pricing")?.Num("prompt"),
            m.Get("pricing")?.Num("completion"),
            m.Num("context_length") ?? m.Num("max_context_length") ?? m.Num("context_window"))).ToList();
    }

    /// <summary>
    /// The chat models, demoted ones (archived, preview, experimental, beta) last. Fastest is a local server's loaded
    /// model, else the first whose name says it is fast (flash, instant, mini, small, lite, fast, turbo, nano).
    /// Recommended is the newest by release date, only where "created" is one (OpenRouter, xAI, Mistral; OpenRouter
    /// only among models with structured output), the cheaper and then the larger context first on a tie.
    /// </summary>
    internal static IReadOnlyList<ModelOption> Options(IReadOnlyList<CompatModel> models, CompatPreset? preset,
        IReadOnlySet<string> loaded, IReadOnlySet<string> notChat)
    {
        var usable = models.Where(m => m.Chat != false && !notChat.Contains(m.Id) && (m.OutputModalities is null || m.OutputModalities.Contains("text"))
                                       && !NotForChat.Any(w => m.Id.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        bool Demoted(CompatModel m) => m.Archived || Unstable().IsMatch(m.Id);
        var ordered = usable.Where(m => !Demoted(m)).Concat(usable.Where(Demoted)).ToList();
        string? fastest = preset is { Local: true }
            ? ordered.FirstOrDefault(m => loaded.Contains(m.Id))?.Id
            : ordered.FirstOrDefault(m => !Demoted(m) && FastWord().IsMatch(m.Id))?.Id;
        string? recommended = preset is not null && CreatedIsRelease.Contains(preset.Id)
            ? ordered.Where(m => !Demoted(m) && m.Created is not null && (preset.Id != "openrouter" || m.SupportedParameters?.Contains("structured_outputs") == true))
                .OrderByDescending(m => m.Created).ThenBy(m => m.PromptPrice ?? double.MaxValue).ThenByDescending(m => m.Context ?? 0)
                .FirstOrDefault()?.Id
            : null;
        return ordered.Select(m => new ModelOption(m.Id, m.Archived ? m.Id + " (deprecated)" : m.Id,
            (m.Id == recommended ? ModelTags.Recommended : ModelTags.None) | (m.Id == fastest ? ModelTags.Fastest : ModelTags.None)
            | (m.Archived ? ModelTags.Deprecated : ModelTags.None),
            Cost(m))).ToList();
    }

    static string? Cost(CompatModel m) => (m.PromptPrice, m.CompletionPrice) switch
    {
        (0, 0) => "free",
        ({ } prompt, { } completion) => string.Create(CultureInfo.InvariantCulture, $"${prompt * 1e6:0.###} / ${completion * 1e6:0.###} per 1M tokens"),
        _ => null,
    };

    [GeneratedRegex(@"(^|[-_:/.])(preview|exp|beta)([-_:/.\d]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Unstable();

    [GeneratedRegex(@"(^|[-_:/.])(flash|instant|mini|small|lite|fast|turbo|nano)([-_:/.\d]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex FastWord();
}

/// <summary>How the answer is held to JSON, best first; a server that rejects one gets the next.</summary>
internal enum CompatJsonMode { Schema, Object, Prompt }

/// <summary>
/// Translation through chat/completions. The JSON mode steps down (json_schema strict, json_object, the prompt
/// alone) only on a 400/422 that says the format is unsupported, or OpenRouter's 404 "No endpoints found" (no
/// endpoint of the model takes the format), and the step that works is kept per base URL and model. A schema
/// mismatch (Groq) or an empty answer (DeepSeek) is tried once more as it is. The answer is always read tolerantly,
/// since a server may ignore the format and still answer 200.
/// </summary>
public sealed class CompatTranslator : ITranslator
{
    /// <summary>
    /// The least a local server gets per request: after its keep-alive the model loads again, while the game holds the
    /// GPU, before it reads the long system block. A cold start is no outage.
    /// </summary>
    static readonly TimeSpan ColdStart = TimeSpan.FromSeconds(45);

    internal static TimeSpan LocalDeadline(int lines) => TimeSpan.FromTicks(Math.Max(ProviderHttp.Deadline(lines).Ticks, ColdStart.Ticks));

    readonly HttpClient _http;
    readonly string _baseUrl;
    readonly string _key;
    readonly string _model;
    readonly string _service;
    readonly bool _openRouter;
    readonly ConcurrentDictionary<string, CompatJsonMode> _modes;
    readonly Func<int, TimeSpan> _timeout;

    /// <param name="timeout">Replaces <see cref="ProviderHttp.Deadline"/> (tests).</param>
    internal CompatTranslator(ProviderConfig config, HttpClient http, ConcurrentDictionary<string, CompatJsonMode> modes, Func<int, TimeSpan>? timeout = null)
    {
        _baseUrl = CompatProvider.Normalize(config.Credentials.BaseUrl) ?? throw new ArgumentException("A server address (http or https) is needed.", nameof(config));
        if (string.IsNullOrWhiteSpace(config.Model)) throw new ArgumentException("A model is needed.", nameof(config));
        _http = http;
        _key = config.Credentials.ApiKey.Trim();
        _model = config.Model.Trim();
        _service = CompatProvider.ServiceName(_baseUrl);
        var preset = CompatPresets.Of(_baseUrl);
        _openRouter = preset?.Id == "openrouter";
        _modes = modes;
        _timeout = timeout ?? (preset is { Local: true } ? LocalDeadline : ProviderHttp.Deadline);
    }

    public string Name => ProviderKeys.For(ProviderIds.Compat, _model);

    string ModeKey => _baseUrl + "|" + _model;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct) =>
        ProviderHttp.WithDeadline(_service, _timeout(request.Lines.Count), ct, async deadline =>
        {
            var mode = _modes.GetValueOrDefault(ModeKey, CompatJsonMode.Schema);
            bool triedAgain = false;
            while (true)
            {
                var response = await ProviderHttp.SendAsync(_http,
                    CompatProvider.Request(HttpMethod.Post, _baseUrl + "/chat/completions", _key, Body(_model, request, mode, _openRouter)),
                    Timeout.InfiniteTimeSpan, _service, deadline);
                // OpenRouter may answer 200 with an error and no choices: its code is the status.
                if (response.Ok && ErrorCode(response.Body) is { } code) response = response with { Status = code };
                if (response.Ok)
                {
                    if (Read(response.Body, request.Lines) is { } result) return result;
                    if (triedAgain) throw new TranslatorUnavailableException($"{_service} returned an empty answer.");
                    triedAgain = true; // "may occasionally return empty content" (DeepSeek)
                    continue;
                }
                if ((int)response.Status is 400 or 404 or 422)
                {
                    string message = Message(response.Body);
                    if ((int)response.Status != 404 && !triedAgain && message.Contains("does not match the expected schema", StringComparison.OrdinalIgnoreCase))
                    {
                        triedAgain = true; // the model's answer, not the server's support (Groq)
                        continue;
                    }
                    // A 404 steps down only when it names the format: OpenRouter's "No endpoints found that can handle
                    // the requested parameters", for a model none of whose endpoints takes it.
                    if (mode != CompatJsonMode.Prompt && NamesFormat(message))
                    {
                        mode++;
                        _modes[ModeKey] = mode;
                        Log.Once($"compat mode {ModeKey}", $"compat: {_service} {_model} does not take this JSON format; it gets {mode} from now on", TimeSpan.FromHours(1), LogLevel.Warn);
                        triedAgain = false;
                        continue;
                    }
                }
                throw CompatProvider.Error(response, _service);
            }
        });

    internal static Dictionary<string, object> Body(string model, TranslateRequest request, CompatJsonMode mode, bool openRouter)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[] { new { role = "system", content = LlmPrompt.System(request) }, new { role = "user", content = LlmPrompt.UserMessage(request) } },
            ["max_tokens"] = ProviderHttp.MaxOutputTokens(request.Lines.Count),
        };
        switch (mode)
        {
            case CompatJsonMode.Schema:
                body["response_format"] = new { type = "json_schema", json_schema = new { name = "translation", strict = true, schema = LlmPrompt.Schema } };
                break;
            case CompatJsonMode.Object:
                body["response_format"] = new { type = "json_object" };
                break;
        }
        // Only to endpoints that honour the format; one that ignores it would answer 200 with prose.
        if (openRouter && mode != CompatJsonMode.Prompt) body["provider"] = new { require_parameters = true };
        return body;
    }

    static string Message(string body)
    {
        using var doc = JsonRead.Parse(body);
        var error = doc?.RootElement.Get("error");
        return (error?.ValueKind == JsonValueKind.String ? error.Value.GetString() : error?.Str("message")) ?? doc?.RootElement.Str("message") ?? "";
    }

    /// <summary>
    /// Only OpenRouter's capability wording of "No endpoints found": the same prefix also says the model is unknown
    /// or that the account's data policy rules out every endpoint, which no other JSON mode changes.
    /// </summary>
    static bool NamesFormat(string message) =>
        new[] { "response_format", "json_schema", "json_object", "structured output", "responseformat", "no endpoints found that can handle the requested parameters" }
            .Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The HTTP error code of a 200 answer that is an error {"error":{"code":402,"message"}}; null for any other answer.</summary>
    static HttpStatusCode? ErrorCode(string body)
    {
        using var doc = JsonRead.Parse(body);
        return doc?.RootElement.Get("error")?.Num("code") is double code and >= 400 and < 600 ? (HttpStatusCode)(int)code : null;
    }

    /// <summary>The answer; null for an empty one.</summary>
    TranslateResult? Read(string body, IReadOnlyList<string> lines)
    {
        string? content;
        using (var doc = JsonRead.Parse(body))
        {
            if (doc is null) throw new TranslatorUnavailableException($"{_service} returned an unexpected response: " + ProviderHttp.Shorten(body));
            var root = doc.RootElement;
            // An error without an HTTP code (one with a code is handled as that status).
            if (root.Get("error") is { ValueKind: JsonValueKind.Object } error)
                throw new ProviderException(CheckStatus.Failed, $"{_service} failed: {ProviderHttp.Shorten(error.Str("message") ?? "")}");
            var choice = root.Items("choices").FirstOrDefault();
            var message = choice.Get("message");
            if (message?.Str("refusal") is { Length: > 0 }) throw ProviderHttp.Declined(_service);
            switch (choice.Str("finish_reason"))
            {
                case "length":
                    throw ProviderHttp.CutOff(_service);
                case "content_filter":
                    throw ProviderHttp.Declined(_service);
                case "error":
                    throw new TranslatorUnavailableException($"{_service} failed while answering.");
            }
            content = message?.Str("content");
        }
        return string.IsNullOrWhiteSpace(content) ? null : LlmPrompt.Parse(content, lines, _service, Name);
    }
}
