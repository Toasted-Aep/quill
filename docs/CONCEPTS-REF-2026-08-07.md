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
    **paperclip / padlock / duplicate / waste bin** marks — §16.2's order, which
    this originally contradicted at positions 2 and 3. §16.2 was transcribed
    from the capture and wins; two orders for the same four marks, on two
    surfaces reached for the same object, is a defect either way.

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

### 11.23 Decisions, 2026-08-10

**Colour-picker icon: candidate B, the palette.** A tilted oval with a thumb
hole near the right rim and four wells arcing along the far edge. Implement it
globally at every colour-picker site.

One measured caveat that must be addressed rather than shipped as-is: at 18 DIP
the palette's wells close to **0.04–0.13 coverage** (0 = fully open) against the
wheel candidate's 0.26–0.39, so it degrades toward a solid blob at the smallest
size. **Keep the palette design; open the wells enough to survive 18 DIP** —
larger wells, fewer of them, or a thinner rim, whichever preserves the read.
Verify by rasterising and measuring each well's coverage, not by eye.

**Brushes preview: the sweep adapts to the pen.** The sample stroke's amplitude
and padding are computed from the pen's **true** maximum width rather than from
constants, so every brush fits the strip and always shows a stroke shape. The
accepted cost is that the preview no longer conveys absolute size — a 5 px and a
50 px pen will read similarly. No caption or scale label.

**Both `BrushesWindow.cs` items are routed together**, since they share the file.

### 11.24 The Brushes library — targeting and preview sizing

1. **`BrushesWindow` needs a targeting entry point.** Its public surface is
   `Attach / Show / Hide / Toggle / IsOpen / Bounds / Refresh`, with no way to
   say *which* slot a chosen brush should land in. Add one, so that
   §11.22 item 4 — right-clicking a tool on the pen row or a dial sector opens
   the library aimed at **that slot** — can be completed.

   This matters beyond right-click: the dial's `+` cells use the same assignment
   path, and replacing `ShowAssign` with a picker that applies to the *active*
   tool instead of the target slot would silently lose it. That is why the
   previous agent stopped rather than doing the ToolWheel half alone.

2. **Fix the blank previews at the cause.** `SampleStroke` lays out a fixed
   sweep — `pad = 34`, amplitude `0.22 x 205 = 45.1` — while the stroke's width
   is the pen's real one, and pressure peaks at 1.0 mid-sweep, so the widest
   point *is* `MaxStrokeWidth`. `SegmentWidth` gives `PenType.Brush` a factor of
   `0.12 + 3.2 x sens x pr^2` — 3.32x at sens 1. The band the sample occupies is
   `2 x 45.1 + width`, so it floods the 205 DIP strip once width passes ~115,
   i.e. a Brush above about size 35. Past that the "stroke" is a solid slab edge
   to edge, which on a light or low-opacity pen is indistinguishable from an
   empty strip.

   Compute the sweep's geometry from `InkSurface.MaxStrokeWidth()` — the dial's
   preview circle already does exactly this — rather than from constants.
   **Do not clamp the pen.** This is the same class of fault as §11.1 item 2,
   where a radius floor barely consulted the pen while the renderer added stroke
   width outside the clamp; clamping there would have hidden a real defect.

### 11.25 Right-click assignment — the settled behaviour, 2026-08-10

Resolves the fork left open in §11.24. The user's ruling:

> *"right clicking opens up brushes library where there are also tools and you
> can select which item to assign there, the panel should close when the page is
> clicked unlike the other floating panels. when right clicking another tool do
> not open up another brushes pannel just use the one already opened and switch
> to the tool currently selected on the second right clicked cell."*

1. **One panel, brushes and tools together.** Right-clicking a tool — on the pen
   row or on a dial sector — opens the Brushes library, and that library offers
   **both brushes and tools** so either can be assigned from the one surface.
   The `Target.PickTool` hook already exists for this; it must now always be
   supplied for slot targeting rather than omitted.

2. **It closes when the page is clicked.** This is a deliberate exception to
   every other floating panel, which persist. Use the same dismissal the
   §11.22 item 3 settings card uses: a handled-events-too handler that **never
   sets `Handled`**, so the dismissing press still reaches the page and draws.
   **No scrim** — §11.19 removed exactly that pattern and the user does not want
   page-covering overlays.

3. **Right-clicking a second tool retargets the OPEN panel.** It must not open a
   second one, and must not close and reopen. The existing panel stays where it
   is and simply re-aims at the newly right-clicked cell — banner text, the
   highlighted cell and the preview strip all switch to the new slot.

   `ShowFor(Target)` should therefore be idempotent with respect to the window:
   if it is already open, swap the target in place and refresh; only create or
   show the window when it is closed.

**Retype in place is confirmed** (§11.24 fork 2, as built): choosing a brush for
a pen-row cell changes that cell's pen **type** while keeping its colour, size
and pressure curve. A tuned 22.2 calligraphy stays 22.2 and keeps its colour; it
just becomes a brush.

**The palette mark is held back** (§11.23). `Icons.Palette` remains defined and
measured but has **no call site** — the user asked for it not to be used until
they decide where a picker mark belongs. Do not wire it anywhere.

### 11.26 Two theme findings, 2026-08-10

**There was no `PageTheme` bug.** A reported `ground=#0F0E10 isDark=1` on an
ivory page was investigated in full, and **all five §7 rows pass on unmodified
main**. The measurement came from a scratch library, not from the app, and no
code needed changing.

**Harness trap — any agent seeding a scratch `library.json` MUST set
`ThemeSource: "Page"` explicitly.** The model defaults are
`ThemeSource = "Manual"` (`Models/NoteModels.cs:397`), `Theme = "Dark"`
(`:389`) and `OledBlack = false`, and those three together derive exactly
`#0F0E10` — while `DefaultBackground` defaults to ivory `#FAF9F5` (`:382`). An
unseeded library therefore renders a **light page with correctly-derived dark
chrome**, which looks precisely like a broken derivation and is not. This cost
one full investigation; do not let it cost another.

The hypothesis that `ResolveGround` was returning the manual/OLED ground while a
page was open is **wrong** — `MainWindow.xaml.cs:1974` takes the page branch
whenever `ThemeSource == "Page"` and a page is open, verified in every run.

**Gallery fallback, changed.** With `ThemeSource "Page"` and no page open
(startup, gallery) the ground used to fall through to the Manual branch, whose
`Theme` defaults to `"Dark"` — so a Page-mode user who never opened Settings got
a dark gallery and a dark startup flash from a setting they never chose. It now
uses the **last page's ground**, keeping the gallery continuous with the page
you came from. The Manual branch remains the fallback for a genuine first run,
where there is no last page.

Still open, deliberately untouched: `Library.Theme` defaulting to `"Dark"` is
what makes the defaults collide into `#0F0E10` in the first place. Changing it
would alter first-run appearance for genuinely new users, so it was left alone.

---

## 12. The grid editor — transcribed 2026-08-10

Ten reference captures of Concepts' grid editor. This supersedes §10.7 and
§11.7 item 50, which described the same pane in less detail. **Agents cannot see
the images; this is the only source.**

### 12.1 The page shell

Every grid type opens the same page inside the Settings floating window, under
`Grid Type`:

1. The window's own header stays — close **X**, `Workspace` / `Interaction`
   tabs, **(i)** — unchanged.
2. Directly beneath it, a **live preview strip**: full panel width, ~300 DIP
   tall, on a band a shade darker than the page, **rendering the grid exactly as
   configured**. It updates as any control below changes.
3. A **`< Back` button** floating over the preview's lower-left. Copy this style
   exactly:
   - a **white pill** — fully rounded ends, corner radius = half its height;
   - height ~46 DIP, width fits the label plus generous padding (~34 DIP each
     side);
   - inset ~20 DIP from the panel's left edge;
   - vertically **straddling the preview strip's bottom edge**, roughly half in
     and half out — it is not inside the strip and not below it;
   - label `< Back` in near-black, ~17 DIP regular;
   - a soft drop shadow; no border.
4. Then the grid's name as a **34 DIP bold heading** — `Isometric Grid`,
   `Graph Paper`, `2-Point`, `3-Point`.
5. Then the sections in §12.3, in the order listed there, ending with
   `Confine to artboard`.

### 12.2 Controls

**Preset circles.** ~86 DIP, thin `Outline` ring, containing a **miniature line
drawing of that preset's own grid** — not a generic glyph. Selected carries a
2 DIP `OnSurface` ring and a bold caption; unselected is muted. Captions sit
beneath, ~13 DIP, and wrap to two lines where needed (`1/2 Wide Below`,
`3/4 Ultrawide Below`). The row scrolls horizontally.

**Numeric rows.** A bold label, an optional grey caption line, a **right-aligned
typeable value box** (white rounded field, hairline border — `100 mm`, `1 pts`,
`10`, `30`, `20%`), and beneath them a **full-width slider**: 2 DIP `OnSurface`
track, white round knob with a hairline. The box is editable directly; the
slider is never the only way in.

**Colour.** Caption *"Automatic color adapts to your background color. Custom
colors are independent of the background color."*, then two circles —
`Automatic` (unfilled) and `Custom` (filled with the chosen colour).

**Orientation.** Two circles, `Landscape` and `Portrait`, drawn as a rounded
rect ruled horizontally and one ruled vertically.

**Confine to artboard.** A square **checkbox**, not a toggle, labelled
*"Only show the grid lines inside the artboard."*

### 12.3 Which sections each grid gets

**`Orientation` appears only where rotating the grid actually changes it.** A
square or dot grid is unchanged by a 90° turn, so it must not carry the control
at all — the user was explicit: *"if rotation does not change the form, do not
have it in page."*

| grid | preset | spacing | divisions | vanishing | density | line weight | colour | opacity | orientation | confine |
|---|---|---|---|---|---|---|---|---|---|---|
| Dot | – | ✓ | – | – | – | ✓ | ✓ | ✓ | **no** | ✓ |
| Lined | ✓ | ✓ | – | – | – | ✓ | ✓ | ✓ | ✓ | ✓ |
| Graph | ✓ | ✓ | ✓ | – | – | ✓ | ✓ | ✓ | **no** | ✓ |
| Isometric | ✓ | ✓ | – | – | – | ✓ | ✓ | ✓ | ✓ | ✓ |
| 1-Point | ✓ | – | – | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| 2-Point | ✓ | – | – | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| 3-Point | ✓ | – | – | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

`Divisions` caption: *"Set the number of divisions between main lines. Set value
to 1 to only show the main lines."*
`Spacing` caption: *"Set the spacing of your grid. The units are determined by
the document units."*
`Density` caption: *"Set the number of vanishing lines per point."*

### 12.4 The baked-in presets

Exactly these, plus a trailing **`Custom`** in every row:

- **Lined** — `Narrow`, `Medium`, `Wide`
- **Graph Paper** — `Square Grid`, `10 / 100`, `16 / 64`
- **2-Point** — `2 Point`, `1/2 Narrow`, `1/4 Narrow`, `Side Narrow`,
  `1/2 Wide`, `1/4 Wide`, `Side Wide`, `1/2 Wide Below`, `Side Ultrawide`
- **3-Point** — `3 Point`, `3/4 Narrow`, `1/2 Narrow`, `3/4 Wide`, `1/4 Wide`,
  `Side Wide Below`, `1/4 Wide Below`, `3/4 Ultrawide Below`, `3/4 Ultrawide`
- **Isometric** — `Isometric`
- **1-Point** — `1 Point`

The perspective preset names describe **where the vanishing points sit**:
`Narrow` / `Wide` / `Ultrawide` is how far apart they are, `1/2` / `1/4` / `3/4`
is where the horizon crosses the frame, `Side` puts a point off the edge, and
`Below` drops the third point beneath the horizon rather than above. Each
preset's circle shows a small cube or fan drawn under that configuration —
which is why the thumbnails differ from one another and cannot be one shared
glyph.

### 12.5 Perspective grids must match Concepts

**Move 1-Point, 2-Point and 3-Point into the `Grid Type` section** alongside the
other grids, and render them as Concepts does: a horizon line, vanishing points
on it, and a **fan of fine lines radiating from each point**, dense near the
point and spreading outward, plus the horizontal/vertical family each
configuration implies. The reference draws them very fine and low-contrast —
they read as a wash of blue-grey guides, never as heavy black lines.

### 12.6 `Edit Points` and the vanishing-point editor

**Only the perspective grids get this.** In the page, under a `Vanishing Points`
heading:

- an **`Edit Points`** button — label in `Accent`, bold, ~17 DIP. One capture
  shows it plain on the panel ground and an earlier one shows it on a filled
  `SurfaceAlt` rounded rect; **build the plain form** and treat the filled one
  as its pressed/hover state.
- beneath it, the caption *"You can edit the vanishing points with a tap & hold
  on canvas or by activating the grid layer."*

**Pressing it dismisses the panel and enters an on-canvas editing mode:**

- A floating label at the **top centre** of the canvas reading
  **`Editing Grid.`** in near-black with **`Done`** beside it in `Accent`, on a
  light rounded background.
- The **horizon line turns red**, spanning the full width.
- Each **vanishing point becomes a red ring** — an unfilled circle ~16 DIP,
  sitting on the horizon, draggable.
- A large thin **circle through the centre** marks the cone of vision, drawn in
  the grid's own colour rather than red.
- A small **crosshair** at the grid's centre point, draggable to move the whole
  grid.
- The pointer takes a **four-way move cursor** over a draggable handle.
- The radial dial **fades** while editing, exactly as it does for the colour
  wheel (§11.19) — the page and the grid stay at full strength.
- `Done` exits and returns to the page.

### 12.7 Observed but not requested

When a grid layer is active the reference shows a bottom-centre bar —
`+ Item Picker`, a lock and `Include`, and `All`. **Not part of this work**;
recorded only so it is not mistaken for something missing.

### 12.8 The vanishing-point editor, corrected from a live capture

A capture taken **mid-drag** supersedes §12.6 where they differ. It shows the
editor in its active state, which the earlier stills did not.

**The horizon tilts. It is not a fixed horizontal.** Dragging a vanishing point
**rotates the horizon**, and the whole grid re-solves under the new geometry —
the fans from every point re-render live during the drag, not on release. This
is the biggest correction: §12.6 implied points slide along a level horizon, and
they do not.

**A blue dashed reference line marks the level horizon.** While the red horizon
is tilted away from level, a **horizontal dashed line in pale blue** stays at
the untilted position, spanning the full width, so the amount of tilt is
readable at a glance. It is a reference only — not draggable. Presumably it
appears only while the horizon is off level; if it is always shown, say so.

**Handle states differ.** An idle vanishing point is an **unfilled red ring**.
The one being dragged is a **filled red dot** of the same size. That is the only
difference between them — no halo, no size change.

**The tilt angle appears in the top bar.** During editing the readout shows the
horizon's angle — the capture reads `2°`. The zoom percentage is not shown
beside it in that state.

**The centre crosshair is faint**, drawn much lighter than the red handles, and
sits at the grid's centre independent of the horizon.

**The cone-of-vision circle is pale blue**, not red, and passes through the
centre region — it is drawn in the grid's own colour family rather than the
handle colour, so it reads as part of the grid rather than as a control.

**The dial is faded** throughout, confirming §12.6.

**`Editing Grid. Done`** sits top-centre: `Editing Grid.` in near-black, `Done`
in `Accent`. On a white page no background is visible behind it; an earlier
capture on a grey ground showed a light rounded plate. Build the plate from
`PageTheme.Panel` so it disappears on paper and separates on a coloured ground —
that satisfies both captures.

### 12.9 The editor's real interaction model — supersedes 12.8

