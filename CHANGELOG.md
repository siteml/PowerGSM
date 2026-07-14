# Changelog

All notable changes to PowerGSM are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to pre-1.0 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
as documented in [VERSIONING.md](VERSIONING.md): `MINOR` bumps may break
compatibility with the previous version, `PATCH` bumps do not.

## [Unreleased]

## [0.6.0] - 2026-07-13

### Added

- **Remote-control plugin interface (`IRemoteControlProvider`, contracts +
  Manager).** Opt-in for games administered via their own out-of-band
  admin channel (HTTP REST etc.) instead of RCON/stdin. Two capabilities,
  plugin-owned protocol: a graceful-stop hook (Manager calls it at the
  start of Stop, 10s cap, then runs the normal node stop path regardless
  — crash classification and the force-kill ladder are unaffected), and
  a remote player list (Manager polls it as the Players source for games
  with no log-based tracking; node list remains the fallback). The
  `RemoteControlContext` carries the node host, merged config, and a
  file-fetch delegate so plugins can read admin credentials from the
  game's own config file rather than duplicating them in PowerGSM.
  ContractsVersion 2 → 3: although additive, plugins distribute
  independently via plugin sources, so the gate lets a v3-requiring
  plugin fail cleanly (ContractsVersionTooNew) on a 0.5.0 manager
  instead of with a raw compile error.
- **Palworld plugin REST integration (plugin v0.2.0, requires contracts
  v3).** Stop now performs
  an announced graceful in-game shutdown via Palworld's REST API
  (announce → save → shutdown) when the REST API is enabled in Server
  Settings, and the Players tab shows the live player list (character
  name, Steam persona, SteamID64, IP) from REST — Palworld has no
  log-based player tracking. Credentials are read live from
  PalWorldSettings.ini; with the REST API disabled everything behaves as
  before.

- **Palworld plugin (`palworld`, plugin v0.1.0).** Windows + native Linux
  dedicated server via anonymous SteamCMD (appid 2394010, per-platform
  depot). Launches the UE5 Shipping-Cmd binary directly (root wrapper as
  fallback); allocator-managed listening port via `-port=` (the tuple's
  PublicPort is advertise-only); optional `-publiclobby` community-browser
  toggle. **Server Settings editor** for the single-line
  `OptionSettings=(...)` UE struct tuple in `PalWorldSettings.ini` —
  bespoke depth/quote-aware parser, unknown keys round-trip verbatim, and
  a blank fresh-install file (Palworld only creates it on first run) is
  seeded from an embedded full default tuple, so the editor works before
  the first launch. Initial field set: name, description, join/admin
  passwords, max players, player list, join/leave messages, world-save
  backups, community-browser advertise IP/port, REST API enable + port.
  Clean graceful stop (CtrlC/SIGINT — Palworld saves on console close);
  60s shutdown timeout for save flush. Log feed: stdout capture on Linux;
  none on Windows (the server writes no file log and logs via the console
  API — REST-based observability planned). RCON deliberately unsupported
  (deprecated upstream); REST API remote control planned.

## [0.5.0] - 2026-07-11

### Added

- **Stardew Valley plugin (`stardewvalley`, plugin v0.1.0).** Headless
  dedicated Stardew server via SMAPI + the `siteml/SMAPIDedicatedServerMod`
  fork (consumed as a GitHub release zip). Install chain: credentialed
  SteamCMD (appid 413150) → SMAPI installer → server-mod zip → (Windows)
  Mesa llvmpipe DLLs for GPU-less nodes. Linux launches through a small
  `/bin/sh` bootstrap that starts a shared Xvfb on display :97 and `exec`s
  SMAPI, so the spawned pid is SMAPI itself and graceful SIGINT stops work.
  Features: full mod-config instance schema (farm creation options, crop
  saver, permissions, chat-command password, …) rendered into the mod's
  `config.json` on every start via `IStartupFileProvider` (round-trip —
  hand-added/unknown fields survive); structured `[PGSM]` log parse rules
  (join/leave/chat/day rollover/remote address); **Farm Save Archive /
  Restore** — saves live under the OS user profile outside the install
  root, so the plugin exposes a Farm Backups managed directory plus
  archive/restore operations (single farm or all farms; Windows `.zip` ↔
  Linux `.tar.gz` both restore on either platform), enabling farm
  migration between nodes; fixed-port (UDP 24642) declaration so the port
  allocator blocks collisions; Discord panel context line (farm name +
  in-game date); Linux prerequisite declarations (xvfb, unzip).
- **`DownloadFileStep.ExtractOnlyPaths` (contracts + node).** Optional
  allowlist of archive entries to extract, with early-stop once all
  matches are on disk — cuts the SDV Mesa step (2 DLLs out of a ~1 GB 7z)
  from minutes to seconds. Older nodes ignore the field.
- **Install download + extraction progress (node).** DownloadFileStep now
  reports live byte progress ("Downloading file: X / Y MB (Z%)") and
  per-entry extraction progress ("Extracting: N / M files") into the
  Progress tab instead of a frozen "Unknown: 0 MB" during large
  downloads/extractions.
