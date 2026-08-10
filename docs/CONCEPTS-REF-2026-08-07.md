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

---

## 10. Revision pass, 2026-08-08

A third pass from the user against the merged build, plus four new screenshots:
the COPIC wheel's centre controls, the wheel open beside the dial, and two of
Concepts' **guideline editor** panels. Where §10 disagrees with anything above,
**§10 wins.**

### 10.1 Top bar

1. **Icon sizes are inconsistent** — some render at touch-mode size while others
   do not. All top-bar glyphs must be **one size in normal mode**, and scale
   **proportionally together** in touch mode. No per-icon exceptions.
2. **Undo and redo leave the top bar** whenever the radial dial is the active
   tool surface (they live in the dial instead — see §10.2).
3. **History moves into the Quill button menu.** Selecting it opens a floating
   window in the Export/Settings family, **docked to the right** of the screen.
4. **Page name and date come off the page surface** and move into the top bar.

### 10.2 Radial dial

5. **Undo and redo move INSIDE the wheel.** Supersedes §1.6, which had them as
   satellites outside the ring. They become buttons within the dial itself.
6. **Sector contents are the wrong way round and clipped.** Supersedes §1.3:
   - the **size label goes to the OUTER part** of the cell (furthest from the
     dial centre);
   - the **stroke silhouette goes to the INNER part** (nearest the centre);
   - marks below the horizontal midline currently render **upside down** — fix
     so every mark is upright regardless of sector;
   - the size text is currently **cut off**. After the re-layout, verify no
     label is clipped at any sector angle or any size string length (`1280`,
     `13K`, `4352`, `36K` all differ in width).

   Note the tension with §1.3's "labels rotate to follow the ring": that stays
   true for the *label*, but the *mark* must read upright.
7. **Selection animation** — on selecting a cell it **rises and lights up**.
8. **The COPIC wheel is still not centred on the dial.** §9.3 asked for it to
   centre on the colour dot and it does not. This is the third request; treat
   the centre of the dial's colour circle as the required centre point and
   verify it by measuring both centres, not by eye.
9. **The pen preview must show whenever the size / opacity / smoothness popover
   is OPEN**, not only while a value is actively being dragged. Also **remove
   the blue guideline** that currently draws with it.

### 10.3 Pen row — revert

10. **The user does not want the new Bar palette.** *"I don't like the current
    pen row revert to the old one."* Restore the previous pen row. The §2 /
    §9.1 Bar work stays in the tree behind the Wheel|Bar setting but must not
    be what a user sees by default.
11. **Remove the ruler from the pen row.** It becomes a **tool**: tiltable with
    a two-finger tilt gesture, with a **tilt visualiser** that can be clicked
    with a mouse to type an exact angle.
12. **Add an eyedropper tool**, selectable from the tool library into either
    the pen row or the dial.

### 10.4 COPIC wheel

13. **Page custom colour centres the wheel on the pen colour icon** — the same
    centre point as §10.2 item 8.
14. **Custom page colour behaves like §9.5**: the chosen colour is saved;
    switching away and back applies the saved colour **without** opening the
    wheel; pressing it again while already selected opens the wheel to edit.
    Add an **`Edit Colour` button to the right of the `Background` heading**,
    styled exactly like the existing `Edit Grid` link.
15. **`COPIC`, `HSL` and `RGB` are too close together**, their **font is too
    big**, and the **eyedropper icon is too big.** Space them out, reduce both.
    (§9.4 asked for the faces to move outward; this is the follow-on.)
16. **`MIX` — awaiting the user's decision.** The control currently offers
    `OFF · 25% · 50% · 75%` and sets how much of a newly picked colour blends
    into the current one, through the spectral pigment mixer in
    `Helpers/PigmentMix.cs`. **Do not redesign it until the user has answered.**
17. **Scroll-wheel rotation** — the wheel must spin with the mouse wheel and
    with horizontal/side scroll.

### 10.5 Floating windows and Settings

18. **Remove the side and top resize handles.** Corner grips only — the corners
    already resize.
19. **Theme circles**: delete the "dark appearance" toggle. The theme row gets a
    **white circle named `Light`** and the existing black circle renamed
    **`Dark`**.
20. **Settings is laggy and scrolls back to the top whenever an option is
    picked.** Selecting a control must not rebuild the whole panel or lose
    scroll position. This is the single most-felt defect in the panel.
21. **Switching measurement category must not auto-select the first item.**
22. **Panel font is too big.** Reduce it, and add a **developer setting** that
    allows changing the font of specific pages.
23. **Bigger margins** around section titles and their explanation lines.
24. **Objects library glitches when scrolled sideways** — fix.
25. **A vertical mouse wheel over a horizontally-scrolling strip must scroll it
    horizontally.** Applies to every horizontal strip: swatches, units, brushes,
    objects.
29. **Mouse modes move into the Interaction page**, presented as circles like
    the other option groups.

### 10.6 Paper

