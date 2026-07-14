# Palworld Plugin — Plan

GameId `palworld` · DisplayName "Palworld" · new game plugin in `GSM.PluginsSource\PalworldPlugin.vb`

Drafted 12 Jul 2026 from the PlannedPlugins.md research (status `[ready]`).
Every exact path/key/flag below is version-dependent — re-verify against a
live install before relying on it. Pocketpair updates frequently.

---

## 1. Game facts (from research — VERIFY on live install)

| Fact | Value |
|---|---|
| Dedicated-server Steam AppID | **2394010** (anonymous, free) |
| Install method | SteamCMD only. No save-wipe-on-validate reports (unlike Dragonwilds) |
| Platform | **Windows AND native Linux** — `PalServer.exe` / `PalServer.sh` |
| Engine | UE5 |
| Launch flags | `-port=` (**the ONLY way to set the listening port** — tuple PublicPort is advertise-only), `-players=`, `-publiclobby`, `-logformat=Text|Json`, `-publicip=`/`-publicport=` (community-browser advertise overrides). Perf flags (`-useperfthreads` etc.) deprecated in v1.0+ ("leaving unset may improve performance") — not emitted |
| Config | `Pal/Saved/Config/<platform>/PalWorldSettings.ini` — single `OptionSettings=(...)` struct line, NOT normal INI (§3) |
| Ports | game **UDP 8211** (`PublicPort`), Steam query **27015**, REST **8212** (HTTP), RCON **25575** (deprecated — skipped) |
| Control | **REST API** (`RESTAPIEnabled=True`, HTTP Basic `admin`/AdminPassword): `/v1/api/players`, `/info`, `/settings`, `POST /save`, `/announce`, `/kick`, `/ban`, `/shutdown` |
| Logs | **NONE** — no file log even with `-log` (confirmed live); console/stdout only. Stdout capture strategy deferred to Slice 4 (needs REST stop first — StdoutIsLog kills the console graceful path). `-logformat=Json` available for structured stdout |
| RCON | Exists but deprecated by Pocketpair, "scheduled to stop functioning" + mangles multibyte names → **do not build on it**. `GetRconProtocol = Nothing` |
| Graceful stop | Genuinely clean for a UE game: closing console saves+exits; REST `/shutdown`; console `Shutdown {sec} {msg}`. `DoExit` = immediate, no save — never use |
| Saves | `Pal/Saved/SaveGames/<worldid>/` |
| Instances per install | **1** (config + saves shared per install) — `MaxInstancesPerInstallation = 1` |
| Settings reload | Boot-time only. No live `/ChangeSettings` (myth). Edits require restart |

### Server layout (relative to install root) — VERIFY
```
PalServer.exe                                   ' Windows launch target (root wrapper? see Q1)
PalServer.sh                                    ' Linux launch target
Pal\Binaries\Win64\PalServer-Win64-Shipping.exe ' UE shipping exe (Q1: which to launch?)
DefaultPalWorldSettings.ini                     ' full OptionSettings template (install root)
Pal\Saved\Config\WindowsServer\PalWorldSettings.ini   ' live config (Linux: LinuxServer)
Pal\Saved\SaveGames\0\<worldid>\                ' world save
Pal\Saved\Logs\                                 ' assumed UE log dir — VERIFY
```

---

## 2. Template & architecture fit

Closest shipped plugin: **Conan Exiles** (SteamCMD-anon UE, single-instance,
platform-split exe, structured config editor) — but three Palworld-specific
deviations:

1. **Config format is a bespoke tuple, not INI/JSON.** One giant
   `OptionSettings=(Key=Val,Key="Val",...)` line. The Slice 2 editor needs a
   custom parser (split on top-level commas, respect quoted strings) — not
   Conan's INI line-walker, not Factorio's JsonNode. Round-trip rule still
   applies: unknown keys inside the tuple MUST survive untouched.
2. **Control surface is HTTP REST, not RCON.** New territory — no existing
   contract interface carries "plugin-driven remote control". Slice 3 designs
   this (see §4 D3). Slice 1 ships without it, relying on CtrlC/SIGINT.
