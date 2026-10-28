using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Translation;

namespace SLiveTranslator.UI.Pages;

/// <summary>One cached translation in the cache manager; editing its translation is a hand fix.</summary>
public sealed class CacheRowViewModel : ObservableObject
{
    readonly CacheManagerViewModel _owner;

    internal CacheRowViewModel(CacheManagerViewModel owner, CacheEntry entry)
    {
        _owner = owner;
        Entry = entry;
    }

    public CacheEntry Entry { get; private set; }

    public string Provider => Entry.Provider;
    public string Pair => CacheManagerViewModel.PairOf(Entry);
    public string Source => Entry.Source;
    public bool Edited => Entry.Edited;
    public string Used => Entry.Used.ToLocalTime().ToString("g", Loc.Instance.Culture);

    /// <summary>A new text is the user's fix of the line: it answers for every provider from now on.</summary>
    public string Translation
    {
        get => Entry.Translation;
        set
        {
            string text = (value ?? "").Trim();
            if (text == Entry.Translation) return;
            if (_owner.Fix(this, text) is { } fixedEntry) Entry = fixedEntry;
            OnPropertyChanged(string.Empty);
        }
    }
}

/// <summary>
/// The cache manager of the Storage page: the cached translations, searched and filtered, in a virtualized list. A
/// translation edited in the list is stored as a hand fix (<see cref="CachedTranslator.SetUserOverride"/>), which
/// answers first whatever the provider; a row can be deleted; the cache can be exported to a file and a file imported
/// into it, where the user's own entries win.
/// </summary>
public sealed class CacheManagerViewModel : ObservableObject
{
    const string FileFilter = "JSON (*.json)|*.json";

    readonly SettingsViewModel _root;
    readonly ISettingsHost _host;
    readonly Action _changed;
    IReadOnlyList<CacheRowViewModel> _rows = [];
    IReadOnlyList<Choice<string?>> _providers = [];
    IReadOnlyList<Choice<string?>> _pairs = [];
    Choice<string?>? _provider;
    Choice<string?>? _pair;
    CacheRowViewModel? _selected;
    string _search = "";
    bool _editedOnly;
    int _total;

