using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SLiveTranslator.Models;

/// <summary>Most severe first: a level also writes everything above it.</summary>
public enum LogLevel { Error, Warn, Info, Debug }

/// <summary>The app-wide log in the data folder (<see cref="DataPaths.LogFile"/>).</summary>
public static class Log
{
    static readonly LogWriter Writer = new(() => DataPaths.Current.LogFile);

    public static string FilePath => DataPaths.Current.LogFile;

    public static LogLevel Level
    {
        get => Writer.Level;
        set => Writer.Level = value;
    }

    public static long MaxBytes
    {
        get => Writer.MaxBytes;
        set => Writer.MaxBytes = value;
    }

    public static void Write(string message) => Writer.Write(LogLevel.Info, message);
    public static void Error(string message) => Writer.Write(LogLevel.Error, message);
    public static void Warn(string message) => Writer.Write(LogLevel.Warn, message);

    /// <inheritdoc cref="LogWriter.Once"/>
    public static bool Once(string key, string message, TimeSpan ttl, LogLevel level = LogLevel.Info) =>
        Writer.Once(key, message, ttl, level);
}

/// <summary>Appends timestamped lines to a file, rotating it to <c>name.1.ext</c> once it grows past <see cref="MaxBytes"/>. Thread-safe.</summary>
public sealed class LogWriter
{
    public const long DefaultMaxBytes = 1024 * 1024;
    const int MaxOnceKeys = 256;
    internal static readonly TimeSpan RotationRetryInterval = TimeSpan.FromMinutes(1);

    readonly object _gate = new();
    readonly Func<string> _path;
    readonly TimeProvider _clock;
    readonly Dictionary<string, DateTimeOffset> _onceUntil = new();
    DateTimeOffset _rotationRetryAt;

    public LogWriter(Func<string> path, TimeProvider? clock = null)
    {
        _path = path;
        _clock = clock ?? TimeProvider.System;
    }

    public LogLevel Level { get; set; } = LogLevel.Info;
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    bool IsEnabled(LogLevel level) => level <= Level;

    public void Write(LogLevel level, string message)
    {
        if (!IsEnabled(level)) return;
        lock (_gate) Append(level, message);
    }

    /// <summary>
    /// Writes <paramref name="message"/> unless the same <paramref name="key"/> was written less than
    /// <paramref name="ttl"/> ago, so a failure that repeats every few seconds leaves one line. Returns true when written.
    /// </summary>
    public bool Once(string key, string message, TimeSpan ttl, LogLevel level = LogLevel.Info)
    {
        if (!IsEnabled(level)) return false;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_onceUntil.TryGetValue(key, out var until) && now < until) return false;
            if (_onceUntil.Count >= MaxOnceKeys)
                foreach (var expired in _onceUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                    _onceUntil.Remove(expired);
            _onceUntil[key] = now + ttl;
            Append(level, message);
            return true;
        }
    }

    void Append(LogLevel level, string message)
    {
        try
        {
            string path = _path();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RotateIfFull(path);
            File.AppendAllText(path, $"{_clock.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff} {LevelTag(level)} {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never take the app down
        }
    }

    /// <summary>
    /// A rename fails while another process holds the file without delete sharing (Get-Content -Wait, a viewer, a
    /// second instance mid-write). The line is then appended to the full file and the rotation retried a bit later.
    /// </summary>
    void RotateIfFull(string path)
    {
        var now = _clock.GetUtcNow();
        if (now < _rotationRetryAt) return;
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= MaxBytes) return;
        try
        {
            File.Move(path, RotatedPath(path), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _rotationRetryAt = now + RotationRetryInterval;
        }
    }

    /// <summary>The last <paramref name="count"/> lines of the log, read while the app may be writing it; empty when there is none.</summary>
    public static IReadOnlyList<string> Tail(string path, int count)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == count) lines.Dequeue();
                lines.Enqueue(line);
            }
            return lines.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Empties the log and removes its rotated copy.</summary>
    /// <returns>False when another program holds the file.</returns>
    public static bool Clear(string path)
    {
        try
        {
            if (File.Exists(path)) File.WriteAllText(path, "");
            File.Delete(RotatedPath(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string RotatedPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".1" + Path.GetExtension(path));

    static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN ",
        LogLevel.Debug => "DEBUG",
        _ => "INFO ",
    };
}
