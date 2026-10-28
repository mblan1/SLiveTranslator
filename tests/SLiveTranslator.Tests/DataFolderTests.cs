using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>Moving and copying the data folder, on temp folders: what is copied, progress, cancel, refusals, conflicts, location.json and portable.flag.</summary>
public class DataFolderTests
{
    /// <summary>Reports on the copying thread at once, so a test can cancel at a known point.</summary>
    sealed class Recorded(Action<double>? onReport = null) : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value)
        {
            lock (Values) Values.Add(value);
            onReport?.Invoke(value);
        }
    }

    static void Write(string root, string file, string text)
    {
        string path = Path.Combine(root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A data folder as the app leaves it, with a cache big enough to be copied in several pieces, and files that are not the app's.</summary>
    static void SeedData(string root, string marker = "mine")
    {
        Write(root, "settings.json", "{\"SchemaVersion\":2,\"Marker\":\"" + marker + "\"}");
        Write(root, "settings.json.bak-v1", "{}");
        Write(root, "cache.json", "{\"version\":2,\"entries\":[]}" + new string(' ', 300_000));
        Write(root, "cache.json.tmp", "half written");
        Write(root, "log.txt", "line\n");
        Write(root, "log.1.txt", "older\n");
        Write(root, "mymemory-usage.json", "{}");
        Write(root, Path.Combine("Profiles", "witcher3.json"), "{\"id\":\"witcher3\"}");
        Write(root, Path.Combine("Profiles", ".seeds.json"), "{}");
        Write(root, "holiday.jpg", "not ours");
        Write(root, Path.Combine("Other", "notes.json"), "not ours");
    }

    static readonly string[] OurFiles =
    [
        "cache.json", "log.1.txt", "log.txt", "mymemory-usage.json", Path.Combine("Profiles", ".seeds.json"), Path.Combine("Profiles", "witcher3.json"),
        "settings.json", "settings.json.bak-v1",
    ];

    static string[] Files(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    static DataFolderMover Mover(TempDir dir, Func<string, long?>? freeSpace = null) => new(dir.File("AppData"), dir.File("Exe"), freeSpace ?? (_ => null));

    // ---- the mover ----

    [Fact]
    public async Task A_copy_takes_only_the_apps_files_reports_its_progress_and_leaves_the_source_as_it_was()
    {
        using var dir = new TempDir();
        string source = dir.File("Source"), target = dir.File("Target");
        SeedData(source);
        var before = Files(source);
        var progress = new Recorded();

        var copied = await Mover(dir).CopyAsync(source, target, progress, CancellationToken.None);

        Assert.Equal(OurFiles.Order(StringComparer.OrdinalIgnoreCase), copied);
        Assert.Equal(OurFiles.Order(StringComparer.OrdinalIgnoreCase), Files(target));
        Assert.Equal(File.ReadAllText(Path.Combine(source, "cache.json")), File.ReadAllText(Path.Combine(target, "cache.json")));
        Assert.True(progress.Values.Count > 3);
        Assert.Equal(progress.Values.Order(), progress.Values); // it only goes up
        Assert.Equal(1, progress.Values[^1]);
        Assert.Equal(before, Files(source));
    }

    [Fact]
    public async Task A_cancelled_copy_leaves_the_source_intact_and_the_target_as_it_was()
    {
        using var dir = new TempDir();
        string source = dir.File("Source"), target = dir.File("Target");
        SeedData(source);
        Write(target, "readme.txt", "the user's");
        var before = Files(source);
        using var cts = new CancellationTokenSource();
        var progress = new Recorded(p =>
        {
            if (p > 0.3) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Mover(dir).CopyAsync(source, target, progress, cts.Token));

        Assert.Equal(["readme.txt"], Files(target)); // no data file and no staging folder
        Assert.Equal(before, Files(source));
        Assert.True(progress.Values[^1] < 1);
    }

    [Fact]
    public async Task Replacing_the_data_of_a_folder_removes_its_data_files_the_source_does_not_have()
    {
        using var dir = new TempDir();
        string source = dir.File("Source"), target = dir.File("Target");
        Write(source, "settings.json", "mine");
        SeedData(target, marker: "theirs");

        await Mover(dir).CopyAsync(source, target, null, CancellationToken.None);

        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal(new[] { "holiday.jpg", "cache.json.tmp", Path.Combine("Other", "notes.json"), "settings.json" }.Order(StringComparer.OrdinalIgnoreCase), Files(target));
    }

    [Fact]
    public void A_folder_that_cannot_take_the_data_is_refused_and_a_checked_folder_is_not_left_behind()
    {
        using var dir = new TempDir();
        string source = dir.File("Source");
        SeedData(source);
        Write(dir.Path, "a-file", "");
        var mover = Mover(dir);

        Assert.Equal(FolderProblem.NotFullPath, mover.Check(source, @"relative\data", portable: false));
        Assert.Equal(FolderProblem.SameFolder, mover.Check(source, source + Path.DirectorySeparatorChar, portable: false));
        Assert.Equal(FolderProblem.InsideCurrent, mover.Check(source, Path.Combine(source, "Backup"), portable: false));
        Assert.Equal(FolderProblem.InsideAppFolder, mover.Check(source, dir.File(@"Exe\Data"), portable: false));
        Assert.Equal(FolderProblem.None, mover.Check(source, dir.File(@"Exe\Data"), portable: true));
        Assert.Equal(FolderProblem.NotWritable, mover.Check(source, dir.File(@"a-file\Data"), portable: false)); // a file is in the way
        Assert.Equal(FolderProblem.NoSpace, Mover(dir, _ => 1024).Check(source, dir.File("Elsewhere"), portable: false));
        Assert.Equal(FolderProblem.None, mover.Check(source, dir.File("Elsewhere"), portable: false));
        Assert.False(Directory.Exists(dir.File("Elsewhere")));
        Assert.False(Directory.Exists(dir.File(@"Exe\Data")));
    }

    [Fact]
    public void Location_json_names_the_new_folder_and_the_default_folder_needs_none()
    {
        using var dir = new TempDir();
        var mover = Mover(dir);
        string custom = dir.File("Games Data");

        mover.WriteLocation(custom);

        using (var doc = JsonDocument.Parse(File.ReadAllText(mover.LocationFile)))
            Assert.Equal(custom, doc.RootElement.GetProperty("DataDir").GetString());
        Directory.CreateDirectory(custom);
        Assert.Equal(custom, DataPaths.Resolve(dir.File("Exe"), dir.File("AppData")).Root);
        Assert.Equal(custom, mover.InstalledRoot);

        mover.WriteLocation(mover.DefaultRoot);

        Assert.False(File.Exists(mover.LocationFile));
        Assert.Equal(mover.DefaultRoot, DataPaths.Resolve(dir.File("Exe"), dir.File("AppData")).Root);
    }

    [Fact]
    public void The_portable_flag_next_to_the_exe_puts_the_data_in_its_folder()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("Exe"));
        var mover = Mover(dir);

        mover.SetPortable(true);
        var portable = DataPaths.Resolve(dir.File("Exe"), dir.File("AppData"));
        Assert.True(portable.IsPortable);
        Assert.Equal(mover.PortableRoot, portable.Root);

        mover.SetPortable(false);
        Assert.False(DataPaths.Resolve(dir.File("Exe"), dir.File("AppData")).IsPortable);
        Assert.Throws<InvalidOperationException>(() => new DataFolderMover(dir.File("AppData"), null).SetPortable(true));
    }

    [Fact]
    public void Deleting_the_old_data_keeps_a_file_another_program_holds_and_says_so()
    {
        using var dir = new TempDir();
        string source = dir.File("Source");
        SeedData(source);
        var files = DataFolderMover.DataFiles(source);

        IReadOnlyList<string> left;
        using (new FileStream(Path.Combine(source, "log.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            left = DataFolderMover.Delete(source, files);

        Assert.Equal(["log.txt"], left);
        Assert.Equal(new[] { "cache.json.tmp", "holiday.jpg", "log.txt", Path.Combine("Other", "notes.json") }.Order(StringComparer.OrdinalIgnoreCase), Files(source));
        Assert.False(Directory.Exists(Path.Combine(source, "Profiles")));
    }

    [Fact]
    public void A_picked_folder_with_other_files_gets_a_folder_of_its_own_and_one_that_holds_them_is_refused()
    {
        using var dir = new TempDir();
        string source = dir.File("Source"), documents = dir.File("Documents");
        SeedData(source);
        Write(documents, "log.txt", "another program's log"); // common names, other programs' files
        Write(documents, Path.Combine("Profiles", "work.json"), "{\"name\":\"work\"}");
        Write(documents, "settings.json", "{\"theme\":\"dark\"}");
        var mover = Mover(dir);

        Assert.Equal(Path.Combine(documents, "SLiveTranslator"), DataFolderMover.DataFolderIn(documents));
        Assert.Equal(dir.File("New"), DataFolderMover.DataFolderIn(dir.File("New"))); // not there yet: used as it is
        Assert.Equal(source, DataFolderMover.DataFolderIn(source)); // the app's data: used as it is
        Assert.False(DataFolderMover.HasData(documents)); // told by what the files hold, not by their names
        Assert.Equal(FolderProblem.NotEmpty, mover.Check(source, documents, portable: false));
        Assert.Equal(FolderProblem.None, mover.Check(source, DataFolderMover.DataFolderIn(documents), portable: false));
    }

    [Fact]
    public async Task A_copy_never_writes_over_another_programs_file_of_the_same_name()
    {
        using var dir = new TempDir();
        string source = dir.File("Source"), target = dir.File("Target");
        SeedData(source);
        Write(target, "log.txt", "another program's log");

        await Assert.ThrowsAsync<IOException>(() => Mover(dir).CopyAsync(source, target, null, CancellationToken.None));

        Assert.Equal(["log.txt"], Files(target));
        Assert.Equal("another program's log", File.ReadAllText(Path.Combine(target, "log.txt")));
    }

    [Fact]
    public async Task Replacing_data_puts_the_old_files_back_when_one_cannot_be_moved()
    {
        using var dir = new TempDir();
        string source = dir.File("Source"), target = dir.File("Target");
        Write(source, "settings.json", "mine");
        SeedData(target, marker: "theirs");
        var before = Files(target).ToDictionary(f => f, f => File.ReadAllText(Path.Combine(target, f)));

        using (new FileStream(Path.Combine(target, "settings.json.bak-v1"), FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => Mover(dir).CopyAsync(source, target, null, CancellationToken.None));

        Assert.Equal(before.Keys.Order(StringComparer.OrdinalIgnoreCase), Files(target));
        foreach (var (file, text) in before) Assert.Equal(text, File.ReadAllText(Path.Combine(target, file)));
    }

    [Fact]
    public void The_same_folder_by_another_path_is_refused()
    {
        using var dir = new TempDir();
        string source = dir.File("Source"), link = dir.File("Link");
        SeedData(source);
        if (!Junction(link, source)) return; // the test machine cannot make a junction
        var mover = Mover(dir);

        Assert.Equal(FolderProblem.SameFolder, mover.Check(source, link, portable: false));
        Assert.Equal(FolderProblem.InsideCurrent, mover.Check(source, Path.Combine(link, "Backup"), portable: false));
        var files = DataFolderMover.DataFiles(source);
        Assert.Equal(files, DataFolderMover.DeleteMoved(source, link, files)); // a move there deletes nothing
        Assert.Equal(OurFiles.Order(StringComparer.OrdinalIgnoreCase), DataFolderMover.DataFiles(source));
        Assert.Empty(Directory.EnumerateFiles(source, ".slt-*")); // the probe is gone
        Directory.Delete(link);
    }

    static bool Junction(string link, string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void After_a_move_a_file_changed_since_the_copy_stays_in_the_old_folder()
    {
        using var dir = new TempDir();
        string source = dir.File("Source"), target = dir.File("Target");
        SeedData(source);
        foreach (var f in DataFolderMover.DataFiles(source)) Write(target, f, File.ReadAllText(Path.Combine(source, f)));
        File.AppendAllText(Path.Combine(source, "log.txt"), "written after the copy\n");

        var left = DataFolderMover.DeleteMoved(source, target, DataFolderMover.DataFiles(source));

        Assert.Equal(["log.txt"], left);
        Assert.True(File.Exists(Path.Combine(source, "log.txt")));
        Assert.False(File.Exists(Path.Combine(source, "settings.json")));
    }

    [Fact]
    public void A_folder_on_the_drive_of_an_exe_at_its_root_counts_as_the_program_folder()
    {
        using var dir = new TempDir();
        string source = dir.File("Source");
        SeedData(source);
        string root = Path.GetPathRoot(dir.Path)!; // the exe at the root of the drive, as on a USB stick
        var mover = new DataFolderMover(dir.File("AppData"), root, _ => null);

        Assert.Equal(FolderProblem.InsideAppFolder, mover.Check(source, Path.Combine(root, "SLT data that does not exist"), portable: false));
    }

    // ---- the page ----

    sealed class Page
    {
        public Page(TempDir dir)
        {
            Source = dir.File("Source");
            Target = dir.File("Target");
            SeedData(Source);
            Host = new FakeHost(dir.File("Profiles")) { DataPaths = DataPaths.At(Source, isPortable: false), DataFolders = Mover(dir) };
            Directory.CreateDirectory(dir.File("Exe"));
            Vm = new SettingsViewModel(Host, new FakeScheduler().Delay) { Prompts = Prompts };
            Prompts.Folder = Target;
        }

        public string Source { get; }
        public string Target { get; }
        public FakeHost Host { get; }
        public FakePrompts Prompts { get; } = new();
        public SettingsViewModel Vm { get; }
        public DataFolderMover Mover => Host.DataFolders;

        public string? Location => File.Exists(Mover.LocationFile)
            ? JsonDocument.Parse(File.ReadAllText(Mover.LocationFile)).RootElement.GetProperty("DataDir").GetString()
            : null;
    }

    [Fact]
    public async Task Change_moves_the_data_names_the_folder_in_location_json_and_the_app_uses_it_at_once()
    {
        using var dir = new TempDir();
        var page = new Page(dir);
        page.Prompts.Answers.Enqueue(DialogAnswer.Primary); // move

        await page.Vm.Storage.Folder.ChangeAsync();

        Assert.Equal(OurFiles.Order(StringComparer.OrdinalIgnoreCase), Files(page.Target));
        Assert.Equal(new[] { "cache.json.tmp", "holiday.jpg", Path.Combine("Other", "notes.json") }.Order(StringComparer.OrdinalIgnoreCase), Files(page.Source));
        Assert.Equal(page.Target, page.Location);
        var used = Assert.Single(page.Host.UsedFolders);
        Assert.Equal((page.Target, false), (used.Root, used.IsPortable));
        Assert.Equal(1, page.Host.Flushes); // what the app held in memory was written before the copy
        Assert.Equal(page.Target, page.Vm.Storage.Folder.Root);
        Assert.Equal(MessageKind.Success, page.Prompts.Messages.Last().Kind);
        Assert.False(page.Vm.Storage.Folder.IsBusy);
    }

    [Fact]
    public async Task Change_by_copy_keeps_the_old_folder_as_it_was()
    {
        using var dir = new TempDir();
        var page = new Page(dir);
        var before = Files(page.Source);
        page.Prompts.Answers.Enqueue(DialogAnswer.Secondary); // copy

        await page.Vm.Storage.Folder.ChangeAsync();

        Assert.Equal(before, Files(page.Source));
        Assert.Equal(OurFiles.Order(StringComparer.OrdinalIgnoreCase), Files(page.Target));
        Assert.Equal(page.Target, Assert.Single(page.Host.UsedFolders).Root);
    }

    [Fact]
    public async Task A_folder_with_data_of_its_own_can_be_used_as_it_is_from_the_next_start()
    {
        using var dir = new TempDir();
        var page = new Page(dir);
        SeedData(page.Target, marker: "theirs");
        page.Prompts.Answers.Enqueue(DialogAnswer.Primary); // use its data

        await page.Vm.Storage.Folder.ChangeAsync();

        Assert.Contains("theirs", File.ReadAllText(Path.Combine(page.Target, "settings.json")));
        Assert.Equal(page.Target, page.Location);
        Assert.Empty(page.Host.UsedFolders); // loaded by the next start, not mixed into this run
        Assert.Equal(1, page.Host.Restarts); // the user agreed to restart
        Assert.Contains(page.Target, page.Vm.Storage.Folder.NextStart);
        Assert.Equal(0, page.Host.Flushes);
    }

    [Fact]
    public async Task A_folder_with_data_of_its_own_can_be_replaced_with_the_data_in_use_or_left_alone()
    {
        using var dir = new TempDir();
        var page = new Page(dir);
        SeedData(page.Target, marker: "theirs");

        page.Prompts.Answers.Enqueue(DialogAnswer.Cancel);
        await page.Vm.Storage.Folder.ChangeAsync();
        Assert.Contains("theirs", File.ReadAllText(Path.Combine(page.Target, "settings.json")));
        Assert.Null(page.Location);
        Assert.Empty(page.Host.UsedFolders);

        page.Prompts.Answers.Enqueue(DialogAnswer.Secondary); // replace with mine
        page.Prompts.Answers.Enqueue(DialogAnswer.Secondary); // by copy
        await page.Vm.Storage.Folder.ChangeAsync();
        Assert.Contains("mine", File.ReadAllText(Path.Combine(page.Target, "settings.json")));
        Assert.Equal(page.Target, page.Location);
        Assert.Equal(0, page.Host.Restarts);
    }

    [Fact]
    public async Task A_refused_folder_changes_nothing_and_says_why()
    {
        using var dir = new TempDir();
        var page = new Page(dir);
        page.Prompts.Folder = Path.Combine(page.Source, "Backup");

        await page.Vm.Storage.Folder.ChangeAsync();

        var (_, text, kind) = page.Prompts.Messages.Last();
        Assert.Equal(MessageKind.Error, kind);
        Assert.Contains("inside the folder in use", text);
        Assert.Empty(page.Prompts.Questions);
        Assert.Empty(page.Host.UsedFolders);
        Assert.Null(page.Location);
    }

    [Fact]
    public async Task Portable_copies_the_data_next_to_the_exe_after_asking_and_off_goes_back_to_the_installed_folder()
    {
        using var dir = new TempDir();
        var page = new Page(dir);
        var folder = page.Vm.Storage.Folder;
        Assert.True(folder.CanTogglePortable);
        Assert.False(folder.IsPortable);

        page.Prompts.Confirm = false;
        await folder.SetPortableAsync(true);
        Assert.Empty(page.Host.UsedFolders); // the copy offer was declined
        Assert.False(File.Exists(dir.File(@"Exe\portable.flag")));

        page.Prompts.Confirm = true;
        await folder.SetPortableAsync(true);
        Assert.True(File.Exists(dir.File(@"Exe\portable.flag")));
        Assert.Equal(OurFiles.Order(StringComparer.OrdinalIgnoreCase), Files(page.Mover.PortableRoot!));
        Assert.Equal((page.Mover.PortableRoot, true), (page.Host.UsedFolders[^1].Root, page.Host.UsedFolders[^1].IsPortable));
        Assert.True(folder.IsPortable);
        Assert.False(folder.ChangeCommand.CanExecute(null)); // portable data stays next to the exe

        await folder.SetPortableAsync(false);
        Assert.False(File.Exists(dir.File(@"Exe\portable.flag")));
        Assert.Equal((page.Mover.DefaultRoot, false), (page.Host.UsedFolders[^1].Root, page.Host.UsedFolders[^1].IsPortable));
        Assert.True(File.Exists(Path.Combine(page.Mover.DefaultRoot, "settings.json")));
        Assert.Null(page.Location); // the default folder needs no location.json
        Assert.True(File.Exists(Path.Combine(page.Source, "settings.json"))); // portable copies, it never moves
    }

    [Fact]
    public void The_page_lists_the_apps_files_their_sizes_and_the_free_space()
    {
        using var dir = new TempDir();
        var page = new Page(dir);
        page.Host.DataFolders = Mover(dir, _ => 5L * 1024 * 1024 * 1024);

        page.Vm.Storage.Folder.Refresh();

        var names = page.Vm.Storage.Folder.Files.Select(f => f.Name).ToList();
        Assert.Contains("cache.json", names);
        Assert.Contains("Profiles (2 files)", names);
        Assert.DoesNotContain("holiday.jpg", names);
        Assert.Equal("293.0 KB", page.Vm.Storage.Folder.Files.Single(f => f.Name == "cache.json").Size);
        Assert.Equal("Free on the drive: 5.00 GB", page.Vm.Storage.Folder.FreeText);
    }
}
