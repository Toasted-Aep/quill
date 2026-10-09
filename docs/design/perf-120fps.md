# Performance: why Quill does not hold 120 fps, and how to find out

Status: **analysis by reading the source only. Nothing in this document has been built,
run or measured.** Written 2026-10-09 against `main` at `f3efd52`, for TODO item 9.1.
File:line references are to that commit and will drift; the names beside them are what
to search for. Every "expected impact" below is an estimate made from reading the code,
and is labelled as one.

The report (owner, 2026-09-29): on the Intel Core Ultra 7 255H laptop (integrated
graphics, 2880x1800 at 200%) Concepts runs at 120 fps and Quill runs at nearly 30, and
Quill visibly lags behind after a button press. TODO 9.1 is done when there is "a steady
120 fps while drawing, panning and zooming ... and no visible lag after any button press -
measured, not estimated".

This document ranks what the code gives reason to suspect, says how to measure each
suspect on the laptop, and lays out the fixes in small steps, safest first. It changes no
code.

## 1. Summary

Reading the code does not find one cause. It finds five separate ones, any of which could
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

- Gain: no freeze at a page turn that follows a save; no freeze in the middle of a stroke.
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
10. **How much of the code was read.** About 4,300 of InkSurface.cs's 11,138 lines (view and
    zoom, pointer move and commit, regions and `DrawRegion`, grid, strokes, the ink cache,
    the spatial index, the text layer entry), about 2,100 of MainWindow.xaml.cs's 12,677
    (save, glow, pen strip, tool selection, view change, page switch), and the parts of
    ToolWheel.cs, PenBar.cs, ChromeBars.cs, LibraryStore.cs, SyncLog.cs and Icons.cs that the
    suspects name. **Not read:** SettingsWindow.cs, ColorWheel.cs (which declares two 16 ms
    timers; I did not check when they run), BrushesWindow.cs, FloatingWindow.cs,
    ExportWindow.cs, the PDF and HTML exporters, MainWindow.xaml beyond its brush uses, and
    most of MainWindow.xaml.cs. Any of those could hold a per-frame cost this document does
    not know about. P2, the stall
    detector, is how to find one: it names whatever is blocking the thread.
11. **That any fix works.** None was written, built or run: the job was set as analysis by
    reading, so every statement here comes from reading the source, git history and the
    repo's notes, not from running Quill.
