# Phase 8-3 — Shim rediscovery / `node.db` hardening `[implemented — builds clean; runtime test skipped this pass]`

> Hardening follow-on to 8-1 (per-instance shim supervisor) and 8-2 (node
> self-update), both of which lean on re-adoption. Goal: a lost or corrupt
> `node.db` must not turn running games into unmanageable orphans.

## Goal

Make the node's `InstanceSnapshots` table a **cache, not a source of truth** for
re-adoption. Previously the relaunched node re-adopted shim-supervised games by
reading each instance's saved `ShimEndpoint` (+ recovery payload) from `node.db`;
if that file was wiped or corrupt while games ran, those games kept running (the
shim holds them) but became orphans — the node reported them Stopped, crash
detection was dead, and a Start spawned a duplicate that failed on port-in-use.

Three halves, all implemented:
- **Rediscover live shims from the OS**, not from `node.db` — every shim sits at
  a deterministic, patterned address, so the node enumerates them (Slice A).
- **Recover where to tail** for file-tailed games without `node.db` — the shim
  carries the log paths it was handed at spawn and echoes them on adopt (Slice C).
- **Survive a corrupt `node.db`** at startup instead of crash-looping (Slice B).

Scope is "Tier 2" (rediscover + lean-adopt) **plus the log-path slice of Tier 3**
(shim echoes the log paths). The rest of Tier 3 — shim echoes the full
`SpawnSpec` so crash-restart works with zero `node.db` — stays deferred.

---

## Keystone insight

The shim endpoint is already a **pure function of the instance id**
(`ShimSession.MakeEndpoint`):

```
Windows → pipe:powergsm-shim-<sanitizedId>
Linux   → unix:<DataDirectory>/shims/<sanitizedId>.sock
```

So the stored `ShimEndpoint` is redundant — derivable. And because every live
shim listens at this well-known pattern, the node asks the OS "what shims are
running right now?" and adopts them with no `node.db` involved:

- **Linux:** `Directory.GetFiles(<DataDirectory>/shims, "*.sock")` (the dir is
  deterministic: `_shimSocketDir = Path.Combine(nodeConfig.DataDirectory, "shims")`).
