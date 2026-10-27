using System.Drawing;

namespace SLiveTranslator.Ocr;

/// <summary>A recognized line (or merged paragraph) of text. Bounds are in pixels of the image it was recognized from.</summary>
public sealed record OcrLine(string Text, Rectangle Bounds, int LineCount = 1);
