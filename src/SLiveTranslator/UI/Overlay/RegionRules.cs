using System;
using System.Collections.Generic;
using SLiveTranslator.Models;
using DRect = System.Drawing.Rectangle;
using WRect = System.Windows.Rect;

namespace SLiveTranslator.UI.Overlay;

/// <summary>
/// A box by its edges, in DIPs (the picker) or pixels (the settings). Its own type: WPF's Rect counts touching boxes
/// as intersecting and refuses negative sizes, and here "stops at the edge" means the boxes touch.
/// </summary>
public readonly record struct Edges(double Left, double Top, double Right, double Bottom)
{
    const double Tolerance = 1e-6;

    public double Width => Right - Left;
    public double Height => Bottom - Top;

    public static Edges Of(WRect r) => new(r.Left, r.Top, r.Right, r.Bottom);
    public static Edges Of(DRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public WRect ToRect() => new(Left, Top, Math.Max(0, Width), Math.Max(0, Height));

    public DRect ToDRect() => DRect.FromLTRB((int)Math.Round(Left), (int)Math.Round(Top), (int)Math.Round(Right), (int)Math.Round(Bottom));

    public Edges Offset(double dx, double dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    /// <summary>True only when the boxes share some area: boxes that touch do not overlap.</summary>
    public bool Overlaps(Edges other) =>
        Left < other.Right - Tolerance && other.Left < Right - Tolerance && Top < other.Bottom - Tolerance && other.Top < Bottom - Tolerance;

    public bool Inside(Edges area) =>
        Left >= area.Left - Tolerance && Top >= area.Top - Tolerance && Right <= area.Right + Tolerance && Bottom <= area.Bottom + Tolerance;

    public bool Contains(double x, double y) => x > Left && x < Right && y > Top && y < Bottom;
}

/// <summary>Which edges of a box a drag moves.</summary>
[Flags]
public enum Sides { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>Where a box lies from the read region.</summary>
public enum Side { Above, Below, LeftOf, RightOf }

/// <summary>What <see cref="RegionRules.Enforce"/> changed.</summary>
public enum PlacementFix { None, MovedAbove, MovedBelow }

/// <summary>
/// The rule between the read region and the box of the translation. In Private overlay they may overlap; in Shared on
/// screen (with the subtitles shared) they must not, or the app's own OCR reads the translation. Drawing, moving and
/// typing keep the box off the read region (it stops at the edge, on the side it came from), and settings that would
/// cover it are moved above it (below when there is no room).
/// </summary>
public static class RegionRules
{
    /// <summary>The space between the read region and a box moved off it (px), as the overlay leaves.</summary>
    public const int Gap = 6;

    /// <summary>A box squeezed into the space beside the region is never lower than this.</summary>
    const double MinHeight = 20;

    /// <summary>Whether the translation must keep off the read region: Shared on screen, with the subtitles shared.</summary>
    public static bool MustAvoid(OverlaySettings o) => o.DisplayMode == DisplayMode.Shared && o.Share.Subtitles;

    /// <summary>The sides of <paramref name="read"/> the box lies on (two at a corner, none when they overlap).</summary>
    public static IReadOnlyList<Side> SidesOf(Edges box, Edges read)
    {
        var sides = new List<Side>(2);
        if (box.Bottom <= read.Top + 1e-6) sides.Add(Side.Above);
        if (box.Top >= read.Bottom - 1e-6) sides.Add(Side.Below);
        if (box.Right <= read.Left + 1e-6) sides.Add(Side.LeftOf);
        if (box.Left >= read.Right - 1e-6) sides.Add(Side.RightOf);
        return sides;
    }

    /// <summary>
    /// A box moved (its size kept) to <paramref name="next"/> from <paramref name="from"/>: if it would cover the read
    /// region, it slides along the edge of the side it came from instead. Dragged right past the region it lands on the
    /// other side. When there is no room on that side it stays where it was.
    /// </summary>
    public static Edges ClampMove(Edges next, Edges from, Edges read, Edges area)
    {
        if (!next.Overlaps(read)) return next;
        var sides = SidesOf(from, read);
        if (sides.Count == 0) return PushOut(next, read, area); // it started on the region (an old setting)
        Edges? best = null;
        double bestShift = double.MaxValue;
        foreach (var side in sides)
        {
            var (moved, shift) = side switch
            {
                Side.Above => (next.Offset(0, read.Top - next.Bottom), next.Bottom - read.Top),
                Side.Below => (next.Offset(0, read.Bottom - next.Top), read.Bottom - next.Top),
                Side.LeftOf => (next.Offset(read.Left - next.Right, 0), next.Right - read.Left),
                _ => (next.Offset(read.Right - next.Left, 0), read.Right - next.Left),
            };
            if (!moved.Inside(area) || Math.Abs(shift) >= bestShift) continue;
            best = moved;
            bestShift = Math.Abs(shift);
        }
        return best ?? from;
    }

    /// <summary>
    /// A box resized (or drawn) to <paramref name="next"/> by moving <paramref name="moving"/> edges, from
    /// <paramref name="from"/>: only an edge that faces the read region and moved is stopped at the region's edge. When
    /// none can be stopped, the box stays as it was.
    /// </summary>
    public static Edges ClampResize(Edges next, Edges from, Edges read, Sides moving)
    {
        if (!next.Overlaps(read)) return next;
        Edges? best = null;
        double bestChange = double.MaxValue;
        foreach (var side in SidesOf(from, read))
        {
            var (edge, candidate, change) = side switch
            {
                Side.Above => (Sides.Bottom, next with { Bottom = read.Top }, next.Bottom - read.Top),
                Side.Below => (Sides.Top, next with { Top = read.Bottom }, read.Bottom - next.Top),
                Side.LeftOf => (Sides.Right, next with { Right = read.Left }, next.Right - read.Left),
                _ => (Sides.Left, next with { Left = read.Right }, read.Right - next.Left),
            };
            if (!moving.HasFlag(edge) || candidate.Width <= 0 || candidate.Height <= 0 || candidate.Overlaps(read) || Math.Abs(change) >= bestChange)
                continue;
            best = candidate;
            bestChange = Math.Abs(change);
        }
        return best ?? from;
    }

    /// <summary>
    /// A box on the read region moved the shortest way off it (above, below, left, right in that order on a tie),
    /// staying in <paramref name="area"/>; when no side has room, above or below squeezed (see <see cref="AboveOrBelow"/>).
    /// </summary>
    public static Edges PushOut(Edges box, Edges read, Edges area)
    {
        if (!box.Overlaps(read)) return box;
        var moves = new[]
        {
            box.Offset(0, read.Top - box.Bottom),
            box.Offset(0, read.Bottom - box.Top),
            box.Offset(read.Left - box.Right, 0),
            box.Offset(read.Right - box.Left, 0),
        };
        Edges? best = null;
        double bestShift = double.MaxValue;
        foreach (var moved in moves)
        {
            double shift = Math.Abs(moved.Left - box.Left) + Math.Abs(moved.Top - box.Top);
            if (!moved.Inside(area) || shift >= bestShift) continue;
            best = moved;
            bestShift = shift;
        }
        return best ?? AboveOrBelow(box, read, area, 0)?.Box ?? box;
    }

    /// <summary>
    /// The box (its size kept) just above the read region with <paramref name="gap"/> between, else just below; when
    /// neither has room, squeezed into the larger of the two spaces. Null when even that is too low.
    /// </summary>
    public static (Edges Box, Side Side)? AboveOrBelow(Edges box, Edges read, Edges area, double gap)
    {
        double w = Math.Min(box.Width, area.Width), h = box.Height;
        double left = Math.Clamp(box.Left, area.Left, Math.Max(area.Left, area.Right - w));
        if (read.Top - gap - h >= area.Top) return (new Edges(left, read.Top - gap - h, left + w, read.Top - gap), Side.Above);
        if (read.Bottom + gap + h <= area.Bottom) return (new Edges(left, read.Bottom + gap, left + w, read.Bottom + gap + h), Side.Below);
        double above = read.Top - gap - area.Top, below = area.Bottom - read.Bottom - gap;
        if (Math.Max(above, below) < MinHeight) return null;
        return above >= below
            ? (new Edges(left, area.Top, left + w, read.Top - gap), Side.Above)
            : (new Edges(left, read.Bottom + gap, left + w, area.Bottom), Side.Below);
    }

    /// <summary>
    /// Makes the settings follow the rule (in Shared on screen only): "over the original", or a custom place without a
    /// box, becomes above the region (below with no room above); a custom box on the region moves above or below it,
    /// its size kept. Bottom centre is left as it is: the overlay gives way at run time when it would land on the region.
    /// </summary>
    /// <param name="screen">The monitor of the read region, in pixels.</param>
    public static PlacementFix Enforce(OverlaySettings o, RegionSettings? read, DRect screen)
    {
        if (!MustAvoid(o) || read is null) return PlacementFix.None;
        var r = Edges.Of(read.ToRectangle());
        if (o.Position == SubtitlePosition.OverRegion || (o.Position == SubtitlePosition.Custom && o.DisplayRegion is null))
        {
            double lines = Math.Round(Math.Clamp(o.FontSize, 10, 80) * 1.36 * 2 + 22); // two lines, as the previews draw them
            bool roomAbove = r.Top - Gap - lines >= screen.Top;
            o.Position = roomAbove ? SubtitlePosition.AboveRegion : SubtitlePosition.BelowRegion;
            return roomAbove ? PlacementFix.MovedAbove : PlacementFix.MovedBelow;
        }
        if (o.Position == SubtitlePosition.Custom && o.DisplayRegion is { } box && Edges.Of(box.ToRectangle()).Overlaps(r))
        {
            if (AboveOrBelow(Edges.Of(box.ToRectangle()), r, Edges.Of(screen), Gap) is { } moved)
            {
                o.DisplayRegion = RegionSettings.From(moved.Box.ToDRect());
                return moved.Side == Side.Above ? PlacementFix.MovedAbove : PlacementFix.MovedBelow;
            }
            o.Position = SubtitlePosition.AboveRegion; // no room for the box: the overlay places the lines itself
            return PlacementFix.MovedAbove;
        }
        return PlacementFix.None;
    }

    /// <summary>The notice for what <see cref="Enforce"/> changed; "" for nothing.</summary>
    public static string NoticeFor(PlacementFix fix) => fix switch
    {
        PlacementFix.MovedAbove => Loc.Instance["Notice.ModeSharedMoved"],
        PlacementFix.MovedBelow => Loc.Instance["Notice.ModeSharedMovedBelow"],
        _ => "",
    };
}