- **Windows:** enumerate `\\.\pipe\` for `powergsm-shim-*`.

`SanitizeId` is **lossy** (non-alphanumerics → `-`), so a socket/pipe name can't
be reversed to the true instance id — the shim therefore reports its own id (and
its log paths) in the handshake. The wire protocol is explicitly append-only, so
every `HelloAckMessage` / `SpawnSpec` addition is non-breaking, no
`ProtocolVersion` bump.

---

## Decisions (all implemented)

### D1 — Shim reports its instance id in the handshake `[done]`

`HelloAckMessage.InstanceId`, populated from the shim's `--instance-id`. Lets a
namespace sweep (which only knows the lossy address) recover the true id straight
from the shim.

### D2 — Snapshot pass first (full payload), then sweep for gaps (lean) `[done]`

`AdoptSnapshots` runs first and unchanged: every instance it adopts from
`node.db` keeps the **full recovery payload** (ExePath/args/cwd for crash-restart,
parse rules, crash policy, log paths). Then `SweepAdoptLiveShims` enumerates the
shim namespace and lean-adopts only live shims whose id is **not already in
`_instances`** — filling the gaps the snapshot pass missed. The sweep always runs
(dedup by id makes it a cheap no-op on already-adopted ids) so it also catches a
shim the snapshot never knew about.

### D3 — Lean adopt: reconnect + track, near-full capability `[done]`

A swept instance with no snapshot is adopted from `(instanceId, endpoint, live
gamePid)` learned over the handshake. The lean adopt:
- **registers EventStore with an empty rule set** (`RegisterInstance(id,
  emptyRules, hydrateState:=True)`) — load-bearing: it makes the instance a valid
  rule-push target and rehydrates persisted match/tile state;
- **recovers the log paths from the shim** (Slice C / D7) and starts file tailers
  (`skipResume:=True`), so file-tailed games resume event tracking;
- sets `CrashPolicy = NeverRestart` and leaves `StartInfo = Nothing`.

What works after a lean adopt: status, graceful stop, stdout/exit relay, file
tailing (so player/chat/server-state tracking resumes go-forward), and — within
~3s, automatically — parse rules, via the Manager's existing stream-health
reconnect re-push (`EnsureLogStreamAsync` → `ReregisterParseRulesAsync` →
`UpdateParseRulesAsync`).

**Residual gap (by design):** the lean path has no `StartInfo`, so **crash-restart
can't rebuild a `SpawnSpec`** until the instance is fully restarted. A crash
during a `node.db`-less window leaves the game stopped (NeverRestart) rather than
auto-restarting. Closing this is the remaining Tier-3 work (shim echoes its full
`SpawnSpec`) — deferred.

*Minimal-snapshot sub-question — resolved: no.* A lean adopt does **not** write a
snapshot back. There's little value (it'd still be payload-less), and the sweep
simply re-discovers the instance on the next restart anyway, which is the whole
point. Keeps the lean path side-effect-free.

### D4 — Probe-then-adopt (reuse the existing adopt path) `[done]`

The sweep does a lightweight **probe** per endpoint first
(`ShimSession.ProbeEndpointAsync`: connect, handshake, read, close — time-boxed,
never throws), then for a live game whose id isn't already adopted, builds the
`ManagedInstance` and calls the existing `AdoptViaShimAsync(managed, endpoint)`
(a second connect). Two connects per shim is a one-time startup cost on a handful
of instances and avoids reordering `AdoptAsync`'s construct-with-known-id shape.
The probe connects + drops cleanly; the shim treats it as a brief node connection
and loops back to accept the real adopt, keeping its game.

### D5 — Stale-entry handling: conservative, no unlink `[done — revised]`

A probe that doesn't answer is treated as dead and skipped. We deliberately do
**not** unlink stale Linux `.sock` files during the sweep: a probe timeout can't
be safely distinguished from a slow-but-live shim, and unlinking a live shim's
socket would orphan it. The `UnixSocketListener` already clears a stale socket at
**bind time** when that instance next starts, so a dangling file is harmless and
self-heals on reuse. (Revised from the original "unlink dead sockets" — the
orphan risk isn't worth the tidy-up.)

### D6 — Corrupt `node.db` self-heals instead of crash-looping `[done]`

`EnsureCreated` now wraps the original body (`EnsureCreatedCore`) in a guard:
on `SqliteException` with `SqliteErrorCode` 11 (`SQLITE_CORRUPT`) or 26
(`SQLITE_NOTADB`) **only**, it clears the connection pool, renames the bad file
aside (`node.db.corrupt-<timestamp>`, recorded in `LastCorruptionBackup`), clears
`-wal`/`-shm`/`-journal` sidecars, and recreates empty. Any other SqliteException
(locked, busy, readonly, …) is **not** corruption and propagates unchanged.
`NodeProgram.Main` logs the reset at Warning once the logger exists. Combined with
the sweep: corrupt `node.db` → reset empty → sweep re-adopts the live shims from
the OS → **nothing orphaned**.

### D7 — Log-path recovery via the shim (Option B) `[done]`

The log path is `f(InstallPath, pluginPattern, InstanceId)` — e.g. LO's
`{InstallPath}/Mist/Saved/Logs/{InstanceId}.log`. The node has only the id; the
install path and the plugin-specific relative pattern both live on the Manager,
so the node **cannot** derive it alone. Rather than depend on the Manager being
up, the **shim carries it**: `SpawnSpec.LogFilePaths` (the resolved absolute
paths the node already computes at start) is handed to the shim at spawn; the
shim remembers it and echoes it in `HelloAckMessage.LogFilePaths` on every later
handshake. On a lean adopt the node reads `ShimSession.AdoptedLogFilePaths` and
starts tailers — `node.db`- **and** Manager-independent. This is the log-path
slice of Tier 3; the full-`SpawnSpec` echo (for crash-restart) remains deferred.

---

## Touch-points (as built)

**`GSM.Shim.Protocol\Protocol.vb`**
- `HelloAckMessage`: `InstanceId` + `LogFilePaths`.
- `SpawnSpec`: `LogFilePaths`.

**`GSM.Shim.Protocol\FrameConnection.vb`**
- `ShimHandshake.AcceptAsync`: gained `instanceId` + `logFilePaths` params; sets
  them on the ack.

**`GSM.Shim\Supervisor.vb`**
- `_logFilePaths` field, set from the Spawn's `SpawnSpec`; passed (with
  `_instanceId`) into `ShimHandshake.AcceptAsync` on every connection.

**`GSM.Node\ShimSession.vb`**
- `ProbeEndpointAsync` (Shared) + `ShimProbeResult`.
- `AdoptAsync` stashes `ack.LogFilePaths`; new `AdoptedLogFilePaths` property.

**`GSM.Node\ProcessManager.vb`**
- `SweepAdoptLiveShims`, `EnumerateShimEndpoints`, `TryLeanAdoptShim` (empty-rule
  register + hydrate + NeverRestart + reconnect + log-path recovery → tailers).
- `StartViaShimAsync` forwards `managed.LogFilePaths` into `SpawnSpec.LogFilePaths`.

**`GSM.Node\NodeProgram.vb`**
- `NodeDatabase`: `EnsureCreated` corruption guard + `BackupAndDeleteCorruptDb` +
  `EnsureCreatedCore` + `LastCorruptionBackup`.
- `Main`: `SweepAdoptLiveShims()` after `AdoptSnapshots()`; Warning log if
  `db.LastCorruptionBackup` is set.

**Rule re-push (verified, no change needed):** `EventStore.UpdateParseRules` does
**not** register-if-absent (drops the push for an unknown instance), which is why
the lean adopt pre-registers an empty rule set. The Manager's existing
stream-health reconnect already re-pushes the real rules within ~3s — no new
"force rules" endpoint or Manager change.

---

## Slices

### Slice A — Shim rediscovery sweep `[implemented]`
D1, D4, the sweep + enumeration + lean adopt (D2/D3), D5.

### Slice B — Corrupt-`node.db` self-heal `[implemented]`
D6.

### Slice C — Log-path recovery via the shim `[implemented]`
D7.

### Build / deploy note
`GSM.Shim.Protocol` is shared, so Node **and** Shim rebuild, and the **shim must
be redeployed**. Only a new-build shim reports `InstanceId` + echoes log paths; a
shim already running from an older build answers the handshake without them, so
the sweep logs "older shim" and skips it. That only affects the `node.db`-loss
case (the snapshot path still adopts old shims). To exercise the sweep, start the
instance under the new shim first.

### Verify
1. **Sweep (A) + log paths (C):** start a file-tailed instance (LO) under a new
   shim → stop the node → **delete `node.db`** → start the node. Expect
   `Shim sweep: lean-adopted instance …` + `recovered N log path(s) from the
   shim …`, same game PID, Running + stoppable, file tailing resumed, and the
   Manager restoring parse rules within ~3s. On both Linux and Windows.
2. **Happy path unaffected:** with `node.db` intact, the snapshot pass adopts as
   before (full payload, crash-restart intact) and the sweep no-ops on the
   already-adopted id (`{Skipped}`).
3. **Corrupt-db (B):** truncate/garble `node.db` → start the node → it logs the
   reset, preserves `node.db.corrupt-<ts>`, starts clean, and the sweep re-adopts
   the live shims.
4. `--shim-reconnect-test` still passes (it exercises the modified handshake).

### Caveat (shared with a normal adopt, not a regression)
Adopt tailers start with `skipResume:=True` (seek to end), so log-path recovery
restores **go-forward** tracking, not a historical rebuild of the current player
list — players already connected before the bounce repopulate as they next
chat/leave/rejoin. Identical to a normal node restart today.

---

## Non-goals / deferred

- **Tier 3 (remainder) — shim echoes the full `SpawnSpec`.** Would let the node
  rebuild `StartInfo` for crash-restart with zero `node.db`, closing the D3 gap.
  The log-path slice of this is done (D7); the `SpawnSpec`/crash-restart slice is
  deferred (more protocol surface + shim state).
- **Native pipe enumeration on Windows.** `Directory.GetFiles("\\.\pipe\")` is
  used with a defensive try/catch; a native `NtQueryDirectoryFile` would be more
  robust against odd pipe names if that ever proves flaky.
- **Manager re-pushing start metadata on reconnect** — an alternative to D7's
  shim-echo for log paths (and a possible route for the SpawnSpec). Not pursued;
  the shim-echo keeps recovery Manager-independent.
