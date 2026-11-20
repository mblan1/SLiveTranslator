using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using Xunit;

namespace SLiveTranslator.Tests;

public class SettingsStoreTests
{
    /// <summary>The user's real version-1 file, as the orchestrator quoted it.</summary>
    const string V1Compact =
        """{"OcrLanguage":"","TargetLanguage":"vi","Provider":"google","MyMemoryEmail":"","ClaudeApiKey":"","ClaudeModel":"claude-opus-5-5","SubtitleIntervalMs":300,"SubtitleRegion":{"X":327,"Y":1094,"Width":1825,"Height":212},"FontSize":22,"ScreenOverlaySeconds":12,"HotkeyTranslateScreen":"Ctrl+Shift+T","HotkeyToggleSubtitles":"Ctrl+Shift+S","HotkeySelectRegion":"Ctrl+Shift+R","HotkeyClear":"Ctrl+Shift+X","HotkeyHistory":"Ctrl+Shift+H","FirstRunDone":true}""";

    /// <summary>The same file as version 1 wrote it to disk: indented, with '+' escaped.</summary>
    const string V1OnDisk = """
        {
          "OcrLanguage": "",
          "TargetLanguage": "vi",
          "Provider": "google",
          "MyMemoryEmail": "",
          "ClaudeApiKey": "",
          "ClaudeModel": "claude-opus-5-5",
          "SubtitleIntervalMs": 300,
          "SubtitleRegion": {
            "X": 327,
            "Y": 1094,
            "Width": 1825,
            "Height": 212
          },
          "FontSize": 22,
          "ScreenOverlaySeconds": 12,
          "HotkeyTranslateScreen": "Ctrl\u002BShift\u002BT",
          "HotkeyToggleSubtitles": "Ctrl\u002BShift\u002BS",
          "HotkeySelectRegion": "Ctrl\u002BShift\u002BR",
          "HotkeyClear": "Ctrl\u002BShift\u002BX",
          "HotkeyHistory": "Ctrl\u002BShift\u002BH",
          "FirstRunDone": true
        }
        """;

    [Theory]
    [InlineData(V1Compact)]
    [InlineData(V1OnDisk)]
    public void Migrates_the_real_version_1_file(string v1)
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        File.WriteAllText(store.FilePath, v1);

        var s = store.Load();

        Assert.True(store.MigratedFromV1);
        Assert.Equal(SettingsStore.CurrentSchemaVersion, s.SchemaVersion);
        Assert.False(s.IsNewerSchema);
        Assert.Equal("", s.General.OcrLanguage);
        Assert.Equal("vi", s.General.TargetLanguage);
        Assert.True(s.General.FirstRunDone);
        Assert.Equal(ProviderIds.Free, s.Translation.Provider);
        Assert.Equal("", s.Translation.Free.MyMemoryEmail);
        Assert.Equal("", s.Translation.Claude.ApiKeyProtected);
        Assert.Equal("claude-opus-5-5", s.Translation.Claude.Model);
        Assert.Equal(300, s.Subtitles.IntervalMs);
        Assert.Equal(new RegionSettings(327, 1094, 1825, 212), s.Subtitles.Region);
        Assert.Equal(OverlaySettings.DefaultFontSize, s.Overlay.FontSize); // version 1's default 22 is the old default
        Assert.Equal(12, s.Overlay.LabelSeconds);
        Assert.Equal("Ctrl+Shift+T", s.Hotkeys.Map[HotkeyAction.TranslateScreen]);
        Assert.Equal("Ctrl+Shift+S", s.Hotkeys.Map[HotkeyAction.ToggleSubtitles]);
        Assert.Equal("Ctrl+Shift+R", s.Hotkeys.Map[HotkeyAction.SelectRegion]);
        Assert.Equal("Ctrl+Shift+X", s.Hotkeys.Map[HotkeyAction.Clear]);
        Assert.Equal("Ctrl+Shift+H", s.Hotkeys.Map[HotkeyAction.History]);
        Assert.Empty(s.Hotkeys.Disabled);
        Assert.Equal([store.FilePath], Directory.GetFiles(dir.Path)); // loading alone writes nothing

