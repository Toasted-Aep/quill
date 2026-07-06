# Quill — roadmap

## Shipped in the last release

- PDF import: any PDF becomes a section (one inkable page per PDF page).
- Selection scaling: corner handles on lasso selections resize ink, shapes
  and text together, fully undoable — pen, touch and mouse.
- Object-eraser preview: hover highlights the stroke that would be deleted.
- F1 shortcut cheat-sheet on liquid glass.
- Exports: copy page as image, section → PNG folder, notebook → Markdown +
  images (Obsidian-friendly).
- Safer saves: file IO off the UI thread, close-time flush, and a conflict
  guard that preserves externally-changed libraries (`library.conflict-*.json`).
- Shape recognition already existed: hold the pen still after drawing to snap
  the stroke to a clean shape.

## Next release — big rocks

- **Lecture audio recording**: record audio while inking; every stroke gets a
  timestamp so tapping ink replays that moment. (MediaCapture → m4a per page,
  stroke `CreatedTicks` already exists — needs a playback bar UI.)
- **CanvasVirtualControl swap**: true region invalidation for huge pages;
  removes the ink-cache ceiling and rebuild hitches.
- **Native equation layout**: bundle KaTeX (offline WebView asset) or write a
  stacked-layout renderer so fractions render properly instead of Unicode.
- **Vector PDF fonts**: TTF subsetting so exported text matches the page,
  including maths glyphs.

## Next release — medium

- Cell merge/split and per-cell fill/border styling for tables; bold header
  row toggle.
- Wet-ink stabiliser slider + per-pen pressure curves.
- Markdown paste → rich text; text style presets (named font/size/colour chips).
- Recent colours row in the pen colour picker.
- Page thumbnails in the start-screen chips (background-rendered, cached).
- MSIX packaging with auto-update and `.quill` file association.
- Localisation groundwork (string extraction; Turkish + English).

## Next release — small

- Ctrl+D duplicate selection in place.
- Double-tap a table divider to auto-fit the column to its content.
- "Paste as plain text" option in text boxes.
- Notebook cover emoji/icon picker for gallery cards.
- Zoom-to-fit button (fit page content to window).
- Per-notebook default page background and grid.

## Known rough edges

- Table rotation doesn't rotate cell text (rotation on tables should likely
  be disabled).
- Ink cache softens slightly between zoom rebuild thresholds (0.75–1.35×).
- Vector-export text uses the first font size found per box.
- The MathLive equation editor needs internet; the LaTeX prompt fallback works
  offline but without preview.
- PDF import caps at 200 pages and rasterises (text in imported PDFs isn't
  selectable — it's a drawing surface).
