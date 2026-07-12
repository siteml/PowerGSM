# PowerGSM Backlog

Items deferred from current or recent phases. Not blocking, but worth
keeping visible so they don't get lost to chat history. Each entry
names the phase it surfaced in and a rough priority signal.

When picking one up: copy the entry into the relevant phase plan
doc (or open a new mini-plan), then delete from here.

---

## Hardening & QoL

### Extract per-game Discord panel context into a plugin interface

**Surfaced:** Stardew Valley plugin work, July 2026.
**Priority:** Medium. Third hardwired game case landed in the
Manager; the pattern should move behind the plugin boundary
before a fourth appears.

**Current state:** `DiscordBotPlugin.BuildContextLine` contains
a `Select Case` on GameId with per-game knowledge baked into
the Manager: LO reads `ServerStateResponse.TileName`, Factorio
reads server-settings.json + SaveFile from merged config (with
a 5-minute per-instance cache), Stardew Valley reads FarmName
from merged config + parses the season/day/year composite out
of `MatchState`. Violates Manager-interprets/plugin-owns-game-
knowledge.

**Target shape:** Opt-in side-interface (consistent with
`ILogParser`/`IModManager`/`ILaunchOptionsProvider` precedent),
e.g. `IPanelContextProvider` with a single method taking a
small context object the Manager assembles — merged
install+instance config dict + `ServerStateResponse` (+ maybe
a file-fetch delegate for Factorio's server-settings.json
read, with caching staying Manager-side) — returning the
display string. Manager falls back to no context line when the
plugin doesn't implement it. Migrate all three existing cases
into their plugins; delete the Select Case.

**Payoff beyond cleanliness:** plugins can expose
configurability (which fields to show, format strings) via
their own config schemas — impossible while the rendering
lives in the Manager.

**Pickup notes:** Additive interface in Contracts (no
ContractsVersion bump — consumers ship with it). Factorio's
cached server-name fetch is the fiddly part; keep the cache in
the Manager and pass the resolved value into the context
object rather than teaching plugins to fetch files.

### Conan world-stable identity

**Surfaced:** Phase 5g-2d planning, May 2026.
**Priority:** Low-medium. Improves Conan-specific identity
scoping in the IdentityResolver; not blocking the resolver
itself (which is designed to treat `SessionIdentity` as
opaque).

**Background:** Phase 5g-2d's IdentityResolver scopes player
identity by `(gameId, SessionScope, ...)` where `SessionScope`
is a plugin-emitted opaque string. For LO this is
`lastoasis:{realmId}` — the LO backend exposes a stable realm
identifier in log lines, so the scope tracks the actual world
regardless of which install hosts it. For Conan, the equivalent
stable world identifier is not exposed in log output; the plugin
falls back to `conanexiles:{installId}`, which is fine for the
common case but bleeds identity across worlds if an operator
swaps `game.db` files between worlds without moving installs.

The two ugly options forced by Conan's design:

- Accept install-scope bleed and document "run Purge & Rebuild
  after world swap" as the recovery story (current v1 default).
- Find a stable identifier inside `game.db` itself and have the
  Conan plugin read it at instance start, emitting as part of
  `SessionIdentity`.

**What to do:** Inspect a `game.db` file (Funcom Conan Exiles
server databases are SQLite) and look for:

- A `game_settings` or `server_settings` table with a UUID or
  unique persistent name
- A `worlds` or equivalent table with metadata (creation
  timestamp + world seed combo is stable across saves)
- A "server name" / "world name" field, even if user-editable
  (rename is rare; rename + game.db swap rarer still)
- Some kind of internal Funcom UUID stamped at world-generation

If a stable identifier exists, update the Conan plugin to read
it at instance start and include it in the emitted
`SessionIdentity` string (e.g., `conanexiles:{worldUuid}`).
Resolver behaviour is unchanged — only the Conan plugin's scope-
emission rule needs updating.

**Inspection result (2026-05-27):** No built-in stable world
identifier anywhere in `game.db`. Checked `dw_settings`
(migration-state key-value table, 8 rows, all game-version-stable
but not world-unique), SQLite pragmas (all zero), `static_buildables`
(reveals map name e.g. Siptah but not unique identity),
`characters.id` / `account.id` (world-local but content-dependent
and not a fingerprint). The two original ugly options stand, but
a better third option emerged:

**Option C: Synthesize-on-first-observation.** Conan plugin
reads `dw_settings` at instance bootstrap. If a row
`(name='gsm_world_uuid', value=<uuid>)` exists, use it. If not,
generate a fresh UUID and INSERT it into `dw_settings` before
Conan launches (we own the db file at that moment, no locking
concerns). UUID rides every save, every backup, every restore;
travels with the world data not the install (copy game.db to
another install → same UUID → resolver correctly recognises the
same world); differs across distinct worlds (new world = new
db = no row = fresh UUID). Minor edge case: rollback to a
backup predating the injection gets a fresh UUID on next
start, which is arguably correct since that rollback is
identity-discontinuous with the rolled-forward state.