        store.Save(s);

        Assert.Equal(v1, File.ReadAllText(store.V1BackupPath));
        string saved = File.ReadAllText(store.FilePath);
        Assert.Contains($"\"SchemaVersion\": {SettingsStore.CurrentSchemaVersion}", saved);
        Assert.Contains("\"TranslateScreen\": \"Ctrl+Shift+T\"", saved);
        Assert.DoesNotContain(@"\u002B", saved);
        Assert.False(File.Exists(store.FilePath + ".tmp"));

        var reloaded = new SettingsStore(store.FilePath);
        var again = reloaded.Load();
        Assert.False(reloaded.MigratedFromV1);
        Assert.Equal(new RegionSettings(327, 1094, 1825, 212), again.Subtitles.Region);
        Assert.Equal("Ctrl+Shift+H", again.Hotkeys[HotkeyAction.History]);
    }

    [Fact]
    public void Migration_encrypts_a_plain_claude_key_and_backs_up_the_original_once()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        string v1 = V1Compact.Replace("\"Provider\":\"google\"", "\"Provider\":\"claude\"")
            .Replace("\"ClaudeApiKey\":\"\"", "\"ClaudeApiKey\":\"sk-ant-plain-test-key\"");
        File.WriteAllText(store.FilePath, v1);

        var s = store.Load();
        Assert.Equal(ProviderIds.Claude, s.Translation.Provider);
        Assert.Equal("sk-ant-plain-test-key", s.Translation.Claude.ApiKey);
        Assert.NotEqual("", s.Translation.Claude.ApiKeyProtected);

        store.Save(s);
        s.Overlay.FontSize = 30;
        store.Save(s); // the second save must not replace the backup with the migrated file

        Assert.DoesNotContain("sk-ant-plain-test-key", File.ReadAllText(store.FilePath));
        Assert.Equal(v1, File.ReadAllText(store.V1BackupPath));
        var reloaded = new SettingsStore(store.FilePath).Load();
        Assert.Equal("sk-ant-plain-test-key", reloaded.Translation.Claude.ApiKey);
        Assert.False(reloaded.Translation.Claude.ApiKeyUnreadable);
        Assert.Equal(30, reloaded.Overlay.FontSize);
    }

    [Theory]
    [InlineData("google", ProviderIds.Free)]
    [InlineData("free", ProviderIds.Free)]
    [InlineData("something-else", ProviderIds.Free)]
    [InlineData("claude", ProviderIds.Claude)]
    public void Version_1_provider_maps_to_what_version_1_ran(string v1Provider, string expected)
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        File.WriteAllText(store.FilePath, $$"""{"Provider":"{{v1Provider}}"}""");

        var s = store.Load();

        Assert.Equal(expected, s.Translation.Provider);
        Assert.Equal("Ctrl+Shift+T", s.Hotkeys[HotkeyAction.TranslateScreen]); // version-1 defaults fill missing values
        Assert.Equal(300, s.Subtitles.IntervalMs);
    }

    [Fact]
    public void Version_2_round_trips_every_section()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var s = new AppSettings();
        s.General.TargetLanguage = "vi";
        s.General.Theme = AppTheme.Dark;
        s.General.StartWithWindows = true;
        s.Translation.Provider = ProviderIds.Azure;
        s.Translation.ContextLines = 4;
        s.Translation.Free.MyMemoryEmail = "player@example.com";
        s.Translation.Claude.ApiKey = "sk-ant-round-trip";
        s.Translation.Claude.Model = "claude-opus-5-5"; // the default is empty: the model box stays empty until a key check
        s.Translation.Claude.Effort = LlmEffort.Medium;
        s.Translation.Azure.ApiKey = "azure-key";
        s.Translation.Azure.Region = "southeastasia";
        s.Translation.Compat.BaseUrl = "http://localhost:11434/v1";
        s.Translation.Compat.Model = "qwen3:8b";
        s.Translation.DeepL.Model = "quality_optimized";
        s.Subtitles.Region = new RegionSettings(1, 2, 3, 4);
        s.Subtitles.OcrMode = OcrMode.Mask;
        s.Overlay.Position = SubtitlePosition.AboveRegion;
        s.Overlay.BackgroundOpacity = 0.5;
        s.Hotkeys[HotkeyAction.Clear] = "Ctrl+Alt+Shift+F20";
        s.Hotkeys.Disabled.Add(HotkeyAction.History);
        s.Notifications.Quota.Enabled = false;
        s.Notifications.Position = NoticePosition.NearRegion;
        s.Storage.LogLevel = LogLevel.Debug;
        s.Storage.CacheMaxEntries = 500;
        s.Ui.Language = "vi";

        store.Save(s);
        var r = new SettingsStore(store.FilePath).Load();

        Assert.Equal(SettingsStore.CurrentSchemaVersion, r.SchemaVersion);
        Assert.Equal("vi", r.General.TargetLanguage);
        Assert.Equal(AppTheme.Dark, r.General.Theme);
        Assert.True(r.General.StartWithWindows);
        Assert.Equal(ProviderIds.Azure, r.Translation.Provider);
        Assert.Equal(4, r.Translation.ContextLines);
        Assert.Equal("player@example.com", r.Translation.Free.MyMemoryEmail);
        Assert.Equal("sk-ant-round-trip", r.Translation.Claude.ApiKey);
        Assert.Equal("claude-opus-5-5", r.Translation.Claude.Model);
        Assert.Equal(LlmEffort.Medium, r.Translation.Claude.Effort);
        Assert.Equal("azure-key", r.Translation.Azure.ApiKey);
        Assert.Equal("southeastasia", r.Translation.Azure.Region);
        Assert.Equal("http://localhost:11434/v1", r.Translation.Compat.BaseUrl);
        Assert.Equal("qwen3:8b", r.Translation.Compat.Model);
        Assert.Equal("quality_optimized", r.Translation.DeepL.Model);
        Assert.Equal("", r.Translation.OpenAI.ApiKey);
        Assert.Equal(new RegionSettings(1, 2, 3, 4), r.Subtitles.Region);
        Assert.Equal(OcrMode.Mask, r.Subtitles.OcrMode);
        Assert.Equal(SubtitlePosition.AboveRegion, r.Overlay.Position);
        Assert.Equal(0.5, r.Overlay.BackgroundOpacity);
        Assert.Equal("Ctrl+Alt+Shift+F20", r.Hotkeys[HotkeyAction.Clear]);
        Assert.Equal("Ctrl+Shift+T", r.Hotkeys[HotkeyAction.TranslateScreen]);
        Assert.Equal(HotkeyAction.History, Assert.Single(r.Hotkeys.Disabled));
        Assert.False(r.Notifications.Quota.Enabled);
        Assert.Equal(8, r.Notifications.Quota.Seconds);
        Assert.Equal(NoticePosition.NearRegion, r.Notifications.Position);
        Assert.Equal(LogLevel.Debug, r.Storage.LogLevel);
        Assert.Equal(500, r.Storage.CacheMaxEntries);
        Assert.Equal("vi", r.Ui.Language);

        string saved = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("sk-ant-round-trip", saved);
        Assert.DoesNotContain("azure-key", saved);
        Assert.DoesNotContain("\"ApiKey\"", saved);
        Assert.DoesNotContain("IsNewerSchema", saved);
        Assert.Contains("\"Ctrl+Alt+Shift+F20\"", saved);
        Assert.Contains("\"Theme\": \"Dark\"", saved);
        Assert.False(File.Exists(store.V1BackupPath));
    }

    [Fact]
    public void Newer_schema_loads_and_saves_without_being_downgraded()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        File.WriteAllText(store.FilePath, """
            {
              "SchemaVersion": 4,
              "General": { "TargetLanguage": "ja", "FirstRunDone": true, "FutureFlag": true },
              "FutureSection": { "Answer": 42 }
            }
            """);

        var s = store.Load();

        Assert.True(s.IsNewerSchema);
        Assert.False(store.MigratedFromV1);
        Assert.Null(store.LoadProblem);
        Assert.False(store.Upgraded);
        Assert.Equal(4, s.SchemaVersion);
        Assert.Equal("ja", s.General.TargetLanguage);
        Assert.True(s.General.FirstRunDone);

        s.Overlay.FontSize = 40;
        store.Save(s);
        var saved = JsonNode.Parse(File.ReadAllText(store.FilePath))!;

        Assert.Equal(4, saved["SchemaVersion"]!.GetValue<int>());
        Assert.True(saved["General"]!["FutureFlag"]!.GetValue<bool>());
        Assert.Equal(42, saved["FutureSection"]!["Answer"]!.GetValue<int>());
        Assert.Equal(40, saved["Overlay"]!["FontSize"]!.GetValue<int>());
        Assert.False(File.Exists(store.V1BackupPath));
    }

    [Fact]
    public void Saved_file_keeps_plus_signs_and_vietnamese_readable()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var s = new AppSettings();
        s.Translation.ProfileId = "Thợ săn quái vật";

        store.Save(s);
        string saved = File.ReadAllText(store.FilePath);

        Assert.Contains("\"Ctrl+Shift+T\"", saved);
        Assert.Contains("Thợ săn quái vật", saved);
        Assert.DoesNotContain(@"\u", saved);
        Assert.Contains("\n  \"General\": {", saved.Replace("\r\n", "\n")); // indented
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{"SchemaVersion": "two"}""")]
    [InlineData("""{"SchemaVersion": 2, "Overlay": {"FontSize": "big"}}""")]
    [InlineData("""{"SchemaVersion": 2, "General": null}""")]
    public void Corrupt_file_gives_defaults_and_keeps_a_copy(string content)
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        File.WriteAllText(store.FilePath, content);

        var s = store.Load();

        Assert.False(s.General.FirstRunDone);
        Assert.Equal(ProviderIds.Free, s.Translation.Provider);
        Assert.Equal(OverlaySettings.DefaultFontSize, s.Overlay.FontSize);
        Assert.Contains(Path.GetFileName(store.CorruptCopyPath), store.LoadProblem);
        Assert.Equal(content, File.ReadAllText(store.CorruptCopyPath));
        Assert.Equal(content, File.ReadAllText(store.FilePath)); // left alone until the next save
    }

    [Fact]
    public void Missing_file_gives_defaults_and_writes_nothing()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File(Path.Combine("sub", "settings.json")));

        var s = store.Load();

        Assert.False(store.MigratedFromV1);
        Assert.Equal(SettingsStore.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Equal(new Dictionary<HotkeyAction, string>(HotkeySettings.Defaults), s.Hotkeys.Map);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "sub")));
    }

    [Fact]
    public void Save_replaces_a_leftover_temp_file_from_an_interrupted_save()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        File.WriteAllText(store.FilePath + ".tmp", "half-written");

        var s = new AppSettings();
        s.General.FirstRunDone = true;
        store.Save(s);

        Assert.False(File.Exists(store.FilePath + ".tmp"));
        Assert.True(new SettingsStore(store.FilePath).Load().General.FirstRunDone);
    }

    [Fact]
    public void Hotkey_missing_from_the_file_falls_back_to_its_default()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        File.WriteAllText(store.FilePath, """{"SchemaVersion": 2, "Hotkeys": {"Map": {"Clear": "F9"}, "Disabled": ["Clear"]}}""");

        var s = store.Load();

        Assert.Equal("F9", s.Hotkeys[HotkeyAction.Clear]);
        Assert.Equal("Ctrl+Shift+T", s.Hotkeys[HotkeyAction.TranslateScreen]);
        Assert.Contains(HotkeyAction.Clear, s.Hotkeys.Disabled);
    }

    [Fact]
    public void Key_from_another_windows_account_reads_as_unreadable_not_as_garbage()
    {
        var claude = new ClaudeProviderSettings { ApiKeyProtected = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAAdamaged" };

        Assert.True(claude.ApiKeyUnreadable);
        Assert.Equal("", claude.ApiKey);
        Assert.False(new ClaudeProviderSettings().ApiKeyUnreadable);
    }

    /// <summary>A later build adds enum values and hotkey actions, with or without raising the schema number.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Unknown_enum_values_and_hotkey_actions_load_and_survive_a_save(int schema)
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        string blob = SecretStore.Protect("sk-ant-newer-build");
        File.WriteAllText(store.FilePath, $$"""
            {
              "SchemaVersion": {{schema}},
              "General": { "TargetLanguage": "vi", "Theme": "HighContrast", "FirstRunDone": true },
              "Translation": { "Provider": "claude", "Claude": { "ApiKeyProtected": "{{blob}}", "Model": "claude-opus-5-5", "Effort": "High" } },
              "Subtitles": { "Region": { "X": 327, "Y": 1094, "Width": 1825, "Height": 212 }, "OcrMode": "Neural" },
              "Hotkeys": {
                "Map": { "TranslateScreen": "Ctrl\u002BAlt\u002BT", "OpenMainWindow": "F7", "Mute": "Ctrl+Shift+M" },
                "Disabled": [ "History", "Mute" ]
              },
              "Storage": { "LogLevel": "Trace" }
            }
            """);

        var s = store.Load();

        Assert.Null(store.LoadProblem);
        Assert.False(File.Exists(store.CorruptCopyPath));
        Assert.Equal(schema > SettingsStore.CurrentSchemaVersion, s.IsNewerSchema);
        Assert.True(s.General.FirstRunDone);
        Assert.Equal("vi", s.General.TargetLanguage);
        Assert.Equal(AppTheme.System, s.General.Theme);
        Assert.Equal(ProviderIds.Claude, s.Translation.Provider);
        Assert.Equal("sk-ant-newer-build", s.Translation.Claude.ApiKey);
        Assert.Equal(LlmEffort.Low, s.Translation.Claude.Effort);
        Assert.Equal(new RegionSettings(327, 1094, 1825, 212), s.Subtitles.Region);
        Assert.Equal(OcrMode.Auto, s.Subtitles.OcrMode);
        Assert.Equal(LogLevel.Info, s.Storage.LogLevel); // the property's default, not the enum's first member
        Assert.Equal("Ctrl+Alt+T", s.Hotkeys[HotkeyAction.TranslateScreen]);
        Assert.Equal("Ctrl+Shift+X", s.Hotkeys[HotkeyAction.Clear]);
        Assert.Equal(HotkeyAction.History, Assert.Single(s.Hotkeys.Disabled));

        s.Overlay.FontSize = 40;
        store.Save(s);
        var saved = JsonNode.Parse(File.ReadAllText(store.FilePath))!;

        Assert.Equal(System.Math.Max(schema, SettingsStore.CurrentSchemaVersion), saved["SchemaVersion"]!.GetValue<int>()); // older: raised
        Assert.Equal("HighContrast", saved["General"]!["Theme"]!.GetValue<string>());
        Assert.Equal("High", saved["Translation"]!["Claude"]!["Effort"]!.GetValue<string>());
        Assert.Equal(blob, saved["Translation"]!["Claude"]!["ApiKeyProtected"]!.GetValue<string>());
        Assert.Equal("Neural", saved["Subtitles"]!["OcrMode"]!.GetValue<string>());
        Assert.Equal("Trace", saved["Storage"]!["LogLevel"]!.GetValue<string>());
        Assert.Equal("Ctrl+Alt+T", saved["Hotkeys"]!["Map"]!["TranslateScreen"]!.GetValue<string>());
        Assert.Equal("F7", saved["Hotkeys"]!["Map"]!["OpenMainWindow"]!.GetValue<string>());
        Assert.Equal("Ctrl+Shift+M", saved["Hotkeys"]!["Map"]!["Mute"]!.GetValue<string>());
        Assert.Equal(["History", "Mute"], saved["Hotkeys"]!["Disabled"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(40, saved["Overlay"]!["FontSize"]!.GetValue<int>());
        Assert.DoesNotContain(@"\u002B", File.ReadAllText(store.FilePath));

        // What the user changes is written; what this version cannot show stays as the newer build wrote it.
        s.General.Theme = AppTheme.Dark;
        s.Hotkeys.Disabled.Remove(HotkeyAction.History);
        s.Hotkeys[HotkeyAction.Clear] = "F9";
        store.Save(s);
        saved = JsonNode.Parse(File.ReadAllText(store.FilePath))!;

        Assert.Equal("Dark", saved["General"]!["Theme"]!.GetValue<string>());
        Assert.Equal(["Mute"], saved["Hotkeys"]!["Disabled"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("F9", saved["Hotkeys"]!["Map"]!["Clear"]!.GetValue<string>());
        Assert.Equal("F7", saved["Hotkeys"]!["Map"]!["OpenMainWindow"]!.GetValue<string>());
        Assert.Equal("High", saved["Translation"]!["Claude"]!["Effort"]!.GetValue<string>());

        var again = new SettingsStore(store.FilePath).Load();
        Assert.Equal(AppTheme.Dark, again.General.Theme);
        Assert.Equal("F9", again.Hotkeys[HotkeyAction.Clear]);
        Assert.Empty(again.Hotkeys.Disabled);
    }

    [Theory]
    [InlineData(22, 12)] // the old default becomes the new one
    [InlineData(30, 30)] // a size the user chose stays
    [InlineData(12, 12)]
    public void A_schema_2_file_is_raised_once_and_its_old_default_size_becomes_12(int before, int after)
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        string content = """{"SchemaVersion": 2, "General": {"FirstRunDone": true}, "Overlay": {"FontSize": """ + before + """, "MaxLines": 4}}""";
        File.WriteAllText(store.FilePath, content);

        var s = store.Load();

        Assert.True(store.Upgraded);
        Assert.Equal(after, s.Overlay.FontSize);
        Assert.Equal(4, s.Overlay.MaxLines);
        Assert.Equal(SettingsStore.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Equal(content, File.ReadAllText(store.FilePath)); // loading alone writes nothing

        store.Save(s);
        var saved = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        Assert.Equal(after, saved["Overlay"]!["FontSize"]!.GetValue<int>());
        Assert.Equal(SettingsStore.CurrentSchemaVersion, saved["SchemaVersion"]!.GetValue<int>());

        // Raised once: 22 chosen afterwards is the user's and stays.
        s.Overlay.FontSize = 22;
        store.Save(s);
        var reloaded = new SettingsStore(store.FilePath);
        Assert.Equal(22, reloaded.Load().Overlay.FontSize);
        Assert.False(reloaded.Upgraded);
    }

    [Fact]
    public void A_current_or_newer_file_with_22_is_not_touched()
    {
        using var dir = new TempDir();
        foreach (int schema in new[] { SettingsStore.CurrentSchemaVersion, SettingsStore.CurrentSchemaVersion + 1 })
        {
            var store = new SettingsStore(dir.File($"settings{schema}.json"));
            File.WriteAllText(store.FilePath, """{"SchemaVersion": """ + schema + """, "Overlay": {"FontSize": 22}}""");

            var s = store.Load();

            Assert.False(store.Upgraded);
            Assert.Equal(22, s.Overlay.FontSize);
        }
    }

    [Fact]
    public void Merge_keeps_the_file_version_of_what_the_app_did_not_change()
    {
        static JsonNode N(string json) => JsonNode.Parse(json)!;

        var merged = SettingsStore.Merge(
            file: N("""{"a": 1, "b": "newer", "set": ["A", "Z"], "gone": 1, "unknown": {"x": 5}}"""),
            synced: N("""{"a": 1, "b": "default", "set": ["A"], "gone": 1}"""),
            current: N("""{"a": 2, "b": "default", "set": [], "added": true}"""));

        Assert.True(JsonNode.DeepEquals(N("""{"a": 2, "b": "newer", "set": ["Z"], "added": true, "unknown": {"x": 5}}"""), merged));
    }

    [Fact]
    public void One_damaged_section_resets_only_that_section()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        string content = """
            {
              "SchemaVersion": 2,
              "General": { "TargetLanguage": "vi", "FirstRunDone": true },
              "Subtitles": { "Region": { "X": 1, "Y": 2, "Width": 3, "Height": 4 } },
              "Overlay": { "FontSize": "big", "MaxLines": 5 },
              "Notifications": null
            }
            """;
        File.WriteAllText(store.FilePath, content);

        var s = store.Load();

        Assert.True(s.General.FirstRunDone);
        Assert.Equal(new RegionSettings(1, 2, 3, 4), s.Subtitles.Region);
        Assert.Equal(OverlaySettings.DefaultFontSize, s.Overlay.FontSize);
        Assert.Equal(3, s.Overlay.MaxLines);
        Assert.True(s.Notifications.TrayBalloons);
        Assert.Contains("Overlay, Notifications", store.LoadProblem);
        Assert.Equal(content, File.ReadAllText(store.CorruptCopyPath));

        store.Save(s);
        var saved = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        Assert.Equal(OverlaySettings.DefaultFontSize, saved["Overlay"]!["FontSize"]!.GetValue<int>()); // the damaged value is not written back
        Assert.NotNull(saved["Notifications"]);
        var reloaded = new SettingsStore(store.FilePath);
        Assert.True(reloaded.Load().General.FirstRunDone);
        Assert.Null(reloaded.LoadProblem);
    }

    [Fact]
    public void A_load_problem_is_told_in_the_language_the_loaded_settings_choose()
    {
        using var dir = new TempDir();
        var loc = new Loc(Loc.Strings(Loc.English), Loc.Strings(Loc.Vietnamese), () => "en"); // Windows in English
        var store = new SettingsStore(dir.File("settings.json"), SecretStore.Protect, loc);
        File.WriteAllText(store.FilePath, """{ "SchemaVersion": 2, "Ui": { "Language": "vi" }, "Overlay": { "FontSize": "big" } }""");

        var s = store.Load();
        loc.SetLanguage(s.Ui.Language); // what the app does next

        Assert.Equal(loc.Format("Store.SectionsDamaged", "Overlay", Path.GetFileName(store.CorruptCopyPath)), store.LoadProblem);
        Assert.StartsWith("Một số cài đặt", store.LoadProblem);
    }

    [Fact]
    public void File_that_cannot_be_read_is_never_replaced_without_a_copy()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        const string content = """{"SchemaVersion": 2, "General": {"FirstRunDone": true}}""";
        File.WriteAllText(store.FilePath, content);

        AppSettings s;
        using (var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            s = store.Load();
            Assert.False(s.General.FirstRunDone);
            Assert.Contains(Path.GetFileName(store.UnreadCopyPath), store.LoadProblem);
            Assert.ThrowsAny<IOException>(() => store.Save(s)); // still held: no copy possible, so no save
        }
        Assert.Equal(content, File.ReadAllText(store.FilePath));

        store.Save(s);

        Assert.Equal(content, File.ReadAllText(store.UnreadCopyPath));
        Assert.Contains($"\"SchemaVersion\": {SettingsStore.CurrentSchemaVersion}", File.ReadAllText(store.FilePath));
        File.Delete(store.UnreadCopyPath);
        store.Save(s);
        Assert.False(File.Exists(store.UnreadCopyPath)); // only the first save copies
    }

    [Fact]
    public void Failing_key_encryption_during_migration_does_not_throw_or_rewrite_the_old_file()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"), _ => throw new CryptographicException("profile not loaded"));
        string v1 = V1Compact.Replace("\"ClaudeApiKey\":\"\"", "\"ClaudeApiKey\":\"sk-ant-plain-test-key\"");
        File.WriteAllText(store.FilePath, v1);

        var s = store.Load();

        Assert.False(store.MigratedFromV1); // so startup does not save over the only copy of the key
        Assert.Contains("profile not loaded", store.LoadProblem);
        Assert.Equal("", s.Translation.Claude.ApiKeyProtected);
        Assert.Equal(new RegionSettings(327, 1094, 1825, 212), s.Subtitles.Region);
        Assert.Equal(v1, File.ReadAllText(store.FilePath));

        store.Save(s);

        Assert.Equal(v1, File.ReadAllText(store.V1BackupPath));
        Assert.DoesNotContain("sk-ant-plain-test-key", File.ReadAllText(store.FilePath));
    }
}
