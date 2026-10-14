# Sync v2

Status: design only. Written against main at 0c41c7b (October 2026). Nothing here has been built or run: Quill needs Windows and the Windows App SDK, and this work was done on Linux. It builds on the data safety audit (docs/design/data-safety-audit.md on the branch cloud/data-safety-audit), findings 6, 9, 16 and 19 and the op-log notes in its "Checked and sound" section.

## 1. How this design was produced

- The nine problems in the brief were re-checked against the code, line by line (section 2).
- Three designers worked independently, each on one approach: (a) repair the existing op log in place; (b) per-element last-writer-wins registers with tombstones and hybrid logical clocks (a state-based CRDT); (c) split the library into per-page files with per-page versioning. Each wrote a complete design covering all nine problems.
- Two skeptics attacked all three designs. The first took crashes at every step, sync-client behaviour and migration with mixed builds. The second took merge semantics and convergence, clocks and garbage collection, and the UI thread and the live model. Between them they produced about 60 concrete failure scenarios.
- The skeptics split on the winner: one ranked (b) first, the other ranked (c)'s storage first with (b)'s merge model. Both ranked (a) last. This document synthesizes one design. It takes (b)'s merge model and storage, adds the ideas from (a) and (c) that survived the skeptics, and removes the machinery that produced most of the serious failures: tombstone garbage collection, a special first-device migration, and device-identity reclaiming.
- A final adversarial review attacked the synthesis and found 17 problems, three of which broke convergence or lost data. All are fixed in this text; section 16 lists them.

## 2. The problems, re-verified

All nine hold. Citations are to main at 0c41c7b.

1. Element delete tombstones carry no parent. Deletes are written as `new Op { K = parts[0], Id = ..., J = null, ... }` with no Parent (SyncLog.cs:285-290); upserts carry it (:283). Apply for st, sh, tx and cm does `FindPage(lib, op.Parent)` and returns null (:543-546), and `seen = op.N` still advances (:443). Holds.
2. A clean replay resurrects erased items. Device id and cursors live in %LOCALAPPDATA%\Quill (:87-91, :172-197), and a missing cursor means offset 0 and seen 0 (:415-416). Every upsert is re-applied, every element delete is dropped (problem 1), and the shadow rebuild after the merge (:461-465) makes the result permanent. Compaction (:33, :322-398) bounds it to ops since the last compaction. Holds.
3. Page and section moves emit no op. Correction to the brief: page and section ops do carry Parent (:243, :246). But the hash covers only PageMetaJson and SecMetaJson, which have no parent (:226-234), so a move changes no hash and emits nothing, and Apply never re-parents an existing page or section (:487-541). A later section delete on the peer also emits tombstones for every page and element it held there (LibraryStore.cs:1053-1073 then SyncLog.cs:285-290), which removes the moved page here outside any trash. Holds.
4. The shadow is rebuilt from the live model after a merge (:461-465), absorbing unsaved local edits. Holds, and it is frequent: unsaved edits exist for the whole debounce window, and for minutes during continuous writing (audit finding 10).
5. Ops apply by per-device counter only (:441-443). Concurrent edits to one element leave each device holding the other's version. Holds.
6. The shadow and counter advance before the append (:282-283, :288-289). File.AppendAllText is not flushed, and a failure is swallowed (:292-297). The cursor jumps to fs.Length (:447) past a torn last line. Two additions: the append and the cursor write share one try, so a failed cursor write after a good append loses the Lamport bump and the next save reuses N; and LibraryStore.Save swallows any exception from OnSaved (LibraryStore.cs:573). Holds; the torn-line part stays PLAUSIBLE.
7. library.json is in the shared data folder and rewritten whole by every device; the conflict guard (LibraryStore.cs:710-748) writes library.conflict-*.json copies of 50-70 MB, at most one per 5 minutes per device, and nothing deletes them. Holds.
8. Apply returns the page id even when the page is already gone (SyncLog.cs:517), and RunForeignMerge reloads the detached `_curPage` (MainWindow.xaml.cs:10284-10287), so every later edit is lost. LoadPage clears undo (InkSurface.cs:879). Holds.
9. The glob `oplog.*.jsonl` (SyncLog.cs:408) and `Split('.').Last()` (:413) make oplog.abc-LAPTOP.jsonl a new actor, replayed from offset 0. Addition: a conflict copy of the device's own log is replayed at itself. Holds.

Further defects found while verifying. A design must fix them or at least not inherit them.

- D1. The per-device sequence ("Lamport", never bumped on receipt) is stored in synccursors.json, separately from deviceid.txt. Losing the cursor file while the id survives resets it to 0, and peers then skip every new op (`op.N <= seen`). Problem 6's cursor-write path reaches the same result.
- D2. A device never replays its own log. At startup it loads library.json, which a peer may have written before merging this device's latest ops. Those edits vanish locally, and this device's next whole-file write propagates the loss.
- D3. PageMetaJson and NbMetaJson are hand-picked subsets, so these fields never travel as ops: Paper, GridColor, GridOpacity, GridAngle, the grid-editor fields, Perspective, the page-size fields, HasPaint, RefFrame, OcrText, PasswordHash and the per-notebook defaults. The comment at NoteModels.cs:512-514 says Perspective is carried in PageMetaJson; it is not.
- D4. Sibling order (MoveInList, MainWindow.xaml.cs:4492) is never synced. Apply re-sorts strokes by CreatedTicks and appends shapes and texts at the end, and erase fragments, pastes and duplicates all copy the original's CreatedTicks (NoteModels.cs:160-165, :248, :322).
- D5. Compaction keeps the latest op per key in N order. A container whose metadata changed after its children were created then appears after them. A fresh reader puts sections into the first notebook (SyncLog.cs:489), puts pages into the first section (:523-524), and drops element ops whose page does not exist yet.
- D6. OnSaved serializes and hashes every entity of a 50-70 MB library on the UI thread at every save (SyncLog.cs:277-284). MergeForeign does file IO and a full shadow rebuild on the UI DispatcherTimer.
- D7. RunForeignMerge flushes live text boxes after the merge has written remote RTF into the model (MainWindow.xaml.cs:10286). A reached box writes its own text back over the remote text, and the next save emits it, so the remote edit is lost everywhere.
- D8. Undo re-inserts objects without checking ids: RemoveStrokesAction and ReplaceStrokesAction (UndoRedo.cs:103-107, :128-133), RemoveTextAction (:274-275) and RemoveMixedAction (:404-409). Any merge that inserts a new object under an existing id, followed by a local undo, leaves two elements with one id. Some undo actions also restore whole snapshots: RecolourTextsAction.Undo puts the old RTF back (:917-923).
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
- Migration is the same on every device. It imports the old logs and library.json as records tagged by origin and stamped so that any real edit beats them, and it repairs items the old bugs resurrected. A bridge log keeps old builds receiving element edits during the mixed period. Tombstones are never collected in the first release, and purged trash is hidden but not yet reclaimed (section 13).

## 4. Data model

### 4.1 Keys, kinds and registers

A key is (kind, id). The kinds are:

- nb (notebook), sec (section), pg (page): the containers.
- ly (layer): id is `<pageId>/<layerKey>`.
- st (stroke), sh (shape), tx (text box), cm (comment): the elements. A creation record carries `pg`, the element's page id, and the state keeps it with the key. Elements never change page (paste, duplicate and cross-page moves mint new ids), so the page id is an attribute fixed at creation, not a register. A delete record may omit it.
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

Any key may also carry `X`, the "exists by legacy" register, value 1. It is written only by imports of old-build data (section 9) and never by a v2 edit. A v2 edit of an element an old build created therefore cannot erase the evidence that the element exists (section 4.4).

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

Stamps order by t, then c, then w (ordinal string compare). Two different values with an equal stamp can only come from a reused legacy N or a cloned running process; they are ordered by the SHA-256 of the value bytes, so every device picks the same winner. Derived records that several devices write independently (keep-both copies, section 4.5) have equal stamps and equal values, so they are plain duplicates. Text form: `t.c.w`, with t and c in lowercase hex.

Special stamps:

- Baseline stamps (migration): t = 0, c = the library.json write time in ms, w = the migrating writer. They are lower than every real edit, and between two baselines the fresher library.json wins.
- Legacy stamps (imported old ops): t = op.Ts converted from .NET ticks to Unix ms, c = op.N, w = "L" + op.A.
- Replacement stamps, used by the baseline exceptions and the library.json watcher (sections 9.1 and 9.3), which record an old-build change to one specific older value: the successor of the replaced value's stamp, (t, c + 1, own writer). Such a record beats exactly the value it replaces and loses to any later edit. It never uses the time the change was noticed.

Clock rules:

