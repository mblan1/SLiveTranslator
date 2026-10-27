using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SLiveTranslator.Translation;

public interface ITranslator
{
    string Name { get; }

    /// <param name="sourceLang">Google-style code ("en", "ja", "zh-CN") or "auto".</param>
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct);
}
