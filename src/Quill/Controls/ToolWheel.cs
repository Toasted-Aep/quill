using System.Numerics;
using Quill.Helpers;
using Quill.Models;
using Quill.Services;
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
/// The radial tool dial, rebuilt against the measured Concepts reference
/// (docs/CONCEPTS-REF-2026-08-07.md §1, which supersedes UI-SPEC-V2 §1).
///
/// ── THE SHAPE ────────────────────────────────────────────────────────────
/// Let R be the ring's outer radius (nominally 98 DIP). Everything is a ratio
/// of R, so the whole dial scales from one number:
///
///     ring outer edge         1.00 R    hairline Outline at 40%
///     ring inner = disc edge  0.70 R
///     inner disc              0.70 R    filled Surface, no border
///     centre colour dot       0.195 R   the active pen's colour
///     ACTIVE SECTOR outer     1.19 R    the sector is PULLED OUTWARD
///
/// EIGHT sectors of 45°, sector 0 centred up-and-left and running clockwise.
/// Angles in the reference are COMPASS BEARINGS - 0° at twelve o'clock, positive
/// clockwise - and every public number in this file is stated that way, because
/// mixing the two conventions is what put the old build's sectors 45° out.
/// <see cref="Pt"/> is the single place a bearing becomes a screen point.
///
/// ── WHAT WAS MISSING ─────────────────────────────────────────────────────
/// The shipped dial had ten sectors, three concentric annuli, and no pop-out at
/// all. The pop-out is the reference's single strongest cue: the selected tool's
/// sector is REDRAWN at 1.19 R, filled OnSurface, with its icon and label
/// inverted to Surface and its outer corners rounded. You can see which tool is
/// live from across the room, which is the entire point of a radial dial and is
/// the one thing a colour-only highlight cannot do.
///
/// Beneath that: stroke-silhouette marks and size labels ROTATED TO FOLLOW THE
/// RING (so the ones at the bottom read upside-down - that is correct, and the
/// reference's text tool shows it plainly), an inner disc laid out from §1.4's
/// ratio table rather than by eye, a coloured arc on the disc rim under every
/// sector that holds a coloured tool, and undo/redo as satellites OUTSIDE the
/// ring with no sector and no background of their own.
///
/// ── THEME ────────────────────────────────────────────────────────────────
/// Every colour comes from <see cref="PageTheme"/>, which derives the whole
/// shell from the PAGE GROUND. Nothing here reads Settings.Theme and nothing
/// hard-codes a grey. §7's rule is honoured literally: on a dark ground
/// (Blueprint, Brown Paper, Darkprint) the RING goes fully transparent - only
/// separators, marks and labels survive - while the inner disc stays opaque.
///
/// ── INPUT ────────────────────────────────────────────────────────────────
/// Everything arrives on <see cref="_shield"/>, one transparent circle exactly
/// the size of the POPPED rim, plus two small satellite circles. That keeps the
/// three shipped input defects fixed: no phantom hover (events only exist inside
/// the shield), slots can be picked (the press never reaches InkSurface, so
/// InkSurface never captures and never drops the pointer mid-gesture), and the
/// dial is still not modal - one pixel beyond the shield belongs to the canvas.
///
/// Rendering is code-built <see cref="Path"/> geometry, never Win2D and never
/// inside InkSurface, so the ink renderer pays nothing for the chrome. The one
/// exception is the live scrub preview, which must go through the real stroke
/// renderer to be worth anything.
/// </summary>
public sealed class ToolWheel
{
    // ===================================================================
    // Geometry - every length a ratio of R, per §1.1
    // ===================================================================
    private const double R = 98;                   // §1.1 nominal outer radius
    // 11.2 items 3 and 4. The user is explicit that the OVERALL DIAL SIZE and
    // the COLOUR CIRCLE SIZE are both already correct, so R and DotR do not
    // move: what was wrong is the SPLIT between the two rings. The inner disc
    // comes in from 0.70 R to 0.58 R and the tools ring takes every DIP of it,
    // going from a 29.4 DIP band to 41.2 - forty percent more - which is what
    // pays for item 5's larger marks and item 8's colour bar.
    private const double RingIn = 0.58 * R;        // 56.8  ring inner = disc edge
    private const double RingOut = 1.00 * R;       // 98.0  ring outer edge
    private const double PopOut = 1.19 * R;        // 116.6 the ACTIVE sector's outer edge
    private const double DotR = 0.195 * R;         // 19.1  centre colour dot - UNCHANGED
    private const double PopCorner = 6;            // §1.2 rounded outer corners

    // 10.2 item 5 SUPERSEDES §1.6: undo and redo are no longer satellites
    // outside the ring, they are BUTTONS INSIDE THE WHEEL - on the inner disc's
    // bottom arc, the one part of §1.4's layout that was empty. The ring is
    // therefore the outermost thing the dial draws again, and the footprint
    // comes off the popped radius instead of a satellite orbit: 265 DIP across
    // rather than 301, so the dial got smaller as well as tidier.
    private const double SatSize = 21;             // the two glyphs, inside the disc
    private const double SatX = 0.28 * DiscR;      // 19.2  either side of the midline
    private const double SatY = 0.68 * DiscR;      // 46.6  below the value row
    private const double SatHit = SatSize + 8;
    private const double Half = PopOut + 16;       // 132.6
    private const double Footprint = Half * 2;

    // 10.2 item 6 SUPERSEDES §1.3's stacking order. The SIZE LABEL takes the
    // OUTER part of the cell and the STROKE SILHOUETTE the INNER part.
    //
    // "The size text is currently cut off" is the same defect stated from the
    // other side: the label sat at 0.735 R = 72, whose line box reached inward
    // to r = 65 - four DIP INSIDE the disc, which is a different fill with the
    // §1.5 colour arc painted across it, so the digits were swallowed. Nothing
    // may leave the 68.6 -> 98 band now, and the budget is spent explicitly:
    //
    //     1.4 pad | 15.0 mark box | 1.2 gap | 10.5 label line | 1.3 pad = 29.4
    //
    // which is the band exactly. The MARK is what gives, as §1.3 already said -
    // there is no arrangement of a 26 DIP silhouette and an 11 DIP label that
    // fits in 29.4 DIP, and the label is the half the user asked to be able to
    // read. LabelLine is set as an explicit LineHeight because a TextBlock's
    // default line box at 9.5 DIP is ~12.7 - taller than the glyphs need and
    // enough on its own to push the label back over the ring edge.
    // 11.2 items 5, 7, 8 and 9, as one radial budget across the 41.2 DIP band.
    // Read outward from the disc edge:
    //
    //     2.6 colour bar | 1.2 | 23.0 mark | 1.4 | 11.5 label line | 1.5 pad
    //
    // which is 41.2 exactly. The mark goes 15 -> 23 (item 5, "tool icons and
    // stroke previews are too small"), the label keeps 10 item 7's order -
    // size text OUTER, silhouette INNER - and gains a DIP of type back, and the
    // per-pen colour preview arrives at the very inner edge (item 8, "into the
    // TOOLS ring, at that ring's innermost edge, nearest the dial centre").
    // Nothing crosses 56.8 inward or 96.5 outward, which is what keeps item 7's
    // "no label may be cut off" true at every angle.
    private const double ArcStroke = 2.6;          // item 9: was 0.035 R = 3.43
    private const double ArcR = RingIn + ArcStroke / 2;                   // 58.1
    private const double MarkBox = 23;
    private const double MarkR = RingIn + ArcStroke + 1.2 + MarkBox / 2;  // 72.1
    private const double LabelSize = 10.5;
    private const double LabelLine = 11.5;
    private const double LabelR =
        RingIn + ArcStroke + 1.2 + MarkBox + 1.4 + LabelLine / 2;         // 90.8
    // Wide enough for "4352", the widest string the reference lists, and well
    // inside the 70 DIP chord a 45 degree sector spans at that radius - so a
    // long label cannot reach its neighbour's separator.
    private const double LabelW = 46;

    // §1.4 inner disc, all offsets in units of r = RingIn.
    private const double DiscR = RingIn;
    private const double Row1Y = -0.45 * DiscR;    // size glyph + readout
    private const double ColX = 0.61 * DiscR;      // smoothness left / opacity right
    private const double Row3Y = 0.42 * DiscR;     // the two values
    // Icons.Mark draws at the authored 24-grid scale instead of stretching the
    // geometry to the box - that stretch WAS the K.5 defect - so a mark that
    // does not fill its grid now comes out at its true size. The boxes grow to
    // compensate, rather than the marks being re-authored to touch the edges.
    // 9.1: the readouts and marks were about 1.5x the reference. The RING
    // geometry above is unchanged - that part was right - but this cluster comes
    // down by the same ~0.72 the Bar palette does.
    private const double SetBox = 14;              // the three property glyphs
    private const double ValueSize = 9;            // 12 x 0.72
    private const double ReadoutSize = 10;         // 13 x 0.72

    // 11.2 item 9: "the colour preview is too wide - reduce its width." The
    // arc used to span the WHOLE 45 degree cell, which reads as a coloured rim
    // rather than as that cell's colour chip. It now covers the middle 58% of
    // the cell and is 2.6 DIP thick instead of 3.4.
    private const double ArcSpanFrac = 0.58;

    // 11.2 item 11. Undo and redo moved inside the hollow centre (item 10), so
    // the two sectors they used to be worth come back as CUSTOMISABLE CELLS -
    // the user's own decision, and they ship EMPTY with a + mark rather than
    // pre-filled, which is what the reference shows for an unassigned cell.
    public const int Slots = 10;                   // was 8; 36° each
    private const double Span = 360.0 / Slots;
    private const double Sector0 = 306;            // §1.1 sector 0 up-and-left, at 36°
    // 9.2: NO gap. The sectors used to be inset by a hairline each side, which
    // rendered as eight detached wedges with dead page showing between them. The
    // reference ring is a continuous annulus DIVIDED BY LINES: neighbours share
    // an exact edge, and the separator drawn over it is the only thing between
    // them.
    private const double Seam = 0;               // hairline gap so wedges read apart

