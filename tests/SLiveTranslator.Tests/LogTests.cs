using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using Xunit;

namespace SLiveTranslator.Tests;

public class LogTests
{
    sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    static string[] Lines(string path) => File.Exists(path) ? File.ReadAllLines(path) : [];

    [Fact]
    public void Writes_timestamp_level_and_message_at_or_above_the_level()
    {
        using var dir = new TempDir();
        var log = new LogWriter(() => dir.File("log.txt"), new ManualClock()) { Level = LogLevel.Warn };

        log.Write(LogLevel.Error, "boom");
        log.Write(LogLevel.Warn, "careful");
        log.Write(LogLevel.Info, "chatty");
        log.Write(LogLevel.Debug, "noise");

        Assert.Equal(["2026-10-27 12:00:00.000 ERROR boom", "2026-10-27 12:00:00.000 WARN  careful"], Lines(dir.File("log.txt")));
    }

    [Fact]
    public void Rotates_to_log_1_once_the_file_is_past_the_limit()
    {
        using var dir = new TempDir();
        string path = dir.File("log.txt");
        // Each line is 79 bytes (23 timestamp + level + 47 message + CRLF), so a 200-byte limit rotates after every third line.
        var log = new LogWriter(() => path, new ManualClock()) { MaxBytes = 200 };

        for (int i = 0; i < 7; i++) log.Write(LogLevel.Info, $"line {i} " + new string('x', 40));

        Assert.Equal(dir.File("log.1.txt"), LogWriter.RotatedPath(path));
        Assert.Equal(["line 3", "line 4", "line 5"], Lines(dir.File("log.1.txt")).Select(l => l[30..36]));
        Assert.Equal(["line 6"], Lines(path).Select(l => l[30..36]));
    }

    [Fact]
    public void Reader_without_delete_sharing_blocks_rotation_but_not_writing()
    {
        using var dir = new TempDir();
        string path = dir.File("log.txt");
        var clock = new ManualClock();
        var log = new LogWriter(() => path, clock) { MaxBytes = 100 };
        log.Write(LogLevel.Info, "before " + new string('x', 100));

        // Get-Content -Wait and most viewers share read/write but not delete, so the rename to log.1.txt fails.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            log.Write(LogLevel.Info, "while tailed 1");
            log.Write(LogLevel.Info, "while tailed 2");
        }
        log.Write(LogLevel.Info, "soon after"); // the rotation is not retried on every line

        Assert.False(File.Exists(LogWriter.RotatedPath(path)));
        Assert.Equal(["before", "while tailed 1", "while tailed 2", "soon after"],
            Lines(path).Select(l => l[30..].TrimEnd('x', ' ')));

        clock.Now += LogWriter.RotationRetryInterval;
        log.Write(LogLevel.Info, "later");

        Assert.Equal(4, Lines(LogWriter.RotatedPath(path)).Length);
        Assert.Equal(["later"], Lines(path).Select(l => l[30..]));
    }

    [Fact]
    public void Once_writes_a_repeated_message_once_per_ttl()
    {
        using var dir = new TempDir();
        var clock = new ManualClock();
        var log = new LogWriter(() => dir.File("log.txt"), clock);
        var ttl = TimeSpan.FromMinutes(10);

        Assert.True(log.Once("google", "google unavailable", ttl));
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.False(log.Once("google", "google unavailable", ttl));
        Assert.True(log.Once("mymemory", "mymemory unavailable", ttl));
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.True(log.Once("google", "google unavailable again", ttl));

        Assert.Equal(["google unavailable", "mymemory unavailable", "google unavailable again"],
            Lines(dir.File("log.txt")).Select(l => l[30..]));
    }

    [Fact]
    public void Once_below_the_level_writes_nothing_and_does_not_start_the_ttl()
    {
        using var dir = new TempDir();
        var log = new LogWriter(() => dir.File("log.txt"), new ManualClock()) { Level = LogLevel.Warn };

        Assert.False(log.Once("k", "hidden", TimeSpan.FromHours(1)));
        log.Level = LogLevel.Info;
        Assert.True(log.Once("k", "shown", TimeSpan.FromHours(1)));

        Assert.Single(Lines(dir.File("log.txt")));
    }

    [Fact]
    public void Concurrent_writers_lose_no_lines()
    {
        using var dir = new TempDir();
        var log = new LogWriter(() => dir.File("log.txt"));

        Parallel.For(0, 400, i =>
        {
            log.Write(LogLevel.Info, "write " + i);
            log.Once("key " + i % 10, "once " + i % 10, TimeSpan.FromHours(1));
        });

        var lines = Lines(dir.File("log.txt"));
        Assert.Equal(400, lines.Count(l => l.Contains(" write ")));
        Assert.Equal(10, lines.Count(l => l.Contains(" once ")));
    }

    [Fact]
    public void Concurrent_writers_with_rotation_keep_the_file_near_the_limit()
    {
        using var dir = new TempDir();
        string path = dir.File("log.txt");
        var log = new LogWriter(() => path) { MaxBytes = 4096 };

        Parallel.For(0, 1000, i => log.Write(LogLevel.Info, "rotating write " + i));

        Assert.True(File.Exists(LogWriter.RotatedPath(path)));
        Assert.InRange(new FileInfo(path).Length, 1, 4096 + 100);
    }
}
