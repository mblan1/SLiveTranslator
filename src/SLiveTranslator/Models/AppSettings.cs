using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SLiveTranslator.Models;

/// <summary>Keeps fields this version does not know, so a file written by a newer version survives a save.</summary>
public abstract class SettingsSection
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>Everything in settings.json (schema 2). Loaded and saved by <see cref="SettingsStore"/>.</summary>
public sealed class AppSettings : SettingsSection
{
    public int SchemaVersion { get; set; } = SettingsStore.CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();
    public SubtitleSettings Subtitles { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    /// <summary>The file came from a newer version of the app; saving keeps its schema number and unknown fields.</summary>
    [JsonIgnore]
    public bool IsNewerSchema => SchemaVersion > SettingsStore.CurrentSchemaVersion;
}

public enum AppTheme { System, Light, Dark }

public sealed class GeneralSettings : SettingsSection
{
    /// <summary>Language to translate into (Google code such as "en", "vi", "ja", "zh-CN").</summary>
    public string TargetLanguage { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>BCP-47 tag of the Windows OCR language ("" = Windows user-profile languages).</summary>
    public string OcrLanguage { get; set; } = "";

    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool StartWithWindows { get; set; }
    public bool FirstRunDone { get; set; }
}

public enum OcrMode { Auto, Mask, Plain }

public sealed class SubtitleSettings : SettingsSection
{
    public int IntervalMs { get; set; } = 300;
    public RegionSettings? Region { get; set; }
    public bool PauseWhenGameUnfocused { get; set; } = true;
    public OcrMode OcrMode { get; set; } = OcrMode.Auto;

    /// <summary>
    /// Largest gap between two lines of one subtitle, in line heights (0.7 to 2.5); a wider gap starts a new text
    /// block, translated on its own. Raise it if the game spaces its subtitle lines wider and sentences come out split.
    /// </summary>
    public double ParagraphGapRatio { get; set; } = 1.3;

    /// <summary>Runs the subtitle loop at below-normal priority.</summary>
    public bool LowImpact { get; set; }
}

public enum SubtitlePosition { BelowRegion, AboveRegion, BottomCenter }

public sealed class OverlaySettings : SettingsSection
{
    public int FontSize { get; set; } = 22;
    public int MaxLines { get; set; } = 3;
    public double TextOpacity { get; set; } = 1.0;
    public double BackgroundOpacity { get; set; } = 0.82;
    public SubtitlePosition Position { get; set; } = SubtitlePosition.BelowRegion;
    public bool ShowOriginal { get; set; }
    public int HistoryLines { get; set; } = 12;

    /// <summary>Seconds the translate-screen labels stay visible (0 = until cleared).</summary>
    public int LabelSeconds { get; set; } = 12;
}

public enum NoticePosition { TopCenter, TopRight, NearRegion }

public sealed class NoticeCategorySettings : SettingsSection
{
    public bool Enabled { get; set; } = true;
    public double Seconds { get; set; } = 2.5;
}

public sealed class NotificationSettings : SettingsSection
{
    public NoticeCategorySettings Status { get; set; } = new();
    public NoticeCategorySettings Warning { get; set; } = new() { Seconds = 8 };
    public NoticeCategorySettings Error { get; set; } = new() { Seconds = 6 };
    public NoticeCategorySettings Quota { get; set; } = new() { Seconds = 8 };
    public NoticePosition Position { get; set; } = NoticePosition.TopCenter;
    public bool Sound { get; set; }
    public bool TrayBalloons { get; set; } = true;
    public bool LiveBadge { get; set; } = true;

    /// <summary>Hides every notice but errors, and every sound (the tray's "Mute notifications").</summary>
    public bool Muted { get; set; }
}

public sealed class StorageSettings : SettingsSection
{
    public int CacheMaxEntries { get; set; } = 20_000;
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public int LogMaxKB { get; set; } = (int)(LogWriter.DefaultMaxBytes / 1024);
}

public sealed class UiSettings : SettingsSection
{
    /// <summary>"en" or "vi"; "" follows the Windows display language.</summary>
    public string Language { get; set; } = "";
}

public sealed record RegionSettings(int X, int Y, int Width, int Height)
{
    public System.Drawing.Rectangle ToRectangle() => new(X, Y, Width, Height);
    public static RegionSettings From(System.Drawing.Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}
