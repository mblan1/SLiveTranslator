using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SLiveTranslator.UI;

public static class TrayIcon
{
    public static readonly Color Idle = Color.FromArgb(0x1E, 0x88, 0xE5);
    public static readonly Color Live = Color.FromArgb(0x43, 0xA0, 0x47);

    /// <summary>Draws the tray icon at runtime so the project needs no image assets. Green = subtitles on.</summary>
    public static Icon Create(Color background)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(background);
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("T", font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
