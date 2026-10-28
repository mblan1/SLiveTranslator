using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.Services;

/// <summary>What a notice is about; each has its own on/off and duration (<see cref="NotificationSettings"/>).</summary>
public enum NoticeCategory { Status, Warning, Error, Quota }

public enum NoticeSound { Asterisk, Exclamation, Hand }

/// <summary>One toast for the overlay.</summary>
/// <param name="Anchor">Picks the monitor (the game's); null keeps the one in use.</param>
/// <param name="Region">The subtitle region, for <see cref="NoticePosition.NearRegion"/> only.</param>
public sealed record ToastRequest(string Text, StatusKind Kind, DRect? Anchor, NoticePosition Position, DRect? Region, double Seconds);

/// <summary>Where notices appear: the overlay toast and badge, the tray balloon, the system sounds. Callable from any thread.</summary>
public interface INoticeSurface
{
    /// <summary>The overlay exists; before it does, an error can only go to the tray balloon.</summary>
    bool HasOverlay { get; }

    void ShowToast(ToastRequest toast);
    void HideToast();
    void ShowBalloon(string title, string text, bool warning);
    void Play(NoticeSound sound);
    void ShowBadge(DRect region, string text);
    void HideBadge();
}

/// <summary>
/// The one path of every notice to the user, applying <see cref="NotificationSettings"/>: each category on or off
/// and its seconds, the position, sounds, tray balloons, the live badge and mute. A notice the app raises by itself
/// (<see cref="Notify"/>) that repeats its category's last one is dropped for a while, longer each time it comes back
/// (<see cref="RepeatWindows"/>), so a failure on every subtitle line shows rarely during an outage. The answer to
/// something the user just did (<see cref="Feedback"/>, <see cref="Progress"/>) shows every time. Thread-safe.
/// </summary>
public sealed class Notifier
{
    /// <summary>How long a repeat of a category's last notice is dropped: the first window, then the next each time it comes back.</summary>
    public static readonly IReadOnlyList<TimeSpan> RepeatWindows = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>A repeat asked for after this long without one is new again.</summary>
    public static readonly TimeSpan QuietReset = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan SoundGap = TimeSpan.FromSeconds(2);

    /// <summary>The shortest time a notice stays is 1 s plus its text at this reading speed, so a long text outlasts its category's seconds.</summary>
    const double ReadingCharsPerSecond = 15;
    const double MinSeconds = 1, MaxSeconds = 30;

    static readonly NoticeCategory[] Categories = [NoticeCategory.Status, NoticeCategory.Warning, NoticeCategory.Error, NoticeCategory.Quota];

    /// <summary>A category's last notice from <see cref="Notify"/>: when it was shown and last asked for, and how often it came back in a row.</summary>
    readonly record struct Last(string Text, DateTimeOffset ShownAt, DateTimeOffset AskedAt, int Returns);

    readonly INoticeSurface _surface;
    readonly Func<NotificationSettings> _settings;
    readonly Func<DRect?> _region;
    readonly TimeProvider _clock;
    readonly Func<TimeSpan, Task> _delay;
    readonly object _gate = new();
    readonly Dictionary<NoticeCategory, Last> _last = new();
    DateTimeOffset _lastSound = DateTimeOffset.MinValue;
    bool _progressShown; // the toast holds a Progress notice that nothing replaced yet

    /// <param name="region">The subtitle region when one is known, for <see cref="NoticePosition.NearRegion"/>.</param>
    /// <param name="delay">Waits between the notices of <see cref="SendTestAsync"/> (tests).</param>
    public Notifier(INoticeSurface surface, Func<NotificationSettings> settings, Func<DRect?>? region = null, TimeProvider? clock = null,
        Func<TimeSpan, Task>? delay = null)
    {
        _surface = surface;
        _settings = settings;
        _region = region ?? (() => null);
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? (wait => Task.Delay(wait));
    }

    /// <summary>Hides every notice but errors, and every sound; kept in <see cref="NotificationSettings.Muted"/>.</summary>
    public bool Muted
    {
        get => _settings().Muted;
        set => _settings().Muted = value;
    }

