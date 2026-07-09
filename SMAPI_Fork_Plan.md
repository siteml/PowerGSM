# SMAPI Dedicated Server Fork — Integration Plan

Status: PLANNED. Companion to `StardewValley_Plugin_Plan.md` (this doc = Slice 1 of that plan, expanded).
Audience: smaller model + Site. Confirm-gated slices, STOP between each.

---

## Decision: separate repo, separate solution

Fork lives at `siteml/SMAPIDedicatedServerMod` (GitHub), NOT in the PowerGSM solution. **Upstream = `Chris82111/SMAPIDedicatedServerMod`** (maintained continuation of ObjectManagerManager — SDV 1.6.15/SMAPI 4.3.2, releases through Oct 2025, MIT). Original ObjectManagerManager repo is stale (Jan 2024, pre-1.6) — do NOT use as base. Rationale for separate repo:

- SMAPI mod = C#, references game DLLs (`Stardew Valley.dll`, `StardewModdingAPI.dll`, MonoGame) that exist only on machines w/ the game installed — cannot build in PowerGSM CI or on dev boxes w/o SDV.
- Must stay rebasable on upstream (`Chris82111/SMAPIDedicatedServerMod`) to pull SDV-compat fixes — needs its own git history.
- Release cadence tracks SDV/SMAPI versions, not PowerGSM versions.
- PowerGSM's coupling point = a GitHub release zip URL consumed by the plugin's download install step. Same relationship as SMAPI/SteamCMD: external artifact, versioned independently.

PowerGSM repo holds only: `StardewValleyPlugin.vb` (points at fork release URL as default `ModDownloadUrl`) + docs.

---

## Repo layout (fork)

```
SMAPIDedicatedServerMod/          (upstream structure preserved)
  DedicatedServer/                 mod project (name per upstream)
    ModEntry.cs
    manifest.json
    config.json (default)
  .github/workflows/release.yml    NEW — build+zip on tag
  PGSM_CHANGES.md                  NEW — delta vs upstream, for rebase sanity
```

Branch strategy:
- `upstream-main` — tracks upstream, never committed to directly
- `main` — PowerGSM version: upstream + PGSM commits on top
- Rebase `main` onto `upstream-main` when upstream ships SDV-compat fixes; PGSM commits are few + isolated (logging, shutdown, RCON) so conflicts stay small

Versioning: tags `pgsm-v1.0.0` etc. (distinct from upstream tags). manifest.json Version bumped in lockstep.

---

## Build requirements

