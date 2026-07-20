# Quill → Concepts — Definitive Direction

Status: lead-architect direction document, for user review before any implementation.
Author role: lead architect. Written from the research dossier, a full read-only
codebase audit, three competing designs, and two adversarial critique passes.

**This document supersedes and deletes two things:**
- `docs/ARTMODE-ARCHITECTURE.md` (the separate `ArtSurface` + "Art Notebook" gallery type).
- The ROADMAP's "Next — Art Mode (beta)" headline (the sparse-tile paint app as a distinct surface).

Per the user's new direction, **there is no separate art surface**. Painting folds into the
ordinary note surface (`InkSurface`), and paper/canvas "panel types" live in the page-background
menu, not behind a mode switch.

Binding environment facts (from ORCHESTRATION-STATE / ROADMAP):
- Build: `dotnet build src/Quill/Quill.csproj -c Debug -p:Platform=x64`. Kill first: `powershell Stop-Process -Name Quill -Force`.
- Source files carry PUA glyphs — patch existing files with python scripts, not the Edit tool. New files are clean.
- Effort scale: **S** ≈ ½ day, **M** ≈ 1 day, **L** ≈ 2–3 days, **XL** ≈ 4+ days of a fast agent.
- In flight, do not collide with: **SyncLog delete-op Parent fix** (separate session — our sync
  work in §3.3 touches the same `SyncLog.Apply` method; coordinate), Liquid-glass v2 and Menu-animations (awaiting visual sign-off).

---

## 0. Thesis

**Quill becomes a pen-first infinite-canvas notebook whose entire tool surface is the pen row,
with one options button for everything else — and where "art," "layers," "objects," and "paper"
are capabilities of the ordinary page, never separate modes.** The fat scrolling top command bar
is retired. Tools live where the hand already is; document- and app-level actions live behind a
single top-right button; continuous parameters (size, colour, stabiliser) live on the pen row
itself. This is Concepts' decomposition — *modal on the wheel, state in dockable panels,
configure-once in settings* — mapped onto Quill's existing, shipped machinery (`_toolTag`,
`_library.Pens`, `PenRow`, `PageSettingsBtn`, `ApplyKeyPreset`).

**What Quill deliberately does NOT copy from Concepts:**

| Not copied | Why |
|---|---|
| A raster layer stack (per-layer pixel buffers, blend modes) | Quill's canvas is infinite; per-layer buffers are undefined without a canvas area and cost VRAM by area. Layers are **ordering metadata over one stroke soup** (§3), exactly Concepts' own choice on the same infinite-canvas problem. No blend modes, ever. |
| Drawing-time snapping ON by default | Concepts is a sketching app; Quill is handwriting-first. Straightening or endpoint-fusing letterforms is catastrophic. **Every while-drawing snap ships OFF** (§6). Edit snaps ship on. |
| The Tool Wheel as the only/primary tool surface | The user asked for the radial wheel as *an option*. The **linear pen row ships first and is a first-class permanent form** (§2). The wheel is a projection of the same state, deferred. |
| A store with uploads and payments | A real marketplace is a staffed, ongoing service (accounts, human IP review, image re-encode, DMCA, VAT via a merchant-of-record, payouts, moderation). Quill ships a **curated read-only feed** — genuinely "a marketplace" in the Concepts brush-market sense — and treats an upload/payment store as a separate funded business decision (§5.6). |
| Layer z-ordering of text | Text is a XAML `RichEditBox` overlay above the whole Win2D canvas; it always renders on top. We **market layers as "ink & shape organization"** and say so in-product rather than faking text stacking (§3.9). |
| Auto-reorganising an existing page | Concepts' automatic tool→layer mapping is delightful on a blank canvas but would silently restack a finished note. Quill defaults new pages to **flat/manual**; auto-layering is opt-in and **never reorders pre-existing elements** (§3.4). |

The pen-first minimum is the north star: any capability that would make an unmodified note-taker's
page more cluttered than today's top bar must degrade to invisibility (density ladder, §2.6) or be
opt-in per page.

---

## 1. Every top-bar command gets a new home

### 1.1 The decision function (applied in order)

1. **Changes what the next stroke does?** → MODAL → pen row / wheel. (Switched dozens of times/session; zero-menu.)
2. **Continuous parameter tuned while drawing?** → pen-row inner ring / always-visible slider. Never a menu.
3. **Persistent toggle whose current STATE must stay visible while drawing?** → floating dockable panel, toggled from the options menu's *Panels* group.
4. **One-shot, used many times/session, non-modal?** → persistent one-tap chrome AND/OR a gesture.
5. **Input mapping / configure-once?** → Settings, off all three surfaces.
6. **Everything else** → the single options menu.

### 1.2 Disposition of the complete `MainWindow.xaml:21-380` inventory

Home key: **PR** = pen row (linear now / wheel slot later) · **CAP** = slim caption strip (persistent) · **OPT** = options menu · **SET** = Settings · **GES** = gesture.

| Command (x:Name / xaml line) | Rule | New home | Notes |
|---|---|---|---|
| `BtnSidebar` (60) | 4 | CAP notebook chip (burger) + OPT *Panels ▸ Notebooks* | Document nav anchors the app; mirrored as a checkable menu item. |
| `CrumbText` (69) | 4 | CAP notebook-chip label | Makes the chip read as a status chip, not a stray button. |
| `ToolPen` (76) | 1 | PR — implicit | Tapping any pen chip calls `ApplyPreset` → `SelectTool("Pen")`. |
| `ToolText` (79) | 1 | PR tool chip (wheel slot S) | |
| `ToolSelect` / lasso (82) | 1 | PR tool chip (wheel slot W) | **The headline request.** Lasso changes what the pen does → it was never a top-bar command. |
| `ToolSpace` / FreeSpace (85) | 1 | PR tool chip (wheel slot SW) | Keep the lasso double-tap→one-shot FreeSpace (`_blankSpaceOnce`, cs:4843-4867). |
| `TouchDrawToggle` (90) | 3/5 | **PR persistent chip** (+ mirror in SET) | Deliberately NOT buried in a submenu (critique). Mode-defining on a pen+touch tablet; flipped often. |
| `ToolComment` (94) | 1 | PR tool chip (default visible) | |
| `ShapeBtn` (99-131) | 1 | PR Shape tool chip + its 14-shape/axes/Table/Equation flyout (wheel slot SE) | Tap arms last-used shape; re-tap opens the flyout. |
| `MouseModeBtn` (133-147) | 5 | OPT *Input ▸ Mouse mode* (one level) | 4 `RadioMenuFlyoutItem`s, group name preserved. |
| `BtnUndo`/`BtnRedo` (151-156) | 4 | PR end-caps / wheel rim satellites (+ GES two/three-finger tap) | Ctrl+Z/Y retained; never buried. |
| `BtnHistory`+`HistoryList`+`BtnReplay` (157-175) | 5 | OPT *This note ▸ History…* | Replay is a mode of history; **merge into one entry** — a real behavioural merge, design its sub-UI first. |
| `VoiceBtn`=`BtnAudioRecording`+`BtnDictate` (179-195) | 5 | OPT *This note ▸ Voice ▸* | |
| `BtnAi` (198-214) | 5 | OPT *This note ▸ AI ▸* | Every item keeps `AllowFocusOnInteraction=False` to preserve text selection. |
| `ZoomBtn` (218-251) | 4 | **CAP numeric readout** ("142%") + OPT *Zoom ▸* | Zoom is a measurement, not a button group. Readout opens the identical flyout. |
| `PageSettingsBtn` (255-321) | 5 | OPT *This page ▸ Page background & panel…* | **Where paper/panel types land** (§7). |
| `ExportBtn` (323-347) | 4 | **CAP one-tap** + OPT *This note ▸ Export ▸* | High-frequency one-shot — kept one tap, not two levels deep (critique). |
| `AppMenuBtn` menu (33-55) | 5/6 | OPT *App* group (Settings…, Shortcuts, About, Quit) | **`AppMenuBtn` deleted** — genuinely one menu button. |
| `BtnCalc` (351) | 3 | OPT *Panels ▸ Calculator* (toggle) | |
| `BtnHideUi` (357) | 4 | OPT *App ▸ Hide interface* | Also stays the Hidden density rung. |
| `BtnWinMin`/`BtnWinFull`/`BtnWinClose` (362-377) | — | **CAP** (persistent, x:Names + handlers untouched) | Caption z-order guaranteed above every panel (§2.5). |
| Search (Ctrl+F, buried in sidebar) | 4 | OPT *This note ▸ Search* + pen-reachable Ctrl+K | Promoted out of the sidebar; shortcut kept. |
| Command palette (Ctrl+K, in `KeyCommands`) | 4 | OPT *App ▸ Command palette* with a **visible** entry | Primary discoverability fallback — must be pen-reachable, not keyboard-only (critique). |
| Theme / paper colour / accent (in `PageSettingsBtn` flyout + Settings) | 5/6 | SET *Appearance* (theme, accent); OPT *This page* (paper colour, per-page) | Configure-once. |

