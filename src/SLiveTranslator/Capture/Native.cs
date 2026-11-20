using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SLiveTranslator.Capture;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x20;
    public const long WS_EX_TOOLWINDOW = 0x80;
    public const long WS_EX_LAYERED = 0x80000;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1;
    public const uint SWP_NOMOVE = 0x2;
    public const uint SWP_NOZORDER = 0x4;
    public const uint SWP_NOACTIVATE = 0x10;
    public const uint SWP_SHOWWINDOW = 0x40;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_GETMINMAXINFO = 0x0024;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const int ATTACH_PARENT_PROCESS = -1;
    public const uint GA_ROOT = 2;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>GetWindowLongPtr, which 32-bit user32 does not export (it is a macro for GetWindowLong there).</summary>
    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtrW(hWnd, nIndex) : new IntPtr(GetWindowLongW(hWnd, nIndex));

    /// <summary>SetWindowLongPtr, with the same 32-bit fallback as <see cref="GetWindowLongPtr"/>.</summary>
    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtrW(hWnd, nIndex, value) : new IntPtr(SetWindowLongW(hWnd, nIndex, value.ToInt32()));

    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int processId);
}
