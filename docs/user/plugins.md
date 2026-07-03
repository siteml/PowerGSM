# Plugin setup

This guide covers setting up each officially supported game. Each plugin
declares its own configuration; the fields below are what you fill in when
creating an installation and its instances (see the
[Manager guide](manager.md#installing-a-game) for the general flow).

Two config scopes recur:

- **Install-level** — set once per installation, shared by every instance of it
  (edited via *Edit Installation…*).
- **Instance-level** — set per instance (edited via *Edit Instance*). Where a
  key exists at both scopes, the instance value wins.

**Ports** marked below are auto-allocated and clash-checked across the whole
Node by the Manager; you can override them. Whatever ports you end up with must
be reachable from players (forward them on the router / open the firewall).

Games at a glance:

| Game | Plugin id | Steam AppID | Install method | Platform | Extra runtime |
|---|---|---|---|---|---|
| [Last Oasis](#last-oasis) | `lastoasis` | 920720 | SteamCMD (anonymous) | Windows / Linux | VC++ on Windows (auto) |
| [Factorio](#factorio) | `factorio` | 427520 | SteamCMD (account owning Factorio) or direct download (anonymous) | Windows / Linux | — |
| [Conan Exiles](#conan-exiles) | `conanexiles` | 443030 | SteamCMD (anonymous) | Windows | VC++ 2015–2022 x64 |
| [Windrose](#windrose) | `windrose` | 4129620 | SteamCMD (anonymous) | Windows | VC++ 2015–2022 x64 |
| [lo-myrealm](#lo-myrealm-last-oasis-name-enrichment) | `lo-myrealm` | — (utility) | — | — | — |

Most of these install anonymously (choose *Anonymous*). The exception is
**Factorio via SteamCMD**, which needs a Steam account that **owns Factorio** —
use Factorio's **direct download** method for an anonymous install instead.

---

## Last Oasis

`lastoasis` · Steam AppID **920720** (the dedicated-server tool) · **Windows or
Linux**.

Last Oasis is realm-based: servers authenticate to the MyRealm backend with a
customer/provider key pair, and each server instance is a tile on your realm.

### Prerequisites

- A MyRealm realm with a **CustomerKey** and **ProviderKey** (from the MyRealm
  dashboard).
- On a Linux Node, the 32-bit Steam libraries (see [prerequisites](prerequisites.md)).
- On a Windows Node, the **Visual C++ 2015–2022 x64** runtime — Last Oasis ships
  it with the server files, so the Node installs it automatically during install
  (run the Node as Administrator). Not needed on Linux.

### Install-level config

| Field | Required | Notes |
|---|---|---|
| **CustomerKey** | yes | Realm-wide auth key from the MyRealm dashboard. |
| **ProviderKey** | yes | Provider auth key. Revoke it to lock out every server using it. |
| **SteamBranch** | no | Beta branch name; blank = default release branch. |
| **SteamBranchPassword** | no | Password for a private beta branch, if any. |

### Instance-level config

| Field | Default | Notes |
|---|---|---|
| **Identifier** | (required) | Unique per instance on the realm; shown in the MyRealm dashboard. |
| **CustomerKey / ProviderKey** | (inherit) | Blank inherits the installation value; set to override for this one instance (useful when one install hosts several realms). |
| **ServerBinary** | `MistServer-Win64-Shipping.exe` | Which executable to launch — the name differs between builds and platforms. On Windows, `…-Shipping.exe` is the shipping build; `MistServer.exe` is the dev build. On Linux the binary name differs again. |
| **Port** | 5555 | Game port, unique per instance. |
| **QueryPort** | 27015 | Steam query port, unique per instance. |
| **Slots** | 5 | Tile slots (max 100 per official docs). |
| **OverrideConnectionAddress** | (auto) | External IP **or domain name** for player connections; blank = auto-detect. |

### Operational notes

- **Linux install writes `steam_appid.txt`.** The dedicated-server tool is
  AppID 920720, but the server binary must authenticate as the *game* (903950).
  On Linux the plugin writes `903950` into
  `Mist/Binaries/Linux/steam_appid.txt` automatically as part of install —
  nothing for you to do.
- **Linux graceful stop uses SIGINT.** Stopping cleanly relies on SIGINT (not
  SIGTERM); a clean stop exits with code 130. The Node handles this — just note
  130 is the "clean stop" code, not an error.
- **Name enrichment.** Character names in history/notifications can be resolved
  to their authoritative current names via the optional
  [lo-myrealm](#lo-myrealm-last-oasis-name-enrichment) utility plugin. Last
  Oasis works fully without it.

---

## Factorio

`factorio` · Steam AppID **427520** · **Windows or Linux** · no extra runtime.

The only game here with two install methods. **SteamCMD requires a Steam
account that owns Factorio** — add it under [Steam credentials](manager.md#steam-credentials)
and select it (not *Anonymous*). **Direct download** pulls the headless build
from factorio.com and needs no account.

### Install-level config

| Field | Notes |
|---|---|
| **SteamBranch** | Beta branch name; blank = stable. (SteamCMD method.) |
| **DownloadUrl** | Factorio headless download URL — only used with the **direct download** method (an alternative to SteamCMD; fetches the headless tarball/zip straight from factorio.com). |
| **UseExperimental** | Relevant to the **direct-download** version tracking (stable vs experimental headless build). With **SteamCMD**, which build you get is governed by **SteamBranch**, so this checkbox has no effect there. |

### Instance-level config

| Field | Default | Notes |
|---|---|---|
| **Port** | 34197 | Game port (UDP), unique per instance. |
| **RconPort** | 27015 | Source RCON port. |
| **RconPassword** | — | Required to use RCON. |
| **SaveFile** | — | Pick a save from the install's `saves/` folder (or type a name). See below. |
| **UseLatestSave** | on | Start from the most recent save. If you turn this **off** and leave **SaveFile** blank, the start is blocked with a warning — pick a save first. |
| **ServerSettings** | `server-settings.json` | Path to the settings file. |
| **MapGenSettings / MapSettings** | — | Optional paths. |

### Extra tabs

Factorio's plugin supports the file features described in the
[Manager guide](manager.md#files-saves-generation-and-config-editing):

- **Saves** — a `saves/` file manager (`.zip` only): upload, download, delete,
  rename, copy.
- **Generate map** — pick a preset (Default, Death World, Rail World, Ribbon
  World, Rich Resources, Lakes, Island), a save name, and an optional seed; the
  Node runs the generator and the new save appears in `saves/`.
- **Server Settings** — a structured form for `server-settings.json` (name,
  visibility, auth, gameplay, autosave). Fields you added by hand outside the
  form are preserved on save.

---

## Conan Exiles

`conanexiles` · Steam AppID **443030** · **Windows only** · needs the **Visual
C++ 2015–2022 x64** runtime (the Node installs it — run the Node as
Administrator).

### Install-level config

| Field | Default | Notes |
|---|---|---|
| **Build** | Enhanced | **Enhanced** is the UE5 build (May 2026 onward). **Legacy** is the older UE4 build via Steam's `conan-exiles-legacy` beta branch. **Pick this before installing** — switching afterwards requires an Update. |
| **SteamBranchPassword** | — | Only if Funcom gates a preview behind a password; blank for both public branches. |

### Instance-level config

| Field | Default | Notes |
|---|---|---|
| **ServerName** | PowerGSM Conan Server | Written into `Engine.ini` `[OnlineSubsystem]` at launch, so spaces/special characters are fine. |
| **ServerPassword** | — | Also written into `Engine.ini`. **Leaving it blank keeps the existing password** — to remove one, blank this *and* tick **ClearServerPassword**. |
| **ClearServerPassword** | off | Only acts when the password box is blank; writes an empty password (open server). |
| **MaxPlayers** | 40 | Official cap is 40. |
| **Port** | 7777 | Game port. **Conan ignores `Engine.ini`'s Port** — this value is authoritative. |
| **QueryPort** | 27015 | Steam query port. Keep it off game-port+1 (see pinger note). |
| **RconPort** | 25575 | Conan implements RCON natively — no external tool. |
| **RconPassword** | — | Blank disables RCON. Set a strong value if internet-exposed. |
| **RconMaxKarma** | 60 | Anti-DDoS karma cap (Funcom's recommended default). |
| **Multihome** | — | Bind to a specific local IP on multi-NIC hosts; blank = all interfaces. |

### The pinger port (important)

The UDP port **immediately after the game port** (game port + 1 — e.g. **7778**
when the game port is 7777) is a hard-coded "pinger" the in-game server browser
uses to detect the server. **It must be left open**, and you cannot move it —
there's no config or command-line option. Don't assign it to the query port or
to another instance, or the server won't appear in the browser even though it's
running. The Manager reserves it as a derived port when allocating.

---

## Windrose

`windrose` · Steam AppID **4129620** · **Windows only** · needs the **Visual
C++ 2015–2022 x64** runtime (the Node installs it — run the Node as
Administrator).

No install-level config. Windrose keeps its configuration in `ServerDescription.json`;
edit it while the instance is stopped.

### Instance-level config

| Field | Default | Notes |
|---|---|---|
| **UseDirectConnection** | on | **On (recommended for a managed host):** the server binds the single fixed port below and does **not** use UPnP. **Off:** the server brokers connections through the Windrose vendor service and opens router ports itself via UPnP NAT punch-through. Written into `ServerDescription.json` at launch. |
| **DirectConnectionServerPort** | 7777 | The one port the server binds in direct mode. **Forward this port on both TCP and UDP.** Allocated and clash-checked by the Manager. Ignored when direct connection is off. |

For a PowerGSM-managed host, leave **UseDirectConnection** on and forward the
one port — it's predictable and doesn't depend on UPnP being available.

---

## lo-myrealm (Last Oasis name enrichment)

`lo-myrealm` is a **utility plugin**, not a game. It doesn't host anything; it
enriches Last Oasis data by resolving character IDs to their **authoritative
current character names**, read live from the MyRealm portal's character-rename
page. This fills in real names in history and notifications before the game
itself would, and tracks portal renames.

Last Oasis works completely without it — this is optional polish, and it
degrades cleanly when unavailable.

### What it needs

- **A logged-in MyRealm web session.** The plugin doesn't hold the session
  itself; it lives in the Manager's shared, encrypted **web-session store**
  (key `myrealm:default`), captured through an embedded login dialog the
  Manager opens when a session is needed and none is stored. Any plugin using
  the same key shares that session.
- Access to the realm the servers belong to (the realm id comes from the Last
  Oasis session, so no extra config is needed per instance).

### Setup

1. Enable the `lo-myrealm` plugin (it ships in `Plugins\`; see
   [Managing plugins](manager.md#managing-plugins)).
2. The first time it needs the portal, the Manager opens a login window — sign
   in to MyRealm there. The session is stored encrypted and reused; you'll only
   be re-prompted if it expires.
3. From then on, Last Oasis player names in the History window and Discord
   notifications resolve to current character names automatically.

---

Next, for developers: [write your own plugin](../developer/plugin-authoring.md).
