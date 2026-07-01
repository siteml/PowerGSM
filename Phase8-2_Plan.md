# Phase 8-2 — Node self-update `[in progress]`

> Seed doc for the 8-2 sub-phase. Expands the 8-2 summary in
> `Phase8_Plan.md` into locked decisions + open sub-questions + slices, so a
> fresh chat can pick it up. Mirrors the Phase 5l Manager self-update,
> Node-flavoured.

## Goal

Make shipping a new Node binary push-button, and make the update itself a
non-event for running game instances. The Manager detects a newer release,
verifies it, and pushes it to each node; the node stages it, steps aside, and
an external survivor swaps the binary and relaunches while the node is down.
Running instances ride straight through because 8-1 already keeps the games
alive across a node bounce.

**Release gate: ships before 0.4.0 is tagged** (same gate as 8-1).

---

## Status

`[in progress]` — designed/confirmed with Site 2026-06-24. **8-1 is complete and
Linux-proven** (shim survives `systemctl stop` via `KillMode=process`,
graceful SIGINT stop, re-adopt on restart all verified on the node), which is
the precondition 8-2 was waiting on. Decisions D1–D5 below are confirmed; the
open sub-questions under each are the remaining design calls to make before
(or during) the relevant slice.

**Slice 6 complete (2026-06-26).** Node staging endpoint + graceful update-exit,
the systemd `ExecStartPre` swap, and the universal-fallback NodeSetup survivor
are implemented and built clean. Both Linux survivor paths — systemd (exit 10 →
`Restart=on-failure` → `ExecStartPre` swap) and bare-exe (clean exit → detached
NodeSetup → swap → direct relaunch) — verified end to end on the node. Windows
verification (service + bare) then surfaced a latent **Phase 8-1** bug: a
deliberate shim detach wasn't suppressing the node's own exit handling, so the
update-exit tree-killed and crash-restarted the supervised game. Fixed in
`ShimSession` (a `_detaching` flag + cleared `_ownsShim` on detach; `SignalExit`
skips the exit cascade when detaching). **All four survivor paths — Linux
(systemd + bare) and Windows (service + bare) — now verified, game PID unchanged
across the bounce.** Slice 7a (Manager push + Update Nodes UI), **7-source (feed
sourcing + Latest column)**, **7b/7c (shim + NodeSetup co-update)**, and slice 8
(health-gate + rollback) are all built clean; the only remaining 8-2 work is the
runtime verification of slice 8's auto-rollback on a live node.

---

## How it builds on 8-1 (the keystone realisation)

**A running process cannot replace the binary it is currently executing.**
Windows locks the live `.exe` outright; on Linux you can unlink-and-replace
but the process keeps running the old inode. So the node *never* swaps its own
live files. It **stages** an update next to the live binary and **exits**; a
process that *outlives* the node does the swap in the gap where the file is
free, then relaunches.

This is the same "step aside and let a survivor finish it" shape 8-1 is built
on — the shims are *other* survivors that carry the games across the same gap:

- The node's update-exit goes through the **existing `DetachShimsForShutdown`
  path** (graceful detach, not a hard teardown), exactly like a normal
  shutdown.
- On **Linux**, `KillMode=process` (shipped in 8-1) means the shims + games
  survive the node's exit and get re-adopted by the relaunched node.
- On **Windows**, the shim detach / re-adopt path (proven in 8-1) does the
  same.

So running instances survive the self-update bounce on both platforms with no
new instance-side machinery. 8-2 is purely about moving + swapping the node
binary safely.

---

## The update sequence

The shape is identical on both platforms; only the *survivor* differs.

| step | Windows | Linux |
|---|---|---|
| stage `GSM.Node.new` (atomic rename from temp) | node | node |
| detach shims, node exits | yes (graceful) | yes (graceful, non-zero) |
| survivor that outlives the node | detached `NodeSetup --apply-update` | systemd |
| swap `.new` → live (only if `.new` present) | NodeSetup, after node PID dies | `ExecStartPre` swap step |
| relaunch | NodeSetup `sc start GSMNode` | systemd `Restart=on-failure` |
| re-adopt running instances | new node (8-1 path) | new node (8-1 path) |

