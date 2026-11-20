using System.Linq;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Overlay;
using SLiveTranslator.UI.Pages;
using Xunit;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.Tests;

/// <summary>Private overlay: the boxes may overlap. Shared on screen: the translation keeps off the read region.</summary>
public class RegionRulesTests
{
    static readonly Edges Read = new(400, 800, 1400, 900);
    static readonly Edges Screen = new(0, 0, 1920, 1080);

    static Edges Box(double left, double top, double width, double height) => new(left, top, left + width, top + height);

    [Theory]
    [InlineData(DisplayMode.Private, true, false)]
    [InlineData(DisplayMode.Shared, true, true)]
    [InlineData(DisplayMode.Shared, false, false)] // the subtitle window is not shared: its text is not captured
    public void The_rule_holds_in_shared_mode_with_the_subtitles_shared(DisplayMode mode, bool subtitles, bool avoid) =>
        Assert.Equal(avoid, RegionRules.MustAvoid(new OverlaySettings { DisplayMode = mode, Share = { Subtitles = subtitles } }));

    [Fact]
    public void Touching_boxes_do_not_overlap()
    {
        Assert.False(Box(400, 700, 1000, 100).Overlaps(Read)); // its bottom is the read region's top
        Assert.False(Box(1400, 800, 100, 100).Overlaps(Read));
        Assert.True(Box(400, 701, 1000, 100).Overlaps(Read));
    }

    [Fact]
    public void A_box_moved_down_onto_the_region_slides_along_its_top_edge()
    {
        var from = Box(500, 650, 600, 100); // above
        var next = Box(560, 760, 600, 100); // dragged down and right, onto the region

        var kept = RegionRules.ClampMove(next, from, Read, Screen);

        Assert.Equal(Box(560, 700, 600, 100), kept); // x as dragged, bottom on the region's top
        Assert.False(kept.Overlaps(Read));
    }

    [Fact]
    public void A_box_moved_from_the_left_stops_at_the_left_edge_and_one_dragged_past_lands_on_the_other_side()
    {
        var from = Box(100, 820, 200, 60);

        Assert.Equal(Box(200, 830, 200, 60), RegionRules.ClampMove(Box(450, 830, 200, 60), from, Read, Screen));
        Assert.Equal(Box(1500, 830, 200, 60), RegionRules.ClampMove(Box(1500, 830, 200, 60), from, Read, Screen)); // fully past: free
    }

    [Fact]
    public void From_a_corner_the_smaller_shift_wins()
    {
        var from = Box(200, 650, 150, 100); // above and to the left
        Assert.Equal(Box(250, 720, 150, 100), RegionRules.ClampMove(Box(260, 720, 150, 100), from, Read, Screen)); // 10 px in sideways, 20 down
        Assert.Equal(Box(330, 700, 150, 100), RegionRules.ClampMove(Box(330, 705, 150, 100), from, Read, Screen)); // 80 px in sideways, 5 down
    }

    [Fact]
    public void With_no_room_on_its_side_a_moved_box_stays_where_it_was()
    {
        var read = new Edges(400, 40, 1400, 140);
        var from = Box(500, 0, 600, 40); // squeezed above
        var next = Box(500, 20, 600, 60); // taller than the room above: cannot slide back up inside the screen

        Assert.Equal(from, RegionRules.ClampMove(next, from, read, Screen));
    }

    [Fact]
    public void Only_the_edge_facing_the_region_is_stopped_when_resizing()
    {
        var from = Box(500, 600, 600, 150); // above, bottom at 750

        Assert.Equal(Box(500, 600, 600, 200), RegionRules.ClampResize(Box(500, 600, 600, 260), from, Read, Sides.Bottom));
        // Moving the left edge cannot cross from above: the box keeps its last shape.
        Assert.Equal(from, RegionRules.ClampResize(Box(300, 600, 800, 260), from, Read, Sides.Left));
        Assert.Equal(Box(300, 600, 800, 150), RegionRules.ClampResize(Box(300, 600, 800, 150), from, Read, Sides.Left)); // no overlap: as dragged
    }

    [Fact]
    public void A_new_box_drawn_from_beside_the_region_stops_at_its_edge()
    {
        var anchor = new Edges(300, 820, 300, 820); // left of the region
        var drawn = new Edges(300, 820, 700, 880); // dragged right, into it

        Assert.Equal(new Edges(300, 820, 400, 880), RegionRules.ClampResize(drawn, anchor, Read, Sides.Right | Sides.Bottom));
    }

