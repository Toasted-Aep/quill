using System.Diagnostics;
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
    // How much annulus the hub's own chrome needs between that caller and Tier 1
    // - the recents row, the mix row and the three mode plates. Below this the
    // wheel's chrome and the dial start sharing pixels, which is the "cramped"
    // the user reported.
    private const float HubRoom = 104f;
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

    private readonly CanvasTextFormat _codeFmt = new()
    {
        FontSize = 11,
        FontFamily = "Segoe UI",
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };
    private readonly CanvasTextFormat _labelFmt = new()
    {
        FontSize = 15,
        FontFamily = "Segoe UI",
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

    /// <summary>The colour a mix starts FROM - what the caller was using when
    /// the picker opened. Only read while a mix ratio is armed.</summary>
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

    public ColorWheelMode Mode
    {
        get => _mode;
        set { if (_mode == value) return; _mode = value; _canvas.Invalidate(); ModeChanged?.Invoke(value); }
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
    private float _rLabel, _rRecent, _chipR, _rMix;
    // Chrome scale. The hub shrinks with the window (the ring is fitted to the
    // viewport), and the mode plates / eyedropper / bubbles are authored at a
    // fixed pixel size, so without this they collide with Tier 1 on a small
    // window. 1.0 is the size they were drawn for.
    private float _ui = 1f;
    private float _base;                // direction from the anchor to the
                                        // viewport centre — everything that has
                                        // to stay on screen hangs off this
    private readonly float[] _arcR = new float[3];
    private const float ArcHalf = 0.72f;   // half-span of an HSL/RGB arc, radians
    private readonly Vector2[] _labelPt = new Vector2[3];
    private Vector2 _dropPt, _puckPt;
    private readonly List<(Vector2 Pt, Color Col)> _chipPts = new();
    // The mix row: OFF / 25% / 50% / 75% (V3 K.12). 0 is off.
    private readonly Vector2[] _mixPt = new Vector2[4];
    private Vector2 _mixCaption;
    private int _mixIndex;
    private static readonly string[] MixNames = { "OFF", "25%", "50%", "75%" };
    private double MixRatio => _mixIndex switch { 1 => 0.25, 2 => 0.5, 3 => 0.75, _ => 0 };

    // Cached tile geometry: one 10° cell per outer ring band (reused across all
    // 36 columns by rotation), plus one cell each for the two inner tiers.
    private readonly CanvasGeometry?[] _outerGeo = new CanvasGeometry?[MaxRings];
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

    private void DisposeGeometry()
    {
        for (int i = 0; i < _outerGeo.Length; i++) { _outerGeo[i]?.Dispose(); _outerGeo[i] = null; }
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

        _r1In = 285f * s; _r1Out = 302f * s;   // Tier 1 inner arc  (17 units)
        _r2In = 307f * s; _r2Out = 328f * s;   // Tier 2 grey ring  (21 units)
        _rOutBase = 333f * s; _band = 21f * s; // Tier 3+ columns   (21 units each)
        _rIn = _r1In;
        _rOut = _rOutBase + MaxRings * _band;
        // The reference sets its SVG code text to 7 units in a 21-unit band, a
        // third of the band. Segoe UI through Win2D at that size is a smear and
        // the band has room for far more: half the band still leaves ~3.5 DIP of
        // padding above and below the line box, and the narrowest cell in the
        // whole wheel (Tier 2, ~7.3° at r≈317, so ~40 DIP of arc) still swallows
        // the longest grey code at that size.
        _codeFmt.FontSize = Math.Clamp(_band * 0.5f, 7f, 14f);

        // The chrome lives in the hub, so it is sized off the HOLE rather than
        // the window: it can never collide with Tier 1 whatever the shape.
        float hole = _r1In;
        // 9.3: when a caller SITS in the hole - the dial does - the hub's usable
        // room is the ANNULUS between it and Tier 1, not the whole disc. The
        // fractions are unchanged; they are simply taken across that annulus, so
        // with no clearance this is byte-identical to what it was.
        float lo = Math.Min(HubClearance, hole * 0.55f);
        float band = hole - lo;
        _ui = Math.Clamp(band / 180f, 0.60f, 1.20f);
        _labelFmt.FontSize = 15f * _ui;
        _bubbleFmt.FontSize = 13f * _ui;
        _rRecent = lo + band * 0.30f;
        _chipR = Math.Clamp(band * 0.045f, 5f, 12f);
        _rMix = lo + band * 0.54f;
        _rLabel = lo + band * 0.74f;
        // 9.4.3: the HSL and RGB faces used to be fitted to the VIEWPORT
        // (0.42 / 0.58 / 0.74 of half the window), which put them far inside the
        // COPIC face - switching mode collapsed the control toward the middle
        // and the three arcs read as a different, smaller instrument. They now
        // take the COPIC face's own bands: Tier 1, Tier 2 and the first column
        // ring. One control, one radius, whichever face is up.
        _arcR[0] = (_r1In + _r1Out) * 0.5f;
        _arcR[1] = (_r2In + _r2Out) * 0.5f;
        _arcR[2] = _rOutBase + _band * 0.5f;

        // Which way the hub's chrome faces. Centred in the viewport that is the
        // direction of the thing that opened the picker; centred ON that thing
        // there is no such direction, so the chrome leans toward the middle of
        // the window, which is the only direction guaranteed to be on screen.
        var lean = CenterOnAnchor ? mid - _c : _hint - _c;
        _base = lean.LengthSquared() < 4f ? 0f : MathF.Atan2(lean.Y, lean.X);

        _puckPt = At(_rLabel, _base - 0.75f);
        _labelPt[0] = At(_rLabel, _base - 0.42f);
        _labelPt[1] = At(_rLabel, _base - 0.09f);
        _labelPt[2] = At(_rLabel, _base + 0.24f);
        _dropPt = At(_rLabel, _base + 0.57f);

        // The mix row is a SECOND, TIGHTER ARC INSIDE the mode plates, on the
        // same side of the hub. It was briefly on the opposite side, which reads
        // well when the ring is centred in the window and is useless when the
        // ring is centred on a corner-docked dial (K.2) - the far side of the
        // hub is then off screen entirely, taking the whole control with it.
        // Everything the user has to reach now hangs off the one direction that
        // is guaranteed to be visible.
        for (int i = 0; i < 4; i++) _mixPt[i] = At(_rMix, _base - 0.42f + i * 0.28f);
        _mixCaption = At(_rMix, _base - 0.78f);

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
    private CanvasGeometry OuterCell(ICanvasResourceCreator rc, int ring)
    {
        if (_outerGeo[ring] is { } cached) return cached;
        float r0 = _rOutBase + ring * _band;
        // Welded outward and clockwise: rings are drawn inner-to-outer and
        // columns in increasing angle, so the overlap is always covered by the
        // neighbour drawn next.
        var geo = ArcTile(rc, r0, r0 + _band + Weld, 0f, ColStep + Weld / MathF.Max(r0, 1f));
        _outerGeo[ring] = geo;
        return geo;
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

    private CanvasGeometry OuterSel(ICanvasResourceCreator rc, int ring)
    {
        if (_outerSelGeo[ring] is { } cached) return cached;
        float r0 = _rOutBase + ring * _band;
        float da = SelInset / MathF.Max(r0, 1f);
        return _outerSelGeo[ring] =
            ArcTile(rc, r0 + SelInset, r0 + _band - SelInset, da, ColStep - da);
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

        // The reference presents this face as a MODAL — ColorPickerModal wraps
        // the very same wheel in a dimmed backdrop and centres it — and that
        // backdrop is most of what makes it read as its own surface rather than
        // something growing out of whatever was tapped. It is drawn here, inside
        // the same Win2D pass, so it fades in on Tier 1's clock with everything
        // else. It lifts while the eyedropper is armed: that tool has to see the
        // page it is about to sample.
        if (!_sampling && p1 > 0.002f)
            ds.FillRectangle(0, 0, w, h, Fade(Color.FromArgb(132, 8, 9, 12), p1));

        if (_mode == ColorWheelMode.Copic) DrawRing(sender, ds, w, h);
        else DrawArcs(ds, p1);

        if (p1 > 0.002f)
        {
            ds.Transform = TierTransform(p1);
            DrawRecents(ds, p1);
            DrawChrome(ds, p1);
            ds.Transform = Matrix3x2.Identity;
        }
    }

    private void DrawRing(ICanvasResourceCreator rc, CanvasDrawingSession ds, float w, float h)
    {
        // How far the viewport reaches from the ring's centre, per side, so the
        // cull is exact whether the ring is centred in the window or on the
        // corner-docked dial (K.2).
        var view = new View(_c, w, h);
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
            float a0 = Norm(OuterStart + col * ColStep + _rot);
            float midA = a0 + ColStep * 0.5f;
            if (!WedgeVisible(a0, a0 + ColStep, _rOutBase, p, view)) continue;

            // The column's own gravity drop: scaled about the WHEEL centre, so
            // the ring implodes/explodes as one body rather than each column
            // shrinking into itself.
            var grow = TierTransform(p);
            var rotate = Matrix3x2.CreateRotation(a0, _c) * grow;
            for (int ring = 0; ring < stack.Length; ring++)
            {
                var sw = stack[ring];
                var col32 = Color.FromArgb(255, sw.R, sw.G, sw.B);
                ds.Transform = rotate;
                ds.FillGeometry(OuterCell(rc, ring), Fade(col32, p));
                if (sw.Code == near.Code)
                    _sel = (OuterSel(rc, ring), rotate,
                            IsDark(col32) ? Colors.White : Color.FromArgb(255, 20, 20, 20), p);
                ds.Transform = grow;

                if (labels) DrawCode(ds, sw.Code, _rOutBase + (ring + 0.5f) * _band, midA, col32, p);
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
                _sel = (sel, tile, IsDark(col32) ? Colors.White : Color.FromArgb(255, 20, 20, 20), p);
            ds.Transform = grow;

            if (labels) DrawCode(ds, cell.Sw.Code, rMid, mid, col32, p);
            ds.Transform = Matrix3x2.Identity;
        }
    }

    // The marker code, centred in its tile and oriented RADIALLY: the text is
    // rotated by (θ − 90°) about the label's own centre, which points the top of
    // the glyphs at the wheel's centre. Because the ring turns as a rigid body,
    // every code then holds the same orientation relative to the wheel through a
    // spin — the reason for the fixed offset rather than a cos-based flip, which
    // reads upright at rest but inverts discontinuously mid-rotation.
    // (Sign verified against the renderer: +90° would face the tops outward.)
    private void DrawCode(CanvasDrawingSession ds, string code, float r, float midA, Color bg, float a)
    {
        var p = At(r, midA);
        var keep = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(midA - MathF.PI / 2f, p) * keep;
        // The box only positions the (centred, unwrapped) line, but it has to
        // be at least as tall as the line or Win2D clips the descenders.
        double boxH = Math.Max(16.0, _codeFmt.FontSize * 1.7);
        ds.DrawText(code,
            new Rect(p.X - _band * 1.7, p.Y - boxH * 0.5, _band * 3.4, boxH),
            Fade(IsDark(bg) ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(230, 24, 24, 24), a),
            _codeFmt);
        ds.Transform = keep;
    }

    // HSL and RGB share one shape: three concentric arcs, each a tapered
    // ribbon of the colour it would produce, with a knob and a value bubble.
    private void DrawArcs(CanvasDrawingSession ds, float a)
    {
        if (a <= 0.002f) return;
        ds.Transform = TierTransform(a);
        for (int i = 0; i < 3; i++)
        {
            float r = _arcR[i];
            float a0 = _base - ArcHalf, a1 = _base + ArcHalf;
            const int steps = 220;
            for (int k = 0; k <= steps; k++)
            {
                float t = k / (float)steps;
                var p = At(r, a0 + (a1 - a0) * t);
                // Overlapping dots make a smooth, tapered stroke for free —
                // far cheaper than one gradient-filled geometry per segment.
                ds.FillCircle(p, 2.6f + 4.4f * t, Fade(ChannelColor(i, t), a));
            }

            float v = ChannelValue(i);
            var knob = At(r, a0 + (a1 - a0) * v);
            ds.FillCircle(knob, 11f * _ui, Fade(ChannelColor(i, v), a));
            ds.DrawCircle(knob, 11f * _ui, Fade(Color.FromArgb(255, 255, 255, 255), a), 2.5f);
            ds.DrawCircle(knob, 11f * _ui, Fade(Color.FromArgb(70, 0, 0, 0), a), 0.8f);

            var bubble = At(r - 40f * _ui, a0 + (a1 - a0) * v);
            Bubble(ds, bubble, ChannelText(i), a);
        }
        ds.Transform = Matrix3x2.Identity;
    }

    private void Bubble(CanvasDrawingSession ds, Vector2 p, string text, float a)
    {
        var rect = new Rect(p.X - 27 * _ui, p.Y - 15 * _ui, 54 * _ui, 30 * _ui);
        ds.FillRoundedRectangle(rect, 5, 5, Fade(Color.FromArgb(232, 18, 18, 18), a));
        ds.DrawRoundedRectangle(rect, 5, 5, Fade(Color.FromArgb(60, 255, 255, 255), a), 1f);
        ds.DrawText(text, rect, Fade(Color.FromArgb(255, 245, 245, 242), a), _bubbleFmt);
    }

    private void DrawRecents(CanvasDrawingSession ds, float a)
    {
        foreach (var (p, col) in _chipPts)
        {
            ds.FillCircle(p, _chipR, Fade(col, a));
            ds.DrawCircle(p, _chipR, Fade(Color.FromArgb(150, 140, 140, 140), a), 1.2f);
        }
    }

    // Mode labels, the eyedropper and the current-colour puck: the picker's own
    // chrome, laid on a small arc around the anchor exactly as the reference
    // stacks COPIC / HSL / RGB beside the dial.
    private void DrawChrome(CanvasDrawingSession ds, float a)
    {
        // Armed to mix, the puck shows BOTH colours - the one a pick will be
        // mixed into on the left, the live colour on the right - so it is clear
        // the next tap combines rather than replaces.
        if (_mixIndex > 0)
        {
            using var half = new CanvasPathBuilder(ds);
            half.BeginFigure(_puckPt + new Vector2(0, -17f * _ui));
            half.AddArc(_puckPt + new Vector2(0, 17f * _ui), 17f * _ui, 17f * _ui, 0f,
                        CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
            half.EndFigure(CanvasFigureLoop.Closed);
            using var geo = CanvasGeometry.CreatePath(half);
            ds.FillCircle(_puckPt, 17f * _ui, Fade(_color, a));
            ds.FillGeometry(geo, Fade(BaseColor, a));
        }
        else
        {
            ds.FillCircle(_puckPt, 17f * _ui, Fade(_color, a));
        }
        ds.DrawCircle(_puckPt, 17f * _ui, Fade(Color.FromArgb(120, 160, 160, 160), a), 1.5f);

        string[] names = { "COPIC", "HSL", "RGB" };
        for (int i = 0; i < 3; i++)
        {
            var p = _labelPt[i];
            bool on = (int)_mode == i;
            var rect = new Rect(p.X - 38 * _ui, p.Y - 17 * _ui, 76 * _ui, 34 * _ui);
            if (on)
            {
                ds.FillRoundedRectangle(rect, 5, 5, Fade(Color.FromArgb(235, 236, 234, 228), a));
                ds.DrawText(names[i], rect, Fade(Color.FromArgb(255, 20, 20, 19), a), _labelFmt);
            }
            else
            {
                ds.DrawText(names[i], rect, Fade(Color.FromArgb(210, 236, 234, 228), a), _labelFmt);
            }
        }

        DrawEyedropper(ds, _dropPt, 34f * _ui,
            Fade(_sampling ? Color.FromArgb(255, 217, 119, 87) : Color.FromArgb(235, 236, 234, 228), a),
            a);

        DrawMixRow(ds, a);
    }

    // V3 K.12's control surface: arm a ratio, then the next swatch you tap is
    // MIXED into the colour you started with as paint rather than replacing it.
    private void DrawMixRow(CanvasDrawingSession ds, float a)
    {
        var capRect = new Rect(_mixCaption.X - 40 * _ui, _mixCaption.Y - 13 * _ui, 80 * _ui, 26 * _ui);
        ds.DrawText(Loc.T("Picker.MixLabel"), capRect,
            Fade(_mixIndex > 0 ? Color.FromArgb(255, 217, 119, 87) : Color.FromArgb(150, 236, 234, 228), a),
            _bubbleFmt);

        for (int i = 0; i < 4; i++)
        {
            var p = _mixPt[i];
            bool on = _mixIndex == i;
            var rect = new Rect(p.X - 25 * _ui, p.Y - 15 * _ui, 50 * _ui, 30 * _ui);
            if (on)
            {
                ds.FillRoundedRectangle(rect, 5, 5, Fade(Color.FromArgb(235, 236, 234, 228), a));
                ds.DrawText(MixNames[i], rect, Fade(Color.FromArgb(255, 20, 20, 19), a), _bubbleFmt);
            }
            else
            {
                ds.DrawText(MixNames[i], rect, Fade(Color.FromArgb(190, 236, 234, 228), a), _bubbleFmt);
            }
        }
    }

    // Hand-authored eyedropper: a bulb on a 45° shaft that tapers to a point,
    // matching the flat single-weight silhouettes the rest of Quill's icons use.
    private static void DrawEyedropper(CanvasDrawingSession ds, Vector2 c, float size, Color col, float a)
    {
        float k = size / 24f;
        Vector2 L(float x, float y) => c + new Vector2((x - 12f) * k, (y - 12f) * k);

        var plate = new Rect(c.X - size * 0.62, c.Y - size * 0.62, size * 1.24, size * 1.24);
        ds.FillRoundedRectangle(plate, 6, 6, Fade(Color.FromArgb(150, 18, 18, 18), a));

        using (var b = new CanvasPathBuilder(ds))
        {
            b.BeginFigure(L(3.8f, 17.8f));
            b.AddLine(L(12.8f, 8.8f));
            b.AddLine(L(15.2f, 11.2f));
            b.AddLine(L(6.2f, 20.2f));
            b.AddLine(L(2.4f, 21.6f));
            b.EndFigure(CanvasFigureLoop.Closed);
            using var geo = CanvasGeometry.CreatePath(b);
            ds.FillGeometry(geo, col);
        }

        var bulbC = L(16.6f, 8.4f);
        var bulb = new Rect(bulbC.X - 5.2 * k, bulbC.Y - 3.6 * k, 10.4 * k, 7.2 * k);
        var keep = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(-MathF.PI / 4f, bulbC) * keep;
        ds.FillRoundedRectangle(bulb, 3.2f * k, 3.2f * k, col);
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
        if (Vector2.Distance(p, _dropPt) < 24f * _ui)
        {
            _sampling = true;
            _canvas.Invalidate();
            return;
        }
        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(p.X - _labelPt[i].X) < 40 * _ui && Math.Abs(p.Y - _labelPt[i].Y) < 19 * _ui)
            {
                Mode = (ColorWheelMode)i;
                return;
            }
        }
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(p.X - _mixPt[i].X) < 26 * _ui && Math.Abs(p.Y - _mixPt[i].Y) < 17 * _ui)
            {
                _mixIndex = i;
                _canvas.Invalidate();
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
                float rel = Norm(a - _base);
                if (Math.Abs(r - _arcR[i]) < 22f && Math.Abs(rel) <= ArcHalf + 0.06f)
                {
                    _dragArc = i;
                    SetChannel(i, (rel + ArcHalf) / (2 * ArcHalf));
                    return;
                }
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
            SetChannel(_dragArc, (Norm(a - _base) + ArcHalf) / (2 * ArcHalf));
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

    private void PickAt(Vector2 p)
    {
        float r = Vector2.Distance(p, _c);
        float a = MathF.Atan2(p.Y - _c.Y, p.X - _c.X) - _rot;
        if (SwatchAt(r, a) is not { } sw) return;
        Commit(Color.FromArgb(255, sw.R, sw.G, sw.B));
    }

    /// <summary>Chooses a colour: mixes it into <see cref="BaseColor"/> as paint
    /// when a ratio is armed (V3 K.12), publishes it, and asks the host to close
    /// (V3 K.11 - "the COPIC wheel closes automatically once a colour is
    /// chosen").
    ///
    /// THE ONE EXCEPTION, and why. K.11 and K.12 pull against each other: closing
    /// on the first pick makes mixing a single-shot operation, and mixing is by
    /// nature iterative - each addition starts from the RESULT of the last, which
    /// is how colour is actually built up on a palette. So the auto-close applies
    /// only while the mix row is OFF, which is the plain "pick a colour" case K.11
    /// is written about. Arm a ratio and the wheel deliberately stays up so
    /// additions can be stacked; tapping away still dismisses it, and setting the
    /// row back to OFF restores the close-on-pick behaviour exactly.</summary>
    private void Commit(Color picked)
    {
        bool mixing = _mixIndex > 0;
        Color = mixing ? PigmentMix.Mix(BaseColor, picked, MixRatio) : picked;
        BaseColor = _color;
        ColorChanged?.Invoke(_color);
        if (mixing) _canvas.Invalidate();   // the puck's two halves both moved
        else Picked?.Invoke();
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
        if (ring < 0 || ring >= MaxRings) return null;
        // Fold the angle into [0, 360) measured from the -90° start, then
        // one division lands the 10° column.
        double deg = a / Deg + 90.0;
        deg = ((deg % 360) + 360) % 360;
        int col = (int)Math.Floor(deg / 10.0) % OuterColumns.Length;
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
        // The mix ratio is armed for ONE session and disarms on the way in. The
        // wheel is a single long-lived instance shared by every call site, so
        // without this a ratio armed on the dial would still be armed the next
        // time the page-background or accent picker opened, and that picker's
        // first tap would silently blend instead of choosing (V3 K.12).
        _mixIndex = 0;
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
        if (_phase == Phase.Exiting) return;         // already leaving
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
        LandTransition();
        _landed = null;      // dropped on the floor: that is the whole point
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
        LandTransition();
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
