# PowerGSM Reference — Build & Project Setup

Part of the PowerGSM reference set (index: [`../PowerGSM_Reference.md`](../PowerGSM_Reference.md)).
Covers solution structure, the incremental build order, the dependency
map, harmless build warnings, and the versioning / protocol / release
pipeline. For language-level pitfalls see [`vbnet-gotchas.md`](vbnet-gotchas.md);
for the plugin runtime model see [`plugins.md`](plugins.md).

> Note: the "PHASE 1–6" headings below are the original *build-order*
> steps for standing the solution up — not roadmap phases. See
> `../ROADMAP.md` / `../CHANGELOG.md` for roadmap phase numbering.

---

## PROJECT SETTINGS (apply to all three .vbproj files)

| Setting | Value |
|---|---|
| RootNamespace | (empty string — MUST be empty) |
| TargetFramework | net8.0 (Contracts + Node) / net8.0-windows (Manager) |
| Nullable | disable |
| ImplicitUsings | disable |
| No explicit Compile items | SDK auto-discovers all .vb files |
| JSON files | Use `<None Update="file.json">` not `<Content Include>` |

---

## PHASE 1 — GSM.Contracts (no dependencies, must compile first)

Goal: a library DLL that defines all shared types.
No NuGet packages required.
All four files compile together as one unit.

### File inventory

| # | File | Namespace | Key types defined |
|---|---|---|---|
| 1 | IGamePlugin.vb | GSM.Plugin | IGamePlugin, ILogParser, ILogSource, FileLogSource, StdoutLogSource, IModManager, InstallStep (+ subclasses), ConfigFieldDescriptor, ConfigFieldType (IntegerField/BooleanField — NOT Integer/Boolean), InstanceState, RconState, RconProtocol, CrashDetectionState, CrashRestartPolicy, RestartDecision, PlayerInfo, InstanceConfig, InstallationConfig, InstallMethod, LogParseRule, ParsedEvent, ParsedEventKind, **ReadySignalKind, ReadySignal, IReadySignalProvider, IPrerequisiteProvider** |
| 2 | IAutomationRule.vb | GSM.Automation | RuleScope (AllInstances — NOT Global), ITrigger, ICondition, IAction, IRuleContext, RuleContext (MustInherit), ConditionResult, ActionResult, NotificationSeverity, all trigger/condition/action classes, **WaitForReadySignalAction, CoordinatedRestartAction** |
| 3 | INotificationPlugin.vb | GSM.Notification | INotificationPlugin, IRemoteCommandHandler, NotificationContext, NotificationTokens, NotificationEventType, CommandPermission (ServerOperator — NOT Operator), InboundCommand |
| 4 | NodeApiContract.vb | GSM.Node.Api | All REST request/response DTOs, INodeClient interface, NodeErrorCodes, InstallationOperationState, PromptType, LogSourceType, PlayerSession, ServerStateResponse, ChatMessage, **PrerequisiteCheckResult, PrerequisiteCheckResponse** |

### Reserved keyword landmines already fixed
- `Integer` → `IntegerField` (ConfigFieldType enum)
- `Boolean` → `BooleanField` (ConfigFieldType enum)
- `Global` → `AllInstances` (RuleScope enum)
- `Operator` → `ServerOperator` (CommandPermission enum)
- `Public_` → `Everyone` (CommandPermission enum)
- `stop` → `stopResult` (variable in RestartInstanceAction)
- `step` → `stepAction` (variable in SequenceAction)

### VB.Net interface implementation rules (enforced here)
- Auto-property: `Public ReadOnly Property X As String = "y" Implements IFoo.X` ✓
- Computed property: `Public ReadOnly Property X As String Implements IFoo.X` + Get block ✓
- MustInherit class: each member needs `MustOverride` + `Implements` on same line ✓
- RuleContext is MustInherit — all IRuleContext members are MustOverride stubs here

---

## PHASE 2 — GSM.Node (depends on Phase 1 only)

