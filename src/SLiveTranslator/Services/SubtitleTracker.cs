using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SLiveTranslator.Services;

/// <summary>
/// Turns the OCR text of successive frames into dialog-line events.
/// A reading counts once it agrees with one of the last few readings, and the best reading of that run is shown.
/// A line already shown in the last few seconds is never reported again, however differently OCR misreads it;
/// text that grows while the line is on screen (typewriter reveal, a word read late) updates the same line instead.
/// </summary>
public sealed class SubtitleTracker
{
    public abstract record Event;

    /// <param name="Text">The best reading so far.</param>
    public sealed record NewLine(int Id, string Text) : Event
    {
        /// <summary>The reading never looked clean, but it stayed on screen; shown so the line is not lost.</summary>
        public bool LowConfidence { get; init; }
    }

    public sealed record LineUpdated(int Id, string Text) : Event
    {
        /// <inheritdoc cref="NewLine.LowConfidence"/>
        public bool LowConfidence { get; init; }
    }

    public sealed record Cleared : Event;

    const int EmptyFramesToClear = 3;
    const int ReadingsToCompare = 3;
    const int ForceShowFrames = 4;
    /// <summary>Fresh readings of a shown line that may still settle which of its readings is right.</summary>
    const int SettleReadings = 4;
    const int MaxTextsPerLine = 8;
    const int MaxVotes = 24;
    const long RecentWindowMs = 12_000;
    const long AfterClearWindowMs = 2_500;

    sealed class Shown
    {
        public required int Id { get; init; }
        public required string Text { get; set; }
        /// <summary>Every text this line was shown with, so an older reading of it is still recognised.</summary>
        public List<string> Texts { get; } = new();
        public long SeenAt { get; set; }
        public int MaxLetters { get; set; }
        public bool LowConfidence { get; set; }
        /// <summary>The shown text was read exactly alike twice. Until then a repeated reading may correct it.</summary>
        public bool Confirmed { get; set; }
        /// <summary>Fresh readings since the line was shown with its text.</summary>
        public int Readings { get; set; }
    }

    /// <summary>A distinct recent reading and how often OCR produced it.</summary>
    sealed class Vote(string text)
    {
        public string Text { get; } = text;
        public int Count { get; set; }
    }

    readonly List<Shown> _recent = new();
    readonly List<string> _readings = new();
    readonly List<Vote> _votes = new(); // cleared when the subtitle disappears
    int _stable, _empty, _lastId;
    long _clearedAt = long.MinValue;
    bool _showing;

    /// <summary>True when a changed reading waits for its confirming frame; the caller should look again soon.</summary>
    public bool AwaitingConfirmation => _stable == 1;

    /// <summary>
    /// Another independent reading would help: a changed reading waits for its confirming frame, or the line on screen
    /// was not read exactly alike twice yet, so which of its readings is right is still open. Only its first few
    /// readings count: readings that kept disagreeing (a bright scene behind the text) rarely settle with more.
    /// </summary>
    public bool Unsettled => _stable == 1 || (_showing && _recent.Count > 0 && _recent[^1] is { Confirmed: false, Readings: < SettleReadings });

    /// <param name="fresh">
    /// False when the frame looked unchanged and the caller passed the last OCR result again. Such a frame still
    /// shows the reading is on screen (it confirms it and counts toward force-show), but it is not another
    /// independent read, so it does not vote for which reading is the right one.
    /// </param>
    public Event? Observe(string text, long nowMs, bool fresh = true)
    {
        // A line that left the screen may come back quickly (OCR missed a frame or two) but is a genuine repeat after a while.
        _recent.RemoveAll(r => nowMs - r.SeenAt > (r.SeenAt <= _clearedAt ? AfterClearWindowMs : RecentWindowMs));

        if (text.Length == 0)
        {
            _readings.Clear();
            _votes.Clear();
            _stable = 0;
            if (++_empty >= EmptyFramesToClear && _showing)
            {
                _showing = false;
                _clearedAt = nowMs;
                return new Cleared();
            }
            return null;
        }

        _empty = 0;
        if (fresh && _showing && _recent.Count > 0) _recent[^1].Readings++;
        // Readings may alternate (the second line read on every other frame), so any of the last few can confirm.
        _stable = _readings.Any(r => IsSameReading(r, text)) ? _stable + 1 : 1;
        var vote = _votes.Find(v => v.Text == text);
        if (vote is null)
        {
            _votes.Add(vote = new Vote(text));
            if (_votes.Count > MaxVotes) _votes.RemoveAt(0);
        }
        if (fresh) vote.Count++;
        _readings.Add(text);
        if (_readings.Count > ReadingsToCompare) _readings.RemoveAt(0);

        return _stable < 2 ? null : Report(RunBest(text), nowMs);
    }

    /// <summary>
    /// The subtitles are no longer watched (the game went to the background): a shown line counts as gone. The game
    /// itself may have paused with the line still on screen, so the line is not reported again when it is still there
    /// on return: it keeps the dedupe window of a line on screen, counted from leaving, not the short one after a clear.
    /// </summary>
    public Event? Leave(long nowMs)
    {
        _readings.Clear();
        _votes.Clear();
        _stable = 0;
        _empty = EmptyFramesToClear;
        if (!_showing) return null;
        _showing = false;
        if (_recent.MaxBy(r => r.SeenAt) is { } onScreen) onScreen.SeenAt = nowMs;
        return new Cleared();
    }

