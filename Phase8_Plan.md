# Phase 8 — Node self-update + restart-survivable instances `[shipped — 0.4.0]`

## Goal

Make a Node binary update — and any Node restart — a non-event for running
game instances, and make shipping those updates push-button. Two sub-phases:

1. **8-1 — Per-instance shim + adopt-on-restart.** A tiny, rarely-updated
   `GSM.Shim` process per instance spawns the game server as *its* child and
   owns the game's stdin/stdout/stderr, pumping output to the Node over a
   named pipe (Windows) / Unix domain socket (Linux) and relaying stdin +
   stop requests. The Node never owns the game's pipes, so Node death (crash,
   update, manual restart) stops degrading stdout-piped servers, and the Node
   re-adopts by reconnecting to live shims instead of re-deriving a `Process`
   handle it can't fully reconstruct.
2. **8-2 — Detect / stage / apply.** The 5l self-update patterns,
   Node-flavoured: per-node version detection, chunked binary push with
   SHA-256, an HTTP-driven self-shutdown + external relaunch that swaps the
   binary while the Node is down, a `/api/version` health-check, and rollback.
   With 8-1 in place, instances keep running throughout. **Full design +
   slices 6–8: see `Phase8-2_Plan.md`.**

**Release gate: ships before 0.4.0 is tagged.** Ordered before Phase 9 so
8-1 hardens the Node before Phase 9 churns it, and 8-2 removes per-machine
manual binary copies as Phase 9 develops.

---

## Status

`[shipped in 0.4.0]` — all sub-phases (8-1 shim, 8-2 node self-update incl.
7b/7c shim + NodeSetup co-update and the slice-8 health-gate, 8-3 shim
rediscovery) built clean and shipped in 0.4.0. Runtime verification of the
slice-8 auto-rollback and the 8-3 rediscovery path is deferred to a
post-release live check. Designed/confirmed with Site 2026-06-19; all decisions
below are confirmed. **Slice 1a** (`GSM.Shim.Protocol` + `GSM.Shim` projects,
framing, `Hello`/`HelloAck`) and **1b** (Windows native spawn via
`CreateProcessW`/`CreatePipe`, stdout/stderr pumps, basic stop, `SpawnAck`)
landed 2026-06-22; both pass `GSM.Shim --self-test` on Windows. **Slice 1c**
(Linux `posix_spawn`/`pipe2`, `POSIX_SPAWN_SETSID` for a new session so a
terminal Ctrl+C can't reach the game, argv rebuilt from the Win32-quoted
spec string, `waitpid`/SIGKILL) landed 2026-06-23 and passes
`GSM.Shim --self-test` on the Linux node (handshake → SpawnAck → marker →
StopGame → Exited code 137 = 128 + SIGKILL).

