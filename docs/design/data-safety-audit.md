# Data safety audit

Audited commit: main at 0c41c7b (October 2026). Scope: every path that writes, replaces, migrates or deletes user data (library file, settings, text boxes, ink, shapes, layers, images, audio, paint, the op log, trash, export, startup, crash handling).

This is a reading audit. Quill was not built or run: it needs Windows and the Windows App SDK, and the audit ran on Linux. One fact was checked by experiment: the .NET 8 floating-point parse and JSON behaviour behind finding 1, run on the .NET 8 runtime in ICU globalization mode (the default on current Windows).

## How the audit was done

Six finders each read one area completely: the library store, save orchestration and app lifecycle, text boxes, ink and objects, paint, and the op log with settings, export and crash handling. Between them they listed about 60 candidate failures, each with file:line evidence.

Every candidate was then handed to an independent skeptic: a separate reviewer who had not produced it. The skeptic re-read the code and tried to refute it. Six skeptics covered the candidates in batches, and no skeptic reviewed its own area's finder. The coordinator then re-checked the top findings by hand.

Only what survived is listed below. Each finding carries a verdict:

- CONFIRMED: the code path was traced end to end and nothing guards it.
- PLAUSIBLE: the path exists, but whether it fires depends on Windows, WinUI, file-system or timing behaviour that could not be observed here.

Refuted or downgraded candidates are listed near the end, so they are not re-raised.

Finally, a separate pass checked every file:line citation in this document against the code (about 220 locations). It found two errors, both now corrected, and a sample of the citations it passed was re-checked by hand.

Two items were already known and are not counted as new:

- KNOWN-1: after paste plus Ctrl+Z, closing with the text box still focused saves the undone paste (TextFlushPolicy / TextTrim).
- KNOWN-2: the paint store's load/save races and lost writes. A replacement store is being built.

Note: the brief names PaintStoreCore and PaintLoadPolicy. Neither exists on main or anywhere in git history. The paint store on main is PaintTileStore, PaintTile and PaintTilesAction in src/Quill/Services/PaintStore.cs, plus PaintTileCodec.

Severity labels used below:

- data loss: content the user made is gone.
- corruption: content is changed into something else.
- stale or resurrected data: an older state replaces a newer one, or deleted content comes back.
- minor: wrong but recoverable, or cosmetic.

## Ranked findings

### 1. One non-finite number silently stops every save for the rest of the session

Severity: data loss. Verdict: CONFIRMED (code traced, and the .NET behaviour checked by experiment). Likelihood: high for anyone who opens the Artboard settings on an infinite page.

Evidence:

- src/Quill/Services/LibraryStore.cs:9 builds the serializer options as `new() { WriteIndented = false }`, with no NumberHandling. System.Text.Json throws ArgumentException on NaN or Infinity (checked: "number values such as positive and negative infinity cannot be written as valid JSON").
- LibraryStore.cs:570-571 swallows that exception: `try { json = JsonSerializer.Serialize(lib, Opts); } catch { return; }`. WriteError is not set, so ReportStorageProblems (src/Quill/MainWindow.xaml.cs:2179-2200) never shows anything. Every later Save fails the same way, including the one on close (MainWindow.xaml.cs:341). The op log is skipped too, because SyncLog.OnSaved runs after the serialize.
- The concrete trigger is the Artboard W/H boxes. New pages default to an infinite canvas (src/Quill/Models/NoteModels.cs:519). On such a page both boxes are created with the text "∞" (U+221E, the infinity sign) (src/Quill/Controls/SettingsWindow.cs:1804-1805, text set at :1902).
- When either box loses focus, Commit (SettingsWindow.cs:1840-1848) runs `double.TryParse(wBox.Text, out double w)` and the same for H, and rejects only values `<= 0`.
- Under ICU, which .NET 8 uses on Windows 10 1903 and later, the culture's PositiveInfinitySymbol is "∞" and TryParse accepts it. Experiment result: TryParse("∞") returned true and +Infinity in en-US, en-GB, de-DE, fr-FR, tr-TR and ja-JP.
- SetPageSize (MainWindow.xaml.cs:6081-6089) then stores the value into PageWidth/PageHeight without validation.

Trigger:

- On an infinite page, open Settings, then Artboard, click into W, then click anywhere else. Every edit from then until the app closes is lost, and nothing on screen says so.
- Typing a width and tabbing to H does the same thing for a moment, because H still reads "∞". It recovers once a valid H is entered.
- Picking the Infinite or A4 chip afterwards does not clear the bad value, because SetPageSize writes the dimensions only for Custom (MainWindow.xaml.cs:6085). Only typing two valid numbers does.
- A second NaN source was considered: NotebookPanel.Height, stored by the notebook-panel grip's ManipulationCompleted at MainWindow.xaml.cs:221-226. It needs ManipulationCompleted to fire without any ManipulationDelta, which is PLAUSIBLE but unverified.

Disk is never corrupted, because the bad value never reaches the file. The loss is everything done after the trigger.

Minimal fix:

- In Commit, require `double.IsFinite` and parse with NumberStyles.Float plus the invariant or current culture explicitly.
- Validate in SetPageSize as well.
- In Save, on a serialize failure set WriteError (so the status line repeats it) and log the exception.
- Optionally run a sanitizer pass that clamps non-finite doubles before serializing, so one bad field can never stop all saves.

Windows test: open a new page, then Settings, then Artboard. Click into W, click elsewhere, draw a stroke, close and reopen. The stroke is missing and library.json's timestamp did not change.

### 2. Cut or Copy then Paste turns vector ink, shapes and text into a flat picture

Severity: data loss (vector data, editability, text). Verdict: CONFIRMED by finder, skeptic and coordinator. Likelihood: high, since Cut, switch page, Paste is the only way to move content between pages.

Evidence:

