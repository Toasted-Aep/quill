# Quill — pen-first lecture notes for Windows 11

A WinUI 3 (Windows App SDK) drawing/notes app built from your design spec.
Ink rendering is custom, on a GPU-accelerated Win2D canvas, with pressure-aware
pen physics, so it feels closer to OneNote than to a toy canvas demo.

## Build & run

1. Install **Visual Studio 2022** (17.8 or newer) with these workloads:
   - *.NET desktop development*
   - *Windows application development* (includes the Windows App SDK / WinUI tooling)
2. Open `Quill.sln`.
3. In the toolbar, set the platform to **x64** (Win2D does not support AnyCPU).
4. Press **F5**. The project is configured as *unpackaged + self-contained*,
   so no MSIX packaging or certificate is needed.
5. Don't run Visual Studio elevated (as admin) — the file save pickers used for
   export don't work from elevated processes.

If NuGet restore complains about exact versions, update `Microsoft.WindowsAppSDK`
and `Microsoft.Graphics.Win2D` to the latest stable — the API surface used here
is conservative.

Notes are auto-saved to `Documents\Quill\library.json` by default (debounced,
plus on page switch and app close); the folder is configurable in Settings.
Existing `Documents\LectureInk` or `%LocalAppData%\LectureInk` notes are
migrated automatically on first run (copy-only — originals stay put).

## What's implemented (vs the spec)

**Input**
- Pen drawing with pressure (and a pressure-independent highlighter)
- Pen eraser tail → eraser, barrel button → lasso select (OneNote-style)
- "Touch draw" toggle: finger/mouse drawing on demand; otherwise touch pans/pinch-zooms

**Pen tools** — Standard, Brush (strong pressure curve), Fountain (angled nib),
Highlighter (uniform-alpha geometry, no blobby overlaps). Per-stroke colour
(8 swatches + full colour picker) and thickness slider.

**Erasers** — Point eraser (splits strokes, erasing only the touched part) and
Stroke eraser (whole object). Both fully undoable.

**Tools** — Lasso select (move with drag, Delete key removes), Ruler
(straight lines snapped to 15°), Insert Free Space (drag to push content apart).

**Text** — Floating text boxes (drag by the grip, ✕ to delete) with Word-style
formatting: font family/size, bold/italic/underline/strikethrough,
superscript/subscript, text colour, highlight, bullets, indent/outdent,
left/centre/right/justify. Spell-check is on (red squiggles) — it follows the
Windows input languages, so install English/Italian/Turkish keyboards in
Windows Settings and it proofs each language as you type in it.

**Math** — Browsable symbol palette (90 symbols, inserted at the caret) plus
superscript/subscript — enough for most inline lecture math. See roadmap for
full equation editing.

**Page & canvas** — Background: white, cream, dark, total black (OLED) or any
custom colour; grid: dotted/squares/row-lines with adjustable spacing; ink
colours auto-contrast helpers (white swatch for dark pages). Zoom: buttons,
Ctrl+wheel, touch pinch, live % indicator, tap % to reset.

**History & replay** — Undo/redo (Ctrl+Z/Y) with a visible history list, and
Replay mode that redraws the page stroke by stroke in original order.

**Organisation** — Notebook → Section → Page tree in a collapsible sidebar;
add/rename/delete at every level; auto-persisted library.

**Export** — Current page to PDF (built-in dependency-free PDF writer) or PNG,
including text boxes, background and grid.

**UI/UX** — Pen row at the bottom, hideable per page (remembered per page);
Minimal-UI mode (everything hidden except a restore button); full-screen
toggle; Mica backdrop; pen-first layout with tooltips everywhere.

## Phase 2 — implemented

- **Handwriting → text (OCR pen)**: lasso-select ink, right-click →
  *Convert handwriting to text* (Windows `InkAnalyzer`, line-aware). The ink
  is replaced by an editable text box; fully undoable.
- **Ink-to-Math**: right-click a lasso selection → *Convert handwriting to
  maths (evaluate)* — recognised text is normalised and run through the
  built-in `CalcEngine`, inserting `expression = result`.
- **Equation editor (typed)**: Shapes menu → *Equation (typed)…* hosts
  MathLive in a WebView2 dialog (LaTeX shortcuts); the rendered formula is
  rasterised and inserted as a movable/resizable image object. Needs the
  WebView2 runtime and internet access.
- **Tables**: Shapes menu → *Table…* inserts an n×m grid of border shapes
  with a text box per cell (single undo step). Photo paste already shipped.
- **Multi-page PDF export**: Export menu → section or whole notebook as one
  PDF (each page auto-fitted and captured in order).
- **Vector PDF export**: Export menu → *vector PDF (ink & shapes)* — strokes,
  shapes and the grid are written as true PDF paths (tiny files, crisp at any
  zoom). Text boxes/images aren't vectorised; use the raster export for those.
- **Touch-screen mode**: Settings toggle that enlarges every toolbar/panel
  control to comfortable tap sizes.

## Roadmap (phase 3)

- **Performance at scale**: swap `CanvasControl` for `CanvasVirtualControl` +
  a cached static-ink layer once pages exceed a few thousand strokes. (The
  draw loop already culls to the viewport, which covers most real pages.)
- **Vector PDF text**: embed fonts and serialise text boxes into the vector
  exporter so vector pages match the raster export exactly.

## Project layout

```
src/Quill/
  App.xaml(.cs)           app entry
  MainWindow.xaml(.cs)    all chrome: toolbars, tree, format bar, export, zoom
  Controls/InkSurface.cs  the ink engine (Win2D rendering, input, tools, replay)
  Models/NoteModels.cs    Notebook/Section/Page/Stroke/Text data model (JSON)
  Services/UndoRedo.cs    command-pattern undo/redo actions
  Services/LibraryStore.cs persistence (Documents\Quill + backups/migration)
  Services/PdfExporter.cs minimal PDF writer (no dependencies)
  Helpers/Util.cs         colour + geometry helpers
```

## Known limitations

- Per-page undo history is cleared when you switch pages (the page content
  itself is always saved).
- Lasso selection moves ink strokes only (not text boxes — drag those by their grip).
- Replay covers ink strokes; text boxes appear after the replay finishes.
- Export rasterizes at screen DPI; export at 100% zoom for the cleanest output.
