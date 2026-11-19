using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Translation.Providers;

namespace SLiveTranslator.UI.Pages;

/// <param name="State">"OK", "In use" …; "" before hotkeys were registered.</param>
public sealed record HotkeyRow(string Action, string Gesture, string State, bool Ok);

/// <summary>One way to more free translation, in the order the app recommends them.</summary>
public sealed record Recommendation(int Number, string Text, bool Done);

/// <summary>The Home page: live state and quick actions, from the settings in use (not the ones being edited).</summary>
public sealed class HomeViewModel : ObservableObject
{
    readonly ISettingsHost _host;

    public HomeViewModel(ISettingsHost host)
    {
        _host = host;
        SelectRegionCommand = new RelayCommand(host.SelectRegion);
        TranslateScreenCommand = new RelayCommand(host.TranslateScreen);
        HistoryCommand = new RelayCommand(host.ToggleHistory);
        ClearCommand = new RelayCommand(host.ClearOverlay);
        PreviewCommand = new RelayCommand(() => host.PreviewOverlay(SettingsStore.Clone(Current).Overlay));
        OpenCommand = new RelayCommand<string>(page =>
        {
            if (page is not null) Navigate?.Invoke(page);
        });
    }

    /// <summary>Shows another page of the window (set by the window's view model).</summary>
    public Action<string>? Navigate { get; set; }

    AppSettings Current => _host.Current;

    public bool SubtitlesOn
    {
        get => _host.SubtitlesOn;
        set
        {
            if (value != _host.SubtitlesOn) _host.SetSubtitles(value);
            OnPropertyChanged(); // the switch shows what the app did: turning on asks for a region first, and may be cancelled
        }
    }

    public string ProviderLine => ProviderLineOf(Current.Translation, _host.Providers);

