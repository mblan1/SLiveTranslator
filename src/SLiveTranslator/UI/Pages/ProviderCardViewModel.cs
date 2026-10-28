using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;
using SLiveTranslator.Translation.Providers;

namespace SLiveTranslator.UI.Pages;

/// <summary>Where a provider's key check is (the model combo state machine of the settings page).</summary>
public enum KeyCheckState
{
    /// <summary>Nothing to check with: the key (or a compatible server's address) is missing.</summary>
    NoKey,

    /// <summary>Enough to check with, not checked yet: a check starts <see cref="ProviderCardViewModel.AutoCheckDelay"/> after typing stops.</summary>
    Typed,

    Checking,

    /// <summary>The key works: the combo holds what it can use.</summary>
    Valid,

    /// <summary>The check failed: the combo stays empty and disabled, the info bar says why.</summary>
    Failed,
}

/// <summary>A value of a combo box with its text in the app's language.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A model or engine in the combo, with its tags written out.</summary>
/// <param name="Available">False for a saved model the key does not list: it stays selected, with a warning, until the user picks another.</param>
public sealed record ModelChoice(string Id, string Text, ModelTags Tags, string? CostHint, bool Available = true)
{
    /// <summary>"★ Claude Opus 5.5  ·  Recommended", "⚡ Claude Haiku 5.5  ·  Fastest", "Gemini 2.0 Flash (deprecated)".</summary>
    public static ModelChoice From(ModelOption option)
    {
        var tags = option.Tags;
        string text = tags.HasFlag(ModelTags.Recommended) ? "★ " : tags.HasFlag(ModelTags.Fastest) ? "⚡ " : "";
        text += option.Label;
        if (tags.HasFlag(ModelTags.Deprecated) && !option.Label.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
            text += " " + Loc.Instance["Model.Deprecated"];
        var names = new[] { ModelTags.Recommended, ModelTags.Balanced, ModelTags.Fastest }.Where(t => tags.HasFlag(t)).Select(t => Loc.Instance["Model." + t]).ToList();
        if (names.Count > 0) text += "  ·  " + string.Join(", ", names);
        return new ModelChoice(option.Id, text, tags, option.CostHint);
    }

    public static ModelChoice Missing(string id) => new(id, Loc.F("Card.ModelUnavailable", id), ModelTags.None, null, Available: false);

    public override string ToString() => Text;
}

/// <summary>
/// One provider's card on the Translation page: its key and other fields, the key check and the model combo. The
/// combo is empty and disabled until a check of the key in the box succeeds; typing starts a check
/// <see cref="AutoCheckDelay"/> after the last key press and cancels the one before. After a check the saved model
/// stays selected (shown as not available when the key does not list it), the recommended one is pre-selected only
/// when nothing was saved, and the model is never switched on the user's behalf. The key box never shows a saved key:
/// it stays empty with a "saved" hint, and what is typed replaces the saved key on Save.
/// </summary>
public sealed class ProviderCardViewModel : ObservableObject
{
    public static readonly TimeSpan AutoCheckDelay = TimeSpan.FromMilliseconds(800);

    /// <summary>What a state change can change on the card; the key box is not among them, so its caret stays where it is.</summary>
    static readonly string[] StateProperties =
    [
        nameof(State), nameof(IsChecking), nameof(IsKeyReadOnly), nameof(CanCheck), nameof(IsModelEnabled), nameof(SelectedModel), nameof(ModelHint),
        nameof(InfoOpen), nameof(InfoKind), nameof(InfoTitle), nameof(InfoMessage), nameof(UsageText), nameof(UsageFraction), nameof(ShowUsage),
        nameof(SavedModelWarning), nameof(OfflineNote), nameof(CostHint), nameof(KeyPlaceholder), nameof(HasSavedKey), nameof(SavedKeyUnreadable),
        nameof(CanRemoveKey),
    ];

    readonly ITranslationProvider _provider;
    readonly Func<ProviderSettings> _section;
    readonly Action _edited;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    CancellationTokenSource? _work; // the pending automatic check, then the check itself
    int _generation;
    string _keyText = "";
    bool _keyEdited;
    bool _removeKey;
    bool _expanded;
    bool _inUse;
    string _savedKey = "";
    ProviderCheck? _result;
    ProviderCredentials? _resultFor;
    ProviderCredentials? _checkingFor;
    ModelChoice? _selected;

