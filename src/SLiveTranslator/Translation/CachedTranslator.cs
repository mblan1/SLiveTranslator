using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation;

/// <summary>
/// Remembers translations on disk so repeated item names and lines never hit the network twice. Neither the UI nor a
/// translation ever waits for the disk: the file is read and written on the thread pool. A save comes a while after
/// the first change (so a burst of lines is written once), writes a temporary file and then replaces cache.json in one
/// step, so a crash never leaves a broken cache. Above <see cref="MaxEntries"/> the least recently used entries go.
/// </summary>
public sealed class CachedTranslator : ITranslator
{
    public const int DefaultMaxEntries = 20_000;

    /// <summary>Keys of translations the user fixed by hand; they are never evicted.</summary>
    internal const string UserPrefix = "user|";

    static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One translation. <see cref="LastUsed"/> is a logical clock, not wall time: only the order matters, and the file
    /// keeps that order (least recently used first), so it survives a restart.
    /// </summary>
    sealed class Entry(string translation, long lastUsed)
    {
        public string Translation { get; } = translation;
        public long LastUsed = lastUsed;
    }

    readonly string _path;
    readonly TimeSpan _saveDelay;
    readonly ConcurrentDictionary<string, Entry> _cache = new();
    readonly Task _loaded;
    readonly object _saveGate = new();
    volatile ITranslator _inner;
    long _clock;
    int _dirty, _saveScheduled;

    public CachedTranslator(ITranslator inner, string path, int maxEntries = DefaultMaxEntries) : this(inner, path, maxEntries, DefaultSaveDelay)
    {
    }

    /// <param name="saveDelay">How long after the first change the cache is written.</param>
    internal CachedTranslator(ITranslator inner, string path, int maxEntries, TimeSpan saveDelay)
    {
        _inner = inner;
        _path = path;
        MaxEntries = maxEntries;
        _saveDelay = saveDelay;
        _loaded = Task.Run(Load);
    }

    /// <summary>The translator asked on a miss. Replacing it keeps the loaded cache; its name is part of every key.</summary>
    public ITranslator Inner
    {
        get => _inner;
        set => _inner = value;
    }

    /// <summary>Entries kept when the cache is saved (<see cref="StorageSettings.CacheMaxEntries"/>).</summary>
    public int MaxEntries { get; set; }

    public string Name => _inner.Name;
    public int Count => _cache.Count;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        if (!_loaded.IsCompleted) await _loaded;
        var inner = _inner; // the same one for the lookup and the answer, even if the provider changes meanwhile
        var result = new string[texts.Count];
        var missing = new List<int>();
        for (int i = 0; i < texts.Count; i++)
        {
            if (_cache.TryGetValue(Key(inner, sourceLang, targetLang, texts[i]), out var hit))
            {
                hit.LastUsed = Interlocked.Increment(ref _clock);
                result[i] = hit.Translation;
            }
            else
            {
                missing.Add(i);
            }
        }

        if (missing.Count > 0)
        {
            var translated = await inner.TranslateAsync(missing.Select(i => texts[i]).ToList(), sourceLang, targetLang, ct);
            for (int j = 0; j < missing.Count; j++)
            {
                result[missing[j]] = translated[j];
                _cache[Key(inner, sourceLang, targetLang, texts[missing[j]])] = new Entry(translated[j], Interlocked.Increment(ref _clock));
            }
            // Only new entries are worth a write; the order the hits changed goes along with it.
            MarkDirty();
        }
        return result;
    }

    /// <summary>Writes the cache now if it changed, waiting for a save in progress; for exit.</summary>
    public void Flush()
    {
        _loaded.Wait();
        Save();
    }

    static string Key(ITranslator inner, string src, string tgt, string text) => $"{inner.Name}|{src}|{tgt}|{text.Trim()}";

    void Load()
    {
        try
        {
            // A save that crashed left at most a stale .tmp next to an intact cache.json; it is overwritten by the next save.
            if (!File.Exists(_path)) return;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            using var doc = JsonDocument.Parse(stream);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    _cache[property.Name] = new Entry(property.Value.GetString()!, ++_clock);
            }
        }
        catch (Exception ex)
        {
            Log.Error("cache load failed: " + ex.Message);
        }
    }

    void MarkDirty()
    {
        Volatile.Write(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _saveScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(_saveDelay);
            Volatile.Write(ref _saveScheduled, 0); // a change while saving schedules the next save
            Save();
        });
    }

    void Save()
    {
        lock (_saveGate)
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            try
            {
                Write(Evict());
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _dirty, 1); // tried again with the next save
                Log.Error("cache save failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// The entries to keep, least recently used first. Above <see cref="MaxEntries"/> the oldest are dropped, except
    /// the hand-fixed ones; an entry used while this runs is kept.
    /// </summary>
    List<KeyValuePair<string, Entry>> Evict()
    {
        var entries = _cache.Select(kv => (Pair: kv, Used: Volatile.Read(ref kv.Value.LastUsed))).OrderBy(e => e.Used).ToList();
        int excess = entries.Count - Math.Max(0, MaxEntries);
        var kept = new List<KeyValuePair<string, Entry>>(entries.Count);
        foreach (var (pair, used) in entries)
        {
            if (excess > 0 && !pair.Key.StartsWith(UserPrefix, StringComparison.Ordinal)
                && Volatile.Read(ref pair.Value.LastUsed) == used && _cache.TryRemove(pair))
            {
                excess--;
                continue;
            }
            kept.Add(pair);
        }
        return kept;
    }

    /// <summary>The same flat {key: translation} file as before, written to a temporary file that then replaces it.</summary>
    void Write(List<KeyValuePair<string, Entry>> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string tmp = _path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
        {
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                json.WriteStartObject();
                foreach (var (key, entry) in entries) json.WriteString(key, entry.Translation);
                json.WriteEndObject();
            }
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
