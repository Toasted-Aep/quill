# Quill — roadmap

Updated 2026-08-10. The previous roadmap (2026-07-20) is superseded: it had a
from-scratch Art Mode as the headline, and the project has since turned to
converting the whole shell to a Concepts-style tool surface. The art work is not
abandoned — the oil-paint substrate is built and verified on a branch — but it
is no longer what the next release is about.

Ordered by state: shipped, in flight, next, later, and the risks that are known
and unowned.

---

## Shipped since the last roadmap

**Theme driven by the page.** Quill no longer has a light theme and a dark
theme; it derives its whole palette from the current page's ground colour, so a
blue page produces blue chrome and a kraft page produces brown chrome. Light and
dark fall out of the ground's luminance rather than being a setting. Derivation
shifts lightness in CIELAB rather than HSL — an HSL shift greyed coloured
grounds out. Verified against six measured proof points.

**Paper textures, rebuilt from scratch.** Nine grounds (Plain White,
Transparent, Crumpled, Lightweight, Heavyweight, Rippled, Blueprint, Brown
Paper, Darkprint) with a measurement harness that links the shipping source
rather than copying it. The previous generation was *mathematically invisible* —
a textured page measured the same per-pixel σ as a blank one — because every
generator blended with Overlay, whose output range on a near-white ground is
about 8%, and the grey matrix averaged three independent noise channels,
dividing σ by √3.

**The radial tool dial.** Ten sectors, pop-out active cell, marks upright at
every angle, size label outboard and stroke inboard, per-pen colour on the ring's
inner edge. The inner disc is four equal quadrants — size, opacity, stability,
and a bottom quadrant halved between undo and redo. Hover outlines are generated
from the same constants as the hit test, after the two drifted far enough that
one plate lit 38.5% of the region that actually responded.

**The COPIC wheel.** All 17 rings of the full palette render; the outer edge is
an accumulation of what the palette needs rather than a target, so it runs off
the window and is reached by rotation. Mouse-wheel and side scroll rotate it,
verified to reach the outermost swatch of the deepest column. Centred on the
dial's colour dot to Δ 0.00 DIP. HSL and RGB are gradient arc sliders with
typeable value boxes; switching faces plays a sequenced in/out animation.

**Panels.** Settings is a floating window again, split across Workspace ·
Interaction · Gestures · Stylus, and no longer rebuilds wholesale on every click
— it rebuilds one section and holds scroll position. A Brushes library with a
live preview strip; an Objects library; an Export pane; corner-only resize
grips; panels constrained to stay clear of both top-bar clusters. Opening the
colour wheel dims the corner chrome by opacity rather than laying a scrim over
the page.

**Assignment.** Right-clicking a pen-row cell or a dial sector opens one library
carrying both brushes and tools, aimed at that slot; a second right-click
retargets the open panel in place rather than stacking another; a page press
dismisses it without swallowing the press.

**Data safety.** A tolerant converter so a single renamed field can never make
the library unloadable; `QUILL_DATA_FOLDER` isolation that relocates the
settings anchor too; the page-background contrast flip made reversible instead
of destroying deliberate text colours on every picker drag.

---

## In flight

- **`FloatingWindow` stale reopen.** `Show()` rebuilds only when content is
  null and `RefreshContent()` only when already open, so `Refresh(); Show();` on
  a previously-opened window rebuilds nothing and shows a stale tree. Settings,
  Export and Objects all share the shape.
- **Chrome relocation.** Dictation to the writing bar; recording and history
  into the Quill dropdown, history as a right-docked panel; mouse modes into the
  Interaction page as circles; microphone and mouse-mode buttons off the top bar
  entirely. Objects-library shapes drawn with the live pen's style and colour.

## Next

- **Grid and guideline editor** — a proper pane with a live preview and a back
  link: presets, vanishing points, horizon tilt, movable centre, density, line
  weight, colour, opacity, orientation, confine-to-artboard. Guidelines move
  under Grid Type.
- **Text-mode quick actions** above the text bubble.
- **A `Colors` tab** beside Brushes: current colour, COPIC/HEX/RGB/HSB readouts,
  user palettes, and dynamic palettes (analogous, monochromatic, complementary,
  shades, triads, most-used, recent).
- **Ruler and Mix as selectable tools** — the ruler tiltable by two-finger
  gesture with a typeable angle; Mix combining two colours through the spectral
  mixer, and diluting toward transparency rather than tinting when one of them
  is the page ground.
- **The missing COPIC codes** — the wheel holds 316 of the 358 Sketch range.
  Only real marker codes, calibrated the same way; no interpolated swatches.

## Later

- **Layers.** The data model is the blocker for PSD export, per-layer
  visibility, selection scoping and per-object rows in the Objects library.
- **Oil paint.** Branch `oilpaint`: tile store, impasto via a distant-specular
  pass, crash-safe `.artq` v2. Phases 1a/1b/2 built and verified, never merged.
- **Smudge**, on the oil raster substrate.
- **A pen library** proper — Krita/Fresco/Concepts brush dynamics behind the
  shell that now exists.
- **Tilt / canvas rotation.** Audited rather than guessed: 62 inline
  screen↔canvas conversions and 51 axis-aligned rect sites in a 7,180-line
  file. Estimated 3–5 days plus a full input-regression pass.
- **A user system** — accounts, sharing, collaboration — and a web viewer.

---

## Known risks, unowned

- **SyncLog replay.** Two builds sharing `Documents\Quill` actively replay
  against each other; cursor and device-id writes are non-atomic, and a torn
  cursor triggers a full replay that can resurrect erased strokes.
- **`File.Replace(tmp, path, null)`** with no backup parameter in the sync path.
- **PDF import rasterises** — imported text is not selectable; 2000-page cap.
- **The MSIX is signed with a self-signed dev cert.** Public distribution needs
  a real code-signing certificate or the Store.
- **Thumbnail pruning** — `thumbs/` keeps PNGs of deleted pages indefinitely.
- **Vector export drops per-run text colour**, flattening to the page ink
  colour; the canvas draw path has the same limitation and both want fixing
  together.
