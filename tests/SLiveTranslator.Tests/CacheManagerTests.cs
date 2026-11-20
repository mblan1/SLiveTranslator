using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Pages;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The cache part of the Storage page on a cache in a temp folder: filters, hand fixes, delete, export and import, statistics and clearing.</summary>
public class CacheManagerTests
{
    static readonly TimeSpan NoBackgroundSave = TimeSpan.FromHours(1);

    sealed class Page : IDisposable
    {
        readonly TempDir _dir = new();

        public Page()
        {
            // Translations the services made, none edited; "claude" is a key from before keys named the model.
            System.IO.File.WriteAllText(_dir.File("cache.json"), """
                {"version":2,"entries":[
                {"p":"free-mt","s":"en","t":"vi","src":"Wind's howling.","tr":"Gió đang rít.","created":1,"used":1,"hits":0,"edited":false,"profileVer":0,"answeredBy":"google"},
                {"p":"free-mt","s":"en","t":"vi","src":"Where is Ciri?","tr":"Ciri ở đâu?","created":1,"used":1,"hits":0,"edited":false,"profileVer":0,"answeredBy":"google"},
                {"p":"claude/claude-opus-5-5","s":"en","t":"vi","src":"Roach is tired.","tr":"Roach mệt rồi.","created":1,"used":1,"hits":0,"edited":false,"profileVer":0,"answeredBy":"claude"},
                {"p":"claude","s":"en","t":"vi","src":"Old line.","tr":"Dòng cũ.","created":1,"used":1,"hits":0,"edited":false,"profileVer":0,"answeredBy":""},
                {"p":"free-mt","s":"en","t":"ja","src":"Where is Ciri?","tr":"シリはどこ?","created":1,"used":1,"hits":0,"edited":false,"profileVer":0,"answeredBy":"google"}
                ]}
                """);
            Cache = new CachedTranslator(new EchoTranslator(), _dir.File("cache.json"), 1000, NoBackgroundSave);
            Host = new FakeHost(_dir.File("Profiles")) { Cache = Cache };
            Vm = new SettingsViewModel(Host, new FakeScheduler().Delay) { Prompts = Prompts };
        }

        public CachedTranslator Cache { get; }
        public FakeHost Host { get; }
        public FakePrompts Prompts { get; } = new();
        public SettingsViewModel Vm { get; }
        public CacheManagerViewModel Manager => Vm.Storage.Manager;
        public string File(string name) => _dir.File(name);
        public void Dispose() => _dir.Dispose();
    }

    [Fact]
    public void The_list_is_searched_and_filtered_by_provider_edited_and_language_pair()
    {
        using var page = new Page();
        var manager = page.Manager;
        Assert.Equal(5, manager.Rows.Count);
        Assert.Equal("5 of 5 entries", manager.CountText);
        Assert.Equal(["All", "claude", "claude/claude-opus-5-5", "free-mt"], manager.ProviderChoices.Select(c => c.Label));
        Assert.Equal(["All", "en → ja", "en → vi"], manager.PairChoices.Select(c => c.Label));

        manager.Search = "ciri";
        Assert.Equal(2, manager.Rows.Count); // in the source or the translation, any case

        manager.SelectedPair = manager.PairChoices.Single(c => c.Value == "en → vi");
        Assert.Equal("Ciri ở đâu?", Assert.Single(manager.Rows).Translation);

        manager.Search = "";
        manager.SelectedProvider = manager.ProviderChoices.Single(c => c.Value == "claude/claude-opus-5-5");
        Assert.Equal("Roach is tired.", Assert.Single(manager.Rows).Source);

        manager.SelectedProvider = manager.ProviderChoices[0];
        manager.SelectedPair = manager.PairChoices[0];
        page.Cache.SetUserOverride("en", "vi", "Roach", "Roach");
        manager.Reload();
        manager.EditedOnly = true;
        Assert.Equal(ProviderKeys.User, Assert.Single(manager.Rows).Provider);
        Assert.Equal("1 of 6 entries", manager.CountText);
    }

    [Fact]
    public async Task Editing_a_translation_is_a_hand_fix_that_answers_for_every_provider()
    {
        using var page = new Page();
        var row = page.Manager.Rows.Single(r => r.Source == "Wind's howling.");

        row.Translation = "  Gió hú.  ";

        Assert.Equal((ProviderKeys.User, true, "Gió hú."), (row.Provider, row.Edited, row.Translation));
        var fix = page.Cache.Snapshot(new CacheFilter(Provider: ProviderKeys.User)).Single();
        Assert.Equal(("Wind's howling.", "Gió hú.", true), (fix.Source, fix.Translation, fix.Edited));
        // The fix answers first whatever provider translates now.
        page.Cache.Inner = new NamedEcho("deepl/latency_optimized");
        var result = await page.Cache.TranslateAsync(new TranslateRequest(["Wind's howling."], "en", "vi", [], null), CancellationToken.None);
        Assert.Equal("Gió hú.", result.Texts[0]);
        Assert.Contains(page.Vm.Storage.Stats, s => s.Provider == ProviderKeys.User); // the statistics followed

        row.Translation = ""; // emptied: the fix goes
        Assert.Empty(page.Cache.Snapshot(new CacheFilter(Provider: ProviderKeys.User)));
    }

