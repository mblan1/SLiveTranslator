using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using SLiveTranslator.Models;
using SLiveTranslator.UI;

namespace SLiveTranslator.Translation;

/// <summary>
/// The game profiles in <see cref="DataPaths.ProfilesDir"/>. The profiles shipped with the app (embedded
/// Assets/Profiles/*.json; none since 2.1) are written there on first use. A shipped profile whose file the user never
/// changed is replaced when a newer version ships, and deleted when it is no longer shipped (<see cref="RetiredSeeds"/>);
/// a changed one is never overwritten or deleted. Whether a file is unchanged is told by the hash of what was written,
/// kept in <see cref="SeedStateFile"/>.
/// </summary>
public sealed class GameProfileStore
{
    /// <summary>Profiles shipped before and no longer: an unchanged copy is deleted at the next start.</summary>
    internal static readonly string[] RetiredSeeds = ["witcher3.json"];

    /// <summary>In <see cref="SeedStateFile"/>: the user removed this shipped profile, so it is never written again.</summary>
    const string RemovedMark = "removed";

    /// <summary><see cref="Models.TranslationSettings.ProfileId"/>: pick the profile by the game's process.</summary>
    public const string Auto = "";

    /// <summary><see cref="Models.TranslationSettings.ProfileId"/>: translate without a profile.</summary>
    public const string Off = "none";

    internal const string SeedStateFile = ".seeds.json";
    const string SeedResourcePrefix = "SLiveTranslator.Assets.Profiles.";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    string _dir;
    readonly IReadOnlyDictionary<string, byte[]> _seeds;
    readonly IReadOnlyCollection<string> _retired;
    readonly object _gate = new();
    volatile Loaded? _loaded;

    /// <summary>The profiles read and the file each came from; replaced whole, so readers never lock.</summary>
    /// <param name="Retired">Ids of the no-longer-shipped profiles deleted when the folder was read.</param>
    sealed record Loaded(GameProfile[] Profiles, Dictionary<GameProfile, string> Files, string[] Problems, string[] Retired);

    public GameProfileStore(string dir) : this(dir, EmbeddedSeeds(), RetiredSeeds)
    {
    }

    /// <param name="seeds">File name → content of the profiles shipped with the app.</param>
    /// <param name="retired">File names of profiles shipped before and no longer.</param>
    internal GameProfileStore(string dir, IReadOnlyDictionary<string, byte[]> seeds, IReadOnlyCollection<string> retired)
    {
        _dir = dir;
        _seeds = seeds;
        _retired = retired;
    }

    public IReadOnlyList<GameProfile> Profiles => Load().Profiles;

    /// <summary>Files that could not be read or saved; already logged.</summary>
    public IReadOnlyList<string> Problems => Load().Problems;

    /// <summary>Ids of profiles no longer shipped whose unchanged copy was deleted when the folder was read.</summary>
    public IReadOnlyList<string> Retired => Load().Retired;

    /// <summary>
    /// What a setting naming a profile that went away should become: <see cref="Auto"/> for a retired one (the user
    /// had not picked a profile of their own), null when the setting is fine as it is.
    /// </summary>
    public string? ReplacementFor(string profileId)
    {
        string id = profileId.Trim();
        if (id.Length == 0 || id.Equals(Off, StringComparison.OrdinalIgnoreCase) || Find(id) is not null) return null;
        return Retired.Contains(id, StringComparer.OrdinalIgnoreCase) ? Auto : null;
    }

