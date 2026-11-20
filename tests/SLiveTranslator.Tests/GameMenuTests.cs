using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using SLiveTranslator.Capture;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Overlay;
using Xunit;
using DPoint = System.Drawing.Point;
using DRect = System.Drawing.Rectangle;
using DSize = System.Drawing.Size;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.Tests;

/// <summary>The in-game menu: its rows, what keys, hovers and clicks do, where it goes, when it closes by itself.</summary>
public class GameMenuTests
{
    static MenuState State(bool subtitles = false, bool history = false, bool shared = false, bool muted = false, int fontSize = 12,
        HotkeySettings? hotkeys = null, bool exitArmed = false) =>
        new(subtitles, history, shared, muted, fontSize, hotkeys ?? new HotkeySettings(), exitArmed);

    [Fact]
    public void Every_function_is_a_row_with_its_number_key_its_state_and_its_hotkey()
    {
        var items = GameMenu.Build(State(subtitles: true, shared: true, fontSize: 14));

        Assert.Equal(
        [
            MenuCommand.Subtitles, MenuCommand.SelectRegion, MenuCommand.Clear, MenuCommand.TranslateScreen, MenuCommand.History,
            MenuCommand.DisplayMode, MenuCommand.FontSize, MenuCommand.Mute, MenuCommand.Open, MenuCommand.Exit,
        ], items.Select(i => i.Command));
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"], items.Select(i => i.Key));
        Assert.Equal(("Subtitles", "On", "Ctrl+Shift+S", true), (items[0].Text, items[0].Value, items[0].Gesture, items[0].On));
        Assert.Equal(("Hidden", false), (items[4].Value, items[4].On));
        Assert.Equal(("Shared", "Ctrl+Shift+P"), (items[5].Value, items[5].Gesture));
        Assert.Equal("14", items[6].Value);
        Assert.True(items[6].Stepper);
        Assert.Equal("Off", items[7].Value);
        Assert.Equal(("", ""), (items[8].Value, items[8].Gesture));
        Assert.Equal(("Quit SLiveTranslator", false), (items[9].Text, items[9].Armed));
        Assert.Equal(("Press again to quit", true), (GameMenu.Build(State(exitArmed: true))[9].Text, GameMenu.Build(State(exitArmed: true))[9].Armed));
    }

    [Fact]
    public void A_switched_off_hotkey_shows_no_gesture()
    {
        var hotkeys = new HotkeySettings();
        hotkeys.Disabled.Add(HotkeyAction.Clear);
        hotkeys[HotkeyAction.TranslateScreen] = "F8";

        var items = GameMenu.Build(State(hotkeys: hotkeys));

        Assert.Equal("", items[2].Gesture);
        Assert.Equal("F8", items[3].Gesture);
    }

    [Theory]
    [InlineData(12, 1, 14)]
    [InlineData(12, -1, 10)]
    [InlineData(10, -1, 10)]
    [InlineData(80, 1, 80)]
    [InlineData(79, 1, 80)]
    [InlineData(11, -1, 10)]
    public void The_font_size_steps_by_two_within_10_and_80(int size, int direction, int expected) =>
        Assert.Equal(expected, GameMenu.StepFont(size, direction));

    [Fact]
    public void Arrows_move_the_selection_round_and_number_keys_pick_their_row()
    {
        var items = GameMenu.Build(State());
        var cursor = new MenuCursor(items.Count);

        Assert.Equal(new MenuStep(MenuMove.Redraw), cursor.Key(MenuKey.Up, 0, items));
        Assert.Equal(9, cursor.Selected); // round to the last
        cursor.Key(MenuKey.Down, 0, items);
        Assert.Equal(0, cursor.Selected);
        Assert.Equal(new MenuStep(MenuMove.Pick, MenuCommand.Subtitles), cursor.Key(MenuKey.Enter, 0, items));
        Assert.Equal(new MenuStep(MenuMove.Pick, MenuCommand.TranslateScreen), cursor.Key(MenuKey.Digit, 4, items));
        Assert.Equal(3, cursor.Selected);
        Assert.Equal(new MenuStep(MenuMove.Close), cursor.Key(MenuKey.Escape, 0, items));
    }

