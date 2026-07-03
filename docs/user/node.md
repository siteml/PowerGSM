# Running a Node

A **Node** is the agent that actually runs your game servers. You install one
on every host machine. It's a small headless service that the Manager
controls over HTTP. Nodes run on **Windows or Linux**.

This guide covers installing, configuring, running, and updating a Node.
Before you start, skim the [prerequisites](prerequisites.md) for your
platform.

---

## 1. Get the files

Download the Node archive for the host's platform from the
[Releases](https://github.com/siteml/PowerGSM/releases) page:

- `PowerGSM-Node-X.Y.Z-win-x64.zip` for a Windows host
- `PowerGSM-Node-X.Y.Z-linux-x64.zip` for a Linux host

Extract it to a folder the Node account can read and write. A folder the Node
owns — not `C:\Program Files` (read-only to standard users) and not a system
path on Linux. Common choices:

- Windows: `C:\PowerGSM\Node`
- Linux: `/opt/PowerGSM` (owned by the service user, see below)

Inside you'll find, among others:

- `GSM.Node[.exe]` — the Node itself.
- `GSM.NodeSetup[.exe]` — the configuration and service-install utility (see next).
- `nodesettings.json` — the Node's configuration file.
- `GSM.Shim/` — per-instance supervisor binaries the Node launches.

The release is **self-contained** — there's no .NET runtime to install.

> **Linux note:** files copied from a Windows build (e.g. via SCP/SFTP) often
> arrive without the execute bit. `GSM.NodeSetup` fixes `+x` on `GSM.Node`
> and the shim binaries automatically the first time you run it, so run it
> before trying to launch the Node directly.

---

## 2. Configure the Node with GSM.NodeSetup

`GSM.NodeSetup` is the companion tool that reads and writes `nodesettings.json`,
generates the auth token, and installs the Node as a service. It sits right
next to `GSM.Node`.

- On **Windows**, running it with no arguments opens a **graphical** setup window.
- On **Linux**, running it with no arguments opens an **interactive console
  wizard**. (Force the console on Windows with `--cli`.)

By default it works on the `nodesettings.json` next to it; point it elsewhere
with `--config <path>`.

### The setup wizard

On a fresh Node (auth token still the placeholder), the tool drops straight
into a 5-step wizard. Press Enter to accept the value shown in `[brackets]`.

1. **Node identity** — a **Node ID** (defaults to the machine name; how the
   Node labels itself to the Manager) and the **listen port** (default `8765`).
2. **Storage** — the **data directory** (the Node's database and SteamCMD
   cache) and the **servers directory** (where new game installs go by
   default). Both default to folders next to the executable. Operators often
   point the servers directory at a larger/separate volume.
3. **Operations** — **max concurrent installs** (default `2`) and **log
   retention in days** (default `30`).
4. **Authentication** — generates a strong **auth token**. This is the shared
   secret the Manager uses to connect. **The wizard prints it once at the end
   — copy it into the Manager when you add the Node.**
5. **Review and save** — confirms and writes the file (backing up any existing
   one to `nodesettings.json.bak`).

After saving, it offers to install the Node as a system service right away.

### The main menu

Run the tool again after configuration and you get a menu instead of the
wizard:

| Option | Does |
|---|---|
| 1 Run setup wizard | Re-run the guided setup. |
| 2 View current configuration | Print all settings (including the token). |
| 3 Edit configuration | Change individual fields, including advanced security settings. |
| 4 Generate a new authentication token | Rotate the token (disconnects any Manager until updated there). |
| 5 Set up service user (Linux) | Create the service user and fix directory ownership without installing the service. |
| 6 Install as system service | Register + start the Node service. |
| 7 Uninstall system service | Remove the service. |
| 8 Show service status | Report whether the service is running. |

### Editing nodesettings.json by hand

You can also edit the file directly — it's plain JSON, bound straight into the
Node at startup (with `reloadOnChange`), so hand-edits work. The fields are
described in the [configuration reference](#configuration-reference) below.

Change the **auth token** before exposing the Node. The Node simply compares
whatever token a Manager presents against `AuthToken` in the file, so the
default `CHANGE_ME_BEFORE_FIRST_RUN` works fine as a token — but it's publicly
known, so leaving it lets anyone who can reach the port control the Node. A
security concern, not a connectivity one.

---

## 3. Run the Node

### As a service (recommended)

Running the Node as a service means it starts on boot and restarts on failure.
Use `GSM.NodeSetup` → **Install as system service**.

#### Windows

Run `GSM.NodeSetup` **as Administrator** (elevation is required to create a
service) and choose *Install as system service*. It creates a service named
`GSMNode`, set to start automatically, and starts it.

The service runs under the **LocalSystem** account by default, which has the
administrative rights the Node needs to install games' Visual C++
redistributables. If you host Unreal titles (Conan Exiles, Windrose), keep it
this way.

To remove it later: `GSM.NodeSetup` (elevated) → *Uninstall system service*.

#### Linux (systemd)

Run `GSM.NodeSetup` on the host. In the *Install as system service* flow it
will:

1. **Recommend a dedicated unprivileged user** (default `powergsm`). Do not
   run the Node as root — many game servers (Last Oasis and other Unreal
   titles) refuse to start as root, and the Node needs no elevated privileges.
   If you run the tool with `sudo`, it offers to **create the user** and
   **chown** the install/data/servers directories to it.
2. **Write a systemd unit** (`gsmnode.service`) configured for you, then:
   - if you ran the tool as **root**, install, enable, and start it directly;
   - otherwise, print the three commands to run yourself:
     ```bash
     sudo install -m 644 <path>/gsmnode.service /etc/systemd/system/gsmnode.service
     sudo systemctl daemon-reload
     sudo systemctl enable --now gsmnode
     ```

Check status and follow logs with:

```bash
systemctl status gsmnode
journalctl -u gsmnode -f
```

To remove it (manual on Linux, run as root):

```bash
sudo systemctl disable --now gsmnode
sudo rm /etc/systemd/system/gsmnode.service
sudo systemctl daemon-reload
```

> **Important — `systemctl stop` leaves games running.** The generated unit
> uses `KillMode=process` on purpose: the Node launches a per-instance shim
> that owns each game process, and both live in the unit's cgroup. Under
> systemd's default (`control-group`), stopping the Node would kill every
> running game with it. With `process`, only the Node is signalled; the games
> keep running and the Node re-adopts them when it comes back. **The trade-off:
> `systemctl stop gsmnode` is not a full teardown — stop your instances from
> the Manager first if you want everything down.**

### In the foreground (testing)

To watch the Node's output directly while testing, run the binary itself:

- Windows: run `GSM.Node.exe` from a terminal.
- Linux, as the service user:
  ```bash
  sudo -u powergsm /opt/PowerGSM/GSM.Node
  ```

The *Set up service user* menu option (Linux) prints the exact `sudo -u`
commands for foreground, backgrounded, and shell access.

---

## 4. Verify it's running

The Node exposes an **unauthenticated** version endpoint. Once it's up, open
this in a browser (or `curl` it) from the Node itself, replacing the port if
you changed it:

```
http://localhost:8765/api/version
```

You should get a small JSON response with the application name, build, and
protocol version. From another machine, use the Node's address instead of
`localhost` to confirm the port is reachable across the network — that's the
same reachability the Manager needs.

---

## 5. Add it to the Manager

With the Node running and its auth token in hand, switch to the Manager and
add the Node using its address, port, and token. See the
[Manager guide](manager.md#adding-a-node).

---

## Configuration reference

`nodesettings.json` has three sections. Defaults are sensible; most operators
only touch the `Node` section.

### `Node`

| Field | Default | Meaning |
|---|---|---|
| `NodeId` | machine name | Label the Node reports to the Manager. |
| `ListenPort` | `8765` | TCP port the Node listens on. |
| `AuthToken` | `CHANGE_ME_BEFORE_FIRST_RUN` | Shared secret for the Manager. **Change before first run.** |
| `DataDirectory` | `./data` (abs.) | Node database and SteamCMD cache. |
| `ServersDirectory` | `./servers` (abs.) | Default parent folder for game-server installs. |
| `MaxConcurrentInstalls` | `2` | How many installs/updates run at once. |
| `LogRetentionDays` | `30` | How long the Node keeps its logs. |
| `MetricsIntervalSeconds` | `5` | How often the Node samples host/instance metrics. |

> Directory paths are written as **absolute** paths by the setup tool on
> purpose: a service's working directory isn't the install folder, so a
> relative `./data` would resolve somewhere unexpected.

### `Security`

Brute-force and abuse protection for the Node's API. Defaults suit most setups.

| Field | Default | Meaning |
|---|---|---|
| `MaxFailedAttempts` | `10` | Failed auths before an IP is locked out. |
| `FailureWindowMinutes` | `5` | Window over which failures are counted. |
| `LockoutMinutes` | `15` | How long a locked-out IP stays blocked. |
| `AuthFailureDelayMs` | `250` | Delay added to each failed auth (slows guessing). |
| `RequestsPerMinutePerIp` | `600` | Per-IP rate limit (`0` = unlimited). |
| `MaxRequestBodyBytes` | `4194304` | Max request size (4 MB). |
| `MaxConcurrentConnections` | `100` | Max simultaneous connections. |

### `Logging`

Standard .NET log-level map (`LogLevel.Default`, `LogLevel.Microsoft.AspNetCore`,
etc.). Set `Default` to `Debug` for verbose troubleshooting.

---

## Directory layout

A configured Node keeps its state in the data and servers directories you set:

- **Data directory** — the Node's own database (instance→process mappings so it
  can re-adopt running games after a restart), chat/event history, and the
  downloaded SteamCMD.
- **Servers directory** — one subfolder per installed game server, unless an
  installation overrides its path.

Both default next to the executable but are freely relocatable — point the
servers directory at a large volume if your game files are big.

---

## Updating a Node

You don't update Nodes by hand. The Manager detects when a Node is older than
itself and can **push the update to the Node** — new Node binary, plus the
setup utility and shim when needed. Running games keep running throughout,
because the per-instance shims own the game processes and the Node re-adopts
them after the swap. If an update comes up unhealthy, the Node rolls back to
the previous binary automatically. See the
[Manager guide](manager.md#updating-nodes).

---

## Headless / automated setup

For scripted or container deployments, skip the interactive tool:

```bash
GSM.NodeSetup --auto-init
```

This writes a fresh `nodesettings.json` with a freshly generated auth token
and **prints the token to stdout** so your provisioning script can capture it
and hand it to the Manager. Combine with `--config <path>` to target a specific
file. On Linux you can then install the systemd unit with the standard
`systemctl` commands, running the Node as your chosen service user.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Manager can't connect | The token entered in the Manager doesn't match `AuthToken` on the Node, wrong port, or the port isn't open between them. Verify `/api/version` is reachable from the Manager's machine. |
| A Steam game crashes instantly on Windows (exit `-1073741515`, no log) | Visual C++ runtime missing. Run the Node with Administrator rights so it can install the game's bundled redistributables. |
| Steam install fails on Linux with a 32-bit library error | Install the 32-bit runtime: `sudo apt install lib32gcc-s1` (Debian/Ubuntu) or enable multilib + `sudo pacman -Sy lib32-gcc-libs` (Arch). |
| Linux Node won't launch / "shim spawn failed" | Execute bit missing on `GSM.Node`/shim binaries after a Windows copy. Run `GSM.NodeSetup` once (it fixes `+x`), or `chmod +x` them. |
| A UE4 game refuses to start on Linux ("Refusing to run with root privileges") | The Node is running as root. Run it as the `powergsm` (or other unprivileged) user. |
| `systemctl stop gsmnode` didn't stop the games | Expected — `KillMode=process` keeps games alive across a Node bounce. Stop instances from the Manager for a full teardown. |

---

Next: [set up the Manager](manager.md).