**§12.8 got this wrong and must not be built.** It said dragging a vanishing
point rotates the horizon. It does not. The user, on a further capture:

> *"make vanishing point snap to the horizon, exactly replicate the photo, it
> has a centre circle to rotate around horizon."*

Three separate handles, three separate jobs:

1. **Vanishing points slide ALONG the horizon.** They are **constrained to the
   line** — a point can never leave it. Dragging one moves it left and right
   along the horizon and does not change the horizon's angle. This is the
   "snap to the horizon" the user is asking for.
2. **The centre circle rotates the horizon.** The pale blue cone-of-vision
   circle carries a **red arc** on its rim — visible in the capture on the
   circle's right side, spanning roughly ±30° about the horizontal, drawn in the
   same red as the handles. That arc is the **rotation grip**: dragging it turns
   the horizon about the centre point. It is the only way to change the angle.
3. **The centre crosshair moves the whole grid.** Faint, at the circle's centre,
   independent of the horizon's angle.

So the tilt seen in the earlier mid-drag capture came from the **rotation grip**,
not from dragging a point — which is why a point appeared filled while the
horizon was already off level.

Everything else in §12.8 stands: idle points are unfilled red rings and the
dragged one is a filled red dot; a pale blue dashed line marks the level horizon
while the red one is tilted; the tilt angle shows in the top-bar readout; the
cone circle is pale blue rather than red; the crosshair is drawn much fainter
than the handles; the dial fades throughout; and `Editing Grid.` / `Done` sits
top-centre with the plate built from `PageTheme.Panel`.

The user's instruction on fidelity was emphatic — *"make it exactly like the
photo. EXACTLY."* Match the capture rather than approximating it: handle sizes,
the arc's extent and position on the rim, the circle's radius relative to the
page, and the weight and colour of every line.

### 12.10 The triangle grid, and an angle for it and isometric

1. **A triangle grid is missing — add it** to `Grid Type` alongside the others,
   with its own editor page built to §12.1.
2. **Both the triangle grid and the isometric grid gain an angle control** —
   a numeric row per §12.2 (bold label, typeable value box in degrees, full-width
   slider) that sets the angle of the grid's diagonals.

   Isometric's true value is 30°, and the triangle grid's equilateral case is
   60°; both should default to those and allow the user off them. Changing the
   angle must re-render the live preview like every other control.

Both keep `Orientation` under §12.3's rule, since rotating either does change
its form.

### 12.11 The angle control changes the CELL SHAPE, not the grid's rotation

§12.10's angle control was built as a rigid rotation of the whole grid. That is
wrong. The user:

> *"angle does not work correctly it rotates whole grid not the grid parts
> individually."*

**A rigid rotation is what `Orientation` already does.** An angle control that
also rotates the whole grid is a duplicate of a control further down the same
page — that redundancy is the tell that the implementation is wrong.

**What the angle actually parameterises: the inclination of the DIAGONAL line
families, measured from horizontal, with the grid's straight family left where
it is.** Changing it changes the shape of every cell.

- **Isometric** — a vertical family plus two diagonal families at **+θ** and
  **−θ** from horizontal. At θ = 30° the cells are true isometric rhombi.
  Lowering θ makes them flatter and wider; raising it makes them taller and
  narrower. **The verticals never move.**
- **Triangle** — a horizontal family plus two diagonal families at **+θ** and
  **−θ**. At θ = 60° the triangles are equilateral. Off 60° they become
  isosceles — narrower and taller, or squatter and wider. **The horizontals
  never move.**

Two checks that distinguish a correct implementation from a rotated one:

1. **The straight family must not move at all** as the angle changes. If the
   verticals in isometric, or the horizontals in triangle, tilt with the slider,
   the whole grid is being rotated.
2. **The cell shape must change.** Sweep the angle and the rhombi/triangles
   should visibly stretch and squash. Under a rigid rotation every cell keeps
   its shape and only the whole field spins — which is exactly what the user is
   seeing.

`Orientation` continues to do the rigid 90° flip, unchanged and independent of
this control.

---

## 13. `Panel` rides a ramp, not a switch — 2026-08-12

**Supersedes the `Panel` column of §7 and the `Panel` line in §6.** Everything
else in both sections stands unchanged.

The user: *"make app theme slightly gray if page isn't totally white (or an in
between colour between white and black) if closer to white and dark gray if
closer to black."*

`Panel` was `IsDark ? #141414 : #F7F7F7` — a hard step, so an ivory page and a
pure white page produced an identical panel, and a near-black page and a merely
dark one did too. It now interpolates continuously:

```
panelL* = 8 + 89 x relativeLuminance(ground)
clamped to <= 34 when IsDark, >= 84 when not
chroma zero
```

**Luminance rather than L\***, because luminance is the axis `IsDark` is decided
on — so the panel's shade and the text's colour can never disagree about which
side of the middle a page is on. **Chroma stays zero** because §6 is explicit
that panels are flat regardless of the page's hue, unlike `Surface` which
carries it.

**The clamps are a legibility floor, not taste.** Contrast against the text that
sits on the panel is worst exactly at the middle, where `IsDark` flips; the
clamps hold the panel away from it.

Measured across every ground:

| ground | Y | dark | panel was | panel now | contrast |
|---|---|---|---|---|---|
| Pure white | 1.000 | no | `#F7F7F7` | `#F6F6F6` | 17.0:1 |
| Lightweight | 0.947 | no | `#F7F7F7` | `#E9E9E9` | 15.2:1 |
| Rippled | 0.872 | no | `#F7F7F7` | `#D6D6D6` | 12.7:1 |
| Heavyweight | 0.778 | no | `#F7F7F7` | `#D1D1D1` | 12.1:1 |
| Mid grey | 0.216 | yes | `#141414` | `#404040` | 9.3:1 |
| Blueprint | 0.199 | yes | `#141414` | `#3D3D3D` | 9.7:1 |
| Brown Paper | 0.206 | yes | `#141414` | `#3E3E3E` | 9.6:1 |
| Darkprint | 0.024 | yes | `#141414` | `#1C1C1C` | 15.2:1 |
| OLED black | 0.000 | yes | `#141414` | `#181818` | 15.9:1 |

Minimum contrast across the whole range is **9.3:1**, comfortably past WCAG AA's
4.5:1 — so no ground produces an illegible panel.

**§7's acceptance table now expects these values, not `#F7F7F7` / `#141414`.**
This is a deliberate divergence from Concepts at the user's instruction; do not
"restore" the two constants to make the old table pass. The rest of §7 — which
grounds are dark, the text colours, the hued `Surface` discs — is untouched and
still binding.

Open question the user has not yet seen on screen: the ramp is steeper at the
light end than "slightly gray" might imply, because luminance is compressed
there. `Heavyweight` lands at `#D1D1D1`, which is grey rather than off-white. If
that reads as too much, the fix is to blend the ramp part-way back toward the
old constants rather than to change its shape.

### 13.1 The dark end, corrected — 2026-08-12

The user, having seen §13's first ramp:

> *"make the app theme totally black (oled black) if very close to black (like
> in darkprint) and very dark grey otherwise."*

The first ramp put Darkprint at `#1C1C1C` — near-black but not black — and put
Blueprint and Brown Paper at `#3D3D3D`, a *medium* dark grey rather than a very
dark one. The dark branch is now two cases:

```
ground luminance <= 0.05   ->  #000000, true black
otherwise (dark side)      ->  L* 10 + 16 x min(1, Y / 0.5)   -> a L* 10..26 band
```

**The step at 0.05 is the rule, not a rough edge.** A ground that is very nearly
black gets a panel that *is* black, rather than easing toward it — that is what
was asked for, so Darkprint and a pinned OLED black both land on `#000000`.

The **light branch is untouched**; §13 built it to the previous instruction and
the user did not revisit it.

| ground | Y | panel | contrast |
|---|---|---|---|
| Pure white | 1.000 | `#F6F6F6` | 17.0:1 |
| Lightweight | 0.947 | `#E9E9E9` | 15.2:1 |
| Rippled | 0.872 | `#D6D6D6` | 12.7:1 |
| Heavyweight | 0.778 | `#D1D1D1` | 12.1:1 |
| Mid grey | 0.216 | `#2A2A2A` | 12.8:1 |
| Blueprint | 0.199 | `#292929` | 13.0:1 |
| Brown Paper | 0.206 | `#292929` | 13.0:1 |
| **Darkprint** | 0.024 | **`#000000`** | 18.8:1 |
| **OLED black** | 0.000 | **`#000000`** | 18.8:1 |

Minimum contrast is now **12.1:1**, up from 9.3:1 — tightening the dark band
moved every dark panel further from its white text rather than closer.

**This supersedes §13's table and, with it, §7's `Panel` column.** Do not
restore `#141414` for the dark cases to make the original acceptance table pass.

### 13.2 Lighter at the light end, and panels carry the ground's hue — 2026-08-12

> *"for app theme: make light gray more lighter, and add creme and other various
> other colours like those."*

Two changes, and the second **reverses §6's rule that panels are neutral.**

**Lighter.** The light branch spanned L\* 84–97, which put Heavyweight on
`#D1D1D1` — grey rather than off-white. It now spans **L\* 90–97** across the top
half of the luminance range. The papers stay distinguishable from one another
inside the narrower band.

**Hue.** `Panel` was deliberately neutral, because §6 said panels are flat
whatever the page's hue, unlike `Surface` which carries it. **That no longer
holds.** `Panel` now carries a fraction of the ground's a/b:

- `LightPanelChroma = 0.85` — light panels take most of it. That is where cream
  lives, and where a tint reads at all.
- `DarkPanelChroma = 0.35` — dark panels take far less, so a dark surface hints
  at its page instead of becoming a coloured slab.
- **Pure black keeps zero chroma.** Black asked for is black, not near-black
  wearing a cast.

| ground | was | now | |
|---|---|---|---|
| Pure white | `#F6F6F6` | `#F6F6F6` | unchanged |
| Lightweight | `#E9E9E9` | **`#F5F4F1`** | warm off-white |
| Rippled | `#D6D6D6` | **`#F4F1EA`** | cream |
| Crumpled | `#DEDEDE` | **`#F3F0E8`** | cream |
| Heavyweight | `#D1D1D1` | **`#F1EDE4`** | cream |
| Mid grey | `#2A2A2A` | `#2A2A2A` | neutral ground, neutral panel |
| Blueprint | `#292929` | **`#182A3D`** | dark navy |
| Brown Paper | `#292929` | **`#372617`** | dark brown |
| Darkprint | `#000000` | `#000000` | true black |
| OLED black | `#000000` | `#000000` | true black |

Worst contrast across every ground is **12.8:1**, up from 12.1:1 — the lighter
light end moved those panels further from their dark text, so legibility
improved rather than being traded away.

**§6's "panels are flat regardless of the page's hue" is now superseded**, along
with §7's `Panel` column and §13/§13.1's tables. This is the third deliberate
divergence from Concepts on this value; do not restore neutrality or the two
original constants to make an older table pass.

### 13.3 Panel margins and edge-to-edge strips — 2026-08-12

> *"reduce page margins by 30%, make circular icons ignore the margins and go
> straight to the end of the page."*

**Two changes to the settings page's layout.**

1. **The content margin drops by 30%.** Whatever the panel's left/right content
   inset is today, multiply it by 0.7. Headings, captions, sliders, value boxes
   and toggles all follow it — they keep aligning with each other.

2. **The horizontally-scrolling circle strips ignore that margin entirely.**
   Every strip of circles — background swatches, grid types, units, presets,
   format/precision, orientation, Wheel|Bar — has a **viewport spanning the full
   panel width**, so a circle scrolls out to the panel's true edge instead of
   being clipped short at the content inset.

**The part that is easy to get wrong:** bleeding the viewport must not drag the
first circle to the frame. The strip's **content** keeps a leading inset equal
to the new content margin, so at rest the first circle still lines up under its
heading; only the *viewport* is edge-to-edge, so items pass under the panel's
edge as they scroll rather than vanishing at an invisible inner boundary.

So: headings and the first item stay aligned; the scroll region is wider than
the text column. Getting this backwards — moving the whole strip left, first
item included — misaligns every row against its own heading.

The scroll-position indicator rules under each strip should span the strip's
**content**, not the bled viewport, or they will read as offset.

---

## 14. Dial readouts, corner hitboxes, and where vanishing points belong — 2026-08-13

### 14.1 The inner disc's readouts sit wrong

Comparing Quill's dial against the Concepts capture, the three readouts are laid
out differently and must match the reference:

- **The size row moves UP.** `≡ 19.2 mm` sits nearer the top of the inner disc
  than Quill places it, further from the colour dot.
- **The opacity and stability values are OFFSET, not centred under their
  glyphs.** Quill puts each percentage directly beneath its mark. The reference
  pulls both **inward toward the centre and downward**: the stability value sits
  down-and-right of its waveform glyph, the opacity value down-and-left of its
  half-disc glyph, both closer to the disc's lower edge than to their marks.

So the disc reads as: size row high, the two glyphs on the horizontal midline
level with the colour dot, and the two values low and drawn in toward the
centre — not as two vertical glyph-over-value pairs.

### 14.2 Custom colour goes first

**`Custom colour` moves to the FRONT of the page-background swatch row.**
Carried from §11.7 item 49, still not done.

### 14.3 The close and help hitboxes overshoot their corner

The Settings close **X** and the help/info button have hit regions that are
**offset and extend outside the panel**, past its rounded corner and onto the
page behind.

This is a regression from the fix that made the close button "cover the whole
corner": it was given negative margins to escape the header's padding, and those
margins pushed the target beyond the panel's own bounds rather than filling to
its edge.

**The rule: each target fills its corner of the panel exactly — from the panel's
inner edge to the header's inner boundary — and stops there.** Nothing may
protrude past the panel's rounded corner. Verify by hit-testing just outside the
corner and confirming the press reaches the page, not the button.

### 14.4 No square grid under 1-Point

**Remove the square/lattice family from the 1-Point perspective grid.** A
one-point grid is a fan from a single vanishing point plus the horizon; the
square grid overlaid on it is wrong and is not in the reference.

### 14.5 Where vanishing points belong — quartering the reference frame

The current 2-Point puts its left vanishing point at the **far left edge of the
page**. That is wrong. The reference places the points by **quartering a
reference frame**:

- **quarter 1** — the first vanishing point
- **quarter 2** — the centre (the midpoint of the frame)
- **quarter 3** — the second vanishing point

and the same logic extends to 3-Point's arrangement.

**The reference frame is the viewport as it was on the page's FIRST frame** —
the view that opens immediately after the page is created, at whatever size and
zoom that screen gave it. So the frame is a property of the page, captured once,
and **differs between screens**: the same preset on a laptop and on a large
monitor produces points at different canvas coordinates, because a quarter of
one frame is not a quarter of the other.

Two consequences that must be handled deliberately:

1. **The frame has to be stored on the page** at creation. It cannot be derived
   later from the current viewport, because the user will have panned and zoomed
   by then — reading it live would move the vanishing points every time the view
   changed.
2. **Existing pages have no stored frame.** Give them one on first load, derived
   from their current view, and say in the report how that was done — a page
   whose grid silently jumps on upgrade is worse than one that never had it.

The named presets (`Narrow`, `Wide`, `Ultrawide`, the fractions, `Side`,
`Below`) then position relative to that quartering rather than to the window
edge, which is what makes `Side` mean "off the frame" instead of "at the edge of
whatever is currently on screen".

**The user's instruction on fidelity is emphatic and repeated: 1-Point, 2-Point
and 3-Point, with every variant, must look exactly like the captures.**

---

## 15. The preset catalogue, and fullscreen chrome — 2026-08-16

