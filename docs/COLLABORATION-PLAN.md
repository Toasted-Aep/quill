# Quill — real-time co-editing & comments: implementation plan

Status: **plan only, nothing implemented.** This documents how collaboration
could land in Quill without rewriting the app, and what each stage costs.

## Ground truth today

- The entire library is one JSON document (`Documents\Quill\library.json`),
  written whole on a debounce. Pages hold three flat lists: `Strokes`,
  `Shapes`, `Texts` — every element already has a stable `Guid Id` and
  `CreatedTicks`. That is a genuinely good starting point for sync.
- All mutation flows through `UndoRedoManager.Push(IPageAction)` — ~26 action
  types with `Do`/`Undo`. This is the natural interception point for
  generating operations to sync.
- There is no networking, no accounts, no server component.

## Recommended architecture (staged)

### Stage 0 — operation log (offline groundwork, no server)
Make every `IPageAction` serializable (`ToOp()` / `FromOp()`): a tagged JSON
record like `{type:"AddStroke", page:<guid>, stroke:{...}, actor:<deviceId>,
lamport:<n>}`. Persist a rolling `oplog.jsonl` next to the library.
Value even without collaboration: crash recovery and a real change history.
Effort: M. This stage de-risks everything after it.

### Stage 1 — async share (CRDT-free, file-based)
Two devices sharing a synced folder (OneDrive/Dropbox — the app already
supports pointing storage at one). Each device appends to its own
`oplog.<deviceId>.jsonl`; on load/timer, merge others' logs by (lamport,
actorId) order and apply unseen ops. Element-level last-writer-wins on
identical Ids; ink is append-mostly so conflicts are rare in practice.
No server, no accounts. Effort: L.

### Stage 2 — live sync (server, still LWW)
A small relay (SignalR or plain WebSocket; Azure Web PubSub works) that
broadcasts ops per "board" (page id + share token). Clients apply remote ops
through the same `FromOp().Do(page)` path and re-render. Presence = coloured
cursors sent as throttled ephemeral messages (never persisted).
Auth: share-link tokens first; real accounts only if it ever goes multi-user
at scale. Effort: XL (server + client + conflict edge cases + reconnect).

### Stage 3 — text CRDT (only if needed)
Ink/shape ops commute well under LWW; free-form RTF text does not. If two
people genuinely co-type in one text box, swap `TextElement.Rtf` sync to a
text CRDT (Yjs via a sidecar, or a C# port like YDotNet). Scope it to text
boxes only. Effort: XL. Defer until Stage 2 shows real demand.

## Comments (independent of live sync — can ship first)

Comments are just data:

```csharp
class PageComment {
  Guid Id; Guid? AnchorElementId;   // stroke/shape/text it points at (null = page-level)
  double X, Y;                      // world anchor if no element
  string Author; string Text; long CreatedTicks; bool Resolved;
}
// NotePage gains: List<PageComment> Comments = new();
```

UI: a small comment pin drawn on the Win2D canvas (same overlay pattern as
the audio playhead), a flyout to read/reply/resolve, a context-menu "Add
comment" on selection. Fully useful single-user (self-notes / TODO pins),
and it rides Stage 1/2 sync for free once ops exist. Effort: M. **This is
the piece I'd build first** — immediate value, no networking.

## Sequencing recommendation

1. Comments (M) — ship standalone.
2. Stage 0 op log (M) — invisible groundwork, improves crash safety.
3. Stage 1 synced-folder sharing (L) — collaboration without servers.
4. Stage 2 live relay (XL) — only if 3 proves the demand.
5. Stage 3 text CRDT (XL) — only if co-typing becomes real.

## Known hazards

- The whole-library debounced save must not fight incoming remote ops —
  Stage 1+ should move to per-page files (this also fixes the existing
  "serialize the world on every save" perf note).
- `NormalizeContent` shifts whole pages on load; it must be disabled for
  shared pages or run as a synced op, or collaborators' coordinates diverge.
- Undo semantics under collaboration: undo must revert *your own* ops only
  (track actor on the undo stack).
