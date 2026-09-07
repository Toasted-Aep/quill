using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Quill.Models;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Quill.Services;

/// <summary>
/// The oil dab engine and its per-gesture scratch buffer (OILPAINT-SPEC §2.1,
/// §1.3, §3.1). Strokes are not filled outlines: each pointer segment becomes N
/// stamped dabs walked by arc length, and the whole gesture accumulates in a
/// scratch copy of the tile grid so a stroke that crosses itself composites
/// exactly once.
///
/// Phase 1b deliberately stops short of bristles, reservoir depletion,
/// wet-on-wet pickup and pigment mixing — those are Phases 2-4 and each is a
/// per-dab computation bolted onto this loop.
/// </summary>
public sealed class OilBrush : IDisposable
{
    // Spec defaults. The four preset sliders are deferred, so these are the
    // shipping constants for this phase.
    public const float DefaultSpacing = 0.08f;   // fraction of DIAMETER
    public const float MinSpacing = 0.02f;
    public const float MaxSpacing = 0.25f;
    public const float DefaultImpasto = 0.6f;
    public const float DefaultFlow = 0.985f;     // oil is opaque; this is the stroke's target coverage

    /// <summary>
    /// Height deposited per unit of (Impasto * alphaDab).
    ///
    /// The spec quotes 0.06 and makes two claims that cannot both hold at
    /// HeightMapScale = 6: that one pass reaches h ~ 0.2 with headroom for four
    /// or five more, AND that a single drag already reads as thick paint under
    /// the raking light. Measured, h = 0.2 is 1.2 world units of relief across a
    /// ~34-unit stroke — a slope of about 4 degrees, which a 30-degree light
    /// barely registers; it took five stacked passes to read as impasto.
    ///
    /// The single-drag result is the headline, so this is calibrated so one
    /// ordinary pass at the default Impasto lands near h = 0.45 and reads
    /// immediately, still leaving roughly two more passes before the terminal
    /// clamp caps it at 1.0. Deviation from the literal 0.06 is deliberate and
    /// is the one number to revisit if Phase 4's sideways displacement lands,
    /// since that adds relief the height field alone cannot express.
    /// </summary>
    public const float HeightPerDab = 0.32f;
    public const int MaxDabsPerEvent = 256;      // anti-hang clamp
    public const int ScratchTileFlushLimit = 12; // §1.3: flush and restart past this

    private const int StampPx = 128;             // unit dab stamp resolution

    public float Spacing { get; set; } = DefaultSpacing;
    public float Impasto { get; set; } = DefaultImpasto;
    public float Flow { get; set; } = DefaultFlow;
    public float Diameter { get; set; } = 14f;   // world units
    public Color Color { get; set; } = Color.FromArgb(255, 193, 68, 14);

    private readonly PaintTileStore _store;
    private readonly Dictionary<(int, int), Scratch> _scratch = new();
    // Per-gesture undo capture (§6.1): the pre-stroke state of each tile the
    // gesture composites onto, recorded the FIRST time that tile is touched
    // (mid-gesture flush included), so the paint undo "before" is the true
    // pre-gesture state however many segments the stroke commits in.
    private readonly Dictionary<(int, int), (bool Existed, byte[]? ColourZ, byte[]? HeightZ)> _undoBefore = new();
    private CanvasRenderTarget? _heightStamp;
    private CanvasRadialGradientBrush? _colourBrush;

    private Vector2 _last;
    private float _prLast;
    private float _dabAccum;          // THE CARRY — persists across pointer events
    private bool _active;
    private double _minX, _minY, _maxX, _maxY;   // world bounds touched since the last flush

    public OilBrush(PaintTileStore store) => _store = store;

    public bool Active => _active;
    public int ScratchTileCount => _scratch.Count;
    /// <summary>Total dabs emitted since <see cref="Begin"/> — diagnostics only.</summary>
    public int DabCount { get; private set; }

