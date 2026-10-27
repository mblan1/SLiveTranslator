using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI.Overlay;

/// <summary>Small "subtitles on" marker at the corner of the watched region while subtitle mode runs.</summary>
public sealed class BadgeOverlay : ClickThroughWindow
{
    readonly TextBlock _text = new() { Foreground = OverlayLook.Text, FontFamily = OverlayLook.Font, FontSize = 12 };

    public BadgeOverlay()
    {
        Title = "SLiveTranslator badge";
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = OverlayLook.LiveDot,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        row.Children.Add(_text);
        Content = new Border { Background = OverlayLook.BadgeBackground, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 3, 8, 3), Child = row };
    }

    /// <summary>Just above the region's top-left corner, or just inside it when the region starts at the top of the screen.</summary>
    public void ShowText(string text, DRect regionPx)
    {
        _text.Text = text;
        var screen = Forms.Screen.FromRectangle(regionPx).Bounds;
        double scale = ScaleAt(regionPx);
        var size = MeasureContent(scale);
        int gap = (int)Math.Round(4 * scale);
        int y = regionPx.Top - size.Height - gap;
        ShowAt(Math.Clamp(regionPx.Left, screen.Left, Math.Max(screen.Left, screen.Right - size.Width)), y >= screen.Top ? y : regionPx.Top + gap);
    }
}
