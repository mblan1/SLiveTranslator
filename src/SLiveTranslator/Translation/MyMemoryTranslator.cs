using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SLiveTranslator.Translation;

/// <summary>
/// MyMemory public translation API (api.mymemory.translated.net). No key needed.
/// Free limit: 5,000 characters/day, or 50,000/day when an email address is supplied.
/// </summary>
public sealed class MyMemoryTranslator : ITranslator
{
    /// <summary><see cref="TranslateResult.AnsweredBy"/> of a line from MyMemory's machine translation.</summary>
    public const string AnsweredByMachine = "mymemory-mt";

    /// <summary>A line from MyMemory's crowd-sourced corpus (translation memory): often an unrelated film line, never cached.</summary>
    public const string AnsweredByCorpus = "mymemory-tm";

    const string Endpoint = "https://api.mymemory.translated.net/get";
    const int MaxBytesPerRequest = 480; // the API rejects queries over 500 bytes
    static readonly TimeSpan QuotaPause = TimeSpan.FromHours(1);
    static readonly HttpClient DefaultHttp = HttpClients.Create(TimeSpan.FromSeconds(20), "SLiveTranslator/1.0");

    readonly HttpClient _http;
    readonly string? _email;
    readonly DailyCharCounter? _usage;
    DateTime _blockedUntilUtc = DateTime.MinValue;

    /// <param name="usage">Counts the characters sent today; the instances made for other emails share it.</param>
    public MyMemoryTranslator(string? email = null, HttpClient? http = null, DailyCharCounter? usage = null)
    {
        _email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        _http = http ?? DefaultHttp;
        _usage = usage;
    }

    public string Name => "mymemory";

    /// <summary>Characters sent today, as counted on this PC; 0 without a counter.</summary>
    public long CharsToday => _usage?.Today ?? 0;

    /// <summary>The free characters per day: 50,000 with an email, 5,000 without.</summary>
    public int DailyLimit => _email is null ? 5_000 : 50_000;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    /// <summary>Each line is answered by the machine translation or by the corpus; <see cref="TranslateResult.LineAnsweredBy"/> tells which.</summary>
    public async Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
    {
        var texts = request.Lines;
        var results = new string[texts.Count];
        var answeredBy = new string[texts.Count];
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(texts.Select(async (text, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var parts = new List<string>();
                bool machine = true;
                foreach (var piece in SplitByBytes(text, MaxBytesPerRequest))
                {
                    var (translated, byMachine) = await RequestAsync(piece, request.Src, request.Tgt, ct);
                    parts.Add(translated);
                    machine &= byMachine;
                }
                results[i] = string.Join(" ", parts);
                answeredBy[i] = machine ? AnsweredByMachine : AnsweredByCorpus;
            }
            finally
            {
                gate.Release();
            }
        }));
        string all = answeredBy.All(a => a == AnsweredByMachine) ? AnsweredByMachine : AnsweredByCorpus;
        return new TranslateResult(results, null, all) { LineAnsweredBy = answeredBy };
    }

    /// <returns>The translation, and whether it is MyMemory's machine translation rather than a corpus match.</returns>
    async Task<(string Text, bool Machine)> RequestAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        if (DateTime.UtcNow < _blockedUntilUtc)
            throw new TranslatorUnavailableException(QuotaMessage, _blockedUntilUtc - DateTime.UtcNow);

        string source = sourceLang == "auto" ? "Autodetect" : sourceLang;
        var url = Endpoint + "?q=" + Uri.EscapeDataString(text)
                  + "&langpair=" + Uri.EscapeDataString(source + "|" + targetLang)
                  + (_email is null ? "" : "&de=" + Uri.EscapeDataString(_email));
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw Blocked();
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        int status = root.TryGetProperty("responseStatus", out var s)
            ? s.ValueKind == JsonValueKind.Number ? s.GetInt32() : int.TryParse(s.GetString(), out var n) ? n : 0
            : 0;
        bool quotaFinished = root.TryGetProperty("quotaFinished", out var q) && q.ValueKind == JsonValueKind.True;
        string translated = root.TryGetProperty("responseData", out var data) && data.TryGetProperty("translatedText", out var t)
            ? t.GetString() ?? ""
            : "";

        if (status == 429 || quotaFinished || translated.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            throw Blocked();
        if (status != 200)
        {
            string details = root.TryGetProperty("responseDetails", out var d) ? d.GetString() ?? "" : "";
            throw new InvalidOperationException("MyMemory: " + (details.Length > 0 ? details : "HTTP status " + status));
        }
        _usage?.Add(text.Length);
        if (MachineTranslation(root) is { } machine) return (WebUtility.HtmlDecode(machine), true);
        translated = WebUtility.HtmlDecode(translated);
        // A subtitle corpus starts every line of a dialog with "- ": a match that adds one is another film's line. It
        // is rejected and the source comes back untranslated (never cached), so a later try can still translate it.
        return AddsDialogDash(text, translated) ? (text, false) : (translated, false);
    }

    /// <summary>The translation starts with a dialog dash ("- ", "– ") that the source does not have.</summary>
    internal static bool AddsDialogDash(string source, string translated)
    {
        static bool Dashed(string s)
        {
            s = s.TrimStart();
            return s.Length > 1 && s[0] is '-' or '–' or '—' && char.IsWhiteSpace(s[1]);
        }
        static bool StartsWithDash(string s) => s.TrimStart() is { Length: > 0 } t && t[0] is '-' or '–' or '—';
        return Dashed(translated) && !StartsWithDash(source);
    }

    /// <summary>
    /// MyMemory ranks crowd-sourced "Public_Corpora" matches above its own machine translation; those are often
    /// unrelated movie lines ("Enough. Get down." came back as a love declaration). Prefer the machine translation.
    /// </summary>
    static string? MachineTranslation(JsonElement root)
    {
        if (!root.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array) return null;
        foreach (var match in matches.EnumerateArray())
        {
            bool machine = match.TryGetProperty("created-by", out var by) && by.ValueKind == JsonValueKind.String && by.GetString() == "MT!";
            if (machine && match.TryGetProperty("translation", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                return t.GetString();
        }
        return null;
    }

    Exception Blocked()
    {
        _blockedUntilUtc = DateTime.UtcNow + QuotaPause;
        return new TranslatorUnavailableException(QuotaMessage, QuotaPause);
    }

    string QuotaMessage => _email is null
        ? "MyMemory free daily limit reached (5,000 characters). Add an email in Settings to raise it to 50,000, or use Claude."
        : "MyMemory free daily limit reached (50,000 characters). Try again tomorrow or use Claude.";

    /// <summary>Splits text at spaces or CJK sentence punctuation into pieces of at most maxBytes UTF-8 bytes.</summary>
    internal static IEnumerable<string> SplitByBytes(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var token in Regex.Split(text, @"(?<=[\s。！？、，])").Where(x => x.Length > 0))
        {
            foreach (var piece in HardSplit(token, maxBytes))
            {
                if (current.Length > 0 && Encoding.UTF8.GetByteCount(current + piece) > maxBytes)
                {
                    yield return current.ToString().Trim();
                    current.Clear();
                }
                current.Append(piece);
            }
        }
        if (current.ToString().Trim().Length > 0) yield return current.ToString().Trim();
    }

    static IEnumerable<string> HardSplit(string token, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(token) <= maxBytes)
        {
            yield return token;
            yield break;
        }
        int chunk = Math.Max(1, maxBytes / 4); // worst case 4 bytes per char
        for (int i = 0; i < token.Length; i += chunk)
            yield return token.Substring(i, Math.Min(chunk, token.Length - i));
    }
}