    /// <summary>Step along the stroke in world units. The 0.5 floor bounds the
    /// dab count as the radius goes to zero.</summary>
    public float Step => MathF.Max(0.5f, Math.Clamp(Spacing, MinSpacing, MaxSpacing) * Diameter);

    /// <summary>Dabs laid over any one point of the stroke — the exponent that
    /// linearises per-dab alpha so changing spacing does not change apparent
    /// darkness (libmypaint's opaque_linearize).</summary>
    public float DabsPerPixel => MathF.Max(1f, Diameter / Step);

    /// <summary>alphaDab = 1 - (1 - alphaTarget)^(1/dabsPerPixel).</summary>
    public float DabAlpha => 1f - MathF.Pow(1f - Math.Clamp(Flow, 0.01f, 0.999f), 1f / DabsPerPixel);

    /// <summary>Height added per dab (§3.1): h += load * Impasto * alphaDab * K.
    /// Phase 1b has no reservoir, so load is fixed at 1.</summary>
    public float DabHeight => Impasto * DabAlpha * HeightPerDab;

    // =======================================================================
    // Gesture lifecycle
    // =======================================================================

    public void Begin(Vector2 p, float pressure)
    {
        _active = true;
        _last = p;
        _prLast = pressure;
        _dabAccum = 0f;      // a new stroke starts with a clean carry
        DabCount = 0;
        _undoBefore.Clear();
        ResetBounds();
    }

    /// <summary>
    /// Walks the segment (last -> p) by arc length, emitting a dab every Step.
    /// The leftover distance stays in <c>_dabAccum</c> and is carried into the
    /// NEXT pointer event — without that carry a stroke beads at every event
    /// boundary, which is invisible in a screenshot and miserable to debug later.
    /// </summary>
    public int Extend(ICanvasResourceCreator rc, Vector2 p, float pressure)
    {
        if (!_active) return 0;
        float travel = Vector2.Distance(_last, p);
        if (travel <= 1e-4f) return 0;

        float step = Step;
        _dabAccum += travel;
        int emitted = 0;
        while (_dabAccum >= step && emitted < MaxDabsPerEvent)
        {
            _dabAccum -= step;
            // position along THIS segment: the leftover is the distance still to
            // run, so the dab sits (travel - leftover) along it
            float u = Math.Clamp(1f - (_dabAccum / travel), 0f, 1f);
            EmitDab(rc, Vector2.Lerp(_last, p, u), MathF.Max(0.02f, MathF.Abs(_prLast + (pressure - _prLast) * u)));
            emitted++;
        }
        _last = p;
        _prLast = pressure;
        DabCount += emitted;
        return emitted;
    }

    /// <summary>Pen-up: composite the whole gesture onto the canvas exactly once.</summary>
    public Rect? End(ICanvasResourceCreator rc)
    {
        if (!_active) return null;
        var bounds = CommitScratch(rc);
        _active = false;
        return bounds;
    }

    public void Cancel()
    {
        _active = false;
        _undoBefore.Clear();
        DisposeScratch();
    }

    // =======================================================================
    // Dabs
    // =======================================================================

