using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>
/// Real RegisterHotKey calls, on an STA thread of their own (the message-only window needs a dispatcher), with
/// Ctrl+Alt+Shift+F20…F24, which no other program uses. Every manager is disposed, so nothing stays registered.
/// </summary>
public class HotkeyManagerTests
{
    const string F20 = "Ctrl+Alt+Shift+F20", F21 = "Ctrl+Alt+Shift+F21", F22 = "Ctrl+Alt+Shift+F22", F23 = "Ctrl+Alt+Shift+F23", F24 = "Ctrl+Alt+Shift+F24";
    const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    static void OnStaThread(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
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

    static readonly Action Nothing = () => { };

    static Dictionary<HotkeyAction, Action> Callbacks() => new()
    {
        [HotkeyAction.TranslateScreen] = Nothing,
        [HotkeyAction.ToggleSubtitles] = Nothing,
        [HotkeyAction.SelectRegion] = Nothing,
        [HotkeyAction.Clear] = Nothing,
        [HotkeyAction.History] = Nothing,
    };

    [Fact]
    public void A_free_gesture_registers_and_its_state_is_kept() => OnStaThread(() =>
    {
        using var manager = new HotkeyManager();

        Assert.Null(manager.State(HotkeyAction.Clear));
        Assert.Equal(HotkeyResult.Ok, manager.Register(HotkeyAction.Clear, F20, Nothing));
        Assert.Equal(HotkeyResult.Ok, manager.State(HotkeyAction.Clear));
    });

    [Fact]
    public void A_gesture_another_program_holds_is_in_use_until_it_lets_go() => OnStaThread(() =>
    {
        using var other = new HotkeyManager();
        using var manager = new HotkeyManager();
        Assert.Equal(HotkeyResult.Ok, other.Register(HotkeyAction.History, F21, Nothing));

        Assert.Equal(HotkeyResult.InUse, manager.Register(HotkeyAction.Clear, F21, Nothing));
        Assert.Equal(HotkeyResult.InUse, manager.State(HotkeyAction.Clear));
        Assert.Equal(HotkeyResult.InUse, manager.Probe(F21, HotkeyAction.Clear));

        other.UnregisterAll();
        Assert.Equal(HotkeyResult.Ok, manager.Register(HotkeyAction.Clear, F21, Nothing));
    });

    [Fact]
    public void An_id_is_used_up_only_by_a_registration_that_worked_and_the_hotkey_calls_its_action() => OnStaThread(() =>
    {
        using var other = new HotkeyManager();
        using var manager = new HotkeyManager();
        other.Register(HotkeyAction.History, F22, Nothing);
        int fired = 0;

        Assert.Equal(HotkeyResult.Reserved, manager.Register(HotkeyAction.Clear, "F12", Nothing));
        Assert.Equal(HotkeyResult.Invalid, manager.Register(HotkeyAction.Clear, "Shift+A", Nothing));
        Assert.Equal(HotkeyResult.InUse, manager.Register(HotkeyAction.Clear, F22, Nothing));
        Assert.Equal(HotkeyResult.Ok, manager.Register(HotkeyAction.TranslateScreen, F23, () => fired++));

        SendMessage(manager.Handle, WM_HOTKEY, new IntPtr(1), IntPtr.Zero); // the first id
        Assert.Equal(1, fired);
    });

    [Fact]
    public void Two_actions_with_one_gesture_are_a_duplicate_and_probing_ones_own_gesture_is_fine() => OnStaThread(() =>
    {
        using var manager = new HotkeyManager();

        Assert.Equal(HotkeyResult.Ok, manager.Register(HotkeyAction.TranslateScreen, F20, Nothing));
        Assert.Equal(HotkeyResult.Duplicate, manager.Register(HotkeyAction.Clear, "ctrl+alt+shift+f20", Nothing));

        Assert.Equal(HotkeyResult.Ok, manager.Probe(F20, HotkeyAction.TranslateScreen));
        Assert.Equal(HotkeyResult.Duplicate, manager.Probe(F20, HotkeyAction.Clear));
        Assert.Equal(HotkeyResult.Duplicate, manager.Probe(F20));
    });

    [Fact]
    public void Register_all_reports_each_action_and_a_switched_off_one_as_disabled() => OnStaThread(() =>
    {
        using var manager = new HotkeyManager();
        var settings = new HotkeySettings();
        settings[HotkeyAction.TranslateScreen] = F20;
        settings[HotkeyAction.ToggleSubtitles] = F20;
        settings[HotkeyAction.SelectRegion] = "R";
        settings[HotkeyAction.Clear] = "F12";
        settings[HotkeyAction.History] = F21;
        settings.Disabled.Add(HotkeyAction.History);

        var results = manager.RegisterAll(settings, Callbacks());

        Assert.Equal(new Dictionary<HotkeyAction, HotkeyResult>
        {
            [HotkeyAction.TranslateScreen] = HotkeyResult.Ok,
            [HotkeyAction.ToggleSubtitles] = HotkeyResult.Duplicate,
            [HotkeyAction.SelectRegion] = HotkeyResult.Invalid,
            [HotkeyAction.Clear] = HotkeyResult.Reserved,
            [HotkeyAction.History] = HotkeyResult.Disabled,
        }, results);
        Assert.Equal(HotkeyResult.Disabled, manager.State(HotkeyAction.History));
        Assert.Equal(HotkeyResult.Ok, manager.Probe(F21)); // a disabled gesture is not held

        // A second call replaces the first: the old gesture is free again.
        settings[HotkeyAction.TranslateScreen] = F22;
        settings[HotkeyAction.ToggleSubtitles] = F23;
        manager.RegisterAll(settings, Callbacks());
        using var other = new HotkeyManager();
        Assert.Equal(HotkeyResult.Ok, other.Register(HotkeyAction.Clear, F20, Nothing));
    });

    [Fact]
    public void A_probe_leaves_nothing_registered_and_checks_the_rules_first() => OnStaThread(() =>
    {
        using var manager = new HotkeyManager();
        using var other = new HotkeyManager();

        Assert.Equal(HotkeyResult.Ok, manager.Probe(F24));
        Assert.Equal(HotkeyResult.Ok, other.Register(HotkeyAction.Clear, F24, Nothing));
        Assert.Equal(HotkeyResult.InUse, manager.Probe(F24));
        Assert.Equal(HotkeyResult.Reserved, manager.Probe("Ctrl+Alt+Delete"));
        Assert.Equal(HotkeyResult.Invalid, manager.Probe("Q"));
        Assert.Null(manager.State(HotkeyAction.Clear));
    });

    [Fact]
    public void Suspend_lets_go_of_every_hotkey_and_resume_takes_them_back() => OnStaThread(() =>
    {
        using var manager = new HotkeyManager();
        using var other = new HotkeyManager();
        manager.Register(HotkeyAction.TranslateScreen, F20, Nothing);
        manager.Register(HotkeyAction.History, F21, Nothing);

        manager.Suspend();
        Assert.True(manager.Suspended);
        Assert.Equal(HotkeyResult.Ok, other.Register(HotkeyAction.Clear, F20, Nothing)); // free while a recorder listens
        other.UnregisterAll();
        Assert.Equal(HotkeyResult.Ok, manager.Probe(F20, HotkeyAction.TranslateScreen));

        var results = manager.Resume();
        Assert.False(manager.Suspended);
        Assert.Equal(HotkeyResult.Ok, results[HotkeyAction.TranslateScreen]);
        Assert.Equal(HotkeyResult.Ok, results[HotkeyAction.History]);
        Assert.Equal(HotkeyResult.InUse, other.Register(HotkeyAction.Clear, F20, Nothing));

        // Taken by another program meanwhile: resume says so.
        other.UnregisterAll();
        manager.Suspend();
        other.Register(HotkeyAction.Clear, F21, Nothing);
        results = manager.Resume();
        Assert.Equal(HotkeyResult.InUse, results[HotkeyAction.History]);
        Assert.Equal(HotkeyResult.InUse, manager.State(HotkeyAction.History));
    });
}
