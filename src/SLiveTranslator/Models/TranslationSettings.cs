using System.Text.Json.Serialization;

namespace SLiveTranslator.Models;

/// <summary>Values of <see cref="TranslationSettings.Provider"/>.</summary>
public static class ProviderIds
{
    /// <summary>Google (gtx), then MyMemory; no key.</summary>
    public const string Free = "free";
    public const string Claude = "claude";
    public const string OpenAI = "openai";
    public const string Gemini = "gemini";

    /// <summary>Any OpenAI-compatible server: OpenRouter, DeepSeek, Groq, Mistral, xAI, Ollama, LM Studio.</summary>
    public const string Compat = "compat";
    public const string DeepL = "deepl";
    public const string Azure = "azure";
    public const string GoogleCloud = "googlecloud";
}

public sealed class TranslationSettings : SettingsSection
{
    /// <summary>One of <see cref="ProviderIds"/>; anything unknown runs the free chain.</summary>
    public string Provider { get; set; } = ProviderIds.Free;

    public bool UseContext { get; set; } = true;
    public int ContextLines { get; set; } = 6;
    public string ActiveProfile { get; set; } = "witcher3";

    public FreeProviderSettings Free { get; set; } = new();
    public ClaudeProviderSettings Claude { get; set; } = new();
    public ProviderSettings OpenAI { get; set; } = new();
    public GeminiProviderSettings Gemini { get; set; } = new();
    public CompatProviderSettings Compat { get; set; } = new();
    public ProviderSettings DeepL { get; set; } = new();
    public AzureProviderSettings Azure { get; set; } = new();
    public ProviderSettings GoogleCloud { get; set; } = new();
}

public sealed class FreeProviderSettings : SettingsSection
{
    /// <summary>Optional; raises the MyMemory free limit from 5,000 to 50,000 characters per day.</summary>
    public string MyMemoryEmail { get; set; } = "";
}

/// <summary>A provider that needs an API key. The file holds only the DPAPI blob of the key.</summary>
public class ProviderSettings : SettingsSection
{
    /// <summary>Base64 DPAPI blob from <see cref="SecretStore.Protect"/>; "" when no key is stored.</summary>
    [JsonPropertyOrder(-1)]
    public string ApiKeyProtected { get; set; } = "";

    /// <summary>Model id for LLM providers, engine for MT providers; "" until one is picked after the key check.</summary>
    [JsonPropertyOrder(-1)]
    public string Model { get; set; } = "";

    /// <summary>The decrypted key, never written to the file; "" when none is stored or it cannot be decrypted.</summary>
    [JsonIgnore]
    public string ApiKey
    {
        get => SecretStore.Unprotect(ApiKeyProtected) ?? "";
        set => ApiKeyProtected = SecretStore.Protect(value.Trim());
    }

    /// <summary>A key is stored but this Windows user cannot decrypt it (data copied from another user or PC): ask for it again.</summary>
    [JsonIgnore]
    public bool ApiKeyUnreadable => SecretStore.Unprotect(ApiKeyProtected) is null;
}

public enum LlmEffort { Low, Medium }

public sealed class ClaudeProviderSettings : ProviderSettings
{
    public ClaudeProviderSettings() => Model = "claude-opus-5-5";

    public LlmEffort Effort { get; set; } = LlmEffort.Low;
}

public sealed class GeminiProviderSettings : ProviderSettings
{
    /// <summary>Optional Google Cloud project id.</summary>
    public string ProjectId { get; set; } = "";
}

public sealed class CompatProviderSettings : ProviderSettings
{
    /// <summary>e.g. "https://openrouter.ai/api/v1" or "http://localhost:11434/v1".</summary>
    public string BaseUrl { get; set; } = "";
}

public sealed class AzureProviderSettings : ProviderSettings
{
    /// <summary>Azure resource region sent as Ocp-Apim-Subscription-Region, e.g. "southeastasia".</summary>
    public string Region { get; set; } = "";
}
