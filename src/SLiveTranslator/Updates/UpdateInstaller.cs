using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SLiveTranslator.Models;

namespace SLiveTranslator.Updates;

/// <summary>
/// Puts a downloaded exe in place of the running one and cleans up after it. Windows lets a running exe be renamed but
/// not replaced or deleted: the running one becomes "SLiveTranslator.exe.old", the download takes its name, and the new
/// copy (started with <see cref="UpdatedArgument"/>) deletes the old one once this process has ended.
/// </summary>
public static class UpdateInstaller
{
    public const string OldSuffix = ".old";
    public const string DownloadSuffix = ".download";

    /// <summary>Given to the new copy with this process's id: it waits for the id to end, then deletes the old exe.</summary>
    public const string UpdatedArgument = "--updated";

    /// <summary>How long the new copy waits for the old process to end.</summary>
    public static readonly TimeSpan OldProcessWait = TimeSpan.FromSeconds(15);

    public static string DownloadPath(string exe) => exe + DownloadSuffix;

    /// <summary>
    /// The download takes the exe's name; the running exe moves aside. Nothing changes when either step fails (the exe is
    /// put back), and the download is left for another try.
    /// </summary>
    /// <returns>Where the running exe went, for <see cref="RollBack"/>.</returns>
    public static string Swap(string exe, string downloaded)
    {
        string old = exe + OldSuffix;
        if (File.Exists(old) && !TryDelete(old)) old = $"{exe}.{DateTime.Now:yyyyMMddHHmmss}{OldSuffix}";
        try
        {
            File.Move(exe, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateProblem.Install, ex.Message);
        }
        try
        {
            File.Move(downloaded, exe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(old, exe);
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                Log.Error($"update: the exe could not be put back from {old}: {back.Message}");
            }
            throw new UpdateException(UpdateProblem.Install, ex.Message);
        }
        return old;
    }

    /// <summary>The new exe did not start: it goes, and the running exe gets its name back.</summary>
    public static void RollBack(string exe, string old)
    {
        try
        {
            File.Delete(exe);
            File.Move(old, exe);
            Log.Write("update: rolled back to the running version");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"update: roll back from {old} failed: {ex.Message}");
        }
    }

    /// <summary>What an update leaves next to the exe: old versions ("SLiveTranslator.exe.old") and an unfinished download.</summary>
    public static IReadOnlyList<string> Leftovers(string exe)
    {
        string? dir = Path.GetDirectoryName(exe);
        if (dir is null || !Directory.Exists(dir)) return [];
        string name = Path.GetFileName(exe);
        try
        {
            return Directory.EnumerateFiles(dir, name + ".*")
                .Where(f =>
                {
                    string n = Path.GetFileName(f);
                    return n.Length > name.Length && n.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)
                           && (n.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase) || n.Equals(name + DownloadSuffix, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Deletes the <see cref="Leftovers"/>, trying again for a while when one is still locked (the old process ending).</summary>
    /// <returns>The files deleted.</returns>
    public static IReadOnlyList<string> CleanUp(string exe, TimeSpan wait)
    {
        var deleted = new List<string>();
        var left = Leftovers(exe).ToList();
        var clock = Stopwatch.StartNew();
        while (left.Count > 0)
        {
            foreach (var file in left.ToList())
            {
                if (!TryDelete(file, quiet: true)) continue;
                deleted.Add(file);
                left.Remove(file);
            }
            if (left.Count == 0 || clock.Elapsed >= wait) break;
            Thread.Sleep(250);
        }
        foreach (var file in deleted) Log.Write("update: deleted " + Path.GetFileName(file));
        foreach (var file in left) Log.Warn("update: could not delete " + file);
        return deleted;
    }

    /// <summary>Waits (up to <see cref="OldProcessWait"/>) for the copy that started this one to end.</summary>
    public static void WaitForExit(int processId)
    {
        try
        {
            using var old = Process.GetProcessById(processId);
            if (!old.WaitForExit(OldProcessWait)) Log.Warn($"update: the old copy ({processId}) is still running");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // it has ended already
        }
    }

    /// <summary>
    /// The single-file exe unpacks its native parts into "%TEMP%\.net\SLiveTranslator\&lt;id&gt;", a new folder per build:
    /// after an update the folders of earlier versions are deleted. The current folder is the one this process loaded a
    /// native part from; nothing is deleted when it is not found or does not lie in such a place.
    /// </summary>
    /// <returns>The folders deleted.</returns>
    public static IReadOnlyList<string> DeleteOldExtractions()
    {
        if (CurrentExtraction() is not { } current) return [];
        var deleted = new List<string>();
        foreach (var dir in OldExtractions(current))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                deleted.Add(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"update: {dir} not deleted: {ex.Message}");
            }
        }
        if (deleted.Count > 0) Log.Write($"update: deleted {deleted.Count} unpacked folder(s) of earlier versions");
        return deleted;
    }

    /// <summary>The other folders beside <paramref name="current"/>, when it is "…\.net\SLiveTranslator\&lt;id&gt;" (or under DOTNET_BUNDLE_EXTRACT_BASE_DIR).</summary>
    public static IReadOnlyList<string> OldExtractions(string current, string? extractBase = null)
    {
        var dir = new DirectoryInfo(current.TrimEnd('\\', '/'));
        if (!dir.Exists || !IsExtraction(dir, extractBase)) return [];
        try
        {
            return dir.Parent!.EnumerateDirectories()
                .Where(d => !Same(d.FullName, dir.FullName))
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>"…\.net\SLiveTranslator\&lt;id&gt;", or "&lt;DOTNET_BUNDLE_EXTRACT_BASE_DIR&gt;\SLiveTranslator\&lt;id&gt;".</summary>
    static bool IsExtraction(DirectoryInfo dir, string? extractBase)
    {
        var app = dir.Parent;
        var root = app?.Parent;
        if (app is null || root is null || !app.Name.Equals("SLiveTranslator", StringComparison.OrdinalIgnoreCase)) return false;
        extractBase ??= Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        return root.Name.Equals(".net", StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrWhiteSpace(extractBase) && Same(Path.GetFullPath(extractBase), root.FullName));
    }

    static bool Same(string a, string b) => a.TrimEnd('\\', '/').Equals(b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder of a native part this process loaded from an unpacked folder of the app.</summary>
    static string? CurrentExtraction()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (ProcessModule module in self.Modules)
            {
                if (Path.GetDirectoryName(module.FileName) is { } dir && IsExtraction(new DirectoryInfo(dir), null)) return dir;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Log.Warn("update: modules not read: " + ex.Message);
        }
        return null;
    }

    static bool TryDelete(string path, bool quiet = false)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!quiet) Log.Warn($"update: {path} not deleted: {ex.Message}");
            return false;
        }
    }
}
