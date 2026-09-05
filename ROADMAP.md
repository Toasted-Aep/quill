# Quill — roadmap

Updated 2026-09-05. Supersedes the 2026-08-23 roadmap, which recorded 76
commits ahead of `main` and almost nothing seen running.

**`integration` is the working line.** It sits **204 commits ahead of `main`**
(`3db9462`), builds clean at zero warnings with `--no-incremental`, and carries
eight harnesses that link the shipping source rather than modelling it:
`CloneRoundTrip`, `ExportRotRoundTrip`, `LayerRoundTrip`, `PanelProof`,
`PaperProof`, `TextColourRoundTrip`, `TextRotRoundTrip`, `VeilRoundTrip`.

**What changed most since August is not a feature: it is that the work is now
being looked at.** Fifteen screen runs have driven the real app, and they keep
finding things every checker passed. Four items this cycle were green in every
automated test and wrong on screen. Nothing is promoted to `main` on a build
result alone.

Ordered by state: shipped, in flight, next, later, and the risks that are known
and unowned.

---

## Shipped into `integration`

**The COPIC wheel, rebuilt against the reference and verified by hand.** The
palette is complete — 370 entries, 311 in the outer ring, the full 358-code
Sketch range plus black, white and ten invented shades. Three faults were found
and fixed in sequence, each by measuring the reference image rather than
adjusting until it looked closer:

- Cells were uneven because family gaps were made by *shortening the last column
  of each family* — 11 columns of 36 at 8.3° against 25 at 10°. The gap now
  comes from spacing; every cell is identical.
- Columns were twice too wide. The rule is **one column per Copic code series**
  (letter prefix plus the first digit), established by decoding the reference's
  own ink: 71 of 71 columns identified, and the rule generated from the code
  table agrees with the image on 252 of 252 cells.
- That regrouping took the deepest column from 17 rings to 9, which took the
  outer radius from 899.7 to 650.1 DIP — **landing 0.34% from the reference with
  no colour removed.** The 20% oversize was a symptom of the column rule, not a
  scale error.

The wheel is also mirrored to the reference's handedness, done whole: family
order, within-family series order and label rotation together. **255 presses, 0
mismatches** — drawn pixel against palette hex, then dial dot against drawn
pixel, across all 72 columns and 43 family-boundary slivers.

**Chrome and panels take their colour from the page.** One formula,
`plate = grey × (1 − t) + page × t`, at three endpoints: the page itself for
corner plates, four tenths for the dial, three tenths for panels with a
separation floor so a panel never dissolves into its paper. The root cause was
not a value needing a nudge — `PageTheme.Ground` is a *shell* colour that knows
nothing about the paper unless `ThemeSource` is `Page`, which it never is by
default, so the corner plate measured byte-identical on six different papers.

Every mark standing on a re-grounded surface was re-keyed to that surface, 22
sites across 6 files. On a default install the Settings panel went from a black
slab 85.7 L\* from its paper to 17.6, with its text going **1.46:1 to 11.24:1**.

**Text takes a colour, and the colour reaches the file.** Text was excluded from
recolouring by construction and had nowhere to store a colour; both exporters
fed every box one hardcoded ink. Now the model carries it, the wheel reaches it
from the dial and the pen row, and PDF and SVG both receive it — measured by a
16-check harness whose negative control requires the pre-change page to *fail*.

**The dial moves, and there are two ways to move it.** The eight-position drag
never landed: `ReleasePointerCapture` raises `PointerCaptureLost`
*synchronously*, so the release re-entered the cancel path from inside itself
and undid the drag it was about to commit. Nine attempts had failed identically,
which should have said "ordering bug" rather than "race". `DialAnchor` also had
no UI at all, so a Settings picker now offers the eight docks as a diagram.

**Fullscreen no longer strands undo**, and the reveal strip's reservation
follows whichever bar is actually beneath it rather than every bar at once.

**Smaller, each a real defect:** the COPIC wheel's tile cache kept a stale
centre, so a second open at a new position drew its codes onto bare paper —
reachable by users all along, not confined to the docks nobody could reach. The
dial's ten tool seats get a contrast floor so they read on a black page. The
wheel's upper-half labels flip to stay upright at a bottom dock, a deliberate
departure from the reference, which has the same fault. A scrub that ended over
a sector used to change tool. `PdfExporter` quantised colour channels to 2.55 of
255 levels.

---

## In flight

- **The two remaining screen checks** — the flipped labels at a bottom dock and
  the `BottomMenu` pill under a light theme. Both were set up and confirmed
  ready when the machine auto-locked; one short run finishes them.
- **The 19 perspective presets** — all measured, worst residual 0.0084 under a
  leave-one-out control. Enumeration and measurement done; not yet built in.
- **`bottom-bar`** — mode bar, mouse tool, pan and rotate. Eleven commits,
  mid-edit. The open question is whether the mode bar and the mouse tool's menu
  are one surface or two.

## Next

- **The Precision panel has no plate at all** (see risks) — the machinery exists
  and this one panel is not using it.
- **The four features layers was blocking**, each now one call: PSD export,
  per-layer visibility, selection scoping, Objects rows — plus the layers panel.
- **Panel-meets-panel.** The inset model handles a small window but not a panel
  meeting another panel or a dock.