**The node's whole job is: receive verified bytes → atomic-rename to
`GSM.Node.new` → detach shims → exit.** It does not version-check, does not
branch on "am I updating", does not relaunch anything. Intelligence lives in
the Manager (decide + verify); the swap lives in the survivor.

---

## Decisions

### D1 — Node stages, an external survivor swaps + relaunches `[confirmed]`

**Decision.** The node only ever stages `GSM.Node.new` (atomic rename from a
fully-downloaded temp file) and exits via the graceful detach path. The swap
and relaunch are done by a process that outlives the node:

- **Windows:** the node spawns `NodeSetup --apply-update --wait-pid <self>`
  **detached** and exits. NodeSetup waits for the node PID to die / the
  service to reach `STOPPED`, swaps `.new` over the live file (now unlocked),
  `sc start GSMNode`, exits.
- **Linux:** the node stages and exits non-zero; systemd's `Restart=on-failure`
  relaunches it, and an idempotent `ExecStartPre` swap step (added to the unit
  by `BuildSystemdUnit`) moves `.new` into place *before* each `ExecStart`.
- **Bare exe (no service, either OS):** the node spawns the detached
  `NodeSetup --apply-update --wait-pid <self>` survivor and exits *clean*,
  exactly like the Windows-service case — NodeSetup is the **universal fallback
  survivor**. Only a node actually running *under systemd* defers to the systemd
  path above; a Linux node started as a plain foreground exe would otherwise
  have no one to swap + relaunch it, so it routes through NodeSetup too. The
  swap/wait/relaunch logic in the survivor is therefore cross-platform; only the
  relaunch leaf is OS-conditional (`sc start` or direct-exe on Windows; always
  direct-exe on Linux-bare, since Linux-under-systemd never reaches NodeSetup).

**Survivor selection + exit code (refined, slice 6; as implemented).** The node
picks its survivor at exit time from `SystemdHelpers.IsSystemdService()`: under
systemd it defers to the systemd survivor, and *everything else* (Windows
service, Windows bare, Linux bare) routes to the detached NodeSetup survivor —
so `IsWindowsService()` isn't needed (Windows takes the NodeSetup path either
way, with `sc start`-or-direct decided inside NodeSetup). The exit code
collapses to one predicate: **exit non-zero iff relying on systemd's
`Restart=on-failure`; otherwise exit 0**, because NodeSetup owns the relaunch
and a Windows-service non-zero exit would race SCM recovery. The update-exit
always goes through `lifetime.StopApplication()` → `ApplicationStopping` →
`DetachShimsForShutdown` (D2), never a hard `Environment.Exit` before the
detach.

**Rationale.** Reuses NodeSetup, which already owns service install + runs
privileged on Windows. On Linux the node runs as the unprivileged `powergsm`
user and **cannot** relaunch a privileged helper or `systemctl restart`
itself — so we don't make it. The swap targets only **`powergsm`-owned files**
in the node's own tree (the deploy already owns them — that's what makes the
8-1 shim `+x` work), and systemd does the relaunch. Different mechanism per
OS, same shape; the asymmetry mirrors how the *stop signal* ended up
OS-specific.

**The swap step is unconditional + idempotent — run every start, swap only
when `.new` is present.** No "apply-update mode" the node has to enter, no flag
file to desync. The *presence of `GSM.Node.new` is the entire state*. A staged
update that didn't get applied (crash mid-update, reboot, power loss) is
applied on the next start, whenever that is. `mv`/rename within one filesystem
is atomic, so the file is only ever old or new, never torn.

