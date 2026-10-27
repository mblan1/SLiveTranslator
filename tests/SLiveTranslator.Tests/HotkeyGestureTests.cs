using System.Windows.Input;
using SLiveTranslator.Hotkeys;
using Xunit;

namespace SLiveTranslator.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.F20, "Ctrl+Alt+Shift+F20")]
    [InlineData(ModifierKeys.Shift | ModifierKeys.Control, Key.T, "Ctrl+Shift+T")]
    [InlineData(ModifierKeys.Windows | ModifierKeys.Alt, Key.K, "Alt+Win+K")]
    [InlineData(ModifierKeys.Control, Key.D5, "Ctrl+5")]
    [InlineData(ModifierKeys.Control, Key.OemComma, "Ctrl+OemComma")]
    [InlineData(ModifierKeys.Control, Key.Oem1, "Ctrl+OemSemicolon")] // one key, two names in the enum
    [InlineData(ModifierKeys.Alt, Key.Return, "Alt+Enter")]
    [InlineData(ModifierKeys.None, Key.F9, "F9")]
    public void Formats_modifiers_in_a_fixed_order_and_reads_it_back(ModifierKeys modifiers, Key key, string text)
    {
        Assert.Equal(text, HotkeyGesture.Format(modifiers, key));
        Assert.True(HotkeyGesture.TryParse(text, out var parsed));
        Assert.Equal(new HotkeyGesture(modifiers, key), parsed);
        Assert.Equal(text, parsed.ToString());
    }

    [Theory]
    [InlineData("ctrl+shift+t", "Ctrl+Shift+T")]
    [InlineData("Control + Shift + T", "Ctrl+Shift+T")]
    [InlineData("SHIFT+CTRL+D7", "Ctrl+Shift+7")]
    [InlineData("Ctrl+oemplus", "Ctrl+OemPlus")]
    [InlineData("windows+alt+f24", "Alt+Win+F24")]
    public void Reads_older_and_differently_cased_gestures(string text, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var parsed));
        Assert.Equal(canonical, parsed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+65")]
    [InlineData("Ctrl+A,B")]
    [InlineData("Ctrl+LeftShift")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+System")]
    public void Refuses_what_is_not_one_key_with_modifiers(string text) =>
        Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void A_key_event_becomes_a_gesture_for_the_recorder()
    {
        // Alt combinations arrive as Key.System with the real key beside it.
        Assert.Equal("Alt+F4", HotkeyGesture.FromKeyEvent(Key.System, ModifierKeys.Alt, Key.F4).ToString());
        Assert.Equal("Ctrl+Alt+Shift+F20", HotkeyGesture.FromKeyEvent(Key.System, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.F20).ToString());
        Assert.Equal("Ctrl+Shift+7", HotkeyGesture.FromKeyEvent(Key.D7, ModifierKeys.Control | ModifierKeys.Shift, Key.None).ToString());
        Assert.Equal("Ctrl+OemQuestion", HotkeyGesture.FromKeyEvent(Key.Oem2, ModifierKeys.Control, Key.None).ToString());
        Assert.Equal("Win+K", HotkeyGesture.FromKeyEvent(Key.K, ModifierKeys.Windows, Key.None).ToString());
    }

    [Theory]
    [InlineData(Key.LeftCtrl, ModifierKeys.Control, Key.None)]
    [InlineData(Key.RightShift, ModifierKeys.Shift | ModifierKeys.Control, Key.None)]
    [InlineData(Key.System, ModifierKeys.Alt, Key.LeftAlt)]
    [InlineData(Key.LWin, ModifierKeys.Windows, Key.None)]
    [InlineData(Key.None, ModifierKeys.None, Key.None)]
    public void Pressing_only_modifiers_makes_no_gesture(Key key, ModifierKeys modifiers, Key systemKey) =>
        Assert.Null(HotkeyGesture.FromKeyEvent(key, modifiers, systemKey));

    [Theory]
    [InlineData("Ctrl+Shift+T", HotkeyResult.Ok, HotkeyRule.None)]
    [InlineData("F9", HotkeyResult.Ok, HotkeyRule.None)] // F1–F24 work alone
    [InlineData("Shift+F3", HotkeyResult.Ok, HotkeyRule.None)]
    [InlineData("Ctrl+Alt+Shift+F20", HotkeyResult.Ok, HotkeyRule.None)]
    [InlineData("T", HotkeyResult.Invalid, HotkeyRule.NeedsModifier)]
    [InlineData("Escape", HotkeyResult.Invalid, HotkeyRule.NeedsModifier)]
    [InlineData("Shift+A", HotkeyResult.Invalid, HotkeyRule.ShiftTyping)]
    [InlineData("Shift+5", HotkeyResult.Invalid, HotkeyRule.ShiftTyping)]
    [InlineData("Shift+OemComma", HotkeyResult.Invalid, HotkeyRule.ShiftTyping)]
    [InlineData("Shift+Space", HotkeyResult.Invalid, HotkeyRule.ShiftTyping)]
    [InlineData("F12", HotkeyResult.Reserved, HotkeyRule.Debugger)]
    [InlineData("Ctrl+Shift+F12", HotkeyResult.Reserved, HotkeyRule.Debugger)]
    [InlineData("Ctrl+Alt+Delete", HotkeyResult.Reserved, HotkeyRule.System)]
    [InlineData("Ctrl+Shift+Escape", HotkeyResult.Reserved, HotkeyRule.System)]
    [InlineData("Alt+Tab", HotkeyResult.Reserved, HotkeyRule.System)]
    [InlineData("Alt+F4", HotkeyResult.Reserved, HotkeyRule.System)]
    [InlineData("Win+L", HotkeyResult.Reserved, HotkeyRule.System)]
    [InlineData("Ctrl+", HotkeyResult.Invalid, HotkeyRule.Unreadable)]
    public void Validate_applies_the_rules(string gesture, HotkeyResult result, HotkeyRule rule)
    {
        var check = HotkeyManager.Validate(gesture);
        Assert.Equal(result, check.Result);
        Assert.Equal(rule, check.Rule);
        Assert.False(check.Warning);
    }

    [Fact]
    public void A_win_combination_is_allowed_with_a_warning() =>
        Assert.Equal(new HotkeyValidation(HotkeyResult.Ok, HotkeyRule.None, Warning: true), HotkeyManager.Validate("Ctrl+Win+F20"));
}
