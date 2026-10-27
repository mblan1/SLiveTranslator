using System;
using System.Drawing;
using SLiveTranslator.Capture;

namespace SLiveTranslator.Services;

/// <summary>What is on the desktop; injectable so tests decide.</summary>
internal interface IWindowProbe
{
    /// <summary>The foreground window and its process (0 when there is none).</summary>
    (IntPtr Window, int ProcessId) Foreground();

    /// <summary>
    /// The top-level window that shows a screen point (physical pixels) and its process; click-through windows such as
    /// the overlay are passed over.
    /// </summary>
    (IntPtr Window, int ProcessId) At(Point screenPoint);

    bool IsMinimized(IntPtr window);

    /// <summary>The window was not closed.</summary>
    bool Exists(IntPtr window);

    /// <summary>The process of the desktop and taskbar (explorer.exe).</summary>
    int ShellProcessId();
}

/// <summary>A window of the game and its process.</summary>
internal sealed record GameWindow(IntPtr Handle, int ProcessId);

/// <summary>
/// Which program is the game, and whether it is in front; while it is not, the subtitle loop only checks once a second
/// whether it is back. The game is the program whose window shows the subtitle region, not the one that had the focus
/// when subtitles started: that may be the tray menu, a dialog that just closed, a browser on the other monitor or a
/// launcher. A game not known yet (nothing but the desktop showed the region) is the first program in front that shows
/// it; one that was closed is looked for again the same way, so a restarted game is found. While the game is not
/// known, subtitles never pause.
/// </summary>
internal sealed class PollingPolicy
{
    public const int AwayIntervalMs = 1000;

    readonly IWindowProbe _probe;
    readonly int _ownProcessId;
    readonly Point _regionCentre;

    /// <param name="region">The subtitle region, physical pixels.</param>
    /// <param name="known">The game found by an earlier session; kept while its window exists.</param>
    public PollingPolicy(IWindowProbe probe, int ownProcessId, Rectangle region, GameWindow? known = null)
    {
        _probe = probe;
        _ownProcessId = ownProcessId;
        _regionCentre = new Point(region.Left + region.Width / 2, region.Top + region.Height / 2);
        Game = known is not null && probe.Exists(known.Handle) ? known : ShowingRegion();
    }

    /// <summary>The game, or null while it is not known.</summary>
    public GameWindow? Game { get; private set; }

    /// <summary>
    /// True while subtitles need not be read: another program is in front, or the game is minimized. No window in front
    /// at all happens for a moment while the focus moves, and does not count.
    /// </summary>
    public bool GameAway(bool pauseWhenUnfocused)
    {
        if (!pauseWhenUnfocused) return false;
        if (Game is { } closed && !_probe.Exists(closed.Handle)) Game = null;
        var (window, processId) = _probe.Foreground();
        if (Game is not { } game)
        {
            if (IsOtherProgram(processId) && _probe.At(_regionCentre).Window == window) Game = new GameWindow(window, processId);
            return false;
        }
        return (processId != 0 && processId != game.ProcessId) || _probe.IsMinimized(window) || _probe.IsMinimized(game.Handle);
    }

    GameWindow? ShowingRegion()
    {
        var (window, processId) = _probe.At(_regionCentre);
        return IsOtherProgram(processId) && !_probe.IsMinimized(window) ? new GameWindow(window, processId) : null;
    }

    /// <summary>Neither this app (its tray menu, its windows) nor the desktop.</summary>
    bool IsOtherProgram(int processId) => processId != 0 && processId != _ownProcessId && processId != _probe.ShellProcessId();
}

/// <summary>The real desktop.</summary>
internal sealed class SystemWindowProbe : IWindowProbe
{
    public static readonly SystemWindowProbe Instance = new();

    public (IntPtr Window, int ProcessId) Foreground()
    {
        var window = Native.GetForegroundWindow();
        return (window, ProcessOf(window));
    }

    public (IntPtr Window, int ProcessId) At(Point screenPoint)
    {
        var child = Native.WindowFromPoint(new Native.POINT { X = screenPoint.X, Y = screenPoint.Y });
        var window = child == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(child, Native.GA_ROOT);
        return (window, ProcessOf(window));
    }

    public bool IsMinimized(IntPtr window) => window != IntPtr.Zero && Native.IsIconic(window);

    public bool Exists(IntPtr window) => window != IntPtr.Zero && Native.IsWindow(window);

    public int ShellProcessId() => ProcessOf(Native.GetShellWindow());

    static int ProcessOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return 0;
        Native.GetWindowThreadProcessId(window, out uint processId);
        return (int)processId;
    }
}
