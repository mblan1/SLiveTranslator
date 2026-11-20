using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Pages;
using SLiveTranslator.Updates;
using Xunit;

namespace SLiveTranslator.Tests;

public class UpdateTests
{
    const string ExeUrl = "https://github.com/mblan1/SLiveTranslator/releases/download/v2.1.0/SLiveTranslator.exe";
    const string Digest = "sha256:519203272c2af63ae2b10a71459ca7b2fa02943104ded4b8ef9517591aa5d642";

    static string Release(string tag = "v2.1.0", bool draft = false, bool prerelease = false, string exeName = "SLiveTranslator.exe",
        string url = ExeUrl, string digest = Digest) => $$"""
        {
          "tag_name": "{{tag}}", "name": "SLiveTranslator {{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/mblan1/SLiveTranslator/releases/tag/{{tag}}",
          "published_at": "2026-11-20T10:00:00Z",
          "body": "## New\n* Updates",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/mblan1/SLiveTranslator/releases/download/{{tag}}/notes.txt", "size": 10 },
            { "name": "{{exeName}}", "browser_download_url": "{{url}}", "size": 200482816, "digest": "{{digest}}" }
          ]
        }
        """;

    // ---- versions and the feed ----

    [Theory]
    [InlineData("v2.1.0", "2.1.0.0")]
    [InlineData("V2.1", "2.1.0.0")]
    [InlineData("2.0.0+105cadc", "2.0.0.0")]
    [InlineData(" 3.4.5.6 ", "3.4.5.6")]
    public void A_tag_or_the_apps_version_reads_as_a_version_with_every_part_set(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateFeed.ParseVersion(text));

    [Theory]
    [InlineData("v2.1.0-beta")]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    public void A_pre_release_or_a_tag_that_is_no_version_is_none(string? text) => Assert.Null(UpdateFeed.ParseVersion(text));

    [Fact]
    public void Only_a_higher_version_is_newer_and_it_is_shown_without_an_unset_fourth_part()
    {
        var current = UpdateFeed.ParseVersion("2.0.0")!;
        Assert.True(UpdateFeed.IsNewer(UpdateFeed.ParseVersion("v2.0.1")!, current));
        Assert.True(UpdateFeed.IsNewer(UpdateFeed.ParseVersion("v2.0.0.1")!, current));
        Assert.True(UpdateFeed.IsNewer(UpdateFeed.ParseVersion("v10.0.0")!, current));
        Assert.False(UpdateFeed.IsNewer(UpdateFeed.ParseVersion("v2.0.0")!, current));
        Assert.False(UpdateFeed.IsNewer(new Version(2, 0), current)); // unset parts count as 0
        Assert.False(UpdateFeed.IsNewer(UpdateFeed.ParseVersion("v1.9.9")!, current));
        Assert.Equal("2.1.0", UpdateFeed.Display(new Version(2, 1, 0, 0)));
        Assert.Equal("2.1.0.3", UpdateFeed.Display(new Version(2, 1, 0, 3)));
    }

