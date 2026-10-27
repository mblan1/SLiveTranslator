using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SLiveTranslator.Translation;

/// <summary>Keeps fields this version does not know, so saving a profile written by a newer version loses nothing.</summary>
public abstract class ProfilePart
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// What a game needs translated right: names that stay as they are, fixed renderings of game terms, who speaks to whom
/// with which pronouns, style notes, and fixes for mistakes the free machine translation keeps making. Stored as
/// ProfilesDir/*.json by <see cref="GameProfileStore"/>; the matching rules are in <see cref="ProfileMatcher"/> and
/// <see cref="FixRuleApplier"/>.
/// </summary>
public sealed class GameProfile : ProfilePart
{
    const string LlmOnlyPrefix = "LLM only:";

    (int Version, List<string> Keep, IReadOnlyList<string> Names)? _names;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Goes up with every saved edit, so cache entries made with an earlier version are translated again.</summary>
    public int Version { get; set; } = 1;

    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "vi";

    /// <summary>Executable names without ".exe"; the profile is picked when the game in the subtitle region is one of them.</summary>
    public List<string> ProcessNames { get; set; } = new();

    /// <summary>Names and terms never translated.</summary>
    public List<string> Keep { get; set; } = new();

    public List<GlossaryEntry> Glossary { get; set; } = new();
    public List<GameCharacter> Characters { get; set; } = new();
    public List<string> Style { get; set; } = new();

    /// <summary>Applied in this order to free machine translation (see <see cref="FixRuleApplier"/>).</summary>
    public List<FixRule> FixRules { get; set; } = new();

    /// <summary>The keep entries that are proper names (they start with a capital); their possessive "Cirils" is "Ciri's".</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Names
    {
        get
        {
            if (_names is { } cached && cached.Version == Version && ReferenceEquals(cached.Keep, Keep)) return cached.Names;
            var names = Keep.Where(k => k.Length > 1 && char.IsUpper(k[0])).ToList();
            _names = (Version, Keep, names);
            return names;
        }
    }

    /// <summary>The profile is written for this language pair ("auto" fits any source).</summary>
    public bool Fits(string src, string tgt) =>
        (src == "auto" || SourceLanguage.Equals(src, StringComparison.OrdinalIgnoreCase)) && TargetLanguage.Equals(tgt, StringComparison.OrdinalIgnoreCase);