**Retired machinery:** `ApplyToolbarVisibility` becomes a no-op; `OptionalTools` + `Library.HiddenTools`
are deprecated (kept in JSON for downgrade safety, ignored); the Settings "toolbar checkboxes"
section (cs:4563-4577) is removed. `BtnSettings` (start-screen-only, 65) is untouched.

Net: ~20 fat-bar commands → **1 options button + 4 persistent CAP affordances (notebook chip,
zoom readout, export, options) + the caption buttons**, with tools on the pen row.

---

## 2. The pen row — linear first, radial as an option

### 2.1 Linear tool segment (ships first; satisfies "lasso to pen row" with no new control)

Extend the existing `PenRow`/`BuildPenStrip()` (cs:2142-2218) with a tool segment so the row
becomes the app's modal surface. New `PenStack` order (rotated for Left/Right docks via the
existing `ApplyPenDock`, cs:1934-1977):

```
[Undo][Redo] | [eraser chip][pen chips…][+] | [Lasso][Text][Shape][Space][Comment] | [TouchDraw][Ruler][collapse]
```

- Tool chips are 36×40 DIP icon buttons calling `SelectTool(tag)` with the existing Tag strings
  (`"Select"`, `"Text"`, `"FreeSpace"`).
- `SelectTool`'s four hardcoded `IsChecked` writes (cs:4884-4887) are replaced by one
  `RefreshToolChips()` that lifts the active chip `TranslateTransform Y=-8` (unifying with the
  pen-chip selection cue, `RefreshPenSelection` cs:2110-2117).
- **Delete the Pen/Eraser-only gate** in `ApplyPenRowVisibility` (cs:5315-5327): the row (or its
  reopen chip) is visible in every tool mode. Per-page `_curPage.PenRowVisible` and minimal-UI
  `_floatPen` behaviour are unchanged.
- Eraser chip, its right-click Point/Stroke flyout, `RulerBtn`, and drag-docking are untouched.

This one change is the whole of the user's "move lasso tool to the pen row." It ships in Wave 0
with the top bar still present, so every tool is reachable from both places during transition.

### 2.2 Shared tool-state funnel (linear and wheel never diverge)

No new state, no MVVM rewrite. The sources of truth stay `_toolTag`, `_activePresetId`,
`_library.Pens`, `Surface.*`. All mutations already funnel through `SelectTool(tag)` and
`ApplyPreset(p)`; add one line at the end of each raising `event Action? ToolUiChanged`. Each
projection subscribes and is a dumb renderer:

- `BuildPenStrip` → the linear subscriber.
- `ToolWheel.Refresh()` → the radial subscriber.
- The preset editor's live writes (`CreatePresetFlyout`, cs:2225-2577) raise the event instead of calling `BuildPenStrip` directly.

`Library.PenRowStyle ∈ {Linear (default), Radial}` chooses which control is added to `CanvasArea`.
Switching is instant, zero data migration.

### 2.3 Radial wheel geometry (deferred to Wave 3, fully specified now)

A new self-contained `Controls/ToolWheel.xaml[.cs]` — **code-generated `Path` arc segments, NOT
Win2D, NOT inside `InkSurface`** — so it costs the ink renderer nothing and can carry an
`AutomationPeer` + keyboard focus. All pointer logic is one root handler: a single `atan2` + radius
test against cached geometry (never per-`Path` hit-testing).

Geometry at scale 1.0 (DIPs), footprint 232×232:

| Zone | Radius (DIP) | Contents |
|---|---|---|
| Centre disc | 0–22 | Current colour swatch + active-tool glyph (the "what mode am I in" readout). Tap-release opens the colour flyout. |
| **Dead zone** | 0–24 | Nothing arms inside it. Pen-jitter armour — the single most important number for pen use (Blender's Threshold). |
| Inner ring | 24–44 | **Exactly 3 wedges** of 120°: Size, Stabiliser, Pressure — mapping 1:1 to `PenPreset.Size/Stabiliser/Sens` (Quill has no opacity/smoothing pair; we do not invent one). Tap a wedge → 180 DIP slider with 4 preset ticks (`Library.WheelParamPresets`, `float[3][4]`). |
| **Outer ring** | 44–96 | **Exactly 8 slots**, 45° each, centres at 0°/45°/…/315° from N. Arc ≈55 DIP at midline (> 40 DIP touch floor). **Hard cap of 8, forever** (Kurtenbach: RT jumps at 7→8, error climbs past it). |
| Satellites | ~104–110 | Undo/Redo (40×40) flanking N; ⠿ drag grip at N; collapse ⌄ at S. |

Interaction numbers:
- **Confirm threshold 140 DIP** — drag past it and the slot fires without waiting for pen-up (the expert flick).
- **Recenter window 150 ms** — if the user drags before the ≤120 ms open animation lands, the original press point is the centre so a fast flick still resolves.
- Tap slot = activate; tap the **active** slot again = open its editor (reuse `CreatePresetFlyout` / shape flyout / eraser flyout); press-hold 350 ms or right-click = the slot-assignment flyout.
- **Slot positions NEVER reflow.** An unassigned or contextually dead slot renders dimmed in place — muscle memory is the entire payoff.
- Scale 0.7×–1.6× via Ctrl+scroll / pinch (`Library.WheelScale`), floor 0.85 in touch mode.
- Placement persisted as `Library.WheelPos` (fractional x,y of `CanvasArea`, default 0.10, 0.82 = lower-left). Dragging the grip across the window's vertical centreline mirrors **only** the satellites/grip (handedness flip); slot angles never change.

Slot binding model (`Library.WheelSlots`, JSON-additive):
```csharp
class WheelSlot { int Index; string Kind /* "preset"|"tool" */; Guid? PresetId; string? ToolTag; }
```
This is the mechanism that lets painting brushes, lasso, text, and shapes coexist on one 8-way
wheel without ever growing it. Defaults put the highest-traffic items on cardinals:
N=pen#1, E=Eraser, S=Text, W=Lasso; NE=pen#2, SE=Shape, SW=FreeSpace, NW=pen#3. A deleted preset
empties its slot (dimmed), never reflows. The linear row is NOT slot-limited (it shows all pens +
all tools), so the wheel is a speed-dial view of the same state.

**Depth is a linear list, never a sub-ring.** Tapping an active slot opens a flat scrolling sheet
(all pens/brushes/objects/marketplace items). If a genuine second level is ever needed, cap it at
8,2 or 4,3 — never 8,3.

### 2.4 Transient summon (Wave 3, opt-in, threads around every existing input claim)

Independent of Linear-vs-Radial, the same control can be summoned centred at the pointer:
- **Pen:** OFF by default. SET *Interaction* adds "Barrel tap opens ∈ {Context menu (default), Tool wheel}". When set to wheel: barrel tap on empty canvas summons at the tip on release; barrel tap over a selection still opens the selection menu; **barrel drag remains lasso always** (InkSurface.cs:1073-1098).
- **Touch:** only when `HandDrawMode` OFF — stationary finger (<8 DIP) for 400 ms on empty canvas summons under the finger. When HandDrawMode is ON the finger draws and the 620 ms shape-hold must not be shadowed.
- **Mouse:** Ctrl+Right-press summons (plain right-press keeps today's lasso/context).
- **Keyboard/UIA:** hold Tab ≥150 ms with canvas focus (never in a text edit — reuse the `ToolAccel` guard, cs:5733-5739) → wheel at last pointer position; keys 1–8 fire slots clockwise from N. This is the accessibility path.
- Dismiss (all devices): release/tap inside the dead zone, Esc, or tap outside.

### 2.5 Persistent chrome after the bar is gone (caption stranding + FormatBar collision, resolved)

**Decision — keep a slim 36 DIP caption strip, do not free-float the caption buttons in
`CanvasArea`.** The design's floating-capsule-in-`CanvasArea` proposal is rejected because it can be
occluded by the full-bleed `GalleryPanel`/`NotebookPanel`/`AiPanel` (min/max/close vanish on the
start screen — a hard critique), and because `FormatBar` (Grid.Row=1, xaml:383) would reflow the
capsule vertically every time Text mode toggles, making the window controls jump.

Resolution:
- **Row 0 collapses from the fat scrolling bar to a slim 36 DIP caption strip** that always exists,
  holds only: notebook chip (burger + `CrumbText`), zoom readout, Export one-tap, the OptionsBtn,
  then `BtnWinMin`/`BtnWinFull`/`BtnWinClose`. `x:Name`s and handlers unchanged; close keeps its
  red hover (cs:1826-1835). Caption buttons **never move** and are always top-most by grid-row
  order — no `Canvas.ZIndex` race.
- `FormatBar` stays Row 1 (below the strip); `CanvasArea` Row 2. No reflow of window controls.
- Drag: attach the proven `WM_NCLBUTTONDOWN`/`HTCAPTION` handoff (cs:1812-1821) + double-tap→maximize
  to the strip's empty regions via `OverInteractive` (cs:1842-1852). This preserves Aero snap,
  Win+Arrow, drag-to-edge, and shake, and gives windowed users a real top-edge grab target (the
  critique's drag-surface concern) — while the pen-first payoff (ink from y=0) returns in
  Hidden/fullscreen mode where the strip is gone.
- OptionsBtn sits left of Min/Max/Close with Min/Max between it and Close, so a misclick lands on
  Minimize (recoverable), never Close.

**Phase non-regression checklist (blocking):** caption reachable on the gallery/start screen and
with any panel open; drag + double-click-maximize from the strip; drag-to-edge half-snap; Win+Arrow;
min/max/close + close-hover-red; minimal-UI round trip incl. fullscreen restore; `FormatBar` appears
in Text mode without moving the caption; touch-mode target inflation on the strip.

**Async-startup gate:** the window is shown before `LibraryStore.LoadAsync` completes. Gate the
OptionsBtn menu and every relocated flyout (History/Export/AI/Settings) on `_libraryReady` so a
click during the load gap is a no-op, not a null-deref.

### 2.6 Density ladder (stops the new design being more cluttered than the bar it replaces)

Three states, hysteresis 80 DIP on breakpoints, animated morph:
- **Normal** ≥ 900 DIP width: full pen row / wheel + all panels.
- **Compact** 600–900: dock to nearest edge, labels→icons, sliders hide; wheel becomes spinnable so occluded slots rotate into reach.
- **Hidden** < 600 or `BtnHideUi`: everything gone except one restore affordance (today's minimal-UI cluster). This is the "ink from y=0" state.

The options menu's *Panels* group can put six things on screen (pen row, Layers, Precision, Objects,
Calculator, Audio) — the density ladder + edge-docking is load-bearing, not polish.

---

## 3. Layers

### 3.1 Model decision — a `LayerId` field, hierarchy rejected

**Decision: add `Guid LayerId` to each element + a metadata-only `List<PageLayer>` on `NotePage`.
Do NOT restructure into `NotePage.Layers[i].Strokes`.** This is not a close call — five verified
codebase facts veto the container hierarchy:

1. **Undo** (decisive). `RemoveStrokesAction` (UndoRedo.cs:80) / `ReplaceStrokesAction` (:103) store
   `List<(int Index, PenStroke)>` and restore with `page.Strokes.Insert(Math.Min(idx, Count), s)` —
   integer indices into ONE page-wide list. A hierarchy makes an index meaningless without also
   knowing the layer, forcing a re-audit of all ~27 `IPageAction`s and turning the point-eraser
   fragment path into an index-restoration bug farm. With a field, "move to layer" undo is just
   `List<(Guid ElementId, Guid OldLayerId)>` and every existing action keeps working.
2. **Sync.** `SyncLog` is an element-level op log parenting elements to the **page**, LWW per
   element, full-entity JSON in `J`. `LayerId` rides inside `J` for free (ordinary upsert). A
   hierarchy needs a new op kind and introduces a data-loss race (device A deletes layer L while B
   draws into it → the stroke arrives parented to a nonexistent list and is dropped).
3. **Touch surface.** `.Strokes`/`.Shapes`/`.Texts` appear ~187× across 7 files. A hierarchy
   rewrites all of them; a field touches ~15 sites.
4. **Spatial index.** One page-global `SpatialGrid` whose staleness net is a count compare
   (InkSurface.cs:2369-2390). Split lists multiply the stale paths; layers should be a post-query
   filter (you *want* the lasso to hit all layers from one index).
5. **Self-healing.** Unknown/deleted `LayerId` → resolves to the lowest-Order layer and stays
   visible. A hierarchy orphan is data loss. This property is the whole argument and you cannot get
   it from a tree.

```csharp
// PenStroke, ShapeElement, TextElement (NoteModels.cs) — additive
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public Guid LayerId { get; set; }

class PageLayer { Guid Id; string Name="Layer"; double Opacity=1.0; bool Visible=true;
                  bool Locked; int Order /*0=backmost, render ascending*/;
                  int Kind /*0 Vector,1 Raster*/; string? Family; long CreatedTicks; }
// NotePage — additive
List<PageLayer> Layers = new();
[JsonIgnore(WhenWritingDefault)] Guid ActiveLayerId;
[JsonIgnore(WhenWritingDefault)] bool ManualLayering;
```
`Guid.Empty` = unassigned = the lowest layer. **Empty `Layers` list ⇒ exactly today's code path**
(panel hidden, every draw/hit path unchanged). Zero migration, no version bump.

`CloneWithPoints` (NoteModels.cs:88-91) currently copies **neither** `LayerId` nor `GroupId` — it
**must copy both**, or a point-erase fragment silently jumps layers/groups.

### 3.2 Serialization — no bloat, no oplog re-emit (blocking)

`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]` is **required on both `LayerId`
and `GroupId`** explicitly (not "by analogy" — critique). Neither `LibraryStore.Opts` nor
`SyncLog.Opts` sets a global default, so omitting the attribute writes `"LayerId":"000…0"` on every
element (~49 B each across a 53 MB library) AND changes every element's FNV hash (SyncLog.cs:164-167),
forcing an op for **every element** on first save after upgrade (tens of MB oplog append). With the
attribute: unlayered content costs 0 bytes and emits 0 ops.

Cost that IS real: bulk field-assignment (layer materialization, group/ungroup, move-to-layer) emits
one op per element — a 5,000-stroke page = ~5,000 ops ≈ 2.5 MB oplog for one click. Batch these into
a single action; confirm `SyncLog` compaction (2 MB threshold) collapses the burst on next save.

### 3.3 SyncLog round-trip — BOTH halves, or every synced device loses layers (blocking)

`PageMetaJson(pg)` (SyncLog.cs:~125) must serialize `Layers`, `ActiveLayerId`, `ManualLayering`,
`Paper` (§7), `Perspective` (§7). **And the reciprocal half must be patched**: `SyncLog.Apply`'s
`"pg"` case (SyncLog.cs:~397-401) copies a **hard-coded** field list back onto the page and is
already lossy (`GridColor` is serialized but never copied). For every field added to `PageMetaJson`,
add the matching copy line:
```csharp
pg.Layers = meta.Layers; pg.ActiveLayerId = meta.ActiveLayerId;
pg.ManualLayering = meta.ManualLayering; pg.Paper = meta.Paper;
pg.Perspective = meta.Perspective; pg.GridColor = meta.GridColor; // fix the pre-existing GridColor drop
```
Without this, a foreign page op deserializes layers into `meta` and throws them away; element
`LayerId`s self-heal to the lowest layer, so **all content collapses onto one layer and paper
reverts on the peer.** Add a cross-device round-trip test (3 layers + paper survive a `MergeForeign`)
to the phase gate. Coordinate with the in-flight SyncLog delete-op Parent fix — same method.

### 3.4 Automatic vs Manual layering — default OFF, never restack a finished page (major)

**New pages default to flat/manual** (`ManualLayering=false` but no auto-routing until the user opts
in) so draw order is byte-for-byte today's, and the "0-layer page renders pixel-identical" guarantee
holds even for a mixed pen+highlighter note. Auto-layering is an explicit per-notebook / per-page
opt-in.

When Auto is ON it routes **only NEW elements** by `PenType` family and **never reorders
pre-existing elements** (turning it on cannot rearrange a finished page — the critique's
highlight-over-ink hazard). Family map over the verified `PenType` enum:
`{Standard,Fountain,Calligraphy,Monoline,Rollerball,Gel,Ballpoint,FeltTip}→"Ink"`;
`{Pencil,Crayon}→"Pencil"`; `{Highlighter}→"Highlight"`; `{Brush,Watercolor,Marker}→"Paint"`;
`ShapeElement→"Shapes"`; text→lowest layer (text is always top-most anyway, §3.9).

**Stacking default decided deliberately, not inherited from an alphabetical rank:** Paint(0) <
Ink(1) < Shapes(2) < Pencil(3) < Highlight(4)? No — the common note convention is **highlighter
BEHIND ink** so a highlight never obscures the annotation written over it. Default ascending Order:
**Highlight(0) < Paint(1) < Ink(2) < Pencil(3) < Shapes(4)**. Layers created on first use of a
family only. Any explicit layer act (reorder, active-pick, rename, drag-to-row) latches
`ManualLayering=true`.

### 3.5 Rendering — derived buckets + a budgeted `DrawRegion` restructure (major)

The claim "reuse today's loop bodies verbatim, zero per-frame cost, fps unchanged" only holds for
0-layer pages. Once any layer exists, `DrawRegion` (InkSurface.cs:2977-3057) must change from
*all-shapes-then-all-strokes* to an **ordered bucket walk**, and `TryDrawInkCache` (cs:6634) must be
**rewritten** to render only the cached run's layers in Order. Budget this explicitly in the phase:

- `_layerBuckets: List<LayerBucket{ PageLayer Meta; List<ShapeElement> Shapes; List<PenStroke> Strokes }>`, ascending Order.
- Rebuild trigger copies the `EnsureGrid` staleness idiom: `_bucketsDirty || count mismatch || _layerRev changed`. `PushAction` (cs:2444-2448) sets `_bucketsDirty` alongside `_gridDirty` so undo/sync mutations are covered by the one choke point.
- Draw: `foreach bucket in Order { if(!Visible) continue; [opacity wrap if <1.0]; shapes body (incl. `_movingSel` offset); strokes body (incl. cache branch); }`. Per-stroke per-frame cost vs today is genuinely zero (buckets are pre-partitioned, no dictionary in the hot loop) — but the two flat loops are gone, so **benchmark a 2500+ stroke page that HAS layers before claiming fps parity.**
- The true fast path stays `Layers empty → exact current code`.
- Bonus fix: today shapes never sit above ink; buckets make z-order per-layer-then-per-type, which is what users expect once a panel exists.

### 3.6 Static ink cache under layers — one target, one contiguous run

Keep **ONE** `CanvasRenderTarget` (3× viewport, `ViewZoom×1.5`, clamped 4096 px). Per-layer targets
are vetoed (4096²·BGRA ≈ 64 MB each; 8 layers ≈ 512 MB). Cache the single **contiguous run of
visible, fully-opaque, Vector layers holding the most strokes** (`_inkCacheRun = (startOrder,
endOrder)`); layers outside the run draw live on the existing `_movingSel/_spacing/_replaying`
fallback. New `_inkCacheDirty` triggers: any layer Visible/Opacity/Order/Kind change, an
`AssignLayerAction` touching a run member, merge/delete/duplicate. Typical shape (one heavy Ink layer
+ a translucent Highlight above) → run = the Ink layer ⇒ essentially all of today's benefit retained.
Focus Mode bypasses the cache entirely (treated like `_replaying`) rather than thrashing rebuilds.

### 3.7 Hit-testing / lasso / eraser — one global index, layers as filters

`SpatialGrid` untouched; layers are post-query filters with an explicit **All / Active** scope toggle
(default **All** — any other default reads as "the lasso broke"), persisted as
`Library.EraserLayerScope` / `LassoLayerScope`. Hidden layer ⇒ excluded from draw **and every hit
path** (erasing over hidden ink must be a no-op, or it feels haunted). Locked layer ⇒ excluded from
eraser (`EraseAt` 2453-2539), lasso (`SelectWithLasso` 2544-2594), and hover previews
(`FindStrokeNear` 1510-1539). Drawing on a locked active layer: pen-down shows a 1.2 s toast and
deposits no ink; Auto never targets a locked layer; the active-layer chip renders red-tinted.

### 3.8 Undo — five flat-list-safe actions through `PushAction`

`AssignLayerAction` (id→layer field writes), `LayerMetaAction` (before/after one `PageLayer`),
`ReorderLayersAction` (`(Guid,oldOrder,newOrder)[]`), `AddLayerAction`/`DeleteLayerAction` (delete
captures the `PageLayer` **and** per-type `List<(int Index, element)>` reusing the
`RemoveStrokesAction` shape so undo restores elements + positions + membership), `MergeDownAction` =
`CompositeAction(AssignLayerAction(all A→B), delete A)`. Merge is **non-destructive** — every stroke
keeps identity and stays individually editable/erasable; say so in the UI (a genuine advantage over
raster merge). All route through `PushAction`, which now dirties grid + buckets + ink cache from one
place.

### 3.9 Text honesty — layers are "ink & shape organization" (major)

Text renders as XAML `RichEditBox` overlays above the whole Win2D canvas (InkSurface.cs:250,
259-263); interleaving text between ink layers needs Win2D text (out of scope — Win2D text exists
only for export, `DrawTextElement` 2734-2775). Spec: a text element's layer drives its
`Visibility`/`IsHitTestVisible`/`Opacity`, but **z-order is always top-most.** The panel shows a
`T{n}` badge on text-bearing rows and the caption "Text always displays on top." Onboarding/coach
marks for layers must never imply text participates in stacking. Market the feature as ink & shape
organization.

### 3.10 Opacity — second increment, perf claim scoped (minor)

Ship visibility/lock/rename/reorder/duplicate/select-all/merge/delete **first, all layers at 1.0**.
Add per-layer opacity as a second increment — the only part that costs frame time. Implement **group
opacity** (overlaps within a layer don't darken at intersections): `Opacity==1.0` → draw straight
(no cost, the 99% case); `Opacity<1.0` → `using(ds.CreateLayer(opacity))` with no explicit
`ID2D1Layer` so D2D pools it. The "fps unchanged" claim holds **only for sparse translucent layers**
— a densely *painted* translucent layer forces a full-canvas intermediate every frame and is excluded
from the opaque-only cache, so it always draws live. Teaching-tip past 2 simultaneous translucent
layers; benchmark a 2000+ element translucent layer before shipping opacity.

### 3.11 Panel + exporters

Floating right-docked card (264 DIP) per the existing panel idiom (`CardBrushFloat`,
`BeginCheapDrag`/`JellyDrag`), opened from OPT *Panels ▸ Layers* and Ctrl+L. Rows 44 DIP (56 in touch
mode), topmost first: thumbnail · name (dbl-click rename) · eye · lock · ⋯. Auto/Manual pill, `[+]`,
drag-to-reorder, drag-selection-onto-row = `AssignLayerAction` (+ a "Move to layer ▸" menu item for
pen users). Focus Mode = double-tap row → transient `_focusLayerId` view state at 0.25 opacity;
**never serialized, never undoable, cleared on page switch.** Soft cap 16 layers. Active-layer name
surfaces as a 24 DIP chip on the pen row (red when locked).
`PdfExporter`, `HtmlSvgExporter`, `ThumbnailCache` must **iterate buckets in Order and skip hidden
layers**. SVG gains fidelity: `<g opacity="…">` per layer is a 1:1 match for group opacity.

---

## 4. Art-in-notes

### 4.1 The reframe

The separate art surface is **cancelled**. `docs/ARTMODE-ARCHITECTURE.md` (separate `ArtSurface` +
Art Notebook) and the ROADMAP "Art Mode (beta)" headline are superseded by this section. Painting is
a capability of `InkSurface`, delivered in two honest tiers.

### 4.2 Tier 1 — vector paint via pen presets (ships now, the 90% replacement)

`Brush`, `Watercolor`, `Marker`, `Crayon` **already exist** in `PenType` and already render on
`InkSurface` (cs:3305-3415). Ship 3 seeded presets behind a one-time `Library.PaintPensSeeded` bool:
Round Brush (`Brush`, Size 9, Sens 1.2), Watercolor Wash (`Watercolor`, Size 14, Sens 0.8, Stabiliser
0.3), Chisel Marker (`Marker`, Size 12). They are ordinary chips — assignable to pen-row/wheel slots,
edited by the existing `CreatePresetFlyout`, persisted in `Library.Pens`. With Auto-layering they
route to the "Paint" family; combined with layers this delivers "art in notes" with **zero new
surface, zero modes.** This tier needs nothing beyond the layer phases and is the honest replacement
for the cancelled Art Mode.

### 4.3 Tier 2 — raster paint (deferred, gated on two decisions)

Realistically 1–2 months (sparse tiles, VRAM LRU, wet-gesture buffer, atomic writer, undo budget,
export integration) — the largest single item across all tracks, and it has no sync story. **Defer
behind:** (1) is true raster actually required given vector paint + layers? (2) sync posture —
this-device-only badge vs asset-file sync first? Specified so it is ready if wanted:

- **Where pixels live — NOT in `library.json`, NOT in the synced folder** (blocking data-loss fix).
  `LibraryStore.Save` re-serializes the whole 53 MB file on the UI thread every 1.5 s, so inline
  pixels are out. And `LibraryStore.Dir` is routinely OneDrive/Dropbox; a **mutable, 800 ms-debounced
  tile writer** would generate continuous cloud re-upload churn, and two devices painting the same
  tile produce "conflicted copy" PNGs the app never reads → silent stroke loss. **Tiles, `meta.json`,
  and undo snapshots write to a non-synced per-machine location:**
  `%LOCALAPPDATA%\Quill\paint\{sha256(LibraryStore.Dir)}\{layerId:N}\{tx}_{ty}.png`. This abandons the
  "follows the image-assets convention" claim deliberately. Cross-device raster requires real
  content-addressed, immutable, tombstoned asset sync — a hard prerequisite, not an open question.
- **Model:** `PageLayer.Kind=1(Raster)`; 512×512 BGRA premultiplied world-aligned tiles, 1.0
  texel/world-unit; `PenPreset.Engine{0 Vector,1 Raster}` + `RasterBrush{0 SoftRound,1 Airbrush}`.
  v1: SoftRound + Airbrush + destination-out pixel eraser. Smudge/wet-mixing out of v1. Raster layers
  are not lasso-selectable in v1 (lasso skips them, tooltip says so); cross-Kind Merge disabled.
- **Undo:** `PaintTilesAction{ Guid LayerId; List<(int tx,int ty, byte[]? BeforePng, byte[]? AfterPng)> }`
  (null Before = created, null After = erased). Running paint-action byte total capped at **128 MB**;
  overflow trims from the **bottom** of the undo stack (new `UndoManager.TrimBottom` — today's stack
  is unbounded, UndoRedo.cs:571). Vector undo stays unlimited.
- **Exports:** raster layers composite to one PNG over the layer's content bounds (SVG `<image>`, PDF
  image XObject, thumbnail draw), iterated in layer Order.

---

## 5. Objects — custom stamps, `.qpack` format, honest marketplace

### 5.1 Semantics — a stamp, not a live instance

An object is a named, reusable **group of ordinary elements** stored library-side. Placing it
**stamps deep copies** into the page — placed content is indistinguishable from hand-drawn content.
There is deliberately **no `ObjectInstance` element kind.** Rationale is structural: a referenced
instance would touch all ~187 `.Strokes/.Shapes/.Texts` sites, all 27 `IPageAction`s, `SyncLog`, both
exporters, and the `SpatialGrid`; a stamp touches none. Concepts itself stamps (dropped objects become
editable strokes). Accepted consequences: editing a library object never updates placed copies (the
"stamp" mental model); file size grows per placement (mitigated by the 2500-stroke ink cache).

**CreatedTicks fix (minor, but stamping makes it frequent):** `CloneWithPoints` copies `CreatedTicks`,
and `SyncLog.Apply` re-sorts the whole strokes list by `CreatedTicks` on every foreign apply
(SyncLog.cs:414). A stamp carrying authoring-time ticks would sort under today's ink after a sync
round-trip, and two stamps of the same object get identical ticks (nondeterministic z-order under an
unstable sort). **On stamp commit assign each cloned element a fresh `CreatedTicks = UtcNow.Ticks + i`**
(tiny per-element increment for deterministic intra-stamp order). Apply the same to paste/duplicate.

### 5.2 Grouping foundation — `GroupId` field

```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public Guid GroupId { get; set; }
```
on all three element classes (`Guid.Empty` = ungrouped). Selection completion runs one O(n) pass
pulling in every element sharing a selected non-empty `GroupId`, then the existing multi-selection
machinery moves/scales/rotates it. `Group` = Ctrl+G, `Ungroup` = Ctrl+Shift+G (trivial actions
storing `(id, oldGroupId)`). Point-erase fragments inherit `GroupId`+`LayerId`. A stamped object is
simply a pre-made group. **This and `LayerId` are added together as one element-metadata foundation
change** (§8 Wave 0) — same three classes, same `CloneWithPoints` fix, same `PageMetaJson` extension,
same old-file + sync round-trip test, done once.

### 5.3 On-disk layout

`<LibraryStore.Dir>/objects/`: `index.json` (user-object metadata), `mine/<guid>.json`
(one payload each), `thumbs/<guid>.png` (256²), `packs/<packGuid>.qpack` (imported packs, read
directly; thumbnails extracted lazily to `cache/<packGuid>/`). Immediate atomic writes
(temp + `File.Replace`); index self-heals by rescanning `mine/*.json`. Sealed `.qpack` zips in `Dir`
are safe (immutable). Loose `mine/*.json` still get LWW-clobbered with no merge if the same object is
edited on two devices simultaneously — noted, acceptable for a personal library; true object sync is
out of scope.

### 5.4 `.qpack` container format — this must be right the first time

One ZIP format for a single object and a pack (a single object = a pack of one; `.qobj` and `.qpack`
both map to the same reader). ZIP over monolithic JSON: thumbnails browsable without parsing strokes,
binary images without base64 bloat, `System.IO.Compression` is built in.

Entries: `manifest.json` (root, required) · `objects/<guid>.json` per object · `thumbs/<guid>.png`
(256²) · `assets/<sha256>.png|.jpg` for embedded images.

```jsonc
// manifest.json
{ "format":"quill-object-pack", "formatVersion":1, "minReader":1,
  "packId":"<guid>", "name":"", "author":"", "authorUrl":"",
  "license":"<SPDX-id | \"custom\" | \"none\">", "licenseText":"<required iff custom>",
  "attributionRequired":false, "description":"", "created":"<ISO-8601 UTC>",
  "appVersion":"", "cover":"<optional relative path>",
  "objects":[ { "id":"<guid>", "name":"", "tags":[], "file":"objects/<guid>.json",
                "thumb":"thumbs/<guid>.png", "w":320.0, "h":240.0 } ] }

// objects/<guid>.json  (ObjectPayload) — element JSON is EXACTLY the NotePage/SyncLog "J" shape.
{ "formatVersion":1, "id":"<guid>", "w":320.0, "h":240.0,
  "strokes":[…], "shapes":[…], "texts":[…] }
```

Rules that make stages 2–3 additive:
- **Coordinates normalized:** bounds top-left subtracted, payload origin (0,0); `w`/`h` = natural
  world size. (Line shapes shift start only; deltas + `Rotation` preserved.)
- **Payload = a page-slice serialization** — the same serialized shape as page persistence, so there
  is never a second schema or renderer to maintain.
- **Images content-addressed:** `ShapeElement.ImagePath` is rewritten on export to `asset:<sha256>`
  (file copied into `assets/`); on import copied into the notebook media store and rewritten back.
  All zip paths forward-slash relative; absolute paths are a validation error.
- **Versioning contract:** readers **refuse** `minReader > supported` (clear dialog naming the
  required app version); otherwise parse tolerantly — unknown JSON fields and unknown zip entries are
  **preserved on re-share** and ignored, so signatures/receipts/per-object licenses/layer hints are
  additive later.
- **Identity = (`packId`, `objectId`) GUIDs, never name-based**, so store packs can never collide
  with user objects.

### 5.5 Import validation boundary (built once, day one; reused by any future store)

Reject entry names with `..`, leading `/`/`\`, drive letters, or resolving outside the extraction
root (zip-slip). Caps: 4096 entries, 256 MB decompressed total, 32 MB/asset, 64 MB/payload, JSON
depth 64. Only `manifest`/`objects`/`thumbs`/`assets` paths consumed; everything else ignored. Images
re-validated by decoding through the platform decoder into a fresh bitmap (never trust the byte stream
onward). Imported-text RTF sanitized: strip `\object`/`\objdata` OLE payloads, cap 1 MB/text — RTF
parsing is the one genuinely scary surface. Nothing in a pack is executable. Import shows a preview
dialog (name, author, license, count, size, thumbnail grid) **before** committing.

### 5.6 Stage 1 local; marketplace = curated feed only (blocking honesty)

**Stage 1 (ships):** create/save/reuse/import/export objects and `.qpack` files, fully local. The
floating Object Library panel (search + My Objects + per-pack sections + tile grid) opens from OPT
*Panels ▸ Objects*. Placement: drag-out ghost (thumbnail at 60% at natural world size) or tap-to-arm
(mirrors `_blankSpaceOnce`); commit = fresh Guids + fresh `GroupId` + fresh `CreatedTicks`, LayerId =
active/Objects-family layer, one `CompositeAction` so one Ctrl+Z removes the whole stamp; then
auto-selected for immediate handles. Rotation (new, generic to all multi-selections):
`RotateMixedAction` bakes stroke points in place / `Rotation +=θ` for shapes+texts; 15° detents
(capture ±4°, release ±6°), Alt = free, live degree badge.

**Marketplace — a firm deferral, not an open question (critique).** A store with user uploads and/or
payments is a staffed ongoing service: accounts, upload pipeline with automated schema/zip validation
**plus human content/IP review**, server-side re-encode of every image (decoder CVEs), DMCA agent +
takedowns, payment with VAT via a merchant-of-record (Paddle-class), creator payouts + tax forms,
refunds, moderation queue, CDN, pack versioning, legal terms. **Out of scope for this plan; a separate
funded business decision.** What Quill ships as "a marketplace" is the **curated feed**: a "Get packs"
tab fetching a static `feed.json` (`{id,name,author,license,sizeBytes,thumbUrl,downloadUrl,sha256}`)
over HTTPS, sha256-verified, imported through the §5.5 pipeline — no accounts, no payments, no new
attack surface, and genuinely "a marketplace" in the Concepts brush-market sense.

---

## 6. Snapping

One `SnapEngine` consulted at two moments; all radii in **screen DIPs**, divided by `ViewZoom` at
query time so snapping feels identical at every zoom (the `1/ViewZoom` convention the grid already
uses). Candidates come from the existing `SpatialGrid`; texts (not indexed) come from a linear scan
skipped above 256 texts. Hidden layers excluded as targets; **locked layers stay valid targets**
(uneditable but snappable — CAD convention). Budget: 1 grid query + ≤64 candidate checks × 9 probe
points per pointer-move, < 0.2 ms; chrome draws in the selection-chrome pass and never invalidates the
ink layer.

**Master default: while-EDITING snaps ON, while-DRAWING snaps OFF** (the handwriting-protection
divergence from Concepts).

### 6.1 Edit snapping (ON by default)

Three target classes, bias-scored (`score = screenDistDIP − classBias`, lowest wins; ties
key-point > grid > alignment). **Hysteresis: engage at ≤ radius, hold until raw pointer > 12 DIP from
the snapped point** (1.5× exit) to kill boundary flicker.

| Class | Radius | Bias | Targets |
|---|---|---|---|
| Key points | 8 DIP | +2 | stroke first/last; shape 4 corners + 4 edge mids + centre (Line: 2 endpoints + mid); text box corners + centre |
| Grid | 6 DIP | 0 | Square → intersections; Dotted → dots; Lines → Y-only; Isometric → lattice vertices; Triangle → lattice vertices |
| Alignment guides | 6 DIP | 0 | dragged bounds edges/centres align to other elements' bounds edges/centres (Figma smart guides); candidates = viewport-intersecting, cached at drag-start, nearest 64 |

Probe points = the 9 canonical `_selBounds` points (+ both Line endpoints for a single-line selection).
Scaling snaps only the dragged corner (targets = key points + grid) plus a **1.00× object detent**
(engage ±4%, release ±6%). 8 DIP sits above pen jitter (~2–3 DIP) and below the 40 DIP touch floor —
magnetic but escapable; grid 2 DIP weaker than key points so diagram-to-diagram beats paper texture.
Snap corrections mutate the live `_moveDx/_moveDy` / scale factor **before** the draw-time offset, so
`MoveStrokesAction`/`ScaleMixedAction` and their integer-index semantics are untouched.

### 6.2 Drawing snapping (all OFF by default)

`SnapSettings` app-wide in `library.json` (grids are per-page and key off the current page). Home:
the Precision panel (Master + While-drawing group + While-editing group, beside Ruler).

| Mode | Default | Numbers / behaviour |
|---|---|---|
| Auto-complete (endpoint fusion) | OFF | candidate endpoints within **24 DIP** show 5 DIP hollow circles; pen-down within **10 DIP** fuses start; pen-up within 10 DIP of an endpoint or the stroke's own start (close loop) fuses end. Sub-toggle "Active layer only" ON (Concepts parity, scopes candidates only). 10 > 8 because pen-down jitter exceeds drag jitter. |
| Align (soft direction constraint) | OFF | direction captured after **12 DIP** travel as nearest legal direction to the initial bearing; wet stroke renders straight from anchor, length = scalar projection; stabiliser + min-gap bypassed; 620 ms shape-recognizer disabled while active. |
| Allow Turns | ON (when Align) | perpendicular deviation > **14 DIP** AND last-24-DIP bearing nearer a different legal direction → commit a vertex, continue as a new segment (one polyline stroke — the L-wall case). |
| Allow Traceback | ON | projection may shrink the segment; off → length = max reached. |
| Lock (snap-to-grid) | OFF | implies Align; anchor snaps to nearest grid intersection (no radius cap); segment lengths quantize to whole `GridSpacing` multiples so vertices land on lattice points. |

Resulting strokes are ordinary `PenStroke`s (2 points/segment, real pressure at those moments) — they
erase/select/sync/export like any ink, and fountain/calligraphy nib width still renders.

### 6.3 Direction providers + suspend

`IEnumerable<float> DirectionsAt(Vector2 worldPos)` per grid: rectangular family → `{0,45,90,135}`;
Isometric → `{30,90,150}`; Triangle → `{0,60,120}`; Perspective → `{bearing(VPᵢ→P)…}` + 90° (see
§7.4). When the `RulerBtn` angle is set, that angle and +90° prepend and win ties. **Hold Alt =
suspend ALL snapping** for as long as held (Office convention; re-engages on release, resets
hysteresis, never persists). Pen-only users fall back to the Precision Master chip — the barrel button
is already claimed by the context menu (InkSurface.cs:1070) and is not overloaded.

---

## 7. Page sizes, panel types & grid geometry

### 7.1 World-unit scale + optional artboard

**1 world unit = 1 DIP = 1/96 inch at zoom 1.0** (consistent with the raster "1 texel/world-unit ≈
1 px at zoom 1"). Each page carries `UnitsPerInch` (default 96) so physical labels and print/export
scale are exact — Concepts' "drawing scale." Social presets are pixel-native (1 unit = 1 px).

The infinite canvas stays infinite. A page is **`Infinite` (default, today's behaviour — no rect
drawn, panning clamped as now)** or carries an optional **`Artboard { double W, H; }`** — a drawn
rectangle that clips export and anchors "fit". `NotePage.Width/Height` (vestigial, 1500×2200) become
the legacy `Infinite` size and are never migrated. Paper texture (§7.3) is independent of the
artboard and fills the visible region regardless.

### 7.2 Preset tables (world units at 96 u/in; social = px)

**A-series (ISO 216)** — portrait, ×3.7795 from mm:

| | mm | world (w×h) | | mm | world |
|---|---|---|---|---|---|
| A7 | 74×105 | 280×397 | A3 | 297×420 | 1123×1587 |
| A6 | 105×148 | 397×559 | A2 | 420×594 | 1587×2245 |
| A5 | 148×210 | 559×794 | A1 | 594×841 | 2245×3179 |
| A4 | 210×297 | 794×1123 | A0 | 841×1189 | 3179×4494 |

**US / office** (×96 from in): Half-Letter 5.5×8.5 → 528×816 · Letter 8.5×11 → 816×1056 · Legal
8.5×14 → 816×1344 · Tabloid/Ledger 11×17 → 1056×1632 · Executive 7.25×10.5 → 696×1008.

**Large format:** ANSI C 17×22 → 1632×2112 · ANSI D 22×34 → 2112×3264 · ANSI E 34×44 → 3264×4224 ·
ARCH C 18×24 → 1728×2304 · ARCH D 24×36 → 2304×3456 · ARCH E 36×48 → 3456×4608. (A1/A0 above serve the
metric large-format slots.)

**Cards:** Business US 3.5×2 in → 336×192 · Business EU 85×55 mm → 321×208 · Index 3×5 → 288×480 ·
Index 4×6 → 384×576 · Index 5×8 → 480×768 · Postcard A6 148×105 mm → 559×397 · Playing 2.5×3.5 in →
240×336.

**Social media (px = world units, `UnitsPerInch` ignored):** IG square 1080×1080 · IG portrait
1080×1350 · IG/TikTok/Reel/Shorts story 1080×1920 · Facebook post 1200×630 · Facebook cover
1640×624 · X post 1600×900 · LinkedIn post 1200×627 · YouTube thumbnail 1280×720 · Pinterest pin
1000×1500.

### 7.3 Panel types — paper + preset bundles in the page-background menu

Additive per-page fields resolved exactly where background/grid resolve today (`NewPage`,
cs:2621-2631) and carried by `PageMetaJson` (§3.3):
```csharp
NotePage.Paper : string?      // null = smooth; WhenWritingDefault
Notebook.DefaultPaper, Library.DefaultPaper
```
v1 paper ids (tileable greyscale PNG, 512², ~60 KB, in app Assets): `grain`, `canvas`, `coldpress`,
`laid`. Rendered in `DrawRegion` between `ds.Clear(bg)` (:2962) and `DrawGrid` (:2968) as a
`CanvasImageBrush` (Wrap) **inside the world transform** so it zooms with content; opacity 0.10 on
light backgrounds, 0.16 on dark (the `DrawGrid` auto-contrast spirit).

The `PageSettingsBtn` flyout (xaml:255-321) gains a top **Panel** row of 8 one-tap preset bundles
`{Background, Grid, GridSpacing, GridColor, Paper, Artboard?}`:

| Panel | Bg | Grid | Spacing | Paper | Artboard |
|---|---|---|---|---|---|
| Note | white | Lines | 32 | – | – |
| Grid | white | Square | 32 | – | – |
| Dot | ivory | Dotted | 32 | – | – |
| Blank | ivory | None | – | – | – |
| Sketch | ivory | None | – | grain | – |
| Canvas | #F2EEE4 | None | – | canvas | – |
| Watercolor | white | None | – | coldpress | – |
| Blackboard | #1C1C1A | None | – | – | – |

Existing Background/Grid rows stay as à-la-carte overrides; add a 5-swatch Paper row; the "use for all
new pages" / "notebook default" promotion buttons extend to include Paper. Exporters render paper.

### 7.4 Construction grids

**GridType enum append — single owner, prerequisite (minor cross-track hazard).** Today
`{None=0,Dotted=1,Square=2,Lines=3}` is consumed by an index-cast (`_curPage.Grid =
(GridType)GridRadios.SelectedIndex`, cs:5290). Appending `Isometric=4, Triangle=5` requires converting
that index-cast to **Tag-based mapping** first. Assign this one change to a single owner before either
the isometric snapping provider (§6.3) or an isometric paper preset is built. Perspective is **not** a
GridType — it is a separate `PerspectiveDef?` overlay (below) that coexists with any GridType.

All grids draw in world space over the visible rect, line width `1/ViewZoom` (constant 1 screen px),
auto-contrast alpha, with the >25000-cell spacing-doubling guard — the `DrawGrid` conventions
(cs:3220-3263). `s` = `GridSpacing`.

**Isometric (`=4`).** Centered-rectangular point lattice: dots at `(m·0.8660s, n·0.5s)` for integer
`m,n` with `(m+n)` even. Nearest-neighbour edges are 30°, 150°, 90° — the three line families. Draw
three clipped line families at {30°, 90°, 150°} (or `FillCircle` at the lattice points for a dot
variant). Snap directions `{30, 90, 150}`.

**Triangle (`=5`).** Equilateral tiling (isometric with a horizontal axis). Points at
`(m·s + (n mod 2)·s/2, n·0.8660s)`. Nearest neighbours: horizontal `(±s,0)` = 0°, and `(±s/2,
±0.8660s)` = ±60°. Line families / snap `{0, 60, 120}`. Row height `0.8660s`, alternate rows offset
`s/2`.

**Perspective overlay.**
```csharp
class PerspectiveDef { double HorizonY; List<Point> Vps; int RayCount = 24; }
// on NotePage: PerspectiveDef? Perspective  (null = none; WhenWritingDefault; in PageMetaJson)
```
Guide fan per VP: cast `RayCount` rays spanning the angular range the viewport corners subtend from
that VP, Liang-Barsky–clipped to the viewport (≤ 3×24 = 72 segments — trivial). Placed in the
page-settings flyout by dragging pins + a horizon handle (panel-types work); snapping consumes
`DirectionsAt`.

| Kind | VPs | Guides drawn | `DirectionsAt(P)` (snap set) | Lock mode |
|---|---|---|---|---|
| 1-point | `[VP]` on horizon | horizon; ray fan from VP; verticals `X=k·s`; horizontals `Y=k·s` | `{ bearing(VP→P), 0°, 90° }` | Align only |
| 2-point | `[VPL, VPR]` on horizon | horizon; fans from VPL & VPR; verticals stay true | `{ bearing(VPL→P), bearing(VPR→P), 90° }` | Align only |
| 3-point | `[VPL, VPR, VPZ]` (VPZ off-horizon) | fans from all three; nothing stays straight | `{ bearing(VPL→P), bearing(VPR→P), bearing(VPZ→P) }` | disabled (no lattice) |

For 1-/2-point, edit-snap also snaps a probe's Y to `HorizonY` within 6 DIP. Lock mode greys out on
perspective (no lattice exists) — Align only, matching §6.2.

---

## 8. Delivery order — one cross-track plan, new home first, demolish last

The three source designs each phased independently; executed literally they would demolish the top bar
while its tenants are still moving in, and re-touch the same files repeatedly. **This is the single
committed order.** Build the new home first; remove the bar last, behind a hidden escape hatch.

### 8.1 The 15 user requests

1. Radial pen row (as an option)
2. Move lasso to the pen row
3. Remove the top bar
4. One options button (top-right) holding every relocated command
5. Layers
6. Snapping
7. Custom objects (create + reuse)
8. Marketplace
9. Scrap the separate Art Mode
10. Integrate painting into the note surface
11. Panel/paper types in the background menu
12. Page-size / artboard presets (A-series, US, large-format, cards, social)
13. Isometric & triangle construction grids
14. 1/2/3-point perspective grids
15. Concepts-ergonomics layer (handedness flip, density ladder, gesture bindings, undo/redo repositioning)

### 8.2 Waves

| Wave | Item (effort) | Requests | Notes / gate |
|---|---|---|---|
| **0 — foundations, bar untouched** | 0A Element-metadata foundation: `LayerId`+`GroupId` on the 3 classes, `[JsonIgnore(WhenWritingDefault)]` on both, fix `CloneWithPoints`, extend `PageMetaJson` **and** the `SyncLog.Apply "pg"` copy lines (+ GridColor), rotation-safe (L) | 5,7 prep | Gate: byte-identical old-file load; 3-layer+paper `MergeForeign` round-trip. **Coordinate with in-flight SyncLog delete-op fix.** |
| | 0B Linear pen-row tool segment (lasso/text/shape/space/comment + undo/redo + touch-draw chip); `SelectTool`→`RefreshToolChips`; drop the Pen/Eraser gate (M) | 2 | Pure addition; every tool reachable from row AND bar. |
| | 0C `ToolUiChanged` funnel + `PenRowStyle` plumbing (S) | 1 prep | No UI yet. |
| **1 — dock into the options button, bar still present** | 1A Slim caption strip + OptionsBtn + full menu, **bar retained** for redundant access; async-startup gate (L) | 4 | Gate: every menu item fires the identical handler as its bar twin. |
| | 1B Layers core: buckets render + `DrawRegion` restructure, Auto/Manual (default flat, no reorder), panel, 5 undo actions, exporters iterate Order, opacity=1.0 (XL) | 5 | Gate: 0-layer page byte-identical + pixel-identical; 2500-stroke layered page fps benchmarked. |
| | 1C Panel/paper types + preset bundles + paper row + promotion; artboard field (M) | 11 | Independent of layers. |
| | 1D Page-size/artboard presets in page settings (S) | 12 | |
| | 1E Edit-snapping MVP (key points + grid + Alt suspend) + selection rotation + alignment guides (L) | 6 (part) | |
| **2 — dock the rest, THEN demolish** | 2A Object library stage 1 local + `.qpack` + hardened importer + stamping (needs layers for stamp targeting) (XL) | 7 | Gate: restart→stamp twice→one Ctrl+Z removes a stamp; malformed zips rejected. |
| | 2B Layer opacity + Focus Mode + ink-cache run selection (L) | 5 (finish) | Only frame-cost part; benchmark translucent layer. |
| | 2C **Remove the top bar** (collapse Row 0 to the strip, retire `ApplyToolbarVisibility`/`OptionalTools`, rewire `HideUi`/`ApplyTouchMode`, migration TeachingTips) — behind a hidden "Classic bar" setting for one release (L) | 3 | Gate: the full §2.5 non-regression checklist. **Demolition happens only here, after every command/panel has a proven home.** |
| **3 — deferred / gated** | 3A Radial `ToolWheel` (persistent form) + transient summon + UIA (XL) | 1,15 | Opt-in; the state funnel already exists. QA-heavy gesture-conflict surface — must not precede the restructure. |
| | 3B Vector paint presets **now** (S); raster engine **deferred** behind the two §4.3 decisions (XL if taken) | 9,10 | Vector tier is the honest Art-Mode replacement. |
| | 3C While-drawing snapping + Isometric/Triangle + 1/2/3-pt perspective (GridType append single-owner first) (L) | 6 (finish),13,14 | |
| | 3D Marketplace = curated feed only (M); upload/payment store = separate funded decision (out of scope) | 8 | |
| | 3E Concepts ergonomics: handedness flip, Compact density rung, gesture bindings (M) | 15 | Handedness flip is load-bearing for the wheel, ship with 3A. |

### 8.3 Already in flight (do not collide)

- **SyncLog delete-op Parent fix** — separate session, same `SyncLog.Apply` method our 0A patches. Sequence 0A after it or merge carefully.
- **Liquid-glass v2**, **Menu-animations** — awaiting visual sign-off; both attach at App.xaml presenter styles and are orthogonal to this work.

### 8.4 Deferred, with reason

- **Raster paint (4.3)** — 1–2 months, no sync story; gated on "is it needed given vector paint + layers?" and the sync-posture decision.
- **Radial wheel + transient summon (2.3/2.4)** — the user asked for it as *an option*; it is a new custom control with a heavy four-device gesture-conflict QA surface. Ships after the restructure is proven in daily use.
- **Upload/payment marketplace (5.6)** — a staffed service, not a dev deliverable.
- **Text z-ordering in layers (3.9)** — needs a Win2D text-rendering migration; deliberately not promised.

---

## 9. Open questions (genuine decisions only)

1. **Touch-draw placement.** This plan keeps "Draw with touch" as a **persistent pen-row chip** (not
   a buried menu item) and mirrors it in Settings. Confirm that is right, or should it *also* be a
   wheel-assignable slot?
2. **Transition safety.** Keep the hidden "Classic top bar" setting for one release as an escape
   hatch (nearly free — all handlers survive), or delete the bar outright with no way back?
3. **Raster paint — is it wanted at all?** With layers + seeded vector Brush/Watercolor/Marker
   presets covering most "art in notes," is true raster (airbrush, pixel erase, wet buffer — the most
   expensive single item) a requirement or a later tier? If wanted: local-only with a badge, or must
   asset-file sync land first (delays it ~1 phase)?
4. **Highlighter stacking default.** This plan sets Auto-layering to put **highlighter behind ink**
   (the common note convention) so a highlight never obscures the annotation over it. Agree, or do
   you want highlight-over-ink?
5. **Delete-layer behaviour.** Delete removes the layer **with its content** behind a counted
   confirmation (Concepts' behaviour, Merge Down offered first as the preserving path) — or should
   Delete always auto-merge content down and only ever remove empty layers?
6. **Wheel default position & handedness.** Lower-left, drag-anywhere, auto centre-line handedness
   flip (Concepts convention) — but today's pen row docks bottom-centre. Confirm lower-left as the
   out-of-box default.
7. **Pack-export licensing UX.** When a user exports a pack to share, default the license to "none"
   (all rights reserved) with an optional picker, or actively prompt for a license + author name to
   seed a healthier sharing ecosystem?


---

## Decisions (user, 2026-07-21)

All seven open questions are now settled:
1. Touch-draw stays a persistent pen-row chip (default taken).
2. Classic top bar kept as a hidden escape-hatch setting for one release.
3. Raster paint: later tier, this-device-only with a visible badge; vector
   paint ships first.
4. Highlighter auto-layers BEHIND ink.
5. Layer delete removes content behind a counted confirmation, with Merge
   Down offered first.
6. Wheel defaults lower-left with auto handedness flip (default taken).
7. Pack export defaults to all-rights-reserved with an optional license
   picker (default taken).
