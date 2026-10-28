using System;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI;

public enum StatusKind { Info, On, Off, Warning, Error }

/// <param name="Pending">The translation is not there yet (the text is a placeholder).</param>
/// <param name="Failed">Not translated: the text is the original, shown dimmed.</param>
/// <param name="LowConfidence">OCR never read the line cleanly; shown in italics.</param>
/// <param name="Original">The line as read from the screen, shown under its translation when <see cref="SubtitleStyle.ShowOriginal"/> is on.</param>
public sealed record SubtitleLineView(string Text, bool Pending, bool Failed = false, bool LowConfidence = false, string? Original = null);

public sealed record HistoryLineView(string Translated, string Original, bool Pending);

/// <summary>How the subtitle box looks (<see cref="OverlaySettings"/>).</summary>
/// <param name="TextOpacity">0.2 to 1.</param>
/// <param name="BackgroundOpacity">0 to 1.</param>
public sealed record SubtitleStyle(double TextOpacity, double BackgroundOpacity, SubtitlePosition Position, bool ShowOriginal)
{
    public static SubtitleStyle Default { get; } = From(new OverlaySettings());

    public static SubtitleStyle From(OverlaySettings settings) => new(Math.Clamp(settings.TextOpacity, 0.2, 1), Math.Clamp(settings.BackgroundOpacity, 0, 1),
        settings.Position, settings.ShowOriginal);
}