**Open sub-questions (mostly settled in slice-6 planning).**
- Windows detached launch — **settled:** native `CreateProcessW` with
  `CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP`,
  fallback to `Process.Start(UseShellExecute:=False)` if breakaway is refused
  (no job present), so the SCM/job can't reap NodeSetup with the service.
- Survivor wait — **settled:** wait on the node **PID**
  (`GetProcessById(pid).WaitForExit`, "already gone" = success) as the
  authoritative file-unlock signal for both console- and service-mode nodes,
  then retry the swap on a sharing violation (handle-linger/AV). SCM-`STOPPED`
  poll noted as the alternative.
- Linux `ExecStartPre`: confirm the deploy tree is `powergsm`-owned end to end
  (verified in 6d); swap command
  `sh -c 'if [ -f .new ]; then mv -f live .old; mv -f .new live; chmod +x live; fi'`
  (absolute, shell-quoted paths).
- Staging/keep locations + names — **settled:** `GSM.Node.new` / `GSM.Node.old`
  / `GSM.Node.<uploadId>.part` beside `GSM.Node` in `AppContext.BaseDirectory`
  (see D4 for `.old`).
- Applying slice 6 to an *existing* systemd node requires regenerating +
  reinstalling the unit once so the `ExecStartPre` line is present (NodeSetup's
  existing write-unit / install flow).

### D2 — The down/back ride 8-1's detach + re-adopt `[confirmed]`

**Decision.** The update-exit calls the **same `DetachShimsForShutdown`** the
normal graceful shutdown uses; the relaunched node re-adopts via the existing
8-1 reconnect path. No new instance-side code.

**Rationale.** Already proven on both platforms in 8-1. The only requirement
8-2 adds is that the update-exit must not be a hard teardown — it must go
through the graceful detach so the shims keep the games and the new node can
re-adopt.

**Open sub-questions — resolved in slice-6 planning.**
- The update-exit path is literally the graceful shutdown path:
  `lifetime.StopApplication()` fires `ApplicationStopping` →
  `DetachShimsForShutdown`, not a separate `Environment.Exit`.
- The Linux non-zero exit is applied *after* `app.Run()` returns (so the detach
  has completed) and only when relying on systemd's `Restart` — see D1's
  exit-code predicate. A small distinctive code, a clean exit not a signal, so
  journald doesn't read it as a crash.

### D3 — Manager owns detect + verify; node trusts only vetted bytes `[confirmed]`

**Decision.** Version detection and verification live in the Manager ("Manager
interprets"). The Manager polls the release feed (GitHub Releases from the
existing Actions pipeline), compares against each node's reported version
(`/api/version` already exists), downloads the new binary, **verifies it**,
then pushes the *verified* bytes to the node's staging endpoint (chunked, with
SHA-256, per the parent doc). The node applies only what the Manager already
vetted — it never fetches and trusts a blob itself.

**Rationale.** Keeps the trust boundary clean: the privileged-ish "replace the
node binary" action only ever consumes bytes the Manager has verified. Fits
the existing pattern where the Manager already verifies everything else.

**Open sub-questions.**
- Verification strength: SHA-256 against a checksum the release publishes
  (minimum) — is that enough, or do we want a signature? Where is the checksum
  published (release asset / manifest file)?
- Transport: Manager **pushes** verified bytes to a node endpoint (chunked,
  resumable?) vs Manager hands the node a URL + checksum to pull. Leaning
  **push** to preserve the trust boundary — confirm.
- Where the Manager reads "latest available version" (release tag / manifest)
  and how it maps that to a per-node arch (win-x64 vs linux-x64).
- Manual / opt-in vs automatic: does the operator click "apply" per node, or
  does the Manager roll it out? (Likely operator-gated to start.)

### D4 — Rollback: keep N-1, health-gated auto-revert `[confirmed]`

**Decision.** Keep the previous binary staged as `GSM.Node.old`. After the
swap + relaunch, the **external survivor** confirms the new node reached a
health endpoint (`GET /api/version`) within a timeout; if it doesn't, the
survivor swaps `GSM.Node.old` back and relaunches the known-good binary.

