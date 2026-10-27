using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.UI.Overlay;

/// <summary>Panel with the recent dialog (newest at the bottom) so lines that went by too fast can be read again.</summary>
public sealed class HistoryOverlay : ClickThroughWindow
{
    readonly TextBlock _title = new()
    {
        Foreground = OverlayLook.HistoryTitle,
        FontFamily = OverlayLook.Font,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Margin = new Thickness(0, 0, 0, 8),
    };
    readonly TextBlock _empty = new() { Text = "No dialog yet.", Foreground = OverlayLook.Original, FontSize = 15, FontFamily = OverlayLook.Font };
    readonly StackPanel _lines = new();
    readonly Border _box;

    public HistoryOverlay()
    {
        Title = "SLiveTranslator history";
        var panel = new StackPanel();
        panel.Children.Add(_title);
        panel.Children.Add(_empty);
        panel.Children.Add(_lines);
        _box = new Border
        {
            Background = OverlayLook.HistoryBackground,
            BorderBrush = OverlayLook.Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Child = panel,
        };
        Content = _box;
    }

    /// <summary>Shows the lines at the top left of the monitor. The line items are made once and reused.</summary>
    public void ShowLines(string title, IReadOnlyList<HistoryLineView> lines, DRect screenPx)
    {
        _title.Text = title;
        _empty.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        while (_lines.Children.Count < lines.Count) _lines.Children.Add(NewItem());
        for (int i = 0; i < _lines.Children.Count; i++)
        {
            var item = (StackPanel)_lines.Children[i];
            if (i >= lines.Count)
            {
                item.Visibility = Visibility.Collapsed;
                continue;
            }
            var line = lines[i];
            item.Visibility = Visibility.Visible;
            var translated = (TextBlock)item.Children[0];
            translated.Text = line.Pending ? "…" : line.Translated;
            translated.Foreground = line.Pending ? OverlayLook.Pending : OverlayLook.Text;
            ((TextBlock)item.Children[1]).Text = line.Original;
        }

        double scale = ScaleAt(screenPx);
        _box.Width = Math.Min(640, screenPx.Width / scale - 48);
        _box.MaxHeight = screenPx.Height / scale - 160;
        ShowAt(screenPx.Left + (int)Math.Round(24 * scale), screenPx.Top + (int)Math.Round(90 * scale));
    }

    static StackPanel NewItem()
    {
        var item = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        item.Children.Add(new TextBlock { FontSize = 16, FontFamily = OverlayLook.Font, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        item.Children.Add(new TextBlock { Foreground = OverlayLook.Original, FontSize = 12, FontFamily = OverlayLook.Font, TextWrapping = TextWrapping.Wrap });
        return item;
    }
}
