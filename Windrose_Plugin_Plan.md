# Windrose Plugin — Plan

GameId `windrose` · DisplayName "Windrose" · new game plugin in `GSM.PluginsSource\WindrosePlugin.vb`

Researched 21 Jun 2026 against the official guide
(`playwindrose.com/dedicated-server-guide`, game version basis
`0.10.0.5.120` / deployment `0.10.0.0.251`), the Steam Community guide,
and the Windrose wiki. Windrose is Steam Early Access (since 14 Apr 2026,
Kraken Express / Pocketpair). Still actively changing — treat every exact
path/key/number below as version-dependent and re-verify against a live
install before relying on it.

---

## 1. Game facts (confirmed)

| Fact | Value |
|---|---|
| Dedicated-server Steam AppID | **4129620** (anonymous login, free, no purchased game needed) |
| Game client AppID | 3041230 (not us) |
| Install method | SteamCMD only (`app_update 4129620 validate`) |
| Platform | **Windows-only** — no native Linux server binary |
| Engine | **Unreal Engine 5.6.1** (project "R5"; `R5\Saved\...` layout, RocksDB save tree). Confirmed from a live log — UE log/shutdown conventions apply |
| RCON | **None** — no RCON surface documented |
| Ports | Game default is **dynamic NAT punch-through via UPnP** (`UseDirectConnection=false`, ICE/P2P) — server opens whatever it wants on the router. **PowerGSM rejects that posture (Decision D1):** default `UseDirectConnection=true` with a fixed, operator-chosen `DirectConnectionServerPort` (e.g. 7777, **TCP+UDP**) — no UPnP, one known port to forward |
| Instances per install | **1.** `CanLaunchMultipleServerInstances=false` by default; docs warn multiple instances against the same RocksDB corrupt saves |
| Multiple worlds | Many worlds per install, **one active at a time**, selected by `WorldIslandId` in `ServerDescription.json` |
| Config surface | **Two JSON files** (not INI, not launch args) — see §3 |
| Launch args | **`-log` only** (confirmed via StartServerForeground.bat). No per-setting args — the exe reads `ServerDescription.json`. `-log` also arms the UE console Ctrl+C handler for graceful shutdown |

### Server layout (relative to SteamCMD install root) — CONFIRMED 21 Jun 2026
```
R5\Binaries\Win64\WindroseServer-Win64-Shipping.exe   ' REAL server (launch target, -log)
WindroseServer.exe                 ' root WRAPPER — do NOT launch (tracks wrong PID)
StartServerForeground.bat          ' start /abovenormal ...Shipping.exe -log  (visible console)
R5WorldDescriptionUpdater.exe      ' applies WorldDescription.json edits (see §3.2)
R5\ServerDescription.json          ' common server config            [CONFIRMED at R5\]
R5\Saved\SaveProfiles\Default\RocksDB_v2\<ReleaseVer e.g. 0.10.0>\Worlds\<id>\WorldDescription.json  [CONFIRMED: live worlds path for dedicated servers]
R5\Saved\SaveProfiles\Default\RocksDB_v2_Backups\...   ' backups
R5\Saved\Logs\R5.log               ' log dir CONFIRMED; filename assumed R5.log
```
> ⚠ `RocksDB_v2` (no `_Backups`) is the live runtime folder — docs say
> **DO NOT TOUCH IT.** Dedicated servers only use the `Worlds` data, not
> player-character data.

---

## 2. Template & architecture fit

Closest existing plugin is **ConanExilesPlugin.vb** — both are SteamCMD-
anonymous, UE-style, Windows-only, single-instance-per-install, with
structured config-file editors. Windrose differs from Conan in three ways
that drive the design:

1. **Config is JSON, not INI.** The file editors follow **Factorio's
   `server-settings.json` editor** (System.Text.Json round-trip that
   preserves unknown fields) rather than Conan's INI line-walker. Read
   `FactorioPlugin.vb`'s `IInstanceFileEditorProvider` impl when building
   Slice 2.
2. **No launch args / no RCON.** `BuildLaunchArguments` returns `""`,
   `GetRconProtocol` returns `Nothing`. The instance Configuration tab is
   nearly empty — almost all config lives in the `ServerDescription.json`
   editor.
3. **World-settings edits need a post-write process run**
   (`R5WorldDescriptionUpdater.exe`). Neither `IInstanceFileEditorProvider`
   (no post-write hook) nor a plain editor fits — modelled instead as an
   `IFileGenerationProvider` "Configure World" op that emits
   `[WriteFileStep, RunProcessStep]` (see §3.2). Slice 3.