**Rationale.** The hard case is "the new node won't even start" — it can't roll
itself back if it can't run. That's exactly why the external agent (NodeSetup
on Windows, systemd + a check on Linux) owns the revert: it's the thing that's
still alive when the new node fails. systemd `StartLimitIntervalSec` /
`StartLimitBurst` is a natural backstop on Linux.

**Open sub-questions — resolved in slice 8.**
- Health definition + timeout: **"answers `/api/version`," not "returns the new
  version."** NodeSetup doesn't know the expected new version string, and the
  Manager already confirms the version separately via its post-push poll (7a),
  so the survivor's bar is simply "did the node come back." NodeSetup polls for
  60 s (2 s grace / 2 s interval); systemd uses `Type=notify` READY within
  `TimeoutStartSec`.
- Who counts attempts: **systemd on Linux** (`Type=notify` turns "started but
  unhealthy" into a failed start; `Restart=on-failure` + `StartLimit*` bound the
  loop; an `ExecStartPre` marker-check does the actual revert) vs **a NodeSetup
  poll + revert on every other path**. The "process up, endpoint dead" case is
  exactly what `Type=notify` catches.
- Revert relaunch + no re-apply loop: the forward swap **consumes `.new` by
  rename** (both survivors), so a failed binary ends up as `live`, not a
  lingering `.new` — there's nothing to re-apply. NodeSetup reverts once and
  exits; the systemd marker is cleared on revert so the next start is a no-op.
  The bad binary is quarantined as `.failed` (not deleted) for forensics.

### D5 — Shim co-update rides the node publish `[confirmed]`

**Decision.** The node publish already bundles `GSM.Shim/<version>/`, so a node
update *carries its matching shim*. Staging a node update also drops the new
shim version folder (chmod `+x` via the `EnsureShimsExecutable` helper added in
8-1). The versioned side-by-side shim layout means the new node picks the
highest shim version; older shim folders stay for already-running adopted
instances until those instances restart.

**Rationale.** No separate shim OTA needed — the shim is a sub-artifact of the
node. The side-by-side versioning (already built) is what makes a mixed state
(new node + old shims still supervising live games) safe.

**Open sub-questions.**
- Pruning: when (if ever) do we delete old `GSM.Shim/<ver>/` folders? Safe only
  once no live instance is still adopted against that version — probably a
  lazy sweep on start that skips versions with live adoptions.
- Does the staging endpoint receive the shim folder as part of the node
  payload, or is the shim swapped by the same `.new`-style mechanism?

---

## Slices (continues 8-1's numbering)

### Slice 6 — Node staging + the external swap survivors `[done]`
- Node: a Manager-driven staging endpoint that receives verified bytes
  (chunked), writes to temp, atomic-renames to `GSM.Node.new`, then an
  update-exit that detaches shims (graceful) and exits.
- Linux: `BuildSystemdUnit` gains the idempotent `ExecStartPre` swap step
  (`.new` → live, keep `.old`); confirm `powergsm` ownership.
- Windows: `NodeSetup --apply-update --wait-pid` detached relauncher (wait for
  PID death → swap → `sc start` → exit).
- Verify with a **dry self-update**: stage a byte-identical-but-renamed
  "newer" binary, confirm swap + relaunch + re-adopt of a live instance, on
  both platforms.
