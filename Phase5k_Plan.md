# Phase 5k — Player-list Discord panel

## Status

`[shipped 2026-06-01]` — drafted 2026-06-01. Seeded from the 5d-8
wrap-up conversation; design decisions below are settled
(Site-confirmed).

**Progress:** fully shipped 2026-06-01. 5k-1 (schema
`Phase5k_PlayerListPanel` + the `PlayerList` render branch + minimal
editor support) and 5k-2 (2a grouping-header bleed fix + underlined
node headers; 2b grouping carried into player panels; 2c `ShowJoinTime`
+ `ShowTotalInTitle` toggles, migration `Phase5k2c_PlayerPanelToggles`)
both shipped. The `/lastseen` presence-label refinement (active-now /
offline instead of joined/left) rode along in the same cycle. See
CHANGELOG.

---

## Goal

A persistent, auto-refreshing Discord panel that shows **who is
currently online** across a configurable set of instances — the
player-facing companion to the existing instance-manager panel. Same
posting/refresh model (one pinned message the bot edits in place), same
scoping, same per-guild visibility story. New surface, almost entirely
reused plumbing.

Example: an operator drops a panel in `#whos-online` scoped to
`game:lastoasis`; it shows each LO tile that has players, with the
online characters under it, refreshing every 30s.

---

## What already exists (and is reused wholesale)

The Discord panel system built across canonical 5d-1…5d-5 plus the 0.2.0
composition/grouping work already provides nearly everything:

- **`DiscordPanelEntity`** (`GsmDbContext.vb`) — one row per panel:
  `GuildId`, `ChannelId`, `MessageId` (the edited message), `DisplayName`,
  `ScopeKind` + `ScopeTargetId`, `RefreshIntervalSeconds` (default 60),
  `LayoutJson`, `GroupingKind` (default "None"), role-override columns,
  timestamps.
- **Scope** — `ScopeKind` ∈ {`allinstances`, `game`, `installation`,
  `instanceset`}; `instanceset` resolves against
  `InstanceEntity.InstanceSetTag`. **This is already the same primitive
  the automation engine's `RuleScope.InstanceSet` uses** — the
  "double-duty named bundle" the roadmap wanted is already in place, so
  there is no unification work. A player panel scopes exactly like an
  instance panel.
- **Refresh loop** — `RefreshLoopAsync` → `RefreshPanelAsync(p)` →
  `BuildPanelMessage(p)` → edit the Discord message. Kind-agnostic: it
  already just rebuilds whatever `BuildPanelMessage` returns on the
  panel's interval. `RequestRefreshAllPanels` + the event-driven
  `MatchesScope` push path (a join/leave on an in-scope instance pokes
  the panel) also already exist and are kind-agnostic.
- **`ResolveInScopeInstances(p)`** → `List(Of InScopeInstance)` — applies
  the scope and returns per-instance runtime (`State`, `PlayerCount`,
  `ContextLine`, `NodeName`, `GameId`, `NextRestart`, `DisplayName`). It
  **already calls `_instanceManager.GetPlayersAsync(e.InstanceId)`** to
  compute `PlayerCount`, so the per-instance player fetch path is
  already here.
- **Identity rendering** — the 5d-6 format
  (`character (Platform: persona)` / `persona (Platform)`) via the
  resolver. Players must be enriched through
  `InstanceManager.EnrichPlayers` (the same path the Overview panel and
  `/players` use — `GetPlayersAsync` stays raw by design), then formatted
  with the shared helper.
- **Pagination pattern** — the instance panel pages its Manage dropdown
  at Discord's 25-option cap with Page X/Y + prev/next buttons.

So 5k is **a new panel _kind_ that branches at `BuildPanelMessage`**, not
a new subsystem.

---

## Settled design decisions

1. **Reuse `DiscordPanelEntity`; discriminate by kind.** Add a
   `PanelKind` column (`"InstanceManager"` default / `"PlayerList"`).
   Existing panels default to `InstanceManager` and render byte-identically.
   `BuildPanelMessage` branches on it. No parallel entity, no parallel
   refresh loop.

2. **Reuse the existing scope mechanism unchanged.** `ScopeKind` /
   `ScopeTargetId` already cover all-instances / game / installation /
   instance-set. Nothing to add. (See "double-duty" note above.)

3. **Contents: currently-online players, grouped by instance, hide-empty
   by default.**
   - Online players only (live roster), aggregated across the scoped
     instances via `ResolveInScopeInstances` + per-instance
     `GetPlayersAsync` → `EnrichPlayers`.
   - **Grouped by instance** (each LO tile / instance is a group): a group
     header (instance display + its `ContextLine`/tile-realm context +
     `(N)` count) with the online characters listed beneath, rendered in
     the 5d-6 identity format.
   - **Empty instances (0 online) are skipped by default** to save space,
     with a per-panel **`ShowEmptyGroups` toggle** (default `False` =
     hidden) to show every in-scope instance regardless. Site-confirmed.

