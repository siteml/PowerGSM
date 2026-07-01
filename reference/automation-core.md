# PowerGSM Reference — Automation (Core)

Part of the PowerGSM reference set (index: [`../PowerGSM_Reference.md`](../PowerGSM_Reference.md)).
The engine half of the automation refactor: contracts + data layer,
polymorphic rule JSON, the RestartCoordinator + ready-signal waits,
CoordinatedRestartAction, the SortOrder / scope / filter model, the
version-mismatch trigger, and the NotifyAction transport. The forms,
rule editor, and tree-UI half is in [`automation-ui.md`](automation-ui.md).
This is one large chronological refactor (Phases 1–5); engine and UI work
interleaved in time but are split here by subsystem, so the phase
sub-headings below skip the numbers that landed in the UI file.

---

## AUTOMATION REFACTOR

Multi-phase rework of the rule engine to support per-instance
scheduled restarts with coordinated queueing. The core insight:
"manual restart" and "scheduled restart" are different beasts —
manual is fire-and-forget, scheduled needs to serialise across
siblings so one realm's restart completes before the next begins.

### Design decisions locked upfront

- **Schedule location: Hybrid** — Restart fields on InstanceEntity
  materialize an auto-generated AutomationRule; power users can edit
  the generated rule directly in Automation Rules form.
- **Ready-for-next signal: configurable trigger + timeout fallback** —
  Plugin declares via opt-in `IReadySignalProvider` interface (new
  interface rather than new IGamePlugin members, so existing plugins
  keep working unchanged). For Last Oasis: `TileLoaded` kind,
  300s default timeout.
- **Concurrency: installation-scoped default (1), node-wide override
  on NodeEntity** — Default `InstallationEntity.MaxConcurrentRestarts = 1`;
  `NodeEntity.MaxConcurrentRestarts = 0` means "no node-wide limit".
- **Stagger strategy: per-instance cron, coordinator queues in
  acquisition order** — if two instances fire at the same cron tick,
  the coordinator stages them sequentially.
- **Manual restart UX: Shift-click = force, plain click = coordinated**
  (Phase 5, not yet implemented). For now, manual restarts are
  uncoordinated; only automation-rule-driven restarts go through the
  coordinator.
- **Cron overlap policy: `SkipIfRunning` default** for auto-generated
  rules; power user can override via rule editor.
- **LO has NO RCON** — in-game chat warnings unavailable; Discord
  webhook warnings via `NotifyAction` + `Wait` chains only.

### Phase 1 — Contracts + data layer (no behavior change)

**GSM.Contracts\IGamePlugin.vb:**
- `ReadySignalKind` enum: `ServerStateEquals`, `TileLoaded`, `CustomMarker`
- `ReadySignal` class: `Kind + MatchValue`
- `IReadySignalProvider` interface (opt-in): `GetReadyForNextSignal()` +
  `DefaultReadyTimeoutSeconds` readonly property

**GSM.Contracts\IAutomationRule.vb:**
- `WaitForReadySignal(instanceId, timeoutSeconds)` method on `IRuleContext`
  + matching `MustOverride` stub on `RuleContext`
- `WaitForReadySignalAction` class between `WaitAction` and `SequenceAction`
- Execute body: delegates to `ctx.WaitForReadySignal`; returns
  `ActionResult.Ok` on both true and false so enclosing sequence
  still progresses (coordinator releases slot on timeout too)

**GSM.Manager\Core\AutomationEngine.vb:**
- Phase 1 stub on `RuleContextImpl.WaitForReadySignal` that throws
  `NotImplementedException` — just enough to compile. Phase 3a
  replaces this with the real implementation.

**GSM.Manager\Data\GsmDbContext.vb:**
- `NodeEntity.MaxConcurrentRestarts As Integer = 0`
- `InstallationEntity.MaxConcurrentRestarts As Integer = 1`
- `InstanceEntity.RestartEnabled As Boolean = False`
- `InstanceEntity.RestartCron As String` (HasMaxLength 100)
- `InstanceEntity.RestartRuleId As String` (HasMaxLength 100)

**Migration:** `AddRestartScheduling` — five AddColumn operations
on existing tables. No data moves, no table recreations.

### Phase 2 — Polymorphic JSON round-trip

**GSM.Manager\Core\AutomationRuleSerializer.vb** — module owning all
JSON serialisation for AutomationRule's polymorphic slots (Trigger,
Conditions, Action).

**Why hand-rolled:** `System.Text.Json`'s built-in `[JsonPolymorphic]`
only works on base classes, not interfaces. The contracts use
interfaces (`IAction`, `ICondition`, `ITrigger`). Reshaping every
contract into an abstract class would ripple for a Manager-side
serialisation concern.

**Why not JsonConverter(Of T):** The converter's Read override takes
`ByRef reader As Utf8JsonReader`. `Utf8JsonReader` is a ref struct
(contains `Span(Of Byte)`). VB.Net's compiler rejects ref struct
references with BC30668 "obsolete: Types with embedded references
are not supported". Hard stop.

**Actual approach:** `JsonNode` tree traversal. Parse into a
JsonNode (a regular class, VB-friendly), inspect `$type`
discriminator, look up concrete type in a dispatch table, let STJ
deserialise that node as that specific type.

- `SerializeAction` / `DeserializeAction` (and Trigger, Conditions
  variants) are the public API.
- `ConvertActionToNode` — emits `$type` + concrete properties. Handles
  `SequenceAction` specially: its `Steps` list of `IAction` wouldn't
  serialise correctly under a naive call (STJ would emit empty
  objects for IAction), so we recurse explicitly into each step.
- `ConvertNodeToAction` — mirror on read. Looks up concrete type,
  recurses for SequenceAction's Steps.

**Dispatch tables:** `TriggerTypes`, `ConditionTypes`, `ActionTypes` —
Dictionary(Of String, Type) mapping `$type` discriminator to
concrete type. To add a new rule type: implement the interface,
pick a discriminator string, add one line here. That's it.

**Legacy format fallback:** pre-Phase-2 triggers were stored as flat
dictionaries without a `$type` envelope. `DeserializeTriggerLegacy`
recognises the old shape; on next save the rule rewrites in the new
format, so this code only runs during the one-time transition.

