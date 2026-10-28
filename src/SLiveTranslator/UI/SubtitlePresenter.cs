using System;
using System.Collections.Generic;
using System.Windows.Threading;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.UI.Overlay;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.UI;

/// <summary>
/// Keeps every dialog line of the session. The subtitle box shows the latest few, so a line that went by fast
/// is still readable; the history panel shows more. Entry points may be called from any thread.
/// </summary>
public sealed class SubtitlePresenter : ISubtitleSink
{
    static readonly TimeSpan Linger = TimeSpan.FromSeconds(6);

    readonly Dispatcher _ui;
    readonly OverlayHost _overlay;
    readonly Func<DRect> _region;
    readonly Func<OverlaySettings> _look;
    readonly Func<string> _historyHotkey;
    readonly Action<string> _onError;
    readonly Action<string> _onNotice;
    readonly Action<bool> _onPaused;
    readonly SubtitleBoard _board = new();
    readonly DispatcherTimer _linger;
    bool _live; // the game shows a subtitle right now

    /// <param name="look">The overlay settings in use: font size, lines, opacity, position, original text, history length.</param>
    /// <param name="linger">How long the last lines stay after the game hides them (tests shorten it).</param>
    public SubtitlePresenter(Dispatcher ui, OverlayHost overlay, Func<DRect> region, Func<OverlaySettings> look,
        Func<string> historyHotkey, Action<string> onError, Action<string> onNotice, Action<bool> onPaused, TimeSpan? linger = null)
    {
        _ui = ui;
        _overlay = overlay;
        _region = region;
        _look = look;
        _historyHotkey = historyHotkey;
        _onError = onError;
        _onNotice = onNotice;
        _onPaused = onPaused;
        _linger = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = linger ?? Linger };
        _linger.Tick += (_, _) =>
        {
            _linger.Stop();
            _board.CloseWindow();
            _overlay.HideSubtitle();
        };
    }

    public bool HistoryVisible { get; private set; }
    public int Count => _board.Count;

    int VisibleLines => Math.Clamp(_look().MaxLines, 1, 5);

    int HistoryLines => Math.Clamp(_look().HistoryLines, 1, 100);

    public void Start() => _ui.Invoke(() =>
    {
        _live = false;
        _board.CloseWindow();
    });

    public void Stop() => _ui.Invoke(() =>
    {
        _linger.Stop();
        _live = false;
        _board.CloseWindow();
        _overlay.HideSubtitle();
    });

    public bool ToggleHistory()
    {
        HistoryVisible = !HistoryVisible;
        if (HistoryVisible) RenderHistory();
        else _overlay.HideHistory();
        return HistoryVisible;
    }

    public void HideHistory()
    {
        HistoryVisible = false;
        _overlay.HideHistory();
    }

    /// <summary>The latest lines with their source and provider, for the history list (any thread).</summary>
    public IReadOnlyList<SubtitleRecord> Records(int lines) => _ui.Invoke(() => _board.Records(lines));

    /// <summary>
    /// Shows the user's fix on every line with this source text. The caller stores it in the cache first
    /// (<see cref="SLiveTranslator.Translation.CachedTranslator.SetUserOverride"/>), so the line keeps it when it comes again.
    /// </summary>
    public void ApplyFix(string source, string translation) => _ui.BeginInvoke(() =>
    {
        if (_board.Fix(source, translation)) Render();
    });

    /// <summary>Draws what is on screen again, in the look the overlay settings now ask for.</summary>
    public void Restyle() => _ui.BeginInvoke(() =>
    {
        if (_board.Visible(VisibleLines).Count > 0) Render();
        else if (HistoryVisible) RenderHistory();
    });

    // ---- ISubtitleSink ----

    public void LineDetected(int id, string original, bool lowConfidence) => _ui.BeginInvoke(() =>
    {
        _board.Detected(id, original, lowConfidence);
        _live = true;
        _linger.Stop();
        Render();
    });

    public void LineTranslated(int id, string translated, bool lowConfidence, string provider) => _ui.BeginInvoke(() =>
    {
        // A translation that arrives after the box was hidden brings the box back for another linger.
        if (_board.Translated(id, translated, lowConfidence, VisibleLines, provider) && !_live) RestartLinger();
        Render();
    });

    public void LineFailed(int id, string original, string error) => _ui.BeginInvoke(() =>
    {
        _board.Failed(id);
        Render();
        _onError(Loc.F("Notice.LineFailed", error));
    });

    public void Cleared() => _ui.BeginInvoke(() =>
    {
        _live = false;
        RestartLinger(); // keep the last lines readable for a few seconds after the game hides them
    });

    public void Paused(bool paused) => _ui.BeginInvoke(() => _onPaused(paused));

    public void Error(string message) => _ui.BeginInvoke(() => _onError(Loc.F("Notice.ReadFailed", message)));

    public void Notice(string message) => _ui.BeginInvoke(() => _onNotice(message));

    // ---- rendering ----

    void RestartLinger()
    {
        _linger.Stop();
        _linger.Start();
    }

    void Render()
    {
        var look = _look();
        _overlay.ShowSubtitleLines(_board.Visible(VisibleLines, look.ShowOriginal), _region(), look.FontSize, SubtitleStyle.From(look));
        if (HistoryVisible) RenderHistory();
    }

    void RenderHistory() =>
        _overlay.ShowHistory(Loc.F("History.Title", _board.Count, _historyHotkey()), _board.History(HistoryLines), _region());
}
