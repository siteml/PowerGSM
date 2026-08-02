# Phase 5j — Purge & Rebuild History from Current State

Design document for the "wipe Manager-side history and re-derive
from the Node's authoritative current state" feature. Surfaced
during Phase 5i (the LO false-leave bug fix) when the operator
needed a way to clean up the bad rows the bug had produced
without losing the timeline for currently-connected players.
Read this first in a new chat; everything below assumes the
conversation is starting fresh.

---

## Status

Not started.

---

## Goal

Two needs surfaced together during the May 2026 false-leave
debugging:

1. **Recover from parsing-logic bugs that polluted history.**
   The UChannel::Close-versus-UNetConnection::Close fix in the
   prior phase eliminated the false-leave attribution going
   forward, but every false leave that fired before the fix is
   still sitting in PlayerActivity. Manually deleting them
   row-by-row through SQL is fragile and doesn't help the next
   time a similar bug lands. Same need will recur whenever
   identity-resolution semantics shift (Conan 5g-2c was
   another such moment) — operators want a "redo the history
   from what's actually true right now" button.

2. **Clean baseline for test→prod or migration scenarios.**
   When promoting a test Manager DB to production, or when
   recovering from a corrupted Manager DB by reattaching to
   live Nodes, the operator wants the History window to show
   only what's verifiable against current Node state, not
   accumulated noise from past sessions that may have included
   broken parse-rule behaviour, misconfigured instances, or
   plain test garbage.

Theme: graduate the Manager from "history is whatever
PersistPlayerObservation wrote, irreversibly" to "history can
be rebuilt from the Node's authoritative state, with no fake
data introduced." The Node is the source of truth for current
players, current tile, and full chat history; the Manager's
job is to faithfully transcribe that into its own tables.

---

## Honest assessment of current infrastructure

### What's reusable

- **`INodeClient` methods.** `GetPlayersAsync` returns the
  Node's in-memory `state.Players` dict (every current
  `PlayerSession` including `JoinedUtc`, identity columns,
  RemoteAddress). `GetServerStateAsync` returns
  `state.ServerState` (MatchState, TileId, TileName,
  CurrentMapPath, LastUpdatedUtc). `GetChatHistoryAsync` with
  `sinceUtc=Nothing` and a large limit returns the Node's
  full `chat_messages` table for the instance. Every wire
  path we need already exists with no contract changes
  required.

- **`InstanceManager._liveStates` / `_logStreamCancellations`.**
  Together they tell us which instances are currently being
  tracked by the Manager (Running state, active SSE stream).
  These are the instances the rebuild applies to.