This is the recommended v2 approach. v1 ships with install-scope
as the default until Conan plugin work is prioritised.

**Why deferred:** Implementation requires Conan plugin updates
plus careful db-write timing (only during instance bootstrap,
before Conan launches). The resolver design works correctly
with install-scope as a fallback, so this is a quality
improvement rather than a blocker.

**Pickup notes:** v2 implementation outline:
1. Conan plugin gains a pre-launch step: open `game.db` (use
   `Microsoft.Data.Sqlite`, already in the Manager's NuGet),
   `SELECT value FROM dw_settings WHERE name='gsm_world_uuid'`
2. If no row: `INSERT INTO dw_settings VALUES('gsm_world_uuid', <new-guid>)`, commit, close
3. Whichever path, emit `SessionIdentity` as
   `conanexiles:{worldUuid}` instead of
   `conanexiles:{installId}`
4. Handle migration: existing PlayerActivity rows for this
   instance with `SessionIdentity` of the old install-scope form
   need either a one-time UPDATE to the new scope value, OR a
   resolver-side alias entry so old and new keys collide into
   one identity. Easier: write a small migration helper that
   reassigns SessionIdentity for the historical rows the first
   time the Conan plugin detects it just made the transition.

### Conan authoritative identity hydration from game.db

**Surfaced:** Phase 5g-2d planning, 2026-05-27 (during the
world-identity investigation above).
**Priority:** Medium-high. Replaces the 5g-2c temporal-
heuristic / cid-stash workaround with authoritative server-side
data.

**Background:** Conan's `game.db` contains authoritative
player-identity mappings that the 5g-2c work had to reconstruct
from log patterns + timing heuristics. Specifically:

```
account.platformId  →  account.id  →  characters.playerId  →  characters.char_name
76561197986549670   →  1           →  1                    →  blingess
76561198023980280   →  2           →  2                    →  Gina
```

A JOIN of the two tables gives us Steam ID ↔ character name
directly, no log scraping or temporal heuristics required. The
cid-stash logic from 5g-2c becomes a fallback for the brief
window before the first db read, not the primary mechanism.

**What to do:** Conan plugin reads `account JOIN characters`
on instance start and periodically (every few minutes, or on
each `/players` poll). For each row, fires an Observe call on
the Manager's IdentityResolver with `(PlatformUserId=platformId,
CharacterId=characters.id, DisplayName=char_name)`. The resolver
merges with whatever it has, and consumers (Overview panel,
Discord display, History) get accurate identity from cold start
— no warm-up period required.

**Why deferred:** Depends on 5g-2d (IdentityResolver) shipping
first, and on the Conan plugin's instance-start hook gaining a
db-read step. Both are modest changes individually but make
sense to land together after the resolver exists.

**Pickup notes:** Read concurrency is fine — SQLite handles
multiple readers, and Conan only writes during gameplay
(periodic saves). Use `FileShare.ReadWrite Or FileShare.Delete`
as with the LO log tailer pattern. Polling cadence should match
the Overview panel's refresh (so observations land just before
the panel renders).

### Player-list ghost on misrouted connection

**Surfaced:** Phase 5g planning, May 2026.
**Priority:** Low. Requires deliberate misconfiguration to
reproduce.

**Repro:** Misconfigure an instance to listen on a port that
already belongs to another instance on the same node. Players
trying to connect to the misconfigured one end up routed to
the other.

**Symptom:** `EventStore` on the receiving instance captures
the *connect* event correctly, but the matching *disconnect*
doesn't clear the player record. The ghost player stays in
the in-memory player list until the instance restarts.
Observed as two connect/disconnect event pairs within ~2
seconds of each other.

**Suspected cause:** The disconnect line in this code path
emits a slightly different format than the standard leave
regex expects, OR the player record was attributed under a
key that the leave handler doesn't search. Needs targeted
log inspection during repro to confirm which.

**Why deferred:** Rare misconfiguration scenario. Also
unprovable to diagnose cleanly until Phase 5g-2 ships —
persistent tile state means log history is tile-labelled
rather than session-labelled, so the misrouted time window
can actually be found in the history view.

**Pickup notes:** Reproduce on a test realm, capture the
exact disconnect line from the misrouted connection,
compare against `LastOasisPlugin.GetLogParseRules` leave
regex. Fix is almost certainly a regex tightening or a
secondary leave handler in `EventStore`.

### Phase 5g-3 — richer LO actor-identity bridging

**Surfaced:** Phase 5g-2 scoping, May 2026.
**Priority:** Low-medium. Closes a residual coverage gap
in the Node's identity resolution chain; not blocking any
current feature.

**Background:** Phase 5g-1 + 5g-2 resolve LO player
identity by chaining `PlatformUserId` across three log-
line sources — login URL `Name=` parameter, the
`LogPersistence: Persisting <DisplayName>, UniqueNetId =
<Platform>:<PlatformUserId>` autosave tick, and the
`LogPersistence: Processing character update ... UniqueId,
CharacterId` world-travel/state line — plus chat lines as
a fallback `DisplayName` source on post-May-2026 LO builds
where `Persisting` fires only at departure. The persistent
`players` table provides cached `DisplayName` lookup by
`PlatformUserId` so returning characters resolve their
in-game name on the first event of a fresh session.

**Residual gap:** Short LO sessions where a player joins,
leaves before chatting AND before the autosave tick lands,
AND no prior session has cached their
(PlatformUserId → DisplayName) mapping. Activity rows for
those sessions carry `CharacterId` + `PlatformUserId` from
join/leave events but never resolve `DisplayName`,
rendering as the Steam handle in the History window.
Uncommon but possible on a chatty multi-player tile where
an idle observer disconnects within a few minutes.

**Hypothesis:** LO log lines emit a richer actor identity
surface than the three sources currently parsed —
specifically `Player_0_C` / `OasisPlayerController_0_C`
entity names and `{UUID}`-shaped `ActorGuid` fields. These
appear by-proxy alongside `CharacterId` in spawn/state/
despawn events, and may also appear in chat-context lines
or other event types that the current rule set ignores.
If so, a transitive identity graph keyed on any of
`{CharacterId, PlatformUserId, ActorGuid, PlayerEntity,
ControllerEntity}` would close the gap by giving more
pairs of events a shared bridge key.

**Why deferred from 5g-2:**
- 5g-2 is about Manager-side propagation of identity that
  the Node has already resolved. Improving the Node's
  resolution further is orthogonal.
- Need representative log samples from a populated realm
  with multi-session coverage to verify which fields
  actually appear in which lines and which co-occur in
  single events. Designing rules without that evidence
  risks designing for behaviour that doesn't exist or
  drifts across UE4 versions.
- The existing `Custom_*` capture-group harvest in
  `EventStore.HarvestCustomFields` plus the
  `PendingIdentitiesByCharacterId` /
  `PendingIdentitiesByPlatformUserId` deferred-binding
  pattern can absorb new bridge fields without contract
  changes — so 5g-3 won't require any rework of 5g-2's
  schema or wire shape.

**Pickup notes:**
1. Grep a representative LO log (populated realm, several
   sessions with mixed connect/disconnect/chat patterns).
   Catalogue every line that contains `Player_0_C`,
   `OasisPlayerController`, or a `{UUID}`-shaped
   `ActorGuid`. Note which other identity fields
   (`CharacterId`, `PlatformUserId`, `Platform`,
   `DisplayName`, `PlatformPersona`) co-occur in the same
   line.
2. Verify entity-index stability: does `Player_0_C` refer
   to the same character for the whole connection
   lifetime, or does the index rotate per tick? Same
   question for `OasisPlayerController_X_C`. If the
   indices are tick-local, they're not useful for
   bridging across event types; if they're connection-
   stable, they are.
3. If `ActorGuid` is the durable per-connection key,
   design new `LogParseRule`s that capture it from
   chat-context lines AND from join/identity lines. Add
   `PendingIdentitiesByActorGuid` stash to
   `InstanceEventState` mirroring the existing patterns.
   Extend `FindExistingSession` to try `ActorGuid` as a
   correlation key.
4. Test on a live realm: spin up the Node with the new
   rules, have a confederate join + leave without
   chatting on a tile with other active chatters, verify
   the History window shows the in-game character name
   for the silent player.

**Out of scope for 5g-3:** Anything that requires
Manager-side rework of `PlayerActivityEntity` columns or
history-rendering paths. 5g-3 is purely a Node-side
resolution improvement; downstream surfaces benefit
automatically once the Node resolves more sessions.

### ~~Phase 5g-2c — Conan Character ID line binding for silent players~~ — Shipped May 22, 2026

Closed by adding a `Character Spawn (Character ID →
CharacterId + DisplayName)` parse rule to
`ConanExilesPlugin.GetLogParseRules` plus a third stash path
in `EventStore.PlayerIdentity` for `(cid + display, no pid)`.
The handler uses a temporal heuristic
(`TryBindRecentSpawn`) to bind the spawn line directly to
the one session joined within the last 3 seconds with no
CharacterId; falls back to a cid-keyed stash when ambiguous.
`DrainPendingCidIdentity` extended to also apply
DisplayName from the stash; the ChatMessage handler now
calls it after `ApplyFields` so chatty-but-late-bound
players also drain cleanly. Solves the silent-player case
for the typical low-population server. Known limitation:
busy servers with concurrent joins where the heuristic is
ambiguous AND no chat fires — those rows still render as
the FLS handle.

### Hot-swap node binary without log-stream interruption

**Surfaced:** Phase 5g-1 testing, May 2026.
**Priority:** Medium-low until the project ships externally;
rises sharply once non-author users are running realms,
because today's update sequence is operationally painful.

**Today's pain:** Any node binary update requires:
1. Stop the node service.
2. Replace the binary.
3. Start the node.
4. Stop **every running instance**.
5. Start each instance back (so the manager re-pushes parse
   rules to the freshly-empty EventStore).

Between steps 1 and 5, log tailing stops, in-memory player
state is lost, parse-rule registrations are gone, and any
automation that depended on those signals is dark. For
production realms with players online this is a hard
maintenance window.

**Idea (revised May 12, 2026):** Manager-coordinated
swap, **not** node-internal hot-swap. The original
formulation here had the old node binary spawning an
interim node and handing off file descriptors and
in-memory state via a node-internal protocol. That's the
hard way. The manager already owns everything needed to
drive the swap from the outside:
- Knowledge of which instances are running on which node
- The plugin source that generates parse rules
- The wire path to push rules into the node
  (`StartInstanceRequest.LogParseRules`)
- Control over the node service lifecycle (Windows
  service control or HTTP-triggered graceful exit)

**Manager-driven swap flow:**
1. Operator stages the new node binary (uploaded to the
   manager, or fetched from a release URL).
2. Manager pushes the binary to the node host (over the
   existing authenticated admin channel, or via
   filesystem write if the node is local).
3. Manager calls a `/api/system/prepare-restart` endpoint
   so the node can flush in-flight work — commit pending
   SQLite writes, finalize chat persistence, drain RCON
   queues, close log-tailer file handles cleanly with
   their current positions checkpointed to disk.
4. Manager stops the node process (service stop or
   graceful HTTP exit, whichever the node host supports).
5. Manager swaps the binary on disk (old binary kept
   alongside as `.bak` for rollback).
6. Manager starts the new node.
7. Manager re-pushes parse rules for every still-running
   game instance via a `RegisterRulesForRunningInstance`
   operation — same payload shape as
   `StartInstanceRequest.LogParseRules` but without
   spawning a new process.
8. New node resumes log tailing from the checkpointed
   positions written in step 3.

**Per-game considerations:**
- **File-tailed games (LO):** Game process is independent
  of the node lifecycle; log file keeps being written
  through the swap. New node opens the file at the
  position checkpointed in step 3 and resumes — zero log
  loss as long as the checkpoint is recent. Worst case
  (no checkpoint, fresh tailer at end-of-file) drops a
  few seconds of in-flight lines.
- **Stdout-tailed games (Factorio):** Game process is a
  child of the node. When the node dies the child's
  stdout pipe closes and the game may error or hang.
  Two paths forward:
  - Spawn stdout-based games with their stdout
    redirected to a file (same model as LO), so they
    don't depend on the node staying alive to consume
    their output. Their parent process death still kills
    them on Windows unless detached, but at least the
    stream survives if they don't.
  - Or accept that stdout-based games require a true
    game restart during node update. Tolerable since
    they're the minority case.

**Why this beats the node-internal hot-swap design:**
- No interim-binary handshake protocol to design, version,
  and maintain.
- No `WSADuplicateSocket` / file-descriptor handoff.
- No in-process state migration between two simultaneous
  binaries.
- Manager already has the authoritative view of "what
  should be running" — if anything diverges, manager
  reconciles.
- Failure recovery is straightforward: if the new binary
  fails to start or rules fail to register, manager
  restores the `.bak` binary and restarts. The operator
  sees a clear failure with a known-good fallback.

**Design questions for when this becomes a real plan:**
- Authenticated binary push: what's the wire format and
  size limit? Probably chunked upload to a
  `/api/system/staged-binary` endpoint with a SHA-256
  verification before the swap.
- Service control on Windows: the manager needs
  permission to stop and start the node's Windows
  service. That's a privileged operation; how does the
  manager get the right to do it? (Service ACL grant
  during `install-service.bat`, or the node exposes a
  `/api/system/shutdown` endpoint that does it itself
  and the manager just calls that.) HTTP-self-shutdown
  is much simpler and avoids the privilege escalation
  question entirely.
- Cross-host nodes: a node on a remote machine
  (`10.5.5.242:8765` in the user's tree) doesn't have
  a local filesystem path the manager can write to.
  The binary push has to go over HTTP for any non-local
  node — reinforces the chunked-upload approach.
- Tailer position checkpointing: granularity vs. cost.
  Checkpoint every N seconds, every N lines, or on a
  flush trigger from prepare-restart? Probably the
  latter is enough for the swap use case, with periodic
  checkpoints as defense against node crashes too.

**Pickup notes:** ~~Land the "re-push parse rules to a
running instance" operation as a tiny standalone
improvement — valuable even without the full swap flow.
That removes the "stop+start every instance" step from
today's manual node update sequence and is the
foundational piece for the manager-driven swap.~~ —
**Endpoint + invocation shipped May 12, 2026** as
`POST /api/instances/{id}/parse-rules` on the node +
`INodeClient.UpdateParseRulesAsync` + automatic invocation
from `EnsureLogStreamAsync` on the Manager. See CHANGELOG
`[Unreleased]`.

**Scope correction (May 12, 2026):** the earlier framing
that this "removes the stop+start every instance" pain
was wrong. The rule re-push fires only when the Manager
is the side that restarted; in that case the node's
`ProcessManager._instances` still has the running
instances registered as Running and
`EnsureLogStreamAsync`'s Running/Starting branch is
taken. On a node restart — which is the actual scenario
that motivated this work — the new node process starts
with an empty `_instances` dict because nothing reads
the persisted `InstanceSnapshots` table on startup yet.
~~`GetInstanceStatus` returns `State=Stopped` for every
running game process, the Manager treats the instances
as down, and the rule push doesn't fire. So today's
manual node-update sequence is still effectively:

  1. Stop node service.
  2. Swap binary.
  3. Start node.
  4. Stop+start every running instance (kicks players).

The rule re-push DID land cleanly and IS useful for the
Manager-restart scenario, but the bigger end-to-end win
requires the next two pieces.~~ — **Closed May 13, 2026**
by the process re-adoption work below.

**Process re-adoption on node startup (the actual
blocker).** ~~Read `InstanceSnapshots` on node startup,
call `Process.GetProcessById` against each saved PID,
verify identity by comparing `proc.StartTime` to
`snapshot.StartedAtUtc` (small epsilon for kernel
resolution), recreate the `ManagedInstance` record
with the Process handle, attach the Exited event
handler, set state to Running, push into `_instances`.
Subtle bit: `InstanceSnapshots` currently doesn't store
everything `FinalizeStart` wires up (LogFilePaths,
ParseRules, ExePath, Strategy, etc.), so re-adoption
needs one of:
- **Extend the snapshot to store all of it.** Self-
  sufficient; node can recover without the Manager.
  Schema migration work, and rules end up duplicated
  between Manager plugin and node snapshot which can
  drift.
- **Manager pushes the recovery payload after
  attachment.** Define `POST /api/instances/{id}/adopt`
  that accepts the per-instance metadata. After the
  node re-adopts the PID, the Manager's next poll
  notices the new state, pushes the recovery payload,
  and the existing rule-push from May 12 carries the
  parse rules. Plugin stays the single source of truth
  for rules. Node can't recover on its own if the
  Manager is also offline at startup.

The second path composes more cleanly with what's
already shipped and matches the "Manager interprets,
Node executes" principle. Worth confirming the design
direction before implementation though — the
self-sufficient option has appeal for the
Manager-down-during-node-restart case.~~ — **Shipped
May 13, 2026** as the hybrid of both options. The
`InstanceSnapshots` schema gained nine recovery columns
(`ExePath`, `Arguments`, `WorkingDirectory`,
`LogFilePathsJson`, `ParseRulesJson`, `Strategy`,
`StdoutIsLog`, `RequiresConsoleIsolation`,
`LogTailerStartDelayMs`); `FinalizeStart` writes them
on every snapshot. New `ProcessManager.AdoptSnapshots`
runs synchronously at node startup before `app.Run()`,
verifies each saved PID via `proc.StartTime` match
(60-second tolerance against system-clock skew during
downtime), and rebuilds the `ManagedInstance` record
with the live `Process` handle, restored crash policy,
re-spun file tailers (resuming from saved
`TailerPositions`), and re-registered EventStore rules.
The manager-side rule re-push from May 12 layers on top
to reconcile any plugin rule drift that happened while
the node was down — plugin stays authoritative for
rules, node has enough to function without the manager.
See CHANGELOG `[Unreleased]` for the full write-up
including limitations (Strategy A stdout capture; env
vars not round-tripped).

**Today's node-update sequence** (actually, now):
stop node → swap binary → start node → instances
re-adopted automatically, manager reconciles rules on
next 3-second poll. Players stay connected. No operator
intervention beyond the binary swap.

**Next piece after re-adoption:** tailer-position
checkpointing semantics are already implemented in the
`TailerPositions` table (per-iteration writes with
fingerprint-protected resume), so file-tailed games
already survive a node restart cleanly for log
continuity — once the process is re-adopted, the new
tailer opens at the saved offset and streams the
backlog written during the gap. This piece is
essentially done; what remains is verifying graceful
shutdown flushes everything cleanly (currently it does
so opportunistically per-iteration, but a
`prepare-restart` endpoint could trigger an explicit
final write).

**Full manager-driven swap** comes together once
process re-adoption exists plus the `prepare-restart`
and `shutdown` endpoints. Each step has standalone
value and they compose into the full no-downtime
upgrade flow.

### Node attach/detach + config import/export/merge/split

**Surfaced:** Phase 5g-1 testing, May 2026.
**Priority:** Medium-low. QoL + multi-manager friendliness;
directly useful for the "throwaway test manager" workflow
we've been doing.

**Status (updated May 22, 2026):** Attach/detach toggle
shipped — single boolean (`NodeEntity.IsEnabled`,
repurposed from a vestigial field) gated at
`InstanceManager.FetchAllInstanceIds` +
`VersionCheckService.RunOnePassAsync` with a MainForm
context-menu toggle and grey-text-plus-"[detached]"-suffix
visual indicator on the tree. Closes pain point #1 below.
Export / import / merge / split remain pending.

**Motivation:** Several distinct pains today blend together:

1. A manager attached to a node it isn't actively using
   still polls it (status, players, etc.) at the regular
   refresh cadence. If the node is on a remote machine
   that's offline, the manager spams retries and surfaces
   noisy disconnect banners. There's no "pause this node
   without removing it" toggle.
2. Moving a node's config between manager DBs (test →
   prod, or sharing a node config with another operator)
   currently means re-creating the node entry by hand:
   address, auth token, installations, instances,
   credentials, automation rules.
3. Multiple managers can technically talk to one node
   (wire-protocol-wise it's just a shared API), but each
   manager has its own DB and there's no concept of
   "share this view". Merging or splitting node
   configurations between managers is manual.
4. Wire-protocol updates between manager and node could
   in principle be negotiated when a node is re-attached,
   but only if attach/detach is a thing the manager
   explicitly does.

**Idea cluster (each can stand alone, but they fit
together):**

- ~~**Attach/detach toggle.** A node entry can be in one of
  three states: attached (poll normally), detached
  (config retained, no traffic), or unknown (last-known
  state stale, needs probe). Re-attach triggers a fresh
  protocol handshake — useful hook for the Phase 5f
  versioning negotiation to land in if it isn't already.
  Vocab note: "attach/detach" reads better than
  "enable/disable" because it implies state preservation,
  and avoids the "connect/disconnect" overload with
  TCP-level connections.~~ — **Shipped May 22, 2026** as
  a binary attached/detached toggle (the third "unknown"
  state was dropped from the initial scope as YAGNI;
  re-attach today just resumes polling on the next
  iteration without an explicit handshake step).
- **Export node config.** Serialise a node entry (address,
  auth token, installations, instances, optionally
  credentials — with a flag for whether to include
  DPAPI-encrypted secrets, since they're per-user and
  won't decrypt elsewhere) to a portable file format.
  JSON is the obvious choice. Should also include
  automation rules and Discord panels associated with the
  node's instances.
- **Import node config.** Read a portable file back into
  a manager's DB. On conflict (same node name or address
  already exists): prompt for merge/replace/skip. Useful
  for the new-test-manager workflow specifically — export
  from prod manager, import into test manager, throwaway
  test work, then either discard the test manager or
  re-import its diffs back.
- **Merge two node configs.** If two managers have
  diverging views of the same node (different
  installations, different automation rules), provide a
  merge UI that picks the union, the intersection, or
  per-entry resolution. This is where it gets fiddly fast
  — might be worth deferring until there's a real
  multi-operator scenario, since hand-merging via
  export/import covers the immediate use case.
- **Split a node config.** Inverse: take one manager's
  view of a node and produce two manager configs (e.g.
  for handoff to another operator who'll manage only a
  subset of instances). Lowest priority of the cluster.

**Design questions:**
- DPAPI credentials don't survive export across
  user/machine boundaries. Export format needs a clear
  story: either omit and require re-entry on import, or
  re-encrypt with a passphrase the operator provides at
  export time and prompts for at import time.
- Auth token rotation: if two managers share a node's
  auth token and one rotates it, the other locks out
  silently. Worth a per-manager "node connection probe
  failed: regenerate token in node settings?" flow.
- Discord bot token collision: if both managers export
  Discord bot configs and import, they collide on bot
  token. Export should flag Discord configs separately
  and let the import either claim or skip them.
- Wire-protocol version mismatch on attach: ties into
  Phase 5f. The attach action is a natural place to
  exchange protocol versions and either succeed, warn,
  or refuse.

**Pickup notes:** ~~Start with the attach/detach toggle
alone — it's the smallest piece, addresses the polling
spam pain point directly, and lays groundwork for the
larger import/export work. Single boolean column on the
Node entity, a check at every poll site, a UI control in
the node context menu.~~ — **Shipped May 22, 2026.**

Export/import comes next as a single "share node
config" file format. Merge and split layer on top later
if demand justifies.

### History data purge: joins/leaves, chat, or both

**Surfaced:** Phase 5g-1 testing, May 2026.
**Priority:** Medium. Becomes important whenever the
identity model shifts (rename events, format migrations,
test-data cleanup) or when a player exercises a
right-to-be-forgotten request on a public realm.

**Motivation:** History rows accumulate under whatever
name/identity model was in effect when they were
written. After Phase 5g-1, join/leave rows on LO carry
the Steam persona (`site_ml`) and chat rows carry the
in-game display name (`site's character`). Same player,
two distinct display strings in the timeline. Even after
5g-2 closes the asymmetry, *existing* historical rows
still carry the pre-5g identity — leaving residual
double-tracking that confuses anyone reading the
history. Same applies to test cruft: a few hours of
test sessions leave a mess of joins/leaves and chat
that the operator wants to delete cleanly before going
live, without nuking the entire history.

