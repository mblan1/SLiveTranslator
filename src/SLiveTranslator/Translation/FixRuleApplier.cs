using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SLiveTranslator.Translation;

/// <summary>
/// Repairs mistakes free machine translation keeps making with a game's terms ("cá viền đỏ" for the horse Roach), by
/// the profile's <see cref="FixRule"/>s and the matching contract:
/// <list type="bullet">
/// <item>The trigger and the unless words are looked for in the original source line, as whole words; one with a
/// capital letter matches as written or in ALL CAPS, an all-lowercase one in any case.</item>
/// <item>The wrong text matches whole syllables in any case, after both sides are NFC-normalized and the old tone
/// placement (oà, oè, uỷ) is read as the new one (òa, òe, ủy).</item>
/// <item>Every match of a rule is replaced; each rule runs once, in file order, on the result of the rules before.</item>
/// <item>An ALL-CAPS match gives an ALL-CAPS replacement, a capitalized one a capitalized replacement.</item>
/// </list>
/// </summary>
public sealed class FixRuleApplier
{
    static readonly Dictionary<string, string> OldTonePlacement = BuildToneMap();

    /// <param name="Text">The rule as written, for <see cref="Applicable"/>.</param>
    sealed record Compiled(Regex[] Trigger, Regex[][] Unless, Regex Wrong, string Right, string Text)
    {
        public bool AppliesTo(string english) => Trigger.Any(t => t.IsMatch(english)) && !Unless.Any(u => u.Any(p => p.IsMatch(english)));
    }

    readonly Compiled[] _rules;

    public FixRuleApplier(IEnumerable<FixRule> rules)
    {
        _rules = rules.Where(r => r.Trigger.Trim().Length > 0 && r.Wrong.Trim().Length > 0)
            .Select(r => new Compiled(
                TermPatterns.Trigger(r.Trigger),
                (r.Unless ?? []).Where(u => u.Trim().Length > 0).Select(TermPatterns.Trigger).ToArray(),
                TermPatterns.Target(FoldTone(r.Wrong)),
                r.Right.Normalize(NormalizationForm.FormC),
                string.Join("\u001f", [r.Trigger, r.Wrong, r.Right, .. r.Unless ?? []])))
            .ToArray();
    }

    /// <summary>The rules whose trigger is in the source line and none of whose unless words are, as written, in file order.</summary>
    public IEnumerable<string> Applicable(string source)
    {
        string english = TermPatterns.FoldQuotes(source);
        return _rules.Where(r => r.AppliesTo(english)).Select(r => r.Text);
    }

    /// <param name="source">The English line as OCR read it, before any placeholder.</param>
    /// <param name="translation">Its translation, with the placeholders already restored.</param>
    public string Apply(string source, string translation) => Apply(source, translation, null);

    /// <param name="fired">Gets the index of every rule that changed the text.</param>
    public string Apply(string source, string translation, List<int>? fired)
    {
        string text = translation.Normalize(NormalizationForm.FormC);
        string english = TermPatterns.FoldQuotes(source);
        for (int i = 0; i < _rules.Length; i++)
        {
            var rule = _rules[i];
            if (!rule.AppliesTo(english)) continue;
            var matches = rule.Wrong.Matches(FoldTone(text)); // the fold keeps every position
            if (matches.Count == 0) continue;
            var sb = new StringBuilder(text.Length + 16);
            int last = 0;
            foreach (Match m in matches)
            {
                sb.Append(text, last, m.Index - last).Append(CarryCase(text.Substring(m.Index, m.Length), rule.Right));
                last = m.Index + m.Length;
            }
            text = sb.Append(text, last, text.Length - last).ToString();
            fired?.Add(i);
        }
        return text;
    }

    /// <summary>NFC, with the old tone placement read as the new one; the length stays the same.</summary>
    internal static string FoldTone(string text)
    {
        text = text.Normalize(NormalizationForm.FormC);
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (!OldTonePlacement.TryGetValue(text.Substring(i, 2), out var modern)) continue;
            sb ??= new StringBuilder(text);
            sb[i] = modern[0];
            sb[i + 1] = modern[1];
            i++;
        }
        return sb?.ToString() ?? text;
    }

    static string CarryCase(string match, string right)
    {
        string letters = string.Concat(match.Where(char.IsLetter));
        if (letters.Length >= 2 && letters == letters.ToUpperInvariant() && letters != letters.ToLowerInvariant()) return right.ToUpperInvariant();
        if (match.Length > 0 && char.IsUpper(match[0]) && right.Length > 0) return char.ToUpperInvariant(right[0]) + right[1..];
        return right;
    }

    /// <summary>"oà" → "òa", "uỷ" → "ủy" and so on, in every combination of upper and lower case.</summary>
    static Dictionary<string, string> BuildToneMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] tones = ["̀", "́", "̉", "̃", "̣"];
        foreach (var (v1, v2) in new[] { ('o', 'a'), ('o', 'e'), ('u', 'y') })
        foreach (var tone in tones)
        foreach (var (c1, c2) in new[] { (v1, v2), (char.ToUpperInvariant(v1), char.ToUpperInvariant(v2)), (char.ToUpperInvariant(v1), v2), (v1, char.ToUpperInvariant(v2)) })
        {
            string old = (c1 + (c2 + tone).Normalize(NormalizationForm.FormC)).Normalize(NormalizationForm.FormC);
            string modern = ((c1 + tone).Normalize(NormalizationForm.FormC) + c2).Normalize(NormalizationForm.FormC);
            if (old.Length == 2 && modern.Length == 2) map[old] = modern;
        }
        return map;
    }
}
