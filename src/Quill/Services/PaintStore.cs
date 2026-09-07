using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Dispatching;
using Quill.Models;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Quill.Services;

/// <summary>
/// One 512x512 world-aligned raster paint tile (OILPAINT-SPEC §1.1). Tile
/// (tx, ty) covers world rect [tx*512, ty*512, 512, 512] and 1 texel is exactly
/// 1 world unit, so the paint layer never has to know about pan or zoom.
/// </summary>
public sealed class PaintTile : IDisposable
{
    public int Tx { get; }
    public int Ty { get; }

    /// <summary>Premultiplied BGRA pigment. Premultiplied is non-negotiable: it
    /// is what makes source-over associative and what keeps dark fringes out of
    /// every scale and rotate.</summary>
    public CanvasRenderTarget Colour { get; }

    /// <summary>Paint thickness, carried in the ALPHA channel as grey
    /// (h, h, h, h) — a legal premultiplied value. 16F rather than 8-bit
    /// because 8-bit height terraces visibly in the specular highlight.</summary>
    public CanvasRenderTarget Height { get; }

    /// <summary>Set whenever pixels change; cleared once the bytes reach disk.</summary>
    internal bool Dirty;

    // Lit cache (§3.3): the six-node lighting graph is re-rendered only when the
    // tile's pixels change, so steady state is one DrawImage per visible tile —
    // the same class of cost as the existing ink cache.
    private CanvasRenderTarget? _lit;
    private bool _litDirty = true;

    /// <summary>Call after any change to Colour or Height.</summary>
    public void InvalidateLit() { _litDirty = true; Dirty = true; }

    public Rect WorldRect => new(Tx * (double)PaintTileStore.TileSize,
                                 Ty * (double)PaintTileStore.TileSize,
                                 PaintTileStore.TileSize, PaintTileStore.TileSize);

    internal PaintTile(ICanvasResourceCreator rc, int tx, int ty)
    {
        Tx = tx;
        Ty = ty;
        // The explicit-DPI 6-arg overload, never the DPI-inheriting one: with an
        // inherited 150% DPI a "512 DIP" tile silently allocates 768 pixels,
        // 1 texel = 1 world unit breaks, and dragging the window to a 100%
        // monitor asks to resample the user's painting.
        Colour = new CanvasRenderTarget(rc, PaintTileStore.TileSize, PaintTileStore.TileSize, 96f,
                                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                                        CanvasAlphaMode.Premultiplied);
        Height = new CanvasRenderTarget(rc, PaintTileStore.TileSize, PaintTileStore.TileSize, 96f,
                                        DirectXPixelFormat.R16G16B16A16Float,
                                        CanvasAlphaMode.Premultiplied);
        var clear = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        using (var ds = Colour.CreateDrawingSession()) ds.Clear(clear);
        using (var ds = Height.CreateDrawingSession()) ds.Clear(clear);
    }

