# EventStream Unification Plan

Status: SLICES 1–3 DONE — all gates (Q1–Q7, Q4b) passed on the
live fleet 2026-08-09. Slice 4 (ConPTY) tracked in the Phase 8 shim
plan. Legacy Manager ingestion deletion pending a parity soak.
POST-SLICE BATCH (2026-08-09, second pass): SSE live push landed
(the deferred drop-in — see the Slice 3 post-slice additions
below), lifecycle rows now render in Manager History (the deferred
display work), and incident follow-ups #4 (orphan reaping) + #6
(redist verification) are implemented. Compile-verified in sandbox
(Contracts/Node full, Manager reference-partial); live gates for
this batch pending — see
claude/EventStream_SSE_Followups_Findings_2026-08-09 in the
project for test steps.
Originally drafted 2026-07-30. Follows from the Palworld save-failure
incident (see claude/Palworld_Incident_2026-07-28 in the project) and
the observation that history and the live player list reach the
Manager/Discord bot through separate pipelines that can disagree.

## Problem

Today the node keeps an in-memory player list + server state (EventStore
folds over parse-rule hits), persists chat to SQLite, and exposes each
through its own endpoint. The Manager separately builds PlayerActivity /
History from its own ingestion, and the Discord bot consumes Manager
plumbing of its own. Three consequences:

1. The same fact (a player joined) travels multiple paths and can
   disagree or go missing on one of them.
2. Lifecycle facts (crash, crash-restart, crash-loop halt) live in NONE
   of them — during the July incident the node silently restarted a
   crashing server five times and the only record was the Windows event
   log. "Running" in the UI was a lie by omission.
3. Every new data source (Palworld REST player list, future ConPTY
   console capture) forces a decision about WHICH pipeline to feed,
   multiplying special cases.

## Design

One canonical, append-only event stream per instance, owned by the
NODE (node autonomy principle: everything must keep working with the
Manager offline). Everything downstream is a projection of that stream.

### Event record

```
EventRecord
  Seq        Long      ' monotonic per instance, assigned by node at append
  InstanceId String
  UtcAt      DateTime
  Kind       ParsedEventKind (extended — see below)
  SourceId   String    ' which ingestion source emitted it ("stdout", "pal-rest", "tailer:pal", "lifecycle")
  Fields     JSON      ' named capture fields: Name, Platform, PlatformUserId, CharacterId, RemoteAddress, Message, MatchState, TileId, TileName, MapPath, Registered, ExitCode, CrashCount, ...
  Raw        String?   ' original log line when there is one; Nothing for synthesized events
```

