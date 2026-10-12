# The Manager↔Node API & protocol

The Node exposes a JSON REST API over HTTPS (plain HTTP optional). The Windows Manager is just one
client of it — **anything that speaks this protocol can be a Manager**: a web
app, a CLI, a bot. This document describes the protocol and what building an
alternative Manager involves.

Authoritative source for every DTO and the client interface:
[`GSM.Contracts/NodeApiContract.vb`](../../GSM.Contracts/NodeApiContract.vb)
(fully documented inline). Endpoint implementations:
[`GSM.Node/Endpoints/`](../../GSM.Node/Endpoints/).

---

## Transport & authentication

- JSON bodies on the Node's configured port (default **8765**). Since 0.7.0
  the Node serves **HTTPS and plain HTTP on that one port** (first-byte
  sniff): a TLS handshake gets the Node's self-signed certificate; plaintext
  is accepted, restricted to loopback, or dropped at the socket according to
  the Node's `HttpMode` (`Open` / `LoopbackOnly` / `Disabled`; new setups
  default to `Disabled`). An optional separate `TlsPort` keeps the legacy
  two-listener layout.
- **Trust model is fingerprint pinning, not a CA.** The certificate is
  self-signed and long-lived; a client should pin its SHA-256 (obtained
  out-of-band from `GSM.NodeSetup`, or from `GET /api/tls/status` /
  `POST /api/tls/enable` over an already-authenticated channel) and reject
  anything else. `GET /api/version` reports `TlsEnabled`, `TlsPort`,
  `SinglePort` and, when TLS is on, `CertFingerprintSha256` (public under
  this model — the pin must still come from a trusted channel).
- TLS lifecycle endpoints (all authenticated): `GET /api/tls/status`
  (enabled / port / single-port / `HttpMode` / fingerprint / expiry),
  `POST /api/tls/enable` (mints the cert if needed, persists, restarts;
  `TlsPort` 0/absent = shared port, a different port = move; 409
  `TlsAlreadyEnabled` when nothing would change), `POST /api/tls/http-mode`
  (`Open` / `LoopbackOnly` / `Disabled`; `Disabled` refused while TLS is
  off), and `POST /api/auth/rotate` (**HTTPS only**, 403 on plaintext) to
  replace the token over the pinned channel. There is no "disable TLS"
  endpoint — turning HTTPS off is a `nodesettings.json` edit on the host.
