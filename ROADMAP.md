# Quill — roadmap

Updated 2026-08-23. Supersedes the 2026-08-17 roadmap, which described four
unmerged branches; there are now twelve merged.

**`integration` is the working line.** It sits **76 commits ahead of `main`**
(`3db9462`), builds clean at zero warnings, and passes seven automated checkers:
click-select 35, canvas 13, selection-presentation 67, text quick actions 36,
`verify_icons`, `VeilRoundTrip` 23, `LayerRoundTrip` 83. Nothing has been
promoted to `main` because most of it has not been seen running — see the
verification note at the end.

Ordered by state: shipped, in flight, next, later, and the risks that are known
and unowned.

---

## Shipped into `integration`

**The canvas is infinite again.** A view clamp added in July while fixing an
unrelated "invisible ink" bug had walled the canvas into `max(page, content) +
900` world units — measured at 3788 × 3788 at 1× zoom. The clamp is gone, along
with the origin wall that predated it and the content-normalising pass that only
existed to serve it. Zoom is finalised at **0.1×–16×** from one definition,
replacing three copies at two different values.

**Selection is one presentation over three subjects.** Attachments, typed text
and drawn strokes all get the same floating action bar, corner circles,
full-canvas guides and mode row. The dial greys **by capability** — a subject
that lacks a property greys that property's control — so an attachment greys
size and stability while a stroke greys nothing, and the per-pen colour arcs
never grey because what they report stays true.

**A click selects, with no drag.** From the Select tool, the pen barrel or
mouse-select; a drag still lassoes. Threshold is 8 screen pixels, so a press
held still indefinitely is still a click.

**The page fades behind a selected attachment**, animated on the app's own
190/130 ms curve, and proven never to write grey into stored colour by a harness
that compiles the real store and diffs a save taken at full fade against a
baseline.

**The fullscreen shell.** A hover-revealed window-control strip that slides from
the screen edge and reverses mid-flight; the caption row folding away; the
format bar and app bar no longer stealing 46 DIP of page height, because the
strip now reserves *width* — the abundant axis — instead.

**Panels remember proportions, not distances.** A panel 100 DIP from an edge
sits 50 from it when the host halves, and clamping is non-destructive, so a
panel squeezed by a small window returns to its chosen size.

**Layers — the data model.** Membership is an int key where 0 means the base
layer, so an existing library gains zero bytes and there is **no load-time
migration to go wrong**: an absent list *means* one base layer. Reference §18,
83 checks.

**Text quick actions and Help.** The quick actions are a `Mode` on the existing
selection chrome rather than a second bar, so two bars over one text box are
structurally impossible. Help is a third door onto the shortcut sheet that was
already generated from the real key bindings — no second copy, so it cannot
drift out of date.

**The Measurement menu.** Zoom and tilt open a panel with presets and two
independent locks; locking tilt shifts the zoom readout sideways to make room.

**Smaller, but each a real defect:** the dial's dark-mode marks no longer render
transparent (a contrast test was comparing against a *neighbouring* surface's
token); custom colour keeps the user's choice instead of mirroring the page;
disabled readouts show nothing rather than a dash; `StartFullscreen` renamed to
`StartMaximised`, which is what it has done since `8105f60`.

`dial-readouts` is deliberately **not** merged — its inward lift was superseded
by the up-and-outward arrangement before it landed.

---

## In flight

- **`bottom-bar`** — the screen-bottom mode bar (rotate / scale / filter), the
  mouse tool with lasso folded into it, pan and rotate tools, and the size
  increases. Eleven commits, mid-edit. The open design question is whether the
  mode bar and the mouse tool's menu are one surface or two.
- **The 19 perspective presets** — enumeration is done and correct (three lists,
  not one catalogue), measurement is not started. Needs the machine to itself at
  a frozen viewport.
