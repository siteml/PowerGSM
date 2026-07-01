# Phase 5m — Manager resilience

## Status

`[core complete]` — plan doc written 2026-05-27 during the 5g-2d
shipping conversation; the twelve open questions were resolved
the same day (see the Decisions section). 5m-1 through 5m-3 shipped
(June 2026); 5m-4 (true service install) parked.

**Shipped (June 2026):**

- **5m-1 — Tray support.** NotifyIcon (Open / Restart-into-mode /
  Exit, double-click to restore), minimize-to-tray / close-to-tray /
  start-minimized prefs (Settings → Window), splitter-width
  persistence. Plus a Settings cosmetic pass: DB + plugins paths as
  read-only textboxes with Copy buttons.
- **5m-2a / 2b — Safe mode core.** `--safe-mode` flag, crash marker in
  `AppContext.BaseDirectory` (write-at-start / delete-on-clean-exit /
  auto-offer next launch), gated service startup per the "What safe
  mode disables" table, the SAFE MODE banner, and "Restart in Safe
  Mode / Restart Normally" in the File + tray menus (race-safe
  relaunch after clean shutdown).
- **5m-2e — Missing-plugin detection + start enforcement (added
  beyond the original plan).** Reconciliation-based orphan detection
  (catches startup / cross-session orphans the hot-reload diff
  misses), surfaced via a startup dialog + persistent banner +
  DarkRed tree badges, escalating to Critical when an orphaned
  instance is running. Hard guard in
  `InstanceManager.StartInstanceAsync` refusing to start a pluginless
  instance (covers every start path), with Start/Restart disabled in
  the panel + tree menu. See the CHANGELOG and the "Phase 5m —
  Manager resilience" gotchas in `PowerGSM_Reference.md`.
- **5m-2c — Safe-mode feature re-enable.** A subsystem-start
  controller in `ManagerProgram` plus a File-menu "Re-enable
  Features…" panel (safe mode only) that brings the individually-
  gated subsystems back up at runtime for iterative fix-and-test
  without leaving safe mode. Re-enable only; version-check pulls the
  automation engine up first.
