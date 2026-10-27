using SLiveTranslator.Models;
using SLiveTranslator.UI.Overlay;
using Xunit;
using DPoint = System.Drawing.Point;
using DRect = System.Drawing.Rectangle;
using DSize = System.Drawing.Size;

namespace SLiveTranslator.Tests;

/// <summary>Where the toast goes for each notification position, in physical pixels; the window itself is not needed.</summary>
public class ToastPlacementTests
{
    static readonly DRect Screen = new(1920, 0, 2560, 1440); // the second monitor
    static readonly DSize Size = new(600, 60);

    [Theory]
    [InlineData(1.0, 1920 + 980, 28)]
    [InlineData(1.5, 1920 + 980, 42)]
    public void Top_centre_is_centred_below_the_top_edge(double scale, int x, int y) =>
        Assert.Equal(new DPoint(x, y), ToastOverlay.Place(NoticePosition.TopCenter, Screen, null, Size, scale));

    [Fact]
    public void Top_right_keeps_the_same_margin_from_the_right_edge() =>
        Assert.Equal(new DPoint(1920 + 2560 - 42 - 600, 42), ToastOverlay.Place(NoticePosition.TopRight, Screen, null, Size, 1.5));

    [Fact]
    public void Near_the_region_is_centred_above_it_clear_of_the_badge()
    {
        var region = new DRect(2300, 1200, 1400, 120);

        var at = ToastOverlay.Place(NoticePosition.NearRegion, Screen, region, Size, 1.25);

        Assert.Equal(new DPoint(2300 + 400, 1200 - 40 - 60), at);
    }

    [Fact]
    public void Near_a_region_at_the_top_of_the_monitor_goes_below_it_and_stays_on_the_monitor()
    {
        var region = new DRect(4300, 10, 400, 80); // at the right edge too

        var at = ToastOverlay.Place(NoticePosition.NearRegion, Screen, region, Size, 1.0);

        Assert.Equal(new DPoint(1920 + 2560 - 600, 10 + 80 + 32), at);
    }

    [Fact]
    public void Near_a_region_at_the_bottom_goes_above_the_translated_lines_drawn_above_it()
    {
        var region = new DRect(1920 + 327, 1094, 1825, 212); // the user's Witcher 3 strip: no room for three lines below it
        var lines = new DRect(1920 + 900, 1094 - 8 - 150, 680, 150);

        var at = ToastOverlay.Place(NoticePosition.NearRegion, Screen, region, Size, 1.25, lines);

        Assert.Equal(new DPoint(1920 + 327 + (1825 - 600) / 2, lines.Top - 40 - 60), at);
    }

    [Fact]
    public void Lines_below_the_region_leave_the_toast_above_the_region()
    {
        var region = new DRect(2300, 900, 1400, 120);
        var lines = new DRect(2600, 1026, 800, 90);

        Assert.Equal(ToastOverlay.Place(NoticePosition.NearRegion, Screen, region, Size, 1.0),
            ToastOverlay.Place(NoticePosition.NearRegion, Screen, region, Size, 1.0, lines));
    }

    [Fact]
    public void Without_room_above_the_lines_the_toast_goes_below_them()
    {
        var region = new DRect(2300, 10, 1400, 80);
        var lines = new DRect(2600, 96, 800, 90);

        var at = ToastOverlay.Place(NoticePosition.NearRegion, Screen, region, Size, 1.0, lines);

        Assert.Equal(new DPoint(2300 + 400, 96 + 90 + 32), at);
    }

    [Fact]
    public void Near_the_region_without_one_falls_back_to_the_top_centre() =>
        Assert.Equal(ToastOverlay.Place(NoticePosition.TopCenter, Screen, null, Size, 1.0),
            ToastOverlay.Place(NoticePosition.NearRegion, Screen, null, Size, 1.0));
}
