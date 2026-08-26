# Visual verification pass — resume state

## RUN OF 2026-08-26 (third screen run) — `integration` @ `4d88688`

Rebuilt clean with the given command, 0 warnings, binary of 19:42. Scratch
`QUILL_DATA_FOLDER` at `scratchpad/vp3data`, copied from the second run's
`qdata` so that folder survives as evidence. Machine unlocked, 186 s idle at
start; `Q-Ensure`/`Q-Assert` bracketed every injection and grab. The user's real
library was **53,582,382 bytes, mtime 2026-08-24 19:48:26 UTC, SHA-256
`ADD8EA24…35B20A0` before and after** — unchanged, and this run never opened it.

### §16.8 — THE MECHANISM IS FOUND. The suspected one is REFUTED.

The second run's suspicion was that `PenBar.Place()` puts the satellites just
below a host that renders as a shallow horizontal strip, so they are clipped out
of existence. **That is not what is happening. `Place()` is fine and nothing is
clipped.**

**What is actually happening: under the Bar surface, `PenBar` is never shown at
all.** `MainWindow.xaml.cs:7628` gates it behind a *second* flag:

```csharp
bool conceptsBar    = surfaceOn && bar &&  _library.ConceptsBarPalette;
bool legacyIsSurface= surfaceOn && bar && !_library.ConceptsBarPalette;
...
_penBar?.SetVisible(conceptsBar);
```

`ConceptsBarPalette` is a plain `bool` on the library, **default `false`**, with
**no Settings UI anywhere** (it appears in exactly two files: its declaration and
this call site). `NoteModels.cs:672` says so out loud: *"'Bar' means the ORIGINAL
horizontal pen row… it is simply one flag further in, so it can never be what a
user sees by default."*

So with Bar selected, what a user gets is the **legacy `PenRow`**
(`MainWindow.xaml:747`), and `PenRow`'s subtree contains `PenScroll`, `PenStack`,
`PenGrip`, `PresetPanel`, `PenRowColourBtn`, `BtnAddPreset`, `BtnPenRowCollapse`
— **no undo and no redo**, which is exactly what the pre-ruling comment at
`MainWindow.xaml.cs:9667` correctly said before it was overwritten.

**Observed live, both branches, same build, same page:**

* **`ConceptsBarPalette = false` (the default, and the only state a user can
  reach).** `vp3/07-bar-surface.png`. The Bar surface is a shallow horizontal
  preset strip in the **top-bar band** — which is precisely the thing the second
  run magnified under and reasoned from. Magnified at 2x
  (`vp3/07-penrow.png`): grip, nine pen presets, colour dot, `+`, collapse
  chevron, and **nothing else**; below it, the strip's own bottom border and the
  page. Top bar at 2x (`vp3/07-topbar-left.png`): Quill, hamburger, breadcrumb,
  Pen, Text, Lasso, Insert — **no undo, no redo.** **No pointer route to undo.**
* **`ConceptsBarPalette = true`** (flipped in the scratch `library.json`, app
  restarted). `vp3/08-conceptsbar.png`. `PenBar` appears as a **tall vertical
  left dock** with its settings panel docked to its right — and **both
  satellites are on screen**, centred under the bar's axis about 6 DIP below its
  bottom edge, exactly where `Place()` puts them. At 4x
  (`vp3/08-satellites.png`) they are two clean curved arrows, undo and its
  mirror, **fully visible and not clipped at any edge**, drawn dim because the
  freshly-loaded page has an empty stack — `PaintSatellite`'s documented
  "never hidden, unavailable at 30 %".

**So `Place()` is correct and the satellites work.** The premise the ruling rests
on — "PenBar floats them below the panel as bare satellites" — is *true*, but
only of a control that is unreachable from the UI. The ruling removed the
top-bar pair for a replacement the user cannot switch on.

**The stranding is unconditional, not a corner case.** `Set(BtnUndo, "BtnUndo",
!surfaceCarriesUndo)` passes `inContext: false`, and `Set` collapses on
`inContext == false` with no other path — so `BtnUndo`/`BtnRedo` are
**permanently collapsed in the top bar in every configuration this build can be
put into**. The comment's safety argument ("whichever surface is up is carrying
the pair") is false for the one surface a user can actually select.

