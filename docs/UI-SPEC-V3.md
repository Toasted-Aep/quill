# Quill UI spec v3 — Concepts-style shell

User-authored, 2026-07-23. Builds on UI-SPEC-V2.md. Where they conflict, v3 wins.

---

## A. Radial dial — DEFECTS in the shipped build (fix first)

The docked dial works but is "nothing like the Concepts dial, very buggy":

1. **Phantom hover** — the dial lights up while the pointer is over the *page*,
   not the dial. Hit-testing is firing outside the wheel.
2. **Shows in the notebook gallery** — it must only load/appear on a page.
3. **Tools cannot be selected** — clicking a slot does nothing.
4. **Opacity not implemented.**
5. **No preview circle** — scrubbing a setting must draw a real circle with the
   current tool (see v2 §1.1).
6. **COPIC wheel unreachable** from the centre disc.
7. **Icons off-centre** inside their sectors.
8. **Dial icons differ from the top-bar icons** — they must match.
9. **A tool present in the dial must be removed from the top bar automatically.**

## B. Page chrome

- When the radial dial is enabled, **hide the page name, date and time** from
  the page surface.
- The page title becomes **renamable from the top bar**.
- Add the **Quill icon to the top bar**, identical to the gallery one.
- **Remove the Backgrounds tab** from the top bar.
- Move **notebook gallery + page name** into a **separate pane**, a sibling of
  Layers / Precision / Objects (same floating-pane family as Settings).
- Top-right, beside import/export/settings: **zoom and tilt readouts with a
  lock**, per the reference screenshot.

## C. Settings

- Port **all Concepts settings**.
- **Measurement units** — Digital: px, pts. Metric: m/cm/mm, mm, cm, m, km.
  Imperial: ft/in, in, ft, yds, mi.
- Move the **light/dark toggle into Settings as an on/off slider**, replacing
  the dropdown.
- Add **grid opacity**.
- **Toggle sliders**: styled per the screenshots, colour `#78a19c`.

### Interaction page
- **Keyboard & Mouse**: keyboard shortcuts + an "edit shortcuts" affordance;
  enable-keyboard-shortcuts on/off.
- **Touch Input — Finger Action**: Do Nothing · Use Active Tool · Pen Canvas ·
  Select · Nudge · Slice · Zoom · Rotate.
- **Gesture shortcuts**: top-button click, double-click, hold, and two/three/
  four-finger tap.

## D. Paper textures — too weak

Backgrounds are "too unrealistic, texture is almost non visible". Increase
contrast/among grain so each paper reads clearly at 100% zoom.

## E. Export / import panes

- Rebuild **Export** in the Concepts layout (see screenshot): a **Format** row
  of circular buttons, a **Region** row, **Options** toggles (include
  background / include grid), a **Details** block (output size, scale
  100/200/400%), and a blue export button `#3282aa`.
- Formats: keep Quill's existing set, and **replace "Concepts" with `.quill`**.
- **Region is dynamic**: the last region chip is the current page size (A4 when
  the page is A4).
- **Import** is a simple dropdown: files · paste from clipboard · take a picture.

## F. COPIC wheel

- It **sticks to the mouse** — fix.
- Give it the **start animation** from the web version.

## G. Pen library ⭐⭐⭐⭐⭐

Pens and brushes from Krita, Fresco and especially Concepts.

## H. Whole-UI conversion to Concepts style ⭐⭐⭐⭐⭐

Convert the entire shell. Existing Quill-only features become **tools** for now
until better homes are found — and the orchestrator owes a recommendation on
where each should end up.

---

## I. The two floating bars (when the radial dial is active)

When the radial dial is the tool surface, the top bar is replaced by two
floating liquid-glass bars, matching the Concepts reference.

**Top-left bar** — sits directly ABOVE the docked radial dial:
1. Notebook-gallery icon
2. Page name  *(and the page name + date stop being drawn on the page itself)*
   — transparent divider —
3. Layers
4. Precision
5. Objects

**Top-right bar** — same floating-pane styling:
1. Zoom and tilt level, lockable
   — transparent divider —
2. Import — from file · paste from clipboard · take a photo
3. Export — opens the export pane (below)
4. Settings