    sealed class NamedEcho(string name) : ITranslator
    {
        public string Name => name;

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(texts.Select(t => "echo:" + t).ToList());
    }

    [Fact]
    public void Delete_removes_the_selected_entry_from_the_cache_and_the_list()
    {
        using var page = new Page();
        var manager = page.Manager;
        Assert.False(manager.DeleteCommand.CanExecute(null));
        manager.Selected = manager.Rows.Single(r => r.Source == "Roach is tired.");

        manager.DeleteCommand.Execute(null);

        Assert.Equal(4, manager.Rows.Count);
        Assert.Equal("4 of 4 entries", manager.CountText);
        Assert.DoesNotContain(page.Cache.Snapshot(), e => e.Source == "Roach is tired.");
        Assert.Null(manager.Selected);
    }

    [Fact]
    public void Export_and_import_round_trip_and_the_users_own_entries_win()
    {
        using var page = new Page();
        page.Cache.SetUserOverride("en", "vi", "Roach", "Roach");
        page.Prompts.SaveFile = page.File("export.json");
        page.Manager.ExportCommand.Execute(null);
        Assert.Equal(MessageKind.Success, page.Prompts.Messages.Last().Kind);

        using var other = new TempDir();
        var fresh = new CachedTranslator(new EchoTranslator(), other.File("cache.json"), 1000, NoBackgroundSave);
        fresh.Set(ProviderKeys.Free, "en", "vi", "Wind's howling.", "Gió hú."); // edited by hand in this cache
        var host = new FakeHost(other.File("Profiles")) { Cache = fresh };
        var prompts = new FakePrompts { OpenFile = page.File("export.json") };
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = prompts };

        vm.Storage.Manager.ImportCommand.Execute(null);

        Assert.Equal(6, vm.Storage.Manager.Rows.Count);
        Assert.Equal(page.Cache.Snapshot().Select(e => (e.Provider, e.Source)).Order(), fresh.Snapshot().Select(e => (e.Provider, e.Source)).Order());
        Assert.Equal("Gió hú.", fresh.Snapshot(new CacheFilter("Wind's howling.", ProviderKeys.Free)).Single().Translation); // the user's edit stays
        Assert.Equal("Roach", fresh.Snapshot(new CacheFilter(Provider: ProviderKeys.User)).Single().Translation);
        Assert.Equal(("Imported 5 entries from export.json. Lines you fixed here keep your fix: 1.", MessageKind.Success),
            (prompts.Messages.Last().Text, prompts.Messages.Last().Kind));

        page.Cache.SetUserOverride("en", "vi", "Wind's howling.", "Gió đang hú.");
        page.Cache.Set(ProviderKeys.Free, "en", "vi", "Wind's howling.", "Gió rít."); // fixed differently there
        page.Manager.ExportCommand.Execute(null);
        vm.Storage.Manager.ImportCommand.Execute(null);
        Assert.Equal("Gió hú.", fresh.Snapshot(new CacheFilter("Wind's howling.", ProviderKeys.Free)).Single().Translation); // still the user's edit here
        Assert.EndsWith("Lines you fixed here keep your fix: 1.", prompts.Messages.Last().Text); // the same fix in both is nothing to keep