    /// <summary>
    /// The impasto lighting graph (§3.3), rendered into the cached lit tile.
    /// One sun for the whole app, shared with LiquidGlass: 135° upper-left at a
    /// 30° raking elevation, because low elevation is what makes thickness read.
    /// </summary>
    public ICanvasImage EnsureLit(ICanvasResourceCreator rc)
    {
        if (_lit != null && !_litDirty) return _lit;
        _lit ??= new CanvasRenderTarget(rc, PaintTileStore.TileSize, PaintTileStore.TileSize, 96f,
                                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                                        CanvasAlphaMode.Premultiplied);
        try
        {
            using var diff = new DistantDiffuseEffect
            {
                Source = Height,
                Azimuth = PaintTileStore.LightAzimuth,
                Elevation = PaintTileStore.LightElevation,
                HeightMapScale = PaintTileStore.HMax,   // normalised height -> world units, 1 texel = 1 w.u.
                DiffuseAmount = 1f,
                LightColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };
            using var spec = new DistantSpecularEffect
            {
                Source = Height,
                Azimuth = PaintTileStore.LightAzimuth,
                Elevation = PaintTileStore.LightElevation,
                HeightMapScale = PaintTileStore.HMax,
                SpecularExponent = 12f,     // broader than glass's 22 — wet oil, not glass
                SpecularAmount = 0.55f,
                LightColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };
            using var ambient = new ColorMatrixEffect { Source = diff, ColorMatrix = AmbientLift(0.45f) };
            using var body = new BlendEffect { Mode = BlendEffectMode.Multiply, Background = Colour, Foreground = ambient };
            using var lit = new BlendEffect { Mode = BlendEffectMode.Screen, Background = body, Foreground = spec };
            // The lighting effects emit alpha = 1, so the chain runs OPAQUE and the
            // paint's own alpha is restored exactly once at the end. Masking each
            // term first would give a + a(1-a) on soft edges (0.5 -> 0.75) and
            // visibly inflate them.
            using var masked = new CompositeEffect { Mode = CanvasComposite.DestinationIn };
            masked.Sources.Add(lit);
            masked.Sources.Add(Colour);
            using var final = new ColorMatrixEffect
            {
                Source = masked,
                ColorMatrix = Identity,
                ClampOutput = true,          // Direct2D does NOT clamp at FLOAT precision
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };
            using var ds = _lit.CreateDrawingSession();
            ds.Blend = CanvasBlend.Copy;
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawImage(final);
            _litDirty = false;
        }
        catch
        {
            // never let a lighting failure blank the paint — fall back to flat colour
            return Colour;
        }
        return _lit;
    }

    private static readonly Matrix5x4 Identity = new()
    {
        M11 = 1f, M22 = 1f, M33 = 1f, M44 = 1f,
    };

    /// <summary>rgb' = Ka + (1-Ka)*rgb, so unlit paint is ambient-lifted rather
    /// than black. Alpha is passed through untouched.</summary>
    private static Matrix5x4 AmbientLift(float ka) => new()
    {
        M11 = 1f - ka, M22 = 1f - ka, M33 = 1f - ka, M44 = 1f,
        M51 = ka, M52 = ka, M53 = ka,
    };

    public void Dispose()
    {
        try { Colour.Dispose(); } catch { }
        try { Height.Dispose(); } catch { }
        try { _lit?.Dispose(); } catch { }
    }
}

/// <summary>
/// Sparse per-page tile store plus its crash-safe writer/loader
/// (OILPAINT-SPEC §1.1, §4). Pixels live in %LOCALAPPDATA%, never in
/// library.json (53 MB, re-serialised on the UI thread every 1.5 s) and never
/// in LibraryStore.Dir (routinely OneDrive, where a mutable debounced tile
/// writer produces "conflicted copy" files the app never reads).
/// </summary>
public sealed class PaintTileStore : IDisposable
{
    public const int TileSize = PaintTileCodec.TileSize;
    public const float HMax = 6.0f;          // world units of paint at h = 1.0

    // One sun for the whole app — the SAME azimuth LiquidGlass passes to the
    // same Win2D DistantSpecular effect over the same alpha-carried height
    // field, so paint impasto and glass chrome never disagree about the light.
    // NB: the spec labels 135° "upper-left", but a controlled dome probe shows
    // Win2D's lighting azimuth is measured in image space (Y down), so 135°
    // actually rakes from the LOWER-left — and LiquidGlass, feeding the identical
    // convention, renders the same way. Consistency is the spec's overriding
    // rule ("exactly one light direction"), so the shared value is kept as-is; a
    // true upper-left highlight would be azimuth ~3.927f (225°) applied to BOTH
    // features together, which is a cross-cutting change beyond this phase.
    public const float LightAzimuth = 2.3561945f;    // matches LiquidGlass (Math.PI * 0.75)
    public const float LightElevation = 0.5235988f;  //  30°, deliberately low so the light rakes

    private readonly Dictionary<(int, int), PaintTile> _tiles = new();
    private CanvasRenderTarget? _eraserStamp;
    private readonly DispatcherQueue? _ui;
    private readonly DispatcherQueueTimer? _saveTimer;
    private readonly DispatcherQueueTimer? _heartbeat;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task _lastWrite = Task.CompletedTask;
    private bool _disposed;

    public Guid PageId { get; }
    public string PageDir { get; }
    public int TileCount => _tiles.Count;

