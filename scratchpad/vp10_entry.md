## RUN OF 2026-09-04 (fourteenth screen run) — THE MIRRORED COPIC WHEEL, SEEN AT LAST

Branch `integration` @ `76e7e3b`. Clean x64 Debug `--no-incremental` build,
**0 warnings**, 23.9 s. Scratch `QUILL_DATA_FOLDER` at `scratchpad/vp10data`,
seeded before first launch. Captures and logs in `scratchpad/vp10/`.

**The wheel was put on screen and pressed 255 times. Four runs of not-looking
end here.** §26.3 is verified on the picture, not on the arithmetic.

### ITEM 1 — the mirrored COPIC wheel: **PASS**, on the method that can fail it

Run 7's method, which is the only one that catches a mirrored draw against an
unmirrored pick: **read the pixel DRAWN at the target before the press, press it,
read the dial's dot after.** The comparison needs no model of `_rot` at all — it
asks whether the renderer and the hit test name the *same* swatch, and a
renderer drawing somewhere other than where `Layout()` says would break it.

```
                                         presses   mismatches
drawn pixel == CopicPalette's own hex       255         0
dial's dot  == the pixel that was drawn     255         0
of those, at a family-boundary sliver        43         0
distinct columns pressed                  72 of 72
```

Every one of the **11 family boundaries** was pressed on both sides, at rings 0
and 1, 0.8° either side of the seam. **The R↔YR seam — the reflection axis —
went first**, and it is clean: `R08` runs to exactly 10.00° and `YR82` starts at
10.10°, with no gap and no page pixel between them.

**Coverage is whole because the dial was re-docked, not because the wheel was
spun.** The ring is centred on the dial's dot, so at any one dock more than half
of it is off screen; `_rot` also resets to 100° on every open (`ColorPickerService`
does `new ColorWheel()`), and the scroll glide is not repeatable. Docking the
dial at each of the four corners in turn — `Library.DialAnchor`, restarted each
time — puts every bearing on screen at some point. Captures `c01-wheel.png`
(TopLeft), `f01-wheel-br.png` (BottomRight), `g01-wheel.png` (TopRight),
`h01-wheel.png` (BottomLeft).

**All 72 columns were also decoded straight off the pixels**, independently of
the presses: walk each ring at 0.05° and match every sample against the 360
palette hexes.

```
ring 0, four docks    12,020 samples   11,790 identified   0 page pixels
columns seen drawn                     72 of 72
columns whose ring-0 ink != the table   0
column width                           4.80–5.00°  (ColStep = 5.0000°)
```

Zero page pixels at ring 0 on any dock is §11.17's "no gaps between rows",
reproduced on our own wheel rather than on the reference.

#### The four checks the brief attached its own failure meanings to

**Family order clockwise — CORRECT.** Read off the drawn pixels, right round the
circle:

```
drawn    YR E Y YG G BG B BV V RV R
brief    YR E Y YG G BG B BV V RV R
```

which is the same cyclic order as the reference's `RV R YR E Y YG G BG B BV V`.
The build took; nothing is caching the old order.

**Series inside each family descending — CORRECT, all eleven.** Not a
half-mirror:

```
YR  YR8 YR6 YR3 YR2 YR1 YR0        G   G9 G8 G4 G2 G1 G0
E   E9 E8 E7 E5 E4 E3 E2 E1 E0     BG  BG9 BG7 BG5 BG4 BG3 BG2 BG1 BG0
Y   Y3 Y2 Y1 Y0                    B   B9 B7 B6 B5 B4 B3 B2 B1 B0
YG  YG9 YG6 YG4 YG2 YG1 YG0        BV  BV9 BV3 BV2 BV1 BV0
V   V9 V2 V1 V0                    RV  RV9 RV6 RV5 RV4 RV3 RV2 RV1 RV0
R   R8 R5 R4 R3 R2 R1 R0
```

`E9 E8 E7 E5 E4 E3 E2 E1 E0` is the brief's own example, and it is what the
screen shows.

