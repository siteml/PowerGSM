# PowerGSM Roadmap

Forward-looking, ordered, statused view of where the project is
going. Companion to:

- **CHANGELOG.md** — past tense, what shipped in which version.
- **PowerGSM_Reference.md** — current state of the codebase,
  build patterns, gotchas.
- **Backlog.md** — deferred items with technical repro notes,
  organised by category. Items get pulled from Backlog into this
  roadmap when prioritised.
- **PhaseXX_Plan.md** files — design specs for individual
  phases. The roadmap links to them; it does not duplicate.

If you want to know **what's coming next**, this is the file. If
you want to know **what shipped and when**, CHANGELOG is the file.
If you want to know **how to do a thing in the current codebase**,
PowerGSM_Reference is the file.

---

## Status indicators

- `[active]` — currently being worked on
- `[design]` — plan doc written, code not started
- `[queued]` — agreed on the work, ordering set, no plan doc yet
- `[future]` — agreed in principle, ordering loose, no plan doc
- `[blocked: reason]` — work waiting on an external input

---

## Current focus

**Phase 5m — Manager resilience** is functionally complete: 5m-1 (tray
+ window-state persistence), 5m-2 safe mode (a/b core + 2c in-safe-mode
feature re-enable + 2d plugin enable/disable), 5m-2e (missing-plugin
detection + a hard start guard, added beyond the original plan after a
pluginless instance was found startable via a persisted `ExeOverride`),
and 5m-3 (watchdog auto-restart + start-at-sign-in via a per-user Task
Scheduler logon task). 5m-4 (true Windows-service install) stays parked
— the watchdog + logon task already deliver auto-restart and
start-on-sign-in without it. → `Phase5m_Plan.md`

**Phase 5l — Manager self-update** is now complete: 5l-1 (detect +
notify), 5l-2 (download + stage), and 5l-3 (apply) have all shipped.
5l-3 closed out with the full Apply pre-flight chain (downgrade guard
→ automation-in-flight warning → running-instances warning →
staged-contracts compat report), the `apply.cmd` binary swap +
`--post-update` relaunch (clean exit so the watchdog stands down), and
an update-history table viewable under Help → Update History. The
end-to-end swap is proven against a real published install; the
from→to history row will validate naturally on the first apply between
two builds that both carry the recording code. → `Phase5l_Plan.md`

**Phase 6 — Plugin GitHub source + manifest + updates** is now
complete: all four sub-phases (manifest model + parsing, source
registry, download + stage, install / updates / uninstall) shipped.
Plugins self-describe via an inline `' <plugin ...>` manifest
(legacy `RequiresContracts` comment still honoured), the official
source is seeded and browsable, and the whole acquire → stage →
consent → install → hot-reload pipeline works without a Manager
restart. The three plugin dialogs were consolidated into a single
tabbed **Tools → Manage Plugins** window (Status / Sources /
Updates). → `Phase6_Plan.md`

**Phase 7 — Manager-side utility plugins** shipped (June 2026):
the `IUtilityPlugin` surface, event subscriptions, and the
declared-capability permission model landed, validated end-to-end
by the lo-myrealm utility plugin across sub-phases 7-5b (Web
Sessions UI), 7-6 (multi-realm discovery / import), and 7-7
(multiple myrealm accounts + per-realm session failover). →
`Phase7_Plan.md`, `Phase7-7_Plan.md`

**Phase 8 — Node self-update + restart-survivable instances** is
complete and ships in **0.4.0** (the release gate). 8-1 (per-instance
`GSM.Shim` supervisor — the node never owns the game's stdio pipes, so
node restart/update/crash is a non-event for instances; all four
survivor paths verified, game PID unchanged across the bounce), 8-2
(node self-update: chunked Manager→node binary push + apply + survivor
swap/relaunch, with shim + NodeSetup co-update and a health-gate
auto-rollback), and 8-3 (shim rediscovery / `node.db` hardening) all
landed. Two runtime verifications ride along code-complete-but-unproven
into 0.4.0 — the slice-8 auto-rollback and the 8-3 rediscovery path —
flagged for a post-release live check. → `Phase8_Plan.md`,
`Phase8-2_Plan.md`, `Phase8-3_Plan.md`