    /// <summary>GPU bytes held by the resident tiles: 1 MiB colour + 2 MiB
    /// height each.</summary>
    public long ResidentBytes =>
        (long)_tiles.Count * (PaintTileCodec.ColourBytes + PaintTileCodec.HeightGpuBytes);

    /// <summary>Raised on the UI thread as each loaded tile lands, so the caller
    /// can invalidate just that tile's world rect.</summary>
    public event Action<Rect>? TileLoaded;

    public PaintTileStore(Guid pageId)
    {
        PageId = pageId;
        PageDir = Path.Combine(LibraryRoot, pageId.ToString("N"));
        _ui = DispatcherQueue.GetForCurrentThread();
        if (_ui != null)
        {
            // §4.3: debounced 2 s after the last commit, plus a 30 s heartbeat
            // while anything is still dirty.
            _saveTimer = _ui.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(2000);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => BeginFlush();

            _heartbeat = _ui.CreateTimer();
            _heartbeat.Interval = TimeSpan.FromSeconds(30);
            _heartbeat.IsRepeating = true;
            _heartbeat.Tick += (_, _) => BeginFlush();
        }
    }

    // =======================================================================
    // Paths
    // =======================================================================

    /// <summary>%LOCALAPPDATA%\Quill\paint\{sha256(LibraryStore.Dir)[..16]}\ —
    /// the hash keys the cache to WHICH library this machine points at, so
    /// switching data folders cannot cross-contaminate pages that share an id.</summary>
    public static string LibraryRoot
    {
        get
        {
            string key;
            try
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(LibraryStore.Dir.ToLowerInvariant()));
                key = Convert.ToHexString(bytes)[..16].ToLowerInvariant();
            }
            catch { key = "default"; }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Quill", "paint", key);
        }
    }

    private static string MetaName => "meta.json";

    // =======================================================================
    // Tile access
    // =======================================================================

    public bool TryGet(int tx, int ty, out PaintTile tile) => _tiles.TryGetValue((tx, ty), out tile!);

    public PaintTile GetOrCreate(ICanvasResourceCreator rc, int tx, int ty)
    {
        if (_tiles.TryGetValue((tx, ty), out var t)) return t;
        t = new PaintTile(rc, tx, ty);
        _tiles[(tx, ty)] = t;
        return t;
    }

    /// <summary>Drop a tile that undo has returned to its pre-existence state
    /// (a stroke that first allocated it is being undone). The tile's GPU
    /// resources are released; a later redo simply re-creates it.</summary>
    public bool RemoveTile(int tx, int ty)
    {
        if (_tiles.Remove((tx, ty), out var t))
        {
            t.Dispose();
            MarkDirty();
            return true;
        }
        return false;
    }

    /// <summary>Nudge the debounced writer after a change made outside the paint
    /// choke point (an undo/redo restoring tile bytes), so restored pixels reach
    /// disk on the same schedule as a fresh stroke.</summary>
    public void ScheduleSave() => MarkDirty();

    public static int TileIndex(double world) => (int)Math.Floor(world / TileSize);

    /// <summary>
    /// The single choke point for putting pixels on the canvas. Runs
    /// <paramref name="paint"/> once per tile overlapping <paramref name="worldRect"/>,
    /// handing it a session whose transform maps WORLD coordinates into that
    /// tile — so callers only ever think in world units and a mark that
    /// straddles a tile seam lands seamlessly on both sides.
    /// </summary>
    public void PaintWorld(ICanvasResourceCreator rc, Rect worldRect,
                           Action<CanvasDrawingSession, PaintTile> paint)
        => ForEachTile(rc, worldRect, height: false, paint);

    /// <summary>Height companion to <see cref="PaintWorld"/>; same tile-local
    /// world transform, targeting the 16F height field.</summary>
    public void PaintWorldHeight(ICanvasResourceCreator rc, Rect worldRect,
                                 Action<CanvasDrawingSession, PaintTile> paint)
        => ForEachTile(rc, worldRect, height: true, paint);

    private void ForEachTile(ICanvasResourceCreator rc, Rect worldRect, bool height,
                             Action<CanvasDrawingSession, PaintTile> paint)
    {
        int t0x = TileIndex(worldRect.Left), t1x = TileIndex(worldRect.Right - 0.001);
        int t0y = TileIndex(worldRect.Top), t1y = TileIndex(worldRect.Bottom - 0.001);
        for (int ty = t0y; ty <= t1y; ty++)
            for (int tx = t0x; tx <= t1x; tx++)
            {
                var tile = GetOrCreate(rc, tx, ty);
                var target = height ? tile.Height : tile.Colour;
                using (var ds = target.CreateDrawingSession())
                {
                    ds.Transform = System.Numerics.Matrix3x2.CreateTranslation(
                        -tx * (float)TileSize, -ty * (float)TileSize);
                    paint(ds, tile);
                }
                tile.Dirty = true;
            }
        MarkDirty();
    }

    // =======================================================================
    // Erase (OILPAINT-SPEC §6.2) — the point eraser rubs paint away too, so a
    // pass over painted tiles cuts DestinationOut dabs into BOTH colour and
    // height. Removing height as well as colour is deliberate: the impasto
    // highlight has to go with the pigment or a ghost ridge is left catching the
    // light. Only tiles that already exist are touched — erasing empty canvas
    // must allocate nothing.
    // =======================================================================

    /// <summary>
    /// Cut an eraser capsule [<paramref name="a"/> -> <paramref name="b"/>] of
    /// the given world radius out of every resident tile it crosses. <paramref
    /// name="beforeTouch"/> fires once per tile just before it is modified, so the
    /// caller can snapshot the tile for undo. Returns the tiles actually touched.
    /// </summary>
    public List<(int Tx, int Ty)> EraseSegment(ICanvasResourceCreator rc, System.Numerics.Vector2 a,
                                               System.Numerics.Vector2 b, float radius,
                                               Action<PaintTile>? beforeTouch)
    {
        var touched = new List<(int, int)>();
        double minX = Math.Min(a.X, b.X) - radius, minY = Math.Min(a.Y, b.Y) - radius;
        double maxX = Math.Max(a.X, b.X) + radius, maxY = Math.Max(a.Y, b.Y) + radius;
        int t0x = TileIndex(minX), t1x = TileIndex(maxX - 0.001);
        int t0y = TileIndex(minY), t1y = TileIndex(maxY - 0.001);
        var stamp = EnsureEraserStamp(rc);
        for (int ty = t0y; ty <= t1y; ty++)
            for (int tx = t0x; tx <= t1x; tx++)
            {
                if (!_tiles.TryGetValue((tx, ty), out var tile)) continue;   // never create on erase
                beforeTouch?.Invoke(tile);
                var toTile = System.Numerics.Matrix3x2.CreateTranslation(
                    -tx * (float)TileSize, -ty * (float)TileSize);
                StampErase(tile.Colour, stamp, toTile, a, b, radius);
                StampErase(tile.Height, stamp, toTile, a, b, radius);
                tile.InvalidateLit();
                touched.Add((tx, ty));
            }
        if (touched.Count > 0) MarkDirty();
        return touched;
    }

    // DestinationOut is a CanvasComposite mode, not a CanvasBlend, so the cut is
    // a DrawImage of an opaque disc stamp rather than a FillCircle: dst *= (1 -
    // stampAlpha). Overlapping dabs stay idempotent, so snapshotting a tile once
    // per gesture is exact however many times the pen crosses it.
    private static void StampErase(CanvasRenderTarget target, CanvasRenderTarget stamp,
                                   System.Numerics.Matrix3x2 toTile,
                                   System.Numerics.Vector2 a, System.Numerics.Vector2 b, float radius)
    {
        using var ds = target.CreateDrawingSession();
        ds.Transform = toTile;
        var src = new Rect(0, 0, stamp.Size.Width, stamp.Size.Height);
        float dist = System.Numerics.Vector2.Distance(a, b);
        float step = Math.Max(1f, radius * 0.5f);
        int n = (int)(dist / step);
        for (int i = 0; i <= n; i++)
        {
            var c = System.Numerics.Vector2.Lerp(a, b, n == 0 ? 0f : (float)i / n);
            var dst = new Rect(c.X - radius, c.Y - radius, radius * 2, radius * 2);
            ds.DrawImage(stamp, dst, src, 1f, CanvasImageInterpolation.Linear, CanvasComposite.DestinationOut);
        }
    }

    private const int EraserStampPx = 64;

    private CanvasRenderTarget EnsureEraserStamp(ICanvasResourceCreator rc)
    {
        if (_eraserStamp != null) return _eraserStamp;
        _eraserStamp = new CanvasRenderTarget(rc, EraserStampPx, EraserStampPx, 96f,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        // A premultiplied white disc: hard core to ~0.85 of the radius, then a
        // smoothstep rim so the erased edge is not a jagged bite. In premultiplied
        // BGRA an alpha of A means colour channels are also A (white * A).
        var px = new byte[EraserStampPx * EraserStampPx * 4];
        float half = EraserStampPx * 0.5f;
        for (int y = 0; y < EraserStampPx; y++)
            for (int x = 0; x < EraserStampPx; x++)
            {
                float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float a = d >= 1f ? 0f : d <= 0.85f ? 1f
                        : 1f - Smooth((d - 0.85f) / 0.15f);
                byte A = (byte)Math.Clamp(a * 255f, 0f, 255f);
                int i = (y * EraserStampPx + x) * 4;
                px[i] = A; px[i + 1] = A; px[i + 2] = A; px[i + 3] = A;
            }
        _eraserStamp.SetPixelBytes(px);
        return _eraserStamp;
    }

    private static float Smooth(float u) => u * u * (3f - 2f * u);

    // =======================================================================
    // Write scheduling (§4.3)
    // =======================================================================

    private void MarkDirty()
    {
        if (_disposed) return;
        _saveTimer?.Start();
        if (_heartbeat != null && !_heartbeat.IsRunning) _heartbeat.Start();
    }

    public bool HasDirtyTiles
    {
        get { foreach (var t in _tiles.Values) if (t.Dirty) return true; return false; }
    }

    /// <summary>UI thread: read back the dirty tiles and hand the bytes to a
    /// background writer. The readback has to happen here — the tiles are GPU
    /// resources on the shared device — but a 512² deflate never runs on the UI
    /// thread.</summary>
    public void BeginFlush()
    {
        if (_disposed) return;
        _saveTimer?.Stop();
        var snaps = new List<PaintTileCodec.TileBlob>();
        foreach (var t in _tiles.Values)
        {
            if (!t.Dirty) continue;
            try
            {
                snaps.Add(new PaintTileCodec.TileBlob(
                    t.Tx, t.Ty, t.Colour.GetPixelBytes(),
                    PaintTileCodec.PackHeight(t.Height.GetPixelBytes())));
                t.Dirty = false;
            }
            catch (Exception ex) { Log(PageDir, $"readback {t.Tx},{t.Ty} failed: {ex.Message}"); }
        }
        if (_heartbeat != null && !HasDirtyTiles) _heartbeat.Stop();
        if (snaps.Count == 0) return;

        var all = _tiles.Keys.ToList();
        string dir = PageDir;
        _lastWrite = Task.Run(async () =>
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);   // two flushes never interleave
            try { WriteBlobs(dir, snaps, all); }
            catch (Exception ex) { Log(dir, $"flush failed: {ex.Message}"); }
            finally { _writeGate.Release(); }
        });
    }

    /// <summary>Page switch / app close: start a flush and block briefly so a
    /// fire-and-forget write cannot be lost with the process.</summary>
    public void FlushBlocking(int millis = 4000)
    {
        try
        {
            BeginFlush();
            _lastWrite.Wait(millis);
        }
        catch { }
    }

    private static void WriteBlobs(string dir, List<PaintTileCodec.TileBlob> snaps, List<(int, int)> all)
    {
        Directory.CreateDirectory(dir);
        var landed = new HashSet<(int, int)>();
        foreach (var b in snaps)
        {
            try
            {
                PaintTileCodec.WriteAtomic(Path.Combine(dir, $"{b.Tx}_{b.Ty}.qtile"), PaintTileCodec.Encode(b));
                landed.Add((b.Tx, b.Ty));
            }
            catch (Exception ex)
            {
                Log(dir, $"tile {b.Tx},{b.Ty} write failed: {ex.Message}");
                // a stale .tmp holds user pixels; never leave one behind
                try { File.Delete(Path.Combine(dir, $"{b.Tx}_{b.Ty}.qtile.tmp")); } catch { }
            }
        }

        // meta.json LAST, same protocol: a crash mid-flush therefore leaves a
        // meta that names only tiles which fully landed.
        try
        {
            foreach (var k in all)
                if (File.Exists(Path.Combine(dir, $"{k.Item1}_{k.Item2}.qtile"))) landed.Add(k);
            var meta = new PaintMeta
            {
                V = 1,
                Tile = TileSize,
                HMax = HMax,
                Tiles = landed.Select(k => new[] { k.Item1, k.Item2 }).ToList()
            };
            PaintTileCodec.WriteAtomic(Path.Combine(dir, MetaName),
                                       Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta)));
        }
        catch (Exception ex) { Log(dir, $"meta write failed: {ex.Message}"); }
    }

    private sealed class PaintMeta
    {
        [JsonPropertyName("v")] public int V { get; set; } = 1;
        [JsonPropertyName("tile")] public int Tile { get; set; } = TileSize;
        [JsonPropertyName("hMax")] public double HMax { get; set; } = PaintTileStore.HMax;
        [JsonPropertyName("tiles")] public List<int[]> Tiles { get; set; } = new();
    }

    // =======================================================================
    // Non-blocking load (§4.4)
    // =======================================================================

    /// <summary>
    /// Returns immediately. The worker inflates off the UI thread and marshals
    /// each finished tile back to create its GPU resources, so a page shows ink
    /// instantly and paint fades in tile by tile.
    /// </summary>
    public void BeginLoad(ICanvasResourceCreator rc)
    {
        var token = _cts.Token;
        string dir = PageDir;
        _ = Task.Run(() =>
        {
            List<int[]> want;
            try
            {
                if (!File.Exists(Path.Combine(dir, MetaName))) return;
                // a half-written .tmp is inert and contains user pixels; clear it
                foreach (var stale in Directory.GetFiles(dir, "*.tmp"))
                    try { File.Delete(stale); } catch { }
                var meta = JsonSerializer.Deserialize<PaintMeta>(File.ReadAllText(Path.Combine(dir, MetaName)));
                if (meta?.Tiles == null || meta.Tile != TileSize) return;
                want = meta.Tiles;
            }
            catch (Exception ex) { Log(dir, $"meta read failed: {ex.Message}"); return; }

            foreach (var k in want)
            {
                if (token.IsCancellationRequested) return;
                if (k.Length < 2) continue;
                int tx = k[0], ty = k[1];
                byte[] colour, heightGpu;
                try
                {
                    var (c, h) = PaintTileCodec.ReadTile(dir, tx, ty, out bool usedBak);
                    if (usedBak) Log(dir, $"tile {tx},{ty} recovered from .bak");
                    colour = c;
                    heightGpu = PaintTileCodec.UnpackHeight(h);
                }
                catch (Exception ex)
                {
                    // one unreadable tile must never take the page down with it
                    Log(dir, $"tile {tx},{ty} unreadable: {ex.Message}");
                    continue;
                }

                _ui?.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || _disposed) return;
                    try
                    {
                        var tile = GetOrCreate(rc, tx, ty);
                        tile.Colour.SetPixelBytes(colour);
                        tile.Height.SetPixelBytes(heightGpu);
                        tile.Dirty = false;          // just came off disk
                        TileLoaded?.Invoke(tile.WorldRect);
                    }
                    catch (Exception ex) { Log(dir, $"tile {tx},{ty} upload failed: {ex.Message}"); }
                });
            }
        }, token);
    }

    /// <summary>True when this page has tiles on disk, without touching the GPU.</summary>
    public static bool HasStoredPaint(Guid pageId)
    {
        try { return File.Exists(Path.Combine(LibraryRoot, pageId.ToString("N"), MetaName)); }
        catch { return false; }
    }

    // =======================================================================

    private static void Log(string dir, string msg)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "paint.crashlog"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}" + Environment.NewLine);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _saveTimer?.Stop(); } catch { }
        try { _heartbeat?.Stop(); } catch { }
        foreach (var t in _tiles.Values) t.Dispose();
        _tiles.Clear();
        try { _eraserStamp?.Dispose(); } catch { }
        _eraserStamp = null;
        try { _cts.Dispose(); } catch { }
    }
}