Ten new captures of the perspective presets, plus one of the fullscreen
top-right corner.

### 15.1 The presets are three per-type lists — 19 named entries, not a cross product

**Corrected 2026-08-17. What this section said before was that the catalogue is
the full cross product `{1/4, 1/2, 3/4, Side} × {Narrow, Wide, Ultrawide} ×
{—, Below}` = 24 two-point presets, plus `1 Point` / `2 Point` / `3 Point`, all
in one list. THAT WAS MY RECONSTRUCTION FROM THE TEN SCREENSHOTS AND IT WAS
WRONG.** The wrong version is spelled out here rather than quietly deleted,
because it was specific enough to build from and anyone who met it once could
rebuild it from memory or from a diff. There is no 24-entry catalogue anywhere in
Concepts, `1/2 Wide` was never a missing entry, and `3/4 Wide` and `1/4 Wide` are
not siblings.

**What Concepts actually ships**, enumerated from the running app (the run is
recorded in §15.5): **the presets are per grid type.** Precision ▸ Grid offers
nine grid types, and each of the three perspective types — `1-Point`, `2-Point`,
`3-Point` — opens its own `Edit Grid` editor carrying its **own** `Preset` strip.
Each strip ends in `Custom`. Left to right, verbatim:

**1-Point** — one named preset:

    1 Point   │   Custom

**2-Point** — nine named presets:

    2 Point   │   1/2 Narrow   │   1/4 Narrow   │   Side Narrow   │   1/2 Wide
    1/4 Wide  │   Side Wide    │   1/2 Wide Below   │   Side Ultrawide   │   Custom

**3-Point** — nine named presets:

    3 Point        │   3/4 Narrow          │   1/2 Narrow   │   3/4 Wide
    1/4 Wide       │   Side Wide Below     │   1/4 Wide Below
    3/4 Ultrawide Below              │   3/4 Ultrawide       │   Custom

**That is 19 named presets across three lists — 1 + 9 + 9 — plus one `Custom`
entry per list.**

**THE USER HAS RULED: mirror Concepts exactly, 19 across three lists. No cross
product.** Their earlier decision to ship 24 is **reversed** — it was taken on my
false premise, and the premise going takes the decision with it. Do not build the
cross product, and do not "fill the holes" in any of the three lists above: the
lists are not holed, they are curated, and each perspective type curates a
different set.

**Where the ten captures actually came from.** Nine of them — `3 Point`,
`3/4 Narrow`, `1/2 Narrow`, `3/4 Wide`, `1/4 Wide`, `Side Wide Below`,
`1/4 Wide Below`, `3/4 Ultrawide Below`, `3/4 Ultrawide` — are **exactly the
3-Point list, complete and in order**. The tenth, `Side Ultrawide`, is the
**ninth entry of the 2-Point list**. So the captures are a full sweep of one list
plus one stray from another, which is precisely why they read as a holed
catalogue: the apparent gap between `3/4 Wide` and `1/4 Wide` is a list
*boundary*, not a missing preset.

**The axes are vocabulary, not a grid.** The three name parts are real, and worth
keeping as vocabulary —

- **Position** — `1/4`, `1/2`, `3/4`, `Side`. Which quarter mark of the page's
  stored reference frame the vanishing-point pair straddles. This is §14.5's
  quartering, and the presets are named directly after it: the rule and the
  vocabulary are one system, not two.
- **Spread** — `Narrow`, `Wide`, `Ultrawide`. How far apart the two points sit
  along the horizon.
- **Elevation** — the bare name, or `Below`.

— but **no list enumerates them combinatorially.** 2-Point never says `3/4`,
3-Point never says `Side Narrow`, and `Ultrawide` appears once in the 2-Point
list and twice in the 3-Point one. Read the parts to know what a name *means*;
read the lists to know what *exists*.

**`2 Point` and `1/2 Narrow` are adjacent entries of the same list, and both
stay.** The last pass found them rendering identically. They are not an
accidental duplicate: `2 Point` *is* the centred default, and `1/2 Narrow` names
that same geometry explicitly. Concepts ships both, side by side, so Quill ships
both. Two names reaching one grid is correct here.

Whether names shared *between* lists agree numerically — whether 2-Point's
`1/4 Wide` and 3-Point's `1/4 Wide` place their common points identically, and
whether `1/2 Narrow` matches across the two lists that both carry it — is not
settled by the names, and is one of the things the §15.2 measurement has to
answer.

### 15.2 The geometry must be MEASURED, and these captures cannot supply it

Do not derive the numbers from the ten images. Two of them are at 100% zoom
and eight at 10%, and the pan differs between captures, so the
vanishing-point separations in them are not comparable to each other. Every
spread read off those images would be a guess wearing a decimal point.

The user chose a measurement pass instead. How it must be run:

1. **Work in a NEW Concepts drawing.** Do not touch `Drawing 5` or any
   other existing drawing. The user's own work is not a test fixture.
2. **Fix the view once**, then never pan or zoom again for the whole sweep.
   Record the zoom readout. Every preset must be captured under one
   identical viewport or the numbers cannot be compared — which is exactly
   what went wrong with the ten reference images.
3. **Enumerate the preset list verbatim first** — every name, in the order
   Concepts lists them. That list is a deliverable in its own right; it
   confirms or corrects whatever structure §15.1 claims, before any
   measuring starts. **Run — recorded in §15.5. It corrected §15.1, which
   now carries the real structure: 19 named presets across three per-type
   lists.** The sweep below is therefore three lists, not one, so the grid
   *type* changes during it.
4. **Capture each preset to its own PNG**, named for the preset.
5. **Measure from the PNGs programmatically, never by eye.** The horizon is
   the one full-width horizontal rule; find it by row-scanning for the
   darkest full-width row. The vanishing points are the small dots sitting
   on it; find them as local minima along that row.
6. **Report fractions, not pixels** — horizon `y` as a fraction of frame
   height, each vanishing point `x` as a fraction of frame width. §14.5
   established that these positions are relative to a stored reference
   frame and therefore differ per screen; a pixel figure measured on one
   monitor is worthless on another, a fraction is not.
7. **Then derive the three axis constants**: what fraction of frame width
   each of `Narrow` / `Wide` / `Ultrawide` spans, what offset each of
   `1/4` / `1/2` / `3/4` / `Side` applies, and what `Below` does to the
   horizon height. If the three axes turn out not to be separable — if, say,
   `Below` also changes the spread — say so plainly rather than forcing the
   grammar.

**The user's instruction on fidelity is unchanged and emphatic: every
variant must look exactly like the captures.**

### 15.3 Fullscreen chrome

**Windowed** (captures 1–3): the OS title bar carries `+`, the account
mark, `PRO`, a fullscreen glyph, then minimize / restore / close. The app's
own top bar carries `100%`, `0°`, then download, upload, gear, help.

**Fullscreen** (captures 4–10): the OS title bar is gone, and `PRO` and the
fullscreen glyph have **migrated down into the app's top bar**. Its
right-hand cluster then reads, left to right:

    [ ]  │  10%   0°   PRO   ↓   ↑   ⚙   ?

So the fullscreen bracket **leads** the cluster, and a thin vertical rule
separates it from the zoom readout. That divider does not exist windowed —
it appears only because the bracket moved in.

**On hover at the top edge** (final capture): a dark strip slides in over
the top-right, drawn **on top of** the app's own top bar and covering the
right end of that cluster. It carries three marks — minimize, **exit
fullscreen**, close.

Behaviour, as the user specified it: **reveal when the pointer reaches the
top edge of the screen, hide when the pointer leaves the strip.** Fullscreen
only; windowed already has the real title bar and needs no overlay.

Four things to get right:

- **The middle mark is exit-fullscreen, not restore-down.** Its glyph is two
  arrows pointing inward at each other diagonally, not the windowed
  double-square. Author it on the same 24-grid as every other mark — and per
  the standing rule, no emoji.
- **The app's own `[ ]` bracket stays visible the whole time.** Only the
  three OS controls hide and reveal. The bracket is the ordinary way back
  out of fullscreen; the strip is the shortcut.
- **The reveal region must not swallow ink.** It is a few DIPs of the screen
  top, and a stroke begun at the top of the canvas has to survive it. Test
  that explicitly — this is the same class of fault as §11's pen row, where
  a handler ate an input nobody noticed was gone.
- **Check the strip's colour against `PageTheme` before hardcoding.** It
  reads as opaque dark in the capture, but the capture is of a white page.
  Decide deliberately whether it tracks the theme or stays dark always, and
  say which was chosen and why.

### 15.3a Verified on screen, 2026-08-17

**The diagram and the four bullets in §15.3 are the transcription of the
captures and are left exactly as they were. This section records where that
transcription does not match the cluster that was built, what the reveal region
actually does to a stroke, and one conflict that has no answer yet.** Measured
on a 2880 × 1800 screen at 192 DPI, so 1 DIP = 2 px throughout: the 4 DIP
reveal band is the top 8 px and the 14 DIP keep band the top 28 px.

**a. The cluster diagram omits two marks and carries a third that does not
exist.** Read off `ChromeBars.BuildViewReadout` and the four `right.Children.Add`
calls after it, the right cluster is lock, zoom, tilt, AI, Import, Export,
Settings. In fullscreen, with the bracket and its divider led in, that is

    [ ]  │  🔓  100%  0°  ✦  ↓  ↑  ⚙

- The **lock** leads the readouts (`Icons.LockOpen` / `Icons.LockClosed` — the
  zoom lock, and it is real: while it is on a stray pinch snaps back). §15.3's
  diagram has no lock in it.
- The **AI mark** (`Icons.Ai`, V3 K.18, "immediately to the LEFT of Import")
  sits between the tilt readout and Import. §15.3's diagram has no AI mark
  either.
- The trailing **`?` help mark does not exist anywhere in the code.** There is
  no help button, no `Icons.Help`, and no `?` mark in `ChromeBars` or in
  `MainWindow.xaml`. Help was **specified in §5** — "Help (`?`, with a small
  `Accent` dot when unread)" — and was never built. §15.3 drew it because §5
  promised it, not because a capture showed one. The document stops implying it
  is there as of this line; if Help is wanted it is new work, not a regression.

Both the lock and the AI mark were verified present on screen in fullscreen, and
no `?` was found anywhere in the cluster.

**b. The reveal region does not swallow ink.** §15.3's third bullet asked for
this to be tested explicitly. It now has been, twice, and it holds.

- **A stroke pressed at the very top edge draws from row 0.** Pressed at y = 1
  DIP at mid-width, then dragged down: ink runs continuously from **screen row 0**
  to row 364, 10 px wide — 40 changed pixels inside rows 0–3 and 80 inside rows
  0–7, i.e. the reveal band is fully inked. The strip revealed at the top right
  at the same time, as it should; it is right-aligned and was nowhere near the
  nib.
- **A stroke dragged UP into the edge keeps the strip away.** Pressed at y = 430
  px in the strip's own column (x = 2700, inside the strip's 2604–2880) and
  dragged to row 0: ink reached row 0, and at the moment the nib sat there the
  strip was **absent** — the `↓ ↑ ⚙` marks it would have covered were still
  visible. The `!Pointer.IsInContact` gate in `OnRootPointerMoved` is doing the
  job the comment claims for it.

**Neither of those results means anything without the mid-canvas control that
was run first.** Eleven earlier attempts at this proof returned nothing, and
both reasons were properties of the harness rather than of the reveal region.
They are written up on their own in **§15.3b**, because they will catch the next
person who automates this app for any reason at all.

**c. The text format bar and the strip both wanted the top edge. RESOLVED:
in fullscreen the bar sits BELOW the strip's band.**

In fullscreen the caption row folds (§15.4 item 4), so `FormatBar` — `Grid.Row` 1
— rises to the screen's top edge whenever the text tool is selected or a text box
is active. That put it under the strip. What was observed before the change:

- With the text tool selected the format bar occupied the **full width of the top
  88 px (44 DIP)** and pushed the `ChromeBars` cluster row down to y ≈ 146 px. The
  strip's 34 DIP band sat entirely inside the format bar's own row.
- Pushing the pointer to the top edge **still revealed the strip**, and the strip
  drew **on top of** the bar, covering its two right-most buttons (dictation and
  the `Ω` special-character button). The passive root listener sees the pointer
  whatever child it is over, so the bar never blocked the reveal.
- While the strip was up those two buttons **could not be reached at all**.
  Walking down from the edge onto the dictation button left the pointer inside
  `OverStrip`, so the strip stayed up (ground sampled `#202020`) and the mark
  under the pointer lit instead (`#8D8D8D`). Approaching the same button from
  *below*, without entering the top 4 DIP, left the strip down and the button
  reachable (`#F5F5F1`). Reachability depended on which direction the pointer
  arrived from.
- Leaving the edge retracted the strip and the bar came back intact.

**The user's ruling: while the app is fullscreen the format bar is offset down by
the strip's band, so the two never overlap. Windowed behaviour is unchanged.**

**The offset is FIXED for as long as the app is fullscreen — it is not applied
only while the strip is revealed, and that is the whole point.** The rejected
variant was to shift the bar just for the moment the strip is out, which costs no
canvas at all. It loses because it would move a row of buttons *under the pointer
as the user reaches for them*, which reads as broken however correct the geometry
is. A hover-dependent layout trades a visible glitch for 34 DIP of canvas in text
mode only, and the user took the canvas loss instead. **Do not reintroduce the
hover-dependent version as an optimisation** — the wasted band is the price that
was knowingly paid, not an oversight.

Also considered and rejected: suppressing the strip in text mode (the window
controls are the one thing that must not become unreachable), shrinking the
strip's hit region (it would break §15.4's rule that hit-testing tracks the
visual), and simply accepting the overlap.

The offset is derived from `FullscreenChrome.Metrics.StripHeight` rather than
written as a literal, so retuning the strip's height moves the bar with it; a
second copy of 34 is how two numbers that must agree stop agreeing. A small gap
is left rather than having the two abut exactly, so a 1 DIP rounding difference
cannot make them touch.

**What was built.** `MainWindow.ApplyFullscreenChrome` sets `FormatBar.Margin`'s
top to `FullscreenChrome.Metrics.StripHeight + FormatBarStripGap` (4 DIP) and back
to zero otherwise. It is gated on the **fold** — `fs && _chromeBars.IsVisible` —
rather than on fullscreen alone, because the fold is precisely what lifts the bar
to the screen's top edge: with the radial surface off, the caption row stays put
and the bar already sits below the strip's band, so offsetting there would spend
canvas for nothing. It is still a fixed geometry, not a hover-dependent one —
both `fs` and the surface choice are states the user changes deliberately, not
things that move while a pointer approaches a button. It also costs nothing while
the bar is collapsed, because a collapsed child adds no height to an `Auto` row,
which is what confines the loss to text mode. Builds at 0 warnings.

**Not verified on screen — the run was stood down under the shared-machine rule
before it could be.** Three things to check, and one number to check them against:

1. Fullscreen, text tool, strip revealed: it must cover **no** format-bar button.
2. Every format-bar button clickable **both** approaching from below **and**
   walking down from the top edge. The second is the case that failed before,
   because walking down kept the pointer inside `OverStrip`.
3. Leaving fullscreen must put the bar back. Do not read geometry straight after
   `SetPresenter` — it does not lay out synchronously; `ApplyFullscreenChrome` is
   self-correcting and runs again on `SizeChanged`.

**The tight number is item 2, and it WAS tight by 2 DIP.** `OverStrip` reaches
`StripHeight + StripSlack` = **40 DIP** down, while the first row of format-bar
buttons started at `StripHeight (34) + FormatBarStripGap (4) + the bar's own
4 DIP top padding` = **42 DIP**. So the buttons cleared the strip's hit rectangle
by 2 DIP, and the bar's top 2 DIP of *background* fell inside it — background
only, no control.

