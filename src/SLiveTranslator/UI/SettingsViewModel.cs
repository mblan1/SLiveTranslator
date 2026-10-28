using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;
using SLiveTranslator.UI.Pages;

namespace SLiveTranslator.UI;

/// <summary>
/// The one view model behind every page of the main window. The pages edit <see cref="Working"/>, a copy of the
/// settings in use; Save hands the app a copy of it with only what was edited here merged into the settings in use
/// (so a region selected by hotkey meanwhile is kept), and names the parts that changed, so the app redoes only those.
/// Typed API keys stay in the provider cards until Save.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    readonly ISettingsHost _host;
    readonly INotifyPropertyChanged _language;
    readonly bool _ready;
    JsonNode _original;
    bool _hasChanges;
    bool _typing;

    /// <param name="delay">The provider cards' wait before an automatic key check (tests replace the clock).</param>
    /// <param name="language">Tells when the app's language changed: <see cref="Loc.Instance"/> (tests raise their own).</param>
    public SettingsViewModel(ISettingsHost host, Func<TimeSpan, CancellationToken, Task>? delay = null, INotifyPropertyChanged? language = null)
    {
        _host = host;
        _language = language ?? Loc.Instance;
        var current = host.Current;
        _original = SettingsStore.ToNode(current);
        Working = SettingsStore.Clone(current);
        Home = new HomeViewModel(host);
        Translation = new TranslationViewModel(this, host, delay);
        Profiles = new ProfileEditorViewModel(this, host.Profiles);
        Subtitles = new SubtitlesViewModel(this, host);
        Overlay = new OverlayViewModel(this, host);
        Hotkeys = new HotkeysViewModel(this, host);
        Notifications = new NotificationsViewModel(this, host);
        Storage = new StorageViewModel(this, host);
        General = new GeneralViewModel(this, host);
        History = new HistoryViewModel(this, host);
        Welcome = new WelcomeViewModel(this, host);
        // Not while the data folder is copied: a profile saved meanwhile would stay behind in the old folder.
        SaveCommand = new RelayCommand(() => Save(), () => HasChanges && !Storage.Folder.IsBusy);
        DiscardCommand = new RelayCommand(Discard, () => HasChanges);
        Storage.Folder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataFolderViewModel.IsBusy)) SaveCommand.NotifyCanExecuteChanged();
        };
        host.SettingsChanged += OnSettingsChanged;
        host.StateChanged += OnStateChanged;
        _language.PropertyChanged += OnLanguageChanged;
        _ready = true;
    }

    /// <summary>The settings the pages edit.</summary>
    public AppSettings Working { get; private set; }

    /// <summary>Messages and questions go to the window once it is open.</summary>
    public IUserPrompts Prompts { get; set; } = NoPrompts.Instance;

    public HomeViewModel Home { get; }
    public TranslationViewModel Translation { get; }
    public ProfileEditorViewModel Profiles { get; }
    public SubtitlesViewModel Subtitles { get; }
    public OverlayViewModel Overlay { get; }
    public HotkeysViewModel Hotkeys { get; }
    public NotificationsViewModel Notifications { get; }
    public StorageViewModel Storage { get; }
    public GeneralViewModel General { get; }
    public HistoryViewModel History { get; }

    /// <summary>The first-run wizard over the window (see <see cref="WelcomeViewModel.Open"/>).</summary>
    public WelcomeViewModel Welcome { get; }

    public RelayCommand SaveCommand { get; }
    public RelayCommand DiscardCommand { get; }

    /// <summary>Something was edited that is not saved yet: a setting, a typed key, the game profile.</summary>
    public bool HasChanges
    {
        get => _hasChanges;
        private set
        {
            if (!SetProperty(ref _hasChanges, value)) return;
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>A number is typed into a box that takes it only on Enter or when left: the Save bar shows meanwhile.</summary>
    public bool Typing
    {
        get => _typing;
        set
        {
            if (_typing == value) return;
            _typing = value;
            Edited();
        }
    }

    /// <summary>A page changed <see cref="Working"/>, a key or a profile.</summary>
    public void Edited()
    {
        if (!_ready) return;
        HasChanges = _typing || !JsonNode.DeepEquals(SettingsStore.ToNode(Working), _original) || Translation.HasPendingKeys || Profiles.IsDirty;
    }

    /// <summary>What a save would change in the running app (for the tests and the save itself).</summary>
    public SettingsChange PendingChanges => SettingsDiff.Between(_host.Current, Merged());

    /// <summary>Stores the edits and has the app apply them; false when they could not be stored (the user was told).</summary>
    public bool Save()
    {
        if (Storage.Folder.IsBusy)
        {
            Prompts.Show(Loc.Instance["Storage.Folder"], Loc.Instance["Storage.BusySave"], MessageKind.Warning);
            return false;
        }
        if (!Profiles.Save()) return false;
        var merged = Merged();
        var changes = SettingsDiff.Between(_host.Current, merged);
        var result = changes == SettingsChange.None ? new SaveResult(true, []) : _host.Save(merged, changes);
        if (!result.Saved)
        {
            Prompts.Show(Loc.Instance["Main.SaveFailedTitle"], string.Join("\n", result.Problems), MessageKind.Error);
            Edited();
            return false;
        }
        Replace(SettingsStore.Clone(merged));
        if (result.Problems.Count > 0) Prompts.Show(Loc.Instance["Main.SavedWithProblems"], string.Join("\n", result.Problems), MessageKind.Warning);
        else Prompts.Show(Loc.Instance["Main.Saved"], "", MessageKind.Success);
        return true;
    }

    /// <summary>Drops every edit: the pages show the settings in use again.</summary>
    public void Discard()
    {
        Profiles.Discard();
        Replace(SettingsStore.Clone(_host.Current));
    }

    /// <summary>
    /// The edits merged into the settings in use: what the pages left as it was keeps the app's version (a region
    /// selected meanwhile), what they changed wins. Typed keys are encrypted into it.
    /// </summary>
    AppSettings Merged()
    {
        var edited = SettingsStore.Clone(Working);
        Translation.ApplyKeys(edited);
        return SettingsStore.FromNode(SettingsStore.MergeEdits(_original, SettingsStore.ToNode(edited), SettingsStore.ToNode(_host.Current))!);
    }

    /// <summary>
    /// The app saved settings of its own: they show on the pages, and edits made here stay, an item removed from a list
    /// included. Where both changed the same value, the app's is shown: it is the one in use.
    /// </summary>
    void OnSettingsChanged(object? sender, EventArgs e)
    {
        var current = SettingsStore.ToNode(_host.Current);
        var working = SettingsStore.MergeEdits(_original, current, SettingsStore.ToNode(Working))!;
        _original = current;
        Working = SettingsStore.FromNode(working);
        ReloadPages(keepTyped: true);
        Edited();
        Home.Refresh();
    }

    void OnStateChanged(object? sender, EventArgs e) => Home.Refresh();

    /// <summary>The texts bound by key follow by themselves; the lists and texts the pages made are made again in the new language.</summary>
    void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != Binding.IndexerName) return;
        Translation.Relocalize();
        Profiles.Reload();
        Subtitles.Relocalize();
        Overlay.Relocalize();
        Hotkeys.Relocalize();
        Notifications.Relocalize();
        Storage.Relocalize();
        General.Relocalize();
        History.Relocalize();
        Welcome.Relocalize();
        Home.Refresh();
    }

    void Replace(AppSettings settings)
    {
        _original = SettingsStore.ToNode(settings);
        Working = settings;
        ReloadPages();
        Edited();
        Home.Refresh();
    }

    void ReloadPages(bool keepTyped = false)
    {
        Translation.Reload(keepTyped);
        Profiles.Reload();
        Subtitles.Reload();
        Overlay.Reload();
        Hotkeys.Reload();
        Notifications.Reload();
        Storage.Reload();
        General.Reload();
    }

    public void Dispose()
    {
        _host.SettingsChanged -= OnSettingsChanged;
        _host.StateChanged -= OnStateChanged;
        _language.PropertyChanged -= OnLanguageChanged;
        Translation.Cancel();
        Storage.Folder.Cancel();
        Hotkeys.EndRecording();
        Welcome.WindowClosed();
        Prompts = NoPrompts.Instance; // what finishes after the window closed has nobody to tell
    }
}
