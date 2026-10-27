using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using SLiveTranslator.Capture;

namespace SLiveTranslator.Hotkeys;

/// <summary>System-wide hotkeys (RegisterHotKey) delivered on the UI thread through a message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    static readonly IntPtr HWND_MESSAGE = new(-3);
    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 1;

    public HotkeyManager()
    {
        _source = new HwndSource(0, 0, 0, 0, 0, "SLiveTranslatorHotkeys", HWND_MESSAGE);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    /// <summary>Registers e.g. "Ctrl+Shift+T". Returns false if the combination is invalid or already taken.</summary>
    public bool Register(string hotkey, Action action)
    {
        if (!TryParse(hotkey, out var modifiers, out var vk)) return false;
        int id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, modifiers | Native.MOD_NOREPEAT, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public static bool TryParse(string hotkey, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(hotkey)) return false;
        foreach (var part in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= Native.MOD_CONTROL; continue;
                case "shift": modifiers |= Native.MOD_SHIFT; continue;
                case "alt": modifiers |= Native.MOD_ALT; continue;
                case "win": modifiers |= Native.MOD_WIN; continue;
            }
            var name = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
            if (!Enum.TryParse<Key>(name, ignoreCase: true, out var key)) return false;
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        return virtualKey != 0;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
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
