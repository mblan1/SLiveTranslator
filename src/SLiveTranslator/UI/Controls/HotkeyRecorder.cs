using System.Text;
using System.Windows.Input;
using SLiveTranslator.Hotkeys;

namespace SLiveTranslator.UI.Controls;

/// <summary>What a key press did to the recording.</summary>
public enum RecordStep
{
    /// <summary>Only modifiers are down: the box shows them ("Ctrl+Shift+…").</summary>
    Partial,

    /// <summary>A key with its modifiers: <see cref="HotkeyRecorder.Gesture"/> is the new gesture.</summary>
    Completed,

    /// <summary>Escape: the gesture the recording started with is back.</summary>
    Cancelled,

    /// <summary>Backspace: no gesture.</summary>
    Cleared,

    /// <summary>Tab alone: not recorded, so the focus can move on.</summary>
    PassThrough,
}

/// <summary>
/// The rules of <see cref="HotkeyBox"/> without a window: key events in, the text to show and the gesture out. A press
/// of a modifier alone shows what is held so far; a key completes the gesture; Escape gives back the gesture the
/// recording started with and Backspace clears it, each pressed without modifiers.
/// </summary>
public sealed class HotkeyRecorder
{
    string _original = "";
    bool _partial;

    /// <summary>What the box shows.</summary>
    public string Text { get; private set; } = "";

    /// <summary>The recorded gesture in its canonical form; "" when cleared.</summary>
    public string Gesture { get; private set; } = "";

    /// <summary>The box took the focus; <paramref name="gesture"/> is the one set now.</summary>
    public void Begin(string gesture)
    {
        _original = Gesture = gesture;
        Text = gesture;
        _partial = false;
    }

    /// <param name="systemKey">The key of an Alt combination, which WPF reports as <see cref="Key.System"/>.</param>
    public RecordStep KeyDown(Key key, ModifierKeys modifiers, Key systemKey = Key.None)
    {
        var pressed = key == Key.System ? systemKey : key;
        if (modifiers == ModifierKeys.None)
        {
            switch (pressed)
            {
                case Key.Escape:
                    return Finish(_original, RecordStep.Cancelled);
                case Key.Back:
                    return Finish("", RecordStep.Cleared);
                case Key.Tab:
                    return RecordStep.PassThrough;
            }
        }
        if (HotkeyGesture.FromKeyEvent(key, modifiers, systemKey) is not { } gesture)
        {
            _partial = true;
            Text = Held(modifiers);
            return RecordStep.Partial;
        }
        return Finish(gesture.ToString(), RecordStep.Completed);
    }

    /// <param name="modifiers">The modifiers still held after the key went up.</param>
    public void KeyUp(ModifierKeys modifiers)
    {
        if (!_partial) return;
        if (modifiers == ModifierKeys.None)
        {
            _partial = false;
            Text = Gesture; // let go without a key: the gesture stays as it was
        }
        else
        {
            Text = Held(modifiers);
        }
    }

    RecordStep Finish(string gesture, RecordStep step)
    {
        Gesture = gesture;
        Text = gesture;
        _partial = false;
        return step;
    }

    static string Held(ModifierKeys modifiers)
    {
        var text = new StringBuilder();
        if (modifiers.HasFlag(ModifierKeys.Control)) text.Append("Ctrl+");
        if (modifiers.HasFlag(ModifierKeys.Alt)) text.Append("Alt+");
        if (modifiers.HasFlag(ModifierKeys.Shift)) text.Append("Shift+");
        if (modifiers.HasFlag(ModifierKeys.Windows)) text.Append("Win+");
        return text.Append('…').ToString();
    }
}
