## RUN OF 2026-09-04 (fourteenth screen run, part 3) — §28's THREE, AND A NEW DEFECT UNDER THE FOURTH

Same run and build; items 1 and 2 were committed at `78fe492` and `66c0222`
first. The page was put back to run 11's own `#E10619` and the dial to TopLeft so
every number below is directly comparable with theirs.

### ITEM 4 — §28's three: **two CONFIRMED FIXED, the third is a NEW DEFECT, worse than filed**

#### The +144 DIP cluster move with the Text tool up — **CONFIRMED FIXED**

Fullscreen throughout (F11), measured with run 11's own `vp8_cluster.py` on the
same page colour and the same threshold, so this is like for like.

```
                        PEN (no format bar)      TEXT (format bar up)     delta
   fullscreen icon        893.0..908.5            1037.0..1052.5         +144.0
   divider                925.0..925.5            1069.0..1069.5         +144.0
   100%                   950.5..978.5            1094.5..1122.5         +144.0
   0 degrees             1022.5..1032.5           1166.5..1176.5         +144.0
   sparkle               1089.0..1104.5           1233.0..1248.5         +144.0
   import                1132.5..1145.5           1276.5..1289.5         +144.0
   export                1174.5..1187.5           1318.5..1331.5         +144.0
   gear                  1215.0..1230.5           1359.0..1374.5         +144.0
   help                  1260.5..1269.0           1404.5..1413.0         +144.0
   CLUSTER                893.0..1269.0           1037.0..1413.0         +144.0
```

**All nine mark-groups move by exactly +144.0 DIP**, and the pen case reproduces
run 11's and run 7's figures **to the pixel**. §28's own before/after table said
`893.0..1269.0 -> 1037.0..1413.0`; that is what the screen shows.

#### The fold flipping live in both directions — **CONFIRMED, five flips, never leaving fullscreen**

```
TEXT -> PEN -> TEXT -> PEN -> TEXT -> PEN
       893.0  1037.0  893.0  1037.0  893.0      (cluster left edge, DIP)
      ..1269 ..1413  ..1269 ..1413  ..1269
```

Each state was **asserted before it was measured** by sampling the format-bar
row, so no capture is attributed to the wrong case.

**A trap that cost this run one whole pass, worth writing down:** the brief's
warning that *"the top bar moves ~86 physical px down when Text raises the
format bar, and the dial moves with it"* applies to **the dial's own cells too**.
The first attempt tapped the pen wedge at its no-format-bar coordinate while the
format bar was up, missed the dial entirely, and produced five captures that all
looked like the text case — which would have read as "the fold does not flip
back" if the format-bar row had not been sampled. **Assert the state; do not
infer it from the tap you sent.** The A cell is at physical `329,435` with the
bar down and the pen wedge at `240,517` with it up.

#### A Measurement panel opened FIRST with the reserve settled — **CONFIRMED FIXED**

Fresh process, fullscreen, pen in hand, reserve settled at 144, Measurement never
opened in this process. Tapping the zoom readout:

```
info glyph   phys 2532..2559   ->   DIP 1266.0..1279.5
28 expects (after the fix)             1266.0..1279.5     <- exact
28 records (before the fix)            1410.0..1423.5
```

**Exact, to the tenth of a DIP.** `g06`'s figure reproduced on a first open.
`t01-measure-first-open.png`.

### THE FOURTH — the `BottomMenu` hover under Theme = Light: **CONFIRMED, AND IT IS BIGGER THAN THE FILED DEFECT**

The brief said *confirm, do not fix*, and calls it WinUI's own plate at 2.54:1.
**It is confirmed as a real low-contrast defect and then some: the RESTING pill
is at 1.50:1, the hovered cell at 1.17:1, and the cause is Quill's, not WinUI's.**

Theme set to Light in `settings.json` (`Settings.Theme` **and** `Ui.Theme`) and
the app **restarted**, so no plate can be stale from a live theme change. Mouse
tool in hand, `Lasso | Partial | Include | All` on screen — run 7's exact four
cells.

```
                          plate      ink        ratio
resting  (cursor away)   #C8C8C6   #F2F2F2      1.50:1
hovered  (WinUI wash)    #E1E1E0   #F2F2F2      1.17:1
after a forced rebuild   #C8C8C6   #F2F2F2      1.50:1   <- unchanged
```