**`FormatBarStripGap` has since been raised from 4 to 12 — 2 DIP is not
clearance.** The user's ruling: two DIP sits inside layout-rounding noise, and
this project has already been bitten by exactly that magnitude — §14.3's corner
target snapped from 5.747 to 5.5 and broke a hitbox. The floor asked for is **8
DIP of clearance, measured**. At 12 the first control starts at `34 + 12 + 4` =
**50 DIP** and clears the 40 DIP hit rectangle by **10**, and the bar's background
no longer enters the rectangle at all.

Ten and not the eight asked for, deliberately: a nominal 8 that rounds to 7.5 has
not met an 8 DIP floor, and the point of raising the constant is to stop the
answer depending on rounding at all. Two DIP of canvas, in text mode only.

**The reason it was 4 is the reason it must not be picked in isolation again.**
The margin already reads `StripHeight` from `FullscreenChrome.Metrics` so that
retuning the strip moves the bar — but `StripSlack` was never in the arithmetic,
and the clearance is a function of `StripHeight + StripSlack`, not of
`StripHeight`. The invariant, recorded on the constant itself:
`(StripHeight + FormatBarStripGap + the bar's padding) − (StripHeight +
StripSlack) ≥ 8`. If either metric is retuned, **confirm it by measuring where the
first button row lands — not by re-doing that arithmetic**, which is what produced
a 2 DIP answer that read as fine on paper.

### 15.3b Driving Quill from injected input — two traps that fake a null result

**Not about fullscreen. This is here because it cost eleven attempts at §15.3's
ink test, and it will cost the same again on any automated test of any part of
this app.** Both traps produce a screenshot with no ink in it, which is exactly
what a genuinely broken feature produces.

**1. A MOUSE CANNOT DRAW IN QUILL unless "Touch draw" is on.** With the pen tool
selected, `InkSurface.OnPointerPressed` sends

    tool == ToolType.Pen && !isPen && !HandDrawMode  →  HandleMousePress(...)

and `HandleMousePress` under the default `MouseMode.Auto` starts a **rubber-band
rectangle**, which commits nothing to the page and leaves no trace once the
button comes up. So an injected mouse drag draws nothing **anywhere on the
canvas** — not at the top edge, not in the middle — and the blank capture is
indistinguishable from a stroke that some handler ate. `HandDrawMode` comes from
the `TouchDrawToggle`, which `ChromeBars` removes from the top bar (K.14) and
rehouses at **Settings ▸ Interaction ▸ Touch Input ▸ Touch draw**. Toggling it
also writes `Library.FingerAction`, but **nothing reads that back at startup**, so
an isolated instance has to be walked through the panel on every single run.

**2. Selecting the TEXT tool raises the format bar over the top of the screen.**
In fullscreen it takes the top 44 DIP full width (see §15.3 item c). A test of
anything in that band with the text tool selected is testing the format bar.
A related decoy: with the text tool the press does not create a text box, it
calls `SetPendingText`, which leaves a **blinking caret** — a thin dark vertical
mark that appears and disappears between captures and reads like intermittent
ink. It blinks on an even cadence; ink does not.

**The rule that separates a broken harness from a broken feature: always run the
same gesture, with the same tool and the same injection, through the MIDDLE of
the canvas first.** If the control does not ink, the harness is wrong and no
conclusion about the feature is available yet. Only once the control has inked
does a null anywhere else mean anything. §15.3's ink test was reported as
"passes" on exactly that basis: the control laid down `#D97757` along the
injected path, 3243 changed pixels, before either edge case was attempted.

### 15.4 Fullscreen chrome — amended after the first build, 2026-08-16

**Amends §15.3. Everything §15.3 says that is not contradicted here still
stands, including the captures it transcribes** — the diagram above is a
record of what was photographed and is deliberately left as it was.

**1. `PRO` comes out of the cluster.** The user, having seen it built:
*"remove pro button for now, keep it in code for possible reuse much
later."* The fullscreen right-hand cluster is therefore

    [ ]  │  10%   0°   ↓   ↑   ⚙   ?

The bracket still leads and the **divider still separates it from the zoom
readout** — the rule exists because the bracket moved in, so `PRO` leaving
does not touch it.

*Parked, not deleted.* The badge's construction path stays compiled, called
and reachable behind one constant (`ChromeBars.Metrics.ProBadgeVisible`,
false). Commenting it out would have let it rot silently through the next
refactor of the method it lived in; code that still compiles cannot. The
constant is read alongside a field rather than on its own so the compiler
never sees a constant-false `if` and the build stays at zero warnings.

**2. The strip slides down, and retracts up.** Chosen over sliding in from
the right because it is the same gesture that revealed it: the pointer
pushes at the top edge and the strip comes down to meet it.

Four things matter more than the curve:

- **Reversible mid-flight.** The pointer routinely leaves before the strip
  has arrived. That must turn round from wherever it is, not snap open and
  then start closing. **One easing curve serves both directions**, because
  two would make the eased position discontinuous at the moment of reversal
  — a visible jump.
- **Hit-testing tracks the visual.** Whatever has visibly arrived is exactly
  what is clickable. A strip on its way *out* is made inert outright, so a
  click can never land on something that is leaving.
- **No flutter at the boundary.** Reveal arms at **4 DIP** from the top;
  once up, hugging the top edge keeps it up out to **14 DIP**, and the
  strip's own rectangle carries 6 DIP of slack. The dead band is the
  mechanism rather than a dwell timer, because a dwell would put latency
  into a gesture whose whole point is that it is immediate.
- **Quill's own timings, not invented ones.** 190 ms out / 130 ms back and
  the cubic-bezier `(0.12, 0.9) → (0.2, 1.0)` open curve, which are the
  app's menu open/close motion (`Helpers/MenuAnim.cs`, commit `9d0d6cf`).

**3. The strip's colour: decided as themed, then REVERSED by the user. It is
a fixed dark.**

It was first built theme-derived, taking `Panel` on the argument that §0
makes every surface derive from the page ground and that new chrome may not
invent a second theme source. That argument is recorded here rather than
deleted, because anyone reading only "the strip is dark" will re-derive it
from §0 and assume it was never considered.

What settles it is a point the themed version had **already conceded** for
close-on-hover red: these are the **OS window controls**, borrowed. Windows'
own caption buttons do not track the colour of your document. Once red is
exempt on that basis, the exemption belongs to the whole strip and not only
to its most destructive button — and matching the capture and matching the
platform convention then agree, which is what makes the disagreement with
§0 worth taking.

So: ground `#202020`, the value Windows 11 gives its own dark caption bar;
marks `#F2F2F2`, the light ink Quill's dark themes already use. Neither is
invented. Measured **14.6:1**, against the 12.1:1 floor §13.1 guarantees for
themed chrome — dropping derivation raised the floor rather than lowering
it, because a fixed pair cannot land on the worst case the way a derived one
can. Close-hover swaps the mark to white: white on `#C42B1C` is **5.7:1**.

**The border stays themed, and that is deliberate.** The fill reads as OS
chrome, which is page-independent. The border separates the strip from the
page behind it, which is page-*relative*: on a light page the dark strip
already separates itself, while on Darkprint the strip and the page sit
within a few levels of each other and that rule is the only thing dividing
them. A fixed border would vanish on exactly the ground that needs it most.

One correction carried in with this: an earlier comment claimed `#C42B1C`
was "the same value the windowed caption button already uses". It is not.
`MainWindow`'s `BtnWinClose` is plain `Transparent` with **no red hover at
all**. The windowed button arguably wants the same treatment, but that is a
separate change and was not made here.

**4. What the caption row does in fullscreen.** Windowed, Quill's `TopBar`
*is* the caption bar — the system one is removed and that row's own three
buttons are the minimise / maximise / close the user gets. §15.3's "the OS
title bar is gone" therefore means **that row folds away**, which is what
lets the app's own top bar (under the dial surface that is `ChromeBars`, per
§5, not that row) sit at the screen's top edge where §15.3 draws it, and
what makes the hover strip the thing carrying the window controls. It folds
**only while `ChromeBars` is up**: with the radial surface off there are no
floating clusters, that row is the only chrome there is, and hiding it would
leave a bare canvas.

### 15.4a The slide, measured on screen — 2026-08-17

Frame bursts of the strip's own rectangle (276 × 96 px at the top right),
grabbed in one process at about 8 ms a frame with the foreground window checked
on every frame, so nothing else could be in front. 1 DIP = 2 px on this screen;
the strip's full extension is 34 DIP = 68 px, and the visible height below is the
lowest row still filled with the strip's `#202020` ground.

**The descent decelerates and settles.** Already recorded and re-seen here:
travel per frame 10, 13, 13, 8, 6, 4, 3, 2, 1 px, monotone and easing out.

**§15.4's first bullet — "reversible mid-flight" — holds, and the strip demonstrably
never arrives before turning round.** Two runs, pulling the pointer off the edge
at different points in the flight:

| pointer pulled away | height when pulled | peak height reached | then |
| --- | --- | --- | --- |
| 76 ms after the reveal armed | 59 px (29.5 DIP) | **60 px (30.0 DIP)** | 58, 54, 48, 37, 17, 0 |
| 40 ms after the reveal armed | 43 px (21.5 DIP) | **49 px (24.5 DIP)** | 39, 21, 0 |

The peak is the whole point. In neither run does the strip ever reach 68 px — it
tops out at 30.0 DIP and 24.5 DIP of 34 — so it cannot have snapped open and then
closed. The sequence through the turn is continuous in both: one frame carries on
outward after the pull (the move lands between frames), then every following
frame is lower than the last, with no jump at the reversal. That is the single
easing curve doing what §15.4 says two curves could not.

The retraction is also the right *duration* for a shared curve rather than for a
fixed 130 ms slide. Visible 60 px is `Ease(_t) = 0.88`, and this ease is front-
loaded (cubic-bezier 0.12, 0.9 → 0.2, 1.0), so 0.88 of the distance is only about
a third of `_t`. A third of 130 ms is ≈ 43 ms, and the measured retraction from
60 px to 0 took ≈ 50 ms. A retraction that had taken the full 130 ms from there
would have meant `_t` was being reset rather than reversed.

### 15.4b Proof 7 — where the Settings panel actually lands, 2026-08-17

Measured on the build at `4a7ab47`, in an instance started fresh so `_placed`
and `_userPlaced` began unset and the failing path was the real one. Two hosts:

- **windowed** — restored, not maximised, outer window rect 98, 98 → 2258, 1411 px
  = **1080 × 656 DIP**, which is close to the 1072 DIP host `4a7ab47`'s message
  describes;
- **fullscreen** — 0, 0 → 2880, 1800 px = **1440 × 900 DIP**.

Panel rectangles were taken by differencing panel-open against panel-closed
*inside the window's client area only* and keeping the span where the changed-
pixel density is at least half its maximum, which excludes the drop shadow. All
four states measure the panel at **516 × 444 DIP**, so nothing below changes its
size.

| state | right gap | top | verdict |
| --- | --- | --- | --- |
| opened WINDOWED, first time | 20.5 DIP | 93.0 DIP | the windowed anchor |
| closed, F11, **REOPENED FULLSCREEN** | **14.0 DIP** | **60.0 DIP** | re-anchored |
| F11 out, panel left open | 20.5 DIP | 93.0 DIP | identical to the first open |
| F11 back in, panel left open | 387.0 DIP | 60.0 DIP | *not* right-anchored |

Gaps in the windowed rows are measured from the **outer** window rect, which
includes the invisible resize border and the caption row; the 20.5 / 93.0 pair is
the same host-relative position as 14 / 60, which is why rows 1 and 3 agree
exactly.

**1. The `_placed`-survives-`Hide()` case is fixed.** Opened windowed, closed,
fullscreen entered, reopened: the panel comes back at **exactly `EdgeGap` = 14.0
DIP from the right edge and `TopBand` = 60.0 DIP from the top** of the 1440 DIP
host. Not a caption row low, not at the windowed x. Those two numbers falling on
the constants to a tenth is the proof — `Show()` re-anchored, which is what
`4a7ab47`'s `!_placed || !_userPlaced` was written to make it do. (What the old
code would have done instead is `57d1aad`'s and `4a7ab47`'s reasoning, not
something measured here; the pre-fix build was not run.)

**2. Across a fullscreen toggle with the panel OPEN, size and top band survive
— and the horizontal behaviour is asymmetric.** Fullscreen → windowed puts it
back at the windowed anchor, because `57d1aad`'s shift-by-host-delta lands it
outside a host 360 DIP narrower and `Constrain()` then clamps it to the right
edge. Windowed → fullscreen instead keeps its absolute left: 543.5 DIP becomes
537.0, a shift of 6.5 DIP, which is exactly the host-origin delta. So it stays
where it was and the now-1440 DIP host leaves it with a **387 DIP right gap** —
sitting in the middle of the screen rather than in the corner it opens in.

That is not a regression in either commit, and it does not contradict
`57d1aad`, which deliberately preserves position rather than snapping to the
corner. But it is the **same visual complaint** `4a7ab47` fixed for the reopen
path, arrived at through the still-open path: `HostGeometryChanged` shifts
without consulting `_userPlaced`, while `Show()` now does. A panel the user never
touched ends up in two different places depending only on whether it happened to
be open while the host grew. **Ruled a bug and fixed — see §15.4c. The table
above is left as the measurement of the build it was taken on.**

**3. NOT VERIFIED: whether a panel the user DRAGGED keeps its spot.** This is the
other half of `4a7ab47` — `_userPlaced` set on drag and on resize, so that
`Show()` re-anchors auto-placed panels only. The run was stood down (the machine
is shared, and input arrived that the run did not generate) with the drag
injection queued and not yet delivered, so **the `_userPlaced` distinction is
untested on screen.** It is the case that matters most for the fix being right
rather than merely harmless, and it is still open. To close it: drag the panel by
its grab bar to a position valid in both hosts, close it, toggle fullscreen,
reopen, and confirm it comes back at the dragged host-relative position instead
of at 14 / 60.

### 15.4c The two placement paths now answer one question — 2026-08-17

**Closes §15.4b item 2. The user's ruling: the asymmetry is a bug, not a design
question, and the rule must be uniform in both paths — user-placed → preserve the
position and clamp; auto-placed → re-anchor to the corner `OpenOn` promises.** A
panel merely open across the change deserves the same answer as one being
reopened, which is what `4a7ab47` established for the reopen path.

**What was wrong.** Two methods decided the same thing and disagreed.
`FloatingWindow.Show()` asked `!_placed || !_userPlaced` and re-anchored when the
answer was yes. `HostGeometryChanged` asked only `!_placed`, and for everything
past that shifted the popup's absolute offsets by the host-origin delta, so an
auto-placed panel that happened to be open across the toggle kept a position it
was never given deliberately.

Fullscreen → windowed *looked* right, but only by accident: the shift landed the
panel outside a host 360 DIP narrower and `Constrain()` clamped it back to the
right edge. That is a clamp, not an anchor, and it is why the two directions
disagreed — windowed → fullscreen has nothing to clamp against, so 543.5 DIP
became 537.0 (the host-origin delta exactly) and the panel sat mid-screen with a
387 DIP right gap. **The clamp had been standing in for the rule, and it only
works in the direction where the host shrinks.**

**What was built.** One predicate, `FloatingWindow.KeepsOwnPosition`
(`_placed && _userPlaced`), read by both paths.

- `Show()` — `if (!KeepsOwnPosition) PlaceAnchored();` Same behaviour as before;
  `!_placed || !_userPlaced` was already that expression, and naming it is what
  makes the second call site obviously the same test rather than a similar one.
