using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.UI.Overlay;

/// <summary>Short message at the top of the game's monitor. Windows hides tray notifications in games; this shows.</summary>
public sealed class ToastOverlay : ClickThroughWindow
{
    readonly DispatcherTimer _timer = new();
    readonly TextBlock _text = new()
    {
        Foreground = OverlayLook.Text,
        FontFamily = OverlayLook.Font,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 900,
    };
    readonly Border _box;

    public ToastOverlay()
    {
        Title = "SLiveTranslator status";
        _box = new Border
        {
            Background = OverlayLook.ToastBackground,
            BorderThickness = new Thickness(0, 0, 0, 3),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 9, 18, 9),
            Child = _text,
        };
        Content = _box;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Dismiss();
        };
    }

    /// <summary>Shows the message centred at the top of the monitor for some seconds.</summary>
    public void ShowMessage(string text, StatusKind kind, DRect screenPx, double seconds)
    {
        _text.Text = text;
        _box.BorderBrush = OverlayLook.Accent(kind);
        double scale = ScaleAt(screenPx);
        var size = MeasureContent(scale);
        ShowAt(screenPx.Left + Math.Max(0, (screenPx.Width - size.Width) / 2), screenPx.Top + (int)Math.Round(28 * scale));
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }
}
