using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SLiveTranslator.Translation;

/// <summary>A source line with its keep entries replaced by placeholders, and what each placeholder stands for.</summary>
/// <param name="Originals">The source text of placeholder n at index n-1; empty when nothing was replaced.</param>
public sealed record ShieldedLine(string Text, IReadOnlyList<string> Originals);

/// <summary>
/// Shields names from machine translation that takes no instructions (Google gtx, MyMemory, Google Cloud): each keep
/// entry becomes a token, and the token is replaced by the exact source text in the answer.
/// <para>The token is <c>QZX</c> and the placeholder's number ("QZX1"). A letter group that is no word in any
/// language, joined to a digit, reads as a code: engines copy it unchanged and at most re-case it, put a space before
/// the digit or move it within the sentence, which <see cref="Restore"/> accepts. Brackets and braces were not used:
/// game text has its own [Space] style key prompts, and engines rewrite punctuation (full-width brackets, spacing).</para>
/// A translation that lost a token, repeated one or holds an unknown one cannot be put back together; the caller then
/// translates the line without placeholders.
/// </summary>
public static class Placeholders
{
    public const string Marker = "QZX";

    static readonly Regex Token = new(Marker + @"\s?(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="spans">The keep entries in the line, in order (<see cref="ProfileMatcher.KeepSpans"/>).</param>
    public static ShieldedLine Protect(string source, IReadOnlyList<TermMatch> spans)
    {
        // A line that already holds the marker could not be told apart from its tokens.
        if (spans.Count == 0 || source.Contains(Marker, StringComparison.OrdinalIgnoreCase)) return new ShieldedLine(source, []);
        var sb = new StringBuilder(source.Length);
        var originals = new List<string>(spans.Count);
        int last = 0;
        foreach (var span in spans)
        {
            sb.Append(source, last, span.Index - last).Append(Marker).Append(originals.Count + 1);
            originals.Add(span.Text);
            last = span.Index + span.Length;
        }
        sb.Append(source, last, source.Length - last);
        return new ShieldedLine(sb.ToString(), originals);
    }

    /// <summary>The line has a letter outside its tokens: something to translate besides the kept names.</summary>
    public static bool HasWords(ShieldedLine line)
    {
        string text = line.Originals.Count == 0 ? line.Text : Token.Replace(line.Text, " ");
        return text.Any(char.IsLetter);
    }

    /// <summary>The translation with every token replaced by its source text; null when the tokens did not survive one each.</summary>
    public static string? Restore(string translated, ShieldedLine line)
    {
        if (line.Originals.Count == 0) return translated;
        var seen = new bool[line.Originals.Count];
        bool broken = false;
        string restored = Token.Replace(translated, m =>
        {
            if (!int.TryParse(m.Groups[1].ValueSpan, out int n) || n < 1 || n > seen.Length || seen[n - 1])
            {
                broken = true;
                return m.Value;
            }
            seen[n - 1] = true;
            return line.Originals[n - 1];
        });
        return broken || Array.IndexOf(seen, false) >= 0 ? null : restored;
    }
}
