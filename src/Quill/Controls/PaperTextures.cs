using System.Numerics;
using Quill.Helpers;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// One entry of the page-background picker (the circular swatch row in the
/// floating settings window, §3.1).
///
/// <para><b>Id</b> is what lands in <c>NotePage.Paper</c>. An EMPTY id means "no
/// texture" — the page keeps the plain-colour behaviour, which is why every
/// existing page (whose Paper is null) is untouched by this feature.</para>
/// <para><b>Background</b> is the ground colour the option also stamps onto the
/// page, and it always comes from <see cref="PaperGrain.GroundRgb"/> so the
/// picker, the renderer and the theme derivation can never disagree.</para>
/// </summary>
public sealed record PaperOption(string Id, string Label, string Background, bool CustomColor = false);

/// <summary>
/// The GPU face of the paper system (§8). All the pixel decisions live in
/// <see cref="PaperGrain"/>, which is pure CPU code with no Win2D dependency and
/// is measured offline by <c>tools/PaperProof</c>; this class only uploads what
/// that produces, caches it, and wraps it in a repeating brush.
///
/// <para>Nothing here is ever written to library.json — a page stores the
/// texture's short id and nothing else, exactly like the page-size preset
/// table.</para>
///
/// <para><b>How a texture is cached.</b> Each texture is baked ONCE into a
/// <see cref="PaperGrain.Tile"/>-square <see cref="CanvasBitmap"/> and repeated
/// with a <see cref="CanvasImageBrush"/> whose edge behaviour is
/// <see cref="CanvasEdgeBehavior.Wrap"/>. Nothing is regenerated per frame; this
/// draws behind every stroke on the page. Every generator is seamless at the
/// tile edges by construction — see the class comment on
/// <see cref="PaperGrain"/>.</para>
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

    // =======================================================================
    // Grounds — the contract the theme system derives from
    // =======================================================================

    /// <summary>
    /// The flat base colour of a paper. <b>This is the single input to the theme
    /// derivation (§6)</b> — a texture is a ground plus grain, and only the
    /// ground feeds <c>PageTheme.SetGround</c>. The grain is zero-mean by
    /// construction, so this is also the page's average colour.
    ///
    /// <para>Blueprint (relative luminance 0.199), Brown Paper (0.206) and
    /// Darkprint (0.024) all land below the 0.5 threshold, which is what flips
    /// the whole shell to dark chrome on those pages exactly as §7 requires.</para>
    /// </summary>
    public static Color GroundOf(PaperKind kind)
    {
        var (r, g, b) = PaperGrain.GroundRgb(kind);
        return Color.FromArgb(255, r, g, b);
    }

    /// <summary>The ground as a hex string, for the places that store colours as
    /// text (the picker row, <c>NotePage.Background</c>).</summary>
    public static string GroundHexOf(PaperKind kind) => ColorUtil.ToHex(GroundOf(kind));

    /// <summary>
    /// The OPAQUE colour a page reads as — the one thing the renderer's
    /// <c>ds.Clear</c> and the theme derivation must agree on. Papers with a
    /// ground of their own (Blueprint, Brown Paper, Darkprint, and the
    /// checkerboard) answer with that ground whatever colour happens to be stored
    /// on the page; the white-stock papers and a null/empty Paper — i.e. every
    /// pre-existing page — answer with the page's own background.
    /// </summary>
    public static Color Ground(string? paper, Color background)
    {
        var kind = PaperGrain.FromId(paper);
        if (kind == null) return background;                    // plain colour page
        if (PaperGrain.TintsPageColor(kind.Value)) return background;
        return GroundOf(kind.Value);
    }

    /// <summary>Convenience overload for a page: its effective ground colour.</summary>
    public static Color Ground(Models.NotePage? page) =>
        page == null
            ? Color.FromArgb(255, 0xFF, 0xFF, 0xFF)
            : Ground(page.Paper, ColorUtil.Parse(page.Background));

    /// <summary>True when the texture tints the page's own colour (so the baked
    /// tile has to be cached per background) rather than owning a fixed
    /// ground.</summary>
    public static bool TintsPageColor(string? paper)
    {
        var k = PaperGrain.FromId(paper);
        return k != null && PaperGrain.TintsPageColor(k.Value);
    }

    /// <summary>The picker row, in the §3.1 order. "Custom Color" opens the colour
    /// picker and clears the texture; "Plain White" is the plain-colour default.
    /// Everything after them is a procedural paper, and every ground comes from
    /// <see cref="PaperGrain.GroundRgb"/> rather than a second hard-coded copy.</summary>
    public static readonly PaperOption[] Options = BuildOptions();

    private static PaperOption[] BuildOptions()
    {
        static PaperOption Of(PaperKind k, string label) =>
            new(PaperGrain.IdOf(k), label, ColorUtil.ToHex(GroundOf(k)));

        return new[]
        {
            new PaperOption(Plain, "Custom Color", "#FAF9F5", CustomColor: true),
            Of(PaperKind.PlainWhite,  "Plain White"),
            Of(PaperKind.Transparent, "Transparent"),
            Of(PaperKind.Crumpled,    "Crumpled"),
            Of(PaperKind.Lightweight, "Lightweight"),
            Of(PaperKind.Heavyweight, "Heavyweight"),
            Of(PaperKind.Rippled,     "Rippled"),
            Of(PaperKind.Blueprint,   "Blueprint"),
            Of(PaperKind.BrownPaper,  "Brown Paper"),
            Of(PaperKind.Darkprint,   "Darkprint"),
        };
    }

    // =======================================================================
    // Cache
    // =======================================================================

    /// <summary>Tile edge, in pixels and in world units (baked at 96 DPI so the
    /// two are the same number and the grain is glued to the page).</summary>
    public const float TileSize = PaperGrain.Tile;

    private static readonly object _gate = new();
    private static readonly Dictionary<string, CanvasBitmap> _tiles = new();
    private static readonly Dictionary<string, CanvasImageBrush> _brushes = new();
    private static float _dpi = 96f;

    // The key carries the DEVICE as well as the paper, because the swatch
    // previews render through CanvasImageSource while the page renders through
    // the CanvasVirtualControl. Were the cache keyed on paper alone, two devices
    // would evict each other's tiles and every frame would re-bake.
    // Only the white-stock papers depend on the page colour; the fixed-ground
    // ones bake to exactly one tile each however many pages use them.
    private static string CacheKey(ICanvasResourceCreator rc, PaperKind kind, Color g)
    {
        int dev = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(rc.Device);
        return PaperGrain.TintsPageColor(kind)
            ? $"{dev:X}|{(int)kind}|{g.R:X2}{g.G:X2}{g.B:X2}"
            : $"{dev:X}|{(int)kind}";
    }

    /// <summary>Drops every cached tile and brush. Called on device loss and on a
    /// DPI change (see <see cref="SetDisplayDpi"/>); both orphan the GPU
    /// resources this dictionary is holding, and without the drop the dead
    /// device's tiles stay pinned for the rest of the session while a second set
    /// is baked beside them.</summary>
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

    /// <summary>Records the display DPI the swatch previews should rasterise at,
    /// and drops the cache when it moves. The page tiles themselves are baked at
    /// a fixed 96 DPI on purpose — one tile pixel is one world unit — but the
    /// <see cref="CanvasImageSource"/> previews are XAML-composited and go soft
    /// when they are rasterised at 96 on a 125% or 150% display.</summary>
    public static void SetDisplayDpi(float dpi)
    {
        if (dpi < 48f || dpi > 800f) return;
        lock (_gate) { if (Math.Abs(dpi - _dpi) < 0.5f) return; _dpi = dpi; }
        Invalidate();
    }

    /// <summary>The repeating brush for a paper, or null for "no texture" (the
    /// plain-colour page). Baked on first use and cached thereafter.</summary>
    public static CanvasImageBrush? Brush(ICanvasResourceCreator rc, string? paper, Color ground)
    {
        var kind = PaperGrain.FromId(paper);
        if (kind == null) return null;
        try
        {
            lock (_gate)
            {
                string k = CacheKey(rc, kind.Value, ground);
                if (_brushes.TryGetValue(k, out var cached)) return cached;
                var tile = TileLocked(rc, kind.Value, ground, k);
                if (tile == null) return null;
                var br = new CanvasImageBrush(rc, tile)
                {
                    // SourceRectangle is set EXPLICITLY. Win2D requires a source
                    // rectangle whenever the extend mode is anything but Clamp —
                    // it is what tells the brush the period to repeat over — and
                    // leaving it to be inferred is a documented way to get a
                    // brush that draws nothing at all.
                    SourceRectangle = new Windows.Foundation.Rect(0, 0, TileSize, TileSize),
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
    public static CanvasBitmap? Tile(ICanvasResourceCreator rc, string? paper, Color ground)
    {
        var kind = PaperGrain.FromId(paper);
        if (kind == null) return null;
        try
        {
            lock (_gate) return TileLocked(rc, kind.Value, ground, CacheKey(rc, kind.Value, ground));
        }
        catch { return null; }
    }

    // caller holds _gate
    private static CanvasBitmap? TileLocked(ICanvasResourceCreator rc, PaperKind kind, Color ground, string key)
    {
        if (_tiles.TryGetValue(key, out var hit)) return hit;
        var bmp = Bake(rc, kind, ground);
        if (bmp != null) _tiles[key] = bmp;
        return bmp;
    }

    /// <summary>Synthesises the tile on the CPU and uploads it. 96 DPI so one
    /// tile pixel is one DIP is one WORLD unit: the brush then repeats every
    /// <see cref="TileSize"/> world units and the texture pans and zooms with the
    /// drawing instead of swimming across it.</summary>
    private static CanvasBitmap? Bake(ICanvasResourceCreator rc, PaperKind kind, Color ground)
    {
        try
        {
            // The white-stock papers take the page's colour; the rest own theirs.
            var g = PaperGrain.TintsPageColor(kind) ? ground : GroundOf(kind);
            var bytes = PaperGrain.Bake(kind, g.R, g.G, g.B);
            return CanvasBitmap.CreateFromBytes(
                rc, bytes, PaperGrain.Tile, PaperGrain.Tile,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f, CanvasAlphaMode.Premultiplied);
        }
        catch { return null; }
    }

    // =======================================================================
    // Swatch previews (the circular buttons in the settings window)
    // =======================================================================

    /// <summary>Renders a paper preview as a XAML <see cref="CanvasImageSource"/>,
    /// so a swatch shows the REAL texture rather than a flat colour. A CROP of the
    /// tile is drawn, not the whole tile scaled down, so the grain reads near its
    /// true size; the crop widens for the papers whose character is structural
    /// (Crumpled's folds, Rippled's waves) because those need more than one
    /// feature in frame to be recognisable.</summary>
    public static CanvasImageSource? Preview(string? paper, Color ground, float px)
    {
        var kind = PaperGrain.FromId(paper);
        if (kind == null) return null;
        try
        {
            float dpi;
            lock (_gate) dpi = _dpi;
            var dev = CanvasDevice.GetSharedDevice();
            var src = new CanvasImageSource(dev, px, px, dpi);
            using (var ds = src.CreateDrawingSession(ground))
            {
                var tile = Tile(dev, paper, ground);
                if (tile != null)
                {
                    float crop = Math.Min(PaperGrain.Tile, px * CropFactor(kind.Value));
                    ds.DrawImage(tile,
                        new Windows.Foundation.Rect(0, 0, px, px),
                        new Windows.Foundation.Rect(0, 0, crop, crop));
                }
            }
            return src;
        }
        catch { return null; }
    }

    private static float CropFactor(PaperKind kind) => kind switch
    {
        PaperKind.Crumpled => 4.4f,     // ~2 creases in a 69 DIP circle
        PaperKind.Rippled => 3.6f,      // ~4 ripples
        PaperKind.Transparent => 1.4f,  // ~12 squares, at close to true size
        _ => 2.0f,                      // the grain papers: half size, still legible
    };

    /// <summary>Renders a grid-kind preview the same way, so the grid swatches
    /// show the actual pattern the page will draw.</summary>
    public static CanvasImageSource? GridPreview(Models.GridType grid, Color ground, Color ink, float px)
    {
        try
        {
            float dpi;
            lock (_gate) dpi = _dpi;
            var dev = CanvasDevice.GetSharedDevice();
            var src = new CanvasImageSource(dev, px, px, dpi);
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