    private void EmitDab(ICanvasResourceCreator rc, Vector2 c, float pressure)
    {
        // pressure drives diameter; the reservoir that will also drive it is
        // Phase 2, so this is the whole width story for now
        float d = MathF.Max(0.6f, Diameter * (0.45f + 0.55f * Math.Clamp(pressure, 0f, 1f)));
        float r = d * 0.5f;
        var world = new Rect(c.X - r, c.Y - r, d, d);
        Grow(world);

        float aDab = DabAlpha;
        float dh = DabHeight;

        int t0x = PaintTileStore.TileIndex(world.Left), t1x = PaintTileStore.TileIndex(world.Right - 0.001);
        int t0y = PaintTileStore.TileIndex(world.Top), t1y = PaintTileStore.TileIndex(world.Bottom - 0.001);
        for (int ty = t0y; ty <= t1y; ty++)
            for (int tx = t0x; tx <= t1x; tx++)
            {
                var s = GetScratch(rc, tx, ty);
                var toTile = Matrix3x2.CreateTranslation(-tx * (float)PaintTileStore.TileSize,
                                                         -ty * (float)PaintTileStore.TileSize);
                // ---- colour: source-over at the LINEARISED per-dab alpha ----
                using (var ds = s.Colour.CreateDrawingSession())
                {
                    ds.Transform = toTile;
                    var brush = EnsureColourBrush(ds);
                    brush.Center = c;
                    brush.RadiusX = r;
                    brush.RadiusY = r;
                    brush.Opacity = aDab;
                    ds.FillEllipse(c, r, r, brush);
                }
                // ---- height: CanvasBlend.Add, at full float precision -------
                // The stamp carries the falloff at amplitude 1.0 and DrawImage's
                // float opacity scales it, because an 8-bit brush colour cannot
                // express a per-dab height of ~0.007 without ~18% quantisation.
                using (var ds = s.Height.CreateDrawingSession())
                {
                    ds.Transform = toTile;
                    ds.Blend = CanvasBlend.Add;
                    ds.DrawImage(EnsureHeightStamp(rc), world, new Rect(0, 0, StampPx, StampPx),
                                 dh, CanvasImageInterpolation.Linear);
                }
            }
    }

    /// <summary>
    /// Dab profile: a flat CORE out to 45% of the radius, then a smoothstep rim
    /// to zero. A pure bump (1-t^2)^2 was tried first and is wrong for paint —
    /// it stacks so little mass that a stroke reads translucent and its ridge is
    /// too shallow for the raking light to catch. A plateau is also what a
    /// loaded brush actually deposits.
    /// </summary>
    private const float DabCore = 0.45f;

    private static float Falloff(float t)
    {
        if (t <= DabCore) return 1f;
        if (t >= 1f) return 0f;
        float u = (t - DabCore) / (1f - DabCore);
        return 1f - u * u * (3f - 2f * u);
    }

    /// <summary>
    /// HEIGHT uses a dome, not the colour profile's plateau. A flat-topped
    /// height dab leaves the middle of the stroke geometrically flat — its
    /// normal points straight up, so a raking light finds no gradient and the
    /// impasto reads as a flat colour band with a faint edge. A dome gives the
    /// stroke genuine cross-section curvature, which is also what a loaded
    /// brush leaves behind: thick down the centre, thinning to the edges.
    /// </summary>
    private static float HeightFalloff(float t) => t >= 1f ? 0f : 1f - t * t;

    private CanvasRadialGradientBrush EnsureColourBrush(ICanvasResourceCreator rc)
    {
        if (_colourBrush != null) return _colourBrush;
        var c = Color;
        CanvasGradientStop S(float t) => new()
        {
            Position = t,
            Color = Color.FromArgb((byte)Math.Clamp(Falloff(t) * 255f, 0f, 255f), c.R, c.G, c.B),
        };
        _colourBrush = new CanvasRadialGradientBrush(rc,
            new[] { S(0f), S(0.35f), S(0.6f), S(0.8f), S(1f) });
        return _colourBrush;
    }

