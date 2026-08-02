# Phase 9 — Node-side utility installations `[design]`

## Goal

A general mechanism for installing, running, configuring, and updating
**node-local helper processes** — managed exactly like game servers (process
management, log tailing, ring buffers, restart policy, shim spawn) but
describing a non-player-facing utility rather than a game. Each utility is a
**Manager-side `IUtilityInstallationPlugin`** (Roslyn-compiled, same plugin
model as games and Phase 7) paired with a **node-side helper binary** it
installs and supervises.

The reference implementation that proves the mechanism end-to-end is the
**Comms Sentinel** — a node-local dead-man's-switch that fires an out-of-band
alert when the Manager stops checking in, covering the one outage neither the
Manager nor the Node can self-report (the Manager itself being down or
network-partitioned).

**Ordered after Phase 8 (hard dependency).** Phase 9 reuses 8-1's shim to spawn
helpers and 8-2's `staged-binary` channel as one of its delivery paths, and it
churns the Node — which is exactly why 8-1 hardens the Node first.

---

## Status

`[design]` — designed/confirmed with Site 2026-06-19; decisions below
confirmed. Not started; hard-blocked on Phase 8 (both 8-1 and 8-2). Grounded
against `GSM.Contracts\IGamePlugin.vb` (2046 lines), `IUtilityPlugin.vb`
(Phase 7 capability model), and the `InstallStep` / `Capabilities` surfaces,
read 2026-06-19.

---

## Scope — the admission test

A capability earns a node-side utility installation **only if it needs
node-local hardware/OS access, or must run independently of the Manager.**
Everything else belongs to a Phase 7 Manager-side utility plugin, which can
already observe logs and state remotely.

- **Passes** (node-local hardware/OS): hardware temperature / sensor reads,
  NIC traffic monitoring, the OS process & service table beyond managed games,
  host CPU / memory utilisation, and a host **dependency installer** (which
  could retire the current best-effort node-side VC++/redist guessing in
  `PrerequisiteProbe` into a real, declarable utility).
- **Passes** (Manager-independence): the **Comms Sentinel** — only a process
  *on the node* can notice the Manager went away.
- **Fails** (Manager can already see it): log-based hang detection, anything
  derivable from log lines or polled state → Phase 7 Manager-side plugin.

This razor is the single most important scoping tool for the phase; when a
proposed utility is ambiguous, ask "could the Manager do this remotely?" — if
yes, it is not a Phase 9 utility.

**Two "utility" surfaces, kept distinct.** Phase 7 shipped *Manager-side
utility plugins* (`IUtilityPlugin`: event subscribers like lo-myrealm). Phase 9
adds *node-side utility installations* (`IUtilityInstallationPlugin`:
process-managed helpers). Same word, different things — the plan and code keep
them visibly separated.

**Naming.** The reference helper is the **Comms Sentinel**, deliberately *not*
"watchdog": `GSM.Watchdog` already exists (Phase 5m-3, supervises the *Manager
process* locally). Three-way "watchdog" collision avoided; the concept is still
a dead-man's-switch, the name just doesn't clash.

---

## Confirmed decisions

1. **Same shape as games; multiple instances allowed.** A utility installation
   is an `InstallationEntity` with `InstallationKind = Utility`, containing
   instances, reusing the entire installation→instance machinery. No singleton
   special-casing (a Sentinel per uplink, a monitor per device, are real
   shapes).

2. **A utility = two coupled parts.** (a) A Manager-side
   `IUtilityInstallationPlugin` describing delivery, launch, config schema,
   declared capabilities, update strategy, and presentation; updated via the
   Phase 6 plugin machinery. (b) A node-side helper binary it installs and
   supervises; updated per the strategy the plugin declares (decision 6).

3. **Reuse the install pipeline as generic download-install.** The existing
   `InstallStep` hierarchy already carries `DownloadFileStep`, `CopyFileStep`,
   `WriteFileStep`, `RunProcessStep` alongside `SteamCmdStep`. A utility's
   `GetInstallSteps` returns download/run steps with no `SteamCmdStep` —
   SteamCMD is meaningless here, generic download is the point.