    /// <summary>
    /// Deletes a profile's file. A shipped profile is recorded as removed, so it is not written again at the next start.
    /// </summary>
    /// <returns>False when no profile has this id.</returns>
    /// <exception cref="IOException">The file could not be deleted (nothing changed).</exception>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            var loaded = Load();
            var profile = loaded.Profiles.FirstOrDefault(p => p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (profile is null) return false;
            if (loaded.Files.TryGetValue(profile, out var file))
            {
                File.Delete(file);
                string name = Path.GetFileName(file);
                if (_seeds.ContainsKey(name)) MarkRemoved(name);
                Log.Write($"profiles: {name} ({profile.Id}) removed");
            }
            var files = new Dictionary<GameProfile, string>(loaded.Files, ReferenceEqualityComparer.Instance);
            files.Remove(profile);
            _loaded = loaded with { Profiles = loaded.Profiles.Where(p => !ReferenceEquals(p, profile)).ToArray(), Files = files };
            return true;
        }
    }

    void MarkRemoved(string fileName)
    {
        string statePath = Path.Combine(_dir, SeedStateFile);
        var state = ReadState(statePath);
        state[fileName] = RemovedMark;
        try
        {
            WriteAtomic(statePath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("profiles: " + SeedStateFile + " not saved, so a removed shipped profile comes back: " + ex.Message);
        }
    }

    public GameProfile? Find(string id) =>
        Profiles.FirstOrDefault(p => p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The profile for this setting and game, or null when none applies.</summary>
    /// <param name="profileId"><see cref="Auto"/>, <see cref="Off"/>, or a profile id.</param>
    /// <param name="processName">The game's executable name; null when the game is not known.</param>
    /// <returns>Null also when the profile is written for another language pair.</returns>
    public GameProfile? Resolve(string profileId, string? processName, string src, string tgt)
    {
        string id = profileId.Trim();
        if (id.Equals(Off, StringComparison.OrdinalIgnoreCase)) return null;
        var profile = id.Length > 0 ? Find(id)
            : processName is null ? null
            : Profiles.FirstOrDefault(p => p.MatchesProcess(processName));
        return profile is not null && profile.Fits(src, tgt) ? profile : null;
    }

    /// <summary>
    /// Writes an edited or new profile; its version goes up, so its cached translations are made again. A copy edited
    /// apart from the profile in use (the settings page, an import) replaces the profile with its id, in that one's
    /// file, and its version goes above that one's: translations running meanwhile never see a half-edited profile.
    /// A profile with an id of its own gets a file of its own, never one already there (see <see cref="FreeFile"/>).
    /// </summary>
    public void Save(GameProfile profile)
    {
        if (profile.Id.Length == 0 || profile.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.Id.StartsWith('.'))
            throw new ArgumentException($"\"{profile.Id}\" cannot be a profile id: use letters, digits and dashes.", nameof(profile));
        lock (_gate)
        {
            var loaded = Load();
            var replaced = loaded.Files.ContainsKey(profile)
                ? null
                : loaded.Profiles.FirstOrDefault(p => p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
            string file = loaded.Files.TryGetValue(replaced ?? profile, out var known) ? known : FreeFile(loaded, profile.Id);
            int version = profile.Version;
            profile.Version = Math.Max(profile.Version, replaced?.Version ?? 0) + 1;
            try
            {
                Directory.CreateDirectory(_dir);
                WriteAtomic(file, JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions));
            }
            catch
            {
                profile.Version = version; // not saved: the cached translations still belong to it
                throw;
            }
            var files = new Dictionary<GameProfile, string>(loaded.Files, ReferenceEqualityComparer.Instance);
            if (replaced is not null) files.Remove(replaced);
            files[profile] = file;
            var profiles = replaced is not null ? loaded.Profiles.Select(p => ReferenceEquals(p, replaced) ? profile : p).ToArray()
                : loaded.Profiles.Contains(profile) ? loaded.Profiles
                : [.. loaded.Profiles, profile];
            _loaded = loaded with { Profiles = profiles, Files = files };
        }
    }

    /// <summary>
    /// The data folder moved and its profiles were copied along: each loaded profile is saved to its file's copy in
    /// <paramref name="dir"/> from now on, so a profile open in the editor stays the one in use.
    /// </summary>
    public void Relocate(string dir)
    {
        lock (_gate)
        {
            string old = _dir;
            _dir = dir;
            if (_loaded is { } loaded)
                _loaded = loaded with
                {
                    Files = loaded.Files.ToDictionary(f => f.Key, f => Path.Combine(dir, Path.GetRelativePath(old, f.Value)),
                        (IEqualityComparer<GameProfile>)ReferenceEqualityComparer.Instance),
                };
        }
    }

    /// <summary>"id.json", else "id-2.json" …: a file that could not be read, or one that holds a profile with another id, is the user's and is never written over.</summary>
    string FreeFile(Loaded loaded, string id)
    {
        var taken = new HashSet<string>(loaded.Files.Values, StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(_dir, id + ".json");
        for (int n = 2; taken.Contains(path) || File.Exists(path); n++) path = Path.Combine(_dir, $"{id}-{n}.json");
        return path;
    }

    /// <summary>An id for a new profile that no profile has and no file in the folder is named after: "custom", "custom-2" …</summary>
    public string NewId(string stem)
    {
        string id = stem;
        for (int n = 2; Find(id) is not null || File.Exists(Path.Combine(_dir, id + ".json")); n++) id = $"{stem}-{n}";
        return id;
    }

    /// <summary>A deep copy to edit apart from the profile in use; <see cref="Save"/> then replaces that one.</summary>
    public static GameProfile Copy(GameProfile profile) => Parse(JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions));

    /// <summary>
    /// Reads a profile as a text editor may have saved it: with a UTF-8 byte order mark (Notepad, Visual Studio,
    /// PowerShell 5), or with null for a field or list item (<see cref="GameProfile.Normalized"/>).
    /// </summary>
    public static GameProfile Parse(ReadOnlySpan<byte> json)
    {
        if (json.StartsWith(Utf8Bom)) json = json[Utf8Bom.Length..];
        return (JsonSerializer.Deserialize<GameProfile>(json, JsonOptions) ?? throw new JsonException("the file holds no profile")).Normalized();
    }

    static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>The profiles shipped with the app, by file name.</summary>
    internal static IReadOnlyDictionary<string, byte[]> EmbeddedSeeds()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var seeds = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(SeedResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            seeds[name[SeedResourcePrefix.Length..]] = copy.ToArray();
        }
        return seeds;
    }

    Loaded Load()
    {
        if (_loaded is { } loaded) return loaded;
        lock (_gate)
        {
            return _loaded ??= Read();
        }
    }

    Loaded Read()
    {
        var problems = new List<string>();
        var retired = new List<string>();
        try
        {
            Directory.CreateDirectory(_dir);
            WriteSeeds(problems, retired);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem(problems, Loc.F("Profiles.PrepareFailed", _dir, ex.Message));
        }

        var profiles = new List<GameProfile>();
        var files = new Dictionary<GameProfile, string>(ReferenceEqualityComparer.Instance);
        var paths = Directory.Exists(_dir)
            ? Directory.EnumerateFiles(_dir, "*.json").Where(f => !Path.GetFileName(f).StartsWith('.')).Order(StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();
        foreach (var path in paths)
        {
            try
            {
                var profile = Parse(File.ReadAllBytes(path));
                if (profile.Id.Length == 0) throw new JsonException("it has no id");
                if (profiles.Any(p => p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    Problem(problems, Loc.F("Profiles.DuplicateId", Path.GetFileName(path), profile.Id));
                    continue;
                }
                profiles.Add(profile);
                files[profile] = path;
            }
            catch (Exception ex)
            {
                // The file is the user's to edit: whatever is wrong with it, it is reported and the other profiles still load.
                Problem(problems, Loc.F("Profiles.Unreadable", Path.GetFileName(path), ex.Message));
            }
        }
        return new Loaded(profiles.ToArray(), files, problems.ToArray(), retired.ToArray());
    }

    /// <summary>
    /// Writes each shipped profile that is missing (unless the user removed it), or outdated and never changed by the
    /// user; deletes each retired one the user never changed.
    /// </summary>
    /// <param name="retired">Gets the ids of the retired profiles deleted.</param>
    void WriteSeeds(List<string> problems, List<string> retired)
    {
        string statePath = Path.Combine(_dir, SeedStateFile);
        var state = ReadState(statePath);
        bool changed = RetireSeeds(state, retired);
        foreach (var (fileName, seed) in _seeds)
        {
            string path = Path.Combine(_dir, fileName);
            string seedHash = Hash(seed);
            if (state.TryGetValue(fileName, out var mark) && mark == RemovedMark) continue;
            if (File.Exists(path))
            {
                byte[] current = File.ReadAllBytes(path);
                bool untouched = state.TryGetValue(fileName, out var written) && written == Hash(current);
                if (!untouched || !IsNewer(seed, current)) continue;
                Log.Write($"profiles: {fileName} updated to the version shipped with this app");
            }
            WriteAtomic(path, seed);
            state[fileName] = seedHash;
            changed = true;
        }
        if (!changed) return;
        try
        {
            WriteAtomic(statePath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the record a shipped file counts as changed by the user: it is kept, never overwritten.
            Problem(problems, Loc.F("Profiles.SeedStateNotSaved", ex.Message));
        }
    }

    /// <summary>
    /// A profile shipped before and no longer: its copy is deleted when it is still exactly as written (the hash in the
    /// state), kept when the user changed it; either way it is no longer tracked. A file that cannot be deleted stays
    /// tracked, so the next start tries again.
    /// </summary>
    /// <returns>Whether the state changed.</returns>
    bool RetireSeeds(Dictionary<string, string> state, List<string> retired)
    {
        bool changed = false;
        foreach (var fileName in _retired)
        {
            if (_seeds.ContainsKey(fileName) || !state.TryGetValue(fileName, out var written)) continue;
            string path = Path.Combine(_dir, fileName);
            try
            {
                if (written != RemovedMark && File.Exists(path))
                {
                    byte[] current = File.ReadAllBytes(path);
                    if (Hash(current) == written)
                    {
                        string? id = IdOf(current);
                        File.Delete(path);
                        if (id is not null) retired.Add(id);
                        Log.Write($"profiles: {fileName} is no longer shipped and was never changed: deleted");
                    }
                    else Log.Write($"profiles: {fileName} is no longer shipped; kept, as it was changed");
                }
                state.Remove(fileName);
                changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"profiles: {fileName} could not be deleted: {ex.Message}");
            }
        }
        return changed;
    }

    static string? IdOf(byte[] json)
    {
        try
        {
            return Parse(json).Id is { Length: > 0 } id ? id : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static bool IsNewer(byte[] seed, byte[] current)
    {
        try
        {
            return Parse(seed).Version > Parse(current).Version;
        }
        catch (Exception)
        {
            return false; // a file that cannot be read is left for the user to look at; reading the profiles reports it
        }
    }

    static Dictionary<string, string> ReadState(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path), JsonOptions)
                       is { } state ? new Dictionary<string, string>(state, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("profiles: " + SeedStateFile + " cannot be read: " + ex.Message);
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    static void WriteAtomic(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    static void Problem(List<string> problems, string message)
    {
        problems.Add(message);
        Log.Error("profiles: " + message);
    }
}