**Slice 2** (the Node-side `ProcessManager` shim path) landed 2026-06-22,
build-verified on Windows (real game test deferred to the node). It split
into **2a** (Node.vbproj wires the protocol reference + the versioned shim
co-location/publish targets; `GSM.Shim\{version}\` folder populated),
**2b-1** (`ShimSession` Node-side client + `ManagedInstance` shim fields +
`ExecutionMode` enum + a `GSM.Node --shim-self-test` that drives a real
`ShimSession` against the deployed shim — PASS on Windows: launch → connect →
handshake → SpawnAck → marker line → StopGame → Exited), and **2b-2** (the
`ProcessManager` wiring: `StartInstanceAsync` async + shim routing,
`StartViaShimAsync`, mode-aware `FinalizeStart` / `HandleProcessExited` /
`StopInstanceAsync` / `RestartInstanceAsync` / `BuildStatusResponse` /
`ScheduleCrashCountReset`). **Slice 3** (adopt-on-restart for shim
instances) split into **3a** (shim reconnect mechanism — DONE, proven on
Windows via `--shim-reconnect-test` 2026-06-22: same game pid + same shim pid
+ replayed marker across a detach/re-adopt) and **3b** (Node adoption
integration: snapshot shim columns + migration, `FinalizeStart` re-enable,
`TryAdoptOne`→`TryAdoptShim` reconnect, clean-shutdown `Detach`). **Slice 4**
(Strategy B/C through the shim — `SpawnWindowsHiddenConsole` for B/C, the
routing gate opened to all strategies, the shim skipping pumps for the
pipe-less B/C case) and **Slice 5** (graceful stop: the Node delivers
`CTRL_C_EVENT`/SIGTERM to the game by PID via the existing `CtrlCSender`,
then escalates through the shim's `kill`/`Shutdown`; the shim drains its
pumps before `Exited` so late-stage output isn't lost; `ConsoleCtrlSuppression`
re-enables a user-typed Ctrl+C as a graceful Node shutdown) both landed
2026-06-23. **The entire Windows side of 8-1 is now proven end-to-end**
(2026-06-23): spawn under shim → Ctrl+C detaches the shims + closes the Node
→ restart re-adopts the same game pid → graceful stop. **Slice 1c** (Linux
`posix_spawn`/`pipe2`) landed 2026-06-23, so 8-1 is now cross-platform — the
shim's protocol, transport (Unix sockets included), supervisor, adoption, and
graceful stop were already OS-agnostic, and the native game spawn was the
last Linux-only leaf. What's left is the on-node proof on Linux (a real game
under the shim + a Node-restart re-adopt), batched into the next Linux
bring-up; the shim self-test already exercises spawn → stop → exit there.
Grounded against the live `GSM.Node\ProcessManager.vb` read
2026-06-19/22/23, not the Backlog narrative.

---

## Background — what already shipped, and the precise gap

A large part of "restart-survivable instances" landed in **May 2026** (the
process re-adoption work; see `Backlog.md` → *Hot-swap node binary*). It is
important not to re-design it:

- `InstanceSnapshots` carries nine recovery columns (`ExePath`, `Arguments`,
  `WorkingDirectory`, `LogFilePathsJson`, `ParseRulesJson`, `Strategy`,
  `StdoutIsLog`, `RequiresConsoleIsolation`, `LogTailerStartDelayMs`) on top
  of the base row (`InstanceId`, `State`, `Pid`, `StartedAtUtc`,
  `CrashPolicyJson`, `StopIntentPending`). `FinalizeStart` writes them via
  `_database.SaveInstanceSnapshot(...)` on every start.
- `ProcessManager.AdoptSnapshots()` runs synchronously at startup before
  `app.Run()`. `TryAdoptOne(InstanceSnapshotRow)` looks up the saved PID,
  verifies identity by matching `proc.StartTime` to `snapshot.StartedAtUtc`
  (60-second skew tolerance), rebuilds the `ManagedInstance`, restores crash
  policy, re-spins file tailers from `TailerPositions`, and re-registers
  EventStore rules. The Manager's `UpdateParseRulesAsync` re-push reconciles
  rule drift on reconnect.
- **Net result today:** file-tailed instances survive a Node restart with
  players connected. On Windows that's LO (Strategy B) and Factorio
  (Strategy C).

**The gap is Strategy A.** `ProcessManager.SpawnStrategy` has three values:

- `StdoutCapture` (A) — redirected stdio, no console; the Node owns the
  child's stdout/stderr pipes and drains them via `BeginOutputReadLine`.
- `HiddenConsoleDirect` (B) — native `CreateProcessW` + `CREATE_NEW_CONSOLE`
  + `SW_HIDE`; game writes its own log file, Node tails the file.
- `HiddenConsoleWrapped` (C) — same as B but `cmd.exe /S /c "<exe> <args>"`.

`ResolveStrategy` returns **`StdoutCapture` for every non-Windows host,
always.** So on Linux, LO *and* Factorio are Strategy A. `TryAdoptOne` can
recover their PID but not their stdout — the read end of the pipe died with
the old Node, and you cannot reconnect to a pipe of a process you didn't
spawn. The code says so directly (`ProcessManager.vb` ~line 471):

> *Strategy A … we can't reconnect to a stdout pipe of a process we didn't
> spawn. The adopted process keeps running but its stdout is no longer
> captured. Neither LO (Strategy B) nor Factorio (Strategy C) hits this path
> today; theoretical for any future plugin that opts into A.*

That "theoretical" is a Windows-deployment view. The moment the Node runs on
Linux — Site's actual environment — *every* instance is Strategy A and the
gap is live: after a Node restart the game keeps running but its output is
gone, EventStore tracking goes dark, and on a broken-pipe write the game can
hang. **Closing this is 8-1's core job.** The shim also unlocks the parked
true-UE4-`CTRL_C` graceful shutdown as a bonus, because a shim owning raw
pipes + a process group can deliver a real `CTRL_C_EVENT`.

---

## Confirmed decisions

1. **Shim owns all three standard streams** — stdin, stdout, stderr — not
   just stdout. stdin ownership is what enables a graceful stop (Factorio
   `/quit` on stdin; a real `CTRL_C_EVENT` for UE4) instead of a kill.

2. **Uniform target architecture: every instance runs under a shim.** One
   model — "the Node talks to shims, never directly to game processes" —
   rather than a stdout-only special case with two adoption paths. The
   per-instance process cost is trivial; collapsing to one execution +
   adoption path is worth more than the saved process. *Rollout is staged*
   (see Slices): Strategy A first because it closes the live gap, then B/C.

3. **The shim is wielded by the Node, never by the Manager or plugins.**
   Plugins are Manager-side and only ever produce *data* (`BuildLaunchArguments`,
   parse rules, the `StdoutIsLog` / `RequiresConsoleIsolation` /
   `LogTailerStartDelayMs` flags). The Node consumes that spec and is the
   thing that spawns today. After 8-1 the Node hands the same spec to the
   shim instead of calling `Process.Start`/`CreateProcessW` itself. **No
   plugin contract changes; no Manager wire-API changes.** What changes is
   internal to the Node: `ProcessManager` stops holding a `Process` handle to
   the *game* and instead holds a shim connection (plus the shim PID and game
   PID for reporting). Its consumers (endpoints, adoption, tailers,
   crash policy) keep their shapes; adoption gets *simpler* (reconnect to a
   live shim vs. re-derive a `Process`).

4. **Native spawning, not `System.Diagnostics.Process`.** The shim spawns the
   game via `CreateProcessW` + `CreatePipe` (Windows) / `posix_spawn` +
   `pipe2` (Linux), owning the raw handles. This is what makes the
   `CTRL_C_EVENT` / process-group story possible and sidesteps the
   "redirected pipe died with the parent" problem at its root. Much of the
   needed interop already exists in `ProcessManager` (`STARTUPINFOW`,
   `PROCESS_INFORMATION`, `SpawnHiddenConsoleProcess`,
   `SpawnWrappedConsoleProcess`, `SendCtrlCToProcess`, `TrySendConsoleCtrlC`,
   `TrySendTaskkill`, `SendSigTermToProcess`, `WrapInSetsidIfLinux`) and the
   `GSM.CtrlCSender` project — these migrate/share into the shim rather than
   being written fresh.

5. **Versioned, append-only Node↔shim protocol from day one.** A new Node
   must always be able to talk to an older shim; the handshake carries a
   protocol version each side downshifts to. New fields are additive; nothing
   is ever removed. This is the cheap insurance that makes a shim update
   *optional and lazy* — a running game's old shim keeps working against a
   new Node indefinitely.

6. **Shim binaries are versioned side-by-side on disk; a running shim is
   never overwritten.** Shims live at `GSM.Shim\{version}\GSM.Shim.exe`
   (path TBD). A new instance launches the newest version present; running
   shims keep executing from their own (still-locked-on-Windows) file; old
   version folders are reclaimed once no live shim references them (refcount
   via the shims' reported versions, like 5l's `.updates` gc). A shim update
   is therefore an additive drop, never a swap-under-a-running-process —
   which is what makes "shim updates are never forced." *(Rejected: FD-passing
   handoff — `SCM_RIGHTS` / `DuplicateHandle` / `WSADuplicateSocket` — to move
   a live game's pipes to a successor shim. That is exactly the
   file-descriptor-handoff complexity the Backlog rejected for the
   node-internal hot-swap; reintroducing it recursively betrays the
   keep-it-simple premise. Also noted but skipped: Linux-only `execve()`
   in-place shim self-swap.)*

7. **Shim runtime: self-contained .NET 8 single-file**,
   matching `GSM.Watchdog` / `GSM.CtrlCSender`, doing native spawn via
   P/Invoke (decision 4). Self-contained publish bundles the runtime so
   neither OS needs .NET installed; `InvariantGlobalization=true` drops the
   libicu dependency, leaving only libc-class libs present on every Linux.
   Native spawn from a .NET host is *not* a contradiction — P/Invoke reaches
   `CreateProcessW`/`posix_spawn` directly; the managed `Process` class is
   simply not used. *Alternatives:* a fully-native binary (static Rust/Go/Zig
   would be more portable than .NET, but adds a toolchain + CI to an
   otherwise-VB.NET solo project); or .NET Native AOT (small native binary,
   fast startup) which is C#-only and would make the shim the one C# project
   in a VB solution — park as a later optimization if per-instance footprint
   bites.

8. **8-2 relaunch: HTTP self-shutdown + external relauncher + service
   recovery.** The Node can't overwrite its own running image, so it doesn't
   try (the 5l-3 `apply.cmd` pattern, Node-flavoured): stage to
   `.updates\{version}\`, self-shutdown over HTTP, and a *separate*
   relauncher — a generated apply-script plus platform service recovery
   (Windows service recovery / systemd `Restart`) — does the copy while the
   process is gone, backs the old binary to `.bak`, starts the new Node,
   health-checks `/api/version`, restores `.bak` on failure. HTTP
   self-shutdown dodges the service-ACL privilege-escalation question
   entirely. On Windows the script waits for the Node to leave `tasklist`
   before copying (handle-lingering / AV race), same as the Manager's
   apply.cmd.

9. **Migration is per-instance state, not a release deadline.** Each instance
   records its execution mode (shim vs. legacy-direct) and shim protocol
   version in its snapshot. New instances start under a shim; existing
   instances keep their legacy-direct adoption path until they next restart.
   The Node supports both adoption paths until draining them is trivial —
   and "direct mode" can simply remain a thin permanent fallback (it is
   today's `AdoptSnapshots`-with-`Process`-handle path, barely any code). A
   long-running instance never "falls out of spec": it is a supported legacy
   mode, surfaced and drained, not a deprecated-with-a-deadline liability.

10. **Unifying lifecycle principle.** Decisions 5/6/9
    are one mechanism: *every instance carries a recorded execution mode +
    shim protocol version; the Manager surfaces whatever is out-of-date (an
    orphan-banner-style nudge, cf. 5m-2e); the existing RestartCoordinator is
    the graceful drain; nothing is ever force-swapped under a running game.*
    This single idea answers "what if shims must be replaced" (decision 6),
    "what if an instance falls out of spec" (decision 9), and the 8-1 → 8-2
    rollout migration at once. The protocol-versioning discipline (decision 5)
    is what holds it together.

11. **PID reporting: the instance PID stays the game/server process; the
    shim PID is node-internal but also surfaced.** The shim spawns the game
    via native `CreateProcess`/`posix_spawn` and reports the *game* PID back,
    so `InstanceStatusResponse.Pid` keeps meaning "the server process"
    exactly as today — no operator-visible change. It actually *improves* the
    Strategy C case: today Factorio-on-Windows reports the `cmd.exe` wrapper
    PID (the spawn tracks the cmd PID), whereas a shim spawning natively with
    its own hidden console drops the wrapper and reports the real game PID.
    The shim PID lives in `ManagedInstance.ShimPid` + the snapshot for
    adoption and shim-health, and is *also* exposed as an optional
    `InstanceStatusResponse.SupervisorPid` so the Manager can show both in a
    detail view ("Server PID 12345 · Supervisor 12340") — primary display
    stays the game. Process metrics follow the game too: `BuildStatusResponse`
    reads `WorkingSet64` for `MemoryMb`, so the node keeps a by-PID *metrics*
    handle on the game (a by-PID handle is fine for metrics — it's only the
    stdout *pipe* you can't reacquire that way) rather than reading the shim's
    few MB. Bonus: the shim PID is stable across game crash-restarts (the
    shim persists and re-spawns on the Node's command), so it's a cleaner
    adoption anchor than the churning game PID.

---

## Architecture (8-1)

```
            Manager  ──(unchanged /api/instances wire API)──►  Node
                                                                 │
                                              per-instance pipe/socket
                                                                 │
                                                              GSM.Shim ──spawns──► game server
                                                              (owns stdin/stdout/stderr,
                                                               buffers output, relays stop)
```

The shim sits strictly *below* the Node's existing wire API. The Manager and
plugins are unaware it exists. The Node's `ProcessManager` is the only
component that gains a notion of shims.

**Endpoint naming** (derived from instanceId so adoption can find it):
- Windows: `\\.\pipe\powergsm-shim-{instanceId}`
- Linux: `{nodeDataDir}/shims/{instanceId}.sock`

**Protocol** — length-prefixed frames (`UInt32` length + `Byte` type +
payload); a versioned `Hello`/`HelloAck` handshake first in each direction.

- Node→shim: `Hello(protocolVersion)`, `Spawn(spec)`, `Stdin(bytes)`,
  `Stop(kind, timeoutMs)` where kind ∈ {ctrlc, sigterm, stdin-line, kill},
  `Detach` (Node going down cleanly — keep the game running), `Shutdown`
  (kill game + exit shim).
- shim→Node: `HelloAck(protocolVersion, shimVersion, gamePid, gameState)`,
  `Stdout(frame)`, `Stderr(frame)`, `Exited(code)`, `Heartbeat`.

**Spawn spec** carries exactly what `ProcessManager` resolves today: exe,
args, working dir, env, and the strategy/console flags (so a shim can host a
hidden-console Strategy B/C game as well as a redirected-stdio Strategy A
game). The spec is the same data `FinalizeStart` already has on
`ManagedInstance`.

**Shim-side catch-up buffer** — a bounded ring of recent stdout/stderr (size
mirrors the Node's `RingBufferStore`), so a reconnecting Node replays lines
emitted while it was down. The Node's existing dedup (EventStore name-dedup,
`INSERT OR IGNORE`, the same machinery the file-tailer backfill relies on)
absorbs any overlap, so the shim can replay generously.

**Adoption with a shim** — `TryAdoptOne`'s shim branch reconnects to the
instance's endpoint, handshakes, and resumes the stream from the shim's
buffer. The `Process.GetProcessById` + `StartTime` dance is replaced by a
shim handshake (shim reports the live game PID + state). This is the gap
fix: stream continuity survives because the shim never let go of the pipes.

**Shim crash (residual risk)** — if a shim dies, its game is orphaned exactly
as today's Strategy A would be: strictly *no worse* than the current
behaviour. Mitigated by the shim being tiny and rarely-updated. The Node
detects the dead endpoint + still-alive game PID and surfaces it. We do *not*
bind the game's life to the shim (e.g. job-object / `PDEATHSIG`) — that would
kill the game on shim crash and defeat the whole point.

---

## New surfaces

### New project — `GSM.Shim.Protocol`

- Small net8.0 class library holding the shared Node<->shim wire protocol
  (frame format, message DTOs, transport listener/client), referenced as a
  normal managed assembly by BOTH `GSM.Node` and `GSM.Shim` so the
  versioned, append-only protocol is single-sourced (decision 5). Added in
  slice 1a — option A of the "where the protocol lives" choice (vs.
  Node-references-the-exe, or a shared linked source file).
- Files: `Protocol.vb` (version const, `FrameType`, `Frame`, DTOs, JSON
  codec), `FrameConnection.vb` (framed reader/writer + `Hello`/`HelloAck`
  handshake), `Transport.vb` (endpoint parse + named-pipe / Unix-socket
  listener + client). Endpoint string form `pipe:<name>` (Windows) /
  `unix:<path>` (Linux).
- VB note: `Span`/`ReadOnlySpan` are unusable from VB (BC30668 — ref
  structs), so the 4-byte length prefix uses plain little-endian byte ops
  and JSON decodes via a `String`, not a span overload.

### New project — `GSM.Shim`

- Self-contained .NET 8 console — `OutputType=Exe`, not `WinExe`. Unlike
  the Watchdog (a logon task would flash a console), the shim is launched
  only by the Node, which spawns it with a no-window flag (slice 1b), so
  nothing flashes; a console Exe also lets `GSM.Shim.exe` with the
  self-test flag print results when run by hand. Entry point parses
  `--instance-id` + `--endpoint`, creates the pipe/socket, listens,
  accepts the Node connection, and replies `HelloAck` to the Node's
  `Hello` (slice 1a — landed, passes the self-test on Windows).
- Native spawn module (P/Invoke `CreateProcessW`/`CreatePipe` /
  `posix_spawn`/`pipe2`), owning raw stdio handles; the Strategy A redirected
  path and the B/C hidden-console paths both land here. Migrates the existing
  `STARTUPINFOW`/`PROCESS_INFORMATION` interop + `WrapInSetsidIfLinux` out of
  `ProcessManager`.
- Graceful-stop module migrating `SendCtrlCToProcess` / `TrySendConsoleCtrlC`
  / `TrySendTaskkill` / `SendSigTermToProcess` and the `GSM.CtrlCSender`
  logic; with the shim owning the process group, `GenerateConsoleCtrlEvent`
  finally reaches UE4.
- Bounded output ring buffer + framed protocol reader/writer.
- Build/publish wiring mirroring `GSM.Watchdog`'s co-location targets so the
  versioned shim binary lands next to / under the Node on build + publish.
- *As-built (slice 1b, Windows):* `NativeSpawn.vb` — `IGameProcess` +
  `WindowsGameProcess` (raw process handle; `WaitForSingleObject` exit watch,
  `TerminateProcess` kill) + the Strategy-A redirected-stdio spawn
  (`CreatePipe` + `STARTF_USESTDHANDLES` + `CREATE_NO_WINDOW`). `Supervisor.vb`
  — owns one game, serve loop (Spawn/Stdin/StopGame/Shutdown/Detach),
  stdout/stderr pumps → frames, `OutputRing` (256 KB, replay wired in slice 3),
  `SpawnAck`/`Exited`. Stop is a basic terminate for now; true graceful
  `CTRL_C`/`SIGTERM` is slice 5. Protocol gained a `SpawnAck` frame (shim→Node
  game-PID reply) and `SpawnSpec.Arguments` is a single Win32-quoted string.

### `GSM.Node\ProcessManager.vb`

- `SpawnGameProcess` gains a shim path: instead of `New Process()` +
  redirect (Strategy A) or `SpawnHiddenConsoleProcess`/`SpawnWrappedConsoleProcess`
  (B/C), launch the versioned shim and send `Spawn(spec)`. The three
  strategies become *spec flags sent to the shim*, not three Node-side spawn
  routines.
- `ManagedInstance` gains shim fields: `ShimEndpoint`, `ShimPid`,
  `ShimProtocolVersion`, `ExecutionMode` (Shim | Direct). It keeps a game-side
  handle for metrics + the reported `Pid` (by-PID, decision 11) alongside the
  shim connection; `Pid` stays the game PID, `ShimPid` the supervisor. In
  Direct mode `Process` stays the game process as today.
- `AdoptSnapshots`/`TryAdoptOne` gain a shim branch (reconnect + handshake);
  the existing PID+StartTime branch stays as Direct-mode adoption.
- `HandleProcessExited`, `EvaluateRestartPolicy`, `AttachProcessHandlers`,
  the file tailers, and `_eventStore` registration keep their shapes — they
  consume shim stream/exit events instead of `Process` events.
- Stop path routes `Stop(kind)` to the shim.
- `BuildStatusResponse` keeps `resp.Pid` on the game PID and keeps
  `WorkingSet64` / `MemoryMb` reading the game-metrics handle;
  `InstanceStatusResponse` gains an optional `SupervisorPid` (the shim) —
  additive, no `ContractsVersion` bump. See decision 11.
- *As-built (slice 2, 2026-06-22, build-verified Windows / node-test-later):*
  - **Strategy A only routes through the shim**; B/C stay Direct (slice 4),
    exactly as the staged-rollout plan intends. Routing gate in
    `StartInstanceAsync`: `Strategy = StdoutCapture AndAlso Not DisableShim`.
  - **Kill-switch `NodeConfiguration.DisableShim`** (default `False` = shim
    on). Lets a live node fall back to legacy direct-A.
  - New file **`GSM.Node\ShimSession.vb`** — the Node-side client (launch
    shim via `Process.Start` no-window for the `ShimPid` handle, connect
    (retry-with-timeout: Windows pipe waits, Linux socket retries),
    handshake, `Spawn`, frame read-loop, byte-level `LineSplitter` so
    stdout chunks become whole UTF-8 lines for the EventStore, stop/stdin/
    shutdown/detach, `ExitedTask`). Logger coalesced to `NullLogger` (VB
    can't `?.`-call the `ILogger` extension methods).
  - New file **`GSM.Node\ShimSelfTest.vb`** + a `GSM.Node --shim-self-test`
    entry (NodeProgram `AttachConsole` since the Node is WinExe; writes
    `shim-selftest-result.txt`). The standing Node-side client harness.
  - `ProcessManager` ctor now takes `NodeConfiguration` (DI); `_shimSocketDir
    = {DataDirectory}\shims`. `StartViaShimAsync` builds the `SpawnSpec`
    with `Environment` = a **full copy of `psi.Environment`** (the shim
    *replaces* the game env block, so passing only overrides would drop
    PATH etc.); wires `onLine`→buffer/EventStore (gated on `CaptureStdout`)
    and `onExited`→`LastShimExitCode`+`HandleProcessExited`.
  - **Shim-mode Stop is hard-kill** (`Stop("kill")` + wait `Exited` +
    `Shutdown` fallback) until graceful lands in **slice 5**; Site accepted
    the interim hard-kill ("not a big deal if the test stuff gets killed").
  - **Snapshot is *skipped* in shim mode** (`FinalizeStart` gates
    `SaveInstanceSnapshot` on `ExecutionMode <> Shim`) — a Direct-style
    snapshot would let the next node startup adopt the game PID out from
    under its live shim. This **defers the planned snapshot shim-columns +
    migration to slice 3** (shim-aware adoption), rather than slice 2.
  - Restart in shim mode tears down the old shim (async-disposed in
    `HandleProcessExited`) and launches a fresh one via `StartViaShimAsync`.

### Snapshot / DB (`_database`)

- `SaveInstanceSnapshot` / `InstanceSnapshotRow` / `LoadAllInstanceSnapshots`
  gain the shim columns (`ShimEndpoint`, `ShimPid`, `ShimProtocolVersion`,
  `ExecutionMode`). One additive migration; existing rows default to
  Direct mode (so already-running pre-shim instances adopt via the legacy
  path — decision 9).

### 8-2 — Node endpoints (`GSM.Node\Endpoints\NodeEndpoints.vb`, `SystemEndpoints`)

- `POST /api/system/staged-binary` — chunked upload + SHA-256 verify →
  stage to `.updates\{version}\` (cross-host nodes have no shared FS, so this
  is HTTP for everyone).
- `POST /api/system/prepare-restart` — flush SQLite, checkpoint
  `TailerPositions`, finalize chat/state, so the post-restart Node resumes
  cleanly.
- `POST /api/system/shutdown` — write the apply-script, exit clean (so the
  watchdog/relauncher owns the swap, not the dying process).
- `/api/version` (exists) is the post-restart health check; it already
  reports `build` / `protocolVersion` / `contractsVersion` / `platform`.

### 8-2 — Manager side

- Per-node version detection reusing the 5l `GitHubReleaseChecker` /
  `SemanticVersion` patterns, comparing the Node's `/api/version` `build`
  against the latest Node release asset. (Node binaries ship as their own
  asset alongside the Manager zip.)
- A Node-update UI surface mirroring the Manager's self-update dialog
  (detect → stage → apply with the same pre-flight/health-check/rollback
  shape), per node in the tree.

---

## Slices (confirm-gated, in order)

**8-1**

1. **`GSM.Shim` skeleton + protocol.** New project; pipe/socket listener;
   versioned `Hello`/`HelloAck`; framed reader/writer; native spawn of a
   trivial child; stdout pumped to a connected client. *Test:* a throwaway
   harness drives spawn + stream + stop against a dummy process on both OSes.
   *Build split:* **1a** (DONE — `GSM.Shim.Protocol` + `GSM.Shim` projects,
   framing, handshake; `--self-test` PASS on Windows), **1b** (DONE — Windows
   native spawn via `CreateProcessW`/`CreatePipe` + stdout/stderr pumps +
   basic stop + `SpawnAck`; self-test drives spawn/stream/stop/exit on
   Windows), **1c** (Linux `posix_spawn`/`pipe2`, tested on the node).
   The Node-side wiring (protocol reference, versioned co-location target,
   `ProcessManager` shim path, `ManagedInstance`/snapshot columns) is slice 2.
   The `--self-test` flag in `GSM.Shim` is the standing harness.
2. **Node spawns Strategy A through the shim.** *(DONE — build-verified
   Windows 2026-06-22; live game test deferred to the node.)* Split **2a**
   (Node.vbproj protocol reference + versioned shim co-location/publish
   targets), **2b-1** (`ShimSession` client + `ManagedInstance` fields +
   `ExecutionMode` + `--shim-self-test`, PASS on Windows), **2b-2**
   (`ProcessManager` wiring: async `StartInstanceAsync` + `StartViaShimAsync`;
   mode-aware finalize/exit/stop/restart/status). Strategy A only; B/C remain
   Direct (slice 4). Kill-switch `DisableShim` (default off). **The
   `ManagedInstance`/snapshot shim *columns* + migration moved to slice 3** —
   2b *skips* snapshotting shim instances rather than writing a Direct-style
   row that would mis-adopt; shim adoption + its DB shape land together in 3.
   Shim-mode stop is hard-kill until slice 5.
3. **Adopt-on-restart for shim instances.** Split **3a** (shim reconnect
   mechanism) + **3b** (Node adoption integration).
   - **3a — DONE, proven on Windows 2026-06-22.** The supervisor now keeps
     the game + pumps + output ring alive across Node disconnects, re-accepts
     the next (re)connecting Node, and replays the ring; it exits only on
     Shutdown or game-exit (`Supervisor` rewritten around a swappable current
     connection + a send-semaphore serialising ring-append/live-write against
     snapshot/replay; `Program` runs `RunAcceptLoopAsync`). `HelloAck` gained
     `ShimPid` (adoption anchor). Node-side `ShimSession.AdoptAsync` (connect
     to an existing endpoint, no launch; learn pid/state from the handshake)
     + `DetachAsync` (release without killing) + `_ownsShim` gating the kill.
     Proven by `GSM.Node --shim-reconnect-test`: start under a shim → detach
     (shim+game live) → adopt from a fresh `ShimSession` → **same game pid +
     same shim pid (from HelloAck) + replayed marker** → kill → Exited.
   - **3b — code applied, build-verified Windows 2026-06-22; node-proof
     pending.** The Node adoption integration:
     - `InstanceSnapshots` gained the four shim columns
       (`ShimEndpoint`/`ShimPid`/`ShimProtocolVersion`/`ExecutionMode`, the
       last 0 = Direct) via the existing additive `PRAGMA table_info` +
       `ALTER TABLE` migration; `SaveInstanceSnapshot` (new *optional* params,
       so a stray caller still writes Direct), `LoadAllInstanceSnapshots`, and
       `InstanceSnapshotRow` carry them.
     - `FinalizeStart` now snapshots shim instances too (the 2b skip removed),
       writing the shim columns + `ExecutionMode = Shim`.
     - `StartViaShimAsync` refactored to share its stdout/exit callback wiring
       (`CreateShimSession`) + field-stamping (`ApplyShimSession`) with a new
       `AdoptViaShimAsync` (reconnect, no spawn, 8 s connect bound).
     - `TryAdoptOne` branches to `TryAdoptShim` when `ExecutionMode = Shim`:
       rebuilds the managed shell (+ a `StartInfo` for post-adopt restart),
       **registers EventStore + publishes into `_instances` *before* the
       adopt** (the shim replays its ring the instant the socket connects, so
       rules/instance must already be armed or replayed lines drop), then
       reconnects; rolls both back + discards the row on a dead endpoint.
     - Clean-shutdown `Detach`: `ProcessManager.DetachShimsForShutdown` wired
       to `IHostApplicationLifetime.ApplicationStopping` in `NodeProgram` —
       sends `Detach` to each shim on a graceful stop (a hard kill just drops
       the pipe, which the shim also treats as keep-the-game).
     *Test (pending, on the node):* start a Linux Strategy-A instance under a
     shim, restart the Node, confirm same game pid + stdout/EventStore
     continuity with a player connected — the gap closed. Windows can't prove
     it (no Strategy-A game there); 3a already proved the reconnect mechanism.
4. **Strategy B/C through the shim (uniform).** *(Code applied,
   build-verified Windows 2026-06-22; B/C live test + node-restart pending.)*
   The shim's `NativeSpawn.SpawnWindows` now dispatches on `spec.Strategy`:
   A keeps the redirected-stdio path (`SpawnWindowsRedirected`); **B/C use a
   new `SpawnWindowsHiddenConsole`** (`CREATE_NEW_CONSOLE` +
   `STARTF_USESHOWWINDOW`/`SW_HIDE`, no pipes), with C wrapping in
   `cmd.exe /S /c "<exe> <args>"` for AttachConsole(parent)-defeating games
   (Factorio), mirroring ProcessManager's quoting. B/C game processes carry
   `Nothing` streams; the Supervisor starts pumps only when streams exist, so
   B/C just get the exit watch (the Node tails the log file exactly as in
   direct mode). ProcessManager's routing gate dropped its
   `Strategy = StdoutCapture` restriction (now `Not DisableShim` for every
   strategy) and `StartViaShimAsync` sends the real `managed.Strategy`.
   **Adoption needed no new code** — 3b's `TryAdoptShim` is strategy-agnostic
   and re-spins file tailers. Direct mode retained for legacy rows + the
   kill-switch. *As-built notes:* C tracks the **cmd.exe** PID (parity with
   today's direct C; the decision-11 "report the real game PID for C"
   improvement is deferred); B/C stream no stdout (file-tailed). *Test:*
   Windows LO + Factorio start under shims; Node restart survives; a pre-shim
   snapshot still adopts Direct.
5. **Graceful stop via shim (the bonus).** *(DONE — Windows end-to-end proven
   2026-06-23.)* **Option D — the Node delivers the stop signal to the *game*
   by PID, not through the shim:** `AttachConsole`/`CTRL_C_EVENT` (Windows, via
   the existing `GSM.CtrlCSender`) and `SIGTERM` (Linux, via `/bin/kill`) both
   target a PID, so they work whether the game is the Node's child (Direct) or
   the shim's (Shim) — the shim isn't involved in the signal, it just watches
   the exit. The shim-stop branch in `StopInstanceAsync` is graceful-first:
   signal the game → wait `ExitedTask`/timeout → escalate to the shim's
   `Stop("kill")` → `Shutdown`. The shim's `WatchExitAsync` now **drains its
   pumps to EOF (bounded 3 s) before sending `Exited`** so a clean shutdown's
   late-stage output (world save, "shutdown complete") reaches the Node instead
   of being dropped; a killed game with a stuck pipe still can't stall it. The
   `GSM.CtrlCSender` helper stays — it's the universal ctrl-c path for both
   modes; the parked plan's "shim owns the process group and fires the event
   itself" wasn't needed. **Ctrl+C re-enabled on the Node:** its console-ctrl
   handler previously swallowed *all* `CTRL_C_EVENT` (to stop `CtrlCSender`'s
   broadcast bouncing back and killing the Node); it now swallows only while a
   new re-entrant `ConsoleCtrlSuppression` flag is active (set around the
   `CtrlCSender` call, + a 150 ms grace for a late bounce), so a user-typed
   Ctrl+C falls through to ASP.NET's `ConsoleLifetime` → `StopApplication()` →
   a graceful Node shutdown that also fires `ApplicationStopping` →
   `DetachShimsForShutdown`. Ctrl+C therefore detaches the shims (games
   survive) *and* closes the Node, and the next start re-adopts. *As-built:*
   the four stop helpers (`SendCtrlCToProcess` / `TrySendConsoleCtrlC` /
   `TrySendTaskkill` / `SendSigTermToProcess`) were refactored to take a `pid`
   rather than a `Process`. *Proven:* LO spawned under a shim → Ctrl+C detaches
   + closes the Node (game live) → restart re-adopts same game pid → graceful
   stop — the whole loop. Strategy-A graceful on Linux (SIGTERM + drain) is
   coded but rides with slice 1c for live test.

**8-2**

6. **Binary push + stage.** `POST /api/system/staged-binary` (chunked +
   SHA-256) + Manager upload. *Test:* stage a Node build to a remote node.
7. **prepare-restart + self-shutdown + relaunch + health-check + rollback.**
   The apply-script per platform; `/api/version` health gate; `.bak`
   restore. *Test:* apply an update to a node with a live shim-backed
   instance; players stay connected across the bounce; a forced bad binary
   rolls back.
8. **Per-node detect + UI.** Version detection + the Node-update dialog.
   *Test:* a node showing an available update applies end-to-end from the
   tree.

---

## Deferred (not this phase)

- **`execve()` in-place shim self-swap** (Linux-only; replace a *running*
  shim's image without restarting the game). Clean on Unix, no Windows
  equivalent; skipped to keep the shim trivial. Revisit only if a forced
  shim update ever becomes unavoidable (decision 5 is designed so it
  shouldn't).
- **FD-passing shim handoff** — rejected, see decision 6.
- **Direct-mode removal.** Kept as a permanent thin fallback (decision 9);
  no deadline to delete it.
- **Per-installation Node-update policies / auto-apply.** 8-2 ships
  detect + manual apply, mirroring 5l's no-auto-update stance.

---

## Watch-outs

- **Two PIDs per instance now (decision 11).** Reported `Pid` + process
  metrics stay on the *game* — keep a by-PID metrics handle, don't let
  `WorkingSet64` collapse to the shim's few MB — while `ShimPid` /
  `SupervisorPid` is the supervisor. `StartTime` identity checks, where still
  used (Direct mode), stay on the game PID.
- **`StopIntentPending` semantics carry over.** `TryAdoptOne` honours a
  pending stop by discarding; the shim path must preserve that — a `Detach`
  before a clean Node shutdown is *not* a stop, and must not be recorded as
  intent-to-stop.
- **Don't bind game life to shim life.** No job-object/`PDEATHSIG` coupling —
  it would kill the game on shim crash and defeat survivability (see
  Architecture → shim crash).
- **Protocol versioning is load-bearing, not optional.** The whole
  lazy-shim-update story (decisions 5/6/10) collapses if the first protocol
  isn't append-only. Land the version handshake in slice 1 and never remove a
  field.
- **`ResolveStrategy` stays the source of truth for the spec flags.** The
  shim doesn't re-derive strategy; the Node resolves it (as today) and sends
  it. Keeps the one decision point on the Node.
- **Adoption is still synchronous before `app.Run()`.** Shim reconnects add
  per-instance handshake latency; keep it bounded (realistic scale 1–10
  instances/node) and time-box a dead-endpoint reconnect so one missing shim
  doesn't stall startup.
- **Read before edit.** `SpawnGameProcess`, `TryAdoptOne`, `FinalizeStart`,
  and the `_database` snapshot signatures have been read for this plan but
  re-read at edit time — `ProcessManager` is 2688 lines and dense.

---

## References

- `GSM.Node\ProcessManager.vb` — `SpawnStrategy`, `SpawnGameProcess`,
  `ResolveStrategy`, `AdoptSnapshots` / `TryAdoptOne`, `FinalizeStart`,
  `ManagedInstance`, the native interop (`STARTUPINFOW`,
  `PROCESS_INFORMATION`, `SpawnHiddenConsoleProcess`,
  `SpawnWrappedConsoleProcess`), the stop family (`SendCtrlCToProcess`,
  `TrySendConsoleCtrlC`, `TrySendTaskkill`, `SendSigTermToProcess`,
  `WrapInSetsidIfLinux`).
- `GSM.Node\Endpoints\NodeEndpoints.vb` — `SystemEndpoints` (`/api/version`,
  `/api/status`, `/api/auth`, `/api/system/prerequisites`); the natural home
  for `/api/system/staged-binary`, `/api/system/prepare-restart`,
  `/api/system/shutdown`.
- `GSM.CtrlCSender` — existing native Ctrl+C send helper; source for the
  shim's graceful-stop code.
- `GSM.Watchdog` — co-location/publish target precedent and the
  self-contained sibling-exe pattern the shim follows; also the relaunch
  precedent for 8-2.
- `Phase5l_Plan.md` — the detect/stage/apply + `apply.cmd` + rollback +
  health-check shapes 8-2 mirrors Node-side.
- `Backlog.md` → *Hot-swap node binary without log-stream interruption* — the
  full history, the May 2026 re-adoption work, and the design questions
  (binary push format, service control, cross-host, tailer checkpointing)
  this plan resolves.
- `ROADMAP.md` → Phase 8 entry, and *Won't do* → the overturned hot-swap
  re-attach rejection (now 8-1).
