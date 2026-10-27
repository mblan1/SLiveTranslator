using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation.Providers;

public enum ProviderKind { Free, MT, LLM }

/// <summary>A setting the provider needs besides its model.</summary>
public enum ProviderField { ApiKey, Region, BaseUrl, ProjectId }

public sealed record ProviderFieldSpec(ProviderField Field, bool Required);

/// <summary>How the game profile reaches the engine.</summary>
public enum ProfileHandling
{
    /// <summary>The whole profile goes into the prompt (LLMs).</summary>
    Prompt,

    /// <summary>The engine keeps the profile's names with markup of its own (DeepL ignore tags, Azure dictionary); the fix rules still apply.</summary>
    Markup,

    /// <summary>The names go out as placeholders and the fix rules repair the answer (<see cref="ProfileTranslator"/>).</summary>
    Placeholders,
}

public enum CheckStatus { Ok, InvalidKey, NoPermission, NoCredit, RateLimited, Network, Timeout, Failed }

[Flags]
public enum ModelTags { None = 0, Recommended = 1, Balanced = 2, Fastest = 4, Deprecated = 8 }

/// <summary>A model (LLM) or engine (MT) the user can pick after a key check.</summary>
/// <param name="CostHint">Price per million tokens, when known ("$4 / $20 per 1M tokens").</param>
public sealed record ModelOption(string Id, string Label, ModelTags Tags = ModelTags.None, string? CostHint = null);

/// <param name="Limit">Null when the account has no limit.</param>
/// <param name="Unit">"characters" or "USD".</param>
public sealed record ProviderUsage(double Used, double? Limit, string Unit);

/// <summary>What a key check found.</summary>
/// <param name="Options">What the key can use; filled whenever the list was read, also when a later test translation found no credit.</param>
/// <param name="Detail">The provider's own message, for the UI to show under its localized summary; "" when there is nothing to add.</param>
public sealed record ProviderCheck(CheckStatus Status, IReadOnlyList<ModelOption> Options, ProviderUsage? Usage, string Detail)
{
    public static ProviderCheck Fail(CheckStatus status, string detail) => new(status, [], null, detail);
}

/// <summary>What a key check or a translator needs to reach the provider. Its text form never shows the key.</summary>
public sealed record ProviderCredentials(string ApiKey, string Region = "", string BaseUrl = "", string ProjectId = "")
{
    public override string ToString() => $"{nameof(ProviderCredentials)} {{ Region = {Region}, BaseUrl = {BaseUrl}, ProjectId = {ProjectId} }}";
}

/// <param name="Model">The model id (LLM) or engine (MT) to translate with.</param>
public sealed record ProviderConfig(ProviderCredentials Credentials, string Model, LlmEffort Effort = LlmEffort.Low)
{
    /// <summary>The configuration a provider's settings section holds; <paramref name="defaultModel"/> when no model is saved.</summary>
    public static ProviderConfig From(ProviderSettings settings, string defaultModel = "") => new(
        new ProviderCredentials(settings.ApiKey,
            (settings as AzureProviderSettings)?.Region.Trim() ?? "",
            (settings as CompatProviderSettings)?.BaseUrl.Trim() ?? "",
            (settings as GeminiProviderSettings)?.ProjectId.Trim() ?? ""),
        settings.Model.Trim() is { Length: > 0 } model ? model : defaultModel,
        (settings as ClaudeProviderSettings)?.Effort ?? LlmEffort.Low);
}

/// <summary>
/// A translation service the user can select: what the settings page asks for, the key check that fills the model
/// list, and the translator for a configuration.
/// </summary>
public interface ITranslationProvider
{
    /// <summary>One of <see cref="ProviderIds"/>.</summary>
    string Id { get; }

    string DisplayName { get; }
    ProviderKind Kind { get; }
    IReadOnlyList<ProviderFieldSpec> Fields { get; }
    ProfileHandling Profile { get; }

    /// <summary>The engine used while none is picked; "" when a model has to be picked after a key check (LLMs).</summary>
    string DefaultModel { get; }

    /// <summary>Whether these credentials cannot work without an API key; by default, whether <see cref="Fields"/> requires one.</summary>
    bool NeedsKey(ProviderCredentials credentials) => Fields.Any(f => f is { Field: ProviderField.ApiKey, Required: true });

    /// <summary>Checks the key and lists what it can use. Never throws for a provider problem; caller cancellation comes through.</summary>
    Task<ProviderCheck> CheckAsync(ProviderCredentials credentials, CancellationToken ct);

    ITranslator Create(ProviderConfig config);
}
