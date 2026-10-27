using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SLiveTranslator.Models;
using DPoint = System.Drawing.Point;
using DRect = System.Drawing.Rectangle;
using DSize = System.Drawing.Size;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI.Overlay;

/// <summary>Short message over the game. Windows hides tray notifications in games; this shows.</summary>
public sealed class ToastOverlay : ClickThroughWindow
{
    /// <summary>Distance from the monitor's edge, in DIPs.</summary>
    const int EdgeMargin = 28;

    /// <summary>Distance from the subtitle region, in DIPs: clear of the live badge above its corner.</summary>
    const int RegionGap = 32;

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
        _timer.Tick += (_, _) => HideMessage();
    }

    /// <summary>Shows the message for some seconds at the top of the monitor, or near the subtitle region.</summary>
    /// <param name="regionPx">The subtitle region, for <see cref="NoticePosition.NearRegion"/>; without one the toast goes to the top centre.</param>
    /// <param name="subtitlesPx">Where the translated lines are, which a toast near the region must not cover; null when none show.</param>
    public void ShowMessage(string text, StatusKind kind, DRect screenPx, double seconds,
        NoticePosition position = NoticePosition.TopCenter, DRect? regionPx = null, DRect? subtitlesPx = null)
    {
        _text.Text = text;
        _box.BorderBrush = OverlayLook.Accent(kind);
        var near = position == NoticePosition.NearRegion ? regionPx : null;
        if (near is { } region) screenPx = Forms.Screen.FromRectangle(region).Bounds;
        double scale = ScaleAt(near ?? screenPx);
        var at = Place(position, screenPx, near, MeasureContent(scale), scale, subtitlesPx);
        ShowAt(at.X, at.Y);
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    /// <summary>Takes the message away before its time.</summary>
    public void HideMessage()
    {
        _timer.Stop();
        Dismiss();
    }

    /// <summary>The toast's top-left corner (physical pixels) on <paramref name="screenPx"/>, the monitor it goes to.</summary>
    /// <param name="subtitlesPx">The translated lines, below or above the region: a toast near the region goes beyond them.</param>
    internal static DPoint Place(NoticePosition position, DRect screenPx, DRect? regionPx, DSize size, double scale, DRect? subtitlesPx = null)
    {
        int margin = (int)Math.Round(EdgeMargin * scale);
        int top = screenPx.Top + margin;
        switch (position)
        {
            case NoticePosition.TopRight:
                return new DPoint(Math.Max(screenPx.Left, screenPx.Right - margin - size.Width), top);
            case NoticePosition.NearRegion when regionPx is { } region:
                // Centred above the region and the lines, wherever they are drawn; below both when there is no room above.
                var taken = subtitlesPx is { } lines ? DRect.Union(region, lines) : region;
                int gap = (int)Math.Round(RegionGap * scale);
                int x = Math.Clamp(region.Left + (region.Width - size.Width) / 2, screenPx.Left, Math.Max(screenPx.Left, screenPx.Right - size.Width));
                int y = taken.Top - gap - size.Height;
                if (y < screenPx.Top) y = Math.Max(screenPx.Top, Math.Min(taken.Bottom + gap, screenPx.Bottom - size.Height));
                return new DPoint(x, y);
            default:
                return new DPoint(screenPx.Left + Math.Max(0, (screenPx.Width - size.Width) / 2), top);
        }
    }
}
