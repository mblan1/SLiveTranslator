using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Overlay;
using SLiveTranslator.UI.Pages;
using Xunit;
using DRect = System.Drawing.Rectangle;
using DSize = System.Drawing.Size;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.Tests;

/// <summary>
/// The display region, Private / Shared on screen, the box behind the text and the timing of the lines: their rules,
/// their settings, and the pages that edit them.
/// </summary>
public class DisplayAndTimingTests
{
    static readonly DRect Screen = new(0, 0, 1920, 1080);
    static readonly DRect Read = new(384, 888, 1152, 132);
    static readonly DSize Box = new(900, 90);

    // ---- timing ----

    [Fact]
    public void A_fixed_stay_keeps_the_lines_that_long_but_never_shorter_than_the_shortest_time_in_all()
    {
        var look = new OverlaySettings { StaySeconds = 6, MinSeconds = 2 };

        Assert.Equal(TimeSpan.FromSeconds(6), SubtitleTiming.Stay(look, "Xin chào.", TimeSpan.FromSeconds(3)));

        look.StaySeconds = 0; // hide with the game…
        Assert.Equal(TimeSpan.Zero, SubtitleTiming.Stay(look, "Ừ.", TimeSpan.FromSeconds(3)));
        // …but a line the game showed for half a second still gets its 2 seconds on screen
        Assert.Equal(TimeSpan.FromSeconds(1.5), SubtitleTiming.Stay(look, "Ừ.", TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void A_stay_by_length_follows_the_reading_speed_and_until_next_never_ends_by_itself()
    {
        var look = new OverlaySettings { Stay = StayMode.ByLength, ReadingSpeed = 20, MinSeconds = 1.5 };

        Assert.Equal(TimeSpan.FromSeconds(4), SubtitleTiming.Stay(look, new string('a', 80), TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(1.5), SubtitleTiming.Stay(look, "Ừ.", TimeSpan.FromSeconds(3))); // the shortest time
        look.Stay = StayMode.UntilNext;
        Assert.Null(SubtitleTiming.Stay(look, "Ừ.", TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Replacing_lines_shows_only_the_newest_and_values_out_of_range_are_clamped()
    {
        Assert.Equal(3, SubtitleTiming.VisibleLines(new OverlaySettings { MaxLines = 3 }));
        Assert.Equal(1, SubtitleTiming.VisibleLines(new OverlaySettings { MaxLines = 3, NewLines = NewLineMode.Replace }));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), SubtitleTiming.Fade(9999));
        Assert.Equal(TimeSpan.Zero, SubtitleTiming.Fade(-5));
        Assert.Equal(TimeSpan.FromSeconds(20), SubtitleTiming.Stay(new OverlaySettings { StaySeconds = 99 }, "", TimeSpan.FromSeconds(9)));
    }

    // ---- where the lines go ----

    static SubtitleStyle Style(SubtitlePosition position, bool avoid = false, DRect? custom = null, TextAlign align = TextAlign.Center) =>
        new SubtitleStyle(1, 0, position, false) { AvoidRegion = avoid, DisplayRegion = custom, Align = align };

    [Fact]
    public void The_lines_go_above_below_over_or_in_their_own_box()
    {
        var above = SubtitlePlacement.Place(Style(SubtitlePosition.AboveRegion), Read, Box, Screen, 6);
        var below = SubtitlePlacement.Place(Style(SubtitlePosition.BelowRegion), new DRect(384, 600, 1152, 132), Box, Screen, 6);
        var over = SubtitlePlacement.Place(Style(SubtitlePosition.OverRegion), Read, Box, Screen, 6);
        var custom = new DRect(100, 80, 800, 140);
        var inBox = SubtitlePlacement.Place(Style(SubtitlePosition.Custom, custom: custom), Read, Box with { Width = 700 }, Screen, 6);

        Assert.Equal(Read.Top - 6, above.Bottom);
        Assert.Equal(732 + 6, below.Top);
        Assert.True(over.IntersectsWith(Read));
        Assert.Equal(Read.Left + (Read.Width - Box.Width) / 2, over.Left);
        Assert.True(custom.Contains(inBox), $"{inBox} is not in {custom}");
    }

    [Fact]
    public void Text_alignment_puts_the_box_on_that_side_of_the_region()
    {
        Assert.Equal(Read.Left, SubtitlePlacement.Place(Style(SubtitlePosition.AboveRegion, align: TextAlign.Left), Read, Box, Screen, 6).Left);
        Assert.Equal(Read.Right, SubtitlePlacement.Place(Style(SubtitlePosition.AboveRegion, align: TextAlign.Right), Read, Box, Screen, 6).Right);
    }

    [Fact]
    public void Shared_on_screen_moves_lines_that_would_cover_the_read_region_above_it()
    {
        var over = SubtitlePlacement.Place(Style(SubtitlePosition.OverRegion, avoid: true), Read, Box, Screen, 6);
        var drawnOver = SubtitlePlacement.Place(Style(SubtitlePosition.Custom, avoid: true, custom: new DRect(400, 900, 1000, 100)), Read, Box, Screen, 6);
        var bottom = SubtitlePlacement.Place(Style(SubtitlePosition.BottomCenter, avoid: true), Read, Box, Screen, 6);

        Assert.False(over.IntersectsWith(Read));
        Assert.Equal(Read.Top - 6, over.Bottom);
        Assert.False(drawnOver.IntersectsWith(Read));
        Assert.False(bottom.IntersectsWith(Read)); // the bottom centre lands on a region at the bottom: it gives way too
        Assert.True(SubtitlePlacement.Covers(SubtitlePosition.OverRegion, null, Read));
        Assert.True(SubtitlePlacement.Covers(SubtitlePosition.Custom, new DRect(400, 900, 1000, 100), Read));
        Assert.False(SubtitlePlacement.Covers(SubtitlePosition.Custom, new DRect(100, 80, 800, 140), Read));
        Assert.False(SubtitlePlacement.Covers(SubtitlePosition.AboveRegion, null, Read));
    }

    [Fact]
    public void The_style_avoids_the_region_only_when_shared_subtitles_are_on()
    {
        var look = new OverlaySettings { DisplayMode = DisplayMode.Shared };
        Assert.True(SubtitleStyle.From(look).AvoidRegion);
        look.Share.Subtitles = false;
        Assert.False(SubtitleStyle.From(look).AvoidRegion);
        Assert.False(SubtitleStyle.From(new OverlaySettings()).AvoidRegion);
    }

    // ---- the box behind the text ----

    [Fact]
    public void A_background_colour_is_a_six_digit_code_or_transparent()
    {
        Assert.Equal("#1E1E1E", BackgroundColors.Normalize("1e1e1e"));
        Assert.Equal("#FFAA00", BackgroundColors.Normalize("#fa0"));
        Assert.Equal("", BackgroundColors.Normalize("#12345"));
        Assert.Equal("", BackgroundColors.Normalize("red"));
        Assert.Equal("", BackgroundColors.Normalize(null));
        Assert.Equal(((byte)0x26, (byte)0x32, (byte)0x3D), BackgroundColors.Rgb("#26323D"));
        Assert.Equal("Transparent", BackgroundColors.Name(""));
        Assert.Equal("Navy", BackgroundColors.Name("#0b1c2c"));
        Assert.Equal("#123456", BackgroundColors.Name("#123456"));
    }

    [Fact]
    public void New_settings_draw_the_text_alone_with_an_outline_and_a_hand_made_style_keeps_the_dark_box()
    {
        var look = new OverlaySettings();
        var style = SubtitleStyle.From(look);

        Assert.Equal("", style.BackgroundColor);
        Assert.True(style.TextOutline);
        Assert.Equal(DisplayMode.Private, look.DisplayMode);
        Assert.Equal(ClearMode.TextOnly, look.Clear);
        Assert.Equal(SubtitleStyle.DarkBox, new SubtitleStyle(1, 0.8, SubtitlePosition.BelowRegion, false).BackgroundColor);
    }

    [Fact]
    public void The_new_settings_round_trip_and_an_older_file_gets_their_defaults()
    {
        var settings = new AppSettings();
        settings.Overlay.BackgroundColor = "#26323D";
        settings.Overlay.DisplayMode = DisplayMode.Shared;
        settings.Overlay.Share.Notices = true;
        settings.Overlay.Position = SubtitlePosition.Custom;
        settings.Overlay.DisplayRegion = new RegionSettings(10, 20, 800, 120);
        settings.Overlay.Stay = StayMode.ByLength;
        settings.Overlay.Clear = ClearMode.StopSubtitles;
        settings.General.HideMainWindowFromCapture = false;

        var copy = SettingsStore.Clone(settings);
        Assert.Equal("#26323D", copy.Overlay.BackgroundColor);
        Assert.Equal(DisplayMode.Shared, copy.Overlay.DisplayMode);
        Assert.True(copy.Overlay.Share.Notices);
        Assert.Equal(new RegionSettings(10, 20, 800, 120), copy.Overlay.DisplayRegion);
        Assert.Equal((StayMode.ByLength, ClearMode.StopSubtitles), (copy.Overlay.Stay, copy.Overlay.Clear));
        Assert.False(copy.General.HideMainWindowFromCapture);

        var older = SettingsStore.FromNode(System.Text.Json.Nodes.JsonNode.Parse("""{"Overlay":{"FontSize":30,"Position":"Sideways","Stay":"Forever"}}""")!);
        Assert.Equal(30, older.Overlay.FontSize);
        Assert.Equal(SubtitlePosition.AboveRegion, older.Overlay.Position); // a name this version does not know reads as the default
        Assert.Equal(StayMode.Fixed, older.Overlay.Stay);
        Assert.Equal("", older.Overlay.BackgroundColor);
        Assert.True(older.General.HideMainWindowFromCapture);
    }

    [Fact]
    public void Each_overlay_window_is_shared_only_in_shared_mode_and_only_when_its_switch_is_on()
    {
        var look = new OverlaySettings();
        Assert.Equal(SharedWindows.None, SharedWindows.For(look));
        look.DisplayMode = DisplayMode.Shared;
        Assert.Equal(new SharedWindows(true, true, true, false), SharedWindows.For(look));
    }

    // ---- the overlay windows and the presenter ----

    static void OnStaThread(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    static void Flush() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    [Fact]
    public void Shared_windows_are_seen_by_screen_capture_and_the_rest_stay_hidden_from_it() => OnStaThread(() =>
    {
        var overlay = new OverlayHost();
        try
        {
            var screen = Forms.Screen.PrimaryScreen!.Bounds;
            var region = new DRect(screen.Left + 400, screen.Top + 500, 800, 100);
            overlay.ShowSubtitleLines([new SubtitleLineView("Xin chào.", false)], region, 22);
            overlay.ShowLiveBadge(region, "Subtitles on");
            Assert.True(overlay.ExcludedFromCapture);

            overlay.Shared = new SharedWindows(Subtitles: true, History: false, Labels: false, Notices: false);
            var windows = overlay.Windows.ToDictionary(w => w.Name, w => w.Window);
            Assert.False(windows["subtitles"].ExcludedFromCapture);
            Assert.True(windows["badge"].ExcludedFromCapture);
            Assert.False(overlay.ExcludedFromCapture); // translate-screen then hides the overlay before it captures

            overlay.ShowHistory("History", [], region); // made after the switch: it follows it too
            Assert.True(overlay.Windows.Single(w => w.Name == "history").Window.ExcludedFromCapture);

            overlay.Shared = SharedWindows.None;
            Assert.True(overlay.ExcludedFromCapture);
        }
        finally
        {
            overlay.Close();
        }
    });

    [Fact]
    public void Clearing_the_text_keeps_the_lines_in_the_history_unless_asked_and_the_next_line_shows_as_usual() => OnStaThread(() =>
    {
        var overlay = new OverlayHost();
        var look = new OverlaySettings();
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        var presenter = new SubtitlePresenter(Dispatcher.CurrentDispatcher, overlay, () => new DRect(screen.Left + 400, screen.Top + 400, 800, 100),
            () => look, () => "Ctrl+Shift+H", _ => { }, _ => { }, _ => { });
        try
        {
            presenter.Start();
            presenter.LineDetected(1, "Hello.", false);
            presenter.LineTranslated(1, "Xin chào.", false, "test");
            Flush();
            var box = overlay.Windows.Single(w => w.Name == "subtitles").Window;
            Assert.True(box.Showing);

            presenter.ClearText(history: false);
            Assert.False(box.Showing);
            Assert.Equal(1, presenter.Count);

            presenter.LineDetected(2, "Bye.", false);
            presenter.LineTranslated(2, "Tạm biệt.", false, "test");
            Flush();
            Assert.True(box.Showing);
            Assert.Equal(new[] { "Tạm biệt." }, OverlayHostTests.SubtitleTextBlocks(box).Where(b => b.Visibility == System.Windows.Visibility.Visible).Select(b => b.Text));

            presenter.ClearText(history: true);
            Assert.Equal(0, presenter.Count);
        }
        finally
        {
            overlay.Close();
        }
    });

    // ---- the pages ----

    static SettingsViewModel Page(TempDir dir, Action<AppSettings>? setup = null)
    {
        var host = new FakeHost(dir.File("Profiles"));
        setup?.Invoke(host.Current);
        return new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() };
    }

    [Fact]
    public void Without_a_read_region_the_number_boxes_change_nothing()
    {
        using var dir = new TempDir();
        var vm = Page(dir);

        vm.Regions.ReadWidth = 20; // what a box with a minimum would push back
        vm.Regions.ReadX = 0;

        Assert.Null(vm.Working.Subtitles.Region);
        Assert.False(vm.HasChanges);
        Assert.False(vm.Regions.PickDisplayCommand.CanExecute(null));
    }

    [Fact]
    public void The_regions_page_edits_the_place_the_box_and_says_what_covering_the_region_means()
    {
        using var dir = new TempDir();
        var vm = Page(dir, s => s.Subtitles.Region = new RegionSettings(384, 888, 1152, 132));
        var page = vm.Regions;

        Assert.Equal(MessageKind.Success, page.OverlapKind);
        page.PlaceOver = true;
        Assert.Equal(SubtitlePosition.OverRegion, vm.Working.Overlay.Position);
        Assert.Equal(MessageKind.Info, page.OverlapKind); // fine with Private overlay
        vm.Working.Overlay.DisplayMode = DisplayMode.Shared;
        Assert.Equal(MessageKind.Warning, page.OverlapKind);

        page.PlaceCustom = true; // a custom box starts where the translation was
        Assert.NotNull(vm.Working.Overlay.DisplayRegion);
        page.CustomWidth = 700;
        Assert.Equal(700, vm.Working.Overlay.DisplayRegion!.Width);

        page.Swatches.Single(s => s.Hex == "#0B1C2C").IsSelected = true;
        Assert.Equal("#0B1C2C", vm.Working.Overlay.BackgroundColor);
        Assert.True(page.HasBackground);
        page.CustomColorText = "#12"; // not a colour yet: nothing changes
        Assert.Equal("#0B1C2C", vm.Working.Overlay.BackgroundColor);
        page.CustomColorText = "#123456";
        Assert.Equal("#123456", vm.Working.Overlay.BackgroundColor);
        page.BackgroundOpacity = 82; // what the slider shows already: no edit
        page.BackgroundOpacity = 60;
        Assert.Equal(0.6, vm.Working.Overlay.BackgroundOpacity);

        Assert.True(vm.HasChanges);
        Assert.Equal(SettingsChange.Overlay, vm.PendingChanges);
    }

    [Fact]
    public void The_display_page_switches_the_mode_and_the_main_window_capture()
    {
        using var dir = new TempDir();
        var vm = Page(dir);

        Assert.True(vm.Display.IsPrivate);
        vm.Display.IsShared = true;
        vm.Display.ShareNotices = true;
        vm.Display.HideMainWindow = false;

        Assert.Equal(DisplayMode.Shared, vm.Working.Overlay.DisplayMode);
        Assert.True(vm.Working.Overlay.Share.Notices);
        Assert.False(vm.Working.General.HideMainWindowFromCapture);
        Assert.Equal("Ctrl+Shift+P", vm.Display.ModeHotkey);
        Assert.True(vm.Save());
    }

    [Fact]
    public void The_timing_page_draws_its_timeline_from_the_settings_and_resets_only_the_timing()
    {
        using var dir = new TempDir();
        var vm = Page(dir);
        var page = vm.Timing;

        Assert.Equal("Stays 6.0 s", page.StayLabel);
        page.StaySeconds = 2.26; // half seconds
        Assert.Equal(2.5, vm.Working.Overlay.StaySeconds);
        page.IsByLength = true;
        page.ReadingSpeed = 20;
        Assert.Equal("Stays ≈ 4.0 s", page.StayLabel);
        page.IsUntilNext = true;
        Assert.Equal("Stays until the next line", page.StayLabel);
        page.FadeOutMs = 333;
        Assert.Equal(350, vm.Working.Overlay.FadeOutMs);
        page.IsClearStop = true;
        vm.Working.Overlay.FontSize = 30; // not timing: the reset keeps it

        page.ResetCommand.Execute(null);

        var d = new OverlaySettings();
        var o = vm.Working.Overlay;
        Assert.Equal((d.Stay, d.StaySeconds, d.ReadingSpeed, d.FadeOutMs, d.Clear), (o.Stay, o.StaySeconds, o.ReadingSpeed, o.FadeOutMs, o.Clear));
        Assert.Equal(30, o.FontSize);
    }

    [Fact]
    public void Home_sums_up_the_regions_the_mode_and_the_timing_and_opens_their_pages()
    {
        using var dir = new TempDir();
        var vm = Page(dir, s => s.Subtitles.Region = new RegionSettings(384, 888, 1152, 132));
        string? opened = null;
        vm.NavigateRequested += (_, page) => opened = page;

        Assert.Equal("Reads 1152 × 132", vm.Home.ReadChip);
        Assert.Equal("Shows above the read region", vm.Home.PlaceChip);
        Assert.Equal("Private overlay", vm.Home.ModeName);
        Assert.Equal("Stays 6 s", vm.Home.TimingChip);
        Assert.Equal("Keeps subtitles on", vm.Home.ClearValue);
        Assert.NotNull(vm.Home.Preview.Read);
        Assert.NotNull(vm.Home.Preview.Show);
        Assert.True(vm.Home.Preview.Show!.Top + vm.Home.Preview.Show.Height <= vm.Home.Preview.Read!.Top + 0.01);

        vm.Home.OpenCommand.Execute(SettingsViewModel.TimingPage);
        Assert.Equal(SettingsViewModel.TimingPage, opened);
    }

    [Fact]
    public void The_mode_hotkey_is_in_the_tray_and_on_by_default()
    {
        Assert.Equal("Ctrl+Shift+P", new HotkeySettings()[HotkeyAction.SwitchDisplayMode]);
        var items = TrayMenu.Build(new TrayState("Free", false, false, false, new HotkeySettings(), [], "free") { Shared = true });
        var item = items.Single(i => i.Command == TrayCommand.DisplayMode);
        Assert.True(item.Checked);
        Assert.Equal("Ctrl+Shift+P", item.Gesture);
    }
}