    /// <summary>"Claude  ·  claude-opus-5-5"; the free services alone for an unknown provider (also the tray's status line).</summary>
    public static string ProviderLineOf(TranslationSettings translation, IEnumerable<ITranslationProvider> providers)
    {
        var provider = providers.FirstOrDefault(p => p.Id.Equals(translation.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return Loc.Instance["Translation.FreeProvider"];
        string model = TranslationViewModel.SectionOf(translation, provider.Id).Model.Trim();
        return model.Length > 0 ? $"{provider.DisplayName}  ·  {model}" : provider.DisplayName;
    }

    public string ProviderStatusText => _host.ProviderStatus.Text;
    public bool ProviderStatusOk => _host.ProviderStatus.Ok;

    public string GoogleStatus => _host.FreeServices.GoogleBlockedUntil is { } until
        ? Loc.F("Home.GoogleBlocked", until.ToString("t", Loc.Instance.Culture))
        : Loc.Instance["Home.GoogleAvailable"];

    public bool GoogleBlocked => _host.FreeServices.GoogleBlockedUntil is not null;

    public string MyMemoryText => Loc.F("Home.MyMemoryUsage", _host.FreeServices.MyMemoryToday, _host.FreeServices.MyMemoryLimit);

    public double MyMemoryFraction => _host.FreeServices.MyMemoryLimit > 0
        ? Math.Clamp((double)_host.FreeServices.MyMemoryToday / _host.FreeServices.MyMemoryLimit, 0, 1)
        : 0;

    /// <summary>MyMemory email, a Gemini free-tier key, a DeepL Free key, then Claude.</summary>
    public IReadOnlyList<Recommendation> Recommendations
    {
        get
        {
            var t = Current.Translation;
            bool Keyed(ProviderSettings s) => s.ApiKeyProtected.Length > 0;
            return
            [
                new(1, Loc.Instance["Home.Recommend.Email"], t.Free.MyMemoryEmail.Trim().Length > 0),
                new(2, Loc.Instance["Home.Recommend.Gemini"], Keyed(t.Gemini)),
                new(3, Loc.Instance["Home.Recommend.DeepL"], Keyed(t.DeepL)),
                new(4, Loc.Instance["Home.Recommend.Claude"], Keyed(t.Claude)),
            ];
        }
    }

    public IReadOnlyList<HotkeyRow> Hotkeys => Enum.GetValues<HotkeyAction>().Select(action =>
    {
        bool disabled = Current.Hotkeys.Disabled.Contains(action);
        var state = disabled ? HotkeyResult.Disabled : _host.HotkeyState(action);
        return new HotkeyRow(HotkeyText.ActionName(action), Current.Hotkeys[action], state is { } s ? HotkeyText.State(s) : "", state is null or HotkeyResult.Ok);
    }).ToList();

    public RelayCommand SelectRegionCommand { get; }
    public RelayCommand TranslateScreenCommand { get; }
    public RelayCommand HistoryCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand PreviewCommand { get; }

    /// <summary>Opens a page by its name (see <see cref="SettingsViewModel.RegionsPage"/>).</summary>
    public RelayCommand<string> OpenCommand { get; }

    // ---- the subtitles card ----

    /// <summary>"On · following the dialog · 142 lines this session", or how to turn them on.</summary>
    public string SubtitlesStatus => _host.SubtitlesOn
        ? Loc.F("Home.StatusOn", _host.HistoryRecords(int.MaxValue).Count)
        : Loc.F("Home.StatusOff", Current.Hotkeys[HotkeyAction.ToggleSubtitles]);

    SubtitleRecord? LastRecord => _host.HistoryRecords(1) is { Count: > 0 } records ? records[^1] : null;

    public bool HasLastLine => LastRecord is { Translation: { Length: > 0 } };
    public string LastTranslation => LastRecord?.Translation ?? "";
    public string LastOriginal => LastRecord?.Source ?? "";

    public string ReadChip => Current.Subtitles.Region is { } r ? Loc.F("Home.ChipRead", r.Width, r.Height) : Loc.Instance["Home.ChipNoRegion"];
    public string PlaceChip => Loc.Instance["Home.Place." + Current.Overlay.Position];
    public string ModeName => Loc.Instance["DisplayMode." + Current.Overlay.DisplayMode];
    public bool Shared => Current.Overlay.DisplayMode == DisplayMode.Shared;

    public string TimingChip => Current.Overlay.Stay switch
    {
        StayMode.UntilNext => Loc.Instance["Home.ChipUntilNext"],
        StayMode.ByLength => Loc.Instance["Home.ChipByLength"],
        _ => Loc.F("Home.ChipStay", Current.Overlay.StaySeconds.ToString("0.#", Loc.Instance.Culture)),
    };

    // ---- the cards under it ----

    public const double PreviewWidth = 256;
    public const double PreviewHeight = 144;

    public RegionPreview Preview => RegionPreview.For(Current, PreviewWidth, PreviewHeight);

    public string ModeDescription => Loc.Instance[Shared ? "Home.ModeSharedText" : "Home.ModePrivateText"];
    public string ModeHotkey => Current.Hotkeys[HotkeyAction.SwitchDisplayMode];

    public string StayValue => Current.Overlay.Stay switch
    {
        StayMode.UntilNext => Loc.Instance["Home.StayUntilNext"],
        StayMode.ByLength => Loc.F("Home.StayByLength", Current.Overlay.ReadingSpeed),
        _ => Loc.F("Timing.SecondsValue", Current.Overlay.StaySeconds.ToString("0.0", Loc.Instance.Culture)),
    };

    public string MinValue => Loc.F("Timing.SecondsValue", Current.Overlay.MinSeconds.ToString("0.0", Loc.Instance.Culture));
    public string FadeValue => Loc.F("Timing.SecondsValue", (Current.Overlay.FadeOutMs / 1000.0).ToString("0.0#", Loc.Instance.Culture));
    public string ClearValue => Loc.Instance[Current.Overlay.Clear == ClearMode.TextOnly ? "Home.ClearKeeps" : "Home.ClearStops"];

    /// <summary>The app's state changed: every text is read again.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
