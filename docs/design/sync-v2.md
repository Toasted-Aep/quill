# Sync v2

Status: design only. Written against main at 0c41c7b (October 2026). Nothing here has been built or run: Quill needs Windows and the Windows App SDK, and this work was done on Linux. It builds on the data safety audit (docs/design/data-safety-audit.md on the branch cloud/data-safety-audit), findings 6, 9, 16 and 19 and the op-log notes in its "Checked and sound" section.

## 1. How this design was produced

- The nine problems in the brief were re-checked against the code, line by line (section 2).
- Three designers worked independently, each on one approach: (a) repair the existing op log in place; (b) per-element last-writer-wins registers with tombstones and hybrid logical clocks (a state-based CRDT); (c) split the library into per-page files with per-page versioning. Each wrote a complete design covering all nine problems.
- Two skeptics attacked all three designs. The first took crashes at every step, sync-client behaviour and migration with mixed builds. The second took merge semantics and convergence, clocks and garbage collection, and the UI thread and the live model. Between them they produced about 60 concrete failure scenarios.
- The skeptics split on the winner: one ranked (b) first, the other ranked (c)'s storage first with (b)'s merge model. Both ranked (a) last. This document synthesizes one design. It takes (b)'s merge model and storage, adds the ideas from (a) and (c) that survived the skeptics, and removes the machinery that produced most of the serious failures: tombstone garbage collection, a special first-device migration, and device-identity reclaiming.
- A final adversarial review attacked the synthesis, and its findings were fixed (section 16).

## 2. The problems, re-verified

All nine hold. Citations are to main at 0c41c7b.

1. Element delete tombstones carry no parent. Deletes are written as `new Op { K = parts[0], Id = ..., J = null, ... }` with no Parent (SyncLog.cs:285-290); upserts carry it (:283). Apply for st, sh, tx and cm does `FindPage(lib, op.Parent)` and returns null (:543-546), and `seen = op.N` still advances (:443). Holds.
2. A clean replay resurrects erased items. Device id and cursors live in %LOCALAPPDATA%\Quill (:87-91, :172-197), and a missing cursor means offset 0 and seen 0 (:415-416). Every upsert is re-applied, every element delete is dropped (problem 1), and the shadow rebuild after the merge (:461-465) makes the result permanent. Compaction (:33, :322-398) bounds it to ops since the last compaction. Holds.
3. Page and section moves emit no op. Correction to the brief: page and section ops do carry Parent (:243, :246). But the hash covers only PageMetaJson and SecMetaJson, which have no parent (:226-234), so a move changes no hash and emits nothing, and Apply never re-parents an existing page or section (:503-541). A later section delete on the peer also emits tombstones for every page and element it held there (LibraryStore.cs:1053-1073 then SyncLog.cs:285-290), which removes the moved page here outside any trash. Holds.
4. The shadow is rebuilt from the live model after a merge (:461-465), absorbing unsaved local edits. Holds, and it is frequent: unsaved edits exist for the whole debounce window, and for minutes during continuous writing (audit finding 10).
5. Ops apply by per-device counter only (:441-443). Concurrent edits to one element leave each device holding the other's version. Holds.
6. The shadow and counter advance before the append (:282-283, :288-289). File.AppendAllText is not flushed, and a failure is swallowed (:292-297). The cursor jumps to fs.Length (:447) past a torn last line. Two additions: the append and the cursor write share one try, so a failed cursor write after a good append loses the Lamport bump and the next save reuses N; and LibraryStore.Save swallows any exception from OnSaved (LibraryStore.cs:573). Holds; the torn-line part stays PLAUSIBLE.
7. library.json is in the shared data folder and rewritten whole by every device; the conflict guard (LibraryStore.cs:710-748) writes library.conflict-*.json copies of 50-70 MB, at most one per 5 minutes per device, and nothing deletes them. Holds.
8. Apply returns the page id even when the page is already gone (SyncLog.cs:517), and RunForeignMerge reloads the detached `_curPage` (MainWindow.xaml.cs:10284-10287), so every later edit is lost. LoadPage clears undo (InkSurface.cs:879). Holds.
9. The glob `oplog.*.jsonl` (:408) and `Split('.').Last()` (:413) make oplog.abc-LAPTOP.jsonl a new actor, replayed from offset 0. Addition: a conflict copy of the device's own log is replayed at itself. Holds.

Further defects found while verifying. A design must fix them or at least not inherit them.

- D1. The per-device sequence ("Lamport", never bumped on receipt) is stored in synccursors.json, separately from deviceid.txt. Losing the cursor file while the id survives resets it to 0, and peers then skip every new op (`op.N <= seen`). Problem 6's cursor-write path reaches the same result.
- D2. A device never replays its own log. At startup it loads library.json, which a peer may have written before merging this device's latest ops. Those edits vanish locally, and this device's next whole-file write propagates the loss.
- D3. PageMetaJson and NbMetaJson are hand-picked subsets, so these fields never travel as ops: Paper, GridColor, GridOpacity, GridAngle, the grid-editor fields, Perspective, the page-size fields, HasPaint, RefFrame, OcrText, PasswordHash and the per-notebook defaults. The comment at NoteModels.cs:512-514 says Perspective is carried in PageMetaJson; it is not.
- D4. Sibling order (MoveInList, MainWindow.xaml.cs:4492) is never synced. Apply re-sorts strokes by CreatedTicks and appends shapes and texts at the end, and erase fragments, pastes and duplicates all copy the original's CreatedTicks (NoteModels.cs:160-165, :248, :322).
- D5. Compaction keeps the latest op per key in N order. A container whose metadata changed after its children were created then appears after them. A fresh reader puts sections into the first notebook (:489), puts pages into the first section (:523-524), and drops element ops whose page does not exist yet.
- D6. OnSaved serializes and hashes every entity of a 50-70 MB library on the UI thread at every save (:277-284). MergeForeign does file IO and a full shadow rebuild on the UI DispatcherTimer.
- D7. RunForeignMerge flushes live text boxes after the merge has written remote RTF into the model (MainWindow.xaml.cs:10286). A reached box writes its own text back over the remote text, and the next save emits it, so the remote edit is lost everywhere.
- D8. Undo re-inserts objects without checking ids: RemoveStrokesAction and ReplaceStrokesAction (UndoRedo.cs:103-107, :128-133), RemoveTextAction (:274-275) and RemoveMixedAction (:404-409). Any merge that inserts a new object under an existing id, followed by a local undo, leaves two elements with one id.
- D9. PageLayers.NextKey returns max + 1 (LayerModels.cs:371-380). Two devices that add a layer to one page while offline mint the same key and fuse two layers.
- D10. An old-build delete of a section or page saves at once (LibraryStore.cs:1053-1098), so one OnSaved batch, with one Ts, carries the container tombstone and a tombstone for every page and element below it. Any consumer that applies those child tombstones by id empties the trash entry. This matters for migration (section 9).
- D11. Nothing enforces a single instance (App.xaml.cs:27-33). Two processes share deviceid.txt, synccursors.json and every %LOCALAPPDATA% file (audit finding 9).

Two corrections to the brief:

- settings.json lives at the fixed anchor, Documents\Quill (LibraryStore.cs:77-80). That is also the default data folder (:194-202), so in the default setup settings.json is synced and shared, not per machine.
- Layer has `Hidden`, not `Visible` (LayerModels.cs:55).

## 3. The design in brief

- The library becomes a set of keyed registers. Every notebook, section, page, layer and element has an explicit life register (alive, trashed or deleted) plus value registers. Each register is stamped with a hybrid logical clock, and merging keeps the higher stamp, so any set of records merged in any order, any number of times, gives the same state.
- Each device appends checksummed batches of records to a journal in %LOCALAPPDATA% and flushes it; that flush is the moment an edit is acknowledged. Then it copies the same bytes to its own segment file in the shared folder (Dir\sync\). Files are named per process run, so no two writers ever share a file name. Checkpoints of the whole state let new devices join and old segments be deleted.
- library.json is never written again. A container delete is a "trashed" write on the container alone, so the trash becomes synced state, and nothing is removed outside it. Deletes beat concurrent edits, and only an explicit undo or restore revives. Concurrent text edits keep both versions.
- One worker thread does all diffing, file IO, merging and checkpointing. The UI thread only captures what changed and applies merge results in place, with compare-and-set by content hash, so unsaved edits are never absorbed and undo survives.
- Migration is the same on every device. It imports the old logs and library.json as low-priority records that any real edit beats, and repairs items the old bugs resurrected. A bridge log keeps old builds receiving element edits during the mixed period. Tombstones are never garbage-collected in the first release.

## 4. Data model

### 4.1 Keys, kinds and registers

A key is (kind, id). The kinds are:

- nb (notebook), sec (section), pg (page): the containers.
- ly (layer): id is `<pageId>/<layerKey>`.
- st (stroke), sh (shape), tx (text box), cm (comment): the elements. Each element record also carries `pg`, its page id. Elements never change page (paste, duplicate and cross-page moves mint new ids), so the page id is part of the identity, not a register.
- fd: a gallery folder name (Library.Folders).

Every key has a life register `L`:

- Elements and layers: 1 alive, 0 deleted.
- Containers: 1 alive, 2 trashed, 0 purged.
- fd: 1 or 0.

Value registers by kind:

- nb: `pos` (order key); `del` (DeletedTicks, DeletedBy, original index); one register per Notebook property except Id and Sections, named `f.<Property>` (for example `f.Name`, `f.PasswordHash`, `f.DefaultBackground`).
- sec: `pos` (notebook id and order key); `del`; `f.<Property>` for every Section property except Id and Pages.
- pg: `pos` (section id and order key); `del`; `f.<Property>` for every NotePage property except Id, the four element lists, Layers and the device-local fields below.
- ly: `z` (order key); `attr` (the Layer object without Key).
- st: `z`; `v` (the whole PenStroke without Id).
- sh: `z`; `v` (the whole ShapeElement without Id).
- tx: `z`; `lay` (X, Y, Width, WidthPinned, MaxWidth, AutoWidth, Rotation, Locked, LayerKey, CreatedTicks); `body` (Rtf); `style` (TextColor); `cell` (TableId, TableRow, TableCol, FillColor, BorderColor, BorderWidth, CellColSpan, CellRowSpan).
- cm: `z`; `lay` (X, Y, AnchorElementId, CreatedTicks); `body` (Text); `meta` (Author, Resolved).
- fd: `z`.

