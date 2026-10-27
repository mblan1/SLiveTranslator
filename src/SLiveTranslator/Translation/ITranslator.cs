using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SLiveTranslator.Translation;

public interface ITranslator
{
    /// <summary>The provider key: part of every cache key, so it names the engine and, for keyed providers, the model.</summary>
    string Name { get; }

    /// <param name="sourceLang">Google-style code ("en", "ja", "zh-CN") or "auto".</param>
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct);

    /// <summary>
    /// Translates with the conversation so far and the game profile. Engines that cannot use either keep this default,
    /// which translates the lines alone.
    /// </summary>
    async Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct) =>
        new(await TranslateAsync(request.Lines, request.Src, request.Tgt, ct), null, Name);

    /// <summary>
    /// What of the game profile this translator's answer for the line depends on, as a short stamp ("" for nothing).
    /// A cached answer made under another stamp is translated again, so a profile edit re-translates only the lines it
    /// can change. By default the whole profile reaches the engine, as an LLM's prompt does; a translator that applies
    /// less of it says so (<see cref="ProfileTranslator"/>).
    /// </summary>
    string ProfileStamp(GameProfile? profile, string line) => profile is null ? "" : ProfileMatcher.For(profile).PromptStamp(line);
}
