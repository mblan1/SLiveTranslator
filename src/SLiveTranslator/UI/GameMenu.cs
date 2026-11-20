using System;
using System.Collections.Generic;
using System.Globalization;
using SLiveTranslator.Models;
using DPoint = System.Drawing.Point;
using DRect = System.Drawing.Rectangle;
using DSize = System.Drawing.Size;

namespace SLiveTranslator.UI;

/// <summary>What a row of the in-game menu does.</summary>
public enum MenuCommand { Subtitles, SelectRegion, Clear, TranslateScreen, History, DisplayMode, FontSize, Mute, Open, Exit }

/// <summary>A row of the in-game menu.</summary>
/// <param name="Key">The number key that picks it: "1"–"9", then "0".</param>
/// <param name="Value">Its state on the right ("On", "Shared", the font size); "" for an action.</param>
/// <param name="Gesture">Its own hotkey, shown dim; "" when it has none or it is switched off.</param>
/// <param name="On">The value is an "on" state (drawn in the accent colour).</param>
/// <param name="Armed">Quit asks to be picked again (drawn as a warning).</param>
public sealed record GameMenuItem(MenuCommand Command, string Key, string Text, string Value = "", string Gesture = "", bool On = false, bool Armed = false)
{
    /// <summary>The font size row: − and + change it, the menu stays open.</summary>
    public bool Stepper => Command == MenuCommand.FontSize;
}

/// <summary>The app's state the menu shows.</summary>
/// <param name="ExitArmed">Quit was picked once: the row asks to be picked again.</param>
public sealed record MenuState(bool SubtitlesOn, bool HistoryVisible, bool Shared, bool Muted, int FontSize, HotkeySettings Hotkeys, bool ExitArmed = false);

public enum MenuKey { Up, Down, Left, Right, Enter, Escape, Digit }

public enum MenuMove { None, Redraw, Pick, Step, Close }

/// <summary>What a key, a hover or a click asks of the menu.</summary>
/// <param name="Direction">For <see cref="MenuMove.Step"/>: −1 or +1.</param>
public readonly record struct MenuStep(MenuMove Move, MenuCommand Command = default, int Direction = 0);

/// <summary>Where the pointer and the foreground were, for the menu's check whether to close by itself.</summary>
public readonly record struct MenuWatch(IntPtr Foreground, bool ButtonDown, DPoint Cursor, DateTime Time);

/// <summary>
/// The in-game menu (its hotkey, Ctrl+Shift+O by default): every function of the app in one list over the game, picked
/// by a click or by the keys it holds while open (arrows, Enter, the number keys, Esc). The game keeps the focus.
/// </summary>
public static class GameMenu
{
    public const int MinFontSize = 10;
    public const int MaxFontSize = 80;
    public const int FontStep = 2;

    /// <summary>The menu closes by itself after this long without a key, a click or a hover.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(30);

    static readonly MenuCommand[] Order =
    [
        MenuCommand.Subtitles, MenuCommand.SelectRegion, MenuCommand.Clear, MenuCommand.TranslateScreen, MenuCommand.History,
        MenuCommand.DisplayMode, MenuCommand.FontSize, MenuCommand.Mute, MenuCommand.Open, MenuCommand.Exit,
    ];

    public static int Count => Order.Length;

    public static IReadOnlyList<GameMenuItem> Build(MenuState s)
    {
        string Gesture(HotkeyAction action) => s.Hotkeys.Disabled.Contains(action) ? "" : s.Hotkeys[action];
        string L(string key) => Loc.Instance[key];
        var items = new List<GameMenuItem>(Order.Length);
        for (int i = 0; i < Order.Length; i++)
        {
            var command = Order[i];
            string key = i < 9 ? (i + 1).ToString(CultureInfo.InvariantCulture) : "0";
            items.Add(command switch
            {
                MenuCommand.Subtitles => new(command, key, L("GameMenu.Subtitles"), L(s.SubtitlesOn ? "GameMenu.On" : "GameMenu.Off"),
                    Gesture(HotkeyAction.ToggleSubtitles), s.SubtitlesOn),
                MenuCommand.SelectRegion => new(command, key, L("GameMenu.SelectRegion"), Gesture: Gesture(HotkeyAction.SelectRegion)),
                MenuCommand.Clear => new(command, key, L("GameMenu.Clear"), Gesture: Gesture(HotkeyAction.Clear)),
                MenuCommand.TranslateScreen => new(command, key, L("GameMenu.TranslateScreen"), Gesture: Gesture(HotkeyAction.TranslateScreen)),
                MenuCommand.History => new(command, key, L("GameMenu.History"), L(s.HistoryVisible ? "GameMenu.Shown" : "GameMenu.Hidden"),
                    Gesture(HotkeyAction.History), s.HistoryVisible),
                MenuCommand.DisplayMode => new(command, key, L("GameMenu.DisplayMode"), L(s.Shared ? "GameMenu.Shared" : "GameMenu.Private"),
                    Gesture(HotkeyAction.SwitchDisplayMode), s.Shared),
                MenuCommand.FontSize => new(command, key, L("GameMenu.FontSize"), s.FontSize.ToString(CultureInfo.InvariantCulture)),
                MenuCommand.Mute => new(command, key, L("GameMenu.Mute"), L(s.Muted ? "GameMenu.On" : "GameMenu.Off"), On: s.Muted),
                MenuCommand.Open => new(command, key, L("GameMenu.Open")),
                _ => new(command, key, L(s.ExitArmed ? "GameMenu.ExitConfirm" : "GameMenu.Exit"), Armed: s.ExitArmed),
            });
        }
        return items;
    }

