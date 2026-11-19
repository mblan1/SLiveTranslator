using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SLiveTranslator.Models;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI.Overlay;

/// <summary>
/// The translated dialog lines next to the subtitle region (or where the display region puts them). Its TextBlocks are
/// made once and reused. It fades in and out, and a thin bar under the lines can show how long they stay.
/// </summary>
public sealed class SubtitleOverlay : ClickThroughWindow
{
    /// <summary>The animations run at a low frame rate: the window is layered, and each frame redraws all of it.</summary>
    const int FadeFrameRate = 30;

    const int CountdownFrameRate = 12;

    readonly StackPanel _lines = new();
    readonly Border _textHost;
    readonly Border _box;
    readonly Grid _bar;
    readonly ScaleTransform _barScale = new(1, 1);
    DRect _bounds;
    bool _fadingOut;

    public SubtitleOverlay()
    {
        Title = "SLiveTranslator subtitles";
        _textHost = new Border { Child = _lines };
        var fill = new Border
        {
            Background = OverlayLook.TimeLeftFill,
            CornerRadius = new CornerRadius(1.5),
            RenderTransform = _barScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        _bar = new Grid
        {
            Height = 3,
            Margin = new Thickness(24, 6, 24, 0),
            Visibility = Visibility.Collapsed,
        };
        _bar.Children.Add(new Border { Background = OverlayLook.TimeLeftTrack, CornerRadius = new CornerRadius(1.5) });
        _bar.Children.Add(fill);
        var panel = new StackPanel();
        panel.Children.Add(_textHost);
        panel.Children.Add(_bar);
        _box = new Border
        {
            Background = OverlayLook.SubtitleBackground,
            BorderBrush = OverlayLook.Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            Child = panel,
        };
        Content = _box;
    }

    /// <summary>Where the lines are (physical pixels); null while they are not shown.</summary>
    public DRect? BoundsPx => Showing ? _bounds : null;

    /// <summary>The time-left bar is running.</summary>
    public bool CountingDown { get; private set; }

    /// <summary>
    /// Shows the latest dialog lines (oldest first, newest last and brightest) where <paramref name="style"/> puts them
    /// (see <see cref="SubtitlePlacement.Place"/>). A box that was hidden fades in.
    /// </summary>
    public void ShowLines(IReadOnlyList<SubtitleLineView> lines, DRect regionPx, int fontSize, SubtitleStyle? style = null)
    {
        style ??= SubtitleStyle.Default;
        bool appearing = !Showing;
        while (_lines.Children.Count < lines.Count)
        {
            _lines.Children.Add(new TextBlock
            {
                FontFamily = OverlayLook.Font,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // The latest finished translation stays bright even while a newer line is still "…".
        int lastReady = lines.Count - 1;
        while (lastReady > 0 && lines[lastReady].Pending) lastReady--;
        var alignment = style.Align switch
        {
            TextAlign.Left => TextAlignment.Left,
            TextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Center,
        };
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
            block.TextAlignment = alignment;
            block.FontSize = newest ? fontSize : Math.Max(10, fontSize * 0.82);
            ShowText(block, line, style.ShowOriginal);
            block.Foreground = line.Pending ? OverlayLook.Pending : line.Failed ? OverlayLook.Original : OverlayLook.Text;
            block.FontStyle = line.Pending || line.LowConfidence ? FontStyles.Italic : FontStyles.Normal;
            block.Opacity = (newest ? 1.0 : 0.62) * style.TextOpacity;
            block.Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0);
        }
        bool boxed = BackgroundColors.Normalize(style.BackgroundColor).Length > 0;
        _box.Background = OverlayLook.SubtitleBackgroundAt(style.BackgroundColor, style.BackgroundOpacity);
        _box.BorderThickness = new Thickness(boxed ? 1 : 0);
        _lines.Effect = style.TextOutline ? OverlayLook.OutlineHalo : null;
        _textHost.Effect = style.TextOutline ? OverlayLook.OutlineDrop : null;
        if (!CountingDown) _bar.Visibility = style.TimeLeftBar ? Visibility.Hidden : Visibility.Collapsed;

        var screen = Forms.Screen.FromRectangle(style.Position == SubtitlePosition.Custom && style.DisplayRegion is { } custom ? custom : regionPx).Bounds;
        double scale = ScaleAt(regionPx);
        _box.MaxWidth = SubtitlePlacement.MaxWidthPx(style, regionPx, screen, scale) / scale;
        var size = MeasureContent(scale);
        _bounds = SubtitlePlacement.Place(style, regionPx, size, screen, (int)Math.Round(6 * scale));

        // A fade-in already running goes on; one that fades out comes back from where it is.
        if (appearing || _fadingOut)
        {
            double from = appearing ? 0 : _box.Opacity;
            _fadingOut = false;
            if (style.FadeIn > TimeSpan.Zero) Animate(from, 1, style.FadeIn, then: null);
            else StopFade();
        }
        ShowAt(_bounds.X, _bounds.Y);
    }

    /// <summary>Hides the lines, fading them out over <paramref name="fade"/> (at once for zero).</summary>
    public void FadeOut(TimeSpan fade)
    {
        StopCountdown();
        if (!Showing || fade <= TimeSpan.Zero || !IsVisible)
        {
            StopFade();
            Dismiss();
            return;
        }
        if (_fadingOut) return;
        _fadingOut = true;
        Animate(_box.Opacity, 0, fade, then: () =>
        {
            if (!_fadingOut) return; // lines came back meanwhile
            _fadingOut = false;
            Dismiss();
            _box.BeginAnimation(OpacityProperty, null);
            _box.Opacity = 1;
        });
    }

    /// <summary>Starts the bar under the lines, which empties over <paramref name="duration"/>.</summary>
    public void StartCountdown(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        CountingDown = true;
        _bar.Visibility = Visibility.Visible;
        var shrink = new DoubleAnimation(1, 0, duration);
        Timeline.SetDesiredFrameRate(shrink, CountdownFrameRate);
        _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
    }

    /// <summary>Stops the bar; its room stays when the look keeps it (see <see cref="SubtitleStyle.TimeLeftBar"/>).</summary>
    public void StopCountdown()
    {
        if (!CountingDown) return;
        CountingDown = false;
        _barScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _barScale.ScaleX = 1;
        _bar.Visibility = Visibility.Hidden;
    }

    void Animate(double from, double to, TimeSpan duration, Action? then)
    {
        var fade = new DoubleAnimation(from, to, duration) { FillBehavior = FillBehavior.HoldEnd };
        Timeline.SetDesiredFrameRate(fade, FadeFrameRate);
        if (then is not null) fade.Completed += (_, _) => then();
        _box.BeginAnimation(OpacityProperty, fade);
    }

    void StopFade()
    {
        _fadingOut = false;
        _box.BeginAnimation(OpacityProperty, null);
        _box.Opacity = 1;
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