26. **Textures are too noticeable — reduce them.** §8's σ floors were set to
    escape the previous invisible build and overshot. Scale grain amplitude
    down and **re-run `tools/PaperProof`**, lowering the floors to match the new
    target rather than deleting them. The control must still measure 0.00 and
    the multi-scale decay check must still pass — quieter, not flatter.

### 10.7 Guidelines / grid editor  (new, from two reference screenshots)

27–28. **Guidelines move into the `Grid Type` category** and gain a full editor.
The reference panel, top to bottom:

- A **live preview strip** at the top, full width, ~200 DIP, rendering the grid
  as configured — the reference shows a 1-point perspective fan.
- A **`< Back`** link beneath the preview, left-aligned.
- The grid's name as a 34 DIP bold heading (`1-Point`).
- **`Preset`** — circles: `1 Point` (glyph: nested squares with diagonals) and
  `Custom` (glyph: a circle with eight radiating spokes). Selected carries the
  2 DIP `OnSurface` ring.
- **`Vanishing Points`** — an **`Edit Points`** button: filled rounded rect in
  `SurfaceAlt`, label in `Accent` bold. Caption beneath: *"You can edit the
  vanishing points with a tap & hold on canvas or by activating the grid
  layer."*
- **`Density`** — a right-aligned **typeable value box** (white rounded field,
  e.g. `30`), caption *"Set the number of vanishing lines per point."*, then a
  **full-width slider**: 2 DIP `OnSurface` track, white knob with a hairline.
  Every numeric setting in this panel follows that box-plus-slider pattern —
  the box is editable directly, so the slider is never the only way in.
- **`Line Weight`** — same pattern, value box reads `1 pts`.
- **`Color`** — caption *"Automatic color adapts to your background color.
  Custom colors are independent of the background color."*, then circles
  `Automatic` (unfilled) and `Custom` (filled with the chosen colour).
- **`Opacity`** — value box `20%` plus slider.
- **`Orientation`** — circles `Landscape` and `Portrait`, glyphs being a
  rounded rect ruled horizontally and one ruled vertically.
- **`Confine to artboard`** — a square **checkbox** (not a toggle) with the
  label *"Only show the grid lines inside the artboard."*

Also required: **edit the tilt of the horizon line, and move the grid's centre
point within the page.**

### 10.8 MIX — resolved by the user, 2026-08-08

Replaces §10.4 item 16. The `OFF · 25% · 50% · 75%` arc in the wheel's centre is
**removed**; mixing leaves the colour picker entirely.

**Mixing becomes a dedicated Mix tool**, selectable into the dial or the pen row
like any other tool. Choosing it lets the user combine two colours — picked from
the canvas with the eyedropper, or from recents and swatches — and produces the
blend through the spectral mixer in `Helpers/PigmentMix.cs` (the one where blue
and yellow give `#3DA06B`, a real green, rather than a steel grey). The colour
wheel goes back to being purely a picker, which also relieves the crowded centre
the user has flagged twice.

**Scope: pens and brushes only.** Page background, grid colour, accent and table
cells always replace outright. You mix ink, not paper.

**One exception, and it is the interesting part — mixing with the page
background dilutes rather than tints.** The user's words: *"if mixing with
background make paint gradually transparent as if it is mixing with the page
colour."*

So when one of the two colours is the page ground, the result is **not** a hue
interpolated toward that ground. It is the original pigment at **reduced alpha**,
as if thinned with water or medium:

- mixing 50% with the background yields the same hue at roughly 50% opacity;
- mixing further approaches fully transparent, never approaches the ground's hue.

This distinction is load-bearing, not cosmetic. A hue-lerp toward the ground
produces a flat opaque colour that *looks* right only on a plain page — on a
textured, Blueprint or Brown Paper page, genuinely diluted paint must let the
grain and the ground show **through** it, which an opaque lerp cannot do. It
also means diluted strokes composite correctly over each other and over ink
underneath, the way a wash does.

Implementation note: this is the same substrate the oil-paint work uses, so
prefer extending `PigmentMix` with an explicit "dilute toward transparency"
path over special-casing the ground colour at each call site.

---

## 11. Revision pass, 2026-08-08 evening

Fourth pass, with eleven new screenshots. **§11 wins over everything above.**
Items the user re-listed from §10 are marked `[§10]` — those were never
completed, not re-requested for emphasis, and conflating the two would hide how
much is still outstanding.

**Standing instruction from this point:** confirm the plan with the user before
modifying anything, and where a task has several correct implementations, ask
which they prefer rather than choosing.

### 11.1 CRITICAL

1. **Settings scroll-resets to the top and lags whenever any option is
   clicked.** The user rates this 5/5 and asks for it to be fixed immediately.
   Almost certainly a wholesale panel rebuild on every change — updates must be
   surgical and must preserve scroll offset. `[§10 item 20]`
2. **The pen preview renders as a SQUARE because the size reads 16000.** The
   user diagnosed this themselves. Find the real cause — a unit confusion or an
   unclamped size — rather than clamping the preview to hide it. It should draw
   as a **hollow circle**: a circle *stroked with the selected pen*, mimicking
   that pen's style, not a filled shape.

