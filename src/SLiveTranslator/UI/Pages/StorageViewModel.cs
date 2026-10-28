using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Diagnostics;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;

namespace SLiveTranslator.UI.Pages;

/// <param name="Provider">The provider key ("claude/claude-opus-5-5", "user").</param>
public sealed record CacheStatRow(string Provider, string Entries, string Edited, string Size);

/// <summary>
/// The Storage & cache page: the data folder (<see cref="Folder"/>), the cache's statistics, size and clearing, the
/// cache manager (<see cref="Manager"/>) and the log (<see cref="Logging"/>). Every clear asks first.
/// </summary>
public sealed class StorageViewModel : SettingsPageViewModel
{
    public const int MinCacheEntries = 100, MaxCacheEntries = 1_000_000;

    readonly ISettingsHost _host;
    IReadOnlyList<CacheStatRow> _stats = [];
    IReadOnlyList<Choice<string>> _clearChoices = [];
    Choice<string>? _clearProvider;
    CacheStats? _numbers;

    /// <param name="logPath">The log file the viewer reads (tests give their own).</param>
    public StorageViewModel(SettingsViewModel root, ISettingsHost host, Func<string>? logPath = null) : base(root)
    {
        _host = host;
        Folder = new DataFolderViewModel(root, host);
        Manager = new CacheManagerViewModel(root, host, RefreshStats);
        Logging = new LogViewModel(root, host, logPath);
        ClearOrphansCommand = new AsyncRelayCommand(() => ClearAsync(CacheScope.Orphans), () => host.Cache is not null);
        ClearProviderCommand = new AsyncRelayCommand(() => ClearAsync(CacheScope.Provider), () => host.Cache is not null && _clearProvider is not null);
        ClearNonEditedCommand = new AsyncRelayCommand(() => ClearAsync(CacheScope.NonEdited), () => host.Cache is not null);
        ClearAllCommand = new AsyncRelayCommand(() => ClearAsync(CacheScope.All), () => host.Cache is not null);
        RefreshStats();
    }

    public DataFolderViewModel Folder { get; }
    public CacheManagerViewModel Manager { get; }
    public LogViewModel Logging { get; }

    // ---- cache statistics ----

    public IReadOnlyList<CacheStatRow> Stats
    {
        get => _stats;
        private set => SetProperty(ref _stats, value);
    }

    /// <summary>"4,300 entries · 1.2 MB · 12 edited by hand"; "not loaded" before the providers were built.</summary>
    public string StatsTotal => _numbers is { } n
        ? Loc.F("Cache.Total", n.Entries, ByteSize.Text(n.Bytes), n.Providers.Sum(p => p.Edited))
        : Loc.Instance["Cache.NotLoaded"];

    public double MaxEntries
    {
        get => Working.Storage.CacheMaxEntries;
        set => Set(Working.Storage.CacheMaxEntries, (int)Math.Clamp(Math.Round(value), MinCacheEntries, MaxCacheEntries), v => Working.Storage.CacheMaxEntries = v);
    }

    /// <summary>The providers with entries, for "Clear by provider".</summary>
    public IReadOnlyList<Choice<string>> ClearProviderChoices
    {
        get => _clearChoices;
        private set => SetProperty(ref _clearChoices, value);
    }

    public Choice<string>? SelectedClearProvider
    {
        get => _clearProvider;
        set
        {
            if (SetProperty(ref _clearProvider, value)) ClearProviderCommand.NotifyCanExecuteChanged();
        }
    }

    public AsyncRelayCommand ClearOrphansCommand { get; }
    public AsyncRelayCommand ClearProviderCommand { get; }
    public AsyncRelayCommand ClearNonEditedCommand { get; }
    public AsyncRelayCommand ClearAllCommand { get; }

    public void RefreshStats()
    {
        _numbers = _host.Cache?.Stats();
        var providers = _numbers?.Providers ?? [];
        Stats = providers.Select(p => new CacheStatRow(p.Provider, p.Entries.ToString("N0", Loc.Instance.Culture), p.Edited.ToString("N0", Loc.Instance.Culture),
            ByteSize.Text(p.Bytes))).ToList();
        string? kept = _clearProvider?.Value;
        ClearProviderChoices = providers.Select(p => new Choice<string>(p.Provider, p.Provider)).ToList();
        SelectedClearProvider = ClearProviderChoices.FirstOrDefault(c => c.Value == kept) ?? ClearProviderChoices.FirstOrDefault();
        OnPropertyChanged(nameof(StatsTotal));
    }