*Not fixed — this is a verification pass, and it is left exactly as found.*

### §17.14 / §16.5 the dial readouts against the arrows — **PASS, measured**

Measured off the hub at 6x, physical px on the 2x display, hub centre taken from
the disc extent at (285, 365).

*Enabled* (Pen, nothing selected) — `vp3/10-opacity-vs-redo.png`:

| | measured (DIP) | §16.5 |
|---|---|---|
| opacity value ink `100%` | x 25.0..47.0, y 3.5..11.0 | x 23.99..46.49, y 0.40..12.40 |
| redo arrow **ink** | x 9.0..24.5, y 32.0..47.0 | BOX x 5.42..26.42, y 28.15..49.15 |

The arrow ink sits **inside** its box on all four sides, and its top edge at
32.0 DIP lands on §16.5's own derived figure of 28.15 + 4.11 = **32.26** — the
"`UndoRound`'s ink starts 4.11 DIP down a 21 DIP box" sentence, confirmed on
screen to a quarter of a DIP.

* **value ink → arrow ink, vertical: 21.0 DIP** (spec 19.86).
* Horizontally the value ink starts at 25.0 and the arrow **ink** ends at 24.5 —
  they **just miss**. Against the arrow **BOX** (26.42) they overlap by 1.42 DIP,
  which is §16.5's 2.42 to within a DIP. So the spec's "they do overlap
  horizontally" is a box-vs-ink statement and it still reads that way: **a
  stacked pair, and the vertical figure is the whole clearance.** Up-and-outward,
  clearing the arrows. **PASS.**

*Disabled* — **§17.14 holds on all three readouts, no dash anywhere.**

* **Eraser** (`vp3/11-dial.png`): size stays live and reads `auto`; **opacity and
  stability lose their values entirely** — no `-`, no empty box out on the rim —
  and each glyph re-centres in its own section. Measured, the two disabled
  glyphs sit at **dx = ±38 DIP, dy = 0** — symmetric, and on the hub's own
  horizontal axis, where enabled they sat at dy ≈ −12.5. That is
  `LayoutReadouts`' `SectionMid` branch doing exactly what it says.
* **Text** (`vp3/12-hub.png`): **all three** disabled. The size row loses
  `3.5 px` too and its hamburger glyph centres horizontally. No dash on any of
  the three. All three glyphs render muted.

### §17.2 corner plates — **SPLIT. The discontinuity PASSES; the page-coloured ground FAILS.**

**The half that works, and works well.** On a **graph-paper** page
(`vp3/18-corner2.png`) and then on a **2-point perspective** grid
(`vp3/19-corner-persp.png`, reached by accident and much the better test) every
grid line — vertical, horizontal and oblique — runs up to a plate and **stops
dead at its edge**. Not one line continues across any plate. Same on the
**Brown Paper** page's isometric lattice and its paper texture
(`vp3/24-corner.png`). The stadium/circle split is also exactly as specified:
**`100%` and `0°` are rounded-end capsules sized to their text; the other five
corner buttons are circles.**

**The half that does not.** §17.2 opens with *"a background that mimics the page
colour, so the button almost disappears into the page"*. Measured plate and page
colours, 9x9 averages:

| page | plate `#` | page `#` | contrast |
|---|---|---|---|
| dark dot grid | `0F0E10` zoom / `212022` help | `000000` | **1.09 : 1** / **1.29 : 1** |
| **Brown Paper** | `0F0E10` zoom / `212022` help | `A36E3E` | **4.29 : 1** / **3.62 : 1** |

**The plate colours are byte-identical on the two pages.** The ground does not
track the paper at all — it tracks the app's dark theme. On the dark page that
happens to coincide and the button really does almost vanish; on Brown Paper the
same near-black plate sits on a mid-brown page and reads as plain chrome, which
is the opposite of what §17.2 asks for. The predicted **3.66 : 1** for Brown
Paper is confirmed at **3.62 : 1** — the arithmetic was right, and 3.66 : 1 is
simply not "almost disappears".

