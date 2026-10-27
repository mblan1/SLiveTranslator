using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
/// <para>
/// A hand fix ("user" entry) answers first, whatever the provider. Each entry keeps the stamp of the game profile
/// parts its translation depended on (<see cref="ITranslator.ProfileStamp"/>); an entry whose stamp differs from the
/// one the profile gives the line now counts as missing, so a profile edit re-translates only the lines it can change.
/// While nothing can translate such a line, its earlier translation answers. Empty answers, answers that only repeat
/// the source, lines the engine left unanswered (<see cref="TranslateResult.FailedLines"/>) and MyMemory corpus
/// matches are never kept; an answer that repeats its source ("Hm.") and an LLM's empty answer (the line is not
/// language) are remembered until the app exits instead, so the line is not sent again meanwhile.
/// </para>
/// <para>
/// In a chain of providers each member keeps its own entries: the cache looks up the first one, and each later one is
/// wrapped in a <see cref="Bucket"/>. While the selected provider is down, a line the free services translated before
/// is answered from the cache, not sent to them again.
/// </para>
/// <para>
/// The file is version 2: {"version":2,"entries":[{p,s,t,src,tr,created,used,hits,edited,profileVer,profileStamp,answeredBy}]},
/// least recently used first ("profileStamp" only when there is one). A version-1 file (one flat
/// {"provider|src|tgt|text": translation} object) is read and cleaned up: "free|" entries (no provider asks for that
/// key since "free-mt"), empty entries and entries identical to their source go. A file of another version is kept
/// as cache.json.bak-v1 (or -v3 …) before the first save replaces it.
/// </para>
/// </summary>
public sealed class CachedTranslator : ITranslator
{
    public const int DefaultMaxEntries = 20_000;

    /// <summary><see cref="TranslateResult.AnsweredBy"/> of a line that came from the cache.</summary>
    public const string AnsweredFromCache = "cache";

    const int FileVersion = 2;
    const string LegacyFreeKey = "free";

    /// <summary>Answers remembered for the run at most (see <see cref="Keep"/>); the memo starts again when full.</summary>
    const int MaxRunAnswers = 2_000;

    static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromSeconds(30);

    /// <summary>Where an entry is kept; it holds the entry's own strings, so the source is not stored twice.</summary>
    readonly record struct EntryKey(string Provider, string Src, string Tgt, string Source);

    /// <summary>
    /// One translation. <see cref="Clock"/> is a logical clock: only the order matters, and the file keeps that order
    /// (least recently used first), so it survives a restart. <see cref="Used"/> is the wall time, for the user. The
    /// provider, languages and answeredBy take a few values only: every entry shares one string of each.
    /// </summary>
    sealed class Entry
    {
        public required string Provider { get; init => field = string.Intern(value); }
        public required string SourceLang { get; init => field = string.Intern(value); }
        public required string TargetLang { get; init => field = string.Intern(value); }
        public required string Source { get; init; }
        public required string Translation { get; init; }
        public required long Created { get; init; }
        public bool Edited { get; init; }
        public int ProfileVersion { get; init; }
        public string ProfileStamp { get; init; } = "";
        public string AnsweredBy { get; init => field = string.Intern(value); } = "";
        public long Used;
        public long Clock;
        public int Hits;

        public EntryKey Key => new(Provider, SourceLang, TargetLang, Source);

