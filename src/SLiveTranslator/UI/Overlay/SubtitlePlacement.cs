using System;
using SLiveTranslator.Models;
using DRect = System.Drawing.Rectangle;
using DSize = System.Drawing.Size;

namespace SLiveTranslator.UI.Overlay;

/// <summary>Where the subtitle box goes on the monitor (physical pixels). Plain arithmetic, so the rules are testable.</summary>
public static class SubtitlePlacement
{
    /// <summary>Space between the bottom of the monitor and the box at <see cref="SubtitlePosition.BottomCenter"/>, as a share of the monitor's height.</summary>
    public const double BottomMargin = 0.06;

    /// <summary>
    /// The box of <paramref name="size"/> for <paramref name="style"/>: below the region (above it when there is no room
    /// below), above it (below when there is no room), at the bottom centre of the monitor, centred over the region, or
    /// in the custom box. Text alignment moves it to that side of the region or box. With
    /// <see cref="SubtitleStyle.AvoidRegion"/>, a place that covers the region gives way to above it, then below it.
    /// </summary>
    public static DRect Place(SubtitleStyle style, DRect regionPx, DSize size, DRect screen, int gap)
    {
        var at = PlaceAt(style.Position, style, regionPx, size, screen, gap);
        if (!style.AvoidRegion || !at.IntersectsWith(regionPx)) return at;
        foreach (var fallback in new[] { SubtitlePosition.AboveRegion, SubtitlePosition.BelowRegion })
        {
            var other = PlaceAt(fallback, style, regionPx, size, screen, gap);
            if (!other.IntersectsWith(regionPx)) return other;
        }
        return at;
    }

    /// <summary>The width the box may take: the region's (at least 480 DIPs), or the custom box's.</summary>
    public static int MaxWidthPx(SubtitleStyle style, DRect regionPx, DRect screen, double scale)
    {
        int width = style.Position == SubtitlePosition.Custom && style.DisplayRegion is { } box
            ? box.Width
            : Math.Max(regionPx.Width, (int)(480 * scale));
        return Math.Max(1, Math.Min(width, screen.Width - (int)(16 * scale)));
    }

    /// <summary>Whether lines placed this way would cover the read region (before any fallback).</summary>
    public static bool Covers(SubtitlePosition position, DRect? custom, DRect regionPx) => position switch
    {
        SubtitlePosition.OverRegion => true,
        SubtitlePosition.Custom => (custom ?? regionPx).IntersectsWith(regionPx),
        _ => false,
    };

    static DRect PlaceAt(SubtitlePosition position, SubtitleStyle style, DRect regionPx, DSize size, DRect screen, int gap)
    {
        var anchor = position == SubtitlePosition.Custom && style.DisplayRegion is { } custom ? custom : regionPx;
        int x = style.Align switch
        {
            TextAlign.Left => anchor.Left,
            TextAlign.Right => anchor.Right - size.Width,
            _ => anchor.Left + (anchor.Width - size.Width) / 2,
        };
        int below = regionPx.Bottom + gap;
        int above = regionPx.Top - size.Height - gap;
        int lowest = Math.Max(screen.Top, screen.Bottom - size.Height);
        int y = position switch
        {
            SubtitlePosition.AboveRegion => above >= screen.Top ? above : Math.Min(below, lowest),
            SubtitlePosition.BottomCenter => Math.Max(screen.Top, screen.Bottom - size.Height - (int)(screen.Height * BottomMargin)),
            SubtitlePosition.OverRegion or SubtitlePosition.Custom => anchor.Top + (anchor.Height - size.Height) / 2,
            _ => below + size.Height <= screen.Bottom ? below : Math.Max(screen.Top, above),
        };
        if (position == SubtitlePosition.BottomCenter) x = screen.Left + Math.Max(0, (screen.Width - size.Width) / 2);
        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - size.Width));
        y = Math.Clamp(y, screen.Top, lowest);
        return new DRect(x, y, size.Width, size.Height);
    }
}