**Capabilities needed:**
- **Delete activity (joins/leaves) only.** Targets
  `PlayerActivityEntity` rows. Useful for wiping name
  mismatches without losing the chat record (chat is
  often the more valuable historical artifact).
- **Delete chat only.** Targets `ChatMessageEntity`
  rows on the manager side AND `chat_messages` rows on
  the node side (the manager's mirror would otherwise
  refill from the node on the next poll). Useful for
  removing accidentally-typed sensitive content or
  pre-launch test chatter.
- **Delete both.** The atomic "nuke this slice of
  history clean" operation.

**Scope axes — these are design questions, not yet
decisions:**
- **Time-range scope.** All rows, rows older than X,
  rows in a date range. Date range is the most general;
  the others fall out as special cases.
- **Player scope.** All players, one player by
  PlatformUserId/CharacterId, or one player by name
  (with the cross-rename complication: "site_ml"
  matches old joins, "site's character" matches new
  chat — the UI needs to expose this so the operator
  can pick one or both names).
- **Tile/session scope.** All sessions, one session,
  all sessions on a tile.
- **Combinations.** Probably the natural UX is the
  History window's filter row already covering time +
  player + tile filters; the purge button operates on
  whatever the current filter selects. "Delete what I'm
  currently looking at" is a natural mental model.

