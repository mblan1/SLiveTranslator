using System;
using System.Collections.Generic;
using System.Linq;

namespace SLiveTranslator.Services;

/// <summary>
/// Turns the OCR text of successive frames into dialog-line events.
/// A reading counts once two frames in a row agree. A line already shown in the last few seconds is never
/// reported again, however differently OCR misreads it; text that keeps growing (typewriter reveal) updates
/// the same line instead.
/// </summary>
public sealed class SubtitleTracker
{
    public abstract record Event;
    public sealed record NewLine(int Id, string Text) : Event;
    public sealed record LineUpdated(int Id, string Text) : Event;
    public sealed record Cleared : Event;

    const int EmptyFramesToClear = 3;
    const long RecentWindowMs = 12_000;

    sealed class Shown
    {
        public required int Id { get; init; }
        public required string Text { get; set; }
        public long SeenAt { get; set; }
        public bool Upgraded { get; set; }
    }

    readonly List<Shown> _recent = new();
    string _candidate = "";
    int _stable, _empty, _lastId;
    bool _showing;

    /// <summary>True when a changed reading waits for its confirming frame; the caller should look again soon.</summary>
    public bool AwaitingConfirmation => _stable == 1 && _candidate.Length > 0;

    public Event? Observe(string text, long nowMs)
    {
        _recent.RemoveAll(r => nowMs - r.SeenAt > RecentWindowMs);

        if (text.Length == 0)
        {
            _candidate = "";
            _stable = 0;
            if (++_empty >= EmptyFramesToClear && _showing)
            {
                _showing = false;
                return new Cleared();
            }
            return null;
        }

        _empty = 0;
        string previous = _candidate;
        _stable = previous.Length > 0 && IsSameReading(previous, text) ? _stable + 1 : 1;
        _candidate = text;
        if (_stable < 2) return null;

        // Of the two agreeing readings keep the cleaner one; a garbled reading is never shown, a cleaner one will follow.
        string best = TextUtil.ReadingScore(previous) > TextUtil.ReadingScore(text) ? previous : text;
        if (!LooksReadable(best)) return null;

        var last = _recent.Count > 0 ? _recent[^1] : null;
        if (last is not null && _showing && IsContinuation(last.Text, best))
        {
            last.Text = best;
            last.SeenAt = nowMs;
            return new LineUpdated(last.Id, best);
        }

        var same = _recent.LastOrDefault(r => TextUtil.SameLine(r.Text, best));
        if (same is not null)
        {
            same.SeenAt = nowMs;
            _showing = true;
            // A first reading that was badly garbled gets one replacement by a clearly cleaner one.
            int shownScore = TextUtil.ReadingScore(same.Text), bestScore = TextUtil.ReadingScore(best);
            if (!same.Upgraded && same == last && bestScore >= shownScore + Math.Max(6, Math.Abs(shownScore) / 4))
            {
                same.Upgraded = true;
                same.Text = best;
                return new LineUpdated(same.Id, best);
            }
            return null;
        }

        _showing = true;
        var shown = new Shown { Id = ++_lastId, Text = best, SeenAt = nowMs };
        _recent.Add(shown);
        return new NewLine(shown.Id, best);
    }

    /// <summary>At least half of the letters are in plausible words. Clean lines score about their letter count.</summary>
    internal static bool LooksReadable(string text) =>
        TextUtil.ReadingScore(text) >= Math.Max(2, TextUtil.Compact(text).Length / 2);

    /// <summary>Two readings of the same still text: the same line and not growing.</summary>
    static bool IsSameReading(string a, string b)
    {
        int la = TextUtil.Compact(a).Length, lb = TextUtil.Compact(b).Length;
        return Math.Abs(la - lb) <= Math.Max(3, Math.Max(la, lb) * 0.12) && TextUtil.SameLine(a, b);
    }

    /// <summary>The shown line is still on screen and the text grew around it (typewriter reveal, second line appearing).</summary>
    static bool IsContinuation(string shown, string now)
    {
        string s = TextUtil.Compact(shown), n = TextUtil.Compact(now);
        if (n.Length <= s.Length * 1.15 + 3) return false;
        return s.Length < 8 ? n.StartsWith(s, StringComparison.Ordinal) : TextUtil.Coverage(s, n) >= 0.8;
    }
}