**Next: Phase 9 — Node-side utility installations** (`[design]`) —
reuses 8-1's shim + 8-2's staged-binary channel. → `Phase9_Plan.md`

---

## Queued (ordered)

Next 6-8 phases. Roughly the order they'll be picked up; some
re-shuffling possible if a dependency or a bug pushes priorities
around.

### Phase 5m — Manager resilience `[shipped]`

Operational hardening so the Manager survives production
deployment unattended. All sub-phases shipped (June 2026): 5m-1
(tray), 5m-2 (safe mode core + 2c feature re-enable + 2d plugin
enable/disable), 5m-2e (missing-plugin detection + start guard), and
5m-3 (watchdog auto-restart + start-at-sign-in via a per-user Task
Scheduler logon task). 5m-4 (true service install) is parked — the
watchdog + logon task cover auto-restart and start-on-sign-in. See
the CHANGELOG for specifics.

→ `Phase5m_Plan.md`

### Phase 5l — Manager self-update `[shipped]`

Three-stage rollout, each independently shippable:

- **5l-1** `[shipped]` — Detection and notification. GitHub
  Releases API polling, status-bar indicator + Help → Check for
  updates, skip-version, Settings → Updates (pre-release opt-in +
  interval), install-writeability probe, and GitHub-style rendered
  release notes (HtmlRenderer + an in-house Markdown→HTML converter
  with a RichTextBox fallback).
- **5l-2** `[shipped]` — Download and stage. Resolve release
  assets, download zip + SHA256SUMS with a cancellable progress
  dialog, verify SHA-256, extract to `.updates\{version}\extracted\`;
  Update-ready / Discard state. Apply button present but disabled
  pending 5l-3.
- **5l-3** `[shipped]` — Apply. Full pre-flight chain (downgrade
  guard → automation-in-flight warning → running-instances warning →
  Roslyn dry-run compat report vs the staged contracts; soft-warn +
  acknowledge), the `apply.cmd` binary swap (rollback backup, clean
  exit so the watchdog stands down, `--post-update` relaunch), and an
  update-history table under Help → Update History recording every
  apply attempt (success on post-update startup, failure when an
  `apply-error.log` is found).

→ `Phase5l_Plan.md`

### Phase 6 — Plugin GitHub source + manifest + updates `[shipped]`

All four sub-phases shipped (June 2026):

- **6-1** `[shipped]` — Inline `' <plugin id name version author
  requiresContracts >` + `' <dependencies>` manifest headers, parsed
  pre-compile; legacy `' <RequiresContracts: N>` still honoured
  (dual-format, phased out slowly). Plugin Status gained Version /
  Author / Source columns; the three first-party plugins carry full
  manifests (`1.0.0`, `author="siteml"`).
- **6-2** `[shipped]` — `PluginSources` table (EF migration
  `PluginSources`) with the official `siteml/PowerGSM` @
  `GSM.PluginsSource` source seeded un-deletable; catalog fetch via
  contents-API listing + raw manifest parse with a per-session cache;
  sources CRUD UI.