    [Fact]
    public void A_box_on_the_region_is_pushed_out_the_shortest_way_and_ties_go_up()
    {
        Assert.Equal(Box(500, 750, 600, 50), RegionRules.PushOut(Box(500, 760, 600, 50), Read, Screen)); // 10 up beats 140 down
        Assert.Equal(Box(500, 900, 600, 50), RegionRules.PushOut(Box(500, 880, 600, 50), Read, Screen)); // 20 down
        Assert.Equal(Box(500, 750, 600, 50), RegionRules.PushOut(Box(500, 825, 600, 50), Read, Screen)); // 75 either way: up
        Assert.Equal(Box(500, 300, 100, 50), RegionRules.PushOut(Box(500, 300, 100, 50), Read, Screen)); // not on it: kept
    }

    [Fact]
    public void Above_or_below_keeps_the_size_and_squeezes_only_when_neither_fits()
    {
        var box = Box(500, 820, 600, 120);
        Assert.Equal((Box(500, 674, 600, 120), Side.Above), RegionRules.AboveOrBelow(box, Read, Screen, 6));

        var top = new Edges(400, 10, 1400, 110);
        Assert.Equal((Box(500, 116, 600, 120), Side.Below), RegionRules.AboveOrBelow(box, top, Screen, 6));

        var tall = new Edges(0, 40, 1920, 1020); // 40 px above, 60 below
        Assert.Equal((new Edges(500, 1026, 1100, 1080), Side.Below), RegionRules.AboveOrBelow(box, tall, Screen, 6));
        Assert.Null(RegionRules.AboveOrBelow(box, new Edges(0, 10, 1920, 1070), Screen, 6));
    }

    static OverlaySettings Shared(SubtitlePosition position, RegionSettings? custom = null) => new()
    {
        DisplayMode = DisplayMode.Shared,
        Position = position,
        DisplayRegion = custom,
    };

    static readonly RegionSettings ReadRegion = new(400, 800, 1000, 100);
    static readonly DRect Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void Enforce_moves_over_the_original_above_or_below_and_leaves_private_mode_alone()
    {
        var shared = Shared(SubtitlePosition.OverRegion);
        Assert.Equal(PlacementFix.MovedAbove, RegionRules.Enforce(shared, ReadRegion, Monitor));
        Assert.Equal(SubtitlePosition.AboveRegion, shared.Position);

        var atTop = Shared(SubtitlePosition.OverRegion);
        Assert.Equal(PlacementFix.MovedBelow, RegionRules.Enforce(atTop, new RegionSettings(400, 10, 1000, 60), Monitor));
        Assert.Equal(SubtitlePosition.BelowRegion, atTop.Position);

        var privately = new OverlaySettings { Position = SubtitlePosition.OverRegion };
        Assert.Equal(PlacementFix.None, RegionRules.Enforce(privately, ReadRegion, Monitor));
        Assert.Equal(SubtitlePosition.OverRegion, privately.Position);

        var unshared = Shared(SubtitlePosition.OverRegion);
        unshared.Share.Subtitles = false;
        Assert.Equal(PlacementFix.None, RegionRules.Enforce(unshared, ReadRegion, Monitor));
        Assert.Equal(PlacementFix.None, RegionRules.Enforce(Shared(SubtitlePosition.OverRegion), null, Monitor));
    }

    [Fact]
    public void Enforce_moves_a_custom_box_on_the_region_with_its_size_kept()
    {
        var o = Shared(SubtitlePosition.Custom, new RegionSettings(500, 820, 600, 120));

        Assert.Equal(PlacementFix.MovedAbove, RegionRules.Enforce(o, ReadRegion, Monitor));

        Assert.Equal(new RegionSettings(500, 674, 600, 120), o.DisplayRegion);
        Assert.Equal(SubtitlePosition.Custom, o.Position);
        Assert.Equal(PlacementFix.None, RegionRules.Enforce(o, ReadRegion, Monitor)); // done once
        var none = Shared(SubtitlePosition.Custom);
        Assert.Equal(PlacementFix.MovedAbove, RegionRules.Enforce(none, ReadRegion, Monitor)); // a custom place with no box is over the region
        Assert.Equal(PlacementFix.None, RegionRules.Enforce(Shared(SubtitlePosition.BottomCenter), ReadRegion, Monitor)); // the overlay gives way itself
    }

    // ---- the pages ----

