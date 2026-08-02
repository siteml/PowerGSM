# Phase 5g — Player Identity Resolution & Node State Persistence

Design document for adding multi-name player identity resolution and
graduating the Node's in-memory event state to persistent SQLite-backed
state. Read this first in the new chat; everything below assumes the
conversation is starting fresh.

---

## Status

- **Phase 5g-1** — shipped May 12, 2026. Tested against
  a live Last Oasis realm and a fresh test install on a
  post-5g-1 LO build (which moved player-persisting log
  output to departure-only, prompting the
  chat-as-DisplayName-source fallback added late in the
  phase). Documented in `CHANGELOG.md` under
  `[Unreleased]`. Awaiting next release for version bump.
- **Phase 5g-2** — code complete May 20, 2026. The node
  side turned out to already be implemented (likely
  shipped quietly alongside 5g-1; the plan document
  wasn't updated to reflect it). The Manager side
  landed in this phase: `PlayerActivityEntity` schema
  additions, write-time identity enrichment in a new
  async `PersistPlayerObservationAsync`, a shared
  `IdentityFormatter` helper, and consumer-side
  rendering updates in `HistoryQueryService` +
  `GsmSlashCommands`. Awaits an EF migration run in
  Visual Studio Package Manager Console plus live
  testing. See **"What this phase actually shipped"**
  below for the delta vs. the original plan.
- **Phase 5g-2b** — code complete May 21, 2026. Conan-
  specific identity gap closure that surfaced during
  5g-2 live testing: the Conan `Join succeeded:` and
  `Player disconnected:` parse rules were capturing the
  FLS handle into the `DisplayName` group, polluting the
  session's DisplayName slot with platform-identity
  data. Renamed to `PlatformPersona` so the slot semantics
  match Last Oasis. Added a render-time chat fallback in
  `HistoryQueryService.LoadTimeline` to backstop edge
  cases (first-time-on-this-Node players, cross-Node
  joins where the players-table cache misses). See
  **"5g-2b: Conan-specific identity gap closure"** below.

---

## Goal

Three concerns, surfaced via diagnostic work against a live Last Oasis
realm in May 2026:

1. **Player identity resolution.** UE4 game/chat lines emit the
   in-game **DisplayName** (e.g. `andre(qc)`) while join/leave lines
   emit a **PlatformPersona** (Steam handle / Xbox gamertag, e.g.
   `andrekop`). These diverge whenever a player renames their
   character on myrealm. Without bridging them, the same player
   looks like two different people across event types.

2. **Persistent player records.** Even after identity is bridged
   correctly, the in-memory player state is lost on Node restart —
   and there's no historical record of who's played the realm
   before. Future features (`/lastseen` slash command, Discord
   "active players" panels, in-Manager cross-instance player lists)
   need persistent identity records as foundation.

3. **Persistent instance state.** The Node's per-instance match
   state (tile name, map path) is in-memory only. Node restart
   wipes it, leaving log-history labelled with raw session IDs
   instead of tile names. Same restart-amnesia problem as #2,
   same fix shape. Plus a related bug: on transition into
   `LeavingMap`, the tile name stays set when it should clear.

Theme: graduate `EventStore` from in-memory ephemeral to persistent
stateful, while shipping the identity-resolution capability that
motivates it.

---

## Honest assessment of current infrastructure

### What's reusable

- **`EventStore` (Node)** — already does in-memory player tracking
  and exposes `/players`, `/server-state`, `/chat` endpoints.
  Extending merge logic and adding persistence, not replacing.
- **SQLite on the Node** — already in use for chat messages
  (`chat_messages` table). Adding two more tables is trivial.
- **`LogParseRule` machinery** — already supports `PlayerIdentity`
  events with `Name`, `PlatformUserId`, `CharacterId`, `Platform`
  capture groups. New plugin rules slot in without contract
  changes to the rule system itself.
- **`PlayerSession` DTO** — exists, gets two new fields and loses
  one.
- **Plugin hot-reload** — LO plugin rule changes don't require a
  Manager restart.

### What's missing and needs to be built

1. **Multi-key identity merging in `EventStore`.** Current logic
   treats each event as carrying complete information. The new
   model has partial events: a `Persisting` line knows DisplayName
   and PlatformUserId but not CharacterId; a `Processing character
   update` knows PlatformUserId and CharacterId but not
   DisplayName. The merge logic needs to handle "match this
   partial event against existing records via any known key,
   create-or-update."

2. **DTO split.** `PlayerSession.Name` is ambiguous. Becomes
   `PlatformPersona` + `DisplayName`, ripping the bandaid in
   one pass (no compat shim — see D5).

3. **Persistence layer for players.** New `players` SQLite
   table on the Node. Upsert-on-event. History is preserved
   (both names retained — Xbox in particular has quirks
   where one name or the other ends up being visible in
   different contexts).

4. **Persistence layer for instance state.** New
   `instance_state` table on the Node. One row per instance,
   upsert on every state-changing event. Hydrated on
   `RegisterInstance`.

5. **Tile state clearing on match-state transitions.**
   Currently `TileLoaded` events set tile info; nothing
   clears it. New handler on `ServerStateChange`: transitions
   into `EnteringMap` or `LeavingMap` clear tile info;
   transitions into `WaitingToStart` or `InProgress` leave it
   alone (expecting a `TileLoaded` to follow).

6. **Last Oasis plugin rule additions.** Two new
   `LogParseRule`s plus a capture-group rename on the
   existing login rule.

---

## Diagnostic backstory (why these decisions, not alternatives)

The investigation that produced this plan ran through three paths
before settling on the one below. Kept here so a future reader
doesn't ask "why didn't we just use the myrealm API?" and have to
reconstruct the answer.

1. **Path 1: log parsing alone.** Initially appeared to fail —
   chat lines have DisplayName but no IDs; login lines have
   CharacterId and PlatformPersona but no DisplayName. No single
   log line at default verbosity bridged them.

2. **Path 2: myrealm API.** Investigated and disproven. The
   documented `myrealm Whitelist API` is whitelist-only —
   three endpoints (`/whitelist/getwhitelist`,
   `createwhitelistitem`, `deletewhitelistitem`). Probed
   ~25 undocumented endpoint candidates across GET and POST
   under the same `ApiKey` header; all returned 404. The
   Characters listing page on myrealm is server-rendered HTML
   behind a Steam OpenID session cookie — scrapeable in
   principle but requires either automating the Steam login
   flow (Steam Guard makes this hostile) or a paste-once
   cookie-capture UX. Both ruled out as too brittle for v1.

3. **Path 1 revisited and won.** Closer inspection of the LO
   log surfaced two `LogPersistence` lines that ARE at default
   verbosity and DO bridge the gap:

   - `Persisting <DisplayName>, UniqueNetId = <Platform>:<PlatformUserId>`
     — periodic auto-save tick, ~one entry per active player
     every 2 minutes. Bridges DisplayName ↔ PlatformUserId.
   - `Processing character update ... UniqueId = <PlatformUserId>,
     CharacterId = <CharacterId>` — fires on world travel and
     character-state updates. Bridges PlatformUserId ↔ CharacterId.

   Combined with the existing login rule (which carries
   CharacterId + PlatformPersona via the URL `Name=` parameter),
   the three lines form a complete identity chain over
   `PlatformUserId` as the join key. No external API needed.

   Multi-platform: confirmed `STEAM:` and `LIVE:` prefixes on
   `UniqueNetId`; regex captures the platform string into the
   existing `Platform` capture group.

Side benefit: dropping the myrealm API dependency means no
session-cookie management, no external service dependency, no
breaking-on-myrealm-redesign risk, and no API key storage.

---

## Resolved design decisions

### D1. Identity model: three-key join, deferred binding

Player records keyed by `CharacterId` (always known on join).
Two secondary indexes resolve partial events:

- `ByPlatformUserId: Dictionary(Of String, String)` —
  PlatformUserId → CharacterId. Populated by
  `Processing character update` events.
- `ByDisplayName: Dictionary(Of String, String)` —
  DisplayName → CharacterId. Populated by `Persisting`
  events once their PlatformUserId resolves to a CharacterId.

**Pending-identity stash** holds partial events that arrive
before their join. Concrete race window: a `Persisting` line
fires for a player whose `Processing character update` hasn't
been seen yet → the (DisplayName, PlatformUserId) pair is
stashed by PlatformUserId until the corresponding character
update line arrives, at which point the stash is drained onto
the player record.

Chat enrichment: chat lines emit DisplayName only. The handler
looks up `ByDisplayName` to attach CharacterId/PlatformUserId.
If no hit (chat fires before the first `Persisting` tick),
chat is emitted with DisplayName only; the next chat from the
same player (after Persisting lands) is fully resolved.

### D2. DTO split: PlatformPersona + DisplayName

`PlayerSession.Name` is removed. Replaced by:

- `PlatformPersona` — Steam handle / Xbox gamertag. Known
  immediately on join from the login URL `Name=` parameter.
- `DisplayName` — LO character name. Known after first
  `Persisting` tick (~up to 2 min lag from session start).

Both fields stored historically per player (D3) — Xbox
quirks mean one name or the other can end up being the
visible one in different contexts.

UI default for "who is this player": `DisplayName ?? PlatformPersona`.

### D3. Persistence: Node-side SQLite, restore on startup

Two new tables.

```sql
CREATE TABLE players (
    character_id            TEXT PRIMARY KEY,
    platform_user_id        TEXT,
    platform                TEXT,                  -- 'STEAM' | 'LIVE' | ...
    current_display_name    TEXT,
    current_platform_persona TEXT,
    known_display_names     TEXT,                  -- JSON array
    known_platform_personas TEXT,                  -- JSON array
    first_seen_utc          TEXT NOT NULL,
    last_seen_utc           TEXT NOT NULL,
    last_tile               TEXT
);

CREATE TABLE instance_state (
    instance_id   TEXT PRIMARY KEY,
    match_state   TEXT,                            -- 'EnteringMap' | 'InProgress' | ...
    tile_id       TEXT,
    tile_name     TEXT,
    map_path      TEXT,
    updated_at_utc TEXT NOT NULL
);
```

Both upserted on every relevant event. Restored on
`EventStore.RegisterInstance` so cross-restart continuity is
seamless to the Manager.

### D4. Tile state clearing on match-state transitions

- Transition into `EnteringMap` or `LeavingMap` → clear
  `TileId`, `TileName`, `MapPath` in EventStore and persisted
  row.
- Transition into `WaitingToStart` or `InProgress` → leave
  tile fields alone; expect a `TileLoaded` event to populate
  them next.
- Transition into other states (none currently defined,
  future-proofing) → no change.

Closes the "instance shows stale tile name after deactivation"
bug from the diagnostic conversation.

### D5. Rip the bandaid: no `Name`-property compat shim

`PlayerSession.Name` is removed in one pass — no
backwards-compatibility shim, no temporary alias property.
Every consumer in Contracts/Node/Manager/Plugins gets touched
in this same change. The surface is small (single dev, no
external consumers), so cleanliness wins over disruption
avoidance.

### D6. All plugin updates ship in lockstep with the contracts split

Per Site's preference ("update plugins in one go whenever
possible"), the plugin source changes for **both** LO and
Factorio ship in the same phase as the contracts change.
This avoids a silent-broken-plugin window between phases
where the old `Name` capture group would compile but emit
events with no name attached (`Name` no longer maps to any
DTO field).

Concretely this means 5g-1 is the bigger phase (contracts +
EventStore merge + all plugin updates + Manager UI rename)
and 5g-2 is the persistence-only phase. See "Proposed phasing"
below.

---

## Clarifying questions

All resolved during the planning conversation. Sequence kept
so the next chat doesn't re-litigate:

- **Q. DTO shape — keep `Name` and add `DisplayName` alongside, or split fully?**
  → D2. Full split.

- **Q. `Name` deprecation cycle?**
  → D5. Rip it.

- **Q. Tile state across Node restart — backward log scan or persistent value?**
  → D3. Persistent value (faster, more reliable, falls naturally out of the player-persistence work).

- **Q. Persistent player table now or later?**
  → Now (D3). Table is a natural by-product of the merge logic; deferring it would create rework when `/lastseen` and Discord identity panels arrive.

- **Q. Plugin updates split across phases or in one go?**
  → D6. One go. All plugin source changes ship in 5g-1.

- **Q. Misrouted-port ghost-player bug — fix here or defer?**
  → Defer. Lives in `Backlog.md`. Diagnosis will be easier once 5g-2 ships (persistent tile state means log history is tile-labelled, making the misrouted time window actually findable).

---

## Proposed phasing

### Phase 5g-1: Contracts split + EventStore merge + plugin updates

The bigger phase. Everything that touches the contracts split
and the identity-resolution behavior ships together so there's
never a build with a broken capture-group mapping.

Scope:

- `GSM.Contracts`:
  - `PlayerSession`: drop `Name`, add `PlatformPersona`,
    `DisplayName`.
  - Capture-group list: add `PlatformPersona` and
    `DisplayName` to the supported names. `Name` is
    removed.
- `GSM.Node` (`EventStore`):
  - Three-index data structure: by CharacterId (primary),
    by PlatformUserId, by DisplayName.
  - Partial-event merge handler that locates existing
    records via any known key.
  - Pending-identity stash for events arriving before
    their join.
  - Chat enrichment: DisplayName → CharacterId lookup on
    every chat event.
- `GSM.Manager`: every consumer of `PlayerSession.Name`
  gets touched, switched to `DisplayName ?? PlatformPersona`
  (log viewer chat formatting, InstancePanel player list,
  AutomationEngine condition matchers, etc).
- `LastOasisPlugin.vb`:
  - Existing login rule: `?Name=(?<Name>...)` →
    `?Name=(?<PlatformPersona>...)`.
  - New rule: `^LogPersistence:.*Persisting (?<DisplayName>.+?),
    UniqueNetId = (?<Platform>\w+):(?<PlatformUserId>\d+)`
    → `ParsedEventKind.PlayerIdentity`.
  - New rule: `^LogPersistence:.*Processing character update.*
    UniqueId = (?<PlatformUserId>\d+),
    CharacterId = (?<CharacterId>\d+)` →
    `ParsedEventKind.PlayerIdentity`.
- `FactorioPlugin.vb`: rename its `Name` capture group on
  the existing player-join rule to `DisplayName`. Factorio
  doesn't have the divergence problem (Steam name and
  in-game name are the same), so the rename is purely
  mechanical — same data flows into the new field.