- **6-3** `[shipped]` — Staging pipeline to `.plugin-updates\{id}\`:
  authoritative re-parse of the downloaded copy, blocking dependency
  resolution, naming/prefix + collision warnings (warn-and-confirm,
  source-owner-derived prefix). `Plugins\` never touched at stage
  time.
- **6-4** `[shipped]` — Install (copy + hot-reload, consent on
  warnings), update detection across all enabled sources with an
  Updates view (stage → consent → install → reload; never auto),
  and file-level Uninstall with orphan-consequence consent. UI
  consolidated into the tabbed **Manage Plugins** window hosting the
  three existing forms.

Out of scope, tracked for later: plugin signing (HTTPS-only for
now), curated marketplace, transitive cross-source dependency
pulls, Node-side distribution.

→ `Phase6_Plan.md`

### Phase 7 — Manager-side utility plugins `[shipped]`

Generalised the `IGamePlugin` / `INotificationPlugin` surface
into an `IManagerPlugin` umbrella with an `IUtilityPlugin`
specialisation: utility plugins get event subscriptions (player
join/leave, server state, chat, instance lifecycle), can expose
menu items / panels / Discord slash commands, and reach Manager
services through a constrained service-locator, all behind a
**declared-capability permission model** (manifest declares
`requires: chat-read, network`, user approves on install). The
reference utility plugin became **lo-myrealm** (rather than the
originally-sketched Steam-login-session): it resolves LO
characterID → character name from the myrealm portal. Sub-phases
through 7-7 shipped — Web Sessions UI (7-5b), multi-realm
discovery + import (7-6), and multiple myrealm accounts with
in-memory per-realm session failover (7-7).

→ `Phase7_Plan.md`, `Phase7-7_Plan.md`

### Phase 5n — Notification scope rework (+ panel ID surfacing) `[shipped]`

Rework the `NotificationsForm` scope section: add **Node** and
**Instance-set** dimensions (sets reuse the existing
`InstanceEntity.InstanceSetTag`, no new entity), replace the confusing
AND-narrowing with **union-of-includes** (only the global all-empty
state means "all"; the invisible "empty = all within" rule goes away),
and present it as a collapsible accordion with summary-bearing headers
+ a live "matches N of M instances" readout (the scheduled-restart
idiom). The send-time matcher (`DiscordWebhookPlugin.MatchesEvent` +
the bot transport) moves to union in lockstep; `NotificationEmitter`
stamps the set tag onto tokens. Bundles an independent slice surfacing
InstallationID / InstanceID / NodeId on their panels with
copy-to-clipboard. Append-only in the 5-series; no ordering dependency
on the 8 → 9 release gate.

**Status:** all three slices shipped (in `main`, unreleased). 5n-1
(schema + four-dimension accordion editor) and 5n-3 (panel ID
surfacing) landed 2026-06-20; **5n-2 (runtime union + scope fan-out)**
landed 2026-06-21 — both transports evaluate the four filters as
union-of-includes, and installation-level Update events fan out across
their instances so instance/set-scoped destinations catch them.
Back-compat settled: pre-rework dual-filter rows are left as-is and
reconfigured once.

→ `Phase5n_Plan.md`

### Phase 8 — Node self-update + restart-survivable instances `[shipped]`

**Shipped in 0.4.0** (the release gate). Ordered *before* Phase 9 —
8-1 hardens the Node before 9 starts churning it, and 8-2 makes
shipping node-side iterations painless. Landed as three sub-phases
(8-3 added as a hardening follow-on):

- **8-1 — Per-instance shim + adopt-on-restart.** A tiny, rarely-
  updated `GSM.Shim` process per instance spawns the game server as
  *its* child and owns the stdio pipes, pumping stdout/stderr into a
  local buffer served to the Node over a named pipe (Windows) /
  Unix domain socket (Linux), and relaying stdin + stop requests.
  Node restart (crash, update, or manual) becomes a non-event for
  instances: the Node re-adopts via persisted instance→PID/shim
  mappings and reconnects to each shim. This overturns the earlier
  "hot-swap re-attach is structurally hard" rejection — the pipes
  being unreconnectable was the whole obstacle, and the shim removes
  it by never letting the Node own them in the first place. Also
  fixes a fragility that exists TODAY (a Node crash closes the pipes
  and degrades/hangs stdout-piped servers — Factorio, Linux LO —
  with no reattach on watchdog restart), and opens a realistic path
  to true UE4 graceful shutdown: the shim can natively
  `CreateProcess` with `CREATE_NEW_PROCESS_GROUP` + raw Win32 pipes
  it pumps itself (the prior attempt failed only at re-attaching
  native pipes into a .NET `Process` object, which a shim never
  needs), then deliver a real `CTRL_C_EVENT` on stop. Migration
  note for the plan doc: pre-shim instances can't be adopted —
  first shim-era Node needs a "restart instances at your
  convenience" story, not a hard cutover.
- **8-2 — Detect / stage / apply.** The 5l patterns, Node-flavoured:
  Manager detects the version mismatch per node, stages the new
  binary on the node, applies via service restart (SC / systemd),
  health-checks `/api/version`, reports back. With 8-1 in place,
  instances keep running throughout — zero player-visible downtime.
  Extended in 0.4.0 with shim + NodeSetup co-update (7b/7c) and a
  health-gate auto-rollback (slice 8).
- **8-3 — Shim rediscovery / `node.db` hardening.** Re-adoption stops
  depending on `node.db`: the node rediscovers live shims from the OS
  (well-known pipe/socket names) and the shim echoes its id + log paths
  on adopt, so a wiped/corrupt `node.db` can't orphan running games.

→ `Phase8_Plan.md`, `Phase8-2_Plan.md`, `Phase8-3_Plan.md`

### Phase 9 — Node-side utility installations `[design]`

Ordered after Phase 8 (hard dependency: reuses 8-1's shim to spawn
helpers and 8-2's `staged-binary` channel for delivery). A general
mechanism for installing, running, and updating **node-local helper
processes** — managed like game servers but describing a non-player-
facing utility. Each is a Manager-side `IUtilityInstallationPlugin`
(reusing the install pipeline as generic download-install, no SteamCMD)
paired with a node-side helper binary. **Admission test:** a utility
earns the node only if it needs node-local hardware/OS access (sensors,
NIC traffic, CPU/mem, the process/service table, a dependency
installer) or must run independently of the Manager — otherwise it's a
Phase 7 Manager-side plugin. Reference implementation: the **Comms
Sentinel**, a node-local dead-man's-switch that fires an out-of-band
alert when the Manager stops checking in (the one outage neither side
can self-report). Distinct gear icon in the tree; no player / chat
surfaces.

→ `Phase9_Plan.md`

---

## Future / under consideration

Agreed-in-principle but unordered. Plan docs not yet written.
Pulled into "Queued" when scoped and prioritised.

### Phase 10 — myrealm administration (POSTs / writes) `[future]`

The write half of the myrealm integration. Everything to date
(7-6 discovery, 7-7 multi-account + failover) only *reads* the
portal; Phase 10 performs authenticated **POST/write** actions —
the concrete seed being character renames, with broader admin
actions (realm / member management, as the portal exposes them)
to be scoped. Builds directly on 7-7: it reuses the multi-session
enumeration and per-realm session selection already in place, and
needs an account with write authority on the target realm (owner,
or an admin where the portal permits). This is the consumer that
finally justifies the **persisted session→realm access map**
deferred from 7-7 — a durable map earns its keep once writes must
target a specific authoritative account rather than "whichever
live session can read the page". Scope is loose and idea-rich; a
dedicated `Phase10_Plan.md` pins it down last, after the Phase
8/9 plan docs are written.

### Phase 5g-3 — LO actor-id bridging `[blocked: log samples]`

Richer LO player identity resolution to close the residual gap
where 5g-1/5g-2's chained `PlatformUserId` resolution misses
edge cases. Detailed background in Backlog.md. Blocked on
capturing the relevant log line samples during a real LO
session.

### Config-file-editing UI implementation

Designed in detail (`IConfigFileProvider` interface, Node
endpoints with path sandboxing, format-aware INI/JSON/Properties
parsers, Manager UI reusing SchemaFormBuilder) but not yet
implemented. Waits for a moment when there's no more urgent
History/Discord work in flight.

### Node import/export

Backup-and-restore for node configuration. Companion to the
attach/detach toggle that landed in 0.3.0. Not blocking
anything; comes up when a real migration scenario arises.

### EditInstallationForm version display

Show the install's currently-installed game version and
upstream version side-by-side in the edit dialog. Cosmetic but
useful.

### OverlapPolicy / ConditionMode persistence migration

Two enum fields on automation rules that currently aren't
persisted correctly across Manager restarts. Defensive cleanup;
no user-visible bug today.

### Per-installation poll intervals

Currently VersionCheckService polls upstream on a single
shared cadence. A per-installation override would help when one
install needs aggressive polling (a heavily-modded server with
fast-moving deps) and another doesn't care.

### Player-list ghost on misrouted connection

From Backlog. Misconfigured port routing causes EventStore to
mis-attribute a leave. Rare repro requirement keeps this
deferred.

---

## Recently shipped

Last few shipped phases. Older shipped phases live only in
CHANGELOG.md.

### 0.3.0 — 2026-05-22

- **Phase 5h** (6 sub-phases) — Plugin shared config, install-
  level vs instance-level config separation, History Source
  column consolidation (5h-6). Also covered platform-name
  column rename and node attach/detach toggle.
- **Phase 5g-2c** — Conan silent-player temporal heuristic +
  cid-stash.
- **Phase 5g-2b** — Conan FLS identity fix.

### 0.2.0 — 2026-05-08

- **Phase 5d (sub-phases 5d-1…5d-5)** — Discord bot: persistent
  control panels, management ephemeral flow, role mapping UI +
  permission enforcement, outbound notifications + slash
  commands (`/help`, `/panels`, `/players`) with per-guild
  visibility scoping, and polish (custom panel composition,
  per-panel role overrides). This is the **canonical 5d
  sequence**; follow-on Discord work continues at 5d-6+
  (display format, command-model surfacing, `/lastseen`).
  Retained here past the usual prune horizon specifically to
  document the numbering, since the follow-on items briefly
  collided onto 5d-2/5d-3/5d-4. → `Phase5d_Plan.md`

### 0.4.0 — 2026-07-01

- **Phase 8 — Node self-update + restart-survivable instances.** 8-1
  per-instance `GSM.Shim` supervisor (node re-adopts live shims across
  restart/update/crash; four survivor paths verified), 8-2 node
  self-update (chunked push → apply → survivor swap/relaunch, shim +
  NodeSetup co-update, health-gate auto-rollback), 8-3 shim rediscovery
  / `node.db` hardening. Slice-8 rollback + 8-3 rediscovery ship
  code-complete pending a post-release live check. → `Phase8_Plan.md`,
  `Phase8-2_Plan.md`, `Phase8-3_Plan.md`
- **Phase 7 — Manager-side utility plugins.** `IUtilityPlugin` surface
  + event subscriptions + declared-capability permission model;
  reference plugin lo-myrealm (characterID → name), through 7-7
  (multi-account + per-realm session failover). ContractsVersion → 2.
  → `Phase7_Plan.md`, `Phase7-7_Plan.md`
- **Phase 6 — Plugin GitHub source + manifest + updates.** Inline
  `' <plugin …>` manifests, seeded official source, acquire → stage →
  consent → install → hot-reload with no Manager restart; tabbed
  **Manage Plugins** window. → `Phase6_Plan.md`
