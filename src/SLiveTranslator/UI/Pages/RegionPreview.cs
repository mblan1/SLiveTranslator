using System;
using System.Linq;
using SLiveTranslator.Models;
using SLiveTranslator.UI.Overlay;
using DRect = System.Drawing.Rectangle;
using DSize = System.Drawing.Size;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI.Pages;

/// <summary>A box on a small picture of a monitor, in the picture's DIPs.</summary>
public sealed record PreviewBox(double Left, double Top, double Width, double Height);

/// <summary>
/// The read region and where the translation goes, drawn small on a picture of the read region's monitor (the Regions
/// page and the Home page's card). The translation's box is where the overlay would put two lines of it.
/// </summary>
public sealed record RegionPreview(PreviewBox? Read, PreviewBox? Show, string MonitorText)
{
    public static RegionPreview For(AppSettings settings, double width, double height)
    {
        if (settings.Subtitles.Region is not { } region) return new RegionPreview(null, null, MonitorOf(null));
        var read = region.ToRectangle();
        var screen = Forms.Screen.FromRectangle(read);
        var bounds = screen.Bounds;
        double k = Math.Min(width / bounds.Width, height / bounds.Height);
        PreviewBox Box(DRect r) => new((r.Left - bounds.Left) * k, (r.Top - bounds.Top) * k, Math.Max(2, r.Width * k), Math.Max(2, r.Height * k));
        return new RegionPreview(Box(read), Box(ShowBox(settings.Overlay, read, bounds)), MonitorOf(screen));
    }

    /// <summary>Where two lines of translation would go (physical pixels).</summary>
    public static DRect ShowBox(OverlaySettings look, DRect read, DRect screen)
    {
        var style = SubtitleStyle.From(look);
        int width = SubtitlePlacement.MaxWidthPx(style, read, screen, 1);
        if (style.Position != SubtitlePosition.Custom) width = Math.Min(width, Math.Max(read.Width, 480));
        int height = (int)Math.Round(Math.Clamp(look.FontSize, 10, 80) * 1.36 * 2 + 22);
        if (style.Position == SubtitlePosition.Custom && style.DisplayRegion is { } custom) return custom;
        return SubtitlePlacement.Place(style, read, new DSize(width, height), screen, 6);
    }

    /// <summary>"Display 1 · 1920 × 1080 · primary"; the primary monitor's when no region is selected.</summary>
    static string MonitorOf(Forms.Screen? screen)
    {
        screen ??= Forms.Screen.PrimaryScreen;
        if (screen is null) return "";
        int number = Array.FindIndex(Forms.Screen.AllScreens, s => s.DeviceName == screen.DeviceName) + 1;
        return Loc.F(screen.Primary ? "Regions.MonitorPrimary" : "Regions.Monitor", Math.Max(1, number), screen.Bounds.Width, screen.Bounds.Height);
    }
}
