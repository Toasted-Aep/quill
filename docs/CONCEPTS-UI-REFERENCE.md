# Concepts UI reference — for the Quill conversion

Research deliverable, 2026-08-05. Written so an implementer never has to guess a
size, spacing, colour or behaviour. Companion to `UI-SPEC-V2.md` (dial geometry)
and `UI-SPEC-V3.md` (user requirements A–J).

---

## 0. Provenance, units, and how to read every number here

### 0.1 Sources and their citation keys

| Key | Source |
|---|---|
| `[SS:<name>]` | User screenshot, `C:\Users\irony\Documents\ShareX\Screenshots\2026-07\<name>.png` |
| `[MAN:p<n>]` | Official Concepts manual, `C:\Users\irony\Downloads\concepts-manual-latest.pdf`, PDF page `<n>` (TOC page numbers == PDF page numbers in this file) |
| `[MANSHOT]` | `C:\Users\irony\Downloads\Screenshot 2026-07-22 at 17-09-22 Settings - Concepts Manual.png` (full-page capture of the manual's Settings page) |
| `[WEB:<file>]` | User's own web implementation, `C:\Users\irony\Downloads\New folder (4)\Concepts\src\...` — **web JS/JSX/CSS only** |
| `[V2]` / `[V3]` | `docs/UI-SPEC-V2.md` / `docs/UI-SPEC-V3.md` |

Every claim below carries one of these. Anything with **(inferred)** was derived
from proportions, not read directly — treat it as a design decision, not a fact.

### 0.2 Measurement basis — READ THIS BEFORE USING ANY NUMBER

All pixel measurements were taken with Python/PIL from the user's screenshots,
not by eye. The anchoring capture is:

- **Image size:** 2880 × 1800 physical px `[SS:ApplicationFrameHost_9I8pRX5lPh]`
- **Display:** 1440 × 900 logical, `LogPixelsX=96`, `DesktopVertRes=1800`,
  `VertRes=900` → **200 % scaling**, verified live on the user's machine.
- Therefore **DIP = physical ÷ 2** throughout. Windows title bar measured 64
  physical px = 32 DIP, which is the standard value and confirms the factor.

Tables give **physical px (@200 %)** and **DIP** side by side. Implement in DIP.

> Caveat: Concepts' wheel is user-scalable (pinch, or mouse-scroll over it —
> `[MAN:p90]`). Every dial number below is the size **as the user had it in these
> captures**. Ratios are scale-invariant and are the thing to implement; the
> absolute DIP figures are a sane default, not a constant.

### 0.3 Angle convention

