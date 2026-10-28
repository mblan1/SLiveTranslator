using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;

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
    }

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

    /// <summary>"Claude · claude-opus-5-5"; the free services alone for an unknown provider.</summary>
    public string ProviderLine
    {
        get
        {
            var translation = Current.Translation;
            var provider = _host.Providers.FirstOrDefault(p => p.Id.Equals(translation.Provider, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return Loc.Instance["Translation.FreeProvider"];
            string model = TranslationViewModel.SectionOf(translation, provider.Id).Model.Trim();
            return model.Length > 0 ? $"{provider.DisplayName}  ·  {model}" : provider.DisplayName;
        }
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

    /// <summary>The app's state changed: every text is read again.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
