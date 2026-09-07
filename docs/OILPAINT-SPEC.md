# OILPAINT-SPEC — oil painting inside the ordinary note surface

Status: design only, no product code. Supersedes nothing; extends
`CONCEPTS-DIRECTION.md` §4.3 (Tier 2 raster) by making it concrete, page-keyed,
and shippable **without** the layer system that does not exist yet.

There is **no Art Mode.** `ArtSurface` was cancelled and reverted. Oil paint is a
`PenType` on `InkSurface`, chosen from the existing pen-preset flyout, painting
onto a raster buffer that `DrawRegion` composites between shapes and ink.

Impasto — thick paint that catches light — is the headline and ships in Phase 1.

---

## 0. The three anchors this design is built on

1. **`InkSurface.DrawRegion` (cs:3090)** hands each tile a session whose
   `ds.Transform` is **already translated** by the `CanvasVirtualControl`. The
   code composes (`scale * translate * regionT`) rather than clobbering, because
   clobbering caused the invisible-ink bug (#inkfix2). **Paint draws in world
   coordinates inside that same composed transform.** That single fact is the
   entire answer to "how does the raster survive pan and zoom": it doesn't need
   to know about pan or zoom at all.
2. **`LiquidGlass.cs` (branch `liquid-glass` / `visual-verify`,
   `src/Quill/Helpers/LiquidGlass.cs`)** already ships height-map lighting with
   `DistantSpecularEffect` over a height field carried in the **alpha channel**
   (cs:199-207, and the comment at cs:298 — "the lighting effects read height
   from ALPHA"). Impasto uses the identical technique. **Nothing in this spec
   requires a custom `.hlsl` or `PixelShaderEffect`.** There is no shader
   compiler in this environment and the built-in lighting effects make one
   unnecessary.
3. **`library.json` is 53 MB and re-serialised on the UI thread every 1.5 s**
   (`LibraryStore.Save`, cs:342-395), and `LibraryStore.Dir` is routinely a
   OneDrive folder. Pixels therefore live **outside** both. `NotePage` gains
   exactly **one** additive bool.

---

## 1. Raster paint surface

### 1.1 Tiles, not one render target

One `CanvasRenderTarget` per page is impossible: the canvas is infinite and
`NotePage.PageSize` defaults to `Infinite`. Sparse world-aligned tiles.

```
Tile (tx, ty) covers world rect  [tx*512, ty*512, 512, 512]
1 texel = 1 world unit = 1 DIP = 1/96 inch   (matches NotePage.UnitsPerInch = 96)
```

Three GPU resources plus one CPU array per live tile:

| resource | Win2D construction | bytes |
|---|---|---|
| **colour** | `new CanvasRenderTarget(device, 512, 512, 96f, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied)` | 1.00 MiB |
| **height** | `new CanvasRenderTarget(device, 512, 512, 96f, DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied)` | 2.00 MiB |
| **lit cache** | `new CanvasRenderTarget(device, 512, 512, 96f, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied)` | 1.00 MiB |
| **CPU proxy** | `byte[]` 64×64 BGRA (1/8 res) for wet pickup | 16 KiB |

**Use the explicit-DPI 6-arg overload, never the `ICanvasResourceCreatorWithDpi`
one.** With an explicit 96f a "512 DIP" tile is exactly 512 pixels on every
monitor; with the inherited-DPI overload a 150 %-scaling monitor silently
allocates 768², `1 texel = 1 world unit` breaks, and dragging the window to a
100 % monitor fires `DpiChanged` and asks you to **resample the user's
painting.** This is a load-bearing rule, not a nicety.

**Colour is premultiplied.** Non-negotiable: it is what makes source-over
associative and what prevents dark fringes on every scale and rotate. Straight
alpha exists only at the file boundary, and this design has no file boundary
that uses it (§4).

**Height lives in the alpha channel of a 16-bit float target**, stored as grey
`(h, h, h, h)` — a legal premultiplied value (white paint at coverage `h`).
`h ∈ [0, 1]` normalised, where `1.0 = HMax = 6.0 world units` of paint
thickness. 16F rather than 8-bit because **8-bit height terraces visibly in the
specular highlight** — the derivative amplifies quantisation. `A8UIntNormalized`
(256 KiB/tile) is a ready fallback if VRAM ever bites, but ship 16F.

### 1.2 Where it composites in `DrawRegion`

Exact insertion point, `InkSurface.cs`:

```
3105  ds.Clear(bg);
3107  ds.Transform = CreateScale(ViewZoom) * CreateTranslation(ViewOffset) * regionT;
3111  DrawGrid / DrawPerspective / DrawArtboard / DrawPageTitle
3122  foreach (var sh in _page.Shapes) { ... }        // includes image shapes
      ────────── ▼ PAINT GOES HERE ▼ ──────────
      DrawPaint(ds, sender, visMinX, visMinY, visMaxX, visMaxY);
      ─────────────────────────────────────────
3143  cacheEligible / TryDrawInkCache / per-stroke loop
3204  live wet stroke
3218  selection chrome, lasso, handles …
```

**Paint sits above shapes and images, below all vector ink.** Rationale: notes
are annotations *over* a painting and must stay legible; and it leaves the
2500-stroke ink cache (`InkCacheThreshold`, cs:79) completely untouched —
`TryDrawInkCache` caches strokes only, and strokes still draw after paint.

`DrawPaint` body, in full:

```csharp
// ds.Transform is ALREADY world-space here. Do not touch it.
int t0x = (int)Math.Floor(visMinX / 512), t1x = (int)Math.Floor(visMaxX / 512);
int t0y = (int)Math.Floor(visMinY / 512), t1y = (int)Math.Floor(visMaxY / 512);
var interp = ViewZoom < 0.9f ? CanvasImageInterpolation.MultiSampleLinear
                             : CanvasImageInterpolation.Linear;
for (int ty = t0y; ty <= t1y; ty++)
for (int tx = t0x; tx <= t1x; tx++)
{
    if (!_paint.TryGet(tx, ty, out var tile)) continue;   // sparse: unallocated = nothing
    var lit = tile.EnsureLit(sender);                     // §3.3, cached
    ds.DrawImage(lit, new Rect(tx * 512.0, ty * 512.0, 512, 512),
                      new Rect(0, 0, 512, 512), 1f, interp);
}
```

That is the whole pan/zoom story. `regionT` is composed, never overwritten; the
paint layer knows only world coordinates.

### 1.3 In-progress stroke scratch buffer

A stroke that crosses itself must not double-composite. The scratch buffer is
the standard fix and it is **the same tile grid**, allocated per gesture:

```
_scratch : Dictionary<(int,int), (CanvasRenderTarget colour, CanvasRenderTarget height)>
```
same formats, same 512² size, cleared to transparent on allocation.

- Dabs draw into the scratch, **not** the canvas.
- Colour dabs use `CanvasBlend.SourceOver` at a **linearised per-dab alpha**
  `αdab = 1 − (1 − αtarget)^(1 / dabsPerPixel)` (libmypaint's
  `opaque_linearize`) so changing spacing does not change apparent darkness.
- Height dabs use `CanvasBlend.Add`.
- `DrawRegion` draws `canvas tile → then scratch tile` for touched tiles while
  the gesture is live, so the wet stroke is visible immediately.
- On pen-up the scratch composites onto the canvas **exactly once** at the
  stroke's opacity, height added and clamped to 1.0 by a
  `ColorMatrixEffect { ClampOutput = true }`.

Transient cost: 3 MiB per touched tile. If one gesture touches **> 12 tiles**,
flush the scratch to canvas mid-gesture and start a fresh segment (a self-cross
across that seam double-composites; rare enough to accept, cheap enough to fix
later).

### 1.4 Memory arithmetic — Letter page, fully painted

```
Letter @ UnitsPerInch 96 : 8.5 × 96 = 816 w.u.  ×  11 × 96 = 1056 w.u.
tiles                    : ceil(816/512)=2  ×  ceil(1056/512)=3  =  6 tiles

per tile   colour 1.00 MiB + height 2.00 MiB + lit 1.00 MiB + proxy 0.016 MiB
         = 4.02 MiB
page       6 × 4.02                                        =  24.1 MiB resident
peak       + scratch (6 × 3.00 MiB) during one long stroke  =  42.1 MiB
```

Disk, per tile: 1.00 MiB colour + 0.50 MiB height (U16) raw = 1.5 MiB,
Deflate-compressed to typically **250–500 KiB** for real painted content
(unpainted regions are runs of zero and collapse). Fully painted Letter page
≈ **1.5–3 MB**. Sparse pages cost only their allocated tiles.

Resident-tile hard cap **64 tiles (≈ 256 MiB)** for a big infinite-canvas
painting; beyond that evict the tiles furthest from the viewport back to their
compressed CPU blob, which already exists (§4.3).

### 1.5 Device-lost recovery (free)

`CanvasVirtualControl` raises `CreateResources` with `DeviceLost`; every tile is
a GPU resource and evaporates. The recovery source is the compressed CPU blob
that §4.3 already produces at every stroke commit for undo. **CPU authority is
therefore free** — it is the undo "After" blob. Cost of producing it:
`GetPixelBytes()` of 1.5 MiB per touched tile at **pen-up only** (~3–8 ms), not
per frame. One readback buys undo, crash-safety, and device-lost recovery.

---

## 2. The oil brush

### 2.1 Dab engine

Strokes are not filled outlines. Each pointer segment becomes N stamped dabs.

- **Spacing is a fraction of DIAMETER** (the Photoshop / Procreate / CSP mental
  model). Default **0.08**, user range 0.02–0.25.
- `step = max(0.5f, spacing * diameter)` — the 0.5 world-unit floor bounds the
  dab count when radius → 0.
- **Partial-dab carry** — ~8 lines, invisible in screenshots, and the whole
  difference between a smooth stroke and a beaded one. Carried across pointer
  events for the life of the gesture:

```csharp
float travel = Vector2.Distance(pLast, pNew);
_dabAccum += travel;
int emitted = 0;
while (_dabAccum >= step && emitted < 256)         // 256 = anti-hang clamp
{
    _dabAccum -= step;
    float u = 1f - (_dabAccum / travel);           // position along THIS segment
    EmitDab(Vector2.Lerp(pLast, pNew, u),
            MathF.Lerp(prLast, prNew, u),
            tangent: Vector2.Normalize(pNew - pLast));
    emitted++;
}
// _dabAccum persists to the next pointer event — this is the carry
```

- Input is `e.GetIntermediatePoints(_canvas)` — already used at cs:1621; without
  it most of the pen's native report rate is lost.
- Phase 1 interpolates linearly. Phase 4 upgrades the spine to **centripetal
  Catmull-Rom (α = 0.5)** over a 4-sample sliding window, walked by arc length.

### 2.2 Dab footprint — a bristle brush, no shaders

Each dab is **7 bristle sub-dabs**, not one blob:

- Base ellipse: width = diameter, height = `diameter * aspect`, `aspect =
  lerp(1.0, 0.55, tilt)`, rotated to the stroke tangent.
- The 7 bristles are ellipses of `0.22 * diameter`, spread evenly across the
  perpendicular axis over `[-0.45d, +0.45d]`, each jittered by a **per-stroke
  fixed random seed** — fixed, so the bristles track consistently down the
  stroke. That consistency is what reads as *a brush* rather than as noise.
- Falloff comes from a `CanvasRadialGradientBrush` per bristle (hard core to
  transparent rim), `FillEllipse`d. Zero custom shader code.
- Per-bristle alpha weight `w_i` from a fixed cosine profile, so the brush is
  denser in the middle.

### 2.3 Paint load and depletion

A reservoir, following Procreate's Charge and Corel Painter's reservoir model:

```
load = 1.0 at pen-down (a new stroke always reloads the brush)
capacity C = lerp(40, 600, PaintLoad)      dabs, PaintLoad ∈ [0,1]
per dab:   deposit = flow * load
           heightDeposit = load * Impasto * αdab * 0.06
           load = max(0, load - 1/C)
```

At `load = 0` the brush deposits **no fresh pigment and no height** — only
pickup colour, which is exactly dry-brush drag: broken, streaky marks that let
the layer underneath show through. A stroke acquires a beginning and an end.

### 2.4 Wet-on-wet pickup and smear

Sampling the canvas per dab via GPU readback would stall every frame. Instead:

- Each tile carries a **CPU colour proxy** at 1/8 resolution (64×64 BGRA,
  16 KiB). Refreshed by `GetPixelBytes` once at stroke start; **within** a
  stroke it is updated in C# by the same dab maths, so wet-on-wet works within
  one stroke too. 1/8 res is the right resolution — pickup is a low-frequency
  phenomenon.
- One-pole pickup memory (libmypaint's `smudge_length`):

```
sampled = proxy.SampleBilinear(dabCentre)
pickup  = pickup * Wetness + sampled * (1 - Wetness)     // Wetness ∈ [0, 0.95]
dabColour = PigmentMix(brushColour, pickup, Wetness * (1 - load))
```

High `Wetness` gives long drag streaks; as the brush empties, canvas colour
takes over. Both knobs are on the preset flyout.

### 2.5 Pigment mixing — licence-clean

**Explicitly NOT Mixbox.** Mixbox (Sochorová & Jamriška) is CC BY-NC — a
non-commercial licence. It does not ship here.

The licence-clean path is the **weighted geometric mean (WGM)** subtractive
model — Scott Allen Burns, *Subtractive Color Mixture Computation*
(arXiv:1710.06364), as shipped in MyPaint (`doc/spectral/spectral.md`).
Published method, open implementations, no NC restriction.

```
Mix(a, b, t):
  Ra = ToReflectance10(a)        // sRGB → 10 spectral bands, memoised
  Rb = ToReflectance10(b)
  R  = Ra^(1-t) * Rb^t           // per band; clamp bands to [1e-4, 1]
  return BandsToSrgb(R)          // fixed 10×3 matrix
```

- **10 bands, not 36.** 8–12 is visually adequate (what IMPaSTo did for
  interactive rates); 36 floats per colour cannot live in a texture.
- **CPU-only, two colours per dab.** 20 `MathF.Pow` per dab × ~100 dabs/frame
  ≈ 2 000 pow/frame — nothing. **Do not attempt per-pixel spectral over the
  canvas in Win2D.**
- `ToReflectance10` memoised in a `Dictionary<uint, float[]>` capped at 4096
  entries; a stroke uses a handful of distinct colours. No multi-megabyte LUT is
  shipped.
- Gated by `PaintPigmentMix` (default **on** for `PenType.Oil` only). The
  fallback "Light" path is a plain RGB lerp, one branch away.

This is the difference between blue + yellow = **green** and blue + yellow =
mud. It is the second-biggest visual payoff in the whole feature.

---

## 3. Impasto — the headline

### 3.1 Height accumulation

`h` is normalised `[0, 1]`, `1.0 = HMax = 6.0 world units` of paint.

```
per dab:  h += load * Impasto * αdab * 0.06     (CanvasBlend.Add into the height scratch)
at commit: h_canvas = clamp(h_canvas + h_scratch, 0, 1)
```

`0.06` per dab means one ordinary pass (~120 dabs, but overlapping so ~12 dabs
of depth at any pixel) reaches ≈ 0.2 ≈ 1.2 world units — a visible ridge that
still has headroom for four or five more passes before it caps. **The cap is
enforced by `ColorMatrixEffect { ClampOutput = true }` at commit**, not by CPU
arithmetic — Direct2D does not clamp at FLOAT precision by default and relying
on it is a classic source of white speckle.

### 3.2 Lighting — one sun for the whole app

Shared constants with `LiquidGlass` so Quill has exactly one light direction:

```
Azimuth   = 2.3561945f   //  135°, upper-LEFT   (LiquidGlass uses Math.PI * 0.75)
Elevation = 0.5235988f   //   30°, raking       (LiquidGlass uses Math.PI * 0.16 ≈ 29°)
```

Low elevation is deliberate: the light **rakes across** the ridges instead of
flooding the face. That is what makes thickness read.

### 3.3 The lit-tile effect graph

Built per tile, rendered into the cached `lit` render target, re-rendered only
when the tile's pixels change or the light moves. Steady-state frame cost is
therefore **one `DrawImage` per visible tile** — the same class of cost as the
existing ink cache.

```csharp
var diff = new DistantDiffuseEffect {
    Source = tile.Height, Azimuth = Az, Elevation = El,
    HeightMapScale = 6f,          // = HMax in world units, at 1 texel/world unit
    DiffuseAmount = 1f, LightColor = Colors.White };

var spec = new DistantSpecularEffect {
    Source = tile.Height, Azimuth = Az, Elevation = El,
    HeightMapScale = 6f,
    SpecularExponent = 12f,       // broader than glass's 22 — wet oil, not glass
    SpecularAmount = 0.55f, LightColor = Colors.White };

// ambient lift so unlit paint is not black: rgb' = Ka + (1-Ka)*rgb, Ka = 0.45
var ambient = new ColorMatrixEffect { Source = diff, ColorMatrix = AmbientLift(0.45f) };

var body = new BlendEffect { Mode = BlendEffectMode.Multiply,
                             Background = tile.Colour, Foreground = ambient };
var lit  = new BlendEffect { Mode = BlendEffectMode.Screen,
                             Background = body, Foreground = spec };

// The lighting effects emit alpha = 1, so `lit` is opaque. Restore the paint's
// OWN alpha exactly once, at the end — this is why the chain is correct.
var masked = new CompositeEffect { Mode = CanvasComposite.DestinationIn,
                                   Sources = { lit, tile.Colour } };

var final = new ColorMatrixEffect { Source = masked,
                                    ColorMatrix = Identity, ClampOutput = true };
final.BufferPrecision = CanvasBufferPrecision.Precision16Float;   // no banding
```

Why the alpha is restored at the end rather than masking each lighting term:
masking both terms first and then blending gives `α + α(1−α)` on soft edges
(0.5 → 0.75), inflating them. Running the chain opaque and applying
`DestinationIn` once yields **exactly** the paint's own alpha. The RGB in the
1-px partial-alpha rim is very slightly light-biased; that is the correct trade.

`HeightMapScale = 6f` because height is stored normalised and the effect's pixel
spacing is 1 world unit — the scale converts normalised height into the same
units as the spacing so the reconstructed normal is physically right.

### 3.4 Deferred impasto refinements (not v1)

- **Sideways displacement** (a bow wave ahead of the stroke, ridges at the
  edges) is what separates *thick* from *embossed*. Phase 4+.
- **Canvas weave**: `NotePage.Paper` folds into the same height field as a
  static tiled base at amplitude 0.03, so bare paper catches the same sun.
  Phase 4. If paper is only overlaid at the end instead, the result looks like a
  photo of paper with a painting pasted on it.

---

## 4. Storage

### 4.1 Location — local only, never synced, never inline

```
%LOCALAPPDATA%\Quill\paint\{sha256(LibraryStore.Dir)[..16]}\{pageId:N}\
    meta.json
    {tx}_{ty}.qtile
```

- **Keyed by page id**, not layer id — there is no layer system.
- The `sha256(LibraryStore.Dir)` segment keys the cache to *which library* this
  machine is pointing at, so switching data folders does not cross-contaminate.
- **Not `LibraryStore.Dir`**: that is routinely OneDrive/Dropbox. A mutable,
  debounced tile writer there generates continuous cloud re-upload churn, and two
  devices painting the same tile produce "conflicted copy" files the app never
  reads — silent, permanent stroke loss.
- **Not `library.json`**: 53 MB re-serialised on the UI thread every 1.5 s.
- Consequence, accepted by the user: **paint is this-device-only, with a visible
  badge.** Cross-device raster needs content-addressed, immutable, tombstoned
  asset sync — a hard prerequisite, not an open question.

`meta.json`: `{ "v":1, "tile":512, "hMax":6.0, "tiles":[[0,0],[1,0],…] }`

`.qtile` format: `"QTIL"` magic, `byte version`, `byte codec (0 = deflate)`,
`int colourLen`, `int heightLen`, then `deflate(BGRA premultiplied, 1 MiB)` and
`deflate(U16 height, 512 KiB)`. No PNG anywhere: PNG is straight alpha, and
premultiply/unpremultiply round-tripping every session compounds precision loss
at low alpha (at α = 8/255 only ~3 bits of colour survive). Raw + Deflate needs
only `System.IO.Compression`, is lossless, and tags its codec per block so a
future codec change costs no format version bump.

### 4.2 Crash-safe write protocol — NEVER in place

This user has already lost three paintings to in-place writes. This is the
highest-consequence component in the feature. The protocol mirrors
`LibraryStore.Save` (cs:373-391), the pattern this codebase already trusts:

```csharp
var tmp = final + ".tmp";
using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                               FileShare.None, 64 * 1024, FileOptions.WriteThrough))
{
    fs.Write(payload, 0, payload.Length);
    fs.Flush(flushToDisk: true);      // to the PLATTER, not the OS cache
}
if (File.Exists(final)) File.Replace(tmp, final, final + ".bak");  // atomic + rotates a backup
else                    File.Move(tmp, final);
```

- **`meta.json` is written LAST**, same protocol. A crash mid-flush therefore
  leaves a `meta` that names only tiles which fully landed; a half-written
  `.tmp` is inert and is deleted on next open.
- A stale `.tmp` is never left behind (it contains user pixels).
- Failures append to `paint.crashlog` beside the tiles — same idiom as
  `DrawRegion`'s self-healing catch (cs:3016-3028).
- `File.WriteAllBytes` over a live tile is **forbidden**. Say so in a comment at
  the call site.

### 4.3 Write triggers and threading

- Dirty tiles only.
- Debounced **2000 ms** after the last stroke commit; plus on page switch; plus
  on window close; plus a **30 s heartbeat** while anything is dirty.
- Runs on a background `Task` behind a `SemaphoreSlim(1,1)` so two flushes never
  interleave. Never on the UI thread — a 512² deflate is 10–20 ms.
- The bytes being written are the **same blob** already produced at stroke
  commit for undo. One readback, three jobs.

### 4.4 Non-blocking load on page open

`SetPage` (cs:565) starts `Task.Run(LoadPaintAsync)` and returns immediately:

1. Worker reads `meta.json`, then each `.qtile`, inflating on the worker thread.
2. Per tile, marshal to the UI thread via `DispatcherQueue.TryEnqueue` to create
   the `CanvasRenderTarget` and `SetPixelBytes` (Win2D resource creation stays on
   the render thread for the shared device).
3. Each landed tile calls `InvalidateScreenRect` for its own world rect.

The page therefore shows ink instantly and paint fades in tile by tile. Opening
a page is never blocked by paint. A cancellation token is tripped on the next
`SetPage` so switching pages fast never stacks loads.

`NotePage` gains **exactly one** additive field:

```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
public bool HasPaint { get; set; }
```

`WhenWritingDefault` is mandatory, not decorative: without it every page writes
`"HasPaint":false` into a 53 MB file and changes every page's FNV hash, forcing
a `SyncLog` op for every page on the first save after upgrade.

---

## 5. UI — inside the existing pen-preset idiom

No new panel, no new mode, no new surface.

- **`PenType.Oil` is APPENDED** to the enum (`NoteModels.cs:5-9`). Appended
  only — `PenType` serialises as its integer, so inserting a member would
  silently repaint every existing stroke in the library. (Same rule the file
  already states for `GridType`.)
- **`PenPreset` gains four additive fields**, all
  `[JsonIgnore(WhenWritingDefault)]` so every existing preset costs 0 bytes:

```csharp
public float PaintLoad { get; set; } = 0.5f;   // reservoir capacity, 40…600 dabs
public float Wetness   { get; set; } = 0.4f;   // pickup memory / smear length
public float Impasto   { get; set; } = 0.6f;   // height per dab
public float Spacing   { get; set; } = 0.08f;  // fraction of diameter
```

- They render as **four sliders inside `CreatePresetFlyout`**
  (`MainWindow.xaml.cs:2280`), shown only when `p.Pen == PenType.Oil`. The oil
  brush is an ordinary chip: assignable to pen-row/wheel slots, editable by the
  existing flyout, persisted in `Library.Pens`.
- One seeded preset behind a one-time `Library.OilPenSeeded` bool (mirroring the
  `PaintPensSeeded` idiom):
  `{ Name = "Oil", Pen = PenType.Oil, Color = "#C1440E", Size = 14f, Sens = 1f }`.
- **Badge**: a "On this device only" chip beside the page title whenever
  `HasPaint` is true. The user has approved local-only raster with a badge.

> ⚠ **`MainWindow.xaml.cs` is being edited by another agent right now.** The
> flyout change is ~40 lines confined to `CreatePresetFlyout` plus ~6 lines in
> the seed list at cs:2028. Sequence it after their work or hand them the diff —
> do not merge blind. `InkSurface.cs` is 7 035 lines and also shared; the
> `DrawRegion` insertion is 12 lines at one site.

---

## 6. Integration

### 6.1 Undo

```csharp
public class PaintTilesAction : IPageAction
{
    public bool TouchesText => false;
    private readonly List<(int tx, int ty, byte[]? Before, byte[]? After)> _tiles;
    public string Description => "Paint stroke";
    public void Do(NotePage page)   => Apply(useAfter: true);
    public void Undo(NotePage page) => Apply(useAfter: false);
    public Rect? AffectedBounds(NotePage page) => _strokeBounds;   // undo flash
}
```

- Granularity: the **tiles the stroke touched**, captured before the scratch
  commits. `null` Before = tile created; `null` After = tile erased empty.
- Size: ~150–400 KiB per tile, so a typical 2-tile stroke is 0.3–0.8 MB.
- Pushed via `PushAction(action, _page, alreadyDone: true)` — the pixels are
  already on the canvas when the action is created.
- **Budget: running paint-undo total capped at 128 MB.** Overflow trims from the
  **bottom** of the stack — needs a new `UndoManager.TrimBottom`; today's stack
  is unbounded. **Vector undo stays unlimited** and is never trimmed.

### 6.2 Spatial index — bypass, confirmed

`SpatialGrid` (cs:2201-2406) indexes `PenStroke` and `ShapeElement` by identity.
Paint has no identity — it is pixels. **`EnsureGrid` is untouched, and no paint
data enters the index.** Consequences, each handled explicitly:

- **Lasso / rect select**: paint is not selectable in v1. Lasso passes over it
  and a tooltip says so. Selection *move* therefore does not move paint.
- **Eraser**: must not silently do nothing. When the Eraser tool passes over a
  page with paint it **also erases paint**, stamping `DestinationOut` dabs into
  the colour and height tiles along the erase path. Vector and raster erase in
  one gesture, wrapped in one `CompositeAction(RemoveStrokesAction,
  PaintTilesAction)`. The eraser is already a dab loop; this is cheap and it is
  the honest behaviour.
- **FreeSpace / insert-space** offsets strokes below a Y line; paint cannot
  follow. v1 shows a one-time 1.2 s toast: *"Paint doesn't move with
  insert-space."* Flagged as a real limitation, not hidden.

### 6.3 Export — yes, paint appears everywhere

The light direction is fixed, so exports look exactly like the screen.

| target | change |
|---|---|
| **Thumbnail** `RenderPageThumbnail` (cs:6834) | composite the lit tiles after the shape loop, before the stroke loop, under the existing thumbnail transform |
| **PDF** `BuildVectorPageAsync` (cs:5770) | render the union of painted tiles into one `CanvasRenderTarget` at export scale (long edge clamped to 4096, matching the ink-cache clamp at cs:6991), `GetPixelBytes()` → one extra `PdfVectorImage(x, y, w, h, pixW, pixH, bgra)` appended to `images` |
| **SVG / HTML** `HtmlSvgExporter` | the same buffer as an `<image>` with a base64 data URI, after shape images, before stroke paths |
| **PNG export** | same composite |

⚠ **Verify** that `PdfExporter.CreateVector` emits its `Images` before its
`Paths` in the content stream (it appears to, cs:285 then the path loop) — PDF
draws in stream order, and paint must land under the ink.

---

## 7. Phased plan — impasto first, each phase independently shippable

### Phase 1 — Height and light (M, ≈ 2–2.5 weeks)

Tile store (colour + height + lit cache), dab engine with partial-dab carry,
scratch buffer, `DrawRegion` composite, the `DistantSpecular` +
`DistantDiffuse` chain, atomic tile persistence, `PaintTilesAction` undo,
`PenType.Oil` + one seeded preset. **No** pickup, **no** pigment mix, **no**
depletion — one solid colour with thickness.

**On screen:** pick the Oil pen and drag. A thick band of colour appears that
catches a raking light from the upper-left; press harder and the ridge grows
taller and the highlight sharpens. Pan, zoom, undo, restart the app — it is all
still there, in the right place. This is the whole "wow" and it ships alone.

**Build gate:** paint a stroke, kill the process mid-stroke, relaunch — the last
committed stroke survives and nothing else is corrupted.

### Phase 2 — Load, depletion, bristles (S, ≈ 4–5 days)

Reservoir + per-dab depletion, the 7-bristle footprint, dry-brush breakup at
`load → 0`, the `PaintLoad` / `Impasto` / `Spacing` sliders in the preset flyout.

**On screen:** a long stroke starts fat and creamy and thins into broken, streaky
bristle marks that let the layer underneath show through. A stroke now has a
beginning and an end instead of being a uniform ribbon.

### Phase 3 — Wet-on-wet and pigment (M, ≈ 1.5–2 weeks)

CPU colour proxy, pickup buffer with smudge-length memory, 10-band WGM
subtractive mixing with its memoised reflectance table, the `Wetness` slider.

**On screen:** drag a yellow stroke through a still-wet blue one and it turns
**green**, dragging streaks of blue along with it. This is the second "wow" and
the one people screenshot.

### Phase 4 — Erase, export, badge, polish (M, ≈ 1.5 weeks)

Paint-aware eraser, thumbnail/PDF/SVG/PNG export, the "on this device only"
badge, canvas-weave folded into the height field, centripetal Catmull-Rom
resampling, the honest limitation toasts, `UndoManager.TrimBottom`.

**On screen:** the painting shows up in the gallery card and in an exported PDF;
the eraser rubs paint away and the highlight goes with it; a small badge on the
page says the paint lives on this machine only.

### Honest total

**≈ 5–7 weeks** of focused single-developer work for all four phases. Phase 1
alone is ≈ 2–2.5 weeks and is genuinely, independently shippable — that is the
point of ordering it first. This is materially cheaper than the "1–2 months"
CONCEPTS-DIRECTION §4.3 estimated for general raster, because this design
deliberately drops layers, blend modes, COW tile sharing, VRAM LRU across pages,
and cross-page tile caching. Those are the parts that make raster expensive, and
none of them are needed to make paint look like paint.

---

## 8. Top three risks

1. **The effect graph misbehaves on real hardware.** The lit chain is six nodes
   and Direct2D's clamping and precision rules are per-format and famously
   inconsistent — many built-in effects (`ColorMatrix`, `Composite`, the lighting
   effects) emit out-of-range values in *unpremultiplied* space even from
   in-range inputs. The specific failure modes to expect: a bright or dark halo
   around every paint edge, or terracing in the specular highlight.
   *Mitigation:* build the graph in Phase 1 against a single hard-coded height
   tile and eyeball it **before** any brush maths exists; keep the terminal
   `ColorMatrixEffect { ClampOutput = true }` and `Precision16Float`; keep the
   `A8UIntNormalized` height fallback ready to swap in.

2. **Per-frame cost of relighting during a live stroke.** Re-running six effect
   nodes over 1–6 tiles per pointer batch could drop the stroke below pen report
   rate, which is the one thing a pen-first app cannot ship.
   *Mitigation is designed in* — cached lit tiles, only dirty tiles re-render,
   and invalidation limited to the screen rect around the fresh segment (the
   existing `#cvc` pattern at cs:1650). But it must be **measured on this user's
   actual machine** before Phase 1 is called done, with a debug overlay showing
   dab count and tile re-render count. Most "this brush feels wrong" reports
   trace to spacing or to missing invalidation, not to the falloff curve.

3. **Data loss.** Three of this user's paintings have already been destroyed by
   in-place writes. The tile writer is the single highest-consequence component
   here, and unlike ink there is no vector fallback to reconstruct from.
   *Mitigation:* temp file + `Flush(flushToDisk: true)` + `File.Replace` with a
   rotating `.bak`, `meta.json` written last, `paint.crashlog`, and a Phase-1
   acceptance gate that explicitly includes killing the process mid-stroke.

*Honourable mention:* `MainWindow.xaml.cs` and `InkSurface.cs` are both being
edited concurrently by other sessions. Both touch points here are small and
localised, but they must be sequenced, not merged blind.

---

## 9. The single most important thing to get right first

**The tile store, the `DrawRegion` composite, and the atomic writer — in that
order, before a single line of brush maths.**

Concretely: get `ds.DrawImage(litTile, tileWorldRect, …)` composing correctly
with the pre-translated `regionT` at every pan and zoom, landing between shapes
and ink, and surviving a kill -9. Everything expressive in this spec — bristles,
depletion, pickup, pigment mixing, even the lighting itself — is a per-dab or
per-tile computation bolted onto that substrate, and every one of them can be
added, tuned, or replaced later. **None of them can be retrofitted onto a
substrate that puts pixels in the wrong world position, composites in the wrong
order, or loses them on a crash.**

The invisible-ink bug (#inkfix2) is the precedent: it was a transform
composition error, it made content *silently vanish*, and it cost real debugging
time. Paint is a bigger version of the same surface with no vector fallback.
Prove the substrate with a single hard-coded tile of flat red before writing
`EmitDab`.
