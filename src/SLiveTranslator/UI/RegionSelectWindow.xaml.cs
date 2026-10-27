using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SLiveTranslator.Capture;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI;

/// <summary>Full-monitor dimmed layer where the user drags a rectangle. Result is in physical screen pixels.</summary>
public partial class RegionSelectWindow : Window
{
    readonly Forms.Screen _screen;
    Point? _start;

    public DRect? Result { get; private set; }

    public RegionSelectWindow(Forms.Screen screen)
    {
        InitializeComponent();
        _screen = screen;
        SourceInitialized += (_, _) =>
        {
            var b = _screen.Bounds;
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_SHOWWINDOW);
        };
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _start = e.GetPosition(Root);
        Selection.Visibility = Visibility.Visible;
        UpdateSelection(_start.Value);
        CaptureMouse();
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_start is not null) UpdateSelection(e.GetPosition(Root));
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start || e.ChangedButton != MouseButton.Left) return;
        ReleaseMouseCapture();
        var rect = new Rect(start, e.GetPosition(Root));
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var px = new DRect(
            (int)Math.Round(_screen.Bounds.Left + rect.X * scale),
            (int)Math.Round(_screen.Bounds.Top + rect.Y * scale),
            (int)Math.Round(rect.Width * scale),
            (int)Math.Round(rect.Height * scale));
        if (px.Width >= 20 && px.Height >= 10)
        {
            Result = px;
            DialogResult = true;
        }
        else
        {
            DialogResult = false;
        }
    }

    void UpdateSelection(Point current)
    {
        var rect = new Rect(_start!.Value, current);
        Canvas.SetLeft(Selection, rect.X);
        Canvas.SetTop(Selection, rect.Y);
        Selection.Width = rect.Width;
        Selection.Height = rect.Height;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
