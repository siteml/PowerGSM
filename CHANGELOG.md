# Changelog

All notable changes to PowerGSM are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to pre-1.0 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
as documented in [VERSIONING.md](VERSIONING.md): `MINOR` bumps may break
compatibility with the previous version, `PATCH` bumps do not.

## [Unreleased]

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