4. **Two delivery paths; the plugin author chooses.** (a) **Node-direct-URL** —
   `GetInstallSteps` returns `DownloadFileStep`, the node's `InstallRunner`
   fetches from the URL (the path that already exists for games). (b)
   **Manager-brokered** — the Manager acquires the asset (Phase 6 source +
   manifest) and pushes it to the node over Phase 8's `staged-binary` channel,
   then a local step places it (the new path). Per-platform assets resolved by
   node platform (`ResolveNodePlatform` / `/api/version`).

5. **Helpers spawn under shims (Phase 8 uniform).** A utility instance is just
   another node-managed process; it runs under a shim, so it survives node
   restarts and a node bounce never blinds it. It may set `StdoutIsLog` /
   `RequiresConsoleIsolation` (via `ILaunchOptionsProvider`) like any process.

6. **Update strategy is per-utility, declared by the plugin.** The Manager-side
   plugin updates via Phase 6. The node-side binary can update via the
   game-style install-step path, the Phase 8 `staged-binary` apply flow, or
   node-direct re-download — and *which* is the plugin's declared choice, since
   different utilities warrant different strategies. Not one-size.

7. **Reuse the Phase 7 capability/consent model.** Utilities declare
   capabilities in their manifest `requires=` list; the operator consents on
   install. The Comms Sentinel is the natural first `network` case (outbound
   webhooks). Extends `Capabilities` (in `IUtilityPlugin.vb`) with a `Network`
   constant.

8. **Distinct UI: gear icon, no player surfaces.** Utility installations render
   in the tree as a distinct kind (gear, reading as "system/utility" — not a
   wrench, which implies repair), with start/stop/config + the helper's own log
   tab, and none of the players / chat / identity surfaces.

9. **No node-side autonomy beyond the alert (anti-split-brain).** A utility
   never independently reports game events. Events during a Manager outage are
   already held by the node (it persists state; the Manager reconciles on
   reconnect — the May 2026 re-adoption work). The Sentinel adds *only* the
   reachability up/down alert. Failover and event fan-out stay Manager-side
   concerns; the node never grows a second brain.

### Comms Sentinel — detection core

