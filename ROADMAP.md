# Quill — roadmap

Updated 2026-09-07. Supersedes the 2026-09-05 roadmap, which recorded 204
commits stranded on `integration` and `oilpaint` adrift on its own branch.

**Both of those are closed.** `main` and `integration` are the same commit,
`0e564ba`, pushed to GitHub — **232 commits**, the first promotion since
`3db9462`. `oilpaint` is merged. The tree builds clean at zero warnings with
`--no-incremental` and **all ten harnesses build and pass**: `CloneRoundTrip`,
`ExportRotRoundTrip`, `HandleProof`, `LayerRoundTrip`, `PanelProof`,
`PaperProof`, `SeatProof`, `TextColourRoundTrip`, `TextRotRoundTrip`,
`VeilRoundTrip`.

**What changed most is not a feature: it is that the work is now being looked
at, and that the checkers are now checked.** Twenty-four screen runs have driven
the real app. Several items were green in every automated test and wrong on screen —
and, worse, **five of the ten harnesses had silently stopped compiling**, each
broken by this cycle's own changes. A project that fails to build exits non-zero
exactly like a check that fails, so a broken harness is indistinguishable from a
suite nobody ran. Two "defects" on the last list turned out not to exist at all.

**Nothing is promoted on a build result alone, and "all green" now means the
green was earned** — every colour harness links the shipping source rather than
modelling it, and `TextColourRoundTrip` carries five negative controls that must
reproduce the defect they guard.

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

- **Oil paint has never been painted on.** `oilpaint` is merged (`0e564ba`) and
  the merge is verified only in the negative: the build is clean and all ten
  harnesses pass, but **none of them exercises paint**, so what is established is
  that the merge broke nothing already covered — not that the engine works.
  Nobody has laid a stroke on this build. **This is the first thing to do.**
  Two drifts were found and resolved on the way in, which is the shape of the
  risk: oilpaint still carried the pre-consolidation zoom clamp (0.1f/8f, with
  8f superseded by 16f), and it set `_contentMaxDirty`, a field deleted with the
  content-normalising pass when the canvas was made infinite. A third such
  reference may sit in a path the harnesses do not reach.
- **Row 3.3 is PARTIAL and one link is unseen** — the `SetText` inside `Loaded`.
  `scratchpad/vp20data` is seeded for that single launch, with the expected
  reading for four boxes written down in advance, including a `#FFFFFF` box in
  the shape 38 real notes carry: it must render readable, not white on white.
- **The 19 perspective presets** — all measured, worst residual 0.0084 under a
  leave-one-out control. Enumeration and measurement done; not yet built in.

## Next

- **The Precision panel has no plate at all** (see risks) — the machinery exists
  and this one panel is not using it.
- **Layers: the panel landed and the round trip finally ran** (run 24, §49.5–49.7).
  Per-layer visibility and opacity are reachable, hiding takes ink off the page
  and out of reach of the selection tools, and §49.3's promise — that the
  multiplier is applied at draw time and never written back — is **measured on
  disk**, not asserted. Two thumbnail faults were found on the way and fixed: the
  gallery render had never heard of layers, and the cache key in front of it
  could not have told the difference if it had. **What is still blocked:** PSD
  export (no writer), and **add / rename / reorder / delete**, without which
  nothing in the app can make a second layer — so on a real page the panel has
  one row. That is the next piece of layer work, and it is the one that makes the
  panel worth opening.
- **Panel-meets-panel.** The inset model handles a small window but not a panel
  meeting another panel or a dock.
- **Per-run text colour: PARTIAL, one link unseen.** All four emitters are
  built and measured (`TextColourRoundTrip` 16 to 38 checks, five negative
  controls); only the `SetText` inside `Loaded` is unverified on screen. The
  finding that shaped it is worth carrying: **all 106 stored notes carry an
  explicit run colour nobody picked** — 68 `#FAF9F5`, 38 `#FFFFFF`, none with
  `\cf0` — so honouring run colour naively would have frozen every note in the
  ink of the page it was typed on and made all 106 invisible once that page went
  white. `IsMachineInk` folds those back to the box's answer.

*Two entries were removed from this section on 2026-09-05 because they were
already done and had been carried forward unverified — the exact failure this
file's own verification note warns about. `CloneWithPoints` drops nothing:
`3bf5a4a` made a copy carry every field but the Id (§18.10). And `bottom-bar`
is not "eleven commits, mid-edit" — it holds zero commits that are not already
in `integration`. Both were inherited from the 2026-08-23 roadmap and restated
rather than checked.*

## Later

- **Smudge**, on the oil raster substrate — which is merged now, so this is
  unblocked for the first time.
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
- ~~**Startup does not maximise.**~~ **Closed, not reproducible (§48).** Measured
  on screen ten ways, including with the user's own `settings.json` and their real
  53 MB library: it maximises. All four suspects are innocent — the presenter
  guard (logging never fired), saved bounds (the restore rect *is* the stored
  `196,196 2160x1313`, maximised on top of it), §38's ordering mechanism
  (**falsified**: the literal pre-`8202615` one-liner maximises too, so that fix
  was never load-bearing), and the launcher's show-command (a `.lnk` with
  `windowstyle=1` maximises anyway). No harness covers window placement.
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
