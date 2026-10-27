using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using SLiveTranslator.Capture;
using SLiveTranslator.Models;

namespace SLiveTranslator.Hotkeys;

/// <summary>
/// System-wide hotkeys (RegisterHotKey) delivered on the UI thread through a message-only window. Keeps the result of
/// each action's last registration for the settings page. Call on the thread that made it.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>The last id an application may use: the probe never meets a registration there.</summary>
    const int ProbeId = 0xBFFF;

    static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>Windows or the user's muscle memory owns these whatever RegisterHotKey says.</summary>
    static readonly HashSet<HotkeyGesture> SystemShortcuts =
    [
        new(ModifierKeys.Control | ModifierKeys.Alt, Key.Delete),
        new(ModifierKeys.Control | ModifierKeys.Shift, Key.Escape),
        new(ModifierKeys.Control, Key.Escape),
        new(ModifierKeys.Alt, Key.Tab),
        new(ModifierKeys.Alt | ModifierKeys.Shift, Key.Tab),
        new(ModifierKeys.Control | ModifierKeys.Alt, Key.Tab),
        new(ModifierKeys.Alt, Key.Escape),
        new(ModifierKeys.Alt, Key.F4),
        new(ModifierKeys.Alt, Key.Space),
        new(ModifierKeys.Windows, Key.L),
    ];

    /// <summary>Keys that type a character: with Shift alone as the hotkey, capitals and symbols could not be typed any more.</summary>
    static readonly HashSet<Key> PunctuationKeys =
    [
        Key.Space, Key.OemSemicolon, Key.OemPlus, Key.OemComma, Key.OemMinus, Key.OemPeriod, Key.OemQuestion, Key.OemTilde,
        Key.OemOpenBrackets, Key.OemPipe, Key.OemCloseBrackets, Key.OemQuotes, Key.Oem8, Key.OemBackslash,
    ];

    sealed record Request(string Gesture, Action Callback, bool Disabled);

    readonly HwndSource _source;
    readonly Dictionary<HotkeyAction, Request> _requests = new();
    readonly Dictionary<HotkeyAction, HotkeyResult> _states = new();
    readonly Dictionary<HotkeyAction, (int Id, HotkeyGesture Gesture)> _held = new();
    readonly Dictionary<int, Action> _callbacks = new();
    int _nextId = 1;

    public HotkeyManager()
    {
        _source = new HwndSource(0, 0, 0, 0, 0, "SLiveTranslatorHotkeys", HWND_MESSAGE);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    /// <summary>Everything is unregistered until <see cref="Resume"/> (a recorder is taking a gesture).</summary>
    public bool Suspended { get; private set; }

    /// <summary>
    /// Registers <paramref name="gesture"/> for <paramref name="action"/>, replacing the action's earlier one. An id is
    /// used up only when Windows accepts the hotkey. While <see cref="Suspended"/>, a valid gesture is registered by
    /// <see cref="Resume"/>.
    /// </summary>
    public HotkeyResult Register(HotkeyAction action, string gesture, Action callback)
    {
        _requests[action] = new Request(gesture, callback, Disabled: false);
        return _states[action] = Apply(action);
    }

    /// <summary>Replaces every registration with the settings' gestures; a switched-off action reports <see cref="HotkeyResult.Disabled"/>.</summary>
    /// <returns>The result of each action in <paramref name="callbacks"/>.</returns>
    public IReadOnlyDictionary<HotkeyAction, HotkeyResult> RegisterAll(HotkeySettings settings, IReadOnlyDictionary<HotkeyAction, Action> callbacks)
    {
        UnregisterAll();
        foreach (var (action, callback) in callbacks)
            _requests[action] = new Request(settings[action], callback, settings.Disabled.Contains(action));
        return RegisterRequests();
    }

    /// <summary>The result of the action's last registration; null when it was never registered.</summary>
    public HotkeyResult? State(HotkeyAction action) => _states.TryGetValue(action, out var state) ? state : null;

    /// <summary>Unregisters everything but remembers it: pressing the current combination in a recorder then records it instead of firing.</summary>
    public void Suspend()
    {
        ReleaseAll();
        Suspended = true;
    }

    /// <summary>Registers again what was registered before <see cref="Suspend"/>.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyResult> Resume()
    {
        Suspended = false;
        return RegisterRequests();
    }

    public void UnregisterAll()
    {
        ReleaseAll();
        _requests.Clear();
        _states.Clear();
    }

    /// <summary>
    /// Whether <paramref name="gesture"/> is free right now: registered under a scratch id and unregistered at once. A
    /// gesture this manager holds for <paramref name="editing"/> counts as free; one it has for another action is a
    /// <see cref="HotkeyResult.Duplicate"/>.
    /// </summary>
    public HotkeyResult Probe(string gesture, HotkeyAction? editing = null)
    {
        var check = Validate(gesture);
        if (check.Result != HotkeyResult.Ok) return check.Result;
        HotkeyGesture.TryParse(gesture, out var parsed);
        if (editing is { } own && _held.TryGetValue(own, out var held) && held.Gesture == parsed) return HotkeyResult.Ok;
        if (TakenByAnother(editing, parsed)) return HotkeyResult.Duplicate;
        if (!TryRegister(ProbeId, parsed, out var result)) return result;
        Native.UnregisterHotKey(_source.Handle, ProbeId);
        return HotkeyResult.Ok;
    }

    /// <inheritdoc cref="Validate(HotkeyGesture)"/>
    public static HotkeyValidation Validate(string gesture) =>
        HotkeyGesture.TryParse(gesture, out var parsed) ? Validate(parsed) : new HotkeyValidation(HotkeyResult.Invalid, HotkeyRule.Unreadable);

    /// <summary>
    /// Whether the gesture may be a hotkey at all: a modifier is needed unless the key is F1–F24, F12 belongs to the
    /// debugger, Shift alone with a key that types would block typing, and the system shortcuts stay Windows'. A Win
    /// combination is allowed with a warning.
    /// </summary>
    public static HotkeyValidation Validate(HotkeyGesture gesture)
    {
        var (modifiers, key) = (gesture.Modifiers, gesture.Key);
        if (key == Key.F12) return new HotkeyValidation(HotkeyResult.Reserved, HotkeyRule.Debugger);
        if (SystemShortcuts.Contains(gesture)) return new HotkeyValidation(HotkeyResult.Reserved, HotkeyRule.System);
        if (modifiers == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24))
            return new HotkeyValidation(HotkeyResult.Invalid, HotkeyRule.NeedsModifier);
        if (modifiers == ModifierKeys.Shift && (key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 || PunctuationKeys.Contains(key)))
            return new HotkeyValidation(HotkeyResult.Invalid, HotkeyRule.ShiftTyping);
        return new HotkeyValidation(HotkeyResult.Ok, Warning: modifiers.HasFlag(ModifierKeys.Windows));
    }

    IReadOnlyDictionary<HotkeyAction, HotkeyResult> RegisterRequests()
    {
        ReleaseAll();
        _states.Clear();
        // In a fixed order, so of two actions with one gesture the same one gets it each time.
        foreach (var action in _requests.Keys.Order().ToList()) _states[action] = Apply(action);
        return new Dictionary<HotkeyAction, HotkeyResult>(_states);
    }

    HotkeyResult Apply(HotkeyAction action)
    {
        Release(action);
        var request = _requests[action];
        if (request.Disabled) return HotkeyResult.Disabled;
        var check = Validate(request.Gesture);
        if (check.Result != HotkeyResult.Ok) return check.Result;
        HotkeyGesture.TryParse(request.Gesture, out var gesture);
        if (TakenByAnother(action, gesture)) return HotkeyResult.Duplicate;
        if (Suspended) return HotkeyResult.Ok;
        if (!TryRegister(_nextId, gesture, out var result)) return result;
        int id = _nextId++;
        _held[action] = (id, gesture);
        _callbacks[id] = request.Callback;
        return HotkeyResult.Ok;
    }

    /// <summary>Another action has this gesture and got it (or gets it on <see cref="Resume"/>).</summary>
    bool TakenByAnother(HotkeyAction? action, HotkeyGesture gesture) =>
        _states.Any(s => s.Key != action && s.Value == HotkeyResult.Ok
                         && HotkeyGesture.TryParse(_requests[s.Key].Gesture, out var other) && other == gesture);

    bool TryRegister(int id, HotkeyGesture gesture, out HotkeyResult result)
    {
        if (Native.RegisterHotKey(_source.Handle, id, gesture.NativeModifiers | Native.MOD_NOREPEAT, gesture.VirtualKey))
        {
            result = HotkeyResult.Ok;
            return true;
        }
        result = Marshal.GetLastPInvokeError() == ErrorHotkeyAlreadyRegistered ? HotkeyResult.InUse : HotkeyResult.Invalid;
        return false;
    }

    void Release(HotkeyAction action)
    {
        if (!_held.Remove(action, out var held)) return;
        Native.UnregisterHotKey(_source.Handle, held.Id);
        _callbacks.Remove(held.Id);
    }

    void ReleaseAll()
    {
        foreach (var action in _held.Keys.ToList()) Release(action);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            handled = true;
            callback();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