---

## 3. Config surface detail

### 3.1 `ServerDescription.json` (Slice 2 — primary editor)

Nested: editable fields live under `ServerDescription_Persistent`. The
editor's JSON read/write must address that nested object and round-trip
`Version` / `DeploymentId` / `PersistentServerId` untouched.

| Field | Type | Notes / default |
|---|---|---|
| `PersistentServerId` | (read-only) | "Do not edit." Round-trip untouched, never surface as editable |
| `InviteCode` | Text | `[0-9a-zA-Z]`, **≥6 chars, case-sensitive**. Validate |
| `IsPasswordProtected` | (derived) | **DERIVED** from `Password` on write (`= Password<>""`); not exposed — avoids the docs-warned mismatch footgun |
| `Password` | Password (sensitive) | server connect password |
| `ServerName` | Text | browser display name |
| `WorldIslandId` | Text | active world id; must match a world folder + its `WorldDescription.json` (Slice 3 manages this) |
| `MaxPlayerCount` | Integer | live default 8; co-op caps 8 |
| `UserSelectedRegion` | Enum | `SEA` / `CIS` / `EU` (EU=EU+NA) / empty=auto |
| `P2pProxyAddress` | Text | listening-socket IP |
| `UseDirectConnection` | Bool | `true`=direct sockets (no UPnP), `false`=ICE/P2P NAT-punch. **PowerGSM defaults `true` (D1)**; setting `false` is caveated via the field description + install notice (no live-validation channel for file values) |
| `DirectConnectionServerPort` | Integer (file-editor field) | game default is **`-1`** (unset); PowerGSM writes a real value (e.g. 7777, **TCP+UDP**) when direct mode is on. **Not** allocator-tracked (**D2**) — lives only in the file; operators pick a port distinct from other Windrose servers (field description says so) |
| `DirectConnectionServerAddress` | Text | "reserved for future use" |
| `DirectConnectionProxyAddress` | Text | NIC selector, default `0.0.0.0` |
| `AutoLoadLatestBackupIfHasBroken` | Bool | default `true` |
| `CanLaunchMultipleServerInstances` | Bool | default `false`; leave off (PowerGSM enforces 1/instance anyway) |

**Decision D1 — operator-controlled networking (default direct connection).**
The game ships `UseDirectConnection=false`, which uses ICE/P2P with UPnP
NAT punch-through — the server opens arbitrary ports on the router. That is
unacceptable for a managed host. PowerGSM therefore **defaults
`UseDirectConnection=true`** so the server binds one fixed, operator-chosen
`DirectConnectionServerPort` (the game default is `-1`/unset, so PowerGSM
must write a real value such as 7777, TCP+UDP) with no UPnP. That port lives
in the `ServerDescription.json` editor; because the node port-allocator only
inspects instance-config fields (not file-editor schemas) it is **not** auto-
tracked for cross-instance clashes (**Decision D2**) — the field description
tells operators to pick a distinct port per server. There's likewise no per-
instance validation channel for file-editor values, so the UPnP-exposure
caveat is carried by the field description + the install-time notice rather
than a live warning; the schema default (`true`/7777) only forces direct mode
for a from-scratch write (on an existing file the form shows the real values
and the operator flips it).
Tradeoff: direct mode means players join by IP:port and cross-network play
needs that one port forwarded manually — the frictionless invite-code/UPnP
join is given up on purpose. Whether direct-mode servers *also* still list
via the invite code, or strictly require IP:port joins, is a VERIFY-ON-LIVE
item (§6).

### 3.2 `WorldDescription.json` (Slice 3 — world management)