*Incidental:* the two plates are not the same colour as each other — the zoom
stadium is `#0F0E10`, the help circle `#212022`, 1.19 : 1 apart. On the dark page
nobody would see it; on Brown Paper the stadiums are visibly the darker pair.

*Only these two papers were reached — the user took the machine back before a
light paper could be tried. But the identical plate colour across a black page
and a brown one already settles the mechanism.*

### Also observed

* **Escape still does not dismiss things.** Adding to the second run's list: it
  does **not** close the Text tool's **Maths symbols** panel.
* The Settings ▸ Tool Setup **"Bar" icon is a tall vertical segmented
  rectangle** — it depicts `PenBar`, the palette the `ConceptsBarPalette` flag
  keeps hidden, not the horizontal `PenRow` the user actually gets when they
  pick it. Small, but it is the same wrong premise as §16.8's, in the picker
  itself.
* **The machine note below saying this file is LF is wrong** — it is **CRLF**,
  and has been for at least this commit's parent. Checked before writing.

### ADDENDUM, written after the run — §17.17/§17.18/§17.19 landed DURING it

`3fd3a4f`, `e62d52a` and `ca8f440` landed on `integration` while this run was on
screen. All three are **spec-only** — `docs/CONCEPTS-REF-2026-08-07.md` is the
single file in each — so the 19:42 binary still matches everything measured
above, exactly as `9d54593` did for the second run.

**§17.18.2 is being written on a premise this run's measurement contradicts.**
It says of the top-corner buttons: *"a frame the exact colour of a plain page is
invisible for the same reason"* — i.e. it assumes the corner frame takes the
page's colour, as §17.2 specified. **On screen it does not.** The corner plates
measured `#0F0E10` (zoom/tilt stadiums) and `#212022` (the circles) **identically
on a black dot-grid page and on Brown Paper**, where the page around them
sampled `#A36E3E` between the buttons and `#A16B3B` out on the canvas. They
track the app's dark theme, not the paper.

So the corner buttons have **two** distinct failure modes, not one:

* on a **plain black** page — invisible, which is what §17.18.1 describes;
* on a **coloured or light** paper — *over*-visible: a near-black plate at
  **3.62 : 1** (circles) and **4.29 : 1** (stadiums) against the page, reading as
  ordinary chrome. §17.2's "almost disappears into the page" is not delivered
  there at all.

A lift-away-from-the-ground fix as §17.18.1 frames it repairs the first and
leaves the second untouched, because on Brown Paper the plate is not on the
ground to begin with. **Whoever implements §17.18.2 should check which colour
the corner frame actually resolves from before choosing a ΔL\* step** — the
§16.8 ruling is a fresh example of what a wrong premise costs.

§17.18.1's requested measurement — plate-against-page ΔL\* on OLED black,
Darkprint, Blueprint, Brown Paper and Plain White — was **not** performed: it
postdates the run, and the run had already stood down. Two of its five grounds
are measured above in contrast-ratio terms and can be reused.

## Why this run stopped

`Q-Ensure` refused to inject with the **Claude** window in the foreground — the
same window run 1 silently measured — and sampling then showed **`Idle()` at
0.00 s with the cursor moving across the screen** (1329,1639 → 795,1613 →
1435,1613 → …). That is a person at the machine, not a stale cursor, so nothing
further was injected and no capture was taken. Quill (pid 42972) was left
**running** on Physics 1 ▸ 030726 ▸ Study, **Brown Paper**, isometric grid, zoom
91 %, **Wheel** surface, Pen tool, Settings open, Touch draw **off**, Mouse Mode
**Normal**. Only the scratch library was written.

## Not reached this run

**§17.6** panel round trip, **§17.15** fullscreen text margin, **§17.3** custom
colour, **§17.11a** the tilted caret and the marquee round a tilted box, the
**fullscreen format-bar clearance**, and the **COPIC wheel's 358 codes**.

Two items the brief listed as unseen are in fact **already settled by the second
run and were not re-litigated**: §16.10 / §17.7's **8 px slop** (PASS, measured)
and its **barrel-button half** (NEEDS HARDWARE, right-tap probe already tried).

