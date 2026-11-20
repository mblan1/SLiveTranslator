using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using SLiveTranslator.Capture;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Overlay;
using Forms = System.Windows.Forms;

namespace SLiveTranslator;

/// <summary>
/// The in-game menu (its hotkey; see <see cref="GameMenu"/>). It opens over the game without taking the focus, so the
/// subtitles keep running and translate-screen still reads the game; while it is open the app holds the arrows, Enter,
/// Esc and the number keys. It closes on Esc, its hotkey again, a pick, any other hotkey, a click outside it, the game
/// losing the front, or 30 s without use. It is seen by screen capture only in Shared mode with notices shared.
/// </summary>
public partial class App
{
    static readonly TimeSpan MenuWatchEvery = TimeSpan.FromMilliseconds(50);

    /// <summary>Holding → steps the size many times a second; it is saved once it rests.</summary>
    static readonly TimeSpan FontSaveDelay = TimeSpan.FromMilliseconds(600);

    MenuOverlay? _menu;
    MenuCursor? _menuCursor;
    IReadOnlyList<GameMenuItem> _menuItems = [];
    HotkeyManager.KeyGrab? _menuKeys;
    DispatcherTimer? _menuWatch;
    DispatcherTimer? _fontSave;
    IntPtr _menuOpenedOver;
    MenuWatch _menuLast;
    DateTime _menuActivity;

    /// <summary>The region picker is open (it runs a nested loop, so hotkeys still arrive): the menu does not open over it.</summary>
    bool _picking;

    bool MenuOpen => _menu?.Showing == true;

    void ToggleMenu()
    {
        if (MenuOpen) CloseMenu();
        else if (!_picking) OpenMenu();
    }

    void OpenMenu()
    {
        _menu ??= CreateMenu();
        _menu.Shared = SharedWindows.For(_settings.Overlay).Notices;
        _menuCursor = new MenuCursor(GameMenu.Count);
        var area = GameArea();
        _menuKeys = _hotkeys?.Grab(MenuKeys());
        RefreshMenuItems();
        _menu.ShowMenu(Loc.Instance["GameMenu.Title"], _menuItems, _menuCursor.Selected, MenuFooter(), area, _settings.Overlay.MenuPosition);
        _menuOpenedOver = Native.GetForegroundWindow();
        _menuLast = WatchNow();
        _menuActivity = DateTime.UtcNow;
        if (_menuWatch is null)
        {
            _menuWatch = new DispatcherTimer { Interval = MenuWatchEvery };
            _menuWatch.Tick += (_, _) => WatchMenu();
        }
        _menuWatch.Start();
    }

    MenuOverlay CreateMenu()
    {
        var menu = new MenuOverlay();
        menu.Hovered += index => OnMenuStep(_menuCursor?.Hover(index));
        menu.Picked += (index, step) => OnMenuStep(_menuCursor?.Click(index, step, _menuItems));
        return menu;
    }

    /// <summary>Hides the menu and gives its keys back (a pending font size is saved).</summary>
    /// <returns>Whether screen capture could see the menu just hidden: a capture right after waits a moment.</returns>
    bool CloseMenu()
    {
        if (_menu is not { Showing: true } menu) return false;
        _menuKeys?.Dispose();
        _menuKeys = null;
        _menuWatch?.Stop();
        bool seen = !menu.ExcludedFromCapture;
        menu.Dismiss();
        FlushFontSave();
        return seen;
    }

    /// <summary>The keys the open menu holds; each is handled after the hotkey message, outside it.</summary>
    IReadOnlyList<GrabKey> MenuKeys()
    {
        GrabKey Plain(Key key, MenuKey menuKey, int digit = 0, bool repeat = false) =>
            new(new HotkeyGesture(ModifierKeys.None, key), () => Dispatcher.BeginInvoke(() => OnMenuKey(menuKey, digit)), repeat);
        var keys = new List<GrabKey>
        {
            Plain(Key.Up, MenuKey.Up, repeat: true),
            Plain(Key.Down, MenuKey.Down, repeat: true),
            Plain(Key.Left, MenuKey.Left, repeat: true),
            Plain(Key.Right, MenuKey.Right, repeat: true),
            Plain(Key.Enter, MenuKey.Enter),
            Plain(Key.Escape, MenuKey.Escape),
        };
        for (int d = 0; d <= 9; d++)
        {
            keys.Add(Plain(Key.D0 + d, MenuKey.Digit, d));
            keys.Add(Plain(Key.NumPad0 + d, MenuKey.Digit, d));
        }
        return keys;
    }

    void OnMenuKey(MenuKey key, int digit)
    {
        if (MenuOpen && _menuCursor is { } cursor) OnMenuStep(cursor.Key(key, digit, _menuItems));
    }

