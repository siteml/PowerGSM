# PowerGSM — Planned Plugins

A catalog of game-server plugins under consideration. This is the holding
pen: a game lands here as an idea, accumulates the research it needs, and
graduates to a `*_Plugin_Plan.md` plan doc (then the ROADMAP) once there's
enough to build against.

Companion docs:

- **`ROADMAP.md`** — where a plugin goes once it's scheduled.
- **`reference/plugins.md`** — how the Roslyn-compiled plugin model works.
- **`Windrose_Plugin_Plan.md`** — the worked example of a plugin plan doc.
- Shipped plugins as references: **Last Oasis**, **Factorio**, **Conan
  Exiles**, **Windrose**, **Stardew Valley** (in `GSM.PluginsSource\`).

> **All "first-look" notes below are unverified starting points** — best
> recollection of how each game's server *probably* works, not confirmed
> fact. Treat them as research leads, not gospel. Concrete specifics
> (SteamCMD app IDs, exe names, exact config paths, ports) are deliberately
> left as TBD until verified, because a wrong app ID is worse than a blank.

---

## How a plugin gets built here

Each plugin is a single Roslyn-compiled `.vb` file in `GSM.PluginsSource\`,
implementing `IGamePlugin` plus whichever opt-in side-interfaces it needs:

- `ILaunchOptionsProvider` — extra launch knobs.
- `IInstanceFileEditorProvider` — structured editing of a config file.
- `IStartupFileProvider` — render instance-config values into the game's own
  config file at launch (ports for file-only games, garble-prone text).
- `IManagedDirectoriesProvider` — surface saves/world/backup dirs.
- `IFileGenerationProvider` — user-triggered file/world generation.
- `IVersionAwarePlugin` — report installed vs upstream version.
- `IPrerequisiteProvider` — runtime deps (e.g. a JRE, VC++ redist).

Plan doc before code. Manager interprets, Node executes.

---

## Research checklist (pin these down before a plan doc)

For each game, the facts a plan doc needs:

1. **Install method** — SteamCMD app ID? other downloader? manual drop?
2. **Platform** — Windows-only / Linux / cross-platform (and does the
   Windows server run under Proton/Wine on a Linux node?).
3. **Engine / runtime** — UE4 / UE5 / Source / GoldSrc / Java / custom.
4. **Server executable + launch args** — and whether it needs a console
   window, `-log`, etc.
5. **Config file(s) + format** — INI / JSON / XML / `.cfg` / `.sii`.
   → candidates for the file editor and/or startup render.
6. **Ports** — game / query / RCON, plus allocator gotchas (reserved
   offsets, hard-coded pinger ports à la Conan's game-port + 1).
7. **Graceful shutdown** — RCON `quit` / CTRL_C / WM_CLOSE / none (UE4
   dedicated servers are the known hard case).
8. **RCON / remote console** — Source RCON / websocket / admin port /
   custom / none.
9. **Log parsing** — are player join/leave/chat/server-state lines present
   and regex-able for `GetLogParseRules`?
10. **Multi-instance per install** — safe, or one-instance-per-install
    (DB locks, shared `Saved/` dirs, server-list quirks)?
11. **Known gotchas** — the thing that'll bite if unguarded.
12. **Status.**

---

## Status legend

- `[idea]` — on the list, no research yet.
- `[research]` — facts being gathered.
- `[ready]` — enough known to write a plan doc.
- `[planned]` — plan doc written.
- `[wip]` / `[shipped]`.

---

## Index

| Game | Status |
|---|---|
| Dune: Awakening | `[blocked: architecture mismatch]` |
| RuneScape: Dragonwilds | `[ready]` |
| Valheim | `[ready]` |
| Palworld | `[ready]` |
| Enshrouded | `[ready]` |
| Nightingale | `[ready]` |
| Towers of Aghasba | `[blocked: no dedicated server]` |
| Soulmask: Shifting Sands | `[ready]` |
| Aloft | `[ready]` |
| Sunkenland | `[ready]` |
| Starbound | `[ready]` |
| Rust | `[ready]` |
| American Truck Simulator | `[ready]` |
| Assetto Corsa | `[ready]` |
| Mindustry | `[ready]` |
| OpenTTD | `[ready]` |
| Space Engineers | `[ready]` |
| Team Fortress Classic | `[ready]` |
| TERA | `[blocked: binaries?]` |
| Stardew Valley (SMAPI mod) | `[shipped]` (0.5.0, plugin v0.1.0, Tier 0) |

---

## Planned plugins

### Dune: Awakening
**Status:** `[blocked: architecture mismatch]` — researched; not viable in the
current node-executes-a-process model.

**Self-hosting exists** (live since ~May 2026; earlier it was official/paid-host
only). The Live server is a Steam *tool* (**AppID 4754530**; PTC is **3104830**,
and the two FLS environments aren't cross-compatible). But it is emphatically
**not** a single self-contained server executable like Conan / Factorio /
Windrose — it's a clustered, orchestrated world.

**What Funcom actually ships:** an Alpine Linux **VHDX** image containing
**k3s** (lightweight Kubernetes) + Funcom's k8s operators, pre-baked at
scale=0. On Windows it's booted in a **Hyper-V VM**; `initial-setup.ps1` + a
bash bootstrap grow the disk, pull the Linux server depot via SteamCMD, and run
`setup.sh` (scales the operators, patches a **BattleGroup** custom resource).
The actual game servers (UE5, "DuneSandbox" — same lineage as Conan's
ConanSandbox) run as **Linux pods**, alongside a **PostgreSQL** DB, a
gateway / "battlegroup director", and **RabbitMQ** queues. Terms: a *battlegroup*
= a world; a *Sietch* = one game-server instance hosting a map.

**Control surface:** a `battlegroup` CLI wrapper (`battlegroup.bat` on Windows /
`~/.dune/bin/battlegroup` inside the VM) — **not a process anything spawns
directly**. Subcommands: `status` / `start` / `stop` / `restart` / `update`
(SteamCMD pull → containerd load → CR patch → operator rolls pods) /
`apply-default-usersettings` / `logs-export` / `backup` / `import` (Postgres
dump/restore), plus VM commands (start/stop VM, rotate SSH key, change password)
and a shell for raw `kubectl`. There's a Director **web UI** for queue/server
monitoring. **No RCON.**

**Config:** a `UserSettings/` folder of **User INI** files mounted at
`/home/dune/server/DuneSandbox/Saved` *inside the VM* (persists across pod
restarts; needs a battlegroup restart to apply), plus a **YAML BattleGroup
spec** (edited via `vi` / advanced-edit) controlling per-map `MinServers` /
`NumExtraServers`, the director, RMQ, etc. Reachable only via the VM's file
browser / SSH — not on the host filesystem.

**Ports:** game servers **UDP 7777–7810**, RMQ **TCP 31982**; admin /
RMQ-management NodePorts (e.g. 31805 / 30438 / 30338) assigned dynamically by
k3s.

**Updates:** self-updating — the VM pulls and applies patches to stay
compatible with the live client. PowerGSM's version-check / update flow is
irrelevant here.

**Host requirements:** Windows 10/11 **Pro + Hyper-V**, BIOS virtualization,
20–40 GB RAM, ~100 GB storage, SSD mandatory, **dedicated bare-metal (not a
VPS)**, and **two IP/MAC addresses** (the external vSwitch binds the physical
NIC). Capped ~40 players per Hagga Basin.

**Verdict for PowerGSM.** This breaks every assumption the node makes: it
doesn't launch a single process (it boots a VM running a k8s cluster), there's
no stdout to tail (logs are pod logs via `logs-export`), no RCON, no
node-spawned lifecycle (start/stop/restart are `battlegroup` subcommands against
the cluster), config lives inside the VM, and the thing self-updates. Supporting
it would mean a wholly different node integration — "drive an external
orchestrator CLI / SSH+kubectl into a VM" — rather than the ProcessManager path,
and Funcom themselves call the tool early and expect it to change.

**Revisit if:** (a) Funcom ships a simpler single-process server — they've said
they want to make hosting "easier, less technical over time" and that
"additional options may be explored later" — or (b) PowerGSM grows a generic
external-orchestrator node mode that manages a CLI-driven cluster instead of
owning the process. A narrower unofficial path exists via the community
Linux-direct route (the VHDX boots under KVM/Proxmox; tools like
`adainrivers/dune-dedicated-server-manager`, `snapetech/DuneAwakeningSelfHost`
Docker Compose + web UI, and the benninger.ca Proxmox guide), but that's still a
cluster, not a process, and unsupported by Funcom.

### RuneScape: Dragonwilds
**Status:** `[ready]` — researched; a good fit for the existing model. Headline
risk is the SteamCMD `validate` save-wipe (below).

**Install:** free Steam tool "RuneScape: Dragonwilds – Dedicated Servers",
**SteamCMD AppID 4019830**, anonymous login. **Both Windows and Linux** (64-bit
only). RAM ~2 GB + 1 GB/player; caps at **6 players** (as of 0.11, Mar 2026).
Squarely in PowerGSM's wheelhouse.

**Engine / executable:** UE5. Windows: `RSDragonwilds.exe` in the server root;
Linux: `RSDragonwilds/Binaries/Linux/RSDragonwildsServer-Linux-Shipping` (a
`RSDragonwildsServer.sh` wrapper also exists). Recommended launch options
`-log -NewConsole` (`-NewConsole` gives a Windows console feedback window);
`-Port=7777` sets the port.

**⚠ Critical gotcha — SteamCMD `validate` wipes saves & config.** Confirmed by
multiple independent sources (official Linux wiki, a community Debian guide, the
community Windows updater .bat): running `app_update 4019830 validate` against a
**live** install deletes/overwrites anything not part of the pristine install —
**including `Saved/SaveGames/` and `DedicatedServer.ini`**. The community
patterns all work around it: stage to a separate dir + `rsync` excluding
`Saved/`, or back up `DedicatedServer.ini` + SaveGames before validate and
restore after. **Design constraint for the plugin / install flow:** confirm
whether `InstallRunner` passes `validate`; if so, gate it — validate is fine on a
*first* install (empty dir) but must be skipped (or replaced by a
stage-and-merge that excludes `Saved/`) on updates of a live install. This is
the single most important thing to get right for this game.

**Config:** `RSDragonwilds/Saved/Config/<platform>/DedicatedServer.ini` (INI).
**Path note:** sources disagree on the platform subfolder — `WindowsServer` on
Windows, and either `LinuxServer` *or* `Linux` on Linux; resolve per node
platform and verify the live path on first run. Key values:
- **OwnerID** — the host's in-game Player ID (Settings menu). **Mandatory — the
  server won't start without it.** `ValidateConfig` should require it.
- **ServerName**, **DefaultWorldName**, **AdminPassword** (grants the in-game
  Server Management tab), **WorldPassword** (supersedes the world's own).
- Max players.
These are clean `IInstanceFileEditorProvider` candidates; OwnerID / ServerName /
passwords could alternatively ride `IStartupFileProvider` if anything garbles on
the INI round-trip (unlikely — they're INI values, not args).

**Ports:** game **UDP 7777** (`IsPort`, allocator-managed); multiple worlds
increment 7777 → 7778 → 7779. Steam query **UDP 27015** (some hosts forward it
TCP+UDP). One-instance-per-install is the safe assumption — `DedicatedServer.ini`
and `Saved/SaveGames/` are shared per install, so multiple worlds want separate
install dirs.

**Saves:** `.sav` files in `RSDragonwilds/Saved/SaveGames/`; on start the server
loads the **latest** `.sav`, or creates a default Standard world if none.
**Never rename a `.sav` when importing** (breaks load). Managed-directory +
backup candidate (`IManagedDirectoriesProvider`).

**Version coupling:** server version must match the client version **exactly**
or the server won't appear / players can't join; the version prints at the top
of the server log. Good `IVersionAwarePlugin` hook (parse version from log), and
a reason updates need to be prompt.

**RCON / remote control:** none found — admin actions are in-game only (the
AdminPassword-gated Server Management tab). Player tracking would be
**log-parse only**.

**Graceful shutdown:** UE5 dedicated server — expect the same hard case as LO /
Conan (WM_CLOSE ignored), so force-kill is the likely fallback. Because the
server writes `.sav` periodically and loads the latest on start, an abrupt kill
risks losing progress since the last save — **check the autosave cadence / any
save-on-shutdown** before relying on force-kill.

**Log parsing:** the version line is easy; player join/leave/chat rules need a
real log capture with a player connecting (same blocker as Windrose Slice 4 /
LO 5g-3). Defer the parse-rules slice until samples exist.

**Suggested slicing:** (1) install (with the validate guard) + launch + OwnerID /
ServerName config; (2) `DedicatedServer.ini` file editor; (3) SaveGames managed
dir + backup; (4) log parse rules (blocked on samples). Community references
worth mining: the official wiki (Windows/Linux pages),
`Skerlord/Runescape-Dragonwilds-DebianSetup` (stage+rsync update pattern), and
`Obietek/DragonWilds-Dedicated-Server-Setup` (GUI manager).

### Valheim
**Status:** `[ready]` — researched; arguably the cleanest fit on the list.
Native cross-platform, pure launch-args config, Ctrl+C / SIGINT graceful stop.

**Install:** SteamCMD **AppID 896660** ("Valheim Dedicated Server"), anonymous.
**Native Windows, Linux, and macOS servers** (first true cross-platform binary in
this set) — Linux `valheim_server.x86_64`, Windows `valheim_server.exe`; ships
with `start_headless_server` example scripts. ~4–8 GB RAM; **10 players** default
(more needs mods). Linux needs a couple of libs (`libpulse0`, `libatomic1`) plus
the crossplay deps.

**Engine:** Unity (NOT Unreal) — so **Ctrl+C / SIGINT is a clean graceful stop**
(the shipped script literally says "PRESS CTRL-C to exit"). Maps straight onto
PowerGSM's **`GSM.CtrlCSender`** (Windows) + **SIGINT** (Linux) — the hard part of
the UE games is the easy part here.

**Config is launch-args, not a file** (LO-shaped): `-nographics -batchmode`
(headless), `-name`, `-world` (world / save identity), `-password`, `-port`
(2456), `-public 0|1` (server-list visibility), `-crossplay`, `-savedir`,
`-saveinterval`, `-backups`. So `BuildLaunchArguments` + `GetInstanceConfigSchema`
carry the whole config. **`ValidateConfig` rule (documented):** the password must
be **≥ 5 chars and must NOT equal the server name** — a clean, real validation
example.

**Ports:** UDP **2456–2458** — `-port` sets the base (2456) and the server uses
**port, port+1, port+2** (2457 is the query port you add to Steam favourites). So
the **allocator must reserve a 3-port block**, like other +offset games.
**Crossplay caveat:** `-crossplay` switches from Steam networking to a **PlayFab
relay with a Join Code** — no port-forwarding, but it **disables BepInEx mods**
and can add lag. So crossplay (BooleanField) changes the connectivity model
(join-code vs IP:port) — worth modelling explicitly.

**Admin / saves — `-savedir` is the key to isolation.** The
`AppData\LocalLow\IronGate\Valheim` / `~/.config/unity3d/IronGate/Valheim`
location is the **default** save path, **not a hardcoded one** — and per
IronGate's official guide a custom `-savedir` relocates the **worlds AND the
`adminlist.txt` / `bannedlist.txt` / `permittedlist.txt` files together** (they
all live in the save root, one level above `worlds/`). So passing **`-savedir
<install / instance-relative>`** does double duty: it puts the data where the
node can reach it, AND gives every instance its **own** admin / ban / permit
config — so there is **no one-instance-per-node limit and no shared-admin
compromise**. (Worth confirming on the live build in slice 1: older versions had
inconsistencies here, and some community setups symlinked the three lists as a
workaround.) Admin control itself is those three line-delimited Player-ID files
plus the host-only in-game F5 console — a simple managed-file / list-editor
candidate; worlds are a managed-dir + backup candidate (autosaves every 30 min;
`-saveinterval` / `-backups` are built in). **World modifiers** (combat
difficulty, death penalty) are set at world creation and **can't change after**
without a fresh world — warn in the UI.

**RCON / control:** **no native RCON** (vanilla). Remote admin = the text lists +
F5 console; structured remote control needs a BepInEx mod. Player state would be
**log-parse only** (needs samples).

**Multi-instance:** fully supported on one node — give each instance a distinct
`-port` block, `-world`, and `-savedir`, and it gets independent worlds *and*
independent admin / ban / permit lists (the lists relocate with `-savedir` — see
above), so no shared-config caveat.

**Fit verdict.** Top-tier — native cross-platform, args-only config, real
Ctrl+C / SIGINT stop, simple text admin. No RCON and no exotic config format. The
only real design choices are the 3-port allocator block, the crossplay
connectivity toggle, and pushing `-savedir` into the install so saves + admin
lists are manageable.

**Suggested slicing:** (1) install + launch (`-name` / `-world` / `-password` /
`-port` / `-public`) + the password ValidateConfig rule + 3-port allocator block;
(2) `-savedir` into install + worlds managed dir + backup; (3) admin / ban /
permit list editors; (4) crossplay toggle + connectivity model; (5) [later] log
parse rules (blocked on samples).

### Palworld
**Status:** `[ready]` — researched; strong fit. Two standout notes: a quirky
single-line config format, and control should target the REST API (RCON is being
removed).

**Install:** SteamCMD **AppID 2394010** ("Palworld Dedicated Server"),
anonymous, free (don't need to own the game). **Windows and Linux** (official
Docker image on Linux too). ~4 GB RAM (uses ~1 GB), ~12 GB disk; up to ~32
players.

**Engine / executable:** UE5. Windows `PalServer.exe`, Linux `PalServer.sh`.
Common launch flags: `-publiclobby` (community server — appears in the in-game
list, lets console players find it) plus perf flags
(`-useperfthreads -NoAsyncLoadingThread -UseMultithreadForDS`).

**⚠ Config format wrinkle — not normal INI.**
`Pal/Saved/Config/<platform>/PalWorldSettings.ini` holds a single section
`[/Script/Pal.PalGameWorldSettings]` whose entire content is one giant line:
`OptionSettings=(ServerName="...",AdminPassword="...",PublicPort=8211,...)` — a
comma-separated, parenthesised struct of ~100+ keys, **not** one-key-per-line.
Worse, on a fresh install `PalWorldSettings.ini` is effectively empty; the
operator must copy the block out of `DefaultPalWorldSettings.ini` first. So a
file editor here needs a **bespoke parser for the `OptionSettings(...)` tuple**
(split on top-level commas, respect quotes), not the section/key INI writer Conan
uses. Settings only load on boot — edits require a restart (there is no live
`/ChangeSettings`; that command is a myth).

**Key settings:** ServerName, ServerDescription, AdminPassword, ServerPassword,
PublicIP, PublicPort (8211), RCONEnabled / RCONPort (25575),
RESTAPIEnabled / RESTAPIPort (8212), plus the full balance set (rates,
Difficulty, DeathPenalty, player/pal caps…). `PublicIP` must be the node's
external IP for master-list visibility (an "advertise IP" need, like LO / Conan).

**Ports:** game **UDP 8211** (PublicPort, `IsPort`), Steam query **27015**
(TCP/UDP, browser visibility), RCON **TCP 25575** (optional), REST API **8212**
(local HTTP). All allocator-managed.

**Control — target the REST API, not RCON.** Palworld exposes an HTTP REST API
(`RESTAPIEnabled=True`, default port 8212, HTTP Basic auth: user `admin`, pass =
AdminPassword): `GET /v1/api/players` (structured player list — name, UID, Steam
ID), `/v1/api/info` (version), `/v1/api/settings`, `POST /v1/api/save`,
`/announce`, `/kick`, `/ban`, `/shutdown`. **RCON still exists but is deprecated
by Pocketpair and "scheduled to stop functioning in an upcoming update"** — so
PowerGSM's RconClient is the wrong path here; a small REST client is the play.
Bonus: the REST player list means **no log parsing needed** for player state
(like Nightingale's `/status`). (RCON also mangles multi-byte player names —
another reason to skip it.)

**Graceful shutdown — actually clean (rare for UE).** Real stop paths exist:
REST `POST /v1/api/shutdown`, or console / RCON `Shutdown {sec} {msg}` /
`GracefulStop {sec} {msg}`, and even closing the console window saves + exits.
`DoExit` is immediate and does NOT save (pair with `Save`). So unlike LO / Conan,
PowerGSM can do a proper announced graceful stop via the REST API — a notable
plus.

**Saves:** `Pal/Saved/SaveGames/<worldid>/`. Managed-dir + backup candidate.
One-instance-per-install is the safe assumption (config + saves shared per
install).

**Fit verdict.** Strong — SteamCMD single-exe UE5, structured player data + clean
shutdown over a documented REST API, INI(-ish) config. The real engineering is
the `OptionSettings(...)` struct editor and a tiny REST client instead of
leaning on RCON. No save-wipe-on-validate issue reported (unlike Dragonwilds).

**Suggested slicing:** (1) install + launch (`-publiclobby`, perf flags) + core
config (ServerName / passwords / PublicIP); (2) `OptionSettings` struct file
editor; (3) REST client for player list + announced graceful shutdown + save;
(4) SaveGames managed dir + backup. RCON intentionally skipped (deprecated).

### Enshrouded
**Status:** `[ready]` — researched; clean Factorio-shaped fit (JSON config, no
RCON, log-based readiness). One deployment caveat: the server is Windows-only
(Proton / Wine on Linux nodes).

**Install:** SteamCMD **AppID 2278520** ("Enshrouded Dedicated Server"),
anonymous. Up to **16 players**.

**⚠ Windows-only binary.** There is **no native Linux server** — the community
runs the Windows build under **Wine / GE-Proton** (the popular `jsknnr` and
`mornedhels` Docker images wrap exactly this), and a Linux SteamCMD must force
the Windows depot with `+@sSteamCmdForcePlatformType windows`. So: trivial on a
**Windows node**; a **Linux node needs a Proton/Wine layer** PowerGSM doesn't
have today. Cleanest first target is Windows nodes only; Linux is a later
"add a Proton wrapper" effort (a node-side capability, not just a plugin change).

**Engine / executable:** Keen Games' own engine (NOT Unreal — so none of the UE
shutdown pain). `enshrouded_server.exe` in the server root; opens a console.
**No launch arguments** — "no known commandline parameters"; everything is in the
JSON. Launch is dead simple.

**Config:** `enshrouded_server.json` in the server root (JSON → clean file-editor
candidate, same JsonNode preserve-unknown approach as Factorio's
`server-settings.json`). Key fields: `name`, `ip` (bind adapter), `gamePort`
(15636), `queryPort` (15637), `slotCount` (max 16), plus voice / text-chat and
game-difficulty settings. **Auth nuance — Server Roles, not a flat password:**
the old top-level `password` is **deprecated / ignored**; access is now a
`userGroups` array (roles like Admin / Friend / Guest, each with its own
`password` and permissions — canKickBan, canAccessInventories, canEditBase,
reservedSlots…). The file editor must handle that nested roles structure, not
just scalar fields.

**Ports:** game **UDP 15636** (`gamePort`), query **UDP 15637** (`queryPort`) —
both `IsPort`, allocator-managed; multiple servers per host change both.

**Readiness + control:** the console prints **`Host_Online (up)!`** on successful
start — a clean **`IReadySignalProvider`** hook (parse the log instead of
guessing from process state). **No RCON, no REST API** — player state would be
log-parse only (needs a capture; defer that slice).

**Graceful shutdown — works, but be patient.** Not a UE server: **closing the
console saves and exits cleanly**, but the save flush can take a while — the
reference Docker image sets a **90 s stop grace period**. So PowerGSM should send
the stop (WM_CLOSE / CtrlCSender) and **wait up to ~90 s** for a clean exit; a
fast force-kill risks **save corruption**. Updates: stop first, back up the
savegame folder, then `app_update 2278520 validate` — never update while running.

**Saves:** a `savegame/` folder under the install (managed-dir + backup
candidate; the community images ship zip-backup scripts). Under Proton the path
gets buried in `compatdata/.../pfx`, another reason Linux support is fiddlier.
One-instance-per-install is the safe assumption.

**Fit verdict.** Strong on a Windows node — JSON config editor + a real ready
signal + a genuine (if slow) graceful stop, and no RCON to worry about. The only
real work beyond the Factorio-style pattern is the `userGroups` roles editor and,
for Linux, a Proton / Wine node capability.

**Suggested slicing:** (1) install + launch + `name` / ports / slots (Windows
node); (2) `enshrouded_server.json` editor incl. Server Roles; (3) `Host_Online`
ready signal; (4) savegame managed dir + backup; (5) [later] Proton / Wine for
Linux nodes; (6) [later] log parse rules (blocked on samples).

### Nightingale
**Status:** `[ready]` — researched; strong fit, and the standout
`IReadySignalProvider` candidate of the whole list thanks to a real HTTP
`/status` endpoint.

**Install:** SteamCMD **AppID 3796810** ("Nightingale Dedicated Server"),
anonymous. Dedicated servers shipped **July 2025** (Early Access; Inflexion
Games, UE5 — project "NWX"). Windows; Linux via SteamCMD / the community Docker
image (`fireblade004/nightingale-server`) — **confirm native-Linux vs Proton**.
Up to **6 players** co-op. `-log` gives a console window; logs at `NWX/Saved/Logs`.

**Marquee feature — `/status` HTTP readiness endpoint.** Launch with
**`-statusPort=<1024-65535>`** and the server serves a **`/status`** endpoint
(localhost-bound by default). While loading it returns **`503 Service
Unavailable`** + JSON; once ready for players, **`200 OK`** + status JSON. That's
a textbook **`IReadySignalProvider`** — poll `/status`, treat 200 as "up", 503 as
"still loading" — *and* a structured status source, cleaner than log-scraping for
readiness (and likely player count). This is the best ready-signal mechanism in
the catalog. (To expose it off localhost, also override the HTTP listener bind:
`-ini:Engine:[HTTPServer.Listeners]:+ListenerOverrides=(Port=<port>,BindAddress=<IP>)`,
or `0.0.0.0` for all interfaces.)

**Config — launch args + UE `-ini:` overrides** (not a tidy single file).
Settings flow through the command line and UE's `-ini:Section:Key:Value`
mechanism: `MAXPLAYERS`, the connection / server password, server IP / bind, game
port, and `-statusPort`. (The community Docker surfaces `MAXPLAYERS` /
`CONNECTIONPASSWORD` env vars that map onto these.) So this is LO / args-shaped
with UE `-ini:` syntax — `BuildLaunchArguments` carries it; a structured "config
file editor" is less central here than for the JSON / INI games.

**Ports:** game **7777 (TCP + UDP)** per the official Docker mapping; **some
guides cite 27015 / 27016 UDP + 27017 TCP** (likely Steam query / auth) — **verify
the real set on the build**. Plus the separate **`-statusPort`** HTTP port. All
`IsPort` / allocator-managed; Nightingale binds all interfaces by default.

**⚠ Realm-import-on-first-connect gotcha (unique).** On a brand-new server that
no one has joined yet, **the first player to connect imports THEIR characters and
realms into the server** (Nightingale's portal / Realm-Card model carries the
player's realms over). To get a clean / empty server you **join first with a fresh
character** (sets up an empty Abeyance realm), then reconnect with your real
character. The plugin's UI should warn about this — it's a genuine data-model
surprise with no analogue in the other games.

**Saves:** persistent state under **`NWX/Saved/`** (the Docker binds
`/config/gamefiles/NWX/Saved` for savedata) — managed-dir + backup candidate.
There's also a built-in weekday backup-on-first-launch of offline realms worth
noting. One-instance-per-install is the safe assumption (distinct ports +
`-statusPort` + `NWX/Saved` for multi-instance).

**Graceful shutdown / RCON:** UE5 dedicated server, so **expect the LO / Conan
WM_CLOSE hard case** (force-kill fallback) unless a shutdown path turns up —
verify. **No RCON**; the `/status` endpoint is read-only, so there's no remote
command channel (admin is in-game). Player state comes from `/status` JSON
(structured — likely no log parsing needed).

**Fit verdict.** Strong, and the cleanest readiness story going (`/status`
503→200). Config is args / `-ini:`-driven rather than a file editor; the two
things to verify on the build are the exact port set and graceful-shutdown
behaviour, and the realm-import gotcha needs a UI warning.

**Suggested slicing:** (1) install + launch (`MAXPLAYERS` / password / port /
`-statusPort`) + the `/status` `IReadySignalProvider`; (2) `NWX/Saved` managed dir
+ backup; (3) status-JSON player surface; (4) graceful-shutdown behaviour
(verify) + realm-import UI warning.

### Towers of Aghasba
**Status:** `[blocked: no dedicated server]` — researched; no self-hostable
server exists.

**Multiplayer is peer-to-peer, host-based co-op** — there is **no dedicated
server** (Dreamlit Inc., UE5, EA since Nov 2024). It's an "Animal Crossing-style"
model: one player hosts via an in-game **Multiplayer Gate** that generates a
**path / session code** over Steam P2P; up to **4 players** (host + 3) visit the
host's island. Story / quest progression is single-player (host-only even in the
beta co-op), and the official Steam page calls multiplayer a **"prototype"**.
There's no headless / server process for PowerGSM to manage — the "server" is just
a player's game client hosting a session.

**The hosting-provider listings are a trap.** Survival Servers / ScalaCube /
Citadel all *advertise* "Towers of Aghasba server hosting," but these are
auto-generated SEO templates — ScalaCube's page literally describes airships and
dogfights (a completely different game). Treat them as zero evidence; the
official Steam forum request for dedicated servers drew **no dev response**.

**Verdict.** Not a viable plugin — nothing to install via SteamCMD as a server,
no server process, no config / ports / RCON. Park it.

**Revisit if:** Dreamlit ships an actual dedicated server (multiplayer is still
"prototype" and they've said deeper co-op is "something we are looking into," but
there's no dedicated-server commitment).

### Soulmask: Shifting Sands
**Status:** `[ready]` — researched; strong, well-documented fit, and notably
**multi-instance-friendly with a real control channel**. (The plugin is just
**Soulmask**; "Shifting Sands" is a DLC *map*, not a separate server — see below.)

**Install:** SteamCMD, anonymous. **Platform-split app IDs:** **3017310
(Windows)** / **3017300 (Linux)** — separate depots, so the plugin picks by node
OS (the game client `2646460` is never installed on the server). Native Windows
*and* Linux servers. Soulmask 1.0 shipped **Apr 10 2026** (CampFire Studio /
Qooland Games), UE-based. **Heavy: 12 GB+ RAM per server process** (16 GB min).
Up to **50 players** (`-MaxPlayers`).

**"Shifting Sands" is a map, not a build.** Soulmask 1.0 has two maps selected by
the launch `LEVELNAME`: **Cloud Mist Forest** (`Level01_Main`, base) and
**Shifting Sands** (`DLC_Level01_Main`, the Egypt DLC map). Same server binary —
so this catalog entry is really the **Soulmask** plugin with a map dropdown. To
let players travel **between** both maps you run **two instances as a cluster**:
main `-serverid=1 -mainserverport=8781`, child
`-serverid=2 -clientserverconnect=MAIN_IP:8781`, plus `KaiQiKuaFu=1` in
`GameXishu.json` on both. (Character / mask / tech transfers between maps;
buildings and local inventory don't.)

**⚠ `-serverid` is account identity.** Player account data is keyed to the
server's `-serverid` (0 if unset). **Change it after players have created
accounts and they lose those accounts and must restart.** So serverid is a
stable primary-key-style value — `ValidateConfig` / lock-after-first-run, and the
cluster's main / child IDs must be assigned deliberately.

**Config — two layers:**
- **Launch parameters** (in `StartServer.bat` / `WSServer.sh`): `LEVELNAME`
  (map) + `-server -log -forcepassthrough -UTF8Output`, `-SteamServerName`,
  `-MaxPlayers`, `-PSW` (join pw), `-adminpsw` (GM pw), `-MULTIHOME`, `-Port`,
  `-QueryPort`, `-EchoPort`, `-pve` / `-pvp` (mode), `-serverid`, and the cluster
  flags. LO-shaped → `BuildLaunchArguments`. (Some of these also live in
  `Engine.ini` `[Dedicated.Settings]` if you prefer the file.)
- **`GameXishu.json`** at `WS\Saved\GameplaySettings\GameXishu.json` — JSON, the
  **100+ gameplay tunables** (XP / drop / survival / combat / tribe / building /
  invasion multipliers + the `KaiQiKuaFu` cluster toggle). **Only appears after
  first run** → start once, clean-stop, then edit (Factorio-pattern JSON file
  editor).

**Ports (4, each instance unique):** game **UDP 8777** (`-Port`), query **UDP
27015** (`-QueryPort`), **telnet / echo 18888** (`-EchoPort`, TCP), and **RCON
19000** (TCP, admin). Allocator-managed. Restrict 18888 / 19000 to local /
node-only (public exposure = anyone with the password gets console access). The
direct-connect invite code is written to `WS\Saved\Logs\WS.log`.

**Control + graceful shutdown — good, and save-corruption-sensitive.** Soulmask
**rolls back / corrupts saves on a hard kill** — never task-kill it. Clean
shutdown paths, all of which save first: **Ctrl+C in the console** (→
`GSM.CtrlCSender` on Windows / **SIGINT** on Linux — the primary path), the
in-game admin **`gm exit`**, or **RCON / telnet `shutdown 300`** (countdown).
So unlike LO / Conan this UE server has explicit safe-stop channels — enabling
`-EchoPort` / RCON also buys an announced remote shutdown. Admin auth is
`-adminpsw` then `gm key <pw>` in the in-game (~) console; **RCON exists** (port
19000) so PowerGSM's RconClient is usable here (rare among the survival set).

**Saves:** **`world.db`** is the entire world (under `WS\Saved`) — the one file to
back up before updates / config edits / cluster migration. Managed-dir + backup
candidate.

**Multi-instance — officially supported.** "A single server can run multiple game
instances"; each needs its own launch script with **unique ports + `-serverid`**.
So this slots straight into PowerGSM's multi-instance model (no shared-Saved
caveat like the UE games), and the cluster feature is just two instances wired
together.

**Fit verdict.** One of the cleaner UE fits — native cross-platform, JSON gameplay
editor, a real RCON / telnet control channel with safe shutdown, and first-class
multi-instance. Main care points: the platform-split app IDs, `-serverid`
immutability, and never hard-killing it.

**Suggested slicing:** (1) install (OS-correct app ID) + launch (map / name /
passwords / ports / mode / `-serverid`) + Ctrl+C clean stop; (2) `GameXishu.json`
editor (post-first-run); (3) `world.db` managed dir + backup; (4) RCON / telnet
client (announced `shutdown`, player / admin commands); (5) [later] cluster
(main / child + `KaiQiKuaFu`) for cross-map travel.

### Aloft
**Status:** `[ready]` — researched; a real dedicated server *does* exist (unlike
Towers), but with a non-standard install model that makes it lower-priority.

**A dedicated server exists, but there's no anonymous depot.** Astrolabe
Interactive / Yogscast Games, **Unity**, EA since 2024, up to **8 players**. The
catch: **the server files ARE the game files** — there's no separate anonymous
SteamCMD server app, so you either copy them from a game install or pull them via
**authed SteamCMD with an account that owns the game** (AppID **1660080**, the
game itself; community-confirmed you must log in, not anonymous). Same
licensing-flavoured friction as Stardew, though far less hacky — this is a proper
headless dedicated server the dev ships, just gated behind ownership. The server
can run alongside the game on the same account.

**Windows / Unity; Linux via Wine.** No native Linux server; community runs it
under **Wine** on Ubuntu (same Proton / Wine-on-Linux story as Enshrouded).
Trivial on a Windows node; Linux needs the Wine layer PowerGSM doesn't have yet.

**Launched via PowerShell scripts the dev ships** in the game dir:
`AloftServerNoGuiCreate.ps1` (create a world) and `AloftServerNoGuiLoad.ps1`
(load / run it). So there's a **two-step lifecycle** — *create* a world once (the
process builds the map and self-exits), then *load* it to run the server. The
create step is a clean **`IFileGenerationProvider`** ("generate world") candidate,
distinct from the run step.

**Config = launch args, but with an unusual `key#value#` syntax** (hash-delimited,
not `-key value`). Run: `-batchmode -nographics -server load#MAP# servername#NAME#
log#ERROR# isvisible#true|false# privateislands#true|false# playercount#8#
serverport#0# admin#<steamid># admin#<steamid2>#`. Create: `-server create#MAP#
islandcount#normal# corruptioncount#normal# creative#false# log#ERROR#`. Notable:
**`isvisible#`** toggles lobby-browser visibility vs code-only; **`admin#`** is a
*repeatable list* of admin Steam IDs; map names take no spaces.
`BuildLaunchArguments` just has to emit the `#`-delimited form.