Goal: a runnable ASP.NET Core service.
Uses Microsoft.NET.Sdk.Web — ASP.NET Core is included automatically.
No EF Core. Local SQLite via raw Microsoft.Data.Sqlite.

### NuGet packages
- Microsoft.Data.Sqlite 8.0.0
- Microsoft.Extensions.Hosting.WindowsServices 8.0.0
- Microsoft.Extensions.Hosting.Systemd 8.0.0
- Microsoft.Win32.Registry 5.0.0 (Windows-only registry probes for host prerequisite checks; required on cross-platform `net8.0` since the types aren't in the default reference set there — see Phase 5g prerequisite-check post-phase addition)
- SharpCompress 0.36.0 (cross-platform archive extraction — tar.xz, tar.gz, 7z, rar)

### File inventory

| # | File | Location | Key types / responsibility |
|---|---|---|---|
| 1 | NodeProgram.vb | GSM.Node\ | Program module (entry point), NodeConfiguration, NodeDatabase. Calls SetErrorMode() at startup to suppress blocking Windows error dialogs inherited by child processes |
| 2 | ProcessManager.vb | GSM.Node\ | ProcessManager, ManagedInstance, PolicyDecision. Spawns processes with redirected stdio, file tailers, SendCtrlCToProcess (taskkill-based graceful stop fallback) |
| 3 | RingBufferStore.vb | GSM.Node\ | RingBufferStore, InstanceBuffer, LineSubscription, BufferedLogLine |
| 4 | RconClient.vb | GSM.Node\ | RconClientManager, RconConnection, RconPacket |
| 5 | InstallRunner.vb | GSM.Node\ | InstallRunner, ActiveOperation. SteamCMD integration, RunCommonRedistAsync for VC++ redist install |
| 6 | EventStore.vb | GSM.Node\ | EventStore, CompiledRule, InstanceEventState. Applies declarative regex rules from plugins, tracks in-memory player/server state, persists chat to SQLite |
| 7 | InstanceEndpoints.vb | GSM.Node\Endpoints\ | InstanceEndpoints module — all /instances/* routes including /players, /server-state, /chat, /logs/recent |
| 8 | NodeEndpoints.vb | GSM.Node\Endpoints\ | InstallEndpoints module + SystemEndpoints module (with `/api/system/prerequisites` route — see Phase 5g prerequisite-check post-phase addition) |
| 9 | **PrerequisiteProbe.vb** | GSM.Node\ | **PrerequisiteProbe — host-side runtime-dependency catalog + detection (currently `vcredist-2015-2022-x64` via Win32 registry probe). See Phase 5g prerequisite-check post-phase addition.** |

### Additional files
- install-service.bat / uninstall-service.bat — Windows service installer scripts, next to GSM.Node.exe

### Project settings
- `<OutputType>WinExe</OutputType>` — node runs headless; avoids its own console window

### Config file
- nodesettings.json — sits next to GSM.Node.exe, copied via `<None Update>`
- Set AuthToken before first run (generate with: openssl rand -base64 36)

### Validation milestone
Run the node. Hit GET http://localhost:8765/api/version in a browser.
You should get JSON back. That confirms the whole stack is alive.

---

## PHASE 3 — GSM.Manager skeleton (depends on Phase 1)

Goal: a WinForms app that launches and shows the main window.
Add the database, DI wiring, and a minimal UI first.
No Core services yet — just enough to open the window.

### NuGet packages
- Microsoft.EntityFrameworkCore.Sqlite 8.0.0
- Microsoft.EntityFrameworkCore.Tools 8.0.0 (PrivateAssets=all)
- Microsoft.CodeAnalysis.VisualBasic 4.8.0
- NCrontab 3.3.6 (resolves to 3.4.0 — harmless warning)
- Microsoft.Extensions.DependencyInjection 8.0.0
- Microsoft.Extensions.Logging 8.0.0
- Microsoft.Extensions.Logging.Console 8.0.0
- Microsoft.Extensions.Configuration.Json 8.0.0
- Microsoft.Extensions.Configuration.EnvironmentVariables 8.0.0
- Microsoft.Win32.Registry 5.0.0

### Phase 3a — Data layer (compile this first within Phase 3)

| # | File | Location | Key types |
|---|---|---|---|
| 1 | GsmDbContext.vb | GSM.Manager\Data\ | GsmDbContext, all entity classes, all IEntityTypeConfiguration classes, GsmDataExtensions |

After adding GsmDbContext.vb, run the EF migration:
- Tools → NuGet Package Manager → Package Manager Console
- Set Default project to GSM.Manager
- `Add-Migration InitialCreate`
- `Update-Database`

If "No DbContext found" error: add GsmDbContextFactory to Data\GsmDbContext.vb:
```vb
Public Class GsmDbContextFactory
    Implements IDesignTimeDbContextFactory(Of GsmDbContext)
    Public Function CreateDbContext(args As String()) As GsmDbContext
        Dim options = New DbContextOptionsBuilder(Of GsmDbContext)().
            UseSqlite("Data Source=gsm.db").Options
        Return New GsmDbContext(options)
    End Function
End Class
```

### Phase 3b — Entry point and UI shell

| # | File | Location | Key types |
|---|---|---|---|
| 2 | ManagerProgram.vb | GSM.Manager\ | Program module, ManagerRingBufferStore, PluginOrphanDetector |
| 3 | MainForm.vb | GSM.Manager\UI\ | MainForm |
| 4 | UiPanels.vb | GSM.Manager\UI\ | WelcomePanel, NodePanel, InstancePanel, LogViewerForm, SchemaFormBuilder, **InstallationPanel** |

At this point the app should open and show the main window with an empty tree.

---

## PHASE 4 — GSM.Manager Core services

Add these one at a time. Each depends on Phase 3a (database).
Order matters: NodeHttpClient before InstanceManager, PluginRegistry before AutomationEngine.

| # | File | Location | Key types | Depends on |
|---|---|---|---|---|
| 1 | NodeHttpClient.vb | GSM.Manager\Core\ | NodeHttpClient, NodeHttpClientFactory, NodeApiException, NodeConnectionException | Phase 1 (INodeClient) |
| 2 | CredentialService.vb | GSM.Manager\Core\ | CredentialService (DPAPI encrypt/decrypt) | Phase 3a (GsmDbContext) |
| 3 | PluginRegistry.vb | GSM.Manager\Core\ | PluginRegistry, ILogParserCoordinator, IRingBufferStore, IOrphanDetector, PluginReloadSummary, PluginLoadStatus | Phase 1 (IGamePlugin) |
| 4 | InstanceManager.vb | GSM.Manager\Core\ | InstanceManager, ActiveLogParser | #1, #2, #3 |
| 5 | InstallationManager.vb | GSM.Manager\Core\ | InstallationManager | #1, #2, #3, #4 |
| 6 | NotificationService.vb | GSM.Manager\Core\ | NotificationService | #3, #4 |
| 7 | AutomationEngine.vb | GSM.Manager\Core\ | AutomationEngine, RuleContextImpl, CronTimer | #4, #5, #6 |
| 8 | **AutomationRuleSerializer.vb** | GSM.Manager\Core\ | AutomationRuleSerializer (polymorphic JSON round-trip for rules) | #7 |
| 9 | **RestartCoordinator.vb** | GSM.Manager\Core\ | RestartCoordinator, PendingSignal, RestartSlot | #3, #4 |
| 10 | HistoryQueryService.vb | GSM.Manager\Core\ | HistoryQueryService (timeline + snapshot queries, session label formatting) | #3, #4 |
| 11 | ChatRetentionPruner.vb | GSM.Manager\Core\ | ChatRetentionPruner (hourly age-based prune) | Phase 3a |

---

## PHASE 5 — GSM.Manager UI forms

Add these after Phase 4. All are optional for initial testing.
The app is functional without them — you can still connect to a node via code.

| # | File | Location | Key forms | Needed for |
|---|---|---|---|---|
| 1 | NodeSetupForm.vb | GSM.Manager\UI\ | NodeSetupForm | Adding nodes |
| 2 | NewInstallationForm.vb | GSM.Manager\UI\ | NewInstallationForm | Adding installations |
| 3 | RemainingForms.vb | GSM.Manager\UI\ | PluginStatusForm, SettingsForm, SteamCredentialsForm, RealmCredentialsForm, AutomationRulesForm, RuleEditorForm, AddInstanceForm, EditInstanceForm, EditInstallationForm | Supporting UI |
| 4 | NotificationsForm.vb | GSM.Manager\UI\ | NotificationsForm | Notification plugin config |
| 5 | TemplateEditorForm.vb | GSM.Manager\UI\ | TemplateEditorForm | Discord message templates |
| 6 | VisibilityProfileEditorForm.vb | GSM.Manager\UI\ | VisibilityProfileEditorForm | Per-plugin event visibility |
| 7 | HistoryWindow.vb | GSM.Manager\UI\ | HistoryWindow (non-modal, UTC/local toggle) | Session history browsing |
| 8 | **FormIconHelper.vb** | GSM.Manager\UI\ | FormIconHelper module (ApplyTo, GetLargeBitmap) | Branding consistency |

---

## PHASE 6 — Plugins (loaded at runtime by Roslyn, not compiled into the project)

These go in GSM.Manager\Plugins\ and are compiled at runtime.
They are NOT part of the VS project — the SDK must not auto-discover them.
Add a .gitignore or move them outside the project tree if the SDK picks them up.

| # | File | GameId | Key classes |
|---|---|---|---|
| 1 | LastOasisPlugin.vb | lastoasis | LastOasisPlugin (implements IGamePlugin + **IReadySignalProvider**), LastOasisInstanceConfig, LastOasisInstallConfig, SteamCmdInstallMonitor |
| 2 | FactorioPlugin.vb | factorio | FactorioPlugin, FactorioConfig, FactorioModManager, FactorioLogParser |

---

## DEPENDENCY MAP

```
GSM.Contracts (Phase 1)
    └── GSM.Node (Phase 2)
    └── GSM.Manager
            ├── Data\ (Phase 3a) → GsmDbContext
            ├── UI\   (Phase 3b) → MainForm, panels
            ├── Core\ (Phase 4)  → services
            └── UI\   (Phase 5)  → forms
```

No circular dependencies. Contracts has zero NuGet dependencies by design.

---

## KNOWN HARMLESS WARNINGS (ignore these)

| Warning | Reason | Action |
|---|---|---|
| NU1603 NCrontab 3.3.6 not found, 3.4.0 resolved | Patch version bump | None |
| NU1608 CodeAnalysis.Common version mismatch | EF Tools pulls older Roslyn | None |
| RAZORSDK1007 reference assembly not found | Cascades from Contracts failing | Goes away when Contracts compiles |
| BC40056 GSM.Plugin namespace not found | RootNamespace double-prefix bug | Fixed by setting RootNamespace to empty |

---


## PHASE 5f — Versioning, protocol negotiation, and release pipeline

Goal: turn PowerGSM into something with a real version story before
the first external user arrives. Three independent version axes,
automated GitHub releases, plugin contracts checked at load time.

### Three version axes

| Axis | Lives in | Bumps when | Purpose |
|---|---|---|---|
| **Build version** | `Directory.Build.props` `<Version>` | Every release (PATCH/MINOR per pre-1.0 SemVer) | Stamps assemblies, names artifacts, drives the UI's About dialog |
| **Protocol version** | `NodeApiContract.ProtocolVersion` | Manager-Node REST contract changes incompatibly | Drives compatibility indicator on Node panels (5 visual states) |
| **Contracts version** | `NodeApiContract.ContractsVersion` | Plugin-facing interfaces in `GSM.Contracts` change incompatibly | Plugins declare `' <RequiresContracts: N>` magic comment; PluginRegistry refuses to load mismatched plugins |

All three start at integer 1 (or `0.1.0` for build). Bumping policy
lives in `VERSIONING.md`; never bump on PATCH-only releases.

### Release pipeline

`.github/workflows/release.yml` triggers on `v*.*.*` tag push. Three
jobs: `build-windows` (Manager + Node + NodeSetup, win-x64),
`build-linux` (Node + NodeSetup, linux-x64), `release` (extracts the
matching `## [X.Y.Z]` section from CHANGELOG, creates GitHub Release
with three zips). Tags containing a hyphen auto-flag as pre-releases.

`.github/workflows/ci.yml` runs Release-config solution build on
every push to `master` and every PR — catches breakage before tag time.

`scripts/bump-version.ps1` is a props-only helper: validates the
version string, detects PATCH/MINOR/MAJOR, updates `<Version>` and
`<FileVersion>` always plus `<AssemblyVersion>` only on MINOR/MAJOR,
prints a numbered checklist of manual follow-ups (CHANGELOG, protocol
or contracts review, commit, tag, push). Doesn't touch CHANGELOG
content — that's real work.

### Files added in this phase

- `Directory.Build.props` — solution-root version stamp (Version,
  FileVersion, AssemblyVersion, plus shared assembly attributes)
- `CHANGELOG.md` — Keep-a-Changelog format, pre-1.0 SemVer
- `VERSIONING.md` — the three-axis policy, bumping rules, history
  tables for protocol and contracts integers
- `RELEASE_PROCESS.md` — cutting-a-release procedure, troubleshooting
  for every CI failure mode hit during 0.1.0-rc1…rc5
- `scripts/bump-version.ps1` — props-only version bumper
- `.github/workflows/release.yml` — tag-triggered release pipeline
- `.github/workflows/ci.yml` — push/PR-triggered build verification
- `GSM.Manager\UI\AboutForm.vb` — Help → About dialog showing
  build/protocol/contracts versions
- `GSM.Manager\Migrations\*_AddNodeProtocolVersion.*` — EF migration
  for the new `NodeEntity.LastSeenProtocolVersion` column

### Files modified in this phase

- `GSM.Contracts\NodeApiContract.vb` — added `Module` with
  `ProtocolVersion`/`ContractsVersion` constants; expanded
  `VersionResponse` DTO; new `INodeClient.GetApiVersionAsync` member
- `GSM.Contracts\IGamePlugin.vb` — `PluginLoadStatus.ContractsVersionTooNew`
  enum value
- `GSM.Node\Endpoints\NodeEndpoints.vb` — `/api/version` returns
  protocol + contracts integers
- `GSM.Node\NodeProgram.vb`, `GSM.Manager\ManagerProgram.vb` —
  startup log line shows version
- `GSM.Manager\UI\MainForm.vb` — Help menu, status bar version label
- `GSM.Manager\UI\UiPanels.vb` — NodePanel compatibility indicator
  (5 visual states: unknown / checking / compatible / older protocol /
  newer protocol)
- `GSM.Manager\UI\RemainingForms.vb` — PluginStatusForm Contracts
  column showing each plugin's declared contracts version
- `GSM.Manager\Core\PluginRegistry.vb` — parses
  `' <RequiresContracts: N>` magic comment from plugin source files
  before compilation; refuses load with `ContractsVersionTooNew` if
  declared version exceeds runtime
- `GSM.Manager\Core\NodeHttpClient.vb` — `GetApiVersionAsync` with
  in-memory cache (TTL 60s)
- `GSM.Manager\Data\GsmDbContext.vb` — `NodeEntity.LastSeenProtocolVersion`
- `GSM.PluginsSource\LastOasisPlugin.vb`, `FactorioPlugin.vb` — added
  `' <RequiresContracts: 1>` magic comment
- `GSM.CtrlCSender\GSM.CtrlCSender.vbproj` — explicit
  `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>` so cross-compile
  from Linux works (with `EnableWindowsTargeting=true`)
- `GSM.Node\GSM.Node.vbproj` — `EnableWindowsTargeting=true` added to
  the inner `<MSBuild>` Properties string in `PublishCtrlCSender`
  target (the inner task replaces global properties, doesn't inherit
  them — outer flag does NOT propagate)
- `VERSIONING.md` — Release process section linking to
  `RELEASE_PROCESS.md`

### CI / publish gotchas (hard-won across rc1–rc5)

| Pattern | Wrong | Right |
|---|---|---|
| **VB.Net pubxml location** | `dotnet publish -p:PublishProfile=winx64` finds nothing because the SDK searches `Properties\PublishProfiles\` only — NETSDK1198 warning, profile silently ignored, framework-dependent output | Use `-p:PublishProfileFullPath="path/to/My Project/PublishProfiles/winx64.pubxml"`. VS publish works fine because VS passes the full path explicitly |
| **Multi-TFM project publish** | `dotnet publish` on a `net8.0;net8.0-windows` project fails NETSDK1129 because pubxml's `<TargetFramework>` is read AFTER the cross-targeting check | Pass `-f net8.0` or `-f net8.0-windows` explicitly on the command line; pubxml setting is too late |
| **Multi-TFM project restore on Linux** | `dotnet restore` resolves all TFMs regardless of `--framework` (restore doesn't honour --framework) — NETSDK1100 on the Windows-slot resolution | Pass `-p:EnableWindowsTargeting=true` on the restore command |
| **Inner MSBuild task property propagation** | Outer `dotnet publish -p:EnableWindowsTargeting=true` does NOT reach the inner `<MSBuild Projects="..." Properties="..."/>` call — `Properties` *replaces* the global set, doesn't append | Add the property directly to the inner `Properties` string in the .vbproj |
| **SDK RID-aware default path beats pubxml `<PublishDir>` / `<PublishUrl>`** | Manager publishes to `bin\Release\net8.0-windows\win-x64\publish\` (RID before "publish") even though pubxml says `bin\Release\net8.0-windows\publish\win-x64\` | Don't rely on the pubxml path. Pass `-p:PublishDir=publish/explicit-path/` on the CLI and zip from that known location. Same fix for any downstream target like NodeSetup's `DeployToNodeFolder` (override `NodeDeploymentDir` to match) |
| **VS Sync command** | Pushes branches but **not** tags — `git push origin master` leaves new tags local-only, workflow never fires | Right-click the tag in VS Git Repository window → Push Tag. Or `git push origin vX.Y.Z` from cmd. Setting Tools → Options → Source Control → Git Global Settings → "Push --tags" makes Sync push tags too |
| **Linux runner SDK version** | `setup-dotnet@v4` with `8.0.x` pin still uses preinstalled `/usr/share/dotnet/sdk/10.0.201/` — .NET 10 has stricter Windows-targeting checks than .NET 8 | Either accept it (workflow handles the stricter checks) or pin the repo to .NET 8 via a `global.json` at solution root with `"sdk": { "version": "8.0.x", "rollForward": "latestFeature" }` |
| **Untagging** | Re-tagging the same version after a botched release — produces two different binaries claiming to be the same release | Bump PATCH and tag a new release. Never delete and re-tag. Pre-release tags (`-rc1`, `-rc2`) are how you iterate before a stable cut |
| **CHANGELOG section missing for tag** | Tag pushed, workflow runs, release job fails because awk extractor finds no `## [X.Y.Z]` matching the tag | Workflow has explicit `if [ -z "$notes" ]; then exit 1` guard so it fails loud rather than producing a release with empty notes. Fix: add the section, bump to next rc tag, push again |
| **Workflow branch trigger** | `branches: [main]` won't fire if the repo's default is `master` | Match the repo's actual default branch name. Tag-triggered workflows (`tags: ['v*.*.*']`) don't care about branch — they fire on tag push regardless |

