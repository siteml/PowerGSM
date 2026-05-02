# Changelog

All notable changes to PowerGSM are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to pre-1.0 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
as documented in [VERSIONING.md](VERSIONING.md): `MINOR` bumps may break
compatibility with the previous version, `PATCH` bumps do not.

## [Unreleased]

## [0.1.0-rc1] - 2026-05-02

Release pipeline dry run for 0.1.0. See `[0.1.0]` below for the
actual changeset captured in this release — this rc1 section exists
only to exercise the GitHub Actions release workflow end-to-end on
a throwaway pre-release tag before cutting the real `v0.1.0`.

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
