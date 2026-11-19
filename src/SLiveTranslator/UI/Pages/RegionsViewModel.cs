using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLiveTranslator.Models;
using SLiveTranslator.UI.Overlay;

namespace SLiveTranslator.UI.Pages;

/// <summary>One colour of the box behind the subtitles, as a swatch on the Regions page.</summary>
public sealed class ColorChoice(RegionsViewModel page, string hex) : ObservableObject
{
    public string Hex { get; } = hex;
    public bool IsTransparent => Hex.Length == 0;
    public string Name => BackgroundColors.Name(Hex);

    public bool IsSelected
    {
        get => page.BackgroundColor == Hex;
        set
        {
            if (value) page.BackgroundColor = Hex;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(Name));
    }
}

/// <summary>
/// The Regions page: where the app reads the subtitles, and where the translation goes and how its box looks. Both
/// can be picked on the screen (the picker saves at once) or typed here.
/// </summary>
public sealed class RegionsViewModel : SettingsPageViewModel
{
    public const double PreviewWidth = 448;
    public const double PreviewHeight = 252;

    static readonly string[] Derived =
    [
        nameof(HasRegion), nameof(RegionText), nameof(Preview), nameof(OverlapText), nameof(OverlapKind), nameof(IsCustom),
        nameof(PlaceAbove), nameof(PlaceBelow), nameof(PlaceBottom), nameof(PlaceOver), nameof(PlaceCustom),
        nameof(CustomX), nameof(CustomY), nameof(CustomWidth), nameof(CustomHeight),
    ];

    readonly ISettingsHost _host;
    CancellationTokenSource? _testCts;
    string _customColorText = "";
    string _testText = "";
    MessageKind _testKind;

    public RegionsViewModel(SettingsViewModel root, ISettingsHost host) : base(root)
    {
        _host = host;
        PickBothCommand = new RelayCommand(() => host.SelectRegions(RegionPickMode.Both));
        PickReadCommand = new RelayCommand(() => host.SelectRegions(RegionPickMode.ReadOnly));
        PickDisplayCommand = new RelayCommand(() => host.SelectRegions(RegionPickMode.DisplayOnly), () => HasRegion);
        TestReadingCommand = new AsyncRelayCommand(TestReadingAsync, () => HasRegion);
        Swatches = new[] { BackgroundColors.Transparent }.Concat(BackgroundColors.Swatches).Select(c => new ColorChoice(this, c)).ToList();
        _customColorText = CustomColorOf(O.BackgroundColor);
    }

    SubtitleSettings S => Working.Subtitles;
    OverlaySettings O => Working.Overlay;

    public RelayCommand PickBothCommand { get; }
    public RelayCommand PickReadCommand { get; }
    public RelayCommand PickDisplayCommand { get; }
    public AsyncRelayCommand TestReadingCommand { get; }

    public override void Reload()
    {
        _customColorText = CustomColorOf(O.BackgroundColor);
        _testText = "";
        base.Reload();
        foreach (var swatch in Swatches) swatch.Refresh();
        PickDisplayCommand.NotifyCanExecuteChanged();
        TestReadingCommand.NotifyCanExecuteChanged();
    }

    public void Relocalize() => Reload();

    // ---- the picture ----

    public RegionPreview Preview => RegionPreview.For(Working, PreviewWidth, PreviewHeight);

    // ---- the read region ----

    public bool HasRegion => S.Region is not null;

    public string RegionText => S.Region is { } r ? Loc.F("Subtitles.RegionValue", r.Width, r.Height, r.X, r.Y) : Loc.Instance["Subtitles.RegionNone"];

    public double ReadX
    {
        get => S.Region?.X ?? 0;
        set => SetRead(r => r with { X = (int)Math.Round(value) });
    }

    public double ReadY
    {
        get => S.Region?.Y ?? 0;
        set => SetRead(r => r with { Y = (int)Math.Round(value) });
    }

    public double ReadWidth
    {
        get => S.Region?.Width ?? 0;
        set => SetRead(r => r with { Width = Math.Max(20, (int)Math.Round(value)) });
    }

    public double ReadHeight
    {
        get => S.Region?.Height ?? 0;
        set => SetRead(r => r with { Height = Math.Max(10, (int)Math.Round(value)) });
    }

