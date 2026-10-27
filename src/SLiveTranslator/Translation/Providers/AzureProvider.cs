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
/// Microsoft Azure Translator, Text API v3.0. The region is needed for a regional or multi-service resource and may
/// be left empty for a global one. There is no model list or usage endpoint: the check translates one character.
/// </summary>
public sealed class AzureProvider(HttpClient? http = null) : ITranslationProvider
{
    internal const string Endpoint = "https://api.cognitive.microsofttranslator.com/translate";

    /// <summary>The standard neural engine (v3.0's default category).</summary>
    public const string General = "general";

    /// <summary>408001: the translation system is being prepared; it asks to retry in a few minutes.</summary>
    static readonly TimeSpan PreparingPause = TimeSpan.FromMinutes(2);

    readonly HttpClient _http = http ?? ProviderHttp.Shared;

    internal static IReadOnlyList<ModelOption> Engines { get; } = [new(General, "General (neural)", ModelTags.Recommended | ModelTags.Fastest)];

    public string Id => ProviderIds.Azure;
    public string DisplayName => "Azure Translator";
    public ProviderKind Kind => ProviderKind.MT;
    public IReadOnlyList<ProviderFieldSpec> Fields { get; } = [new(ProviderField.ApiKey, Required: true), new(ProviderField.Region, Required: false)];
    public ProfileHandling Profile => ProfileHandling.Markup;
    public string DefaultModel => General;

    public ITranslator Create(ProviderConfig config) => new AzureTranslator(config, _http);

    public async Task<ProviderCheck> CheckAsync(ProviderCredentials credentials, CancellationToken ct)
    {
        if (credentials.ApiKey.Trim().Length == 0) return ProviderCheck.Fail(CheckStatus.InvalidKey, "No API key.");
        try
        {
            var response = await ProviderHttp.SendAsync(_http, Request(credentials, ["a"], "en", "vi"), ProviderHttp.CheckTimeout, DisplayName, ct);
            if (!response.Ok) throw Error(response);
            return new ProviderCheck(CheckStatus.Ok, Engines, null, "");
        }
        catch (ProviderException ex)
        {
            return ProviderCheck.Fail(ex.Status, ex.Message);
        }
    }

    internal static HttpRequestMessage Request(ProviderCredentials credentials, IReadOnlyList<string> texts, string src, string tgt)
    {
        string url = Endpoint + "?api-version=3.0" + (src == "auto" ? "" : "&from=" + Uri.EscapeDataString(src)) + "&to=" + Uri.EscapeDataString(tgt);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = ProviderHttp.Json(texts.Select(t => new { Text = t }).ToList()) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=UTF-8");
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", credentials.ApiKey.Trim());
        if (credentials.Region.Trim().Length > 0) request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", credentials.Region.Trim());
        return request;
    }

    /// <summary>
    /// Azure's error {"error":{"code":401000,"message"}}: 401xxx is the key (or a Speech key, or the wrong region),
    /// 403001 the free quota, other 403xxx a permission, 429xxx a rate limit, 408001 a system still being prepared.
    /// </summary>
    internal static ProviderException Error(ProviderHttp.Response response)
    {
        using var doc = JsonRead.Parse(response.Body);
        var error = doc?.RootElement.Get("error");
        int code = (int)(error?.Num("code") ?? 0);
        var status = code switch
        {
            403001 => CheckStatus.NoCredit,
            >= 401000 and < 402000 => CheckStatus.InvalidKey,
            >= 403000 and < 404000 => CheckStatus.NoPermission,
            >= 429000 and < 430000 => CheckStatus.RateLimited,
            _ => ProviderHttp.StatusOf(response.Status),
        };
        return new ProviderException(status, ProviderHttp.Summary("Azure Translator", response.Status, code > 0 ? code.ToString() : "", error?.Str("message") ?? ""),
            code == 408001 ? PreparingPause : response.RetryAfter);
    }
}

/// <summary>
/// Translation with Azure Translator v3.0: all lines in one request, the profile's keep entries marked with
/// &lt;mstrans:dictionary&gt; so they come back as written (it needs a known source language). There is no context.
/// </summary>
public sealed class AzureTranslator : ITranslator
{
    readonly HttpClient _http;
    readonly ProviderCredentials _credentials;
    readonly string _engine;
    readonly Func<int, TimeSpan> _timeout;

    /// <param name="timeout">Replaces <see cref="ProviderHttp.Deadline"/> (tests).</param>
    internal AzureTranslator(ProviderConfig config, HttpClient http, Func<int, TimeSpan>? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(config.Credentials.ApiKey)) throw new ArgumentException("An Azure Translator key is needed.", nameof(config));
        _http = http;
        _credentials = config.Credentials;
        _engine = string.IsNullOrWhiteSpace(config.Model) ? AzureProvider.General : config.Model.Trim();
        _timeout = timeout ?? ProviderHttp.Deadline;
    }

    public string Name => ProviderKeys.For(ProviderIds.Azure, _engine);

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct) =>
        ProviderHttp.WithDeadline("Azure Translator", _timeout(request.Lines.Count), ct, async deadline =>
        {
            var matcher = request.Profile is { } profile && request.Src != "auto" ? ProfileMatcher.For(profile) : null;
            var texts = request.Lines.Select(l => matcher is null ? l : Mark(l, matcher.KeepSpans(l))).ToList();
            var response = await ProviderHttp.SendAsync(_http, AzureProvider.Request(_credentials, texts, request.Src, request.Tgt), Timeout.InfiniteTimeSpan, "Azure Translator", deadline);
            if (!response.Ok) throw AzureProvider.Error(response);
            using var doc = JsonRead.Parse(response.Body);
            var answers = doc?.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(r => r.Items("translations").FirstOrDefault().Str("text") ?? "").ToList()
                : [];
            if (answers.Count != request.Lines.Count) throw new TranslatorUnavailableException("Azure Translator returned an unexpected response: " + ProviderHttp.Shorten(response.Body));
            return new TranslateResult(answers, null, Name);
        });

    /// <summary>The line with each keep entry as a dictionary entry that translates to itself.</summary>
    internal static string Mark(string line, IReadOnlyList<TermMatch> spans)
    {
        if (spans.Count == 0) return line;
        var sb = new StringBuilder(line.Length + 64 * spans.Count);
        int last = 0;
        foreach (var span in spans)
        {
            sb.Append(line, last, span.Index - last)
                .Append("<mstrans:dictionary translation=\"").Append(span.Text.Replace("\"", "&quot;")).Append("\">")
                .Append(span.Text).Append("</mstrans:dictionary>");
            last = span.Index + span.Length;
        }
        return sb.Append(line, last, line.Length - last).ToString();
    }
}