**Acceptance:** project compiles. Connect a player to a live
LO realm — within ~2 minutes (first auto-save tick) the
player list shows the in-game display name. Chat lines and
join/leave events line up under the same identity. Rename a
character on myrealm, reconnect — display reflects the new
name within two minutes. Factorio plugin continues to work
identically to before. Restarting the Node loses identity
state (expected; fixed in 5g-2).

### Phase 5g-2: Node persistence + tile state clearing + Manager-side identity propagation

Scope:

**Node side (state persistence):**

- New SQLite tables `players` and `instance_state` on the
  Node, initialised in `NodeDatabase` startup.
- `EventStore` upserts to both on every relevant event.
- `EventStore.RegisterInstance` hydrates `instance_state`
  from SQLite on first call per instance (and on Node
  restart while the instance is still running).
- State-machine handler clears tile info on `EnteringMap`
  and `LeavingMap` transitions.
- `EventStore.FindOrCreateSession` hydrates `DisplayName`
  from the `players` table on Login when a known
  PlatformUserId arrives — closes the "returning renamed
  character shows Steam handle on join until first chat"
  gap that survives 5g-1.

**Manager side (history identity propagation):**

- `PlayerActivityEntity` gains `CharacterId` and
  `PlatformUserId` columns. `PlayerName` stays (carries
  whatever name the join/leave log line carried at the
  time — Steam persona on LO, character name on
  Factorio) but is no longer the only identity surface.
