using System;
using System.Collections.Generic;
using System.Linq;
using SLiveTranslator.Models;
using SLiveTranslator.Translation.Providers;

namespace SLiveTranslator.Translation;

/// <summary>A provider the user can select, and how its translator is made from the settings.</summary>
/// <param name="Id">One of <see cref="ProviderIds"/>.</param>
/// <param name="TakesInstructions">An LLM: the game profile goes into its prompt. Any other engine gets the profile
/// applied by a <see cref="ProfileTranslator"/>.</param>
/// <param name="KeySettings">The settings that hold its API key; null for a provider without a key.</param>
/// <param name="Create">Called only when <see cref="Unusable"/> found nothing missing.</param>
public sealed record ProviderRegistration(string Id, string DisplayName, bool TakesInstructions,
    Func<TranslationSettings, ProviderSettings?> KeySettings, Func<TranslationSettings, ITranslator> Create)
{
    /// <summary>The provider behind the registration, for the settings page: its fields, key check and model list. Null for one registered by hand.</summary>
    public ITranslationProvider? Provider { get; init; }

    /// <summary>The engine keeps the profile's names with markup of its own: the <see cref="ProfileTranslator"/> passes the profile on instead of placeholders.</summary>
    public bool MarksKeepEntries { get; init; }

    /// <summary>The registration of a provider whose settings are <paramref name="settings"/>.</summary>
    public static ProviderRegistration For(ITranslationProvider provider, Func<TranslationSettings, ProviderSettings> settings) =>
        new(provider.Id, provider.DisplayName, provider.Profile == ProfileHandling.Prompt, settings,
            s => provider.Create(ProviderConfig.From(settings(s), provider.DefaultModel)))
        {
            Provider = provider,
            MarksKeepEntries = provider.Profile == ProfileHandling.Markup,
        };

    /// <summary>
    /// Why the provider cannot run with these settings, as a notice for the user; null when it can. A key that is
    /// missing or cannot be decrypted, a missing server address, or no model picked (an LLM's model box stays empty
    /// until a key check) all leave the free services translating.
    /// </summary>
    public string? Unusable(TranslationSettings settings)
    {
        if (KeySettings(settings) is not { } section) return null;
        // An undecryptable key must not reach an SDK as "": it may fall back to a key in the environment, which may
        // bill someone else.
        if (section.ApiKeyUnreadable)
            return $"The saved {DisplayName} API key cannot be decrypted on this Windows account. Enter it again in Settings; the free services are used until then.";
        string noKey = $"{DisplayName} is selected but has no API key. Enter one in Settings; the free services are used until then.";
        bool keyEmpty = section.ApiKey.Trim().Length == 0;
        if (Provider is null) return keyEmpty ? noKey : null;
        var config = ProviderConfig.From(section, Provider.DefaultModel);
        // A compatible cloud preset (OpenRouter …) needs a key although a local server takes none.
        if (keyEmpty && Provider.NeedsKey(config.Credentials)) return noKey;
        if (Provider.Fields.Any(f => f is { Field: ProviderField.BaseUrl, Required: true })
            && !(Uri.TryCreate(config.Credentials.BaseUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"))
            return $"{DisplayName} is selected but has no valid server address. Enter one in Settings; the free services are used until then.";
        if (config.Model.Length == 0)
            return $"{DisplayName} is selected but no model is picked. Check the key in Settings and pick a model; the free services are used until then.";
        return null;
    }
}

/// <summary>The translator for the settings, and a notice for the user when the selected provider cannot be used.</summary>
public sealed record ProviderChain(ITranslator Translator, string? Notice);

/// <summary>
/// Owns the translators for the whole run of the app and builds the chain the settings ask for: the selected provider,
/// then the free services as the last fallback, each with the game profile applied the way it can take it, all behind
/// the cache. The cache and the free services are kept across settings saves: the cache is loaded once, Google
/// remembers that this network is blocked and MyMemory that its daily quota is used up, and a save must not forget
/// either and try again.
/// </summary>
public sealed class ProviderRegistry
{
    readonly Dictionary<string, ProviderRegistration> _providers = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _order = new();
    readonly Func<string, MyMemoryTranslator> _newMyMemory;
    readonly string? _cachePath;
    readonly DailyCharCounter? _myMemoryUsage;
    readonly object _gate = new();
    MyMemoryTranslator? _myMemory;
    ProfileTranslator? _free;
    string _myMemoryEmail = "";

    /// <param name="cachePath">cache.json, for <see cref="Apply"/>.</param>
    /// <param name="myMemoryUsage">Counts the characters sent to MyMemory today; shared by every MyMemory made here.</param>
    /// <param name="google">Replaces the real Google (tests).</param>
    /// <param name="newMyMemory">Makes the MyMemory for an email (tests).</param>
    public ProviderRegistry(string? cachePath = null, DailyCharCounter? myMemoryUsage = null, GoogleTranslator? google = null,
        Func<string, MyMemoryTranslator>? newMyMemory = null)
    {
        _cachePath = cachePath;
        _myMemoryUsage = myMemoryUsage;
        Google = google ?? new GoogleTranslator();
        _newMyMemory = newMyMemory ?? (email => new MyMemoryTranslator(email, usage: myMemoryUsage));
        Register(ProviderRegistration.For(new ClaudeProvider(), s => s.Claude));
        Register(ProviderRegistration.For(new OpenAIProvider(), s => s.OpenAI));
        Register(ProviderRegistration.For(new GeminiProvider(), s => s.Gemini));
        Register(ProviderRegistration.For(new CompatProvider(), s => s.Compat));
        Register(ProviderRegistration.For(new DeepLProvider(), s => s.DeepL));
        Register(ProviderRegistration.For(new AzureProvider(), s => s.Azure));
        Register(ProviderRegistration.For(new GoogleCloudProvider(), s => s.GoogleCloud));
    }

    public GoogleTranslator Google { get; }

    /// <summary>Every provider that can be selected besides the free services, in the order they were registered.</summary>
    public IReadOnlyList<ProviderRegistration> Providers => _order.Select(id => _providers[id]).ToList();

    /// <summary>The provider of this id, for its fields, key check and model list; null for an unknown id or one registered by hand.</summary>
    public ITranslationProvider? Provider(string id) => _providers.TryGetValue(id, out var registration) ? registration.Provider : null;

    /// <summary>The cache in front of every chain, made by the first <see cref="Apply"/>; null before.</summary>
    public CachedTranslator? Cache { get; private set; }

    /// <summary>A service of a chain built here gave way to the next one, or came back (see <see cref="FallbackTranslator.ServiceSwitched"/>).</summary>
    public event EventHandler<ServiceSwitch>? ServiceSwitched;

    /// <summary>Adds a provider, or replaces the one with its id. Registration belongs to the start of the run: lookups take no lock.</summary>
    public void Register(ProviderRegistration provider)
    {
        if (!_providers.ContainsKey(provider.Id)) _order.Add(provider.Id);
        _providers[provider.Id] = provider;
    }

    /// <summary>MyMemory for this email; a new one only when the email changed (the quota belongs to the email).</summary>
    public MyMemoryTranslator MyMemory(string email)
    {
        email = email.Trim();
        lock (_gate)
        {
            if (_myMemory is null || email != _myMemoryEmail)
            {
                _myMemory = _newMyMemory(email);
                _myMemoryEmail = email;
                _free = null;
            }
            return _myMemory;
        }
    }

    /// <summary>
    /// Google, then MyMemory when Google is blocked or fails, with the game profile applied by placeholders and fix
    /// rules. Its answers are cached under <see cref="ProviderKeys.Free"/>.
    /// </summary>
    public ITranslator Free(string myMemoryEmail)
    {
        var myMemory = MyMemory(myMemoryEmail);
        lock (_gate)
        {
            if (_free is null || _myMemory != myMemory)
            {
                var chain = new FallbackTranslator(ProviderKeys.Free, Google, myMemory);
                chain.ServiceSwitched += Relay;
                _free = new ProfileTranslator(chain);
            }
            return _free;
        }
    }

    /// <summary>
    /// Puts the chain for the settings behind the cache, which is made on the first call and kept: a settings save only
    /// changes what answers a miss.
    /// </summary>
    /// <returns>The cache as the translator, and the notice of <see cref="Build"/>.</returns>
    public ProviderChain Apply(AppSettings settings)
    {
        int maxEntries = Math.Max(100, settings.Storage.CacheMaxEntries);
        lock (_gate)
        {
            Cache ??= new CachedTranslator(Free(settings.Translation.Free.MyMemoryEmail),
                _cachePath ?? throw new InvalidOperationException("The registry was made without a cache file."), maxEntries);
            var chain = Build(settings.Translation, Cache);
            Cache.Inner = chain.Translator;
            Cache.MaxEntries = maxEntries;
            return chain with { Translator = Cache };
        }
    }

    /// <summary>Writes the cache and the MyMemory usage count now; for exit.</summary>
    public void Flush()
    {
        Cache?.Flush();
        _myMemoryUsage?.Flush();
    }

    /// <summary>
    /// The translator for the settings: the selected provider, then the free services. A provider that cannot run yet
    /// (<see cref="ProviderRegistration.Unusable"/>: no key, an undecryptable key, no model picked) leaves the free
    /// services translating alone, and the notice says so.
    /// </summary>
    /// <param name="cache">
    /// The cache the chain goes behind: the free services after the selected provider answer from their own entries
    /// (<see cref="CachedTranslator.Bucket"/>). Null builds the chain without it.
    /// </param>
    public ProviderChain Build(TranslationSettings settings, CachedTranslator? cache = null)
    {
        var free = Free(settings.Free.MyMemoryEmail);
        if (!_providers.TryGetValue(settings.Provider, out var provider)) return new ProviderChain(free, null);

        if (provider.Unusable(settings) is { } notice)
        {
            Log.Warn("translation: " + notice);
            return new ProviderChain(free, notice);
        }

        var active = provider.Create(settings);
        if (!provider.TakesInstructions) active = new ProfileTranslator(active, placeholders: !provider.MarksKeepEntries);
        var chain = new FallbackTranslator(active.Name, active, cache?.Bucket(free) ?? free) { MembersAreProviders = true };
        chain.ServiceSwitched += Relay;
        return new ProviderChain(chain, null);
    }

    void Relay(object? sender, ServiceSwitch e) => ServiceSwitched?.Invoke(this, e);
}
