using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>
/// Applies the shipped fix rules to every Vietnamese translation of a cache file and writes what they changed, in the
/// format of the reference validator's dry run. Runs only when SLT_DRYRUN_CACHE names the cache file (it is only
/// read) and SLT_DRYRUN_OUT the report to write; otherwise it returns at once.
/// </summary>
public class ProfileDryRun
{
    [Fact]
    [Trait("Category", "DryRun")]
    public void Shipped_fix_rules_on_a_cache_file()
    {
        string? cachePath = Environment.GetEnvironmentVariable("SLT_DRYRUN_CACHE");
        string? reportPath = Environment.GetEnvironmentVariable("SLT_DRYRUN_OUT");
        if (string.IsNullOrEmpty(cachePath) || string.IsNullOrEmpty(reportPath)) return;

        var rules = new FixRuleApplier(TestProfiles.Witcher3().FixRules);
        using var doc = JsonDocument.Parse(File.ReadAllText(cachePath));
        var report = new List<string>();
        var perRule = new SortedDictionary<int, int>();
        int n = 0, entries = 0, hits = 0;
        foreach (var (src, tgt, source, translation) in Entries(doc.RootElement))
        {
            n++;
            if (tgt != "vi") continue;
            var fired = new List<int>();
            string fixedText = rules.Apply(source, translation, fired);
            if (fired.Count == 0) continue;
            entries++;
            hits += fired.Count;
            foreach (int i in fired) perRule[i] = perRule.GetValueOrDefault(i) + 1;
            report.Add($"#{n} [{string.Join(",", fired)}] {translation.Normalize(System.Text.NormalizationForm.FormC)}");
            report.Add($"      -> {fixedText}");
            report.Add($"      en: {source}");
        }
        report.Add($"dry run: {hits} rule hits on {entries} of {n} entries; {perRule.Count} distinct rules fired");
        report.Add("per rule: " + string.Join(", ", perRule));
        File.WriteAllLines(reportPath, report);
    }

    static IEnumerable<(string Src, string Tgt, string Source, string Translation)> Entries(JsonElement root)
    {
        if (root.TryGetProperty("entries", out var list))
        {
            foreach (var e in list.EnumerateArray())
                yield return (e.GetProperty("s").GetString()!, e.GetProperty("t").GetString()!, e.GetProperty("src").GetString()!, e.GetProperty("tr").GetString()!);
            yield break;
        }
        foreach (var property in root.EnumerateObject())
        {
            var parts = property.Name.Split('|', 4);
            if (parts.Length == 4 && property.Value.ValueKind == JsonValueKind.String) yield return (parts[1], parts[2], parts[3], property.Value.GetString()!);
        }
    }
}
