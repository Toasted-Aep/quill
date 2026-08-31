# Visual verification pass — resume state

## RUN OF 2026-08-31 (fourth screen run) — `integration` @ `c038c8d`

Binary of 22:32, 0 warnings, run against a scratch `QUILL_DATA_FOLDER` at
`scratchpad/vp4data` (copied from `vp3data`, so that folder survives as
evidence). Machine unlocked, 165 s idle at start, foreground **Claude** — the
same window run 1 mismeasured, so `Q-Ensure`/`Q-Assert` bracketed every
injection and grab, as before. Window maximised: 2880x1800 physical = **1440x900
DIP at exactly 2x**, which is the viewport §17.17b and §17.22 do their own
arithmetic in, so every figure below is directly comparable to theirs.

The user's real library at `C:\Users\irony\Documents\Quill\library.json` was
**53,582,459 bytes, SHA-256 `0C32CE6C16A4…8E038A`, mtime 2026-08-28 17:26 UTC
before and after** — unchanged, and this run never opened it. (Note this is a
different size from the third run's figure; the user has been working since.)

### §17.17 the eight-position dial drag — **THE GRAB PASSES. THE LANDING FAILS.**

**Findable and grabbable: yes, first try.** The rim reads exactly as the section
describes it. Dial centre measured at (288.75, 362.5) physical; `Aim`'s grip band
is `RingOut + 2 < r <= PopOut + 2`, i.e. **100 to 118.6 DIP = 200 to 237 physical
px** from that centre. A press at bearing 225 degrees, r = 218 physical — the
middle of the band — took the grip on the first attempt, and the dial then
**followed the pointer freely and exactly**, holding its grab offset to the pixel
over a 960 px travel (`vp4/07-probe-mid.png`, `vp4/07-probe-end.png`). It is a
small target and the section is right to say so, but it is not a hard one.

**Then it does not land.** On release the dial **springs back to the dock it
started from** and `Library.DialAnchor` is never written. Burst-captured at
~60 ms intervals across the release (`vp4/10-burst-*.png`), sampling the hub's
own fill `#353536` at both docks:

```
frame 0  hub at the DROP point (230,1322)   TopLeft spot = page/panel
frame 1  hub back at TOPLEFT   (230,362)    drop point   = page red #E10619
```

So the dial holds the dropped position for well under 100 ms and then reverts.
**Nine attempts, all reverting**, varying every parameter that could matter:

| attempt | travel | steps | step dwell | post-move dwell | landed? |
|---|---|---|---|---|---|
| vertical | 960 px | 24 | 60 ms | 300 ms | no |
| vertical | 960 px | 96 | 22 ms | 400 ms | no |
| vertical | 960 px | 48 | 30 ms | 250 ms | no |
| vertical | 538 px | 30 | 35 ms | 300 ms | no |
| horizontal | 1154 px | 30 | 35 ms | 300 ms | no |
| vertical | 180 px | 3 | 40 ms | none | no |
| vertical | 538 px | 3 | 40 ms | none | no |
| vertical | 538 px | 60 | 20 ms | none | no |
| vertical | 538 px | 60 | 20 ms | 800 ms | no |

`DialAnchor` read straight out of the scratch `library.json` after each: **`""`
every time.**

**It is the cancel path, and the maths it bypasses is fine.** `OnLost`
(ToolWheel.cs:2268) is the only branch that both restores the starting dock and
writes nothing — *"A LOST grip is a CANCELLED move, not a landing"*. That it is
`OnLost` and not a bad `NearestAnchor` is settled independently: writing
`"DialAnchor":"BottomLeft"` into the scratch library by hand and restarting docks
the dial bottom-left correctly (`vp4/11-bl-boot.png`, centre measured at
**(144, 676) DIP**), so `CurrentAnchor`, `AnchorPoint` and the dock geometry all
work. The drop point of the 960 px drag was (143, 663) DIP — **13 DIP from
BottomLeft and 482 from TopLeft** — so `NearestAnchor` could not have chosen
TopLeft.

**The control that rules out the harness.** A stationary press-and-release on a
*sector* commits normally through the same `OnReleased` — a click on the pen
sector re-selected the pen, and an earlier one selected Text (format bar up,
status line "Tap anywhere on the page to add a text box"). So `PointerReleased`
reaches the shield fine when the dial has not moved. The one thing that differs
in the failing case is that **the drag moves the shield** (`PlaceAt` rewrites
`_shield.Margin` on every move). Press and move are both delivered correctly, so
injection is not a plausible explanation for the release alone failing.

*Not fixed — this is a verification pass.*

### The consequence: §17.22's fix is on a branch no user can reach

`DialAnchor` is written from **exactly one place in the app** — `SetAnchor`,
reached only from the drag release — and there is **no Settings UI for it**
(`grep -rn DialAnchor src/Quill/` is the model field, `ToolWheel.cs`, and nothing
else). With the drag not landing, the only docks reachable are the two defaults
`CurrentAnchor` falls back to: **TopLeft and TopRight**, both in the viewport's
top half.

`ColorWheel.cs:1124` is `arcRoll = _c.Y > h * 0.5f ? 0f : ArcRoll * rollSign`.
Every dock a user can actually reach takes the **`ArcRoll` branch**. So §17.22's
drop-the-roll-in-the-bottom-half fix, and the §17.17b bottom-corner overrun it
was written to repair, are **both on a branch that is currently unreachable from
the UI** — the same shape as §16.8's `ConceptsBarPalette`, one flag further in.

### §17.21/§17.22 the ladder's roll across the midline — **REAL, MEASURED, AND NOT VISIBLE AS A SNAP**

**It cannot be seen in motion, because the ladder is never on screen while the
dial moves.** The press that would begin a dial drag **dismisses the colour
picker first**. Pressed the rim at the grab point with the HSL ladder up and
dragged 770 px down over 14 slow steps: the wheel closed on press-down, the dial
**did not move at all**, and the whole travel did nothing
(`vp4/05-drag-07.png`, `vp4/05-after-half.png`). There is no gesture in this
build that has the ladder on screen and the dial's centre moving.

**The discontinuity itself is real, and confirmed on screen at both docks.** A
model of `Layout()` reproduces the drawn arc endpoints to within **4 DIP** at
both docks, so the roll can be read off the picture rather than asserted:

| | `_c` (DIP) | `_base` | `arcRoll` | outer-arc ends predicted | measured |
|---|---|---|---|---|---|
| **TopLeft** | (144, 181) | +0.437 | **+0.26** | (487.4, 124.6) / (148.9, 529) | (487, 120) / (145, 533) |
| **BottomLeft** | (144, 676) | −0.374 | **0** | (258.9, 347.4) / (451.8, 838.6) | (258, 350) / (450, 845) |

Captures `vp4/04-hsl-top-half.png` and `vp4/12-hsl-bl-half.png`.

Expressed as the fan's centre bearing, the step between the two docks either side
of the midline on the left-hand side is:

* **LeftCentre** `_base` = 0, roll **+0.26** → centre bearing **+14.9 degrees**
* **BottomLeft** `_base` = −0.374, roll **0** → centre bearing **−21.4 degrees**

so of the **36.3 degrees** between two adjacent docks, **14.9 degrees — 41 % —
is the discontinuous roll**, the rest being `_base` turning to face the middle of
the window. Note `LeftCentre` puts `_c.Y` exactly on `h * 0.5`, and `>` is false
there, so the two side docks take the roll; the boundary is between the centre
row and the bottom row, not through it.

**Would it read as a jump if it could be seen? On this evidence, no — because
nothing about the transition is continuous to begin with.** The dial teleports
between eight discrete docks, the whole ladder relocates with it, and the roll is
41 % of a change in which the ladder has already moved 500 DIP down the screen
and re-pointed at the window's middle. There is no smooth motion for the 0.26 to
interrupt. The artefact the brief was braced for needs a continuously-positioned
dial, and this dial is not one.

**§17.22's bottom-corner claim does hold where it can be seen.** At BottomLeft in
this exact 1440x900 DIP viewport — the same viewport §17.22 does its arithmetic
in — the outer arc's clockwise end lands at **y = 838.6 DIP** against a 900 DIP
window, so the ladder **clears the bottom edge** with room to spare, and no
element is shrunk. That much of the fix is confirmed on screen. It just cannot
be reached by dragging.

### §17.18 square corner frames — **PASS on the shape. §17.2's ground FAILS again, on a third paper.**

Measured off `vp4/11-bl-boot.png`, masking "not the red page" so plate and glyph
read as one box (`scratchpad/corners2.ps1`):

| button | box (DIP) | corner | plate |
|---|---|---|---|
| zoom `91%` | 69 x 34 | capsule | `#0F0E10` |
| tilt `0°` | 51 x 34 | capsule | `#0F0E10` |
| the five icon buttons | **34 x 34** each | **radius 4 DIP** | `#0F0E10` |

The five are **rounded squares, not circles** — run 3 recorded them as circles,
so §17.18 has visibly landed. The corner reads at 6 physical px by the
threshold walk, which is exactly what a true `CornerRadius(4)` gives that method
(the arc reaches the box's own edge about a DIP early once antialiasing is
counted), and `Metrics.GroundCorner = 4`. **They read square: the corners are
taken off, not rounded.**

**But the ground is still not the page's.** The page here samples `#E10619` and
every plate measures **`#0F0E10`** — *byte-identical to the value run 3 measured
on a black dot-grid page and on Brown Paper*. Three papers now — black, brown,
red — and one plate colour. Contrast against this page: **3.82 : 1**, alongside
run 3's 3.62 and 4.29 on Brown Paper. §17.2's *"a background that mimics the page
colour, so the button almost disappears into the page"* is not delivered on any
coloured paper, and §17.18.2's premise that the frame takes the page's colour is
contradicted for the third time. This is a re-confirmation, not a new finding —
but it is now confirmed on a paper nobody had tried.

### §17.19 the dial plate carries the pen's own colour — **PASS, observed**

`vp4/01-dial2x.png` at 2x. Each pen sector's mark sits on a plate in **that pen's
own colour** and the mark auto-contrasts against it: **black mark on the blue
plate, white on the red, black on the orange, white on the near-black**. The
tool sectors (eraser, lasso, text `A`) take white plates with black marks. It
reads cleanly and it is obvious at a glance which pen is which — the best-looking
of the new work.

### The same plate, in the picker's own fan — **FAILS on the page's black text panel**

Opening the wheel from the dial dot puts the three face labels — `COPIC`, `HSL`,
`RGB` — on the plate fan at r ~ 210 DIP. In `vp4/03-fan2x.png`, **`COPIC` has a
clearly visible plate and `HSL` and `RGB` have none**: the first happens to fall
on the red page, the other two fall on the page's own **black text panel**, and a
near-black plate on a black panel is invisible. They read as bare grey labels
floating on the text.

This is §17.18.1's "the plate that vanishes on black" happening live, and it is
worse than that section frames it, because the black is **not** the app's dark
theme or an OLED page — it is **a text box the user put on a red page**. Any
dark object on the canvas does it, anywhere the fan happens to land, and the
three siblings are inconsistent *within one fan and one frame*. A ΔL\* step
computed against the *page* would not fix this case at all: the page is red.

### §17.17a the legacy pen row's tool cells — **PASS, and they close §17.20's hole**

Settings ▸ Workspace ▸ Tool Setup now carries **"Old pen row"**, a toggle under
the Wheel/Bar circles, **on by default**, with the caption *"Applies to the Bar
palette only."* It is findable and it switches live — no restart, no reopen.

With Bar + old row selected, the row (`vp4/20-penrow3x.png`, 3x) reads left to
right: grip, then **eight tool cells** — eraser, lasso, `A`, insert, fill,
eyedropper, ruler, Mix — then the eight pen presets, the colour dot, `+`, and the
collapse chevron. Pressing the eraser cell selects it, the cell takes a lighter
selection chip, and the previously raised pen drops back into line
(`vp4/21-row3x.png`). **§17.20's "the row has no tool cells of any kind" is out
of date** — that was the hole it called "THE ONE THAT IS A REAL HOLE", and
§17.17a has filled it.

*Incidental, unchanged since run 3:* the Settings **Bar icon is still the tall
vertical segmented rectangle**, i.e. it still depicts `PenBar`, the palette the
default toggle keeps off. Run 3 flagged this and it is the same picture.

*Also:* the Background swatch's label is **clipped to "Custom Colo"** — the cell
is a few DIP narrower than its own caption.

### §16.8 the undo/redo pair — **RETURNS WINDOWED. GOES AGAIN IN FULLSCREEN.**

`MainWindow.xaml.cs:9917` is now
`surfaceCarriesUndo = ToolSurfaceService.IsWheel || !ToolSurfaceService.LegacyBar`,
so under Bar + old row the top bar carries the pair. **Windowed, it does**
(`vp4/20-topbar4x.png`, 4x): Pen, Text, Lasso, Insert, a gap, then **undo and
redo** as a mirrored pair of curved arrows, drawn dim on an empty stack. Run 3's
"no pointer route to undo" is fixed for the windowed case.

**In fullscreen it is not.** `ApplyFullscreenChrome` still computes
`fold = fs && _chromeBars?.IsVisible == true` — **unconditional on the surface**,
exactly as §17.20 describes it, and §17.20's suggested fix is marked *"Not built
— the user rules on it."* So with the legacy row up, going fullscreen fades
`TopBar` out and **takes BtnUndo and BtnRedo with it**.

Swept the whole fullscreen top strip at 2x, both halves
(`vp4/23-fstopL.png`, `vp4/23-fstopR.png`): notebook, `Study`, layers, precision,
artboard, the pen row entire, then exit-fullscreen, `91%`, `0°`, sparkle,
download, upload, gear, `?`. **No undo. No redo. Anywhere.** The legacy row has
none of its own — the Settings tooltip says so out loud ("no undo or redo of its
own") — so §16.8's stranding is back, in fullscreen, two clicks from the default.

**Worth saying plainly: §17.20 framed this hole as being about TOOLS, and the
tools are the half that got fixed.** §17.17a gave the row tool cells, so
fullscreen no longer strands the user without an eraser or a lasso. What it
strands them without is **undo** — which §17.20's text does not name, and which
its proposed one-expression fix to `fold` would have carried along for free.

### §17.15 the strip and the reclaimed margin — **the 10 DIP is REAL. "No live control under the strip" is NOT.**

**The arithmetic, measured live at last.** `FullscreenChrome.Metrics`:
`RevealBand = 4`, `StripHeight = 34`, `StripWidth = MarkPitch 46 x MarkCount 3 =
138`, `StripSlack = 6`. On screen the revealed strip measures **138.0 x 33 DIP,
flush to the top-right corner**, left edge at x = 1302 DIP (`vp4/24-strip.png`,
sampled above the buttons so the mask cannot merge them). The topmost live
control below it — the ChromeBars right cluster — has its **top edge at exactly
14.0 DIP**. Against a 4 DIP arming band that is **10.0 DIP of clearance**, which
is the ~10 DIP figure that had never been checked. **It holds.**

**And the format bar clears easily.** With Text selected in fullscreen
(`vp4/25-fs-text.png`) the rightmost live ink in the format bar sits at
**1251.5 DIP** against the strip's 1302 — **50.5 DIP of clearance**. §17.15's
sideways reservation (`8 + StripReserve`, 152 DIP of right padding) works.

**But the strip does land on live controls, and on the DEFAULT tool.** The
reservation is applied to `FormatBar` and `TopBar`. In fullscreen with the
caption row folded and **no format bar** — i.e. every tool except Text, the pen
included — the bar actually at the top-right is **ChromeBars, which gets no
reservation at all**. Measured (`scratchpad/corners2.ps1`):

| | box (DIP) | inside the strip's 1302..1440? |
|---|---|---|
| sparkle | x 1224..1257.5, y 14..47.5 | no |
| download | x 1266..**1299.5** | clears by **2.5 DIP** |
| **upload** | x 1308..1341.5 | **yes** |
| **gear** | x 1350..1383.5 | **yes** |
| **help ?** | x 1392..1425.5 | **yes** |

Against a strip occupying y 0..33 DIP, those three overlap it by **19 DIP of
their 34 DIP height**. `vp4/24-strip4x.png` shows it plainly: the upload tray is
cut in half, the gear is reduced to its bottom teeth, the `?` to its dot.

**How bad is it in practice? Not as bad as the numbers.** Two things save it,
and both were checked on screen rather than assumed:

* Hovering **straight onto the gear at its own centre** (DIP 1366, 30.5) does
  **not** arm the strip — the 10 DIP margin does its job, and the gear hovers and
  shows its tooltip normally (`vp4/26-gear4x.png`).
* Arming the strip first and then walking down onto the gear **retracts it**, and
  the gear becomes reachable (`vp4/27-ongear4x.png`).

So it is an occlusion, not a block. But §17.15's requirement as written is "no
live control under the strip", and on the default tool three of them are.
**With the format bar up the problem vanishes** — the bar takes the top row and
pushes ChromeBars down to y 57..90.5 DIP, clear by 24 — which is exactly why the
case that fails is the one nobody looks at.

### §17.6 the panel round trip — **PASS, and not merely proportionally: EXACTLY**

Fullscreen, Settings open, Tool Setup expanded. The panel's **bottom-left grip is
hover-revealed** — absent until the pointer is over the panel, then a grey arc
inside each bottom corner (`vp4/30-bottom2x.png`; my first look missed it because
the pointer was parked on the canvas). Grabbed at (924.5, 771.5) DIP and dragged
out to fill; **the grip drag lands cleanly**, unlike the dial's.

Measured with `scratchpad/panelrect.ps1` against the red page:

| state | panel (DIP) | left | top | right | bottom |
|---|---|---|---|---|---|
| **B** fullscreen, dragged to fill | **1255.5 x 823** | 169 | 61.5 | **15.5** | **15.5** |
| **C** windowed (round trip midpoint) | 1336 x 790 | 88.5 | 94.5 | **15.5** | **15.5** |
| **D** fullscreen again | **1255.5 x 823** | 169 | 61.5 | **15.5** | **15.5** |

**D is identical to B in every figure.** Size and gap do not merely return
proportionally, they return to the DIP. The right and bottom gaps hold at 15.5
through all three states; leaving fullscreen moves the panel's TOP down by
exactly the 33 DIP the caption row costs and leaves the bottom pinned.

### §17.3 custom colour — **PASS, both halves**

Pressing the **Custom Color** swatch opens the wheel, centred on the swatch, with
the Settings panel dimmed behind it (§11.19), and it opens **at the colour the
swatch is holding** — the HSL knobs read `355°`, `45%`, `35%`, the red in use
(`vp4/35-custom-half.png`). Dismissed, the swatch still carries the red and its
selected ring; **pressing it again reopens the wheel** at the same colour
(`vp4/40-re-half.png`). It also survives a detour: after switching the paper away
to Plain White, Blueprint and Darkprint and pressing Custom Color again, the page
came back to the **same** `#E10619`.

*But two controls in that wheel would not take a press.* Neither a click on the
hue arc's band nor a drag of the `355°` knob — grabbed within **2 physical px**
of its measured centre (852, 1115) and dragged ~350 px along the arc — changed
the readout. The arc is alive to *hover*: the pointer raises a large translucent
grab ring that follows it along the band (`vp4/39-a3x.png`). This is the same
signature as the dial's rim drag, and it is the second control found this run
where a gesture that should MOVE something does not land while taps that SELECT
something do. The Settings panel's grip, by contrast, drags fine — so it is not
every drag in the app.

### §17.18.1's requested measurement — **DONE, all five grounds, and the plate never moves**

§17.18.1 asked for plate-against-page on OLED black, Darkprint, Blueprint, Brown
Paper and Plain White. Run 3 had two of them. Here are the rest, measured off the
**gear button's own plate** against the page sampled in the gap beside it
(`scratchpad/plate.ps1`, sRGB relative luminance and CIE L\*):

| paper | page | plate | contrast | ΔL\* |
|---|---|---|---|---|
| OLED black *(run 3)* | `#000000` | `#0F0E10` / `#212022` | 1.09 / 1.29 : 1 | — |
| **Darkprint** | `#262B31` | `#0F0E10` | **1.35 : 1** | **13.3** |
| Brown Paper *(run 3)* | `#A36E3E` | `#0F0E10` / `#212022` | 3.62 / 4.29 : 1 | — |
| **Custom red** | `#E10619` | `#0F0E10` | **3.89 : 1** | **43.2** |
| **Blueprint** | `#2D7FC1` | `#0F0E10` | **4.54 : 1** | **47.5** |
| **Plain White** | `#FCFCFC` | `#0F0E10` | **18.77 : 1** | **94.9** |

**`#0F0E10` on every single one.** Six papers across three runs and the plate has
never once moved. §17.2's *"a background that mimics the page colour, so the
button almost disappears into the page"* is delivered only where the page happens
to be near-black anyway; on **Plain White the corner button sits at 18.77 : 1,
ΔL\* 94.9** — maximum possible contrast, the exact opposite of what the section
asks for, and the paper a note-taking app is most likely to be used on.

A ΔL\* step computed against the page, as §17.18.2 proposes, still assumes the
plate resolves from the page. It does not, on any of the six.

### The COPIC wheel — **the 49 landed. The seam reads BETTER than the numbers in six families and WORSE in two. And the wheel does not draw at a bottom dock.**

#### The rendering failure, which is the bigger finding

**At the BottomLeft dock the COPIC face draws its marker CODES with no swatch
tiles underneath them.** `vp4/50-labels3x.png` at 3x: `E71 E81 E93 E70 E51 E42
E41 E50 E40 E30 C1 C2 YR68 YR61 YR27 YR21 YR18 YR14` — two dozen labels in
near-black text lying on bare red paper, with the page's own graph-paper grid
lines running straight through where the tiles should be.

Not an animation frame: re-captured after the wheel had been open and idle, and
the pixels at four labels' own tile centres sample **`#E10619` exactly** — the
page colour, byte-for-byte — at `E71`, `E42`, `YR27` and `YR68`. Radial scans out
from the dial along three bearings clear of the dial and the page's text panel
find **no sustained tile band at all**: bearing 3° is page from 130 DIP to
1228 DIP, bearing 10° is page all the way off the screen edge, bearing 18° gives
only three 2-DIP glyph blips.

**It is the dock.** Restarting with `DialAnchor` set back to `TopLeft` and
reopening the same wheel on the same page renders it **perfectly** —
`vp4/53-half.png`, every family in full colour, codes on their tiles, identical
to this run's first capture before anything had been touched. The comparison is
like for like: same build, same page, same paper, same wheel, one changed dock.

So the viewport's bottom half now holds **two** things: §17.22's roll fix, which
is correct and which nobody can reach, and this, which is broken and which
nobody can reach either. **The dial drag not landing is what keeps both of them
out of sight**, which is the strongest argument for fixing the drag first: it is
not only a missing feature, it is the lid on an untested half of the window.

#### The seam, measured on the palette the wheel actually draws

All **49 added codes are present** (`CopicPalette.cs`, `CODE:hex` strings; a
plain regex finds 360 codes and all 49 of §11.27's list). The file is what the
screen draws — four tiles sampled off the live wheel match their entries
**byte-for-byte** (`RV99 #614d4f`, `RV42 #ffa79b`, `RV69 #81494a`,
`RV66 #a95c8d`).

§11.27's headline is a **median of 35.9 RGB units, 0.62 of a marker step**. That
number is a poor predictor of what a viewer sees, in both directions, because a
step *within* a family is mostly a lightness step while the divergence is mostly
a **hue** one — and because it averages over families that were never touched.

Measured per family, circularly (`scratchpad/seam2.py`), as "how far outside its
own family's hue band does an added code sit":

| family | kept | added | the kept codes' own band | added codes outside it |
|---|---|---|---|---|
| **RV** | 16 | 12 | ±12.3° | **RV42 at 40°**, RV69 at 31°, RV91 at 26° |
| **G** | 24 | 5 | ±32.1° | **G40 at 52°**, G82 at 42°, G85 at 34° |
| R | 27 | 3 | ±15.4° | R30 and R02 at 17° — marginal |
| B | 30 | 5 | ±19.9° | **none** |
| BG | 14 | 13 | ±29.4° | **none** |
| YG | 22 | 2 | ±58.7° | **none** |
| YR | 19 | 5 | ±9.8° | **none** |
| BV | 20 | 2 | ±31.1° | **none** |
| Y | 19 | 1 | ±14.1° | **none** |

**Six of the nine families that received codes show no seam at all** — including
**BG, which took 13 of the 49**, the joint-largest block. On those the
calibration difference is real but lands inside the spread the family already
had, and nothing looks wrong.

**Two are visibly wrong, and one of them badly.** In **RV** the kept codes hold a
tight ±12.3° band of magenta, and **`RV42 #ffa79b` sits 40° outside it — more
than three times the family's entire band.** On screen (`vp4/54-rv2x.png`, 2.2x)
it does not read as a mis-stepped pink at all: **it is a peach tile in a fan of
magentas**, and `RV69 #81494a` and `RV99 #614d4f` beside it read as browns. In
**G**, `G40 #e8edbe` at 52° is a pale khaki among greens.

**So: does it read as badly as the numbers suggest? No — it reads differently.**
Milder than 35.9 across most of the wheel, and worse than it where it goes wrong,
because the damage is concentrated in a handful of codes that leave their
family's hue band rather than spread thinly over all 308. If the user ever wants
this narrowed without re-sourcing anything, **RV42, RV69, RV99, G40 and G82 are
the five tiles that carry almost all of the visible seam** — which is a much
smaller ask than the section's "no external source will ever match" framing
implies, and it stays inside §11.27's rule, because it is a list for the user to
hand-extend `copicColors.js` with, not a dataset to swap in.

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