- The clock is a hybrid logical clock l = (lt, lc), persisted in the local device.json (lazily) and recovered at startup as the maximum of that value and every stamp in this device's own journal.
- Next(): if now > lt then (now, 0), else (lt, lc + 1). The counter has 64 bits, so it does not wrap.
- Joining a remote record with stamp r: if r.t <= now + 1 hour, l becomes max(l, r). Otherwise l is not moved, and the status line names the writer whose clock is ahead (rate-limited).
- A local write to a register uses stamp max(Next(), successor of the register's stored stamp). An edit made after seeing a value therefore beats that value whatever the clocks say. This per-key bump is not fed back into l, so one far-future register does not drag the whole clock forward.
- If at startup lt > now + 1 hour (a clock that was fast has been corrected, or the clock went backwards), l restarts at now and the status line warns. Causality still holds through the per-key bump; only genuinely concurrent conflicts are decided by the skewed time. Nothing in this design depends on wall-clock time for correctness, because nothing is collected in the first release (section 13).

### 4.3 Records and the join

A record writes one or more registers of one key at one stamp:

`{"q": seq, "k": kind, "id": id, "pg": pageId?, "s": stamp, "o": origin?, "b": baseStamp?, "bm": {register: stamp}?, "pb": {key: {register: stamp}}?, "L": life?, "g": {register: value, ...}?}`

- `q` is the writer's sequence number, contiguous from 1 within a writer.
- `o` is the origin: `e` a user edit (the default when absent), `b` a baseline, `x` a baseline exception, `w` the library.json watcher, `l` a legacy import, `k` a keep-both copy, `r` a rescue. The state keeps each register's origin. Migration rules need it, because a baseline written by an ordinary writer cannot otherwise be told apart from an edit.
- `b`, on a value write, is the stamp of that register's value the user was looking at when the write was made. It is read from the shadow (section 7.3), never from the merged state.
- `bm`, on a delete (L = 0), maps each register of the key to the shadow stamp of that register. A delete therefore says register by register what the deleter had seen. A single maximum would let a move of a text box hide concurrent typing in it.
- `pb`, on a purge (L = 0 on a container), maps every key the purger held under the container (the container itself included, dead keys included) to its registers' stamps. It is the input to the rescue rule (section 4.5). A purge record may exceed the normal segment size and then gets a segment of its own.
- A creation or a revive (L = 1) always carries every register, so a revived entity is self-contained.
- Baseline, watcher and legacy records have no `b` and no `bm`.

The state holds, per key: `pg` where known, and per register (stamp, origin, value or dropped).

Join: for each register in an incoming record, keep the incoming value if the state has none or the incoming stamp is higher. That is the whole merge. It is commutative, associative and idempotent, so duplicated, reordered, late or conflict-copied files cannot change the result.

Dead values: while a stroke, shape or layer has L = 0, the state drops the value of every register stamped below the life register's stamp. It keeps that register's stamp and origin. Dead texts and comments keep every value, because a concurrent edit may still need rescuing (section 4.5). This keeps erased ink from growing the state, and it is safe:

- The decision depends only on stamps, so every device drops exactly the same values in any delivery order.
- A value stamped above the delete (a concurrent edit from a device that had not seen it) is kept. A later revive or a later delete compares against a real value, never against a hole.
- A revive is written by a device that had seen the delete, so every register in it is stamped above the delete and replaces every dropped value.

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
- A purged container (L = 0) is hidden, and so is everything under it. Its descendants keep their own life registers and values: a purge writes only the container's life. That is what lets a rescue (section 4.5) bring the whole subtree back by reviving the container alone.
- A key whose parent has no records at all is held invisible for 24 hours of local observation, then shown in a synthetic "Unfiled" section. Unfiled is projection only: it is not persisted and writes no `pos`. A user action on an Unfiled item writes real records. Nothing is ever placed in the "first notebook" (D5).
- The "Recovered" notebook has id UUIDv5(Quill namespace, "recovered-notebook"). The Recovered section for a container P has id UUIDv5(P + "recovered") and sits in that notebook. A rescuer creates either one (L = 1, at fresh stamps) if it is not alive, so concurrent rescuers write the same keys.

Order:

- Siblings, the elements of each list and the layers of a page are ordered by (order key, id).
- Order keys are base-62 strings compared ordinally (fractional indexing). The diff keeps the longest increasing run of existing keys and gives new keys only to items outside it, between their neighbours, so a move or reorder costs one record. A list whose keys grow past 32 characters gets one rebalancing write.
- New layers get a random unused key in [1, 2^30) instead of max + 1, which fixes D9. Existing keys are unchanged.

### 4.5 The two rules that are not a plain join

Both rules run on the worker whenever a join changes a register they look at. Each produces ordinary records. Those records are journaled before the merge that produced them is committed (section 7.2), and they are published, so every device converges on the result whichever device produced it first.

Keep-both for text. It applies to the `body` register of tx and cm only (the RTF of a text box, the text of a comment), and never to records of origin b, x, w or l.

- Two body values are concurrent when neither writer had seen the other: incoming r and stored v with r.s > v.s and r.b < v.s, or v.s > r.s and v.b < r.s. A body without `b` (a creation) counts as having seen nothing.
- If two concurrent values differ, the loser (the lower stamp) is kept as a new element. Its id is UUIDv5(Quill namespace, id + loser stamp). It has the original's `lay` offset by 16 px, `body` = the losing value, `cell` cleared (CloneAsFreeBox, NoteModels.cs:341-346) and a `z` just after the original. All its registers are stamped with the loser's stamp, with origin k.
- A delete of a text box or comment meets a body value stamped above the delete's `bm.body`, so the deleter never saw that text. The text is kept the same way, as a copy. A delete without `bm` (a legacy delete) never makes a copy.
- Any device that sees both values makes the same element with the same id, the same stamps and the same values, so copies are duplicates, not conflicts. A three-way race can produce one redundant copy, never lost text.
- A move, recolour or table change on one device plus typing on another is not a conflict, because they are different registers.

Rescue on purge. A purge carries `pb`, everything the purger held under the container (section 4.3).

- The check runs when the purge record joins, and again whenever a later record joins into any key under a purged container. Later records include old-build ops and the work of a device returning after a long time offline.
- The device looks at every key it holds under the purged container P, and at P itself. If any such key is absent from `pb`, or has a register stamped above its `pb` entry (a missing entry counts as unseen), then the purge hid an edit, a restore or new content the purger never saw.
- That device writes L = 1 on P alone, at a fresh stamp with origin r. If P's parent is not in the tree, it also writes `pos` to P's Recovered section (section 4.4).
- Nothing else is rewritten. Every descendant keeps its own registers, so concurrent erases, moves and edits keep their stamps and keep winning or losing exactly as they would have. Two rescuers write the same thing.
- The status line says which page or section came back, and why.

## 5. Files

### 5.1 The shared folder

New files live in Dir\sync\. Old builds list only `Dir\oplog.*.jsonl`, non-recursively (SyncLog.cs:408), and read only library.json and trash.json, so they never see anything below. Names:

- `folder.json`: `{"fmt":2,"libraryId":<guid>,"createdBy":D,"created":stamp}`. The first v2 device writes it once, with temp, flush and rename. If two devices race and a conflict copy appears, the lowest libraryId among the variants is canonical. The libraryId is informational and used by "Change storage folder" (section 11). Local state is keyed by the folder path, not by this id (section 5.4).
- `dev-<D>.json`: the device file, written only by device D (temp, flush, File.Replace). It holds:
  - the device part D, the machine name and the build;
  - the legacyId (from the old deviceid.txt, if any) and the bridgeId;
  - `merged`: per writer, the highest contiguous sequence merged, written only after this device has published every own batch it wrote before that merge;
  - `ckJoined`: the verified checkpoints this device has joined;
  - `trashSeen`: per trashed container, the life stamp of the trash it has seen.

  `merged` folds every writer whose files are all listed in a joined checkpoint into `ckJoined`, so the map holds only writers with live segments and does not grow with every launch. The device file is used for acknowledgements and auto-purge, never for the correctness of the merge.
- `seg-<W>-<firstSeq>.qsd`: a segment. W is the 20-hex writer id, and firstSeq is the first sequence number in the file, 10 digits.
- `ck-<W>-<seq>.qsc`: a checkpoint written by writer W covering its state at its own sequence seq.
- Temp files: `.~<final name>.tmp`. They match none of the patterns.

Canonical names are matched exactly and ordinally:

- `^folder\.json$`
- `^dev-[0-9a-f]{12}\.json$`
- `^seg-[0-9a-f]{20}-\d{10}(-r\d{1,4})?\.qsd$` (the `-r` suffix marks a republished copy, section 7.2)
- `^ck-[0-9a-f]{20}-\d{10}\.qsc$`

Any other name that starts with `folder`, `dev-`, `seg-` or `ck-` (case-insensitive) and does not start with `.~` is a variant. Variants include the OneDrive "-MACHINE(-2)", Dropbox "conflicted copy", Syncthing ".sync-conflict-..." and Google Drive " (1)" forms. A file's identity always comes from its header, never from its name. Segment and checkpoint variants are read and joined like originals, which is harmless because the join is idempotent. Variants of folder.json and device files feed only clone detection and the libraryId rule.

### 5.2 Segment format

A segment is UTF-8 lines. Each line is `<json>\t<crc32c of the json bytes, 8 lowercase hex>\n`.

- First line, the header: `{"f":"qseg/1","w":W,"d":D,"first":firstSeq,"build":"..."}`.
- Then batches. A batch is one or more record lines followed by an end line `{"end":lastSeq,"n":count}`.
- A batch counts only if every line in it is LF-terminated, passes its CRC and parses, its sequence numbers are contiguous, and its end line matches.
- Readers apply every complete batch anywhere in the file. A torn or garbled region therefore never hides later complete batches, and nothing ever waits for a missing sequence number.

A process never appends to a file it did not create, and every process run has a new W (section 7.7), so a segment has exactly one writer for its whole life. Journal segments in %LOCALAPPDATA% use the same format and names, and the shared copy is meant to be byte-identical to the journal copy.

A segment is sealed (no more appends) at 1 MB, after an hour, and when the process ends. After any failed append, to the journal or to the shared copy, the process also switches to a new writer id, so it never appends after a torn tail and never reuses a sequence number.

### 5.3 Checkpoint format

A checkpoint is a gzip stream of lines:

- Header: `{"f":"qck/1","by":W,"vv":{writer:highest contiguous seq merged},"files":[{"name","len","sha256"}],"legacy":{per old actor: import positions},"created":stamp}`.
  - `files` lists every segment, variant and earlier checkpoint whose content is fully joined into this state, with exact length and hash. It is what makes a file safe to delete.
- One line per key: `{"k","id","pg","r":{register:[stamp,origin,value]}}`. A dropped value (section 4.3) is written as `[stamp,origin,null,"d"]`.
- Trailer: `{"n":lineCount,"sha":sha256 of everything before the trailer}`.

It is written to a temp file, flushed with Flush(true), renamed, then re-read. It counts as verified only when the trailer matches. An unverified checkpoint is never joined and never used to justify a delete.

### 5.4 Local files

Location: `%LOCALAPPDATA%\Quill\sync\<16 hex of SHA-256 of the normalized Dir>\`. The directory is never renamed. Its device.json records the Dir path and the libraryId it has seen. At startup the worker scans every directory under %LOCALAPPDATA%\Quill\sync\ and replays the journals of any directory whose recorded Dir normalizes to this Dir, or whose libraryId matches any folder.json variant in Dir. A respelled path, or two devices racing to create folder.json, therefore never orphans a journal.

- `device.json`: `{D, l, Dir, libraryId, legacyId, bridgeId, bridgeN}`, written with temp, flush, File.Replace and .bak.
- `journal\seg-<W>-<firstSeq>.qsd`: this device's own segments. They are the acknowledged record of every local edit and the source for publishing. A journal segment is deleted 7 days after both conditions hold: a verified checkpoint lists a shared copy whose length and SHA-256 equal the journal segment's, and the local cache covers it.
- `cache.qsc` (with .bak): the state in checkpoint format, uncompressed, plus four things:
  - the consumed-files table (name, length, last write time, SHA-256 of the first and last 4 KB);
  - first-seen times for trashed containers;
  - the library.json watcher's per-field hashes (section 9.3);
  - legacy import state.
- `local.json`: the device-local fields (section 4.1).
- `device.lock`: opened with FileShare.None by the primary process.
- `instance-<W>\`: journal and cache of a secondary process, with its own `instance.lock` (section 7.7).

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
- The sync worker: one long-running thread with a queue. It owns the state, the shadow, the clock, all sync file IO, diffing, stamping, journal and publish, reading and joining, the two rules of section 4.5, building patches, the cache, checkpoints, backups, legacy import, the library.json watcher and the bridge.
  - The queue has two levels. Captures, acknowledgements, trash marks and the close request always run first. Long jobs run in chunks of at most 100 ms and yield between chunks: checkpoint compression, migration, watcher parses, legacy imports and bootstrap joins.
  - Within the first level, items run in the order the UI posted them.
  - In stages 6-9 a newer whole-library capture replaces an older one still waiting in the queue. From stage 10 a newer capture of a page replaces an older one of the same page.
- The thread pool: parallel parsing at startup and checkpoint compression, called from the worker.

The UI thread and the worker exchange only immutable byte arrays (rented from a pool, to limit large-object-heap churn) and plain result objects. The UI's captures and acknowledgements are processed in the order they were posted. That ordering is what keeps the shadow consistent (section 7.3).

UI-thread cost:

- In the first stages where v2 owns saving (stages 6-9, section 12), the capture is today's whole-library serialize, which SaveNow already performs at every save. Sync adds nothing to the UI thread beyond hashing the few elements a merge touches. Today's OnSaved diff, which serializes every entity a second time on the UI thread (D6), is gone.
- Stage 10 replaces the whole-library capture with a capture of dirty pages plus the tree skeleton, which also cuts the save itself (section 7.1).

## 7. Algorithms

### 7.1 Write path

1. Trigger (UI thread):
   - SaveNow runs as today: on the debounce timer, at page switch and at close.
   - Audit finding 10 asks for a maximum delay between saves. Stages 6-9 still capture the whole library on the UI thread, which costs an estimated 0.3-0.7 s, so a forced save during continuous inking would freeze the pen. Until stage 10 a pending change is therefore forced into a save only in an input gap (no pointer down and no key for 2 s), and at most once a minute.
   - From stage 10 the forced save runs every 10 s while changes are pending.
2. Capture (UI thread):
   - Surface.FlushTexts.
   - Serialize to a UTF-8 byte array:
     - Stages 6-9: the whole Library as today, plus `Library.Trashed`, serialized separately (see section 7.4).
     - Stage 10: each dirty page, plus the skeleton. The skeleton holds each container's id, parent, list position and fields, including those in `Library.Trashed`, and each page's element counts.
   - Post the capture to the worker.

   In stage 10 a page is dirty when:
   - it is the current page and has raised ContentChanged since its last capture;
   - a tree command or MarkPageDirty marked it;
   - its element counts in the skeleton changed. This catches PDF import, which fills pages 2..N without making them current (MainWindow.xaml.cs:9996-10026).

   The safety nets are an idle full capture at most every 10 minutes, a full capture at close, and a sweep of one non-current page every 2 s while idle.
3. Diff (worker). Split the capture into keys and registers, canonicalize each register value (known fields only), hash it with xxHash64, and compare it with the shadow (what the UI held, section 7.3):
   - A changed register becomes a record with b = the shadow's stamp for that register.
   - A new key becomes a creation (L = 1 with every register).
   - A key present in the shadow, missing from a captured page or list, becomes a delete: L = 0, with `bm` from the shadow.
   - A key that is dead in the state but present in the capture becomes a revive (L = 1 with every register). Only undo and restore can produce this, because paste and duplicate mint new ids.
   - Container life comes from trash marks (section 7.4): L = 2 and `del` for a delete mark, L = 1 for a restore mark, a purge record for a purge mark. A container missing from the skeleton with no mark is written as L = 2, never purged, and logged as an anomaly.
   - List order becomes `pos` or `z` order keys (section 4.4).
   - The diff compares only against keys the UI was actually given. A key held back by the projection (unknown parent) can therefore never be "deleted" by its absence.
4. Stamp (worker): stamps per register as in section 4.2, and contiguous sequence numbers.
5. Journal (worker): append the batch and its end line to the journal segment, then Flush(true). When the flush returns, the edits are acknowledged.
6. Commit (worker): only now join the records into the state, set the shadow for those registers to the captured hashes and stamps, and advance the sequence number.
   - A failed journal write commits nothing. The worker switches to a new writer id (section 5.2), and the next capture produces the same records again, because the shadow did not move.
   - The worker sets SyncWriteError, which the status line repeats every 60 s for as long as it lasts, the way WriteError works today.
7. Publish (worker): append the same bytes to Dir\sync\seg-W-firstSeq.qsd, then Flush(true).
   - On failure (sharing violation, offline placeholder, full disk, missing folder), the batch stays unpublished in the journal. The writer switches ids, and republishing (section 7.2, step 6) retries on every pass.
   - After 5 minutes unpublished, the status line says "Saved on this PC, not yet synced".
8. Rotate segments as in section 5.2.

Close: Closed runs the capture, then waits up to 15 s for the worker to journal it, as LibraryStore.Flush does today, and reports a timeout. The capture goes ahead of any long job in the queue (section 6). The WM_QUERYENDSESSION and WM_ENDSESSION handling from audit finding 10 belongs in the same stage.

### 7.2 Read and merge path (worker)

1. Triggers:
   - a 20 s timer;
   - a FileSystemWatcher event on Dir\sync\ and Dir, debounced 2 s;
   - app activation;
   - startup.
2. Enumerate Dir\sync\ and the legacy files in Dir (oplog.*.jsonl and its variants, library.json, trash.json). For each file whose length, last write time, or hash of its first and last 4 KB differs from the consumed-files table, read it whole. A segment is at most about 1 MB. A checkpoint is read once, because its name is never reused.
   - Open files with FileShare.ReadWrite | FileShare.Delete.
   - A sharing violation, an offline placeholder or an IO error skips the file until the next pass.
   - A file that is shorter than before, or whose prefix changed (a sync client rolled it back or replaced it), is read again in full, which is harmless.
3. Decode and join into a scratch overlay over the state:
   - every complete batch (section 5.2);
   - every verified checkpoint;
   - the records the legacy importer and the watcher produce from the legacy files (sections 9.2 and 9.3).
4. Derive: run the keep-both and rescue rules (section 4.5) on the keys the overlay changed.
5. Commit:
   - Journal the derived records, and the importer's and watcher's records (write-path steps 4 and 5).
   - Only when that succeeds, commit the overlay to the state and record the files as consumed.
   - If the journal write fails, discard the overlay. The same files are read again on the next pass, so no derived record is ever lost behind a consumed mark.
6. Republish own segments: compare every own journal segment with its shared copy and any `-r` copies, by length and SHA-256.
   - Append a missing tail when the shared copy is an exact prefix of the journal segment.
   - If no copy matches and none is a prefix (truncated mid-batch, rolled back, replaced), write the whole journal segment as the next free `seg-<W>-<firstSeq>-r<k>.qsd`. That happens once per divergence, not once per pass.
7. Patch: for every key whose projection differs from the shadow, build a patch entry and post it. This covers keys touched by this pass and keys deferred earlier. Patches are grouped by page, and each entry carries:
   - the key and kind;
   - the operation: upsert, remove, move, trash, restore or purge;
   - the new value, already deserialized into a model object on the worker;
   - the order key;
   - the base hash, which is the shadow's hash for that key.

   There is at most one outstanding entry per key: a newer value replaces an entry still waiting in the UI's queue. After every acknowledgement and every commit, keys still differing from the shadow are patched again, so a deferred change is retried until it lands.
8. Persist:
   - The cache is written every 10 minutes, at close, and after a merge of more than 20 MB, always after the journal writes of step 5.
   - The device file is written every 10 minutes.
   - Consumed-file marks persist only inside the cache, together with the state they produced, so they can never claim more than is on disk. Losing them costs a re-read and nothing else.

### 7.3 Handing a merge to the live model (UI thread)

The shadow is the worker's record, per key and register, of the hash and stamp of what the UI holds. It moves only on a journaled own write (section 7.1, step 6) or on an acknowledged apply. It is never rebuilt from the live model, which is problem 4.

When patches are applied:

- Patches for the current page wait while a pen, eraser, lasso, drag or text-selection gesture is in progress, and are applied at pointer-up. If a patch has waited 30 s, it is applied between two strokes.
- An entry for a text box that has keyboard focus waits until focus leaves it.
- Patches for other pages are applied in slices of at most 5 ms per dispatcher callback.

Applying a patch for the current page happens in one dispatcher callback:

1. Surface.FlushTexts (not releasing), so typed text is in the model before any comparison. That ordering fixes D7.
2. For each entry, find the live object through the page's id map and hash its canonical serialization. That is one element, not the page.
   - Live object present, hash equal to the base: apply. CopyFrom copies the fields onto the existing object, and the object moves to its order position if `z` changed. In-place copying keeps undo actions' object references valid. The actions hold element objects and add, remove and mutate them by reference (for example UndoRedo.cs:63-157 for strokes, :263-276 for texts, :387-410 for mixed selections).
   - Live object present, hash different: defer. The user changed it since the last capture. The next capture writes that change with a stamp above the remote one, so it wins everywhere. If both changed a text body, keep-both makes a copy.
   - Live object missing but the base says present: defer. This is an unsaved local delete, and the next capture writes it.
   - Live object missing and the base says absent: insert. If the session's identity map already holds an object for this id (one an undo action may reference), CopyFrom onto that object and insert it. Otherwise insert the worker-built object.
     - The identity map is a per-session dictionary, kept by InkSurface. It maps the id (or layer key) of every element and layer object that has been in any page list this session to that object, through weak references.
   - Remove: remove if the hash equals the base, else defer.
3. Undo hygiene, for elements and layers alike:
   - If any applied update or removal touches an id referenced by an action on the undo stack, drop that action and everything older. That covers snapshot actions such as RecolourTextsAction, whose Undo would otherwise put an old RTF back over the remote text.
   - If it touches an id referenced by an action on the redo stack, clear the redo stack. Redo after a remote partial erase therefore cannot bring back the original over its fragments.
   - IPageAction gains `IEnumerable<Guid>? ElementIds` (layer actions report layer keys). Null means unknown and is treated as referencing everything.
   - Every Undo and Redo insert path skips an object whose id is already present in its list (D8).
4. Call the new InkSurface.ApplyRemoteChanges(ids). It:
   - invalidates the ink cache, the spatial index and the bitmaps of changed images;
   - rebuilds only the changed text boxes, without flushing (the flush already ran in step 1);
   - prunes the selection of removed ids.

   It does not call LoadPage, and it does not call UndoManager.Clear.
5. Post an acknowledgement to the worker. It lists the applied keys with their new hashes and stamps, and the deferred keys. The worker moves the shadow for applied keys only.

Other pages follow the same rules without the flush, because none of their boxes is live.

Container changes move the same Notebook, Section and NotePage objects between the tree lists and `Library.Trashed` (section 7.4), then BuildTree runs. The open page needs special handling:

- If the open page, or an ancestor, becomes trashed on another device, the page stays open with a banner: "Moved to the trash on <machine>", with Restore and Close. The page object is still reachable through `Library.Trashed` and is captured like any other page, so edits keep saving into the same keys. Nothing is detached, which is problem 8.
- If a purge patch arrives for the open page, the UI first runs a capture of that page, and the worker journals it and re-runs the rescue check. The window switches away only if no rescue applies, to the nearest live sibling or to the gallery, and says why. Unsaved strokes are never dropped by the switch.

Why the posting order is enough:

- A capture taken before a patch was applied reaches the worker before that patch's acknowledgement, and a capture taken after it reaches the worker after.
- A patch computed before a capture landed may carry a base hash that the capture has since replaced. At apply time the live object then differs from the base, the entry is deferred, and the re-patch after the next commit offers the newer value. That is correct.
- A page switch between the post and the apply is harmless. The flush and the InkSurface refresh run only if the patch's page is current at apply time, and SwitchToPage has already saved the outgoing page.

### 7.4 Trash, restore and purge

`Library.Trashed` is a new non-serialized list. It holds the live Notebook, Section and NotePage objects of the topmost trashed containers, each with its `del` data (DeletedTicks, DeletedBy, original parent and index). The trash dialog reads it instead of trash.json, and it is projected from the state like the tree.

- Delete of a notebook, section or page: LibraryStore's Delete* methods move the object into `Library.Trashed`. They post a trash mark {id, kind, DeletedTicks, parent ids, index} to the worker ahead of the next capture. The diff writes L = 2 plus `del` on the container alone.
  - Children are not touched. They follow their own `pos`.
  - trash.json is no longer written by v2. That ends audit finding 16's whole-file trash for v2 builds.
- Restore moves the object back and posts a restore mark. The diff writes L = 1 with b = the trash stamp. If the original parent is not in the tree, it also writes `pos` to the parent's nearest live ancestor, or to the Recovered section, matching today's RestorePage fallback (LibraryStore.cs:1161-1176).
- Delete forever posts a purge mark. The worker writes the purge record: L = 0 on the container alone, with `pb` built from its state. The container leaves `Library.Trashed`, and the rescue rule (section 4.5) protects work the purger had not seen.
  - In the first release a purge hides content but does not reclaim its space. Reclamation is stage 12 (section 13).
- Auto-purge, today's 30-day retention (TrashBin.RetentionDays), runs on a device only when all four of these hold:
  1. This device first saw the container trashed at least RetentionDays ago, by its local clock.
  2. The newest stamp anywhere in the container's subtree is older than RetentionDays.
  3. Every device whose device file changed in the last 60 days, judged by this device's observation of the file's content, lists the container in `trashSeen` with the current trash stamp or a later one.
  4. When other devices exist, the newest stamp seen from them is within one day of this device's clock.

  Rule 3 stops a laptop that wrote on a trashed page while offline from losing that work to a 30-day expiry elsewhere. The rescue rule covers a device silent for more than 60 days.
- Element deletes (erase, delete selection) have no trash today and get none: L = 0, undoable within the session as today.

### 7.5 Checkpoints and deleting files

When a checkpoint is due:

- The segments not listed in any verified checkpoint exceed 32 MB, or 7 days have passed since the newest verified checkpoint and at least one segment is unlisted.
- A device writes one only if its own state has joined every file it lists.
- To avoid a stampede, the device with the smallest D among devices whose file changed in the last 7 days writes at once. Any other device waits a random 0-48 hours and writes only if the condition still holds.

Deleting segments:

- A segment or variant may be deleted when a verified checkpoint lists it with the identical name, length and SHA-256, that checkpoint has been readable locally for 7 days, and, where the Cloud Files placeholder state can be read, the checkpoint is fully in sync.
- Normally the owner device deletes the segments of its own writers. Any device may delete files of writers whose device file has not changed for 90 days.
- Exact-content matching means a rolled-back, truncated or clone-collided file is never deleted on the strength of a different file with the same name.

Deleting checkpoints:

- A checkpoint may be deleted when a newer verified checkpoint lists it in `files` (that is, its writer joined it), and the newer one has been readable for 7 days.
- Normally the owner deletes its own. Checkpoints of devices silent for 180 days may be deleted by anyone under the same rule.
- At least two verified checkpoints are always kept.

Why this is always safe: nothing is collected, and a verified checkpoint holds the joined result of every file it lists. Joining that checkpoint therefore gives the same state as joining those files. A stale copy of a deleted file that reappears later (a rollback, a conflict copy, a re-upload) can only re-join records that are already in the checkpoint. Nothing in the deletion rules depends on clocks.

### 7.6 Startup, joining and reinstall

On the worker, during LibraryStore.BeginLoad:

1. Read device.json. If it is missing, mint D. If device.lock cannot be taken, run as a secondary (section 7.7).
2. Load cache.qsc, or its .bak, or start with an empty state.
3. Replay every local journal segment, and join it. This covers every run of this device, adopted instance directories (section 7.7), and every local directory that names this Dir or its libraryId (section 5.4). This is the fix for D2: a device always re-applies its own acknowledged edits, whatever library.json or the cache says.
4. If the folder is reachable, do one read-and-merge pass over the shared folder (section 7.2, steps 2-6).
5. If no migration marker is in the cache, migrate (section 9.1).
6. Project the state into a new Library on the worker. The UI thread then runs AdoptInPlace, ApplySettings and EnableSaving exactly as today (MainWindow.xaml.cs:571-578). The shadow equals the projection.

A new PC, a reinstall or a wiped profile has no cache and no journal, so it mints a new D and joins:

- It joins every verified checkpoint and every segment in sync\, and imports the legacy files.
- Tombstones are in the checkpoints and segments, and an older upsert can never beat a newer tombstone, so nothing erased comes back (problem 2).
- A cursor that does not exist cannot be lost.

If the files in sync\ are offline placeholders that cannot be read, the device starts from what it can read:

- library.json is a real old library: the migration baseline (section 9.1) supplies the content. Its records are low-priority, and the rest joins as files hydrate. No rule here can resurrect or revert anything, because baseline records lose to every real record.
- library.json is the retirement stub or absent, and sync\ shows any v2 file: the device has nothing to show yet. It says "Waiting for your notes to download from <folder>" and never seeds a library.

A seed library ("My Notebook") is created only when neither a real library.json nor any v2 file exists anywhere in Dir, which is today's genuine first run. When Dir lies inside a cloud-synced folder, seeding first asks: "No notes were found in <folder> yet. Start a new library here, or wait for your files to sync?" A folder counts as cloud-synced when its path is under the OneDrive, OneDriveConsumer or OneDriveCommercial environment paths, or when its files carry cloud placeholder attributes.

### 7.7 Identity, second instances and clones

Each process run has its own writer id: W = D (12 hex, random per installation, kept in device.json) followed by R (8 hex, random per process start, and again after any failed append). Sequence numbers start at 1 for each W. This has three consequences:

- A sequence number is never reused, even after a crash mid-batch, a torn journal tail or a restore from an image. The next run is a new writer.
- Two machines restored from one disk image, or a profile copied to another PC, get different W at their next launch. Disk clones therefore cannot collide.
- A lost device.json costs nothing but a new D.

The device-identity reclaiming, fingerprinting, forks and re-homing from design (a) are not needed.

Second instance. Stage 1 adds the single-instance redirect from audit finding 9 (AppInstance.FindOrRegisterForKey). As defence in depth, a process that cannot take device.lock runs as a secondary writer:

- It creates `instance-<W>\`, holds `instance-<W>\instance.lock` with FileShare.None for its whole life, and starts from a copy of the primary's cache (or its .bak) plus every journal.
- It journals and publishes as usual.
- A primary adopts an instance directory only when it can take that directory's instance.lock, which proves the secondary has exited. Adopting means copying its journal segments into the primary's journal after verifying them, then deleting the directory.

The two windows then merge through the shared folder, like two devices.

Cloned running process. A VM snapshot resumed on two hosts continues one W, and both copies write segments with the same names. The sync client then produces variants.

- Readers join every complete batch of every file, variants included, so no record is lost.
- Deletion requires exact-content coverage, so no file is deleted on the strength of its twin.
- A process that finds a file with its own W whose content differs from its journal switches to a new run id at once. Its records already published stay valid.

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
7. library.json: never written by v2, so there are no whole-file rewrites and no new conflict copies. Settings > Storage lists the existing library.conflict-*.json files with a delete button, and retirement (section 9.6) copies them aside.
8. Open page deleted elsewhere: it is trashed, not detached. It stays open with a banner, edits keep saving, and Restore is offered. Undo survives, because LoadPage is never called for a merge.
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
   - A genuine first run (no library.json and no v2 file) takes today's path: the LectureInk folders, then Seed (with the confirmation of section 7.6). L0 is the result.
2. Import every legacy log in Dir, conflict copies included (section 9.2), and join everything readable in Dir\sync\ (other devices may have migrated already).
3. Import trash.json. For each entry whose container is not in L0:
   - Create the container and its subtree, if the state does not already have them, as baseline records (below).
   - Set L = 2 at stamp (DeletedTicks converted to ms, 0, writer), origin b, with `del` from the entry.

   An entry whose container is in L0 is stale, because that container was restored, so it is ignored.
4. Write the baseline. For each entity in L0 and in the trash entries:
   - If the state has no record for the key: a creation at a baseline stamp (t = 0, c = library.json write time in ms, w = this writer), origin b.
   - If the state has the key, look at each register where L0's value differs from the state's value:
     - If the state's value has origin b (another device's baseline), write L0's value at this device's baseline stamp, origin b. The fresher library.json wins.
     - If the state's value has origin l (an old op), L0 holds an edit the old build made later and never logged: problem 4's absorbed edits, moves, order, and the unlogged fields of D3. Write L0's value at the successor of the replaced stamp, origin x (a baseline exception).
     - If the state's value has origin e, k, r, x or w, write nothing. A v2 edit is never overridden by a baseline.
   - Life is never raised by a baseline. A key that is dead in the state stays dead, which re-erases every element that problems 1, 2 and 9 resurrected into library.json, wherever a delete op survived (old compaction keeps tombstones).
   - A key alive in the state but absent from L0 is left alive: an absence in an old library is ambiguous, and keeping the item is the safe side.
5. Journal the baseline and publish it as ordinary segments (about 70 one-megabyte segments, uploaded once). If sync\ had no v2 files, also write folder.json. Then write the migration marker into the cache.
   - A crash before the marker repeats the migration. That is harmless: the same inputs produce the same records, and duplicates join idempotently.
   - The first checkpoint then covers the baseline segments, and they are deleted like any others.
6. Leave every legacy file untouched: library.json(.bak), trash.json(.bak), oplog.*, backups\, the conflict files, deviceid.txt and synccursors.json. deviceid.txt's value is recorded as legacyId in the device file.

If the device is offline, or sync\ is unreadable, the steps run with what is readable. Baselines and exceptions beat only the specific older values they replace, so records that arrive later win wherever they are real edits.

### 9.2 Importing old op logs

The old logs are read continuously: at migration and on every pass while any old log changes. Each op becomes a record with origin l. The records go into this device's journal and are published, so every device converges on the union of every importer's reading. Interpretations that depend on what a device has read are therefore settled by the union. Each rule below is chosen so that the union is the right answer.

- Identity: an old op is identified by (A, N, Ts, the hash of its line), and its actor is the op's own A field, never the file name. Ops from bridge logs, and ops carrying "Q2", are skipped, because they are v2's own.
- Stamp: (Ts in ms, N, "L" + A).
- Fields written: stage-1 builds (section 12) add two members to every container, text and comment op:
  - "Qf": the names of the fields that op changed;
  - "Qb": the bridge stamp of the value it replaced, when that value came from a bridge op.

  For such ops the importer writes only the Qf fields, at max(the op's stamp, successor of Qb). For ops from older builds, which lack Qf, it writes every field the op carries, at the op's stamp. Section 9.5 lists what that costs.
- Element upsert (st, sh, tx, cm): a write of the element's value registers (v, or lay, body, style or meta, and cell), plus `X` = 1, with no life register. Two refinements:
  - If the key's current life register is 0 and was written by "L" + A at a lower stamp, the old build has undone its own erase. The record then also carries L = 1, a revive.
  - Otherwise an upsert never writes life. An old build that never received a v2 delete, and lasso-moves the deleted ink, therefore cannot bring it back. An element an old build created has no life register at all and is alive through `X` (section 4.4).
  - An element first seen through an old op gets a `z` after its page's last element.
- Element tombstone: L = 0 at its stamp, applied by id, without `bm` (so it never makes a text copy). One exception, the same-batch rule:
  - A batch is the set of A's ops with one Ts. It is decided only once it is complete: a later op from A with another Ts has been read, or the batch has been the tail of A's log for two passes.
  - If the batch holds a container tombstone, skip every element or page tombstone in it that the container's trash.json entry contains. If no entry is found, skip every element or page tombstone in the batch.
  - That old build put the subtree into its trash, and a restore must bring it back whole (D10). An element erased seconds before the page was deleted is absent from the trash entry, so its tombstone stands.
- Container tombstone (nb, sec, pg): L = 2 (trashed), never purged, with `del` from trash.json if an entry exists.
- Container upsert:
  - If the key has no `pos`, write `pos` from the op's Parent (a creation), plus `X` = 1.
  - Write `f.<Property>` for the fields selected above, each as its own register.
  - Never write `pos` for an existing container, which matches the old semantics: Apply never re-parented.
  - Never reset a field the op does not carry. That keeps old meta ops order-independent.
  - Write L = 1 only when the key's current life register is 2 and was written by "L" + A at a lower stamp, which is a restore on that old build. A restore on one old build of a container another old build deleted therefore stays in the trash on v2 builds until someone restores it there. The content is safe, so this is a documented gap rather than a loss.
- Layers in a page op: one `attr` and `z` write per layer present. Absent layers are not deleted, matching the old "adopt, never clear" rule (SyncLog.cs:533-540).
- Generation changes, compaction, conflict copies and repeated N (from two instances or a lost synccursors.json) need no special handling. Every distinct op becomes a deterministic record, and the join dedupes.

### 9.3 Edits that old builds never log

Old builds rewrite library.json at every save, and some of their changes reach no op: moves, reorders, the D3 fields, absorbed edits, and elements created during an absorbed window. While any old log or library.json has changed in the last 30 days, each v2 device runs a library.json watcher on the worker. "Changed" means a content change this device has observed (a different hash), not a write time.

- It keeps per-field hashes of the last library.json version it saw, for these registers: container `pos` and order, `f.<Property>` fields that old ops do not carry, and the set of element and container ids.
- On the first sight of a file it adopts nothing. Section 9.1 already took the baseline.
- On each later version, for each of those registers whose value changed between the two versions:
  - if the state's value has origin l, b, x or w, it writes the new value at the successor of that value's stamp, origin w;
  - if the state's value has origin e, k or r, it writes nothing. A v2 edit always wins over an old build's unlogged change, because the old build never saw the v2 value: the bridge does not carry these registers, so the two changes were concurrent.
- An id that appears in a version is created (with `X` = 1, origin w) only if the state has never seen the key at all: no record and no tombstone. A page the bridge delivered to the old build is therefore never re-imported with the old build's placement.
- "Write library.json for an older version" (section 9.7) resets the watcher's base to the file it wrote, so an export never re-enters as a change.

Residual risk: when two or more old builds are active, library.json alternates between their snapshots. A field that one old build changed and another never saw can then be imported as a change and then a change back. That is the same flip-flop old builds inflict on each other today. Upgrade every device within a short period; the app shows a banner while old builds are active.

### 9.4 The bridge to old builds

While old builds are active, each v2 device appends old-format ops for its own v2 records to a bridge log, so old builds keep receiving element edits.

- File: Dir\oplog.<bridgeId>.jsonl, where bridgeId is the first 12 hex of SHA-256("quill-bridge:" + D).
  - The file is separate from the device's old log, so an old build on the same machine (after a downgrade) reads it as foreign and never shares a file or an N sequence with it.
  - N starts at 1 and continues from the highest N in the file.
  - Every op carries "Q2": the stamp of the v2 record it carries. Stage-1 builds remember, per key, the Q2 of the last bridge op they applied and echo it as "Qb" (section 9.2).
- What it carries:
  - Element upserts (J = the element JSON, Parent = its page).
  - Element tombstones with Parent set, which the old Apply honours.
  - Container upserts in the old meta shape, for creations and for changes to the fields the old shape carries.
- What it never carries: container deletes, moves, trash state or purges. An old build therefore never removes anything outside its own trash on v2's behalf.
- It never echoes. Old builds rebuild their shadow after a merge (SyncLog.cs:461-465); stage-1 builds refresh it for the keys Apply touched. Either way a merged bridge op is never re-emitted, and v2 skips bridge logs and Q2 ops on import.
- Text copies and rescues are ordinary own records, and they are bridged.
- It is compacted like an old log (latest op per key, tombstones kept, generation header), with containers written before their children (D5).
- It runs while any non-bridge old log or library.json has changed in the last 30 days. When it resumes after a pause, it first re-emits the latest value of every key changed during the pause.

### 9.5 Mixed period: what is guaranteed, and what is not

Guaranteed:

- Nothing an old build logs is lost on v2. It arrives through the importer.
- An old build's unlogged changes reach v2 through the baseline (at its upgrade) and the watcher (while it runs), unless they conflict with a concurrent v2 edit of the same register. The v2 edit then wins.
- Nothing deleted on v2 comes back through an old build's upsert. Deleted ink can come back only as new fragments: an old build that has not yet received the delete partially erases through it. Fragments are new elements.
- Nothing v2 does removes anything on an old build outside that build's trash.
- Old builds see v2's element edits, new pages and renames. They do not see v2's moves, reorders, trash or purges.

Not guaranteed, with builds older than stage 1:

- An old op re-sends every field of a page or text box it touched. A stale old build (offline, or before its first merge after waking) can therefore overwrite concurrent v2 edits to other fields of that page or box. That includes typed text, which is not kept as a copy because legacy records carry no base stamp.
- An old build whose clock is slow loses its own causally later edit of a value it received through the bridge.
- Old builds keep their own bugs among themselves, for example absorbing each other's edits (problem 4).

Stage-1 builds remove the first two, because they send only changed fields and echo the bridge stamp, and they no longer absorb edits. That is why stage 1 ships first, and why the app asks the user to update every device while it sees pre-stage-1 activity.

### 9.6 Retiring the legacy files

Retirement runs when both hold:

- Every old actor id seen in any old log (by op.A) is the legacyId of a v2 device, or the user has confirmed in Settings that no older version is in use.
- No old log or library.json has changed (by observed content) for 30 days.

Then:

1. Copy (do not move) library.json, its .bak, trash.json, its .bak, the old logs, library.conflict-*.json and library.pending.json into Dir\legacy\<date>\, flushing each copy.
2. Replace library.json.bak with a stub by File.Replace, with a backup name inside legacy\. The stub is `{"Notebooks":"This library was upgraded to Quill sync v2. Update Quill to open it."}`.
3. Wait until the stub .bak reports in sync (Cloud Files state) or 24 hours have passed, so other machines receive it before the next step.
4. Replace library.json with the stub the same way.

The order matters:
   - At no instant is library.json missing. Otherwise an old build would fall back to the LectureInk folders or seed a new library (LibraryStore.cs:301-350).
   - No machine sees a stub library.json beside a real .bak, from which an old build would load and then save over the stub (LibraryStore.cs:317-319).

What happens afterwards:

- An old build fails closed. The stub throws on deserialize, the .bak stub does too, and anySource is true, so LoadFailed is set and the save gate stays shut (LibraryStore.cs:337-347).
- Old logs stay where they are and are still imported, so an old laptop that returns after months still delivers its ops. If it uploads a real library.json over the stub, the watcher resumes from the last real version it saw, and retirement repeats after another quiet period.
- Settings > Storage offers deleting legacy\ and the old conflict copies 30 days later.

### 9.7 Downgrade

- Before retirement, an old build on the same machine opens the last library.json the old build wrote (v2 never writes it). It replays every foreign log from its cursors, which includes this device's bridge log, so it catches up on elements and the old meta fields. Its own edits flow back through its old log.
- After retirement it fails closed on the stub.
- Settings > Storage has "Write library.json for an older version", which exports the current state on demand and resets the watcher base.

## 10. Failure handling

### 10.1 Crash or power loss on the write path

NTFS journals metadata but not data. A rename or a larger end-of-file can survive a power cut while unflushed data reads back as zeros, and a rename that has returned can still roll back if the log was not flushed. This design therefore acknowledges on a flushed append, not on a rename.

- During capture or diff: nothing is acknowledged. The window of lost work is the time since the last save, as today.
- During the journal append, before Flush(true) returns: a torn or zero-filled tail. The batch has no valid end line, so it is ignored, never acknowledged and never published. The next run, or the same run after its id switch, is a new writer, so no sequence number is reused.
- After the journal flush, before commit: the record is durable. Startup replays it from the journal, and the state, the shadow and the sequence are rebuilt from the journal.
- During or after the shared append: the republish step (section 7.2, step 6) restores the shared copy from the journal on the next pass. A torn shared tail never hides anything, because readers skip incomplete batches.
- During the cache write: temp, flush, File.Replace with .bak. An older cache plus journal replay plus a shared pass gives the same state, because every record that cache lacks is still in a journal or a shared file.
- During a device-file write: same pattern. A stale `merged` or `trashSeen` only delays auto-purge.

### 10.2 Crash on the merge path

- During read, join or derive: the overlay is in memory only, and nothing is marked consumed. The files are read again on the next start.
- During the commit: derived, imported and watcher records are journaled before the overlay commits. A crash after the journal flush leaves durable own records, which replay. A crash before it leaves nothing.
- Between posting a patch and the UI applying it: the patch is lost with the process and recomputed from the state at the next start.
- During the UI apply: the model is in memory only. The cache on disk describes an older shadow, and the next start recomputes.

### 10.3 Checkpoints, republishing and deletes

- A temp checkpoint that is partly written or zero-filled is ignored. A renamed checkpoint whose data did not survive fails its trailer check and is never used.
- A crash during a republish leaves either nothing or an `-r` copy that is a prefix of the journal segment, which the next pass completes.
- A delete of a segment, checkpoint, variant or journal segment either happened or did not. Both states are valid, because every deleted file's content is covered by exact-content listing in a verified checkpoint that has been readable for 7 days.

### 10.4 Migration, instances, bridge and retirement

- Migration is repeatable until its marker lands. Every step produces deterministic records, and the baseline is journaled before the marker.
- Instance adoption copies and verifies the segments before deleting the instance directory. A crash in between leaves duplicates, which join idempotently.
- The bridge is an old-format append. A torn last line costs an old build one op (the old reader's existing weakness). The bridge's next compaction rewrites the file through temp, flush and File.Replace, exactly as old compaction does.
- Retirement copies before it replaces, and replaces with backups, so every step can be retried. The wait between the two stubs makes the order hold on every machine, not only on the one retiring.

### 10.5 Sync-client behaviour

- Late or out of order: the join commutes. Held keys wait for their parents, and a moved item stays put until its new parent arrives.
- Duplicated or re-uploaded: the join is idempotent.
- Conflict copies: variants are joined, then deleted only under exact-content coverage.
- Partial or growing files (Dropbox, Syncthing, network shares): only complete batches with valid CRCs are read.
- A sync client deleting a file, or a delete that beats an append: no record is lost while any device holds it. The owner republishes from its journal, and any device's checkpoint holds the joined state.
- Version rollback of a file, or OneDrive's "restore your OneDrive" rolling the whole folder back: devices republish their journals on the next pass, and anything their checkpoints cover comes back through those checkpoints.
- Files-On-Demand placeholders:
  - Unreadable placeholders are skipped this pass.
  - v2 asks the user to keep Dir\sync\ "always on this device" (pinning; see section 15). Acknowledgement never depends on hydration, because the journal is local.
  - A placeholder is never treated as a deletion.
- Sharing violations: skipped and retried on the worker, never on the UI thread.

### 10.6 Clocks

- Skew of minutes or hours: the per-key bump keeps causality. Skew only decides genuinely concurrent conflicts, in favour of the faster clock by the size of its lead.
- A clock days ahead: its stamps are joined but not adopted into other clocks, so it drags no one forward, and a warning names it.
- A clock that jumps backwards, or a corrected fast clock: l restarts at now, and the per-key bump keeps every causal edit winning. Nothing stalls, because nothing depends on wall time except the auto-purge guard, which refuses to run while this device's clock disagrees with the others' by more than a day.
- Migration and the watcher never stamp by observed time (section 4.2), so a slow or fast clock on the importing device changes nothing there.

### 10.7 A device offline for a month, or a year

- It comes back with its own journal, which it publishes. Its edits join by stamp. Deletes made elsewhere beat its edits to the same elements.
- Its offline text edits are kept as copies wherever they conflict.
- Pages trashed elsewhere stay trashed, with its edits inside them. Auto-purge waited 60 days for its acknowledgement. If the page was purged anyway, the rescue rule brings it back the moment its records join.
- Nothing it holds can resurrect anything, because every tombstone still exists.

### 10.8 Restores, wiped profiles and second instances

- A machine restored from an image: a new run id, an old cache and old journal segments. All of it joins idempotently with the newer shared state. Its journal may republish batches that are already published, which is harmless.
- A wiped profile: a new D. It joins as a new device. Unpublished journal batches are lost only if the shared folder was unwritable when they were written, and the status line was saying so.
- Second instance: section 7.7.

### 10.9 Disk full, read-only folder, missing folder

- Journal write fails: SyncWriteError, no acknowledgement, a writer id switch, and a retry at the next capture. The warning repeats in the status line.
- Shared folder unwritable or missing: edits are acknowledged locally, publishing is retried, and the status line says "not yet synced".
- A data folder that is missing at launch (audit finding 12) is out of scope here. This design never seeds a library into it without the checks of section 7.6, because the state comes from the local cache and journal.

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
- Stage 1. Hotfixes in today's op-log format. They also make old builds good neighbours during the mixed period:
  - tombstones carry Parent, from a key-to-parent map kept beside the shadow; Apply finds an element by id through an id-to-page index (built once per merge pass, never a library scan per op) when Parent is missing;
  - a strict log-name pattern, so conflict copies are reported and ignored;
  - append, Flush(true), then advance the shadow and counter, and never swallow a failure (report it through WriteError);
  - the cursor stops at the last complete line;
  - the Lamport counter is recovered from the highest N in the device's own log;
  - the shadow is refreshed only for keys Apply touched;
  - flush texts before the merge, not after (D7);
  - after a merge, if `_curPage` is no longer in the tree, switch away at once with a message, before any further edit;
  - container, text and comment ops carry "Qf", and ops replacing a bridged value carry "Qb" (section 9.2);
  - the single-instance redirect.

  Gate: AT-01, AT-01b, AT-02 (without its move variant), AT-04, AT-06, AT-09, AT-10 and AT-11, in their legacy variants (section 14.3).
- Stage 2. The v2 core, in pure .NET with no UI:
  - stamps and the clock;
  - the record codec;
  - registers, the join (with dead values) and the projection;
  - order keys;
  - the register table with its reflection test;
  - CopyFrom per model type with a reflection test.

  Gate: the join property tests and the reflection tests.
- Stage 3. Storage:
  - a file-system interface (so the harness can substitute SimFs);
  - journal, publish, republish and segment readers;
  - the consumed-files table and the overlay commit;
  - cache, checkpoints and the deletion rules.

  Gate: AT-08 (storage crash points), AT-09, AT-10, AT-19, AT-20 and AT-24 at engine level.
- Stage 4. The legacy importer, migration and the bridge, run against LegacyDevice and Stage1Device in the harness. Gate: AT-15, AT-16, AT-17, AT-25, AT-26 and AT-27.
- Stage 5. Dark launch in the app, behind a flag:
  - v2 migrates, journals and merges into its own private state;
  - it publishes only to %LOCALAPPDATA%, never to Dir\sync\, so nothing derived from the still-authoritative old model can reach another device;
  - library.json and the old log stay authoritative;
  - after each save, a debug check compares v2's projection with the live library and logs every difference;
  - the UI and worker costs and the state's memory are measured.

  Gate: two weeks on the owner's machines with no unexplained difference. Stage 6 discards the dark state and migrates afresh.
- Stage 6. v2 owns load and save on one device:
  - load from cache and journal;
  - save through the journal;
  - library.json no longer written;
  - `Library.Trashed`, trash marks and the trash view from the state;
  - foreign files joined at startup only, with no live hand-off yet;
  - the bridge on;
  - SetDataFolder rewritten (section 11).

  Gate: AT-03, AT-04 and AT-05 with merges at launch.
- Stage 7. The live hand-off:
  - patches, compare-and-set, deferral and re-patching;
  - ApplyRemoteChanges, the identity map and undo hygiene;
  - the trashed-page banner.

  Gate: AT-06, AT-11, AT-12, AT-14, AT-18 and AT-29.
- Stage 8. Keep-both, the rescue rule and the guarded auto-purge. Gate: AT-07, AT-13, AT-21 and AT-28.
- Stage 9. The library.json watcher and the mixed-period banner. Gate: AT-17 extended, and AT-25.
- Stage 10. Dirty-page capture, which removes the whole-library serialize from the save:
  - element counts in the skeleton;
  - MarkPageDirty at OCR and import;
  - the idle sweep;
  - the full capture at idle and at close;
  - the 10 s maximum-delay save.

  Gate: AT-18 with the stage-10 budget, and AT-22.
- Stage 11. Legacy retirement, the stubs, and the cleanup UI for conflict copies and legacy\. Gate: AT-23.
- Stage 12. Reclamation, before the retained content becomes a cost (section 13): drop the content of purged subtrees and dead texts, and then tombstones, under causal stability. It must follow the rules in section 13. Gate: AT-13, AT-19 and AT-21 with reclamation on, plus mutations M24 to M27 re-run with it.

## 13. Costs

All figures are estimates. Nothing was measured, and stage 5 measures them.

- UI thread per save:
  - Stages 6-9: today's whole-library serialize and nothing more. Sync work moved off the UI thread entirely; today's OnSaved diff on the UI thread is gone.
  - Stage 10: about 5-10 ms per MB of dirty page, plus about 1 ms per 1,000 pages for the skeleton.
- UI thread per merge: hashing each touched element of a page and applying it in place, in slices of at most 5 ms for pages other than the current one.
- Worker per save:
  - Stages 6-9: parsing the 50-70 MB capture (roughly 0.3-0.6 s), hashing the registers, then one small journal append with a flush and one small shared append with a flush. Coalescing keeps the worker from falling behind.
  - Stage 10: proportional to the dirty pages.
- Memory: the worker's state holds register values as UTF-8 plus about 40 bytes per register. Expect 80-130 MB beyond today's model. This is the largest new cost, and stage 5 must measure it.
- Disk in the shared folder:
  - one or two checkpoints per device, each an estimated 15-25 MB gzip;
  - at most 32 MB of uncovered segments, plus 7 days of grace;
  - the one-time baseline, about 70 MB of segments per migrating device, until the first checkpoint covers it.
- Retained content in the first release, which only stage 12 reclaims:
  - tombstones, at about 40 bytes each. A heavy user erasing 100,000 elements a year adds about 4 MB a year;
  - values on dead strokes and shapes that a concurrent edit wrote after the delete (rare);
  - dead texts and comments, whole;
  - purged subtrees, whole. Emptying the trash hides content but frees no space until stage 12.
- Disk locally: the cache (about the library's size, plus its .bak), journals of a few MB, and 12 gzip backups.
- Upload per save: one segment of up to 1 MB re-uploaded, against 50-70 MB today.
- Startup: about today's library.json parse, on a worker. A first join on a new device downloads the checkpoints and segments and parses about one to three times the library.

Nothing is collected in the first release. Every serious failure the skeptics found in designs (a) and (b), and the catastrophic one in (c), sat in collection machinery:

- for (a): collection racing late segments, restored snapshots and permanent legacy logs;
- for (b): a purged subtree collected by ancestry, re-joins after expiry, and expiry by wall clock;
- for (c): floors raised from an incomplete view.

Stage 12 must follow these rules:

- Content (values) of a purged subtree or a dead text may be dropped only when the purge or delete is causally stable:
  - every device whose file changed in the last 90 days, by the observer's monitoring of content, publishes a `merged` map covering it;
  - that device published every own batch it wrote before that merge;
  - the dropping device has joined every such batch.
- A tombstone may be dropped only when it is older than 180 days and causally stable in the same sense.
- A record for a key absent from the state is then dropped only if both hold: its stamp is at or below the published collection floor, and every device has acknowledged its writer and sequence. The stamp test protects late copies from a cloned process. The sequence test protects a returning device's unsynced edits.
- A device whose cache predates the floor must rebuild from a checkpoint plus its own journal before publishing anything.
- Nothing is dropped by ancestry alone.
- Collection pauses while old logs are active.

## 14. Test plan

### 14.1 The harness

tools/SyncSim is a headless console project in the pattern of tools/LayerRoundTrip. It compiles the real model files and the real sync core with <Compile Include>, plus a shim, and uses no WinUI. The sync core therefore talks to a small file-system interface rather than to System.IO directly.

The simulated world:

- SimFs: an in-memory file system per device with explicit durability.
  - Written bytes stay volatile until Flush(true).
  - A crash discards the volatile layer, optionally keeping a random prefix of an unflushed append (a torn write) or zero-filling it. It optionally keeps or rolls back a rename whose log was not flushed.
  - It supports File.Replace, Move, Delete, append, enumerate and locks.
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
- No lost acknowledged edit: every acknowledged edit is present unless an edit ranked later by section 4 replaced it. A concurrent text body survives as a copy, except where section 9.5 lists a pre-stage-1 loss.
- No resurrection: no element or container is live after its last acknowledged delete, unless a later acknowledged undo, restore or rescue brought it back.
- Trash completeness: every container deleted on any device is live (restored), in the trash on every device, or purged by an explicit purge or by a guarded auto-purge.
- Integrity: no duplicate ids in any list; every live page is reachable; every element's page exists or is held.

It also checks invariants after every simulated step: a kill and restart at that point always loads, and the open page is reachable or flagged as trashed.

### 14.3 Acceptance tests

Each test is a fixed-seed scenario. A randomized soak runs the same oracle over thousands of seeds per profile. A legacy variant runs the same scenario on Stage1Devices instead of v2 devices, and is used as the stage 1 gate.

- AT-01 ElementDeletePropagates: A draws S and syncs, then erases S; B ends without S.
- AT-01b DeleteBeforeCreate: B receives A's erase of S before S's creation. Legacy variant: a parentless old tombstone that arrives before its page.
- AT-02 PartialEraseNoDoubleInk: A partially erases S into F1 and F2; B ends with F1 and F2 and without S. Variants:
  - B concurrently moves S while offline; S stays erased.
  - A erases S and undoes the erase, while B, offline, moves S. Every delivery order ends with S alive at B's position on every device.
- AT-03 FreshDeviceJoin: A draws and erases 200 strokes over a day; new device C joins with empty local state and ends equal to A. C's first save changes nothing on A.
- AT-04 ReinstallNoResurrection: the audit's single-PC test. A wipes its local app-data and restarts. The legacy variant deletes deviceid.txt, its .bak and synccursors.json.
- AT-05 MoveThenContainerDelete: A moves P from S1 to S2, and B, not yet seeing the move, deletes S1. Both end with P live in S2, and S1 with its remaining pages in the trash.
- AT-06 UnsavedEditSurvivesMerge: A types in box T, unsaved, with T reached but not focused. Meanwhile B edits T's text and A's merge lands mid-edit; then A saves. Both texts survive on every device (the original and a copy). The legacy variant checks only A's text.
- AT-07 ConcurrentEditsConverge: A and B edit the same stroke, shape, text body, text position and page name while offline. All devices agree, the text body loser is a copy, and the position and body changes both survive. Variant: A moves T and then deletes it, while B types in T; B's text survives as a copy.
- AT-08 CrashEveryStep: kill and restart at every named crash point of sections 10.1 to 10.4, in both crash modes (torn and zero-filled). The points are enumerated, not sampled, and include:
  - migration, before and after the baseline journal and the marker;
  - republish;
  - the overlay commit, with derived records;
  - instance adoption;
  - journal deletion;
  - cache write against consumed marks;
  - bridge compaction;
  - each retirement step.
- AT-09 TornTail: a device dies mid-append. Peers read the torn file repeatedly while it is torn and after the device restarts. Nothing acknowledged is lost, and nothing unacknowledged appears.
- AT-10 ConflictCopiesEverywhere: each profile's conflict naming applied to every sync file type and to old logs. Nothing is lost, nothing resurrects, and variants are deleted only under exact coverage.
- AT-11 PeerTrashesOpenPage: B trashes the page open on A while A draws. A's later strokes are saved and reach B inside the trashed page, A sees the banner, and Restore on A brings back everything. Legacy variant: the open page is deleted by a peer; A switches away at once, and no edit goes to a detached page.
- AT-12 UndoSurvivesMerge: A draws three strokes, a merge from B touches the page, and A undoes three times. Exactly A's three strokes go. Variants:
  - A remote partial erase of A's stroke, then undo and redo on A: no doubled ink, and no duplicate id.
  - B revives an id that A erased (B undoes its own earlier erase), the merge inserts it on A, and A then undoes its own erase: one element, not two.
  - A recolours box T, B's typing in T is applied in place on A, and A presses undo: B's text survives.
- AT-13 MonthOffline: C is offline for 30 days while A and B erase, move, trash and purge, and C edits pages that A trashes. When C returns, nothing resurrects, C's edits survive (inside the trash, rescued, or as copies), and auto-purge waited.
- AT-14 ClockSkew: B runs 3 days fast, then is corrected; A's clock jumps back a year. Causally later edits always win, nothing stalls, and the warnings fire.
- AT-15 ClonedProcess: A's running process is cloned to D, and both edit. Both devices' edits survive, no file is deleted on a twin's coverage, and both clones switch run ids.
- AT-16 MigrationFromLegacy: fixtures produced by LegacyDevice, including:
  - parentless tombstones and compactions;
  - absorbed edits and unlogged moves;
  - a lost synccursors.json, and two instances reusing N;
  - conflict copies of old logs;
  - trash.json entries, and old-build container deletes (D10).

  First launch of v2 on each device loses nothing, re-erases resurrected items, and restores whole containers from the trash.
- AT-17 MixedBuilds: two LegacyDevices, one Stage1Device and two v2 devices share a folder for 14 simulated days, with edits, erases, moves and deletes on all of them. Then the old devices upgrade, one of them 60 days late. The oracle passes, with exactly the losses section 9.5 lists for LegacyDevices and none for the Stage1Device.
- AT-18 UiThreadBudget: with a generated 60 MB library, the harness counts what the UI-side code serializes and hashes per save, per forced save and per merge, during 10 minutes of continuous inking, and asserts the budget for the current stage.
- AT-19 RollbackAndRestore: OneDrive's whole-folder rollback to a date three days back, and separately a device restored from a 4-month-old image. Nothing is lost or resurrected.
- AT-20 ReadersNeverStall: a sealed segment rolled back to a shorter version, a sharing violation at startup, and a garbled middle region. Every complete batch is read, and nothing waits.
- AT-21 PurgeRescue: A purges a container while B, offline, restores it and edits inside it. B's work survives on every device as a revived container.
- AT-22 DirtyCaptureCompleteness: PDF import of 20 pages, OCR on non-current pages, and table heal on load, each followed by a kill at a random point. Nothing is missing after restart.
- AT-23 Retirement: retirement with an old build that returns after 90 days, and an old build started on a second machine between the two stub steps. Nothing is stranded, an old build launched afterwards fails closed, and no instant leaves Dir without library.json.
- AT-24 DiskFull: the journal append fails for 10 minutes. Nothing commits, the warning repeats, and the writer id switches. Once space returns, everything is journaled exactly once.
- AT-25 WatcherStamps: four cases while an old build is active:
  - a v2-created page bridged to the old build and then moved on v2;
  - an old-build Paper change and a later v2 Paper change, seen late by a third device;
  - a migration from a stale library.json after another device's baseline;
  - an export for an older version.

  No v2 edit is reverted, and the export does not re-enter.
- AT-26 StaleOldBuildWakes: an old build offline for 3 days wakes and renames a page and nudges a text box before its first merge, while v2 had changed that page's background and that box's text. With a Stage1Device nothing v2 wrote is lost. With a LegacyDevice the losses match section 9.5.
- AT-27 LibraryIdRace: two devices migrate on the same day, one offline with unpublished journal batches. After both folder.json copies meet, no journal is orphaned and nothing is lost.
- AT-28 PurgeRescueRegisters: page P is trashed. E, with P open under the banner, partially erases a stroke and moves a box, and its upload is slow. A purges P. C, offline, had drawn on P. After every delivery order there is no doubled ink, the move stands, and C's stroke is present.
- AT-29 DeferredTwice: a peer moves stroke S twice while A is inking. After pointer-up, A shows the second position, and A's next unrelated edit changes nothing about S.

### 14.4 Planted mutations

The harness builds each mutation and fails if every acceptance test stays green. Each mutation names the tests that must turn red.

- M01: element delete records require a page lookup (drop the record when the page is unknown). AT-01b, AT-16.
- M02: commit the state, shadow or sequence before the journal flush. AT-08.
- M03: swallow a journal append failure and commit anyway. AT-24.
- M04: rebuild the shadow from the live projection after a merge. AT-06.
- M05: join by arrival order instead of stamp order. AT-07, AT-10.
- M06: treat a variant as a different writer and skip it, or derive identity from file names. AT-10, AT-15.
- M07: stop reading a file at the first bad line. AT-20.
- M08: skip journal replay at startup. AT-04, AT-08, AT-19.
- M09: let a field write revive a dead element. AT-02 (first variant), AT-17.
- M10: stamp a local write with Next() only, without the per-key bump. AT-14.
- M11: reload the open page through LoadPage on merge. AT-12.
- M12: apply a container delete by tombstoning its subtree. AT-05, AT-11.
- M13: remove the Flush(true) on the journal append. AT-08 in the power-loss mode.
- M14: reuse the writer id across process runs or after a failed append. AT-08, AT-15, AT-24.
- M15: delete a segment on sequence coverage instead of exact-content coverage. AT-15, AT-19.
- M16: apply old child tombstones from a container-delete batch. AT-16.
- M17: let a baseline override a register with origin e, or raise life. AT-16, AT-17.
- M18: apply compare-and-set before flushing texts. AT-06.
- M19: insert on undo without the id check. AT-12 (third variant).
- M20: auto-purge without the `trashSeen` guard. AT-13.
- M21: bridge a container delete. AT-17 (old devices lose a page outside their trash).
- M22: let a v2 write clear or skip `X`, so liveness depends on the legacy value register. AT-17.
- M23: drop dead values regardless of their stamps. AT-02 (second variant).
- M24: rescue by rewriting every descendant register at fresh stamps. AT-28.
- M25: stamp watcher and baseline-exception records with the observed time. AT-25.
- M26: give a delete a single base stamp, the maximum over its registers. AT-07 (variant).
- M27: do not re-patch deferred keys. AT-29.
- M28: commit the merge overlay before journaling its derived records. AT-08 (overlay commit points).

### 14.5 Property tests

- The join is commutative, associative and idempotent over random record sets, including equal stamps with different values and the dead-value rule.
- The projection is a pure function of the state.
- After any delivery order and a full exchange, the set of keep-both copies and rescued containers is the same on every device.
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
  - how reads of placeholders fail offline;
  - how reliably "is this folder cloud-synced" can be detected for the seeding question (section 7.6).
- FileSystemWatcher reliability on OneDrive folders (missed or duplicated events). The 20 s timer is the fallback.
- Whether AppInstance.FindOrRegisterForKey redirection works in this unpackaged app (Quill.csproj:15, WindowsPackageType None).
- Real costs, all estimated in section 13:
  - serializing 50-70 MB on the UI thread, which decides how cautious the stage 6-9 forced-save rule must be;
  - parsing it with Utf8JsonReader on a worker;
  - hashing per register;
  - the memory of the state;
  - gzip of a checkpoint;
  - startup projection time;
  - large-object-heap collections caused by the captures.
- RichEdit behaviour:
  - whether rebuilding one text box without a flush, right after a flush, loses nothing;
  - whether an uncommitted IME composition is in the flushed text;
  - how TextFlushPolicy's latches behave across ApplyRemoteChanges. The final review traced that BuildTextUi clears them per box, but only a run can confirm it.
- How the dispatcher behaves when patches are applied in 5 ms slices during inking, and whether applying a waiting patch between two strokes after 30 s is invisible to the user.
- File.Replace semantics on OneDrive folders for the cache, the device file and the stubs, and how often ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 occurs there.
- Windows time behaviour: how far clocks drift between NTP corrections on typical laptops, and whether DateTime.UtcNow ever steps backwards in practice.

## 16. Final adversarial review and what changed

A final reviewer attacked the synthesized design and found 17 problems. All are fixed above. They are recorded here so they are not re-raised.

- F1, divergence. Dropping all content of a dead stroke broke the join: after an erase, an undo and a concurrent offline move, devices settled on different positions depending on delivery order. Fixed: only values stamped below the delete are dropped (section 4.3). Test: AT-02 second variant, M23.
- F2, resurrection and loss. A rescue rewrote every descendant at fresh stamps, which brought back a concurrently erased stroke beside its fragments and reverted concurrent moves. Fixed: a purge writes only the container's life, with `pb`, and a rescue revives only the container (sections 4.3-4.5, 7.4). Test: AT-28, M24.
- F3, loss of typed text. A delete's base stamp was the maximum over all registers, so a move followed by a delete hid concurrent typing. Fixed: per-register `bm` (section 4.3). Test: AT-07 variant, M26.
- F4, loss of v2 edits. Watcher records and baseline exceptions were stamped with the time a change was noticed, so they reverted later v2 edits (moved bridged pages, late observers, stale migrations, exports). Fixed: origin tags, and replacement stamps that beat only the value they replace (sections 4.2, 9.1, 9.3). Test: AT-25, M25.
- F5, loss in the mixed period. Old ops re-send every field, so a stale old build overwrote concurrent v2 edits, and a slow old clock lost causally later edits. Fixed for stage-1 builds with Qf and Qb (sections 9.2, 9.4). Documented as a remaining loss for older builds (section 9.5). Test: AT-26.
- F6, loss of edits on the open trashed page. Trashed containers had no place in the live model, and the trash mark was never defined. Fixed: `Library.Trashed`, explicit trash, restore and purge marks, and a capture before any purge-driven switch away (sections 7.1, 7.3, 7.4).
- F7, read-order dependence. The same-batch rule and the old-build undo rule depended on which log a device read first. Fixed: batches are decided only once complete, the trash.json entry decides, and revive depends on the state's life writer (section 9.2).
- F8, orphaned journals. Keying local state by libraryId could orphan a journal when two devices raced to create folder.json, and a fresh PC could seed a library while OneDrive was still downloading. Fixed: local state is keyed by the folder path, every matching directory is replayed, and seeding in a cloud folder asks first (sections 5.4, 7.6). Test: AT-27.
- F9, undo reverting remote edits. Snapshot undo actions (RecolourTextsAction) reverted remote text applied in place, and layers had no protection. Fixed: undo hygiene covers in-place updates as well as removals, for elements and layers (section 7.3). Test: AT-12 fourth variant.
- F10, a stale UI. A second patch for a key was deferred and never retried. Fixed: one outstanding entry per key, and re-patching after every acknowledgement and commit (section 7.2). Test: AT-29, M27.
- F11, narrow losses around storage:
  - the first baseline written only as a checkpoint;
  - checkpoint deletion by superset;
  - derived records lost behind consumed marks;
  - sequence reuse after a failed append;
  - inexact journal deletion;
  - startup-only republishing.

  Fixed in sections 5.2-5.4, 7.2, 7.5 and 9.1. Test: AT-08, M28.
- F12, transition resurrection. The dark launch published records derived from the old, buggy model. Fixed: stage 5 never publishes to the shared folder (section 12).
- F13, narrow loss. A restarted primary could delete a live secondary's journal. Fixed: instance locks (section 7.7).
- F14, revert. The stub order held only on the retiring machine. Fixed: wait for the stub .bak to sync before stubbing library.json (section 9.6). Test: AT-23.
- F15, cost and stalls:
  - the forced 10 s save would freeze inking while the capture is still whole-library;
  - large captures churned the large-object heap;
  - the close capture could queue behind long jobs;
  - per-run writer maps grew forever;
  - stage 1 scanned the library per tombstone.

  Fixed in sections 5.1, 6, 7.1 and 12.
- F16, tests that did not test their rule. Fixed:
  - AT-01b, the strengthened AT-06, new AT-12 variants and AT-24;
  - the stage 1 gate restricted to tests its format can pass;
  - the AT-08 enumeration completed;
  - mutation targets corrected.
- F17, rules promised but unspecified:
  - `pg` on deletes;
  - rescue on late arrivals;
  - the Recovered notebook;
  - Unfiled as projection only;
  - the auto-purge acknowledgement (`trashSeen`);
  - a secondary's starting state;
  - the meaning of "changed in 30 days".

  All are now specified.

The reviewer also checked these, and they hold:

- problems 1, 2, 3, 6 and 9;
- the clock rules;
- keep-both convergence, including three-way races;
- flush-then-compare against the real TextFlushPolicy latches;
- the bridge;
- the `X` register;
- the stub failing closed.

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
- Per-element version vectors. Rejected in favour of stamps plus per-register base stamps. The only places that need concurrency detection are text bodies and purges, and base stamps are enough there.
- Delete-loses for ink: keeping a concurrently edited stroke alive. Rejected: it reintroduces doubled ink after a partial erase. Text and comments get keep-both instead, so typing is never lost.
- Dropping all content of dead entities at once, and purges that tombstone every descendant. Both looked cheaper, and both broke the join (section 16, F1 and F2). Reclaiming space waits for causal stability in stage 12.

## 18. Decisions for the owner

- Deletes beat concurrent edits for ink and shapes. The alternative is doubled ink, so this is the recommendation, but it is a policy choice.
- Emptying the trash hides content immediately but frees space only once stage 12 ships. The alternative, reclaiming at once, cannot be made safe without the causal-stability machinery.
- Rolling backups move to %LOCALAPPDATA%. Keeping them in the synced folder costs about 12 x 70 MB per device in OneDrive.
- Library properties other than settings become device-local: Recents, calculator state, dial choices.
- Retirement of the legacy files is automatic once every old installation has upgraded; the alternative is user confirmation only.
- Stage 1 ships hotfixes to the old format before v2. The recommendation is yes: it cuts the damage of finding 6 now, makes old builds safe neighbours during the mixed period, and removes the losses section 9.5 lists for older builds.