Screen convention, matching SVG/Win2D: **0° = east (3 o'clock), angles increase
clockwise**, y grows downward. So 90° = south, 180° = west, 270° = north
(12 o'clock).

`[V2]` uses the opposite (counter-clockwise, y-up) convention. Both describe the
**same geometry** — I verified the conversion and `[V2]`'s arcs are correct. See
§2.3 for the mapping so nobody "fixes" a spec that isn't broken.

---

## 1. Layout map — every persistent surface when the tool wheel is active

Anchor capture `[SS:ApplicationFrameHost_9I8pRX5lPh]`, window maximised at
1440 × 900 DIP.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ☰  Concepts                        +   👤  PRO  ⛶      —   ▢   ✕             │  Title bar, 32 DIP
├──────────────────────────────────────────────────────────────────────────────┤
│ ▦  Drawing │ ☰   ⣿   ⌾              🔒100%  0°   ⤓   ⤒   ⚙   ?               │  Status bar (transparent)
│                                                                              │
│      ╭───────────╮                                                           │
│    ╭─┤ TOOL WHEEL├─╮                                                         │
│    │ ╰───────────╯ │        ← wheel is DOCKED here, always visible           │
│    ╰───────────────╯                                                         │
│                                                                              │
│  ☰ Layers                                                    ┌─────────────┐ │
│    ↕ Sorting │ Manual                                        │  SETTINGS   │ │
│    + New Layer                                               │  (docked,   │ │
│    ▭ Pen      100%                                           │   right,    │ │
│    ▭ Marker   100%   ← active                                │  full-ht)   │ │
│    ▭ Pencil   100%                                           │             │ │
│    ▭ Custom   100%                                           │             │ │
│                                                              │             │ │
│  ⣿ Precision                                                 │             │ │
│    ■ Grid │ Dot Grid                                         │             │ │
│    □ Snap │ Options                                          │             │ │
│    □ Measure │ 1:64 px                                       │             │ │
│    □ Guide │ Arc                                             │             │ │
│    ■ Recognition │ Options                                   └─────────────┘ │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 1.1 The single most important structural fact

**Concepts' on-canvas menus have NO chrome.** No panel background, no border, no
card, no blur, no rounded rectangle. Layers, Precision, the Measurement popup and
the wheel's outer ring are drawn as bare text + icons directly over the canvas —
the dot grid is visible *between the rows*.

Measured proof: sampling the region behind the status bar
(x 600–1900, y 100–150) returns `#000000` 98 % + `#3f3f3f` 1 % — i.e. canvas
black plus the grid dots, with no intervening surface `[SS:…9I8pRX5lPh]`. The
same is true behind Layers and Precision.

The **only** surfaces with a real background are:
1. the **title bar** (`#1e2025`),
2. the **Settings panel** (`#030303`, docked right, full height),
3. the **tool wheel hub** (`#262626`),
4. the tool wheel's **outer ring**, which is a *translucent black scrim* (§2.6).

This is the biggest single visual difference from most implementations, which
default to floating "liquid glass" cards. See §8.

### 1.2 Title bar — Windows only `[MAN:p77-78]`

| Property | Physical px | DIP | Source |
|---|---|---|---|
| Height | 64 | **32** | measured, `[SS:…9I8pRX5lPh]` column x=1200: `#1e2025` rows 0–63, `#000000` from 64 |
| Background | `#1e2025` (also samples `#1f2023`) | — | sampled |
| Title text | `#ffffff` | — | sampled |

Contents, left → right `[MAN:p77-78]`:
hamburger drop-down · app name "Concepts" · **(right side)** `+` new drawing ·
Account · `PRO` (Pro Store) · Full-screen · minimise · maximise · close.

The drop-down menu contains: save, open, save as, export, settings, about,
in-app purchases, "Ask Us Anything" support, exit `[MAN:p77]`.

### 1.3 Status bar — transparent, floats on the canvas `[MAN:p78-79]`

Measured glyph boxes, physical px, `[SS:…9I8pRX5lPh]`:

| Item | x span | w × h | centre x | DIP centre |
|---|---|---|---|---|
| Gallery icon | 46–77 | 32 × 32 | 61.5 | 30.75 |
| "Drawing" (name) | 128–235 | 108 × 27 | — | — |
| Layers toggle | 306–334 | 29 × 25 | 320 | 160 |
| Precision toggle | 391–417 | 27 × 27 | 404 | 202 |
| Objects toggle | 473–503 | 31 × 31 | 488 | 244 |
| Zoom lock | 2326–2341 | 16 × 19 | 2333.5 | 1166.75 |
| "100%" | 2356–2410 | 55 × 17 | — | — |
| "0°" | 2457–2477 | 21 × 17 | — | — |
| Import ⤓ | 2550–2580 | 31 × 27 | 2565 | 1282.5 |
| Export ⤒ | 2634–2664 | 31 × 27 | 2649 | 1324.5 |
| Settings ⚙ | 2721–2747 | 27 × 27 | 2734 | 1367 |
| Help ? | 2809–2826 | 18 × 28 | 2817.5 | 1408.75 |

Derived rules:
- **Icon pitch is 84 physical px = 42 DIP**, uniform on both clusters
  (layers→precision 84, precision→objects 84; import→export 84, export→settings 85,
  settings→help 83.5).
- **Nominal icon box 32 × 32 physical = 16 × 16 DIP** glyph; hit target 42 DIP.
- Left margin to first icon centre 61.5 px; right margin from window edge to help
  centre 62.5 px — **symmetric ≈ 31 DIP margins**.
- Row centre at y = 126 physical = **63 DIP** from window top, i.e. 31 DIP below
  the title bar. Content vertical extent y 110–143 physical.
- The three menu toggles show **a line under the icon when that menu is on
  canvas** `[MAN:p93]` — visible in `[SS:…9I8pRX5lPh]` as short underlines beneath
  the layers/precision/objects glyphs.
- The Pro Store button reads `PRO` when purchased, `Go PRO` otherwise
  `[MAN:p79]`. On Windows it lives in the **title bar**, not the status bar
  `[MAN:p79]`.
- The help `?` carries a small **purple dot badge** (unread stories) — sampled
  lavender, `[SS:…9I8pRX5lPh]`.

### 1.4 Tool wheel placement

| Property | Physical px | DIP |
|---|---|---|
| Centre | (207, 355) | **(103.5, 177.5)** |
| Outer radius | 187 | **93.5** |
| Bounding box | x 20–394, y 168–542 | x 10–197, y 84–271 |
| Left margin | 20 | **10** |
| Top of wheel below title bar | 168 − 64 = 104 | **52** |

The wheel is **docked top-left and permanently visible** — there is no
hold-to-summon on desktop. It is a *movable canvas element*: tap+hold+drag
relocates it, and dropping it on the canvas layout manager in the middle of the
screen converts it into the **Tool Bar** `[MAN:p87]`. Scroll-wheel over it scales
it `[MAN:p90]`.

### 1.5 Left column (Layers + Precision)

Measured row bands, physical px, `[SS:…9I8pRX5lPh]`:

| Element | y band | pitch |
|---|---|---|
| "Layers" header | 575–599 (cap 25) | — |
| "Sorting │ Manual" | 654–681 | 79 |
| "+ New Layer" | 735–760 | 81 |
| Layer rows | ~822, 945, 1067, 1188 | **122** |
| "Precision" header | 1303–1323 (cap 21) | — |
| Grid row | 1396–1419 | — |
| Snap / Measure / Guide / Recognition | 1476, 1556, 1636, 1716 | **80** |

| Derived | Physical | DIP |
|---|---|---|
| Text-row pitch (Precision, Sorting/New Layer) | 80 | **40** |
| Layer-row pitch (with thumbnail) | 122 | **61** |
| Layer thumbnail | 186 × 114 | **93 × 57** |
| Left margin, panel icons | 44 | **22** |
| Left margin, panel text | 90 | **45** |
| Gap: wheel bottom (542) → Layers header (575) | 33 | **16.5** |

The layer thumbnail aspect is 186/114 = **1.63**, essentially the 1440×900 canvas
aspect (1.60) — the thumbnail is a scaled view of the canvas **(inferred)**.

### 1.6 Right side — Settings panel

| Property | Physical px | DIP | Source |
|---|---|---|---|
| Left edge | x ≈ 2083 | 1041.5 | measured, `[SS:…oFjbQ2DItz]` row y=900 |
| Width | 797 | **398.5** | 2880 − 2083 |
| Height | full, below title bar | — | observed |
| Background | `#030303` | — | sampled (empty region x2400–2800, y600–700) |

There is a soft shadow/gradient edge from x ≈ 2020 to 2083 (≈ 31 DIP) — the panel
is a **docked side panel with a shadow**, not a floating window `[SS:…oFjbQ2DItz]`.

---

## 2. The tool wheel — exact geometry

### 2.1 Radii (the core numbers)

Measured by radial colour profiling from centre (207, 355), 720 angular samples
per radius `[SS:…9I8pRX5lPh]`:

| Ring | Inner r (phys) | Outer r (phys) | Inner (DIP) | Outer (DIP) | **Ratio of R** |
|---|---|---|---|---|---|
| Colour disc (centre) | 0 | 37.5 | 0 | 18.75 | 0 → **0.200** |
| Settings annulus (hub) | 37.5 | 112 | 18.75 | 56 | 0.200 → **0.599** |
| Tools annulus (outer) | 112 | 187 | 56 | 93.5 | 0.599 → **1.000** |

**The proportions are 1 : 3 : 5.** `r_colour = R/5`, `r_hub = 3R/5`, `r_outer = R`.
Implement these ratios, not the pixels.

Consequences worth stating explicitly:
- The two annuli are **the same thickness** (74.5 and 75 phys px).
- The rings are **flush — zero gap** between colour disc, hub and outer ring.
- The outer ring **reaches the outer edge exactly**; there is no inset.

Corroborating measurements: transition from `#0091fb` (disc) to hub grey occurs at
r = 38–40; hub `#262626` holds solid from r = 41 to r = 110; canvas black returns
at r = 113; the outer boundary stroke peaks sharply at **r = 186–188** with
median 187.0 measured independently across 22 of 24 angular buckets.

### 2.2 Outer annulus — 10 sectors of exactly 36°

Detected by scoring every 0.25° for a continuous radial grey line across
r 116–183 `[SS:…9I8pRX5lPh]`. Divider lines found at:

```
0.0°   36.0°   71.9°   107.9°   144.0°   216.0°   324.0°
```

— peak score 134/134 samples at each, widths 0.25–0.75°. (180°, 252° and 288° are
occluded by the undo/redo glyphs and the active sector respectively.)

**Conclusion: 10 sectors × 36°, boundaries on the 36° grid at 0/36/…/324, sector
centres at 18/54/90/126/162/198/234/270/306/342.** One sector is centred exactly
at 12 o'clock (270°).

**Slot allocation** `[MAN:p77, p81, p84]`: **eight configurable tool slots, plus a
fixed undo and redo**. The manual is explicit — "The outer ring has up to eight
configurable tool slots" `[MAN:p81]`; "Eight configurable tools, along with undo
and redo" `[MAN:p77]`; "At the side of your Tool Wheel, you will find the undo and
redo buttons" `[MAN:p84]`. In the capture, undo and redo occupy the two
**left-hand** sectors (centres 162° and 198°), rendering as solid arrow glyphs.

> **This corrects `[V2] §1.2`,** which lists 10 freely-customisable slots with undo
> and redo as items 8 and 9. The count is right; the *customisability* is not —
> undo/redo are fixed and sit at the 9-o'clock pair. Recommend matching Concepts.

An **empty slot** renders as a dim grey `+` glyph on the transparent ring
(visible at 234° in `[SS:…9I8pRX5lPh]`), not as a hidden or removed sector. This
matches `[V2]`'s "transparent, not hidden, not greyed" intent for unavailable
slots, but note Concepts uses it for *empty* slots specifically.

### 2.3 Inner annulus — 3 sectors of 120°

Label positions measured from the hub `[SS:…9I8pRX5lPh]`:

| Setting | Icon position (angle from centre) | Sector span (screen CW) | Sector centre |
|---|---|---|---|
| **Size** | top, ≈ 270° | 210° → 330° | **270°** |
| **Opacity** | lower-right, ≈ 30° | 330° → 90° | **30°** |
| **Smoothing** | lower-left, ≈ 150° | 90° → 210° | **150°** |

Boundaries at **90°, 210°, 330°**.

`[V2] §1.1` mapping (its convention is CCW, y-up):

| `[V2]` says | = screen CW | Sector | Verdict |
|---|---|---|---|
| Size `+30° → +150°` | 330° → 210° (through 270°) | top | ✅ correct |
| Opacity `+30° → −90°` | 330° → 90° (through 30°) | lower-right | ✅ correct |
| Smoothness `+150° → −90°` | 210° → 90° (through 150°) | lower-left | ✅ correct |

`[WEB:components/RadialDial.jsx]` uses `-150→-30`, `-30→90`, `90→210` — also
identical. **All three sources agree; do not change this.**

Hub content layout (physical px from centre, `[SS:…9I8pRX5lPh]`):
- Size: icon (three stacked bars) + value text **"512 px"** on one baseline at
  r ≈ 75, top of hub. This is the only setting whose value is rendered *with
  units and inline with its icon*.
- Smoothing: squiggle icon at r ≈ 76, angle ≈ 172°; value "10%" below-left at
  r ≈ 78.5, angle ≈ 127°.
- Opacity: half-filled-circle icon at r ≈ 71, angle ≈ 10°; value "100%"
  below-right at r ≈ 78.5, angle ≈ 53°.

### 2.4 Per-tool colour arcs — a detail most reimplementations miss

Every tool sector draws **a thin arc of that tool's own colour** hugging the
outside of the hub.

| Property | Physical px | DIP | Ratio of R |
|---|---|---|---|
| Inner radius | 112.5 | 56.25 | 0.601 |
| Outer radius | 116.5 | 58.25 | 0.623 |
| Thickness | 4.0 | **2.0** | 0.021 |
| Angular span | full 36° of the sector | | |

Measured evidence `[SS:tdKKCawzwe]`, radial scan along 306°:
`r=111.5 #252525 → r=112.0 #2c4937 → r=113.5 #46c676 → r=117.0 #06100a`.
Angular scan at r = 115 recovers the lavender arc at **72.0°–107.2°** — exactly
the 72°–108° sector — filled `#a783b5`, and the green arc over 288°–324° filled
`#46c676`. Both match their sector bounds to within measurement error.

Arcs are only visible for tools whose colour contrasts with the hub; white- and
black-inked tools produce arcs that read as part of the chrome.

### 2.5 Resting vs interacting states

**Active tool sector.** Measured on two different captures with two different
active tools:

| Capture | Active slot centre | Measured span | Fill sampled | Tool's colour (centre disc) |
|---|---|---|---|---|
| `[SS:…9I8pRX5lPh]` | 270° | 249.4°–290.6° = **41.2°** | `#0091fb` | `#0091fb` |
| `[SS:tdKKCawzwe]` | 54° | 33.5°–74.5° = **41.0°** | `#ffffff` | `#ffffff` |

Two findings, both important:

1. **The active sector is filled with the ACTIVE TOOL'S OWN COLOUR**, not a fixed
   system accent. The fill always equals the centre colour disc. When the tool's
   colour is white, the sector is white and its icon inverts to black
   `[SS:tdKKCawzwe]`.
2. **The active sector is drawn ≈ 41°, i.e. expanded ≈ 2.5° beyond its 36°
   bounds on each side.** The span is constant across all radii (measured at
   r = 118/130/145/160/175/184 → 41.0, 40.8, 41.1, 41.2, 41.0, 40.8), so this is a
   true angular expansion, not a stroke. Radial extent is unchanged
   (r 114 → 188, same as the ring).

**Unsupported settings are dimmed.** In `[SS:tdKKCawzwe]` the active tool supports
neither opacity nor smoothing: both icons render dim grey and **their value
labels disappear entirely**. Only the size row ("3840 px") remains. This confirms
and refines `[V2] §1.1`'s "grey that arc out" — Concepts greys the *icon* and
*removes the number*, it does not grey a filled arc.

**Tool-specific bottom bar.** `[SS:tdKKCawzwe]` shows a row of three controls
centred at the bottom of the window (y ≈ 1188 phys / 594 DIP): `🔒 Ignore` ·
`☰ All` · `⊘ Ignore`. Bare text + icon, **no background card**, matching §1.1.
This is Concepts' equivalent of `[V2] §1.3`'s selection-tool rectangle — but note
it is chrome-less and the labels are value-style ("Ignore"/"All"), not toggles.

**Precision rows dim contextually.** In the same capture, "Guide" and
"Recognition" render dim because the active tool cannot use them.

### 2.6 Ring translucency

The outer annulus is **not opaque**. Measured by comparing dot-grid pixels inside
and outside the ring `[SS:…9I8pRX5lPh]`:

| Region | Dominant dot colour | Mean luminance |
|---|---|---|
| Outside wheel (r 210–330) | `#3f3f3f` (n=1140) | 51.2 |
| Inside outer annulus (r 120–180) | `#252525` / `#1d2022` | 34.3 |

Dots remain visible through the ring but are darkened; `1 − 37/63 ≈ 0.41`, so the
ring behaves as **black at ≈ 40 % opacity** over the canvas **(inferred from the
dot attenuation — I could not isolate it against a light background)**.

The hub, by contrast, samples a flat `#262626` at every radius from 41 to 110 with
no dot bleed-through — **opaque**.

### 2.7 Motion timings — HONEST UNKNOWN

**I could not measure any Concepts animation timing.** Static screenshots cannot
yield durations, and I did not obtain control of the running app (§9).

The only concrete timings available are from the user's own web implementation,
which they describe as "fully dialled in" — **cite these as the web reference, not
as Concepts' values** `[WEB:index.css]`:

| Animation | Duration | Easing | Notes |
|---|---|---|---|
| Colour-wheel open ("gravity drop") | **160 ms** | `cubic-bezier(0.16, 1, 0.3, 1)` | `scale3d(0.5)→(1)` + fade, origin = dial centre |
| Colour-wheel close | **120 ms** | `cubic-bezier(0.4, 0, 1, 1)` | `scale3d(1)→(0.5)` + fade |
| Generic fade-in (popups, preview circle) | **120 ms** | `cubic-bezier(0.16, 1, 0.3, 1)` | `opacity 0→1`, `scale 0.97→1` |
| Sector fill colour transition | 150 ms | default | `[WEB:RadialDial.jsx]` `transition-colors duration-150` |
| Colour-picker close debounce | 120 ms | — | `[WEB:RadialDial.jsx]` `handleCloseColorPicker` |

Colour-wheel **stagger** `[WEB:ConcentricColorWheel.jsx]`:
- Tier 1 (hue band): delay **0 ms** opening, 60 ms closing
- Tier 2 (grey band): delay **20 ms** opening, 40 ms closing
- Outer family spokes: delay **30 → 120 ms**, distributed by a fixed shuffled rank
  map over 36 columns (`30 + rank/36 × 90`); closing `(35−rank)/36 × 50`

The shuffle is precomputed once at module load, so the scatter pattern is stable
within a session but differs between sessions. Recommend **keeping the stagger,
replacing the random shuffle with a fixed seed** so it is reproducible.

---

## 3. The colour wheels

Invoked by **tapping the colour dot at the centre of the tool wheel**
`[MAN:p137-138]`. Tap+hold opens the Colors menu — **iOS only**, so on Windows
tap+hold has no colour-menu behaviour `[MAN:p84, p144]`.

Three modes: **COPIC** (default), **HSL**, **RGB** `[MAN:p137]`, plus a **Color
Picker** (eyedropper) and a **Star** button to the Colors menu (iOS only)
`[MAN:p139-140]`.

### 3.1 COPIC wheel geometry — measured from the real app

Concentric with the tool wheel, centre (207, 355), measured by luminance-minimum
ring detection over angles 60°–118° `[SS:…ODTfKjJmnK]`:

| Band | Inner r (phys) | Outer r (phys) | Thickness | DIP | Ratio of dial R (187) |
|---|---|---|---|---|---|
| *(empty gap)* | 187 | 421 | 234 | 93.5–210.5 | 1.00 → 2.25 |
| **Ring 1** — tonal value spectrum, true black & white | **421** | **470** | 49 | 210.5–235 | 2.25 → 2.51 |
| *(gap)* | 470 | 485.5 | 15.5 | | |
| **Ring 2** — cool/warm/neutral/tonal greys | **485.5** | **534.5** | 49 | 242.75–267.25 | 2.60 → 2.86 |
| *(gap)* | 534.5 | 550 | 15.5 | | |
| **Rings 3+** — colour families in blending gradients | **551** | 551 + 47·k | **47 each** | 275.5 + 23.5·k | 2.95 → ≈ 5.0 |

Ring identity per `[MAN:p140-141]`: innermost = tonal value spectrum + true black
and white; next = cool, warm, neutral and tonal greys; then the colours in their
blending gradients.

Outer spoke rings measured along three angles `[SS:…ODTfKjJmnK]`: boundaries at
551, 598, 645, 692, 739, 786, 833 (angle 78°, 6 rings) but only 551→693 (3 rings)
at angles 88° and 104° — **spoke depth varies per colour family**, matching the
real COPIC system where families have different member counts. Maximum observed
outer radius ≈ 927.

**The wheel is enormous and deliberately overflows the window** — at ≈ 5× the dial
radius it extends far past the viewport on a 1440×900 display. This is correct
behaviour, not a bug: the user rotates it to reach off-screen colours.

Interaction: "Drag your finger up or down to turn the wheel. Tap a color to set
it to your active brush" `[MAN:p141]`.

### 3.2 Mode buttons and eyedropper placement

Measured from `[SS:…vjujOsR8F7]` (COPIC wheel open), positions relative to the
dial centre:

| Control | Radius (phys) | Angle |
|---|---|---|
| `COPIC` | ≈ 326 | ≈ 0° (due east) |
| `HSL` | ≈ 315 | ≈ 22° |
| `RGB` | ≈ 315 | ≈ 44° |
| Eyedropper | ≈ 299 | ≈ 67° |

So the mode switcher is an **arc of four controls at r ≈ 300–326 phys
(150–163 DIP), spaced ≈ 22° apart, running clockwise from 3 o'clock to ≈ 5
o'clock** — i.e. tucked into the gap between the dial and Ring 1. Labels are plain
text on a small dark rounded chip; the active mode is the one whose wheel is
shown.

> `[SS:…vjujOsR8F7]` is a 1185 × 1323 crop, not a full-screen capture, so treat
> these four figures as ± a few px. The *arrangement* is solid.

### 3.3 HSL and RGB modes

Both are **three concentric curved arc sliders**, not wheels `[MAN:p141-143]`,
`[SS:…MDYvkOQEX6]`, `[SS:…XdqXP5i69k]`.

HSL `[MAN:p142]`:
- **Hue** — the *inner* slider.
- **Saturation** — the slider *closest to the top of the screen*; renders grey at
  one end, pure hue at the other.
- **Lightness** — the third slider, *underneath* Saturation; white → black.

RGB `[MAN:p143]`:
- **Blue** — closest to the top of the screen.
- **Green** — the middle slider.
- **Red** — closest to the bottom.

Both: drag the small circular handles; **tap+hold a value to inline-edit it**
`[MAN:p142, p144]`.

### 3.4 Colour Picker (eyedropper) `[MAN:p154-158]`

- Activated from the eyedropper icon in the colour wheel's inner ring
  `[MAN:p155]`, or via tap+hold on canvas → Selection menu → Color Picker
  `[MAN:p156]`.
- On canvas it renders as a **circle with crosshairs at its centre**. The
  **bottom half of the surrounding ring shows the tool's current colour; the top
  half shows the colour under the picker** `[MAN:p157]`.
- Commit by lifting the stylus/finger `[MAN:p157]`.
- Options: **Alpha On/Off** — with alpha on it samples colour *and* opacity and
  ignores the background; with alpha off it samples at 100 % opacity including
  the background `[MAN:p157]`. **Brush Type** — pulls the stroke's brush
  properties into the active slot `[MAN:p158]`.

---

## 4. Panels

### 4.1 Layers `[MAN:p181-188]`

**Invoked:** layers toggle in the status bar; the icon gains an underline while
the panel is on canvas `[MAN:p93]`. **Where:** left column, directly under the
tool wheel, 16.5 DIP below it. **Chrome:** none.

Internal layout (see §1.5 for pitches):

| Row | Content | Behaviour |
|---|---|---|
| Header | `☰ Layers` | Tap to **minimise the panel** `[MAN:p182]` |
| Sorting | `↕ Sorting │ Manual` | Toggles Automatic ⇄ Manual `[MAN:p182]` |
| New layer | `+ New Layer` | Adds **above the active layer**; accepts a dragged selection to create a layer from it `[MAN:p182]` |
| Layer row | eye · thumbnail · name · opacity % | see below |

Layer row anatomy, measured `[SS:…9I8pRX5lPh]`:
- **Eye icon** at x ≈ 44–68 phys (22–34 DIP), vertically centred on the row.
  Open eye = visible, outline/closed = hidden.
- **Thumbnail** 186 × 114 phys (**93 × 57 DIP**), left edge x ≈ 99 phys.
- **Name** (bold when active) and **opacity %** stacked to the right of the
  thumbnail, x ≈ 205 phys onward.
- **Active layer:** "the thumbnail of your currently active layer has a stronger
  thumbnail outline, and the layer name will have a gray background"
  `[MAN:p182-183]`. Measured: selected row background `#262626` (28 % of the row's
  pixels), thumbnail border white 2–4 px; unselected rows have **no** background
  (`#000000` 84 %).
- A thin **vertical connector line** runs down the left gutter linking the eye
  icons `[SS:…9I8pRX5lPh]`.

Interactions `[MAN:p182-183, p185, p187-188]`:
- Tap non-active layer → activate. Tap **active** layer → open **Layer Options**.
- **Double-tap** → activate *and* enter **Focus Mode**; double-tap the focused
  layer to exit; scrubbing on the eye icons also enters/moves focus. In Focus
  Mode the focused layer is fully visible and the others are subdued `[MAN:p187]`.
- **Tap+hold+drag** a layer to reorder — which forces Sorting to Manual
  `[MAN:p184-185]`.
- Drag a selection onto a layer to move those strokes there `[MAN:p183]`.

**Layer Options menu** `[MAN:p183-184]`: Select All · Lock Layer · Duplicate
Layer · Delete Layer · Merge Down · Rename Layer · Opacity.

Automatic sorting creates a layer per tool; Pen, Fountain Pen, Dynamic Pen, Fixed
Width and Dotted Line **share** one layer, as do Soft and Hard Pencil
`[MAN:p186]`. Free plan caps at **five layers**; Pro is unlimited
`[MAN:p182, p186]`.

### 4.2 Precision `[MAN:p189-190]`

**Invoked:** precision toggle in the status bar. **Where:** left column, below
Layers. **Chrome:** none. Row pitch **40 DIP** (§1.5).

Five rows, each `[checkbox] Label │ value` `[SS:…9I8pRX5lPh]`:

| Row | Value shown in capture | Checkbox state |
|---|---|---|
| Grid | `Dot Grid` | filled ■ (on) |
| Snap | `Options` | empty □ |
| Measure | `1:64 px` | empty □ |
| Guide | `Arc` | empty □ |
| Recognition | `Options` | filled ■ (on) |

The label is bold white, the value is regular and dimmer, separated by a thin
vertical bar. The **checkbox is a small square**, filled when active — measured
≈ 22 × 22 phys (11 DIP) at x ≈ 72–94 phys.

Rows **dim when the active tool cannot use them** — Guide and Recognition are
visibly dimmed in `[SS:tdKKCawzwe]`.

Chapter coverage for the sub-menus: Grids `[MAN:p191-201]`, Snap
`[MAN:p202-208]`, Measure `[MAN:p209-229]`, Shape Guides `[MAN:p230-231]`, Shape
Recognition `[MAN:p232-234]`. Shape recognition is driven from Settings →
Interaction → Draw & Hold, with an activation-time slider; supported shapes are
straight and curved lines, arrows, triangles, squares, rectangles, circles and
ellipses, drawn with one to four strokes `[MAN:p310]`.

### 4.3 Objects `[MAN:p235-262]`

**Invoked:** objects toggle in the status bar `[MAN:p79]`; it "toggles the Objects
menu on canvas" — i.e. it is a **movable canvas element** like Layers and
Precision `[MAN:p90]`, not a modal.

Content: the **Object Library**, choosing between the **Object Market** and **My
Objects** libraries `[MAN:p77]`. Sub-topics: Interface `[MAN:p236-239]`, Using
Objects `[MAN:p240-244]`, Make Your Own Objects `[MAN:p245-255]`, Sharing Object
Packs `[MAN:p256-257]`, **Pexels** stock-image integration `[MAN:p258-262]`.

> **Unknown:** I have no screenshot of the Objects panel open on Windows, so its
> exact width, grid density, thumbnail size and chrome are **not measured**. Do
> not invent them — capture it before implementing. See §9.

### 4.4 Settings `[MAN:p296-313]`

**Invoked:** gear icon in the status bar `[MAN:p296]`. **Where:** docked right,
full height below the title bar, **398.5 DIP wide**, background `#030303` (§1.6).

Header row `[SS:…ODTfKjJmnK]`:
- **✕ close** upper-left (x ≈ 2130 phys)
- **Two tabs: `Workspace` │ `Interaction`**, left-aligned after the close button;
  the active tab is **bold white with a full-width underline**, the inactive one
  is regular grey and un-underlined.
- **ⓘ info** upper-right (x ≈ 2790 phys)
- A short **horizontal drag handle centred at the very top** of the panel
  (measured x ≈ 2455–2530 phys, y ≈ 205 phys) — a rounded 2-px bar.

This matches `[V2] §2`'s floating-window description (close upper-left, info
upper-right, drag bar top-centre, category row below) **except that in Concepts
for Windows it is docked to the right edge, not floating**.

#### Workspace tab `[MAN:p296-301]`

Collapsible sections, each with a **chevron** at the right of its title
`[SS:…ODTfKjJmnK]`:

1. **Canvas**
   - **Background** — "Standard paper or custom background color?" Ten options
     including textured papers, transparent, blueprint and darkprint, plus custom
     colour `[MAN:p296]`. Rendered as a **horizontal scrolling row of large
     circles with captions beneath**: Custom Color · Plain White · Transparent
     (checkerboard) · Crumpled · Light… — measured circle Ø ≈ 132 phys
     (**66 DIP**), pitch ≈ 160 phys (80 DIP). An **"Edit Color"** blue text link
     sits right-aligned on the section header row.
   - **Grid Type** — "You can quickly toggle the grid in the Precision or Layers
     menus." Same circular-option row: No Grid · **Dot Grid** · Graph Paper ·
     Lined Paper · Iso… `[MAN:p297]`. **"Edit Grid"** blue link right-aligned.
     Full grid list: Dot, Graph Paper, Lined Paper, Isometric, Triangle, and 1-, 2-
     and 3-point Perspective `[MAN:p297]`.
2. **Artboard** `[MAN:p297-298]`
   - "Set a reference frame for easier exports."
   - `W :` and `H :` numeric fields (showing `∞`), plus a **swap/rotate** button
     to the right.
   - Preset chips: `Infinite` (selected — dark filled chip) · `1024x768` · `A4` ·
     `1080p` · `…` (three dots = more presets `[MAN:p298]`).
3. **Measurements** `[MAN:p298-300]`
   - **Drawing Scale** — "Define how objects on screen compare to real life."
     Two value fields `1 px` : `64 px`, then chips `1:1` · `1:10` · **`1:64`**
     (selected) · `1:100` · `…` `[SS:WX05SCbfIg]`.
   - **Units** — "Any units displayed or entered on canvas will be converted to
     this system." Three text tabs **Digital │ Metric │ Imperial** (active one
     bold + underlined), then a row of circular buttons: for Digital, `px` and
     `pts` `[SS:WX05SCbfIg]`. Full list `[MAN:p299]`: Digital = Pixels, Points;
     Metric = Automatic scale, Millimeters, Centimeters, Meters, Kilometers;
     Imperial = Automatic scale, Inches, Feet, Yards, Miles.
   - **Display Format & Precision** — "Select your preferred notation." Four
     circular buttons showing live examples with captions beneath: `6.5 pixels`
     /Full · `6.5 px` /**Abbreviated** (selected) · `6` /Rounded · `6.0` /Tenths
     `[SS:…oFjbQ2DItz]`. Measured circle Ø ≈ 104 phys (**52 DIP**).
   - Two toggle rows `[MAN:p300]`: "Show stroke length on the right side when
     drawing" and "Show scale in the status bar for selections".
4. **Tool Setup** `[MAN:p301]`
   - **Interface** — "Choose your preferred tool palette." Two circular buttons
     with glyphs and captions: **Wheel** (selected, white ring) · **Bar**
     `[SS:…oFjbQ2DItz]`.
   - **Performance** — "Enable Low-Latency Mode" toggle (Android only)
     `[MAN:p301]`.
5. **`Restore Default Settings`** — a blue text link at the bottom, sampled
   **`#389dce`** `[SS:…oFjbQ2DItz]`.

**Selected/unselected circular buttons:** both fill `#1a1a1a`; the *selected* one
adds a **white 2-px ring**. Sampled: unselected "Full" `#1a1a1a` 88 %; selected
"Abbreviated" `#1a1a1a` 86 % with a higher white fraction from the ring
`[SS:…oFjbQ2DItz]`.

**Toggles:**

| State | Track | Knob | Source |
|---|---|---|---|
| Off | **`#363839`** | `#ffffff` | sampled, `[SS:…oFjbQ2DItz]` |
| On | **`#61a29c`** | `#ffffff` | sampled, `[MANSHOT]` |

Knob sits right when on, left when off; pill shape, aspect ≈ 1.7 : 1
(measured 136 × 80 in `[MANSHOT]`'s scale).

> ⚠️ **`[V3] §C` and `§J` specify `#78a19c` for the toggle colour. The real
> Concepts value, sampled from the official manual, is `#61a29c`.** They are
> visually close (both muted teals) but not the same. **Recommendation:** use
> `#61a29c` for fidelity, and flag the discrepancy to the user rather than
> silently overriding their written spec.

#### Interaction tab `[MAN:p301-313]`

- **External Displays** — iOS only `[MAN:p302-305]`; no Windows equivalent.
- **Keyboard & Mouse** `[MAN:p306-307]` — Enable/Disable Keyboard Shortcuts
  toggle; **Edit Shortcuts** opens the full editable list. Tap a field, type the
  combination (Ctrl/Alt/Shift + alphanumeric), ✕ cancels, an arrow beside the
  field restores that shortcut's default, and **Reset Shortcuts** at the bottom
  restores all.
- **Touch Input** `[MAN:p308-311]`
  - **Finger Action**: Do Nothing · Use Active Tool · Pan Canvas · Select ·
    Nudge · Slice · **Configured Tool** `[MAN:p308]`.
    > Note: `[V3] §C` lists "Zoom · Rotate" as finger actions. The manual lists
    > **Configured Tool** instead and does not include Zoom/Rotate. Recommend the
    > manual's list, plus Quill-specific additions if wanted — flagged, not
    > silently changed.
  - **Two Fingers**: separate toggles to disable Canvas Rotation and Canvas Zoom
    `[MAN:p308]`.
  - **Tap + Hold**: action chooser plus an **activation-time slider**; plus
    "Highlight selection" and "Drag & Drop active selections" toggles
    `[MAN:p309]`.
  - **Two-, three- and four-finger tap**, each choosing from: do nothing, undo,
    redo, select last item, show Layers, show Color Wheel, Tool Setup, show
    Objects, toggle Shape Guide, toggle Interface, toggle canvas rotation, toggle
    canvas zoom, select all `[MAN:p310]`.
  - **Draw & Hold**: Shape Recognition on/off + activation-time slider
    `[MAN:p310]`.
  - **Left or Right Handed** — iOS only `[MAN:p311]`.
- **Stylus** `[MAN:p312-313]` — Pressure Response sliders · Pressure · Tilt ·
  Tap & Hold · **Enable Artboard Drag** (Windows & Android only) · Barrel Roll
  (iOS) · Hover Brush Previews (iOS). **Shortcut Buttons** configurable to: No
  Action, Undo, Redo, Select Last Item, Show Layers, Show Color Wheel, Tool
  Setup, Show Objects `[MAN:p100]` — and on Windows **the right mouse button**
  can be bound the same way `[MAN:p313]`. **Eraser** action is configurable and
  is shared with the keyboard `E` shortcut `[MAN:p313]`.

### 4.5 Export `[MAN:p275-287, p292-295]`

**Invoked:** export icon (⤒) in the status bar `[MAN:p276]`.

> **Unknown:** I have no screenshot of the Windows Export panel, so its geometry
> is **not measured**. The structure below is from the manual and is reliable;
> the *layout* is not. See §9.

Sections, in order `[MAN:p277-279]`:

1. **Format** — JPG (lossy raster) · PNG (lossless raster) · SVG (simplified
   vector) · DXF (CAD vector) · **.concept / .concepts** (native) · PSD
   (layered raster) · PDF Flattened (raster) · PDF Vector Paths.
   Per-format notes at `[MAN:p286-287]`; a one-line description accompanies the
   selection.
2. **Region** — **Screenshot** (visible area) · **Entire Drawing** ·
   **Artboard** (only if an artboard is set; named for its size, e.g. A3) ·
   **PDF Bounds** (only with imported PDFs) · **Selection** (and when a selection
   is active this is *the only* available option) `[MAN:p278, p294]`.
   > This confirms `[V3] §E`'s "Region is dynamic": Concepts genuinely varies the
   > chip set by document state. The dynamic chip is **Artboard**, labelled with
   > the artboard's size.
3. **Options** — Include Background · Include Grid (only available if the grid is
   active in Precision) · Use Filters (SVG only) · Wireframe (DXF only) ·
   Visible Layers Only (PSD only) · Original Pages (PDF Bounds only)
   `[MAN:p278-279]`. For PDF specifically: Transparent · Paper · Grid · Original
   Pages `[MAN:p294]`.
4. **Details** — for Entire Drawing / Artboard: **72, 150, 300, 600 ppi**. For
   Screenshot or a pixel-measured Artboard: **100 %, 200 %, 400 %**
   `[MAN:p279, p295]`.
5. **Export** button.

**Post-export flow** `[MAN:p280-281]`: a **preview window** appears; the user must
tap **Share/Save** or drag the preview out, otherwise *the file is not written*.
"Back to Export" or the **✕ in the top-left corner** cancels — corroborating the
close-upper-left convention in `[V2] §2` and `[V3] §J`.

Windows note: native `.concepts` export is reached via **Save As**, not the
export menu `[MAN:p287]`. iOS writes `.concept`, Windows/Android `.concepts`, and
they are **not yet interchangeable** `[MAN:p287]`.

### 4.6 Import `[MAN:p263-274]`

**Invoked:** import icon (⤓) in the status bar, or from the Gallery
`[MAN:p263]`.

Opens a small menu with `[MAN:p264]`:
- **Photos** — *not available on Windows*
- **Files**
- **Paste from Clipboard** (also `Ctrl+V` `[MAN:p272]`)
- **Take a Picture**
- **Recently Imported** — iOS only

Accepted formats: JPG, PNG, PSD, PDF, `.concept`/`.concepts` `[MAN:p263]`.
Imports arrive **to scale** and **pre-selected** so they can be positioned before
placing; tap outside the bounding box to commit `[MAN:p265]`. With Automatic
layer sorting an **Image layer is created at the bottom of the layer list**; with
Manual sorting the image lands on the active layer `[MAN:p265]`.

Multi-page PDFs open **a scrollable page menu at the side of the screen**; drag or
tap pages onto the canvas; swipe the menu off-screen to hide it and restore it
from the selection menu `[MAN:p290]`.

This matches `[V3] §E`'s "Import is a simple dropdown: files · paste from
clipboard · take a picture" — Concepts' Windows set is exactly those three.

### 4.7 Measurement popup (zoom / rotation)

**Invoked:** tap the zoom or angle value in the status bar; tap+hold to inline-edit
`[MAN:p79, p95-96]`. **Where:** directly below the status-bar readout, right side.
**Chrome: none** — bare text on canvas `[SS:…JZuvjQFMmA]`.

Measured rows (physical px, left edge x ≈ 2238):

| Row | y band | Height |
|---|---|---|
| Title "Measurement" + ⓘ | 256–289 | 34 |
| "Zoom" label | 343–360 | 18 |
| 🔍 `100%` 🔒 | 390–420 | 31 |
| chips `10%` `100%` `250%` `1600%` | 465–484 | 20 |
| "Rotation" label | 538–557 | 20 |
| ↻ `0°` 🔒 | 586–617 | 32 |
| chips `0°` `90°` `180°` `270°` | 662–681 | 20 |

The selected chip has a **dark rounded-rect background**; others are bare text.
Each of Zoom and Rotation has **its own padlock** to freeze that value —
confirming `[V3] §B`'s "zoom and tilt readouts with a lock". Note Concepts uses
**two independent locks**, one per value, not a single shared lock.

Double-tapping the zoom field returns to canvas centre `[MAN:p96]`.

---

## 5. Colour and type

### 5.1 Sampled colours

All values below were read from pixels with PIL. **Sampled** = read directly;
**inferred** = derived.

| Token | Hex | Source | Confidence |
|---|---|---|---|
| Canvas background (dark theme) | `#000000` | `[SS:…9I8pRX5lPh]` | sampled |
| Dot grid | `#3f3f3f` | `[SS:…9I8pRX5lPh]` | sampled |
| Title bar background | `#1e2025` (≈ `#1f2023`) | `[SS:…9I8pRX5lPh]` | sampled |
| Status bar background | *none — transparent* | `[SS:…9I8pRX5lPh]` | sampled |
| Wheel hub (settings annulus) | `#262626` | `[SS:…9I8pRX5lPh]` | sampled |
| Wheel outer ring scrim | black @ ≈ 40 % | `[SS:…9I8pRX5lPh]` | **inferred** from dot attenuation `#3f3f3f`→`#252525` |
| Sector divider / outer stroke | `#3f3f3f` | `[SS:…9I8pRX5lPh]` | sampled |
| Active sector fill | **= active tool's colour** | `[SS:…9I8pRX5lPh]` + `[SS:tdKKCawzwe]` | sampled, two cases |
| Selected layer row background | `#262626` | `[SS:…9I8pRX5lPh]` | sampled |
| Unselected layer row | *none — transparent* | `[SS:…9I8pRX5lPh]` | sampled |
| Settings panel background | `#030303` | `[SS:…oFjbQ2DItz]` | sampled |
| Settings circular button fill | `#1a1a1a` | `[SS:…oFjbQ2DItz]` | sampled |
| Settings circular button, selected | `#1a1a1a` + white 2 px ring | `[SS:…oFjbQ2DItz]` | sampled |
| Toggle track, OFF | `#363839` | `[SS:…oFjbQ2DItz]` | sampled |
| Toggle track, ON | **`#61a29c`** | `[MANSHOT]` | sampled |
| Toggle knob | `#ffffff` | both | sampled |
| Link / accent blue | `#389dce` | `[SS:…oFjbQ2DItz]` "Restore Default Settings" | sampled |
| Primary text | `#ffffff` | all | sampled |
| Example tool colours (for arcs) | `#0091fb`, `#46c676`, `#a783b5` | `[SS:…9I8pRX5lPh]` | sampled (user's own tool colours, not brand tokens) |

**Not sampled — no source available:**
- Export panel's primary button. `[V3] §E/§J` specifies `#3282aa`; I have **no
  Concepts screenshot of the export panel** to verify it. Treat `#3282aa` as the
  user's choice, unverified against Concepts.
- Any light-theme value. Every capture is dark theme.
- Hover and pressed colours (§9).

### 5.2 Type hierarchy

Measured glyph heights, physical px `[SS:…9I8pRX5lPh]`, `[SS:…JZuvjQFMmA]`.
Font-size figures are **inferred** from cap/ascender heights at a typical 0.7
cap-height ratio; the *relative hierarchy* is measured and reliable.

| Role | Measured glyph height (phys) | ≈ DIP font size (inferred) | Weight |
|---|---|---|---|
| Panel section title ("Measurement", "Artboard", "Canvas") | 34 | **24** | Bold |
| Panel sub-heading ("Background", "Artboard Size") | ~20 | **15** | Semibold |
| Menu header ("Layers") | 25 (incl. descender) | **18** | Semibold |
| Menu header ("Precision") | 21 | **17** | Semibold |
| Page name ("Drawing") | 27 (incl. descender) | **18** | Semibold |
| Body / row label ("Grid", "Snap") | 24–30 | **15** | Semibold for label, Regular for value |
| Status bar readouts ("100%", "0°") | 17 | **14** | Regular |
| Layer name / opacity | ~19 | **14** | Bold when active, Regular otherwise |
| Wheel hub value ("512 px") | ~22 | **15** | Semibold |
| Wheel sector size numbers ("1280", "4352") | ~17 | **11** | Semibold |
| Chip labels ("1:64", "100%") | ~20 | **14** | Regular; Semibold when selected |

Face: the UI uses the Windows system UI font (Segoe UI Variable) — **inferred**
from glyph shapes; I did not extract font metadata.

**Hierarchy rule:** label/value pairs are consistently *semibold white label* +
*regular dimmer value*, separated by a thin `│`. This is used in Precision
("Grid │ Dot Grid"), Layers ("Sorting │ Manual") and the wheel.

---

## 6. Interaction

### 6.1 Gestures `[MAN:p84, p90, p94-96, p308-310]`

| Gesture | Effect |
|---|---|
| Tap tool sector | Activate that tool |
| Tap **active** tool sector again, or double-tap an inactive one | Open the **Brush menu** for that slot `[MAN:p81]` |
| Tap a setting arc | Open its slider + presets `[MAN:p83]` |
| **Tap+hold+slide on a setting** | Slider opens *as you slide* and closes the instant you lift — the "in flow" adjustment `[MAN:p83]` |
| Tap+hold a preset value | Manually type a value `[MAN:p83]` |
| Tap centre colour disc | Open the colour wheel `[MAN:p84]` |
| Tap+hold centre colour disc | Colors menu — **iOS only** `[MAN:p84]` |
| Pinch on the wheel / **scroll wheel over it** | Scale the wheel `[MAN:p90]` |
| Tap+hold+drag the wheel | Relocate it; drop on the centre layout manager → **Tool Bar** `[MAN:p87]` |
| Two-finger drag | Pan; two-finger pinch = zoom; two-finger twist = rotate `[MAN:p94-96]` |
| Two-finger tap | Undo (default) `[MAN:p84]` |
| Three-finger tap | Redo (default) `[MAN:p84]` |
| Swipe outward on Layers/Precision labels | → compact mode → hidden mode `[MAN:p91-92]` |

### 6.2 Keyboard `[MAN:p84, p95-96, p306-307, p313]`

| Key | Action |
|---|---|
| `Ctrl+Z` / `Ctrl+Shift+Z` | Undo / Redo |
| `Ctrl+V` | Paste from clipboard (import) |
| `Space` | Pan mode — press to toggle, or **hold** to pan and release to return |
| `S` | Zoom mode — same toggle-or-hold semantics; drag right zooms in, left out |
| `R` | Rotation mode — same toggle-or-hold semantics |
| `E` | Eraser, using the configured eraser action |

All shortcuts are **user-rebindable** and can be globally disabled
`[MAN:p306-307]`.

### 6.3 UI density modes `[MAN:p90-93]`

Three modes, a genuinely distinctive Concepts feature:

1. **Normal** — all menus visible, wheel fully on canvas.
2. **Compact** — entered by swiping outward on Layers/Precision, or cornering the
   wheel. Labels collapse to icons; the wheel **docks to the corner**; size,
   opacity and smoothing sliders are hidden from the Tool Bar.
3. **Hidden** — swipe outward again. All menus hide. **Tap the Concepts icon on
   the canvas to bring them back**, or bind a gesture to toggle hidden mode.

Additionally, each of Precision, Layers and Objects can be independently shown or
hidden from the status-bar toggles `[MAN:p93]`.

### 6.4 Modal vs non-modal

| Surface | Modality | Dismissal |
|---|---|---|
| Tool wheel | Non-modal, always present | n/a |
| Setting slider popup | Non-modal | Tap the setting again; or lift after a tap+hold+slide; or tap outside `[MAN:p83]` |
| Colour wheel | **Effectively modal** — covers the canvas | Tap the centre disc again, or tap outside |
| Layers / Precision / Objects | Non-modal canvas elements, movable | Status-bar toggle, or swipe to compact/hidden |
| Measurement popup | Non-modal | Tap elsewhere |
| Settings | Non-modal docked panel | ✕ upper-left |
| Export | Modal (has a blocking preview step) | ✕ upper-left, or "Back to Export" |
| Import | Transient menu | Pick an item or tap away |

In `[WEB:RadialDial.jsx]` the colour wheel dims the whole screen with
`bg-slate-900/30 backdrop-grayscale backdrop-blur-[2px]` and a click on the
backdrop closes it. **Concepts itself does not grey the screen** — in
`[SS:…ODTfKjJmnK]` the status bar behind the open COPIC wheel is dimmed but the
canvas and the Settings panel are *not* greyscaled or blurred. Recommend a plain
dim without the greyscale/blur.

### 6.5 Hover and press — HONEST UNKNOWN

Concepts is touch-first and these captures are all static. **I could not measure
hover or press states.** Do not copy `[WEB:RadialDial.jsx]`'s
`group-hover:fill-[#252c3d]` as though it were a Concepts value — it is the web
author's invention.

Sensible defaults **(inferred, flagged as such)**: lift the sector scrim by
≈ 8 % luminance on hover; on press, use the active-sector treatment at reduced
opacity. Confirm against the live app before shipping.

---

## 7. What Concepts has no equivalent for

Quill is a **notes** app; Concepts is a sketching app. These Quill features have
no Concepts counterpart and must be designed, not copied:

| Quill feature | Concepts equivalent | Recommendation |
|---|---|---|
| Notebook / section / page hierarchy | None — flat Gallery of drawings, with **Folders** `[MAN:p30-34]` | Put notebook+page into its own canvas element in the Layers/Precision family, per `[V3] §B` |
| Page name + date drawn on the page | None | `[V3] §B` already says to remove these when the dial is active — matches Concepts, which shows the name only in the status bar |
| Backgrounds tab | Settings → Workspace → Canvas → Background | `[V3] §B` says remove the tab — correct, it belongs in Settings |
| Paper textures as a first-class feature | Background presets (crumpled, blueprint, darkprint…) | Concepts treats these as *background*, one row of circles |
| `.quill` native format | `.concept`/`.concepts` | `[V3] §E`'s substitution is exactly right |
| Text tool as a notes primitive | Concepts has text but it is not central | `[V2] §1.3`'s above-the-text icon bar has no Concepts analogue — it is a Quill invention and should be labelled as such |
| Light theme | Exists, but I have **no light-theme capture** | Unknown — see §9 |

---

## 8. DIFFERENCES TABLE — Concepts vs Quill, and what must change

Quill's current behaviour is taken from `[V3] §A`'s defect list (the user's own
description of the shipped build) and `[V2]`.

| # | Element | Concepts does | Quill currently does | Must change |
|---|---|---|---|---|
| 1 | **Panel chrome** | Layers, Precision, Objects, Measurement have **no background, border or blur** — bare text on canvas (§1.1) | Floating "liquid glass" cards with rounded corners and borders `[V3] §I` | **Remove all chrome** from these four surfaces. Keep chrome only on the title bar and the Settings panel. This is the highest-impact single change. |
| 2 | **Ring ratios** | `0.2 R` / `0.6 R` / `1.0 R`, rings **flush**, outer ring reaches the edge (§2.1) | Web ref: `0.20 / 0.208–0.631 / 0.646–0.962` — gaps between rings, outer ring inset `[WEB:RadialDial.jsx]` | Set radii to exactly `R/5`, `3R/5`, `R`. Remove the inter-ring gaps and the outer inset. |
| 3 | **Active sector fill** | Filled with **the active tool's own colour**; icon inverts for contrast (§2.5) | Fixed accent `#007bbd` with a `#38bdf8` stroke `[WEB:RadialDial.jsx]` | Bind the active sector fill to the tool's colour. Compute icon colour from fill luminance. |
| 4 | **Active sector shape** | Expanded to **41°** (+2.5° per side), same radii as the ring (§2.5) | Same 36° as inactive, differentiated by stroke `[WEB:RadialDial.jsx]` | Expand the active wedge angularly; drop the stroke. |
| 5 | **Per-tool colour arcs** | Every sector shows a **2 DIP arc of its tool's colour** at r `0.601–0.623 R` (§2.4) | Absent | Add. Small, cheap, and very recognisably Concepts. |
| 6 | **Slot semantics** | **8 configurable + fixed undo/redo** at 162°/198° `[MAN:p77, p81, p84]` | 10 fully-customisable slots incl. undo/redo as items 8–9 `[V2] §1.2` | Fix undo/redo to the 9-o'clock pair; make the other 8 customisable. |
| 7 | **Empty slot** | Dim grey `+` glyph on the transparent ring (§2.2) | "Transparent" sector `[V2] §1.1` | Render a `+` affordance so the slot is discoverable. |
| 8 | **Unsupported setting** | Icon dims **and its value label is removed** (§2.5) | "Grey that arc out" `[V2] §1.1` | Dim the icon and hide the number; do not grey a filled arc. |
| 9 | **Hit testing** | — | **Phantom hover: dial lights up while the pointer is over the page** `[V3] §A.1` | Hit-test against the annulus (`r_in ≤ r ≤ r_out` **and** the sector's angular range), not the wheel's bounding box. This is almost certainly a square-bounds test. |
| 10 | **Tool selection** | Tap a sector to activate `[MAN:p81]` | **Clicking a slot does nothing** `[V3] §A.3` | Wire up sector hit-testing to slot activation. |
| 11 | **Opacity** | Full slider + 4 presets `[MAN:p82]` | **Not implemented** `[V3] §A.4` | Implement. |
| 12 | **Preview circle** | *(unverified — see §9)* | **No preview circle** `[V3] §A.5` | `[V2] §1.1` wants a real ink-drawn circle. `[WEB:RadialDial.jsx]` draws it at `r = clamp(130 + size·0.8, 134, 180)` with a dashed `#38bdf8` outer guide. Implement the user's spec; I could not confirm Concepts' behaviour. |
| 13 | **COPIC wheel reachability** | Tap the centre colour disc `[MAN:p137-138]` | **Unreachable from the centre disc** `[V3] §A.6` | Wire the centre disc to open the wheel. |
| 14 | **COPIC wheel sizing** | Rings at `2.25–2.51 R`, `2.60–2.86 R`, then `47 px` spokes from `2.95 R`; **overflows the window by design** (§3.1) | Web ref rings are proportionally much thinner (`2.28–2.42 R` etc.) `[WEB:ConcentricColorWheel.jsx]` | Thicken the rings to the measured ratios. Do not shrink the wheel to fit. |
| 15 | **COPIC wheel stickiness** | Drag to rotate, release to stop (with momentum) `[MAN:p141]` | **Sticks to the mouse** `[V3] §F` | Release pointer capture on pointer-up. `[WEB:ConcentricColorWheel.jsx]` has correct momentum logic (velocity from a 5-sample history, `×0.94` decay) to copy. |
| 16 | **Icon centring** | Icons centred in their sector | **Off-centre** `[V3] §A.7` | Place at the sector's angular midpoint at `r ≈ 0.78 R`, then offset by half the icon box. The web ref's `translate(x-8, y±3/9)` is an approximation — compute properly. |
| 17 | **Icon consistency** | One icon set | **Dial icons differ from top-bar icons** `[V3] §A.8` | Single shared icon set. |
| 18 | **Tool duplication** | — | Tool appears in both dial and top bar `[V3] §A.9` | Remove from the top bar when present in the dial. |
| 19 | **Dial in gallery** | Wheel exists only over a drawing | **Shows in the notebook gallery** `[V3] §A.2` | Gate on "a page is open". |
| 20 | **Settings placement** | **Docked right, full height, 398.5 DIP, `#030303`** (§1.6) | Floating resizable window `[V2] §2` | Dock it. Keep ✕ upper-left, ⓘ upper-right, drag handle top-centre, tab row below. |
| 21 | **Settings tabs** | `Workspace` │ `Interaction` `[MAN:p296]` | `Settings` · `Interaction` `[V2] §2` | Rename the first tab to **Workspace**. |
| 22 | **Toggle colour** | **`#61a29c`** (sampled, `[MANSHOT]`) | `#78a19c` `[V3] §C, §J` | Recommend `#61a29c`; flag to the user before changing their stated value. |
| 23 | **Toggle OFF track** | `#363839`, white knob (§4.4) | unspecified | Specify. |
| 24 | **Zoom/tilt lock** | **Two independent padlocks**, one per value (§4.7) | "zoom and tilt readouts with a lock" (singular) `[V3] §B` | Two locks. |
| 25 | **Status bar icon pitch** | **42 DIP**, 16 DIP glyphs, 31 DIP edge margins (§1.3) | unspecified | Adopt. |
| 26 | **Active-menu indicator** | **Underline beneath the status-bar icon** when that menu is on canvas `[MAN:p93]` | unspecified | Add. |
| 27 | **Row pitches** | Text rows **40 DIP**; layer rows **61 DIP**; thumbnail **93 × 57 DIP** (§1.5) | unspecified | Adopt. |
| 28 | **Layer active state** | Row bg `#262626` + **thicker white thumbnail outline**; inactive rows fully transparent `[MAN:p182-183]` | unspecified | Adopt both cues. |
| 29 | **Density modes** | **Normal / compact / hidden**, via swipe on Layers/Precision `[MAN:p90-92]` | Single "minimal UI" button `[V2] §4.2` | Consider the three-stage model; at minimum keep an escape back from hidden (Concepts uses an on-canvas icon). |
| 30 | **Wheel ⇄ Bar** | Wheel and Tool Bar are interchangeable; switch by dragging to the centre layout manager or in Settings → Workspace → Tool Setup `[MAN:p87-88, p301]` | Not mentioned | Note as a future feature; the Settings section already exists in the spec. |
| 31 | **Export region chips** | Dynamic: **Artboard** appears only when set and is labelled with its size; **Selection** *replaces* all others when a selection is active `[MAN:p278, p294]` | "last region chip is the current page size" `[V3] §E` | Extend: also collapse to Selection-only when a selection exists. |
| 32 | **Export completion** | A **preview step** is mandatory; leaving without Share/Save silently discards `[MAN:p280]` | Not mentioned | Either implement the preview or write directly — but do not half-implement a preview that discards. |
| 33 | **Finger actions** | Do Nothing · Use Active Tool · Pan Canvas · Select · Nudge · Slice · **Configured Tool** `[MAN:p308]` | includes Zoom · Rotate `[V3] §C` | Flag: the manual has no Zoom/Rotate finger actions. Confirm with the user. |
| 34 | **Right mouse button** | Bindable exactly like a stylus shortcut button `[MAN:p313]` | `[V2] §2` uses right-click for the pen library | Compatible, but make it *configurable* rather than hard-wired. |
| 35 | **Canvas element movability** | Wheel, Precision, Layers and Objects are **all drag-relocatable**, and menus re-orient when the wheel moves side `[MAN:p90, p93]` | Fixed positions | Large feature; defer, but do not architect it out. |

---

## 9. Gaps and honest unknowns

Things I could **not** determine. None of these are guessed at above.

1. **All animation timings in Concepts itself.** §2.7's numbers are the *web
   implementation's*. Concepts' real durations, easings and stagger are unmeasured.
2. **Hover and pressed states.** No capture shows a pointer interaction (§6.5).
3. **The setting-slider popup.** `[MAN:p82-83]` describes four presets + a slider,
   but I have **no Windows screenshot of it open**, so its position relative to the
   wheel, size, and styling are unknown. `[WEB:RadialDial.jsx]` places it at
   `left:175px, top:75px, width:270px` — that is the web author's choice, not
   Concepts'.
4. **The Objects panel** — never captured open (§4.3).
5. **The Export panel layout** — structure known from the manual, geometry unknown
   (§4.5). In particular `#3282aa` for the export button is **unverified**.
6. **The Import menu's rendered appearance** — item list known, layout unknown.
7. **The Brush menu / brush library**, reached by tapping an active tool again
   `[MAN:p81, p103-136]` — no capture; this is a large surface and `[V3] §G` rates
   the pen library ⭐⭐⭐⭐⭐, so **capture it before building**.
8. **Light theme.** Every source capture is dark. All colours in §5.1 are
   dark-theme only.
9. **Whether the wheel's outer ring scrim is a flat alpha or a gradient/vignette.**
   The 40 % figure is inferred from dot attenuation against black only.
10. **The exact typeface and font sizes.** Sizes in §5.2 are inferred from glyph
    heights; the hierarchy is measured but the absolute pt values are not.
11. **Compact and hidden mode appearance** — described in `[MAN:p91-92]` but the
    manual's own illustrations were not extractable as text and I have no capture.
12. **Concepts' preview-circle behaviour while scrubbing a setting** — `[V2]` wants
    a real ink-drawn circle; I could not confirm what Concepts actually draws.

### 9.1 Why these remain unknown

I attempted to drive the running Concepts app (it was open throughout,
`ApplicationFrameHost` PID 18552, `TopHatchInc.Concepts` v2026.6.4.0) to capture
the missing states. **The user was continuously active at the machine** — idle
time polled at 46 s, 9 s, 0 s, 28 s, 43 s, 4 s, 0 s and 0 s across the session,
with the foreground window changing between polls. Per the standing rule to yield
when the user is working, **I did not take control and I changed nothing on their
machine.**

### 9.2 Exactly what to capture to close these gaps

A short session, in this order, would close 1–8 and 11–12:

1. Open a scratch drawing. Tap each of the three setting arcs → screenshot the
   slider popup (closes #3).
2. Tap an active tool a second time → screenshot the Brush menu (closes #7).
3. Objects toggle → screenshot (closes #4).
4. Export icon → screenshot each of the four sections (closes #5).
5. Import icon → screenshot the menu (closes #6).
6. Hover the pointer over an inactive sector and over a status-bar icon; capture
   both (closes #2).
7. Swipe Layers outward twice → capture compact and hidden modes (closes #11).
8. Begin a size scrub and capture mid-gesture (closes #12).
9. Settings → Workspace → Background → a light paper, then re-capture the wheel
   (closes #8 and #9).
10. Screen-record the colour wheel opening at 60 fps (closes #1).

Save into
`C:\Users\irony\AppData\Local\Temp\claude\C--Users-irony\5d0bc6f7-2eaf-4e19-afbf-f5efd33b5de9\scratchpad\concepts-ref\`
and record the window size, since every measurement here is anchored to
2880 × 1800 physical / 1440 × 900 DIP.

---

## 10. Quick-reference implementation constants

Everything an implementer needs, in DIP, in one place.

```
DISPLAY BASIS      1440 x 900 DIP  (captures 2880 x 1800 @ 200%)

TITLE BAR          height 32,  bg #1e2025
STATUS BAR         transparent; content centre y=63; icon glyph 16;
                   icon pitch 42; edge margin 31

TOOL WHEEL         centre (103.5, 177.5);  R = 93.5
  colour disc      0      -> 0.200 R   (18.75)
  settings hub     0.200  -> 0.599 R   (18.75 -> 56)   fill #262626 opaque
  tool ring        0.599  -> 1.000 R   (56 -> 93.5)    black @ ~40% (inferred)
  tool colour arc  0.601  -> 0.623 R   (56.25 -> 58.25), 2 thick, full 36 deg
  outer stroke     #3f3f3f at R
  dividers         #3f3f3f, boundaries at 0/36/72/.../324 deg
  sector centres   18/54/90/126/162/198/234/270/306/342 deg
  undo, redo       fixed at 162 deg and 198 deg
  active sector    fill = TOOL COLOUR, span 41 deg (36 + 2.5 each side)
  settings arcs    size 210->330 (centre 270); opacity 330->90 (centre 30);
                   smoothing 90->210 (centre 150)
  icon radius      ~0.78 R

LEFT COLUMN        icon margin 22; text margin 45
  text row pitch   40
  layer row pitch  61
  layer thumbnail  93 x 57  (canvas aspect)
  selected layer   bg #262626 + white 2px thumbnail outline

SETTINGS PANEL     docked right, width 398.5, full height, bg #030303
  circular button  d 52 (small) / 66 (background+grid), fill #1a1a1a,
                   selected adds white 2px ring
  toggle           off #363839 / on #61a29c, knob #ffffff, aspect 1.7:1
  link             #389dce

CANVAS             bg #000000, dot grid #3f3f3f
```

---

*End of reference. Every measurement traceable; every gap flagged.*
