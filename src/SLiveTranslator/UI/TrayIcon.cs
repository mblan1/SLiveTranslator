using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI;

/// <summary>
/// The tray icons: the app icon at the system's small-icon size, so Windows shows the frame drawn for that size
/// instead of scaling another one, and the same with a green dot while subtitles are on. Made once and kept: switching
/// between them makes no new icon handle.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public static readonly Color LiveDot = Color.FromArgb(0x43, 0xA0, 0x47);

    TrayIcon(Icon idle, Icon live)
    {
        Idle = idle;
        Live = live;
    }

    public Icon Idle { get; }
    public Icon Live { get; }

    /// <summary>Reads the app icon from the WPF resources; needs the running WPF application.</summary>
    public static TrayIcon Load()
    {
        // Made here, not in a static field: "pack:" parses only once WPF has registered the scheme.
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        return From(stream, Forms.SystemInformation.SmallIconSize);
    }

    internal static TrayIcon From(Stream ico, Size size)
    {
        var idle = new Icon(ico, size);
        using var bitmap = idle.ToBitmap();
        DrawLiveDot(bitmap);
        return new TrayIcon(idle, ToIcon(bitmap));
    }

    /// <summary>About 40 % of the icon in the bottom-right corner, with a dark rim so it shows on light and dark taskbars.</summary>
    static void DrawLiveDot(Bitmap bitmap)
    {
        int s = Math.Min(bitmap.Width, bitmap.Height);
        int d = Math.Max(6, (int)Math.Round(s * 0.42));
        var rect = new Rectangle(s - d - 1, s - d - 1, d, d);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(LiveDot);
        using var rim = new Pen(Color.FromArgb(0xE0, 0x10, 0x14, 0x18), 1f);
        g.FillEllipse(fill, rect);
        g.DrawEllipse(rim, rect);
    }

    /// <summary>
    /// The bitmap as a one-image .ico with its alpha as it is. Bitmap.GetHicon would premultiply it, which darkens
    /// every half-transparent pixel of the rounded corners into a dark fringe.
    /// </summary>
    static Icon ToIcon(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var pixels = new byte[w * h * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * w * 4, w * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        int maskStride = (w + 31) / 32 * 4;
        int imageBytes = 40 + pixels.Length + maskStride * h;
        using var ico = new MemoryStream();
        using (var o = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            o.Write((short)0); o.Write((short)1); o.Write((short)1); // icon file, one image
            o.Write((byte)w); o.Write((byte)h); o.Write((byte)0); o.Write((byte)0);
            o.Write((short)1); o.Write((short)32); o.Write(imageBytes); o.Write(6 + 16);
            o.Write(40); o.Write(w); o.Write(h * 2); o.Write((short)1); o.Write((short)32); // BITMAPINFOHEADER: XOR + AND height
            o.Write(0); o.Write(imageBytes - 40); o.Write(0); o.Write(0); o.Write(0); o.Write(0);
            for (int y = h - 1; y >= 0; y--) o.Write(pixels, y * w * 4, w * 4); // bottom-up BGRA
            o.Write(new byte[maskStride * h]); // the alpha channel decides; the AND mask stays empty
        }
        ico.Position = 0;
        return new Icon(ico);
    }

    public void Dispose()
    {
        Idle.Dispose();
        Live.Dispose();
    }
}
