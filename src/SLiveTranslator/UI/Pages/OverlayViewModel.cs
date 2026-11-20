using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI.Pages;

/// <summary>The Overlay page: how the translated subtitles look, with a preview on the screen.</summary>
public sealed class OverlayViewModel : SettingsPageViewModel
{
    public OverlayViewModel(SettingsViewModel root, ISettingsHost host) : base(root)
    {
        Positions = Choices<SubtitlePosition>("Position.");
        PreviewCommand = new RelayCommand(() => host.PreviewOverlay(SettingsStore.Clone(Working).Overlay));
    }

    OverlaySettings O => Working.Overlay;

    /// <summary>The app's language changed: the position list is written again in it, the selection kept.</summary>
    public void Relocalize()
    {
        Positions = Choices<SubtitlePosition>("Position.");
        OnPropertyChanged(nameof(Positions));
        OnPropertyChanged(nameof(SelectedPosition));
    }

    public RelayCommand PreviewCommand { get; }

    public double FontSize
    {
        get => O.FontSize;
        set => Set(O.FontSize, (int)Math.Clamp(Math.Round(value), 10, 80), v => O.FontSize = v);
    }

    public double MaxLines
    {
        get => O.MaxLines;
        set => Set(O.MaxLines, (int)Math.Clamp(Math.Round(value), 1, 5), v => O.MaxLines = v);
    }

    /// <summary>Percent, 20 to 100.</summary>
    public double TextOpacity
    {
        get => Math.Round(O.TextOpacity * 100);
        set => Set(O.TextOpacity, Math.Clamp(Math.Round(value), 20, 100) / 100, v => O.TextOpacity = v);
    }

    /// <summary>Percent, 0 to 100.</summary>
    public double BackgroundOpacity
    {
        get => Math.Round(O.BackgroundOpacity * 100);
        set => Set(O.BackgroundOpacity, Math.Clamp(Math.Round(value), 0, 100) / 100, v => O.BackgroundOpacity = v);
    }

    public IReadOnlyList<Choice<SubtitlePosition>> Positions { get; private set; }

    public Choice<SubtitlePosition> SelectedPosition
    {
        get => Positions.FirstOrDefault(p => p.Value == O.Position) ?? Positions[0];
        set
        {
            if (value is null) return;
            // "Over the original" is not offered in Shared on screen (see RegionRules): it stays above the region.
            var position = value.Value == SubtitlePosition.OverRegion && Overlay.RegionRules.MustAvoid(O) ? SubtitlePosition.AboveRegion : value.Value;
            Set(O.Position, position, v => O.Position = v);
            if (position != value.Value) OnPropertyChanged();
        }
    }

    public bool ShowOriginal
    {
        get => O.ShowOriginal;
        set => Set(O.ShowOriginal, value, v => O.ShowOriginal = v);
    }

    public double HistoryLines
    {
        get => O.HistoryLines;
        set => Set(O.HistoryLines, (int)Math.Clamp(Math.Round(value), 3, 50), v => O.HistoryLines = v);
    }

    /// <summary>Seconds the translate-screen labels stay; 0 keeps them until cleared.</summary>
    public double LabelSeconds
    {
        get => O.LabelSeconds;
        set => Set(O.LabelSeconds, (int)Math.Clamp(Math.Round(value), 0, 300), v => O.LabelSeconds = v);
    }
}
