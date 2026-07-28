using System.Diagnostics;
using System.Numerics;
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
/// ── PLACEMENT ─────────────────────────────────────────────────────────────
/// The picker is its own surface, not something hanging off whatever opened
/// it. The ring centres itself in the viewport and is scaled so the WHOLE of
/// it — inner arc, grey ring and the deepest 17-band family column — is on
/// screen at once, at any window size. It therefore never moves while the
/// pointer moves or while the radial dial behind it re-renders. The point the
/// caller passes is kept only as a bias hint: it picks which side of the hub
/// the chrome cluster (mode labels, eyedropper, puck, recents) faces, so the
/// picker still leans toward the thing you tapped.
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
    // Breathing room between the outermost band and the window edge, px.
    private const float EdgeMargin = 18f;
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
    private float _snapTo;              // settle target once the glide is done
    private bool _snapping;
    private bool _sampling;             // eyedropper armed

    // drag bookkeeping
    private int _dragArc = -1;          // 0..2 while an HSL/RGB arc is dragged
    private bool _dragRing;
    private float _lastAngle;
    private Vector2 _pressPt, _lastPt;
    private float _pathPx;              // pointer path length since the press
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
    private float _rLabel, _rRecent, _chipR;
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

    // Cached tile geometry: one 10° cell per outer ring band (reused across all
    // 36 columns by rotation), plus one cell each for the two inner tiers.
    private readonly CanvasGeometry?[] _outerGeo = new CanvasGeometry?[MaxRings];
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
        _tier1Geo?.Dispose(); _tier1Geo = null;
        _tier2Geo?.Dispose(); _tier2Geo = null;
    }

    // =======================================================================
    // Geometry
    // =======================================================================
    // The ring is centred in the viewport and scaled so that ALL of it fits:
    // the reference's own radii are quoted against its 690-unit outer radius,
    // so dividing that radius into the space available keeps every proportion
    // the reference has while guaranteeing the outermost band of the deepest
    // family column is on screen. Nothing here reads the pointer or the
    // opener, so the wheel is fixed for as long as it is up.
    private void Layout(float w, float h)
    {
        _c = new Vector2(w * 0.5f, h * 0.5f);
        float refOut = 333f + MaxRings * 21f;  // the reference's outer radius
        float fit = Math.Max(150f, Math.Min(w, h) * 0.5f - EdgeMargin);
        float s = fit / refOut;                // reference-unit → local pixels

        _r1In = 285f * s; _r1Out = 302f * s;   // Tier 1 inner arc
        _r2In = 307f * s; _r2Out = 328f * s;   // Tier 2 grey ring
        _rOutBase = 333f * s; _band = 21f * s; // Tier 3+ columns, 21px rings
        _rIn = _r1In;
        _rOut = _rOutBase + MaxRings * _band;
        _codeFmt.FontSize = Math.Clamp(_band * 0.42f, 6f, 12f);

        // The chrome lives in the hub, so it is sized off the HOLE rather than
        // the window: it can never collide with Tier 1 whatever the shape.
        float hole = _r1In;
        _ui = Math.Clamp(hole / 180f, 0.60f, 1.20f);
        _labelFmt.FontSize = 15f * _ui;
        _bubbleFmt.FontSize = 13f * _ui;
        _rRecent = hole * 0.40f;
        _chipR = Math.Clamp(hole * 0.045f, 5f, 12f);
        _rLabel = hole * 0.68f;
        // The HSL / RGB face draws instead of the ring, so it gets the lot.
        _arcR[0] = fit * 0.42f;
        _arcR[1] = fit * 0.58f;
        _arcR[2] = fit * 0.74f;

        // The only thing the opener's point decides: which way the hub faces.
        var toHint = _hint - _c;
        _base = toHint.LengthSquared() < 4f ? 0f : MathF.Atan2(toHint.Y, toHint.X);

        _puckPt = At(_rLabel, _base - 0.75f);
        _labelPt[0] = At(_rLabel, _base - 0.42f);
        _labelPt[1] = At(_rLabel, _base - 0.09f);
        _labelPt[2] = At(_rLabel, _base + 0.24f);
        _dropPt = At(_rLabel, _base + 0.57f);

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

    // One 10° outer cell at ring band `ring`, inset by a hairline gap so
    // neighbours read as separate chips. Cached and re-used for all 36 columns.
    private CanvasGeometry OuterCell(ICanvasResourceCreator rc, int ring)
    {
        if (_outerGeo[ring] is { } cached) return cached;
        float r0 = _rOutBase + ring * _band + 1f;
        float r1 = r0 + _band - 2f;
        float gap = 1.2f / MathF.Max(r0, 1f);
        var geo = ArcTile(rc, r0, r1, gap, ColStep - gap);
        _outerGeo[ring] = geo;
        return geo;
    }

    private CanvasGeometry Tier1Cell(ICanvasResourceCreator rc)
    {
        if (_tier1Geo is { } cached) return cached;
        float r0 = _r1In + 1f, r1 = _r1Out - 1f;
        float gap = 1.0f / MathF.Max(r0, 1f);
        return _tier1Geo = ArcTile(rc, r0, r1, gap, Tier1Width - gap);
    }

    private CanvasGeometry Tier2Cell(ICanvasResourceCreator rc)
    {
        if (_tier2Geo is { } cached) return cached;
        float r0 = _r2In + 1f, r1 = _r2Out - 1f;
        float gap = 1.0f / MathF.Max(r0, 1f);
        return _tier2Geo = ArcTile(rc, r0, r1, gap, Tier2Width - gap);
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
        var view = new Rect(-40, -40, w + 80, h + 80);
        var near = CopicPalette.Nearest(_color.R, _color.G, _color.B);
        // Labels are dropped while the ring is really moving: they are the only
        // per-cell text in the frame, and they are unreadable at speed anyway.
        bool labels = Math.Abs(_vel) < 1.2f;

        // Tier 1 (inner arc) then Tier 2 (grey ring): disjoint bands, uniform
        // cell width, so one cached geometry rotated to each cell's A0 does it.
        // Each tier carries its own slot in the entrance cascade.
        DrawInnerTier(rc, ds, view, Tier1Cells, Tier1Cell(rc), (_r1In + _r1Out) * 0.5f, near, labels,
            TierProgress(Tier1OpenDelay, Tier1CloseDelay));
        DrawInnerTier(rc, ds, view, Tier2Cells, Tier2Cell(rc), (_r2In + _r2Out) * 0.5f, near, labels,
            TierProgress(Tier2OpenDelay, Tier2CloseDelay));

        // Tier 3+ (outer family columns): 36 fixed 10° columns, each a radial
        // stack of its own depth.
        for (int col = 0; col < OuterColumns.Length; col++)
        {
            var stack = OuterColumns[col];
            if (stack.Length == 0) continue;
            float p = TierProgress(ColOpenDelay[col], ColCloseDelay[col]);
            if (p <= 0.002f) continue;              // not arrived yet / already gone
            float a0 = Norm(OuterStart + col * ColStep + _rot);
            float midA = a0 + ColStep * 0.5f;
            float rColOut = _rOutBase + stack.Length * _band;

            // Cheap reject: sample the column's mid-angle across its radial run.
            bool visible = false;
            for (int k = 0; k <= 3 && !visible; k++)
            {
                var q = At(_rOutBase + (rColOut - _rOutBase) * k / 3f, midA);
                visible = view.Contains(new Point(q.X, q.Y));
            }
            if (!visible) continue;

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
                    ds.DrawGeometry(OuterCell(rc, ring),
                        Fade(IsDark(col32) ? Colors.White : Color.FromArgb(255, 20, 20, 20), p), 3f);
                ds.Transform = grow;

                if (labels) DrawCode(ds, sw.Code, _rOutBase + (ring + 0.5f) * _band, midA, col32, p);
                ds.Transform = Matrix3x2.Identity;
            }
        }
    }

    // Draws one inner tier: fill each cell by rotating the shared geometry to
    // the cell's start angle, outline the nearest swatch, and label it.
    private void DrawInnerTier(ICanvasResourceCreator rc, CanvasDrawingSession ds, Rect view,
        Cell[] cells, CanvasGeometry geo, float rMid, CopicSwatch near, bool labels, float p)
    {
        if (p <= 0.002f) return;
        var grow = TierTransform(p);
        foreach (var cell in cells)
        {
            float a0 = cell.A0 + _rot;
            float mid = (cell.A0 + cell.A1) * 0.5f + _rot;
            var q = At(rMid, mid);
            if (!view.Contains(new Point(q.X, q.Y))) continue;   // thin band: one sample is enough

            var col32 = Color.FromArgb(255, cell.Sw.R, cell.Sw.G, cell.Sw.B);
            ds.Transform = Matrix3x2.CreateRotation(a0, _c) * grow;
            ds.FillGeometry(geo, Fade(col32, p));
            if (cell.Sw.Code == near.Code)
                ds.DrawGeometry(geo,
                    Fade(IsDark(col32) ? Colors.White : Color.FromArgb(255, 20, 20, 20), p), 2.5f);
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
        ds.DrawText(code,
            new Rect(p.X - _band * 1.7, p.Y - 8, _band * 3.4, 16),
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
        ds.FillCircle(_puckPt, 17f * _ui, Fade(_color, a));
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
        // Nothing is where it looks like it is mid-transition (every tier is
        // scaled about the centre on its own clock), and an exit is already
        // committed, so input parks until the cascade lands. It is ~280ms.
        if (_phase != Phase.Idle) { e.Handled = true; return; }

        var pt = e.GetCurrentPoint((UIElement)sender).Position;
        var p = new Vector2((float)pt.X, (float)pt.Y);
        e.Handled = true;
        _input?.CapturePointer(e.Pointer);

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
        for (int i = 0; i < _chipPts.Count; i++)
        {
            if (Vector2.Distance(p, _chipPts[i].Pt) < _chipR + 3f)
            {
                Color = _chipPts[i].Col;
                ColorChanged?.Invoke(_color);
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
                _snapping = false;
                _vel = 0;
                _dragRing = true;
                _lastAngle = a;
                _pressPt = _lastPt = p;
                _pathPx = 0f;
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
        _rot += d;
        _pathPx += Vector2.Distance(p, _lastPt);
        _lastPt = p;
        // 60 Hz-ish frames: a light smoothing keeps a jittery pen from throwing
        // the ring across the screen on release
        _vel = _vel * 0.55f + (d * 60f) * 0.45f;
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
        if (Math.Abs(_vel) > StopVel) _spin.Start();
        else BeginSnap();
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
        Color = Color.FromArgb(255, sw.R, sw.G, sw.B);
        ColorChanged?.Invoke(_color);
    }

    // Pure-arithmetic hit-test: pick the tier by radius, then the cell by angle.
    // `a` is the pointer angle already de-rotated into the ring's own frame.
    private CopicSwatch? SwatchAt(float r, float a)
    {
        if (r >= _r1In && r <= _r1Out) return CellHit(Tier1Cells, a);
        if (r >= _r2In && r <= _r2Out) return CellHit(Tier2Cells, a);
        if (r >= _rOutBase)
        {
            int ring = (int)MathF.Floor((r - _rOutBase) / _band);
            if (ring < 0) return null;
            // Fold the angle into [0, 360) measured from the -90° start, then
            // one division lands the 10° column.
            double deg = a / Deg + 90.0;
            deg = ((deg % 360) + 360) % 360;
            int col = (int)Math.Floor(deg / 10.0) % OuterColumns.Length;
            var stack = OuterColumns[col];
            if (ring < stack.Length) return stack[ring];
        }
        return null;
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

    // The glide: exponential falloff, then a short ease onto the nearest column
    // boundary so the ring always comes to rest square rather than mid-swatch.
    private void OnSpinTick(object? sender, object e)
    {
        if (_snapping)
        {
            float d = Norm(_snapTo - _rot);
            if (Math.Abs(d) < 0.0015f) { _rot = _snapTo; _snapping = false; _spin.Stop(); }
            else _rot += d * 0.28f;
            _canvas.Invalidate();
            return;
        }

        _rot += _vel * (1f / 60f);
        _vel *= Decay;
        if (Math.Abs(_vel) < StopVel) BeginSnap();
        _canvas.Invalidate();
    }

    private void BeginSnap()
    {
        _vel = 0;
        _snapping = true;
        _snapTo = MathF.Round(_rot / ColStep) * ColStep;
        if (!_spin.IsEnabled) _spin.Start();
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
    private bool _clockPending;
    private Action? _exitDone;

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
        if (ReduceMotion)
        {
            _anim.Stop();
            _phase = Phase.Idle;      // straight to the end state
            _canvas.Invalidate();
            return;
        }
        _phase = Phase.Entering;
        _clockPending = true;
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
        if (!_anim.IsEnabled) _anim.Start();
        _canvas.Invalidate();
    }

    /// Drops a transition in flight WITHOUT firing its completion callback —
    /// for when the picker is reopened mid-close.
    public void CancelAnimation()
    {
        _anim.Stop();
        _phase = Phase.Idle;
        _clockPending = false;
        _exitDone = null;
    }

    private void OnAnimTick(object? sender, object e)
    {
        if (_phase != Phase.Idle && ElapsedMs() < (_phase == Phase.Entering ? EnterSpan : ExitSpan))
        {
            _canvas.Invalidate();
            return;
        }

        _anim.Stop();
        bool wasExit = _phase == Phase.Exiting;
        _phase = Phase.Idle;
        _clockPending = false;
        var done = _exitDone;
        _exitDone = null;
        if (wasExit) { done?.Invoke(); return; }   // the host hides us; no repaint
        _canvas.Invalidate();                      // land on the exact end state
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
