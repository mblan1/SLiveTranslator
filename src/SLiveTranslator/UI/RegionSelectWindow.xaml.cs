using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SLiveTranslator.Capture;
using SLiveTranslator.Models;
using SLiveTranslator.UI.Overlay;
using DRect = System.Drawing.Rectangle;
using DSize = System.Drawing.Size;
using Forms = System.Windows.Forms;

namespace SLiveTranslator.UI;

/// <summary>Which boxes the picker asks for.</summary>
public enum RegionPickMode
{
    /// <summary>The read region, then where the translation goes.</summary>
    Both,

    /// <summary>Only the read region (step 1 ends with Done).</summary>
    ReadOnly,

    /// <summary>Only where the translation goes, next to the read region in use (step 2 alone).</summary>
    DisplayOnly,
}

/// <summary>Where the translation goes and how its box looks, as picked in step 2.</summary>
/// <param name="Custom">The drawn box, for <see cref="SubtitlePosition.Custom"/> (physical pixels).</param>
public sealed record DisplayPick(SubtitlePosition Position, DRect? Custom, string BackgroundColor, double BackgroundOpacity, bool TextOutline);

/// <param name="Read">The read region (physical pixels).</param>
/// <param name="Display">Null when the display step was not part of the pick.</param>
public sealed record RegionPick(DRect Read, DisplayPick? Display);

/// <summary>What the OCR read in the box being drawn, for the picker's live check.</summary>
public sealed record ReadPreview(int Lines, string Language, string Text);

/// <summary>
/// The dimmed monitor where the user drags the read region (step 1) and then the box for the translation (step 2).
/// A box can be moved, resized by its handles or the arrow keys, and drawn again; step 1 reads the box as it changes
/// and says what it found, step 2 shows the translation in the box with the background picked beside it. The window
/// is hidden from screen capture, so that check reads the game under it. Results are in physical screen pixels.
/// </summary>
public partial class RegionSelectWindow : Window
{
    const double Grip = 7;
    const double HandleSize = 10;
    const double SnapDistance = 10;
    static readonly int[] OpacitySteps = [40, 60, 80, 100];
    static readonly SubtitlePosition[] PresetOrder =
        [SubtitlePosition.AboveRegion, SubtitlePosition.BelowRegion, SubtitlePosition.BottomCenter, SubtitlePosition.OverRegion, SubtitlePosition.Custom];

    enum Drag { None, New, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    readonly RegionPickMode _mode;
    readonly OverlaySettings _look;

    /// <summary>Shared on screen: the display box must keep off the read region (see <see cref="RegionRules"/>).</summary>
    readonly bool _avoid;
    readonly Func<DRect, CancellationToken, Task<ReadPreview?>>? _reader;
    readonly DispatcherTimer _readTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly List<(ToggleButton Button, string Color)> _swatches = new();
    readonly List<(ToggleButton Button, int Percent)> _opacities = new();
    readonly List<(ToggleButton Button, SubtitlePosition Position)> _presets = new();
    readonly Brush _accent;
    readonly Brush _amber;
    Forms.Screen _screen;
    CancellationTokenSource? _readCts;
    int _step = 1;
    Rect? _sel;
    Rect? _custom; // the box drawn in step 2, kept while a preset is tried
    DRect? _readPx;
    Drag _drag;
    Point _dragStart;
    Rect _dragFrom;
    SubtitlePosition _position;
    string _background;
    double _backgroundOpacity;
    bool _outline;
    bool _syncing;

    /// <summary>The last drag or key was stopped at the read region's edge (the status chip says so).</summary>
    bool _keptOff;

    /// <summary>A new box was started on the read region in Shared mode, which is not allowed (the chip says so).</summary>
    bool _startedOnRead;

    /// <summary>The read region alone (the first-run wizard and the tests).</summary>
    public RegionSelectWindow(Forms.Screen screen) : this(screen, RegionPickMode.ReadOnly, null, new OverlaySettings(), null)
    {
    }

