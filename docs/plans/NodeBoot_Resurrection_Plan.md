# Node-boot instance resurrection `[implemented 2026-08-09 — compile-verified in sandbox; live gates GR1–GR4 pending]`

> Companion to ConPTY_Plan.md's sign-off round (raised 2026-08-09).
> Gap: when the HOST restarts (or the node dies together with its
> games), instances that were running do not come back. Adoption only
> recovers still-alive processes; a dead PID / dead shim endpoint is
> discarded ("PID {Pid} not running — discarding") and the instance
> reads Stopped until an operator or the Manager's AutoStart
> (Manager-START-scoped, InstanceEntity.AutoStart) intervenes.

## Goal

An instance marked resurrect-on-boot that was RUNNING with no stop
intent when the node last wrote its snapshot comes back automatically
when the node starts and finds it dead — Manager offline included
(node autonomy principle). Crashloop protection applies: a
resurrected instance that dies rides the existing crash-restart
ladder, never a blind restart loop.

## Why node-side, not a Manager flag

- The snapshot ALREADY carries the full recovery payload
  (ExePath/Arguments/WorkingDirectory, LogFilePathsJson,
  ParseRulesJson, Strategy + flags, CrashPolicyJson, PollSourcesJson)
  and both adopt paths already rebuild a complete StartInfo from it —
  crash-restart of an adopted instance uses exactly that psi today.
  Resurrection is "that same rebuild, plus actually spawning".
- A Manager-side answer (poll → notice dead → start) needs the
  Manager up and breaks the standing rule that recovery works with
  everything else down. AutoStart stays what it is: Manager-start
  behaviour.

## Decisions (proposed)

### R1 — Opt-in flag, per instance, rides the wire additively

- `StartInstanceRequest.ResurrectOnNodeBoot As Boolean = False`
  (additive; REST ProtocolVersion untouched; absent deserialises
  False on old nodes/Managers).
- Node persists it as an additive `InstanceSnapshots` column
  (`ResurrectOnBoot`, existing PRAGMA table_info + ALTER pattern —
  node.db migrations are cheap; NOT an EF migration).
