# Quill — roadmap

The previous roadmap is complete: every open item (spatial index, async load
phase 2, gallery→page connected animation, oplog compaction, keyboard presets,
cover thumbnails, per-run export formatting, i18n groundwork) shipped in the
Track C batch, alongside five new glow patterns (Aurora, Shimmer, Ember,
Chase, Heartbeat). This roadmap is organised around what comes next: Art Mode.

## Art Mode (beta) — the headline

Built from scratch on a research dossier (docs/ARTMODE-RESEARCH.md: 115
findings across brush engines, canvas architecture, compositing, pen input,
undo/formats, tools, colour). The earlier fork-based attempt was scrapped.

Decisions locked with the user:
- Scope: Tier A ~10 tools (brush, eraser, soft-edge fill, eyedropper,
  layers + blend modes, selection/transform, colour picker, pan/zoom/rotate +
  flip, undo, export) — the full sketch→line→flat→shade→adjust→export loop.
- Pixel formats: hybrid — 8-bit gamma premultiplied for paint layers, FP16
  linear only for future simulation buffers. Conversion points must be
  explicit in the design.
- Pigment mixing behind an interface; licence-clean spectral default
  (Kubelka-Munk + RGB→reflectance upsampling), Mixbox droppable later.
- Natural media (impasto relight first, then wet map) is post-beta.

Phases (each independently shippable, build- and launch-verified):
1. **Design** — docs/ARTMODE-V2-DESIGN.md via competing architectures +
   judge panel + adversarial critique. Supersedes and deletes the stale
   docs/ARTMODE-ARCHITECTURE.md.
2. **Core surface** — sparse 128×128 tile store (CPU-authoritative), layer
   model, above/below composite cache, Art notebook type in the gallery.
3. **Stroke pipeline** — PenSample normalisation, intermediate-point drain
   (reverse order!), one-euro + rope stabilisation, Catmull-Rom + arc-length
   resample, dab engine with partial-dab carry, stroke scratch buffer.
4. **Undo + format** — tile COW snapshots with a byte budget; crash-safe
   .artq v2: temp file + flush-to-disk + atomic replace, autosave journal.
   Writing over the live file is forbidden (it destroyed three paintings).
5. **Tier-A tools** on the core.
6. **Colour** — picker, palettes, premultiplied/linear correctness.
7. **Post-beta** — impasto height + relight, wet map, pigment mixing.

## In flight, needs visual verification

- **Liquid glass v2** (branch `liquid-glass`): height-field refraction
  (DisplacementMapEffect) + normal-based rim lighting (DistantSpecularEffect)
  replacing flat acrylic. Compiles and runs; refraction not yet confirmed on
  screen. Bevel width / specular exponent will need tuning by eye.
- **Menu animations** (branch `menu-animations`): open rise/scale/fade,
  implicit close animation, staggered item cascade, hover nudge — attached at
  the two presenter styles so all ~70 menus inherit it. Compiles; unverified.

## Follow-ups from the Track C batch

- i18n completion: gallery strings, the 113 XAML tooltips + 51 menu literals
  (need x:Name + an ApplyLanguage assignment pass), dialogs, F1 sheet.
  Turkish/Italian translations need a native-speaker pass.
- Per-run text COLOUR in vector export (runs now carry size/font/bold/italic;
  colour is still flattened to the page ink colour — same limitation as the
  canvas draw path).
- PDF italic (needs per-run Tm skew without losing Tj advancement) and real
  bold faces in FontSubsetter (currently synthesised via stroke).
- Thumbnail pruning: thumbs/ keeps PNGs of deleted pages forever.
- RenderPageThumbnail draws Line/Arrow shapes as rectangles.
- SyncLog: delete ops never set Parent (pre-existing; flagged during C4).
- Oplog vs. backup-restore: restoring library.json from a backup resurrects
  externally-deleted elements on the next merge. Decide whether restores
  should stamp a generation the oplog respects.
- Undo of a SyncLog foreign-merge upsert relies on the count-drift safety net
  rather than direct index maintenance (known, benign, documented in C1).

## Known rough edges (carried)

- PDF import rasterises (imported text isn't selectable); 2000-page cap.
- The MSIX is signed with a self-signed dev cert — public distribution needs
  a real code-signing cert or the Store.
- Collaboration Stage 2 (live relay server) remains future work; Stages 0+1
  (oplog + synced folder) shipped, now with compaction.
