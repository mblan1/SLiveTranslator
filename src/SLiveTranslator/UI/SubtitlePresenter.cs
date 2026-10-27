using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using SLiveTranslator.Services;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.UI;

/// <summary>
/// Keeps every dialog line of the session. The subtitle box shows the latest few, so a line that went by fast
/// is still readable; the history panel shows more. Entry points may be called from any thread.
/// </summary>
public sealed class SubtitlePresenter : ISubtitleSink
{
    const int VisibleLines = 3;
    const int HistoryLines = 12;
    const int MaxEntries = 300;
    static readonly TimeSpan Linger = TimeSpan.FromSeconds(6);

    sealed class Entry
    {
        public required int Id { get; init; }
        public string Original { get; set; } = "";
        public string? Translated { get; set; }
    }

    readonly Dispatcher _ui;
    readonly OverlayWindow _overlay;
    readonly Func<DRect> _region;
    readonly Func<int> _fontSize;
    readonly Func<string> _historyHotkey;
    readonly Action<string> _onError;
    readonly Action<string> _onNotice;
    readonly List<Entry> _entries = new();
    readonly DispatcherTimer _linger;
    int _windowStart; // entries before this index belong to an earlier conversation and stay out of the subtitle box

    public SubtitlePresenter(Dispatcher ui, OverlayWindow overlay, Func<DRect> region, Func<int> fontSize, Func<string> historyHotkey,
        Action<string> onError, Action<string> onNotice)
    {
        _ui = ui;
        _overlay = overlay;
        _region = region;
        _fontSize = fontSize;
        _historyHotkey = historyHotkey;
        _onError = onError;
        _onNotice = onNotice;
        _linger = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = Linger };
        _linger.Tick += (_, _) =>
        {
            _linger.Stop();
            _windowStart = _entries.Count;
            _overlay.HideSubtitle();
        };
    }

    public bool HistoryVisible { get; private set; }
    public int Count => _entries.Count;

    public void Start() => _ui.Invoke(() => _windowStart = _entries.Count);

    public void Stop() => _ui.Invoke(() =>
    {
        _linger.Stop();
        _windowStart = _entries.Count;
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

    // ---- ISubtitleSink ----

    public void LineDetected(int id, string original) => _ui.BeginInvoke(() =>
    {
        var entry = Find(id);
        if (entry is null)
        {
            entry = new Entry { Id = id };
            _entries.Add(entry);
            Trim();
        }
        entry.Original = original;
        entry.Translated = null;
        _linger.Stop();
        Render();
    });

    public void LineTranslated(int id, string translated) => _ui.BeginInvoke(() =>
    {
        if (Find(id) is not { } entry) return;
        entry.Translated = translated;
        Render();
    });

    public void LineFailed(int id, string original, string error) => _ui.BeginInvoke(() =>
    {
        if (Find(id) is { } entry) entry.Translated = original; // show the original rather than "…" forever
        Render();
        _onError(error);
    });

    public void Cleared() => _ui.BeginInvoke(() =>
    {
        _linger.Stop();
        _linger.Start(); // keep the last lines readable for a few seconds after the game hides them
    });

    public void Error(string message) => _ui.BeginInvoke(() => _onError(message));

    public void Notice(string message) => _ui.BeginInvoke(() => _onNotice(message));

    // ---- rendering ----

    Entry? Find(int id)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (_entries[i].Id == id) return _entries[i];
        return null;
    }

    void Trim()
    {
        if (_entries.Count <= MaxEntries) return;
        int drop = _entries.Count - MaxEntries;
        _entries.RemoveRange(0, drop);
        _windowStart = Math.Max(0, _windowStart - drop);
    }

    void Render()
    {
        int from = Math.Max(Math.Min(_windowStart, _entries.Count), _entries.Count - VisibleLines);
        var visible = _entries.Skip(from).Select(e => new SubtitleLineView(e.Translated ?? "…", e.Translated is null)).ToList();
        _overlay.ShowSubtitleLines(visible, _region(), _fontSize());
        if (HistoryVisible) RenderHistory();
    }

    void RenderHistory()
    {
        var lines = _entries.Skip(Math.Max(0, _entries.Count - HistoryLines))
            .Select(e => new HistoryLineView(e.Translated ?? "", e.Original, e.Translated is null))
            .ToList();
        _overlay.ShowHistory($"Dialog history ({_entries.Count} lines)  ·  {_historyHotkey()} to close", lines, _region());
    }
}
