using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Quill.Helpers;
using Quill.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// Which face of the picker is showing. Persisted on the library so the
/// picker reopens in the mode the user last worked in.
/// </summary>
public enum ColorWheelMode { Copic, Hsl, Rgb }

/// <summary>
/// Quill's one colour picker — a Concepts-style rotatable swatch ring with an
/// HSL and an RGB face, an eyedropper and a recent-colours arc.
///
/// ── HOW TO USE IT ────────────────────────────────────────────────────────
/// Do not new this up. Call the service, which owns the popup, the recents
/// list and the persisted mode:
///
///     ColorPickerService.Open(
///         xamlRoot,                       // any element's XamlRoot
///         openedFromPoint,                // a HINT only — see PLACEMENT
///         currentColour,
///         c => { /* called live, on every change */ });
///
/// The callback fires continuously while a slider is dragged and once per
/// swatch tap, so treat it as "the colour is now this" rather than "the user
/// committed". The picker stays open until it is dismissed (tap away, Esc, or
/// <see cref="ColorPickerService.Close"/>); pass an onClosed callback if you
/// need to know. See ColorPickerService for the one-time host wiring
/// (canvas sampler, recents list, persistence).
///
/// ── PLACEMENT AND SIZE ────────────────────────────────────────────────────
/// The picker is its own surface, not something hanging off whatever opened
/// it: the ring centres itself in the viewport, so it never moves while the
/// pointer moves or while the radial dial behind it re-renders. The point the
/// caller passes is kept only as a bias hint: it picks which side of the hub
/// the chrome cluster (mode labels, eyedropper, puck, recents) faces, so the
/// picker still leans toward the thing you tapped.
///
/// The ring is sized from the reference's OWN geometry — one reference unit is
/// one DIP, exactly as the reference's 260-unit viewBox maps onto its 260px
/// SVG — so a band is 21 DIP and a marker code is ~10.5 DIP. It is therefore
/// 1380 DIP across and OVERFLOWS the window, which is the intended behaviour
/// and not a bug: the reference does the same (overflow-visible on a 260px
/// box) and reaches the outer families by SPINNING them round to the
/// horizontal, where the viewport is widest. Fitting the whole 690-unit radius
/// on screen instead is what squeezed the bands to ~13 DIP with a 6 DIP code.
///
/// ── ENTRANCE ──────────────────────────────────────────────────────────────
/// The reference's radial gravity drop, ported keyframe-for-keyframe — see
/// the "Entrance / exit" region at the bottom of this file.
///
/// ── LAYOUT (calibrated to the reference wheel) ────────────────────────────
/// Three concentric tiers, exactly as the dialled-in web wheel:
///   • Tier 1 — a 144° inner arc of accents + core (13 chips, 3 groups).
///   • Tier 2 — a full grey ring (Toner/Warm/Neutral/Cool, 46 chips).
///   • Tier 3+ — 36 fixed 10° columns grouped into 11 colour families, each
///     column a radial stack whose depth is however many inks it holds (up to
///     17 in the deep Earth column). Family widths are the reference's own:
///     R/RV/V/BV/B/G/YG/Y/YR span 30° each, BG 40°, E 50°.
/// The whole ring rotates as one; a wheel-scroll or flick spins it and it
/// settles square on a 10° column boundary.
///
/// ── WHY WIN2D ────────────────────────────────────────────────────────────
/// The ring is ~320 swatches on up to 17 concentric arcs. As XAML that would
/// be 600+ live elements (a Path and a rotated TextBlock each) whose layout
/// would have to be invalidated on every frame of a spin. In Win2D the whole
/// ring is one immediate-mode pass over cached per-ring geometry, and
/// hit-testing is pure arithmetic — atan2 for the column, a radius division for
/// the ring — so nothing scales with the swatch count. (The reference is DOM/
/// SVG and a little laggy for exactly this reason; only its layout is ported.)
/// </summary>
public sealed class ColorWheel : UserControl
{
    // ---- ring layout ------------------------------------------------------
    private const float Decay = 0.938f;   // per-frame inertia falloff
    private const float StopVel = 0.06f;  // rad/s below which a spin has settled

    private const float Deg = MathF.PI / 180f;
    // Breathing room between the ring and the window edge, px.
    private const float EdgeMargin = 18f;
    // The reference-unit radius of the grey ring's outer edge. The grey ring is
    // the innermost FULL circle, so it is the one thing that has to be on screen
    // all the way round without spinning; it is the only thing the size guard in
    // Layout defends.
    private const float Tier2OuterRef = 328f;
    // Tier 1's inner edge at s = 1: the radius of the HOLE, which is what has to
    // stay clear of whatever the wheel is centred on (9.3).
    private const float Tier1InnerRef = 285f;
    // 11.12 item 3. How much wider than the reference every band past the hole
    // is drawn. At the clearance a docked dial forces, s settles near 0.70, so
    // a 21-unit column cell came out 14.6 DIP tall against ~42 DIP of arc - a
    // long flat sliver, which is what "too narrow" and 11.3 item 20's "closer
    // to square" are both describing. 1.85 puts that cell at 27 DIP and the
    // innermost column ring within a third of square.
    //
    // 11.15 item 4 puts this at +15% over the 27.08 DIP the user approved in
    // 11.12, reversing 11.14 item 3's retraction. The cell depth is an absolute
    // number, not a proportion, so the constant is solved for it at the s a
    // docked dial forces: 21 * 0.9004 * 1.6470 = 31.14 DIP.
    //
    // 11.20 item 3 then deepens every cell "by a further 20% outward, keeping
    // the inner empty radius exactly where it is". Both conditions are already
    // how this works - the hole is its own number and every band past it is
    // laid out cumulatively outward from there - so item 3 is one factor and
    // nothing else moves: 1.6470 * 1.20 = 1.9764, and 21 * 0.9004 * 1.9764 =
    // 37.37 DIP of cell. 11.21 item 2 then lets the outer edge go wherever 17
    // rings of that put it, which is 1009 DIP.
    private const float CellScale = 1.9764f;
    // 11.21 RETIRES the outer target. 11.15 item 1 asked for a 15% smaller
    // outer extent; holding a target radius while item 4 deepened every cell
    // meant flooring to the last WHOLE cell that fitted, and at the settled
    // numbers that was 9 rings of 17 - eight columns of the COPIC palette
    // simply not drawn. The user ruled on the trade:
    //
    //   "increase radius to facilitate cell depth, do not remove any cell, the
    //    cells can go out of the screen, thats why rotation is there."
    //
    // So there is no target radius any more. The outer edge is an ACCUMULATION
    // again - hole, spine, gap, then all MaxRings columns - and running off the
    // window is expected: 11.21 item 3 clips at the window edge and forbids
    // shrinking the ring, re-centring it or moving the dial, and item 4 makes
    // rotation the access mechanism for whatever lands outside.
    //
    // No swatch is ever dropped. If a future size instruction and the full
    // palette ever conflict again, the palette wins and the conflict is
    // reported rather than resolved by trimming rings.
    // 11.15 item 3: "texts and the other elements shrink 20%", applied
    // INDEPENDENTLY of item 1 - one factor, on everything the wheel draws that
    // is not a swatch. It replaces 11.14's split 0.85 / 0.80, which 11.15
    // supersedes entirely, and it is taken off the 11.13 sizes because that is
    // the render these instructions are measured against.
    private const float TextScale = 0.80f;
    // 11.20 item 4: "everything that opens when the colour wheel is pressed
    // shrinks 20%, proportionally - the whole surface, not selected parts."
    //
    // "The whole surface" cannot be read literally as EVERYTHING, because three
    // things it would have to include are pinned by instructions the user gave
    // in the same breath and restated afterwards: the hole keeps its radius
    // (11.15 item 2, restated by 11.21), the cells were just deepened 20% by
    // item 3 immediately above, and 11.21 items 2-3 make the outer radius the
    // palette's own size and forbid shrinking the ring at all. Taking 20% off
    // any of those would undo an instruction rather than add one. What is left
    // - and what "the whole surface, not selected parts" is distinguishing
    // itself from - is every element the picker draws that is not palette
    // geometry: the type, the plates, the puck, the recents, the eyedropper,
    // the arcs and their boxes, all at one factor rather than a chosen few.
    //
    // Kept as its own constant beside TextScale rather than folded into it:
    // they are two separate instructions (11.15 item 3 and 11.20 item 4) that
    // happen to land on the same elements, and one of them may be revised
    // without the other.
    private const float SurfaceScale = 0.80f;
    // What an element is actually drawn at: 0.80 x 0.80 = 0.64 of its 11.13
    // size. The RADII the chrome sits on are deliberately not in here - they
    // are fractions of the annulus between the dial and Tier 1, and pulling
    // them in would move the chrome onto the dial rather than make it smaller.
    private const float Elem = TextScale * SurfaceScale;
    // 11.20 item 5, on top of item 4: the chip's padding around the word, not
    // the word. Item 4 already brought the type down; this is what makes the
    // frame itself tighter rather than merely smaller with its contents.
    private const float PlateFrame = 0.80f;
    // 11.20 item 2, superseding 11.17's "the inner spine ring is radially
    // narrower": "the two inner rings have a different cell depth from the
    // outermost ring. Equalise them: all rings take the OUTERMOST ring's
    // depth." The two inner rings ARE Tier 1 and Tier 2 - the only two bands
    // in the wheel that were not one _band deep (17 and 21 reference units at
    // 0.62, so 19.3 DIP against the family cell's 31.1). They now take _band
    // exactly, and this constant survives only for the 5-unit hairline gaps
    // between them, which are separators rather than cells and do not
    // participate in "cell depth".
    //
    // 11.21 restates this as settled ("cell depth is equalised to the outermost
    // ring and then deepened"), so it is not a fork - it is the ruling.
    private const float SpineGapScale = 1.0f;
    // 11.17: "then a band of bare background" between the spine and the family
    // fans - "a real margin, not an artefact". The 5-unit reference gap is not
    // that; it is a hairline. Nine tenths of a family cell is, and it is the
    // one band in the wheel whose only job is to be empty.
    private const float SpineGap = 0.90f;
    // 11.17: "families are separated by visible gaps" of background, while
    // cells TOUCH within a family. Taken off the trailing edge of each family's
    // last column, so the leading edge - where 11.18's cornered code sits - is
    // never the side that moves.
    private const float FamGap = 1.7f * Deg;
    // How much annulus the hub's own chrome needs between the caller and
    // Tier 1: the recents row, the current-colour puck and the mode-plate arc.
    //
    // 11.13 item 1, "the empty centre gets bigger": this IS the hole, since the
    // hole is HubClearance + HubRoom, and 82 -> 140 takes it from 198.6 to
    // 256.6 DIP. 10.8 had brought it DOWN to 82 to stop the mode plates landing
    // on the dial's popped sector - that fix is not what is being reversed
    // here, and it still holds, because "lo" below still starts the chrome
    // outside the caller rather than at a fraction of the hole. What is being
    // reversed is 11.12's rule that the hole may not grow, and it is safe to
    // reverse only because item 2 grows the OUTER edge in the same breath: s
    // rises from 0.697 to 0.900, so every band past the hole widens by 29% at
    // the same time and the ring is not squeezed from one side.
    private const float HubRoom = 140f;
    // Abutting tiles share an edge, and two antialiased edges over the same
    // pixel leave a faint line of background showing through. Each tile is
    // therefore drawn a hair past the side it shares with its successor, which
    // then paints over the overlap — the chips read as one continuous sheet.
    // Deliberately sub-pixel: the hit-test still divides the band exactly.
    private const float Weld = 0.5f;
    // The outer ring is 36 contiguous 10° columns starting at the top (-90°).
    private const float ColStep = 10f * Deg;
    private const float OuterStart = -90f * Deg;

