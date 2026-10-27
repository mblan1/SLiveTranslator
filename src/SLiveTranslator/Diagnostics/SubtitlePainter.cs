using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;

namespace SLiveTranslator.Diagnostics;

/// <summary>
/// Draws Witcher-style subtitle frames in memory: white text with a dark shadow over a busy scene that pans like a
/// cutscene camera (or stays still), as wide as a 2560 px monitor, with a "Space Skip" key hint beside the region.
/// </summary>
internal sealed class SubtitlePainter : IDisposable
{
    public const int Width = 2560, Height = 212, CentreX = Width / 2;

    /// <summary>The scene with a pale sky and bright patches, some as light as the text.</summary>
    public const int BrightScene = 1;

    readonly Bitmap[] _scenes;
    readonly Dictionary<float, Font> _fontCache = new();

    public SubtitlePainter()
    {
        _scenes = [Scene(7, bright: false), Scene(11, bright: true), Scene(13, bright: false)];
    }

    /// <summary>The scene a frame shows: three scenes in turn, 40 frames each; always <paramref name="stillScene"/> on a still camera.</summary>
    public static int SceneOf(int frame, bool still, int stillScene = 0) => still ? stillScene : frame / 40 % 3;

    /// <summary>A wide busy texture; frames show a window of it that slides, like a camera pan in a cutscene.</summary>
    static Bitmap Scene(int seed, bool bright)
    {
        var scene = new Bitmap(Width + 800, Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(scene);
        var rnd = new Random(seed);
        var (a, b) = bright
            ? (Color.FromArgb(150, 165, 185), Color.FromArgb(120, 110, 80))
            : (Color.FromArgb(35, 40, 48), Color.FromArgb(85, 70, 55));
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, scene.Width, Height), a, b, 20f))
            g.FillRectangle(bg, 0, 0, scene.Width, Height);
        for (int i = 0; i < 420; i++)
        {
            int v = rnd.Next(bright ? 100 : 20, bright ? 256 : 190);
            using var brush = new SolidBrush(Color.FromArgb(rnd.Next(70, 200), v, Math.Max(0, v - rnd.Next(0, 60)), Math.Max(0, v - rnd.Next(0, 90))));
            g.FillEllipse(brush, rnd.Next(-20, scene.Width), rnd.Next(-20, Height), rnd.Next(6, 90), rnd.Next(4, 46));
        }
        return scene;
    }

    Font FontOf(float size)
    {
        if (!_fontCache.TryGetValue(size, out var font)) _fontCache[size] = font = new Font("Segoe UI", size, GraphicsUnit.Pixel);
        return font;
    }

    /// <summary>Width of the widest line of the text as drawn.</summary>
    public float Measure(string text, float size)
    {
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        return text.Split('\n').Max(line => g.MeasureString(line, FontOf(size)).Width);
    }

    /// <summary>Draws a frame into <paramref name="target"/> (<see cref="Width"/> x <see cref="Height"/>).</summary>
    /// <param name="still">The camera does not move: every frame shows the same part of <paramref name="stillScene"/>.</param>
    public void Draw(Bitmap target, int frame, string? text, float size, bool still, int stillScene = 0)
    {
        using var g = Graphics.FromImage(target);
        int pan = still ? 0 : frame * 11 % 800;
        g.DrawImage(_scenes[SceneOf(frame, still, stillScene)], new Rectangle(0, 0, Width, Height), new Rectangle(pan, 0, Width, Height), GraphicsUnit.Pixel);

        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        DrawShadowed(g, "Space", FontOf(22f), 2270, 170);
        DrawShadowed(g, "Skip", FontOf(22f), 2350, 170);
        if (text is null) return;

        var font = FontOf(size);
        var lines = text.Split('\n');
        float lineHeight = size * 1.32f;
        float top = Height - 40 - lines.Length * lineHeight;
        for (int i = 0; i < lines.Length; i++)
        {
            float w = g.MeasureString(lines[i], font).Width;
            DrawShadowed(g, lines[i], font, CentreX - w / 2, top + i * lineHeight);
        }
    }

    static void DrawShadowed(Graphics g, string text, Font font, float x, float y)
    {
        using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        foreach (var (dx, dy) in new[] { (1, 1), (2, 2), (-1, 1), (1, -1), (0, 2) }) g.DrawString(text, font, shadow, x + dx, y + dy);
        g.DrawString(text, font, Brushes.White, x, y);
    }

    public void Dispose()
    {
        foreach (var s in _scenes) s.Dispose();
        foreach (var f in _fontCache.Values) f.Dispose();
    }
}
