# Concepts reference, measured 2026-08-07

Transcribed by the orchestrator from nine reference screenshots the user supplied
(Concepts for Windows, ~125% display scale). **Sub-agents cannot see those
images — this file is the only source of truth for them.** Where a number is
given as a ratio it is exact; where given in DIP it is derived by dividing the
measured screenshot pixels by 1.25 and is ±2 DIP.

Supersedes the dial geometry in UI-SPEC-V2 §1 and CONCEPTS-UI-REFERENCE where
they disagree.

---

## 0. The theme contract (implemented in `Services/PageTheme.cs`)

Every surface in the shell derives from the **page background colour**. This is
not a light/dark switch — a blue page produces blue chrome, a brown page
produces brown chrome. See §6 for the derivation and §7 for the observed proof
points.

All agents code against `Quill.Services.PageTheme`. Do not invent a second
theme source, do not read `Settings.Theme` directly in new chrome.

---

## 1. Radial dial

### 1.1 Geometry

Let **R** = outer radius of the ring. Nominal **R = 98 DIP** (196 DIP across),
scaled by the user's dial-size setting.

| Element | Radius | Notes |
|---|---|---|
| Ring outer edge | `1.00 R` | hairline outline, `Outline` at 40% |
| Ring inner edge = inner disc edge | `0.70 R` | |
| Inner disc | `0.70 R` | filled `Surface`, no border |
| Centre colour dot | `0.195 R` | filled with the active pen's colour |
| Active sector outer edge | `1.19 R` | the sector is **pulled outward** |

- **8 sectors, 45° each.** Sector 0 is centred on 315° (up-and-left); they run
  clockwise from there.
- Sector fill is `Surface` lightened toward the ground — near-white on a paper
  page. Separators are hairlines in `Outline`, drawn radially from `0.70 R` to
  `1.00 R`.
- The whole dial casts one soft drop shadow (y+2, blur 12, black at 18%).

### 1.2 The active sector "pops out"

The selected tool's sector is **redrawn at `1.19 R`**, filled with `OnSurface`
(black on a light page), its icon and label inverted to `Surface`. Its outer
corners are rounded ~6 DIP. This is the single strongest visual cue in the
reference and the shipped build does not have it.

### 1.3 Sector contents

Each sector holds, stacked along its radial midline:
- the **stroke silhouette** for that tool, ~26 DIP tall, drawn in the tool's own
  colour (grey for non-drawing tools);