### 11.2 Radial dial — geometry rebalance

The user is explicit that the **overall dial size and the colour circle size are
both already correct.** What is wrong is the split between the rings.

3. **The inner circle (size / stability / opacity) is too big — shrink it.**
4. **The outer ring gets thicker in proportion**, taking the freed space.
5. **Tool icons and stroke previews in the outer ring are too small — enlarge
   them**, while guaranteeing they never visually overflow the dial.
6. **Pens still render upside down.** Rotate every mark so it reads upright at
   any sector angle. `[§10 item 6]`
7. **Size text goes to the OUTER part of the cell, the stroke to the INNER
   part**, and no label may be cut off afterwards. `[§10 item 6]`
8. **The per-pen colour preview moves out of the inner circle and into the
   TOOLS ring, at that ring's innermost edge** (nearest the dial centre).
9. **The colour preview is too wide — reduce its width.**
10. **Undo and redo move inside the hollow centre** as buttons. `[§10 item 5]`
11. **Now that undo and redo are inside, add two more customisable cells to the
    outer ring, bringing it to a full ten.**
12. **Selection animation: the cell rises and lights up.** `[§10 item 7]`
13. **Hover indicators** on opacity, size, stability, undo and redo.
14. **Redesign the undo and redo icons.**
15. **The COPIC wheel is still not centred on the dial.** Fourth request.
    `[§10 items 8 and 13]`

### 11.3 Colour wheel

16. **HSL exactly as its screenshot**: curved arc sliders, each a gradient
    stroke with a round knob and its own typeable value box — hue in degrees
    (`0°`), the others in percent, laid out as concentric arcs.
17. **RGB exactly as its screenshot**: three curved arc sliders, red / green /
    blue, each a black-to-full-channel gradient with a knob and a typeable
    integer box.
18. **Redesign the eyedropper icon and remove its border/frame.** `[§10 18]`
19. **Cells are too small and the faces too cramped.** `COPIC`, `HSL` and `RGB`
    all need a **larger hollow centre** — more empty space inside the ring.
    `[§10 15]`
20. **Increase the height of the colour cells**, and make the innermost ring's
    cells read closer to **square**.
21. **Colour names are slightly off** — verify each label against its swatch.
22. **Add more colours.** ⚠️ **Send the user a before/after image of the wheel
    and get approval BEFORE committing.**
23. **Text colour must be modifiable from the COPIC wheel.**
24. **Rotate with the scroll wheel and with horizontal/side scroll.** `[§10 24]`
25. **A `Colors` tab beside `Brushes`** in the same floating panel, reached from
    the **star icon** in the colour wheel. Per its screenshot: `Current Color`
    with a swatch; read-only fields `COPIC`, `HEX`, `R/G/B`, `H/S/B`, each with
    a gradient underline; the hint *"You can drag the color preview to any of
    your custom palettes below."*; **`My Palettes`** with an `Add` button and a
    grid of named 8-colour strips (`Concepts bright`, `My Palette`,
    `Calm Pastel`, …) plus `+` placeholders, with the hint *"Make palettes of up
    to 8 colors by dragging colors from anywhere - even other apps. To mix
    between colors, just tap-hold-drag the palette on canvas."*; and
    **`Dynamic Palettes`** — `Analogous`, `Monochromatic`, `Complementary`,
    `Shades`, `Triads`, `Most Used Colors`, `Recently Used Colors`.

### 11.4 Tools and the writing bar

26. **Dictation moves to the writing bar. Recording moves to the Quill
    dropdown. Remove the microphone options from the top bar.**
27. **Remove "leave free space" from the top bar**; add it as a tool in the
    Brushes panel.
28. **Eyedropper becomes a selectable tool.** `[§10 28]`
29. **Ruler leaves the pen row and becomes a tool**, tiltable by a two-finger
    gesture, with a **tilt visualiser** clickable by mouse to type an exact
    angle. `[§10 29]`
30. **Toolbar button-hiding behaves oddly now that not every button is present**
    — rework it.

### 11.5 Top bar

31. **Make the top bar about 15% THICKER.** This reverses §10.6, which took it
    from 74 to 52 — the user has now seen that and wants some height back.
32. **Icon sizes are inconsistent** — all equal in normal mode, scaled
    proportionally together in touch mode. `[§10 32]`
33. **Undo and redo leave the top bar when the dial is active.** `[§10 33]`
34. **History moves into the Quill dropdown**, opening a right-side floating
    panel in the Settings/Export family. `[§10 34]`
35. **Page name and date come off the page and into the top bar.** `[§10 35]`

### 11.6 Settings and floating panels

36. **More tabs beside `Workspace` and `Interaction`.** Workspace is judged
    correct; **Interaction is messy** and must be split further.
    ⚠️ **Ask the user which tabs they want before building.**
