using System;
using System.IO;
using System.Runtime.CompilerServices;
using SLiveTranslator.Models;
using SLiveTranslator.UI;

namespace SLiveTranslator.Tests;

/// <summary>
/// Gives the whole test run its own data folder, so no test writes to the user's real %APPDATA%\SLiveTranslator.
/// A module initializer runs before any test code; xUnit 2 has no assembly-wide fixture.
/// </summary>
static class TestEnvironment
{
    public static string DataDir { get; } = Path.Combine(Path.GetTempPath(), "SLiveTranslator.Tests", "run-" + Guid.NewGuid().ToString("N"));

    /// <summary>Tests that call real services run only with SLT_LIVE=1; otherwise they return at once.</summary>
    public static bool LiveTestsEnabled => Environment.GetEnvironmentVariable("SLT_LIVE") == "1";

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(DataDir);
        DataPaths.UseForTests(DataDir);
        // Tests read the English notices whatever the PC's display language; no test switches the shared instance.
        Loc.Instance.SetLanguage(Loc.English);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TempDir.TryDelete(DataDir);
    }
}

/// <summary>A fresh folder under %TEMP% that is deleted on dispose.</summary>
sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SLiveTranslator.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => TryDelete(Path);

    public static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a file still open elsewhere; %TEMP% is cleaned up eventually
        }
    }
}