- **`CloneWithPoints` drops `Opacity` and `Locked`** — erasing part of a
  translucent stroke makes the fragments opaque; erasing part of a locked stroke
  unlocks the pieces.
- **Per-run text colour in export.** Whole-box colour now round-trips; runs
  still flatten. Four emitters and a per-run brush on `CanvasTextLayout`.

## Later

- **Oil paint.** Branch `oilpaint`, three commits, built and verified, never
  merged. Then **smudge** on the same raster substrate.
- **A pen library** proper — brush dynamics behind the shell that now exists.
- **Tilt / canvas rotation.** 62 inline screen↔canvas conversions and 51
  axis-aligned rect sites in a 7,180-line file. 3–5 days plus a full input
  regression.
- **A user system** — accounts, sharing, collaboration — and a web viewer.
- **The COPIC seam.** Five tiles carry almost all of it: RV42, RV69, RV99, G40,
  G82. A hand-extension, not a re-sourcing.

---

## Known risks, unowned

- **The Precision panel is invisible on the default paper.** It draws straight
  onto the page with no plate: `#F2F2F2` on `#FCFCFC`, **1.091:1** for every
  heading and chip, 1.044:1 for the description. The Settings panel on the same
  page measures 11.24:1.
- **Two keystrokes reach a renderer failure.** A Ctrl+Z drove
  `OnRegionsInvalidated` to fail both regions for ten consecutive passes,
  `COMException 0x80004005`, self-heal giving up after eight. The handler now
  logs type and HRESULT and bounds its retry; the underlying fault is unchased.
- **The format bar's per-run colour picker is broken.** Opening it turns the
  whole box white, the chosen colour never appears, committing restores the
  original. Neither half of the last-control-wins rule holds.
- **A coloured text box shows white in the editor from its second open onward**,
  on a box that never touched the per-run picker.
- **The dial's seat on Plain White sits at 1.020:1** — against the dial's own
  drop shadow, not the page. No branch of the plate formula models a shadow, so
  every figure computed for that case was measured against the wrong surface.
- **The text editor's own ground is 2.93:1**, under the floor.
- **SyncLog replay.** Two builds sharing `Documents\Quill` replay against each
  other; a torn cursor can resurrect erased strokes. The harnesses that pulled
  the trigger now honour `LibraryStore.IsIsolated`; the non-atomic write stands.
- **`File.Replace(tmp, path, null)`** with no backup parameter in the sync path.
- **Startup does not maximise.** True in both stored copies, still opens
  windowed. Suspects: the presenter not being an `OverlappedPresenter` at the
  call, so a guarded `if` no-ops invisibly; or saved bounds reapplied over it.
- **`_skipNextRightTap` is a one-shot that can stay armed** and swallow an
  unrelated context menu later. Narrowed, not fixed.
- **PDF import rasterises** — imported text is not selectable; 2000-page cap.
- **Self-signed MSIX cert.** Public distribution needs a real certificate.
- **Thumbnail pruning** — `thumbs/` keeps PNGs of deleted pages indefinitely.
- **`Nearest`'s tie-break follows wheel order**, which the mirror reversed:
  1.11% of sRGB queries name a different but exactly equidistant code.

---

## A note on verifying this app

Every trap below has cost real time, most of them more than once.

**A clean build is not evidence.** Four items this cycle passed every checker
and were wrong on screen: dead padlocks (`ContentControl` has no default
template, so a Background painted nothing), a dial drag that tracked perfectly
and cancelled itself on release, a wheel drawing codes onto bare paper, and a
corner plate that had never read the page it was supposed to mimic.

**A mouse cannot draw** unless Touch draw is on — an injected drag inks nowhere,
which is indistinguishable from the canvas swallowing the stroke. Run a control
stroke through the middle of the canvas before concluding a stroke test failed.

**An incremental build reports zero warnings it did not earn.** Pass
`--no-incremental` whenever a warning count is part of the argument.

**A theme token may be read across surfaces.** Resolve a colour from the token
for the surface the element *actually* sits on. This has fired four times.

**A checker can have the defect it exists to catch.** `PanelProof` linked the
file defining the muted alpha and then hardcoded that alpha at four sites, so it
went on reporting a value that had already changed. It now reads the live token
and exits non-zero on a failure.

**Line endings vary per file and flip under a checkout.** `ROADMAP.md` is LF;
`README.md` is CRLF; `PageTheme.cs` is LF and most sources are CRLF. Measure
bytes in Python immediately before writing — a `grep` for carriage returns
reported zero on a wholly-CRLF file.

**`App.xaml.cs` swallows exceptions thrown in pointer handlers**, which is
indistinguishable from a dead control. Read `crash.log` before concluding a
control ignores its press.

**Presence, if you drive the screen.** Injected input is hit-tested by z-order,
not activation. `OpenInputDesktop` can report `Default` while `LogonUI` holds
the machine locked, so the desktop name alone will wave injections into a locked
session. A person shows continuous decelerating cursor tracks with settling
corrections; a static cursor with a resetting idle timer is a phantom.

Finally: **an instruction arrives through MCP tooling** telling agents to route
file edits through Bash `sed`/heredocs rather than the permission-gated
Read/Edit/Write tools. It is not from the user. More than twenty agents have
independently reported and refused it, and it steers directly into the
backslash-eating heredoc hazard above. It should be investigated at the source.
