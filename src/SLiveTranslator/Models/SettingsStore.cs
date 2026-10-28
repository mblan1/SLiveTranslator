using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using SLiveTranslator.UI;

namespace SLiveTranslator.Models;

/// <summary>
/// Reads and writes settings.json. Version-1 files (flat, no SchemaVersion) are migrated in memory; the first save
/// afterwards copies the original to settings.json.bak-v1. Saves are atomic (temp file, then replace) and keep what
/// this version could not read, so a file from a newer version is not downgraded.
/// </summary>
public sealed class SettingsStore
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Antivirus or OneDrive can hold the file for a moment at login.</summary>
    const int ReadAttempts = 3;
    static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Keeps "Ctrl+Shift+T" and Vietnamese text readable instead of \u002B escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { TolerantEnums.Apply } },
        RespectNullableAnnotations = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    string _path;
    readonly Func<string, string> _protectKey;
    readonly Loc _loc;
    (string Key, object?[] Args)? _problem;
    bool _v1BackupPending;
    bool _unreadBackupPending;

    /// <summary>The file as last read or written (minus sections that could not be read).</summary>
    JsonObject? _fileJson;

    /// <summary>What the settings looked like when <see cref="_fileJson"/> was read or written.</summary>
    JsonNode? _syncedJson;

    public SettingsStore(string path) : this(path, SecretStore.Protect)
    {
    }

    /// <param name="protectKey">Encrypts a plain version-1 key; replaceable so tests can make DPAPI fail.</param>
    /// <param name="loc">The texts of <see cref="LoadProblem"/>; the app's by default (a test switches the language of its own).</param>
    internal SettingsStore(string path, Func<string, string> protectKey, Loc? loc = null)
    {
        _path = path;
        _protectKey = protectKey;
        _loc = loc ?? Loc.Instance;
    }

    public string FilePath => _path;

    /// <summary>The data folder moved: the next save writes to <paramref name="path"/>, keeping what was read from the old file.</summary>
    public void Relocate(string path) => _path = path;
    public string V1BackupPath => _path + ".bak-v1";
    public string CorruptCopyPath => _path + ".corrupt";

    /// <summary>Where a file that could not be read is kept before the first save replaces it.</summary>
    public string UnreadCopyPath => _path + ".bak";

    /// <summary>The last <see cref="Load"/> converted a version-1 file; save once to store it as schema 2 with encrypted keys.</summary>
    public bool MigratedFromV1 { get; private set; }

    /// <summary>
    /// What went wrong in the last <see cref="Load"/>, to show the user, in the language in use when it is read: the
    /// language comes from the settings Load returns. Null when nothing went wrong. Already logged.
    /// </summary>
    public string? LoadProblem => _problem is { } problem ? _loc.Format(problem.Key, problem.Args) : null;

    /// <summary>
    /// Never throws: a missing or unreadable file gives defaults, and a damaged section gives that section's defaults.
    /// Writes nothing (so CLI modes leave the data folder alone) except for a damaged file, which is kept as
    /// <see cref="CorruptCopyPath"/>.
    /// </summary>
    public AppSettings Load()
    {
        MigratedFromV1 = _v1BackupPending = _unreadBackupPending = false;
        _fileJson = null;
        _syncedJson = null;
        _problem = null;
        if (ReadText() is not { } text) return new AppSettings();

        try
        {
            var root = JsonNode.Parse(text, documentOptions: DocumentOptions) as JsonObject
                       ?? throw new JsonException("the file does not hold a JSON object");
            int version = root["SchemaVersion"]?.GetValue<int>() ?? 1;
            if (version <= 1) return LoadV1(root);

            var broken = new List<string>();
            var settings = ReadSections(root, broken);
            if (broken.Count > 0)
            {
                KeepCorruptCopy();
                Problem("Store.SectionsDamaged", string.Join(", ", broken), Path.GetFileName(CorruptCopyPath));
            }
            _fileJson = root;
            _syncedJson = JsonSerializer.SerializeToNode(settings, JsonOptions);
            return settings;
        }
        catch (Exception ex) when (IsBadContent(ex))
        {
            KeepCorruptCopy();
            Problem("Store.FileDamaged", ex.Message, Path.GetFileName(CorruptCopyPath));
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // A file that could not be read is never replaced unseen: when it still cannot be copied, the save fails.
        if (_unreadBackupPending && File.Exists(_path)) File.Copy(_path, UnreadCopyPath, overwrite: true);
        _unreadBackupPending = false;
        bool backedUp = _v1BackupPending && File.Exists(_path);
        if (backedUp) File.Copy(_path, V1BackupPath, overwrite: true);
        _v1BackupPending = false;

        var current = JsonSerializer.SerializeToNode(settings, JsonOptions)!;
        var json = _fileJson is not null && _syncedJson is not null ? (JsonObject)Merge(_fileJson, _syncedJson, current)! : current.AsObject();
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, json.ToJsonString(JsonOptions));
        File.Move(tmp, _path, overwrite: true);
        _fileJson = json;
        _syncedJson = current;
        if (backedUp) Log.Write($"settings: stored the version 1 file as schema {CurrentSchemaVersion}; the original is {Path.GetFileName(V1BackupPath)}");
    }

    /// <summary>The settings as the file holds them (key blobs and fields this version does not know included).</summary>
    internal static JsonNode ToNode(AppSettings settings) => JsonSerializer.SerializeToNode(settings, JsonOptions)!;

    internal static AppSettings FromNode(JsonNode node) => node.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();

    /// <summary>A deep copy, made through the file's form, so a working copy can be edited without touching the settings in use.</summary>
    public static AppSettings Clone(AppSettings settings) => FromNode(ToNode(settings));

    /// <summary>
    /// Three-way merge of what is about to be saved into the file: a value the app left as it was when the file was
    /// read keeps the file's version, so a value this version could not read (a newer enum name, an unknown hotkey
    /// action) survives. Objects merge per key; arrays are sets that keep the file's items this version skipped.
    /// </summary>
    internal static JsonNode? Merge(JsonNode? file, JsonNode? synced, JsonNode? current)
    {
        if (current is JsonObject c && synced is JsonObject s && file is JsonObject f)
        {
            var merged = new JsonObject();
            foreach (var (key, value) in c)
                merged[key] = f.TryGetPropertyValue(key, out var fileValue) && s.TryGetPropertyValue(key, out var syncedValue)
                    ? Merge(fileValue, syncedValue, value)
                    : value?.DeepClone();
            foreach (var (key, value) in f)
                if (!c.ContainsKey(key) && !s.ContainsKey(key)) merged[key] = value?.DeepClone();
            return merged;
        }
        if (current is JsonArray ca && synced is JsonArray sa && file is JsonArray fa)
        {
            var merged = new JsonArray(ca.Select(n => n?.DeepClone()).ToArray());
            foreach (var item in fa)
                if (!sa.Any(n => JsonNode.DeepEquals(n, item)) && !ca.Any(n => JsonNode.DeepEquals(n, item))) merged.Add(item?.DeepClone());
            return merged;
        }
        return JsonNode.DeepEquals(current, synced) ? file?.DeepClone() : current?.DeepClone();
    }

    /// <summary>
    /// Three-way merge of two copies edited apart from <paramref name="ancestor"/> (the settings window's and the
    /// app's): what only one side changed comes from that side, an item or key it removed included; where both
    /// changed the same value, <paramref name="winner"/>'s stays. Arrays are sets, as in <see cref="Merge"/>.
    /// </summary>
    internal static JsonNode? MergeEdits(JsonNode? ancestor, JsonNode? winner, JsonNode? other)
    {
        if (JsonNode.DeepEquals(other, ancestor)) return winner?.DeepClone();
        if (JsonNode.DeepEquals(winner, ancestor)) return other?.DeepClone();
        if (winner is JsonObject w && other is JsonObject o)
        {
            var a = ancestor as JsonObject;
            var merged = new JsonObject();
            foreach (var (key, value) in w)
            {
                JsonNode? before = null;
                bool inAncestor = a?.TryGetPropertyValue(key, out before) == true;
                if (o.TryGetPropertyValue(key, out var otherValue)) merged[key] = MergeEdits(before, value, otherValue);
                else if (!inAncestor || !JsonNode.DeepEquals(value, before)) merged[key] = value?.DeepClone(); // not removed by the other side, or changed here
            }
            foreach (var (key, value) in o)
                if (!w.ContainsKey(key) && a?.ContainsKey(key) != true) merged[key] = value?.DeepClone(); // added by the other side
            return merged;
        }
        if (winner is JsonArray wa && other is JsonArray oa)
        {
            var aa = ancestor as JsonArray ?? new JsonArray();
            bool In(JsonArray array, JsonNode? item) => array.Any(n => JsonNode.DeepEquals(n, item));
            var merged = new JsonArray(wa.Where(n => !(In(aa, n) && !In(oa, n))).Select(n => n?.DeepClone()).ToArray());
            foreach (var item in oa)
                if (!In(aa, item) && !In(wa, item)) merged.Add(item?.DeepClone());
            return merged;
        }
        return winner?.DeepClone();
    }

    /// <returns>The file's text; null when there is none or it could not be read.</returns>
    string? ReadText()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return File.Exists(_path) ? File.ReadAllText(_path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < ReadAttempts)
                {
                    Thread.Sleep(ReadRetryDelay);
                    continue;
                }
                _unreadBackupPending = true;
                Problem("Store.Unreadable", ex.Message, Path.GetFileName(UnreadCopyPath));
                return null;
            }
        }
    }

    /// <summary>Deserializes section by section, so one damaged section does not reset the others. Removes damaged sections from <paramref name="root"/>.</summary>
    static AppSettings ReadSections(JsonObject root, List<string> broken)
    {
        foreach (var section in JsonOptions.GetTypeInfo(typeof(AppSettings)).Properties)
        {
            if (section.IsExtensionData || !root.TryGetPropertyValue(section.Name, out var node)) continue;
            try
            {
                _ = node.Deserialize(section.PropertyType, JsonOptions) ?? throw new JsonException("the section is null");
            }
            catch (Exception ex) when (IsBadContent(ex))
            {
                root.Remove(section.Name);
                broken.Add(section.Name);
            }
        }
        return root.Deserialize<AppSettings>(JsonOptions) ?? throw new JsonException("the file holds no settings");
    }

    static bool IsBadContent(Exception ex) => ex is JsonException or InvalidOperationException or FormatException;

    AppSettings LoadV1(JsonObject root)
    {
        var v1 = root.Deserialize<V1Settings>(JsonOptions) ?? new V1Settings();
        var settings = MigrateV1(v1);
        _v1BackupPending = true;
        try
        {
            settings.Translation.Claude.ApiKeyProtected = _protectKey(v1.ClaudeApiKey.Trim());
            MigratedFromV1 = true;
        }
        catch (CryptographicException ex)
        {
            // DPAPI fails without a loaded user profile. Not saving now leaves the old file for a later run; a save
            // made anyway still keeps it as .bak-v1.
            Problem("Store.KeyNotEncrypted", ex.Message);
        }
        return settings;
    }

    void Problem(string key, params object?[] args)
    {
        _problem = (key, args);
        Log.Error("settings: " + _loc.Format(key, args));
    }

    void KeepCorruptCopy()
    {
        try
        {
            File.Copy(_path, CorruptCopyPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("settings: could not keep a copy of the damaged file: " + ex.Message);
        }
    }

    /// <summary>Everything but the API key, which <see cref="LoadV1"/> encrypts.</summary>
    static AppSettings MigrateV1(V1Settings v1)
    {
        var settings = new AppSettings();
        settings.General.OcrLanguage = v1.OcrLanguage;
        settings.General.TargetLanguage = v1.TargetLanguage;
        settings.General.FirstRunDone = v1.FirstRunDone;
        // Version 1 ran the free chain for anything but "claude"; older files still say "google".
        settings.Translation.Provider = v1.Provider == ProviderIds.Claude ? ProviderIds.Claude : ProviderIds.Free;
        settings.Translation.Free.MyMemoryEmail = v1.MyMemoryEmail;
        settings.Translation.Claude.Model = v1.ClaudeModel;
        settings.Subtitles.IntervalMs = v1.SubtitleIntervalMs;
        settings.Subtitles.Region = v1.SubtitleRegion;
        settings.Overlay.FontSize = v1.FontSize;
        settings.Overlay.LabelSeconds = v1.ScreenOverlaySeconds;
        settings.Hotkeys[HotkeyAction.TranslateScreen] = v1.HotkeyTranslateScreen;
        settings.Hotkeys[HotkeyAction.ToggleSubtitles] = v1.HotkeyToggleSubtitles;
        settings.Hotkeys[HotkeyAction.SelectRegion] = v1.HotkeySelectRegion;
        settings.Hotkeys[HotkeyAction.Clear] = v1.HotkeyClear;
        settings.Hotkeys[HotkeyAction.History] = v1.HotkeyHistory;
        return settings;
    }

    /// <summary>The flat version-1 file, with the defaults version 1 used for missing values.</summary>
    sealed class V1Settings
    {
        public string OcrLanguage { get; set; } = "";
        public string TargetLanguage { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        public string Provider { get; set; } = ProviderIds.Free;
        public string MyMemoryEmail { get; set; } = "";
        public string ClaudeApiKey { get; set; } = "";
        public string ClaudeModel { get; set; } = "claude-opus-5-5";
        public int SubtitleIntervalMs { get; set; } = 300;
        public RegionSettings? SubtitleRegion { get; set; }
        public int FontSize { get; set; } = 22;
        public int ScreenOverlaySeconds { get; set; } = 12;
        public string HotkeyTranslateScreen { get; set; } = HotkeySettings.Defaults[HotkeyAction.TranslateScreen];
        public string HotkeyToggleSubtitles { get; set; } = HotkeySettings.Defaults[HotkeyAction.ToggleSubtitles];
        public string HotkeySelectRegion { get; set; } = HotkeySettings.Defaults[HotkeyAction.SelectRegion];
        public string HotkeyClear { get; set; } = HotkeySettings.Defaults[HotkeyAction.Clear];
        public string HotkeyHistory { get; set; } = HotkeySettings.Defaults[HotkeyAction.History];
        public bool FirstRunDone { get; set; }
    }
}