- Copy and Cut fill the in-memory canvas clipboard and also push a rendered PNG to the system clipboard. PushSelectionToClipboardAsync calls `dp.SetBitmap(...)` and `Clipboard.SetContent(dp)` (MainWindow.xaml.cs:11201-11217).
- Paste checks for a bitmap first:
  - PasteAccel_Invoked (MainWindow.xaml.cs:12046-12052) runs `if (hasBitmap) { ... _ = PasteImageAsync(); return; }`. The vector branch, `if (InkSurface.HasCanvasClipboard)`, comes after it (:12061-12067).
  - The context-menu paste does the same (:11196-11197).
- No other Ctrl+V handler runs first. Paste is only a KeyboardAccelerator, and the canvas KeyDown handles Escape only.
- The pasted image is scaled to at most 520 px (src/Quill/Controls/InkSurface.cs:5798, :8419). Text is rendered as pixels. The eraser skips images (InkSurface.cs:3538).
- This is not the intended design. docs/FEATURE-CATALOGUE.md:481 says a private Quill format should be set at copy time and checked first on paste, so that Quill-to-Quill paste stays vector.

Trigger: lasso ink and a text box, Ctrl+X, switch page, Ctrl+V.

- The result is a small raster image. The page switch cleared undo on the source page (InkSurface.cs:879), so the vector original cannot be brought back there.
- The vector copy survives in a static field only until the next Copy or Cut, or app exit.
- For a selection over 4000 px the capture returns null (InkSurface.cs:5585). The ink is still cut, and the system clipboard keeps whatever older image it held, which is what then gets pasted.

Minimal fix: put a private format (or a sequence marker) in the DataPackage at copy time. On paste, prefer the canvas clipboard whenever the system clipboard still holds Quill's own package, and use the bitmap only when another app owns the clipboard.

Windows test: lasso some ink and a text box, Ctrl+X, switch page, Ctrl+V. The result is an image and the text cannot be edited.

### 3. Undoing a paint stroke that only created new tiles is never saved, so the stroke comes back

Severity: stale or resurrected data. Verdict: CONFIRMED by finder, skeptic and coordinator. Likelihood: high for anyone who paints. This is not part of the known race family: it happens with no concurrency at all.

Evidence:

- RemoveTile (src/Quill/Services/PaintStore.cs:265-274) removes the tile from memory and calls MarkDirty. MarkDirty (:417-422) only starts the timer; it marks no tile dirty.
- BeginFlush (:438-451) collects only dirty tiles and returns at :451 when there are none. So meta.json is not rewritten and still lists the removed tile, and the .qtile file stays.
- FlushBlocking on page switch and on close takes the same early return.
- BeginLoad (:550-583) reloads every tile meta.json lists.

Trigger:

- Make an oil stroke on an unpainted area, wait about 3 s so it reaches disk, Ctrl+Z, then switch page and back (or restart). The stroke is back.
- It needs every tile in the undone step to have been created by that stroke (PaintStore.cs:688-693), and no other paint edit on the page before leaving it. Undoing the first stroke on a blank page is the common case.

Minimal fix: keep a "removed keys" set. When it is non-empty, flush must rewrite meta.json even with no dirty tiles, and delete or tombstone the removed .qtile files. The replacement store should carry this rule over.

Windows test: as in the trigger, then check that meta.json still lists the tile.

### 4. A second page-background change reverts text typed since the first change

Severity: data loss (no undo entry). Verdict: CONFIRMED (finder and skeptic traced it step by step; coordinator re-read). Likelihood: moderate.

Evidence:

- ApplyTextContrast takes the contrast baseline once per session: `_contrastBaseline ??= _curPage.Texts.ToDictionary(t => t.Id, t => t.Rtf ?? "")` (MainWindow.xaml.cs:7640).
- Each later change rebuilds every box from that old snapshot: `var next = FlipTextContrast(original, nowDark); if (next == t.Rtf) continue; t.Rtf = next;` (:7644-7651).
- EndContrastSession is called only at :4205 (page switch), :7564 and :7572 (custom picker). The preset buttons (:7535-7539) and SetPagePaper (:7658-7662) never end the session.
- The live box rescues an edit only while its reach latch is set. RebuildTextLayer clears that latch after its flush (InkSurface.cs:10018-10028), and an unreached box is never written back (src/Quill/Services/TextFlushPolicy.cs, ShouldWriteBack).

Trigger: on an Ivory page with a box reading "Hello":

1. Click the Dark preset.
2. Type " world" into the box.
3. Click Black. The edit survives this step, but the latch is now cleared.
4. Click Ivory. The box reverts to "Hello", is autosaved, and stays that way after a restart.

Any other rebuild between two background changes has the same effect (undo, lasso move, table operation, layer toggle). Boxes created during the session are safe.

Minimal fix: end the contrast session after every discrete background change. Alternatively, per box, remember the RTF this function last wrote and take a fresh baseline whenever t.Rtf no longer matches it.

Windows test: the four steps above, then restart.

### 5. Undoing a text-colour change can restore the whole old text, dropping later typing

Severity: data loss. Verdict: CONFIRMED with a corrected trigger. The simple sequence (colour, type, Undo) was refuted: there the live box wins and only the colour undo silently fails. Likelihood: moderate.

Evidence:

- The text-colour action snapshots each box's whole RTF (src/Quill/Services/UndoRedo.cs:906).
- Do sets only the colour (:912-915).
- Undo restores the old document: `_texts[i].Rtf = _before[i].Rtf;` (:922). Nothing is captured for Redo.
- It is pushed by SetTextColour (InkSurface.cs:4612-4616) and RecolourSelection (:4530-4557).
- The live box overrides the restore only while it is reached or touched, and any rebuild clears both latches.

Trigger: pick a text colour with the caret in a box, type " world", click the canvas, then press Undo, Redo, Undo. " world" is gone for good. Any rebuild between the typing and the undo has the same effect.

