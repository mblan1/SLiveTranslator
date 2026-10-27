using System;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.UI;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.Services;

/// <summary>
/// Turns what the translation services report into notices, each once per cause rather than once per line: the
/// selected provider giving way to the free services and coming back, and MyMemory's daily quota running out.
/// Thread-safe.
/// </summary>
public sealed class AlertRules
{
    /// <summary>Share of MyMemory's daily characters that warns before they run out.</summary>
    public const double QuotaWarningShare = 0.8;

    /// <summary>The selected provider failing again in the same way this soon after its notice is not said again.</summary>
    public static readonly TimeSpan DownRepeat = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A return after a shorter outage is only logged: a provider that times out now and then is passed over for
    /// <see cref="FallbackTranslator.DefaultPause"/> each time, and a notice pair every half minute would cover the game.
    /// </summary>
    public static readonly TimeSpan ShortOutage = TimeSpan.FromMinutes(1);

    /// <summary>A provider's notice that it gave way, not yet answered by one that it is back.</summary>
    sealed record DownNotice(string Provider, CheckStatus? Status, DateTimeOffset At);

    readonly Notifier _notifier;
    readonly TimeProvider _clock;
    readonly object _gate = new();
    DateOnly? _quotaWarnedOn, _quotaExhaustedOn;
    DownNotice? _down;
    DateTimeOffset? _downSince; // when the selected provider last gave way

    public AlertRules(Notifier notifier, TimeProvider? clock = null)
    {
        _notifier = notifier;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// A chain switched services. Only the selected provider's switches are said: a paid provider out of credit must
    /// not be replaced unnoticed. The free services' own switch (Google blocked, so MyMemory) is only logged. A
    /// provider that keeps failing in the same way is said once per <see cref="DownRepeat"/>, and its return only
    /// after a said outage of <see cref="ShortOutage"/> or more.
    /// </summary>
    /// <param name="selected">The selected provider's translator name ("claude/claude-opus-5-5"); the free chain's name when none is.</param>
    /// <param name="displayName">The provider's name for the user ("Claude").</param>
    public void ServiceSwitched(ServiceSwitch change, string selected, string displayName, DRect? anchor)
    {
        var now = _clock.GetUtcNow();
        if (change.From == selected && change.Reason.Length > 0)
        {
            lock (_gate)
            {
                _downSince = now;
                if (_down is { } said && said.Provider == selected && said.Status == change.Status && now - said.At < DownRepeat) return;
            }
            if (!_notifier.Notify(NoticeCategory.Quota, Loc.F("Alert.ProviderDown", displayName, Reason(change.Status)), anchor)) return;
            lock (_gate) _down = new DownNotice(selected, change.Status, now);
        }
        else if (change.To == selected && change.Reason.Length == 0)
        {
            lock (_gate)
            {
                var outage = now - _downSince;
                if (_down?.Provider != selected || outage < ShortOutage)
                {
                    Log.Write($"alerts: {displayName} answers again" + (outage is { } o ? $" after {o.TotalSeconds:0} s" : ""));
                    return;
                }
                _down = null;
            }
            _notifier.Notify(NoticeCategory.Status, Loc.F("Alert.ProviderBack", displayName), anchor, StatusKind.On);
        }
    }

    /// <summary>
    /// MyMemory counted more characters today: a quota warning once the count passes <see cref="QuotaWarningShare"/>
    /// of the daily limit, an error once it reaches it, each once per local day.
    /// </summary>
    /// <param name="hasEmail">An email raises the limit; without one, the notice suggests it.</param>
    public void MyMemoryUsage(long used, int limit, bool hasEmail)
    {
        if (limit <= 0) return;
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        bool exhausted = used >= limit;
        lock (_gate)
        {
            if (exhausted)
            {
                if (_quotaExhaustedOn == today) return;
                _quotaExhaustedOn = _quotaWarnedOn = today; // the warning would come too late now
            }
            else
            {
                if (used < limit * QuotaWarningShare || _quotaWarnedOn == today) return;
                _quotaWarnedOn = today;
            }
        }
        string tail = hasEmail ? "Email" : "NoEmail";
        if (exhausted) _notifier.Notify(NoticeCategory.Error, Loc.F("Alert.MyMemoryLimit." + tail, limit));
        else _notifier.Notify(NoticeCategory.Quota, Loc.F("Alert.MyMemoryNearLimit." + tail, used, limit));
    }

    /// <summary>Why a provider stopped answering, short enough for the middle of a sentence.</summary>
    public static string Reason(CheckStatus? status) =>
        Loc.Instance["Reason." + (status is null or CheckStatus.Ok ? CheckStatus.Failed : status.Value)];
}