- **5m-2d — Plugin enable/disable.** Plugin Status form gained a
  plugin-file list with Enable/Disable, moving files in/out of a
  `Plugins\Disabled\` subfolder + reload. Disable warns about
  orphaning; closing the form refreshes the orphan banner/badges.
- **5m-3 — Watchdog (auto-restart + start at sign-in).** Standalone
  `GSM.Watchdog` supervisor: launches the Manager, relaunches on
  unexpected exit, escalates to safe mode after 2 crashes/60s, gives
  up after 5/300s (exit 0, so the Task Scheduler backstop doesn't
  re-trigger the loop). Decoupled from the Manager (no shared
  assembly) via an exit-code contract (0 clean quit / 10 deferred /
  20 relaunch / 21 relaunch-safe) plus a shared mutex *name*. The
  Manager is now single-instance — a second launch brings the running
  one forward and bows out (deferred-exit when watched). Headless
  `WinExe` (logs to `watchdog.log`), co-located next to
  `GSM.Manager.exe` by the Manager's build/publish targets (mirroring
  Node → CtrlCSender). Settings → Startup toggle installs a per-user
  Task Scheduler logon task (`LeastPrivilege` + `InteractiveToken`,
  no UAC) from an XML definition with a restart-on-failure backstop.
  See the CHANGELOG and the "Phase 5m" gotchas in
  `PowerGSM_Reference.md`.

**Pending:** nothing in 5m's core scope — Phase 5m is functionally
complete. 5m-4 (a true Windows-service install for the Manager)
remains parked: the 5m-3 watchdog + logon task already delivers
auto-restart and start-at-sign-in without the service-install
complexity (no SYSTEM account, no session-0 GUI isolation, no UAC).

**Divergences from this plan, as built:** the banner is amber
(Warning severity), not red — red is reserved for the Critical
running-orphan escalation; the safe-mode "disable" set also covers
node background polling; "Exit Safe Mode" shipped as "Restart
Normally". The 2c/2d split and the whole 2e orphan track are
refinements made during implementation and aren't reflected in the
sub-phase descriptions below.

## Goal

Make the Manager survive production deployment unattended:

- Auto-restart on crash, like the Node already does.
- Start on system reboot, like the Node already does via its
  service install.
- Minimize / close to tray instead of forcing an open window.
- Recover from a faulty plugin / rule / startup path via a
  **safe mode** that disables the surfaces most likely to be
  carrying broken code, while keeping enough of the Manager
  alive that the operator can investigate and fix.
- Surface start-time failures with a "boot into safe mode"
  option instead of silently dying.

The Node has all of this (Windows Service install, auto-restart,
headless). The Manager has none of it. As automation and
notification coverage grows, a Manager that quietly stops is a
larger operational risk than the same Manager a year ago.

## Problem framing

Today's Manager is a regular WinForms app launched manually. If
it crashes:

- Automation rules stop firing. Scheduled restarts don't happen,
  Discord notifications stop, version-check polling pauses, the
  resolver cache stops being fed. Game instances themselves keep
  running (the Node owns them), but the Manager-layer
  intelligence around them goes dark.
- The operator doesn't notice until they next open the app or
  until something stops happening that should have.
- Recovery is "launch it again," and if the cause was startup-
  side (a plugin throwing during Roslyn compile, an automation
  rule with a corrupt OverlapPolicy, a malformed gsm.db
  migration), the relaunch crashes the same way. The only escape
  is to manually move the offending plugin file aside or hand-
  edit the DB, neither of which is friendly.

Safe mode is the in-app version of "move the plugin aside" —
boot with the risky surfaces disabled so the operator can use
the Manager UI to investigate and remediate, then exit safe mode
for a normal restart.

True Windows-Service install (Manager runs as a service like the
Node) is operationally appealing but architecturally large: a
Windows Service can't show UI cleanly (Session 0 isolation), so
the proper structure is engine-as-service plus UI-as-separate-
process talking to it over IPC. That's a refactor on the order
of the original 3-assembly split. **Out of scope for 5m v1.**
A watchdog process gets us most of the way (auto-restart, start-
on-boot via Task Scheduler) without the refactor; revisit true
service install only if the watchdog approach proves insufficient.

## Design

Four sub-phases, each independently shippable. Suggested
implementation order is 5m-1 → 5m-2 → 5m-3. 5m-4 is parked.

### Phase 5m-1 — Tray support

Pure WinForms work. No Manager-internal architecture changes.

**Behaviour:**

- A `NotifyIcon` lives for the Manager's lifetime, owned by
  `MainForm`. Icon image is the same as the app icon for v1
  (status-driven icon variants are a future polish).
- Context menu: `Open`, `Restart in Safe Mode`, `Exit`.
  - `Open` restores `MainForm` (`WindowState = Normal`,
    `Show()`, `Activate()`).
  - `Restart in Safe Mode` writes the crash marker and exits;
    the watchdog (or the user) relaunches into safe mode. If
    the watchdog isn't running, surfaces a confirmation dialog
    explaining the manual relaunch step.
  - `Exit` triggers real exit (the close-to-tray interceptor
    is bypassed for this path).
- Double-click on the tray icon = same as `Open`.
- **Minimize-to-tray**: intercept `MainForm.Resize`; when
  `WindowState = Minimized` AND the user preference is set,
  call `Hide()` so the taskbar entry disappears. Restore via
  the tray menu / double-click.
- **Close-to-tray**: intercept `MainForm.FormClosing`; when
  `e.CloseReason = UserClosing` AND the user preference is
  set, set `e.Cancel = True` and `Hide()` instead. Other close
  reasons (Windows shutdown, code-driven `Application.Exit()`)
  pass through normally.

**User preferences:**

- `MinimizeToTray` — default ON. Low-risk; users typically
  prefer their long-running apps minimize quietly.
- `CloseToTray` — default OFF. Users expect the X to mean
  exit; opt-in via Settings.
- `StartMinimized` — default OFF. Useful when paired with
  auto-start via watchdog/Task Scheduler.

Stored in the existing `AppSettings` key-value table via
`GsmDbContext.GetSettingInt` / `SetSetting` (keys
`ui.minimizeToTray`, `ui.closeToTray`, `ui.startMinimized`,
as 0/1 ints) — the same store chat-retention days and other
Manager settings already use. No new settings file. (Decision
#4.)

### Phase 5m-2 — Safe mode

The recovery mechanism. Self-contained — touches startup logic
and adds UI affordances, but doesn't introduce new services.

**Triggers** (any one is sufficient):

1. **`--safe-mode` CLI flag** — explicit user invocation.
   Parsed by `ManagerProgram.Main` before any service start.
2. **Crash marker at startup** — automatic recovery hint. See
   below for the marker mechanism.
3. **Tray menu → Restart in Safe Mode** — runtime escape
   hatch. Equivalent to writing the marker and exiting.
4. **Watchdog auto-trigger after N rapid restarts** — see 5m-3.

**Crash marker mechanism:**

- File: `safe-mode-marker.json` in `AppContext.BaseDirectory`
  (the binary's folder) — alongside the `logs` dir and, as of
  the gsm.db path fix that landed with this phase's planning,
  the database too. Chosen over a working-directory-relative
  path because the marker has to be found at the next startup
  regardless of how the Manager was launched; BaseDirectory is
  stable across launch methods where a relative path isn't.
  (Decision #1.)
- **Write**: at Manager startup, right after CLI parse and
  before any service start. Content: a JSON object with the
  Manager version, the startup timestamp, and the PID, so the
  next launch can decide whether the marker is stale.
- **Delete**: in `MainForm.FormClosing` after all services
  cleanly stop. A failure to reach that point (crash, kill,
  power loss) leaves the marker behind.
- **Read**: at next Manager startup, before write. If present
  AND not stale (timestamp within the last 24h), surface a
  dialog before service start.

**Startup decision flow** (in `ManagerProgram.Main`, after CLI
parse, before DI build):

```
If CliArgs.SafeMode Then
    safeMode = True