37. **Add the Interaction settings shown in the screenshots**, which are far
    richer than what exists: `Keyboard & Mouse` (edit-shortcuts link, enable
    toggle); `Touch Input` → `Finger Action` as circles (`Do Nothing`,
    `Use Active Tool`, `Pan Canvas`, `Select`, `Nudge`, `Slice`, `Zoom`,
    `Rotate`); `Two Fingers` toggles (`Enable Canvas Zoom`, `Enable Zoom Snap`,
    `Enable Canvas Rotation`, `Enable Rotation Snap`); `Tap & Hold` circles
    (`Last Used`, `Do Nothing`, `Lasso`, `Item Picker`, `Color Picker`) with an
    `Activation Time` slider and a `Highlight selection` toggle; `Draw & Hold`
    with `Enable Shape Recognition` and its own activation slider;
    `Two / Three / Four Finger Tap` rows of circles (`Do Nothing`, `Undo`,
    `Redo`, `Select Last`, `Show Layers`, `Show Colors`, `Tool Setup`,
    `Show Objects`, `Toggle Canvas Rotation`, `Toggle Canvas Zoom`,
    `Select All`, `Toggle Interface`); `Stylus` → `Pressure Response` as a
    **two-handle range slider** (`0% - 100%`), `Preferences` toggles
    (`Enable Pressure`, `Enable Tilt`, `Enable Tap & Hold`,
    `Enable Artboard Drag`, `Enable Hover Brush Previews`);
    `Side Button / Right Mouse Button` circles; `Eraser Action` circles
    (`Soft Mask`, `Hard Mask`, `Slice`, `Nudge`) with a `Size` slider; and
    `Top Button: Click / Double Click / Long Press` rows.
38. **Mouse modes move into Interaction as circles.** `[§10 38]`
39. **Bigger margins between subtitles and their explanation text.** `[§10 39]`
40. **Font is too big** — reduce it, and add a **developer font-size setting for
    every panel**. `[§10 40]`
41. **Remove top, bottom and side resize handles from every floating panel —
    corner handles only.** `[§10 41]`
42. **Constrain floating panels.** They may not be resized past a limit, must
    leave a margin at the page edge, and **must never cover the top-left
    cluster** (gallery, page name, Layers, Precision, Objects) **or the
    top-right cluster** (zoom/tilt, AI, Import, Export, Settings). They open as
    high as possible, and the **top corner resize handles are removed.**
43. **Theme circles: remove "dark appearance"; add a white `Light` circle and
    rename the black one `Dark`.** `[§10 43]`
44. **Switching measurement category must not auto-select the first item.**
    `[§10 44]`
45. **All Quill-specific settings must match the rest of the panel's styling.**
46. **`Precision` and `Layers` panes go top and bottom** (either order).
47. **Objects library glitches when scrolled sideways.** `[§10 47]`
48. **A vertical wheel over a horizontal strip scrolls it horizontally.**
    `[§10 48]`

### 11.7 Page background

49. **`Custom colour` moves to the FRONT of the background swatch row** and
    gains an **`Edit Colour`** link to the right of the `Background` heading,
    styled like `Edit Grid`. `[§10 49]`
50. **`Edit Grid` is redesigned to open the full grid editor pane** described in
    §10.7 — presets, vanishing points, horizon tilt, centre position, density,
    line weight, colour, opacity, orientation, confine-to-artboard, with a live
    preview and a `< Back` link.
51. **Guidelines move into the `Grid Type` category.** `[§10 51]`

### 11.8 Brushes panel

52. **A live preview of the currently selected brush** — the reference draws the
    stroke large on the transparency checkerboard, updating with the selection.
53. The `Subscribed` section lists packs with a name, a one-line description, a
    cover thumbnail and a horizontally scrolling strip of brush thumbnails
    (`Waterful` → `Watercolor A1`…; `Tiling Patterns` → `Wood Parquet 1`…).

### 11.9 Text mode

54. **Quick-action buttons above the text bubble** for text modification, per
    the screenshot: a `Cancel Editing` affordance with a red X, and a row of
    attach / duplicate / lock / delete marks.

### 11.10 New, larger pieces

55. **A user system** — plan it and write the design as a markdown file
    alongside the other docs: accounts, collaboration, sharing.
56. **A web viewer for Quill.** The user marks this *"not important maybe do it
    later"* — do not start it without asking.

### 11.11 Decisions, 2026-08-08 — answered by the user

**Settings tabs (resolves §11.6 item 36).** Four tabs:
**`Workspace` · `Interaction` · `Gestures` · `Stylus`.**

- `Workspace` — unchanged; the user judges it correct as built.
- `Interaction` — keeps `Finger Action` and the **mouse modes** (§11.6 item 38).
- `Gestures` — takes `Tap & Hold`, `Draw & Hold`, and the
  `Two / Three / Four Finger Tap` rows.
- `Stylus` — takes `Pressure Response`, the `Preferences` toggles,
  `Side Button / Right Mouse Button`, and `Eraser Action`.

`Keyboard & Mouse` stays in `Interaction`. The §11.6 item 37 content is
distributed across these four rather than piled into one tab.

