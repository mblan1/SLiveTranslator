using System;
using System.Collections.Generic;
using System.Linq;
using SLiveTranslator.Translation;

namespace SLiveTranslator.UI;

/// <summary>A line of the session as the history lists it, with what a hand fix needs.</summary>
/// <param name="Source">The text as read from the screen: what the translation was made from and what a fix is stored for.</param>
/// <param name="Translation">Null while it is not there yet.</param>
/// <param name="Provider">The provider key of the translation ("user" after a fix); null while there is none.</param>
/// <param name="Src">The languages the line was translated between, which a fix is stored for; "" when not known.</param>
public sealed record SubtitleRecord(int Id, string Source, string? Translation, string? Provider, string Src = "", string Tgt = "");

/// <summary>
/// The dialog lines of the session and which of them the subtitle box shows. Plain state with no WPF, so the rules
/// are testable; <see cref="SubtitlePresenter"/> calls it on the UI thread and draws the result.
/// </summary>
internal sealed class SubtitleBoard
{
    const string RetryMark = " ↻";

    sealed class Entry
    {
        public required int Id { get; init; }
        public string Original { get; set; } = "";
        public string? Translated { get; set; }
        public string? Provider { get; set; }
        public string Src { get; set; } = "";
        public string Tgt { get; set; } = "";
        public bool Failed { get; set; }
        /// <summary>The current reading is unreliable.</summary>
        public bool LowConfidence { get; set; }
        /// <summary>The reading that <see cref="Translated"/> was made from is unreliable; it may be an earlier one.</summary>
        public bool TranslatedLowConfidence { get; set; }
    }

    readonly int _maxEntries;
    readonly List<Entry> _entries = new();
    int _windowStart; // entries before this index belong to an earlier conversation and stay out of the subtitle box

    public SubtitleBoard(int maxEntries = 300) => _maxEntries = maxEntries;

    public int Count => _entries.Count;

    /// <summary>A new line, or a shown line that grew. A grown line keeps its old translation until the new one arrives.</summary>
    public void Detected(int id, string original, bool lowConfidence, string src = "", string tgt = "")
    {
        var entry = Find(id);
        if (entry is null)
        {
            entry = new Entry { Id = id, Src = src, Tgt = tgt };
            _entries.Add(entry);
            Trim();
        }
        entry.Original = original;
        entry.LowConfidence = lowConfidence;
    }

    /// <summary>
    /// Stores the translation. Returns true when the line had already left the subtitle box but is among the last
    /// <paramref name="visibleLines"/>: the box takes it back, so a late translation is still seen. An empty
    /// translation (the line is not language) drops the line from the box and the history.
    /// </summary>
    /// <param name="lowConfidence">The reading that was translated is unreliable.</param>
    /// <param name="provider">The provider key the translation came from.</param>
    public bool Translated(int id, string translated, bool lowConfidence, int visibleLines, string? provider = null)
    {
        int index = _entries.FindLastIndex(e => e.Id == id);
        if (index < 0) return false;
        if (translated.Length == 0)
        {
            _entries.RemoveAt(index);
            if (index < _windowStart) _windowStart--;
            return false;
        }
        _entries[index].Translated = translated;
        _entries[index].Provider = provider;
        _entries[index].TranslatedLowConfidence = lowConfidence;
        _entries[index].Failed = false;
        if (index >= _windowStart || index < _entries.Count - visibleLines) return false;
        _windowStart = index;
        return true;
    }

    public void Failed(int id)
    {
        if (Find(id) is { } entry) entry.Failed = true;
    }

    /// <summary>The user fixed the translation of this source text: every line that shows it now shows the fix.</summary>
    /// <returns>Whether a line changed.</returns>
    public bool Fix(string source, string translation)
    {
        bool changed = false;
        foreach (var entry in _entries.Where(e => e.Original == source))
        {
            entry.Translated = translation;
            entry.Provider = ProviderKeys.User;
            entry.Failed = false;
            entry.TranslatedLowConfidence = false;
            changed = true;
        }
        return changed;
    }

    /// <summary>The latest lines with their source and provider, oldest first; for the history list and its hand fix.</summary>
    public IReadOnlyList<SubtitleRecord> Records(int lines) =>
        _entries.Skip(Math.Max(0, _entries.Count - lines)).Select(e => new SubtitleRecord(e.Id, e.Original, e.Translated, e.Provider, e.Src, e.Tgt)).ToList();

    /// <summary>The box was hidden: the lines so far stay in the history only.</summary>
    public void CloseWindow() => _windowStart = _entries.Count;

    /// <summary>The latest lines of the current conversation, oldest first.</summary>
    /// <param name="withOriginal">Each translated line carries the text it was made from (the overlay shows it under the translation).</param>
    public IReadOnlyList<SubtitleLineView> Visible(int lines, bool withOriginal = false)
    {
        int from = Math.Max(Math.Min(_windowStart, _entries.Count), _entries.Count - lines);
        return _entries.Skip(from).Select(e => View(e, withOriginal)).ToList();
    }

    public IReadOnlyList<HistoryLineView> History(int lines) =>
        _entries.Skip(Math.Max(0, _entries.Count - lines))
            .Select(e => e.Translated is null && e.Failed
                ? new HistoryLineView(e.Original + RetryMark, e.Original, false)
                : new HistoryLineView(e.Translated ?? "", e.Original, e.Translated is null))
            .ToList();

    /// <summary>
    /// A failed line shows its original, dimmed and marked; a line that grew keeps its old translation meanwhile,
    /// in italics while that translation came from an unreliable reading.
    /// </summary>
    static SubtitleLineView View(Entry e, bool withOriginal) =>
        e.Translated is { } translated ? new SubtitleLineView(translated, false, LowConfidence: e.TranslatedLowConfidence, Original: withOriginal ? e.Original : null)
        : e.Failed ? new SubtitleLineView(e.Original + RetryMark, false, Failed: true, LowConfidence: e.LowConfidence)
        : new SubtitleLineView("…", true, LowConfidence: e.LowConfidence);

    Entry? Find(int id)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (_entries[i].Id == id) return _entries[i];
        return null;
    }

    void Trim()
    {
        if (_entries.Count <= _maxEntries) return;
        int drop = _entries.Count - _maxEntries;
        _entries.RemoveRange(0, drop);
        _windowStart = Math.Max(0, _windowStart - drop);
    }
}
