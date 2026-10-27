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
        if (TrigramDice(x, y) < 0.45 && (shortLen < 12 || Coverage(x, y) < 0.8)) return false;
        return Skeleton(x) == Skeleton(y) || !HasDistinctWords(a, b);
    }

    /// <summary>
    /// Each reading has a word the other cannot be a misreading of: "Where is Yennefer?" / "Where is Ciri?",
    /// "What do you want?" / "What do you need?". Trigrams cannot tell these apart from OCR noise, because most of
    /// the line is shared. OCR mangles letters inside words and merges or drops words; it does not put an unrelated
    /// clean word on both sides. A word garbled on both sides ("getto" / "geryo") still resembles its counterpart.
    /// </summary>
    static bool HasDistinctWords(string a, string b)
    {
        var onlyA = UnexplainedWords(a, b);
        if (onlyA.Count == 0) return false;
        var onlyB = UnexplainedWords(b, a);
        if (onlyB.Count == 0) return false;
        return onlyA.Any(u => onlyB.All(v => Similarity(u, v) < 0.5)) && onlyB.Any(v => onlyA.All(u => Similarity(u, v) < 0.5));
    }

    /// <summary>Words of <paramref name="text"/> (2+ letters) that are not, give or take OCR slips, anywhere in <paramref name="other"/>.</summary>
    static List<string> UnexplainedWords(string text, string other)
    {
        string target = Skeleton(Compact(other));
        var result = new List<string>();
        foreach (var token in WordSplit.Split(text))
        {
            string word = Skeleton(Compact(token));
            if (word.Count(char.IsLetter) < 2) continue;
            int allowed = word.Length <= 2 ? 0 : word.Length <= 5 ? 1 : word.Length <= 9 ? 2 : 3;
            if (NearestMatch(word, target) > allowed) result.Add(word);
        }
        return result;
    }

    static readonly Regex WordSplit = new(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant);

    /// <summary>A compact string with the shapes OCR confuses made equal: rn/m, vv/w, 1/l/i, 0/o.</summary>
    static string Skeleton(string compact)
    {
        var sb = new StringBuilder(compact.Length);
        for (int i = 0; i < compact.Length; i++)
        {
            char c = compact[i];
            if (i + 1 < compact.Length && (c, compact[i + 1]) is ('r', 'n') or ('v', 'v'))
            {
                sb.Append(c == 'r' ? 'm' : 'w');
                i++;
                continue;
            }
            sb.Append(c switch { '1' or 'l' => 'i', '0' => 'o', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>Fewest edits that turn <paramref name="word"/> into some substring of <paramref name="text"/>.</summary>
    static int NearestMatch(string word, string text)
    {
        if (text.Contains(word, StringComparison.Ordinal)) return 0;
        var prev = new int[text.Length + 1]; // matching may start anywhere in the text, so row 0 is all zeros
        var cur = new int[text.Length + 1];
        for (int i = 1; i <= word.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= text.Length; j++)
            {
                int cost = word[i - 1] == text[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev.Min();
    }

    static readonly HashSet<char> InnerPunctuation = new("'’-·、，。！？：；「」『』（）".ToCharArray());
    static readonly char[] EdgePunctuation = ".,!?;:\"“”‘’'…—–-()[]«»¡¿。、！？，：；「」『』（）*".ToCharArray();
    static readonly HashSet<char> CommonPunctuation = new(EdgePunctuation.Concat("/%+&·"));

    /// <summary>
    /// How clean an OCR reading looks: letters in plausible words count for it, junk counts against it.
    /// A token is split at punctuation and at a lower→upper case change first, so one stray mark costs only
    /// itself: "why,you" and ".Wrong:Footwork!" are two clean words, a junk character ("uams@Utothe") costs 2
    /// and a digit/letter mix ("11m", "pr4ctice") half its length.
    /// </summary>
    /// <param name="language">
    /// The OCR language. For English an accented or other non-ASCII letter counts as junk: English OCR invents them
    /// in garbled words ("VLéfrijSéd", "yoå'd", "taßleep"), which would otherwise score like clean letters.
    /// </param>
    public static int ReadingScore(string text, string? language = null)
    {
        bool ascii = IsEnglish(language);
        int score = 0;
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (char c in raw)
                if (IsJunk(c) || ascii && IsForeignLetter(c)) score -= 2;
            foreach (var part in WordParts(raw)) score += PartScore(part, ascii);
        }
        return score;
    }

    static bool IsEnglish(string? language) => language is not null && language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Latin, Greek or Cyrillic letter outside ASCII; scripts such as CJK are not counted.</summary>
    static bool IsForeignLetter(char c) => c > 0x7F && c < 0x2E80 && char.IsLetter(c);

    /// <summary>A character that is neither text, space nor ordinary punctuation ('@', '|', '~', '$'): OCR noise.</summary>
    public static bool IsJunk(char c) => !char.IsWhiteSpace(c) && !char.IsLetterOrDigit(c) && !CommonPunctuation.Contains(c);

    /// <summary>Runs of letters, digits and in-word marks, cut at a lower→upper case change ("courtyardsShe").</summary>
    static IEnumerable<string> WordParts(string token)
    {
        int start = 0;
        for (int i = 0; i <= token.Length; i++)
        {
            bool end = i == token.Length;
            bool separator = !end && !char.IsLetterOrDigit(token[i]) && !InnerPunctuation.Contains(token[i]);
            bool caseChange = !end && i > start && char.IsUpper(token[i]) && char.IsLower(token[i - 1]);
            if (!end && !separator && !caseChange) continue;
            if (i > start) yield return token[start..i].Trim(EdgePunctuation);
            start = caseChange ? i : i + 1;
        }
    }

    static int PartScore(string part, bool ascii)
    {
        int letters = part.Count(c => char.IsLetter(c) && !(ascii && IsForeignLetter(c))), digits = part.Count(char.IsDigit);
        if (letters == 0) return digits;
        if (digits > 0) return -Math.Max(1, part.Length / 2);
        if (part.Length == 1 && "aAiIoO".IndexOf(part[0]) < 0 && part[0] < 0x2E80) return -1; // a stray letter, not a word
        return letters;
    }

    /// <summary>
    /// The better of two OCR readings of the same text: the more complete one when it is clearly longer and contains
    /// the other, otherwise the higher score plus a bonus for length, so a clean reading that dropped a word
    /// ("Butyou were emir.") loses to the complete one with a few slips. Ties keep <paramref name="a"/>.
    /// </summary>
    /// <param name="language">The OCR language, see <see cref="ReadingScore"/>.</param>
    public static string PickReading(string a, string b, string? language = null)
    {
        string ca = Compact(a), cb = Compact(b);
        if (Math.Max(ca.Length, cb.Length) >= Math.Min(ca.Length, cb.Length) * 1.15 && Coverage(ca, cb) >= 0.75)
            return cb.Length > ca.Length ? b : a;
        return ReadingScore(b, language) + 0.3 * cb.Length > ReadingScore(a, language) + 0.3 * ca.Length ? b : a;
    }

    // ---- contractions ----

    const RegexOptions Contraction = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // OCR reads a thin apostrophe as l, 1, |, ! or t ("Wonlt", "shouldlve", "Couldtve"), or drops it.
    static readonly Regex BrokenNt = new(@"(?<=\p{L})n[l1|!]t\b(?!-)", Contraction);
    static readonly Regex[] StemContractions =
    [
        new(@"\b(don|won|can|couldn|wouldn|shouldn|didn|doesn|isn|wasn|aren|weren|haven|hasn|hadn|mustn|needn|ain)()(t)\b(?!-)", Contraction),
        new(@"\b(he|she|it|that|there|here|what|where|who|how|why|when)([l1|!t]?)(s|d|ll)\b(?!-)", Contraction),
        new(@"\b(you|we|they|who)([l1|!t]?)(ll|d|ve|re)\b(?!-)", Contraction),
        new(@"\b(let)([l1|!t]?)(s)\b(?!-)", Contraction),
        new(@"\b(should|would|could|must|might)([l1|!t]?)(ve)\b(?!-)", Contraction),
        new(@"\b(I)([l1|!]?)(m|ll|d|ve)\b(?!-)", RegexOptions.CultureInvariant), // only a capital I: "ill", "id" are words
    ];
    // "I" itself misread: "l'm" anywhere, "Pm" before a word, "11m" / "lm" only where a sentence starts.
    static readonly Regex MisreadI = new(@"(?<![\p{L}\d])[l1|]['’](m|ll|d|ve)\b(?!-)", RegexOptions.CultureInvariant);
    static readonly Regex MisreadIm = new(@"(?<![\p{L}\d'’])Pm\b(?=\s+\p{Ll})", RegexOptions.CultureInvariant);
    static readonly Regex LeadingMisreadI = new(@"(?<=^|\n|[.!?…]\s+)[l1|][l1|!]?(m|ll|d|ve)\b(?!-)", RegexOptions.CultureInvariant);

    /// <summary>Real words that look like a contraction with its apostrophe dropped or misread.</summary>
    static readonly HashSet<string> RealWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "its", "hell", "held", "shell", "shed", "well", "were", "wed", "weld", "lets", "whore", "howls",
    };

    sealed record NamePattern(string Key, Regex Pattern);
    static NamePattern? _namePossessive; // the last list used; names change only with the game profile

    /// <summary>
    /// Puts back apostrophes that OCR misread or dropped in contractions ("Wonlt" → "Won't", "shouldlve" →
    /// "should've", "11m" → "I'm") and in possessives of <paramref name="names"/> ("Cirils" → "Ciri's").
    /// Only known stems are touched, and real words such as "Howls", "shells" or "well" are left alone.
    /// </summary>
    public static string RepairContractions(string text, IEnumerable<string>? names = null)
    {
        if (text.Length == 0) return text;
        text = BrokenNt.Replace(text, m => $"{m.Value[0]}'{m.Value[2]}");
        foreach (var pattern in StemContractions)
            text = pattern.Replace(text, m => RealWords.Contains(m.Value) ? m.Value : $"{m.Groups[1].Value}'{m.Groups[3].Value}");
        text = MisreadI.Replace(text, m => "I'" + m.Groups[1].Value);
        text = MisreadIm.Replace(text, "I'm");
        text = LeadingMisreadI.Replace(text, m => "I'" + m.Groups[1].Value);
        if (NamePossessive(names) is { } possessive)
            text = possessive.Replace(text, m => m.Groups[1].Value + "'s");
        return text;
    }

    static Regex? NamePossessive(IEnumerable<string>? names)
    {
        var list = names?.Where(n => n.Length > 1).OrderByDescending(n => n.Length).Select(Regex.Escape).ToList();
        if (list is null || list.Count == 0) return null;
        string key = string.Join("|", list);
        if (_namePossessive is { } cached && cached.Key == key) return cached.Pattern;
        var pattern = new Regex($@"\b({key})[l1|!t]s\b(?!-)", RegexOptions.CultureInvariant);
        _namePossessive = new NamePattern(key, pattern);
        return pattern;
    }

    // ---- layout ----

    static readonly Regex NumberedChoice = new(@"^\s*\d{1,2}[.)]\s", RegexOptions.CultureInvariant);

    /// <summary>Merges OCR lines that sit directly under each other (tooltips, dialogue boxes) into paragraphs.</summary>
    /// <param name="maxGapRatio">Largest gap between two lines of one paragraph, in line heights. OCR boxes of lines
    /// without descenders are short, so subtitles, whose lines are spaced wider, need more than the default.</param>
    /// <param name="keepNumberedApart">A line starting with "1." or "2)" (a dialog choice) always starts a new paragraph.</param>
    public static List<OcrLine> GroupParagraphs(IReadOnlyList<OcrLine> lines, double maxGapRatio = 0.7, bool keepNumberedApart = false)
    {
        var result = new List<OcrLine>();
        foreach (var line in lines.OrderBy(l => l.Bounds.Top).ThenBy(l => l.Bounds.Left))
        {
            int idx = -1;
            bool startsParagraph = keepNumberedApart && NumberedChoice.IsMatch(line.Text);
            for (int k = result.Count - 1; !startsParagraph && k >= 0 && k >= result.Count - 6; k--)
            {
                if (Belongs(result[k], line, maxGapRatio)) { idx = k; break; }
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

    static bool Belongs(OcrLine para, OcrLine line, double maxGapRatio)
    {
        double lineH = (double)para.Bounds.Height / para.LineCount;
        if (lineH <= 0) return false;
        double gap = line.Bounds.Top - para.Bounds.Bottom;
        if (gap > lineH * maxGapRatio || gap < -lineH * 0.5) return false;
        bool overlapX = line.Bounds.Left < para.Bounds.Right && line.Bounds.Right > para.Bounds.Left;
        bool alignedLeft = Math.Abs(line.Bounds.Left - para.Bounds.Left) < lineH * 1.5;
        double heightRatio = line.Bounds.Height / lineH;
        return (overlapX || alignedLeft) && heightRatio > 0.5 && heightRatio < 2.0;
    }
}
