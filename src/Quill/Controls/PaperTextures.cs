using System.Numerics;
using Quill.Helpers;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// One entry of the page-background picker (the circular swatch row in the
/// floating settings window, mirroring the Concepts reference).
///
/// <para><b>Id</b> is what lands in <c>NotePage.Paper</c>. An EMPTY id means "no
/// texture" — the page keeps today's plain-colour behaviour, which is why every
/// existing page (whose Paper is null) is untouched by this feature.</para>
/// <para><b>Background</b> is the plain colour the option also stamps onto the
/// page. For the fixed-ground papers (Blueprint / Darkprint / Brown) it matches
/// <see cref="PaperTextures.Ground"/> so the two can never disagree.</para>
/// </summary>
public sealed record PaperOption(string Id, string Label, string Background, bool CustomColor = false);

/// <summary>
/// Procedural paper textures (§7.3). Everything here is CODE — nothing is ever
/// written to library.json, which is already ~70 MB; a page stores only the
/// texture's short id, exactly like the page-size preset table.
///
/// <para><b>How a texture is made.</b> There is no shader compiler in this
/// toolchain, so every texture is composed from Win2D's BUILT-IN effects:
/// <see cref="TurbulenceEffect"/> (with <c>Tileable = true</c>) for fibre,
/// crumple and ripple noise, <see cref="ColorMatrixEffect"/> to flatten that
/// noise into an opaque grey height-field, <see cref="BlendEffect"/> to lay the
/// height-field over the page's ground through a <see cref="CanvasCommandList"/>,
/// and plain drawn lines for Blueprint / Darkprint / the checkerboard.</para>
///
/// <para><b>How a texture is cached.</b> Each texture is baked ONCE into a
/// 256x256 <see cref="CanvasRenderTarget"/> and repeated with a
/// <see cref="CanvasImageBrush"/> whose edge behaviour is
/// <see cref="CanvasEdgeBehavior.Wrap"/>. Nothing is regenerated per frame —
/// this draws behind every stroke on the page. Every generator is seamless at
/// the tile edges (tileable turbulence, wave periods that divide 256, grid
/// lines drawn at both 0 and 256).</para>
/// </summary>
public static class PaperTextures
{
    // ---- ids (kept as consts so a typo is a compile error, not a blank page) ----
    public const string Plain = "";
    public const string Transparent = "transparent";
    public const string Crumpled = "crumpled";
    public const string Lightweight = "lightweight";
    public const string Heavyweight = "heavyweight";
    public const string Rippled = "rippled";
    public const string Blueprint = "blueprint";
    public const string Brown = "brown";
    public const string Darkprint = "darkprint";

    // Fixed grounds. These are the colours the theme derivation reads, so they
    // are the single source of truth for "is a Blueprint page dark?" (it is).
    public const string BlueprintHex = "#10365E";
    public const string DarkprintHex = "#14161C";
    public const string BrownHex = "#B0824F";

    /// <summary>The picker row, in the reference's order. "Custom Color" opens the
    /// colour picker and clears the texture; "Plain White" is the plain-colour
    /// default. Everything after them is a procedural paper.</summary>
    public static readonly PaperOption[] Options =
    {
        new(Plain,       "Custom Color", "#FAF9F5", CustomColor: true),
        new(Plain,       "Plain White",  "#FFFFFF"),
        new(Transparent, "Transparent",  "#FFFFFF"),
        new(Crumpled,    "Crumpled",     "#F1EEE6"),
        new(Lightweight, "Lightweight",  "#FAF9F5"),
        new(Heavyweight, "Heavyweight",  "#EFEBE1"),
        new(Rippled,     "Rippled",      "#F4F2EB"),
        new(Blueprint,   "Blueprint",    BlueprintHex),
        new(Brown,       "Brown paper",  BrownHex),
        new(Darkprint,   "Darkprint",    DarkprintHex),
    };

    /// <summary>True when the texture tints the page's own colour (so the baked
    /// tile has to be cached per background) rather than owning a fixed ground.</summary>
    public static bool TintsPageColor(string? paper) =>
        paper is Crumpled or Lightweight or Heavyweight or Rippled;