**Ports:** game **15636**, Steam query **15637** (defaults; `serverport#0#` = use
default). Allocator-managed.

**Connection:** a **Friend / room code** (written to **`ServerRoomCode.txt`** and
the console) — plus optional lobby-browser visibility via `isvisible#true#`. No
DNS hostnames.

**Control / RCON:** **no RCON.** Admins (the `admin#` Steam IDs) manage some
settings live from **in-game**; otherwise it's launch-line config. Player state
would be **log-parse only**.

**Graceful shutdown:** Unity headless (`-batchmode -nographics`), so **Ctrl+C /
SIGINT is the likely clean stop** (as with Valheim) — verify; it maps to
`GSM.CtrlCSender` / SIGINT.

**Saves:** in the **user profile**, not the install — `AppData\LocalLow\Astrolabe
Interactive\Aloft\Data01\Saves\` (Windows) / the equivalent under the Wine prefix
on Linux. Managed-dir + backup candidate, but profile-relative (like Stardew).

**Fit verdict.** Feasible and genuinely a dedicated server (so not blocked), but
the **owning-account install + PowerShell-script create / load lifecycle + hash-arg
syntax + Wine-on-Linux** make it a notch more bespoke than the anonymous-SteamCMD
games. Best treated as lower-priority than the clean `[ready]` set.

**Suggested slicing:** (1) install (authed / owning account, or game-files copy) +
the `create#` world-gen step (`IFileGenerationProvider`); (2) `load#` launch +
name / ports / visibility / playercount / admins + Ctrl+C stop; (3) saves managed
dir + backup; (4) [later] Wine for Linux nodes; (5) [later] log parse rules.