- **`ResolveSessionIdentity`.** Already handles the three-tier
  fallback (live parser → open SessionHost row → fallback
  `{gameId}:{instanceId}`). After purging SessionHosts but
  before re-inserting them, the resolver falls through to the
  parser (which still holds CurrentSessionIdentity from before
  the purge, since `_logParsers` isn't cleared) or to the
  final `{gameId}:{instanceId}` fallback. Both produce a real
  identity we can stamp onto the rebuilt rows.

- **`_activePlayers` / `_chatCursors`.** Manager-side caches
  that gate notification dedup and chat-mirror pagination.
  Need to be repopulated post-rebuild so live events flowing
  back through the resumed SSE stream don't re-fire as if
  they were brand-new joins or re-insert chat rows we just
  inserted.

- **`StopLogStream` / `EnsureLogStreamAsync`.** Together they
  give us a clean pause-and-resume for the SSE log streams
  surrounding the purge window.

### What's missing and needs to be built

1. **Coordinated purge+rebuild flow on the Manager.** No
   existing entry point. Will be a single async method on
   `InstanceManager` that orchestrates the steps in the right
   order under a serialising lock so concurrent invocations
   (e.g. operator double-clicks the menu item) collapse to
   one. Returns a counts-plus-warnings result for the UI to
   render.

2. **UI surface.** Tools menu entry plus a confirmation
   dialog. The dialog needs to enumerate explicitly what
   survives the operation and what doesn't — the "what
   doesn't" list is the user's only chance to back out
   before destructive action.

3. **Chat filtering logic.** The Node's chat history covers
   the whole instance lifetime; we want only chat from
   currently-connected players, only since each player's
   most recent join. The filter takes the full chat list,
   matches each row to a current `PlayerSession` via
   identity priority (CharacterId → PlatformUserId →
   DisplayName), keeps only those where the chat timestamp
   is at or after the matched session's `JoinedUtc`, and
   drops everything else.

### What's deliberately NOT in scope

- **Filter-based granular purge** (the broader feature in
  Backlog under "History data purge: joins/leaves, chat, or
  both"). Phase 5j is a fresh-baseline-from-current-state
  operation; granular purge is "delete what I'm currently
  filtering for in the History window". Different surface,
  different use cases, different UI. The Backlog entry
  stays in place for future scoping.

- **Full log-file replay** (Phase 5j-discovery
  alternative). Doing a true complete replay from the
  on-disk log file would require a new Node endpoint
  streaming the full log, plus a chunked parse pipeline on
  the Manager side. Larger feature, real value but bigger
  scope; deferred until there's an operational need for
  reconstructing history beyond current connections.
  Acknowledged as the next-level version of this feature
  if v1 turns out insufficient.

- **Per-player purge / right-to-be-forgotten.** Distinct
  surface (per-PlatformUserId or per-CharacterId targeted
  delete with audit trail). Cross-references the Backlog
  entry; not part of this phase.

- **Soft delete / undo.** Hard-delete only. Soft-delete
  would require adding a `DeletedUtc` column to four
  tables and filtering every read path. Backlog entry
  notes this as a future refinement; v1 is hard-delete
  with an honest confirmation dialog.

- **Audit trail of purge operations.** Useful long-term
  ("operator X purged 247 rows on date Y") but not v1.
  The operation is operator-initiated through an obvious
  UI surface; the failure mode is forgetting they did it,
  which an audit row doesn't actually solve. Defer until
  there's a multi-operator scenario that needs it.

- **Node-side chat purge.** This phase touches only the
  Manager's `ChatMessages` table. The Node's
  `chat_messages` SQLite table is the source we're
  pulling from; purging it is the granular-purge feature's
  concern, not this one. After rebuild, the Manager's
  chat is a filtered subset of the Node's — that's
  intentional, not a bug.

---

## Architecture

### What the rebuild produces

For each instance currently in `_liveStates` with state
Running AND a live SSE log stream registered in
`_logStreamCancellations`:

| Table | Source | Filter / shape |
|---|---|---|
| `PlayerActivity` | Node `state.Players` | One `join` row per current `PlayerSession`. `TimestampUtc` = `sess.JoinedUtc`, identity columns straight from session, `EventKind = "join"`, `PlayerName` = `sess.PlatformPersona` (consistent with how `PersistPlayerObservation` writes it today). No `leave` rows — these are currently-online players, by definition they haven't left. |
| `PlayerSessions` | Node `state.Players` | One row per current `PlayerSession`. `FirstSeenUtc` = `LastSeenUtc` = `sess.JoinedUtc`. `LastHostInstanceId` = the instance we're rebuilding. (Using `JoinedUtc` for `LastSeenUtc` rather than `DateTime.UtcNow` because we have no verified "last activity" timestamp; `JoinedUtc` is provably real, `UtcNow` would be a fabrication.) |
| `ChatMessages` | Node `GetChatHistoryAsync(null, large)` | Per-row identity match against current PlayerSessions + `chat.TimestampUtc >= matched_sess.JoinedUtc`. Unmatched rows are dropped silently. Matching by (1) CharacterId, (2) PlatformUserId, (3) DisplayName, in priority order. |
| `SessionHosts` | Node `instance_state` via `GetServerStateAsync` | One open row per running instance whose `ServerState.TileName` is non-empty. `HostedFromUtc` = `ServerState.LastUpdatedUtc`. `HostedUntilUtc` = `Nothing`. `TileName` from the server state. |

Instances that are running but have no tile loaded yet (e.g.,
between tile cycles) produce no SessionHost row and no
PlayerActivity/Chat rows — `state.Players` is empty in that
case anyway.

### Order of operations

Stepping through what `PurgeAndRebuildHistoryAsync` does:

1. **Acquire the purge lock.** A new `SemaphoreSlim(1, 1)` on
   `InstanceManager` serialises concurrent invocations. If a
   second caller arrives while the first is running, it
   awaits (or fails fast with a clear message — see "Design
   decisions" below).

2. **Identify target instances.** Iterate `_liveStates`,
   filter to entries with `CurrentState = Running`, intersect
   with `_logStreamCancellations.Keys`. The intersection is
   the set we rebuild for. Skip instances whose Node is
   detached (per the Phase 5h node attach/detach toggle) —
   those aren't actively being tracked anyway.

3. **Pause live writes.** For each target instance, call
   `StopLogStream(instanceId)` to cancel the active SSE
   stream. This stops new lines from flowing into
   `HandlePlayerJoin/Leave` and the chat-mirror loop. In-flight
   `PersistPlayerObservationAsync` tasks already on the thread
   pool aren't directly cancelled, but they're fire-and-forget
   with their own internal try/catch; we'll briefly yield to
   let them complete naturally before snapshotting.

4. **Snapshot from Node.** For each target instance, fetch in
   parallel: `GetPlayersAsync`, `GetServerStateAsync`,
   `GetChatHistoryAsync(null, ChatRebuildLimit=10000)`. Wrap
   each call in its own try/catch — a single Node-side failure
   shouldn't abort the whole operation. Failed instances log
   a warning into the result and are skipped during the
   rebuild step.

5. **Resolve session identities up-front.** For each target
   instance, call `ResolveSessionIdentity(instanceId)`. This
   uses the live parser's `CurrentSessionIdentity` if
   available, falls back to a (now-deleted-soon)
   LookupOpenSessionHostIdentity, then to
   `{gameId}:{instanceId}`. We capture this BEFORE the
   purge so the lookup behaves predictably; using the cached
   value for the rebuild keeps identity stamping
   deterministic across the purge/rebuild boundary.

6. **Atomic purge + rebuild.** One `DbContext`, one
   transaction:
   - `DELETE FROM PlayerActivity`
   - `DELETE FROM ChatMessages`
   - `DELETE FROM SessionHosts`
   - `DELETE FROM PlayerSessions`
   - For each target instance with snapshot data: insert the
     SessionHost, PlayerActivity rows, PlayerSession rows,
     filtered ChatMessage rows.
   - `SaveChanges` once at the end. Either the whole rebuild
     commits or the whole thing rolls back.

7. **Repopulate Manager-side caches.** Before resuming SSE
   streams, prime the dedup caches with the rebuilt state:
   - For each target instance, set
     `_activePlayers[instanceId]` = HashSet of `PlayerName`
     values from the rebuilt rows. This prevents a re-fired
     `PlayerJoin` event for a currently-online player (which
     the Node's SSE ring buffer will replay on
     resubscription) from being written again as a duplicate
     PlayerActivity row.
   - For each target instance, set `_chatCursors[instanceId]`
     to the MAX `TimestampUtc` of chat rows we just inserted
     (or `DateTime.MinValue` if none were inserted). This
     prevents the next chat-mirror tick from re-pulling rows
     we already have.

8. **Resume SSE streams.** For each target instance, call
   `EnsureLogStreamAsync(instanceId)`. Internally this calls
   `ReregisterParseRulesAsync` (no-op for running instances
   since rules were already pushed at StartInstance time) and
   then `StartLogStream` to re-subscribe. The Node's SSE
   ring buffer replays its tail; with `_activePlayers`
   primed, duplicate Joins for current players no-op
   correctly.

9. **Return the result.** Counts of rows created per table,
   list of warnings (Node-side fetch failures, instances
   skipped because no tile loaded, etc.). Caller (the UI)
   renders this in a "Rebuild complete" dialog.

### Race conditions and how they're handled

**During step 3 (pause) and step 4 (snapshot).** The SSE
stream is cancelled, but the Node continues to receive log
lines from the game process and continues to update its own
`state.Players` and `chat_messages`. The snapshot we take in
step 4 is "Node state at fetch time" — slightly newer than
what was in the SSE stream when we paused. New events that
arrive between pause and snapshot are correctly captured.
This is the right direction.

**During step 6 (purge+rebuild transaction).** No live writes
are happening because SSE streams are still paused. The Node
keeps tailing the game but those events stay on the Node side
until we resume in step 8.

**During step 8 (resume).** The Node's SSE ring buffer
replays its last ~4096 lines. For a long-running instance
this overlaps significantly with what we just rebuilt:
- Join events for currently-online players: `HandlePlayerJoin`
  checks `_activePlayers` (primed in step 7), sees the name
  already in the set, no-ops. No duplicate row.
- Leave events for players who left between snapshot and
  resume: write through correctly. We didn't include them in
  the rebuild (they weren't in `state.Players` at snapshot
  time), but they need to land in PlayerActivity going
  forward. `HandlePlayerLeave` checks `_activePlayers`,
  finds the name (because we primed it with their pre-leave
  state), removes it, writes the leave row. Correct.
- Chat events: each line replayed by SSE goes through
  `EventStore.ProcessLine` on the Node, which inserts into
  Node's `chat_messages` with INSERT OR IGNORE (per the
  ux_chat_dedup index added in Phase 5g-1). Manager side,
  these come through the chat-mirror loop, not the SSE
  parser — `MirrorChatForInstanceAsync` uses `_chatCursors`
  to fetch only rows newer than the cursor. With the cursor
  primed in step 7 to the MAX timestamp of the rebuild, the
  next tick correctly picks up only post-rebuild chat.

**Operator cancels mid-flight.** The purge lock is a
`SemaphoreSlim`, not a cancellable wait — once the lock is
acquired, the operation runs to completion. The `IProgress`
callback gives the UI live status; "Cancel" on the dialog
just dismisses the progress display, the underlying work
continues. Acceptable for a v1: the operation is fast (<10s
typical) and partial completion (transaction rolled back) on
mid-flight failure is the more important guarantee.

### Sticky design decisions

**Parallel snapshots per instance.** Step 4 fetches per-
instance data sequentially in the v1 implementation. With
many instances, it could be parallelised via `Task.WhenAll`
across all three calls per instance, then across instances.
Defer parallelisation until measured slowness justifies it
— for the operator's typical 3-instance LO setup, sequential
is sub-second.

**SessionHost timestamp choice.** Plan recommends
`instance_state.updated_at_utc` (Node-persisted), which for
a stably-loaded LO tile represents the tile-load completion
time. Alternatives considered and rejected:
- "Earliest current-player JoinedUtc" (provably correct lower
  bound, but reads weirdly when a tile has exactly one
  player and the SessionHost timestamp exactly equals their
  JoinedUtc).
- "Don't create a SessionHost at all" (strict no-synthetic-
  data, but leaves PlayerActivity rendering with the
  fallback `{gameId}:{instanceId}` Source label — worse UX).

**Concurrent invocation.** Two operators triggering the
purge simultaneously is the multi-operator edge case.
Implementation: `SemaphoreSlim(1, 1)`. Second caller
awaits the first (default), with a configurable bail-out
("operation in progress" error after 5s) so a UI that
double-fires doesn't block forever. The UI itself should
also disable the menu item while the operation is running.

**What "currently connected" means precisely.** Defined as
"present in the Node's in-memory `state.Players` dict at
the moment of the snapshot fetch." Not "ever connected during
the current session," not "currently shown in the
InstancePanel" (which polls every 3 seconds and may be
stale). The Node is the authoritative source; one fresh
`/api/instances/{id}/players` call per instance is the
definition.

---

## Phase 5j-1: Core service

**Goal:** Single async method on `InstanceManager` that
orchestrates the purge+rebuild, callable from a hidden trigger
(menu, slash command, RCON) for initial testing without
shipping a UI surface.

**Deliverables:**

- New result DTO `PurgeAndRebuildResult` in
  `GSM.Manager.Core` namespace:
  - `InstancesRebuilt As Integer`
  - `InstancesSkipped As Integer`
  - `PlayerActivityRowsCreated As Integer`
  - `PlayerSessionRowsCreated As Integer`
  - `ChatRowsCreated As Integer`
  - `ChatRowsFilteredOut As Integer`
  - `SessionHostRowsCreated As Integer`
  - `Warnings As List(Of String)`
  - `DurationMs As Long`

- New method
  `PurgeAndRebuildHistoryAsync(progress As IProgress(Of String)) As Task(Of PurgeAndRebuildResult)`
  on `InstanceManager`. Implementation follows the order-of-
  operations laid out in Architecture above. Uses a private
  `_purgeLock As New SemaphoreSlim(1, 1)` member to serialise
  concurrent invocations. Progress callback fires at each
  major step transition with human-readable strings ("Pausing
  log streams...", "Fetching current state from 3
  instances...", "Rebuilding history rows...", "Resuming log
  streams...").

- New private helpers (in `InstanceManager`):
  - `IdentifyRebuildTargetsAsync() As List(Of String)` — the
    intersection of `_liveStates` (Running) and
    `_logStreamCancellations.Keys`, after filtering out
    detached nodes.
  - `SnapshotInstanceStateAsync(instanceId As String) As
    Task(Of InstanceSnapshot)` — fetches players/state/chat
    in one call. Returns Nothing on failure (caller logs into
    warnings).
  - `FilterChatToCurrentSessions(chat As IReadOnlyList(Of
    ChatMessage), players As IReadOnlyList(Of PlayerSession))
    As (Kept As List(Of ChatMessage), Dropped As Integer)` —
    applies the identity+timestamp filter described above.
    Returns kept rows + count of dropped rows for the result
    summary.
  - `PrimePostRebuildCaches(instanceId As String, snapshot As
    InstanceSnapshot, maxChatTimestamp As DateTime)` — sets
    `_activePlayers` and `_chatCursors` for the instance.

- New private DTO `InstanceSnapshot` (internal):
  - `InstanceId As String`
  - `SessionIdentity As String`
  - `Players As IReadOnlyList(Of PlayerSession)`
  - `ServerState As ServerStateResponse`
  - `Chat As IReadOnlyList(Of ChatMessage)`

- Wired up to a dev-only trigger initially. Options:
  - Hidden menu item under Tools that's only visible when a
    debug compile flag is set.
  - RCON-style `gsm:purge-rebuild` command via the existing
    notification command surface.
  - Simplest: just an unbound method that gets exercised via
    a temporary `Task.Run` in `MainForm.Load` during dev
    testing. Removed once 5j-2's UI lands.

**Design notes:**

- The async method is fire-and-forget from the UI's
  perspective — UI awaits the result for rendering, but
  doesn't try to thread-block on it. Standard `Async Function`
  pattern.
- `_purgeLock.WaitAsync(5000)` (5-second wait, then fail) so
  double-clicks don't deadlock the UI but legitimate races
  serialise correctly.
- Transaction scope: `using transaction = db.Database.BeginTransaction()`
  inside the rebuild step. Commit on success, rollback in any
  exception path. EF Core's SaveChanges enrols automatically.
- No EF migration needed — purely DELETE + INSERT against
  existing tables.

**VB.NET considerations:**

- `SemaphoreSlim.WaitAsync` is documented to return a `Task`
  that completes when the semaphore is acquired. The
  `WaitAsync(timeout)` overload returns `Task(Of Boolean)`
  — `True` on acquired, `False` on timeout. Standard usage.
- Async over `Using db = scope.ServiceProvider.GetRequiredService(...)` —
  watch for any `Using db = ...` blocks containing the await
  on transaction commit; VB lifetime rules say the `Using`
  block awaits all its inner async before disposing, so
  this is fine, but worth being explicit in the
  implementation rather than relying on Dispose-order
  intuition.
- For the cache priming, `_activePlayers` is a
  `ConcurrentDictionary(Of String, HashSet(Of String))` — the
  HashSet itself isn't thread-safe, but we own it during the
  prime-then-resume window (SSE stream is paused). Safe to
  populate without locks during this window; after resume,
  the existing per-instance access pattern resumes.

---

## Phase 5j-2: UI integration

**Goal:** Surface the rebuild operation through the Manager
UI with a clear confirmation flow that doesn't surprise the
operator.

**Deliverables:**

- New menu item in `MainForm` Tools menu: **"Purge & Rebuild
  History..."**. Placement between existing entries TBD
  during implementation (probably above Settings, below
  Automation Rules).

- New form `PurgeAndRebuildHistoryForm` (in
  `RemainingForms.vb`): modal confirmation dialog.
  Layout, top to bottom:
  - **Heading:** "Purge & Rebuild History"
  - **Subhead:** "This will delete all chat, player activity,
    player sessions, and session hosts from the Manager database
    and rebuild them from currently-running instances'
    authoritative state on their Nodes."
  - **What's preserved** section (a `GroupBox` titled
    "Preserved"):
    - "Real join timestamps for every currently-connected
      player (from each Node's in-memory session state)"
    - "Full chat history for currently-connected players,
      since each player's most recent join, taken from each
      Node's persistent chat database"
    - "Current tile / session metadata for each running
      instance (one open SessionHost row per instance with
      a loaded tile)"
  - **What's lost** section (a `GroupBox` titled "Removed,
    not recoverable"):
    - "All historical join/leave events for players no
      longer connected"
    - "All chat from players no longer connected"
    - "All chat from previous sessions of currently-connected
      players (if they were on earlier and rejoined)"
    - "All session-host history for previous tiles on running
      instances"
    - "All history from instances not currently running on
      attached nodes"
  - **Affected instances preview** (a small `ListBox`): one
    line per instance in the rebuild target set, showing
    Node + Instance display name + current online count.
    Refreshed on form open by querying
    `InstanceManager.GetLiveState` for each known instance.
  - **Confirmation:** typed-confirmation TextBox — the
    operator must type the word `REBUILD` (case-sensitive)
    before the Confirm button activates. Same shape as
    "DELETE" pattern documented in the History data purge
    Backlog entry.
  - **Confirm + Cancel** buttons.

- New form `PurgeAndRebuildProgressForm` (in
  `RemainingForms.vb`): modal progress dialog shown while
  the operation runs.
  - `Label` for current step (driven by the `IProgress`
    callback).
  - `ProgressBar` in marquee mode (no determinate progress
    available since we don't pre-count rows).
  - **No Cancel button** — the operation runs to completion
    once started (see Architecture > "Operator cancels
    mid-flight").

- New form `PurgeAndRebuildResultForm` (in
  `RemainingForms.vb`): modal results summary shown after
  the operation completes (or after an error).
  - Counts table: instances rebuilt, rows created per table,
    rows filtered out, duration.
  - Warnings list (empty for clean runs).
  - OK button to dismiss.

- Wire-up in `MainForm`:
  - Menu item Click handler awaits
    `PurgeAndRebuildHistoryForm.ShowDialog`. On confirm,
    swaps to `PurgeAndRebuildProgressForm`, kicks off the
    service call with the progress dialog as the
    `IProgress(Of String)` consumer.
  - On task completion, closes the progress dialog and
    shows the result form.
  - On exception, closes the progress dialog and shows an
    error MessageBox plus a partial result form with the
    warnings populated. Transaction rolled back means DB is
    intact; the UI should make this clear ("Operation
    failed and was rolled back; no rows were deleted").

**Design notes:**

- The "What's lost" list is the central honesty: it's the
  one section the operator needs to internalise before
  hitting Confirm. Wording is deliberate. Don't soften it
  with "you may lose..." or "potentially..." — it's
  definite loss for those categories, and the operator
  needs to know that with no ambiguity.
- The affected-instances preview prevents the surprise where
  the operator forgets which instances are running and
  scopes the operation wider than they intended. If only
  one of three LO instances is running, only that one is
  rebuilt; the History rows for the other two are deleted
  along with everything else but don't get any rebuild.
  The preview surfaces this honestly.
- The typed-confirmation field for `REBUILD` matches the
  pattern recommended in the History data purge Backlog
  entry for any operation that affects more than N rows or
  is "all-time" scoped. This is unconditionally all-time
  scoped, so the typed-confirm is unconditional.
- Result form not auto-dismissed: the operator should
  acknowledge what happened before closing. Closed via the
  OK button only.

---

## Phase 5j-3 (optional, deferred): Per-instance rebuild

**Goal:** Right-click an instance in the tree, choose
"Rebuild History for This Instance" — performs the same
operation but scoped to one instance instead of all running
ones.

**Status:** Deferred. The motivation is clear (an operator
notices bad rows for one specific instance and doesn't want
to nuke the others' rebuilt state). The complication is
that purge can no longer be "DELETE FROM table" without a
WHERE clause; every delete needs an InstanceId filter, which
makes the SQL slower and the orchestration more fragile.
PlayerSession is name-keyed (not instance-keyed), so a
single-instance purge can't cleanly exclude rows for the
target instance without knowing which player names belong
where — and PlayerSessions across instances may legitimately
share PlayerName.

Pickup notes if it becomes needed: scope each DELETE by
SessionIdentity (which is unique per instance per
running-session), accept that PlayerSessions targeting works
on (SessionIdentity, PlayerName) composite keys, and
constrain the rebuild target set to a single instance ID
passed into `PurgeAndRebuildHistoryAsync`. Lift the existing
implementation; don't fork a separate method.

For v1, the use case is covered by full purge + rebuild —
if the operator only wants to clean up one instance, they
can stop the others before triggering the rebuild (skipping
those instances from the target set), then start them again
after. Awkward but functional.

---

## Validation plan

**Setup:**

1. Live LO realm with at least one tile loaded and at least
   two players connected (Z3RO SH4DOW scenario from the
   false-leave bug, plus one other).
2. Pre-rebuild DB state: PlayerActivity contains at least
   one false-leave row (artificially inject if not present
   from earlier testing) plus normal joins/leaves for the
   current session. ChatMessages contains chat from past
   players who have since disconnected plus chat from
   current players.

**Test cases:**

1. **Clean rebuild against active LO realm.**
   - Trigger via Tools menu.
   - Verify confirmation dialog enumerates the target
     instances correctly (running ones only).
   - Confirm with `REBUILD`.
   - Verify progress dialog shows step transitions.
   - Verify result dialog reports nonzero
     `PlayerActivityRowsCreated`, `ChatRowsCreated`,
     `SessionHostRowsCreated`.
   - Open History window: expect only join rows for
     currently-connected players with timestamps matching
     each player's actual `JoinedUtc`. No leave rows. Chat
     limited to chat from currently-connected players since
     their JoinedUtc.
   - Verify the false-leave bug's residual rows are gone
     (they should be).

2. **Live events flow correctly after rebuild.**
   - Have a confederate player disconnect immediately after
     rebuild completes.
   - Verify their leave row appears in PlayerActivity with
     the correct identity and timestamp.
   - Verify their pre-rebuild join row (just inserted) is
     correctly paired with this leave row when viewed in
     History.
   - Have another confederate join.
   - Verify their join row appears with the actual UE4
     log timestamp, not some artifact of the rebuild.

3. **Chat continues to mirror correctly.**
   - Have a connected player say something in chat after
     rebuild.
   - Wait for the chat-mirror tick (5 seconds default).
   - Verify the new chat line appears in History without
     duplicating any pre-rebuild chat.
   - Verify the cursor is correctly tracking newest-only.

4. **No-running-instances edge case.**
   - Stop all instances.
   - Trigger rebuild.
   - Verify confirmation dialog shows an empty
     affected-instances list.
   - If operator still confirms: verify rebuild runs
     cleanly with all-zero counts, no errors.

5. **Multi-instance rebuild.**
   - Two LO instances running (different realms, same node).
   - Trigger rebuild.
   - Verify both instances get their own SessionHost rows
     with correct SessionIdentity values (different per
     realm/tile).
   - Verify per-instance player and chat segregation.

6. **Node fetch failure recovery.**
   - Detach one node mid-rebuild (simulate failure by
     briefly blocking its port).
   - Verify the operation continues for other nodes'
     instances.
   - Verify the result `Warnings` list contains the failed
     instance(s).
   - Verify the DB transaction either commits with partial
     data (other instances) or rolls back entirely
     depending on which strategy lands in implementation.
     (Recommend: commit partial; rolling back on one
     Node's failure punishes the operator for the
     unrelated outage.)

7. **Concurrent-invocation safety.**
   - Trigger rebuild twice in quick succession (e.g.,
     spam the menu item or run from two operators).
   - Verify second invocation either waits cleanly or
     errors with a clear "operation in progress" message.
   - Verify no DB corruption or duplicate rows result.

8. **Cancellation behaviour.**
   - Trigger rebuild, immediately try to close the
     progress dialog.
   - Verify the underlying operation continues to
     completion regardless of UI dismissal.
   - Verify result form still appears even if progress
     was dismissed.

**Manual SQL verification post-rebuild:**

```sql
-- Should match the count of currently-connected players
-- across all running instances.
SELECT COUNT(*) FROM PlayerActivity;

-- Same as above (one summary row per current player).
SELECT COUNT(*) FROM PlayerSessions;

-- One per running instance with a loaded tile.
SELECT COUNT(*) FROM SessionHosts WHERE HostedUntilUtc IS NULL;

-- Should have non-NULL CharacterId for at least the LO rows
-- (Conan and Factorio may have NULL on older sessions).
SELECT EventKind, COUNT(*), SUM(CASE WHEN CharacterId IS NULL THEN 1 ELSE 0 END) AS null_cid
FROM PlayerActivity GROUP BY EventKind;
```

---

## Open questions to resolve during implementation

1. **Chat fetch limit.** `GetChatHistoryAsync` accepts a
   limit parameter. Node-side: how many rows does it return
   when limit is large but `sinceUtc` is null? Need to
   verify there's no implicit cap that would silently drop
   older chat. If there is, the rebuild should iterate
   (fetch the most recent N, then fetch the N before that
   via `sinceUtc=oldest_fetched_so_far` going backward —
   but `GetChatHistoryAsync` is a "since" query, not a
   "before" query, so this would need an additional Node-
   side endpoint OR a single fetch with explicit
   confirmation that the limit was sufficient). For v1,
   pick a large default (10000) and document the limit; if
   the user has an instance with more than 10000 chat
   rows of currently-connected-player chat (extraordinarily
   chatty long-lived session), they can hit "Rebuild again"
   or we add pagination later.

2. **Progress callback granularity.** Per-instance step
   ("Snapshotting instance 2 of 3...") or per-table-write
   step within an instance? Recommend per-instance for
   v1; finer granularity if the operation feels slow in
   practice.

3. **InstanceSet tag rebuild.** If an instance was tagged
   with `InstanceSetTag`, that tag is on the `InstanceEntity`
   row (not affected by purge). No action needed; mention
   only as a thing the rebuild deliberately doesn't touch
   so it's obvious in the "what's preserved" list.

4. **Notification-on-completion.** Should the rebuild
   completion fire an `InstanceLifecycle`-style notification
   event? Probably no for v1 — it's an explicit operator
   action, not an autonomous one. Adds complexity for
   marginal value. Defer.

5. **Diagnostic logging.** Beyond the `IProgress` callback
   (which is for UI), should there be a structured log
   entry in the Manager log for the operation start/end
   with the result counts? Yes — at Information level, one
   entry at start ("Purge & rebuild requested, N instances
   targeted") and one at completion ("Purge & rebuild
   completed, M rows created, K warnings, duration Xms").
   Useful for post-mortem if something goes wrong.

---

## Cross-references

- **Phase 5g** (`Phase5g_Plan.md`) — Established the
  identity-column infrastructure that the rebuild stamps
  onto PlayerActivity rows.
- **Phase 5h** (`Phase5h_Plan.md`) — Node attach/detach
  toggle. Detached nodes are skipped during the rebuild
  target identification.
- **Backlog: "History data purge: joins/leaves, chat, or
  both"** — Different feature (granular filter-based delete)
  that stays in the Backlog. Phase 5j is the rebuild-from-
  current-state cousin; they don't overlap.
- **Backlog: "Player-list ghost on misrouted connection"** —
  Related in spirit (history-cleanup motivation), but a
  separate bug about Node-side leave detection, not a
  rebuild operation.