10. **Passive detection; reading the record is the node-liveness check.** The
    Sentinel polls a node localhost endpoint exposing `{ lastManagerContactUtc,
    attached }`. It does *not* probe the Manager — in a real partition it
    couldn't reach the Manager either, and the Manager is an outbound-only
    control plane with no inbound listener to probe. Three states fall out
    cleanly:
    - localhost read **fails** (node endpoint dead) → **node is down → stay
      silent** (node-down is the Manager's job — decision 11);
    - read **fresh** → fine;
    - read **stale past threshold** → **Manager unreachable → alert.**
    Because the Sentinel can only read the record when the node is up, it never
    has to disambiguate node-down from Manager-unreachable — an unreadable
    record *is* node-down.

11. **Manager-unreachable only; never node-down.** The inverse (node goes
    silent) is already the Manager's job — it polls, sees silence, and has
    working webhooks to report it. The Sentinel deliberately does not duplicate
    that; no extra noise.

12. **Detachment disarms it.** A detached node intentionally has no Manager
    contact, so the `attached` flag in the localhost record gates the Sentinel:
    while detached it stays quiet, no false alarm on the attach/detach toggle.

13. **Threshold operator-configurable, default auto-derived.** Exact
    Manager→node contact cadence isn't a known constant, so the default
    threshold is a multiple of the *observed* contact interval (the node knows
    its own request-receipt history); the operator can override.

14. **Both alert channels.** Discord-webhook-shaped *and* generic HTTP POST,
    chosen in config, fired node-side fully independent of the Manager.
    Brackets the outage: an "unreachable" alert on trip and a "contact
    restored" alert on recovery.

---

## Architecture

```
  Manager                                   Node (same machine as helpers)
  ┌──────────────────────────────┐          ┌───────────────────────────────┐
  │ IUtilityInstallationPlugin   │          │ InstallRunner (download/run)  │
  │  · acquire (Phase 6 source)  │──push───►│ ProcessManager (+shim spawn)  │
  │  · config UI (SchemaForm)    │ Phase 8  │ localhost: manager-contact rec│
  │  · gear-icon tree node       │ staged-  │                               │
  └──────────────────────────────┘ binary   │  ┌─────────────────────────┐  │
                                             │  │ shim → Comms Sentinel   │  │
              (node-direct-URL alt:          │  │  polls localhost record │  │
               node fetches from URL)        │  │  fires own webhook ─────┼──┼──► Discord / HTTP
                                             │  └─────────────────────────┘  │   (out-of-band)
                                             └───────────────────────────────┘
```

The helper is a node-managed process like any game instance; the Manager-side
plugin is its describer/installer/configurer. The Sentinel's runtime
independence is what matters — delivery and config happen while the Manager is
reachable (setup time); detection and alerting run node-local and
Manager-free.

---

## New surfaces

### Contracts — `GSM.Contracts\IUtilityPlugin.vb` (or a new `IUtilityInstallation.vb`)

`IUtilityInstallationPlugin` — derived from `IGamePlugin` by keeping the
process/install machinery and dropping the player-facing surface:

- **Keep:** identity (`UtilityId`/`DisplayName`/`MaxInstancesPerInstallation`),
  `GetInstallSteps` / `GetUpdateSteps` (decision 3), `GetExecutablePath`, a
  launch-spec method (the non-player-facing analogue of `BuildLaunchArguments`
  — builds the helper's command line from config), `GetInstallConfigSchema` /
  `GetInstanceConfigSchema`, `ValidateConfig`, `EvaluateCrash` (restart
  policy), and optional log surfaces (`GetLogSources` / `GetLogParseRules` for
  the helper's own output).
- **Opt-in (reuse the existing provider interfaces):** `ILaunchOptionsProvider`
  (shim strategy flags), `IVersionAwarePlugin` (update detection),
  `IPrerequisiteProvider` (relevant to a dependency-installer utility).
- **Drop:** `GetRconProtocol`, `CreateModManager`, `IConnectionBindingAware`,
  `ISourceLabelProvider`, `IFileGenerationProvider`, and all player / identity
  / chat concepts.
- **Capabilities:** add `Capabilities.Network` constant; `requires=` parsing
  already exists.

### Data — `GSM.Manager\Data\GsmDbContext.vb`

- `InstallationEntity.InstallationKind` (enum: `Game` | `Utility`), additive
  column, defaults to `Game` so every existing row is unaffected. One small
  migration. The instance/installation tables otherwise unchanged (decision 1).

### Node — delivery + the contact record

- `InstallRunner` already executes `DownloadFileStep` / `RunProcessStep`, so
  node-direct-URL delivery is largely free; Manager-brokered delivery lands the
  asset via the Phase 8 `staged-binary` endpoint then runs a local place step.
- **New: a Manager-contact record.** The node's authenticated-request pipeline
  stamps `lastManagerContactUtc` on every Manager call, and a small localhost
  endpoint (e.g. `GET /api/system/manager-contact`, loopback-only) returns
  `{ lastManagerContactUtc, attached }` for a co-located Sentinel to poll.
  (Confirm during build that the Manager hosts no inbound listener, validating
  passive-only detection — decision 10.)

### Comms Sentinel helper (first-party, downloaded — decision: opt-in, not bundled)

- A small self-contained binary (the `GSM.Watchdog` / `GSM.CtrlCSender`
  self-contained pattern), shipped as a **downloadable** utility install so it
  exercises the real acquire→deliver→install→run path rather than a bundled
  shortcut. Config schema: threshold (auto-derived default), alert channel
  (Discord webhook | generic HTTP POST) + endpoint + message template,
  arm-while-attached. Declares `requires="network"`.
- It is useless on the same network/connection as the Manager, so it is opt-in
  by nature — installed only where a node sits on a distinct uplink.

### Manager UI

- Tree renders `InstallationKind = Utility` with the gear icon and a
  utility-flavoured panel (start/stop/config + helper log tab, no players/chat).
- Reuses `SchemaFormBuilder` for the helper's config; reuses the Phase 6
  Manage-Plugins acquisition flow for utilities; reuses the consent dialog for
  declared capabilities.

---

## Slices (confirm-gated, in order)

**The mechanism**

1. **`InstallationKind` + the utility installation/instance path.** Migration;
   tree shows a utility install (gear); a trivial hand-placed helper binary
   starts/stops as an instance under a shim. *Test:* a do-nothing helper runs,
   logs to its tab, survives a node restart.
2. **`IUtilityInstallationPlugin` + install pipeline reuse.** The trimmed
   interface; `GetInstallSteps` (download/run) drives node-direct-URL install.
   *Test:* a utility plugin installs its helper from a URL on a node, then runs
   it.
3. **Manager-brokered delivery + per-platform assets.** Phase 8 `staged-binary`
   push path; platform-asset resolution; capability consent on install. *Test:*
   the same helper delivered Manager-side to a remote node, correct
   per-platform asset, `network` consent prompted.
4. **Update flow.** Per-utility update strategy (Phase 6 plugin side + the
   declared node-binary strategy). *Test:* bump a utility, update applies by
   its declared path.

**The reference helper**

5. **Manager-contact record + localhost endpoint.** Node stamps
   `lastManagerContactUtc`, exposes the loopback record incl. `attached`.
   *Test:* record advances on poll, freezes when the Manager is stopped, flips
   on detach.
6. **Comms Sentinel detection + alert.** The helper polls the record,
   threshold logic, detach-disarm, both alert channels, trip + recovery.
   *Test:* partition the Manager from the node's view → Sentinel fires the
   webhook; node-down → silent; detach → silent; recovery → restored alert.

---

## Deferred / motivating future utilities (not built this phase)

- **Host system monitors** — temperature / sensors, CPU / memory, NIC traffic,
  process & service table. The central Q0 use case; each a future utility on
  this mechanism.
- **Dependency installer** — promote the node-side best-effort prereq coverage
  (`PrerequisiteProbe`, the deferred VC++ auto-install) into a declarable
  utility, carving that mess out of the game-install path.
- **Node-local event fallback during an outage** — explicitly out of scope
  (decision 9): no node-side event autonomy; the node holds and the Manager
  reconciles.

---

## Watch-outs

- **Don't reopen the Node↔Manager bus.** The Sentinel alerts *outbound on its
  own channel*; it never calls back into the Manager and the node never grows
  event autonomy (decision 9). Keep it that way — split-brain is the thing
  we're avoiding.
- **`IUtilityPlugin` vs `IUtilityInstallationPlugin`.** Two different surfaces
  sharing a word; don't let code or docs conflate the Phase 7 event-subscriber
  with the Phase 9 process-managed install.
- **Three "watchdog" meanings.** `GSM.Watchdog` (Manager supervisor) is
  unrelated to the Comms Sentinel; never call the Sentinel a watchdog in code
  or UI.
- **Detection must stay passive.** If a future idea wants active Manager
  probing, remember the partition case makes it useless and the Manager has no
  inbound listener — passive-record-read is load-bearing (decision 10).
- **Loopback-only contact endpoint.** `manager-contact` is for a co-located
  Sentinel; bind it to loopback so it isn't a remote information leak.
- **Hard Phase 8 dependency.** Both 8-1 (shim spawn) and 8-2 (`staged-binary`)
  must land first; Phase 9 slices assume them.
- **Read before edit.** `IGamePlugin` surface, `InstallRunner`,
  `InstallationEntity`, and the node auth pipeline were read for this plan but
  re-read at edit time.

---

## References

- `GSM.Contracts\IGamePlugin.vb` — the `IGamePlugin` member list +
  `InstallStep` hierarchy (`DownloadFileStep` / `RunProcessStep` / …) the
  utility interface is derived from; the opt-in providers
  (`ILaunchOptionsProvider`, `IVersionAwarePlugin`, `IPrerequisiteProvider`).
- `GSM.Contracts\IUtilityPlugin.vb` — the Phase 7 `Capabilities` /
  `requires=` consent model to reuse (add `Network`); the distinct
  event-subscriber surface to keep separate.
- `Phase8_Plan.md` — shim spawn (8-1) for running helpers; the
  `/api/system/staged-binary` channel (8-2) for Manager-brokered delivery; the
  per-instance execution model.
- `Phase6_Plan.md` — plugin source / manifest / acquire-update machinery the
  Manager-side utility plugin half reuses.
- `Phase7_Plan.md`, `Phase7-7_Plan.md` — Manager-side utility plugins, for the
  surface this phase is explicitly *not* (the distinction in Scope).
- `GSM.Node\PrerequisiteProbe.vb` — the best-effort node-side prereq coverage a
  future dependency-installer utility would supersede.
- `ROADMAP.md` → Phase 9 entry; *Won't do* → the Node↔Manager message-bus
  rejection this plan honours.
