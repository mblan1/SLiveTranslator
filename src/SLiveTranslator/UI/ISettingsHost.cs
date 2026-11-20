using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.Updates;

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

    /// <summary>The app changed and saved its settings itself (a region selected by hotkey, a provider picked in the tray).</summary>
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

    /// <summary>The read region and where the translation goes, picked on the screen (both steps).</summary>
    void SelectRegion();

    /// <summary>The picker with only some of its steps (see <see cref="RegionPickMode"/>).</summary>
    void SelectRegions(RegionPickMode mode) => SelectRegion();

    /// <summary>
    /// As <see cref="SelectRegions(RegionPickMode)"/>, from the main window: the picker starts from its unsaved
    /// settings, so it follows the display mode chosen there (in Shared mode the box keeps off the read region).
    /// </summary>
    void SelectRegions(RegionPickMode mode, AppSettings working) => SelectRegions(mode);

    /// <summary>What the OCR reads in the region now (the Regions page's test); null when it cannot read.</summary>
    Task<ReadPreview?> TestReadingAsync(RegionSettings region, CancellationToken ct) => Task.FromResult<ReadPreview?>(null);

    /// <summary>Clear, as its hotkey does (see <see cref="OverlaySettings.Clear"/>).</summary>
    void ClearOverlay()
    {
    }

    /// <summary>Private overlay and Shared on screen, as its hotkey switches them: saved at once.</summary>
    void SwitchDisplayMode()
    {
    }

    void TranslateScreen();
    void ToggleHistory();

    /// <summary>Shows a sample subtitle in this look for a few seconds.</summary>
    void PreviewOverlay(OverlaySettings look);

    // ---- hotkeys ----

    /// <summary>Takes every hotkey off until <see cref="ResumeHotkeys"/>: a recorder has the focus, so pressing one records it.</summary>
    void SuspendHotkeys();

    /// <summary>Registers again what <see cref="SuspendHotkeys"/> took off; nothing happens when nothing is suspended.</summary>
    void ResumeHotkeys();

    /// <summary>Whether <paramref name="gesture"/> could be registered now for <paramref name="action"/> (another program may hold it).</summary>
    HotkeyResult ProbeHotkey(string gesture, HotkeyAction action);

    /// <summary>While true, a hotkey press only raises <see cref="HotkeyPressed"/> and does nothing else (the welcome wizard's try-out).</summary>
    bool HotkeyTrial { get; set; }

    /// <summary>A registered hotkey was pressed (on the UI thread).</summary>
    event EventHandler<HotkeyAction>? HotkeyPressed;

    // ---- notifications ----

    /// <summary>One notice of each enabled category, in this look (see <see cref="Notifier.SendTestAsync"/>).</summary>
    Task SendTestNotices(NotificationSettings look);

    // ---- storage ----

    /// <summary>The data folder in use.</summary>
    DataPaths DataPaths { get; }

    /// <summary>Copies and moves the data folder, and writes location.json and portable.flag.</summary>
    DataFolderMover DataFolders { get; }

    /// <summary>The translation cache of the run; null before the providers were built.</summary>
    CachedTranslator? Cache { get; }

    /// <summary>Writes the settings, the cache and the usage count to the data folder now, before it is copied.</summary>
    void FlushData();

    /// <summary>
    /// The data was copied to <paramref name="paths"/>: settings, cache, profiles, usage count and log are written
    /// there from now on, without a restart.
    /// </summary>
    void UseDataFolder(DataPaths paths);

    /// <summary>Starts the app again (to load the data of another folder) and exits this one.</summary>
    void Restart();

    void OpenFolder(string path);

    // ---- general ----

    /// <summary>"Start with Windows": the Run value in the registry.</summary>
    StartupRegistration Startup { get; }

    /// <summary>Writes or removes the Run value at once, and keeps the setting along with it.</summary>
    void SetStartWithWindows(bool on);

    /// <summary>The Windows OCR languages installed ("en-US").</summary>
    IReadOnlyList<string> OcrLanguages { get; }

    /// <summary>Shows the app in this language at once, without saving it (the welcome wizard's first step); "" follows Windows.</summary>
    void ApplyLanguage(string setting);

    /// <summary>The last check for an update; null before the first one ended, <see cref="UpdateCheck.Disabled"/> for a build that does not update itself.</summary>
    UpdateCheck? LastUpdateCheck => UpdateCheck.Disabled;

    DateTime? LastUpdateCheckTime => null;

    bool CheckingForUpdate => false;

    /// <summary>Checks now; a newer version brings up the update window (and closes the main window).</summary>
    Task<UpdateCheck> CheckForUpdatesAsync() => Task.FromResult(UpdateCheck.Disabled);

    // ---- history ----

    /// <summary>The latest subtitle lines of the session, oldest first.</summary>
    IReadOnlyList<SubtitleRecord> HistoryRecords(int lines);

    /// <summary>The language pair of the subtitles: what a hand fix is stored for.</summary>
    (string Src, string Tgt) Languages { get; }

    /// <summary>The overlay shows the user's fix on every line with this source (the cache holds it already).</summary>
    void ShowFix(string source, string translation);

    /// <summary>Takes one line out of the session's history (and off the screen when it shows there).</summary>
    void RemoveHistory(int id)
    {
    }

    /// <summary>Forgets every line of the session: the history and the lines on screen. The translation cache keeps its translations.</summary>
    void ClearHistory()
    {
    }
}
