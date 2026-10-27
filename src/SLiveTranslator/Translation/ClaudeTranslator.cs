using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models;
using Anthropic.Models.Beta.Messages;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation;

/// <summary>
/// Translation with Claude. The instructions and the game profile are a cached system prompt; the request holds the
/// recent conversation and the new lines as JSON, and the answer is one item per line.
/// <para>
/// Whatever goes wrong (key, credit, rate limit, network, a stalled request, an answer of the wrong shape) is
/// reported as <see cref="TranslatorUnavailableException"/> with a short message, so the next service of the chain
/// translates the line and the chain passes Claude over for a while. The SDK does not retry: for a live subtitle the
/// chain is the retry.
/// </para>
/// </summary>
public sealed class ClaudeTranslator : ITranslator
{
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>How long Claude is passed over after a key, permission or credit error: it does not go away by itself.</summary>
    internal static readonly TimeSpan AccountProblemPause = TimeSpan.FromMinutes(10);

    static readonly JsonSerializerOptions MessageJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly AnthropicClient _client;
    readonly string _model;
    readonly Effort _effort;
    readonly Func<int, TimeSpan> _timeout;

    /// <param name="apiKey">
    /// Never empty: the SDK would then use ANTHROPIC_API_KEY or an "ant auth login" profile, which may bill someone else.
    /// </param>
    public ClaudeTranslator(string apiKey, string model, LlmEffort effort = LlmEffort.Low) : this(apiKey, model, effort, null, null)
    {
    }

