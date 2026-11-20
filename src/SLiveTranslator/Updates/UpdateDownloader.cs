using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SLiveTranslator.Updates;

public enum UpdateProblem
{
    /// <summary>The connection failed or stalled.</summary>
    Network,
    /// <summary>Fewer (or more) bytes than the release lists.</summary>
    Incomplete,
    /// <summary>The checksum does not match, or the file is not a program.</summary>
    Corrupt,
    /// <summary>The exe says it is an older version than its release (the release was made from the wrong build).</summary>
    WrongVersion,
    /// <summary>The exe's folder cannot be written to (Program Files, a read-only drive).</summary>
    NotWritable,
    /// <summary>Writing the download failed (a full drive).</summary>
    Disk,
    /// <summary>The new exe could not be put in place of the running one.</summary>
    Install,
    /// <summary>The new exe was put in place but did not start; the old one was put back.</summary>
    Restart,
}

/// <param name="Detail">The raw reason, shown inside the localized text.</param>
public sealed class UpdateException(UpdateProblem problem, string detail) : Exception($"{problem}: {detail}")
{
    public UpdateProblem Problem { get; } = problem;
    public string Detail { get; } = detail;
}

/// <param name="Total">The size to reach; 0 when neither the release nor the server tells it.</param>
/// <param name="BytesPerSecond">The recent speed, smoothed; 0 until known.</param>
public sealed record DownloadProgress(long Received, long Total, double BytesPerSecond)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;

    public TimeSpan? Remaining => BytesPerSecond >= 1 && Total > Received ? TimeSpan.FromSeconds((Total - Received) / BytesPerSecond) : null;
}

/// <summary>
/// Downloads the new exe next to the running one (the same drive, so putting it in place is a rename), checking it as it
/// comes: a stall of <see cref="StallTimeout"/> fails, and the result must have the listed size and checksum and be a
/// Windows program. A failed or cancelled download leaves no file behind.
/// </summary>
public static class UpdateDownloader
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

    public static async Task DownloadAsync(HttpClient http, UpdateInfo update, string target, IProgress<DownloadProgress>? progress, CancellationToken ct,
        TimeSpan? stallTimeout = null)
    {
        FileStream file;
        try
        {
            file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            throw new UpdateException(UpdateProblem.NotWritable, ex.Message);
        }

        bool ok = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, update.Download);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new UpdateException(UpdateProblem.Network, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
            long total = update.Size > 0 ? update.Size : response.Content.Headers.ContentLength ?? 0;
            progress?.Report(new DownloadProgress(0, total, 0));

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var buffer = new byte[81920];
            var clock = Stopwatch.StartNew();
            long received = 0, lastReceived = 0;
            var lastReport = TimeSpan.Zero;
            double speed = 0;
            while (true)
            {
                stall.CancelAfter(stallTimeout ?? StallTimeout);
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new UpdateException(UpdateProblem.Network, "no data for " + (int)(stallTimeout ?? StallTimeout).TotalSeconds + " s");
                }
                if (read == 0) break;
                await WriteAsync(file, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;

                var now = clock.Elapsed;
                if (now - lastReport < ReportEvery) continue;
                double recent = (received - lastReceived) / (now - lastReport).TotalSeconds;
                speed = speed == 0 ? recent : speed * 0.8 + recent * 0.2;
                lastReport = now;
                lastReceived = received;
                progress?.Report(new DownloadProgress(received, Math.Max(total, received), speed));
            }
            progress?.Report(new DownloadProgress(received, Math.Max(total, received), speed));
            await WriteAsync(file, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);

            if (update.Size > 0 && received != update.Size)
                throw new UpdateException(UpdateProblem.Incomplete, $"{received:N0} of {update.Size:N0} bytes");
            string actual = Convert.ToHexString(hash.GetHashAndReset());
            if (update.Sha256 is { } expected && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(UpdateProblem.Corrupt, "SHA-256 " + actual[..12] + "… instead of " + expected[..12] + "…");
            ok = true;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateProblem.Network, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateProblem.Network, "timed out");
        }
        catch (IOException ex)
        {
            // Writing fails as Disk in WriteAsync; an IOException here is the response breaking off.
            throw new UpdateException(UpdateProblem.Network, ex.Message);
        }
        finally
        {
            await file.DisposeAsync().ConfigureAwait(false);
            if (!ok) TryDelete(target);
        }

        if (!IsProgram(target))
        {
            TryDelete(target);
            throw new UpdateException(UpdateProblem.Corrupt, "not a Windows program");
        }
        // An exe older than its release would find the release again after the restart, and update for ever.
        if (ProgramVersion(target) is { } version && UpdateFeed.IsNewer(update.Version, version))
        {
            TryDelete(target);
            throw new UpdateException(UpdateProblem.WrongVersion, UpdateFeed.Display(version));
        }
    }

    /// <summary>The program's file version (the app's &lt;Version&gt;, "2.0.1.0"); null without version information.</summary>
    internal static Version? ProgramVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            return version == new Version(0, 0, 0, 0) ? null : version;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Writes the bytes (none: flushes); a failure is the drive's (full, removed), not the network's.</summary>
    static async Task WriteAsync(FileStream file, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        try
        {
            if (bytes.IsEmpty) await file.FlushAsync(ct).ConfigureAwait(false);
            else await file.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new UpdateException(UpdateProblem.Disk, ex.Message);
        }
    }

    /// <summary>A Windows program starts with "MZ".</summary>
    static bool IsProgram(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Models.Log.Warn($"update: {path} not deleted: {ex.Message}");
        }
    }
}
