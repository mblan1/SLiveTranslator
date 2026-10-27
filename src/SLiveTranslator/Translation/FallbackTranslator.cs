using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation;

/// <summary>Another service of a chain answered than the one before.</summary>
/// <param name="Reason">Why the earlier ones failed; empty when the chain went back to a service that works again.</param>
public sealed record ServiceSwitch(string From, string To, string Reason);

/// <summary>Tries each translator in order and moves on when one is blocked, over quota, or unreachable.</summary>
public sealed class FallbackTranslator : ITranslator
{
    /// <summary>How often an identical failure is logged again when the service does not say how long it is paused.</summary>
    static readonly TimeSpan RepeatLogInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long a provider that failed is passed over when it does not say how long it is paused.</summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(30);

    sealed record Pause(DateTimeOffset Until, string Reason);

    readonly IReadOnlyList<ITranslator> _chain;
    readonly Pause?[] _paused;
    string? _answering; // the member that answered last; null until one did

    public FallbackTranslator(string name, params ITranslator[] chain)
    {
        Name = name;
        _chain = chain;
        _paused = new Pause?[chain.Length];
    }

    public string Name { get; }

    /// <summary>
    /// Each member is a provider of its own (the selected one, then the free services), so an answer belongs to the
    /// member that gave it in the cache. Otherwise the chain is one provider made of several services (Google, then
    /// MyMemory) and every answer belongs to <see cref="Name"/>.
    /// <para>
    /// A provider that failed is then passed over for its <see cref="TranslatorUnavailableException.RetryAfter"/>, or
    /// <see cref="DefaultPause"/>, and asked again after that: a provider that is down or stalls (a timeout) must not
    /// hold every line before the next one answers. The free services pause themselves, and the last member is always
    /// asked.
    /// </para>
    /// </summary>
    public bool MembersAreProviders { get; init; }

    /// <summary>The clock of the pauses (tests).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Raised once each time a different member starts answering: a service failed and the next one took over, or an
    /// earlier one works again. Raised on the thread of the translation.
    /// </summary>
    public event EventHandler<ServiceSwitch>? ServiceSwitched;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public async Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
    {
        var failures = new List<string>();
        for (int i = 0; i < _chain.Count; i++)
        {
            var translator = _chain[i];
            bool pausable = MembersAreProviders && i < _chain.Count - 1;
            if (pausable && Volatile.Read(ref _paused[i]) is { } pause && Time.GetUtcNow() < pause.Until)
            {
                failures.Add(pause.Reason);
                continue;
            }
            try
            {
                var result = await translator.TranslateAsync(request, ct);
                Answered(translator, failures);
                return result with { Provider = MembersAreProviders ? result.Provider ?? translator.Name : Name };
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && TranslatorUnavailableException.IsServiceFailure(ex))
            {
                // A paused service fails every line until the pause ends: one log line per pause is enough.
                var retryAfter = (ex as TranslatorUnavailableException)?.RetryAfter;
                string line = $"{translator.Name} unavailable: {ex.Message}";
                Log.Once(line, line, retryAfter ?? RepeatLogInterval, LogLevel.Warn);
                failures.Add(ex.Message);
                if (pausable) Volatile.Write(ref _paused[i], new Pause(Time.GetUtcNow() + (retryAfter ?? DefaultPause), ex.Message));
            }
        }
        throw new TranslatorUnavailableException(
            "No translation service is working right now. " + string.Join(" ", failures.Distinct()));
    }

    /// <summary>The first member's: it answers whenever it can, and a later member keeps its own entries (<see cref="CachedTranslator.Bucket"/>).</summary>
    public string ProfileStamp(GameProfile? profile, string line) => _chain[0].ProfileStamp(profile, line);

    void Answered(ITranslator translator, List<string> failures)
    {
        // Lines translated at the same time see the switch once: only the one that changes the field reports it.
        string from = Interlocked.Exchange(ref _answering, translator.Name) ?? _chain[0].Name;
        if (from == translator.Name) return;
        ServiceSwitched?.Invoke(this, new ServiceSwitch(from, translator.Name, string.Join(" ", failures.Distinct())));
    }
}
