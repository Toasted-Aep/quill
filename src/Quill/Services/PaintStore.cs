using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
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

    public void Dispose()
    {
        try { Colour.Dispose(); } catch { }
        try { Height.Dispose(); } catch { }
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

    private readonly Dictionary<(int, int), PaintTile> _tiles = new();
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
        try { _cts.Dispose(); } catch { }
    }
}