**COPIC colours (resolves §11.3 item 22).** Add **only the real Copic codes that
are missing.** The Sketch range is 358 markers; the wheel holds 316, so roughly
42 genuine codes are absent. Add exactly those, calibrated by the same method as
the existing 316 — **no interpolated swatches, no invented families, nothing
that is not a marker you could buy.** Every added cell must carry its true code.

The approval gate stands: **produce a before/after image of the wheel and get
the user's approval before committing.**

**The two new dial cells (resolves §11.2 item 11).** Ship them **empty and
customisable**, each showing a `+` mark, assigned by the user — which is what
the Concepts reference itself shows for an unassigned cell. Do **not** pre-fill
them with the eyedropper, ruler or mix tools; those live in the tool library
until the user places them.

**Order of work.** Bugs first, then the dial, then the panels:

1. §11.1 — the settings scroll-reset (5/5) and the 16000-size square preview.
2. §11.2 / §11.3 — the dial geometry rebalance and the colour wheel.
3. §11.6 / §11.7 — the settings rebuild, the tab split, and the grid editor.

The reasoning the user endorsed: fix what is hit constantly before changing how
things look.

### 11.12 Colour wheel — scale up, 2026-08-09

Supersedes §11.3 items 15, 18, 19 and 20, which asked for the same thing in
smaller pieces. Against the current build the user's verdict is that **the whole
wheel is under-scaled**: *"make the colour wheel; copic, rgb, hsl text;
eyedropper (basically everything) bigger. widen the width of the cells in copic
colour wheel."*

Concretely, from the reference capture:

1. **The `COPIC` / `HSL` / `RGB` face labels are far too small** relative to the
   ring, and `HSL` and `RGB` render as bare grey text while `COPIC` sits in a
   chip. Scale all three up substantially and give them consistent treatment.
2. **The eyedropper is too small** — it reads as a dark dot at this size. Scale
   it with the rest. (§11.3 item 18 also removes its border/frame.)
3. **The COPIC cells are too narrow radially — widen them.** This is the "width"
   the user means: the cell's extent from the ring's inner edge outward, not its
   angular span. It pairs with §11.3 item 20, which asked for more cell height
   and near-square cells on the innermost ring.
4. **Everything else in the wheel scales with them** — the recents dots, the
   `Black`/`White` chips, the numeric chips on the HSL/RGB faces, and the
   swatch labels.

The constraint that makes this non-trivial: the wheel must still **centre on the
dial's colour dot** (§11.2 item 15, measured at Δ 0.00 DIP) and must not swallow
the dial, so growing the cells cannot come out of the hole. Take the space
outward, and if the ring then overruns the window, clip at the edge rather than
shrinking the hole or moving the dial — the user has rejected both of those
twice.

⚠️ **Approval gate.** Render a before/after of the wheel and get the user's
approval before committing, exactly as §11.3 item 22 requires for the added
colours. Two visual changes to the same surface, one approval step.

### 11.13 Colour wheel — grow the hole and the outer radius, 2026-08-09

The user has approved the §11.12 scale-up (*"great what you did with bigness"*)
and now wants the wheel bigger again, in two specific ways:

1. **The empty centre of the COPIC wheel gets bigger** — a larger hole.
2. **The overall circle radius gets bigger** — a larger outer edge.

**This partially reverses the constraint in §11.12**, which said the extra cell
width must come outward and must not come out of the hole. That instruction was
written to protect readability: the hole had previously been 220 DIP around a
116 DIP dial, and shrinking `HubRoom` from 104 to 82 (hole 198) is what stopped
the mode plates landing on the dial's popped sector. **That reasoning no longer
binds, because the outer radius grows at the same time** — both edges move
outward together, so the ring band is preserved rather than being squeezed from
one side.

What must still hold:

- **The wheel stays centred on the dial's colour dot**, measured at Δ 0.00 DIP.
  Growing either radius must not disturb that.
- **The hub chrome must not land on the dial.** The bug that made the old large
  hole unreadable was the mode plates being laid over the popped sector, not the
  hole size itself. With a larger hole this hazard returns — re-verify it, do
  not assume the earlier fix still covers the new geometry.
- **If the ring overruns the window, clip at the window edge.** Do not shrink
  the ring, do not re-centre it, and do not move the dial. The user has rejected
  all three.

⚠️ Same approval gate: render a before/after and get approval before committing.

### 11.14 Colour wheel — final geometry and label treatment, 2026-08-09

The user reviewed the §11.13 render and refined it. This **supersedes §11.13's
"widen cells"**, which they have now retracted, and settles the contradiction
between "9% smaller" and "keep current total radius".

1. **Shrink the whole wheel by 9%.** A scale change on the entire control, and
   **separate from** the text reductions below — the two do not compound into
   one factor, they are applied independently.
2. **Widen the inner empty circle.** §11.13's enlargement stands (hole
   198.62 → 256.62 DIP) and remains wanted.
