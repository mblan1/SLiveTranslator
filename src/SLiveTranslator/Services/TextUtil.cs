using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SLiveTranslator.Ocr;

namespace SLiveTranslator.Services;

public static class TextUtil
{
    public static string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>1.0 = identical, 0.0 = completely different (Levenshtein ratio).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
    }

    static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ---- comparing OCR readings ----

    /// <summary>Lowercase letters and digits only, accents removed. OCR adds and drops spaces, punctuation and accents.</summary>
    public static string Compact(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    static Dictionary<string, int> Trigrams(string s)
    {
        var map = new Dictionary<string, int>();
        for (int i = 0; i + 3 <= s.Length; i++)
        {
            var t = s.Substring(i, 3);
            map[t] = map.TryGetValue(t, out var n) ? n + 1 : 1;
        }
        return map;
    }

    static int Shared(Dictionary<string, int> a, Dictionary<string, int> b) =>
        a.Sum(kv => Math.Min(kv.Value, b.TryGetValue(kv.Key, out var n) ? n : 0));

    /// <summary>Trigram overlap of two compact strings. Robust to scattered OCR mistakes and a missing first or last word.</summary>
    public static double TrigramDice(string a, string b)
    {
        var x = Trigrams(a);
        var y = Trigrams(b);
        int total = x.Values.Sum() + y.Values.Sum();
        return total == 0 ? 0 : 2.0 * Shared(x, y) / total;
    }

    /// <summary>Share of the shorter compact string's trigrams that also occur in the longer one.</summary>
    public static double Coverage(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        var s = Trigrams(shorter);
        int count = s.Values.Sum();
        return count == 0 ? 0 : (double)Shared(s, Trigrams(longer)) / count;
    }

    /// <summary>
    /// True when two OCR readings are the same dialog line. Tuned on real misreadings from a game: readings of one
    /// line scored at least 0.51, the closest pair of different lines 0.29.
    /// </summary>
    public static bool SameLine(string a, string b)
    {
        string x = Compact(a), y = Compact(b);
        if (x == y) return true;
        int shortLen = Math.Min(x.Length, y.Length), longLen = Math.Max(x.Length, y.Length);
        if (shortLen == 0) return false;
        if (longLen > shortLen * 1.35 + 2) return false; // much longer means more text, not another reading
        if (shortLen < 8) return Similarity(x, y) >= 0.8; // trigrams are unreliable on very short text
        return TrigramDice(x, y) >= 0.45 || (shortLen >= 12 && Coverage(x, y) >= 0.8);
    }

    static readonly HashSet<char> InnerPunctuation = new("'’-·、，。！？：；「」『』（）".ToCharArray());
    static readonly char[] EdgePunctuation = ".,!?;:\"“”‘’'…—–-()[]«»¡¿。、！？，：；「」『』（）*".ToCharArray();

    /// <summary>
    /// How clean an OCR reading looks: letters in plausible words count for it, garbled tokens
    /// ("lowéncourtyard" is fine, "uams@Utothe" or "dlå)lnnsell" is not) count against it.
    /// </summary>
    public static int ReadingScore(string text)
    {
        int score = 0;
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim(EdgePunctuation);
            if (token.Length == 0) continue;
            if (token.All(char.IsDigit))
            {
                score += token.Length;
                continue;
            }
            bool clean = token.Any(char.IsLetter) && token.All(c => char.IsLetter(c) || InnerPunctuation.Contains(c));
            for (int i = 1; clean && i < token.Length; i++)
                if (char.IsUpper(token[i]) && char.IsLower(token[i - 1])) clean = false; // "lOC", "courtyardsShe"
            if (clean && token.Length == 1 && "aAiIoO".IndexOf(token[0]) < 0 && token[0] < 0x2E80) clean = false;
            score += clean ? token.Count(char.IsLetter) : -raw.Length;
        }
        return score;
    }

    // ---- layout ----

    /// <summary>Merges OCR lines that sit directly under each other (tooltips, dialogue boxes) into paragraphs.</summary>
    public static List<OcrLine> GroupParagraphs(IReadOnlyList<OcrLine> lines)
    {
        var result = new List<OcrLine>();
        foreach (var line in lines.OrderBy(l => l.Bounds.Top).ThenBy(l => l.Bounds.Left))
        {
            int idx = -1;
            for (int k = result.Count - 1; k >= 0 && k >= result.Count - 6; k--)
            {
                if (Belongs(result[k], line)) { idx = k; break; }
            }

            if (idx < 0)
            {
                result.Add(line);
            }
            else
            {
                var p = result[idx];
                result[idx] = new OcrLine(p.Text + " " + line.Text, Rectangle.Union(p.Bounds, line.Bounds), p.LineCount + 1);
            }
        }
        return result;
    }

    static bool Belongs(OcrLine para, OcrLine line)
    {
        double lineH = (double)para.Bounds.Height / para.LineCount;
        if (lineH <= 0) return false;
        double gap = line.Bounds.Top - para.Bounds.Bottom;
        if (gap > lineH * 0.7 || gap < -lineH * 0.5) return false;
        bool overlapX = line.Bounds.Left < para.Bounds.Right && line.Bounds.Right > para.Bounds.Left;
        bool alignedLeft = Math.Abs(line.Bounds.Left - para.Bounds.Left) < lineH * 1.5;
        double heightRatio = line.Bounds.Height / lineH;
        return (overlapX || alignedLeft) && heightRatio > 0.5 && heightRatio < 2.0;
    }
}
