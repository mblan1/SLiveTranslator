using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SLiveTranslator.Models;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI.Overlay;

/// <summary>The translated dialog lines next to the subtitle region. Its TextBlocks are made once and reused.</summary>
public sealed class SubtitleOverlay : ClickThroughWindow
{
    /// <summary>Space between the bottom of the monitor and the box at <see cref="SubtitlePosition.BottomCenter"/>, as a share of the monitor's height.</summary>
    const double BottomMargin = 0.06;

    readonly StackPanel _lines = new();
    readonly Border _box;
    DRect _bounds;

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

    /// <summary>Where the lines are (physical pixels); null while they are not shown.</summary>
    public DRect? BoundsPx => Showing ? _bounds : null;

    /// <summary>
    /// Shows the latest dialog lines (oldest first, newest last and brightest) where <paramref name="style"/> puts them:
    /// below the region (above it when the region is at the bottom of the screen), above it (below when there is no
    /// room), or at the bottom centre of the monitor.
    /// </summary>
    public void ShowLines(IReadOnlyList<SubtitleLineView> lines, DRect regionPx, int fontSize, SubtitleStyle? style = null)
    {
        style ??= SubtitleStyle.Default;
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
            block.FontSize = newest ? fontSize : Math.Max(10, fontSize * 0.82);
            ShowText(block, line, style.ShowOriginal);
            block.Foreground = line.Pending ? OverlayLook.Pending : line.Failed ? OverlayLook.Original : OverlayLook.Text;
            block.FontStyle = line.Pending || line.LowConfidence ? FontStyles.Italic : FontStyles.Normal;
            block.Opacity = (newest ? 1.0 : 0.62) * style.TextOpacity;
            block.Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0);
        }
        _box.Background = OverlayLook.SubtitleBackgroundAt(style.BackgroundOpacity);

        var screen = Forms.Screen.FromRectangle(regionPx).Bounds;
        double scale = ScaleAt(regionPx);
        _box.MaxWidth = Math.Min(Math.Max(regionPx.Width, 480 * scale), screen.Width - 16 * scale) / scale;
        var size = MeasureContent(scale);
        int gap = (int)Math.Round(6 * scale);
        int x = Math.Clamp(regionPx.Left + (regionPx.Width - size.Width) / 2, screen.Left, Math.Max(screen.Left, screen.Right - size.Width));
        int below = regionPx.Bottom + gap;
        int above = regionPx.Top - size.Height - gap;
        int y = style.Position switch
        {
            SubtitlePosition.AboveRegion => above >= screen.Top ? above : Math.Min(below, Math.Max(screen.Top, screen.Bottom - size.Height)),
            SubtitlePosition.BottomCenter => Math.Max(screen.Top, screen.Bottom - size.Height - (int)(screen.Height * BottomMargin)),
            _ => below + size.Height <= screen.Bottom ? below : Math.Max(screen.Top, above),
        };
        if (style.Position == SubtitlePosition.BottomCenter) x = screen.Left + Math.Max(0, (screen.Width - size.Width) / 2);
        _bounds = new DRect(x, y, size.Width, size.Height);
        ShowAt(x, y);
    }

    /// <summary>The translation, with the original under it, smaller and dimmed, when that is asked for and differs.</summary>
    static void ShowText(TextBlock block, SubtitleLineView line, bool showOriginal)
    {
        if (!showOriginal || line.Pending || line.Failed || string.IsNullOrWhiteSpace(line.Original) || line.Original == line.Text)
        {
            block.Text = line.Text;
            return;
        }
        block.Inlines.Clear();
        block.Inlines.Add(new Run(line.Text));
        block.Inlines.Add(new LineBreak());
        block.Inlines.Add(new Run(line.Original) { FontSize = Math.Max(10, block.FontSize * 0.72), FontWeight = FontWeights.Normal, Foreground = OverlayLook.Original });
    }
}
