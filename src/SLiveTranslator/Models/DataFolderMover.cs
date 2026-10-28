using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;

namespace SLiveTranslator.Models;

/// <summary>Why a folder cannot take the data (see <see cref="DataFolderMover.Check"/>).</summary>
public enum FolderProblem { None, NotFullPath, SameFolder, InsideCurrent, InsideAppFolder, NotWritable, NoSpace, NotEmpty }

/// <summary>
/// Copies or moves the app's data to another folder and points the app at it: location.json in the default folder,
/// or portable.flag next to the exe. The data only goes to a folder that is empty or holds the app's data already
/// (<see cref="DataFolderIn"/> picks a subfolder otherwise), and only the app's own files are touched
/// (<see cref="DataFiles"/>). A copy goes to a staging folder inside the target first and is put in place only when
/// every file was copied; the files it replaces are moved aside and put back if putting it in place fails. Cancelled
/// or failed, it leaves the target as it was, and the source is only read.
/// </summary>
/// <param name="appDataRoot">The roaming AppData folder, where location.json lives (tests give a temp folder).</param>
/// <param name="exeDir">The folder of the running exe, for portable.flag; null when unknown.</param>
/// <param name="freeSpace">Free bytes on the drive of a folder; null when unknown (tests give their own).</param>
public sealed class DataFolderMover(string appDataRoot, string? exeDir, Func<string, long?>? freeSpace = null)
{
    const string StagingName = ".slt-copy";
    const string AsideName = ".slt-old";
    const string ProbeName = ".slt-write-test";
    /// <summary>Names the app's own work files start with; they do not make a folder "not empty".</summary>
    const string WorkPrefix = ".slt-";

    static readonly Regex SettingsMark = new("\"(SchemaVersion|SubtitleIntervalMs|HotkeyTranslateScreen)\"\\s*:", RegexOptions.CultureInvariant);
    static readonly Regex CacheMark = new("\"version\"\\s*:\\s*\\d+\\s*,\\s*\"entries\"|^\\s*\\{\\s*\"[a-z0-9-]+(/[^\"|]+)?\\|[a-zA-Z-]+\\|[a-zA-Z-]+\\|",
        RegexOptions.CultureInvariant);

    /// <summary>Room left over on the target besides the data, for the cache and log to grow.</summary>
    const long SpaceMargin = 16L * 1024 * 1024;

    readonly Func<string, long?> _freeSpace = freeSpace ?? DriveFreeSpace;

    /// <summary>%APPDATA%\SLiveTranslator: used when nothing else is named, and where location.json is kept.</summary>
    public string DefaultRoot => Path.Combine(appDataRoot, DataPaths.AppFolderName);

    public string LocationFile => Path.Combine(DefaultRoot, DataPaths.LocationFileName);

    /// <summary>&lt;exe&gt;\Data; null when the exe's folder is unknown.</summary>
    public string? PortableRoot => exeDir is null ? null : Path.Combine(exeDir, "Data");

    /// <summary>The folder in use without portable.flag: the one location.json names, else <see cref="DefaultRoot"/>.</summary>
    public string InstalledRoot => DataPaths.Resolve(null, appDataRoot).Root;

    public long? FreeSpace(string folder) => _freeSpace(folder);

    /// <summary>Whether <paramref name="target"/> can take the data of <paramref name="source"/>; creates no folder that was not there.</summary>
    /// <param name="portable">The target is the portable folder, the one place inside the app's folder allowed.</param>
    public FolderProblem Check(string source, string target, bool portable)
    {
        if (!Path.IsPathFullyQualified(target)) return FolderProblem.NotFullPath;
        string to = Full(target), from = Full(source);
        if (Same(to, from)) return FolderProblem.SameFolder;
        if (Inside(to, from)) return FolderProblem.InsideCurrent;
        // The same folder by another way (a junction, a symlink, a subst drive, a share): a move would delete the data.
        if (Alias(from, to) is { } alias) return alias;
        // An update or uninstall replaces the program's folder, and Program Files is not writable without admin rights.
        if (!portable && exeDir is not null && (Same(to, Full(exeDir)) || Inside(to, Full(exeDir)))) return FolderProblem.InsideAppFolder;
        if (Directory.Exists(to) && !IsEmpty(to) && !HasData(to) && !Same(to, Full(DefaultRoot))) return FolderProblem.NotEmpty;
        if (!Writable(to)) return FolderProblem.NotWritable;
        if (_freeSpace(to) is { } free && free < Size(from) + SpaceMargin) return FolderProblem.NoSpace;
        return FolderProblem.None;
    }

    /// <summary>
    /// The data folder for a folder the user picked: the folder itself when it does not exist yet, is empty or holds the
    /// app's data; otherwise a "SLiveTranslator" folder in it, so the data never mixes with another program's files.
    /// </summary>
    public static string DataFolderIn(string picked)
    {
        string full = Full(picked);
        return !Directory.Exists(full) || IsEmpty(full) || HasData(full) ? full : Path.Combine(full, DataPaths.AppFolderName);
    }