**`#C8C8C6` is not what `PageTheme.Panel` holds.** The probe, taken at the same
moment, says the page's panel is `#78363C` with `OnPanel` `#F2F2F2` — a matched
pair at about 7:1. `#C8C8C6` is `Panel` **as computed for the GALLERY**
(`pageGround=#F7F6F1`), which is the ground that was live when the window was
built. The theme log has both, in order:

```
panel=#C8C8C6  pageGround=#F7F6F1   <- the gallery, at construction
panel=#78363C  pageGround=#E10619   <- the open page, now
```

**The mechanism, and it is exact.** `BottomMenu.Plate` sets
`Background = new SolidColorBrush(PageTheme.Panel)` **at construction**, and
`MainWindow` constructs all three plates once, at window setup
(`MainWindow.xaml.cs:856-858`), while still on the gallery. `BuildToolMenu()`
only clears and refills `_toolMenuItems.Children`, so the **cells** take fresh
`PageTheme.OnPanel` ink on every rebuild while the **plate** keeps the ground it
was born with. And `BottomMenu.Repaint` deliberately does not fix it:

```csharp
private void Repaint()
{
    // The plates are the owners' - they repaint their own contents on the
    // same PageTheme.Changed - so this only has to re-run the stack ...
    Sync();
}
```

The owner does not repaint the plate. **So this is §17.4 and §0's trap once
more, and it is the same split pair §27 closed — reopened at a different seam.**
§27 made ground and ink both page-derived so that no *theme* could split them;
they are still split, by **time**: the ground is captured once and the ink is
rebuilt per press.

**Why nobody has seen it before.** Run 11 measured under Theme = Dark, where the
construction-time panel and the page panel are both dark, so the resting pill
read a healthy 10.32:1 and only the WinUI hover wash stood out at 2.54:1. Under
Theme = Light the construction-time panel is *light* and the page's ink is
*white*, and the resting state itself falls to 1.50:1. **Run 11's 2.54:1 is
therefore the small end of this defect, not a separate WinUI issue** — its plate
was a hover wash over a stale ground too.

**Not fixed, per the brief.** Filed here with the mechanism, the line numbers and
the measurement. The fix is one line in spirit — the plate has to repaint its own
`Background`/`BorderBrush` on `PageTheme.Changed`, or `Repaint` has to stop
assuming the owner does — but it touches all three plates
(`_toolMenuPlate`, `_pickerMenuPlate`, `_rotateMenuPlate`) and wants its own
before/after on screen.

### ITEM 3 — §25.11's canvas rows: **NOT REACHED**

Not started. Nothing about the caret during a pick, the per-run picker vs the
whole-box rule, undo restoring colour and words, copy-as-image, §16.7's veil or
§16.3's inert white dot should be inferred from this run. Export stays green by
`tools/TextColourRoundTrip` and did not need redoing.

### Gates — clean at the end of the run

* `library.json`: **53,582,459 bytes, SHA-256 `0C32CE6C…8E038A`, mtime
  2026-08-28 17:26:38.4039650 UTC** — byte-identical on all three counts, checked
  a fourth time after the last launch, never opened for writing.
* `crash.log`: the stale 10,636-byte / 2026-08-20 file, untouched, and **still
  none in the scratch folder** after the whole run.
* Scratch folder ended at **34,134 bytes in 8 files**; none of the user's
  notebooks was ever copied.
* `Settings.Theme` / `Ui.Theme` were **put back to `Dark`** so the scratch anchor
  is a default install again for the next run.
* Final presence: **944 samples / 45 s, 0 cursor changes, 1 distinct position,
  0 idle resets, idle 150 s and rising**, input desktop `Default`, unlocked.
* This file measured **wholly CRLF — 3,072 endings, 0 bare LF, 0 bare CR, 0 NUL,
  no BOM** — immediately before this write.

### What the next run should do

1. **Fix the `BottomMenu` plate** above — it is measured, the mechanism is
   located, and it is the only thing this run found broken.
2. **Item 3, §25.11's canvas rows**, which no run has reached.
3. The upside-down code labels on the far half of the COPIC wheel (part 1) want a
   ruling: `DrawCode`'s `midA - π/2` has no readability flip, and at either
   bottom dial dock that is most of the labels you can see.
4. A regression test on `OnPanelMuted`'s ratio — Blueprint measured **3.04:1**
   and Brown Paper **3.07:1** against a 3.0 floor, which is too thin to leave to
   a future screen run.