3. **Fresh-install config is empty.** `PalWorldSettings.ini` ships
   empty/absent; the canonical template is `DefaultPalWorldSettings.ini` at
   the install root. Editor must handle "file empty → seed from schema
   defaults" (NOT Windrose's RequiresExistingFile lockout — Palworld's file
   has no server-owned unforgeable fields, a from-scratch write is safe).

---

## 3. Config surface — `OptionSettings` tuple (Slice 2 editor)

File: `Pal/Saved/Config/<WindowsServer|LinuxServer>/PalWorldSettings.ini`
```
[/Script/Pal.PalGameWorldSettings]
OptionSettings=(Difficulty=None,DayTimeSpeedRate=1.000000,ServerName="...",AdminPassword="",PublicPort=8211,RESTAPIEnabled=False,...)
```

Parser (plugin-owned, reused by editor + startup render):
- Locate `OptionSettings=(` ... matching `)`; split payload on commas at
  paren-depth 0 and outside double quotes.
- Values: bare tokens (numbers, True/False, enum names) or `"quoted"`
  strings. Quoted values may contain commas — respect quotes.
- Serialise back preserving key order + unknown keys; only schema-managed
  keys are rewritten.

Editor schema (initial field set — the ~100-key full set stays raw/unknown):

| Field | Type | Notes |
|---|---|---|
| ServerName | Text | |
| ServerDescription | Text | |
| AdminPassword | Password | also the REST API password |
| ServerPassword | Password | join password |
| ServerPlayerMaxNum | IntegerField | default 32 |
| PublicIP | Text | node external IP for master-list visibility (LO/Conan-style advertise IP) |
| PublicPort | IntegerField | 8211 — see D2 for port ownership |
| RESTAPIEnabled | BooleanField | default True under PowerGSM (D3 depends on it) |
| RESTAPIPort | IntegerField | 8212 — see D2 |
| bIsUseBackupSaveData | BooleanField | if present — VERIFY key name |
| Difficulty / rates / DeathPenalty etc. | later batch | add after core proves out; floats as Text (no Float type, Conan precedent) |

**Decision D1 — RCONEnabled left alone / defaulted off.** Deprecated; plugin
never enables or surfaces it beyond raw round-trip.

**Decision D2 (REVISED post-Slice-1) — listening port is the `-port=` launch
arg.** Official docs: tuple `PublicPort`/`PublicIP` are community-browser
ADVERTISE values and do not change the bind; `-port=` is the only bind
control. So the game port is a plain instance-config IsPort field emitting
`-port=` — SHIPPED in Slice 1, no startup render needed for it.
Tuple-resident RESTAPIPort still needs file handling (Slice 2 editor field,
non-allocated, or a small IStartupFileProvider render if allocator tracking
is wanted — decide in Slice 2/3). Advertise PublicIP/PublicPort are plain
Slice 2 editor fields.
  - Fresh-install seeding (REVISED): install-time copy is IMPOSSIBLE —
    official docs confirm the config dirs are created only by the first
    server run (and the node's CopyFileStep silently "succeeded" without
    producing the file — node quirk, Backlog). Instead the plugin embeds
    the full default `OptionSettings` tuple (captured from a live 12 Jul
    2026 install) as a constant; the Slice 2 editor and any render build a
    complete valid file from it whenever the live file is blank/absent.

**Decision D3 — REST client lives plugin-side (Manager), new opt-in
interface.** Plugins run Manager-side and already do HTTP
(IVersionAwarePlugin), so the REST calls themselves are easy. What's
missing is contract plumbing for:
  (a) player list surface — today players come only from node log-parse
      rules; a REST-sourced list needs a new opt-in (e.g.
      `IRemotePlayerProvider.GetPlayersAsync(config, ct)`) the Manager
      polls and feeds into the same player UI, and
  (b) graceful-stop hook — `InstanceManager.StopInstanceAsync` has no
      plugin pre-stop callback; an opt-in (e.g.
      `IGracefulStopProvider.RequestStopAsync(config, ct)` returning
      handled/fallthrough) would fire REST `/save` + `/shutdown {sec}`
      before the CtrlC/kill path.
  Both are additive side-interfaces whose sole consumer ships with them →
  no ContractsVersion bump (established rule). Manager reaches the REST API
  at `http://{node-host}:{RESTAPIPort}` — VERIFY the REST listener binds
  non-localhost; if localhost-only, D3 needs a node-side proxy instead
  (bigger design — decide after verification).
  Slice 1/2 ship without any of this; CtrlC/SIGINT stop is believed clean.