This and finding 4 share one root cause. They are the only two places that restore a box's text from a snapshot (MainWindow.xaml.cs:7649 and UndoRedo.cs:922), and the snapshot wins as soon as a rebuild has cleared the latches.

Minimal fix: never restore characters for a colour undo. Instead, re-stamp the current document with the old colour, or restore only if t.Rtf still equals the RTF stored right after the stamp.

Windows test: the sequence above.

### 6. Sync: element deletes never reach other devices, and a clean replay brings every erased item back

Severity: stale or resurrected data (doubled ink after partial erases). Verdict: CONFIRMED by finder, skeptic and coordinator. Likelihood: high for anyone with two PCs on one synced folder. Also high, for a single user, the first time the library is opened on a new or reinstalled PC.

Evidence:

- The op-log merge is always on. SyncLog.Initialize runs on every load (MainWindow.xaml.cs:577). A 20 s timer runs RunForeignMerge (:441-442, :10276). Every save appends ops (LibraryStore.cs:573).
- It only acts when another device's oplog.*.jsonl is in the data folder.
- Delete tombstones are written without Parent (src/Quill/Services/SyncLog.cs:285-290; upserts do carry it at :283).
- Apply for strokes, shapes, texts and comments looks up `FindPage(lib, op.Parent)` and returns null when it is missing (:543-546). The per-device `seen` counter still advances (:443), so the delete is never retried.
- A partial erase removes the original stroke and inserts fragments with new ids. On the peer the original stays and the fragments are drawn on top: doubled ink.
- Page, section and notebook deletes do apply, because they do not use Parent.

The single-user path:

- The device id and read cursors live in %LOCALAPPDATA%\Quill (SyncLog.cs:87-91, :172-197). The oplogs live in the data folder, which defaults to Documents\Quill and is often OneDrive-synced.
- A new PC, a reinstall or a wiped profile therefore gets a new id and empty cursors, and replays the old PC's whole log from offset 0 (:415-416). Every upsert is re-applied and every element delete is ignored.
- Every stroke, shape, text and comment erased since that log's last compaction (2 MB threshold, :33) comes back.
- The resurrected elements are then saved into library.json. Because the shadow is rebuilt after the merge (:461-465), no op ever corrects them.
- A sync client's conflict copy of a log (for example oplog.abc-LAPTOP.jsonl) also matches the glob and is treated as a foreign actor.

Minimal fix: write Parent on tombstones (keep a key-to-parent map next to the shadow). In Apply, when Parent is empty, search all pages by element id. Also consider shipping the device id inside the data folder, keyed by machine, so a reinstall reconnects to its own log.

Windows test (single PC):

1. Draw and partially erase several strokes.
2. Close Quill and delete %LOCALAPPDATA%\Quill\deviceid.txt, its .bak and synccursors.json.
3. Relaunch and wait 20 s. The erased strokes reappear under their fragments.

### 7. "Change storage folder" can overwrite a library already there, and leaves paint, images, audio and trash behind

Severity: data loss. Verdict: CONFIRMED by three finders independently, two skeptics, and the coordinator. Likelihood: moderate. The most natural reason to pick a folder that already holds a library is pointing a second PC at a shared OneDrive folder.

Evidence:

- The button goes straight from a folder picker to SetDataFolder (MainWindow.xaml.cs:6147-6155) and always reports "storage updated".
- SetDataFolder (LibraryStore.cs:417-455):
  - copies the target's library.json aside only inside an empty catch (:438-444);
  - resets the conflict baseline to the target's timestamp (:450), so no conflict archive is made;
  - then saves the in-memory library over the target;
  - wraps everything in an outer catch (:454).
- The target survives only in library.json.bak, which the next save overwrites. Its backups folder is pruned one snapshot per 15 minutes of use (:893).
- The cached trash bin (:921-927) still belongs to the old folder and overwrites the target's trash.json on the next delete. There is no replaced-* copy for it.
- Images, PDF pages, assets and audio are stored as absolute paths under the old folder (MainWindow.xaml.cs:9741, :9768, :12280-12282, :12499; InkSurface.cs:5802). They are not copied, so deleting the old folder later loses them.
- Paint tiles live under %LOCALAPPDATA%\Quill\paint\ followed by a hash of the LibraryStore.Dir string (PaintStore.cs:229-244). After the switch every page's paint is looked up in an empty folder.
  - The same happens on any change in how the path is spelled: a trailing slash, UNC versus mapped drive, a renamed OneDrive root.
  - Paint is also absent from backups, trash, .quill export and sync.
- If SaveSettings fails inside the switch (its three retries are silent, LibraryStore.cs:165-171), the next launch opens the old folder and the session's work is orphaned.

Minimal fix:

- If the target holds a readable library, ask whether to open it or replace it.
- Fail the switch when the safety copy fails.
- Flush and verify the write before flipping DataFolder.
- Reset or merge the trash.
- Copy images/, assets/, audio/ and backups/, and store library-relative paths.
- Key paint by a library GUID stored in library.json rather than by the folder path.

Windows test: point Quill at a folder holding another library whose library.json is open in another program. Make two edits. The target library cannot be recovered.

### 8. A parked save is never loaded, and the next successful save deletes it

Severity: data loss. Verdict: CONFIRMED by finder, skeptic and coordinator. Likelihood: low to moderate, mainly with OneDrive or antivirus holding library.json at close.

Evidence:

- When File.Replace keeps failing (four tries, about 0.9 s, LibraryStore.cs:811-827), the save is parked as library.pending.json (:841-856).
- Load reads only SourcePaths() (:259-272) and never the pending file. No other code refers to it.
- Every later successful promote deletes it unconditionally (:816-819).
- The message telling the user to rename it is shown only by ReportStorageProblems while the window is open (MainWindow.xaml.cs:2179, polled from the 20 s sync timer at :442). At close the app exits before it can appear.
- A second instance's successful save also deletes this instance's parked file.

