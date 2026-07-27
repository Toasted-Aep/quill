using Quill.Helpers;
using Quill.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
// ImplicitUsings pulls in System.IO, whose Path would otherwise collide with the shape.
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Quill.Controls;

/// <summary>
/// The radial tool dial — an opt-in alternative projection of the very same
/// tool state the linear pen row shows (docs/CONCEPTS-DIRECTION.md §2.2-§2.4,
/// extended to the three-zone reference design).
///
/// Three concentric zones:
///   OUTER RING  ten user-assignable slots (pens, tools and commands), each
///               carrying its icon and — for pens — its size number. The active
///               slot is highlighted and shows a live stroke preview.
///   MIDDLE RING three arc sliders that tile the full circle with no overlap:
///               Size 160°→30°, Opacity 30°→-90°, Smoothness -90°→160°
///               (0° at the RIGHT, sweeping clockwise). An arc that does not
///               apply to the current tool is greyed out, never hidden.
///   INNER DISC  one colour button showing the current colour (white when the
///               active tool has none). Tapping it opens the colour picker.
///
/// Self-contained by design: the whole control attaches itself to the canvas
/// host and reaches back into MainWindow only through <see cref="Host"/>, a bag
/// of small delegates. Nothing here owns tool state; every commit goes through
/// the host's ApplyPreset/SelectTool, which is what keeps the radial and linear
/// surfaces from ever diverging.
///
/// Rendering is code-built <see cref="Path"/> arc segments (never Win2D, never
/// inside InkSurface) so the ink renderer pays nothing for it. All pointer
/// logic is one atan2 + radius test against cached geometry — the arcs
/// themselves are IsHitTestVisible=false and are never hit-tested individually.
/// </summary>
public sealed class ToolWheel
{
    // ===================================================================
    // Geometry — DIPs at scale 1.0. Angles are MATH convention throughout:
    // 0° at the right, counter-clockwise positive, y flipped for the screen.
    // ===================================================================
    private const double ColourR = 34;    // inner colour button
    private const double DeadR = 24;      // dead zone: nothing arms inside it (pen-jitter armour)
    private const double MidIn = 40;      // middle ring: the three arc sliders
    private const double MidOut = 88;
    private const double OutIn = 92;      // outer ring: the ten slots
    private const double OutOut = 136;
    private const double SatR = 152;      // satellite orbit (drag grip)
    private const double Footprint = 280;
    private const double ConfirmR = 176;  // drag past this and the aimed slot fires without pen-up
    private const double IconR = 106;     // slot icon centre
    private const double NumR = 126;      // slot size-number centre
    private const double ReadR = 64;      // arc icon + readout centre

    public const int Slots = 10;          // exactly ten, user-assignable
    private const double SlotSpan = 360.0 / Slots;   // 36°
    private const double Seam = 1.0;      // hairline gap so adjacent wedges read as separate

    // The three arc sliders, as (start, end) sweeping CLOCKWISE. They tile 360°
    // exactly: 130 + 120 + 110. Start is the minimum, end the maximum, so a
    // clockwise drag always increases the value.
    private const double SizeA0 = 160, SizeA1 = 30;      // span 130
    private const double OpacA0 = 30, OpacA1 = -90;      // span 120
    private const double SmoothA0 = 270, SmoothA1 = 160; // span 110

    private const double TapSlop = 8;     // "stationary" for the summon hold and the tap-vs-drag split
    private const int HoldMs = 400;       // canvas press-and-hold before the dial summons
    private const int TapMs = 350;        // press shorter than this + no drag = sticky open
    private const int AssignMs = 550;     // press-hold on a slot opens its assignment flyout

    // Default anchor: lower-left of the canvas (fractional). Left-handers drag
    // the dial across the centreline; only the satellites mirror.
    private const double AnchorFx = 0.12, AnchorFy = 0.78;

    private enum Zone { None, Colour, Arc, Slot }

    /// <summary>Everything the dial needs from MainWindow, as delegates, so it
    /// never takes a dependency on the window itself.</summary>
    public sealed class Host
    {
        public required Func<Library> Library { get; init; }
        public required Func<Guid?> ActivePreset { get; init; }
        public required Func<string> ToolTag { get; init; }
        public required Action<PenPreset> ApplyPreset { get; init; }
        public required Action<string> SelectTool { get; init; }
        /// <summary>PenChipData: the two-tone chip geometry for a pen type.</summary>
        public required Func<PenType, (string Body, string Colour)> ChipData { get; init; }
        /// <summary>BuildTwoToneChip: the exact visual the linear row draws, so a
        /// pen looks identical in both surfaces.</summary>
        public required Func<string, string, Color, FrameworkElement> TwoTone { get; init; }
        /// <summary>SetMouseMode, so the options-menu radios stay in step.</summary>
        public required Action<string> SetMouseMode { get; init; }
        public required Func<bool> ReduceMotion { get; init; }
        public required Action Save { get; init; }
    }

    /// <summary>Workstream A's system colour picker plugs in here. The signature
    /// is a deliberate 1:1 shim over ColorPickerService.Open(Point rootPoint,
    /// Color current, Action&lt;Color&gt; onChanged, Action? onClosed) — that type
    /// lives on the colour-system branch, not this one, so the dial calls THROUGH
    /// this hook rather than referencing the service directly and both branches
    /// compile standalone. The host wires it to ColorPickerService.Open once the
    /// two branches merge; until then MainWindow supplies a simple inline picker,
    /// so nothing is blocked. rootPoint is the colour disc's position in the
    /// XamlRoot, i.e. where the picker should anchor.</summary>
    public Action<Point, Color, Action<Color>, Action?>? ColourPickerHook { get; set; }

    // Slot ids are plain strings so the persisted list stays human-readable and
    // forward-compatible: "pen:<guid>" | "tool:<tag>" | "cmd:<name>" | "" (empty).
    private const string KindPen = "pen:", KindTool = "tool:", KindCmd = "cmd:";

    private static readonly string[] ToolKinds = { "Eraser", "Select", "Text", "FreeSpace" };
    private static readonly string[] CmdKinds = { "Undo", "Redo", "MouseMode" };

    private readonly Grid _host;
    private readonly InkSurface _surface;
    private readonly Host _h;

    private readonly Grid _layer;        // overlay; Background stays null so ink still gets the pointer
    private readonly Rectangle _scrim;   // swallows canvas input (and taps-outside) only while open
    private readonly Canvas _wheel;
    private readonly Border _chip;       // the persistent summon chip at the anchor
    private readonly Grid _chipArt;

