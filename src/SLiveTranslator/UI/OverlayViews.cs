namespace SLiveTranslator.UI;

public enum StatusKind { Info, On, Off, Warning, Error }

/// <param name="Pending">The translation is not there yet (the text is a placeholder).</param>
/// <param name="Failed">Not translated: the text is the original, shown dimmed.</param>
/// <param name="LowConfidence">OCR never read the line cleanly; shown in italics.</param>
public sealed record SubtitleLineView(string Text, bool Pending, bool Failed = false, bool LowConfidence = false);

public sealed record HistoryLineView(string Translated, string Original, bool Pending);
