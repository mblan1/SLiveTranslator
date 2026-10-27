using System;
using System.Collections.Generic;
using System.Linq;

namespace SLiveTranslator.Translation.Providers;

/// <param name="Local">A server on this PC; it needs no key, and its loaded model is the fast one.</param>
public sealed record CompatPreset(string Id, string Name, string BaseUrl, bool NeedsKey, bool Local = false);

/// <summary>The OpenAI-compatible servers the settings page offers; the base URL fills in from the one picked.</summary>
public static class CompatPresets
{
    public static IReadOnlyList<CompatPreset> All { get; } =
    [
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", NeedsKey: true),
        new("deepseek", "DeepSeek", "https://api.deepseek.com", NeedsKey: true), // no /v1
        new("groq", "Groq", "https://api.groq.com/openai/v1", NeedsKey: true),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", NeedsKey: true),
        new("xai", "xAI", "https://api.x.ai/v1", NeedsKey: true),
        new("ollama", "Ollama", "http://localhost:11434/v1", NeedsKey: false, Local: true),
        new("lmstudio", "LM Studio", "http://localhost:1234/v1", NeedsKey: false, Local: true),
    ];

    /// <summary>The preset whose server the address names (the host; for a local server, the port on this PC); null for any other.</summary>
    public static CompatPreset? Of(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return null;
        return All.FirstOrDefault(p =>
        {
            var known = new Uri(p.BaseUrl);
            return p.Local ? uri.IsLoopback && uri.Port == known.Port : uri.Host.Equals(known.Host, StringComparison.OrdinalIgnoreCase);
        });
    }
}
