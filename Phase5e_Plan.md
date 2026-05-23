# Phase 5e — Service Plugins and Manager Heartbeat Watchdog

Design document for adding "service plugins" to the framework — a
sibling to game plugins for long-running supervised logic that
isn't a game server. First concrete service: a manager heartbeat
watchdog. Read this first in the new chat; everything below
assumes the conversation is starting fresh.

---

## Goal

1. **Make service plugins a first-class concept.** Add an
   `IServicePlugin` contract sibling to `IGamePlugin` so the
   framework can host things that run-but-aren't-games:
   watchdogs, port monitors, cert-expiry checkers, availability
   probes, anything that fits the "runs in the background, has
   start/stop/status, emits events" mould.

2. **Ship the first service plugin: `ManagerHeartbeatWatchdog`.**
   Hosted on a node, polls the manager's `/api/version` endpoint
   on a schedule, posts to a configured webhook when the manager
   becomes unreachable, posts again when it recovers. Multiple
   watchdogs can run on different nodes for multi-vantage-point
   monitoring (a node on a different network catches firewall/
   routing issues a same-LAN watchdog wouldn't see).

3. **Reuse existing UX heavily.** Service instances appear in the
   manager's instance tree alongside game instances. Start/stop/
   restart works the same way. Status indicators, logs, automation
   rules — all the lifecycle plumbing applies. No third UI surface
   to learn.

The framing is "the watchdog is the first service, not the only
one" — building the contract for one specific case would just
mean rebuilding it differently the second time someone wants a
non-game service.

---

## Honest assessment of the current infrastructure

### What's already there and reusable

- `IGamePlugin` contract pattern — opt-in interfaces, plugin
  manifest properties, plugin discovery via `PluginRegistry`.
  Same pattern repeats for services.
- Installation/instance entity model in EF — services can fit the
  same shape (an installation per node + one or more instances).
- Node-side process supervision (`ProcessManager`) — already has
  the supervisor mindset, status tracking, ring buffer logging.
  Service supervision is sibling work to process supervision.
- Notification webhooks via `DiscordWebhookPlugin` and
  `IDestinationTargetingPlugin` — services can use the existing
  destination targeting to fire alerts.
- Automation rules — services can be rule targets and event
  sources just like game instances.
- Plugin loading via Roslyn at the manager side.

### What's missing and needs to be built

1. **`IServicePlugin` contract.** Smaller than `IGamePlugin`:
   identity, config schema, an entry point. No install steps, no
   version awareness, no log parsing, no RCON, no mod support.

2. **Node-side service hosting.** New `ServiceRunner` sibling to
   `ProcessManager`. Hosts in-process service instances. Tracks
   state (Stopped/Starting/Running/Stopping/Failed), exposes
   start/stop, surfaces logs through the existing ring buffer.

3. **Decision on where service logic runs** — see the
   architectural question below. This is the design pivot
   that determines almost everything else.

4. **Manager-side UI.** Services in the instance tree need to
   render distinctly (different icon, perhaps a "Services"
   subgroup). New-installation flow gains a service-plugin path
   alongside the game-plugin path.

5. **The watchdog plugin itself.** Concrete config schema +
   polling logic + webhook integration.

---

## Resolved design decisions

All architectural and clarifying questions were resolved in the
planning conversation. Reasoning kept so the next chat doesn't
re-litigate.

### D1. Where service plugin logic executes: declarative primitives on the node

**The framework's current model is "manager interprets, node
executes".** Service plugins follow the same pattern: the node
ships with a fixed set of "service primitives" (`HttpPoll`,
`TcpPortCheck`, etc.); service plugins compile and run on the
manager and produce a declarative `ServiceDescription` composed
of primitives + actions; the node executes the description.

Two alternatives were considered and rejected:

  Manager-hosted (node as transparent proxy) was rejected
  because it kills the multi-vantage-point benefit — the
  poll's network origin is the manager regardless of which
  node "runs" the service.

  Node-hosted plugin code (full Roslyn loader on the node) was
  rejected for v1 as too expensive: it'd give services maximum
  flexibility but introduces plugin distribution, hot-reload,
  and trust-model concerns to a process that today has none.
  Tractable as a future upgrade if the primitive set proves
  too restrictive.

Declarative primitives win because the watchdog (and most
plausible v1 services) are straightforwardly expressible as a
composition: "poll URL on schedule, fire webhook on N
consecutive failures." New primitives can be added as concrete
needs arise; each is small (interface in `GSM.Contracts` +
node-side implementation + manager-side description type).

### D2. Service installations: keep the installation parent

Game plugins have installations (the files) and instances (a
running configuration). Services don't have files in the
install sense, but keeping a placeholder "installation" parent
for service instances costs ~nothing and preserves uniformity:
the existing tree, breadcrumbs, automation-rule scoping, and
edit-installation form all keep working without service-
specific branching. The Edit Installation form for a service
is mostly empty — that's fine.

Most service installations will host a single instance, but
multi-instance is supported ("two watchdogs on this node,
monitoring different things").

### D3. Webhook config for the watchdog: per-instance URL field

The watchdog has a `WebhookUrl` config field directly on the
instance. It does NOT round-trip through the regular
`NotificationEmitter` / destination-targeting path, because:

  The watchdog's whole job is to fire alerts when the manager
  is unreachable. The notification destination registry lives
  on the manager. If the manager is down, the destination
  config is unreachable too — exactly the case the watchdog
  exists to handle. The watchdog must be self-sufficient: URL
  cached locally on the node, POSTs go directly from the
  node to the webhook endpoint without manager involvement.

Future service plugins whose use case doesn't require manager-
independence (a port-watcher that complements but doesn't
replace manager monitoring, say) can opt into the standard
destination-targeting path. The architecture supports both;
it's a per-plugin design choice. The watchdog specifically
requires direct.

### D4. Service start policy: per-plugin AutoStart property

`IServicePlugin` exposes an `AutoStart As Boolean` property.
False by default (matching games — manual start after install).
Watchdog plugin overrides to True since the whole point is
continuous monitoring.

Per-plugin rather than system-wide because services have
genuinely different natural defaults: a watchdog wants to be
running whenever the node is; a one-off probe might want
manual control. Each plugin author picks the right behaviour
for the use case.

---

## Proposed phasing

### Phase 5e-1: Service plugin contract + node-side primitive runner

Foundation. No watchdog yet — just the framework.

**Contract additions** (`GSM.Contracts`):

```vb
Public Interface IServicePlugin
    ReadOnly Property ServiceId As String     ' "manager-watchdog"
    ReadOnly Property DisplayName As String
    ReadOnly Property AutoStart As Boolean

    Function GetInstanceConfigSchema() _
        As IReadOnlyList(Of ConfigFieldDescriptor)

    Function BuildServiceDescription(config As InstanceConfig) _
        As ServiceDescription
End Interface

Public Class ServiceDescription
    ' Composition of primitives + actions. Primitives:
    '   HttpPollPrimitive(url, intervalSeconds, timeoutSeconds)
    '   TcpPortCheckPrimitive(host, port, intervalSeconds)
    '   ...
    ' Each primitive emits Success/Failure outcomes per tick.
    Public Property Primitives As List(Of ServicePrimitive)
    Public Property Actions As List(Of ServiceAction)
    ' Actions reference primitives by ID and trigger on
    ' patterns: "FailedConsecutive(N)", "Recovered", "Always"
End Class
```

**Node-side runner** (`GSM.Node`): `ServiceRunner` sibling to
`ProcessManager`. Hosts service descriptions, runs primitives
on their schedules, fires actions when transitions occur. State
machine: Stopped → Starting → Running → Stopping → Failed.
Logs to the same ring buffer as game instances so the existing
log viewer works.

**Manager-side**: `ServicePluginRegistry` parallel to
`PluginRegistry` (or extend `PluginRegistry` to handle both
kinds; lean toward separate for clarity). New-installation
flow gains a "Service" tab with the registry's plugins.

**Database**: probably reuses `Instance` and `Installation`
entities with a `Kind` discriminator column (`Game | Service`).
Migration adds the column with default `Game` for existing
rows. UI tree groups by kind.

**Acceptance**: a no-op test service plugin (returns an empty
`ServiceDescription`) can be installed, started, stopped via
the existing UI. Status tracking works. No actual primitives
execute yet (or: a single trivial primitive that writes "tick"
to the log every 10s).

### Phase 5e-2: HTTP poll primitive + webhook action

Implement enough for the watchdog. Two pieces:

- `HttpPollPrimitive`: hits a URL on a schedule, treats 2xx as
  Success, anything else (or timeout, or DNS failure, or TCP
  refusal) as Failure. Reports per-tick.
- `WebhookAction`: POSTs JSON to a configured URL. The plugin
  composes the JSON payload (title, message, severity).

`ServiceRunner` wires primitive outcomes to actions per the
description's rules. Failure-threshold and recovery-threshold
logic lives in the runner so plugins describe declaratively
("fire on FailedConsecutive(3)") without implementing the
state machine themselves.

**Acceptance**: a hand-crafted service description that polls
`https://httpbin.org/status/500` every 10s with a webhook
action on `FailedConsecutive(3)` actually fires three POSTs
to a test webhook within ~30s. Stop/start works.

### Phase 5e-3: ManagerHeartbeatWatchdog plugin

The first concrete service plugin. Delivered as a `.vb` in
`GSM.PluginsSource`.

**Config schema:**
- Manager URL (default: tries to discover from the node's
  configured manager endpoint, otherwise blank with
  required-field validation)
- Webhook URL (Discord webhook URL or generic; required)
- Poll interval seconds (default 30)
- Failure threshold consecutive (default 3)
- Recovery notification (default true) — whether to also fire
  on Failed → Running transition

**Builds** a `ServiceDescription` using `HttpPollPrimitive`
against the configured URL + `WebhookAction` for both failure
and recovery.

**Auto-start = true.**

**Acceptance**: operator creates a "Manager Watchdog"
installation on a node, sets the URL + webhook, hits Start
once, kills the manager process. Within ~90s (3 × 30s) a
"Manager unreachable" message arrives at the configured
webhook. Start the manager again; recovery message arrives.

### Phase 5e-4: UI polish & docs

- Distinct icon for service instances in the tree
- "Services" subgroup or tab in the New-Installation flow
- Auto-start indicator on the instance panel
- Documentation for writing new service plugins (composition
  of primitives + actions)
- Update `PowerGSM_Reference.md` with the contract, the
  primitive set, and the architectural decision rationale.

### Phase 5e-5: Additional primitives (deferred / on demand)

Add as concrete needs arise:
- `TcpPortCheckPrimitive` — for non-HTTP service availability
- `CertificateExpiryPrimitive` — for cert renewal alerts
- `DiskSpacePrimitive` — for disk-fill warnings on the node
- `PingPrimitive` — for ICMP-based reachability when an HTTP
  endpoint isn't available

Each new primitive is small (interface + node-side
implementation + manager-side description type). Ship when
something needs them.

---

## What this changes for existing functionality

**Game plugins:** untouched. `IGamePlugin` contract unchanged.
Existing installations and instances continue to work identically.
The `Kind` discriminator defaults to `Game` for existing rows
via migration.

**Notification destinations:** untouched. The watchdog uses its
own out-of-band webhook (per Q2) but other future service
plugins can opt into the standard destination targeting.

**Automation rules:** service instances become valid rule
targets — "if the watchdog enters Failed state, run X" is a
natural rule. State change triggers should already accommodate
this; the rule UI just needs to allow service instances in its
target picker.

**Discord bot (Phase 5d):** orthogonal. The watchdog operates
out-of-band from the bot and complements it: the watchdog
catches "the manager is down" cases the bot can't, the bot
catches everything else.

**Multi-vantage-point monitoring:** an emergent benefit. An
operator wanting "watch the manager from outside the LAN" can
deploy a node on a remote network and run a watchdog instance
there. Each watchdog instance is independent; no coordination
between them.

---

## Suggested first turn in the new chat

Paste this document. All decisions D1–D4 are settled in the doc.
Start with Phase 5e-1.

A reasonable opening:

> Read Phase5e_Plan.md. All decisions D1–D4 are resolved in the
> doc. Start with Phase 5e-1: produce the IServicePlugin
> contract, the ServiceRunner skeleton, and the schema migration
> adding the Kind discriminator to Instance/Installation.