### Sunkenland
**Status:** `[ready]` — researched; feasible and well-documented, but a notch
bespoke (authed install + bring-your-own-world + a connection model unlike
anything else here).

**Install:** SteamCMD **AppID 2667530** ("Sunkenland Dedicated Server"), but
**NOT anonymous** — you must **log in with an account that owns the game** (Steam
Guard prompts first time), same ownership gate as Aloft / Stardew. Vector3 Studio,
**Unity**. **Windows-only** (no Linux build; community runs it under **Wine +
Xvfb** via the `melle2/sunkenland-ds` Docker image). **15-player hard cap.**
**Install quirk:** run the `app_update` a **second time** after the first — some
files only download on the subsequent pass, otherwise you hit `SessionInfo:
IsValid` join failures.

**⚠ Bring-your-own-world.** The server **cannot generate a world** — you create
one in the **Sunkenland game client**, then copy its world folder
(`AppData\LocalLow\Vector3 Studio\Sunkenland\Worlds\...`) onto the server and
point `-worldGuid` at its GUID. So the plugin needs a **world-import** step
(operator supplies a world folder); there's no headless world-gen like Aloft's
`create#`. (`IManagedDirectoriesProvider` + an import flow.)

**Config = launch args** in `start_headless_server.bat`:
`Sunkenland-DedicatedServer -nographics -batchmode -worldGuid "<GUID>" -region
"us" -maxPlayerCapacity "15" -port 29000 -queryport 29002` (`-nographics
-batchmode` must stay). A join **password** and a **session-invisible** toggle
also exist (exposed as `GAME_PASSWORD` / `GAME_SESSION_INVISIBLE` in the Docker
image). LO-shaped → `BuildLaunchArguments`.

