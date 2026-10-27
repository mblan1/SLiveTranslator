using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SLiveTranslator.Translation;

/// <summary>
/// Applies the game profile for an engine that takes no instructions: keep entries go out as placeholders and come
/// back as the exact source text, and the profile's fix rules repair the answer. A line whose placeholders did not
/// survive is translated again without them, so a name is never lost or doubled. A line with nothing to translate
/// besides its kept names ("Vesemir!", "Alt") is answered here, without a request. Engines that take instructions
/// (LLMs) get the profile in their <see cref="TranslateRequest"/> instead and are not wrapped. Such engines fix no OCR
/// errors, so the result has no <see cref="TranslateResult.FixedSources"/>.
/// </summary>
public sealed class ProfileTranslator(ITranslator inner) : ITranslator
{
    /// <summary><see cref="TranslateResult.AnsweredBy"/> of a line that is its own translation: kept names, digits, punctuation.</summary>
    public const string AnsweredByProfile = "profile";

    public string Name => inner.Name;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public async Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
    {
        if (request.Profile is not { } profile) return await inner.TranslateAsync(request, ct);
        var matcher = ProfileMatcher.For(profile);
        var lines = request.Lines;
        var shielded = lines.Select(l => Placeholders.Protect(l, matcher.KeepSpans(l))).ToList();
        var texts = new string[lines.Count];
        var answeredBy = new string[lines.Count];
        var sent = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (Placeholders.HasWords(shielded[i])) sent.Add(i);
            else (texts[i], answeredBy[i]) = (lines[i], AnsweredByProfile);
        }
        if (sent.Count == 0) return new TranslateResult(texts, null, AnsweredByProfile) { LineAnsweredBy = answeredBy };

        // The inner engine gets no profile: it is applied here.
        var first = await inner.TranslateAsync(request with { Lines = sent.Select(i => shielded[i].Text).ToList(), Profile = null }, ct);
        var lost = new List<int>();
        for (int j = 0; j < sent.Count; j++)
        {
            int i = sent[j];
            answeredBy[i] = first.AnsweredByAt(j);
            if (Placeholders.Restore(first.Texts[j], shielded[i]) is { } restored) texts[i] = restored;
            else lost.Add(i);
        }
        if (lost.Count > 0)
        {
            var again = await inner.TranslateAsync(request with { Lines = lost.Select(i => lines[i]).ToList(), Profile = null }, ct);
            for (int j = 0; j < lost.Count; j++)
            {
                texts[lost[j]] = again.Texts[j];
                answeredBy[lost[j]] = again.AnsweredByAt(j);
            }
        }

        foreach (int i in sent) texts[i] = matcher.FixRules.Apply(lines[i], texts[i]);
        return new TranslateResult(texts, null, first.AnsweredBy) { Provider = first.Provider, LineAnsweredBy = answeredBy };
    }

    /// <summary>Only the keep entries in the line and the fix rules for it change what comes back.</summary>
    public string ProfileStamp(GameProfile? profile, string line) => profile is null ? "" : ProfileMatcher.For(profile).MachineStamp(line);
}