    private readonly Ellipse _body = new();
    private readonly Path[] _slotArc = new Path[Slots];
    private readonly Grid[] _slotArt = new Grid[Slots];
    private readonly TextBlock[] _slotNum = new TextBlock[Slots];
    private readonly Path[] _arcSeg = new Path[3];
    private readonly Path[] _arcFill = new Path[3];
    private readonly Grid[] _arcIcon = new Grid[3];
    private readonly TextBlock[] _arcText = new TextBlock[3];
    private readonly Ellipse _colour = new();
    private readonly Path _grip = new();

    private bool _on;
    private bool _open;
    // Docked: the wheel lives permanently in the top-left corner (the Concepts
    // arrangement) instead of being summoned at the pointer. While docked it is
    // NOT modal - it never shows the scrim, never closes on an outside press,
    // and a press beyond its rim is left entirely to the canvas so drawing,
    // lasso and the barrel menu are untouched.
    private bool _docked = true;
    private bool _sticky;                // opened by a tap: stays up until the next tap
    private bool _mirrored;              // handedness: satellites flip on the right half
    private double _scale = 1;
    private Point _centre;
    private int _hoverSlot = -1;
    private int _hoverArc = -1;
    private int _dragArc = -1;           // an arc slider currently being scrubbed
    private bool _hoverColour;
    private uint? _pointer;
    private Point _pressPt;
    private long _pressMs;
    private bool _dragArm;               // opened from a press: release commits
    private bool _fromChip;

    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(HoldMs) };
    private readonly DispatcherTimer _assign = new() { Interval = TimeSpan.FromMilliseconds(AssignMs) };
    private Point _holdPt;
    private bool _holdArmed;
    private int _assignSlot = -1;
    private UIElement? _keyTarget;

    // ===================================================================
    // Attach
    // ===================================================================
    public static ToolWheel Attach(Grid host, InkSurface surface, Host h) => new(host, surface, h);

    private ToolWheel(Grid host, InkSurface surface, Host h)
    {
        _host = host; _surface = surface; _h = h;

        _layer = new Grid { Visibility = Visibility.Collapsed };
        Canvas.SetZIndex(_layer, 60);

        _scrim = new Rectangle { Fill = new SolidColorBrush(Colors.Transparent), Visibility = Visibility.Collapsed };
        _layer.Children.Add(_scrim);

        _wheel = new Canvas
        {
            Width = Footprint, Height = Footprint, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed, Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform()
        };
        BuildWheel();
        _layer.Children.Add(_wheel);

        _chipArt = new Grid { Width = 26, Height = 26, IsHitTestVisible = false };
        _chip = new Border
        {
            Width = 50, Height = 50, CornerRadius = new CornerRadius(25), BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = _chipArt
        };
        ToolTipService.SetToolTip(_chip, Loc.T("Wheel.Chip.Tip"));
        _layer.Children.Add(_chip);

        _host.Children.Add(_layer);
        // the dock is corner-anchored, so it has to be re-parked on every resize
        _host.SizeChanged += (_, _) => { if (_on && _docked) { PlaceDocked(); PlaceChip(); } };

        // One root handler set, registered handledEventsToo so a press the ink
        // surface has already claimed is still *observed* (and then ignored).
        // Summoning only ever happens on a press the surface left unhandled —
        // that is what makes the canvas hold incapable of shadowing drawing, the
        // barrel lasso, the right-button menu or any selection drag.
        _host.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        _host.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        _host.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        _host.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnLost), true);
        _host.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnLost), true);

        _hold.Tick += (_, _) => { _hold.Stop(); if (_holdArmed && !_docked) { _holdArmed = false; Open(_holdPt, dragArm: true, fromChip: false); } };
        _assign.Tick += (_, _) => { _assign.Stop(); if (_assignSlot >= 0) ShowAssign(_assignSlot); };
        _host.SizeChanged += (_, _) => { if (!_open) PlaceChip(); };
        // Every colour here is captured at paint time, so a theme flip has to
        // repaint the dial the same way it rebuilds the pen strip.
        _host.ActualThemeChanged += (_, _) => Refresh();
        _host.Loaded += (_, _) => { PlaceChip(); HookKeys(); };
        if (_host.IsLoaded) { PlaceChip(); HookKeys(); }
    }

    // Escape has to work wherever focus happens to be, so the handler rides the
    // window's root content and only claims the key while the dial is up.
    private void HookKeys()
    {
        if (_keyTarget != null) return;
        if (_host.XamlRoot?.Content is not UIElement top) return;
        _keyTarget = top;
        top.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_open) return;
        if (e.Key == Windows.System.VirtualKey.Escape) { Close(); e.Handled = true; return; }
        // 1-9 then 0 fire the slots clockwise from N — the keyboard / assistive path.
        int n = e.Key == Windows.System.VirtualKey.Number0 ? 9 : (int)e.Key - (int)Windows.System.VirtualKey.Number1;
        if (n is >= 0 and < Slots) { Commit(n); e.Handled = true; }
    }

    // ===================================================================
    // Public API — the whole surface MainWindow talks to
    // ===================================================================

    /// <summary>Show or hide the dial. Takes effect immediately, both ways.</summary>
    public void SetVisible(bool on)
    {
        if (_on == on) { if (on) { PlaceDocked(); Refresh(); } return; }
        _on = on;
        if (!on) { _docked = false; Close(); }
        _layer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            _docked = true;
            PlaceChip();
            _chip.Visibility = Visibility.Collapsed;   // the dock replaces the summon chip
            OpenDocked();
        }
    }

    /// <summary>Parks the wheel in the top-left corner and leaves it open. Unlike
    /// Open() this raises no scrim, so the canvas keeps every press outside the
    /// rim.</summary>
    private void OpenDocked()
    {
        PlaceDocked();
        _open = true;
        _dragArm = false; _fromChip = false; _sticky = true;
        _hoverSlot = -1; _hoverArc = -1; _dragArc = -1; _hoverColour = false;
        _scrim.Visibility = Visibility.Collapsed;
        _wheel.Visibility = Visibility.Visible;
        _wheel.Opacity = 1;
        var st = (ScaleTransform)_wheel.RenderTransform;
        st.ScaleX = st.ScaleY = _scale;
        Refresh();
    }

    /// <summary>Top-left dock, mirrored to the top-right for a left-handed user
    /// so the wheel sits under the drawing hand rather than across the page.</summary>
    private void PlaceDocked()
    {
        double w = _host.ActualWidth, h = _host.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double half = Footprint / 2;
        const double pad = 10;
        // dock left; PenDock == "Right" means the user keeps their tools on the
        // right, so mirror the corner to match rather than crossing the page
        _mirrored = string.Equals(_h.Library().PenDock, "Right", StringComparison.OrdinalIgnoreCase);
        double cx = _mirrored ? Math.Max(half, w - half - pad) : half + pad;
        double cy = Math.Min(half + pad, Math.Max(half, h - half));
        _centre = new Point(cx, cy);
        Canvas.SetLeft(_wheel, _centre.X - half);
        Canvas.SetTop(_wheel, _centre.Y - half);
        PlaceGrip();
    }

    /// <summary>The ToolUiChanged subscriber: a dumb re-render of whatever the
    /// shared state now says. Never writes state.</summary>
    public void Refresh()
    {
        if (!_on) return;
        var lib = _h.Library();
        _scale = lib.TouchMode ? 1.12 : 1.0;
        var st = (ScaleTransform)_wheel.RenderTransform;
        if (!_open) { st.ScaleX = st.ScaleY = _scale; }

        bool dark = IsDark();
        var accent = Accent();
        var ink = Ink();
        var dim = Color.FromArgb(dark ? (byte)0x70 : (byte)0x66, ink.R, ink.G, ink.B);
        var edge = dark ? Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x14, 0x14, 0x13);
        var bodyCol = dark ? Color.FromArgb(0xF2, 0x1C, 0x1B, 0x1F) : Color.FromArgb(0xF2, 0xF7, 0xF5, 0xF0);
        var slotCol = dark ? Color.FromArgb(0xE8, 0x2A, 0x29, 0x2E) : Color.FromArgb(0xE8, 0xEE, 0xEB, 0xE4);

        // Brush-level writes only: WinUI caches GradientStop mutations, so every
        // repaint replaces the brush rather than poking a live one.
        _body.Fill = new SolidColorBrush(bodyCol);
        _body.Stroke = new SolidColorBrush(edge);

        var ids = ResolveSlots();
        for (int i = 0; i < Slots; i++)
        {
            string id = ids[i];
            bool live = id.Length > 0;
            bool active = IsActive(id);
            bool lit = active || _hoverSlot == i;

            _slotArc[i].Fill = new SolidColorBrush(_hoverSlot == i ? Tint(accent, 0.85) : active ? accent : slotCol);
            _slotArc[i].Stroke = new SolidColorBrush(_hoverSlot == i ? accent : edge);
            _slotArc[i].StrokeThickness = _hoverSlot == i ? 2 : 1;
            _slotArc[i].Opacity = live ? 1 : 0.32;

            var fg = lit ? (ColorUtil.IsDark(accent) ? Colors.White : Color.FromArgb(255, 0x14, 0x14, 0x13)) : ink;

            _slotArt[i].Children.Clear();
            var art = SlotArt(id, fg, active);
            if (art != null) _slotArt[i].Children.Add(art);

            // Pens carry their own size number on the rim; tools and commands do not.
            var pen = PenOf(id);
            _slotNum[i].Text = pen != null ? $"{pen.Size:0.#}" : "";
            _slotNum[i].Foreground = new SolidColorBrush(lit ? fg : dim);
        }

        // ---- the three arc sliders ----
        var ap = ActivePen();
        bool eraser = _h.ToolTag() == "Eraser";
        bool[] enabled = { ArcEnabled(0), ArcEnabled(1), ArcEnabled(2) };
        double[] frac =
        {
            enabled[0] ? (eraser ? Norm01(lib.EraserSize, 0, 80) : Norm01(ap!.Size, 1, 24)) : 0,
            InkAlpha(ap) / 255.0,
            enabled[2] ? ap!.Stabiliser : 0,
        };
        string[] read =
        {
            enabled[0] ? (eraser ? (lib.EraserSize <= 0 ? Loc.T("Wheel.Auto") : $"{lib.EraserSize:0} px") : $"{ap!.Size:0.#} px") : "—",
            $"{InkAlpha(ap) * 100 / 255}%",
            enabled[2] ? $"{ap!.Stabiliser * 100:0}%" : "—",
        };

        for (int i = 0; i < 3; i++)
        {
            bool en = enabled[i];
            _arcSeg[i].Fill = new SolidColorBrush(_hoverArc == i && en ? Tint(accent, 0.26)
                : dark ? Color.FromArgb(0x55, 0x00, 0x00, 0x00) : Color.FromArgb(0x14, 0x14, 0x14, 0x13));
            _arcSeg[i].Stroke = new SolidColorBrush(edge);
            _arcSeg[i].Opacity = en ? 1 : 0.45;

            // The progress track: an arc from the slider's minimum end, swept
            // clockwise by the value. Replaced wholesale, never mutated.
            var (a0, a1) = ArcRange(i);
            double span = Norm360(a0 - a1);
            _arcFill[i].Data = StrokeArc(a0, a0 - span * Math.Clamp(frac[i], 0, 1), MidOut - 5);
            _arcFill[i].Stroke = new SolidColorBrush(en ? ArcHue(i, accent) : dim);
            _arcFill[i].Opacity = en ? 1 : 0.35;

            _arcIcon[i].Children.Clear();
            var glyph = ArcGlyph(i, en ? ink : dim);
            if (glyph != null) _arcIcon[i].Children.Add(glyph);
            _arcIcon[i].Opacity = en ? 1 : 0.5;

            _arcText[i].Text = read[i];
            _arcText[i].Foreground = new SolidColorBrush(en ? ink : dim);
            _arcText[i].Opacity = en ? 1 : 0.6;
        }

        // ---- the inner colour button ----
        var swatch = ActiveColour();
        _colour.Fill = new SolidColorBrush(swatch);
        _colour.Stroke = new SolidColorBrush(_hoverColour ? accent : edge);
        _colour.StrokeThickness = _hoverColour ? 3 : 1.5;

        _grip.Fill = new SolidColorBrush(dim);

        // The chip mirrors the disc, so the closed state still reads as a status chip.
        _chip.Background = new SolidColorBrush(swatch);
        _chip.BorderBrush = new SolidColorBrush(edge);
        _chipArt.Children.Clear();
        var chipFg = ColorUtil.IsDark(swatch) ? Colors.White : Color.FromArgb(255, 0x14, 0x14, 0x13);
        var chipGlyph = _h.ToolTag() == "Pen" && ap != null ? PenChip(ap, 24) : IconFill(ToolGlyph(_h.ToolTag()), chipFg, 22);
        if (chipGlyph != null) _chipArt.Children.Add(chipGlyph);
    }

    // ===================================================================
    // Geometry helpers — math convention, y flipped for the screen
    // ===================================================================
    private static double Norm360(double d) => ((d % 360) + 360) % 360;
    private static double Norm01(double v, double lo, double hi) => Math.Clamp((v - lo) / (hi - lo), 0, 1);

    private static Point P(double deg, double r)
    {
        double t = deg * Math.PI / 180;
        return new Point(Footprint / 2 + r * Math.Cos(t), Footprint / 2 - r * Math.Sin(t));
    }

    private static (double A0, double A1) ArcRange(int i) => i switch
    {
        0 => (SizeA0, SizeA1),
        1 => (OpacA0, OpacA1),
        _ => (SmoothA0, SmoothA1),
    };

    /// <summary>Filled annulus sector swept CLOCKWISE from a0 down to a1.</summary>
    private static Geometry Sector(double a0, double a1, double rIn, double rOut)
    {
        bool large = Norm360(a0 - a1) > 180;
        var fig = new PathFigure { StartPoint = P(a0, rOut), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new ArcSegment { Point = P(a1, rOut), Size = new Size(rOut, rOut),
                                          SweepDirection = SweepDirection.Clockwise, IsLargeArc = large });
        fig.Segments.Add(new LineSegment { Point = P(a1, rIn) });
        fig.Segments.Add(new ArcSegment { Point = P(a0, rIn), Size = new Size(rIn, rIn),
                                          SweepDirection = SweepDirection.Counterclockwise, IsLargeArc = large });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    /// <summary>Open arc for the progress tracks, again swept clockwise.</summary>
    private static Geometry StrokeArc(double a0, double a1, double r)
    {
        var geo = new PathGeometry();
        if (Math.Abs(a0 - a1) < 0.3) return geo;   // an empty track draws nothing at all
        var fig = new PathFigure { StartPoint = P(a0, r), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment { Point = P(a1, r), Size = new Size(r, r),
                                          SweepDirection = SweepDirection.Clockwise,
                                          IsLargeArc = Norm360(a0 - a1) > 180 });
        geo.Figures.Add(fig);
        return geo;
    }

    private static double SlotMid(int i) => 90 - SlotSpan * i;   // slot 0 at N, then clockwise

    private void BuildWheel()
    {
        const double c = Footprint / 2;

        _body.Width = _body.Height = MidOut * 2;
        _body.StrokeThickness = 1;
        _body.IsHitTestVisible = false;
        Canvas.SetLeft(_body, c - MidOut);
        Canvas.SetTop(_body, c - MidOut);
        _wheel.Children.Add(_body);

        for (int i = 0; i < 3; i++)
        {
            var (a0, a1) = ArcRange(i);
            var seg = new Path { Data = Sector(a0 - Seam, a1 + Seam, MidIn, MidOut), StrokeThickness = 1, IsHitTestVisible = false };
            _arcSeg[i] = seg;
            _wheel.Children.Add(seg);
        }
        for (int i = 0; i < 3; i++)
        {
            var fill = new Path { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round,
                                  StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
            _arcFill[i] = fill;
            _wheel.Children.Add(fill);
        }

        for (int i = 0; i < Slots; i++)
        {
            double mid = SlotMid(i);
            var p = new Path { Data = Sector(mid + SlotSpan / 2 - Seam, mid - SlotSpan / 2 + Seam, OutIn, OutOut),
                               IsHitTestVisible = false };
            _slotArc[i] = p;
            _wheel.Children.Add(p);
        }
        // Slot content rides above every arc so a highlight never paints over it.
        for (int i = 0; i < Slots; i++)
        {
            double mid = SlotMid(i);
            var at = P(mid, IconR);
            var g = new Grid { Width = 30, Height = 30, IsHitTestVisible = false };
            Canvas.SetLeft(g, at.X - 15);
            Canvas.SetTop(g, at.Y - 15);
            _slotArt[i] = g;
            _wheel.Children.Add(g);

            var nAt = P(mid, NumR);
            var t = new TextBlock { Width = 44, FontSize = 10.5, TextAlignment = TextAlignment.Center,
                                    IsHitTestVisible = false, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            Canvas.SetLeft(t, nAt.X - 22);
            Canvas.SetTop(t, nAt.Y - 7);
            _slotNum[i] = t;
            _wheel.Children.Add(t);
        }

        // Arc icon + numeric readout, stacked at each slider's angular midpoint.
        for (int i = 0; i < 3; i++)
        {
            var (a0, a1) = ArcRange(i);
            double mid = a0 - Norm360(a0 - a1) / 2;
            var at = P(mid, ReadR);
            var g = new Grid { Width = 22, Height = 22, IsHitTestVisible = false };
            Canvas.SetLeft(g, at.X - 11);
            Canvas.SetTop(g, at.Y - 21);
            _arcIcon[i] = g;
            _wheel.Children.Add(g);

            var t = new TextBlock { Width = 66, FontSize = 12, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
            Canvas.SetLeft(t, at.X - 33);
            Canvas.SetTop(t, at.Y + 3);
            _arcText[i] = t;
            _wheel.Children.Add(t);
        }

        _colour.Width = _colour.Height = ColourR * 2;
        _colour.IsHitTestVisible = false;
        Canvas.SetLeft(_colour, c - ColourR);
        Canvas.SetTop(_colour, c - ColourR);
        _wheel.Children.Add(_colour);

        // Drag grip satellite — the app's own six-dot grip mark as vector
        // geometry rather than a glyph.
        _grip.Data = ParseGeom(
            "M3 4.5 a1.5 1.5 0 1 0 0.01 0 Z M8 4.5 a1.5 1.5 0 1 0 0.01 0 Z M13 4.5 a1.5 1.5 0 1 0 0.01 0 Z " +
            "M3 11.5 a1.5 1.5 0 1 0 0.01 0 Z M8 11.5 a1.5 1.5 0 1 0 0.01 0 Z M13 11.5 a1.5 1.5 0 1 0 0.01 0 Z");
        _grip.Width = _grip.Height = 16;
        _grip.Stretch = Stretch.Uniform;
        _grip.IsHitTestVisible = false;
        _wheel.Children.Add(_grip);
        PlaceGrip();
    }

    private void PlaceGrip()
    {
        // Handedness flip: the grip (and nothing else) mirrors to the far side.
        // Slot angles never change, so muscle memory survives the flip.
        var at = P(_mirrored ? 135 : 45, SatR);
        Canvas.SetLeft(_grip, at.X - 8);
        Canvas.SetTop(_grip, at.Y - 8);
    }

    // ===================================================================
    // Slot bindings + persistence
    // ===================================================================

    /// <summary>The ten slot ids. An empty persisted list means "use the
    /// defaults", so an existing library.json costs nothing until the user
    /// actually customises the dial.</summary>
    private string[] ResolveSlots()
    {
        var lib = _h.Library();
        var stored = lib.WheelSlots;
        string[] outp;
        if (stored.Count == 0) outp = DefaultSlots(lib);
        else
        {
            outp = new string[Slots];
            for (int i = 0; i < Slots; i++) outp[i] = i < stored.Count ? stored[i] ?? "" : "";
        }
        // A deleted preset empties its slot (dimmed) — it never reflows, because
        // muscle memory is the whole payoff.
        for (int i = 0; i < Slots; i++)
            if (outp[i].StartsWith(KindPen, StringComparison.Ordinal) && PenOf(outp[i]) == null)
                outp[i] = "";
        return outp;
    }

    private static string[] DefaultSlots(Library lib)
    {
        // Highest-traffic items on the cardinals; undo / redo / mouse mode ride
        // the left flank the way the reference dial does.
        var s = new string[Slots];
        for (int i = 0; i < 4; i++) s[i] = i < lib.Pens.Count ? KindPen + lib.Pens[i].Id : "";
        s[4] = KindTool + "Eraser";
        s[5] = KindTool + "Select";
        s[6] = KindTool + "Text";
        s[7] = KindCmd + "Redo";
        s[8] = KindCmd + "Undo";
        s[9] = KindCmd + "MouseMode";
        return s;
    }

    private void AssignSlot(int i, string id)
    {
        var lib = _h.Library();
        if (lib.WheelSlots.Count == 0) lib.WheelSlots.AddRange(DefaultSlots(lib));
        while (lib.WheelSlots.Count < Slots) lib.WheelSlots.Add("");
        lib.WheelSlots[i] = id;
        _h.Save();
        Refresh();
    }

    private PenPreset? PenOf(string id)
    {
        if (!id.StartsWith(KindPen, StringComparison.Ordinal)) return null;
        return Guid.TryParse(id.AsSpan(KindPen.Length), out var g)
            ? _h.Library().Pens.FirstOrDefault(x => x.Id == g) : null;
    }

    private bool IsActive(string id)
    {
        if (id.Length == 0) return false;
        if (PenOf(id) is { } pen) return _h.ToolTag() == "Pen" && _h.ActivePreset() == pen.Id;
        if (id.StartsWith(KindTool, StringComparison.Ordinal)) return _h.ToolTag() == id[KindTool.Length..];
        return false;
    }

    // ===================================================================
    // Placement / open / close
    // ===================================================================
    private void PlaceChip()
    {
        double w = _host.ActualWidth, h = _host.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double x = Math.Clamp(w * AnchorFx, 30, Math.Max(30, w - 70));
        double y = Math.Clamp(h * AnchorFy, 30, Math.Max(30, h - 70));
        _chip.Margin = new Thickness(x - 25, y - 25, 0, 0);
    }

    private Point ChipCentre() => new(_chip.Margin.Left + 25, _chip.Margin.Top + 25);

    private void Open(Point at, bool dragArm, bool fromChip)
    {
        if (!_on || _open) return;
        double half = Footprint * _scale / 2;
        _centre = new Point(
            Math.Clamp(at.X, half, Math.Max(half, _host.ActualWidth - half)),
            Math.Clamp(at.Y, half, Math.Max(half, _host.ActualHeight - half)));
        _mirrored = _centre.X > _host.ActualWidth / 2;
        PlaceGrip();

        Canvas.SetLeft(_wheel, _centre.X - Footprint / 2);
        Canvas.SetTop(_wheel, _centre.Y - Footprint / 2);
        _open = true; _dragArm = dragArm; _fromChip = fromChip; _sticky = !dragArm;
        _hoverSlot = -1; _hoverArc = -1; _dragArc = -1; _hoverColour = false;
        Refresh();

        _scrim.Visibility = Visibility.Visible;
        _wheel.Visibility = Visibility.Visible;
        _wheel.Opacity = 1;
        var st = (ScaleTransform)_wheel.RenderTransform;
        st.ScaleX = st.ScaleY = _scale;
        if (_h.ReduceMotion()) return;
        // <=120 ms open: a fast flick must still resolve against final geometry.
        var sb = new Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimation { From = _scale * 0.74, To = _scale, Duration = TimeSpan.FromMilliseconds(120),
                                          EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, st);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(100) };
        Storyboard.SetTarget(fade, _wheel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Begin();
    }

    private void Close()
    {
        _hold.Stop(); _holdArmed = false;
        _assign.Stop(); _assignSlot = -1;
        if (_docked && _on)
        {
            // A docked wheel never goes away - just drop the transient hover and
            // drag state and repaint.
            _pointer = null;
            _hoverSlot = -1; _hoverArc = -1; _dragArc = -1; _hoverColour = false;
            _dragArm = _fromChip = false;
            _scrim.Visibility = Visibility.Collapsed;
            Refresh();
            return;
        }
        if (!_open) { _scrim.Visibility = Visibility.Collapsed; return; }
        _open = _sticky = _dragArm = false;
        _pointer = null;
        _hoverSlot = -1; _hoverArc = -1; _dragArc = -1; _hoverColour = false;
        _scrim.Visibility = Visibility.Collapsed;
        _wheel.Visibility = Visibility.Collapsed;
        if (_on) Refresh();
    }

    // ===================================================================
    // Hit-testing — one atan2 + radius test, never a per-Path hit test
    // ===================================================================
    private (Zone Z, int Index, double R, double A) Aim(Point p)
    {
        double dx = p.X - _centre.X, dy = p.Y - _centre.Y;
        double r = Math.Sqrt(dx * dx + dy * dy) / _scale;
        double a = Norm360(Math.Atan2(-dy, dx) * 180 / Math.PI);
        if (r <= DeadR) return (Zone.None, -1, r, a);
        if (r <= ColourR) return (Zone.Colour, -1, r, a);
        if (r >= OutIn - 4) return (Zone.Slot, (int)Math.Round(Norm360(90 - a) / SlotSpan) % Slots, r, a);
        if (r >= MidIn - 4) return (Zone.Arc, ArcAt(a), r, a);
        return (Zone.None, -1, r, a);
    }

    private static int ArcAt(double a)
    {
        for (int i = 0; i < 3; i++)
        {
            var (a0, a1) = ArcRange(i);
            if (Norm360(a0 - a) <= Norm360(a0 - a1)) return i;
        }
        return 0;
    }

    private void Track(Point p, bool allowFlick)
    {
        var (z, idx, r, a) = Aim(p);
        if (_dragArc >= 0) { ScrubArc(_dragArc, a); return; }

        int slot = z == Zone.Slot ? idx : -1;
        int arc = z == Zone.Arc ? idx : -1;
        bool col = z == Zone.Colour;
        if (slot != _hoverSlot || arc != _hoverArc || col != _hoverColour)
        {
            if (slot != _hoverSlot) { _assign.Stop(); _assignSlot = -1; }
            _hoverSlot = slot; _hoverArc = arc; _hoverColour = col;
            Refresh();
        }
        // The expert flick: past the confirm threshold the slot fires without
        // waiting for pen-up.
        if (allowFlick && slot >= 0 && r >= ConfirmR) Commit(slot);
    }

    // ===================================================================
    // Commit — every write goes through the host, so the linear row sees it
    // ===================================================================
    private void Commit(int slot)
    {
        if (slot < 0 || slot >= Slots) { Close(); return; }
        string id = ResolveSlots()[slot];
        Close();
        if (id.Length == 0) return;
        if (PenOf(id) is { } pen) { _h.ApplyPreset(pen); return; }
        if (id.StartsWith(KindTool, StringComparison.Ordinal)) { _h.SelectTool(id[KindTool.Length..]); return; }
        if (!id.StartsWith(KindCmd, StringComparison.Ordinal)) return;
        switch (id[KindCmd.Length..])
        {
            case "Undo": _surface.Undo(); break;
            case "Redo": _surface.Redo(); break;
            case "MouseMode": ShowMouseModes(); break;
        }
    }

    // ---- arc scrubbing -------------------------------------------------
    // Opacity is greyed out because Quill has no pen-opacity parameter: stroke
    // colours serialise through ColorUtil.ToHex, which carries no alpha, so a
    // live opacity would silently reset on reload. The arc still renders (the
    // spec asks for disabled, not hidden) and reports the alpha the renderer
    // does apply. Giving ArcEnabled(1) a real condition is the only change
    // needed the day a pen opacity lands.
    private bool ArcEnabled(int i)
    {
        var ap = ActivePen();
        bool eraser = _h.ToolTag() == "Eraser";
        return i switch
        {
            0 => ap != null || eraser,
            1 => false,
            _ => ap != null && !eraser,
        };
    }

    private void ScrubArc(int i, double a)
    {
        if (!ArcEnabled(i)) return;
        var (a0, a1) = ArcRange(i);
        double t = Math.Clamp(Norm360(a0 - a) / Norm360(a0 - a1), 0, 1);
        var lib = _h.Library();
        var ap = ActivePen();
        if (i == 0)
        {
            if (_h.ToolTag() == "Eraser") { lib.EraserSize = Math.Round(t * 80); _surface.EraserSize = lib.EraserSize; }
            else if (ap != null) { ap.Size = (float)Math.Round(1 + t * 23, 1); _surface.PenSize = ap.Size; }
            else return;
        }
        else if (i == 2 && ap != null)
        {
            ap.Stabiliser = (float)Math.Round(t, 2);
            _surface.PenStabiliser = ap.Stabiliser;
        }
        else return;
        _h.Save();
        Refresh();
    }

    // ---- flyouts -------------------------------------------------------
    private void ShowColourPicker(Point discRoot)
    {
        var ap = ActivePen();
        if (ap == null) return;
        var start = ColorUtil.Parse(ap.Color);
        void Commit(Color c)
        {
            ap.Color = ColorUtil.ToHex(c);
            _h.ApplyPreset(ap);   // routes through the shared funnel: the linear row repaints too
            _h.Save();
        }
        // The dial was already closed before this fired; nothing to do on the
        // picker closing, but the slot is here for symmetry with the service.
        if (ColourPickerHook != null) { ColourPickerHook(discRoot, start, Commit, null); return; }

        // Fallback until Workstream A's picker is wired in.
        var picker = new ColorPicker { Color = start, IsAlphaEnabled = false, IsMoreButtonVisible = true,
                                       IsColorSliderVisible = true, IsHexInputVisible = true, Width = 288 };
        picker.ColorChanged += (_, e) => Commit(e.NewColor);
        new Flyout { Content = picker }.ShowAt(_chip);
    }

    // The colour disc's centre in XamlRoot coordinates, so the picker anchors
    // where the disc actually is rather than at the summon chip.
    private Point DiscRootPoint()
    {
        try
        {
            var t = _wheel.TransformToVisual((UIElement?)_host.XamlRoot?.Content ?? _host);
            return t.TransformPoint(new Point(Footprint / 2, Footprint / 2));
        }
        catch { return _centre; }
    }

    private void ShowMouseModes()
    {
        var menu = new MenuFlyout();
        foreach (var mode in new[] { "Auto", "Grab", "Select", "Move" })
        {
            var m = mode;
            var item = new MenuFlyoutItem { Text = Loc.T("Wheel.Mouse." + m) };
            item.Click += (_, _) => _h.SetMouseMode(m);
            menu.Items.Add(item);
        }
        menu.ShowAt(_chip);
    }

    /// <summary>The slot-assignment flyout: press-hold or right-click a slot.
    /// This is how the user chooses which ten tools occupy the dial.</summary>
    private void ShowAssign(int slot)
    {
        _assignSlot = -1;
        var lib = _h.Library();
        var panel = new StackPanel { Spacing = 2, Width = 244 };
        var fly = new Flyout { Content = new ScrollViewer { MaxHeight = 440, Content = panel } };

        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Wheel.Assign.Header", slot + 1), FontSize = 12, Opacity = 0.7,
            Margin = new Thickness(4, 2, 4, 6), TextWrapping = TextWrapping.Wrap
        });

        void Row(string id, FrameworkElement? art, string label)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (art != null) line.Children.Add(art);
            line.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            var b = new Button
            {
                Content = line, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 3, 6, 3)
            };
            b.Click += (_, _) => { fly.Hide(); AssignSlot(slot, id); };
            panel.Children.Add(b);
        }

        foreach (var preset in lib.Pens) Row(KindPen + preset.Id, PenChip(preset, 22), preset.Name);
        foreach (var t in ToolKinds) Row(KindTool + t, IconFill(ToolGlyph(t), Ink(), 20), Loc.T("Wheel.Tool." + t));
        foreach (var c in CmdKinds) Row(KindCmd + c, CmdArt(c, Ink(), 20), Loc.T("Wheel.Cmd." + c));
        Row("", null, Loc.T("Wheel.Assign.Empty"));

        fly.ShowAt(_chip);
    }

    // ===================================================================
    // Pointer state machine
    // ===================================================================
    private bool OverChip(object? src) => src is DependencyObject d && IsWithin(d, _chip);

    private static bool IsWithin(DependencyObject? node, DependencyObject root)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, root)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_on) return;
        var pt = e.GetCurrentPoint(_host);
        var p = pt.Position;

        if (_open)
        {
            _pointer = e.Pointer.PointerId;
            _pressPt = p; _pressMs = Environment.TickCount64;
            _dragArm = true; _fromChip = false;
            var (z, idx, r, a) = Aim(p);
            if (r > SatR + 20)
            {
                // Docked is not modal: hand the press straight back to the canvas
                // so drawing beside the wheel behaves exactly as if it were absent.
                if (_docked) { _pointer = null; _dragArm = false; return; }
                Close(); e.Handled = true; return;
            }
            _hoverSlot = z == Zone.Slot ? idx : -1;
            _hoverArc = z == Zone.Arc ? idx : -1;
            _hoverColour = z == Zone.Colour;
            // Right-click assigns straight away; a press-hold gets there too.
            if (z == Zone.Slot && pt.Properties.IsRightButtonPressed) { Refresh(); ShowAssign(idx); e.Handled = true; return; }
            if (z == Zone.Slot) { _assignSlot = idx; _assign.Stop(); _assign.Start(); }
            if (z == Zone.Arc && ArcEnabled(idx)) { _dragArc = idx; ScrubArc(idx, a); }
            else Refresh();
            e.Handled = true;
            return;
        }

        if (OverChip(e.OriginalSource))
        {
            _pointer = e.Pointer.PointerId;
            _pressPt = p; _pressMs = Environment.TickCount64;
            _scrim.CapturePointer(e.Pointer);
            Open(ChipCentre(), dragArm: true, fromChip: true);
            e.Handled = true;
            return;
        }

        // Canvas press-and-hold. Only a press the ink surface LEFT UNHANDLED can
        // summon — drawing, the barrel lasso, the right-button menu and every
        // selection drag all mark the event handled, so none can be shadowed.
        if (e.Handled) return;
        _holdArmed = true; _holdPt = p; _pointer = e.Pointer.PointerId;
        _hold.Stop(); _hold.Start();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_on) return;
        if (_pointer != null && e.Pointer.PointerId != _pointer) return;
        var p = e.GetCurrentPoint(_host).Position;

        if (_holdArmed && Dist(p, _holdPt) > TapSlop) { _holdArmed = false; _hold.Stop(); }
        if (!_open) return;
        if (_assignSlot >= 0 && Dist(p, _pressPt) > TapSlop) { _assign.Stop(); _assignSlot = -1; }
        Track(p, allowFlick: _dragArm && _dragArc < 0);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_on) return;
        _hold.Stop(); _holdArmed = false;
        _assign.Stop(); _assignSlot = -1;
        if (_pointer != null && e.Pointer.PointerId != _pointer) return;
        _pointer = null;
        if (!_open) return;
        var p = e.GetCurrentPoint(_host).Position;
        try { _scrim.ReleasePointerCapture(e.Pointer); } catch { }

        bool wasArm = _dragArm;
        if (_dragArc >= 0)
        {
            // Scrubbing ends where it ends; a dial summoned by a press closes, a
            // sticky one stays up so the value can be nudged again.
            _dragArc = -1;
            if (wasArm && _fromChip) { _dragArm = false; _sticky = true; return; }
            if (wasArm) Close();
            return;
        }
        if (!wasArm) return;

        var (z, idx, r, _) = Aim(p);
        bool tap = Environment.TickCount64 - _pressMs < TapMs && Dist(p, _pressPt) <= TapSlop;

        if (z == Zone.Colour) { var root = DiscRootPoint(); Close(); ShowColourPicker(root); return; }
        if (r <= DeadR)
        {
            // Dead zone. A quick tap that opened the dial leaves it up so a mouse
            // user can click a slot; anything longer is a cancel.
            if (tap && _fromChip) { _dragArm = false; _sticky = true; return; }
            Close();
            return;
        }
        if (z == Zone.Slot) { Commit(idx); return; }
        if (z == Zone.Arc) { if (_fromChip && tap) { _dragArm = false; _sticky = true; return; } Close(); return; }
        Close();
    }

    private void OnLost(object sender, PointerRoutedEventArgs e)
    {
        _hold.Stop(); _holdArmed = false;
        _assign.Stop(); _assignSlot = -1;
        if (_open && _dragArm && _pointer != null && e.Pointer.PointerId == _pointer) { _pointer = null; Close(); }
    }

    private static double Dist(Point a, Point b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

    // ===================================================================
    // State reads
    // ===================================================================
    private PenPreset? ActivePen()
    {
        var id = _h.ActivePreset();
        return id == null ? null : _h.Library().Pens.FirstOrDefault(x => x.Id == id);
    }

    // The inner disc shows the live ink colour; a tool that has no colour of its
    // own shows white, per the reference design.
    private Color ActiveColour() =>
        _h.ToolTag() == "Pen" && ActivePen() is { } p ? ColorUtil.Parse(p.Color) : Colors.White;

    // The alpha the renderer actually lays down, which is what the (greyed)
    // opacity readout reports.
    private static byte InkAlpha(PenPreset? p) => p?.Pen switch
    {
        PenType.Marker => 235,
        PenType.Ballpoint => 240,
        _ => 255,
    };

    private Color Accent() { try { return ColorUtil.Parse(_h.Library().AccentColor); } catch { return Color.FromArgb(255, 0x2E, 0x94, 0xF2); } }
    private bool IsDark() => _host.ActualTheme == ElementTheme.Dark;
    private Color Ink() => IsDark() ? Color.FromArgb(255, 0xEC, 0xE9, 0xE2) : Color.FromArgb(255, 0x2A, 0x28, 0x25);
    private static Color Tint(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    // Each slider keeps its own progress hue so the three tracks stay tellable
    // apart at a glance, exactly as the reference dial does.
    private static Color ArcHue(int i, Color accent) => i switch
    {
        0 => Color.FromArgb(255, 0x4C, 0xC9, 0x7A),
        1 => accent,
        _ => Color.FromArgb(255, 0xB4, 0x8C, 0xE8),
    };

    // ===================================================================
    // Art
    // ===================================================================
    private FrameworkElement? SlotArt(string id, Color fg, bool active)
    {
        if (id.Length == 0) return null;
        if (PenOf(id) is { } pen)
            // The active pen shows a stroke preview instead of its chip — the
            // dial's answer to "what will this actually lay down?".
            return active ? StrokePreview(pen, fg) : PenChip(pen, 26);
        if (id.StartsWith(KindTool, StringComparison.Ordinal)) return IconFill(ToolGlyph(id[KindTool.Length..]), fg, 22);
        if (id.StartsWith(KindCmd, StringComparison.Ordinal)) return CmdArt(id[KindCmd.Length..], fg, 22);
        return null;
    }

    // The pen slot reuses the linear row's own two-tone chip, so a pen looks
    // identical in both surfaces.
    private FrameworkElement? PenChip(PenPreset p, double size)
    {
        try
        {
            var (body, col) = _h.ChipData(p.Pen);
            var art = _h.TwoTone(body, col, ColorUtil.Parse(p.Color));
            art.Width = size * 0.72;
            art.Height = size;
            art.HorizontalAlignment = HorizontalAlignment.Center;
            art.VerticalAlignment = VerticalAlignment.Center;
            return art;
        }
        catch { return null; }
    }

    // A short tapered squiggle in the pen's own colour, its weight following the
    // preset size.
    private FrameworkElement? StrokePreview(PenPreset p, Color fg)
    {
        try
        {
            var col = ColorUtil.Parse(p.Color);
            // On the accent highlight a same-value pen would vanish, so fall back
            // to the highlight's own foreground when contrast collapses.
            var stroke = ColorUtil.IsDark(col) == ColorUtil.IsDark(fg) ? fg : col;
            return new Path
            {
                Data = ParseGeom("M2 17 C6 6 10 20 14 10 C16.5 4 19 9 22 6"),
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = Math.Clamp(p.Size / 2.4, 1.4, 5.5),
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = 26, Height = 26, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch { return null; }
    }

    private static FrameworkElement? ArcGlyph(int i, Color fg) => i switch
    {
        // Size: three rules, thick to thin — the shipped icon set's weight mark.
        0 => IconFill("M4 6.2 H20 V9.4 H4 Z M4 11.6 H20 V13.6 H4 Z M4 15.8 H20 V16.9 H4 Z", fg, 20),
        // Opacity (new mark): a disc whose right half is solid and whose left
        // half is an open ring — coverage, on the app's 24 grid. F1 = nonzero,
        // so the reversed inner circle cuts the hole and the half-disc fills it.
        1 => IconFill("F1 M12 2.6 A9.4 9.4 0 0 1 12 21.4 A9.4 9.4 0 0 1 12 2.6 Z " +
                      "M12 4.7 A7.3 7.3 0 0 0 12 19.3 A7.3 7.3 0 0 0 12 4.7 Z " +
                      "M12 4.7 A7.3 7.3 0 0 1 12 19.3 Z", fg, 20),
        // Smoothness (new mark): a jagged trace on the left resolving into a
        // smooth wave on the right. Stroked, because that is what the mark IS.
        _ => IconStroke("M2 17.2 L4.6 8.2 L6.6 15.8 L8.6 7.4 L10.8 14.2 C12.9 14.2 13.3 8.4 15.7 8.4 " +
                        "C18.1 8.4 18.6 15 22 15", fg, 20, 1.7),
    };

    private static FrameworkElement? CmdArt(string cmd, Color fg, double size) => cmd switch
    {
        // Undo / redo: one curved-arrow geometry, mirrored for redo.
        "Undo" => IconFill(UndoGlyph, fg, size),
        "Redo" => Mirror(IconFill(UndoGlyph, fg, size)),
        _ => IconFill(MouseGlyph, fg, size),
    };

    private static FrameworkElement? Mirror(FrameworkElement? el)
    {
        if (el == null) return null;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = new ScaleTransform { ScaleX = -1 };
        return el;
    }

    private const string UndoGlyph =
        "M8.6 3.6 L1.8 9.6 L8.6 15.6 V11.4 H13.8 A4.2 4.2 0 1 1 13.8 19.8 H8.6 V22.4 H13.8 " +
        "A6.8 6.8 0 1 0 13.8 8.8 H8.6 Z";
    private const string MouseGlyph =
        "M6 2.6 L6 19.8 L10.3 15.8 L13.1 21.6 L15.8 20.3 L13 14.6 L18.7 14.3 Z";

    private static Path? IconFill(string data, Color fill, double size)
    {
        try
        {
            return new Path
            {
                Data = ParseGeom(data), Fill = new SolidColorBrush(fill),
                Width = size, Height = size, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch { return null; }
    }

    private static Path? IconStroke(string data, Color colour, double size, double thickness)
    {
        try
        {
            return new Path
            {
                Data = ParseGeom(data), Stroke = new SolidColorBrush(colour), StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = size, Height = size, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch { return null; }
    }

    private static Geometry ParseGeom(string data)
    {
        var p = (Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + data + "'/>");
        var geo = p.Data;
        p.Data = null;   // a Geometry cannot be parented to two Paths at once
        return geo!;
    }

    // Tool glyphs on the app's own 24 grid, matching the shipped icon set so the
    // dial and the top bar draw the same marks.
    private static string ToolGlyph(string tag) => tag switch
    {
        "Eraser" => "M13.6 3.5 20.5 10.4 11.4 19.5 H20.5 V21.5 H7.5 L3.5 17.5 A2 2 0 0 1 3.5 14.7 Z M6.3 14.7 11.4 19.8 14.2 17 9.1 11.9 Z",
        "Select" => "M6.23 12.96 L5.60 14.21 L8.01 15.43 L8.64 14.18 Z M4.17 9.77 L2.90 10.36 L4.04 12.81 L5.31 12.22 Z M5.31 6.78 L4.04 6.19 L2.90 8.64 L4.17 9.23 Z M8.64 4.82 L8.01 3.57 L5.60 4.79 L6.23 6.04 Z M13.35 4.40 L13.35 3.00 L10.65 3.00 L10.65 4.40 Z M17.77 6.04 L18.40 4.79 L15.99 3.57 L15.36 4.82 Z M19.83 9.23 L21.10 8.64 L19.96 6.19 L18.69 6.78 Z M18.69 12.22 L19.96 12.81 L21.10 10.36 L19.83 9.77 Z M15.36 14.18 L15.99 15.43 L18.40 14.21 L17.77 12.96 Z M10.5 15.6 a1.5 1.5 0 1 0 3 0 a1.5 1.5 0 1 0 -3 0 Z M11.9 16.9 C11.9 18.3 10.2 18.6 9.3 19.4 C8.5 20.1 8.6 21.1 9.3 21.8 L7.9 22.2 C6.9 21.1 7.1 19.6 8.3 18.7 C9.4 17.9 10.4 17.8 10.4 16.8 Z",
        "FreeSpace" => "M3 4.4 H21 V6 H3 Z M3 18 H21 V19.6 H3 Z M12 7.2 15 10.6 13 10.6 13 13.4 15 13.4 12 16.8 9 13.4 11 13.4 11 10.6 9 10.6 Z",
        // Text: a serif "A" on the baseline, drawn rather than borrowed from a font
        "Text" => "M11.1 3.5 H12.9 L19 20.5 H16.9 L15.2 15.6 H8.8 L7.1 20.5 H5 Z M9.4 13.8 H14.6 L12 6.4 Z",
        _ => "M15.5 5.5 a1.9 1.9 0 0 1 2.9 2.9 L12 15.3 8.6 11.9 Z M9.8 13.1 10.9 14.2 4 20 Z",
    };
}
