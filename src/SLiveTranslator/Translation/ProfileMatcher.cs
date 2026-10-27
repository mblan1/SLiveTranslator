using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SLiveTranslator.Translation;

/// <summary>A profile term found in a source line.</summary>
/// <param name="Text">The source text exactly as written there.</param>
/// <param name="Translation">The glossary rendering; null for a keep entry, which stays as it is.</param>
public sealed record TermMatch(int Index, int Length, string Text, string? Translation);

/// <summary>
/// Finds a profile's keep entries and glossary terms in English source lines, by the profile matching contract:
/// <list type="bullet">
/// <item>Whole words or phrases (no letter or digit on either side), the longest entry first, never overlapping.</item>
/// <item>’ ‘ ` ´ count as ', and a space in an entry matches any run of white space, line breaks included.</item>
/// <item>A keep entry that starts in lowercase matches in any case ("witcher" also finds Witcher and WITCHER); one
/// that starts with a capital matches as written or in ALL CAPS ("Roach", "ROACH", never "roach").</item>
/// <item>A glossary term matches in any case, its last word also as a plural (s, es, ies, f→ves). Terms whose note
/// starts "LLM only:" are left out: only an LLM can apply them.</item>
/// </list>
/// One matcher per profile version, shared by all threads.
/// </summary>
public sealed class ProfileMatcher
{
    static readonly ConditionalWeakTable<GameProfile, ProfileMatcher> Cache = new();
    static readonly JsonSerializerOptions StampJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    sealed record Candidate(Regex[] Patterns, string? Translation);

    /// <param name="Text">The entry as written, for <see cref="PromptStamp"/>.</param>
    sealed record GlossaryPattern(Regex Pattern, string Text);

    readonly Candidate[] _keep;
    readonly Lazy<Candidate[]> _terms;
    readonly Lazy<GlossaryPattern[]> _promptGlossary;
    readonly string _promptBase;

    ProfileMatcher(GameProfile profile)
    {
        Version = profile.Version;
        var keep = profile.Keep.Where(k => k.Trim().Length > 0)
            .Select(k => (k.Length, Candidate: new Candidate(TermPatterns.English(k, caseInsensitive: !char.IsUpper(k.Trim()[0])), null)))
            .OrderByDescending(c => c.Length)
            .ToArray();
        _keep = keep.Select(c => c.Candidate).ToArray();
        var glossary = profile.Glossary.Where(g => !g.LlmOnly && g.En.Trim().Length > 0).Select(g => (g.En, g.Vi)).ToArray();
        // Only engines with a glossary of their own need these; keep entries sort first, so a term that is both stays as it is.
        _terms = new(() => keep
            .Concat(glossary.Select(g => (g.En.Length, Candidate: new Candidate([TermPatterns.Plural(g.En)], g.Vi))))
            .OrderByDescending(c => c.Length)
            .Select(c => c.Candidate)
            .ToArray());
        var allGlossary = profile.Glossary.Where(g => g.En.Trim().Length > 0).Select(g => (g.En, Text: $"{g.En}={g.Vi}={g.Note}")).ToArray();
        _promptGlossary = new(() => allGlossary.Select(g => new GlossaryPattern(TermPatterns.Plural(g.En), g.Text)).ToArray());
        // What reaches an LLM's prompt for every line, whatever the line holds.
        _promptBase = Hash([JsonSerializer.Serialize(new { profile.Name, profile.SourceLanguage, profile.TargetLanguage, profile.Characters, profile.Style }, StampJson)]);
        FixRules = new FixRuleApplier(profile.FixRules);
    }

    /// <summary>The profile version this matcher was built from.</summary>
    public int Version { get; }

    /// <summary>The profile's fix rules, compiled.</summary>
    public FixRuleApplier FixRules { get; }

    /// <summary>The matcher for the profile as it is now; rebuilt after an edit raised its version.</summary>
    public static ProfileMatcher For(GameProfile profile)
    {
        if (Cache.TryGetValue(profile, out var matcher) && matcher.Version == profile.Version) return matcher;
        matcher = new ProfileMatcher(profile);
        Cache.AddOrUpdate(profile, matcher);
        return matcher;
    }

    /// <summary>The keep entries in the line, in order: what free machine translation must not touch.</summary>
    public IReadOnlyList<TermMatch> KeepSpans(string source) => Find(source, _keep);

    /// <summary>
    /// Keep entries and glossary terms in one pass, in order, for engines that apply a glossary themselves: a glossary
    /// phrase that holds a keep entry ("Skellige Isles") is one term.
    /// </summary>
    public IReadOnlyList<TermMatch> Terms(string source) => Find(source, _terms.Value);

