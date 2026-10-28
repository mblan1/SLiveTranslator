using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;

namespace SLiveTranslator.UI;

/// <param name="Saved">False when the file could not be written: nothing was applied, and the window keeps its changes.</param>
/// <param name="Problems">What did not work after a save (a hotkey in use, a provider without a key), already logged.</param>
public sealed record SaveResult(bool Saved, IReadOnlyList<string> Problems);

/// <param name="GoogleBlockedUntil">Local time Google's block on this network ends; null while it is not blocked.</param>
public sealed record FreeServiceStatus(DateTime? GoogleBlockedUntil, long MyMemoryToday, int MyMemoryLimit);

/// <param name="Ok">False when the selected provider cannot be used or is not answering.</param>
public sealed record ProviderStatus(string Text, bool Ok);

/// <param name="Provider">The provider key that answered ("claude/claude-opus-5-5", "free-mt").</param>
/// <param name="Notice">Why the selected provider did not answer (no key, no model); null when it did.</param>
public sealed record TestTranslation(string Text, string Provider, string? Notice);

/// <summary>What the main window needs from the running app: the settings in use, the services behind them, live state and actions.</summary>
public interface ISettingsHost
{
    /// <summary>The settings in use.</summary>
    AppSettings Current { get; }

    /// <summary>Stores <paramref name="settings"/> and makes them the ones in use, redoing only what <paramref name="changes"/> names.</summary>
    SaveResult Save(AppSettings settings, SettingsChange changes);

    /// <summary>The app changed and saved its settings itself (a region selected by hotkey, the classic window).</summary>
    event EventHandler? SettingsChanged;

    /// <summary>Something on the Home page changed: subtitles on or off, a provider's status, the free services' quota.</summary>
    event EventHandler? StateChanged;

    IReadOnlyList<ITranslationProvider> Providers { get; }
    GameProfileStore Profiles { get; }

    /// <summary>Translates one line through the chain <paramref name="settings"/> describe, without the cache: a real request.</summary>
    Task<TestTranslation> TestTranslationAsync(AppSettings settings, string line, CancellationToken ct);

    bool SubtitlesOn { get; }
    void SetSubtitles(bool on);
    ProviderStatus ProviderStatus { get; }
    FreeServiceStatus FreeServices { get; }

    /// <summary>Whether the action's hotkey is registered; null before hotkeys were registered.</summary>
    HotkeyResult? HotkeyState(HotkeyAction action);

    void SelectRegion();
    void TranslateScreen();
    void ToggleHistory();

    /// <summary>Shows a sample subtitle in this look for a few seconds.</summary>
    void PreviewOverlay(OverlaySettings look);
}