- **Done (2026-06-26).** Implemented in `GSM.Node\SelfUpdate.vb` (staging +
  survivor routing + exit), `SystemEndpoints` (staged-binary + apply-update),
  `NodeProgram` (DI + exit code — the flag is read from a reference captured
  *before* `app.Run()`, since the host disposes its DI container on return and
  resolving the service afterwards throws and silently drops it back to exit 0),
  `ServiceManager.BuildSystemdUnit` (`ExecStartPre` swap) + `StartWindowsService`,
  and `GSM.NodeSetup\SelfUpdateApply.vb` (the universal survivor). Verified on
  the node via the `--self-update-dry-run` harness
  (`GSM.Node\SelfUpdateDryRun.vb`) on **all four survivor paths** (Linux systemd
  + bare, Windows service + bare): swap + relaunch + re-adopt with the game PID
  unchanged across the bounce. Windows verification surfaced a latent **Phase
  8-1** bug — the node tree-killed and crash-restarted its own supervised game
  on the update-exit because a deliberate shim detach didn't suppress the
  node-side exit handler — fixed in `ShimSession` (`_detaching` flag + cleared
  `_ownsShim` on detach; `SignalExit` skips the `onExited` cascade when
  detaching).

### Slice 7 — Manager detect + verify + push

Split into **7a (push plumbing + permanent UI)**, **7-source (feed sourcing
+ Latest column)**, and **7b/7c (shim + NodeSetup co-update)** — all done.

#### Slice 7a — push transport + Update Nodes UI `[done — verified]`
- `NodeHttpClient.StageBinaryAsync` (begin → chunk* → commit, SHA-256 + size,
  8 MB chunks with 409-offset resync, one-shot infinite-timeout client) +
  `ApplyUpdateAsync` (POST apply-update → survivor / 409). **Concrete-only on
  `NodeHttpClient`, not on `INodeClient`** (the `TryGetCachedVersion`
  precedent) so there's no Contracts rebuild; promote if a second caller
  appears. The factory hands back `INodeClient`, so the drive-point holds/casts
  to `NodeHttpClient` (same `TryCast` pattern the node-icon resolver already
  uses).
- Permanent UI: **Nodes → "Update Nodes…"** (`NodeUpdatesForm`, modelled on
  `PluginUpdatesForm`) — per-node checkbox list (build + platform +
  reachability, concurrent ~8 s-bounded probes), multi-select, sequential push
  with a per-node Result and a ≤60 s relaunch poll; one node's failure never
  blocks the rest.
- **OS-match guard** — the picked file's format is sniffed from magic bytes
  (`0x7F ELF` → Linux, `MZ` → Windows), must match each node's reported OS; a
  **mixed selection pops one selector per platform** and routes each node to
  its matching binary.
- **Done (2026-06-28).** Verified end-to-end against the live Linux node:
  stage → apply → survivor swap → relaunch → shim re-adopt, game PID unchanged.
  Sourcing deliberately decoupled (caller supplies a local file).

**Design decisions locked in 7a:**
- **Per-node, independent operator actions.** Node / shim / NodeSetup updates
  are decoupled from the Manager's *own* self-update (Help → Check for updates)
  **and from each other** — unreachable / mid-session / not-yet nodes never
  block the batch. This is why the trigger is a permanent fleet view, not a
  coupled "push everything."
- **Manual file-push is a first-class permanent path**, not throwaway
  scaffolding: the operator may push a release build *or* their own build and
  owns the versioning + consequences, backstopped by the node's commit-time
  SHA-256 + size check and the survivor relaunch. The release-feed mode is
  additive (a future "Latest → one click" alongside the file picker).
- **Target selector present from day one** (Node live; Shim / NodeSetup inert
  placeholders that snap back) so the per-target separation is visible before
  7b/7c wire them.

#### Slice 7-source — feed sourcing + Latest column `[done]`
- **7-source-a — Latest column.** `NodeUpdatesForm` resolves the newest release
  once per load (the background `GitHubReleaseChecker`'s persisted result, or one
  bounded live check) and compares each reachable node's build against it
  (`SemanticVersion`) in a new **Latest** column — `X (update)` + tinted row when
  behind, `current` when not; status line gains `· latest release X`.
