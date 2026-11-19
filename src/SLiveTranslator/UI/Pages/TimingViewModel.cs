using System;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI.Pages;

/// <summary>
/// The Timing page: how long the translated lines stay after the game hid its subtitle, how they fade, what a new line
/// does to the older ones, and what Clear does. Its timeline draws one sample line with these settings.
/// </summary>
public sealed class TimingViewModel : SettingsPageViewModel
{
    public TimingViewModel(SettingsViewModel root, ISettingsHost host) : base(root)
    {
        PlayCommand = new RelayCommand(() => host.PreviewOverlay(SettingsStore.Clone(Working).Overlay));
        ResetCommand = new RelayCommand(Reset);
    }

    OverlaySettings O => Working.Overlay;

    public RelayCommand PlayCommand { get; }
    public RelayCommand ResetCommand { get; }

    public void Relocalize() => Reload();

    /// <summary>Every timing value back to its default; the rest of the overlay settings stay.</summary>
    void Reset()
    {
        var d = new OverlaySettings();
        var o = O;
        bool changed = o.Stay != d.Stay || o.StaySeconds != d.StaySeconds || o.ReadingSpeed != d.ReadingSpeed || o.MinSeconds != d.MinSeconds
                       || o.FadeInMs != d.FadeInMs || o.FadeOutMs != d.FadeOutMs || o.NewLines != d.NewLines || o.TimeLeftBar != d.TimeLeftBar
                       || o.Clear != d.Clear || o.ClearHistory != d.ClearHistory;
        if (!changed) return;
        (o.Stay, o.StaySeconds, o.ReadingSpeed, o.MinSeconds) = (d.Stay, d.StaySeconds, d.ReadingSpeed, d.MinSeconds);
        (o.FadeInMs, o.FadeOutMs, o.NewLines, o.TimeLeftBar) = (d.FadeInMs, d.FadeOutMs, d.NewLines, d.TimeLeftBar);
        (o.Clear, o.ClearHistory) = (d.Clear, d.ClearHistory);
        Reload();
        Root.Edited();
    }

    // ---- how long the lines stay ----

    public bool IsFixed
    {
        get => O.Stay == StayMode.Fixed;
        set => SetStay(value, StayMode.Fixed);
    }

    public bool IsByLength
    {
        get => O.Stay == StayMode.ByLength;
        set => SetStay(value, StayMode.ByLength);
    }

    public bool IsUntilNext
    {
        get => O.Stay == StayMode.UntilNext;
        set => SetStay(value, StayMode.UntilNext);
    }

    void SetStay(bool chosen, StayMode mode)
    {
        if (!chosen) return;
        Set(O.Stay, mode, v => O.Stay = v, nameof(O.Stay));
        Reload();
    }

    /// <summary>0 to 20 s, in half seconds.</summary>
    public double StaySeconds
    {
        get => O.StaySeconds;
        set
        {
            Set(O.StaySeconds, Math.Clamp(Math.Round(value * 2) / 2, 0, 20), v => O.StaySeconds = v);
            Timeline();
        }
    }

    public string StayText => Seconds(O.StaySeconds);

    /// <summary>Characters a second, 8 to 30.</summary>
    public double ReadingSpeed
    {
        get => O.ReadingSpeed;
        set
        {
            Set(O.ReadingSpeed, (int)Math.Clamp(Math.Round(value), 8, 30), v => O.ReadingSpeed = v);
            Timeline();
        }
    }

    public string ReadingSpeedText => Loc.F("Timing.SpeedValue", O.ReadingSpeed);

    public string ByLengthExample => Loc.F("Timing.ByLengthExample", SubtitleTiming.SampleChars, Seconds(SubtitleTiming.ByLength(O, SubtitleTiming.SampleChars)));

    /// <summary>0.5 to 5 s, in half seconds.</summary>
    public double MinSeconds
    {
        get => O.MinSeconds;
        set
        {
            Set(O.MinSeconds, Math.Clamp(Math.Round(value * 2) / 2, 0.5, 5), v => O.MinSeconds = v);
            Timeline();
        }
    }

    public string MinText => Seconds(O.MinSeconds);

    public bool TimeLeftBar
    {
        get => O.TimeLeftBar;
        set => Set(O.TimeLeftBar, value, v => O.TimeLeftBar = v);
    }

    // ---- fades ----

    /// <summary>0 to 500 ms, in steps of 20.</summary>
    public double FadeInMs
    {
        get => O.FadeInMs;
        set
        {
            Set(O.FadeInMs, (int)Math.Clamp(Math.Round(value / 20) * 20, 0, 500), v => O.FadeInMs = v);
            Timeline();
        }
    }

    /// <summary>0 to 2000 ms, in steps of 50.</summary>
    public double FadeOutMs
    {
        get => O.FadeOutMs;
        set
        {
            Set(O.FadeOutMs, (int)Math.Clamp(Math.Round(value / 50) * 50, 0, 2000), v => O.FadeOutMs = v);
            Timeline();
        }
    }