`ParsedEventKind` gains lifecycle members (APPEND-ONLY, same discipline
as NodeErrorCodes): `ProcessStarted`, `ProcessExited`,
`ProcessExitedUnexpectedly`, `CrashRestartScheduled`, `CrashLoopHalted`,
`InstanceStopRequested`, `InstanceAdopted`. Crash-restart visibility
(incident follow-up #3) falls out of this for free: ProcessManager emits
lifecycle events into the same stream everything else reads.

### Sequence + cursor semantics

- Seq is per-instance, strictly increasing, assigned under the same lock
  as the append. Never reused; restarts continue from MAX(seq) in SQLite.
- Consumers track a cursor (last seen seq) and pull
  `GET /api/instances/{id}/events?since={seq}&max={n}`.
- Live push: SSE `GET /api/instances/{id}/events/stream?since={seq}` —
  replays the gap first, then goes live. Backfill and live are the SAME
  representation, so a Manager that was offline for a week and one that
  never disconnected run identical code.

### Ingestion sources (front end of the stream)

All existing capture paths become emitters into the stream instead of
private updaters of EventStore state:

- Stdout/stderr capture         (exists — reroute emit)
- File tailers                  (exists — reroute emit)
- REST/state pollers            (NEW — slice 2, Palworld first)
- ConPTY console capture        (FUTURE — GSM.Shim Phase 8 work; becomes
                                 just another emitter, no downstream change)
- ProcessManager lifecycle      (NEW — emits the lifecycle kinds above)

Parse rules are applied inside the ingestion layer exactly as today —
compiled once per RegisterInstance, applied per line — but a rule hit
now produces an appended EventRecord instead of a direct mutation of the
player list.

### REST/state poller (the piece that gets Palworld player listing NOW)

The node polls a plugin-declared endpoint, diffs successive snapshots,
and synthesizes events. For Palworld: poll `/v1/api/players` (Basic auth
admin/AdminPassword, port from the tuple's RESTAPIPort) every N seconds
(default 5). Player present now but not in previous snapshot →
PlayerJoin (Fields from the REST payload: Name, PlatformUserId=userId,
CharacterId=playerId, RemoteAddress=ip). Absent now, present before →
PlayerLeave. Downstream cannot tell a polled event from a parsed one —
that is the point.

Poller hygiene rules (learned the hard way this week):
- Poll failures are NOT leave events. Only synthesize leaves from a
  SUCCESSFUL poll that lacks the player. An API that stops answering
  emits a `Custom` source-degraded event once, not 32 fake leaves.
- Debounce restarts: after ProcessStarted, wait for the first successful
  poll before diffing (world-load window returns empty/blank payloads).
- The poller touches the game's REST API only — never the filesystem.
  (No new watchers. Ever. See incident doc.)

### Source authority (dedupe)

A game can have TWO sources emitting the same fact — Palworld will,
once ConPTY lands: log-parsed PlayerJoin AND poller-diffed PlayerJoin.
Without a rule, every join double-emits and every projection counts it
twice.

Rule (decided 2026-07-30): DEFAULT is first-come-append — no
suppression, no ranking. Per-kind source authority is strictly OPT-IN.

Priority ranking was considered and rejected: ranking only orders
sources, it doesn't suppress any, so duplicates survive it. The only
coherent positions are "dedupe via a declared owner" or "don't dedupe" —
so the default is don't (correct wherever one source emits a kind,
i.e. every current game), and authority is the opt-in for a plugin that
KNOWS it is double-sourced.

- Each ingestion source declaration (log source, poll source) may carry
  `AuthoritativeKinds As ParsedEventKind()` — the kinds this source OWNS
  for the instance.
- At RegisterInstance the node builds a per-kind authority map. For a
  kind with a declared owner, events of that kind from any OTHER source
  are dropped at append time (counted in a per-source `suppressed`
  metric, visible in diagnostics, so a misconfigured map is observable
  rather than silent).
- Kinds with NO declared owner: first-come-append (today's behavior) —
  correct for the common case of a single source per kind.
- Lifecycle kinds are always owned by the node's own lifecycle emitter;
  plugin declarations cannot claim them.

Palworld's declaration: `pal-rest` owns PlayerJoin/PlayerLeave (poll
diffing is steadier than log text across game patches — Pocketpair can
reword a log line any Tuesday; the REST payload is a stable contract).
ChatMessage stays log-owned once ConPTY exists, since it has no REST
equivalent. If the console text later proves richer for joins (e.g.
carries CharacterId at join time that REST lacks), authority flips with
a one-line plugin change and no node/Manager changes — that's the test
that the abstraction is right.

### Identity enrichment (Last Oasis pattern)

VERIFIED 2026-08-02 against GSM.Manager/Core/IdentityResolver.vb and
IdentityFormatter.vb — the original premise here was wrong in two ways
and the section is corrected accordingly:

1. The NODE already resolves LO character names itself. EventStore's
   parse path handles the whole Persisting/Processing-character-update
   dance node-side: PlayerIdentity parse rules (the kind already exists
   in ParsedEventKind and is parse-emitted, enrichment-only), the
   pid-/cid-keyed pending-identity stashes, the persistent `players`
   table (keyed character_id, COALESCE-merged), and DisplayName
   hydration at join from that table. Node events therefore carry
   resolved names as soon as the logs surface them — NOT raw
   CharacterIds only. "Resolved names live only in the Manager" was
   false for LO.

2. What the Manager's IdentityResolver actually adds is different and
   narrower: it is a union-find fusion cache keyed
   (GameId, SessionScope) — for LO the scope string is
   "{realm_id}:{tile_id}" from the plugin parser's SessionIdentity,
   so realm+tile, not the instance — hydrated from the last 30 days /
   5000 rows of PlayerActivity, fed by Manager-side observations of
   node surfaces. Its value over the node's own resolution:
   cross-instance identity, history-era bindings the node's players
   table never saw, and record fusion when partial keys arrive via
   different instances. IdentityFormatter is display-only coalesce
   (DisplayName → PlatformPersona → fallback) and is irrelevant to the
   stream design.

3. SECOND AMENDMENT (2026-08-02, after re-checking the LO name-
   resolving plugin itself — LoMyrealmPlugin, which the first pass
   missed): there IS an authoritative Manager-side name source beyond
   fusion, and it is the piece that must not get lost in the stream
   migration. LoMyrealmPlugin (utility plugin, identity-read/write,
   LO only) subscribes to PlayerJoin/PlayerLeave, takes realm_id off
   the event's SessionIdentity, and reads the AUTHORITATIVE current
   character name from the myrealm rename page
   (myrealm.lastoasis.gg/realm/{realm_id}/Characters/{cid}/Rename)
   through the Manager's shared, encrypted web session
   ("myrealm:default", captured via the host's login dialog). It
   fills the naming window BEFORE the first Persisting tick and
   tracks portal renames, contributing CharacterId → name into the
   resolver scoped to the event's realm:tile. Also relevant: on the
   post-5g-1 LO build, Persisting fires mostly AT DEPARTURE, so
   node-side parse resolution is weaker than point 1 implies —
   in-play name quality on LO genuinely depends on chat lines, the
   node players-table cache, and myrealm.

   Consequences for the stream design:
   - The Slice-3 manager:identity append endpoint is NOT merely
     optional polish — it is the vehicle by which myrealm-resolved
     names (inherently Manager-side: web session + login dialog)
     become stream facts visible to node folds, Discord, and any
     future consumer. Keep it in Slice 3 scope.
   - myrealm lookups are join-triggered and rename-tracking; their
     ContributeIdentity output is scoped realm:tile, so the
     scope→instances mapping note below applies to exactly this path.
   - Node autonomy statement stands, with the honest caveat: without
     a Manager, LO names on a quiet tile may lag until chat or a
     departure-time Persisting tick lands.

The design consequence (Slice 3, unchanged in shape, corrected in
framing): the kind-restricted, authenticated append endpoint
(`POST /api/instances/{id}/events`, allowed kinds: PlayerIdentity only,
SourceId forced to `manager:identity`) is kept, but as an OPTIONAL
enrichment channel for the two things the node cannot know —
realm-scoped fusion results and history-hydrated bindings — not as the
primary name-resolution path. Notes for the implementer:

- `manager:identity` events coexist with parse-emitted PlayerIdentity
  events under first-come-append (both are enrichment-folds, merge-
  based; no authority declaration needed).
- IdentityResolver is scope-keyed, so the Manager must map a scope's
  resolution result onto the instance(s) currently serving that scope
  when appending.

Node autonomy holds exactly as before: with no Manager attached, the
node's own resolution covers the live-log cases (which is most of
them); Manager enrichment upgrades the rest on reconnect.

### Raw log feed (log viewer window)

The live log window's feed stays a SEPARATE channel (ring buffer +
`/logs/recent` + stream), not an event kind. Raw lines are high-volume,
lossy by design (4000-line trim), and have opposite retention needs
from semantic events; routing them through the stream would churn seq
numbers and bloat the events table for no consumer benefit. Provenance
is preserved anyway: rule-matched lines carry their original text in
the event's `Raw` field. If full console text in the stream is ever
wanted for debugging, a per-instance-gated `LogLine` kind is the clean
future add — default off, excluded from Manager sync unless enabled.

