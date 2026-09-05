# Quill — to-do

Created 2026-09-05 from ROADMAP.md's "Known risks, unowned", plus the two
screen checks left unreached when the machine auto-locked.

Ordered by **user-facing severity first, then by whether it can be fixed
without guessing**. Each item names what "done" means, because several of
these have a fix that is easy and a *verification* that is not.

Status key: `TODO` · `IN FLIGHT` · `NEEDS SCREEN` · `DONE` · `RULING NEEDED`

---

## Wave 1 — finish what is built but unseen  ·  DONE

| # | item | done means |
|---|---|---|
| 1.1 | **Flipped COPIC labels at a bottom dock** (`b8e3512`) — **DONE, PASS** (run 16): every label upright at both the 9 o’clock (`BottomRight` dock) and 3 o’clock (`BottomLeft` dock) seams, no double-flip. | every label upright including at the 9/3 o'clock seam; none flipping twice near the boundary |
| 1.2 | **`BottomMenu` pill under Theme = Light** (`b8e3512`) — **DONE, PASS** (run 16): resting pill `#78363C` matches the Red page’s panel (7.84:1, was 1.50:1); repaints immediately on a page turn with the menu already open, no relaunch. | resting pill shows the page's panel colour, not the pale gallery grey; repaints on a theme change *while a page is already open* |

## Wave 2 — chrome contrast: three faults, one class

**2.0 goes first: it protects the verification of everything behind it.**

| # | item | done means |
|---|---|---|
| **2.0** | **A second legacy-import path bypasses the scratch-isolation gate** — **DONE, FIXED** (run 17): reading `Load()` end to end there were **four** automatic out-of-folder reads, not one; all four are now gated on `IsIsolated`. Proven by three launches — an empty folder, run 16's exact leaking shape, and runs 7–11's shape — all ending with **0** of the user's notebook Ids present. The path closed, not the seeding widened. | a seeded scratch folder imports nothing, proven by a run that starts empty and stays empty |
| 2.1 | **Precision panel has no plate** — **DONE, FIXED** (run 17): it was excluded **deliberately**, by a measured reference (bare, no chrome), so the plate stays gone and the *marks* were re-keyed to the page instead — new `PageTheme.OnPage/OnPageMuted/PageOutline/PageIsDark`. On screen on `#FCFCFC`: headings and chips **1.091:1 → 17.957:1**, description **1.044:1 → 4.012:1**. The harness then failed the run on Blueprint/Brown Paper muted (2.16:1, pre-existing), so the muted alpha got a floor; worst over nine papers **2.164:1 → 3.006:1**. | it uses the same panel machinery Settings does |
| 2.2 | **Text editor ground 2.93:1** — **DONE, FIXED** (run 17): `#606060` was WinUI's `TextControlBackgroundFocused` (`#B31E1E1E`, dark theme) composited over the paper, because `RootGrid.RequestedTheme` is keyed to the **shell**. The box's own `Transparent` and its own ink are now pinned against WinUI's visual states, so the editor stands on the page. On screen **2.93:1 → 17.97:1**; worst of the nine papers **4.374:1**, and a 636,056-ground sweep puts the floor at **4.183:1 over the whole gamut**. §0 forced the mark to move with the ground: fixing the ground alone would have shipped `#FFFFFF` on `#FCFCFC` = **1.02:1** on a re-opened box. | clears the 3:1 floor on all nine shipped papers, measured by a harness |
| 2.3 | **Dial seat on Plain White 1.020:1** — it stands on the dial's **drop shadow**, not the page, so every figure §24/§29 computed for that case used the wrong surface | the plate formula knows the shadow exists, or the seat is lifted off it |

## Wave 3 — text colour: two defects the sweep found

| # | item | done means |
|---|---|---|
| 3.1 | **Format-bar per-run picker broken** — opening it whitens the whole box, the chosen colour never appears, committing restores the original | §25.3's *last-control-wins* holds in both directions |
| 3.2 | **Coloured box shows white in the editor from its second open onward**, on a box that never touched the per-run picker | second and later opens show the box's own colour |
| 3.3 | **Per-run colour still flattens on export** (whole-box round-trips already) | four emitters + a per-run brush on `CanvasTextLayout` |

## Wave 4 — durability, cheap and worth doing