4. **Fixed layout for v1.** A player panel does *not* get the
   configurable element-composition system the instance panels have.
   One sensible fixed layout (group header + player rows). `LayoutJson`
   stays unused for player panels in v1 (or holds only the few
   player-panel settings if convenient). Revisit configurability later if
   wanted.

5. **Pagination: truncate for v1.** Discord embed limits (≈4096-char
   description / field caps) are handled by capping total length and
   per-group counts and appending "…and N more" rather than multi-page
   prev/next. Multi-page player panels are deferred (see Open).

6. **Refresh:** reuse `RefreshIntervalSeconds`. Roadmap default for player
   panels is 30s; existing entity default is 60. Set 30 on creation in the
   editor for player kind (don't change the column default).

---

## Schema change

One migration adds two columns to `DiscordPanelEntity`:

- `PanelKind As String = "InstanceManager"` — discriminator.
- `ShowEmptyGroups As Boolean = False` — the decision-3 toggle.

Both have safe defaults so existing panels are unaffected and the
upgrade is non-destructive. EF Core 8: `Add-Migration
Phase5k_PlayerListPanel`, then `Update-Database` (auto-applied at startup
via `Database.Migrate()`; keep creation manual for review per the usual
hygiene).

---

## Implementation rounds (smallest-first)

**5k-1 — Renderable, refreshing player panel (core).**
- Schema: `PanelKind` + `ShowEmptyGroups` columns + migration.
- `BuildPanelMessage` branches on `PanelKind`: existing path for
  `InstanceManager`; new `BuildPlayerListMessage(p)` for `PlayerList`.
- `BuildPlayerListMessage`: `ResolveInScopeInstances(p)` → for each, fetch
  + `EnrichPlayers` → build groups (header + enriched player rows in 5d-6
  format) → drop empty groups unless `ShowEmptyGroups` → assemble embed
  with truncation. No Manage button (read-only roster).
- Minimal editor support so a player panel is creatable and testable:
  a Kind selector on the panel editor, plus the scope / channel /
  refresh-interval / `ShowEmptyGroups` controls (reuse the existing panel
  editor controls; gate the layout-composition UI off for `PlayerList`).
- Reuses `RefreshLoopAsync` / `RefreshPanelAsync` untouched.
- **Testable:** create a `PlayerList` panel scoped to `game:lastoasis`,
  confirm it posts, lists online players grouped by tile, hides empty
  tiles, and refreshes on its interval + on join/leave push.

**5k-2 — Polish.**
- Per-group online count in the header; total online in the panel title.
- Grouping options if useful (the entity already has `GroupingKind`;
  decide whether player panels honor ByNode/ByGame or stay
  instance-grouped).
- Optional per-row detail (join time / online duration via the relative
  `<t:unix:R>` tag) if it reads well — evaluate against clutter.
- Empty-panel state (whole scope has nobody on): show a tidy
  "No players online" body rather than an empty embed.

  *Shipped (5k-2a/2b/2c): grouping carried into player panels with a
  three-level header scheme (underlined `## ` node / `### ` game / bold
  instance) — which also fixed a pre-existing bold-on-bold bleed in the
  instance panel's by-node-then-game grouping; per-row join time and
  total-in-title became per-panel toggles (`ShowJoinTime` /
  `ShowTotalInTitle`); per-group counts and the empty-panel "No players
  online" state landed in 5k-1.*

---

## Open questions (resolve during implementation)

- **Multi-page player panels.** If a busy instance-set routinely
  overflows the embed, revisit the v1 truncation decision and add
  prev/next paging (mirroring the Manage-dropdown pager). Deferred until a
  real overflow is observed.
- **Per-row detail.** Resolved (5k-2c): per-row join time is a
  per-panel `ShowJoinTime` toggle (default off), rendered as a relative
  `<t:unix:R>` tag from `PlayerSession.JoinedUtc`.
- **Grouping semantics for non-LO games.** Instance-grouping is natural
  for LO tiles; confirm it still reads well for Conan/Factorio (one
  instance = one server) — likely fine (one group), but verify.

---

## References

- `Phase5d_Plan.md` — canonical Discord bot/panel foundation (5d-1…5d-5).
- `DiscordBotPlugin.vb` — `BuildPanelMessage`, `ResolveInScopeInstances`,
  `RefreshLoopAsync` / `RefreshPanelAsync`, `MatchesScope`,
  `BuildPanelRenderItems`, the Manage-dropdown pager.
- `GsmDbContext.vb` — `DiscordPanelEntity`, `InstanceEntity.InstanceSetTag`.
- 5d-6 / 5g-2d (CHANGELOG) — identity rendering + `EnrichPlayers`.
- ROADMAP "Phase 5k" entry.
