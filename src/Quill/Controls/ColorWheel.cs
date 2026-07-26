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
///         anchorInRootCoords,             // where the ring is centred — e.g.
///                                         // the radial dial's centre disc, or
///                                         // the middle of the button tapped
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
    // The outer ring is 36 contiguous 10° columns starting at the top (-90°).
    private const float ColStep = 10f * Deg;
    private const float OuterStart = -90f * Deg;

    private readonly CanvasControl _canvas = new();
    private readonly DispatcherTimer _spin = new() { Interval = TimeSpan.FromMilliseconds(16) };

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

    /// Where the ring is centred, in this control's coordinates.
    public Vector2 Anchor
    {
        get => _c;
        set { _c = value; _geoDirty = true; _canvas.Invalidate(); }
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
    private Vector2 _c;
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
    private double _travelled;
    private long _pressTicks;

    // resolved geometry (see Layout). All radii are scaled from the reference's
    // own pixel radii by `m/760`, so the three tiers keep the reference's
    // proportions on Quill's full-screen canvas.
    private float _r1In, _r1Out;        // Tier 1 (inner accent/core arc)
    private float _r2In, _r2Out;        // Tier 2 (grey ring)
    private float _rOutBase, _band;     // Tier 3+ (outer family columns)
    private float _rIn, _rOut;          // grabbable annulus [inner tier, outer edge]
    private float _rLabel, _rRecent, _chipR;
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

        _canvas.Draw += OnDraw;
        host.PointerPressed += OnPressed;
        host.PointerMoved += OnMoved;
        host.PointerReleased += OnReleased;
        host.PointerCanceled += (_, _) => EndDrag();
        _spin.Tick += OnSpinTick;
        SizeChanged += (_, _) => { _geoDirty = true; _canvas.Invalidate(); };
        Unloaded += (_, _) =>
        {
            _spin.Stop();
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
    // Every radius is a fraction of the shorter viewport edge, so the ring
    // keeps the same proportions the reference has on a full-screen canvas —
    // an arc that sweeps past the anchor rather than a dialog that fits.
    private void Layout(float w, float h)
    {
        float m = Math.Max(320f, Math.Min(w, h));
        float s = m / 760f;                    // reference-unit → local pixels

        _r1In = 285f * s; _r1Out = 302f * s;   // Tier 1 inner arc
        _r2In = 307f * s; _r2Out = 328f * s;   // Tier 2 grey ring
        _rOutBase = 333f * s; _band = 21f * s; // Tier 3+ columns, 21px rings
        _rIn = _r1In;
        _rOut = _rOutBase + MaxRings * _band;
        _codeFmt.FontSize = Math.Clamp(_band * 0.40f, 6.5f, 12f);

        _rRecent = m * 0.16f;
        _chipR = m * 0.013f;
        _rLabel = m * 0.26f;
        _arcR[0] = m * 0.34f;
        _arcR[1] = m * 0.44f;
        _arcR[2] = m * 0.54f;

        var toCentre = new Vector2(w * 0.5f, h * 0.5f) - _c;
        _base = toCentre.LengthSquared() < 4f ? 0f : MathF.Atan2(toCentre.Y, toCentre.X);

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

        if (_mode == ColorWheelMode.Copic) DrawRing(sender, ds, w, h);
        else DrawArcs(ds);

        DrawRecents(ds);
        DrawChrome(ds);
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
        DrawInnerTier(rc, ds, view, Tier1Cells, Tier1Cell(rc), (_r1In + _r1Out) * 0.5f, near, labels);
        DrawInnerTier(rc, ds, view, Tier2Cells, Tier2Cell(rc), (_r2In + _r2Out) * 0.5f, near, labels);

        // Tier 3+ (outer family columns): 36 fixed 10° columns, each a radial
        // stack of its own depth.
        for (int col = 0; col < OuterColumns.Length; col++)
        {
            var stack = OuterColumns[col];
            if (stack.Length == 0) continue;
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

            var rotate = Matrix3x2.CreateRotation(a0, _c);
            for (int ring = 0; ring < stack.Length; ring++)
            {
                var sw = stack[ring];
                var col32 = Color.FromArgb(255, sw.R, sw.G, sw.B);
                ds.Transform = rotate;
                ds.FillGeometry(OuterCell(rc, ring), col32);
                if (sw.Code == near.Code)
                    ds.DrawGeometry(OuterCell(rc, ring),
                        IsDark(col32) ? Colors.White : Color.FromArgb(255, 20, 20, 20), 3f);
                ds.Transform = Matrix3x2.Identity;

                if (labels) DrawCode(ds, sw.Code, _rOutBase + (ring + 0.5f) * _band, midA, col32);
            }
        }
    }

    // Draws one inner tier: fill each cell by rotating the shared geometry to
    // the cell's start angle, outline the nearest swatch, and label it.
    private void DrawInnerTier(ICanvasResourceCreator rc, CanvasDrawingSession ds, Rect view,
        Cell[] cells, CanvasGeometry geo, float rMid, CopicSwatch near, bool labels)
    {
        foreach (var cell in cells)
        {
            float a0 = cell.A0 + _rot;
            float mid = (cell.A0 + cell.A1) * 0.5f + _rot;
            var q = At(rMid, mid);
            if (!view.Contains(new Point(q.X, q.Y))) continue;   // thin band: one sample is enough

            var col32 = Color.FromArgb(255, cell.Sw.R, cell.Sw.G, cell.Sw.B);
            ds.Transform = Matrix3x2.CreateRotation(a0, _c);
            ds.FillGeometry(geo, col32);
            if (cell.Sw.Code == near.Code)
                ds.DrawGeometry(geo, IsDark(col32) ? Colors.White : Color.FromArgb(255, 20, 20, 20), 2.5f);
            ds.Transform = Matrix3x2.Identity;

            if (labels) DrawCode(ds, cell.Sw.Code, rMid, mid, col32);
        }
    }

    // The marker code, centred in its tile and oriented RADIALLY: the text is
    // rotated by (θ − 90°) about the label's own centre, which points the top of
    // the glyphs at the wheel's centre. Because the ring turns as a rigid body,
    // every code then holds the same orientation relative to the wheel through a
    // spin — the reason for the fixed offset rather than a cos-based flip, which
    // reads upright at rest but inverts discontinuously mid-rotation.
    // (Sign verified against the renderer: +90° would face the tops outward.)
    private void DrawCode(CanvasDrawingSession ds, string code, float r, float midA, Color bg)
    {
        var p = At(r, midA);
        var keep = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(midA - MathF.PI / 2f, p) * keep;
        ds.DrawText(code,
            new Rect(p.X - _band * 1.7, p.Y - 8, _band * 3.4, 16),
            IsDark(bg) ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(230, 24, 24, 24),
            _codeFmt);
        ds.Transform = keep;
    }

    // HSL and RGB share one shape: three concentric arcs, each a tapered
    // ribbon of the colour it would produce, with a knob and a value bubble.
    private void DrawArcs(CanvasDrawingSession ds)
    {
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
                ds.FillCircle(p, 2.6f + 4.4f * t, ChannelColor(i, t));
            }

            float v = ChannelValue(i);
            var knob = At(r, a0 + (a1 - a0) * v);
            ds.FillCircle(knob, 11f, ChannelColor(i, v));
            ds.DrawCircle(knob, 11f, Color.FromArgb(255, 255, 255, 255), 2.5f);
            ds.DrawCircle(knob, 11f, Color.FromArgb(70, 0, 0, 0), 0.8f);

            var bubble = At(r - 40f, a0 + (a1 - a0) * v);
            Bubble(ds, bubble, ChannelText(i));
        }
    }

    private void Bubble(CanvasDrawingSession ds, Vector2 p, string text)
    {
        var rect = new Rect(p.X - 27, p.Y - 15, 54, 30);
        ds.FillRoundedRectangle(rect, 5, 5, Color.FromArgb(232, 18, 18, 18));
        ds.DrawRoundedRectangle(rect, 5, 5, Color.FromArgb(60, 255, 255, 255), 1f);
        ds.DrawText(text, rect, Color.FromArgb(255, 245, 245, 242), _bubbleFmt);
    }

    private void DrawRecents(CanvasDrawingSession ds)
    {
        foreach (var (p, col) in _chipPts)
        {
            ds.FillCircle(p, _chipR, col);
            ds.DrawCircle(p, _chipR, Color.FromArgb(150, 140, 140, 140), 1.2f);
        }
    }

    // Mode labels, the eyedropper and the current-colour puck: the picker's own
    // chrome, laid on a small arc around the anchor exactly as the reference
    // stacks COPIC / HSL / RGB beside the dial.
    private void DrawChrome(CanvasDrawingSession ds)
    {
        ds.FillCircle(_puckPt, 17f, _color);
        ds.DrawCircle(_puckPt, 17f, Color.FromArgb(120, 160, 160, 160), 1.5f);

        string[] names = { "COPIC", "HSL", "RGB" };
        for (int i = 0; i < 3; i++)
        {
            var p = _labelPt[i];
            bool on = (int)_mode == i;
            var rect = new Rect(p.X - 38, p.Y - 17, 76, 34);
            if (on)
            {
                ds.FillRoundedRectangle(rect, 5, 5, Color.FromArgb(235, 236, 234, 228));
                ds.DrawText(names[i], rect, Color.FromArgb(255, 20, 20, 19), _labelFmt);
            }
            else
            {
                ds.DrawText(names[i], rect, Color.FromArgb(210, 236, 234, 228), _labelFmt);
            }
        }

        DrawEyedropper(ds, _dropPt, 34f,
            _sampling ? Color.FromArgb(255, 217, 119, 87) : Color.FromArgb(235, 236, 234, 228));
    }

    // Hand-authored eyedropper: a bulb on a 45° shaft that tapers to a point,
    // matching the flat single-weight silhouettes the rest of Quill's icons use.
    private static void DrawEyedropper(CanvasDrawingSession ds, Vector2 c, float size, Color col)
    {
        float k = size / 24f;
        Vector2 L(float x, float y) => c + new Vector2((x - 12f) * k, (y - 12f) * k);

        var plate = new Rect(c.X - size * 0.62, c.Y - size * 0.62, size * 1.24, size * 1.24);
        ds.FillRoundedRectangle(plate, 6, 6, Color.FromArgb(150, 18, 18, 18));

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
        var pt = e.GetCurrentPoint((UIElement)sender).Position;
        var p = new Vector2((float)pt.X, (float)pt.Y);
        e.Handled = true;
        CapturePointer(e.Pointer);

        if (_sampling)
        {
            _sampling = false;
            SampleRequested?.Invoke(pt);
            _canvas.Invalidate();
            return;
        }

        // chrome first: it sits over the empty middle of the ring
        if (Vector2.Distance(p, _dropPt) < 24f)
        {
            _sampling = true;
            _canvas.Invalidate();
            return;
        }
        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(p.X - _labelPt[i].X) < 40 && Math.Abs(p.Y - _labelPt[i].Y) < 19)
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
                _travelled = 0;
                _pressTicks = Environment.TickCount64;
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

        ReleasePointerCapture(e.Pointer);
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
        _travelled += Math.Abs(d);
        // 60 Hz-ish frames: a light smoothing keeps a jittery pen from throwing
        // the ring across the screen on release
        _vel = _vel * 0.55f + (d * 60f) * 0.45f;
        _canvas.Invalidate();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint((UIElement)sender).Position;
        bool wasRing = _dragRing;
        double travelled = _travelled;
        long held = Environment.TickCount64 - _pressTicks;
        EndDrag();
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (!wasRing) return;

        if (travelled < 0.012 && held < 600)
        {
            PickAt(new Vector2((float)pt.X, (float)pt.Y));
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
    // Colour maths
    // =======================================================================
    private static bool IsDark(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128;

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