**⚠ Connection model — Steam Datagram Relay, ServerID + region, no IP / port.**
Players don't use an IP — Sunkenland routes through **SDR**, so there's nothing to
port-forward. They join with a **ServerID (a GUID)** + the matching **region** (+
password). The catch: the **ServerID regenerates on every restart**, so it can't
be bookmarked. On boot the console prints **`Server Start Complete, Ready for
Clients to Join. ServerID is '<GUID>'`** — a **two-for-one**: a clean
**`IReadySignalProvider`** log line *and* the (volatile) join token the plugin
must **re-parse and surface prominently on every start**. The `-port` /
`-queryport` are just local sockets (only matter for running multiple instances on
one box).

**Admin / control — no RCON.** Admin and ban lists are two text files in the
**world folder** — **`AdminSteamIDs.txt`** and **`BanSteamIDs.txt`** (one Steam ID
per line, no commas / comments; restart to apply). Simple managed-file /
list-editor candidates. Player state would be **log-parse only**.

**Graceful shutdown:** Unity headless, so **Ctrl+C / SIGINT is the likely clean
stop** (→ `GSM.CtrlCSender` / SIGINT) — and it matters: **saves can corrupt if the
server is killed mid-save**, so don't fast-force-kill, and lean on backups.

**Saves:** the world folder (managed-dir + backup candidate; corruptible on a bad
crash → back up). Multi-instance is fine with distinct `-port` / `-queryport` +
world.

