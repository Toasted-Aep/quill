# Quill — roadmap

Updated 2026-08-17. Supersedes the 2026-08-10 roadmap, which is now mostly
"shipped": the grid editor, the tool assignment work and the Concepts-style
chrome have landed, and the project's centre of gravity has moved to the
fullscreen shell and to measuring the perspective presets properly rather than
reconstructing them from screenshots.

Ordered by state: shipped, in flight, next, later, and the risks that are known
and unowned.

---

## Shipped since the last roadmap

**The grid and guideline editor.** A pane with a live preview and a back link:
presets, vanishing points, horizon, movable centre, density, line weight,
colour, opacity, orientation. Vanishing points are constrained to the horizon
and rotated by an arc control rather than dragged freely. The editor now
follows a page switch instead of writing its spec to whichever page happened to
be open — the earlier version silently destroyed the *destination* page's grid,
which an acceptance test written the obvious way passes.

**Vanishing points placed by quartering a reference frame.** Each page stores
the viewport as it stood on its first painted frame, and every preset is
measured onto a quarter mark of that frame rather than to the window edge. So
the same preset gives the same composition on a laptop and on a large monitor.
Proven not to drift under a pan to (900, 700) at 2.4× zoom.

**Three tools that were not tools.** Eyedropper, ruler and Mix became
selectable, assignable to a dial sector or a pen-row cell through the existing
route. The ruler left the top bar — a tool you select does not also need a
toggle — and takes its angle from a two-finger twist or a typed value.

**Mix dilutes rather than tints.** Two pigments go through the spectral mixer;
a pigment and the page ground go through a new path that holds all three
channels bit-for-bit and moves only alpha. A hue-lerp toward the ground makes a
flat opaque colour that only looks right on plain white — on brown paper, blue
at 50% lerps to `#577AA0` opaque, where dilution keeps `#1E4FD0` at half alpha
and lets the grain read through.

**The star in the colour wheel** opens the `Colors` tab — current colour, the
COPIC/HEX/RGB/HSB readouts, user palettes and dynamic palettes.

**Panel theming.** `Panel` rides a luminance ramp rather than a light/dark
switch, carrying the ground's hue at reduced chroma. Darkprint is a dark grey
rather than OLED black; Plain White gained the faint grain it was missing.

---

## In flight

Four branches, none merged. `main` is at `3baff80`.

- **`fullscreen-chrome`** — the fullscreen shell. Two authored marks, the
  reshaped top-bar cluster, a hover-revealed window-control strip that slides
  down from the screen edge, `PRO` parked behind a flag that still compiles, and
  the caption row folding away so the app's own bar can reach the screen top.
  Six of seven proofs pass on screen, several measured rather than eyeballed —
  including ink surviving a stroke at the very top of the canvas, and the slide
  demonstrably reversing mid-flight rather than snapping open then closing.
  Outstanding: the format bar's clearance is arithmetic, not measured.
- **`panel-inset`** — panels remember their *distance from the side they are
  anchored to*, not an absolute offset, and clamping is non-destructive: a panel
  squeezed by a small window returns to its chosen size and gap when the room
  comes back. This retires a one-way clamp that had been accepted as a
  limitation. Built and reasoned; **not yet seen on a screen**.
- **`dial-readouts`** — the opacity and stability values lifted clear of the
  undo/redo arrows, which they overlapped by 9 × 6 DIP. Measured on screen and
  confirmed.
- **`claude/intelligent-chaplygin-44b127`** — `StartFullscreen` renamed to
  `StartMaximised`, which is what it has actually done since `8105f60`, with
  migration for both the library and the settings mirror.

## Next

- **The 19 perspective presets.** Concepts keeps a *separate list per grid
  type* — 1-Point has 2, 2-Point has 9, 3-Point has 9, each ending in `Custom`.
  There is no single 24-entry catalogue; an earlier reconstruction of one was
  wrong. The geometry must be measured from the real app at a frozen viewport
  and recorded as fractions of the reference frame, because pixel figures do not
  survive a change of screen. Enumeration is done; measurement is not.
- **Help.** Specified in the original chrome pass and never built. The `?` mark
  does not exist anywhere in the code.
- **The missing COPIC codes** — the wheel holds 316 of the 358 Sketch range.
  Only real marker codes, calibrated the same way; no interpolated swatches to
  even a ring out. Gated on a before/after review.
- **Text-mode quick actions** above the text bubble.
- **Panel-meets-panel.** The inset model handles a window too small; it does not
  yet handle a panel meeting another panel or a dock, because `FloatingWindow`
  has no access to the dock width. The hooks are in the right places.

## Later

- **Layers.** The data model is **done and unblocked** — branch `layers-model`,
  reference §18, proved by `tools/LayerRoundTrip` (69 checks). Membership is an
  int key on the element where 0 means the base layer, so an existing library
  gains zero bytes and there is no load-time migration to go wrong. What is left
  is the four features it was blocking, each of which is now one call:
  `PageLayers.InOrder` for PSD export, `Layer.Hidden` + `EffectiveOpacity` for
  per-layer visibility, `PageLayers.CanSelect(page, key, scope)` for selection
  scoping, `PageLayers.Rows` for the Objects library — plus the panel itself,
  which was deliberately not built.
- **Oil paint.** Branch `oilpaint`: tile store, impasto via a distant-specular
  pass, crash-safe `.artq` v2. Built and verified, never merged.
- **Smudge**, on the oil raster substrate.
- **A pen library** proper — brush dynamics behind the shell that now exists.
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
- **Startup does not maximise.** The app opens windowed with the setting true in
  both stored copies. The rename above corrects the *name*; the behaviour is
  unexplained. Suspects: the presenter not being an `OverlappedPresenter` at the
  call, so a guarded `if` no-ops invisibly; or saved window bounds being
  reapplied over the maximise.
- **A `stackalloc` inside a loop** in the stroke path — hoisted on a branch, not
  yet on `main`.
- **PDF import rasterises** — imported text is not selectable; 2000-page cap.
- **The MSIX is signed with a self-signed dev cert.** Public distribution needs
  a real code-signing certificate or the Store.
- **Thumbnail pruning** — `thumbs/` keeps PNGs of deleted pages indefinitely.
- **Vector export drops per-run text colour**, flattening to the page ink
  colour; the canvas draw path has the same limitation and both want fixing
  together.

---

## A note on verifying this app

Two traps have each cost a day of work and are worth knowing before writing any
automated check.

**A mouse cannot draw in Quill** unless Touch draw is enabled — a pen tool with
a non-pen pointer routes to a selection handler that commits nothing. An
injected drag therefore inks *nowhere*, which is indistinguishable from the
canvas swallowing the stroke. Always run a control stroke through the middle of
the canvas before concluding that a stroke test failed.

**An incremental build reports zero warnings it did not earn**, because it skips
the C# compile entirely. Always pass `--no-incremental` when a warning count is
part of the evidence.
