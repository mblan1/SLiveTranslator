using System;
using System.Collections.Generic;
using System.Linq;
using SLiveTranslator.Models;
using SLiveTranslator.UI.Pages;

namespace SLiveTranslator.UI;

public enum TrayCommand
{
    Separator, Status, Subtitles, TranslateScreen, History, SelectRegion, Clear, Providers, Provider, Mute, Open, OpenDataFolder, Exit,
}

/// <summary>One entry of the tray menu, as plain data: the app turns it into a menu item.</summary>
/// <param name="Gesture">The hotkey shown beside it; "" for none or one switched off.</param>
/// <param name="Value">The provider id of a <see cref="TrayCommand.Provider"/> item.</param>
/// <param name="Items">The submenu of <see cref="TrayCommand.Providers"/>.</param>
public sealed record TrayItem(TrayCommand Command, string Text = "", string Gesture = "", bool Checked = false, bool Enabled = true,
    string? Value = null, IReadOnlyList<TrayItem>? Items = null);

/// <param name="ProviderLine">"Claude  ·  claude-opus-5-5", as the Home page shows it.</param>
/// <param name="Providers">The providers to offer: the free services and the ones set up.</param>
/// <param name="Provider">The selected provider id.</param>
public sealed record TrayState(string ProviderLine, bool SubtitlesOn, bool HistoryVisible, bool Muted, HotkeySettings Hotkeys,
    IReadOnlyList<Choice<string>> Providers, string Provider);

/// <summary>
/// The tray menu for the app's state, in its language: a status line, the actions with their hotkeys, the provider
/// submenu (one checked), mute, the main window, the data folder and Exit. Built again each time it opens.
/// </summary>
public static class TrayMenu
{
    public static IReadOnlyList<TrayItem> Build(TrayState state)
    {
        var loc = Loc.Instance;
        string Gesture(HotkeyAction action) => state.Hotkeys.Disabled.Contains(action) ? "" : state.Hotkeys[action];
        var providers = state.Providers
            .Select(p => new TrayItem(TrayCommand.Provider, p.Label, Checked: p.Value.Equals(state.Provider, StringComparison.OrdinalIgnoreCase), Value: p.Value))
            .ToList();
        var separator = new TrayItem(TrayCommand.Separator);
        return
        [
            new(TrayCommand.Status, Loc.F("Tray.Status", state.ProviderLine, loc[state.SubtitlesOn ? "Tray.StatusOn" : "Tray.StatusOff"]), Enabled: false),
            separator,
            new(TrayCommand.Subtitles, loc["Tray.Subtitles"], Gesture(HotkeyAction.ToggleSubtitles), Checked: state.SubtitlesOn),
            new(TrayCommand.TranslateScreen, loc["Tray.TranslateScreen"], Gesture(HotkeyAction.TranslateScreen)),
            new(TrayCommand.History, loc["Tray.History"], Gesture(HotkeyAction.History), Checked: state.HistoryVisible),
            new(TrayCommand.SelectRegion, loc["Tray.SelectRegion"], Gesture(HotkeyAction.SelectRegion)),
            new(TrayCommand.Clear, loc["Tray.Clear"], Gesture(HotkeyAction.Clear)),
            separator,
            new(TrayCommand.Providers, loc["Tray.Provider"], Items: providers),
            new(TrayCommand.Mute, loc["Tray.Mute"], Checked: state.Muted),
            separator,
            new(TrayCommand.Open, loc["Tray.Open"]),
            new(TrayCommand.OpenDataFolder, loc["Tray.OpenDataFolder"]),
            separator,
            new(TrayCommand.Exit, loc["Tray.Exit"]),
        ];
    }
}
