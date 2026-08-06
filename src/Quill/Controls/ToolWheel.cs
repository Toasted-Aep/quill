using System.Numerics;
using Quill.Helpers;
using Quill.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
/// The radial tool dial (docs/UI-SPEC-V2.md section 1; defects in UI-SPEC-V3.md
/// section A).
///
/// THREE CONCENTRIC ANNULI, each cut into equal sectors; a sector IS the button:
///
///   CENTRE DISC     the colour button - the live ink colour, WHITE when the
///                   active tool has none of its own. Opens the COPIC wheel.
///   INNER ANNULUS   exactly three 120 degree sectors: Size (+30 to +150),
///                   Smoothness (+150 to -90), Opacity (+30 to -90). 0 degrees
///                   is at the right. Each carries its own vector mark and greys
///                   out when it does not apply to the active tool.
///   OUTER ANNULUS   exactly ten 36 degree sectors: the tool slots, slot 0 at
///                   twelve o'clock and running clockwise. A slot whose command
///                   cannot run right now (redo with an empty stack) draws FULLY
///                   TRANSPARENT - not hidden, not dimmed.
///
/// INPUT. Everything the dial listens to arrives on <see cref="_shield"/>, one
/// transparent circle exactly the size of the outer rim. That single decision
/// fixes three shipped defects at once:
///   - no phantom hover: the shield only raises events inside the rim, so a
///     pointer over the page cannot light a sector (V3 A.1);
///   - slots can be picked: the press never reaches InkSurface, so InkSurface
///     never captures the pointer and never releases that capture mid-gesture.
///     The old build handled presses on the host with handledEventsToo, and
///     InkSurface.ReleasePointerCaptures() raised PointerCaptureLost BEFORE the
///     release finished bubbling; the dial's own OnLost then cleared the armed
///     flag and OnReleased bailed out without ever committing (V3 A.3);
///   - the dial is still NOT modal: there is no scrim, and the shield is only as
///     big as the wheel, so a press one pixel beyond the rim belongs to the
///     canvas and drawing, lasso and the barrel menu work right beside it.
///
/// Rendering is code-built <see cref="Path"/> annular sectors (never Win2D,
/// never inside InkSurface) so the ink renderer pays nothing for the chrome. The
/// one exception is the live scrub preview, which is a real Win2D surface
/// precisely because it has to go through the real stroke renderer.
/// </summary>
public sealed class ToolWheel
{
    // ===================================================================
    // Geometry - DIPs at scale 1.0. Angles are MATH convention throughout:
    // 0 degrees at the RIGHT, counter-clockwise positive, y flipped for screen.
    // ===================================================================
    // Every number below is the user's own web build, 1:1 - see
    // "New folder (4)/Concepts/src/components/RadialDial.jsx", whose SVG is a
    // 260 box with the dial centred at (130,130). Only the COLOURS differ: the
    // web hard-codes a slate palette, and Quill's must follow the page
    // (ThemeSource="Page"), so hues come from the app and never from the JSX.
    private const double Footprint = 260;          // viewBox 0 0 260 260
    private const double Half = Footprint / 2;     // 130

    private const double ColourR = 26;             // centre disc: the colour button
    private const double InIn = 27, InOut = 82;    // inner annulus: the 3 settings
    private const double OutIn = 84, OutOut = 125; // outer annulus: the 10 tools
    private const double RimR = OutOut;            // hit shield radius == the rim
    private const double PlateR = 125;             // the disc the sectors sit on

    private const double IconR = 104;              // slot mark centre, mid-annulus
    // The web anchors slot content at rCenter and then splits it by half:
    // upper half puts the mark below the anchor and the size text above it,
    // lower half does the reverse (RadialDial.jsx lines 200-218).
    private const double MarkUpDy = 5, MarkDownDy = -1;
    private const double TextUpDy = -13, TextDownDy = 12;
    private const double MarkBox = 17;             // web uses 16; +1 keeps the two-tone pen chips legible
    private const double GripR = 138;              // handedness grip satellite

    public const int Slots = 10;                   // exactly ten, user-assignable
    private const double SlotSpan = 360.0 / Slots; // 36 degrees each
    private const double SetSpan = 120.0;          // 3 x 120 tile the inner ring
    private const double Seam = 0.9;               // hairline so wedges read apart

    // The three setting sectors as (zeroEnd, oneEnd) sweeping CLOCKWISE, so a
    // clockwise drag always increases the value. The boundaries are exactly the
    // ones the spec names: Size +30/+150, Opacity +30/-90, Smoothness +150/-90.
    private const double SizeA0 = 150, SizeA1 = 30;
    private const double OpacA0 = 30, OpacA1 = -90;
    private const double SmoothA0 = 270, SmoothA1 = 150;

    private const int TapMs = 400;                 // press shorter than this + no drag = a tap
    private const double TapSlop = 8;
    private const int AssignMs = 550;              // press-hold on a slot opens its assignment flyout

    // The live preview is a ring drawn CONCENTRIC with the dial, exactly as the
    // web does it, so it never covers the sector being dragged. Its surface has
    // to be big enough for the widest ring plus the dashed guide outside it.
    private const double PreviewBox = 420;

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

    /// <summary>The COPIC wheel. MainWindow points this straight at
    /// ColorPickerService.Open (rootPoint, current, onChanged, onClosed).</summary>
    public Action<Point, Color, Action<Color>, Action?>? ColourPickerHook { get; set; }

    /// <summary>Raised whenever the occupied slots change (and when the dial is
    /// shown or hidden), carrying the top-bar element keys the dial has taken
    /// over. A tool that lives in the dial is removed from the top bar rather
    /// than offered twice - UI-SPEC-V3 A.9.</summary>
    public event Action<IReadOnlySet<string>>? SlotsChanged;

    // Slot ids are plain strings so the persisted list stays human-readable and
    // forward-compatible: "pen:<guid>" | "tool:<tag>" | "cmd:<name>" | "" (empty).
    private const string KindPen = "pen:", KindTool = "tool:", KindCmd = "cmd:";

    private static readonly string[] ToolKinds = { "Eraser", "Select", "Text", "FreeSpace", "Fill" };
    // The three the dial has always known how to run itself. Everything else a
    // user can put in a slot arrives through ExtraCommands, so the dial never
    // grows a second dependency on MainWindow.
    private static readonly string[] BuiltInCmds = { "Undo", "Redo", "MouseMode" };