**Top bar proper:** add the Quill icon, identical to the notebook-gallery
button. Every remaining top-bar feature moves into the radial dial as a
selectable tool.

## J. Export pane

A floating window in the same family as the settings window:
- Close (X) upper-left, title "Export", info (i) upper-right, drag bar top-centre.
- **Format** — a row of circular buttons, each with a sub-label: JPG
  (Compressed) · PNG (Lossless) · SVG (Vector) · DXF (Vector) · PSD (Lossless)
  · **.quill (Native)** · PDF (Flattened) · PDF (Vector Paths). A one-line
  description of the selected format sits under the row.
- **Region** — "Select the area you'd like to export": Screenshot · Entire
  Drawing · Selection · and a DYNAMIC last chip that is the current page size
  (A4 when the page is A4, Artboard when infinite).
- **Options** — "Select anything you'd like to include in the file":
  Include Background · Include Grid, as `#78a19c` toggle sliders.
- **Details** — Output Size (px @ppi) and Scale: 100% · 200% · 400%.
- Primary action button in `#3282aa`.

---

## K. Fix list, 2026-08-06 (user-reported against the shipped build)

Decisions taken: pen slot icons are a **stylised stroke silhouette** (a vector
mark shaped like that pen's stroke, in the pen's colour), not a live render.
**Visible bugs are fixed before layers**; layers then follows as its own piece.

### Dial + colour
1. Dial still appears in the **notebook gallery** — regression, was fixed once.
2. COPIC wheel is **not centred on the dial**.
3. Colour **names disappear while scrolling** the COPIC wheel.
4. Size / opacity / smoothness need a **slider window that opens**, not
   drag-only scrubbing with no UI.
5. Dial icons are **cut off** — parts missing "like erased", clipped into a
   small square.
6. Dial **collides with the Notebooks window** — hide or move the dial when it
   opens.
7. Make the dial **smaller**.
8. Pen slot icons = the **stroke that pen leaves** (stylised silhouette).
9. Add a **colour picker to the pen row**; when the dial is off, the wheel
   opens centred on that picker instead. ***
10. Clicking **Custom colour again** (after selecting, in Settings) opens the
    COPIC wheel.
11. COPIC **closes automatically** once a colour is chosen.
12. **Colour mixing** is not implemented.

### Chrome, panels, settings
13. **Revert** the settings floating window (docked stays).
14. **Touch draw** moves into Settings as an on/off toggle.
15. **Leave free space** moves to the toolbar.
16. **Remove the Objects button** from the top bar (it is in the left cluster).
17. **Comments** move to the tool window, selectable from the dial.
18. **AI button** sits to the left of Import.
19. **Layers / Precision / Objects do not open.**
20. Layers and Precision become **floating, borderless, background-less panes**
    in the bottom-left.
21. **Every panel moves out of the way dynamically when something overlaps
    it.** ***
22. Export **regions renamed**: Screenshot · Selection · Entire Page · Current
    Section · Current Notebook · and a sixth meaning "just the page area"
    (e.g. A4 when A4 is set) — needs a better name than "Page".
23. **Unit selection as circles**, with more units (cm, m, …).
24. **App theme selection as coloured circles.**
25. Textured page backgrounds **more realistic**.
26. **Tilt** is still unimplemented (canvas rotation).

### L. Objects library panel (2026-08-07)

Opens on the **LEFT** side, in the same family as the old floating resizable
"iPad-like" settings/export window (drag bar top-centre, close upper-left,
info upper-right, resize grips).

Layout, per the Concepts reference (`Objects - Concepts Manual-1.pdf`, 111pp,
and the user's screenshot):
- A tab row: **Favorites · My Packs · Object Market · Pexels** (plus a close X
  at its left).
- A **search** field beneath the tabs.
- Then **categories**, each a titled row — e.g. "Basic Shapes", "Background
  People", "iOS Icons", "Hand Drawn Icons Vol 1" — with a star (favourite) and
  a check (installed/enabled) at the right of each title.
- **Beneath each category title, the shape visuals themselves** in a
  horizontally-scrolling strip on a subtly tinted band.