    private const int TapMs = 400;
    private const double TapSlop = 8;
    private const int AssignMs = 550;
    private const double PreviewBox = 420;

    private enum Zone { None, Dot, Size, Opacity, Smooth, Disc, Sector, Undo, Redo }

    /// <summary>The three scrubable properties, in the order §1.4 lays them out.</summary>
    private enum Prop { Size = 0, Opacity = 1, Smooth = 2 }

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
        /// <summary>BuildTwoToneChip: the exact visual the linear row draws.</summary>
        public required Func<string, string, Color, FrameworkElement> TwoTone { get; init; }
        public required Action<string> SetMouseMode { get; init; }
        public required Func<bool> ReduceMotion { get; init; }
        public required Action Save { get; init; }
    }

    /// <summary>The COPIC wheel. MainWindow points this straight at
    /// ColorPickerService.Open (rootPoint, current, onChanged, onClosed).</summary>
    /// <para>The last argument is the radius THIS control occupies around that
    /// point: 9.3 opens the wheel on the dial's colour dot and leaves the dial
    /// where it is, so the wheel must hold its own hub chrome outside the dial
    /// rather than laying it on top.</para>
    public Action<Point, Color, Action<Color>, Action?, double>? ColourPickerHook { get; set; }

    /// <summary>Raised whenever the occupied slots change (and when the dial is
    /// shown or hidden), carrying the top-bar element keys the dial has taken
    /// over, so a tool that lives in the dial is not also offered on the bar.</summary>
    public event Action<IReadOnlySet<string>>? SlotsChanged;

    private const string KindPen = "pen:", KindTool = "tool:", KindCmd = "cmd:";
    private static readonly string[] ToolKinds = { "Eraser", "Select", "Text", "FreeSpace", "Fill" };
    private static readonly string[] BuiltInCmds = { "Undo", "Redo", "MouseMode" };

    /// <summary>A top-bar command donated to the dial. The host owns the
    /// behaviour; the dial owns the sector, the mark and the top-bar hand-back.</summary>
    public sealed class ExtraCommand
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        /// <summary>Geometry from <see cref="Icons"/>, never a glyph or an emoji.</summary>
        public required string Icon { get; init; }
        public bool Stroked { get; init; }
        public string? TopBarKey { get; init; }
        public Func<bool>? IsActive { get; init; }
        public Func<bool>? IsAvailable { get; init; }
        public Action? Run { get; init; }
        public Func<FlyoutBase?>? Flyout { get; init; }
    }

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

    /// <summary>Where the ring's top edge sits below the host's top edge with no
    /// inset applied. ChromeBars converts its measured bar height into a
    /// <see cref="TopInset"/> against this, rather than hard-coding the dock.</summary>
    public static double RestingRimTop => Half + 10 - RingOut;

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

    private readonly Grid _layer;
    private readonly Ellipse _shield;          // the ring's hit surface, radius PopOut
    private readonly Canvas _wheel;
    private readonly CanvasControl _preview;
    private readonly Border _bottom;
    private readonly StackPanel _bottomRow;
    private readonly ValuePopover _popover = new();

    // ---- painted parts -------------------------------------------------
    private readonly Ellipse _shadow = new();
    private readonly Path[] _sector = new Path[Slots];
    private readonly Path[] _sep = new Path[Slots];
    private readonly Ellipse _ringEdge = new();
    private readonly Path _pop = new();            // the active sector, at 1.19 R
    // 10.2 item 7: the same geometry as _pop, in Accent, faded out over the
    // selection. "Rises AND LIGHTS UP" is two things and this is the second.
    private readonly Path _flash = new();
    // Scaled about the DIAL CENTRE, not the path's own bounds, so growing it
    // reads as the sector being pulled out of the ring along its own radius.
    private readonly ScaleTransform _popScale = new() { CenterX = Half, CenterY = Half };
    private readonly ScaleTransform _flashScale = new() { CenterX = Half, CenterY = Half };
    private readonly Canvas[] _mark = new Canvas[Slots];
    private readonly TextBlock[] _label = new TextBlock[Slots];
    // §1.2: when a sector is pulled out to 1.19 R its contents go with it.
    // Without this the mark and the label stay on the ring's own mid-band and
    // the popped wedge reads as an empty black tab with the icon stuck at its
    // base - which is what it looked like on screen.
    private readonly TranslateTransform[] _markT = new TranslateTransform[Slots];
    private readonly TranslateTransform[] _labelT = new TranslateTransform[Slots];
    private readonly Ellipse _disc = new();
    // 11.2 item 13: ONE plate, moved to whichever of the five inner controls is
    // under the pointer. One element rather than five because only one can be
    // hovered at a time, and five would each need their own theme sync.
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _hoverPlate = new();
    private readonly Path[] _rimArc = new Path[Slots];
    private readonly Ellipse _dot = new();
    private readonly Canvas _sizeGlyph = new(), _smoothGlyph = new(), _opacGlyph = new();
    private readonly TextBlock _sizeText = new(), _smoothText = new(), _opacText = new();
    private readonly Canvas _undoArt = new(), _redoArt = new();

    private bool _on;
    private bool _mirrored;
    private double _scale = 1;
    private Point _centre;
    private int _hoverSlot = -1;
    private int _active = -1;          // the popped sector, cached for the hit test
    private double _sizeRowW = 40;     // measured in LayoutSizeRow, read by PlaceHover
    private int _rose = -2;            // which sector the rise animation last played for
    // Held in a FIELD on purpose: an unrooted Storyboard can be collected while
    // it is still running, at which point it simply stops and Completed never
    // fires. Every animation below is FillBehavior.Stop over a value Refresh has
    // already written, so a collected storyboard costs the motion and nothing
    // else - but the reference is kept anyway so it normally does not happen.
    private Storyboard? _riseSb;
    private Zone _hoverZone = Zone.None;
    private Prop? _dragProp;
    private bool _scrubbing;
    private bool _pressed;
    private uint? _pointer;
    private Point _pressPt;
    private long _pressMs;
    private int _assignSlot = -1;
    private readonly DispatcherTimer _assign = new() { Interval = TimeSpan.FromMilliseconds(AssignMs) };
    private UIElement? _keyTarget;
    private HashSet<string> _taken = new(StringComparer.Ordinal);

    // ---- visibility, as a REQUEST plus a set of vetoes -------------------
    // V3 K.1 was a REPEAT regression: the dial came back over the notebook
    // gallery. The first fix put a SetVisible(false) in ShowGallery, which is
    // fragile by construction - visibility is recomputed from the dial SETTING
    // in a dozen places (tool change, page load, minimal-UI toggle, surface
    // switch...), so the very next one of those puts the dial straight back on
    // top of the gallery.
    //
    // The request and the reasons to overrule it are separate here, and -
    // this is the part that cannot silently regress - EVERY path that could put
    // the layer on screen runs through Enforce(), including Refresh() and
    // Place(). There is no code path that shows the dial without re-consulting
    // the vetoes, so a future caller cannot reintroduce the defect by adding
    // one more SetVisible(true).
    private bool _want;
    private readonly HashSet<string> _blocks = new(StringComparer.Ordinal);

    /// <summary>A host predicate that vetoes the dial - the notebook gallery and
    /// the floating Notebooks window (V3 K.1, K.6). Evaluated on every request
    /// AND on every repaint.</summary>
    public Func<bool>? IsBlocked { get; set; }

    // ===================================================================
    // Attach
    // ===================================================================
    public static ToolWheel Attach(Grid host, InkSurface surface, Host h) => new(host, surface, h);

    private ToolWheel(Grid host, InkSurface surface, Host h)
    {
        _host = host; _surface = surface; _h = h;

        _layer = new Grid { Visibility = Visibility.Collapsed };
        Canvas.SetZIndex(_layer, 60);

        _shield = HitCircle(PopOut * 2);
        _layer.Children.Add(_shield);
        // 10.2 item 5: undo and redo are inside the ring now, so they are inside
        // the shield too and Aim() resolves them like every other zone. The two
        // satellite hit circles that used to float outside are gone with them.

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

        // §1.7 - the value popover. One instance for the life of the dial: WinUI
        // allows an element exactly one parent, and re-adding a live one throws.
        _popover.ValueChanged += Refresh;
        // 10.2 item 9: the card can go down without the dial asking, so the
        // preview is driven off the card rather than off the gesture.
        _popover.Closed += SyncPreview;
        _layer.Children.Add(_popover.Element);

        _host.Children.Add(_layer);

        _host.SizeChanged += (_, _) => { if (_on) Place(); };
        // The whole shell follows the PAGE GROUND now, so a ground change is what
        // repaints the dial - not ActualThemeChanged, which no longer decides
        // anything here.
        PageTheme.Changed += OnThemeChanged;
        _surface.UndoManager.Changed += Refresh;
        ToolSurfaceService.Changed += _ => Apply();

        _shield.PointerPressed += OnPressed;
        _shield.PointerMoved += OnMoved;
        _shield.PointerReleased += OnReleased;
        _shield.PointerCanceled += OnLost;
        _shield.PointerCaptureLost += OnLost;
        _shield.PointerExited += (_, _) => { if (!_pressed) ClearHover(); };
        _shield.RightTapped += (_, e) =>
        {
            var (z, idx) = Aim(e.GetPosition(_host));
            if (z != Zone.Sector) return;
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

    private static Ellipse HitCircle(double d) => new()
    {
        Width = d,
        Height = d,
        // Transparent still hit-tests; a null Fill would not. An Ellipse
        // hit-tests to its actual ellipse, so the square's corners stay with the
        // canvas and drawing works right beside the dial.
        Fill = new SolidColorBrush(Colors.Transparent),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private void OnThemeChanged() { if (_on) Refresh(); }

    private void HookKeys()
    {
        if (_keyTarget != null) return;
        if (_host.XamlRoot?.Content is not UIElement top) return;
        _keyTarget = top;
        // handledEventsToo is DELIBERATELY false. With it true this ran even
        // after a text box had consumed the key, so typing a year into a note
        // fired sectors - and a number with the wrong digits in it silently
        // rewrote the page. The focus guard below is the belt to that brace.
        top.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), false);
    }

    /// <summary>True while a text surface owns the keyboard, so every keyboard
    /// shortcut in the app defers to typing in exactly the same way.</summary>
    private bool Typing()
    {
        try
        {
            var root = _host.XamlRoot;
            if (root == null) return false;
            return Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root)
                   is TextBox or RichEditBox or PasswordBox or AutoSuggestBox;
        }
        catch { return false; }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_on || e.Handled || Typing()) return;
        int n = (int)e.Key - (int)Windows.System.VirtualKey.Number1;
        if (n is >= 0 and < Slots) { Commit(n); e.Handled = true; }
    }

    // ===================================================================
    // Public API
    // ===================================================================

    /// <summary>Show or hide the dial. Takes effect immediately, both ways - but
    /// a request is only ever a request: the vetoes win (V3 K.1, K.6).</summary>
    public void SetVisible(bool on)
    {
        _want = on;
        Apply();
    }

    /// <summary>Veto the dial for a named reason, and lift it again. The dial
    /// stays down while ANY reason is outstanding, whatever the host asked for.</summary>
    public void Block(string reason, bool on)
    {
        if (on ? !_blocks.Add(reason) : !_blocks.Remove(reason)) return;
        Apply();
    }

    /// <summary>The single predicate. The dial is on screen if and only if this
    /// is true, and it is re-evaluated by every path that paints.</summary>
    private bool Wanted =>
        _want
        && ToolSurfaceService.IsWheel                     // the Bar is the other surface
        && _blocks.Count == 0
        && !(IsBlocked?.Invoke() ?? false);

    /// <summary>Reconciles what is on screen with <see cref="Wanted"/>. Safe to
    /// call at any time; the host calls it whenever a veto's condition may have
    /// changed without <see cref="Block"/> being used.</summary>
    public void Apply()
    {
        bool on = Wanted;
        if (_on == on) { if (on) { Place(); Refresh(); } return; }
        _on = on;
        if (!on)
        {
            Shut();
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

    private void Shut()
    {
        _popover.Close();
        ClearHover();
        _dragProp = null;
        _scrubbing = false;
        _pressed = false;
        _pointer = null;
        _preview.Visibility = Visibility.Collapsed;
        _bottom.Visibility = Visibility.Collapsed;
        _taken = new HashSet<string>(StringComparer.Ordinal);
        SlotsChanged?.Invoke(_taken);      // the top bar takes its buttons back
        // Collapse NOW rather than on an animation's Completed: a storyboard that
        // never runs would otherwise leave the dial floating over the gallery,
        // which is the very defect this exists to prevent.
        _layer.Visibility = Visibility.Collapsed;
    }

    /// <summary>The last line of defence for K.1/K.6. Called at the top of every
    /// painting entry point, so there is no way to end up visible while a veto
    /// stands - not by adding a SetVisible(true), not by calling Refresh
    /// directly, not by a repaint arriving from the theme or the undo stack.
    /// Returns false when the caller should stop.</summary>
    private bool Enforce()
    {
        if (!_on) return false;
        if (Wanted) return true;
        _on = false;
        Shut();
        return false;
    }

    /// <summary>The "radial gravity drop": scale 0.5 -> 1 with opacity over
    /// 160 ms. WinUI's easing functions cannot express an arbitrary cubic bezier,
    /// so the curve is a spline key frame, which can. The dial always LANDS at
    /// its resting state first, so a storyboard that never runs costs nothing but
    /// the motion.</summary>
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
    /// their tools on the right, so the wheel sits under the drawing hand.</summary>
    private void Place()
    {
        if (!Enforce()) return;
        double w = _host.ActualWidth, h = _host.ActualHeight;
        if (w <= 0 || h <= 0) return;
        const double pad = 10;
        _mirrored = string.Equals(_h.Library().PenDock, "Right", StringComparison.OrdinalIgnoreCase);
        double half = Half * _scale;
        double cx = _mirrored ? Math.Max(half, w - half - pad) : half + pad;
        double cy = Math.Min(half + pad + _topInset, Math.Max(half, h - half));
        _centre = new Point(cx, cy);

        // Margin, not Canvas.Left: this layer is a GRID, and a Grid ignores the
        // Canvas attached properties - which parked the whole wheel at (0,0)
        // while the hit maths believed it sat at the pad.
        _wheel.Margin = new Thickness(cx - Half, cy - Half, 0, 0);

        // The shield has to be the size the wheel is actually PAINTED at, or the
        // outer band of every sector is drawn and cannot be pressed.
        double rim = PopOut * _scale;
        _shield.Width = _shield.Height = rim * 2;
        _shield.Margin = new Thickness(cx - rim, cy - rim, 0, 0);
        _preview.Margin = new Thickness(cx - PreviewBox / 2, cy - PreviewBox / 2, 0, 0);
        PlacePopover();
    }

    /// <summary>§1.7: the popover is docked to the RIGHT of the inner disc and
    /// deliberately overlaps the ring. On a right-hand dock it goes to the left
    /// instead, or it would hang off the window it is docked against.</summary>
    private void PlacePopover()
    {
        if (!_popover.IsOpen) return;
        double x = _mirrored
            ? _centre.X - DiscR * 0.8 * _scale - ValuePopover.W
            : _centre.X + DiscR * 0.8 * _scale;
        _popover.Place(new Point(x, _centre.Y - ValuePopover.H / 2), _host.ActualWidth, _host.ActualHeight);
    }

    /// <summary>The ToolUiChanged subscriber: a dumb re-render of whatever the
    /// shared state now says. Never writes state.</summary>
    public void Refresh()
    {
        if (!Enforce()) return;
        var lib = _h.Library();
        double scaleWas = _scale;
        _scale = lib.TouchMode ? 1.1 : 1.0;
        if (Math.Abs(scaleWas - _scale) > 0.001)
        {
            ((ScaleTransform)_wheel.RenderTransform).ScaleX = _scale;
            ((ScaleTransform)_wheel.RenderTransform).ScaleY = _scale;
            Place();
        }

        // ---- the palette, all of it from the page ground -----------------
        var onSurface = PageTheme.OnSurface;
        var surface = PageTheme.Surface;
        var muted = PageTheme.OnSurfaceMuted;
        var outline = PageTheme.Outline;
        bool dark = PageTheme.IsDark;

        // §1.1: "Sector fill is Surface lightened toward the ground - near-white
        // on a paper page." §7: on a Blueprint / Brown Paper / Darkprint page the
        // RING goes fully transparent, and only separators, marks and labels
        // remain. The inner disc stays opaque in every case.
        var ringFill = dark ? Colors.Transparent : Mix(surface, PageTheme.Ground, 0.62);

        _shadow.Fill = ShadowBrush();
        _disc.Fill = new SolidColorBrush(surface);
        // §1.1 calls this "Outline at 40%". Read literally that is 0.14 x 0.40 =
        // 5.6% of OnSurface, which is not a hairline - it is nothing, and on a
        // dark ground where §7 takes the ring's fill away it is the only thing
        // left holding the outer edge. Read as "the outline token's colour at
        // 40%" it is a hairline you can actually see, which is what the
        // reference shows, so that is the reading taken.
        _ringEdge.Stroke = new SolidColorBrush(PageTheme.WithAlpha(onSurface, 92));
        _ringEdge.Fill = null;

        var ids = ResolveSlots();
        int active = -1;
        for (int i = 0; i < Slots; i++) if (IsActive(ids[i])) { active = i; break; }
        // Aim() runs on every pointer move; resolving the slots there allocated
        // an array and ran a LINQ lookup per pen, per move.
        _active = active;

        var taken = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Slots; i++)
        {
            string id = ids[i];
            bool live = Available(id);
            bool act = i == active;
            bool hover = _hoverSlot == i && !act;

            // The plain sector. The ACTIVE one is not painted here at all - it is
            // redrawn by _pop at 1.19 R, on top of everything.
            _sector[i].Fill = new SolidColorBrush(
                act ? Colors.Transparent
                : hover ? Mix(ringFill, onSurface, dark ? 0.10 : 0.07)
                : ringFill);
            _sector[i].Opacity = live ? 1 : 0;

            // §1.1: separators are hairlines in Outline from 0.70 R to 1.00 R,
            // and §7 keeps them when the ring itself has gone.
            _sep[i].Stroke = new SolidColorBrush(outline);

            // §1.3: the mark in the tool's OWN colour (grey for a non-drawing
            // tool), the size label beneath it, both rotated to follow the ring.
            // On the active sector both invert to Surface against the OnSurface
            // fill - that inversion IS the pop-out's other half.
            var fg = act ? surface : onSurface;
            _mark[i].Children.Clear();
            var art = SlotArt(id, fg, act);
            if (art != null) _mark[i].Children.Add(art);

            var pen = PenOf(id);
            _label[i].Text = pen != null ? SizeLabel(pen.Size) : "";
            _label[i].Foreground = new SolidColorBrush(act ? surface : onSurface);

            // 11.2 item 11: an EMPTY cell is not a dead cell. It carries a
            // muted + and answers a tap by opening the same assignment list a
            // press-hold does, which is the only way a user could ever fill it.
            bool empty = id.Length == 0;
            double a = live ? 1 : empty ? 0.45 : 0;
            _mark[i].Opacity = a;
            _label[i].Opacity = live ? 1 : 0;

            // Ride outward with the pop, along the sector's own radius.
            double push = act ? (PopOut - RingOut) / 2 : 0;
            var outward = Polar(SlotMid(i), push);
            _markT[i].X = outward.X; _markT[i].Y = outward.Y;
            _labelT[i].X = outward.X; _labelT[i].Y = outward.Y;

            // §1.5: a 45° arc on the disc rim, in the tool's colour, aligned to
            // its sector. Neutral tools paint nothing at all.
            var arc = SlotColour(id);
            if (live && arc is { } c)
            {
                _rimArc[i].Stroke = new SolidColorBrush(c);
                _rimArc[i].Opacity = 1;
            }
            else _rimArc[i].Opacity = 0;

            string? bar = TopBarKey(id);
            if (bar != null) taken.Add(bar);
        }
        if (!taken.SetEquals(_taken)) { _taken = taken; SlotsChanged?.Invoke(_taken); }

        // ---- §1.2 the active sector, pulled outward ----------------------
        if (active >= 0)
        {
            _pop.Data = PopGeometry(active);
            _pop.Fill = new SolidColorBrush(onSurface);
            _pop.Visibility = Visibility.Visible;
            // A SECOND geometry, not the same instance: WinUI's one-parent
            // rule covers Geometry too, and sharing it throws mid-Refresh.
            _flash.Data = PopGeometry(active);
            _flash.Fill = new SolidColorBrush(PageTheme.Accent);
        }
        else { _pop.Visibility = Visibility.Collapsed; _flash.Visibility = Visibility.Collapsed; }

        // ---- §1.4 the inner disc ----------------------------------------
        var ap = ToolPen();
        bool eraser = _h.ToolTag() == "Eraser";
        bool[] enabled = { Enabled(Prop.Size), Enabled(Prop.Opacity), Enabled(Prop.Smooth) };
        string[] read =
        {
            enabled[0] ? (eraser ? (lib.EraserSize <= 0 ? Loc.T("Wheel.Auto") : $"{lib.EraserSize:0} px") : $"{ap!.Size:0.#} px") : "-",
            enabled[1] ? $"{ap!.Opacity * 100:0}%" : "-",
            enabled[2] ? $"{ap!.Stabiliser * 100:0}%" : "-",
        };

        Glyph(_sizeGlyph, Icons.Size, enabled[0] ? onSurface : muted, stroked: false);
        Glyph(_opacGlyph, Icons.Opacity, enabled[1] ? onSurface : muted, stroked: false);
        Glyph(_smoothGlyph, Icons.Smoothness, enabled[2] ? onSurface : muted, stroked: true);
        _sizeText.Text = read[0];
        _opacText.Text = read[1];
        _smoothText.Text = read[2];
        foreach (var (t, en) in new[] { (_sizeText, enabled[0]), (_opacText, enabled[1]), (_smoothText, enabled[2]) })
        {
            t.Foreground = new SolidColorBrush(en ? onSurface : muted);
            t.Opacity = en ? 1 : 0.6;
        }
        LayoutSizeRow();

        _dot.Fill = new SolidColorBrush(ActiveColour());
        _dot.Stroke = new SolidColorBrush(_hoverZone == Zone.Dot ? PageTheme.Accent : outline);
        _dot.StrokeThickness = _hoverZone == Zone.Dot ? 3 : 2;

        // ---- 11.2 item 13: hover indicators ------------------------------
        PlaceHover(onSurface);

        // ---- 10.2 item 5: undo and redo, inside the disc -----------------
        // 11.2 item 14: the redesigned pair.
        Button(_undoArt, Icons.UndoRound, false, _surface.UndoManager.CanUndo, onSurface);
        Button(_redoArt, Icons.UndoRound, true, _surface.UndoManager.CanRedo, onSurface);

        BuildToolOptions(onSurface, outline, surface);
        _popover.Sync();
        // 10.2 item 9: the preview follows the POPOVER, not the drag.
        SyncPreview();
        // 10.2 item 7. Last, so the geometry and the fills it animates are
        // already the ones the frame will use.
        if (_rose != active) { _rose = active; if (active >= 0) Rise(); }
    }

    /// <summary>11.2 item 13: "hover indicators on opacity, size, stability,
    /// undo and redo." A soft plate behind whichever of the five the pointer is
    /// over. The colour dot keeps its own accent ring, which it already had and
    /// which reads better on a filled circle than a plate behind it would.</summary>
    private void PlaceHover(Color ink)
    {
        double w, h, cx, cy;
        switch (_hoverZone)
        {
            case Zone.Size:
                w = _sizeRowW + 16; h = SetBox + 12;
                cx = Half; cy = Half + Row1Y;
                break;
            case Zone.Smooth:
            case Zone.Opacity:
                w = 52; h = Row3Y + SetBox / 2 + 22;
                cx = Half + (_hoverZone == Zone.Smooth ? -ColX : ColX);
                cy = Half + (Row3Y - SetBox / 2) / 2 + 2;
                break;
            case Zone.Undo:
            case Zone.Redo:
                w = h = SatHit;
                cx = Half + (_hoverZone == Zone.Undo ? -SatX : SatX);
                cy = Half + SatY;
                break;
            default:
                _hoverPlate.Visibility = Visibility.Collapsed;
                return;
        }
        _hoverPlate.Width = w;
        _hoverPlate.Height = h;
        _hoverPlate.RadiusX = _hoverPlate.RadiusY = Math.Min(10, h / 2);
        _hoverPlate.Fill = new SolidColorBrush(PageTheme.WithAlpha(ink, 26));
        Canvas.SetLeft(_hoverPlate, cx - w / 2);
        Canvas.SetTop(_hoverPlate, cy - h / 2);
        _hoverPlate.Visibility = Visibility.Visible;
    }

    private void Button(Canvas host, string icon, bool mirror, bool live, Color fg)
    {
        host.Children.Clear();
        // Same treatment §1.6 gave the satellites - OnSurface when available,
        // OnSurface at 30% when not, never hidden - now that they are cells on
        // the disc rather than free-floating marks.
        var c = live ? fg : PageTheme.WithAlpha(fg, 77);
        host.Children.Add(Icons.Mark(icon, c, SatSize, mirror: mirror));
    }

    /// <summary>10.2 item 7: "on selecting a cell it rises and lights up."
    ///
    /// <para>The rise is the popped sector scaled about the DIAL'S centre from
    /// the ring's own radius up to 1.19 R, so it grows outward along its own
    /// radial midline rather than swelling in place; the light is the same
    /// wedge in Accent, fading out over it. Both animate FillBehavior.Stop over
    /// values Refresh has already written, which is what makes a storyboard
    /// that is collected or never begins cost the motion and nothing else.</para></summary>
    private void Rise()
    {
        _popScale.ScaleX = _popScale.ScaleY = 1;
        _flashScale.ScaleX = _flashScale.ScaleY = 1;
        _flash.Opacity = 0;
        _flash.Visibility = Visibility.Visible;
        if (_h.ReduceMotion()) return;
        try
        {
            _riseSb?.Stop();
            var sb = new Storyboard();
            void Track(DependencyObject t, string prop, double a, double b, int ms, double c1 = 0.16)
            {
                var anim = new DoubleAnimationUsingKeyFrames
                {
                    EnableDependentAnimation = true,
                    FillBehavior = FillBehavior.Stop,
                };
                anim.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = a });
                anim.KeyFrames.Add(new SplineDoubleKeyFrame
                {
                    KeyTime = TimeSpan.FromMilliseconds(ms),
                    Value = b,
                    KeySpline = new KeySpline { ControlPoint1 = new Point(c1, 1), ControlPoint2 = new Point(0.3, 1) }
                });
                Storyboard.SetTarget(anim, t);
                Storyboard.SetTargetProperty(anim, prop);
                sb.Children.Add(anim);
            }
            double from = RingOut / PopOut;          // start flush with the ring
            Track(_popScale, "ScaleX", from, 1, 190);
            Track(_popScale, "ScaleY", from, 1, 190);
            Track(_flashScale, "ScaleX", from, 1, 190);
            Track(_flashScale, "ScaleY", from, 1, 190);
            Track(_flash, "Opacity", 0.8, 0, 340, 0.4);
            _riseSb = sb;
            sb.Begin();
        }
        catch { }
    }

    private static void Glyph(Canvas host, string data, Color fg, bool stroked)
    {
        host.Children.Clear();
        // A stroked mark scales its pen with the box, so at 14 DIP a 2.1-unit
        // pen lands under a pixel and the wave greys out. It keeps its weight.
        host.Children.Add(Icons.Mark(data, fg, SetBox, stroked: stroked, thickness: 2.9));
    }

    /// <summary>§1.4 row 1: the size glyph and its readout are a PAIR, centred
    /// together on the disc's midline - so the pair has to be measured before it
    /// can be placed, unlike everything else here.</summary>
    private void LayoutSizeRow()
    {
        _sizeText.Measure(new Size(200, 40));
        double tw = _sizeText.DesiredSize.Width;
        double total = SetBox + 5 + tw;
        _sizeRowW = total;                     // 11.2 item 13's hover plate
        double x = Half - total / 2;
        Canvas.SetLeft(_sizeGlyph, x);
        Canvas.SetTop(_sizeGlyph, Half + Row1Y - SetBox / 2);
        Canvas.SetLeft(_sizeText, x + SetBox + 5);
        Canvas.SetTop(_sizeText, Half + Row1Y - ReadoutSize * 0.72);
    }

    // ===================================================================
    // Geometry helpers. BEARINGS: 0 at twelve o'clock, positive clockwise -
    // the reference's own convention, and the only one used in this file.
    // ===================================================================
    private static Point Polar(double bearing, double r)
    {
        double t = (bearing - 90) * Math.PI / 180;
        return new Point(r * Math.Cos(t), r * Math.Sin(t));
    }

    /// <summary>A bearing and a radius to a point in the wheel canvas.</summary>
    private static Point Pt(double bearing, double r)
    {
        var p = Polar(bearing, r);
        return new Point(Half + p.X, Half + p.Y);
    }

    private static double Norm360(double d) => ((d % 360) + 360) % 360;
    private static double Norm01(double v, double lo, double hi) => Math.Clamp((v - lo) / (hi - lo), 0, 1);

    /// <summary>The bearing of sector <paramref name="i"/>'s midline. §1.1:
    /// sector 0 is centred up-and-left, and they run clockwise from there.</summary>
    private static double SlotMid(int i) => Norm360(Sector0 + Span * i);

    /// <summary>A true annular sector, bearings sweeping clockwise from
    /// <paramref name="b0"/> to <paramref name="b1"/>.</summary>
    private static Geometry Sector(double b0, double b1, double rIn, double rOut)
    {
        bool large = Norm360(b1 - b0) > 180;
        var fig = new PathFigure { StartPoint = Pt(b0, rOut), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new ArcSegment { Point = Pt(b1, rOut), Size = new Size(rOut, rOut),
                                          SweepDirection = SweepDirection.Clockwise, IsLargeArc = large });
        fig.Segments.Add(new LineSegment { Point = Pt(b1, rIn) });
        fig.Segments.Add(new ArcSegment { Point = Pt(b0, rIn), Size = new Size(rIn, rIn),
                                          SweepDirection = SweepDirection.Counterclockwise, IsLargeArc = large });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    /// <summary>§1.2: the active sector at 1.19 R with its two OUTER corners
    /// rounded by ~6 DIP. The inner edge stays square against the disc, which is
    /// what makes the sector read as pulled out of the ring rather than as a
    /// free-floating lozenge.</summary>
    private static Geometry PopGeometry(int slot)
    {
        double mid = SlotMid(slot);
        double b0 = mid - Span / 2 + Seam, b1 = mid + Span / 2 - Seam;
        double k = PopCorner;
        double dB = k / PopOut * 180 / Math.PI;      // the corner's angular bite

        var fig = new PathFigure { StartPoint = Pt(b0, PopOut - k), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new ArcSegment { Point = Pt(b0 + dB, PopOut), Size = new Size(k, k),
                                          SweepDirection = SweepDirection.Clockwise });
        fig.Segments.Add(new ArcSegment { Point = Pt(b1 - dB, PopOut), Size = new Size(PopOut, PopOut),
                                          SweepDirection = SweepDirection.Clockwise });
        fig.Segments.Add(new ArcSegment { Point = Pt(b1, PopOut - k), Size = new Size(k, k),
                                          SweepDirection = SweepDirection.Clockwise });
        fig.Segments.Add(new LineSegment { Point = Pt(b1, RingIn) });
        fig.Segments.Add(new ArcSegment { Point = Pt(b0, RingIn), Size = new Size(RingIn, RingIn),
                                          SweepDirection = SweepDirection.Counterclockwise });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    /// <summary>An open arc, for §1.5's rim colours.</summary>
    private static Geometry Arc(double b0, double b1, double r)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure { StartPoint = Pt(b0, r), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment { Point = Pt(b1, r), Size = new Size(r, r),
                                          SweepDirection = SweepDirection.Clockwise,
                                          IsLargeArc = Norm360(b1 - b0) > 180 });
        geo.Figures.Add(fig);
        return geo;
    }

    private void BuildWheel()
    {
        // §1.1: one soft drop shadow for the whole dial (y+2, blur 12, black at
        // 18%). A radial-gradient ellipse rather than a composition DropShadow:
        // the shadow sits over a Win2D swap chain, and a real backdrop-sampling
        // effect there smears by a frame every time the ink moves under it.
        _shadow.Width = _shadow.Height = (RingOut + 14) * 2;
        _shadow.IsHitTestVisible = false;
        Canvas.SetLeft(_shadow, Half - RingOut - 14);
        Canvas.SetTop(_shadow, Half - RingOut - 14 + 2);
        _wheel.Children.Add(_shadow);

        for (int i = 0; i < Slots; i++)
        {
            double mid = SlotMid(i);
            var p = new Path
            {
                Data = Sector(mid - Span / 2 + Seam, mid + Span / 2 - Seam, RingIn, RingOut),
                IsHitTestVisible = false
            };
            _sector[i] = p;
            _wheel.Children.Add(p);
        }

        // 11.2 item 8: the colour bars used to sit ON THE DISC's rim, inside
        // the inner circle. They are part of the TOOLS ring now, so they are
        // drawn over the sector fills and the disc is drawn under both.
        _disc.Width = _disc.Height = DiscR * 2;
        _disc.IsHitTestVisible = false;
        Canvas.SetLeft(_disc, Half - DiscR);
        Canvas.SetTop(_disc, Half - DiscR);
        _wheel.Children.Add(_disc);

        for (int i = 0; i < Slots; i++)
        {
            double mid = SlotMid(i);
            // item 9: the middle ArcSpanFrac of the cell, not all of it.
            double half = Span * ArcSpanFrac / 2;
            var p = new Path
            {
                Data = Arc(mid - half, mid + half, ArcR),
                StrokeThickness = ArcStroke,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            _rimArc[i] = p;
            _wheel.Children.Add(p);
        }

        // Separators AFTER the sectors so they survive §7's transparent ring.
        for (int i = 0; i < Slots; i++)
        {
            double edge = SlotMid(i) - Span / 2;
            var fig = new PathFigure { StartPoint = Pt(edge, RingIn), IsClosed = false, IsFilled = false };
            fig.Segments.Add(new LineSegment { Point = Pt(edge, RingOut) });
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var l = new Path { Data = geo, StrokeThickness = 1, IsHitTestVisible = false };
            _sep[i] = l;
            _wheel.Children.Add(l);
        }

        _ringEdge.Width = _ringEdge.Height = RingOut * 2;
        _ringEdge.StrokeThickness = 1;
        _ringEdge.IsHitTestVisible = false;
        Canvas.SetLeft(_ringEdge, Half - RingOut);
        Canvas.SetTop(_ringEdge, Half - RingOut);
        _wheel.Children.Add(_ringEdge);

        // The popped sector paints OVER the ring and under the sector content.
        _pop.IsHitTestVisible = false;
        _pop.Visibility = Visibility.Collapsed;
        _pop.RenderTransform = _popScale;
        _wheel.Children.Add(_pop);

        // 10.2 item 7's "lights up", directly over it and under the content.
        _flash.IsHitTestVisible = false;
        _flash.Visibility = Visibility.Collapsed;
        _flash.Opacity = 0;
        _flash.RenderTransform = _flashScale;
        _wheel.Children.Add(_flash);

        // 10.2 item 6. The MARK takes the inner part of the cell and is NEVER
        // rotated - "every mark upright regardless of sector" - so its transform
        // is the bare radial translate that carries it out with the pop. The
        // LABEL takes the outer part and keeps §1.3's rotation, which is the one
        // half of §1.3 that 10.2 explicitly leaves standing: labels below the
        // horizontal midline still read upside-down, and that is correct.
        for (int i = 0; i < Slots; i++)
        {
            double mid = SlotMid(i);

            var at = Pt(mid, MarkR);
            _markT[i] = new TranslateTransform();
            var g = new Canvas
            {
                Width = MarkBox, Height = MarkBox, IsHitTestVisible = false,
                RenderTransform = _markT[i]
            };
            Canvas.SetLeft(g, at.X - MarkBox / 2);
            Canvas.SetTop(g, at.Y - MarkBox / 2);
            _mark[i] = g;
            _wheel.Children.Add(g);

            var lp = Pt(mid, LabelR);
            // Rotate FIRST, then translate: a TransformGroup applies its children
            // in order, so the translate lands in the parent's (screen) space and
            // can be a plain radial offset rather than a rotated one.
            _labelT[i] = new TranslateTransform();
            var lg = new TransformGroup();
            lg.Children.Add(new RotateTransform { Angle = mid });
            lg.Children.Add(_labelT[i]);
            var t = new TextBlock
            {
                Width = LabelW,
                FontSize = LabelSize,
                // An explicit line box. The default one is ~1.33 em, which at
                // this size is 12.7 DIP against a 10.5 DIP budget, and that
                // overflow alone is enough to push the label back over the rim.
                LineHeight = LabelLine,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = lg
            };
            Canvas.SetLeft(t, lp.X - LabelW / 2);
            Canvas.SetTop(t, lp.Y - LabelLine / 2);
            _label[i] = t;
            _wheel.Children.Add(t);
        }

        // 11.2 item 13's hover plate, under everything the disc carries so the
        // glyph and its value read on top of it rather than through it.
        _hoverPlate.RadiusX = _hoverPlate.RadiusY = 8;
        _hoverPlate.IsHitTestVisible = false;
        _hoverPlate.Visibility = Visibility.Collapsed;
        _wheel.Children.Add(_hoverPlate);

        // §1.4 inner disc. Row 1 is laid out at paint time (the glyph and its
        // readout are centred as a pair); rows 2 and 3 are fixed by the ratios.
        _sizeText.FontSize = ReadoutSize;                       // §1.4 "size readout 13 DIP semibold"
        _sizeText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _sizeText.IsHitTestVisible = false;
        _sizeGlyph.Width = _sizeGlyph.Height = SetBox;
        _sizeGlyph.IsHitTestVisible = false;
        _wheel.Children.Add(_sizeGlyph);
        _wheel.Children.Add(_sizeText);

        Put(_smoothGlyph, -ColX, 0, SetBox);
        Put(_opacGlyph, +ColX, 0, SetBox);
        PutValue(_smoothText, -ColX, Row3Y);
        PutValue(_opacText, +ColX, Row3Y);

        _dot.Width = _dot.Height = DotR * 2;
        _dot.StrokeThickness = 2;

        _dot.IsHitTestVisible = false;
        Canvas.SetLeft(_dot, Half - DotR);
        Canvas.SetTop(_dot, Half - DotR);
        _wheel.Children.Add(_dot);

        // 10.2 item 5: INSIDE the wheel, on the disc's bottom arc.
        Put(_undoArt, -SatX, SatY, SatSize);
        Put(_redoArt, +SatX, SatY, SatSize);

        void Put(Canvas c, double dx, double dy, double box)
        {
            c.Width = c.Height = box;
            c.IsHitTestVisible = false;
            Canvas.SetLeft(c, Half + dx - box / 2);
            Canvas.SetTop(c, Half + dy - box / 2);
            _wheel.Children.Add(c);
        }
        void PutValue(TextBlock t, double dx, double dy)
        {
            t.FontSize = ValueSize;                            // §1.4 "values 12 DIP semibold"
            t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            t.TextAlignment = TextAlignment.Center;
            t.Width = 56;
            t.IsHitTestVisible = false;
            Canvas.SetLeft(t, Half + dx - 28);
            Canvas.SetTop(t, Half + dy - ValueSize * 0.65);
            _wheel.Children.Add(t);
        }
    }

    private static Brush ShadowBrush()
    {
        // Rebuilt every paint, never mutated: WinUI caches GradientStop changes
        // and a live brush whose stops moved simply does not repaint.
        var b = new RadialGradientBrush { Center = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
        double solid = RingOut / (RingOut + 14);
        b.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(46, 0, 0, 0) });
        b.GradientStops.Add(new GradientStop { Offset = solid * 0.98, Color = Color.FromArgb(46, 0, 0, 0) });
        b.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(0, 0, 0, 0) });
        return b;
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    // ===================================================================
    // Slot bindings + persistence
    // ===================================================================

    /// <summary>The eight slot ids. An empty persisted list means "use the spec
    /// defaults", so an existing library.json costs nothing until the user
    /// actually customises the dial.</summary>
    private string[] ResolveSlots()
    {
        var lib = _h.Library();
        var stored = lib.WheelSlots;
        string[] outp;
        if (stored.Count == 0) outp = DefaultSlots(lib);
        else if (stored.Count == Slots)
        {
            outp = new string[Slots];
            for (int i = 0; i < Slots; i++) outp[i] = stored[i] ?? "";
        }
        else
        {
            // MIGRATION. The dial used to have TEN sectors and the spec now says
            // eight. Undo and redo are satellites in the new shape (§1.6), so
            // dropping those two from a ten-slot list is usually the whole
            // difference - and it keeps the user's own tools in their own order,
            // which is the only part of the layout muscle memory cares about.
            var kept = new List<string>();
            foreach (var s in stored)
            {
                if (kept.Count >= Slots) break;
                if (string.IsNullOrEmpty(s)) { kept.Add(""); continue; }
                if (s is KindCmd + "Undo" or KindCmd + "Redo") continue;
                kept.Add(s);
            }
            var fill = DefaultSlots(lib);
            outp = new string[Slots];
            for (int i = 0; i < Slots; i++) outp[i] = i < kept.Count ? kept[i] : fill[i];
        }
        // A deleted preset empties its slot - it never reflows, because muscle
        // memory is the whole payoff.
        for (int i = 0; i < Slots; i++)
            if (outp[i].StartsWith(KindPen, StringComparison.Ordinal) && PenOf(outp[i]) == null)
                outp[i] = "";
        return outp;
    }

    /// <summary>§1.3's reference order, clockwise from sector 0 (up-and-left):
    /// pen, smudge, eraser, selection, pen, pen, text, marker. Quill has no
    /// smudge tool, so that sector carries the pencil - the reference's own
    /// grainy-silhouette slot - and the fill tool takes the spare.
    ///
    /// <para>11.2 item 11 adds two more. They are deliberately EMPTY: the user
    /// was offered the eyedropper, the ruler and the mix tool pre-placed here
    /// and chose blank instead, so the two new cells carry a + and wait to be
    /// assigned, exactly as the reference shows an unassigned cell.</para></summary>
    private string[] DefaultSlots(Library lib)
    {
        var s = new string[Slots];
        s[0] = KindPen + EnsurePen(lib, PenType.Standard, "Ink", "#141413", 3.5f);
        s[1] = KindPen + EnsurePen(lib, PenType.Pencil, "Pencil", "#3A3A38", 4f);
        s[2] = KindTool + "Eraser";
        s[3] = KindTool + "Select";
        s[4] = KindPen + EnsurePen(lib, PenType.Fountain, "Fountain", "#2F6D4F", 5f);
        s[5] = KindPen + EnsurePen(lib, PenType.FeltTip, "Felt-tip", "#D97757", 5f);
        s[6] = KindTool + "Text";
        s[7] = KindPen + EnsurePen(lib, PenType.Marker, "Marker", "#141413", 8f);
        s[8] = "";
        s[9] = "";
        return s;
    }

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
        // Materialise whatever is on screen before writing one cell of it, or the
        // migration above would be re-derived from the stale ten-slot list every
        // time and quietly undo the user's edit.
        var now = ResolveSlots();
        lib.WheelSlots.Clear();
        lib.WheelSlots.AddRange(now);
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
            _ => Extra(cmd) is { } x && (x.IsAvailable?.Invoke() ?? true),
        };
    }

    /// <summary>§1.5: the colour a sector contributes to the disc rim, or null
    /// for a neutral tool - which paints nothing at all.</summary>
    private Color? SlotColour(string id)
    {
        if (PenOf(id) is not { } pen) return null;
        try { return ColorUtil.Parse(pen.Color); } catch { return null; }
    }

    /// <summary>The top-bar element a slot supersedes, or null.</summary>
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
    // Hit-testing - one atan2 plus a radius test, never a per-Path hit test.
    // Every zone is BOUNDED, which is what killed the phantom hover: the old
    // build's sector test had no ceiling, so a pointer anywhere on the page
    // resolved to a sector by angle alone and lit it up.
    // ===================================================================
    private (Zone Z, int Index) Aim(Point p)
    {
        double dx = (p.X - _centre.X) / _scale, dy = (p.Y - _centre.Y) / _scale;
        double r = Math.Sqrt(dx * dx + dy * dy);
        // Screen y is down, so a bearing is atan2(dx, -dy).
        double b = Norm360(Math.Atan2(dx, -dy) * 180 / Math.PI);

        if (r <= DotR) return (Zone.Dot, -1);
        // 10.2 item 5. Tested BEFORE the §1.4 property regions, or the undo
        // button would resolve to Zone.Smooth by x alone and pressing it would
        // open the smoothness card instead of undoing anything.
        if (Math.Abs(dy - SatY) <= SatHit / 2)
        {
            if (Math.Abs(dx + SatX) <= SatHit / 2) return (Zone.Undo, -1);
            if (Math.Abs(dx - SatX) <= SatHit / 2) return (Zone.Redo, -1);
        }
        if (r < DiscR)
        {
            // §1.4's own layout, read back as hit regions: the size pair across
            // the top, then smoothness left and opacity right of the dot.
            if (dy < Row1Y / 2) return (Zone.Size, -1);
            if (dx < -DiscR * 0.18) return (Zone.Smooth, -1);
            if (dx > DiscR * 0.18) return (Zone.Opacity, -1);
            return (Zone.Disc, -1);
        }

        int slot = (int)Math.Round(Norm360(b - Sector0) / Span) % Slots;
        if (r <= RingOut + 2) return (Zone.Sector, slot);
        // Beyond the ring only the POPPED sector is there to be pressed; the rest
        // of that band is empty and belongs to nobody.
        if (r <= PopOut + 2 && slot == _active) return (Zone.Sector, slot);
        return (Zone.None, -1);
    }

    private void ClearHover()
    {
        if (_hoverSlot < 0 && _hoverZone == Zone.None) return;
        _hoverSlot = -1; _hoverZone = Zone.None;
        Refresh();
    }

    // ===================================================================
    // Pointer state machine - all of it on the shield, none on the host
    // ===================================================================
    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_on || e.Handled) return;
        var pt = e.GetCurrentPoint(_host);
        var (z, idx) = Aim(pt.Position);
        if (z == Zone.None) return;                 // the empty band: never claim it

        // CAPTURE FIRST, THEN ARM. CapturePointer throws when the pointer already
        // belongs to another element or is no longer in contact; the old order
        // armed on a pointer id it did not own, and every later pointer was then
        // ignored for the rest of the session.
        bool got;
        try { got = _shield.CapturePointer(e.Pointer); }
        catch (ArgumentException) { got = false; }
        if (!got) { _pressed = false; _pointer = null; ClearHover(); return; }

        _pointer = e.Pointer.PointerId;
        _pressed = true;
        _pressPt = pt.Position;
        _pressMs = Environment.TickCount64;

        _hoverSlot = z == Zone.Sector ? idx : -1;
        _hoverZone = z;

        if (z == Zone.Sector && pt.Properties.IsRightButtonPressed) { Refresh(); ShowAssign(idx); e.Handled = true; return; }
        if (z == Zone.Sector) { _assignSlot = idx; _assign.Stop(); _assign.Start(); }

        var prop = PropOf(z);
        if (prop is { } pr && Enabled(pr))
        {
            // A TAP OPENS THE POPOVER AND CHANGES NOTHING; a DRAG scrubs. The
            // press deliberately does not scrub here: it used to, so merely
            // reaching for the popover jumped the setting to whatever the finger
            // landed on. Scrubbing begins in OnMoved, once the gesture is
            // unambiguously a drag.
            _dragProp = pr;
            _scrubbing = false;
            OpenPopover(pr);
            SyncPreview();
        }
        else { Refresh(); }
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_on) return;
        if (_pointer != null && e.Pointer.PointerId != _pointer) return;
        var p = e.GetCurrentPoint(_host).Position;
        if (_pressed && _assignSlot >= 0 && Dist(p, _pressPt) > TapSlop) { _assign.Stop(); _assignSlot = -1; }

        var (z, idx) = Aim(p);
        if (_dragProp is { } pr)
        {
            if (!_scrubbing && Dist(p, _pressPt) > TapSlop) _scrubbing = true;
            // The scrub is a straight horizontal drag now, not an angular one:
            // the three properties live in the inner DISC in §1.4, not on arcs,
            // so there is no arc to sweep. It tracks the popover's own slider
            // 1:1, which is the surface the user is looking at.
            if (_scrubbing) Scrub(pr, p);
            return;
        }

        int slot = z == Zone.Sector ? idx : -1;
        if (slot == _hoverSlot && z == _hoverZone) return;
        if (slot != _hoverSlot) { _assign.Stop(); _assignSlot = -1; }
        _hoverSlot = slot; _hoverZone = z;
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

        if (_dragProp != null)
        {
            bool scrubbed = _scrubbing;
            _dragProp = null;
            _scrubbing = false;
            // A scrub's popover closes when the finger lifts; a TAP's stays up,
            // because a tap IS the user asking for it. The test is "did this
            // gesture ever scrub", not an elapsed time - a deliberate press held
            // without moving is still a tap, and it used to close the very
            // popover it had just opened.
            if (scrubbed) _popover.Close();
            Refresh();
            return;
        }
        if (!wasPressed) return;

        var (z, idx) = Aim(p);
        bool tap = Environment.TickCount64 - _pressMs < TapMs && Dist(p, _pressPt) <= TapSlop;
        if (z == Zone.Dot && tap) { ShowColourPicker(); return; }
        if (z == Zone.Undo && tap) { _surface.Undo(); Refresh(); return; }
        if (z == Zone.Redo && tap) { _surface.Redo(); Refresh(); return; }
        if (z == Zone.Sector) { Commit(idx); return; }
        Refresh();
    }

    private void OnLost(object sender, PointerRoutedEventArgs e)
    {
        _assign.Stop(); _assignSlot = -1;
        if (_pointer != null && e.Pointer.PointerId != _pointer) return;
        _pressed = false;
        _pointer = null;
        if (_dragProp != null) { _dragProp = null; _scrubbing = false; SyncPreview(); }
        ClearHover();
    }

    private static double Dist(Point a, Point b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

    // ===================================================================
    // Commit - every write goes through the host, so the pen bar sees it too
    // ===================================================================
    private void Commit(int slot)
    {
        if (slot < 0 || slot >= Slots) return;
        string id = ResolveSlots()[slot];
        // 11.2 item 11: tapping an empty cell offers the tool list.
        if (id.Length == 0) { ClearHover(); ShowAssign(slot); return; }
        if (!Available(id)) { Refresh(); return; }
        // A value card belongs to the tool that was live when it opened, so
        // changing tool closes it rather than silently re-pointing it.
        _popover.Close();
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
                if (x.Flyout != null) { try { x.Flyout()?.ShowAt(_shield, ShieldAt()); } catch { } }
                else x.Run?.Invoke();
                break;
        }
        Refresh();
    }

    // ---- the three properties -------------------------------------------
    private static Prop? PropOf(Zone z) => z switch
    {
        Zone.Size => Prop.Size,
        Zone.Opacity => Prop.Opacity,
        Zone.Smooth => Prop.Smooth,
        _ => null,
    };

    private bool Enabled(Prop p)
    {
        var ap = ToolPen();
        bool eraser = _h.ToolTag() == "Eraser";
        return p switch
        {
            Prop.Size => ap != null || eraser,   // size applies to pens and the eraser
            _ => ap != null,                     // opacity and smoothness are pen-only
        };
    }

    private double Value(Prop p)
    {
        var lib = _h.Library();
        var ap = ToolPen();
        bool eraser = _h.ToolTag() == "Eraser";
        return p switch
        {
            Prop.Size => eraser ? Norm01(lib.EraserSize, 0, 80) : ap == null ? 0 : Norm01(ap.Size, 1, 24),
            Prop.Opacity => ap == null ? 0 : Math.Clamp(ap.Opacity, 0, 1),
            _ => ap?.Stabiliser ?? 0,
        };
    }

    /// <summary>A horizontal scrub across the popover's own width. One setter
    /// behind the scrub, the popover's slider and its chips, so the three can
    /// never disagree.</summary>
    private void Scrub(Prop p, Point at)
    {
        double reach = ValuePopover.W - 40;
        double t = Value(p) + (at.X - _pressPt.X) / reach;
        _pressPt = at;
        ApplySetting(p, Math.Clamp(t, 0, 1));
    }

    private void ApplySetting(Prop p, double t)
    {
        if (!Enabled(p)) return;
        var lib = _h.Library();
        var ap = ToolPen();
        switch (p)
        {
            case Prop.Size when _h.ToolTag() == "Eraser":
                lib.EraserSize = Math.Round(t * 80);
                _surface.EraserSize = lib.EraserSize;
                break;
            case Prop.Size when ap != null:
                ap.Size = (float)Math.Round(1 + t * 23, 1);
                _surface.PenSize = ap.Size;
                break;
            case Prop.Opacity when ap != null:
                ap.Opacity = (float)Math.Round(Math.Max(0.05, t), 2);
                _surface.PenOpacity = ap.Opacity;
                break;
            case Prop.Smooth when ap != null:
                ap.Stabiliser = (float)Math.Round(t, 2);
                _surface.PenStabiliser = ap.Stabiliser;
                break;
            default: return;
        }
        _h.Save();
        Refresh();
    }

    /// <summary>§1.7. Both surfaces open the same control, so the dial and the
    /// pen bar cannot drift.</summary>
    private void OpenPopover(Prop p)
    {
        _popover.Open(SpecFor(p), p.ToString(),
            new Point(_centre.X + DiscR * 0.8 * _scale, _centre.Y - ValuePopover.H / 2),
            _host.ActualWidth, _host.ActualHeight);
        PlacePopover();
    }

    /// <summary>The popover's contract for one property. Shared with the pen bar
    /// through <see cref="PenBar"/>, which builds the identical thing from the
    /// same numbers.</summary>
    private ValuePopover.Spec SpecFor(Prop p)
    {
        bool eraser = _h.ToolTag() == "Eraser";
        return p switch
        {
            Prop.Size => new ValuePopover.Spec
            {
                Name = Loc.T("Wheel.Set.Size"),
                ToolName = ToolName(),
                Get = () => Value(Prop.Size),
                Set = t => ApplySetting(Prop.Size, t),
                // Size is not a percentage, so its chips carry real sizes - the
                // reference's own 0/50/70/100 set is meaningless for a nib.
                Format = t => eraser ? $"{t * 80:0}" : $"{1 + t * 23:0.#}",
                Presets = eraser
                    ? new[] { 0.10, 0.25, 0.50, 1.00 }
                    : new[] { Norm01(1, 1, 24), Norm01(3, 1, 24), Norm01(8, 1, 24), Norm01(16, 1, 24) },
                Step = 1.0 / 46,
            },
            Prop.Opacity => Percent(Loc.T("Wheel.Set.Opacity"), Prop.Opacity),
            _ => Percent(Loc.T("Wheel.Set.Smoothness"), Prop.Smooth),
        };

        ValuePopover.Spec Percent(string name, Prop pr) => new()
        {
            Name = name,
            ToolName = ToolName(),
            Get = () => Value(pr),
            Set = t => ApplySetting(pr, t),
            Format = t => $"{t * 100:0}%",
            Presets = new[] { 0.0, 0.5, 0.7, 1.0 },   // §1.7's own chip set
            Step = 0.05,
        };
    }

    /// <summary>The tooltip line under the popover: the tool the value belongs
    /// to, so a card that says 55% is never ambiguous between two pens.</summary>
    private string ToolName()
    {
        if (_h.ToolTag() == "Pen" && ActivePen() is { } p && !string.IsNullOrWhiteSpace(p.Name)) return p.Name;
        return Loc.T("Wheel.Tool." + _h.ToolTag());
    }

    /// <summary>§1.3's size label. The reference's own numbers (`1280`, `13K`)
    /// are Concepts' brush units; Quill's pen size is in DIP, so the label is the
    /// size itself - the typography, the rotation and the placement are what the
    /// spec is actually describing.</summary>
    private static string SizeLabel(double size) =>
        size >= 1000 ? $"{size / 1000:0.#}K" : size.ToString("0.#");

    // ===================================================================
    // The live scrub preview - a REAL circle through the REAL stroke
    // renderer, never a UI ellipse.
    // ===================================================================
    /// <summary>10.2 item 9: "the pen preview must show whenever the size /
    /// opacity / smoothness popover is OPEN", not only while a value is being
    /// dragged. One predicate, called from every path that can change either
    /// condition - including the popover's own Closed event, since the card can
    /// be dismissed without the dial being told.</summary>
    private void SyncPreview()
    {
        bool on = _popover.IsOpen || _dragProp != null;
        _preview.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) _preview.Invalidate();
    }

    private void DrawPreview(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var centre = new Vector2((float)PreviewBox / 2, (float)PreviewBox / 2);

        if (_h.ToolTag() == "Eraser")
        {
            double er = _h.Library().EraserSize > 0 ? _h.Library().EraserSize / 2 : _surface.PenSize * 1.5;
            float rr = (float)Math.Clamp(er, 8, PreviewBox / 2 - 12);
            var ring = new PenStroke { Pen = PenType.Monoline, Color = ColorUtil.ToHex(PageTheme.OnSurface), Size = 1.6f, Sens = 1f };
            for (int i = 0; i <= 180; i++)
            {
                double th = i / 180.0 * Math.PI * 2;
                ring.Points.Add(new StrokePoint(centre.X + (float)(rr * Math.Cos(th)),
                                                centre.Y + (float)(rr * Math.Sin(th)), 0.5f));
            }
            _surface.RenderStrokeTo(ds, sender, ring);
            return;
        }

        // 11.1 item 2. Build the stroke first, ask the renderer how wide it
        // will really be, and only then choose the radius - the reverse of the
        // old order, which fixed the radius at a floor of PopOut + 22 and let
        // the pen's width decide whether any hole survived.
        //
        // The ring is laid out from the OUTSIDE in. Its outer edge sits just
        // inside the control, so nothing is ever clipped into a square; its
        // inner edge is then whatever is left, and if that is less than a
        // readable hole the whole stroke is drawn TO SCALE instead. Drawing to
        // scale is honest for a preview - it still mimics the pen's style,
        // its taper and its nib contrast - and it is the only option that
        // survives a pen size the rest of the app never expected.
        const float Edge = 8f;                    // breathing room in the box
        const float MinHole = 26f;                // the "hollow" in hollow circle
        float outer = (float)PreviewBox / 2 - Edge;   // 202
        var stroke = _surface.PreviewCircle(centre, outer * 0.5f);
        float w = Math.Max(0.5f, _surface.MaxStrokeWidth(stroke));
        // The widest ring that leaves MinHole of clear middle.
        float maxW = outer - MinHole / 2f;
        if (w > maxW)
        {
            // Too fat to draw life-size. Scale the stroke down rather than
            // clipping it - the clip IS the square the user reported.
            float k = maxW / w;
            stroke.Size *= k;
            w = maxW;
        }
        float radius = Math.Max(MinHole / 2f + w / 2f, outer - w / 2f);
        // Re-lay the points at the radius the width just decided.
        var hoop = _surface.PreviewCircle(centre, radius);
        hoop.Size = stroke.Size;
        _surface.RenderStrokeTo(ds, sender, hoop);

        if (GeometryProbe.On)
            GeometryProbe.Write("PREVIEW",
                $"tool={_h.ToolTag()} penSize={_surface.PenSize:F2} drawnSize={hoop.Size:F2} " +
                $"maxWidth={w:F2} radius={radius:F2} hole={radius - w / 2:F2} box={PreviewBox / 2:F0}");
    }

    // ===================================================================
    // Tool-specific options strip
    // ===================================================================
    private void BuildToolOptions(Color ink, Color edge, Color plate)
    {
        string tool = _h.ToolTag();
        if (tool is not ("Select" or "Eraser")) { _bottom.Visibility = Visibility.Collapsed; return; }

        _bottom.Visibility = Visibility.Visible;
        _bottom.Background = new SolidColorBrush(plate);
        _bottom.BorderBrush = new SolidColorBrush(edge);
        _bottomRow.Children.Clear();

        void Toggle(string label, bool on, bool enabled, Action click)
        {
            var fg = !enabled ? PageTheme.WithAlpha(ink, 0x66)
                   : on ? plate
                   : ink;
            var b = new Button
            {
                Content = new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(fg) },
                Background = new SolidColorBrush(on ? ink : Colors.Transparent),
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
            Toggle(Loc.T(_surface.LassoSquare ? "Wheel.Sel.Square" : "Wheel.Sel.Freeform"), _surface.LassoSquare, true,
                   () => { _surface.LassoSquare = !_surface.LassoSquare; Refresh(); });
            Toggle(Loc.T(_surface.SelectPartial ? "Wheel.Sel.Partial" : "Wheel.Sel.Complete"), !_surface.SelectPartial, true,
                   () => { _surface.SelectPartial = !_surface.SelectPartial; Refresh(); });
            Toggle(Loc.T("Wheel.Sel.Layer"), false, false, () => { });
            return;
        }

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

    /// <summary>§1.8: the COPIC wheel opens CENTRED ON THE DIAL'S CENTRE. It
    /// falls back to the first pen in the library when nothing is active, which
    /// is most of the time right after launch - the disc used to look dead.</summary>
    private void ShowColourPicker()
    {
        // The popover is about the pen's NUMBERS; the wheel is about its colour.
        // Leaving the card up underneath the ring just buried it.
        _popover.Close();
        var ap = ActivePen() ?? _h.Library().Pens.FirstOrDefault();
        if (ap == null) return;
        var start = ColorUtil.Parse(ap.Color);
        void Apply(Color c)
        {
            ap.Color = ColorUtil.ToHex(c);
            _h.ApplyPreset(ap);
            _h.Save();
        }
        if (ColourPickerHook != null)
        {
            // 9.3, which supersedes 1.8 and the judgement call made when the dial
            // was rebuilt. Relocating the dial to the middle of the viewport did
            // put the whole ring on screen, but the user asked for the opposite -
            // "centred in the middle of the radial dial / centred where the
            // colour circle is" - so the dial stays exactly where it is, and the
            // overhang is handled by the wheel shrinking and by the panels giving
            // way. The dot IS the dial's centre (1.4 row 2, x = 0), so the mount
            // point and the clearance come from the same place.
            ColourPickerHook(DotRootPoint(), start, Apply, Refresh, PopOut * _scale);
            return;
        }

        var picker = new ColorPicker { Color = start, IsAlphaEnabled = false, IsMoreButtonVisible = true,
                                       IsColorSliderVisible = true, IsHexInputVisible = true, Width = 288 };
        picker.ColorChanged += (_, e) => Apply(e.NewColor);
        new Flyout { Content = picker }.ShowAt(_shield, ShieldAt());
    }

    /// <summary>The dial's own centre in XamlRoot coordinates - §1.8's mount
    /// point for the COPIC wheel, not a hint.
    ///
    /// <para>Derived from <c>_centre</c> and the HOST's transform rather than
    /// from the shield's. The shield's own transform is only correct after a
    /// layout pass, and this is called immediately after
    /// <see cref="FocusCentre"/> has moved the dial - at which point the new
    /// margin is set but not yet arranged, so asking the shield would mount the
    /// colour wheel on the dial's OLD position.</para></summary>
    public Point DotRootPoint()
    {
        try
        {
            var t = _host.TransformToVisual((UIElement?)_host.XamlRoot?.Content ?? _host);
            var p = t.TransformPoint(_centre);
            // 10.2 item 8. The dot's own element, not the maths, so a future
            // change to _centre or to the disc layout that moved the dot
            // without moving this point would show up as a mismatch here
            // rather than as a fourth report from the user.
            if (GeometryProbe.On)
            {
                GeometryProbe.Point("DIAL-DOT", p, $"centre={_centre.X:F2},{_centre.Y:F2} scale={_scale:F2} clearance={PopOut * _scale:F2}");
                try
                {
                    var dt = _dot.TransformToVisual((UIElement?)_host.XamlRoot?.Content ?? _host);
                    GeometryProbe.Point("DIAL-DOT-ELEMENT",
                        dt.TransformPoint(new Point(_dot.Width / 2, _dot.Height / 2)),
                        $"r={_dot.Width / 2:F2}");
                }
                catch { }
            }
            return p;
        }
        catch { return _centre; }
    }

    /// <summary>The dial's bounds in host coordinates, for the shared panel
    /// overlap system - it asks rather than the dial hard-coding a dodge. The
    /// POPPED radius, since that is the furthest the ring ever reaches.</summary>
    public Rect Bounds =>
        _on ? new Rect(_centre.X - PopOut * _scale, _centre.Y - PopOut * _scale, PopOut * _scale * 2, PopOut * _scale * 2)
            : new Rect(0, 0, 0, 0);

    private static FlyoutShowOptions ShieldAt() =>
        new() { Position = new Point(PopOut, PopOut), Placement = FlyoutPlacementMode.Bottom };

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
        menu.ShowAt(_shield, ShieldAt());
    }

    /// <summary>The slot-assignment flyout: press-hold or right-click a sector.
    /// This is how the user chooses which eight tools occupy the dial.</summary>
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
        foreach (var t in ToolKinds) Row(KindTool + t, Icons.Mark(Icons.Tool(t), PageTheme.OnSurface, 20), Loc.T("Wheel.Tool." + t));
        foreach (var c in CmdIds()) Row(KindCmd + c, CmdArt(c, PageTheme.OnSurface, 20), CmdLabel(c));
        Row("", null, Loc.T("Wheel.Assign.Empty"));

        var at = Pt(SlotMid(slot), MarkR);
        fly.ShowAt(_shield, new FlyoutShowOptions
        {
            Position = new Point(at.X - (Half - PopOut), at.Y - (Half - PopOut)),
            Placement = FlyoutPlacementMode.Bottom
        });
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
    /// active tool is not a pen - so the three properties grey out rather than
    /// reporting a pen nobody is drawing with.</summary>
    private PenPreset? ToolPen() => _h.ToolTag() == "Pen" ? ActivePen() : null;

    /// <summary>The centre dot shows the live ink colour; a tool with no colour
    /// of its own shows the surface, not white - white is a page colour on a
    /// blue page and the dot would vanish.</summary>
    private Color ActiveColour() =>
        _h.ToolTag() == "Pen" && ActivePen() is { } p ? ColorUtil.Parse(p.Color) : PageTheme.SurfaceAlt;

    // ===================================================================
    // Art - every mark comes from Helpers/Icons, the same source the top bar
    // binds to, so the two surfaces can never drift again.
    // ===================================================================

    /// <summary>§1.3: the stroke silhouette for a sector, in the tool's OWN
    /// colour, grey for a non-drawing tool. On the active (popped) sector it
    /// inverts to Surface, because the sector beneath it is OnSurface.</summary>
    private FrameworkElement? SlotArt(string id, Color fg, bool inverted)
    {
        // 11.2 item 11's unassigned cell.
        if (id.Length == 0)
            return Icons.Mark(Icons.Plus, fg, MarkBox * 0.62, stroked: true, thickness: 2.2);
        if (PenOf(id) is { } pen) return PenStrokeMark(pen, fg, inverted);
        if (id.StartsWith(KindTool, StringComparison.Ordinal))
            return Icons.Mark(Icons.Tool(id[KindTool.Length..]), fg, MarkBox);
        if (id.StartsWith(KindCmd, StringComparison.Ordinal)) return CmdArt(id[KindCmd.Length..], fg, MarkBox);
        return null;
    }

    /// <summary>A pen sector shows THE STROKE THAT PEN LEAVES - a hand-authored
    /// silhouette of its mark (tapered for a nib, chisel for a marker, grainy for
    /// a pencil, even and round-ended for a ballpoint), painted in the pen's own
    /// colour. Not the pen-body chip, and not a live render.</summary>
    private static FrameworkElement? PenStrokeMark(PenPreset p, Color fg, bool inverted)
    {
        try
        {
            var ink = ColorUtil.Parse(p.Color);
            // On the popped sector the seat is OnSurface, so the pen's own colour
            // would frequently be invisible; there the mark inverts wholesale.
            // Off it, only a genuine contrast collapse forces the fallback.
            var seat = inverted ? PageTheme.OnSurface : PageTheme.Surface;
            var paint = inverted || Math.Abs(Lum(ink) - Lum(seat)) < 0.14 ? fg : ink;
            paint.A = (byte)Math.Clamp(255 * Math.Clamp(p.Opacity, 0.2f, 1f), 60, 255);
            return Icons.Mark(Icons.PenStroke(p.Pen), paint, MarkBox);
        }
        catch { return null; }
    }

    private static double Lum(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

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

    private FrameworkElement? CmdArt(string cmd, Color fg, double size) => cmd switch
    {
        "Undo" => Icons.Mark(Icons.Undo, fg, size),
        "Redo" => Icons.Mark(Icons.Undo, fg, size, mirror: true),
        "MouseMode" => Icons.Mark(Icons.Mouse, fg, size),
        _ => Extra(cmd) is { } x
             ? Icons.Mark(x.Icon, fg, size, stroked: x.Stroked, thickness: 1.9)
             : Icons.Mark(Icons.Mouse, fg, size),
    };
}
