using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using SLiveTranslator.Capture;
using DPoint = System.Drawing.Point;
using DRect = System.Drawing.Rectangle;
using DSize = System.Drawing.Size;

namespace SLiveTranslator.UI.Overlay;

/// <summary>
/// Base of the overlay windows: transparent, topmost, click-through, never activated, hidden from screen capture (so
/// the app never reads its own text) and only as big as its content. Small windows instead of one over the whole
/// monitor: a layered window redraws the box around everything that changed in it, so a toast at the top and the
/// subtitles at the bottom of one monitor-sized window redrew the whole screen behind the game.
/// </summary>
public class ClickThroughWindow : Window
{
    static readonly Style[] PlainStyles = [Sealed(typeof(TextBlock)), Sealed(typeof(Border)), Sealed(typeof(StackPanel)), Sealed(typeof(Canvas))];

    DPoint? _placedAt;
    readonly bool _clickThrough;

    public ClickThroughWindow() : this(clickThrough: true)
    {
    }

    /// <param name="clickThrough">
    /// False for a window that takes clicks (the in-game menu): still never activated, so the game stays in front and
    /// keeps the keyboard. Nothing in it may take the focus either (no WPF Buttons: they focus themselves on a click).
    /// </param>
    protected ClickThroughWindow(bool clickThrough)
    {
        _clickThrough = clickThrough;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = !clickThrough;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        // The main window merges WPF-UI's implicit styles into the app's resources; these empty ones come first for
        // the overlay's own elements, so they keep the look set here whatever the app's theme.
        foreach (var style in PlainStyles) Resources[style.TargetType] = style;
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    static Style Sealed(Type type)
    {
        var style = new Style(type);
        style.Seal();
        return style;
    }

    /// <summary>The window handle; the window is created (hidden) on first use, so it can be placed before it shows.</summary>
    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>Screen capture, and so the app's own OCR, does not see the window (Windows 10 2004 and later).</summary>
    public bool ExcludedFromCapture { get; private set; }

    bool _shared;

    /// <summary>
    /// The window is part of the screen for screen sharing and recording (Shared on screen): capture, the app's own OCR
    /// included, sees it. False (the default) hides it from capture.
    /// </summary>
    public bool Shared
    {
        get => _shared;
        set
        {
            if (_shared == value) return;
            _shared = value;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) ApplyCaptureAffinity(hwnd);
        }
    }

    void ApplyCaptureAffinity(IntPtr hwnd)
    {
        bool ok = Native.SetWindowDisplayAffinity(hwnd, _shared ? Native.WDA_NONE : Native.WDA_EXCLUDEFROMCAPTURE);
        ExcludedFromCapture = !_shared && ok;
    }

    void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        if (_clickThrough) ex |= Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        ApplyCaptureAffinity(hwnd);
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(AllowAnySize);
        if (!_clickThrough) source?.AddHook(NeverActivate);
        // WPF took the minimum size from the first query, sent before the hook existed: a resize asks again.
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 1, 1, Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Windows makes a window at least 136x39 px, and WPF keeps to that when it sizes the window to its content; the
    /// badge is smaller, and the rest of the box would only be redrawn for nothing.
    /// </summary>
    static IntPtr AllowAnySize(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_GETMINMAXINFO) return IntPtr.Zero;
        var info = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
        info.MinTrackSize = new Native.POINT { X = 1, Y = 1 };
        Marshal.StructureToPtr(info, lParam, false);
        return IntPtr.Zero; // not handled: WPF takes the minimum from here
    }

    /// <summary>A click on a window that takes clicks leaves the game in front (WS_EX_NOACTIVATE alone does not stop it).</summary>
    static IntPtr NeverActivate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_MOUSEACTIVATE) return IntPtr.Zero;
        handled = true;
        return new IntPtr(Native.MA_NOACTIVATE);
    }

    /// <summary>
    /// The window's own state: shown by its last Show call and not dismissed since. <see cref="OverlayHost.Hide"/>
    /// hides the windows only for a moment and brings back only these, so a toast that expired meanwhile stays gone.
    /// </summary>
    public bool Showing { get; private set; }

    /// <summary>Hides the window until its next Show call.</summary>
    public void Dismiss()
    {
        Showing = false;
        Hide();
    }

    /// <summary>
    /// Physical pixels per DIP on the monitor that shows most of <paramref name="targetPx"/>, where the window is going.
    /// Not the window's own DPI: that is of the monitor it is on now (the system's before it first shows), which is
    /// another one on monitors set to different scales.
    /// </summary>
    protected double ScaleAt(DRect targetPx)
    {
        var rect = new Native.RECT { Left = targetPx.Left, Top = targetPx.Top, Right = targetPx.Right, Bottom = targetPx.Bottom };
        var monitor = Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    /// <summary>The size of the content in physical pixels at <paramref name="scale"/> (see <see cref="ScaleAt"/>).</summary>
    protected DSize MeasureContent(double scale)
    {
        var content = (UIElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new DSize((int)Math.Ceiling(content.DesiredSize.Width * scale), (int)Math.Ceiling(content.DesiredSize.Height * scale));
    }

    /// <summary>Shows the window with its top-left corner at a screen point (physical pixels); it is moved only when that changed.</summary>
    protected void ShowAt(int x, int y)
    {
        Showing = true;
        var at = new DPoint(x, y);
        if (IsVisible && _placedAt == at) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _placedAt = at;
        if (!IsVisible) Show();
        // Arriving on a monitor at another scale, WPF resizes the window for its DPI and moves it to where Windows
        // suggests; the corner goes back where it belongs.
        if (Native.GetWindowRect(Handle, out var placed) && (placed.Left != x || placed.Top != y))
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Shows the window over a screen rectangle (physical pixels): for a window that is not sized to its content.</summary>
    protected void ShowOver(DRect boundsPx)
    {
        Showing = true;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, boundsPx.Left, boundsPx.Top, boundsPx.Width, boundsPx.Height, Native.SWP_NOACTIVATE);
        _placedAt = boundsPx.Location;
        if (!IsVisible) Show();
        if (Native.GetWindowRect(Handle, out var placed) && placed.ToRectangle() != boundsPx) // see ShowAt
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, boundsPx.Left, boundsPx.Top, boundsPx.Width, boundsPx.Height, Native.SWP_NOACTIVATE);
    }
}
