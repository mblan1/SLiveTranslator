using System;
using System.Collections.Generic;
using System.Linq;
using SLiveTranslator.Models;
using DRect = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI.Overlay;

/// <summary>
/// What the app draws over the game: one small click-through window per element (subtitles, live badge, toast,
/// history, translate-screen labels), each created when first needed. Call on the UI thread.
/// </summary>
public sealed class OverlayHost
{
    SubtitleOverlay? _subtitle;
    BadgeOverlay? _badge;
    ToastOverlay? _toast;
    HistoryOverlay? _history;
    LabelsOverlay? _labels;
    Forms.Screen? _screen; // the monitor in use, for the toast and the history
    bool _hidden;

    /// <summary>The overlay windows created so far.</summary>
    internal IEnumerable<(string Name, ClickThroughWindow Window)> Windows
    {
        get
        {
            if (_subtitle is not null) yield return ("subtitles", _subtitle);
            if (_badge is not null) yield return ("badge", _badge);
            if (_toast is not null) yield return ("toast", _toast);
            if (_history is not null) yield return ("history", _history);
            if (_labels is not null) yield return ("labels", _labels);
        }
    }

    /// <summary>Screen capture does not see any of the overlay windows, so they need not be hidden for translate-screen.</summary>
    public bool ExcludedFromCapture => Windows.All(w => w.Window.ExcludedFromCapture);

    SharedWindows _shared = SharedWindows.None;

    /// <summary>
    /// Which windows screen capture sees (Shared on screen); the others stay hidden from it. Applies to the windows
    /// made so far and to each one made later.
    /// </summary>
    public SharedWindows Shared
    {
        get => _shared;
        set
        {
            _shared = value;
            foreach (var (_, window) in Windows) Share(window);
        }
    }

    T Made<T>(T window) where T : ClickThroughWindow => Share(window);

    T Share<T>(T window) where T : ClickThroughWindow
    {
        window.Shared = window switch
        {
            SubtitleOverlay => _shared.Subtitles,
            HistoryOverlay => _shared.History,
            LabelsOverlay => _shared.Labels,
            _ => _shared.Notices,
        };
        return window;
    }

    // ---- subtitles ----

    /// <summary>Shows the latest dialog lines (oldest first, newest last and brightest) next to the watched region.</summary>
    /// <param name="style">The look the overlay settings ask for; null for the default one.</param>
    public void ShowSubtitleLines(IReadOnlyList<SubtitleLineView> lines, DRect regionPx, int fontSize, SubtitleStyle? style = null)
    {
        if (lines.Count == 0)
        {
            HideSubtitle();
            return;
        }
        Restore();
        _screen = Forms.Screen.FromRectangle(regionPx);
        (_subtitle ??= Made(new SubtitleOverlay())).ShowLines(lines, regionPx, fontSize, style);
    }

    /// <summary>Hides the lines, fading them out over <paramref name="fade"/> (at once by default).</summary>
    public void HideSubtitle(TimeSpan fade = default) => _subtitle?.FadeOut(fade);

    /// <summary>The bar under the lines empties over <paramref name="duration"/>, until they go.</summary>
    public void StartSubtitleCountdown(TimeSpan duration) => _subtitle?.StartCountdown(duration);

    public void StopSubtitleCountdown() => _subtitle?.StopCountdown();

    public void ShowLiveBadge(DRect regionPx, string text)
    {
        Restore();
        _screen = Forms.Screen.FromRectangle(regionPx);
        (_badge ??= Made(new BadgeOverlay())).ShowText(text, regionPx);
    }

    public void HideLiveBadge() => _badge?.Dismiss();

    // ---- history ----

    public bool HistoryVisible => _history?.Showing == true;

    public void ShowHistory(string title, IReadOnlyList<HistoryLineView> lines, DRect? anchorPx)
    {
        Restore();
        (_history ??= Made(new HistoryOverlay())).ShowLines(title, lines, ScreenFor(anchorPx).Bounds);
    }

    public void HideHistory() => _history?.Dismiss();

    // ---- status toast ----

    /// <summary>Short message over the game. Windows hides tray notifications in games, this does not.</summary>
    /// <param name="anchorPx">Picks the monitor (the game's); null keeps the one in use.</param>
    /// <param name="regionPx">The subtitle region, for <see cref="NoticePosition.NearRegion"/>; the toast keeps clear of the translated lines next to it.</param>
    public void ShowStatus(string text, StatusKind kind, DRect? anchorPx = null, double seconds = 2.5,
        NoticePosition position = NoticePosition.TopCenter, DRect? regionPx = null)
    {
        Restore();
        (_toast ??= Made(new ToastOverlay())).ShowMessage(text, kind, ScreenFor(anchorPx).Bounds, seconds, position, regionPx, _subtitle?.BoundsPx);
    }

    /// <summary>Takes the toast away before its time.</summary>
    public void HideStatus() => _toast?.HideMessage();

    // ---- translate-screen labels ----

    /// <summary>Draws one label per recognized paragraph, on top of the original text.</summary>
    public void ShowLabels(IEnumerable<(DRect BoundsPx, string Text, int LineCount)> items, DRect areaPx, int baseFontSize)
    {
        Restore();
        _screen = Forms.Screen.FromRectangle(areaPx);
        (_labels ??= Made(new LabelsOverlay())).ShowLabels(items, _screen.Bounds, baseFontSize);
    }

    /// <summary>Removes the labels and closes their monitor-sized window.</summary>
    public void ClearLabels()
    {
        _labels?.Close();
        _labels = null;
    }

    // ---- all windows ----

    /// <summary>
    /// Hides everything until the next Show call (for a capture that would see the overlay). That call brings back what
    /// is still meant to be shown: not what hid itself meanwhile, such as an expired toast or the badge of subtitles
    /// turned off.
    /// </summary>
    public void Hide()
    {
        _hidden = true;
        foreach (var (_, window) in Windows) window.Hide();
    }

    void Restore()
    {
        if (!_hidden) return;
        _hidden = false;
        foreach (var (_, window) in Windows)
            if (window.Showing && !window.IsVisible) window.Show();
    }

    public void Close()
    {
        foreach (var (_, window) in Windows.ToList()) window.Close();
        (_subtitle, _badge, _toast, _history, _labels, _hidden) = (null, null, null, null, null, false);
    }

    /// <summary>Moves to the anchor's monitor only when nothing else is on screen, so the overlay never jumps away from the subtitles.</summary>
    Forms.Screen ScreenFor(DRect? anchorPx)
    {
        bool busy = _subtitle?.Showing == true || _badge?.Showing == true || _history?.Showing == true || _labels is not null;
        if (_screen is null || (!busy && anchorPx is { } a && Forms.Screen.FromRectangle(a).DeviceName != _screen.DeviceName))
            _screen = Forms.Screen.FromRectangle(anchorPx ?? Forms.Screen.PrimaryScreen!.Bounds);
        return _screen;
    }
}