**Engine wiring:** `AutomationEngine.DeserializeRule` now calls the
serializer. New `SerializeRuleToEntity(rule, existing?)` helper
function lets callers persist new rules. The old ad-hoc dictionary
parser was removed.

### Phase 3a — RestartCoordinator + ready-signal waits

**GSM.Manager\Core\RestartCoordinator.vb** — singleton registered in
DI. Two concerns:

1. **Slot allocation** — per-installation + per-node semaphores for
   concurrency control. `AcquireAsync(instanceId, cancellation)`
   returns a `RestartSlot`; `Release(slot)` drops both gates. Order:
   installation gate first, node gate second (prevents deadlock
   across two installations sharing a node).

2. **Ready-signal waits** — `WaitForReadySignalAsync(instanceId,
   timeoutSeconds)` blocks until the plugin's declared signal fires,
   the timeout elapses, or the instance reaches a terminal state.
   Uses `TaskCompletionSource` keyed by instanceId in a pending dict.
   `Task.WhenAny(signalTask, timeoutTask, terminalTask)` picks the
   winner.

**Why TCS instead of events:** Waits are rare and transient (one per
coordinated restart, maybe a few per day). A per-wait TCS is simpler
than plumbing a persistent event subscription that fires on every
log line.

**Terminal-state watchdog:** `WatchTerminalStateAsync(instanceId,
signalTask)` polls `InstanceManager.GetLiveState` every 1s until
the signal completes or the instance hits
Stopped/Crashed/CrashLoopHalted. On terminal state, completes the
TCS with False so the wait bails cleanly rather than hanging.

**Construction-cycle break:** `RestartCoordinator` needs
`InstanceManager` for state polling; `InstanceManager` needs
`RestartCoordinator` to notify on TileLoaded. Ctor deps both ways
would deadlock DI. Solution: `AttachInstanceManager(im)` /
`AttachRestartCoordinator(rc)` setter methods called from
`ManagerProgram` after both singletons are resolved.

**Signal notification:** `NotifySignalObserved(instanceId, kind,
observedValue)` — called by InstanceManager from its log-event
handlers. Checks `_pendingSignals[instanceId]`, matches on kind +
(for `ServerStateEquals`) value, calls `pending.Tcs.TrySetResult(True)`
on match.