---

## 4. Slice plan (confirm-gated, each compiles + testable)

**Slice 1 — core plugin, installs + launches + stops.**
[shipped to source 12 Jul 2026; Windows-tested PASS]
As built: SteamCMD per-platform depot, exe candidates (Shipping-Cmd
confirmed the resident process on Windows), `-log` + allocator-managed
`-port=` + `-publiclobby` toggle, no perf flags, no seed copy, inert file
log source (keeps hidden-console spawn → CtrlC graceful, verified),
GracefulShutdownTimeoutMs=60s, vcredist prereq, notices.
Still open: Linux run (exe/.sh + SIGINT halves of Q1/Q2), REST bind (Q4).

**Slice 2 — `OptionSettings` tuple editor.**
`IInstanceFileEditorProvider`, platform-dependent RelativePath
(`Pal/Saved/Config/WindowsServer/...` vs `LinuxServer`), bespoke tuple
parser, embedded default-tuple constant as the blank-file seed (D2),
initial field set per §3 (advertise PublicIP/PublicPort included;
RESTAPIEnabled/RESTAPIPort included; game port EXCLUDED — it's the launch
arg), unknown keys round-trip. Official per-key reference:
docs.palworldgame.com/settings-and-operation/configuration.
**Goal: edit name/passwords/caps/rates/advertise+REST settings from the UI,
including on a blank fresh-install file.**

**Slice 3 — (absorbed).** Game port shipped in Slice 1 as a launch arg;
advertise + REST ports land in the Slice 2 editor. Only remaining question
is whether RESTAPIPort wants allocator tracking (would need a small
IStartupFileProvider render) — decide when Slice 4's REST client makes the
port matter.

