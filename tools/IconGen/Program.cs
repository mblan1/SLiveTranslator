using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// One hand-tuned layout per output size, in device pixels. Small sizes use whole-pixel
/// edges (crisp bars, square caps); 48 px and up get rounded caps and subtle depth.
/// </summary>
internal sealed record Layout(
    int Size,
    double Margin, double Corner,          // outer rounded square
    Rect Bubble, double BubbleCorner,      // white speech bubble body
    double TailX, double TailWidth, double TailTip, // tail: vertical left edge at TailX, tip at (TailX, TailTip)
    Rect Bar1, Rect Bar2, double BarCorner, // subtitle bars (top: gradient, bottom: amber)
    bool Polish);                          // sheen, bubble shading and soft shadow

internal static class Program
{
    private static readonly Color Teal = Color.FromRgb(0x0F, 0x6C, 0xBD);
    private static readonly Color Violet = Color.FromRgb(0x6B, 0x4F, 0xD8);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xB0, 0x20);

    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    [STAThread]
    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRepoRoot();
        string assets = Path.Combine(root, "src", "SLiveTranslator", "Assets");
        string toolDir = Path.Combine(root, "tools", "IconGen");
        Directory.CreateDirectory(assets);
        Directory.CreateDirectory(toolDir);

        var images = new Dictionary<int, byte[]>();
        foreach (int size in Sizes)
            images[size] = Render(LayoutFor(size));

        byte[] png256 = EncodePng(256, 256, images[256]);

        string icoPath = Path.Combine(assets, "app.ico");
        string pngPath = Path.Combine(assets, "app-256.png");
        string previewPath = Path.Combine(toolDir, "preview.png");

        File.WriteAllBytes(icoPath, BuildIco(images, png256));
        File.WriteAllBytes(pngPath, png256);
        File.WriteAllBytes(previewPath, BuildPreview(images));

        Console.WriteLine($"wrote {icoPath}");
        Console.WriteLine($"wrote {pngPath}");
        Console.WriteLine($"wrote {previewPath}");
        return Verify(icoPath) ? 0 : 1;
    }

    // ---------------------------------------------------------------- layouts

    private static Rect R(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);

    private static Layout LayoutFor(int size) => size switch
    {
        // Full-bleed at tray sizes; bars 2 px with square caps.
        16 => new Layout(16, 0, 3.5, R(2, 3, 14, 12), 2, 4, 4, 15,
                         R(4, 5, 12, 7), R(4, 8, 9, 10), 0, false),
        20 => new Layout(20, 0, 4.5, R(3, 4, 17, 14), 2.5, 5, 4, 17,
                         R(5, 6, 15, 8), R(5, 10, 11, 12), 0, false),
        24 => new Layout(24, 1, 5, R(4, 4, 20, 16), 3, 6, 5, 20,
                         R(6, 6, 18, 9), R(6, 11, 13, 14), 0, false),
        32 => new Layout(32, 1, 6.5, R(5, 6, 27, 22), 4, 8, 6, 26,
                         R(8, 9, 24, 13), R(8, 15, 18, 19), 1, false),
        40 => new Layout(40, 1, 8.5, R(6, 7, 34, 28), 5, 10, 8, 33,
                         R(10, 11, 30, 16), R(10, 19, 23, 24), 2.5, false),
        48 => new Layout(48, 2, 9.5, R(8, 9, 40, 34), 5, 13, 9, 40,
                         R(13, 14, 35, 20), R(13, 23, 28, 29), 3, true),
        64 => new Layout(64, 2, 13, R(10, 12, 54, 44), 8, 16, 12, 52,
                         R(16, 18, 48, 26), R(16, 30, 36, 38), 4, true),
        256 => new Layout(256, 8, 53, R(40, 48, 216, 176), 32, 64, 48, 208,
                          R(64, 80, 192, 112), R(64, 128, 144, 160), 16, true),
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    // ---------------------------------------------------------------- drawing

    /// <summary>Renders one size and returns straight (non-premultiplied) BGRA, top-down rows.</summary>
    private static byte[] Render(Layout l)
    {
        int n = l.Size;
        double m = l.Margin;
        var square = new Rect(m, m, n - 2 * m, n - 2 * m);

        var gradient = new LinearGradientBrush(Teal, Violet, new Point(m, m), new Point(n - m, n - m))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        gradient.Freeze();

        var root = new ContainerVisual();

        var back = new DrawingVisual();
        using (DrawingContext dc = back.RenderOpen())
        {
            dc.DrawRoundedRectangle(gradient, null, square, l.Corner, l.Corner);
            if (l.Polish)
            {
                // Soft top-light, Fluent style.
                var sheen = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF), 0),
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.6),
                    },
                };
                sheen.Freeze();
                dc.DrawRoundedRectangle(sheen, null, square, l.Corner, l.Corner);
            }
        }
        root.Children.Add(back);

        Brush bubbleFill = Brushes.White;
        if (l.Polish)
        {
            var shade = new LinearGradientBrush(Colors.White, Color.FromRgb(0xEC, 0xEE, 0xFB), 90);
            shade.Freeze();
            bubbleFill = shade;
        }

        var amber = new SolidColorBrush(Amber);
        amber.Freeze();

        var front = new DrawingVisual();
        using (DrawingContext dc = front.RenderOpen())
        {
            dc.DrawGeometry(bubbleFill, null, BubbleGeometry(l));
            dc.DrawRoundedRectangle(gradient, null, l.Bar1, l.BarCorner, l.BarCorner);
            dc.DrawRoundedRectangle(amber, null, l.Bar2, l.BarCorner, l.BarCorner);
        }
        if (l.Polish)
        {
            front.Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x14, 0x10, 0x4A),
                Direction = 270,
                ShadowDepth = n * 0.02,
                BlurRadius = n * 0.06,
                Opacity = 0.35,
                RenderingBias = RenderingBias.Quality,
            };
        }
        root.Children.Add(front);

        var rtb = new RenderTargetBitmap(n, n, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        return ToStraightBgra(rtb);
    }

    private static Geometry BubbleGeometry(Layout l)
    {
        var body = new RectangleGeometry(l.Bubble, l.BubbleCorner, l.BubbleCorner);
        double bottom = l.Bubble.Bottom;
        var tail = new StreamGeometry();
        using (StreamGeometryContext g = tail.Open())
        {
            // Starts 1 px inside the body so the union has no seam.
            g.BeginFigure(new Point(l.TailX, bottom - 1), true, true);
            g.LineTo(new Point(l.TailX + l.TailWidth, bottom - 1), false, false);
            g.LineTo(new Point(l.TailX + l.TailWidth, bottom), false, false);
            g.LineTo(new Point(l.TailX, l.TailTip), false, false);
        }
        Geometry union = Geometry.Combine(body, tail, GeometryCombineMode.Union, null, 0.01, ToleranceType.Absolute);
        union.Freeze();
        return union;
    }

    private static byte[] ToStraightBgra(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var px = new byte[w * h * 4];
        converted.CopyPixels(px, w * 4, 0);
        return px;
    }

    private static byte[] EncodePng(int width, int height, byte[] bgra)
    {
        BitmapSource bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- ICO

    private static byte[] BuildIco(IReadOnlyDictionary<int, byte[]> images, byte[] png256)
    {
        var payloads = new List<(int Size, byte[] Data)>();
        foreach (int size in Sizes)
            payloads.Add((size, size == 256 ? png256 : BuildDib(size, images[size])));

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // ICONDIR
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type: icon
        w.Write((ushort)payloads.Count); // count

        // ICONDIRENTRY[]
        int offset = 6 + 16 * payloads.Count;
        foreach ((int size, byte[] data) in payloads)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // width (0 = 256)
            w.Write((byte)(size >= 256 ? 0 : size)); // height
            w.Write((byte)0);                         // palette colours
            w.Write((byte)0);                         // reserved
            w.Write((ushort)1);                       // planes
            w.Write((ushort)32);                      // bits per pixel
            w.Write(data.Length);                     // bytes in resource
            w.Write(offset);                          // image offset
            offset += data.Length;
        }

        foreach ((_, byte[] data) in payloads)
            w.Write(data);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>32-bit BGRA DIB: BITMAPINFOHEADER (height doubled), bottom-up XOR rows, zero AND mask.</summary>
    private static byte[] BuildDib(int n, byte[] bgra)
    {
        int xorSize = n * n * 4;
        int maskStride = (n + 31) / 32 * 4;
        int andSize = maskStride * n;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);              // biSize
        w.Write(n);               // biWidth
        w.Write(n * 2);           // biHeight (XOR + AND)
        w.Write((ushort)1);       // biPlanes
        w.Write((ushort)32);      // biBitCount
        w.Write(0);               // biCompression = BI_RGB
        w.Write(xorSize + andSize); // biSizeImage
        w.Write(0);               // biXPelsPerMeter
        w.Write(0);               // biYPelsPerMeter
        w.Write(0);               // biClrUsed
        w.Write(0);               // biClrImportant

        for (int y = n - 1; y >= 0; y--)
            w.Write(bgra, y * n * 4, n * 4);

        w.Write(new byte[andSize]);
        w.Flush();
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- preview

    private static readonly int[] Zoomed = { 16, 24, 32 };
    private const int Zoom = 8;
    private const int Pad = 24;
    private const int Gap = 24;
    private const int LabelH = 22;
    private const int TitleH = 26;

    private static byte[] BuildPreview(IReadOnlyDictionary<int, byte[]> images)
    {
        int row1W = 0, row2W = 0;
        foreach (int s in Sizes) row1W += s;
        row1W += Gap * (Sizes.Length - 1);
        foreach (int s in Zoomed) row2W += s * Zoom;
        row2W += Gap * 2 * (Zoomed.Length - 1);

        int panelW = Pad * 2 + Math.Max(row1W, row2W);
        int row1H = 256, row2H = 32 * Zoom;
        int panelH = Pad + TitleH + row1H + LabelH + Gap + row2H + LabelH + Pad;
        int width = panelW * 2, height = panelH;

        var placements = new List<(int Size, int Scale, int X, int Y)>();
        var labels = new List<(string Text, double X, double Y, bool Dark)>();

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            for (int p = 0; p < 2; p++)
            {
                bool dark = p == 1;
                int ox = p * panelW;
                dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
                                 null, new Rect(ox, 0, panelW, panelH));
                labels.Add((dark ? "Dark  #202020" : "Light  #F3F3F3", ox + Pad, Pad, dark));

                // Row 1: every size at 1:1, bottom-aligned.
                int x = ox + Pad;
                int baseY = Pad + TitleH + row1H;
                foreach (int s in Sizes)
                {
                    placements.Add((s, 1, x, baseY - s));
                    labels.Add(($"{s}", x, baseY + 4, dark));
                    x += s + Gap;
                }

                // Row 2: small sizes scaled 8x, nearest-neighbour.
                x = ox + Pad;
                int base2 = baseY + LabelH + Gap + row2H;
                foreach (int s in Zoomed)
                {
                    placements.Add((s, Zoom, x, base2 - s * Zoom));
                    labels.Add(($"{s} px  x{Zoom}", x, base2 + 4, dark));
                    x += s * Zoom + Gap * 2;
                }
            }

            var typeface = new Typeface("Segoe UI");
            foreach ((string text, double lx, double ly, bool dark) in labels)
            {
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 13,
                                           dark ? Brushes.Gainsboro : Brushes.DimGray, 1.0);
                dc.DrawText(ft, new Point(lx, ly));
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        byte[] canvas = ToStraightBgra(rtb); // opaque, so straight == premultiplied

        foreach ((int s, int scale, int px, int py) in placements)
            Blit(canvas, width, images[s], s, scale, px, py);

        return EncodePng(width, height, canvas);
    }

    /// <summary>Alpha-blends a straight-BGRA image onto an opaque canvas, nearest-neighbour scaled.</summary>
    private static void Blit(byte[] canvas, int canvasW, byte[] src, int n, int scale, int x0, int y0)
    {
        int size = n * scale;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int si = ((y / scale) * n + (x / scale)) * 4;
                int di = ((y0 + y) * canvasW + (x0 + x)) * 4;
                int a = src[si + 3];
                for (int c = 0; c < 3; c++)
                    canvas[di + c] = (byte)((src[si + c] * a + canvas[di + c] * (255 - a) + 127) / 255);
                canvas[di + 3] = 255;
            }
        }
    }

    // ---------------------------------------------------------------- verification

    private static bool Verify(string icoPath)
    {
        byte[] ico = File.ReadAllBytes(icoPath);
        int count = BitConverter.ToUInt16(ico, 4);
        bool ok = BitConverter.ToUInt16(ico, 0) == 0 && BitConverter.ToUInt16(ico, 2) == 1 && count == Sizes.Length;
        Console.WriteLine($"{Path.GetFileName(icoPath)}: {ico.Length} bytes, {count} entries");
        for (int i = 0; i < count; i++)
        {
            int e = 6 + 16 * i;
            int w = ico[e] == 0 ? 256 : ico[e];
            int h = ico[e + 1] == 0 ? 256 : ico[e + 1];
            int bpp = BitConverter.ToUInt16(ico, e + 6);
            int len = BitConverter.ToInt32(ico, e + 8);
            int off = BitConverter.ToInt32(ico, e + 12);
            bool png = ico[off] == 0x89 && ico[off + 1] == (byte)'P' && ico[off + 2] == (byte)'N' && ico[off + 3] == (byte)'G';
            ok &= w == Sizes[i] && h == Sizes[i] && bpp == 32 && off + len <= ico.Length && png == (Sizes[i] == 256);
            Console.WriteLine($"  {w,3}x{h,-3} {bpp}bpp {(png ? "PNG" : "DIB"),-3} {len,7} bytes @ {off}");
        }

        using var stream = new MemoryStream(ico);
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var decoded = new List<string>();
        foreach (BitmapFrame f in decoder.Frames)
            decoded.Add($"{f.PixelWidth}");
        Console.WriteLine($"  WPF IconBitmapDecoder frames: {string.Join(", ", decoded)}");
        ok &= decoder.Frames.Count == Sizes.Length;

        Console.WriteLine(ok ? "verify: OK" : "verify: FAILED");
        return ok;
    }

    private static string FindRepoRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? d = new(start); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "SLiveTranslator.slnx")))
                    return d.FullName;
            }
        }
        throw new InvalidOperationException("Repo root not found; pass it as the first argument.");
    }
}
