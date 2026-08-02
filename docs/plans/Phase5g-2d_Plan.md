# Phase 5g-2d — Manager-side IdentityResolver

Design document for centralising player-identity enrichment on the
Manager so that DisplayName / CharacterId / PlatformUserId surface
consistently across every rendering surface (Overview player list,
History view, Discord output, future Discord player-list panel)
regardless of whether the Node's `/players` snapshot happens to
have the fields populated at the moment of the query. Closes out
the 5g-2 identity arc by promoting the Manager to system-of-record
for resolved identity. Read this first in a new chat; everything
below assumes the conversation is starting fresh.

---

## Status

Not started.

---

## Goal

Two motivations that arrived together in May 2026 testing:

1. **The Overview panel showed "site_ml" in its Character column
   while History (for the same player, same session) correctly
   showed "site's character".** Same source data, same player,
   different render. The History view got it right because
   `PersistPlayerObservationAsync`'s leave-time inheritance does
   a fallback DB lookup against older Joins. The Overview panel
   renders raw `/players` output with no equivalent fallback.
   When the Node's `/players` returns an empty DisplayName
   (timing race between `Login request` → `PlayerIdentity` event
   and `Join succeeded` → `PlayerJoin` event in the Node's
   EventStore — race observed on every cycle after Cycle 1 of
   2026-05-25's test), every consumer that doesn't have a
   fallback shows the wrong thing.

2. **Each new render surface re-implementing the fallback is
   the wrong direction.** Discord display is the next consumer
   (#2 in the current planning roadmap). After that comes the
   Discord player-list panel (#4) and `/lastseen` (#3). If each
   surface implements its own DB-lookup-then-fallback dance,
   they will drift; one will get the rules right and the others
   will lag. Centralising the lookup in one service that every
   consumer goes through means fixing the rules once fixes it
   everywhere.

Theme: graduate the Manager from "each consumer does its own
identity dance with the data it has on hand" to "the Manager is
the system-of-record for resolved identity, and every consumer
asks the same resolver for an enriched view of a `PlayerSession`
before rendering."

---

## The schizophrenic-keys problem

The non-obvious part of this design — and the part most likely
to go wrong if rushed — is the key model. Real-world identity
data arrives in pieces, from different sources, at different
times, and the cache needs to merge those pieces into one stable
identity record per actual person/character without fragmenting.

Possible signals the Manager observes for a single player, in
roughly the order they typically arrive:

- **`NotifyAcceptedConnection`** — only `RemoteAddress`. No
  identity content; useful for binding subsequent events to
  this physical connection but not for the resolver.
- **`Login request`** line — `PlatformUserId` (e.g., the Steam
  ID from `userId: Steam:UNKNOWN [0x19F...]` once that finally
  resolves on a future LO build) and `CharacterId` (e.g.,
  `1688850153415813`). No `DisplayName` yet, no
  `PlatformPersona` yet.
- **`Join succeeded`** line — `PlatformPersona` (e.g.,
  `site_ml`). The imperative parser fires `PlayerJoin` here and
  passes only this name.
- **Chat lines and other game-specific events** — may surface
  `DisplayName` (the actual in-game character name, e.g.,
  `site's character`) keyed to a `CharacterId`.
- **Node's `/players` snapshot** — composite blob that *may*
  have any subset of `PlatformUserId`, `CharacterId`,
  `DisplayName`, `PlatformPersona` populated depending on what
  the Node has resolved so far. The fields the Manager queries
  most are `PlatformPersona` and `DisplayName`.

If the resolver caches naively (one entry per key it sees), the
same actual person ends up with multiple records:

- One keyed on `PlatformPersona=site_ml` (from the
  `Join succeeded` line, no character info)
- One keyed on `(PlatformUserId=7656..., CharacterId=1688...)`
  (from the `Login request` line, no persona)
- One keyed on `DisplayName=site's character` (from a chat
  line, no persona, no UserId)

Each record carries partial information. Consumers looking up
by persona find the first record (no DisplayName); consumers
looking up by CharacterId find the second (no DisplayName);
nobody benefits. **This is the schizophrenia the user flagged
explicitly** and the design must prevent it.

### Solution: identity as a record carrying multiple alias keys

An `IdentityRecord` is a value object that accumulates whatever
keys and fields we've observed for one actual person/character.
The fields:

- `GameId` — required (e.g., `lastoasis`)
- `SessionScope` — required, **opaque to the resolver**.
  Derived from the plugin's `SessionIdentity` value (set on
  `PlayerActivity.SessionIdentity` by the imperative parser
  and exposed via the Node's EventStore on `/players`
  responses). The resolver does not interpret the structure
  of this value — it's a string used as a key. Different
  plugins use different strategies and can iterate
  independently of the resolver:
    - LO: `lastoasis:{realmId}` — backend-stable, survives
      tile reassignments
    - Conan: `conanexiles:{installId}` for v1, with
      documented bleed-on-world-swap behaviour. Conan's
      log output does not expose a stable world identifier,
      and inspecting `game.db` for one is a deferred task
      (see Backlog.md `Conan world-stable identity`)
    - Factorio: `factorio:{installId}` — Factorio has no
      in-game identity concept beyond Steam, so install
      scope is the right granularity; save-file migration
      between installs is documented as a Purge & Rebuild
      scenario
  Decoupling the resolver from per-game scope decisions
  means each plugin can iterate on its own scope rule
  without resolver changes, and the resolver design stays
  game-agnostic.
- `PlatformUserId`, `CharacterId`, `PlatformPersona`,
  `DisplayName` — all nullable; any combination may be filled

The cache stores `IdentityRecord` instances and indexes them
by a set of *alias keys*. Each alias key is a tuple of
`(GameId, SessionScope, KeyKind, KeyValue)` where `KeyKind` is
one of `PlatformUserId`, `CharacterId`, `PlatformPersona`,
`DisplayName`, and `KeyValue` is the observed string.

When a new partial observation arrives, the resolver:

1. Computes the alias keys present in the observation.
2. Looks each one up in the alias-index dictionary.
3. If two or more existing records match (different aliases
   from the observation point to different records), it merges
   them into one — union of all aliases, union of all fields
   (field-level conflict resolution discussed below).
4. If exactly one record matches, the observation is merged
   into that record (new alias keys added to the index, new
   fields filled in).
5. If no record matches, a new `IdentityRecord` is created and
   indexed under all alias keys present.

This is a small union-find structure with field merging on the
union operation. The merge step is what prevents schizophrenia:
the moment an observation arrives that connects two previously-
separate records (e.g., a chat line containing `DisplayName`
matched against a `CharacterId` already in the cache, where the
`CharacterId` previously had no `DisplayName` — that chat line
both fills in the missing field AND, if a separate record was
already keyed on the same `DisplayName` from a different
source, merges them).

### Field-level conflict resolution

When merging two records (or applying a new observation to an
existing record), most fields are write-once: if the existing
record has a non-empty value, keep it; otherwise take the new
one. The exceptions:

- **`DisplayName`** — characters can be renamed in some games.
  Take the newer observation always (newest-write-wins). The
  user explicitly called this out as a feature: "if someone
  actually gets their character renamed, the node can update
  that information."
- **`PlatformPersona`** — Steam personas can change too. Same
  rule: newest-write-wins.
- **`PlatformUserId` and `CharacterId`** — never change for a
  given identity. If a merge attempts to set a different
  non-empty value than what's already there, that's a bug
  somewhere upstream; log a warning and keep the original.

"Newer" is determined by an observation timestamp passed in
alongside each observation. Default to `DateTime.UtcNow` when
no timestamp is supplied (live observations); History-backed
hydration uses each row's `TimestampUtc`.

---

## Design

### Service shape

A new service registered as singleton in `ManagerProgram`:

```vb
Public Interface IIdentityResolver
    ''' <summary>
    ''' Apply an observation to the cache. May create a new
    ''' record, merge into an existing one, or fuse two
    ''' previously-separate records that turn out to be the
    ''' same identity. Thread-safe.
    ''' </summary>
    Sub Observe(gameId As String, sessionScope As String,
                observation As IdentityObservation)

    ''' <summary>
    ''' Return an enriched copy of the passed-in PlayerSession,
    ''' with any fields the cache has resolved for the matching
    ''' identity filled in. The input session is not mutated;
    ''' the returned session is a new instance. If no record
    ''' matches any alias key from the input, the input is
    ''' returned unchanged.
    ''' </summary>
    Function Enrich(gameId As String, sessionScope As String,
                    session As PlayerSession) As PlayerSession

    ''' <summary>
    ''' Lookup by a single canonical key. Used by /lastseen and
    ''' similar commands where the caller has only a name to
    ''' work with. Returns the most-recently-touched record
    ''' that has the given key, or Nothing if no match.
    ''' </summary>
    Function FindByKey(gameId As String, sessionScope As String,
                       keyKind As IdentityKeyKind,
                       keyValue As String) As IdentityRecord
End Interface
```

`IdentityObservation` is a flat record carrying whichever fields
this particular observation supplies — same shape as
`IdentityRecord` minus the alias-key index. The resolver figures
out which aliases the observation contributes and routes the
merge.

### Hydration on startup

When the service is constructed, it scans the `PlayerActivity`
table for the most recent N rows per `(SessionIdentity,
PlayerName)` tuple where any identity column is non-empty,
and replays them as observations through `Observe`. This
warms the cache so the first render after Manager start
already has identity coverage for recently-active players.

"Recent N" needs a defensible default. The Manager DB will
accumulate years of player activity over time; scanning
everything at startup is wasteful. Recommend:

- All rows from the last 30 days, OR the most recent 5000
  rows, whichever yields fewer (pre-filtering in SQL with
  `ORDER BY TimestampUtc DESC LIMIT 5000`).
- Configurable later if real deployments hit limits.

Hydration is one-shot at startup; subsequent identity changes
come through write-through paths (below). The cache is not
periodically re-hydrated from DB.

### Write-through from PersistPlayerObservationAsync

`PersistPlayerObservationAsync` is where Manager-observed
identity events get persisted to History. After it writes a
`PlayerActivity` row with resolved identity columns, it calls
`_identityResolver.Observe(...)` to push the same data into
the cache. The cache and the DB stay in lockstep without a
poll loop.

### Write-through from /players responses

When `InstanceManager` fetches `/players` (background poll,
stream-restart resync, ad-hoc UI panel refresh), the response
sessions get passed through `_identityResolver.Observe(...)`
for each session with at least one non-empty identity field.
This is the path that catches the "DisplayName populated mid-
session" moment — e.g., when the Node's EventStore finally
resolves a CharacterId via a chat line after several minutes
of the session being identity-light.

### Read-through at every render surface

Each consumer of `PlayerSession` data calls
`_identityResolver.Enrich(...)` immediately before rendering:

- **Overview panel's `ApplyPlayers`** (UiPanels.vb ~2622)
  — runs each session through Enrich before computing
  `characterCol` / `platformCol`. The existing fallback
  logic stays as a final safety net but should rarely fire
  after this change.

- **Discord display formatting** (DiscordWebhookPlugin /
  DiscordBotPlugin's `/players` and future `/lastseen`
  command handlers) — Enrich before formatting the
  `(Steam: site_ml)` / `(Steam)` strings.

- **`PersistPlayerObservationAsync`'s identity-enrichment
  step** — currently does an HTTP call to `/players` and
  matches by PlatformPersona/DisplayName. After this phase,
  the lookup goes resolver-first; HTTP fallback only if the
  resolver has nothing. Closes the loop where History reads
  benefit from a fresher cache.

- **HistoryQueryService** (when it renders rows for the
  History window) — currently reads the persisted identity
  columns straight from `PlayerActivity` rows. Could
  optionally Enrich for rows whose stored columns are empty,
  giving the History view the same retroactive fill the
  Overview panel will now get. Probably worth doing; small
  query-side change, no schema impact.

### Thread safety

The resolver is hit from background polls (one task per
instance), the SSE callback thread, the UI thread (panel
refresh), and the Discord bot thread (slash command handlers).
All concurrent.

The internal data structures (alias-index dictionary, record
storage) are guarded by a single `ReaderWriterLockSlim`:

- `Observe` and `Enrich` that lead to writes (merge, new
  record creation) take the write lock.
- `Enrich` and `FindByKey` that hit existing records and
  don't mutate take the read lock.

Reader-writer is the right pattern here because reads will
dominate (every panel refresh, every render) and writes are
relatively rare (mostly on player join/leave). Single
`Object`-based `SyncLock` would also work and is simpler;
acceptable for v1 if the throughput proves not to need RW.

### Cache size and eviction

A typical Manager deployment sees a few thousand distinct
players across all instances over its lifetime. In-memory
storage for a few thousand `IdentityRecord` objects (each
maybe 200 bytes) is trivial — under 1 MB. No eviction needed
for v1; cache grows unbounded.

If a real deployment ever pushes into hundreds of thousands
of identities (large public servers, long retention), an LRU
with cap can be added without changing the interface. Not in
scope for this phase.

---

## Touch points (file inventory)

Files that will change:

- **`GSM.Manager\Core\IdentityResolver.vb`** (new). Service
  implementation, alias index, merge logic, hydration.
- **`GSM.Manager\Core\IdentityModel.vb`** (new). The
  `IdentityRecord`, `IdentityObservation`, `IdentityKeyKind`,
  and `IIdentityResolver` types. Could go in
  `IdentityResolver.vb` for fewer files but kept separate for
  symmetry with how other Core services are structured.
- **`GSM.Manager\ManagerProgram.vb`** — DI registration of
  the singleton; service-init order (the resolver must be
  available before `InstanceManager`'s background poll
  starts, before `ReconnectLogStreamsAsync`).
- **`GSM.Manager\Core\InstanceManager.vb`** — call sites:
  `PersistPlayerObservationAsync` (write-through after DB
  write; read-through to replace the HTTP fallback as the
  primary lookup); the background poll's `/players`
  consumer (write-through); `ResyncActivePlayersFromNodeAsync`
  (write-through, given /players is fetched anyway).
- **`GSM.Manager\UI\UiPanels.vb`** — `ApplyPlayers` calls
  Enrich before computing the column values.
- **`GSM.Manager\Core\DiscordWebhookPlugin.vb`** and
  **`GSM.Manager\Core\DiscordBotPlugin.vb`** — Enrich before
  rendering messages. (Confirm exact filenames during
  implementation — the bot plugin may have a different name.)
- **`GSM.Manager\Core\HistoryQueryService.vb`** — optional;
  Enrich rows whose stored identity is empty for retroactive
  fill in the History window. Decide during implementation
  whether to do this in v1 or defer.

Files that do NOT change:

- Any Node-side file. This is a Manager-only change.
- Database schema. No new tables, no new columns. The cache
  is in-memory; hydration reads existing `PlayerActivity`
  columns.
- `INodeClient` contract. No new endpoints.
- Plugin contracts (`IGamePlugin` etc.). Plugins don't see
  the resolver directly.

---

## Decisions (resolved 2026-05-27)

Each open question from the original draft, with its
resolution.

1. **SessionScope derivation for non-LO games — RESOLVED.**
   The resolver treats `SessionIdentity` as opaque. Plugins
   emit whatever value they consider stable per identity-
   context; the resolver keys its alias index on
   `(gameId, SessionScope, ...)` without inspecting the
   value's structure. LO emits `lastoasis:{realmId}` (current
   behaviour from 5g-2); Conan emits `conanexiles:{installId}`
   for v1 with documented bleed-on-world-swap (no stable
   identifier surfaces in Conan logs, and game.db inspection
   is deferred — see Backlog.md `Conan world-stable
   identity`); Factorio emits `factorio:{installId}` for the
   same install-scoped reason. See Service shape section
   above for the architectural framing.

2. **Factorio cross-install bleed — RESOLVED.** Install-scope.
   Each Factorio install gets its own SessionScope value
   derived from installId. Two installs hosting different
   maps for the same Steam user are treated as distinct
   identity contexts. Save-file migration between installs
   is a documented v1 limitation; the operator runs Purge &
   Rebuild if they want consolidated history after a save
   migration.

3. **Hydration scope: include rows from deleted instances? —
   RESOLVED.** Yes, but only within scope. Deleted-instance
   rows stay hydratable for `/lastseen` queries across
   instance lifecycles; cross-scope (different realm,
   different install, different game) rows do NOT bleed in
   because the alias index is scoped by
   `(gameId, SessionScope, ...)`. The scoping prevents the
   schizophrenic-keys problem AND prevents cross-context
   identity leakage in one mechanism.

4. **Identity for SessionHosts — RESOLVED.** No. Resolver
   stays player-focused. Existing `LookupOpenSessionHostIdentity`
   covers session/host tracking; no need to expand the
   resolver into that domain for v1.

5. **Diagnostic surface — RESOLVED.** Yes. Tools menu item
   `View IdentityResolver cache...` opens a read-only window
   showing the current record list with search-by-key.
   Minimal UI; primary use case is "why is this player
   showing up as X instead of Y" investigation.

6. **Persistence of merge conflicts — RESOLVED.** Log-only.
   `PlatformUserId` change attempts that conflict with
   existing records log a warning at the `Warning` level and
   are discarded (existing value wins). If conflicts turn
   out to indicate a recurring upstream bug, a dedicated
   diagnostic table can be added later without changing the
   API.

---

## Test plan

Three categories:

### Unit-style (offline)

- New record creation from a single-field observation.
- Merge when an observation supplies a new alias for an
  existing record.
- Fuse when an observation connects two previously-separate
  records.
- DisplayName newest-write-wins on rename.
- PlatformUserId change attempt produces a warning and does
  not overwrite.
- Empty/null fields don't pollute existing values.

### Integration (with a live Node)

- Cold-start hydration: Manager DB has player activity, no
  cache; on startup, Overview panel shows correct character
  names before any /players poll lands.
- Live join with full identity: Overview panel renders
  correct DisplayName immediately on first refresh after
  player joins.
- Live join with delayed identity: player joins with
  PlatformPersona only; chat message later supplies
  DisplayName; next /players refresh sees Enrich return the
  filled DisplayName; Overview panel updates without a
  refresh-cycle delay.
- Cross-instance same-player: same Steam user joins instance
  A then instance B in the same session scope; the resolver
  recognises them as the same identity and surfaces the same
  DisplayName.

### Regression

- Existing History rendering still works for old rows.
- Discord webhook notifications still fire with correct
  identity data.
- `PersistPlayerObservationAsync`'s leave-time inheritance
  still kicks in when the resolver has nothing (defensive;
  shouldn't need to fire often after this phase, but the
  code stays).

---

## Cross-references

- **Phase 5g-2** (`Phase5g_Plan.md`) — Established the
  identity-column infrastructure (`CharacterId`,
  `PlatformUserId` on PlayerActivity; History renders
  resolved DisplayName). 5g-2d completes that arc by
  ensuring the resolved values surface everywhere they
  should.
- **Phase 5g-2c** (in CHANGELOG / Backlog) — Conan silent-
  player temporal-heuristic / cid-stash. That stash is
  Conan-plugin-internal and tracks character IDs the Node
  has seen recently; orthogonal to the Manager-side
  IdentityResolver, but a candidate consumer if/when the
  Conan plugin needs to query Manager-side resolved
  identity.
- **Phase 5j** (`Phase5j_Plan.md`) — Purge & Rebuild. The
  rebuild path's `PrimePostRebuildCaches` will need to
  also re-warm the IdentityResolver after rebuild, since
  PlayerActivity is wiped and re-synthesised. Small touch
  point in the rebuild code.
- **Phases 5d-6 (Discord display) and 5d-8 (/lastseen)** —
  Both depend on this resolver landing first. 5d-6 has since
  shipped; after 5g-2d is in place, both are mostly view-layer
  work. (5d-7, surfacing the command model, also rides this.)
- **Phase 5k (player-list Discord panel)** — Same as
  above; the panel renders enriched sessions.