    /// <summary>
    /// The folder holds SLiveTranslator data, told by what the files hold and not by their names ("settings.json" and
    /// "log.txt" are common names): settings of either version, a cache of either format, or the record of the game
    /// profiles the app shipped.
    /// </summary>
    public static bool HasData(string dir)
    {
        var paths = DataPaths.At(dir, isPortable: false);
        return Starts(paths.SettingsFile, SettingsMark) || Starts(paths.CacheFile, CacheMark)
               || File.Exists(Path.Combine(paths.ProfilesDir, GameProfileStore.SeedStateFile));
    }

    /// <summary>The start of the file matches: settings and the head of a cache say what they are within the first few KB.</summary>
    static bool Starts(string file, Regex mark)
    {
        try
        {
            if (!File.Exists(file)) return false;
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var head = new char[16 * 1024];
            return mark.IsMatch(new string(head, 0, reader.ReadBlock(head, 0, head.Length)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool IsEmpty(string dir) =>
        !Directory.EnumerateFileSystemEntries(dir).Any(e => !Path.GetFileName(e).StartsWith(WorkPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="target"/> is <paramref name="source"/>, or inside it, by another path: a file of a unique
    /// name made in the source shows up there. Null when it does not, or when the source cannot be written to tell.
    /// </summary>
    static FolderProblem? Alias(string source, string target)
    {
        if (!Directory.Exists(source)) return null;
        string name = WorkPrefix + "same-" + Guid.NewGuid().ToString("N");
        string probe = Path.Combine(source, name);
        try
        {
            File.WriteAllText(probe, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        try
        {
            for (string? dir = target; dir is not null; dir = Path.GetDirectoryName(dir))
                if (File.Exists(Path.Combine(dir, name))) return Same(dir, target) ? FolderProblem.SameFolder : FolderProblem.InsideCurrent;
            return null;
        }
        finally
        {
            TryDelete(probe);
        }
    }

    /// <summary>
    /// The app's files in a data folder, relative to it: settings and cache with their backups, the log, the usage
    /// count and the game profiles. Temporary files and location.json (which belongs to the default folder) are not.
    /// </summary>
    public static IReadOnlyList<string> DataFiles(string root)
    {
        if (!Directory.Exists(root)) return [];
        var paths = DataPaths.At(root, isPortable: false);
        string settings = Path.GetFileName(paths.SettingsFile), cache = Path.GetFileName(paths.CacheFile);
        var single = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileName(paths.LogFile), Path.GetFileName(LogWriter.RotatedPath(paths.LogFile)), Path.GetFileName(paths.MyMemoryUsageFile),
        };
        bool Ours(string name) =>
            !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            && (single.Contains(name) || name.Equals(settings, StringComparison.OrdinalIgnoreCase) || name.Equals(cache, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(settings + ".", StringComparison.OrdinalIgnoreCase) || name.StartsWith(cache + ".", StringComparison.OrdinalIgnoreCase));
        var files = Directory.EnumerateFiles(root).Select(Path.GetFileName).OfType<string>().Where(Ours).ToList();
        if (Directory.Exists(paths.ProfilesDir))
            files.AddRange(Directory.EnumerateFiles(paths.ProfilesDir, "*.json").Select(f => Path.GetRelativePath(root, f)));
        return files.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Bytes of the app's files in the folder.</summary>
    public static long Size(string root) => DataFiles(root).Sum(f => FileSize(Path.Combine(root, f)));

    /// <summary>
    /// Copies the app's files of <paramref name="source"/> into <paramref name="target"/>, replacing the ones there; the
    /// target's own data files that the source does not have go too, so it holds exactly the source's data.
    /// </summary>
    /// <param name="progress">The share copied, 0 to 1, reported from a worker thread.</param>
    /// <returns>The files copied, relative.</returns>
    /// <exception cref="OperationCanceledException">Cancelled: the target is as it was.</exception>
    public Task<IReadOnlyList<string>> CopyAsync(string source, string target, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() => Copy(Full(source), Full(target), progress, ct), ct);

    static IReadOnlyList<string> Copy(string source, string target, IProgress<double>? progress, CancellationToken ct)
    {
        var files = DataFiles(source);
        long total = Math.Max(1, files.Sum(f => FileSize(Path.Combine(source, f))));
        string staging = Path.Combine(target, StagingName);
        TryDeleteFolder(staging);
        try
        {
            long done = 0;
            var buffer = new byte[81920];
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                string from = Path.Combine(source, file), to = Path.Combine(staging, file);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                // The app may append to its log or replace its cache meanwhile: read without blocking either.
                using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        done += read;
                        progress?.Report((double)done / total);
                    }
                }
                File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from)); // a version-1 cache dates its entries by it
            }
            ct.ThrowIfCancellationRequested();
            PutInPlace(staging, target, files);
            progress?.Report(1);
            return files;
        }
        finally
        {
            TryDeleteFolder(staging);
        }
    }

    /// <summary>
    /// Moves the staged files into the target. When the target holds the app's data, all of its data files go aside
    /// first (those the source does not have too, so the target ends up with exactly the source's data); a failure puts
    /// them back. In any other folder a file of the same name is another program's and stops the copy untouched.
    /// </summary>
    static void PutInPlace(string staging, string target, IReadOnlyList<string> files)
    {
        var existing = DataFiles(target);
        bool ours = HasData(target);
        if (!ours && existing.Intersect(files, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } clash)
            throw new IOException($"{Path.Combine(target, clash)} belongs to another program.");

        string aside = Path.Combine(target, AsideName);
        TryDeleteFolder(aside);
        var movedAside = new List<string>();
        var placed = new List<string>();
        try
        {
            foreach (var file in ours ? existing : [])
            {
                MoveFile(Path.Combine(target, file), Path.Combine(aside, file));
                movedAside.Add(file);
            }
            foreach (var file in files)
            {
                MoveFile(Path.Combine(staging, file), Path.Combine(target, file));
                placed.Add(file);
            }
        }
        catch
        {
            foreach (var file in placed) TryDelete(Path.Combine(target, file));
            foreach (var file in movedAside)
            {
                try
                {
                    MoveFile(Path.Combine(aside, file), Path.Combine(target, file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // stays in the aside folder, which is then kept
                }
            }
            DeleteEmptyFolders(aside);
            throw;
        }
        TryDeleteFolder(aside);
        DeleteEmptyFolders(DataPaths.At(target, isPortable: false).ProfilesDir); // the source had no profiles
    }

    static void MoveFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
    }

    /// <summary>
    /// After a move: deletes the source's files whose copy is in the target, the same size, and keeps any other (one
    /// written after the copy, or one another program holds). Deletes nothing when the two are one folder after all.
    /// </summary>
    /// <returns>The files left in the source.</returns>
    public static IReadOnlyList<string> DeleteMoved(string source, string target, IReadOnlyList<string> files)
    {
        if (Alias(Full(source), Full(target)) is not null) return files;
        var copied = files.Where(f => File.Exists(Path.Combine(target, f)) && FileSize(Path.Combine(source, f)) == FileSize(Path.Combine(target, f))).ToList();
        return [.. files.Except(copied, StringComparer.OrdinalIgnoreCase), .. Delete(source, copied)];
    }

    /// <summary>Deletes the files (relative to <paramref name="root"/>), and the profiles folder once it is empty.</summary>
    /// <returns>The files that could not be deleted (in use by another program).</returns>
    public static IReadOnlyList<string> Delete(string root, IEnumerable<string> files)
    {
        var left = new List<string>();
        foreach (var file in files)
        {
            try
            {
                File.Delete(Path.Combine(root, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(file);
            }
        }
        string profiles = DataPaths.At(root, isPortable: false).ProfilesDir;
        try
        {
            if (Directory.Exists(profiles) && !Directory.EnumerateFileSystemEntries(profiles).Any()) Directory.Delete(profiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept: something else was put there meanwhile
        }
        return left;
    }

    /// <summary>Names <paramref name="target"/> in location.json; for the default folder the file is removed, since nothing needs naming.</summary>
    public void WriteLocation(string target)
    {
        if (Same(Full(target), Full(DefaultRoot)))
        {
            if (File.Exists(LocationFile)) File.Delete(LocationFile);
            return;
        }
        Directory.CreateDirectory(DefaultRoot);
        string tmp = LocationFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { SchemaVersion = 1, DataDir = Full(target) }));
        File.Move(tmp, LocationFile, overwrite: true);
    }

    /// <summary>Creates or removes portable.flag next to the exe.</summary>
    /// <exception cref="InvalidOperationException">The exe's folder is unknown.</exception>
    public void SetPortable(bool on)
    {
        string flag = Path.Combine(exeDir ?? throw new InvalidOperationException("The folder of the running program is not known."), DataPaths.PortableFlagName);
        if (on) File.WriteAllText(flag, "");
        else if (File.Exists(flag)) File.Delete(flag);
    }

    static bool Writable(string dir)
    {
        bool existed = Directory.Exists(dir);
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ProbeName);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (!existed) TryDeleteFolder(dir, recursive: false);
        }
    }

    static long? DriveFreeSpace(string folder)
    {
        try
        {
            return Path.GetPathRoot(folder) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // a network share has no drive letter to ask
        }
    }

    /// <summary>Bytes of the file; 0 when it went away meanwhile or cannot be read.</summary>
    public static long FileSize(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    static void TryDeleteFolder(string dir, bool recursive = true)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left behind; the next copy clears the staging folder first
        }
    }

    /// <summary>Removes the folder and its subfolders where they hold no file; a file still in them keeps them.</summary>
    static void DeleteEmptyFolders(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.EnumerateDirectories(dir)) DeleteEmptyFolders(sub);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) TryDeleteFolder(dir, recursive: false);
    }

    static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a probe or a staged copy left behind does no harm
        }
    }

    static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A drive root keeps its separator ("E:\"), so it is not added twice.</summary>
    static bool Inside(string child, string parent) =>
        child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && !Same(child, parent);
}