    /// <param name="readPx">The read region in use: step 1 starts with it, and step 2 needs it in <see cref="RegionPickMode.DisplayOnly"/>.</param>
    /// <param name="look">The overlay settings in use: where the translation goes and its look, which step 2 starts from.</param>
    /// <param name="reader">Reads a box of the screen for the live check; null leaves the check out.</param>
    public RegionSelectWindow(Forms.Screen screen, RegionPickMode mode, DRect? readPx, OverlaySettings look,
        Func<DRect, CancellationToken, Task<ReadPreview?>>? reader)
    {
        InitializeComponent();
        _screen = screen;
        _mode = readPx is null && mode == RegionPickMode.DisplayOnly ? RegionPickMode.Both : mode;
        _look = look;
        _avoid = RegionRules.MustAvoid(look);
        _reader = reader;
        _readPx = readPx;
        _accent = (Brush)Resources["Accent"];
        _amber = (Brush)Resources["Amber"];
        _position = look.Position;
        _background = BackgroundColors.Normalize(look.BackgroundColor);
        _backgroundOpacity = Math.Clamp(look.BackgroundOpacity, 0.1, 1);
        _outline = look.TextOutline;
        BuildPanels();
        _readTimer.Tick += (_, _) => ReadSelection();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // The live check reads the game under this window.
            Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            PlaceOn(_screen);
        };
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Start();
        };
        SizeChanged += (_, _) => Layout();
        Closed += (_, _) =>
        {
            _readTimer.Stop();
            _readCts?.Cancel();
        };
    }

    /// <summary>The read region picked; null when cancelled.</summary>
    public DRect? Result => Pick?.Read;

    /// <summary>Everything picked; null when cancelled.</summary>
    public RegionPick? Pick { get; private set; }

    double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    Rect Area => new(0, 0, ActualWidth, ActualHeight);

    void PlaceOn(Forms.Screen screen)
    {
        _screen = screen;
        var b = screen.Bounds;
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_SHOWWINDOW);
    }

    void Start()
    {
        if (_mode == RegionPickMode.DisplayOnly) EnterDisplayStep();
        else
        {
            _sel = _readPx is { } read && _screen.Bounds.IntersectsWith(read) ? ToDip(read) : null;
            ShowStep();
            if (_sel is not null) ScheduleRead();
        }
    }

    // ---- the two steps ----

    void ShowStep()
    {
        bool display = _step == 2;
        Instruction.Text = Loc.Instance[display ? "Region.DisplayInstruction" : "Region.ReadInstruction"];
        InstructionHint.Text = Loc.Instance[display ? "Region.DisplayHint" : "Region.ReadHint"];
        NextButton.Content = Loc.Instance[display || _mode == RegionPickMode.ReadOnly ? "Region.Done" : "Region.Next"];
        RedoButton.Visibility = display ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = display && _mode == RegionPickMode.Both ? Visibility.Visible : Visibility.Collapsed;
        PresetRow.Visibility = display ? Visibility.Visible : Visibility.Collapsed;
        BackgroundPanel.Visibility = display ? Visibility.Visible : Visibility.Collapsed;
        ReadBox.Visibility = ReadTag.Visibility = display ? Visibility.Visible : Visibility.Collapsed;
        PreviewHost.Visibility = display ? Visibility.Visible : Visibility.Collapsed;
        KeysText.Text = Loc.Instance[display ? "Region.KeysDisplay" : "Region.Keys"];
        SelectionEdge.Stroke = SizeTag.Background = display ? _amber : _accent;
        if (_mode == RegionPickMode.DisplayOnly)
        {
            Step1Dot.Visibility = Step1Label.Visibility = Visibility.Collapsed;
        }
        Step1Dot.Background = display ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)) : _accent;
        Step1Mark.Text = display ? "✓" : "1";
        Step1Mark.Foreground = display ? new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)) : Brushes.Black;
        Step1Label.FontWeight = display ? FontWeights.Normal : FontWeights.SemiBold;
        Step1Label.Foreground = display ? (Brush)Resources["Dim2"] : Brushes.White;
        Step2Dot.Background = display ? _amber : Brushes.Transparent;
        Step2Dot.BorderThickness = new Thickness(display ? 0 : 1);
        Step2Mark.Foreground = display ? Brushes.Black : Brushes.White;
        Step2Label.FontWeight = display ? FontWeights.SemiBold : FontWeights.Normal;
        Step2Label.Foreground = display ? Brushes.White : (Brush)Resources["Dim2"];
        if (_mode == RegionPickMode.ReadOnly)
            Step2Dot.Visibility = Step2Label.Visibility = Visibility.Collapsed;
        ModeText.Text = Loc.F("Region.Mode", Loc.Instance["DisplayMode." + _look.DisplayMode]);
        SyncPanels();
        Layout();
    }

    void EnterDisplayStep()
    {
        _step = 2;
        _readTimer.Stop();
        _readCts?.Cancel();
        _custom = _look.DisplayRegion is { } custom && _screen.Bounds.IntersectsWith(custom.ToRectangle())
            ? ToDip(custom.ToRectangle())
            : null;
        if (_avoid && ReadDip is { } read)
        {
            // In Shared mode the box starts off the read region: "over the original" is not offered, and a saved box on
            // the region moves above it (below when there is no room).
            if (_position == SubtitlePosition.OverRegion) _position = SubtitlePosition.AboveRegion;
            if (_custom is { } box && Edges.Of(box).Overlaps(read))
                _custom = RegionRules.AboveOrBelow(Edges.Of(box), read, Edges.Of(Area), RegionRules.Gap / Scale)?.Box.ToRect();
        }
        _keptOff = _startedOnRead = false;
        _sel = PresetBox(_position);
        ShowStep();
    }

    /// <summary>The read region in this window's DIPs, in step 2.</summary>
    Edges? ReadDip => _step == 2 && _readPx is { } read ? Edges.Of(ToDip(read)) : null;

    void OnNext(object sender, RoutedEventArgs e) => Next();

    /// <summary>Enter, for the self-test: step 1 goes on to step 2 (or ends), step 2 ends.</summary>
    internal void Confirm() => Next();

    void Next()
    {
        if (_sel is not { } sel) return;
        var px = ToPx(sel);
        if (px.Width < 20 || px.Height < 10) return;
        if (_step == 1)
        {
            _readPx = px;
            if (_mode == RegionPickMode.ReadOnly)
            {
                Finish(new RegionPick(px, null));
                return;
            }
            EnterDisplayStep();
            return;
        }
        Finish(DisplayPickOf(px));
    }

    /// <summary>Step 2's result for the box at <paramref name="px"/>.</summary>
    RegionPick DisplayPickOf(DRect px)
    {
        // DIPs rounded to pixels can land one pixel on the region: the saved box keeps off it too.
        if (_avoid && _position == SubtitlePosition.Custom && _readPx is { } readPx && Edges.Of(px).Overlaps(Edges.Of(readPx)))
            px = RegionRules.PushOut(Edges.Of(px), Edges.Of(readPx), Edges.Of(_screen.Bounds)).ToDRect();
        var display = new DisplayPick(_position, _position == SubtitlePosition.Custom ? px : null, _background, _backgroundOpacity, _outline);
        return new RegionPick(_readPx!.Value, display);
    }

    /// <summary>The self-test: what Done would give now in step 2 (a window not shown as a dialog cannot end with a result).</summary>
    internal RegionPick? PendingPick => _step == 2 && _sel is { } sel && _readPx is not null ? DisplayPickOf(ToPx(sel)) : null;

    /// <summary>The self-test: whether "over the original" can be picked.</summary>
    internal bool OverPresetEnabled => _presets.First(p => p.Position == SubtitlePosition.OverRegion).Button.IsEnabled;

    void Finish(RegionPick pick)
    {
        Pick = pick;
        DialogResult = true;
    }

    void OnBack(object sender, RoutedEventArgs e) => Back();

    void Back()
    {
        if (_step != 2 || _mode != RegionPickMode.Both) return;
        _step = 1;
        _sel = _readPx is { } read ? ToDip(read) : null;
        ShowStep();
        ScheduleRead();
    }

    void OnRedo(object sender, RoutedEventArgs e)
    {
        _sel = null;
        StatusChip.Visibility = Visibility.Collapsed;
        Layout();
        Focus();
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    // ---- step 2: presets and the background ----

    void BuildPanels()
    {
        foreach (var position in PresetOrder)
        {
            var button = new ToggleButton { Content = Loc.Instance["Region.Preset." + position], Margin = new Thickness(0, 0, 2, 0) };
            if (_avoid && position == SubtitlePosition.OverRegion)
            {
                // Shared on screen: the app's own OCR would read the translation over the original.
                button.IsEnabled = false;
                button.ToolTip = Loc.Instance["Region.OverOffShared"];
                ToolTipService.SetShowOnDisabled(button, true);
            }
            button.Click += (_, _) => PickPreset(position);
            _presets.Add((button, position));
            Presets.Children.Add(button);
        }
        foreach (var color in new[] { BackgroundColors.Transparent }.Concat(BackgroundColors.Swatches))
        {
            var button = new ToggleButton
            {
                Style = (Style)Resources["Swatch"],
                Background = color.Length == 0 ? (Brush)Resources["Checker"] : new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                ToolTip = BackgroundColors.Name(color),
            };
            AutomationName(button, (string)button.ToolTip);
            button.Click += (_, _) =>
            {
                _background = color;
                SyncPanels();
                Layout();
            };
            _swatches.Add((button, color));
            Swatches.Children.Add(button);
        }
        foreach (int percent in OpacitySteps)
        {
            var button = new ToggleButton { Content = Loc.F("Region.OpacityValue", percent), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 4, 0) };
            button.Click += (_, _) =>
            {
                _backgroundOpacity = percent / 100.0;
                SyncPanels();
                Layout();
            };
            _opacities.Add((button, percent));
            Opacities.Children.Add(button);
        }
    }

    static void AutomationName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    void SyncPanels()
    {
        _syncing = true;
        foreach (var (button, position) in _presets) button.IsChecked = position == _position;
        foreach (var (button, color) in _swatches) button.IsChecked = color == _background;
        bool boxed = _background.Length > 0;
        foreach (var (button, percent) in _opacities)
        {
            button.IsEnabled = boxed;
            button.IsChecked = boxed && Math.Abs(percent / 100.0 - _backgroundOpacity) < 0.051;
        }
        OutlineToggle.IsChecked = _outline;
        BackgroundName.Text = boxed
            ? Loc.F("Region.BackgroundValue", BackgroundColors.Name(_background), Math.Round(_backgroundOpacity * 100))
            : Loc.Instance["Region.TransparentDefault"];
        _syncing = false;
    }

    void OnOutlineChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _outline = OutlineToggle.IsChecked == true;
        Layout();
    }

    void PickPreset(SubtitlePosition position)
    {
        if (_avoid && position == SubtitlePosition.OverRegion) return;
        _position = position;
        _keptOff = _startedOnRead = false;
        _sel = PresetBox(position);
        SyncPanels();
        Layout();
        Focus();
    }

    /// <summary>Where the overlay would put two lines of translation for a preset, in this window's DIPs.</summary>
    Rect PresetBox(SubtitlePosition position)
    {
        if (position == SubtitlePosition.Custom && _custom is { } custom) return custom;
        var read = _readPx ?? new DRect(_screen.Bounds.Left + _screen.Bounds.Width / 5, _screen.Bounds.Bottom - _screen.Bounds.Height / 5,
            _screen.Bounds.Width * 3 / 5, _screen.Bounds.Height / 10);
        // Where the overlay would really put it: in Shared mode a preset landing on the region (bottom centre) gives way.
        var style = SubtitleStyle.From(_look) with { Position = position == SubtitlePosition.Custom ? SubtitlePosition.AboveRegion : position, AvoidRegion = _avoid };
        double scale = Scale;
        int width = SubtitlePlacement.MaxWidthPx(style, read, _screen.Bounds, scale);
        int height = (int)Math.Round((Math.Clamp(_look.FontSize, 10, 80) * 1.36 * 2 + 22) * scale);
        var box = SubtitlePlacement.Place(style, read, new DSize(width, height), _screen.Bounds, (int)Math.Round(6 * scale));
        return ToDip(box);
    }

    void OnPanelMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // a click on a panel draws nothing

    // ---- the mouse ----

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        var p = e.GetPosition(Root);
        var hit = HitTest(p);
        if (e.ClickCount == 2 && hit == Drag.Move)
        {
            Next();
            return;
        }
        // Shared mode: a new box cannot start on the read region; the chip says to start beside it.
        _startedOnRead = hit == Drag.None && _avoid && ReadDip is { } read && read.Contains(p.X, p.Y);
        if (_startedOnRead)
        {
            Layout();
            return;
        }
        _keptOff = false;
        _drag = hit == Drag.None ? Drag.New : hit;
        _dragStart = p;
        _dragFrom = _sel ?? new Rect(p, p);
        if (_drag == Drag.New) _sel = new Rect(p, p);
        CaptureMouse();
        Layout();
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Root);
        if (_drag == Drag.None)
        {
            Cursor = CursorFor(HitTest(p));
            return;
        }
        _sel = KeptOffRead(Dragged(p), p);
        Layout();
    }

    /// <summary>
    /// Shared mode, step 2: the box being drawn, moved or resized stops at the read region's edge, on the side it came
    /// from (see <see cref="RegionRules"/>). Elsewhere the box is as dragged.
    /// </summary>
    Rect KeptOffRead(Rect next, Point p)
    {
        if (!_avoid || ReadDip is not { } read) return next;
        var from = Edges.Of(_sel ?? _dragFrom);
        var wanted = Edges.Of(next);
        var kept = _drag switch
        {
            Drag.Move => RegionRules.ClampMove(wanted, from, read, Edges.Of(Area)),
            // A new box grows from where it was started, towards the pointer.
            Drag.New => RegionRules.ClampResize(wanted, new Edges(_dragStart.X, _dragStart.Y, _dragStart.X, _dragStart.Y), read,
                (p.X >= _dragStart.X ? Sides.Right : Sides.Left) | (p.Y >= _dragStart.Y ? Sides.Bottom : Sides.Top)),
            _ => RegionRules.ClampResize(wanted, from, read, SidesOf(_drag)),
        };
        _keptOff = kept != wanted;
        return kept.ToRect();
    }

    static Sides SidesOf(Drag drag) => drag switch
    {
        Drag.Left => Sides.Left,
        Drag.Right => Sides.Right,
        Drag.Top => Sides.Top,
        Drag.Bottom => Sides.Bottom,
        Drag.TopLeft => Sides.Top | Sides.Left,
        Drag.TopRight => Sides.Top | Sides.Right,
        Drag.BottomLeft => Sides.Bottom | Sides.Left,
        Drag.BottomRight => Sides.Bottom | Sides.Right,
        _ => Sides.None,
    };

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        ReleaseMouseCapture();
        var drag = _drag;
        _drag = Drag.None;
        if (_sel is { } sel && (sel.Width * Scale < 20 || sel.Height * Scale < 10))
            _sel = drag == Drag.New && _dragFrom.Width * Scale >= 20 ? _dragFrom : null; // a click alone keeps the box there was
        Changed();
    }

    /// <summary>The box was drawn, moved or resized by the user.</summary>
    void Changed()
    {
        if (_step == 2 && _sel is { } sel)
        {
            _position = SubtitlePosition.Custom;
            _custom = sel;
            SyncPanels();
        }
        Layout();
        if (_step == 1) ScheduleRead();
    }

    Rect Dragged(Point p)
    {
        var area = Area;
        double x = Math.Clamp(p.X, 0, area.Width);
        double y = Math.Clamp(p.Y, 0, area.Height);
        if (_step == 2 && _readPx is { } readPx && _drag is Drag.New or Drag.Left or Drag.Right or Drag.TopLeft or Drag.TopRight or Drag.BottomLeft or Drag.BottomRight)
        {
            var read = ToDip(readPx);
            if (Math.Abs(x - read.Left) < SnapDistance) x = read.Left;
            else if (Math.Abs(x - read.Right) < SnapDistance) x = read.Right;
        }
        var r = _dragFrom;
        double dx = x - _dragStart.X, dy = y - _dragStart.Y;
        switch (_drag)
        {
            case Drag.New:
                return new Rect(_dragStart, new Point(x, y));
            case Drag.Move:
                return new Rect(Math.Clamp(r.X + dx, 0, Math.Max(0, area.Width - r.Width)), Math.Clamp(r.Y + dy, 0, Math.Max(0, area.Height - r.Height)), r.Width, r.Height);
        }
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if (_drag is Drag.Left or Drag.TopLeft or Drag.BottomLeft) left = Math.Min(x, right - 4);
        if (_drag is Drag.Right or Drag.TopRight or Drag.BottomRight) right = Math.Max(x, left + 4);
        if (_drag is Drag.Top or Drag.TopLeft or Drag.TopRight) top = Math.Min(y, bottom - 4);
        if (_drag is Drag.Bottom or Drag.BottomLeft or Drag.BottomRight) bottom = Math.Max(y, top + 4);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    Drag HitTest(Point p)
    {
        if (_sel is not { } r) return Drag.None;
        bool nearLeft = Math.Abs(p.X - r.Left) <= Grip, nearRight = Math.Abs(p.X - r.Right) <= Grip;
        bool nearTop = Math.Abs(p.Y - r.Top) <= Grip, nearBottom = Math.Abs(p.Y - r.Bottom) <= Grip;
        bool inX = p.X >= r.Left - Grip && p.X <= r.Right + Grip, inY = p.Y >= r.Top - Grip && p.Y <= r.Bottom + Grip;
        if (nearLeft && nearTop) return Drag.TopLeft;
        if (nearRight && nearTop) return Drag.TopRight;
        if (nearLeft && nearBottom) return Drag.BottomLeft;
        if (nearRight && nearBottom) return Drag.BottomRight;
        if (nearLeft && inY) return Drag.Left;
        if (nearRight && inY) return Drag.Right;
        if (nearTop && inX) return Drag.Top;
        if (nearBottom && inX) return Drag.Bottom;
        return r.Contains(p) ? Drag.Move : Drag.None;
    }

    static Cursor CursorFor(Drag drag) => drag switch
    {
        Drag.Move => Cursors.SizeAll,
        Drag.Left or Drag.Right => Cursors.SizeWE,
        Drag.Top or Drag.Bottom => Cursors.SizeNS,
        Drag.TopLeft or Drag.BottomRight => Cursors.SizeNWSE,
        Drag.TopRight or Drag.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Cross,
    };

    // ---- the keyboard ----

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                DialogResult = false;
                break;
            case Key.Enter:
                Next();
                break;
            case Key.Back:
                Back();
                break;
            case Key.Tab when _step == 1:
                NextMonitor();
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when _sel is { } sel:
                double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1;
                double dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
                double dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
                bool resize = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                var next = resize
                    ? new Rect(sel.X, sel.Y, Math.Max(4, sel.Width + dx), Math.Max(4, sel.Height + dy))
                    : new Rect(Math.Clamp(sel.X + dx, 0, Math.Max(0, ActualWidth - sel.Width)), Math.Clamp(sel.Y + dy, 0, Math.Max(0, ActualHeight - sel.Height)), sel.Width, sel.Height);
                _keptOff = false;
                if (_avoid && ReadDip is { } read)
                {
                    // Shift+arrows move the right and bottom edges; arrows move the whole box.
                    var kept = resize
                        ? RegionRules.ClampResize(Edges.Of(next), Edges.Of(sel), read, Sides.Right | Sides.Bottom)
                        : RegionRules.ClampMove(Edges.Of(next), Edges.Of(sel), read, Edges.Of(Area));
                    _keptOff = kept != Edges.Of(next);
                    next = kept.ToRect();
                }
                _sel = next;
                Changed();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void NextMonitor()
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length < 2) return;
        int index = Array.FindIndex(screens, s => s.DeviceName == _screen.DeviceName);
        _sel = null;
        StatusChip.Visibility = Visibility.Collapsed;
        PlaceOn(screens[(index + 1) % screens.Length]);
        Dispatcher.BeginInvoke(Layout, DispatcherPriority.Loaded);
    }

    // ---- step 1: the live check ----

    void ScheduleRead()
    {
        if (_reader is null || _step != 1 || _sel is null) return;
        _readTimer.Stop();
        _readTimer.Start();
        ShowStatus(Loc.Instance["Region.Reading"], StatusTone.Neutral);
    }

    async void ReadSelection()
    {
        _readTimer.Stop();
        if (_reader is null || _step != 1 || _sel is not { } sel) return;
        _readCts?.Cancel();
        var cts = _readCts = new CancellationTokenSource();
        ReadPreview? found;
        try
        {
            found = await _reader(ToPx(sel), cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("region picker: reading failed: " + ex.Message);
            found = null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || _step != 1) return;
        if (found is { Lines: > 0 } read)
            ShowStatus(Loc.F(read.Lines == 1 ? "Region.ReadsOne" : "Region.ReadsMany", read.Lines, read.Language), StatusTone.Good);
        else
            ShowStatus(Loc.Instance["Region.ReadsNothing"], StatusTone.Caution);
    }

    enum StatusTone { Neutral, Good, Caution }

    void ShowStatus(string text, StatusTone tone)
    {
        StatusText.Text = text;
        StatusDot.Fill = tone switch
        {
            StatusTone.Good => new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)),
            StatusTone.Caution => new SolidColorBrush(Color.FromRgb(0xFC, 0xE1, 0x00)),
            _ => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
        };
        StatusChip.Visibility = Visibility.Visible;
        Layout();
    }

    // ---- drawing ----

    void Layout()
    {
        if (!IsLoaded) return;
        var area = Area;
        double w = area.Width, h = area.Height;

        Toolbar.MaxWidth = Math.Max(200, w - 48);
        Toolbar.Measure(new Size(Toolbar.MaxWidth, double.PositiveInfinity));
        Canvas.SetLeft(Toolbar, Math.Max(24, (w - Toolbar.DesiredSize.Width) / 2));
        Canvas.SetTop(Toolbar, 24);
        double toolbarBottom = 24 + Toolbar.DesiredSize.Height;

        KeysBox.Measure(new Size(w, h));
        Canvas.SetLeft(KeysBox, (w - KeysBox.DesiredSize.Width) / 2);
        Canvas.SetTop(KeysBox, h - KeysBox.DesiredSize.Height - 16);

        if (_step == 2 && _readPx is { } readPx)
        {
            var read = ToDip(readPx);
            Place(ReadBox, read);
            ReadTag.Measure(new Size(w, h));
            Canvas.SetLeft(ReadTag, read.Left);
            Canvas.SetTop(ReadTag, read.Bottom + 6 + ReadTag.DesiredSize.Height <= h ? read.Bottom + 6 : read.Top - ReadTag.DesiredSize.Height - 6);
        }

        if (_sel is not { } sel)
        {
            Dim.Data = new RectangleGeometry(area);
            Selection.Visibility = SizeTag.Visibility = StatusChip.Visibility = Visibility.Collapsed;
            return;
        }

        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(area), new RectangleGeometry(sel));
        Selection.Visibility = Visibility.Visible;
        Place(Selection, sel);
        DrawHandles(sel);

        if (_step == 2)
        {
            SelectionFill.Background = OverlayLook.SubtitleBackgroundAt(_background, _backgroundOpacity);
            PreviewText.Text = Loc.Instance["Overlay.Sample2"] + "\n" + Loc.Instance["Overlay.Sample3"];
            PreviewText.FontSize = Math.Clamp(_look.FontSize, 10, 80);
            PreviewText.TextAlignment = _look.Align switch { TextAlign.Left => TextAlignment.Left, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Center };
            PreviewText.Effect = _outline ? OverlayLook.OutlineHalo : null;
            PreviewHost.Effect = _outline ? OverlayLook.OutlineDrop : null;
            ShowDisplayStatus();
        }
        else
        {
            SelectionFill.Background = Brushes.Transparent;
        }

        var px = ToPx(sel);
        SizeText.Text = Loc.F(_step == 2 ? "Region.SizeDisplay" : "Region.SizeRead", px.Width, px.Height);
        SizeTag.Visibility = Visibility.Visible;
        SizeTag.Measure(new Size(w, h));
        StatusChip.Measure(new Size(w, h));
        double tagTop = sel.Top - SizeTag.DesiredSize.Height - 8;
        bool tagBelow = tagTop < toolbarBottom + 4;
        if (tagBelow) tagTop = sel.Bottom + 8;
        Canvas.SetLeft(SizeTag, Math.Clamp(sel.Left, 0, Math.Max(0, w - SizeTag.DesiredSize.Width)));
        Canvas.SetTop(SizeTag, tagTop);
        if (StatusChip.Visibility == Visibility.Visible)
        {
            double chipW = StatusChip.DesiredSize.Width;
            bool sameRow = sel.Width >= SizeTag.DesiredSize.Width + chipW + 16;
            double chipLeft = sameRow ? sel.Right - chipW : sel.Left;
            double chipTop = sameRow ? tagTop : (tagBelow ? tagTop + SizeTag.DesiredSize.Height + 6 : sel.Bottom + 8);
            Canvas.SetLeft(StatusChip, Math.Clamp(chipLeft, 0, Math.Max(0, w - chipW)));
            Canvas.SetTop(StatusChip, Math.Clamp(chipTop, 0, Math.Max(0, h - StatusChip.DesiredSize.Height)));
        }

        if (_step == 2)
        {
            BackgroundPanel.Measure(new Size(w, h));
            double pw = BackgroundPanel.DesiredSize.Width, ph = BackgroundPanel.DesiredSize.Height;
            double left = sel.Right + 16 + pw <= w ? sel.Right + 16 : sel.Left - 16 - pw >= 0 ? sel.Left - 16 - pw : w - pw - 24;
            double top = Math.Clamp(sel.Top, toolbarBottom + 12, Math.Max(toolbarBottom + 12, h - ph - 48));
            Canvas.SetLeft(BackgroundPanel, left);
            Canvas.SetTop(BackgroundPanel, top);
        }
    }

    void ShowDisplayStatus()
    {
        if (_readPx is not { } read || _sel is not { } sel) return;
        var px = ToPx(sel);
        bool covers = _position == SubtitlePosition.OverRegion || Edges.Of(px).Overlaps(Edges.Of(read));
        string text;
        Color dot;
        if (_avoid && _startedOnRead)
            (text, dot) = (Loc.Instance["Region.StartOutside"], Color.FromRgb(0xFC, 0xE1, 0x00));
        else if (_avoid && _keptOff)
            (text, dot) = (Loc.Instance["Region.KeptOff"], Color.FromRgb(0xFC, 0xE1, 0x00));
        else if (!covers)
            (text, dot) = (Loc.Instance["Region.ClearOfRead"], Color.FromRgb(0x6C, 0xCB, 0x5F));
        else
            (text, dot) = _avoid ? (Loc.Instance["Region.CoversShared"], Color.FromRgb(0xFC, 0xE1, 0x00))
                : (Loc.Instance["Region.CoversPrivate"], Color.FromRgb(0x60, 0xCD, 0xFF));
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(dot);
        StatusChip.Visibility = Visibility.Visible;
    }

    void DrawHandles(Rect sel)
    {
        if (Handles.Children.Count == 0)
            for (int i = 0; i < 8; i++)
                Handles.Children.Add(new Rectangle { Width = HandleSize, Height = HandleSize, Fill = Brushes.White, StrokeThickness = 2, RadiusX = 2, RadiusY = 2 });
        var points = new[]
        {
            new Point(0, 0), new Point(sel.Width / 2, 0), new Point(sel.Width, 0), new Point(0, sel.Height / 2),
            new Point(sel.Width, sel.Height / 2), new Point(0, sel.Height), new Point(sel.Width / 2, sel.Height), new Point(sel.Width, sel.Height),
        };
        for (int i = 0; i < 8; i++)
        {
            var handle = (Rectangle)Handles.Children[i];
            handle.Stroke = _step == 2 ? _amber : _accent;
            Canvas.SetLeft(handle, points[i].X - HandleSize / 2);
            Canvas.SetTop(handle, points[i].Y - HandleSize / 2);
        }
    }

    static void Place(FrameworkElement element, Rect r)
    {
        Canvas.SetLeft(element, r.X);
        Canvas.SetTop(element, r.Y);
        element.Width = Math.Max(0, r.Width);
        element.Height = Math.Max(0, r.Height);
    }

    DRect ToPx(Rect r)
    {
        double scale = Scale;
        return new DRect(
            (int)Math.Round(_screen.Bounds.Left + r.X * scale),
            (int)Math.Round(_screen.Bounds.Top + r.Y * scale),
            (int)Math.Round(r.Width * scale),
            (int)Math.Round(r.Height * scale));
    }

    Rect ToDip(DRect px)
    {
        double scale = Scale;
        return new Rect((px.Left - _screen.Bounds.Left) / scale, (px.Top - _screen.Bounds.Top) / scale, px.Width / scale, px.Height / scale);
    }
}
