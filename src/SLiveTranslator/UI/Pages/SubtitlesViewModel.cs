using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI.Pages;

/// <summary>The Subtitles page: the region and how it is read.</summary>
public sealed class SubtitlesViewModel : SettingsPageViewModel
{
    public SubtitlesViewModel(SettingsViewModel root, ISettingsHost host) : base(root)
    {
        SelectRegionCommand = new RelayCommand(host.SelectRegion);
        OcrModes = Choices<OcrMode>("OcrMode.");
    }

    SubtitleSettings S => Working.Subtitles;

    /// <summary>The app's language changed: the OCR modes and the region are written again in it, the selection kept.</summary>
    public void Relocalize()
    {
        OcrModes = Choices<OcrMode>("OcrMode.");
        OnPropertyChanged(nameof(OcrModes));
        OnPropertyChanged(nameof(SelectedOcrMode));
        OnPropertyChanged(nameof(RegionText));
    }

    public RelayCommand SelectRegionCommand { get; }

    public string RegionText => S.Region is { } r ? Loc.F("Subtitles.RegionValue", r.Width, r.Height, r.X, r.Y) : Loc.Instance["Subtitles.RegionNone"];

    public double IntervalMs
    {
        get => S.IntervalMs;
        set => Set(S.IntervalMs, (int)Math.Clamp(Math.Round(value), 100, 5000), v => S.IntervalMs = v);
    }

    public bool PauseWhenGameUnfocused
    {
        get => S.PauseWhenGameUnfocused;
        set => Set(S.PauseWhenGameUnfocused, value, v => S.PauseWhenGameUnfocused = v);
    }

    public IReadOnlyList<Choice<OcrMode>> OcrModes { get; private set; }

    public Choice<OcrMode> SelectedOcrMode
    {
        get => OcrModes.FirstOrDefault(m => m.Value == S.OcrMode) ?? OcrModes[0];
        set
        {
            if (value is not null) Set(S.OcrMode, value.Value, v => S.OcrMode = v);
        }
    }

    public bool LowImpact
    {
        get => S.LowImpact;
        set => Set(S.LowImpact, value, v => S.LowImpact = v);
    }

    public double ParagraphGapRatio
    {
        get => S.ParagraphGapRatio;
        set => Set(S.ParagraphGapRatio, Math.Round(Math.Clamp(value, 0.7, 2.5), 2), v => S.ParagraphGapRatio = v);
    }
}