        prompts.OpenFile = other.File("missing.json");
        vm.Storage.Manager.ImportCommand.Execute(null);
        Assert.Equal(MessageKind.Error, prompts.Messages.Last().Kind);
    }

    [Fact]
    public async Task Each_clear_asks_first_names_how_many_go_and_removes_only_its_scope()
    {
        using var page = new Page();
        var storage = page.Vm.Storage;
        Assert.Equal("free-mt", storage.Stats[0].Provider); // the most entries first
        Assert.Equal(["claude", "claude/claude-opus-5-5", "free-mt"], storage.Stats.Select(s => s.Provider).Order());
        Assert.StartsWith("5 entries", storage.StatsTotal);

        page.Prompts.Confirm = false;
        await storage.ClearOrphansCommand.ExecuteAsync(null);
        Assert.Equal(5, page.Cache.Count);
        Assert.Equal("Remove 1 entries kept under names no service uses any more?", page.Prompts.Questions.Last());

        page.Prompts.Confirm = true;
        await storage.ClearOrphansCommand.ExecuteAsync(null);
        Assert.DoesNotContain(page.Cache.Snapshot(), e => e.Provider == "claude");
        Assert.Equal("Removed 1 entries.", page.Prompts.Messages.Last().Text);

        storage.SelectedClearProvider = storage.ClearProviderChoices.Single(c => c.Value == "claude/claude-opus-5-5");
        await storage.ClearProviderCommand.ExecuteAsync(null);
        Assert.Equal("Remove the 1 entries of claude/claude-opus-5-5?", page.Prompts.Questions.Last());
        Assert.Equal([ProviderKeys.Free], page.Cache.Snapshot().Select(e => e.Provider).Distinct());
        Assert.Equal(3, page.Manager.Rows.Count); // the list followed

        await storage.ClearAllCommand.ExecuteAsync(null);
        Assert.Equal(0, page.Cache.Count);
        Assert.Empty(storage.Stats);
    }

    [Fact]
    public void The_largest_cache_is_an_edit_of_the_settings_within_its_limits()
    {
        using var page = new Page();
        page.Vm.Storage.MaxEntries = 50;
        Assert.Equal(StorageViewModel.MinCacheEntries, page.Vm.Working.Storage.CacheMaxEntries);
        page.Vm.Storage.MaxEntries = 40_000;
        Assert.Equal(40_000, page.Vm.Working.Storage.CacheMaxEntries);
        Assert.Equal(SettingsChange.Translation, page.Vm.PendingChanges);
    }

    [Fact]
    public void The_history_fixes_a_line_in_the_cache_and_on_the_overlay()
    {
        using var page = new Page();
        page.Host.History.Add(new SubtitleRecord(7, "Wind's howling.", "Gió đang rít.", ProviderKeys.Free));
        page.Host.History.Add(new SubtitleRecord(8, "Press [E]\nto talk", "Nhấn [E]\nđể nói", ProviderKeys.Free));
        var history = page.Vm.History;
        history.Refresh();
        Assert.Equal([8, 7], history.Rows.Select(r => r.Index)); // newest first

        history.Selected = history.Rows.Single(r => r.Index == 7);
        Assert.Equal("Gió đang rít.", history.FixText);
        history.FixText = "Gió hú.";
        history.FixCommand.Execute(null);

        Assert.Equal("Gió hú.", page.Cache.Snapshot(new CacheFilter(Provider: ProviderKeys.User)).Single().Translation);
        Assert.Equal(("Wind's howling.", "Gió hú."), Assert.Single(page.Host.Fixes));
        Assert.Equal((ProviderKeys.User, "Gió hú."), (history.Selected!.Provider, history.Selected.Translation));

        history.Selected = history.Rows.Single(r => r.Index == 8);
        history.FixText = "Nhấn [E] để nói"; // one line for a source of two
        history.FixCommand.Execute(null);
        Assert.Equal(MessageKind.Error, page.Prompts.Messages.Last().Kind);
        Assert.Single(page.Host.Fixes);

        history.FixText = "Nhấn [E]\r\nđể nói chuyện";
        history.FixCommand.Execute(null);
        Assert.Equal(("Press [E]\nto talk", "Nhấn [E]\nđể nói chuyện"), page.Host.Fixes[^1]);
    }

    [Fact]
    public async Task The_history_removes_a_line_and_clears_after_asking()
    {
        using var page = new Page();
        foreach (int id in new[] { 1, 2, 3 }) page.Host.History.Add(new SubtitleRecord(id, $"Line {id}", $"Dòng {id}", ProviderKeys.Free));
        var history = page.Vm.History;
        history.Refresh();
        Assert.False(history.RemoveCommand.CanExecute(null)); // nothing selected
        Assert.True(history.ClearCommand.CanExecute(null));

        history.Selected = history.Rows.Single(r => r.Index == 2);
        history.RemoveCommand.Execute(null);

        Assert.Equal([1, 3], page.Host.History.Select(r => r.Id));
        Assert.Equal([3, 1], history.Rows.Select(r => r.Index));
        Assert.Equal(1, history.Selected!.Index); // the next line, ready for another Remove

        page.Prompts.Confirm = false;
        await history.ClearCommand.ExecuteAsync(null);
        Assert.Equal(0, page.Host.HistoryClears);
        Assert.Equal(2, history.Rows.Count);
        Assert.Contains("2", page.Prompts.Questions.Last());

        page.Prompts.Confirm = true;
        await history.ClearCommand.ExecuteAsync(null);
        Assert.Equal(1, page.Host.HistoryClears);
        Assert.True(history.IsEmpty);
        Assert.False(history.ClearCommand.CanExecute(null));
    }
}