- `InstanceManager.HandlePlayerJoin` / `HandlePlayerLeave`
  populate the new identity columns by looking up the
  current `PlayerSession` from the Node's `/players`
  endpoint at write time. Falls back to `Nothing` for the
  identity columns if the lookup misses (mirror not yet
  current, race with the join event, etc.) — `PlayerName`
  remains usable on its own.
- `HistoryQueryService.LoadTimeline` rendering for
  activity rows: prefer the resolved `DisplayName` from
  the `players` join (lookup by `CharacterId`) over the
  raw `PlayerName` column. Same `DisplayName ??
  PlatformPersona` coalesce as the live player list, so
  the History window and the InstancePanel render the
  same name for the same player.
- One-shot migration on first 5g-2 startup: for existing
  `PlayerActivity` rows where `CharacterId` is null,
  attempt to backfill from `ChatMessages` matching by
  `SessionIdentity` (single-player-session attribution
  only — multi-player sessions left unbacked). Best-effort
  cleanup of pre-5g-2 cruft without introducing the
  render-time guesswork rejected in 5g-1.

**Acceptance:** stop and restart the Node service while an
instance is running. Manager polls `/server-state` — tile
name returns correctly. Players visible before restart are
still visible after (with last-known state). Tile name
clears in real time when the server enters
`LeavingMap`/`EnteringMap`. A renamed character
reconnecting shows their in-game name on join (not the
Steam handle). The History window's join/leave rows
display the same name as the live player list — no more
`site_ml` vs `site's character` split for the same
person. Re-test against both LO and Factorio realms
(Factorio remains a no-op for the rename case since
PlatformPersona == DisplayName there).