    private readonly CanvasControl _canvas = new();
    // The element the pointer handlers are attached to. Capture MUST be taken
    // on this exact element: a captured pointer is routed to the capturing
    // element and bubbles UP from there, so capturing on the UserControl (the
    // parent) silently orphans these handlers - PointerMoved and
    // PointerReleased simply never arrive, the ring cannot be dragged, and a
    // tap never completes, so no swatch can ever be picked.
    private UIElement? _input;
    private readonly DispatcherTimer _spin = new() { Interval = TimeSpan.FromMilliseconds(16) };
    // The one clock the whole entrance/exit cascade runs off. It ticks only
    // while a transition is in flight, so an idle picker costs nothing.
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(16) };

    // 11.14 item 4 puts the code in the cell's upper-left CORNER, so the box
    // is anchored rather than centred. "Upper" is the only self-consistent
    // reading of the word on a ring: DrawCode turns the glyph tops toward the
    // wheel's centre, so up-the-page for a reader is inward-along-the-radius.
    private readonly CanvasTextFormat _codeFmt = new()
    {
        FontSize = 11,
        FontFamily = "Segoe UI",
        HorizontalAlignment = CanvasHorizontalAlignment.Left,
        VerticalAlignment = CanvasVerticalAlignment.Top,
        WordWrapping = CanvasWordWrapping.NoWrap
    };
    private readonly CanvasTextFormat _labelFmt = new()
    {
        FontSize = 15,
        FontFamily = "Segoe UI",
        // 11.14 item 5. Smaller AND heavier at once is the point: the plate is
        // losing most of its frame, so the word itself has to carry the weight
        // the border used to.
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };
    private readonly CanvasTextFormat _bubbleFmt = new()
    {
        FontSize = 13,
        FontFamily = "Segoe UI",
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    // =======================================================================
    // Public surface
    // =======================================================================

    /// Where the picker was opened from, in this control's coordinates.
    ///
    /// This is a HINT, not a mount point. The ring always centres itself in
    /// the viewport (see Layout) so it reads as its own surface and cannot
    /// drift with the pointer or with the dial that opened it; the hint only
    /// chooses which side of the hub the chrome cluster leans toward.
    public Vector2 Anchor
    {
        get => _hint;
        set { _hint = value; _canvas.Invalidate(); }
    }

    /// <summary>Centre the ring ON <see cref="Anchor"/> instead of in the
    /// viewport. V3 K.2: opened from the dial's centre disc the wheel has to be
    /// concentric WITH THE DIAL, and K.9 says the same of the pen row's picker
    /// when the dial is off. With this false the ring keeps its old behaviour
    /// and centres itself in the viewport.</summary>
    public bool CenterOnAnchor { get; set; }

    /// <summary>The radius, in DIPs, that the CALLER occupies around
    /// <see cref="Anchor"/>. 9.3: the radial dial stays put and the wheel opens
    /// on its colour dot, so the wheel has to hold its hub chrome outside the
    /// dial and size its hole to clear it. Zero - the default - means nothing is
    /// in the hole and the hub lays out exactly as it always did.</summary>
    public float HubClearance { get; set; }

    /// <summary>The colour the caller was using when the picker opened. 10.8
    /// took the wheel's own mix row away, so nothing here reads this any more -
    /// it is kept because ColorPickerService still sets it and the Mix TOOL is
    /// the thing that now wants to know where a blend started from.</summary>
    public Color BaseColor { get; set; } = Colors.White;

    /// <summary>Raised when the user CHOOSES a colour (a swatch or a recent),
    /// as opposed to merely changing one. V3 K.11: the host closes on this.</summary>
    public event Action? Picked;

    /// The colour the picker is showing. Setting it does NOT raise ColorChanged.
    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            (_h, _s, _l) = ToHsl(value);
            _canvas.Invalidate();
        }
    }

    /// <summary>Which face is up. Reading it gives the face the user has ASKED
    /// for, which during 11.20 item 6's switch is not yet the one being drawn.
    ///
    /// <para>Setting it plays that switch: "the outgoing face's elements
    /// gravitate inwards one by one - the existing closing animation - and only
    /// then does the incoming face play its open animation. Sequential, not
    /// crossfaded." So the setter starts an EXIT and records where to go; the
    /// one animation clock hands over to the entrance when that exit lands (see
    /// OnAnimTick).</para>
    ///
    /// <para>Deliberately not two Storyboards chained on Completed: an unrooted
    /// Storyboard can be collected while it runs, its Completed never fires,
    /// and the second stage never starts - which would leave the picker with
    /// neither face on screen. This control already owns a single DispatcherTimer
    /// cascade, so the sequence is two runs of that.</para></summary>
    public ColorWheelMode Mode
    {
        get => _switchTo ?? _mode;
        set
        {
            if ((_switchTo ?? _mode) == value) return;
            // Mid-cascade, or with animation turned off in Windows: swap now.
            // There is no closing animation to run first in either case.
            if (_phase != Phase.Idle || ReduceMotion) { ApplyMode(value); return; }
            _switchTo = value;
            _phase = Phase.Exiting;
            _clockPending = true;
            _phaseT0 = Stopwatch.GetTimestamp();
            if (!_anim.IsEnabled) _anim.Start();
            // Sequential, not crossfaded, is the whole of item 6, and the
            // difference is 300 ms long - too short to photograph reliably and
            // too important to assert. The two stages say when they happen.
            GeometryProbe.Write("FACE-SWITCH", $"stage=close-begin from={_mode} to={value}");
            _canvas.Invalidate();
        }
    }

    /// <summary>Seeds the face WITHOUT the switch animation and without echoing
    /// ModeChanged back at the host that just supplied it - for opening the
    /// picker, where there is nothing on screen to animate away.</summary>
    public void ResetMode(ColorWheelMode m)
    {
        _switchTo = null;
        _mode = m;
        _canvas.Invalidate();
    }

    private void ApplyMode(ColorWheelMode m)
    {
        _switchTo = null;
        if (_mode == m) { _canvas.Invalidate(); return; }
        _mode = m;
        _canvas.Invalidate();
        ModeChanged?.Invoke(m);
    }

    /// Recently used colours, newest first. The host owns the list.
    public IReadOnlyList<Color> Recents { get; set; } = Array.Empty<Color>();

    /// Raised on every change — swatch tap, slider drag, eyedropper sample.
    public event Action<Color>? ColorChanged;
    public event Action<ColorWheelMode>? ModeChanged;
    /// The eyedropper wants the colour under this point (this control's coords).
    public event Action<Point>? SampleRequested;
    /// The user tapped away from every affordance.
    public event Action? Dismissed;

    // =======================================================================
    // State
    // =======================================================================
    private Vector2 _c;      // ring centre — resolved in Layout, never set from outside
    private Vector2 _hint;   // where the picker was opened from (bias only)
    private Color _color = Colors.Black;
    private ColorWheelMode _mode = ColorWheelMode.Copic;
    // 11.20 item 6: the face the closing cascade is on its way TO, or null.
    private ColorWheelMode? _switchTo;
    private double _h, _s, _l;          // HSL mirror of _color, kept so that a
                                        // grey does not lose its hue mid-drag
    private float _rot = 100f * Deg;    // ring rotation, radians (reference default)
    private float _vel;                 // rad/s, for the inertia glide
    // §9.4.2: the ring used to ease onto the nearest column boundary once the
    // glide had decayed. The user asked for that to go - "when spinning the
    // copic colour wheel it snaps to a set position, remove snapping" - so a
    // spin now stops where it stops. Nothing downstream wanted the detent: the
    // hit test resolves a cell from the live angle rather than from a column
    // index, so a ring resting mid-column answers exactly as well as an aligned
    // one, and the codes are drawn from the same angle either way.
    private bool _sampling;             // eyedropper armed

    // drag bookkeeping
    private int _dragArc = -1;          // 0..2 while an HSL/RGB arc is dragged
    private bool _dragRing;
    private float _lastAngle;
    private Vector2 _pressPt, _lastPt;
    private float _pathPx;              // pointer path length since the press
    private long _lastMoveTs;           // Stopwatch ticks at the previous move
    private long _spinTs;               // Stopwatch ticks at the previous glide tick
    private uint _dragPointer;          // the ONE pointer allowed to drive a drag
    // A tap is a press that never really moved. This is measured in PIXELS at
    // the point of contact rather than in radians: the old radian slop was
    // ~10px at the ring's inner edge but under 4px out at the rim, so a
    // careful pen tap on an outer swatch was read as a flick and picked
    // nothing at all. There is no long-press gesture on the ring either, so a
    // slow deliberate press now selects however long it is held down.
    private const float TapSlopPx = 12f;

    // resolved geometry (see Layout). All radii are scaled from the reference's
    // own pixel radii by `m/760`, so the three tiers keep the reference's
    // proportions on Quill's full-screen canvas.
    private float _r1In, _r1Out;        // Tier 1 (inner accent/core arc)
    private float _r2In, _r2Out;        // Tier 2 (grey ring)
    private float _rOutBase, _band;     // Tier 3+ (outer family columns)
    private float _rIn, _rOut;          // grabbable annulus [inner tier, outer edge]
    private float _rLabel, _rRecent, _chipR, _rPuck;
    // 11.21 item 1: ALWAYS MaxRings. This was 11.15 item 1's fit calculation -
    // how many of the 17 column rings still fitted inside a 15%-reduced outer
    // edge - and the answer, 9, is what the user overruled. It is now simply
    // the palette's own depth, kept as a field because the geometry caches and
    // the hit test both index by it.
    private int _rings = MaxRings;
    private Vector2 _probeC = new(float.NaN, float.NaN);   // 10.2 item 8, see Layout
    // Chrome scale. The hub shrinks with the window (the ring is fitted to the
    // viewport), and the mode plates / eyedropper / bubbles are authored at a
    // fixed pixel size, so without this they collide with Tier 1 on a small
    // window. 1.0 is the size they were drawn for.
    private float _ui = 1f;
    private float _base;                // direction from the anchor to the
                                        // viewport centre — everything that has
                                        // to stay on screen hangs off this
    // 11.20 items 7-8 split "arc" from "channel". An ARC is a ring the
    // instrument draws on - RGB now has one, HSL two - and a CHANNEL is a
    // segment of one, so three dials can share a single arc.
    private readonly float[] _arcR = new float[2];
    private int _arcCount = 1;
    private readonly int[] _chArc = new int[3];     // channel -> which arc
    private readonly float[] _chA0 = new float[3];  // the channel's value 0
    private readonly float[] _chA1 = new float[3];  // the channel's value 1
    // 11.15 item 6: each channel arc gets "its own radius and its own angular
    // span". One shared half-span is what made the three read as one dial with
    // three needles; three spans make them three sliders. They also narrow as
    // they go out, which is what keeps the outermost arc's leading end from
    // reaching up under the top chrome bar at the radius the fan now needs.
    // 11.20 item 9: the value boxes are real text fields, so they are XAML on
    // their own layer rather than marks in the Win2D pass. They exist only
    // while the instrument is at rest on an HSL/RGB face - during the cascade
    // the drawn box takes over, because a TextBox cannot gravitate inward with
    // the rest of the surface.
    private readonly Canvas _fields = new();
    private readonly TextBox[] _field = new TextBox[3];
    private bool _fieldsLive;
    private int _editing = -1;
    // Half the thickness of a channel arc, and the knob's radius - "slightly
    // wider than the arc" (item 6), so the knob reads as riding ON the track
    // rather than as a bead threaded through it.
    private float _arcW, _arcKnob;
    // How far clockwise the whole arc ladder is rolled off _base. _base points
    // at the middle of the window, so on a corner-docked dial an arc centred on
    // it swings its anticlockwise end - and the value box hung outside that end
    // - up under the top chrome bar.
    //
    // 11.20 items 7-8 make that end further out than it was: the segments now
    // run to the full 0.86 rad on the OUTER arc rather than narrowing to 0.60,
    // because three channels have to fit on one of them. Measured at 0.18: the
    // last segment's box reached y = 81.3 DIP against a bar that ends near 85.
    // 0.26 puts it at 106.8 and costs nothing at the other end, which is still
    // 149 DIP clear of the left edge.
    //
    // Shared by the draw pass and the hit test, so a tap cannot resolve against
    // the unrolled position of an arc that is drawn rolled.
    private const float ArcRoll = 0.26f;
    // The measured size of each face's word, so 11.16's chip can be snug on it
    // ("horizontal padding roughly double the vertical") instead of a fixed box
    // with a different margin round every word. Re-measured only when the type
    // size changes, not per frame.
    private readonly Vector2[] _plateSz = new Vector2[3];
    private float _plateFontMeasured = -1f;
    // 11.20 item 1. The leading above the cap line at the current code size.
    private float _codeInkMeasured = -1f, _codeInkTop;
    private readonly Vector2[] _labelPt = new Vector2[3];
    private Vector2 _dropPt, _puckPt;
    private readonly List<(Vector2 Pt, Color Col)> _chipPts = new();
    // 10.8: the OFF / 25% / 50% / 75% arc that used to sit here is GONE, and
    // with it V3 K.12's whole "arm a ratio, then the next swatch mixes" model.
    // Mixing is a TOOL now (Helpers/PigmentMix + the Mix tool), not a mode of
    // the picker, which puts the wheel back to being purely a picker and takes
    // the single biggest object out of the centre the user has now flagged
    // twice as crowded (10.4 item 15).

    // Cached tile geometry: one 10° cell per outer ring band (reused across all
    // 36 columns by rotation), plus one cell each for the two inner tiers.
    private readonly CanvasGeometry?[] _outerGeo = new CanvasGeometry?[MaxRings];
    // 11.17: a family's LAST column is short by FamGap and carries no weld, so
    // the gap it opens is background rather than a hairline the next family's
    // weld paints over. Two variants per ring, not one.
    private readonly CanvasGeometry?[] _outerGeoEnd = new CanvasGeometry?[MaxRings];
    // 9.4.1. The selection outline is NOT the cell geometry. Cell tiles are
    // WELDED - grown half a pixel outward and clockwise - so neighbouring fills
    // leave no seam between swatches. A centred stroke on a welded tile lands
    // half its width OUTSIDE the true cell on the two grown edges and on the
    // true boundary on the other two, which is exactly the user's crop of BG90:
    // doubled along the shared edges, single along the outer ones. Worse, the
    // stroke was drawn inline, so the next cell's fill painted over part of it
    // and the survivor read as a second, offset line.
    //
    // The outline therefore has its own UNWELDED tile, inset by half the stroke
    // width so the pen lies strictly inside the cell, and it is drawn in a
    // second pass after every fill so nothing can cover it. Codes are unique
    // across the palette, so there is at most one of these per frame.
    private readonly CanvasGeometry?[] _outerSelGeo = new CanvasGeometry?[MaxRings];
    private CanvasGeometry? _tier1Sel, _tier2Sel;
    private (CanvasGeometry Geo, Matrix3x2 T, Color C, float A)? _sel;
    private const float SelStroke = 2.5f;
    private CanvasGeometry? _tier1Geo, _tier2Geo;
    private bool _geoDirty = true;

    // ---- the reference's fixed column / arc tables ------------------------
    // Outer: 36 radial columns in angular order from -90°. Column i covers
    // [-90+10i, -80+10i]°; entry 0 of a column is its innermost ink.
    private static readonly CopicSwatch[][] OuterColumns = BuildOuterColumns();
    private static readonly int MaxRings = OuterColumns.Max(c => c.Length);
    // 11.17: "cells touch edge to edge within a family; families are separated
    // by visible gaps". The palette already groups the 36 columns into its 11
    // sectors, so the boundary is data, not a guess - this simply records which
    // column is the last of its sector.
    private static readonly bool[] ColEndsFamily = BuildFamilyEnds();

    // Inner arcs: each cell carries its own [A0, A1] because the group dividers
    // push later cells along. Widths are uniform within a tier (Tier?Width), so
    // one cached geometry rotated to each A0 draws the whole ring.
    private readonly record struct Cell(float A0, float A1, CopicSwatch Sw);
    private static readonly Cell[] Tier1Cells;
    private static readonly Cell[] Tier2Cells;
    private static readonly float Tier1Width;   // radians per Tier 1 chip
    private static readonly float Tier2Width;   // radians per Tier 2 chip

    static ColorWheel()
    {
        // Tier 1: a 144° arc, 3 groups, 4.5° dividers between groups only.
        (Tier1Cells, Tier1Width) = BuildInnerCells(
            CopicPalette.Tier1Categories, start: -128f, span: 144f, gap: 4.5f, gapAfterLast: false);
        // Tier 2: the full circle, 4 groups, 5.5° divider after every group.
        (Tier2Cells, Tier2Width) = BuildInnerCells(
            CopicPalette.Tier2GrayCategories, start: -90f, span: 360f, gap: 5.5f, gapAfterLast: true);

        // ---- the entrance cascade's per-column delay table ----------------
        // The reference shuffles the 36 columns ONCE at module load and uses a
        // column's rank in that shuffle as its slot in the cascade, so the ring
        // assembles as a scatter rather than a sweep. Same here: one shuffle
        // per process, and the two delay formulae verbatim from the reference.
        int n = OuterColumns.Length;
        var order = Enumerable.Range(0, n).ToArray();
        var rng = new Random();
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        ColOpenDelay = new int[n];
        ColCloseDelay = new int[n];
        for (int rank = 0; rank < n; rank++)
        {
            int colIdx = order[rank];
            ColOpenDelay[colIdx] = (int)Math.Round(30 + rank / 36.0 * 90);     // 30..120 ms
            ColCloseDelay[colIdx] = (int)Math.Round((35 - rank) / 36.0 * 50);  // ~49..0 ms
        }
        EnterSpan = EnterMs + ColOpenDelay.Max();                              // 280 ms
        ExitSpan = ExitMs + Math.Max(Tier1CloseDelay, ColCloseDelay.Max());    // 180 ms
    }

    private static CopicSwatch[][] BuildOuterColumns()
    {
        var cols = new List<CopicSwatch[]>(36);
        foreach (var sector in CopicPalette.Sectors)
            foreach (var slice in sector.Slices)
                cols.Add(slice.Colors);   // already -90°→270° in reference order
        return cols.ToArray();
    }

    private static bool[] BuildFamilyEnds()
    {
        var ends = new List<bool>(36);
        foreach (var sector in CopicPalette.Sectors)
            for (int i = 0; i < sector.Slices.Length; i++)
                ends.Add(i == sector.Slices.Length - 1);
        return ends.ToArray();
    }

    private static (Cell[] Cells, float Width) BuildInnerCells(
        CopicCategory[] cats, float start, float span, float gap, bool gapAfterLast)
    {
        int total = cats.Sum(c => c.Colors.Length);
        int groups = cats.Length;
        int gaps = gapAfterLast ? groups : groups - 1;
        float per = (span - gaps * gap) / total;

        var cells = new List<Cell>(total);
        float cur = start;
        for (int gi = 0; gi < groups; gi++)
        {
            foreach (var sw in cats[gi].Colors)
            {
                cells.Add(new Cell(cur * Deg, (cur + per) * Deg, sw));
                cur += per;
            }
            if (gapAfterLast || gi < groups - 1) cur += gap;
        }
        return (cells.ToArray(), per * Deg);
    }

    public ColorWheel()
    {
        var host = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        host.Children.Add(_canvas);
        // 11.20 item 9. A Canvas with no background is hit-test transparent
        // except over its children, so the ring keeps every press that is not
        // on a value field.
        host.Children.Add(_fields);
        BuildFields();
        Content = host;
        IsTabStop = true;
        _input = host;

        _canvas.Draw += OnDraw;
        host.PointerPressed += OnPressed;
        host.PointerMoved += OnMoved;
        host.PointerReleased += OnReleased;
        host.PointerCanceled += (_, _) => EndDrag();
        // A capture can be lost without a release — the window deactivates, a
        // flyout opens, the shell steals it. Without this the drag stayed armed
        // and EVERY later pointer move, button up or not, spun the ring: the
        // wheel appeared to track the mouse from the far side of the screen.
        host.PointerCaptureLost += (_, _) => EndDrag();
        // 10.4 item 17. A vertical notch and a horizontal (tilt / trackpad)
        // notch both spin the ring, in the same direction a drag would: the
        // ring is a wheel, and the one thing a mouse wheel over a wheel should
        // do is turn it. It feeds the SAME velocity the drag inertia uses, so a
        // flick of the scroll wheel coasts to a stop exactly like a flick of the
        // pointer rather than stepping.
        host.PointerWheelChanged += OnWheelScroll;
        _spin.Tick += OnSpinTick;
        _anim.Tick += OnAnimTick;
        SizeChanged += (_, _) => { _geoDirty = true; _canvas.Invalidate(); };
        Unloaded += (_, _) =>
        {
            _spin.Stop();
            _anim.Stop();
            DisposeGeometry();
            _canvas.RemoveFromVisualTree();
        };
    }

    /// <summary>11.20 item 9: "the value boxes must be typeable - a real text
    /// field, not a readout."
    ///
    /// <para>Styled to the box 11.15 item 6 describes and 11.16 measured - a
    /// white rounded rect, a hairline border, dark centred text - which means
    /// overriding the TextBox template's own brushes rather than only the
    /// control's properties: the default template repaints background, border
    /// and foreground from theme resources on hover and on focus, so setting
    /// Background alone leaves a box that changes colour when it is used. The
    /// white is deliberate and settled: these sit ON a saturated arc, not on
    /// paper, so they take their contrast from the arc.</para></summary>
    private void BuildFields()
    {
        var ground = new SolidColorBrush(Color.FromArgb(245, 253, 253, 252));
        var edge = new SolidColorBrush(Color.FromArgb(70, 22, 23, 26));
        var ink = new SolidColorBrush(Color.FromArgb(255, 24, 24, 26));
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var tb = new TextBox
            {
                TextAlignment = TextAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Background = ground,
                Foreground = ink,
                BorderBrush = edge,
                IsSpellCheckEnabled = false,
                Visibility = Visibility.Collapsed,
            };
            foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver",
                                        "TextControlBackgroundFocused", "TextControlBackgroundDisabled" })
                tb.Resources[key] = ground;
            foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                                        "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
                tb.Resources[key] = edge;
            foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver",
                                        "TextControlForegroundFocused" })
                tb.Resources[key] = ink;
            // The focused template thickens the bottom edge into an accent
            // underline. 11.16's box has one hairline all the way round.
            tb.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
            tb.GotFocus += (_, _) => _editing = idx;
            tb.LostFocus += (_, _) => { CommitField(idx); if (_editing == idx) _editing = -1; };
            tb.KeyDown += (_, ke) =>
            {
                if (ke.Key == Windows.System.VirtualKey.Enter)
                {
                    CommitField(idx);
                    ke.Handled = true;
                }
                else if (ke.Key == Windows.System.VirtualKey.Escape)
                {
                    _field[idx].Text = ChannelText(idx);
                    ke.Handled = true;
                }
            };
            _field[i] = tb;
            _fields.Children.Add(tb);
        }
    }

    /// Reads one typed value back into the colour. Lenient on purpose: the box
    /// shows "317°" and "55%", and someone retyping one of those will leave the
    /// unit on it. Anything that will not parse simply snaps back.
    private void CommitField(int i)
    {
        string raw = (_field[i].Text ?? string.Empty).Trim().TrimEnd('%', '\u00B0', ' ');
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out float v) &&
            !float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
        {
            _field[i].Text = ChannelText(i);
            return;
        }
        // SetChannel clamps, so an out-of-range number is pinned rather than
        // refused - which is what a slider would have done with the same drag.
        SetChannel(i, _mode == ColorWheelMode.Hsl
            ? (i == 0 ? v / 360f : v / 100f)
            : v / 255f);
        _field[i].Text = ChannelText(i);
    }

    /// Puts the live fields where the drawn boxes would be, or takes them away.
    /// Called at the END of the draw pass, once Layout has resolved the arcs,
    /// and every write is guarded on a change so this cannot start a layout
    /// loop with the pass that invoked it.
    private void SyncFields()
    {
        for (int i = 0; i < 3; i++)
        {
            var tb = _field[i];
            if (!_fieldsLive)
            {
                if (tb.Visibility != Visibility.Collapsed) tb.Visibility = Visibility.Collapsed;
                continue;
            }
            double w = BoxW, h = BoxH;
            if (Math.Abs(tb.Width - w) > 0.01) tb.Width = w;
            if (Math.Abs(tb.Height - h) > 0.01) tb.Height = h;
            if (Math.Abs(tb.FontSize - _bubbleFmt.FontSize) > 0.01) tb.FontSize = _bubbleFmt.FontSize;
            if (Math.Abs(tb.CornerRadius.TopLeft - h * 0.28) > 0.01)
                tb.CornerRadius = new CornerRadius(h * 0.28);
            var p = FieldCentre(i);
            double left = p.X - w * 0.5, top = p.Y - h * 0.5;
            if (Math.Abs(Canvas.GetLeft(tb) - left) > 0.01) Canvas.SetLeft(tb, left);
            if (Math.Abs(Canvas.GetTop(tb) - top) > 0.01) Canvas.SetTop(tb, top);
            // Not while it is being typed into: the caret is mid-number and
            // rewriting the text would move it.
            if (_editing != i)
            {
                string t = ChannelText(i);
                if (tb.Text != t) tb.Text = t;
            }
            if (tb.Visibility != Visibility.Visible) tb.Visibility = Visibility.Visible;
        }
    }

    private void DisposeGeometry()
    {
        for (int i = 0; i < _outerGeo.Length; i++) { _outerGeo[i]?.Dispose(); _outerGeo[i] = null; }
        for (int i = 0; i < _outerGeoEnd.Length; i++) { _outerGeoEnd[i]?.Dispose(); _outerGeoEnd[i] = null; }
        for (int i = 0; i < _outerSelGeo.Length; i++) { _outerSelGeo[i]?.Dispose(); _outerSelGeo[i] = null; }
        _tier1Geo?.Dispose(); _tier1Geo = null;
        _tier2Geo?.Dispose(); _tier2Geo = null;
        _tier1Sel?.Dispose(); _tier1Sel = null;
        _tier2Sel?.Dispose(); _tier2Sel = null;
        _sel = null;
    }

    // =======================================================================
    // Geometry
    // =======================================================================
    // The ring is centred in the viewport and drawn at the reference's own
    // scale — NOT shrunk to fit. The reference mounts this wheel in a 260x260
    // SVG whose viewBox is "0 0 260 260", i.e. one SVG unit per CSS pixel, and
    // lets overflow-visible spill the 690-unit radius across the page; the
    // outer families are reached by spinning, not by zooming out. Copying that
    // is the whole point: fitting all 690 units into half a window's height is
    // what collapsed a band to 13 DIP and its code to 6. Nothing here reads the
    // pointer or the opener, so the wheel is fixed for as long as it is up.
    private void Layout(float w, float h)
    {
        var mid = new Vector2(w * 0.5f, h * 0.5f);
        _c = CenterOnAnchor ? _hint : mid;
        // One reference unit == one DIP. Fixed, so the swatches are the same
        // comfortable size at every window size and only HOW MUCH of the ring
        // you can see changes.
        float s = 1f;
        // The one concession: below roughly 692 DIP of usable space the grey
        // ring itself would be off screen in every direction at once and the
        // picker would open on an empty middle, so the ring is allowed to
        // shrink to keep that full circle reachable. No window Quill is used at
        // comes near this, and it never scales the ring UP.
        float halfMin = Math.Min(w, h) * 0.5f - EdgeMargin;
        if (CenterOnAnchor)
        {
            // 9.3. Centred on a corner-docked dial the ring necessarily overhangs
            // the window; the spec says clip it there and SHRINK so more of it
            // lands on screen, rather than moving the dial. Two demands pull
            // opposite ways - shrinking puts more ring on screen, but the hole
            // must stay wider than the caller PLUS room for this wheel's own hub
            // chrome, or the two controls sit on top of each other. The hub wins
            // and the overhang is clipped, because a ring whose middle you cannot
            // use is worse than a ring that runs off the edge.
            float reach = Math.Min(Math.Min(_c.X, w - _c.X), Math.Min(_c.Y, h - _c.Y)) - EdgeMargin;
            float fits = Math.Max(0f, reach) / Tier2OuterRef;
            float needs = (HubClearance + HubRoom) / Tier1InnerRef;
            s = Math.Clamp(Math.Max(fits, needs), 0.5f, 1f);
        }
        else if (halfMin < Tier2OuterRef) s = Math.Max(0.45f, halfMin / Tier2OuterRef);

        // 11.12 item 3: "widen the width of the cells" - the cell's RADIAL
        // extent, from the ring's inner edge outward.
        //
        // The old line scaled every radius by one s, so the only way to make a
        // cell wider was to make the hole wider with it - and the hole is the
        // one thing 11.2 item 15 pins, because the wheel is centred on the
        // dial's colour dot and the dial has to stay usable inside it. So the
        // hole keeps its own number and the BANDS get their own scale on top,
        // laid out cumulatively outward at the reference's own proportions:
        // 17 units of Tier 1, a 5 unit gap, 21 of Tier 2, another 5, then
        // columns of 21. At CellScale 1 this is byte-identical to the line it
        // replaces; above 1 every ring past the hole gets thicker and nothing
        // inside it moves at all.
        _r1In = 285f * s;
        float u = s * CellScale;               // one reference unit, in DIP
        _band = 21f * u;                       // Tier 3+ columns - 11.15 item 4
        // 11.20 item 2 / 11.21: every ring takes the OUTERMOST ring's depth, so
        // Tier 1 and Tier 2 are one _band each rather than the reference's 17
        // and 21 units at 0.62. Only the hairline BETWEEN them is still a
        // reference proportion - it separates two cells, it is not one.
        float su = u * SpineGapScale;
        _r1Out = _r1In + _band;                // Tier 1 inner accent arc
        _r2In = _r1Out + 5f * su;
        _r2Out = _r2In + _band;                // Tier 2 grey ring
        _rOutBase = _r2Out + _band * SpineGap; // 11.17's band of bare background
        _rIn = _r1In;
        // 11.21 items 1-3. Every ring the palette has, drawn - the outer edge
        // is whatever that comes to, not a target to floor against. The clamp
        // is gone with the floor: _rings IS MaxRings, and the field survives
        // only because the hit test and the geometry caches index by it.
        _rings = MaxRings;
        _rOut = _rOutBase + _rings * _band;
        // The reference sets its SVG code text to 7 units in a 21-unit band, a
        // third of the band. Segoe UI through Win2D at that size is a smear and
        // the band has room for far more: half the band still leaves ~3.5 DIP of
        // padding above and below the line box, and the narrowest cell in the
        // whole wheel (Tier 2, ~7.3° at r≈317, so ~40 DIP of arc) still swallows
        // the longest grey code at that size.
        // 11.15 item 3: 20% off, and off the 11.13 size - which was the 14 DIP
        // ceiling, since a 35 DIP band had long since run past it. Item 1 is a
        // geometry scale and does not compound into this.
        _codeFmt.FontSize = Math.Clamp(_band * 0.5f, 7f, 14f) * Elem;

        // The chrome lives in the hub, so it is sized off the HOLE rather than
        // the window: it can never collide with Tier 1 whatever the shape.
        float hole = _r1In;
        // 9.3: when a caller SITS in the hole - the dial does - the hub's usable
        // room is the ANNULUS between it and Tier 1, not the whole disc. The
        // fractions are unchanged; they are simply taken across that annulus, so
        // with no clearance this is byte-identical to what it was.
        // The chrome starts OUTSIDE the caller, full stop - the old
        // "or 55% of the hole" cap was a guard against a hole too small to
        // hold any chrome at all, and it fired as soon as 10.8 shrank the
        // hub, laying the mode plates over the dial's own popped sector.
        // Keeping 70 DIP of annulus expresses that guard directly.
        float lo = Math.Min(HubClearance, Math.Max(0f, hole - 70f));
        float band = hole - lo;
        // 11.12: the hub's own scale. band is the annulus between the caller
        // and Tier 1 - 82 DIP with a dial in the hole - and dividing that by
        // 180 pinned _ui at its 0.60 floor in every real case, which is how
        // COPIC / HSL / RGB ended up at 6.6 DIP of type. The divisor is now the
        // annulus the hub actually gets, so the floor stops being the answer.
        // 11.13. The chrome's arc radius grows with the hole, but its ITEMS
        // must not grow with it: at 11.12's sizes and this hole a mode plate is
        // 128 DIP wide and five of them do not fit abreast in the quadrant a
        // corner-docked dial leaves on screen. The ceiling holds them near the
        // size the user approved while the ring grows past them. Raising it
        // again means moving the cluster off one arc, not just more clamp.
        //
        // 11.14 asks whether item 5 has bought the ceiling out. Measured: it
        // has bought out the reason, and not the ceiling. band is 140 here, so
        // uncapped _ui would be 1.40; a 68-wide plate at 1.40 is 95 DIP against
        // a 113 DIP gap, so the FIT objection is gone. But raising the cap puts
        // the plate type at 15.3 x 1.40 = 21.4 DIP against the 19.8 on screen
        // now - BIGGER, which is the opposite of what item 5 asked for. The cap
        // is what makes item 5's 15% visible at all (19.8 -> 16.8), so it
        // stays, now on its own merits rather than for want of room.
        _ui = Math.Clamp(band / 100f, 0.80f, 1.10f);
        // 11.12 item 1: "the face labels are far too small." 11 -> 18.
        // 11.15 item 3 takes 20% back off that, against a bold face.
        _labelFmt.FontSize = 18f * Elem * _ui;
        _bubbleFmt.FontSize = 15f * Elem * _ui;
        // 11.12: the plates are 42 DIP tall now against 26, so they reach
        // inward to where the recents row used to sit and the first chip landed
        // ON the HSL plate. The row moves in; the two bands no longer meet.
        // 11.13 re-spaces all three bands across the wider annulus.
        _rRecent = lo + band * 0.13f;
        // 11.12 item 4: the recents dots scale with everything else - and 11.15
        // item 3 names them among the "other elements" that come down 20%.
        _chipR = Math.Clamp(band * 0.075f, 7f, 14f) * Elem;
        // 10.8 took the mix row out from between these two, so the mode plates
        // move in to where it was rather than leaving a gap the size of a
        // control that no longer exists.
        // 11.15 item 5. The number is set by the plate's CORNER, not its
        // centre: the chip is axis-aligned and rides a radial arc, so its
        // circumscribed radius is what has to clear the hole. At 0.72 that
        // corner reached 257.37 against a 256.62 hole and clipped the first row
        // of Tier 1. 0.66 puts it at 248.97 - 7.65 DIP of margin outward, 52.45
        // inward to the dial.
        _rLabel = lo + band * 0.66f;
        // 11.13: the puck comes OFF the plate arc. Five items abreast no longer
        // fit the visible quadrant, and the annulus the bigger hole opened up is
        // exactly where a sixth band can go - so the puck takes its own radius
        // between the recents row and the plates, at the leading end.
        _rPuck = lo + band * 0.36f;
        // 9.4.3: the HSL and RGB faces used to be fitted to the VIEWPORT
        // (0.42 / 0.58 / 0.74 of half the window), which put them far inside the
        // COPIC face - switching mode collapsed the control toward the middle
        // and the three arcs read as a different, smaller instrument. They now
        // take the COPIC face's own bands: Tier 1, Tier 2 and the first column
        // ring. One control, one radius, whichever face is up.
        // 11.15 item 6. The three arcs used to sit on Tier 1, Tier 2 and the
        // first column ring, which after 11.17 narrowed the spine are 20 DIP
        // apart - closer than one arc is thick, let alone an arc plus a knob
        // plus a value box. They take their own ladder now, pitched so that a
        // box hung outside one arc still clears the next arc's inner edge by
        // more than its own height. Nothing is measured off the tiers, so the
        // HSL and RGB faces no longer inherit the COPIC face's band structure.
        _arcW = 9f * _ui * Elem;
        _arcKnob = _arcW + 4.5f * _ui * Elem;
        float pitch = _arcW * 2f + 72f * _ui * Elem;
        float arc0 = _r1In + 40f * _ui * Elem;
        // 11.20 items 7 and 8, superseding 11.15 item 6's one-arc-per-channel
        // ladder. RGB is "three dials on a SINGLE arc, ordered anticlockwise:
        // red, green, blue"; HSL is "two arcs - the first carries the hue
        // wheel, the second carries, anticlockwise, saturation then lightness."
        //
        // Anticlockwise is the word both items use, and on a y-down canvas that
        // is DECREASING angle. So an arc is filled from its clockwise end
        // downward, and the VALUE runs the same way inside each segment - the
        // ordering and the scale then progress in one direction rather than
        // arguing with each other.
        const float SegGap = 0.16f;
        float top = _base + ArcRoll + 0.86f, bot = _base + ArcRoll - 0.86f;
        if (_mode == ColorWheelMode.Hsl)
        {
            _arcCount = 2;
            _arcR[0] = arc0;
            _arcR[1] = arc0 + pitch;
            // The hue wheel gets a whole arc to itself, so it is the one
            // channel whose segment IS its arc.
            Segment(0, 0, top, bot);
            float half = (top - bot - SegGap) * 0.5f;
            Segment(1, 1, top, top - half);
            Segment(2, 1, top - half - SegGap, bot);
        }
        else
        {
            _arcCount = 1;
            // The outer of the two radii, not the inner: one arc carrying three
            // channels needs the circumference, and a value box hung off a knob
            // at the inner radius would sit where the second arc no longer is.
            _arcR[0] = arc0 + pitch;
            _arcR[1] = _arcR[0];
            float third = (top - bot - SegGap * 2f) / 3f;
            for (int i = 0; i < 3; i++)
            {
                float s0 = top - i * (third + SegGap);
                Segment(i, 0, s0, s0 - third);
            }
        }

        // Which way the hub's chrome faces. Centred in the viewport that is the
        // direction of the thing that opened the picker; centred ON that thing
        // there is no such direction, so the chrome leans toward the middle of
        // the window, which is the only direction guaranteed to be on screen.
        var lean = CenterOnAnchor ? mid - _c : _hint - _c;
        _base = lean.LengthSquared() < 4f ? 0f : MathF.Atan2(lean.Y, lean.X);

        // 10.2 item 8: the ring's centre, converted into the SAME space the
        // caller's anchor was given in, so the two probe lines are directly
        // comparable. Written once per open rather than once per frame - this
        // runs inside the draw pass and a file append per frame would be felt
        // on a spin.
        if (GeometryProbe.On && _probeC != _c)
        {
            _probeC = _c;
            try
            {
                var t = TransformToVisual((UIElement?)XamlRoot?.Content ?? this);
                GeometryProbe.Point("WHEEL-CENTRE", t.TransformPoint(new Point(_c.X, _c.Y)),
                    $"local={_c.X:F2},{_c.Y:F2} anchor={_hint.X:F2},{_hint.Y:F2} " +
                    $"onAnchor={(CenterOnAnchor ? 1 : 0)} hub={HubClearance:F2} scale={s:F3} hole={_r1In:F2} viewport={w:F0}x{h:F0} " +
                    $"band={_band:F2} rOut={_rOut:F2} ui={_ui:F3} code={_codeFmt.FontSize:F2} " +
                    $"lo={lo:F2} hubBand={band:F2} rRecent={_rRecent:F2} chipR={_chipR:F2} " +
                    $"rPuck={_rPuck:F2} rLabel={_rLabel:F2} rings={_rings}/{MaxRings} " +
                    $"r1Out={_r1Out:F2} r2In={_r2In:F2} r2Out={_r2Out:F2} rOutBase={_rOutBase:F2} " +
                    $"spine={(_r2Out - _r2In):F2} gapBand={(_rOutBase - _r2Out):F2} " +
                    $"label={_labelFmt.FontSize:F2} bubble={_bubbleFmt.FontSize:F2} " +
                    $"arcs={_arcCount} arcR={_arcR[0]:F1},{_arcR[1]:F1} arcW={_arcW:F2} " +
                    $"drop={44f * _ui * Elem:F1} puck={20f * _ui * Elem:F1} " +
                    $"surface={SurfaceScale:F2} elem={Elem:F3} codeInkTop={_codeInkTop:F2}");
            }
            catch { }
        }

        // 10.4 item 15: "COPIC, HSL and RGB are too close together." They were
        // 0.33 rad apart on an arc whose radius shrinks with the hub, so on a
        // docked dial the three plates were nearly touching. The step is now
        // 0.46 rad, and because that widens the whole cluster the puck and the
        // eyedropper move out with it rather than being left inside it.
        // Trailing the fan rather than leading it: spread out to item 15's
        // spacing, a cluster that STARTS with the puck reaches up past the
        // dial and puts it under the top chrome bar.
        // 11.13. Four items on the arc, not five, at 0.52 rad - which is a
        // 113 DIP gap at this radius against a 101 DIP plate. The whole fan is
        // then rolled 0.18 rad clockwise: at the bigger radius the leading plate
        // was reaching up into the top chrome bar, and rolling the cluster is
        // the one correction that costs nothing, since the far end still lands
        // well inside the window.
        const float Step = 0.52f, Roll = 0.18f;
        _labelPt[0] = At(_rLabel, _base + Roll - Step * 1.5f);
        _labelPt[1] = At(_rLabel, _base + Roll - Step * 0.5f);
        _labelPt[2] = At(_rLabel, _base + Roll + Step * 0.5f);
        _dropPt = At(_rLabel, _base + Roll + Step * 1.5f);
        // TRAILING the fan, not tucked under its first plate. A plate is an
        // axis-aligned rect 101 DIP wide, so its inner CORNER reaches about
        // 32 DIP closer to the centre than its midpoint does - enough to
        // swallow a puck that only cleared the midpoint's radius.
        _puckPt = At(_rPuck, _base + Roll + Step * 2.4f);

        _chipPts.Clear();
        int n = Math.Min(12, Recents.Count);
        if (n > 0)
        {
            float step = _chipR * 2.4f / _rRecent;
            float a0 = _base - step * (n - 1) * 0.5f;
            for (int i = 0; i < n; i++)
                _chipPts.Add((At(_rRecent, a0 + i * step), Recents[i]));
        }
    }

    private Vector2 At(float r, float a) => _c + new Vector2(r * MathF.Cos(a), r * MathF.Sin(a));

    /// Records where one channel lives: which arc, and the two angles its
    /// value 0 and value 1 sit at (11.20 items 7-8).
    private void Segment(int ch, int arc, float a0, float a1)
    {
        _chArc[ch] = arc;
        _chA0[ch] = a0;
        _chA1[ch] = a1;
    }

    /// Where an angle falls along a channel's segment: 0 at its value-zero end,
    /// 1 at the other, outside [0,1] past either. Norm keeps the ±π seam from
    /// turning a near miss into a hit on the far side of the ring.
    private float SegT(int ch, float a)
    {
        float span = _chA1[ch] - _chA0[ch];
        if (MathF.Abs(span) < 1e-4f) return 0f;
        return Norm(a - _chA0[ch]) / span;
    }

    private static float Norm(float a)
    {
        while (a <= -MathF.PI) a += MathF.Tau;
        while (a > MathF.PI) a -= MathF.Tau;
        return a;
    }

    // Can any part of the wedge [a0, a1] beyond radius rIn reach the viewport?
    //
    // Now that the ring overflows, this decides most of what is drawn, so it has
    // to be EXACT rather than cheap: the hit-test answers for every swatch the
    // arithmetic says is under the pointer, so a cull that drops a chip which
    // still has pixels on screen would put a live, invisible target there —
    // precisely the kind of render/hit-test disagreement that made this control
    // unpickable before. (The old midpoint sample did exactly that: an outer
    // cell is ~120 DIP of arc at r=690, so its centre can sit 60 DIP past the
    // edge while its near corner is still visible.)
    //
    // The viewport is a rectangle, so along a ray at θ the visible run is
    // [0, R(θ)] with R = min(wall_x/|cos θ|, wall_y/|sin θ|), each wall being the
    // one that ray actually heads toward (see View). R peaks on the axes and
    // bottoms out on the diagonals, so over an interval its maximum is at an
    // endpoint or at whichever axis direction the interval contains — five
    // candidates, exact. p folds in the entrance cascade's scale about the
    // centre, which only ever brings MORE of the ring into view.
    // The viewport as four SIGNED distances from the ring's centre. It used to be
    // two half-extents, which is only right while the ring is centred in the
    // window; once K.2 centres it on a corner-docked dial the window is entirely
    // off to one side, and a symmetric box around the dial is about four times
    // the real viewport. Everything in that phantom three-quarters passed the
    // cull and was filled AND LABELLED off screen — which is exactly the cost
    // K.3 could least afford, having just made every marker code draw on every
    // frame. Signed reaches cull to the actual window instead.
    private readonly struct View
    {
        public readonly float Left, Right, Up, Down;
        public View(Vector2 c, float w, float h)
        {
            Left = MathF.Max(0f, c.X) + 2f;
            Right = MathF.Max(0f, w - c.X) + 2f;
            Up = MathF.Max(0f, c.Y) + 2f;
            Down = MathF.Max(0f, h - c.Y) + 2f;
        }
    }

    private static bool WedgeVisible(float a0, float a1, float rIn, float p, View v)
    {
        float reach = MathF.Max(RayReach(a0, v), RayReach(a1, v));
        for (int k = -2; k <= 2 && reach < float.MaxValue; k++)
        {
            float axis = k * MathF.PI / 2f;
            if (axis >= a0 && axis <= a1) reach = MathF.Max(reach, RayReach(axis, v));
        }
        float scale = p >= 0.999f ? 1f : 0.5f + 0.5f * p;
        return reach > rIn * scale;
    }

    // How far the ray at angle `a` travels from the centre before it leaves the
    // window. cos/sin are taken SIGNED and pick the near or far wall accordingly;
    // both are 2π-periodic, so an angle outside (-π, π] still answers correctly
    // and the callers do not have to normalise a1.
    private static float RayReach(float a, View v)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        float ca = MathF.Abs(c), sa = MathF.Abs(s);
        float rx = ca < 1e-6f ? float.MaxValue : (c > 0 ? v.Right : v.Left) / ca;
        float ry = sa < 1e-6f ? float.MaxValue : (s > 0 ? v.Down : v.Up) / sa;
        return MathF.Min(rx, ry);
    }

    // An annular tile spanning [a0, a1] between r0 and r1, built at unrotated
    // angles and re-used for every column via a rotation transform.
    private CanvasGeometry ArcTile(ICanvasResourceCreator rc, float r0, float r1, float a0, float a1)
    {
        using var b = new CanvasPathBuilder(rc);
        b.BeginFigure(At(r0, a0));
        b.AddArc(At(r0, a1), r0, r0, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        b.AddLine(At(r1, a1));
        b.AddArc(At(r1, a0), r1, r1, 0f, CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
        b.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(b);
    }

    // One 10° outer cell at ring band `ring`, FLUSH with its neighbours. The
    // reference hands createArcTilePath() the slice's own [startAngle, endAngle]
    // and rInner = 333 + ring*21, rOuter = rInner + 21 — no inset anywhere, so
    // chips share edges and the wheel reads as one sheet of colour. The gutter
    // this used to carry was doing two kinds of harm: it looked like a gap
    // round every swatch, and it put the painted tile a pixel inside the band
    // the hit-test answers for, so the two stopped describing the same shape.
    // Cached and re-used for all 36 columns.
    private CanvasGeometry OuterCell(ICanvasResourceCreator rc, int ring, bool endsFamily)
    {
        var slot = endsFamily ? _outerGeoEnd : _outerGeo;
        if (slot[ring] is { } cached) return cached;
        float r0 = _rOutBase + ring * _band;
        // Welded outward and clockwise: rings are drawn inner-to-outer and
        // columns in increasing angle, so the overlap is always covered by the
        // neighbour drawn next. A family's last column is the one place where
        // there IS no next neighbour to cover it - 11.17 wants background
        // showing there - so that variant is short by FamGap and unwelded.
        float span = endsFamily ? ColStep - FamGap : ColStep + Weld / MathF.Max(r0, 1f);
        return slot[ring] = ArcTile(rc, r0, r0 + _band + Weld, 0f, span);
    }

    // The two inner tiers are isolated bands, so they are welded only along the
    // shared side — the next chip in the group. The wider breaks left between
    // CATEGORIES are the reference's own group dividers (4.5° in Tier 1, 5.5°
    // in Tier 2), not gutters, and are left alone.
    private CanvasGeometry Tier1Cell(ICanvasResourceCreator rc)
    {
        if (_tier1Geo is { } cached) return cached;
        return _tier1Geo = ArcTile(rc, _r1In, _r1Out, 0f, Tier1Width + Weld / MathF.Max(_r1In, 1f));
    }

    private CanvasGeometry Tier2Cell(ICanvasResourceCreator rc)
    {
        if (_tier2Geo is { } cached) return cached;
        return _tier2Geo = ArcTile(rc, _r2In, _r2Out, 0f, Tier2Width + Weld / MathF.Max(_r2In, 1f));
    }

    // The three selection tiles: no weld, and inset by half the pen so the
    // stroke cannot reach a neighbour's territory (9.4.1).
    private const float SelInset = SelStroke * 0.5f + 0.25f;

    // Not cached per family-end variant: the outline is inset on every side
    // anyway, so the widest it can be is the narrow cell's own painted extent
    // less the inset, and one shape that never reaches past a short cell is
    // preferable to two that each only fit one of them.
    private CanvasGeometry OuterSel(ICanvasResourceCreator rc, int ring)
    {
        if (_outerSelGeo[ring] is { } cached) return cached;
        float r0 = _rOutBase + ring * _band;
        float da = SelInset / MathF.Max(r0, 1f);
        return _outerSelGeo[ring] =
            ArcTile(rc, r0 + SelInset, r0 + _band - SelInset, da, ColStep - FamGap - da);
    }

    private CanvasGeometry Tier1Sel(ICanvasResourceCreator rc)
    {
        if (_tier1Sel is { } cached) return cached;
        float da = SelInset / MathF.Max(_r1In, 1f);
        return _tier1Sel = ArcTile(rc, _r1In + SelInset, _r1Out - SelInset, da, Tier1Width - da);
    }

    private CanvasGeometry Tier2Sel(ICanvasResourceCreator rc)
    {
        if (_tier2Sel is { } cached) return cached;
        float da = SelInset / MathF.Max(_r2In, 1f);
        return _tier2Sel = ArcTile(rc, _r2In + SelInset, _r2Out - SelInset, da, Tier2Width - da);
    }

    // =======================================================================
    // Drawing
    // =======================================================================
    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs e)
    {
        float w = (float)sender.ActualWidth, h = (float)sender.ActualHeight;
        if (w < 2 || h < 2) return;
        if (_geoDirty) { DisposeGeometry(); _geoDirty = false; }
        Layout(w, h);
        var ds = e.DrawingSession;

        // The clock starts on the first frame after BeginEnter, not on the
        // call itself: the very first open has to realise the CanvasControl,
        // and starting from the call would eat the head of the cascade.
        if (_clockPending) { _animT0 = Stopwatch.GetTimestamp(); _clockPending = false; }

        // Tier 1's clock also carries the hub chrome, so the surface arrives
        // as one gesture (the reference's chrome sits outside the SVG group
        // that animates, but here they share a canvas).
        float p1 = TierProgress(Tier1OpenDelay, Tier1CloseDelay);

        // 11.19 takes the scrim away. It used to be drawn here, full-window, and
        // it was doing two jobs: separating the wheel from the page, and dimming
        // the chrome. The user wants only the second, and wants it done to the
        // chrome itself - "the page, its ink, and the radial dial stay at full
        // opacity", and in the reference the dial is fully saturated against
        // faded top-bar icons, which a scrim over everything cannot produce.
        // ColorPickerService.Dimming now carries that, so nothing is painted
        // over the page at all.
        //
        // The consequence lands here: every mark this control makes outside the
        // ring used to be authored against a fixed dark backdrop, so all of it
        // now comes from PageTheme instead (section 0 - never a hardcoded grey).

        // 11.20 item 9's hand-off, decided BEFORE anything is drawn so the one
        // pass agrees with itself: the live text fields own the value boxes
        // only while the instrument is at rest on an arc face. Mid-cascade -
        // including the face switch of item 6 - the drawn box stands in, since
        // a XAML control cannot be scaled about the wheel centre frame by frame
        // along with everything else.
        _fieldsLive = _mode != ColorWheelMode.Copic && _phase == Phase.Idle && _switchTo == null;

        if (_mode == ColorWheelMode.Copic) DrawRing(sender, ds, w, h);
        else DrawArcs(ds, p1);

        if (p1 > 0.002f)
        {
            ds.Transform = TierTransform(p1);
            DrawRecents(ds, p1);
            DrawChrome(ds, p1);
            ds.Transform = Matrix3x2.Identity;
        }

        SyncFields();
    }

    private void DrawRing(ICanvasResourceCreator rc, CanvasDrawingSession ds, float w, float h)
    {
        // How far the viewport reaches from the ring's centre, per side, so the
        // cull is exact whether the ring is centred in the window or on the
        // corner-docked dial (K.2).
        var view = new View(_c, w, h);
        MeasureCodeInk(rc);
        var near = CopicPalette.Nearest(_color.R, _color.G, _color.B);
        // V3 K.3. This used to be `Math.Abs(_vel) < 1.2f` - every marker code
        // vanished the moment the ring was dragged or flicked, which is exactly
        // when you are hunting for one. The codes ARE the wheel's index; the
        // cull above already keeps the drawn set to what is on screen, so they
        // are simply always drawn now.
        const bool labels = true;

        // Tier 1 (inner arc) then Tier 2 (grey ring): disjoint bands, uniform
        // cell width, so one cached geometry rotated to each cell's A0 does it.
        // Each tier carries its own slot in the entrance cascade.
        _sel = null;
        DrawInnerTier(ds, view, Tier1Cells, Tier1Cell(rc), Tier1Sel(rc), _r1In, (_r1In + _r1Out) * 0.5f,
            near, labels, TierProgress(Tier1OpenDelay, Tier1CloseDelay));
        DrawInnerTier(ds, view, Tier2Cells, Tier2Cell(rc), Tier2Sel(rc), _r2In, (_r2In + _r2Out) * 0.5f,
            near, labels, TierProgress(Tier2OpenDelay, Tier2CloseDelay));

        // Tier 3+ (outer family columns): 36 fixed 10° columns, each a radial
        // stack of its own depth. At rest most of them run off the window — by
        // design; the deep inks are brought back by spinning the column round to
        // the horizontal, where the viewport reaches furthest.
        for (int col = 0; col < OuterColumns.Length; col++)
        {
            var stack = OuterColumns[col];
            if (stack.Length == 0) continue;
            float p = TierProgress(ColOpenDelay[col], ColCloseDelay[col]);
            if (p <= 0.002f) continue;              // not arrived yet / already gone
            bool endsFam = ColEndsFamily[col];
            float span = endsFam ? ColStep - FamGap : ColStep;
            float a0 = Norm(OuterStart + col * ColStep + _rot);
            float midA = a0 + span * 0.5f;
            if (!WedgeVisible(a0, a0 + span, _rOutBase, p, view)) continue;

            // The column's own gravity drop: scaled about the WHEEL centre, so
            // the ring implodes/explodes as one body rather than each column
            // shrinking into itself.
            var grow = TierTransform(p);
            var rotate = Matrix3x2.CreateRotation(a0, _c) * grow;
            // 11.21 item 1: every ring this column HAS. _rings is MaxRings, so
            // the Min only ever picks the column's own depth - it is kept
            // because a column shorter than the deepest one still has to stop
            // at its own last ink, and because the caches are sized by _rings.
            // Nothing is dropped: no swatch in the palette goes undrawn, and if
            // a future size instruction cannot be met with all of them on
            // screen, the palette wins and the conflict gets reported.
            int depth = Math.Min(stack.Length, _rings);
            for (int ring = 0; ring < depth; ring++)
            {
                var sw = stack[ring];
                var col32 = Color.FromArgb(255, sw.R, sw.G, sw.B);
                ds.Transform = rotate;
                ds.FillGeometry(OuterCell(rc, ring, endsFam), Fade(col32, p));
                if (sw.Code == near.Code)
                    _sel = (OuterSel(rc, ring), rotate,
                            LabelInk(col32), p);
                ds.Transform = grow;

                if (labels) DrawCode(ds, sw.Code, _rOutBase + (ring + 0.5f) * _band, midA,
                                     _band, span, col32, p);
                ds.Transform = Matrix3x2.Identity;
            }
        }

        // 9.4.1: the one selection outline, last, so no later fill covers it.
        if (_sel is { } hit)
        {
            ds.Transform = hit.T;
            ds.DrawGeometry(hit.Geo, Fade(hit.C, hit.A), SelStroke);
            ds.Transform = Matrix3x2.Identity;
        }
    }

    // Draws one inner tier: fill each cell by rotating the shared geometry to
    // the cell's start angle, outline the nearest swatch, and label it.
    private void DrawInnerTier(CanvasDrawingSession ds, View view,
        Cell[] cells, CanvasGeometry geo, CanvasGeometry sel, float rIn, float rMid,
        CopicSwatch near, bool labels, float p)
    {
        if (p <= 0.002f) return;
        var grow = TierTransform(p);
        foreach (var cell in cells)
        {
            float a0 = cell.A0 + _rot;
            float mid = (cell.A0 + cell.A1) * 0.5f + _rot;
            float n0 = Norm(a0);
            if (!WedgeVisible(n0, n0 + (cell.A1 - cell.A0), rIn, p, view)) continue;

            var col32 = Color.FromArgb(255, cell.Sw.R, cell.Sw.G, cell.Sw.B);
            var tile = Matrix3x2.CreateRotation(a0, _c) * grow;
            ds.Transform = tile;
            ds.FillGeometry(geo, Fade(col32, p));
            if (cell.Sw.Code == near.Code)
                _sel = (sel, tile, LabelInk(col32), p);
            ds.Transform = grow;

            if (labels) DrawCode(ds, cell.Sw.Code, rMid, mid,
                                 (rMid - rIn) * 2f, cell.A1 - cell.A0, col32, p);
            ds.Transform = Matrix3x2.Identity;
        }
    }

    // The marker code, oriented RADIALLY: the text is rotated by (θ − 90°)
    // about the label's own centre, which points the top of the glyphs at the
    // wheel's centre. Because the ring turns as a rigid body, every code then
    // holds the same orientation relative to the wheel through a spin — the
    // reason for the fixed offset rather than a cos-based flip, which reads
    // upright at rest but inverts discontinuously mid-rotation.
    // (Sign verified against the renderer: +90° would face the tops outward.)
    //
    // 11.14 item 4 moves it off the centre of the tile and into the tile's
    // UPPER-LEFT corner. In the rotated frame that rotation establishes, local
    // −Y is radially inward and local −X is the trailing side of the arc, so
    // the corner is (inner edge, trailing edge) - which is why the caller has
    // to hand over the cell's radial depth and angular span; neither is
    // derivable here, the tiers and the columns having different both.
    private void DrawCode(CanvasDrawingSession ds, string code, float r, float midA,
                          float depth, float span, Color bg, float a)
    {
        var p = At(r, midA);
        var keep = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(midA - MathF.PI / 2f, p) * keep;
        // The box only positions the (unwrapped) line, but it has to be at
        // least as tall as the line or Win2D clips the descenders.
        double boxH = Math.Max(16.0, _codeFmt.FontSize * 1.7);
        // Arc taken at the cell's INNER edge, where the label now sits: a cell
        // is a wedge, so its arc there is shorter than at the mid-radius the
        // old centred box measured from, and using the wider figure would hang
        // the first glyph outside the tile.
        float inner = r - depth * 0.5f;
        float halfArc = span * MathF.Max(inner, 1f) * 0.5f;
        const float Pad = 2.5f;
        // 11.20 item 1: "swatch names have too much upper margin inside their
        // cells - tighten it."
        //
        // Most of that margin was never a margin. A text layout's box starts at
        // the top of the LINE, and a line carries the font's ascent above the
        // cap height - about a quarter of the size for Segoe UI - so the gap
        // the user is looking at was Pad plus that leading, roughly three times
        // what the number in the source said. Subtracting the measured leading
        // makes the constant mean what it reads as, and the constant itself is
        // then a fraction of the type rather than a fixed 2.5 DIP, so it stays
        // proportional now that item 4 has taken the type down again.
        float padTop = (float)_codeFmt.FontSize * 0.20f;
        ds.DrawText(code,
            new Rect(p.X - halfArc + Pad, p.Y - depth * 0.5 + padTop - _codeInkTop,
                     Math.Max(4.0, halfArc * 2f - Pad * 2f), boxH),
            Fade(LabelInk(bg), a),
            _codeFmt);
        ds.Transform = keep;
    }

    /// <summary>How far below the top of a code label's line box its glyph ink
    /// actually starts, so <see cref="DrawCode"/> can take it back off and the
    /// designed margin is the one on screen (11.20 item 1).
    ///
    /// <para>Measured, not assumed: it depends on the font's own metrics at the
    /// current size. Once per size change, like the mode plates - a text layout
    /// per cell per frame is 600 layouts a frame on a 17-ring wheel.</para></summary>
    private void MeasureCodeInk(ICanvasResourceCreator rc)
    {
        if (Math.Abs(_codeInkMeasured - (float)_codeFmt.FontSize) <= 0.01f) return;
        _codeInkMeasured = (float)_codeFmt.FontSize;
        try
        {
            // Caps and digits only - which is every code in the palette, and
            // the one string whose ink top is the ink top of all of them.
            using var tl = new CanvasTextLayout(rc, "E0000", _codeFmt, 400f, 100f);
            _codeInkTop = (float)(tl.DrawBounds.Top - tl.LayoutBounds.Top);
        }
        catch { _codeInkTop = 0f; }
    }

    // HSL and RGB share one shape: three concentric arcs, each a tapered
    // ribbon of the colour it would produce, with a knob and a value bubble.
    private void DrawArcs(CanvasDrawingSession ds, float a)
    {
        if (a <= 0.002f) return;
        ds.Transform = TierTransform(a);
        for (int i = 0; i < 3; i++)
        {
            float r = _arcR[_chArc[i]];
            // The segment Layout gave this channel. The 0.18 rad clockwise roll
            // is already in it, and for the same reason it always was: _base
            // points at the middle of the window, so on a corner-docked dial an
            // arc centred on it puts its leading end - and the value box hung
            // outside that end - up under the top chrome bar.
            float a0 = _chA0[i], a1 = _chA1[i];
            // "One thick arc with round caps, a gradient along its length."
            //
            // Stamped as overlapping discs rather than stroked with a gradient
            // brush, and that is not an optimisation - it is the only way this
            // repaints. WinUI caches GradientStop mutations, so a brush whose
            // stops are rewritten as the colour changes paints correctly once
            // and then freezes; swapping in a new brush every frame for three
            // arcs is three allocations a frame for a control that is dragged.
            // A disc per step is immune to both, and the caps come out round
            // for free because the end steps ARE discs.
            //
            // Thickness is now CONSTANT. The old 2.6 -> 7.0 taper read as a
            // comet and made "slightly wider than the arc" undefinable, since
            // the arc was a different width at every point along it.
            //
            // The track goes down first, a hair proud of the gradient all the
            // way round. Without the scrim 11.19 removed, lightness ends in
            // white and saturation ends in grey ON WHITE PAPER, and half of
            // each arc was simply not there. This is the slider's track, not a
            // border on the arc - it is behind it, and it is what the arc is
            // drawn ON - so 11.16's "no frame" still holds.
            const int steps = 260;
            var track = Services.PageTheme.WithAlpha(Services.PageTheme.OnSurface, 46);
            for (int k = 0; k <= steps; k++)
            {
                float t = k / (float)steps;
                ds.FillCircle(At(r, a0 + (a1 - a0) * t), _arcW + 1.4f, Fade(track, a));
            }
            for (int k = 0; k <= steps; k++)
            {
                float t = k / (float)steps;
                ds.FillCircle(At(r, a0 + (a1 - a0) * t), _arcW, Fade(ChannelColor(i, t), a));
            }

            float v = ChannelValue(i);
            float va = a0 + (a1 - a0) * v;
            var knob = At(r, va);
            // The knob carries the CURRENT COLOUR, not the channel's colour at
            // this position - item 6 is explicit, and it is also what makes the
            // three knobs agree with each other and with the puck.
            ds.FillCircle(knob, _arcKnob, Fade(_color, a));
            ds.DrawCircle(knob, _arcKnob, Fade(Color.FromArgb(255, 255, 255, 255), a), 2.6f);
            ds.DrawCircle(knob, _arcKnob, Fade(Color.FromArgb(70, 0, 0, 0), a), 0.9f);

            // "A value box beside the knob, OUTSIDE the arc" - so radially out,
            // past the knob, at the knob's own bearing. Inside is where it used
            // to be, and inside is where the next arc in is.
            //
            // 11.20 item 9 makes the box a real text field, which is XAML and
            // cannot be scaled about the wheel centre frame by frame with the
            // rest of the surface. So the two take turns: the field is on
            // screen at rest, and this drawn box stands in for it through the
            // cascade, at the same place and the same size.
            if (!_fieldsLive) ValueBox(ds, FieldCentre(i), ChannelText(i), a);
        }
        ds.Transform = Matrix3x2.Identity;
    }

    // 11.15 item 6: "a white rounded rect with a hairline border and dark
    // text". Left white rather than themed on purpose - it is a readout badge
    // on the reference, the one piece of this control the doc gives an explicit
    // colour to, and it sits ON a saturated gradient arc rather than on the
    // page, so it takes its contrast from the arc and not from the paper.
    private float BoxW => 62f * _ui * Elem;
    private float BoxH => 30f * _ui * Elem;

    /// The centre of one channel's value box, in this control's coordinates:
    /// radially outside the knob, at the knob's own bearing.
    private Vector2 FieldCentre(int i)
    {
        float r = _arcR[_chArc[i]];
        float va = _chA0[i] + (_chA1[i] - _chA0[i]) * ChannelValue(i);
        return At(r + _arcW + 11f * _ui * Elem + BoxH * 0.5f, va);
    }

    private void ValueBox(CanvasDrawingSession ds, Vector2 p, string text, float a)
    {
        var rect = new Rect(p.X - BoxW * 0.5, p.Y - BoxH * 0.5, BoxW, BoxH);
        float rad = BoxH * 0.28f;
        ds.FillRoundedRectangle(rect, rad, rad, Fade(Color.FromArgb(245, 253, 253, 252), a));
        ds.DrawRoundedRectangle(rect, rad, rad, Fade(Color.FromArgb(70, 22, 23, 26), a), 1f);
        ds.DrawText(text, rect, Fade(Color.FromArgb(255, 24, 24, 26), a), _bubbleFmt);
    }

    private void DrawRecents(CanvasDrawingSession ds, float a)
    {
        foreach (var (p, col) in _chipPts)
        {
            ds.FillCircle(p, _chipR, Fade(col, a));
            ds.DrawCircle(p, _chipR, Fade(Services.PageTheme.WithAlpha(
                Services.PageTheme.OnSurface, 150), a), 1.2f);
        }
    }

    // Mode labels, the eyedropper and the current-colour puck: the picker's own
    // chrome, laid on a small arc around the anchor exactly as the reference
    // stacks COPIC / HSL / RGB beside the dial.
    private void DrawChrome(CanvasDrawingSession ds, float a)
    {
        // 10.8: one colour, one puck. The split puck existed to show what a
        // pick would be mixed INTO, and there is nothing to mix into here now.
        // 11.15 item 3 brings it down 20% with everything else.
        float puckR = 20f * _ui * Elem;
        ds.FillCircle(_puckPt, puckR, Fade(_color, a));
        ds.DrawCircle(_puckPt, puckR, Fade(Services.PageTheme.WithAlpha(
            Services.PageTheme.OnSurface, 215), a), 2.4f);

        string[] names = { "COPIC", "HSL", "RGB" };
        // 11.16 wants the chip snug on the word - "horizontal padding roughly
        // double the vertical" - which a fixed box cannot be for three words of
        // different lengths. Measured once per type size, not per frame.
        if (Math.Abs(_plateFontMeasured - (float)_labelFmt.FontSize) > 0.01f)
        {
            for (int i = 0; i < 3; i++)
            {
                using var tl = new CanvasTextLayout(ds, names[i], _labelFmt, 400f, 100f);
                var lb = tl.LayoutBounds;
                // 11.20 item 5: "the frame around the COPIC / RGB / HSL labels
                // shrinks 20%." The frame is the PADDING - the word is type and
                // item 4 has already taken it down - so the 11.16 ratio (about
                // double horizontally) is kept and both figures come off 20%.
                _plateSz[i] = new Vector2(
                    (float)lb.Width + (float)_labelFmt.FontSize * 1.76f * PlateFrame,
                    (float)lb.Height + (float)_labelFmt.FontSize * 0.92f * PlateFrame);
            }
            _plateFontMeasured = (float)_labelFmt.FontSize;
        }
        for (int i = 0; i < 3; i++)
        {
            var p = _labelPt[i];
            // The chip moves the instant the plate is tapped, while the old
            // face is still gravitating away: 11.20 item 6 sequences the
            // ANIMATION, and a tap that leaves the highlight behind for 300 ms
            // reads as a tap that missed.
            bool on = (int)(_switchTo ?? _mode) == i;
            // 11.16 reverses 11.12 item 1. Giving all three a plate was meant to
            // stop HSL and RGB reading as annotations, but the Concepts capture
            // is explicit that they ARE bare: "no box, no border, no ground",
            // muted grey, at the SAME type size, and it is the chip alone that
            // says which face is up. An outlined box on the two inactive faces
            // is a third state the control does not have.
            var sz = _plateSz[i];
            var rect = new Rect(p.X - sz.X * 0.5, p.Y - sz.Y * 0.5, sz.X, sz.Y);
            if (on)
            {
                // Themed rather than the capture's literal #ECEAE4: 11.19 took
                // the dark scrim this was authored against away, so a fixed
                // light chip would vanish into a light page. Surface is the
                // section 0 derivation of "a neutral plate over this paper",
                // and it keeps the capture's RELATION - filled ground, ink at
                // full contrast - on every page the theme can produce.
                ds.FillRoundedRectangle(rect, 6, 6, Fade(Services.PageTheme.WithAlpha(
                    Services.PageTheme.Surface, 242), a));
                ds.DrawText(names[i], rect, Fade(Services.PageTheme.OnSurface, a), _labelFmt);
            }
            else
            {
                ds.DrawText(names[i], rect, Fade(Services.PageTheme.OnSurfaceMuted, a), _labelFmt);
            }
        }

        // 11.12 item 2, superseding 10.4 item 15's shrink: at 25 it "reads as
        // a dark dot". 44, less 11.15 item 3's 20%. 11.3 item 18 and 11.16 both
        // say the same thing about what is behind it: nothing.
        DrawEyedropper(ds, _dropPt, 44f * _ui * Elem,
            Fade(_sampling ? Services.PageTheme.Accent : Services.PageTheme.OnSurface, a),
            a);
    }

    // Hand-authored eyedropper: a bulb on a 45° shaft that tapers to a point,
    // matching the flat single-weight silhouettes the rest of Quill's icons use.
    private void DrawEyedropper(CanvasDrawingSession ds, Vector2 c, float size, Color col, float a)
    {
        float k = size / 24f;
        Vector2 L(float x, float y) => c + new Vector2((x - 12f) * k, (y - 12f) * k);

        // 11.3 item 18: no border, no frame. The dark plate was what made this
        // read as a dark dot rather than as a tool.

        // The shaft. It was 2.4 units across, which at any size reads as a
        // scratch rather than as a pipette - the whole mark was a hairline with
        // a lozenge on the end. 4.8 units, and the tip is a real point.
        using (var b = new CanvasPathBuilder(ds))
        {
            b.BeginFigure(L(1.8f, 22.2f));          // the drip tip
            b.AddLine(L(3.6f, 15.8f));
            b.AddLine(L(13.4f, 6.0f));
            b.AddLine(L(18.0f, 10.6f));
            b.AddLine(L(8.2f, 20.4f));
            b.EndFigure(CanvasFigureLoop.Closed);
            using var geo = CanvasGeometry.CreatePath(b);
            // 11.16: "no frame, no border, no background plate, no chip. Just
            // the mark." The hairline that used to run round this silhouette
            // was there to lift a pale pipette off the scrim; 11.19 removed the
            // scrim, and the fill is now PageTheme.OnSurface, which is the ink
            // the page itself contrasts with. So the outline is not merely
            // unnecessary, it is the border the doc says is not there.
            ds.FillGeometry(geo, col);
        }

        // The bulb, squared off across the shaft rather than a thin lozenge.
        var bulbC = L(18.4f, 5.6f);
        var bulb = new Rect(bulbC.X - 6.4 * k, bulbC.Y - 4.6 * k, 12.8 * k, 9.2 * k);
        var keep = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(-MathF.PI / 4f, bulbC) * keep;
        ds.FillRoundedRectangle(bulb, 3.6f * k, 3.6f * k, col);
        ds.Transform = keep;
    }

    // =======================================================================
    // Channel plumbing for the HSL / RGB faces
    // =======================================================================
    private float ChannelValue(int i) => _mode == ColorWheelMode.Hsl
        ? i switch { 0 => (float)(_h / 360.0), 1 => (float)_s, _ => (float)_l }
        : i switch { 0 => _color.R / 255f, 1 => _color.G / 255f, _ => _color.B / 255f };

    private Color ChannelColor(int i, float t) => _mode == ColorWheelMode.Hsl
        ? i switch
        {
            0 => FromHsl(t * 360.0, 1.0, 0.5),
            1 => FromHsl(_h, t, _l <= 0.02 || _l >= 0.98 ? 0.5 : _l),
            _ => FromHsl(_h, _s, t)
        }
        : i switch
        {
            0 => Color.FromArgb(255, (byte)(t * 255), 0, 0),
            1 => Color.FromArgb(255, 0, (byte)(t * 255), 0),
            _ => Color.FromArgb(255, 0, 0, (byte)(t * 255))
        };

    private string ChannelText(int i) => _mode == ColorWheelMode.Hsl
        ? i switch { 0 => $"{_h:0}°", 1 => $"{_s * 100:0}%", _ => $"{_l * 100:0}%" }
        : i switch { 0 => _color.R.ToString(), 1 => _color.G.ToString(), _ => _color.B.ToString() };

    private void SetChannel(int i, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (_mode == ColorWheelMode.Hsl)
        {
            if (i == 0) _h = t * 360.0; else if (i == 1) _s = t; else _l = t;
            _color = FromHsl(_h, _s, _l);
        }
        else
        {
            byte v = (byte)Math.Round(t * 255);
            _color = i switch
            {
                0 => Color.FromArgb(255, v, _color.G, _color.B),
                1 => Color.FromArgb(255, _color.R, v, _color.B),
                _ => Color.FromArgb(255, _color.R, _color.G, v)
            };
            (_h, _s, _l) = ToHsl(_color);
        }
        ColorChanged?.Invoke(_color);
        _canvas.Invalidate();
    }

    // =======================================================================
    // Input
    // =======================================================================
    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        // An exit is already committed, so a press during it is dropped. An
        // ENTRANCE is not: making an impatient user wait out 280ms of cascade
        // is bad enough, but the old "park all input until the phase is Idle"
        // was unbounded — if the clock never reached the end (see OnAnimTick),
        // _phase stayed Entering and the picker ate EVERY press for the rest of
        // its life, which reads exactly as "buttons not clickable". A press now
        // lands the entrance on its end state first, so the frame the press is
        // hit-tested against is the one on screen from that instant.
        if (_phase == Phase.Entering) { LandTransition(); _canvas.Invalidate(); }
        if (_phase != Phase.Idle) { e.Handled = true; return; }

        var pt = e.GetCurrentPoint((UIElement)sender).Position;
        var p = new Vector2((float)pt.X, (float)pt.Y);
        e.Handled = true;
        // Guarded for the same reason as ToolWheel.OnPressed: CapturePointer
        // throws ArgumentException on a pointer that is already captured or no
        // longer in contact, and an unguarded throw here loses the press.
        try { _input?.CapturePointer(e.Pointer); }
        catch (ArgumentException) { }

        if (_sampling)
        {
            _sampling = false;
            SampleRequested?.Invoke(pt);
            _canvas.Invalidate();
            return;
        }

        // chrome first: it sits over the empty middle of the ring
        if (Vector2.Distance(p, _dropPt) < 30f * _ui)
        {
            _sampling = true;
            _canvas.Invalidate();
            return;
        }
        for (int i = 0; i < 3; i++)
        {
            // The target is the chip the draw pass measured, plus a little, so
            // it shrinks with the plate exactly as the plate shrinks - at the
            // 0.52 rad arc step, a target left at the old 48 x 23 would reach
            // into its neighbour and steal the tap. Floored so that a press
            // arriving before the first draw pass still lands.
            float hx = MathF.Max(_plateSz[i].X, 44f) * 0.5f + 5f;
            float hy = MathF.Max(_plateSz[i].Y, 26f) * 0.5f + 5f;
            if (Math.Abs(p.X - _labelPt[i].X) < hx && Math.Abs(p.Y - _labelPt[i].Y) < hy)
            {
                Mode = (ColorWheelMode)i;
                return;
            }
        }
        for (int i = 0; i < _chipPts.Count; i++)
        {
            if (Vector2.Distance(p, _chipPts[i].Pt) < _chipR + 3f)
            {
                Commit(_chipPts[i].Col);
                return;
            }
        }

        float r = Vector2.Distance(p, _c);
        float a = MathF.Atan2(p.Y - _c.Y, p.X - _c.X);

        if (_mode == ColorWheelMode.Copic)
        {
            if (r >= _rIn && r <= _rOut)
            {
                _spin.Stop();
                _vel = 0;
                _dragRing = true;
                _dragPointer = e.Pointer.PointerId;
                _lastAngle = a;
                _pressPt = _lastPt = p;
                _pathPx = 0f;
                _lastMoveTs = Stopwatch.GetTimestamp();
                return;
            }
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                // Each channel owns a radius AND an angular segment, and after
                // 11.20 item 7 two channels can share the radius - so the
                // angular gate is what separates them and it is the channel's
                // own segment, not a span shared across the face. The radial
                // gate is the arc's thickness plus a thumb's worth.
                if (Math.Abs(r - _arcR[_chArc[i]]) >= _arcW + 13f) continue;
                float t = SegT(i, a);
                if (t < -0.05f || t > 1.05f) continue;
                _dragArc = i;
                SetChannel(i, t);
                return;
            }
        }

        _input?.ReleasePointerCapture(e.Pointer);
        Dismissed?.Invoke();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragRing && _dragArc < 0) return;
        // The overlay is full-screen by design (it has to catch the tap-away
        // that dismisses the picker), so a pointer move ANYWHERE lands here.
        // Two gates make that harmless: the pointer must still be in contact —
        // a stale drag left armed by a lost release would otherwise let a
        // button-up mouse spin the ring from across the display — and it must
        // be the same pointer that started the drag.
        if (!e.Pointer.IsInContact) { EndDrag(); return; }
        if (_dragRing && e.Pointer.PointerId != _dragPointer) return;
        var pt = e.GetCurrentPoint((UIElement)sender).Position;
        var p = new Vector2((float)pt.X, (float)pt.Y);
        float a = MathF.Atan2(p.Y - _c.Y, p.X - _c.X);
        e.Handled = true;

        if (_dragArc >= 0)
        {
            SetChannel(_dragArc, SegT(_dragArc, a));
            return;
        }

        float d = Norm(a - _lastAngle);
        _lastAngle = a;
        // Kept in (-π, π]. The rotation is modular for every tier, so folding it
        // costs nothing visually and stops a long session of spinning from
        // pushing _rot somewhere float cannot resolve a 10° column any more —
        // which would put the drawn ring and the arithmetic hit-test out of step
        // by a fraction of a swatch and make edge taps pick the neighbour.
        _rot = Norm(_rot + d);
        _pathPx += Vector2.Distance(p, _lastPt);
        _lastPt = p;
        // Velocity off the CLOCK rather than an assumed 60 moves a second: a pen
        // reports at 120-240 Hz, so a per-event constant reads a flick as a
        // quarter of its real speed and the ring barely coasts. The smoothing is
        // the same as before — it keeps a jittery pen from throwing the ring
        // across the screen on release.
        long now = Stopwatch.GetTimestamp();
        float dt = (float)((now - _lastMoveTs) / (double)Stopwatch.Frequency);
        _lastMoveTs = now;
        if (dt > 1e-4f && dt < 0.12f) _vel = _vel * 0.55f + (d / dt) * 0.45f;
        _canvas.Invalidate();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint((UIElement)sender).Position;
        var p = new Vector2((float)pt.X, (float)pt.Y);
        bool wasRing = _dragRing;
        float path = _pathPx;
        EndDrag();
        _input?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (!wasRing) return;

        if (path <= TapSlopPx && Vector2.Distance(p, _pressPt) <= TapSlopPx)
        {
            PickAt(p);
            return;
        }
        // A flick glides; a slow release just stops. Neither settles onto a
        // column boundary any more (§9.4.2).
        if (Math.Abs(_vel) > StopVel) { _spinTs = Stopwatch.GetTimestamp(); _spin.Start(); }
        else { _vel = 0; _spin.Stop(); }
    }

    private void EndDrag()
    {
        _dragRing = false;
        _dragArc = -1;
    }

    /// <summary>10.4 item 17: "the wheel must spin with the mouse wheel and with
    /// horizontal / side scroll."</summary>
    private void OnWheelScroll(object sender, PointerRoutedEventArgs e)
    {
        if (_phase == Phase.Entering) { LandTransition(); }
        if (_phase != Phase.Idle) { e.Handled = true; return; }
        var props = e.GetCurrentPoint((UIElement)sender).Properties;
        int delta = props.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        // One notch is 120. A tenth of a radian per notch turns the ring by
        // about half a 10 degree column, so two notches step one family column
        // and a spin still feels continuous rather than ratcheted. 9.4.2
        // removed snapping entirely, so nothing here rounds.
        float step = delta / 120f * 0.10f;
        // A horizontal notch is a side-scroll; it turns the same way a
        // left-to-right drag across the top of the ring does, which is the
        // opposite sign from a vertical notch.
        if (props.IsHorizontalMouseWheel) step = -step;
        _rot = Norm(_rot + step);
        // Feed the inertia rather than replacing it, so several fast notches
        // build a glide instead of each one landing dead.
        _vel = Math.Clamp(_vel * 0.6f + step * 26f, -14f, 14f);
        if (Math.Abs(_vel) > StopVel) { _spinTs = Stopwatch.GetTimestamp(); _spin.Start(); }
        // 11.21 item 4 turns rotation from a convenience into the ACCESS
        // MECHANISM: at 17 rings the outer columns run off the window, and a
        // swatch that cannot be spun into view is a swatch that is lost. That
        // makes "the wheel spins with the mouse wheel" an acceptance test
        // rather than a feature, and it has been asked for three times
        // (10.4 item 17, 11.3 item 24, 11.21 item 4) without ever being
        // confirmed on screen - because there is nothing outside the process
        // that can see a float in a Win2D draw pass. It says so here, in the
        // same channel WHEEL-CENTRE uses, so the confirmation is a measurement
        // and not an opinion about a screenshot.
        GeometryProbe.Write("WHEEL-ROT",
            $"rot={_rot:F4} deg={_rot / Deg:F2} step={step:F4} " +
            $"horiz={(props.IsHorizontalMouseWheel ? 1 : 0)} vel={_vel:F2}");
        _canvas.Invalidate();
    }

    private void PickAt(Vector2 p)
    {
        float r = Vector2.Distance(p, _c);
        float a = MathF.Atan2(p.Y - _c.Y, p.X - _c.X) - _rot;
        if (SwatchAt(r, a) is not { } sw) return;
        Commit(Color.FromArgb(255, sw.R, sw.G, sw.B));
    }

    /// <summary>Chooses a colour, publishes it and asks the host to close
    /// (V3 K.11 - "the COPIC wheel closes automatically once a colour is
    /// chosen").
    ///
    /// <para>K.11 used to carry an exception, because K.12's mix row made
    /// picking iterative and closing on the first pick would have made mixing
    /// single-shot. 10.8 moved mixing out to its own tool, so the exception went
    /// with it and this is now unconditional - which is what K.11 asked for in
    /// the first place.</para></summary>
    private void Commit(Color picked)
    {
        Color = picked;
        BaseColor = _color;
        ColorChanged?.Invoke(_color);
        Picked?.Invoke();
    }

    // Pure-arithmetic hit-test: pick the tier by radius, then the cell by angle.
    // `a` is the pointer angle already de-rotated into the ring's own frame.
    //
    // EVERY branch is bounded on BOTH sides. The angle alone must never be
    // allowed to resolve a chip: the overlay covers the whole window, so
    // without a ceiling a point way outside the ring would still divide down to
    // a column and answer with whatever ink happened to sit at that bearing.
    private CopicSwatch? SwatchAt(float r, float a)
    {
        if (r < _rIn || r > _rOut) return null;                        // outside the ring entirely
        if (r <= _r1Out) return r >= _r1In ? CellHit(Tier1Cells, a) : null;
        if (r <= _r2Out) return r >= _r2In ? CellHit(Tier2Cells, a) : null;
        if (r < _rOutBase) return null;                                 // the gap before the columns

        int ring = (int)MathF.Floor((r - _rOutBase) / _band);
        // _rings, not MaxRings: r > _rOut was already refused above, but the
        // painted set is what the pointer is aiming at and the two have to
        // describe the same shape (9.4.1's rule, applied to depth).
        if (ring < 0 || ring >= _rings) return null;
        // Fold the angle into [0, 360) measured from the -90° start, then
        // one division lands the 10° column.
        double deg = a / Deg + 90.0;
        deg = ((deg % 360) + 360) % 360;
        int col = (int)Math.Floor(deg / 10.0) % OuterColumns.Length;
        // 11.17's gap is background, and background is not a swatch: a tap that
        // lands between two families answers null rather than handing over the
        // ink whose column it merely points at.
        if (ColEndsFamily[col] && (deg - col * 10.0) * Deg > ColStep - FamGap) return null;
        var stack = OuterColumns[col];
        return ring < stack.Length ? stack[ring] : null;
    }

    private static CopicSwatch? CellHit(Cell[] cells, float a)
    {
        foreach (var cell in cells)
        {
            // Norm gives the shortest signed offset from the cell start; inside
            // the cell it lands in [0, width]. Works across the ±π seam too.
            float d = Norm(a - cell.A0);
            if (d >= 0 && d <= cell.A1 - cell.A0) return cell.Sw;
        }
        return null;
    }

    // The glide: exponential falloff, and nothing after it (§9.4.2).
    private void OnSpinTick(object? sender, object e)
    {
        // A DispatcherTimer is not a frame clock — it drops ticks under load —
        // so the glide integrates real elapsed time and both rates are raised to
        // dt*60. At exactly 60 Hz that is the old per-frame constant, so the
        // feel is unchanged; off 60 Hz the ring now decays in the same wall-clock
        // time instead of coasting further on a busy machine.
        long now = Stopwatch.GetTimestamp();
        float dt = Math.Clamp((float)((now - _spinTs) / (double)Stopwatch.Frequency), 0.001f, 0.05f);
        _spinTs = now;

        _rot = Norm(_rot + _vel * dt);
        _vel *= MathF.Pow(Decay, dt * 60f);
        if (Math.Abs(_vel) < StopVel) { _vel = 0; _spin.Stop(); }
        _canvas.Invalidate();
    }

    /// The host feeds an eyedropper result back in. Null (nothing under the
    /// pointer) simply leaves the colour alone.
    public void ApplySample(Color? c)
    {
        if (c == null) return;
        Color = c.Value;
        ColorChanged?.Invoke(_color);
    }

    // =======================================================================
    // Entrance / exit — the reference's radial gravity drop
    // =======================================================================
    // The reference plays one CSS keyframe per SVG group:
    //
    //   radialGravityDrop  160ms cubic-bezier(0.16, 1, 0.3, 1)  fill both
    //       opacity 0 -> 1, transform scale3d(0.5,0.5,1) -> scale3d(1,1,1)
    //   radialGravityExit  120ms cubic-bezier(0.4, 0, 1, 1)     fill forwards
    //       opacity 1 -> 0, transform scale 1 -> 0.5
    //
    // …with transform-origin at the wheel centre and a per-group
    // animation-delay that makes the ring assemble in tiers: the inner chips
    // first, the grey ring 20ms behind, then the 36 family columns rippling in
    // over the next 90ms. 280ms end to end.
    //
    // Win2D is immediate mode, so there is nothing for a Storyboard to hang
    // off and no way for one to stagger tiers that are not elements. Instead
    // ONE 16ms clock runs while a transition is in flight and every tier
    // derives its own progress from the wall-clock elapsed time inside the
    // draw pass — same delays, same curve, one timer, no allocation. The
    // clock stops itself on the frame the last tier lands, so an idle picker
    // does no work at all.
    //
    // A tier's progress p (1 = fully present) drives both halves of the
    // keyframe exactly as CSS does — one eased value feeding opacity and
    // transform together: alpha = p, scale = lerp(0.5, 1, p) about the centre.

    private const float EnterMs = 160f, ExitMs = 120f;
    private const int Tier1OpenDelay = 0, Tier1CloseDelay = 60;    // inner chips
    private const int Tier2OpenDelay = 20, Tier2CloseDelay = 40;   // grey ring
    private static readonly int[] ColOpenDelay;                    // per family column
    private static readonly int[] ColCloseDelay;
    private static readonly float EnterSpan, ExitSpan;             // last tier lands

    // Exactly the solver a browser runs for cubic-bezier(x1,y1,x2,y2): WebKit's
    // UnitBezier — Newton-Raphson on x, bisection when the derivative stalls.
    // Not a KeySpline lookalike or an exponential stand-in; the same curve.
    private readonly struct UnitBezier
    {
        private readonly float _ax, _bx, _cx, _ay, _by, _cy;

        public UnitBezier(float x1, float y1, float x2, float y2)
        {
            _cx = 3f * x1; _bx = 3f * (x2 - x1) - _cx; _ax = 1f - _cx - _bx;
            _cy = 3f * y1; _by = 3f * (y2 - y1) - _cy; _ay = 1f - _cy - _by;
        }

        private float X(float t) => ((_ax * t + _bx) * t + _cx) * t;
        private float Y(float t) => ((_ay * t + _by) * t + _cy) * t;
        private float Dx(float t) => (3f * _ax * t + 2f * _bx) * t + _cx;

        public float Solve(float x)
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;

            float t = x;
            for (int i = 0; i < 8; i++)
            {
                float err = X(t) - x;
                if (MathF.Abs(err) < 1e-5f) return Y(t);
                float d = Dx(t);
                if (MathF.Abs(d) < 1e-6f) break;
                t -= err / d;
            }

            float lo = 0f, hi = 1f;
            t = x;
            while (hi - lo > 1e-6f)
            {
                float xa = X(t);
                if (MathF.Abs(xa - x) < 1e-5f) break;
                if (x > xa) lo = t; else hi = t;
                t = lo + (hi - lo) * 0.5f;
            }
            return Y(t);
        }
    }

    private static readonly UnitBezier EnterEase = new(0.16f, 1f, 0.3f, 1f);
    private static readonly UnitBezier ExitEase = new(0.4f, 0f, 1f, 1f);

    private enum Phase { Idle, Entering, Exiting }
    private Phase _phase = Phase.Idle;
    private long _animT0;
    private long _phaseT0;              // wall clock at BeginEnter / BeginExit
    private bool _clockPending;
    private Action? _exitDone;
    private Action? _landed;            // handed out by LandTransition
    // How far past its own span a transition may run before it is declared over
    // whatever the draw clock says. Generous enough never to clip a real
    // animation, short enough that a wedged one is not noticed.
    private const float PhaseGuardMs = 600f;

    // Windows' "Animation effects" switch. Read here rather than plumbed in
    // from MainWindow so the picker keeps its zero footprint there; the
    // UISettings instance is cached but the property is read live, so turning
    // the setting off takes effect on the next open.
    private static Windows.UI.ViewManagement.UISettings? _uiSettings;
    private static bool ReduceMotion
    {
        get
        {
            try
            {
                _uiSettings ??= new Windows.UI.ViewManagement.UISettings();
                return !_uiSettings.AnimationsEnabled;
            }
            catch { return false; }   // no UISettings (unpackaged edge cases): animate
        }
    }

    private float ElapsedMs() => _clockPending
        ? 0f
        : (float)((Stopwatch.GetTimestamp() - _animT0) * 1000.0 / Stopwatch.Frequency);

    /// A tier's presence: 1 fully here, 0 fully gone. Alpha is this value and
    /// the tier is scaled about the wheel centre by lerp(0.5, 1, value).
    private float TierProgress(int openDelay, int closeDelay)
    {
        if (_phase == Phase.Idle) return 1f;
        float t = ElapsedMs();
        return _phase == Phase.Entering
            ? EnterEase.Solve(Math.Clamp((t - openDelay) / EnterMs, 0f, 1f))
            : 1f - ExitEase.Solve(Math.Clamp((t - closeDelay) / ExitMs, 0f, 1f));
    }

    /// scale3d(p', p', 1) about the wheel centre, p' = lerp(0.5, 1, p).
    private Matrix3x2 TierTransform(float p) => p >= 0.999f
        ? Matrix3x2.Identity
        : Matrix3x2.CreateScale(0.5f + 0.5f * p, _c);

    /// Plays the entrance. Call once the picker has been made visible.
    public void BeginEnter()
    {
        _exitDone = null;
        // The per-session mix ratio that used to be disarmed here went with the
        // mix row itself (10.8). Nothing about a pick is stateful across
        // sessions now, which is what the reset was defending against.
        if (ReduceMotion)
        {
            LandTransition();         // straight to the end state
            _landed = null;
            _canvas.Invalidate();
            return;
        }
        _phase = Phase.Entering;
        _clockPending = true;
        _phaseT0 = Stopwatch.GetTimestamp();
        if (!_anim.IsEnabled) _anim.Start();
        _canvas.Invalidate();
    }

    /// Plays the exit and calls back once it has finished. The host must not
    /// hide the picker until then — that is the whole point of the callback.
    public void BeginExit(Action onComplete)
    {
        if (_exitDone != null) return;               // already leaving, for real
        // A face switch is also an Exiting phase, and a close arriving during
        // one must not be swallowed by it - the host does not hide the picker
        // until the callback fires, so a dropped close leaves the overlay up
        // for good. The switch is abandoned and the exit clock restarts.
        _switchTo = null;
        if (ReduceMotion)
        {
            CancelAnimation();
            onComplete();
            return;
        }
        _exitDone = onComplete;
        _phase = Phase.Exiting;
        _clockPending = true;
        _phaseT0 = Stopwatch.GetTimestamp();
        if (!_anim.IsEnabled) _anim.Start();
        _canvas.Invalidate();
    }

    /// Drops a transition in flight WITHOUT firing its completion callback —
    /// for when the picker is reopened mid-close.
    public void CancelAnimation()
    {
        var swap = _switchTo;
        LandTransition();
        _landed = null;      // dropped on the floor: that is the whole point
        // A face switch caught mid-flight still has to ARRIVE: the user asked
        // for that face and the host has already been told about it.
        if (swap is { } face) ApplyMode(face);
    }

    private void OnAnimTick(object? sender, object e)
    {
        float span = _phase == Phase.Entering ? EnterSpan : ExitSpan;
        // The cascade's own clock is started by the first DRAW, not by the call
        // that begins it, so that a cold CanvasControl does not eat the head of
        // the animation. That is fine while frames are arriving and a trap when
        // they are not — minimised, occluded, or simply never invalidated, the
        // clock reads 0 forever, the phase never lands, and with it the picker
        // stops accepting input at all. The timer itself keeps ticking in all
        // those cases, so a wall-clock deadline measured from BeginEnter /
        // BeginExit is the backstop: past it the phase is over regardless.
        float wall = (float)((Stopwatch.GetTimestamp() - _phaseT0) * 1000.0 / Stopwatch.Frequency);
        if (_phase != Phase.Idle && ElapsedMs() < span && wall < span + PhaseGuardMs)
        {
            _canvas.Invalidate();
            return;
        }

        bool wasExit = _phase == Phase.Exiting;
        var swap = _switchTo;
        LandTransition();
        if (swap is { } face)
        {
            // 11.20 item 6. The outgoing face has finished gravitating inward,
            // so the incoming one may start. LandTransition put _exitDone into
            // _landed, and for a face switch that is null - a switch is not a
            // close and must not tell the host to hide the picker.
            GeometryProbe.Write("FACE-SWITCH",
                $"stage=close-landed-open-begin from={_mode} to={face} " +
                $"afterMs={(Stopwatch.GetTimestamp() - _phaseT0) * 1000.0 / Stopwatch.Frequency:F1}");
            ApplyMode(face);
            BeginEnter();
            return;
        }
        if (wasExit) { _landed?.Invoke(); _landed = null; return; }   // host hides us; no repaint
        _canvas.Invalidate();                                         // land on the exact end state
    }

    /// Ends whatever transition is in flight RIGHT NOW: clock off, phase Idle,
    /// every tier at its end state. The exit's completion callback is handed
    /// back through <see cref="_landed"/> rather than invoked here, so the one
    /// caller that must run it (OnAnimTick) does, and the one that must not
    /// (a press landing the entrance) cannot.
    private void LandTransition()
    {
        _anim.Stop();
        _phase = Phase.Idle;
        _clockPending = false;
        _landed = _exitDone;
        _exitDone = null;
    }

    // =======================================================================
    // Colour maths
    // =======================================================================
    private static bool IsDark(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128;

    // 11.16: "colour flips for contrast ... derived from the swatch's
    // luminance, not from a hand-maintained list". A list would be wrong the
    // day the missing Copic codes land, so this picks whichever of the two inks
    // has the better WCAG contrast ratio against the swatch, using PageTheme's
    // own gamma-correct luminance rather than a second definition of the word.
    private static readonly Color LabelLight = Color.FromArgb(240, 255, 255, 255);
    private static readonly Color LabelDark = Color.FromArgb(236, 22, 22, 22);
    private static readonly double LabelLightY = Services.PageTheme.Luminance(LabelLight);
    private static readonly double LabelDarkY = Services.PageTheme.Luminance(LabelDark);

    private static Color LabelInk(Color bg)
    {
        double y = Services.PageTheme.Luminance(bg);
        return Ratio(y, LabelLightY) >= Ratio(y, LabelDarkY) ? LabelLight : LabelDark;
    }

    private static double Ratio(double a, double b) =>
        (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    /// Multiplies a colour's own alpha by the tier's opacity. Cells are
    /// disjoint, so per-colour alpha gives the same result as a layer at a
    /// fraction of the cost of ~40 CreateLayer scopes a frame.
    private static Color Fade(Color c, float a) => a >= 0.999f
        ? c
        : Color.FromArgb((byte)(c.A * Math.Clamp(a, 0f, 1f)), c.R, c.G, c.B);

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        double d = max - min;
        if (d > 1e-6)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromArgb(255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