Trigger:

1. Close Quill while library.json is held open by another program for more than about a second.
2. Launch again. The older library.json loads.
3. Make one edit. The first autosave deletes the parked file.

The rolling snapshot taken on the parked path (:861) limits the loss to edits since the last snapshot, at most 15 minutes.

Minimal fix: in Load, if library.pending.json exists and is newer than library.json, fail closed with an offer to adopt it. Delete it only if it was parked in this session.

Windows test: hold library.json open with FileShare.Read, edit, close Quill. Release the file, relaunch, edit once. library.pending.json is gone and the edit with it.

### 9. Two Quill windows overwrite each other, and the conflict guard has holes

Severity: stale or resurrected data. Verdict: CONFIRMED by two finders and two skeptics. Likelihood: moderate. Startup shows a loading veil, so double-launching is easy, and nothing prevents a second instance.

Evidence:

- There is no single-instance guard. The app is unpackaged (src/Quill/Quill.csproj:15, WindowsPackageType None). App.OnLaunched just creates a new MainWindow. There is no AppInstance.FindOrRegisterForKey, named mutex or custom Main.
- Close always saves, with no dirty flag (MainWindow.xaml.cs:341, :2159-2164). A forgotten idle window writes the snapshot it loaded at launch over the other window's work.
- The conflict guard (LibraryStore.cs:710-748) archives the foreign version first, then overwrites it. It has these gaps:
  - Foreign writes within 2 s of our own baseline are ignored (:725).
  - The check runs before the temp write (:724 versus :804), so a foreign promote during our write is rotated into .bak and lost at the next save.
  - Archiving is throttled to one copy per 5 minutes (:727), and the same warning text is not shown again (MainWindow.xaml.cs:2199).
  - Only a newer timestamp counts as foreign. A backup copied over library.json in Explorer keeps its old timestamp and is overwritten without any archive.
- settings.json is last-writer-wins as well:
  - Each process caches it once (LibraryStore.cs:99).
  - SaveSettings writes the whole cached object, including DataFolder (:156-171).
  - Window placement captured on close (MainWindow.xaml.cs:1803) usually triggers that write.
  - So a stale second window can revert the storage folder chosen in the other window, and the next launch opens the old library.

Minimal fix:

- Enforce a single instance (AppInstance.FindOrRegisterForKey, redirecting to the running window).
- Track a dirty flag and skip the close save when nothing changed.
- Re-check for a foreign write immediately before PromoteTemp, using size or a hash rather than mtime alone.
- Always archive, with rotating names and a capped count.
- Re-read and merge settings.json under the lock before writing it.

Windows test: start two instances, edit in B, close B, then close A. Relaunch: B's edits exist only in library.conflict-*.json, and nothing points at it.

### 10. Autosave never forces a save during continuous work, and nothing handles logoff or shutdown

Severity: data loss on crash, power loss, logoff or shutdown. Verdict: CONFIRMED (code). Whether Window.Closed fires at logoff is PLAUSIBLE that it does not. Likelihood: moderate, with impact bounded by how long the user works without a pause.

Evidence:

- ScheduleSave restarts the debounce timer on every change: `_saveTimer.Stop(); _saveTimer.Start();` (MainWindow.xaml.cs:2151-2152). The interval is 1.5 s by default, configurable from 0.5 to 10 s (:106, :652).
- Steady handwriting with no pause that long never saves.
- Paint has a 30 s forced heartbeat (PaintStore.cs:213-216). The library has none.
- The only lifecycle hook is Window.Closed (:341). There is no WM_QUERYENDSESSION/WM_ENDSESSION handling, no AppWindow.Closing, and no ShutdownBlockReason.
- At a plain logoff or shutdown no WM_CLOSE is sent, so Closed most likely does not run. App.UnhandledException sets Handled = true for every exception (src/Quill/App.xaml.cs:14-24), so ordinary crashes are rare. The exposure is power loss, a kill, or a system shutdown.

Minimal fix: add a maximum-delay rule (save at least every N seconds while changes are pending). Subclass the HWND to run the close-time save and flush on WM_QUERYENDSESSION/WM_ENDSESSION, and register a shutdown block reason while a write is in flight.

Windows test: write continuously for a minute without pausing, then end the process from Task Manager. Separately, draw, then run `shutdown /l` within a second. Inspect library.json in both cases.

### 11. Closing the window while recording loses the recording

Severity: data loss (audio). Verdict: CONFIRMED (code). Whether the cut-off .m4a still plays is PLAUSIBLE that it does not. Likelihood: moderate for lecture use.

Evidence:

- The Closed handler (MainWindow.xaml.cs:341) never touches the recorder.
- The page is linked to the take only in CommitRecordedTake (:12562-12589), which runs on Stop (:12537) or on a page switch (:12405-12415). The comment at :12509-12513 says "do NOT repoint the page at it yet".
- StopAsync/FinishAsync (src/Quill/Services/AudioRecorder.cs:127-145) never run on close, so the file is very likely not finalised.
- On the next launch NextTakePath skips the non-empty file (AudioRecorder.cs:38-45), and nothing re-adopts it.

Minimal fix: on close, stop and commit the recording before saving. Record the pending take path on the page when recording starts, and adopt orphaned audio/{pageId}*.m4a files on load.

Windows test: record for 30 s and close with the X. The page has no audio, and check whether audio\{pageId}.m4a plays.

### 12. A data folder that is missing at launch is seeded, and can later overwrite the real library

Severity: data loss. Verdict: CONFIRMED (skeptic found a worse trigger than the finder). Likelihood: low to moderate (external drives, network shares, BitLocker volumes, OneDrive Known Folder Move).

Evidence:

- With DataFolder set but no library at that path, every source path is missing, so Load seeds a fresh library (LibraryStore.cs:343-349).
- The seeded library has RadialDialMigrated false (NoteModels.cs:708), so the window autosaves it about 1.5 s after launch (MainWindow.xaml.cs:594-598). The close save is a guaranteed second write.
- If the drive is truly absent, every write fails and is reported. That case loses nothing.
- The dangerous case is a path that becomes reachable during the session: a mapped drive reconnecting, a share coming online, a BitLocker drive being unlocked.
  - Load armed the conflict baseline as DateTime.MinValue (:311), so the guard is off (:724).
  - The next save rotates the real library.json into .bak, and the save after that destroys it.

Minimal fix: when DataFolder is set and the folder or its library is missing, fail closed with "folder not found" (the same pattern as SettingsUnreadable), offering retry and choose-folder.

Windows test: set DataFolder to a disconnected mapped drive, launch, reconnect the drive, wait for two saves. Inspect library.json and library.json.bak.

### 13. Table operations corrupt or duplicate cell text

Severity: corruption. Verdict: CONFIRMED for all three parts (skeptic simulated the string transforms). Likelihood: low to moderate wherever tables are edited.

Merge selected cells:

- The merged cell is built from StripRtf of each cell (InkSurface.cs:9250-9261) and joined without escaping.
- StripRtf (:10737-10758) does not skip the font-table, colour-table or generator groups. A cell reading "Name" becomes "Segoe UI; ;; *Riched20 3.1.0008 Name" plus a trailing NUL. Non-ASCII escapes, backslashes and braces are mangled too.
- There is no flush before the merge. If the top-left cell was edited since its last rebuild, its live text overwrites the merge instead, and the other cells' text survives only inside the undo action.
- CellMergeAction.Undo (UndoRedo.cs:1439-1443) never restores the top-left cell's text.

Split cell undo duplicates cells:

- Split pushes a CellMergeAction whose "hidden" list is the cells it is about to create (InkSurface.cs:9299-9300). The action's Do removes them (a no-op, since they are not added yet) and the caller then adds them.
- Undo then runs `page.Texts.AddRange(_hiddenTexts)` (UndoRedo.cs:1442), adding the same objects a second time.
- After a save and reload, each such cell exists twice with the same Id, and later edits land in one copy while the other is shown.

Toggle header row:

- The branch is inverted: PushAction runs Do first, so the code at InkSurface.cs:9352 sees the new value.
- It edits RTF as a string (:9355-9363). Replacing every "}" with "\b0}" turns a typed brace into a literal "\b0" and an early group close, so the rest of the cell is cut off. The strip pass removes "\b " from typed backslashes.
- HeaderRowAction (UndoRedo.cs:1447-1458) never restores the text.

Minimal fix:

- Flush before any table operation.
- Merge through RichEdit documents or RtfToPlainText with escaping.
- Snapshot and restore the top-left RTF in CellMergeAction.
- Give split its own action whose Undo removes the created cells.
- Render header bold from table.HeaderRow instead of rewriting RTF.

Windows test: type in A1 and B1, page away and back, select the row, Merge. The merged cell shows font-table junk, and Ctrl+Z leaves A1 mangled. Separately, Merge then Split then Ctrl+Z, then reopen: duplicate cell boxes are stacked.

### 14. Pasting text that looks slightly like Markdown deletes underscores and asterisks

Severity: corruption of pasted text. Verdict: CONFIRMED (simulated). Likelihood: moderate.

Evidence:

- Every Ctrl+V into a focused box goes through HandleTextPasteAsync (MainWindow.xaml.cs:12055).
- The text is treated as Markdown if it contains a backtick, "**" or "~~", or any line starts with "#", "- " or "* " (:12085-12086).
- FormatInlineRtf toggles italics on every single "_" and "*" and removes them (:12182-12213).
- Results: "- see my_file_name.txt" pastes as "see myfilename.txt" with "file" in italics, and "2*3*4" becomes "234". The clipboard's own rich formatting is discarded.

Minimal fix: convert only unambiguous Markdown (or on request), and require word-boundary markers for "_" and "*".

Windows test: paste "- see my_file_name.txt" from Notepad into a text box.

### 15. The paint index is rebuilt from memory, so tiles that are on disk but not loaded drop out of it

Severity: data loss (files stay on disk until overwritten, so manual recovery is possible at first). Verdict: CONFIRMED in code. The likely trigger for part of it is the known race family. Likelihood: low.

Evidence:

- WriteBlobs builds meta.json only from `_tiles.Keys`, the tiles in memory (PaintStore.cs:453, :499-500).
- That drops tiles that are on disk but not in memory. Three ways to get there:
  - Failed index load. Any exception reading meta.json returns an empty store (:540-548), and meta.json.bak is never tried.
  - Failed tile load. A tile whose .qtile and .bak both fail to read is skipped (:563-567).
  - Half-finished replace. File.Replace fails with ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 after the old file has moved to .bak (PaintTileCodec.cs:165). The catch then deletes the .tmp holding the new pixels (PaintStore.cs:491), and the tile is de-indexed because the final file is missing (:500). The same failure on meta.json makes the whole page look blank.
- The next stroke de-indexes everything not resident. Once the user paints that tile again, the orphaned file is rotated into .bak and then overwritten.
- Tiles are marked clean before the write succeeds (:443-446), and write failures go only to paint.crashlog. That part is the known lost-writes family (KNOWN-2).

Minimal fix: index on-disk files together with memory, never memory alone. Try meta.json.bak and fall back to scanning *.qtile. Never write a smaller index unless keys were deliberately removed. After a failed replace, restore from .tmp or .bak before deleting anything.

Windows test: truncate meta.json of a painted page, open it, make one stroke, wait 3 s, close. meta.json now lists only the new tile.

### 16. The trash can be wiped: fail-open load, last-writer-wins, and a one-click permanent delete

Severity: data loss (soft-deleted items). Verdict: CONFIRMED (logic). Likelihood: low on one device, moderate with two instances or two devices.