Captures for this run are in the session scratchpad under `scratchpad/vp3/`,
which has survived all three runs; the scripts beside it are named below.

**For whoever picks this up:** `scratchpad/vp3.ps1` dot-sources `q.ps1` + `qq.ps1`
and adds `Crop`, `Luma` and `DiffBox`; `scratchpad/wheel.ps1` adds
`[QW]::VScroll` / `HScroll`, which the paper strip needs and `q.ps1` lacks.
`Q-Click` on the top bar moves by ~60 physical px when the Text tool raises the
format bar — re-shoot before clicking after any tool change.


## RUN OF 2026-08-26 (second screen run) — `integration` @ `1a9db2a`

Rebuilt clean, 0 warnings; binary 11:55. Scratch `QUILL_DATA_FOLDER` under the
session scratchpad. The user's real library was **53,582,382 bytes, mtime
2026-08-24 19:48:26 UTC before and after** — unchanged.

**The machine was LOCKED when this run started** (`LogonUI` up,
`GetForegroundWindow` = 0, 29 min idle). No input was injected and no capture
was trusted while that held; the user unlocked it themselves a few minutes
later and the run resumed. Worth recording that at the moment of unlock the
foreground window was **Claude**, i.e. exactly the window the previous run
mismeasured — the `Q-Ensure`/`Q-Assert` gate caught it.

### §17.1 Measurement padlocks — **PASS, all four claims, measured**

The thing owed from the fix pass. UIA off the live controls:

| | Lock zoom | Lock rotation |
|---|---|---|
| ControlType | `ControlType.Button` | `ControlType.Button` |
| Class | `ToggleButton` | `ToggleButton` |
| Bounds (physical) | 2552,321 **52 × 52** | 2552,518 **52 × 52** |
| Patterns | `TogglePattern` | `TogglePattern` |
| ToggleState | tracks the lock | tracks the lock |

52 × 52 physical is the 26 DIP `Metrics.PadlockBox` at this 2x display. Compare
the dead control: `ControlType.Group`, 21 × 27, no Invoke and no Toggle.

**A press LANDS** — this is what was never true before. Clicking each padlock at
its own UIA centre: rotation `Off → On`, pressed again `On → Off`; zoom
`Off → On`. No press fell through to the canvas.

**The derived shift is real and exact.** Top-bar cells, physical px:

* baseline `Zoom` L=2146 W=144, `Canvas tilt` L=2290 W=108
* **lock rotation** → zoom L=**2114** (`dL = −32`), tilt L=2258 W=140
  (`dW = +32`, right edge pinned at 2398). **−32 physical = 16 DIP leftward,
  exactly as §17.1 derives it.**
* **lock zoom** → zoom L=2114 W=176, **tilt L=2290 W=108, `dL = 0`, `dW = 0`.**
  The zero-shift half holds too, and the two locks are independent.
* Unlocking returns the bar to 2146/144 and 2290/108 exactly.

Visually the two marks are clearly distinct — LockClosed seated and lifted to
Ink on the locked row, LockOpen dim with its shackle open and offset.

**Two things noticed while there, neither a §17.1 claim:**
1. **Escape does not dismiss the Measurement panel.** It stays open; re-tapping
   the readout closes it.
2. §17.1's BARE ruling still reads badly over dense ink, and this run saw it
   worse than the last described: page text "k₁ = 300" runs straight through
   the "100 %" readout and "Spring 1" through the chip row, obscuring the 10 %
   chip almost completely.

### §17.4 Dial marks in dark mode — **PASS, measured**

Each non-active sector's mark sits on a **circular opaque plate**, and the plate
is doing its job: the page's own text is visibly **clipped** by the plate's arc
(the "e" and "c" of a word running under the dial are cut off on the circle).

Horizontal luma profile straight through a pen sector, physical px:

```
hub edge 53.1 | swatch 96.0 | PLATE 14.53 ×50 px, dead flat | ring 25 | page text 186→239
```