ElseIf CrashMarkerPresent() Then
    Dim choice = ShowCrashRecoveryDialog()  ' Safe / Normal / Exit
    safeMode = (choice = SafeMode)
    ' Normal: delete the marker (operator overrode the recovery)
End If

' Write the marker for THIS run before any service starts.
WriteCrashMarker(safeMode)

' Build the DI container; gate the service-start steps below
' on `safeMode` (see "What safe mode disables").
```

**What safe mode disables:**

| Service                          | Normal | Safe mode |
|----------------------------------|--------|-----------|
| DB migrations                    | run    | run       |
| IdentityResolver hydration       | run    | run       |
| NodeHttpClientFactory            | start  | start     |
| InstanceManager (basic ops)      | start  | start     |
| **PluginRegistry.ReloadAll**     | run    | **skip**  |
| **AutomationEngine.Start**       | start  | **skip**  |
| **NotificationService**          | start  | **skip**  |
| **VersionCheckService.Start**    | start  | **skip**  |
| **ChatRetentionPruner.Start**    | start  | **skip**  |
| **Discord plugin registration**  | run    | **skip**  |

Rationale: DB migrations, the resolver, and NodeHttpClient are
plugin-independent and read-mostly. InstanceManager (without
plugins) can still attach/detach nodes and view instance
state. Everything else is either plugin-driven or fires
automation, which is precisely the surface most likely to be
the failure cause.

**UI affordances in safe mode:**

- Top-of-MainForm banner: red, bold, full-width:
  `"SAFE MODE — plugins, automation, and notifications are
  disabled. [Exit Safe Mode]"`.
- The `Exit Safe Mode` button: writes a clean shutdown,
  spawns `Manager.exe` without the `--safe-mode` flag,
  exits. (The next launch's crash-marker check is bypassed
  because the marker just got deleted as part of the clean
  shutdown.)
- Tray icon variant: red overlay or "S" badge so a quick
  glance at the tray shows the Manager is in safe mode
  without restoring the window.
- File menu: "Restart in Normal Mode" mirror of the banner
  button.
- All log lines prefixed with `[SAFE]` so log forensics is
  easy.

**What stays editable in safe mode:**

