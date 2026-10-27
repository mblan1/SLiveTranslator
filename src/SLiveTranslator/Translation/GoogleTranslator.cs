using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SLiveTranslator.Translation;

/// <summary>
/// Google Translate through the unofficial free web endpoint. No key needed, but Google blocks networks that send
/// too many requests, so all strings of one screen go out in a single request and a block pauses the service.
/// </summary>
public sealed class GoogleTranslator : ITranslator
{
    const string Endpoint = "https://translate.googleapis.com/translate_a/single";
    const int MaxCharsPerRequest = 4000;
    static readonly HttpClient DefaultHttp = CreateClient();

    readonly HttpClient _http;
    DateTime _blockedUntilUtc = DateTime.MinValue;
    int _blocksInARow;

    /// <summary>10, 30, then 60 minutes: each retry on a blocked network costs about a second of delay.</summary>
    TimeSpan BlockPause => TimeSpan.FromMinutes(_blocksInARow switch { <= 1 => 10, 2 => 30, _ => 60 });

    public GoogleTranslator(HttpClient? http = null) => _http = http ?? DefaultHttp;

    static HttpClient CreateClient()
    {
        // Don't follow redirects: a blocked network is redirected to a captcha page, and following it costs another round trip.
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SLiveTranslator/1.0");
        return client;
    }

    public string Name => "google";

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        var results = new string[texts.Count];
        foreach (var chunk in Chunks(texts))
        {
            // One line per string; the endpoint keeps line breaks, so the answer splits back into the same items.
            var joined = string.Join("\n", chunk.Select(i => OneLine(texts[i])));
            var lines = (await RequestAsync(joined, sourceLang, targetLang, ct)).Trim('\n', ' ').Split('\n');
            if (lines.Length == chunk.Count)
            {
                for (int k = 0; k < chunk.Count; k++) results[chunk[k]] = lines[k].Trim();
            }
            else
            {
                foreach (var i in chunk) results[i] = (await RequestAsync(OneLine(texts[i]), sourceLang, targetLang, ct)).Trim();
            }
        }
        return results;
    }

    static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    static IEnumerable<List<int>> Chunks(IReadOnlyList<string> texts)
    {
        var chunk = new List<int>();
        int chars = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            if (chunk.Count > 0 && chars + texts[i].Length > MaxCharsPerRequest)
            {
                yield return chunk;
                chunk = new List<int>();
                chars = 0;
            }
            chunk.Add(i);
            chars += texts[i].Length + 1;
        }
        if (chunk.Count > 0) yield return chunk;
    }

    async Task<string> RequestAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        if (DateTime.UtcNow < _blockedUntilUtc)
            throw new TranslatorUnavailableException(BlockedMessage);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client"] = "gtx",
            ["dt"] = "t",
            ["sl"] = sourceLang,
            ["tl"] = targetLang,
            ["q"] = text,
        });
        using var response = await _http.PostAsync(Endpoint, form, ct);

        // A blocked network gets HTTP 429, or a redirect to google.com/sorry (a captcha page).
        bool sorryPage = response.Headers.Location?.OriginalString.Contains("/sorry", StringComparison.OrdinalIgnoreCase) == true
                         || response.RequestMessage?.RequestUri?.AbsolutePath.Contains("/sorry", StringComparison.OrdinalIgnoreCase) == true;
        if (response.StatusCode == HttpStatusCode.TooManyRequests || sorryPage)
        {
            _blocksInARow++;
            _blockedUntilUtc = DateTime.UtcNow + BlockPause;
            throw new TranslatorUnavailableException(BlockedMessage);
        }
        response.EnsureSuccessStatusCode();
        _blocksInARow = 0;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var sb = new StringBuilder();
        if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement[0].ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in doc.RootElement[0].EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 && segment[0].ValueKind == JsonValueKind.String)
                    sb.Append(segment[0].GetString());
            }
        }
        return sb.ToString();
    }

    string BlockedMessage =>
        $"Google Translate is blocking requests from this network (HTTP 429). It is paused for {BlockPause.TotalMinutes:0} minutes.";
}
