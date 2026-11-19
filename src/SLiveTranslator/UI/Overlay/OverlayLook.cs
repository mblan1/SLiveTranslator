using System;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SLiveTranslator.UI.Overlay;

/// <summary>Brushes and the font of every overlay window: made once and frozen, so drawing a line allocates none.</summary>
internal static class OverlayLook
{
    public static readonly FontFamily Font = new("Segoe UI");

    public static readonly Brush Text = Brushes.White;
    public static readonly Brush Pending = Frozen(0xFF, 0xB0, 0xBE, 0xC5);
    public static readonly Brush Original = Frozen(0xFF, 0x90, 0x9A, 0xA0);
    public static readonly Brush Outline = Frozen(0x60, 0xFF, 0xFF, 0xFF);
    public static readonly Brush SubtitleBackground = Frozen(0xD0, 0x10, 0x14, 0x18);
    public static readonly Brush BadgeBackground = Frozen(0xC8, 0x10, 0x14, 0x18);
    public static readonly Brush LiveDot = Frozen(0xFF, 0x4C, 0xAF, 0x50);
    public static readonly Brush HistoryBackground = Frozen(0xE6, 0x10, 0x14, 0x18);
    public static readonly Brush HistoryTitle = Frozen(0xFF, 0x90, 0xCA, 0xF9);
    public static readonly Brush ToastBackground = Frozen(0xEE, 0x10, 0x14, 0x18);
    public static readonly Brush LabelBackground = Frozen(0xD8, 0x10, 0x14, 0x18);
    public static readonly Brush LabelOutline = Frozen(0x50, 0xFF, 0xFF, 0xFF);

    static readonly Brush AccentInfo = Frozen(0xFF, 0x21, 0x96, 0xF3);
    static readonly Brush AccentOn = Frozen(0xFF, 0x4C, 0xAF, 0x50);
    static readonly Brush AccentOff = Frozen(0xFF, 0x9E, 0x9E, 0x9E);
    static readonly Brush AccentWarning = Frozen(0xFF, 0xFF, 0xB3, 0x00);
    static readonly Brush AccentError = Frozen(0xFF, 0xF4, 0x43, 0x36);

    public static readonly Brush TimeLeftTrack = Frozen(0x2E, 0xFF, 0xFF, 0xFF);
    public static readonly Brush TimeLeftFill = Frozen(0xCC, 0xFF, 0xFF, 0xFF);

    /// <summary>The dark halo round each letter of the outlined subtitles.</summary>
    public static readonly Effect OutlineHalo = FrozenShadow(depth: 0, blur: 4, opacity: 1);

    /// <summary>The shadow a little below the letters, over the halo: together they read like an outline.</summary>
    public static readonly Effect OutlineDrop = FrozenShadow(depth: 1.5, blur: 3, opacity: 0.9);

    static Brush _subtitleBackground = SubtitleBackground;

    /// <summary>The subtitle box's dark background at an opacity (0 to 1).</summary>
    public static Brush SubtitleBackgroundAt(double opacity) => SubtitleBackgroundAt(SubtitleStyle.DarkBox, opacity);

    /// <summary>
    /// The subtitle box's background in a colour ("#RRGGBB"; "" is transparent) at an opacity (0 to 1). The brush of the
    /// last colour and opacity asked for is kept, so a line allocates none.
    /// </summary>
    public static Brush SubtitleBackgroundAt(string color, double opacity)
    {
        if (BackgroundColors.Normalize(color).Length == 0) return Brushes.Transparent;
        byte alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        var (r, g, b) = BackgroundColors.Rgb(color);
        var brush = _subtitleBackground;
        if (((SolidColorBrush)brush).Color != Color.FromArgb(alpha, r, g, b)) _subtitleBackground = brush = Frozen(alpha, r, g, b);
        return brush;
    }

    static Effect FrozenShadow(double depth, double blur, double opacity)
    {
        var effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = depth, BlurRadius = blur, Opacity = opacity, Direction = 270 };
        effect.Freeze();
        return effect;
    }

    /// <summary>The colour of a toast's bottom edge.</summary>
    public static Brush Accent(StatusKind kind) => kind switch
    {
        StatusKind.On => AccentOn,
        StatusKind.Off => AccentOff,
        StatusKind.Warning => AccentWarning,
        StatusKind.Error => AccentError,
        _ => AccentInfo,
    };

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
