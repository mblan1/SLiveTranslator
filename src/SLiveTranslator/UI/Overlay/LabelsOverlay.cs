using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.UI.Overlay;

/// <summary>
/// The translate-screen labels, each on top of the text it translates. As big as the monitor, so it exists only
/// while labels are shown and is closed when they are cleared.
/// </summary>
public sealed class LabelsOverlay : ClickThroughWindow
{
    readonly Canvas _canvas = new();
    DRect _screen;

    public LabelsOverlay()
    {
        Title = "SLiveTranslator labels";
        SizeToContent = SizeToContent.Manual;
        Content = _canvas;
    }

    /// <summary>Draws one label per recognized paragraph, replacing the labels shown before.</summary>
    public void ShowLabels(IEnumerable<(DRect BoundsPx, string Text, int LineCount)> items, DRect screenPx, int baseFontSize)
    {
        if (!IsVisible || screenPx != _screen) ShowOver(screenPx);
        _screen = screenPx;
        _canvas.Children.Clear();
        double scale = ScaleAt(screenPx);
        double widthDip = screenPx.Width / scale, heightDip = screenPx.Height / scale;

        foreach (var (boundsPx, text, lineCount) in items)
        {
            var r = new Rect((boundsPx.Left - screenPx.Left) / scale, (boundsPx.Top - screenPx.Top) / scale, boundsPx.Width / scale, boundsPx.Height / scale);
            double lineHeight = r.Height / Math.Max(1, lineCount);
            var border = new Border
            {
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = OverlayLook.Text,
                    FontSize = Math.Clamp(lineHeight * 0.72, 10, Math.Max(12, baseFontSize * 1.6)),
                    FontFamily = OverlayLook.Font,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = Math.Max(r.Width + 24, 240),
                },
                Background = OverlayLook.LabelBackground,
                BorderBrush = OverlayLook.LabelOutline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
            };
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = border.DesiredSize;
            Canvas.SetLeft(border, Math.Clamp(r.Left - 6, 0, Math.Max(0, widthDip - size.Width)));
            Canvas.SetTop(border, Math.Clamp(r.Top - 2, 0, Math.Max(0, heightDip - size.Height)));
            _canvas.Children.Add(border);
        }
    }
}