    /// <summary>A top-bar command donated to the dial (UI-SPEC-V3 I: "every
    /// remaining top-bar feature moves into the radial dial as a selectable
    /// tool"). The host owns the behaviour; the dial owns the slot, the mark and
    /// the top-bar hand-back.</summary>
    public sealed class ExtraCommand
    {
        /// <summary>Slot id suffix - persisted as "cmd:&lt;Id&gt;".</summary>
        public required string Id { get; init; }
        public required string Label { get; init; }
        /// <summary>Geometry from <see cref="Icons"/>, never a glyph or an emoji.</summary>
        public required string Icon { get; init; }
        /// <summary>True when the mark IS a stroke rather than a silhouette.</summary>
        public bool Stroked { get; init; }
        /// <summary>The top-bar element this supersedes, so the bar drops it
        /// exactly the way it drops the pen and the eraser (V3 A.9).</summary>
        public string? TopBarKey { get; init; }
        public Func<bool>? IsActive { get; init; }
        public Func<bool>? IsAvailable { get; init; }
        /// <summary>Run on tap. Ignored when <see cref="Flyout"/> is supplied.</summary>
        public Action? Run { get; init; }
        /// <summary>A menu to open on the dial instead of running a command -
        /// this is how the shape and history buttons keep their existing menus
        /// rather than having them re-declared here.</summary>
        public Func<FlyoutBase?>? Flyout { get; init; }
    }

    /// <summary>Commands beyond undo / redo / mouse-mode that may occupy a slot.
    /// Assign before the first Refresh; changing it re-announces the slots.</summary>
    public IReadOnlyList<ExtraCommand> ExtraCommands
    {
        get => _extras;
        set { _extras = value ?? Array.Empty<ExtraCommand>(); if (_on) Refresh(); }
    }
    private IReadOnlyList<ExtraCommand> _extras = Array.Empty<ExtraCommand>();

    private ExtraCommand? Extra(string cmd)
    {
        foreach (var x in _extras) if (string.Equals(x.Id, cmd, StringComparison.Ordinal)) return x;
        return null;
    }

    private IEnumerable<string> CmdIds()
    {
        foreach (var c in BuiltInCmds) yield return c;
        foreach (var x in _extras) yield return x.Id;
    }

    private string CmdLabel(string cmd) => Extra(cmd)?.Label ?? Loc.T("Wheel.Cmd." + cmd);

    /// <summary>Headroom the dock leaves at the top of the host. The floating
    /// top-left bar sits directly ABOVE the docked dial (UI-SPEC-V3 I), and at
    /// the shipped dock the wheel's rim is 15 DIPs from the top edge - there is
    /// no "above" until the dial makes room. ChromeBars sets this to the bar's
    /// measured height; 0 restores the original dock exactly.</summary>
    /// <summary>Where the wheel's rim sits, in DIPs below the host's top edge,
    /// with no inset applied. ChromeBars needs it to convert the measured
    /// "top of wheel, 52 DIP below the title bar" into an inset without
    /// hard-coding this control's own dock padding.</summary>
    public static double RestingRimTop => Half + 10 - RimR;

    public double TopInset
    {
        get => _topInset;
        set
        {
            if (Math.Abs(_topInset - value) < 0.5) return;
            _topInset = value;
            if (_on) Place();
        }
    }
    private double _topInset;

    private readonly Grid _host;
    private readonly InkSurface _surface;
    private readonly Host _h;

    private readonly Grid _layer;        // overlay; Background stays null so ink still gets the pointer
    private readonly Ellipse _shield;    // the dial's ONLY input surface - exactly the outer rim
    private readonly Canvas _wheel;
    private readonly CanvasControl _preview;  // live scrub preview ring, real ink
    private readonly Border _bottom;     // tool-specific options rectangle (spec 1.3)
    private readonly StackPanel _bottomRow;

    private readonly Ellipse _body = new();
    private readonly Path[] _slotArc = new Path[Slots];
    private readonly Grid[] _slotArt = new Grid[Slots];
    private readonly TextBlock[] _slotNum = new TextBlock[Slots];
    private readonly Path[] _setArc = new Path[3];
    private readonly Path[] _setFill = new Path[3];
    private readonly Grid[] _setIcon = new Grid[3];
    private readonly TextBlock[] _setText = new TextBlock[3];
    private readonly Ellipse _colour = new();
    private readonly Path _grip = new();

    private bool _on;
    private bool _mirrored;              // handedness: the grip flips on a right dock
    private double _scale = 1;
    private Point _centre;
    private int _hoverSlot = -1;
    private int _hoverSet = -1;
    private int _dragSet = -1;           // a setting arc currently being scrubbed
    private bool _hoverColour;
    private bool _pressed;
    private uint? _pointer;
    private Point _pressPt;
    private long _pressMs;
    private int _assignSlot = -1;
    private readonly DispatcherTimer _assign = new() { Interval = TimeSpan.FromMilliseconds(AssignMs) };
    private UIElement? _keyTarget;
    private HashSet<string> _taken = new(StringComparer.Ordinal);

    // ===================================================================
    // Attach
    // ===================================================================
    public static ToolWheel Attach(Grid host, InkSurface surface, Host h) => new(host, surface, h);

    private ToolWheel(Grid host, InkSurface surface, Host h)
    {
        _host = host; _surface = surface; _h = h;

        _layer = new Grid { Visibility = Visibility.Collapsed };
        Canvas.SetZIndex(_layer, 60);

        // The hit shield goes in FIRST so the wheel paints over it; input order
        // is unaffected because everything above it is IsHitTestVisible=false.
        _shield = new Ellipse
        {
            Width = RimR * 2,
            Height = RimR * 2,
            // Transparent still hit-tests; a null Fill would not. An Ellipse
            // hit-tests to its actual ellipse, not its bounding box, so the
            // corners of the square stay with the canvas.
            Fill = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        // The shield is the dial's one hit surface, so it is also the thing an
        // assistive tool should find: name it, and the whole control becomes
        // addressable (and locatable) instead of being an anonymous ellipse.
        _layer.Children.Add(_shield);

        _wheel = new Canvas
        {
            Width = Footprint, Height = Footprint, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform()
        };
        _preview = new CanvasControl
        {
            Width = PreviewBox, Height = PreviewBox, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed, ClearColor = Colors.Transparent
        };
        _preview.Draw += DrawPreview;
        _layer.Children.Add(_preview);

        // Name the wheel so the whole control is addressable to assistive tools
        // (and locatable to a test) instead of being an anonymous canvas.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_wheel, "Tool dial");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_wheel, "ToolWheel");
        BuildWheel();
        _layer.Children.Add(_wheel);

        _bottomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _bottom = new Border
        {
            Child = _bottomRow, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 18)
        };
        _layer.Children.Add(_bottom);

        _host.Children.Add(_layer);

        // The dock is corner-anchored, so it is re-parked on every resize.
        _host.SizeChanged += (_, _) => { if (_on) Place(); };
        // Every colour here is captured at paint time, so a theme flip has to
        // repaint the dial the same way it rebuilds the pen strip.
        _host.ActualThemeChanged += (_, _) => Refresh();
        // Undo/redo availability is what makes those two sectors transparent, so
        // the dial repaints the moment the stacks change.
        _surface.UndoManager.Changed += Refresh;

        _shield.PointerPressed += OnPressed;
        _shield.PointerMoved += OnMoved;
        _shield.PointerReleased += OnReleased;
        _shield.PointerCanceled += OnLost;
        _shield.PointerCaptureLost += OnLost;
        _shield.PointerExited += (_, _) => { if (!_pressed) ClearHover(); };
        _shield.RightTapped += (_, e) =>
        {
            var (z, idx, _, _) = Aim(e.GetPosition(_host));
            if (z != Zone.Slot) return;
            ShowAssign(idx);
            e.Handled = true;
        };

        _assign.Tick += (_, _) =>
        {
            _assign.Stop();
            if (_assignSlot < 0) return;
            int s = _assignSlot; _assignSlot = -1;
            ShowAssign(s);
        };
        _host.Loaded += (_, _) => { Place(); HookKeys(); };
        if (_host.IsLoaded) { Place(); HookKeys(); }
    }

