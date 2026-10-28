using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Translation;

namespace SLiveTranslator.UI.Pages;

/// <summary>A subtitle line of the session in the history list.</summary>
public sealed class HistoryRowViewModel(SubtitleRecord record) : ObservableObject
{
    SubtitleRecord _record = record;

    public SubtitleRecord Record => _record;
    public int Index => _record.Id;
    public string Source => _record.Source;
    public string Translation => _record.Translation ?? Loc.Instance["History.Pending"];
    public string Provider => _record.Provider ?? "";

    public void Relocalize() => OnPropertyChanged(nameof(Translation));

    internal void Fixed(string translation)
    {
        _record = _record with { Translation = translation, Provider = ProviderKeys.User };
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// The History page: the latest subtitle lines, newest first, with their source, translation and provider. "Fix this
/// line" stores the user's translation as a hand fix in the cache, which answers first for every provider, and the
/// overlay shows it at once. The overlay itself stays click-through: the fix is made here.
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    public const int Lines = 200;

    readonly SettingsViewModel _root;
    readonly ISettingsHost _host;
    IReadOnlyList<HistoryRowViewModel> _rows = [];
    HistoryRowViewModel? _selected;
    string _fixText = "";

    public HistoryViewModel(SettingsViewModel root, ISettingsHost host)
    {
        _root = root;
        _host = host;
        RefreshCommand = new RelayCommand(Refresh);
        FixCommand = new RelayCommand(Fix, () => Selected is not null && FixText.Trim().Length > 0);
        Refresh();
    }

    static string Title => Loc.Instance["History.Fix"];

    public IReadOnlyList<HistoryRowViewModel> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public bool IsEmpty => Rows.Count == 0;

    public HistoryRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            FixText = value?.Record.Translation ?? "";
            FixCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The translation the user writes for the selected line; one line for each line of its source.</summary>
    public string FixText
    {
        get => _fixText;
        set
        {
            if (SetProperty(ref _fixText, value ?? "")) FixCommand.NotifyCanExecuteChanged();
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand FixCommand { get; }

    public void Refresh()
    {
        int? keep = _selected?.Index;
        string typed = _fixText;
        Rows = _host.HistoryRecords(Lines).Reverse().Select(r => new HistoryRowViewModel(r)).ToList();
        OnPropertyChanged(nameof(IsEmpty));
        Selected = Rows.FirstOrDefault(r => r.Index == keep);
        if (Selected is not null) FixText = typed; // the same line stays selected: what the user was typing stays too
    }

    void Fix()
    {
        if (Selected is not { } row) return;
        string text = string.Join("\n", FixText.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()));
        if (_host.Cache is not { } cache)
        {
            _root.Prompts.Show(Title, Loc.Instance["History.NoCache"], MessageKind.Error);
            return;
        }
        // The pair the line was translated between: the languages may have been changed since.
        var (src, tgt) = row.Record.Src.Length > 0 ? (row.Record.Src, row.Record.Tgt) : _host.Languages;
        try
        {
            cache.SetUserOverride(src, tgt, row.Source, text);
        }
        catch (ArgumentException)
        {
            _root.Prompts.Show(Title, Loc.F("History.FixLines", row.Source.Split('\n').Length), MessageKind.Error);
            return;
        }
        _host.ShowFix(row.Source, text);
        row.Fixed(text);
        _root.Prompts.Show(Title, Loc.Instance["History.Fixed"], MessageKind.Success);
    }

    /// <summary>The app's language changed: "waiting" is written again.</summary>
    public void Relocalize()
    {
        foreach (var row in Rows) row.Relocalize();
    }
}
