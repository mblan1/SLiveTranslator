using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI.Overlay;

/// <summary>The translated dialog lines next to the subtitle region. Its TextBlocks are made once and reused.</summary>
public sealed class SubtitleOverlay : ClickThroughWindow
{
    readonly StackPanel _lines = new();
    readonly Border _box;

    public SubtitleOverlay()
    {
        Title = "SLiveTranslator subtitles";
        _box = new Border
        {
            Background = OverlayLook.SubtitleBackground,
            BorderBrush = OverlayLook.Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            Child = _lines,
        };
        Content = _box;
    }

    /// <summary>
    /// Shows the latest dialog lines (oldest first, newest last and brightest) below the region, or above it when the
    /// region is at the bottom of the screen.
    /// </summary>
    public void ShowLines(IReadOnlyList<SubtitleLineView> lines, DRect regionPx, int fontSize)
    {
        while (_lines.Children.Count < lines.Count)
        {
            _lines.Children.Add(new TextBlock
            {
                FontFamily = OverlayLook.Font,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            });
        }

        // The latest finished translation stays bright even while a newer line is still "…".
        int lastReady = lines.Count - 1;
        while (lastReady > 0 && lines[lastReady].Pending) lastReady--;
        for (int i = 0; i < _lines.Children.Count; i++)
        {
            var block = (TextBlock)_lines.Children[i];
            if (i >= lines.Count)
            {
                block.Visibility = Visibility.Collapsed;
                continue;
            }
            var line = lines[i];
            bool newest = i >= lastReady;
            block.Visibility = Visibility.Visible;
            block.Text = line.Text;
            block.FontSize = newest ? fontSize : Math.Max(10, fontSize * 0.82);
            block.Foreground = line.Pending ? OverlayLook.Pending : line.Failed ? OverlayLook.Original : OverlayLook.Text;
            block.FontStyle = line.Pending || line.LowConfidence ? FontStyles.Italic : FontStyles.Normal;
            block.Opacity = newest ? 1.0 : 0.62;
            block.Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0);
        }

        var screen = Forms.Screen.FromRectangle(regionPx).Bounds;
        double scale = ScaleAt(regionPx);
        _box.MaxWidth = Math.Min(Math.Max(regionPx.Width, 480 * scale), screen.Width - 16 * scale) / scale;
        var size = MeasureContent(scale);
        int gap = (int)Math.Round(6 * scale);
        int x = Math.Clamp(regionPx.Left + (regionPx.Width - size.Width) / 2, screen.Left, Math.Max(screen.Left, screen.Right - size.Width));
        int below = regionPx.Bottom + gap;
        int y = below + size.Height <= screen.Bottom ? below : Math.Max(screen.Top, regionPx.Top - size.Height - gap);
        ShowAt(x, y);
    }
}
