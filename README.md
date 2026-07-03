# Quill — pen-first lecture notes for Windows 11

*(formerly LectureInk / Fluent Ink — the internal namespace, exe name and storage folder keep the LectureInk name so existing notes and shortcuts continue to work)*

A WinUI 3 (Windows App SDK) drawing/notes app built from your design spec.
Ink rendering is custom, on a GPU-accelerated Win2D canvas, with pressure-aware
pen physics, so it feels closer to OneNote than to a toy canvas demo.

## Build & run

1. Install **Visual Studio 2022** (17.8 or newer) with these workloads:
   - *.NET desktop development*
   - *Windows application development* (includes the Windows App SDK / WinUI tooling)
2. Open `LectureInk.sln`.
3. In the toolbar, set the platform to **x64** (Win2D does not support AnyCPU).
4. Press **F5**. The project is configured as *unpackaged + self-contained*,
   so no MSIX packaging or certificate is needed.
5. Don't run Visual Studio elevated (as admin) — the file save pickers used for
   export don't work from elevated processes.

If NuGet restore complains about exact versions, update `Microsoft.WindowsAppSDK`
and `Microsoft.Graphics.Win2D` to the latest stable — the API surface used here
is conservative.

Notes are auto-saved to `%LocalAppData%\LectureInk\library.json` (debounced,
plus on page switch and app close).

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

## Roadmap (phase 2) — and how to build each piece

- **Handwriting → text (OCR pen)**: feed stroke points into
  `Windows.UI.Input.Inking.InkStrokeBuilder.CreateStrokeFromInkPoints`, then
  `InkRecognizerContainer.RecognizeAsync` (or `InkAnalyzer` for layout-aware
  results). These WinRT APIs are callable from a WinUI 3 desktop app; the model
  here already stores every point, so it's a contained feature.
- **Ink-to-Math**: no public OneNote-equivalent API. Options: MyScript iink SDK
  (commercial, best quality), or recognize-to-text + a UnicodeMath parser for
  simple expressions.
- **Keyboard equation editor (Word-style)**: host the RichEdit math zone
  (`SES_MATH` via custom RichEdit interop) or embed MathLive/KaTeX in a
  WebView2 and rasterize the result onto the canvas as an equation object.
- **Spreadsheet objects / photo paste**: image elements are a small extension
  of the existing `TextElement` pattern (clipboard `Bitmap` → file →
  `CanvasBitmap`); tables can start as a styled `Grid` of text boxes.
- **Multi-page / whole-section PDF export**: `PdfExporter.Create` already
  accepts a list of pages; iterate pages through the surface and capture each.
- **Vector PDF export** (selectable text, infinite zoom): replace the
  image-based writer with stroke → PDF path serialization.
- **Performance at scale**: swap `CanvasControl` for `CanvasVirtualControl`
  + incremental rendering once pages exceed a few thousand strokes.

## Project layout

```
src/LectureInk/
  App.xaml(.cs)           app entry
  MainWindow.xaml(.cs)    all chrome: toolbars, tree, format bar, export, zoom
  Controls/InkSurface.cs  the ink engine (Win2D rendering, input, tools, replay)
  Models/NoteModels.cs    Notebook/Section/Page/Stroke/Text data model (JSON)
  Services/UndoRedo.cs    command-pattern undo/redo actions
  Services/LibraryStore.cs persistence in %LocalAppData%
  Services/PdfExporter.cs minimal PDF writer (no dependencies)
  Helpers/Util.cs         colour + geometry helpers
```

## Known limitations

- Per-page undo history is cleared when you switch pages (the page content
  itself is always saved).
- Lasso selection moves ink strokes only (not text boxes — drag those by their grip).
- Replay covers ink strokes; text boxes appear after the replay finishes.
- Export rasterizes at screen DPI; export at 100% zoom for the cleanest output.