    public static int IndexOf(MenuCommand command) => Array.IndexOf(Order, command);

    /// <summary>The next subtitle size up or down, within 10–80.</summary>
    public static int StepFont(int size, int direction) => Math.Clamp(size + Math.Sign(direction) * FontStep, MinFontSize, MaxFontSize);

    /// <summary>The row a number key picks: 1–9, then 0 for the tenth; null past the last row.</summary>
    public static int? IndexOfDigit(int digit, int count)
    {
        int index = digit == 0 ? 9 : digit - 1;
        return digit is >= 0 and <= 9 && index < count ? index : null;
    }

    /// <summary>The menu's top-left corner: in the middle of the game window, kept on its monitor.</summary>
    public static DPoint Place(DRect area, DSize size, DRect screen)
    {
        int x = area.Left + (area.Width - size.Width) / 2;
        int y = area.Top + (area.Height - size.Height) / 2;
        return new DPoint(Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - size.Width)),
            Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - size.Height)));
    }

    /// <summary>
    /// Whether the open menu should go by itself: another window came to the front (the user switched away), a mouse
    /// button went down outside the menu (a click on the game), or nothing happened for <paramref name="idleLimit"/>.
    /// </summary>
    public static bool ShouldClose(IntPtr openedOver, MenuWatch before, MenuWatch now, DRect menuPx, DateTime lastActivity, TimeSpan idleLimit) =>
        now.Foreground != openedOver
        || (now.ButtonDown && !before.ButtonDown && !menuPx.Contains(now.Cursor))
        || now.Time - lastActivity >= idleLimit;
}

/// <summary>The selected row of the open menu and what keys, hovers and clicks do with it.</summary>
public sealed class MenuCursor(int count)
{
    readonly int _count = Math.Max(1, count);

    public int Selected { get; private set; }

    /// <summary>Quit was picked once; any other move disarms it.</summary>
    public bool ExitArmed { get; private set; }

    public MenuStep Key(MenuKey key, int digit, IReadOnlyList<GameMenuItem> items)
    {
        switch (key)
        {
            case MenuKey.Up:
                return Select((Selected - 1 + _count) % _count);
            case MenuKey.Down:
                return Select((Selected + 1) % _count);
            case MenuKey.Left or MenuKey.Right:
                // From any row: the size is what the arrows change sideways, so the font row is selected to show it.
                Selected = Math.Max(0, GameMenu.IndexOf(MenuCommand.FontSize));
                ExitArmed = false;
                return new MenuStep(MenuMove.Step, MenuCommand.FontSize, key == MenuKey.Right ? 1 : -1);
            case MenuKey.Enter:
                return Activate(Selected, items);
            case MenuKey.Escape:
                return new MenuStep(MenuMove.Close);
            default:
                if (GameMenu.IndexOfDigit(digit, Math.Min(_count, items.Count)) is not { } index) return new MenuStep(MenuMove.None);
                if (items[index].Stepper) return Select(index); // a number alone does not say up or down
                if (items[index].Command != MenuCommand.Exit) ExitArmed = false;
                Selected = index;
                return Activate(index, items);
        }
    }

    /// <summary>The pointer moved onto a row.</summary>
    public MenuStep Hover(int index) => index == Selected ? new MenuStep(MenuMove.None) : Select(index);

    /// <summary>A click on a row, or on the font row's − (−1) or + (+1).</summary>
    public MenuStep Click(int index, int step, IReadOnlyList<GameMenuItem> items)
    {
        if (index < 0 || index >= items.Count) return new MenuStep(MenuMove.None);
        if (items[index].Command != MenuCommand.Exit) ExitArmed = false;
        Selected = index;
        if (items[index].Stepper) return step != 0 ? new MenuStep(MenuMove.Step, MenuCommand.FontSize, step) : new MenuStep(MenuMove.Redraw);
        return Activate(index, items);
    }

    MenuStep Select(int index)
    {
        Selected = index;
        ExitArmed = false;
        return new MenuStep(MenuMove.Redraw);
    }

    MenuStep Activate(int index, IReadOnlyList<GameMenuItem> items)
    {
        var item = items[index];
        if (item.Stepper) return new MenuStep(MenuMove.Redraw); // − and + (or ← →) change it
        if (item.Command == MenuCommand.Exit && !ExitArmed)
        {
            ExitArmed = true; // a stray 0 does not quit
            return new MenuStep(MenuMove.Redraw);
        }
        return new MenuStep(MenuMove.Pick, item.Command);
    }
}