3. **Do NOT widen the cells.** Retracted. If §11.13's uncommitted work deepened
   them, revert that part — cell radial depth returns to the §11.12 value.
   (For the record, when the user says a cell's *width* they mean its **radial
   depth**, measured from the centre outward; *height* is the arc direction.)
4. **COPIC swatch labels: font 20% smaller**, and **positioned at the upper-left
   corner of each cell** rather than centred.
5. **The `COPIC` / `HSL` / `RGB` face plates**: **greatly reduce the border**,
   **text 15% smaller**, and **bold**. Smaller and heavier at once is
   deliberate — do not preserve the size to keep the weight.

Item 5 may resolve the `_ui` cap fork on its own: the cap was pinned at 1.10
because five plates at §11.12's item sizes no longer fit the quadrant a
corner-docked dial leaves visible, which forced four items at 0.52 rad and a
0.18 rad clockwise roll. Lighter frames and smaller type free arc length —
**re-check whether the cap is still needed** and relax it if it is not.

Unchanged and still binding: the wheel stays centred on the dial's colour dot
(Δ 0.00 DIP — re-measure and report after any geometry edit), hub chrome must
not land on the dial's popped sector (re-verify against the new geometry rather
than assuming), and if the ring overruns the window, **clip at the window edge**
— never shrink the ring, re-centre it, or move the dial.

⚠️ Approval gate stands: before/after render, approval before commit.

### 11.15 Colour wheel — the settled numbers, 2026-08-09

**Supersedes §11.14 entirely.** The user ruled on §11.14's contradiction ("as
built, cells absorb the shrink") and then immediately replaced the whole
instruction with new figures. These are the ones to build.

1. **COPIC wheel 15% smaller.** Read as the **outer** extent: the ring's outer
   radius comes in 15%.
2. **The empty inner circle keeps its CURRENT, pre-shrink radius** — the
   256.62 DIP hole from §11.13. It does **not** scale with item 1.
3. **Texts and the other elements in the colour wheel shrink 20%** — swatch
   labels, the `COPIC`/`HSL`/`RGB` plates, the eyedropper, the recents chips,
   the value boxes. Applied **independently** of item 1; the two do not
   compound.
4. **Cells get 15% MORE depth** — more distance from the inside of the ring
   outward. This **reverses §11.14 item 3**, which retracted the widening;
   widening is back on, at +15% over the §11.12 depth of 27.08.

   Note the consequence and report it: with the hole pinned, the outer radius
   pulled in 15%, and each cell 15% deeper, **fewer rings of colour fit
   on-screen at once**. That is arithmetic, not a bug — but say how many rings
   survive so the user can judge.

5. **Fix the overlapping margins.** The user: *"the margins of text and colour
   wheels are off, they overlap."* Labels are colliding with the swatch ring
   and with each other. Give every text element a real margin against the
   geometry around it and verify no two drawn elements intersect.

6. **HSL and RGB must match their screenshots exactly.** Both are **curved arc
   sliders**, not the ring layout:

   - Each channel is **one thick arc** with round caps, swept about the wheel's
     centre, each at its **own radius and its own angular span**, arranged so
     no two arcs touch.
   - Each arc is a **gradient along its length**: RGB channels run black → full
     channel (red, green, blue). HSL runs hue → the full spectrum, saturation →
     grey to the current hue, lightness → black through the hue to white.
   - Each carries a **round knob** filled with the current value's colour,
     slightly wider than the arc.
   - Each has a **value box** beside the knob, outside the arc: a white rounded
     rect with a hairline border and dark text. RGB shows integers (`216`,
     `175`, `232`); HSL shows `317°` for hue and percentages (`55%`, `80%`).
   - The `COPIC` / `HSL` / `RGB` labels sit in a column to the left of the arcs;
     the **active** face is the one drawn in a filled chip, the others plain.

Everything still binding: the wheel stays centred on the dial's colour dot
(Δ 0.00 DIP — re-measure and report), hub chrome must not reach the dial's
popped sector at radius 116.62 (report the clearance table), and window overrun
is **clipped at the window edge** — never shrink, re-centre, or move the dial.

⚠️ Approval gate stands: before/after render, approved before commit.

### 11.16 Colour wheel — margins, type and box styles, 2026-08-09

The user supplied a capture of the wheel and said: *"copy the exact margins and
writing, box styles of this image and the images in my last message."* The
"last message" images are the HSL and RGB arc-slider captures already
transcribed in §11.15 item 6.

These are captures of **Quill's own build**, not an external app, so this is a
"preserve and match this styling" instruction rather than a port. Transcribed
below; measurements are from a ~1280 px-wide capture and are proportions rather
than absolute DIP.

**Face labels — `COPIC` / `HSL` / `RGB`**
- The **active** face sits in a **filled chip**: light neutral ground, corner
  radius small (~6 DIP), horizontal padding roughly double the vertical
  (~14 / ~8), text near-black.
- The **inactive** faces are **plain text — no box, no border, no ground** — in
  a muted grey, at the **same type size** as the active one. Only the chip
  distinguishes them.