/// <summary>
/// One touched tile's before/after state for a paint undo step (OILPAINT-SPEC
/// §6.1). Colour and height are the RAW GPU bytes, deflated losslessly (§ codec
/// CompressRaw). <see cref="BeforeExisted"/> is false when the stroke was the
/// first to allocate the tile, so undo removes it again; a null After pair means
/// the tile no longer exists in the after-state.
/// </summary>
public sealed record PaintTileCapture(
    int Tx, int Ty,
    bool BeforeExisted, byte[]? BeforeColourZ, byte[]? BeforeHeightZ,
    byte[]? AfterColourZ, byte[]? AfterHeightZ);

/// <summary>
/// A tile-granular raster paint undo step (OILPAINT-SPEC §6.1). It slots into the
/// SAME undo stack as vector strokes, so Ctrl+Z / the dial's undo button removes
/// the most recent edit whether it was ink or paint. The pixels are already on
/// the canvas when this is built, so it is pushed with alreadyDone:true; Undo
/// restores the before-blobs and re-lights, Redo restores the after-blobs.
/// </summary>
public sealed class PaintTilesAction : IPageAction, IPaintUndo
{
    // Paint is pixels, not text, so undoing it must not tear down the text layer.
    public bool TouchesText => false;
    public string Description { get; }

