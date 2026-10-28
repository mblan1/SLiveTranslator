using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace SLiveTranslator.Services;

/// <summary>The values of one registry key; the app's is <see cref="RunKey"/>, tests use one in memory.</summary>
public interface IRegistry
{
    /// <returns>The value as text; null when there is none.</returns>
    string? GetValue(string name);

    void SetValue(string name, string value);
    void DeleteValue(string name);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run: what Windows starts when the user signs in.</summary>
public sealed class RunKey : IRegistry
{
    const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Path);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// "Start with Windows": a Run value that starts this exe with <see cref="Argument"/>, so it comes up quietly in the
/// tray. The registry is the truth, not the settings: the value may have been removed by a cleanup tool, or point at
/// the exe's old place after it was moved.
/// </summary>
/// <param name="exePath">The running exe (<see cref="Environment.ProcessPath"/>); null when unknown.</param>
public sealed class StartupRegistration(IRegistry registry, string? exePath)
{
    public const string ValueName = "SLiveTranslator";

    /// <summary>Starts in the tray without the "running" notice; read by <see cref="StartupArgs"/>.</summary>
    public const string Argument = "--startup";

    /// <summary>The value written: the exe quoted, as a path with spaces needs, then <see cref="Argument"/>.</summary>
    public string? Command => string.IsNullOrEmpty(exePath) ? null : $"\"{exePath}\" {Argument}";

    public bool IsEnabled => registry.GetValue(ValueName) is { Length: > 0 };

    /// <summary>
    /// The value starts an exe that is not there any more (this one was moved since). One that is there, another copy
    /// such as an installed build while a test build runs, stays the user's choice.
    /// </summary>
    public bool IsStale => registry.GetValue(ValueName) is { Length: > 0 } value && Command is { } command
                           && !value.Trim().Equals(command, StringComparison.OrdinalIgnoreCase)
                           && !(ExeOf(value) is { } exe && System.IO.File.Exists(exe));

    /// <summary>The exe a Run value starts: the quoted path, or the text up to the first argument.</summary>
    static string? ExeOf(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"')) return value.IndexOf('"', 1) is var end and > 1 ? value[1..end] : null;
        int args = value.IndexOf(" --", StringComparison.Ordinal);
        return args >= 0 ? value[..args] : value;
    }

    /// <exception cref="InvalidOperationException">Turning it on while the exe's path is unknown.</exception>
    public void SetEnabled(bool on)
    {
        if (!on)
        {
            registry.DeleteValue(ValueName);
            return;
        }
        registry.SetValue(ValueName, Command ?? throw new InvalidOperationException("The path of the running program is not known."));
    }

    /// <summary>Points a stale value at this exe; a missing one stays missing.</summary>
    /// <returns>Whether the value was rewritten.</returns>
    public bool RepairIfStale()
    {
        if (!IsStale) return false;
        SetEnabled(true);
        return true;
    }
}

/// <summary>What the command line asks for: a quiet start in the tray (<see cref="StartupRegistration.Argument"/>) or a CLI mode.</summary>
/// <param name="Cli">The arguments of a CLI mode ("--selftest dir"); null for a normal start.</param>
public sealed record StartupArgs(bool Quiet, string[]? Cli)
{
    /// <summary>"--startup" is taken out first, so it never reaches the "--" CLI branch as an unknown option.</summary>
    public static StartupArgs Parse(string[] args)
    {
        bool quiet = false;
        var rest = new List<string>();
        foreach (var arg in args)
        {
            if (arg.Equals(StartupRegistration.Argument, StringComparison.OrdinalIgnoreCase)) quiet = true;
            else rest.Add(arg);
        }
        bool cli = rest.Count > 0 && rest[0].StartsWith("--", StringComparison.Ordinal);
        return new StartupArgs(quiet, cli ? rest.ToArray() : null);
    }
}