**Slice 4 — REST control (D3) + player list.**
[shipped to source 13 Jul 2026; live-tested: stop PASS ("Remote-control
stop: plugin initiated in-game shutdown" + 1.8s exit), player list PASS
with a real player (name/persona/steam-id/IP all populated)]
As built: contract gained `RemoteControlContext` (NodeHost + merged
config + FetchInstanceFile delegate) and `IRemoteControlProvider`
(RequestStopAsync → Boolean handled; GetPlayersAsync →
`GSM.Node.Api.PlayerSession` list — the plugin owns ALL field mapping,
the Manager passes the list through untouched; decline-fast semantics,
exceptions = decline). Manager: `BuildRemoteControlContextAsync` shared
helper; stop hook at the top of StopInstanceAsync (10s cap, node stop
path runs unconditionally after — stop-intent + kill ladder preserved);
player-list override in Manager GetPlayersAsync (8s cap, node list
fallback). Plugin: REST client reading RESTAPIEnabled/RESTAPIPort/
AdminPassword live from the settings file (30s per-instance cache, no
DB credential copy); stop = /announce → /save → /shutdown waittime=1;
/players field semantics verified live (name=character,
accountName=Steam persona, userId="steam_<id64>", playerId=character
hex id, **iP** — erratic casing, harvest is case-insensitive).
Manager→node-host:RESTAPIPort direct; localhost-relay hardening is a
Backlog item. Log-strategy flip (StdoutIsLog on Windows) NOT taken —
Windows stdout carries no UE log at all, so there's nothing to gain;
Linux already captures stdout.
Follow-up idea (post-slice): automation-rule integration — player-count
triggers get REST-fed data for free once polling feeds the standard
pipeline; a generic "remote announce" IAction via the same provider is a
natural add. Design when wanted.

**Slice 5 — saves (REVISED; config-dir half shipped, archive half future).**
Shipped: read-only "Config files" managed dir (platform config dir — flat,
works). NOT shipped: raw `Pal/Saved/SaveGames` managed dir — tried and
reverted 13 Jul 2026: the save is a nested tree (`0/<worldid>/{Level.sav,
Players/, backup/}`) and the node's managed-files listing is FLAT
(`Directory.EnumerateFiles`, top level only) → tab permanently blank.
Proper saves support = SDV-style **world archive/restore** (pack
`0/<worldid>` into a zip/tar.gz in a flat backups dir, restore on either
platform; IFileGenerationProvider pattern) — better for whole-world
download/migration than a recursive file listing anyway. Future slice,
after Slice 4. (Node flat-listing limitation also noted in Backlog.md.)

**[later] Log parse rules** — only if REST player data leaves gaps
(join/leave history timestamps?); needs live log captures.

---

## 5. Scope fences

- **No RCON, ever** (deprecated upstream).
- **No live settings reload** — don't fake it; notices say "restart to apply".
- Slice 1 ships standalone; editor/REST degrade cleanly when absent.
- Full ~100-key tuple NOT schema'd — initial curated set only; unknowns
  round-trip.
- Floats as Text (Conan/Windrose precedent).
- D3 contract additions are generic (any future REST/HTTP-controlled game),
  not Palworld-named.

---

## 6. Open questions — status (12 Jul 2026, Windows live test + official docs)

1. ~~**Launch target**~~ — **RESOLVED (Windows)**: `PalServer-Win64-
   Shipping-Cmd.exe` is the resident process; candidate order correct.
   Linux half still open (does `PalServer.sh` exec the binary?).
2. ~~**Graceful stop**~~ — **RESOLVED (Windows)**: Manager Stop cleanly
   stops the server. Save-stick assumed OK (30s autosave cadence bounds the
   risk). Linux SIGINT half still open.
3. ~~**Log file**~~ — **RESOLVED**: NONE. No file log even with `-log`
   (mod required for one). Console/stdout is the only feed — stdout capture
   flips on in Slice 4 alongside REST stop.
4. ~~**REST bind address**~~ — **RESOLVED**: binds all interfaces
   (verified: Manager-machine curl to node-host:port → 200). Slice 4
   shipped Manager-direct; localhost-relay hardening in Backlog.
5. ~~**Config dir at install time**~~ — **RESOLVED**: does NOT exist;
   official docs confirm dirs are created only by the first run. Seed copy
   removed; embedded default tuple instead. (Side-find: node CopyFileStep
   reported success without producing the file — Backlog.)
6. **Query port 27015** — OPEN, deprioritised (no `-queryport` in the
   official args list; likely fixed — reserve via Notice if it matters).
7. ~~**Command-line overrides**~~ — **RESOLVED**: `-port=` is the ONLY bind
   control (tuple PublicPort is advertise-only), so the port is a launch
   arg by necessity, not a conflict. `-players=` exists but tuple
   ServerPlayerMaxNum is preferred (single source of truth) — not emitted.

---

**Chat: documented gap (no vanilla feed).** The REST API has no chat-read
endpoint (announce is send-only) and vanilla RCON exposes the same
command set — the "chat via RCON" guides refer to server mods
(PalDefender/PalGuard) bolting custom commands onto deprecated RCON.
Nothing to build on. If ever wanted: a chat mod that logs to a FILE
feeds the normal tail+parse pipeline with zero contract work — opt-in
mod territory, not plugin baseline.

## 7. Contract notes

Slices 1–3 ship on contracts v2 (plugin v0.1.x). Slice 4 adds the
remote-control surface and bumps **ContractsVersion 2 → 3** — revised
from the original "no bump" call: plugins distribute independently via
plugin sources, so a v3-consuming plugin can land on a v2 (0.5.0)
Manager, and the gate turns a raw Roslyn compile failure into a clean
ContractsVersionTooNew refusal. Plugin is therefore **v0.2.0 with
`' <RequiresContracts: 3>`** from Slice 4 on.
