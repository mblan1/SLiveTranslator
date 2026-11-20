using System;
using SLiveTranslator.Models;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.UI;

public enum StatusKind { Info, On, Off, Warning, Error }

/// <param name="Pending">The translation is not there yet (the text is a placeholder).</param>
/// <param name="Failed">Not translated: the text is the original, shown dimmed.</param>
/// <param name="LowConfidence">OCR never read the line cleanly; shown in italics.</param>
/// <param name="Original">The line as read from the screen, shown under its translation when <see cref="SubtitleStyle.ShowOriginal"/> is on.</param>
public sealed record SubtitleLineView(string Text, bool Pending, bool Failed = false, bool LowConfidence = false, string? Original = null);

public sealed record HistoryLineView(string Translated, string Original, bool Pending);

/// <summary>How the subtitle box looks and where it goes (<see cref="OverlaySettings"/>).</summary>
/// <param name="TextOpacity">0.2 to 1.</param>
/// <param name="BackgroundOpacity">0 to 1, of <see cref="BackgroundColor"/>.</param>
public sealed record SubtitleStyle(double TextOpacity, double BackgroundOpacity, SubtitlePosition Position, bool ShowOriginal)
{
    /// <summary>The old dark box, for a style made by hand; the settings' default is "" (no box).</summary>
    public const string DarkBox = "#101418";

    public static SubtitleStyle Default { get; } = From(new OverlaySettings());

    /// <summary>"#RRGGBB" of the box behind the lines; "" draws the text alone.</summary>
    public string BackgroundColor { get; init; } = DarkBox;

    /// <summary>A dark outline round the letters.</summary>
    public bool TextOutline { get; init; }

    public TextAlign Align { get; init; } = TextAlign.Center;

    /// <summary>The box of <see cref="SubtitlePosition.Custom"/>; null falls back to the read region.</summary>
    public DRect? DisplayRegion { get; init; }

    /// <summary>
    /// The lines must keep off the read region (Shared on screen: the app's own OCR would read them): a place that would
    /// cover it gives way to above it, then below it.
    /// </summary>
    public bool AvoidRegion { get; init; }

    /// <summary>How long the box takes to appear when it was hidden.</summary>
    public TimeSpan FadeIn { get; init; }

    /// <summary>Room for the time-left bar under the lines, kept while it does not run so the box does not jump.</summary>
    public bool TimeLeftBar { get; init; }

    public static SubtitleStyle From(OverlaySettings settings) => new(Math.Clamp(settings.TextOpacity, 0.2, 1), Math.Clamp(settings.BackgroundOpacity, 0, 1),
        settings.Position, settings.ShowOriginal)
    {
        BackgroundColor = BackgroundColors.Normalize(settings.BackgroundColor),
        TextOutline = settings.TextOutline,
        Align = settings.Align,
        DisplayRegion = settings.DisplayRegion?.ToRectangle(),
        AvoidRegion = Overlay.RegionRules.MustAvoid(settings),
        FadeIn = SubtitleTiming.Fade(settings.FadeInMs),
        TimeLeftBar = settings.TimeLeftBar && settings.Stay != StayMode.UntilNext,
    };
}

/// <summary>Which overlay windows screen capture sees; <see cref="None"/> in Private overlay mode.</summary>
public sealed record SharedWindows(bool Subtitles, bool History, bool Labels, bool Notices)
{
    public static SharedWindows None { get; } = new(false, false, false, false);

    public static SharedWindows For(OverlaySettings o) =>
        o.DisplayMode == DisplayMode.Shared ? new(o.Share.Subtitles, o.Share.History, o.Share.Labels, o.Share.Notices) : None;
}

/// <summary>The colours offered for the box behind the subtitles, and the check of a typed one.</summary>
public static class BackgroundColors
{
    public const string Transparent = "";

    /// <summary>The swatches, after "transparent": black, charcoal, slate, navy.</summary>
    public static readonly string[] Swatches = ["#000000", "#1E1E1E", "#26323D", "#0B1C2C"];

    static readonly string[] SwatchNames = ["Region.Swatch.Black", "Region.Swatch.Charcoal", "Region.Swatch.Slate", "Region.Swatch.Navy"];

    /// <summary>"#RRGGBB" in capitals for a valid colour (with or without "#", 3 or 6 digits); "" for anything else.</summary>
    public static string Normalize(string? color)
    {
        string c = (color ?? "").Trim().TrimStart('#');
        if (c.Length == 3) c = string.Concat(c[0], c[0], c[1], c[1], c[2], c[2]);
        if (c.Length != 6) return Transparent;
        foreach (char ch in c)
            if (!Uri.IsHexDigit(ch)) return Transparent;
        return "#" + c.ToUpperInvariant();
    }

    /// <summary>"Transparent", the swatch's name ("Black"), or the code of another colour.</summary>
    public static string Name(string? color)
    {
        string c = Normalize(color);
        if (c.Length == 0) return Loc.Instance["Region.Transparent"];
        return Array.IndexOf(Swatches, c) is var i and >= 0 ? Loc.Instance[SwatchNames[i]] : c;
    }

    /// <summary>Red, green and blue of a normalized colour.</summary>
    public static (byte R, byte G, byte B) Rgb(string color)
    {
        int n = Convert.ToInt32(Normalize(color).TrimStart('#') is { Length: 6 } hex ? hex : "000000", 16);
        return ((byte)(n >> 16), (byte)(n >> 8), (byte)n);
    }
}