**Fit verdict.** Feasible and mostly log / args-driven, with a genuinely useful
ready-signal — but the **authed install, bring-your-own-world, and
regenerates-every-restart ServerID** make it more involved than the clean set.
The ServerID surfacing is the interesting bit: PowerGSM would parse it from the
boot log and show it as the join code each launch.

**Suggested slicing:** (1) install (authed account, double `app_update`) + world
import + launch (worldGuid / region / cap / password); (2) parse "Server Start
Complete … ServerID" → ready signal + surface the ServerID; (3)
`AdminSteamIDs.txt` / `BanSteamIDs.txt` list editors; (4) world managed dir +
backup + Ctrl+C clean stop; (5) [later] Wine + Xvfb for Linux nodes.

### Starbound
**Status:** `[ready]` — researched; a clean, mature fit (native cross-platform,
JSON config, **built-in RCON**). One friction: an ownership-gated install.

**Install:** SteamCMD **AppID 211820** — but that's the **full game**, not a
separate server depot. The dedicated-server binary is **bundled inside the game
install** (`win64/` and `linux/` subfolders), so there's **no anonymous server
package** — you must **log in with an account that owns Starbound** (Steam Guard
first time), same gate as Aloft / Sunkenland. (The running server doesn't need to
stay logged in; the account is only for install / update.) Chucklefish, mature
(2016). **Native 64-bit Windows AND Linux** binaries — no Wine, unlike the other
authed-install games. Linux needs `libvorbisfile3` (+ SteamCMD's 32-bit libs).

**Executable:** `starbound_server.exe` (Windows `win64/`) / `starbound_server`
(Linux) — headless console.

**Config:** **`starbound_server.config`** in the **`storage/`** dir — **JSON**, a
clean file-editor candidate (JsonNode preserve-unknown, Factorio-style). Caveat:
on a **syntax error the server renames it `.old` and regenerates defaults**, so
the editor must always emit valid JSON (round-trip safe). Key fields: serverName,
maxPlayers, serverPassword, the `serverUsers` / admin block
(`allowAdminCommands`, anonymous-connection toggles), and the RCON trio
(`runRconServer`, `rconServerPort`, `rconServerPassword`).

**Ports:** game **21025 (TCP + UDP)** (`gameServerPort`); **RCON** on a separate
TCP port (`rconServerPort`, e.g. 21026 — must differ from the game port).
Allocator reserves both.

**Control — built-in RCON (Source-style).** Set `runRconServer:true` +
`rconServerPort` + `rconServerPassword` and the server speaks **Source-RCON**
(srcdsrcon / PuTTY-raw / community tools all connect), with the main admin
commands (ban, etc.) available over it. So **PowerGSM's `RconClient` is directly
usable here** — one of the few on the list with native RCON (alongside Soulmask).
Admins can also be set in-game via the `serverUsers` block.

**Graceful shutdown:** custom C++ engine (not UE) — **Ctrl+C / SIGINT is a clean
stop** (the reference Docker allows a 2 min grace to flush), mapping to
`GSM.CtrlCSender` / SIGINT; or an RCON shutdown. No UE force-kill pain.

**Saves / mods:** the whole **`storage/`** dir is the state (universe + players +
config) — managed-dir + backup candidate. **Steam Workshop mods** are supported
(`+workshop_download_item 211820 <id>`; `allowAssetsMismatch` governs client /
server mismatch), so there's an optional `IModManager`-style angle like Factorio.
The enhanced **OpenStarbound** fork is a popular drop-in.

**Multi-instance:** per install with distinct `gameServerPort` / `rconServerPort`
+ a separate `storage/` dir.

**Fit verdict.** Essentially "Factorio-shaped, but with native RCON and an
ownership-gated install." Clean: native cross-platform, JSON editor, real control
channel, clean Ctrl+C stop. The only real friction is the authed (owning-account)
install since the server ships inside the game.

**Suggested slicing:** (1) install (authed / owning account) + launch + core
config (name / maxPlayers / password); (2) `starbound_server.config` JSON editor
incl. RCON + serverUsers; (3) RCON client (admin commands + announced shutdown);
(4) `storage/` managed dir + backup; (5) [later] Workshop mod management.

### Rust
**Status:** `[ready]` — researched; clearly feasible and very well-documented, but
the **highest-effort** plugin on the list (WebSocket RCON + a wipe manager +
convar / cfg editor + Oxide / Carbon + a heavy resource footprint).

**Install:** SteamCMD **AppID 258550** (Rust Dedicated Server), **anonymous** —
no ownership needed (the install is separate from the client app 252490; players
still need to own Rust). **~8 GB** download, native **Windows / Linux / macOS**.
**Very heavy:** Facepunch's floor is **12 GB RAM**; a 50-slot modded server runs
**18-24 GB** at peak, SSD / NVMe strongly preferred (frequent large saves).
Updated **frequently** (monthly, with the client patch) — must re-run `app_update`
on patch day or clients can't connect (protocol mismatch). `RustDedicated`
launched with `-batchmode`.

**Config — convars, two layers** (Source-cvar-style, not JSON / INI):
- **Launch `+convars`** for essentials: `-batchmode +server.ip +server.port
  (28015) +server.identity "<id>" +server.seed +server.worldsize (3000-4500)
  +server.maxplayers +server.tickrate +server.saveinterval +rcon.ip +rcon.port
  (28016) +rcon.password +rcon.web 1 -logfile`.
- **`server.cfg`** in `server/<identity>/cfg/` — the bulk of settings, read at
  startup and **takes priority over the command line**; **the server never
  rewrites it** (manual edit), and `server.writecfg` persists runtime changes back
  to it. Plus **`users.cfg`** (ownerid / moderatorid admins). The
  `server/<identity>/cfg/` folder only appears **after first boot** (start → stop
  → edit). Some convars are **runtime-only** (set live via RCON). So the "config
  editor" here is a **convar key / value editor**, not a structured-file editor.

**`server.identity` is the instance key** — it names the `server/<identity>/`
data folder (world, saves, cfg). Distinct identity + distinct ports = clean
multi-instance.

**Ports (3, all distinct):** game **UDP 28015** (`server.port`), **RCON TCP
28016** (`rcon.port`), query **UDP 28017** (`server.queryport`; defaults to
1+greatest of server / rcon port if unset, and can't equal the game port).
Allocator reserves the block.

**⚠ Control — WebSocket RCON, NOT Source RCON.** `rcon.web 1` enables Rust's
**WebSocket** RCON (Facepunch WebRCON / RustAdmin / rcon.io). This is the one real
**infra requirement**: PowerGSM's existing `RconClient` is (almost certainly)
Source-RCON TCP, which **won't talk to Rust** — the plugin needs a **WebSocket
RCON client** (ws:// + JSON `{Identifier,Message,Type}` framing). RCON is central
here: player / ban management, `save`, announcements, and scheduled restarts /
wipe-day map regen (the RustAdmin model). Admins are `ownerid <steamid64>`
(authlevel 2).

**⚠ Wipes — a first-class operation unique to Rust.** Facepunch forces a wipe on
the **first Thursday of every month** (the major patch changes the save format).
Owners add their own cadence (weekly / biweekly / monthly). A wipe = **delete the
map save** (and optionally blueprint / player data). So a good Rust plugin wants a
**wipe action** (targeted save-file deletion) + an **automation rule** around the
monthly forced wipe (update server + Oxide / Carbon, then wipe) — a natural fit
for PowerGSM's automation engine, and nothing else on the list needs it.

**Mods:** **Oxide / uMod** or **Carbon** frameworks (installed over the server
files; plugins for kits / shops / clans), updated alongside Rust on patch day —
an optional but near-universal mod-management angle.

**Graceful shutdown:** Unity engine — **RCON `quit`** (after a `save`) is the
clean stop, or **Ctrl+C / SIGINT** (→ `GSM.CtrlCSender` / SIGINT). Linux console
is non-interactive, so RCON (or screen / systemd) is the normal control path.

**Saves:** under `server/<identity>/` (map + players + blueprints) — frequent
large saves; managed-dir + backup candidate **and** the wipe target.

**Fit verdict.** Strong but the biggest build on the list. The three things that
make it more than a config-and-launch plugin: a **WebSocket RCON client**, a
**wipe manager + monthly-forced-wipe automation**, and the **convar / `server.cfg`
editor**. Plus a heavy resource footprint to surface to operators. Everything is
extremely well-documented, so it's `[ready]` — just scope it as a large plugin.

**Suggested slicing:** (1) install (anonymous, ~8 GB) + launch (`+server.*` /
`+rcon.*` essentials, identity, 3-port block); (2) **WebSocket RCON client**
(connect / commands / `save` / `quit`); (3) `server.cfg` + `users.cfg` convar
editor (post-first-boot); (4) saves managed dir + backup; (5) **wipe action** +
forced-wipe automation rule (update + wipe); (6) [later] Oxide / Carbon + plugin
management.

### American Truck Simulator
**Status:** `[ready]` — researched; feasible and lightweight, with one genuinely
unusual flow (config is *exported from a game client*, not generated by the
server) and an SCS-specific SII config format.

**Install:** SteamCMD **AppID 2239530** (ATS Dedicated Server), **anonymous**.
Native **Windows and Linux** (`amtrucks_server.exe` / `bin/linux_x64` +
`server_launch.sh`). SCS Software (Prism3D engine). Tiny next to the survival
games. **8-player hard cap** (raising `max_players` past 8 does nothing). The
same model applies to **ETS2** (separate AppID 1948160) — an ATS plugin
generalises there cheaply.
> Linux gotcha: `SteamAPI_Init() failed` unless `steamclient.so` is symlinked
> into `~/.steam/sdk64/` — the shipped `server_launch.sh` does this for you.

**⚠ The unusual part — `server_packages` come from the game client.** The server
needs **three files** in its home dir: `server_config.sii` (settings, auto-created
on first launch), plus **`server_packages.sii`** (map / DLC / mod config) and
**`server_packages.dat`** (binary map data). The server **cannot generate the
last two** — they're exported from a **running copy of the actual game** via the
console command **`export_server_packages`** (after enabling the console with
`uset g_console "1"` in the game's `config.cfg`). Without them the server errors
"Server packages file not found" and won't start. The files are **not tied to the
Steam account** (portable), so the server box doesn't need the game — but someone
must export them from a client. So the plugin needs a **packages-import step**
(operator supplies `server_packages.sii` + `.dat`), conceptually like Sunkenland's
bring-your-own-world. Re-export after big game updates (map-data version).

**Config — `server_config.sii`** in SCS's **SII** format (a `SiiNunit { ... }`
struct, **not INI / JSON** — a bespoke SII editor, which would also serve ETS2).
Fields: `lobby_name`, `description`, `welcome_message`, `password`, `max_players`
(≤8), traffic / damage / collision toggles, `connection_dedicated_port` (27015),
`query_dedicated_port` (27016), **`server_logon_token`** (GSLT), and
**`moderator_list`** (count + `moderator_list[N]: <Steam64ID>`).

**`server_logon_token` (GSLT):** needed to appear on the **public** server
listing — created at Steam's Game Server Account Management using the **base
game's app ID** (not the dedicated server's). An operator step; optional if you
only want direct session-search-id joins.

**Ports:** TCP **+** UDP **27015** (`connection_dedicated_port`) and **27016**
(`query_dedicated_port`); the `*_virtual_port` values (100 / 101) are internal,
not real ports. Allocator reserves the 27015-27016 block (both protocols). **No
NAT punching** — public IP / forwarding is needed for the browser listing, but a
**session search id** (printed on start, e.g. `…/101`) lets players direct-connect
through the Convoys menu even behind NAT. Surface that search id from the log
(stable per config, unlike Sunkenland's regenerating ID).

**Control / RCON:** **no RCON**, no remote console. Admins are the
`moderator_list` Steam IDs; player state would be **log-parse only**
(`server.log.txt`).

**Graceful shutdown / saves:** a **lightweight session server** — no growing
world save (the home dir's config + packages is the whole state), so shutdown
isn't save-critical. Console app on a custom engine; **Ctrl+C / SIGINT is the
likely clean stop** (verify) → `GSM.CtrlCSender` / SIGINT.

**Multi-instance:** supported via **`-homedir <path>`** (each instance its own
home dir with its own three files) — or renamed `-server` / `-server_cfg` files —
plus distinct ports and a distinct connection token. `-homedir` fits PowerGSM's
per-instance dir model cleanly.

**Fit verdict.** Easy install and lightweight to run; the two real build items are
the **`server_packages` import step** (can't be generated server-side) and the
**SII `server_config.sii` editor** (reusable for ETS2). GSLT creation is an
operator step to document. No RCON, no heavy saves, friendly multi-instance.

**Suggested slicing:** (1) install + `-homedir` launch + packages-import (drop
`server_packages.sii` + `.dat`); (2) `server_config.sii` SII editor (name / ports
/ password / moderators / token); (3) session-search-id surfaced from the log;
(4) [later] ETS2 via the same plugin shape; (5) [later] log parse rules.

### Assetto Corsa
**Status:** `[ready]` — researched; a clean dual-INI fit, with content / car-ID
coupling as the fiddly part. (This is **base AC** / `acServer`; ACC is a separate
game with its own server — out of scope here.)

**Install:** SteamCMD — the **Assetto Corsa Dedicated Server** tool (AppID
**302550**; base game is 244210), **anonymous**. **Windows-native**
(`acServer.exe`); on **Linux** run it under **Wine**, or switch to the community
**AssettoServer** (a native reimplementation that also adds plugins / AI traffic /
more admin). Kunos custom engine. Lightweight.

**Config — two coupled INI files in `cfg/`** (plus `blacklist.txt` of banned
Steam GUIDs):
- **`server_cfg.ini`** — `[SERVER]` (NAME, PASSWORD, **ADMIN_PASSWORD**
  (mandatory), TRACK, CONFIG_TRACK, **CARS=**, **MAX_CLIENTS**,
  REGISTER_TO_LOBBY, UDP_PORT / TCP_PORT / HTTP_PORT) + per-session sections
  `[PRACTICE]` / `[QUALIFY]` / `[RACE]` (laps / time), `[WEATHER_N]`,
  `[DYNAMIC_TRACK]`, assists. Clean `IInstanceFileEditorProvider` target.
- **`entry_list.ini`** — the car **slots**: `[CAR_0]`, `[CAR_1]`… each with
  `MODEL` (car folder name), `SKIN`, optional `GUID` / `DRIVERNAME` / `TEAM`,
  `BALLAST`, `RESTRICTOR`, `SPECTATOR`.

**⚠ The fiddly part — cross-file + content-ID coupling.** Two hard validation
rules the editor must enforce or the server won't start:
1. **entry slots ≥ `MAX_CLIENTS`** — fewer `[CAR_N]` blocks than MAX_CLIENTS = the
   server fails to start.
2. **every entry `MODEL` must also be in `CARS=`** in server_cfg.ini.
And `TRACK` / `CARS` / `MODEL` are **content folder names** (`ks_toyota_gt86`,
track `ks_black_cat_county`…) that must match installed content under
`content/cars` & `content/tracks`; connecting players must **own the same DLC /
have the same mods** (checksum-matched) or hit "unavailable content". So a good
editor enumerates installed content to populate car / track choices and validates
the slot / CARS / MAX_CLIENTS triangle — that content-ID handling is the real
work here, not the INI writing.

**Ports:** game **9600 (TCP + UDP)** (`TCP_PORT` / `UDP_PORT`) and **HTTP 8081**
(`HTTP_PORT`, lobby registration). Allocator reserves the set; multiple instances
take distinct sets (9601 / 8082…). `REGISTER_TO_LOBBY=1` lists it publicly.

**Control — in-game admin commands, no RCON.** `ADMIN_PASSWORD` is mandatory;
admins authenticate **in-game** with `/admin <pw>` then `/kick`, `/ban_id`,
`/next_session`, `/restart_session`, `/ballast <car> <kg>`, `/restrictor`,
`/client_list`, etc. There's **no remote console socket** on the official server
(the community AssettoServer adds more). Player state would be **log-parse only**.

**Graceful shutdown / saves:** a **lightweight session server** — no persistent
world (just optional results JSON in `out/`), so shutdown isn't save-critical.
Console app on a custom engine; **Ctrl+C / SIGINT is the likely clean stop**
(verify) → `GSM.CtrlCSender` / SIGINT. "Saves" to manage = the `cfg/` files +
`content/`; no growing world.

**Multi-instance:** distinct port sets + a separate `cfg/` (or separate server
dir) per instance. Feasible (the community ACServerManager runs several).

**Fit verdict.** A nice INI-editor plugin on the surface, but the substance is the
**dual-file validation + content-ID awareness** (enumerate installed cars /
tracks, enforce slots / CARS / MAX_CLIENTS). No RCON, no heavy saves.
Windows-native; Linux means Wine or pivoting to AssettoServer (a bigger, different
target).

**Suggested slicing:** (1) install + launch + `server_cfg.ini` core
(name / track / passwords / ports / MAX_CLIENTS); (2) `entry_list.ini` slot editor
+ the slots / CARS / MAX_CLIENTS validation; (3) content enumeration (cars /
tracks) to drive dropdowns; (4) `blacklist.txt` + session / weather sections;
(5) [later] log parse rules; (6) [later] Linux via Wine or an AssettoServer
variant.

### Mindustry
**Status:** `[ready]` — researched; a clean, lightweight fit and a refreshingly
different shape from the SteamCMD games (Java jar + JRE prereq + command-driven
control).

**Install — NOT SteamCMD.** Download **`server-release.jar`** from the official
**GitHub releases** (Anuken/Mindustry) or itch.io — an HTTP download, like
Factorio, not a Steam depot. Open-source. **Cross-platform** (any OS with Java —
Windows / Linux / macOS, no Wine, no platform split). Lightweight (~512 MB–1 GB
via JVM `-Xms` / `-Xmx`).
> **JRE prerequisite** (`IPrerequisiteProvider`, first real use in this set): the
> headless server is pure JVM and needs a current **OpenJDK (Java 11+)** present.

**Launch + config — command-driven, no config file.** `java -jar
server-release.jar` boots to a **console** ("Server loaded. Type 'help'."); it
does **not** auto-host. Settings are set with the **`config`** command
(`config port <n>`, `config name <NAME>`…) and persisted to a binary
`config/settings.bin` — there's **no text config file to edit**. You can
front-load a **comma-separated command sequence** on the launch line, e.g. `… -jar
server-release.jar config port {PORT},config name {NAME},host {MAP}`. So
`BuildLaunchArguments` emits a *command list*, and the "config editor" is really a
set of `config` commands, not a file.

**Maps:** `.msav` files in **`config/maps/`** (custom maps built in the game
editor, exported, dropped in); start one with **`host <mapname> [mode]`** (modes:
survival / sandbox / attack / pvp). A lightweight managed-dir + map-import angle.

**Ports:** **6567 TCP + UDP** (single port, both protocols; `config port`).
Allocator reserves it; multiple instances take distinct ports + separate
`config/` dirs.

**Control — console commands + a `socketInput` TCP socket (a real hook).**
Mindustry exposes **`socketInput`** — "allows a local application to control this
server through a local TCP socket" — i.e. an RCON-like local command channel
(community libs like `pydustry` use it). Either that or plain **stdin** drives the
full command set: `host` / `stop` / `exit`, `say`, `kick`, `ban` / `unban`,
`admin add|remove <uuid>`, and **`players` / `info` / `search`** for structured
player data. So no log-parsing needed for players, and PowerGSM gets a genuine
control channel (drive stdin, which it already pipes, or connect socketInput).

**Graceful shutdown:** the **`exit`** (or `stop`) command, or **Ctrl+C / SIGINT**
(`kill -15`) — clean JVM shutdown → `GSM.CtrlCSender` / SIGINT, or just send `exit`
over stdin. No UE force-kill pain, no save-corruption worries (game state is
transient unless `save`d).

**Saves / mods:** the **`config/`** dir (settings.bin, `maps/`, `saves/`,
`mods/`) is the whole state — managed-dir + backup candidate. Java / JS **mods**
in `config/mods/` give an optional mod-management angle.

**Version coupling:** server build must match the client build (filename isn't
reliable — the server prints its build). `IVersionAwarePlugin` via GitHub
releases; update = swap the jar.

**Fit verdict.** Easy and lightweight, and a good showcase for the side-interfaces
the SteamCMD games don't exercise: **`IPrerequisiteProvider`** (JRE) + a
non-Steam HTTP-download install (Factorio-style) + a command / socket control
model. The only "new" bits are the JRE prereq and driving config via `config`
commands rather than a file.

**Suggested slicing:** (1) JRE prereq + jar download (GitHub release) + launch
(`config port` / `name`, `host <map> <mode>`); (2) `stop` / `exit` clean shutdown
+ stdin command drive; (3) `players` / `info` player surface (+ optional
socketInput); (4) `config/maps` managed dir + map import; (5) `IVersionAwarePlugin`
from GitHub releases; (6) [later] mods.

### OpenTTD
**Status:** `[ready]` — researched; a clean, lightweight fit with the **richest
structured remote-control surface** of the non-Steam set (the admin network).

**Install — NOT SteamCMD; free / open-source.** Download the OpenTTD binary from
**openttd.org** (HTTP download, like Mindustry / Factorio; also on Steam / GOG as
the game, but the binary is its own server). **No separate server build** — the
same `openttd` executable runs headless with **`-D`**. **Cross-platform native**
(Windows / Linux / macOS, no Wine), lightweight (~1 GB RAM). Open-source.

**Launch:** `openttd -D` (capital D = dedicated) `[-g <save>]` `[-c <cfg>]`; `-f`
forks to background and logs to `openttd.log`. The `-D` window has a console;
remote control is via rcon / the admin port (below).

**Config — `openttd.cfg`** (INI-style, sectioned; generated on first run). The
`[network]` section is a clean **`IInstanceFileEditorProvider`** target:
`server_name`, `max_clients`, `server_password`, **`rcon_password`**,
**`admin_password`**, `server_port` (3979), **`server_admin_port`** (3977),
`server_bind_addresses`, `server_game_type` / advertise, `autoclean_companies`,
`pause_on_join`.

**⭐ Marquee — the admin network (TCP 3977).** OpenTTD ships a **documented binary
admin protocol** (`docs/admin_network.md`): connect TCP `server_admin_port`,
authenticate (key-exchange `ADMIN_JOIN_SECURE` with `admin_password`; plaintext
join is disabled by default) within 10 s, then **execute rcon commands AND receive
structured events** — `CLIENT_JOIN` / `CLIENT_INFO`, `COMPANY_NEW` / `INFO`,
`CHAT`, `CONSOLE`, `NEWGAME`, `SHUTDOWN` — with subscribe / poll frequencies.
Admin apps **stay connected across new-game / load** (unlike normal clients). So
this gives PowerGSM **rcon + structured player (client) join / leave + chat with
no log parsing** — the richest telemetry surface here after Palworld's REST. It's
a **bespoke length-prefixed binary protocol** (moderate build), but well-specified
with reference libs in many languages (`libottdadmin2` Py, `joan` Java, `gopenttd`
Go, `openttd-js-api` JS). (A simpler path exists too: `rcon_pw <pw>` then
`rcon <pw> <cmd>` from a client.)

**Ports:** game **3979 (TCP + UDP)** (`server_port`); **admin 3977 (TCP)**
(`server_admin_port`); public-list / coordinator **3978 (UDP)**. Allocator
reserves the set. **Modern note:** OpenTTD's **Game Coordinator** relays
connections, so **port-forwarding is often no longer needed** for public play —
worth modelling (public via coordinator vs direct IP:port).

**Control commands:** `kick`, `ban`, `pause` / `unpause`, `save <name>`, `load`,
`newgame`, `clients` / `companies` (list), `move`, `reset_company`, `say`,
`content` (NewGRF download), `quit`. Admin auth via `admin_password`; in-game
admin via `rcon_password`.

**Graceful shutdown:** `save` then `quit` (via the admin port / console), or
**Ctrl+C / SIGINT** → `GSM.CtrlCSender` / SIGINT (the admin port also emits a
`SHUTDOWN` event). It autosaves periodically; force-kill loses progress since the
last (auto)save. Custom engine — no UE force-kill pain.

**Saves / content:** `.sav` files in the personal dir (`~/.local/share/openttd/`
/ `Documents\OpenTTD\`), with `save/autosave/`; managed-dir + backup candidate,
`-g <save>` to load. **NewGRF** content (graphics / AI / game scripts) must match
between server and clients (auto-fetched via the in-game **BaNaNaS** content
service) — an optional content / mod-management angle.

**Multi-instance:** distinct `-c <cfg>` + save dir + `server_port` / `admin_port`
per instance.

**Fit verdict.** Easy, free, cross-platform, lightweight — and the admin network
makes it the best structured-telemetry target of the non-Steam games. The one
real build item is the **admin-port protocol client** (bespoke binary TCP, but
well-documented with references); everything else is a clean INI editor + launch
flags.

**Suggested slicing:** (1) binary download + launch (`-D -g -c`) + `openttd.cfg`
`[network]` editor (name / max_clients / passwords / ports); (2) **admin-port
client** — auth + rcon + client-join / leave + chat events (player surface, no
log parsing); (3) saves managed dir + backup + `save` / `quit` clean shutdown;
(4) [later] NewGRF / BaNaNaS content matching.

### Space Engineers
**Status:** `[ready]` — researched; a strong but heavyweight, Windows-centric fit
with an XML config and a real HTTP remote API.

**Install:** SteamCMD **AppID 298740** ("Space Engineers Dedicated Server"),
**anonymous**. **Windows-only** binary
(`DedicatedServer64\SpaceEngineersDedicated.exe`); needs **.NET Framework 4**. On
**Linux** it runs under **Wine** (community Docker images +
`winetricks dotnet40`). Keen Software House (VRAGE engine). **Heavy: ~6 GB RAM**,
~7 GB disk.

**Launch — bypass the GUI configurator.** Normally SE is set up through the
`SpaceEngineersDedicated.exe` GUI (its "Add new instance" path installs *Windows
services* — which PowerGSM should skip, since it manages the process itself).
Headless: **`SpaceEngineersDedicated.exe -console -path <instanceDir>
[-ignorelastsession]`** runs as a plain console process; `-path` sets the
per-instance **config + saves** directory (instead of `%AppData%\Roaming\
SpaceEngineersDedicated`), and `-maxPlayers` / `-ip` / `-port` override the cfg.

**Config — `SpaceEngineers-Dedicated.cfg` (XML).** In the `-path` dir. An **XML
editor** (not INI / JSON; `XDocument` preserve-unknown). Elements: `<ServerName>`,
`<WorldName>`, `<ServerPort>` (27016), `<SteamPort>`, **`<RemoteApiPort>`** /
**`<RemoteSecurityKey>`**, `<Administrators>` (Steam IDs), `<Banned>`, `<Mods>`
(Workshop IDs), `<LoadWorld>` (path to the world to load), `<IP>`, plus a large
`<SessionSettings>` block (GameMode, MaxPlayers, AutoSaveInMinutes,
InventorySizeMultiplier, AssemblerSpeedMultiplier…).

**Control — the VRage Remote API (HTTP, key-authed).** Set `<RemoteApiPort>` +
`<RemoteSecurityKey>` and the server exposes an **HTTP remote-management API**
(there's even a `VRageRemoteClient.exe` GUI for it) — players / kick / ban / save /
world management, HMAC-signed with the security key. So, like Palworld, PowerGSM
drives a small **HTTP client** rather than RCON (none exists), and gets player
data without log-parsing. Admins also via the `<Administrators>` list.

**Ports:** game **UDP 27016** (`<ServerPort>`) + a **Steam port** (`<SteamPort>`)
+ the **Remote API port** (`<RemoteApiPort>`, TCP / HTTP). Each instance needs a
unique set; allocator-managed.

**Graceful shutdown — fits PowerGSM's existing path.** `taskkill /IM
SpaceEngineersDedicated.exe` **without** `/f` "stops the server correctly, saving
the world"; adding `/f` kills without saving. So PowerGSM's current **`taskkill`
(no `/F`) graceful stop is exactly right here** (SE handles it as save-and-exit) —
better than the UE games. It autosaves on `AutoSaveInMinutes`; on Linux send the
Wine process SIGTERM. (The Remote API can also save / stop.)

**Worlds / saves:** per-world folders under `<path>\Saves\<WorldName>\`
(`Sandbox.sbc` + data). The server can **create a fresh world from a scenario**
(New-game) *or* you copy one in from a client's `AppData\Roaming\SpaceEngineers\
Saves`. `<LoadWorld>` selects it. Managed-dir + backup candidate.

**Mods:** Steam **Workshop** mods via the `<Mods>` list (manifest
`appworkshop_244850.acf`) — an optional mod-management angle.

**Multi-instance:** `-path <distinct dir>` + distinct ports per instance (the
foreground / `-console` route, not the GUI's Windows-service mechanism).

**Fit verdict.** Solid on a Windows node — XML config editor + a real HTTP control
API + a graceful stop that matches PowerGSM's existing `taskkill`-no-`/F` path.
The caveats are weight (~6 GB) and Windows-only (Linux = Wine + .NET 4). The two
build items are the **XML cfg editor** and the **VRage Remote API HTTP client**.

**Suggested slicing:** (1) install + `-console -path` launch + `SpaceEngineers-
Dedicated.cfg` XML core (name / world / ports / MaxPlayers); (2) world managed dir
+ backup (+ create-from-scenario / import); (3) VRage Remote API HTTP client
(players / kick / ban / save); (4) `taskkill`-no-`/F` graceful stop wiring;
(5) [later] Workshop mods; (6) [later] Wine for Linux nodes.

### Team Fortress Classic
**Status:** `[ready]` — researched; a clean, mature fit, and the highest-leverage
one: it's really a **template for the whole GoldSrc / HLDS family** (CS 1.6, Day
of Defeat, HLDM…), with TFC as the worked example.

**Install:** SteamCMD **HLDS AppID 90** + the TFC mod via
**`app_set_config 90 mod tfc`**, anonymous:
`+login anonymous +force_install_dir <dir> +app_set_config 90 mod tfc
+app_update 90 validate +quit`. Native **Windows (`hlds.exe`) and Linux
(`hlds_run`)**, 32-bit, **tiny / lightweight**, very stable (minimal update
churn).
> **⚠ Install gotcha (well-documented):** HLDS app 90 needs **`app_update 90
> validate` run several times** before everything lands, and for GoldSrc *mods*
> the mod files often **don't download on the first pass** (only the engine does)
> due to appmanifest issues — and the stock `hlds_run` even **omits the
> `app_set_config 90 mod tfc` line**, so it only grabs CS / HL. The install flow
> needs **retry-until-"fully installed"** logic (and optionally the LinuxGSM
> appmanifest workaround). This is the one real install wrinkle.

**Launch:** `hlds(.exe) -console -game tfc -secure +map 2fort +maxplayers 32
-port 27015 +ip <ip> +rcon_password <pw> +exec server.cfg -zone 1024 +log on`.
LO-shaped. Notes: **`-console` is required** for the server to appear in the
internet browser (and for headless text mode); **`-zone 1024`** prevents
script-memory crashes on some maps; `-game tfc` selects the mod.

**Config — `tfc/server.cfg`** (a GoldSrc **cvar / console-command** file, the
Source-family model — like Rust's convars but GoldSrc): `hostname`, `sv_password`,
**`rcon_password`**, `mp_timelimit`, `sv_region`, `mp_teamplay`, `tfc_autoteam`,
`sv_lan`. Plus **`mapcycle.txt`** (rotation), **ban lists** (`banned_ip.cfg` /
`banned_user.cfg` + `listip` / `listid`), and **`motd.txt`**. A cvar / text
editor, not structured INI / JSON.

**⭐ Control — Source / GoldSrc RCON.** Set `rcon_password` and you get a mature
**RCON** (`status` for the player list, `kick`, `banid`, `changelevel`, `say`,
`users`). The cleanest classic RCON on the list — PowerGSM's `RconClient` drives
it, **but note GoldSrc RCON is UDP challenge-response** (subtly different from
Source's TCP RCON), so confirm / implement the GoldSrc variant. `status` over RCON
gives players without log parsing.

**Ports:** **27015 TCP + UDP** (`-port`) — a single port serves game, queries,
and RCON (no separate query port). Allocator reserves it; multiple instances take
distinct `-port`s.

**Graceful shutdown / saves:** session FPS — **no persistent world save**, so
shutdown isn't save-critical. `quit` (RCON / console) or **Ctrl+C / SIGINT**
(→ `GSM.CtrlCSender` / SIGINT). State = config (`server.cfg`, `mapcycle.txt`, ban
lists) + custom maps (`tfc/maps/*.bsp`); managed-dir + backup is just those.

**Plugins:** **AMX Mod X** (over Metamod) is the standard admin / plugin framework
(kick / ban / RTV / admin tools) — an optional plugin-management angle, the
GoldSrc analogue of Oxide.

**⭐ The real payoff — it templates the GoldSrc family.** The *exact same plugin
shape* (HLDS app 90 + `mod <name>` + GoldSrc RCON + `server.cfg` + `mapcycle.txt`
+ port 27015) covers **Counter-Strike 1.6** (`mod cstrike`), **Day of Defeat**
(`mod dod`), **Half-Life DM** (`mod valve`), Ricochet, DMC, and more. So building
TFC really builds a reusable **HLDS / GoldSrc base** — the highest code-reuse
payoff in the catalog (bigger than ATS → ETS2).

**Fit verdict.** Clean, light, stable, with real RCON and a tiny footprint — and
the family-template angle makes it punch above its weight. The only friction is
the multi-run / appmanifest install gotcha and confirming the GoldSrc (UDP) RCON
variant.

**Suggested slicing:** (1) install (app 90 + `mod tfc`, **retry-until-complete**)
+ launch (`-console -game tfc +map +maxplayers -port`); (2) `server.cfg` cvar
editor + `mapcycle.txt` + ban lists; (3) **GoldSrc RCON** client (`status` /
kick / ban / changelevel); (4) maps managed dir + backup; (5) [later] AMX Mod X;
(6) [later] generalise to CS 1.6 / DoD / HLDM via a shared HLDS base.

### TERA
**Status:** `[blocked: binaries?]`
First-look (unverified): official servers were shut down, and **no public
dedicated-server binaries were ever released** — self-hosting means a
community-reverse-engineered server emulator, which is a fundamentally
different (and legally/technically murkier) effort than the SteamCMD games
above. Per the request: *only viable if usable binaries can be found.*
**First task is a feasibility/legality check**, not a plan doc.
Research notes: _TBD_

### Stardew Valley (SMAPI headless server mod)
**Status:** `[shipped]` — shipped in PowerGSM 0.5.0 as plugin v0.1.0 (Tier 0:
install / launch / stop / farm archive-restore, Windows + Linux). Tiers 1–4
(stdin pseudo-RCON, multi-instance via Harmony port patch, …) remain future
work — see `StardewValley_Plugin_Plan.md` and the fork repo
`siteml/SMAPIDedicatedServerMod` (fork slices F1–F4 done). The research notes
below are retained for history; the shipped design diverged in places (fork
consumed as a release zip, `/bin/sh` + shared Xvfb `:97` bootstrap on Linux
instead of xvfb-run, Mesa llvmpipe for GPU-less Windows).

**There is no official dedicated server.** Stardew multiplayer normally needs the
host running the full game client. The community workaround is a **headless
server mod** running under **SMAPI** (Stardew Modding API, by Pathoschild) that
turns the host farmer into an automated **bot** so the world stays online.
Several forks exist:
- **`ObjectManagerManager/SMAPIDedicatedServerMod`** — the one asked about; the
  modern, actively-developed lineage.
- **`theghost99/StardewUnattendedServer`** — .NET 6, tracks SV 1.6.9+ / SMAPI
  4.1.7; good "currently maintained" reference.
- **`DawningW/stardew-always-on-server`** — clearest headless / Linux (xvfb)
  instructions.
- The original Nexus "Always On Server" (#2677, 2018) — historical.

**Install model (this is the hard part — nothing like the SteamCMD games):**
1. **Authenticated SteamCMD with an owning account.** SDV is AppID **413150**
   but it is **NOT anonymous** — `steamcmd +login <account> +app_update 413150
   validate +quit`. The server needs a real Steam account that **owns the game**
   (+ Steam Guard 2FA). PowerGSM *can* do this (it already has Steam-credential +
   Steam-Guard-prompt handling), but it's an operational / licensing oddity —
   that account can't simultaneously play SDV elsewhere, and a GOG copy is the
   DRM-free alternative.
2. **SMAPI is a separate, non-Steam install.** Download SMAPI from Pathoschild's
   GitHub, run its installer against the game dir, drop the mod folder into
   `StardewValley/Mods/`, then launch via **`StardewModdingAPI.exe`** (not the
   game exe). So the install flow is multi-step and custom — well outside
   `InstallRunner`'s SteamCMD-only path.
3. **Possible build-from-source.** Some forks ship prebuilt releases; others
   expect you to build the mod against your game install (`GamePath` in
   `DedicatedServer.csproj`), needing the .NET SDK. Confirm per fork.
4. **Headless needs a virtual display.** SDV is MonoGame and wants a display; on
   Linux you must wrap the launch in **`xvfb-run -a`** (Xvfb as an
   `IPrerequisiteProvider`). On Windows it spawns a game window in the session.

**Config:** `config.json` in the mod's folder (generated on first run; JSON →
clean file-editor candidate). Settings: FarmName, StartingCabins, CabinLayout,
farm-creation options, and host-automation toggles (PurchaseJojaMembership,
EnableCropSaver, MoveBuildPermission…). **Gotcha:** farm-creation fields
(FarmName / cabins / layout) only apply when the farm is *first created* —
changing them later means **resetting the farm (losing progress)**, so the editor
needs a loud "applies at creation only" warning. Also, a `config.json` left over
from an older mod version can block startup (delete → regenerate).

**Control:** the SMAPI **console** (commands typed into the SMAPI window) plus
in-game chat commands the mod adds. **No RCON, no REST API.** Co-op connection is
Stardew's **invite-code / Galaxy P2P** model (the "Always On" mod writes an
`InviteCode.txt`), or **direct IP on UDP 24642** if hosting by IP — there's no
standard server-browser / query. Player count / status would come from
**log-parsing the SMAPI console**.

**Graceful shutdown — actually a good match.** The server stops cleanly with
**Ctrl+C / SIGINT** in the SMAPI console (it saves on exit). That maps directly
onto PowerGSM's existing **`GSM.CtrlCSender`** (Windows) and **SIGINT** (Linux)
paths — a real plus. Watch the SMAPI **"press any key to continue" crash prompt**
(the community uses auto-restart `.bat`s + marker files); PowerGSM's
stdin-newline trick + crash-restart policy should cover it.

**Saves:** in the user profile, **not** the install dir —
`%APPDATA%\StardewValley\Saves\<Farm_ID>\` (Windows) /
`~/.config/StardewValley/Saves/` (Linux). Managed-dir / backup candidate, but the
path is profile-relative, unlike the UE games' in-install saves.

**Version fragility:** the mod must match the SDV + SMAPI versions; an SDV update
can break hosting until the fork updates. Expect to pin versions and update
deliberately.

**Verdict.** Feasible, and a fun novel plugin shape (mod-loader-hosted game), but
it's the most manual install on the list and carries a licensing wrinkle (an
authed account that owns the game). Best treated as a later, lower-priority
"because we can" plugin once the SteamCMD-native games are done — its one bright
spot is that graceful stop and crash-restart line up neatly with PowerGSM's
existing CtrlCSender / SIGINT machinery. If pursued, settle first: which fork
(maintained + prebuilt?), Steam-auth vs GOG install, and the xvfb prerequisite on
Linux.

---

## Notes on grouping (for when we flesh these out)

Rough buckets, useful for batching the research:

- **SteamCMD survival/UE (closest to existing plugins):** Dragonwilds,
  Palworld, Enshrouded, Nightingale, Soulmask, Aloft,
  Sunkenland, Valheim, Rust.
- **SteamCMD, non-survival:** Starbound, American Truck Simulator,
  Assetto Corsa, Space Engineers, Team Fortress Classic.
- **Non-SteamCMD install (different download/prereq model):** Mindustry
  (Java jar + JRE), OpenTTD (own download).
- **Feasibility-gated:** TERA (emulator only), Dune: Awakening (self-host
  exists, but it's a Hyper-V/k8s cluster, not a process — architecture
  mismatch, parked), Towers of Aghasba (no dedicated server — P2P host-based
  only, parked).
- **Mod-hosted / no official server (novel shape):** Stardew Valley (full game +
  SMAPI + headless bot mod; authed Steam or GOG; xvfb on Linux; clean Ctrl+C /
  SIGINT stop that fits CtrlCSender).
