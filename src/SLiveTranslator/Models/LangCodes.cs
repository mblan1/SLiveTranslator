using System;

namespace SLiveTranslator.Models;

public static class LangCodes
{
    /// <summary>Maps a Windows OCR language tag (e.g. "en-US", "zh-Hans-CN") to a translator source code.</summary>
    public static string FromOcrTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "auto";
        if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            bool traditional = tag.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                               || tag.EndsWith("TW", StringComparison.OrdinalIgnoreCase)
                               || tag.EndsWith("HK", StringComparison.OrdinalIgnoreCase);
            return traditional ? "zh-TW" : "zh-CN";
        }
        return tag.Split('-')[0].ToLowerInvariant();
    }

    public static readonly string[] CommonTargets =
    [
        "en", "vi", "ja", "ko", "zh-CN", "zh-TW", "th", "id", "ms", "fr", "de", "es", "pt", "it", "ru", "ar", "hi", "tr", "pl", "nl", "uk",
    ];
}
