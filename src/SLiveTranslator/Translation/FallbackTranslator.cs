using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation;

/// <summary>Tries each translator in order and moves on when one is blocked, over quota, or unreachable.</summary>
public sealed class FallbackTranslator : ITranslator
{
    readonly IReadOnlyList<ITranslator> _chain;

    public FallbackTranslator(string name, params ITranslator[] chain)
    {
        Name = name;
        _chain = chain;
    }

    public string Name { get; }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var translator in _chain)
        {
            try
            {
                return await translator.TranslateAsync(texts, sourceLang, targetLang, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is TranslatorUnavailableException or HttpRequestException or TaskCanceledException)
            {
                Log.Write($"{translator.Name} unavailable: {ex.Message}");
                failures.Add(ex.Message);
            }
        }
        throw new TranslatorUnavailableException(
            "No free translation service is working right now. " + string.Join(" ", failures.Distinct()));
    }
}