    /// <param name="changed">Entries were added or removed here (the page's statistics are read again).</param>
    public CacheManagerViewModel(SettingsViewModel root, ISettingsHost host, Action changed)
    {
        _root = root;
        _host = host;
        _changed = changed;
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null);
        ExportCommand = new RelayCommand(Export, () => Cache is not null);
        ImportCommand = new RelayCommand(Import, () => Cache is not null);
        RefreshCommand = new RelayCommand(Reload);
        Reload();
    }

    CachedTranslator? Cache => _host.Cache;
    IUserPrompts Prompts => _root.Prompts;
    static string Title => Loc.Instance["Cache.Manager"];

    public static string PairOf(CacheEntry entry) => $"{entry.SourceLang} → {entry.TargetLang}";

    public IReadOnlyList<CacheRowViewModel> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    /// <summary>"120 of 4,300 entries".</summary>
    public string CountText => Loc.F("Cache.Count", Rows.Count, _total);

    public string Search
    {
        get => _search;
        set
        {
            if (!SetProperty(ref _search, value ?? "")) return;
            Filter();
        }
    }

    public IReadOnlyList<Choice<string?>> ProviderChoices
    {
        get => _providers;
        private set => SetProperty(ref _providers, value);
    }

    public Choice<string?>? SelectedProvider
    {
        get => _provider;
        set
        {
            if (value is null || !SetProperty(ref _provider, value)) return;
            Filter();
        }
    }

    public IReadOnlyList<Choice<string?>> PairChoices
    {
        get => _pairs;
        private set => SetProperty(ref _pairs, value);
    }

    public Choice<string?>? SelectedPair
    {
        get => _pair;
        set
        {
            if (value is null || !SetProperty(ref _pair, value)) return;
            Filter();
        }
    }

    public bool EditedOnly
    {
        get => _editedOnly;
        set
        {
            if (!SetProperty(ref _editedOnly, value)) return;
            Filter();
        }
    }

    public CacheRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) DeleteCommand.NotifyCanExecuteChanged();
        }
    }

    public RelayCommand DeleteCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand RefreshCommand { get; }

    /// <summary>The cache changed as a whole (cleared, imported) or the page shows: the filter lists and the rows are made again.</summary>
    public void Reload()
    {
        var all = Cache?.Snapshot() ?? [];
        _total = all.Count;
        string? provider = _provider?.Value, pair = _pair?.Value;
        string any = Loc.Instance["Cache.All"];
        ProviderChoices = new[] { new Choice<string?>(null, any) }
            .Concat(all.Select(e => e.Provider).Distinct().Order(StringComparer.Ordinal).Select(p => new Choice<string?>(p, p))).ToList();
        PairChoices = new[] { new Choice<string?>(null, any) }
            .Concat(all.Select(PairOf).Distinct().Order(StringComparer.Ordinal).Select(p => new Choice<string?>(p, p))).ToList();
        _provider = ProviderChoices.FirstOrDefault(c => c.Value == provider) ?? ProviderChoices[0];
        _pair = PairChoices.FirstOrDefault(c => c.Value == pair) ?? PairChoices[0];
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedPair));
        ExportCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        Filter();
    }

    void Filter()
    {
        var filter = new CacheFilter(_search.Trim().Length > 0 ? _search.Trim() : null, _provider?.Value, _editedOnly ? true : null);
        var entries = Cache?.Snapshot(filter) ?? [];
        Rows = entries.Where(e => _pair?.Value is not { } pair || PairOf(e) == pair).Select(e => new CacheRowViewModel(this, e)).ToList();
        Selected = null;
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Stores the hand fix; an empty text removes the user's fix of the line.</summary>
    /// <returns>The entry the row shows now; null when nothing was stored (the user was told).</returns>
    internal CacheEntry? Fix(CacheRowViewModel row, string text)
    {
        var entry = row.Entry;
        if (Cache is not { } cache) return null;
        if (text.Length == 0 && entry.Provider != ProviderKeys.User) return null; // a provider's answer is deleted, not emptied
        try
        {
            cache.SetUserOverride(entry.SourceLang, entry.TargetLang, entry.Source, text);
            _changed();
        }
        catch (ArgumentException)
        {
            Prompts.Show(Title, Loc.Instance["Cache.FixLines"], MessageKind.Error);
            return null;
        }
        return entry with { Provider = ProviderKeys.User, Translation = text, Edited = true, AnsweredBy = ProviderKeys.User, Used = DateTimeOffset.Now };
    }

    void Delete()
    {
        if (Selected is not { } row || Cache is not { } cache) return;
        var e = row.Entry;
        if (cache.Remove(e.Provider, e.SourceLang, e.TargetLang, e.Source))
        {
            _total--;
            Rows = Rows.Where(r => !ReferenceEquals(r, row)).ToList();
            OnPropertyChanged(nameof(CountText));
            _changed();
        }
        Selected = null;
    }

    void Export()
    {
        if (Cache is not { } cache || Prompts.PickSaveFile("slivetranslator-cache.json", FileFilter) is not { } path) return;
        try
        {
            cache.Export(path);
            Prompts.Show(Title, Loc.F("Cache.Exported", Path.GetFileName(path)), MessageKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Prompts.Show(Title, Loc.F("Cache.ExportFailed", ex.Message), MessageKind.Error);
        }
    }

    void Import()
    {
        if (Cache is not { } cache || Prompts.PickOpenFile(FileFilter) is not { } path) return;
        try
        {
            int merged = cache.Import(path, out int kept);
            Prompts.Show(Title, kept > 0 ? Loc.F("Cache.ImportedKept", merged, Path.GetFileName(path), kept) : Loc.F("Cache.Imported", merged, Path.GetFileName(path)),
                MessageKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Prompts.Show(Title, Loc.F("Cache.ImportFailed", Path.GetFileName(path), ex.Message), MessageKind.Error);
        }
        _changed();
        Reload();
    }
}