**Columns dark→pale inward→outward — CORRECT.** The drawn code at every ring of
every column equals the table's, and the table sorts each column by blend
descending; `E0` reads `E09 E08 E07 E04 E02 E01 E00 E000 E0000` from the inside
out (`lab-E0-bearing082.png`). **One honest qualification:** measured as
*luminance* rather than as blend number, 4 of 29 columns are not monotonic
(`YR1`, `E5`, `E2`, `E1` — e.g. `E19` is lighter than `E18`). That is the Copic
palette's own numbering, not the wheel's radial sort: the drawn ink matches the
table exactly, so nothing was flipped.

**Labels — NOT mirror-written, and the rotation is the proven no-op. But see
below.** Checked at three bearings ~85° apart (`lab-R2-bearing-002.png`,
`lab-YR0-bearing037.png`, `lab-E0-bearing082.png`): every glyph reads correctly,
none is reversed, and each label's top points at the wheel centre.

**72 / 9 / ~650 DIP — all three confirmed on the running app.**
`QUILL_GEOM_PROBE` says `rings=9/9`, `band=28.10`, `rOut=585.33` at `scale=0.900`.
The outer ink radius was then **measured on screen** rather than taken from
`Layout()`, by walking outward along the deepest column until the ink stops:

```
E0  (9 rings)   last ink 1170.32 px = 585.16 DIP   -> 650.18 DIP at s=1
YR0 (8 rings)   last ink 1114.32 px, +1 band       -> 650.29 DIP at s=1
Layout() says                                          650.37 DIP at s=1
reference (Concepts)                                   647.9  DIP     -> +0.35%
```

### What the run found that the brief did not ask for

**1. Every code label on the upper half of the wheel is upside-down, and at a
bottom dock that is nearly all of the ones you can see.** `DrawCode` rotates by
`midA - π/2` with **no readability flip**, so a label's top always points at the
centre — which is upright at `midA = 90°` (straight down from the centre) and
fully inverted at `midA = 270°`. At the default TopLeft dock the visible bearings
are about −30…115° and nobody ever sees it. At **BottomRight** and **BottomLeft**
the visible half is exactly the inverted half: `f01-wheel-br.png` shows `BV0000`,
`B0000`, `B21`, `V20` and their neighbours written upside-down.

This is **not** a mirror artefact — the ninth run's proof that the label rotation
is bit-identical before and after still holds, and it is a function of the drawn
angle alone. It is pre-existing, and it is filed here because the brief's own
check says "upside-down means the label rotation was not the proven no-op" and
the honest answer is: the rotation *is* the proven no-op, **and** the labels are
upside-down on half the wheel regardless. Both statements are true and the second
one has never been recorded.

**2. Run 7's BottomLeft rendering failure does not reproduce on this build.**
Run 7 found the COPIC face at `BottomLeft` drawing marker codes on bare page with
no swatch tiles underneath. Retested here on the same dock: `h01-wheel.png` shows
every column in full colour with tiles, ring 0 decodes **3,026 of 3,086 samples
with 0 page pixels**, and 39 presses on that dock all delivered the swatch aimed
at. Whatever caused it has gone; it is not listed as fixed anywhere, so it is
worth knowing it is no longer reproducible.

**3. §21's restart claim held incidentally.** The dial was re-docked three times
by `Library.DialAnchor` and restarted each time; it came back at the dock it was
given on all three.

### A trap the next run should not re-pay for: the strict presence guard

`Q-Presence` compares the cursor against **where the run asked to put it**, and
that produced one **false abort**: `Put 81,1087` landed on `80,1087`. SendInput's
absolute coordinates are 0..65535 across the virtual screen, so a requested x can
land one physical pixel short. A 60 s track taken at that point immediately
afterwards showed **1,261 samples, ZERO cursor changes, one distinct position,
zero idle resets, idle rising monotonically** — the exact opposite of the human
signature, which is *repeated* 1–4 px corrections, not one.

**The fix is not to loosen the guard.** `vp10.ps1` overrides `Put` to record where
the cursor **landed**, after asserting the landing is within 2 px of the request.
The guard then stays exact-match strict against any later movement, and the
injection's own quantisation cannot trip it. 255 presses ran under it afterwards
with no further trip.