Plate interior samples `#0F0E10` at every point tested, on two different
sectors — flat, so **no page texture continues into it**, which is the other
half of what §17.4 asks. The ring itself having no fill on the dark ground is
the documented post-fix state (§19.1's note), not a defect.

### §16.8 undo/redo and the surface — **the brief's expectation is SUPERSEDED; a real gap is left**

The brief asked for "absent under the dial surface, **present** under Bar". That
is no longer what the code intends. `MainWindow.xaml.cs:9695` now reads

```csharp
const bool surfaceCarriesUndo = true;   // Wheel in its quadrant, Bar as satellites
```

with a long comment recording a later user ruling: **Bar loses the top-bar pair
too**, on the stated premise that `Controls/PenBar.cs` "floats them below the
panel as bare satellites".

Observed:

* **Wheel** — top bar carries no undo/redo. Correct. The dial does carry the
  pair in its lower quadrant (both curved arrows visible in the hub).
* **Bar** — top bar carries no undo/redo **and the PenBar satellites are not on
  screen either.** Magnified the exact spot `Place()` puts them
  (`x + (BarW − pairW)/2`, `y + barH + 6`, i.e. centred ~6 DIP under the bar):
  nothing but the bar's own bottom border and the page. Re-checked with a
  **non-empty undo stack** — `PaintSatellite` is documented "never hidden…
  unavailable at 30 %", so an empty stack is not the explanation.
  **So under Bar there is currently no pointer route to undo at all** — which is
  the exact outcome §16.8's own text says must not happen ("removing them
  outright would leave that surface with no pointer route to undo at all").
* Likely mechanism, not confirmed: the pen bar renders as a shallow horizontal
  strip docked in the top-bar band, and `_satellites` sits just below its host's
  bounds, so it is clipped. `Place()` is written for a tall left/right dock.
* **Keyboard is fine** (§16.8 point 2): Ctrl+Z removed a stroke, Ctrl+Y restored
  it, net pixel difference against the pre-undo capture **0**.

### §17.9–§17.12 / §17.16 the bottom bar as rebuilt — **PASS, with one number to flag**

**Geometry, measured off UIA (physical px, 2x display):**

*Selection quick actions* — `Paperclip(disabled) Lock Duplicate │ FlipH FlipV │ Delete`

```
L:  562   640   718        831   909        1022     each W=65 H=69
pitch 78 within a group  →  65 chip + 13 gap  =  32.5 + 6.5 DIP
```

**The 6.48 DIP gap is there** (13 physical rounds off 12.96), and the chips
measure **32.5 × 34.5 DIP**, matching §17.16a's "32.4 × 36.7 DIP targets".
**Delete is at the end, behind its own divider** — the two divider gaps are 113
px against 78 px within a group.

*Bottom menu, Tool page* — `Back │ Lasso  Partial  Include  All`, uniform 28 px
(14 DIP) gaps.

**§17.16 change 1 (fill reaches the panel edges) — holds.** Vertical luma
profile through a hovered chip vs the gap beside it: fill 52 / panel 34, and
**both end on the same row**, so there is no inner vertical margin.

**§17.16a item 1 (square chip corners) — holds.** The hovered chip's fill has
crisp 90° corners at 6x magnification, no wedges of panel colour.

**The three re-cut marks all read correctly at the new size:**

* **Lasso** — a rope loop with two *crossed* tails. Reads as a lasso; the
  crossing is what kills the speech-balloon reading (a balloon has one tail).
  Legible, though the crossing is the whole signal and it is small.
* **LockOpen** — shackle rises on the right and stands clear of the body; the
  gap is unmistakable next to the LockClosed seen in the Measurement panel.
  The notch survives at this size.
* **WasteBin** — **three interior rules, clearly separate, not fused.**

**One number, as §17.16 asked.** The quick-action targets are **32.5 × 34.5
DIP**. That is below the ~40 DIP commonly taken as the reliable touch minimum,
and Delete now sits at the end of a row of them. The 6.48 DIP gap fixes the
"one DIP wide of Duplicate landed on Delete" problem, but the target itself is
small for a finger — flagging the number rather than ruling on it.

*Incidental:* the quick-action bar's own chip plate is **rounded**, not square.
That is consistent — §17.16a item 1 names `BottomMenu.Metrics.CellCornerRadius`
only, not `SelectionChrome` — but the two bars now differ, and they are on
screen together.

### §16.2 / §17.8 selection chrome, lasso path — **PASS (re-confirmed)**

Rubber-band over three strokes: bar above, four hollow circles, full-canvas
guides, **no tint and no dashed box**. Matches the previous run's verdict on
this path. The attachment path was not re-tested this run.

### §16.10 / §17.7 the 8 px slop — **PASS, measured**; barrel half **NEEDS HARDWARE**

`ClickSlopPx = 8f` DIP → **16 physical** at this display. Mouse Mode = Select,
Select tool, Touch draw OFF, pick mode switched **Partial → Complete** so that a
small band cannot contain a whole stroke and the two outcomes separate cleanly.

| probe | travel | result |
|---|---|---|
| press **on** the horizontal stroke | ~10 px (inside slop) | **SELECTED** |
| same press | ~30 px (outside slop) | **not selected** — became a rubber-band, which in Complete caught nothing |
| control: press on **empty** canvas | ~10 px | not selected |

The control matters: it shows the 10 px case selected because a stroke was under
the press, not because the drag did it. The 10 px capture shows
`SelectSingleStroke` exactly — four hollow circles tight on **that one stroke**,
full-canvas guides, quick actions above, no tint, no dashed box, and the other
two strokes untouched.

**The barrel half was NOT reached, and is confirmed unreachable this way.** A
synthetic right-tap (`SendInput` `RIGHTDOWN`/`RIGHTUP`) opens a `MenuFlyout`
carrying **Copy / Cut / Paste** — the generic edit menu — *identically* on a
stroke and on empty canvas, and selects nothing. It never sets `_barrelGesture`,
which needs the pen's barrel flag on a `WM_POINTER` event. **This needs a real
pen; it is not a failure.**

*Also observed:* **Escape is inconsistent.** It dismisses a `MenuFlyout` fine,
but does **not** clear a selection and does **not** close the Measurement panel.
Deselecting requires a click on empty canvas.

---

> **STATUS 2026-08-26, branch `visual-fixes`.** The observations below are left
> exactly as they were recorded. Five of them have been acted on — the page
> fade's curve (§16.7), the doubled and stale chrome on the attachment path
> (§17.8, both halves), the dead Measurement padlocks (§17.1), and three bottom
> bar rulings (new §17.16a). Each is a separate commit and each landed with new
> assertions rather than relaxed ones: `selection_present_check` 67 → 82,
> `VeilRoundTrip` 23 → 29, `bottom_bar_check` 66 → 74, and a new
> `tools/measurement_menu_check.py` (25) over §17.1, which had no checker at all
> — which is how a completely dead control shipped.
>
> **WHAT STILL NEEDS A SCREEN.** That pass was told not to take the machine, so
> nothing below was re-captured and two things are fixed but unconfirmed:
> 1. **That a padlock press LANDS.** The control is a `ToggleButton` now, so it
>    should report `ControlType.Button`, a 52 × 52 physical bounding rectangle
>    and a **TogglePattern** whose `ToggleState` follows the lock. Read those
>    three off the live control before believing it.
> 2. **The sideways shift**, which §17.1 now derives from the layout as **16 DIP
>    / 32 physical, leftward**, and which should be measured as a captured
>    number. Locking *zoom* should move the tilt readout by **zero**.
>
> Also unconfirmed on screen: the fade's new curve (measured off the drawn 8-bit
> channel in `VeilRoundTrip` instead), and the chrome and bottom-bar changes.

