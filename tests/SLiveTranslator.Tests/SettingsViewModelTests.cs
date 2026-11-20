using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Pages;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The shared view model of the main window: what an edit touches, Save, Discard, and settings the app changed meanwhile.</summary>
public class SettingsViewModelTests
{
    public static TheoryData<string, SettingsChange> Edits => new()
    {
        { "overlay font size", SettingsChange.Overlay },
        { "overlay position", SettingsChange.Overlay },
        { "subtitle interval", SettingsChange.Subtitles },
        { "subtitle region", SettingsChange.Subtitles },
        { "provider", SettingsChange.Translation },
        { "claude model", SettingsChange.Translation },
        { "context lines", SettingsChange.Translation },
        { "game profile", SettingsChange.Translation },
        { "target language", SettingsChange.Translation | SettingsChange.Subtitles },
        { "ocr language", SettingsChange.Ocr | SettingsChange.Subtitles },
        { "cache size", SettingsChange.Translation },
        { "hotkey", SettingsChange.Hotkeys },
        { "hotkey off", SettingsChange.Hotkeys },
        { "notice seconds", SettingsChange.Notifications },
        { "ui language", SettingsChange.Language },
        { "theme", SettingsChange.Theme },
        { "log level", SettingsChange.Log },
        { "first run", SettingsChange.Other },
        { "nothing", SettingsChange.None },
    };

    static void Edit(AppSettings s, string edit)
    {
        switch (edit)
        {
            case "overlay font size": s.Overlay.FontSize = 30; break;
            case "overlay position": s.Overlay.Position = SubtitlePosition.BottomCenter; break;
            case "subtitle interval": s.Subtitles.IntervalMs = 500; break;
            case "subtitle region": s.Subtitles.Region = new RegionSettings(10, 20, 800, 90); break;
            case "provider": s.Translation.Provider = ProviderIds.Gemini; break;
            case "claude model": s.Translation.Claude.Model = "claude-haiku-5-5"; break;
            case "context lines": s.Translation.ContextLines = 3; break;
            case "game profile": s.Translation.ProfileId = "none"; break;
            case "target language": s.General.TargetLanguage = "ja"; break;
            case "ocr language": s.General.OcrLanguage = "en-US"; break;
            case "cache size": s.Storage.CacheMaxEntries = 500; break;
            case "hotkey": s.Hotkeys[HotkeyAction.History] = "Ctrl+Alt+H"; break;
            case "hotkey off": s.Hotkeys.Disabled.Add(HotkeyAction.Clear); break;
            case "notice seconds": s.Notifications.Warning.Seconds = 3; break;
            case "ui language": s.Ui.Language = "vi"; break;
            case "theme": s.General.Theme = AppTheme.Dark; break;
            case "log level": s.Storage.LogLevel = LogLevel.Debug; break;
            case "first run": s.General.FirstRunDone = true; break;
        }
    }

    [Theory]
    [MemberData(nameof(Edits))]
    public void An_edit_names_only_the_parts_of_the_app_it_touches(string edit, SettingsChange expected)
    {
        var before = new AppSettings { General = { TargetLanguage = "vi" } };
        var after = SettingsStore.Clone(before);
        Edit(after, edit);

        Assert.Equal(expected, SettingsDiff.Between(before, after));
    }

    [Fact]
    public void Two_edits_name_both_parts_and_nothing_else()
    {
        var before = new AppSettings();
        var after = SettingsStore.Clone(before);
        after.Overlay.MaxLines = 2;
        after.Hotkeys[HotkeyAction.Clear] = "Ctrl+Alt+X";

        Assert.Equal(SettingsChange.Overlay | SettingsChange.Hotkeys, SettingsDiff.Between(before, after));
    }

    static (FakeHost Host, SettingsViewModel Vm, FakePrompts Prompts, FakeScheduler Clock) Make(TempDir dir, params ITranslationProvider[] providers)
    {
        var host = new FakeHost(dir.File("Profiles"), providers);
        var clock = new FakeScheduler();
        var vm = new SettingsViewModel(host, clock.Delay);
        var prompts = new FakePrompts();
        vm.Prompts = prompts;
        return (host, vm, prompts, clock);
    }