| # | item | done means |
|---|---|---|
| 4.1 | **`File.Replace(tmp, path, null)`** — **DONE, FIXED**: the only such call was oplog compaction's swap in `SyncLog.cs`; it now passes `path + ".bak"`, matching `LibraryStore.PromoteTemp`'s existing pattern for library.json/settings.json/trash.json. The fixed name is overwritten every compaction (no accumulation) and its `.bak` suffix sits outside the `oplog.*.jsonl` glob `MergeForeign` scans, so it can never be read back as a peer's log. Code-only; not verified on screen. | a backup is taken, or the call is justified in writing |
| 4.2 | **Thumbnail pruning** — **DONE, FIXED**: `ThumbnailCache.Forget(pageId)` deletes every cached PNG variant for one page by id, called from `LibraryStore.DeleteNotebook/DeleteSection/DeletePage` (the pages the caller just spliced out of the live tree, not a rescan) and from `SyncLog`'s foreign-delete merge. No reconciliation sweep against a page-id list, so nothing can be reaped on a stale read. Code-only; not verified on screen. | deleting a page reclaims its thumbnail |
| 4.3 | **`_skipNextRightTap` can stay armed** — **DONE, FIXED**: bound with a 500ms wall-clock window (`Environment.TickCount64`, the same primitive `PenRepairDots` already uses nearby) checked in `OnRightTapped`; both arm sites now go through one `ArmSkipNextRightTap()`. Pointer-id bound rejected — `RightTappedRoutedEventArgs` carries no `PointerId` to compare against, and by the time it fires `_activePointer` is already cleared. Reset-on-gesture-end rejected — one arm site fires FROM inside a gesture-end handler itself. Code-only; not verified on screen. | the flag cannot outlive the gesture that set it |

## Wave 5 — harder, and honest about it

| # | item | done means |
|---|---|---|
| 5.1 | **Renderer failure two keystrokes away** — **DONE, FIXED** (§37): the undo/redo flash and the shape settle pulse both called `_canvas.Invalidate()` from INSIDE `DrawRegion`/`DrawShape`, i.e. with the `CanvasVirtualControl` drawing session still open — an animation repainting itself from within its own `BeginDraw`. `_flashRect` is written only by `FlashAction`, called only from `Undo()`/`Redo()`, which is exactly why it reproduces from Ctrl+Z and not from idle rendering. Both now set `_animRepaint`, consumed once per pass after every session is closed, via `DispatcherQueue.TryEnqueue` (§27's own precedent). Device loss ruled out by the shipped log itself — `IsDeviceLost` is already recorded and no line carries `DEVICE-LOST`; use-after-dispose ruled out by the exception type (a disposed Win2D wrapper raises `ObjectDisposedException`, not `COMException`). Code-only; not verified on screen. | the cause is identified. Logging and retry bounding already shipped; this is the fault itself |
| 5.2 | **Startup does not maximise** — true in both stored copies, still opens windowed | it maximises, and the reason it did not is written down |
| 5.3 | **SyncLog replay** — non-atomic cursor/device-id writes; a torn cursor can resurrect erased strokes | the write is atomic. High severity, low likelihood, needs care |

## Not code tasks — flagged, not queued

- **Self-signed MSIX cert** — public distribution needs a real certificate. A purchase, not a fix.
- **PDF import rasterises** — imported text not selectable, 2000-page cap. A feature limit, not a defect.
- **`Nearest`'s tie-break follows wheel order**, reversed by the mirror: 1.11% of sRGB queries name a different but *exactly equidistant* code. Left deliberately — the outline must land on a cell the wheel draws.

## Standing rulings the user still owes

- **COPIC seam** — RV42, RV69, RV99, G40, G82 carry almost all of it. A hand-extension, not a re-sourcing.
- **Pen colour in the pen icon** — 48 of 72 pen/paper pairs under 3:1, worst 1.01:1. Live behind `ToolWheel.PenColourInIcon`; needs an outline or a non-page-derived base.
- **Blueprint / Brown Paper dial seats** — excluded from §29's floor because the gate is on L\* not on ratio. Verified to read as clear discs, but if they ever read as absent that gate is a ruling.
- **§28.4's WinUI hover-wash** on `BottomMenu` — WinUI's own plate, not ours.
