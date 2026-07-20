# Quill — roadmap

Updated 2026-07-20. The previous roadmap is fully shipped: the Track C batch
closed every open item (spatial index, async load phase 2, gallery→page
connected animation, oplog compaction, keyboard presets, cover thumbnails,
per-run export formatting, i18n groundwork) and added five glow patterns
(Aurora, Shimmer, Ember, Chase, Heartbeat). What follows is ordered by state:
happening now, next up, then later.

## Now — in flight

- **Art Mode design** (queued behind the session-capacity reset): produce
  docs/ARTMODE-V2-DESIGN.md from the research dossier via competing
  architectures, a judge panel and an adversarial critique. Supersedes and
  deletes the stale docs/ARTMODE-ARCHITECTURE.md. Research itself is done
  and committed (docs/ARTMODE-RESEARCH.md — 115 findings, 141 pitfalls).
- **SyncLog delete-op Parent fix** (running in a separate session): delete
  ops were written without Parent, risking misapplied deletions on a second
  device. Fix + simulated-two-device verification.
- **Liquid glass v2** (branch `liquid-glass`, awaiting visual sign-off):
  height-field refraction (DisplacementMapEffect) + normal-based rim
  lighting (DistantSpecularEffect) replacing flat acrylic. Compiles and
  runs; the refraction has not been confirmed on screen, and bevel width /
  specular exponent will need tuning by eye against a real page.
- **Menu animations** (branch `menu-animations`, awaiting visual sign-off):
  open rise/scale/fade, implicit close animation, staggered item cascade,
  hover nudge — attached at the two presenter styles in App.xaml so all ~70
  menus and every flyout inherit it with no call-site changes.

## Next — Art Mode (beta), the headline

Built from scratch on the research dossier; the earlier fork-based attempt
was scrapped and reverted.

Decisions locked with the user:
- Scope: Tier A ~10 tools (brush, eraser, soft-edge fill, eyedropper,
  layers + blend modes, selection/transform, colour picker, pan/zoom/rotate
  + flip, undo, export) — the complete sketch→line→flat→shade→adjust→export
  loop, so a real piece can be finished in the beta.
- Pixel formats: hybrid — 8-bit gamma premultiplied for paint layers, FP16
  linear only for simulation buffers. The design doc must name every
  conversion point between the two.
- Pigment mixing behind an IPigmentMixer interface; licence-clean spectral
  default (Kubelka-Munk + RGB→reflectance upsampling), Mixbox droppable
  in later if licensing is resolved.
- Natural media is post-beta (impasto height + relight first — one shader
  pass, biggest payoff — then wet map, then pigment simulation).

Implementation phases (each independently shippable, build- and
launch-verified before the next starts):
1. **Core surface** — sparse 128×128 tile store (CPU-authoritative), layer
   model, above/below composite cache, Art notebook type in the gallery.
2. **Stroke pipeline** — PenSample normalisation, intermediate-point drain
   (reverse order!), one-euro + rope stabilisation, Catmull-Rom +
   arc-length resample, dab engine with partial-dab carry, stroke scratch
   buffer so self-crossing strokes never double-composite.
3. **Undo + format** — tile copy-on-write snapshots under a byte budget;
   crash-safe .artq v2: temp file + flush-to-disk + atomic replace, plus an
   autosave/recovery journal. Writing over the live file is forbidden — the
   scrapped attempt destroyed three real paintings exactly that way.
4. **Tier-A tools** on that core.
5. **Colour** — picker, palettes, premultiplied/linear correctness.
6. **Export & polish** — flatten/export paths, gallery/thumbnail
   integration, settings.

## Later

**Art Mode post-beta**
- Impasto height field + relight; coarse wet map; pigment-space mixing.
- Smudge, gradient tool, adjustment one-shots, layer groups (Tier B).

**Carried follow-ups from Track C**
- i18n completion: gallery strings, the 113 XAML tooltips + 51 menu
  literals (need x:Name + an ApplyLanguage assignment pass), dialogs, the
  F1 sheet. Turkish/Italian translations need a native-speaker pass.
- Per-run text COLOUR in vector export (size/font/bold/italic now carried;
  colour still flattens to the page ink colour — same limitation as the
  canvas draw path, fix both together).
- PDF italic (needs per-run Tm skew without losing Tj advancement) and
  real bold faces in FontSubsetter (bold is currently synthesised).
- Thumbnail pruning: thumbs/ keeps PNGs of deleted pages forever.
- RenderPageThumbnail draws Line/Arrow shapes as rectangles.
- Oplog vs. backup-restore: restoring library.json from a backup
  resurrects externally-deleted elements on the next merge. Decide whether
  restores should stamp a generation the oplog respects.
- Undo of a foreign-merge upsert relies on the spatial index's count-drift
  safety net rather than direct maintenance (known, benign).

**Standing rough edges**
- PDF import rasterises (imported text isn't selectable); 2000-page cap.
- The MSIX is signed with a self-signed dev cert — public distribution
  needs a real code-signing cert or the Store.
- Collaboration Stage 2 (live relay server); Stages 0+1 shipped, now with
  compaction.