        public CacheEntry ToPublic() => new(Provider, SourceLang, TargetLang, Source, Translation, DateTimeOffset.FromUnixTimeMilliseconds(Created),
            DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref Used)), Volatile.Read(ref Hits), Edited, ProfileVersion, AnsweredBy);
    }

    readonly string _path;
    readonly TimeSpan _saveDelay;
    readonly TimeProvider _time;
    readonly ConcurrentDictionary<EntryKey, Entry> _cache = new();
    readonly ConcurrentDictionary<(EntryKey Key, string Stamp), string> _runAnswers = new();
    readonly Task _loaded;
    readonly object _saveGate = new();
    volatile ITranslator _inner;
    long _clock;
    int _dirty, _touched, _saveScheduled;

    /// <summary>Where the file found at load is copied before the first save replaces it; null when it needs no copy.</summary>
    string? _backupPath;

    public CachedTranslator(ITranslator inner, string path, int maxEntries = DefaultMaxEntries) : this(inner, path, maxEntries, DefaultSaveDelay)
    {
    }

    /// <param name="saveDelay">How long after the first change the cache is written.</param>
    internal CachedTranslator(ITranslator inner, string path, int maxEntries, TimeSpan saveDelay, TimeProvider? time = null)
    {
        _inner = inner;
        _path = path;
        MaxEntries = maxEntries;
        _saveDelay = saveDelay;
        _time = time ?? TimeProvider.System;
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

    public string V1BackupPath => BackupPath(1);
    public string CorruptCopyPath => _path + ".corrupt";

    /// <summary>Where a file of this version found at start is kept before the first save replaces it.</summary>
    public string BackupPath(int version) => _path + ".bak-v" + version;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
        (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

    public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct) =>
        AnswerAsync(_inner, request, handFixes: true, ct); // the same translator for the lookup and the answer, even if the provider changes meanwhile

    public string ProfileStamp(GameProfile? profile, string line) => _inner.ProfileStamp(profile, line);

    /// <summary>
    /// A later member of a chain of providers (the free services after the selected one), with its own entries: they
    /// answer before it is asked, and its answers are kept under its key. Hand fixes were looked up before the chain.
    /// </summary>
    public ITranslator Bucket(ITranslator member) => new BucketView(this, member);

    sealed class BucketView(CachedTranslator cache, ITranslator member) : ITranslator
    {
        public string Name => member.Name;

        public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            (await TranslateAsync(TranslateRequest.Plain(texts, sourceLang, targetLang), ct)).Texts;

        public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct) => cache.AnswerAsync(member, request, handFixes: false, ct);

        public string ProfileStamp(GameProfile? profile, string line) => member.ProfileStamp(profile, line);
    }

    /// <summary>
    /// Answers each line from the entries of the translator's key that are current for the profile, and asks the
    /// translator for the rest. Only answers under that key are kept here: another member of a chain keeps its own.
    /// </summary>
    async Task<TranslateResult> AnswerAsync(ITranslator translator, TranslateRequest request, bool handFixes, CancellationToken ct)
    {
        if (!_loaded.IsCompleted) await _loaded;
        string key = translator.Name;
        var lines = request.Lines;
        long now = Now();
        var texts = new string[lines.Count];
        var answeredBy = new string[lines.Count];
        var providers = new string[lines.Count];
        var stamps = new string[lines.Count];
        var stale = new Entry?[lines.Count];
        var missing = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            string source = lines[i].Trim();
            var entry = handFixes ? Find(ProviderKeys.User, request.Src, request.Tgt, source) : null;
            if (entry is null)
            {
                stamps[i] = translator.ProfileStamp(request.Profile, source);
                if (Find(key, request.Src, request.Tgt, source) is { } own)
                {
                    if (own.Edited || own.ProfileStamp == stamps[i]) entry = own;
                    else stale[i] = own;
                }
            }
            if (entry is not null)
            {
                Touch(entry, now);
                (texts[i], answeredBy[i], providers[i]) = (entry.Translation, AnsweredFromCache, entry.Provider);
            }
            else if (_runAnswers.TryGetValue((new EntryKey(key, request.Src, request.Tgt, source), stamps[i]), out string? answer))
            {
                (texts[i], answeredBy[i], providers[i]) = (answer, AnsweredFromCache, key);
            }
            else
            {
                missing.Add(i);
            }
        }
        if (missing.Count == 0)
            return new TranslateResult(texts, null, AnsweredFromCache) { Provider = providers.FirstOrDefault(), LineAnsweredBy = answeredBy };

        TranslateResult result;
        try
        {
            result = await translator.TranslateAsync(request with { Lines = missing.Select(i => lines[i]).ToList() }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && TranslatorUnavailableException.IsServiceFailure(ex) && missing.All(i => stale[i] is not null))
        {
            // Nothing can translate the lines now, but each was translated before the profile changed: better than none.
            Log.Once("cache stale " + key, $"cache: {key} cannot translate now ({ex.Message}); translations made with an earlier game profile answer",
                TimeSpan.FromMinutes(10), LogLevel.Warn);
            foreach (int i in missing)
            {
                Touch(stale[i]!, now);
                (texts[i], answeredBy[i], providers[i]) = (stale[i]!.Translation, AnsweredFromCache, key);
            }
            return new TranslateResult(texts, null, AnsweredFromCache) { Provider = providers[0], LineAnsweredBy = answeredBy };
        }

        bool added = false;
        bool judged = result.FixedSources is not null;
        for (int j = 0; j < missing.Count; j++)
        {
            int i = missing[j];
            (texts[i], answeredBy[i], providers[i]) = (result.Texts[j], result.AnsweredByAt(j), result.ProviderAt(j) ?? key);
            // Another member's bucket kept (or answered) it; a failed line has no answer to keep.
            if (providers[i] == key && answeredBy[i] != AnsweredFromCache && result.FailedLines?.Contains(j) != true)
                added |= Keep(new EntryKey(key, request.Src, request.Tgt, lines[i].Trim()), texts[i], answeredBy[i], stamps[i], request.Profile?.Version ?? 0, now, judged);
        }
        // Only new entries are worth a write; the order the hits changed goes along with it.
        if (added) MarkDirty();

        string[]? fixedSources = null;
        if (result.FixedSources is { } sources)
        {
            fixedSources = lines.ToArray();
            for (int j = 0; j < missing.Count && j < sources.Count; j++) fixedSources[missing[j]] = sources[j];
        }
        return new TranslateResult(texts, fixedSources, result.AnsweredBy)
        {
            Provider = providers[0], LineAnsweredBy = answeredBy, FailedLines = result.FailedLines?.Select(j => missing[j]).ToList(),
        };
    }

    /// <summary>
    /// Keeps a real translation. Two answers that are not kept are remembered for the run instead, so the line is not
    /// sent again meanwhile: one that repeats its source ("Hm."), and an empty one from an engine that judges each
    /// line (an LLM, which reports the fixed sources), whose "" says the line is not language. Any other empty answer
    /// is a failure that a later try may not repeat.
    /// </summary>
    /// <param name="judged">The answer came with <see cref="TranslateResult.FixedSources"/>.</param>
    /// <returns>Whether an entry was added.</returns>
    bool Keep(EntryKey key, string translation, string answeredBy, string stamp, int profileVersion, long now, bool judged)
    {
        if (!Keepable(key.Source, translation, answeredBy))
        {
            string text = translation.Trim();
            if (answeredBy != MyMemoryTranslator.AnsweredByCorpus && (text == key.Source || (judged && text.Length == 0)))
            {
                if (_runAnswers.Count >= MaxRunAnswers) _runAnswers.Clear();
                _runAnswers[(key, stamp)] = text;
            }
            return false;
        }
        // A hand edit stays, whatever answered meanwhile.
        if (_cache.TryGetValue(key, out var old) && old.Edited) return false;
        Put(new Entry
        {
            Provider = key.Provider, SourceLang = key.Src, TargetLang = key.Tgt, Source = key.Source, Translation = translation,
            Created = now, Used = now, ProfileVersion = profileVersion, ProfileStamp = stamp, AnsweredBy = answeredBy,
        });
        return true;
    }

    // ---- the cache manager (and the hand fix) ----

    /// <summary>The entries that match, most recently used first.</summary>
    public IReadOnlyList<CacheEntry> Snapshot(CacheFilter? filter = null)
    {
        _loaded.Wait();
        return _cache.Values
            .Where(e => filter is null || Matches(e, filter))
            .OrderByDescending(e => Volatile.Read(ref e.Clock))
            .Select(e => e.ToPublic())
            .ToList();
    }

    /// <summary>
    /// The user's own translation of a line: it answers first for every provider, and is never evicted. A text of
    /// several lines (a subtitle with a key hint) is cached line by line, so the fix needs as many lines. An empty
    /// translation removes the fix.
    /// </summary>
    public void SetUserOverride(string sourceLang, string targetLang, string source, string translation)
    {
        var sources = source.Split('\n');
        var texts = translation.Split('\n');
        if (texts.Length != sources.Length && translation.Trim().Length > 0)
            throw new ArgumentException($"The fix needs one line for each of the {sources.Length} lines of the source.", nameof(translation));
        _loaded.Wait();
        long now = Now();
        for (int i = 0; i < sources.Length; i++)
        {
            string line = sources[i].Trim();
            if (line.Length == 0) continue;
            string text = i < texts.Length ? texts[i].Trim() : "";
            if (text.Length == 0)
            {
                _cache.TryRemove(new EntryKey(ProviderKeys.User, sourceLang, targetLang, line), out _);
                continue;
            }
            Put(new Entry
            {
                Provider = ProviderKeys.User, SourceLang = sourceLang, TargetLang = targetLang, Source = line, Translation = text,
                Created = now, Used = now, Edited = true, AnsweredBy = ProviderKeys.User,
            });
        }
        MarkDirty();
    }

    /// <summary>
    /// Replaces a provider's translation with an edited one; it is never evicted or translated again. An empty
    /// translation removes the entry, as for a hand fix.
    /// </summary>
    public void Set(string provider, string sourceLang, string targetLang, string source, string translation)
    {
        if (translation.Trim().Length == 0)
        {
            Remove(provider, sourceLang, targetLang, source);
            return;
        }
        _loaded.Wait();
        long now = Now();
        source = source.Trim();
        var old = Find(provider, sourceLang, targetLang, source);
        Put(new Entry
        {
            Provider = provider, SourceLang = sourceLang, TargetLang = targetLang, Source = source, Translation = translation.Trim(),
            Created = old?.Created ?? now, Used = now, Edited = true, ProfileVersion = old?.ProfileVersion ?? 0, ProfileStamp = old?.ProfileStamp ?? "",
            AnsweredBy = ProviderKeys.User,
        });
        MarkDirty();
    }

    public bool Remove(string provider, string sourceLang, string targetLang, string source)
    {
        _loaded.Wait();
        bool removed = _cache.TryRemove(new EntryKey(provider, sourceLang, targetLang, source.Trim()), out _);
        if (removed) MarkDirty();
        return removed;
    }

    /// <param name="provider">The provider key, for <see cref="CacheScope.Provider"/>.</param>
    /// <returns>How many entries went.</returns>
    public int Clear(CacheScope scope, string? provider = null)
    {
        if (scope == CacheScope.Provider && provider is null) throw new ArgumentNullException(nameof(provider));
        _loaded.Wait();
        Func<Entry, bool> goes = scope switch
        {
            CacheScope.All => _ => true,
            CacheScope.Provider => e => e.Provider == provider,
            CacheScope.Orphans => e => ProviderKeys.IsOrphan(e.Provider),
            CacheScope.NonEdited => e => !e.Edited,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        int removed = 0;
        foreach (var pair in _cache)
            if (goes(pair.Value) && _cache.TryRemove(pair)) removed++;
        _runAnswers.Clear();
        if (removed > 0) MarkDirty();
        return removed;
    }

    public CacheStats Stats()
    {
        _loaded.Wait();
        var providers = _cache.Values
            .GroupBy(e => e.Provider)
            .Select(g => new ProviderCacheStats(g.Key, g.Count(), g.Count(e => e.Edited), g.Sum(SizeOf)))
            .OrderByDescending(p => p.Entries)
            .ToList();
        return new CacheStats(providers.Sum(p => p.Entries), providers.Sum(p => p.Bytes), providers);
    }

    /// <summary>Writes every entry to a file of the cache's own format.</summary>
    public void Export(string path)
    {
        _loaded.Wait();
        WriteFile(path, _cache.Values.OrderBy(e => Volatile.Read(ref e.Clock)).ToList());
    }

    /// <summary>
    /// Merges a cache file (either version) into this cache, as recently used entries. An imported entry replaces the
    /// one there, except that a hand-fixed or edited entry is never replaced by one that is not.
    /// </summary>
    /// <returns>How many entries were added or replaced.</returns>
    public int Import(string path)
    {
        _loaded.Wait();
        List<Entry> entries;
        using (var stream = File.OpenRead(path))
        using (var doc = JsonDocument.Parse(stream))
            entries = Parse(doc.RootElement, File.GetLastWriteTimeUtc(path), out _);
        int merged = 0;
        foreach (var entry in entries)
        {
            if (_cache.TryGetValue(entry.Key, out var existing) && existing.Edited && !entry.Edited) continue;
            Put(entry);
            merged++;
        }
        if (merged > 0) MarkDirty();
        return merged;
    }

    /// <summary>Writes the cache now if it changed or was used, waiting for a save in progress; for exit.</summary>
    public void Flush()
    {
        _loaded.Wait();
        Save();
    }

    // ---- entries ----

    Entry? Find(string provider, string src, string tgt, string source) =>
        _cache.TryGetValue(new EntryKey(provider, src, tgt, source), out var entry) ? entry : null;

    void Put(Entry entry)
    {
        entry.Clock = Interlocked.Increment(ref _clock);
        _cache[entry.Key] = entry;
    }

    /// <summary>A hit: its use and order are written with the next save, or on exit.</summary>
    void Touch(Entry entry, long now)
    {
        Volatile.Write(ref entry.Clock, Interlocked.Increment(ref _clock));
        Volatile.Write(ref entry.Used, now);
        Interlocked.Increment(ref entry.Hits);
        Volatile.Write(ref _touched, 1);
    }

    /// <summary>Worth keeping: a real translation. A hand fix may repeat its source on purpose ("Roach" stays "Roach").</summary>
    static bool Keepable(Entry entry) =>
        entry.Provider != LegacyFreeKey && (entry.Edited ? entry.Translation.Trim().Length > 0 : Keepable(entry.Source, entry.Translation, entry.AnsweredBy));

    static bool Keepable(string source, string translation, string answeredBy) =>
        !string.IsNullOrWhiteSpace(translation) && translation.Trim() != source.Trim() && answeredBy != MyMemoryTranslator.AnsweredByCorpus;

    static bool Matches(Entry e, CacheFilter filter) =>
        (filter.Provider is null || e.Provider == filter.Provider)
        && (filter.Edited is null || e.Edited == filter.Edited)
        && (string.IsNullOrEmpty(filter.Text) || e.Source.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)
                                              || e.Translation.Contains(filter.Text, StringComparison.OrdinalIgnoreCase));

    static long SizeOf(Entry e) =>
        Encoding.UTF8.GetByteCount(e.Source) + Encoding.UTF8.GetByteCount(e.Translation) + Encoding.UTF8.GetByteCount(e.Provider)
        + Encoding.UTF8.GetByteCount(e.AnsweredBy) + e.SourceLang.Length + e.TargetLang.Length + e.ProfileStamp.Length + 120; // field names and numbers

    long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    // ---- the file ----

    void Load()
    {
        try
        {
            // A save that crashed left at most a stale .tmp next to an intact cache.json; it is overwritten by the next save.
            if (!File.Exists(_path)) return;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            using var doc = JsonDocument.Parse(stream);
            var entries = Parse(doc.RootElement, File.GetLastWriteTimeUtc(_path), out int version);
            foreach (var entry in entries) Put(entry);
            // An older file, or one of a newer app that may hold what this one does not read, is kept as it was.
            if (version != FileVersion) _backupPath = BackupPath(version);
        }
        catch (Exception ex)
        {
            // The next save must not replace what could not be read without keeping a copy.
            _backupPath = CorruptCopyPath;
            Log.Error("cache load failed: " + ex.Message);
        }
    }

    /// <summary>The entries worth keeping from a file of either version, in file order.</summary>
    /// <param name="writtenUtc">When the file was written: the age a version-1 entry is given.</param>
    /// <param name="version">1 for the flat file of version 1.</param>
    static List<Entry> Parse(JsonElement root, DateTime writtenUtc, out int version)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("the file holds no JSON object");
        var entries = new List<Entry>();
        if (!root.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number)
        {
            version = 1;
            long written = new DateTimeOffset(writtenUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
            foreach (var property in root.EnumerateObject())
            {
                var parts = property.Name.Split('|', 4);
                if (parts.Length < 4 || property.Value.ValueKind != JsonValueKind.String) continue;
                entries.Add(new Entry
                {
                    Provider = parts[0], SourceLang = parts[1], TargetLang = parts[2], Source = parts[3].Trim(), Translation = property.Value.GetString()!,
                    Created = written, Used = written, Edited = parts[0] == ProviderKeys.User, AnsweredBy = parts[0] == ProviderKeys.User ? ProviderKeys.User : "",
                });
            }
        }
        else
        {
            version = versionElement.TryGetInt32(out int v) ? v : FileVersion + 1;
            if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array) throw new JsonException($"the version {version} file holds no entries");
            if (version > FileVersion) Log.Warn($"cache: the file is version {version}; fields this version does not know are not kept");
            foreach (var item in list.EnumerateArray())
                if (ReadEntry(item) is { } entry) entries.Add(entry);
        }
        return entries.Where(Keepable).ToList();
    }

    static Entry? ReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string? Text(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long Number(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
        if (Text("p") is not { } p || Text("s") is not { } s || Text("t") is not { } t || Text("src") is not { } src || Text("tr") is not { } tr) return null;
        return new Entry
        {
            Provider = p, SourceLang = s, TargetLang = t, Source = src.Trim(), Translation = tr,
            Created = Number("created"), Used = Number("used"), Hits = (int)Math.Clamp(Number("hits"), 0, int.MaxValue),
            Edited = item.TryGetProperty("edited", out var edited) && edited.ValueKind == JsonValueKind.True,
            ProfileVersion = (int)Math.Clamp(Number("profileVer"), 0, int.MaxValue), ProfileStamp = Text("profileStamp") ?? "",
            AnsweredBy = Text("answeredBy") ?? "",
        };
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

    /// <summary>Writes the file when an entry changed or, so the order of use and the hit counts survive, was used.</summary>
    void Save()
    {
        lock (_saveGate)
        {
            bool dirty = Interlocked.Exchange(ref _dirty, 0) == 1, touched = Interlocked.Exchange(ref _touched, 0) == 1;
            if (!dirty && !touched) return;
            try
            {
                if (_backupPath is { } backup && File.Exists(_path))
                {
                    File.Copy(_path, backup, overwrite: true);
                    Log.Write($"cache: kept the file found at start as {Path.GetFileName(backup)}");
                }
                _backupPath = null;
                WriteFile(_path, Evict());
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
    /// the hand-fixed and edited ones; an entry used while this runs is kept.
    /// </summary>
    List<Entry> Evict()
    {
        var entries = _cache.Select(kv => (Pair: kv, Clock: Volatile.Read(ref kv.Value.Clock))).OrderBy(e => e.Clock).ToList();
        int excess = entries.Count - Math.Max(0, MaxEntries);
        var kept = new List<Entry>(entries.Count);
        foreach (var (pair, clock) in entries)
        {
            if (excess > 0 && !pair.Value.Edited && Volatile.Read(ref pair.Value.Clock) == clock && _cache.TryRemove(pair))
            {
                excess--;
                continue;
            }
            kept.Add(pair.Value);
        }
        return kept;
    }

    /// <summary>Writes a version-2 file to a temporary file that then replaces the target.</summary>
    static void WriteFile(string path, List<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
        {
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                json.WriteStartObject();
                json.WriteNumber("version", FileVersion);
                json.WriteStartArray("entries");
                foreach (var e in entries)
                {
                    json.WriteStartObject();
                    json.WriteString("p", e.Provider);
                    json.WriteString("s", e.SourceLang);
                    json.WriteString("t", e.TargetLang);
                    json.WriteString("src", e.Source);
                    json.WriteString("tr", e.Translation);
                    json.WriteNumber("created", e.Created);
                    json.WriteNumber("used", Volatile.Read(ref e.Used));
                    json.WriteNumber("hits", Volatile.Read(ref e.Hits));
                    json.WriteBoolean("edited", e.Edited);
                    json.WriteNumber("profileVer", e.ProfileVersion);
                    if (e.ProfileStamp.Length > 0) json.WriteString("profileStamp", e.ProfileStamp);
                    json.WriteString("answeredBy", e.AnsweredBy);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
