using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SLiveTranslator.Capture;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI;

public enum StatusKind { Info, On, Off, Warning, Error }

/// <param name="Pending">The translation is not there yet (the text is a placeholder).</param>
/// <param name="Failed">Not translated: the text is the original, shown dimmed.</param>
/// <param name="LowConfidence">OCR never read the line cleanly; shown in italics.</param>
public sealed record SubtitleLineView(string Text, bool Pending, bool Failed = false, bool LowConfidence = false);

public sealed record HistoryLineView(string Translated, string Original, bool Pending);

/// <summary>Transparent, click-through, always-on-top layer that draws translations and status over the game.</summary>
public partial class OverlayWindow : Window
{
    static readonly Brush PendingBrush = Frozen(Color.FromRgb(0xB0, 0xBE, 0xC5));
    static readonly Brush OriginalBrush = Frozen(Color.FromRgb(0x90, 0x9A, 0xA0));

    readonly DispatcherTimer _toastTimer = new();
    Forms.Screen? _screen;
    double _dpiScale = 1.0;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = Handle;
            long ex = Native.GetWindowLongPtrW(hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtrW(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            // Hide the overlay from screen capture so our own labels are never OCR'd (Windows 10 2004+).
            ExcludedFromCapture = Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public bool ExcludedFromCapture { get; private set; }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Stretches the overlay over the monitor that contains the given screen rectangle.</summary>
    public void CoverScreenOf(DRect areaPx)
    {
        _screen = Forms.Screen.FromRectangle(areaPx);
        if (!IsVisible) Show();
        var b = _screen.Bounds;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    /// <summary>Moves to the anchor's monitor only when nothing else is on screen, so subtitles never jump monitors.</summary>
    void EnsureScreen(DRect? anchorPx)
    {
        bool busy = SubtitleBox.IsVisible || LiveBadge.IsVisible || HistoryPanel.IsVisible || Labels.Children.Count > 0;
        if (_screen is null || (!busy && anchorPx is { } a && Forms.Screen.FromRectangle(a).DeviceName != _screen.DeviceName))
            CoverScreenOf(anchorPx ?? Forms.Screen.PrimaryScreen!.Bounds);
    }

    Rect ToDip(DRect px)
    {
        var origin = _screen?.Bounds.Location ?? System.Drawing.Point.Empty;
        return new Rect((px.Left - origin.X) / _dpiScale, (px.Top - origin.Y) / _dpiScale, px.Width / _dpiScale, px.Height / _dpiScale);
    }

    double ScreenWidthDip => (_screen?.Bounds.Width ?? 1920) / _dpiScale;
    double ScreenHeightDip => (_screen?.Bounds.Height ?? 1080) / _dpiScale;

    static Size Measure(FrameworkElement e, double maxWidth = double.PositiveInfinity)
    {
        e.Measure(new Size(maxWidth, double.PositiveInfinity));
        return e.DesiredSize;
    }

    // ---- subtitles ----

    /// <summary>Shows the latest dialog lines (oldest first, newest last and brightest) next to the watched region.</summary>
    public void ShowSubtitleLines(IReadOnlyList<SubtitleLineView> lines, DRect regionPx, int fontSize)
    {
        if (lines.Count == 0)
        {
            HideSubtitle();
            return;
        }
        CoverScreenOf(regionPx);
        var region = ToDip(regionPx);

        SubtitleLines.Children.Clear();
        // The latest finished translation stays bright even while a newer line is still "…".
        int lastReady = lines.Count - 1;
        while (lastReady > 0 && lines[lastReady].Pending) lastReady--;
        for (int i = 0; i < lines.Count; i++)
        {
            bool newest = i >= lastReady;
            SubtitleLines.Children.Add(new TextBlock
            {
                Text = lines[i].Text,
                FontSize = newest ? fontSize : Math.Max(10, fontSize * 0.82),
                Foreground = lines[i].Pending ? PendingBrush : lines[i].Failed ? OriginalBrush : Brushes.White,
                FontStyle = lines[i].Pending || lines[i].LowConfidence ? FontStyles.Italic : FontStyles.Normal,
                Opacity = newest ? 1.0 : 0.62,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0),
            });
        }

        SubtitleBox.MaxWidth = Math.Min(Math.Max(region.Width, 480), ScreenWidthDip - 16);
        SubtitleBox.Visibility = Visibility.Visible;
        var size = Measure(SubtitleBox, SubtitleBox.MaxWidth);

        double x = Math.Clamp(region.Left + (region.Width - size.Width) / 2, 0, Math.Max(0, ScreenWidthDip - size.Width));
        double below = region.Bottom + 6;
        double above = region.Top - size.Height - 6;
        double y = below + size.Height <= ScreenHeightDip ? below : Math.Max(0, above);
        Canvas.SetLeft(SubtitleBox, x);
        Canvas.SetTop(SubtitleBox, y);
    }

    public void HideSubtitle() => SubtitleBox.Visibility = Visibility.Collapsed;

    /// <summary>Small "subtitles on" marker at the corner of the watched region while subtitle mode runs.</summary>
    public void ShowLiveBadge(DRect regionPx, string text)
    {
        CoverScreenOf(regionPx);
        var region = ToDip(regionPx);
        LiveBadgeText.Text = text;
        LiveBadge.Visibility = Visibility.Visible;
        var size = Measure(LiveBadge);
        double y = region.Top - size.Height - 4;
        Canvas.SetLeft(LiveBadge, Math.Clamp(region.Left, 0, Math.Max(0, ScreenWidthDip - size.Width)));
        Canvas.SetTop(LiveBadge, y >= 0 ? y : region.Top + 4);
    }

    public void HideLiveBadge() => LiveBadge.Visibility = Visibility.Collapsed;

    // ---- history ----

    public bool HistoryVisible => HistoryPanel.IsVisible;

    /// <summary>Panel with the recent dialog (newest at the bottom) so lines that went by too fast can be re-read.</summary>
    public void ShowHistory(string title, IReadOnlyList<HistoryLineView> lines, DRect? anchorPx)
    {
        EnsureScreen(anchorPx);
        HistoryTitle.Text = title;
        HistoryLines.Children.Clear();
        if (lines.Count == 0)
            HistoryLines.Children.Add(new TextBlock { Text = "No dialog yet.", Foreground = OriginalBrush, FontSize = 15, FontFamily = new FontFamily("Segoe UI") });

        foreach (var line in lines)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            item.Children.Add(new TextBlock
            {
                Text = line.Pending ? "…" : line.Translated,
                Foreground = line.Pending ? PendingBrush : Brushes.White,
                FontSize = 16,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            item.Children.Add(new TextBlock
            {
                Text = line.Original,
                Foreground = OriginalBrush,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
            });
            HistoryLines.Children.Add(item);
        }

        HistoryPanel.Width = Math.Min(640, ScreenWidthDip - 48);
        HistoryPanel.MaxHeight = ScreenHeightDip - 160;
        HistoryPanel.Visibility = Visibility.Visible;
        Canvas.SetLeft(HistoryPanel, 24);
        Canvas.SetTop(HistoryPanel, 90);
    }

    public void HideHistory() => HistoryPanel.Visibility = Visibility.Collapsed;

    // ---- status toast ----

    /// <summary>Short message at the top of the game's monitor. Windows hides tray notifications in games, this does not.</summary>
    public void ShowStatus(string text, StatusKind kind, DRect? anchorPx = null, double seconds = 2.5)
    {
        EnsureScreen(anchorPx);
        ToastText.Text = text;
        Toast.BorderBrush = kind switch
        {
            StatusKind.On => Frozen(Color.FromRgb(0x4C, 0xAF, 0x50)),
            StatusKind.Off => Frozen(Color.FromRgb(0x9E, 0x9E, 0x9E)),
            StatusKind.Warning => Frozen(Color.FromRgb(0xFF, 0xB3, 0x00)),
            StatusKind.Error => Frozen(Color.FromRgb(0xF4, 0x43, 0x36)),
            _ => Frozen(Color.FromRgb(0x21, 0x96, 0xF3)),
        };
        Toast.Visibility = Visibility.Visible;
        var size = Measure(Toast);
        Canvas.SetLeft(Toast, Math.Max(0, (ScreenWidthDip - size.Width) / 2));
        Canvas.SetTop(Toast, 28);

        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    // ---- translate-screen labels ----

    /// <summary>Draws one label per recognized paragraph, on top of the original text.</summary>
    public void ShowLabels(IEnumerable<(DRect BoundsPx, string Text, int LineCount)> items, DRect areaPx, int baseFontSize)
    {
        Labels.Children.Clear();
        CoverScreenOf(areaPx);
        var background = Frozen(Color.FromArgb(0xD8, 0x10, 0x14, 0x18));
        var outline = Frozen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));

        foreach (var (boundsPx, text, lineCount) in items)
        {
            var r = ToDip(boundsPx);
            double lineHeight = r.Height / Math.Max(1, lineCount);
            double fontSize = Math.Clamp(lineHeight * 0.72, 10, Math.Max(12, baseFontSize * 1.6));

            var border = new Border
            {
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    FontSize = fontSize,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = Math.Max(r.Width + 24, 240),
                },
                Background = background,
                BorderBrush = outline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
            };
            var size = Measure(border);
            Canvas.SetLeft(border, Math.Clamp(r.Left - 6, 0, Math.Max(0, ScreenWidthDip - size.Width)));
            Canvas.SetTop(border, Math.Clamp(r.Top - 2, 0, Math.Max(0, ScreenHeightDip - size.Height)));
            Labels.Children.Add(border);
        }
    }

    public void ClearLabels() => Labels.Children.Clear();
}