Per-world file, deep path under `RocksDB_v2\<ver>\Worlds\<id>\`. Two
moving parts:

- **Switch active world** = set `WorldIslandId` in `ServerDescription.json`
  (covered by the Slice 2 editor; Slice 3 adds a friendlier picker that
  lists discovered world folders).
- **Edit world settings** = edit `WorldDescription.json` **then run**
  `R5WorldDescriptionUpdater.exe <path>`. Model as `IFileGenerationProvider`:
  schema = preset + the WDS parameter knobs; `BuildGenerationSteps` emits a
  `WriteFileStep` (the new JSON) + a `RunProcessStep` (the updater),
  `ExpectedOutputRelativePath` = the world's `WorldDescription.json`.

`WorldDescription.json` fields: `IslandId`, `WorldName`, `CreationTime`,
`WorldPresetType` (`Easy`/`Medium`/`Hard`/`Custom`), `WorldSettings`.
`WorldSettings` is the gnarly part — three dicts (`BoolParameters`,
`FloatParameters`, `TagParameters`) whose **keys are escaped JSON strings**,
e.g. `"{\"TagName\": \"WDS.Parameter.MobHealthMultiplier\"}"`. The structured
form maps friendly labels ⇆ those tag keys.

WDS knobs (full tag = `WDS.Parameter.<suffix>`):

| Label (suffix) | Default | Range |
|---|---|---|
| CoopQuests (`Coop.SharedQuests`) | true | bool |
| Immersive exploration (`EasyExplore`) | false | bool (note: legacy name; `true` = harder) |
| Mob health (`MobHealthMultiplier`) | 1.0 | 0.2–5.0 |
| Mob damage (`MobDamageMultiplier`) | 1.0 | 0.2–5.0 |
| Ship health (`ShipsHealthMultiplier`) | 1.0 | 0.4–5.0 |
| Ship damage (`ShipsDamageMultiplier`) | 1.0 | 0.2–2.5 |
| Boarding difficulty (`BoardingDifficultyMultiplier`) | 1.0 | 0.2–5.0 |
| Coop stats correction (`Coop.StatsCorrectionModifier`) | 1.0 | 0.0–2.0 |
| Coop ship stats correction (`Coop.ShipStatsCorrectionModifier`) | 0.0 | 0.0–2.0 |
| Combat difficulty (`CombatDifficulty`) | Normal | Easy/Normal/Hard (TagParameter, nested) |

> Floats go through as `Text` (no Float field type in the contract), same as
> Conan's multipliers. Setting a non-preset value forces `WorldPresetType`
> to `Custom` on next launch.

---

## 4. Slice plan (confirm-gated, each compiles + is testable)

**Slice 1 — core plugin, installs + launches.**
`IGamePlugin` only: GameId/DisplayName, `MaxInstancesPerInstallation=1`,
`GetInstallSteps`/`GetUpdateSteps` (SteamCmdStep AppId 4129620, anonymous,
validate), `GetExecutablePath` (`WindroseServer.exe`, empty for Linux),
`BuildLaunchArguments=""`, `ValidateConfig` **empty** (InviteCode /
password-pair validation deferred to the Slice 2 editor schema, since those
fields live in `ServerDescription.json` not instance CustomFields),
`EvaluateCrash` (standard policy delegation), `GetLogSources`
(best-effort file log), `GetRconProtocol=Nothing`, `CreateModManager=Nothing`,
`CreateLogParser=Nothing` / `GetLogParseRules` empty (crash detection via
exit code only; log markers wait for Slice 4).
`IInstallationNoticeProvider`: Windows-only, NAT/UPnP, "stop server before
editing config". `IPrerequisiteProvider`: `vcredist-2015-2022-x64`
(defensive — VERIFY). Empty instance + install config schemas.
**Status: CONFIRMED WORKING 21 Jun 2026 — installs + launches the Shipping
exe as a hidden-console background process; graceful shutdown (AttachConsole
+ CTRL_C) verified.**

**Slice 2 — `ServerDescription.json` structured editor.**
`IInstanceFileEditorProvider` (JSON, nested `ServerDescription_Persistent`,
Factorio-modelled; `IsPasswordProtected` derived from `Password`, region
`Auto`⇆`""`, InviteCode preserve-if-blank, D1 direct-connection defaults).
`IManagedDirectoriesProvider`: **Logs (R) only** — Worlds/backups deferred to
Slice 3 (version-subfolder path + live-RocksDB caution).
`ILaunchOptionsProvider`: `GracefulShutdownTimeoutMs = 45000` (RocksDB flush +
backup on stop). Instance Configuration schema left empty (D2). **Status:
written 21 Jun 2026 — awaiting plugin-reload test. Goal: edit server name,
password, region, max players, direct-connection port from the UI.**

**Slice 3 — world management.**
World-folder discovery + active-world picker (`WorldIslandId`), and
world-settings editing via `IFileGenerationProvider` + `R5WorldDescriptionUpdater.exe`,
including the escaped-tag-key `WorldSettings` mapping. **Goal: create/configure
worlds and switch the active one from the UI.**

**Slice 4 — SHIPPED.** Player-event tracking + server-state surface, verified
against real `Windrose R5.log` captures (the earlier blocker — no player-
connect capture — is resolved; two clean join/play/leave captures drove the
work). Both halves landed, matching every other plugin:
- **Node-side `GetLogParseRules`** — connect (`VerifyUeCredentials`: AccountId +
  IP), roster-dump name enrichment, farewell + disconnect leaves; AccountId
  (hex) → **CharacterId** so the node `players` table persists (its PK is
  `character_id`; pid-only sessions were live-only). Server-ready + world id via
  `TileLoaded` (MapPath / short TileName / IslandId→TileId); listen port, tick
  rate, shutdown reason as `Custom_*`.
- **Manager-side `WindroseLogParser` (`ILogParser`)** — live join/leave into
  History + notifications. Join on `LogNet: Join succeeded: <name>`; leaves
  resolve their name via an `AccountId→Name` binding harvested from the roster
  lines (leave lines are AccountId-only). This is the half that actually feeds
  History; Windrose shipped without it (Slice-1 `CreateLogParser = Nothing`),
  which is why join/leave never appeared until now.

Graceful-shutdown question was already **RESOLVED** in Slice 2: R5 (UE5.6.1)
honours AttachConsole+CTRL_C cleanly and needs no console isolation.

---

## 5. Scope fences

- **No RCON.** Don't invent one.
- **Networking is operator-controlled, not portless (D1).** PowerGSM
  defaults `UseDirectConnection=true` + a fixed `DirectConnectionServerPort`
  (no UPnP NAT-punch). Flipping back to P2P is allowed; caveated via field
  description + install notice (no live warning channel for file values). The
  allocator does **not** track that port (**D2** — file-editor field);
  operators pick distinct ports per server.
- **Slice 1 ships standalone** without Slices 2–4; degrade cleanly when
  config editors absent (raw-file access via managed dirs still works).
- **Don't touch `RocksDB_v2`** (live runtime). Slice 2 surfaces only Logs
  (read-only); Slice 3's managed dirs target Worlds / `RocksDB_v2_Backups`.
- **World-settings JSON is fiddly** (escaped-tag keys + mandatory updater
  exe). Kept entirely in Slice 3; don't bleed it into the Slice 2 editor.
- Floats as `Text` (no contract Float type) — consistent with Conan/Factorio.

---

## 6. Open questions — status (mostly RESOLVED 21 Jun 2026 via live log)

1. ~~**Exe path & name**~~ — **RESOLVED**:
   `R5\Binaries\Win64\WindroseServer-Win64-Shipping.exe`, launched with
   `-log`. The root `WindroseServer.exe` is a wrapper — don't launch it.
2. ~~**`ServerDescription.json` location**~~ — **RESOLVED**: `R5\ServerDescription.json`
   (under R5, not install root). Drives the Slice 2 editor `RelativePath`.
3. ~~**Launch args**~~ — **RESOLVED**: `-log` only.
4. ~~**VC++ redist required?**~~ — **RESOLVED**: yes (UE5.6.1 Shipping). Keep the
   `vcredist-2015-2022-x64` prereq.
5. ~~**Log file path/format**~~ — **dir RESOLVED** (`R5\Saved\Logs`); filename
   assumed `R5.log` (UE names log after project "R5") — confirm the exact file.
   Join/leave line shapes confirmed against real captures [Slice 4 SHIPPED].
6. **Direct-mode join UX (D1)** — PARTIAL. Confirmed the *default* mode
   (`UseDirectConnection=false`) registers the server with the vendor
   Connection Manager (`r5coopapigateway-{eu,ru,kr}-release.windrose.support:443`),
   auth's as a dedicated server, and brokers P2P/ICE — players join by invite
   code. Still unconfirmed whether a `UseDirectConnection=true` server ALSO
   registers for invite-code joins or strictly requires IP:port. Needs a
   direct-mode run.

---

## 7. Contract notes (no changes needed)

Everything maps onto the **existing** contract — no `ContractsVersion` bump.
Interfaces used: `IGamePlugin`, `IInstallationNoticeProvider`,
`IPrerequisiteProvider`, `IManagedDirectoriesProvider`,
`IInstanceFileEditorProvider`, `ILaunchOptionsProvider`,
`IFileGenerationProvider`. Header magic comment:
`' <RequiresContracts: 1>` (same as Conan).
