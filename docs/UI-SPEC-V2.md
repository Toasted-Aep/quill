# Quill UI spec v2 — radial dial, floating windows, tools, chrome

User-authored specification, 2026-07-23. This supersedes the dial section of
CONCEPTS-DIRECTION.md where the two conflict. Reference screenshots live in
`C:\Users\irony\Documents\ShareX\Screenshots\2026-07\` and the Concepts manual
is `C:\Users\irony\Downloads\concepts-manual-latest.pdf`.

---

## 1. Radial dial — geometry

Three concentric **annuli** (donut rings, hollow centre), each cut into equal
**sectors**. A sector is the button. 0° is at the right; angles increase
counter-clockwise unless stated.

| Ring | Contents | Sector size |
|---|---|---|
| Centre disc | colour button (current colour; white if the tool has none) | — |
| Inner annulus | 3 setting arcs | 120° each |
| Outer annulus | 10 tool slots | 36° each |

- A slot whose tool is **unavailable** (e.g. redo with nothing to redo) renders
  its sector **transparent** — not hidden, not greyed: transparent.
- The dial **starts maximised/docked**, top-left, always visible. Hold-to-summon
  is removed.
- The dial is **hidden in the notebook gallery** (it only belongs over a page).

### 1.1 Inner annulus — the three settings

Covering the colour circle, an annulus of three arcs, each with its own icon:

| Setting | Arc |
|---|---|
| Size | +30° → +150° |
| Opacity | +30° → −90° |
| Smoothness | +150° → −90° |

While a setting is being modified, a **preview circle is DRAWN** — an actual
ink drawing produced by the current tool, not a UI shape — using the tool's
current colour and dynamics, so the user sees exactly what the brush will do.

If a setting does not apply to the active tool, grey that arc out.

### 1.2 Outer annulus — the ten tools

Each slot shows the tool icon plus its **size number**, positioned according to
where the slot sits: size text **above** the icon for slots in the upper half,
**below** for slots in the lower half.

Defaults, starting at 12 o'clock and going **clockwise**:

1. Pencil
2. Fill tool (behaviour as Concepts)
3. Selection tool (mouse)
4. Eraser
5. Felt-tip pen
6. Text tool
7. Fountain pen
8. Redo
9. Undo
10. Standard pen

All ten are user-customisable.

### 1.3 Tool-specific UI

**Selection tool** — a UI rectangle appears at the **bottom of the screen** with
three toggles:
1. Lasso mode: freeform ↔ square
2. Partial ↔ complete selection
3. Active layer ↔ all layers affected

**Eraser** — modes: nudge, slice, hard-mask, soft-mask.

**Text tool** — does **not** open a separate top toolbar. Options appear
directly **above the text**, icon-only (names shown on hover):
edit text · copy · lock · duplicate · delete · flip horizontally · flip vertically.
Remaining text options go in a right-click context menu (OneNote-style).

---

## 2. Floating window (shared control)

One reusable window used by the settings page **and** the tool/pen-library menu.

- Resizable, with **iPadOS-style resize indicators**.
- Movable by a **bar indicator centred at the top middle**.
- **Close** button upper-left; **info/help** button upper-right.
- Directly below those: a **category divider** row — Settings · Interaction.
- Right-clicking a **tool** opens this window showing **only** the pen
  library / brushes category, nothing else.
- Liquid-glass styling, rounded corners.

---

## 3. Tools and brushes to add

**Tools:** rotate, pan, zoom.

**Brushes:** dotted, fill, airbrush, soft pencil, hard pencil, marker
(board-marker-on-paper character).

**Realism fixes (existing pens are too generic):**
- Watercolour — currently does not read as watercolour at all; needs real
  pigment behaviour (edge darkening, granulation, wet diffusion).
- Pencil — needs graphite tooth/grain against the paper texture.
- All other pens — add material realism.

---

## 4. Chrome (from the original brief, still outstanding)

### 4.1 Window bar (restore native Windows controls)

- **Top-left:** hamburger (three lines) dropdown containing —
  notebook gallery (Ctrl+Q) · new page in section (Ctrl+N) · new section in
  notebook (Ctrl+M) · open .quill (Ctrl+O) · import (Ctrl+Shift+I: clipboard,
  camera, files) · save as (Ctrl+Shift+S, dropdown: page/section/notebook,
  saved as .quill) · export (Ctrl+Shift+E, dropdown: page/section/notebook,
  then file type) — divider — hide layers (Alt+7) · hide precision — divider —
  copy · paste — divider — settings · about · help · quit.
- Next to the hamburger: the **app icon and the word "Quill"**.
- **Top-right:** the native window control buttons; to their left a **+** icon
  (dropdown: new page / new section / new notebook), a **user** icon (future),
  and a **full-screen** button.

**Help window** — floating: link to the GitHub page, a list of keyboard
shortcuts with the option to rebind them, Discord / Instagram / LinkedIn
buttons (future), and a landing page with drawings and catchphrases.

### 4.2 Top bar

**Top-left, six buttons:**
1. Notebook gallery
2. Notebooks
3. Page name
4. Layers — opens a panel directly below the pen dial
5. Precision — opens in the **bottom-left**, below layers: grid options ·
   snap on/off · measure (drawing scale) · guide (line, arc, angle, ellipse,
   rectangle, each reshapeable) · recognition (perfect-shape insertion on/off)
6. Objects — the objects menu in a floating window, with a **+** to add shapes
   and objects to the library

**Top-right:** zoom and tilt level (lockable, with icons) · export (icon, with
page/section/notebook then file-type dropdowns) · settings (opens the floating
window) · minimal-UI button.
