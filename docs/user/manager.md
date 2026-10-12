# Using the Manager

The **Manager** is the Windows desktop application you use to control
everything: your Nodes, the games installed on them, the running instances,
automation, notifications, and Discord integration. This guide walks through
installing it and using each feature.

Before you start, have at least one [Node running](node.md) with its auth token
and certificate fingerprint handy.

---

## Contents

- [Install and first launch](#install-and-first-launch)
- [The main window](#the-main-window)
- [Adding a Node](#adding-a-node)
- [Installing a game](#installing-a-game)
- [Editing, updating, and deleting installations](#editing-updating-and-deleting-installations)
- [Instances](#instances)
- [The instance panel](#the-instance-panel)
- [Viewing logs](#viewing-logs)
- [Files: saves, generation, and config editing](#files-saves-generation-and-config-editing)
- [History](#history)
- [Steam credentials](#steam-credentials)
- [Shared resources](#shared-resources)
- [Automation rules](#automation-rules)
- [Notifications](#notifications)
- [Discord bot](#discord-bot)
- [Managing plugins](#managing-plugins)
- [Updating Nodes](#updating-nodes)
- [Updating the Manager](#updating-the-manager)
- [Settings](#settings)
- [Staying alive: tray, safe mode, watchdog](#staying-alive-tray-safe-mode-watchdog)
- [Utility plugins and web sessions](#utility-plugins-and-web-sessions)

---

## Install and first launch

1. Download `PowerGSM-Manager-X.Y.Z-win-x64.zip` from the
   [Releases](https://github.com/siteml/PowerGSM/releases) page.
2. Extract it to a folder your user account can write to (e.g.
   `C:\PowerGSM\Manager`). Avoid `C:\Program Files` — it's read-only to
   standard users, and self-update won't be able to swap the binaries there.
3. Run `GSM.Manager.exe`.

The build is self-contained, so there's no runtime to install. On first launch
the Manager creates its database (`gsm.db`), a `Plugins\` folder with the
bundled game plugins, and a `Logs\` folder, all next to the executable. You'll
see a welcome panel and an empty tree — that's expected; you add a Node next.

---

## The main window

The window has three parts:

- **The tree (left).** Your world, nested: **Nodes → Installations →
  Instances**. A Node is a host machine; an installation is one copy of a game
  installed on that Node; an instance is one running server from that
  installation. Right-click any item for its actions.
- **The panel (right).** Shows details for whatever you select — a node
  summary, an installation, or an instance with its tabs.
- **The menu bar.** Grouped as **File**, **Nodes**, **Tools**, **Help**.

Menu map:

| Menu | Items |
|---|---|
| **File** | Restart Normally / Restart in Safe Mode, Re-enable Features (safe mode only), Exit |
| **Nodes** | Add Node…, New Installation…, Update Nodes…, Secure All Nodes (HTTPS)… |
| **Tools** | History…, Purge & Rebuild History…, Reload Plugins, Manage Plugins…, Open Plugins Folder, Steam Credentials…, Shared Resources…, Automation Rules…, Notifications…, Discord Bot…, Diagnostics…, Settings… |
| **Help** | Check for updates…, Update History…, About PowerGSM… |

The status bar along the bottom shows update availability, read-only-install
warnings, and a safe-mode banner when relevant.

In the tree, a **green person glyph with a number** after an instance is its
current online player count (installations show the sum), so you can see
where the activity is without opening anything. Everything under a
**[detached]** Node is grey — the Manager isn't talking to it.

---

## Adding a Node

**Nodes → Add Node…** (or right-click the tree root). You need four things
from the Node you set up (the setup wizard printed the last two together):

- **Address** — the Node's hostname or IP.
- **Port** — its listen port (default `8765`).
- **Auth token** — the shared secret.
- **Cert SHA-256** — the fingerprint of the Node's certificate. With it, the
  Manager's first contact is pinned HTTPS and the token never crosses the
  wire in the clear. Leave it blank only for a Node that was set up without
  HTTPS — the Manager then connects over plain HTTP, and you secure it
  afterwards (below).

**Test Connection** proves the address/port/token (and fingerprint) before
you save. Give the Node a friendly name and save. The Manager connects and
the Node appears in the tree. Selecting it shows a **protocol compatibility
indicator**:

- **green** — Manager and Node speak the same protocol version.
- **orange** — one side is newer. Everything the older side understands still
  works; newer features are simply unused. You don't have to update both sides
  at once.
- **red** — the Node couldn't be contacted. Check the address, port, token,
  and firewall.

### Securing a Node (HTTPS)

The Node panel has an **HTTPS** line. For a Node added over plain HTTP it
offers **Secure this node…**, which in one pass: turns HTTPS on (the Node
mints its certificate), verifies the certificate over a pinned connection,
switches plain HTTP **off**, and **rotates the auth token** over the new
encrypted channel. Nothing on the Node's machine is touched by hand and
running games are unaffected (the Node bounces once; its shims keep the
games up). *Advanced* exposes the enable-only step if you want to keep plain
HTTP for a while.

**Nodes → Secure All Nodes (HTTPS)…** lists every Node that isn't fully
secured yet (with its running-instance count), runs the same sequence on
each ticked Node one at a time, and reports per row. Untick *rotate token*
if another Manager also uses these Nodes and you'd rather re-enter tokens
later.

On a secured Node the panel reads *HTTPS: active on shared port N (pinned
…)* and *HTTP: off*, with links to **Turn HTTP off / on**, **Rotate auth
token…**, **Switch back to HTTP** and — for Nodes onboarded on a separate
HTTPS port before 0.7 — **Move HTTPS to shared port…**.

**Certificate mismatch.** If a Node ever presents a certificate other than
the pinned one, the Manager **blocks it** and the panel turns red with
**Re-trust certificate…**. Expect this after NodeSetup's *Regenerate TLS
certificate* or a reinstall with a fresh data directory; otherwise treat it
as suspicious. The dialog shows the fingerprint actually presented — compare
it with what NodeSetup reports on the Node before accepting.

### Node process control

The panel's status row offers **Restart node…** (the Node relaunches itself
via NodeSetup; games stay up) and **Shut down node…** (double-confirmed:
the Node stays down until someone starts it on that machine).

**Attach / Detach.** Right-click a Node to *Detach* it — the Manager stops
polling it and stops probing it altogether, but keeps its configuration;
useful when a host is deliberately offline. Its installations and instances
show grey (unknown) in the tree. *Attach* re-enables it. **Delete Node**
removes it entirely (the games on the host are untouched on disk).

**Edit Node…** changes the saved address, port, token, or name (the pinned
fingerprint is shown read-only; use *Re-trust* to change it).

---

## Installing a game

An *installation* is one copy of a game on a Node. Create one with **Nodes →
New Installation…** or by right-clicking a Node → *Add Installation…*.

The form walks you through:

1. **Node** — which host to install on.
2. **Game / plugin** — pick from the loaded plugins (Last Oasis, Factorio,
   Conan Exiles, Windrose, …). This determines everything game-specific.
3. **Install method** — depends on the game. Common options:
   - **SteamCMD** — installs via Steam. Most games. Choose a **Steam
     credential** — an account that owns the game — or *Anonymous* for servers
     that install without one (e.g. Windrose). **Factorio via SteamCMD needs an
     account that owns Factorio**; its direct-download method is anonymous.
     See [Steam credentials](#steam-credentials).
   - **Direct download** — fetches the server files from a URL (Factorio
     offers this as an alternative to Steam).
4. **Install-level configuration** — settings shared by every instance of this
   installation (for example Last Oasis's CustomerKey/ProviderKey). Per-game
   details are in the [plugin setup guide](plugins.md).
5. **Install path** — where the files go on the Node (defaults under the Node's
   servers directory).

Start the install and a **Progress** tab shows live output. Notes:

- **Steam Guard.** If the account has Steam Guard, SteamCMD asks for a
  verification code; the Manager pauses, prompts you for it, and resumes once
  you enter it. This works with **email** Steam Guard (a code is emailed the
  first time a new server logs in) and with a **mobile-authenticator code you
  type in** (the rotating 5-character code from the Steam mobile app).
    - **Caveat — "approve in the app" / QR sign-in.** If your account is set up
      so Steam asks you to *approve the login by tapping in the Steam mobile
      app* (or by scanning a QR code) instead of giving you a code to type,
      there's nothing to enter into the Manager's prompt — that flow isn't
      driven by the code box, and it re-challenges on every login, which is
      awkward for unattended updates. For a dedicated-server Steam account,
      prefer **email** Steam Guard or a typed authenticator code, or use
      **Anonymous** where the game allows it (Windrose, or Factorio's
      direct-download method). *(Noted from
      how SteamCMD and the mobile authenticator behave; not yet verified
      against PowerGSM directly.)*
- **Windows redistributables.** After a Steam install, the Node installs any
  Visual C++ redistributables the game ships (needs the Node running as
  Administrator). If a required runtime is missing, the Manager shows a
  pre-install notice with a download link.
- Large installs run in the background; you can keep working. The Node limits
  how many run at once (its `MaxConcurrentInstalls` setting).

---

## Editing, updating, and deleting installations

Right-click an installation:

- **Edit Installation…** — change its display name and any install-level config
  fields. (The install method and Steam credential association are set at
  creation.)
- **Update Installation** — re-run SteamCMD / re-download to pull the latest
  server build. The Manager also checks upstream versions in the background and
  flags when an update is available.
- **Delete Installation** — removes it from the Manager. You're asked whether
  to also delete the files on the Node.

---

## Instances

An *instance* is a runnable server from an installation. One installation can
host several instances (e.g. multiple realms or worlds).

- **Add Instance…** — right-click an installation. You give the instance a name
  and fill in its **instance-level configuration** (the fields the plugin
  declares — world name, ports, passwords, etc.). Instance config overrides
  install-level config where they overlap.
- **Ports.** The Manager allocates ports automatically across the whole Node so
  instances don't collide, and validates them before start. You can override
  them in the instance config.
- **Instance sets.** An instance can carry a **set tag** (edited via *Edit
  Instance*). Set tags let you target groups of instances in notifications
  (e.g. a "production" set) without creating any new grouping entity.

**Running an instance.** Select it (or right-click) for **Start**, **Stop**,
**Restart**. The panel shows live status — Starting, Running (with PID),
Stopped (with exit code), Crashed — refreshed every few seconds. On start, the
plugin may run a quick pre-flight check and warn you (with a "Start anyway?"
prompt) about likely-fatal misconfigurations before launching.

Stopping is graceful where the game supports it; a force-kill fallback applies
after a timeout. Because each instance is supervised by its own shim on the
Node, restarting or updating the Node does **not** stop your instances.

**Surviving a host reboot.** Every instance's configuration ends with a
lifecycle group the Node acts on **without the Manager**:

- **Recover state on node restart** — if the instance was running when the
  host (or Node) went down, the Node restarts it from its last launch settings
  at the next boot.
- **Auto start with node** — start it whenever the Node starts, even if it
  was stopped on purpose (like a Windows service set to Automatic).
- **Node-boot start delay (seconds)** — stagger: at boot the Node launches
  instances in ascending delay (0 / 30 / 60 brings three servers up a minute
  apart instead of all at once). Manager-initiated starts ignore it.

Changes apply to the Node immediately on save. A boot-time restart loop is
suppressed after three rapid attempts. The same group holds the crash policy
knobs (max crashes, window, reset-after, minimum restart delay, graceful
shutdown timeout).

**Delete Instance** removes it (with a confirmation).

---

## The instance panel

Selecting an instance shows tabbed detail. The area **above** the tabs (the
panel header) always shows the instance's live status — Starting, Running (with
PID), Stopped (with exit code), Crashed — along with its current session/tile
and the Start / Stop / Restart actions, regardless of which tab is selected.
Which tabs appear below depends on what the game's plugin supports:

| Tab | What it's for |
|---|---|
| **Overview** | The currently connected players and their info. |
| **Configuration** | The instance's settings (the plugin's schema), editable here. |
| **[File editors]** | Structured editors for known config files (e.g. Factorio `server-settings.json`) — a form, not raw text. Only for plugins that provide them. |
| **[Managed directories]** | File managers for game folders (e.g. Factorio `saves/`) — upload, download, delete, rename, copy. |
| **Chat** | In-game chat the Node captured (for games whose logs expose it). |
| **Logs** | Live server log (see below). |

The panel remembers your last-selected tab and per-instance "show logs"
preference across the session, so comparing the same tab across instances
doesn't mean re-clicking each time.

---

## Viewing logs

Open an instance's **Logs** tab (or the standalone log viewer) for a live,
streaming view of the server's output. It:

- streams new lines as they arrive from the Node,
- reloads recent history when you open it, so a freshly-opened viewer isn't
  blank after a Manager restart,
- reconnects automatically if the stream drops.

The Node is the source of truth for logs — it keeps capturing and recording
player/chat/state events even while the Manager is closed, and the Manager
catches up when it reconnects.

---

## Files: saves, generation, and config editing

For plugins that support it, the instance panel gives you three file-related
capabilities without touching the Node's disk directly:

- **Managed directories** — a file manager per declared folder. Upload a save,
  download one to back it up, rename, copy, or delete. (Factorio exposes
  `saves/`, `.zip` only.)
- **File generation** — a schema-driven "generate a file" action. For Factorio
  this is **map generation**: pick a preset (Default, Death World, Rail World,
  Ribbon World, Rich Resources, Lakes, Island), a save name, and an optional
  seed; the Node runs the generator and the new save lands in `saves/`. It runs
  as its own tab so you can watch progress or keep working.
- **Structured config editor** — edits a known config file as a form. Factorio's
  `server-settings.json` is the canonical case (name, visibility, auth,
  gameplay, autosave). The file on the Node is the source of truth: it's
  fetched fresh when you open the tab and written back on Save, and any fields
  you added by hand outside the form are preserved.

Raw config files (e.g. an `.ini`) remain editable through the managed-file
browser where the plugin exposes them.

---

## History

**Tools → History…** (or the per-instance *History* button, which pre-fills the
filter for that instance's current session) opens a non-modal window with:

- **Timeline** — a chronological stream of player joins/leaves and chat,
  filterable by session, player name, and time range.
- **Snapshot at instant** — who was online at a specific moment, reconstructed
  from the activity log.
- **Use UTC** toggle — switch between local and UTC timestamps (defaults to
  local).

Sessions are shown with friendly labels (e.g. "LO realm Site-Main / Tile 5 /
date") rather than raw IDs. Player and session history is kept indefinitely;
chat is pruned on a configurable retention (see [Settings](#settings)).

**Purge & Rebuild History…** (Tools) is **destructive**. It deletes all stored
history, then rebuilds only from what running instances currently expose —
anything not live right now is lost. Use it to recover from corrupted history,
not as a routine resync.

---

## Steam credentials

**Tools → Steam Credentials…** manages the Steam accounts used to install and
update games. The account must **own a license for the game** it's installing.
Credentials are stored **encrypted** on your machine (Windows DPAPI, scoped to
your user account). Add an account here, then select it when creating a
Steam-based installation; updates reuse it automatically. Use *Anonymous* for
games whose servers install without a game-owning account (e.g. Windrose, and
Factorio's direct-download method — but Factorio via SteamCMD needs an account
that owns the game).

---

## Shared resources

**Tools → Shared Resources…** manages configuration values shared across
plugins/installations (for example values a plugin wants defined once and
reused). Most operators only touch this when a specific plugin calls for it.

---

## Automation rules

**Tools → Automation Rules…** lets the Manager act on its own: scheduled
restarts, reactions to events, sequences of actions.

A rule is **trigger → (optional) conditions → action(s)**, with a **scope**
(all instances, a specific instance, a set, etc.):

- **Triggers** — a schedule (cron expression), an instance event (started,
  stopped, crashed, crash-loop), a version-update-available event, and others.
- **Conditions** — optional gates (e.g. only if players are online / not
  online, time windows).
- **Actions** — start/stop/restart an instance, run a sequence, send a
  notification, and more. A **sequence** action runs several steps in order,
  useful for "announce → wait → restart" style routines.

The rule editor guides each part; the condition and action editors are
dedicated dialogs. Rules run even if you're not looking at the Manager (but not
in safe mode).

---

## Notifications

**Tools → Notifications…** sends Discord messages when things happen (instance
started/stopped/crashed, crash loops, update started/completed/failed, player
joined/left).

You configure **destinations** (a Discord webhook, or the bot). Each
destination has:

- **Event filters** — which event types it receives.
- **Scope** — a four-part filter over **Node / Installation / Instance /
  Instance-set**. An event reaches a destination if it matches **any** filter
  you set; a destination with no filters set receives **everything**. So "my
  production set" can be a destination that only hears about instances tagged
  `production`, across whichever nodes host them.
- **Message templates** — customize the text with tokens like `{InstanceName}`,
  `{NodeName}`, `{InstanceSetTag}`, `{TileName}`, etc.

Installation-level events (updates) fan out to the instances under that
installation, so instance- and set-scoped destinations still catch them.

---

## Discord bot

**Tools → Discord Bot…** configures a full Discord bot (via a bot token) that
goes beyond one-way webhooks:

- **Control panels** — live, auto-refreshing embeds in a channel showing
  instance status or the current player list, with buttons to start/stop/restart
  (permission-gated).
- **Player-list panels** — online players grouped by node/game/instance.
- **Slash commands** — `/players`, `/help`, `/panels`, `/lastseen <player>`,
  and management actions. `/lastseen` looks up a player's presence and last-seen
  time; management commands are gated to operators.
- **Role mappings** — map Discord roles to permission levels so only trusted
  roles can control servers.
- **Visibility profiles / per-guild scoping** — control which servers a given
  Discord guild can see and act on, and override panel/command access per role.

The Discord Bot form hosts these as sub-sections (panels, role mappings,
commands, visibility). Set the bot token first; the rest becomes available once
it connects.

### Creating the bot and getting a token

The token comes from Discord, not PowerGSM. One-time setup:

1. Open the [Discord Developer Portal](https://discord.com/developers/applications)
   and click **New Application**. Name it (this is the bot's name).
2. Open the **Bot** tab. Click **Reset Token** (or **Add Bot** on older
   layouts), then **Copy** the token. This is what you paste into PowerGSM.
   Treat it like a password — anyone with it controls the bot. If it leaks,
   reset it here and paste the new one.
3. Still on the **Bot** tab, under **Privileged Gateway Intents**, enable the
   intents PowerGSM's features need — **Server Members Intent** (role/member
   resolution) and **Message Content Intent** — then **Save Changes**.
4. Open **OAuth2 → URL Generator**. Tick scopes **`bot`** and
   **`applications.commands`**. Under bot permissions tick at least **Send
   Messages**, **Embed Links**, **Read Message History**, and **Use Slash
   Commands** (add **Manage Messages** if you want the bot to tidy its own
   panels). Copy the generated URL, open it, and add the bot to your server.
5. Back in PowerGSM, paste the token into **Tools → Discord Bot…** and connect.

Discord's own walkthrough: <https://discord.com/developers/docs/quick-start/getting-started>.
The exact button labels in the portal change from time to time; the four things
you need are an application, a bot token, the right intents, and an invite URL
with the `bot` + `applications.commands` scopes.

---

## Managing plugins

Game support is provided by **plugins** — VB source files in the `Plugins\`
folder that the Manager compiles and loads at runtime. **Tools → Manage
Plugins…** opens a tabbed window:

- **Status** — every plugin, its version/author/source, and whether it loaded
  cleanly. A plugin that fails to compile is reported here without stopping the
  others.
- **Sources** — GitHub sources the Manager can fetch plugins from. The official
  `siteml/PowerGSM` source is seeded and can't be removed; add your own.
- **Updates** — checks your enabled sources for newer plugin versions and lets
  you stage → review → install them. Plugins never auto-update; you approve
  each one.

Installing or updating a plugin hot-reloads it — no Manager restart. Other
Tools entries: **Reload Plugins** (recompile the folder), **Open Plugins
Folder** (drop a `.vb` plugin in by hand).

To write your own plugin, see [Writing plugins](../developer/plugin-authoring.md).

---

## Updating Nodes

You don't update Nodes by hand. **Nodes → Update Nodes…** opens a fleet view —
one row per Node with its installed build, platform, and whether a newer
release exists. Tick the Nodes you want and update them:

- **Update all components** (the default) — pushes NodeSetup, the Shim,
  CtrlCSender (Windows Nodes) and the Node itself in that order, Node last
  so its relaunch already uses the new survivor and shim. If a step fails,
  that Node's remaining steps are skipped so it's never left on a mixed set.
- **Single target** — untick *Update all components* and pick Node, Shim,
  NodeSetup or CtrlCSender.
- **Source** — the **latest release** from the feed (one click; each binary
  is checksum-verified against the release before it's pushed), or a **binary
  you pick** (your own build). Picked files are identified from their bytes
  — the dialog tells you what you picked and warns about a wrong component,
  wrong platform, or a downgrade before anything is uploaded; the Node
  verifies size + SHA-256 again on commit.

The Manager stages each binary on the Node, applies it, and (for the Node
target) waits for it to come back. **Running games stay up** throughout — the
per-instance shims keep them alive and the Node re-adopts them after the swap.
If a Node update comes up unhealthy, the Node rolls back automatically. One
Node failing never aborts the batch. The dialog can't be closed while a push
is running.

---

## Updating the Manager

The Manager updates itself.

- **Help → Check for updates…** forces a check; a status-bar indicator appears
  when a newer release is available.
- The update dialog shows the **release notes**, then lets you **Download**
  (verified against the release checksums, staged without touching the running
  install) and **Apply**.
- **Apply** runs a short pre-flight: it warns if an automation rule is mid-run
  or instances are running (your game servers keep running — they're on the
  Nodes — only the Manager's log streams blink and reconnect), and checks that
  your plugins will still compile against the new version. Then it swaps the
  binaries and relaunches.
- **Help → Update History…** shows every apply attempt and its outcome.
- **Skip this version** hides the indicator until a higher version appears.

If the install location is read-only (e.g. Program Files without elevation),
the Manager warns you up front that self-update can't swap the binaries there.

---

## Settings

**Tools → Settings…**:

- **Data retention** — how many days of chat to keep (default 90). Player and
  session history is never time-pruned.
- **Paths** — read-only display of where `gsm.db` and `Plugins\` resolve.
- **Updates** — how often to check for Manager updates, and whether to include
  pre-releases.
- **Startup** — register the watchdog logon task so the Manager starts (and
  is supervised) at sign-in.

**Tools → Diagnostics…** is the fleet health view: one row per Node with
build / protocol / platform / event-stream / TLS state, plus per-instance
event sources, parse traces and boot-recovery state for troubleshooting.

---

## Staying alive: tray, safe mode, watchdog

The Manager is built to survive unattended operation:

- **Tray icon.** Minimizes to the system tray; window position and state are
  remembered. Right-click the tray icon for quick actions and Exit.
- **Start at sign-in.** A per-user logon task can launch the Manager when you
  sign in (no admin needed). Paired with the watchdog, it auto-restarts on
  crash.
- **Watchdog.** A small sibling process relaunches the Manager if it crashes,
  with a give-up threshold to avoid infinite crash loops.
- **Safe mode.** If a previous run crashed, the Manager offers to start in
  **safe mode** — the database, nodes, and basic instance control still work,
  but the riskier subsystems (plugins, automation, notifications, background
  polling) are switched off so you can recover. **File → Re-enable Features…**
  turns individual subsystems back on without a full restart, and **File →
  Restart Normally** leaves safe mode. You can also start it deliberately with
  the `--safe-mode` command-line switch.

---

## Utility plugins and web sessions

Beyond game plugins, the Manager supports **utility plugins** that enrich the
experience without hosting a game. The shipped example is **lo-myrealm**, which
resolves Last Oasis character IDs to character names via the myrealm portal.

Utility plugins can add their own menu items and panels, and some need a
**web session** — a logged-in browser session captured through an embedded
browser window — plus stored **realm credentials**. Where a utility plugin
needs these, it surfaces the capture and credential UI itself. Setup for
lo-myrealm specifically is covered in the [plugin setup guide](plugins.md).

---

Next: [set up your game's plugin](plugins.md), or — for developers —
[write your own plugin](../developer/plugin-authoring.md).