    void OnMenuStep(MenuStep? step)
    {
        if (step is not { } s || !MenuOpen) return;
        _menuActivity = DateTime.UtcNow;
        switch (s.Move)
        {
            case MenuMove.Redraw:
                RedrawMenu();
                break;
            case MenuMove.Step:
                StepFontSize(s.Direction);
                break;
            case MenuMove.Close:
                CloseMenu();
                break;
            case MenuMove.Pick:
                RunMenuCommand(s.Command);
                break;
        }
    }

    void RedrawMenu()
    {
        if (_menu is null || _menuCursor is null) return;
        RefreshMenuItems();
        _menu.Update(_menuItems, _menuCursor.Selected, MenuFooter());
    }

    void RefreshMenuItems() =>
        _menuItems = GameMenu.Build(new MenuState(_subtitles?.IsRunning == true, _presenter?.HistoryVisible == true,
            _settings.Overlay.DisplayMode == DisplayMode.Shared, _notifier.Muted, _settings.Overlay.FontSize, _settings.Hotkeys,
            _menuCursor?.ExitArmed == true));

    string MenuFooter()
    {
        string footer = Loc.F("GameMenu.Keys", Hotkey(HotkeyAction.Menu));
        if (_menuKeys is { Missed.Count: > 0 } keys)
            footer += "\n" + Loc.F("GameMenu.KeysMissing", string.Join(", ", keys.Missed.Distinct().Select(g => g.ToString())));
        return footer;
    }

    /// <summary>A row picked: the menu closes first, so a window it opens (the picker, settings) or a capture finds it gone.</summary>
    async void RunMenuCommand(MenuCommand command)
    {
        if (command != MenuCommand.Exit && UpdateBlocks())
        {
            CloseMenu();
            return;
        }
        bool seen = CloseMenu();
        switch (command)
        {
            case MenuCommand.Subtitles:
                ToggleSubtitles();
                break;
            case MenuCommand.SelectRegion:
                SelectRegion();
                break;
            case MenuCommand.Clear:
                ClearOverlay();
                break;
            case MenuCommand.TranslateScreen:
                if (seen) await Task.Delay(80); // the menu leaves the shared screen before it is read
                TranslateScreen();
                break;
            case MenuCommand.History:
                ToggleHistory();
                break;
            case MenuCommand.DisplayMode:
                SwitchDisplayMode();
                break;
            case MenuCommand.Mute:
                // Muting hides the status notices: "muted" is shown before, "unmuted" after.
                var area = _subtitles?.Region ?? GameArea();
                if (_notifier.Muted)
                {
                    ToggleMute();
                    _notifier.Feedback(NoticeCategory.Status, Loc.Instance["Notice.Unmuted"], area);
                }
                else
                {
                    _notifier.Feedback(NoticeCategory.Status, Loc.Instance["Notice.Muted"], area);
                    ToggleMute();
                }
                break;
            case MenuCommand.Open:
                ShowMainWindow();
                break;
            case MenuCommand.Exit:
                ExitApp();
                break;
        }
    }

    /// <summary>The subtitle size one step up or down: shown at once, saved once the steps rest.</summary>
    void StepFontSize(int direction)
    {
        var look = _settings.Overlay;
        int next = GameMenu.StepFont(look.FontSize, direction);
        if (next != look.FontSize)
        {
            look.FontSize = next;
            _presenter?.Restyle();
            _notifier.Feedback(NoticeCategory.Status, Loc.F("Notice.FontSize", next), _subtitles?.Region ?? GameArea());
            if (_fontSave is null)
            {
                _fontSave = new DispatcherTimer { Interval = FontSaveDelay };
                _fontSave.Tick += (_, _) =>
                {
                    _fontSave.Stop();
                    SaveFontSize();
                };
            }
            _fontSave.Stop();
            _fontSave.Start();
        }
        RedrawMenu();
    }

    void FlushFontSave()
    {
        if (_fontSave?.IsEnabled != true) return;
        _fontSave.Stop();
        SaveFontSize();
    }

    void SaveFontSize()
    {
        TrySaveSettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    void WatchMenu()
    {
        if (!MenuOpen || _menu is null)
        {
            _menuWatch?.Stop();
            return;
        }
        var now = WatchNow();
        bool close = GameMenu.ShouldClose(_menuOpenedOver, _menuLast, now, _menu.BoundsPx, _menuActivity, GameMenu.IdleLimit);
        _menuLast = now;
        if (close) CloseMenu();
    }

    static MenuWatch WatchNow() =>
        new(Native.GetForegroundWindow(), Held(Native.VK_LBUTTON) || Held(Native.VK_RBUTTON) || Held(Native.VK_MBUTTON), Forms.Cursor.Position, DateTime.UtcNow);

    static bool Held(int virtualKey) => (Native.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
