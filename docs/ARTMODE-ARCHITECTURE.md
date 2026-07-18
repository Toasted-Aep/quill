# Art Mode — Architecture (Phase B)

Status: B1 design document, for user review before implementation.
Scope: raster/pigment paint simulation inside Quill, surfaced as a new
"Art Notebook" gallery type. Companion to docs/ORCHESTRATION-STATE.md (Phase B).

Guiding constraints (binding, from user decisions):
- Lives INSIDE the existing app: new `ArtSurface` control + Art Notebook type.
  `InkSurface.cs` is NOT modified structurally; the page host swaps controls.
- AI features are cloud APIs; keys in Windows Credential Locker exactly like
  `Services/AiService.cs` (`PasswordVault`, resource string per provider).
- Raster data never goes into `library.json` (LibraryStore serialises the whole
  Library on every debounced save — art pages store only metadata + a relative
  `.artq` path there).

---

## 1. ArtSurface control (tiled raster grid)

New file: `src/Quill/Controls/ArtSurface.cs` (a `UserControl` hosting a
`CanvasVirtualControl`, mirroring `InkSurface`'s view maths: `ViewOffset`,
`ViewZoom`, `ScreenToWorld`, `RegionsInvalidated` partial redraw).

### Tile model
- Tile size: **256 × 256 texels**, fixed. Canvas of W×H px → `ceil(W/256) ×
  ceil(H/256)` tiles, allocated lazily on first touch (blank tiles cost nothing).
- Per tile, three GPU surfaces + optional CPU mirrors:

| Map      | Format                         | Meaning |
|----------|--------------------------------|---------|
| Pigment  | `CanvasRenderTarget` R16G16B16A16Float | Premultiplied linear colour of the *paint layer* (substrate drawn beneath at composite time). Alpha = coverage. |
| Wetness  | `CanvasRenderTarget` R16Float  | 0 = bone dry … 1 = fully wet. Drives bleed radius, mixing pickup and the static-wet rule. |
| Height   | `CanvasRenderTarget` R16Float  | Accumulated paint thickness (impasto). Fed to the lighting shader. |

- A fourth transient target per *dirty* tile (`_scratch`) is used for
  ping-pong passes (diffusion, drying) and is pooled, not per-tile.

### Key classes
```
ArtTile        { int Tx, Ty; CanvasRenderTarget Pigment, Wetness, Height;
                 bool Dirty; long LastTouchedTicks; }
TileGrid       { Dictionary<(int,int), ArtTile>; ArtTile GetOrCreate(tx,ty);
                 IEnumerable<ArtTile> InRect(Rect world); }
ArtSurface     { TileGrid Tiles; ArtDocument Doc; ArtBrushEngine Engine;
                 ArtUndoManager Undo; view/zoom/pan; input routing; }
ArtDocument    { CanvasSpec Spec; SubstrateKind Substrate; List<ArtStroke> Strokes;
                 MediumPreset ActiveMedium; float DrynessBias; bool RealTimeDrying; }
```
New files: `Controls/ArtSurface.cs`, `Art/TileGrid.cs`, `Art/ArtDocument.cs`.
(New folder `src/Quill/Art/` for the engine; controls stay in `Controls/`.)

### Draw loop
`RegionsInvalidated` → for each region: draw substrate texture (tiled,
pre-generated, §2) → for each intersecting ArtTile draw Pigment via the
impasto lighting effect chain (§2) → overlays (brush cursor ring, stroke
hover highlight, selection). Physics passes (§3) run on a `CompositionTarget
.Rendering` tick only while strokes are wet AND real-time drying is on;
otherwise on stroke-end only. Only dirty tiles are processed and invalidated.

Integration points: `MainWindow` page-host panel (where `InkSurface` is
instantiated) gains a branch: if the current notebook is an Art Notebook,
construct/reuse an `ArtSurface` instead. Both expose a small shared interface
`ICanvasHost { Undo(); Redo(); Refresh(); ContentChanged; }` (new file
`Controls/ICanvasHost.cs`) so top-bar wiring stays common.

---

## 2. GPU pipeline — substrates & impasto lighting

Win2D has no compute shaders; everything runs as `PixelShaderEffect` (HLSL
compiled to `.bin` at build via CompileShader/D2DPixelShader tooling — add a
csproj target) or built-in effect graphs.

### Substrate textures
Four procedural substrates, generated once per (kind, DPI) into a 512×512
wrapping `CanvasRenderTarget` and tiled:
- **Canvas weave** — two crossed sine/noise warp threads, per-thread lighting.
- **Belgian linen** — finer, irregular slub noise on the weave generator.
- **Wood panel** — 1-D ridged Perlin along grain + subtle plank banding.
- **Rough watercolor** — high-amplitude blue-noise bump ("cold press").

