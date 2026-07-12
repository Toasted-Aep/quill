# Quill — roadmap

## Shipped recently

**Ink & input**
- Pen repair mode (Settings): bridges mid-stroke drop-outs from a faulty pen
  and suppresses lift-bounce dots; deliberate dots still register.
- Shape resize overhaul: rotated shapes resize about a pinned anchor (no more
  drifting), polygon vertices anchor sensibly, bigger rotate-handle hit areas.
- Lasso double-click = leave blank space once, then the pen comes back.
- Shape recognition settle pulse; custom axis labels on x-y / x-y-z shapes.

**Look & feel**
- Window-level Mica (Windows 11) with translucent root; liquid-glass depth
  tiers (floating docks clearer than chrome); glow engine with Off / Breathe /
  Circulate modes shared by every glow in the app; accent unified everywhere,
  with optional accent-follow (active pen or notebook colour).
- Follow-Windows theme mode; true-black OLED dark theme; text ink follows the
  page background, not the app theme.
- Original vector icon set for pen types, minimal-UI buttons and tools.
- Honours Windows' "Transparency effects" and "Animation effects" settings.
- Minimal-UI cluster snaps to any edge (middle magnetism) and tucks into a
  corner pull-out tab; touch mode scales glyphs, not just hit targets.

**Features**
- AI assistant: floating chat with history that SEES the page (PNG snapshot of
  the actual ink) plus one-shot summarise / action items / smart tags / ask /
  improve-selection. Providers: Claude, OpenAI, Gemini, or a local
  OpenAI-compatible server; keys live in the Windows Credential Locker.
- Ctrl+K command palette (jump to any page, run any common action).
- Voice dictation via Windows speech into the focused text box.
- [[Note Name]] links between pages; bare URLs auto-link (Ctrl+Click opens).
- Typed equations: pixel-perfect MathLive capture tinted to the page,
  editable in place (right-click → Edit equation).
- Exports: vector PDF, SVG, HTML (vectors + selectable text) at page /
  section / notebook scope; per-section Markdown + images; gallery Save… menu.
- Calculator constants page (g, R, N_A, c, h, k_B… + user-defined).
- Per-notebook default font/size; custom accent swatches; pen-dock position
  picker; visual emoji cover picker; configurable autosave; window placement
  and eraser mode remembered.
- Comment pins: tap the Comment tool to drop a numbered note pin anywhere,
  tap a pin to read / edit / resolve / delete it; resolved pins grey out.
  (Standalone step toward staged collaboration — see COLLABORATION-PLAN.md.)
- Undo/redo flash-highlight: undoing or redoing pulses an accent highlight
  over the affected element (bounds reported across the ink/shape/text/mixed
  action types).
- Pressure-curve editor: drag the midpoint of a live curve to shape how pen
  pressure maps to width (replaces the three-preset dropdown).
- OneNote-style two-tone pen row: each pen is a grey body with the pen's own
  colour on its tip and band; the selected pen (or eraser) lifts out of the row.
- Ctrl+D duplicates the selection in place; double-tap a table divider to
  auto-fit that column to its content.

## Next release — big rocks

- **CanvasVirtualControl swap**: true region invalidation for huge pages;
  removes the ink-cache ceiling and rebuild hitches. (Tracked, 0% built.)
- **Spatial index** (grid buckets) for hit-testing/erasing at high stroke
  counts — needs stroke/shape mutation centralised first (~27 call sites).
- **Async library load**: show the window before deserialising every
  notebook; most of startup becomes a post-load continuation.

## Next release — medium

- Operation log (Stage 0 of collaboration): make every `IPageAction`
  serializable to a tagged JSON op and persist a rolling `oplog.jsonl` — crash
  recovery and change history now, sync groundwork later. Comments already ship
  and will ride the op-log for free (see docs/COLLABORATION-PLAN.md).
- Gallery card → page connected animation (needs a XAML placeholder target
  over the Win2D canvas).
- Toolbar hide/show customisation (choose which tool buttons appear).
- Alternate keyboard preset layouts (full remapping is deliberately out of
  scope; the command palette covers most of it).

## Next release — small

- Notebook cover thumbnails in gallery cards (background-rendered, cached).
- MSIX packaging with auto-update and `.quill` file association.
- Finish string extraction so the language picker can ship (en/tr/it groundwork
  exists; most UI text is still code-built English).

## Known rough edges

- Ink cache softens slightly between zoom rebuild thresholds (0.50–1.05×).
- Vector-export text uses the first font size found per box.
- The MathLive equation editor needs internet; the LaTeX prompt fallback works
  offline but without preview.
- PDF import caps at 200 pages and rasterises (imported text isn't selectable).
- Table rotation doesn't rotate cell text (rotation on tables should likely be
  disabled).
