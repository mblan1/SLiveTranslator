using System;
using System.Collections.Generic;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation;

/// <summary>A provider the user can select, and how its translator is made from the settings.</summary>
/// <param name="Id">One of <see cref="ProviderIds"/>.</param>
/// <param name="TakesInstructions">An LLM: the game profile goes into its prompt. Any other engine gets the profile
/// applied by a <see cref="ProfileTranslator"/>.</param>
/// <param name="KeySettings">The settings that hold its API key; null for a provider without a key.</param>
/// <param name="Create">Called only when the key is there and readable.</param>
public sealed record ProviderRegistration(string Id, string DisplayName, bool TakesInstructions,
    Func<TranslationSettings, ProviderSettings?> KeySettings, Func<TranslationSettings, ITranslator> Create);

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
        Register(new ProviderRegistration(ProviderIds.Claude, "Claude", TakesInstructions: true, s => s.Claude,
            s => new ClaudeTranslator(s.Claude.ApiKey, s.Claude.Model, s.Claude.Effort)));
    }

    public GoogleTranslator Google { get; }

    /// <summary>The cache in front of every chain, made by the first <see cref="Apply"/>; null before.</summary>
    public CachedTranslator? Cache { get; private set; }

    /// <summary>A service of a chain built here gave way to the next one, or came back (see <see cref="FallbackTranslator.ServiceSwitched"/>).</summary>
    public event EventHandler<ServiceSwitch>? ServiceSwitched;

    /// <summary>Adds a provider, or replaces the one with its id.</summary>
    public void Register(ProviderRegistration provider) => _providers[provider.Id] = provider;

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
    /// The translator for the settings: the selected provider, then the free services. A provider that needs a key
    /// runs on the free services alone while its key is missing or cannot be decrypted, and the notice says so.
    /// </summary>
    /// <param name="cache">
    /// The cache the chain goes behind: the free services after the selected provider answer from their own entries
    /// (<see cref="CachedTranslator.Bucket"/>). Null builds the chain without it.
    /// </param>
    public ProviderChain Build(TranslationSettings settings, CachedTranslator? cache = null)
    {
        var free = Free(settings.Free.MyMemoryEmail);
        if (!_providers.TryGetValue(settings.Provider, out var provider)) return new ProviderChain(free, null);

        if (provider.KeySettings(settings) is { } key)
        {
            // An undecryptable key must not reach an SDK as "": it may fall back to a key in the environment, which
            // may bill someone else.
            if (key.ApiKeyUnreadable)
                return OnFree($"The saved {provider.DisplayName} API key cannot be decrypted on this Windows account. Enter it again in Settings; the free services are used until then.");
            if (key.ApiKey.Trim().Length == 0)
                return OnFree($"{provider.DisplayName} is selected but has no API key. Enter one in Settings; the free services are used until then.");
        }

        var active = provider.Create(settings);
        if (!provider.TakesInstructions) active = new ProfileTranslator(active);
        var chain = new FallbackTranslator(active.Name, active, cache?.Bucket(free) ?? free) { MembersAreProviders = true };
        chain.ServiceSwitched += Relay;
        return new ProviderChain(chain, null);

        ProviderChain OnFree(string notice)
        {
            Log.Warn("translation: " + notice);
            return new ProviderChain(free, notice);
        }
    }

    void Relay(object? sender, ServiceSwitch e) => ServiceSwitched?.Invoke(this, e);
}