    static (FakeHost Host, SettingsViewModel Vm) Pages(TempDir dir, DisplayMode mode)
    {
        var host = new FakeHost(dir.File("Profiles"));
        host.Current.Subtitles.Region = ReadRegion;
        host.Current.Overlay.DisplayMode = mode;
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay);
        return (host, vm);
    }

    [Fact]
    public void The_regions_page_refuses_over_the_original_in_shared_mode_but_not_in_private_mode()
    {
        using var dir = new TempDir();
        var (_, shared) = Pages(dir, DisplayMode.Shared);
        Assert.False(shared.Regions.CanPlaceOver);
        shared.Regions.PlaceOver = true;
        Assert.Equal(SubtitlePosition.AboveRegion, shared.Working.Overlay.Position);

        using var dir2 = new TempDir();
        var (_, privately) = Pages(dir2, DisplayMode.Private);
        Assert.True(privately.Regions.CanPlaceOver);
        privately.Regions.PlaceOver = true;
        Assert.Equal(SubtitlePosition.OverRegion, privately.Working.Overlay.Position);
    }

    [Fact]
    public void Typed_custom_coordinates_stop_at_the_read_region_in_shared_mode()
    {
        using var dir = new TempDir();
        var (_, vm) = Pages(dir, DisplayMode.Shared);
        var regions = vm.Regions;
        regions.PlaceCustom = true; // seeded above the region
        var seeded = vm.Working.Overlay.DisplayRegion!;
        Assert.True(seeded.Y + seeded.Height <= ReadRegion.Y);

        regions.CustomY = 850; // onto the region
        Assert.Equal(ReadRegion.Y, vm.Working.Overlay.DisplayRegion!.Y + vm.Working.Overlay.DisplayRegion.Height);
        Assert.Equal(Loc.Instance["Regions.KeptOff"], regions.KeptOffText);

        regions.CustomHeight = 500; // the bottom edge cannot go past the region's top
        Assert.Equal(ReadRegion.Y, vm.Working.Overlay.DisplayRegion!.Y + vm.Working.Overlay.DisplayRegion.Height);

        regions.CustomX = 20; // a move that stays clear is as typed
        Assert.Equal(20, vm.Working.Overlay.DisplayRegion!.X);
        Assert.Equal("", regions.KeptOffText);
    }

    [Fact]
    public void A_read_region_typed_onto_the_custom_box_moves_the_box_not_the_region()
    {
        using var dir = new TempDir();
        var (_, vm) = Pages(dir, DisplayMode.Shared);
        vm.Working.Overlay.Position = SubtitlePosition.Custom;
        vm.Working.Overlay.DisplayRegion = new RegionSettings(500, 500, 600, 100);

        vm.Regions.ReadY = 520;

        Assert.Equal(520, vm.Working.Subtitles.Region!.Y);
        var box = vm.Working.Overlay.DisplayRegion!;
        Assert.False(Edges.Of(box.ToRectangle()).Overlaps(Edges.Of(vm.Working.Subtitles.Region.ToRectangle())));
        Assert.Equal((600, 100), (box.Width, box.Height));
        Assert.NotEqual("", vm.Regions.KeptOffText);
    }

    [Fact]
    public void Private_mode_lets_the_boxes_overlap()
    {
        using var dir = new TempDir();
        var (_, vm) = Pages(dir, DisplayMode.Private);
        vm.Regions.PlaceCustom = true;

        vm.Regions.CustomY = 850;

        Assert.Equal(850, vm.Working.Overlay.DisplayRegion!.Y);
        Assert.Equal("", vm.Regions.KeptOffText);
    }

    [Fact]
    public void Switching_to_shared_on_the_display_page_moves_a_translation_on_the_region_and_says_so()
    {
        using var dir = new TempDir();
        var (_, vm) = Pages(dir, DisplayMode.Private);
        vm.Working.Overlay.Position = SubtitlePosition.OverRegion;

        vm.Display.IsShared = true;

        Assert.Equal(SubtitlePosition.AboveRegion, vm.Working.Overlay.Position);
        Assert.Equal(Loc.Instance["Notice.ModeSharedMoved"], vm.Display.MovedNote);
        Assert.True(vm.HasChanges);
    }

    [Fact]
    public void The_pickers_on_the_pages_start_from_the_unsaved_settings()
    {
        using var dir = new TempDir();
        var (host, vm) = Pages(dir, DisplayMode.Private);
        vm.Display.IsShared = true; // not saved yet

        vm.Regions.PickBothCommand.Execute(null);

        Assert.Equal(DisplayMode.Shared, Assert.Single(host.PickedWith).Overlay.DisplayMode);
    }

    [Fact]
    public void The_appearance_page_keeps_over_the_original_out_of_shared_mode()
    {
        using var dir = new TempDir();
        var (_, vm) = Pages(dir, DisplayMode.Shared);

        vm.Overlay.SelectedPosition = vm.Overlay.Positions.Single(p => p.Value == SubtitlePosition.OverRegion);

        Assert.Equal(SubtitlePosition.AboveRegion, vm.Working.Overlay.Position);
    }
}
