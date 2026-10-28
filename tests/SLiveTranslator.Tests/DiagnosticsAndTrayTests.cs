using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SLiveTranslator.Diagnostics;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Pages;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>"Copy diagnostics" keeps keys out, the tray menu as plain data, and the text families of the new pages.</summary>
public class DiagnosticsAndTrayTests
{
    const string FakeKey = "sk-ant-FAKE-0123456789abcdef";

    [Fact]
    public void Diagnostics_carry_the_platform_folder_settings_and_log_but_no_key_or_email()
    {
        var settings = new AppSettings();
        settings.Translation.Claude.ApiKey = FakeKey;
        settings.Translation.DeepL.ApiKey = "deepl-fake-key:fx";
        settings.Translation.Free.MyMemoryEmail = "geralt@kaermorhen.example";
        settings.Overlay.FontSize = 31;
        string blob = settings.Translation.Claude.ApiKeyProtected;
        var log = Enumerable.Range(1, 150).Select(i => $"2026-10-28 12:00:00.000 INFO  line {i}").Append("2026-10-28 12:00:01.000 WARN  claude: 401 for key " + FakeKey).ToList();
        var paths = DataPaths.At(@"D:\SLT Data", isPortable: true);

        string report = DiagnosticsReport.Build(settings, ["en-US"], paths, log);

        Assert.DoesNotContain(FakeKey, report);
        Assert.DoesNotContain("deepl-fake-key", report);
        Assert.DoesNotContain(blob, report);
        Assert.DoesNotContain("geralt@kaermorhen.example", report);
        Assert.Contains("\"ApiKeyProtected\": \"REDACTED\"", report);
        Assert.Contains("\"MyMemoryEmail\": \"REDACTED\"", report);
        Assert.Contains("\"FontSize\": 31", report);
        Assert.Contains("\"Hotkeys\": {", report); // a section whose name holds \"key\" is kept
        Assert.Contains("Ctrl+Shift+T", report);
        Assert.Contains("OCR languages: en-US", report);
        Assert.Contains(@"Data folder: D:\SLT Data (portable)", report);
        Assert.Contains("Version: " + AppInfo.Version, report);
        Assert.Contains("claude: 401 for key REDACTED", report);
        Assert.DoesNotContain("line 51\n", report.Replace("\r\n", "\n")); // the last 100 lines only
        Assert.Contains("line 52", report);
    }

