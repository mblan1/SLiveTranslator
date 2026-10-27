using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SLiveTranslator.Translation.Providers;

/// <summary>
/// The prompt every LLM provider sends and the reading of its answer. The system block (instructions, then the game
/// profile) depends only on the languages and the profile, so it is byte-for-byte the same between calls and the
/// provider's prompt cache serves it. The user message holds what changes every line: the recent conversation and the
/// new lines, as JSON. The answer is {"items":[{"id","source","text"}]}: "source" is the line with obvious OCR errors
/// fixed, and an empty "text" means the line is not language (it is dropped, and the cache keeps nothing for it).
/// </summary>
public static partial class LlmPrompt
{
    static readonly JsonSerializerOptions MessageJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The answer's JSON schema; strict-mode ready (every field required, no extra fields).</summary>
    public const string SchemaJson =
        """{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"id":{"type":"integer"},"source":{"type":"string"},"text":{"type":"string"}},"required":["id","source","text"],"additionalProperties":false}}},"required":["items"],"additionalProperties":false}""";

    public static readonly JsonElement Schema = JsonDocument.Parse(SchemaJson).RootElement.Clone();

    /// <summary>The schema's top-level fields, as the Anthropic SDK takes a schema.</summary>
    public static IReadOnlyDictionary<string, JsonElement> SchemaFields { get; } =
        Schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    public static string Instructions(string src, string tgt)
    {
        string from = src == "auto" ? "the language they are in (detect it)" : $"'{src}'";
        return
            $"You translate live video-game subtitles from {from} into natural spoken '{tgt}' for a player reading them mid-game. " +
            "The text comes from screen OCR and may contain recognition errors.\n" +
            "Input JSON: \"context\" = recent earlier lines, each with its \"source\" and the \"translation\" already shown (use it to tell " +
            "who speaks to whom and to stay consistent; do not translate it again); \"lines\" = the new strings, each with an \"id\".\n" +
            "Output: ONLY a JSON object {\"items\":[{\"id\":0,\"source\":\"...\",\"text\":\"...\"}]} with one item per input line and the same id: " +
            "\"source\" = the line with obvious OCR errors fixed (\"11m\" → \"I'm\", \"Cirils\" → \"Ciri's\", \"youyvere\" → \"you were\"); " +
            "\"text\" = the translation. If a line is not language (random letters, HUD numbers, an unrecoverable fragment) return \"text\": \"\". " +
            "Never merge, split, drop or reorder lines.\n" +
            "Translate meaning, not words: natural equivalents for idioms. Keep tone, sarcasm, swearing and archaic speech. " +
            "Subtitle-length spoken style. Add nothing that is not implied; keep every name, number, negation and clause. " +
            "A line cut off at the edge: translate only what is there. Keep choice numbers (\"1. ...\") and key names (Space, Left Shift, Alt, Tab) as they are.\n" +
            "Speakers and pronouns: infer speaker and listener from the context and the profile, pick the pair the profile gives and keep it for that pair. " +
            "When unsure, leave the pronoun out if that is natural, or reuse the last pair seen.\n" +
            "Never translate names or the profile's keep list; use its glossary renderings exactly.";
    }

    /// <summary>The cached system block: the instructions, then the profile.</summary>
    public static string System(TranslateRequest request)
    {
        string instructions = Instructions(request.Src, request.Tgt);
        return request.Profile is { } profile ? instructions + "\n\n" + profile.ToPrompt() : instructions;
    }

    /// <summary>{"context":[{"source","translation"}],"lines":[{"id","text"}]}; after the cached prefix, since it changes every line.</summary>
    public static string UserMessage(TranslateRequest request) =>
        JsonSerializer.Serialize(new
        {
            context = request.Context.Select(c => new { source = c.Source, translation = c.Translation }),
            lines = request.Lines.Select((l, i) => new { id = i, text = l }),
        }, MessageJson);

    /// <summary>
    /// Reads the items of an answer by id (a number, or a number written as a string). Code fences, a reasoning
    /// model's &lt;think&gt; block and text around the JSON are ignored; a bare array of items is read too. A line
    /// with no item is a failed line (<see cref="TranslateResult.FailedLines"/>): the next service of the chain
    /// translates it. An answer with no usable item at all fails the request.
    /// </summary>
    /// <param name="service">The provider's name, for the error message.</param>
    /// <param name="answeredBy">The translator's name, for <see cref="TranslateResult.AnsweredBy"/>.</param>
    public static TranslateResult Parse(string answer, IReadOnlyList<string> lines, string service, string answeredBy)
    {
        if (Items(answer) is { } items)
        {
            var texts = lines.ToArray();
            var sources = lines.ToArray();
            var found = new bool[lines.Count];
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idElement) || Id(idElement) is not { } id
                    || id < 0 || id >= lines.Count || found[id]) continue;
                if (!item.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) continue;
                found[id] = true;
                texts[id] = t.GetString()!.Trim(); // "" is "not language"
                if (item.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                    sources[id] = s.GetString()!.Trim();
            }
            var failed = Enumerable.Range(0, lines.Count).Where(i => !found[i]).ToList();
            if (failed.Count < lines.Count)
                return new TranslateResult(texts, sources, answeredBy) { FailedLines = failed.Count > 0 ? failed : null };
        }
        throw new TranslatorUnavailableException($"{service} returned an unexpected response: " + ProviderHttp.Shorten(answer));
    }

    static List<JsonElement>? Items(string answer)
    {
        string text = ThinkBlock().Replace(answer, "");
        // The object as asked, from its first '{' to the last '}', whatever '[' the text before it holds ("[en->vi]");
        // a bare array of items only when that span holds no items.
        return Items(text, '{', '}') ?? Items(text, '[', ']');
    }

    /// <summary>The items in the span from the first <paramref name="open"/> to the last <paramref name="close"/>; null when it holds none.</summary>
    static List<JsonElement>? Items(string text, char open, char close)
    {
        int start = text.IndexOf(open);
        int end = text.LastIndexOf(close);
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text.AsMemory(start, end - start + 1));
            var root = doc.RootElement;
            // "items" as asked; a model that named the list otherwise still answered it.
            var list = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind != JsonValueKind.Object ? (JsonElement?)null
                : root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items
                : root.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Array) is { ValueKind: JsonValueKind.Array } other ? other
                : null;
            // A list without one object is no list of items (an array field of a bare array's single item, read as the object).
            return list is { } found && found.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object)
                ? found.EnumerateArray().Select(e => e.Clone()).ToList()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static int? Id(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number when id.TryGetInt32(out int n) => n,
        JsonValueKind.String when int.TryParse(id.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) => n,
        _ => null,
    };

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();
}