    public bool MatchesProcess(string processName)
    {
        string name = WithoutExe(processName.Trim());
        return ProcessNames.Any(p => WithoutExe(p.Trim()).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    static string WithoutExe(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// The profile as a block for an LLM's system prompt: keep list, glossary, characters with their pronoun pairs, and
    /// style, in the profile's own order. The text depends on nothing else, so the prompt prefix stays cacheable.
    /// </summary>
    public string ToPrompt()
    {
        var sb = new StringBuilder(8 * 1024);
        sb.Append("<game_profile>\n");
        sb.Append("Game: ").Append(Name).Append('\n');
        if (Keep.Count > 0)
            sb.Append("Keep exactly as written, never translate: ").AppendJoin(", ", Keep).Append('\n');
        if (Glossary.Count > 0)
        {
            sb.Append("Glossary, use these renderings exactly (English = ").Append(TargetLanguage).Append("):\n");
            foreach (var g in Glossary)
            {
                sb.Append(g.En).Append(" = ").Append(g.Vi);
                if (NoteText(g.Note) is { Length: > 0 } note) sb.Append(" (").Append(note).Append(')');
                sb.Append('\n');
            }
        }
        if (Characters.Count > 0)
        {
            sb.Append("Characters, and the pronoun pair each uses with others (to listener: self/other):\n");
            foreach (var c in Characters)
            {
                sb.Append(c.Name).Append(", ").Append(c.Gender);
                if (c.About.Length > 0) sb.Append(": ").Append(c.About);
                if (c.Address.Count > 0)
                    sb.Append(". Address: ").AppendJoin("; ", c.Address.Select(a => $"to {a.To} {a.Self}/{a.Other}"));
                sb.Append('\n');
            }
        }
        if (Style.Count > 0)
        {
            sb.Append("Style:\n");
            foreach (var s in Style) sb.Append("- ").Append(s).Append('\n');
        }
        sb.Append("</game_profile>");
        return sb.ToString();
    }

    /// <summary>
    /// The profile with every null a hand-edited file may hold ("keep": null, a rule with "right": null) read as the
    /// field's default, and null list items dropped, so no use of the profile has to expect one.
    /// </summary>
    internal GameProfile Normalized()
    {
        Id ??= "";
        Name ??= "";
        SourceLanguage ??= "en";
        TargetLanguage ??= "vi";
        ProcessNames = Present(ProcessNames);
        Keep = Present(Keep);
        Style = Present(Style);
        Glossary = Present(Glossary);
        foreach (var g in Glossary) (g.En, g.Vi) = (g.En ?? "", g.Vi ?? "");
        Characters = Present(Characters);
        foreach (var c in Characters)
        {
            (c.Name, c.Gender, c.About) = (c.Name ?? "", c.Gender ?? "", c.About ?? "");
            c.Address = Present(c.Address);
            foreach (var a in c.Address) (a.To, a.Self, a.Other) = (a.To ?? "", a.Self ?? "", a.Other ?? "");
        }
        FixRules = Present(FixRules);
        foreach (var r in FixRules)
        {
            (r.Trigger, r.Wrong, r.Right) = (r.Trigger ?? "", r.Wrong ?? "", r.Right ?? "");
            if (r.Unless is not null) r.Unless = Present(r.Unless);
        }
        return this;
    }

    static List<T> Present<T>(List<T>? items) where T : class => items?.Where(i => i is not null).ToList() ?? new();

    static string NoteText(string? note) =>
        note is null ? "" : note.StartsWith(LlmOnlyPrefix, StringComparison.Ordinal) ? note[LlmOnlyPrefix.Length..].Trim() : note.Trim();

    internal static bool IsLlmOnly(string? note) => note?.StartsWith(LlmOnlyPrefix, StringComparison.Ordinal) == true;
}

public sealed class GlossaryEntry : ProfilePart
{
    public string En { get; set; } = "";
    public string Vi { get; set; } = "";

    /// <summary>A note that starts "LLM only:" marks a term that only an LLM can apply; it never goes to a mechanical glossary.</summary>
    public string? Note { get; set; }

    [JsonIgnore]
    public bool LlmOnly => GameProfile.IsLlmOnly(Note);
}

public sealed class GameCharacter : ProfilePart
{
    /// <summary>Reserved listener groups in <see cref="AddressPair.To"/>, besides character names.</summary>
    public static readonly IReadOnlyList<string> Groups = ["children", "enemies", "strangers"];

    public string Name { get; set; } = "";
    public string Gender { get; set; } = "";
    public string About { get; set; } = "";
    public List<AddressPair> Address { get; set; } = new();
}

/// <summary>How a character speaks to <see cref="To"/>: the pronoun for themself and the one for the listener.</summary>
public sealed class AddressPair : ProfilePart
{
    public string To { get; set; } = "";
    public string Self { get; set; } = "";
    public string Other { get; set; } = "";
}

/// <summary>
/// When the English source contains <see cref="Trigger"/> and none of <see cref="Unless"/>, every <see cref="Wrong"/>
/// in the translation becomes <see cref="Right"/>.
/// </summary>
public sealed class FixRule : ProfilePart
{
    public string Trigger { get; set; } = "";
    public string Wrong { get; set; } = "";
    public string Right { get; set; } = "";

    /// <summary>English words or phrases that make the rule unsafe for the line; matched like <see cref="Trigger"/>.</summary>
    public List<string>? Unless { get; set; }
}
