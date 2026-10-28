using System;
using System.Text.Json.Nodes;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI;

/// <summary>What a settings save has to redo in the running app; everything else reads the settings when it needs them.</summary>
[Flags]
public enum SettingsChange
{
    None = 0,

    /// <summary>The provider chain: the translation section, the target language, the cache size.</summary>
    Translation = 1 << 0,

    /// <summary>The OCR engine: its language.</summary>
    Ocr = 1 << 1,

    /// <summary>The subtitle watcher, restarted when it runs: its section, the target language, the OCR engine.</summary>
    Subtitles = 1 << 2,

    /// <summary>The look of the overlay, restyled at once.</summary>
    Overlay = 1 << 3,

    Hotkeys = 1 << 4,
    Notifications = 1 << 5,
    Language = 1 << 6,
    Theme = 1 << 7,

    /// <summary>The log's level and size.</summary>
    Log = 1 << 8,

    /// <summary>Saved, with nothing to redo (the first-run flag; Start with Windows, which the General page applies at once).</summary>
    Other = 1 << 9,
}

public static class SettingsDiff
{
    /// <summary>What changed from <paramref name="before"/> to <paramref name="after"/>, compared in the file's form.</summary>
    public static SettingsChange Between(AppSettings before, AppSettings after)
    {
        var a = SettingsStore.ToNode(before);
        var b = SettingsStore.ToNode(after);
        bool Changed(string section, string? field = null) =>
            !JsonNode.DeepEquals(field is null ? a[section] : a[section]?[field], field is null ? b[section] : b[section]?[field]);

        var changes = SettingsChange.None;
        if (Changed("Translation") || Changed("General", nameof(GeneralSettings.TargetLanguage)) || Changed("Storage", nameof(StorageSettings.CacheMaxEntries)))
            changes |= SettingsChange.Translation;
        if (Changed("General", nameof(GeneralSettings.OcrLanguage))) changes |= SettingsChange.Ocr | SettingsChange.Subtitles;
        if (Changed("Subtitles") || Changed("General", nameof(GeneralSettings.TargetLanguage))) changes |= SettingsChange.Subtitles;
        if (Changed("Overlay")) changes |= SettingsChange.Overlay;
        if (Changed("Hotkeys")) changes |= SettingsChange.Hotkeys;
        if (Changed("Notifications")) changes |= SettingsChange.Notifications;
        if (Changed("Ui")) changes |= SettingsChange.Language;
        if (Changed("General", nameof(GeneralSettings.Theme))) changes |= SettingsChange.Theme;
        if (Changed("Storage", nameof(StorageSettings.LogLevel)) || Changed("Storage", nameof(StorageSettings.LogMaxKB))) changes |= SettingsChange.Log;
        if (changes == SettingsChange.None && !JsonNode.DeepEquals(a, b)) changes = SettingsChange.Other;
        return changes;
    }
}

/// <summary>
/// What the running app redoes for a save, and nothing more: a save of the overlay look leaves the provider chain, the
/// watcher and the hotkeys alone. The notices and everything else read the settings when they need them.
/// </summary>
/// <param name="RestartSubtitles">Stop the watcher before the rest and start it again after: it runs, and its settings or OCR engine changed.</param>
public sealed record SaveActions(bool Language, bool Log, bool RestartSubtitles, bool RebuildOcr, bool RebuildProviders,
    bool RegisterHotkeys, bool Restyle, bool Theme)
{
    public static SaveActions For(SettingsChange changes, bool subtitlesRunning) => new(
        Language: changes.HasFlag(SettingsChange.Language),
        Log: changes.HasFlag(SettingsChange.Log),
        RestartSubtitles: subtitlesRunning && (changes & (SettingsChange.Subtitles | SettingsChange.Ocr)) != 0,
        RebuildOcr: changes.HasFlag(SettingsChange.Ocr),
        RebuildProviders: changes.HasFlag(SettingsChange.Translation),
        RegisterHotkeys: changes.HasFlag(SettingsChange.Hotkeys),
        Restyle: changes.HasFlag(SettingsChange.Overlay),
        Theme: changes.HasFlag(SettingsChange.Theme));
}
