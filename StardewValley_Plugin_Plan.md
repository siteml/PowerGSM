# Stardew Valley Plugin — Plan

Status: PLANNED. Not started.
Audience: written for a smaller model to execute. Follow slices in order.
Do NOT skip ahead. Each slice ends with a STOP — wait for Site to confirm before next slice.

---

## Rules for the executing model (read first)

1. All Windows file writes via Filesystem MCP. Every `edit_file`: run `dryRun:true` first, then identical `dryRun:false`. Never add extra keys to edit objects.
2. New files: `Filesystem:write_file`. Create directories first with `create_directory`.
3. Plugin files live in `GSM.PluginsSource\` and are Roslyn-compiled at runtime. Plugin-only changes = plugin reload, NOT Manager rebuild.
4. VB.NET landmines (all apply here):
   - No `vbLf`/`Chr`/`ChrW` in plugin files — use `Convert.ToChar(10)`.
   - Named regex groups via concat: `"(?<" & "Name" & ">..."` — never literal `(?<Name>`.
   - Extension methods need explicit `Imports` (e.g. `Imports Microsoft.Extensions.Logging`).
   - No async lambdas returning Task — extract named `Private Async Function`.
   - Avoid reserved keywords as identifiers.
5. Copy pattern from existing plugins: `FactorioPlugin.vb` (download-based install, stdout parsing) and `LastOasisPlugin.vb` (SteamCMD install with credentials). Read both before writing code.
6. Site's terse replies ("go", "works") = proceed.

---

## Background / decisions already made

- Game: Stardew Valley, Steam appid **413150**. NOT anonymous — SteamCMD install requires Site's Steam credential (existing credential flow handles this, same as Last Oasis).
- SDV has no real dedicated server. Server = full game client + SMAPI + a "dedicated server" mod that automates the host farmer.
- **Mod choice at install time** via install config field `ServerMod`:
  - `headless` (default) = PowerGSM fork of ObjectManagerManager/SMAPIDedicatedServerMod (fork to be created at `siteml/SMAPIDedicatedServerMod`)
  - `alwayson` (fallback) = Stardew Multiplayer Server Mod (Nexus 20659, funny-snek lineage)
- **Fork decision: YES** — Site owns the fork, updates it against SDV/SMAPI versions. Fork benefits: emit structured, regex-friendly log lines (join/leave/chat/day/ready); handle SIGINT + WM_CLOSE as save-then-exit.
- Saves live OUTSIDE install dir: `%APPDATA%\StardewValley\Saves` (Win), `~/.config/StardewValley/Saves` (Linux). Mod swap never touches saves.
- Plugin owns canonical config (FarmName, StartingCabins, etc.) and writes the selected mod's config.json at instance start. This is what makes mod-swapping between runs safe.
- Linux: run under `xvfb-run -a`. Graceful stop = SIGINT (matches existing LO/Linux pattern, exit 130 = clean).
- Windows: game shows a window; runs under GSM.Shim like everything else.
- **Concurrency constraints (v1):** vanilla SDV hardcodes UDP port 24642 (no config) → only ONE running instance per node in v1. Set `MaxInstancesPerInstallation = 1`. Saves dir is shared per OS user but each farm = own subfolder (`FarmName_UniqueID`) — collision risk is same-FarmName only, plus minor shared-file contention (`startup_preferences`). v1 mitigation: FarmName collision check. True multi-instance = Tier 4 fork Harmony patches (port + save path).

---

## Feature tiers — least friction/most required → most optional/highest effort

Organizing principle: PowerGSM absorbs *responsibilities* (restart, scheduling, config, monitoring); the fork stays a thin in-process agent (host-farmer bot + structured logs + clean shutdown). Bot logic CANNOT move into the plugin — it needs SMAPI hooks inside the game process; plugin is Manager-side and only sees args/files/log lines.

### Tier 0 — Required (plugin unusable without)
- SteamCMD credentialed install (appid 413150) + SMAPI + mod zip install steps
- `ServerMod` install-time choice (headless default)
- Launch via StardewModdingAPI; Linux xvfb; SIGINT/WM_CLOSE graceful stop
- Plugin writes mod config.json from canonical instance config (enables safe mod swap; saves live outside install dir)

### Tier 1 — Required for parity with other plugins (low friction)
- Fork emits `[PGSM]` structured lines: READY, JOIN, LEAVE, CHAT, DAY, INVITECODE
- `GetLogParseRules()` mapping those → PlayerJoin/PlayerLeave/ChatMessage/ServerStateChange
- `IReadySignalProvider` on `[PGSM] READY`
- Fork save-then-exit on SIGINT / console close

### Tier 2 — Fork thinning (low effort, reduces per-SDV-update maintenance)
- Strip from fork: crash-restart bat scripts, Discord bot integration, invite-code-to-file hacks — PowerGSM supersedes all (crash policy on Node, notifications plugin, INVITECODE log line)
- Result: fork = bot + logging + shutdown only

### Tier 3 — Pseudo-RCON (optional, moderate effort, highest payoff of optionals)
- Fork exposes a command channel; plugin drives it like RCON
- Transport options (pick one at design time): stdin line commands (simplest — Node already owns the stdin pipe) vs local HTTP listener (needs port mgmt). Lean stdin.
- Commands v1: `save`, `say <msg>`, `kick <player>`, `stop` (save+exit)
- Fork responds with `[PGSM] ACK cmd="..."` / `[PGSM] ERR ...` lines — reuses existing log parse path, no new response transport
- Plugin side: implement via existing RCON abstraction if `RconProtocol` supports custom/stdin, else report contract gap to Site — do NOT invent contract changes

### Tier 4 — Most optional / highest effort (backlog, not in slices)
- alwayson mod parse rules (blocked on real log captures — 5g-3 pattern)
- Richer bot control via pseudo-RCON: pause/resume, force-sleep, festival skip toggles
- **Multi-instance unlock via fork Harmony patches:** (a) patch Lidgren server init → port from mod config (vanilla hardcodes 24642), (b) patch save-path resolution → per-instance save dir. Both required for >1 concurrent instance per node. Until then `MaxInstancesPerInstallation = 1` stands.
- SMAPI/mod auto-update check (compare fork release tag vs installed)

---

## Slice 1 — Fork + structured logging (outside PowerGSM repo)

EXPANDED into its own document: see `SMAPI_Fork_Plan.md` (slices F1–F6). Summary of what must be done before plugin Slice 2:

1. Fork `ObjectManagerManager/SMAPIDedicatedServerMod` → `siteml/SMAPIDedicatedServerMod`.
2. Verify it builds against current SMAPI (4.5.x) + SDV (1.6.14+). Fix compile breaks if any.
3. Add structured log lines (single-line, stable prefixes) emitted via SMAPI Monitor:
   - `[PGSM] READY farm="<name>"` — world loaded, accepting connections
   - `[PGSM] JOIN name="<player>" id="<uniqueMultiplayerId>"`
   - `[PGSM] LEAVE name="<player>" id="<id>"`
   - `[PGSM] CHAT name="<player>" msg="<text>"`
   - `[PGSM] DAY season="<s>" day="<n>" year="<y>"`
   - `[PGSM] INVITECODE code="<code>"` (if invite-code mode)
4. Graceful stop: on SIGINT (Linux) / console close (Win), save game then exit 0.
5. Tag a release with a downloadable zip (GitHub release asset) — install step will download this.

STOP. Site confirms fork release URL before Slice 2.

## Slice 2 — Plan review + contracts check

1. Read `GSM.PluginsSource\FactorioPlugin.vb` and `LastOasisPlugin.vb` fully.
2. Confirm existing `InstallStep` subclasses cover: SteamCMD app install (with credential), file download, archive extract. If a needed step type is missing, report to Site — do NOT invent contract changes.
3. Confirm `ConfigFieldDescriptor` supports a string-choice field for `ServerMod` (or use string field with validation note).

STOP. Report findings.

## Slice 3 — StardewValleyPlugin.vb skeleton

New file: `GSM.PluginsSource\StardewValleyPlugin.vb`. GameId `stardewvalley`.

Install config schema:
- `ServerMod` — string, `headless` | `alwayson`, default `headless`
- `ModDownloadUrl` — string, default = fork release zip URL (overridable)

Instance config schema:
- `FarmName` — string, required. Manager-side validation: reject/flag if another instance on the same node already uses this FarmName (check how existing plugins do config validation; if no validation hook exists, report to Site — do NOT invent one)
- `StartingCabins` — int 0-3, default 3
- `CabinLayout` — string, default per mod
- `InviteCodeMode` — bool, default false (direct IP)
- NO Port field in v1 — vanilla hardcodes 24642 UDP. Document this. Port field arrives with Tier 4 fork patch.

Install steps (branch not needed here — same for both mods):
1. SteamCMD install appid 413150 (credentialed)
2. Download SMAPI release zip, extract, run its install into game dir (check Factorio plugin's pattern for download+extract; SMAPI install on headless = copy files per SMAPI unattended docs — if unclear, report to Site rather than guessing)
3. Download `ModDownloadUrl` zip, extract into `<install>\Mods\`

STOP after compiles + plugin reload shows it loaded.

## Slice 4 — Launch + config generation

1. `BuildLaunchArguments`: launch `StardewModdingAPI` (not game exe). Linux: wrapped in xvfb (confirm with Site whether wrapper goes in plugin args or shim/node concern — likely plugin emits plain command, node/shim handles display; DISCUSS before coding).
2. Before-start config write: plugin writes mod config.json into the selected mod's folder from canonical instance config. Branch on `ServerMod`:
   - headless: ObjectManagerManager config.json format (FarmName, StartingCabins, CabinLayout, …)
   - alwayson: that mod's format (research at implementation time; if undocumented, report)
3. Ready signal: implement `IReadySignalProvider` matching `[PGSM] READY` (headless) / best-available line (alwayson).

STOP.

## Slice 5 — Log parse rules

`GetLogParseRules()` branching on `ServerMod`:
- headless: rules for `[PGSM] JOIN/LEAVE/CHAT/DAY` → PlayerJoin/PlayerLeave/ChatMessage/ServerStateChange. Named groups via concat pattern (rule 4 above). Map `id` → `PlatformUserId`, `name` → `Name`, `msg` → `Message`.
- alwayson: whatever native lines exist (needs real log capture — same blocker pattern as 5g-3; if no captures available, ship headless rules only and leave alwayson rules TODO).

STOP.

## Slice 6 — Live verification

1. Install on Windows node via Manager UI, headless mod, real Steam credential.
2. Verify: install completes, instance starts, READY detected, join/leave/chat appear in Manager, graceful stop saves + exits clean.
3. Linux test node `10.5.2.63:8765`: repeat; verify xvfb + SIGINT stop (exit 130 or 0 per fork behavior).
4. Mod swap test: stop instance, change `ServerMod`, re-run mod install step, start — confirm same save loads.

STOP.

## Slice 7 — Docs

- CHANGELOG entry, `PowerGSM_Reference.md` plugin note, `docs/user/plugins.md` Stardew section (Steam credential requirement, saves location, mod choice, xvfb note), PlannedPlugins.md update.

---

## Open questions (resolve before/at relevant slice)

- Q1 (Slice 1): does alwayson mod even work headless/windowless enough to bother keeping as fallback? If not, drop enum to headless-only and simplify.
- Q2 (Slice 3): SMAPI unattended install method on both OSes.
- Q3 (Slice 4): xvfb wrapping — plugin vs node responsibility.
- Q4 (Slice 4/5): invite-code retrieval — fork emits `[PGSM] INVITECODE`; surface where in UI?
- Q5 (Tier 3): does existing `RconProtocol`/RCON abstraction accommodate stdin-based command channel, or is that a contract gap?
- Q6 (Slice 3): is there an existing plugin/Manager hook for cross-instance config validation (FarmName collision)? If not, where should the check live?