    /// <summary>How many entries the clear would remove, for its question.</summary>
    int Affected(CacheScope scope)
    {
        var providers = _numbers?.Providers ?? [];
        return scope switch
        {
            CacheScope.Orphans => providers.Where(p => ProviderKeys.IsOrphan(p.Provider)).Sum(p => p.Entries),
            CacheScope.Provider => providers.Where(p => p.Provider == _clearProvider?.Value).Sum(p => p.Entries),
            CacheScope.NonEdited => providers.Sum(p => p.Entries - p.Edited),
            _ => providers.Sum(p => p.Entries),
        };
    }

    async Task ClearAsync(CacheScope scope)
    {
        if (_host.Cache is not { } cache) return;
        string? provider = scope == CacheScope.Provider ? _clearProvider?.Value : null;
        if (scope == CacheScope.Provider && provider is null) return;
        string title = Loc.Instance["Cache.ClearTitle"];
        string question = Loc.F("Cache.Clear." + scope, Affected(scope), provider);
        if (!await Root.Prompts.ConfirmAsync(title, question, Loc.Instance["Cache.ClearYes"], Loc.Instance["Dialog.Cancel"])) return;
        int removed = cache.Clear(scope, provider);
        Root.Prompts.Show(title, Loc.F("Cache.Cleared", removed), MessageKind.Success);
        RefreshStats();
        Manager.Reload();
    }

    // ---- for the settings view model and the page ----

    /// <summary>The page is on screen: the folder, the cache and the log are read again.</summary>
    public void PageShown()
    {
        Folder.Refresh();
        RefreshStats();
        Manager.Reload();
        Logging.Refresh();
    }

    public override void Reload()
    {
        base.Reload();
        Logging.Reload();
    }

    /// <summary>The app's language changed: numbers, sizes and lists are written again.</summary>
    public void Relocalize()
    {
        RefreshStats();
        Folder.Refresh();
        Manager.Reload();
        Logging.Relocalize();
    }
}

/// <summary>The log part of the Storage page: its level, a viewer of its last lines with a filter, Clear, and "Copy diagnostics".</summary>
public sealed class LogViewModel : SettingsPageViewModel
{
    public const int ViewerLines = 500;

    readonly ISettingsHost _host;
    readonly Func<string> _path;
    IReadOnlyList<string> _all = [];
    string _filter = "";
    string _text = "";

    public LogViewModel(SettingsViewModel root, ISettingsHost host, Func<string>? path = null) : base(root)
    {
        _host = host;
        _path = path ?? (() => Log.FilePath);
        Levels = Choices<LogLevel>("LogLevel.");
        RefreshCommand = new RelayCommand(Refresh);
        ClearCommand = new RelayCommand(Clear);
        OpenFolderCommand = new RelayCommand(() => host.OpenFolder(host.DataPaths.Root));
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        Refresh();
    }

    public IReadOnlyList<Choice<LogLevel>> Levels { get; private set; }

    public Choice<LogLevel> SelectedLevel
    {
        get => Levels.FirstOrDefault(l => l.Value == Working.Storage.LogLevel) ?? Levels[0];
        set
        {
            if (value is not null) Set(Working.Storage.LogLevel, value.Value, v => Working.Storage.LogLevel = v);
        }
    }

    /// <summary>Only lines with this text (any case) show.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value ?? "")) return;
            ApplyFilter();
        }
    }

    /// <summary>The lines shown, newest last.</summary>
    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }

    public void Refresh()
    {
        _all = LogWriter.Tail(_path(), ViewerLines);
        ApplyFilter();
    }

    void ApplyFilter()
    {
        string filter = _filter.Trim();
        Text = string.Join(Environment.NewLine, filter.Length == 0 ? _all : _all.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    void Clear()
    {
        if (!LogWriter.Clear(_path())) Root.Prompts.Show(Loc.Instance["Logs.Title"], Loc.Instance["Logs.ClearFailed"], MessageKind.Error);
        Refresh();
    }

    /// <summary>Version, platform, OCR languages, data folder, the settings in use without keys, and the last log lines.</summary>
    void CopyDiagnostics()
    {
        string report = DiagnosticsReport.Build(_host.Current, _host.OcrLanguages, _host.DataPaths, LogWriter.Tail(_path(), DiagnosticsReport.LogLines));
        bool copied = Root.Prompts.CopyText(report);
        Root.Prompts.Show(Loc.Instance["Logs.Title"], Loc.Instance[copied ? "Logs.Copied" : "Logs.CopyFailed"], copied ? MessageKind.Success : MessageKind.Error);
    }

    /// <summary>The app's language changed: the level list is written again, the selection kept.</summary>
    public void Relocalize()
    {
        Levels = Choices<LogLevel>("LogLevel.");
        OnPropertyChanged(nameof(Levels));
        OnPropertyChanged(nameof(SelectedLevel));
    }
}