- beneath it, the **size label** — `1280`, `13K`, `36K`, `346`, `4352` — in
  11 DIP semibold, **rotated to follow the ring**. Labels near the bottom of the
  wheel therefore read upside-down; that is correct and matches the reference
  (the text tool's `Aa` is visibly inverted).
- Non-sizeable tools (eraser, selection, text) show **no number**.

Reference order, clockwise from sector 0:
`Pen 1280` · `Smudge 13K` · `Eraser` · `Selection` · `Pen 36K (green)` ·
`Pen 346 (pink)` · `Text Aa` · `Marker 4352`

### 1.4 Inner disc layout

Origin at the disc centre, `r = 0.70 R`:

| Row | Offset | Content |
|---|---|---|
| 1 | `y = −0.45 r` | size glyph (three stacked rules, thick→thin) + `1280 px`, the pair centred |
| 2 | `y = 0`, `x = −0.62 r` | smoothness glyph (a small waveform) |
| 2 | `y = 0`, `x = 0` | **colour dot**, radius `0.28 r`, opens the COPIC wheel |
| 2 | `y = 0`, `x = +0.60 r` | opacity glyph (a circle half-filled, left dark) |
| 3 | `y = +0.42 r`, `x = −0.62 r` | smoothness value, e.g. `0%` |
| 3 | `y = +0.42 r`, `x = +0.60 r` | opacity value, e.g. `100%` |

Type: values 12 DIP semibold, size readout 13 DIP semibold, all `OnSurface`.

### 1.5 Colour arcs on the disc rim

Each sector holding a **coloured** tool paints a 45° arc on the inner disc rim
(radius `0.70 r`, stroke `0.035 R`) in that tool's colour, aligned to its
sector. Neutral tools paint nothing. In the reference this reads as a black arc
under the black pens, a green arc under `36K` and a pink arc under `346`.

### 1.6 Satellites

**Undo** and **redo** float *outside* the ring at the 9-o'clock and 7:30
positions — bold curved arrows, ~30 DIP, `OnSurface` when available and
`OnSurface` at 30% when not. They are not sectors and have no background.

### 1.7 The value popover  (fixes §K item 4)

Scrubbing or tapping size / opacity / smoothness opens a **horizontal popover**
docked to the right of the inner disc, overlapping the ring:

- Rounded rect ~344 × 116 DIP, radius 10, fill `Surface` at 78% with a blur, no
  border.
- **Preset chips** across the top: `0%` `50%` `70%` `100%`. The active chip sits
  in a filled rounded chip (`Surface` raised, `OnSurface` text); the others are
  bare `OnSurfaceMuted`.
- **Slider** beneath: a 2 DIP track in `OnSurface` at 55%, tick marks at each
  preset, a filled round knob (radius 7 DIP) at the current value.
- **Label row**: the property name centred in 11 DIP letter-spaced caps —
  `OPACITY` — with the decrement glyph at the far left and the increment glyph
  at the far right.
- A small dark **tool-name tooltip** (`Felt Tip Pen`) appears below-left of the
  popover while it is open.

### 1.8 COPIC wheel  (user request, 2026-08-07)

**The COPIC wheel opens centred on the dial's centre point**, not on the
viewport and not on the pointer. Its inner hole should read as a ring around the
dial: the wheel's inner radius ≥ `1.25 R` so the dial stays visible and usable
inside it. Closes automatically on pick (§K 11).

---

## 2. The pen row ("Bar" palette)

The alternative to the dial, selected in Settings → Tool Setup → Interface.

- A **vertical** rounded panel, radius 16 DIP, fill `Surface`, hairline
  `Outline`, soft shadow. Width ~86 DIP.
- One cell per tool, ~86 DIP tall: the **stroke silhouette** (~34 DIP) centred,
  then the size label in 13 DIP beneath.
- The **active cell** is marked by a full-cell-width 2 DIP rule in `OnSurface`
  drawn *between the silhouette and the label*. No fill, no highlight.
- Tools without a size show the silhouette alone (eraser, selection, `Aa`).
- **Undo** floats below the panel, outside it, as a bold arrow — same treatment
  as the dial's satellites.

### 2.1 The attached settings popover

Docked to the **right of the first cell**, a second rounded panel (radius 14,
fill `Surface` shaded one step darker than the bar, ~96 DIP wide) stacking:

1. size glyph + `1280 px`
2. opacity glyph + `100%`
3. smoothness glyph + `0%`
4. the colour dot (filled, ~34 DIP)

Same glyphs as the dial's inner disc. Tapping any row opens the §1.7 popover.

---

## 3. Settings — floating panel, Concepts layout

**Revert to the floating window family** (the Export window's chrome): drag pill
top-centre, close **X** upper-left, info **(i)** upper-right, resize grips in
the bottom corners, radius ~20 DIP, fill `Surface`, no visible border.

Header: two tabs, **Workspace** and **Interaction**, centred, 17 DIP semibold;
the active tab carries a 2 DIP `OnSurface` underline.

### 3.1 Workspace tab

Sections are **collapsible**: a large heading (30 DIP bold) with a chevron at
the far right.

**Canvas**
- `Background` (17 DIP semibold) + `Standard paper or custom background color?`
  (15 DIP, `OnSurfaceMuted`).
- A horizontally scrolling row of **circular swatches**, 69 DIP diameter,
  28 DIP apart, each captioned beneath in 13 DIP. Selected = 2 DIP `OnSurface`
  ring + bold caption; unselected = hairline `Outline` ring + muted caption.
- Order: `Plain White` · `Transparent` (checkerboard) · `Crumpled` ·
  `Lightweight` · `Heavyweight` · `Rippled` · `Blueprint` · `Brown Paper` ·
  `Darkprint`.
- A short **scroll indicator rule** sits under the row, its width proportional
  to the visible fraction, in `Outline`.

**Grid Type** — heading with an `Edit Grid` link (in `Accent`) right-aligned,
subtitle `You can quickly toggle the grid in the Precision or Layers menus.`,
then the same circular-swatch row: `No Grid` · `Dot Grid` · `Graph Paper` ·
`Lined Paper` · `Isometric` · …

**Artboard** — `Artboard Size`, `Set a reference frame for easier exports.`,
then `W:` and `H:` numeric fields (rounded, ~100 DIP wide, `∞` when infinite)
with a swap-axes button, then preset chips: `Infinite` (selected = filled
`SurfaceAlt`) · `1024x768` · `A4` · `1080p` · `…`.

**Measurements**
- `Units` + `Any units displayed or entered on canvas will be converted to this
  system.`
- Sub-tabs `Digital` · `Metric` · `Imperial`, 18 DIP bold, active underlined.
- Circles again, 80 DIP: the first is the **combined** option showing the stack
  (`m / cm / mm`), then each single unit. Digital: `px/pts`, `px`, `pts`.
  Metric: `m/cm/mm`, `mm`, `cm`, `m`, `km`. Imperial: `ft/in`, `in`, `ft`,
  `yds`, `mi`.
- `Display Format & Precision` + `Select your preferred notation.` — two groups
  of circles separated by a vertical hairline: `6.5 pixels` (Full) /
  `6.5 px` (Abbreviated) | `6` (Rounded) / `6.0` (Tenths). One selection per
  group.
- Two toggle rows: `Show stroke length on the right side when drawing`,
  `Show scale in the status bar for selections`.

**Tool Setup** → `Interface`, `Choose your preferred tool palette.`, two circles
holding a wheel glyph (`Wheel`) and a bar glyph (`Bar`).

### 3.2 Toggle switch

Pill 53 × 35 DIP, radius 18. Off: track `OnSurfaceMuted` at 45%, knob white,
left. On: track `#78a19c`, knob white, right. Knob 27 DIP with a 1 DIP shadow.
120 ms ease.

### 3.3 Interaction tab

Per UI-SPEC-V3 §C: Keyboard & Mouse, Touch Input → Finger Action, Gesture
shortcuts. Same section/heading grammar as Workspace.

---

## 4. Brushes panel

Same floating-window family as Settings/Export. Observed on a Darkprint page,
so the screenshot shows the dark resolution of the theme.

Top to bottom:
1. Header: **X** left, title `Brushes` beside it, **(i)** right, drag pill above.
2. A full-width **preview strip**, ~205 DIP tall, painted with the transparency
   checkerboard, on which a sample stroke of the *currently selected* brush is
   drawn live.
3. A band `My Brushes` — 30 DIP bold on `SurfaceAlt`.
4. `Basics` (16 DIP semibold) then a **horizontally scrolling strip** of brush
   cells: silhouette ~40 DIP in `OnSurface`, name beneath in 13 DIP.
   Reference cells: `Pen` · `Fountain` · `Dynamic Pen` · `Fixed Width` · …
5. `Tools` — `Selection` · `Nudge` · `Slice` · `Hard Mask`.
6. A band `Subscribed` (30 DIP bold on `SurfaceAlt`), then pack rows: name
   (17 DIP semibold), a one-line description in `OnSurfaceMuted`, and a check
   mark right-aligned when installed. Reference row: `Waterful` —
   "An artistic ocean of watercolor…".

Sections divide with a hairline `Outline`.

---

## 5. Top bar — no tools

The reference top bar carries **no drawing tools at all**.

**Left cluster:** gallery glyph (four squares) · document title (`Drawing 2`,
17 DIP semibold) · divider · Layers (three stacked curved rules) · Precision
(3 × 3 dot grid) · Objects (a nib-and-ring mark).

**Right cluster:** lock glyph + zoom `47%` + tilt `0°` · divider · Import
(download arrow) · Export (upload arrow) · Settings (gear) · Help (`?`, with a
small `Accent` dot when unread).

Both clusters are **transparent until hovered**, when a rounded `Surface` panel
fades in behind the group. Where a cluster meets the window edge the panel uses
an **inverse-rounded notch** rather than a plain corner. Icon pitch 42 DIP,
glyph 16 DIP, edge margin 31 DIP, divider 1 × 16 DIP.

Everything else that used to live in the top bar becomes a **selectable tool**
in the dial / pen row.

---

## 6. Theme derivation

Inputs: the page ground colour `G` (the paper's base colour, or the flat colour
for Blueprint / Brown Paper / Darkprint).

```
Y      = relative luminance of G (sRGB, gamma-correct)
IsDark = Y < 0.5

Surface     = G shifted in L*:  L*(G) > 80  ->  L* − 15      (darker than paper)
                                otherwise   ->  L* + 18      (lighter than ink)
              chroma scaled to 55% of G's
SurfaceAlt  = Surface, L* ± 4 away from G
OnSurface       = IsDark ? #F2F2F2 : #141414
OnSurfaceMuted  = OnSurface at 55% alpha
Outline         = OnSurface at 14% alpha
Panel           = IsDark ? #141414 : #F7F7F7        (settings / export / brushes)
Accent          = the user's accent, kept
```

The 15/18 split is what reproduces every observed case: a near-white page gets
a *darker* grey dial disc, while blue, brown and near-black pages all get a
*lighter* disc of their own hue.

## 7. Observed proof points (do not regress these)

| Page background | Panel | Dial inner disc |
|---|---|---|
| Lightweight (near-white paper) | light `#F7F7F7`, black text | mid grey |
| Heavyweight / Rippled | light | mid grey |
| Blueprint `≈ #2E80C2` | **dark**, white text | light desaturated blue |
| Brown Paper `≈ #A9713F` | **dark**, white text | light tan |
| Darkprint `≈ #262B31` | **dark**, white text | lighter slate |

On Blueprint, Brown Paper and Darkprint the dial **ring** goes fully
transparent — only the separators, icons and labels remain, letting the page
show through. The inner disc stays opaque.

---

## 8. Paper textures — rebuild from scratch

The nine backgrounds in §3.1 are the full set. Each must be legible at 100%
zoom and must survive the theme system (a texture is a *ground plus grain*, and
the ground is what feeds §6).

- `Plain White` — flat `#FFFFFF`, no grain.
- `Transparent` — the checkerboard, ~8 DIP squares.
- `Crumpled` — the strongest: long creased folds with soft shading either side
  of each crease, plus fine grain. This is the "crumpled paper" look and reads
  clearly in the reference even at thumbnail size.
- `Lightweight` — fine, tight, low-amplitude fibre grain on near-white.
- `Heavyweight` — coarser, cloudier grain; visibly greyer overall than
  Lightweight.
- `Rippled` — a directional, wavy laid-paper pattern; ripples run horizontally.
- `Blueprint` — flat saturated blue ground with a faint fibre grain.
- `Brown Paper` — kraft ground with visible fibre fleck, warmer and coarser.
- `Darkprint` — near-black slate ground with a faint grain.

The prior implementation was invisible for two reasons, both fixed and both to
be avoided again: **Overlay blend** yields only ~8% output range on a near-white
ground (use LinearLight or a direct luminance offset), and **averaging three
independent turbulence channels divides σ by √3** (use one channel).

Acceptance: measure per-pixel luminance σ over a 512² render. A blank page
measures ≈ 0.0. Lightweight must exceed 4.0, Heavyweight and Crumpled must
exceed 7.0, Blueprint and Brown Paper must exceed 3.0. Save a PNG of each for
review.

---

## 9. Revision pass, 2026-08-07 evening

Twelve corrections from the user against the just-merged build (`33bb650`),
plus two new reference screenshots — a **dark-theme** capture of the Bar palette
with its settings popover and the Settings panel, and a close crop of the COPIC
wheel showing a border defect. Where these disagree with §1–§3, **§9 wins.**

### 9.1 Everything is too big  (dial + pen row)

Re-measured off the dark-theme capture, which shows the Bar palette at Concepts'
real proportions. My §2 figures were roughly 1.5× too large.

| Element | Was (§2) | Now |
|---|---|---|
| Bar panel width | 86 DIP | **56 DIP** |
| Bar cell height | 86 DIP | **62 DIP** |
| Bar tool mark | 34 DIP | **27 DIP** |
| Bar size label | 13 DIP | **10 DIP** |
| Settings popover width (§2.1) | 96 DIP | **62 DIP** |

The same shrink applies to the **dial's** size / opacity / smoothness cluster
(§1.4): glyphs and value type come down by the same ~0.72 factor. The ring
geometry in §1.1 stays as specified — it is the *readouts and marks* that are
oversized, not the wheel.

In the dark capture the Bar's active cell is marked by a **filled lighter cell
background** plus a short accent rule on the leading edge, not by the
between-icon-and-label rule of §2. Support both: filled cell on dark grounds,
rule on light. **Undo and redo both** sit below the bar, side by side.

### 9.2 Remove the gaps between dial sectors

Sectors currently render with visible gaps. They must be **contiguous** — a
hairline separator between neighbours, no dead space. The reference ring is a
continuous annulus divided by lines, not a ring of detached wedges.

### 9.3 COPIC wheel — open centred on the colour circle

Supersedes §1.8 and the judgement call made when the dial was rebuilt. The
wheel opens **centred on the dial's colour dot** (the §1.4 row-2 centre swatch),
not on the viewport, and **the dial does not move.** The user was explicit:
*"centred in the middle of the radial dial / centred where the colour circle
is."*

Because the dial is corner-docked, a wheel centred there will overhang the
window. That is expected — solve it by **making room**, not by relocating the
dial:
- the wheel may extend past the window edge; clip it there rather than shifting
  the centre;
- shrink the wheel's outer radius while the dial is docked so more of the ring
  falls on-screen;
- push the floating bars and any open pane out of the way for the duration
  (the `PanelLayout` dynamic-overlap system already does this — use it);
- the user's words are *"when copic colour wheel is open, it is a bit cramped,
  make room / arrange better."*

### 9.4 COPIC wheel — three defects

1. **Double outline.** The selection outline draws twice along some edges — the
   close crop shows the selected cell (`BG90`) with a doubled dark border on its
   shared edges while the outer edges are single. Cause is almost certainly each
   cell stroking its own centred border so neighbours overlap, with the selected
   cell then stroking on top. Draw cell borders as a single shared grid, or
   stroke the selection **inside** the cell bounds only.
2. **Rotation snapping.** Spinning the wheel snaps to fixed positions. **Remove
   snapping entirely** — free continuous rotation with inertia.
3. **RGB and HSL faces start too far inward.** Their rings begin too close to
   the centre; push them outward so they read at the same radius band as the
   COPIC face.

### 9.5 Custom colour in Settings — press once to apply, twice to edit

Supersedes UI-SPEC-V3 §K item 10, which had this backwards.

- Pressing **Custom colour** applies **the colour the user previously set**. It
  does not open a picker.
- Pressing it **again, while it is already the selected colour**, opens the
  COPIC wheel to edit it.

So the first press is a selection, the second is an edit. A never-yet-set custom
colour has nothing to apply, so in that one case the first press opens the
wheel.

### 9.6 Top bar — thinner, and stripped

*"make top bar smaller thinner to maximise page space, remove unused features
from there."* Reduce the bar's height to the minimum that still fits the §5
glyph size (16 DIP) and its hover panel. Anything not in the §5 left/right
cluster lists comes out. Page space is the priority.

### 9.7 New homes for three features

- **Calculator** moves into the **Quill button dropdown** (the app menu behind
  the Quill mark in the top bar). It is currently in `HiddenTools` as
  `BtnCalc` in the user's settings.
- **Dictation** and **Recording** become **selectable tools** — assignable to a
  dial sector or a pen-row cell like any other tool. They are in the user's
  `HiddenTools` as `VoiceBtn`.

Both are part of the §5 goal of a top bar carrying no tools at all.

### 9.8 Settings — the Wheel | Bar circles

Confirmed against the dark capture: **Tool Setup → Interface** shows two 80 DIP
circles captioned `Wheel` and `Bar`. The selected one carries a 2 DIP `OnSurface`
ring and a bold caption. The `Wheel` glyph is a filled disc with a bite out of
it and a small dot; the `Bar` glyph is a tall rounded vertical bar. Both already
exist as `Icons.SurfaceWheel` and `Icons.SurfaceBar`.

The same capture also confirms a **`Restore Default Settings`** link in
`Accent`, centred, as the last row of the Workspace tab.

### 9.9 Dark-theme confirmation

The dark capture is the theme system's acceptance case for panels: panel fill
near-black, section headings and values in near-white, captions muted, swatch
circles drawn as `Outline` rings with **no fill**, and the selected circle's ring
in full `OnSurface`. Toggle tracks stay light-grey when off. This is what §6
must produce on a Darkprint ground.