**Plugin fallback:** If the plugin doesn't implement
`IReadySignalProvider`, the coordinator falls back to a grace delay
(plugin's `DefaultReadyTimeoutSeconds` or 30s default) with no event
wait — still serialises access, just on a timer.

**InstanceManager change:** `HandleTileLoaded` (existing method in
log-stream handler) gets a new tail block that calls
`_restartCoordinator.NotifySignalObserved(instanceId, TileLoaded,
Nothing)`. Additive — doesn't change existing TileLoaded behavior.

**LastOasisPlugin update** (in Plugins\LastOasisPlugin.vb, the
deployed file — NOT in the source tree):
```vb
Implements IGamePlugin
Implements IReadySignalProvider

Public Function GetReadyForNextSignal() As ReadySignal Implements IReadySignalProvider.GetReadyForNextSignal
    Return New ReadySignal With {.Kind = ReadySignalKind.TileLoaded, .MatchValue = Nothing}
End Function

Public ReadOnly Property DefaultReadyTimeoutSeconds As Integer = 300 _
    Implements IReadySignalProvider.DefaultReadyTimeoutSeconds
```

LO's parser already emits TileLoaded when match state hits
LeavingMap, so the signal plumbing works out of the box.

### Phase 3b — CoordinatedRestartAction + slot acquire/release

**IRuleContext additions:**
- `AcquireRestartSlot(instanceId As String) As Task(Of Boolean)` —
  blocks on semaphores, returns True if acquired
- `ReleaseRestartSlot(instanceId As String) As Sub` — synchronous
  (not Task-returning) because it's called from Finally blocks, and
  VB doesn't permit Await in Finally

**CoordinatedRestartAction** (new action in IAutomationRule.vb):
Atomic acquire → stop → delay → start → wait-for-ready → release.
Slot release is in a plain `Finally` block (synchronous, allowed).
Properties: `InstanceId`, `GracefulTimeoutMs`, `DelayBetweenMs`,
`ReadyTimeoutSeconds` (0 = use plugin default).

**Serializer registration:** `"coordinated_restart"` → `GetType(CoordinatedRestartAction)`
added to `ActionTypes`.

**Coordinator additions:**
- `_heldSlots` dictionary (instanceId → RestartSlot) for the
  released-by-instance-id API
- `AcquireForInstanceAsync(instanceId)` — wraps `AcquireAsync`,
  rejects second concurrent acquire for same instance, stashes slot
- `ReleaseForInstance(instanceId)` — looks up + releases the stashed
  slot. No-op if nothing held.

**RuleContextImpl overrides:** `AcquireRestartSlot` and
`ReleaseRestartSlot` delegate to coordinator via lazy DI resolution
(same pattern as `WaitForReadySignal`). `ReleaseRestartSlot` wraps
everything in try/catch so a release-time failure can't mask the
original exception that caused the sequence to bail.

**Behaviour after Phase 3b:**
- Manual restarts (UI button, right-click menu) still fire-and-forget
  as before — NOT coordinated.
- Automation-rule restarts via `CoordinatedRestartAction` go through
  the coordinator with full queueing + ready-signal gating.
- No UI changes yet — Phase 4 materialises rules from EditInstanceForm.

**Cross-project rebuild reminder:** Phase 1, 3a, and 3b all modify
GSM.Contracts. After any contracts change, Node must be rebuilt too
(even though Node doesn't USE the new types — it links against the
same Contracts DLL, so a stale Node DLL is a loader mismatch risk).

### Phase 4a (partial) — SortOrder infrastructure

**New field:** `InstanceEntity.SortOrder As Integer = 0`. Position
within sibling list in an installation. Lower values come first.
Used by the stagger feature (Phase 4 continuation) and installation
panel reorder UI.

**Index:** `HasIndex(New With {InstallationId, SortOrder})` —
composite so `WHERE InstallationId = X ORDER BY SortOrder` uses it
directly.

**Helper:** `GsmDataExtensions.NextSortOrder(db, installationId)` —
returns `max(SortOrder)+1` across siblings, or 1 if none.
`DefaultIfEmpty(0)` pattern avoids "Sequence contains no elements"
on the first insert into a new installation.

**Migration:** `AddInstanceSortOrder`. Three steps:

1. `DropIndex("IX_Instances_InstallationId")` — old single-column
   index; the composite index supersedes it
2. `AddColumn("SortOrder", INTEGER, defaultValue:=0)`
3. `Sql(...)` backfill — see below
4. `CreateIndex("IX_Instances_InstallationId_SortOrder", {InstallationId, SortOrder})`

**Backfill SQL (IMPORTANT — and a hard-won lesson):**

```sql
WITH numbered AS (
    SELECT InstanceId, ROW_NUMBER() OVER (
        PARTITION BY InstallationId ORDER BY CreatedUtc, InstanceId
    ) AS rn FROM Instances
)
UPDATE Instances SET SortOrder = (
    SELECT rn FROM numbered WHERE numbered.InstanceId = Instances.InstanceId
)
```

**What went wrong the first time:** original attempt used a correlated
subquery where the ROW_NUMBER's partition filter collapsed to a single
row before the window function ran, yielding `rn = 1` for every row.
Fix: pre-compute the numbering in a CTE over the full table, then
UPDATE by joining back on InstanceId.

### Migration workflow lessons learned

- **`Update-Database X` is a goto, not an undo.** It means "bring the
  DB to the state immediately after X completes." To undo a single
  migration, pass the name of the PREVIOUS one. To undo all
  migrations, use `Update-Database 0`.
- **Editing a migration .vb file and rebuilding does NOT re-run it.**
  EF tracks applied migrations in `__EFMigrationsHistory`; anything
  listed there is skipped on next `Migrate()` call. To fix a
  misapplied migration: rollback past it (via `Update-Database
  <previous>`) so EF removes the history row, THEN rebuild and run
  so the corrected version applies fresh. OR: apply a corrective
  SQL directly in a DB browser.
- **Fresh deploy is always clean.** If a dev DB got poisoned by a
  bad migration run, the fix on disk still produces the correct
  behaviour on a fresh deploy — EF will run all migrations in order
  with nothing to skip. So a corrective migration isn't strictly
  needed if the dev can be fixed manually.

### Phase 4a — completion (Phase 4a closed)

The rest of Phase 4a landed across multiple iterations after the
SortOrder migration. All items below are done and tested.

**File:** `GSM.Manager\Core\RestartRuleMaterializer.vb` (new)

Three public functions, all `Public Shared` on the module:

- **`Materialize(db, instance) As MaterializationResult`** — reads
  `instance.RestartEnabled` + `RestartCron` + `RestartRuleId` and
  produces the right CRUD on the `AutomationRules` table.
  Does NOT call `SaveChanges` — caller owns the transaction so
  rule and instance commits are atomic. Returns an action enum
  (`NoChange` / `Created` / `Updated` / `Deleted`) plus the rule ID.
  Defensively refuses to stomp drifted rules (returns NoChange).
  Defensively clears `RestartRuleId` when disabling, even when the
  rule entity is missing — catches orphan ID cases.

- **`IsSimpleRestartRule(ruleEntity) As Boolean`** — structural
  drift detection. Returns true iff the rule matches the canonical
  shape: `Scope=Instance`, no conditions, `ScheduleTrigger`,
  `CoordinatedRestartAction` whose `InstanceId` matches `TargetId`.
  Does NOT check value-level fields (cron, timeouts) — only
  structural shape. Drift is purely structural.

- **`ExtractCronFromRule(ruleEntity) As String`** — reads the cron
  from the rule's `ScheduleTrigger`. Used by `EditInstanceForm` on
  load: the rule's cron is the authoritative value, NOT
  `Instance.RestartCron` (which is a cache that can drift if the
  rule was edited elsewhere).

**Comparison subtlety in `Materialize`:** can't call
`SerializeRuleToEntity(rule, existing)` because that mutates
`existing` in place — the post-mutation comparison would always be
equal. Solution: serialize into a fresh temp entity, compare to
existing, copy fields if different. The action enum then accurately
reports NoChange vs Updated.

---

### CoordinatedRestartAction — skip-when-not-Running guard

In `GSM.Contracts\IAutomationRule.vb` `CoordinatedRestartAction.Execute`:
state check BEFORE acquiring slot. If instance state isn't `Running`,
returns `ActionResult.Ok("Skipped: <id> is <State>, not Running")`
without side effects.

**Why Ok and not Fail:** the rule did exactly what it should have.
Nightly cron tick on a manually-stopped instance is a no-op by
design — logging it as `Failed` would be misleading. The execution
history shows "Executed" with the skip message in Details.

**Why before slot acquisition:** no point queueing behind other
restarts if we're going to bail. Cleaner too: don't have to remember
to release.

**Transitional state safety:** Starting / Stopping / Updating also
skip. Restarting mid-transition is destructive; better to skip and
let the next cron tick catch a stable state.

### AutomationEngine.Start() bug fix

The engine was registered in DI but never started. `_engineCts`
stayed null forever. First time anything called `ReloadRules`
which called `LoadRulesFromDatabase` which called `SetupTrigger`
which did `timer.Start(_engineCts.Token)` — NullReferenceException.

No previous test caught this because no rules existed in the DB
until the new EditInstanceForm started writing them.

Two-part fix:
- `ManagerProgram` now calls `engine?.Start()` after the chat
  pruner starts (services are wired by then). Plus matching
  `engine?.Stop()` in the shutdown hook for symmetry.
- `AutomationEngine.ReloadRules` is now self-starting: if
  `_engineCts` is null or cancelled, synthesize a fresh one. Makes
  `ReloadRules` safe to call from any UI path without requiring the
  caller to know about engine lifecycle.

---

### Phase 4a — closed

All Phase 4a items complete:
- Stagger + propagation in EditInstanceForm with all six save scenarios
- InstallationPanel reorder UI with Up/Down buttons
- Delete-instance warning + cascade
- Tree state preservation
- Plus: state-driven Start/Stop/Restart buttons, execution history
  details extraction, CoordinatedRestartAction skip-when-not-Running,
  engine startup fix, AutomationRulesForm non-modal singleton.

### Phase 4b-pre1 — scope & filter model expansion (closed)

Groundwork for the Phase 4b RuleEditorForm rewrite. Adds two
new rule scopes plus an optional game-level filter, so the
rewritten editor can express rules like "all Last Oasis
instances tagged 'realm-alpha' across any node" without us
having to backtrack on the model layer mid-form-rewrite.

**Design pivot during the round:** initial proposal was a
plugin-provided `IGroupingProvider` interface with a label
like "Realm" — plugins opt in, plugins without it hide the
grouping field. User counter-proposed `InstanceSetTag`: a
generic, game-agnostic, user-defined tag on every instance.
This is strictly simpler (no new interface, no plugin opt-in,
no per-game gating in the UI) and generalises better — a
Factorio admin can use it to group instances as production /
test, a Last Oasis admin uses it for realms. The plugin
doesn't know or care.

**Files modified:**

- `GSM.Contracts\IAutomationRule.vb`
  - `RuleScope` enum: added `Node`, `InstanceSet` (5 values total)
  - `AutomationRule.GameFilter` (nullable)
  - `AllInstancesEmptyCondition` refactored: `InstallationId`
    field replaced with `Scope`/`TargetId`/`GameFilter` triplet.
    Default `Scope = Installation` keeps the most-common
    historical use case working without a migration of
    serialised JSON (fresh DB anyway, but a defensive default).
  - `IRuleContext`/`RuleContext`:
    `GetInstanceIdsForScope(scope, targetId, gameFilter)`
    new method. Old `GetInstanceIdsForInstallation` kept
    as a thin convenience for existing callers.

- `GSM.Manager\Data\GsmDbContext.vb`
  - `InstanceEntity.InstanceSetTag` (nullable, 100 char cap)
    — indexed because the dominant access pattern at rule
    fire time is `WHERE InstanceSetTag = X [AND GameId = Y]`
    across the whole Instances table.
  - `AutomationRuleEntity.GameFilter` (nullable, 100 char cap)
    — NOT indexed; engine reads all enabled rules at
    startup/reload so per-rule filter is in memory.

- `GSM.Manager\Core\AutomationEngine.vb`
  - `RuleContextImpl.GetInstanceIdsForScope` — handles all
    5 scopes via direct EF query. Installation scope still
    delegates to InstanceManager (existing path) plus an
    optional `ApplyGameFilter` post-filter when the rule
    sets `GameFilter` on Installation scope (defensive;
    redundant for well-formed installations).
  - Misconfigured `Node`/`InstanceSet` scope with empty
    `TargetId` returns empty rather than "all instances" —
    avoids the footgun where a typo in target accidentally
    targets every instance in the system.
  - `DeserializeRule` reads `GameFilter` from entity column
    to in-memory rule
  - `SerializeRuleToEntity` writes `GameFilter` back

- `GSM.Manager\Core\RestartRuleMaterializer.vb`
  - `IsSimpleRestartRule` treats any non-null `GameFilter`
    as drift. Reasoning: a simple restart rule targets ONE
    specific instance whose game is already determined, so
    a `GameFilter` is at best redundant and at worst
    contradictory (e.g. user picks `GameFilter = factorio`
    on a rule for a Last Oasis instance — rule fires but
    resolves zero instances). Either way, the simple form
    can't express it.
  - `Materialize` comparison includes `GameFilter` so
    changes round-trip

- `GSM.Manager\UI\RemainingForms.vb` (EditInstanceForm)
  - New "Instance Set:" combo box with autocomplete pulling
    distinct existing `InstanceSetTag` values from the DB.
    `DropDownStyle = DropDown` (free-form text allowed) +
    `AutoCompleteMode = SuggestAppend` for live narrowing.
  - Empty-or-whitespace input normalised to `Nothing` on
    save — the InstanceSet scope query uses string equality,
    so empty string and Nothing should behave identically;
    storing Nothing keeps the data shape clean.
  - Form size grew 580×755 → 580×785 to fit the new row

**Migration:** `Add-Migration AddInstanceSetTagAndGameFilter`
then `Update-Database`. EF generates two AddColumn statements
plus the InstanceSetTag index. No backfill needed — nullable
columns default to NULL on existing rows.

**Out of scope for this round (deferred to 4b-1):**

- No UI to author rules with `Node`/`InstanceSet` scope or
  `GameFilter`. The existing stub `RuleEditorForm` still
  only supports Schedule/Manual/VersionMismatch with no
  action picker — it's effectively unusable but unchanged.
- No bulk Instance Set editor (have to tag instances one
  at a time via Edit Instance).

---

### Phase 5 — Version-mismatch trigger wiring (skeleton)

Closed the gap between the rule editor (which has supported
VersionMismatchTrigger since Phase 4b-1) and the engine
(which previously had no path to fire those rules).

**The architectural decision:** skeleton-only for now. A new
public `RaiseVersionMismatchAsync(installationId)` method on
AutomationEngine fires every enabled rule with a
VersionMismatchTrigger whose scope/target matches the
affected installation. The actual mismatch *detection*
(SteamCMD app_info_print polling, Factorio API polling,
etc.) is deferred to a future round — plugins or external
tools that already detect updates can call this method
directly today, and a future polling service will plug into
the same entry point without further engine changes.

Reasoning for the skeleton-first approach:
- Polling design has open questions (per-installation
  intervals, re-fire throttling, how to surface "available
  vs installed" version info in the UI) that aren't worth
  resolving in the same round as the engine wiring
- Plugins might want to push detection events via their
  own channels (Factorio's update API is push-friendly,
  Steam isn't) — keeping the entry point generic instead
  of polling-specific avoids forcing one design
- The rule editor's UI for VersionMismatchTrigger has been
  available for ~1 month with a "not yet wired" caveat;
  removing that caveat now closes the user-visible gap

**Scope-matching logic** in `RaiseVersionMismatchAsync`
mirrors the rest of the engine:
- **Instance** — rule fires if its TargetId is one of the
  instances under the affected installation
- **Installation** — rule fires if TargetId == installationId
- **Node** — rule fires if the affected installation lives
  on the rule's target node, with optional GameFilter pre-check
- **InstanceSet** — rule fires if any instance under the
  affected installation carries the rule's TargetId tag
- **AllInstances** — always fires, optionally narrowed by
  GameFilter

The scope-match helper `VersionMismatchRuleMatches` is
factored out for testability — takes pre-resolved
installation context (GameId, NodeId, instance ids, set
tags) so the matching logic is pure.

**Idempotency / throttling is the caller's concern.** The
engine has no "don't refire if user hasn't updated yet"
logic. A polling service that ticks every 5 minutes should
track which installations it's already raised for and only
call again when the upstream version changes again —
otherwise every poll cycle would refire every matching
rule. This is intentional: the engine doesn't know what
"version" means semantically (build numbers? patch dates?
git hashes?) — only callers know.

**Trigger reason format:** `VersionMismatch:{installationId}`.
Visible in the execution history's TriggerReason column,
lets users distinguish manual fires from version-driven
ones from scheduled fires.

**Files modified:**
- `GSM.Manager\Core\AutomationEngine.vb` — added
  `RaiseVersionMismatchAsync` (~95 lines) and
  `VersionMismatchRuleMatches` helper (~40 lines).
  `SetupTrigger` comment updated to note that
  VersionMismatch is wired via the new method.
- `GSM.Manager\UI\RuleEditorForm.vb` — trigger help text
  updated from "not yet wired" to "wired via
  RaiseVersionMismatchAsync, polling not yet automatic"

**What's still pending (deferred to a future round):**
- Automatic version-check polling service
- Per-plugin `GetLatestVersionAsync` capability on IGamePlugin
- "Installed version" / "Available version" columns on
  InstallationEntity
- UI surfacing of version info in InstallationPanel

### Phase 5 — Version-mismatch full implementation (closed)

The deferred items from the skeleton are now done. End-to-end
working version-mismatch detection: a polling service runs
every 60 minutes, checks each installation's upstream version,
and fires VersionMismatchTrigger rules when the upstream
advances past the installed build. UI surfaces installed vs
latest with a checked-Nm-ago hint and a manual "Check Now"
button.

**The architectural decisions:**

1. **Opt-in `IVersionAwarePlugin` interface** rather than a
   required method on IGamePlugin. Same pattern as
   `IDestinationTargetingPlugin` (4b-1.5) and `IReadySignalProvider`.
   Reasoning: plugins like webhooks or notification transports
   genuinely don't have a version concept; forcing them to
   throw NotImplementedException would be ceremony for nothing.
   Steam-installed games don't need it either — the existing
   `InstallationManager.CheckForUpdatesAsync` Steam path
   already handles them.

2. **60-minute poll interval.** Adjustable later via AppSetting
   if needed; for now hard-coded as a constant. Balance:
   short enough to catch updates the same workday they ship,
   long enough that SteamCMD invocations don't pile up (each
   Steam check spawns a SteamCMD process on the node and runs
   for 5-10 seconds against Valve's CDN).

3. **Throttling via `LastVersionCheckUtc` + 55-minute restart
   grace.** Manager restarts during dev iteration don't
   trigger an immediate fresh poll of every installation —
   would otherwise burn through Steam quota on every F5.
   Manual "Check Now" button passes `respectThrottle=False`
   to bypass.

4. **One event per detected upstream advance.** The mismatch
   event fires only when latest != installed AND latest !=
   previously-known. Subsequent polls finding the same
   upstream value update the timestamp but don't refire,
   avoiding hourly notification spam while the user takes
   their time to update.

**Files added/modified:**

- `GSM.Contracts\IGamePlugin.vb` — added
  `IVersionAwarePlugin` interface (~50 lines).
- `GSM.Manager\Data\GsmDbContext.vb` — added two columns
  to `InstallationEntity`:
  - `LatestKnownVersion As String` — last value the
    polling service observed from upstream
  - `LastVersionCheckUtc As DateTime?` — when the last
    successful poll happened (null until first success)
- Migration `AddVersionTrackingColumns` — generated via
  `Add-Migration` in PMC. Both columns nullable + additive,
  safe migration.
- `GSM.Manager\Core\VersionCheckService.vb` (new file,
  ~330 lines). Background polling service following the
  ChatRetentionPruner lifecycle pattern. Has both a
  background loop and a public `CheckInstallationAsync`
  for manual one-shot checks (used by the InstallationPanel
  "Check Now" button).
- `GSM.Manager\Core\AutomationEngine.vb` — already had
  `RaiseVersionMismatchAsync` from the skeleton round; no
  changes needed.
- `GSM.Manager\ManagerProgram.vb` — DI registration for
  `VersionCheckService` (singleton, alongside
  `ChatRetentionPruner`). Started AFTER `AutomationEngine`
  because the service raises events into it; stopped
  BEFORE the engine in shutdown order.
- `GSM.Manager\UI\UiPanels.vb` — InstallationPanel version
  label upgraded to show installed → latest with checked-Nm-
  ago suffix; "Check for Updates" button now routes through
  `VersionCheckService.CheckInstallationAsync` so it covers
  both Steam and plugin paths uniformly. NodePanel's
  Version column shows just the buildid ("22526048") via
  a `FormatVersionShort` helper instead of the full stamp,
  so the column doesn't get truncated to ellipsis.
- `GSM.Manager\bin\Debug\net8.0-windows\Plugins\FactorioPlugin.vb`
  — implements `IVersionAwarePlugin` via factorio.com's
  `latest-releases` JSON API. Adds an `UseExperimental`
  install config field so users can opt into tracking
  experimental builds.
  - LO plugin doesn't need to implement `IVersionAwarePlugin`
    — SteamCMD-installed, so the Steam path covers it.

**Two paths converge in `VersionCheckService.CheckInstallationAsync`:**

- **Steam path** (preferred when `InstallMethod=SteamCmd`):
  delegates to `_installationManager.CheckForUpdatesAsync`,
  which talks to the node and reads the ACF manifest. The
  result has `UpdateAvailable: Boolean` (authoritative — used
  for the firing decision) and `LatestBuildId: String` (used
  to format the stored `LatestKnownVersion`).
- **Plugin path** (fallback for non-Steam, or Steam path
  failure): if the plugin implements `IVersionAwarePlugin`,
  calls `GetLatestVersionAsync` and compares the returned
  string against `InstalledVersion` for the firing decision.
  Plugin authors are responsible for returning a string format
  that matches what `InstalledVersion` looks like for their
  game's install path — otherwise the comparison spuriously
  reports out-of-date forever (known limitation, documented
  in code).

Installations whose plugin is neither SteamCmd-based nor
`IVersionAwarePlugin` are silently skipped (warn-level log
entry only). VersionMismatch rules referencing those
installations simply never fire.

**Critical bug found and fixed during testing:**

First attempt stored just the raw buildid ("22526048") in
`LatestKnownVersion` while `InstalledVersion` was the full
stamp ("steam:920720@public build 22526048"). This caused
the UI to ALWAYS display "update available" even when
buildids matched, because string equality on the two
formats can't possibly succeed.

Fix:
- For the firing decision, use `result.UpdateAvailable`
  directly (authoritative — InstallationManager has already
  done apples-to-apples comparison via the ACF manifest).
- For storage, splice the latest buildid into the same
  prefix as InstalledVersion: `"steam:920720@public build
  {LatestBuildId}"`. Now string comparison works correctly
  for the UI display.
- Reload the local entity view after CheckForUpdatesAsync
  runs because that method updates InstalledVersion in its
  own DbContext scope.

**Trigger reason format:** `VersionMismatch:{installationId}`.
Visible in the execution history's TriggerReason column,
lets users distinguish version-driven fires from manual or
scheduled ones.

**Verified working end-to-end:** Manual "Check Now" button
for LO_Playground returns matching buildids and correctly
displays "Up to date (steam:920720@public build 22526048)"
in green; the version label shows the same.

**Known limitations carried forward:**

- **Plugin-path format alignment.** A plugin that returns
  "2.0.42" while InstalledVersion is
  "steam:427520@public build 12345" reports out-of-date
  forever. Plugin author's responsibility to return a
  matching format — future work could add a stamp-builder
  helper plugins reuse, but for now it's plugin-side. For
  Factorio specifically: if installed via SteamCMD, the
  Steam path takes priority and avoids the issue entirely.
  Direct-download installs would need the plugin to record
  a matching format on install.
- **No semver awareness.** Versions are opaque strings
  compared for equality only. "2.0.42" → "2.0.43" is
  treated the same as "2.0.43" → "2.0.42" (downgrade)
  — both are "different," so the rule fires either way.
  Acceptable since users authoring rules can add
  conditions if they want stricter semantics.

---

### Phase 4b-1.5 — NotifyAction transport gap closed

**New file:** `GSM.Manager\UI\ConditionEditorForm.vb`
(~440 lines)

- FixedDialog 640×360, mirrors RuleEditorForm's pattern:
  type combo + sub-editor swap, BuildFn/LoadFn lambdas
  closing over local controls
- Three sub-editor builders, one per condition type
- New `ConditionSubEditor` Friend class with an extra
  `ValidateFn` slot — conditions have varying validation
  needs and centralising in the form's OnSave (like
  RuleEditorForm does for actions) would require an enum
  dispatch on type. Cleaner to colocate validation with
  the sub-editor that knows its own controls.
- Receives lookup data (instances/installations/nodes/tags/
  game IDs) from RuleEditorForm via constructor — avoids
  re-querying the DB on every modal open and keeps display
  names consistent between parent and child forms.

**AllInstancesEmptyCondition sub-editor specifics:**
- Scope picker excludes Instance — single-instance reduces
  to WaitForPlayerCountCondition, no point having two ways
  to express the same thing
- AllInstances scope hides the target row (no per-target
  selection needed)
- Same scope-target-coordination logic as RuleEditorForm.
  Duplicated rather than abstracted because two callsites
  isn't enough to justify a shared helper class — the cost
  of factoring out an inter-form state class would exceed
  the cost of duplication
- The repopulateTarget closure is exposed so LoadFn can
  call it explicitly when loading an existing condition
  whose scope matches the default — same
  SelectedIndexChanged-doesn't-fire-on-no-op-assignment
  gotcha as RuleEditorForm.OnScopeChanged hit in 4b-1

**Modified file:** `GSM.Manager\UI\RuleEditorForm.vb`

- Replaced 70px Conditions placeholder with 150px real
  editor: ConditionMode combo (All / Any), Add/Edit/Remove
  buttons, Up/Down reorder buttons, ListBox with one-line
  summaries
- Form total height grew 800 → 880 to accommodate; Action
  group shifted from y=460 → y=540, buttons from y=700 →
  y=780
- Renamed `_preservedConditions`/`_preservedConditionMode`
  to `_conditions` since they're now editable. The
  conditions list is initialized to `New List(Of ICondition)`
  in the constructor BEFORE InitializeControls runs, so
  button handlers always have a real list to mutate even
  in new-rule mode
- New `SummarizeCondition` helper renders one-line
  descriptions with display-name lookups via
  `LookupInstanceName` / `LookupInstallationName` /
  `LookupNodeName`. Falls back to the raw ID when the
  lookup misses (instance deleted, etc.) — more useful
  than "(deleted)" because it lets the user copy-paste
  to identify what they had selected
- Up/Down buttons earn their place because conditions
  evaluate in order with short-circuit (first failure for
  All-mode, first pass for Any-mode); putting cheap fast-
  failing conditions first is a real performance lever
- Double-click on a list row triggers Edit — same
  affordance as InstallationPanel's instance list

**Persistence note (open):** AutomationRuleEntity does NOT
yet have a `ConditionMode` column. Like `OverlapPolicy`,
the AutomationRule object has it but the entity doesn't,
so it doesn't round-trip through the DB. Form defaults to
All on load. Adding a column for both is a small focused
migration that can land any time — noted here for future
pickup.

**Out of scope for 4b-2 (deferred):**
- "Test condition now" button — would require evaluating a
  condition outside rule context (no firing rule, no
  RuleContext), doable but separate feature
- Plugin-contributed condition types via
  `IConditionProvider` — interface exists in Contracts but
  no plugin uses it yet; SummarizeCondition's fallback
  branch will handle them gracefully when one shows up
- Condition templates / presets ("waiting for empty
  server" as a one-click)

Closed the gap from 4b-1 between the rule editor (which lets
users pick a NotificationDestination) and the runtime
(which previously dispatched via INotificationPlugin lookup).
Also added {Token} substitution for custom messages.

**The architectural decision:** rule-authored notifications
bypass the event-routing fan-out path entirely. They go
direct-to-destination via a new optional capability
interface that transport plugins opt into. Reasoning:

- The Notifications form's destination model is event-routing
  configuration: "when InstanceCrashed fires, send to these
  destinations." That's its job.
- NotifyAction is custom imperative messaging: "at this point
  in this sequence, send this exact prose to this one
  destination." That's a different shape.
- Bolting per-destination addressing onto INotificationPlugin's
  fan-out interface would force-fit two unrelated semantics
  into one method.

**New interface in Contracts:**
```vb
Public Interface IDestinationTargetingPlugin
    Function OwnsDestination(destinationId As String) As Boolean
    Function SendCustomToDestinationAsync(...) As Task(Of Boolean)
End Interface
```
Lives in `GSM.Contracts\INotificationPlugin.vb` alongside
the existing notification interfaces. Plugins opt in by
implementing it; plugins that don't are still valid
INotificationPlugins but won't appear in NotifyAction
dispatch. Currently only DiscordWebhookPlugin implements
it. Future transports (Slack, Telegram, email) will add
their own implementations.

**Field rename with on-disk back-compat:**
`NotifyAction.NotificationPluginId` → `DestinationId`. The
property is decorated with
`<JsonPropertyName("notificationPluginId")>` so it
serialises into the same JSON key as before. Any rules
saved before the rename load cleanly without a migration;
new saves write the same key. The codebase reads as the
new name, the storage format reads as the old name.

This required `Imports System.Text.Json.Serialization`
at the top of `IAutomationRule.vb` (Contracts).

**Dispatch resolution in NotificationService:**
New method `SendToDestinationAsync(destinationId, message,
severity, tokens)` iterates registered plugins, asks each
`IDestinationTargetingPlugin` whether it `OwnsDestination`,
and the first one to claim ownership handles dispatch.
No central registry of which plugin owns which destination
— plugins answer for themselves, which keeps NotificationService
free of transport-specific knowledge.

The old `SendSimpleAsync(pluginId, ...)` is marked
DEPRECATED in its summary but kept callable. No current
code path uses it (RuleContextImpl.SendNotification was
the only caller and it now routes through
SendToDestinationAsync). Will be removed once we're
confident no plugin-level callers remain.

**Token substitution:**
`NotificationService.SubstituteTokens(message, tokens)` is
a public Shared method that resolves `{Token}` placeholders
from a `NotificationTokens` bundle. Single regex pass over
the message string, MatchEvaluator-based so unknown tokens
stay literal in output (visible to user, easy to fix
rather than silently disappearing).

Supported tokens:
```
{RuleName}         {InstanceId} / {InstanceName}
{InstallationId}   {InstallationName}
{NodeId}           {NodeName}
{GameId}           {Time}              {Date}
```
The rule editor's Notify sub-editor shows the full list
as an italic gray help line below the Severity field.

Tokens are resolved by RuleContextImpl.BuildTokensFromContext
at fire time. For Instance-scoped rules, walks up
Instance → Installation → Node so all four levels'
names are available. For multi-instance scopes (Installation,
Node, InstanceSet, AllInstances) populates only the levels
that make sense (e.g. Node-scoped rules don't have a single
InstanceName).

Lookup failures (instance deleted between rule arming and
firing, etc.) are non-fatal: the corresponding token
substitutes as empty string and the notification still goes
out. Logged at Warning level for diagnosability.

**Visibility profile and templates: NOT applied to custom
messages.** The destination's VisibilityProfile is for
redacting structured event tokens (IPs, paths) from auto-
generated event notifications. A user-authored message is
literal prose; the author wrote it, presumably means it.
Templates are similarly skipped — templates transform
structured event data into prose, but custom messages are
already prose. The destination's `EventType = Custom`
context path renders the message as-is.

**Files modified:**
- `GSM.Contracts\IAutomationRule.vb` —
  `JsonPropertyName` import, `NotifyAction` field rename,
  `IRuleContext.SendNotification` parameter rename
- `GSM.Contracts\INotificationPlugin.vb` —
  `IDestinationTargetingPlugin` interface added
- `GSM.Manager\Core\DiscordWebhookPlugin.vb` —
  implements `IDestinationTargetingPlugin`,
  `OwnsDestination` + `SendCustomToDestinationAsync`
  methods (~100 lines)
- `GSM.Manager\Core\NotificationService.vb` —
  `SendToDestinationAsync` + `SubstituteTokens` Shared
  helper (~120 lines), `SendSimpleAsync` marked deprecated
- `GSM.Manager\Core\AutomationEngine.vb` —
  `Imports GSM.Notification`, `RuleContextImpl.SendNotification`
  rewired to call `SendToDestinationAsync`,
  `BuildTokensFromContext` helper (~110 lines)
- `GSM.Manager\UI\RuleEditorForm.vb` — removed orange
  warning label, cleaned up the hidden-overlay label hack
  from 4b-1, added token reference help text, updated all
  field references from `NotificationPluginId` to
  `DestinationId`

**Out of scope for 4b-1.5 (deferred):**
- Multi-destination notifications (one rule sends to many
  destinations). Could be a separate `BroadcastToTagAction`
  or a multi-select destination picker on `NotifyAction`.
- Reusing destination templates for custom messages —
  arguably nice but a different feature.
- Test/preview button in the rule editor that fires a
  message immediately to confirm wiring works.
- Removing the deprecated `SendSimpleAsync` and the legacy
  Plugin model entirely. Need confidence no caller paths
  remain first.

### NotifyAction transport gap (open, deferred)

During 4b-1 implementation, surfaced a real architectural
mismatch in how the Notify action targets recipients.

**The two-system landscape:**

- **System A — "Plugin" model (legacy):**
  - `NotificationPluginEntity` table stores `INotificationPlugin`
    registrations (Discord bot etc.) keyed by `PluginId`.
  - `NotificationService._plugins` holds the live plugin
    instances.
  - `SendSimpleAsync(pluginId, ...)` looks up by PluginId
    and dispatches via `plugin.SendNotificationAsync`.
  - `NotifyAction.NotificationPluginId` field stores a PluginId.

- **System B — "Destination" model (current):**
  - `NotificationDestinationEntity` table stores per-Discord-
    webhook destinations with scoping, visibility profiles,
    template overrides.
  - The Notifications form (rewritten at some point) manages
    these. Each destination has `Enabled`, `TransportKind`,
    `TransportConfigJson`, `EnabledEventTypesJson`, etc.
  - The emitter/broadcast path (`NotificationEmitter.Emitted`
    → `BroadcastAsync` → `plugin.SendNotificationAsync`) uses
    these destinations indirectly: each plugin reads them at
    send time and routes accordingly.

**The gap:** automatic event-driven notifications (server
started, crashed, etc.) work because they go via the
emitter/broadcast path which respects destinations. But
rule-action-driven notifications (`NotifyAction` from a
user-authored rule) go via
`SendSimpleAsync(NotificationPluginId, ...)` which looks
up by *PluginId*. Users author rules against destinations
(what they see in the Notifications form) but the runtime
can't dispatch to a destination ID.

**Why these systems are NOT redundant:** the Notifications
form is *declarative event routing* — "server crashed”
automatically goes to these destinations. NotifyAction is
*imperative custom messaging* — a rule that sends "realm
update in 5 minutes" at a specific point in a sequence,
with a custom message that no event type covers.

**Phase 4b-1 partial fix (this round):**

- `RuleEditorForm` now reads from `NotificationDestinations`
  (filtered to `Enabled = True`) so users see what they
  actually configured
- The action's `NotificationPluginId` field stores the
  selected `DestinationId` (field name kept for serialiser
  back-compat; rename deferred)
- An inline warning label in the Notify sub-editor explains
  that rules will save but the runtime dispatch won't fire
  until the transport refactor lands
- Validation messages updated to say "destination" not
  "plugin"

**Phase 4b-1.5 fix plan (deferred to its own round):**

1. Rename `NotifyAction.NotificationPluginId` →
   `DestinationId` (with `[JsonPropertyName("NotificationPluginId")]`
   on the property, OR a dual-read in the serialiser, so
   any rules already saved with the old field name still
   load).
2. Add `IRuleContext.SendCustomNotification(destinationId,
   message, severity)` distinct from the existing
   `SendNotification(pluginId, ...)`. Or repurpose
   `SendNotification` and update both callsites.
3. Implement the new context method in `RuleContextImpl` to
   resolve the `NotificationDestinationEntity`, look up its
   `TransportKind`, and dispatch directly via that
   transport's send path (bypassing the per-plugin
   broadcast logic which is event-type-driven).
4. Probably means a new helper on `NotificationService` like
   `SendToDestinationAsync(destinationId, NotificationContext)`
   that the rule context calls.

**Why not done in 4b-1:** scope creep. 4b-1's job was "build
the form." The transport refactor is its own design
conversation — e.g., should NotifyAction support multiple
destinations? Should it use the same template system as
event-driven destinations or always send a literal message?
Better to do deliberately than rush.

**Resolved in 4b-1.5** — see "Phase 4b-1.5 — NotifyAction
transport gap closed" section above for the full
resolution. This section retained for historical context.

---

### Phase 5 (future) — Manual restart coordination + Shift override

- Route Restart buttons + menu items through coordinator
- `Control.ModifierKeys.HasFlag(Keys.Shift)` at click time = force,
  bypass queue
- Tooltip on Restart button: "Shift+click to bypass restart queue"
- Grey-out restart buttons while instance is in a coordinated
  restart (UX feedback for the queue state)

### Automation refactor — file map (cumulative through Phase 4a closeout)

| Layer | File | New types added across automation refactor |
|---|---|---|
| Contracts | IGamePlugin.vb | ReadySignalKind, ReadySignal, IReadySignalProvider |
| Contracts | IAutomationRule.vb | WaitForReadySignalAction, CoordinatedRestartAction (with skip-when-not-Running guard); AcquireRestartSlot/ReleaseRestartSlot on IRuleContext + RuleContext |
| Manager Core | AutomationRuleSerializer.vb | Polymorphic JSON for ITrigger/ICondition/IAction |
| Manager Core | RestartCoordinator.vb | Singleton with semaphores + TCS-based ready-signal waits |
| Manager Core | RestartRuleMaterializer.vb | Materialize + IsSimpleRestartRule + ExtractCronFromRule |
| Manager Core | AutomationEngine.vb | RuleContextImpl overrides + self-starting ReloadRules |
| Manager Core | InstanceManager.vb | `_restartCoordinator` field + AttachRestartCoordinator + TileLoaded notification |
| Manager Root | ManagerProgram.vb | DI registration for RestartCoordinator + bidirectional Attach calls + engine.Start()/Stop() |
| Manager Data | GsmDbContext.vb | Entity fields: MaxConcurrentRestarts (Node, Installation), RestartEnabled/RestartCron/RestartRuleId (Instance), SortOrder (Instance); NextSortOrder extension |
| Manager UI | MainForm.vb | _suppressTreeAfterSelect flag; tree state preservation; non-modal AutomationRulesForm singleton; delete-instance cascade |
| Manager UI | UiPanels.vb | InstallationPanel reorder UI (Up/Down + # column); InstancePanel state-driven buttons |
| Manager UI | RemainingForms.vb | EditInstanceForm restart section (cron, presets, stagger, propagation radios, enable-on-all); AutomationRulesForm execution history details extraction; ApplyMinuteOffsetToCron helper |
| Plugins | LastOasisPlugin.vb | Implements IReadySignalProvider (TileLoaded kind, 300s timeout) |
