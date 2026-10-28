using System;
using System.IO;
using System.Linq;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.UI;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>Start with Windows on an in-memory Run key (the real registry is never touched), the --startup argument, and the General page.</summary>
public class StartupRegistrationTests
{
    const string Exe = @"C:\Program Files\SLive Translator\SLiveTranslator.exe";

    [Fact]
    public void Enabling_writes_the_quoted_exe_with_the_startup_argument_and_disabling_removes_it()
    {
        var registry = new FakeRegistry();
        var startup = new StartupRegistration(registry, Exe);
        Assert.False(startup.IsEnabled);

        startup.SetEnabled(true);

        Assert.Equal("\"C:\\Program Files\\SLive Translator\\SLiveTranslator.exe\" --startup", registry.Values[StartupRegistration.ValueName]);
        Assert.True(startup.IsEnabled);
        Assert.False(startup.IsStale);

        startup.SetEnabled(false);
        Assert.Empty(registry.Values);
        Assert.False(startup.IsEnabled);
        startup.SetEnabled(false); // nothing there to remove
    }

    [Fact]
    public void A_value_left_pointing_at_the_old_place_of_the_exe_is_repaired_and_a_missing_one_stays_missing()
    {
        var registry = new FakeRegistry();
        var startup = new StartupRegistration(registry, Exe);
        Assert.False(startup.RepairIfStale());
        Assert.Empty(registry.Values);

        registry.Values[StartupRegistration.ValueName] = "\"D:\\Downloads\\SLiveTranslator.exe\" --startup";
        Assert.True(startup.IsEnabled);
        Assert.True(startup.IsStale);

        Assert.True(startup.RepairIfStale());
        Assert.Equal(startup.Command, registry.Values[StartupRegistration.ValueName]);
        Assert.False(startup.RepairIfStale());

        registry.Values[StartupRegistration.ValueName] = startup.Command!.ToUpperInvariant(); // Windows paths ignore case
        Assert.False(startup.IsStale);
    }

    [Fact]
    public void A_value_that_starts_another_copy_still_there_is_left_alone()
    {
        using var dir = new TempDir();
        string other = dir.File("Installed SLiveTranslator.exe");
        File.WriteAllText(other, "");
        var registry = new FakeRegistry();
        registry.Values[StartupRegistration.ValueName] = $"\"{other}\" --startup";
        var startup = new StartupRegistration(registry, Exe); // a test build opened its settings

        Assert.True(startup.IsEnabled);
        Assert.False(startup.IsStale);
        Assert.False(startup.RepairIfStale());
        Assert.Equal($"\"{other}\" --startup", registry.Values[StartupRegistration.ValueName]);

        File.Delete(other); // that copy went away: this one takes over
        Assert.True(startup.RepairIfStale());
        Assert.Equal(startup.Command, registry.Values[StartupRegistration.ValueName]);
    }

    [Fact]
    public void Without_the_exe_path_it_cannot_be_turned_on()
    {
        var startup = new StartupRegistration(new FakeRegistry(), null);
        Assert.Null(startup.Command);
        Assert.Throws<InvalidOperationException>(() => startup.SetEnabled(true));
    }

    [Theory]
    [InlineData(new string[0], false, null)]
    [InlineData(new[] { "--startup" }, true, null)]
    [InlineData(new[] { "--STARTUP" }, true, null)]
    [InlineData(new[] { "--selftest", @"C:\out" }, false, "--selftest|C:\\out")]
    [InlineData(new[] { "--startup", "--bench-subtitles" }, true, "--bench-subtitles")]
    [InlineData(new[] { "notes.txt" }, false, null)]
    public void The_startup_argument_is_taken_out_before_the_cli_branch(string[] args, bool quiet, string? cli)
    {
        var start = StartupArgs.Parse(args);

        Assert.Equal(quiet, start.Quiet);
        Assert.Equal(cli, start.Cli is null ? null : string.Join("|", start.Cli));
    }

    [Fact]
    public void The_app_parses_its_arguments_before_it_looks_for_a_cli_mode()
    {
        string app = File.ReadAllText(Path.Combine(SourceDir(), "App.xaml.cs"));
        int parse = app.IndexOf("StartupArgs.Parse(e.Args)", StringComparison.Ordinal);
        int cli = app.IndexOf("RunCliAsync(", StringComparison.Ordinal);

        Assert.True(parse > 0 && parse < cli);
        Assert.DoesNotContain("e.Args[0].StartsWith", app);
        Assert.Contains("if (start.Quiet) return;", app);
    }

    static string SourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SLiveTranslator.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "SLiveTranslator");
    }

    [Fact]
    public void The_general_page_shows_the_registry_repairs_a_stale_value_on_open_and_switches_at_once()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles"));
        host.Registry.Values[StartupRegistration.ValueName] = "\"C:\\Old\\SLiveTranslator.exe\" --startup";
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };

        Assert.True(vm.General.StartWithWindows);
        Assert.Equal(host.Startup.Command, host.Registry.Values[StartupRegistration.ValueName]);

        vm.General.StartWithWindows = false;
        Assert.Empty(host.Registry.Values);
        Assert.False(host.Current.General.StartWithWindows);
        Assert.False(vm.HasChanges); // applied, not waiting for Save

        vm.General.StartWithWindows = true;
        Assert.True(host.Current.General.StartWithWindows);
        Assert.Equal(host.Startup.Command, host.Registry.Values[StartupRegistration.ValueName]);
    }

    [Fact]
    public void The_general_page_edits_language_and_theme_for_save_and_tells_what_the_app_runs_on()
    {
        using var dir = new TempDir();
        var host = new FakeHost(dir.File("Profiles")) { OcrLanguages = ["en-US", "vi-VN"] };
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };
        var page = vm.General;

        Assert.Equal(["Same as Windows", "English", "Tiếng Việt"], page.Languages.Select(l => l.Label));
        page.SelectedLanguage = page.Languages.Single(l => l.Value == "vi");
        page.SelectedTheme = page.Themes.Single(t => t.Value == AppTheme.Dark);

        Assert.Equal(("vi", AppTheme.Dark), (vm.Working.Ui.Language, vm.Working.General.Theme));
        Assert.Equal(SettingsChange.Language | SettingsChange.Theme, vm.PendingChanges);
        Assert.Equal("en-US, vi-VN", page.OcrLanguages);
        Assert.Equal(host.DataPaths.Root, page.DataFolder);
        Assert.False(string.IsNullOrWhiteSpace(page.Version));
        Assert.Contains(".NET", page.Runtime);

        page.RunSetupCommand.Execute(null);
        Assert.True(vm.Welcome.IsOpen);
    }
}