---

## What this phase actually shipped

Written post-implementation, May 20, 2026. Captures the
delta between the original 5g-2 plan above and what
actually landed in code. The original section is kept
intact above for historical context.

### Node side: turned out to be already done

The scoping conversation discovered that every node-side
bullet in the original 5g-2 plan was already implemented:

- `players` and `instance_state` SQLite tables present
  in `EventStore.EnsureStateTables`.
- `PersistPlayer` upserts on every PlayerJoin /
  PlayerIdentity / PlayerLeave / ChatMessage with
  COALESCE-merge semantics.
- `PersistInstanceStateSnapshot` upserts on
  ServerStateChange / TileLoaded with full-current-state
  semantics.
- `LoadInstanceState` + `RegisterInstance(...,
  hydrateState:=True)` hydration parameter; `ProcessManager.TryAdoptOne`
  passes `hydrateState:=True` so adopted instances
  inherit prior tile/match state immediately.
- Tile fields clear on `EnteringMap` / `LeavingMap`
  transitions in the ServerStateChange handler — both
  in memory and persisted.
- `LookupPlayerDisplayName` cached-name lookup on
  PlatformUserId binding.

No node-side code changes needed in 5g-2.

### Manager side: implemented as planned, with three deviations

1. **`PlayerActivityEntity` gained `DisplayName` as a
   third column** (in addition to `CharacterId` +
   `PlatformUserId`). The original plan called for only
   two. Adding `DisplayName` as a snapshot column means
   History rendering is offline-safe — no need for a
   Manager-side mirror of the Node's `players` table or
   a render-time wire call to look up the current name.
   `HistoryQueryService` coalesces
   `DisplayName → PlayerName` via `IdentityFormatter` and
   that's it. Caps in `PlayerActivityEntityConfig` match
   `ChatMessageEntityConfig` for schema consistency:
   64 chars for the numeric IDs, 100 for DisplayName.
   New index on `CharacterId` mirrors the
   `ix_chat_character` index 5g-1 added to ChatMessages.