    public string FadeInText => Millis(O.FadeInMs);
    public string FadeOutText => Millis(O.FadeOutMs);

    // ---- new lines and Clear ----

    public bool IsPush
    {
        get => O.NewLines == NewLineMode.Push;
        set => SetNewLines(value, NewLineMode.Push);
    }

    public bool IsReplace
    {
        get => O.NewLines == NewLineMode.Replace;
        set => SetNewLines(value, NewLineMode.Replace);
    }

    void SetNewLines(bool chosen, NewLineMode mode)
    {
        if (!chosen) return;
        Set(O.NewLines, mode, v => O.NewLines = v, nameof(O.NewLines));
        OnPropertyChanged(nameof(IsPush));
        OnPropertyChanged(nameof(IsReplace));
    }

    public string PushHint => Loc.F("Timing.PushHint", Math.Clamp(O.MaxLines, 1, 5));

    public bool IsClearText
    {
        get => O.Clear == ClearMode.TextOnly;
        set => SetClear(value, ClearMode.TextOnly);
    }

    public bool IsClearStop
    {
        get => O.Clear == ClearMode.StopSubtitles;
        set => SetClear(value, ClearMode.StopSubtitles);
    }

    void SetClear(bool chosen, ClearMode mode)
    {
        if (!chosen) return;
        Set(O.Clear, mode, v => O.Clear = v, nameof(O.Clear));
        OnPropertyChanged(nameof(IsClearText));
        OnPropertyChanged(nameof(IsClearStop));
    }

    public bool ClearHistory
    {
        get => O.ClearHistory;
        set => Set(O.ClearHistory, value, v => O.ClearHistory = v);
    }

    public string ClearHotkey => Working.Hotkeys.Disabled.Contains(HotkeyAction.Clear) ? Loc.Instance["Display.HotkeyOff"] : Working.Hotkeys[HotkeyAction.Clear];

    public string ToggleHotkey => Working.Hotkeys[HotkeyAction.ToggleSubtitles];

    // ---- the timeline: one sample line the game shows for 3 s ----

    double StayUnits => O.Stay switch
    {
        StayMode.UntilNext => 4,
        StayMode.ByLength => SubtitleTiming.ByLength(O, SubtitleTiming.SampleChars),
        _ => Math.Max(Math.Clamp(O.StaySeconds, 0, 20), SubtitleTiming.MinSeconds(O) - SubtitleTiming.SampleShowSeconds),
    };

    static GridLength Star(double value) => new(Math.Max(0.001, value), GridUnitType.Star);

    public GridLength FadeInLength => Star(O.FadeInMs / 1000.0);
    public GridLength ShowLength => Star(SubtitleTiming.SampleShowSeconds);
    public GridLength StayLength => Star(StayUnits);
    public GridLength FadeOutLength => Star(O.FadeOutMs / 1000.0);

    public bool StaysUntilNext => O.Stay == StayMode.UntilNext;

    public string StayLabel => O.Stay switch
    {
        StayMode.UntilNext => Loc.Instance["Timing.StaysUntilNext"],
        _ when StayUnits <= 0 => Loc.Instance["Timing.HidesWithGame"],
        StayMode.ByLength => Loc.F("Timing.StaysAbout", Seconds(StayUnits)),
        _ => Loc.F("Timing.Stays", Seconds(StayUnits)),
    };

    public string TimelineStart => Loc.F("Timing.TimelineStart", Millis(O.FadeInMs));

    public string TimelineEnd => O.Stay == StayMode.UntilNext
        ? Loc.F("Timing.TimelineEndNext", Millis(O.FadeOutMs))
        : Loc.F("Timing.TimelineEnd", Millis(O.FadeOutMs),
            Seconds(O.FadeInMs / 1000.0 + SubtitleTiming.SampleShowSeconds + StayUnits + O.FadeOutMs / 1000.0));

    void Timeline()
    {
        foreach (var name in new[]
                 {
                     nameof(FadeInLength), nameof(StayLength), nameof(FadeOutLength), nameof(StayLabel), nameof(TimelineStart), nameof(TimelineEnd),
                     nameof(StayText), nameof(ReadingSpeedText), nameof(ByLengthExample), nameof(MinText), nameof(FadeInText), nameof(FadeOutText),
                 })
            OnPropertyChanged(name);
    }

    static string Seconds(double s) => Loc.F("Timing.SecondsValue", s < 10 ? Math.Round(s, 1).ToString("0.0", Loc.Instance.Culture) : Math.Round(s).ToString("0", Loc.Instance.Culture));

    static string Millis(int ms) => ms >= 1000 ? Seconds(ms / 1000.0) : Loc.F("Timing.MsValue", ms);
}