    private readonly PaintTileStore _store;
    private readonly ICanvasResourceCreator _rc;
    private readonly List<PaintTileCapture> _tiles;
    private readonly Rect? _bounds;

    public PaintTilesAction(PaintTileStore store, ICanvasResourceCreator rc,
                            List<PaintTileCapture> tiles, Rect? bounds, string description = "Paint stroke")
    {
        _store = store;
        _rc = rc;
        _tiles = tiles;
        _bounds = bounds;
        Description = description;
    }

    /// <summary>The undo weight §6.1 caps: every before- and after-blob this step
    /// is holding.</summary>
    public long PaintBytes
    {
        get
        {
            long t = 0;
            foreach (var c in _tiles)
            {
                t += (c.BeforeColourZ?.Length ?? 0) + (c.BeforeHeightZ?.Length ?? 0);
                t += (c.AfterColourZ?.Length ?? 0) + (c.AfterHeightZ?.Length ?? 0);
            }
            return t;
        }
    }

    public void Do(NotePage page) => Apply(useAfter: true);
    public void Undo(NotePage page) => Apply(useAfter: false);
    public Rect? AffectedBounds(NotePage page) => _bounds;

    private void Apply(bool useAfter)
    {
        foreach (var c in _tiles)
        {
            var cz = useAfter ? c.AfterColourZ : c.BeforeColourZ;
            var hz = useAfter ? c.AfterHeightZ : c.BeforeHeightZ;
            bool exists = useAfter ? c.AfterColourZ != null : c.BeforeExisted;
            if (!exists)
            {
                // The tile did not exist in this direction's state — return it to
                // nothing (a redo may re-create it from GetOrCreate later).
                _store.RemoveTile(c.Tx, c.Ty);
                continue;
            }
            var tile = _store.GetOrCreate(_rc, c.Tx, c.Ty);
            try
            {
                if (cz != null)
                    tile.Colour.SetPixelBytes(PaintTileCodec.DecompressRaw(cz, PaintTileCodec.ColourBytes));
                if (hz != null)
                    tile.Height.SetPixelBytes(PaintTileCodec.DecompressRaw(hz, PaintTileCodec.HeightGpuBytes));
                tile.InvalidateLit();   // sets Dirty, so the restored pixels re-light and re-persist
            }
            catch { /* one bad tile must not take the whole undo down */ }
        }
        _store.ScheduleSave();
    }
}
