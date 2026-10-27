using System;
using System.Collections.Generic;
using System.Linq;
using SLiveTranslator.Ocr;

namespace SLiveTranslator.Services;

/// <summary>The image a subtitle frame is recognized from.</summary>
internal enum OcrSource { Plain, Mask }

/// <summary>
/// Decides whether a subtitle frame is recognized twice (the frame as it is and its white-text mask) or only the way
/// that keeps winning. Once one way has won <see cref="WinsToSettle"/> times in a row, only that way is read; the
/// other is read again as soon as the reading looks worse: a lower score, text that is not readable, another line,
/// a line box more, or nothing read at all. Plain state with no OCR, so the rules are testable.
/// </summary>
internal sealed class OcrSourcePolicy
{
    public const int WinsToSettle = 5;

    OcrSource _winner;
    int _wins;
    string _text = "";
    int _score, _lines;

    /// <summary>The only source to read, or null to read both and compare them.</summary>
    public OcrSource? Single => _wins >= WinsToSettle ? _winner : null;

    /// <summary>
    /// The <see cref="Single"/> source read <paramref name="lines"/>, joined to <paramref name="text"/>: true when it can
    /// be used without the other reading, because it is the same line as before (or that line grown, as in a typewriter
    /// reveal), at least as clean. Only a frame with white text gets here, so a source that read nothing may have missed
    /// what the other one sees (white text on snow, a line of another colour). A line box more, or one that looks like
    /// scene noise, is what the other image decides about: it may not show that noise at all.
    /// </summary>
    public bool Accepts(string text, IReadOnlyList<OcrLine> lines)
    {
        if (text.Length == 0 || _text.Length == 0) return false; // text appeared, went, or was not read: compare
        if (lines.Count > _lines || lines.Any(l => LooksLikeNoise(l.Text))) return false;
        return SubtitleTracker.LooksReadable(text) && (TextUtil.SameLine(text, _text) || Grew(_text, text))
               && !ScoreDropped(TextUtil.ReadingScore(text));
    }

    /// <summary>The new reading holds the old one and more: the same line revealed further.</summary>
    static bool Grew(string before, string now)
    {
        string b = TextUtil.Compact(before), n = TextUtil.Compact(now);
        return n.Length > b.Length && TextUtil.Coverage(b, n) >= 0.8;
    }

    /// <summary>A few points below the best score of the line: one misread letter is normal, a lost word is not.</summary>
    bool ScoreDropped(int score) => score < _score - Math.Max(2, _score / 10);

    /// <summary>The <see cref="Single"/> source's reading was used alone.</summary>
    public void Kept(string text, int lines)
    {
        _score = Math.Max(_score, TextUtil.ReadingScore(text));
        _text = text;
        _lines = lines;
    }

    /// <summary>
    /// Both sources were read and <paramref name="winner"/>'s reading <paramref name="text"/> was picked. A reading of
    /// nothing tells nothing about which image reads better, so it neither counts as a win nor breaks a run of wins.
    /// </summary>
    public void Won(OcrSource winner, string text, int lines)
    {
        _text = text;
        _score = TextUtil.ReadingScore(text);
        _lines = lines;
        if (text.Length == 0) return;
        _wins = winner == _winner ? _wins + 1 : 1;
        _winner = winner;
    }

    /// <summary>Scene texture rather than text: a junk character, or too few plausible words.</summary>
    internal static bool LooksLikeNoise(string text) => text.Any(TextUtil.IsJunk) || !SubtitleTracker.LooksReadable(text);
}
