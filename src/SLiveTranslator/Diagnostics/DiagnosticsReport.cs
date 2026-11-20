using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SLiveTranslator.Models;

namespace SLiveTranslator.Diagnostics;

/// <summary>The version and platform the app runs on, for the About page and the diagnostics.</summary>
public static class AppInfo
{
    /// <summary>"2.0.0", without the build metadata the SDK appends ("+commit").</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "?").Split('+')[0];

    public const string Author = "Snw";

    public static string Runtime => RuntimeInformation.FrameworkDescription;
    public static string OsDescription => RuntimeInformation.OSDescription;
}

/// <summary>
/// "Copy diagnostics": what a bug report needs, with every API key and email replaced by <see cref="Redacted"/>, in
/// the settings and anywhere else in the text (a key that reached the log).
/// </summary>
public static class DiagnosticsReport
{
    public const string Redacted = "REDACTED";
    public const int LogLines = 100;

    /// <summary>Keys this short are not looked for in the text: they would replace ordinary words.</summary>
    const int MinSecretLength = 6;

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Build(AppSettings settings, IReadOnlyList<string> ocrLanguages, DataPaths paths, IReadOnlyList<string> logLines)
    {
        var text = new StringBuilder();
        text.AppendLine("SLiveTranslator diagnostics");
        text.AppendLine("Version: " + AppInfo.Version);
        text.AppendLine("Runtime: " + AppInfo.Runtime);
        text.AppendLine("OS: " + AppInfo.OsDescription);
        text.AppendLine("OCR languages: " + (ocrLanguages.Count > 0 ? string.Join(", ", ocrLanguages) : "none"));
        text.AppendLine("Data folder: " + paths.Root + (paths.IsPortable ? " (portable)" : ""));
        text.AppendLine();
        text.AppendLine("Settings:");
        var node = SettingsStore.ToNode(settings);
        Redact(node);
        text.AppendLine(node.ToJsonString(Pretty));
        text.AppendLine();
        text.AppendLine($"Last {LogLines} log lines:");
        foreach (var line in logLines.TakeLast(LogLines)) text.AppendLine(line);

        string report = text.ToString();
        foreach (var secret in Secrets(settings)) report = report.Replace(secret, Redacted, StringComparison.Ordinal);
        return report;
    }

    /// <summary>Every non-empty text whose name says it is a key or an email (the encrypted key blobs, MyMemory's email).</summary>
    static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[name];
                    if (value is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 && IsSecretName(name)) obj[name] = Redacted;
                    else Redact(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array) Redact(item);
                break;
        }
    }

    static bool IsSecretName(string name) =>
        name.Contains("Key", StringComparison.OrdinalIgnoreCase) || name.Contains("Email", StringComparison.OrdinalIgnoreCase);

    /// <summary>The decrypted keys and the email as written: they must not appear anywhere in the report.</summary>
    static IEnumerable<string> Secrets(AppSettings settings)
    {
        var t = settings.Translation;
        var keys = new ProviderSettings[] { t.Claude, t.OpenAI, t.Gemini, t.Compat, t.DeepL, t.Azure, t.GoogleCloud }.Select(s => s.ApiKey);
        return keys.Append(t.Free.MyMemoryEmail).Select(s => s.Trim()).Where(s => s.Length >= MinSecretLength).Distinct().OrderByDescending(s => s.Length);
    }
}
