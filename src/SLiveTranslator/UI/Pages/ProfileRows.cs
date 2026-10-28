using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Translation;

namespace SLiveTranslator.UI.Pages;

/// <summary>The rows of one grid, the selected row, and the Add and Remove buttons; tells about every edit.</summary>
public sealed class RowList<T> : ObservableObject where T : class, INotifyPropertyChanged
{
    readonly Func<T> _create;
    readonly Action _changed;
    T? _selected;

    public RowList(IEnumerable<T> rows, Func<T> create, Action changed)
    {
        _create = create;
        _changed = changed;
        Items = new ObservableCollection<T>(rows);
        foreach (var row in Items) row.PropertyChanged += OnRowChanged;
        Items.CollectionChanged += OnItemsChanged;
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove, () => _selected is not null);
    }

    public ObservableCollection<T> Items { get; }

    public T? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) RemoveCommand.NotifyCanExecuteChanged();
        }
    }

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }

    void Add()
    {
        var row = _create();
        Items.Add(row);
        Selected = row;
    }

    void Remove()
    {
        if (_selected is not { } row) return;
        int index = Items.IndexOf(row);
        Items.Remove(row);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<T>() ?? []) row.PropertyChanged -= OnRowChanged;
        foreach (var row in e.NewItems?.OfType<T>() ?? []) row.PropertyChanged += OnRowChanged;
        _changed();
    }

    void OnRowChanged(object? sender, PropertyChangedEventArgs e) => _changed();
}

/// <summary>A name or term of the keep list.</summary>
public sealed class KeepRow(string text) : ObservableObject
{
    string _text = text;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value ?? "");
    }
}

/// <summary>A glossary entry; it edits its own copy, so fields this version does not know stay with it.</summary>
public sealed class GlossaryRow(GlossaryEntry entry) : ObservableObject
{
    public GlossaryEntry Entry { get; } = entry;

    public string En
    {
        get => Entry.En;
        set => SetProperty(Entry.En, value ?? "", Entry, (e, v) => e.En = v);
    }

    public string Vi
    {
        get => Entry.Vi;
        set => SetProperty(Entry.Vi, value ?? "", Entry, (e, v) => e.Vi = v);
    }

    public string Note
    {
        get => Entry.Note ?? "";
        set => SetProperty(Entry.Note ?? "", value ?? "", Entry, (e, v) => e.Note = v.Length == 0 ? null : v);
    }
}

/// <summary>How a character speaks to one listener.</summary>
public sealed class AddressRow(AddressPair pair) : ObservableObject
{
    public AddressPair Pair { get; } = pair;

    public string To
    {
        get => Pair.To;
        set => SetProperty(Pair.To, value ?? "", Pair, (p, v) => p.To = v);
    }

    public string Self
    {
        get => Pair.Self;
        set => SetProperty(Pair.Self, value ?? "", Pair, (p, v) => p.Self = v);
    }

    public string Other
    {
        get => Pair.Other;
        set => SetProperty(Pair.Other, value ?? "", Pair, (p, v) => p.Other = v);
    }
}

/// <summary>A character with the pronoun pairs they use, edited in a grid of their own.</summary>
public sealed class CharacterRow : ObservableObject
{
    public CharacterRow(GameCharacter character, Action changed)
    {
        Character = character;
        Address = new RowList<AddressRow>(character.Address.Select(a => new AddressRow(a)), () => new AddressRow(new AddressPair()), changed);
    }

    public GameCharacter Character { get; }
    public RowList<AddressRow> Address { get; }

    public string Name
    {
        get => Character.Name;
        set => SetProperty(Character.Name, value ?? "", Character, (c, v) => c.Name = v);
    }

    public string Gender
    {
        get => Character.Gender;
        set => SetProperty(Character.Gender, value ?? "", Character, (c, v) => c.Gender = v);
    }

    public string About
    {
        get => Character.About;
        set => SetProperty(Character.About, value ?? "", Character, (c, v) => c.About = v);
    }

    /// <summary>The character with the pairs as the grid holds them, blank ones left out.</summary>
    public GameCharacter Build()
    {
        Character.Address = Address.Items.Select(a => a.Pair).Where(a => a.To.Trim().Length > 0).ToList();
        return Character;
    }
}

/// <summary>A fix rule; its "unless" words are one comma-separated text in the grid.</summary>
public sealed class FixRuleRow(FixRule rule) : ObservableObject
{
    string _unless = rule.Unless is { } words ? string.Join(", ", words) : "";

    public FixRule Rule { get; } = rule;

    public string Trigger
    {
        get => Rule.Trigger;
        set => SetProperty(Rule.Trigger, value ?? "", Rule, (r, v) => r.Trigger = v);
    }

    public string Wrong
    {
        get => Rule.Wrong;
        set => SetProperty(Rule.Wrong, value ?? "", Rule, (r, v) => r.Wrong = v);
    }

    public string Right
    {
        get => Rule.Right;
        set => SetProperty(Rule.Right, value ?? "", Rule, (r, v) => r.Right = v);
    }

    /// <summary>As typed; the rule gets the words between the commas.</summary>
    public string Unless
    {
        get => _unless;
        set
        {
            if (!SetProperty(ref _unless, value ?? "")) return;
            var words = _unless.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Rule.Unless = words.Count == 0 ? null : words;
        }
    }
}
