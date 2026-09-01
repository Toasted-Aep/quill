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

/// <summary>The eight places the radial dial may sit (CONCEPTS-REF-2026-08-07
/// §17.17): the four corners, and the midpoint of each side.
///
/// <para>The order is CLOCKWISE FROM THE TOP LEFT, so a neighbour in the list is
/// a neighbour on screen. Nothing depends on the numbering, but a table that
/// reads round the window is easier to check against a screenshot than one that
/// groups the corners together.</para>
///
/// <para>Persisted by NAME in <c>Library.DialAnchor</c>, so a value written by a
/// build with a different member order still means the same corner.</para></summary>
public enum DialAnchor
{
    TopLeft = 0,
    TopCentre = 1,
    TopRight = 2,
    RightCentre = 3,
    BottomRight = 4,
    BottomCentre = 5,
    BottomLeft = 6,
    LeftCentre = 7,
}

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
    // 11.22 item 1 keeps them inside the disc but makes them HALVES OF THE
    // BOTTOM QUADRANT rather than free-floating buttons, so the hit test is the
    // wedge and these two numbers only place the glyph. They already sit on the
    // wedges' midlines - atan2 puts them at 247.6 and 292.4 degrees against
    // midlines of 247.5 and 292.5 - so nothing has to move.
    private const double SatSize = 21;             // the two glyphs, inside the disc
    private const double SatX = 0.28 * DiscR;      // 15.92  either side of the midline
    private const double SatY = 0.68 * DiscR;      // 38.65  below the value row
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

    // 17.4: the diameter of a mark's page-coloured seat.
    //
    // It has to cover the mark's INK, and the marks do not fill their 24 grid
    // evenly - scratchpad/mark_holes.py measures the reach of every one of them
    // from the box's centre and the worst is StrokeChisel at 13.53 grid units,
    // which is 25.93 DIP across at MarkBox 23. Rounded up to 26, and the two
    // clearances that bound it from either side, measured on the constants above
    // rather than guessed:
    //
    //     inner edge  MarkR - 13 = 59.14   the colour arcs reach 59.44
    //     outer edge  MarkR + 13 = 85.14   the size labels' line box starts 85.04
    //     and 29.21 DIP of clear page between one seat and the next
    //
    // Both overlap by a fraction of a DIP, and both are harmless because the
    // seat is added to the canvas FIRST - before the sectors, the arcs, the pop
    // and the marks - so everything it meets is painted over it, and the hover
    // tint still composites on top instead of being hidden underneath.
    private const double SeatSize = 26;

    // §1.4 inner disc, all offsets in units of r = RingIn.
    private const double DiscR = RingIn;
    // 14.1 SUPERSEDES §1.4's row table for all three readouts.
    //
    // The disc has to read as: SIZE ROW HIGH, the two glyphs on the horizontal
    // midline level with the colour dot, and the two values LOW AND DRAWN IN
    // TOWARD THE CENTRE - not as two vertical glyph-over-value pairs, which is
    // what §1.4's -0.45 / ±0.61 / +0.42 grid produced.
    //
    // 1. The size row moves UP, from -0.45 r to -0.62 r: "nearer the top of the
    //    inner disc, further from the colour dot". The dot's own top edge is at
    //    -0.34 r (DotR = 19.1 against DiscR = 56.8), so the gap above the dot
    //    goes from 6.5 DIP to 16 and the glyph still clears the disc's rim by
    //    13 DIP.
    private const double Row1Y = -0.62 * DiscR;    // size glyph + readout
    //
    // 16.5 SUPERSEDES 14.1 ITEM 2 AND ITS 0.33 r LIFT. The user: "move
    // stability and opacity up and to the outer side (left for stability, right
    // for opacity) and move their texts that show the percentage accordingly to
    // not overlap redo and undo."
    //
    // So the glyphs leave the horizontal midline - 14.1 put them there and 16.5
    // takes them off it - and both pairs climb the OUTER half of their own
    // quadrant. The values follow their glyphs rather than being pulled toward
    // the centre, which is what 14.1 did and what collided.
    //
    // 16.5 says not to trust these constants but to measure the result against
    // the arrows' boxes, because 14.1 named that bound correctly and then chose
    // a number that violated it. Measured (scratchpad/dial_layout.py, which
    // reproduces this arithmetic and prints every clearance):
    //
    //     undo / redo glyph boxes   x 5.42..26.42 either side, y 28.15..49.15
    //     opacity value ink "100%"  x 23.99..46.49, y 0.40..12.40
    //     -> 15.75 DIP of vertical clearance (16.11).  The two DO overlap in
    //        x, by 2.42 DIP, so the vertical figure is the whole of it. That
    //        is measured to the arrow's BOX; to the arrow's own INK it is
    //        19.86, because Icons.Mark keeps the 24 grid and UndoRound's ink
    //        starts 4.11 DIP down a 21 DIP box.  14.1's ValueY = 0.52 r left
    //        9.25 x 7.56 DIP of digits sitting ON the arrow instead.
    //
    // and the other four bounds the pair has to satisfy:
    //
    //     glyph box corner is 4.31 DIP inside the disc rim
    //     glyph box clears the size row by 5.94 DIP vertically
    //     value ink clears the colour dot by 4.88 DIP
    //     every corner of both boxes lies in bearings 45..135, the opacity
    //     section - so the 11.2 item 13 hover plate still covers them
    private const double ColX = 0.70 * DiscR;      // stability left / opacity right
    private const double ColY = -0.22 * DiscR;     // 16.5: up, off the midline
    private const double ValueX = 0.62 * DiscR;    // outward WITH the glyph
    private const double ValueY = 0.11 * DiscR;    // up WITH the glyph
    // The value TextBlock's fixed width. It is far wider than any string it
    // holds - the box is a centring device, not a bound - so every clearance
    // above is measured on the INK, not on this.
    private const double ValueW = 56;
    //
    // 16.4: "when one is unavailable, its glyph and value move to the middle of
    // that section". The section is the same annular quadrant HoverGeometry
    // draws and Aim resolves - see the quadrant table below - so its middle is
    // the mid-radius point on the quadrant's own midline.
    //
    // 17.14 SETTLES WHAT SITS THERE. 16.4's centring "is right and the user said
    // so"; what was still wrong is that the readout showed "-". With the dash
    // gone the disabled state has NO value at all, so there is no stack to
    // centre - the two offsets that balanced a 16.8 glyph over a 12.0 value line
    // (-7.00 and +9.25) described a block that no longer exists, and keeping the
    // glyph at -7 would leave it sitting 7 DIP high in its own section. THE
    // GLYPH IS THE WHOLE MARK NOW, and it centres on the section's middle.
    private const double SectionR = (DotR + DiscR) / 2;             // 37.97
    // The gap between a mark and its number, wherever the two are a row.
    private const double SetGap = 5;
    // Icons.Mark draws at the authored 24-grid scale instead of stretching the
    // geometry to the box - that stretch WAS the K.5 defect - so a mark that
    // does not fill its grid now comes out at its true size. The boxes grow to
    // compensate, rather than the marks being re-authored to touch the edges.
    // 9.1: the readouts and marks were about 1.5x the reference. The RING
    // geometry above is unchanged - that part was right - but this cluster comes
    // down by the same ~0.72 the Bar palette does.
    // 11.20 item 10. The user: "even if you press outside of the hover outline
    // but in their arc area it registers, so you just need to fix the hover
    // outline." Aim and HoverGeometry are written against the SAME quadrant
    // table (11.22 item 1) now, so the outline IS the hit region by
    // construction rather than by a second, independent measurement. The
    // previous split - a chord at half the size row, a dead column at 0.18 DiscR and two
    // 29 DIP squares - is gone with it, and so is the 46%-covered size plate and
    // the 977 DIP2 of tool ring the column plates used to paint on.
    //
    // 11.22 item 1's quadrants, as BEARINGS: this file measures 0 at twelve
    // o'clock and counts clockwise, the reference measures 0 at three o'clock
    // and counts anticlockwise, so bearing = 90 - horizon angle.
    //
    //      size      45..135 horizon  ->  315..45  bearing   (top)
    //      opacity  315..45           ->   45..135           (right)
    //      redo     270..315          ->  135..180           (bottom right)
    //      undo     225..270          ->  180..225           (bottom left)
    //      stability 135..225         ->  225..315           (left)
    private const double QuadSize = 45, QuadOpacity = 135, QuadRedo = 180,
                         QuadUndo = 225, QuadSmooth = 315;

    // 11.22 item 2: "icons for opacity, size and stability grow 20%." 14 -> 16.8.
    private const double SetBox = 16.8;            // the three property glyphs
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

    private enum Zone { None, Dot, Size, Opacity, Smooth, Sector, Undo, Redo, Grip }

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

    /// <summary>Raised whenever the dial's dock changes - by a rim drag landing
    /// (§17.17) or by <see cref="SetDock"/> - carrying the anchor it landed on.
    /// The Settings dock picker (§21) subscribes to this so a drag updates the
    /// picker live; that is the other half of the round trip <see cref="SetDock"/>
    /// completes in the opposite direction.</summary>
    public event Action<DialAnchor>? DockChanged;

    /// <summary>Reference 11.22 item 4: right-clicking a sector, and tapping an
    /// empty + cell, hand the slot to the Brushes library rather than to the
    /// flyout. The host owns that library, so it owns this; returning false
    /// falls back to <c>ShowAssign</c>, which is also what happens when the
    /// library cannot be opened at all.
    ///
    /// <para>PRESS-AND-HOLD IS DELIBERATELY NOT ROUTED HERE. The flyout is the
    /// only surface that can put a COMMAND or a named pen in a slot, or empty
    /// it, and 11.24 item 1 is explicit that the + cells' assignment route must
    /// survive this change - so the flyout keeps an entry point of its own, and
    /// <see cref="ShowSlotMenu"/> gives the library a way back to it.</para></summary>
    public Func<int, bool>? BrushPickerHook { get; set; }

    private const string KindPen = "pen:", KindTool = "tool:", KindCmd = "cmd:";
    // 11.4 items 28/29 and 10.8 add the last three. The assignment flyout and
    // the Brushes library both read this list, so a tool named here is placeable
    // in any of the ten sectors - including 11.2 item 11's two empty + cells,
    // which 11.11 rules must stay unassigned until the user fills them.
    private static readonly string[] ToolKinds =
        { "Eraser", "Select", "Text", "FreeSpace", "Fill", "Eyedropper", "Ruler", "Mix" };
    private static readonly string[] BuiltInCmds = { "Undo", "Redo", "MouseMode" };

    /// <summary>The same eight, for the LEGACY PEN ROW (CONCEPTS-REF 17.21).
    ///
    /// <para>The row gets tool cells because in fullscreen it was the only
    /// surface on screen and it had none. It takes THIS list, in THIS order,
    /// rather than a copy: two orders for one set of tools is exactly the kind
    /// of drift 17.20 was told to avoid on the other side of the same task, and
    /// the eraser being first here is why the row's existing eraser chip is
    /// already in the right place and does not move.</para>
    ///
    /// <para>Read-only on purpose - the array itself stays private so no caller
    /// can reorder the dial's sectors by writing through it.</para></summary>
    public static IReadOnlyList<string> ToolOrder => ToolKinds;

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
    // 17.19 SUPERSEDES 17.4 AND 17.18.1: one flat plate per slot, OVER the
    // sector fill, carrying the PEN'S OWN COLOUR - or white / black on a cell
    // that holds a tool, a command or nothing, since those have no colour of
    // their own to show. Both themes, one rule, no branch. See the block in
    // BuildWheel for why it moved above the sectors.
    private readonly Ellipse[] _seat = new Ellipse[Slots];
    private readonly TranslateTransform[] _seatT = new TranslateTransform[Slots];
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
    // 11.20 item 10: it is a PATH now, not a rectangle - see HoverGeometry.
    private readonly Path _hoverPlate = new();
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
    // 17.17: a grip drag. _dragMoved is separate from _dragging because a press
    // on the rim that never travels must land nowhere and write nothing.
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragFrom;
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
        // 16.3 / 16.9: which marks are live, and what the readouts say, are now a
        // function of the SELECTION as well as of the active tool, so the dial
        // re-renders when the selection changes exactly as it does when the tool
        // does. Refresh is a dumb re-render of shared state and never writes it,
        // so this cannot loop.
        SelectionState.Changed += Refresh;

        // 11.22 item 3: "clicking outside the opacity / size / stability panel
        // closes it." On the ROOT, as a handled-events-too handler, and it never
        // marks the press handled - so the dismissing press still reaches
        // whatever is underneath. Explicitly NOT a full-screen scrim: 11.19
        // removed that pattern and the user does not want page-covering
        // overlays. Hooked on Loaded because XamlRoot is null in the ctor.
        _layer.Loaded += (_, _) => HookRootPress();

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
            PickForSlot(idx);
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
        _taken = new HashSet<string>(StringComparer.Ordinal) { "\0" };
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

    // ===================================================================
    // §17.17 - the eight docks
    // ===================================================================

    /// <summary>Clearance kept at the BOTTOM edge, the counterpart of
    /// <see cref="TopInset"/> at the top.
    ///
    /// <para>Read straight off <see cref="BottomMenu.Metrics"/> rather than
    /// pushed in by a host, because those are compile-time constants and a
    /// second plumbing route would be a second thing to keep in step. The mode
    /// bar is 56 DIP of plate standing 14 DIP clear of the window's edge; 10 DIP
    /// of air above it is the same gap the top dock leaves under the chrome bar.
    ///
    /// <para><b>Reserved unconditionally, even when the bar is down.</b> The
    /// mode bar comes and goes with the TOOL - it is up for Select and for a
    /// live selection and down otherwise - so a dial that only cleared it while
    /// it was showing would hop 80 DIP up the screen every time the user picked
    /// the lasso, and back down when they picked the pen. A dock that moves on
    /// its own is worse than one parked slightly high.</para></summary>
    private static double BottomReserve =>
        BottomMenu.Metrics.BottomInset + BottomMenu.Metrics.CellHeight + 10;

    /// <summary>The dock the library says, or the pre-17.17 default when it says
    /// nothing. An unrecognised name is the default too: a tool palette is not
    /// worth failing over, which is the same reading
    /// <see cref="ToolSurfaceService.Parse"/> takes.</summary>
    private DialAnchor CurrentAnchor
    {
        get
        {
            var raw = _h.Library().DialAnchor;
            if (!string.IsNullOrWhiteSpace(raw)
                && Enum.TryParse<DialAnchor>(raw.Trim(), ignoreCase: true, out var v)
                && Enum.IsDefined(typeof(DialAnchor), v))
                return v;
            // Never dragged: the dock the dial has always had, on the pen hand's
            // side. Reading PenDock here and NOWHERE ELSE is deliberate - once
            // the user drags, the dial's position is its own setting and moving
            // the pen row must not shove it.
            return string.Equals(_h.Library().PenDock, "Right", StringComparison.OrdinalIgnoreCase)
                ? DialAnchor.TopRight : DialAnchor.TopLeft;
        }
    }

    /// <summary>Public face of <see cref="CurrentAnchor"/> (§21). A picker
    /// outside the dial needs to show where the dial ACTUALLY is - including the
    /// honest PenDock-derived default before the first drag - never a blank.</summary>
    public DialAnchor Dock => CurrentAnchor;

    /// <summary>Where a dock puts the dial's CENTRE, in host coordinates.
    ///
    /// <para>The four "centre" docks take the window's own midline; the corners
    /// take the same padded inset the dial has always had. Both are clamped so
    /// the popped rim stays on screen in a window too small to hold the dial
    /// twice over - a clamp, not a fallback, so the eight docks collapse toward
    /// each other rather than one of them vanishing.</para></summary>
    private Point AnchorPoint(DialAnchor a)
    {
        double w = _host.ActualWidth, h = _host.ActualHeight;
        const double pad = 10;
        double half = Half * _scale;
        double left = half + pad;
        double right = w - half - pad;
        double top = half + pad + _topInset;
        double bottom = h - half - pad - BottomReserve;

        double cx = a switch
        {
            DialAnchor.TopLeft or DialAnchor.LeftCentre or DialAnchor.BottomLeft => left,
            DialAnchor.TopRight or DialAnchor.RightCentre or DialAnchor.BottomRight => right,
            _ => w / 2,
        };
        double cy = a switch
        {
            DialAnchor.TopLeft or DialAnchor.TopCentre or DialAnchor.TopRight => top,
            DialAnchor.BottomLeft or DialAnchor.BottomCentre or DialAnchor.BottomRight => bottom,
            _ => h / 2,
        };
        // Keep the popped rim on screen whatever the window is doing. Math.Max
        // second so a window narrower or shorter than the dial still centres it
        // rather than pinning it off the far edge.
        cx = Math.Max(half, Math.Min(cx, Math.Max(half, w - half)));
        cy = Math.Max(half, Math.Min(cy, Math.Max(half, h - half)));
        return new Point(cx, cy);
    }

    /// <summary>Which of the eight a loose centre lands on. Straight nearest
    /// neighbour over the eight resting POINTS, not a quadrant test on the
    /// window: the docks are not evenly spread - the bottom row is 80 DIP up to
    /// clear the mode bar - and a geometric shortcut would disagree with where
    /// the dial actually goes.</summary>
    private DialAnchor NearestAnchor(Point p)
    {
        var best = DialAnchor.TopLeft;
        double bestD = double.MaxValue;
        foreach (DialAnchor a in Enum.GetValues<DialAnchor>())
        {
            var q = AnchorPoint(a);
            double dx = q.X - p.X, dy = q.Y - p.Y;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = a; }
        }
        return best;
    }

    /// <summary>Land on a dock and remember it. Idempotent, and it writes only
    /// when the name actually moves - so a drag that comes back to where it
    /// started costs no save, and a first drag that lands on the dial's original
    /// corner still records it, because from then on it is the user's choice and
    /// not a reading of <c>PenDock</c>.</summary>
    private void SetAnchor(DialAnchor a)
    {
        var lib = _h.Library();
        string name = a.ToString();
        if (!string.Equals(lib.DialAnchor, name, StringComparison.Ordinal))
        {
            lib.DialAnchor = name;
            try { _h.Save(); } catch { }
        }
        Place();
        Refresh();
        // Fired on every landing, changed or not - a rim drag that returns to
        // its own dock still tells a listening picker the gesture is over, the
        // same way Refresh() above always repaints regardless of whether
        // anything moved.
        DockChanged?.Invoke(a);
    }

    /// <summary>Public face of <see cref="SetAnchor"/> (§21): the SAME semantics
    /// a rim drag gets, for a caller outside the dial - the Settings dock
    /// picker. Writes <c>Library.DialAnchor</c> only when it actually changes,
    /// then places and refreshes. Never write <c>Library.DialAnchor</c>
    /// directly from outside the dial; that desyncs the picker from the drag.</summary>
    public void SetDock(DialAnchor a) => SetAnchor(a);

    private void Place() => PlaceAt(AnchorPoint(CurrentAnchor));

    /// <summary>The one place the wheel's parts are laid out against a centre.
    /// Taken as a parameter rather than read from <see cref="_centre"/> so a drag
    /// can move the dial without first having to write its resting anchor - the
    /// snap only happens on release, and a half-finished drag must never be what
    /// the library records.</summary>
    private void PlaceAt(Point centre)
    {
        if (!Enforce()) return;
        double w = _host.ActualWidth, h = _host.ActualHeight;
        if (w <= 0 || h <= 0) return;
        // Which way the popover and the colour wheel's chrome lean. It used to be
        // the pen dock; it is now WHICH HALF OF THE WINDOW THE DIAL IS IN, which
        // is the question that was always being asked - the pen dock was just the
        // only answer available while the dial could not move. A top-centre dial
        // takes the left-hand reading, matching its resting dock.
        _mirrored = centre.X > w / 2;
        double cx = centre.X, cy = centre.Y;
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

        // ---- 17.4: what is actually BEHIND a mark ------------------------
        //
        // THE DEFECT. On a dark ground the line above gives the ring no fill at
        // all, so a sector mark has nothing behind it but the page - grain, grid
        // and all - and the marks that are authored with even-odd counters show
        // it straight through themselves: Text's bowl is 20% of the mark, the
        // eraser's worn face 16%, Mix's lens 24%. A pen's mark is worse: it is
        // painted at THE PEN'S OWN OPACITY (60..255 alpha, below), so on a
        // transparent sector it composites onto the page and is literally
        // translucent. In light mode none of this shows, because the sector
        // underneath is opaque.
        //
        // Section 0's warning, exactly: the code below used to resolve the mark's
        // seat from PageTheme.Surface - the INNER DISC's token - because the
        // ring's own has no definition on the dark side. A colour taken from the
        // wrong token is how a mark ends up judged against a backdrop it is not
        // on, and the contrast test that decides whether a pen keeps its own ink
        // was reading the disc while the mark sat on the page.
        //
        // 17.19 SETTLES IT, AND SUPERSEDES BOTH 17.4 AND 17.18.1.
        //
        // 17.4's fix was a plate of PageTheme.Ground - the page's own colour,
        // findable because the page's grain and grid stop at its edge. True, and
        // measured passing, but only on a page that HAS grain or a grid: on a
        // plain black page there is nothing to interrupt and the plate is
        // invisible by construction. 17.18.1 proposed lifting it a step in L*.
        // The user replaced both:
        //
        //     a pen cell's plate is THAT PEN'S OWN COLOUR
        //     a tool cell's plate is WHITE OR BLACK, by the page background
        //     the mark is WHITE OR BLACK, whichever contrasts with the plate
        //
        // One rule, both themes, no light/dark branch - and section 0's rule is
        // now satisfied BY CONSTRUCTION rather than by care: the mark is judged
        // against the surface it actually sits on, because the plate IS that
        // surface. There is no longer a seat colour to resolve from a token, so
        // the whole family of defects behind 17.4 - a contrast test reading the
        // disc while the mark sat on the page, counters showing the page
        // through a mark, a pen mark composited at the pen's own alpha - is
        // unreachable rather than fixed case by case.
        //
        // 17.4's OTHER half still holds: the plate is flat and carries no page
        // texture.

        // The hover tint, and why it is no longer ONE expression.
        //
        // It was Mix(ringFill, onSurface, dark ? 0.10 : 0.07) on both sides. On
        // the dark side the line above leaves the ring with no fill, and Mix -
        // alone among this file's copies of it - interpolates the ALPHA channel
        // too, so that mix ran from Colors.Transparent, i.e. from ARGB(0,0,0,0).
        // A tenth of the way toward a WHITE ink starting at a transparent BLACK
        // is ARGB(26,24,24,24): the ink's alpha carried on Transparent's own
        // primaries. The sector went DARKER under the pointer on exactly the
        // pages whose ink is white.
        //
        // Measured over the page, through the shadow the ring sits on
        // (scratchpad/hover_wash.py). In L*, because a percentage of luminance
        // says nothing on a page whose luminance is 0.003, the hover step was:
        //
        //     light grounds   -4.75 .. -5.28   the cue the reference has
        //     Blueprint       -3.23            visible, but the wrong way
        //     Brown Paper     -3.37            visible, but the wrong way
        //     Darkprint       -0.53            under one L*, i.e. nothing
        //     pinned dark     +0.30            under one L*, i.e. nothing
        //     OLED black      +0.55            #000000 -> #020202
        //
        // That is section 0's warning in its other form: ringFill has NO COLOUR
        // on this side of the theme, so a value derived from it is not the
        // ring's colour, it is Transparent's.
        //
        // Where the ring has no fill of its own, the tint is carried on the
        // INK's colour at the same weight instead - 26/255 = 10.2%, which is
        // what the dark branch already asked for, and the same wash _hoverPlate
        // uses for the disc's five controls and ChromeUi.Wash for a selected
        // chip. An alpha wash composites onto whatever the transparent ring is
        // really showing, which is the page, so it needs no opinion about what
        // that is. The same five grounds now measure +5.36, +5.48, +9.90, +10.62
        // and +8.76 L* - a lift, of the light side's own size or better, on
        // every one of them. OLED black goes #000000 -> #191919.
        //
        // THE LIGHT BRANCH IS UNTOUCHED. ringFill is opaque there, the mix never
        // saw a transparent operand, and light mode measures the same -4.75 to
        // -5.28 L* it did before.
        var hoverFill = dark ? PageTheme.WithAlpha(onSurface, 26)
                             : Mix(ringFill, onSurface, 0.07);

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
            // 11.2 item 11: an EMPTY cell is not a dead cell. It carries a
            // muted + and answers a tap by opening the same assignment list a
            // press-hold does, which is the only way a user could ever fill it.
            bool empty = id.Length == 0;

            // The plain sector. The ACTIVE one is not painted here at all - it is
            // redrawn by _pop at 1.19 R, on top of everything.
            _sector[i].Fill = new SolidColorBrush(
                act ? Colors.Transparent
                : hover ? hoverFill
                : ringFill);
            _sector[i].Opacity = live ? 1 : 0;

            // 17.19: the plate. IN BOTH THEMES AND ON EVERY SECTOR, including the
            // popped one - the pop is the SECTOR's cue and the plate is the
            // CELL's, they are different elements and both hold.
            //
            // Two cells get none. An UNAVAILABLE one (16.3) is already painting
            // no mark, and a bare coloured button with nothing on it would be
            // precisely the "live-looking control that silently does nothing"
            // that section rules out. An EMPTY one keeps 11.2 item 11's bare
            // muted + on the sector: 17.19 names pen cells and tool cells, and
            // an empty cell is neither - giving it a full white or black disc
            // would make an unassigned sector read as an assigned one, which is
            // the one thing the + exists to prevent.
            Color? plate = empty || !live ? null : PlateFor(id);
            _seat[i].Fill = new SolidColorBrush(plate ?? Colors.Transparent);
            _seat[i].Opacity = plate is null ? 0 : 1;

            // §1.1: separators are hairlines in Outline from 0.70 R to 1.00 R,
            // and §7 keeps them when the ring itself has gone.
            _sep[i].Stroke = new SolidColorBrush(outline);

            // 17.19: the mark is WHITE OR BLACK, whichever contrasts better with
            // the plate it is drawn on. §1.3's "in the tool's own colour" and
            // 17.4's inversion on the popped sector are both gone: the pen's
            // colour is the PLATE now, and a mark in the same colour on it would
            // be invisible. Measured, that choice can never do worse than
            // 4.583:1 for any colour in sRGB - see BestInk - which clears the 3:1
            // floor for non-text marks with room to spare. The + on a plateless
            // empty cell keeps OnSurface, because there it really is standing on
            // the sector and the page, and that is the token keyed to those.
            var fg = plate is { } pc ? BestInk(pc) : onSurface;
            _mark[i].Children.Clear();
            var art = SlotArt(id, fg);
            if (art != null) _mark[i].Children.Add(art);

            var pen = PenOf(id);
            _label[i].Text = pen != null ? SizeLabel(pen.Size) : "";
            _label[i].Foreground = new SolidColorBrush(act ? surface : onSurface);

            double a = live ? 1 : empty ? 0.45 : 0;
            _mark[i].Opacity = a;
            _label[i].Opacity = live ? 1 : 0;

            // Ride outward with the pop, along the sector's own radius. 17.19
            // adds the plate to the two that already did: it stands under the
            // mark, so it has to travel with it or the mark steps off it.
            double push = act ? (PopOut - RingOut) / 2 : 0;
            var outward = Polar(SlotMid(i), push);
            _markT[i].X = outward.X; _markT[i].Y = outward.Y;
            _labelT[i].X = outward.X; _labelT[i].Y = outward.Y;
            _seatT[i].X = outward.X; _seatT[i].Y = outward.Y;

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
        // 16.9: with a stroke selected the disc reports THAT STROKE's values, not
        // the active pen's - in the reference capture a selected stroke reads
        // size, stability 0% and opacity 100% while the Select tool is in hand,
        // which the pen-only path below cannot produce. A selection that
        // disagrees with itself prints an em dash: the control is still live
        // (the subject HAS the property) but there is no single number to show,
        // which is a different state from having none.
        var sel = SelectionState.Current;
        string Read(Prop p, float? v, string suffix, float scale)
        {
            if (sel.Any) return v is { } n ? $"{n * scale:0.#}{suffix}" : "—";
            return "";
        }
        // 17.14: A DISABLED READOUT SHOWS NOTHING, NOT A DASH. The "-" used to be
        // produced inside Read, which asked Enabled itself; the guard is now
        // enabled[] - the array Refresh has ALREADY read out of the one disabled
        // predicate, three lines up - so removing the dash removed a call to that
        // predicate rather than adding a second answer to the same question.
        //
        // The guard is also load-bearing for a second reason: ap is null whenever
        // the active tool is not a pen, and the pen fallbacks below dereference
        // it. Short-circuiting on enabled[] is what keeps them from being
        // evaluated at all - the dash used to do that job by being non-empty.
        string[] read =
        {
            !enabled[0] ? ""
                : Read(Prop.Size, sel.Size, " px", 1f) is { Length: > 0 } a0 ? a0
                : eraser ? (lib.EraserSize <= 0 ? Loc.T("Wheel.Auto") : $"{lib.EraserSize:0} px")
                : $"{ap!.Size:0.#} px",
            !enabled[1] ? ""
                : Read(Prop.Opacity, sel.Opacity, "%", 100f) is { Length: > 0 } a1 ? a1
                : $"{ap!.Opacity * 100:0}%",
            !enabled[2] ? ""
                : Read(Prop.Smooth, sel.Stability, "%", 100f) is { Length: > 0 } a2 ? a2
                : $"{ap!.Stabiliser * 100:0}%",
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
        // 16.5 / 16.4. Placement is state-dependent, so it happens here rather
        // than once in BuildWheel.
        LayoutSizeRow(enabled[0]);
        LayoutReadouts(_opacGlyph, _opacText, Prop.Opacity, enabled[1], +1);
        LayoutReadouts(_smoothGlyph, _smoothText, Prop.Smooth, enabled[2], -1);

        // 16.3: "The colour circle goes WHITE and becomes unusable" for a subject
        // that cannot be recoloured. Not muted, not dimmed - white, which is the
        // one fill that cannot be mistaken for a colour the subject carries.
        // 16.9 keeps it live for a stroke and fills it with THAT STROKE's colour.
        //
        // NOTE what is NOT touched here: the per-pen colour arcs on the ring,
        // painted above as _rimArc. 16.3 is explicit - "the user was explicit" -
        // that they must not grey. They REPORT which colour each pen carries, and
        // that stays true whatever is selected; greying them would destroy
        // information rather than disable a control.
        bool colourDead = ColourInert;
        _dot.Fill = new SolidColorBrush(colourDead ? Colors.White : SelectionColour() ?? ActiveColour());
        _dot.Stroke = new SolidColorBrush(
            colourDead ? outline : _hoverZone == Zone.Dot ? PageTheme.Accent : outline);
        _dot.StrokeThickness = !colourDead && _hoverZone == Zone.Dot ? 3 : 2;

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
    /// which reads better on a filled circle than a plate behind it would.
    ///
    /// <para>11.20 item 10: the plate is now the HIT REGION, drawn exactly.
    /// Rebuilt each paint rather than cached - WinUI's one-parent rule covers
    /// Geometry, and a shared instance throws mid-Refresh (see PopGeometry).
    /// Twenty segments cost nothing next to that risk.</para></summary>
    private void PlaceHover(Color ink)
    {
        if (HoverGeometry(_hoverZone) is not { } geo)
        {
            _hoverPlate.Visibility = Visibility.Collapsed;
            return;
        }
        _hoverPlate.Data = geo;
        _hoverPlate.Fill = new SolidColorBrush(PageTheme.WithAlpha(ink, 26));
        _hoverPlate.Visibility = Visibility.Visible;
    }

    /// <summary>11.20 item 10 / 11.22 item 1: the exact region Aim resolves to
    /// <paramref name="z"/>.
    ///
    /// <para>One annular sector per control, from the colour dot's edge to the
    /// disc's, built by the SAME <see cref="Sector"/> the tools ring is drawn
    /// with - so the arcs cannot acquire their own flag bugs. Read the bearings
    /// off the quadrant table beside the constants; they are the only place the
    /// outline's shape is stated, and Aim's degrees are derived from the same
    /// table, so the two cannot drift apart.</para>
    ///
    /// <para>Rebuilt each paint rather than cached: WinUI's one-parent rule
    /// covers Geometry, and a shared instance throws mid-Refresh (see
    /// PopGeometry).</para></summary>
    private static Geometry? HoverGeometry(Zone z) => z switch
    {
        Zone.Size => Sector(QuadSmooth, QuadSize, DotR, DiscR),
        Zone.Opacity => Sector(QuadSize, QuadOpacity, DotR, DiscR),
        Zone.Redo => Sector(QuadOpacity, QuadRedo, DotR, DiscR),
        Zone.Undo => Sector(QuadRedo, QuadUndo, DotR, DiscR),
        Zone.Smooth => Sector(QuadUndo, QuadSmooth, DotR, DiscR),
        _ => null,
    };

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
    /// can be placed, unlike everything else here.
    ///
    /// <para>16.4: the size row is already a centred pair, so being unavailable
    /// costs it no rearrangement - only the 2.73 DIP that separates 14.1's
    /// Row1Y from the middle of the top section.</para>
    ///
    /// <para>17.14: and the readout is EMPTY by then, not "-", so the pair is
    /// the glyph and nothing else. The 5 DIP that separates a mark from its
    /// number has to go with the number, or the glyph would centre 2.5 DIP left
    /// of the midline - the gap holding a place for a readout that is not
    /// there.</para></summary>
    private void LayoutSizeRow(bool enabled)
    {
        _sizeText.Measure(new Size(200, 40));
        double tw = _sizeText.DesiredSize.Width;
        double total = SetBox + (tw > 0 ? SetGap + tw : 0);
        double x = Half - total / 2;
        double y = enabled ? Row1Y : SectionMid(Prop.Size).Y;
        Canvas.SetLeft(_sizeGlyph, x);
        Canvas.SetTop(_sizeGlyph, Half + y - SetBox / 2);
        Canvas.SetLeft(_sizeText, x + SetBox + SetGap);
        Canvas.SetTop(_sizeText, Half + y - ReadoutSize * 0.72);
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

        // ---- 17.19: THE PLATES, AND WHY THEY MOVED UP THE STACK ----------
        //
        // Under 17.4 these went in FIRST, before the sectors, and that was
        // correct for what they were: a page-coloured patch that only showed on
        // a DARK ground, where section 7 leaves the ring transparent and the
        // sector above them paints nothing. The hover tint, painted on the
        // sector, then composited over the plate instead of being buried.
        //
        // 17.19 makes the plate the PEN'S OWN COLOUR, in both themes and with no
        // light/dark branch - so it now has to show on a LIGHT ground too, where
        // the sector fill is opaque and would bury it completely. They go in
        // after the ring, after the popped sector and after its flash, and
        // before the marks. The one thing that changes with them: a hover no
        // longer washes over the plate, only over the sector around it, which is
        // right - a coloured button is not a place to put a tint.
        for (int i = 0; i < Slots; i++)
        {
            var at = Pt(SlotMid(i), MarkR);
            var s = new Ellipse { Width = SeatSize, Height = SeatSize, IsHitTestVisible = false };
            Canvas.SetLeft(s, at.X - SeatSize / 2);
            Canvas.SetTop(s, at.Y - SeatSize / 2);
            // The plate rides out with the pop exactly as the mark standing on it
            // does. It did not need to before, because it was hidden on the
            // active sector; 17.19 paints it there too.
            _seatT[i] = new TranslateTransform();
            s.RenderTransform = _seatT[i];
            _seat[i] = s;
            _wheel.Children.Add(s);
        }

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

        // 16.4 moved these two pairs off the constants and onto the enabled
        // state, so Put/PutValue only add and configure them here; every frame's
        // POSITION comes from LayoutReadouts, which Refresh calls.
        Put(_smoothGlyph, -ColX, ColY, SetBox);
        Put(_opacGlyph, +ColX, ColY, SetBox);
        PutValue(_smoothText, -ValueX, ValueY);
        PutValue(_opacText, +ValueX, ValueY);

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
            PlaceBox(c, dx, dy, box);
            _wheel.Children.Add(c);
        }
        void PutValue(TextBlock t, double dx, double dy)
        {
            t.FontSize = ValueSize;                            // §1.4 "values 12 DIP semibold"
            t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            t.TextAlignment = TextAlignment.Center;
            t.Width = ValueW;
            t.IsHitTestVisible = false;
            PlaceValue(t, dx, dy);
            _wheel.Children.Add(t);
        }
    }

    /// <summary>A square box centred at (dx, dy) from the wheel's centre.</summary>
    private static void PlaceBox(UIElement e, double dx, double dy, double box)
    {
        Canvas.SetLeft(e, Half + dx - box / 2);
        Canvas.SetTop(e, Half + dy - box / 2);
    }

    /// <summary>A value TextBlock: a fixed-width centring box whose top sits at
    /// dy - 0.65 * ValueSize, so dy tracks the digits rather than the box.</summary>
    private static void PlaceValue(TextBlock t, double dx, double dy)
    {
        Canvas.SetLeft(t, Half + dx - ValueW / 2);
        Canvas.SetTop(t, Half + dy - ValueSize * 0.65);
    }

    /// <summary>16.4: the middle of a property's SECTION - the mid-radius point
    /// on the quadrant's own midline.
    ///
    /// <para>The bearings are read straight off the quadrant table beside the
    /// constants, the same one HoverGeometry and Aim use, so a section's centre
    /// cannot drift away from the region the pointer resolves to.</para></summary>
    private static Point SectionMid(Prop p) => Polar(
        p switch
        {
            Prop.Size => 0,          // top,   QuadSmooth 315 .. QuadSize 45
            Prop.Opacity => 90,      // right, QuadSize 45 .. QuadOpacity 135
            _ => 270,                // left,  QuadUndo 225 .. QuadSmooth 315
        }, SectionR);

    /// <summary>16.5 and 16.4, as one placement pass: where stability and
    /// opacity put their glyph and their value this frame.
    ///
    /// <para>Position depends on the ENABLED state now, so it cannot stay in
    /// BuildWheel where it used to live - Refresh calls this every paint.</para>
    ///
    /// <para>16.4 owns the layout of the disabled case only. WHICH conditions
    /// disable a readout is <see cref="Enabled"/>'s business, and 16.3's
    /// attachment rule belongs there rather than here - see the note on
    /// Enabled.</para></summary>
    private static void LayoutReadouts(Canvas glyph, TextBlock value, Prop p,
                                       bool enabled, int side)
    {
        if (enabled)
        {
            // 16.5: up and outward, the value following its glyph.
            PlaceBox(glyph, side * ColX, ColY, SetBox);
            PlaceValue(value, side * ValueX, ValueY);
            return;
        }
        // 16.4 as 17.14 leaves it: the GLYPH centres in its section. The value
        // is empty, so it is parked on the same point rather than under it - a
        // TextBlock holding "" paints nothing, and leaving it at its enabled
        // position would strand an invisible box out on the rim.
        var c = SectionMid(p);
        PlaceBox(glyph, c.X, c.Y, SetBox);
        PlaceValue(value, c.X, c.Y);
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
        if (r < DiscR)
        {
            // 11.22 item 1: four EQUAL QUADRANTS, the bottom one halved between
            // redo (leading) and undo (trailing). Undo and redo are therefore
            // still inside the circle, as 11.2 item 10 requires, without being
            // the two loose squares that used to overlap their neighbours and
            // spill 4 DIP past the disc edge.
            //
            // The reference measures from the RIGHT HORIZON, anticlockwise, and
            // screen y points down - so the angle is atan2(-dy, dx), not the
            // bearing the rest of this file uses.
            double q = Norm360(Math.Atan2(-dy, dx) * 180 / Math.PI);
            if (q < 45 || q >= 315) return (Zone.Opacity, -1);
            if (q < 135) return (Zone.Size, -1);
            if (q < 225) return (Zone.Smooth, -1);
            if (q < 270) return (Zone.Undo, -1);
            return (Zone.Redo, -1);
        }

        int slot = (int)Math.Round(Norm360(b - Sector0) / Span) % Slots;
        if (r <= RingOut + 2) return (Zone.Sector, slot);
        // Beyond the ring only the POPPED sector is there to be pressed; the rest
        // of that band is 17.17's GRIP - see the Zone.Grip case in OnPressed for
        // why the rim is what moves the dial.
        if (r <= PopOut + 2) return slot == _active ? (Zone.Sector, slot) : (Zone.Grip, -1);
        return (Zone.None, -1);
    }

    /// <summary>11.22 item 3. Attached once, to the XamlRoot's content, so a
    /// press anywhere in the window is seen whether or not something nearer the
    /// pointer already handled it.</summary>
    private UIElement? _rootHooked;

    private void HookRootPress()
    {
        if (_host.XamlRoot?.Content is not UIElement root) return;
        if (ReferenceEquals(root, _rootHooked)) return;
        _rootHooked = root;
        try { root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnAnyPress), true); }
        catch { _rootHooked = null; }
    }

    private void OnAnyPress(object sender, PointerRoutedEventArgs e)
    {
        if (!_popover.IsOpen) return;
        Point p;
        try { p = e.GetCurrentPoint(_host).Position; }
        catch { return; }

        // Inside the card: its chips, slider and arrows deal with it themselves.
        try
        {
            var b = _popover.Element.TransformToVisual(_host)
                            .TransformBounds(new Rect(0, 0, ValuePopover.W, ValuePopover.H));
            if (b.Contains(p)) return;
        }
        catch { }

        // The control the card belongs to. This handler runs AFTER the shield's,
        // so without this the press that opens a card would close it again in
        // the same gesture.
        if (_on && PropOf(Aim(p).Z) is not null) return;

        _popover.Close();       // deliberately not Handled
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

        // ---- 17.17: WHAT IS DRAGGED ------------------------------------
        //
        // The rim. The band between the ring's outer edge and the popped
        // radius, everywhere except under the popped sector itself.
        //
        // The section names the two candidates it expects and rules both out:
        // the ring "would fight the ring's own controls" - all ten sectors are
        // live and one of them is a press away from switching tool - and the hub
        // "would fight the colour dot". The hub is in fact WORSE than that reads:
        // its four other quadrants are size, opacity, stability and the undo /
        // redo pair, and three of those already interpret a DRAG as a scrub, so a
        // drag begun there would have to be taken away from a gesture that exists.
        //
        // The rim is the one part of the dial no control owns. Aim has always
        // returned nothing for it, OnPressed has always refused to claim it -
        // "the empty band: never claim it" - and it rings the dial, so it can be
        // grabbed from whichever side happens to face the middle of the screen.
        //
        // It is 18.6 DIP deep (RingOut 98 to PopOut 116.6, plus the 2 DIP of
        // slop Aim already allows either side), 20.5 in touch mode, and BROKEN
        // for the 36 degrees the popped sector occupies. That is a small target
        // and it is worth saying so plainly rather than discovering it - but the
        // landing is a SNAP, so unlike a free drag it needs no precision at all
        // once it has started: the grab is the only accurate part of the gesture.
        //
        // The shield is deliberately NOT widened to make it bigger. It already
        // swallows presses inside PopOut, and every DIP added to it is a DIP of
        // canvas that stops taking ink.
        if (z == Zone.Grip)
        {
            _dragging = true;
            _dragMoved = false;
            _dragFrom = _centre;
            // The card is docked to the dial and does not ride with it, so it
            // would be left standing where the dial used to be.
            _popover.Close();
            e.Handled = true;
            return;
        }

        if (z == Zone.Sector && pt.Properties.IsRightButtonPressed) { Refresh(); PickForSlot(idx); e.Handled = true; return; }
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

        // 17.17: the dial follows the finger while the grip is down and lands on
        // one of the eight when it lifts. It follows FREELY rather than jumping
        // between the eight as you pass them, because a control that teleports
        // out from under the pointer cannot be aimed - the snap is the landing,
        // not the travel. Same TapSlop the rest of this file uses, so a press
        // that never really moved is not a move.
        if (_dragging)
        {
            if (!_dragMoved && Dist(p, _pressPt) > TapSlop) _dragMoved = true;
            if (_dragMoved)
            {
                // Clamped to the host so the dial can always be seen and grabbed
                // again; the release re-clamps it properly to a dock.
                double cx = Math.Clamp(_dragFrom.X + (p.X - _pressPt.X), 0, Math.Max(0, _host.ActualWidth));
                double cy = Math.Clamp(_dragFrom.Y + (p.Y - _pressPt.Y), 0, Math.Max(0, _host.ActualHeight));
                PlaceAt(new Point(cx, cy));
            }
            return;
        }

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

        // 17.17: TAKE THE GESTURE OFF THE FIELDS BEFORE GIVING UP THE CAPTURE.
        //
        // ReleasePointerCapture raises PointerCaptureLost SYNCHRONOUSLY: OnLost
        // runs to completion INSIDE the call on the line below, before the next
        // line of this method. With _dragging still set it therefore cancelled
        // the very drag this release was about to land - the dial sprang back to
        // the dock it started from and DialAnchor was never written. Traced:
        // OnReleased entered with _dragging=true, OnLost fired 1 ms later with
        // _pointer ALREADY null (so it came from the line below, not from the
        // shell), and the drag branch was then dead by the time it was reached.
        //
        // That is why nine drags across every timing, travel and step count
        // failed identically: nothing about it was a race. It was never a lost
        // pointer and never the drag's own re-layout of the shield. OnLost is
        // UNCHANGED and still cancels - a deactivated window or a flyout
        // stealing the pointer is not the user choosing a dock - it simply has
        // nothing left to cancel when WE are the ones ending the gesture.
        //
        // The scrub state is snapshotted for the same reason: the nested OnLost
        // also cleared _dragProp, so a finished scrub fell past its own branch
        // into the TAP tests below - its card never closed, and a scrub that
        // ended over a sector reached Commit and changed tool.
        bool wasDragging = _dragging;
        bool wasDragMoved = _dragMoved;
        var wasProp = _dragProp;
        bool wasScrubbing = _scrubbing;
        _dragging = false;
        _dragMoved = false;
        _dragProp = null;
        _scrubbing = false;
        _pressed = false;
        _pointer = null;
        try { _shield.ReleasePointerCapture(e.Pointer); } catch { }
        e.Handled = true;

        // 17.17: SNAP ONLY. Whatever loose centre the drag left the dial at, it
        // lands on the nearest of the eight and the choice is written down. A
        // grip press that never passed the slop is not a move and writes nothing
        // - the rim carries no tap action of its own, so it simply does nothing,
        // which is what it did before this section.
        if (wasDragging)
        {
            if (wasDragMoved) SetAnchor(NearestAnchor(_centre));
            else Refresh();
            return;
        }

        if (wasProp != null)
        {
            // A scrub's popover closes when the finger lifts; a TAP's stays up,
            // because a tap IS the user asking for it. The test is "did this
            // gesture ever scrub", not an elapsed time - a deliberate press held
            // without moving is still a tap, and it used to close the very
            // popover it had just opened.
            if (wasScrubbing) _popover.Close();
            Refresh();
            return;
        }
        if (!wasPressed) return;

        var (z, idx) = Aim(p);
        bool tap = Environment.TickCount64 - _pressMs < TapMs && Dist(p, _pressPt) <= TapSlop;
        // 16.3: white AND UNUSABLE. A dot that still opened the picker would be
        // the "live-looking colour control that silently does nothing" the
        // section calls worse than one that says so.
        if (z == Zone.Dot && ColourInert) return;
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
        // A LOST grip is a CANCELLED move, not a landing: the dial goes back to
        // the dock it started from and nothing is written. Losing capture is how
        // a gesture ends when the window is deactivated or a flyout steals the
        // pointer, and neither of those is the user choosing a corner.
        if (_dragging) { _dragging = false; _dragMoved = false; Place(); }
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
        // 11.2 item 11 + 11.22 item 4: the + cell offers the Brushes library
        // aimed at itself, and falls back to the flyout when there is none.
        if (id.Length == 0) { ClearHover(); PickForSlot(slot); return; }
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

    /// <summary>Whether a readout has anything to report. THE dial's disabled
    /// flag - there is not a second one, and there must not become one.
    ///
    /// <para>CONCEPTS-REF 16.3, and the sentence it is easy to get wrong: the
    /// rule is NOT "something is selected, so grey the dial". It is <b>a subject
    /// that LACKS a property greys that property's control</b>. An attachment
    /// has no pen size and no stabiliser, so those two grey while opacity stays
    /// live; a STROKE has all three (16.9), so nothing greys and the readouts
    /// show that stroke's own values. Ask the subject what it HAS, never whether
    /// it exists - the two questions agree on an attachment and diverge the
    /// moment a stroke is selected.</para>
    ///
    /// <para>With nothing selected this falls through to the older question -
    /// what does the active TOOL have - which is the same rule applied to a
    /// different subject. A non-pen tool has no opacity and no stabiliser, so
    /// text disables all three and the eraser disables two.</para>
    ///
    /// <para>16.4's centred layout hangs off this one predicate, as do the muted
    /// brush and the BLANK readout 17.14 replaced the dash with: Refresh reads it
    /// once into <c>enabled[]</c> and every consequence follows from that array.
    /// A parallel notion of "disabled" would give the dial two disagreeing
    /// answers, and 16.4's layout would follow only one of them.</para></summary>
    private bool Enabled(Prop p)
    {
        if (SelectionState.Current is { Any: true } sel)
            return p switch
            {
                Prop.Size => sel.HasPenSize,
                Prop.Opacity => sel.HasOpacity,
                _ => sel.HasStability,
            };
        var ap = ToolPen();
        bool eraser = _h.ToolTag() == "Eraser";
        return p switch
        {
            Prop.Size => ap != null || eraser,   // size applies to pens and the eraser
            _ => ap != null,                     // opacity and smoothness are pen-only
        };
    }

    /// <summary>The subject's own value for a property, normalised to 0..1 on the
    /// same scales <see cref="Value"/> uses, or null when there is no subject or
    /// the selection disagrees with itself.</summary>
    private double? SubjectValue(Prop p)
    {
        var sel = SelectionState.Current;
        if (!sel.Any) return null;
        return p switch
        {
            Prop.Size => sel.Size is { } v ? Norm01(v, 1, 24) : null,
            Prop.Opacity => sel.Opacity is { } v ? Math.Clamp(v, 0, 1) : null,
            _ => sel.Stability is { } v ? Math.Clamp(v, 0, 1) : (double?)null,
        };
    }

    private double Value(Prop p)
    {
        // 16.9: a scrub that starts on a selection starts from the SELECTION's
        // value, or the first nudge would jump it to the active pen's.
        if (SubjectValue(p) is { } sv) return sv;
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
        // 16.9: "the controls stay usable and EDITING THEM EDITS THE SELECTION."
        // A subject that offers a setter takes the write; the active pen is left
        // alone, because the user is adjusting the stroke in front of them, not
        // choosing what the next one will look like.
        if (SelectionState.Current is { Any: true } sel)
        {
            switch (p)
            {
                case Prop.Size when sel.SetSize != null:
                    sel.SetSize((float)Math.Round(1 + t * 23, 1));
                    Refresh();
                    return;
                case Prop.Opacity when sel.SetOpacity != null:
                    sel.SetOpacity((float)Math.Round(Math.Max(0.05, t), 2));
                    Refresh();
                    return;
                case Prop.Smooth when sel.SetStability != null:
                    sel.SetStability((float)Math.Round(t, 2));
                    Refresh();
                    return;
            }
            // A subject with no setter for this property still blocks the write:
            // the control is live because the subject HAS the property, and
            // silently redirecting the edit to the active pen would move a
            // number the user is not looking at.
            if (sel.Kind != SubjectKind.None) return;
        }
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
        if (ColourInert) return;                    // 16.3, belt and braces
        // 16.9: with a recolourable subject in hand the wheel recolours THE
        // SELECTION. The pen keeps its own colour - the user is changing the
        // stroke in front of them, not the next one they will draw.
        var subject = SelectionState.Current;
        var ap = ActivePen() ?? _h.Library().Pens.FirstOrDefault();
        if (subject is { Any: true, CanRecolour: true, SetInk: not null })
        {
            var from = subject.Ink ?? (ap != null ? ColorUtil.Parse(ap.Color) : PageTheme.OnSurface);
            void ApplyToSelection(Color c) => subject.SetInk!(c);
            if (ColourPickerHook != null)
            {
                ColourPickerHook(DotRootPoint(), from, ApplyToSelection, Refresh, PopOut * _scale);
                return;
            }
        }
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

    /// <summary>11.22 item 4's entry: the Brushes library, aimed at this slot.
    /// Falls through to the flyout when the host has no library to offer, so a
    /// sector is never left with no way to be assigned.</summary>
    private void PickForSlot(int slot)
    {
        if (slot < 0 || slot >= Slots) return;
        bool taken = false;
        try { taken = BrushPickerHook?.Invoke(slot) == true; } catch { taken = false; }
        if (!taken) ShowAssign(slot);
    }

    /// <summary>The full assignment flyout, for the Brushes library's "More..."
    /// - pens by name, tools, commands and Empty. This is the guarantee that
    /// pointing right-click at the library costs the + cells nothing.</summary>
    public void ShowSlotMenu(int slot) { if (slot >= 0 && slot < Slots) ShowAssign(slot); }

    /// <summary>What a slot holds, when it holds a pen. The library previews the
    /// SLOT rather than the live tool while it is aimed at one.</summary>
    public PenPreset? SlotPen(int slot) =>
        slot >= 0 && slot < Slots ? PenOf(ResolveSlots()[slot]) : null;

    /// <summary>Put a pen in a slot (11.24 item 1's targeted assignment).</summary>
    public void SetSlotPen(int slot, PenPreset pen)
    {
        if (slot >= 0 && slot < Slots) AssignSlot(slot, KindPen + pen.Id);
    }

    /// <summary>Put a tool in a slot.</summary>
    public void SetSlotTool(int slot, string tag)
    {
        if (slot >= 0 && slot < Slots) AssignSlot(slot, KindTool + tag);
    }

    /// <summary>Empty a slot.</summary>
    public void ClearSlot(int slot) { if (slot >= 0 && slot < Slots) AssignSlot(slot, ""); }

    /// <summary>The slot-assignment flyout: press-hold a sector, or "More..." in
    /// the Brushes library. This is how the user chooses which tools occupy the
    /// dial, and it is the only surface that can put a COMMAND in one.</summary>
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

    /// <summary>16.3 / 16.9: whether the colour circle is white and inert. True
    /// exactly when there is a subject and that subject cannot be recoloured -
    /// never merely because something is selected.</summary>
    private static bool ColourInert => SelectionState.Current is { Any: true, CanRecolour: false };

    /// <summary>The selection's own ink, when it has one. Null falls through to
    /// the active pen's colour, which is what the dot has always shown.</summary>
    private static Color? SelectionColour() =>
        SelectionState.Current is { Any: true, CanRecolour: true } s ? s.Ink : null;

    // ===================================================================
    // Art - every mark comes from Helpers/Icons, the same source the top bar
    // binds to, so the two surfaces can never drift again.
    // ===================================================================

    /// <summary>17.19: WHAT COLOUR A CELL'S PLATE IS.
    ///
    /// <para>A pen cell takes that pen's own colour. Everything else - a tool, a
    /// command, an empty cell - takes white or black by the page background,
    /// because "a tool has no colour of its own to show".</para>
    ///
    /// <para><b>CONTRASTING, not matching.</b> On a dark page the tool plate is
    /// WHITE and on a light page BLACK. A plate that took the page's own side
    /// would be the §17.4 defect back again under a new name: a patch the colour
    /// of the page, on a page with no grid or texture to interrupt, is invisible
    /// however it was arrived at.</para>
    ///
    /// <para>Fully opaque. A pen's own <c>Opacity</c> used to modulate its MARK's
    /// alpha, and 17.19 names that among the defects it makes unreachable - a
    /// half-transparent mark is not "white or black". The pen's opacity is
    /// reported by the disc's own readout, which is where a number belongs.</para>
    ///
    /// <para><b>THE PAGE, NOT THE THEME - AND THEY ARE NOT THE SAME THING.</b>
    /// 17.19 says "according to page background", and the obvious reading of
    /// that is <see cref="PageTheme.IsDark"/>. It is wrong, and it is wrong in a
    /// way that has already shipped once: <c>MainWindow.ResolveGround</c> returns
    /// the page's paper ONLY when <c>ThemeSource == "Page"</c>, and that field
    /// defaults to <c>"Manual"</c> - so on a default install
    /// <see cref="PageTheme.Ground"/> is a FIXED shell colour and knows nothing
    /// about the paper. That is exactly how §17.2's "a background that mimics the
    /// page colour" came to be measured byte-identical (#0F0E10) on a dark page
    /// and on Brown Paper. A tool plate resolved that way would be white on a
    /// pinned-dark shell whatever the paper underneath it actually was.</para>
    ///
    /// <para>So this reads <see cref="PaperTextures.Ground"/> off the live page -
    /// the same helper <c>ResolveGround</c> uses for its own Page branch - and
    /// judges it with <see cref="PageTheme.Luminance"/>, which is the gamma-correct
    /// threshold the whole shell decides light from dark on.
    /// <c>ColorUtil.IsDark</c> averages raw bytes and puts Brown Paper on the
    /// wrong side, and Brown Paper is one of the three grounds §7 names as dark.
    /// <c>MainWindow.PushGround</c> refreshes the dial so a paper change repaints
    /// these even when the shell's own ground did not move.</para></summary>
    private Color PlateFor(string id) =>
        PenOf(id) is { } pen ? SafeInk(pen.Color)
                             : PageIsDark ? Colors.White : Colors.Black;

    /// <summary>Whether the PAGE - the paper the user is drawing on - is dark.
    /// Deliberately not <see cref="PageTheme.IsDark"/>; see
    /// <see cref="PlateFor"/> for why those two disagree by default.</summary>
    private bool PageIsDark
    {
        get
        {
            try { return PageTheme.Luminance(PaperTextures.Ground(_surface.Page)) < 0.5; }
            catch { return PageTheme.IsDark; }
        }
    }

    private static Color SafeInk(string hex)
    {
        try { var c = ColorUtil.Parse(hex); c.A = 255; return c; }
        catch { return Colors.Gray; }
    }

    /// <summary>17.19: "the mark on the plate is white or black, whichever
    /// contrasts better with the plate it is drawn on."
    ///
    /// <para>WCAG relative-luminance contrast, not a luminance threshold. The two
    /// agree almost everywhere, but the ratio is the thing the 3:1 floor for
    /// non-text marks is stated in, so measuring it here means the choice and the
    /// acceptance test are computed from one expression. Ties go to black, which
    /// only happens at the exact crossover.</para>
    ///
    /// <para><b>THE FLOOR IS PROVABLE, and 17.19 asked for it.</b> The section
    /// warns that a mid-luminance pen contrasts poorly with both white and black
    /// and asks for the worst case with a number. Taking the better of the two is
    /// worst exactly where the curves cross: <c>1.05/(Y+0.05) = (Y+0.05)/0.05</c>,
    /// i.e. Y = √0.0525 − 0.05 = 0.17913, where both give <b>4.583:1</b>. That is
    /// the minimum over the WHOLE sRGB gamut, not just the shipped pens — no
    /// colour a user can pick does worse — and it clears the 3:1 floor for
    /// non-text marks by half as much again. The shipped pens run 4.979:1 (Red
    /// fountain #D32F2F, the nearest to mid) to 18.434:1 (Ink #141413).</para></summary>
    private static Color BestInk(Color plate) =>
        Contrast(Colors.White, plate) > Contrast(Colors.Black, plate) ? Colors.White : Colors.Black;

    /// <summary>WCAG 2.x contrast ratio, 1..21. <see cref="PageTheme.Luminance"/>
    /// is the gamma-correct relative luminance the whole shell decides light from
    /// dark on, so this cannot disagree with <c>IsDark</c>.</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = PageTheme.Luminance(a), lb = PageTheme.Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>§1.3's stroke silhouette for a sector, in 17.19's white-or-black
    /// rather than "the tool's own colour": the tool's own colour is the PLATE
    /// now, and a mark in it would be a hole.</summary>
    private FrameworkElement? SlotArt(string id, Color fg)
    {
        // 11.2 item 11's unassigned cell.
        if (id.Length == 0)
            return Icons.Mark(Icons.Plus, fg, MarkBox * 0.62, stroked: true, thickness: 2.2);
        if (PenOf(id) is { } pen) return PenStrokeMark(pen, fg);
        if (id.StartsWith(KindTool, StringComparison.Ordinal))
            return Icons.Mark(Icons.Tool(id[KindTool.Length..]), fg, MarkBox);
        if (id.StartsWith(KindCmd, StringComparison.Ordinal)) return CmdArt(id[KindCmd.Length..], fg, MarkBox);
        return null;
    }

    /// <summary>A pen sector shows THE STROKE THAT PEN LEAVES - a hand-authored
    /// silhouette of its mark (tapered for a nib, chisel for a marker, grainy for
    /// a pencil, even and round-ended for a ballpoint). Not the pen-body chip,
    /// and not a live render.
    ///
    /// <para>17.19: painted in <paramref name="fg"/>, full stop. There is no
    /// contrast test left to get wrong here - the caller resolved white or black
    /// against the plate this mark is standing on, and the plate is the pen's own
    /// colour, so the old "keep the ink unless it collapses into the seat" branch
    /// would collapse on every single cell.</para></summary>
    private static FrameworkElement? PenStrokeMark(PenPreset p, Color fg)
    {
        try { return Icons.Mark(Icons.PenStroke(p.Pen), fg, MarkBox); }
        catch { return null; }
    }

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
        "Undo" => Icons.Mark(Icons.UndoRound, fg, size),
        "Redo" => Icons.Mark(Icons.UndoRound, fg, size, mirror: true),
        "MouseMode" => Icons.Mark(Icons.Mouse, fg, size),
        _ => Extra(cmd) is { } x
             ? Icons.Mark(x.Icon, fg, size, stroked: x.Stroked, thickness: 1.9)
             : Icons.Mark(Icons.Mouse, fg, size),
    };
}