    /// <param name="section">The provider's section of the settings being edited; read each time, since a save or a discard replaces them.</param>
    /// <param name="edited">Called after the card changed the settings (a model picked, a field typed).</param>
    /// <param name="delay">Waits out <see cref="AutoCheckDelay"/> (tests replace the clock).</param>
    public ProviderCardViewModel(ITranslationProvider provider, Func<ProviderSettings> section, Action edited,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _provider = provider;
        _section = section;
        _edited = edited;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        CheckCommand = new RelayCommand(() => _ = CheckAsync(), () => CanCheck);
        RemoveKeyCommand = new RelayCommand(RemoveKey, () => CanRemoveKey && !IsChecking);
        (EffortChoices, PresetChoices) = MakeChoices();
        _savedKey = Section.ApiKey;
        State = Ready ? KeyCheckState.Typed : KeyCheckState.NoKey;
    }

    ProviderSettings Section => _section();

    public string Id => _provider.Id;
    public string DisplayName => _provider.DisplayName;
    public ProviderKind Kind => _provider.Kind;

    bool Has(ProviderField field) => _provider.Fields.Any(f => f.Field == field);

    public bool ShowRegion => Has(ProviderField.Region);
    public bool ShowBaseUrl => Has(ProviderField.BaseUrl);
    public bool ShowProjectId => Has(ProviderField.ProjectId);
    public bool IsClaude => _provider.Id == ProviderIds.Claude;
    public bool IsGemini => _provider.Id == ProviderIds.Gemini;
    public bool IsCompat => _provider.Id == ProviderIds.Compat;

    /// <summary>The card's details are open on the page: the Translation page checks a saved key only then.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set => SetProperty(ref _expanded, value);
    }

    /// <summary>The provider selected to translate.</summary>
    public bool IsInUse
    {
        get => _inUse;
        set => SetProperty(ref _inUse, value);
    }

    public string ModelLabel => Loc.Instance[Kind == ProviderKind.MT ? "Card.Engine" : "Card.Model"];
    public string KeyLabel => Loc.Instance[_provider.Fields.Any(f => f is { Field: ProviderField.ApiKey, Required: false }) ? "Card.KeyOptional" : "Card.Key"];

    // ---- fields ----

    /// <summary>
    /// What the key box holds; empty while the saved key is in use. Emptying the box gives the saved key back: only
    /// <see cref="RemoveKeyCommand"/> removes it, so a stray key press in the box never deletes a working key.
    /// </summary>
    public string KeyText
    {
        get => _keyText;
        set
        {
            value ??= "";
            if (_keyText == value) return;
            _keyText = value;
            _removeKey = false;
            _keyEdited = value.Trim().Length > 0 || _savedKey.Length == 0;
            OnPropertyChanged();
            _edited(); // a typed key is an edit to save, though it stays here until then
            CredentialsChanged();
        }
    }

    public bool HasSavedKey => !_keyEdited && _savedKey.Length > 0;

    /// <summary>A key is saved but cannot be decrypted on this Windows account: it has to be entered again.</summary>
    public bool SavedKeyUnreadable => !_keyEdited && Section.ApiKeyProtected.Length > 0 && Section.ApiKeyUnreadable;

    public string KeyPlaceholder => _removeKey ? Loc.Instance["Card.KeyRemoved"]
        : HasSavedKey ? Loc.Instance["Card.KeySaved"]
        : SavedKeyUnreadable ? Loc.Instance["Card.KeyUnreadable"]
        : "";

    /// <summary>
    /// A typed key that differs from the saved one, a removed key, or anything typed over a key that cannot be
    /// decrypted (an emptied box then removes it); <see cref="ApplyKey"/> stores it.
    /// </summary>
    public bool HasPendingKey => _keyEdited && (_keyText.Trim() != _savedKey || Section.ApiKeyUnreadable);

    /// <summary>A key is saved, readable or not, and nothing is typed over it.</summary>
    public bool CanRemoveKey => HasSavedKey || SavedKeyUnreadable;

    /// <summary>The saved key goes on Save; typing a key again keeps it, with the typed one.</summary>
    public RelayCommand RemoveKeyCommand { get; }

    void RemoveKey()
    {
        if (!CanRemoveKey) return;
        _keyText = "";
        _keyEdited = true;
        _removeKey = true;
        OnPropertyChanged(nameof(KeyText));
        _edited();
        CredentialsChanged();
    }

    public string Region
    {
        get => (Section as AzureProviderSettings)?.Region ?? "";
        set => SetField(s => (s as AzureProviderSettings)?.Region, (s, v) => ((AzureProviderSettings)s).Region = v, value);
    }

