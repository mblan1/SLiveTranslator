using System;
using System.Collections.Generic;
using System.Linq;
using SLiveTranslator.Translation;

namespace SLiveTranslator.Services;

/// <summary>
/// The latest lines of the conversation with the translations shown for them, sent along with each new line so a
/// translator that can use it keeps who speaks to whom and the pronouns consistent. A minute without a line ends the
/// conversation, and so does selecting another region. Thread-safe.
/// </summary>
internal sealed class ContextWindow
{
    public const int MaxLines = 10;
    public const long SilenceMs = 60_000;

    readonly object _gate = new();
    readonly SortedList<int, ContextLine> _lines = new();
    long? _lastLineAt;
    int _firstId = int.MinValue; // lines before this one belong to an earlier conversation
    bool _restart;

    /// <summary>A line was read at this time: after a long silence or a <see cref="Clear"/> the conversation starts again with it.</summary>
    public void LineSeen(int id, long nowMs)
    {
        lock (_gate)
        {
            if (_restart || (_lastLineAt is { } last && nowMs - last > SilenceMs))
            {
                _lines.Clear();
                _firstId = id;
                _restart = false;
            }
            _lastLineAt = nowMs;
        }
    }

    /// <summary>The line was shown with this translation; a newer translation of the same line replaces it.</summary>
    public void Shown(int id, string source, string translation)
    {
        lock (_gate)
        {
            if (id < _firstId) return; // translated after its conversation ended
            _lines[id] = new ContextLine(source, translation);
            while (_lines.Count > MaxLines) _lines.RemoveAt(0);
        }
    }

    /// <summary>Up to <paramref name="count"/> lines shown before this one, oldest first.</summary>
    public IReadOnlyList<ContextLine> Before(int id, int count)
    {
        if (count <= 0) return Array.Empty<ContextLine>();
        lock (_gate)
        {
            var before = _lines.Where(l => l.Key < id).Select(l => l.Value).ToList();
            return before.Count <= count ? before : before.GetRange(before.Count - count, count);
        }
    }

    /// <summary>Ends the conversation; a translation of one of its lines that arrives later is not kept.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _lastLineAt = null;
            _firstId = int.MaxValue;
            _restart = true;
        }
    }
}