    [Fact]
    public void Opening_changes_nothing_and_an_edit_waits_for_save()
    {
        using var dir = new TempDir();
        var (host, vm, _, _) = Make(dir);

        Assert.False(vm.HasChanges);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.Overlay.FontSize = 30;

        Assert.True(vm.HasChanges);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal(OverlaySettings.DefaultFontSize, host.Current.Overlay.FontSize); // the settings in use are untouched until Save
        Assert.Equal(SettingsChange.Overlay, vm.PendingChanges);

        vm.Overlay.FontSize = OverlaySettings.DefaultFontSize; // edited back
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void Save_hands_the_app_the_edits_and_what_they_touch()
    {
        using var dir = new TempDir();
        var (host, vm, prompts, _) = Make(dir);
        var inUse = host.Current;

        vm.Overlay.FontSize = 30;
        vm.Overlay.SelectedPosition = vm.Overlay.Positions.Single(p => p.Value == SubtitlePosition.AboveRegion);
        vm.Subtitles.IntervalMs = 450;

        Assert.True(vm.Save());

        var (saved, changes) = Assert.Single(host.Saves);
        Assert.Equal(SettingsChange.Overlay | SettingsChange.Subtitles, changes);
        Assert.NotSame(inUse, saved);
        Assert.NotSame(vm.Working, saved); // later edits never reach the settings in use without a save
        Assert.Equal((30, SubtitlePosition.AboveRegion, 450), (saved.Overlay.FontSize, saved.Overlay.Position, saved.Subtitles.IntervalMs));
        Assert.False(vm.HasChanges);
        Assert.Equal(MessageKind.Success, prompts.Messages.Last().Kind);

        vm.Overlay.FontSize = 31;
        Assert.Equal(30, host.Current.Overlay.FontSize);
    }

    [Fact]
    public void A_save_that_could_not_be_written_keeps_the_edits()
    {
        using var dir = new TempDir();
        var (host, vm, prompts, _) = Make(dir);
        host.FailSave = true;
        vm.Subtitles.LowImpact = true;

        Assert.False(vm.Save());

        Assert.True(vm.HasChanges);
        Assert.True(vm.Working.Subtitles.LowImpact);
        Assert.Equal(("Settings not saved", "the disk is full", MessageKind.Error), prompts.Messages.Last());
    }

    [Fact]
    public void Discard_shows_the_settings_in_use_again()
    {
        using var dir = new TempDir();
        var (host, vm, _, _) = Make(dir);
        vm.Overlay.ShowOriginal = true;
        vm.Translation.UseContext = false;

        vm.Discard();

        Assert.False(vm.HasChanges);
        Assert.False(vm.Overlay.ShowOriginal);
        Assert.True(vm.Translation.UseContext);
        Assert.Empty(host.Saves);
    }

    [Fact]
    public void A_region_the_app_saved_meanwhile_shows_on_the_page_and_survives_the_save_of_other_edits()
    {
        using var dir = new TempDir();
        var (host, vm, _, _) = Make(dir);
        vm.Overlay.FontSize = 28;

        host.ChangeOutside(s => s.Subtitles.Region = new RegionSettings(100, 900, 1200, 120));

        Assert.Equal("1200 × 120 px at (100, 900)", vm.Subtitles.RegionText);
        Assert.True(vm.HasChanges); // the font size is still to save
        Assert.Equal(SettingsChange.Overlay, vm.PendingChanges);
        Assert.True(vm.Save());
        var (saved, changes) = host.Saves.Single();
        Assert.Equal(SettingsChange.Overlay, changes);
        Assert.Equal(new RegionSettings(100, 900, 1200, 120), saved.Subtitles.Region);
        Assert.Equal(28, saved.Overlay.FontSize);
    }

    [Fact]
    public void A_change_the_app_made_without_telling_is_kept_by_the_save_too()
    {
        using var dir = new TempDir();
        var (host, vm, _, _) = Make(dir);
        vm.Overlay.MaxLines = 2;
        host.Current.Notifications.Muted = true; // the tray's mute, while the window was open

        Assert.True(vm.Save());

        var (saved, changes) = host.Saves.Single();
        Assert.True(saved.Notifications.Muted);
        Assert.Equal(2, saved.Overlay.MaxLines);
        Assert.Equal(SettingsChange.Overlay, changes);
    }

    [Fact]
    public void A_hotkey_switched_back_on_stays_on_when_the_app_saves_a_region_meanwhile()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles"));
        host.Current.Hotkeys.Disabled.Add(HotkeyAction.Clear);
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };

        vm.Working.Hotkeys.Disabled.Remove(HotkeyAction.Clear); // as the Hotkeys page's switch does
        vm.Edited();
        host.ChangeOutside(s => s.Subtitles.Region = new RegionSettings(0, 900, 1600, 120));

        Assert.DoesNotContain(HotkeyAction.Clear, vm.Working.Hotkeys.Disabled);
        Assert.Equal(new RegionSettings(0, 900, 1600, 120), vm.Working.Subtitles.Region);
        Assert.True(vm.Save());
        var (saved, changes) = host.Saves.Single();
        Assert.Empty(saved.Hotkeys.Disabled);
        Assert.Equal(new RegionSettings(0, 900, 1600, 120), saved.Subtitles.Region);
        Assert.Equal(SettingsChange.Hotkeys, changes);
    }

    [Fact]
    public void What_the_app_took_out_of_a_list_meanwhile_is_not_brought_back_by_a_save()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles"));
        host.Current.Hotkeys.Disabled.Add(HotkeyAction.Clear);
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };

        host.Current.Hotkeys.Disabled.Remove(HotkeyAction.Clear); // switched back on elsewhere while the window was open
        host.Current.Hotkeys.Map.Remove(HotkeyAction.History); // and a gesture back to its default
        vm.Overlay.MaxLines = 2;

        Assert.True(vm.Save());
        var saved = host.Saves.Single().Settings;
        Assert.Empty(saved.Hotkeys.Disabled);
        Assert.False(saved.Hotkeys.Map.ContainsKey(HotkeyAction.History));
        Assert.Equal(2, saved.Overlay.MaxLines);
    }

    [Theory]
    [InlineData(SettingsChange.Overlay, false, "Restyle")]
    [InlineData(SettingsChange.Overlay, true, "Restyle")]
    [InlineData(SettingsChange.Subtitles, false, "")]
    [InlineData(SettingsChange.Subtitles, true, "RestartSubtitles")]
    [InlineData(SettingsChange.Ocr | SettingsChange.Subtitles, true, "RestartSubtitles RebuildOcr")]
    [InlineData(SettingsChange.Translation, true, "RebuildProviders")]
    [InlineData(SettingsChange.Translation | SettingsChange.Subtitles, false, "RebuildProviders")]
    [InlineData(SettingsChange.Hotkeys, true, "RegisterHotkeys")]
    [InlineData(SettingsChange.Notifications, true, "")] // the notices read their settings when they show
    [InlineData(SettingsChange.Language | SettingsChange.Theme | SettingsChange.Log, false, "Language Log Theme")]
    [InlineData(SettingsChange.Other, true, "")]
    public void A_save_redoes_only_what_it_changed(SettingsChange changes, bool subtitlesRunning, string expected)
    {
        var redo = SaveActions.For(changes, subtitlesRunning);

        var done = new[]
        {
            ("Language", redo.Language), ("Log", redo.Log), ("RestartSubtitles", redo.RestartSubtitles), ("RebuildOcr", redo.RebuildOcr),
            ("RebuildProviders", redo.RebuildProviders), ("RegisterHotkeys", redo.RegisterHotkeys), ("Restyle", redo.Restyle), ("Theme", redo.Theme),
        }.Where(a => a.Item2).Select(a => a.Item1);
        Assert.Equal(expected, string.Join(" ", done));
    }

    [Fact]
    public void A_number_being_typed_shows_the_save_bar_until_the_box_takes_it()
    {
        using var dir = new TempDir();
        var (_, vm, _, _) = Make(dir);

        vm.Typing = true;
        Assert.True(vm.HasChanges);
        Assert.True(vm.SaveCommand.CanExecute(null));

        vm.Typing = false; // the box took the number, which was the one saved
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void A_language_switch_writes_the_lists_again_and_keeps_what_is_selected()
    {
        using var dir = new TempDir();
        var claude = new FakeProvider(ProviderIds.Claude);
        var host = new FakeHost(dir.File("Profiles"), claude);
        host.Current.Translation.Provider = ProviderIds.Claude;
        host.Current.Overlay.Position = SubtitlePosition.AboveRegion;
        host.Current.Subtitles.OcrMode = OcrMode.Plain;
        var language = new FakeLanguage();
        SettingsUi.Run(() =>
        {
            var vm = new SettingsViewModel(host, new FakeScheduler().Delay, language);
            var card = vm.Translation.Cards.Single();
            card.KeyText = "sk-ant-typed";
            _ = card.CheckAsync();
            claude.Checks[0].Reply(SettingsUi.Ok(new ModelOption("claude-opus-5-5", "Claude Opus 5.5", ModelTags.Recommended)));
            var before = (vm.Overlay.Positions, vm.Subtitles.OcrModes, vm.Translation.ProviderChoices, card.EffortChoices, card.PresetChoices, Model: card.SelectedModel);
            var raised = new List<string?>();
            vm.Overlay.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            language.Switch();

            Assert.NotSame(before.Positions, vm.Overlay.Positions);
            Assert.Equal([nameof(OverlayViewModel.Positions), nameof(OverlayViewModel.SelectedPosition)], raised); // the list first, so the combo keeps its pick
            Assert.Equal(SubtitlePosition.AboveRegion, vm.Overlay.SelectedPosition.Value);
            Assert.NotSame(before.OcrModes, vm.Subtitles.OcrModes);
            Assert.Equal(OcrMode.Plain, vm.Subtitles.SelectedOcrMode.Value);
            Assert.NotSame(before.ProviderChoices, vm.Translation.ProviderChoices);
            Assert.Equal(ProviderIds.Claude, vm.Translation.SelectedProvider.Value);
            Assert.NotSame(before.EffortChoices, card.EffortChoices);
            Assert.NotSame(before.PresetChoices, card.PresetChoices);
            Assert.NotSame(before.Model, card.SelectedModel); // the model texts are written again
            Assert.Equal("claude-opus-5-5", card.SelectedModel!.Id);
            Assert.Equal(KeyCheckState.Valid, card.State);
            Assert.Equal("sk-ant-typed", card.KeyText);
            Assert.False(vm.PendingChanges.HasFlag(SettingsChange.Overlay));

            vm.Dispose();
            var positions = vm.Overlay.Positions;
            language.Switch(); // a closed window no longer listens
            Assert.Same(positions, vm.Overlay.Positions);
        });
    }

    sealed class FakeLanguage : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Switch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(System.Windows.Data.Binding.IndexerName));
    }

    [Fact]
    public void Any_card_can_be_set_up_before_its_provider_is_picked_and_an_open_card_checks_its_saved_key()
    {
        using var dir = new TempDir();
        var claude = new FakeProvider(ProviderIds.Claude);
        var gemini = new FakeProvider(ProviderIds.Gemini);
        var host = new FakeHost(dir.File("Profiles"), claude, gemini);
        host.Current.Translation.Gemini.ApiKey = "gemini-saved";
        var clock = new FakeScheduler();
        SettingsUi.Run(() =>
        {
            var vm = new SettingsViewModel(host, clock.Delay);
            var (claudeCard, geminiCard) = (vm.Translation.Cards[0], vm.Translation.Cards[1]);
            geminiCard.IsExpanded = true; // the page is not on screen yet: nothing is checked
            Assert.Empty(gemini.Checks);

            vm.Translation.PageShown();
            Assert.Equal("gemini-saved", Assert.Single(gemini.Checks).Credentials.ApiKey);
            Assert.False(claudeCard.IsExpanded); // the free services are in use: no card opens by itself
            Assert.False(geminiCard.IsInUse);

            claudeCard.IsExpanded = true;
            claudeCard.KeyText = "sk-ant-typed";
            clock.Advance(800);
            Assert.Equal("sk-ant-typed", Assert.Single(claude.Checks).Credentials.ApiKey);
            Assert.Equal(ProviderIds.Free, vm.Translation.SelectedProvider.Value); // set up, not picked

            vm.Translation.SelectedProvider = vm.Translation.ProviderChoices.Single(c => c.Value == ProviderIds.Claude);
            Assert.True(claudeCard.IsInUse);
            Assert.False(geminiCard.IsInUse);
            Assert.Single(claude.Checks); // its check is under way already
        });
    }

    [Fact]
    public void Discard_while_the_page_shows_checks_the_saved_key_again()
    {
        using var dir = new TempDir();
        var claude = new FakeProvider(ProviderIds.Claude);
        var host = new FakeHost(dir.File("Profiles"), claude);
        host.Current.Translation.Provider = ProviderIds.Claude;
        host.Current.Translation.Claude.ApiKey = "sk-ant-saved";
        var clock = new FakeScheduler();
        var opus = new ModelOption("claude-opus-5-5", "Claude Opus 5.5", ModelTags.Recommended);
        SettingsUi.Run(() =>
        {
            var vm = new SettingsViewModel(host, clock.Delay) { Prompts = new FakePrompts() };
            vm.Translation.PageShown();
            var card = vm.Translation.SelectedCard!;
            Assert.True(card.IsExpanded);
            claude.Checks[0].Reply(SettingsUi.Ok(opus));
            card.KeyText = "sk-ant-other";
            clock.Advance(800);
            claude.Checks[1].Reply(ProviderCheck.Fail(CheckStatus.InvalidKey, ""));

            vm.Discard();

            Assert.Equal("sk-ant-saved", claude.Checks[2].Credentials.ApiKey);
            claude.Checks[2].Reply(SettingsUi.Ok(opus));
            Assert.Equal(KeyCheckState.Valid, card.State);
            Assert.True(card.IsModelEnabled);

            vm.Translation.PageHidden();
            card.KeyText = "sk-ant-other";
            vm.Discard(); // the page is not on screen: its next showing checks
            Assert.Equal(3, claude.Checks.Count);
            vm.Translation.PageShown();
            Assert.Equal(4, claude.Checks.Count);
        });
    }

    [Fact]
    public void A_typed_key_is_a_change_and_is_saved_encrypted_once()
    {
        using var dir = new TempDir();
        var claude = new FakeProvider(ProviderIds.Claude);
        var (host, vm, _, _) = Make(dir, claude);
        var card = vm.Translation.Cards.Single();

        card.KeyText = "sk-ant-typed";

        Assert.True(vm.HasChanges);
        Assert.Equal("", vm.Working.Translation.Claude.ApiKeyProtected); // the key stays in the card until Save
        Assert.True(vm.Save());
        var (saved, changes) = host.Saves.Single();
        Assert.Equal(SettingsChange.Translation, changes);
        Assert.Equal("sk-ant-typed", saved.Translation.Claude.ApiKey);
        Assert.Equal("", card.KeyText);
        Assert.True(card.HasSavedKey);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void A_typed_key_stays_when_the_app_changes_settings_of_its_own()
    {
        using var dir = new TempDir();
        var (host, vm, _, _) = Make(dir, new FakeProvider(ProviderIds.Claude));
        var card = vm.Translation.Cards.Single();
        card.KeyText = "sk-ant-typed";

        host.ChangeOutside(s => s.Subtitles.Region = new RegionSettings(0, 0, 640, 80));

        Assert.Equal("sk-ant-typed", card.KeyText);
        Assert.True(vm.HasChanges);
    }

    [Fact]
    public void Picking_a_provider_shows_its_card_and_checks_its_saved_key()
    {
        using var dir = new TempDir();
        var claude = new FakeProvider(ProviderIds.Claude);
        var gemini = new FakeProvider(ProviderIds.Gemini);
        var host = new FakeHost(dir.File("Profiles"), claude, gemini);
        host.Current.Translation.Gemini.ApiKey = "gemini-saved";
        SettingsUi.Run(() =>
        {
            var vm = new SettingsViewModel(host, new FakeScheduler().Delay);
            Assert.True(vm.Translation.IsFree);
            vm.Translation.PageShown();
            Assert.Empty(gemini.Checks);

            vm.Translation.SelectedProvider = vm.Translation.ProviderChoices.Single(c => c.Value == ProviderIds.Gemini);

            Assert.Same(vm.Translation.Cards[1], vm.Translation.SelectedCard);
            Assert.Equal("gemini-saved", Assert.Single(gemini.Checks).Credentials.ApiKey);
            Assert.Empty(claude.Checks);
            Assert.Equal(SettingsChange.Translation, vm.PendingChanges);
        });
    }

    [Fact]
    public void A_provider_this_version_does_not_know_stays_unless_another_is_picked()
    {
        using var dir = new TempDir();
        var (host, vm, _, _) = Make(dir, new FakeProvider(ProviderIds.Claude));
        host.Current.Translation.Provider = "future-provider";
        vm.Discard(); // reads the settings in use again

        Assert.Equal(ProviderIds.Free, vm.Translation.SelectedProvider.Value);
        vm.Overlay.FontSize = 24;
        Assert.True(vm.Save());
        Assert.Equal("future-provider", host.Saves.Single().Settings.Translation.Provider);
    }

    [Fact]
    public void The_home_page_reads_the_settings_in_use_and_the_live_state()
    {
        using var dir = new TempDir();
        var (host, vm, _, _) = Make(dir, new FakeProvider(ProviderIds.Claude));
        host.Current.Translation.Provider = ProviderIds.Claude;
        host.Current.Translation.Claude.Model = "claude-opus-5-5";
        host.FreeServices = new FreeServiceStatus(new DateTime(2026, 10, 28, 14, 30, 0), 4_000, 5_000);

        Assert.Equal("Fake claude  ·  claude-opus-5-5", vm.Home.ProviderLine);
        Assert.Equal("Google Translate: blocked on this network until 2:30 PM; MyMemory translates meanwhile", vm.Home.GoogleStatus);
        Assert.Equal("MyMemory today: 4,000 of 5,000 free characters", vm.Home.MyMemoryText);
        Assert.Equal(0.8, vm.Home.MyMemoryFraction, 3);
        Assert.Equal([1, 2, 3, 4], vm.Home.Recommendations.Select(r => r.Number));
        Assert.All(vm.Home.Recommendations, r => Assert.False(r.Done));
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, vm.Home.Hotkeys.Count);
        Assert.Equal(("Translate screen", "Ctrl+Shift+T", "OK"), (vm.Home.Hotkeys[0].Action, vm.Home.Hotkeys[0].Gesture, vm.Home.Hotkeys[0].State));

        vm.Home.SubtitlesOn = true;
        Assert.True(host.SubtitlesOn);
        var changed = 0;
        vm.Home.PropertyChanged += (_, _) => changed++;
        host.RaiseStateChanged();
        Assert.True(changed > 0);
    }

    [Fact]
    public void The_overlay_preview_shows_the_look_on_the_page_without_saving_it()
    {
        using var dir = new TempDir();
        var (host, vm, _, _) = Make(dir);
        vm.Overlay.BackgroundOpacity = 40;
        vm.Overlay.TextOpacity = 10; // below the lowest allowed

        vm.Overlay.PreviewCommand.Execute(null);

        Assert.Equal((0.4, 0.2), (host.Previewed!.BackgroundOpacity, host.Previewed.TextOpacity));
        Assert.NotSame(vm.Working.Overlay, host.Previewed);
        Assert.Empty(host.Saves);
    }

    [Fact]
    public async Task Test_translation_uses_the_page_as_it_is_typed_keys_included_without_saving()
    {
        using var dir = new TempDir();
        var (host, vm, _, _) = Make(dir, new FakeProvider(ProviderIds.Claude));
        vm.Translation.SelectedProvider = vm.Translation.ProviderChoices.Single(c => c.Value == ProviderIds.Claude);
        vm.Translation.Cards.Single().KeyText = "sk-ant-typed";

        await vm.Translation.TestCommand.ExecuteAsync(null);

        Assert.Equal("Gió đang rít.\n(answered by fake)", vm.Translation.TestResult);
        Assert.Equal((ProviderIds.Claude, "sk-ant-typed"), (host.Tested!.Translation.Provider, host.Tested.Translation.Claude.ApiKey));
        Assert.Equal("", vm.Working.Translation.Claude.ApiKeyProtected);
        Assert.Empty(host.Saves);
        Assert.False(vm.Translation.Testing);
    }
}