    public string BaseUrl
    {
        get => (Section as CompatProviderSettings)?.BaseUrl ?? "";
        set
        {
            if (Section is not CompatProviderSettings compat || compat.BaseUrl == (value ?? "")) return;
            compat.BaseUrl = value ?? "";
            compat.Preset = CompatPresets.Of(compat.BaseUrl)?.Id ?? "";
            OnPropertyChanged(nameof(SelectedPreset));
            FieldChanged(nameof(BaseUrl));
        }
    }

    public string ProjectId
    {
        get => (Section as GeminiProviderSettings)?.ProjectId ?? "";
        set => SetField(s => (s as GeminiProviderSettings)?.ProjectId, (s, v) => ((GeminiProviderSettings)s).ProjectId = v, value);
    }

    public IReadOnlyList<Choice<string>> PresetChoices { get; private set; }

    /// <summary>The OpenAI-compatible server the address belongs to; picking one fills in its address.</summary>
    public Choice<string>? SelectedPreset
    {
        get
        {
            string id = (Section as CompatProviderSettings)?.Preset is { Length: > 0 } preset ? preset : CompatPresets.Of(BaseUrl)?.Id ?? "";
            return PresetChoices.FirstOrDefault(c => c.Value == id);
        }
        set
        {
            if (value is null || Section is not CompatProviderSettings compat || value.Value.Length == 0 || compat.Preset == value.Value) return;
            compat.Preset = value.Value;
            compat.BaseUrl = CompatPresets.All.First(p => p.Id == value.Value).BaseUrl;
            OnPropertyChanged();
            FieldChanged(nameof(BaseUrl));
        }
    }

    public IReadOnlyList<Choice<LlmEffort>> EffortChoices { get; private set; }

    public Choice<LlmEffort>? SelectedEffort
    {
        get => Section is ClaudeProviderSettings claude ? EffortChoices.FirstOrDefault(c => c.Value == claude.Effort) : null;
        set
        {
            if (value is null || Section is not ClaudeProviderSettings claude || claude.Effort == value.Value) return;
            claude.Effort = value.Value;
            OnPropertyChanged();
            _edited();
        }
    }

    void SetField(Func<ProviderSettings, string?> get, Action<ProviderSettings, string> set, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        value ??= "";
        if (get(Section) is not { } current || current == value) return;
        set(Section, value);
        FieldChanged(name!);
    }

    void FieldChanged(string name)
    {
        OnPropertyChanged(name);
        _edited();
        CredentialsChanged();
    }

    /// <summary>What a check or a translator gets: the typed key, else the saved one.</summary>
    public ProviderCredentials Credentials => new(_keyEdited ? _keyText.Trim() : _savedKey, Region.Trim(), BaseUrl.Trim(), ProjectId.Trim());

    /// <summary>Every required field is filled in, and the key too when these credentials need one.</summary>
    bool Ready
    {
        get
        {
            var credentials = Credentials;
            foreach (var spec in _provider.Fields.Where(f => f.Required))
            {
                string value = spec.Field switch
                {
                    ProviderField.ApiKey => credentials.ApiKey,
                    ProviderField.Region => credentials.Region,
                    ProviderField.BaseUrl => credentials.BaseUrl,
                    _ => credentials.ProjectId,
                };
                if (value.Length == 0) return false;
            }
            return !_provider.NeedsKey(credentials) || credentials.ApiKey.Length > 0;
        }
    }

    // ---- the state machine ----

    public KeyCheckState State { get; private set; }

    public bool IsChecking => State == KeyCheckState.Checking;
    public bool IsKeyReadOnly => IsChecking;
    public bool CanCheck => Ready && !IsChecking;
    public RelayCommand CheckCommand { get; }

    public ObservableCollection<ModelChoice> Models { get; } = new();
    public bool IsModelEnabled => State == KeyCheckState.Valid && Models.Count > 0;

    /// <summary>The model in the combo. Picking one stores it in the settings being edited; the combo emptying itself stores nothing.</summary>
    public ModelChoice? SelectedModel
    {
        get => _selected;
        set
        {
            if (value is null || value == _selected) return;
            _selected = value;
            if (Section.Model != value.Id)
            {
                Section.Model = value.Id;
                _edited();
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(CostHint));
            OnPropertyChanged(nameof(SavedModelWarning));
            OnPropertyChanged(nameof(InfoKind));
        }
    }

    public string ModelHint => State switch
    {
        KeyCheckState.NoKey => Loc.Instance[ShowBaseUrl ? "Card.Hint.NeedAddress" : "Card.Hint.NeedKey"],
        KeyCheckState.Typed => Loc.Instance["Card.Hint.PressCheck"],
        KeyCheckState.Valid when Models.Count == 0 => Loc.Instance["Card.Hint.NoModels"],
        _ => "",
    };