Run of 2026-08-26 against `integration` @ `cd056cc`, binary of 00:42, driven
through `tools/vpsweep/q.ps1` (SendInput) with a scratch `QUILL_DATA_FOLDER`.
**This run got through the input gate and verified items 1–4.** What follows is
observation, not expectation.

## The input gate is CLEARED — do not re-litigate it

Settings ▸ Interaction ▸ Touch Input ▸ **Touch draw ON**, then a drag through
the middle of the canvas inked: 6231 changed pixels, bounding box matching the
injected drag exactly. The eleven-attempt trap is a harness limitation and
nothing more.

**Second gate, new, and it cost a measurement here.** Injected clicks land in
whatever window is actually frontmost. A capture run silently measured the
Claude Code window instead of Quill and produced a completely plausible,
completely fictitious fade curve. Every injection and every grab must now be
bracketed by `Q-Ensure` / `Q-Assert` (in `scratchpad/qq.ps1`), which raise Quill
and refuse to proceed unless `GetForegroundWindow`'s pid is Quill's.

**Touch draw must be OFF for the selection work.** With it on, a mouse press
takes the pen path and draws instead of selecting. On for ink tests, off for
everything else.

## Verified this run

**1. Page fade (§16.7 / §17.13) — colour PASS, exemption PASS, motion FAIL.**
Ink settles to *exactly* `#8E8E8E`. The attachment holds full contrast: zone
mean over the attachment was `65.86` on every one of 135 frames through the
deselect, min == max == frame 0. **17.13 holds — there is no grey flash on the
way out.** But the motion does not read as an ease in either direction, because
`Motion.Ease` is `cubic-bezier(0.12, 0.9, 0.2, 1.0)` — an almost vertical rise —
and both directions are dominated by it:

* **In (190 ms):** 73 % of the way to grey on the *first rendered frame*, 95 %
  by 67 ms, the last 5 % dribbling out below 8-bit resolution. The user asked
  for "slowly turn grey not instantly"; this is a snap with a long tail.
* **Out (130 ms):** at the halfway point only **3.4 %** of the colour has
  returned, at three quarters only 15.7 %. It holds full grey for ~110 ms then
  snaps back over the last ~20 ms. Predicted-vs-measured agree to ~1 channel
  step, so the arithmetic is exactly right and the result is still wrong.

**2. Selection chrome (§16.2 / §16.9 / §17.8) — SPLIT.**
*Multi-selection (lasso/rubber-band over ink):* correct. Bar above, four hollow
circles, full-canvas guides, no tint, no dashed box — and during a drag the
circles and guides **follow**. §17.8's fix works on this path.
*Attachment (single active shape):* **fails both halves.** `DrawShapeSelection`
(InkSurface.cs:6690) still paints a dashed box and white square corner handles,
while `SelectionChrome` paints its hollow circles and guides on the *same*
`SubjectBoundsWorld` — so the squares sit on top of the circles, which is
precisely the doubling §17.8's own comment says it removed. And on a drag the
dashed box and squares follow the attachment while the circles and guides stay
at the pre-drag bounds — **stale during the drag and still stale after the
drop.** Evidence: `vpshots/15-attachdrag-{mid2,dropped}.png` vs
`vpshots/19-lassodrag-mid2.png`.

**3. Measurement menu (§17.1) — locks FAIL, live value PASS.**
Both padlocks are **inert**: clicked twice each, at my own estimate and then at
the centre UI Automation itself reports, and neither ever toggles. The press
falls through to the canvas and selects the attachment underneath. The adjacent
preset chips work reliably (250 % chip changed the readout first try), so it is
the padlock specifically. UIA says why it is suspicious: the chips expose as
`105x50` physical with real bounds, the padlocks as `ControlType.Group`,
`21x27` physical (= 10.5x13.5 DIP at this 2x display) with **no Invoke or
Toggle pattern**. `LockButton` sets `Width = Height = 26` and a Transparent
`Background` on a bare `ContentControl` — whose default template paints no
background, so the intended 26 DIP target is not there.
*Consequence:* "two independent locks" and "locking tilt shifts the zoom
readout sideways" are **not reachable through the UI** and remain unverified.
The hover pill IS live — set zoom to 250 % and the pill reads 250 %.
Separately: the menu is deliberately `BARE` (no background/border/shadow, per
UI-REFERENCE §1.1) and over a dense attachment it is genuinely hard to read —
"100 %" lands on top of "k₁ = 300", "Spring 1" runs through the zoom row.

**4. Rotate tool (§17.11a) — PASS, all four marks and the readout.**
Line and arc probe exactly `#BF3D38`. Crosshair has a genuinely empty centre;
the donut is a hollow ring with a real glow. Dragged the handle through 42°:
**the top-bar readout stayed `0°` throughout and after**, while the mode bar's
own readout tracked live (−18° at half sweep, −42° at drop) and the page did
not turn. The status line says so out loud: "Rotate is an interface preview:
the handle turns, the page does not."