    /// <summary>
    /// The OPAQUE colour a page reads as — the one thing both the renderer's
    /// <c>ds.Clear</c> and the theme derivation must agree on. Papers with a
    /// fixed ground (Blueprint, Darkprint, Brown) answer with that ground no
    /// matter what colour happens to be stored on the page; everything else —
    /// including a null/empty Paper, i.e. every existing page — answers with the
    /// page's own background, which is exactly today's behaviour.
    /// </summary>
    public static Color Ground(string? paper, Color background) => paper switch
    {
        Blueprint => ColorUtil.Parse(BlueprintHex),
        Darkprint => ColorUtil.Parse(DarkprintHex),
        Brown => ColorUtil.Parse(BrownHex),
        // the checkerboard is a light neutral, so a transparent page is a LIGHT page
        Transparent => Color.FromArgb(255, 0xF2, 0xF2, 0xF2),
        _ => background,
    };

    /// <summary>Convenience overload for a page: its effective ground colour.</summary>
    public static Color Ground(Models.NotePage? page) =>
        page == null
            ? Color.FromArgb(255, 0xFF, 0xFF, 0xFF)
            : Ground(page.Paper, ColorUtil.Parse(page.Background));

    // =======================================================================
    // Cache
    // =======================================================================
    public const float TileSize = 256f;

    private static readonly object _gate = new();
    private static readonly Dictionary<string, CanvasRenderTarget> _tiles = new();
    private static readonly Dictionary<string, CanvasImageBrush> _brushes = new();

    // The key carries the DEVICE as well as the paper, because the swatch
    // previews render through CanvasImageSource while the page renders through
    // the CanvasVirtualControl. Were the cache keyed on paper alone, two devices
    // would evict each other's tiles and every frame would re-bake.
    // Only the tinting papers depend on the page colour; the fixed-ground ones
    // bake to exactly one tile each however many pages use them.
    private static string CacheKey(ICanvasResourceCreator rc, string paper, Color g)
    {
        int dev = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(rc.Device);
        return TintsPageColor(paper)
            ? $"{dev:X}|{paper}|{g.R:X2}{g.G:X2}{g.B:X2}"
            : $"{dev:X}|{paper}";
    }

    /// <summary>Drops every cached tile/brush — after a device loss, or when the
    /// app moves to a different Win2D device.</summary>
    public static void Invalidate()
    {
        lock (_gate)
        {
            foreach (var t in _tiles.Values) { try { t.Dispose(); } catch { } }
            foreach (var b in _brushes.Values) { try { b.Dispose(); } catch { } }
            _tiles.Clear();
            _brushes.Clear();
        }
    }

    /// <summary>The repeating brush for a paper, or null for "no texture" (the
    /// plain-colour page). Baked on first use and cached thereafter.</summary>
    public static CanvasImageBrush? Brush(ICanvasResourceCreator rc, string? paper, Color ground)
    {
        if (string.IsNullOrEmpty(paper)) return null;
        try
        {
            lock (_gate)
            {
                string k = CacheKey(rc, paper, ground);
                if (_brushes.TryGetValue(k, out var cached)) return cached;
                var tile = Tile(rc, paper, ground, k);
                if (tile == null) return null;
                var br = new CanvasImageBrush(rc, tile)
                {
                    ExtendX = CanvasEdgeBehavior.Wrap,
                    ExtendY = CanvasEdgeBehavior.Wrap,
                    Interpolation = CanvasImageInterpolation.Linear,
                };
                _brushes[k] = br;
                return br;
            }
        }
        catch { return null; }
    }

    /// <summary>The baked tile itself — used by the settings swatches, which draw
    /// a crop of it inside their circle rather than a scaled-down whole tile.</summary>
    public static CanvasRenderTarget? Tile(ICanvasResourceCreator rc, string? paper, Color ground)
    {
        if (string.IsNullOrEmpty(paper)) return null;
        try
        {
            lock (_gate) return Tile(rc, paper, ground, CacheKey(rc, paper, ground));
        }
        catch { return null; }
    }

    // caller holds _gate
    private static CanvasRenderTarget? Tile(ICanvasResourceCreator rc, string paper, Color ground, string key)
    {
        if (_tiles.TryGetValue(key, out var hit)) return hit;
        var rt = Bake(rc, paper, ground);
        if (rt != null) _tiles[key] = rt;
        return rt;
    }