    /// <summary>A typed number changes the region; with no region yet there is nothing to change (pick one first).</summary>
    void SetRead(Func<RegionSettings, RegionSettings> change, [CallerMemberName] string? name = null)
    {
        if (S.Region is null) return;
        var next = change(S.Region);
        Set(S.Region, next, v => S.Region = v, name);
        Refresh();
    }

    /// <summary>A strip at the bottom of the primary monitor, for a custom box made before any region was picked.</summary>
    static RegionSettings DefaultRegion()
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return new RegionSettings(b.Left + b.Width / 5, b.Bottom - b.Height / 5, b.Width * 3 / 5, b.Height / 10);
    }

    public string TestText
    {
        get => _testText;
        private set => SetProperty(ref _testText, value);
    }

    public MessageKind TestKind
    {
        get => _testKind;
        private set => SetProperty(ref _testKind, value);
    }

    async Task TestReadingAsync()
    {
        if (S.Region is not { } region) return;
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        TestKind = MessageKind.Info;
        TestText = Loc.Instance["Regions.Testing"];
        ReadPreview? found;
        try
        {
            found = await _host.TestReadingAsync(region, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested) return;
        if (found is { Lines: > 0 } read)
        {
            TestKind = MessageKind.Success;
            TestText = Loc.F(read.Lines == 1 ? "Regions.TestFoundOne" : "Regions.TestFound", read.Lines, read.Language, read.Text);
        }
        else
        {
            TestKind = MessageKind.Warning;
            TestText = Loc.Instance["Regions.TestNothing"];
        }
    }

    // ---- where the translation goes ----

    public bool PlaceAbove
    {
        get => O.Position == SubtitlePosition.AboveRegion;
        set => Place(value, SubtitlePosition.AboveRegion);
    }

    public bool PlaceBelow
    {
        get => O.Position == SubtitlePosition.BelowRegion;
        set => Place(value, SubtitlePosition.BelowRegion);
    }

    public bool PlaceBottom
    {
        get => O.Position == SubtitlePosition.BottomCenter;
        set => Place(value, SubtitlePosition.BottomCenter);
    }

    public bool PlaceOver
    {
        get => O.Position == SubtitlePosition.OverRegion;
        set => Place(value, SubtitlePosition.OverRegion);
    }

    public bool PlaceCustom
    {
        get => O.Position == SubtitlePosition.Custom;
        set => Place(value, SubtitlePosition.Custom);
    }

    public bool IsCustom => O.Position == SubtitlePosition.Custom;

    void Place(bool chosen, SubtitlePosition position)
    {
        if (!chosen || O.Position == position) return;
        // A custom box starts where the translation is now, so choosing it moves nothing yet.
        if (position == SubtitlePosition.Custom && O.DisplayRegion is null)
        {
            var read = (S.Region ?? DefaultRegion()).ToRectangle();
            O.DisplayRegion = RegionSettings.From(RegionPreview.ShowBox(O, read, System.Windows.Forms.Screen.FromRectangle(read).Bounds));
        }
        Set(O.Position, position, v => O.Position = v, nameof(O.Position));
        Refresh();
    }

    public double CustomX
    {
        get => O.DisplayRegion?.X ?? 0;
        set => SetCustom(r => r with { X = (int)Math.Round(value) });
    }

    public double CustomY
    {
        get => O.DisplayRegion?.Y ?? 0;
        set => SetCustom(r => r with { Y = (int)Math.Round(value) });
    }

    public double CustomWidth
    {
        get => O.DisplayRegion?.Width ?? 0;
        set => SetCustom(r => r with { Width = Math.Max(40, (int)Math.Round(value)) });
    }

    public double CustomHeight
    {
        get => O.DisplayRegion?.Height ?? 0;
        set => SetCustom(r => r with { Height = Math.Max(20, (int)Math.Round(value)) });
    }

    void SetCustom(Func<RegionSettings, RegionSettings> change, [CallerMemberName] string? name = null)
    {
        if (O.DisplayRegion is null) return;
        var next = change(O.DisplayRegion);
        Set(O.DisplayRegion, next, v => O.DisplayRegion = v, name);
        Refresh();
    }

    public bool AlignLeft
    {
        get => O.Align == TextAlign.Left;
        set => SetAlign(value, TextAlign.Left);
    }

    public bool AlignCenter
    {
        get => O.Align == TextAlign.Center;
        set => SetAlign(value, TextAlign.Center);
    }

    public bool AlignRight
    {
        get => O.Align == TextAlign.Right;
        set => SetAlign(value, TextAlign.Right);
    }

    void SetAlign(bool chosen, TextAlign align)
    {
        if (!chosen) return;
        Set(O.Align, align, v => O.Align = v, nameof(O.Align));
        OnPropertyChanged(nameof(AlignLeft));
        OnPropertyChanged(nameof(AlignCenter));
        OnPropertyChanged(nameof(AlignRight));
        OnPropertyChanged(nameof(Preview));
    }

    /// <summary>Whether the translation's place leaves the read region free, and what that means in each display mode.</summary>
    public string OverlapText => OverlapOf(Working).Text;

    public MessageKind OverlapKind => OverlapOf(Working).Kind;

    /// <summary>The overlap message of these settings (also the Display &amp; sharing page's).</summary>
    public static (string Text, MessageKind Kind) OverlapOf(AppSettings settings)
    {
        if (settings.Subtitles.Region is not { } region) return (Loc.Instance["Regions.NoRegionYet"], MessageKind.Info);
        var o = settings.Overlay;
        bool covers = SubtitlePlacement.Covers(o.Position, o.DisplayRegion?.ToRectangle(), region.ToRectangle());
        bool shared = o.DisplayMode == DisplayMode.Shared && o.Share.Subtitles;
        if (!covers) return (Loc.Instance["Regions.ClearOfRead"], MessageKind.Success);
        return shared ? (Loc.Instance["Regions.CoversShared"], MessageKind.Warning) : (Loc.Instance["Regions.CoversPrivate"], MessageKind.Info);
    }

    // ---- the box behind the text ----

    public IReadOnlyList<ColorChoice> Swatches { get; }

    /// <summary>"#RRGGBB", or "" for no box.</summary>
    public string BackgroundColor
    {
        get => BackgroundColors.Normalize(O.BackgroundColor);
        set
        {
            Set(BackgroundColors.Normalize(O.BackgroundColor), BackgroundColors.Normalize(value), v => O.BackgroundColor = v);
            foreach (var swatch in Swatches) swatch.Refresh();
            OnPropertyChanged(nameof(HasBackground));
            OnPropertyChanged(nameof(BackgroundName));
            if (Array.IndexOf(BackgroundColors.Swatches, BackgroundColor) >= 0 || BackgroundColor.Length == 0)
            {
                _customColorText = "";
                OnPropertyChanged(nameof(CustomColorText));
            }
        }
    }

    /// <summary>A colour typed as "#RRGGBB"; it is taken once it is valid.</summary>
    public string CustomColorText
    {
        get => _customColorText;
        set
        {
            if (!SetProperty(ref _customColorText, value)) return;
            string color = BackgroundColors.Normalize(value);
            if (color.Length > 0) BackgroundColor = color;
        }
    }

    static string CustomColorOf(string color)
    {
        string c = BackgroundColors.Normalize(color);
        return c.Length > 0 && Array.IndexOf(BackgroundColors.Swatches, c) < 0 ? c : "";
    }

    public bool HasBackground => BackgroundColor.Length > 0;

    public string BackgroundName => HasBackground
        ? Loc.F("Region.BackgroundValue", BackgroundColors.Name(BackgroundColor), Math.Round(O.BackgroundOpacity * 100))
        : Loc.Instance["Region.TransparentDefault"];

    /// <summary>Percent, 0 to 100, in whole percents (a value saved before stays as it is until moved).</summary>
    public double BackgroundOpacity
    {
        get => Math.Round(O.BackgroundOpacity * 100);
        set
        {
            if (Math.Abs(value - Math.Round(O.BackgroundOpacity * 100)) < 0.5) return;
            Set(O.BackgroundOpacity, Math.Clamp(Math.Round(value), 0, 100) / 100, v => O.BackgroundOpacity = v);
            OnPropertyChanged(nameof(BackgroundName));
        }
    }

    public bool TextOutline
    {
        get => O.TextOutline;
        set => Set(O.TextOutline, value, v => O.TextOutline = v);
    }

    void Refresh()
    {
        foreach (var name in Derived) OnPropertyChanged(name);
        PickDisplayCommand.NotifyCanExecuteChanged();
        TestReadingCommand.NotifyCanExecuteChanged();
    }
}
