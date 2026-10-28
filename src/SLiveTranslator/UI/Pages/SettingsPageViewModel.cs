using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI.Pages;

/// <summary>A page whose values live in the settings being edited: each edit tells the shared view model.</summary>
public abstract class SettingsPageViewModel(SettingsViewModel root) : ObservableObject
{
    protected SettingsViewModel Root { get; } = root;
    protected AppSettings Working => Root.Working;

    /// <summary>Stores <paramref name="value"/> when it differs from <paramref name="current"/>, and tells the page and the shared view model.</summary>
    protected void Set<T>(T current, T value, Action<T> store, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        store(value);
        OnPropertyChanged(name);
        Root.Edited();
    }

    /// <summary>The settings being edited were replaced: every value is read again.</summary>
    public void Reload() => OnPropertyChanged(string.Empty);

    protected static IReadOnlyList<Choice<T>> Choices<T>(string prefix) where T : struct, Enum =>
        Enum.GetValues<T>().Select(v => new Choice<T>(v, Loc.Instance[prefix + v])).ToList();
}
