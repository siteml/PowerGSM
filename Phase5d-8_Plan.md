# Phase 5d-8 — `/lastseen` slash command

## Status

`[shipped 2026-06-01]` — drafted 2026-05-30; corrected same day; shipped 2026-06-01 across four rounds (1 command shape, 2a identity-awareness, 2b-i scope filters, 2b-ii roster). Decisions
seeded from the 5d-7 wrap-up conversation and now settled (see
Decisions). **Correction (2026-05-30):** an earlier draft claimed
location wasn't persisted and made tile-source an open decision —
that was wrong. Tile/realm IS persisted (`SessionHosts.TileName`)
and already composed into a tile-first `SourceLabel` by
`HistoryQueryService` (the History window's backing service), so
`/lastseen` reuses that service: no schema change, no open
location decision. Implementation belongs in a fresh session.

Depends on **5d-7 (shipped 2026-05-30)** — `/lastseen` registers in
the `SlashCommandCatalog` and inherits the documented visibility +
permission model and the `/help` / Commands & Access surfaces with
no extra UI work.

## Goal

Add a `/lastseen` slash command that answers "when/where was this
player last seen" from the Manager's persisted player history, with
a selectable scope so the question fits the game (LO scopes by
realm/game, not instance; other games may scope by game or
installation). Operators-only. Output leads with human-meaningful
location (tile + realm) and falls back to friendlier IDs before
raw GUIDs.

## Data model (as built — confirmed 2026-05-30)

`PlayerActivity` (EF Core, SQLite; `AddPlayerActivity` +
`Phase5g2_PlayerActivity_Identity` migrations) is the system of
record:

| Column | Notes |
|---|---|
| `ActivityId` | PK |
| `SessionIdentity` | resolver session key; **encodes realm for LO** (resolver derives sessionScope per-game: LO → realmId, Conan/Factorio → installId) |
| `NodeId`, `InstanceId` | nullable |
| `TimestampUtc` | event time |
| `PlayerName` | raw observed name |
| `EventKind` | string ≤20 (Join/Leave/…) |
| `CharacterId`, `DisplayName`, `PlatformUserId` | identity columns (5g2) |

Indexes: `(PlayerName, TimestampUtc)`, `(SessionIdentity,
TimestampUtc)`, `(CharacterId)` — so "most recent row for player /
session" is a cheap indexed lookup.

`IdentityResolver` (singleton, Phase 5g-2d) maps piecemeal/typed
names to a resolved identity via union-find over alias keys
(PlatformUserId / CharacterId / PlatformPersona / DisplayName). It
hydrates from the most recent `PlayerActivity` rows at startup and
stays current via `Observe(...)`. This is the typed-name → identity
hop `/lastseen` needs, already solved.

**Retention (confirmed):** `PlayerActivity` is **not** time-pruned
— history stays. The only removal is a manual "nuclear" purge of
joins/leaves. (Chat *is* pruned, but `/lastseen` doesn't read chat.)
So `/lastseen` can rely on long-lived history. The only standing
risk is unbounded table growth; out of scope here (nuclear purge
exists as the escape hatch).

**Location is persisted and already composed.** Tile/realm does
not live on `PlayerActivity` — it lives on **`SessionHosts`**
(`SessionIdentity → InstanceId` over a `HostedFromUtc/UntilUtc`
window, plus a `TileName` column from the `AddTileNameColumn`
migration). `HistoryQueryService` already joins `PlayerActivity` →
`SessionHosts` → `Instances`/`Installations`/`Nodes` and runs each
row through an `ISourceLabelProvider` to build a `SourceLabel`;
for LO that is exactly `"{TileName} — {RealmName} — {Node}/{Install}"`
(the History grid's Source column). The same service also produces
the resolver-enriched `CharacterName` / `PlatformPersona` split. So
`/lastseen` does NOT resolve location or identity itself — it
reuses `HistoryQueryService`.

## Design

Smallest-first: 5d-8a is the command + scoping + gating + catalogue
row; 5d-8b is the output rendering (and the location-source
decision it depends on).

### 5d-8a — command shape, scoping, permission

**"Multiple argument versions" — not difficult.** DSharpPlus models
this with **optional options on one command** (parameters with
default `Nothing`), each with its own autocomplete provider; the
handler branches on what was supplied. No need for true overloads.
This keeps the 5d-7a catalogue intact — `/lastseen` stays one row.

Proposed signature:

```
/lastseen [player] [instance] [game] [installation]
```

- All optional; each autocompleted and visibility-gated to the
  guild via `GetInstancesVisibleInGuild`.
- **Scope filters are mutually exclusive** (instance / game /
  installation). Handler rejects >1 with a clear ephemeral message.
- Resolution:
  - **player given** → that player's last-seen; restricted to the
    scope filter if one was given, else across everything visible
    in this guild.
  - **only a scope given** → the most-recently-seen players in that
    scope (roster, capped ~15–20).
  - **nothing given** → ephemeral error asking for a player or a
    scope.
- Game/installation filters resolve to an instance set via the
  `Instances` table, intersected with the guild-visible set, then
  `PlayerActivity` is queried on `InstanceId IN (set)`. For LO the
  natural use is `game:` (or bare `player`), and the answer groups
  by realm via `SessionIdentity` — instance filtering is offered
  but pointless there, matching the operator's mental model.

**Permission:** `ServerOperator` minimum (matches `/players`; it
exposes who-was-where). Read straight from the catalogue entry.

**Considered alternative — subcommands** (`/lastseen player …`,
`/lastseen game …`): more Discord-idiomatic per-scope arg sets, but
needs a `<SlashCommandGroup>` class and the catalogue/`/help` to
represent subcommands. Heavier for no functional gain over optional
args. **Lean: optional args (Option A); defer subcommands** unless
the flat option list feels cluttered in practice.

### 5d-8b — output rendering

Reuse `HistoryQueryService`'s `SourceLabel` verbatim — it already
leads with tile + realm and falls back through node/install, and
it's the same string the History grid shows, so `/lastseen` and
History agree by construction. Player identity uses the service's
`CharacterName` / `PlatformPersona` (resolver-enriched), matching
`/players` and History.

A player's last-seen line is then: resolved name + `SourceLabel` +
relative time (`<t:unix:R>` from the row's `TimestampUtc`) + the
Join/Leave kind. No new location or realm-name logic; no schema
change. The earlier "tile isn't persisted" decision is withdrawn
(see the Status correction).

### Autocomplete + visibility

- **player:** distinct recent names from `PlayerActivity` (prefer
  resolved `DisplayName`), restricted to names seen on
  guild-visible instances; capped at 25 (Discord limit). New
  provider.
- **instance:** reuse the existing `InstanceAutocompleteProvider`
  (already guild-visibility-scoped).
- **game / installation:** providers querying `Instances` for
  distinct GameId / installations intersected with guild-visible
  set (same shape as the panel editor's `LoadDistinctGameIds` /
  `LoadInstallations`, but as slash autocomplete providers).
- Command body re-validates every supplied value against the
  guild-visible set (autocomplete suggests, doesn't constrain),
  exactly as `/players` does.

### 5d-7a catalogue integration (the payoff)

Adding `/lastseen` is: one `Const LastSeenName/LastSeenDescription`,
one `CommandEntry` row (`ServerOperator`, a VisibilityNote), and the
`<SlashCommand>` attribute on the handler. `/help` and the Commands
& Access dialog pick it up automatically; gating reads the tier from
the catalogue. No UI changes.

## Touch points

- **`GSM.Manager\Core\GsmSlashCommands.vb`** — new `LastSeenAsync`
  handler (+ optional-arg options & autocomplete attributes); new
  autocomplete provider class(es); new catalogue consts + `All` row.
- **`GSM.Manager\Core\HistoryQueryService.vb`** — add a focused
  read method for "most recent timeline row(s) for a player /
  scope" (or call `QueryTimelineAsync` with a tight filter and take
  the latest). This is the only data-layer work; the rows it
  returns already carry `SourceLabel` + resolved identity.
- **Autocomplete sources** — player names + scope values, scoped to
  the guild-visible instance set; reuse the service's existing
  metadata lookups (`GetKnownPlayerNamesAsync`,
  `GetKnownSessionsAsync`) where they fit.
- No schema/migration changes. No new location or realm-name
  plumbing.

## Decisions

Settled from the 2026-05-30 conversation unless marked *(lean)* or
*(open)*.

1. **Permission — ServerOperator+.** Matches `/players`.
2. **Scope is selectable via mutually-exclusive optional args**
   (instance / game / installation), implemented as optional
   options on one command — *not* subcommands. Subcommands deferred.
   *(lean)*
3. **LO scopes by realm/game, not instance.** Instance filter is
   offered generically but is a no-op-ish choice for LO; the answer
   groups by realm via `SessionIdentity`.
4. **player optional; scope optional; require at least one.**
   player+scope → scoped lookup; scope-only → recent-players
   roster; neither → error. *(lean — confirm the roster mode is
   wanted for v1)*
5. **Output leads with tile + realm, then node/install** — by
   reusing `HistoryQueryService`'s `SourceLabel` as-is, so
   `/lastseen` and the History grid render identical "where"
   strings (the service already avoids raw GUIDs when a friendly
   name resolves).
6. **Retention — none needed.** History is permanent (only a manual
   nuclear purge exists); `/lastseen` relies on that. No pruning
   work in this phase.
7. **No schema change; location & identity come from
   `HistoryQueryService`.** Supersedes the withdrawn "tile isn't
   persisted" open decision (see Status). Tile/realm is on
   `SessionHosts` and already composed into `SourceLabel`.

## Open sub-decisions / risks

- **Roster mode** (scope-only, no player) — confirm it's wanted; if
  not, drop it and require a player.
- **Identity span of the answer** — report last-seen across all of
  a player's resolved aliases (resolver union-find) vs. only the
  typed string. *(lean: use the resolver — it's why it exists)*

## Cross-references

- **Phase 5d-7 (shipped 2026-05-30)** — catalogue, `/help`, and the
  Commands & Access dialog all consume `SlashCommandCatalog`;
  `/lastseen` slots in as one row and inherits the lot.
- **Phase 5g-2d** — `IdentityResolver` and `PlayerActivity` identity
  columns are the backbone of the typed-name → identity → history
  lookup; `/lastseen` is largely a read surface over that work.
- **Phase 5k (player-list panel)** — shares the same player-history
  and identity plumbing (`HistoryQueryService`, `SessionHosts`, the
  resolver); `/lastseen` and 5k draw "where/who" from the same
  composed `SourceLabel`.
