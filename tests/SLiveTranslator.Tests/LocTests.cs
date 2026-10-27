using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.UI;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>
/// The string tables and the language switch. Tests that switch languages use an instance of their own: the shared
/// <see cref="Loc.Instance"/> stays English for the whole run (see <see cref="TestEnvironment"/>).
/// </summary>
public class LocTests
{
    static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}");

    /// <summary>The shipped file as written, keeping duplicate keys (a dictionary would hide them).</summary>
    static List<(string Key, string Value)> Entries(string language)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"SLiveTranslator.Assets.Strings.{language}.json")!;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetString() ?? "")).ToList();
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SLiveTranslator.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("SLiveTranslator.slnx not found above " + AppContext.BaseDirectory);
    }

    static string SourceDir => Path.Combine(RepoRoot(), "src", "SLiveTranslator");

    static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(SourceDir, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    static SortedSet<string> Indexes(string text) => new(Placeholder.Matches(text).Select(m => m.Groups[1].Value));

    [Fact]
    public void Every_key_has_a_text_in_english_and_vietnamese_and_none_is_written_twice()
    {
        var english = Entries(Loc.English);
        var vietnamese = Entries(Loc.Vietnamese);

        Assert.Empty(english.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => "en: " + g.Key));
        Assert.Empty(vietnamese.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => "vi: " + g.Key));
        Assert.Equal(english.Select(e => e.Key).Order(), vietnamese.Select(e => e.Key).Order());
        Assert.Empty(english.Concat(vietnamese).Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key));
        Assert.True(english.Count > 100);
    }

    [Fact]
    public void Both_languages_use_the_same_placeholders()
    {
        var vietnamese = Entries(Loc.Vietnamese).ToDictionary(e => e.Key, e => e.Value);
        var mismatched = Entries(Loc.English)
            .Where(e => !Indexes(e.Value).SetEquals(Indexes(vietnamese[e.Key])))
            .Select(e => $"{e.Key}: en {{{string.Join(",", Indexes(e.Value))}}} vi {{{string.Join(",", Indexes(vietnamese[e.Key]))}}}");
        Assert.Empty(mismatched);
    }

    [Fact]
    public void Vietnamese_is_in_composed_form_and_written_with_its_diacritics()
    {
        var vietnamese = Entries(Loc.Vietnamese);
        Assert.Empty(vietnamese.Where(e => !e.Value.IsNormalized(NormalizationForm.FormC)).Select(e => e.Key));
        var texts = vietnamese.ToDictionary(e => e.Key, e => e.Value);
        Assert.Equal("Phụ đề: BẬT  ·  {0} để tắt  ·  {1} để xem lịch sử", texts["Notice.SubtitlesOn"]);
        Assert.Equal("Dịch màn hình", texts["Action.TranslateScreen"]);
        Assert.Equal("Đã lưu cài đặt", texts["Notice.SettingsSaved"]);
        Assert.Equal("Khóa API không hợp lệ.", texts["Check.InvalidKey"]);
    }

    [Fact]
    public void Every_formatted_text_formats_with_its_placeholders()
    {
        foreach (var language in new[] { Loc.English, Loc.Vietnamese })
        {
            var loc = new Loc(Loc.Strings(Loc.English), Loc.Strings(Loc.Vietnamese), () => "en");
            loc.SetLanguage(language);
            foreach (var (key, value) in Entries(language))
            {
                var args = Enumerable.Range(0, Indexes(value).Select(int.Parse).DefaultIfEmpty(-1).Max() + 1).Select(i => (object)(1000 + i)).ToArray();
                string formatted = loc.Format(key, args);
                Assert.DoesNotContain("{", formatted.Replace("{{", ""));
            }
        }
    }

    [Fact]
    public void Switching_the_language_tells_every_binding_once()
    {
        var loc = new Loc(Loc.Strings(Loc.English), Loc.Strings(Loc.Vietnamese), () => "en");
        var changed = new List<string?>();
        loc.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        loc.SetLanguage("vi");
        loc.SetLanguage("VI"); // no change
        Assert.Equal("Thoát", loc["Tray.Exit"]);
        Assert.Equal(Loc.Vietnamese, loc.Language);
        Assert.Equal("vi-VN", loc.Culture.Name);

        loc.SetLanguage("en");
        Assert.Equal("Exit", loc["Tray.Exit"]);
        Assert.Equal(2, changed.Count(p => p == Binding.IndexerName));
        Assert.Equal("Item[]", Binding.IndexerName);
    }

    [Theory]
    [InlineData("", "vi", Loc.Vietnamese)]
    [InlineData("", "fr", Loc.English)]
    [InlineData("", "en", Loc.English)]
    [InlineData("en", "vi", Loc.English)]
    [InlineData("vi", "en", Loc.Vietnamese)]
    [InlineData("de", "vi", Loc.Vietnamese)] // unknown: follows Windows
    public void An_empty_setting_follows_the_windows_display_language(string setting, string windows, string expected)
    {
        var loc = new Loc(new Dictionary<string, string>(), new Dictionary<string, string>(), () => windows);
        loc.SetLanguage(setting);
        Assert.Equal(expected, loc.Language);
    }

    [Fact]
    public void A_missing_text_falls_back_to_english_and_then_to_the_key()
    {
        var loc = new Loc(new Dictionary<string, string> { ["A"] = "Sword", ["B"] = "Shield", ["E"] = "Empty" },
            new Dictionary<string, string> { ["A"] = "Kiếm", ["E"] = "" }, () => "vi");

        Assert.Equal("Kiếm", loc["A"]);
        Assert.Equal("Shield", loc["B"]);
        Assert.Equal("Empty", loc["E"]);
        Assert.Equal("Nowhere.Key", loc["Nowhere.Key"]);
        Assert.True(loc.Has("B"));
        Assert.False(loc.Has("Nowhere.Key"));
    }

    [Fact]
    public void Format_uses_the_culture_of_the_language_and_survives_a_broken_text()
    {
        var english = new Dictionary<string, string> { ["N"] = "{0:N0} of {1:N0} characters", ["Broken"] = "{0 left" };
        var vietnamese = new Dictionary<string, string> { ["N"] = "{0:N0}/{1:N0} ký tự", ["Broken"] = "{0 còn lại" };
        var loc = new Loc(english, vietnamese, () => "en");

        Assert.Equal("40,000 of 50,000 characters", loc.Format("N", 40_000, 50_000));
        loc.SetLanguage("vi");
        Assert.Equal("40.000/50.000 ký tự", loc.Format("N", 40_000, 50_000));
        Assert.Equal("{0 còn lại", loc.Format("Broken", 3));
        Assert.Equal("Ctrl+Shift+S: subtitles   Ctrl+Shift+T: translate screen   Ctrl+Shift+H: history",
            Loc.F("Notice.Keys", "Ctrl+Shift+S", "Ctrl+Shift+T", "Ctrl+Shift+H"));
    }

    [Fact]
    public void A_bound_text_changes_with_the_language_without_a_restart()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var loc = new Loc(Loc.Strings(Loc.English), Loc.Strings(Loc.Vietnamese), () => "en");
                var text = new TextBlock();
                text.SetBinding(TextBlock.TextProperty, new Binding("[Region.Hint]") { Source = loc });
                Assert.StartsWith("Drag a rectangle", text.Text);

                loc.SetLanguage("vi");

                Assert.StartsWith("Kéo một hình chữ nhật", text.Text);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [Fact]
    public void The_region_select_window_binds_its_texts_to_the_shared_instance()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new RegionSelectWindow(System.Windows.Forms.Screen.PrimaryScreen!);
                var hint = (TextBlock)((Border)window.Root.Children[0]).Child;
                Assert.Equal(Loc.Instance["Region.Hint"], hint.Text);
                Assert.Equal(Loc.Instance["Region.Title"], window.Title);
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [Fact]
    public void Every_key_family_built_at_run_time_is_complete()
    {
        var keys = Entries(Loc.English).Select(e => e.Key).ToHashSet();
        var needed = Enum.GetValues<HotkeyAction>().Select(a => "Action." + a)
            .Concat(Enum.GetValues<HotkeyResult>().Select(r => "Hotkey.State." + r))
            .Concat(Enum.GetValues<HotkeyRule>().Where(r => r != HotkeyRule.None).Select(r => "Hotkey.Rule." + r))
            .Concat(new[] { HotkeyResult.Invalid, HotkeyResult.InUse, HotkeyResult.Reserved, HotkeyResult.Duplicate }.Select(r => "Hotkey.Why." + r))
            .Concat(Enum.GetValues<CheckStatus>().Select(s => "Check." + s))
            .Concat(Enum.GetValues<CheckStatus>().Where(s => s != CheckStatus.Ok).Select(s => "Reason." + s))
            .Concat(Enum.GetValues<NoticeCategory>().Select(c => "Notice.Test." + c))
            .Concat(from rule in new[] { "Alert.MyMemoryNearLimit.", "Alert.MyMemoryLimit." } from tail in new[] { "Email", "NoEmail" } select rule + tail);
        Assert.Empty(needed.Except(keys));
    }

    [Fact]
    public void Every_key_the_code_and_the_xaml_name_exists()
    {
        var keys = Entries(Loc.English).Select(e => e.Key).ToHashSet();
        var prefixes = keys.Select(k => k[..k.IndexOf('.')]).ToHashSet();
        var literal = new Regex("\"([A-Z][A-Za-z]+(?:\\.[A-Z][A-Za-z]+)+)\"");
        var binding = new Regex(@"\{Binding \[([^\]]+)\]");
        var used = SourceFiles("*.cs").SelectMany(f => literal.Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: m.Groups[1].Value)))
            .Where(u => prefixes.Contains(u.Key[..u.Key.IndexOf('.')]))
            .Concat(SourceFiles("*.xaml").SelectMany(f => binding.Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: m.Groups[1].Value))))
            .ToList();

        Assert.Contains(used, u => u.Key == "Badge.Paused"); // the scan finds keys in a conditional too
        Assert.Contains(used, u => u.File == "RegionSelectWindow.xaml");
        Assert.Empty(used.Where(u => !keys.Contains(u.Key)).Select(u => $"{u.File}: {u.Key}"));
    }

    [Fact]
    public void The_app_gives_the_notifier_texts_from_the_string_tables_only()
    {
        string app = File.ReadAllText(Path.Combine(SourceDir, "App.xaml.cs"));

        Assert.Matches(@"\.Feedback\(NoticeCategory\.\w+, Loc\.", app); // the pattern below sees the calls
        Assert.DoesNotMatch(@"\.(Notify|Feedback)\(NoticeCategory\.\w+,\s*\$?""", app);
        Assert.DoesNotMatch(@"\.Progress\(\$?""", app);
        Assert.DoesNotMatch(@"MessageBox\.Show\(\$?""", app);
        Assert.DoesNotMatch(@"StartupBalloon\(\$?""", app);
        Assert.DoesNotMatch(@"\bBalloon\(|ShowBalloonTip|ShowStatus\(", app);
        // Only the notifier's surface and the self-test draw a toast; every other notice goes through the notifier.
        var direct = SourceFiles("*.cs")
            .Where(f => File.ReadAllText(f).Contains(".ShowStatus("))
            .Select(f => Path.GetFileName(f))
            .Order()
            .ToList();
        Assert.Equal(["AppNoticeSurface.cs", "OverlaySelfTest.cs"], direct);
    }

    [Fact]
    public void A_key_check_reads_as_a_summary_for_the_provider_kind_with_the_raw_detail_under_it()
    {
        var models = new[] { new ModelOption("a", "A"), new ModelOption("b", "B"), new ModelOption("c", "C") };

        Assert.Equal("Key valid · 3 models", ProviderCheckText.Summary(ProviderKind.LLM, new ProviderCheck(CheckStatus.Ok, models, null, "")));
        Assert.Equal("Key valid · 2 engines", ProviderCheckText.Summary(ProviderKind.MT, new ProviderCheck(CheckStatus.Ok, models[..2], null, "")));
        Assert.Equal("This key's character quota is used up.", ProviderCheckText.Summary(ProviderKind.MT, ProviderCheck.Fail(CheckStatus.NoCredit, "")));
        Assert.Equal("The account has no credit left.", ProviderCheckText.Summary(ProviderKind.LLM, ProviderCheck.Fail(CheckStatus.NoCredit, "")));
        Assert.Equal("The API key is not valid.\nDeepL: HTTP 403: Wrong key",
            ProviderCheckText.WithDetail(ProviderKind.MT, ProviderCheck.Fail(CheckStatus.InvalidKey, "DeepL: HTTP 403: Wrong key")));
        Assert.Equal("The service did not answer in time.", ProviderCheckText.WithDetail(ProviderKind.LLM, ProviderCheck.Fail(CheckStatus.Timeout, "")));

        foreach (var kind in Enum.GetValues<ProviderKind>())
        foreach (var status in Enum.GetValues<CheckStatus>())
        {
            string summary = ProviderCheckText.Summary(kind, ProviderCheck.Fail(status, ""));
            Assert.False(summary.StartsWith("Check.", StringComparison.Ordinal), $"{kind} {status}: {summary}");
        }
    }

    [Fact]
    public void A_hotkey_explains_why_it_cannot_be_used()
    {
        Assert.Equal("F12 is reserved for debuggers", HotkeyText.Explain(HotkeyManager.Validate("F12")));
        Assert.Equal("needs Ctrl, Alt, Shift or Win (only F1–F24 work alone)", HotkeyText.Explain(HotkeyManager.Validate("T")));
        Assert.Equal("Windows reserves many Win shortcuts; this one may not work.", HotkeyText.Explain(HotkeyManager.Validate("Win+Alt+K")));
        Assert.Equal("", HotkeyText.Explain(HotkeyManager.Validate("Ctrl+Shift+T")));
        Assert.Equal("Subtitles on/off", HotkeyText.ActionName(HotkeyAction.ToggleSubtitles));
        Assert.Equal("In use", HotkeyText.State(HotkeyResult.InUse));
    }
}
