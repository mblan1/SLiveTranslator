using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;
using SLiveTranslator.Translation.Providers;

namespace SLiveTranslator.UI.Pages;

/// <summary>
/// The Translation page: which provider translates, a card per provider (key, check, model), context and the free
/// services' email. Every card can be set up and checked before its provider is picked; a saved key is checked in the
/// background when its card is open on screen, the selected provider's card being open when the page shows.
/// </summary>
public sealed class TranslationViewModel : ObservableObject
{
    /// <summary>A Witcher line in the game's language for "Test translation".</summary>
    public const string SampleLine = "Wind's howling. We should find shelter for Roach before nightfall, Ciri.";

    public const int MinContextLines = 2, MaxContextLines = 10;

    readonly SettingsViewModel _root;
    readonly ISettingsHost _host;
    CancellationTokenSource? _test;
    string _testResult = "";
    bool _testing;
    bool _visible;

    public TranslationViewModel(SettingsViewModel root, ISettingsHost host, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _root = root;
        _host = host;
        Cards = host.Providers.Select(p => new ProviderCardViewModel(p, () => Section(p.Id), root.Edited, delay)).ToList();
        foreach (var card in Cards)
        {
            card.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProviderCardViewModel.IsExpanded) && card.IsExpanded && _visible) card.Activate();
            };
        }
        ProviderChoices = MakeProviderChoices();
        TestCommand = new AsyncRelayCommand(TestAsync);
        MarkInUse();
    }

    IReadOnlyList<Choice<string>> MakeProviderChoices() =>
        new[] { new Choice<string>(ProviderIds.Free, Loc.Instance["Translation.FreeProvider"]) }
            .Concat(_host.Providers.Select(p => new Choice<string>(p.Id, p.DisplayName))).ToList();

    TranslationSettings Settings => _root.Working.Translation;

    ProviderSettings Section(string id) => SectionOf(Settings, id);

    /// <summary>The section of <paramref name="settings"/> that holds the key and model of the provider <paramref name="id"/>.</summary>
    internal static ProviderSettings SectionOf(TranslationSettings settings, string id) => id switch
    {
        ProviderIds.Claude => settings.Claude,
        ProviderIds.OpenAI => settings.OpenAI,
        ProviderIds.Gemini => settings.Gemini,
        ProviderIds.Compat => settings.Compat,
        ProviderIds.DeepL => settings.DeepL,
        ProviderIds.Azure => settings.Azure,
        ProviderIds.GoogleCloud => settings.GoogleCloud,
        _ => throw new ArgumentException("no settings for the provider " + id, nameof(id)),
    };

    public IReadOnlyList<ProviderCardViewModel> Cards { get; }
    public IReadOnlyList<Choice<string>> ProviderChoices { get; private set; }

    /// <summary>The selected provider; one this version does not know shows as the free services and stays unless another is picked.</summary>
    public Choice<string> SelectedProvider
    {
        get => ProviderChoices.FirstOrDefault(c => c.Value.Equals(Settings.Provider, StringComparison.OrdinalIgnoreCase)) ?? ProviderChoices[0];
        set
        {
            if (value is null || value == SelectedProvider) return;
            Settings.Provider = value.Value;
            _root.Edited();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCard));
            OnPropertyChanged(nameof(IsFree));
            MarkInUse();
            OpenSelectedCard();
        }
    }

    /// <summary>The selected provider's card; null for the free services.</summary>
    public ProviderCardViewModel? SelectedCard => Cards.FirstOrDefault(c => c.Id.Equals(Settings.Provider, StringComparison.OrdinalIgnoreCase));

    public bool IsFree => SelectedCard is null;

    public string MyMemoryEmail
    {
        get => Settings.Free.MyMemoryEmail;
        set
        {
            if (Settings.Free.MyMemoryEmail == (value ?? "")) return;
            Settings.Free.MyMemoryEmail = value ?? "";
            Changed();
        }
    }

    public bool UseContext
    {
        get => Settings.UseContext;
        set
        {
            if (Settings.UseContext == value) return;
            Settings.UseContext = value;
            Changed();
        }
    }

    public double ContextLines
    {
        get => Math.Clamp(Settings.ContextLines, MinContextLines, MaxContextLines);
        set
        {
            int lines = (int)Math.Clamp(Math.Round(value), MinContextLines, MaxContextLines);
            if (Settings.ContextLines == lines) return;
            Settings.ContextLines = lines;
            Changed();
        }
    }

    // ---- test translation ----

    public AsyncRelayCommand TestCommand { get; }

    public bool Testing
    {
        get => _testing;
        private set => SetProperty(ref _testing, value);
    }

    public string TestResult
    {
        get => _testResult;
        private set => SetProperty(ref _testResult, value);
    }

    /// <summary>The sample line through the chain the page describes now, typed keys included: a real request, so only on the button.</summary>
    async Task TestAsync()
    {
        _test?.Cancel();
        var cts = _test = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var settings = SettingsStore.Clone(_root.Working);
        ApplyKeys(settings);
        Testing = true;
        TestResult = Loc.Instance["Translation.Testing"];
        try
        {
            var result = await _host.TestTranslationAsync(settings, SampleLine, cts.Token);
            if (_test == cts) TestResult = Loc.F("Translation.TestResult", result.Text, result.Provider) + (result.Notice is { } notice ? "\n" + notice : "");
        }
        catch (Exception ex)
        {
            // Every failure answers the button, a timeout too; a test a newer one replaced says nothing.
            if (_test == cts) TestResult = Loc.F("Translation.TestFailed", ex.Message);
        }
        finally
        {
            if (_test == cts) Testing = false;
        }
    }

    // ---- for the settings view model ----

    public bool HasPendingKeys => Cards.Any(c => c.HasPendingKey);

    /// <summary>Stores every typed key in <paramref name="settings"/>.</summary>
    public void ApplyKeys(AppSettings settings)
    {
        foreach (var card in Cards) card.ApplyKey(SectionOf(settings.Translation, card.Id));
    }

    /// <summary>The page is on screen: the selected provider's card opens, and the saved keys of the open cards are checked in the background.</summary>
    public void PageShown()
    {
        _visible = true;
        OpenSelectedCard();
        ActivateOpenCards();
    }

    /// <summary>The page left the screen: no saved key is checked until it shows again.</summary>
    public void PageHidden() => _visible = false;

    /// <summary>
    /// The settings being edited were replaced: saved or discarded, or changed by the app (<paramref name="keepTyped"/>:
    /// typed keys stay). A check a card dropped is made again while the page is on screen.
    /// </summary>
    public void Reload(bool keepTyped = false)
    {
        foreach (var card in Cards) card.Reset(keepTyped);
        MarkInUse();
        OnPropertyChanged(string.Empty);
        ActivateOpenCards();
    }

    /// <summary>The app's language changed: the provider list and the cards are written again in it.</summary>
    public void Relocalize()
    {
        ProviderChoices = MakeProviderChoices();
        OnPropertyChanged(nameof(ProviderChoices));
        OnPropertyChanged(nameof(SelectedProvider));
        foreach (var card in Cards) card.Relocalize();
    }

    void MarkInUse()
    {
        var selected = SelectedCard;
        foreach (var card in Cards) card.IsInUse = ReferenceEquals(card, selected);
    }

    void OpenSelectedCard()
    {
        if (SelectedCard is not { } card) return;
        if (card.IsExpanded && _visible) card.Activate();
        card.IsExpanded = true; // checks its saved key when the page is on screen
    }

    void ActivateOpenCards()
    {
        if (!_visible) return;
        foreach (var card in Cards.Where(c => c.IsExpanded)) card.Activate();
    }

    public void Cancel()
    {
        _test?.Cancel();
        foreach (var card in Cards) card.Cancel();
    }

    void Changed([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        _root.Edited();
    }
}