- Manager sourcing — DECIDED at sign-off: **(a) instance custom
  field** `"ResurrectOnNodeBoot"` (truthy check), read in
  StartInstanceAsync beside the GracefulTimeoutMs custom-field
  precedent. Zero EF migration. As built: new `GetBoolField` helper
  (GetIntField's sibling); truthy = true/1/yes/on. UI: the field is a
  checkbox ("Recover state on node restart") in
  CommonConfigFields.GetInstanceLifecycleFields — the SAME
  manager-owned schema list that renders GracefulTimeoutMs etc. on
  every game's Edit Instance form — so no bespoke form wiring and no
  EF column; the value rides ConfigJson like every other lifecycle
  knob.

### R2 — Resurrection pass runs AFTER adoption + sweep, non-blocking

Order at startup: `AdoptSnapshots()` (unchanged, synchronous) →
`SweepAdoptLiveShims()` (unchanged) → **`ResurrectDeadSnapshots()`**:

- Candidates: snapshot rows whose adopt FAILED dead (dead PID /
  dead shim endpoint) with `State = Running`, `Not StopIntentPending`,
  `ResurrectOnBoot = True`. Today those rows are discarded inside
  TryAdoptOne/TryAdoptShim; the discard sites instead hand the row to
  a pending-resurrection list when the flag is set (row still removed
  from InstanceSnapshots — the resurrection writes a fresh one on
  FinalizeStart). As built, THREE discard sites queue: PID-not-running
  and PID-recycled-by-a-different-process (TryAdoptOne — both mean
  the game is dead) and dead-shim-endpoint (TryAdoptShim).
  Deliberately NOT queued: start-time-unreadable (process exists —
  resurrecting would risk a double-start), no-recovery-payload, and
  the top-level adoption-threw catch (unknown state).
- Dedup guard: skip any id already in `_instances` (adopted or
  sweep-adopted — a live shim the snapshot pass missed must not be
  double-started).
- The pass itself runs as a background task AFTER `app.Run()` is
  reachable (do not stretch the synchronous startup window; adoption
  latency watch-out stands). Per instance: rebuild StartInfo from the
  row exactly as the adopt path does, then route through the SAME
  start path a crash-restart uses (shim launch via StartViaShimAsync /
  direct spawn, FinalizeStart, EventStore registration, pollers,
  tailers) so every downstream behaviour is the normal-start
  behaviour.
- Stream visibility: the normal path already emits ProcessStarted;
  prepend a `Custom` "boot-resurrect" event (SourceId "lifecycle",
  fields: DeadPid, SnapshotState) so History shows WHY it started.
  No notifications beyond what a normal start emits (backfill/
  double-notify rules untouched).

### R3 — Crashloop protection = the existing ladder, plus a boot-flap guard

- The rebuilt instance carries the snapshot's CrashPolicyJson, so a
  resurrected game that crashes follows the normal
  RestartWithBackoff/RestartLimited/CrashLoopHalted machinery
  (in-memory, as today).
- Boot-flap residual: crash counts reset with node restarts, so a
  host/node restart LOOP would re-resurrect each boot. Guard AS
  BUILT: new node.db table `BootResurrectAttempts (InstanceId PK,
  LastAttemptUtc, AttemptCount)` — attempts < 10 min apart chain;
  a gap ≥ 10 min resets the count. At 3 in-window attempts the pass
  suppresses with a Warning + Custom "boot-resurrect-suppressed"
  event. The row clears when the resurrected run (same PID) is
  still Running 10 minutes later. DIVERGENCE from the draft:
  "clears on manual start" was dropped — resurrection rides the
  same StartInstanceAsync as a manual start, so the path can't tell
  them apart without widening the wire; the healthy-clear covers
  recovery, and suppression never blocks a manual start anyway.

### R5 — "Auto start with node" (added at review, 2026-08-09) `[implemented]`

Second, UNCONDITIONAL flag — like a Windows service set to Automatic:
start the instance whenever the node starts, prior state irrelevant.
Raised when the "Recover state" label review made the conditional
semantics explicit and a start-even-if-stopped variant was wanted too.

- Wire: `StartInstanceRequest.AutoStartOnNodeBoot` (additive, no
  bumps). Custom field `AutoStartOnNodeBoot`; checkbox "Auto start
  with node" in CommonConfigFields under the Recover checkbox.
- Snapshot: additive `AutoStartOnBoot` column. KEY MECHANIC: stops no
  longer delete the snapshot for flagged instances — they PARK it
  (`MarkInstanceSnapshotStopped`: State=Stopped, Pid=0, shim fields
  cleared, payload kept). All 3 stop sites route through
  `ClearOrParkSnapshot` (StopInstanceAsync, intentional-exit,
  stop-during-backoff).
- Boot: TryAdoptOne gets a parked-row branch (State=Stopped → queue
  if flagged, row LEFT in place so failed/suppressed starts retry
  next boot; stale unflagged parked rows are discarded).
  QueueBootResurrection eligibility is now: no StopIntentPending +
  payload + (AutoStartOnBoot any-state OR ResurrectOnBoot
  State=Running). Dead-discard sites park flagged rows instead of
  removing. StopIntentPending-at-boot honors the stop THIS boot but
  parks so the NEXT boot autostarts (double-start safety: the game
  may still be alive mid-stop).
- Shares the BootResurrectAttempts flap guard; boot-resurrect event
  gains `Trigger`=autostart|recover.
- Piggyback fix: BuildResurrectionRequest now round-trips BOTH flags
  (was hardcoded ResurrectOnNodeBoot=True) and derives
  `NeedsPseudoConsole` from the persisted Strategy so a ConPTY
  instance resurrects under ConPTY instead of silently downgrading.
- ~~Known gap: the flag only travels on STARTS.~~ CLOSED by R6.

### R6 — Live flag updates + delete cleanup (review round 2, 2026-08-10) `[implemented]`

Closes R5's disarm gap and the delete-leftover hazard. Two additive
node endpoints (no version bumps; pre-endpoint nodes 404 and callers
fall back to the old behaviour):

- `POST /api/instances/{id}/boot-flags` (BootFlagsRequest — both
  flags): ProcessManager.UpdateBootFlags updates the in-memory
  ManagedInstance (if tracked) AND the snapshot row's flag columns
  (UpdateInstanceSnapshotBootFlags). Turning autostart OFF on a
  PARKED row deletes the row outright. Manager pushes on every Edit
  Instance save (fire-and-forget from the save handler →
  InstanceManager.PushBootFlagsAsync, which re-merges the config
  layers so the pushed values are exactly what a start would use).
  ~~Only residual: instance stopped AND node has no snapshot at all
  — nothing to park, arms from next start.~~ CLOSED same day — the
  first live test hit exactly this (every autostart tick landed on
  tracked=False/snapshot=False): new additive `POST
  /api/instances/park` (body = full StartInstanceRequest). When a
  boot-flags push reports autostart=True but snapshot=False, the
  Manager builds the SAME request a real start would (StartInstance-
  Async parkOnly:=True — exits before the spawn; first exe candidate,
  no probing) and the node persists it as a parked Stopped row
  (ProcessManager.ParkInstance: FinalizeStart-shaped serialization,
  ResolveStrategy for the strategy column, 409 while tracked
  running). Ticking autostart on a stopped instance now arms
  immediately — no start/stop cycle. Residual-residual: a config
  edit AFTER parking updates flags but not the parked payload until
  the next real start (same staleness class as any snapshot).
- `POST /api/instances/{id}/forget` (ForgetInstanceResponse):
  ProcessManager.ForgetInstance drops the snapshot row (parked or
  live) + BootResurrectAttempts row + untracks/unregisters
  (pollers, event store). REFUSES (409) while the instance is
  Running/Starting. Manager calls it in all three delete flows
  BEFORE the local rows go away (MainForm): instance delete (409 →
  "stop it first" and abort; node unreachable → explicit
  delete-anyway prompt warning about possible boot-start leftovers),
  installation delete and node delete (best-effort loop per
  instance, no extra dialogs).
- Client plumbing: INodeClient.UpdateBootFlagsAsync /
  ForgetInstanceAsync + NodeHttpClient impls (Conflict body
  surfaced as a value, not an exception). INodeClient gained
  members — same no-bump rationale as UpdateParseRulesAsync
  (Manager↔Node plumbing, not plugin-facing; bundled distribution).

### R4 — Known parity gap, accepted

Request `EnvironmentVars` are not persisted in snapshots, so a
resurrected instance (like an adopted-then-crash-restarted one TODAY)
loses plugin env overrides (Stardew GALLIUM_DRIVER). Same class as
the existing documented adopt gap — recorded, not fixed here. (Fix
would be an additive EnvJson snapshot column — note as follow-up.)

## Touch-points

- Contracts `NodeApiContract.vb`: StartInstanceRequest.ResurrectOnNodeBoot
  (additive; no version bumps).
- Manager `InstanceManager.StartInstanceAsync`: read custom field →
  set request flag (option a).
- Node `NodeProgram.vb`: ResurrectDeadSnapshots kickoff after sweep
  (background). (Does NOT touch the reloadOnChange block.)
- Node `ProcessManager.vb`: snapshot column + SaveInstanceSnapshot/
  Row/Load; TryAdoptOne/TryAdoptShim discard sites → pending list;
  ResurrectDeadSnapshots + boot-flap KV guard; StartInfo rebuild
  reuse.

## Slice + gates (single slice)

- Compile: Contracts + Node full vbc; Manager touched file partial
  (BC36943 hand-audit).
- **GR1**: instance with flag set, running → reboot the host (or kill
  node + game together) → node start resurrects it: fresh shim, new
  PID, Running in Manager, tailers/pollers/rules live, History shows
  boot-resurrect → ProcessStarted at true times.
- **GR2**: same but flag unset → today's behaviour (discard, Stopped).
- **GR3**: StopIntentPending honoured (stopped-during-downtime is NOT
  resurrected); live-shim adopt path unaffected (plain node service
  restart still adopts, zero resurrections fired).
- **GR4**: crash-looping game resurrected once → normal ladder →
  CrashLoopHalted; forced boot-flap (3 rapid node restarts with an
  instant-crash exe) → suppressed with event.
- **GR5 (Auto start with node)**: flag set + instance STOPPED
  normally → node service restart → instance starts (Trigger=
  autostart in the boot-resurrect event); also starts after host
  reboot from stopped; recover-only behaviour unchanged when only
  that flag is set.
- **GR6 (live flags + delete)**: with the instance stopped-but-
  parked, UNTICK "Auto start with node" → Save → node log "Boot
  flags for {id} ... parked row removed" → node restart → stays
  stopped (no start/stop cycle needed). Tick it while RUNNING →
  Save → node log shows the flag update → kill node+game → boot →
  starts. Delete a parked-autostart instance → node log "Forgot
  instance {id}" → node restart → nothing starts. Delete while
  running → blocked with "stop it first". Delete with node offline
  → warning prompt, delete-anyway works.

## Status / verification (2026-08-09)

IMPLEMENTED. Contracts + Shim.Protocol + Node: FULL vbc compile in
sandbox, 0 errors (ref packs from the .NET 8 SDK; dependency dlls
from bin\Release). Manager (InstanceManager.vb): reference-partial
compile — only missing-external-type errors, zero syntax errors; the
touched Manager code contains no Await (BC36943 n/a). VS build +
live gates GR1–GR4 are the operator's part — see the findings note
(claude/NodeBoot_Resurrection_Findings_2026-08-09) for test steps.
Node redeploy required; instances opt in via the "Recover state on
node restart" checkbox on Edit Instance (stored as
ResurrectOnNodeBoot in ConfigJson) and the flag takes effect at each
instance's NEXT start (that start's snapshot carries it).
