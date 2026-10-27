using System;
using System.Diagnostics;

namespace SLiveTranslator.Services;

/// <summary>
/// Runs the whole process at below-normal priority until disposed ("low impact" subtitles), so the game always gets
/// the CPU first; restores the priority it had before.
/// </summary>
internal sealed class LowPriorityScope : IDisposable
{
    readonly ProcessPriorityClass _previous;
    bool _disposed;

    public LowPriorityScope()
    {
        using var process = Process.GetCurrentProcess();
        _previous = process.PriorityClass;
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        using var process = Process.GetCurrentProcess();
        process.PriorityClass = _previous;
    }
}