    public bool InfoOpen => State is KeyCheckState.Checking or KeyCheckState.Valid or KeyCheckState.Failed;

    public MessageKind InfoKind => State switch
    {
        KeyCheckState.Checking => MessageKind.Info,
        KeyCheckState.Valid => _result?.Detail.Length > 0 || SavedModelWarning.Length > 0 ? MessageKind.Warning : MessageKind.Success,
        _ => _result?.Status is CheckStatus.RateLimited or CheckStatus.Network or CheckStatus.Timeout ? MessageKind.Warning : MessageKind.Error,
    };

    /// <summary>The localized summary of the check.</summary>
    public string InfoTitle => State == KeyCheckState.Checking ? Loc.Instance["Card.Checking"]
        : _result is { } result && State is KeyCheckState.Valid or KeyCheckState.Failed ? ProviderCheckText.Summary(Kind, result) : "";

    /// <summary>The provider's own message, untranslated.</summary>
    public string InfoMessage => State is KeyCheckState.Valid or KeyCheckState.Failed ? _result?.Detail ?? "" : "";

    public bool ShowUsage => State == KeyCheckState.Valid && _result?.Usage is not null;

    public string UsageText => ShowUsage ? UsageTextOf(_result!.Usage!) : "";

    /// <summary>The share of the limit used, 0 to 1; 0 without a limit.</summary>
    public double UsageFraction => ShowUsage && _result!.Usage is { Limit: > 0 } usage ? Math.Clamp(usage.Used / usage.Limit.Value, 0, 1) : 0;

    /// <summary>The saved model is not on the key's list: it is kept, and the user is told.</summary>
    public string SavedModelWarning => State == KeyCheckState.Valid && _selected is { Available: false } missing ? Loc.F("Card.SavedModelMissing", missing.Id) : "";

    /// <summary>The check could not reach the service: translation still goes to the saved model.</summary>
    public string OfflineNote => State == KeyCheckState.Failed && _result?.Status is CheckStatus.Network or CheckStatus.Timeout
                                 && Section.Model.Trim() is { Length: > 0 } saved
        ? Loc.F("Card.OfflineSavedModel", saved)
        : "";

    public string CostHint => _selected?.CostHint is { } cost ? Loc.F("Card.Price", cost) : "";

    /// <summary>The card is on screen: a saved key that was not checked yet is checked in the background.</summary>
    public void Activate()
    {
        if (State == KeyCheckState.Typed && !_keyEdited) _ = CheckAsync();
    }

    /// <summary>Checks the credentials now, cancelling a check under way or waiting to start.</summary>
    public Task CheckAsync()
    {
        int generation = Restart(out var ct);
        return Ready ? RunCheckAsync(generation, ct) : Task.CompletedTask;
    }

    /// <summary>Stops the check under way or waiting to start (the window closes).</summary>
    public void Cancel()
    {
        _generation++;
        _work?.Cancel();
        _work = null;
        if (State == KeyCheckState.Checking) SetState(Ready ? KeyCheckState.Typed : KeyCheckState.NoKey);
    }

    /// <summary>Stores the typed key in <paramref name="target"/> when it differs from the saved one ("" for a removed key).</summary>
    public void ApplyKey(ProviderSettings target)
    {
        if (HasPendingKey) target.ApiKey = _keyText;
    }