    [Fact]
    public void Left_and_right_change_the_font_size_from_any_row_and_its_own_number_only_selects_it()
    {
        var items = GameMenu.Build(State());
        var cursor = new MenuCursor(items.Count);

        Assert.Equal(new MenuStep(MenuMove.Step, MenuCommand.FontSize, 1), cursor.Key(MenuKey.Right, 0, items));
        Assert.Equal(6, cursor.Selected);
        Assert.Equal(new MenuStep(MenuMove.Step, MenuCommand.FontSize, -1), cursor.Key(MenuKey.Left, 0, items));
        Assert.Equal(new MenuStep(MenuMove.Redraw), cursor.Key(MenuKey.Enter, 0, items)); // Enter does not say up or down
        cursor.Key(MenuKey.Up, 0, items);
        Assert.Equal(new MenuStep(MenuMove.Redraw), cursor.Key(MenuKey.Digit, 7, items));
        Assert.Equal(6, cursor.Selected);
    }

    [Fact]
    public void Quit_needs_a_second_pick_and_anything_else_disarms_it()
    {
        var items = GameMenu.Build(State());
        var cursor = new MenuCursor(items.Count);

        Assert.Equal(new MenuStep(MenuMove.Redraw), cursor.Key(MenuKey.Digit, 0, items));
        Assert.True(cursor.ExitArmed);
        cursor.Key(MenuKey.Up, 0, items);
        Assert.False(cursor.ExitArmed);

        cursor.Key(MenuKey.Digit, 0, items);
        Assert.Equal(new MenuStep(MenuMove.Pick, MenuCommand.Exit), cursor.Key(MenuKey.Digit, 0, items));

        var clicks = new MenuCursor(items.Count);
        Assert.Equal(new MenuStep(MenuMove.Redraw), clicks.Click(9, 0, items));
        Assert.Equal(new MenuStep(MenuMove.Pick, MenuCommand.Exit), clicks.Click(9, 0, items));
    }

    [Fact]
    public void Hovering_selects_and_clicking_picks_or_steps()
    {
        var items = GameMenu.Build(State());
        var cursor = new MenuCursor(items.Count);

        Assert.Equal(new MenuStep(MenuMove.Redraw), cursor.Hover(4));
        Assert.Equal(new MenuStep(MenuMove.None), cursor.Hover(4));
        Assert.Equal(new MenuStep(MenuMove.Pick, MenuCommand.Clear), cursor.Click(2, 0, items));
        Assert.Equal(new MenuStep(MenuMove.Step, MenuCommand.FontSize, -1), cursor.Click(6, -1, items));
        Assert.Equal(new MenuStep(MenuMove.Redraw), cursor.Click(6, 0, items));
        Assert.Equal(new MenuStep(MenuMove.None), cursor.Click(42, 0, items));
    }

    [Fact]
    public void Number_keys_map_to_rows_and_zero_is_the_tenth()
    {
        Assert.Equal(0, GameMenu.IndexOfDigit(1, 10));
        Assert.Equal(8, GameMenu.IndexOfDigit(9, 10));
        Assert.Equal(9, GameMenu.IndexOfDigit(0, 10));
        Assert.Null(GameMenu.IndexOfDigit(0, 9));
        Assert.Null(GameMenu.IndexOfDigit(11, 10));
    }

    [Fact]
    public void The_menu_goes_in_the_middle_of_the_game_window_and_stays_on_its_monitor()
    {
        var screen = new DRect(0, 0, 1920, 1080);

        Assert.Equal(new DPoint(750, 300), GameMenu.Place(new DRect(0, 0, 1920, 1080), new DSize(420, 480), screen));
        Assert.Equal(new DPoint(1500, 0), GameMenu.Place(new DRect(1600, -200, 600, 300), new DSize(420, 480), screen)); // window partly off screen
    }

    [Fact]
    public void It_closes_by_itself_when_the_game_loses_the_front_on_a_click_outside_or_after_a_while()
    {
        var menu = new DRect(750, 300, 420, 480);
        var t0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var game = new IntPtr(100);
        MenuWatch At(bool down, int x, int y, double seconds = 1, IntPtr? front = null) => new(front ?? game, down, new DPoint(x, y), t0.AddSeconds(seconds));

        Assert.False(GameMenu.ShouldClose(game, At(false, 10, 10), At(false, 20, 20), menu, t0, GameMenu.IdleLimit));
        Assert.True(GameMenu.ShouldClose(game, At(false, 10, 10), At(false, 10, 10, front: new IntPtr(200)), menu, t0, GameMenu.IdleLimit));
        Assert.True(GameMenu.ShouldClose(game, At(false, 10, 10), At(true, 10, 10), menu, t0, GameMenu.IdleLimit)); // pressed outside
        Assert.False(GameMenu.ShouldClose(game, At(false, 800, 400), At(true, 800, 400), menu, t0, GameMenu.IdleLimit)); // pressed on it
        Assert.False(GameMenu.ShouldClose(game, At(true, 10, 10), At(true, 12, 10), menu, t0, GameMenu.IdleLimit)); // held since before
        Assert.True(GameMenu.ShouldClose(game, At(false, 10, 10), At(false, 10, 10, seconds: 31), menu, t0, GameMenu.IdleLimit));
    }

