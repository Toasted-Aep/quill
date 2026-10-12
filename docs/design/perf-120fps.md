# Performance: why Quill does not hold 120 fps, and how to find out

Status: **analysis by reading the source only. Nothing in this document has been built,
run or measured.** Written 2026-10-09 against `main` at `f3efd52`, for TODO item 9.1.
File:line references are to that commit and will drift; the names beside them are what
to search for. Every "expected impact" below is an estimate made from reading the code,
and is labelled as one.

**Second pass (2026-10-12), same commit's source.** The first pass read about 4,300 of
InkSurface.cs's lines and 2,100 of MainWindow.xaml.cs's. The second pass read the rest of
both, and SettingsWindow.cs, ColorWheel.cs, BrushesWindow.cs, FloatingWindow.cs, the
exporters, ExportWindow.cs, HistoryWindow.cs, ObjectsWindow.cs, SelectionChrome.cs,
FullscreenChrome.cs, ValuePopover.cs, PanelLayout.cs, PaperTextures.cs, PaperGrain.cs,
GridArt.cs and most of BottomMenu.cs and MeasurementMenu.cs (exact ranges are in section 6).
Still nothing has been built, run or measured. Material added by the second pass is marked
"(second pass)" in headings, or in the text where it amends a first-pass passage. The
ranking in section 1.1 is the current one; the S-numbers of the first pass are unchanged and
the new suspects are S12 to S20.

The report (owner, 2026-09-29): on the Intel Core Ultra 7 255H laptop (integrated
graphics, 2880x1800 at 200%) Concepts runs at 120 fps and Quill runs at nearly 30, and
Quill visibly lags behind after a button press. TODO 9.1 is done when there is "a steady
120 fps while drawing, panning and zooming ... and no visible lag after any button press -
measured, not estimated".

This document ranks what the code gives reason to suspect, says how to measure each
suspect on the laptop, and lays out the fixes in small steps, safest first. It changes no
code.

## 1. Summary

Reading the code does not find one cause. (The second pass, section 1.1, adds more and
re-ranks them; this list is the first pass's.) It finds five separate ones, any of which could
account for much of what was reported, and each can be confirmed or cleared with a
measurement that takes minutes. In the order to look at them:

1. **The binary the owner launches is probably a Debug build** (S1). The repo's own notes
   say the owner's `Quill.lnk` targets the x64 Debug output, and every documented build
   command is `-c Debug`. Quill's own code, which is where the per-stroke and per-segment
   loops live, then runs unoptimised. This costs nothing to test.
2. **Every pan step, zoom step, tool switch and pen lift repaints the whole 5.2 million
   pixel canvas** (S2). By the code's own comments Win2D hands the app about a hundred
   separate rectangles for that, and each rectangle re-runs the entire page draw from
   scratch: grid for the whole viewport, a scan of every stroke on the page, then the
   strokes. There is no retained picture of the page to slide around, except on pages of
   2,500 strokes or more.
3. **Ink is drawn as one `DrawLine` call per segment, per frame, per rectangle** (S3), and
   the stroke being written is redrawn from its first point on every pen move.
4. **A pen or tool button rebuilds the dial and the pen strip from scratch, parsing XAML
   for every icon** (S4). So does every finished stroke, because the dial (the default tool
   surface) also rebuilds on every undo-stack change.
5. **Every save serialises the 53 MB library twice on the UI thread, and a plain pan or
   zoom schedules one** (S5). Every page turn does one immediately.

Behind those: a 40 ms glow timer that never stops and acrylic cards that sit over the
canvas (S6), per-event work in the view-changed chain (S7), the in-memory data model and
garbage collector (S8), the ink cache's rebuild behaviour (S9), eraser and selection paths
(S10), and text boxes built eagerly for the whole page (S11).

### 1.1 Current ranking (second pass)

The second pass looked for a cost that runs continuously and found none beyond the glow
timer (S6). Every other timer in the app is a one-shot, a burst of under half a second, or
runs only while a window or tool is in use (table in S20). So the sustained "nearly 30 fps"
is still explained, if it is explained by the code at all, by S1 to S3 and S6; what the
second pass adds is mostly in the "lag after a button press" half of the report, and a
second source of save stalls.

| Rank | Suspect | Explains | Change from the first pass |
|------|---------|----------|----------------------------|
| 1 | S1 Debug build | everything CPU-side | unchanged |
| 2 | S2 full-viewport repaint, about 100 regions, each a full page draw | sustained fps while panning, zooming, switching tool | unchanged; per-region shape costs added (S16) |
| 3 | S3 one DrawLine per segment, wet stroke redrawn from its first point | sustained fps while writing | unchanged; extra per-region wet-stroke allocations (S3 addendum) |
| 4 | S4 button press rebuilds the dial and pen strip, extended by S14 (bottom menu, selection chrome, theme cascade) and S18 (dial animation) | lag after a button press | S4 widened: more rebuilds per press than the first pass found |
| 5 | S5 whole-library saves on the UI thread, extended by S12 (a page turn saves twice) and S13 (colour picker writes settings.json per callback) | freezes after a pause, at page turns, while picking a colour | S5 corrected and widened |
| 6 | S6 glow timer and acrylic cards | steady background cost, damage behind the canvas | unchanged |
| 7 | S11 text layer, rebuilt from many more triggers and heavier per box than stated | page turn, delete, duplicate, paste, undo on text pages | up from 11; the cost per box and the trigger list were understated |
| 8 | S7 view-changed chain | per-event cost during pan and zoom | unchanged |
| 9 | S17 image selection fade: a full uncached repaint per frame plus per-frame text-box work | a hitch when an image is clicked | new (S10 understated it) |
| 10 | S15 paper tile bake on the UI thread | a hitch on a page turn or first draw of a paper | new |

Below the line: S9 ink cache (only on pages of 2,500+ strokes), S16 per-region shape
costs (only on pages with tables, equations, grainy shapes or large images), S8 GC, S10
eraser and selection paths, and S19 and S20, which cost only while a window, tool or
non-default setting is in use.

Order of work:

1. Run the current code as a **Release** build against a **copy** of the library (F0).
   Minutes of effort, no risk to data, and it says how much of the problem S1 is.
2. Add the probes of section 4 (one new file and about a dozen small hooks, off unless an
   environment variable is set) and run the scenario script on the Debug and Release
   builds, with the real library copy and with a small one.
3. Fix in the order of section 5. The first three fixes touch neither the file format nor
   the paint tiles. The two that could endanger notes (F5 and F6) come only after
   measurement shows they are needed, and each has a harness gate.

Data safety is the constraint on everything below. library.json is 53,582,382 bytes of the
owner's notes; paint lives in separate tiles under `%LOCALAPPDATA%`. No step in the first
tier changes what is written to either, and the measurements themselves run on a copy
(section 4.1), so nothing in this plan needs the live library to be touched.

## 2. The parts that matter, on one page

**The canvas.** `InkSurface` (src/Quill/Controls/InkSurface.cs, 11,138 lines) hosts one
Win2D `CanvasVirtualControl` (line 473; README.md:96 still lists the move from
`CanvasControl` to `CanvasVirtualControl` as future work, which has been done). The control
covers the canvas area: about 2880 x 1800 = 5.2 million pixels on the owner's screen
(1440 x 900 DIP at 200%). When something invalidates it, Win2D calls
`OnRegionsInvalidated` (line 5901) with a list of rectangles. The app opens a drawing
session on each one (line 5917) and calls `DrawRegion` (line 6269), which paints that
rectangle from scratch: clear, paper, grid, shapes, every stroke as vector lines, then the
overlays. Nothing from the previous frame is reused.

**What invalidates it.** The whole control, for: any pan or zoom step (InkSurface.cs:790),
a tool switch (995), `Refresh()` (998, used by the page-settings handlers), every pointer
press (1865, so pen-down) and the end of every gesture (2948, so pen-lift), every pointer
move of every tool except the pen (2463), eraser hover (2246), and about seventy other call
sites. The only small invalidations are the pen's wet-ink rectangle (2311-2323, through
`InvalidateScreenRect`) and oil-paint tile updates (6081, 6099, 6203). There is no swap chain
and no frame loop of the app's own: drawing is driven entirely by Win2D's invalidation
callback.

**What a pointer move does.** For the pen (InkSurface.cs:2228-2327): read the point and its
intermediate points (2288-2307), append one `StrokePoint` for each that is at least 0.7
screen units from the last, then invalidate one rectangle around the new segment
(2311-2323). Hover invalidates nothing, except with the eraser (2246). Every other tool ends
the handler in a whole-control `Invalidate()` (2463).

**What is allocated.** Per pen move: the list and wrapper objects from
`GetIntermediatePoints` (2288) and one `StrokePoint` heap object per accepted sample (2303).
Per region per frame: a `PenStroke` and a copied pressure-curve list for the wet stroke
(6478-6487), four strings per stroke from colour parsing (7118), a `CanvasPathBuilder` and a
`CanvasGeometry` per stroke for the polyline pens (7313-7318), a `CanvasTextFormat` per
comment pin (6641), and, when the page header is shown, date strings and a text layout for
the title (7067-7070). Per glow tick: a list of brushes (MainWindow.xaml.cs:1204-1220). Per
button press: the objects of S4.

**Everything else is XAML on the same thread.** The dial (`ToolWheel`), the pen strip
(`BuildPenStrip`), the pen bar, the top bars (`ChromeBars`), the panels, and the text boxes
(one `RichEditBox` per text element, in a layer above the canvas) are WinUI elements on the
UI thread. That thread also runs input handling, the paint pass above, and autosave. At
120 fps it has 8.3 ms per frame for all of it.

**Saving.** `ScheduleSave` (MainWindow.xaml.cs:2142) restarts a 1.5 s timer; when it fires,
`SaveNow` (2159) serialises the whole library on the UI thread and hands the string to a
worker for the file write (LibraryStore.cs:563-580).

**The build.** Quill.csproj sets no optimisation, tiering, ReadyToRun or GC property, so
the configuration alone decides what the JIT does with Quill's own code.

## 3. Ranked suspects

Ranked by how much of the report each could explain, weighted by how firm the evidence in
the code is. "Confirm or clear" names probes from section 4.

### S1. The daily-driver binary is a Debug build

**Evidence.**
- docs/VISUAL-PASS-RESUME.md:5203-5205 (run 24, written up 2026-09-09): "`Quill.lnk` points at
  neither [stale binary] (it targets the current x64 Debug build)".
- Every documented build is Debug: docs/CONCEPTS-DIRECTION.md:16 and
  docs/ORCHESTRATION-STATE.md:48 (`dotnet build ... -c Debug -p:Platform=x64`), README.md:14
  (F5 in Visual Studio, whose default is Debug). The screen-run scripts launch
  `bin\x64\Debug\...\Quill.exe` (scratchpad/vp25_launch.ps1:52, vp5.ps1, vp9.ps1,
  vp10.ps1, vp11.ps1, vp12.ps1).
- A Release build is only documented for the signed MSIX (docs/PACKAGING.md:16). A stale
  `bin\x64\Release` from 2026-07-12 exists on the owner's disk but nothing launches it.
- src/Quill/Quill.csproj sets nothing about optimisation, tiering, ReadyToRun or the GC, so
  Debug means the SDK's Debug defaults. No source file uses `#if DEBUG`, so the effect is
  the compiler and JIT only, not extra diagnostics code.

**Why it costs frames.** A Debug build compiles Quill's C# without optimisation and marks
the assembly so the JIT does not optimise it either (and, as I understand the runtime, never
tiers it up later the way it does Release code). WinUI, Win2D, the runtime and
System.Text.Json ship optimised, so the penalty lands on exactly the code Quill owns: the
per-stroke and per-segment loops in `DrawRegion` and `DrawStroke`, `SegmentWidth`,
`GetBounds`, `ColorUtil.Parse`, the pointer handlers, and the model's JSON converters. Small
struct maths such as `Vector2` is not inlined in unoptimised code.

**Expected impact (estimate).** Tight managed loops commonly run 2-5 times slower
unoptimised. How much of a frame is Quill's managed code versus Direct2D and the GPU is
not known, so the effect on frame time is somewhere from modest to dominant. It multiplies
every CPU-side suspect below, which is why it comes first.

**Confidence.** The evidence says Debug is what the owner launched in September. Whether it
is still the case is unknown until the launcher is looked at.

**Confirm or clear.** P0 (which binary), then the same scenario on Debug and Release
(section 4.5, runs 1 and 2). If Release's paint pass takes under 60% of Debug's, S1 is a
major cause. If the two are within 15%, S1 is cleared as the main cause.

### S2. A full-viewport repaint through about a hundred regions, each a full page draw

**Evidence.**
- Win2D is given the entire control to repaint on a pan step, zoom step, tool switch and
  so on (list in section 2). OnViewChanged (InkSurface.cs:739-792) calls
  `_canvas.Invalidate()` at 790 for every step.
- The code's own comments put a full pass at about 120 regions: "one bad frame wrote ~120
  identical lines" (5907-5909) and "a pass can hold ~120" (6330-6331). Tile-sized regions
  over 2880 x 1800 pixels would give roughly 100, so the order of magnitude agrees.
  `OnRegionsInvalidated` opens a drawing session per region (5917).
- `DrawRegion` (6269-6667) does, once per region: parse the page background colour (6287),
  clear (6290), paper, grid, perspective guides, artboard, title (6296-6300), fetch the
  draw plan, walk every shape (6397) computing its bounds, and walk **every stroke on the
  page** testing its bounds (6447-6471), then the overlays.
- `DrawGrid` (6694-6776) takes its extent from the whole control, not from the region:
  `tl`/`br` are `ToWorld(0,0)` and `ToWorld(ActualWidth, ActualHeight)` (6699-6700). Each
  region therefore issues every grid line or dot in the viewport. A dotted grid at the
  default spacing of 32 is about 1,300 `FillCircle` calls per region (6761-6763), so
  roughly 130,000 per full pass. `DrawPerspective` (6833) and `DrawArtboard` (7028) do the
  same with the viewport, and `DrawPageTitle` (7056) formats a date string and lays out
  text in every region when the page header is shown.
- No retained image exists for ordinary pages. The static-ink cache only engages at
  `InkCacheThreshold = 2500` strokes (line 181, used at 6333-6335).

**Why it costs frames or latency.**
- A pan redraws all 5.2 million pixels from vector commands every frame; the previous
  frame's pixels are not moved, they are thrown away.
- Each region carries a fixed cost (open a session on the virtual surface, set up the
  context, clear, flush and close it) that is paid about a hundred times per frame. The
  size of that fixed cost on this GPU is the main unknown.
- Each region also pays the scene walk: a bounds test against every stroke and shape on the
  page, and the viewport-wide grid. Walk cost grows as regions x page size.
- At 200% scale the canvas has four times the pixels, and so about four times the regions,
  of the same window at 100%. A design that is comfortable on a 1080p screen can be several
  times worse here.