- **`RunProcessStep.RequiresRealConsole` (contracts + node).** Spawns
  console-UI installers (SMAPI's calls `Console.Clear()`/`ReadKey`) with a
  real invisible console and no stream redirection, which they need to
  avoid "handle is invalid" crashes.
- **`DownloadFileStep.ExtractToRelativePath` (contracts + node).** Extract
  an archive into an install-root subdirectory (e.g. SDV's mod zip into
  `Mods\`) with a path-traversal guard; composes with
  `StripTopLevelDirectory`.
- **`LaunchOptions.EnvironmentVars` (contracts + manager + node).** Plugins
  can set environment variables on the game process across all three spawn
  strategies (SDV uses it for `GALLIUM_DRIVER=llvmpipe` /
  `LIBGL_ALWAYS_SOFTWARE=1` + `DISPLAY`).
- **Linux prerequisite probes (node).** PrerequisiteProbe catalog gains
  `linux-xvfb` and `linux-unzip` (PATH-walk detection; `python3` satisfies
  the unzip prereq; non-Linux nodes report satisfied), surfaced as
  pre-install notices with `apt install` instructions.

### Fixed

- **Log viewer garbled/truncated lines (node).** Captured stdout lines are
  now sanitised (NUL bytes from UTF-16 console output decoded as UTF-8,
  ANSI/VT escapes) at both the shim and direct ingestion sites.
- **`RunProcessStep` execution (node).** Added stdin redirection and
  stdout/stderr draining, and replaced `WaitForExitAsync` with `HasExited`
  polling (the former deadlocks with redirected streams on .NET 8).
- **Shim spawn failures now carry detail.** "Shim spawn failed" responses
  include the underlying error (e.g. `posix_spawn failed … (error=2)`),
  and the Manager's executable-candidate fallback recognises the
  ENOENT-shaped shim error — so a wrong-platform first candidate falls
  through to the next instead of aborting the start.
- **Rooted executable candidates.** Plugin-supplied absolute candidate
  paths (e.g. `/bin/sh`) are no longer joined onto the install root.
- **ExeOverride persisted too eagerly (manager).** The winning executable
  candidate is now saved only after the instance survives its first 30
  seconds, so a spawn that crashes during init can't lock in a bad
  candidate that skips the plugin's list on every later start.
- **File-generation ManagedFilePicker dropdowns were always empty
  (manager).** `FileGenerationPanel` never passed a file-list provider to
  `SchemaFormBuilder`; it now lists the managed directory via the node's
  file endpoints (SDV's restore-source picker was the first casualty).
- **File-generation success message showed a literal `\u2713`.** Replaced
  with an actual ✓ (VB has no `\u` string escapes).

## [0.4.2] - 2026-07-03

### Fixed

- **Self-update dropped non-core files.** `UpdateOrchestrator`'s apply.cmd
  copied only GSM.Manager.exe + GSM.Contracts.dll, so any other file in the
  release (WebView2Loader.dll, the `runtimes\` tree, newly added
  dependencies) never reached an updated install — e.g. lo-myrealm's web
  login window failing on a missing WebView2Loader.dll. Now robocopies the
  whole extracted payload, with `/XF gsm.db gsm.db-wal gsm.db-shm
  nodesettings.json /XD .updates WebView2Data logs` so stateful files (esp.
  the live DB) can't be clobbered even if one slips into the zip. (Takes
  effect from updates run by a build containing this; the current install's
  old applier still needs WebView2Loader.dll copied in by hand once.)
- **WebSessionCaptureForm silent close.** The generic init failure set an
  error but showed no dialog, so a missing WebView2Loader.dll just flashed
  the window shut. Now surfaces the exception type + message like the
  runtime-missing path.
- **Slice-5 editor never locked (Windrose Server Settings).** The editor-tab
  reconstruction in `InstancePanel.BuildEditorTabs` rebuilt each
  `InstanceFileEditor` without copying the new `RequiresExistingFile` flag, so
  it always arrived False and the file-absent lockout never fired — the editor
  rendered a saveable defaults form for a not-yet-generated config. Now
  propagated in the reconstruction. (Shipped un-locked in the deployed 0.4.1.)
- **Edit Instance → Save crash "Value cannot be null. (Parameter 'key')".** The
  Slice-5 startup-files Notice descriptor carried no `.Key`, so once the
  instance's ConfigJson held values `SchemaFormBuilder`'s ValueExtractor hit
  `Dictionary.ContainsKey(Nothing)` on save. Gave the Notice a key
  (`_startupFilesNotice`) and hardened the extractor to skip Notice / keyless
  fields outright, so no plugin-supplied descriptor can null-crash the save.

## [0.4.1] - 2026-07-02

### Added

- **User & developer documentation suite.** New root `README.md` (project
  overview, quick system requirements, doc index) plus `docs/user/`
  (`prerequisites.md`, `node.md`, `manager.md`, `plugins.md`) and
  `docs/developer/` (`plugin-authoring.md`, `api-protocol.md`) — covering
  install/setup of Node (Windows + Linux) and Manager, every Manager feature,
  per-plugin setup for all four shipped games and lo-myrealm, plugin authoring
  against `IGamePlugin`/`IUtilityPlugin`, and the Manager↔Node REST protocol
  including guidance for building alternative (e.g. web-based) Managers.

- **Windrose — best-effort install-pane warning on pre-0.4.1 builds.** Plugin
  reads the Manager's own `InformationalVersion` by reflection (entry assembly;
  no contract dependency) and, when it's below 0.4.1, prepends a Warning notice
  to `GetPreInstallNotices` about the same-IP session collision. Manager-only
  signal (can't see the node's version from a plugin), so the body says to
  update the node too; self-suppresses at 0.4.1+.

- **Config-file-presence UX (Slice 5).** Surfaces the "config not generated
  yet" state for plugins whose settings are written into the game's own file
  at launch (`IStartupFileProvider`), so a defaults-only partial write can't
  crash the server. Additive `InstanceFileEditor.RequiresExistingFile`
  (default False, no `ContractsVersion` bump): when True and the target file
  is absent, the structured editor (e.g. Windrose Server Settings) locks —
  fields + Save disabled, "start the server once to generate it, then Reload"
  hint — instead of rendering a saveable defaults form. Edit Instance shows a
  Notice banner that these settings apply from the SECOND launch (the game
  generates the file on the first), gated by a per-instance readiness flag in
  `AppSettings` (`instance.{id}.startupFilesReady`) that flips once every
  declared startup file exists on the node; kept out of instance ConfigJson so
  a config-edit save can't clobber it and it never rides in `CustomFields`.
  Windrose sets `RequiresExistingFile = True` on its Server Settings editor;
  other plugins default off and are unchanged. Motivated by a live crash: a
  partial ServerDescription.json (missing server-owned Version / DeploymentId /
  PersistentServerId / P2p*) made the server fail vendor registration fatally.

### Fixed

- **Node `EventStore` — two players from one IP no longer collapse into a
  single session.** `FindExistingSession`'s RemoteAddress fallback claimed an
  existing same-IP session even when the incoming event carried a *different*
  strong id (CharacterId / PlatformUserId), so a second player joining from the
  same public IP (no port to tell them apart) overwrote the first player's
  identity in `/players` and the persisted `players` row. The addr fallback now
  declines a match whose cid/pid conflicts with the incoming one, keeping
  same-IP players distinct while preserving addr-first identity enrichment (an
  addr-only session with no strong id still matches, so LO's
  NotifyAcceptedConnection→identity sequence is untouched). Node-side only; no
  contract change (`ContractsVersion` stays 2). Surfaced by Windrose (bare-IP
  `NetAddress`); Last Oasis (IP:Port, unique per connection) and Conan (name in
  the disconnect line) were never exposed. Disconnect correlation is unchanged
  for all three — the guard only fires on a cid/pid conflict, and leave lines
  that carry only an address have no strong id to conflict.

## [0.4.0] - 2026-07-01

### Added

- **Windrose Slice 4 — player-event tracking + server-state surface.** Plugins
  only (`GSM.PluginsSource\WindrosePlugin.vb`); `GSM.Contracts`, Node, and
  Manager untouched. Verified against real `Windrose R5.log` captures
  (UE5.6.1 / project R5).
  - **Two halves, both required (matching Conan / LO / Factorio).** The plugin
    shipped through Slice 1 with `CreateLogParser` returning `Nothing`, so
    player join/leave never reached the Manager's History — the node populated
    `/players` but nothing turned that into History rows.
    - **Node-side `GetLogParseRules`** — declarative regex rules the node's
      `EventStore` applies to keep `/players` + server-state authoritative
      (survives Manager offline). Player connect binds `AccountId` + IP on one
      `VerifyUeCredentials` line; roster-dump line enriches the in-game name;
      farewell / disconnect resolve the leave. `AccountId` (32-char hex) maps to
      **CharacterId**, not PlatformUserId — the node's `players` table is keyed
      by `character_id` and `PersistPlayer` no-ops on an empty one, so a
      pid-only session was tracked live but never persisted. Server-ready +
      world identity land via `TileLoaded` (MapPath, short TileName, IslandId →
      TileId); listen port, max tick rate, and shutdown reason are harvested as
      `Custom_*` fields; the exit-reason rule ignores the redundant second
      `EngineExit()` line.
    - **Manager-side `WindroseLogParser` (`ILogParser`)** — drives live
      join/leave into `PlayerActivity` (History) + join/leave notifications.
      Join anchors on the canonical `LogNet: Join succeeded: <name>` (same line
      Conan uses). The leave lines carry only `AccountId`, so the parser
      harvests an `AccountId → Name` binding from the roster-dump lines and
      resolves leave names through it (same shape as Conan's IP→name binding,
      keyed on AccountId since Windrose leave lines have no IP).

- **Phase 8-2 slices 7b / 7c — shim + NodeSetup co-update (the Manager can now
  push a new shim or a new NodeSetup to a node, not just the node binary).**
  Node + Manager; `GSM.Contracts` untouched (the staged-binary transport already
  carried an arbitrary `target` + `version`).
  - **Node — three target *shapes* (`GSM.Node\SelfUpdate.vb`).** `ResolveTarget`
    now maps `node` / `nodesetup` / `shim` to a shape: **SwapWithSurvivor**
    (node — stage `.new`, a survivor swaps it over live and relaunches on exit,
    as before), **SwapInPlace** (nodesetup — stage `.new`, the node swaps its
    *idle* NodeSetup binary in place keeping `.old`; the node does **not**
    bounce, and there is no auto-revert — a bad NodeSetup is only exercised on
    the next node apply, `.old` kept for manual restore), and
    **VersionedInstall** (shim — the verified bytes are installed straight into
    `GSM.Shim\<version>\GSM.Shim[.exe]` at commit; no `.new`, no swap, no exit).
    `apply-update` dispatches on shape and only stops the host for the node
    target (`ApplyResult.RequiresExit`).
  - **Shim place is lock-safe, then fail-clean.** A brand-new version folder is
    conflict-free. A same-version RE-push (e.g. replacing a corrupted shim)
    tries to free the destination — delete if idle, else rename the live exe
    aside (`*.superseded-*`, swept on next node start) — and if the OS pins it
    (a running shim the OS/AV/indexer won't release) the **commit fails cleanly
    with 409** ("restart the instances on that shim version, or push a higher
    version"), leaving the verified `.part` for the sweep. Nothing is ever
    half-applied or torn. Renaming an in-use file within a volume is usually
    permitted on Windows but is treated as best-effort, not relied upon.
  - **Shim version is path-sanitized** (`[0-9A-Za-z.+-]`, no `..` / separators);
    a shim push without a usable version is refused at `begin` (400).
  - **Manager — Target selector wired (`NodeUpdatesForm` + `NodeReleaseSource`).**
    The Nodes → Update Nodes dialog's Target dropdown (Node / Shim / NodeSetup)
    now drives the push. Node stages → applies → polls for relaunch; shim /
    nodesetup stage → apply → "Installed" (the node stays up). Manual-pick and
    feed modes both source the right binary from the **same node zip** — node /
    nodesetup at the zip root, shim from its `GSM.Shim\<ver>\` folder — with the
    download+extract reused across targets for a (version, rid). Shim versions
    are stripped at `+` so the node's version folder parses. Confirm prompts
    describe the actual per-target effect (only Node goes briefly offline).

- **Phase 8-2 slice 8 — self-update health gate + auto-rollback (a bad node
  update now reverts itself instead of bricking the node).** Three layers of
  defense around the slice-6 / 7a stage → apply → relaunch path; all node-side,
  `GSM.Contracts` untouched.
  - **8a — commit-time OS-match guard (`GSM.Node\SelfUpdate.vb`).** Before a
    staged upload is promoted to `.new`, the node sniffs its magic bytes
    (`0x7F ELF` / `MZ` PE) and refuses (422, deleting the `.part`) a binary that
    is a recognized executable for the *wrong* OS — so a wrong-platform push can
    never reach the swap. Mirrors the Manager-side picker guard; only a definite
    mismatch is blocked (an unrecognized format passes through to the health
    gate). The Manager already blocks this at file selection, so the node guard
    is defense-in-depth for direct-API / future-bug callers.
  - **8b-1 — NodeSetup survivor health gate + revert
    (`GSM.NodeSetup\SelfUpdateApply.vb`).** On the Windows-service /
    Windows-bare / Linux-bare relaunch paths, after applying an update the
    survivor polls `http://127.0.0.1:<port>/api/version` (port from
    `nodesettings.json`; unauthenticated) for up to 60 s. If the new node never
    answers it **rolls back**: stop the bad node (`sc stop`, or kill just the
    node PID for direct launch — shims/games survive), quarantine the bad binary
    as `.failed`, restore `.old`, relaunch the previous binary, and re-confirm
    it. New exit codes 5 (rolled back) / 6 (rollback itself failed).
    `ServiceManager` gained `StopWindowsService`.
  - **8b-2 — systemd survivor health gate + revert (`GSM.NodeSetup\
    ServiceManager.vb` unit + `GSM.Node\NodeProgram.vb`).** The generated unit
    becomes `Type=notify` (the node already sends `READY=1` via `UseSystemd()`,
    so a binary that starts but never goes ready now counts as a *failed* start
    — the "hung but alive" case `Type=simple` misses), gains
    `StartLimitIntervalSec=200` / `StartLimitBurst=5` to bound the loop, and its
    `ExecStartPre` becomes apply-**or**-revert: applying a `.new` drops a
    `.update-pending` marker that the node deletes once it has been healthy for
    15 s; a marker that outlives a start (with a `.old` present) triggers a
    rollback — quarantine the bad binary as `.failed`, restore `.old`, clear the
    marker. Survives crash / reboot / power-loss mid-update.
  - **Deployment note:** the systemd half lives in the unit file, so an existing
    systemd node only gets 8b-2 once its unit is regenerated (re-run NodeSetup
    install / rewrite `gsmnode.service` + `daemon-reload`); the marker-clear half
    ships with the next node binary. Both halves are needed.

- **Phase 8-2 slice 7a — Manager → node binary push (the Manager can now
  *pitch* an update to a node, not just catch one).** Slice 6 gave the node the
  receive / apply / survive machinery; 7a is the Manager side that drives it.
  **Verified working end-to-end against the live Linux node** (stage → apply →
  survivor swap → relaunch → shim re-adopt, game PID unchanged). Manager-only —
  `GSM.Contracts` untouched.
  - **Push transport on `NodeHttpClient`** (concrete-only, deliberately *not* on
    `INodeClient`, following the `TryGetCachedVersion` precedent — no full-
    solution Contracts rebuild; promote later if a second caller needs it).
    `StageBinaryAsync(target, localFile, version, ct)` SHA-256 + sizes the file,
    then walks the node's `staged-binary` endpoint — `begin` → `chunk*` (8 MB,
    append-only, offset-validated; a 409 re-seeks to the node's reported offset
    and resumes) → `commit` (the node re-verifies size + SHA-256 over the whole
    file before renaming `.part` to the target's `.new`). Runs on a one-shot
    infinite-timeout `HttpClient` (same rationale as `UploadFileAsync`) so a
    tens-of-MB push isn't chopped by the shared 30 s timeout.
    `ApplyUpdateAsync(target, ct)` POSTs `apply-update` and returns the survivor
    (202) or throws `NodeApiException(Conflict)` when nothing is staged. Sourcing
    is decoupled: the caller supplies a local file; release-feed download/verify
    is a later slice.
  - **Permanent UI: Nodes → "Update Nodes…"** (`NodeUpdatesForm`, modelled on
    `PluginUpdatesForm`). A checkbox list of every configured node — display
    name, address, current build + platform + reachability (probed concurrently,
    each ~8 s-bounded so one unreachable node doesn't stall the list), and a
    per-node Result. Multi-select, then **Update…** stages → applies → polls
    `/api/version` until the node relaunches (≤60 s), writing the outcome into
    each row. Every node is handled **independently** — an unreachable, detached,
    or failing node never blocks the rest.
  - **OS-match guard.** The picked file's actual format is sniffed from its magic
    bytes (`0x7F ELF` → Linux, `MZ` → Windows), independent of filename, and must
    match each target node's reported OS. A **mixed-platform selection pops one
    file selector per platform present** (a Linux binary *and* a Windows binary)
    and routes each node to the build it can run; a wrong-format pick offers
    Retry/Cancel; an unrecognised file warns before proceeding; a node whose OS
    was never reported (`Unknown`) is skipped in a typed push.
  - **Manual push is a first-class, permanent path** — the operator may push a
    release build *or* their own build; they own the versioning and the
    consequences, with the node's commit-time SHA-256 + size check and the
    survivor relaunch as the integrity backstops. The optional Version field is
    prefilled from the file's `ProductVersion` when blank (metadata for the node
    target; structural for the shim target later).
  - **Target selector (Node / Shim / NodeSetup)** is present from day one with
    only **Node** wired; Shim and NodeSetup snap back with a "later version"
    note, making the **per-target separation** explicit. Node, shim, and
    NodeSetup updates are deliberately **decoupled from the Manager's own
    self-update** (Help → Check for updates) **and from each other** — multi-node
    fleets routinely have a node that's offline, mid-session, or not one the
    operator wants to touch yet.

- **Phase 8-2 slice 7-source — feed-driven node sourcing (push *the latest
  release* to a node without hand-picking a file).** Builds on 7a's manual
  push: the Manager can now show what the newest release is and source the
  per-platform node binary from the GitHub release feed itself. Manager-only —
  `GSM.Contracts` untouched.
  - **7-source-a — Latest column.** `NodeUpdatesForm` resolves the newest
    release once per load (the background `GitHubReleaseChecker`'s persisted
    result, or one bounded live check if nothing's cached) and adds a **Latest**
    column comparing each reachable node's installed build against it
    (`SemanticVersion`): a node behind the release reads `X (update)` and its row
    is tinted; a current node reads `current`. The status line gains a
    `· latest release X` note.
  - **7-source-b — one-click feed sourcing.** A **Latest release** checkbox
    swaps the per-platform file picker for a download. New `NodeReleaseSource`
    (`GSM.Manager.Core`) takes a platform + release tag, finds that release's
    `PowerGSM-Node-{ver}-{rid}.zip`, downloads it, **SHA-256 verifies it against
    the release `SHA256SUMS`**, extracts the inner `GSM.Node[.exe]`, and hands
    back the local path — which then feeds the *same* stage → apply → relaunch
    loop the manual push uses. One download per platform is cached and shared
    across same-platform nodes in a batch (a 5-Linux-node update fetches once);
    the cache lives under `<install>\.node-updates\<ver>\<rid>\` and re-checks
    `File.Exists` before reuse. Trust chain: release `SHA256SUMS` → verified zip
    → extracted binary → the existing push (which re-SHAs on the wire) → the
    node's commit-time re-verify. Unknown-platform nodes have no release asset to
    match and are skipped; the manual file-pick path is unchanged and remains
    first-class. The node zip also carries `GSM.Shim/` + `GSM.NodeSetup`, so the
    later shim (7b) / NodeSetup (7c) co-updates source from the same download.
  - **Shared release-asset helpers.** The asset-fetch / find-URL / parse-sums /
    SHA-256 / download-with-progress helpers (and the `ReleaseWithAssets` /
    `ReleaseAsset` DTOs) that drive the Manager's own self-update were lifted out
    of `UpdateOrchestrator` into a shared `ReleaseAssetHelpers` (`ReleaseAssets.vb`)
    so node sourcing and Manager self-update share one verified-download path.

- **Phase 8-3 — shim rediscovery + `node.db` hardening (`node.db` becomes a
  cache, not a source of truth).** A lost or corrupt `node.db` no longer orphans
  running games. **Built clean on all targets; runtime verification deferred
  (build-only this pass).** See `Phase8-3_Plan.md`.
  - **Shim rediscovery sweep** (`ProcessManager.SweepAdoptLiveShims` +
    `EnumerateShimEndpoints` + `TryLeanAdoptShim`, called after `AdoptSnapshots`
    in `NodeProgram`). The shim endpoint is a pure function of the instance id
    (`pipe:powergsm-shim-<id>` / `unix:<dataDir>/shims/<id>.sock`), so after the
    snapshot pass the node enumerates the OS shim namespace (named pipes on
    Windows, the socket dir on Linux), probes each
    (`ShimSession.ProbeEndpointAsync` — connect, handshake, read, close;
    time-boxed, never throws), and **lean-adopts any live shim whose id isn't
    already adopted**. The snapshot pass runs first, so snapshot-backed
    instances keep their full recovery payload; the sweep only fills the gaps (a
    wiped/corrupt `node.db`, or a snapshot row that failed to adopt while its
    shim is alive). The sweep always runs and no-ops cheaply on already-adopted
    ids.
  - **Shim reports its own identity + tail paths in the handshake.**
    `HelloAckMessage` gains `InstanceId` (the sweep recovers the true id from
    the shim — `SanitizeId` is lossy, so the pipe/socket name can't be reversed)
    and `LogFilePaths`; `SpawnSpec` gains `LogFilePaths`. The node hands the
    shim the resolved tail paths at spawn; the shim remembers them and echoes
    them on every later handshake. Both additions are append-only (no
    `ProtocolVersion` bump). A **pre-8-3 shim answers without them and is skipped
    by the sweep** (logged "older shim") — only relevant on the `node.db`-loss
    path, since the snapshot path still adopts older shims.
  - **Lean adopt** (`TryLeanAdoptShim`) registers EventStore with an empty rule
    set (so the instance is a valid push target — `UpdateParseRules` ignores
    unregistered instances) with `hydrateState:=True`, recovers the log paths the
    shim echoed and starts file tailers (`skipResume:=True`, switching
    `CaptureStdout` off since the file is authoritative), and sets
    `CrashPolicy = NeverRestart` (there's no `StartInfo` to rebuild a
    `SpawnSpec`). Net: a lean-adopted game is statused, stoppable,
    stdout/exit-relayed, and file-tailed — so player/chat/server-state tracking
    resumes go-forward — and the Manager's existing stream-health reconnect
    re-pushes the real parse rules within ~3s. **Residual gap (by design):** no
    crash-restart until the instance is fully restarted (closing it needs the
    shim to echo the full `SpawnSpec` — deferred Tier-3 work). Adopt tailers
    start from end (`skipResume`), so recovery is go-forward, not a historical
    rebuild of the current player list — identical to a normal node restart.
  - **Corrupt-`node.db` self-heal** (`NodeDatabase`). The DDL body moved to
    `EnsureCreatedCore`; `EnsureCreated` wraps it and, on `SqliteException` with
    `SqliteErrorCode` 11 (`SQLITE_CORRUPT`) or 26 (`SQLITE_NOTADB`) **only**,
    clears the connection pool, renames the bad file aside
    (`node.db.corrupt-<timestamp>`, surfaced via `LastCorruptionBackup`), drops
    the `-wal`/`-shm`/`-journal` sidecars, and recreates empty — then `Main`
    logs the reset at Warning once the logger exists. Any other SqliteException
    (locked, busy, readonly, …) is not corruption and propagates unchanged.
    Previously a corrupt file threw out of `Main` before `app.Run()` and
    crash-looped the node (under systemd, until StartLimit gave up). Combined
    with the sweep: corrupt `node.db` → reset empty → sweep re-adopts the live
    shims → nothing orphaned.

- **Phase 8-2 (slice 6) — node self-update (staging + external swap
  survivors).** The node can now receive a new binary, stage it, and bounce
  itself into it without losing the games it supervises — the swap is performed
  by something that *outlives* the node, since a process can't replace the
  image it's executing. **Verified end to end on all four survivor paths —
  Linux (systemd + bare-exe) and Windows (service + bare-exe)** — game PID
  unchanged across the bounce in each. Manager-driven push, version detection,
  and NodeSetup/shim co-update are slice 7. See `Phase8-2_Plan.md`.
  - **Chunked staging endpoint** (`SelfUpdate.vb`, `SystemEndpoints`) —
    `POST /api/system/staged-binary/{begin,{uploadId}/chunk,{uploadId}/commit}`:
    a begin/chunk/commit session streams the binary append-only to a temp
    `.part` (offset-validated per chunk, so it resumes from the last good
    offset), then commit verifies SHA-256 + declared size over the whole file
    and atomic-renames `.part` → `GSM.Node.new` beside the live binary
    (`GSM.Node.exe.new` on Windows), `+x` on Linux. Bearer-gated like every
    `/api/*` route; the chunk endpoint lifts its own request-body cap. The
    chunk shape (rather than a single streamed PUT) keeps each request under
    Kestrel's body limit and gives the Manager progress/resume/cancel for free.
    The node only verifies the bytes the Manager declares — the trust boundary
    is the Manager's verified push (slice 7), not the node.
  - **Graceful update-exit** (`POST /api/system/apply-update`) — refuses with
    409 if nothing is staged; otherwise picks a survivor and exits through the
    *normal* graceful path (`IHostApplicationLifetime.StopApplication()` →
    `ApplicationStopping` → `DetachShimsForShutdown`, so the shim-backed games
    survive), never a hard `Environment.Exit`. The 202 flushes before the host
    tears down.
  - **Universal-fallback survivor model.** The node picks its survivor at exit
    from the supervision signal it already has
    (`SystemdHelpers.IsSystemdService()`): **only a node actually running under
    systemd** defers the swap to the unit's new idempotent `ExecStartPre`
    (`.new` → live, keep `.old`, re-`chmod +x`, guarded so it's a no-op on a
    normal start) and the relaunch to `Restart=on-failure`; **everything else
    — Windows service, Windows bare, *and Linux bare* — spawns a detached
    `GSM.NodeSetup --apply-update --wait-pid <self>`** (`SelfUpdateApply.vb`)
    that waits for the node PID to die, swaps `.new` over the live binary
    (keeping `.old`, retrying on a transient lock), and relaunches via
    `sc start` if the service is installed or a direct exec otherwise. This
    closes the gap where a Linux node run as a plain foreground exe (no systemd)
    would otherwise stage an update with no one to apply it. On Windows the
    detached spawn uses `CreateProcessW` with
    `CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP`
    (falling back to `Process.Start` if breakaway is refused) so the SCM/job
    can't reap the survivor with the service.
  - **Exit-code predicate.** The node exits non-zero (code 10) *iff* it's
    relying on systemd's `Restart=on-failure` to relaunch it; otherwise it
    exits 0, because NodeSetup owns the relaunch and a non-zero exit from a
    Windows service would race SCM recovery. (A clean exit, not a signal, so
    journald doesn't read the systemd case as a crash.) The flag is read from a
    reference captured *before* `app.Run()` — the host's DI container is
    disposed when `Run` returns, so resolving the service afterwards throws and
    would silently drop the node back to exit 0.
  - **`--self-update-dry-run` harness** (`SelfUpdateDryRun.vb`) — exercises the
    whole path with no Manager and no hand-driven HTTP, modelled on the
    `--shim-self-test` / `--shim-reconnect-test` harnesses. Stages the running
    binary through the real begin/chunk/commit code, then (unless
    `--stage-only`) POSTs apply-update to the running node over loopback
    (reading port + token from `nodesettings.json`). Transcript to console and
    `self-update-dryrun-result.txt`; the survivor leaf logs to
    `nodesetup-apply.log`.

- **Phase 8-1 (in progress) — per-instance shim supervisor (Strategy A).**
  Groundwork so a Node restart stops severing a stdout-piped game's output:
  each Strategy-A instance now runs its game under a tiny per-instance
  `GSM.Shim` process that owns the game's stdin/stdout/stderr and relays them
  to the Node over a named pipe (Windows) / Unix socket (Linux) — the Node
  never holds the game's pipes. **Build-verified on Windows; the live game
  test runs on the Linux node.** See `Phase8_Plan.md`.
  - New projects `GSM.Shim.Protocol` (versioned, append-only frame protocol)
    and `GSM.Shim` (self-contained .NET 8, native spawn — `CreateProcessW` on
    Windows, `posix_spawn`/`pipe2` on Linux, the Linux path starting the game
    in a new session via `POSIX_SPAWN_SETSID` so a terminal Ctrl+C can't
    reach it; passes `GSM.Shim --self-test` on the Linux node);
    the shim binary deploys side-by-side at `GSM.Shim\{version}\`.
  - Node-side `ShimSession` client + `ProcessManager` routing: Strategy A
    (stdout-captured) instances start / stop / restart through the shim,
    with stdout lines flowing into the same ring buffer / EventStore as
    before. Strategy B/C (Windows hidden-console games like LO and Factorio)
    also route through the shim now — spawned with their own hidden console
    (cmd-wrapped for Factorio); the Node tails their log files as before.
    Optional `nodesettings` `Node:DisableShim` kill-switch
    (default off). New optional `InstanceStatusResponse.SupervisorPid`
    surfaces the shim PID; the reported `Pid` stays the game.
  - **Restart-survivable (Strategy A).** The shim keeps its game + output
    ring alive across a Node disconnect and replays buffered output to the
    next Node that reconnects; on startup the Node re-adopts shim instances
    by reconnecting to the live shim (by saved endpoint) rather than
    re-deriving a process handle, and sends `Detach` on a clean shutdown so a
    deliberate Node restart leaves the game running. The reconnect mechanism
    is proven on Windows (`--shim-reconnect-test`: same game pid + shim pid +
    replayed output across a detach/re-adopt); end-to-end restart survival
    with a live game is verified on the Linux node.
  - **Graceful stop + Ctrl+C.** Stopping a shim-mode game now sends the real
    shutdown signal first — `CTRL_C_EVENT` (Windows, via `GSM.CtrlCSender`) /
    `SIGTERM` (Linux), delivered straight to the game by PID — and only
    escalates to a hard kill if it doesn't exit in time; the shim flushes the
    game's final output before reporting the exit, so a clean shutdown's last
    log lines (world save, "shutdown complete") aren't lost. Separately,
    **Ctrl+C in the Node console once again closes the Node** (it had been
    suppressed so a game-stop signal couldn't bounce back and kill the Node):
    the suppression is now scoped to just the moment the Node is firing that
    signal, so a user Ctrl+C shuts the Node down gracefully — detaching the
    shims so the games keep running, and the next start re-adopts them.

- **Startup config render (`IStartupFileProvider`).** A third
  field→runtime bridge alongside launch args and user-triggered file
  generation: the Manager renders selected instance-config values into a
  game's **own** config file just before launch, preserving everything
  else in the file. Closes two gaps — file-only games (no launch args)
  couldn't receive an allocator-assigned port, and arg-garbling text
  values (Conan's server name / password) corrupted through the command
  line but read clean from a config file. See
  `StartupConfigRender_Plan.md`.
  - **Contract + Manager hook** — new opt-in side-interface
    `IStartupFileProvider` (`GetStartupFiles` / `RenderStartupFile`) in
    `GSM.Contracts`; `ContractsVersion` unchanged at 2 (the still-in-dev
    v2 surface was never released, so the render folds in without a
    bump; adopting plugins declare `requiresContracts="2"`).
    `InstanceManager.ApplyStartupFileRendersAsync` runs inside
    `StartInstanceAsync` after the config-layer merge and before the
    start request, reusing the file editor's node download/upload
    endpoints (404 → empty, write only on a diff). **Proceed-and-warn:**
    a render read/write hiccup logs a warning and the launch continues
    with the file's last values. **Single-ownership:** a value rendered
    at start is removed from the file-editor schema for the same file,
    so the editor and the Configuration tab never fight.
  - **Windrose** — `UseDirectConnection` + `DirectConnectionServerPort`
    (`IsPort`, now allocator-managed) moved from the
    `ServerDescription.json` editor to the Configuration tab and
    rendered into `ServerDescription_Persistent` at launch (skipped on
    first launch so the server creates the file; port stamped only in
    direct mode). Verified live: a freshly-allocated port bound by the
    engine in direct mode with no config on the command line.
  - **Conan Exiles** — `ServerName` (off the launch URL) and
    `ServerPassword` (off the Engine.ini editor) are now Configuration
    fields rendered into `Engine.ini` `[OnlineSubsystem]`; the
    "Network (Engine.ini)" structured editor tab is removed (raw
    `Engine.ini` still editable via the `.ini` browser). `ServerName`
    always writes (blank → default name). `ServerPassword` is
    **set / keep / clear** via a new **Clear server password** checkbox:
    a non-empty field writes it, blank-with-checkbox-unticked preserves
    the existing file value, blank-with-checkbox-ticked writes an empty
    password (open server). The INI section-writer was extracted into a
    shared `WriteIniSection` used by both the editor and the render.
    **Migration:** a Conan instance whose password was set via the old
    Engine.ini editor keeps that password (blank field + unticked
    checkbox preserves it); re-enter it on the Configuration tab only if
    you want PowerGSM to manage it going forward.

- **Phase 5n — Notification scope rework (UI + schema).** The
  `NotificationsForm` scope section is rebuilt around a
  **union-of-includes** model across four dimensions — Node,
  Installation, Instance, and Instance-set — replacing the old
  two-widget AND-narrowing whose "leave deselected = all" rule was
  invisible and whose instances panel read as broken on open. An
  instance is in scope if it matches **any** checked dimension; only
  the all-empty state means "all instances." Sets reuse the existing
  per-instance `InstanceSetTag` (no new entity, no set-management UI —
  the form only consumes the tag). See `Phase5n_Plan.md`.
  - **Schema + editor (5n-1)** — `NotificationDestinationEntity` gains
    `NodeFilterJson` and `InstanceSetFilterJson` (additive TEXT
    columns; forward-only migration `NotificationScopeDimensions`);
    `DestinationEdit` round-trips all four filters (the set filter is
    `Ordinal`, matching `RuleScope.InstanceSet`; the ID filters stay
    `OrdinalIgnoreCase`). The editor is a four-section collapsible
    accordion (Nodes / Installations / Instances / Instance sets), each
    header carrying a live "N of M selected" summary, with a persistent
    "Matches N of M instance(s)" readout. The scope sections, Events,
    and Visibility now share one dock-stacked column that grows to fit
    its content and is scrolled by the details panel as a whole: each
    section's list grows to show every row (no nested list scrollbar),
    and the lists forward the mouse wheel to the panel so scrolling
    works with the pointer over a section. *At this slice the new
    Node/Set filters persisted but stayed inert — runtime still used
    the prior AND on the original two filters; 5n-2 wires them in.*
  - **Runtime union + scope fan-out (5n-2)** — both Discord transports
    (webhook and bot) now evaluate the four filters as a
    **union-of-includes** at send time, replacing the old AND-narrowing
    on the original two filters: an event is in scope if it matches
    **any** populated dimension, and the all-empty destination still
    matches everything. Because installation-level events (the three
    Update events, emitted with no instance) carry no instance or set
    tag of their own, they are **fanned out** at emit time across every
    instance under the installation — so a destination scoped to an
    instance, or to an instance-set tag carried by one of those
    instances, now receives that installation's update notifications
    instead of silently missing them. The model is "an event carries
    every scope identifier it relates to; a filter matches on
    intersection." `NotificationContext` gains `ScopeInstanceIds` /
    `ScopeInstanceSetTags` (matching-only collections, populated in the
    emitter) and `NotificationTokens` gains `InstanceSetTag`
    (`{InstanceSetTag}` is now a substitutable template token); all
    three are additive, so `ContractsVersion` is unchanged. *The
    `{InstanceSetTag}` token stays single-valued and renders empty on
    installation-level update events (no single instance to name) —
    fan-out affects matching, not substitution.* **Back-compat:**
    destinations saved before the rework that relied on the old
    AND-narrowing are left as-is and must be reconfigured once against
    the union model.
  - **Panel ID surfacing (5n-3)** — `NodePanel`, `InstallationPanel`,
    and `InstancePanel` show their `NodeId` / `InstallationId` /
    `InstanceId` as a dim sub-label with a right-click **Copy ID**, via
    a shared `PanelIdLabel` helper, so the GUIDs behind history/log
    lines are identifiable when display names aren't.

- **Phase 7 — Utility plugins (Manager-side, headless).** A second
  plugin kind alongside game plugins: `IUtilityPlugin` plugins
  aren't tied to a game and don't manage installations or
  instances — they react to Manager-wide events and act through a
  capability-gated context. They ride the same Phase 6 pipeline
  (inline manifest, Roslyn compile, sources/stage/consent/install,
  hot-reload) with two extra rules: a `<plugin>` manifest is
  REQUIRED (no legacy leniency) and `IUtilityPlugin.PluginId` must
  match the manifest id. Contracts surface lives in the new
  `GSM.Utility` namespace; `ContractsVersion` bumped 1 → 2 (a
  documented exception to the breaking-only rule — a new
  plugin-facing surface bumps so plugins that require it fail fast
  on an older Manager; v1 game plugins load unchanged). See
  `Phase7_Plan.md`.
  - **Discovery + Status (7-1)** — utility plugins are discovered
    in the same per-file compile; the Plugin Status list renames
    "Game ID" → "Plugin ID" and adds a **Kind** column (Game /
    Utility).
  - **Host + event dispatch (7-2)** — a `UtilityPluginHost`
    (driven by a new `PluginRegistry.Reloaded` event) gives each
    plugin lifecycle (Initialize/Shutdown) and a per-plugin bounded
    queue drained on a background task, so a slow or throwing
    plugin can never block the Manager. Repeated failures suspend a
    plugin's delivery (shown as "Suspended" in Status) until the
    next reload. Events tap `NotificationEmitter.Emitted`:
    PlayerJoin / PlayerLeave / InstanceStarted / InstanceStopped /
    InstanceCrashed. Plugins declare interest via `SubscribedEvents`
    (ChatMessage / ServerStateChange delivery is deferred to 7-4a).
  - **Capabilities + consent + gating (7-3)** — plugins declare
    capabilities in a manifest `requires="..."` attribute
    (`events`, `identity-read`, `identity-write`, `notifications`,
    `network`, `config`, `web-capture`); the install/update consent
    prompts list them, and the runtime context throws a
    descriptive error on undeclared access. Real gated services:
    broadcast notifications, identity resolve/contribute (through
    IdentityResolver), a per-plugin config bag, and a **Configure...**
    dialog in the Status tab (renders `GetConfigSchema()` via
    SchemaFormBuilder). Honest scoping: this is informed consent +
    convenience-API gating, NOT a sandbox.
  - **Embedded-browser session capture (7-3 r2, Decision 7a)** —
    `IUtilityContext.CaptureWebSessionAsync` shows a Manager-owned
    **WebView2** dialog where the user performs a real third-party
    login (genuine portal + Steam Guard; PowerGSM never sees
    credentials), then harvests the resulting session cookies via
    `CookieManager.GetCookiesAsync` — HttpOnly cookies included.
    Runs on a dedicated STA thread; degrades gracefully when the
    WebView2 runtime is absent; browser state lives in a wipeable
    per-plugin folder. `Microsoft.Web.WebView2` is a Manager-only
    dependency — plugins never reference it.
  - **Static enforcement ratchet (7-3b)** — because the Manager
    compiles every plugin, two cheap gates: a capability-declaring
    plugin without `network` is compiled WITHOUT the `System.Net.*`
    reference assemblies (undeclared network use becomes a compile
    error naming the capability; game plugins are unaffected), and
    a stage-time `PluginSourceAudit` flags DllImport / Process.Start
    / reflection / undeclared-network as advisory notes in the
    install consent. Determined obfuscation is out of scope by
    design.
  - **Event tap — identity-rich player/chat/state events (7-4a)** —
    PlayerJoin/PlayerLeave events are now sourced from the
    Manager's identity-resolution path rather than the notification
    emitter, so they arrive carrying the fully-resolved
    `CharacterId` / `PlatformUserId` / `Platform` / `CharacterName`
    plus the instance's `SessionIdentity` (`lastoasis:{realm}:{tile}`
    on LO, a `{gameId}:{instanceId}` fallback elsewhere) — not just
    a display label. `ChatMessage` (from the Manager's chat mirror,
    ~5s cursor-deduped) and `ServerStateChange` (LO tile
    bind/unbind) delivery are added, completing the kinds deferred
    in 7-2. `UtilityEvent` gains `SessionIdentity` + `CharacterName`
    (additive; no `ContractsVersion` bump). `PlayerName` now carries
    the RAW persona with the resolved character name in the new
    field. One behaviour change: synthetic leaves (instance-stop
    flush, Manager-downtime reconcile) now reach utility plugins —
    correct for programmatic consumers, where the emitter suppressed
    them only to avoid Discord noise. Game plugins are untouched
    (the LO/Conan/Factorio source is source-compatible; only the
    Manager plumbing moved).
  - **lo-myrealm reference plugin (7-4b)** — first first-party
    utility plugin (ships in `GSM.PluginsSource` as
    `LoMyrealmPlugin.vb`, id `lo-myrealm`; renamed from the working
    title "SteamSessionPlugin" once it was clear the held session is
    a myrealm/GPORTAL portal session and the logic is LO-specific).
    On a PlayerJoin/PlayerLeave whose LO CharacterId the resolver
    can't yet name, it reads the authoritative current character
    name from the myrealm rename page
    (`/realm/{realm_id}/Characters/{character_id}/Rename`, realm_id
    off the event's SessionIdentity) and contributes it back through
    the resolver — filling the naming window before the first
    Persisting tick. **Verify-on-join** (default on) re-reads on
    join to catch portal renames (never prompts, ≥5 min/character).
    A one-shot **"Sign in at next plugin reload"** config flag
    triggers the login manually, since the automatic prompt only
    fires on a genuine naming gap (which never occurs on a realm the
    resolver already fully knows). Expiry is handled structurally
    (no-redirect GET; redirect or served sign-in page → invalidate +
    notify once → next gap re-prompts), so the unknown session
    lifetime is moot.
  - **Shared web-session store (7-5)** — session capture/persist/
    expiry moves out of the plugin and into the Manager so future
    portal plugins don't each reimplement it. Two additive
    `IUtilityContext` members (gated by `web-capture`):
    `GetOrCaptureWebSessionAsync(sessionKey, …, allowPrompt)` and
    `InvalidateWebSession(sessionKey)`. A new `WebSessionStore`
    (Manager Core) holds sessions keyed by a plugin convention
    (`"{site}:{account}"`, e.g. `myrealm:default`), cookie headers
    **DPAPI-encrypted at rest** via `CredentialService` in a new
    `web_sessions` table (EF migration `AddWebSessions`) — retiring
    7-4b's plaintext-in-plugin-config cookie. The host owns
    once-per-key prompt throttling and in-flight dedup (concurrent
    requests for one key share a single dialog; a cancelled capture
    blocks further prompts until invalidation or restart). Plugins
    sharing a key share the session — the cross-plugin provision
    mechanism, with no plugin→plugin references. lo-myrealm migrates
    onto the store as the reference consumer (drops its own cookie
    persistence entirely). No `ContractsVersion` bump: additive
    members on an existing interface whose only consumer ships with
    them.
  - **Web Sessions UI + liveness validation (7-5b)** — a fourth
    **Web Sessions** tab in Manage Plugins lists each stored session
    (key / captured-by plugin / captured / last-used — never the
    cookie) with **Revoke** (also the cleanup path for a session
    orphaned by uninstalling its owning plugin) and **Validate**.
    Validation routes to a new opt-in `IWebSessionValidator`
    side-interface (additive, same pattern as ILogParser/IModManager;
    adding to IUtilityPlugin itself would fail-compile every existing
    plugin) — `CanValidateWebSession` + `ValidateWebSessionAsync`
    return Valid / Expired / Failed, invoked outside the plugin's
    event queue (thread-safe, classify-only; the UI offers the
    revoke on Expired). lo-myrealm implements it by probing the
    realm's `General/UpdateName` page (exists for the life of the
    realm), and — when no realm has been learned from gameplay yet —
    **discovers one from the portal itself**: GET the authenticated
    landing page, harvest every `/customer/{id}` link (owned +
    admin'd realms), walk to the first `/realm/{id}`. So Validate
    works seconds after sign-in with no running instance. A
    customer with no realm configured yet reports Valid ("signed in;
    no realm configured yet"), not a failure. The realm-page probe
    also reads back the realm name, surfaced as `realm "…"
    reachable`.

  - **myrealm realm onboarding & import (7-6)** — onboard a realm
    end-to-end from the Manager instead of hand-entering its keys:
    Shared Resources → Realms grows an **Import…** button that signs
    in to the myrealm portal (reusing the 7-5 shared session),
    scrapes every realm the account owns or admins, and turns each
    into a Realm shared-config group. Read-only against the portal —
    it GETs realm identity (realm_id, name, Customer Key, provider
    keys) and writes only PowerGSM's own group store; realm
    administration (anything that POSTs) stays out, parked as Phase
    10. The scrape→group channel is generic: a new
    `IWebPortalDataProvider` side-interface (in `GSM.Utility`, opt-in
    like `IWebSessionValidator`) returns `WebPortalImportRecord`s
    tagged with their target game plugin + shared-config key, the
    identity fields that define them (`MatchFieldKeys`), and a
    plugin-composed display name — the Manager's new
    `PortalImportService` matches each record against existing groups
    on ALL identity fields (decrypted plaintext, Ordinal) to classify
    New / Update / Unchanged, never hard-coding a game field name. A
    checkbox dialog shows the plan (New/Update pre-checked, Unchanged
    inert) before anything is written. **Per-provider-key model:**
    lo-myrealm emits one record per (CustomerKey, ProviderKey) pair —
    a realm hosted from several providers becomes several groups
    sharing a RealmName but differing by ProviderKey, so no
    list-typed shared-config schema is needed. The group DisplayName
    carries a `"{RealmName} ({UsedBy})"` provider suffix for pickers
    while the History **Source** column (and the History session-
    filter dropdown) reads the canonical RealmName field (new
    non-sensitive `SharedConfigFields` on `SourceLabelContext`,
    surfaced via a `SharedConfigService` plaintext-only read that
    never decrypts the keys), so the per-provider entries of one
    realm render identically in History.
    lo-myrealm self-captures its session inside discovery
    (re-prompting when a stored session has gone stale) so
    onboarding's no-session-yet case just works. All additive:
    `WebSessionCaptureResult` gains `CompletionUrl`,
    `WebPortalImportRecord` / `SourceLabelContext` / `IUtilityPlugin`
    gain members, no `ContractsVersion` bump.
  - **Multiple myrealm accounts + per-realm failover (7-7)** — the
    operator can sign in to several myrealm accounts and PowerGSM
    uses them all automatically. Two payoffs: discovery/import (7-6)
    now spans every signed-in account (owner + admins), deduplicated
    by `(CustomerKey, ProviderKey)` to one group per realm; and
    lo-myrealm's characterID → name enrichment survives an expired
    account by failing over to any other live session that can reach
    the realm. Account key is `myrealm:{accountName}`, derived from
    the portal landing greeting; the host gains `StoreWebSession` /
    `ListWebSessions` context verbs (web-capture gated) and an
    `IWebPortalDataProvider.AddAccountAsync`, surfaced as an **Add
    account…** button in the Web Sessions form. Discovery enumerates
    every `myrealm:*` account and dedups in the plugin; character
    lookup keeps an in-memory `realmId → session` cache that
    self-heals (a redirected / expired / forbidden read walks the
    other live accounts, uses the first that reaches the realm, and
    re-homes), logging `served by` / `failed over from … to …` on
    each (re)home. `myrealm:default` participates as just another
    account. All additive: new `WebSessionSummary` type + additive
    context / interface members, no `ContractsVersion` bump.

- **Phase 6 — Plugin sources, manifests, and updates.** Plugins are
  now managed artifacts instead of hand-copied files. Each plugin
  self-describes via an inline manifest comment —
  `' <plugin id="..." name="..." version="..." author="..."
  requiresContracts="...">` plus an optional `' <dependencies>`
  block — parsed before compile, with the legacy
  `' <RequiresContracts: N>` comment still honoured (legacy-only
  files load as untracked local plugins; `author` is pure credit and
  never used for trust or origin). A new **Tools → Manage Plugins**
  window consolidates three tabs:
  - **Status** — the existing Plugin Status view, now with Version /
    Author / Source columns from the manifest, plus an **Uninstall**
    button (deletes the plugin file after a consent prompt spelling
    out the orphan consequences; data and configuration are kept).
  - **Sources** — manage the GitHub repos the Manager browses for
    plugins (the official `siteml/PowerGSM` @ `GSM.PluginsSource`
    source is seeded on first run and can be disabled but never
    deleted or impersonated), and browse each source's live catalog
    (contents-API listing + raw manifest parse; only files declaring
    a `<plugin>` block are catalogued). **Install...** downloads the
    chosen plugin to a staging area (`.plugin-updates\{id}\`),
    re-parses the downloaded copy as authoritative, blocks on
    missing/too-old declared dependencies, warns (with explicit
    consent) on a bare third-party id or a collision with an
    installed plugin — third-party plugins are expected to use ids
    prefixed with their source's GitHub owner — then copies it into
    `Plugins\` and hot-reloads. No restart needed; the live
    `Plugins\` folder is never touched until the verified install
    step.
  - **Updates** — compares every installed, version-carrying plugin
    against the best version across all enabled sources and lists
    installed → latest; **Update...** runs the same stage → consent
    → install → reload path. Never auto-applies.
  All three surfaces support **batch operations via checkbox
  selection** (with a Select-all toggle): check any number of
  catalog entries, pending updates, or plugin files and Install /
  Update / Enable / Disable / Uninstall them in one pass — one
  combined consent listing every item (with per-plugin warnings
  inlined), one plugin reload at the end, one summary. Buttons are
  count-labelled (e.g. "Install (3)...") and state-aware (Enable
  counts only checked disabled files, and vice versa).
  New `PluginSources` table (EF migration `PluginSources`); staged
  plugins persist in settings. See `Phase6_Plan.md`.

- **Phase 5l-3 — Apply updates (pre-flight + binary swap + history).**
  The **Apply update** button is now live on the "Update ready"
  dialog. Applying first refuses downgrades, then runs informed-
  consent pre-flight prompts when relevant: one if an automation rule
  is mid-execution (it names the rule and warns it won't resume), and
  one if instances are running (reassuring that the game servers run
  on the node and keep running — only the Manager's live log streams
  blink, and the new Manager reconnects and catches up on everything
  that happened while it was down: joins, leaves, server state, chat).
  A plugin-compatibility check then dry-run-compiles every plugin
  against the *staged* `GSM.Contracts.dll` (a green/red report;
  incompatibilities are a soft warning gated by an acknowledgement
  checkbox) as the final confirmation. It then closes PowerGSM and
  swaps the binaries via a generated `apply.cmd`: it waits for the
  Manager to exit, backs up the current `GSM.Manager.exe` +
  `GSM.Contracts.dll` to `.updates\rollback\`, copies the staged
  binaries in, and relaunches (`--post-update`). The Manager exits
  cleanly (code 0) so the watchdog stands down rather than racing the
  swap; only the two binaries are touched (never the database,
  settings, plugins, logs, or the watchdog). A failed swap is logged
  to `.updates\apply-error.log` and surfaced on the next launch. Every
  apply attempt — success or failure — is recorded to an update-
  history table, viewable under **Help → Update History** (when /
  from → to / outcome / detail). See `Phase5l_Plan.md`.

- **Phase 5l-2 — Download + stage updates.** Builds on 5l-1: the
  update dialog now has a **Download update** button (when an update
  is available and the install folder is writable). It resolves the
  release's assets, downloads `SHA256SUMS` + the Manager zip with a
  cancellable progress dialog (live %/MB while downloading,
  indeterminate for the verify/extract phases), verifies the zip's
  SHA-256 against the sums file, and extracts it into
  `<install>\.updates\{version}\extracted\`. Nothing touches the
  running install — staging is fully reversible. After a successful
  download the dialog flips to an **Update ready** state with a
  (disabled, pending 5l-3) **Apply update** button and a **Discard
  download** button that wipes the staged folder. Releases predating
  the `SHA256SUMS` pipeline step stage without checksum verification
  (logged). Applying a staged update is Phase 5l-3. See
  `Phase5l_Plan.md`.

- **Phase 5l-1 — Update notifications (detection + notify only).**
  PowerGSM now checks GitHub for newer releases on a background
  schedule and tells you when one is available — it never downloads
  or installs anything (staging and one-click apply are later
  sub-phases). A status-bar indicator appears when a newer,
  non-skipped release exists and opens a passive **update dialog** on
  click; **Help → Check for updates...** forces an immediate check.
  The dialog shows the current→latest versions and the release notes
  rendered GitHub-style (headings, bullets, inline `code`, links)
  with **View on GitHub** / **Skip this version** / **Close**.
  Release-notes rendering uses `HtmlRenderer.WinForms` fed by a small
  in-house Markdown→HTML converter, degrading safely to a RichTextBox
  renderer and then plain text so a render hiccup can never break the
  dialog. **Settings → Updates** adds an "Include pre-release
  versions" toggle and a configurable check interval. On startup a
  writeability probe warns once (and shows a persistent "read-only
  install" indicator) when PowerGSM lives in a folder it can't write
  to, since automatic updates couldn't apply there. See
  `Phase5l_Plan.md`.

- **Phase 5m-3 — Watchdog (auto-restart + start at sign-in).** A tiny
  standalone supervisor (`GSM.Watchdog`) whose only job is to keep the
  Manager running: it launches the Manager, relaunches it if it exits
  unexpectedly, escalates to safe mode after repeated fast crashes
  (2 within 60s), and gives up after a rapid-restart limit (5 within
  300s) so a hard-broken Manager doesn't spin forever. Manager and
  watchdog are decoupled — no shared assembly — communicating only via
  process launch, a shared named-mutex *name*, and an exit-code
  contract: the Manager, when launched by the watchdog
  (`POWERGSM_WATCHDOG=1`), defers its in-app **Restart Normally / Restart
  in Safe Mode** to the watchdog via exit codes 20/21 instead of
  self-spawning, so the replacement stays supervised; a clean quit
  (0) stands the watchdog down. The Manager is now **single-instance**
  (named mutex): a second launch signals the running one to come
  forward (restoring from the tray) and bows out — exiting with a
  dedicated *deferred* code when watched so the watchdog monitors the
  existing instance rather than reading the quick exit as a crash. The
  watchdog is headless (`WinExe`, no console; logs to `watchdog.log`)
  and is co-located next to `GSM.Manager.exe` automatically by the
  Manager's build/publish (framework-dependent on Build for dev,
  self-contained single-file on Publish). **Settings → Startup** has a
  "Start PowerGSM automatically when I sign in" toggle that installs a
  per-user Task Scheduler logon task (`LeastPrivilege` +
  `InteractiveToken` → no UAC, GUI visible, per-user/DPAPI scope
  unchanged), created from an XML definition with a restart-on-failure
  backstop; the checkbox reflects the live task state and disables
  itself if the watchdog isn't co-located. See `Phase5m_Plan.md`.

- **Phase 5m-2e — Missing-plugin detection + start enforcement.**
  Guards against an installation/instance whose game plugin isn't
  loaded (deleted between sessions, failed to compile, or disabled).
  Detection is *reconciliation-based* — every installation/instance's
  GameId is checked against the loaded-plugin set — so unlike the
  existing hot-reload diff it catches orphans at startup and across
  sessions, not just plugins removed mid-session. Surfaced loudly: a
  startup details dialog, a persistent `MainForm` banner (Warning,
  escalating to **Critical** red when an orphaned instance is actually
  running on its node — the data-integrity case, where the Manager
  can't parse its logs so player/activity history silently stops), and
  a DarkRed tree badge on each affected installation/instance. Re-runs
  after every manual **Reload Plugins** so removing/restoring a plugin
  updates the warning + badges live. Suppressed in safe mode
  (everything is orphaned by design there). **Enforcement:**
  `InstanceManager.StartInstanceAsync` now hard-refuses to start an
  instance whose plugin isn't loaded — closing a footgun where a
  persisted `ExeOverride` from a prior (plugin-loaded) start let the
  node launch the bare executable with empty arguments and no parse
  rules, producing an unmanageable, untracked, crash-looping process.
  The guard covers every start path (panel, tree menu, autostart,
  scheduled restart); the panel and tree-menu Start/Restart actions
  are also disabled for orphaned instances, with Stop left enabled so
  a running orphan can be brought down. See `Phase5m_Plan.md`.

- **Phase 5m-2 — Safe mode.** A recovery mode that boots the Manager
  with the surfaces most likely to carry broken code disabled — plugin
  compilation, the automation engine, notifications/Discord,
  version-check, chat-retention pruning, and node background polling —
  while keeping the DB, node clients, and basic instance ops alive so
  the operator can investigate and fix. Three entry points: the
  `--safe-mode` CLI flag, an automatic offer when the previous run
  didn't exit cleanly (a crash marker written in the binary directory
  at startup and deleted on clean shutdown), and "Restart in Safe
  Mode" in both the File and tray menus. In safe mode a persistent
  amber banner names what's disabled with a "Restart Normally" link;
  the File/tray entries mirror it. Restart-into-mode relaunches the
  exe with/without the flag *after* the outgoing instance has shut
  down cleanly and cleared its marker, so an intentional restart never
  trips the crash-recovery offer. See `Phase5m_Plan.md`.

- **Phase 5m-2c — Safe-mode feature re-enable.** A "Re-enable
  Features…" panel (File menu, safe mode only) that turns the
  individually-gated subsystems — plugins, node polling, Discord,
  automation, version-check, chat pruner — back on at runtime without
  leaving safe mode, for iterative fix-and-test (notably: fix a runaway
  automation rule, then re-enable just the engine to verify while still
  in the safe harbour). Backed by a subsystem-start controller in
  `ManagerProgram` mirroring Main's per-subsystem start steps;
  re-enable only (restart safe mode to turn something back off),
  idempotent, and version-check pulls the automation engine up first
  since it raises events into it. See `Phase5m_Plan.md`.

- **Phase 5m-2d — Plugin enable/disable.** The Plugin Status form
  gained a "Plugin files" list (every `.vb` in `Plugins\`, plus
  disabled ones in `Plugins\Disabled\`) with Enable/Disable buttons.
  Disabling moves the file into the `Disabled\` subfolder and reloads;
  enabling moves it back. The subfolder approach (rather than an
  extension rename) lets `ReloadAll`'s top-directory `*.vb` scan skip
  it cleanly, with no dependence on Windows' short-extension glob
  quirk; a disabled plugin — being unloaded — is surfaced from the file
  list since it can't appear in the loaded-plugins list. Disable warns
  that dependent installations/instances will be orphaned. Closing the
  form refreshes the orphan banner + tree badges, so a disable→reload
  immediately reflects in the warnings (and the start guard) — also
  fixing a gap where reloads done inside that form previously didn't.
  See `Phase5m_Plan.md`.

- **Phase 5m-1 — System tray + window-state persistence.** The Manager
  owns a tray icon (Open / Exit, double-click to restore) and honours
  three preferences (Settings → Window): minimize-to-tray (default
  on), close-to-tray (default off), and start-minimized (default off).
  The tree/content splitter width is persisted and restored across
  launches. Settings now shows the database and plugins directory
  paths as read-only, selectable text boxes with Copy buttons instead
  of truncated labels. See `Phase5m_Plan.md`.

- **Phase 5k-2 — Player-list panel polish + grouping fix.** Three
  refinements on top of 5k-1. **(2a)** Fixed a grouping-header bleed in
  the instance-manager panel: under "by node, then game" the game
  sub-header rendered as bold — identical to the bold instance rows
  beneath it, so the two levels ran together. Headers are now three
  distinct levels: node `## __underlined H2__`, game `### H3`, instance
  rows bold. **(2b)** Player panels now honour the same Group-by
  setting (none / by node / by game / by node-then-game), reusing that
  level scheme above each instance's player block; the editor's
  grouping combo is enabled for both panel kinds (the layout-
  composition controls stay gated off for player panels). **(2c)** Two
  new per-panel display toggles, both gated to the player-list kind:
  `ShowJoinTime` appends each player's join time as a relative
  timestamp (`PlayerSession.JoinedUtc`), and `ShowTotalInTitle`
  appends the online count to the panel title. Schema adds the two
  bool columns (migration `Phase5k2c_PlayerPanelToggles`, both default
  False). See `Phase5k_Plan.md`.

- **Phase 5k-1 — Player-list Discord panel (core).** A new panel kind
  that renders a live online-players roster instead of the instance-
  manage table. Reuses the panel infrastructure wholesale: the same
  `DiscordPanelEntity` (new `PanelKind` discriminator), the same scope
  mechanism (all-instances / game / installation / instance-set), the
  same per-panel refresh loop + join/leave event-push, and the same
  plain-content full-width rendering. `BuildPanelMessage` branches on
  `PanelKind`; the `PlayerList` branch groups currently-online players
  by instance (a header per instance with its tile/save context + the
  online count), renders each player in the 5d-6 identity format via
  the resolver (`EnrichPlayers`, the same path `/players` uses), and is
  read-only (no Manage button). Instances with nobody online are hidden
  by default; a per-panel `ShowEmptyGroups` toggle shows them all.
  Player lists are length-capped with a truncation marker (multi-page
  deferred). The panel editor gained a Panel-kind selector and the
  show-empty toggle, gating off the layout-composition + grouping
  controls that don't apply to a fixed-layout roster. The enriched
  player list is stashed on the per-instance panel runtime from the
  same `GetPlayersAsync` fetch that already fed the player count, so
  there are no extra node round-trips. Schema: `PanelKind` (default
  "InstanceManager") + `ShowEmptyGroups` (default False) columns,
  migration `Phase5k_PlayerListPanel`, both defaulted so existing
  panels are unchanged. Polish — per-group counts in the title,
  grouping options, per-row join time, empty-whole-panel state — is the
  5k-2 follow-on. See `Phase5k_Plan.md`.

- **Phase 5d-8 — `/lastseen` Discord slash command.** Operators can
  ask when and where a player was last seen, or list who's been on
  recently. Three modes: a **player lookup**
  (`/lastseen player:<name>`) reports whether the player is **active
  now** or **offline** — derived from their most recent join/leave —
  with a relative timestamp and the same tile/realm/node Source label
  the History grid shows (reuses `HistoryQueryService` — no schema
  change); an optional **scope filter** (`instance` / `game` /
  `installation`, mutually exclusive) narrows the lookup, resolving
  game/installation to an instance set via the Instances table
  intersected with the guild-visible set; and a **roster mode**
  (scope only, no player) lists the most-recently-seen players in
  that scope, deduped by identity, each flagged active-now or offline. Gated at `ServerOperator` through
  the shared command catalogue (appears in `/help`, permission-
  checked like `/players`), guild-scoped to instances exposed via
  this server's panels, and ephemeral.

  Identity-aware: the typed name is resolved through the
  `IdentityResolver` (exact match on any facet — persona, character
  name, character id, platform id) and the lookup runs against the
  resolved persona, so searching by the in-game character name finds
  the same history a Steam-handle search would; the "also matched"
  disambiguation groups by identity facets rather than display
  string, so a single player rendered under both a resolved character
  name and a raw persona no longer lists itself. Three new
  autocomplete providers back the arguments (player names, games,
  installations; instance reuses the existing one). Built in four
  rounds — command shape, identity-awareness, scope filters, roster.
  See `Phase5d-8_Plan.md`.

- **Phase 5g-2d — Manager-side IdentityResolver.** Centralised
  in-memory cache that promotes the Manager to system-of-record
  for resolved player identity, closing the asymmetry between
  surfaces that showed the character name (History via leave-time
  inheritance + chat-fallback) and surfaces that didn't (Overview
  panel showing the raw persona, Discord showing the Steam handle).
  Solves the "schizophrenic keys" problem — identity observations
  arrive piecemeal from different sources (Login URL gives
  PlatformUserId, Persisting line gives DisplayName, /players
  snapshot gives PlatformPersona) — using a small union-find: each
  `IdentityRecord` carries a set of alias keys, any new observation
  matching an existing key merges into that record, and observations
  that bridge previously-separate records fuse them. Field-level
  merge rules: PlatformUserId / CharacterId / Platform are stable
  per identity (fill-if-empty, warn-and-keep on the
  should-never-happen conflict); DisplayName / PlatformPersona are
  newest-write-wins to support legitimate renames. Scope keys are
  opaque to the resolver — plugins decide what `SessionIdentity`
  means for their game (LO: `lastoasis:{realmId}` backend-stable;
  Conan: `conanexiles:{installId}` for v1 with documented
  bleed-on-world-swap; Factorio: `factorio:{installId}`).

  Hydrates at Manager startup from the most recent 5000
  PlayerActivity rows in the last 30 days carrying any identity
  facet, so the cache is warm from the first Enrich call — no
  cold-start window. Continuously fed by three write-through
  paths: `PersistPlayerObservationAsync` (every live join/leave),
  `ResyncActivePlayersFromNodeAsync` (on stream reconnect /
  poll-loop health check), and `BackfillIdentitiesForInstanceAsync`
  (the 10s identity-backfill pass, no extra round-trips). The
  persist path also *consults* the resolver as a write-time
  fallback when /players misses — the common PlayerLeave case
  where the Node has already evicted the session — so new
  PlayerActivity rows get stamped with the resolved identity at
  write time instead of leaning on render-time inheritance.

  Five read consumers now enrich through the resolver: the
  Overview panel (`InstancePanel.ApplyPlayers` via
  `InstanceManager.EnrichPlayers`), the History timeline
  (`HistoryQueryService.LoadTimeline` as primary fallback, with the
  existing chat-fallback as secondary for cases the resolver can't
  help), the Discord `/players` slash command, and both Discord
  notification paths (join/leave label composition via
  `PlayerLabelForNotification`, hitting both webhook and bot since
  it sits at the emitter input). `GetPlayersAsync` deliberately
  stays raw — enrichment is opt-in per consumer rather than a
  hidden side effect of fetching.

  `Platform` is tracked as a carried attribute (not an alias key —
  a platform name identifies no one), surfaced through `Enrich` for
  consumers that want to render `character (Platform: persona)`.
  One documented behaviour worth knowing: `PlayerActivity` has no
  Platform column, so hydration can't supply it — Platform fills
  from live observations within ~10s of a player being online (the
  next backfill pass, or the join's persist write-through). A
  brand-new player's very first join notification after a Manager
  restart may render `character (persona)` without the Steam:
  prefix; the leave and all subsequent joins show the full format.
  Self-corrects automatically.

  Thread safety via `ReaderWriterLockSlim` — writes are rare
  (one per observation), reads dominate (every render). All
  consumers receive copies via `Enrich` / `FindByKey` /
  `GetAllRecords`; the cache's records are never directly
  mutable from outside. Diagnostic surface (`RecordCount`,
  `GetAllRecords`, `IsHydrated`) is in place for a future Tools
  menu `View IdentityResolver cache...` UI; the UI itself is
  deferred. See `Phase5g-2d_Plan.md` for the full architectural
  framing and the six resolved planning decisions.

- **Tools → Purge && Rebuild History...** lets the operator
  wipe the Manager's history tables (PlayerActivity,
  PlayerSessions, SessionHosts, ChatMessages) and re-derive
  them from the Node's authoritative current state for every
  currently-running instance on an attached node. Two use
  cases: cleaning up rows produced by a parsing-logic bug
  that's since been fixed (e.g. the LO false-leave fix below),
  and establishing a clean baseline for test→prod migration
  or recovery from a corrupted Manager DB. Currently-connected
  players' real `JoinedUtc` timestamps are preserved from the
  Node's in-memory session state; no fake "now" timestamps
  are written. Chat is filtered to only include lines from
  currently-connected players, only since each player's most
  recent join, to keep the rebuilt timeline coherent. Whole
  purge+rebuild runs in a single EF transaction — a mid-flight
  failure rolls back cleanly. Dialog flow: confirmation form
  with explicit "what's preserved / what's removed" disclosure
  plus typed-REBUILD gate, modeless progress dialog with
  live status updates while the work runs, and a result
  summary showing row counts, filtered-out chat count, and
  any non-fatal warnings. See `Phase5j_Plan.md` for the full
  design.

### Fixed

- **Shim-supervised games were killed and spuriously crash-restarted on a
  graceful node shutdown (most visibly a Windows self-update), instead of
  surviving for re-adoption.** (Phase 8-1 bug, surfaced by 8-2 slice 6.) The
  clean-shutdown path sends each shim a `Detach` frame — the shim then keeps
  its game and waits for the next node — but the node-side `ShimSession` didn't
  mark the session as intentionally detached, so when the shim closed its end
  of the pipe the node's own read loop saw the drop, fired its exit callback,
  and routed into `HandleProcessExited`. Because a detach never sets
  `StopIntentPending`, that handler treated the drop as an unexpected exit: it
  disposed the `ShimSession` — whose `Dispose` still ran `TryKillShim` with
  `_ownsShim` True, so `Kill(entireProcessTree:=True)` took down the live shim
  *and the game under it* in one go — and then scheduled a crash-restart that
  spawned a throwaway fresh shim+game. On a self-update the net effect was the
  game vanishing the instant the node began its update-exit (while the node was
  still alive), a throwaway restart, then the real swap+relaunch landing on the
  wrong process. Linux dodged it only on timing — the bare node there exits
  promptly enough that the read-loop drop never runs the cascade; the ~30s
  graceful-shutdown lingering on Windows (a live SSE log stream holding Kestrel
  open to its drain timeout) gave it the window every time. Fixed in
  `ShimSession`: a deliberate `Detach` (both `SendDetachAsync`, used by the
  shutdown hook, and `DetachAsync`) now sets a `_detaching` flag and clears
  `_ownsShim` *before* the frame is sent, and `SignalExit` skips the `onExited`
  cascade when `_detaching` is set — so a post-detach link drop is benign, the
  game is left running, and the next node re-adopts it by saved endpoint.
  Verified on all four survivor paths (Linux systemd + bare, Windows service +
  bare): game PID unchanged across the bounce. The lesson: a deliberate detach
  must suppress the node's own exit handling, or the node tears down and
  crash-restarts the very game it just chose to leave running.

- **Last Oasis player Leave dropped — and subsequent sessions
  mis-paired — when a player was connected across a Manager
  log-stream reconnect or restart.** The LO parser turns an
  address-only close line (`UChannel::Close` / `UNetConnection::Close`
  carry a RemoteAddr, not a name) into a *named* leave by looking
  the address up in a per-parser RemoteAddr→name table built at
  `Join succeeded`. That table lives on the parser instance, which
  the Manager recreates on every log-stream reconnect — and a full
  Manager restart starts a fresh process with an empty table. So a
  player who joined before the reconnect/restart had no binding, and
  their eventual disconnect — most visibly a `UChannel::Close`-only
  idle-kick/timeout, which has no `UNetConnection::Close` for the
  Manager's nameless-leave heuristic to fall back on — no-matched at
  the parser and produced no Leave. Worse, the miss **cascaded**
  through the name-keyed `_activePlayers` dedup bucket: with the
  stale name still in the bucket, the player's *next* reconnect Join
  was suppressed as a duplicate (no History row, no Discord
  notification), and that reconnect's close then fired a Leave that
  closed the *prior* still-open session — collapsing several real
  log sessions into one mis-paired History entry.

  Fixed in three parts. (1) New opt-in `IConnectionBindingAware`
  contract interface (implemented only by the LO parser) lets the
  Manager own the RemoteAddr→name table per instance and inject the
  same dictionary on every parser (re)creation, so bindings survive
  in-process stream reconnects; the store is cleared only on a real
  instance stop, not on reconnect. (2) On resync the Manager now
  rehydrates that table from the Node's authoritative `/players`
  `RemoteAddress` — which the Node already tracks and returns — so
  bindings are restored after a full Manager restart too; the
  parser's first post-restart close then resolves normally, fires
  the Leave, and clears the bucket so the next Join is no longer
  suppressed. (3) No Node change required — the fix consumes the
  existing `PlayerSession.RemoteAddress` wire field, which the Node
  already populates (it's the Overview tab's IP Address column).

  Follow-on (now also handled): a player who leaves *entirely while
  the Manager is offline* produces no close line at all, so the
  parser-binding fix above can't see them. The resync now also runs a
  `/players`-diff leave-reconcile — any player whose most-recent
  activity row on an instance is a Join but who is absent from that
  instance's authoritative `/players` is synthesised a Leave
  (persist-only; no Discord ping, since the departure is in the past).
  Two guards keep it from firing falsely: it's scoped by InstanceId
  rather than the realm-wide SessionIdentity (so a player online on a
  sibling tile isn't reconciled away), and it's gated on Node uptime
  ≥ 5 min (right after a *Node* restart `/players` under-reports
  still-connected players, since the Node resumes log tailing from a
  byte offset and never replays old Join lines). One residual edge
  remains: a player who stays connected but completely silent across a
  Node restart never re-appears in `/players` and could eventually be
  reconciled as left — the same class as the existing node-restart
  player-state gap; rare and accepted.

- **Duplicate player Join rows in History after a stream reconnect.**
  `ResyncActivePlayersFromNodeAsync`'s join-synthesis dedup skipped
  re-synthesizing a join only when the most-recent PlayerActivity
  row was a join AND its `TimestampUtc` was ≥ the Node's `JoinedUtc`.
  But the live join row is stamped at the Manager's processing time
  (`DateTime.UtcNow`) while `JoinedUtc` comes from the Node's clock;
  when the Manager clock ran slightly behind the Node's, the `>=`
  failed and the resync synthesized a *second* join (carrying the
  resolved character name) alongside the live row (still showing the
  raw persona) — the blank-then-named duplicate pair seen on
  reconnected sessions. The dedup now treats an open join
  (most-recent row is a join, regardless of timestamp) as "already
  represented" and skips synthesis, since synthesis exists only to
  fill a *missing* join.

- **Last Oasis: false-positive player Leave events in History
  when another player on the same tile disconnected.** When
  player A disconnected, UE4 emitted a `UNetConnection::Close`
  log line followed by one or more `UChannel::Close` lines for
  the same connection. The LO parser matched both shapes,
  producing two leave events per real disconnect. The first
  resolved player A's name correctly (and cleared the IP-to-name
  dict entry); the second failed the lookup (entry just
  removed) and emitted a nameless leave. InstanceManager's
  "exactly one player online means it was that player"
  nameless-leave heuristic then misattributed the second leave
  to whichever player was still on the tile. Net symptom: when
  someone disconnected from a two-player tile, the History
  window logged BOTH the real leaver AND the remaining player
  as having left at the same timestamp, even though the
  remaining player was still on the server (visible in the
  InstancePanel with their original JoinedAt preserved). The LO
  parser now matches `UNetConnection::Close` only, which fires
  exactly once per disconnect and reliably resolves the
  leaver's name. The InstanceManager heuristic is preserved as
  a defensive fallback for the manager-reconnect-mid-session
  case and for plugins that may produce nameless leaves by
  other means.

### Changed

- **Phase 5d-6 — Discord player display format.** Both Discord
  surfaces — the `/players` slash command and join/leave
  notifications — now render player identity as
  `character (Platform: persona)` when a distinct character name
  is known (e.g. `site's character (Steam: site_ml)`) and
  `persona (Platform)` when only the persona is known
  (e.g. `site_ml (Steam)`), matching one format across both
  surfaces. The platform parenthetical drops to bare
  `character (persona)` or `persona` when Platform isn't known
  yet — see the Platform-hydration note under 5g-2d Added above
  for when that transient state arises. For `/players` the change
  lives in `BuildPlayersResponse`; for notifications it lives in
  the new `PlayerLabelForNotification` helper that hooks the three
  emit sites in `InstanceManager.HandlePlayerJoin` /
  `HandlePlayerLeave`, so the composed label flows through the
  shared notification emitter and reaches both the webhook and bot
  plugins without per-plugin changes.

- **Manager database path anchored to the binary directory.**
  The runtime DB connection string resolved `gsm.db` as a
  relative path, i.e. against the process working directory.
  For normal launches (double-click, VS debug) the working
  directory equals the binary's folder, so the DB sat next to
  the binary as intended — but a launch that sets a different
  working directory (a shortcut's "Start in", a Task Scheduler
  entry) would resolve `gsm.db` elsewhere and silently create a
  fresh empty database, presenting to the operator as total
  data loss. Surfaced while planning Phase 5m — the watchdog
  launches the Manager via Task Scheduler, which is exactly the
  divergent-working-directory case. The runtime DbContext now
  resolves the path against `AppContext.BaseDirectory` (where
  logs already go), making it robust regardless of launch
  method. Existing deployments keep their DB alongside the
  binary, so this points at the same file — no migration
  needed. Only affected case: a deployment previously launched
  with a divergent working directory, whose DB therefore lives
  somewhere other than the binary folder; that file must be
  moved next to the binary or the Manager starts fresh. The
  design-time `GsmDbContextFactory` (Add-Migration in the
  Package Manager Console) is unchanged — dev tooling, not
  runtime.

- **Conan Exiles: AdminPassword moved from the Configuration
  tab to the Server Settings (ServerSettings.ini) file editor.**
  Conan reads `AdminPassword` natively from
  `ServerSettings.ini`'s `[ServerSettings]` section. Older
  versions of the plugin appended it to the launch URL as
  `?AdminPassword=X` and stored it as an instance-level
  config field, which worked at spawn time but split the
  canonical INI value from PowerGSM's stored value — a
  footgun if an operator edited the INI directly and then
  re-saved the instance config. Now surfaced via the Server
  Settings tab where Conan natively stores it.

  **Migration for existing Conan operators:** the legacy
  value in your instance ConfigJson is ignored on launch. Open
  the Server Settings tab for each Conan instance and set
  AdminPassword there (top of the Identity cluster). Without
  it, in-game admin claim won't work and RCON karma can't
  rise above 0 — same critical-field semantics as before,
  just a different home in the UI.

## [0.3.0] - 2026-05-22

### Changed

- **InstancePanel player columns renamed for cross-game
  accuracy.** "Steam name" → "Platform name",
  "Steam/Platform ID" → "Platform ID". The slot is
  game-dependent (Steam handle on LO, Funcom FLS handle
  on Conan, multiplayer username on Factorio); the
  Steam-prefixed label was misleading on Conan in
  particular post-5g-2b. Underlying data binding
  unchanged — the column still reads
  `PlayerSession.PlatformPersona`. Closes the
  "Conan InstancePanel Steam name column label" Backlog
  item.

- **NodePanel status label switched to attach/detach
  vocabulary.** "Enabled" / "Disabled" → "Attached" /
  "Detached". Companion to the new attach/detach toggle
  (see Added) — "attach" implies state preservation,
  which matches what the flag does, while "enable"
  carried a binary-functional connotation that doesn't.
  Underlying database column stays `IsEnabled` to avoid
  a column-rename migration; the vocabulary change is
  UI-only.

- **History window "Source" column replaces "Tile / Session" +
  "Instance" (Phase 5h-6).** The two old columns are consolidated
  into a single Source column whose contents are plugin-formatted
  via the new `ISourceLabelProvider` interface (see Added). Last
  Oasis renders `{TileName} — {RealmName} — {Node}/{Install}`,
  dropping any segment with no data; plugins not implementing the
  interface get a manager-supplied default of
  `{Node}/{Install}/{Instance}`. The instance-path segment is
  intentionally Node/Install rather than Node/Install/Instance
  for LO because the LO backend reassigns tiles across instances
  within an installation — the on-disk installation is the
  meaningful disambiguator at the History level. The full
  InstanceId GUID (previously embedded as the last segment of
  the old Instance column for grep-the-log workflows) is now
  reachable via the new row tooltip and right-click context
  menu. Snapshot-mode rows get the same plugin-formatted label;
  `SnapshotRow` gained `InstanceId` (captured from the join
  event during activity replay) for that purpose. The legacy
  `TimelineRow.TileDisplayName` + `TimelineRow.InstanceDisplay`
  properties are kept on the row for backwards-compat but no
  longer rendered.

- **History row tooltip + right-click context menu (Phase 5h-6).**
  Hovering any row pops a tooltip with the full SessionIdentity
  and full InstanceId on separate lines (skipping either line
  when empty). Right-click opens a context menu with two items:
  "Copy instance ID" and "Copy session identity" — both copy
  the raw value to the clipboard and confirm via the status bar.
  The Opening handler disables items whose identifier is empty
  on the selected row, so accidental no-op clicks can't happen.
  Tooltip + `ListViewItem.Tag` are set fresh on every render call,
  including the UTC-toggle cache replay, so both stay in sync
  with what's actually displayed.

- **`FormatSessionLabel` learns about linked realms (Phase 5h-6).**
  The session-filter dropdown at the top of the History window
  now shows the linked SharedConfigGroup's DisplayName
  ("Forested Wetlands — Site's World") instead of the truncated
  realm_id substring for installs that link to a group. A new
  pre-pass in `LoadKnownSessions` walks SessionHosts → Instance
  → Installation → SharedConfigGroup and feeds the realm name
  through to the formatter via a new optional `realmDisplayName`
  parameter; first-write-wins per identity if (somehow) multiple
  installs hosting the same session link to different groups.
  Unlinked installs continue to render `tile — realm {hash}` as
  before. Session-host rows pre-dating the realm link stay on
  the legacy format until that session is hosted again under
  the new linkage — no backfill.

- **Conan parse-rule "Map Loaded" classifier corrected.** The
  Conan Exiles plugin's `LogWorld: Bringing World` parse rule
  was set to `ParsedEventKind.Custom` capturing `MapPath`, which
  was a silent no-op — `Custom` is a scrape-only kind that
  populates `ServerStateResponse.CustomFields` but doesn't
  affect `ServerState.CurrentMapPath`. Changed to
  `ParsedEventKind.TileLoaded` matching the equivalent LO rule
  so the map path now correctly populates the server-state
  tracking + mirrors to the persistent `instance_state` row.

- **`PlayerSession` identity model split.** `PlayerSession.Name`
  is gone, replaced by `PlatformPersona` (Steam handle / Xbox
  gamertag — captured from the Login URL's `?Name=` parameter,
  known immediately on join) and `DisplayName` (in-game
  character name — captured from the LO `Persisting` line and
  from chat speakers, known after the player's first chat or
  Persisting tick). On Last Oasis these can diverge whenever
  a player renames their character via myrealm; without the
  split, the in-game-renamed character appeared as their
  original Steam persona everywhere the manager rendered
  them. Manager UI surfaces (player list, Discord panels,
  slash command output) default to `DisplayName ?? PlatformPersona`
  via a coalesce, so a player who hasn't been name-resolved
  yet still renders their Steam handle rather than going blank.
  Factorio is unaffected by the divergence: PlatformPersona
  stays Nothing because the Factorio multiplayer username is
  both platform identity and in-game display name in one
  field, which lands on DisplayName.

- **`ChatMessage` identity expansion.** `ChatMessage.PlayerName`
  is gone, replaced by `DisplayName` (always populated, carries
  whatever name the chat line emitted) plus `PlatformUserId`
  and `CharacterId` (populated when the speaker's session has
  been identity-resolved at the time the chat line fires;
  Nothing otherwise). Enables cross-rename queries against
  chat history — a `WHERE CharacterId = X` query returns every
  line that character ever spoke, regardless of what name they
  were going by at the time.

- **`ChatMessageEntity` migration on the Manager.** The
  `ChatMessages` SQLite table renames `PlayerName` →
  `DisplayName` and adds `PlatformUserId` + `CharacterId`
  columns plus an index on `CharacterId`. EF Core 8
  auto-detected the property rename and emitted `RenameColumn`
  in the generated migration, so existing chat history
  survives intact under the new column name. New identity
  columns are NULL on pre-5g-1 rows.

- **`PlayerActivity` identity snapshot columns.** The
  `PlayerActivity` SQLite table gains `CharacterId`,
  `PlatformUserId`, and `DisplayName` columns plus a
  non-unique index on `CharacterId`. Populated at write
  time by a new `InstanceManager.PersistPlayerObservationAsync`
  that wire-calls the Node's `/players` endpoint and
  matches the joining/leaving player against the resolved
  session by `PlatformPersona` or `DisplayName`. The
  History window's Join/Leave rows now render the in-game
  character name (matching how Chat rows have always
  rendered), closing the activity-vs-chat asymmetry that
  was a known gap throughout 5g-1. Misses — Node hasn't
  resolved the session yet at join time, or has already
  removed the session by the time the leave-side wire
  call resolves — leave the columns NULL; the renderer
  falls back to the raw `PlayerName` via the new
  `IdentityFormatter`. Pre-migration rows continue to
  render under that same fallback. The originally-planned
  one-shot backfill from `ChatMessages` for pre-5g-2 rows
  was dropped during scoping: on Last Oasis,
  `PlayerActivity.PlayerName` (Steam handle) and
  `ChatMessages.DisplayName` (character name) differ by
  default for nearly every player from character creation
  onward, so name-equality matching across the two
  tables would only recover the edge case of players who
  happened to pick their Steam handle as their character
  name, at non-trivial false-positive risk on busier
  tiles. Architectural choice was write-time snapshot
  (rather than render-time lookups or a Manager-side
  mirror of the Node's `players` table); snapshot
  semantics now match `ChatMessageEntity`'s existing
  approach. Migration: `Add-Migration
  Phase5g2_PlayerActivity_Identity`, then
  `Update-Database`.

- **Shared `IdentityFormatter` helper.** New
  `GSM.Manager.Core.IdentityFormatter` module with one
  method: `Format(displayName, platformPersona,
  fallback)` returning the first non-empty value. Three
  consumers now share it instead of duplicating inline
  coalesces: `HistoryQueryService.LoadTimeline`'s
  activity-row assembly, `GsmSlashCommands.BuildPlayersResponse`
  (Discord `/players`), and the InstancePanel player
  list. 5g-1 testing surfaced subtle rendering
  differences between consumers from inline duplication;
  this centralises the coalesce decision in one place
  so future visibility-profile gating (admin vs guest
  views, PlatformUserId redaction) has a single edit
  point.

- **Conan plugin parse-rule labelling corrected (5g-2b).**
  The Conan Exiles plugin's `LogNet: Join succeeded:` and
  `LogNet: Player disconnected:` parse rules now capture
  the post-colon token into the `PlatformPersona` group
  rather than `DisplayName`. The token is structurally
  the FLS handle (Funcom's account-level identifier —
  sometimes bare like `losno420`, sometimes with a
  discriminator like `losno420#72569`, depending on how
  the account was provisioned), NOT the in-game character
  name. Character names only arrive via chat lines and
  via the `ConanSandbox: Display: Character ID <n> has
  name <Name>` spawn line (the latter not consumed yet —
  see Backlog Phase 5g-2c). The original labelling
  polluted the Node's `PlayerSession.DisplayName` with
  platform-identity data until chat eventually landed
  and overwrote it, and produced History join/leave rows
  showing the FLS handle for characters whose chat rows
  correctly rendered as the character name. The leave-
  side rename also closes a latent bug where the leave
  event's FLS-handle token would no longer match the
  session via the `DisplayName` key after chat had
  flipped the session's DisplayName to the character
  name — matching by `PlatformPersona` is stable across
  the chat-driven DisplayName updates. Living Conan
  sessions bound under the old rules need to disconnect
  and reconnect once for the new binding to take effect;
  old History rows stay on the FLS handle permanently
  (no backfill, same false-positive-risk rationale as
  the 5g-2 backfill drop above).

- **History viewer render-time chat fallback for activity
  rows (5g-2b).** New
  `HistoryQueryService.ApplyChatFallbackDisplayNames`
  helper backstops the write-time identity snapshot
  introduced by the `PlayerActivity` migration. For
  Join/Leave TimelineRows whose snapshot DisplayName was
  empty or equal to the raw PlayerName, AND
  PlatformUserId is populated, the helper looks up the
  most recent `ChatMessages.DisplayName` for that
  (SessionIdentity, PlatformUserId) pair and overrides
  `TimelineRow.PlayerName` with the result. One indexed
  query per distinct (sid, pid) pair, leveraging the
  `IX_chat_pid` index from 5g-1. Handles the edge cases
  where the write-time snapshot couldn't bind a character
  name — first-time-on-this-Node players (Node's
  players-table cache misses), cross-Node migrations
  where a returning player joins on a Node that doesn't
  have them in its persistent cache, and (most
  importantly) Conan join events that fire before chat
  lands. Players who never chatted within the queried
  scope still fall through to the raw parser PlayerName —
  best-effort backstop, not a complete resolution path.
  Closing the remaining never-chatted gap for Conan would
  require parsing the Character ID spawn line plus a new
  EventStore stash path for `(cid + display, no pid)`
  events; deferred to Backlog Phase 5g-2c.

### Added

- **Phase 5g-2c — Conan Character ID binding for silent
  players.** New `Character Spawn (Character ID →
  CharacterId + DisplayName)` parse rule on the Conan
  plugin captures the spawn line
  (`ConanSandbox: Display: Character ID <n> has name <X>
  and guild ID <g>.`) that fires ~100-200ms after Join
  succeeded. Classified as `PlayerIdentity`.

  **EventStore handling.** The spawn line carries
  CharacterId + DisplayName but no PlatformUserId / IP /
  PlatformPersona, so it can't match an existing session
  by any key. New `TryBindRecentSpawn` helper applies a
  temporal heuristic: among active sessions with no
  CharacterId yet, find the one joined within the last
  3 seconds. If exactly one matches, bind cid+display
  directly. If zero or multiple match (concurrent joins),
  fall back to a cid-keyed stash in the existing
  `PendingIdentitiesByCharacterId` collection.

  **Drain extension.** `DrainPendingCidIdentity` now also
  applies DisplayName from pending entries, guarded with
  "only when the session's DisplayName is empty or equals
  PlatformPersona" so a chat-bound DisplayName isn't
  displaced by a stale spawn entry. The ChatMessage
  handler now calls `DrainPendingCidIdentity` after
  `ApplyFields` so chatty-but-late-bound players also
  drain cleanly.

  **Result.** First-time-ever players who join and never
  chat now render their in-game character name in the
  History window for the typical low-population server
  case. Known limitation documented in both
  `ConanExilesPlugin.GetLogParseRules` and
  `EventStore.PlayerIdentity`: busy-server scenarios with
  concurrent joins where the temporal heuristic is
  ambiguous AND no chat fires for one of the matching
  sessions — those rows still render as the FLS handle.
  Bounded by concurrent join rate; not visible on the
  operator's typical setup.

  Closes the "Phase 5g-2c — Conan Character ID line
  binding for silent players" Backlog item.

- **Node attach/detach toggle.** New context-menu item
  on each node in the tree ("Detach Node" when attached,
  "Attach Node" when detached) flips
  `NodeEntity.IsEnabled`. Detached nodes are filtered out
  of `InstanceManager.FetchAllInstanceIds` (the
  background polling loop's per-instance status refresh)
  and `VersionCheckService.RunOnePassAsync` (background
  version polling), removing the 3-second retry spam
  when a remote node is offline. The node's existing
  configuration is preserved — re-attaching resumes
  polling on the next iteration (within 3 seconds). The
  tree visually marks detached nodes with grey text + a
  "[detached]" suffix; the NodePanel status label
  follows suit (see Changed).

  **Out of scope (deliberately):** existing log streams
  to a detached node are NOT actively cancelled — they
  continue until the underlying TCP connection drops or
  the operator closes the viewer. Explicit operator
  actions (manual Start/Stop/Restart from the
  InstancePanel, opening a log viewer, manual "Check for
  Updates") are also NOT gated; only the background
  polling loops are. Operator wanted background-noise
  suppression, not an entire node-disable wall.

  **No schema migration needed.** Repurposes the
  existing vestigial `NodeEntity.IsEnabled` column,
  which had been declared with no UI to toggle it and no
  poll site to read it from. Only consumers prior to
  this change were `NewInstallationForm`'s node dropdown
  filter (still works correctly under the new
  semantics) and the `NodePanel` status label
  (re-vocabularised).

  Closes the "attach/detach toggle" piece of the
  "Node attach/detach + config import/export/merge/split"
  Backlog item; export / import / merge / split remain
  pending.

- **Plugin-defined shared configuration groups
  (Phase 5h-1 through 5h-5).** Plugins can opt into a new
  shared-config concept where multiple installations link to a
  common group whose fields they share via a three-layer merge.
  The Last Oasis plugin uses it for **Realms**: a single Realm
  holds the realm-wide `CustomerKey` + `ProviderKey` +
  `RealmName`, and the operator setup of three LO installs
  hosting different tile pools on the same realm previously
  required duplicating credentials into each install's
  `ConfigJson`. With the feature, the credentials live on the
  Realm and each install just links to it.

  **Interface** (Phase 5h-1, `GSM.Contracts`): new
  `ISharedConfigProvider` interface. Plugins declare
  `SharedConfigKey` (lowercase id), `SharedConfigLabel`
  (user-facing string — "Realm" for LO),
  `GetSharedConfigSchema()` returning
  `IReadOnlyList(Of ConfigFieldDescriptor)`, and
  `DiscriminatorFieldKey` (the field whose value identifies the
  group across installations — `CustomerKey` for LO).

  **Storage** (Phase 5h-1): new `SharedConfigGroupEntity` table
  with `GroupId` PK, `PluginId`, `GroupType`, `DisplayName`,
  `ConfigJson`, `CreatedUtc`, `UpdatedUtc`. `InstallationEntity`
  gains a nullable `SharedConfigGroupId` FK with
  `OnDelete=SetNull` — deleting a group leaves its installations
  alive but unlinked, falling back to install-level config.
  Migration: `20260522145126_Phase5h_SharedConfigGroups`.

  **Service** (Phase 5h-1): new `SharedConfigService` owns CRUD
  with field-level encryption-at-rest via DPAPI (same mechanism
  as `CredentialService`). Fields marked `IsSensitive=True` in
  the plugin schema get a `__GSM_ENC__:` sentinel prefix
  wrapping base64 DPAPI bytes; `LoadGroupFieldsPlaintext`
  decrypts at read time before handing values back to the
  schema renderer.

  **Three-layer merge** (Phase 5h-2): new
  `InstanceManager.MergeConfigLayers(db, installation, instance)`
  overlays group → install → instance with the rule "blank
  upper-layer values don't overwrite non-blank lower-layer
  values". So a Realm's `CustomerKey` flows through to instance
  config unless an install explicitly overrides it, and an
  install's value flows through unless the instance explicitly
  overrides. Plugins see the merged result via
  `InstanceConfig.CustomFields` exactly as before — layering is
  transparent to them.

  **LO opt-in** (Phase 5h-3): `LastOasisPlugin` implements
  `ISharedConfigProvider`, exposing `CustomerKey` + `ProviderKey`
  + `RealmName` as the realm schema. The same three fields
  remain in `GetInstallConfigSchema()` during the transition for
  backwards-compat — the merge favours install over group when
  both are set, so existing installs keep working unchanged
  until the operator manually links them and (optionally)
  clears the install-level values.

  **Management UI** (Phase 5h-4): new Tools → Shared Resources
  dialog. `SharedConfigGroupsForm` has one tab per loaded plugin
  implementing `ISharedConfigProvider` (today: just "Realms" for
  LO; future plugins appear automatically). Each tab lists
  existing groups with linked-installation counts plus
  Add/Edit/Delete buttons. `SharedConfigGroupEditForm` renders
  the plugin's schema via the existing `SchemaFormBuilder`, so
  password fields / integer pickers / file pickers all behave
  consistently with the install / instance editors. Delete
  warns when installations are linked (FK becomes NULL per the
  migration config, not a cascade).

  **Installation editor integration** (Phase 5h-5): both
  `NewInstallationForm` and `EditInstallationForm` gained a
  Realm row containing a ComboBox + "New..." button. The row
  hides automatically when the selected plugin doesn't
  implement `ISharedConfigProvider`. NewInstallationForm's
  `OnGameChanged` refreshes visibility + contents when the user
  picks a different game; EditInstallationForm pre-selects the
  installation's current `SharedConfigGroupId` on load. The
  "New..." button opens `SharedConfigGroupEditForm` in
  create-new mode and re-selects the new group on return via
  the form's `SavedGroupId` property.

  **Scope dropped:** auto-migration prompt for existing
  installations sharing a `CustomerKey`. Reviewed and dropped —
  zero deployed copies in the wild, and the operator's own
  three-installation migration through the new UI takes under a
  minute. Manual migration also preserves the install-level
  `CustomerKey`/`ProviderKey` fields for backwards-compat;
  clearing them via Edit Installation is left to the operator's
  discretion (until cleared, install-layer values continue to
  win the merge per precedence).

- **Plugin-defined Source-column formatting (Phase 5h-6).**
  New `ISourceLabelProvider` interface in `GSM.Contracts` lets
  plugins control how their rows render in the History window's
  Source column (see Changed). One method:
  `FormatSourceLabel(context As SourceLabelContext) As String`,
  invoked once per row at render time. `SourceLabelContext`
  carries `SessionIdentity`, `TileName`, `NodeName`,
  `InstallationName`, `InstanceName`, `InstanceId`, and
  `SharedConfigGroupName` (the user-set realm name from the
  linked SharedConfigGroup) — all pre-resolved by the manager
  so the plugin doesn't touch EF or the session-hosts table.

  **LO implementation:** three em-dash-separated segments —
  `{TileName} — {RealmDisplay} — {Node}/{Install}` — dropping
  any segment with no data. RealmDisplay prefers the linked
  group's DisplayName and falls back to
  `realm {first-8-of-realm_id}…` parsed out of SessionIdentity
  when no group is linked. Matches pre-5h-6
  `FormatSessionLabel` output for unlinked installs so visual
  experience for unlinked rows is unchanged — the upgrade is
  that linked installs now show the realm by name.

  **Manager dispatch:** new `HistoryQueryService.LoadResolvedInstances`
  pre-pass walks Instance + Installation + Node in one query
  and pulls SharedConfigGroup DisplayNames in a second, merging
  results into a per-InstanceId `ResolvedInstance` map. The
  new `ResolveSourceLabel` static helper builds the context,
  dispatches to the plugin via `PluginRegistry.GetPlugin(GameId)`,
  and falls back to `BuildDefaultSourceLabel`
  ("Node/Install/Instance", skipping empty segments) when the
  plugin doesn't opt in, returns Nothing, or throws. Plugin
  exceptions caught defensively — a misbehaving plugin's
  formatting bug shouldn't kill the whole query.

  `TimelineRow` and `SnapshotRow` both gained a `SourceLabel`
  property; `SnapshotRow` additionally gained `InstanceId`
  (captured from the join event during activity replay) since
  the existing snapshot pipeline didn't preserve it.

- **Show Logs toggle persistence per instance.** The
  InstancePanel's "Show Logs" toggle now persists across
  panel disposal and reconstruction, so navigating away
  from an instance and back keeps logs visible if they were
  visible before. Implementation is a class-shared
  `ConcurrentDictionary(Of String, Boolean)` keyed by
  InstanceId; the toggle writes its state on every user
  change, and a new `OnLoad` override reads the saved value
  and applies it (with a `_restoringShowLogs` flag to
  suppress the redundant write-back and the auto-select-
  Logs-tab side effect during restore). Restore runs from
  `OnLoad` rather than the constructor because
  `ShowLogsTab` uses `Me.BeginInvoke` for its deferred
  initial fill, which throws `InvalidOperationException`
  against a not-yet-parented UserControl. Manager-restart
  scope by design — a fresh manager session starts with
  logs hidden everywhere.

- **Last-selected tab persistence per panel type.** Both
  InstancePanel and InstallationPanel now remember the
  user's tab selection across navigation. Two separate
  class-shared `Private Shared` String fields (one per
  panel class) hold the last-selected tab's `.Text`; each
  panel's `OnLoad` walks `_tabs.TabPages` looking for a tab
  whose Text matches the saved value and selects it,
  guarded by a `_restoringTabSelection` flag to suppress
  the `SelectedIndexChanged` handler's write-back during
  restore. Text-keyed identity rather than index because
  dynamic tabs (Logs toggle on InstancePanel, plugin-
  supplied managed-files and editor tabs, Progress tab on
  InstallationPanel during install/update) shift indices
  across panels — Configuration might be at index 1 on a
  Last Oasis instance and 1 on a Factorio instance too,
  but with a different count of trailing tabs, so any
  index-based scheme would be brittle. Tabs that exist on
  only some panels (e.g., "Server Settings" on Factorio
  but not Last Oasis) fall through cleanly to the default
  tab when the saved name doesn't match. Handler hookup
  happens AFTER the initial tab Add calls in
  `InitializeControls` so the synthetic `SelectedIndexChanged`
  that fires on the first Add (SelectedIndex `-1 → 0`)
  doesn't pre-write a default tab name. Instance and
  installation preferences are deliberately independent —
  flipping through instances on Configuration doesn't drag
  installation panels along. May 2026 user feedback
  measured this as removing about 80–90% of the
  navigation clicks involved in comparing configurations
  or logs across instances during live operation.

- **Process re-adoption on node startup.** The node now reads
  its persisted `InstanceSnapshots` table at startup and
  re-attaches to game-server processes that survived the
  previous node session. For each snapshot row,
  `ProcessManager.AdoptSnapshots`:

  1. Calls `Process.GetProcessById(snapshot.Pid)` to find the
     live process (cleanly removes the snapshot if the PID is
     gone).
  2. Verifies identity by comparing `proc.StartTime.ToUniversalTime()`
     against the saved `StartedAtUtc`. Match tolerance is 60
     seconds, generous enough to cover system-clock
     adjustments during downtime (NTP correction at boot,
     manual time changes) and well within the timescale that
     would distinguish real PID reuse — Windows recycles PIDs
     over minutes-to-hours in practice, not seconds. To make
     the comparison effectively exact, `FinalizeStart` now
     records `proc.StartTime.ToUniversalTime()` rather than
     `DateTime.UtcNow` so writer and reader pull from the same
     kernel-fixed source.
  3. On match, rebuilds the `ManagedInstance` with the live
     `Process` handle, restores crash-policy fields from
     `CrashPolicyJson`, restores spawn metadata (Strategy,
     StdoutIsLog, RequiresConsoleIsolation,
     LogTailerStartDelayMs), reconstructs a `ProcessStartInfo`
     for post-adopt crash-restart, deserializes log file paths
     + parse rules from their respective JSON columns, attaches
     the same `OutputDataReceived` / `ErrorDataReceived` /
     `Exited` handlers as a fresh spawn, sets
     `EnableRaisingEvents=True`, registers parse rules with
     `EventStore`, starts file tailers (which resume from saved
     `TailerPositions` cursors), re-arms the crash-count-reset
     timer, and pushes the record into `_instances`.

  After the pass, the new node process is functionally
  indistinguishable from the prior one with respect to those
  instances: same crash detection (`Process.Exited` routes
  through `HandleProcessExited` normally), same graceful-stop
  path (`AttachConsole(pid)` in `GSM.CtrlCSender` works against
  any PID with a console regardless of which node process
  owns the handle), same manager-facing status reports. The
  manager's existing rule re-push on reconnect
  (`UpdateParseRulesAsync` from `EnsureLogStreamAsync`) layers
  on top to reconcile any plugin rule changes that happened
  while the node was down — the snapshot's rules are stale
  until that push but the window is typically the next 3-second
  poll cycle. Synchronous before `app.Run()` so endpoint
  requests never see a transient "everything is Stopped" view.

  Schema migration is additive: nine new columns on
  `InstanceSnapshots` (`ExePath`, `Arguments`,
  `WorkingDirectory`, `LogFilePathsJson`, `ParseRulesJson`,
  `Strategy`, `StdoutIsLog`, `RequiresConsoleIsolation`,
  `LogTailerStartDelayMs`) discovered via `PRAGMA table_info`
  so an upgrade-in-place picks them up once and a fresh
  install converges to the same final shape. Pre-migration
  snapshots have NULL in the recovery columns and are
  treated as un-adoptable (logged + cleaned up rather than
  crashing the load).

  Known limitation: Strategy A (`StdoutIsLog=True`, redirected
  stdio) game processes lose their stdout capture on adoption
  because the stdout pipe was owned by the previous node and
  is no longer connected. Neither LO (Strategy B) nor Factorio
  (Strategy C) hits this path today; theoretical for any
  future plugin that opts into A. Custom environment variables
  set at spawn time are not yet round-tripped through the
  snapshot — a post-adopt crash-restart for an instance that
  needed env vars would spawn without them. No current plugins
  use env vars, but follow-up if one does.

  Closes the node-binary-update workflow: stop node → swap
  binary → start node → instances re-adopted automatically,
  manager reconciles rules on next poll. Players stay
  connected; no operator intervention beyond the binary
  swap itself.

- **Manager re-pushes parse rules on reconnect to a running
  instance.** New `POST /api/instances/{id}/parse-rules` node
  endpoint accepts a `List<LogParseRule>` body and routes to a
  new `EventStore.UpdateParseRules` method that swaps the
  compiled rule list atomically under the state lock while
  preserving the per-instance Players, ServerState,
  PendingRemoteAddress, and PendingIdentitiesByPlatformUserId
  caches. The Manager's `EnsureLogStreamAsync` now invokes the
  matching `UpdateParseRulesAsync` client method right before
  resubscribing to the SSE log stream.

  **Scope clarification (May 12, 2026):** this refresh path
  only fires when the Manager restarts against a node that is
  STILL UP from the prior session. In that case the node's
  `ProcessManager._instances` still has the running instances
  registered with state=Running, so `EnsureLogStreamAsync`'s
  Running/Starting branch is taken and the rule push fires.

  It does NOT close the node-binary-update pain end-to-end
  by itself: on a node restart the new node process starts
  with an empty `_instances` dict (nothing reads the
  persisted `InstanceSnapshots` table on startup yet), so
  `GetInstanceStatus` reports `State=Stopped` for every
  running game process and the Manager skips the rule push.
  Closing that gap requires the process re-adoption work
  tracked in Backlog. The persisted `TailerPositions` table
  already covers log-event continuity for the file-tailed
  games (LO, Factorio) across a node restart — events
  written during the node-down window get streamed in by the
  tailer resuming from the saved byte offset.

  Graceful on older nodes — a 404 from the missing endpoint
  surfaces as `NodeApiException(StatusCode=NotFound)` and the
  reconnect proceeds without the refresh.

  **Now composes with process re-adoption (see above).** On a
  node restart, the new node first adopts the live game
  processes (rebuilding the `_instances` dict before
  `app.Run()`), so the manager's poll sees `State=Running`,
  triggers the rule re-push, and EventStore swaps to the
  current plugin rules while keeping the player/server-state
  caches the adoption rebuilt. The full node-update sequence
  is now: stop node → swap binary → start node →
  everything reconciles automatically. The earlier scope
  clarification ("only fires on manager-restart, not
  node-restart") no longer applies as of the adoption work.

- **History viewer "Instance" column.** The History timeline
  ListView now shows a fourth column titled "Instance" with
  the format `<NodeName>:<InstanceName>:<InstanceId>` per row,
  resolved via a single JOIN against Instances + Installations
  + Nodes once per query. The full InstanceId GUID is
  preserved (not truncated) because LO writes per-instance
  log files as `{InstanceId}.log` — keeping the raw string
  visible lets an operator grep the on-disk log for the exact
  line that produced any chat / join / leave row. Rows whose
  InstanceId no longer resolves to a live (instance,
  installation, node) triple render as
  `(deleted):(deleted):{instanceId}` so retrospective
  debugging of removed servers still works. Snapshot mode
  is unaffected for now — the column is timeline-only since
  that's the event-anchored view where the lookup matters.

- **Three-key player identity resolution in `EventStore`.**
  Player records now merge partial events via any known
  identity key — `CharacterId` (primary, from the Login
  line), `PlatformUserId` (from `Processing character
  update`), `DisplayName` (from `Persisting`), with
  secondary fallbacks on `RemoteAddress` and
  `PlatformPersona`. The `Persisting <DisplayName>,
  UniqueNetId = <Platform>:<PlatformUserId>` log line is now
  a recognised `PlayerIdentity` event in the Last Oasis
  plugin, bridging DisplayName ↔ PlatformUserId. Combined
  with the existing `Processing character update` line
  (PlatformUserId ↔ CharacterId) and the Login line
  (PlatformPersona + CharacterId), the three log lines form
  a complete identity chain over PlatformUserId without
  external API dependency or session-cookie scraping.

- **Pending-identity stash for race-window handling.** When
  a Persisting line fires for a player whose
  Processing-character-update hasn't landed yet (the typical
  case for a player whose connection arrived mid-autosave
  tick), the (DisplayName, PlatformUserId, Platform) tuple
  is stashed in
  `InstanceEventState.PendingIdentitiesByPlatformUserId`
  keyed by PlatformUserId. The next event that resolves
  PlatformUserId to a session drains the stash, applying
  the deferred DisplayName binding. Stash entries are
  removed on session leave so they don't accumulate across
  long-running sessions; process restart resets in-memory
  state entirely.

- **Chat-as-DisplayName-source fallback.** Post-5g-1 Last
  Oasis builds emit `Persisting <DisplayName>, UniqueNetId
  = ...` only at player departure (~250ms before
  disconnect, which the manager's 3-second poll cycle
  reliably misses). Without a second source, renamed
  characters would show their Steam persona for the entire
  session and only flip — if at all — right as they're
  leaving. The chat handler now writes the speaker back to
  the session's `DisplayName` when a name-based lookup
  matches an existing session OR when exactly one player is
  tracked on the tile (single-player fallback). Multi-player
  tiles with simultaneous unresolved renamed players fall
  through with no attribution rather than guessing — chat
  rows persist with the speaker as `DisplayName` text but no
  `PlatformUserId`/`CharacterId` linkage, and the live
  player list keeps showing PlatformPersona for those
  players until 5g-2 ships the persistent DisplayName
  lookup at Login.

- **`-EnableCheats` as a default Last Oasis launch
  argument.** Admin chat commands (kick, ban, give,
  teleport, etc.) require this flag at server launch —
  without it the command parser is disabled and admin chat
  lines are silently ignored. Was previously the operator's
  responsibility to add via custom args; now on by default
  since most operators want it and forgetting it produces
  a confusing "my commands don't do anything" symptom with
  no error feedback.

- **Render-time chat fallback in the History window.**
  `HistoryQueryService.LoadTimeline` now backstops the
  write-time identity snapshot for activity rows whose
  `DisplayName` came back empty or equal to the raw
  `PlayerName`. For these rows, a render-time lookup
  against `ChatMessages` by `(SessionIdentity,
  PlatformUserId)` pulls the most recent chat
  `DisplayName` the player used and overrides
  `TimelineRow.PlayerName`. One indexed query per
  distinct (sid, pid) pair, leveraging the `IX_chat_pid`
  index from 5g-1. Handles edge cases the write-time
  snapshot can't cover: returning players joining on a
  Node whose `players` table doesn't have them yet
  (cross-Node migration, fresh `players.db`), the
  pre-5g-2b Conan case where the snapshot caught the
  FLS handle in both slots, and the short-session race
  where the Node hadn't yet resolved DisplayName at
  snapshot time. Players who never chatted within the
  queried scope fall through to the raw `PlayerName`.

### Fixed

- **Manager-side log-stream doubling on instance restart.**
  Stopping and restarting an instance produced every-line-
  doubled log output for the rest of the instance's session
  — not just startup bursts, but slow steady-state lines
  too. Root cause was a race between `StartInstanceAsync`'s
  success-path call to `StartLogStream` and
  `BackgroundPollLoopAsync`'s stream-health check: the
  background poll could observe
  `_liveStates(id).CurrentState = Running` and an empty
  `_logStreamCancellations(id)` during the brief window
  between the manager setting the state and the call chain
  reaching `StartLogStream`. Both callers then ran
  `StartLogStream` concurrently, and the dict assignment
  (`_logStreamCancellations(instanceId) = cts`) was a naked
  upsert — overwrote without cancelling the prior cts. The
  previous task's `CancellationTokenSource` was now orphaned
  (no longer in the dict, no remaining reference to call
  `Cancel()` on), and its background SSE consumer ran
  forever in parallel with the new one. Every line emitted
  by the instance arrived via both subscribers and got
  written twice to the manager ring buffer. Fix is an
  idempotent `StartLogStream`: under a new `_logStreamLock`
  SyncLock, the method `TryRemove`s any existing entry,
  calls `Cancel()` + `Dispose()` on it, clears the stale
  `_logParsers` entry, then installs the fresh cts.
  Whichever caller reaches the lock second cancels the
  first, and the orphaned task's existing compare-and-remove
  in its Finally sees a mismatched cts in the dict and bails
  correctly. `Task.Run` runs INSIDE the lock so parser
  registration in `_logParsers` happens before the streaming
  task starts — otherwise the new task could read lines
  while a previous parser is still registered.

- **Rich-text log viewer beep cascade.** With the Logs tab
  open during a Last Oasis startup burst, the Windows system
  ding sound played continuously for as long as lines were
  flowing in. Rich-edit responds to `EM_REPLACESEL` on a
  `ReadOnly = True` control by calling `MessageBeep` BEFORE
  performing the replacement — the append still succeeds
  (which is why the log content was visible correctly), but
  every call rings the system bell. `RichTextBox.AppendText`,
  `SelectedText = ""`, and the trim path's `Select() +
  SelectedText = ""` all funnel through `EM_REPLACESEL`, so
  any one of them is enough to produce the cascade. Fix in
  `InstancePanel.AppendLogLinesToTab` brackets the redraw-
  suspended mutation block with `_logTextBox.ReadOnly =
  False` at the start and restores `= True` in the Finally
  alongside the WM_SETREDRAW re-enable. The toggle window is
  invisible to the user because the `WM_SETREDRAW = 0`
  across the same span prevents the rich-edit from
  processing input events while the flag is flipped.

- **UE4 dedicated-server log tailing on Linux nodes.** The
  node's file tailer used `New FileStream(path, FileMode.Open,
  FileAccess.Read, FileShare.ReadWrite Or
  FileShare.Delete)`, which works on Windows but fails on
  Linux when the UE4 process has the file open with an
  advisory `flock(LOCK_EX)`. .NET 8's `FileStream` on Linux
  consults the advisory lock and refuses the open; `lsof`
  showed `MistServ ... 3uW` (fd 3, mode r+w, capital W =
  write lock on the entire file). Fix is a libc.open
  bypass: `<DllImport("libc")> LibcOpen(path, O_RDONLY)`
  returns a raw fd that ignores the advisory flock entirely,
  wrapped in a `SafeFileHandle(handle, ownsHandle:=True)`
  and passed to `New FileStream(handle, FileAccess.Read)`.
  Windows continues to use the regular FileStream
  constructor (no flock semantics there).
  `OpenLogFileForTailing(path)` encapsulates the platform
  switch so callers don't have to know.

- **Spawn-path file-tailer duplication regression.** A fresh
  instance start on Strategy A (StdoutCapture, the path
  Linux LO is forced into via `ResolveStrategy`) was starting
  BOTH the stdout-capture ingest AND a file tailer for the
  same .log file, producing exactly one duplicate per line.
  The adoption path needs the file tailer because it has no
  stdout pipe to inherit, but the fresh-spawn path doesn't.
  `ProcessManager.FinalizeStart` now gates the tailer start
  on `If managed.Strategy <> SpawnStrategy.StdoutCapture
  Then StartFileTailers(...)`. The adoption path in
  `TryAdoptOne` unconditionally starts file tailers as
  before. Strategy B (Windows hidden console) and Strategy C
  (Linux Factorio with native terminal) are unaffected
  since they don't capture stdout for the log buffer either
  way.

- **Linux file-tailing gap for UE4 verbose categories.** The
  duplication fix above ("Spawn-path file-tailer duplication
  regression") over-corrected for Linux + file-logged games:
  with the gate set to "Strategy = StdoutCapture means no
  tailer" and Linux forced onto StdoutCapture by
  `ResolveStrategy` (CREATE_NEW_CONSOLE is Win32-only, so
  there's no Strategy B/C path on Linux), a Linux LO instance
  ended up with stdout as its only log source. The UE4 Linux
  console output device filters at Display verbosity by
  default — the documented "mirror everything to stdout and
  stderr" behaviour does not hold for Verbose-category lines.
  `LogPersistence: Verbose: Processing character update` and
  `LogPersistence: Verbose: Persisting <name>'s character`
  never reached the EventStore, so player sessions on Linux
  instances never bound `PlatformUserId`, Persisting lines
  couldn't correlate to the session via pid lookup, and
  in-game character names never resolved past the Steam
  persona. The Windows instance of the same plugin ran on
  Strategy B and tailed the file directly, which is why it
  worked there on identical EventStore + plugin code.

  Fix moves the duplication-avoidance condition off of
  `Strategy` and onto `CaptureStdout`, which becomes the
  single source of truth for whether stdout duplicates the
  file. `StartInstanceAsync` now sets `CaptureStdout = True`
  only when the plugin has NOT declared file log sources —
  if it has, the file is the authoritative source, stdout
  gets drained (so the child doesn't block on a full pipe
  after ~4KB) but its data is not forwarded to the ring
  buffer or EventStore. `FinalizeStart` then starts file
  tailers whenever `hasFileLogs AndAlso Not CaptureStdout`,
  which captures all three cases correctly: Linux
  Strategy A + file logs (new behaviour, file is the source
  via libc.open tailer), Windows Strategy B/C + file logs
  (unchanged, file is the source via FileStream tailer), and
  any Strategy A without file logs (unchanged, stdout is the
  source). `TryAdoptOne` applies the same hasFileLogs-aware
  CaptureStdout assignment so a post-adopt crash-restart
  spawns with behaviour matching the original. Adoption path
  still unconditionally starts tailers since the stdout pipe
  of an adopted process can't be re-attached regardless.

- **Ghost "Unknown" player entries for persisted-but-not-
  connected characters.** The Linux file-tailing fix above
  exposed a second-order bug: with the tailer now running on
  Linux, EventStore began seeing every `LogPersistence:
  Verbose: Processing character update` line UE4 emits —
  including the ones fired during server boot for every
  character persisted on the tile, and during autosave ticks
  for characters whose players are offline but whose bodies
  still occupy the tile. The Last Oasis plugin classified
  that line as `Kind = PlayerJoin` (to close a world-travel
  race documented inline in the plugin), so each of those
  loads called `FindOrCreateSession` and materialised a
  session in `state.Players` with `cid + pid` and no
  `PlatformPersona`/`DisplayName` — the Manager player list
  rendered each as "Unknown" because both name surfaces
  were empty. On a tile that retained, say, twelve persisted
  characters from prior sessions, the player list would
  immediately show twelve Unknowns on instance start, with
  no actual players online.

  Fix is a design change to the world-travel correlation:
  the LO plugin now classifies Processing-character-update
  as `Kind = PlayerIdentity` (enrichment-only), and the
  EventStore carries a new cid-keyed pending-identity stash
  alongside the existing pid-keyed one. When the event
  arrives before any session exists for the CharacterId, the
  `(PlatformUserId, Platform)` pair stashes under the cid in
  `InstanceEventState.PendingIdentitiesByCharacterId`. When a
  subsequent Login creates the session via
  `FindOrCreateSession`, a new `DrainPendingCidIdentity`
  helper applies the stashed pid to the session — closing
  the same world-travel race the PlayerJoin classification
  did, but without materialising a session for events that
  fire without an associated network connection. Stash
  entries that never get drained (no Login ever arrives for
  that cid) sit idle until the instance is unregistered;
  bounded by the persisted-character count on the tile,
  typically < 100.

  `DrainPendingCidIdentity` must run BEFORE
  `DrainPendingIdentity` in the enrichment flow — the former
  sets PlatformUserId on the session, which the latter then
  uses to look up the pid-keyed (DisplayName, Platform)
  stash. PlayerJoin and PlayerIdentity cases both call them
  in this order. PlayerLeave cleanup removes from both
  stashes so neither accumulates across long-running
  sessions. The `PendingIdentity` class gained a
  `PlatformUserId` field used only on cid-keyed entries
  (pid-keyed entries use the dict key itself).

  Sequence trace for the three relevant scenarios under the
  new design:

    World-travel arrival (Processing-character-update fires
    before Login): cid stash captures (pid, Platform); Login
    creates the session; DrainPendingCidIdentity applies pid;
    Persisting (if pid stash had landed) drains via
    DrainPendingIdentity.

    Fresh connect (Login fires before Processing-character-
    update): Login creates the session with cid+persona;
    Processing-character-update finds the existing session
    by cid and enriches with pid directly via the
    PlayerIdentity enrichment branch — no stash involvement.

    Persistence-only events (server boot, autosave of
    offline-on-tile characters): cid stash accumulates entries
    for characters with no current connection; player list
    stays correctly empty. Stash drains naturally if those
    characters ever Login, or is dropped on instance
    unregistration.

- **LO Persisting regex truncated names ending in `'s
  character`.** The Persisting-line capture in the Last Oasis
  plugin assumed UE4 appends a literal `'s character` suffix
  to character names in `LogPersistence` output — the regex
  was `Persisting (?<DisplayName>.+?)'s character, UniqueNetId
  = (?<Platform>\w+):(?<PlatformUserId>\d+)`. That assumption
  is wrong: UE4 emits character lines as `Persisting <Name>,
  UniqueNetId = <Platform>:<UID>` with no appended suffix.
  Any in-game name happening to end in `'s character`
  (which is a natural way to name a character; the on-card
  display in-game shows it directly) got silently chopped at
  the regex's expected suffix — `site's character` captured
  as just `site`, persisted into the `players` table under
  that truncated form, and the cached name was then used to
  hydrate the player list on every subsequent join. The user
  saw their character's correct name appear briefly only on
  the rare occasions a Persisting tick landed during an
  active session, immediately replaced by the truncated
  cache on the next reconnect.

  Fix changes the anchor from `'s character,` to `,
  UniqueNetId` — the literal token that discriminates the
  character-shaped line from the actor-shaped one
  (`Persisting <ActorClass>, ActorGuid = {GUID}`, which uses
  `, ActorGuid =` instead and never matches). The non-greedy
  capture still backtracks through any commas embedded in the
  name until the `, UniqueNetId` anchor matches, so names
  like "andre, the wanderer" capture fully. Stale truncated
  entries already in the `players` table get overwritten on
  the next Persisting tick after a player joins, so no
  one-shot cleanup is needed — the natural autosave cadence
  fixes the cache within ~2 minutes of the affected player's
  next session.

- **Chat duplication on adoption replay.** The
  `skipResume:=True` parameter introduced for node-adoption
  EventStore rebuilding prevented the in-memory caches from
  re-firing notification events on replayed lines, but the
  chat persistence path still called `INSERT INTO
  chat_messages` with `timestampUtc = DateTime.UtcNow` taken
  from `EmitTailLine`. On every adoption, the entire ring
  buffer's chat lines re-flowed through `ProcessLine` and
  got persisted again with fresh server-side timestamps,
  producing duplicate rows that diverged only in timestamp.
  Fix is two-pronged: a new `TryParseUe4Timestamp(text)`
  extracts the `[YYYY.MM.DD-HH.MM.SS:fff]` prefix on UE4
  lines and uses that as the persisted `timestamp_utc`, and
  a new `ux_chat_dedup` UNIQUE INDEX on `(instance_id,
  timestamp_utc, display_name, text)` plus a switch to
  `INSERT OR IGNORE` makes the persistence idempotent
  regardless of replay count. Lines without a parseable UE4
  timestamp (Factorio, plain text) fall back to
  `DateTime.UtcNow` and are still de-duped by the index —
  the practical collision rate on real chat is negligible.

- **Node SSE backfill / live-stream subscription race.**
  The Last Oasis startup burst (hundreds of lines in 2-3
  seconds) was producing doubled lines on FRESH stream
  subscriptions — distinct from the manager-side double-
  subscription bug above, and visible on a single subscriber
  alone. Root cause in `InstanceBuffer.StreamToResponseAsync`:
  the old code took the buffer's internal SyncLock twice in
  sequence — once via `AddSubscription` which set
  `subscription.LastSequence = _writePos - 1`, and once via
  `GetTail(tailLines)` which read `_writePos` again. Between
  the two acquisitions, an `Append` could fire and bump
  `_writePos`; the new line then appeared in BOTH the tail
  returned to the client AND in
  `GetLinesSince(LastSequence)` on the subscription's first
  live-stream read. Fix is a new
  `SubscribeAndGetTail(subscription, tailCount)` method that
  takes a single SyncLock and uses one consistent
  `_writePos` snapshot for both halves: tail returns
  `(_writePos - take)..(_writePos - 1)` and live stream
  starts at `_writePos`, no overlap and no gap. Legacy two-
  call path remains as a deprecated entry point for callers
  that don't need both halves.

- **Manager-side `SessionIdentity` fallback for adopted
  instances.** The Last Oasis parser's session identity is
  committed by a 4-line tile-load sequence (`Started hosting
  tile` → realm_id → tile_name → tile_id), but on adoption
  that sequence can be hours old and has rotated out of the
  node SSE ring buffer (4096 lines). The manager parser came
  up with `CurrentSessionIdentity = Nothing`, and any chat
  or player-activity rows recorded on the parser's first
  hour after adoption went to disk with empty session
  identity, orphaning the rows from any tile context. Fix
  is a layered `ResolveSessionIdentity` helper: parser-
  committed identity first (live path, unchanged), then a
  per-instance in-memory cache, then a SQLite lookup against
  `SessionHosts WHERE InstanceId = ? AND HostedUntilUtc IS
  NULL ORDER BY HostedFromUtc DESC LIMIT 1` to find the
  most recent open hosting record, then finally a
  synthesized `{gameId}:{instanceId}` if nothing matches.
  Self-healing: once the parser commits a real identity
  (e.g., when the next 4-line sequence fires on tile
  change), the cache invalidates and future lookups bypass
  the DB. Cache is dropped on instance stop via
  `ClearPlayerTracking`.

- **Linux Ctrl+C signal isolation for game children.**
  Stopping the node service via Ctrl+C on Linux was also
  killing every game-server child because the kernel routes
  SIGINT to the controlling terminal's entire process
  group. Game children spawned by the node were in the same
  process group by default. Fix wraps the spawn in `setsid`
  on Linux: `ProcessManager.WrapInSetsidIfLinux(psi)`
  rewrites the `ProcessStartInfo` so the child runs as
  `setsid <exe> <args>`, detaching it into a new session
  and process group. The node's own Ctrl+C handler still
  signals its game children explicitly via the gsm-broker
  path when an instance stop is requested; the only thing
  setsid blocks is incidental terminal propagation.
  Idempotent — calling on a psi that's already setsid-
  wrapped is a no-op.

- **Last Oasis Linux server authentication and AppID file.**
  The Linux Last Oasis server (`MistServer-Linux-Shipping`)
  was failing its Steam authentication on launch because
  the Linux distribution requires (1) `Mist` as positional
  argument 0 in the launch command, and (2) a
  `steam_appid.txt` file in `Mist/Binaries/Linux/` (the OS-
  specific binaries directory) rather than the install
  root. `LastOasisPlugin.BuildLaunchArguments` now prepends
  `"Mist"` to the argv list on Linux only, and the
  `WriteFileStep` for `steam_appid.txt` resolves to the
  platform-specific path. Windows is unaffected.

- **`DateTime.Parse` adoption crash on node startup.**
  `NodeProgram.LoadAllInstanceSnapshots` was passing both
  `DateTimeStyles.RoundtripKind` and
  `DateTimeStyles.AssumeUniversal` to `DateTime.Parse`. The
  two are mutually exclusive — RoundtripKind says "honor
  the kind designator in the string", AssumeUniversal says
  "force UTC on un-designated strings". .NET 8 throws
  `ArgumentException` rather than silently picking one,
  which crashed the node startup adoption pass entirely.
  Fix is to drop `AssumeUniversal` — every snapshot
  timestamp this code reads is written by
  `ToUniversalTime().ToString("o")` which always includes
  the `Z` designator, so `RoundtripKind` alone produces
  correct UTC parsing.

- **Steam Guard email spam from periodic version checks.** The
  Manager's `CheckForUpdatesAsync` was unconditionally
  resolving the installation's stored Steam credentials and
  sending them to the node for the version check. On nodes
  Steam didn't recognise — typically Linux nodes on
  residential connections that don't share a fingerprint with
  the user's normal Steam client — every hourly check
  triggered a Steam Guard challenge against the account,
  blasting the user's inbox with verification-code emails
  they never asked for and reporting "failed" since the
  challenge couldn't be answered in the polling path. The
  request now sends `SteamCredentials = Nothing`, which the
  node-side `CheckAppVersionAsync` was already wired to
  interpret as `+login anonymous`. `+app_info_print` is a
  read-only public-metadata query against Steam's app DB —
  the public branch's `buildid` is exposed without a license
  check, which is why anonymous works for paid apps too
  (what requires authentication is `+app_update`, the depot
  download, which is the install/update path and unaffected
  by this change). On a node that's been getting Steam Guard
  spam, the next version-check cycle should run quietly with
  no email and produce a usable buildid for the
  `LatestKnownVersion` comparison.

- **"Check for Updates" surfaces actual error messages.**
  `VersionCheckService.CheckInstallationAsync` previously
  returned `Task(Of Boolean)`, swallowing every failure mode
  into a `False` return and leaving the manual-button UI to
  fall back to a `"Check failed (see log for details)"`
  placeholder. Return type is now `Task(Of VersionCheckResult)`
  with a populated `ErrorMessage` on every failure path —
  installation not found, plugin not loaded, Steam-side
  error message from the node, plugin exception, empty
  upstream response, outer Catch. The InstallationPanel's
  manual `OnCheckForUpdates` handler renders short errors
  in the existing status label below the button, and routes
  long or multi-line errors (the multi-line SteamCMD
  missing-libs hint from the Linux pre-flight being the
  motivating case at ~500 chars across multiple lines) into
  a resizable monospace dialog via the new shared
  `DetailedErrorDialog` helper. The label still shows a short
  first-line summary in the dialog case so the post-dismiss
  state is informative. Threshold is `> 150 chars OR contains
  a newline`, picked to match "doesn't fit in a 400px-wide
  AutoSize label" in practice.

- **Version label clarified to "last successfully checked".**
  The InstallationPanel's version line previously rendered
  the freshness suffix as `, checked Xh ago`, which read
  ambiguously when the timestamp belonged to a long-ago
  success and recent checks had been failing. `LastVersionCheckUtc`
  is, by design, only updated on a successful check (see the
  header comment on `VersionCheckService`) — a transient or
  permanent failure leaves the timestamp untouched so the
  poller retries promptly. The label now reads `, last
  successfully checked Xh ago`, making the success-only
  semantics visible at a glance: a user looking at
  `(update available, last successfully checked 14h ago)`
  knows the timestamp isn't lying about recent failures, just
  about the last good result. No schema or data change —
  string-only.

- **In-tab player list now drops players on UE4 control-channel
  close.** The node-side Last Oasis PlayerLeave parse rule
  matched only `LogNet: UNetConnection::Close:` lines, but some
  Last Oasis disconnect flows fire only `LogNet: UChannel::Close:
  ... ChIndex == 0 ... RemoteAddr: <addr>,` and never produce a
  separate `UNetConnection::Close` line at all — the channel-0
  (control-channel) close IS the disconnect signal. EventStore
  never removed the player from its in-memory `state.Players`
  dict, so the in-tab player list kept showing them indefinitely.
  The History viewer captured the leave correctly via the
  manager-side parser, which already matched both close forms,
  producing a visible asymmetry between live status and history.

  The rule now matches `UChannel::Close:` with `ChIndex == 0`
  OR `UNetConnection::Close` and captures the RemoteAddr from
  either form, which is enough for the EventStore's
  RemoteAddress-based session lookup to resolve and remove the
  session. The `ChIndex == 0` guard restricts the new branch to
  the control channel — actor channels close mid-game without
  meaning a player disconnect, and matching them would produce
  false-fire removals. Per-event idempotency (FindExistingSession
  returns Nothing once the session is gone) makes a redundant
  later UNetConnection::Close on the same disconnect a safe no-op.

- **Conan parse rules mis-captured the FLS handle as
  `DisplayName`.** The Conan `Join succeeded:` and
  `Player disconnected:` log lines carry the FLS handle
  (Funcom-issued account identifier, e.g.
  `losno420#72569` or bare `blingity`) as their
  post-colon token, NOT the in-game character name —
  character names land later via chat lines or the
  Node's persistent players-table cache. The plugin's
  parse rules were capturing this token into the
  `DisplayName` group, polluting the Node's
  `PlayerSession.DisplayName` slot with platform-identity
  data and producing FLS-handle entries in the History
  window's Join/Leave rows for a character whose Chat
  rows correctly rendered the character name. Both
  captures renamed to `PlatformPersona` so the slot
  semantics match Last Oasis: FLS handle goes to the
  platform-identity slot (stable for the session's
  lifetime), DisplayName is free for the character name
  to land via chat or cache. Also closes a latent
  leave-side bug where, after chat had flipped the
  session's DisplayName to the character name, a leave
  event capturing the FLS handle into DisplayName would
  no longer match the session via the DisplayName key
  (fell through to RemoteAddress match, which works but
  is fragile under simultaneous disconnects). Conan
  `PlayerActivity` rows written before this fix are not
  backfilled and continue to render the FLS handle in
  the History window; sessions starting after the fix
  render the character name once chat has fired in the
  session or the Node's cache has the binding from a
  prior session. Currently-running Conan instances need
  affected players to disconnect and reconnect once for
  the new binding to take effect on their session — the
  Manager pushes the new parse rules to the Node
  automatically via `UpdateParseRulesAsync`, but the
  Node's in-memory `PlayerSession` state for already-
  connected players isn't re-evaluated.

- **Conan `Map Loaded` rule was a silent no-op.** The Conan
  plugin's parse rule for `LogWorld: Bringing World <MapPath>
  up for play` had `Kind = ParsedEventKind.Custom` while
  capturing into a well-known group name (`MapPath`, not
  `Custom_MapPath`). The Custom kind has no `Select Case`
  branch in `EventStore.ApplyMatch`, so the captured value
  didn't reach `ServerState.CurrentMapPath`; and
  `HarvestCustomFields` only scrapes capture groups whose
  names start with `Custom_`, so it didn't pick up the value
  either. The rule fired on every Conan boot and did
  literally nothing. Fix is a one-token change to
  `Kind = ParsedEventKind.TileLoaded`, matching the LO
  plugin's identical "Bringing World" rule.
  `CurrentMapPath` now populates on Conan instances and
  mirrors to `instance_state` for node-restart survival via
  the existing `PersistInstanceStateSnapshot` call.
  `TileId` / `TileName` stay empty since Conan doesn't use
  LO's tile model. No Manager-side rendering of
  `CurrentMapPath` exists yet on either game, so this is
  preparatory for future Overview UI rather than an
  immediate visible change — but it closes the
  inconsistency between the two identical-shape parse rules
  and gives Conan parity with LO on the contract-defined
  field.

### Notes

- **Residual identity-resolution gaps.** Two narrow cases
  remain where `PlayerActivity.DisplayName` can't be
  resolved at write time AND the render-time chat
  fallback can't bridge through (player never chatted in
  the queried scope), so the History window falls back to
  the raw `PlayerName` — Steam handle on Last Oasis, FLS
  handle on Conan:

  - **Short LO sessions.** A player joins, leaves before
    chatting AND before the first `Persisting` autosave
    tick lands (~2-minute window on post-May-2026 LO
    builds where Persisting fires only at departure),
    AND no prior session on the Node has cached their
    PlatformUserId→DisplayName mapping. Tracked as Phase
    5g-3 in the backlog; the hypothesis is that a richer
    transitive identity graph using LO's `Player_0_C` /
    `OasisPlayerController_0_C` / `ActorGuid` actor
    surfaces could close it.

  - **First-time-ever Conan players who don't chat.** The
    Conan `Character ID <n> has name <Name>` spawn line
    carries CharacterId + character name but no
    PlatformUserId, which the Node's current
    `PlayerIdentity` stash machinery can't bind to a
    session (it has paths for `pid+display, no cid` and
    `cid+pid, no display` but not for `cid+display, no
    pid`). Tracked as Phase 5g-2c in the backlog; closing
    it needs a third EventStore stash path plus a
    heuristic to drain it on subsequent chat.

  Both cases self-heal on a future session where the
  player either chats or has been chatted-before, since
  the Node's persistent `players` table caches the
  binding on first resolution. Pre-5g-2 Last Oasis rows
  and pre-5g-2b Conan rows also remain rendered under
  their original raw-PlayerName values; both phases
  intentionally skipped backfill (false-positive risk
  outweighs edge-case recovery) and there's no plan to
  revisit.

- **Conan InstancePanel "Steam name" column is
  technically misleading.** The column shows whatever
  lives in `PlayerSession.PlatformPersona`, which on
  Last Oasis is the actual Steam handle (or Xbox
  gamertag) but on Conan post-5g-2b is the FLS handle.
  Funcom doesn't log the Steam display name; the FLS
  handle is the most useful platform-identity surface
  available. Tracked as a cosmetic backlog item;
  candidate fixes are a generic "Persona" relabel or a
  plugin-driven column label.

## [0.2.0] - 2026-05-08

### Fixed

- Factorio direct-download installs on Linux now extract via
  native `tar` instead of SharpCompress 0.36.0. The previous
  extractor's Pax-extended-header handling didn't recognise
  the BSD-tar variant Factorio's build pipeline emits, so any
  entry with a path longer than the 100-char standard tar
  header limit landed on disk with its name truncated at
  boundary 100 — `rail-chain-signal-elevated.lua` became
  `rail-chain-signal-elevated.l`, the elevated-rails mod's
  `require()` chain failed at engine init, and map generation
  died with exit code 1 before any useful diagnostic surfaced.
  Native `tar -xJf` reads Pax records correctly, applies the
  long names, and `--strip-components=1` collapses the
  archive's `factorio/` wrapper directory in one flag — replaces
  the manual staging-and-hoist dance the SharpCompress branch
  needs to do by hand. SharpCompress remains the fallback for
  any future Windows direct-download case.
- Factorio direct-download installs on Linux now preserve the
  executable bit on `bin/x64/factorio`. SharpCompress's
  `WriteEntryToDirectory` doesn't apply the tar entry's unix
  mode field — files extracted with the process default umask
  (typically 0664), which left the Factorio binary unable to
  launch via `Process.Start` (errno 13 EACCES). Native tar
  applies modes during extraction; the SharpCompress fallback
  path also now calls `File.SetUnixFileMode` per entry on
  Linux as a backstop.
- Factorio direct-download installs no longer perpetually
  report "update available" immediately after a fresh install.
  The manager-side `BuildVersionStamp` produced
  "installed (timestamp)" strings that could never match the
  canonical "2.0.76" version the factorio.com API returns,
  so the version-check loop reported drift on every poll.
  Plugins implementing the new
  `IVersionAwarePlugin.GetInstalledVersionAsync` hook now
  stamp the installed-version field with a value that
  compares cleanly against `GetLatestVersionAsync`. Factorio
  reads `data/base/info.json` for it; the
  `VersionCheckService` also opportunistically re-reads on
  every poll cycle to upgrade pre-existing rows without
  requiring a reinstall.
- Factorio direct-download updates are no longer silent
  no-ops. `GetUpdateSteps` lacked a `DirectDownload` branch
  entirely, so updates on direct-download installs returned
  an empty step list; the runner executed zero steps and
  recorded "completed successfully" with no
  Download/Extract/Configure entries between the bookends.
  The plugin now emits a parallel branch matching the install
  path (re-fetch tarball, extract over existing files,
  re-write `config-path.cfg`).
- Factorio direct-download tarballs no longer leave the
  install layout one level too deep. The headless tarball
  wraps every entry under a `factorio/` top-level directory,
  which left plugin-relative paths like `bin/x64/factorio`
  and `data/base/info.json` resolving against
  `<install>/factorio/...` instead of `<install>/...`.
  Plugins now request top-level stripping via the new
  `DownloadFileStep.StripTopLevelDirectory` flag — native
  tar implements it via `--strip-components=1`, the
  SharpCompress fallback via a staging-and-hoist pass.
- Factorio direct-download installs no longer leak
  `@PaxHeader` pseudo-files into the install root. The
  BSD-tar pipeline emits Pax extended headers as type-flag-
  incorrect entries that SharpCompress treats as regular
  files. The native-tar branch consumes them as metadata;
  the SharpCompress fallback filters entries whose path
  segments match the `PaxHeader` / `@PaxHeader` /
  `PaxHeaders*` patterns.
- The Generate Map failure dialog now surfaces the engine's
  captured stdout/stderr in a resizable, monospace TextBox
  scrolled to the end. Previously the captured output
  existed in the `GenerateMapResponse.Output` field but was
  dropped by the UI's status-label-only rendering whenever
  the bare error message ran over 80 characters — the user
  saw `Process exited with code 1 (expected 0): ...` with
  no diagnostic context. Reused for any future plugin-
  driven file-generation operation that fails with engine
  output.

- Chat messages, player joins, and player leaves on Factorio
  instances no longer get re-ingested on every instance restart.
  Previously the tailer re-read the log file from the beginning on
  each start, causing EventStore to re-emit all prior events and
  produce duplicate rows in the Chat tab (the same message would
  appear three times after three restarts, each with a different
  timestamp).
- The History timeline now records a Leave event for every player
  who was online when an instance stops or crashes, instead of
  leaving dangling joins with no matching leaves. Manager-side
  player tracking flushes synthetic leave rows to PlayerActivity
  on terminal-state transitions (Stopped/Crashed/CrashLoopHalted)
  in addition to the existing graceful-stop path.
- Chat messages persisted to the node after a Manager restart no
  longer get silently filtered out of the manager's mirror. The
  cursor seeded from EF Core's SQLite store came back with
  `DateTimeKind=Unspecified`, which serialised without a `Z`
  suffix; the node parsed that as a local-time value and shifted
  the cursor forward by the manager's UTC offset, causing every
  chat between the original cursor and (cursor + offset) to be
  excluded from the response. `SeedChatCursor` now tags the value
  as UTC, and the node endpoint treats `Unspecified`
  `since` parameters as UTC defensively. Chats missed during the
  bug window will be back-mirrored on the next manager start.

- Per-node connection-failure log dedup. Multi-instance nodes
  going offline used to produce one warning per instance per
  3-second poll cycle — a 4-instance node down generated ~80
  warnings every 5 minutes, drowning out everything else.
  Now deduplicated per-node: the first failure logs once with
  `(further failures will be suppressed for up to 5 minute(s))`,
  a heartbeat every 5 minutes if the node's still unreachable
  so an operator who arrives mid-outage still sees the state,
  and the recovery line names the downtime
  (`Node X reachable again (was unreachable for 12m;
  suppressed 47 duplicate warning(s))`).
- Steam-managed installations now stamp `InstalledVersion`
  with a real buildid. Previously SteamCmd installs stamped
  a synthetic `installed (timestamp)` string that could never
  match the canonical `steam:{appId}@{branch} build {N}`
  format the `VersionCheckService` produces from
  `app_info_print` output, so every poll cycle reported drift
  on every Steam-managed installation. The node now reads the
  buildid from `appmanifest_{appid}.acf` after a successful
  install and surfaces it via the new
  `InstallProgressResponse.InstalledBuildId` field; the
  manager stamps `InstalledVersion` directly in the comparable
  format and no longer needs the previous fire-and-forget
  post-install version-check round trip.
- ANSI escape sequences stripped from SteamCMD stdout and
  stderr. Linux SteamCMD wraps every line in CSI sequences
  when writing to a pipe (`\x1b[0m...` resets and colour
  codes); without stripping, log files contained visible
  `[0m` artefacts and the manager's message field showed
  gibberish. Stripping happens at stdout/stderr receipt and
  again defensively at the content_log parser entry, so log
  files, the message field, and regex matching all see clean
  text. Windows SteamCMD doesn't colour its output, so this
  is a no-op there.
- Linux SteamCMD installs now report progress during the
  Downloading phase. The I/O counter poller (which derives
  bytes from `wchar` in `/proc/<pid>/io` to give smooth
  per-second progress) doesn't track SteamCMD's mmap-based
  writes on Linux until the kernel flushes dirty pages from
  the page cache — only catches up in bursts under cache
  pressure — so the bar sat at `0 / N MB (0.0%)` until ~50%
  of the download had elapsed. A new stdout-side parser
  handles SteamCMD's per-second
  `Update state (0xN) PHASE, progress: PCT (BYTES / TOTAL)`
  lines (these don't appear in content_log.txt, only stdout,
  so the previous Windows-only content_log path didn't see
  them). The I/O poller now writes cooperatively — only when
  its derived value is ahead of what's already there — so
  the stdout parser stays authoritative on Linux without a
  platform branch in the code; whichever source is denser
  wins each tick.
- SteamCMD's interactive REPL prompt no longer sticks as
  the post-completion display message. SteamCMD writes
  `-- type 'quit' to exit --` immediately before consuming
  the `+quit` verb; the line was reaching the message
  fallback in the stdout handler and stayed visible for the
  rest of the install-completion display. The fallback now
  skips whitespace-only lines and decoration rows (pure
  `-=_` divider rows, REPL prompts), and the success path
  overwrites the message with `Installation completed.`
  regardless of what the last stdout line was.
- `dotnet publish -r linux-x64` no longer drops a Windows
  `.exe` in the output folder. The two MSBuild targets that
  cross-compile and copy `GSM.CtrlCSender.exe` into the
  Node's publish output now gate on the runtime identifier
  — skipped when `RuntimeIdentifier` is set and doesn't
  start with `win`. Mirrors the existing pattern used for
  `install-service.bat` / `uninstall-service.bat`. When RID
  is unset (legacy framework-dependent publish, no platform
  commitment) the helper is still included on the
  Windows-bias assumption, parallel to the .bat files'
  behaviour.

### Added

- **Install-method-aware installation UI.** The Installation
  panel header now shows "Install method: Steam (SteamCMD)"
  / "Direct download" / "Manual" alongside the install path
  and version, surfacing what was previously implicit. The
  Steam-credential row is hidden in the New Installation and
  Edit Installation forms when the chosen method isn't
  SteamCMD, removing a confusing dead control on direct-
  download or manual installations.
- `DownloadFileStep.StripTopLevelDirectory` flag for plugins
  whose archives wrap every entry under a single top-level
  directory (autotools-style `factorio_2.0.76.tar.xz` → all
  entries under `factorio/`). The node's archive extractor
  detects the wrapper and hoists contents up to the install
  root. Native tar uses `--strip-components=1`; the
  SharpCompress fallback uses a staging-and-hoist pass.
  Defaults to False — existing plugins are unaffected.
- `IVersionAwarePlugin.GetInstalledVersionAsync(config,
  client, cancellation)` — reads the installed version off
  the node's filesystem (via the node's existing file-ops
  endpoints) in the same format `GetLatestVersionAsync`
  returns, so the manager's inequality check between the
  two can detect drift cleanly without false positives from
  synthetic provenance stamps. Called only for non-SteamCmd
  installs (Steam installs continue to use the appmanifest
  ACF buildid path). Currently implemented by Factorio
  (reads `data/base/info.json`); Last Oasis's plugin doesn't
  implement `IVersionAwarePlugin` so the contract change is
  non-breaking for it.

- **Save management tab on Factorio instances.** Lists save files
  on the node with Upload, Download, Delete, Rename, and Copy
  buttons. Streamed uploads/downloads handle 100MB+ saves without
  buffering on either side. The instance config's "Save File"
  field is now a picker dropdown populated from the same listing
  — no more typing exact filenames. Powered by a new opt-in
  `IManagedDirectoriesProvider` plugin interface; plugins that
  don't implement it (Last Oasis) keep their previous three-tab
  layout untouched.
- **"Generate New Map..." tab.** Click the Generate New button on
  the Saves tab to open a Generate Map sibling tab. Pick a preset
  (Default, Death World, Rail World, Ribbon World, Rich Resources,
  Lakes, Island), set a save name and optional uint32 seed, hit
  Generate. The save appears in the Saves tab when the operation
  completes. Powered by a new opt-in `IFileGenerationProvider`
  plugin interface that's generic across any one-off file-
  producing operation, not just maps.
- **"Server Settings" tab on Factorio instances.** Edit the 18
  most commonly-changed `server-settings.json` fields (name,
  visibility, factorio.com auth, game password, /commands
  permissions, auto-pause, autosave settings, AFK kick) without
  opening the JSON. The plugin owns parse/serialise so unknown
  fields a user added by hand outside the form
  (segment_size_*, max_upload_*, etc.) round-trip unchanged on
  Save. A missing file renders with schema defaults and gets
  created on first Save. Powered by a new opt-in
  `IInstanceFileEditorProvider` plugin interface.
- **Node-side file CRUD endpoints** for plugin-declared managed
  directories. Foundation for everything above. Path validation
  rejects `..` traversal and enforces plugin-declared root +
  extension allowlists per request. Uploads stream via
  `Request.Body.CopyToAsync` rather than buffering, so a 100MB
  Factorio save doesn't blow up the node's working set.
- **Pre-flight config validation on instance start.** Plugins'
  `ValidateConfig` hook now runs before every start; warnings
  surface as a warn-and-confirm dialog rather than letting the
  user discover the problem via a crash a few seconds later. The
  canonical case is Factorio with no save selected and "Use
  latest save" off — previously a 30-line stack trace, now a
  one-line MessageBox the user can act on.
- **Custom Discord panel composition.** Panels in the Discord
  bot integration are no longer locked to a fixed row layout.
  Each panel's row composition is configurable from the panel
  editor: pick which elements appear (state icon, instance
  name, state text, player count, next restart, game context
  line, node name, free-text separators), in what order, and
  whether the whole panel is grouped (none, by node, by game,
  by node-then-game). Existing panels render byte-identically
  until edited — the default layout reproduces the prior
  hardcoded format. Stored as JSON on the panel row; new
  `Layout:` and `Group by:` controls in the editor.
- **Per-panel Discord role overrides.** Panels can now define
  their own role-to-permission map that fully replaces the
  guild-default for that panel. Useful when one guild hosts
  multiple games with different ops teams — LO operators get
  Manage on the LO panel without also gaining it on the
  Factorio panel. Configured via a new "Override roles..."
  button on the panel editor. The status hint shows whether a
  panel uses the guild default or has overrides in effect.
  Whole-mapping override (not augmentation): if a panel has
  any overrides, the guild-default is not consulted for that
  panel — enables denial-by-omission.
- **Pagination on the Discord Manage dropdown.** Discord caps
  select-component options at 25; previously the Manage
  dropdown silently hid the rest. Panels with more than 25
  in-scope instances now show "Page X of Y" with prev/next
  buttons. Single-page panels are visually unchanged.
- **Bot connection retry buffer.** The Discord bot's outbound
  notification path used to drop events on transient failures
  (rate limits, network blips, brief disconnects). Failed
  events are now held in a per-destination ring buffer (cap
  100) and replayed on the next worker tick, ahead of fresh
  events so order is preserved. Permanent failures (channel
  deleted, bot lacks permission, malformed payload) still
  drop fast and log loudly. Buffer overflow during a long
  outage drops the oldest events to bound memory.
- **Live bot connection state on the Discord Bot form.** The
  status label now polls once per second and shows uptime
  when connected ("Connected for 2h 18m (since 14:23
  local)."). Previously the label stayed stale at
  "Connecting to Discord…" until the form was reopened.
- The node persists a per-(instance, log file) byte cursor in a new
  `TailerPositions` SQLite table. On instance start, the tailer
  resumes from the saved position when the file's first-256-byte
  fingerprint matches what was saved, otherwise falls back to the
  existing size-based heuristic. This fixes Factorio's chat
  duplication, eliminates the engine-state "Closed → InGame"
  zoom-through on restart, and gives clean resume behaviour after
  Manager restarts while an instance is still running. No effect on
  Last Oasis, which already creates a new log file per run.
- **Status icons on the MainForm tree.** Each node /
  installation / instance entry now carries a coloured shape
  badge encoding its current state. Colour is shared across
  all three tiers — Green = healthy, Yellow = update
  available or version mismatch, Red = unreachable or
  crashed, Blue = working (installing / starting / stopping),
  Gray = unknown / not installed / stopped, DarkRed =
  crash-loop halted — so colour reads independently of tier.
  Shape encodes the tier itself: nodes show a stacked-rack
  server icon, installations show a folder, instances show a
  circle. Refreshes every 2 seconds from cached manager
  state (no extra network polling) and immediately on tree
  rebuild (Add Node, Edit Installation, etc.) so badges are
  current without waiting for the next tick. Bitmaps drawn
  programmatically with GDI+; no external assets to ship.

### Notes

- SharpCompress 0.36.0 doesn't process Pax extended-header
  entries in BSD-tar-produced archives. Linux/macOS tar.xz
  extraction routes around it via native `tar`; Windows
  tar.xz still uses SharpCompress because no plugin
  currently produces a Windows direct-download case. If one
  materialises, modern Windows (10 1803+) ships bsdtar at
  `%SystemRoot%\System32\tar.exe` and the same `tar -xf`
  shell-out works (xz autodetected).
- The `ArchiveFactory.Open` path used for `.tar.gz` / `.7z`
  / `.rar` in the SharpCompress fallback doesn't apply
  unix file modes either. No current users; tracked as a
  follow-up if a future plugin needs executable content
  out of one of those formats.

- Pre-existing duplicate rows in `chat_messages` from prior
  Factorio restarts are not cleaned up automatically. Run
  `DELETE FROM chat_messages WHERE instance_id = '<id>';` against
  the node DB and the equivalent against the manager DB if a fresh
  slate is wanted; otherwise they're cosmetic.
- The very first instance restart after upgrading still replays
  history once because the cursor table starts empty. Subsequent
  restarts are clean.
- Synthetic leaves are persist-only — they don't fire `PlayerLeft`
  notifications. The corresponding `InstanceStopped` /
  `InstanceCrashed` notification already covers the situation, and
  per-player notifications on top of that would spam Discord badly
  when a populated server stops.

- Pre-existing duplicate rows in `chat_messages` from before the
  `ux_chat_dedup` UNIQUE INDEX shipped are not cleaned up
  automatically by the migration that adds the index. The index
  itself only de-dupes going-forward inserts; historical
  duplicates predate it and the index creation succeeds against
  the existing data (INDEX-only constraints don't validate prior
  rows). If a fresh slate is wanted, query for rows duplicated
  by `(instance_id, display_name, text)` and DELETE all but the
  earliest per group. Affects both the node DB and the manager
  DB — they hold separate copies. Optional — the duplicates are
  cosmetic and only visible in History queries that group by
  display name + text without timestamp.

- Orphaned `SessionIdentity` rows from before the manager-side
  `ResolveSessionIdentity` fallback shipped (sessions recorded
  with empty identity during the adoption window when the
  4-line tile-load sequence had rotated out of the node SSE
  ring buffer) can be retroactively rebased via a SQLite 3.33+
  UPDATE FROM joining `SessionHosts` on the `(InstanceId,
  HostedFromUtc..HostedUntilUtc)` range. Optional — affects only
  historical session-grouped queries ("show me all chat from
  the realm that hosted tile X yesterday"), not live operation.
  Going-forward rows pick up the correct identity through the
  fallback chain.

## [0.1.0] - 2026-05-02

First named version. Establishes the versioning baseline; everything
listed here was built incrementally before this point and is captured
in one section as a one-time backfill. Future releases will only list
deltas relative to the previous version.

### Added

#### Architecture

- Three-project solution: `GSM.Contracts` (shared interfaces and DTOs,
  no NuGet dependencies), `GSM.Node` (ASP.NET Core Minimal API service
  that runs on game-server machines), and `GSM.Manager` (WinForms
  desktop app). Plus `GSM.CtrlCSender` (Windows console-control helper)
  and `GSM.NodeSetup` (cross-platform installer).
- Manager-interprets / Node-executes split: plugins run only on the
  Manager and send plain data to Nodes. Plugin interfaces live in
  Contracts so Roslyn-compiled plugin source can reference them
  without depending on the Manager executable.
- Build versioning via `Directory.Build.props`. Protocol and contracts
  versions tracked separately in `NodeApiContract.vb`. See
  [VERSIONING.md](VERSIONING.md).

#### GSM.Node — game-server agent

- ASP.NET Core Minimal API host with bearer-token authentication, per-IP
  rate limiting, and per-IP auth-failure lockout middleware.
- `ProcessManager`: spawns game-server processes with redirected stdio,
  manages their lifecycle, drains output streams to prevent UE4 pipe
  blocking, and handles graceful shutdown.
- `RingBufferStore`: per-instance log ring buffer with subscription-based
  streaming via Server-Sent Events for the Manager log viewer.
- `EventStore`: applies declarative regex-based log parse rules from
  plugins; tracks per-instance player list and server state in memory;
  persists chat messages to SQLite. Manager can connect at any time and
  see current state without having been running during the events.
- `RconClientManager`: source RCON protocol client with reconnect logic.
- `InstallRunner`: SteamCMD integration with Steam Guard prompt flow,
  exit-code interpretation (5 = guard required, 7 = post-install
  self-update success), redistributable install pass (`vc_redist`,
  `dxsetup`), tar.xz/tar.gz/7z/rar extraction via SharpCompress.
- File-based log tailing alongside stdout capture, with open/read/close
  per poll cycle to coexist with the game's exclusive write handle.
- Crash detection and restart policy enforced node-side so restarts
  work even when the Manager is offline.
- Windows service deployment via `install-service.bat`/`uninstall-service.bat`.
- Console-control-event isolation: process-local handler that swallows
  CTRL_C_EVENT so the helper-fired CTRL_C reaches game children without
  also tearing down the node host.

#### GSM.Manager — desktop control plane

- WinForms application with EF Core SQLite persistence and migrations
  run via the Visual Studio Package Manager Console.
- `PluginRegistry`: hot-reload Roslyn compilation of plugins from
  `.vb` source files in the Plugins folder. Each file compiles as its
  own assembly so a single broken plugin doesn't block others. Orphan
  detection surfaces installations or instances whose plugin disappeared.
- `NodeHttpClient` + `NodeHttpClientFactory`: typed HTTP client per node
  with bearer-token auth, retry-on-transient policies, and Server-Sent
  Events log streaming.
- `CredentialService`: Steam credential storage encrypted with Windows
  DPAPI.
- `InstanceManager`: instance lifecycle (start/stop/restart), live state
  refresh poller, log-stream reconnect on Manager restart.
- `InstallationManager`: install/update orchestration including the
  Steam Guard prompt round trip back to the user.
- `NotificationService` + `NotificationEmitter`: pluggable notification
  pipeline. Includes built-in Discord webhook plugin with custom embed
  rendering, token substitution (`{RuleName}`, `{InstanceName}`,
  `{NodeName}`, `{Time}`, `{Date}`, etc.), and 1:1 destination targeting
  via `IDestinationTargetingPlugin`.
- `AutomationEngine`: declarative rules with five scopes, four trigger
  types, and eleven leaf actions; cron timers via NCrontab; condition
  evaluation with three condition types; reorderable sequence steps.
- `RestartCoordinator`: tile-loaded ready-signal handling for staggered
  multi-instance restarts.
- `VersionCheckService`: 60-minute polling per installation, raises
  `VersionMismatch` events for rules that subscribed.
- `ChatRetentionPruner`: idempotent background pruner for the chat
  history table.
- `RuleEditorForm`, `ConditionEditorForm`, `StepEditorForm`,
  `TemplateEditorForm`, `VisibilityProfileEditorForm`,
  `NotificationsForm`, `HistoryWindow`, `PluginStatusForm`,
  `SteamCredentialsForm`, `RealmCredentialsForm`, `SettingsForm`,
  `NewInstallationForm`, `EditInstallationForm`, `EditInstanceForm`,
  `AddInstanceForm`, `NodeSetupForm`, `LogViewerForm`,
  `AutomationRulesForm`.
- MainForm tree (Nodes → Installations → Instances) with humanised
  Automation Rules listview: live "Running... (12s)" / "Ran 2m ago" /
  "Skipped 5s ago" Last Run column, display-name substitution for raw
  GUIDs in execution history.
- File logging on both Manager and Node: daily rotation, 30-day
  retention, framework chatter clamped to Warning to keep volume sane.

#### Plugins (loaded at runtime via Roslyn)

- `LastOasisPlugin`: realm-aware Last Oasis dedicated server support
  with CustomerKey/ProviderKey held at the installation level and
  optional per-instance overrides for multi-realm hosts. Includes
  `SteamCmdInstallMonitor` for tile-binding readiness signals.
- `FactorioPlugin`: Factorio dedicated server with mod management,
  declarative log-parse rules for player join/leave and chat.

#### Tooling

- `GSM.CtrlCSender`: tiny Windows console helper used by the Node to
  deliver `CTRL_C_EVENT` to UE4 game-server children. Published
  self-contained-single-file in production so it works inside a Node
  publish folder that has no shared framework.
- `GSM.NodeSetup`: cross-platform installer with Windows-only WinForms
  GUI (gated behind `WINDOWS_GUI` compile constant) and Linux-friendly
  console fallback. Post-publish target deploys it next to the Node
  binary.
- About dialog (`Help → About`) showing build version, contracts
  version, and protocol version. Status-bar version indicator on the
  main window.

### Notes

This is an internal-baseline release. Sharing with external users is
not intended for 0.1.x; the immediate motivation for naming this
version is to establish the versioning, changelog, and release-process
groundwork before the first external user arrives.

<!--
  Comparison and tag links go here once phase 5f-4 stands up
  the GitHub Actions release workflow and the repo's public URL
  is settled. Form: [0.1.0]: <repo-url>/releases/tag/v0.1.0
-->
