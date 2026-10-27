namespace SLiveTranslator.Translation;

/// <summary>
/// Names of the cache buckets. A keyed provider's key holds its model or engine ("claude/claude-opus-5-5",
/// "deepl/latency_optimized"), because another model answers differently; the free services share one key.
/// </summary>
public static class ProviderKeys
{
    /// <summary>Google (gtx), then MyMemory. Entries made before MyMemory preferred machine translation were "free".</summary>
    public const string Free = "free-mt";

    /// <summary>Translations the user fixed by hand; they answer for every provider.</summary>
    public const string User = "user";

    public static string For(string providerId, string engine) => $"{providerId}/{engine}";

    /// <summary>A key no provider asks for any more: one from before keys named the model ("free", "claude").</summary>
    public static bool IsOrphan(string key) => key != Free && key != User && !key.Contains('/');
}