### Node persistence

New SQLite table `events` (instance_id, seq, utc_at, kind, source_id,
fields_json, raw), PK (instance_id, seq), index on (instance_id, kind).
`chat_messages` remains as a legacy view/table during migration, written
by a projection rather than by ingestion directly. Retention: per-kind
policy enforced by the existing node pruner pattern (prune at startup +
periodic timer, same shape as FileLoggerProvider.PruneOldLogs).
Defaults DECIDED 2026-08-02 (Slice 1):

- PlayerJoin / PlayerLeave: NEVER PRUNED (revised 2026-08-02; was
  90d). Join/leave history is what last-seen is built from — pruning
  it silently breaks last-seen for any player inactive longer than
  the window. The only sanctioned removal is an explicit operator
  hard purge / rebuild, done manually against the events table, never
  by the automatic pruner.
- Lifecycle kinds (ProcessStarted .. InstanceAdopted): 365 days
- ChatMessage: 90 days (the `chat_messages` legacy table keeps its own
  existing behaviour untouched)
- PlayerIdentity / ServerStateChange / TileLoaded / Custom: 30 days
  (high-volume state noise; projections fold them into current-state
  tables anyway; durable name state lives in the players table and
  the Manager's identity stores, not in raw identity events)

Hard purge / purge watermarks (added 2026-08-02, prompted by the
observed "purged chat comes back" behaviour): dedup alone cannot make
a purge stick — once the rows are deleted, the next tailer backfill /
adoption replay of the same log lines re-inserts them (exactly what
the pre-stream chat purge suffered). So a purge is two facts, not
one: DELETE the rows AND record a per-(scope, kind) watermark in
`event_purge_watermarks`; the append path drops any event of that
kind timestamped at or before its watermark BEFORE the insert
attempt, and watermark-dropped events are not folded either — purged
history neither re-persists, nor re-materialises in projections, nor
ever reaches Slice-3 Manager sync. Operator surface:
`POST /api/instances/{id}/events/purge?kind=...[&throughUtc=...]`
(kind=ChatMessage also purges the legacy chat_messages projection so
/chat agrees). Watermarks move forward only. This endpoint is the
sanctioned hard-purge path for join/leave (the pruner never touches
those); purging them is an explicit last-seen reset, and replay below
a watermark can no longer rebuild in-memory state for that kind —
that is the meaning of "hard purge/rebuild". The retention pruner's
MAX(seq) guard applies to purges too (at most one expired row per
scope survives until newer events land).

Replay dedup (Slice 1 implementation note): the events table carries a
UNIQUE index on (scope_type, scope_id, source_id, kind, utc_at, raw)
with INSERT OR IGNORE. This is REPLAY dedup only — same source
re-flowing the same line (adoption ring-buffer replay, tailer backfill)
appends nothing — and deliberately NOT cross-source dedup: two sources
emitting the same fact both append, per the first-come-append default.
Duplicate-suppressed replays still drive the in-memory folds so
adoption rebuilds the player list exactly as today; they just don't
re-persist or re-notify. Seq is only advanced on a real insert, so
replays create neither dupes nor gaps.

### Projections (back end of the stream)

- Current player list  = fold over Join/Leave/Identity (in-memory, per
  instance — same data the EventStore holds today, reframed).
- Server state         = fold over ServerStateChange/lifecycle.
- Chat                 = filter Kind=ChatMessage.
- History (Manager)    = the stream itself, synced by cursor into the
  Manager DB; PlayerActivity tables become a Manager-side projection fed
  by the sync, not a separate ingestion path.
- Discord bot          = consumes the Manager's synced stream via the
  existing panel/notification surfaces; its plumbing collapses to one
  subscription.

Existing endpoints (`/players`, `/server-state`, `/chat`,
`/logs/recent`) SURVIVE as projection reads — wire-compatible, so the
Manager migrates consumer-by-consumer instead of big-bang.

## Plugin contract impact

- IGamePlugin: NO changes to existing members. GetLogSources,
  GetLogParseRules, CreateLogParser, restart hooks — untouched. Every
  existing plugin compiles and runs unmodified.
- NEW additive interface (own interface, not a new IGamePlugin member,
  so old plugins don't even recompile). REVISED 2026-08-09 before
  Slice 2 implementation — the original draft had the Manager
  resolving `AuthFieldRefs` "from the tuple" as if the values were
  Manager-side config fields. Verified reality (PalworldPlugin's
  live IRemoteControlProvider, 13 Jul): RESTAPIPort / AdminPassword /
  RESTAPIEnabled live in the GAME'S OWN INI on the node
  (PalWorldSettings.ini OptionSettings tuple), read via the
  RemoteControlContext.FetchInstanceFile pattern — and they only
  take effect at game boot, so values resolved at StartInstance time
  are valid for that entire run. The contract is therefore async and
  context-ful, mirroring RemoteControlContext, and the plugin returns
  FULLY RESOLVED sources; the Manager copies them onto the wire
  verbatim (zero game-specific code outside the plugin, per Q5):

```vb
Public Class StatePollResolveContext
    Public Property Config As InstanceConfig
    Public Property FetchInstanceFile As Func(Of String, Task(Of String))
End Class

Public Interface IStatePollProvider
    ' Called at instance start. Return Nothing/empty for "no poller"
    ' (e.g. Palworld with RESTAPIEnabled=False).
    Function GetStatePollSourcesAsync(context As StatePollResolveContext) _
        As Task(Of IReadOnlyList(Of ResolvedStatePollSource))
End Interface

' Wire DTO (NodeApiContract) — also the contract return type:
Public Class ResolvedStatePollSource
    Public Property SourceId As String            ' "pal-rest"
    Public Property Url As String                 ' "http://127.0.0.1:8212/v1/api/players" (node-loopback)
    Public Property AuthKind As PollAuthKind      ' None / Basic
    Public Property AuthUsername As String        ' "admin"
    Public Property AuthPassword As String        ' resolved from the game INI at start
    Public Property IntervalSeconds As Integer    ' default 5
    Public Property PayloadShape As PollPayloadShape ' PlayerArray (v1)
    Public Property PlayersArrayPath As String    ' "players" — JSON property holding the array
    Public Property KeyField As String            ' capture-field used as the diff identity key ("PlatformUserId")
    Public Property PlatformPrefixField As String ' optional: split "steam_7656..." into Platform + id (matches the remote-control path's semantics)
    Public Property FieldMap As Dictionary(Of String, String) ' payload key -> capture-field name (payload keys matched case-insensitively — live API returns "iP")
    Public Property AuthoritativeKinds As List(Of ParsedEventKind) ' kinds this source OWNS — see Source authority
End Class
```

  Consequences accepted with this shape:
  - Poll sources (including the resolved password) are persisted in
    the node's InstanceSnapshots (PollSourcesJson, additive column)
    so polling survives node restart + adoption without the Manager.
    Plaintext-in-node.db is accepted: node.db sits on the same box as
    the game INI that holds the same password in plaintext.
  - Lean-adopted (node.db-loss) instances have no poll sources until
    a full instance restart — same documented gap as parse rules.
  - Baseline join emission is roster-aware: the first successful poll
    emits PlayerJoin only for players NOT already in the in-memory
    roster (which stream hydration just rebuilt), so node restarts
    don't fabricate join events for players who never left.

- Contracts: RequiresContracts bump, everything appended at the end.
- ProtocolVersion: additive only (new endpoints, new StartInstanceRequest
  field carrying poll sources). Old Manager + new node and vice versa
  keep working on the existing surfaces.
- IAutomationRule / notification plugins: unchanged interfaces; the rule
  engine gains richer triggers later (lifecycle kinds) as a separate,
  additive step.

## Slices

Slice 1 — Stream core (node)
  events table + append path + seq assignment (keyed on
  ScopeType/ScopeId from day one, Instance-only in practice); ProcessManager lifecycle
  emitters; reroute existing stdout/tailer parse hits to append;
  player-list/server-state/chat rebuilt as folds; `/events` + SSE
  endpoints. Existing endpoints wire-compatible.
  Q1: node restart mid-stream → seq continues, no dupes/gaps. 
  Q2: /players output byte-identical to pre-slice behavior on LO + Windrose.
  Q3: crash restart of a test instance appears as lifecycle events and
      the fold shows Running with CrashCount populated.

  STATUS (2026-08-02): IMPLEMENTED — node-side code complete:
  - `events` table keyed (scope_type, scope_id, seq) + per-scope append
    lock + MAX(seq) restart continuation + replay-dedup unique index:
    GSM.Node/EventStream.vb (EventStreamStore, new file).
  - ParsedEventKind lifecycle members appended (enum end, append-only);
    ProcessManager emits all seven via EmitLifecycle (SourceId
    "lifecycle") — start/stop/exit/crash-restart/halt/adopt paths.
  - Ingestion rerouted: EventStore.ProcessLine appends an EventRecord
    per rule hit (SourceId "stdout"/"tailer"), then folds it via
    ApplyEvent (the former ApplyMatch, refactored to consume the
    record's field set — mechanically parity-preserving). /players,
    /server-state, /chat, /logs/recent handlers untouched.
  - New endpoints: GET /api/instances/{id}/events?since&max and
    /events/stream (SSE, replay-then-live), standard bearer middleware.
  - Retention: per-kind pruner in EventStreamStore (startup + 6h timer)
    with a MAX(seq)-per-scope guard so pruning can never cause seq
    reuse across a restart.
  - NodeVersionResponse.EventStreamSupported (additive, TLS-field
    pattern) + /api/version emits eventStreamSupported=true.
  - Hard-purge watermarks + POST /events/purge endpoint (see Node
    persistence section) — makes purges stick across replay, fixing
    the pre-stream "purged chat comes back on re-push" behaviour.
  - Roster hydration from the stream (follow-up, same day): on
    adoption, EventStore folds the persisted stream from the last
    ProcessStarted event onward to rebuild the current player list
    with ORIGINAL join times, before the tailer/ring-buffer replay
    runs. Pre-stream, the live roster was in-memory only and restart
    recovery depended on the 512KB log backfill window — join lines
    older than the window were lost, and lean-adopted instances got
    no roster at all until fresh activity. The ProcessStarted bound
    is the ghost guard: sessions cannot span a game-process restart,
    so nothing before it can still be online. Instances started
    under a pre-stream binary have no ProcessStarted in the stream;
    they fall back to the legacy log-replay rebuild until their next
    full restart.
  - Drive-by fix: RestartInstanceAsync read StartViaShimAsync's String
    result into a Boolean (Option Strict Off coercion) — every
    successful shim-mode crash-restart was mislabelled a failure and
    went CrashLoopHalted. Fixed to the shimErr-string contract; Q3
    depends on this path.

  Gate status: PASSED LIVE 2026-08-09 on the win-test node (LO test
  instance, via scripts/eventstream-gates.ps1):
  - Q2 PASS — /players byte-identical to the pre-slice baseline with
    a live resolved player connected across the deploy.
  - Q1 PASS — seq continued across a node-service restart (17 -> 18),
    no dupes, no gaps, pre-restart prefix stable (replay dedup held).
  - Q3 PASS — kill of the game exe produced the full lifecycle chain
    in the stream (ProcessExitedUnexpectedly code 1 ->
    CrashRestartScheduled 1/5 -> ProcessStarted new pid, Mode=Shim);
    client-side fold = Running with CrashCount=1, and the event's Pid
    matched the live process in the Manager UI. This also live-proves
    the shim crash-restart Boolean-coercion fix — pre-fix, that kill
    would have halted the instance and orphaned the game.
  (Earlier compile verification + SQL-simulation of the storage
  semantics, 2026-08-02, remain as recorded in the findings note.)

Slice 2 — REST poller + Palworld opt-in
  Generic poller host in node; IStatePollProvider in Contracts;
  PalworldPlugin implements it (players endpoint). Join/leave events
  flow with the server's log capture still dark.
  Q4: player joins/leaves Palworld → events within one poll interval;
      no phantom leaves across a poll failure or server restart.
  Q4b: with a second source artificially emitting PlayerJoin for the
      same instance, the authority map suppresses the duplicate and the
      suppressed counter increments (dedupe proven before ConPTY makes
      it real).
  Q5: Discord/Manager player list shows Palworld players with zero
      Palworld-specific code outside the plugin.

  STATUS (2026-08-09): IMPLEMENTED — code complete across all four
  layers, compile-verified (Contracts / Node / PalworldPlugin all 0
  errors). Live gates pending.
  - Contracts: StatePollResolveContext + IStatePollProvider
    (IGamePlugin.vb, additive interface per the revised async shape
    above); ResolvedStatePollSource / PollAuthKind / PollPayloadShape /
    EventSourceDiagnostics DTOs + StartInstanceRequest.PollSources
    (NodeApiContract.vb). ContractsVersion 3 -> 4 (new plugin-facing
    surface). ProtocolVersion unchanged — everything on the wire is
    additive.
  - Node: GSM.Node/StatePoller.vb (new) — StatePollerHost (DI
    singleton) + per-(instance,source) PollerRun loops. Hygiene rules
    implemented as specified: success-only diffing keyed on KeyField;
    poll failures NEVER synthesize leaves; one Custom
    source-degraded event after 3 consecutive failures + one
    recovered event (folds into ServerState.CustomFields as
    Custom_PollSource_<id>=degraded|ok); per-run baseline = restart
    debounce, and the first successful poll is roster-aware (no
    fabricated joins for players stream-hydration already restored).
    FieldMap payload keys matched case-insensitively (live API
    returns "iP"); PlatformPrefixField splits "steam_7656..." into
    Platform + PlatformUserId. Reserved SourceIds rejected
    (stdout/tailer/lifecycle). Poller touches the game REST API only.
  - Node authority gate: EventStreamStore.SetAuthority installs the
    per-kind owner map at register time (lifecycle kinds force-owned
    by "lifecycle"; plugin claims on them ignored with a warning);
    append drops non-owner events of owned kinds, counted per-source
    (Appended/Suppressed), surfaced at
    GET /api/instances/{id}/events/sources. Suppressed and
    watermark-purged events do NOT fold. Unowned kinds remain
    first-come-append.
  - Node wiring: EventStore.IngestSynthesizedEvent (poller entry
    point, same append+fold path as parse hits); ProcessManager
    installs authority + starts pollers at FinalizeStart and on
    adoption, stops them on process exit; PollSourcesJson persisted
    in InstanceSnapshots (additive column) so polling survives node
    restart + adoption Manager-offline (plaintext accepted, lean-adopt
    gap documented — both per the consequences list above).
  - Q4b offline harness: GSM.Node --eventstream-selftest — proves
    owner-appends-and-folds, non-owner suppressed (counter=1, not
    folded), lifecycle protection, unowned-kind first-come, leave
    folds. Exit 0/1.
  - Manager: generic resolution block in InstanceManager
    .StartInstanceAsync — TryCast plugin to IStatePollProvider, build
    StatePollResolveContext (Config + FetchInstanceFile via the
    existing node file endpoint), copy returned sources verbatim onto
    StartInstanceRequest.PollSources. Zero game-specific code — the
    Q5 shape.
  - Plugin: PalworldPlugin 0.3.0, requiresContracts 4. Implements
    IStatePollProvider; returns the single pal-rest source
    (http://127.0.0.1:{RESTAPIPort}/v1/api/players, Basic
    admin/{AdminPassword} resolved from PalWorldSettings.ini via
    FetchInstanceFile, 5s, PlayerArray at "players", KeyField
    PlatformUserId, FieldMap name/accountName/playerId/userId/ip,
    AuthoritativeKinds = PlayerJoin+PlayerLeave). Declines (returns
    Nothing) when RESTAPIEnabled=False or AdminPassword empty.

  Gate status: ALL PASSED 2026-08-09.
  - Q4b PASS — --eventstream-selftest on the deployed node binary,
    all 12 assertions ok, exit 0 (authority dedupe + counters proven
    offline, ahead of ConPTY making it real).
  - Q4 PASS — live Palworld join/leave produced PlayerJoin/PlayerLeave
    (sourceId=pal-rest) within one poll interval; no phantom leaves.
  - Q5 PASS — Palworld player visible in Manager UI player panel with
    zero Palworld-specific code outside the plugin.
  SLICE 2 DONE.

Slice 3 — Manager sync + projection migration
  Cursor sync service in Manager (pull + SSE); History/PlayerActivity fed
  from synced events; Discord bot moved to the single subscription;
  legacy Manager ingestion paths deleted once parity is verified —
  FOR STREAM-CAPABLE NODES ONLY (see mixed-fleet rule below).

  SCOPE REVISED 2026-08-09 (pre-implementation reality check of the
  Manager ingestion paths — same discipline as Slice 2):

  - Sync unit: per-instance pull loop (EventSyncService, new),
    /events?since cursor paging, 3s cadence — the same cadence class
    as the existing chat mirror. SSE live-push DEFERRED: the node
    endpoint has existed since Slice 1 and the Manager loop is
    written cursor-first, so SSE is a drop-in latency upgrade later;
    shipping pull-only removes a whole class of
    reconnect/channel-buffering bugs from the slice that has to
    prove exactly-once (Q6). [DELIVERED 2026-08-09 — the live
    phase is now SSE-first with the 3s pull loop retained as the
    bounded-window fallback; see the post-slice additions below.]
  - Cursor persistence: AppSettings KV row
    ("eventsync:cursor:{instanceId}") — NOT a new EF table, so no
    hand-authored migration. The cursor row is updated in the SAME
    SaveChanges as the projected rows — one transaction, which IS
    the exactly-once mechanism.
  - First connect to a stream-capable node seeds the cursor at the
    node's CURRENT head seq (new head=1 query param on /events) and
    backfills nothing: everything before that moment was already
    ingested by the legacy path — replaying from 0 would duplicate
    it (join/leave is never pruned, so "from 0" can be months).
    Gaps only backfill AFTER the cursor exists.
  - What syncs: PlayerJoin/PlayerLeave → PlayerActivity +
    PlayerSessions + the _activePlayers bucket + resolver
    write-through + utility-plugin publish + notifications. Event
    fields carry identity AT WRITE TIME (kills the legacy
    /players-race — leave rows now carry identity natively) and
    event UtcAt stamps the row (kills the wall-clock approximation;
    backfilled rows land at their true times). Persistence is
    UNCONDITIONAL (no bucket-gating like the legacy path — the
    stream is already replay-deduped and cursor-exactly-once; the
    bucket is maintained as a best-effort mirror, and legacy noisy-
    verdict gating does not apply to stream facts).
  - Backfill guardrail (per Automation §): notifications fire only
    in live mode AND for events younger than 5 minutes; catch-up
    batches never notify. Utility-plugin events publish for ALL
    applied events (programmatic consumers — matches the legacy
    resync-synthesis semantics).
  - Double-ingestion guard: while sync is active for an instance,
    StreamLogsInBackgroundAsync's parser dispatch SKIPS
    PlayerJoin/PlayerLeave (TileLoaded/Unloaded still dispatch) and
    the /players resync-synthesis pass is skipped entirely (cursor
    replay IS the catch-up). After catch-up the bucket is seed-
    merged from /players so pre-cursor sessions are represented.
  - What does NOT move this slice (all deliberate):
    * Chat stays on the /chat time-cursor mirror — node-side /chat
      is already a projection of the same stream, so the
      disagreement class is dead regardless of which Manager pipe
      carries it.
    * TileLoaded/Unloaded + SessionIdentity stay on the Manager-side
      imperative parser — SessionIdentity (realm:tile) is a parser
      concept; stream TileLoaded carries no realm scope.
    * Crash/lifecycle NOTIFICATIONS stay status-poll-driven
      (RefreshInstanceStateAsync) — firing them from synced
      lifecycle events too would double-notify.
    * Lifecycle rows in Manager History: DEFERRED —
      PlayerActivity.EventKind is join/leave-binary all the way
      through HistoryQueryService and the render layers; widening
      that model is display work, not sync work, and nothing in it
      is blocked by shipping sync first.
  - manager:identity: node gains POST /api/instances/{id}/events —
    PlayerIdentity kind ONLY, SourceId forced to manager:identity,
    routed through EventStore ingestion so the node's
    enrich-or-stash identity rules apply unchanged. Manager pushes
    from UtilityPluginHost.ContributeIdentity (the LoMyrealm path):
    after the resolver Observe, the contribution is mapped
    (GameId, SessionScope) → running instances whose
    GetCurrentSessionIdentity matches, and pushed to stream-capable
    nodes. Only PLUGIN contributions push — resolver observations
    that originated from node data must not boomerang back.
  - Contracts: INodeClient + GetEventsAsync / GetEventsHeadAsync /
    AppendIdentityEventAsync, AppendEventRequest/Response DTOs.
    ContractsVersion stays 4 (decided after implementation review:
    INodeClient is the Manager's own client surface — plugins
    neither implement nor consume it, and ContractsVersion governs
    plugin-facing types only). ProtocolVersion unchanged (all wire
    changes additive).
  - Legacy path DELETION is still a later step after a parity soak;
    this slice only gates the join/leave dispatch for synced
    instances.

  STATUS (2026-08-09): IMPLEMENTED — code complete per the revised
  scope above. Contracts / Node / PalworldPlugin compile 0 errors;
  the four touched Manager files pass a reference-partial compile
  with zero syntax errors (full Manager build is the VS gate).
  - Contracts: INodeClient + GetEventsAsync / GetEventsHeadAsync /
    AppendIdentityEventAsync; AppendEventRequest/Response;
    ContractsVersion stays 4 (nothing plugin-facing changed).
  - Node: EventStreamStore.GetHeadSeq; GET /events head=1 param;
    POST /api/instances/{id}/events (PlayerIdentity only, SourceId
    forced manager:identity, rides IngestSynthesizedEvent — replay
    dedup, purge watermarks, enrich-or-stash all apply).
  - Manager: EventSyncService (new) — per-instance pull loops,
    Inactive/Pending/Active state model, head-seeded first-connect
    cursor, catch-up-then-live, 3s cadence, self-cleanup +
    background-poll re-ensure; InstanceManager
    .ApplyStreamPlayerEventsAsync (transactional apply + cursor —
    the Q6 mechanism — with event-field identity, resolver consult,
    leave-time inheritance, bucket mirror, live-only notifications,
    utility publish); legacy join/leave dispatch + resync synthesis
    gated off while sync owns the instance; NodeHttpClient client
    methods; UtilityPluginHost.ContributeIdentity pushes plugin
    contributions to stream-capable nodes serving the scope;
    EventSyncService in DI.

  Gate status: PASSED 2026-08-09 — Q6: Manager closed through live
  join/leave activity; on restart History backfilled the missed
  rows exactly once at their true times, no duplicates across a
  further restart. Q7: panels/History agree. SLICE 3 DONE.
  Remaining before legacy-path deletion: parity soak on the live
  fleet (run both-capable for a while; watch for History
  divergence, duplicate rows, missed leaves).

  POST-SLICE ADDITIONS (2026-08-09, second pass — implemented,
  live gates pending):

  - SSE live phase (the deferred drop-in). EventSyncService's
    live loop now opens GET /events/stream?since={cursor} via
    INodeClient.StreamEventsAsync (new client member — NO
    ContractsVersion bump per the v4-comment precedent) and
    applies each event through the SAME transactional
    ApplyStreamPlayerEventsAsync path (cursor rides the
    SaveChanges — the Q6 mechanism, preserved per event).
    Catch-up stays pull-by-cursor; on stream drop the loop
    falls back to the original 3s pull for a bounded window
    (15s doubling to 5min on consecutive quick failures, reset
    by any healthy session), then retries SSE. Node-side the
    /events/stream loop now writes an SSE comment
    (": keepalive") after 15s of quiet, paired with a 45s
    Manager idle timeout so a silently dead TCP path is
    detected instead of hanging — additive and wire-compatible
    (SSE comments were always legal; older stream-capable nodes
    without it just recycle through the idle timeout, which the
    cursor makes harmless).
    Gate (pending live): join lands in Manager History/Discord
    <1s; killing the SSE connection mid-session falls back +
    reconnects with no lost or duplicated events across the gap.
  - Lifecycle rows in Manager History (the deferred display
    work). ApplyStreamPlayerEventsAsync now projects the synced
    lifecycle kinds (start/stop/crash/restart/halt/adopt) into
    PlayerActivity rows: EventKind carries the new lowercase
    kind strings (the column was designed string-typed for this
    — NO EF migration needed), PlayerName carries the
    "(server)" marker, DisplayName carries the human detail
    ("exit 3", "restart 2/5 in 4000ms", halt reason). No
    bucket, no resolver, no utility publish, and NO
    notifications ever (lifecycle notifications stay
    status-poll-driven — the double-notify rule holds, and
    backfilled lifecycle rows are silent by construction).
    HistoryFilter gains IncludeLifecycle — default FALSE so
    the player-centric consumers (Discord /lastseen + roster,
    snapshot replay, player-name dropdown) are untouched; the
    History window opts in with a new "Server events" checkbox
    (checked by default there) and renders the new
    TimelineRow.RowKind members with labels/colors. Discord
    render deliberately unchanged — its TimelineRow consumers
    are presence queries, not a timeline view (divergence from
    the old deferral note's wording, recorded in the findings
    doc).
  - Incident follow-ups #4 + #6 — see the incident doc
    (claude/Palworld_Incident_2026-07-28) for the amended
    follow-up text and implementation summary (OrphanReaper.vb
    on the node's crash-restart/halt paths; RedistStatus /
    RedistResults on InstallProgressResponse).

  Mixed-fleet rule (new Manager + old node): capability via /api/version —
  additive `EventStreamSupported` field (absent deserializes False, same
  pattern as the TLS fields) or ProtocolVersion threshold. Legacy nodes are
  consumed through the surviving /players, /server-state, /chat projections
  exactly as today — those endpoints are the fallback surface in BOTH
  directions, which is why wire-compat is non-negotiable. Legacy Manager
  ingestion stays alive as the per-node fallback until the fleet is
  upgraded. Degradations, accepted: legacy nodes contribute no lifecycle
  events to History; an upgraded node's stream starts at its upgrade moment
  (cursor from seq 0, no fabricated backfill). No fleet-wide flag day.
  Q6: Manager offline for an hour during activity → on reconnect,
      History backfills exactly once (no dupes), player list correct.
  Q7: History page and player panel and Discord panel agree with each
      other for the same session (the disagreement bug class dies here).

Slice 4 — ConPTY source (full plan: docs/plans/ConPTY_Plan.md)
  When the shim gains ConPTY, its output enters as SourceId="conpty"
  through the same parse-rule path. Zero downstream changes expected —
  that expectation is itself the acceptance test.

  STATUS (2026-08-09): IMPLEMENTED through C4a (compile-verified in
  sandbox; live gates pending). Shim protocol v2 adds the ConPty
  pseudoconsole spawn (CreatePseudoConsole) + a pty-ctrlc stop kind;
  the node resolves SpawnStrategy.ConPty from the new additive
  LaunchOptions/StartInstanceRequest.NeedsPseudoConsole boolean
  (ContractsVersion stays 4 per the v4-comment precedent), strips VT
  node-side (VtConsoleFilter, stateful across frames), and the ONLY
  ingestion change was the predicted one: ProcessLine's sourceId
  argument ("conpty"). PalworldPlugin 0.4.0 opts in and drops its
  inert-FileLogSource strategy hack; its parse rules (C4b) are
  authored AFTER live captures. The acceptance expectation held at
  implementation time: zero changes in any downstream consumer
  (Manager sync / Discord / folds / /events). Gates — G1 live console
  text + stop/crash-restart/adopt (Q1/Q3-class), G2 sourceId=conpty
  parse hits with zero downstream changes, G3 pal-rest authority
  suppressing conpty join/leave duplicates in /events/sources (the
  live Q4b) — tracked in the ConPTY plan.

## Automation & installations (design direction — build in later slices)

The stream turns the rule engine's trigger model into event-pattern
subscription: kind + field predicates + optional windows ("3 crashes in
10 min", "player count 0 for 30 min"). Lifecycle kinds make whole rule
classes possible that automation cannot see today.

Plugin extensibility, three tiers:
- Triggers: FREE. Plugin parse rules already emit Custom events with
  named fields; a trigger on Custom + those field names IS a
  plugin-defined trigger. Only editor UX (field discovery) is needed.
- Conditions: predicates over event fields + the current-state folds as
  queryable context. No new contract.
- Actions: new additive contract — IPluginActionProvider: plugin
  declares action descriptors (id, param schema via
  ConfigFieldDescriptor, scope), node executes (Palworld:
  announce/kick/save via its REST API). ActionEditorFactory already
  renders dynamic editors.

Guardrails (non-negotiable, designed in from Slice 1):
- Backfill never fires actions. Synced-gap events are marked backfill;
  each trigger carries FireOnBackfill (default False; notifications may
  opt in, actions never).
- At-most-once per event: firings dedupe on (ruleId, seq) so Manager
  restarts / SSE reconnects cannot double-fire.
- Node-side crash policy remains special-cased node-enforced code, NOT
  a stream-consuming rule — it must work with everything else down.

Installations: stream key generalizes from InstanceId to
(ScopeType, ScopeId) — Instance | Installation | Node — IN SLICE 1,
even though only Instance is used at first (retrofitting a key is
misery). Later slice: InstallRunner steps, SteamGuard WaitingForInput,
redist results, and version-check "update available" become events —
then "update available -> install -> restart instance" is just a rule
instead of bespoke orchestrator glue.

## Non-goals

- No filesystem watching in this plan. Standing policy for any FUTURE
  watcher (decided 2026-07-30): targeted only — explicit whitelisted
  directories or single files, never recursive over a broad root, and
  never anywhere under SaveGames / instance-managed data dirs. Blanket
  content-root watching is what caused the July incident.
- No change to plugin log-source declarations or parse-rule semantics.
- No Manager-side canonical store: the node owns truth; the Manager owns
  a synced copy and views.
- No event replay ACROSS instances (per-instance streams only, for now).

## Open questions

- ~~Retention defaults per kind~~ — DECIDED 2026-08-02, see Node
  persistence section (join/leave=NEVER pruned, lifecycle=365d,
  chat=90d, state/identity/custom=30d).
- ~~Whether `/logs/recent` should become a projection~~ — DECIDED: stays
  separate (see Raw log feed section).
- Manager→node identity append: auth reuses the existing node token; is
  kind-restriction enough, or should the endpoint also be gated to
  Manager-registered sessions only?
- Seq exposure in Discord embeds for debugging (probably yes, hidden
  behind a verbose toggle).