    [Fact]
    public void A_release_gives_its_exe_with_size_checksum_notes_and_page()
    {
        var update = UpdateFeed.Parse(Release())!;

        Assert.Equal(new Version(2, 1, 0, 0), update.Version);
        Assert.Equal("v2.1.0", update.Tag);
        Assert.Equal("2.1.0", update.VersionText);
        Assert.Equal(new Uri(ExeUrl), update.Download);
        Assert.Equal(200482816, update.Size);
        Assert.Equal("519203272C2AF63AE2B10A71459CA7B2FA02943104DED4B8EF9517591AA5D642", update.Sha256);
        Assert.Equal("## New\n* Updates", update.Notes);
        Assert.Equal(new Uri("https://github.com/mblan1/SLiveTranslator/releases/tag/v2.1.0"), update.Page);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), update.Published);
    }

    [Fact]
    public void A_draft_a_pre_release_a_release_without_the_exe_or_a_plain_http_download_is_not_offered()
    {
        Assert.Null(UpdateFeed.Parse(Release(draft: true)));
        Assert.Null(UpdateFeed.Parse(Release(prerelease: true)));
        Assert.Null(UpdateFeed.Parse(Release(tag: "nightly")));
        Assert.Null(UpdateFeed.Parse(Release(exeName: "SLiveTranslator.zip")));
        Assert.Null(UpdateFeed.Parse(Release(url: "http://example.com/SLiveTranslator.exe")));
        Assert.NotNull(UpdateFeed.Parse(Release(url: "http://127.0.0.1:8123/SLiveTranslator.exe"))); // the test feed on this PC
        Assert.Null(UpdateFeed.Parse(Release(digest: "md5:abc"))!.Sha256); // offered, but its size is all that can be checked
    }

    [Fact]
    public void The_test_feed_is_taken_only_when_it_is_on_this_pc()
    {
        Assert.Equal(UpdateFeed.Latest, UpdateFeed.FeedFrom(null));
        Assert.Equal(UpdateFeed.Latest, UpdateFeed.FeedFrom("https://example.com/latest.json"));
        Assert.Equal(UpdateFeed.Latest, UpdateFeed.FeedFrom("file:///C:/latest.json"));
        Assert.Equal(new Uri("http://127.0.0.1:8123/latest.json"), UpdateFeed.FeedFrom("http://127.0.0.1:8123/latest.json"));
        Assert.Equal(new Uri("http://localhost:8123/latest.json"), UpdateFeed.FeedFrom(" http://localhost:8123/latest.json "));
    }

    [Fact]
    public async Task A_check_finds_a_newer_release_and_asks_github_for_its_json()
    {
        string? accept = null;
        var http = new HttpClient(new FakeHandler((request, _) =>
        {
            accept = request.Headers.Accept.ToString();
            return FakeHandler.Json(Release());
        }));

        var check = await UpdateFeed.CheckAsync(http, UpdateFeed.Latest, new Version(2, 0, 0, 0), CancellationToken.None);

        Assert.Equal(UpdateCheckState.Available, check.State);
        Assert.Equal("v2.1.0", check.Update!.Tag);
        Assert.Contains("application/vnd.github+json", accept);
    }

    [Fact]
    public async Task The_same_version_no_release_at_all_or_a_failure_is_not_an_update()
    {
        async Task<UpdateCheck> Check(Func<HttpResponseMessage> respond) =>
            await UpdateFeed.CheckAsync(new HttpClient(new FakeHandler((_, _) => respond())), UpdateFeed.Latest, new Version(2, 1, 0, 0), CancellationToken.None);

        Assert.Equal(UpdateCheckState.UpToDate, (await Check(() => FakeHandler.Json(Release()))).State);
        Assert.Equal(UpdateCheckState.UpToDate, (await Check(() => new HttpResponseMessage(HttpStatusCode.NotFound))).State);
        var limited = await Check(() => new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "rate limit exceeded" });
        Assert.Equal(UpdateCheckState.Failed, limited.State);
        Assert.Equal("HTTP 403 rate limit exceeded", limited.Problem);
        Assert.Equal(UpdateCheckState.Failed, (await Check(() => FakeHandler.Json("{ not json"))).State);
        Assert.Equal(UpdateCheckState.Failed, (await Check(() => throw new HttpRequestException("No such host is known."))).State);
    }

    // ---- the download ----

    static byte[] Program(int size = 300_000)
    {
        var bytes = new byte[size];
        new Random(7).NextBytes(bytes);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    static UpdateInfo Info(byte[] bytes, string? sha256 = "", long? size = null) =>
        new(new Version(2, 1, 0, 0), "v2.1.0", "", new Uri(ExeUrl), size ?? bytes.Length,
            sha256 == "" ? Convert.ToHexString(SHA256.HashData(bytes)) : sha256, UpdateFeed.ReleasesPage, null);

    static HttpClient Serving(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new FakeHandler((_, _) => new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) }));

    sealed class Collect : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = new();
        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task A_download_writes_the_checked_exe_and_reports_its_progress_to_the_last_byte()
    {
        using var dir = new TempDir();
        var bytes = Program();
        var progress = new Collect();
        string target = Path.Combine(dir.Path, "SLiveTranslator.exe.download");

        await UpdateDownloader.DownloadAsync(Serving(bytes), Info(bytes), target, progress, CancellationToken.None);

        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.Equal(new DownloadProgress(0, bytes.Length, 0), progress.Reports[0]);
        Assert.Equal(bytes.Length, progress.Reports[^1].Received);
        Assert.Equal(1.0, progress.Reports[^1].Fraction);
    }

    [Fact]
    public async Task A_short_damaged_or_foreign_download_fails_and_leaves_no_file()
    {
        using var dir = new TempDir();
        var bytes = Program();
        string target = Path.Combine(dir.Path, "SLiveTranslator.exe.download");

        async Task<UpdateProblem> Fails(HttpClient http, UpdateInfo info)
        {
            var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateDownloader.DownloadAsync(http, info, target, null, CancellationToken.None));
            Assert.False(File.Exists(target));
            return ex.Problem;
        }

        Assert.Equal(UpdateProblem.Incomplete, await Fails(Serving(bytes[..1000]), Info(bytes)));
        Assert.Equal(UpdateProblem.Corrupt, await Fails(Serving(bytes), Info(bytes, sha256: new string('A', 64))));
        var html = "<html>blocked</html>"u8.ToArray();
        Assert.Equal(UpdateProblem.Corrupt, await Fails(Serving(html), Info(html, sha256: null)));
        Assert.Equal(UpdateProblem.Network, await Fails(Serving(bytes, HttpStatusCode.InternalServerError), Info(bytes)));
        Assert.Equal(UpdateProblem.Network,
            await Fails(new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException("No such host is known."))), Info(bytes)));
    }

    [Fact]
    public async Task An_exe_older_than_its_release_is_not_installed_so_it_cannot_update_for_ever()
    {
        using var dir = new TempDir();
        string target = Path.Combine(dir.Path, "SLiveTranslator.exe.download");
        var host = File.ReadAllBytes(Environment.ProcessPath!); // the test host: a program with version information
        var hostVersion = UpdateDownloader.ProgramVersion(Environment.ProcessPath!)!;
        Assert.Null(UpdateDownloader.ProgramVersion(Path.Combine(dir.Path, "none.exe")));
        UpdateInfo Release(Version v) => Info(host) with { Version = v };

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateDownloader.DownloadAsync(Serving(host), Release(new Version(hostVersion.Major + 1, 0, 0, 0)), target, null, CancellationToken.None));
        Assert.Equal(UpdateProblem.WrongVersion, ex.Problem);
        Assert.Equal(UpdateFeed.Display(hostVersion), ex.Detail);
        Assert.False(File.Exists(target));

        await UpdateDownloader.DownloadAsync(Serving(host), Release(hostVersion), target, null, CancellationToken.None);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task A_stalled_download_fails_and_a_cancelled_one_stops_without_a_file()
    {
        using var dir = new TempDir();
        string target = Path.Combine(dir.Path, "SLiveTranslator.exe.download");
        var stalled = new HttpClient(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }));
        var info = Info(Program(), sha256: null);

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateDownloader.DownloadAsync(stalled, info, target, null, CancellationToken.None, stallTimeout: TimeSpan.FromMilliseconds(200)));
        Assert.Equal(UpdateProblem.Network, ex.Problem);
        Assert.False(File.Exists(target));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateDownloader.DownloadAsync(stalled, info, target, null, cts.Token));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_folder_that_cannot_be_written_to_is_told_apart()
    {
        using var dir = new TempDir();
        var bytes = Program();
        string target = Path.Combine(dir.Path, "missing", "SLiveTranslator.exe.download");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateDownloader.DownloadAsync(Serving(bytes), Info(bytes), target, null, CancellationToken.None));
        Assert.Equal(UpdateProblem.NotWritable, ex.Problem);
    }

    sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- putting it in place and cleaning up ----

    [Fact]
    public void The_download_takes_the_exes_name_and_the_old_exe_moves_aside_and_can_be_put_back()
    {
        using var dir = new TempDir();
        string exe = Path.Combine(dir.Path, "SLiveTranslator.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(exe + ".old", "an earlier old one");
        File.WriteAllText(UpdateInstaller.DownloadPath(exe), "new");

        string old = UpdateInstaller.Swap(exe, UpdateInstaller.DownloadPath(exe));

        Assert.Equal(exe + ".old", old);
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(old));
        Assert.False(File.Exists(UpdateInstaller.DownloadPath(exe)));

        UpdateInstaller.RollBack(exe, old);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void A_swap_without_the_download_changes_nothing()
    {
        using var dir = new TempDir();
        string exe = Path.Combine(dir.Path, "SLiveTranslator.exe");
        File.WriteAllText(exe, "old");

        var ex = Assert.Throws<UpdateException>(() => UpdateInstaller.Swap(exe, UpdateInstaller.DownloadPath(exe)));

        Assert.Equal(UpdateProblem.Install, ex.Problem);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void Old_exes_and_an_unfinished_download_are_deleted_and_nothing_else()
    {
        using var dir = new TempDir();
        string exe = Path.Combine(dir.Path, "SLiveTranslator.exe");
        foreach (var name in new[] { "SLiveTranslator.exe", "SLiveTranslator.exe.old", "SLiveTranslator.exe.20261120101010.old", "SLiveTranslator.exe.download",
                     "SLiveTranslator.exe.config", "SLiveTranslator.exe.download.txt", "Other.exe.old", "portable.flag" })
            File.WriteAllText(Path.Combine(dir.Path, name), name);

        var deleted = UpdateInstaller.CleanUp(exe, TimeSpan.Zero).Select(Path.GetFileName).Order().ToList();

        Assert.Equal(["SLiveTranslator.exe.20261120101010.old", "SLiveTranslator.exe.download", "SLiveTranslator.exe.old"], deleted);
        Assert.Equal(["Other.exe.old", "portable.flag", "SLiveTranslator.exe", "SLiveTranslator.exe.config", "SLiveTranslator.exe.download.txt"],
            Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    [Fact]
    public void Only_the_unpacked_folders_of_other_versions_beside_the_current_one_are_old()
    {
        using var dir = new TempDir();
        string app = Path.Combine(dir.Path, ".net", "SLiveTranslator");
        foreach (var id in new[] { "Current", "OldA", "OldB" }) Directory.CreateDirectory(Path.Combine(app, id));

        var old = UpdateInstaller.OldExtractions(Path.Combine(app, "Current"), extractBase: "").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["OldA", "OldB"], old);

        // Not in such a place: nothing is old.
        string other = Path.Combine(dir.Path, "Games", "SLiveTranslator");
        Directory.CreateDirectory(Path.Combine(other, "bin"));
        Directory.CreateDirectory(Path.Combine(other, "saves"));
        Assert.Empty(UpdateInstaller.OldExtractions(Path.Combine(other, "bin"), extractBase: ""));
        // Under DOTNET_BUNDLE_EXTRACT_BASE_DIR it is such a place.
        Assert.Equal(["saves"], UpdateInstaller.OldExtractions(Path.Combine(other, "bin"), extractBase: Path.Combine(dir.Path, "Games"))
            .Select(Path.GetFileName).ToList());
    }

    [Fact]
    public void The_new_copy_is_told_the_old_ones_id_without_it_reaching_the_command_line_modes()
    {
        var args = StartupArgs.Parse(["--updated", "4242"]);
        Assert.Equal(4242, args.UpdatedFrom);
        Assert.Null(args.Cli);
        Assert.False(args.Quiet);

        var quiet = StartupArgs.Parse(["--startup", "--updated", "7"]);
        Assert.True(quiet.Quiet);
        Assert.Equal(7, quiet.UpdatedFrom);

        Assert.Equal(0, StartupArgs.Parse(["--updated"]).UpdatedFrom);
        Assert.Null(StartupArgs.Parse([]).UpdatedFrom);
        Assert.Equal(["--selftest", "out"], StartupArgs.Parse(["--selftest", "out"]).Cli!);
    }

    // ---- the window ----

    [Fact]
    public void Release_notes_lose_their_markdown()
    {
        string notes = "## New\r\n* **Updates** – checks [GitHub](https://github.com)\r\n  - nested `code`\r\n\r\n\r\n" +
                       "| Hotkey | Action |\r\n| --- | --- |\r\n| `Ctrl+S` | Subtitles |\r\n<br>\r\nSee *this*.\r\n![shot](a.png)\r\n";

        Assert.Equal(string.Join(Environment.NewLine, "New", "• Updates – checks GitHub", "  • nested code", "", "Ctrl+S  –  Subtitles", "", "See this."),
            ReleaseNotesText.Plain(notes));
        Assert.Equal("", ReleaseNotesText.Plain(null));
    }

    sealed class FakeActions : IUpdateActions
    {
        public Func<IProgress<DownloadProgress>, CancellationToken, Task> Download { get; set; } = (_, _) => Task.CompletedTask;
        public Action Install { get; set; } = () => { };
        public int Installs { get; private set; }
        public int Quits { get; private set; }
        public List<Uri> Opened { get; } = new();

        public Task DownloadAsync(UpdateInfo update, IProgress<DownloadProgress> progress, CancellationToken ct) => Download(progress, ct);

        public void InstallAndRestart()
        {
            Installs++;
            Install();
        }

        public void OpenPage(Uri page) => Opened.Add(page);
        public void Quit() => Quits++;
    }

    static UpdateViewModel Window(FakeActions actions) => new(UpdateFeed.Parse(Release())!, "2.0.0", actions);

    [Fact]
    public void The_window_shows_both_versions_the_size_and_the_notes_and_offers_only_update_or_quit()
    {
        var actions = new FakeActions();
        var vm = Window(actions);

        Assert.Equal("2.0.0", vm.CurrentVersion);
        Assert.Equal("2.1.0", vm.NewVersion);
        Assert.Equal("191.2 MB", vm.SizeText);
        Assert.Contains("2.1.0", vm.Message);
        Assert.Contains("2.0.0", vm.Message);
        Assert.Equal("New" + Environment.NewLine + "• Updates", vm.Notes);
        Assert.True(vm.CanUpdate);
        Assert.False(vm.ShowProgress);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.Equal("Update now", vm.UpdateText);

        vm.OpenPageCommand.Execute(null);
        vm.QuitCommand.Execute(null);
        Assert.Equal([new Uri("https://github.com/mblan1/SLiveTranslator/releases/tag/v2.1.0")], actions.Opened);
        Assert.Equal(1, actions.Quits);
    }

    [Fact]
    public void The_download_shows_bytes_percent_speed_and_time_left()
    {
        var vm = Window(new FakeActions());

        vm.ShowSample(new DownloadProgress(0, 200482816, 0));
        Assert.Equal("Connecting…", vm.ProgressText);
        Assert.Equal("", vm.SpeedText);

        vm.ShowSample(new DownloadProgress(87_400_000, 200482816, 3_600_000));
        Assert.True(vm.ShowProgress);
        Assert.False(vm.CanUpdate);
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.Equal(43, Math.Floor(vm.Percent));
        Assert.Equal("83.4 MB of 191.2 MB · 43 %", vm.ProgressText);
        Assert.Equal("3.4 MB/s · about 32 s left", vm.SpeedText);
        Assert.Equal("3 min", UpdateViewModel.Duration(TimeSpan.FromSeconds(150)));
    }

    [Fact]
    public async Task Update_now_downloads_then_installs()
    {
        var actions = new FakeActions();
        var vm = Window(actions);

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, actions.Installs);
        Assert.Equal(UpdateStep.Installing, vm.Step);
        Assert.Equal(100, vm.Percent);
        Assert.False(vm.QuitCommand.CanExecute(null)); // the files are moving
    }

    [Fact]
    public async Task A_failure_is_explained_and_can_be_tried_again()
    {
        var actions = new FakeActions { Download = (_, _) => throw new UpdateException(UpdateProblem.NotWritable, "Access to the path is denied.") };
        var vm = Window(actions);

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(UpdateStep.Failed, vm.Step);
        Assert.True(vm.ShowProblem);
        Assert.Equal(Loc.F("Update.Problem.NotWritable", "Access to the path is denied"), vm.Problem);
        Assert.Equal("Try again", vm.UpdateText);
        Assert.Equal(0, actions.Installs);

        actions.Download = (_, _) => Task.CompletedTask;
        actions.Install = () => throw new UpdateException(UpdateProblem.Restart, "The system cannot find the file specified.");
        await vm.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(UpdateStep.Failed, vm.Step);
        Assert.Equal(Loc.F("Update.Problem.Restart", "The system cannot find the file specified"), vm.Problem);
    }

    [Fact]
    public async Task A_cancelled_download_goes_back_and_says_the_update_is_still_needed()
    {
        var actions = new FakeActions { Download = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        var vm = Window(actions);

        var running = vm.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(UpdateStep.Downloading, vm.Step);
        vm.CancelCommand.Execute(null);
        await running;

        Assert.Equal(UpdateStep.Ready, vm.Step);
        Assert.True(vm.Cancelled);
        Assert.Equal(Loc.Instance["Update.Cancelled"], vm.StatusText);
        Assert.Equal(0, actions.Installs);
    }

    [Fact]
    public void The_about_page_says_when_it_last_checked_and_what_it_found()
    {
        var at = new DateTime(2026, 11, 20, 14, 5, 0);
        Assert.Equal("Checking for updates…", GeneralViewModel.UpdateStatusText(null, null, checking: true));
        Assert.Equal(Loc.Instance["General.UpdateNotChecked"], GeneralViewModel.UpdateStatusText(null, null, false));
        Assert.Equal(Loc.Instance["General.UpdateDisabled"], GeneralViewModel.UpdateStatusText(UpdateCheck.Disabled, null, false));
        Assert.EndsWith("is the latest · checked at 2:05 PM", GeneralViewModel.UpdateStatusText(UpdateCheck.UpToDate, at, false));
        Assert.Equal("Version 2.1.0 is out and must be installed.",
            GeneralViewModel.UpdateStatusText(new UpdateCheck(UpdateCheckState.Available, UpdateFeed.Parse(Release())), at, false));
        Assert.StartsWith("Could not check at 2:05 PM: HTTP 403", GeneralViewModel.UpdateStatusText(UpdateCheck.Failed("HTTP 403"), at, false));
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slt-update-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