Evidence:

- LoadTrash (LibraryStore.cs:929-944) returns an empty bin on any failure, and the result is cached for the session (:927).
  - If both trash.json and its .bak are unreadable at the first load (no .bak yet, or both are offline OneDrive placeholders), the next delete moves the real bin into .bak, and the delete after that overwrites .bak.
- The bin is loaded once and rewritten whole with no conflict check (:955-989), while trash.json lives in the shared data folder. A second instance or second device drops the entries the other one added.
- A page, section or notebook deleted on another device is removed locally without entering the local trash (SyncLog.cs:480, :494, :505-517).
- "Delete forever" in the trash dialog has no confirmation (MainWindow.xaml.cs:5733-5735).

Minimal fix: add a TrashLoadFailed flag that blocks trash writes (so deletes abort). Re-read and merge by entry id before every write. Confirm Delete forever.

Windows test: delete once, hold trash.json exclusively while launching, release it, delete twice more. Inspect trash.json and its .bak.

### 17. A transient read error on library.json loads the older .bak, which then replaces the newer file

Severity: stale or resurrected data, then loss. Verdict: PLAUSIBLE (depends on Windows sharing and cloud-file behaviour). Likelihood: low.

Evidence:

- TryRead (LibraryStore.cs:504-529) treats IOException and UnauthorizedAccessException exactly like a parse error.
- Load then falls back to library.json.bak (:318-319) with saving enabled.
- The .corrupt preservation copy (:527) fails for the same reason the read did. The baseline was armed from the newer file (:309), so no conflict archive is made.
- The close save rotates the newer file into .bak, and the next save overwrites it.
- The settings getter has the same silent fallback to settings.json.bak (:103-113), which may still hold the previous DataFolder.

Minimal fix: fall back to .bak only on a parse failure. On an I/O error, set LoadFailed and offer Try again. Tell the user whenever a backup was used.

Windows test: hold library.json with FileShare.None during launch, release it, close Quill twice, compare the file with the original.

### 18. An older build silently strips fields it does not know

Severity: corruption. Verdict: CONFIRMED. Likelihood: medium for mixed builds on one data folder, which the code explicitly expects (LibraryStore.cs:11-13).

Evidence:

- There is no schema version, no [JsonExtensionData] and no UnmappedMemberHandling anywhere in src/Quill. Unknown members are dropped on read and missing from the rewrite.
- An older, pre-layers build that saves the library drops Layers, ActiveLayer and every element's LayerKey, so hidden layers reappear flattened. Strokes themselves survive (NoteModels.cs:538-546).
- Element lists added later than the old build are lost outright.

Minimal fix: write a format version, and have older builds open newer files read-only. Add [JsonExtensionData] on Library, NotePage, Layer and the element types so unknown fields round-trip.

Windows test: create layers in the current build, open and edit with a build from before layers, reopen in the current build.

### 19. Other sync defects (only with two devices on one folder)

Verdict: CONFIRMED unless noted. Likelihood: low to moderate, multi-device only.

- A peer deleting the page that is open here leaves the user editing a detached page. MainWindow.xaml.cs:10284-10287 reloads `_curPage` without checking it is still in the library, and Apply returns the page id even when it is already gone (SyncLog.cs:517). Everything drawn afterwards is never saved. Severity: data loss.
- Page and section moves emit no op, because PageMetaJson and SecMetaJson carry no parent (SyncLog.cs:226-234), and Apply never re-parents. If the old section is later deleted, the peer deletes the moved page with it, outside any trash. Severity: data loss on the peer.
- After a merge the shadow is rebuilt from the live model (:461-465), absorbing this device's unsaved edits, which are then never emitted. Severity: stale data.
- Ops are applied by per-device counter only, never by timestamp (:441-443), so concurrent edits to one element leave the devices permanently different. Severity: stale data.
- The shadow and counter are updated before File.AppendAllText, and a failed append is swallowed (:281-297), so those ops are lost for good. The append is not flushed, and the read cursor jumps to fs.Length past a torn last line (:447). The torn-line part is PLAUSIBLE.
- library.json itself is shared and rewritten whole by every device. The conflict guard then writes a 50-70 MB conflict copy up to every 5 minutes per device, and nothing ever deletes them.
- The 20 s merge reloads the current page whenever a peer touched it, which clears local undo (InkSurface.cs:879).

Minimal fix: as in finding 6. Also: verify `_curPage` is still reachable after a merge, include the parent in page and section ops, rebuild the shadow only for keys Apply touched, and compare timestamps per key.

### 20. Smaller confirmed issues

Each was confirmed by its skeptic. Likelihood is low unless stated.