Any key may also carry `X`, the "exists by legacy" register, value 1. It is written only by imports of old-build data (section 9) and never by a v2 edit, so a v2 edit of an element an old build created cannot erase the evidence that the element exists (section 4.4).

Why these grains:

- Strokes and shapes are the bulk of the library and are rarely edited after creation, so each is one value register. A concurrent move of one stroke on two devices keeps one move, which is acceptable.
- Text boxes split position, body, colour and table membership, so moving or recolouring a box on one device while typing in it on another keeps both changes. Comments split their text from their author and resolved flag for the same reason.
- Containers use one register per field. That makes old-build imports exact: an old meta op writes exactly the fields it carries and nothing else (section 9.2).

Device-local fields never sync. They move into a local file (section 5.4):

- NotePage: ViewX, ViewY, ViewZoom, ActiveLayer, and OcrText (derived from ink; each device indexes on demand).
- Library: every property except Notebooks and Folders. Settings stay mirrored in settings.json as today. Recents, LastPageId, calculator state and dial choices become local.

A reflection test, written like tools/CloneRoundTrip, fails the build if a serialized property of Notebook, Section, NotePage, Layer, PenStroke, ShapeElement, TextElement or PageComment is in no register and not on the device-local list. That closes D3 for good.

Unknown fields: register values are stored as raw JSON objects. A local write overlays only the fields this build knows onto the stored raw object, so fields written by a newer build survive an older v2 build's edit (audit finding 18).

### 4.2 Stamps and the clock

A stamp is (t, c, w):

- t: milliseconds since the Unix epoch, 64-bit.
- c: a non-negative 64-bit counter.
- w: the writer id (section 7.7).

Stamps order by t, then c, then w (ordinal string compare). Two different values with an equal stamp can only come from a reused legacy N or a cloned running process; they are ordered by the SHA-256 of the value bytes, so every device picks the same winner. Text form: `t.c.w`, with t and c in lowercase hex.

Special stamps:

- Baseline stamps (migration): t = 0, c = the library.json write time in ms, w = the migrating writer. They are lower than every real edit. The few baseline records that carry an edit the old build never logged use t = min(library.json write time, now + 1 hour), c = 0 instead (section 9.1).
- Legacy stamps (imported old ops): t = op.Ts converted from .NET ticks to Unix ms, c = op.N, w = "L" + op.A.

Clock rules:

- The clock is a hybrid logical clock l = (lt, lc), persisted in the local device.json (lazily) and recovered at startup as the maximum of that value and every stamp in this device's own journal.
- Next(): if now > lt then (now, 0), else (lt, lc + 1). The counter has 64 bits, so it does not wrap.
- Joining a remote record with stamp r: if r.t <= now + 1 hour, l becomes max(l, r). Otherwise l is not moved, and the status line names the writer whose clock is ahead (rate-limited).
- A local write to a register uses stamp max(Next(), successor of the register's stored stamp), where the successor of (t, c, w) is (t, c + 1, own writer). An edit made after seeing a value therefore beats that value whatever the clocks say. This per-key bump is not fed back into l, so one far-future register does not drag the whole clock forward.
- If at startup lt > now + 1 hour (a clock that was fast has been corrected, or the clock went backwards), l restarts at now and the status line warns. Causality still holds through the per-key bump; only genuinely concurrent conflicts are decided by the skewed time. Nothing in this design depends on wall-clock time for correctness, because tombstones are never collected in the first release (section 13).

### 4.3 Records and the join

A record writes one or more registers of one key at one stamp:

`{"q": seq, "k": kind, "id": id, "pg": pageId?, "s": stamp, "b": baseStamp?, "L": life?, "g": {register: value, ...}?}`

- `q` is the writer's sequence number, contiguous from 1 within a writer.
- `b`, the base stamp, is the stamp of the value the user was looking at when the write was made, read from the shadow (section 7.3), never from the merged state. For a delete it is the highest stamp the writer held for any register of the key. Baseline and legacy records have no `b`.
- A creation or a revive (L = 1) always carries every register, so a revived entity is self-contained.

The state holds, per key, per register: (stamp, b, value).

Join: for each register in an incoming record, keep the incoming value if the state has none or the incoming stamp is higher. That is the whole merge. It is commutative, associative and idempotent, so duplicated, reordered, late or conflict-copied files cannot change the result.

Storage of dead entities: when L = 0 wins for a stroke, shape or layer, the state keeps only the life register (a tombstone of about 40 bytes) and drops the content. Dead texts and comments keep all their registers, so that a concurrent edit can still be rescued (section 4.5).

### 4.4 Projection: from state to the Library

The live Library is a pure function of the state.

Liveness:

- A key is alive if its life register is 1. A key with no life register at all is alive if it carries `X` (an element or container an old build created; see section 9.2). Otherwise it is invisible: a field write that arrived before its creation waits, invisible, until the creation arrives.
- Field writes never change life. A stroke erased on one device and moved on another stays erased; only an explicit L = 1 (undo of the erase, or a restore) brings it back. This is what stops doubled ink after a partial erase meets a concurrent move.
- An element is shown if it is alive and its page is in the tree or in the trash.

The tree:

- A page is in the tree if it is alive and its `pos` section is in the tree.
- A section is in the tree if it is alive and its `pos` notebook is alive.
- A notebook is in the tree if it is alive.
- A container that is trashed, or that sits under a trashed ancestor, is in the trash and listed under its topmost trashed ancestor. Its children follow their own `pos`, so a page moved out of a section before that section was trashed stays live where it was moved.
- A container with life 0 (purged) is gone, and so is everything under it, except what a rescue has revived (section 4.5).
- A key whose parent has no records at all is held invisible for 24 hours of local observation, then shown in a synthetic "Unfiled" section. That section is not persisted; a user action on it writes real records. Nothing is ever placed in the "first notebook" (D5).

Order:

- Siblings, the elements of each list and the layers of a page are ordered by (order key, id).
- Order keys are base-62 strings compared ordinally (fractional indexing). The diff keeps the longest increasing run of existing keys and gives new keys only to items outside it, between their neighbours, so a move or reorder costs one record. A list whose keys grow past 32 characters gets one rebalancing write.
- New layers get a random unused key in [1, 2^30) instead of max + 1, which fixes D9. Existing keys are unchanged.

### 4.5 The two rules that are not a plain join

Both rules run at join time on the worker. Each produces ordinary records, which go into this device's next batch, so every device converges on the result whichever device produced it first.

Keep-both for text. It applies to the `body` register of tx and cm only (the RTF of a text box, the text of a comment), and never to legacy or baseline records. A record without a base stamp counts as having seen nothing.

- Two body values are concurrent when neither writer had seen the other: incoming r and stored v with r.s > v.s and r.b < v.s, or v.s > r.s and v.b < r.s.
- If two concurrent values differ, the loser (the lower stamp) is kept as a new element. Its id is UUIDv5(Quill namespace, id + loser stamp). It has the original's `lay` offset by 16 px, `body` = the losing value, `cell` cleared (CloneAsFreeBox, NoteModels.cs:341-346) and a `z` just after the original, all stamped with the loser's stamp.
- A delete of a text box or comment (L = 0 with base b) that meets a body value with stamp greater than b, which the deleter never saw, keeps that body the same way.
- Any device that sees both values makes the same element with the same id and the same stamps, so copies are idempotent. A three-way race can produce one redundant copy, never lost text.
- A move on one device plus typing on another is not a conflict, because they are different registers.

Rescue on purge. A purge (L = 0 on a container, written explicitly or by auto-purge) carries, per key, the base stamp b the purger held.

- A device that joins the purge checks the container and every key under it that it holds alive. If any of those keys has a register stamped above the purge's b for that key, or has no purge tombstone at all (the purger never knew it existed), then the purge has destroyed an edit, a restore or new content the purger never saw.
- That device writes a revive (L = 1 with every register, at fresh stamps) for the container and for every descendant it holds alive.
- If the original parent is no longer in the tree, the revive also sets `pos` to a "Recovered" section whose id is UUIDv5(container id + "recovered"), so concurrent rescuers agree.
- The status line says which page came back and why.

## 5. Files

### 5.1 The shared folder

New files live in Dir\sync\. Old builds list only `Dir\oplog.*.jsonl`, non-recursively (SyncLog.cs:408), and read only library.json and trash.json, so they never see anything below. Names:

- `folder.json`: `{"fmt":2,"libraryId":<guid>,"createdBy":D,"created":stamp}`, written once by the first v2 device with temp, flush and rename. If two devices race and a conflict copy appears, the lowest libraryId among the variants is canonical. The id is used only to name local caches.
- `dev-<D>.json`: the device file, written only by device D (temp, flush, File.Replace). It holds: the device part D, the machine name, the build, the legacyId (from the old deviceid.txt, if any), the bridgeId, the writer ids D has used, and `merged`, the highest contiguous sequence merged per writer. It is used for acknowledgements and clone signals, never for correctness of the merge.
- `seg-<W>-<firstSeq>.qsd`: a segment. W is the 20-hex writer id, and firstSeq is the first sequence number in the file, 10 digits.
- `ck-<W>-<seq>.qsc`: a checkpoint written by writer W covering its state at its own sequence seq.
- Temp files: `.~<final name>.tmp`. They match none of the patterns.

Canonical names are matched exactly and ordinally:

- `^folder\.json$`
- `^dev-[0-9a-f]{12}\.json$`
- `^seg-[0-9a-f]{20}-\d{10}(-r\d{1,4})?\.qsd$` (the `-r` suffix marks a republished copy, section 7.6)
- `^ck-[0-9a-f]{20}-\d{10}\.qsc$`

Any other name that starts with `folder`, `dev-`, `seg-` or `ck-` (case-insensitive) and does not start with `.~` is a variant. Variants include the OneDrive "-MACHINE(-2)", Dropbox "conflicted copy", Syncthing ".sync-conflict-..." and Google Drive " (1)" forms. A file's identity always comes from its header, never from its name. Segment and checkpoint variants are read and joined like originals, which is harmless because the join is idempotent. Variants of folder.json and device files feed only clone detection and the libraryId rule.

### 5.2 Segment format

A segment is UTF-8 lines. Each line is `<json>\t<crc32c of the json bytes, 8 lowercase hex>\n`.

- First line, the header: `{"f":"qseg/1","w":W,"d":D,"first":firstSeq,"build":"..."}`.
- Then batches. A batch is one or more record lines followed by an end line `{"end":lastSeq,"n":count}`.
- A batch counts only if every line in it is LF-terminated, passes its CRC and parses, its sequence numbers are contiguous, and its end line matches.
- Readers apply every complete batch anywhere in the file. A torn or garbled region therefore never hides later complete batches, and nothing ever waits for a missing sequence number.

A process never appends to a file it did not create, and every process run has a new W (section 7.7), so a segment has exactly one writer for its whole life. Journal segments in %LOCALAPPDATA% use the same format and names. The shared copy is byte-identical to the journal copy.

A segment is sealed (no more appends) at 1 MB, after an hour, after a failed append, and when the process ends.

### 5.3 Checkpoint format

A checkpoint is a gzip stream of lines:

- Header: `{"f":"qck/1","by":W,"vv":{writer:highest contiguous seq merged},"files":[{"name","len","sha256"}],"legacy":{per old actor: import positions},"created":stamp}`.
  - `files` lists every segment and variant whose content is fully joined into this state, with exact length and hash. It is what makes a file safe to delete.
- One line per key: `{"k","id","pg","r":{register:[stamp,b,value]}}`. A dead stroke, shape or layer has its life register only.
- Trailer: `{"n":lineCount,"sha":sha256 of everything before the trailer}`.

It is written to a temp file, flushed with Flush(true), renamed, then re-read. It counts as verified only when the trailer matches. An unverified checkpoint is never joined and never used to justify a delete.

### 5.4 Local files

Location: `%LOCALAPPDATA%\Quill\sync\<libraryId>\`. Before folder.json exists, a 16-hex hash of the normalized Dir is used, and the directory is renamed once the id is known. A respelled path costs only a rebuild from the shared folder.

- `device.json`: `{D, l, legacyId, bridgeId, bridgeN}`, written with temp, flush, File.Replace and .bak.
- `journal\seg-<W>-<firstSeq>.qsd`: this device's own segments. They are the acknowledged record of every local edit and the source for publishing. A journal segment is deleted 7 days after both conditions hold: its shared copy is listed in a verified checkpoint, and the local cache covers it.
- `cache.qsc` (with .bak): the state in checkpoint format, uncompressed, plus four things:
  - the consumed-files table (name, length, last write time, SHA-256 of the first and last 4 KB);
  - first-seen times for trashed containers;
  - the library.json watcher's per-field hashes (section 9.3);
  - legacy import state.
- `local.json`: the device-local fields (section 4.1).
- `device.lock`: opened with FileShare.None by the primary process.
- `instance-<W>\`: journal and cache of a secondary process (section 7.7).

Rolling backups move out of the shared folder, to `%LOCALAPPDATA%\Quill\backups\<libraryId>\library-<time>.json.gz`:

- Format: library.json format, gzip.
- Frequency: written by the worker from the state, at most one per 15 minutes of activity.
- Retention: 12.

Today's backups\ in the data folder stays untouched for old builds. The shared checkpoints are the cross-device recovery points.

## 6. What each thread does

- UI thread:
  - capture at save time: FlushTexts, then serialize;
  - applying merge patches with compare-and-set;
  - InkSurface refresh of changed elements;
  - banners and status messages.

  It never reads or writes a sync file, never hashes the library, and never diffs.
- The sync worker: one long-running thread with a FIFO queue. It owns the state, the shadow, the clock, all sync file IO, diffing, stamping, journal and publish, reading and joining, the two rescue rules, building patches, the cache, checkpoints, backups, legacy import, the library.json watcher and the bridge.
- The thread pool: parallel parsing at startup and checkpoint compression, called from the worker.

The UI thread and the worker exchange only immutable byte arrays and plain result objects. Captures and acknowledgements from the UI are processed by the worker in the order the UI posted them. That FIFO ordering is what keeps the shadow consistent (section 7.3).

UI-thread cost:

- In the first stages where v2 owns saving (stages 6-9, section 12), the capture is today's whole-library serialize, which SaveNow already performs at every save. Sync then adds nothing to the UI thread except hashing the few elements a merge touches. Today's OnSaved diff, which serializes every entity a second time on the UI thread (D6), is gone.
- Stage 10 replaces the whole-library capture with a capture of dirty pages plus the tree skeleton, which also cuts the save itself (section 7.1).

## 7. Algorithms

### 7.1 Write path

1. Trigger (UI thread):
   - SaveNow runs as today, on the debounce timer, at page switch and at close.
   - A new maximum-delay rule forces a save at least every 10 s while changes are pending (audit finding 10).
2. Capture (UI thread):
   - Surface.FlushTexts.
   - Serialize to a UTF-8 byte array:
     - Stages 6-9: the whole Library, as today.
     - Stage 10: each dirty page, plus the skeleton. The skeleton holds each container's id, parent, list position and fields, and each page's element counts.
   - Post the capture to the worker.

   In stage 10 a page is dirty when it is the current page and has raised ContentChanged since its last capture, when a tree command or MarkPageDirty marked it, or when its element counts in the skeleton changed. That catches PDF import, which fills pages 2..N without making them current (MainWindow.xaml.cs:9996-10026). The safety nets are an idle full capture at most every 10 minutes, a full capture at close, and a sweep of one non-current page every 2 s while idle.
3. Diff (worker). Split the capture into keys and registers, canonicalize each register value (known fields only), hash it with xxHash64, and compare it with the shadow (what the UI held, section 7.3):
   - A changed register becomes a record with b = the shadow's stamp for that register.
   - A new key becomes a creation (L = 1 with every register).
   - A key present in the shadow, missing from a captured page or list, becomes a delete (L = 0, b = the highest shadow stamp for the key).
   - A key that is dead in the state but present in the capture becomes a revive (L = 1 with every register). Only undo and restore can produce this, because paste and duplicate mint new ids.
   - A container missing from the skeleton becomes trashed (L = 2), never purged. Purge is written only by the trash API.
   - List order becomes `pos` or `z` order keys (section 4.4).
   - The diff compares only against keys the UI was actually given. A key held back by the projection (unknown parent) can therefore never be "deleted" by its absence (skeptic finding A12).
4. Stamp (worker): stamps per register as in section 4.2, and contiguous sequence numbers.
5. Journal (worker): append the batch and its end line to the journal segment, then Flush(true). When the flush returns, the edits are acknowledged.
6. Commit (worker): only now join the records into the state, set the shadow for those registers to the captured hashes and stamps, and advance the sequence number.
   - A failed journal write commits nothing. The next capture produces the same records again, because the shadow did not move.
   - The worker sets SyncWriteError, which the status line repeats every 60 s for as long as it lasts, in the same way as WriteError today.
7. Publish (worker): append the same bytes to Dir\sync\seg-W-firstSeq.qsd, then Flush(true).
   - On failure (sharing violation, offline placeholder, full disk, folder missing), the batch stays unpublished in the journal and is retried on every pass.
   - After 5 minutes unpublished, the status line says "Saved on this PC, not yet synced".
8. Rotate segments as in section 5.2.

Close: Closed runs the capture, then waits up to 15 s for the worker to journal it, as LibraryStore.Flush does today, and reports a timeout. The WM_QUERYENDSESSION and WM_ENDSESSION handling from audit finding 10 belongs in the same stage.

### 7.2 Read and merge path (worker)

1. Triggers:
   - a 20 s timer;
   - a FileSystemWatcher event on Dir\sync\ and Dir, debounced 2 s;
   - app activation;
   - startup.
2. Enumerate Dir\sync\ and the legacy files in Dir (oplog.*.jsonl and its variants, library.json, trash.json). For each file whose length, last write time, or hash of its first and last 4 KB differs from the consumed-files table, read it whole. A segment is at most about 1 MB. A checkpoint is read once, because its name is never reused.
   - Open files with FileShare.ReadWrite | FileShare.Delete.
   - A sharing violation, an offline placeholder or an IO error skips the file until the next pass.
   - A file that is shorter than before, or whose prefix changed (a sync client rolled it back or replaced it), is simply read again in full, which is harmless.
3. Decode: collect every complete batch (section 5.2) and every verified checkpoint, and join every record. Legacy files go through the legacy importer (section 9.2), which produces records.
4. Derive: run the keep-both and rescue rules (section 4.5) on the keys the join touched. Their records go through write-path steps 4 to 7 before anything is posted.
5. Patch: for every touched key whose projection differs from the shadow, build a patch entry. Patches are grouped by page:
   - the key and kind;
   - the operation: upsert, remove, move, trash, restore or purge;
   - the new value, already deserialized into a model object on the worker;
   - the order key;
   - the base hash, which is the shadow's hash for that key.

   Post each page's patch to the UI.
6. Persist:
   - The cache is written every 10 minutes, at close, and after a merge of more than 20 MB.
   - The device file's `merged` map is written every 10 minutes.
   - Consumed-file marks persist only inside the cache, together with the state they produced, so they can never claim more than is on disk. Losing them costs a re-read and nothing else.

### 7.3 Handing a merge to the live model (UI thread)

The shadow is the worker's record, per key and register, of the hash and stamp of what the UI holds. It moves only on a journaled own write (section 7.1, step 6) or on an acknowledged apply. It is never rebuilt from the live model, which is problem 4.

When patches are applied:

- Patches for the current page wait while a pen, eraser, lasso, drag or text-selection gesture is in progress, and are applied at pointer-up.
- An entry for a text box that has keyboard focus waits until focus leaves it.
- Patches for other pages are applied in slices of at most 5 ms per dispatcher callback.

Applying a patch for the current page happens in one dispatcher callback:

1. Surface.FlushTexts (not releasing), so that typed text is in the model before any comparison. That ordering fixes D7.
2. For each entry, find the live object through the page's id map and hash its canonical serialization. That is one element, not the page.
   - Live object present and hash equal to the base: apply. CopyFrom copies the fields onto the existing object, and the object moves to its order position if `z` changed. In-place copying keeps every undo action's object reference valid (UndoRedo.cs:63-157).
   - Live object present and hash different: defer. The user changed it since the last capture. The next capture writes that change with a stamp above the remote one, so it wins everywhere; if both changed a text body, keep-both makes a copy.
   - Live object missing but the base says present: defer. This is an unsaved local delete, and the next capture writes it.
   - Live object missing and the base says absent: insert. If the session's identity map already holds an object for this id (one an undo action may reference), CopyFrom onto that object and insert it. Otherwise insert the worker-built object. The identity map is a per-session dictionary from id to every element object that has been in any page list this session (weak references), kept by InkSurface.
   - Remove: remove if the hash equals the base, else defer.
3. Undo hygiene:
   - If a removed id is referenced by an action on the undo stack, drop that action and everything older. If it is referenced by an action on the redo stack, clear the redo stack. Redo after a remote partial erase therefore cannot bring back the original over its fragments.
   - IPageAction gains `IEnumerable<Guid>? ElementIds`. Null means unknown and is treated as referencing everything.
   - Every Undo and Redo insert path skips an object whose id is already present in the list (D8).
4. Call the new InkSurface.ApplyRemoteChanges(ids). It:
   - invalidates the ink cache, the spatial index and the bitmaps of changed images;
   - rebuilds only the changed text boxes, without flushing (the flush already ran in step 1);
   - prunes the selection of removed ids.

   It does not call LoadPage, and it does not call UndoManager.Clear.
5. Post an acknowledgement to the worker listing the applied keys with their new hashes and stamps, and the deferred keys. The worker moves the shadow for applied keys only.

Other pages follow the same rules without the flush, because none of their boxes is live.

Container changes move the same Notebook, Section and NotePage objects between lists, then BuildTree runs. The open page needs special handling:

- If the open page, or an ancestor, becomes trashed on another device, the page stays open with a banner: "Moved to the trash on <machine>", with Restore and Close. Edits keep saving into the same keys, inside the trashed page. Nothing is detached, which is problem 8.
- If it is purged (L = 0) and no rescue applies, the window switches to the nearest live sibling, or to the gallery, and says why.

Why the FIFO order is enough:

- A capture taken before a patch was applied reaches the worker before that patch's acknowledgement, and a capture taken after it reaches the worker after.
- A patch computed before a capture landed may carry a base hash that the capture has since replaced. At apply time the live object then differs from the base, and the entry is deferred, which is correct.
- A page switch between the post and the apply is harmless. The flush and the InkSurface refresh run only if the patch's page is current at apply time, and SwitchToPage has already saved the outgoing page.

### 7.4 Trash, restore and purge

- Delete of a notebook, section or page writes L = 2 plus `del` on the container alone, with a trash mark so the diff knows the absence is deliberate.
  - Children are not touched. They follow their own `pos`.
  - trash.json is no longer written by v2. The trash view is projected from the state, which ends audit finding 16's whole-file trash for v2 builds.
- Restore writes L = 1 with b = the trash stamp. If the original parent is not in the tree, it also writes `pos` to the original parent's nearest live ancestor, or to a "Recovered" section, matching today's RestorePage fallback (LibraryStore.cs:1161-1176).
- Delete forever writes a purge, L = 0 with b, for the container and for every descendant this device holds, and drops their content. The rescue rule (section 4.5) protects edits and restores the purger had not seen.
- Auto-purge, today's 30-day retention (TrashBin.RetentionDays), runs on a device only when all four of these hold:
  1. This device first saw the container trashed at least RetentionDays ago, by its local clock.
  2. The newest stamp anywhere in the container's subtree is older than RetentionDays.
  3. Every device whose device file changed in the last 60 days, judged by this device's observation of the file, publishes a `merged` map that covers the record that trashed the container.
  4. When other devices exist, the newest stamp seen from them is within one day of this device's clock.

  Rule 3 is what stops a laptop that wrote on a trashed page while offline from losing that work to a 30-day expiry elsewhere. The rescue rule covers a device silent for more than 60 days.
- Element deletes (erase, delete selection) have no trash today and get none: L = 0, undoable within the session as today.

### 7.5 Checkpoints and deleting files

When a checkpoint is due:

- The segments not listed in any verified checkpoint exceed 32 MB, or 7 days have passed since the newest verified checkpoint and at least one segment is unlisted.
- A device writes one only if its own state has joined every file it lists.
- To avoid a stampede, the device with the smallest D among devices whose file changed in the last 7 days writes at once. Any other device waits a random 0-48 hours and writes only if the condition still holds.

Deleting segments:

- A segment or variant may be deleted when a verified checkpoint lists it with the identical name, length and SHA-256, that checkpoint has been readable locally for 7 days, and, where the Cloud Files placeholder state can be read, the checkpoint is fully in sync.
- Normally the owner device deletes the segments of its own writers. Any device may delete files of writers whose device file has not changed for 90 days.
- Exact-content matching means a rolled-back, truncated or clone-collided file is never deleted on the strength of a different file with the same name (skeptic finding B4).

Deleting checkpoints:

- A device deletes its own older checkpoint once a newer verified checkpoint, from any device, lists a superset of its files and has been readable for 7 days.
- Checkpoints of devices silent for 180 days follow the same rule and may be deleted by anyone.
- At least two verified checkpoints are always kept.

Why this is always safe: tombstones are never collected, and a verified checkpoint holds the joined result of every file it lists. Joining that checkpoint therefore gives the same state as joining those files. A stale copy of a deleted file that reappears later (a rollback, a conflict copy, a re-upload) can only re-join records that are already in the checkpoint. Nothing in the deletion rules depends on clocks.

### 7.6 Startup, joining and reinstall

On the worker, during LibraryStore.BeginLoad:

1. Read device.json. If it is missing, mint D. If device.lock cannot be taken, run as a secondary (section 7.7).
2. Load cache.qsc, or its .bak, or start with an empty state.
3. Replay every local journal segment, from every run of this device and from any instance-* directory, and join it. This is the fix for D2: a device always re-applies its own acknowledged edits, whatever library.json or the cache says.
4. Do one read-and-merge pass over the shared folder (section 7.2, steps 2-4), if the folder is reachable.
5. If no migration marker is in the cache, migrate (section 9.1).
6. Republish: for every own journal segment, make sure the shared copy holds the same bytes.
   - Append a missing tail when the shared copy is an exact prefix of the journal copy.
   - If the shared copy diverges (truncated mid-batch, rolled back, replaced), write the whole journal segment as `seg-<W>-<firstSeq>-r<k>.qsd`. The extra file is harmless, because joins are idempotent.
7. Project the state into a new Library on the worker. The UI thread then runs AdoptInPlace, ApplySettings and EnableSaving exactly as today (MainWindow.xaml.cs:571-578). The shadow equals the projection.

A new PC, a reinstall or a wiped profile has no cache and no journal, so it mints a new D and joins:

- It joins every verified checkpoint and every segment in sync\, and imports the legacy files.
- Tombstones are in the checkpoints and segments, and an older upsert can never beat a newer tombstone, so nothing erased comes back (problem 2).
- A cursor that does not exist cannot be lost.

If the files in sync\ are offline placeholders that cannot be read, the device starts from what it can read. Two cases:

- library.json is a real old library: the migration baseline (section 9.1) supplies the content. Its records are low-priority, and the rest joins as files hydrate. No rule here can resurrect or revert anything, because baseline records lose to every real record.
- library.json is the retirement stub or absent, and sync\ shows any v2 file: the device has nothing to show yet. It says "Waiting for your notes to download from <folder>" and never seeds a library.

A seed library ("My Notebook") is created only when neither a real library.json nor any v2 file exists anywhere in Dir, which is today's genuine first run.

### 7.7 Identity, second instances and clones

Each process run has its own writer id: W = D (12 hex, random per installation, kept in device.json) followed by R (8 hex, random per process start). Sequence numbers start at 1 for each W. This has three consequences:

- A sequence number is never reused, even after a crash mid-batch, a torn journal tail or a restore from an image. The next run is a new writer.
- Two machines restored from one disk image, or a profile copied to another PC, get different W at their next launch. Disk clones therefore cannot collide.
- A lost device.json costs nothing but a new D.

The device-identity reclaiming, fingerprinting, forks and re-homing from design (a) are not needed.

Second instance. Stage 1 adds the single-instance redirect from audit finding 9 (AppInstance.FindOrRegisterForKey). As defence in depth, a process that cannot take device.lock runs as a secondary writer:

- It keeps its own journal and cache under `instance-<W>\`.
- It publishes as usual.
- At exit it finishes publishing. The primary's next startup republishes any leftover batches from instance directories and then removes them.

The two windows then merge through the shared folder, like two devices.

Cloned running process. A VM snapshot resumed on two hosts continues one W, and both copies write segments with the same names. The sync client then produces variants.

- Readers join every complete batch of every file, variants included, so no record is lost.
- Deletion requires exact-content coverage, so no file is deleted on the strength of its twin.
- A process that finds a file with its own W whose content differs from its journal starts a new run id at once. Its records already published stay valid.

### 7.8 Library-level data and other files

- Gallery folders are fd keys. Every other Library property is device-local or a setting (section 4.1).
- Paint tiles, image and audio paths, and settings.json are out of scope (audit findings 7 and 9, and KNOWN-2). Section 11 lists what this design must not make worse.

## 8. How each problem is solved

1. Element deletes: a delete is the element's own life register, keyed by (kind, id). Joining it needs no parent and cannot fail. There is no seen-counter that could skip a record: files are re-read whole, and the join is idempotent.
2. Clean replay: a fresh device joins checkpoints and segments that hold every tombstone, and a newer tombstone always beats an older upsert. Legacy parentless tombstones are applied by id. Migration repairs elements the old bugs already brought back (section 9.1). No local cursor matters for correctness.
3. Moves: a move is one `pos` record, and reorders are order-key records. A container delete writes L = 2 on the container alone, with no child tombstones. A page moved out of a section before that section was deleted stays live where it was moved, and the deleted section's remaining pages go to the trash on every device.
4. Absorbed edits: the shadow moves only on a journaled own write or an acknowledged apply. A merge never overwrites a locally changed object; it defers and lets the local change win by stamp.
5. Concurrent edits: one total order of stamps decides every register on every device. Text bodies keep both versions.
6. Durability: the record is flushed to the journal before the state, the shadow or the sequence advances. Failures are retried and reported, never swallowed. CRC and batch-end lines reject torn writes, and readers never move a cursor past anything.
7. library.json: never written by v2, so there are no whole-file rewrites and no new conflict copies. Settings > Storage lists the existing library.conflict-*.json files with a delete button, and retirement (section 9.5) moves them aside.
8. Open page deleted elsewhere: it is trashed, not detached. It stays open with a banner, edits keep saving, Restore is offered, and undo survives because LoadPage is never called for a merge.
9. Conflict copies: identity comes from file headers and op contents, never from names. A copy is just another input to an idempotent join. Legacy logs are read by each op's own A field, and copies of them are deduplicated by the join.

The further defects:

- D1: there is no stored counter. Sequence numbers are per run, and the clock is recovered from the journal.
- D2: the journal is replayed at every startup, and library.json is not loaded after migration.
- D3: per-field registers for containers, plus the reflection test.
- D4: order keys for every sibling list and element list.
- D5: no replay order. Orphans are held, then shown under Unfiled.
- D6: section 6.
- D7: flush before compare-and-set, in one callback.
- D8: undo hygiene in section 7.3.
- D9: random layer keys.
- D10: the same-batch rule in section 9.2.
- D11: the single-instance redirect plus the secondary-writer mode.

## 9. Migration

### 9.1 First launch of a v2 build, on every device

There is no special first device. Every device runs the same steps, on the worker, before the window adopts the library:

1. Decide whether there is anything to migrate.
   - library.json is absent, or is the retirement stub (section 9.6), and Dir\sync\ holds any v2 file: there is nothing to migrate. Write the marker and join (section 7.6).
   - Otherwise load L0 with today's LibraryStore.Load, which fails closed exactly as today. If it fails, stop: v2 does nothing, and nothing is written.
   - A genuine first run (no library.json and no v2 file) takes today's path: the LectureInk folders, then Seed. L0 is the result.
2. Import every legacy log in Dir, conflict copies included (section 9.2), and join everything readable in Dir\sync\ (other devices may have migrated already).
3. Import trash.json. For each entry whose container is not in L0:
   - Create the container and its subtree, if the state does not already have them, as baseline records (below).
   - Set L = 2 at stamp (DeletedTicks converted to ms, 0, writer), with `del` from the entry.

   An entry whose container is in L0 is stale, because that container was restored, so it is ignored.
4. Write the baseline. For each entity in L0 and in the trash entries:
   - If the state has no record for the key: a creation at a baseline stamp (t = 0, c = library.json write time in ms, w = this writer).
   - If the state has the key: for each register where L0's value differs from the state's value, and the state's value was written by a legacy or baseline writer, write L0's value at t = min(library.json write time, now + 1 hour), with b = the state value's stamp. These are edits the old build made and never logged: problem 4's absorbed edits, moves, order, and the unlogged fields of D3.
   - A register whose state value came from a v2 writer is never overridden by a baseline. Life is never raised by a baseline. A key that is dead in the state stays dead, which re-erases every element that problems 1, 2 and 9 resurrected into library.json, wherever a delete op survived (old compaction keeps tombstones). A key alive in the state but absent from L0 is left alive: an absence in an old library is ambiguous, and keeping the item is the safe side.
   - Baseline records that are creations at t = 0 merge across devices by their c, so the fresher library.json wins between baselines. Any real edit beats them.
5. Write the baseline to the journal. If sync\ had no v2 files, write it as a checkpoint instead of 70 one-megabyte segments, and also write folder.json. Then write the migration marker into the cache.
   - A crash before the marker repeats the migration. That is harmless: the same inputs produce the same records, and duplicates join idempotently.
6. Leave every legacy file untouched: library.json(.bak), trash.json(.bak), oplog.*, backups\, the conflict files, deviceid.txt and synccursors.json. deviceid.txt's value is recorded as legacyId in the device file.

If the device is offline, or sync\ is unreadable, the steps run with what is readable. The baseline's low stamps mean records that arrive later win wherever they are real edits.

### 9.2 Importing old op logs

The old logs are read continuously: at migration and on every pass while any old log changes. Each op becomes a record. The records go into this device's journal and are published, so every device converges on the union of every importer's reading. This is the answer to skeptic finding B3, where two devices imported the same old op differently.

- Identity: an old op is identified by (A, N, Ts, the hash of its line), and its actor is the op's own A field, never the file name. Ops from bridge logs and ops carrying "Q2":1 are skipped, because they are v2's own.
- Stamp: (Ts in ms, N, "L" + A).
- Element upsert (st, sh, tx, cm): a write of the element's value registers (v, or lay, body, style or meta, and cell), plus `X` = 1, with no life register. One exception:
  - If the previous op for the same key in the same actor's log, as far as this device has read it, is a tombstone, the old build undid its own erase. The record then also carries L = 1, a revive.
  - Otherwise an upsert never writes life. An old build that never received a v2 delete, and lasso-moves the deleted ink, therefore cannot bring it back (skeptic findings B2 and B10). An element an old build created has no life register at all and is alive through `X` (section 4.4).
  - The order key `z` comes from the element's index in the old op stream: an element first seen through an old op is placed after its page's last element.
- Element tombstone: L = 0 at its stamp, applied by id, with one exception. Skip the tombstone when it is in the same batch (same A and Ts) as a tombstone of a container that holds the element in the state, and the element appears in that container's trash.json entry. When no entry is found, skip it too. That old build put the subtree into its trash, and a restore must bring it back whole (D10). An element the user erased seconds before deleting the page is absent from the trash entry, so its tombstone stands.
- Container tombstone (nb, sec, pg): L = 2 (trashed), never purged, with `del` from trash.json if an entry exists. Child page tombstones in the same batch follow the rule above.
- Container upsert:
  - If the key has no `pos`, write `pos` from the op's Parent (a creation), plus `X` = 1.
  - Write `f.<Property>` exactly for the properties present in the op's meta JSON, each as its own register at the op's stamp.
  - Never write `pos` for an existing container, which matches old semantics: Apply never re-parented.
  - Never reset a field the op does not carry. This is what makes old meta ops order-independent (skeptic finding A2).
  - Write L = 1 only when the previous op for the key in the same actor's log is a tombstone, which is a restore on that old build. A restore on one old build of a container that another old build deleted therefore stays in the trash on v2 builds until someone restores it there. The content is safe, so this is a documented gap rather than a loss.
- Layers in a page op: one `attr` and `z` write per layer present. Absent layers are not deleted, matching the old "adopt, never clear" rule (SyncLog.cs:533-540).
- Generation changes, compaction, conflict copies and repeated N (from two instances or a lost synccursors.json) need no special handling. Every distinct op becomes a deterministic record, and the join dedupes.

### 9.3 Edits that old builds never log

Old builds rewrite library.json at every save, and some of their changes reach no op: moves, reorders, the D3 fields, absorbed edits, and elements created during an absorbed window. While any old log or library.json has changed in the last 30 days, each v2 device runs a library.json watcher on the worker:

- It keeps per-field hashes of the last library.json version it saw, for these registers: container `pos` and order, `f.<Property>` fields that old ops do not carry, and the set of element ids.
- On the first sight of a file it adopts nothing. Section 9.1 already took the baseline.
- On each later version, it writes a record at t = min(file write time, now + 1 hour) for each of those registers whose value changed between the two versions. It also creates each element or container id that appears and that the state has never seen (no record, no tombstone), with `X` = 1, so it is alive through the legacy rule.

Residual risk: when two or more old builds are active, library.json alternates between their snapshots. A field that one old build changed and another never saw can then be imported as a change and then a change back. That is the same flip-flop old builds inflict on each other today. Upgrade every device within a short period; the app shows a banner while old builds are active.

### 9.4 The bridge to old builds

While old builds are active, each v2 device appends old-format ops for its own v2 records to a bridge log, so old builds keep receiving element edits.

- File: Dir\oplog.<bridgeId>.jsonl, where bridgeId is the first 12 hex of SHA-256("quill-bridge:" + D).
  - The file is separate from the device's old log, so an old build on the same machine (after a downgrade) reads it as foreign and is never confused by it (skeptic findings C4 and C10).
  - N starts at 1 and continues from the highest N in the file. Every op carries "Q2":1.
- What it carries:
  - Element upserts (J = the element JSON, Parent = its page).
  - Element tombstones with Parent set, which the old Apply honours.
  - Container upserts in the old meta shape, for creations and for changes to the fields the old shape carries.
- What it never carries: container deletes, moves, trash state or purges. An old build therefore never removes anything outside its own trash on v2's behalf (skeptic findings A4 and A7).
- It never echoes. Old builds rebuild their shadow after a merge (SyncLog.cs:461-465), so a merged bridge op is never re-emitted. v2 skips bridge logs and Q2 ops on import.
- Text copies and rescues are ordinary own records, and they are bridged.
- It is compacted like an old log (latest op per key, tombstones kept, generation header), with containers written before their children (D5).
- It runs while any non-bridge old log or library.json has changed in the last 30 days. When it resumes after a pause, it first re-emits the latest value of every key changed during the pause.

### 9.5 Mixed period: what is guaranteed

- Nothing an old build logs is lost on v2. It arrives through the importer.
- Nothing an old build changes without logging is lost on v2, with two exceptions: the multi-old-build flip-flop above, and an absorbed edit to an existing element made after that device's migration snapshot. That edit is lost only if the old build never logs that element again.
- Nothing deleted on v2 comes back through an old build's upsert. Deleted ink can come back only as new fragments: an old build that has not yet received the delete partially erases through it. Fragments are new elements.
- Nothing v2 does removes anything on an old build outside that build's trash.
- Old builds see v2's element edits, new pages and renames. They do not see v2's moves, reorders, trash or purges.
- Old builds keep their own bugs among themselves, which is why stage 1 (section 12) ships its fixes to the old format first. Stage-1 builds are far better neighbours: for example, they no longer absorb edits.

### 9.6 Retiring the legacy files

Retirement runs when both hold:

- Every old actor id seen in any old log (by op.A) is the legacyId of a v2 device, or the user has confirmed in Settings that no older version is in use.
- No old log or library.json has changed for 30 days.

Then:

1. Copy (do not move) library.json, its .bak, trash.json, its .bak, the old logs, library.conflict-*.json and library.pending.json into Dir\legacy\<date>\, flushing each copy.
2. Replace library.json.bak, then library.json, with a stub, each by File.Replace with a backup name inside legacy\. The stub is `{"Notebooks":"This library was upgraded to Quill sync v2. Update Quill to open it."}`. The order matters:
   - At no instant is library.json missing. Otherwise an old build would fall back to the LectureInk folders or seed a new library (LibraryStore.cs:301-350).
   - At no instant is a real .bak paired with a stub primary.
3. An old build then fails closed. The stub throws on deserialize, the .bak stub does too, and anySource is true, so LoadFailed is set and the save gate stays shut (LibraryStore.cs:337-347).
4. Old logs stay where they are and are still imported, so an old laptop that returns after months still delivers its ops. If it uploads a real library.json over the stub, the watcher resumes from the last real version it saw, and retirement repeats after another quiet period.
5. Settings > Storage offers deleting legacy\ and the old conflict copies 30 days later.

### 9.7 Downgrade

- Before retirement, an old build on the same machine opens the last library.json the old build wrote (v2 never writes it). It replays every foreign log from its cursors, which includes this device's bridge log, so it catches up on elements and the old meta fields. Its own edits flow back through its old log.
- After retirement it fails closed on the stub.
- Settings > Storage has "Write library.json for an older version", which exports the current state on demand.

## 10. Failure handling

### 10.1 Crash or power loss on the write path

NTFS journals metadata but not data. A rename or a larger end-of-file can survive a power cut while unflushed data reads back as zeros, and a rename that has returned can still roll back if the log was not flushed. This design therefore acknowledges on a flushed append, not on a rename.

- During capture or diff: nothing is acknowledged. At most 10 s of work is lost, as today's debounce allows.
- During the journal append, before Flush(true) returns: a torn or zero-filled tail. The batch has no valid end line, so it is ignored, never acknowledged and never published. The next run is a new writer, so no sequence number is reused.
- After the journal flush, before commit: the record is durable. Startup replays it from the journal, and the state, the shadow and the sequence are rebuilt from the journal.
- During or after the shared append: startup republishes from the journal (section 7.6, step 6). A torn shared tail never hides anything, because readers skip incomplete batches and republishing writes a new file when the shared copy diverges.
- During the cache write: temp, flush, File.Replace with .bak. An older cache plus journal replay plus a shared pass gives the same state.
- During a device-file write: same pattern. A stale `merged` map only delays auto-purge.

### 10.2 Crash on the merge path

- During read, join or derive: nothing persisted except journaled derived records, which are durable own edits. The files are read again on the next start.
- Between posting a patch and the UI applying it: the patch is lost with the process and recomputed from the state at the next start.
- During the UI apply: the model is in memory only. The cache on disk describes an older shadow, and the next start recomputes.

### 10.3 Checkpoints and deletes

- A temp checkpoint that is partly written or zero-filled is ignored. A renamed checkpoint whose data did not survive fails its trailer check and is never used.
- A delete either happened or did not. Both states are valid, because every deleted file's content is in a verified checkpoint that has been readable for 7 days.

### 10.4 Migration, bridge and retirement

- Migration is repeatable until its marker lands, and every step produces deterministic records.
- The bridge is an old-format append. A torn last line costs an old build one op (the old reader's existing weakness), and the bridge's next compaction rewrites it.
- Retirement copies before it replaces, and replaces with backups, so every step can be retried.

### 10.5 Sync-client behaviour

- Late or out of order: the join commutes. Held keys wait for their parents, and a moved item stays put until its new parent arrives.
- Duplicated or re-uploaded: the join is idempotent.
- Conflict copies: variants are joined, then deleted only under exact-content coverage.
- Partial or growing files (Dropbox, Syncthing, network shares): only complete batches with valid CRCs are read.
- A delete that beats an append, or a sync client deleting a file: no record is lost while any device holds it. The owner republishes from its journal, and any device's checkpoint holds the joined state.
- Version rollback of a file, or OneDrive's "restore your OneDrive" rolling the whole folder back: devices whose state is newer republish their journals. Anything rolled back that a checkpoint or any device's state covers comes back through their checkpoints.
- Files-On-Demand placeholders:
  - Unreadable placeholders are skipped this pass.
  - v2 asks the user to keep Dir\sync\ "always on this device" (pinning; see section 15), because the journal makes acknowledgement independent of hydration.
  - A placeholder is never treated as a deletion.
- Sharing violations: skipped and retried on the worker, never on the UI thread.

### 10.6 Clocks

- Skew of minutes or hours: the per-key bump keeps causality. Skew only decides genuinely concurrent conflicts, in favour of the faster clock by the size of its lead.
- A clock days ahead: its stamps are joined but not adopted into other clocks, so it drags no one forward, and a warning names it.
- A clock that jumps backwards, or a corrected fast clock: l restarts at now, the per-key bump keeps every causal edit winning, and nothing stalls, because nothing depends on wall time except the auto-purge guard. That guard refuses to run while this device's clock disagrees with the others' by more than a day.

### 10.7 A device offline for a month, or a year

- It comes back with its own journal, which it publishes. Its edits join by stamp. Deletes made elsewhere beat its edits to the same elements.
- Its offline text edits are kept as copies wherever they conflict.
- Pages trashed elsewhere stay trashed, with its edits inside them. Auto-purge waited for its acknowledgement for 60 days, and the rescue rule covers longer absences.
- Nothing it holds can resurrect anything, because every tombstone still exists.

### 10.8 Restores, wiped profiles and second instances

- A machine restored from an image: a new run id, an old cache and old journal segments. All of it joins idempotently with the newer shared state. Its journal may republish batches that are already published, which is harmless.
- A wiped profile: a new D. It joins as a new device. Unpublished journal batches are lost only if the shared folder was unwritable when they were written, and the status line was saying so.
- Second instance: section 7.7.

### 10.9 Disk full, read-only folder, missing folder

- Journal write fails: SyncWriteError and no acknowledgement. Repeated in the status line.
- Shared folder unwritable or missing: edits are acknowledged locally, publishing is retried, and the status line says "not yet synced".
- A data folder that is missing at launch (audit finding 12) is out of scope here. This design never seeds a library into it, because the state comes from the local cache and journal.

## 11. What this design must not make worse

- Paint stays in %LOCALAPPDATA% keyed by the folder path (audit finding 7, KNOWN-2), and does not sync. HasPaint becomes a synced page field, so a peer knows a page has paint it cannot show. It must not clear a peer's paint.
- Images and audio are absolute paths under the data folder. They sync as `f` and `v` values unchanged. Making them relative is audit finding 7's fix, not this one.
- settings.json is unchanged. Library properties that used to ride along in library.json without being settings (Recents, calculator state, dial choices) become device-local, so they stop following the user between PCs. That is a deliberate behaviour change.
- "Change storage folder" (LibraryStore.SetDataFolder) must be rewritten for v2 before stage 6 ships. Today it saves library.json into the new folder (audit finding 7). Under v2 it must instead:
  - write a verified checkpoint and folder.json, with the same libraryId, into the new folder's sync\;
  - publish the journal there;
  - refuse a folder that already holds a different libraryId, unless the user chooses to open that library instead.

## 12. Staged build plan

Each stage ships on its own, is small enough to review, and has a harness gate.

- Stage 0. The harness (section 14) with the LegacyDevice driver, and the acceptance tests written against today's code, where most fail. No product change.
- Stage 1. Hotfixes in today's op-log format, which also make old builds better neighbours during the mixed period:
  - tombstones carry Parent, from a key-to-parent map kept beside the shadow, and Apply finds an element by id when Parent is missing;
  - a strict log-name pattern, so conflict copies are reported and ignored;
  - append, Flush(true), then advance the shadow and counter, and never swallow a failure (report it through WriteError);
  - the cursor stops at the last complete line;
  - the Lamport counter is recovered from the highest N in the device's own log;
  - the shadow is refreshed only for keys Apply touched;
  - flush texts before the merge, not after (D7);
  - after a merge, check that `_curPage` is still in the tree;
  - the single-instance redirect.

  Gate: AT-01, AT-02, AT-04, AT-06, AT-09, AT-10 and AT-11 pass in their legacy variants.
- Stage 2. The v2 core, in pure .NET with no UI:
  - stamps and the clock;
  - the record codec;
  - registers, the join and the projection;
  - order keys;
  - the register table with its reflection test;
  - CopyFrom per model type with a reflection test.

  Gate: the join property tests and the reflection tests.
- Stage 3. Storage:
  - a file-system interface (so the harness can substitute SimFs);
  - journal, publish and segment readers;
  - the consumed-files table;
  - cache, checkpoints and the deletion rules.

  Gate: AT-08, AT-09, AT-10, AT-19 and AT-20 at engine level.
- Stage 4. The legacy importer, migration and the bridge, run against LegacyDevice in the harness. Gate: AT-15, AT-16 and AT-17.
- Stage 5. Dark launch in the app, behind a flag:
  - v2 migrates, journals, publishes and merges into its own state;
  - library.json and the old log stay authoritative;
  - after each save, a debug check compares v2's projection with the live library and logs every difference;
  - the UI and worker costs are measured.

  Gate: two weeks on the owner's machines with no unexplained difference.
- Stage 6. v2 owns load and save on one device:
  - load from cache and journal;
  - save through the journal;
  - library.json no longer written;
  - the trash view from the state;
  - foreign files joined at startup only, with no live hand-off yet;
  - the bridge on.

  Gate: AT-03, AT-04 and AT-05 with merges at launch.
- Stage 7. The live hand-off:
  - patches, compare-and-set and deferral;
  - ApplyRemoteChanges, the identity map and undo hygiene;
  - the trashed-page banner.

  Gate: AT-06, AT-11, AT-12, AT-14 and AT-18.
- Stage 8. Keep-both, the rescue rule and the guarded auto-purge. Gate: AT-07, AT-13 and AT-21.
- Stage 9. The library.json watcher and the mixed-period banner. Gate: AT-17 extended.
- Stage 10. Dirty-page capture, which removes the whole-library serialize from the save:
  - element counts in the skeleton;
  - MarkPageDirty at OCR and import;
  - the idle sweep;
  - the full capture at idle and at close.

  Gate: AT-18 with the stage-10 budget, and AT-22.
- Stage 11. Legacy retirement, the stub, and the cleanup UI for conflict copies and legacy\. Gate: AT-23.
- Stage 12 (optional, only if telemetry shows tombstone growth matters). Tombstone collection, per section 13. Gate: AT-13 and AT-19 with collection on, plus the mutations listed there.

## 13. Costs

All figures are estimates. Nothing was measured, and stage 5 measures them.

- UI thread per save:
  - Stages 6-9: today's whole-library serialize and nothing more. Sync work moved off the UI thread entirely; today's OnSaved diff on the UI thread is gone.
  - Stage 10: about 5-10 ms per MB of dirty page, plus about 1 ms per 1,000 pages for the skeleton.
- UI thread per merge: hashing each touched element of a page and applying it in place, in slices of at most 5 ms for pages other than the current one.
- Worker per save:
  - Stages 6-9: parsing the 50-70 MB capture (roughly 0.3-0.6 s), hashing the registers, then one small journal append with a flush and one small shared append with a flush.
  - Stage 10: proportional to the dirty pages.
- Memory: the worker's state holds register values as UTF-8 plus about 40 bytes per register. Expect 80-130 MB beyond today's model. This is the largest new cost, and stage 5 must measure it.
- Disk in the shared folder:
  - one or two checkpoints per device, each an estimated 15-25 MB gzip;
  - at most 32 MB of uncovered segments, plus 7 days of grace;
  - tombstones at about 40 bytes each. A heavy user erasing 100,000 elements a year adds about 4 MB a year to each checkpoint.
- Disk locally: the cache (about the library's size, plus its .bak), journals of a few MB, and 12 gzip backups.
- Upload per save: one segment of up to 1 MB re-uploaded, against 50-70 MB today.
- Startup: about today's library.json parse, on a worker. A first join on a new device downloads the checkpoints and segments and parses about one to three times the library.

Tombstone collection is not in the first release. Every serious failure the skeptics found in designs (a) and (b), and the catastrophic one in (c), sat in that machinery:

- for (a): collection racing late segments, restored snapshots and permanent legacy logs;
- for (b): a purged subtree collected by ancestry, re-joins after expiry, and expiry by wall clock;
- for (c): floors raised from an incomplete view.

Tombstones are small. If stage 12 is ever built, it must follow these rules:

- A tombstone may be dropped only when it is older than 180 days, and every device active in the last 90 days, by the observer's monotonic observation, has published a `merged` map covering it.
- A record for a key absent from the state is dropped only if its stamp is at or below the published collection floor, and every device has acknowledged its writer and sequence. Both conditions are required: the stamp test protects late copies from a cloned process, and the sequence test protects a returning device's unsynced edits.
- A device whose cache predates the floor must rebuild from a checkpoint plus its own journal before publishing anything.
- No purged subtree may be dropped by ancestry. Every descendant needs its own tombstone, and those tombstones carry base stamps.
- Collection pauses while old logs are active.

## 14. Test plan

### 14.1 The harness

tools/SyncSim is a headless console project in the pattern of tools/LayerRoundTrip. It compiles the real model files and the real sync core with <Compile Include>, plus a shim, and uses no WinUI. The sync core therefore talks to a small file-system interface rather than to System.IO directly.

The simulated world:

- SimFs: an in-memory file system per device with explicit durability.
  - Written bytes stay volatile until Flush(true).
  - A crash discards the volatile layer, optionally keeping a random prefix of an unflushed append (a torn write) or zero-filling it. It optionally keeps or rolls back a rename whose log was not flushed.
  - It supports File.Replace, Move, Delete, append and enumerate.
  - It can inject sharing violations, full disks and read-only folders.
- SimCloud: a sync-client model between the devices' copies of the data folder, with four profiles (OneDrive, Dropbox, Syncthing, Google Drive).
  - Each file has a cloud version. Uploads and downloads happen after random delays, in any order across files.
  - Files arrive whole or, in the Dropbox and Syncthing profiles, as growing prefixes.
  - A file changed on two devices before either syncs produces a conflict copy under the profile's naming.
  - Deletes propagate and can beat a concurrent append. Duplicate deliveries, re-uploads of stale versions and a whole-folder rollback to a date can be injected.
  - Files-On-Demand: a file can be listed but unreadable for a while.
- SimClock: one per device, with configurable skew, forward and backward jumps, and correction.
- SimDevice: runs the real sync engine, a projection standing in for the UI, and the real hand-off logic, driven by a scripted or random user. The user actions are:
  - draw, partial erase (remove one id, add fragments), object erase;
  - move and recolour;
  - type into a text box, with typing held unflushed for a while to mimic the RichEdit;
  - add and delete comments;
  - rename, move and reorder pages and sections;
  - delete to trash, restore and purge;
  - undo and redo, and switch pages.

  A device can be killed between any two SimFs calls, reinstalled (local app-data wiped), imaged and restored, cloned while running, taken offline for simulated days, or run as two instances.
- LegacyDevice: runs today's main SyncLog.cs and LibraryStore save path, frozen as a linked copy in tools/SyncSim/Legacy under a renamed namespace. Old-build behaviour is then the real code, not a model of it. A Stage1Device runs the stage-1 hotfixed old format.

### 14.2 The oracle

Every user action is recorded with its device and the instant it was acknowledged (the journal flush). After quiescence (all files delivered, every device merged twice, no pending timers) the oracle checks:

- Convergence: every device's projection is byte-identical after canonical serialization.
- No lost acknowledged edit: every acknowledged edit is present unless an edit ranked later by section 4 replaced it. A concurrent text body survives as a copy.
- No resurrection: no element or container is live after its last acknowledged delete, unless a later acknowledged undo, restore or rescue brought it back.
- Trash completeness: every container deleted on any device is live (restored), in the trash on every device, or purged by an explicit purge or by a guarded auto-purge.
- Integrity: no duplicate ids in any list; every live page is reachable; every element's page exists or is held.

It also checks invariants after every simulated step: a kill and restart at that point always loads, and the open page is reachable or flagged as trashed.

### 14.3 Acceptance tests

Each test is a fixed-seed scenario. A randomized soak runs the same oracle over thousands of seeds per profile.

- AT-01 ElementDeletePropagates: A draws S and syncs, then erases S; B ends without S.
- AT-02 PartialEraseNoDoubleInk: A partially erases S into F1 and F2; B ends with F1 and F2 and without S. Variant: B concurrently moves S while offline; S stays erased.
- AT-03 FreshDeviceJoin: A draws and erases 200 strokes over a day; new device C joins with empty local state and ends equal to A. C's first save changes nothing on A.
- AT-04 ReinstallNoResurrection: the audit's single-PC test. A wipes its local app-data and restarts. The legacy variant also deletes deviceid.txt, its .bak and synccursors.json.
- AT-05 MoveThenContainerDelete: A moves P from S1 to S2, and B, not yet seeing the move, deletes S1. Both end with P live in S2, and S1 with its remaining pages in the trash.
- AT-06 UnsavedEditSurvivesMerge: A types, unsaved; a merge from B lands mid-edit; A saves. A's text reaches B.
- AT-07 ConcurrentEditsConverge: A and B edit the same stroke, shape, text body, text position and page name while offline. All devices agree, the text body loser is a copy, and the position and body changes both survive.
- AT-08 CrashEveryStep: for each named crash point of section 10.1 to 10.4, kill and restart, in both crash modes (torn and zero-filled). The points are enumerated, not sampled.
- AT-09 TornTail: a device dies mid-append. Peers read the torn file repeatedly while it is torn and after the device restarts. Nothing acknowledged is lost, and nothing unacknowledged appears.
- AT-10 ConflictCopiesEverywhere: each profile's conflict naming applied to every sync file type and to old logs. Nothing is lost, nothing resurrects, and variants are deleted only under exact coverage.
- AT-11 PeerTrashesOpenPage: B trashes the page open on A while A draws. A's later strokes are saved and reach B inside the trashed page, A sees the banner, and Restore on A brings back everything.
- AT-12 UndoSurvivesMerge: A draws three strokes, a merge from B touches the page, and A undoes three times. Exactly A's three strokes go. Variant: a remote partial erase of A's stroke, then undo and redo on A; no doubled ink, and no duplicate id.
- AT-13 MonthOffline: C is offline for 30 days while A and B erase, move, trash and purge, and C edits pages that A trashes. When C returns, nothing resurrects, C's edits survive (inside the trash, rescued, or as copies), and auto-purge waited.
- AT-14 ClockSkew: B runs 3 days fast, then is corrected; A's clock jumps back a year. Causally later edits always win, nothing stalls, and the warnings fire.
- AT-15 ClonedProcess: A's running process is cloned to D, and both edit. Both devices' edits survive, no file is deleted on a twin's coverage, and both clones switch run ids.
- AT-16 MigrationFromLegacy: fixtures produced by LegacyDevice, including parentless tombstones, compactions, absorbed edits, unlogged moves, a lost synccursors.json, two instances reusing N, conflict copies of old logs, trash.json entries, and old-build container deletes (D10). First launch of v2 on each device loses nothing, re-erases resurrected items, and restores whole containers from the trash.
- AT-17 MixedBuilds: two LegacyDevices, one Stage1Device and two v2 devices share a folder for 14 simulated days, with edits, erases, moves and deletes on all of them. Then the old devices upgrade, one of them 60 days late. The oracle passes, under the guarantees of section 9.5.
- AT-18 UiThreadBudget: with a generated 60 MB library, the harness counts what the UI-side code serializes and hashes per save and per merge, and asserts the budget for the current stage.
- AT-19 RollbackAndRestore: OneDrive's whole-folder rollback to a date three days back, and separately a device restored from a 4-month-old image. Nothing is lost or resurrected.
- AT-20 ReadersNeverStall: a sealed segment rolled back to a shorter version, a sharing violation at startup, and a garbled middle region. Every complete batch is read, and nothing waits.
- AT-21 PurgeRescue: A purges a container while B, offline, restores it and edits inside it. B's work survives as a revived or recovered subtree on every device.
- AT-22 DirtyCaptureCompleteness: PDF import of 20 pages, OCR on non-current pages, and table heal on load, each followed by a kill at a random point. Nothing is missing after restart.
- AT-23 Retirement: retirement with an old build that returns after 90 days. Nothing is stranded, an old build launched afterwards fails closed, and no instant leaves Dir without library.json.

### 14.4 Planted mutations

The harness builds each mutation and fails if every acceptance test stays green. Each mutation names the tests that must turn red.

- M01: element delete records require a page lookup (drop the record when the page is unknown). AT-01, AT-02, AT-16.
- M02: commit the state, shadow or sequence before the journal flush. AT-08.
- M03: swallow a journal append failure and commit anyway. AT-08.
- M04: rebuild the shadow from the live projection after a merge. AT-06.
- M05: join by arrival order instead of stamp order. AT-07, AT-10.
- M06: treat a variant as a different writer and skip it, or derive identity from file names. AT-10, AT-15.
- M07: stop reading a file at the first bad line. AT-20.
- M08: skip journal replay at startup. AT-04, AT-08, AT-19.
- M09: let a field write revive a dead element. AT-02 (variant), AT-17.
- M10: stamp a local write with Next() only, without the per-key bump. AT-14.
- M11: reload the open page through LoadPage on merge. AT-12.
- M12: apply a container delete by tombstoning its subtree. AT-05, AT-11.
- M13: remove the Flush(true) on the journal append. AT-08 in the power-loss mode.
- M14: reuse the writer id across process runs. AT-08, AT-15.
- M15: delete a segment on sequence coverage instead of exact-content coverage. AT-15, AT-19.
- M16: apply old child tombstones from a container-delete batch. AT-16.
- M17: let a baseline override a register written by a v2 writer, or raise life. AT-16, AT-17.
- M18: apply compare-and-set before flushing texts. AT-06.
- M19: insert on undo without the id check. AT-12.
- M20: auto-purge without the acknowledgement guard. AT-13.
- M21: bridge a container delete. AT-17 (old devices lose a page outside their trash).

### 14.5 Property tests

- The join is commutative, associative and idempotent over random record sets, including equal stamps with different values.
- The projection is a pure function of the state.
- After any delivery order and a full exchange, the set of keep-both copies and rescued subtrees is the same on every device.
- Order keys: random insert, move and delete sequences keep the order every device sees identical, and keys stay under the rebalancing limit.

## 15. Could not be established without Windows

- Whether FlushFileBuffers through .NET's Flush(true) on a file in an OneDrive folder reaches the disk with the Cloud Files filter driver in the path, and its latency on typical laptops. The journal lives in %LOCALAPPDATA% partly to avoid depending on this.
- How OneDrive treats a file that is appended every few seconds:
  - whether it holds it open while uploading (sharing violations on the next append);
  - whether it can upload a mid-append prefix;
  - whether differential sync makes the re-upload cheap.
- Whether FileShare.ReadWrite | FileShare.Delete reads of a file OneDrive is writing see a consistent prefix.
- The exact conflict-copy names in current OneDrive, Dropbox, Google Drive and Syncthing builds, including localized machine names and names longer than the path limit.
- Files-On-Demand:
  - whether .NET can read the placeholder and in-sync state reliably (file attributes RECALL_ON_DATA_ACCESS, PINNED and UNPINNED, or CfGetPlaceholderStateFromAttributeTag);
  - whether setting the pinned attribute on Dir\sync\ keeps it hydrated;
  - how reads of placeholders fail offline.
- FileSystemWatcher reliability on OneDrive folders (missed or duplicated events). The 20 s timer is the fallback.
- Whether AppInstance.FindOrRegisterForKey redirection works in this unpackaged app (Quill.csproj:15, WindowsPackageType None).
- Real costs, all estimated in section 13:
  - serializing 50-70 MB on the UI thread;
  - parsing it with Utf8JsonReader on a worker;
  - hashing per register;
  - the memory of the state;
  - gzip of a checkpoint;
  - startup projection time.
- RichEdit behaviour:
  - whether rebuilding one text box without a flush, right after a flush, loses nothing;
  - whether an uncommitted IME composition is in the flushed text;
  - how TextFlushPolicy's latches behave across ApplyRemoteChanges.
- How the dispatcher behaves when patches are applied in 5 ms slices during inking, and whether a patch for the current page waiting for pointer-up can starve during long pen sessions. A 30 s maximum wait, after which the patch is applied between strokes, is the proposed fallback.
- File.Replace semantics on OneDrive folders for the cache, the device file and the stubs, and how often ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 occurs there.
- Windows time behaviour: how far clocks drift between NTP corrections on typical laptops, and whether the stamps in DateTime.UtcNow ever step backwards in practice.

## 16. Final adversarial review and what changed

A final reviewer attacked the synthesized design. Its findings and the fixes are recorded here so they are not re-raised.

(Filled in after the review.)

## 17. Rejected alternatives

- Design (a) as the architecture: repair the op log in place. Rejected because:
  - it acknowledges on an append in the shared folder, so OneDrive locks and placeholders block saves;
  - readers need contiguous sequence numbers, so rollbacks and sharing violations stall sync silently;
  - compaction deletes segments before their replacements upload;
  - a snapshot restored from an image can outlive tombstone collection and republish deleted items;
  - device fingerprinting and forks re-home tens of MB on benign events such as a double launch;
  - whole-element last-writer-wins lets a concurrent move undo a partial erase.

  Its stage-1 subset is adopted as stage 1. From it this design keeps:
  - the low baseline stamp for migration;
  - the bridge log with a separate name;
  - flush before compare-and-set;
  - treating a missing local object as an unsaved delete;
  - the "Recovered" section.
- Design (c)'s storage: immutable per-page files. Rejected because:
  - every save rewrites the whole page file (megabytes for heavy pages), and OneDrive's recycle bin counts every superseded file against quota;
  - thousands of files slow a first sync;
  - the floor rule can delete live content when raised from an incomplete view;
  - superseded files are deleted by non-owners before the new file has propagated (Syncthing);
  - per-element version vectors with kind-specific rules break undo of an erase, depend on merge order for text, and never let legacy imports dominate.

  From it this design keeps:
  - never-reused file names and identity from headers;
  - conflict copies treated as just more inputs;
  - deterministic text copy ids;
  - per-field registers for containers;
  - its threading (5 ms slices, waiting for gestures and focus);
  - the legacy stub that makes old builds fail closed.
- Design (b) as written:
  - Its tombstone collection is deferred (section 13).
  - Its genesis at the library.json write time is replaced by low baselines, because a late or offline genesis reverted the whole library.
  - Its one-way mixed period is replaced by the bridge.
  - Its single elected checkpoint writer is replaced by per-device checkpoints.
  - Its writer epoch is replaced by a run id per process.
  - Its losing-writer text copy is replaced by deterministic copies.
- Keep a shared library.json and improve the conflict guard. Rejected: a whole-file last-writer-wins document cannot merge, and every fix still leaves 50-70 MB conflict copies and lost edits.
- A text CRDT for RTF (the collaboration plan's stage 3). Rejected for now: RichEdit RTF is not a character sequence Quill controls, and keep-both is the safe approximation. The register model leaves room for it, because a body register could later hold a CRDT.
- A relay server (the collaboration plan's stage 2). Out of scope. The record format is transport-neutral, so a relay could carry the same batches later.
- Field-level registers for strokes and shapes. Rejected: they roughly triple the per-element metadata of the bulk of the library, for a concurrency case (two devices moving and recolouring one stroke at once) that almost never happens.
- Per-element version vectors. Rejected in favour of stamps plus one base stamp. The only places that need concurrency detection are text bodies and purges, and the base stamp is enough there.
- Delete-loses for ink: keeping a concurrently edited stroke alive. Rejected: it reintroduces doubled ink after a partial erase. Text and comments get keep-both instead, so typing is never lost.

## 18. Decisions for the owner

- Deletes beat concurrent edits for ink and shapes. The alternative is doubled ink, so this is the recommendation, but it is a policy choice.
- Rolling backups move to %LOCALAPPDATA%. Keeping them in the synced folder costs about 12 x 70 MB per device in OneDrive.
- Library properties other than settings become device-local: Recents, calculator state, dial choices.
- Retirement of the legacy files is automatic once every old installation has upgraded; the alternative is user confirmation only.
- Stage 1 ships hotfixes to the old format before v2. The recommendation is yes, because it cuts the damage of finding 6 now and makes old builds safer neighbours during the mixed period.