2. **One-shot backfill from `ChatMessages` was dropped.**
   The original plan called for a startup migration that
   walked old activity rows and attributed identity from
   single-occupant chat windows. On Last Oasis,
   `PlayerActivity.PlayerName` is the platform persona
   (Steam handle / Xbox gamertag) while
   `ChatMessages.DisplayName` is the in-game character
   name; those differ by default at character creation
   for nearly every player, not just after a rare myrealm
   admin name change. Name-equality matching across the
   two tables would only recover the edge case of players
   who happened to pick their Steam handle as their
   character name, at non-trivial false-positive risk on
   busier tiles. Decided: old rows render via
   `IdentityFormatter`'s fallback to `PlayerName`
   (unchanged from pre-5g-2 behaviour), new rows benefit
   from the snapshot columns going forward.

3. **Shared `IdentityFormatter` helper + Discord bundle.**
   The plan suggested `HistoryQueryService` should use
   the "same `DisplayName ?? PlatformPersona` coalesce
   as the live player list." To prevent the History
   window and `GsmSlashCommands.BuildPlayersResponse`
   from drifting in formatting decisions (they already
   had subtly different inline coalesces), the work
   bundled a new `IdentityFormatter` module in
   `GSM.Manager.Core` and switched both consumers to it.
   Discord visibility-profile gating (redacting
   `PlatformUserId` for non-admin viewers, admin-tier
   overrides) was scoped out as its own future phase —
   that's a policy decision worth focused review,
   distinct from a formatting helper.

### Architectural choice: write-time snapshot (Option A)

The scoping conversation surfaced an architectural
ambiguity in the plan's "prefer the resolved DisplayName
from the players join" wording — join against what,
exactly? Three interpretations were on the table:

- **Option A (chosen):** Write-time snapshot.
  `PlayerActivityEntity` carries the resolved
  `CharacterId`/`PlatformUserId`/`DisplayName` captured
  from the Node's `/players` endpoint at the moment the
  join/leave row is persisted. History rendering is
  local-only.
- **Option B:** Render-time live wire calls per row.
  Latency and offline-node problems.
- **Option C:** Manager-side mirror of the Node's
  `players` table with its own sync subsystem. Extra
  moving parts.

Option A won because (a) character names on Last Oasis
are chosen at character creation and are generally
stable over the character's lifetime — admin name
changes via myrealm are a rare edge case, not a routine
player action — so the snapshot is correct in nearly
every case, and (b) it matches `ChatMessageEntity`'s
semantics, which already snapshots speaker identity at
write time. History rendering uniformly uses snapshot
semantics across both row kinds.

### Files touched in code

- `GSM.Manager/Data/GsmDbContext.vb` —
  `PlayerActivityEntity` + `PlayerActivityEntityConfig`
  additions.
- `GSM.Manager/Core/IdentityFormatter.vb` (NEW) —
  shared coalesce helper.
- `GSM.Manager/Core/InstanceManager.vb` —
  `PersistPlayerObservation` split into a sync entry
  point + async `PersistPlayerObservationAsync` that
  does the wire call before the DB write.
- `GSM.Manager/Core/HistoryQueryService.vb` —
  activity-slice append routes through `IdentityFormatter`,
  populates `PlatformUserId` and `CharacterId` on
  `TimelineRow` for Join/Leave kinds; `TimelineRow` doc
  comments updated to reflect dual-kind population.
- `GSM.Manager/Core/GsmSlashCommands.vb` —
  `BuildPlayersResponse` switched to `IdentityFormatter`.

Follow-up work captured in **Phase 5g-3** entry in
`Backlog.md`: investigate richer LO actor-identity
bridging via `Player_0_C` / `OasisPlayerController_0_C` /
`{UUID}`-shaped `ActorGuid` fields to close the residual
gap where short sessions never bind `DisplayName` before
the player disconnects.

### 5g-2b: Conan-specific identity gap closure

Live-tested 5g-2 against a Conan Exiles instance and the
History window showed `losno420#72569` (an FLS handle)
on join/leave rows for a character whose chat lines
correctly showed `Gina`. Root cause: Conan's
`Join succeeded:` log line carries the FLS handle as its
post-colon token — NOT the in-game character name. The
plugin's parse rule was capturing it into the `DisplayName`
group, polluting the Node's `PlayerSession.DisplayName`
with platform-identity data until chat eventually fired
and overwrote it. The Manager's write-time snapshot at
join caught the bad DisplayName; at leave time it caught
either the bad value or (if chat had flipped it) found
no session match because `FindExistingSession` was
trying to match by DisplayName==FLS_handle while the
session's DisplayName was now "Gina".

**Plugin changes** in `ConanExilesPlugin.vb`:

