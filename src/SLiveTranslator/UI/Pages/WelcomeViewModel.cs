using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI.Pages;

/// <summary>A hotkey on the wizard's last step; it lights up once pressed.</summary>
public sealed class TrialHotkey(HotkeyAction action, string gesture) : ObservableObject
{
    bool _pressed;

    public HotkeyAction Action => action;
    public string Name => HotkeyText.ActionName(action);
    public string Gesture => gesture;

    public bool Pressed
    {
        get => _pressed;
        set => SetProperty(ref _pressed, value);
    }

    public void Relocalize() => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// The first-run wizard over the main window, also opened by "Run setup again": the language (shown at once), the
/// translation service with the Translation page's own card and key check (the free services are a valid choice), the
/// subtitle region, and a try of the hotkeys, which only light up while that step shows. It edits the settings of the
/// window; Finish or Skip saves them and marks the first run done.
/// </summary>
public sealed class WelcomeViewModel : ObservableObject
{
    public const int StepCount = 4;
    const int ProviderStep = 1, HotkeyStep = 3;

    readonly SettingsViewModel _root;
    readonly ISettingsHost _host;
    IReadOnlyList<TrialHotkey> _trial = [];
    bool _open;
    bool _languageShown; // the language step showed one that is not saved
    bool _trialOn;
    int _step;

    public WelcomeViewModel(SettingsViewModel root, ISettingsHost host)
    {
        _root = root;
        _host = host;
        Languages = GeneralViewModel.LanguageChoices();
        NextCommand = new RelayCommand(() => Step++, () => Step < StepCount - 1);
        BackCommand = new RelayCommand(() => Step--, () => Step > 0);
        FinishCommand = new RelayCommand(Finish);
        root.Translation.PropertyChanged += OnTranslationChanged;
    }

    public bool IsOpen
    {
        get => _open;
        private set => SetProperty(ref _open, value);
    }

    /// <summary>0 language, 1 provider, 2 region, 3 hotkeys.</summary>
    public int Step
    {
        get => _step;
        set
        {
            value = Math.Clamp(value, 0, StepCount - 1);
            if (_step == value) return;
            _step = value;
            OnStep();
        }
    }

    public string StepText => Loc.F("Welcome.StepOf", Step + 1, StepCount);
    public bool OnLanguage => Step == 0;
    public bool OnProvider => Step == ProviderStep;
    public bool OnRegion => Step == 2;
    public bool OnHotkeys => Step == HotkeyStep;
    public bool IsLast => Step == StepCount - 1;

    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }

    /// <summary>Finish on the last step, Skip on the others: both save what was set and end the first run.</summary>
    public RelayCommand FinishCommand { get; }

    // ---- step 1: language ----

    public IReadOnlyList<Choice<string>> Languages { get; private set; }

    public Choice<string> SelectedLanguage
    {
        get => Languages.FirstOrDefault(l => l.Value.Equals(_root.Working.Ui.Language.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
        set
        {
            if (value is null || value.Value == _root.Working.Ui.Language) return;
            _root.Working.Ui.Language = value.Value;
            _root.Edited();
            _languageShown = true;
            _host.ApplyLanguage(value.Value); // the rest of the wizard reads in it
            OnPropertyChanged();
        }
    }

    // ---- steps 2 and 3: the pages' own parts ----

    public TranslationViewModel Translation => _root.Translation;
    public SubtitlesViewModel Subtitles => _root.Subtitles;

    // ---- step 4: hotkeys ----

    public IReadOnlyList<TrialHotkey> Hotkeys
    {
        get => _trial;
        private set => SetProperty(ref _trial, value);
    }

    public void Open()
    {
        _step = 0;
        IsOpen = true;
        OnStep();
    }

    void Finish()
    {
        _root.Working.General.FirstRunDone = true;
        _root.Edited();
        if (!_root.Save()) return; // the user was told; the wizard stays to try again
        _languageShown = false;
        Close();
    }

    void Close()
    {
        Trial(false);
        IsOpen = false;
    }

    void OnStep()
    {
        Trial(IsOpen && OnHotkeys);
        if (IsOpen && OnProvider) _root.Translation.SelectedCard?.Activate();
        OnPropertyChanged(string.Empty);
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A key saved before is checked as soon as its provider is picked on the provider step.</summary>
    void OnTranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranslationViewModel.SelectedCard) && IsOpen && OnProvider) _root.Translation.SelectedCard?.Activate();
    }

    /// <summary>The hotkeys in use light up when pressed and do nothing else, while the step shows.</summary>
    void Trial(bool on)
    {
        if (on == _trialOn) return;
        _trialOn = on;
        _host.HotkeyTrial = on;
        if (!on)
        {
            _host.HotkeyPressed -= OnHotkeyPressed;
            return;
        }
        var hotkeys = _host.Current.Hotkeys;
        Hotkeys = Enum.GetValues<HotkeyAction>().Where(a => !hotkeys.Disabled.Contains(a)).Select(a => new TrialHotkey(a, hotkeys[a])).ToList();
        _host.HotkeyPressed += OnHotkeyPressed;
    }

    void OnHotkeyPressed(object? sender, HotkeyAction action)
    {
        if (Hotkeys.FirstOrDefault(h => h.Action == action) is { } hotkey) hotkey.Pressed = true;
    }

    /// <summary>The app's language changed: the language list is written again, the selection kept.</summary>
    public void Relocalize()
    {
        Languages = GeneralViewModel.LanguageChoices();
        OnPropertyChanged(nameof(Languages));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(StepText));
        foreach (var hotkey in Hotkeys) hotkey.Relocalize();
    }

    /// <summary>The window closes: the hotkeys work again, and a language shown but not saved gives way to the one in use.</summary>
    public void WindowClosed()
    {
        _root.Translation.PropertyChanged -= OnTranslationChanged;
        Close();
        if (_languageShown) _host.ApplyLanguage(_host.Current.Ui.Language);
    }
}
