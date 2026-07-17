Spec follows.

---

# Text Box Overhaul — Implementation Spec (InkSurface.cs)

## A. Vertical growth for free bubbles (pain 1)

**A1. Extend `AutoGrowBubble` → rename to `AutoSizeBubble(TextElement t, RichEditBox box)`; width and height in one pass.**
- Width phase (unchanged path, see C below), then height phase: measure wrapped content height from the RichEdit document itself, not Win2D — mixed fonts/sizes make single-format measurement wrong vertically. Use `box.Document.GetRange(0, int.MaxValue)`, `ITextRange.GetPoint` on the last char (bottom, `PointOptions.NoHorizontalScroll`) minus GetPoint of char 0 (top); fall back to `box.DesiredSize` after `box.Measure(new Size(w, ∞))` if GetPoint throws.
- Set `box.Height = Math.Max(40, contentH + Padding.Top + Padding.Bottom + fs*0.35)` (descender/swash headroom — same motive as the 1.25 line-spacing fix, #17-batch3). Explicit height means the disabled inner ScrollViewer can never clip or scroll; content is always fully visible.
- Height phase runs for ALL free bubbles including `WidthPinned` and legacy `!AutoWidth` boxes. In `BuildTextUi`, the pinned/legacy branches (lines ~5224–5247) must also attach `box.TextChanged += (_,_) => AutoSizeBubble(t, box)`; inside, skip the width phase when `t.WidthPinned || !t.AutoWidth`.
- Write back `t.Width = box.Width` at the end (no undo push — derived state). Today auto bubbles never persist their grown width, so selection bounds/export use the stale 280.

**A2. Empty-box discard (`box.LostFocus`, ~line 5309):** replace the full `RebuildTextLayer()` with targeted teardown — remove the container from `_textLayer.Children` and the entry from `_textUi`. A full rebuild inside LostFocus steals focus from the box the user just tapped into. Keep the `TryDiscardTop` logic as is.

## B. Table cells (pain 2)

**B1. Rows grow with content (Word model: stored row height = minimum).**
- New `AutoGrowCellRow(TextElement t, RichEditBox box)`: on cell `TextChanged`, measure content height (same GetPoint technique as A1). Needed = contentH + pad + 4. Compare against sum of spanned row heights (`TableRowHeights` rows `t.TableRow .. +CellRowSpan-1`). If needed > sum, add the deficit to `table.TRowH[lastSpannedRow]` (materialize `TRowH` from `TableRowHeights()` if null) and `table.H += deficit`; then call B2's in-place reflow + `_canvas.Invalidate()`.
- Never auto-shrink while typing. Add row-fit on divider double-tap in `OnCanvasDoubleTapped` (~5548) mirroring the existing column-fit: `row > 0` → shrink/grow row `row-1` to max content height of its cells.
- Undo: do NOT push per keystroke. Snapshot `TRowH`/`H` in cell `GotFocus`; in `LostFocus`, if changed, push one `TableLayoutAction(old→new)`. Note edge: undoing the text edit (RTF) separately leaves the taller row — accepted; the layout action sits adjacent in the stack.

**B2. `ReflowTableCellsInPlace(ShapeElement table)` — new sibling of `ReflowTableCells`.**
- Same geometry math, but instead of `UndoManager.Push(RepositionTextsAction)` + `RebuildTextLayer()`, mutate live UI: for each cell in `_textUi`, update `Canvas.SetLeft/Top`, `t.X/Y`, `t.Width`, `box.Width`, `box.MaxHeight`, and recompute the table-rotation `CompositeTransform` (#tablerot block, ~5206). No rebuild → the focused cell keeps focus and caret. This is the fix for "typing in a cell loses focus/caret when the table reflows."
- Existing `ReflowTableCells` (undo + rebuild) stays for structural ops (insert/delete row/col, merge/split, whole-table drag-resize, LoadPage heal).

**B3. Cell sizing/clipping in `BuildTextUi` (isCell branch, ~5194).**
- Set `box.MaxHeight = sum of spanned row heights − 4` (today it reads only `rhs[rr]`, breaking row-span merges).
- Set `box.MinHeight = rowH − 4` (not the fixed 20) so an empty cell's hit target fills the row — fixes "can't tap the lower half of a tall empty cell to focus."
- Keep the GotFocus/LostFocus scroll-enable toggle (~5221) as a safety net for content that outruns a mid-typing measurement, but with B1 it should rarely engage.

**B4. Caret placement on tap.** Where the Text/Select tool tap resolves to a table cell via `TableCellAt`, after `Focus(FocusState.Pointer)` call `box.Document.Selection.SetPoint(tapScreenPoint, PointOptions.None, false)` inside try/catch so the caret lands at the tap, not at position 0.

## C. Width-growth feel for free bubbles (pain 3)

All inside the width phase of `AutoSizeBubble`:
- **C1. Font size input:** replace `Selection.CharacterFormat.Size` (caret-position font — wrong the moment sizes are mixed) with the document's max run size: walk `TextRangeUnit.CharacterFormat` runs (`range.Move(CharacterFormat, 1)` loop, O(#runs), capped at 64 iterations) taking `max(Size)`. Cache the result; recompute only when `TextChanged` fires with the doc length delta ≠ ±1 (paste/format change) or every 10th keystroke.
- **C2. Per-line measurement:** split plain text on `'\r'`, measure each line's natural width with one shared `CanvasTextFormat` (hoist out of the loop; the current per-call `using` allocations are churn), take the max. A long first line + short second line currently keeps the box at max even though nothing needs it — this fixes shrink-back.
- **C3. Hysteresis:** grow immediately when `needed > box.Width − 2`; shrink only when `needed < box.Width − 32`, debounced 400 ms via a per-box `DispatcherQueueTimer` (store alongside the `_textUi` tuple). Kills the per-keystroke jitter.
- **C4. Constants:** floor 200 (align with `MinWidth`; the current 260 floor contradicts `MinWidth = Math.Min(t.MaxWidth, 200)` at ~5240). Keep the one-char lookahead (`fs*0.75`) and the snapshotted `t.MaxWidth` cap exactly as is (#15 contract). Sanity: if a loaded box has `0 < t.MaxWidth < 200`, re-snapshot via `ComputeBubbleMaxWidth`.
- `ComputeBubbleMaxWidth` itself: no change.

## D. Edge cases

- **Undo/redo:** width/height/row-growth are derived or session-coalesced (B1); `RebuildTextLayer` after undo re-runs `AutoSizeBubble` on build, so restored RTF re-sizes correctly. Verify `TryDiscardTop` still matches after A2 (it does — model removal unchanged).
- **Cells vs bubbles:** `AutoSizeBubble` must never run for `TableId != null` (guard at top, mirroring current comment); `AutoGrowCellRow` never for free boxes.
- **Pinned widths:** width phase skipped, height phase mandatory (A1). `rGrip.ManipulationDelta` live-drag should call the height phase each delta so wrap changes are visible during the drag.
- **Rotation:** free bubble height growth with `RenderTransformOrigin(0.5,0.5)` shifts the visual pivot — accepted (matches image behavior); do not re-anchor. Rotated tables: B2 recomputes each cell's CompositeTransform after row growth, and `RotCentre()` reads live ActualWidth/Height so rotate-after-grow stays correct.
- **Zoom:** all measurements are world/DIP-space (text layer transform maps screen→world) — no zoom terms needed in A/B/C.
- **`FlushTexts`:** unchanged, but B1's LostFocus layout push must run BEFORE any `FlushTexts`-triggered rebuild in the same handler chain.
- **LoadPage heal (#cellfix, ~529):** untouched; it calls `ReflowTableCells` (rebuild variant) — correct, nothing has focus yet.

## E. Tests (manual, x64 Debug build)

1. Type 3 paragraphs in a new bubble → box grows downward, no clip, no inner scrollbar, wheel still pans canvas.
2. Pinned-width box (drag rGrip, then type past wrap) → height grows, width fixed.
3. Legacy box (`AutoWidth=false` from an old note) → height grows, width never re-wraps.
4. Mixed font sizes (8pt line + 36pt line) → width tracks the 36pt line; no premature wrap; delete the 36pt line → box shrinks after ~0.4 s.
5. Type past a cell's row height → row grows, cells below shift, focus + caret never lost, table outline redraws.
6. Same in a row-span-merged cell → the last spanned row grows.
7. One undo after leaving the cell reverts the row height; a second reverts the text.
8. Tap the empty lower half of a tall cell → cell focuses, caret at tap point.
9. Rotate a table, type in a cell until the row grows → cells keep riding the rotation.
10. Rotated free bubble: type to grow → no jump on next rotate-handle drag.
11. Empty bubble LostFocus while tapping directly into another box → new box keeps focus, undo stack has no dead step.
12. Insert/delete row/col and merge/split still reflow and undo as one step (regression on B2 split).
13. Divider double-tap on a row edge fits the row to content; on a column edge still fits the column.
14. Window resize then new bubble → new cap honored; existing boxes' caps unchanged (#15).

Primary files: `C:\Users\irony\Downloads\Quill Gem - Fable\Quill\src\Quill\Controls\InkSurface.cs` (all changes), `C:\Users\irony\Downloads\Quill Gem - Fable\Quill\src\Quill\Models\NoteModels.cs` (no schema change required).