using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SLiveTranslator.Capture;
using SLiveTranslator.Models;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI.Overlay;

/// <summary>
/// The in-game menu (see <see cref="GameMenu"/>): a dark panel in the middle of the game window, one row per function
/// with its number key, its state and its hotkey. It takes clicks but is never activated, and nothing in it takes the
/// focus (rows are plain borders, not buttons), so the game keeps the keyboard; the keys reach the menu through the
/// hotkeys the app holds while it is open.
/// </summary>
public sealed class MenuOverlay : ClickThroughWindow
{
    const double PanelWidth = 420;

    /// <summary>The space (DIPs) between the menu and the game window's edges in a corner.</summary>
    const double EdgeMargin = 24;

    /// <summary>The pointer must move this far (px) after the menu opened before hovering selects: a parked cursor selects nothing.</summary>
    const int HoverAfter = 3;

    readonly TextBlock _title;
    readonly StackPanel _rows;
    readonly TextBlock _footer;
    readonly List<Row> _rowViews = new();
    System.Drawing.Point _openedAt;
    bool _hoverArmed;
    int _pressed = -1;

    sealed class Row
    {
        public required Border Box;
        public required TextBlock Key;
        public required TextBlock Label;
        public required TextBlock Value;
        public required TextBlock Gesture;
        public required Border Minus;
        public required Border Plus;
    }

    /// <summary>A row was clicked: its index, and for the font row's − or + the step (−1, +1), else 0.</summary>
    public event Action<int, int>? Picked;

    /// <summary>The pointer moved onto a row.</summary>
    public event Action<int>? Hovered;

    public MenuOverlay() : base(clickThrough: false)
    {
        _title = new TextBlock { FontFamily = OverlayLook.Font, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = OverlayLook.HistoryTitle, Margin = new Thickness(10, 4, 10, 8) };
        _rows = new StackPanel();
        _footer = new TextBlock
        {
            FontFamily = OverlayLook.Font, FontSize = 11.5, Foreground = OverlayLook.Original, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 8, 10, 2),
        };
        var panel = new StackPanel { Children = { _title, _rows, _footer } };
        Content = new Border
        {
            Width = PanelWidth,
            Background = OverlayLook.ToastBackground,
            BorderBrush = OverlayLook.Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 8, 6, 8),
            Child = panel,
        };
        MouseMove += (_, _) =>
        {
            if (_hoverArmed) return;
            var now = Forms.Cursor.Position;
            _hoverArmed = Math.Abs(now.X - _openedAt.X) > HoverAfter || Math.Abs(now.Y - _openedAt.Y) > HoverAfter;
        };
    }

    /// <summary>Where the menu is on the screen (physical pixels), for a click outside it.</summary>
    public DRect BoundsPx => Native.GetWindowRect(Handle, out var r) ? r.ToRectangle() : DRect.Empty;

    /// <summary>Shows the menu in a corner (or the middle) of <paramref name="areaPx"/>, the game window, kept on its monitor.</summary>
    public void ShowMenu(string title, IReadOnlyList<GameMenuItem> items, int selected, string footer, DRect areaPx,
        MenuPlacement placement = MenuPlacement.TopRight)
    {
        _title.Text = title;
        Update(items, selected, footer);
        double scale = ScaleAt(areaPx);
        var size = MeasureContent(scale);
        var at = GameMenu.Place(areaPx, size, Forms.Screen.FromRectangle(areaPx).Bounds, placement, (int)Math.Round(EdgeMargin * scale));
        _openedAt = Forms.Cursor.Position;
        _hoverArmed = false;
        _pressed = -1;
        ShowAt(at.X, at.Y);
    }

    /// <summary>The rows written again (a state changed, another row is selected), in place.</summary>
    public void Update(IReadOnlyList<GameMenuItem> items, int selected, string footer)
    {
        while (_rowViews.Count < items.Count) AddRow(_rowViews.Count);
        for (int i = 0; i < _rowViews.Count; i++)
        {
            var row = _rowViews[i];
            if (i >= items.Count)
            {
                row.Box.Visibility = Visibility.Collapsed;
                continue;
            }
            var item = items[i];
            row.Box.Visibility = Visibility.Visible;
            row.Box.Background = i == selected ? OverlayLook.MenuSelected : OverlayLook.Clear;
            row.Key.Text = item.Key;
            row.Label.Text = item.Text;
            row.Label.Foreground = item.Armed ? OverlayLook.MenuWarning : OverlayLook.Text;
            row.Value.Text = item.Value;
            row.Value.Foreground = item.On ? OverlayLook.MenuAccent : OverlayLook.Text;
            row.Gesture.Text = item.Gesture;
            row.Minus.Visibility = row.Plus.Visibility = item.Stepper ? Visibility.Visible : Visibility.Collapsed;
        }
        _footer.Text = footer;
        _footer.Visibility = footer.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void AddRow(int index)
    {
        var key = new TextBlock
        {
            FontFamily = OverlayLook.Font, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = OverlayLook.Text,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Background = OverlayLook.MenuBadge, Child = key, Margin = new Thickness(0, 0, 10, 0) };
        var label = new TextBlock { FontFamily = OverlayLook.Font, FontSize = 14.5, Foreground = OverlayLook.Text, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { FontFamily = OverlayLook.Font, FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        var minus = StepButton("−", index, -1);
        var plus = StepButton("+", index, +1);
        var valueBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { minus, value, plus } };
        var gesture = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Foreground = OverlayLook.Original,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0), MinWidth = 96, TextAlignment = TextAlignment.Right,
        };
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(valueBox, 2);
        Grid.SetColumn(gesture, 3);
        grid.Children.Add(badge);
        grid.Children.Add(label);
        grid.Children.Add(valueBox);
        grid.Children.Add(gesture);
        var box = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6), Background = OverlayLook.Clear, Child = grid };
        box.MouseEnter += (_, _) =>
        {
            if (_hoverArmed) Hovered?.Invoke(index);
        };
        box.MouseLeftButtonDown += (_, e) =>
        {
            _pressed = index;
            e.Handled = true;
        };
        box.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed == index) Picked?.Invoke(index, 0);
            _pressed = -1;
            e.Handled = true;
        };
        _rows.Children.Add(box);
        _rowViews.Add(new Row { Box = box, Key = key, Label = label, Value = value, Gesture = gesture, Minus = minus, Plus = plus });
    }

    /// <summary>The font row's − or +: a plain border, so a click takes no focus.</summary>
    Border StepButton(string text, int index, int step)
    {
        var button = new Border
        {
            Width = 24, Height = 22, CornerRadius = new CornerRadius(4), Background = OverlayLook.MenuButton,
            Child = new TextBlock
            {
                Text = text, FontFamily = OverlayLook.Font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = OverlayLook.Text,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0),
            },
        };
        bool pressed = false;
        button.MouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            e.Handled = true;
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            if (pressed) Picked?.Invoke(index, step);
            pressed = false;
            e.Handled = true;
        };
        return button;
    }

    /// <summary>Raises a click on a row as the mouse would (the tests).</summary>
    internal void ClickRow(int index, int step = 0) => Picked?.Invoke(index, step);
}