- SMAPI mods build via `Pathoschild.Stardew.ModBuildConfig` NuGet — auto-locates game install, references game DLLs, packages mod zip on build. Verify upstream csproj already uses it (it should); if not, add.
- Build machine needs SDV installed (Site's desktop qualifies). CI (GitHub Actions) CANNOT build without game DLLs — see release workflow below.

## Release workflow (Slice F4)

Two options — Site picks:
- **A (manual, v1):** build locally in VS/`dotnet build -c Release`, ModBuildConfig produces the mod zip, attach to GitHub release by hand. Zero CI setup. RECOMMENDED to start.
- **B (later):** private NuGet/artifact of stripped reference assemblies to enable CI builds — real effort, legally gray (redistributing game assemblies even stripped). Defer indefinitely unless release friction hurts.

Zip layout must be: `DedicatedServer/` folder at zip root (manifest.json inside) — extracts directly into `Mods\`. Plugin install step assumes this.

---

## Slices

### Slice F1 — Fork + build baseline
1. Fork `Chris82111/SMAPIDedicatedServerMod` → `siteml/SMAPIDedicatedServerMod` (delete any earlier fork of ObjectManagerManager first — GitHub allows one fork of a network per account and OMM/Chris82111 share a network; delete via old fork's Settings → Danger Zone). Create `upstream-main` branch tracking upstream/main; `main` = working branch.
2. Clone locally (SUGGESTED: `C:\Users\Site\source\repos\SMAPIDedicatedServerMod` — keep out of PowerGSM tree so SDK/Roslyn never sees .cs files).
3. Build against SMAPI 4.3.2+/SDV 1.6.15 (upstream's tested pair; try current SMAPI 4.5.x — likely fine). Fix compile breaks. Run on Site's SDV copy, confirm mod loads + hosts a farm.
4. Add `PGSM_CHANGES.md` (empty changelog).

STOP — Site confirms "builds + runs" before F2.

### Slice F2 — Structured logging
Add `[PGSM]` lines via SMAPI `Monitor.Log(..., LogLevel.Info)` — single-line, key="value" quoted format:
- `[PGSM] READY farm="<name>"` — after world load complete
- `[PGSM] JOIN name="<n>" id="<uniqueMultiplayerId>"`
- `[PGSM] LEAVE name="<n>" id="<id>"`
- `[PGSM] CHAT name="<n>" msg="<text>"` — escape embedded quotes in msg (\" ) so regex stays single-line-safe
- `[PGSM] DAY season="<s>" day="<n>" year="<y>"`
- `[PGSM] INVITECODE code="<c>"` — when invite-code mode active
Hook points: SMAPI events (SaveLoaded, PeerConnected, PeerDisconnected, ChatMessage via multiplayer API or existing upstream hooks, DayStarted). Reuse upstream's existing event handlers where present — add log line, don't restructure.
Record every change in PGSM_CHANGES.md.

STOP — Site captures a session log, confirms all lines fire.

### Slice F3 — Clean shutdown
0. Window behavior (Windows only): game flashes fullscreen before mod minimizes it. `skipWindowPreparation=true` avoids flash but breaks mod's minimize (window stays foreground). Fix in fork: explicit minimize (ShowWindow SW_MINIMIZE / SDL equivalent) after mod init, independent of window-preparation pass. Linux/xvfb unaffected — no real window. Low priority; instances run headless-ish under node anyway.
1. Linux SIGINT + Windows console close (shim sends WM_CLOSE/CTRL_CLOSE): intercept, `Game1.player.team.SetLocalRequiredFarmers`... — CORRECTION: use SMAPI-safe path: trigger save via game's save logic then `Environment.Exit(0)`. Exact mechanism: research upstream — it may already have shutdown handling; if unclear HOW to save-on-demand safely mid-day, report options to Site (options likely: force sleep-save vs `SaveGame` direct vs exit-without-save-if-mid-day policy). DO NOT guess.
2. Verify: Windows stop via PowerGSM → exit 0, save intact. Linux → SIGINT, exit 0/130, save intact.

STOP.

### Slice F4 — Release v1
1. Confirm zip layout (`DedicatedServer/` at root).
2. Tag `pgsm-v1.0.0`, create GitHub release, attach zip (manual — option A).
3. Record release asset URL → becomes default `ModDownloadUrl` in StardewValleyPlugin.vb (Slice 3 of plugin plan).

STOP. Plugin plan Slice 2+ proceeds from here.

### Slice F5 — Pseudo-RCON (Tier 3, after plugin v1 works end-to-end)
NOTE: upstream already has an in-game command system (`/message ServerBot Sleep|ForceShutDown|Pause|InviteCode|Build|...` + password auth). F5 = add stdin as a second command entry point routing into that EXISTING command dispatcher — do not build parallel command handling.
stdin command channel:
1. Background thread reads `Console.In` lines. Commands: `save`, `say <msg>`, `kick <player>`, `stop`.
2. Execute on game thread (SMAPI `GameLoop.UpdateTicked` queue or `Game1.delayedActions` — research safe cross-thread pattern; report if unclear).
3. Respond `[PGSM] ACK cmd="<cmd>"` / `[PGSM] ERR cmd="<cmd>" msg="<why>"`.
4. PowerGSM side handled in plugin plan (Q5: RconProtocol stdin support — likely contract gap; Node owns stdin pipe already for SteamCMD keepalive precedent).

STOP.

### Slice F6 — Fork thinning (Tier 2 — can run any time after F4)
Remove upstream features PowerGSM supersedes: restart scripts, any Discord integration, invite-code file writes. Keep bot core only. Each removal = own commit, logged in PGSM_CHANGES.md (eases rebase conflict triage).

---

## Maintenance runbook (post-ship)

SDV or SMAPI update breaks mod:
1. `git fetch upstream` — did upstream fix it? → rebase `main` onto `upstream-main`, rebuild, retag.
2. No upstream fix → fix on `main` directly (PGSM_CHANGES.md), consider PR back upstream.
3. Rebuild, tag `pgsm-vX.Y.Z`, release, update `ModDownloadUrl` default in plugin (or users override field per-install — no plugin change strictly required).

Harmony note: if Tier 4 port/save-path patches land later, they're the most SDV-update-fragile pieces — check those first on any breakage.

---

## Open questions

- QF1: RESOLVED — upstream = Chris82111, builds against 1.6.15/SMAPI 4.3.2 per repo badges; verify vs current SMAPI 4.5.x at F1.
- QF2 (F3): safe save-on-demand mechanism mid-day (vs only at sleep)? Upstream `ForceShutDown` = kick all, new day, shut down — examine its implementation first.
- QF3 (F5): correct cross-thread execution pattern for stdin-driven commands — examine how upstream's chat-command handler executes; stdin path should marshal the same way.
- QF4: RESOLVED — MIT.
- QF5 (F2/plugin): upstream regenerates/invalidates config.json across versions (README warns old config unusable). Plugin writes config.json fresh every start — confirm plugin plan Slice 4 covers full config (not partial merge with existing file).