- All three sit on an **arc concentric with the wheel**, stepping down and
  left, roughly evenly spaced. They are not a straight vertical column.

**Eyedropper**
- A **bare glyph**: no frame, no border, no background plate, no chip. Just the
  mark, at roughly the same visual weight as a face label.

**Swatch cells**
- **Square corners — no rounding anywhere.**
- Cells **within a family touch** edge to edge, with no gap between neighbours.
- **Families are separated by a visible gap** of background.
- A **clear background band separates the inner spine ring** (the `C`/`N`
  neutrals, `White`, `Black` and the numeric chips) **from the outer family
  fans**. That band is a real margin, not an artefact.
- The **inner spine ring is radially narrower** than the family cells.

**Swatch label type**
- Small relative to the cell, **rotated to follow the ring**.
- **Colour flips for contrast**: near-black on light swatches, white on dark
  ones (`N9`, `N8`, `Black`, the dark `C` neutrals all carry white text). This
  must be derived from the swatch's luminance, not from a hand-maintained list.

**⚠️ One conflict to resolve, not to guess at.** §11.15 item 4 says swatch
labels move to the **upper-left corner** of each cell. In this capture they read
as **centred**. Ask the user which they want before building — do not silently
pick. Everything else above can proceed.

### 11.17 Correction and additions, 2026-08-09

**§11.16 was framed wrongly and is corrected here.** I described the wheel
capture as Quill's own build and therefore as a "preserve and match this
styling" instruction. **It is Concepts.** It is a *target to reach*, not a state
to keep. The user's words: *"do not say they are the same again quill is nothing
like this."*

Everything §11.16 transcribed about margins, type and box styles still holds —
it was an accurate reading of the image — but it must be treated as a
specification of where the wheel needs to GET TO, and the gap between Quill's
current wheel and that target is large. Study the structure below before
concluding any part already matches.

**Structure of the Concepts wheel, read from the capture**

- An inner **spine ring**: one narrow band of neutrals at small radius, holding
  `White`, `Black`, the numeric chips (`0`, `100`, `110`), the `C1`–`C10` and
  `N0`–`N9` grey ramps, and the fluorescents (`FV2`, `FB2`, `FBG2`, `FYG2`,
  `FYG1`, `FY1`, `FYR1`, `FRV1`). It is **radially narrower** than everything
  outside it.
- Then a **band of bare background** — a real gap, not an artefact.
- Then the **family fans**. Each family (`E`, `Y`, `YG`, `G`, `BG`, `B`, `BV`,
  `V`, `R`, `RV`, `YR` …) is a **block of cells in rows and columns**, radiating
  outward: darkest values at the inner edge, lightest tints at the outer edge,
  so `E99 … E93` sits inboard of `E30 … E0000`.
- Cells **touch edge to edge within a family**; **families are separated by
  visible gaps** of background.
- Square corners throughout. Labels rotated to follow the ring, colour flipping
  to white on dark swatches.

### 11.18 Swatch labels — cornered. Settled.

The user: *"I never asked for a different position for swatch labels"* and
*"make them cornered."*

**Labels sit in the corner of each cell**, per §11.15 item 4's upper-left
placement. This is settled — do not raise it again, and do not read the centred
appearance of any capture as contradicting it.

### 11.19 Dimming while the colour wheel is open

The user: *"make greying while colour wheel is open just reduce opacity of icons
/ settings / panels in page, the ones on the upper corners."*

When the colour wheel opens, Quill currently lays a **grey scrim** over the
page. Replace that:

- **No scrim.** Nothing is painted over the page.
- Instead, **reduce the opacity of the chrome itself** — the top-left cluster
  (gallery, page name, Layers, Precision, Objects), the top-right cluster
  (zoom/tilt, AI, Import, Export, Settings), and any open floating panel.
- The **page, its ink, and the radial dial stay at full opacity.** In the
  reference the dial is fully saturated while the top-bar icons are visibly
  faded — that contrast is the whole point of the effect.
- Restore on close.

### 11.20 Wheel, dial and chrome — 2026-08-10

**Wheel geometry**

1. **Swatch names have too much upper margin** inside their cells — tighten it.
2. **The two inner rings have a different cell depth from the outermost ring.
   Equalise them: all rings take the OUTERMOST ring's depth.**
3. **Then deepen every cell by a further 20% outward**, keeping the inner empty
   radius exactly where it is. Growth goes outward only.
4. **Everything that opens when the colour wheel is pressed shrinks 20%**,
   proportionally — the whole surface, not selected parts.
5. **The frame around the `COPIC` / `RGB` / `HSL` labels shrinks 20%.**

**Face switching**

6. **Animate switching between COPIC / RGB / HSL.** The outgoing face's elements
   **gravitate inwards one by one** — the existing closing animation — and only
   then does the incoming face play its open animation. Sequential, not
   crossfaded.

**Arc sliders**

7. **RGB: three dials on a SINGLE arc**, ordered **anticlockwise: red, green,
   blue.**
