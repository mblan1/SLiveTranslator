using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using SLiveTranslator.Services;
using Xunit;

namespace SLiveTranslator.Tests;

public class PollingPolicyTests
{
    const int Own = 100, Game = 200, Shell = 300, Browser = 400, RestartedGame = 500;
    static readonly IntPtr GameWindow = new(0x1234), BrowserWindow = new(0x5678), Desktop = new(0x10), RestartedWindow = new(0x4321);
    static readonly Rectangle Region = new(327, 1094, 1825, 212);

    /// <summary>The game shows the region and is in front, until a test changes that.</summary>
    sealed class FakeProbe : IWindowProbe
    {
        public (IntPtr Window, int ProcessId) InFront { get; set; } = (GameWindow, Game);
        public (IntPtr Window, int ProcessId) AtRegion { get; set; } = (GameWindow, Game);
        public HashSet<IntPtr> Minimized { get; } = new();
        public HashSet<IntPtr> Closed { get; } = new();
        public Point? AskedAt { get; private set; }

        public (IntPtr Window, int ProcessId) Foreground() => InFront;

        public (IntPtr Window, int ProcessId) At(Point screenPoint)
        {
            AskedAt = screenPoint;
            return AtRegion;
        }

        public bool IsMinimized(IntPtr window) => Minimized.Contains(window);
        public bool Exists(IntPtr window) => !Closed.Contains(window);
        public int ShellProcessId() => Shell;
        public string? ProcessName(int processId) => processId == Game ? "witcher3" : null;
    }

    static PollingPolicy Policy(FakeProbe probe, GameWindow? known = null) => new(probe, Own, Region, known);

    [Fact]
    public void The_game_is_the_program_that_shows_the_middle_of_the_region()
    {
        // The hotkey pressed while a browser on the other monitor had the focus: the browser is not the game.
        var probe = new FakeProbe { InFront = (BrowserWindow, Browser) };

        var policy = Policy(probe);

        Assert.Equal(new GameWindow(GameWindow, Game), policy.Game);
        Assert.Equal(new Point(327 + 912, 1094 + 106), probe.AskedAt);
        Assert.True(policy.GameAway(true));
        probe.InFront = (GameWindow, Game);
        Assert.False(policy.GameAway(true));
    }

    [Fact]
    public void Started_from_the_tray_menu_the_game_behind_it_is_still_found()
    {
        var probe = new FakeProbe { InFront = (new IntPtr(0x77), Own) };
        var policy = Policy(probe);

        Assert.Equal(Game, policy.Game?.ProcessId);
        probe.InFront = (BrowserWindow, Browser);
        Assert.True(policy.GameAway(true));
    }

    [Theory]
    [InlineData(Shell)] // only the desktop shows the region
    [InlineData(Own)] // the settings window covers it
    [InlineData(0)] // nothing there
    public void A_game_not_known_yet_is_the_first_program_in_front_that_shows_the_region(int atRegion)
    {
        var probe = new FakeProbe { AtRegion = (Desktop, atRegion), InFront = (BrowserWindow, Browser) };
        var policy = Policy(probe);
        Assert.Null(policy.Game);
        Assert.False(policy.GameAway(true)); // the browser in front does not show the region: still unknown, no pause
        Assert.Null(policy.Game);

        probe.AtRegion = probe.InFront = (GameWindow, Game); // the game started
        Assert.False(policy.GameAway(true));

        Assert.Equal(new GameWindow(GameWindow, Game), policy.Game);
        probe.InFront = (BrowserWindow, Browser);
        Assert.True(policy.GameAway(true));
    }

    [Fact]
    public void A_minimized_window_at_the_region_is_not_taken_for_the_game()
    {
        var probe = new FakeProbe();
        probe.Minimized.Add(GameWindow);

        Assert.Null(Policy(probe).Game);
    }