Each substrate produces TWO maps: albedo (what you see) and a **tooth/bump
map** (height perturbation). Tooth feeds (a) the lighting normal and (b) the
brush engine: dry media (pastel, crayon-like) deposit ∝ tooth peaks; wet media
pool in valleys. New file: `Art/SubstrateFactory.cs`.

### Impasto heightmap lighting
Composite chain per tile: pigment → `PixelShaderEffect("impasto.hlsl")`
sampling Height (tile) + substrate bump, computing a screen-space normal
(Sobel on height) and Blinn-Phong with a fixed key light (up-left, subtle),
specular scaled by the medium's `Gloss` parameter (oil/enamel/encaustic high,
watercolor/pastel ~0). Shader files: `Art/Shaders/impasto.hlsl`,
`bleed.hlsl`, `dry.hlsl`, `stamp.hlsl` (+ compiled `.bin` embedded resources).

---

## 3. Paint physics engine

New files: `Art/ArtBrushEngine.cs`, `Art/PaintPhysics.cs`, `Art/Pigment.cs`.

### Brush load & depletion
- Brush state: `BrushLoad { Vector4 Color; float Amount; float Water; }`
  (Amount 0..1 = pigment reservoir, Water 0..1 = solvent).
- Per-stamp deposit `d = f(Amount) * pressure * mediumFlow`, then
  `Amount -= d * depletionRate`. Depletion **curve per medium** (the three
  user-specified shapes):
  - `SCurve`:      `f(a) = smoothstep(0,1,a)` — holds, then falls off (oil).
  - `Exponential`: `f(a) = a²` — fast initial dump, long faint tail (watercolor).
  - `Linear`:      `f(a) = a` (digital, ink wash).
- **Water dip resets colour**: the palette overlay (§3-mixing) has a water
  well; tapping it sets `Amount=0, Water=1, Color=clear` — the next pickup
  from palette or canvas starts clean. Dipping in a paint well sets Color and
  refills Amount per medium's `LoadCapacity`.

### Stamp pass (GPU)
Strokes are resampled to stamps at `spacing = radius * mediumSpacing`.
`stamp.hlsl` blends stamp into Pigment using Kubelka-Munk-approx mixing:
existing canvas colour is picked up into the brush (`pickupRate * wetness`)
and the deposited colour is `KM_mix(brush, canvas, d)`. KM approximation:
convert RGB→(absorption K, scattering S) via per-channel `K=(1-c)²/(2c)`,
mix K/S weighted, invert. Cheap, gives believable yellow+blue=green.
Wetness += water content; Height += `d * bodyFactor` (impasto media only;
knives *displace*: they read height along the stroke and push it sideways).

### Dilution & bleed into wet layers
`bleed.hlsl` ping-pong pass over dirty wet tiles (plus 1-texel apron from
neighbours so bleed crosses tile seams): pigment diffuses with rate
`∝ wetness² * mediumBleed`, biased downhill on the substrate tooth valleys;
edges darken slightly (watercolor edge effect) via divergence term. Runs at
~30 Hz while any tile has wetness > threshold and real-time drying is on.

