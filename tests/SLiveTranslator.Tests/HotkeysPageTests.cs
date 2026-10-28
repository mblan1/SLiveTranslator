using System;
using System.Linq;
using System.Windows.Input;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Controls;
using SLiveTranslator.UI.Pages;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The hotkey recorder's rules (no window) and the Hotkeys page: states, switches, Reset, and taking the hotkeys off while recording.</summary>
public class HotkeysPageTests
{
    [Fact]
    public void Modifiers_alone_show_what_is_held_and_a_key_completes_the_gesture()
    {
        var recorder = new HotkeyRecorder();
        recorder.Begin("Ctrl+Shift+T");
        Assert.Equal("Ctrl+Shift+T", recorder.Text);

        Assert.Equal(RecordStep.Partial, recorder.KeyDown(Key.LeftCtrl, ModifierKeys.Control));
        Assert.Equal("Ctrl+…", recorder.Text);
        Assert.Equal(RecordStep.Partial, recorder.KeyDown(Key.LeftShift, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal("Ctrl+Shift+…", recorder.Text);
        Assert.Equal("Ctrl+Shift+T", recorder.Gesture); // nothing recorded yet

        Assert.Equal(RecordStep.Completed, recorder.KeyDown(Key.K, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal(("Ctrl+Shift+K", "Ctrl+Shift+K"), (recorder.Gesture, recorder.Text));
    }

    [Fact]
    public void Letting_go_of_the_modifiers_without_a_key_shows_the_gesture_again()
    {
        var recorder = new HotkeyRecorder();
        recorder.Begin("Ctrl+Shift+T");
        recorder.KeyDown(Key.LeftCtrl, ModifierKeys.Control);
        recorder.KeyDown(Key.LeftAlt, ModifierKeys.Control | ModifierKeys.Alt);

        recorder.KeyUp(ModifierKeys.Control);
        Assert.Equal("Ctrl+…", recorder.Text);
        recorder.KeyUp(ModifierKeys.None);
        Assert.Equal("Ctrl+Shift+T", recorder.Text);
        Assert.Equal("Ctrl+Shift+T", recorder.Gesture);
    }

    [Fact]
    public void Alt_combinations_digits_and_function_keys_are_recorded_in_the_canonical_form()
    {
        var recorder = new HotkeyRecorder();
        recorder.Begin("");

        recorder.KeyDown(Key.System, ModifierKeys.Alt, Key.LeftAlt); // WPF reports Alt as a system key
        Assert.Equal("Alt+…", recorder.Text);
        Assert.Equal(RecordStep.Completed, recorder.KeyDown(Key.System, ModifierKeys.Alt | ModifierKeys.Control, Key.F));
        Assert.Equal("Ctrl+Alt+F", recorder.Gesture);

        recorder.KeyDown(Key.D5, ModifierKeys.Control);
        Assert.Equal("Ctrl+5", recorder.Gesture);
        recorder.KeyDown(Key.F20, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
        Assert.Equal("Ctrl+Alt+Shift+F20", recorder.Gesture);
        recorder.KeyDown(Key.System, ModifierKeys.None, Key.F10); // F10 alone also arrives as a system key
        Assert.Equal("F10", recorder.Gesture);
    }

    [Fact]
    public void Escape_gives_back_the_gesture_the_recording_started_with_and_backspace_clears_it()
    {
        var recorder = new HotkeyRecorder();
        recorder.Begin("Ctrl+Shift+H");
        recorder.KeyDown(Key.J, ModifierKeys.Control | ModifierKeys.Alt);
        Assert.Equal("Ctrl+Alt+J", recorder.Gesture);

        Assert.Equal(RecordStep.Cancelled, recorder.KeyDown(Key.Escape, ModifierKeys.None));
        Assert.Equal(("Ctrl+Shift+H", "Ctrl+Shift+H"), (recorder.Gesture, recorder.Text));

        Assert.Equal(RecordStep.Cleared, recorder.KeyDown(Key.Back, ModifierKeys.None));
        Assert.Equal(("", ""), (recorder.Gesture, recorder.Text));

        // With a modifier they are keys like any other.
        Assert.Equal(RecordStep.Completed, recorder.KeyDown(Key.Escape, ModifierKeys.Shift));
        Assert.Equal("Shift+Escape", recorder.Gesture);
    }

    [Fact]
    public void Tab_alone_is_not_recorded_so_the_focus_can_move_on()
    {
        var recorder = new HotkeyRecorder();
        recorder.Begin("Ctrl+Shift+X");

        Assert.Equal(RecordStep.PassThrough, recorder.KeyDown(Key.Tab, ModifierKeys.None));
        Assert.Equal("Ctrl+Shift+X", recorder.Gesture);
        Assert.Equal(RecordStep.Completed, recorder.KeyDown(Key.Tab, ModifierKeys.Control | ModifierKeys.Alt));
    }

    static (FakeHost Host, SettingsViewModel Vm) Make(TempDir dir)
    {
        var host = new FakeHost(dir.File("Profiles"));
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };
        return (host, vm);
    }

    static HotkeyRowViewModel Row(SettingsViewModel vm, HotkeyAction action) => vm.Hotkeys.Rows.Single(r => r.Action == action);

    [Fact]
    public void Each_action_has_a_row_with_the_state_of_its_registered_hotkey()
    {
        using var dir = new TempDir();
        var (host, _) = Make(dir);
        host.States[HotkeyAction.Clear] = HotkeyResult.InUse;
        host.Current.Hotkeys.Disabled.Add(HotkeyAction.History);
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay);

        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, vm.Hotkeys.Rows.Count);
        Assert.Equal(("Translate screen", "Ctrl+Shift+T", "OK"), (Row(vm, HotkeyAction.TranslateScreen).Name, Row(vm, HotkeyAction.TranslateScreen).Gesture,
            Row(vm, HotkeyAction.TranslateScreen).StateText));
        var clear = Row(vm, HotkeyAction.Clear);
        Assert.Equal(HotkeyResult.InUse, clear.State);
        Assert.True(clear.StateProblem);
        Assert.Equal("used by another program", clear.Detail);
        var history = Row(vm, HotkeyAction.History);
        Assert.False(history.IsEnabled);
        Assert.True(history.StateOff);
        Assert.Equal("Disabled", history.StateText);
        Assert.Empty(host.Probes); // every gesture is the one registered: nothing to probe
    }

    [Fact]
    public void A_recorded_gesture_is_checked_against_the_rules_the_other_actions_and_other_programs()
    {
        using var dir = new TempDir();
        var (host, vm) = Make(dir);
        var region = Row(vm, HotkeyAction.SelectRegion);

        region.Gesture = "F12";
        Assert.Equal((HotkeyResult.Reserved, "F12 is reserved for debuggers"), (region.State, region.Detail));
        region.Gesture = "Shift+K";
        Assert.Equal(HotkeyResult.Invalid, region.State);

        region.Gesture = "Ctrl+Shift+T"; // the gesture of Translate screen, which comes first
        Assert.Equal(HotkeyResult.Duplicate, region.State);
        Assert.Equal(HotkeyResult.Ok, Row(vm, HotkeyAction.TranslateScreen).State);

        host.ProbeResult = _ => HotkeyResult.InUse;
        region.Gesture = "Ctrl+Alt+R";
        Assert.Equal((HotkeyResult.InUse, "used by another program"), (region.State, region.Detail));
        Assert.Contains(("Ctrl+Alt+R", HotkeyAction.SelectRegion), host.Probes);

        host.ProbeResult = _ => HotkeyResult.Ok;
        region.Gesture = "Win+Alt+R";
        Assert.Equal(HotkeyResult.Ok, region.State);
        Assert.Equal("Windows reserves many Win shortcuts; this one may not work.", region.Detail);
        Assert.Equal("Alt+Win+R", vm.Working.Hotkeys[HotkeyAction.SelectRegion]); // stored in the canonical order
        Assert.Equal(SettingsChange.Hotkeys, vm.PendingChanges);
        Assert.Equal("Ctrl+Shift+R", host.Current.Hotkeys[HotkeyAction.SelectRegion]); // applied on Save only
    }

    [Fact]
    public void Swapping_two_gestures_is_no_conflict_although_the_other_action_still_holds_one()
    {
        using var dir = new TempDir();
        var (host, vm) = Make(dir);
        host.ProbeResult = _ => HotkeyResult.Duplicate; // the manager: another action of the app holds it right now

        Row(vm, HotkeyAction.Clear).Gesture = "Ctrl+Shift+H";
        Row(vm, HotkeyAction.History).Gesture = "Ctrl+Shift+X";

        Assert.Equal(HotkeyResult.Ok, Row(vm, HotkeyAction.Clear).State);
        Assert.Equal(HotkeyResult.Ok, Row(vm, HotkeyAction.History).State);
    }

    [Fact]
    public void Clearing_turns_the_action_off_and_switching_it_on_again_gives_it_its_default()
    {
        using var dir = new TempDir();
        var (_, vm) = Make(dir);
        var clear = Row(vm, HotkeyAction.Clear);

        clear.Gesture = "";
        Assert.False(clear.IsEnabled);
        Assert.Contains(HotkeyAction.Clear, vm.Working.Hotkeys.Disabled);
        Assert.Equal(HotkeyResult.Disabled, clear.State);

        clear.IsEnabled = true;
        Assert.Equal("Ctrl+Shift+X", clear.Gesture);
        Assert.Equal(HotkeyResult.Ok, clear.State);

        clear.IsEnabled = false;
        clear.Gesture = "Ctrl+Alt+X"; // recording a gesture turns it on
        Assert.True(clear.IsEnabled);
        Assert.DoesNotContain(HotkeyAction.Clear, vm.Working.Hotkeys.Disabled);
    }

    [Fact]
    public void Reset_and_reset_all_bring_back_the_defaults()
    {
        using var dir = new TempDir();
        var (_, vm) = Make(dir);
        Row(vm, HotkeyAction.History).Gesture = "Ctrl+Alt+H";
        Row(vm, HotkeyAction.Clear).IsEnabled = false;
        Row(vm, HotkeyAction.TranslateScreen).Gesture = "Ctrl+Alt+T";
        Assert.True(vm.HasChanges);

        Row(vm, HotkeyAction.History).ResetCommand.Execute(null);
        Assert.Equal("Ctrl+Shift+H", vm.Working.Hotkeys[HotkeyAction.History]);

        vm.Hotkeys.ResetAllCommand.Execute(null);
        Assert.Empty(vm.Working.Hotkeys.Disabled);
        Assert.All(Enum.GetValues<HotkeyAction>(), a => Assert.Equal(HotkeySettings.Defaults[a], vm.Working.Hotkeys[a]));
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void The_hotkeys_are_off_only_while_a_recorder_has_the_focus_and_come_back_when_the_window_closes()
    {
        using var dir = new TempDir();
        var (host, vm) = Make(dir);
        var row = Row(vm, HotkeyAction.ToggleSubtitles);

        row.IsRecording = true;
        Assert.True(host.Suspended);
        Assert.Equal("Press the new combination. Esc cancels, Backspace clears.", row.Detail);
        row.IsRecording = false; // the box lost the focus
        Assert.False(host.Suspended);

        Row(vm, HotkeyAction.Clear).IsRecording = true;
        vm.Dispose(); // closed while recording
        Assert.False(host.Suspended);
        Assert.Equal(2, host.Suspends);
    }

    [Fact]
    public void Showing_the_page_probes_again_what_another_program_may_have_taken_meanwhile()
    {
        using var dir = new TempDir();
        var (host, vm) = Make(dir);
        var history = Row(vm, HotkeyAction.History);
        history.Gesture = "Ctrl+Alt+H";
        Assert.Equal(HotkeyResult.Ok, history.State);

        host.ProbeResult = _ => HotkeyResult.InUse;
        vm.Hotkeys.Relocalize();
        Assert.Equal(HotkeyResult.Ok, history.State); // a probe is kept while the page shows

        vm.Hotkeys.PageShown();
        Assert.Equal(HotkeyResult.InUse, history.State);
    }
}