- `HostGeometryChanged` — the `!_placed` early return stays (a window never
  placed has no position to preserve and no corner to return to; `Show()` will
  place it against the current origin when it opens). Then
  `if (!KeepsOwnPosition) { PlaceAnchored(); return; }` before the shift, so the
  auto arm re-anchors and the user arm still shifts-and-clamps exactly as
  `57d1aad` intended.

`PlaceAnchored` recomputes from the current origin and size and ends in
`Constrain()`, so the re-anchoring arm is not skipping the clamp; it also
refreshes `_lastOrg`, so a later drag shifts from the right baseline. The
predicate keeps `_placed` in it only for the never-placed case: a resize sets
`_userPlaced` without touching `_placed`, but a window being resized is open and
therefore placed by construction.

A consequence worth naming, because it is wider than fullscreen: an auto-placed
panel now re-anchors on **every** host size change, including an ordinary drag of
the window border. That is the rule, not a side effect — a panel positioned only
by `OpenOn` belongs in `OpenOn`'s corner, and widening the window used to leave it
stranded inland with nothing to clamp it back.

Builds at 0 warnings. **Not yet verified on screen: the dragged-panel case is
still §15.4b item 3's, and it is now the thing that decides whether the whole
`_userPlaced` distinction is right rather than merely harmless — if a drag does
not survive a close / toggle / reopen, both call sites are wrong together.** What
the one run on this build did reach is §15.4d.

### 15.4d The run on the fixed build — one row measured, then stood down again

**Third stand-down under the shared-machine rule. Read this before automating any
of it a fourth time: the launch state below is not what the settings file says it
is, and knowing that is most of the setup cost.**

The build carrying both §15.4c and §15.3a's raised gap was launched with
`QUILL_DATA_FOLDER` pointed at the isolated scratch folder. Three facts about how
it comes up, none of them assumable:

- **It came up WINDOWED, not fullscreen, despite `Ui.StartFullscreen = true` in
  that folder's `settings.json`.** Outer rect 196, 196 → 2356, 1509 px = **1080 ×
  656.5 DIP** at dpi 192 (scale 2), which is the same host *size* §15.4b measured
  and a different origin. So every fullscreen step has to be entered explicitly,
  and a harness that assumes the first capture is fullscreen is measuring the
  windowed host.
- **It came up on the gallery** ("Welcome back"), so a page has to be opened
  before any chrome exists to measure. The `Continue` button was used, which lands
  in the scratch library's own notebook and never touches the user's.
- **The Settings panel came up ALREADY OPEN** with the page — so the very first
  capture is a first `Show()` on a fresh process, `_placed` and `_userPlaced` both
  starting false. That is the auto-placed path, which is convenient, but it also
  means a run that wants a *closed* starting state has to close it first.

The radial surface was up (the dial visible at the top left), so
`_chromeBars.IsVisible` was true and the caption row would have folded on
fullscreen — the precondition §15.3a item c's offset is gated on.

**What was measured — the windowed first-open anchor, on the fixed build.** Taken
from the capture offline, so no differencing pair was needed: the panel's fill is
`#F7F7F7` against a `#FCFCFC` page, which separates them directly. Keeping the
columns and rows whose fill-run is at least half the widest, then including the
1–3 px warm border and shadow:

| | measured here | §15.4b row 1 |
| --- | --- | --- |
| top | **93.0 DIP** | 93.0 DIP |
| right gap | 21.0 DIP | 20.5 DIP |
| left | **543.5 DIP** | 543.5 DIP |
| size | 515.5 × 542.5 DIP | 516 × 444 DIP |

Top and left land on §15.4b's numbers exactly, and the right gap differs only by
the half DIP that the border convention moves. So **`PlaceAnchored` still owns the
windowed first open and §15.4c did not disturb it** — the right null result, and
the only row of §15.4b's table this run got to.

The **height differs — 542.5 rather than 444** — and that is the panel's own
persisted size in that data folder, not a placement effect: nothing in §15.4c
touches size, and `MaxSize` is not binding at that height on a 656.5 DIP host.
Worth knowing only because a harness that hard-codes 444 to find the panel will
miss it.

**Then the guard tripped.** The cursor was left at 1440, 363 by the last injected
click, was still there when that step's guard checked, and had moved to
**1344, 1002** by the start of the next step — with no injection in flight
between the two. Afterwards `GetLastInputInfo` counted steadily up from 34 s with
no further input and the foreground was still the Quill this run had activated, so
it reads as a single real pointer move from the person at the machine rather than
a stream of them. Under the rule that is a stop either way. The Quill was closed
with `WM_CLOSE` (posted, not clicked — closing by injection would have been more
injection), and the user's own `library.json` was confirmed byte-identical by
SHA-256 before and after, as was the still-running Concepts instance.

**Consequently unverified, and in this order of value:**

1. **§15.4c itself** — the 387 DIP asymmetry. The toggle was never entered, so the
   fix is compiled and reasoned and *not* seen. The cheapest possible check, and
   it needs only what this run already had on screen: with the panel open at the
   windowed anchor above, enter fullscreen and measure. Auto-placed, so the answer
   must be `EdgeGap` 14.0 / `TopBand` 60.0 of the 1440 DIP host, where the old
   build gave 387.0 / 60.0.
2. **§15.4b item 3** — the dragged panel. Still the case that decides whether
   `_userPlaced` earns its keep. The drag handle is the pill in the panel header,
   centred at the panel's own top; in this capture it sat at 1800, 404 px.
3. **§15.3a item c** — the format bar, all three checks, now against
   `FormatBarStripGap` = 12 rather than 4.

### 15.4e A panel's position is an INSET from the side it is anchored to — 2026-08-17

**This replaces every absolute offset in `FloatingWindow`, and it retires §15.4b's
one-way clamp along with §15.4c's two arms. Read this before touching the
placement code: reintroducing an absolute offset reintroduces both bugs.**

The user's own wording, which is the specification:

> make it so that when panel gets resized the distance from the side they're on
> gets remembered, and the panels move accordingly. when the panels encounter
> another panel element (for example when the window gets too small) they try to
> fill the screen but still remember the original distance.

**The model, as four rules.**

1. **The stored geometry is an INSET plus a wanted size, and it is the only source
   of truth.** `_insetSide` is the distance from the panel's anchored edge to the
   host edge on its `OpenOn` side; `_insetTop` is the distance from the host's top;
   `_wantW` / `_wantH` are the size it wants. The popup's offsets and the panel's
   `Width` / `Height` are *derived* from those four on every host change and are
   never read back as state. A panel 14 DIP from the right edge stays 14 DIP from
   the right edge whatever the host does; one dragged to 387 stays at 387.
2. **Only a user gesture writes the stored geometry.** A drag writes the insets; a
   resize writes the insets and the wanted size. Nothing else does — not a
   fullscreen toggle, not a window-border drag, not a clamp.
3. **A resize preserves the anchored-side distance.** The grabbed edge moves and
   the opposite edge holds still, so on a right-anchored panel the far (left) edge
   grows inward and the right gap is untouched. This is not arranged for; it falls
   out of the inset being the thing stored, because a resize that leaves the
   anchored edge alone cannot change a distance measured to that edge.
4. **Clamping is NON-DESTRUCTIVE.** §11.6 item 42's limits still hold on every
   frame — the whole panel on the page, inside its margins, below the top-bar band
   — but they are applied to a *candidate* rect on its way to the screen and
   returned, never written back. A panel with no room fills what there is and
   returns to its stored geometry exactly when the room comes back.

Rule 4 is the one §15.4b recorded as an accepted limitation: *"clamping into
smaller windowed bounds is one-way, so a panel sized to full fullscreen height
stays at the clamped size on return."* It is no longer true. `Resolve` is pure and
`Constrain`'s successors return their answers, so the clamp has nothing to
overwrite.

**"Another panel element" is, today, the top band.** `TopBand` is the top bar's
two clusters, and it is the one such element a floating window can currently meet;
the other three sides are `EdgeGap`. The room is computed in `MaxSize` and
`ConstrainPosition` and nowhere else, so a future reserved region — a dock, a
second floating panel via `PanelLayout` — goes in those two methods and inherits
rule 4 for free. That is deliberately *not* built here: nothing in this change
plumbs another panel's rect into `FloatingWindow`, and the parenthetical case the
user named ("when the window gets too small") is the one that is.

**What was removed, and why it was safe.**

| gone | why |
| --- | --- |
| `_lastOrg` | the baseline a shift-by-delta needed. `HostOrigin` is now read fresh on every apply as the *current* translation, never differenced, so there is no delta to take. |
| `_userPlaced` | it recorded "the user chose this position" as a MODE. The inset records it as a NUMBER, which is strictly more information. No readers left. |
| `KeepsOwnPosition` | §15.4c's shared predicate, and with `_userPlaced` gone there is nothing to predicate on. |
| `PlaceAnchored` | the re-anchor arm. An auto-placed panel's inset *is* `EdgeGap`, so preserving the inset re-anchors it — the arm and the rule are the same computation. |
| `FirstPlacement` | a one-shot `SizeChanged` handler for the not-yet-measured host. The permanent subscription fires on the same event; `Resolve` simply returns false until then. |
| `Constrain` / `ClampIntoView` (as mutators) | split into pure `ConstrainSize` and `ConstrainPosition`. **The safety role is intact** — it runs on every apply — but it can no longer be mistaken for the positioning rule. |
| one of two `_host.SizeChanged` handlers | `ClampIntoView` was subscribed alongside `HostGeometryChanged` and clamped the offsets the latter had just shifted. Resolving clamps on the way through. |

**The `KeepsOwnPosition` question, answered.** §15.4c's two arms *are* subsumed,
and this is the reasoning to keep: an auto-placed panel's inset is `EdgeGap`, so
preserving it re-anchors; a dragged panel's inset is whatever it was dragged to,
so preserving it holds position. One rule, both correct behaviours. `_userPlaced`
was deleted only after confirming it had no other reader anywhere in the tree — it
was private to `FloatingWindow.cs` and read solely through `KeepsOwnPosition`.

One behavioural improvement falls out. Under §15.4c a *resize* set `_userPlaced`,
so a panel resized in its default corner stopped re-anchoring and began shifting
by the host delta. Under the inset model a far-edge resize leaves the anchored
inset at `EdgeGap`, so it keeps landing in the corner — which is what a panel that
was only ever resized should do.

**The arithmetic, worked on paper against §15.4b's measured hosts.** `EdgeGap`
14.0, `TopBand` = `RowTop` + `IconPitch` + 8 = 10 + 42 + 8 = **60.0**, Settings
requesting 516 × 724. Windowed 1080 × 656 DIP, fullscreen 1440 × 900 DIP.

| case | host | left | right gap | top | size | stored |
| --- | --- | --- | --- | --- | --- | --- |
| **A** auto-placed, open | 1080 × 656 | 550.0 | **14.0** | 60.0 | 516 × 582 | auto / auto, want 516 × 724 |
| A, host grows | 1440 × 900 | 910.0 | **14.0** | 60.0 | 516 × 724 | unchanged |
| A, back | 1080 × 656 | 550.0 | **14.0** | 60.0 | 516 × 582 | unchanged |
| **B** dragged to a 387 gap | 1080 × 656 | 177.0 | **387.0** | 60.0 | 516 × 582 | 387.0 / auto |
| B, host grows | 1440 × 900 | 537.0 | **387.0** | 60.0 | 516 × 724 | unchanged |
| B, back | 1080 × 656 | 177.0 | **387.0** | 60.0 | 516 × 582 | unchanged |

Case A is the whole of §15.4c's complaint, and note that **no clamp is involved in
either direction** — 910 and 550 are both derived, both interior, and the two
directions are symmetric by construction rather than by one of them happening to
overflow. The old model reached case B's 387 *by accident*: §15.4b measured a
windowed left of 543.5, the shift-by-delta moved it to 537.0 (the host-origin
delta of −6.5 exactly), and 1440 − 537.0 − 516 = **387.0** on a host wide enough
to leave it stranded mid-screen. Now 387 is reached only when the user actually
drags there, and then it is held in both hosts.

**The round trip, which is rule 4's proof.** Fullscreen 1440 × 900, the bottom-left
grip dragged out 400 in each axis:

| step | host | rendered | stored |
| --- | --- | --- | --- |
| resized to fill | 1440 × 900 | 916 × 826 at left 510.0, gap 14.0 | 14.0 / auto, want **916 × 826** |
| host shrunk | 720 × 420 | 692 × 346 at left 14.0, gap 14.0 | **unchanged** |
| host tiny | 300 × 300 | 320 × 260 at left 14.0 | **unchanged** |
| **back** | 1440 × 900 | **916 × 826 at left 510.0, gap 14.0** | **unchanged** |

Identical to the pre-shrink rect in all four numbers, and the stored tuple
`(14.0, auto, 916.0, 826.0)` is byte-identical before, during and after. The 300 ×
300 row is the documented `MinW` / `MinH` floor: the room is narrower than the
window's 320 DIP minimum, so the minimum wins and the panel hangs 34 DIP past the
right margin rather than shrinking to nothing. That floor is `MaxSize`'s
pre-existing behaviour, unchanged.

**Two destruction paths that had to be closed by hand,** because rule 4 is not
automatic once a gesture is involved:

- A plain **move** must not bank the rendered size. The first draft routed both
  gestures through one "store this rect" method, and a horizontal drag in a
  windowed host therefore committed the *clamped* height as the wanted height —
  destroying a fullscreen-chosen height through the drag handler rather than
  through the clamp. Verified: sized to 826 fullscreen, clamped to 582 windowed,
  dragged sideways there, back to fullscreen → still 826.
- A gesture commits **only the axis that moved**. A purely horizontal drag in a
  host with no vertical slack would otherwise bank the clamped top as a deliberate
  choice and lose the one made when there was room for it.

A drag *does* bank the clamped position on the axis it moved, which is not a
violation of rule 4 — it is rule 2. Storing the raw pointer target instead would
give the drag a dead zone (shove 200 DIP past the edge and the first 200 DIP back
moves nothing) and bank a number no host can honour. The old in-place `Constrain`
had the same effect, so the feel is unchanged.