### Mixing palette overlay
`Controls/MixingPaletteOverlay.cs`: a floating liquid-glass panel (reuse the
app's glass styling + `Liquidness`) containing a small ArtSurface-like mixing
slab (single 512×512 tile, same stamp/mix shaders, never dries), 8 paint
wells (recent colours; long-press to set from picker), and the water well.
Brush strokes on the slab mix; a tap picks up `BrushLoad` from under the tap.

### Drying model
- Per-medium `DryHalfLife` (seconds). `dry.hlsl`: `wetness *= exp(-dt/τ)`;
  as wetness crosses 0, pigment is "fixed" (pickupRate → 0 for that texel).
- **Real-time drying toggle** (per document): off → wetness only advances on
  explicit user action.
- **Dryness slider** (0..1, per document): global bias multiplying τ and
  clamping max wetness — at 1.0 everything behaves dry-on-dry.
- **Static-wet rule** (drying toggle OFF): a stroke's paint stays wet until
  the *next* stroke begins, at which point all previous strokes' wetness is
  multiplied by `StaticWetCarry` (per medium; watercolor keeps 0.5, oil 0.9,
  acrylic 0.2) — so wet-into-wet is always possible with the last stroke but
  the canvas doesn't stay a swamp forever.

---

## 4. The 13 media presets + palette knives

`Art/MediumPreset.cs` — plain record, all presets in a static table
(`MediumPresets.All`). Parameters: `Flow, LoadCapacity, DepletionCurve,
DepletionRate, WaterAffinity, Bleed, PickupRate, BodyFactor (height),
Gloss, DryHalfLifeSec, StaticWetCarry, ToothResponse, EdgeDarken, Grain`.

| Medium        | Curve | Flow | Load | Bleed | Pickup | Body | Gloss | τ dry (s) | Notes |
|---------------|-------|------|------|-------|--------|------|-------|-----------|-------|
| Oil           | S     | 0.8  | 0.9  | 0.15  | 0.7    | 0.9  | 0.7   | 600       | high carry, knife-friendly |
| Watercolor    | Exp   | 0.9  | 0.6  | 0.9   | 0.3    | 0.0  | 0.05  | 25        | edge darken 0.6, tooth pooling |
| Pastel        | Lin   | 0.5  | 0.7  | 0.0   | 0.1    | 0.15 | 0.0   | 0 (dry)   | deposit ∝ tooth peaks, grain 0.8 |
| Acrylic       | S     | 0.85 | 0.8  | 0.25  | 0.4    | 0.5  | 0.4   | 60        | fast dry |
| Digital       | Lin   | 1.0  | ∞    | 0.0   | 0.0    | 0.0  | 0.0   | 0         | no physics; plain alpha blend |
| Ink wash      | Exp   | 0.95 | 0.5  | 0.8   | 0.2    | 0.0  | 0.1   | 20        | value-biased KM (stains) |
| Encaustic     | S     | 0.6  | 0.9  | 0.05  | 0.6    | 1.0  | 0.8   | 8         | dries almost instantly, max body |
| Spray         | Exp   | 0.7  | 0.8  | 0.1   | 0.0    | 0.05 | 0.1   | 15        | stamp = noise splat cloud, radius ∝ distance param |
| Fresco secco  | Lin   | 0.6  | 0.5  | 0.3   | 0.2    | 0.1  | 0.0   | 40        | matte, chalky KM shift toward white |
| Gouache       | S     | 0.85 | 0.7  | 0.4   | 0.35   | 0.2  | 0.1   | 45        | rewettable: dry pigment regains pickup when wet stroke passes |
| Enamel        | S     | 0.75 | 0.85 | 0.2   | 0.5    | 0.6  | 0.9   | 300       | self-levelling: height diffuses while wet |
| Tempera       | Lin   | 0.7  | 0.55 | 0.15  | 0.25   | 0.15 | 0.15  | 30        | short strokes, fast deplete |
| Sand          | Lin   | 0.5  | 0.8  | 0.0   | 0.0    | 0.7  | 0.0   | 0         | granular noise stamp, height-only + albedo speckle |

**Palette knives** (`Art/KnifeTool.cs`): not a medium — a tool that runs a
displacement stamp: reads Pigment+Height under the blade, smears both along
stroke direction (load-carry like a brush but pickup-dominant), flattens
height (`height = lerp(height, min-under-blade, knifePressure)`). Variants:
flat spread, edge scrape (removes to substrate), stipple.

---

## 5. Canvas setup dialog

`Controls/ArtCanvasDialog.cs` (ContentDialog, styled like existing dialogs).
Shown when creating an Art Notebook page (and from page context menu → resize).

`CanvasSpec { double WidthUnits, HeightUnits; Unit Unit /* in|cm */;
int Dpi /* 150|300 */; SizePreset Preset /* A4, Letter, EightByTen, Custom */;
SubstrateKind Substrate; }` → pixel size = units × dpi (A4 = 8.27×11.69 in).
Guard: warn above ~64 MP (tile count/VRAM). Substrate picker shows live
128×128 procedural swatches from `SubstrateFactory`. Persist last-used spec
in `Library` (new nullable fields, additive → old JSON loads fine).

---

## 6. Art Notebook integration

### Model changes (`Models/NoteModels.cs`, additive only)
```
Notebook { public string Kind { get; set; } = "Notes"; }   // "Notes" | "Art"
NotePage { public string? ArtFile { get; set; }            // rel. path to .artq
           public string? ArtSpecJson { get; set; } }      // CanvasSpec cache
```
Old libraries deserialize with defaults — zero migration. Gallery
(`MainWindow` gallery view): "New Art Notebook" option; art cards show the
.artq thumbnail; `CoverEmoji`/colour reused as-is.

### Trimmed top bar
When the active page is an art page, the existing top bar hides text, lasso
and dictation buttons and keeps: **comment, touch-draw, perfect-object
(→ stroke beautification, §9), undo, redo, history, recording, AI**. Done by
visibility bindings on a `IsArtMode` flag in MainWindow — no new toolbar.
- **Panning + mouse modes**: reuse `MouseMode` (Auto/Grab/Select/Move)
  semantics from InkSurface; ArtSurface implements the same gestures
  (space/middle-drag pan, ctrl+wheel zoom, two-finger pan/zoom).
- **Touch-draw off by default** for art pages: finger pans, pen paints
  (per-document bool, toggle in top bar as today).
- Pen row is replaced by a **media strip**: 13 medium chips + knife + brush
  size/water sliders + colour well that opens the mixing palette overlay.

---

## 7. Stroke list panel & painting history

`Art/ArtStroke.cs`:
```
ArtStroke { Guid Id; string Medium; Vector4 Color; float Size, Water;
            int RngSeed; List<StrokePoint> Points;   // reuse Models.StrokePoint
            long CreatedTicks; int FirstDirtyTileEpoch; }
```
Strokes are **fully deterministic**: engine randomness (spray, sand, bristle
jitter) comes from `RngSeed`. Therefore the stroke list IS the document's
edit history and replaying strokes 0..k reproduces the canvas at step k.

`Controls/ArtStrokePanel.cs` (side panel, glass-styled, like existing panels):
virtualised list of strokes (icon = medium glyph + colour swatch + time).
- **Hover** → ArtSurface draws the stroke's path as a glowing outline overlay.
- **Delete / recolour** → mutate the stroke list, then **rebuild by replay**:
  clear affected tiles and replay all strokes intersecting them in order
  (bounded: only tiles the deleted stroke touched, plus strokes overlapping
  those tiles). Full-canvas replay is the fallback for pathological overlap.
- **Painting history** (top-bar history button): a scrubber slider 0..N runs
  replay to index k — same code path as InkSurface's stroke replay concept,
  reusing the `ReplayEnded`-style events so the top-bar wiring matches.
  Replay runs with drying accelerated (wetness fast-forwarded between strokes
  by their real timestamp deltas, capped) so history looks correct.

---

## 8. Undo — tile-delta snapshots with memory budget

`Art/ArtUndoManager.cs` (parallel to `UndoRedoManager`; ArtSurface routes the
shared top-bar Undo/Redo buttons to it via `ICanvasHost`).

- On stroke/knife begin: record the set of tiles about to be touched
  (predicted from the path, expanded as physics dirties more).
- On stroke end: for each touched tile, read back Pigment/Wetness/Height
  (`GetPixelBytes`), **delta = XOR against the pre-stroke copy, Deflate-
  compressed** (XOR makes untouched texels zero → compresses extremely well).
- `TileDeltaAction { List<(tx,ty, byte[] deltaP, deltaW, deltaH)>; ArtStroke? StrokeRef; }`
  — Undo applies XOR-decompressed deltas back and removes the stroke from the
  list; Redo re-applies. Stroke-list mutations (delete/recolour) push a
  `StrokeListAction` that stores the replay-affected tile deltas the same way.
- **Memory budget**: default 256 MB (setting later). A deque tracks total
  compressed bytes; oldest actions are evicted (undo depth silently shortens
  on huge canvases). Deltas > budget/4 alone (giant fill) store a full
  compressed tile copy instead.
- Pre-stroke copies are taken lazily per tile on first dirty (copy-on-write),
  so idle cost is zero.

---

## 9. .artq serialization

Format: **ZIP container** (System.IO.Compression, like the docx/pptx family):
```
manifest.json      version, CanvasSpec, substrate, medium state, dryness
                   slider, real-time-drying flag, tile index [(tx,ty,layerFlags)]
strokes.json       full ArtStroke list (replay/history survives save)
tiles/{tx}_{ty}.p  raw R16G16B16A16F pigment (Deflate via zip entry)
tiles/{tx}_{ty}.w  raw R16F wetness  ← wet state persists across sessions
tiles/{tx}_{ty}.h  raw R16F height
thumb.png          256px composite for the gallery card
```
`Art/ArtqStore.cs`: `Save(ArtDocument, path)` / `Load(path)`. Files live at
`LibraryStore.Dir/art/{pageId}.artq` (rides the user's central-folder sync,
consistent with `LibraryStore.Dir` semantics). Save is debounced on stroke-end
(reuse `AutosaveSeconds`), written atomically (`.tmp` + `File.Replace`,
mirroring `LibraryStore.WriteCore`), with a single `.bak` rotation. Only
dirty tiles are re-serialized when rewriting (copy untouched entries from the
previous zip). `NotePage.ArtFile` stores the relative path; `LibraryStore`
is untouched except that deleting an art page should delete its `.artq`
(hook in the existing page-delete path in MainWindow).

---

## 10. Cloud AI integration points

`Art/ArtAiService.cs`, modelled exactly on `Services/AiService.cs`: static
class, `PasswordVault` with the same `VaultResource`, provider rows added to
the existing AI settings page (Stability / Replicate). All calls: flatten the
document (or a selection rect) to PNG in-memory → base64/multipart POST →
result imported as a new *stroke-list entry of kind "AI layer"* (a special
ArtStroke variant `AiPatch { pngBytes, destRect }` so it replays, lists,
deletes and undoes like everything else).

| Feature | Endpoint style | Trigger |
|---------|----------------|---------|
| Sketch-to-image (ControlNet) | img2img + control image = flattened canvas | AI menu → "Render my sketch" + prompt box |
| Inpaint / outpaint | mask from a marquee (inpaint) or expanded CanvasSpec border (outpaint) | AI menu |
| SAM flatting | SAM segmentation → fill regions with flat colour into a new AiPatch | AI menu → "Flat fill" |
| Pose assist | pose-detection → skeleton overlay rendered as guide (non-printing overlay layer, not pigment) | AI menu |
| Stroke beautification | LOCAL-first (curve fitting/smoothing of last ArtStroke path, then replay it); cloud model optional later | the kept **perfect-object** button |

Errors/timeouts surface via the same InfoBar pattern the chat AI uses.
No keys or images ever touch `library.json`.

---

## 11. Implementation phases (B2..B7)

Effort: S ≈ ½ day, M ≈ 1 day, L ≈ 2–3 days, XL ≈ 4+ days of a fast-worker
agent. Each phase must build (`dotnet build src/Quill/Quill.csproj -c Debug
-p:Platform=x64`) and be demoable.

| Phase | Deliverable | Key files | Effort |
|-------|-------------|-----------|--------|
| **B2** | ArtSurface skeleton: tile grid, pan/zoom, digital medium only (plain alpha stamp), canvas setup dialog, Art Notebook in gallery, trimmed top bar + media strip shell, ICanvasHost | ArtSurface.cs, TileGrid.cs, ArtDocument.cs, ArtCanvasDialog.cs, ICanvasHost.cs, NoteModels additions, MainWindow wiring | **XL** |
| **B3** | Substrates (4 procedural) + tooth maps; impasto height accumulation + lighting shader; shader build pipeline | SubstrateFactory.cs, Shaders/*.hlsl, csproj shader target | **L** |
| **B4** | Physics core: BrushLoad + 3 depletion curves, KM mixing + pickup, wetness map, bleed pass, drying (τ, toggle, slider, static-wet rule), water dip | ArtBrushEngine.cs, PaintPhysics.cs, Pigment.cs, stamp/bleed/dry.hlsl | **XL** |
| **B5** | 13 media presets tuned + palette knives + mixing palette overlay | MediumPreset.cs, KnifeTool.cs, MixingPaletteOverlay.cs | **L** |
| **B6** | ArtStroke recording + deterministic replay, stroke list panel (hover/delete/recolour), painting history scrubber, tile-delta undo w/ budget, .artq save/load + thumbnails + page-delete hook | ArtStroke.cs, ArtStrokePanel.cs, ArtUndoManager.cs, ArtqStore.cs | **XL** |
| **B7** | Cloud AI: settings rows + ArtAiService, sketch-to-image, inpaint/outpaint, SAM flatting, pose overlay, beautification (local) as AiPatch strokes | ArtAiService.cs, AI menu UI | **L** |

Dependency order is strict B2→B3→B4→B5→B6→B7 except: B6's stroke recording
skeleton (ArtStroke capture) should land in **B2** so every later phase's
strokes are already replayable; B7 beautification (local) can ship any time
after B2.

### Risks / notes for implementers
- Win2D `PixelShaderEffect` needs precompiled shader blobs — set the build
  target up in B3 first, verify one trivial shader end-to-end before writing
  the real ones.
- Tile-seam correctness in bleed: always sample with a 1-texel apron copied
  from neighbours; test with a wash crossing a seam.
- fp16 readback (`GetPixelBytes`) is the undo hot path — measure; if slow,
  restrict Pigment to 8-bit sRGB + separate 16f only where needed.
- PUA-glyph caution from ORCHESTRATION-STATE applies to *existing* files
  (MainWindow etc.) — patch those via python scripts; new Art/ files are clean.
- Kill the app before building: `powershell Stop-Process -Name Quill -Force`.