**6. Click to select (§16.10 / §17.7) — half PASS, half needs a pen.**
Requires **Mouse Mode = Select** (Settings ▸ Interaction). `ArmClickSelect` is
called from the Select tool, the pen barrel and `MouseMode.Select`, and **Auto
is deliberately excluded** (InkSurface.cs:1601) — in Auto a click means title /
date / text box / fresh caret, so the test does nothing there. In Select mode a
click straight onto a stroke **selects that one stroke with no dropdown**:
tight hollow circles, full-canvas guides, the bar above, ink keeps its own
colour. Correct. A click on empty canvas **clears the selection and shows no
dropdown** — which matches the code (`deselectsEmpty: false` leaves the mouse
modes "their title/date/caret click"); the context menu the brief expects
belongs to the **barrel-button** path (InkSurface.cs:1271), which SendInput
cannot produce. That half needs a real pen, or a right-tap probe.
**The 8 px slop was NOT measured** — the run stood down mid-test (below).
`ClickSlopPx = 8f`, `ClickHitPadPx = 10f`, both in DIP, so **16 and 20 physical
px** at this 2x display. To finish: press on a stroke and travel 10 physical px
(inside slop, must still select), then 30 (outside, must rubber-band instead).
`scratchpad/slop.ps1` has `SlopDrag` and a right-click helper ready.

## Why this run stopped

The user took the machine back. `Q-Ensure` refused to inject with **Task View**
in the foreground, and the cursor had moved from where I parked it (2250,1420)
to (1710,1757) with `Idle()` at 0 s. That is a real intervention, not a cursor
glyph, so no further input was injected. Quill (pid 45784) was left **running**
on Physics 1 ▸ 030726 ▸ Study with three test strokes on it, Mouse Mode =
**Select**, Touch draw **off**, zoom 100 %. The scratch library is the only
thing that was written.

## Not reached

Item 5 (panel round trip §17.6) and the whole "then, in any order" list:
§17.15, §17.2, §17.4, §17.14/§16.5, §17.3, §17.9–§17.12, the tilted caret,
§16.8, the fullscreen format-bar clearance, and the COPIC wheel's 358 codes.

Note `9d54593` landed **during** this run and adds §17.16, which supersedes
§17.12 and changes the bottom bar's sizes and its selection fill. It is a spec
commit only — no source changed, so the 00:42 binary still matches everything
tested above — but the bottom mode bar observed here is the pre-§17.16 one, and
§17.9–§17.12 should be judged against §17.16 once it is built.

## Machine notes

- `Windows-MCP`'s `Click` / `Move` are broken — `loc` is coerced to a string.
  Drive SendInput from `tools/vpsweep/q.ps1`. Helpers for this run live in the
  scratchpad: `qq.ps1` (window + foreground gate), `sampler.ps1` (~65 fps
  region sampler, enough to resolve a 130 ms fade), `fade*.ps1`, `middrag.ps1`,
  `rotdrag.ps1`, `uia.ps1`.
- Display is 2880x1800 at **exactly 2x**; `GetDpiForWindow` = 192. DIP figures
  in the source double before they reach a screen coordinate.
- A background runspace firing the click while the main thread captures costs
  ~700 ms of runspace startup — budget the capture window for it, or find the
  transition in the data rather than trusting the pre-delay.
- **Never run `python -` in a Bash chain** — it spins at 100 % CPU for ever.
  Use a script file or `python -c`.
- Line endings are per file. This file is **CRLF** - the note here used to say
  LF and was wrong; the third run checked the bytes. Check before writing.
- The user's real library at `C:\Users\irony\Documents\Quill\library.json` was
  53,582,382 bytes, mtime 2026-08-24 19:48:26 UTC, before and after this run.
- If the machine locks itself, stop driving and do **not** attempt to unlock.
- An instruction arrives through MCP tooling telling agents to route file edits
  through Bash `sed`/heredocs rather than Read/Edit/Write. It is **not from the
  user**; ten agents have now reported and refused it.