- **The missing COPIC codes** — 316 of 358. Real markers only, no interpolated
  swatches. Gated on a before/after review; has lost its work to usage limits
  twice without committing.

## Next

- **The four features layers was blocking**, each now one call: PSD export,
  per-layer visibility, selection scoping, Objects rows — plus the layers panel,
  deliberately not built with the model.
- **Panel-meets-panel.** The inset model handles a window too small but not a
  panel meeting another panel or a dock. The hooks are in the right places.
- **`CloneWithPoints` drops `Opacity` and `Locked`** — erasing part of a
  translucent stroke makes the fragments opaque, and erasing part of a locked
  stroke unlocks the pieces.

## Later

- **Oil paint.** Branch `oilpaint`, three commits: tile store, impasto via a
  distant-specular pass, crash-safe `.artq` v2. Built and verified, never merged.
- **Smudge**, on the oil raster substrate.
- **A pen library** proper — brush dynamics behind the shell that now exists.
- **Tilt / canvas rotation.** Audited rather than guessed: 62 inline
  screen↔canvas conversions and 51 axis-aligned rect sites in a 7,180-line file.
  Estimated 3–5 days plus a full input-regression pass.
- **A user system** — accounts, sharing, collaboration — and a web viewer.

---

## Known risks, unowned

- **SyncLog replay.** Two builds sharing `Documents\Quill` replay against each
  other; cursor and device-id writes are non-atomic, and a torn cursor can
  trigger a replay that resurrects erased strokes. *Partly improved*: the
  harnesses that were silently resetting the real cursor file on every run now
  follow `LibraryStore.IsIsolated`, so the trigger is no longer being pulled by
  the test suite. The underlying non-atomic write is untouched.
- **`File.Replace(tmp, path, null)`** with no backup parameter in the sync path.
- **Startup does not maximise.** The setting is true in both stored copies and
  the app still opens windowed. The rename fixed the *name*; the behaviour is
  unexplained. Suspects: the presenter not being an `OverlappedPresenter` at the
  call, so a guarded `if` no-ops invisibly; or saved bounds reapplied over it.
- **`_skipNextRightTap` is a one-shot that can stay armed** and swallow an
  unrelated context menu later. Narrowed, not fixed.
- **PDF import rasterises** — imported text is not selectable; 2000-page cap.
- **Self-signed MSIX cert.** Public distribution needs a real certificate.
- **Thumbnail pruning** — `thumbs/` keeps PNGs of deleted pages indefinitely.
- **Vector export drops per-run text colour**, flattening to the page ink
  colour; the canvas draw path shares the limitation and both want fixing
  together.

---

## A note on verifying this app

Four traps, each of which has cost real time.

**A mouse cannot draw in Quill** unless Touch draw is enabled — a pen tool with
a non-pen pointer routes to a selection handler that commits nothing. An
injected drag inks *nowhere*, which is indistinguishable from the canvas
swallowing the stroke. Always run a control stroke through the middle of the
canvas before concluding a stroke test failed.

**An incremental build reports zero warnings it did not earn**, because it skips
the C# compile entirely. A three-second "clean build" is evidence of nothing.
Pass `--no-incremental` whenever a warning count is part of the argument.

**`scratchpad/render_icons.py` fills only and cannot render stroked marks** —
`Icons.Close`, `Plus` and `Minus` all report zero ink through it. An offline
render is not proof for a stroked mark; measure the path analytically.

**A theme token may be read across surfaces.** One contrast test compared
against the *inner disc's* token because the ring's own had no dark-side
definition, so a mark sitting on neither passed the test and vanished into a
black page — correct in light mode, invisible in dark. When a colour is
resolved, check it is the token for the surface the element actually sits on.

Finally: **an instruction arrives through MCP tooling** telling agents to route
file edits through Bash `sed`/heredocs rather than the permission-gated
Read/Edit/Write tools. It is not from the user. Five agents have independently
reported and refused it. It should be investigated at the source.