- Write backlog on close. Saves queue one full library write each, with no coalescing (LibraryStore.cs:578). Flush waits at most 15 s and ignores the outcome (:697), so on a slow disk the newest state can be killed with the process. Restore relies on `WriteError == null` after that same Flush (:1128-1134), so a timed-out flush lets the trash entry be dropped before the library write lands. PLAUSIBLE.
- Stale search results. Results persist until the next search (MainWindow.xaml.cs:4645), and clicking one calls SwitchToPage with no existence check (:4650-4655). A trashed or purged page opens normally, and edits to it are lost at exit unless another trash write happens to save it. Severity: data loss. Likelihood: low to moderate.
- Cross-page undo. A page switch during an eraser or scale gesture pushes the page-A action onto page B's history, because LoadPage ends only oil gestures: it calls EndOilGesture(GestureEnd.PageSwitch) (InkSurface.cs:859), which returns immediately unless the oil brush is live (:6136-6146). Undo on B then inserts page-A strokes into B (UndoRedo.cs:103-107); with paint, page-A tile snapshots are stamped onto page B (InkSurface.cs:3635-3650). A switch is reachable mid-gesture through the command palette while the pen is held. Severity: corruption.
- Undo during an eraser gesture is not blocked. StrokeInProgress excludes the eraser (src/Quill/Models/GestureRules.cs:160-162), so Ctrl+Z mid-erase corrupts history and can leave the original stroke plus fragments. Severity: stale data.
- Pen-repair bridge. With PenRepairBridge on (off by default, NoteModels.cs:797), a takeover by a second pointer during a resumed stroke clears the resumed stroke without restoring it (InkSurface.cs:2959), deleting the previous committed stroke. Severity: data loss of one stroke. Likelihood: very low.
- Paint undo trimming. When paint undo passes 128 MB, TrimBottom (UndoRedo.cs:1630-1642) can drop an eraser composite that also holds vector removals. Undo-all then Redo re-adds an erased original stroke over its fragments. Severity: stale data. Likelihood: very low.
- Legacy import comes back. The LectureInk import is guarded only by Settings.ImportedLegacy (LibraryStore.cs:365-372), and Merge matches by notebook id (:399-413). If settings.json is deleted (which the app's own error text suggests, :297-298) and no LectureInk settings.json exists to re-adopt, notebooks deleted since the first import come back. Severity: resurrected data.
- Import creates duplicate page ids. Import matches notebooks by id only (:399-413), so a page can exist twice with one id, sharing one paint folder and one thumbnail. A failed RestoreNotebook is silent in the UI (MainWindow.xaml.cs:5724-5731).
- Empty text box removed without undo. A box emptied and blurred is removed with no undoable action and without checking Locked (InkSurface.cs:10501-10529). Severity: minor data loss.
- No safety in SaveNow. SaveNow and the Closed lambda have no try/finally (MainWindow.xaml.cs:2159-2164, :341). If FlushTexts throws (TextTrim.cs:72 calls GetText unguarded), Save, FlushPaint and LibraryStore.Flush are all skipped at close.
- GPU device lost. The NewDevice handler (InkSurface.cs:494-521) rebuilds textures and the oil brush but not the page's paint tiles. Strokes and undo over existing paint are silently swallowed until a page switch, and dirty tiles cannot be read back. On-disk data survives. PLAUSIBLE.
- No tile checksum. Tile files use raw deflate (PaintTileCodec.cs:95-104). A bit flip that still inflates to the right size loads silently, and the next edit rotates it over the good .bak. PLAUSIBLE, very low.
- Restore defaults changes the page. Settings "Restore defaults" changes the current page's grid, spacing, colour, opacity and page size, with no undo (SettingsWindow.cs:2558-2566). It needs two presses and the tooltip mentions the page. Severity: minor.
- crash.log grows without a cap. It lives next to library.json (App.xaml.cs:18-20, InkSurface.cs:5944-5953), so it is synced too. Severity: minor.
- Export side effects. Exporting a notebook switches through every page, replacing the Recents list and queueing one full save per page (MainWindow.xaml.cs:4188-4195). Markdown and PNG folder export overwrite files without asking, but only inside the folder the user picked. Severity: minor.
- AI rewrite into a detached box. The rewrite holds a box across an await (MainWindow.xaml.cs:10805-10816). If a rebuild or page switch happens meanwhile, the result goes into a detached box while the status says "Rewritten". The original text is intact. Severity: minor.

### Known items, for reference

- KNOWN-1, new trigger with the same root cause:
  1. Type a sentence and wait for the 1.5 s autosave.
  2. Backspace back to the text as it was when the box was built.
  3. Close.
  - The comparison baseline is fixed once per build (InkSurface.cs:1042, TextFlushPolicy.cs:120), so the deletion is never written and the sentence returns on reopen. Whether the RTF is byte-identical after typing and backspacing needs Windows.
  - The fix on the unreleased branch should be checked against this trigger too.
- KNOWN-2 family:
  - tiles marked clean before the write lands (PaintStore.cs:443-446);
  - a load overwriting a stroke made on a not-yet-loaded tile (:575-578);
  - two flushes reaching the write gate in either order (:457);
  - the 4 s close flush (:466-474) finishing before meta.json is written.
  - The replacement store should also take findings 3 and 15 into account. Neither of them needs a race.

## Checked and sound

These paths were verified by at least one finder and one skeptic:

- **Library write protocol.**
  - library.json, settings.json and trash.json are written by temp file, StreamWriter flush, `FileStream.Flush(true)`, then File.Replace with a named backup (LibraryStore.cs:762-791). The live file is never opened for truncation.
  - The old WriteAllText fallback is gone. A failed promote parks the save by rename (:841) and reports it every 60 s while the window is open (MainWindow.xaml.cs:2179-2200).
- **Write ordering.**
  - Writes are chained with ContinueWith and serialized by `_writeLock` (LibraryStore.cs:578, :712), so an older JSON can never land after a newer one.
  - Serialization runs synchronously on the UI thread, so the model cannot change mid-write. The only off-thread model writers found (PDF import, OCR search) marshal back to the UI thread.
- **Failed loads fail closed.**
  - A library that exists but does not parse leaves the save gate shut for the session (LibraryStore.cs:337-347, :559-565). Startup never calls EnableSaving on that path.
  - ScheduleSave, SaveNow, window placement, trash writes and auto-purge are all gated.
  - Closing during startup writes nothing, so a placeholder or seeded library cannot replace real notes.
- **Unreadable or stub files.**
  - An unreadable settings.json fails closed (:291-300), and SaveSettings refuses to write over it (:158).
  - Stub library files such as "{}", "null" or zero notebooks fall through to .bak (:512-520, :535-545), and a corrupt primary is kept as .corrupt.
- **Legacy and pre-rename sources** are read only when library.json does not exist at all (:329). Migration copies via a temporary name and a rename.
- **Rolling snapshots** are written by temp file and rename, under a name the pruning scan cannot see (:888-890). The cost is depth: 12 snapshots at most one per 15 minutes is about 3 hours of history.
- **Soft deletes.** The trash is written before the item leaves the library, with in-memory rollback if that fails (:1008-1016). A crash in between produces a duplicate, never a loss. All delete UIs re-home the current page.
- **Page switch** saves the outgoing page with a releasing flush before LoadPage (MainWindow.xaml.cs:4195-4206). Typing raises ContentChanged and schedules a save (InkSurface.cs:10494-10500).
- **Text flushing.**
  - A box with nothing stored is always written on first flush (TextFlushPolicy.cs:113).
  - RebuildTextLayer flushes before clearing (InkSurface.cs:10018-10028).
  - Undo and Redo flush before applying (:1146, :1179). DeleteEditingText flushes before removing (:4796).
  - Hidden layers' boxes are not built, and any visibility change flushes first.
- **TextTrim** only removes a trailing run of paragraph marks. It refuses tables, list items and span mismatches, re-checks after deleting, restores on failure, and latches refused trims (TextTrim.cs:76-94, :188-317). No path removing non-empty content was found.
- **Undo is per page.** LoadPage clears history (InkSurface.cs:879), so no action can point at another page's objects. The exception is the gesture-in-flight case in finding 20.
- **Paste and duplicate never reuse element ids.** Clones omit the Id (NoteModels.cs:160-165, :236-249, :315-348), and table cells are re-linked to the copy (src/Quill/Models/ElementClone.cs:59-66).
- **Asset files are never deleted.** The only file deletes are temp files, empty audio placeholders, thumbnails and pruned backups, so undo history, trash, clipboard and backups cannot lose an image or recording. Orphans accumulate instead.
- **Hidden and locked layers** lose nothing at save. Elements carry LayerKey, whole lists are serialized, and unknown keys fall back to the base layer without being rewritten (src/Quill/Models/LayerModels.cs:195-201). Layer delete, merge and reorder actions exist in UndoRedo.cs but have no UI caller.
- **Paint write protocol.**
  - Tiles and meta.json are written by temp file, write-through flush, and File.Replace with .bak, with meta.json written last (PaintTileCodec.cs:156-167).
  - A truncated tile fails its length checks and is read from .bak (:172-182). A tile that cannot be read is skipped, not saved back as blank.
  - Deleting pages or notebooks never deletes paint, so restore from trash brings it back.
- **Op-log state files.** Device id and cursors use temp file, flush and replace with .bak, and reads fall back to .bak (SyncLog.cs:120-170). Compaction is crash-safe and keeps tombstones (:322-398). A device's own log is never replayed at itself while its id is intact.
- **Export writers** build files in memory and write only to the file or folder the user picked. They restore view state in finally, and the .quill export only reads the model.
- **ThumbnailCache, AI, dictation and ThumbnailStamp** do not write user data. AI keys are kept in the Credential Locker.

## Refuted or downgraded during verification

- Paint tiles recovered from .bak loading with Dirty false: refuted as a loss. The next edit writes a good file.
- Text colour undo in the simple sequence (colour, type, Undo): refuted. The live box wins, and only the colour revert silently fails. The real trigger is in finding 5.
- Pen-repair bridge losing a stroke on a tool or page switch: refuted. Vector gestures survive those. Only the takeover path remains (finding 20).
- Delete crashing between the trash write and the library write: fails safe (a duplicate, not a loss).
- Table-cell heal on page load being a write side effect: not data loss, since it only adds empty cells and runs on every open.
- A detached text box's late LostFocus deleting the rebuilt box: no concrete path found that loses non-empty text. PLAUSIBLE only.
- NotebookPanel.Height NaN reaching the save: depends on ManipulationCompleted firing without a delta. Unverified, and superseded by the confirmed trigger in finding 1.
- Element id collisions from paste or duplicate: none. Clones get fresh ids.

## Could not establish without Windows

- **Clipboard (finding 2).** Whether Clipboard.GetContent().Contains(Bitmap) is true for Quill's own SetBitmap package. Very likely yes, and it decides finding 2.
- **ICU on user machines (finding 1).** Whether every user machine runs .NET in ICU mode. NLS mode (older Windows, or a UseNls switch) uses "Infinity" rather than "∞", and then finding 1's Artboard trigger does not fire. The swallowed-exception defect remains either way.
- **Shutdown and logoff (finding 10).** Whether Window.Closed fires for an unpackaged WinUI 3 app at logoff, shutdown or a Windows Update restart.
- **File.Replace semantics.**
  - Whether the result keeps the replacement's or the replaced file's last-write time, which decides one hole in the conflict guard (finding 9).
  - Whether there is any instant during ReplaceFile when library.json does not exist, during which a concurrent startup could take the first-run path.
  - How often ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 occurs under Defender or OneDrive (finding 15).
- **Cloud and sharing behaviour.** How OneDrive Files-On-Demand placeholders, Defender and sync clients actually lock or hydrate library.json, trash.json, oplogs and paint files. This sets the real likelihood of findings 8, 15, 16 and 17.
- **RichEdit behaviour (findings 4, 5, 13, 20 and KNOWN-1).**
  - Whether RTF after type-then-backspace is byte-identical to the baseline.
  - Whether a NUL inside merged cell RTF truncates the cell.
  - Whether SetText(FormatRtf) can silently produce an empty document.
  - Whether an uncommitted IME composition is included in GetText at save time.
  - The exact LostFocus ordering when a focused box is removed.
- **Audio (finding 11).** Whether an .m4a whose recording was never finalised can still be played.
- **GPU device loss (finding 20).** Whether GetPixelBytes on a lost device throws or returns zeros. If it returns zeros, dirty tiles would be saved blank over good files.
- **Timing.** Real serialize and write times for a 50-70 MB library on slow, scanned or synced disks, which set how often the 15 s close flush and the 4 s paint flush time out.
- **Power loss.** Whether NTFS can persist a rename without its data after a power cut for the unflushed copies (migration File.Copy, oplog appends).