- **Startup config render (`IStartupFileProvider`)** — new field→runtime
  bridge that renders instance-config values into a game's own config file
  just before launch (preserving the rest of the file), so file-only games
  can receive allocator-assigned ports and garble-prone text values stop
  corrupting on the command line. `IStartupFileProvider` in Contracts
  (`ContractsVersion` unchanged at 2) +
  `InstanceManager.ApplyStartupFileRendersAsync` reusing the file editor's
  node endpoints (proceed-and-warn, write-on-diff, single-ownership).
  Adopted by **Windrose** (direct-connection port + toggle →
  Configuration/allocator, rendered into `ServerDescription.json`; verified
  live) and **Conan** (ServerName off the launch URL + ServerPassword off
  the Engine.ini editor → both Configuration fields rendered into
  `Engine.ini` `[OnlineSubsystem]`, with a set/keep/clear password checkbox
  and the Network editor tab removed). → `StartupConfigRender_Plan.md`
- **Phase 5n** — Notification scope rework. Scope is now a four-
  dimension **union-of-includes** (Node / Installation / Instance /
  Instance-set; only the all-empty state means "all") with a
  collapsible accordion editor (summary headers + live "matches N of M"
  readout) and `NodeFilterJson` / `InstanceSetFilterJson` columns
  (migration `NotificationScopeDimensions`). Both Discord transports
  evaluate the union at send time; installation-level Update events
  **fan out** across their instances so instance/set-scoped destinations
  catch them; `{InstanceSetTag}` is a new template token. Bundled 5n-3:
  Node / Installation / Instance panels show their ID with right-click
  Copy. Back-compat: pre-rework dual-filter rows are left as-is and
  reconfigured once. → `Phase5n_Plan.md`