    /// <param name="http">Replaces the SDK's own HTTP client (tests).</param>
    /// <param name="timeout">Replaces <see cref="RequestTimeout"/> (tests).</param>
    internal ClaudeTranslator(string apiKey, string model, LlmEffort effort, HttpClient? http, Func<int, TimeSpan>? timeout)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("A Claude API key is needed.", nameof(apiKey));
        // The deadline is set per request (RequestTimeout); the client's own only backs it up.
        _client = http is null
            ? new AnthropicClient { ApiKey = apiKey.Trim(), Timeout = RequestTimeout(int.MaxValue), MaxRetries = 0 }
            : new AnthropicClient { ApiKey = apiKey.Trim(), Timeout = RequestTimeout(int.MaxValue), MaxRetries = 0, HttpClient = http };
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _effort = effort == LlmEffort.Medium ? Effort.Medium : Effort.Low;
        _timeout = timeout ?? RequestTimeout;
    }

    public string Name => ProviderKeys.For(ProviderIds.Claude, _model);

    /// <summary>
    /// How long a request of this many lines may take. A subtitle line (1-3 s with Opus) must not wait long for a
    /// stalled request: the free services answer it instead. A translate-screen chunk of 16 dense paragraphs, each
    /// repeated as its fixed source in the answer, takes a while longer.
    /// </summary>
    internal static TimeSpan RequestTimeout(int lines) => TimeSpan.FromSeconds(Math.Clamp(6 + 4 * (long)lines, 10, 120));

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public async Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
    {
        var timeout = _timeout(request.Lines.Count);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var response = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 4096,
                System = SystemBlocks(request),
                OutputConfig = new BetaOutputConfig { Effort = _effort },
                // Safety-classifier declines are re-served by a fallback model inside the same call.
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
                Messages = [new BetaUserMessageParam(UserMessage(request))],
            }, deadline.Token);

            if (response.StopReason is { } stop && stop == BetaStopReason.Refusal)
                throw new TranslatorUnavailableException("Claude declined to translate this text.");

            var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            var (texts, sources) = ParseItems(text, request.Lines);
            return new TranslateResult(texts, sources, Name);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslatorUnavailableException($"Claude did not answer within {timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is not TranslatorUnavailableException && !ct.IsCancellationRequested)
        {
            throw Unavailable(ex);
        }
    }

    /// <summary>A short message that is the same for every line the problem fails; the full one is logged once.</summary>
    internal static TranslatorUnavailableException Unavailable(Exception ex)
    {
        if (ex is AnthropicApiException api)
        {
            var (type, message) = ErrorOf(api);
            string summary = $"Claude: HTTP {(int)api.StatusCode}" + (type.Length > 0 ? " " + type : "") + (message.Length > 0 ? ": " + message : "");
            Log.Once($"claude {(int)api.StatusCode} {type}", "claude: " + api.Message, AccountProblemPause, LogLevel.Warn);
            bool account = api.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                           || api.ErrorType is ErrorType.AuthenticationError or ErrorType.PermissionError or ErrorType.BillingError
                           || type is "authentication_error" or "permission_error" or "billing_error";
            return new TranslatorUnavailableException(summary, account ? AccountProblemPause : null);
        }
        Log.Once("claude " + ex.GetType().Name, "claude: " + ex, AccountProblemPause, LogLevel.Warn);
        string what = ex is AnthropicIOException or HttpRequestException ? "cannot be reached" : "failed";
        return new TranslatorUnavailableException($"Claude {what} ({Shorten(FirstLine(ex.Message))}).");
    }

    /// <summary>The error's type and message from the API's body {"type":"error","error":{"type","message"},"request_id"}.</summary>
    static (string Type, string Message) ErrorOf(AnthropicApiException api)
    {
        try
        {
            using var doc = JsonDocument.Parse(api.ResponseBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                string Field(string name) => error.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                return (Field("type"), Shorten(Field("message")));
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // not the API's error body (a proxy's page): the status says enough
        }
        return ("", "");
    }

    static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    static string Shorten(string text) => text.Length > 200 ? text[..200] + "…" : text;

    /// <summary>The instructions, then the profile; the cache mark on the last block caches both.</summary>
    internal static List<BetaTextBlockParam> SystemBlocks(TranslateRequest request)
    {
        string instructions = Instructions(request.Src, request.Tgt);
        return request.Profile is { } profile
            ? [new(instructions), new(profile.ToPrompt()) { CacheControl = new BetaCacheControlEphemeral() }]
            : [new(instructions) { CacheControl = new BetaCacheControlEphemeral() }];
    }

    internal static string Instructions(string src, string tgt)
    {
        string from = src == "auto" ? "the language they are in (detect it)" : $"'{src}'";
        return
            $"You translate live video-game subtitles from {from} into natural spoken '{tgt}' for a player reading them mid-game. " +
            "The text comes from screen OCR and may contain recognition errors.\n" +
            "Input JSON: \"context\" = recent earlier lines with the translation already shown (use it to tell who speaks to whom " +
            "and to stay consistent; do not translate it again); \"lines\" = the new strings, each with an \"id\".\n" +
            "Output: ONLY a JSON object {\"items\":[{\"id\":0,\"source\":\"...\",\"text\":\"...\"}]} with one item per input line and the same id: " +
            "\"source\" = the line with obvious OCR errors fixed (\"11m\" → \"I'm\", \"Cirils\" → \"Ciri's\", \"youyvere\" → \"you were\"); " +
            "\"text\" = the translation. If a line is not language (random letters, HUD numbers, an unrecoverable fragment) return \"text\": \"\". " +
            "Never merge, split, drop or reorder lines.\n" +
            "Translate meaning, not words: natural equivalents for idioms. Keep tone, sarcasm, swearing and archaic speech. " +
            "Subtitle-length spoken style. Add nothing that is not implied; keep every name, number, negation and clause. " +
            "A line cut off at the edge: translate only what is there. Keep choice numbers (\"1. ...\") and key names (Space, Left Shift, Alt, Tab) as they are.\n" +
            "Speakers and pronouns: infer speaker and listener from the context and the profile, pick the pair the profile gives and keep it for that pair. " +
            "When unsure, leave the pronoun out if that is natural, or reuse the last pair seen.\n" +
            "Never translate names or the profile's keep list; use its glossary renderings exactly.";
    }

    /// <summary>{"context":[{"source","text"}],"lines":[{"id","text"}]}; outside the cached prefix, since it changes every line.</summary>
    internal static string UserMessage(TranslateRequest request) =>
        JsonSerializer.Serialize(new
        {
            context = request.Context.Select(c => new { source = c.Source, text = c.Translation }),
            lines = request.Lines.Select((l, i) => new { id = i, text = l }),
        }, MessageJson);

    /// <summary>
    /// The items of the answer, by id (a number, or a number written as a string); code fences and text around the
    /// JSON are ignored. A line the answer has no text for keeps its original, as before.
    /// </summary>
    internal static (string[] Texts, string[] Sources) ParseItems(string answer, IReadOnlyList<string> lines)
    {
        int start = answer.IndexOf('{');
        int end = answer.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(answer.AsMemory(start, end - start + 1));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    var texts = lines.ToArray();
                    var sources = lines.ToArray();
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idElement) || Id(idElement) is not { } id
                            || id < 0 || id >= lines.Count) continue;
                        if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                            texts[id] = t.GetString()!;
                        if (item.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                            sources[id] = s.GetString()!;
                    }
                    return (texts, sources);
                }
            }
            catch (JsonException)
            {
                // reported below
            }
        }
        throw new TranslatorUnavailableException("Claude returned an unexpected response: " + (answer.Length > 200 ? answer[..200] + "…" : answer));
    }

    static int? Id(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number when id.TryGetInt32(out int n) => n,
        JsonValueKind.String when int.TryParse(id.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) => n,
        _ => null,
    };
}