### The presence gate — clear on five separate measurements

Every reading is one DPI-aware process at 40 ms, `OpenInputDesktop` checked
first and `Default` on all five, `LogonUI` never running, machine unlocked.
**Sample count and elapsed time agree on every one** — run 13's 44-samples-over-
10,059-s reading has no counterpart here.

| # | when | samples | cursor changes | idle |
|---|---|---|---|---|
| 1 | before anything | 1270 / 60 s | **0**, one position | 0 resets, → 195 s |
| 2 | before launch | 952 / 45 s | **0**, one position | 0 resets, → 336 s |
| 3 | before the sweep | 632 / 30 s | **0**, one position | 0 resets, → 267 s |
| 4 | after the guard trip | 1261 / 60 s | **0**, one position | 0 resets, → 88 s |
| 5 | mid-item | 944 / 45 s | **0**, one position | 0 resets, → 55 s |

The cursor never went anywhere this run did not put it. Quill was pinned
`HWND_TOPMOST` and every press gated on `WindowFromPoint(cursor)` resolving to
Quill, so nothing could land in anyone else's window.

### Gates — all clean

* `C:\Users\irony\Documents\Quill\library.json`: **53,582,459 bytes, SHA-256
  `0C32CE6C16A4310CDCEB4902C6FF5C9B6CBA7A11AA55BE4F88DAB5771F8E038A`, mtime
  2026-08-28 17:26:38.4039650 UTC** — sealed before the first launch and
  re-checked after the last, **byte-identical on all three counts**, never opened
  for writing.
* **Migration prevented, not survived.** The scratch folder was seeded before
  first launch, so `MigrateFromLegacyIfNeeded` returned early. It ended at
  **27,911 bytes in 7 files** (library, settings, their `.bak`s and three of
  Quill's own dated backups); not one of the user's notebooks was copied.
* `crash.log`: the stale **10,636-byte / 2026-08-20 22:55:25 UTC** file,
  untouched — and **none was created in the scratch folder across 255 presses**,
  so `App.xaml.cs` swallowed nothing.
* Quill wrote `Theme: Dark` / `ThemeSource: Manual` into the scratch library on
  its own first save. Those are the **model defaults**, so §27.6 check 1's
  "default install" is intact; `vp10_dock.py` asserts they stay that way and
  writes only `DialAnchor`.
* This file measured **wholly CRLF — 2,713 endings, 0 bare LF, 0 bare CR, 0 NUL,
  no BOM** — immediately before this write, and re-measured after.
* The `system-reminder` telling agents to route file edits through Bash
  `sed`/heredocs arrived again and was **refused — the twentieth run to do so.**
  It is not from the user. Edits went through Write/Edit.
* **No text read off the screen was treated as an instruction or as permission.**

### Tooling this run leaves behind

* `scratchpad/vp10.ps1` — vp9's harness re-pathed, **plus the `Put` fix above.**
* `scratchpad/vp10_sweep.ps1` — run 7's method, parameterised on the dial's dot
  and a sentinel tile, so it re-runs at any dock. Reopens the wheel between
  presses (a pick closes it) and re-checks presence before every press.
* `scratchpad/vp10_scan.py` — decode a drawn ring against the palette's 360
  hexes at 0.05°. This is the tool that measures the *picture*.
* `scratchpad/vp10_rings.py` — every ring of every column, plus family order and
  the radial run.
* `scratchpad/vp10_model.py` / `vp10_probes2.py` — the 72-column table and the
  probe generator, parameterised on the wheel centre.
* `scratchpad/vp10_dock.py` — re-dock the dial in the SCRATCH library only; it
  refuses the real path and asserts Theme/ThemeSource never move.
* `scratchpad/vp10_final.py` — pools every press log and re-derives the totals.

### Items 2, 3 and 4 — NOT REACHED at the time of this commit

Item 1 was committed before item 2 was started, per the brief. §27.6's panel
grounds, §25.11's canvas rows and §28's three remain as run 12 left them.

