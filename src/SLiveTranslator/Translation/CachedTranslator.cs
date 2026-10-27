using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation;

/// <summary>Remembers translations on disk so repeated item names and lines never hit the network twice.</summary>
public sealed class CachedTranslator : ITranslator
{
    readonly ITranslator _inner;
    readonly string _path;
    readonly ConcurrentDictionary<string, string> _cache = new();
    int _dirty;

    public CachedTranslator(ITranslator inner, string path)
    {
        _inner = inner;
        _path = path;
        try
        {
            if (File.Exists(path))
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new())
                    _cache[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            Log.Write("cache load failed: " + ex.Message);
        }
    }

    public string Name => _inner.Name;
    public int Count => _cache.Count;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        var result = new string[texts.Count];
        var missing = new List<int>();
        for (int i = 0; i < texts.Count; i++)
        {
            if (_cache.TryGetValue(Key(sourceLang, targetLang, texts[i]), out var hit)) result[i] = hit;
            else missing.Add(i);
        }

        if (missing.Count > 0)
        {
            var translated = await _inner.TranslateAsync(missing.Select(i => texts[i]).ToList(), sourceLang, targetLang, ct);
            for (int j = 0; j < missing.Count; j++)
            {
                result[missing[j]] = translated[j];
                _cache[Key(sourceLang, targetLang, texts[missing[j]])] = translated[j];
            }
            Interlocked.Exchange(ref _dirty, 1);
        }
        return result;
    }

    public void Save()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_cache.ToDictionary(kv => kv.Key, kv => kv.Value)));
        }
        catch (Exception ex)
        {
            Log.Write("cache save failed: " + ex.Message);
        }
    }

    string Key(string src, string tgt, string text) => $"{_inner.Name}|{src}|{tgt}|{text.Trim()}";
}
