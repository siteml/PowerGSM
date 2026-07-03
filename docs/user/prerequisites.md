# Prerequisites

Everything you need in place before installing PowerGSM. Read the parts that
apply to you: most people run **one Manager** on a control PC and **one or
more Nodes** on host machines.

---

## The short version

- **You do not need to install .NET.** Release builds are self-contained.
- **You do not need to install SteamCMD.** The Node downloads it on first use.
- **The Manager is Windows-only.** Nodes run on Windows **or** Linux.
- **Open the Node's port** (default 8765) between the Manager and each Node.
- **Hosting Steam-based games on Linux** needs 32-bit runtime libraries
  (one `apt`/`pacman` command). Everything else is handled for you.

---

## Manager (control PC)

The Manager is a Windows desktop application.

| Requirement | Detail |
|---|---|
| OS | Windows 10, Windows 11, or Windows Server, 64-bit. |
| Runtime | None to install — the release build bundles its own .NET 8 runtime. (Only building from source needs the .NET 8 SDK.) |
| Disk | The application is small. The SQLite database it creates grows slowly with history; budget a few hundred MB to be safe. |
| Network | Outbound HTTP to each Node's address and port. Outbound HTTPS to GitHub for update checks and plugin downloads (optional but recommended). |
| Privileges | A normal user account is fine. Administrator is **not** required to run the Manager. |

The Manager stores its database, credentials, plugins, and logs next to its
executable, so install it somewhere your user account can write (a folder
under your user profile, or a dedicated folder you own — not `C:\Program
Files`, which is read-only to standard users).

---

## Node (each host machine)

The Node is a small headless service. Install one on every machine that will
actually run game servers. It runs on Windows or Linux.

### Common to both platforms

| Requirement | Detail |
|---|---|
| CPU / architecture | 64-bit x64. |
| Runtime | None to install — the release build is self-contained. |
| SteamCMD | **Not required in advance.** The Node downloads SteamCMD automatically the first time it installs a Steam-based game, and keeps it up to date. |
| Disk | Enough for the game servers you host. Dedicated servers range from a few hundred MB to tens of GB each; plan generously. |
| Network — inbound | The port the Node listens on (default **8765**) must be reachable from the Manager. |
| Network — outbound | The Node downloads SteamCMD, game files, and (on Windows) redistributables. Allow outbound HTTP/HTTPS. |
| Network — game ports | Each game server needs its own inbound ports (game, query, RCON, etc.) reachable from players. These are per-game — see the [plugin setup guide](plugins.md). |

### Windows Node

| Requirement | Detail |
|---|---|
| OS | Windows 10, 11, or Windows Server, 64-bit. |
| Visual C++ runtime | Many Steam games (Unreal Engine titles like Conan Exiles and Windrose) need the Microsoft Visual C++ 2015–2022 x64 runtime. The Node installs a game's bundled redistributables automatically after a SteamCMD install — **this needs Administrator.** If the runtime is still missing, the Manager shows a pre-install notice with a download link before you install. |
| Privileges | Run the Node **as Administrator** (or as a service under an account with admin rights) if you host games that ship redistributables. Without it, redist installation is skipped and affected games crash silently at launch (exit code `-1073741515`, no log). |
| Service | The Node can run as a Windows service. Use the **GSM.NodeSetup** utility that ships beside it — it creates and starts the `GSMNode` service for you (needs Administrator). |

### Linux Node

| Requirement | Detail |
|---|---|
| OS | A 64-bit Linux distribution with glibc (Ubuntu, Debian, and similar are the tested targets). |
| 32-bit libraries (for Steam games) | SteamCMD is a 32-bit program. Install the 32-bit GCC runtime **before** installing any Steam-based game:<br>• Debian / Ubuntu: `sudo apt install lib32gcc-s1`<br>• Arch: enable multilib, then `sudo pacman -Sy lib32-gcc-libs`<br>The Node detects the missing library on its own and tells you the exact command if you skip this. |
| User account | Run the Node as a dedicated **unprivileged** user (e.g. `powergsm`). It does not need root. |
| systemd | Recommended for auto-start and clean shutdown. The setup utility can generate a unit file for you. **Note:** the generated unit uses `KillMode=process` — the default (`control-group`) would kill the game along with the Node on `systemctl stop`. If you write your own unit, keep that setting. |

> **Which games run on Linux?** Factorio and Last Oasis run on Linux Nodes.
> Windows-only Unreal titles (Conan Exiles, Windrose) depend on the Visual
> C++ runtime and are intended for Windows Nodes. The plugin's own platform
> check will stop you installing a Windows-only game onto a Linux Node.

---

## Authentication between Manager and Node

The Manager authenticates to each Node with a **shared secret (auth token)**.
You set this token in the Node's `nodesettings.json` before the Node's first
run, and enter the same token in the Manager when you add the Node.

- The token defaults to `CHANGE_ME_BEFORE_FIRST_RUN` — change it.
- Generate a strong token, for example: `openssl rand -base64 36`.
- Anyone with the token and network access to the Node can control the games
  on it. Treat it like a password; use a different token per Node if you like.

The Node also has built-in brute-force protection (failed-attempt lockout and
per-IP rate limiting), configurable in `nodesettings.json`.

---

## Firewall / network summary

| Direction | From → To | Port | Why |
|---|---|---|---|
| Manager → Node | control PC → each host | Node port (default 8765/TCP) | All management traffic. |
| Players → Node | internet → each host | per-game ports | Reaching the game servers themselves. |
| Node → internet | each host → out | 80/443 | SteamCMD, game downloads, Windows redistributables. |
| Manager → internet | control PC → out | 443 | GitHub update checks and plugin downloads (optional). |

If a Node sits behind NAT, forward both its management port and each game's
ports.

---

## Per-game notes at a glance

Full setup for each game is in the [plugin setup guide](plugins.md). Quick
orientation:

| Game | Install source | Platform | Extra runtime |
|---|---|---|---|
| **Factorio** | SteamCMD (needs a Steam account that owns Factorio) or direct download from factorio.com (anonymous) | Windows or Linux | none |
| **Last Oasis** | SteamCMD (anonymous) | Windows or Linux | VC++ 2015–2022 x64 on Windows — shipped with the server files and installed by the Node automatically (run as admin); not needed on Linux |
| **Windrose** | SteamCMD (free, anonymous) | Windows | Visual C++ 2015–2022 x64 |
| **Conan Exiles** | SteamCMD | Windows | Visual C++ 2015–2022 x64 |

---

Next: [install and run a Node](node.md).
