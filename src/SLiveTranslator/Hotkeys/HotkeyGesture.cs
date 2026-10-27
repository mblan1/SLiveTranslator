using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Input;
using SLiveTranslator.Capture;

namespace SLiveTranslator.Hotkeys;

/// <summary>What became of an action's hotkey.</summary>
public enum HotkeyResult
{
    Ok,

    /// <summary>Not a usable gesture (see <see cref="HotkeyRule"/>), or Windows refused it for another reason.</summary>
    Invalid,

    /// <summary>Another program holds it (ERROR_HOTKEY_ALREADY_REGISTERED).</summary>
    InUse,

    /// <summary>Windows or the debugger owns it (F12, Ctrl+Alt+Delete, Alt+Tab …).</summary>
    Reserved,

    /// <summary>Another action of this app has the same gesture.</summary>
    Duplicate,

    /// <summary>The action is switched off (<see cref="Models.HotkeySettings.Disabled"/>).</summary>
    Disabled,
}

/// <summary>Why <see cref="HotkeyManager.Validate(string)"/> refused a gesture.</summary>
public enum HotkeyRule { None, Unreadable, NeedsModifier, ShiftTyping, Debugger, System }

/// <param name="Warning">A Win combination: allowed, but Windows reserves many of them.</param>
public readonly record struct HotkeyValidation(HotkeyResult Result, HotkeyRule Rule = HotkeyRule.None, bool Warning = false);

/// <summary>
/// A key with its modifiers. The text form is canonical: modifiers in the order Ctrl, Alt, Shift, Win, then the key
/// ("Ctrl+Alt+Shift+F20", "Ctrl+5", "Ctrl+OemComma").
/// </summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    const ModifierKeys AllModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

    static readonly (ModifierKeys Flag, string Name)[] ModifierOrder =
        [(ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win")];

    /// <summary>Keys with two names in <see cref="Key"/>: <see cref="Enum.ToString()"/> may give either, the format always gives this one.</summary>
    static readonly Dictionary<Key, string> Names = new()
    {
        [Key.Enter] = "Enter",
        [Key.CapsLock] = "CapsLock",
        [Key.PageUp] = "PageUp",
        [Key.PageDown] = "PageDown",
        [Key.PrintScreen] = "PrintScreen",
        [Key.OemSemicolon] = "OemSemicolon",
        [Key.OemQuestion] = "OemQuestion",
        [Key.OemTilde] = "OemTilde",
        [Key.OemOpenBrackets] = "OemOpenBrackets",
        [Key.OemPipe] = "OemPipe",
        [Key.OemCloseBrackets] = "OemCloseBrackets",
        [Key.OemQuotes] = "OemQuotes",
        [Key.OemBackslash] = "OemBackslash",
    };

    /// <summary>The MOD_ flags of RegisterHotKey.</summary>
    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Control) ? Native.MOD_CONTROL : 0) | (Modifiers.HasFlag(ModifierKeys.Alt) ? Native.MOD_ALT : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? Native.MOD_SHIFT : 0) | (Modifiers.HasFlag(ModifierKeys.Windows) ? Native.MOD_WIN : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString() => Format(Modifiers, Key);

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var text = new StringBuilder();
        foreach (var (flag, name) in ModifierOrder)
            if (modifiers.HasFlag(flag)) text.Append(name).Append('+');
        return text.Append(KeyName(key)).ToString();
    }

    /// <summary>Reads what <see cref="Format"/> writes and the older forms ("Control+shift+t", "Ctrl+D5"), ignoring case.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ModifierOf(part) is { } modifier)
            {
                modifiers |= modifier;
                continue;
            }
            if (key is not null || KeyOf(part) is not { } named) return false; // a second key, or none
            key = named;
        }
        if (key is not { } found) return false;
        gesture = new HotkeyGesture(modifiers, found);
        return true;
    }

    /// <summary>
    /// The gesture a WPF key event makes, for a recorder: an Alt combination arrives as <see cref="Key.System"/> with
    /// the key in <paramref name="systemKey"/>. Null while only modifiers are pressed.
    /// </summary>
    public static HotkeyGesture? FromKeyEvent(Key key, ModifierKeys modifiers, Key systemKey)
    {
        var pressed = key == Key.System ? systemKey : key;
        if (pressed == Key.None || IsModifierKey(pressed)) return null;
        return new HotkeyGesture(modifiers & AllModifiers, pressed);
    }

    static string KeyName(Key key) =>
        key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString()
        : Names.TryGetValue(key, out var name) ? name
        : key.ToString();

    static ModifierKeys? ModifierOf(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => ModifierKeys.Control,
        "alt" => ModifierKeys.Alt,
        "shift" => ModifierKeys.Shift,
        "win" or "windows" => ModifierKeys.Windows,
        _ => null,
    };

    static Key? KeyOf(string part)
    {
        if (part.Length == 1 && char.IsAsciiDigit(part[0])) return Key.D0 + (part[0] - '0');
        // Enum.TryParse also takes numbers ("65") and lists ("A, B"): only a key's name counts.
        if (!part.All(char.IsAsciiLetterOrDigit) || char.IsAsciiDigit(part[0])) return null;
        if (!Enum.TryParse<Key>(part, ignoreCase: true, out var key) || IsModifierKey(key)
            || key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed) return null;
        return KeyInterop.VirtualKeyFromKey(key) == 0 ? null : key;
    }

    static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
}
