using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SLiveTranslator.Updates;

/// <summary>A release newer than this copy, with the exe to download.</summary>
/// <param name="Size">Bytes of the exe as the release lists it; 0 when not listed.</param>
/// <param name="Sha256">The exe's SHA-256 in hex as the release lists it (GitHub's asset digest); null when not listed.</param>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, Uri Download, long Size, string? Sha256, Uri Page, DateTimeOffset? Published)
{
    public string VersionText => UpdateFeed.Display(Version);
}

public enum UpdateCheckState { Disabled, UpToDate, Available, Failed }

/// <param name="Problem">Why the check failed (for the log and the About page).</param>
public sealed record UpdateCheck(UpdateCheckState State, UpdateInfo? Update = null, string? Problem = null)
{
    public static UpdateCheck Disabled { get; } = new(UpdateCheckState.Disabled);
    public static UpdateCheck UpToDate { get; } = new(UpdateCheckState.UpToDate);
    public static UpdateCheck Failed(string problem) => new(UpdateCheckState.Failed, Problem: problem);
}

/// <summary>
/// The app's releases on GitHub: the latest one is read from the releases API, and it is an update when its tag is a
/// higher version than this copy and it has the exe attached. Drafts and pre-releases are never offered.
/// </summary>
public static class UpdateFeed
{
    public const string Repository = "mblan1/SLiveTranslator";
    public const string AssetName = "SLiveTranslator.exe";

    /// <summary>A loopback address to read instead of GitHub, to try an update on this PC (see README › Diagnostics).</summary>
    public const string TestFeedVariable = "SLT_UPDATE_FEED";

    public static readonly Uri Latest = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases/latest");

    /// <summary>GitHub's latest release, or the test feed when it is an http(s) address on this PC; anything else is ignored.</summary>
    public static Uri FeedFrom(string? testFeed) =>
        Uri.TryCreate(testFeed?.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : Latest;

    /// <summary>"v2.1.0", "2.1" or "2.1.0+abc" as a version with every part set (2.1.0.0); null for a pre-release ("2.1.0-beta") or anything else.</summary>
    public static Version? ParseVersion(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        int plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        if (t.Contains('-') || !Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    /// <summary>"2.1.0", with the fourth part only when it is set.</summary>
    public static string Display(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);

    public static bool IsNewer(Version candidate, Version current) => Full(candidate) > Full(current);

    static Version Full(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>
    /// The release in GitHub's JSON with its exe; null for a draft, a pre-release, a tag that is not a version, or a
    /// release without the exe. The download must be https (http only on this PC, for the test feed).
    /// </summary>
    public static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Bool(root, "draft") || Bool(root, "prerelease")) return null;
        string tag = Str(root, "tag_name");
        if (ParseVersion(tag) is not { } version) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !AssetName.Equals(Str(asset, "name"), StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(Str(asset, "browser_download_url"), UriKind.Absolute, out var download)
                || !(download.Scheme == Uri.UriSchemeHttps || (download.Scheme == Uri.UriSchemeHttp && download.IsLoopback)))
                return null;
            long size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long n) && n > 0 ? n : 0;
            var page = Uri.TryCreate(Str(root, "html_url"), UriKind.Absolute, out var html) && html.Scheme == Uri.UriSchemeHttps ? html : ReleasesPage;
            DateTimeOffset? published = DateTimeOffset.TryParse(Str(root, "published_at"), out var at) ? at : null;
            return new UpdateInfo(version, tag, Str(root, "body"), download, size, Sha256Of(Str(asset, "digest")), page, published);
        }
        return null;
    }

    /// <summary>The hex of "sha256:…" in capitals; null for another algorithm or a malformed digest.</summary>
    static string? Sha256Of(string digest)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string hex = digest[prefix.Length..].Trim();
        if (hex.Length != 64) return null;
        foreach (char c in hex)
            if (!Uri.IsHexDigit(c)) return null;
        return hex.ToUpperInvariant();
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Reads the latest release. No release at all (404) or one without the exe is "up to date"; a failure of the network
    /// or of GitHub (a rate limit) is <see cref="UpdateCheckState.Failed"/>, and the app is used as it is meanwhile.
    /// </summary>
    public static async Task<UpdateCheck> CheckAsync(HttpClient http, Uri feed, Version current, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, feed);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return UpdateCheck.UpToDate;
            if (!response.IsSuccessStatusCode) return UpdateCheck.Failed($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json) is { } update && IsNewer(update.Version, current) ? new UpdateCheck(UpdateCheckState.Available, update) : UpdateCheck.UpToDate;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UpdateCheck.Failed("timed out");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return UpdateCheck.Failed(ex.Message);
        }
    }
}