    [Fact]
    public void The_menu_has_its_own_hotkey_and_every_default_is_distinct_and_valid()
    {
        Assert.Equal("Ctrl+Shift+O", HotkeySettings.Defaults[HotkeyAction.Menu]);
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, HotkeySettings.Defaults.Values.Distinct().Count());
        foreach (var gesture in HotkeySettings.Defaults.Values) Assert.Equal(HotkeyResult.Ok, HotkeyManager.Validate(gesture).Result);
    }

    // ---- the window and the keys it holds (real windows and RegisterHotKey, on an STA thread) ----

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    const int WM_HOTKEY = 0x0312;
    const int FirstGrabId = 0xB000;

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

    [Fact]
    public void The_menu_window_takes_clicks_but_never_the_front_and_stays_out_of_capture_unless_shared() => OnStaThread(() =>
    {
        var menu = new MenuOverlay();
        try
        {
            var picks = new List<(int, int)>();
            menu.Picked += (row, step) => picks.Add((row, step));
            menu.ShowMenu("SLiveTranslator", GameMenu.Build(State()), 0, "keys", Forms.Screen.PrimaryScreen!.Bounds);

            long ex = Native.GetWindowLongPtr(menu.Handle, Native.GWL_EXSTYLE).ToInt64();
            Assert.NotEqual(0, ex & Native.WS_EX_NOACTIVATE);
            Assert.Equal(0, ex & Native.WS_EX_TRANSPARENT);
            Assert.Equal(new IntPtr(Native.MA_NOACTIVATE), SendMessage(menu.Handle, Native.WM_MOUSEACTIVATE, IntPtr.Zero, IntPtr.Zero));
            // Other tests may bring their windows to the front meanwhile; the menu itself never is.
            Assert.NotEqual(menu.Handle, Native.GetForegroundWindow());
            Assert.True(menu.ExcludedFromCapture);
            Assert.True(menu.IsVisible);
            Assert.False(menu.BoundsPx.IsEmpty);

            menu.ClickRow(2);
            menu.ClickRow(6, +1);
            Assert.Equal([(2, 0), (6, 1)], picks);

            menu.Shared = true;
            Assert.False(menu.ExcludedFromCapture);
            menu.Dismiss();
            Assert.False(menu.Showing);
        }
        finally
        {
            menu.Close();
        }
    });

    static HotkeyGesture Key(Key key) => new(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, key);

    [Fact]
    public void Grabbed_keys_reach_their_action_until_given_back_and_survive_the_hotkeys_being_set_again() => OnStaThread(() =>
    {
        using var manager = new HotkeyManager();
        int pressed = 0;
        var grab = manager.Grab([new GrabKey(Key(System.Windows.Input.Key.F17), () => pressed++)]);
        Assert.Empty(grab.Missed);

        SendMessage(manager.Handle, WM_HOTKEY, new IntPtr(FirstGrabId), IntPtr.Zero);
        Assert.Equal(1, pressed);

        manager.Suspend(); // a recorder: the actions go, the grab stays
        manager.RegisterAll(new HotkeySettings(), new Dictionary<HotkeyAction, Action>());
        SendMessage(manager.Handle, WM_HOTKEY, new IntPtr(FirstGrabId), IntPtr.Zero);
        Assert.Equal(2, pressed);

        grab.Dispose();
        SendMessage(manager.Handle, WM_HOTKEY, new IntPtr(FirstGrabId), IntPtr.Zero);
        Assert.Equal(2, pressed);
        using var again = manager.Grab([new GrabKey(Key(System.Windows.Input.Key.F17), () => { })]); // free again
        Assert.Empty(again.Missed);
    });

    [Fact]
    public void A_key_another_program_or_one_of_the_apps_own_hotkeys_holds_is_missed() => OnStaThread(() =>
    {
        using var other = new HotkeyManager();
        using var manager = new HotkeyManager();
        using var held = other.Grab([new GrabKey(Key(System.Windows.Input.Key.F18), () => { })]);
        Assert.Equal(HotkeyResult.Ok, manager.Register(HotkeyAction.Clear, Key(System.Windows.Input.Key.F19).ToString(), () => { }));

        using var grab = manager.Grab(
        [
            new GrabKey(Key(System.Windows.Input.Key.F18), () => { }),
            new GrabKey(Key(System.Windows.Input.Key.F19), () => { }),
        ]);

        Assert.Equal([Key(System.Windows.Input.Key.F18), Key(System.Windows.Input.Key.F19)], grab.Missed);
    });
}