    private CanvasRenderTarget EnsureHeightStamp(ICanvasResourceCreator rc)
    {
        if (_heightStamp != null) return _heightStamp;
        _heightStamp = new CanvasRenderTarget(rc, StampPx, StampPx, 96f,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        // written procedurally so the profile is exact and testable rather than
        // whatever a gradient brush happens to interpolate
        var px = new byte[StampPx * StampPx * 8];
        float half = StampPx * 0.5f;
        for (int y = 0; y < StampPx; y++)
            for (int x = 0; x < StampPx; x++)
            {
                float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                float a = HeightFalloff(MathF.Sqrt(dx * dx + dy * dy));
                ushort h = BitConverter.HalfToUInt16Bits((Half)a);
                int i = (y * StampPx + x) * 8;
                byte lo = (byte)(h & 0xFF), hi = (byte)(h >> 8);
                px[i] = lo; px[i + 1] = hi;
                px[i + 2] = lo; px[i + 3] = hi;
                px[i + 4] = lo; px[i + 5] = hi;
                px[i + 6] = lo; px[i + 7] = hi;   // height lives in ALPHA
            }
        _heightStamp.SetPixelBytes(px);
        return _heightStamp;
    }

    // =======================================================================
    // Scratch buffer (§1.3)
    // =======================================================================

    private sealed class Scratch : IDisposable
    {
        public required CanvasRenderTarget Colour { get; init; }
        public required CanvasRenderTarget Height { get; init; }
        public void Dispose()
        {
            try { Colour.Dispose(); } catch { }
            try { Height.Dispose(); } catch { }
        }
    }

    public bool TryGetScratchColour(int tx, int ty, out CanvasRenderTarget colour)
    {
        if (_scratch.TryGetValue((tx, ty), out var s)) { colour = s.Colour; return true; }
        colour = null!;
        return false;
    }

    private Scratch GetScratch(ICanvasResourceCreator rc, int tx, int ty)
    {
        if (_scratch.TryGetValue((tx, ty), out var s)) return s;
        var colour = new CanvasRenderTarget(rc, PaintTileStore.TileSize, PaintTileStore.TileSize, 96f,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        var height = new CanvasRenderTarget(rc, PaintTileStore.TileSize, PaintTileStore.TileSize, 96f,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        var clear = Color.FromArgb(0, 0, 0, 0);
        using (var ds = colour.CreateDrawingSession()) ds.Clear(clear);
        using (var ds = height.CreateDrawingSession()) ds.Clear(clear);
        s = new Scratch { Colour = colour, Height = height };
        _scratch[(tx, ty)] = s;
        return s;
    }

    /// <summary>True once the gesture has spread past the scratch budget; the
    /// caller flushes and starts a fresh segment (a self-cross across that seam
    /// double-composites — rare enough to accept, cheap enough to fix later).</summary>
    public bool NeedsMidGestureFlush => _scratch.Count > ScratchTileFlushLimit;

    /// <summary>
    /// Composite scratch onto the canvas ONCE. Colour is plain source-over;
    /// height is added and then clamped to 1.0 by a terminal ColorMatrixEffect —
    /// Direct2D does not clamp at FLOAT precision, and relying on it is a classic
    /// source of white speckle.
    /// </summary>
    public Rect? CommitScratch(ICanvasResourceCreator rc)
    {
        if (_scratch.Count == 0) return null;
        foreach (var ((tx, ty), s) in _scratch)
        {
            RecordBefore(tx, ty);       // snapshot the pre-stroke tile BEFORE it changes
            var tile = _store.GetOrCreate(rc, tx, ty);
            try
            {
                using (var ds = tile.Colour.CreateDrawingSession())
                    ds.DrawImage(s.Colour);

                using (var tmp = new CanvasRenderTarget(rc, PaintTileStore.TileSize, PaintTileStore.TileSize, 96f,
                           DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied))
                {
                    using (var ds = tmp.CreateDrawingSession())
                    {
                        ds.Clear(Color.FromArgb(0, 0, 0, 0));
                        ds.DrawImage(tile.Height);
                        ds.Blend = CanvasBlend.Add;
                        ds.DrawImage(s.Height);
                    }
                    using var clamp = new ColorMatrixEffect
                    {
                        Source = tmp,
                        ColorMatrix = HeightClampMatrix,
                        ClampOutput = true,
                        BufferPrecision = CanvasBufferPrecision.Precision16Float,
                    };
                    using var ds2 = tile.Height.CreateDrawingSession();
                    ds2.Blend = CanvasBlend.Copy;
                    ds2.DrawImage(clamp);
                }
                tile.InvalidateLit();
            }
            catch { /* one bad tile must not take the stroke down */ }
        }
        DisposeScratch();
        var b = BoundsRect();
        ResetBounds();
        return b;
    }

    private static readonly Matrix5x4 HeightClampMatrix = new()
    {
        M11 = 1f, M22 = 1f, M33 = 1f, M44 = 1f,
    };

    private void DisposeScratch()
    {
        foreach (var s in _scratch.Values) s.Dispose();
        _scratch.Clear();
    }

    // =======================================================================
    // Undo capture (§6.1)
    // =======================================================================

    /// <summary>Snapshot a tile's pre-stroke bytes the first time the gesture
    /// touches it. A tile the stroke is about to allocate is recorded as
    /// non-existent, so undo drops it again.</summary>
    private void RecordBefore(int tx, int ty)
    {
        var key = (tx, ty);
        if (_undoBefore.ContainsKey(key)) return;
        if (_store.TryGet(tx, ty, out var existing))
            _undoBefore[key] = (true,
                PaintTileCodec.CompressRaw(existing.Colour.GetPixelBytes()),
                PaintTileCodec.CompressRaw(existing.Height.GetPixelBytes()));
        else
            _undoBefore[key] = (false, null, null);
    }

    /// <summary>
    /// Pen-up: read back the AFTER state of every tile the gesture touched and
    /// pair it with the recorded BEFORE, yielding the tile-granular deltas for one
    /// <c>PaintTilesAction</c>. Returns null when the gesture painted nothing.
    /// Clears the capture, so the next stroke starts clean.
    /// </summary>
    public List<PaintTileCapture>? TakeUndoCaptures(ICanvasResourceCreator rc)
    {
        if (_undoBefore.Count == 0) return null;
        var list = new List<PaintTileCapture>(_undoBefore.Count);
        foreach (var ((tx, ty), before) in _undoBefore)
        {
            byte[]? afterColour = null, afterHeight = null;
            if (_store.TryGet(tx, ty, out var t))
            {
                afterColour = PaintTileCodec.CompressRaw(t.Colour.GetPixelBytes());
                afterHeight = PaintTileCodec.CompressRaw(t.Height.GetPixelBytes());
            }
            list.Add(new PaintTileCapture(tx, ty, before.Existed, before.ColourZ, before.HeightZ,
                                          afterColour, afterHeight));
        }
        _undoBefore.Clear();
        return list;
    }

    // =======================================================================

    private void ResetBounds()
    {
        _minX = double.MaxValue; _minY = double.MaxValue;
        _maxX = double.MinValue; _maxY = double.MinValue;
    }

    private void Grow(Rect r)
    {
        if (r.Left < _minX) _minX = r.Left;
        if (r.Top < _minY) _minY = r.Top;
        if (r.Right > _maxX) _maxX = r.Right;
        if (r.Bottom > _maxY) _maxY = r.Bottom;
    }

    private Rect? BoundsRect()
        => _minX > _maxX ? null : new Rect(_minX, _minY, _maxX - _minX, _maxY - _minY);

    public void Dispose()
    {
        DisposeScratch();
        try { _heightStamp?.Dispose(); } catch { }
        _heightStamp = null;
        _colourBrush = null;
    }

    /// <summary>Drops the cached per-stroke brush so the next stroke picks up a
    /// new colour.</summary>
    public void ResetBrushCache() => _colourBrush = null;

    // =======================================================================

    /// <summary>
    /// One-time seed of the Oil preset. Runs independently of the first-run pen
    /// seed, so libraries that already have pens (i.e. every existing user) still
    /// get the brush exactly once.
    /// </summary>
    public static void SeedOilPreset(Library lib)
    {
        if (lib.OilPenSeeded) return;
        lib.OilPenSeeded = true;
        if (lib.Pens.Any(p => p.Pen == PenType.Oil)) return;
        lib.Pens.Add(new PenPreset
        {
            Name = "Oil", Pen = PenType.Oil, Color = "#C1440E", Size = 14f, Sens = 1f,
        });
    }
}
