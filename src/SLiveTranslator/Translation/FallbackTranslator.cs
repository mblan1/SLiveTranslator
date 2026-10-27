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

    /// <summary>Why a member did not answer; <paramref name="AboutText"/> when it declined this text only, which is no outage.</summary>
    readonly record struct Failure(string Reason, bool AboutText);

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
        var failures = new List<Failure>();
        var (result, member) = await TranslateFromAsync(0, request, failures, ct);
        // Members that only declined this text (a refusal) work for the next line: nothing switched. A member paused
        // meanwhile answered a request sent before its pause: the next lines still go past it, so nothing switched back.
        if ((member == 0 || !failures.TrueForAll(f => f.AboutText)) && !IsPaused(member)) Answered(_chain[member], failures);
        return result;
    }

    bool IsPaused(int member) => Volatile.Read(ref _paused[member]) is { } pause && Time.GetUtcNow() < pause.Until;

    /// <summary>
    /// Asks the members from <paramref name="first"/> on, in order. Lines a member left unanswered
    /// (<see cref="TranslateResult.FailedLines"/>) go to the members after it; the member that answered the rest is
    /// the one that answered.
    /// </summary>
    async Task<(TranslateResult Result, int Member)> TranslateFromAsync(int first, TranslateRequest request, List<Failure> failures, CancellationToken ct)
    {
        for (int i = first; i < _chain.Count; i++)
        {
            var translator = _chain[i];
            bool pausable = MembersAreProviders && i < _chain.Count - 1;
            if (pausable && Volatile.Read(ref _paused[i]) is { } pause && Time.GetUtcNow() < pause.Until)
            {
                failures.Add(new Failure(pause.Reason, AboutText: false));
                continue;
            }
            TranslateResult result;
            try
            {
                result = await translator.TranslateAsync(request, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && TranslatorUnavailableException.IsServiceFailure(ex))
            {
                // A paused service fails every line until the pause ends: one log line per pause is enough.
                var unavailable = ex as TranslatorUnavailableException;
                var retryAfter = unavailable?.RetryAfter;
                bool aboutText = unavailable?.AboutText == true;
                string line = $"{translator.Name} unavailable: {ex.Message}";
                Log.Once(line, line, retryAfter is { } wait && wait > TimeSpan.Zero ? wait : RepeatLogInterval, LogLevel.Warn);
                failures.Add(new Failure(ex.Message, aboutText));
                if (pausable && !aboutText) Volatile.Write(ref _paused[i], new Pause(Time.GetUtcNow() + (retryAfter ?? DefaultPause), ex.Message));
                continue;
            }
            result = result with { Provider = MembersAreProviders ? result.Provider ?? translator.Name : Name };
            // The member works; only the lines it left out go on, and a failure there is the later members' own.
            if (result.FailedLines is { Count: > 0 } failed && i < _chain.Count - 1)
            {
                var rest = request with { Lines = failed.Select(j => request.Lines[j]).ToList() };
                try
                {
                    result = Merge(result, failed, (await TranslateFromAsync(i + 1, rest, new List<Failure>(), ct)).Result);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && TranslatorUnavailableException.IsServiceFailure(ex))
                {
                    // Nothing after it can answer them now: the lines it answered still count, the others stay failed.
                }
            }
            return (result, i);
        }
        throw new TranslatorUnavailableException(
            "No translation service is working right now. " + string.Join(" ", failures.Select(f => f.Reason).Distinct()));
    }

    /// <summary>The answer with its failed lines replaced by the later members' answer for them, line by line.</summary>
    static TranslateResult Merge(TranslateResult result, IReadOnlyList<int> failed, TranslateResult rest)
    {
        int count = result.Texts.Count;
        var texts = result.Texts.ToArray();
        var answeredBy = Enumerable.Range(0, count).Select(result.AnsweredByAt).ToArray();
        var providers = Enumerable.Range(0, count).Select(i => result.ProviderAt(i)!).ToArray();
        var sources = result.FixedSources?.ToArray();
        for (int k = 0; k < failed.Count; k++)
        {
            int i = failed[k];
            (texts[i], answeredBy[i], providers[i]) = (rest.Texts[k], rest.AnsweredByAt(k), rest.ProviderAt(k)!);
            if (sources is not null && rest.FixedSources is { } fixedSources) sources[i] = fixedSources[k];
        }
        var stillFailed = rest.FailedLines?.Select(k => failed[k]).ToList();
        return result with
        {
            Texts = texts, FixedSources = sources, LineAnsweredBy = answeredBy, LineProviders = providers,
            FailedLines = stillFailed is { Count: > 0 } ? stillFailed : null,
        };
    }

    /// <summary>The first member's: it answers whenever it can, and a later member keeps its own entries (<see cref="CachedTranslator.Bucket"/>).</summary>
    public string ProfileStamp(GameProfile? profile, string line) => _chain[0].ProfileStamp(profile, line);

    void Answered(ITranslator translator, List<Failure> failures)
    {
        // Lines translated at the same time see the switch once: only the one that changes the field reports it.
        string from = Interlocked.Exchange(ref _answering, translator.Name) ?? _chain[0].Name;
        if (from == translator.Name) return;
        ServiceSwitched?.Invoke(this, new ServiceSwitch(from, translator.Name, string.Join(" ", failures.Select(f => f.Reason).Distinct())));
    }
}