- **Phase 5l-3** — Apply updates. Live Apply button: staged-contracts
  plugin-compat pre-flight (Roslyn dry-run compile, soft-warn +
  acknowledge) + Tools → Test Plugin Compatibility, downgrade guard,
  and an `apply.cmd` binary swap (waits for exit, backs up to
  `.updates\rollback\`, swaps the two binaries, relaunches
  `--post-update`); Manager exits clean (0) so the watchdog stands
  down. Failed swaps log to `.updates\apply-error.log` and surface on
  next launch. Also fixes the 5m-3 `PublishWatchdog` target leaking
  `net8.0-windows` onto the watchdog. Automation/instance pre-flight
  prompts + Help → Update history still to come. → `Phase5l_Plan.md`
- **Phase 5l-2** — Download + stage updates. Download button +
  cancellable progress dialog; resolve release assets, fetch zip +
  SHA256SUMS, verify SHA-256, extract to `.updates\{version}\
  extracted\`; Update-ready / Discard state (Apply disabled pending
  5l-3). Also: `release.yml` now writes BUILD-INFO.json into the
  Manager zip + a SHA256SUMS release asset. → `Phase5l_Plan.md`
- **Phase 5l-1** — Manager update notifications (detect + notify
  only): background GitHub release check, status-bar indicator +
  Help → Check for updates, skip-version, Settings → Updates
  (pre-release opt-in + interval), install-writeability probe, and
  GitHub-style rendered release notes via HtmlRenderer + an in-house
  Markdown→HTML converter with a RichTextBox fallback. No download or
  apply yet. → `Phase5l_Plan.md`
- **Phase 5m** — Manager resilience. 5m-1 tray + window-state
  persistence; 5m-2 safe mode (CLI flag, crash-marker recovery, gated
  startup, banner, restart-into/out-of-mode, 2c in-safe-mode feature
  re-enable panel, 2d plugin enable/disable via a `Disabled\`
  subfolder); 5m-2e missing-plugin detection (reconciliation-based
  orphan warning + DarkRed tree badges, running-orphan escalation)
  plus a hard `StartInstanceAsync` guard refusing to launch a
  pluginless instance, plus 5m-3 (watchdog auto-restart +
  start-at-sign-in via a per-user Task Scheduler logon task). Phase
  5m is complete.
  → `Phase5m_Plan.md`
- **Phase 5k** — Player-list Discord panel. New `PlayerList` panel kind
  (online players grouped by instance in the 5d-6 format, hide-empty
  toggle), reusing the existing panel scope + refresh + editor; schema
  adds `PanelKind` + `ShowEmptyGroups`. 5k-2 added grouping for player
  panels (with a three-level node/game/instance header scheme that also
  fixed a by-node-then-game bleed in the instance panel) plus
  `ShowJoinTime` / `ShowTotalInTitle` toggles. → `Phase5k_Plan.md`
- **Phase 5d-8** — `/lastseen` slash command: player lookup (presence —
  active-now / offline — from the most-recent join/leave, rendered with
  the History SourceLabel),
  optional instance/game/installation scope filters, and a scope-only
  roster mode. Identity-aware (resolver-translated name match,
  identity-grouped disambiguation); gated ServerOperator via the 5d-7
  command catalogue. → `Phase5d-8_Plan.md`
- **Phase 5d-7** — Discord command-model surfacing in the Manager:
  command catalogue as single source of truth, awareness labels in
  the panel/role editors, and a Commands surface in `DiscordBotForm`
  with a per-guild effective-access preview. → `Phase5d-7_Plan.md`
- **LO player-leave attribution across Manager reconnect/restart.**
  Connection bindings externalised via the new
  `IConnectionBindingAware` contract interface (Manager owns the
  RemoteAddr→name table, injects it per parser (re)creation,
  rehydrates from `/players` on resync), plus a cross-clock
  duplicate-join dedup fix. Closes dropped leaves and session
  mis-pairing when a player was connected across a reconnect or
  restart. CHANGELOG has the full breakdown.
- **LO `/players`-diff leave reconcile** (resync "Pass 1.7").
  Synthesises a Leave for a player whose Join is still open in
  History but who is absent from the Node's authoritative `/players`
  — catching departures that happened entirely while the Manager was
  offline. Scoped by InstanceId (realm-wide SessionIdentity would
  false-leave sibling-tile players) and gated on Node uptime
  (post-restart `/players` under-reports). Persist-only; no Discord
  ping for past departures.
- **Phase 5g-2d** — Manager-side IdentityResolver. Centralised
  identity cache with union-find alias keys; hydrated from
  PlayerActivity; fed by three write-through paths (persist,
  resync, 10s backfill); five read consumers enriched
  (Overview panel, History, Discord /players, both join/leave
  notification paths). Platform tracked as carried attribute.
  Persist-time resolver consult fills the /players-miss gap so
  History rows get stamped at write time. CHANGELOG entry has
  the full breakdown.
- **Phase 5d-6** — Discord player display format unified across
  /players and join/leave notifications:
  `character (Platform: persona)` when both known,
  `persona (Platform)` otherwise.
- **Phase 5i** — LO false-leave parser fix.
  `UNetConnection::Close` only; the dual-match bug producing
  ghost leaves is closed.
- **Phase 5j-1 / 5j-2** — Purge & Rebuild History from Current
  State. → `Phase5j_Plan.md`
- **Phase 5j-3** — Stream-restart resync (`ResyncActivePlayers
  FromNodeAsync` in InstanceManager). Two-pass: synthesise
  Join rows for currently-online players whose join was
  missed during Manager downtime; sync `_activePlayers` bucket
  from /players. Fixes the "Cycle 2 missing from History" bug
  observed 2026-05-25. No plan doc — hotfix-class change
  scoped from chat.
- **Conan AdminPassword** — moved from Configuration tab to
  Server Settings INI editor where Conan natively reads it.

---

## Won't do (this cycle)

Things considered and explicitly deferred or rejected. Saves
having the same conversation twice.

### Node-side plugins calling Manager-side plugins via a generic message bus

Considered during Phase 7/9 planning. Pattern would be a
pub/sub channel between Node-side and Manager-side plugins of
the same family — useful in theory, but no concrete consumer
today, and it implies a Node↔Manager bidirectional protocol
that doesn't exist. The current request/response API can carry
plugin-specific data via existing endpoints if a real use case
shows up. Deferred to "next major version if ever."

### ~~Hot-swap Node update with active-instance re-attach~~ (OVERTURNED — now Phase 8-1)

Originally rejected during early Phase 8 thinking: "new processes
can't reconnect orphan-child stdio pipes, so even if game instances
survive the parent Node's exit, the new Node can't manage them",
with job-object workarounds trading cleanup hygiene for zombie
risk. **Overturned June 2026:** the per-instance shim (Phase 8-1)
dissolves the core obstacle — the Node never owns the game's pipes
at all; a tiny pipe-owning supervisor does, and it survives Node
restarts. The zombie-server concern inverts too: the shim is a
*managed* parent with an explicit stop channel, unlike a detached
orphan. Kept here (struck through) per this section's purpose —
the original reasoning was sound against the original design, and
knowing why it flipped saves re-litigating it.

### Plugin auto-update without operator approval

Considered for Phase 6. Plugins are executable code with broad
access to Manager state; an auto-update path is a supply-chain
risk amplifier. Phase 6 lands with notification + staged
update + manual apply only.

---

## Maintenance

This file is only useful if it stays current. Update it when:

- A phase ships → move from queued/active to recently shipped,
  add CHANGELOG link if available
- A phase enters active development → move from queued to
  current focus
- A new agreed-upon-in-principle phase surfaces → add to
  future
- Priorities shift → re-order queued
- A future item gets a plan doc → mark it `[design]` and add
  the link
- A backlog item gets prioritised → move from Backlog.md into
  Queued or Future here

Prune recently-shipped entries older than 2-3 versions back.
CHANGELOG keeps the long memory.