**Builds at 0 warnings** (`--no-incremental`; an incremental build here skips the
C# compile and reports a 0 it did not earn).

**NOT VERIFIED ON SCREEN.** The whole of the above is arithmetic and reasoning; the
change was written, built and committed without the app being launched, because
another run was in progress on this machine. Still open, in order of value:

1. **The round trip.** Open a panel fullscreen, drag the bottom-left grip until it
   fills the page, leave fullscreen, confirm it clamps, re-enter fullscreen and
   confirm it returns to the *same* size and gap. This is the case §15.4b logged as
   a limitation and the one this change exists for.
2. **§15.4d item 1, the 387 asymmetry.** Auto-placed panel open across a toggle,
   both directions. Must be `EdgeGap` 14.0 / `TopBand` 60.0 of whichever host is
   up, and — the part the old build could not give — the *same* in both directions.
3. **§15.4b item 3, the dragged panel.** Drag by the header pill, close, toggle,
   reopen. Must return to the dragged distance, not to the corner. This no longer
   tests a flag; it tests whether the inset is being read on the reopen path.
4. **A resize's anchored edge.** Drag the bottom-left grip on the right-anchored
   Settings panel and confirm the right gap does not move (rule 3).

### 15.5 The preset sweep — the list, enumerated from Concepts, 2026-08-17

Run per §15.2. **Setup, in the order that section requires it:** Concepts was
already fullscreen (window rect 0, 0 → 2880, 1800, and its cluster reads
`[ ] │ 82% 0° PRO ↓ ↑ ⚙ ?` — the bracket leading with the divider, which is what
§15.3 transcribed), and it was fullscreen **before** the scratch drawing was made.
A **new blank drawing** was then created from the gallery — it came up as
**Drawing 7** — at **100% zoom, 0° tilt**, and the viewport has not been panned or
zoomed since. `Drawing 5` was opened only far enough to see its gallery thumbnail
(which is labelled `1/4 Ultrawide`, confirming it is the drawing the reference
captures came from) and was **not** opened or altered. Nothing was deleted.

**Step 3 of §15.2 — the verbatim list — does not confirm the 24 + 3 grammar. It
corrects it, and the correction is structural: THE PRESETS ARE PER GRID TYPE.**

The presets do not live in one catalogue. Precision ▸ Grid offers nine grid
**types** — `No Grid`, `Dot Grid`, `Graph Paper`, `Lined Paper`, `Isometric Grid`,
`Triangle`, `1-Point`, `2-Point`, `3-Point` — and each of the three perspective
types opens its own editor (`Edit Grid`) with its **own** `Preset` strip. Read off
those strips, left to right, exactly as Concepts lists them:

**1-Point** — two entries:

    1 Point   │   Custom

**2-Point** — nine presets, then `Custom`:

    2 Point   │   1/2 Narrow   │   1/4 Narrow   │   Side Narrow   │   1/2 Wide
    1/4 Wide  │   Side Wide    │   1/2 Wide Below   │   Side Ultrawide   │   Custom

**3-Point** — nine presets, then `Custom`:

    3 Point        │   3/4 Narrow          │   1/2 Narrow   │   3/4 Wide
    1/4 Wide       │   Side Wide Below     │   1/4 Wide Below
    3/4 Ultrawide Below              │   3/4 Ultrawide       │   Custom

**What this settles about the ten captures.** §15.1 read the ten captured names as
one two-point catalogue with holes in it. They are not. Nine of the ten —
`3 Point`, `3/4 Narrow`, `1/2 Narrow`, `3/4 Wide`, `1/4 Wide`, `Side Wide Below`,
`1/4 Wide Below`, `3/4 Ultrawide Below`, `3/4 Ultrawide` — **are exactly the
3-Point list, in order and complete**. The tenth, `Side Ultrawide`, is the ninth
entry of the **2-Point** list. So the captures were a complete sweep of the
3-Point presets plus one 2-Point preset, and the reason `1/2 Wide` appeared to be
a missing sibling of `3/4 Wide` and `1/4 Wide` is that those three names are not
siblings at all — `1/2 Wide` is 2-Point's, the other two are 3-Point's.

Consequences for §15.1. **§15.1 has since been rewritten to carry the corrected
structure and the user's ruling — 19 named presets across three lists, no cross
product. The bullets below are the reasoning that forced that rewrite, kept as
the record of how it was found:**

- **There is no 24-entry two-point catalogue in Concepts.** The cross product
  `4 × 3 × 2 = 24` is not what the app ships; each perspective type ships a
  curated nine. Shipping 24 remained a perfectly good *decision* at the moment
  this was written — Quill's decision, not a reconstruction of Concepts. **The
  user then ruled against it: mirror Concepts exactly. The 24 are off.**
- **The axes are real as vocabulary and not as a grid.** `Narrow` / `Wide` /
  `Ultrawide` and `1/4` / `1/2` / `3/4` / `Side` and the bare / `Below` pair all
  appear, but no list enumerates them combinatorially: 2-Point never says `3/4`,
  3-Point never says `Side Narrow`, and `Ultrawide` appears once in the 2-Point
  list and twice in the 3-Point one.
- **`2 Point` and `1/2 Narrow` are both in the 2-Point list**, adjacent, which is
  consistent with §15.1's finding that they render identically and that keeping
  both is correct.
- Each list ends with **`Custom`**, which the old §15.1 did not mention at all
  and which is how Concepts exposes a hand-placed configuration. The rewritten
  §15.1 carries it.

**Still to do: steps 4 to 7 — the measurement.** The viewport is frozen and the
scratch drawing is in place, but each preset has not yet been captured to its own
PNG and no horizon or vanishing point has been measured, so **no spread /
position / `Below` constants are derived here and none should be inferred from
this section.** What is above is step 3 and step 3 only. When the measurement is
run, the sweep is **19 named presets across three lists**, not 27 in one, and the
per-list grouping is itself a variable: whether `1/4 Wide` in the 2-Point list and
`1/4 Wide` in the 3-Point list place their shared points identically is one of the
things the numbers will answer.


---

## 16. Attachments, the greyed dial, and the canvas that stopped being infinite — 2026-08-17

One capture: a **dark page** with an image attachment selected, the dial at the
top-left with most of its marks greyed, and the attachment carrying a floating
action bar, an edge frame and a bottom action row.

### 16.1 THE CANVAS IS NO LONGER INFINITE — regression, highest priority

The user, marked five out of five: *"at some point you've managed to make the
canvas limited, make it infinite again."*

Quill's canvas is unbounded by design — pan and zoom have never had a stop.
Something in recent work introduced one. **Find the cause before changing
anything**; do not "add infinity back" by removing whatever clamp is found
first, because the clamp may be load-bearing for something else.

Leading suspects, in the order worth checking:

1. **`NotePage.RefFrame`** (§14.5). A frame captured on the page's first
   painted frame, added so vanishing points could be quartered against it. If
   anything treats that frame as the extent of the page rather than as a
   measuring reference, the canvas acquires exactly one page's worth of bounds.
2. **The grid editor's confine-to-artboard** option (§12), if it is being
   applied when it was not asked for.
3. **Any clamp added for panel or chrome geometry** that reached the canvas
   transform by mistake.

Bisect against history rather than reasoning from the code alone — the change
is recent and the symptom is sharp, so a bisect will name it faster than a read.
Report which commit introduced it.

### 16.2 Attachments get quick actions and an edge frame

Text already gets a floating action bar. **An attachment must get one too.**
From the capture, an image attachment when selected shows:

- **A floating bar centred above it**, carrying, left to right: a paperclip, a
  padlock, a duplicate mark, a waste bin, then a **divider**, then flip
  horizontal and flip vertical.
- **Guide lines and corner handles.** Four small hollow circles mark the corners
  of the bounding box. The lines are **NOT a box on those bounds** — an earlier
  version of this section said they were and was wrong. They are **full-canvas
  guides projected from the box**: two verticals at its left and right edges
  running the whole viewport height, two horizontals at its top and bottom
  running the whole width. Thin and low-contrast; they read as alignment
  guides, not as a selection outline.
- **A bottom action row**, centred below: **Rotate**, **Scale**, **Filter**,
  each an icon with its word beside it.

The frame is the part the user called out specifically — *"add the lines that
mark the edges of the attachment."* Selection is currently ambiguous without it.

### 16.3 The dial greys out when an attachment is selected

Because almost nothing in the dial applies to a photo:

- **Grey every mark EXCEPT opacity, undo and redo.** Those three stay live —
  an attachment's opacity is adjustable, and undo/redo always apply.
- **The colour circle goes WHITE and becomes unusable**, in the dial *and* in
  the pen row. You cannot recolour a photograph, and a live-looking colour
  control that silently does nothing is worse than one that says so.
- **Do NOT grey the per-pen colour arcs on the ring.** The user was explicit.
  Those arcs report which colour each pen carries; that fact is still true while
  an attachment is selected, and greying it would destroy information rather
  than disable a control.

Use the disabled treatment the dial already has for an unavailable readout;
this clause is about **which marks** grey, not about a new colour. The grey the
user asked for by hex belongs to the page, not to the dial - see §16.7.

### 16.4 A greyed readout centres in its section

Size, opacity and stability each occupy a section of the inner disc. **When one
is unavailable, its glyph and value move to the middle of that section** rather
than staying in the offset glyph/value arrangement §14.1 describes. Disabled,
there is no value to read, so the split that exists to separate mark from
number has nothing to separate.

### 16.5 Stability and opacity move up and outward

**This supersedes §14.1 item 2 and the `0.33 r` lift.** The user: *"move
stability and opacity up and to the outer side (left for stability, right for
opacity) and move their texts that show the percentage accordingly to not
overlap redo and undo."*

So both the glyphs and their values move **up** and **outward** — stability
toward the left edge of the disc, opacity toward the right. The values follow
their glyphs and must still clear undo and redo, which is what the `0.33 r` lift
was for; moving outward gives more room to do it with, because the arrows sit
low and central.

Measure the result against the arrows' boxes rather than trusting the
constants — §14.1 named that bound correctly and then picked a number that
violated it, and the collision the user reported was `9 × 6 DIP` of digits
sitting on top of an arrow.

### 16.6 The undo and redo arrowheads are asymmetric

*"redo and undo icons are a bit off, the arrow tip is longer on the outer
side."* The head is lopsided — the barb on the outer edge extends further than
the one on the inner edge. Make the head symmetric about its own shaft.

Re-author the geometry on the 24-unit grid rather than nudging numbers, and
render it at the size it actually draws before calling it fixed.

### 16.7 While an attachment is selected, the PAGE fades to grey

Corrected from a misreading. *"make texts the exact shade of grey shown in
photo ... make them slowly turn grey not instantly"* is not about the dial's
labels. It is about **everything the user has put on the page** - pen strokes,
typed text, objects - which de-emphasises while an attachment is selected, so
the attachment reads as the thing being worked on. The greyed handwriting
beneath the attachment in the capture is the example.

**The colour is `#8E8E8E`**, given directly by the user. Not sampled, not
approximated - that exact value.

Three things this must get right:

1. **It is a RENDER-TIME effect and must never touch stored colour.** Nothing
   may write `#8E8E8E` into a stroke, a text run or an object. The page's own
   colours have to come back exactly when the attachment is deselected, and a
   user who saves in this state must not find their drawing greyed on reload.
   This is the one way to turn a visual nicety into data loss.
2. **It animates.** The user was explicit: *"slowly turn grey not instantly"*.
   Use Quill's own motion rather than inventing a duration - `MenuAnim.cs` runs
   190 ms out and 130 ms back on a `(0.12, 0.9) -> (0.2, 1.0)` curve, and the
   fullscreen strip already borrows it. Fade back on deselect too; a
   one-directional fade would leave the page grey until something forced a
   repaint.
3. **The attachment itself does not fade.** It is the subject. In the capture
   it holds full contrast while the ink around it is grey.

Interaction worth deciding rather than assuming: what happens with **two**
attachments, or an attachment selected while a stroke is mid-flight. Report
what the implementation does rather than leaving it to be discovered.

### 16.8 The top bar drops undo and redo when the dial is the surface

The user: *"remove redo and undo if radial dial is selected from top bar as
redo and undo is already present in the radial dial."*

**Conditional on the active tool surface, not unconditional.** The radial dial
carries undo and redo in its lower quadrant (§10.2 item 5), so a second pair in
the top bar is duplication. The **pen row has no undo or redo of its own**, so
when the Bar surface is selected the top-bar pair must stay — removing them
outright would leave that surface with no pointer route to undo at all.

This continues §5's rule that the top bar carries no tools: the bar holds what
has nowhere else to live, and the moment the dial provides a home, the bar's
copy is redundant.

Where it lives: `BtnUndo` and `BtnRedo` in `MainWindow.xaml` (around lines
231-236), immediately preceded by an `AppBarSeparator`.

Four things to get right:

1. **Hide the separator with them if it exists only to divide that pair**, or
   removing the buttons leaves a rule floating against its neighbour. Check what
   the separator actually separates before assuming either way.
2. **Keyboard accelerators are untouched.** `Ctrl+Z` and `Ctrl+Y` are bound
   independently of these buttons and must keep working under every surface. The
   dial is a pointer affordance, not the only route.
3. **`UpdateUndoButtons()` must not fault** when the buttons are not in the
   tree, and must not be the thing that puts them back.
4. **The switch is live.** Changing the surface in settings updates the top bar
   immediately — no restart, no reopening the page. A setting that needs a
   relaunch to take effect reads as a bug.

### 16.9 Drawn strokes get the same selection treatment

*"remember the selection? add the lines and other stuff for selection of
drawings too. (you are already doing these lines and quick actions for
attachments and typed texts)."*

So a selected **stroke** gets everything §16.2 gives an attachment: the floating
quick-action bar above, the four corner handles, the full-canvas guide lines,
and the **Rotate / Scale / Filter** row below. One selection presentation, three
kinds of subject.

**But the dial does NOT grey the way §16.3 describes.** That is the difference
between the two cases and it matters. In the capture, a selected stroke leaves
the dial *live and populated with that stroke's own values* — size reading
`2.31 cm`, stability `0%`, opacity `100%`, and the colour dot showing the
stroke's blue. §16.3 greys the dial for an attachment because a photograph has
no pen size and cannot be recoloured. **A stroke has all of those**, so the
controls stay usable and editing them edits the selection.

Do not generalise §16.3's greying to selection as a whole. It is specific to
subjects that genuinely lack the properties the dial exposes.

### 16.10 Click to select, without dragging

*"make just holding selection button on pen and clicking (not dragging to
select) select the stroke."*

Today selection requires a drag — a lasso or marquee around the target. A
**click on a stroke must select that stroke**, with no drag at all.

**Implement this for the selection modality however it is reached**, rather than
for one trigger. The phrase "selection button on pen" could mean the stylus
barrel button (`_barrelGesture` already exists in `InkSurface`) or the selection
tool chosen in the pen row or dial. The generous reading covers both and cannot
be wrong: **whenever selection is the active modality, a press-and-release
without meaningful movement selects the stroke under the point.** A drag
continues to lasso exactly as it does now.

Three things to get right:

1. **Distinguish a click from a drag by movement, not by timing.** A held press
   that never moves is still a click, and a stylus always jitters a little — use
   a small movement threshold in screen space, and remember the canvas can be at
   any zoom from 0.1x to 16x, so a world-space threshold would mean something
   different at each end.
2. **Hit-test with tolerance.** A hairline stroke is nearly impossible to hit on
   its mathematical path. There is precedent in the file: eraser and selection
   proximity tests already pad by roughly the stroke's own size.
3. **Say what happens when strokes overlap** — topmost, or nearest centre. Pick
   one, state it, and be consistent with whatever the lasso already does.


### 16.11 What §16.4, §16.5 and §16.6 were actually measured at — 2026-08-18

§14.1 named its bound correctly — *the values must clear undo and redo* — and
then chose a number that violated it, and that is how the collision the user
reported got in. So the figures live here, next to the rig that reproduces
each, and the next revision checks numbers against boxes rather than against
prose.

Three rigs, all offline, all reading the **committed source** rather than the
generator that wrote it:

- `scratchpad/verify_icons.py` — reassembles every `const string` in `Icons.cs`
  and re-parses it. A break that lands between a coordinate's x and y fuses
  them, still compiles, and renders **blank**; only reassembly finds it.
  **55 literals, all parse.**
- `scratchpad/head_symmetry.py` — reads `UndoRound` out of `Icons.cs`, takes
  the fill rule off the literal's own `F1` prefix, and measures on the polygon.
  Exact arithmetic where a raster would only answer to a pixel.
- `scratchpad/dial_layout.py` — reproduces `ToolWheel.cs`'s constants and
  prints every clearance the disc has to satisfy.

#### §16.6 — the arrowheads

Everything is in grid units on the authored 24-grid; the right-hand column is
the same figure at the **21 DIP** `SatSize` actually draws.

```
                                  before (1113f8a^)   committed
ink reach past the OUTER edge          4.302             3.220
ink reach inside the INNER edge        3.068             3.229
IMBALANCE                              1.234             0.009   grid units
  at 21 DIP                            1.080             0.008   px
subpath winding                   band CW, barbs CCW    all CW
fill rule                         even-odd (default)    F1, nonzero
subpaths                               3                 2
```