- All of it runs on the UI thread, so pointer events queue behind the pass. That is the
  "lags behind" feeling: a 30 ms pass leaves nothing for input.

**Expected impact (estimate).** This sets the ceiling on pan and zoom frame rate. A budget
of 8.3 ms for the whole frame has to cover about a hundred session open/close pairs plus
the walks above. I would not expect 120 fps to be reachable with this structure whatever
else is fixed. The size of the fixed per-region cost is what P3 measures.

**Confidence.** High that the structure is as described (it is in the code, and the
authors' own comments agree). Unmeasured: the real region count at this resolution, and
the per-region fixed cost.

**Confirm or clear.** P3 on a blank page (no ink, no grid) while panning: if the pass takes
over 6 ms with nothing on the page, the structure alone defeats 120 fps. If it takes under
2 ms, the fixed cost is not the limit and S3 and the page content are.

### S3. The cost model of ink: one call per segment, redrawn from scratch

**Evidence.**
- `DrawStroke` (7099-7239): for the default pens (Standard, Brush, Fountain, Rollerball,
  Gel, Ballpoint, FeltTip, Marker, Calligraphy) it issues one `ds.DrawLine` per segment
  (7229-7238), each a call across the WinRT boundary with two vectors, a colour, a width
  and a stroke style.
- Highlighter, Pencil, Crayon, Watercolor and Monoline go through `DrawPolyline`
  (7309-7320), which builds a `CanvasPathBuilder` and a `CanvasGeometry` for the stroke on
  every call. Pencil calls it three times per stroke (7159-7162) and Crayon four
  (7182-7186). Nothing is cached between regions or frames.
- Every call parses the colour string: `ColorUtil.Parse` (InkSurface.cs:7118) does a
  `TrimStart`, three `Substring` calls and three `Convert.ToByte`, inside a try/catch
  (Helpers/Util.cs:8-25). That is four short-lived strings per stroke per region per frame.
- A stroke is drawn once per region it touches, and the cull test reads
  `PenStroke.GetBounds` (NoteModels.cs:117-142), whose validity check dereferences the
  stroke's last point. `StrokePoint` is a class (NoteModels.cs:59), so that is a chain of
  dependent loads through scattered heap objects, per stroke, per region.
- The wet stroke (the one being written) is rebuilt as a new `PenStroke` with a copied
  pressure curve in every region (6476-6497) and redrawn from its first point, so a pen move
  costs time proportional to the stroke's length so far, and a whole stroke costs the square
  of it. A point is kept once it is 0.7 screen units from the last one (2301-2303, which is
  1.4 device pixels at 200%), and points are never simplified afterwards (`FinalizeStroke`,
  3216-3226). That density is a large part of why the library is as big as it is.

**Why it costs frames or latency.** Native-call overhead per segment, Direct2D tessellating
each round-capped segment and the GPU rasterising it with anti-aliasing, a cache-hostile cull
loop, and steady allocation. Cost scales with the points on screen multiplied by the number
of regions each stroke crosses.

**Expected impact (estimate).** For a frame, calls = (segments of visible strokes) x
(regions each crosses on average). A page with 120,000 visible segments, each stroke
crossing three regions, is 360,000 calls per full repaint; at an assumed 1 us of managed
overhead per call that is 360 ms of command recording alone. The owner reports about 30 fps
(33 ms), which at the same assumed cost corresponds to only ~30,000 calls per frame, so
either the pages in use are light, or they are over 2,500 strokes and go through the cache
(S9), or the per-call cost is lower than assumed. The page census (P11) settles it.

**Confidence.** High on the mechanism; the magnitude depends entirely on the owner's pages.

**Confirm or clear.** P4 (calls and points per pass) and P3's per-step split. Test: the same
view on a copy of the page with its strokes removed; if the pass time falls to the blank-page
figure, ink is the cost.

### S4. A button press rebuilds the dial and the pen strip, parsing XAML per icon

**Evidence.**
- `SelectTool` (MainWindow.xaml.cs:6806-6866) ends with `ToolUiChanged` (6865). Its
  subscribers: `ToolWheel.Refresh` (711), `PenBar.Refresh` (754), `SyncBottomMenu` (972),
  `ChromeBars.RefreshObjects` (995), and the Brushes window when open (6042).
- `ApplyPreset` (3764-3783), the handler of every pen button, calls `SelectTool` (which
  fires `ToolUiChanged`), then `BuildPenStrip()` (3775), then fires `ToolUiChanged` a
  second time (3782).
- `ToolWheel.Refresh` (ToolWheel.cs:1014-1466) allocates about sixty new
  `SolidColorBrush` objects, and clears and re-creates the mark in all 10 sectors
  (`SlotArt`, 3223-3233), the three setting glyphs and the undo/redo pair: 15 calls to
  `Icons.Mark`. Each reaches `Icons.Geo` (Helpers/Icons.cs:853-860), which calls
  `XamlReader.Load` on a string of XAML to parse one path. There is no cache. It also
  calls `Measure` on text blocks and rebuilds popover geometry.
- `BuildPenStrip` (3121-3169) clears the strip and rebuilds it: the eraser chip (two
  `XamlReader.Load` calls through `ParseGeometry`, MainWindow.xaml.cs:3043, and a Flyout
  with RadioButtons built eagerly, 3305-3330), seven tool cells (one `Icons.Mark` each,
  3212-3269), and for every pen preset a two-tone chip (two more `XamlReader.Load` calls,
  3141), a `Button` with its default template, a tooltip and a Flyout. The flyout's body is
  built lazily (3368), which is good.
- The dial also refreshes on every `UndoManager.Changed` (ToolWheel.cs:623), so after every
  finished stroke, undo and redo; on every `SelectionState.Changed` (630); and on every step
  of dragging its size or opacity popover (610). `PenBar` does the same (PenBar.cs:233, 239,
  242). Closing any pen's editor flyout rebuilds the strip again (MainWindow.xaml.cs:3760).
- `SetTool` ends in a full-canvas `Invalidate()` (InkSurface.cs:995), so each tool switch
  is also an S2 repaint.
- Which of these surfaces exist depends on settings: `ToolSurface` defaults to `"Wheel"`
  (NoteModels.cs:715), and a surface that is switched off returns at the top of its
  `Refresh` (`Enforce`, ToolWheel.cs:784-791, PenBar.cs:315-322). P0 records which are on.

**Why it costs frames or latency.** It is all synchronous on the UI thread, in the click
handler. `XamlReader.Load` is the slowest way WinUI offers to make an element, and it is
being used on a hot path to make the same icons again. Throwing away and recreating
elements also forces layout, and the repaint at the end follows.

**Expected impact (estimate).** A pen-button press does about 60 to 80 `XamlReader.Load`
calls (2 x 15 for the dial, 9 for the eraser chip and tool cells, 2 per pen preset). At an
assumed 0.1 to 0.3 ms each that is 6 to 24 ms, plus brush allocation, layout and the S2
repaint: plausibly 30 to 100 ms in one block, which is two to six frames at 60 fps and
visibly "behind". The same dial rebuild after every finished stroke lands in the middle of
writing. This is the closest match in the code to the reported "lags after a button press".

**Confidence.** High that the work happens and is synchronous; the per-call cost is a guess.

**Confirm or clear.** P8: duration of the handler and the number of `XamlReader.Load` calls
per press. Cleared if a press costs under 5 ms of handler time and under 5 loads.

### S5. Saves serialise the whole library twice on the UI thread

**Evidence.**
- `ScheduleSave` (MainWindow.xaml.cs:2142-2153) restarts a 1.5 s timer (106, 328) and first
  calls `LibraryStore.PersistSettings` (2150). On expiry `SaveNow` (2159-2164) calls
  `FlushTexts` and `LibraryStore.Save`.
- `LibraryStore.Save` (LibraryStore.cs:563-580): `JsonSerializer.Serialize(lib, Opts)` on
  the calling (UI) thread (570), producing one string of the whole library (53,582,382
  bytes of UTF-8, so about 107 MB as a UTF-16 string, which lands on the large object
  heap); then `SyncLog.OnSaved(lib)` (573), still on the UI thread; only then the file write
  is queued to the thread pool (578).
- `SyncLog.OnSaved` (SyncLog.cs:270-300) walks `Entities` (236-254), which serialises every
  stroke, shape, text and comment in the library individually to its own string
  (247-250), builds a key string per element, then FNV-hashes every character of every one
  (202-210, 280) under a lock. That is a second, finer serialisation of the same 53 MB on
  every save, even when one stroke changed.
- Triggers: every `ContentChanged` (MainWindow.xaml.cs:243), so every stroke, erase and move;
  **every pan or zoom step**, through `OnViewChanged` (7517-7530, `ScheduleSave` at 7529);
  and `ScheduleSave()` appears 133 times in MainWindow.xaml.cs, in most settings and button
  handlers. Every **page turn** saves synchronously and unconditionally
  (`SwitchToPage` -> `SaveNow(releasing: true)`, 4195), as does closing the window (341).
- The worker side: `WriteTemp` writes the whole string through a `StreamWriter` and forces
  it to disk with `fs.Flush(true)` (772-779), `File.Replace` swaps it in keeping a `.bak`
  (785-791), and `TrySnapshot` writes a further full copy every 15 minutes (870-899).
- The authors know the cost: PaintStore.cs:155-156 calls library.json "53 MB,
  re-serialised on the UI thread every 1.5 s", and LibraryStore.cs:693-697 notes the write
  "does not always finish in 4s".

**Why it costs frames or latency.**
- The UI thread is blocked for the length of both serialisations. Nothing is drawn, no
  pointer event is handled and no button reacts until they finish.
- The large strings and buffers are allocated on the large object heap, which triggers
  generation-2 collections over a heap that holds, by the model's design, a heap object for
  every ink point (S8).
- The worker then writes and fsyncs 53 MB, real-time antivirus may scan the new file, and
  every fifteen minutes a second copy goes to `backups`. That is CPU and disk contention on a
  laptop where the CPU and integrated GPU share one power budget. Whether it lowers GPU
  clocks is a hypothesis, not a finding.
- The stall comes 1.5 s after the last change, which is typically the moment a person
  pauses and then presses something. It is why the symptom can read as "lag after a
  button press" even for a button that does no work.

**Expected impact (estimate).** A UI-thread stall of 0.5 to 3 s per save on this machine
(System.Text.Json typically runs at 100 to 300 MB/s on object graphs like this, done twice,
plus hashing; a power-limited laptop may be slower than that). It does not appear in an
"fps while panning" figure taken mid-gesture; it appears as a freeze after each pause and at
each page turn. P12 measures it exactly without touching the app.

**Confidence.** High that it is on the UI thread and happens after view changes (all in the
code). The duration is a guess until P6 or P12 gives it.

**Confirm or clear.** P2 (a stall of 100 ms or more starting about 1.5 s after the last pan,
zoom or stroke), P6 (the time inside `Serialize` and `OnSaved`), and the control run on a
small library, where the stall should vanish.

### S6. The compositor: a glow timer that never stops, and acrylic cards over the canvas

**Evidence.**
- `GlowMode` defaults to `"Breathe"` (NoteModels.cs:783). `ApplyGlowMode`
  (MainWindow.xaml.cs:1222-1263) starts a 40 ms `DispatcherTimer` (1226), and `GlowTick`
  (1265-1375) sets `Opacity` on every shared glass brush each tick (1373), forever, unless
  the setting is Off or reduced motion is on. `GlowBrushes()` (1204-1220) takes locks and
  allocates a list each tick.
- The glass is acrylic: `CardBrush` and `CardBrushFloat` are `AcrylicBrush` (App.xaml:15-31),
  used by about sixteen borders in MainWindow.xaml (lines 32, 462, 606, 648, 749, 804, 816,
  902, 972-997, 1019, 1036, 1058, 1137).
- The authors have already recorded the cost: "The glow pulse is a dependent animation:
  every tick invalidates every glass panel. And dragging an acrylic panel re-blurs it per
  frame. Both together are what tanked drag framerate" (MainWindow.xaml.cs:1774-1778), and
  `BeginCheapDrag` (1781-1788) swaps one dragged panel to a plain colour. And:
  "an AcrylicBrush over a Win2D swap chain samples it a frame late and smears every time
  the ink moves underneath" (ValuePopover.cs:377-379).
- The window uses a Mica backdrop (MainWindow.xaml.cs:180-181).

**Why it costs frames or latency.** Each canvas change is also a change behind every
acrylic card over it, so the backdrop blur is recomputed for them on a GPU that is already
filling 5.2 million pixels. The glow timer wakes the UI thread 25 times a second even when
nothing else is happening, and every tick damages the glass panels. The mitigation exists
only for dragging a panel, not for drawing, panning or zooming.

**Expected impact (estimate).** Unknown; GPU time for the blur at 200% scale on integrated
graphics, plus pacing noise from the timer. It is the cheapest suspect to test, because
Settings already has a glow mode of Off.

**Confidence.** Medium: the mechanism is documented by the authors, the size is not.

**Confirm or clear.** P9: glow Off, then solid cards. If pan or draw pass cadence improves
by more than about 15%, S6 is real.

### S7. Per-event work in the view-changed chain

**Evidence.** Every pan or zoom step runs, on the UI thread and before any drawing:
- `InkSurface.OnViewChanged` (739-792): four property writes on the text layer's transform
  (785-788), the velocity tracker, `Invalidate`, then the `ViewChanged` event (791).
- `MainWindow.OnViewChanged` (7517-7530): builds a string, sets two `TextBlock`s, may start a
  fade storyboard, then `ScheduleSave` (7529) -> `LibraryStore.PersistSettings` (2150).
- `PersistSettings` (LibraryStore.cs:635-655): `SettingProps()` does about 60
  `typeof(Library).GetProperty(name)` lookups (621-628), then `SerializeToElement` of each
  value (644) and a `GetRawText` string comparison (646), on every call, even though a pan
  changes no setting.
- `ChromeBars.OnViewChanged` -> `SyncReadouts` (ChromeBars.cs:1271, 582-594) and
  `SelectionChrome.OnViewMoved` (SelectionChrome.cs:870).
- A touch manipulation delta that includes a pinch calls both `ZoomAround` and `PanBy`
  (803-819), so the chain runs twice for that event. A trackpad or wheel sends many events a
  second (821-847).

**Why it costs frames or latency.** It is fixed per-event cost on the thread that has to
draw, before the draw. And it re-arms the 53 MB save (S5).

**Expected impact (estimate).** 0.3 to 2 ms per event in Release, perhaps three times that in
Debug. At 120 to 240 events a second that is a tenth to a half of the frame budget.

**Confidence.** High on the mechanism, low on the size.

**Confirm or clear.** P7.

### S8. The data model and the garbage collector

**Evidence.** `StrokePoint` is a class with three fields (NoteModels.cs:59-67), so a library
of 53 MB of JSON becomes, at an estimated 40 to 50 bytes of JSON per point, on the order of a
million separate heap objects, plus one `List` per stroke; the P11 census gives the real
count. All of it stays resident. Draw and save allocate heavily on top (S3, S5). Nothing in
Quill.csproj changes the GC mode, so it is the default (workstation, concurrent).

**Why it costs frames or latency.** A generation-2 collection over that many objects takes
tens of milliseconds even when concurrent, and the large-object allocations in the save path
(S5) force them. Draw-time allocation adds generation-0 collections during a pan.

**Expected impact (estimate).** Occasional 50 to 200 ms pauses, mostly around saves, plus
background GC work. Low as a steady-state cost.

**Confirm or clear.** P10: gen-2 count and total pause time per minute while panning, and
per save.

### S9. The ink cache: only for large pages, rebuilt on every edit, and soft at 200%

**Evidence.**
- Engages at 2,500 strokes (InkSurface.cs:181, 6333-6335). `ContentChanged` marks it dirty
  (586-593), so every stroke, erase or move forces a rebuild.
- The rebuild (`TryDrawInkCache`, 11057-11135) runs inside a region callback on the UI
  thread: it allocates a new `CanvasRenderTarget` of up to 4096 x 4096 pixels (64 MB;
  11082, 11092), disposing the old one, and draws every stroke in an area three viewports
  wide and three high with the per-stroke path.
- It rebuilds again when zoom leaves 0.5x to 1.05x of the build zoom (11070), when the view
  leaves the cached area, and 260 ms after a zoom settles (597-606).
- It is rendered at 96 DPI and `ViewZoom * 1.5` pixels per world unit (11080, 11092). The
  screen is 2 pixels per world unit at 100% zoom, and the 4096 clamp lowers the cache
  further: at zoom 1 a 1440 x 900 viewport makes a 4320 x 2700 world area, which at 1.5
  pixels per unit would be 6480 x 4050, so it is scaled by 0.63 to fit, to about 0.95 pixels
  per unit. On this display cached ink is therefore drawn from a bitmap at under half the
  screen's resolution. That is a softness problem, not a speed one.
- A selection move, free-space drag, replay or veil fade disables it (6333), so those fall
  back to drawing every stroke every frame.

**Why it costs frames or latency.** On pages over the threshold, each pen lift is followed by
a rebuild that draws the whole three-by-three area: a hitch right after you stop writing.

**Expected impact (estimate).** A 50 to 500 ms hitch after each edit on big pages; not
present on pages under the threshold. Whether the owner's pages exceed 2,500 strokes is not
known; a 53 MB library suggests some do.

**Confirm or clear.** P11 for the page's stroke count and P3 for the rebuild time.

### S10. Eraser, hover and selection paths

- Eraser hover invalidates the whole canvas on every pointer move (InkSurface.cs:2246), and
  so does every pointer move of the eraser, lasso, select, shape and free-space tools (2463).
  A tool whose cursor is a ring needs a ring-sized rectangle, not 5.2 million pixels.
- Every `PushAction` marks the spatial index stale (3507); the next hit-test or erase step
  then rebuilds it from every stroke (3443-3448). The eraser queries it per pointer move
  (3513-3523).
- Selecting an image starts the "veil" fade (4961-4998): each frame of about 190 ms does a
  full invalidate, rewrites the text boxes' properties (`ApplyTextVeil`) and stands the ink
  cache down.

Impact is tool-specific and probably felt as a slow eraser rather than slow drawing. Measure
with P3 and P5 while using the eraser.

### S11. Text boxes are built eagerly for the whole page

`RebuildTextLayer` (InkSurface.cs:10013-10050) builds a grid, several text blocks and a
`RichEditBox` for every text element on the page whether or not it is on screen
(`BuildTextUi`, 10075-). It runs on page load (924), on undo and redo of anything that
touches text (1153), after moving or scaling a selection that contains text (2859, 2880),
and after free-space (2938). The layer sits under a `CompositeTransform` that is rewritten
on every view change (785-788). The cost grows with the number of text boxes on the page:
page-turn time, and zoom smoothness on text-heavy pages. P11 gives the count.

**Addendum (second pass).** The per-box cost and the trigger list were both understated.
- Each `BuildTextUi` (10075-10628) creates a `Grid`, two `TextBlock`s, a `RichEditBox` with
  `IsSpellCheckEnabled = true` (10199) and a `Border`, sets about seven brushes and resources
  (`PinEditorBrushes`, 4472-4486, called at 10215), sets default character and paragraph
  formats (10218-10225), parses the whole RTF with `Document.SetText` (10238), then calls
  `AutoSizeBubble` at once (10429) and again on `Loaded` (10431). `AutoSizeBubble`
  (8738-8889) reads the text, scans up to 64 ranges for the largest run (`MaxRunFontSizePx`,
  8894-8919), creates a `CanvasTextLayout` per line and measures the content height with
  several `GetRange` and `GetPoint` calls. `Loaded` also re-reads the RTF and runs
  `RtfRunParser.RunColoursLost` (10298-10299). Estimate: 1 to 5 ms per box in Release, so
  100 to 500 ms for a page of 50 boxes, all on the UI thread, more in Debug.
- Callers that rebuild unconditionally (all in InkSurface.cs): 2859 (end of a scale
  gesture), 1352 (delete), 1394 (duplicate), 5776 (paste), 9031 (insert table), 9369 (table
  header row), 9121-9123 (`ReflowTableCells`, so every row or column insert, delete, merge,
  split and a divider double-tap), 4547 (recolour a selection that includes text), 3857 and
  3869 (layer visibility). Conditional: 1153 and 1184 (undo and redo, only if the action
  touches text), 2880 (only if `_selTexts.Count > 0`). So on a page with many text boxes,
  deleting, duplicating, pasting or scaling anything freezes for the whole build even when
  no text is involved.
- Not established: whether `RichEditBox.TextChanged` fires once after the initial
  `SetText` (the handler at 10494-10500 raises `ContentChanged`). If it does, every rebuild
  also schedules a save (S5).

### Corrections to the first pass (second pass)

These passages of the first pass were wrong or incomplete. The text above is left as
written so the change is visible; read it with these amendments.

1. **S5, "ScheduleSave() appears 133 times".** There are 132 call sites, the declaration
   (MainWindow.xaml.cs:2142), and 7 method-group subscriptions (243, 270, 709, 752, 6036,
   6127, 8035). The point stands.
2. **S5, "Every page turn saves synchronously and unconditionally".** The synchronous save
   is guarded by `_curPage != null` (4195). More importantly it is followed by a second,
   deferred full save, which the first pass missed (S12).
3. **S5 and S7, `PersistSettings` as a CPU cost only.** It also writes settings.json
   synchronously, with retry sleeps of up to 240 ms, whenever a setting that is mirrored
   there changed, and the mirrored list includes `Pens` and `RecentColors` (S13).
4. **S4, the list of what a press runs.** It omitted `SyncBottomMenu`'s rebuilds (S14),
   `SelectionChrome.Build` (S14), the Brushes window when open (S19), and the dial's own
   animation (S18). `SelectionState.Changed` also feeds `SyncBottomMenu` and
   `SyncPenRowColour` (MainWindow.xaml.cs:973, 979); it is de-duplicated
   (SelectionSubject.cs:125-137), so it does not fire per pointer move.
5. **S4, "pen and tool buttons schedule no save".** True of `SelectTool` and `ApplyPreset`
   themselves. The eraser-size radios (3309, 3310), the pen colour picker (3839) and
   `PushRecentColor` (8800) do schedule one.
6. **S11 and S10, which paths rebuild the text layer.** InkSurface.cs:2859 (the end of a
   scale gesture) is unconditional; only 2880 tests `_selTexts.Count > 0`. Delete (1352),
   duplicate (1394), paste (5776), table insert (9031), table header toggle (9369) and every
   table row or column edit through `ReflowTableCells` (9121-9123) also rebuild it without
   asking whether any text is involved. And the per-box cost is larger than "a grid, several
   text blocks and a RichEditBox" (S11 below, addendum). Boxes on hidden or 0% layers are
   skipped (10040-10041).
7. **S10, the selection veil.** It does more per frame than the first pass says (S17).
8. **S3, the wet stroke.** Besides the copied pressure curve it creates a `Guid` per region
   (`PenStroke.Id` defaults to `Guid.NewGuid()`, NoteModels.cs:71) and calls
   `EffectivePressureCurve()` (InkSurface.cs:7326-7330), which allocates. Small, but
   multiplied by about a hundred regions per frame while writing.
9. **P9 and F3, "SetDisplayDpi re-bakes textures when the DPI moves".** It drops every
   cached paper tile (`PaperTextures.Invalidate`, PaperTextures.cs:221-242), and each tile
   then re-bakes on the UI thread (S15). Feeding it a scaled DPI during motion, as F3
   suggested, would therefore be expensive, not just fiddly. Page tiles are baked at a fixed
   96 DPI by design (InkSurface.cs:480-483 comment); `SetDisplayDpi` is for the Settings
   swatches. F3 and F7 must change `DpiScale` without calling it with the changed value.
10. **S6, "no `EnableDependentAnimation`".** The first pass did not claim this, but note for
    S6 and the compositor discussion that the dial's own animations are dependent
    animations on the UI thread (S18).

The second pass found no first-pass claim in the ranges it re-read to be wrong in any other
way. The S9 arithmetic (cache scaled by 0.63 to about 0.95 pixels per world unit) checks
against InkSurface.cs:11080-11092.

### S12. A page turn queues a second full save (second pass)

**Evidence.**
- `SwitchToPage` (MainWindow.xaml.cs:4187-4188) calls `ScheduleSave()` first, which restarts
  the 1.5 s `_saveTimer` (2150-2153). Then, at 4195, it calls `SaveNow(releasing: true)`
  synchronously. `SaveNow` (2159-2164) does not stop `_saveTimer`. Then 4200-4203 can
  call `ScheduleSave()` again if `LastPageId` changed.
- The timer is still armed, so about 1.5 s after the page appears it fires (tick handler at
  328) and runs `SaveNow()` again.
- `LibraryStore.Save` (563-580) has no dirty check, so both saves serialise the whole
  library and run `SyncLog.OnSaved`, exactly as S5 describes.
- Every caller of `SwitchToPage` pays this: the tree, recents, gallery chips (5533), search
  results (4655), and the export and AI loops that step through pages (10088, 10139,
  10213/10221, 9068). A section or notebook export therefore does one blocking full save
  per page, plus one deferred.

**Why it costs frames or latency.** A page turn is a UI-thread stall (first save, before
the new page loads) and then a second stall 1.5 s later, just as the user starts to write
or press something. The second one is entirely redundant: nothing changed since the first.

**Expected impact (estimate).** Twice the S5 figure per page turn. It also makes the
symptom "freezes a moment after I turn the page" rather than only "freezes when I stop".

**Confidence.** High that it happens (it is all in the code and was read twice). Duration
unmeasured.

**Confirm or clear.** P2 and P6: two saves, 1.5 s apart, after one page turn with no edit.

### S13. The pen colour picker saves settings and rebuilds the chrome on every callback (second pass)

**Evidence.**
- MainWindow.xaml.cs:3835-3840: the picker callback sets `p.Color`, calls `ApplyPreset(p)`,
  then `ScheduleSave()`. The comment at 8809-8811 says the picker "fires live on every
  change".
- `ApplyPreset` (3764) is S4: `SelectTool`, `BuildPenStrip`, a second `ToolUiChanged`. So a
  drag across the wheel does the whole S4 rebuild per pointer move.
- `ScheduleSave` first calls `LibraryStore.PersistSettings` (LibraryStore.cs:635-655).
  `Pens` is a mirrored setting, so the comparison finds a change and calls `SaveSettings`
  (156-173) **synchronously on the UI thread**: serialise, write a temp file, flush to disk,
  replace. On a failed write it sleeps 80 ms then 160 ms (168).
- `PushRecentColor` (MainWindow.xaml.cs:8800-8807) writes `RecentColors`, also mirrored, so
  it can write settings.json too. The eyedropper (8854) and the Mix tool (7292) call
  `BuildPenStrip`, `ScheduleSave` and `PushRecentColor`.
- Not established: whether the dial's own centre-disc colour wheel goes through this same
  callback (ToolWheel.cs was only partly read).

**Why it costs frames or latency.** A fsynced file write inside a drag callback. It is
small (settings.json is small) but a flush to disk on a laptop can take milliseconds to tens
of milliseconds, and it comes with the S4 rebuild.

**Expected impact (estimate).** 20 to 100 ms per callback while dragging a pen colour.
Only while a colour is being picked for a pen.

**Confidence.** High on the mechanism, low on the size.

**Confirm or clear.** P8 (rebuild count per callback) and P6 (settings writes per second
during a drag).

### S14. Cascades from the bottom menu, the selection chrome and the theme (second pass)

**Evidence.**
- `SyncBottomMenu` (MainWindow.xaml.cs:6881-6920) is a subscriber of `ToolUiChanged` (972)
  and `SelectionState.Changed` (973). In the Mouse tool it calls `BuildToolMenu` (6900) and
  then `_bottomMenu.Publish`; in Rotate it does the same with `BuildRotateMenu`. `Publish`
  and `Retract` each end in `Sync()`, and `Sync()` raises `BottomMenu.Changed`
  unconditionally (BottomMenu.cs:317-340, called from 244, 249, 262). The handler at
  MainWindow.xaml.cs:971 re-runs `BuildToolMenu`, `BuildPickerMenu` and `BuildRotateMenu`.
  The result is up to three builds of the tool menu per call, each of 4 to 5 cells with an
  `Icons.Mark` and so a `XamlReader.Load` apiece (BottomMenu.cs:436). In the pen tool the
  builds return early and the cost is two cheap `Sync()` calls.
- `SelectionChrome` subscribes `Build` to `BottomMenu.Changed` (SelectionChrome.cs:410).
  When a selection appears, `Sync` (819-849) calls `Publish` (which fires `Changed` and
  `Build`) and then `Build()` again at 847: two builds. On deselect, `Retract` fires
  `Build` once for a bar that is about to be hidden (line 537 falls through to
  `BuildSelectionBar`). Each `Build` is `BuildSelectionBar` plus `BuildModeBar`
  (598-637, 651-683): about ten `Icons.Mark` calls and eight buttons.
- `PageTheme.Changed` has 13 subscribers. Those in the files read: five `FloatingWindow`s
  even when closed (FloatingWindow.cs:444-471, 1223; two `Icons.Mark` each), `ValuePopover`
  (196, 401-402), `SelectionChrome`, `FullscreenChrome` (233-241), `MeasurementMenu.Rebuild`
  (MeasurementMenu.cs:312, which tests `_root.Parent != null`, always true because the
  panel is added at construction) and `BottomMenu.Sync`. It fires when a page turn changes
  the ground (a different paper colour), on theme change, and when Settings changes a paper.
  `PushGround` (MainWindow.xaml.cs:2375-2449) additionally calls `ToolWheel.Refresh`
  unconditionally (2414). `SwitchToPage` reaches it at 4210.
- `ApplyFullscreenChrome` runs twice per tool press, from `UpdateFormatBarVisibility`
  (9094-9107, called at 6831) and `ApplyPenRowVisibility` (8157, called at 6833). Each run
  rewrites `FormatBar.Margin`, `FormatBar.Padding` and `TopBar.Padding` with new `Thickness`
  values unconditionally (8590-8592) and calls `SetFullscreen`, `SetActive` and
  `SetStripReserve`. Whether identical writes invalidate layout in WinUI 3 is not
  established.

**Why it costs frames or latency.** Same as S4: synchronous rebuilds on the click's own
thread. They add to S4's total for a press in the Mouse, Select and Rotate tools, and to the
cost of selecting or deselecting anything.

**Expected impact (estimate).** Mouse or Rotate tool press: about 12 to 15 extra
`XamlReader.Load` calls, 5 to 20 ms. Selection created or cleared: 2 to 3 builds of 3 to 10
ms. Page turn to a different paper colour: 40 to 80 `XamlReader.Load` calls, 5 to 25 ms.
`ApplyFullscreenChrome`: 0.1 to 1 ms per press.

**Confidence.** High that the work happens, medium to low on the size.

**Confirm or clear.** P8, with the counter extended to `BottomMenu.Changed` and
`SelectionChrome.Build` (section 4.3, P8).

### S15. Paper tiles are baked on the UI thread, inside the draw (second pass)

**Evidence.**
- `InkSurface.DrawPaper` (InkSurface.cs:6685) calls `PaperTextures.Brush`
  (PaperTextures.cs:246-275), which on a miss calls `TileLocked` and `Bake` (291-315) under a
  global lock, inside the region callback.
- `PaperGrain.Bake` (PaperGrain.cs:99-131) fills a 512 x 512 tile: 10 to 15 noise octaves
  per pixel, 22 fold families per pixel for the crumpled paper, blurs and flecks. It uses
  `Parallel.For`, which still blocks the calling (UI) thread. Each bake allocates about
  eight to ten `float[262144]` arrays (1 MB each, large object heap) and a 1 MB output.
- A tile is baked on the first draw of a paper after launch, on a page turn to a paper not
  yet baked, and, for the white-stock papers (Crumpled, Lightweight, Heavyweight, Rippled),
  again for every distinct page background colour (cache key, PaperTextures.cs:208-214).
  `PaperTextures.Invalidate` (221-230), called on a new device (InkSurface.cs:499) and from
  `SetDisplayDpi` (237-242; InkSurface.cs:520, 526), drops every tile.
- Opening Settings calls `PaperTextures.Preview` (SettingsWindow.cs:653,
  PaperTextures.cs:327-351), which can bake up to eight papers in a row on the first open.
  Whether those preview tiles share the canvas's device and so its tiles is not established.
- At rest it is cheap: per region per frame `DrawPaper` does a string key, a lock and a
  dictionary lookup (PaperTextures.cs:252-255).

**Why it costs frames or latency.** A one-off multi-hundred-millisecond block, plus large
object heap pressure that feeds S8. Not a sustained cost.

**Expected impact (estimate).** 80 to 400 ms per tile on a few low-power cores: a hitch on
the first page, on a page turn to a new paper, and 1 to 3 s on the first Settings open.
The GPU cost of the wrapped, linearly sampled image-brush fill each frame
(PaperTextures.cs:258-269) is not established.

**Confidence.** High that it is synchronous, low on the size.

**Confirm or clear.** P2 naming a stall with `PerfProbe.Activity = "paper-bake"`, and a
timer round `Bake`.

### S16. Per-region shape costs: tables, equations, grainy shapes, full-size images (second pass)

`DrawShape` runs once per shape per region, so these multiply by the region count (S2).
- **Tables** (InkSurface.cs:8191-8258): per region it allocates `TableColWidths`,
  `TableRowHeights`, two coordinate arrays and a 2D `cellMap` (8196-8204), walks **every text
  element on the page** to find the table's cells (8206), parses cell and border colours per
  cell (8237, 8244), and issues two `DrawLine` calls per cell.
- **Equation images** (8151-8167): per region it parses the page background and tests
  darkness (8152) and, when the ink matches the page, builds a new `ColorMatrixEffect`
  (8154-8166). `EquationInkIsDark` is cached (10633-10651), but its first call reads every
  pixel of the bitmap on the UI thread (`GetPixelColors`), once per path.
- **Pencil and crayon shapes** (8084-8105): `PenStyle.Grain` allocates an array per call,
  and each grain pass builds a new `ShapeElement`, with a `Guid.NewGuid()` (NoteModels.cs:170),
  for two or three extra passes per shape per region.
- **Images** (8143-8178): `_bitmaps` holds full-resolution `CanvasBitmap`s (loaded at 8402
  without downscaling), drawn once per region the image touches. The load itself is
  asynchronous (8396-8411) and does not stall. The bitmaps are disposed on page load
  (869-873), so there is no leak.

Ordinary shapes are a few `DrawLine` calls and are cheap. These matter only on pages that
have them, which is the P11 census question. Estimate: unmeasured; high confidence on the
mechanism. Fix: F4 (a to e) done once per pass, and F13 below.

### S17. Selecting an image fades the page with a full repaint per frame (second pass)

This replaces the short mention in S10.
- Any selected or active image, equation included, sets `FadesPage` (InkSurface.cs:4396),
  and `PublishSelection` calls `SetVeil` (4400). `SetVeil` attaches
  `CompositionTarget.Rendering += VeilTick` (4970), and `VeilTick` (4979-4998) runs every
  displayed frame for about 190 ms up and 130 ms down.
- Each tick does a full `_canvas.Invalidate()` (4993), the ink cache stays stood down for
  the duration (`_inkCacheDirty`, 4969), and `ApplyTextVeil` (5017-5037) visits every text
  box: `_page.Texts.FirstOrDefault(x => x.Id == id)` (5023), which is quadratic over the
  text boxes, `TextInkFor` (colour parses, including the page background when a box has no
  colour of its own, 4424-4427), and a **new `SolidColorBrush`** assigned to `Box.Foreground`
  (5035).
- `DrawShape` also fills a grey rectangle over every non-selected image while veiled
  (8185-8188).
- The same tick runs again on deselect, and `VeilTick` ends with two more invalidations
  (4979-4998).

Impact (estimate): a full uncached repaint per frame for about 320 ms in total plus
O(text boxes squared) work per frame; felt as a hitch when an image is clicked and again
when it is released, on text-heavy pages most. High confidence on the mechanism.
Confirm with P3 (pass count during the fade) and P4 (cache state).

### S18. The dial's animations are UI-thread dependent animations (second pass)

- `ToolWheel.Drop` (ToolWheel.cs:795-827) animates `ScaleX`, `ScaleY` and `Opacity` over
  160 ms with `EnableDependentAnimation = true` (810). `ToolWheel.Rise` (1517-1560)
  animates two `ScaleTransform`s and the flash opacity over 190 and 340 ms, also with
  `EnableDependentAnimation = true` (1534). Animating a `ScaleTransform` property this way
  runs on the UI thread, a frame at a time, rather than on the compositor.
- `Rise` runs when a cell of the dial is chosen, so every dial tool or pen choice is
  followed by a few hundred milliseconds of per-frame UI-thread animation that competes
  with the S4 rebuild and the S2 repaint for the same thread. `Drop` runs when the dial
  opens.
- Both are skipped when reduced motion is on (`ReduceMotion()`).
- Other storyboards read (FloatingWindow.cs:712, 1062, PanelLayout.cs:393, SettingsWindow.cs:3652,
  PenBar.cs:330, the `FadeTo` family at MainWindow.xaml.cs:1851-2128) animate opacity or a
  translate and are not dependent. `UpdateThemeToggle` (2557) animates a `TranslateTransform`
  `X` on a theme change only.

Impact (estimate): small per frame, but it extends the window in which a press feels
unresponsive from the rebuild's length to the rebuild plus about 190 to 340 ms. Medium
confidence. Test: P1 and P2 around a dial press with Settings > reduced motion on and off.

### S19. Costs that exist only while a window or panel is open (second pass)

Each of these is conditional, and P0 should record which are open.
- **Brushes window.** It subscribes to `ToolUiChanged` (MainWindow.xaml.cs:6042) and stays
  subscribed. While open, `Refresh` (BrushesWindow.cs:396-401) rebuilds the body (14
  `BrushCell`s and about 13 `ToolCell`s with `Icons.Mark`) and `DrawPreview` (556-580)
  allocates a `CanvasImageSource` of about 864 x 410 pixels at 200% and runs the real stroke
  renderer over a 160-point sample with two or three width passes (619-698). `ApplyPreset`
  fires `ToolUiChanged` twice, so a pen press does this twice. The cell marks are cached
  (186, 750-754). The first show costs 14 real-renderer renders.
- **History window.** `Refresh` runs on every `UndoManager.Changed` (MainWindow.xaml.cs:265)
  and is guarded by `IsOpen` (HistoryWindow.cs:100-103). Open, it rebuilds a row of two text
  blocks per undo entry and copies the whole stack (`History`, UndoRedo.cs:1604); ink undo
  is unbounded (the comment near UndoRedo.cs:1620). One rebuild per finished stroke, O(undo
  depth).
- **Measurement panel.** `ChromeBars.OnViewChanged` (ChromeBars.cs:1271) calls
  `SyncReadouts` (582-594), which calls `MeasurementMenu.Sync` (635-662) on every pan or
  zoom event. That calls `PaintLock` twice (671-681), each an `Icons.Mark` plus new brushes
  for about 14 chips. Estimate 0.3 to 1 ms per event while the panel is open.
- **Colour wheel.** Its two 16 ms timers (ColorWheel.cs:339, 342) are not a standing cost:
  `_spin` starts only after a flick or a wheel notch (2491, 2524) and stops when the
  velocity settles (about 1.4 s at the maximum, Decay and StopVel at 95-96); `_anim` runs only
  during the entrance and exit cascade, about 280 ms in and 180 ms out, with a wall-clock
  cap (2856-2861). At rest neither runs. If the wheel is hidden mid-glide, `_spin` keeps
  ticking for up to 1.4 s on a collapsed canvas. While the wheel is active each draw
  (`OnDraw`, 1729) is a full-window `CanvasControl` redraw: the Copic face draws a fill and
  an uncached text layout per visible cell (about 300 to 600 cells, estimate; the file's own
  comment says about 600 layouts a frame, 1988) and the HSL/RGB face about 1,566
  `FillCircle` calls (2041-2052). Estimate 5 to 15 ms of command building per frame on this
  CPU, on a `DispatcherTimer` that is not vsync-aligned. The overlay is only collapsed, never
  removed, when closed (ColorPickerService.cs:193, 223), so its swap chain stays allocated;
  the GPU cost of a collapsed control is not established.
  `DimChromeForPicker` (MainWindow.xaml.cs:1036-1060) sets opacity 0.32 on the chrome
  clusters and open popups while it is up; if any contain acrylic that adds GPU work.
- **Settings window.** The first open builds about 100 `Icons.Mark` loads plus 18
  `CanvasImageSource` swatches (9 papers, 9 grids; the paper ones trigger S15). A paper, theme
  or font-scale tap clears `_previews` and rebuilds the whole tab (`GroundAction`,
  SettingsWindow.cs:494-504; `Refresh`, 442-460): estimate 20 to 80 ms. On the grid page every
  slider move calls `_h.ApplyGrid`, which repaints the page canvas behind the panel, and
  `RedrawPreview` (1211-1227) allocates a new `CanvasImageSource` of about 1000 x 600 pixels
  (`GridArt.Strip`, GridArt.cs:78-88, never disposed explicitly) and redraws up to 6,000
  lattice cells (GridArt.cs:144): estimate 2 to 8 ms per move plus a canvas repaint.
- **Value popover** (size and opacity sliders). Each move calls `Commit` then `Sync`
  (ValuePopover.cs:324-330, 237-248), which clears and rebuilds four tick rectangles and
  their brushes (`BuildTicks`, 286-310). Small, plus whatever the host does per step (S4: a
  dial refresh).
- **Background and grid sliders.** Each callback calls `SetPageBackground` (7594-7602):
  `ApplyTextContrast` (7631-7653) flushes the text boxes and runs a regular expression over
  every box's RTF, then `Surface.Refresh`, `SyncThemeToPage` (coalesced to 60 ms) and
  `ScheduleSave`. The grid spacing, opacity and colour handlers pair `Surface.Refresh` with
  `ScheduleSave` per tick (6071-6097, 7737, 8075, 8083). `Surface.Refresh` is called from 34
  sites.

### S20. Non-default or niche paths, and a list of every timer (second pass)

Costs that apply only to a setting or tool, with the default stated:
- **Accent follow** (default `Manual`, NoteModels.cs:785). With `Pen`, every `ApplyPreset`
  (3779-3780) starts `ApplyAccentLive` (2670-2701): a 16 ms timer for 220 ms, each tick
  setting about eight application resources, writing brushes and calling `Surface.Refresh()`
  (2706), so about 14 full repaints, then `BuildPenStrip` (2687), then 280 ms later
  `RefreshThemeForAccent` (2739-2753) flips `RootGrid.RequestedTheme` to the other value and
  back (a full re-resolve of every theme resource in the tree) and restarts the glow timer for
  a ripple. With `Notebook`, the same on every page switch (4221). If the owner has followed
  the pen, this is the strongest button-press suspect in the app; P0 must record it.
- **Motion blur** (a setting, default off: `MotionBlur` is a plain `bool`, NoteModels.cs:799;
  Settings toggle at MainWindow.xaml.cs:6457). When on, `TrackViewVelocity`
  (InkSurface.cs:5964-5977) starts the 33 ms `_blurDecayTimer` (304, 569-579) after any
  view movement above 350 px/s, and while the velocity is above the threshold
  `OnRegionsInvalidated` (5901) takes the blurred path: `DrawRegionBlurred` (6242-6268)
  renders **each region** into a `CanvasRenderTarget` and composites it through a
  `GaussianBlurEffect`. That is a second render target pass plus a blur per region, about a
  hundred times a frame, on exactly the fast-pan frames that matter, and a new render
  target whenever a region's size differs from the last. The decay timer then invalidates
  the whole canvas every 33 ms until the blur lands. If the owner turned this on, it is
  a first-order cause of slow panning; P0 must record it. Not the default.
- **Text tool caret.** A 530 ms timer calls a full-canvas `Invalidate()` (InkSurface.cs:565)
  for as long as a pending caret exists: two full repaints a second in the Text tool.
- **Rotate and select tools.** `RotateDragTo` (InkSurface.cs:5447-5476) allocates a
  `RotateFreeMixedAction` per pointer move, rotates every point of every selected stroke,
  recomputes the selection bounds with another O(points) pass (4145-4146), marks the grid
  and ink cache dirty (`_gridDirty` makes the next index query rebuild it all), fires
  `SubjectMoved`, and does the full invalidate. Estimate a few milliseconds per move for a
  large lasso. Restyling a selection (size, opacity, colour) pushes an undo action that
  records each stroke, rebuilds the subject with `ColorUtil.Parse` per stroke
  (4307-4412), and because the new value differs, fires `SelectionState.Changed` and so the
  S4 refresh, per tick of the control. Whether the value popover coalesces ticks was not read.
- **Shape recognition hold timer.** `HoldTick` (InkSurface.cs:7416-7444, timer 140 ms at
  407) runs while a pen stroke is live and recognition is on. Once the pen has rested for
  620 ms it runs `RecognizeShape` (7451-7560: several O(n) passes, recursive corner finding)
  over the whole wet stroke and retries every 620 ms. Cost only when the pen rests
  mid-stroke; sub-millisecond to a few milliseconds.
- **Peer sync timer** (`_syncTimer`, MainWindow.xaml.cs:10275-10293, 20 s). On the UI thread
  `SyncLog.MergeForeign` (SyncLog.cs:402-468) globs the folder; if no peer log grew it
  returns quickly. If one did (another device through OneDrive), it deserialises the ops,
  rebuilds the whole shadow with `Entities(lib)` (the S5 serialisation again), then the
  handler runs `BuildTree`, `LoadPage` and `ScheduleSave`. Needs a second device.
- **Audio.** A 100 ms thread-pool timer per opened page with audio (AudioPlayer.cs:43). During
  playback `PositionChanged` reaches `UpdateAudioPlayerPosition` (MainWindow.xaml.cs:
  12456-12477), which calls `Surface.Refresh()`: a full repaint at about 10 Hz.
- **Gallery.** It builds `MenuFlyout`s eagerly for every notebook card, section and page
  chip; `BuildGallery` is called from about fifteen handlers and chip clicks call `BuildTree`
  first (5532). Gallery open only, and at launch if it is the start screen (797).
- **Exports.** `ExportWindow`, `PdfExporter` and `HtmlSvgExporter` run on the UI thread
  (`CaptureAsync` up to 8192 pixels, `BgraToRgb`, Deflate, indented JSON; PdfExporter.cs:106-107,
  516-535), so an export freezes the app for its length. Never while drawing.
- **Thumbnails.** `RenderPageThumbnail` (InkSurface.cs:10865-10976) is static, blocks on
  `.Wait()`, uses `CanvasDevice.GetSharedDevice()` (probably the canvas's own device) and draws
  every segment of every stroke with no culling. Who calls it and how often was not read
  (ThumbnailCache.cs is unread). Consistent with the first pass's "gallery only"; if it also
  runs after saves it competes for the GPU.
- **Hover.** `Surface.PointerMoved` in MainWindow.xaml.cs (625) calls `GetCurrentPoint` on
  every unhandled move (pen hover is frequent); `InkSurface.OnPointerMoved` already does.
  Under 20 microseconds, estimate.
- **Mica.** With Mica active the root background has alpha 0xD0 (2494-2498). Whether that
  costs GPU time is not established.

Every timer and per-frame hook in the app, as the second pass found them:

| Timer or hook | Where | Runs | Verdict |
|---|---|---|---|
| Glow, 40 ms | MainWindow.xaml.cs:1226 | forever unless glow Off or reduced motion | S6 |
| Save, 1.5 s | MainWindow.xaml.cs:106 | one-shot, re-armed by `ScheduleSave` | S5, S12 |
| Peer sync, 20 s | MainWindow.xaml.cs:10275 | forever, cheap unless a peer log grew | S20 |
| Storage problem poll, 20 s | MainWindow.xaml.cs:2179-2200 | forever, cheap | harmless |
| Status 3 s, zoom readout 1.2 s | MainWindow.xaml.cs:107, 108 | one-shot | harmless |
| Ground 60 ms, accent 280 ms, equation debounce 280 ms, veil 600 ms | 2359, 2650, 9560, 1132 | one-shot | harmless |
| Accent lerp 16 ms | MainWindow.xaml.cs:2678 | 220 ms burst, only when accent follow is on | S20 |
| Zoom settle 260 ms | InkSurface.cs:34 | one-shot | S9 |
| Blur decay 33 ms | InkSurface.cs:304, 569-579 | only after a fast pan or zoom, and only if Motion blur is on (default off); each tick is a full invalidate | S20 |
| Replay 16 ms | InkSurface.cs:377 | only while a replay plays | harmless |
| Caret 530 ms | InkSurface.cs:385 | only in the Text tool | S20 |
| Hold 140 ms | InkSurface.cs:407 | only while a pen stroke is live | S20 |
| `VeilTick` (Rendering) | InkSurface.cs:4970 | 130 to 190 ms bursts, image selection | S17 |
| Dial assign timer, 550 ms | ToolWheel.cs:534, 2301 | one-shot, started on pressing a sector and stopped on move or release | harmless |
| Colour wheel spin and anim, 16 ms | ColorWheel.cs:339, 342 | bursts, wheel open | S19 |
| `FullscreenChrome` Rendering | FullscreenChrome.cs:429 | 130 to 190 ms slides | harmless |
| Audio, 100 ms | AudioPlayer.cs:43 | pages with audio | S20 |

Items found harmless in the second pass, so they need not be read again: `ApplyToolbarVisibility`
(about eleven `Visibility` sets), `UpdateUndoButtons`, `RootGrid.GettingFocus`, the 18
keyboard accelerators, `JellyDrag` and `BeginCheapDrag`, `ShowStatus`, the `CometRim`
rectangles (Comet mode only), the calculator, equation dialog, PDF import, command palette
and context menu, `PanelLayout` (reflow coalesced at low priority, driven by size changes
only), `StripScroll`, `ObjectsWindow`, `PagePlate` colour maths, `GridPointEditor` (grid edit
mode only), `GeometryProbe` (off unless `QUILL_GEOM_PROBE`), `SegmentWidth`, hit-testing
(`HitStroke` scans all strokes per tap, which is fine), `DrawTextElement` (only called from
capture, 5602 and 5635, not from the draw path; the comment at 5708 is stale), the
`FlushTexts` and `TextLinksAt` paths, and `EnsureRefFrame` (a null test).

### How the "lag after a button press" splits by button

The report names no button, and the code treats them differently:
- **Pen and tool buttons** pay S4 (the rebuild) and an S2 repaint (`SetTool`,
  InkSurface.cs:995). They schedule no save.
- **Zoom buttons** (MainWindow.xaml.cs:7511-7515) go through the view-change chain (S7), an S2
  repaint, and then an S5 save 1.5 s later.
- **Page settings** (grid, spacing, colour, paper; for example MainWindow.xaml.cs:6071-6097)
  call `Surface.Refresh()` (an S2 repaint) and `ScheduleSave()` (S5).
- **Page navigation** saves immediately (S5, 4195), then rebuilds the text layer (S11).
- **Undo and redo** refresh the dial (S4), rebuild the text layer when the action touches text
  (S11), and schedule a save.

Second pass additions:
- **Pen and tool buttons** also pay S14 (`ApplyFullscreenChrome` twice, and in the Mouse,
  Select and Rotate tools up to three tool-menu builds), S18 (the dial's `Rise` animation on
  the UI thread for 190 to 340 ms) and, if those windows are open, S19 (the Brushes window
  rebuilds twice per pen press). With Accent follow set to Pen, S20's retint adds about 14
  repaints, a strip rebuild and a theme flip.
- **The pen colour picker** pays S13 on every drag step: the S4 rebuild plus a synchronous
  settings.json write.
- **Page navigation** saves twice (S12), may rebuild the whole chrome cascade if the paper
  colour differs (S14), may bake a paper tile (S15), and rebuilds the text layer (S11).
- **Selecting or deselecting something** builds the selection bar and mode bar two or three
  times (S14); selecting an image adds the fade of S17.
- **Settings buttons** (paper, theme, font scale) rebuild the Settings tab (S19) and may bake
  paper tiles (S15); the first open of Settings builds about 100 icons and 18 swatches.
- **Delete, duplicate, paste** rebuild the text layer whether or not text is involved (S11).

So "lags after a button press" may be two different things on two different buttons, and P2
and P8 together will say which.

### What was looked at and is not a suspect

- Oil paint at rest: `DrawPaint` (InkSurface.cs:6007-6040) is one `DrawImage` per visible
  lit tile, and `PaintTileStore` writes on its own schedule outside library.json.
- Thumbnails: rendered on the thread pool (ThumbnailCache.cs, `GetAsync`), gallery only.
- Diagnostics: the only logging in the draw path is the render-failure line to crash.log
  (InkSurface.cs:5948), written only on an exception; `GeometryProbe` is off unless
  `QUILL_GEOM_PROBE` is set; no `DebugSettings` are touched.
- `FlushTexts` (1090-1125) is cheap for boxes nobody has touched.

## 4. Instrumentation plan

Goal: for each suspect, a number that confirms or clears it, taken on the owner's laptop,
in a Release build, without risking the real library.

### 4.1 Ground rules

1. **Measure on a copy, never on the live library.** Setting `QUILL_DATA_FOLDER` isolates the
   library, settings, backups, op log and its cursors (LibraryStore.cs:60-75,
   SyncLog.cs:73-89), and puts paint tiles under a different hash. Copy `library.json` and
   `settings.json` into a folder outside `Documents\Quill`, point the variable at it, and
   close the normal Quill first (two instances on one library trip the conflict guard,
   LibraryStore.cs:722-738). Paint will not be present in the copy; that is fine for these
   measurements.
2. **Release x64 only**, except for the one deliberate Debug-versus-Release comparison.
3. **Probes are off unless `QUILL_PERF_PROBE=<file>` is set**, and cost one boolean test
   when off. The file must not be inside `LibraryStore.Dir`.
4. **Switches that change behaviour are honoured only when `LibraryStore.IsIsolated` is
   true** (that is, `QUILL_DATA_FOLDER` is set), so a probe build can never turn off saving
   for the real library.
5. **Plugged in, Windows power mode "Best performance", display at 120 Hz** (Settings >
   Display > Advanced display; "Dynamic" can drop to 60 Hz), and Task Manager's Processes tab
   showing Quill is not in Efficiency mode. Record the answers (P0).
6. **Calibrate against Concepts with the same tool**, so "120" is a measured baseline and
   not a feeling: record the same pan in Concepts with PresentMon (below).

### 4.2 The probe class

One new file, `src/Quill/Helpers/PerfProbe.cs`, in the style of `GeometryProbe.cs`:

- `static readonly bool On` from the environment variable, resolved once.
- Hot-path calls write a timestamp, an id and a value into a preallocated array of structs.
  No strings and no allocation in the draw or input paths. (`GeometryProbe` appends to a file
  on every call, which is right for rare events and wrong for per-region timing.)
- A background thread appends the buffer to the CSV file once a second.
- `PerfProbe.Activity` is a static string that the heavy paths set while they run
  ("save", "dial-refresh", "load-page", "pen-strip"), so a stall can be given a name.

### 4.3 The probes

**P0. Build and machine identity (no code beyond one startup line).**
- Which binary: right-click the shortcut > Properties > Target, and note whether the path
  contains `\bin\x64\Debug\` or `\Release\`. In code, log
  `typeof(App).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled`
  (true means Debug code), `Environment.Is64BitProcess`, `GC.GetGCMemoryInfo()` and
  `System.Runtime.GCSettings.IsServerGC`, `ToolSurface`, `GlowMode`, the monitor refresh
  rate, whether the library path is under OneDrive, and whether the process is in Efficiency
  mode.
- Confirms or clears S1 (with the Debug-versus-Release runs in 4.5). Also records the
  settings that decide which chrome is on.
- Second pass: also record `AccentFollow` (S20; the default `Manual` is harmless, `Pen` and
  `Notebook` are not), `MotionBlur` (S20; default off), `ShapeRecognition`, `Liquidness`,
  the paper kind and whether it is a white-stock paper (S15), which of the Brushes,
  History, Measurement, Objects and Settings windows are open during the scenarios (S19),
  the tool in use (Mouse, Select and Rotate have the S14 triple build), and whether the
  peer sync folder holds other devices' op logs (S20).

**P1. UI frame clock.** `CompositionTarget.Rendering` (already used in this repo,
FullscreenChrome.cs:429, InkSurface.cs:4970) fires once per composed frame on the UI thread.
Record the interval between events; report p50, p95, p99 and the share over 8.3, 16.7 and
33.3 ms per scenario. Attach it only in measurement runs: a Rendering handler keeps the frame
loop running, which is a small cost of its own.

**P2. UI-thread stall detector.** A background thread posts a no-op to the dispatcher every
4 ms (`DispatcherQueue.TryEnqueue`) and records how long each took to run. Any delay of 20 ms
or more is logged with `PerfProbe.Activity` and the time since the last pointer or key event.
This is the single most useful probe for S4, S5 and S9: it turns "it froze for a bit" into a
list of stalls, each with a name and a length.

**P3. Paint pass anatomy.** In `OnRegionsInvalidated` (InkSurface.cs:5901): the pass length,
the number of regions (`args.InvalidatedRegions.Count`), their total area and bounding box,
the time since the previous pass, and the longest single region. Inside `DrawRegion`,
accumulate stopwatch ticks into static counters, reset per pass, for: lines 6287-6300 (clear,
paper, grid, perspective, artboard, title), the shapes loop, the strokes step, the wet-stroke
block (6476-6498), and the overlays. Fit pass time against region count across scenarios.

**P4. Ink counters.** Per pass: strokes tested, strokes drawn, points drawn, `DrawLine` calls
(count in `DrawStroke`, 7234, and in `DrawGrid`), `CanvasGeometry` builds (7318),
`ColorUtil.Parse` calls, whether the ink cache served the pass, and
`GC.GetAllocatedBytesForCurrentThread()` before and after.

**P5. Input path.** In `OnPointerMoved` (2228): handler duration, number of intermediate
points, and `now - PointerPoint.Timestamp` (event age when handled; the timestamp is QPC-based
microseconds, treat as indicative). Store the time of each `Invalidate` and report the delay to
the next `OnRegionsInvalidated`: that is the app-side part of ink latency. End-to-end
pen-to-photon needs a camera: film the screen and pen with a phone at 240 fps and count frames.

**P6. Save anatomy.** Time separately: `PersistSettings` (LibraryStore.cs:635), `FlushTexts`,
`JsonSerializer.Serialize` (570), `SyncLog.OnSaved` (573), and on the worker `WriteTemp`,
`PromoteTemp` and `TrySnapshot` (710-748, 772-791, 870). Record the string length, and
`GC.CollectionCount(0..2)` and `GC.GetTotalPauseDuration()` before and after. Count how many
saves a minute a normal session produces, and what triggered each (stroke, view, button,
page turn).

**P7. View-change chain.** Wrap `ViewChanged?.Invoke()` (InkSurface.cs:791) and, inside
`MainWindow.OnViewChanged` (7517), the `ScheduleSave` call. Report microseconds per event and
events per second during the pan and zoom scenarios.

**P8. Chrome rebuild.** Time `SelectTool` (6806), `ApplyPreset` (3764), `BuildPenStrip`
(3121), `ToolWheel.Refresh` (1014), `PenBar.Refresh` (394) and `SyncBottomMenu`; count calls
to `XamlReader.Load` (increment a counter in `Icons.Geo` 855, `ParseGeometry` 3043,
`MakeIconPath` 3100 and FloatingWindow.cs:1104); and record the time from the click handler's
return to the next `Rendering` event, which includes the layout and render of the rebuilt
trees. Count `ToolWheel.Refresh` calls per finished stroke.

**P9. Compositor and resolution switches (experiments, isolated runs only).**
- `QUILL_PERF_NOGLOW=1`: stop the glow timer (same as Settings > glow Off; no code needed).
- `QUILL_PERF_SOLIDCARDS=1`: replace `CardBrush` and `CardBrushFloat` with solid colours
  at startup.
- `QUILL_PERF_NOSAVE=1`: skip the autosave tick (the page-turn and close saves stay).
- `QUILL_PERF_DPISCALE=0.5`: set `CanvasVirtualControl.DpiScale` (to be confirmed present in
  the Win2D 1.4.0 API; if it is not, this experiment is dropped). Note the DPI plumbing at
  InkSurface.cs:494-527 (`PaperTextures.SetDisplayDpi`), which re-bakes on a DPI change.
- `QUILL_PERF_NOGRID=1`: skip `DrawGrid`.

**P10. GC and heap.** Without code: `dotnet-counters monitor --process-id <pid>
System.Runtime` (gen-0/1/2 counts, allocation rate, gen sizes, GC pause time) in a second
window during the scenarios. In code: the same figures at the start and end of each scenario
step.

**P11. Page census.** At `LoadPage` (852) and at each scenario step: strokes, total points,
mean points per stroke, shapes, text elements, comments, whether paint is present, the
grid type and spacing, the paper, the layer count, and whether the ink cache is in use.
Also, per pass, how many strokes intersect the viewport.

**P12. Headless save-cost harness.** A console tool in the pattern of tools/CloneRoundTrip
(it links NoteModels.cs, LibraryStore.cs, SyncLog.cs and a stand-in for the thumbnail cache,
and isolates itself with `QUILL_DATA_FOLDER`). Load a **copy** of the real library, then time,
over ten repetitions after one warm-up: `JsonSerializer.Serialize(lib)`, `SyncLog.OnSaved`
with a one-stroke edit and with none, and the file write. Report time, allocated bytes and GC
counts. This isolates S5 completely from the UI and the GPU, and it is the benchmark that the
save fixes (F5, F6) must beat.

**P13. Save count and cause (second pass).** In `LibraryStore.Save` and `SaveSettings`
log the time, the caller (an enum passed from `SaveNow`, the timer tick, `SwitchToPage`, the
close handler, an export loop), the duration, and whether the library changed since the last
save (a counter bumped by `ScheduleSave`, `UndoManager.Changed` and `MergeForeign`). Run the
scenario "turn the page, touch nothing, wait 5 s": S12 predicts two saves, the second 1.5 s
after the first and with no change between them. Also count `SaveSettings` calls per second
during a pen colour drag (S13).

**P14. Cascade counters (second pass).** Increment named counters, reset per user action, in
`BottomMenu.Changed` (MainWindow.xaml.cs:971), `BuildToolMenu`, `BuildRotateMenu`,
`SelectionChrome.Build` (SelectionChrome.cs), `PageTheme.Changed` raises, `ApplyFullscreenChrome`
(8590), `ToolWheel.Refresh`, `PenBar.Refresh`, `BuildPenStrip` and `RebuildTextLayer`
(InkSurface.cs:10013). Report them per action in the F scenario and in a new select/deselect
step. This turns the S4, S11, S14 claims about "how many times" into counts.

**P15. Text-layer build (second pass).** Time `RebuildTextLayer` and `BuildTextUi` per box,
split into the `RichEditBox` creation, `SetText`, and `AutoSizeBubble`; report boxes built,
boxes skipped by layer, total time, and the caller (a string passed in). Needed for S11 and
for the delete, duplicate and paste cases.

**P16. Paper bake (second pass).** Time `PaperGrain.Bake` per call with the paper kind, ground
colour and the caller (page draw or Settings preview), and count `PaperTextures.Invalidate`
calls. Confirms S15 and shows whether `SetDisplayDpi` invalidates unexpectedly (for example on
a monitor refresh-rate or scale change).

**P17. Veil (second pass).** Count `VeilTick` frames, text boxes visited and the pass time
during an image selection and deselection. Confirms S17.

**P18. Per-region shape cost (second pass).** In `DrawShape`, accumulate ticks by shape kind
(table, equation, image, other) and by grain passes, per pass, beside P3. Only meaningful on a
page that has them; P11 tells which page that is.

**P19. Windows open (second pass).** Log `Visible` for each floating window at the start and end
of each scenario step, and time `BrushesWindow.Refresh`/`DrawPreview`, `HistoryWindow.Refresh`,
`MeasurementMenu.Sync` and `ColorWheel.OnDraw` while open. Needed to decide whether S19 matters
for the owner's way of working.

**External tools, no code.**
- Task Manager > Performance > GPU and the Processes tab's GPU engine column: is the 3D
  engine busy while the UI thread is saturated (GPU-bound) or idle (CPU-bound)? Watch the
  per-core graph to see whether the UI thread sits on an efficiency core.
- PresentMon (the Intel/Microsoft capture tool) to record frame times of what is presented.
  For a XAML app the presenting process is the desktop compositor, so use it on the
  compositor process as well, and rely on P1 and P3 for the app's own cadence. Use the same
  tool on Concepts.
- `dotnet-trace collect --profile cpu-sampling` on the Release process, or the Visual
  Studio CPU Usage profiler, during a pan: the top methods on the UI thread settle S1 to S3
  and S7 without any code.

### 4.4 The scenario script

One script, run the same way every time. Each step is 10 to 20 seconds, ending with a 5 s
pause with hands off the pen (the autosave stall lands there).

| Step | What the owner does | Exercises |
|------|---------------------|-----------|
| A | Idle on a page, no input, 20 s | glow timer, background work (S6) |
| B | Hover the pen over the page; then again with the eraser tool | S10 |
| C | Write 20 strokes at normal speed | S3, S4 (dial refresh per stroke), S5, S9 |
| D | Pan continuously across the page with two fingers, then with the mouse wheel | S2, S3, S7 |
| E | Zoom with Ctrl+wheel from 100% to 400% and back, three times | S2, S7, S9 |
| F | Press ten pen and tool buttons in turn (pen, pen, eraser, mouse tool, pen, undo, redo...) | S4 |
| G | Switch between two pages five times | S5, S11 |
| H | Hands off for 30 s after the last edit | S5, S8, S6 |
| I (second pass) | Turn the page, touch nothing for 5 s; repeat to a page with a different paper colour | S12, S14, S15, S11 |
| J (second pass) | Select a stroke, deselect; select an image, deselect; choose the Mouse tool and the Rotate tool | S14, S17 |
| K (second pass) | Delete, duplicate and paste a stroke on a page with many text boxes | S11 |
| L (second pass) | Drag a pen's colour on the wheel for 5 s; drag the page background colour for 5 s | S13, S19 |
| M (second pass) | Repeat F with the Brushes window open, then with the History window open | S19 |

Pages: a **blank** page (no ink, no grid), the owner's **densest** page, and the same dense
page with a dotted or square grid on.

### 4.5 The run matrix

| Run | Build | Library | Purpose |
|-----|-------|---------|---------|
| 1 | Debug (as launched today) | copy of the real one | the baseline the owner sees |
| 2 | Release | copy of the real one | S1; the baseline for everything after |
| 3 | Release | an empty folder named by `QUILL_DATA_FOLDER`, with a copy of the real settings.json beside it (the app seeds a one-page notebook) | separates data size (S3, S5, S8, S9) from structure and chrome (S2, S4, S6) |
| 4 | Release | copy of the real one, with glow off and solid cards | S6 |
| 5 | Release | copy of the real one, `NOSAVE=1` | S5 |
| 6 (second pass) | Release | copy of the real one, Motion blur off, Accent follow Manual, no optional windows open | removes S20's optional costs; if the owner's settings differ, runs 2 and 6 differ by exactly those |

Run 3 is the most informative control. If an empty library at Release still cannot hold 120
fps while panning, the cause is structural (S2, S6, the chrome); if it can, the cause is the
size of the data.

### 4.6 What the numbers decide

| Observation | Verdict |
|-------------|---------|
| Run 2's pass time under 60% of run 1's | S1 is a major cause; do F0 first and re-baseline |
| Blank-page pan pass over 6 ms | S2's structure alone prevents 120 fps; F7 and F8 are required |
| Blank-page pass under 2 ms, dense-page pass much longer | ink (S3) is the pan limit; F4 and F10 first |
| Region count near 100 and pass time growing with it | per-region fixed cost; fewer, larger regions (F7, F8) |
| Grid on adds more than 30% to the pass | per-region grid (S2); F4b |
| P2 shows a stall of 200 ms or more about 1.5 s after the last pan, zoom or stroke | S5 confirmed; F1 then F5 |
| Same stall absent in run 5, or P12 under 30 ms | S5 cleared as a cause of that stall |
| P12 over 300 ms for Serialize plus OnSaved | S5 is a large cause; F6 is justified |
| A button press costs over 10 ms of handler time, or over 20 `XamlReader.Load` calls | S4 confirmed |
| Gen-2 pause total over 100 ms per minute outside saves | S8 matters; otherwise cleared |
| Glow off or solid cards moves pass cadence by more than 15% | S6 confirmed |
| Ink cache rebuild over 100 ms after a stroke on a page of 2,500+ strokes | S9 confirmed |
| P1 and P3 agree (frames lost on the UI thread) while Task Manager shows the GPU idle | CPU-bound: fix the managed path first |
| UI thread idle while the GPU 3D engine is near 100% | GPU-bound: resolution and overdraw (F3, F7, F8) come first |
| P13 shows two saves after one page turn with no edit | S12 confirmed; F5c |
| P13 shows `SaveSettings` called during a colour drag, or taking over 5 ms | S13 confirmed; F15 |
| P14 shows more than 3 `BottomMenu.Changed` or more than 1 `SelectionChrome.Build` per selection change | S14 confirmed; F14 |
| P15 shows over 100 ms for `RebuildTextLayer` on delete, duplicate or paste | S11 confirmed as a button-press cost; F12 |
| P16 shows a bake over 50 ms in the draw callback | S15 confirmed; F13 |
| P17 shows `VeilTick` over 8 ms per frame, or running over 15 frames | S17 confirmed; F16 |
| `MotionBlur` on and run 6 pans noticeably faster | the owner's setting is a cause; turn it off or make it cheaper (S20) |
| `AccentFollow` is Pen or Notebook and a press is faster with Manual | S20 accent retint is the press cost |

## 5. Fix plan

Ordered by expected gain for the effort, with the risk each step carries for notes and for
paint stated in the same place. Nothing here has been prototyped.

### 5.1 Data-safety rules, and gates for every step

Rules. Nothing in this plan may break these, and a step that needs to is not ready:
- The write sequence stays exactly as it is: temp file, flush to disk, replace with a named
  `.bak`, rolling snapshots, and the unconditional flush at close (LibraryStore.cs:710-899,
  693-698; MainWindow.xaml.cs:341).
- library.json's format does not change before F11, and F11 only with a migration.
- The settings mirror (`PersistSettings`) stays reachable from every setting change.
- The paint tile store and its writer (PaintStore.cs) are not edited by F0 to F10.
- The op log and its cursors (SyncLog.cs) are not edited before F6, and F6 follows its own
  rule about deletions.
- Every measurement and every trial runs on a copy of the library (section 4.1).

Gates. Every step must also pass these:

1. **Measured.** The probe that motivated the step improves by the amount stated, on the
   copy, in Release, and the other probes do not regress.
2. **Library untouched.** SHA-256 of the real `library.json` and `settings.json` is the same
   before and after any work session on the owner's machine (the screen runs already record
   size, mtime and SHA-256 this way, docs/VISUAL-PASS-RESUME.md:4435 and 5000).
3. **Harnesses.** The ten tools/ harnesses (each a console project under tools/) build and
   pass, and `LayerRoundTrip`'s check count does not fall (it reported 83 in run 25; note the
   current figure before starting). docs/VISUAL-PASS-RESUME.md:5207-5209
   describes a `scratchpad/w4_harness.ps1` that builds and runs all ten and reports build and
   run separately; that script is not in the tree I read, so it may need rewriting. Steps that
   touch the draw order must keep `DrawPlan`'s order (CONCEPTS-REF section 58.4, cited at
   InkSurface.cs:6308); steps that add a field to the model must keep `CloneRoundTrip` green.
4. **On screen.** Any step that touches drawing or chrome gets a maximised screen run, not
   only a clean build and green harnesses: earlier runs found defects only on screen (the
   "found on screen" items in docs/TODO.md, Wave 8). When driving the app with injected
   input, run a known-good control stroke first, because a blank capture can mean the
   gesture never started rather than that the draw path failed
   (docs/CONCEPTS-REF-2026-08-07.md:2379-2383, docs/VISUAL-PASS-RESUME.md:5024-5029).
5. **One step per commit**, each reversible by a revert.

### 5.2 The steps

| Step | What | Gain (estimate) | Effort | Risk to notes | Risk to paint |
|------|------|-----------------|--------|---------------|---------------|
| F0 | Run Release against a copy | possibly the largest single gain | minutes | none | none |
| F1 | A pan or zoom no longer schedules a library save | removes a 53 MB save after every view change; cuts S7 | hours | very low | none |
| F2 | Stop rebuilding the dial and pen strip on every press and stroke | button-press lag; stroke-lift jitter | 1 to 2 days | none | none |
| F3 | Cheap compositor and resolution experiments | unknown; test-first | hours | none | none |
| F4 | A cheaper paint pass that draws the same pixels | large on grid pages and dense pages | days | none | low (z-order) |
| F5 | Never save mid-gesture; skip saves with nothing to save | removes page-turn and idle stalls | hours | low to medium | none |
| F6 | Per-page serialisation cache and an incremental op log | UI-thread save from seconds to tens of ms | about a week | the highest in this plan | none |
| F7 | Fewer pixels while the view moves | 2 to 4 times on pan and zoom | days | none | none |
| F8 | Let the compositor do the panning | pan at the display rate on any GPU | weeks | none | low |
| F9 | Wet ink on its own small layer | pen latency and per-move cost independent of the page | days | none | low |
| F10 | A better settled-ink cache (device resolution, incremental, or command lists) | dense pages | days to a week | none | low |
| F11 | Data model and file layout | GC, save size | weeks | high | none |
| F12 (second pass) | Text layer: rebuild only what changed, and make a box cheaper | delete, duplicate, paste, scale, page turn on text pages | days | none | none |
| F13 (second pass) | Bake paper tiles off the UI thread, or ahead of time | page-turn and first-open hitches | 1 to 2 days | none | none |
| F14 (second pass) | Coalesce the bottom menu, selection chrome and theme cascades | press and selection lag | 1 to 2 days | none | none |
| F15 (second pass) | Colour picker: do not save settings or rebuild the strip per callback | colour drag | hours | very low | none |
| F16 (second pass) | A cheaper selection veil | hitch on image select | hours to a day | none | low |
| F17 (second pass) | Per-pass shape work: tables, equations, grain, images | pages that have them | days | none | low |

### F0. Run Release against a copy

Close Quill. From the repo root, in PowerShell:

```powershell
dotnet build src\Quill\Quill.csproj -c Release -p:Platform=x64

$data = "D:\quill-perf\data"        # any folder OUTSIDE Documents\Quill
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item "$env:USERPROFILE\Documents\Quill\library.json"  $data
Copy-Item "$env:USERPROFILE\Documents\Quill\settings.json" $data

$env:QUILL_DATA_FOLDER = $data      # isolates library, settings, backups, op log
& "src\Quill\bin\x64\Release\net8.0-windows10.0.19041.0\Quill.exe"
```

This does not touch the Debug output or the live library. If Release is clearly smoother,
repoint `Quill.lnk` at the Release exe (and note in the README that day-to-day use is
Release; Debug is for stepping through code). Whether to also set `TieredPGO` or
ReadyToRun is a later question, answered by P0 and a startup comparison, not a first move.

- Gain: unknown until run; potentially the largest single change, because it multiplies the
  managed share of everything below.
- Notes: no risk (a copy; same code, same file format). When it is later run on the real
  library, close the Debug instance first.

Second pass amendments to F0 to F11: F1 and F5 gain the items below (F1 also drops the
`PersistSettings` reflection pass from view changes, which it already did by not calling
`ScheduleSave`); F2 gains items 5 to 7; F3's resolution experiment must not feed the changed
DPI to `PaperTextures.SetDisplayDpi` (corrections, item 9); F4 gains the tables and
equations (F17); the order of work in section 1 is unchanged except that F5c, F15 and F12's
first item are cheap enough to go beside F1.

### F1. A pan or zoom does not schedule a library save

Split `ScheduleSave` so that `OnViewChanged` (MainWindow.xaml.cs:7517) does not call
`PersistSettings` (a pan changes no setting) and does not arm the 1.5 s timer for the sake of
a view offset. The page's view position is already written into the page model on every step
(InkSurface.cs:781-783), so it is saved with the next real save, at the next page turn, and
at close (341). Nothing else needs to change.

- Gain: removes the S5 stall that follows every pan or zoom, and S7's reflection and JSON
  work per event.
- Notes: the only thing that can be lost is the last view position after a crash, never
  content. The settings mirror (`PersistSettings`) exists so that a changed setting survives
  a crash (MainWindow.xaml.cs:2147-2150); it stays, called from the places that change a
  setting.
- Paint: none.
- Gate: P6 shows zero library saves after a pure pan; reopening a page after a normal close
  restores its view.

### F2. Stop rebuilding the chrome on every press and every stroke

In order of gain per effort:
1. In `ApplyPreset` (MainWindow.xaml.cs:3764), do not call `BuildPenStrip()` when only the
   active pen changed. `RefreshPenSelection()` (3087) already moves the lift; update the
   active chip's colour path in place. Call `BuildPenStrip` only when the list of presets or a
   preset's pen type or colour changed.
2. Coalesce `ToolUiChanged` to one invocation per dispatcher turn, so `SelectTool` followed by
   `ApplyPreset`'s own call refreshes once.
3. Make `ToolWheel.Refresh` and `PenBar.Refresh` update in place: keep each sector's mark and
   rebuild it only when the slot's tool or pen id changed; otherwise just set the existing
   shapes' brush colours. Undo-stack changes need only the two undo/redo marks, and only when
   `CanUndo` or `CanRedo` actually flipped.
4. Cache parsed icon geometry. A WinUI `Geometry` cannot be shared between two elements, so
   caching the object is not enough; either reuse the shape and change its colour (as in 3),
   or write a small parser for the path mini-language that builds a fresh `PathGeometry`
   (the icon data uses M, L, H, V, C, S, Q, T, A and Z). Do the first; the second only if
   something still needs to create icons on a hot path.

5. (second pass, S14) Make `BottomMenu.Sync` raise `Changed` only when the top page or the
   back-button state actually changed (compare with the previous values), and have
   `SyncBottomMenu` build its menu once per call instead of once itself and once more through
   each `Publish` and `Retract`. Give `SelectionChrome.Build` the same guard, and skip it
   when the mode is `None`. Wrap `ApplyFullscreenChrome` so it returns when the presenter
   kind, fullscreen state and strip reserve are unchanged, and assign each `Thickness` only
   when it differs.
6. (second pass, S18) Keep the dial's `Rise` and `Drop` as they are until P1 and P2 show them
   in the stall list; if they do, replace the dependent animations with composition
   animations (`Visual` scale and opacity through `ElementCompositionPreview`), which run off
   the UI thread.
7. (second pass) Make `PageTheme.Changed` subscribers repaint lazily: closed windows
   (`FloatingWindow`, `ValuePopover`, `MeasurementMenu`) mark themselves stale and repaint on
   open instead of loading icons at once. `MeasurementMenu.Rebuild` should test
   `IsOpen`, not `_root.Parent`.

- Gain: a press goes from tens of milliseconds to a few; the dial stops costing anything
  when a stroke ends.
- Notes and paint: none; this is UI only.
- Gate: P8 shows under 5 `XamlReader.Load` calls and under 5 ms handler time per press; an
  on-screen run of the dial, the strip and the bar in light and dark themes and on a dark
  paper, because these surfaces capture their colours at build time (ChromeBars.cs:511-512)
  and an in-place update is where a stale colour would hide.

### F3. Quiet the compositor, and try fewer pixels

Each is a small experiment, kept only if P9 shows a win.
- Pause the glow timer while the pointer is down on the canvas and while the view is moving,
  and while the window is inactive; resume on release. This generalises `BeginCheapDrag`
  (MainWindow.xaml.cs:1781). If glow Off wins decisively, consider making Off the default for
  new installs; it is a user-visible look, so it is the owner's call.
- During a pan, zoom or pen-down, show the acrylic cards that overlap the canvas as their
  plain fallback colour and restore them after (the fallback colours exist,
  App.xaml:16, 20, 29, 31).
- During a view change only, render at a lower `DpiScale` and re-render at full resolution
  when the view settles (`_zoomSettleTimer`, InkSurface.cs:597, already exists for the cache).
  This cuts the pixel and region count by up to four. Ink looks softer while moving. The
  catch is `PaperTextures.SetDisplayDpi` (494-527), which re-bakes textures when the DPI
  moves; it must be fed the real DPI, not the scaled one.
- Gain: unknown; that is what the experiments are for.
- Notes and paint: none; visual only.

### F4. A cheaper paint pass that draws the same pixels

All of these keep the output pixel-identical, and none touches the model's stored fields.
a. **Once per pass, not per region.** Parse the background colour (6287), take the plan, and
   build the list of strokes and shapes that intersect the union of the invalidated regions
   (use the spatial index when there are 128 or more elements, 3244, or one sweep). Each
   region then tests that short list, not every stroke on the page. This turns regions x page
   size into page size plus regions x visible.
b. **Clip the page furniture to the region.** `DrawGrid`, `DrawPerspective`, `DrawArtboard`
   and `DrawPageTitle` take the viewport; give them the region's world rectangle so a region
   draws only the grid lines and dots inside it, and the title only in the region that holds
   it. Direct2D already clips the pixels; this removes the calls.
c. **Colour once.** Keep the parsed colour with the stroke in a non-serialised field,
   invalidated when `Color` is set, instead of parsing per draw (7118). Mark the field so it
   is not written to JSON; `CloneRoundTrip` walks the model by reflection and will fail if a
   field is added and not handled in the clone.
d. **The wet stroke.** Build the temporary `PenStroke` once per pass, not per region, and skip
   segments whose bounds do not meet the region. Better, and separate, is F9.
e. **Cache polyline geometry** for the polyline pens (7309-7320) per stroke, keyed by the
   stroke's point count and offset, and reuse it across regions and frames.
f. **Optional, changes pixels slightly: draw-time decimation.** When zoomed out, skip points
   that are under about one device pixel from the last drawn point. Stored data is untouched.
   Judge it on screen, and keep it a separate commit.

- Gain: a, b and c remove most of the walk cost in S2 and the parse cost in S3; the size
  depends on the page (largest on grid pages and dense pages).
- Notes: none. Paint: the `Paint` and `WetPaint` steps (6362-6373) and the draw plan's order
  must stay exactly where they are; keep `tools/LayerRoundTrip` green.
- Gate: P3 and P4 show the walk and call counts fall; a pixel comparison of captures at the
  same view on five sample pages, before and after, shows zero difference for a to e. The
  capture helper exists (tools/vpsweep/q.ps1: SendInput, watchdog and screen capture) and
  tools/measure_vp.py differences a capture against a baseline; a pixel-diff script for the
  draw pass does not exist yet and would be written with this step.

### F5. Never save mid-gesture; do not save when there is nothing to save

a. In the timer tick (MainWindow.xaml.cs:328), if a stroke or drag is in progress, re-arm
   for 500 ms instead of serialising under the pen. The surface already has the test
   (`StrokeInProgress`, InkSurface.cs:1194, currently private); a small read-only property
   exposes it.
b. Keep a change stamp that every real mutation bumps (`ScheduleSave`, `UndoManager.Changed`,
   `SyncLog.MergeForeign`'s result). The **page-turn** save (4195) is skipped when the stamp
   equals the one at the last successful save, and no more than five minutes have passed.
   **The save at close stays unconditional**, and a full save still runs at least every few
   minutes whatever the stamp says.

c. (second pass, S12) Stop the save timer when a save runs. `SaveNow` (MainWindow.xaml.cs:2159)
   should call `_saveTimer.Stop()` before saving, so a page turn that already saved
   synchronously does not queue a second identical save 1.5 s later. In `SwitchToPage` the
   leading `ScheduleSave()` (4187-4188) is redundant with the `SaveNow` that follows it; keep
   only the settings mirror (`PersistSettings`) there. For the export and AI loops that step
   through pages (10088, 10139, 10213/10221, 9068), pass a flag so the per-page switch does
   not save, and save once at the end.

- Gain: no freeze at a page turn that follows a save; no freeze in the middle of a stroke;
  half the saves per page turn (c).
- Notes: a missed mutation path today is still saved by whatever calls `ScheduleSave` next;
  with a skip it would wait for the close or the periodic save. That is why the periodic and
  close saves are unconditional. Medium-low risk, and the one step in tier 1 that changes
  when saves happen, so it comes after F1 and F2 have been measured.
- Paint: none.
- Gate: P2 shows no stall at a page turn that follows a save; an edit made through each major
  path (stroke, erase, move, shape, text, table, image paste, undo, page rename) followed by a
  page turn and a restart still has its edit (the existing round-trip harnesses cover the
  model; add one case per path).

### F6. Per-page serialisation cache, and an incremental op log

Only if P6 and P12 show F1 and F5 leave a stall that matters. This is the only step in the
plan that can lose notes if it is wrong, so it is the last of the save steps, and it ships
behind a switch.

Design:
- Most of the 53 MB is pages nobody has opened this session. Cache each page's serialised
  UTF-8 and, when the library is written, splice the cached bytes for **cold** pages with
  `Utf8JsonWriter.WriteRawValue` through a converter for `NotePage`, and serialise only
  **hot** pages freshly.
- A page is hot from the moment `LoadPage` makes it current, or any library operation
  touches it (structural edits, import, duplicate, move, rename, paste, sync merge), until the
  app exits. Any structural operation drops every cache and does one full serialisation.
  Conservative on purpose: a wrongly hot page costs time; a wrongly cold page loses an edit.
- `SyncLog.OnSaved` must follow. Its deletion pass (SyncLog.cs:285-290) emits a delete op for
  every shadow key it did not enumerate. **An incremental version that enumerates only the
  hot pages would tell every peer that the whole library had been deleted.** Cold pages' keys
  must be carried forward as seen, and this needs its own test.
- Keep exactly what happens to the bytes afterwards: the temp file, flush, replace, `.bak`
  and fifteen-minute snapshots (LibraryStore.cs:710-899) are unchanged.

Gates:
- A harness (in the P12 pattern, on a copy of the real library) proves the cached output is
  **byte-identical** to the current serialiser's, on the unmodified library and after a
  sequence of mutations through every `IPageAction`.
- An audit mode, on in every measurement run, re-serialises the cold pages and compares hashes
  on a worker thread, and logs any difference.
- A kill switch (`QUILL_FULL_SAVE=1`) restores the current behaviour, and a full save still
  runs at close.

- Gain: the UI-thread part of a save falls from the whole library to the current page, tens
  of milliseconds rather than seconds. The worker still writes 53 MB.
- Notes: highest risk in this document, hence the gates. Paint: none.

### F7. Fewer pixels while the view moves

If F3's resolution experiment wins, make it permanent and tune it: the scale during motion,
the delay before sharpening, and (from P3) whether the win comes from fewer regions or fewer
pixels. Keep the sharpen pass cheap (it is an S2 repaint, once). Visual only; no data effect.

### F8. Let the compositor do the panning

The structural fix for S2. As I understand Win2D's documentation, the virtual control is
built so that a surface larger than the window is slid by the compositor and only newly
exposed rectangles raise `RegionsInvalidated`; confirm that with a small prototype before
committing to this step. Quill instead keeps a window-sized control and moves the picture by
changing what it draws, which dirties every pixel on every step. The change:
- Make the control larger than the viewport by an overscan margin and translate it with a
  composition offset during a pan, so a pan step costs a transform change; redraw only the
  strips that come into view, and re-centre when the margin is used up.
- During a zoom gesture, scale the existing picture with a composition transform and
  re-render at the new scale when the gesture settles (the same trigger as the existing
  260 ms `_zoomSettleTimer`).
- Everything that converts between screen and world coordinates (hit-testing, the
  selection overlay, the text layer's transform, `WorldToScreen`) must follow the new origin.
  That is the bulk of the work and the main risk, and it is why this comes after the cheaper
  steps.
- Pages must still save `ViewX`, `ViewY` and `ViewZoom` with their existing meaning
  (InkSurface.cs:781-783), so a page opens where it was left.

- Gain: panning at the display's refresh rate on any GPU, since a step costs almost nothing.
  It does not help zoom or drawing, which F7, F9 and F10 address.
- Notes: none if the saved view keeps its meaning. Paint: `DrawPaint` composes onto the
  world-space transform (6007-6040) and is unaffected if the transform is composed the same
  way; check oil paint on screen.

### F9. Wet ink on its own small layer

Give the in-progress stroke (and the eraser ring, lasso and ruler) their own transparent
control above the page, so a pen move redraws a few hundred pixels and never runs
`DrawRegion`. On pen-up, commit the stroke to the page, invalidate its rectangle once on the
main control, and clear the wet layer **only after** the main layer has painted that
rectangle, or the stroke flickers out for a frame. It also gives a natural place for
prediction. The oil brush's wet scratch is drawn inside `DrawPaint` (6013-6014, 6037) and is
left alone in the first version.

- Gain: pen latency and per-move cost stop depending on the page; removes the O(n squared)
  of the wet stroke.
- Notes: none. Paint: oil excluded at first; test oil on screen anyway.

### F10. A better settled-ink cache

The current cache is rebuilt in full after every edit, is under screen resolution at 200%,
and only applies at 2,500 strokes (S9). Options, in the order I would try them:
- **Command lists.** Record each stroke (or a spatial chunk of strokes) once into a
  `CanvasCommandList` and replay it with one `DrawImage`. Commands are vector, so ink stays
  crisp at any zoom, interop calls fall from one per segment to one per chunk, and nothing
  has to be re-rendered when the zoom changes. Bake the layer opacity and veil colour into the
  recording, and re-record when they change.
- **Incremental bitmap cache at device resolution**, appending a new stroke to the cache
  instead of dirtying everything. The code carries a warning from the last attempt: an
  "aggressive 600 + per-stroke cache-append dropped just-drawn ink (#hotfix)"
  (InkSurface.cs:181). Any new cache needs a specific on-screen test that ink just drawn
  never disappears, a fallback to the per-stroke path on any exception (as `TryDrawInkCache`
  has), and a switch.
- Gain: dense pages. Notes: none. Paint: the cache holds ink only, and oil paint is drawn
  outside it (6362-6373); keep it that way.

### F11. Data model and file layout

Only if P10 and P12 show, after everything above, that generation-2 collections or the size
of the write still dominate.
- `StrokePoint` as a struct, or a stroke's points as packed float arrays, removes about a
  million heap objects and the pointer chasing in the cull loop.
- A per-page file layout (an index plus one file per page) would make a save write the pages
  that changed, which is the structural answer to S5 and removes the need for F6.
- Both change the on-disk format or the in-memory contract of every consumer (the renderer,
  the exporters, the op log, the harnesses). They need a migration with dual-write for a
  release, a verified reader for the old format, and the bytes-before-bytes-after gate on a
  copy. Highest risk to notes in this document; not for the first round.

### F12. The text layer: rebuild only what changed (second pass)

Answers S11.
1. Callers that need no rebuild must not make one: delete (InkSurface.cs:1352), duplicate
   (1394), paste (5776) and the scale gesture (2859) should rebuild only when the selection or
   the pasted content contains a text element, as 2880 already does. Table edits
   (`ReflowTableCells`, 9121-9123) should rebuild only the table's cells.
2. Keep a box per text element and update it in place (position, size, rotation, colour)
   when only geometry changed; rebuild a box only when its RTF or font changed.
3. Make `BuildTextUi` cheaper: build boxes lazily for those in or near the viewport; avoid
   the second `AutoSizeBubble` on `Loaded` when the first already used a stored size; leave
   `IsSpellCheckEnabled` off until a box takes focus.
4. `ApplyTextVeil` (S17) should look boxes up through a dictionary built once, not
   `FirstOrDefault` per box per frame.

- Gain: page-turn time on text pages, and the freeze on delete, duplicate and paste.
- Notes: none for the file; the model is untouched. Risk is visual (a box that fails to
  refresh). Gate: P15 shows the count of boxes built per action fall; an on-screen run that
  edits, moves, recolours, rotates and undoes text on a page with tables and with layers.

### F13. Paper tiles off the UI thread (second pass)

Answers S15. Bake on the thread pool and have `PaperTextures.Brush` return a flat fill of
the paper's ground colour until the tile is ready, then invalidate once. Bake the page's
paper at load before the first draw if it is cheap, and the Settings previews only when the
tab is first shown, one per idle turn. Do not drop all tiles on a DPI change: page tiles do
not depend on it (they are baked at 96 DPI by design); only the Settings swatches do.

- Gain: no paper-bake hitch on a page turn or in Settings.
- Notes and paint: none. Gate: P16 shows no bake on the UI thread; paper looks the same on a
  white-stock paper with a custom background colour.

### F14. Coalesce the cascades (second pass)

Answers S14. `BottomMenu.Changed`, `PageTheme.Changed`, `ToolUiChanged` and
`SelectionState.Changed` should each be raised through one dispatcher-turn coalescer, as
`SyncThemeToPage` already does with its 60 ms timer, so the builds that hang off them run once
per turn at low priority, after the click handler returns. This is the generalisation of F2
item 2.

- Gain: the Mouse, Rotate and selection cases of the press lag.
- Notes and paint: none. Gate: P14 shows each build counted once per action.

### F15. The colour picker: no save and no rebuild per callback (second pass)

Answers S13. While the picker is dragged, update the active preset's colour and the strip
chip in place and call `Surface.PenColor`; run `BuildPenStrip`, `PushRecentColor` and
`ScheduleSave` once, when the picker closes. `PersistSettings` should not write the file
synchronously: hand the serialised string to the worker thread that already writes
library.json, with the same temp, flush and replace sequence, and never sleep on the UI thread.

- Gain: a colour drag stops stuttering; no fsync per drag step.
- Notes: the risk is a lost colour if the app dies mid-drag, which is a view of the colour,
  not content. Settings are still written at close and on the picker's close. Gate: P13.

### F16. A cheaper selection veil (second pass)

Answers S17. Compute the veil's text-box colours once at the start of the fade; animate one
opacity on a single overlay (or a cached veiled copy of the page) rather than repainting the
page and rewriting every text box each frame; try keeping the ink cache usable by drawing the veil as
a translucent rectangle over the cached ink; whether that looks the same as the present
per-element fade is not established, so judge it on screen. If that is not possible, shorten it to the minimum frames and make `ApplyTextVeil` O(n).

- Gain: no hitch when an image is clicked. Paint: none; low visual risk. Gate: P17.

### F17. Per-pass shape work (second pass)

Answers S16. Compute table geometry, cell colours and the cell-to-text map once per pass
(or once per edit), not per region; cache the equation's tinted bitmap or its
`ColorMatrixEffect` per pass; draw grain once per shape per pass; and downscale loaded image
bitmaps to a size near their on-screen maximum, keeping the original on disk for export.

- Gain: pages with tables, equations, grain pens and large images. Notes: none; the model is
  untouched. Paint: none. Gate: P18; a pixel comparison as for F4.

### 5.3 Acceptance

TODO 9.1's own words, as numbers, measured by P1 and P2 on the owner's laptop in Release:
- Drawing, panning and zooming: at least 95% of frames at or under 9.2 ms (a 10% allowance
  on 8.3 ms), and no UI-thread stall of 33 ms or more outside a page turn.
- A press of any button: handler under 5 ms and the next frame within 16.7 ms.
- A page turn: under 100 ms of UI-thread time.
- Autosave: no UI-thread stall over 16 ms.
- Concepts measured the same way on the same day, as the reference.

## 6. What could not be established by reading

1. **Which binary the owner launches now.** The September note says Debug. Nothing in the
   source can say what `Quill.lnk` points at today.
2. **Every actual cost.** Region count at 2880 x 1800, the fixed cost of a region on this
   GPU, the cost of `XamlReader.Load`, the time `Serialize` and `OnSaved` take, the size of
   garbage-collection pauses. Where this document gives a number it is labelled as a
   guess. The cost model is real; the magnitudes are not measured.
3. **Whether the frame is bound by the CPU or the GPU.** The code is shaped for CPU cost
   (call counts, walks), but a 5.2 million pixel anti-aliased repaint on integrated graphics
   may be limited by fill. Task Manager and P3 together decide it.
4. **The owner's pages.** Strokes, points, text boxes, shapes, grid type, layers and paper per
   page; whether any exceed 2,500 strokes; which page is the "30 fps" page. The report does
   not say, and S3, S9 and S11 depend on it.
5. **Whether the library folder is synced.** The recorded path is
   `C:\Users\irony\Documents\Quill`, which is not under OneDrive, but PaintStore.cs:157 calls
   the folder "routinely OneDrive", and the laptop may differ from where it was recorded.
6. **Machine state.** Power plan, AC or battery, the real refresh rate when Quill is running
   (dynamic refresh), Efficiency mode, and the driver version. Windows can run a UI thread on
   an efficiency core, and this cannot be seen from source.
7. **Whether Windows Defender or any other scanner is touching the 53 MB file on every save,**
   and whether the CPU and GPU power sharing in the package is lowering GPU clocks during a
   save. Both are hypotheses.
8. **Whether `CanvasVirtualControl.DpiScale` exists and behaves as needed in Win2D 1.4.0**
   for F3 and F7. It is in the API as I remember it, not as I could check it from here.
9. **What Concepts does** to reach 120 fps. Everything in F7 to F10 is a design that would
   plausibly get there, not a description of that app.
10. **How much of the code was read.** First pass: about 4,300 of InkSurface.cs's 11,138
    lines (view and zoom, pointer move and commit, regions and `DrawRegion`, grid, strokes, the
    ink cache, the spatial index, the text layer entry), about 2,100 of MainWindow.xaml.cs's
    12,677 (save, glow, pen strip, tool selection, view change, page switch), and the parts of
    ToolWheel.cs, PenBar.cs, ChromeBars.cs, LibraryStore.cs, SyncLog.cs and Icons.cs that the
    suspects name.
    **Second pass (this update).** The reading was done by me and by three read-only helper
    sessions, each given a file set and asked for findings with line numbers; I re-read the
    code at the lines behind S12, S13, S14 (the `BottomMenu.Sync` and `SelectionChrome`
    parts), S17, the text-layer callers and the motion-blur path before writing them, and did
    not re-read the rest, which rests on the helpers' reports.
    - InkSurface.cs: read line by line 4290-5909, 7318-11138; spot reads of 1340-1420,
      2195-2220, 2840-2889, 3425-3470, 3818-3874, 4040-4060, 4110-4220, 5925-5985, 6470-6505.
      Not re-read in the second pass: lines 1-4290 beyond those spot reads, and 5909-7318,
      which the first pass read.
    - MainWindow.xaml.cs: read 1-1177, 1375-2304 (with 2300-2800, the theme and accent code,
      read by me), 2800-3120, 3400-12677. **Not read by a helper:** 1178-1374 (the glow
      timer, which the first pass read), 2536-2589 and 2761-2799 (small stretches of the
      accent and title-bar code), and 3121-3399 (`BuildPenStrip`, which the first pass read).
    - Read in full by a helper: SettingsWindow.cs, FloatingWindow.cs, BrushesWindow.cs,
      ExportWindow.cs, ColorWheel.cs from line 290 (the first 290 lines are constants and
      comments, skimmed), FullscreenChrome.cs from line 56, PanelLayout.cs, ValuePopover.cs,
      SelectionChrome.cs, StripScroll.cs, HistoryWindow.cs, ObjectsWindow.cs, PaperTextures.cs,
      PaperGrain.cs, GridArt.cs, PdfExporter.cs and HtmlSvgExporter.cs.
    - Read in part: BottomMenu.cs 150-503, MeasurementMenu.cs 200-350 and 560-721, ChromeUi.cs
      95-180 and 375-470, PagePlate.cs 283-300 and 415-500, GridPointEditor.cs 180-510 and
      630-787 (the rest by search).
    - Still **not read:** ToolWheel.cs and PenBar.cs beyond what S4 and S18 name (the dial's
      colour disc, how its sliders call back, and what its `SlotsChanged` does), ChromeBars.cs
      beyond `OnViewChanged`/`SyncReadouts`, ThumbnailCache.cs (so how often thumbnails render
      is unknown), ColorPickerService.cs beyond lines 60-271, the Services other than the
      exporters, SyncLog and LibraryStore parts named above, MainWindow.xaml beyond its brush
      uses and the first 120 lines, and the tools/ projects. Any of those could hold a cost this
      document does not know about. P2, the stall detector, is how to find one: it names
      whatever is blocking the thread.
11. **That any fix works.** None was written, built or run: the job was set as analysis by
    reading, so every statement here comes from reading the source, git history and the
    repo's notes, not from running Quill.
12. **Second pass: which optional features the owner has on.** S20 (accent follow, motion
    blur), S19 (Brushes, History, Measurement windows) and the tool in use (Mouse, Select,
    Rotate: S14) decide which of the new costs apply. The settings file on the laptop would say
    in seconds; nothing in the source can.
13. **Second pass: no continuous cost was found, and that is itself not proof.** The second
    pass searched every `DispatcherTimer`, `CompositionTarget.Rendering`, `Storyboard` and
    `System.Threading.Timer` in the tree and read what each does (table in S20). A cost that
    runs without a timer, for example a compositor effect, a layout loop caused by an element
    resizing itself, or the driver, would not appear in that search. The `ThemeShadow`s found
    are two small ones, on the Settings back pill (SettingsWindow.cs:1188) and the grid-edit
    bar (GridPointEditor.cs:780), only while those are shown.
14. **Second pass: costs still labelled as estimates.** Every per-call figure in S12 to S20
    (1 to 5 ms per text box, 80 to 400 ms per paper bake, 5 to 25 ms per theme cascade,
    5 to 15 ms per colour-wheel frame) is a reading-based guess about WinUI, RichEdit and a
    low-power CPU. They are not measurements, and the helper reports they came from gave no
    measurements either.
15. **Second pass: whether the preview paper tiles share the canvas's tiles**
    (`PaperTextures.Preview` against the page's own tiles; same Win2D device or not), whether
    identical `Thickness` or `Visibility` writes invalidate layout in WinUI 3, whether a
    collapsed full-window `CanvasControl` (the colour wheel) costs GPU time, whether the
    separate popup island of `FloatingWindow` costs anything while open, and what Mica with
    the root at alpha 0xD0 costs the compositor.