    // =======================================================================
    // Generators
    // =======================================================================
    private static CanvasRenderTarget? Bake(ICanvasResourceCreator rc, string paper, Color g)
    {
        try
        {
            // 96 DPI so one tile pixel == one DIP == one WORLD unit: the brush
            // then repeats every 256 world units and is glued to the page.
            var rt = new CanvasRenderTarget(rc, TileSize, TileSize, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                ds.Clear(g);
                switch (paper)
                {
                    case Transparent:
                        Checkerboard(ds);
                        break;
                    case Lightweight:
                        Fibre(rc, ds, g, 0.090f, 0.090f, 4, 11, 1.9f, 0.55f, BlendEffectMode.Overlay);
                        break;
                    case Heavyweight:
                        Fibre(rc, ds, g, 0.045f, 0.045f, 5, 23, 2.4f, 0.85f, BlendEffectMode.Overlay);
                        Fibre(rc, ds, g, 0.006f, 0.320f, 3, 41, 2.1f, 0.40f, BlendEffectMode.Overlay);
                        break;
                    case Crumpled:
                        // ridged (Turbulence) noise at a low frequency reads as
                        // creases; a soft blur rounds the folds, and a finer
                        // fractal pass puts the paper grain back on top.
                        Creases(rc, ds, g);
                        Fibre(rc, ds, g, 0.110f, 0.110f, 3, 7, 1.8f, 0.30f, BlendEffectMode.Overlay);
                        break;
                    case Rippled:
                        Ripples(ds);
                        Fibre(rc, ds, g, 0.100f, 0.100f, 3, 5, 1.7f, 0.28f, BlendEffectMode.Overlay);
                        break;
                    case Blueprint:
                        Fibre(rc, ds, g, 0.120f, 0.120f, 3, 13, 1.6f, 0.30f, BlendEffectMode.Overlay);
                        PrintGrid(ds,
                            Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF),   // major
                            Color.FromArgb(0x59, 0xBE, 0xE1, 0xFF));  // minor
                        break;
                    case Darkprint:
                        Fibre(rc, ds, g, 0.120f, 0.120f, 3, 17, 1.6f, 0.35f, BlendEffectMode.Overlay);
                        PrintGrid(ds,
                            Color.FromArgb(0x73, 0xE6, 0xEC, 0xF7),
                            Color.FromArgb(0x33, 0x93, 0xA6, 0xC4));
                        break;
                    case Brown:
                        Fibre(rc, ds, g, 0.050f, 0.050f, 5, 31, 2.3f, 0.80f, BlendEffectMode.Overlay);
                        Fibre(rc, ds, g, 0.004f, 0.280f, 3, 53, 2.0f, 0.45f, BlendEffectMode.Overlay);
                        break;
                }
            }
            return rt;
        }
        catch { return null; }
    }

    // Tileable turbulence flattened to an OPAQUE grey height-field. Working in
    // grey (rather than a translucent overlay) keeps us clear of the premultiplied
    // alpha the turbulence effect emits: BlendEffect does the modulation instead.
    private static ICanvasImage Noise(float fx, float fy, int octaves, int seed,
                                      float contrast, TurbulenceEffectNoise kind)
    {
        var turb = new TurbulenceEffect
        {
            Frequency = new Vector2(fx, fy),
            Octaves = Math.Clamp(octaves, 1, 8),
            Seed = seed,
            Size = new Vector2(TileSize, TileSize),
            Tileable = true,
            Noise = kind,
        };
        return new ColorMatrixEffect
        {
            Source = turb,
            ClampOutput = true,
            ColorMatrix = GreyMatrix(contrast),
        };
    }

    // out.rgb = (lum(in.rgb) - 0.25) * contrast + 0.5 ; out.a = 1.
    // 0.25 is the mean of the turbulence output once premultiplied by its own
    // (also ~0.5-mean) alpha channel, so the height-field lands centred on mid
    // grey — which is the no-op point of an Overlay blend.
    private static Matrix5x4 GreyMatrix(float c)
    {
        float k = c / 3f;
        float o = 0.5f - 0.25f * c;
        return new Matrix5x4
        {
            M11 = k, M12 = k, M13 = k, M14 = 0,
            M21 = k, M22 = k, M23 = k, M24 = 0,
            M31 = k, M32 = k, M33 = k, M34 = 0,
            M41 = 0, M42 = 0, M43 = 0, M44 = 0,
            M51 = o, M52 = o, M53 = o, M54 = 1,
        };
    }

    // Lays a height-field over the ground through a BlendEffect. The ground is a
    // CanvasCommandList rather than the drawing session itself, because a blend
    // needs BOTH operands as images.
    private static void Modulate(ICanvasResourceCreator rc, CanvasDrawingSession ds, Color ground,
                                 ICanvasImage height, float strength, BlendEffectMode mode)
    {
        using var groundList = new CanvasCommandList(rc);
        using (var gds = groundList.CreateDrawingSession())
            gds.FillRectangle(0, 0, TileSize, TileSize, ground);
        using var blend = new BlendEffect { Background = groundList, Foreground = height, Mode = mode };
        var box = new Windows.Foundation.Rect(0, 0, TileSize, TileSize);
        ds.DrawImage(blend, box, box, Math.Clamp(strength, 0f, 1f));
    }

    private static void Fibre(ICanvasResourceCreator rc, CanvasDrawingSession ds, Color ground,
                              float fx, float fy, int octaves, int seed, float contrast,
                              float strength, BlendEffectMode mode)
    {
        using var n = (IDisposable)Noise(fx, fy, octaves, seed, contrast, TurbulenceEffectNoise.FractalSum);
        Modulate(rc, ds, ground, (ICanvasImage)n, strength, mode);
    }

    private static void Creases(ICanvasResourceCreator rc, CanvasDrawingSession ds, Color ground)
    {
        // TurbulenceEffectNoise.Turbulence is the |noise| variant: its creases are
        // sharp ridges rather than smooth hills, which is exactly what a folded
        // sheet looks like. A small blur rounds the fold shoulders.
        using var ridges = (IDisposable)Noise(0.013f, 0.013f, 5, 71, 3.4f, TurbulenceEffectNoise.Turbulence);
        using var soft = new GaussianBlurEffect
        {
            Source = (ICanvasImage)ridges,
            BlurAmount = 1.6f,
            BorderMode = EffectBorderMode.Hard,   // no transparent halo at the tile edge
        };
        Modulate(rc, ds, ground, soft, 0.95f, BlendEffectMode.Overlay);
    }

    // Soft wave relief. Drawn rather than noised so the waves are regular; the
    // period is 2 cycles across the 256 tile and the bands sit on a 32 grid, so
    // both axes wrap seamlessly and no stroke is ever clipped by the tile edge.
    private static void Ripples(CanvasDrawingSession ds)
    {
        const int bands = 8;
        const float step = TileSize / bands;   // 32
        for (int b = 0; b < bands; b++)
        {
            float cy = b * step + step * 0.5f;
            float phase = b * 0.9f;
            for (int pass = 0; pass < 2; pass++)
            {
                float dy = pass == 0 ? -2.4f : 2.4f;
                var col = pass == 0
                    ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x1F, 0x00, 0x00, 0x00);
                var prev = Vector2.Zero;
                for (int x = 0; x <= (int)TileSize; x += 4)
                {
                    float y = cy + dy + 3.6f * MathF.Sin(x / TileSize * MathF.Tau * 2f + phase);
                    var p = new Vector2(x, y);
                    if (x > 0) ds.DrawLine(prev, p, col, 5.5f);
                    prev = p;
                }
            }
        }
    }

    // Blueprint / Darkprint gridlines. Drawn at 0 AND at TileSize so the two
    // half-lines either side of the wrap seam add up to one full line.
    private static void PrintGrid(CanvasDrawingSession ds, Color major, Color minor)
    {
        const float minorStep = 16f, majorStep = 64f;
        for (float v = 0; v <= TileSize; v += minorStep)
        {
            bool maj = v % majorStep == 0f;
            var c = maj ? major : minor;
            float w = maj ? 1.5f : 0.7f;
            ds.DrawLine(v, 0, v, TileSize, c, w);
            ds.DrawLine(0, v, TileSize, v, c, w);
        }
    }

    // The standard transparency checkerboard. 16 squares per axis: an even count,
    // so the pattern is continuous across the wrap.
    private static void Checkerboard(CanvasDrawingSession ds)
    {
        const float s = 16f;
        var light = Color.FromArgb(255, 0xFF, 0xFF, 0xFF);
        var dark = Color.FromArgb(255, 0xCF, 0xCF, 0xCF);
        ds.Clear(light);
        int n = (int)(TileSize / s);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                if (((i + j) & 1) == 1)
                    ds.FillRectangle(i * s, j * s, s, s, dark);
    }

    // =======================================================================
    // Swatch previews (the circular buttons in the settings window)
    // =======================================================================

    /// <summary>Renders a paper preview as a XAML <see cref="CanvasImageSource"/>,
    /// so a swatch can show the REAL texture rather than a flat colour. A CROP of
    /// the tile is drawn, not the whole tile scaled down, so the grain and the
    /// blueprint grid read at their true size.</summary>
    public static CanvasImageSource? Preview(string? paper, Color ground, float px)
    {
        try
        {
            var dev = CanvasDevice.GetSharedDevice();
            var src = new CanvasImageSource(dev, px, px, 96);
            using (var ds = src.CreateDrawingSession(ground))
            {
                var tile = Tile(dev, paper, ground);
                if (tile != null)
                {
                    float crop = Math.Min(TileSize, px * 2.2f);
                    ds.DrawImage(tile,
                        new Windows.Foundation.Rect(0, 0, px, px),
                        new Windows.Foundation.Rect(0, 0, crop, crop));
                }
            }
            return src;
        }
        catch { return null; }
    }

    /// <summary>Renders a grid-kind preview the same way, so the grid swatches
    /// show the actual pattern the page will draw.</summary>
    public static CanvasImageSource? GridPreview(Models.GridType grid, Color ground, Color ink, float px)
    {
        try
        {
            var dev = CanvasDevice.GetSharedDevice();
            var src = new CanvasImageSource(dev, px, px, 96);
            using (var ds = src.CreateDrawingSession(ground))
            {
                float sp = px / 4.5f;
                float lw = 1f;
                switch (grid)
                {
                    case Models.GridType.Dotted:
                        for (float y = sp * 0.5f; y < px; y += sp)
                            for (float x = sp * 0.5f; x < px; x += sp)
                                ds.FillCircle(new Vector2(x, y), 1.3f, ink);
                        break;
                    case Models.GridType.Square:
                        for (float v = sp * 0.5f; v < px; v += sp)
                        {
                            ds.DrawLine(v, 0, v, px, ink, lw);
                            ds.DrawLine(0, v, px, v, ink, lw);
                        }
                        break;
                    case Models.GridType.Lines:
                        for (float y = sp * 0.5f; y < px; y += sp)
                            ds.DrawLine(0, y, px, y, ink, lw);
                        break;
                    case Models.GridType.Isometric:
                        PreviewFamily(ds, px, 30f, sp * 0.87f, ink, lw);
                        PreviewFamily(ds, px, 90f, sp * 0.87f, ink, lw);
                        PreviewFamily(ds, px, 150f, sp * 0.87f, ink, lw);
                        break;
                    case Models.GridType.Triangle:
                        PreviewFamily(ds, px, 0f, sp * 0.87f, ink, lw);
                        PreviewFamily(ds, px, 60f, sp * 0.87f, ink, lw);
                        PreviewFamily(ds, px, 120f, sp * 0.87f, ink, lw);
                        break;
                }
            }
            return src;
        }
        catch { return null; }
    }

    private static void PreviewFamily(CanvasDrawingSession ds, float px, float angleDeg,
                                      float perp, Color ink, float lw)
    {
        float a = angleDeg * MathF.PI / 180f;
        var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
        var nrm = new Vector2(-dir.Y, dir.X);
        float span = px * 1.5f;
        for (float k = -span; k <= span; k += perp)
            ds.DrawLine(nrm * k + dir * -span + new Vector2(px / 2, px / 2),
                        nrm * k + dir * span + new Vector2(px / 2, px / 2), ink, lw);
    }
}
