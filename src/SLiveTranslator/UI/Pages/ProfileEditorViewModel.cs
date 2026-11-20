using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;

namespace SLiveTranslator.UI.Pages;

/// <summary>
/// The Game profile page: which profile translation uses (by the game, none, or one), and grids to edit a profile's
/// keep list, glossary, characters with their pronoun pairs, and fix rules. The grids edit a copy; Save writes it
/// through <see cref="GameProfileStore"/>, which raises its version, so its cached translations are made again.
/// </summary>
public sealed class ProfileEditorViewModel : ObservableObject
{
    const string FileFilter = "JSON (*.json)|*.json";

    readonly SettingsViewModel _root;
    readonly GameProfileStore _store;
    GameProfile? _target; // the store's profile being edited
    GameProfile? _copy;
    string _savedJson = "";
    string _name = "";
    string _processNames = "";
    bool _isDirty;
    bool _loading;

    public ProfileEditorViewModel(SettingsViewModel root, GameProfileStore store)
    {
        _root = root;
        _store = store;
        NewCommand = new RelayCommand(NewProfile, () => !IsDirty);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsDirty);
        ExportCommand = new RelayCommand(Export, () => _copy is not null);
        RemoveProfileCommand = new AsyncRelayCommand(RemoveProfileAsync, () => _target is not null);
        ClearProfileCommand = new AsyncRelayCommand(ClearProfileAsync, () => _copy is not null);
        Keep = Rows<KeepRow>([], () => new KeepRow(""));
        Glossary = Rows<GlossaryRow>([], () => new GlossaryRow(new GlossaryEntry()));
        Characters = Rows<CharacterRow>([], () => new CharacterRow(new GameCharacter(), Edited));
        FixRules = Rows<FixRuleRow>([], () => new FixRuleRow(new FixRule()));
        Load(DefaultTarget());
    }

    TranslationSettings Settings => _root.Working.Translation;

    // ---- which profile translation uses ----

    /// <summary>Auto (by the game), None, then every profile.</summary>
    public IReadOnlyList<Choice<string>> UseChoices =>
        new[] { new Choice<string>(GameProfileStore.Auto, Loc.Instance["Profile.Auto"]), new Choice<string>(GameProfileStore.Off, Loc.Instance["Profile.None"]) }
            .Concat(_store.Profiles.Select(p => new Choice<string>(p.Id, p.Name.Length > 0 ? p.Name : p.Id))).ToList();

    public Choice<string>? SelectedUse
    {
        get => UseChoices.FirstOrDefault(c => c.Value.Equals(Settings.ProfileId.Trim(), StringComparison.OrdinalIgnoreCase)) ?? UseChoices[0];
        set
        {
            if (value is null || value.Value.Equals(Settings.ProfileId.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            Settings.ProfileId = value.Value;
            OnPropertyChanged();
            _root.Edited();
        }
    }

    // ---- the profile being edited ----

    public IReadOnlyList<GameProfile> Profiles => _store.Profiles;

    /// <summary>The profile in the grids; another one can be picked only while the grids hold no unsaved edit.</summary>
    public GameProfile? EditTarget
    {
        get => _target;
        set
        {
            if (value is null || ReferenceEquals(value, _target) || IsDirty) return;
            Load(value);
        }
    }

    public bool CanSwitch => !IsDirty;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? "")) Edited();
        }
    }

    /// <summary>The game's executable names, comma-separated: the profile is used when the game in front is one of them.</summary>
    public string ProcessNames
    {
        get => _processNames;
        set
        {
            if (SetProperty(ref _processNames, value ?? "")) Edited();
        }
    }

    public RowList<KeepRow> Keep { get; private set; }
    public RowList<GlossaryRow> Glossary { get; private set; }
    public RowList<CharacterRow> Characters { get; private set; }
    public RowList<FixRuleRow> FixRules { get; private set; }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!SetProperty(ref _isDirty, value)) return;
            OnPropertyChanged(nameof(CanSwitch));
            NewCommand.NotifyCanExecuteChanged();
            ImportCommand.NotifyCanExecuteChanged();
        }
    }

    public RelayCommand NewCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand ExportCommand { get; }

    /// <summary>Deletes the profile's file (after asking); the setting that picked it goes back to automatic.</summary>
    public AsyncRelayCommand RemoveProfileCommand { get; }

    /// <summary>Empties the profile's lists (after asking): keep, glossary, characters, fix rules and style; saved with Save.</summary>
    public AsyncRelayCommand ClearProfileCommand { get; }

    /// <summary>False when there is no profile at all (none ships with the app): the page asks for New or Import.</summary>
    public bool HasProfile => _copy is not null;

    static string Title => Loc.Instance["Profile.Title"];

    static string NameOf(GameProfile profile) => profile.Name.Trim().Length > 0 ? profile.Name.Trim() : profile.Id;

    /// <summary>The edited profile as it would be saved: the grids' rows, blank ones left out.</summary>
    public GameProfile? Build()
    {
        if (_copy is null) return null;
        var profile = GameProfileStore.Copy(_copy);
        profile.Name = _name.Trim();
        profile.ProcessNames = Split(_processNames);
        profile.Keep = Keep.Items.Select(k => k.Text.Trim()).Where(k => k.Length > 0).ToList();
        profile.Glossary = Glossary.Items.Select(g => g.Entry).Where(g => g.En.Trim().Length > 0).ToList();
        profile.Characters = Characters.Items.Select(c => c.Build()).Where(c => c.Name.Trim().Length > 0).ToList();
        profile.FixRules = FixRules.Items.Select(r => r.Rule).Where(r => r.Wrong.Length > 0).ToList();
        return GameProfileStore.Copy(profile);
    }

    /// <summary>Writes the edited profile; false when it could not be written (the user was told).</summary>
    public bool Save()
    {
        if (!IsDirty || Build() is not { } profile) return true;
        try
        {
            _store.Save(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _root.Prompts.Show(Loc.Instance["Profile.Title"], Loc.F("Profile.SaveFailed", ex.Message), MessageKind.Error);
            return false;
        }
        Load(profile);
        return true;
    }

    /// <summary>Back to the saved profile.</summary>
    public void Discard()
    {
        if (_target is not null) Load(_store.Find(_target.Id) ?? _target);
    }

    /// <summary>The settings being edited were replaced: the profile in use may have changed.</summary>
    public void Reload()
    {
        OnPropertyChanged(nameof(UseChoices));
        OnPropertyChanged(nameof(SelectedUse));
    }

    GameProfile? DefaultTarget() => _store.Find(Settings.ProfileId) ?? _store.Profiles.FirstOrDefault();

    void Load(GameProfile? target)
    {
        _loading = true;
        _target = target;
        _copy = target is null ? null : GameProfileStore.Copy(target);
        _name = _copy?.Name ?? "";
        _processNames = string.Join(", ", _copy?.ProcessNames ?? []);
        Keep = Rows(_copy?.Keep.Select(k => new KeepRow(k)) ?? [], () => new KeepRow(""));
        Glossary = Rows(_copy?.Glossary.Select(g => new GlossaryRow(g)) ?? [], () => new GlossaryRow(new GlossaryEntry()));
        Characters = Rows(_copy?.Characters.Select(c => new CharacterRow(c, Edited)) ?? [], () => new CharacterRow(new GameCharacter(), Edited));
        FixRules = Rows(_copy?.FixRules.Select(r => new FixRuleRow(r)) ?? [], () => new FixRuleRow(new FixRule()));
        _savedJson = Json(Build());
        _loading = false;
        IsDirty = false;
        OnPropertyChanged(string.Empty);
        ExportCommand.NotifyCanExecuteChanged();
        RemoveProfileCommand.NotifyCanExecuteChanged();
        ClearProfileCommand.NotifyCanExecuteChanged();
        _root.Edited();
    }

    async Task RemoveProfileAsync()
    {
        if (_target is not { } target) return;
        string name = NameOf(target);
        if (!await _root.Prompts.ConfirmAsync(Title, Loc.F("Profile.RemoveAsk", name), Loc.Instance["Profile.RemoveProfile"], Loc.Instance["Dialog.Cancel"]))
            return;
        try
        {
            _store.Delete(target.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _root.Prompts.Show(Title, Loc.F("Profile.RemoveFailed", name, ex.Message), MessageKind.Error);
            return;
        }
        // The setting named it: translation goes back to picking a profile by the game (saved with the other settings).
        if (Settings.ProfileId.Trim().Equals(target.Id, StringComparison.OrdinalIgnoreCase)) Settings.ProfileId = GameProfileStore.Auto;
        OnPropertyChanged(nameof(Profiles));
        Reload();
        Load(_store.Profiles.FirstOrDefault());
        _root.Prompts.Show(Title, Loc.F("Profile.Removed", name), MessageKind.Success);
    }

    async Task ClearProfileAsync()
    {
        if (_copy is null || _target is null) return;
        if (!await _root.Prompts.ConfirmAsync(Title, Loc.F("Profile.ClearAsk", NameOf(_target)), Loc.Instance["Profile.ClearProfile"], Loc.Instance["Dialog.Cancel"]))
            return;
        _copy.Style = []; // no grid of its own: it goes with the rest
        // New lists rather than Items.Clear(): a reset would leave the old rows' change handlers attached.
        Keep = Rows<KeepRow>([], () => new KeepRow(""));
        Glossary = Rows<GlossaryRow>([], () => new GlossaryRow(new GlossaryEntry()));
        Characters = Rows<CharacterRow>([], () => new CharacterRow(new GameCharacter(), Edited));
        FixRules = Rows<FixRuleRow>([], () => new FixRuleRow(new FixRule()));
        OnPropertyChanged(nameof(Keep));
        OnPropertyChanged(nameof(Glossary));
        OnPropertyChanged(nameof(Characters));
        OnPropertyChanged(nameof(FixRules));
        Edited();
    }

    RowList<T> Rows<T>(IEnumerable<T> rows, Func<T> create) where T : ObservableObject => new(rows, create, Edited);

    void Edited()
    {
        if (_loading) return;
        IsDirty = Json(Build()) != _savedJson;
        _root.Edited();
    }

    static string Json(GameProfile? profile) => profile is null ? "" : JsonSerializer.Serialize(profile, GameProfileStore.JsonOptions);

    static List<string> Split(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    void NewProfile()
    {
        string id = _store.NewId("custom");
        string target = _root.Working.General.TargetLanguage.Trim();
        var profile = new GameProfile { Id = id, Name = Loc.Instance["Profile.NewName"], TargetLanguage = target.Length > 0 ? target : "vi" };
        try
        {
            _store.Save(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _root.Prompts.Show(Loc.Instance["Profile.Title"], Loc.F("Profile.SaveFailed", ex.Message), MessageKind.Error);
            return;
        }
        OnPropertyChanged(nameof(Profiles));
        Reload();
        Load(profile);
    }

    async Task ImportAsync()
    {
        if (_root.Prompts.PickOpenFile(FileFilter) is not { } path) return;
        GameProfile imported;
        try
        {
            imported = GameProfileStore.Parse(File.ReadAllBytes(path));
            if (imported.Id.Trim().Length == 0) throw new JsonException(Loc.Instance["Profile.NoId"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _root.Prompts.Show(Loc.Instance["Profile.Title"], Loc.F("Profile.ImportFailed", Path.GetFileName(path), ex.Message), MessageKind.Error);
            return;
        }
        if (_store.Find(imported.Id) is { } existing
            && !await _root.Prompts.ConfirmAsync(Loc.Instance["Profile.Title"], Loc.F("Profile.ReplaceAsk", existing.Name, imported.Id),
                Loc.Instance["Profile.Replace"], Loc.Instance["Dialog.Cancel"]))
            return;
        try
        {
            _store.Save(imported);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _root.Prompts.Show(Loc.Instance["Profile.Title"], Loc.F("Profile.ImportFailed", Path.GetFileName(path), ex.Message), MessageKind.Error);
            return;
        }
        OnPropertyChanged(nameof(Profiles));
        Reload();
        Load(imported);
        _root.Prompts.Show(Loc.Instance["Profile.Title"], Loc.F("Profile.Imported", imported.Name), MessageKind.Success);
    }

    void Export()
    {
        if (Build() is not { } profile || _root.Prompts.PickSaveFile(profile.Id + ".json", FileFilter) is not { } path) return;
        try
        {
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(profile, GameProfileStore.JsonOptions));
            _root.Prompts.Show(Loc.Instance["Profile.Title"], Loc.F("Profile.Exported", Path.GetFileName(path)), MessageKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _root.Prompts.Show(Loc.Instance["Profile.Title"], Loc.F("Profile.ExportFailed", ex.Message), MessageKind.Error);
        }
    }
}
