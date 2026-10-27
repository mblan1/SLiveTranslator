using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation.Providers;

/// <summary>
/// DeepL. A key ending in ":fx" belongs to the Free API. The check reads the character usage (a limit of 10^12 means
/// none) and whether Vietnamese is a target for the key; the engine is DeepL's model type.
/// </summary>
public sealed class DeepLProvider(HttpClient? http = null) : ITranslationProvider
{
    public const string LatencyOptimized = "latency_optimized";
    public const string QualityOptimized = "quality_optimized";

    /// <summary>DeepL's character limit for an account without one.</summary>
    const double NoLimit = 1e12;

    readonly HttpClient _http = http ?? ProviderHttp.Shared;

    internal static IReadOnlyList<ModelOption> Engines { get; } =
    [
        new(LatencyOptimized, "Latency optimized", ModelTags.Recommended | ModelTags.Fastest),
        new(QualityOptimized, "Quality optimized"),
    ];

    public string Id => ProviderIds.DeepL;
    public string DisplayName => "DeepL";
    public ProviderKind Kind => ProviderKind.MT;
    public IReadOnlyList<ProviderFieldSpec> Fields { get; } = [new(ProviderField.ApiKey, Required: true)];
    public ProfileHandling Profile => ProfileHandling.Markup;
    public string DefaultModel => LatencyOptimized;

    public ITranslator Create(ProviderConfig config) => new DeepLTranslator(config, _http);

    public async Task<ProviderCheck> CheckAsync(ProviderCredentials credentials, CancellationToken ct)
    {
        string key = credentials.ApiKey.Trim();
        if (key.Length == 0) return ProviderCheck.Fail(CheckStatus.InvalidKey, "No API key.");
        ProviderUsage usage;
        try
        {
            var response = await ProviderHttp.SendAsync(_http, Request(HttpMethod.Get, "/v2/usage", key), ProviderHttp.CheckTimeout, DisplayName, ct);
            if (!response.Ok) throw Error(response);
            using var doc = JsonRead.Parse(response.Body);
            usage = Usage(doc?.RootElement);
        }
        catch (ProviderException ex)
        {
            return ProviderCheck.Fail(ex.Status, ex.Message);
        }
        if (usage.Limit is { } max && usage.Used >= max)
            return new ProviderCheck(CheckStatus.NoCredit, Engines, usage, "DeepL: the character quota of this key is used up.");
        return new ProviderCheck(CheckStatus.Ok, Engines, usage, await ListsVietnameseAsync(key, ct) == false ? "DeepL does not list Vietnamese as a target language for this key." : "");
    }

    /// <summary>
    /// The account's usage, or the key's where a Pro key has a limit of its own (api_key_character_*) with less left:
    /// that one runs out first, and a 456 follows when it does.
    /// </summary>
    static ProviderUsage Usage(JsonElement? root)
    {
        static double? Limit(double? limit) => limit >= NoLimit ? null : limit;
        var account = new ProviderUsage(root?.Num("character_count") ?? 0, Limit(root?.Num("character_limit")), "characters");
        if (Limit(root?.Num("api_key_character_limit")) is not { } keyLimit) return account;
        var key = new ProviderUsage(root?.Num("api_key_character_count") ?? 0, keyLimit, "characters");
        return account.Limit is { } accountLimit && accountLimit - account.Used <= keyLimit - key.Used ? account : key;
    }

    /// <summary>Whether /v3/languages lists Vietnamese as a target; null when the list cannot be read (the key works all the same).</summary>
    async Task<bool?> ListsVietnameseAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await ProviderHttp.SendAsync(_http, Request(HttpMethod.Get, "/v3/languages?resource=translate_text", key), ProviderHttp.CheckTimeout, DisplayName, ct);
            using var doc = response.Ok ? JsonRead.Parse(response.Body) : null;
            return doc is null ? null : NamesLanguage(doc.RootElement, "vi");
        }
        catch (ProviderException)
        {
            return null;
        }
    }

    /// <summary>A language entry anywhere in the list whose code ("language", "code" or "lang") is the language, in any case or region.</summary>
    static bool NamesLanguage(JsonElement e, string language) => e.ValueKind switch
    {
        JsonValueKind.Array => e.EnumerateArray().Any(i => NamesLanguage(i, language)),
        JsonValueKind.Object => e.EnumerateObject().Any(p =>
            (p.Name is "language" or "code" or "lang" && p.Value.ValueKind == JsonValueKind.String
             && p.Value.GetString()!.Split('-')[0].Equals(language, StringComparison.OrdinalIgnoreCase))
            || NamesLanguage(p.Value, language)),
        _ => false,
    };

    internal static string BaseUrl(string key) => key.Trim().EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com" : "https://api.deepl.com";

    internal static HttpRequestMessage Request(HttpMethod method, string path, string key, object? body = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl(key) + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key.Trim());
        if (body is not null) request.Content = ProviderHttp.Json(body);
        return request;
    }

    /// <summary>DeepL's error {"message"}: 401/403 the key, 456 the quota (also cost control), 429 too many requests.</summary>
    internal static ProviderException Error(ProviderHttp.Response response)
    {
        using var doc = JsonRead.Parse(response.Body);
        var status = (int)response.Status switch
        {
            401 or 403 => CheckStatus.InvalidKey,
            456 => CheckStatus.NoCredit,
            _ => ProviderHttp.StatusOf(response.Status),
        };
        return new ProviderException(status, ProviderHttp.Summary("DeepL", response.Status, "", doc?.RootElement.Str("message") ?? ""), response.RetryAfter);
    }
}