    /// <summary>
    /// What of the profile free machine translation of the line depends on (<see cref="ProfileTranslator"/>): the keep
    /// entries in it, which go out as placeholders, and the fix rules that apply to it. "" when the profile does
    /// nothing to the line, the same as without a profile. An edit of anything else (characters, style, glossary,
    /// entries the line does not hold) leaves the stamp, and so the cached translation, as it is.
    /// </summary>
    public string MachineStamp(string source)
    {
        var keep = KeepSpans(source);
        var rules = FixRules.Applicable(source).ToList();
        return keep.Count == 0 && rules.Count == 0 ? "" : Hash(["keep", .. keep.Select(k => k.Text), "rules", .. rules]);
    }

    /// <summary>
    /// What of the profile an LLM's translation of the line depends on: the keep entries and glossary terms in the line
    /// (those only an LLM can apply too), and the characters and style, which may matter for any line.
    /// </summary>
    public string PromptStamp(string source)
    {
        string english = TermPatterns.FoldQuotes(source);
        var glossary = _promptGlossary.Value.Where(g => g.Pattern.IsMatch(english)).Select(g => g.Text);
        return Hash([_promptBase, "keep", .. KeepSpans(source).Select(k => k.Text), "glossary", .. glossary]);
    }

    /// <summary>A short hash: enough to tell two versions of the same few entries apart.</summary>
    static string Hash(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001e", parts))), 0, 8);

    static List<TermMatch> Find(string source, Candidate[] candidates)
    {
        var result = new List<TermMatch>();
        if (source.Length == 0) return result;
        string folded = TermPatterns.FoldQuotes(source); // same length, so positions hold for the source
        foreach (var candidate in candidates)
        {
            foreach (var pattern in candidate.Patterns)
            {
                for (var m = pattern.Match(folded); m.Success; m = m.NextMatch())
                {
                    if (result.Any(t => m.Index < t.Index + t.Length && t.Index < m.Index + m.Length)) continue;
                    result.Add(new TermMatch(m.Index, m.Length, source.Substring(m.Index, m.Length), candidate.Translation));
                }
            }
        }
        result.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }
}

/// <summary>The English-side patterns of the profile contract, shared by keep, glossary, rule triggers and unless words.</summary>
internal static class TermPatterns
{
    static readonly char[] Quotes = ['’', '‘', '`', '´'];

    /// <summary>Curly quotes, backtick and acute accent as a plain apostrophe; keeps the length.</summary>
    public static string FoldQuotes(string s) =>
        s.IndexOfAny(Quotes) < 0 ? s : s.Replace('’', '\'').Replace('‘', '\'').Replace('`', '\'').Replace('´', '\'');

    /// <param name="caseInsensitive">Otherwise the term matches as written or in ALL CAPS.</param>
    public static Regex[] English(string term, bool caseInsensitive)
    {
        string folded = FoldQuotes(term.Trim());
        if (caseInsensitive) return [Build(Words(folded), RegexOptions.IgnoreCase)];
        string upper = folded.ToUpperInvariant();
        return upper == folded ? [Build(Words(folded), RegexOptions.None)] : [Build(Words(folded), RegexOptions.None), Build(Words(upper), RegexOptions.None)];
    }

    /// <summary>A trigger or unless word: one with any capital letter matches as written or in ALL CAPS, otherwise any case.</summary>
    public static Regex[] Trigger(string term) => English(term, caseInsensitive: !term.Any(char.IsUpper));

    /// <summary>A glossary term in any case, its last word also as a plural.</summary>
    public static Regex Plural(string term)
    {
        var words = Words(FoldQuotes(term.Trim())).ToList();
        string last = words[^1];
        var forms = new List<string> { last, last + "s", last + "es" };
        if (last.EndsWith('y')) forms.Add(last[..^1] + "ies");
        if (last.EndsWith('f')) forms.Add(last[..^1] + "ves");
        string lastPattern = "(?:" + string.Join("|", forms.Distinct().OrderByDescending(f => f.Length).Select(Regex.Escape)) + ")";
        string body = string.Join(@"\s+", words.Take(words.Count - 1).Select(Regex.Escape).Append(lastPattern));
        return new Regex(Bounded(body), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>A Vietnamese (target-side) phrase: whole syllables, any case, any run of white space between words.</summary>
    public static Regex Target(string phrase) => Build(Words(phrase.Trim()), RegexOptions.IgnoreCase);

    static IEnumerable<string> Words(string term) => term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    static Regex Build(IEnumerable<string> words, RegexOptions options) =>
        new(Bounded(string.Join(@"\s+", words.Select(Regex.Escape))), options | RegexOptions.CultureInvariant);

    static string Bounded(string body) => @"(?<![\p{L}\p{N}])" + body + @"(?![\p{L}\p{N}])";
}