- Installations, instances, nodes, automation rule defs
  (the operator can FIX a corrupted rule via the rule editor
  in safe mode; the engine just doesn't EXECUTE them).
- Plugin source files (the operator opens the plugin editor,
  fixes the broken plugin, then exits safe mode).
- Settings.

### Phase 5m-3 — Watchdog auto-restart

A small, separate process whose only job is "keep
`Manager.exe` running, restart on crash, give up after too
many rapid restarts." Mirrors the Node's "wrap the binary in
something that restarts it" approach without going full
service.

**New project:** `GSM.Watchdog` — separate `.vbproj`, console
app, .NET 8, single-file self-contained publish. Tiny —
target under 250 lines.

**Behaviour:**

```
LoadConfig()   ' restart limits, target binary path

Loop:
    LaunchManager()                  ' Process.Start(manager.exe, args)
    Wait for exit
    If clean exit (code 0):
        Log "manager exited cleanly", exit watchdog
    Else:
        Log "manager exited with code N (crash)"
        Push timestamp to restart-history ring buffer
        If RecentRestartsExceedLimit():
            Log "rapid-restart limit hit, giving up"
            Exit watchdog with non-zero code
        ElseIf RecentRestartsSuggestSafeMode():
            Args = "--safe-mode"
            Continue loop
        Else:
            Continue loop
```

**Config** (small JSON file next to the binary):

- `ManagerPath` — default `Manager.exe` in same directory.
- `MaxRestartsInWindow` — default 5.
- `WindowSeconds` — default 300 (5 minutes).
- `SafeModeAfterRapidCount` — default 2 (after this many
  crashes in a short window, the next restart adds the
  `--safe-mode` flag automatically).
- `SafeModeRapidWindowSeconds` — default 60.

**Logging:** own log file (`watchdog.log` next to the
binary). Records each launch, each exit, each restart
decision, and the give-up event. Helps post-mortem the
"why did the Manager just stop" question.

**Auto-start (start on system reboot):**

Two viable paths. Pick one as default, support both.

- **Task Scheduler entry** (recommended default): runs
  `GSM.Watchdog.exe` at user logon, with restart-on-failure
  enabled at the Task Scheduler level as a backstop.
  Installed by `install-watchdog.bat` similar to the Node's
  `install-service.bat`. Manual setup is also documented for
  users who prefer not to run a batch script.
- **Startup folder shortcut**: drop a shortcut to
  `GSM.Watchdog.exe` in
  `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\`.
  Simpler, less control, but no admin needed.

**`install-watchdog.bat`** (in deployment artifacts):
```bat
schtasks /Create /TN "PowerGSM Watchdog" ^
    /TR "<path>\GSM.Watchdog.exe" ^
    /SC ONLOGON /RL HIGHEST /F
```

**`uninstall-watchdog.bat`**:
```bat
schtasks /Delete /TN "PowerGSM Watchdog" /F
```

**Optional, not required:** the Manager can launch standalone
(direct `Manager.exe` from VS or a shortcut) without the
watchdog. Useful for dev / debug. The crash-marker mechanism
works either way — manual relaunch just doesn't auto-happen.

### Phase 5m-3 — design update (2026-06): Manager-installed + single-instance

Two refinements decided when 5m-3 came up for build, after 5m-1/2
shipped. These supersede the batch-file and launch details above.

**Auto-start is installed by the Manager, not a batch file.** A GUI
app should own this in-UI rather than asking the operator to run a
script as admin (the batch above used `/RL HIGHEST`, forcing a UAC
prompt the Manager doesn't need — it's a client, not the privileged
Node). Settings gets a "Start PowerGSM at login and restart it if it
crashes" toggle that creates / queries / deletes the Task Scheduler
entry via `schtasks.exe` (always present; no COM or NuGet dependency):

- **Normal run level + `ONLOGON`**, not `/RL HIGHEST` — a per-user
  logon task at normal level needs no elevation to create or delete
  and runs in the interactive session (GUI shows, DPAPI credential
  scope unchanged). Drops the UAC prompt entirely.
- **`schtasks /Create /XML <def>`**, not the inline `/TR` form. The
  install path contains spaces (`PowerGSM stuff`) and `/TR`'s quoting
  of exe-plus-args is notoriously fragile; an imported task-definition
  XML carries command and arguments in separate fields, and is also
  the only way to express the watchdog's own restart-on-failure
  backstop that the CLI flags can't set.
- The toggle reflects reality via `schtasks /Query /TN "PowerGSM
  Watchdog"`; toggling off runs `/Delete /F`.
- The `.bat` is demoted to a documented manual `schtasks` command in
  `RELEASE_PROCESS.md` for headless / scripted setups. This supersedes
  the "Node parallel" symmetry argument under Cross-references — the
  Node is headless so a script is right there; the Manager is a GUI
  and owns its own install.

**Single-instance, to stop duplicate Managers.** The watchdog launches
`Manager.exe`, but a duplicate Manager can also come from the operator
launching one manually on top of a watchdog-started one — so the guard
lives in the *Manager*, not only the watchdog:

- **Manager single-instance mutex** in `ManagerProgram.Main` (a named
  mutex, e.g. `Global\PowerGSM.Manager.SingleInstance`). A second
  instance signals the first to restore/focus its window and exits —
  covering every source of a duplicate (watchdog, manual launch,
  Startup shortcut), not just the watchdog.
- **Watchdog single-instance mutex** (`Global\PowerGSM.Watchdog…`) so
  two watchdogs can't both spawn Managers.
- **Watchdog monitors an existing Manager** rather than spawning: on
  start (and after any deferral) it probes the Manager mutex via
  `Mutex.TryOpenExisting`; if a Manager is already up it waits/polls
  until that one exits instead of launching its own. Sharing the mutex
  *name* across the two projects (a hardcoded constant in each — the
  watchdog doesn't reference the Manager assembly) is the detection
  channel.
- **Deferral exit must not look like a crash.** When the watchdog does
  launch a Manager that immediately exits because another instance
  holds the mutex, that "second instance, deferring" path must exit
  with a clean / sentinel code the watchdog reads as "a Manager is
  alive, stand down" — never as a crash to restart-loop. This is the
  easy-to-miss interaction between single-instance and the
  restart-on-nonzero logic in the Behaviour pseudocode above.

### Phase 5m-4 — True Windows Service install (out of scope)

Parked. Mentioned here so the absence is intentional rather
than overlooked.

A real Service install of the Manager would mean splitting:

- **Engine** — `InstanceManager`, `PluginRegistry`,
  `AutomationEngine`, `NotificationService`,
  `VersionCheckService`, the DI container, the DB. Runs as
  a Windows Service in Session 0. No UI.
- **UI** — the WinForms app, running per-user, connecting to
  the engine over IPC (named pipe, gRPC, or a local REST
  surface mirroring the Node's pattern).

Cost: roughly equivalent to the original 3-assembly split.
New IPC contract, every UI form now has a network-shaped
service boundary in front of the engine, all stateful
operations need to be safe across UI-disconnect/reconnect.

Benefits over the 5m-1+5m-2+5m-3 combination:
- Engine survives user logout / locked workstation cleanly
  (a watchdog'd Manager.exe doesn't if it requires an
  interactive session).
- Multiple operators can attach UIs to the same engine.
- Cleaner crash recovery (the service restart mechanism is
  better-defined than a watchdog process).

If the watchdog approach turns out insufficient in real use
(particularly: the "engine dies when the user logs out"
case bites someone), revisit. Otherwise leave parked.

## Touch points (file inventory)

- **`GSM.Manager\UI\TrayController.vb`** (new). NotifyIcon
  ownership, context menu, minimize/close intercepts, icon
  variant for safe mode.
- **`GSM.Manager\UI\MainForm.vb`** (modified). Wire the tray
  controller; add the SAFE MODE banner UserControl; intercept
  Resize / FormClosing.
- **`GSM.Manager\UI\RemainingForms.vb`** (modified, or new
  `SafeModeBanner.vb`). The banner UserControl + Settings tab
  for tray preferences, the latter persisting via the existing
  `AppSettings` table (`GetSettingInt` / `SetSetting`).
- **`GSM.Manager\ManagerProgram.vb`** (modified). CLI parse
  for `--safe-mode`; crash-marker write/read/delete; gated
  service-start logic; safe-mode decision dialog.
- **`GSM.Watchdog\`** (new project). `.vbproj` + `Program.vb`
  + `watchdogsettings.json`. Single-file self-contained
  publish. Single-instance mutex; monitors an already-running
  Manager instead of spawning a duplicate.
- **Settings — "Start at login / auto-restart" toggle** (in the
  tray-prefs Settings surface). Creates / queries / deletes the Task
  Scheduler logon entry via `schtasks /XML` (no UAC). Primary install
  path; a manual `schtasks` command in `RELEASE_PROCESS.md` is the
  fallback (replaces install-watchdog.bat as primary).
- **`GSM.Manager\ManagerProgram.vb`** (single-instance, additional).
  Named-mutex single-instance guard with focus-existing-and-exit, plus
  a deferral exit code the watchdog treats as non-crash.
- **`RELEASE_PROCESS.md`** (modified). Document the watchdog
  install steps and the safe-mode operator workflow.
- **`PowerGSM_Reference.md`** (modified). Add a "Safe mode
  for plugin debugging" section under the plugin-development
  guidance.

## Decisions (resolved 2026-05-27)

Each question from the original draft with its resolution. Two
changed from the initial leans after checking the codebase
(marked **changed**); the rest confirm the lean.

1. **Crash-marker location — RESOLVED (refined).**
   `AppContext.BaseDirectory\safe-mode-marker.json`, not the
   working-directory-relative path the initial "next to gsm.db"
   lean implied. The marker must be found at the next startup
   regardless of launch method; BaseDirectory is stable where a
   relative path isn't. Co-located with the `logs` dir and —
   after the related fix below — the DB.

2. **Default tray preferences — RESOLVED (as leaned).**
   MinimizeToTray ON, CloseToTray OFF, StartMinimized OFF.

3. **Crash recovery dialog default — RESOLVED (as leaned).**
   Defaults to Safe Mode, no timeout. The previous run crashed,
   so the safe choice is the default; the operator is present
   to override.

4. **Settings persistence — RESOLVED (changed).** The initial
   lean (`%APPDATA%\PowerGSM\settings.json`) is dropped — there
   is already a DB-backed key-value store (`AppSettings` table +
   `GetSetting` / `GetSettingInt` / `SetSetting` on
   `GsmDbContext`, used today for chat-retention days and
   others). Tray prefs reuse it: `ui.minimizeToTray`,
   `ui.closeToTray`, `ui.startMinimized` as 0/1 ints. No new
   settings store or entity.

5. **Watchdog auto-safe-mode threshold — RESOLVED (as leaned).**
   2 crashes within 60s → next launch adds `--safe-mode`.

6. **Watchdog give-up threshold — RESOLVED (as leaned).**
   5 restarts within 5 minutes → watchdog exits; the
   crash-marker recovery dialog then guides the next manual
   launch.

7. **Watchdog logging — RESOLVED (as leaned).** Verbose; one
   line per launch / exit / decision. Logs are tiny and the
   full history is worth having for the "dies every Tuesday at
   3am" diagnosis.

8. **Safe-mode banner placement — RESOLVED (as leaned).**
   Top-of-MainForm, red, full-width. Safe mode is a real
   non-default state; subtlety is wrong.

9. **NodeHttpClient in safe mode — RESOLVED (as leaned).** Yes.
   Plugin-independent; the operator can still view / attach
   nodes and see instance state.

10. **VersionCheckService in safe mode — RESOLVED (as leaned).**
    Disabled. It drives plugin update-check code — exactly the
    plugin-touching surface safe mode suppresses.

11. **Tray status overlay — RESOLVED (as leaned).** Deferred.
    v1 ships a static icon (plus the safe-mode variant);
    player-count / running-instance overlays are future polish.

12. **Safe-mode entry points — RESOLVED (as leaned).** Both the
    File menu and the tray context menu carry "Restart in Safe
    Mode", for operators in either UI state.

### Related fix that landed with this planning

The `gsm.db` relative-path footgun (the neighbour of Decision
#1) was fixed at the source rather than worked around: the
runtime DbContext now resolves the DB against
`AppContext.BaseDirectory`. See the CHANGELOG entry "Manager
database path anchored to the binary directory". Consequence for
5m-3: a Task Scheduler launch no longer risks a
fresh-DB-in-the-wrong-place data-loss event. Setting the task's
"Start in" to the binary dir remains good hygiene (other
relative-path assumptions could surface later) but is no longer
a correctness guard.

## Test plan

Per-sub-phase, in implementation order:

**5m-1 (tray):**
- Tray icon appears at Manager startup; tooltip shows
  "PowerGSM Manager".
- Minimize on a normal launch → window disappears, tray icon
  remains.
- Double-click tray → window restores.
- Close (X) on a launch with close-to-tray ON → window
  hides; with close-to-tray OFF → app exits.
- Tray menu `Exit` → app exits cleanly (regardless of
  close-to-tray setting).
- Windows shutdown while minimized → app exits cleanly via
  the `FormClosing.CloseReason` discriminator.

**5m-2 (safe mode):**
- `Manager.exe --safe-mode` from command line → SAFE MODE
  banner shows; verify plugins not loaded
  (`PluginRegistry.RegisteredPlugins.Count = 0`); verify no
  automation rule fires; verify no Discord activity.
- `--safe-mode` start → "Exit Safe Mode" button → app
  restarts in normal mode (verify banner gone, plugins
  loaded).
- Crash simulation: kill `Manager.exe` mid-run (Task
  Manager → End Task). On next manual start, the crash-
  recovery dialog appears; choose Safe Mode; verify same as
  above.
- Clean shutdown via tray Exit → no marker on disk; next
  start is normal (no dialog).
- Marker file integrity: kill Manager again, then directly
  inspect the marker JSON; verify timestamp + PID; relaunch
  picks up the marker.
- Plugin-load failure simulation: deliberately introduce a
  syntax error in a plugin .vb file, launch normally,
  watch it fail or degrade; then re-launch in safe mode,
  verify the Manager comes up, fix the plugin via the
  editor, exit safe mode → plugin loads.
- Long-stale marker: backdate the marker file to 48h ago,
  relaunch, verify the recovery dialog is suppressed
  (stale marker = ignore).

**5m-3 (watchdog):**
- Launch `GSM.Watchdog.exe` directly → it starts
  `Manager.exe`; close Manager via tray Exit → watchdog
  logs clean exit and itself exits.
- Crash Manager (`kill -9` equivalent) → watchdog restarts
  it within seconds; verify via the new process PID in
  watchdog log.
- Rapid-crash loop: write a plugin that throws on load
  (will crash the Manager during startup if it hits the
  faulty load path; or simulate via `Environment.Exit(1)`
  in `ManagerProgram` gated by an env var). Verify
  watchdog restarts, then after N rapid restarts adds
  `--safe-mode`, then after the give-up threshold exits.
- Task Scheduler: run `install-watchdog.bat`, log out and
  back in, verify watchdog launched at logon, verify
  Manager came up. Run `uninstall-watchdog.bat`, verify
  cleanup.
- Watchdog log forensics: trigger a crash + restart cycle,
  read `watchdog.log`, verify the entries are useful.
- Manual launch without watchdog: start `Manager.exe`
  directly → watchdog isn't running → Manager runs
  normally; crash → no auto-restart (expected); next
  manual start gets the safe-mode recovery dialog.

**Combined / scenario tests:**
- A faulty plugin that crashes Manager on load is the
  canonical scenario. Verify: watchdog restart loop →
  auto-safe-mode after N → operator notified by SAFE MODE
  banner → operator opens plugin in editor → fixes →
  exits safe mode → Manager loads cleanly.
- "Manager crashes overnight" simulation: leave Manager
  running with watchdog, manually kill it, verify it's
  back within the configured restart window, verify the
  crash event is logged.
- Update interaction (forward-looking): 5l's apply.cmd
  exits the running Manager during update; the watchdog
  shouldn't restart the OLD Manager during the update
  window. Likely resolved by apply.cmd writing a
  "watchdog pause" marker that the watchdog respects, or
  by apply.cmd stopping the watchdog before binary swap.
  Decide during 5l-3 implementation.

## Cross-references

- **Phase 5l (self-update)** depends on this for the "self-
  update broke something" recovery path. 5l-3's dry-run
  plugin-compat check catches static incompatibilities; safe
  mode catches the rest. Apply.cmd will need to coordinate
  with the watchdog (see test plan).
- **Phase 5g-2d's PluginRegistry** is the primary "faulty
  plugin" risk that safe mode addresses. The IdentityResolver
  itself runs in safe mode (read-mostly, low risk).
- **ROADMAP placement:** after 5k (panel), before 5l
  (self-update). Rationale: 5l is the next risky-to-startup
  change; safe mode should land first so 5l has a recovery
  net.
- **Node parallel:** the Node already has
  `install-service.bat` / `uninstall-service.bat` next to
  its binary. Mirror that pattern with
  `install-watchdog.bat` / `uninstall-watchdog.bat` next to
  the Manager binary, even though they use different Windows
  mechanisms (SC for Node services, schtasks for the
  watchdog Task Scheduler entry).
- **Reference doc:** add a "Safe mode for plugin debugging"
  section under the plugin-development guidance, plus a
  pointer in the gotchas table for "Manager won't start after
  plugin change."
- **Backlog impact:** none directly. The watchdog + safe mode
  pattern doesn't surface new backlog items, but it may
  expose latent ones (e.g., a long-running issue that only
  manifests when the Manager actually runs unattended for
  weeks) as the operator stress-tests it.
