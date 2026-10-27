using System;
using System.IO;
using SLiveTranslator.Models;
using Xunit;

namespace SLiveTranslator.Tests;

public class DataPathsTests
{
    static void WriteLocation(TempDir appData, string json)
    {
        Directory.CreateDirectory(Path.Combine(appData.Path, DataPaths.AppFolderName));
        File.WriteAllText(Path.Combine(appData.Path, DataPaths.AppFolderName, DataPaths.LocationFileName), json);
    }

    static string LocationJson(string dir) => $$"""{"SchemaVersion":1,"DataDir":{{System.Text.Json.JsonSerializer.Serialize(dir)}}}""";

    [Fact]
    public void Tests_run_in_their_own_folder_not_the_real_data_folder()
    {
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataPaths.AppFolderName);

        Assert.Equal(TestEnvironment.DataDir, DataPaths.Current.Root);
        Assert.False(DataPaths.Current.Root.StartsWith(real, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(TestEnvironment.DataDir, "log.txt"), Log.FilePath);
    }

    [Fact]
    public void Portable_flag_next_to_the_exe_wins_over_everything()
    {
        using var exe = new TempDir();
        using var appData = new TempDir();
        using var custom = new TempDir();
        File.WriteAllText(exe.File(DataPaths.PortableFlagName), "");
        WriteLocation(appData, LocationJson(custom.Path));

        var paths = DataPaths.Resolve(exe.Path, appData.Path);

        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine(exe.Path, "Data"), paths.Root);
    }

    [Fact]
    public void Location_file_moves_the_data_folder()
    {
        using var exe = new TempDir();
        using var appData = new TempDir();
        using var custom = new TempDir();
        WriteLocation(appData, LocationJson(custom.Path));

        var paths = DataPaths.Resolve(exe.Path, appData.Path);

        Assert.False(paths.IsPortable);
        Assert.Equal(custom.Path, paths.Root);
        Assert.Equal(custom.File("settings.json"), paths.SettingsFile);
        Assert.Null(paths.UnavailableLocation);
    }

    [Fact]
    public void Default_is_the_app_folder_in_appdata()
    {
        using var exe = new TempDir();
        using var appData = new TempDir();

        var paths = DataPaths.Resolve(exe.Path, appData.Path);
        string root = Path.Combine(appData.Path, DataPaths.AppFolderName);

        Assert.False(paths.IsPortable);
        Assert.Equal(root, paths.Root);
        Assert.Equal(Path.Combine(root, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(root, "cache.json"), paths.CacheFile);
        Assert.Equal(Path.Combine(root, "log.txt"), paths.LogFile);
        Assert.Equal(Path.Combine(root, "Profiles"), paths.ProfilesDir);
        Assert.Equal(root, DataPaths.Resolve(null, appData.Path).Root);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""["C:\\Games"]""")]
    [InlineData("""{"SchemaVersion":1}""")]
    [InlineData("""{"SchemaVersion":1,"DataDir":""}""")]
    [InlineData("""{"SchemaVersion":1,"DataDir":"relative\\folder"}""")]
    public void Unusable_location_file_falls_back_to_the_default(string json)
    {
        using var exe = new TempDir();
        using var appData = new TempDir();
        WriteLocation(appData, json);

        var paths = DataPaths.Resolve(exe.Path, appData.Path);

        Assert.Equal(Path.Combine(appData.Path, DataPaths.AppFolderName), paths.Root);
        Assert.Null(paths.UnavailableLocation); // a broken file is not a missing drive
    }

    [Fact]
    public void Location_pointing_at_a_missing_folder_falls_back_to_the_default()
    {
        using var exe = new TempDir();
        using var appData = new TempDir();
        string unplugged = Path.Combine(exe.Path, "unplugged-drive");
        WriteLocation(appData, LocationJson(unplugged));

        var paths = DataPaths.Resolve(exe.Path, appData.Path);

        Assert.Equal(Path.Combine(appData.Path, DataPaths.AppFolderName), paths.Root);
        Assert.Equal(unplugged, paths.UnavailableLocation); // so the app can say why the settings look empty
    }
}