    /// <summary>The app's language changed: the combos' texts and the card's own texts are made again, the selections kept.</summary>
    public void Relocalize()
    {
        (EffortChoices, PresetChoices) = MakeChoices();
        OnPropertyChanged(nameof(EffortChoices));
        OnPropertyChanged(nameof(PresetChoices));
        OnPropertyChanged(nameof(SelectedEffort));
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(ModelLabel));
        OnPropertyChanged(nameof(KeyLabel));
        if (State == KeyCheckState.Valid && _result is { } result) FillModels(result.Options, preselect: false);
        RaiseState();
    }

    static (IReadOnlyList<Choice<LlmEffort>>, IReadOnlyList<Choice<string>>) MakeChoices() =>
    (
        Enum.GetValues<LlmEffort>().Select(e => new Choice<LlmEffort>(e, Loc.Instance["Effort." + e])).ToList(),
        CompatPresets.All.Select(p => new Choice<string>(p.Id, p.Name)).Append(new Choice<string>("", Loc.Instance["Card.PresetCustom"])).ToList()
    );

    /// <summary>
    /// The settings being edited were replaced. After a save or a discard (<paramref name="keepTyped"/> false) the key
    /// box shows the saved key again; when the app changed settings of its own, a typed key stays. A check of these very
    /// credentials stays (with the saved model selected again), and so does one under way for them; any other result
    /// is dropped. A typed key without a result is then checked as after typing; the saved key is checked again by
    /// <see cref="Activate"/>, when the card is on screen.
    /// </summary>
    public void Reset(bool keepTyped = false)
    {
        if (!keepTyped)
        {
            _keyEdited = false;
            _keyText = "";
            _removeKey = false;
        }
        _savedKey = Section.ApiKey;
        OnPropertyChanged(nameof(KeyText));
        OnPropertyChanged(nameof(Region));
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(ProjectId));
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(SelectedEffort));
        if (State == KeyCheckState.Checking && Credentials == _checkingFor)
        {
            RaiseState();
            return;
        }
        if (_result is { } result && Credentials == _resultFor && State is KeyCheckState.Valid or KeyCheckState.Failed)
        {
            if (State == KeyCheckState.Valid) FillModels(result.Options, preselect: false);
            RaiseState();
            return;
        }
        int generation = Restart(out var ct);
        ClearResult();
        SetState(Ready ? KeyCheckState.Typed : KeyCheckState.NoKey);
        if (State == KeyCheckState.Typed && _keyEdited) _ = CheckAfterDelayAsync(generation, ct);
    }

    void CredentialsChanged()
    {
        int generation = Restart(out var ct);
        ClearResult();
        if (!Ready)
        {
            SetState(KeyCheckState.NoKey);
            return;
        }
        SetState(KeyCheckState.Typed);
        _ = CheckAfterDelayAsync(generation, ct);
    }

    async Task CheckAfterDelayAsync(int generation, CancellationToken ct)
    {
        try
        {
            await _delay(AutoCheckDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (generation == _generation && !ct.IsCancellationRequested) await RunCheckAsync(generation, ct);
    }

    async Task RunCheckAsync(int generation, CancellationToken ct)
    {
        var credentials = _checkingFor = Credentials;
        ClearResult();
        SetState(KeyCheckState.Checking);
        ProviderCheck result;
        try
        {
            result = await _provider.CheckAsync(credentials, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // A provider reports its own problems as a result; anything else still ends the check.
            Log.Warn($"settings: the {_provider.Id} key check failed: {ex.Message}");
            result = ProviderCheck.Fail(CheckStatus.Failed, ex.Message);
        }
        if (generation != _generation || ct.IsCancellationRequested) return; // newer typing or a newer check took over

        _work = null;
        _result = result;
        _resultFor = credentials;
        if (result.Status == CheckStatus.Ok)
        {
            FillModels(result.Options, preselect: true);
            SetState(KeyCheckState.Valid);
        }
        else
        {
            SetState(KeyCheckState.Failed);
        }
    }

    /// <summary>The key's models in the combo, with the saved one selected; the recommended one only when nothing was saved.</summary>
    void FillModels(IReadOnlyList<ModelOption> options, bool preselect)
    {
        _selected = null;
        Models.Clear();
        foreach (var option in options) Models.Add(ModelChoice.From(option));
        string saved = Section.Model.Trim();
        if (saved.Length > 0)
        {
            var pick = Models.FirstOrDefault(m => m.Id == saved);
            if (pick is null)
            {
                pick = ModelChoice.Missing(saved);
                Models.Insert(0, pick);
            }
            _selected = pick;
        }
        else if (preselect && Models.FirstOrDefault(m => m.Tags.HasFlag(ModelTags.Recommended)) is { } recommended)
        {
            _selected = recommended;
            Section.Model = recommended.Id;
            _edited();
        }
    }

    int Restart(out CancellationToken ct)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        ct = _work.Token;
        return ++_generation;
    }

    void ClearResult()
    {
        _result = null;
        _resultFor = null;
        _selected = null;
        Models.Clear();
    }

    void SetState(KeyCheckState state)
    {
        State = state;
        RaiseState();
    }

    void RaiseState()
    {
        foreach (var name in StateProperties) OnPropertyChanged(name);
        CheckCommand.NotifyCanExecuteChanged();
        RemoveKeyCommand.NotifyCanExecuteChanged();
    }

    static string UsageTextOf(ProviderUsage usage)
    {
        bool money = usage.Unit == "USD";
        if (usage.Limit is not { } limit) return Loc.F(money ? "Card.Usage.UsdUnlimited" : "Card.Usage.CharsUnlimited", usage.Used);
        return Loc.F(money ? "Card.Usage.Usd" : "Card.Usage.Chars", usage.Used, limit);
    }
}
