using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation.Providers;

/// <summary>
/// Google Cloud Translation, Basic (v2), with an API key. There is no model list or usage endpoint: the check
/// translates one character. Names are kept by the <see cref="ProfileTranslator"/>'s placeholders.
/// </summary>
public sealed class GoogleCloudProvider(HttpClient? http = null, TimeProvider? time = null) : ITranslationProvider
{
    internal const string Endpoint = "https://translation.googleapis.com/language/translate/v2";

    /// <summary>Neural machine translation, the v2 default.</summary>
    public const string Nmt = "nmt";

    /// <summary>A per-minute quota of this key's project (enforced per client IP); it passes quickly.</summary>
    static readonly TimeSpan UserRatePause = TimeSpan.FromSeconds(10);

    static readonly TimeZoneInfo Pacific = FindPacific();

    readonly HttpClient _http = http ?? ProviderHttp.Shared;
    readonly TimeProvider _time = time ?? TimeProvider.System;

    internal static IReadOnlyList<ModelOption> Engines { get; } = [new(Nmt, "Neural (NMT)", ModelTags.Recommended | ModelTags.Fastest)];

    public string Id => ProviderIds.GoogleCloud;
    public string DisplayName => "Google Cloud Translation";
    public ProviderKind Kind => ProviderKind.MT;
    public IReadOnlyList<ProviderFieldSpec> Fields { get; } = [new(ProviderField.ApiKey, Required: true)];
    public ProfileHandling Profile => ProfileHandling.Placeholders;
    public string DefaultModel => Nmt;

    public ITranslator Create(ProviderConfig config) => new GoogleCloudTranslator(config, _http, _time);

    public async Task<ProviderCheck> CheckAsync(ProviderCredentials credentials, CancellationToken ct)
    {
        string key = credentials.ApiKey.Trim();
        if (key.Length == 0) return ProviderCheck.Fail(CheckStatus.InvalidKey, "No API key.");
        try
        {
            var response = await ProviderHttp.SendAsync(_http, Request(key, GoogleCloudTranslator.Body(["a"], "en", "vi", Nmt)), ProviderHttp.CheckTimeout, DisplayName, ct);
            if (!response.Ok) throw Error(response, _time.GetUtcNow());
            return new ProviderCheck(CheckStatus.Ok, Engines, null, "");
        }
        catch (ProviderException ex)
        {
            return ProviderCheck.Fail(ex.Status, ex.Message);
        }
    }

    /// <summary>The key goes in the query, as v2 takes it; the URL is never logged.</summary>
    internal static HttpRequestMessage Request(string key, object body) =>
        new(HttpMethod.Post, Endpoint + "?key=" + Uri.EscapeDataString(key.Trim())) { Content = ProviderHttp.Json(body) };

    /// <summary>
    /// Google's error body: an invalid key is a 400 (keyInvalid / API_KEY_INVALID). Of the 403s, the daily limit stops
    /// the key until midnight Pacific time, the per-user rate passes in seconds, and any other (API not enabled, key
    /// blocked, billing) is a permission problem.
    /// </summary>
    internal static ProviderException Error(ProviderHttp.Response response, DateTimeOffset now)
    {
        var error = GoogleApiError.Read(response);
        string summary = error.Summary("Google Cloud Translation", response);
        bool Says(string reason, string text) => error.Reason.Equals(reason, StringComparison.OrdinalIgnoreCase) || error.Message.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Says("keyInvalid", "API key not valid") || error.Reason == "API_KEY_INVALID")
            return new ProviderException(CheckStatus.InvalidKey, summary);
        if (Says("dailyLimitExceeded", "Daily Limit Exceeded"))
            return new ProviderException(CheckStatus.RateLimited, summary, UntilPacificMidnight(now));
        if (Says("userRateLimitExceeded", "User Rate Limit Exceeded") || Says("rateLimitExceeded", "Rate Limit Exceeded"))
            return new ProviderException(CheckStatus.RateLimited, summary, error.RetryAfter ?? UserRatePause);
        return new ProviderException(ProviderHttp.StatusOf(response.Status), summary, error.RetryAfter);
    }

    /// <summary>When Google's daily quotas start again.</summary>
    internal static TimeSpan UntilPacificMidnight(DateTimeOffset now)
    {
        var tomorrow = TimeZoneInfo.ConvertTime(now, Pacific).Date.AddDays(1);
        return new DateTimeOffset(tomorrow, Pacific.GetUtcOffset(tomorrow)) - now;
    }

    static TimeZoneInfo FindPacific()
    {
        foreach (string id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // try the other name
            }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Pacific", TimeSpan.FromHours(-8), "Pacific", "Pacific");
    }
}

/// <summary>Translation with Google Cloud Translation v2: up to 128 lines a request, plain text, the source language given.</summary>
public sealed class GoogleCloudTranslator : ITranslator
{
    const int MaxStrings = 128;

    readonly HttpClient _http;
    readonly string _key;
    readonly string _engine;
    readonly TimeProvider _time;
    readonly Func<int, TimeSpan> _timeout;

    /// <param name="timeout">Replaces <see cref="ProviderHttp.Deadline"/> (tests).</param>
    internal GoogleCloudTranslator(ProviderConfig config, HttpClient http, TimeProvider time, Func<int, TimeSpan>? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(config.Credentials.ApiKey)) throw new ArgumentException("A Google Cloud API key is needed.", nameof(config));
        _http = http;
        _key = config.Credentials.ApiKey.Trim();
        _engine = string.IsNullOrWhiteSpace(config.Model) ? GoogleCloudProvider.Nmt : config.Model.Trim();
        _time = time;
        _timeout = timeout ?? ProviderHttp.Deadline;
    }

    public string Name => ProviderKeys.For(ProviderIds.GoogleCloud, _engine);

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct) =>
        ProviderHttp.WithDeadline("Google Cloud Translation", _timeout(request.Lines.Count), ct, async deadline =>
        {
            var texts = new List<string>(request.Lines.Count);
            foreach (var chunk in request.Lines.Chunk(MaxStrings))
            {
                var response = await ProviderHttp.SendAsync(_http, GoogleCloudProvider.Request(_key, Body(chunk, request.Src, request.Tgt, _engine)),
                    Timeout.InfiniteTimeSpan, "Google Cloud Translation", deadline);
                if (!response.Ok) throw GoogleCloudProvider.Error(response, _time.GetUtcNow());
                using var doc = JsonRead.Parse(response.Body);
                var answers = doc?.RootElement.Get("data")?.Items("translations").Select(t => t.Str("translatedText") ?? "").ToList();
                if (answers?.Count != chunk.Length)
                    throw new TranslatorUnavailableException("Google Cloud Translation returned an unexpected response: " + ProviderHttp.Shorten(response.Body));
                texts.AddRange(answers);
            }
            return new TranslateResult(texts, null, Name);
        });

    /// <summary>format=text (the default, html, would escape the answer); the source is always given (v2 fails to detect short lines).</summary>
    internal static Dictionary<string, object> Body(IReadOnlyList<string> lines, string src, string tgt, string engine)
    {
        var body = new Dictionary<string, object> { ["q"] = lines, ["target"] = tgt, ["format"] = "text", ["model"] = engine };
        if (src != "auto") body["source"] = src;
        return body;
    }
}
