using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SLiveTranslator.Capture;

public static class ScreenCapture
{
    /// <summary>Copies a screen rectangle (physical pixels, virtual-screen coordinates) into a bitmap.</summary>
    public static Bitmap Capture(Rectangle rect)
    {
        var bmp = new Bitmap(Math.Max(1, rect.Width), Math.Max(1, rect.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>
    /// Bounds of the foreground window (normally the game) in physical pixels, clipped to its monitor.
    /// Falls back to the primary monitor when the foreground window is ours or unusable.
    /// </summary>
    public static Rectangle ForegroundWindowRect(Func<IntPtr, bool> isOwnWindow)
    {
        var fallback = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || isOwnWindow(hwnd)) return fallback;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<Native.RECT>()) != 0)
            return fallback;
        var rect = Rectangle.Intersect(r.ToRectangle(), Screen.FromHandle(hwnd).Bounds);
        return rect.Width < 100 || rect.Height < 100 ? fallback : rect;
    }
}