**Two-side coordination for chat:**
- Manager's `ChatMessages` table is mirrored from the
  node's `chat_messages` SQLite. Deleting from the
  manager alone gets re-mirrored on the next chat
  history fetch. So a chat purge has to:
  1. Issue a `DELETE /api/instances/{id}/chat?filter=...`
     to the node (new endpoint needed).
  2. Delete the matching rows from the manager's
     `ChatMessages` table.
  3. Reset the `_lastChatTimestamp` cursor in the live
     chat panel so it doesn't try to re-fetch deleted
     rows.
- Activity (joins/leaves) is manager-side only, so its
  purge is a single DB delete.

**Confirmation UX:** Destructive operations on history
are easy to regret. The dialog should show:
- Row count that will be affected.
- The exact filter the purge is operating under.
- Whether the purge is activity, chat, or both.
- A typed-confirmation field (e.g. "type DELETE to
  confirm") for any purge that affects more than N
  rows or any all-time purge.
- Optionally an export-to-file step before deletion,
  so the operator has a recoverable snapshot if they
  change their mind.

**Design questions:**
- Soft delete vs. hard delete. Soft delete preserves
  the rows with a `DeletedUtc` column and the UI just
  filters them out, which gives an undo window. Hard
  delete is simpler but irreversible. Soft delete also
  plays better with the right-to-be-forgotten case if
  the deletion needs to propagate to backups (a soft-
  delete row can be hard-purged on the next backup
  rotation).
- Should purge events themselves leave an audit row?
  ("Operator X deleted 247 chat rows on 2026-05-12 at
  21:34 with filter Y.") Yes, almost certainly —
  otherwise a malicious or careless purge is
  invisible. Tiny `PurgeAuditEntity` table with the
  filter, count, timestamp, and operator identity
  (Discord user if invoked via slash command, manager
  user if from the UI).
- Slash-command surface: probably a `/history purge`
  command behind admin role, mirroring the History
  window's purge UX in the bot. Useful for remote
  cleanup without opening the manager.

**Pickup notes:** Start with the History window UI
side — add "Purge activity" / "Purge chat" / "Purge
both" buttons that operate on the current filter
selection, with the typed-confirmation dialog. Keep it
hard-delete initially since soft-delete adds a column
to two tables and a filter to every read path. Audit
table lands alongside. Slash command and soft-delete
refinement come later if the feature sees real use.

---

## Documentation

These came up during Phase 5g planning while discussing what
ships-before-sharing-with-others looks like. None block any
current code work, but they're all on the path to "this is
something a non-author can use."

### End-user documentation: Node and Manager setup & operation

**Priority:** High once Phase 5f (versioning & release) lands.
Blocking for any external user.

**Scope:**
- Node installation: prerequisites, service install
  (`install-service.bat`), nodesettings.json walkthrough,
  auth token generation, port and firewall notes.
- Manager installation: prerequisites, first-run flow,
  adding a node, connection troubleshooting.
- Adding an installation: SteamCmd credentials, install
  paths, common pitfalls (the trailing-backslash gotcha,
  Steam Guard, etc.).
- Creating an instance: per-game launch arguments, config
  fields, port assignment.
- Operating: status icons, log viewer, automation rules,
  Discord bot setup (cross-reference to Phase 5d operator
  setup flow).

**Format:** Markdown in a `docs/` subdirectory, or a single
`USAGE.md` at root. Screenshots for the Manager UI.

### Packaging: self-contained or dependency manifest

**Priority:** Medium. Annoyance until fixed.

**Current state:** `dotnet publish -c Release --self-contained
-r win-x64` is mentioned in PowerGSM_Reference.md as the
distribution path. Whether it actually produces a single
runnable binary or requires .NET runtime installed alongside
hasn't been verified in a fresh-machine scenario.

**Pickup tasks:**
1. Test publish output on a clean Windows machine (no .NET
   runtime installed). Does it run?
2. If not self-contained: document the required runtime
   version and where to get it.
3. If partial dependencies (VC++ redist, etc): list them.
4. Either way: add a "Troubleshooting: missing
   dependencies" section to the end-user docs above with
   error-message-to-fix mapping.

### Plugin authoring guide

**Priority:** Medium. Becomes important when external users
want to support games beyond LO and Factorio.

**Scope:**
- Conceptual overview: what is a plugin file, how is it
  loaded (Roslyn at runtime), how to test changes
  (hot-reload), file-per-game convention.
- `IGamePlugin` contract walkthrough: every member, what
  it means, when it fires.
- The opt-in feature interfaces: `IModManager`,
  `IConfigFileProvider` (when it lands), etc.
- LogParseRule authoring: capture group names that bind
  to DTO fields, the `ParsedEventKind` taxonomy, regex
  gotchas (the lowercased-named-group VS quirk noted in
  the reference doc).
- Install methods: SteamCMD vs direct download, manifest
  files, common-redist handling.

**Two commented template plugins to ship alongside:**
1. `_Template_SteamCmd.vb` — minimal SteamCMD-installed
   game plugin with every method commented explaining
   what it does, with example values for the simplest
   possible game.
2. `_Template_DirectDownload.vb` — same for a
   direct-download-and-extract game (like Factorio).
   Includes archive-extraction handling (SharpCompress)
   and the user-agent quirk.

Templates live in `GSM.PluginsSource/Templates/` or
`docs/plugin-templates/` — decide on placement.

### Public API documentation

**Priority:** Medium-low. Important if anyone ever wants to
build a third-party tool that talks to the Node directly.

**Scope:**
- Every endpoint on the Node REST API: URL, method,
  request/response shape, auth requirement, error codes.
- `INodeClient` contract as the canonical client surface.
- DTO reference: every type in `GSM.Node.Api` with field
  descriptions.
- Authentication: `AuthToken` header, generation, rotation.
- Wire-protocol versioning (cross-reference to Phase 5f).

**Format:** Either auto-generated from XML doc comments on
the DTO types (preferred — keeps doc in sync with code), or
hand-written Markdown reference. Likely both: auto-gen the
DTO reference, hand-write the endpoint catalog and the
"how to write a client" narrative.

**Pickup prerequisite:** Phase 5f (protocol versioning)
should land first so the docs can be tagged with the
contracts version they describe.