    [Fact]
    public void Copy_diagnostics_puts_the_report_on_the_clipboard_and_says_when_it_could_not()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles"));
        host.Current.Translation.OpenAI.ApiKey = FakeKey;
        var prompts = new FakePrompts();
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = prompts };
        File.WriteAllLines(dir.File("log.txt"), ["first", "warn: something", "last"]);
        var log = new LogViewModel(vm, host, () => dir.File("log.txt"));

        log.CopyDiagnosticsCommand.Execute(null);

        Assert.NotNull(prompts.Copied);
        Assert.DoesNotContain(FakeKey, prompts.Copied);
        Assert.Contains("warn: something", prompts.Copied);
        Assert.Equal(MessageKind.Success, prompts.Messages.Last().Kind);

        prompts.ClipboardBusy = true;
        log.CopyDiagnosticsCommand.Execute(null);
        Assert.Equal(MessageKind.Error, prompts.Messages.Last().Kind);
    }

    [Fact]
    public void The_log_viewer_shows_the_last_lines_with_a_filter_and_clear_empties_the_log()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles"));
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };
        File.WriteAllLines(dir.File("log.txt"), Enumerable.Range(1, 600).Select(i => i % 100 == 0 ? $"WARN hotkeys {i}" : $"INFO line {i}"));
        File.WriteAllText(dir.File("log.1.txt"), "older");
        var log = new LogViewModel(vm, host, () => dir.File("log.txt"));

        var lines = log.Text.Split(Environment.NewLine);
        Assert.Equal(LogViewModel.ViewerLines, lines.Length);
        Assert.Equal(("INFO line 101", "WARN hotkeys 600"), (lines[0], lines[^1]));

        log.Filter = "HOTKEYS";
        Assert.Equal(["WARN hotkeys 200", "WARN hotkeys 300", "WARN hotkeys 400", "WARN hotkeys 500", "WARN hotkeys 600"], log.Text.Split(Environment.NewLine));

        log.SelectedLevel = log.Levels.Single(l => l.Value == LogLevel.Debug);
        Assert.Equal(SettingsChange.Log, vm.PendingChanges);

        log.ClearCommand.Execute(null);
        Assert.Equal("", File.ReadAllText(dir.File("log.txt")));
        Assert.False(File.Exists(dir.File("log.1.txt")));
        Assert.Equal("", log.Text);

        log.OpenFolderCommand.Execute(null);
        Assert.Equal([host.DataPaths.Root], host.Opened);
    }

    static TrayState State(bool subtitlesOn = false, bool muted = false, string provider = ProviderIds.Free, HotkeySettings? hotkeys = null) =>
        new("Claude  ·  claude-opus-5-5", subtitlesOn, HistoryVisible: true, muted, hotkeys ?? new HotkeySettings(),
            [new(ProviderIds.Free, "Free: Google, then MyMemory"), new(ProviderIds.Claude, "Claude"), new(ProviderIds.DeepL, "DeepL")], provider);

    [Fact]
    public void The_tray_menu_has_the_status_the_actions_with_their_hotkeys_and_the_rest_in_order()
    {
        var items = TrayMenu.Build(State());

        Assert.Equal(
        [
            TrayCommand.Status, TrayCommand.Separator, TrayCommand.Subtitles, TrayCommand.TranslateScreen, TrayCommand.History, TrayCommand.SelectRegion,
            TrayCommand.Clear, TrayCommand.Separator, TrayCommand.Providers, TrayCommand.Mute, TrayCommand.Separator, TrayCommand.Open,
            TrayCommand.OpenDataFolder, TrayCommand.Separator, TrayCommand.Exit,
        ], items.Select(i => i.Command));
        var status = items[0];
        Assert.Equal("Claude  ·  claude-opus-5-5  ·  Subtitles OFF", status.Text);
        Assert.False(status.Enabled);

        string Gesture(TrayCommand command) => items.Single(i => i.Command == command).Gesture;
        Assert.Equal("Ctrl+Shift+S", Gesture(TrayCommand.Subtitles));
        Assert.Equal("Ctrl+Shift+T", Gesture(TrayCommand.TranslateScreen));
        Assert.Equal("Ctrl+Shift+H", Gesture(TrayCommand.History));
        Assert.Equal("Ctrl+Shift+R", Gesture(TrayCommand.SelectRegion));
        Assert.Equal("Ctrl+Shift+X", Gesture(TrayCommand.Clear));
        Assert.Equal("", Gesture(TrayCommand.Open));
        Assert.Equal(("Open SLiveTranslator", "Exit"), (items[11].Text, items[^1].Text));
    }

    [Fact]
    public void The_tray_menu_checks_what_is_on_and_leaves_out_the_gesture_of_a_hotkey_that_is_off()
    {
        var hotkeys = new HotkeySettings();
        hotkeys.Disabled.Add(HotkeyAction.Clear);
        hotkeys[HotkeyAction.History] = "Ctrl+Alt+H";

        var items = TrayMenu.Build(State(subtitlesOn: true, muted: true, hotkeys: hotkeys));

        TrayItem Item(TrayCommand command) => items.Single(i => i.Command == command);
        Assert.True(Item(TrayCommand.Subtitles).Checked);
        Assert.True(Item(TrayCommand.History).Checked);
        Assert.True(Item(TrayCommand.Mute).Checked);
        Assert.False(Item(TrayCommand.TranslateScreen).Checked);
        Assert.Equal("", Item(TrayCommand.Clear).Gesture);
        Assert.Equal("Ctrl+Alt+H", Item(TrayCommand.History).Gesture);
        Assert.EndsWith("Subtitles ON", items[0].Text);
    }

    [Fact]
    public void The_provider_submenu_offers_each_provider_with_only_the_selected_one_checked()
    {
        var providers = TrayMenu.Build(State(provider: "CLAUDE")).Single(i => i.Command == TrayCommand.Providers).Items!;

        Assert.Equal(["Free: Google, then MyMemory", "Claude", "DeepL"], providers.Select(p => p.Text));
        Assert.All(providers, p => Assert.Equal(TrayCommand.Provider, p.Command));
        Assert.Equal([ProviderIds.Free, ProviderIds.Claude, ProviderIds.DeepL], providers.Select(p => p.Value));
        Assert.Equal([false, true, false], providers.Select(p => p.Checked));
    }

    [Fact]
    public void The_tray_follows_the_language()
    {
        var loc = Loc.Instance;
        var items = TrayMenu.Build(State());
        Assert.Equal(loc["Tray.Mute"], items.Single(i => i.Command == TrayCommand.Mute).Text);
        Assert.Equal(loc["Tray.Provider"], items.Single(i => i.Command == TrayCommand.Providers).Text);
    }

    [Fact]
    public void Every_text_family_of_the_new_pages_is_complete_in_both_languages()
    {
        var english = Loc.Strings(Loc.English);
        var vietnamese = Loc.Strings(Loc.Vietnamese);
        var needed = Enum.GetValues<FolderProblem>().Where(p => p != FolderProblem.None).Select(p => "Storage.Problem." + p)
            .Concat(Enum.GetValues<NoticeCategory>().SelectMany(c => new[] { "Notice.Category." + c, "Notice.CategoryHint." + c }))
            .Concat(Enum.GetValues<NoticePosition>().Select(p => "NoticePosition." + p))
            .Concat(Enum.GetValues<AppTheme>().Select(t => "Theme." + t))
            .Concat(Enum.GetValues<LogLevel>().Select(l => "LogLevel." + l))
            .Concat(Enum.GetValues<CacheScope>().Select(s => "Cache.Clear." + s))
            .ToList();
        Assert.Empty(needed.Where(k => !english.ContainsKey(k)).Select(k => "en: " + k));
        Assert.Empty(needed.Where(k => !vietnamese.ContainsKey(k)).Select(k => "vi: " + k));
        Assert.False(english.ContainsKey("Tray.SettingsClassic"));
        Assert.False(english.ContainsKey("Page.ComingSoon"));
    }

    [Fact]
    public void Nothing_refers_to_the_classic_settings_window_any_more()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SLiveTranslator.slnx"))) dir = dir.Parent;
        string src = Path.Combine(dir!.FullName, "src", "SLiveTranslator");
        var mentions = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .Where(f => File.ReadAllText(f) is var text && (text.Contains("SettingsWindow") || text.Contains("SettingsClassic")))
            .Select(Path.GetFileName);
        Assert.Empty(mentions);
        Assert.False(File.Exists(Path.Combine(src, "UI", "SettingsWindow.xaml")));
    }

    [Fact]
    public void A_moved_data_folder_gets_the_cache_the_profiles_and_the_usage_count_written_there()
    {
        using var dir = new TempDir();
        var cache = new CachedTranslator(new EchoTranslator(), dir.File(@"Old\cache.json"), 1000, TimeSpan.FromHours(1));
        cache.SetUserOverride("en", "vi", "Roach", "Roach");
        cache.Flush();
        var profiles = new GameProfileStore(dir.File(@"Old\Profiles"));
        var witcher = profiles.Find("witcher3")!;
        Directory.CreateDirectory(dir.File(@"New\Profiles"));
        foreach (var file in Directory.EnumerateFiles(dir.File(@"Old\Profiles")))
            File.Copy(file, Path.Combine(dir.File(@"New\Profiles"), Path.GetFileName(file)));
        var usage = new DailyCharCounter(dir.File(@"Old\mymemory-usage.json"));
        usage.Add(120);
        usage.Flush();

        cache.Relocate(dir.File(@"New\cache.json"));
        profiles.Relocate(dir.File(@"New\Profiles"));
        usage.Relocate(dir.File(@"New\mymemory-usage.json"));
        cache.Flush();
        usage.Flush();
        var copy = GameProfileStore.Copy(witcher);
        copy.Name = "Witcher 3 (moved)";
        profiles.Save(copy);

        Assert.Contains("\"Roach\"", File.ReadAllText(dir.File(@"New\cache.json"))); // written though nothing changed since
        Assert.Contains("\"chars\":120", File.ReadAllText(dir.File(@"New\mymemory-usage.json")));
        Assert.Equal(1, Directory.EnumerateFiles(dir.File(@"New\Profiles")).Count(f => File.ReadAllText(f).Contains("Witcher 3 (moved)")));
        Assert.DoesNotContain(Directory.EnumerateFiles(dir.File(@"Old\Profiles")), f => File.ReadAllText(f).Contains("Witcher 3 (moved)"));
        Assert.Same(copy, profiles.Find("witcher3"));
    }
}