    /// <summary>
    /// The best of the recent readings of the same text. The text on screen reads the same frame after frame while
    /// OCR noise rarely repeats, so a reading OCR produced twice beats a one-off however clean the one-off looks.
    /// </summary>
    string RunBest(string text)
    {
        var same = _votes.Where(v => v.Text == text || IsSameReading(v.Text, text)).ToList();
        var repeated = same.Where(v => v.Count >= 2).ToList();
        return (repeated.Count > 0 ? repeated : same).Select(v => v.Text).Aggregate((a, b) => TextUtil.PickReading(a, b));
    }

    bool IsRepeated(string text) => _votes.Find(v => v.Text == text) is { Count: >= 2 };

    Event? Report(string best, long nowMs)
    {
        // A garbled reading waits for a cleaner one; if none comes, a reading that looks like words is shown anyway.
        bool lowConfidence = !LooksReadable(best);
        if (lowConfidence && (_stable < ForceShowFrames || !MayForceShow(best))) return null;

        bool repeated = IsRepeated(best);
        var last = _recent.Count > 0 ? _recent[^1] : null;
        if (last is not null && _showing)
        {
            if (IsRepeated(last.Text)) last.Confirmed = true;
            if (Replaces(last, best, lowConfidence, repeated))
            {
                Show(last, best, lowConfidence, repeated, nowMs);
                return new LineUpdated(last.Id, best) { LowConfidence = lowConfidence };
            }
            // One line of a two-line subtitle read alone, after both were shown: the same subtitle, not a new line.
            if (IsPartOf(best, last.Text))
            {
                last.SeenAt = nowMs;
                return null;
            }
        }

        var same = _recent.LastOrDefault(r => r.Texts.Any(t => TextUtil.SameLine(t, best)));
        if (same is not null)
        {
            same.SeenAt = nowMs;
            _showing = true;
            return null;
        }

        _showing = true;
        var shown = new Shown { Id = ++_lastId, Text = best };
        Show(shown, best, lowConfidence, repeated, nowMs);
        _recent.Add(shown);
        return new NewLine(shown.Id, best) { LowConfidence = lowConfidence };
    }

    static void Show(Shown line, string text, bool lowConfidence, bool confirmed, long nowMs)
    {
        line.Confirmed = confirmed;
        line.Readings = 0;
        line.Text = text;
        line.Texts.Add(text);
        if (line.Texts.Count > MaxTextsPerLine) line.Texts.RemoveAt(0);
        line.SeenAt = nowMs;
        line.MaxLetters = Math.Max(line.MaxLetters, Letters(text));
        line.LowConfidence = lowConfidence;
    }

    /// <summary>
    /// The reading is a better version of the last shown line, which is still on screen: the text grew
    /// (typewriter reveal, a word OCR missed before), a clean reading replaces one shown with low confidence, or a
    /// repeated reading corrects a first reading that won against it only by chance.
    /// </summary>
    static bool Replaces(Shown last, string best, bool lowConfidence, bool repeated)
    {
        if (lowConfidence && !last.LowConfidence) return false;
        if (IsContinuation(last.Text, best)) return true;
        if (last.LowConfidence && !lowConfidence) return Resembles(last.Text, best);
        if (!last.Confirmed && repeated && Words(best) >= Words(last.Text) && Resembles(last.Text, best)) return true;
        // Grow: more letters than ever shown, so two readings of one line cannot keep replacing each other, and more
        // words, because OCR noise adds letters inside words ("borrowfthe") while a word read late adds a word.
        return TextUtil.Coverage(TextUtil.Compact(last.Text), TextUtil.Compact(best)) >= 0.8
               && Letters(best) > last.MaxLetters
               && Words(best) > Words(last.Text)
               && TextUtil.ReadingScore(best) >= TextUtil.ReadingScore(last.Text);
    }

    /// <summary>A clean reading of a line shown garbled: SameLine is tuned on ordinary misreadings, a garbled one only resembles it.</summary>
    static bool Resembles(string garbled, string clean) =>
        TextUtil.SameLine(garbled, clean) || TextUtil.Similarity(TextUtil.Compact(garbled), TextUtil.Compact(clean)) >= 0.6;

    static int Letters(string text) => text.Count(char.IsLetter);

    static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter));

    /// <summary>At least half of the letters are in plausible words. Clean lines score about their letter count.</summary>
    internal static bool LooksReadable(string text) =>
        TextUtil.ReadingScore(text) >= Math.Max(2, TextUtil.Compact(text).Length / 2);

    static readonly Regex LetterRun = new(@"\p{L}{3,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Garbled text rather than scene noise: letters are most of what is visible, junk does not outweigh the words,
    /// and at least two runs of three letters. Noise from a still scene reads the same every frame ("rn 1l ~ lI",
    /// "ffl 1i ~ wv"), so staying on screen alone does not make it text.
    /// </summary>
    static bool MayForceShow(string text)
    {
        int visible = text.Count(c => !char.IsWhiteSpace(c));
        return TextUtil.Compact(text).Length >= 4 && Letters(text) >= visible * 0.6
               && TextUtil.ReadingScore(text) >= 0 && LetterRun.Count(text) >= 2;
    }

    /// <summary>The reading is a shorter piece of the shown text, long enough to tell (trigrams need 8 characters).</summary>
    static bool IsPartOf(string part, string whole)
    {
        string p = TextUtil.Compact(part), w = TextUtil.Compact(whole);
        return p.Length >= 8 && p.Length < w.Length && TextUtil.Coverage(p, w) >= 0.8;
    }

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
