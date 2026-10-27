using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SLiveTranslator.Models;

public sealed class AppSettings
{
    /// <summary>BCP-47 tag of the Windows OCR language ("" = Windows user-profile languages).</summary>
    public string OcrLanguage { get; set; } = "";

    /// <summary>Language to translate into (Google code such as "en", "vi", "ja", "zh-CN").</summary>
    public string TargetLanguage { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>"free" (Google, then MyMemory; no key) or "claude" (needs an API key). Older files may say "google", which means "free".</summary>
    public string Provider { get; set; } = "free";

    /// <summary>Optional; raises the MyMemory free limit from 5,000 to 50,000 characters per day.</summary>
    public string MyMemoryEmail { get; set; } = "";
    public string ClaudeApiKey { get; set; } = "";
    public string ClaudeModel { get; set; } = "claude-opus-5-5";

    public int SubtitleIntervalMs { get; set; } = 300;
    public RegionSettings? SubtitleRegion { get; set; }
    public int FontSize { get; set; } = 22;

    /// <summary>Seconds the screen-translation labels stay visible (0 = until cleared).</summary>
    public int ScreenOverlaySeconds { get; set; } = 12;

    public string HotkeyTranslateScreen { get; set; } = "Ctrl+Shift+T";
    public string HotkeyToggleSubtitles { get; set; } = "Ctrl+Shift+S";
    public string HotkeySelectRegion { get; set; } = "Ctrl+Shift+R";
    public string HotkeyClear { get; set; } = "Ctrl+Shift+X";
    public string HotkeyHistory { get; set; } = "Ctrl+Shift+H";

    public bool FirstRunDone { get; set; }

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SLiveTranslator");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write("settings load failed: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}

public sealed record RegionSettings(int X, int Y, int Width, int Height)
{
    public System.Drawing.Rectangle ToRectangle() => new(X, Y, Width, Height);
    public static RegionSettings From(System.Drawing.Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}