    [Fact]
    public void A_game_that_was_closed_and_started_again_is_found_again()
    {
        var probe = new FakeProbe();
        var policy = Policy(probe);
        probe.Closed.Add(GameWindow);
        probe.AtRegion = probe.InFront = (RestartedWindow, RestartedGame);

        Assert.False(policy.GameAway(true));

        Assert.Equal(new GameWindow(RestartedWindow, RestartedGame), policy.Game);
        probe.InFront = (BrowserWindow, Browser);
        Assert.True(policy.GameAway(true));
    }

    [Fact]
    public void A_restart_after_a_dialog_keeps_the_game_whatever_the_dialog_left_in_front()
    {
        // Settings saved while a full-screen game was minimized: a browser now shows the region and has the focus.
        var probe = new FakeProbe();
        var before = Policy(probe).Game;
        probe.Minimized.Add(GameWindow);
        probe.AtRegion = probe.InFront = (BrowserWindow, Browser);

        var restarted = Policy(probe, before);

        Assert.Equal(before, restarted.Game);
        Assert.True(restarted.GameAway(true));
        probe.Minimized.Clear();
        probe.AtRegion = probe.InFront = (GameWindow, Game);
        Assert.False(restarted.GameAway(true));

        probe.Closed.Add(GameWindow); // a game closed meanwhile is not kept
        probe.AtRegion = probe.InFront = (BrowserWindow, Browser);
        Assert.Equal(Browser, Policy(probe, before).Game?.ProcessId);
    }

    [Fact]
    public void Subtitles_pause_while_another_program_is_in_front_or_the_game_is_minimized()
    {
        var probe = new FakeProbe();
        var policy = Policy(probe);
        Assert.False(policy.GameAway(true));

        probe.InFront = (BrowserWindow, Browser);
        Assert.True(policy.GameAway(true));

        probe.InFront = (GameWindow, Game);
        probe.Minimized.Add(GameWindow);
        Assert.True(policy.GameAway(true));
    }

    [Fact]
    public void No_window_in_front_for_a_moment_is_not_the_game_going_away()
    {
        var probe = new FakeProbe();
        var policy = Policy(probe);
        probe.InFront = (IntPtr.Zero, 0);

        Assert.False(policy.GameAway(true));
        probe.Minimized.Add(GameWindow);
        Assert.True(policy.GameAway(true));
    }

    [Fact]
    public void Subtitles_never_pause_when_the_setting_is_off()
    {
        var probe = new FakeProbe();
        var policy = Policy(probe);
        probe.InFront = (BrowserWindow, Browser);

        Assert.False(policy.GameAway(false));
    }

    [Fact]
    public void With_pausing_off_the_game_is_still_found_since_its_process_picks_the_profile()
    {
        var probe = new FakeProbe { AtRegion = (Desktop, Shell), InFront = (BrowserWindow, Browser) };
        var policy = Policy(probe);
        Assert.Null(policy.Game);

        probe.AtRegion = probe.InFront = (GameWindow, Game); // the game started
        Assert.False(policy.GameAway(false));

        Assert.Equal(new GameWindow(GameWindow, Game), policy.Game);
        probe.InFront = (BrowserWindow, Browser);
        Assert.False(policy.GameAway(false)); // and subtitles still never pause
    }

    [Fact]
    public void Another_window_of_the_game_in_front_is_still_the_game()
    {
        var probe = new FakeProbe();
        var policy = Policy(probe);
        probe.InFront = (new IntPtr(0x9999), Game); // the game's launcher or a second window

        Assert.False(policy.GameAway(true));
    }

    [Fact]
    public void Low_impact_lowers_the_process_priority_and_restores_it()
    {
        using var process = Process.GetCurrentProcess();
        var before = process.PriorityClass;

        using (new LowPriorityScope())
        {
            process.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, process.PriorityClass);
        }

        process.Refresh();
        Assert.Equal(before, process.PriorityClass);
    }
}