8. **HSL: two arcs.** First arc carries the **hue wheel**; the second carries,
   **anticlockwise, saturation then lightness.**
9. **The value boxes must be typeable** — a real text field, not a readout.

**Hover targets**

10. **The hover outlines for opacity, size, stability, undo and redo are wrong.**
    The user: *"even if you press outside of the hover outline but in their arc
    area it registers, so you just need to fix the hover outline."* The **hit
    test is correct and must not change** — the drawn outline is what is wrong.
    Make the outline match the real hit region (the arc sector), rather than
    shrinking the hit region to match the outline.

**Icons**

11. **A new eyedropper** was supplied at
    `C:\Users\irony\Downloads\background-removed.svg` — a filled dropper with a
    detached droplet. **Implement it globally**, at every eyedropper site.
12. **Design a better colour-picker icon** (distinct from the eyedropper) and
    **ask the user before implementing it globally.**

**Moves**

13. **Dictation moves to the writing bar.**
14. **History moves to the Quill icon dropdown**, opening a floating panel in
    the Settings / Export / Objects family.
15. **Mouse modes move into the Interaction page and come OFF the top bar
    permanently.**

**Objects**

16. **Shapes in the objects library are drawn with the current pen style and
    pen colour**, rather than a fixed preview style.

### 11.21 The wheel grows instead of dropping colours — 2026-08-10

The user ruled on the 9-rings-of-17 trade, and the answer **supersedes §11.15
item 1**, which pulled the outer extent in 15%:

> *"increase radius to facilitate cell depth, do not remove any cell, the cells
> can go out of the screen, thats why rotation is there."*

So:

1. **No swatch is ever dropped.** All 17 rings render. A wheel that shows only
   part of the palette is not acceptable, and trimming rings to fit is not a
   permitted way to satisfy any size instruction.
2. **The outer radius grows to whatever the full palette needs** at the
   equalised, +15% cell depth — roughly `hole + 17 x depth`. §11.15 item 1's
   15% reduction of the outer extent **no longer applies**; the geometry is
   driven by the palette, not by a target radius.
3. **Running off-screen is expected and fine.** Clip at the window edge, exactly
   as every earlier revision required — never shrink the ring, never re-centre
   it, never move the dial.
4. **Rotation is the access mechanism**, which makes it load-bearing rather than
   a convenience: off-screen swatches are reachable only by spinning the wheel.

   **Mouse-wheel scrolling must rotate the COPIC wheel**, along with horizontal
   / side scroll. This has now been asked three times (§10.4 item 17, §11.3 item
   24) and implemented but never visually confirmed — **confirm it on screen
   this time**, and confirm it reaches the outermost ring's furthest swatch.

Everything else from §11.15 stands unchanged: the hole stays pinned at its
current radius, cell depth is equalised to the outermost ring and then deepened
15%, type and the other elements stay at 0.80x, and the label frames stay at
their reduced size.

**Dimming (§11.19) is settled as built:** the two icon clusters and any open
floating panel dim; the breadcrumb row (title, undo/redo, history) stays at full
strength, as does the page, the ink and the dial.

### 11.22 Dial inner circle, panel dismissal, brush picking — 2026-08-10

1. **The inner circle's division is wrong. Replace it with four equal quadrants**,
   angles measured with **0° at the right horizon (3 o'clock)**:

   | from | to | control |
   |---|---|---|
   | 135° | 45° | **Size** (top) |
   | 45° | 315° | **Opacity** (right) |
   | 315° | 270° | **Redo** |
   | 270° | 225° | **Undo** |
   | 225° | 135° | **Stability** (left) |

   So size, opacity and stability take one quadrant each, and the bottom
   quadrant is halved between redo (leading) and undo (trailing). Undo and redo
   therefore stay **inside** the circle per §11.2 item 10, but as bottom-quadrant
   halves rather than free-floating buttons.

2. **Icons for opacity, size and stability grow 20%.**

3. **Clicking outside the opacity / size / stability panel closes it.** Today it
   stays open. A press anywhere beyond the panel's own bounds dismisses it, and
   that press must still reach whatever is underneath — do not swallow it on a
   full-screen scrim, which is the pattern §11.19 just removed elsewhere.

4. **Right-clicking a tool — on the pen row or on a dial sector — opens the
   Brushes library** with that slot as the target, so a pen can be chosen for it.
   This is the assignment path the dial's `+` cells need too.

5. **Some Brushes-library previews render nothing.** The user's own diagnosis,
   and it is almost certainly right: *"probably because of a size bug where the
   pen has too large a size to register a meaningful stroke to preview."*

   Treat that as the same class of fault as §11.1 item 2 — the square preview
   that turned out to be a radius floor which barely consulted the pen while the
   renderer added stroke width *outside* the clamp. **Find the real cause; do not
   clamp the preview to hide it.** `InkSurface.MaxStrokeWidth()` already computes
   the true per-pen width from the real points, and a pen at 22.2 measured
   78.32 DIP of width — 3.5×. A preview strip sized without asking that question
   will be blown out by exactly the same pens.