    /// <summary>A notice the app raises by itself (a failed line, a provider giving way): a repeat is dropped for a while.</summary>
    /// <param name="anchor">Picks the monitor; null keeps the one in use.</param>
    /// <param name="kind">The toast's accent; by default the category's.</param>
    /// <returns>Whether the notice was shown.</returns>
    public bool Notify(NoticeCategory category, string text, DRect? anchor = null, StatusKind? kind = null)
    {
        LogNotice(category, text);
        var settings = _settings();
        if (!Allowed(settings, category)) return false;
        int returns = 0;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_last.TryGetValue(category, out var last) && last.Text == text && now - last.AskedAt < QuietReset)
            {
                if (now - last.ShownAt < RepeatWindows[Math.Min(last.Returns, RepeatWindows.Count - 1)])
                {
                    _last[category] = last with { AskedAt = now };
                    return false;
                }
                returns = last.Returns + 1;
            }
            _last[category] = new Last(text, now, now, returns);
        }
        // A repeat was read before: it stays for the category's seconds only.
        double seconds = Options(settings, category).Seconds;
        return Show(settings, category, text, anchor, kind ?? DefaultKind(category), returns > 0 ? Clamp(seconds) : Duration(seconds, text));
    }

    /// <summary>
    /// The answer to something the user just did (a hotkey, the tray, a settings save): shown every time, even right
    /// after the same one, so a key pressed twice is answered twice. It leaves the repeat check of <see cref="Notify"/> alone.
    /// </summary>
    /// <inheritdoc cref="Notify"/>
    public bool Feedback(NoticeCategory category, string text, DRect? anchor = null, StatusKind? kind = null)
    {
        LogNotice(category, text);
        var settings = _settings();
        if (!Allowed(settings, category)) return false;
        return Show(settings, category, text, anchor, kind ?? DefaultKind(category), Duration(Options(settings, category).Seconds, text));
    }

    /// <summary>
    /// A Status notice for work under way ("Translating screen…"), shown like <see cref="Feedback"/> for up to
    /// <paramref name="seconds"/>: the notice of its result replaces it, and <see cref="EndProgress"/> takes it away
    /// when there is none to show.
    /// </summary>
    public bool Progress(string text, DRect? anchor, double seconds)
    {
        var settings = _settings();
        if (!Allowed(settings, NoticeCategory.Status)) return false;
        return Show(settings, NoticeCategory.Status, text, anchor, StatusKind.Info, Clamp(seconds), progress: true);
    }

    /// <summary>The work of the last <see cref="Progress"/> notice is done: takes it away unless another notice replaced it.</summary>
    public void EndProgress()
    {
        lock (_gate)
        {
            if (!_progressShown) return;
            _progressShown = false;
        }
        _surface.HideToast();
    }

    /// <summary>The tray balloon that says the app started; Windows hides it while a game is in front, so it is extra.</summary>
    public void StartupBalloon(string title, string text)
    {
        var settings = _settings();
        if (settings.TrayBalloons && !settings.Muted) _surface.ShowBalloon(title, text, warning: false);
    }

    /// <summary>The marker at the subtitle region while subtitles are on, unless switched off.</summary>
    public void ShowLiveBadge(DRect region, string text)
    {
        if (_settings().LiveBadge) _surface.ShowBadge(region, text);
        else _surface.HideBadge();
    }

    public void HideLiveBadge() => _surface.HideBadge();

    /// <summary>
    /// One notice of each enabled category for its seconds, a sound apart, each with its sound, ignoring mute and the
    /// repeat check: what the user's settings look and sound like.
    /// </summary>
    /// <param name="look">Settings not saved yet (the Notifications page as it is); by default the ones in use.</param>
    public async Task SendTestAsync(NotificationSettings? look = null)
    {
        bool first = true;
        foreach (var category in Categories)
        {
            var settings = look ?? _settings();
            var options = Options(settings, category);
            if (!options.Enabled) continue;
            if (!first) await _delay(SoundGap);
            first = false;
            Show(settings, category, Loc.Instance["Notice.Test." + category], null, DefaultKind(category), Clamp(options.Seconds), test: true);
        }
    }

    static bool Allowed(NotificationSettings settings, NoticeCategory category) =>
        Options(settings, category).Enabled && (!settings.Muted || category == NoticeCategory.Error);

    /// <summary>Errors and quota warnings are logged even when not shown; the other notices are logged where they arise.</summary>
    static void LogNotice(NoticeCategory category, string text)
    {
        if (category is NoticeCategory.Error or NoticeCategory.Quota)
            Log.Once("notice: " + text, $"notice ({category}): {text}", TimeSpan.FromMinutes(1), category == NoticeCategory.Error ? LogLevel.Error : LogLevel.Warn);
    }

    /// <param name="test">From <see cref="SendTestAsync"/>: sounds even when muted or right after another one.</param>
    /// <returns>Whether it reached the user: before the overlay exists only an error can, by the tray balloon.</returns>
    bool Show(NotificationSettings settings, NoticeCategory category, string text, DRect? anchor, StatusKind kind, double seconds,
        bool progress = false, bool test = false)
    {
        if (_surface.HasOverlay)
        {
            var region = settings.Position == NoticePosition.NearRegion ? _region() : null;
            var position = settings.Position == NoticePosition.NearRegion && region is null ? NoticePosition.TopCenter : settings.Position;
            lock (_gate) _progressShown = progress;
            _surface.ShowToast(new ToastRequest(text, kind, anchor, position, region, seconds));
        }
        else if (category == NoticeCategory.Error && settings.TrayBalloons)
        {
            _surface.ShowBalloon("SLiveTranslator", text, warning: true);
        }
        else
        {
            return false;
        }
        if (settings.Sound && (test || !settings.Muted)) PlayOnce(category, test);
        return true;
    }

    /// <param name="always">Skips the gap: the test waits it out already, and a timer can end a little early.</param>
    void PlayOnce(NoticeCategory category, bool always)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (!always && now - _lastSound < SoundGap) return;
            _lastSound = now;
        }
        _surface.Play(category switch
        {
            NoticeCategory.Status => NoticeSound.Asterisk,
            NoticeCategory.Error => NoticeSound.Hand,
            _ => NoticeSound.Exclamation,
        });
    }

    /// <summary>The category's seconds, or the time it takes to read the text when that is longer.</summary>
    static double Duration(double seconds, string text) => Clamp(Math.Max(seconds, 1 + text.Length / ReadingCharsPerSecond));

    static double Clamp(double seconds) => Math.Clamp(seconds, MinSeconds, MaxSeconds);

    static NoticeCategorySettings Options(NotificationSettings settings, NoticeCategory category) => category switch
    {
        NoticeCategory.Warning => settings.Warning,
        NoticeCategory.Error => settings.Error,
        NoticeCategory.Quota => settings.Quota,
        _ => settings.Status,
    };

    static StatusKind DefaultKind(NoticeCategory category) => category switch
    {
        NoticeCategory.Error => StatusKind.Error,
        NoticeCategory.Warning or NoticeCategory.Quota => StatusKind.Warning,
        _ => StatusKind.Info,
    };
}
