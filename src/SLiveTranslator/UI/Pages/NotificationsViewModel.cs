using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;
using SLiveTranslator.Services;

namespace SLiveTranslator.UI.Pages;

/// <summary>One kind of notice on the Notifications page: on or off, and for how long.</summary>
public sealed class NoticeCategoryRow(NoticeCategory category, Func<NoticeCategorySettings> section, Action edited) : ObservableObject
{
    public const double MinSeconds = 1, MaxSeconds = 30;

    NoticeCategorySettings S => section();

    public NoticeCategory Category => category;
    public string Name => Loc.Instance["Notice.Category." + category];
    public string Hint => Loc.Instance["Notice.CategoryHint." + category];

    public bool Enabled
    {
        get => S.Enabled;
        set
        {
            if (S.Enabled == value) return;
            S.Enabled = value;
            OnPropertyChanged();
            edited();
        }
    }

    public double Seconds
    {
        get => S.Seconds;
        set
        {
            double seconds = Math.Round(Math.Clamp(value, MinSeconds, MaxSeconds), 1);
            if (S.Seconds.Equals(seconds)) return;
            S.Seconds = seconds;
            OnPropertyChanged();
            edited();
        }
    }

    /// <summary>The settings being edited were replaced, or the language changed.</summary>
    public void Reload() => OnPropertyChanged(string.Empty);
}

/// <summary>The Notifications page: each kind of notice, where they show, sound, tray balloons, the live badge and mute.</summary>
public sealed class NotificationsViewModel : SettingsPageViewModel
{
    public NotificationsViewModel(SettingsViewModel root, ISettingsHost host) : base(root)
    {
        Categories =
        [
            new(NoticeCategory.Status, () => N.Status, root.Edited),
            new(NoticeCategory.Warning, () => N.Warning, root.Edited),
            new(NoticeCategory.Error, () => N.Error, root.Edited),
            new(NoticeCategory.Quota, () => N.Quota, root.Edited),
        ];
        Positions = Choices<NoticePosition>("NoticePosition.");
        TestCommand = new AsyncRelayCommand(() => host.SendTestNotices(SettingsStore.Clone(Working).Notifications));
    }

    NotificationSettings N => Working.Notifications;

    public IReadOnlyList<NoticeCategoryRow> Categories { get; }

    public IReadOnlyList<Choice<NoticePosition>> Positions { get; private set; }

    public Choice<NoticePosition> SelectedPosition
    {
        get => Positions.FirstOrDefault(p => p.Value == N.Position) ?? Positions[0];
        set
        {
            if (value is not null) Set(N.Position, value.Value, v => N.Position = v);
        }
    }

    public bool Sound
    {
        get => N.Sound;
        set => Set(N.Sound, value, v => N.Sound = v);
    }

    public bool TrayBalloons
    {
        get => N.TrayBalloons;
        set => Set(N.TrayBalloons, value, v => N.TrayBalloons = v);
    }

    public bool LiveBadge
    {
        get => N.LiveBadge;
        set => Set(N.LiveBadge, value, v => N.LiveBadge = v);
    }

    public bool Muted
    {
        get => N.Muted;
        set => Set(N.Muted, value, v => N.Muted = v);
    }

    /// <summary>One notice of each kind that is on, as the page is set now, saved or not.</summary>
    public AsyncRelayCommand TestCommand { get; }

    public override void Reload()
    {
        base.Reload();
        foreach (var row in Categories) row.Reload();
    }

    /// <summary>The app's language changed: the positions and kinds are written again, the selection kept.</summary>
    public void Relocalize()
    {
        Positions = Choices<NoticePosition>("NoticePosition.");
        OnPropertyChanged(nameof(Positions));
        OnPropertyChanged(nameof(SelectedPosition));
        foreach (var row in Categories) row.Reload();
    }
}
