using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SLiveTranslator.Translation.Providers;

/// <summary>Reads fields of an API's JSON without trusting its shape: a missing field or one of another type is null.</summary>
internal static class JsonRead
{
    /// <summary>The document, or null when the text is no JSON (a proxy's HTML page).</summary>
    public static JsonDocument? Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? Get(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? Str(this JsonElement e, string name) => e.Get(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>A number, or a number written as a string.</summary>
    public static double? Num(this JsonElement e, string name) => e.Get(name) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) => n,
        _ => null,
    };

    public static bool? Bool(this JsonElement e, string name) => e.Get(name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    public static IEnumerable<JsonElement> Items(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static IEnumerable<string> Strings(this JsonElement e, string name) =>
        e.Items(name).Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!);

    /// <summary>The largest Unix time in seconds a date can have (9999-12-31); a larger number is read as milliseconds.</summary>
    const long MaxUnixSeconds = 253_402_300_799;

    /// <summary>A date as ISO text, Unix seconds or Unix milliseconds; a number out of either range is no date.</summary>
    public static DateTimeOffset? Date(this JsonElement e, string name) => e.Get(name) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt64(out long n) => n switch
        {
            >= 0 and <= MaxUnixSeconds => DateTimeOffset.FromUnixTimeSeconds(n),
            > MaxUnixSeconds and <= MaxUnixSeconds * 1000 + 999 => DateTimeOffset.FromUnixTimeMilliseconds(n),
            _ => null,
        },
        { ValueKind: JsonValueKind.String } v when DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) => d,
        _ => null,
    };
}
