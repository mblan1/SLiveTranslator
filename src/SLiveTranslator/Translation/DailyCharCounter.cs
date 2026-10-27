using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SLiveTranslator.Models;

namespace SLiveTranslator.Translation;

/// <summary>
/// Characters sent to a service today, counted on this PC, so the user sees how much of a daily free quota is left.
/// Kept in a small file in the data folder; the count starts again at local midnight. Counting never waits for the
/// disk: the file is written on the thread pool a while after the first new count (so a translate-screen pass of 50
/// labels is one write), and by <see cref="Flush"/> on exit. Thread-safe; never throws.
/// </summary>
public sealed class DailyCharCounter
{
    static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromSeconds(30);

    readonly string _path;
    readonly TimeProvider _clock;
    readonly TimeSpan _saveDelay;
    readonly object _gate = new();
    readonly object _saveGate = new();
    bool _loaded, _dirty, _saveScheduled;
    DateOnly _day;
    long _chars;

    public DailyCharCounter(string path, TimeProvider? clock = null) : this(path, clock, DefaultSaveDelay)
    {
    }

    /// <param name="saveDelay">How long after the first new count the file is written.</param>
    internal DailyCharCounter(string path, TimeProvider? clock, TimeSpan saveDelay)
    {
        _path = path;
        _clock = clock ?? TimeProvider.System;
        _saveDelay = saveDelay;
    }

    /// <summary>Characters counted since local midnight.</summary>
    public long Today
    {
        get
        {
            lock (_gate)
            {
                Roll();
                return _chars;
            }
        }
    }

    /// <summary>Characters were counted; the argument is today's new total. Raised on the thread that counted.</summary>
    public event Action<long>? Counted;

    public void Add(int chars)
    {
        if (chars <= 0) return;
        long total;
        bool scheduleSave;
        lock (_gate)
        {
            Roll();
            total = _chars += chars;
            _dirty = true;
            scheduleSave = !_saveScheduled;
            _saveScheduled = true;
        }
        Counted?.Invoke(total);
        if (!scheduleSave) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(_saveDelay);
            Flush();
        });
    }

    /// <summary>Writes the count now if it changed; for exit.</summary>
    public void Flush()
    {
        // One write at a time, each with the count as it is when it starts, so an older count never replaces a newer one.
        lock (_saveGate)
        {
            string json;
            lock (_gate)
            {
                _saveScheduled = false;
                if (!_dirty) return;
                _dirty = false;
                json = $$"""{"day":"{{_day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}}","chars":{{_chars}}}""";
            }
            if (Save(json)) return;
            lock (_gate) _dirty = true; // tried again with the next write
        }
    }

    DateOnly LocalToday => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    /// <summary>Reads the file once, and starts a new count when the day changed.</summary>
    void Roll()
    {
        if (!_loaded)
        {
            _loaded = true;
            _day = LocalToday;
            Load();
        }
        if (_day == LocalToday) return;
        _day = LocalToday;
        _chars = 0;
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            if (root.TryGetProperty("day", out var day) && DateOnly.TryParseExact(day.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var saved)
                && saved == _day && root.TryGetProperty("chars", out var chars) && chars.TryGetInt64(out long count))
                _chars = Math.Max(0, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log.Warn($"usage counter {Path.GetFileName(_path)} cannot be read: {ex.Message}");
        }
    }

    bool Save(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Once("usage save " + _path, $"usage counter {Path.GetFileName(_path)} cannot be saved: {ex.Message}", TimeSpan.FromMinutes(10), LogLevel.Warn);
            return false;
        }
    }
}
