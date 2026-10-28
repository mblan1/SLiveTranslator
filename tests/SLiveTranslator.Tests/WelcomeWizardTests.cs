using System.Linq;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.UI;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The first-run wizard: its four steps, the language shown at once, the provider card's key check, the hotkey try-out, and Finish or Skip.</summary>
public class WelcomeWizardTests
{
    [Fact]
    public void The_wizard_walks_through_four_steps_and_finish_saves_the_first_run_done()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles"));
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };
        var wizard = vm.Welcome;
        Assert.False(wizard.IsOpen);

        wizard.Open();
        Assert.True(wizard.IsOpen);
        Assert.Equal(("Step 1 of 4", true), (wizard.StepText, wizard.OnLanguage));
        Assert.False(wizard.BackCommand.CanExecute(null));

        wizard.NextCommand.Execute(null);
        Assert.True(wizard.OnProvider);
        Assert.Same(vm.Translation, wizard.Translation);
        wizard.NextCommand.Execute(null);
        Assert.True(wizard.OnRegion);
        wizard.Subtitles.SelectRegionCommand.Execute(null);
        Assert.Equal(1, host.RegionSelections);
        wizard.NextCommand.Execute(null);
        Assert.True(wizard.OnHotkeys);
        Assert.True(wizard.IsLast);
        Assert.False(wizard.NextCommand.CanExecute(null));

        wizard.FinishCommand.Execute(null);

        Assert.False(wizard.IsOpen);
        Assert.True(host.Saves.Single().Settings.General.FirstRunDone);
        Assert.False(host.HotkeyTrial);
    }

    [Fact]
    public void The_language_step_shows_the_app_in_the_language_at_once_and_a_closed_window_gives_it_back_unsaved()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles"));
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };
        vm.Welcome.Open();

        vm.Welcome.SelectedLanguage = vm.Welcome.Languages.Single(l => l.Value == Loc.Vietnamese);

        Assert.Equal(["vi"], host.AppliedLanguages);
        Assert.Equal("vi", vm.Working.Ui.Language);
        Assert.Equal(SettingsChange.Language, vm.PendingChanges);

        vm.Dispose(); // closed without saving
        Assert.Equal(["vi", ""], host.AppliedLanguages);
    }

    [Fact]
    public void Skip_also_ends_the_first_run_and_keeps_what_was_set()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles"));
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };
        vm.Welcome.Open();
        vm.Welcome.SelectedLanguage = vm.Welcome.Languages.Single(l => l.Value == Loc.English);

        vm.Welcome.FinishCommand.Execute(null); // Skip, on the first step

        var saved = host.Saves.Single().Settings;
        Assert.Equal((true, "en"), (saved.General.FirstRunDone, saved.Ui.Language));
        vm.Dispose();
        Assert.Equal(["en"], host.AppliedLanguages); // saved: nothing to give back
    }

    [Fact]
    public void The_provider_step_uses_the_translation_card_and_checks_a_saved_key_when_its_provider_is_picked()
    {
        using var dir = new TempDir();
        var claude = new FakeProvider(ProviderIds.Claude);
        var host = new FakeHost(dir.File("Profiles"), claude);
        host.Current.Translation.Claude.ApiKey = "sk-ant-saved";
        SettingsUi.Run(() =>
        {
            var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };
            vm.Welcome.Open();
            vm.Welcome.Step = 1;
            Assert.True(vm.Translation.IsFree); // the free services are a valid choice
            Assert.Empty(claude.Checks);

            vm.Translation.SelectedProvider = vm.Translation.ProviderChoices.Single(c => c.Value == ProviderIds.Claude);

            Assert.Equal("sk-ant-saved", Assert.Single(claude.Checks).Credentials.ApiKey);
            claude.Checks[0].Reply(SettingsUi.Ok(new ModelOption("claude-opus-5-5", "Claude Opus 5.5", ModelTags.Recommended)));
            Assert.Equal("claude-opus-5-5", vm.Translation.SelectedCard!.SelectedModel!.Id);
        });
    }

    [Fact]
    public void The_hotkey_step_lights_up_each_hotkey_pressed_and_only_while_it_shows()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles"));
        host.Current.Hotkeys.Disabled.Add(HotkeyAction.Clear);
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };
        vm.Welcome.Open();
        Assert.False(host.HotkeyTrial);

        vm.Welcome.Step = 3;

        Assert.True(host.HotkeyTrial); // presses do nothing but light up
        Assert.DoesNotContain(vm.Welcome.Hotkeys, h => h.Action == HotkeyAction.Clear);
        Assert.Equal("Ctrl+Shift+S", vm.Welcome.Hotkeys.Single(h => h.Action == HotkeyAction.ToggleSubtitles).Gesture);
        host.Press(HotkeyAction.ToggleSubtitles);
        Assert.Equal([HotkeyAction.ToggleSubtitles], vm.Welcome.Hotkeys.Where(h => h.Pressed).Select(h => h.Action));

        vm.Welcome.BackCommand.Execute(null);
        Assert.False(host.HotkeyTrial);
        host.Press(HotkeyAction.History);
        Assert.False(vm.Welcome.Hotkeys.Single(h => h.Action == HotkeyAction.History).Pressed);

        vm.Welcome.Step = 3;
        vm.Dispose();
        Assert.False(host.HotkeyTrial); // a window closed on the step gives the hotkeys back
    }
}