- Every request except `GET /api/version` requires
  `Authorization: Bearer <token>` — the Node's `AuthToken` from
  `nodesettings.json`. The check is constant-time; failures get a delay and
  count toward per-IP lockout, and requests are rate-limited (see the
  `Security` section of the [Node guide](../user/node.md#configuration-reference)).
- `GET /api/version` is intentionally **unauthenticated** — connectivity
  probing.
- Errors return a `NodeErrorResponse` with a `NodeErrorCodes` value
  (`InstanceNotFound`, `InstanceAlreadyRunning`, `InstallationInProgress`,
  `AuthenticationFailed`, `DiskSpaceInsufficient`, `ProcessStartFailed`, …).

## Versioning

Two independent version numbers, both reported by `GET /api/version`:

- **`ProtocolVersion`** (currently **1**) — the wire contract. Bumps **only on
  breaking change** (endpoint removed, DTO field removed/renamed/re-semanticed).
  New endpoints and new optional fields do *not* bump it. A client should
  compare versions and degrade gracefully — the official Manager shows an
  orange indicator on mismatch and keeps using the shared subset rather than
  refusing to talk.
- **`ContractsVersion`** (currently **4**) — the plugin-facing surface. Only
  relevant if your alternative Manager also loads PowerGSM plugins.

`NodeVersionResponse`: `Application`, `Version`, `Build`, `ProtocolVersion`,
`ContractsVersion`, `Runtime`, `Platform`.

---

## Endpoint map

### System

| Route | Purpose |
|---|---|
| `GET /api/version` | Identity + versions. **Unauthenticated.** |
| `POST /api/auth` | Validate a token (`NodeAuthRequest` → `NodeAuthResponse`). |
| `GET /api/status` | Host status (`NodeStatusResponse`): metrics, disk, instances summary. |
| `GET /api/system/prerequisites` | Probe named host prerequisites (e.g. `vcredist-2015-2022-x64`) → `PrerequisiteCheckResponse`. |
| `POST /api/system/staged-binary/begin` / `…/{uploadId}/chunk` / `…/{uploadId}/commit` | Chunked upload of a new binary for target `node`, `shim` (versioned install), `nodesetup` or `ctrlcsender` (Windows only; in-place swap); commit verifies size + SHA-256 and rejects a wrong-OS executable. |
| `POST /api/system/apply-update` | Apply the staged target. For `node` the Node exits and a survivor swaps + relaunches it (re-adopting running instances via their shims, auto-rollback if unhealthy); the other targets apply in place with no bounce. |
| `POST /api/system/restart` / `POST /api/system/shutdown` | Relaunch via the survivor / exit without relaunch. |
| `GET /api/tls/status`, `POST /api/tls/enable`, `POST /api/tls/http-mode`, `POST /api/auth/rotate` | TLS lifecycle + token rotation (see Transport). |

### Installation

| Route | Purpose |
|---|---|
| `POST /api/install` | Start an install/update (`InstallRequest`: the ordered `InstallStep` list a plugin produced, plus install path and optional `SteamCredential`). Long-running; returns initial `InstallProgressResponse`. |
| `GET /api/install/{installationId}/progress` | Poll progress. `InstallationOperationState` includes `WaitingForInput` when SteamCMD wants a Steam Guard code. |
| `POST /api/install/{installationId}/prompt` | Answer a pending prompt (`PromptResponse`, e.g. the Guard code). |
| `POST /api/install/{installationId}/cancel` | Cancel. |
| `POST /api/install/version-check` | Run a plugin-supplied version probe on the node (`AppVersionCheckRequest/Response`). |
| `POST /api/install/uninstall` | Delete installed files (`UninstallRequest`). |

### Instances

| Route | Purpose |
|---|---|
| `POST /api/instances/start` | `StartInstanceRequest` — see below; the heart of the protocol. |
| `POST /api/instances/stop` | `StopInstanceRequest` (graceful with force-kill fallback). |
| `GET /api/instances` | All instance statuses. |
| `GET /api/instances/{id}/status` | One status: `CurrentState`, `Pid`, `SupervisorPid` (the per-instance shim), `UptimeSeconds`, `CpuPercent`, `MemoryMb`, `CrashCount`, `LastExitCode`, `StateChangedAt`, `ErrorMessage`. |
| `GET /api/instances/{id}/logs` | **SSE stream** (`text/event-stream`) of live log lines. |
| `GET /api/instances/{id}/logs/recent` | Recent history from the ring buffer (viewer backfill). |
| `POST /api/instances/{id}/parse-rules` | Replace the declarative parse rules on a running instance (hot rule updates without restart). |
| `GET /api/instances/{id}/players` | Current `PlayerSession` list — node-tracked, works with no Manager attached. |
| `GET /api/instances/{id}/server-state` | `ServerStateResponse` (match state, tile, map…). |
| `GET /api/instances/{id}/chat` | Persisted `ChatMessage` history. Query params: `limit` (default 500) and `since` (ISO UTC timestamp — return only newer messages; offset-less strings are treated as UTC). |
| `POST /api/instances/{id}/rcon/connect` / `disconnect` / `command` / `GET …/rcon/status` | RCON proxying — the node holds the RCON connection. |
| `POST /api/instances/{id}/boot-flags`, `POST /api/instances/park`, `POST /api/instances/{id}/forget`, `GET /api/instances/{id}/boot-state` | Node-boot resurrection: live flag update (`ResurrectOnNodeBoot`, `AutoStartOnNodeBoot`, optional `BootStartDelaySeconds`), park a stopped instance's launch payload, forget it, inspect the persisted state. |
| `GET /api/instances/{id}/events[/stream]`, `POST …/events`, `…/events/sources`, `/watermarks`, `/parsetrace`, `/test-line`, `/purge` | The durable per-instance event stream (cursor read, SSE live-follow, Manager enrichment append, diagnostics, hard purge). |

### Files & generation

| Route | Purpose |
|---|---|
| `GET /api/instances/{id}/files` | List a managed directory (`FileEntry` list). |
| `GET /api/instances/{id}/files/download` | Download a file. |
| `POST /api/instances/{id}/files/upload` | Upload. |
| `POST /api/instances/{id}/files/rename` / `copy`, `DELETE /api/instances/{id}/files` | Manage. |
| `POST /api/instances/{id}/generate-map` | Run a plugin-built generation plan (`GenerateMapRequest/Response`); progress like installs. |

File access is constrained to the managed directories declared for the
instance — not arbitrary node paths.

---

## `StartInstanceRequest` — the protocol in one DTO

Everything a plugin "interprets" arrives at the node in this request:

| Field | Meaning |
|---|---|
| `InstanceId`, `ExePath`, `Arguments`, `WorkingDirectory`, `EnvironmentVars` | What to run. |
| `CrashPolicy`, `MaxCrashCount`, `CrashWindowMinutes`, `CrashCountResetAfterSeconds`, `MinRestartDelayMs` | Autonomous crash-restart policy — the node enforces it with no Manager attached. |
| `ResurrectOnNodeBoot`, `AutoStartOnNodeBoot`, `BootStartDelaySeconds` | Node-boot behaviour, persisted on the node's snapshot so it works with no Manager attached. |
| `RconPort`, `RconPassword`, `RconProtocol` | RCON wiring (informational to the node — the Manager drives `/rcon/connect`). |
| `PollSources` | Resolved REST state-poll sources (contracts v4) the node polls into the event stream. |
| `LogFilePaths` | Absolute file paths to tail (already token-substituted by the Manager). When present, files are the authoritative log source. |
| `LogParseRules` | The declarative regex rules — the node applies them to every line, maintaining `/players`, `/server-state`, `/chat` standalone. |
| `StdoutIsLog`, `RequiresConsoleIsolation`, `LogTailerStartDelayMs` | Capture tuning. |

The response is `InstanceStatusResponse`. Note `SupervisorPid`: each instance
runs under a per-instance **GSM.Shim** supervisor that owns the process's
stdio; the Node talks to the shim over a local socket. That's why Node
restarts/updates don't disturb running games — and it's transparent to API
clients; you never talk to a shim directly.

---

## Building your own Manager

A Manager, at minimum, is a client that:

1. **Stores node addresses + tokens** and talks to each node's API.
2. **Produces launch/install data.** This is the real work. Two options:
   - **Load PowerGSM plugins** (compile the `.vb` files with Roslyn against
     `GSM.Contracts`, like the official Manager) — you inherit every game's
     logic for free. Your host must supply the plugin-facing services the
     shipped plugins expect.
   - **Hardcode or re-implement per-game logic** in your own stack — fine for
     a purpose-built panel that manages one or two known games; you just need
     to construct valid `StartInstanceRequest`s and `InstallRequest`s.
3. **Polls or streams state:** `GET /api/instances` for status, the SSE log
   stream for live output, `/players` + `/chat` + `/server-state` for game
   state. Design for **node-as-source-of-truth**: the node keeps recording
   while your app is offline; reconcile on connect rather than assuming you
   saw every event.

For a **web-based Manager** specifically:

- The API is already HTTP+JSON+SSE, so a browser front-end maps naturally —
  but **don't ship the node token to the browser**. Put a small backend
  between the browser and the nodes that holds tokens, terminates TLS, does
  your user auth/multi-tenancy, and proxies (the node's CORS posture and
  bearer-token model assume a trusted client).
- SSE log streaming proxies cleanly through standard reverse proxies (disable
  response buffering).
- Respect the node's rate limits (default 600 req/min/IP) — a proxy funnels
  many users through one IP; either raise `RequestsPerMinutePerIp` in
  `nodesettings.json` or coalesce polling server-side.

Version-negotiate like the official Manager: read `GET /api/version` first,
tolerate a newer node, and treat unknown response fields as ignorable — the
protocol only breaks on a `ProtocolVersion` bump.

---

## Node-side data model worth knowing

- The node persists instance→process mappings, chat, and event cursors in its
  own SQLite DB (in its data directory) — that's what powers re-adoption and
  Manager-free tracking.
- All timestamps are UTC.
- Chat retention is node-side; player/server state is in-memory + rebuilt
  from parse rules.

For the protocol details of any specific DTO, read
[`NodeApiContract.vb`](../../GSM.Contracts/NodeApiContract.vb) — every class
there is the wire shape, with doc comments.