/// <summary>
/// Translation with DeepL's /v2/translate: all lines in one request, the English of the recent conversation as
/// context (not billed), and the profile's keep entries marked as ignored XML tags, so they come back as written.
/// </summary>
public sealed class DeepLTranslator : ITranslator
{
    const string KeepTag = "keep";

    readonly HttpClient _http;
    readonly string _key;
    readonly string _engine;
    readonly Func<int, TimeSpan> _timeout;

    /// <param name="timeout">Replaces <see cref="ProviderHttp.Deadline"/> (tests).</param>
    internal DeepLTranslator(ProviderConfig config, HttpClient http, Func<int, TimeSpan>? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(config.Credentials.ApiKey)) throw new ArgumentException("A DeepL API key is needed.", nameof(config));
        _http = http;
        _key = config.Credentials.ApiKey.Trim();
        _engine = string.IsNullOrWhiteSpace(config.Model) ? DeepLProvider.LatencyOptimized : config.Model.Trim();
        _timeout = timeout ?? ProviderHttp.Deadline;
    }

    public string Name => ProviderKeys.For(ProviderIds.DeepL, _engine);

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct) =>
        ProviderHttp.WithDeadline("DeepL", _timeout(request.Lines.Count), ct, async deadline =>
        {
            var matcher = request.Profile is { } profile ? ProfileMatcher.For(profile) : null;
            var spans = request.Lines.Select(l => matcher?.KeepSpans(l) ?? Array.Empty<TermMatch>()).ToList();
            bool tagged = spans.Any(s => s.Count > 0);
            var response = await ProviderHttp.SendAsync(_http,
                DeepLProvider.Request(HttpMethod.Post, "/v2/translate", _key, Body(request, _engine, tagged ? request.Lines.Select((l, i) => Mark(l, spans[i])).ToList() : null)),
                Timeout.InfiniteTimeSpan, "DeepL", deadline);
            if (!response.Ok) throw DeepLProvider.Error(response);
            using var doc = JsonRead.Parse(response.Body);
            var texts = doc?.RootElement.Items("translations").Select(t => t.Str("text") ?? "").ToList() ?? [];
            if (texts.Count != request.Lines.Count) throw new TranslatorUnavailableException("DeepL returned an unexpected response: " + ProviderHttp.Shorten(response.Body));
            return new TranslateResult(tagged ? texts.Select(Unmark).ToList() : texts, null, Name);
        });

    /// <param name="marked">The lines as XML with the keep entries tagged; null sends them as plain text.</param>
    internal static Dictionary<string, object> Body(TranslateRequest request, string engine, IReadOnlyList<string>? marked)
    {
        var body = new Dictionary<string, object>
        {
            ["text"] = marked ?? request.Lines,
            ["target_lang"] = Target(request.Tgt),
            ["model_type"] = engine,
        };
        if (request.Src != "auto") body["source_lang"] = request.Src.Split('-')[0].ToUpperInvariant();
        if (request.Context.Count > 0) body["context"] = string.Join("\n", request.Context.Select(c => c.Source));
        if (marked is not null)
        {
            body["tag_handling"] = "xml";
            body["ignore_tags"] = new[] { KeepTag };
        }
        return body;
    }

    /// <summary>DeepL's target code: a region where DeepL needs one (EN-US, PT-BR, ZH-HANS), else the language in capitals.</summary>
    internal static string Target(string tgt) => tgt.ToLowerInvariant() switch
    {
        "en" => "EN-US",
        "pt" => "PT-BR",
        "zh" or "zh-cn" => "ZH-HANS",
        "zh-tw" => "ZH-HANT",
        var t => t.Split('-')[0].ToUpperInvariant(),
    };

    /// <summary>The line as XML text with each keep entry in a tag DeepL leaves untranslated.</summary>
    internal static string Mark(string line, IReadOnlyList<TermMatch> spans)
    {
        var sb = new StringBuilder(line.Length + 16 * spans.Count);
        int last = 0;
        foreach (var span in spans)
        {
            sb.Append(Escape(line[last..span.Index])).Append('<').Append(KeepTag).Append('>').Append(Escape(span.Text)).Append("</").Append(KeepTag).Append('>');
            last = span.Index + span.Length;
        }
        return sb.Append(Escape(line[last..])).ToString();
    }

    internal static string Unmark(string xml) =>
        xml.Replace($"<{KeepTag}>", "").Replace($"</{KeepTag}>", "")
            .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");

    static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