- **7-source-b — one-click feed sourcing.** A **Latest release** checkbox swaps
  the per-platform file picker for a download. New `NodeReleaseSource`
  (`GSM.Manager.Core`) takes a platform + release tag → finds
  `PowerGSM-Node-{ver}-{rid}.zip` → downloads → **SHA-256 verifies against the
  release `SHA256SUMS`** → extracts the inner `GSM.Node[.exe]` → returns the
  local path, which feeds the *same* `StageBinaryAsync` → apply → relaunch loop
  the manual push uses. One download per platform, cached + shared across
  same-platform nodes in a batch (`<install>\.node-updates\<ver>\<rid>\`);
  unknown-platform nodes are skipped. The shared asset helpers
  (fetch / find / parse-sums / SHA / download) were lifted out of
  `UpdateOrchestrator` into `ReleaseAssetHelpers` (`ReleaseAssets.vb`) so node
  sourcing + Manager self-update share one verified-download path.
- **Done (2026-06-29).** Built clean; Manager-only, `GSM.Contracts` untouched.
  (Mirrors `UpdateOrchestrator`'s GitHub asset fetch + SHA256SUMS.) The node zip
  carries `GSM.Shim/` + `GSM.NodeSetup`, so 7b/7c source from the same download.

#### Slice 7b / 7c — co-update `[done]`
Node introduces a **target shape** (`SelfUpdateService.ResolveTarget` → `node` /
`nodesetup` / `shim`); `apply-update` dispatches on it and only bounces the host
for the node target (`ApplyResult.RequiresExit`).
- **7b — shim co-update (VersionedInstall).** The shim is side-by-side
  versioned, so "update" = install the verified bytes straight into
  `GSM.Shim\<version>\GSM.Shim[.exe]` at **commit** — no `.new`/`.old`, no
  survivor, no exit; running instances keep their old shim until restart.
  (Answers D5: the shim rides the same chunked transport as its own target,
  written into a version folder — *not* a single-file swap.) The place is
  **lock-safe then fail-clean**: a new version folder is conflict-free; a
  same-version re-push (corrupted-shim replace) frees the destination
  (delete-if-idle, else rename `*.superseded-*`) or, if the OS pins a running
  shim, **fails the commit with 409** (restart those instances or push a higher
  version) leaving the verified `.part` for the sweep — never half-applied. Shim
  version is path-sanitized; a shim push with no usable version is refused (400).
- **7c — NodeSetup co-update (SwapInPlace).** NodeSetup is idle on disk except
  during a node apply, so the node swaps its `.new` over the live binary
  in-process (keeping `.old`) with **no bounce** and **no auto-revert** — a bad
  NodeSetup only surfaces on the next node apply; `.old` kept for manual restore.
- **Manager.** `NodeUpdatesForm`'s Target selector now drives the push (node →
  stage/apply/relaunch-poll; shim/nodesetup → stage/apply/"Installed"); shim
  version stripped at `+`. `NodeReleaseSource.SourceAsync` gained a `target` arg
  and locates the per-target binary in the **same** node zip (node/nodesetup at
  root, shim from `GSM.Shim\<ver>\`), reusing one download+extract per
  (version, rid). Confirm prompts describe the actual per-target effect.
- Trust boundary preserved: the node applies only Manager-pushed, verified bytes.
- **Done (2026-06-30).** Node + Manager built clean; `GSM.Contracts` untouched.

### Slice 8 — Health-gate + rollback `[built — runtime verify pending]`

Built in three layers (2026-06-29; all build-clean); all node-side,
`GSM.Contracts` untouched. Answers D4's open sub-questions above.

#### 8a — commit-time OS-match guard `[built]`
`GSM.Node\SelfUpdate.vb` `CommitAsync`: after the SHA-256 verify and before
promoting `.part` → `.new`, sniff the staged bytes' magic bytes
(`DetectStagedFormat`: `0x7F ELF` / `MZ` PE) and reject (422, delete the
`.part`) a binary that's a recognized executable for the *wrong* OS. Only a
definite mismatch is blocked; an unrecognized format passes to the health gate.
Defense-in-depth behind the Manager-side picker guard — a direct-API or buggy
caller can't get a wrong-platform binary as far as the swap.

#### 8b-1 — NodeSetup survivor health-gate + revert `[built]`
`GSM.NodeSetup\SelfUpdateApply.vb` (Windows-service / Windows-bare / Linux-bare
paths). After the swap + relaunch, poll `http://127.0.0.1:<port>/api/version`
(port from `nodesettings.json`; `/api/version` is unauthenticated and the node
binds `ListenAnyIP`, so loopback reaches it) — 2 s grace, every 2 s, up to 60 s.
On no-answer, **revert**: stop the bad node (`sc stop`, or `Kill()` just the
node process for a direct launch — children/games survive), quarantine the bad
binary as `.failed`, restore `.old` → live, relaunch, re-confirm. Exit codes
gained 5 (rolled back) / 6 (rollback failed). `ServiceManager.StopWindowsService`
added (only `Start` existed). The systemd node never reaches NodeSetup, so this
is inert there — 8b-2 covers it.

#### 8b-2 — systemd survivor health-gate + revert `[built]`
`ServiceManager.BuildSystemdUnit` + `GSM.Node\NodeProgram.vb`. The unit becomes
`Type=notify` (the node already sends `READY=1` via `UseSystemd()`, so a binary
that starts-but-never-readies counts as a *failed* start — the "hung but alive"
case `Type=simple` can't see), gains `StartLimitIntervalSec=200` /
`StartLimitBurst=5` in `[Unit]`, and its `ExecStartPre` becomes apply-**or**-
revert: applying a `.new` drops a `.update-pending` marker; the node deletes it
once it's been healthy 15 s (`ScheduleUpdateMarkerClear` on `ApplicationStarted`
→ named async delay, no inline-lambda trap); a marker that outlives a start
(with a `.old` present) reverts — `.failed` quarantine, `.old` → live, clear
marker. Survives crash / reboot / power-loss mid-update. **Deployment:** the
marker-write/revert lives in the unit, so an existing systemd node only gets
this once its unit is regenerated (re-run NodeSetup install / rewrite
`gsmnode.service` + `daemon-reload`); the marker-clear ships with the next node
binary — both halves needed.

**Runtime verify (todo):** push a deliberately-broken-but-correct-platform
binary, confirm auto-revert to N-1 with instances still adopted and healthy — on
a systemd node (8b-2) and a Windows/bare node (8b-1). The `Type=notify` switch
is the one behavior change to watch (the node must reach ready within systemd's
`TimeoutStartSec`; adoption of many instances runs before ready). Docs closeout
done (CHANGELOG + this plan); flip `Phase8_Plan.md` 8-2 status to done once the
slice-7 remainder lands.

---

## Non-goals / deferred

- **Delta / binary-diff updates** — full binary push only.
- **Auto-updating NodeSetup itself — *promoted to a slice-7 payload item***
  (was: manual / out-of-band). The chicken-and-egg the original non-goal feared
  (a process swapping its own running image) never actually arises: NodeSetup is
  **idle-on-disk** except during the brief apply-update window, so updating it is
  a plain overwrite of a file nobody is executing — not a self-swap. The Manager
  verifies the bytes and overwrites the idle `GSM.NodeSetup`; *then* the node's
  apply-update spawns the now-current NodeSetup to do the node swap. The real
  constraint is **ordering**: when a node release bumps the survivor protocol,
  the new NodeSetup must be in place *before* apply-update runs — Manager
  orchestration ("if required-NodeSetup-version > installed, push + overwrite
  NodeSetup first, then stage + apply the node"). Same "what's in the push"
  question as D5's shim co-update; both settled in slice 7.
- **Independent shim OTA** — the shim ships with the node publish (D5); no
  separate shim-only update channel.
- **Shift+click hard-kill UI affordance** — parked; unrelated to self-update.
