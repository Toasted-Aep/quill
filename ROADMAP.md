# Quill — roadmap for the next release

Ideas and fixes queued for the next cycle, roughly ordered by impact.

## Performance & core engine

- **CanvasVirtualControl swap**: replace `CanvasControl` with `CanvasVirtualControl`
  for true region-based invalidation on huge pages (the static ink cache covers
  most cases today, but virtualisation would remove the 4096px cache ceiling
  and the rebuild hitch when panning far).
- **Wet-ink smoothing**: optional Catmull-Rom smoothing / stabiliser strength
  slider for shaky-hand input; per-pen pressure curve editor.
- **Background autosave off the UI thread**: serialisation currently runs on
  the dispatcher; move `LibraryStore.Save` serialisation to a worker.

## Ink & tools

- **Shape recognition ("draw a rectangle, get a rectangle")**: after a stroke
  closes on itself, offer to snap it to the nearest shape.
- **Selection scaling**: lasso selections currently move — add corner handles to
  scale/rotate the selected ink like a shape.
- **Stroke eraser preview**: highlight what the eraser will delete before lifting.
- **Pen gestures**: double-tap the stylus barrel to switch pen ↔ eraser
  (Wacom/Surface pens expose this via `PenDevice`).

## Tables & text

- **Cell merge / split** and per-cell borders + fill colours (Word parity, part 2).
- **Table header row styling**: bold first row toggle.
- **Text style presets**: save a font/size/colour combo as a named style chip.
- **Markdown paste**: pasting markdown creates formatted rich text.

## Equations & maths

- **Native equation layout**: render LaTeX to proper stacked layout offline
  (own layout engine or bundled KaTeX), replacing the Unicode flattening.
- **Ink-to-math v2**: piecewise recognition of superscripts/fractions from ink
  geometry instead of plain line text.
- **Graphing calculator on the page**: drop a live graph object onto the canvas
  fed by the calculator's variables.

## Export & interop

- **Vector PDF fonts**: embed the actual note fonts (TTF subsetting) so vector
  text matches the page exactly, including the maths glyphs Helvetica lacks.
- **PDF import & annotate**: open a PDF as page backgrounds and ink over it.
- **PNG export of sections** (zip of pages) and clipboard "copy page as image".
- **OneNote/Obsidian export**: markdown + attachments folder per notebook.

## App & sync

- **MSIX packaging + auto-update**: installer, file associations (.quill),
  Store-ready identity.
- **Cloud sync**: the storage folder already supports any synced directory —
  add conflict detection (library.json last-writer-wins is risky).
- **Lecture audio recording**: record while taking notes; tap a stroke to jump
  to that moment in the recording (the killer lecture feature).
- **Localisation**: extract strings; Turkish + English first.

## UI polish

- **Liquid glass v3**: refraction-style edge highlight that follows the pointer
  (Composition light), glass shadows under floating panels.
- **Keyboard shortcut overlay**: hold F1 for a cheat-sheet of every shortcut.
- **Recent colours row** in the pen colour picker.
- **Page thumbnails** in the start-screen chips and the notebook tree on hover.

## Known rough edges to revisit

- Table rotation is disabled-in-spirit but not blocked; rotating a table
  doesn't rotate its cell text.
- The equation editor needs internet for MathLive; bundle it offline.
- Ink cache: zoom drift between 0.75–1.35× reuses a scaled cache (slightly
  soft ink until it rebuilds).
- `RtfToPlainText` approximates sizes for vector export; multi-size runs in one
  box export at the first size found.