    // The digit shortcuts have to work wherever focus happens to be, so the
    // handler rides the window's root content.
    private void HookKeys()
    {
        if (_keyTarget != null) return;
        if (_host.XamlRoot?.Content is not UIElement top) return;
        _keyTarget = top;
        top.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_on) return;
        // 1-9 then 0 fire the slots clockwise from twelve o'clock - the keyboard
        // and assistive path onto the same commit funnel a tap uses.
        int n = e.Key == Windows.System.VirtualKey.Number0 ? 9 : (int)e.Key - (int)Windows.System.VirtualKey.Number1;
        if (n is >= 0 and < Slots) { Commit(n); e.Handled = true; }
    }

    // ===================================================================
    // Public API - the whole surface MainWindow talks to
    // ===================================================================

    /// <summary>Show or hide the dial. Takes effect immediately, both ways. The
    /// gallery calls this with false: the dial belongs over a page and nowhere
    /// else (UI-SPEC-V3 A.2). GalleryPanel is a sibling INSIDE CanvasArea while
    /// this layer sits at ZIndex 60, so nothing else would ever have covered
    /// it - the dial floated on top of the gallery.</summary>
    public void SetVisible(bool on)
    {
        if (_on == on) { if (on) { Place(); Refresh(); } return; }
        _on = on;
        if (!on)
        {
            ClearHover();
            _dragSet = -1;
            _pressed = false;
            _pointer = null;
            _preview.Visibility = Visibility.Collapsed;
            _bottom.Visibility = Visibility.Collapsed;
            _taken = new HashSet<string>(StringComparer.Ordinal);
            SlotsChanged?.Invoke(_taken);      // the top bar takes its buttons back
            // Collapse NOW rather than on an animation's Completed: a storyboard
            // that never runs would otherwise leave the dial floating over the
            // gallery, which is the very defect this call exists to fix.
            _layer.Visibility = Visibility.Collapsed;
            return;
        }
        _layer.Visibility = Visibility.Visible;
        // Force the next Refresh to re-announce the slots even if the set is
        // unchanged, so the top bar is always re-trimmed on the way back in.
        _taken = new HashSet<string>(StringComparer.Ordinal) { " " };
        Place();
        Refresh();
        Drop();
    }

    /// <summary>The web build's "radial gravity drop": scale3d 0.5 -> 1 with
    /// opacity, over 160 ms on cubic-bezier(0.16, 1, 0.3, 1)
    /// (index.css .animate-gravity-drop). WinUI's easing functions cannot
    /// express an arbitrary cubic bezier, so the curve is a spline key frame,
    /// which can. The dial always LANDS at its resting state first, so a
    /// storyboard that never runs costs nothing but the motion.</summary>
    private void Drop()
    {
        var st = (ScaleTransform)_wheel.RenderTransform;
        st.ScaleX = st.ScaleY = _scale;
        _wheel.Opacity = 1;
        if (_h.ReduceMotion()) return;
        try
        {
            var span = TimeSpan.FromMilliseconds(160);
            var sb = new Storyboard();
            void Track(DependencyObject target, string prop, double a, double b)
            {
                var anim = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
                anim.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = a });
                anim.KeyFrames.Add(new SplineDoubleKeyFrame
                {
                    KeyTime = span,
                    Value = b,
                    KeySpline = new KeySpline { ControlPoint1 = new Point(0.16, 1), ControlPoint2 = new Point(0.3, 1) }
                });
                Storyboard.SetTarget(anim, target);
                Storyboard.SetTargetProperty(anim, prop);
                sb.Children.Add(anim);
            }
            Track(st, "ScaleX", _scale * 0.5, _scale);
            Track(st, "ScaleY", _scale * 0.5, _scale);
            Track(_wheel, "Opacity", 0, 1);
            sb.Begin();
        }
        catch { }
    }

    /// <summary>Top-left dock, mirrored to the top-right when the user keeps
    /// their tools on the right, so the wheel sits under the drawing hand rather
    /// than across the page.</summary>
    private void Place()
    {
        double w = _host.ActualWidth, h = _host.ActualHeight;
        if (w <= 0 || h <= 0) return;
        const double pad = 10;
        _mirrored = string.Equals(_h.Library().PenDock, "Right", StringComparison.OrdinalIgnoreCase);
        double cx = _mirrored ? Math.Max(Half, w - Half - pad) : Half + pad;
        double cy = Math.Min(Half + pad + _topInset, Math.Max(Half, h - Half));
        _centre = new Point(cx, cy);

        // Margin, not Canvas.Left: this layer is a GRID, and a Grid ignores the
        // Canvas attached properties the shipped build was setting - which parked
        // the whole wheel at (0,0) while the hit maths believed it sat at the pad.
        _wheel.Margin = new Thickness(cx - Half, cy - Half, 0, 0);
        _shield.Margin = new Thickness(cx - RimR, cy - RimR, 0, 0);

        // concentric with the dial, so the ring reads as the dial's own halo
        _preview.Margin = new Thickness(cx - PreviewBox / 2, cy - PreviewBox / 2, 0, 0);
        PlaceGrip();
    }

    /// <summary>The ToolUiChanged subscriber: a dumb re-render of whatever the
    /// shared state now says. Never writes state.</summary>
    public void Refresh()
    {
        if (!_on) return;
        var lib = _h.Library();
        _scale = lib.TouchMode ? 1.1 : 1.0;
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
        var taken = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Slots; i++)
        {
            string id = ids[i];
            bool live = Available(id);
            bool active = IsActive(id);
            bool lit = active || _hoverSlot == i;

            _slotArc[i].Fill = new SolidColorBrush(_hoverSlot == i ? Tint(accent, 0.85) : active ? accent : slotCol);
            _slotArc[i].Stroke = new SolidColorBrush(_hoverSlot == i ? accent : edge);
            _slotArc[i].StrokeThickness = _hoverSlot == i ? 2 : 1;

            var fg = lit ? (ColorUtil.IsDark(accent) ? Colors.White : Color.FromArgb(255, 0x14, 0x14, 0x13)) : ink;

            _slotArt[i].Children.Clear();
            var art = SlotArt(id, fg, active);
            if (art != null) _slotArt[i].Children.Add(art);

            // Pens carry their own size number on the rim; tools and commands do not.
            var pen = PenOf(id);
            _slotNum[i].Text = pen != null ? $"{pen.Size:0.#}px" : "";
            _slotNum[i].Foreground = new SolidColorBrush(lit ? fg : dim);

            // UNAVAILABLE == FULLY TRANSPARENT. Not collapsed (the sector keeps
            // its place in the ring and its hit region), not dimmed - the sector,
            // its mark and its label all go to zero alpha together.
            double a = live ? 1 : 0;
            _slotArc[i].Opacity = a;
            _slotArt[i].Opacity = a;
            _slotNum[i].Opacity = a;

            string? bar = TopBarKey(id);
            if (bar != null) taken.Add(bar);
        }
        if (!taken.SetEquals(_taken)) { _taken = taken; SlotsChanged?.Invoke(_taken); }

        // ---- the three setting sectors ----
        var ap = ToolPen();
        bool eraser = _h.ToolTag() == "Eraser";
        bool[] enabled = { SetEnabled(0), SetEnabled(1), SetEnabled(2) };
        double[] frac =
        {
            enabled[0] ? (eraser ? Norm01(lib.EraserSize, 0, 80) : Norm01(ap!.Size, 1, 24)) : 0,
            enabled[1] ? Math.Clamp(ap!.Opacity, 0, 1) : 0,
            enabled[2] ? ap!.Stabiliser : 0,
        };
        string[] read =
        {
            enabled[0] ? (eraser ? (lib.EraserSize <= 0 ? Loc.T("Wheel.Auto") : $"{lib.EraserSize:0} px") : $"{ap!.Size:0.#} px") : "-",
            enabled[1] ? $"{ap!.Opacity * 100:0}%" : "-",
            enabled[2] ? $"{ap!.Stabiliser * 100:0}%" : "-",
        };

        for (int i = 0; i < 3; i++)
        {
            bool en = enabled[i];
            _setArc[i].Fill = new SolidColorBrush(_hoverSet == i && en ? Tint(accent, 0.26)
                : dark ? Color.FromArgb(0x55, 0x00, 0x00, 0x00) : Color.FromArgb(0x14, 0x14, 0x14, 0x13));
            _setArc[i].Stroke = new SolidColorBrush(edge);
            _setArc[i].Opacity = en ? 1 : 0.45;      // greyed, never hidden

            // The progress track: an arc from the sector's zero end, swept
            // clockwise by the value. Replaced wholesale, never mutated.
            var (a0, _) = SetRange(i);
            _setFill[i].Data = StrokeArc(a0, a0 - SetSpan * Math.Clamp(frac[i], 0, 1), InOut - 6);
            _setFill[i].Stroke = new SolidColorBrush(en ? accent : dim);
            _setFill[i].Opacity = en ? 1 : 0.35;

            _setIcon[i].Children.Clear();
            var glyph = SetGlyph(i, en ? ink : dim);
            if (glyph != null) _setIcon[i].Children.Add(glyph);
            _setIcon[i].Opacity = en ? 1 : 0.5;

            _setText[i].Text = read[i];
            _setText[i].Foreground = new SolidColorBrush(en ? ink : dim);
            _setText[i].Opacity = en ? 1 : 0.6;
        }

        // ---- the centre colour disc ----
        var swatch = ActiveColour();
        _colour.Fill = new SolidColorBrush(swatch);
        _colour.Stroke = new SolidColorBrush(_hoverColour ? accent : edge);
        _colour.StrokeThickness = _hoverColour ? 4 : 3;
        _grip.Fill = new SolidColorBrush(dim);

        BuildToolOptions(ink, edge, bodyCol, accent);
        if (_dragSet >= 0) _preview.Invalidate();
    }

    // ===================================================================
    // Geometry helpers - math convention, y flipped for the screen
    // ===================================================================
    private static double Norm360(double d) => ((d % 360) + 360) % 360;
    private static double Norm01(double v, double lo, double hi) => Math.Clamp((v - lo) / (hi - lo), 0, 1);

    private static Point P(double deg, double r)
    {
        double t = deg * Math.PI / 180;
        return new Point(Half + r * Math.Cos(t), Half - r * Math.Sin(t));
    }

    private static (double A0, double A1) SetRange(int i) => i switch
    {
        0 => (SizeA0, SizeA1),
        1 => (OpacA0, OpacA1),
        _ => (SmoothA0, SmoothA1),
    };

    /// <summary>A true annular sector: outer arc, radial edge, inner arc back,
    /// radial edge home. This IS the button - not an icon floating on a disc.</summary>
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

    private static double SlotMid(int i) => 90 - SlotSpan * i;   // slot 0 at twelve, then clockwise

    private void BuildWheel()
    {
        // The plate the whole dial sits on - the web's r=125 background circle.
        // It also fills the 82->84 seam between the two annuli, which is what
        // makes the three rings read as one object.
        _body.Width = _body.Height = PlateR * 2;
        _body.StrokeThickness = 2.5;
        _body.IsHitTestVisible = false;
        Canvas.SetLeft(_body, Half - PlateR);
        Canvas.SetTop(_body, Half - PlateR);
        _wheel.Children.Add(_body);

        for (int i = 0; i < 3; i++)
        {
            var (a0, a1) = SetRange(i);
            var seg = new Path { Data = Sector(a0 - Seam, a1 + Seam, InIn, InOut), StrokeThickness = 1, IsHitTestVisible = false };
            _setArc[i] = seg;
            _wheel.Children.Add(seg);
        }
        for (int i = 0; i < 3; i++)
        {
            var fill = new Path { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round,
                                  StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
            _setFill[i] = fill;
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

        // Slot content rides above every sector so a highlight never paints over
        // it. Each mark sits in an explicit 30x30 box centred on the sector's
        // radial midpoint - the shipped build let the Path size itself to its
        // geometry EXTENT instead, which pulled every asymmetric mark off-centre
        // inside its wedge (V3 A.7).
        for (int i = 0; i < Slots; i++)
        {
            double mid = SlotMid(i);
            var at = P(mid, IconR);
            // The size number follows the slot's POSITION, not its radius: ABOVE
            // the mark for a slot in the upper half, BELOW it in the lower half
            // (spec 1.2). sin(mid) > 0 is the upper half.
            bool upper = Math.Sin(mid * Math.PI / 180) > 0;

            var g = new Grid { Width = MarkBox, Height = MarkBox, IsHitTestVisible = false };
            Canvas.SetLeft(g, at.X - MarkBox / 2);
            Canvas.SetTop(g, at.Y + (upper ? MarkUpDy : MarkDownDy) - MarkBox / 2);
            _slotArt[i] = g;
            _wheel.Children.Add(g);

            var t = new TextBlock { Width = 44, FontSize = 8.5, TextAlignment = TextAlignment.Center,
                                    IsHitTestVisible = false, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
            Canvas.SetLeft(t, at.X - 22);
            Canvas.SetTop(t, at.Y + (upper ? TextUpDy : TextDownDy) - 6);
            _slotNum[i] = t;
            _wheel.Children.Add(t);
        }

        // Setting mark + readout, laid out side by side at the web's own
        // offsets (RadialDial.jsx: translate(112,69) / (160,150) / (62,150),
        // 13px mark, text 16 to its right on the same line).
        var setAt = new[] { new Point(112, 69), new Point(160, 150), new Point(62, 150) };
        for (int i = 0; i < 3; i++)
        {
            var a = setAt[i];
            var g = new Grid { Width = 13, Height = 13, IsHitTestVisible = false };
            Canvas.SetLeft(g, a.X);
            Canvas.SetTop(g, a.Y);
            _setIcon[i] = g;
            _wheel.Children.Add(g);

            var t = new TextBlock { Width = 48, FontSize = 9.5, TextAlignment = TextAlignment.Left,
                                    IsHitTestVisible = false, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
            Canvas.SetLeft(t, a.X + 16);
            Canvas.SetTop(t, a.Y - 2);
            _setText[i] = t;
            _wheel.Children.Add(t);
        }

        _colour.Width = _colour.Height = ColourR * 2;
        _colour.StrokeThickness = 3;          // the web's 3px collar
        _colour.IsHitTestVisible = false;
        Canvas.SetLeft(_colour, Half - ColourR);
        Canvas.SetTop(_colour, Half - ColourR);
        _wheel.Children.Add(_colour);

        // Handedness grip - the app's own six-dot grip mark as vector geometry.
        _grip.Data = Icons.Geo(
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
        var at = P(_mirrored ? 135 : 45, GripR);
        Canvas.SetLeft(_grip, at.X - 8);
        Canvas.SetTop(_grip, at.Y - 8);
    }

    // ===================================================================
    // Slot bindings + persistence
    // ===================================================================

    /// <summary>The ten slot ids. An empty persisted list means "use the spec
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
        // A deleted preset empties its slot - it never reflows, because muscle
        // memory is the whole payoff.
        for (int i = 0; i < Slots; i++)
            if (outp[i].StartsWith(KindPen, StringComparison.Ordinal) && PenOf(outp[i]) == null)
                outp[i] = "";
        return outp;
    }

    /// <summary>Spec 1.2, from twelve o'clock going clockwise: pencil, fill,
    /// selection, eraser, felt-tip, text, fountain, redo, undo, standard pen.</summary>
    private string[] DefaultSlots(Library lib)
    {
        var s = new string[Slots];
        s[0] = KindPen + EnsurePen(lib, PenType.Pencil, "Pencil", "#3A3A38", 4f);
        s[1] = KindTool + "Fill";
        s[2] = KindTool + "Select";
        s[3] = KindTool + "Eraser";
        s[4] = KindPen + EnsurePen(lib, PenType.FeltTip, "Felt-tip", "#D97757", 5f);
        s[5] = KindTool + "Text";
        s[6] = KindPen + EnsurePen(lib, PenType.Fountain, "Fountain", "#141413", 5f);
        s[7] = KindCmd + "Redo";
        s[8] = KindCmd + "Undo";
        s[9] = KindPen + EnsurePen(lib, PenType.Standard, "Ink", "#141413", 3.5f);
        return s;
    }

    /// <summary>The id of the first preset of <paramref name="type"/>, creating
    /// one if the library has none. The default dial names four specific pens; a
    /// library seeded before this spec has no pencil and no felt-tip, and leaving
    /// those slots empty would ship a dial with holes in it.</summary>
    private static Guid EnsurePen(Library lib, PenType type, string name, string colour, float size)
    {
        var hit = lib.Pens.FirstOrDefault(p => p.Pen == type);
        if (hit != null) return hit.Id;
        var made = new PenPreset { Name = name, Pen = type, Color = colour, Size = size, Sens = 1f };
        lib.Pens.Add(made);
        return made.Id;
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
        if (id.StartsWith(KindCmd, StringComparison.Ordinal))
            return Extra(id[KindCmd.Length..])?.IsActive?.Invoke() ?? false;
        return false;
    }

    /// <summary>Can this slot's command run RIGHT NOW? False draws the sector
    /// fully transparent. Undo and redo are the live cases; the fill tool is
    /// declared by the spec but not yet built, so it honestly reports unavailable
    /// rather than pretending to be a button that does nothing.</summary>
    private bool Available(string id)
    {
        if (id.Length == 0) return false;
        if (id.StartsWith(KindPen, StringComparison.Ordinal)) return PenOf(id) != null;
        if (id.StartsWith(KindTool, StringComparison.Ordinal)) return id[KindTool.Length..] != "Fill";
        if (!id.StartsWith(KindCmd, StringComparison.Ordinal)) return false;
        string cmd = id[KindCmd.Length..];
        return cmd switch
        {
            "Undo" => _surface.UndoManager.CanUndo,
            "Redo" => _surface.UndoManager.CanRedo,
            "MouseMode" => true,
            // A donated command that reports itself unavailable draws FULLY
            // TRANSPARENT, exactly like redo with an empty stack.
            _ => Extra(cmd) is { } x && (x.IsAvailable?.Invoke() ?? true),
        };
    }

    /// <summary>The top-bar element a slot supersedes, or null (V3 A.9).</summary>
    private string? TopBarKey(string id)
    {
        if (id.StartsWith(KindPen, StringComparison.Ordinal)) return "ToolPen";
        if (id.StartsWith(KindTool, StringComparison.Ordinal))
            return id[KindTool.Length..] switch
            {
                "Text" => "ToolText",
                "Select" => "ToolSelect",
                "FreeSpace" => "ToolSpace",
                _ => null,
            };
        if (id.StartsWith(KindCmd, StringComparison.Ordinal))
            return id[KindCmd.Length..] switch
            {
                "Undo" => "BtnUndo",
                "Redo" => "BtnRedo",
                "MouseMode" => "MouseModeBtn",
                var c => Extra(c)?.TopBarKey,
            };
        return null;
    }

    // ===================================================================
    // Hit-testing - one atan2 + radius test, never a per-Path hit test.
    // Every zone is BOUNDED, which it always should have been: the shipped
    // build's slot test was `r >= OutIn - 4` with no ceiling, so a pointer
    // anywhere on the page resolved to a slot by angle alone and lit it up.
    // That is the phantom hover (V3 A.1); the shield now stops the events
    // arriving at all, and this ceiling is the belt to that pair of braces.
    // ===================================================================
    private (Zone Z, int Index, double R, double A) Aim(Point p)
    {
        double dx = p.X - _centre.X, dy = p.Y - _centre.Y;
        double r = Math.Sqrt(dx * dx + dy * dy) / _scale;
        double a = Norm360(Math.Atan2(-dy, dx) * 180 / Math.PI);
        if (r <= ColourR) return (Zone.Colour, -1, r, a);
        if (r >= OutIn - 3 && r <= OutOut + 2) return (Zone.Slot, (int)Math.Round(Norm360(90 - a) / SlotSpan) % Slots, r, a);
        if (r >= InIn - 3 && r <= InOut + 3) return (Zone.Arc, SetAt(a), r, a);
        return (Zone.None, -1, r, a);
    }

    private static int SetAt(double a)
    {
        for (int i = 0; i < 3; i++)
        {
            var (a0, _) = SetRange(i);
            if (Norm360(a0 - a) <= SetSpan) return i;
        }
        return 0;
    }

    private void ClearHover()
    {
        if (_hoverSlot < 0 && _hoverSet < 0 && !_hoverColour) return;
        _hoverSlot = -1; _hoverSet = -1; _hoverColour = false;
        Refresh();
    }

    // ===================================================================
    // Pointer state machine - all of it on the shield, none of it on the host
    // ===================================================================
    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_on) return;
        var pt = e.GetCurrentPoint(_host);
        var (z, idx, _, a) = Aim(pt.Position);
        if (z == Zone.None) return;                 // the seam at the rim: ignore, never claim

        _pointer = e.Pointer.PointerId;
        _pressed = true;
        _pressPt = pt.Position;
        _pressMs = Environment.TickCount64;
        _shield.CapturePointer(e.Pointer);

        _hoverSlot = z == Zone.Slot ? idx : -1;
        _hoverSet = z == Zone.Arc ? idx : -1;
        _hoverColour = z == Zone.Colour;

        if (z == Zone.Slot && pt.Properties.IsRightButtonPressed) { Refresh(); ShowAssign(idx); e.Handled = true; return; }
        if (z == Zone.Slot) { _assignSlot = idx; _assign.Stop(); _assign.Start(); }
        if (z == Zone.Arc && SetEnabled(idx)) { _dragSet = idx; ShowPreview(true); Scrub(idx, a); }
        else Refresh();
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_on) return;
        if (_pointer != null && e.Pointer.PointerId != _pointer) return;
        var p = e.GetCurrentPoint(_host).Position;
        if (_pressed && _assignSlot >= 0 && Dist(p, _pressPt) > TapSlop) { _assign.Stop(); _assignSlot = -1; }

        var (z, idx, _, a) = Aim(p);
        if (_dragSet >= 0) { Scrub(_dragSet, a); return; }

        int slot = z == Zone.Slot ? idx : -1;
        int set = z == Zone.Arc ? idx : -1;
        bool col = z == Zone.Colour;
        if (slot == _hoverSlot && set == _hoverSet && col == _hoverColour) return;
        if (slot != _hoverSlot) { _assign.Stop(); _assignSlot = -1; }
        _hoverSlot = slot; _hoverSet = set; _hoverColour = col;
        Refresh();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_on) return;
        _assign.Stop(); _assignSlot = -1;
        if (_pointer != null && e.Pointer.PointerId != _pointer) return;
        var p = e.GetCurrentPoint(_host).Position;
        bool wasPressed = _pressed;
        _pressed = false;
        _pointer = null;
        try { _shield.ReleasePointerCapture(e.Pointer); } catch { }
        e.Handled = true;

        if (_dragSet >= 0)
        {
            // Scrubbing ends where it ends, and the preview goes with it.
            _dragSet = -1;
            ShowPreview(false);
            Refresh();
            return;
        }
        if (!wasPressed) return;

        var (z, idx, _, _) = Aim(p);
        bool tap = Environment.TickCount64 - _pressMs < TapMs && Dist(p, _pressPt) <= TapSlop;
        if (z == Zone.Colour && tap) { ShowColourPicker(); return; }
        if (z == Zone.Slot) { Commit(idx); return; }
        Refresh();
    }

    private void OnLost(object sender, PointerRoutedEventArgs e)
    {
        _assign.Stop(); _assignSlot = -1;
        if (_pointer != null && e.Pointer.PointerId != _pointer) return;
        _pressed = false;
        _pointer = null;
        if (_dragSet >= 0) { _dragSet = -1; ShowPreview(false); }
        ClearHover();
    }

    private static double Dist(Point a, Point b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

    // ===================================================================
    // Commit - every write goes through the host, so the linear row sees it
    // ===================================================================
    private void Commit(int slot)
    {
        if (slot < 0 || slot >= Slots) return;
        string id = ResolveSlots()[slot];
        if (!Available(id)) { Refresh(); return; }   // a transparent sector is inert
        ClearHover();
        if (PenOf(id) is { } pen) { _h.ApplyPreset(pen); return; }
        if (id.StartsWith(KindTool, StringComparison.Ordinal)) { _h.SelectTool(id[KindTool.Length..]); return; }
        if (!id.StartsWith(KindCmd, StringComparison.Ordinal)) return;
        switch (id[KindCmd.Length..])
        {
            case "Undo": _surface.Undo(); break;
            case "Redo": _surface.Redo(); break;
            case "MouseMode": ShowMouseModes(); break;
            case var c when Extra(c) is { } x:
                // A donated menu opens ON THE DIAL rather than on the (now
                // hidden) top-bar button it came from.
                if (x.Flyout != null) { try { x.Flyout()?.ShowAt(_shield, ShieldAt(RimR, RimR)); } catch { } }
                else x.Run?.Invoke();
                break;
        }
        Refresh();
    }

    // ---- setting scrubbing ---------------------------------------------
    private bool SetEnabled(int i)
    {
        var ap = ToolPen();
        bool eraser = _h.ToolTag() == "Eraser";
        return i switch
        {
            0 => ap != null || eraser,      // size applies to pens and to the eraser
            1 => ap != null,                // opacity is real now: PenPreset.Opacity
            _ => ap != null,                // smoothness is a wet-ink filter
        };
    }

    private void Scrub(int i, double a)
    {
        if (!SetEnabled(i)) return;
        var (a0, _) = SetRange(i);
        double t = Math.Clamp(Norm360(a0 - a) / SetSpan, 0, 1);
        var lib = _h.Library();
        var ap = ToolPen();
        switch (i)
        {
            case 0 when _h.ToolTag() == "Eraser":
                lib.EraserSize = Math.Round(t * 80);
                _surface.EraserSize = lib.EraserSize;
                break;
            case 0 when ap != null:
                ap.Size = (float)Math.Round(1 + t * 23, 1);
                _surface.PenSize = ap.Size;
                break;
            case 1 when ap != null:
                ap.Opacity = (float)Math.Round(Math.Max(0.05, t), 2);
                _surface.PenOpacity = ap.Opacity;
                break;
            case 2 when ap != null:
                ap.Stabiliser = (float)Math.Round(t, 2);
                _surface.PenStabiliser = ap.Stabiliser;
                break;
            default: return;
        }
        _h.Save();
        Refresh();
    }

    // ===================================================================
    // The live scrub preview (spec 1.1) - a REAL circle, drawn by the REAL
    // stroke renderer through InkSurface.PreviewCircle + RenderStrokeTo, never
    // a UI ellipse. It sits just outside the rim so it never covers the sector
    // being dragged, and it goes away the moment the drag ends.
    // ===================================================================
    private void ShowPreview(bool on)
    {
        _preview.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) _preview.Invalidate();
    }

    private void DrawPreview(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var centre = new Vector2((float)PreviewBox / 2, (float)PreviewBox / 2);
        var accent = Accent();

        if (_h.ToolTag() == "Eraser")
        {
            // The eraser lays down no ink, so its preview is its ACTUAL radius -
            // still a real stroke through the same renderer, never a UI ring.
            double er = _h.Library().EraserSize > 0 ? _h.Library().EraserSize / 2 : _surface.PenSize * 1.5;
            float rr = (float)Math.Clamp(er, 8, PreviewBox / 2 - 12);
            var mark = ColorUtil.IsDark(PageGround()) ? Colors.White : Color.FromArgb(255, 0x14, 0x14, 0x13);
            var ring = new PenStroke { Pen = PenType.Monoline, Color = ColorUtil.ToHex(mark), Size = 1.6f, Sens = 1f };
            for (int i = 0; i <= 180; i++)
            {
                double th = i / 180.0 * Math.PI * 2;
                ring.Points.Add(new StrokePoint(centre.X + (float)(rr * Math.Cos(th)),
                                                centre.Y + (float)(rr * Math.Sin(th)), 0.5f));
            }
            _surface.RenderStrokeTo(ds, sender, ring);
            ds.DrawCircle(centre, rr + 4, accent, 1.5f, _guide);
            return;
        }

        // The web's own sizing: a ring concentric with the dial that grows a
        // little with the brush (RadialDial.jsx previewRadius), with a dashed
        // guide just outside the stroke's outer edge.
        float size = Math.Max(1f, _surface.PenSize);
        float radius = (float)Math.Clamp(130 + size * 0.8, 134, 180);
        _surface.RenderStrokeTo(ds, sender, _surface.PreviewCircle(centre, radius));
        ds.DrawCircle(centre, radius + size / 2 + 3, accent, 1.5f, _guide);
    }

    // dashed guide ring, the web's strokeDasharray="4 4"
    private static readonly Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle _guide =
        new() { CustomDashStyle = new float[] { 4, 4 } };

    // ===================================================================
    // Tool-specific UI (spec 1.3) - the rectangle at the bottom of the screen
    // ===================================================================
    private void BuildToolOptions(Color ink, Color edge, Color plate, Color accent)
    {
        string tool = _h.ToolTag();
        if (tool is not ("Select" or "Eraser")) { _bottom.Visibility = Visibility.Collapsed; return; }

        _bottom.Visibility = Visibility.Visible;
        _bottom.Background = new SolidColorBrush(plate);
        _bottom.BorderBrush = new SolidColorBrush(edge);
        _bottomRow.Children.Clear();

        void Toggle(string label, bool on, bool enabled, Action click)
        {
            var fg = !enabled ? Color.FromArgb(0x66, ink.R, ink.G, ink.B)
                   : on ? (ColorUtil.IsDark(accent) ? Colors.White : Color.FromArgb(255, 0x14, 0x14, 0x13))
                   : ink;
            var b = new Button
            {
                Content = new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(fg) },
                Background = new SolidColorBrush(on ? accent : Colors.Transparent),
                BorderBrush = new SolidColorBrush(edge),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 4, 10, 4),
                IsEnabled = enabled,
            };
            b.Click += (_, _) => click();
            _bottomRow.Children.Add(b);
        }

        if (tool == "Select")
        {
            // 1. lasso shape, 2. partial vs complete, 3. layer scope.
            Toggle(Loc.T(_surface.LassoSquare ? "Wheel.Sel.Square" : "Wheel.Sel.Freeform"), _surface.LassoSquare, true,
                   () => { _surface.LassoSquare = !_surface.LassoSquare; Refresh(); });
            Toggle(Loc.T(_surface.SelectPartial ? "Wheel.Sel.Partial" : "Wheel.Sel.Complete"), !_surface.SelectPartial, true,
                   () => { _surface.SelectPartial = !_surface.SelectPartial; Refresh(); });
            // Quill has no layer model yet, so the third toggle is present and
            // honest about it rather than lying about a scope it cannot change.
            Toggle(Loc.T("Wheel.Sel.Layer"), false, false, () => { });
            return;
        }

        // Eraser: the four styles InkSurface already implements
        // (NudgeRuns / SliceRuns / HardMaskRuns / SoftMaskRuns), surfaced at last.
        void Style(EraserStyle s, string key) =>
            Toggle(Loc.T(key), _surface.EraserMode == EraserMode.Point && _surface.EraserStyle == s, true,
                   () => { _surface.EraserMode = EraserMode.Point; _surface.EraserStyle = s; Refresh(); });
        Style(EraserStyle.Nudge, "Wheel.Erase.Nudge");
        Style(EraserStyle.Slice, "Wheel.Erase.Slice");
        Style(EraserStyle.HardMask, "Wheel.Erase.Hard");
        Style(EraserStyle.SoftMask, "Wheel.Erase.Soft");
        Toggle(Loc.T("Wheel.Erase.Object"), _surface.EraserMode == EraserMode.Object, true,
               () => { _surface.EraserMode = EraserMode.Object; Refresh(); });
    }

    // ---- flyouts -------------------------------------------------------

    /// <summary>Opens the COPIC wheel on the centre disc (V3 A.6). The shipped
    /// build bailed out whenever there was no ACTIVE pen preset, which is most of
    /// the time right after launch and always outside pen mode - so the disc
    /// looked dead. It now falls back to the first pen in the library.</summary>
    private void ShowColourPicker()
    {
        var ap = ActivePen() ?? _h.Library().Pens.FirstOrDefault();
        if (ap == null) return;
        var start = ColorUtil.Parse(ap.Color);
        void Apply(Color c)
        {
            ap.Color = ColorUtil.ToHex(c);
            _h.ApplyPreset(ap);   // the shared funnel: the linear row repaints too
            _h.Save();
        }
        if (ColourPickerHook != null) { ColourPickerHook(DiscRootPoint(), start, Apply, Refresh); return; }

        var picker = new ColorPicker { Color = start, IsAlphaEnabled = false, IsMoreButtonVisible = true,
                                       IsColorSliderVisible = true, IsHexInputVisible = true, Width = 288 };
        picker.ColorChanged += (_, e) => Apply(e.NewColor);
        new Flyout { Content = picker }.ShowAt(_shield, ShieldAt(RimR, RimR));
    }

    // The colour disc's centre in XamlRoot coordinates, which is what
    // ColorPickerService.Open anchors the ring to.
    private Point DiscRootPoint()
    {
        try
        {
            var t = _shield.TransformToVisual((UIElement?)_host.XamlRoot?.Content ?? _host);
            return t.TransformPoint(new Point(RimR, RimR));
        }
        catch { return _centre; }
    }

    private static FlyoutShowOptions ShieldAt(double x, double y) =>
        new() { Position = new Point(x, y), Placement = FlyoutPlacementMode.Bottom };

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
        menu.ShowAt(_shield, ShieldAt(RimR, RimR));
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
        foreach (var t in ToolKinds) Row(KindTool + t, Icons.Filled(Icons.Tool(t), Ink(), 20), Loc.T("Wheel.Tool." + t));
        foreach (var c in CmdIds()) Row(KindCmd + c, CmdArt(c, Ink(), 20), CmdLabel(c));
        Row("", null, Loc.T("Wheel.Assign.Empty"));

        var at = P(SlotMid(slot), IconR);
        fly.ShowAt(_shield, ShieldAt(at.X - (Half - RimR), at.Y - (Half - RimR)));
    }

    // ===================================================================
    // State reads
    // ===================================================================
    private PenPreset? ActivePen()
    {
        var id = _h.ActivePreset();
        return id == null ? null : _h.Library().Pens.FirstOrDefault(x => x.Id == id);
    }

    /// <summary>The preset the ACTIVE TOOL is actually using, or null when the
    /// active tool is not a pen. The setting sectors key off this so they grey
    /// out under the selection, text and free-space tools instead of reporting a
    /// pen nobody is drawing with - "grey out an arc that does not apply to the
    /// active tool" (spec 1.1).</summary>
    private PenPreset? ToolPen() => _h.ToolTag() == "Pen" ? ActivePen() : null;

    // The centre disc shows the live ink colour; a tool that has no colour of its
    // own shows WHITE, per the spec.
    private Color ActiveColour() =>
        _h.ToolTag() == "Pen" && ActivePen() is { } p ? ColorUtil.Parse(p.Color) : Colors.White;

    /// <summary>The live page's own background - what the preview ink will land
    /// on. Falls back to the dial's plate colour when no page is open.</summary>
    private Color PageGround()
    {
        try
        {
            var bg = _surface.Page?.Background;
            if (!string.IsNullOrWhiteSpace(bg)) return ColorUtil.Parse(bg!);
        }
        catch { }
        return IsDark() ? Color.FromArgb(255, 0x1C, 0x1B, 0x1F) : Color.FromArgb(255, 0xF7, 0xF5, 0xF0);
    }

    private Color Accent() { try { return ColorUtil.Parse(_h.Library().AccentColor); } catch { return Color.FromArgb(255, 0x2E, 0x94, 0xF2); } }
    private bool IsDark() => _host.ActualTheme == ElementTheme.Dark;
    private Color Ink() => IsDark() ? Color.FromArgb(255, 0xEC, 0xE9, 0xE2) : Color.FromArgb(255, 0x2A, 0x28, 0x25);
    private static Color Tint(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    // ===================================================================
    // Art - every mark comes from Helpers/Icons, the same source the top bar
    // now binds to, so the two surfaces can never drift again (V3 A.8).
    // ===================================================================
    private FrameworkElement? SlotArt(string id, Color fg, bool active)
    {
        if (id.Length == 0) return null;
        if (PenOf(id) is { } pen)
            // The active pen shows a stroke preview instead of its chip - the
            // dial's answer to "what will this actually lay down?".
            return active ? StrokePreview(pen, fg) : PenChip(pen, 26);
        if (id.StartsWith(KindTool, StringComparison.Ordinal)) return Icons.Filled(Icons.Tool(id[KindTool.Length..]), fg, 22);
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
    // preset size and its alpha following the preset opacity.
    private static FrameworkElement? StrokePreview(PenPreset p, Color fg)
    {
        try
        {
            var col = ColorUtil.Parse(p.Color);
            // On the accent highlight a same-value pen would vanish, so fall back
            // to the highlight's own foreground when contrast collapses.
            var stroke = ColorUtil.IsDark(col) == ColorUtil.IsDark(fg) ? fg : col;
            stroke.A = (byte)Math.Clamp(255 * Math.Clamp(p.Opacity, 0.08f, 1f), 20, 255);
            return new Path
            {
                Data = Icons.Geo("M2 17 C6 6 10 20 14 10 C16.5 4 19 9 22 6"),
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = Math.Clamp(p.Size / 2.4, 1.4, 5.5),
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = 26, Height = 26, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch { return null; }
    }

    private static FrameworkElement? SetGlyph(int i, Color fg) => i switch
    {
        0 => Icons.Filled(Icons.Size, fg, 20),
        1 => Icons.Filled(Icons.Opacity, fg, 20),
        _ => Icons.Stroked(Icons.Smoothness, fg, 20, 1.7),
    };

    private FrameworkElement? CmdArt(string cmd, Color fg, double size) => cmd switch
    {
        "Undo" => Icons.Filled(Icons.Undo, fg, size),
        "Redo" => Icons.Filled(Icons.Undo, fg, size, mirror: true),
        "MouseMode" => Icons.Filled(Icons.Mouse, fg, size),
        _ => Extra(cmd) is { } x
             ? (x.Stroked ? Icons.Stroked(x.Icon, fg, size, 1.7) : Icons.Filled(x.Icon, fg, size))
             : Icons.Filled(Icons.Mouse, fg, size),
    };
}
