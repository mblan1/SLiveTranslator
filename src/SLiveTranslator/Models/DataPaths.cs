using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SLiveTranslator.Models;

/// <summary>
/// Where settings, cache, log and profiles live. Chosen once per run: a <c>portable.flag</c> next to the exe means
/// <c>&lt;exe&gt;\Data</c>; otherwise the folder named in <c>%APPDATA%\SLiveTranslator\location.json</c>; otherwise
/// <c>%APPDATA%\SLiveTranslator</c>.
/// </summary>
public sealed class DataPaths
{
    public const string AppFolderName = "SLiveTranslator";
    public const string PortableFlagName = "portable.flag";
    public const string LocationFileName = "location.json";

    /// <summary>An offline network share can block a folder check for the whole SMB timeout, before any window is up.</summary>
    static readonly TimeSpan FolderCheckTimeout = TimeSpan.FromSeconds(2);

    static DataPaths? _current;

    DataPaths(string root, bool isPortable, string? unavailableLocation = null)
    {
        Root = root;
        IsPortable = isPortable;
        UnavailableLocation = unavailableLocation;
    }

    public static DataPaths Current => LazyInitializer.EnsureInitialized(ref _current, Resolve);

    public string Root { get; }
    public bool IsPortable { get; }

    /// <summary>The folder location.json names when it could not be reached (a drive not plugged in), so <see cref="Root"/> is the default instead.</summary>
    public string? UnavailableLocation { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string CacheFile => Path.Combine(Root, "cache.json");
    public string LogFile => Path.Combine(Root, "log.txt");
    public string ProfilesDir => Path.Combine(Root, "Profiles");

    /// <summary>The characters sent to MyMemory today (its free quota is per day).</summary>
    public string MyMemoryUsageFile => Path.Combine(Root, "mymemory-usage.json");

    static DataPaths Resolve() =>
        Resolve(Path.GetDirectoryName(Environment.ProcessPath), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <param name="exeDir">Folder of the running exe; null when unknown.</param>
    /// <param name="appDataRoot">The roaming AppData folder (injectable so tests never touch the real one).</param>
    internal static DataPaths Resolve(string? exeDir, string appDataRoot)
    {
        if (!string.IsNullOrEmpty(exeDir) && File.Exists(Path.Combine(exeDir, PortableFlagName)))
            return new DataPaths(Path.Combine(exeDir, "Data"), isPortable: true);

        string defaultRoot = Path.Combine(appDataRoot, AppFolderName);
        if (ReadLocation(Path.Combine(defaultRoot, LocationFileName)) is not { } custom) return new DataPaths(defaultRoot, isPortable: false);
        return FolderReachable(custom)
            ? new DataPaths(custom, isPortable: false)
            : new DataPaths(defaultRoot, isPortable: false, unavailableLocation: custom);
    }

    /// <summary>
    /// The custom folder from location.json, or null when the file is missing, unreadable, or does not name a full path.
    /// Nothing is logged here: the log itself lives in the folder being resolved.
    /// </summary>
    static string? ReadLocation(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("DataDir", out var dir)
                || dir.ValueKind != JsonValueKind.String) return null;
            string? path = dir.GetString();
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    static bool FolderReachable(string path)
    {
        var check = Task.Run(() => Directory.Exists(path));
        return check.Wait(FolderCheckTimeout) && check.Result;
    }

    /// <summary>The paths of a data folder that was not resolved from the exe and AppData (a folder the user switched to).</summary>
    internal static DataPaths At(string root, bool isPortable) => new(root, isPortable);

    /// <summary>The app moved its data to <paramref name="paths"/> while running: every path asked for from now on is there.</summary>
    internal static void Use(DataPaths paths) => Volatile.Write(ref _current, paths);

    /// <summary>Points every path of this process at <paramref name="dir"/> so tests never write to the user's real data folder.</summary>
    internal static void UseForTests(string dir) => Use(new DataPaths(dir, isPortable: false));
}