`1.234` is the user's complaint measured: *"the arrow tip is longer on the
outer side"*, by just over a pixel where the dial draws it. It is now
**0.009 grid units, eight thousandths of a pixel** — a 140-fold reduction, and
at the floor the literal's two-decimal coordinates set.

The head is a **true mirror**, not just balanced radially. Its axis of symmetry
must pass through its own centroid, so the angle is the only free parameter;
solved, and the outline reflected back onto itself:

```
best mirror axis                       141.996 deg
reflected outline vs original          RMS 0.0051, max 0.0115 grid units
                                       (0.010 px at 21 DIP)
```

and that axis is the band's own tangent, which is what *"symmetric about its
own shaft"* means:

```
band centre        (12.0000, 12.3015)
inner / mid / outer 7.5903 / 8.1498 / 8.7093     width 1.1191
sweep               284.00 deg, cap centres at bearings 308.00 and 232.00
tangent at the head end                142.002 deg
-> the head's mirror axis is           0.006 deg off its own shaft
```

**`F1` is load-bearing, and it is not the only thing that changed.** The old
head was two barbs wound **opposite** to the band, so it could not have been
repaired by prefixing `F1` — under nonzero the barbs would have *subtracted*
where they cross the band. It shipped even-odd instead, which XOR'd
**0.496 grid²** out of the mark: the white notch at the head's vertex, plainly
visible in a 240 px render. The committed head is **one closed outline wound
the same way as the band**, so nonzero unions them; rendered even-odd it would
lose **1.450 grid², 2.60% of the mark**, where the head crosses the band.

**Redo is not separate geometry.** `Icons.BindTopBar` and `ToolWheel.Button`
both draw `UndoRound` with `ScaleX = -1`, so every figure above covers the pair.

#### §16.5 — the readouts, enabled

All in DIP in the wheel's frame: origin at the disc centre, `+x` right,
`+y` down. `DiscR = 56.84`.

```
undo / redo boxes         x -26.42..-5.42  and  5.42..26.42   y  28.15..49.15
opacity value ink "100%"  x  23.99..46.49                     y   0.40..12.40
stability value ink "0%"  x -41.54..-28.94                    y   0.40..12.40
glyph boxes               x ±31.39..±48.19                    y -20.90..-4.10
```

```
value ink -> arrow BOX          15.75 DIP vertically
value ink -> arrow INK          19.86 DIP vertically
glyph box -> arrow BOX          32.26 DIP vertically
glyph box inside the disc rim    4.31 DIP
value ink inside the disc rim    8.72 DIP
glyph box clears the size row    5.94 DIP
glyph bottom -> value ink top    4.51 DIP
value ink clears the colour dot  4.88 DIP
```

The value and the arrow **do overlap horizontally, by 2.42 DIP**, so the
vertical figure is the whole of the clearance — this is a stacked pair, not a
side-by-side one. The arrow-INK figure is larger than the arrow-BOX one because
`Icons.Mark` keeps the authored 24-grid rather than stretching it, and
`UndoRound`'s ink starts 4.11 DIP down a 21 DIP box.

Every corner of both boxes lies in bearings 45..135 — the opacity section — so
the §11.2 item 13 hover plate still covers them.

**A correction to the figure the first pass recorded.** It said *27.75 DIP of
vertical clearance*. That is the value ink's **top** against the arrow's top,
not a gap: the two branches of the expression that printed it were the wrong
way round, and the same slip made the glyph figure read `-49.05` instead of
`+32.26`. The clearance is **15.75 DIP**. Both `dial_layout.py` and the comment
block in `ToolWheel.cs` now say so. No conclusion moves — the overlap test was
always separate and always passed — but this is precisely the §14.1 failure
repeating one revision later, and it is why the numbers are written down here.

For comparison, §14.1's arrangement (`ValueX 0.50 r`, `ValueY 0.52 r`) measured
the same way:

```
opacity value   OVERLAPS redo by 9.25 x 7.56 DIP
stability value OVERLAPS undo by 4.30 x 7.56 DIP
```

#### §16.4 — the readouts, disabled

The section's middle is the mid-radius point on the quadrant's own midline,
`SectionR = 37.97`, so `(0, -37.97)` for size, `(+37.97, 0)` for opacity and
`(-37.97, 0)` for stability.

```
glyph box 16.80 + gap 2.00 + value line 12.00 = 30.80 tall
stack spans -15.40..+15.40 about the section's middle
centring error                   0.00 DIP
stack inside the disc rim        7.97 DIP
stack vs undo and redo           clear
size row moves                   2.73 DIP  (Row1Y -35.24 -> -37.97)
```

**One assumption here is not a measurement.** The value's line box is taken as
`4/3 x 9 = 12.00 DIP`, WinUI's auto line height for Segoe UI Variable at this
size. The disabled stack centres *exactly* because of it — `DisabledValueDy`
9.25 is `15.40 - 12.00 + 0.65 x 9`. If the real line box differs by δ the
stack's centre moves δ/2 and nothing else changes; the enabled clearances have
15.75 DIP of room and do not care. **Measure the line box on screen and correct
`DisabledValueDy` if it is not 12.00.**

#### §16.8 — the top bar, verified by reading

- **Conditional, in one line:** `bool dialCarriesUndo = ToolSurfaceService.IsWheel;`
  feeds the existing `inContext` channel in `ApplyToolbarVisibility`, the same
  one that takes `TouchDrawToggle` away when the pen is not up. `HiddenTools`
  still overrides in both directions.
- **The accelerators cannot break.** `Ctrl+Z` / `Ctrl+Y` are built by
  `ApplyKeyPreset` into `RootGrid.KeyboardAccelerators` from the shortcut table
  and invoke `UndoAccel_Invoked` -> `Surface.Undo()`. Neither `BtnUndo` nor
  `BtnRedo` appears anywhere on that path, so collapsing them cannot stop it.
- **`UpdateUndoButtons` writes only `IsEnabled`**, null-guarded, so it can
  neither fault on an absent button nor put one back.
- **Live:** `ToolSurfaceService.Changed` fires on `Set` and now runs
  `ApplyToolbarVisibility` alongside `ApplyPenRowVisibility`.
- **One owner.** Only four references to `BtnUndo`/`BtnRedo` exist in the tree:
  `UpdateUndoButtons` (IsEnabled), `ApplyToolbarVisibility` (Visibility), and
  `ToolWheel.TopBarKey`. `PenBar.TopBarKey` can only ever return `ToolPen`,
  `ToolText`, `ToolSelect`, `ToolSpace`, `ToolComment` or `ShapeBtn`.
- **`ToolWheel.TopBarKey` maps `cmd:Undo -> "BtnUndo"`**, so a user who puts
  Undo in a dial slot hides the top-bar copy through the hand-back as well.
  That reinforces this rule under Wheel and cannot reach it under Bar.
- **`SepUndo` is now null-safe too.** `ApplyToolbarVisibility`'s whole body is
  inside one `try/catch`, so a null reference in the separator's test would be
  swallowed and would take the eight `Set()` calls below it with it — the
  toolbar would quietly stop following the user's choices.
- **§16.8's premise is still false and the finding stands.** `Controls/PenBar.cs`
  floats undo and redo below the panel as bare satellites, from the same
  `Icons.UndoRound`, so under the **Bar** surface the pair appears twice. Whether
  Bar should lose the top-bar copy too is the user's call; `dialCarriesUndo` is
  the one line that would change.

#### Not verified on screen

**Everything above is arithmetic on the committed source and offline rasters.**
No surface was switched in a running app, no readout was seen disabled, and the
21 DIP arrowhead has been rendered but not photographed off the display. What
remains for a screen pass: the disabled stack's real line box (above), the
arrowhead at its true DPI, and the top bar redrawing on a live surface switch.

---

## 17. The Measurement menu, the bottom bar, and a correction pass — 2026-08-18

Six captures and a numbered response to the five checks. §17.1–17.5 are new
work; §17.6–17.15 are corrections to what was just built.

### 17.1 Zoom and tilt get a Measurement menu

The zoom and tilt readouts in the top bar open a panel titled **Measurement**,
with an (i) info mark at its right. Two sections:

- **Zoom** — a magnifier mark, the live value (`10%`), and a **padlock**. Below,
  a row of preset buttons: `10%` `100%` `250%` `1600%`, the active one filled.
- **Rotation** — a rotate mark, the live value (`0°`), and its **own padlock**.
  Below: `0°` `90°` `180°` `270°`, active one filled.

**Two separate locks, not one.** Zoom and tilt lock independently.

**Hover** shows the two readouts as a dark pill carrying a padlock beside each
value — `[lock] 10%  [lock] 0°`.

**Locking tilt shifts the zoom readout sideways** to make room for the lock
glyph appearing beside the tilt value. The row re-lays out; it does not overlap.

`1600%` is the ceiling of the zoom range finalised at 0.1×–16×. Read
`InkSurface.MinZoom` / `MaxZoom` rather than repeating those numbers — they were
consolidated from three copies at two values precisely so a fourth would not
appear.

### 17.2 The corner buttons get a page-coloured ground, and zoom/tilt become stadiums

The top-corner buttons carry a **background that mimics the page colour**, so
the button almost disappears into the page — **but the page's grid and texture
do NOT continue across it**, and that discontinuity is what makes the button
findable. A flat patch of page colour over a textured or gridded page reads as a
soft plate rather than as chrome. Continuing the texture across it destroys the
entire effect.

**The zoom and tilt readouts take a stadium shape** — a rounded-end capsule
sized to their text. Every other corner button stays a circle.

### 17.3 Custom colour keeps the user's colour, and reopens the wheel

**Custom colour in page settings must NOT mirror the current page colour.** It
holds **the last colour the user chose**, and keeps holding it when the page
colour changes by other means.

**Pressing it while already selected opens the COPIC wheel** to pick a new page
colour. So: first press applies the remembered colour, second press edits it.
The user marked this **4 out of 5**. The press-once-apply / press-twice-edit
pattern was asked for once before; match it exactly rather than inventing a
second convention.

### 17.4 Dark mode: the dial's tools and pens must not be transparent

In dark mode the tool and pen marks in the radial dial **render transparent**.
They must instead be filled with **the page background colour, without the
page's texture** — the same treatment §17.2 gives the corner buttons.

Establish *why* they come out transparent before changing anything. A mark
correct in one theme and transparent in the other usually means a colour is
resolved from a token with no definition on that side, which §0 warns about
directly.

### 17.5 The panel close and help marks fill their corner

A square covering the **whole corner** of the panel, with **one** corner rounded
to follow the panel's own radius and the other three square. Not a circle, not a
small mark floating in padding. §14.3 asked for this and overshot past the
panel's edge onto the page; this is the shape it should have been.

---

The rest are corrections to work just built.

### 17.6 A panel returns PROPORTIONALLY, not at a fixed size

The inset model restores a panel's position but keeps its clamped **size**. The
user: *"I want the panel to return to its proportional size, so for example 100
pixels away from bottom edge, and panel gets 50% smaller 50 pixels away now."*

So the remembered geometry **scales with the host** rather than being restored
literally. A panel that sat 100 DIP from an edge sits 50 DIP from it when the
host halves, and the size scales the same way. Store the inset **as a fraction
of the host**, not as an absolute distance.

This supersedes the absolute-inset storage. **Keep the non-destructive
clamping** — the two are compatible, and the round-trip proof must still hold.

### 17.7 Click on a stroke selects; click on empty opens the menu

Today a selection click produces **both** the quick actions and the right-click
dropdown. The rule:

- **holding selection and clicking ON a stroke** — quick actions for that
  stroke, and **no** dropdown;
- **clicking NOT on a stroke** — the right-click dropdown menu, as before.

§16.10 made the click select only when a stroke is under it, which is half of
this. The other half is suppressing the menu in that case.

### 17.8 The selection tint goes

**Remove the selection fill.** Only the edges remain — the corner circles and
the full-canvas guides of §16.2. No tinted rectangle, no dashed box.

This is the suppression offered when the selection chrome landed and deferred
until the user had seen it. They have now seen it.

### 17.9 The bottom-of-screen mode bar

**Quick actions stay above the subject, but nothing floats below it.** The
bottom row moves to the **bottom of the screen** and becomes a mode bar:

1. **Rotate** — on/off
2. **Scale** — on/off, and stretch
3. **Filter** — opens the colour picker, and Alpha on/off

**Filter descends into the colour picker's own bottom menu.** Because it was
reached from the mode bar, that submenu carries a **back button** returning to
the three-part bar. Reached as a tool in its own right, the colour picker has
**no** back button. That conditional back button is the detail most likely to be
missed.

**Bottom menus name intent with an icon and carry only essential words.** Strip
explanatory text; the icon does the work.

### 17.10 The mouse tool

Filter acts on the mouse tool directly, so the mouse tool has to exist. Its
bottom menu:

1. **Item picker / Lasso.** When Lasso is chosen, a further control appears to
   its right: **Partial / Complete**.
2. **Include / Ignore** — a locked padlock and an unlocked padlock.
3. **All / Active** — all layers, or the active layer only.

The capture reads: `< | Lasso | Complete | Include | All`.

**Lasso becomes part of the mouse tool** rather than a separate selection mode.

All/Active is a **layer scope**, so this depends on the layer model. Use the
seam that model defines; do not invent a competing layer concept.

### 17.11 Pan and rotate tools

Add a **pan** tool and a **rotate** tool alongside the others.

### 17.12 Sizes

- **Quick action buttons: +80%.**
- **Bottom-of-screen menu buttons: +100%.**

### 17.13 The attachment itself must not fade

The page fade works, but *"the attachment also turns grey for a moment and
returns after clicking out of it; clicking into the attachment does not do
this."*

The subject is being veiled for a frame or two before it is recognised as the
subject — an **ordering fault, not a colour one**. The selected attachment must
**never** fade, not even transiently. Find where the veil is applied before the
selection state has settled and reorder it. **Do not paper over it with a second
exemption**: that hides the race rather than removing it, and it returns the
moment settle timing changes.

### 17.14 A disabled readout shows nothing, not a dash

The centring is right and the user said so. But an unavailable readout still
shows `-`. **Remove the dash entirely** and centre the glyph in its button.

There is exactly one disabled predicate in the dial and there must not become a
second.

### 17.15 Fullscreen text mode has too much margin

*"in full screen text mode text appears a long way down. There's a big margin
there still."*

The format bar and the app's top bar now stack, and `FormatBarStripGap` widened
the gap further. Text starts far below where it should. Reduce the stacked
margin — and check whether the two bars need to occupy separate rows in
fullscreen at all, since §15.3 already moved the window controls into a hover
strip that appears only on demand.

---

## 18. Layers — the data model, 2026-08-18

The roadmap parks four features behind one sentence: *"Layers. The data model is
the blocker for PSD export, per-layer visibility, selection scoping and
per-object rows in the Objects library."* This section is that model. **There is
no layers panel and this section does not describe one** — it describes the data,
what happens to the pages that already exist, and the five places the four
features attach.

> Written as 17 and renumbered to 18 once `42b00ab` put the correction pass into
> section 17, where its commit message had always said it was. If you are here
> for the mode bar's layer scope, it is 18.1 and it is the first thing in this
> section for that reason.

### 18.1 THE SCOPE SEAM — read this first if you are building the mode bar

The bottom mode bar's third control is a **layer scope: All layers / Active
layer**. It does not need the rest of this section. It needs one enum and one
predicate, and both exist:

```csharp
public enum LayerScope { AllLayers, ActiveLayer }        // AllLayers is the zero value

PageLayers.InScope(page, element.LayerKey, scope)        // "is this in scope?"
PageLayers.CanSelect(page, element.LayerKey, scope)      // scope AND the layer is not hidden or locked
```

**`AllLayers` is deliberately the zero value**, so a default-constructed scope,
an unset field and a stub that has never heard of layers all mean *today's
behaviour*: everything is in scope.

**On a page with one implicit layer, both scopes return true for everything.**
That is the compatibility guarantee the mode bar can build against — a stub that
hard-codes one layer and a finished build running a real layer list cannot
disagree until the user actually makes a second layer. Nothing about the mode bar
has to change when they do.

**Where the current scope is stored is the mode bar's business, not this
model's.** `LayerScope` is a tool mode — it belongs beside whatever else the mode
bar persists, and this model deliberately neither stores it on `NotePage` nor
mirrors it into `Library`. Passing it in as an argument is the whole interface.
There is no second layer concept to invent and none should be invented: if
something needs to know about layers, it asks `PageLayers`.

The active layer itself is `PageLayers.Active(page)`, and it resolves — an
`ActiveLayer` naming a layer that no longer exists comes back as the base layer
rather than leaving the page with nowhere to draw.

### 18.2 The shape of it

```
NotePage
  Layers      List<Layer>?   bottom-first z-order.  null/empty = ONE implicit base layer
  ActiveLayer int            the layer new ink lands on.  0 = base

Layer
  Key         int            stable identity for the life of the page.  0 = the base layer
  Name        string         "" = derive "Layer N" from position
  Hidden      bool           false = visible (today)
  Opacity     float          1 = fully opaque (today), a MULTIPLIER over element opacity
  Locked      bool           false = editable (today)
  CreatedTicks long

PenStroke / ShapeElement / TextElement
  LayerKey    int            which layer.  0 = base.  Absent = 0.
```

Every default is today's behaviour, which is the whole trick: a page that has
never heard of layers deserialises into a page with one visible, unlocked,
fully-opaque layer holding everything, and does so without a single byte on
disk changing hands.

`Key` is identity; **list position is order**. There is deliberately no `Order`
integer beside `Key`: a list is already ordered, and a second source of truth for
the same fact is a bug waiting for the two to disagree. Reordering moves the item
in the list and touches nothing else, because nothing else references position.

### 18.3 Why the membership key is an `int` and not a `Guid`

`LayerKey` appears on **every stroke, every shape and every text box in the
library** — it is by a wide margin the most-repeated new field this model adds.
A `Guid` serialises as 36 characters plus its property name; on a library that is
already 53 MB and is rewritten whole on every 1.5 s autosave, that is several
megabytes of autosave cost bought for nothing. A small int with
`WhenWritingDefault` costs **exactly zero** for anything on the base layer, which
is everything that exists today, and about fourteen bytes for anything that is
not.

The int is a **key, never an index**. Keys are handed out by
`PageLayers.NextKey` and never reused within a page, so reordering or deleting a
layer cannot silently repoint content at a different one — the exact hazard the
`GridType` comment in `NoteModels.cs` warns about for appended enums.

The cost of choosing an int is that keys are **page-scoped**: an element copied
to another page carries a key that means something different there, and the paste
path has to re-key it. That is a real obligation and it is written down here so
the person who builds cross-page paste finds it. See 18.10.

**`LayerKey` carries `TolerantIntConverter`** — the converter already in
`NoteModels.cs`, used unmodified. The reason is the one recorded beside
`Library.ColorPickerMode`: *"a shipped build wrote `"Copic"` here, and a plain
int property makes that file undeserializable — which cost the whole library."*
`LayerKey` is on hundreds of thousands of elements; if any future build ever
writes a layer *name* where the key goes, a plain `int` would make **the entire
library unloadable**. With the tolerant converter, an unreadable key reads as 0,
the element lands on the base layer, and the user loses a layer assignment
instead of their notes. The converter's `"copic"/"hsl"/"rgb"` name map is inert
here because nothing ever writes those strings to this field; it is left exactly
as it is rather than generalised, because touching it would put
`ColorPickerMode` at risk to tidy up a case that cannot arise.

### 18.4 Migration: nothing is migrated

Every page that exists has content with no layer. The temptation is a load-time
pass that walks the library, materialises a base layer on every page and stamps
every element with its key. **That pass is the most dangerous code this feature
could contain** — it rewrites all 53 MB on first launch, it runs before the user
has done anything worth saving, and its failure mode is a library that has been
half-converted.

So it does not exist. The model migrates by *meaning* instead:

- **`Layers` null or empty means one implicit base layer.** `PageLayers.All`
  synthesises it on read. Nothing is written.
- **A missing `LayerKey` deserialises to 0, which is that base layer.** Nothing
  is written.
- **The list is materialised lazily**, by `PageLayers.Materialise`, the first
  time the user does something that genuinely needs a second layer. Only then
  does a page start carrying `"Layers":[…]`, and even then the elements already
  on it keep no `LayerKey` at all, because 0 is still right for them.

A page saved by a layers-aware build and read by a build that predates layers
therefore loses the layer *records* (System.Text.Json ignores unknown properties
by default; they are dropped on the next save by the old build) and **loses no
content whatsoever**, because the content never moved. `Strokes`, `Shapes` and
`Texts` stay exactly where they have always been on `NotePage`. That is the
second reason membership lives on the element rather than the content living
inside the layer: a model where `Layer` owns `List<PenStroke>` reads as an empty
page to anything that does not know about layers.

### 18.5 Z-order: the one rule that keeps an existing page identical

Today a page paints **all shapes, then all strokes, then the text overlay**, each
in list order. Layers introduce a second ordering, and the two have to be
reconciled without changing what an existing page looks like.

> **Within a layer, the existing type order is preserved exactly. Across layers,
> layer order wins.**

With zero or one layer that rule is bit-identical to today's painting, which is
what makes it safe to land the model before the renderer knows about it. The
harness asserts it directly: for a page with no layers, `PageLayers.InOrder`
yields exactly one bucket holding every element in exactly the order the draw
loop walks today.

`InOrder` is a single function returning, bottom layer first,
`(Layer, Shapes, Strokes, Texts)`. It is the seam the renderer will adopt and the
seam PSD export will iterate, so the two can never disagree about what is on top.

### 18.6 What belongs to a layer, and what does not

| Belongs | Does not |
|---|---|
| `PenStroke` | `NotePage.Background`, `Paper` |
| `ShapeElement` (including image attachments) | the grid, and `PerspectiveDef` |
| `TextElement` | `PageComment` |
| | `RefFrame`, page size, `OcrText`, audio |

Two of those are judgement calls rather than obvious.

**Comments are not layered.** A comment is a note *about* the drawing, not part
of it; hiding a layer must not hide the pin that says why the layer is wrong,
and a PSD export has nowhere to put one. If comments ever need scoping they want
an anchor to an element (`PageComment.AnchorElementId` is already reserved for
it), not a layer of their own.

**The grid and the perspective overlay are not layered**, even though §12's
editor calls them "the grid layer" and the Precision menu says so too. They are
page-wide guides with their own visibility and opacity controls already; giving
them a second, competing set inside the layer list would produce two switches for
one fact. What the reference calls a grid layer is a *menu*, not a member of this
list.

### 18.7 The fail-safe: an unknown key is visible, never hidden

The failure this model has to refuse is content that exists, is intact, and
cannot be seen. Every resolution path therefore falls **towards** visibility:

- A `LayerKey` naming no layer resolves to the base layer and draws. It is
  **not** rewritten — a build that later restores the missing layer reunites the
  content with it, which a helpful load-time repair would have made impossible.
- `ActiveLayer` naming no layer resolves to the base layer.
- A garbled key read through `TolerantIntConverter` becomes 0, the base layer.
- Deleting a layer **reassigns its content to the base layer by default**
  (`LayerRemoval.ReassignToBase`). Deleting the content with it is available and
  is never the default.
- The base layer cannot be deleted. A page always has at least one layer, so
  there is always somewhere for content to be.

### 18.8 Visibility and opacity are render-time, exactly like the veil

§16.7 cost this project a whole harness to establish that *"it is a RENDER-TIME
effect and must never touch stored colour."* Layer opacity is the same shape of
promise and it is kept the same way: `PageLayers.EffectiveOpacity` returns
`Hidden ? 0 : Opacity`, the renderer multiplies it into the element's own alpha
at draw time, and **nothing ever writes it back**. Hiding a layer and dropping it
to 30% must leave every stroke's stored `Opacity` byte-for-byte as it was.

`tools/LayerRoundTrip` proves precisely that, the way `tools/VeilRoundTrip`
proves §16.7 — see 18.11.

### 18.9 The five seams

Named, not built. Each is one call.

1. **PSD export** iterates `PageLayers.InOrder(page)` — bottom layer first, one
   PSD layer per `Layer`, its shapes then its strokes then its texts. `Layer.Name`
   (via `DisplayName`) is the PSD layer name; `Hidden` and `Opacity` map straight
   onto the PSD layer flags rather than being baked into pixels.
2. **Per-layer visibility** toggles `Layer.Hidden` and multiplies
   `PageLayers.EffectiveOpacity` into the draw call. The renderer's existing
   per-element loops become the body of the per-layer loop; nothing else moves.
3. **Selection scoping** asks `PageLayers.IsEditable(page, element.LayerKey)` —
   false when the layer is hidden or locked — and
   `PageLayers.InActive(page, element.LayerKey)` for "is this in the active
   layer", or `PageLayers.CanSelect(page, element.LayerKey, scope)` for both at
   once against a **All layers / Active layer** mode (18.1, which is the form the
   bottom mode bar wants). All are pure predicates over the page, so they compose with the
   existing `Locked` flag rather than competing with it: an element is editable
   when **neither** it nor its layer is locked. `SelectionSubject` is untouched —
   layers decide *what may enter* a selection, `SelectionSubject` describes what
   the selection *supports*, and those are different questions.
4. **The Objects library** lists `PageLayers.Rows(page)` — one `LayerRow` per
   object, grouped by layer, **top layer first** because that is the order a
   layers list reads in. A row is `(Layer, Kind, Id, Label, Locked)`; the caller
   already holds the page, so a row carries identity rather than a copy of the
   element.
5. **New ink** lands on `PageLayers.Active(page)`. The commit path stamps
   `LayerKey` from it; `AssignLayerAction` (in `UndoRedo.cs`, modelled on
   `LockMixedAction`) moves a selection between layers undoably.

### 18.10 Obligations this model creates

Written down because each is a place where a later change loses data quietly.

- **`PenStroke.CloneWithPoints` must carry `LayerKey`.** It is how the eraser
  fragments a stroke, how duplicate works and how the selection clone works —
  without it, erasing through a stroke on layer 3 drops its fragments onto the
  base layer. This is fixed as part of the model, not left to the renderer.
- **Cross-page paste must re-key.** Keys are page-scoped (18.3).
- **A table and its cells must share a layer.** A `ShapeElement` of kind `Table`
  and the `TextElement`s carrying its cells (`TableId`) are one object to the
  user and three lists to the model. Nothing stops them being assigned
  separately today, and `LayerRemoval.DeleteContent` on a layer holding only the
  cells would leave a table drawn with its contents gone. Whatever moves a
  selection between layers should carry a table's cells with it — the same
  obligation `ReflowTableCells` already discharges for geometry.
- **`SyncLog` must carry the layer list in the page op.** Element ops serialise
  the whole element, so `LayerKey` rides along for free — but `PageMetaJson` is a
  hand-picked field list, and a peer that receives keys without the layers they
  name would resolve every one of them to the base layer. `Layers` and
  `ActiveLayer` are added to the page op and to its apply side for that reason.
- **`Layers` is not in `LibraryStore.SettingFields`** and must never be: those
  are library-wide settings mirrored into `settings.json`; layers are page
  content.

### 18.11 The proof

`tools/LayerRoundTrip`, built the way `tools/VeilRoundTrip` is built — the
**real** `NoteModels.cs`, the **real** `LibraryStore.cs`, `<Compile Include>`d
straight out of `src/Quill` so the harness cannot drift into testing a copy, run
headless against a library isolated in a temp folder by `QUILL_DATA_FOLDER`, with
a hard abort if the resolved path is not inside it.

It settles, by doing it rather than asserting it:

- a page written before layers existed loads, and every element lands on a
  visible base layer;
- adding layers costs an untouched page **zero bytes** — the byte-for-byte
  baseline comparison;
- a full page (strokes, shapes, an image attachment, text, comments) survives
  save → reload with every layer field and every `LayerKey` intact;
- hiding a layer and setting another to 30% changes **only** the layers array —
  no element's stored bytes move;
- deleting a layer reassigns rather than deletes, and the content is still
  visible afterwards;
- an element pointing at a layer that does not exist still draws;
- a `LayerKey` written as a **string** — the `ColorPickerMode` disaster,
  reproduced on purpose — still loads the library;
- `InOrder` on a page with no layers reproduces today's draw order exactly;
- and 18.1's compatibility guarantee: on a page with one implicit layer the two
  scopes are indistinguishable.

**69 checks, all holding**, as of this section. Two of them are sharper than the
prose above and worth naming:

- *"the ONLY thing that changed is inside the layers array"*. Two saves of the
  same library, one with a layer hidden and another at 30% and one without.
  Strip every bracket-matched, string-aware `"Layers":[…]` out of both files and
  what remains is **byte-identical, 4261 bytes each side**. That is 18.8 as a
  measurement rather than a promise.
- *"the first page op carries no layer list"*. The op log is diffed by hash, so
  a page that has no layers must hash to exactly what it hashed to before layers
  existed — otherwise the first save after upgrading emits one page op per page
  in the library. It does, and the check reads the log to say so.

Run: `dotnet run --project tools/LayerRoundTrip/LayerRoundTrip.csproj -c Debug`

**One thing lives outside the isolation folder, and this harness puts it back.**
`SyncLog` keeps its per-device cursors in `%LOCALAPPDATA%\Quill\synccursors.json`
— *not* in the data folder — and `LibraryStore.Save` calls `SyncLog.OnSaved`
unconditionally. Any harness that saves therefore rewrites the real user's cursor
file from an empty in-memory one, resetting the read offset for every peer; per
the roadmap's own unowned "SyncLog replay" risk, a reset cursor triggers the full
replay that can **resurrect erased strokes**. `LayerRoundTrip` snapshots that file
and `deviceid.txt` before its first save, restores both byte-for-byte in a
`finally`, and checks the restore as its last line. **`tools/VeilRoundTrip` does
not**, and neither will the next harness written to this pattern unless the
behaviour moves into `SyncLog` itself.

### 18.12 Left for the user to rule on

These are decisions the model does not force, and they were left open rather than
made quietly:

1. **Blend modes.** PSD layers have them; Quill's renderer has none. No
   `Layer.Blend` field is stored, because a field nothing implements is a lie in
   the file. Adding it later costs nothing (`WhenWritingDefault`); it would be a
   string, not an enum, per the `ToolSurface` precedent.
2. **Whether hidden layers export.** Currently `Hidden` is carried into PSD as a
   layer flag rather than dropped — the PSD keeps the content and hides it. PDF
   and SVG export have no such flag and would have to drop it.
3. **Deleting a layer.** Default is reassign-to-base. Photoshop deletes the
   content. The safe default was chosen; the other is one enum value away.
4. **A cap on layer count.** None is imposed.
5. **Layer names are derived in English** (`"Layer 3"`) when `Name` is empty.
   `Helpers/Loc.cs` exists; whether a derived name should be localised — and so
   change when the UI language changes — is a product decision.
6. **Per-notebook or per-library default layer sets.** Not modelled. Every page
   starts with one implicit layer.