- `Join succeeded:` rule capture group renamed from
  `DisplayName` to `PlatformPersona`. The post-colon
  token is structurally the platform-account identifier
  (analogous to Steam persona on LO), not an in-game
  character name. Renaming aligns the slot semantics:
  the FLS handle goes into `PlatformPersona` (stable for
  the session's lifetime), `DisplayName` is left for the
  character name to land later via chat or the Node's
  `LookupPlayerDisplayName` cache.
- `Player disconnected:` rule capture group renamed
  the same way. Symmetric with the join side and also
  fixes a latent bug: post-chat, the session's
  DisplayName had been flipped to the character name,
  so a leave event capturing the FLS handle as
  `DisplayName` would no longer match the session via
  the DisplayName key. Matching by `PlatformPersona` is
  stable across the chat-driven DisplayName updates.
- Comment blocks updated throughout: the connect/
  disconnect sequence doc lists, the chat rule's
  explanatory comment, the "What this plugin still does
  NOT detect" paragraph, and the standalone
  `ConanExilesLogParser` class header.
- Stale TODO removed ("refine ParseLine once real Conan
  logs are available") — the parser already does
  recognise the actual line shapes; that TODO predated
  the live testing.

**Manager changes** in
`GSM.Manager/Core/HistoryQueryService.vb`:

- New `ApplyChatFallbackDisplayNames` helper. For
  activity TimelineRows where the write-time snapshot's
  `DisplayName` was empty or equal to the raw
  `PlayerName`, AND `PlatformUserId` is populated, the
  helper batch-looks-up `ChatMessages.DisplayName` by
  `(SessionIdentity, PlatformUserId)` and overrides
  `TimelineRow.PlayerName` with the most recent chat
  DisplayName found. One indexed query per distinct
  (sid, pid) pair, leveraging the `IX_chat_pid` index
  from 5g-1.
- `LoadTimeline` activity-slice append updated to
  track rows-needing-fallback inline and invoke the
  helper before exiting the using scope.
- `TimelineRow.PlayerName` doc comment extended to
  describe the render-time chat fallback as a defined
  behaviour rather than implementation accident.

**Why no Character ID parse rule:** The Conan log emits
`ConanSandbox: Display: Character ID <n> has name <Name>
and guild ID <g>.` ~100-200ms after `Join succeeded:`,
and it'd be tempting to wire that up as a `PlayerIdentity`
rule. Investigation of `EventStore.vb` showed it'd be a
no-op: the `PlayerIdentity` handler has stash paths for
`(pid+display, no cid)` and `(cid+pid, no display)` but
not for `(cid+display, no pid)`. With no existing session
bound to the matching CharacterId at that moment, the
event would `FindExistingSession`-miss across all keys
and silently drop. Closing this gap requires a third
stash path on the EventStore side plus a heuristic to
drain it later — deferred to a follow-up.

**Live testing required after deploy:** the currently-
running Conan instance's in-memory session state is
bound under the OLD rules (PlatformPersona empty,
DisplayName=FLS_handle); rule updates flow through to
the Node via `UpdateParseRulesAsync` but don't re-
evaluate live state. Players whose sessions started
under the old rules need to disconnect and reconnect for
the new binding to take effect. Old History rows
(`losno420#72569`) stay as-is permanently — no backfill,
same rationale as 5g-2 dropped its backfill.

**Files touched in 5g-2b:**

- `GSM.PluginsSource/ConanExilesPlugin.vb` — parse rule
  capture-group renames + comment updates + stale TODO
  removal.
- `GSM.Manager/Core/HistoryQueryService.vb` — new
  `ApplyChatFallbackDisplayNames` helper, activity-slice
  append updated to feed it, `TimelineRow.PlayerName`
  doc comment extended.
- `Phase5g_Plan.md`, `PowerGSM_Reference.md`,
  `Backlog.md` — documentation.

## What this changes for existing functionality

- **`PlayerSession.Name` removal** is a breaking source
  change inside the project. Every consumer is updated in
  5g-1 in the same commit. No external API consumers exist
  yet.
- **Capture-group `Name` removal** affects plugin source.
  Both LO and Factorio plugin updates land in 5g-1 alongside
  the contracts split (D6).
- **`/players` and `/server-state` endpoint response
  shapes** gain fields. JSON responses are additive on the
  Node side (the new fields appear; consumers ignoring
  them work fine). No URL or method changes.
- **Chat persistence (`chat_messages` table)** is
  unaffected. Names are stored as-text per message;
  historical entries retain whatever name the log line
  had at the time.
- **AutomationEngine rule conditions** that filter by
  player name: any condition matching `Name` is updated
  in 5g-1 to match `DisplayName` (or coalesce
  `DisplayName ?? PlatformPersona` if the rule's intent
  was "any name this player goes by").
- **Discord bot panel** (Phase 5d work) reads from
  `/players` and `/server-state`. It automatically
  benefits from DisplayName once 5g-1 ships — no
  panel-specific changes needed, just the existing
  `DisplayName ?? PlatformPersona` coalesce in whatever
  the panel renders.

---

## Suggested first turn in the new chat

Paste this document. Confirm two facts before starting:

1. Grep `LastOasisPlugin.vb` for the existing login rule's
   regex; record the exact form so the rename is mechanical.
2. Grep `FactorioPlugin.vb` for any capture groups using
   `Name` (likely one rule); record so the rename doesn't
   miss anything.

Then start with **Phase 5g-1**: walk the `Name`-property
removal across all projects in dependency order
(Contracts → Node → Manager → plugins). Compile after each
project's changes. The compiler will surface every consumer
that needs touching — let the build errors drive the diff.
