using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SLiveTranslator.Updates;

/// <summary>A release's Markdown notes as plain text for the update window: headings, lists, tables and emphasis without their marks.</summary>
public static partial class ReleaseNotesText
{
    public static string Plain(string? markdown)
    {
        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var output = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (TableSeparator().IsMatch(line)) continue;
            if (line.TrimStart().StartsWith('|'))
            {
                // A table's header is the row above its separator.
                if (i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1].TrimEnd())) continue;
                var cells = line.Trim().Trim('|').Split('|').Select(c => Inline(c.Trim())).Where(c => c.Length > 0);
                output.Add(string.Join("  –  ", cells));
                continue;
            }
            if (Heading().Match(line) is { Success: true } heading)
            {
                if (output.Count > 0 && output[^1].Length > 0) output.Add("");
                output.Add(Inline(heading.Groups[1].Value));
                continue;
            }
            if (ListItem().Match(line) is { Success: true } item)
            {
                string indent = new(' ', item.Groups[1].Value.Replace("\t", "  ").Length / 2 * 2);
                output.Add(indent + "• " + Inline(item.Groups[2].Value));
                continue;
            }
            output.Add(Inline(line.Trim()));
        }

        // No more than one empty line in a row, none at the ends.
        var text = new List<string>();
        foreach (var line in output)
        {
            if (line.Length == 0 && (text.Count == 0 || text[^1].Length == 0)) continue;
            text.Add(line);
        }
        while (text.Count > 0 && text[^1].Length == 0) text.RemoveAt(text.Count - 1);
        return string.Join(Environment.NewLine, text);
    }

    static string Inline(string text)
    {
        text = Image().Replace(text, "");
        text = Link().Replace(text, "$1");
        text = Tag().Replace(text, "");
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        text = Emphasis().Replace(text, "$1");
        return text.Trim();
    }

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[*+-]\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"</?[A-Za-z][^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?<![\w*])\*(?!\s)([^*\n]+?)(?<!\s)\*(?![\w*])")]
    private static partial Regex Emphasis();
}
