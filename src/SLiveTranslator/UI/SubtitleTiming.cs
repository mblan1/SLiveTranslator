using System;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI;

/// <summary>The timing rules of the subtitle box (the Timing page), as plain arithmetic so they are testable.</summary>
public static class SubtitleTiming
{
    /// <summary>The line the game shows in the Timing page's timeline, and how many characters it has.</summary>
    public const double SampleShowSeconds = 3;

    public const int SampleChars = 80;

    /// <summary>
    /// How long the lines stay after the game hid its subtitle; null keeps them until the next line (or Clear).
    /// A line the game showed only for a moment still gets the shortest time on screen in all.
    /// </summary>
    /// <param name="newestText">The newest translated line, for <see cref="StayMode.ByLength"/>.</param>
    /// <param name="shownFor">How long that line has been on screen already.</param>
    public static TimeSpan? Stay(OverlaySettings o, string newestText, TimeSpan shownFor)
    {
        double min = MinSeconds(o);
        double stay;
        switch (o.Stay)
        {
            case StayMode.UntilNext:
                return null;
            case StayMode.ByLength:
                stay = ByLength(o, newestText.Length);
                break;
            default:
                stay = Math.Clamp(o.StaySeconds, 0, 20);
                break;
        }
        stay = Math.Max(stay, min - shownFor.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(0, stay));
    }

    /// <summary>Seconds a line of <paramref name="chars"/> characters stays at the user's reading speed, at least the shortest time.</summary>
    public static double ByLength(OverlaySettings o, int chars) => Math.Max(MinSeconds(o), chars / (double)ReadingSpeed(o));

    public static double MinSeconds(OverlaySettings o) => Math.Clamp(o.MinSeconds, 0.5, 5);

    public static int ReadingSpeed(OverlaySettings o) => Math.Clamp(o.ReadingSpeed, 8, 30);

    /// <summary>How many lines the box shows: one when a new line replaces the last.</summary>
    public static int VisibleLines(OverlaySettings o) => o.NewLines == NewLineMode.Replace ? 1 : Math.Clamp(o.MaxLines, 1, 5);

    public static TimeSpan Fade(int ms) => TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, 2000));
}
