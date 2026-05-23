# PowerGSM — Project Reference & Incremental Build Guide

## How to use this document
Each phase produces a solution that compiles cleanly.
Work through phases in order. Within a phase, files can be added in any order.
To request a file: paste the file entry to Claude — it contains everything needed
to regenerate it correctly.

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

## QUICK REFERENCE — VB.Net gotchas in this codebase

| Pattern | Wrong | Right |
|---|---|---|
| Interface property with Get block | `Property X As String` | `Property X As String Implements IFoo.X` on the Property line |
| Abstract base class | `Class Foo : Implements IFoo` (no members) | `MustInherit Class Foo : MustOverride Function ...() Implements IFoo.X` |
| Enum member = reserved keyword | `Integer`, `Boolean`, `Global`, `Operator`, `Stop`, `Step` | Suffix with Field/Result/etc or rename |
| Variable = VB keyword | `handles`, `step`, `color` | `windowHandles`, `stepAction`, `lineColor` |
| Loop variable shadowing inherited property | `For Each tag In list` inside a Form (Control.Tag exists) | Rename loop variable; produces BC30039 "Loop control variable cannot be a property or a late-bound indexed array." |
| WinForms SelectedIndexChanged on already-matching value | Setting a combo's SelectedIndex to its current value does NOT fire SelectedIndexChanged | When using `SelectedIndexChanged` to drive setup logic during form load, call the handler explicitly after `SelectComboById` (or whatever sets the index). Idempotent and reliable regardless of whether the value actually changed. RuleEditorForm hit this when loading an Instance-scoped rule (default scope == loaded scope == position 0): target combo stayed empty until user toggled scope away and back. |
| **`Me.Invoke` from form constructor before window handle exists** | **`Task.Run(...)` fired from a Form's constructor whose async callee then calls `Me.Invoke(...)` to marshal back** | **WinForms doesn't create the window handle until first Show. Any `Me.Invoke` before that throws `InvalidOperationException` ("Invoke or BeginInvoke cannot be called on a control until the window handle has been created"); a surrounding `Try/Catch` silently eats it and the async work appears to run but no UI updates land. Defer the initial fire-and-forget to `Protected Overrides Sub OnShown(e)` — the handle is guaranteed to exist by then. NewInstallationForm hit this on its install-path-suggestion fetch: form opened with the path field blank, populated only after the user changed game or node (those events fire from a fully-loaded form where the handle does exist). The same shape applies to any async path-or-data-fetch you want firing at form open; constructor-time triggers are a trap.** |
| RootNamespace + explicit Namespace | Double-prefix: GSM.GSM.Plugin | Set RootNamespace to empty string |
| Await in Finally | Not supported in VB.Net | Use ExceptionDispatchInfo pattern OR make the Finally body synchronous |
| Await in Catch | Not supported in VB.Net | Catch-and-rethrow with flag variable |
| Async iterator (Yield in Async) | Not supported in VB.Net | Use callback/Action pattern |
| Async lambda return type | Cannot specify; Task(Of Object) inferred | Extract to named `Private Async Function` |
| Lambda returning interface from concrete | `Function() New ConcreteFoo()` infers Func(Of ConcreteFoo) | `Function() CType(New ConcreteFoo(), IFoo)` for single-expression; `Function() As IFoo ... End Function` for multi-line |
| Single-line Try/Catch | `Try:Catch:End Try` colon-separated | Must be multi-line |
| Null-conditional on LHS | `foo?.Bar = x` | `If foo IsNot Nothing Then foo.Bar = x` |
| Anonymous lambda in Using | Lifetime/disposal issues | Class-level `AddressOf` handlers |
| `proc.WaitForExitAsync` with redirected streams | Deadlocks | Poll `HasExited` instead |
| Extension methods (ILogger) | Not auto-resolved | Add `Imports Microsoft.Extensions.Logging` |
| **StreamReader closes the underlying FileStream** | **`Using fs As New FileStream(...) ... Using reader As New StreamReader(fs) ... End Using ... fs.Length` → ObjectDisposedException** | **StreamReader's End Using disposes the wrapped FileStream by default. Either compute everything you need from `fs` BEFORE the StreamReader's Using opens, or use the `StreamReader(stream, encoding, detectBom, bufferSize, leaveOpen)` overload with `leaveOpen:=True`. Caught when the tailer position cursor's post-read fingerprint compute hit a disposed stream.** |
| Interface implementer missing param-type import | Cascades to BC30401 "cannot implement" + BC30149 "must implement" | Add `Imports` for namespace where parameter types live, even if interface itself is imported. Implementer must resolve every type in the signature, not just the interface name. |
| EF migrations | Not supported in VB.Net | Run from Package Manager Console; use `Add-Migration`/`Update-Database` |
| Comment line inside initializer | Breaks implicit line continuation | Move comment above the initializer |
| Trailing comma before closing brace | Invalid in initializers | Remove trailing commas |
| NETSDK1022 duplicate Compile items | Explicit `<Compile Include>` + SDK auto-discovery | Remove all `<Compile Include>` blocks |
| Content file copy behaviour | `<Content Include="file.json">` | `<None Update="file.json"><CopyToOutputDirectory>PreserveNewest` |
| Regex named captures through string literals | Literal `(?<Name>` | If a tooling issue lowercases names, build via concat: `"(?<" & "Name" & ">..."` |
| Plugin Roslyn compilation excludes Microsoft.VisualBasic | `vbCrLf`, `vbLf`, `vbCr`, `AscW(c)`, `ChrW(n)` in plugin code (BC30451 "not declared") | Define `Private Shared ReadOnly` shims via `Convert.ToChar(...)` at class scope; e.g. `Private Shared ReadOnly _crlf As String = Convert.ToChar(13).ToString() & Convert.ToChar(10).ToString()`. Manager/Node/Contracts code is unaffected — only Roslyn-loaded plugins. |
| **JsonConverter(Of T) in VB** | **Read override takes `ByRef reader As Utf8JsonReader`** | **`Utf8JsonReader` is a ref struct; VB can't consume it. Use `JsonNode` tree traversal instead. BC30668 "Types with embedded references are not supported".** |
| **STJ polymorphism on interfaces** | **`[JsonPolymorphic]` attribute** | **Only works on base classes. Interfaces need hand-rolled polymorphism (see AutomationRuleSerializer).** |
| **EF `Update-Database X` semantics** | **"Undo migration X"** | **"Bring DB to the state after X completed." To undo one migration, name the PREVIOUS one. To undo all, `Update-Database 0`.** |
| **EF migration re-apply after file edit** | **Edit .vb, rebuild, run, expect re-run** | **EF skips migrations already in `__EFMigrationsHistory`. Must rollback THEN reapply, or apply corrective SQL directly.** |
| **EF Core SQLite drops DateTimeKind on read-back** | **Store `DateTime.UtcNow` (Kind=Utc) via EF, read it back, get Kind=Unspecified** | **EF Core's SQLite provider stores DateTime as TEXT in `yyyy-MM-dd HH:mm:ss.fffffff` format — no offset, no Z suffix, so the kind is unrecoverable on read. Downstream `ToString("o")` then emits a no-Z string and any consumer calling `ToUniversalTime()` on the parsed value treats it as Local and shifts by the host's UTC offset. Was a silent filter on chat-mirror cursors after manager restart — every restart-then-chat sequence dropped messages. Fix: tag with `DateTime.SpecifyKind(value, DateTimeKind.Utc)` immediately after EF returns the value, or add a `ValueConverter` on the entity property if it shows up in many places. The column-name suffix (`TimestampUtc` vs `Timestamp`) tells you the contract; restore the metadata to match.** |
| **Roslyn references in self-contained single-file publish** | **Walk `TRUSTED_PLATFORM_ASSEMBLIES` + `MetadataReference.CreateFromFile(path)` — in .NET 6+ single-file mode TPA paths are virtual paths inside the bundle; every `CreateFromFile` throws `FileNotFoundException` and is silently swallowed. Refs end up empty → cascading BC30002 "System.X is not defined" + BC30652 "<Missing Core Assembly>" on every line of every Roslyn-compiled plugin. Only manifests in published builds, not in dev.** | **`Basic.Reference.Assemblies` NuGet (meta-package, NOT the TFM-specific `Basic.Reference.Assemblies.Net80` — only the meta-package exposes the documented `ReferenceAssemblies.Net80` API). For project references you also compile against at runtime (e.g. GSM.Contracts), mark the `<ProjectReference>` with `<ExcludeFromSingleFile>true</ExcludeFromSingleFile>` so they publish as loose DLLs next to the .exe and `Assembly.Location` returns a real path.** |

---

## POST-PHASE ADDITIONS

Changes made after the initial 6-phase build. Listed here so the
reference document covers the current state, not just the initial walkthrough.

### Installation config persistence & edit UI

- `InstallationEntity` has a `ConfigJson` field that persists install-level
  config values (e.g. Last Oasis CustomerKey/ProviderKey — stored once,
  shared by all instances of that installation).
- `InstallationEntity.SteamCredentialId` associates a Steam credential
  with an installation so updates reuse it automatically.
- `EditInstallationForm` in RemainingForms.vb edits the display name and
  all install-level config fields via SchemaFormBuilder on
  `plugin.GetInstallConfigSchema()`.
- MainForm "Edit Installation..." context menu opens this form.

### Instance config merge

In `InstanceManager.StartInstanceAsync`, installation ConfigJson is
merged into instance CustomFields before the plugin is invoked:

1. Load `installEntity.ConfigJson` into a case-insensitive dict
2. Overlay `instanceEntity.ConfigJson` on top (instance overrides
   installation)
3. Pass merged dict as `InstanceConfig.CustomFields` to the plugin

This is how Last Oasis's CustomerKey/ProviderKey (stored on the
installation) reach `BuildLaunchArguments`. Instance-level overrides
for those same keys work too — useful when one installation hosts
multiple realms.

### Per-file plugin compilation

`PluginRegistry.ReloadAll` compiles each `.vb` file in the Plugins
directory as its own `VisualBasicCompilation` with a unique assembly
name (`GSM.Plugins.<filename>`). All plugin assemblies still share one
`AssemblyLoadContext` so unload/reload cycles work atomically. A single
plugin file failing to compile does NOT prevent others from loading —
failures are recorded per-file in the `PluginReloadSummary`.

### Log file tailing on the node

`StartInstanceRequest.LogFilePaths` lists absolute paths the node
should tail and merge into the instance log buffer. Plugins declare
file log sources via `GetLogSources`; Manager substitutes
`{InstallPath}` / `{InstanceId}` tokens in `FileLogSource.PathPattern`
and sends resolved paths on start. Tailer in `ProcessManager`:

- Open-read-close pattern per 500ms poll cycle (no persistent FileStream)
- `FileShare.ReadWrite Or FileShare.Delete` so the game's exclusive
  write doesn't conflict
- Resumes from a persisted byte cursor when the file's first-bytes
  fingerprint matches the last saved one (see "Tailer position
  cursor" below); otherwise falls back to a size-based heuristic
  on first open: read from 0 if file is small (<2MB), backfill
  last 512KB if large
- 5-second startup delay after file exists
- Handles truncation (position reset to 0 if file shrinks)

When `LogFilePaths` is populated, stdout capture is skipped for the log
buffer (file is the authoritative source) but stdout pipes are STILL
drained via `BeginOutputReadLine`/`BeginErrorReadLine` — unread redirected
pipes will block UE4 writes after ~4KB and hang the server. The handlers
just discard data when `captureStdout = False`.

### Declarative log parsing (EventStore)

Plugins implement `GetLogParseRules()` returning a list of
`LogParseRule` objects with regex patterns (using named capture groups)
and a `ParsedEventKind` classifier. The rules are sent to the node in
`StartInstanceRequest.LogParseRules`.

Node's `EventStore`:
- Compiles rules once per `RegisterInstance` call (on instance start)
- Applies them to every log line flowing through stdout capture or
  file tailer
- Updates in-memory per-instance player list and server state (thread-safe)
- Persists chat messages to SQLite (`chat_messages` table)
- Exposes data via `/api/instances/{id}/players`, `/server-state`, `/chat`

Node tracks all of this independently — a Manager can connect at any
time and see current state without having been running during the
events.

Supported capture group names (match = populates the named field on
PlayerSession/ServerStateResponse/etc.):
`Name`, `Platform`, `PlatformUserId`, `CharacterId`, `RemoteAddress`,
`Message`, `MatchState`, `TileId`, `TileName`, `MapPath`, `Registered`.

`ParsedEventKind` values: `PlayerJoin`, `PlayerLeave`, `PlayerIdentity`,
`ChatMessage`, `ServerStateChange`, `TileLoaded`, `Custom`.

### Live state refresh in the UI

- `InstancePanel` has a 3-second `_refreshTimer` polling
  `InstanceManager.RefreshInstanceStateAsync(instanceId)` to keep the
  status label live (Running/Starting/Stopped/Crashed with PID or exit
  code). Maps all 8 `InstanceState` values to colored labels.
- `LogViewerForm` has a 500ms refresh timer polling the manager ring
  buffer with timestamp-based cursor (`_lastSeenTimestamp`). Batched
  append via `WM_SETREDRAW`-suspended RichTextBox to avoid per-line
  scroll thrash under high throughput. Trims to 4000 lines when buffer
  exceeds 5000.
- Log viewer reloads history from the node on open via
  `GET /api/instances/{id}/logs/recent` so post-Manager-restart views
  aren't empty.
- `InstanceManager.EnsureLogStreamAsync(instanceId)` reconnects a
  stream if one isn't active — called when the log viewer opens and
  from `ReconnectLogStreamsAsync` at Manager startup.

### SteamCMD integration

Root-cause findings documented for future reference:

- **Trailing backslash in install path** corrupts SteamCMD arg parsing:
  `"C:\GameServers\"` becomes `\"` interpreted as escaped quote,
  swallowing `+login`, `+app_update`, `+quit`. Always strip trailing
  backslashes before quoting.
- **Self-update pre-pass** with NO stream redirection first (loop until
  exit code 0), then redirected install pass. Using redirected streams
  for self-update poisons subsequent launches.
- **Do not use `WaitForExitAsync`** with redirected stdio — deadlocks
  waiting for streams to close. Poll `HasExited` in a `While` loop.
- **Steam Guard flow (exit code 5)**: detect, set operation state to
  `WaitingForInput`, await TCS; Manager polls, prompts user, POSTs code
  via `/api/install/{id}/prompt`; retry loop relaunches SteamCMD with
  `+set_steam_guard_code CODE` prepended.
- **Exit code 7 after success** = SteamCMD self-updated post-install.
  Treat as success if manifest file exists.
- **Send empty newline to stdin every 20s** unconditionally — keeps
  SteamCMD from hanging in interactive mode on some commands.

### Common redist installation

`InstallRunner.RunCommonRedistAsync` runs after successful SteamCMD
install on Windows. Enumerates `_CommonRedist\**\*.exe` and runs each
with silent flags:
- `dxsetup.exe` → `/silent`
- `vcredist*` / `vc_redist*` → `/install /quiet /norestart`
- Everything else → `/quiet /norestart`

Success codes: 0, 1638, 3010. 5100 = system requirements not met (skip).
5-minute timeout per installer. Only runs on Windows. Node process must
run with sufficient privileges (administrator typically required).

### Archive extraction

Two-tier extraction strategy in `InstallRunner.
ExecuteDownloadStepAsync`:

1. **Native `tar` (primary, Linux/macOS only)** — for `.tar.xz`
   / `.txz` archives, the extractor shells out to the platform's
   native `tar` binary via `ProcessStartInfo.ArgumentList` (each
   arg as a separate argv entry, so spaces or special chars in
   paths don't need quoting). tar handles every variant of
   long-name extension correctly: POSIX Pax, GNU `LongName` /
   `LongLink`, BSD-tar's variant. Preserves unix file modes.
   `--strip-components=1` collapses a single-top-level wrapper
   directory in one flag, replacing the manual staging-and-hoist
   pass. `HasExited` polling rather than `WaitForExitAsync`
   because the latter deadlocks on redirected streams in .NET 8
   (same pattern as `RunSteamCmdProcessAsync`).

2. **`SharpCompress 0.36.0` (fallback)** — used for `.zip` (via
   the built-in `System.IO.Compression.ZipFile`), Windows
   `.tar.xz`, and `.tar.gz` / `.tgz` / `.7z` / `.rar` (via
   `ArchiveFactory.Open`). Windows `.tar.xz` is in scope only
   because no current plugin produces a Windows direct-download
   case; if one materialises, modern Windows (10 1803+) ships
   bsdtar at `%SystemRoot%\System32\tar.exe` and the same
   shell-out pattern drops in. Fallback path includes:

   - **Pax-header filtering.** SharpCompress treats BSD-tar's
     `@PaxHeader` entries as regular files; `IsPaxHeaderEntryKey`
     filters entries whose path segments match `PaxHeader`,
     `@PaxHeader`, `PaxHeaders*`, or `@PaxHeader*` so they don't
     leak as junk files. Doesn't help long-name resolution —
     SharpCompress doesn't process the long-name records for
     this variant either, which is why the native-tar branch
     exists.
   - **Strip-top-level via staging+hoist.** Extract to
     `<install>/.gsm-staging/`, detect a single top-level
     directory in staging, hoist its contents up via
     same-volume `Directory.Move` / `File.Move` (O(1) metadata
     ops, near-free). `MergeDirectoryRecursive` handles the
     update flow where target directories already exist.
   - **Unix mode preservation via `ApplyUnixModeIfNeeded`.**
     `WriteEntryToDirectory` doesn't apply tar entry modes;
     the runner snapshots `entry.Mode` before extraction and
     calls `File.SetUnixFileMode(destPath, mode And &HFFF)` on
     Linux/macOS afterward (lower 12 bits to round-trip
     suid/sgid/sticky if set). No-op on Windows where
     `UnixFileMode` would throw `PlatformNotSupportedException`.
     Same shim hasn't been wired into the
     `ArchiveFactory.Open` (tar.gz / 7z / rar) path because no
     plugin currently produces those — tracked as a follow-up.

Download validation rejects files under 1KB as likely error
pages. HttpClient has `AllowAutoRedirect=True` and a
`User-Agent: PowerGSM/1.0` header (factorio.com rejects requests
without a UA).

### Top-level wrapper directory handling (`StripTopLevelDirectory`)

Many release tarballs wrap every entry under a single top-level
directory (`factorio_2.0.76.tar.xz` → all entries under
`factorio/`). Without stripping, the install lands a level too
deep — plugin-relative paths like `bin/x64/factorio` resolve
against `<install>/factorio/...` instead of `<install>/...`,
and version-detection reads of `data/base/info.json` miss the
file entirely.

Plugins opt in via `DownloadFileStep.StripTopLevelDirectory =
True`. The native-tar branch implements it with
`--strip-components=1`. The SharpCompress fallback extracts to
a staging directory under `<install>/.gsm-staging/`, then
`HoistStagedContents` checks for a single top-level subdirectory
and promotes its contents up to the install root via same-
volume `Directory.Move` / `File.Move`. Multi-top-level archives
(the flag was set but the archive doesn't actually have a
single wrapper dir) extract everything to the install root
as-is — the flag is a request, not a guarantee.

Update flow lands in `MergeDirectoryRecursive`, which overlays
the new files onto the existing install directory tree. Files
at conflicting paths are overwritten; subdirectories are
recursively merged. Same-volume `File.Move(... overwrite:=True)`
is O(1) metadata, so even a multi-GB update finishes the hoist
step in well under a second.

### Plugin-reported installed version (`IVersionAwarePlugin.GetInstalledVersionAsync`)

For non-SteamCmd installs, the manager-side `BuildVersionStamp`
used to produce "installed (timestamp)" / "download (timestamp)"
placeholders that could never match the canonical version
string `GetLatestVersionAsync` returned ("2.0.76"). The
`VersionCheckService`'s inequality check then reported drift on
every poll, putting a permanent "update available" badge on
fresh installs.

`IVersionAwarePlugin.GetInstalledVersionAsync(config, client,
cancellation)` is the fix — the plugin reads its installed
version off the node's filesystem in the same format
`GetLatestVersionAsync` returns. The plugin uses the supplied
`INodeClient` to call the existing file-ops endpoints (no
direct filesystem access required); `allowedRoots` /
`allowedExtensions` scope the read to just the version-bearing
file. Returns `Nothing` on any failure (file missing, parse
failure, network blip) so the caller falls back to the
synthetic stamp rather than recording a meaningless value.

Called by `InstallationManager` post-install/update on
non-SteamCmd installs (Steam installs continue to use the
appmanifest ACF buildid path), and opportunistically by
`VersionCheckService` on every poll cycle so pre-existing rows
with placeholder stamps upgrade themselves without requiring a
reinstall.

Factorio implements the method by reading `data/base/info.json`
(the manifest of Factorio's bundled `base` mod, which the engine
updates to match its own version on every patch). Last Oasis
doesn't implement `IVersionAwarePlugin` at all — the contract
change is non-breaking for plugins that don't opt in.

### Direct-download update flow

`FactorioPlugin.GetUpdateSteps` previously had a `SteamCmd`
branch only; `DirectDownload` fell through to an empty step
list. The runner executed zero steps and recorded "completed
successfully" with no Download/Extract/Configure entries
between the bookends — a silent no-op that left the install
frozen at whatever version it was at on first install.

The direct-download update branch now emits the same
`DownloadFileStep` (with `StripTopLevelDirectory = True`) the
install path uses; the URL auto-redirects to whatever's
current on the chosen channel, so the same URL produces the
new version on update. Saves and mods live under `saves/` and
`mods/` which aren't in the tarball, so they survive the
overlay.

The `BuildConfigPathStep` now runs on update too. SteamCmd's
`app_update` rewrites `config-path.cfg` to its upstream
defaults on every update; the headless tarball ships its own
`config-path.cfg` and tar extraction overwrites our copy. Both
paths need the rewrite; manual installs are a pure no-op (we
never wrote the cfg in the first place).

### Install-method-aware UI

Three small UI changes around install-method visibility:

- `NewInstallationForm` hides the Steam-credential dropdown
  (label + combo) when the chosen install method isn't
  `SteamCmd`. Captured via `_steamCredLabel` field plus a
  `_methodComboBox.SelectedIndexChanged` handler that toggles
  `Visible` on both controls. Force-resets the combo to index
  0 (Anonymous) when the method changes away from Steam, so
  switching back doesn't leave a stale credential selection.
- `EditInstallationForm` (`RemainingForms.vb`) does the same
  hide-on-non-Steam pass on form load — one-shot rather than
  reactive because the install method isn't editable
  post-creation. Promoted the previously-local `credLbl` to
  a `Private _credLabel As Label` field so the visibility
  toggle has access to it.
- `InstallationPanel` header (`UiPanels.vb`) renders an
  `Install method:` line between the path and version. The
  Steam-account credential label is only shown for SteamCmd
  installs. Header height grew from 150 to 170px to fit the
  new line; subsequent labels shifted +20px in y to match.

### Engine output dialog for file-generation failures

`FileGenerationPanel.ApplyFailureState` previously rendered the
failure summary into the single-line status label. When the
bare error message exceeded 80 characters (the typical case —
"Process exited with code 1 (expected 0): /opt/PowerGSM/..."
is well over that), the captured engine output (which the node
did populate via `GenerateMapResponse.Output`) was dropped
from the display, leaving the user with no diagnostic context.

The panel now opens a resizable dialog whenever a generation
failure has non-empty captured output. Layout matches
`NewInstallationForm.ShowInstallErrorDialog` for visual
consistency: warning icon + bold headline + `Engine output:`
label + multiline read-only `TextBox` scrolled to the end
(engine errors land at the bottom of the output, after a
banner of init lines that aren't actionable) + OK button.
Minimum size 480×280, default 720×480, fully resizable.

Reused for any future plugin-driven file-generation operation
that fails with engine output — the panel itself is generic
(`IFileGenerationProvider`-driven) and not Factorio-specific.

### Crash restart policy pushed to Node

`CrashPolicy`, `MaxCrashCount`, `CrashWindowMinutes` are fields on
`StartInstanceRequest` — the node enforces them autonomously so
restarts work even if the Manager is offline.

### Graceful instance shutdown

Current approach: `taskkill /PID <pid>` (no `/F`) via
`SendCtrlCToProcess`. Sends WM_CLOSE, works for most console apps.

**KNOWN LIMITATION**: UE4 dedicated servers (including MistServer) do
not respond to WM_CLOSE. No clean graceful shutdown available in .NET
8 for UE4 servers because:
- `ProcessStartInfo.CreateNewProcessGroup` was proposed in 2020 but
  never shipped in any .NET version
- Reflection on `_standardCreationFlags` fails because the field
  doesn't exist in .NET 8 (available private fields: _fileName,
  _arguments, _directory, _userName, _verb, _argumentList, _windowStyle,
  _environmentVariables, and k__BackingField variants)
- Native CreateProcess + CREATE_NEW_PROCESS_GROUP + manual pipes +
  reflection to attach streams was tried and broke stream redirection
- SendKeys to child window (WindowsGSM's approach) requires a visible
  window; removing `-log` prevents UE4 from creating one
- PostMessage WM_KEYDOWN Ctrl+C to all child windows: UE4 ignores

Force-kill via `Process.Kill(entireProcessTree:=True)` after 25s
timeout is the fallback. Parked pending Process Monitor investigation
of WindowsGSM's CreateProcess flags.

### Reserved keyword landmines (updated)
- `Integer` → `IntegerField` (ConfigFieldType enum)
- `Boolean` → `BooleanField` (ConfigFieldType enum)
- `Global` → `AllInstances` (RuleScope enum)
- `Operator` → `ServerOperator` (CommandPermission enum)
- `Public_` → `Everyone` (CommandPermission enum)
- `Stop` → `stopResult` (variable in RestartInstanceAction)
- `Step` → `stepAction` (variable in SequenceAction)
- `Handles` → `windowHandles` (variable in EnumWindows callbacks)
- `Color` → `lineColor` (variable in LogViewerForm)
- `Tag` → `setTag` (loop variable in RuleEditorForm) — inside Form-derived classes, `tag` resolves to the inherited Control.Tag property; produces BC30039 on `For Each tag In ...`

### Windows service deployment

`install-service.bat` (in GSM.Node output directory):
```
sc create GSMNode binPath= "<path>\GSM.Node.exe" start= auto
sc description GSMNode "PowerGSM Node Service"
sc start GSMNode
```

`uninstall-service.bat`:
```
sc stop GSMNode
sc delete GSMNode
```

Deploy via `dotnet publish -c Release --self-contained -r win-x64`
to get a standalone binary distribution.

### Tailer position cursor

The tailer in `ProcessManager.TailFileAsync` persists a per-(instance,
log file) cursor to the node's SQLite DB so log history isn't replayed
on instance restart. Without this, games that append to the same log
file across runs (Factorio's `factorio-current.log` and the
`--console-log` file) caused EventStore to re-process all prior chat /
join / leave events every time the instance started, producing
duplicate rows in `chat_messages` (visible in the Chat tab as the
same message at three different timestamps after three restarts).
Games that archive the old log and create a new one per run (LO via
UE4's timestamped log-archive convention) didn't hit the bug because
the tailer saw a fresh file — but they also didn't get clean
Manager-restart-while-instance-running behaviour, which the cursor
fixes too.

**Storage:** new `TailerPositions` table on the node DB, composite
PK on `(InstanceId, LogPath)`. Columns: `BytePosition` (Int64),
`Fingerprint` (TEXT), `UpdatedAtUtc` (TEXT). One row per file an
instance is tailing; Factorio gets two rows because it tails both
`factorio-current.log` and `factorio-console.log`. Created via
`CREATE TABLE IF NOT EXISTS` in `NodeDatabase.EnsureCreated` — no
migration mechanism on the node side, just additive schema on next
node start.

**Fingerprint:** SHA-256 hex of the file's first 256 bytes, computed
lazily on first open via `TryComputeFingerprint(fs)`. Returns Nothing
if the file is shorter than 256 bytes — files that small don't
justify the resume overhead, and locking in a hash of a partial file
would change as the file grew, defeating the comparison. When
fingerprint is Nothing the cursor isn't persisted on that iteration
and the next iteration retries.

**Resume logic on first open:**
- Compute current fingerprint (Nothing for sub-256-byte files)
- Read saved cursor for `(instanceId, logPath)`
- Resume only when (saved row exists) AND (current fingerprint
  computed) AND (fingerprints match) AND (fs.Length >= savedPosition).
  All four conditions required.
- Otherwise fall back to existing size-based heuristic

**File-replacement detection** (LO archive case):
- New file at same path → first 256 bytes differ → fingerprint
  mismatch → no resume → existing heuristic kicks in (read from 0
  for small files, backfill last 512KB for large ones)

**Truncation handling:** if `fs.Length < position` mid-run, both
`position` and `currentFingerprint` are reset. Next save iteration
recomputes fingerprint against the new (shorter) content.

**Persistence ordering — the StreamReader gotcha** (added to the VB
gotchas table): `Using reader As New StreamReader(fs)` disposes `fs`
on `End Using` because StreamReader closes the underlying stream by
default. Calling `TryComputeFingerprint(fs)` after the inner Using
block throws `ObjectDisposedException`. Fix: compute the fingerprint
BEFORE the StreamReader takes ownership, while `fs` is still alive,
then save afterward using the cached fingerprint without touching
`fs`. Caught during initial deployment — surfaced as a warn-level
"Tailer error" log + `System.IO.FileStream.get_Length()` stack frame.

**Persistence cadence:** save runs after every successful read
iteration (every ~500ms when content is flowing). SQLite writes are
sub-millisecond; the density determines how much progress is lost on
an unexpected node crash mid-tail (worst case: ~500ms of unread tail
that gets re-read on next start, harmless because the cursor catches
up immediately on the following save).

**First-deploy behaviour:** the very first instance restart after
deploying this still replays history once, because the table is
empty and there's no saved row to compare against. After that first
run completes, the cursor is in the DB; subsequent restarts are
clean.

**Pre-existing duplicate rows are not auto-cleaned.** Whatever
duplicates landed in `chat_messages` before the fix stay where they
are. Manual cleanup if desired:
```sql
DELETE FROM chat_messages WHERE instance_id = '<instance>';
```
against `<node-data-dir>/node.db` (and the equivalent EF query against
the manager DB's `ChatMessages` table). Going forward, no new
duplicates are created.

**Verification log line:** when resume succeeds, the tailer logs
`Resuming tailer for {Id} at byte {Pos} ({Path})` at Information
level. Absence of this line on the second restart after deploy
means either (a) the file replaced (fingerprint mismatch, expected
for LO) or (b) the saved position was past `fs.Length` (truncation).
No log spam in the steady-state save path — only the resume
decision is logged.

**Files modified:**
- `GSM.Node\NodeProgram.vb` — `TailerPositions` table in
  `EnsureCreated`, `GetTailerPosition` / `SaveTailerPosition` methods
  on `NodeDatabase`, new `TailerPositionRow` class
- `GSM.Node\ProcessManager.vb` — `TailFileAsync` consults saved
  cursor on first open, persists after every successful read,
  truncation path clears cached fingerprint; new
  `TryComputeFingerprint` static helper

### History timeline integrity

Two pre-1.0 bugs fixed in the same arc, both manifesting as
**missing rows** in the History window even though the underlying
event reached the node correctly. Documented together because
both are about reconciling node-authoritative state into the
manager's EF mirror, both surface only after specific timing
conditions, and both bit during the same realistic test session.

#### Bug 1 — Synthetic player-leave on instance stop or crash

When an instance stopped or crashed with a player still online,
the History timeline showed the player's Join with no matching
Leave. Symptom: timeline ended on a join, the player
"disappeared" from the user's mental model with no closure.

Root cause: the manager's per-instance `_activePlayers` HashSet
(populated by `HandlePlayerJoin` / `HandlePlayerLeave` from the
log stream) was emptied on stop via `ClearPlayerTracking` —
but the old implementation just dropped the bucket without
emitting any leave events for the still-tracked names. The node
side doesn't help: when the process exits, no leave log line
ever gets written, so there's nothing for the parser to see.
The player's join was persisted to `PlayerActivity`, but the
matching leave only existed in the manager's in-memory bucket
and vanished with it.

Fix in `GSM.Manager\Core\InstanceManager.vb`:

- **`ClearPlayerTracking` rewritten to flush.** Drains the
  bucket atomically (TryRemove + SyncLock + ToList + Clear),
  then for each name calls `PersistPlayerObservation(instanceId,
  name, isJoin:=False)` wrapped in try/catch so one DB error
  doesn't lose the rest. Logs `"Flushed {Count} player(s) as
  synthetic leave on stop for {Id}"` at Information level.
  **Persist-only** — does NOT fire `PlayerLeft` notifications.
  The `InstanceStopped` / `InstanceCrashed` notification
  already covers the scenario, and per-player notifications on
  top of that would spam Discord when a populated server stops.
- **Order swap in `StopInstanceAsync.Finally`:**
  `ClearPlayerTracking` BEFORE `StopLogStream`, not after. The
  flush calls `ResolveSessionIdentity`, which reads the
  parser's `CurrentSessionIdentity` from `_logParsers`.
  `StopLogStream` removes that parser entry, after which the
  resolver falls back to `{gameId}:{instanceId}`. For Last Oasis
  that fallback differs from the real `lastoasis:realmId:tileId`
  session identity the joins were stamped with, so flushing
  AFTER `StopLogStream` would orphan the synthetic leaves
  under a different SessionIdentity than the matching joins.
  Factorio's fallback happens to match its real format, so the
  Factorio path was correct either way — but cheap to get
  right for both regardless.
- **Terminal-state detector in `RefreshInstanceStateAsync`.**
  Catches the crash and crash-loop paths where
  `StopInstanceAsync.Finally` (which also calls
  `ClearPlayerTracking`) wasn't the path that took the instance
  down. Compares `previous.CurrentState` to `result.CurrentState`;
  if `newState` is terminal (Stopped / Crashed / CrashLoopHalted)
  AND `prevState` was not, fires the flush. **Idempotent** —
  a user-initiated stop flushes via the Finally first, and this
  callsite then sees an empty bucket and no-ops. Wrapped in
  try/catch so a flush exception can't cascade into the
  notification-emitting branch above it. Doesn't depend on
  `_emitter` being non-null — the flush is about persistence,
  not notifications.

#### Bug 2 — Chat mirror DateTimeKind round-trip

After a manager restart, chat messages persisted to the node
failed to mirror into the manager's `ChatMessages` table.
Symptom: Chat tab on the manager showed the message (queries
the node directly), but the History window's timeline didn't
(queries the manager mirror). User-visible diagnostic: open
`gsm.db` in DB Browser, run `SELECT * FROM ChatMessages WHERE
InstanceId = '<id>'` — the missing row is genuinely absent.

Root-cause chain:

1. JSON deserialisation of the node's `/api/instances/{id}/chat`
   response: `ChatMessage.TimestampUtc` arrives with `Kind=Utc`
   (System.Text.Json parsing the node's `Z`-suffixed timestamp).
2. Manager stores `ChatMessageEntity.TimestampUtc` via EF Core.
   **EF Core's SQLite provider stores `DateTime` as TEXT in
   `yyyy-MM-dd HH:mm:ss.fffffff` format — no offset, no Z
   suffix — and reads it back with `Kind=Unspecified`.** The
   kind information is unrecoverable from the storage format.
3. After a manager restart, in-memory `_chatCursors` is empty.
   `MirrorChatForInstanceAsync` calls `SeedChatCursor`, which
   returns `db.ChatMessages.Max(c.TimestampUtc)` — Kind=Unspecified.
4. `NodeHttpClient.GetChatHistoryAsync` serializes the cursor
   via `sinceUtc.Value.ToString("o")`. For Kind=Utc that
   produces `2026-05-03T00:03:57.0000000Z`. For Kind=Unspecified
   it produces `2026-05-03T00:03:57.0000000` — no Z.
5. The node endpoint parses with `DateTimeStyles.RoundtripKind`
   → keeps `Kind=Unspecified`, then calls `parsed.ToUniversalTime()`.
   **`ToUniversalTime()` on `Unspecified` treats it as Local
   time and shifts by the host's UTC offset.**
6. For a user in Cicero IL (UTC-5 in May), a cursor of
   `00:03:57 Unspecified` becomes `05:03:57 Utc` on the node
   side. The SQL `WHERE timestamp_utc > '...05:03:57Z'` then
   excludes any chat whose actual UTC timestamp is between
   `00:03:57` and `05:03:57` — five hours of silently-dropped
   messages.

The bug self-corrects after one successful mirror (newCursor
from JSON has Kind=Utc), but it PREVENTS successful mirrors,
so the cursor stays Unspecified across the manager session.
Triggers on every manager restart with chats persisted between
restarts. Not surfaced earlier because dev iteration in a tight
rebuild-test loop usually keeps chats in a single manager session
or restarts the instance (clearing node DB chat for the new
run), so the cross-restart case is the one that bites real
users first.

Fix is defense-in-depth at both ends:

- **`GSM.Manager\Core\InstanceManager.vb` — `SeedChatCursor`.**
  Final return is `DateTime.SpecifyKind(latest.Value,
  DateTimeKind.Utc)`. The column is named `TimestampUtc` and
  is always written from `DateTime.UtcNow`, so the
  metadata restoration isn't a guess — it's reasserting an
  invariant the storage format dropped. Long XML-doc comment
  on the function captures the full chain so future readers
  don't have to re-derive it.
- **`GSM.Node\Endpoints\InstanceEndpoints.vb` —
  `/api/instances/{id}/chat` endpoint.** Replaces
  `parsed.ToUniversalTime()` with a `Select Case parsed.Kind`:
  Utc → as-is; Local → ToUniversalTime; Unspecified →
  SpecifyKind(parsed, Utc). The contract is that an offset-less
  ISO string in this parameter means "this is a UTC value, the
  sender just didn't put a Z on it" — the parameter is named
  `since` against a column called `timestamp_utc`. Even with
  the manager-side fix, this stricter parsing is cheap defense
  for any future caller that sends offset-less timestamps.

**Recovery path:** chats persisted to the node during the bug
window are still in the node's `chat_messages` table — the bug
was a filter on the read side, not a loss on the write side.
After rebuilding the manager with the fix and restarting it,
the next mirror cycle re-seeds the cursor (now Utc-kind), the
request hits the node with a Z-suffixed `since`, and any chats
more recent than the last successfully-mirrored row come
through on the next poll. **No manual SQL or DB cleanup
needed** — recovery is automatic on the next manager start.
The Manager's other tabs aren't affected: the live Chat tab
queries the node directly and never went through the broken
path; the History window's chat rows show up as soon as
the mirror catches up.

**Files modified:**
- `GSM.Manager\Core\InstanceManager.vb` — `ClearPlayerTracking`
  rewritten to flush bucket as synthetic leaves;
  `StopInstanceAsync.Finally` order swap;
  `RefreshInstanceStateAsync` terminal-state detector;
  `SeedChatCursor` UTC-kind tagging
- `GSM.Node\Endpoints\InstanceEndpoints.vb` —
  `/api/instances/{id}/chat` endpoint Kind-aware `since`
  handling

---

## ROUND D — Session history & UI polish

### Session history persistence

Three tables track player and session history across the lifespan of
sessions, orthogonal to chat retention:

- **PlayerSessions** — aggregate summary per (SessionIdentity, PlayerName).
  First/last seen timestamps, LastHostInstanceId. Upserted on every
  join/leave observation.
- **PlayerActivity** — per-event stream. Every join and leave produces
  a row; powers the timeline view in the History window.
- **SessionHosts** — records which instance hosted which session, and
  when. Opens on TileLoaded, closes on TileUnloaded or instance stop.
  Includes TileName (populated from plugin-supplied Metadata when
  available).

**Retention model:** time-scoped data (ChatMessages) gets pruned on a
configurable `ChatRetentionDays` setting (default 90). Identity-scoped
data (PlayerSessions, PlayerActivity, SessionHosts) is never
time-pruned — it persists until the underlying session identity goes
away (e.g. realm reset).

**Session identity format:**
- Last Oasis parser produces `"lastoasis:{realm_id}:{tile_id}"` via
  its CurrentSessionIdentity property
- Fallback for games without migration semantics: `"{gameId}:{instanceId}"`

`InstanceManager.ResolveSessionIdentity(instanceId)` centralises this
resolution; `GetCurrentSessionIdentity(instanceId)` is the public
wrapper used by the UI (e.g. the History button on InstancePanel).

### HistoryQueryService

`GSM.Manager\Core\HistoryQueryService.vb` — singleton registered in
ManagerProgram DI. Two query surfaces:

- `QueryTimelineAsync(filter)` — returns chronological event rows
  across ChatMessages + PlayerActivity, filtered by SessionIdentity,
  PlayerName, and UTC time range. Powers the History window's
  timeline tab.
- `QuerySnapshotAsync(instantUtc, filter)` — returns who was online
  at a specific instant by replaying PlayerActivity up to that
  timestamp. Powers the "snapshot at instant" tab.

`FormatSessionLabel(sessionIdentity)` produces human-friendly labels
("LO realm Site-Main / Tile 5 / 2026-04-21 19:23") by joining
SessionHosts and the earliest PlayerActivity row. Used throughout the
History window so users never see raw "lastoasis:uuid:uuid".

### HistoryWindow

Non-modal Form registered via MainForm's Tools → History menu AND from
`InstancePanel.OnOpenHistory()` (launched by the per-instance "History"
button, which pre-fills the filter with the instance's current session
and a recent time range).

**UTC / local time toggle:** "Use UTC" checkbox at the top (defaults
to local). `PickerToUtc()` helper uses `DateTime.SpecifyKind` so
pickers produce unambiguous UTC values; `FormatDisplayTime()` converts
on display. The toggle operates on cached query results in-place
(`_lastTimelineResult`, `_lastSnapshotRows`, `_lastSnapshotInstantUtc`)
— no re-query on toggle, just re-render. Pickers default to `DateTime.Now`.

### FormIconHelper (branding consistency)

`GSM.Manager\UI\FormIconHelper.vb` — module with:

- `ApplyTo(form As Form)` — sets the PowerGSM icon on any Form. Silent
  no-op on failure (never let icon load break UI construction).
- `GetLargeBitmap() As Bitmap` — returns a 256×256-or-largest bitmap
  variant of the icon for use as a logo. Caller owns the Bitmap and
  must dispose it.

Resource name: `PowerGSM.ico`. Stream resolved via
`GetType(FormIconHelper).Assembly.GetManifestResourceStream` — works
on modules because the underlying type is NotInheritable Shared.

**Applied to all 16 Forms in the Manager:** MainForm (replaced inline
icon code), NodeSetupForm, NewInstallationForm, NotificationsForm,
TemplateEditorForm, VisibilityProfileEditorForm, HistoryWindow,
PluginStatusForm, SteamCredentialsForm, SteamCredentialEditForm,
RealmCredentialsForm, AutomationRulesForm, RuleEditorForm,
SettingsForm, AddInstanceForm, EditInstanceForm, EditInstallationForm,
LogViewerForm.

**NOT applied to UserControls** (InstancePanel, InstallationPanel,
etc.) — `ApplyTo` takes a Form, so a UserControl would be a type
mismatch. Careful on edit operations that match UserControl
constructors structurally — I've caught this once; revert and target
the specific Form constructor.

### WelcomePanel logo redesign

`WelcomePanel` (in UiPanels.vb) rewritten to display a 128×128
PictureBox at (20, 20) showing the large icon via
`FormIconHelper.GetLargeBitmap()`. Title "PowerGSM" at (170, 40),
subtitle "Game Server Manager" at (170, 85) — both left-aligned
to the same X. Info text at (22, 170) clears the logo vertically.

`Dispose(disposing)` override disposes the PictureBox's Image when
the panel is swapped out of the content area — otherwise every
navigation back to the Nodes root would leak another bitmap copy.

### Settings form rewrite (retention UI)

`SettingsForm` in RemainingForms.vb rewritten with real content:

- **Data Retention section** — NumericUpDown for chat retention days
  (1–3650, default 90), with helper text clarifying that identity-scoped
  data (PlayerSessions, PlayerActivity) is never time-pruned.
- **Paths section** — read-only labels showing resolved full paths for
  `gsm.db` and `Plugins\` directory. `ResolveFullPath` helper wraps
  `IO.Path.GetFullPath` in try/catch.
- **Save / Cancel** with `AcceptButton`/`CancelButton` wiring. Save
  writes to `AppSettings` via `db.SetSetting(SettingKeys.ChatRetentionDays, ...)`.

No new wiring in `ManagerProgram` — `ChatRetentionPruner` already
re-reads the setting on every hourly pass, picks up changes within
the hour.

**VB gotcha learned here:** inside an interpolated string's `{...}`
hole, the expression is normal VB. Don't double-up quotes around
string literals. `$"Database: {ResolveFullPath("gsm.db")}"` is
correct; `$"Database: {ResolveFullPath(""""gsm.db"""")}"` is not.

---

## AUTOMATION REFACTOR

Multi-phase rework of the rule engine to support per-instance
scheduled restarts with coordinated queueing. The core insight:
"manual restart" and "scheduled restart" are different beasts —
manual is fire-and-forget, scheduled needs to serialise across
siblings so one realm's restart completes before the next begins.

### Design decisions locked upfront

- **Schedule location: Hybrid** — Restart fields on InstanceEntity
  materialize an auto-generated AutomationRule; power users can edit
  the generated rule directly in Automation Rules form.
- **Ready-for-next signal: configurable trigger + timeout fallback** —
  Plugin declares via opt-in `IReadySignalProvider` interface (new
  interface rather than new IGamePlugin members, so existing plugins
  keep working unchanged). For Last Oasis: `TileLoaded` kind,
  300s default timeout.
- **Concurrency: installation-scoped default (1), node-wide override
  on NodeEntity** — Default `InstallationEntity.MaxConcurrentRestarts = 1`;
  `NodeEntity.MaxConcurrentRestarts = 0` means "no node-wide limit".
- **Stagger strategy: per-instance cron, coordinator queues in
  acquisition order** — if two instances fire at the same cron tick,
  the coordinator stages them sequentially.
- **Manual restart UX: Shift-click = force, plain click = coordinated**
  (Phase 5, not yet implemented). For now, manual restarts are
  uncoordinated; only automation-rule-driven restarts go through the
  coordinator.
- **Cron overlap policy: `SkipIfRunning` default** for auto-generated
  rules; power user can override via rule editor.
- **LO has NO RCON** — in-game chat warnings unavailable; Discord
  webhook warnings via `NotifyAction` + `Wait` chains only.

### Phase 1 — Contracts + data layer (no behavior change)

**GSM.Contracts\IGamePlugin.vb:**
- `ReadySignalKind` enum: `ServerStateEquals`, `TileLoaded`, `CustomMarker`
- `ReadySignal` class: `Kind + MatchValue`
- `IReadySignalProvider` interface (opt-in): `GetReadyForNextSignal()` +
  `DefaultReadyTimeoutSeconds` readonly property

**GSM.Contracts\IAutomationRule.vb:**
- `WaitForReadySignal(instanceId, timeoutSeconds)` method on `IRuleContext`
  + matching `MustOverride` stub on `RuleContext`
- `WaitForReadySignalAction` class between `WaitAction` and `SequenceAction`
- Execute body: delegates to `ctx.WaitForReadySignal`; returns
  `ActionResult.Ok` on both true and false so enclosing sequence
  still progresses (coordinator releases slot on timeout too)

**GSM.Manager\Core\AutomationEngine.vb:**
- Phase 1 stub on `RuleContextImpl.WaitForReadySignal` that throws
  `NotImplementedException` — just enough to compile. Phase 3a
  replaces this with the real implementation.

**GSM.Manager\Data\GsmDbContext.vb:**
- `NodeEntity.MaxConcurrentRestarts As Integer = 0`
- `InstallationEntity.MaxConcurrentRestarts As Integer = 1`
- `InstanceEntity.RestartEnabled As Boolean = False`
- `InstanceEntity.RestartCron As String` (HasMaxLength 100)
- `InstanceEntity.RestartRuleId As String` (HasMaxLength 100)

**Migration:** `AddRestartScheduling` — five AddColumn operations
on existing tables. No data moves, no table recreations.

### Phase 2 — Polymorphic JSON round-trip

**GSM.Manager\Core\AutomationRuleSerializer.vb** — module owning all
JSON serialisation for AutomationRule's polymorphic slots (Trigger,
Conditions, Action).

**Why hand-rolled:** `System.Text.Json`'s built-in `[JsonPolymorphic]`
only works on base classes, not interfaces. The contracts use
interfaces (`IAction`, `ICondition`, `ITrigger`). Reshaping every
contract into an abstract class would ripple for a Manager-side
serialisation concern.

**Why not JsonConverter(Of T):** The converter's Read override takes
`ByRef reader As Utf8JsonReader`. `Utf8JsonReader` is a ref struct
(contains `Span(Of Byte)`). VB.Net's compiler rejects ref struct
references with BC30668 "obsolete: Types with embedded references
are not supported". Hard stop.

**Actual approach:** `JsonNode` tree traversal. Parse into a
JsonNode (a regular class, VB-friendly), inspect `$type`
discriminator, look up concrete type in a dispatch table, let STJ
deserialise that node as that specific type.

- `SerializeAction` / `DeserializeAction` (and Trigger, Conditions
  variants) are the public API.
- `ConvertActionToNode` — emits `$type` + concrete properties. Handles
  `SequenceAction` specially: its `Steps` list of `IAction` wouldn't
  serialise correctly under a naive call (STJ would emit empty
  objects for IAction), so we recurse explicitly into each step.
- `ConvertNodeToAction` — mirror on read. Looks up concrete type,
  recurses for SequenceAction's Steps.

**Dispatch tables:** `TriggerTypes`, `ConditionTypes`, `ActionTypes` —
Dictionary(Of String, Type) mapping `$type` discriminator to
concrete type. To add a new rule type: implement the interface,
pick a discriminator string, add one line here. That's it.

**Legacy format fallback:** pre-Phase-2 triggers were stored as flat
dictionaries without a `$type` envelope. `DeserializeTriggerLegacy`
recognises the old shape; on next save the rule rewrites in the new
format, so this code only runs during the one-time transition.

**Engine wiring:** `AutomationEngine.DeserializeRule` now calls the
serializer. New `SerializeRuleToEntity(rule, existing?)` helper
function lets callers persist new rules. The old ad-hoc dictionary
parser was removed.

### Phase 3a — RestartCoordinator + ready-signal waits

**GSM.Manager\Core\RestartCoordinator.vb** — singleton registered in
DI. Two concerns:

1. **Slot allocation** — per-installation + per-node semaphores for
   concurrency control. `AcquireAsync(instanceId, cancellation)`
   returns a `RestartSlot`; `Release(slot)` drops both gates. Order:
   installation gate first, node gate second (prevents deadlock
   across two installations sharing a node).

2. **Ready-signal waits** — `WaitForReadySignalAsync(instanceId,
   timeoutSeconds)` blocks until the plugin's declared signal fires,
   the timeout elapses, or the instance reaches a terminal state.
   Uses `TaskCompletionSource` keyed by instanceId in a pending dict.
   `Task.WhenAny(signalTask, timeoutTask, terminalTask)` picks the
   winner.

**Why TCS instead of events:** Waits are rare and transient (one per
coordinated restart, maybe a few per day). A per-wait TCS is simpler
than plumbing a persistent event subscription that fires on every
log line.

**Terminal-state watchdog:** `WatchTerminalStateAsync(instanceId,
signalTask)` polls `InstanceManager.GetLiveState` every 1s until
the signal completes or the instance hits
Stopped/Crashed/CrashLoopHalted. On terminal state, completes the
TCS with False so the wait bails cleanly rather than hanging.

**Construction-cycle break:** `RestartCoordinator` needs
`InstanceManager` for state polling; `InstanceManager` needs
`RestartCoordinator` to notify on TileLoaded. Ctor deps both ways
would deadlock DI. Solution: `AttachInstanceManager(im)` /
`AttachRestartCoordinator(rc)` setter methods called from
`ManagerProgram` after both singletons are resolved.

**Signal notification:** `NotifySignalObserved(instanceId, kind,
observedValue)` — called by InstanceManager from its log-event
handlers. Checks `_pendingSignals[instanceId]`, matches on kind +
(for `ServerStateEquals`) value, calls `pending.Tcs.TrySetResult(True)`
on match.

**Plugin fallback:** If the plugin doesn't implement
`IReadySignalProvider`, the coordinator falls back to a grace delay
(plugin's `DefaultReadyTimeoutSeconds` or 30s default) with no event
wait — still serialises access, just on a timer.

**InstanceManager change:** `HandleTileLoaded` (existing method in
log-stream handler) gets a new tail block that calls
`_restartCoordinator.NotifySignalObserved(instanceId, TileLoaded,
Nothing)`. Additive — doesn't change existing TileLoaded behavior.

**LastOasisPlugin update** (in Plugins\LastOasisPlugin.vb, the
deployed file — NOT in the source tree):
```vb
Implements IGamePlugin
Implements IReadySignalProvider

Public Function GetReadyForNextSignal() As ReadySignal Implements IReadySignalProvider.GetReadyForNextSignal
    Return New ReadySignal With {.Kind = ReadySignalKind.TileLoaded, .MatchValue = Nothing}
End Function

Public ReadOnly Property DefaultReadyTimeoutSeconds As Integer = 300 _
    Implements IReadySignalProvider.DefaultReadyTimeoutSeconds
```

LO's parser already emits TileLoaded when match state hits
LeavingMap, so the signal plumbing works out of the box.

### Phase 3b — CoordinatedRestartAction + slot acquire/release

**IRuleContext additions:**
- `AcquireRestartSlot(instanceId As String) As Task(Of Boolean)` —
  blocks on semaphores, returns True if acquired
- `ReleaseRestartSlot(instanceId As String) As Sub` — synchronous
  (not Task-returning) because it's called from Finally blocks, and
  VB doesn't permit Await in Finally

**CoordinatedRestartAction** (new action in IAutomationRule.vb):
Atomic acquire → stop → delay → start → wait-for-ready → release.
Slot release is in a plain `Finally` block (synchronous, allowed).
Properties: `InstanceId`, `GracefulTimeoutMs`, `DelayBetweenMs`,
`ReadyTimeoutSeconds` (0 = use plugin default).

**Serializer registration:** `"coordinated_restart"` → `GetType(CoordinatedRestartAction)`
added to `ActionTypes`.

**Coordinator additions:**
- `_heldSlots` dictionary (instanceId → RestartSlot) for the
  released-by-instance-id API
- `AcquireForInstanceAsync(instanceId)` — wraps `AcquireAsync`,
  rejects second concurrent acquire for same instance, stashes slot
- `ReleaseForInstance(instanceId)` — looks up + releases the stashed
  slot. No-op if nothing held.

**RuleContextImpl overrides:** `AcquireRestartSlot` and
`ReleaseRestartSlot` delegate to coordinator via lazy DI resolution
(same pattern as `WaitForReadySignal`). `ReleaseRestartSlot` wraps
everything in try/catch so a release-time failure can't mask the
original exception that caused the sequence to bail.

**Behaviour after Phase 3b:**
- Manual restarts (UI button, right-click menu) still fire-and-forget
  as before — NOT coordinated.
- Automation-rule restarts via `CoordinatedRestartAction` go through
  the coordinator with full queueing + ready-signal gating.
- No UI changes yet — Phase 4 materialises rules from EditInstanceForm.

**Cross-project rebuild reminder:** Phase 1, 3a, and 3b all modify
GSM.Contracts. After any contracts change, Node must be rebuilt too
(even though Node doesn't USE the new types — it links against the
same Contracts DLL, so a stale Node DLL is a loader mismatch risk).

### Phase 4a (partial) — SortOrder infrastructure

**New field:** `InstanceEntity.SortOrder As Integer = 0`. Position
within sibling list in an installation. Lower values come first.
Used by the stagger feature (Phase 4 continuation) and installation
panel reorder UI.

**Index:** `HasIndex(New With {InstallationId, SortOrder})` —
composite so `WHERE InstallationId = X ORDER BY SortOrder` uses it
directly.

**Helper:** `GsmDataExtensions.NextSortOrder(db, installationId)` —
returns `max(SortOrder)+1` across siblings, or 1 if none.
`DefaultIfEmpty(0)` pattern avoids "Sequence contains no elements"
on the first insert into a new installation.

**Migration:** `AddInstanceSortOrder`. Three steps:

1. `DropIndex("IX_Instances_InstallationId")` — old single-column
   index; the composite index supersedes it
2. `AddColumn("SortOrder", INTEGER, defaultValue:=0)`
3. `Sql(...)` backfill — see below
4. `CreateIndex("IX_Instances_InstallationId_SortOrder", {InstallationId, SortOrder})`

**Backfill SQL (IMPORTANT — and a hard-won lesson):**

```sql
WITH numbered AS (
    SELECT InstanceId, ROW_NUMBER() OVER (
        PARTITION BY InstallationId ORDER BY CreatedUtc, InstanceId
    ) AS rn FROM Instances
)
UPDATE Instances SET SortOrder = (
    SELECT rn FROM numbered WHERE numbered.InstanceId = Instances.InstanceId
)
```

**What went wrong the first time:** original attempt used a correlated
subquery where the ROW_NUMBER's partition filter collapsed to a single
row before the window function ran, yielding `rn = 1` for every row.
Fix: pre-compute the numbering in a CTE over the full table, then
UPDATE by joining back on InstanceId.

### Migration workflow lessons learned

- **`Update-Database X` is a goto, not an undo.** It means "bring the
  DB to the state immediately after X completes." To undo a single
  migration, pass the name of the PREVIOUS one. To undo all
  migrations, use `Update-Database 0`.
- **Editing a migration .vb file and rebuilding does NOT re-run it.**
  EF tracks applied migrations in `__EFMigrationsHistory`; anything
  listed there is skipped on next `Migrate()` call. To fix a
  misapplied migration: rollback past it (via `Update-Database
  <previous>`) so EF removes the history row, THEN rebuild and run
  so the corrected version applies fresh. OR: apply a corrective
  SQL directly in a DB browser.
- **Fresh deploy is always clean.** If a dev DB got poisoned by a
  bad migration run, the fix on disk still produces the correct
  behaviour on a fresh deploy — EF will run all migrations in order
  with nothing to skip. So a corrective migration isn't strictly
  needed if the dev can be fixed manually.

### Phase 4a — completion (Phase 4a closed)

The rest of Phase 4a landed across multiple iterations after the
SortOrder migration. All items below are done and tested.

**File:** `GSM.Manager\Core\RestartRuleMaterializer.vb` (new)

Three public functions, all `Public Shared` on the module:

- **`Materialize(db, instance) As MaterializationResult`** — reads
  `instance.RestartEnabled` + `RestartCron` + `RestartRuleId` and
  produces the right CRUD on the `AutomationRules` table.
  Does NOT call `SaveChanges` — caller owns the transaction so
  rule and instance commits are atomic. Returns an action enum
  (`NoChange` / `Created` / `Updated` / `Deleted`) plus the rule ID.
  Defensively refuses to stomp drifted rules (returns NoChange).
  Defensively clears `RestartRuleId` when disabling, even when the
  rule entity is missing — catches orphan ID cases.

- **`IsSimpleRestartRule(ruleEntity) As Boolean`** — structural
  drift detection. Returns true iff the rule matches the canonical
  shape: `Scope=Instance`, no conditions, `ScheduleTrigger`,
  `CoordinatedRestartAction` whose `InstanceId` matches `TargetId`.
  Does NOT check value-level fields (cron, timeouts) — only
  structural shape. Drift is purely structural.

- **`ExtractCronFromRule(ruleEntity) As String`** — reads the cron
  from the rule's `ScheduleTrigger`. Used by `EditInstanceForm` on
  load: the rule's cron is the authoritative value, NOT
  `Instance.RestartCron` (which is a cache that can drift if the
  rule was edited elsewhere).

**Comparison subtlety in `Materialize`:** can't call
`SerializeRuleToEntity(rule, existing)` because that mutates
`existing` in place — the post-mutation comparison would always be
equal. Solution: serialize into a fresh temp entity, compare to
existing, copy fields if different. The action enum then accurately
reports NoChange vs Updated.

### EditInstanceForm Restart Schedule section (in `RemainingForms.vb`)

Form size grew 580×560 → 580×755 to fit the section. Config panel
shrunk 300→220 height to partially compensate.

**Layout:** two sibling panels at the same coordinates (`520×240`):
- `_normalPanel` — standard editable controls (visible by default)
- `_driftPanel` — warning text + "Open in Automation Rules..." button

Visibility toggled as a unit via `ApplyDriftState()`. Toggling at
the panel level handles all sub-widgets (including unstored static
labels like "Cron:", "Hour:", "Stagger step:") without per-control
visibility tracking.

**Normal panel contents (top to bottom):**
- Enable scheduled restart checkbox (master toggle)
- Cron text field + live next-run preview ("Next: Fri 4:00 AM (in 18h 23m)" or "Invalid cron expression" in red)
- "Set Daily" preset: hour numeric (0–23) + button → writes `0 H * * *`
- "Set Interval" preset: hours numeric (1–24) + button → writes `0 */N * * *`
- Stagger step numeric (0–60, default 5; 0 = no stagger / literal copy)
- **Propagation:** mutually-exclusive radio group:
  - "This instance only" (default)
  - "Stagger across enabled siblings (renumber by SortOrder)"
  - "Apply same cron to enabled siblings (no stagger)"
- "Enable scheduled restart on all instances first" checkbox (one-way ON only)
- Help text explaining the queue model

**Helper:** `ApplyMinuteOffsetToCron(cron, offsetMinutes) As String`
(Friend Shared) — parses a cron, adds an offset to its minute field,
bumps the hour on overflow when hour is also numeric. Wildcard or
step-style hours (`*`, `*/12`) are left untouched. Negative offsets
supported via floor-divide trick:
`hourBump = (totalMinutes - newMinute) \ 60` (VB's `\` operator
truncates toward zero, so the standard-mod approach loses the borrow
on negatives — the floor-divide form is exact).

**Three states handled on load:**
- **No rule** (RuleId null or entity missing) — load from
  `Instance.RestartCron` cache; orphan case treated as fresh
- **Simple rule** — pull cron from the rule's `ScheduleTrigger`
  (authoritative), not from `Instance.RestartCron` (cache)
- **Drifted rule** — drift panel shown; restart fields on Instance
  NOT touched on save, preserving power-user edits

**Save path — six scenarios all handled:**

| Enable-all | Propagation | Result |
|---|---|---|
| ☐ | None | Just this instance |
| ☐ | Stagger | Stagger across currently-enabled siblings |
| ☐ | Apply same | Literal cron to currently-enabled siblings |
| ☑ | None | Enable everyone, no cron propagation |
| ☑ | Stagger | Enable everyone first, then stagger across all |
| ☑ | Apply same | Enable everyone first, then literal cron to all |

Order of operations matters: Enable-on-all runs FIRST so newly-
enabled siblings count as enabled in the propagation set.

**Stagger formula:** for the active set (this instance + enabled
non-drifted siblings, sorted by SortOrder), find this instance's
renumbered position, then for each sibling at active-position M:
```
newCron = ApplyMinuteOffsetToCron(thisCron, (M - thisPosition) * step)
```
Example: 5 instances all enabled, this is at SortOrder 3 with
`30 4 * * *` typed, step 5 → SortOrder 1=`20 4`, 2=`25 4`, 3=`30 4`,
4=`35 4`, 5=`40 4`. The user's typed cron stays on this instance
untouched; everyone else fans out from there.

**Drift-skip:** any sibling with a drifted rule (via
`IsSiblingDrifted` helper) is left alone in all paths. Drifted
siblings don't get assigned a position in the renumbered active
set and don't get cron writes.

**Engine reload:** `engine.ReloadRules()` is called only when at
least one rule actually changed (`anyRuleChanged` flag). No-op
saves don't trigger a reload, keeping log spam low.

### InstallationPanel reorder UI

In `UiPanels.vb`, `InstallationPanel` got Up/Down buttons in a
right-docked button column next to the instances list:

- New `#` column showing 1-based renumbered position (matches what
  the stagger algorithm computes internally)
- Listview now sorted by `SortOrder ASC`, then `CreatedUtc` as
  tiebreaker
- `OnReorderInstance(direction)` swaps `SortOrder` values with the
  adjacent sibling, persists immediately, then **swaps the row
  CONTENT in place** (text + tag) rather than removing and
  reinserting `ListViewItem` objects. Selection moves to the row
  that now holds the user's data.
- Calls `MainForm.RefreshNodeTree()` afterward so the tree reflects
  the new order.

**Why content-swap over item-move:** earlier attempts removed the
two `ListViewItem` objects and re-inserted them at swapped indices.
This worked for the data, but the Win32 listview's selection state
is keyed by row index, and the remove/reinsert dance was getting
mixed up with selection rendering. Swapping content keeps both row
objects in place — simpler, faster, no selection-state confusion.

### Delete-instance warning + cascade

In `MainForm.OnDeleteInstance`: pre-check whether the instance has
an existing (not just a stale ID for) restart rule. Different
confirmation message when a rule is involved. Cascade-delete the
rule entity in the same transaction. `engine.ReloadRules()` runs
afterward only when a rule was actually removed.

Stale `RestartRuleId` (entity missing) is treated as no-rule: no
special warning, no engine reload, no cascade attempted.

### Tree state preservation across refreshes

`MainForm.RefreshNodeTree()` previously did `Nodes.Clear()` +
rebuild, which collapsed the tree to its initial state and lost
the user's selection on every action that touched the DB. Fixed
with capture-and-restore:

- `CollectExpandedTags(nodes, tags)` — recursive walk gathering
  Tag values of expanded nodes BEFORE the clear
- `RestoreExpandedTags(nodes, tags)` — recursive walk after the
  rebuild, re-expanding any node whose tag was captured
- `FindNodeByTag(nodes, tag)` — recursive search to find the new
  TreeNode for a previously-selected tag (so `SelectedNode = X`
  can restore selection)

Tag values are stable across rebuilds even though `TreeNode`
references aren't — perfect identity key.

Instance loop in `RefreshNodeTree` also picked up the
`OrderBy(SortOrder).ThenBy(CreatedUtc)` so the tree mirrors the
InstallationPanel's instance order — without this, even a
refreshed tree would show instances in raw insert order.

### Critical: AfterSelect suppression during programmatic restoration

The single nastiest bug of the session. Reproduction:

1. User clicks Up on a row in InstallationPanel's listview
2. `OnReorderInstance` swaps content, sets selection on moved row
3. Calls `RefreshNodeTree()`
4. RefreshNodeTree captures expanded tags + selected tag, clears
   tree, rebuilds, then assigns `_treeView.SelectedNode = X` to
   restore selection
5. **That assignment fires `AfterSelect`** (WinForms TreeView
   doesn't have a no-event variant)
6. `TreeView_AfterSelect` matches `installation:X` and calls
   `ShowPanel(New InstallationPanel(X))`
7. `ShowPanel` disposes the current InstallationPanel and creates
   a brand new one
8. The fresh panel has NO listview selection — the user's
   just-set selection is gone

Fix: `_suppressTreeAfterSelect` flag on MainForm. Set true around
the restoration `SelectedNode` assignment, checked at the top of
`TreeView_AfterSelect` for early return.

**Bonus side effect:** every previous `RefreshNodeTree` callsite
(EditInstance save, EditInstallation save, AddNode, AddInstance,
Delete operations, etc.) was previously rebuilding the entire active
panel even when the entity hadn't changed. The fix makes all of
those snappier — panels only rebuild when the user actually
navigates to a different node.

**Lesson learned:** when a bug seems impossible to diagnose,
diagnostic instrumentation beats theory crafting. The whole
session's worth of focus / `HideSelection` / `Show()` / `Activate()`
theories were all wrong; a single MessageBox at the top of
`OnReorderInstance` showing `SelectedItems.Count` revealed in 30
seconds that the count was 0 when it should have been 1, which
pointed straight at "something between the handler exit and the
next handler entry is clearing selection."

### State-driven Start/Stop/Restart buttons (InstancePanel)

New cached field `_latestProcState` updated on every state observation.
New method `RefreshButtonsFromState()` drives button enabled-state:

- `Running` → Stop + Restart enabled, Start disabled
- `Stopped` / `Crashed` / `CrashLoopHalted` → Start enabled, others disabled
- Transitional states (`Starting` / `Stopping` / `Updating`) → all disabled
- Unknown / `WaitingForInput` → all disabled (safe default)

Called from end of `ApplyProcessState` (3-second refresh tick) and
Finally blocks of click handlers. The old `SetButtonsEnabled(True)`
in Finally was wrong — e.g. after a successful Stop, it'd re-enable
the Stop button which should be disabled.

### Execution history details column (AutomationRulesForm)

Replaced the 50-char hard-truncated raw JSON with
`FormatExecutionDetails(exec)`:
- If `SkipReason` is set → show that
- Else deserialize `ActionResultJson` as `ActionResult` and show its
  `Message` field
- Fallback: 80-char-truncated raw JSON if parse fails

Widened Details column 220→290 px. Shrunk Rule column 150→100
(it shows GUIDs anyway).

**Subtle gotcha avoided:** initial implementation used
`Dictionary(Of String, Object)` parse — STJ boxes values as
`JsonElement` and `.ToString()` on a string-kinded `JsonElement`
returns content without quotes on .NET 8 but the behavior varies
by version. Switched to direct `ActionResult` deserialization for
version-stable behavior.

### CoordinatedRestartAction — skip-when-not-Running guard

In `GSM.Contracts\IAutomationRule.vb` `CoordinatedRestartAction.Execute`:
state check BEFORE acquiring slot. If instance state isn't `Running`,
returns `ActionResult.Ok("Skipped: <id> is <State>, not Running")`
without side effects.

**Why Ok and not Fail:** the rule did exactly what it should have.
Nightly cron tick on a manually-stopped instance is a no-op by
design — logging it as `Failed` would be misleading. The execution
history shows "Executed" with the skip message in Details.

**Why before slot acquisition:** no point queueing behind other
restarts if we're going to bail. Cleaner too: don't have to remember
to release.

**Transitional state safety:** Starting / Stopping / Updating also
skip. Restarting mid-transition is destructive; better to skip and
let the next cron tick catch a stable state.

### AutomationEngine.Start() bug fix

The engine was registered in DI but never started. `_engineCts`
stayed null forever. First time anything called `ReloadRules`
which called `LoadRulesFromDatabase` which called `SetupTrigger`
which did `timer.Start(_engineCts.Token)` — NullReferenceException.

No previous test caught this because no rules existed in the DB
until the new EditInstanceForm started writing them.

Two-part fix:
- `ManagerProgram` now calls `engine?.Start()` after the chat
  pruner starts (services are wired by then). Plus matching
  `engine?.Stop()` in the shutdown hook for symmetry.
- `AutomationEngine.ReloadRules` is now self-starting: if
  `_engineCts` is null or cancelled, synthesize a fresh one. Makes
  `ReloadRules` safe to call from any UI path without requiring the
  caller to know about engine lifecycle.

### AutomationRulesForm — modal → non-modal singleton

Was opened with `ShowDialog()` from the Tools menu, the tree-root
click, and the EditInstanceForm drift redirect. Three reasons it
should be non-modal:

1. Live-updating execution history — you want to keep it open and
   watch rules fire from elsewhere
2. Matches History window precedent
3. Rule firing happens in the background; modal blocks the user
   from doing anything else while inspecting

Fix in MainForm:
- New `_automationWindow` field tracking the singleton
- `OnAutomationRules()` made `Public`. Brings existing window to
  front (un-minimize + Activate) if open; otherwise creates,
  hooks `FormClosed` to drop reference, then `Show(Me)` for
  owner-coupling.
- `EditInstanceForm.OnOpenInAutomationRules` finds MainForm via
  `Application.OpenForms.OfType(Of MainForm)()` and calls
  `OnAutomationRules` — routes through the singleton path.

**Owner-coupling rationale:** `Show(Me)` makes the window stay above
MainForm in z-order. Without owner, clicking the tree-root
"Automation Rules" node would open the window but it'd immediately
go behind MainForm — the tree click event continues dispatching
back through MainForm, which steals focus. Owner-coupling sidesteps
the race entirely. Side effect: minimizing MainForm minimizes the
child too. Acceptable trade-off given the alternative was the
window disappearing on click.

### Tree-click race for non-modal child windows — lessons

General pattern: when a non-modal window is opened from a click
handler ON A CHILD CONTROL of MainForm (tree node, button on a
panel), the click event continues dispatching after the new window
appears, and MainForm steals focus back at the end of dispatch.
Fix is owner-coupling (`Show(Me)`) for windows that should stay
above MainForm, OR `BeginInvoke(Activate)` deferral for windows
that should be peers.

History window uses no-owner because users want it independent of
MainForm minimization. AutomationRulesForm uses owner-coupling
because it's reachable from a tree node and the alternative is
losing it on every click.

### Phase 4a — closed

All Phase 4a items complete:
- Stagger + propagation in EditInstanceForm with all six save scenarios
- InstallationPanel reorder UI with Up/Down buttons
- Delete-instance warning + cascade
- Tree state preservation
- Plus: state-driven Start/Stop/Restart buttons, execution history
  details extraction, CoordinatedRestartAction skip-when-not-Running,
  engine startup fix, AutomationRulesForm non-modal singleton.

### Phase 4b-pre1 — scope & filter model expansion (closed)

Groundwork for the Phase 4b RuleEditorForm rewrite. Adds two
new rule scopes plus an optional game-level filter, so the
rewritten editor can express rules like "all Last Oasis
instances tagged 'realm-alpha' across any node" without us
having to backtrack on the model layer mid-form-rewrite.

**Design pivot during the round:** initial proposal was a
plugin-provided `IGroupingProvider` interface with a label
like "Realm" — plugins opt in, plugins without it hide the
grouping field. User counter-proposed `InstanceSetTag`: a
generic, game-agnostic, user-defined tag on every instance.
This is strictly simpler (no new interface, no plugin opt-in,
no per-game gating in the UI) and generalises better — a
Factorio admin can use it to group instances as production /
test, a Last Oasis admin uses it for realms. The plugin
doesn't know or care.

**Files modified:**

- `GSM.Contracts\IAutomationRule.vb`
  - `RuleScope` enum: added `Node`, `InstanceSet` (5 values total)
  - `AutomationRule.GameFilter` (nullable)
  - `AllInstancesEmptyCondition` refactored: `InstallationId`
    field replaced with `Scope`/`TargetId`/`GameFilter` triplet.
    Default `Scope = Installation` keeps the most-common
    historical use case working without a migration of
    serialised JSON (fresh DB anyway, but a defensive default).
  - `IRuleContext`/`RuleContext`:
    `GetInstanceIdsForScope(scope, targetId, gameFilter)`
    new method. Old `GetInstanceIdsForInstallation` kept
    as a thin convenience for existing callers.

- `GSM.Manager\Data\GsmDbContext.vb`
  - `InstanceEntity.InstanceSetTag` (nullable, 100 char cap)
    — indexed because the dominant access pattern at rule
    fire time is `WHERE InstanceSetTag = X [AND GameId = Y]`
    across the whole Instances table.
  - `AutomationRuleEntity.GameFilter` (nullable, 100 char cap)
    — NOT indexed; engine reads all enabled rules at
    startup/reload so per-rule filter is in memory.

- `GSM.Manager\Core\AutomationEngine.vb`
  - `RuleContextImpl.GetInstanceIdsForScope` — handles all
    5 scopes via direct EF query. Installation scope still
    delegates to InstanceManager (existing path) plus an
    optional `ApplyGameFilter` post-filter when the rule
    sets `GameFilter` on Installation scope (defensive;
    redundant for well-formed installations).
  - Misconfigured `Node`/`InstanceSet` scope with empty
    `TargetId` returns empty rather than "all instances" —
    avoids the footgun where a typo in target accidentally
    targets every instance in the system.
  - `DeserializeRule` reads `GameFilter` from entity column
    to in-memory rule
  - `SerializeRuleToEntity` writes `GameFilter` back

- `GSM.Manager\Core\RestartRuleMaterializer.vb`
  - `IsSimpleRestartRule` treats any non-null `GameFilter`
    as drift. Reasoning: a simple restart rule targets ONE
    specific instance whose game is already determined, so
    a `GameFilter` is at best redundant and at worst
    contradictory (e.g. user picks `GameFilter = factorio`
    on a rule for a Last Oasis instance — rule fires but
    resolves zero instances). Either way, the simple form
    can't express it.
  - `Materialize` comparison includes `GameFilter` so
    changes round-trip

- `GSM.Manager\UI\RemainingForms.vb` (EditInstanceForm)
  - New "Instance Set:" combo box with autocomplete pulling
    distinct existing `InstanceSetTag` values from the DB.
    `DropDownStyle = DropDown` (free-form text allowed) +
    `AutoCompleteMode = SuggestAppend` for live narrowing.
  - Empty-or-whitespace input normalised to `Nothing` on
    save — the InstanceSet scope query uses string equality,
    so empty string and Nothing should behave identically;
    storing Nothing keeps the data shape clean.
  - Form size grew 580×755 → 580×785 to fit the new row

**Migration:** `Add-Migration AddInstanceSetTagAndGameFilter`
then `Update-Database`. EF generates two AddColumn statements
plus the InstanceSetTag index. No backfill needed — nullable
columns default to NULL on existing rows.

**Out of scope for this round (deferred to 4b-1):**

- No UI to author rules with `Node`/`InstanceSet` scope or
  `GameFilter`. The existing stub `RuleEditorForm` still
  only supports Schedule/Manual/VersionMismatch with no
  action picker — it's effectively unusable but unchanged.
- No bulk Instance Set editor (have to tag instances one
  at a time via Edit Instance).

### Phase 4b-1 — RuleEditorForm rewrite shell (closed)

Replaced the stub editor with a real one. Power users can
now author single-action rules covering all 5 scopes, all 4
trigger types, and any of the 11 leaf action types via a
dropdown-driven form. Conditions section is a placeholder
(deferred to 4b-2). SequenceAction is excluded from the
action picker (deferred to 4b-3) but rules with existing
sequences load with a warning and round-trip the sequence
untouched on save.

**New file:** `GSM.Manager\UI\RuleEditorForm.vb` (~1100 lines)

**Form layout** (FixedDialog 760×800):
- **Rule** group: Name, Enabled, Scope (5 values), Game filter,
  Target combo (varies by scope), Overlap policy
- **Trigger** group: type picker + sub-editor for the
  selected type (Schedule with cron preview & presets,
  StateChange with from/to combos, VersionMismatch and
  Manual as info-only)
- **Conditions** group: placeholder text only — existing
  conditions on a rule are preserved across save in
  `_preservedConditions` and re-serialised untouched
- **Action** group: type picker + sub-editor for the
  selected type (11 builders covering coordinated_restart,
  start/stop/restart_instance, start/stop_all_instances,
  update_installation, send_rcon, notify, wait,
  wait_for_ready)
- Save / Cancel

**Sub-editor pattern:** every trigger / action type has a
`Build*Editor()` method returning a `TriggerSubEditor` /
`ActionSubEditor` record:
```vb
Friend Class TriggerSubEditor
    Public Property Panel As Panel
    Public Property BuildFn As Func(Of ITrigger)
    Public Property LoadFn As Action(Of ITrigger)
End Class
```
The lambdas close over the panel's controls so the form
doesn't need per-type field storage. `OnTriggerTypeChanged`
/ `OnActionTypeChanged` clears the host panel, dispatches
to the right `Build*Editor()` method, and mounts the
resulting Panel.

**Key VB.Net gotcha** (added to the gotcha table): lambdas
that construct a concrete `ScheduleTrigger` / `StartInstanceAction`
/ etc. infer their return type as `Func(Of ConcreteType)`,
which does NOT fit `Func(Of ITrigger)` / `Func(Of IAction)`
slots. Two workarounds:
- Single-expression lambdas: wrap the return in
  `CType(..., ITrigger)` / `CType(..., IAction)`.
- Multi-line lambdas with branching: use explicit return
  type — `Function() As ITrigger ... End Function`.

Used CType for the simple cases (most builders) and
explicit return type for StateChangeTrigger and NotifyAction
whose construction logic branches.

**Target combo behaviour by scope:**
- Instance / Installation / Node → `DropDownList` of `IdItem`
  entries pre-populated from the cached lookup data
- InstanceSet → `DropDown` (allows free-form text) with
  AutoCompleteMode = SuggestAppend pulling from existing
  distinct tags. Free-form lets users target a tag they're
  about to create.
- AllInstances → label and combo hidden

The target combo's `DropDownStyle` is toggled in
`OnScopeChanged` and contents fully cleared & repopulated
on every scope change.

**SequenceAction round-trip in 4b-1:** when a rule's action
is a SequenceAction, the action picker is disabled and a
warning label is shown in the sub-editor panel. The
sequence is stashed in `_preservedSequenceAction` and
written back unchanged on save. Other fields (Name,
Scope, Trigger, GameFilter, Overlap, Enabled) remain
editable. This means power users can adjust a coordinated
update rule's name / target without losing the sequence
steps before 4b-3 lands.

**Validation on save:**
- Name required
- Target required for non-AllInstances scope
- Cron expression required + parseable for ScheduleTrigger
- Action's required identifier present (instance,
  installation, or notification plugin per action type)
- SendRcon command non-empty; Notify message non-empty
- WaitAction needs no validation (DurationMs has a sensible default)

**Helper utilities (`Friend Shared`, callable from
future forms / sub-editors):**
- `GetSelectedId(combo)` — unwraps an IdItem from the
  combo's selected item
- `SelectComboById(combo, id)` — selects the item whose
  IdItem.Id matches; no-op if not found
- `ClampToRange(value, num)` — clamps an Integer into a
  NumericUpDown's Min/Max range so loading an out-of-
  range value doesn't throw at .Value assignment
- `IdItem` class — lightweight (Id, Display) item carrier
  for combo entries

**Lookup data caching:** form pulls all needed lookups in
one `LoadLookupData` pass at construction time:
`_instances`, `_installations`, `_nodes`,
`_notificationPlugins`, `_distinctSetTags`,
`_distinctGameIds`. All `AsNoTracking()`. Type-switch
handlers reuse this cached data instead of re-querying
on every dropdown change.

**Deferred to 4b-2 (conditions UI):** ConditionEditorForm
for adding/editing/removing conditions, ConditionMode
selector (All vs Any), per-condition sub-editors for the
3 condition types. Currently the Conditions section just
shows a placeholder string and `_preservedConditions`
holds the deserialised list across save.

**Deferred to 4b-3 (sequence editor):** SequenceAction
sub-editor with reorderable step list; StepEditorForm
modal for editing one step; re-enable SequenceAction in
the action picker dropdown.

**Files modified:**
- New: `GSM.Manager\UI\RuleEditorForm.vb` (~1100 lines)
- Modified: `GSM.Manager\UI\RemainingForms.vb` — old stub
  RuleEditorForm class removed (~160 lines deleted)

No changes to AutomationRulesForm — its
`Using dlg As New RuleEditorForm()` calls work unchanged
because the new class has the same name + same constructor
signature (`Optional editRuleId As String = Nothing`) in
the same namespace.

### Phase 5 — Version-mismatch trigger wiring (skeleton)

Closed the gap between the rule editor (which has supported
VersionMismatchTrigger since Phase 4b-1) and the engine
(which previously had no path to fire those rules).

**The architectural decision:** skeleton-only for now. A new
public `RaiseVersionMismatchAsync(installationId)` method on
AutomationEngine fires every enabled rule with a
VersionMismatchTrigger whose scope/target matches the
affected installation. The actual mismatch *detection*
(SteamCMD app_info_print polling, Factorio API polling,
etc.) is deferred to a future round — plugins or external
tools that already detect updates can call this method
directly today, and a future polling service will plug into
the same entry point without further engine changes.

Reasoning for the skeleton-first approach:
- Polling design has open questions (per-installation
  intervals, re-fire throttling, how to surface "available
  vs installed" version info in the UI) that aren't worth
  resolving in the same round as the engine wiring
- Plugins might want to push detection events via their
  own channels (Factorio's update API is push-friendly,
  Steam isn't) — keeping the entry point generic instead
  of polling-specific avoids forcing one design
- The rule editor's UI for VersionMismatchTrigger has been
  available for ~1 month with a "not yet wired" caveat;
  removing that caveat now closes the user-visible gap

**Scope-matching logic** in `RaiseVersionMismatchAsync`
mirrors the rest of the engine:
- **Instance** — rule fires if its TargetId is one of the
  instances under the affected installation
- **Installation** — rule fires if TargetId == installationId
- **Node** — rule fires if the affected installation lives
  on the rule's target node, with optional GameFilter pre-check
- **InstanceSet** — rule fires if any instance under the
  affected installation carries the rule's TargetId tag
- **AllInstances** — always fires, optionally narrowed by
  GameFilter

The scope-match helper `VersionMismatchRuleMatches` is
factored out for testability — takes pre-resolved
installation context (GameId, NodeId, instance ids, set
tags) so the matching logic is pure.

**Idempotency / throttling is the caller's concern.** The
engine has no "don't refire if user hasn't updated yet"
logic. A polling service that ticks every 5 minutes should
track which installations it's already raised for and only
call again when the upstream version changes again —
otherwise every poll cycle would refire every matching
rule. This is intentional: the engine doesn't know what
"version" means semantically (build numbers? patch dates?
git hashes?) — only callers know.

**Trigger reason format:** `VersionMismatch:{installationId}`.
Visible in the execution history's TriggerReason column,
lets users distinguish manual fires from version-driven
ones from scheduled fires.

**Files modified:**
- `GSM.Manager\Core\AutomationEngine.vb` — added
  `RaiseVersionMismatchAsync` (~95 lines) and
  `VersionMismatchRuleMatches` helper (~40 lines).
  `SetupTrigger` comment updated to note that
  VersionMismatch is wired via the new method.
- `GSM.Manager\UI\RuleEditorForm.vb` — trigger help text
  updated from "not yet wired" to "wired via
  RaiseVersionMismatchAsync, polling not yet automatic"

**What's still pending (deferred to a future round):**
- Automatic version-check polling service
- Per-plugin `GetLatestVersionAsync` capability on IGamePlugin
- "Installed version" / "Available version" columns on
  InstallationEntity
- UI surfacing of version info in InstallationPanel

### Phase 5 — Version-mismatch full implementation (closed)

The deferred items from the skeleton are now done. End-to-end
working version-mismatch detection: a polling service runs
every 60 minutes, checks each installation's upstream version,
and fires VersionMismatchTrigger rules when the upstream
advances past the installed build. UI surfaces installed vs
latest with a checked-Nm-ago hint and a manual "Check Now"
button.

**The architectural decisions:**

1. **Opt-in `IVersionAwarePlugin` interface** rather than a
   required method on IGamePlugin. Same pattern as
   `IDestinationTargetingPlugin` (4b-1.5) and `IReadySignalProvider`.
   Reasoning: plugins like webhooks or notification transports
   genuinely don't have a version concept; forcing them to
   throw NotImplementedException would be ceremony for nothing.
   Steam-installed games don't need it either — the existing
   `InstallationManager.CheckForUpdatesAsync` Steam path
   already handles them.

2. **60-minute poll interval.** Adjustable later via AppSetting
   if needed; for now hard-coded as a constant. Balance:
   short enough to catch updates the same workday they ship,
   long enough that SteamCMD invocations don't pile up (each
   Steam check spawns a SteamCMD process on the node and runs
   for 5-10 seconds against Valve's CDN).

3. **Throttling via `LastVersionCheckUtc` + 55-minute restart
   grace.** Manager restarts during dev iteration don't
   trigger an immediate fresh poll of every installation —
   would otherwise burn through Steam quota on every F5.
   Manual "Check Now" button passes `respectThrottle=False`
   to bypass.

4. **One event per detected upstream advance.** The mismatch
   event fires only when latest != installed AND latest !=
   previously-known. Subsequent polls finding the same
   upstream value update the timestamp but don't refire,
   avoiding hourly notification spam while the user takes
   their time to update.

**Files added/modified:**

- `GSM.Contracts\IGamePlugin.vb` — added
  `IVersionAwarePlugin` interface (~50 lines).
- `GSM.Manager\Data\GsmDbContext.vb` — added two columns
  to `InstallationEntity`:
  - `LatestKnownVersion As String` — last value the
    polling service observed from upstream
  - `LastVersionCheckUtc As DateTime?` — when the last
    successful poll happened (null until first success)
- Migration `AddVersionTrackingColumns` — generated via
  `Add-Migration` in PMC. Both columns nullable + additive,
  safe migration.
- `GSM.Manager\Core\VersionCheckService.vb` (new file,
  ~330 lines). Background polling service following the
  ChatRetentionPruner lifecycle pattern. Has both a
  background loop and a public `CheckInstallationAsync`
  for manual one-shot checks (used by the InstallationPanel
  "Check Now" button).
- `GSM.Manager\Core\AutomationEngine.vb` — already had
  `RaiseVersionMismatchAsync` from the skeleton round; no
  changes needed.
- `GSM.Manager\ManagerProgram.vb` — DI registration for
  `VersionCheckService` (singleton, alongside
  `ChatRetentionPruner`). Started AFTER `AutomationEngine`
  because the service raises events into it; stopped
  BEFORE the engine in shutdown order.
- `GSM.Manager\UI\UiPanels.vb` — InstallationPanel version
  label upgraded to show installed → latest with checked-Nm-
  ago suffix; "Check for Updates" button now routes through
  `VersionCheckService.CheckInstallationAsync` so it covers
  both Steam and plugin paths uniformly. NodePanel's
  Version column shows just the buildid ("22526048") via
  a `FormatVersionShort` helper instead of the full stamp,
  so the column doesn't get truncated to ellipsis.
- `GSM.Manager\bin\Debug\net8.0-windows\Plugins\FactorioPlugin.vb`
  — implements `IVersionAwarePlugin` via factorio.com's
  `latest-releases` JSON API. Adds an `UseExperimental`
  install config field so users can opt into tracking
  experimental builds.
  - LO plugin doesn't need to implement `IVersionAwarePlugin`
    — SteamCMD-installed, so the Steam path covers it.

**Two paths converge in `VersionCheckService.CheckInstallationAsync`:**

- **Steam path** (preferred when `InstallMethod=SteamCmd`):
  delegates to `_installationManager.CheckForUpdatesAsync`,
  which talks to the node and reads the ACF manifest. The
  result has `UpdateAvailable: Boolean` (authoritative — used
  for the firing decision) and `LatestBuildId: String` (used
  to format the stored `LatestKnownVersion`).
- **Plugin path** (fallback for non-Steam, or Steam path
  failure): if the plugin implements `IVersionAwarePlugin`,
  calls `GetLatestVersionAsync` and compares the returned
  string against `InstalledVersion` for the firing decision.
  Plugin authors are responsible for returning a string format
  that matches what `InstalledVersion` looks like for their
  game's install path — otherwise the comparison spuriously
  reports out-of-date forever (known limitation, documented
  in code).

Installations whose plugin is neither SteamCmd-based nor
`IVersionAwarePlugin` are silently skipped (warn-level log
entry only). VersionMismatch rules referencing those
installations simply never fire.

**Critical bug found and fixed during testing:**

First attempt stored just the raw buildid ("22526048") in
`LatestKnownVersion` while `InstalledVersion` was the full
stamp ("steam:920720@public build 22526048"). This caused
the UI to ALWAYS display "update available" even when
buildids matched, because string equality on the two
formats can't possibly succeed.

Fix:
- For the firing decision, use `result.UpdateAvailable`
  directly (authoritative — InstallationManager has already
  done apples-to-apples comparison via the ACF manifest).
- For storage, splice the latest buildid into the same
  prefix as InstalledVersion: `"steam:920720@public build
  {LatestBuildId}"`. Now string comparison works correctly
  for the UI display.
- Reload the local entity view after CheckForUpdatesAsync
  runs because that method updates InstalledVersion in its
  own DbContext scope.

**Trigger reason format:** `VersionMismatch:{installationId}`.
Visible in the execution history's TriggerReason column,
lets users distinguish version-driven fires from manual or
scheduled ones.

**Verified working end-to-end:** Manual "Check Now" button
for LO_Playground returns matching buildids and correctly
displays "Up to date (steam:920720@public build 22526048)"
in green; the version label shows the same.

**Known limitations carried forward:**

- **Plugin-path format alignment.** A plugin that returns
  "2.0.42" while InstalledVersion is
  "steam:427520@public build 12345" reports out-of-date
  forever. Plugin author's responsibility to return a
  matching format — future work could add a stamp-builder
  helper plugins reuse, but for now it's plugin-side. For
  Factorio specifically: if installed via SteamCMD, the
  Steam path takes priority and avoids the issue entirely.
  Direct-download installs would need the plugin to record
  a matching format on install.
- **No semver awareness.** Versions are opaque strings
  compared for equality only. "2.0.42" → "2.0.43" is
  treated the same as "2.0.43" → "2.0.42" (downgrade)
  — both are "different," so the rule fires either way.
  Acceptable since users authoring rules can add
  conditions if they want stricter semantics.

### Phase 4b-3 polish — Tabbed layout (closed)

Final layout pass on RuleEditorForm. The form had grown to
~935px tall with all four sections (Rule / Trigger /
Conditions / Action) stacked vertically — pushing against
1080p comfort and forcing scroll on smaller displays.
Reorganised into a tabbed layout that drops form height to
~480px and groups fields functionally instead of by
typography.

**Layout:**
```
┌─ Edit Rule ────────────────────────────────────────────┐
│ Name: [...........................] [✓] Enabled        │  ← Header strip (always visible)
│ ──────────────────────────────────────────────────────│
│ ┌──────┬─────────┬────────────┬────────┐              │
│ │ Rule │ Trigger │ Conditions │ Action │              │  ← Tabs
│ └──────┴─────────┴────────────┴────────┘              │
│ ┌────────────────────────────────────────────────────┐ │
│ │  (selected tab's content here)                     │ │  ← One tab visible at a time
│ └────────────────────────────────────────────────────┘ │
│                                    [Save] [Cancel]    │
└────────────────────────────────────────────────────────┘
```

**Header strip — Name + Enabled:** Lives outside any tab
because they're the rule's identity (referenced from every
tab) and a global toggle (conceptually outside any one
section). Saves users a click when they just want to rename
or toggle.

**Tab contents:**
- **Rule:** Scope, GameFilter, Target, Overlap. Fields that
  say "what does this rule apply to."
- **Trigger:** Type combo + sub-editor. Sub-editor area
  expanded to ~240px tall (was 100px when stacked).
- **Conditions:** Mode + Add/Edit/Remove/↑/↓ + listbox.
  Listbox grew to ~240px tall (was 85px).
- **Action:** Type combo + sub-editor. Sub-editor area
  also ~240px tall, finally giving the sequence editor's
  listbox proper breathing room (~170px, visible ~10 rows).

All tabs sized to fit the largest. Smaller tabs have
whitespace below their content — acceptable since the tab
background is uniform and the bordered sub-panels visually
frame their content.

**Validation glyphs:** When Save fails, the broken tab gets
a "⚠ " prefix (Segoe UI U+26A0 — plain Unicode, renders
cleanly without font mixing) and the form auto-switches to
that tab so the user sees the inline error message in
context. Asymmetric "show only when broken" pattern —
adding ✓ checkmarks to good tabs every time would feel like
the form is grading you. The glyph stays until the next
Save attempt clears it.

`_plainTabCaptions As Dictionary(Of TabPage, String)` stashes
the original captions at construction so `ClearTabValidationGlyphs`
can restore them at the start of each Save attempt. The
`MarkTabBroken(tab)` helper is idempotent — calling twice
doesn't double the prefix.

**Validation order matches tab order** so the auto-selected
"first broken tab" is also the leftmost. Header (Name) →
Rule → Trigger → Action. Conditions tab has no save-time
validation (empty list is valid; per-condition validation
runs inside ConditionEditorForm).

**Header-strip Name validation** can't mark a tab (Name lives
outside the tab control) so it focuses the textbox directly
instead of switching tabs.

**Forms structure after this round:**
- RuleEditorForm.vb — ~1500 lines, tabbed layout
- ActionEditorFactory.vb — ~750 lines, 11 leaf-action
  builders + helpers
- StepEditorForm.vb — ~190 lines, single-step modal
- ConditionEditorForm.vb — ~440 lines, single-condition
  modal

**Note on file rewrites:** This was a complete rewrite of
RuleEditorForm.vb rather than incremental edits. The
structural change (form → header + tabs) touched the layout
section top-to-bottom, and surgical edits across that many
sections would have been more error-prone than a clean
rewrite. The handler/validation/sub-editor logic was
preserved verbatim — only the layout containers changed.

### Phase 4b-3 — Sequence editor (closed)

Final piece of the rule editor: full SequenceAction authoring
with reorderable step list and modal step editor. With this
phase, the rule editor is feature-complete for all 12 action
types. Users can compose multi-step coordinated operations
(announcement → wait → wait_for_player_count → update →
start all → notify) entirely through the UI.

**Three new architectural pieces:**

1. **ActionEditorFactory** (new file `GSM.Manager\UI\ActionEditorFactory.vb`,
   ~750 lines). Holds the 11 leaf-action builders previously
   private to RuleEditorForm. Constructor takes the lookup
   data (instances, installations, notification destinations).
   `BuildEditor(id) As ActionSubEditor` is the public
   dispatcher. Two `Public Shared` helpers: `GetActionTypeId`
   and `ValidateAction`, both moved out of the form so
   StepEditorForm can call them without going through the
   parent form. Row helpers (AddInstanceComboRow, etc.) are
   private instance methods on the factory.

2. **StepEditorForm** (new file `GSM.Manager\UI\StepEditorForm.vb`,
   ~190 lines). Modal that mirrors ConditionEditorForm's
   shape: type combo + sub-panel + Save/Cancel. Constructor
   takes the factory; uses it to build whichever leaf-action
   sub-editor matches the chosen type. Type combo excludes
   "sequence" — no nested-sequence UI even though the
   serialiser supports nesting (use cases rare, UX gets
   unwieldy).

3. **Sequence sub-editor in RuleEditorForm** (~150 lines
   added). Lives in the form (not the factory) because it
   needs access to mutable `_sequenceSteps` state and the
   StepEditorForm modal launcher (factory creating the modal
   would be a circular dependency). Layout fits inside the
   existing 690×145 sub-panel: top row = Add/Edit/Remove/↑/↓
   buttons + ContinueOnFailure checkbox; below = step listbox.

**File-size delta:**
- RuleEditorForm.vb: ~84KB → ~69KB (removed ~600 lines of
  builders + helpers that moved to factory; added ~150
  lines of sequence editor)
- New: ActionEditorFactory.vb (~28KB)
- New: StepEditorForm.vb (~8KB)

**Step summary format** (one line per step in the listbox):
```
1. Notify PowerGSM #test: "Restart in 5 min"
2. Wait 240000ms
3. Notify PowerGSM #test: "Restart in 1 min"
4. Wait 60000ms
5. Coordinated Restart: LOP_Site-Main_S01
```
Numbers are 1-based to match the engine's "Step 1/N" log
progress messages. Long messages truncate at 30 chars +
"..." so the listbox stays readable. Looked-up names for
instance/installation/destination references; falls back to
raw ID if entity was deleted.

**Sequence validation on save:**
- Sequence must have at least one step (else "Sequence must
  have at least one step. Click Add to create one, or pick
  a different action type.")
- Each step is validated via `ActionEditorFactory.ValidateAction`
  individually; first failure stops with `"Step N: <error>"`.
  Per-step validation also runs when the step modal saves,
  so this is a defensive double-check (covers the case where
  the step was authored, then a referenced entity was
  deleted between authoring and rule save).

**Edit-mode round-trip:** previously the form gray'd out
the action picker and showed a warning when the rule had a
sequence ("editor lands in 4b-3"). Now it just loads the
steps into _sequenceSteps via the editor's LoadFn and lets
the user edit normally.

**Sub-editor-instance handler binding pattern:** the step-list
buttons need a way to find the active listbox without
storing it as a form field (it's transient sub-editor state
that shouldn't outlive a type-change). Solution: stash the
listbox reference in the sub-editor panel's `Tag` property.
`GetSequenceListBox()` retrieves it via the form's
`_currentActionEditor.Panel.Tag`. If the user has switched
away from the sequence type, the handlers no-op rather than
mutating dead state.

**Visibility of helper types:** `IdItem` and `ActionSubEditor`
became `Public` (from `Friend`) because the factory needs
them from a different file. Same project so `Friend` would
work too, but `Public` is safer if anything ever moves to
a different namespace.

**Out of scope (deliberately not added):**
- Step duplication ("Duplicate this step" button) — Add+Edit
  replicates the data anyway
- Drag-to-reorder — Up/Down buttons match conditions UX
- Live "sequence will take ~N minutes" preview — wait and
  wait_for_player_count have known durations, but
  wait_for_ready and update_installation don't
- Nested-sequence UI in StepEditorForm — serialiser
  supports it; if a power user really wants it they can
  hand-edit the JSON

### Phase 4b-1.5 — NotifyAction transport gap closed

**New file:** `GSM.Manager\UI\ConditionEditorForm.vb`
(~440 lines)

- FixedDialog 640×360, mirrors RuleEditorForm's pattern:
  type combo + sub-editor swap, BuildFn/LoadFn lambdas
  closing over local controls
- Three sub-editor builders, one per condition type
- New `ConditionSubEditor` Friend class with an extra
  `ValidateFn` slot — conditions have varying validation
  needs and centralising in the form's OnSave (like
  RuleEditorForm does for actions) would require an enum
  dispatch on type. Cleaner to colocate validation with
  the sub-editor that knows its own controls.
- Receives lookup data (instances/installations/nodes/tags/
  game IDs) from RuleEditorForm via constructor — avoids
  re-querying the DB on every modal open and keeps display
  names consistent between parent and child forms.

**AllInstancesEmptyCondition sub-editor specifics:**
- Scope picker excludes Instance — single-instance reduces
  to WaitForPlayerCountCondition, no point having two ways
  to express the same thing
- AllInstances scope hides the target row (no per-target
  selection needed)
- Same scope-target-coordination logic as RuleEditorForm.
  Duplicated rather than abstracted because two callsites
  isn't enough to justify a shared helper class — the cost
  of factoring out an inter-form state class would exceed
  the cost of duplication
- The repopulateTarget closure is exposed so LoadFn can
  call it explicitly when loading an existing condition
  whose scope matches the default — same
  SelectedIndexChanged-doesn't-fire-on-no-op-assignment
  gotcha as RuleEditorForm.OnScopeChanged hit in 4b-1

**Modified file:** `GSM.Manager\UI\RuleEditorForm.vb`

- Replaced 70px Conditions placeholder with 150px real
  editor: ConditionMode combo (All / Any), Add/Edit/Remove
  buttons, Up/Down reorder buttons, ListBox with one-line
  summaries
- Form total height grew 800 → 880 to accommodate; Action
  group shifted from y=460 → y=540, buttons from y=700 →
  y=780
- Renamed `_preservedConditions`/`_preservedConditionMode`
  to `_conditions` since they're now editable. The
  conditions list is initialized to `New List(Of ICondition)`
  in the constructor BEFORE InitializeControls runs, so
  button handlers always have a real list to mutate even
  in new-rule mode
- New `SummarizeCondition` helper renders one-line
  descriptions with display-name lookups via
  `LookupInstanceName` / `LookupInstallationName` /
  `LookupNodeName`. Falls back to the raw ID when the
  lookup misses (instance deleted, etc.) — more useful
  than "(deleted)" because it lets the user copy-paste
  to identify what they had selected
- Up/Down buttons earn their place because conditions
  evaluate in order with short-circuit (first failure for
  All-mode, first pass for Any-mode); putting cheap fast-
  failing conditions first is a real performance lever
- Double-click on a list row triggers Edit — same
  affordance as InstallationPanel's instance list

**Persistence note (open):** AutomationRuleEntity does NOT
yet have a `ConditionMode` column. Like `OverlapPolicy`,
the AutomationRule object has it but the entity doesn't,
so it doesn't round-trip through the DB. Form defaults to
All on load. Adding a column for both is a small focused
migration that can land any time — noted here for future
pickup.

**Out of scope for 4b-2 (deferred):**
- "Test condition now" button — would require evaluating a
  condition outside rule context (no firing rule, no
  RuleContext), doable but separate feature
- Plugin-contributed condition types via
  `IConditionProvider` — interface exists in Contracts but
  no plugin uses it yet; SummarizeCondition's fallback
  branch will handle them gracefully when one shows up
- Condition templates / presets ("waiting for empty
  server" as a one-click)

Closed the gap from 4b-1 between the rule editor (which lets
users pick a NotificationDestination) and the runtime
(which previously dispatched via INotificationPlugin lookup).
Also added {Token} substitution for custom messages.

**The architectural decision:** rule-authored notifications
bypass the event-routing fan-out path entirely. They go
direct-to-destination via a new optional capability
interface that transport plugins opt into. Reasoning:

- The Notifications form's destination model is event-routing
  configuration: "when InstanceCrashed fires, send to these
  destinations." That's its job.
- NotifyAction is custom imperative messaging: "at this point
  in this sequence, send this exact prose to this one
  destination." That's a different shape.
- Bolting per-destination addressing onto INotificationPlugin's
  fan-out interface would force-fit two unrelated semantics
  into one method.

**New interface in Contracts:**
```vb
Public Interface IDestinationTargetingPlugin
    Function OwnsDestination(destinationId As String) As Boolean
    Function SendCustomToDestinationAsync(...) As Task(Of Boolean)
End Interface
```
Lives in `GSM.Contracts\INotificationPlugin.vb` alongside
the existing notification interfaces. Plugins opt in by
implementing it; plugins that don't are still valid
INotificationPlugins but won't appear in NotifyAction
dispatch. Currently only DiscordWebhookPlugin implements
it. Future transports (Slack, Telegram, email) will add
their own implementations.

**Field rename with on-disk back-compat:**
`NotifyAction.NotificationPluginId` → `DestinationId`. The
property is decorated with
`<JsonPropertyName("notificationPluginId")>` so it
serialises into the same JSON key as before. Any rules
saved before the rename load cleanly without a migration;
new saves write the same key. The codebase reads as the
new name, the storage format reads as the old name.

This required `Imports System.Text.Json.Serialization`
at the top of `IAutomationRule.vb` (Contracts).

**Dispatch resolution in NotificationService:**
New method `SendToDestinationAsync(destinationId, message,
severity, tokens)` iterates registered plugins, asks each
`IDestinationTargetingPlugin` whether it `OwnsDestination`,
and the first one to claim ownership handles dispatch.
No central registry of which plugin owns which destination
— plugins answer for themselves, which keeps NotificationService
free of transport-specific knowledge.

The old `SendSimpleAsync(pluginId, ...)` is marked
DEPRECATED in its summary but kept callable. No current
code path uses it (RuleContextImpl.SendNotification was
the only caller and it now routes through
SendToDestinationAsync). Will be removed once we're
confident no plugin-level callers remain.

**Token substitution:**
`NotificationService.SubstituteTokens(message, tokens)` is
a public Shared method that resolves `{Token}` placeholders
from a `NotificationTokens` bundle. Single regex pass over
the message string, MatchEvaluator-based so unknown tokens
stay literal in output (visible to user, easy to fix
rather than silently disappearing).

Supported tokens:
```
{RuleName}         {InstanceId} / {InstanceName}
{InstallationId}   {InstallationName}
{NodeId}           {NodeName}
{GameId}           {Time}              {Date}
```
The rule editor's Notify sub-editor shows the full list
as an italic gray help line below the Severity field.

Tokens are resolved by RuleContextImpl.BuildTokensFromContext
at fire time. For Instance-scoped rules, walks up
Instance → Installation → Node so all four levels'
names are available. For multi-instance scopes (Installation,
Node, InstanceSet, AllInstances) populates only the levels
that make sense (e.g. Node-scoped rules don't have a single
InstanceName).

Lookup failures (instance deleted between rule arming and
firing, etc.) are non-fatal: the corresponding token
substitutes as empty string and the notification still goes
out. Logged at Warning level for diagnosability.

**Visibility profile and templates: NOT applied to custom
messages.** The destination's VisibilityProfile is for
redacting structured event tokens (IPs, paths) from auto-
generated event notifications. A user-authored message is
literal prose; the author wrote it, presumably means it.
Templates are similarly skipped — templates transform
structured event data into prose, but custom messages are
already prose. The destination's `EventType = Custom`
context path renders the message as-is.

**Files modified:**
- `GSM.Contracts\IAutomationRule.vb` —
  `JsonPropertyName` import, `NotifyAction` field rename,
  `IRuleContext.SendNotification` parameter rename
- `GSM.Contracts\INotificationPlugin.vb` —
  `IDestinationTargetingPlugin` interface added
- `GSM.Manager\Core\DiscordWebhookPlugin.vb` —
  implements `IDestinationTargetingPlugin`,
  `OwnsDestination` + `SendCustomToDestinationAsync`
  methods (~100 lines)
- `GSM.Manager\Core\NotificationService.vb` —
  `SendToDestinationAsync` + `SubstituteTokens` Shared
  helper (~120 lines), `SendSimpleAsync` marked deprecated
- `GSM.Manager\Core\AutomationEngine.vb` —
  `Imports GSM.Notification`, `RuleContextImpl.SendNotification`
  rewired to call `SendToDestinationAsync`,
  `BuildTokensFromContext` helper (~110 lines)
- `GSM.Manager\UI\RuleEditorForm.vb` — removed orange
  warning label, cleaned up the hidden-overlay label hack
  from 4b-1, added token reference help text, updated all
  field references from `NotificationPluginId` to
  `DestinationId`

**Out of scope for 4b-1.5 (deferred):**
- Multi-destination notifications (one rule sends to many
  destinations). Could be a separate `BroadcastToTagAction`
  or a multi-select destination picker on `NotifyAction`.
- Reusing destination templates for custom messages —
  arguably nice but a different feature.
- Test/preview button in the rule editor that fires a
  message immediately to confirm wiring works.
- Removing the deprecated `SendSimpleAsync` and the legacy
  Plugin model entirely. Need confidence no caller paths
  remain first.

### NotifyAction transport gap (open, deferred)

During 4b-1 implementation, surfaced a real architectural
mismatch in how the Notify action targets recipients.

**The two-system landscape:**

- **System A — "Plugin" model (legacy):**
  - `NotificationPluginEntity` table stores `INotificationPlugin`
    registrations (Discord bot etc.) keyed by `PluginId`.
  - `NotificationService._plugins` holds the live plugin
    instances.
  - `SendSimpleAsync(pluginId, ...)` looks up by PluginId
    and dispatches via `plugin.SendNotificationAsync`.
  - `NotifyAction.NotificationPluginId` field stores a PluginId.

- **System B — "Destination" model (current):**
  - `NotificationDestinationEntity` table stores per-Discord-
    webhook destinations with scoping, visibility profiles,
    template overrides.
  - The Notifications form (rewritten at some point) manages
    these. Each destination has `Enabled`, `TransportKind`,
    `TransportConfigJson`, `EnabledEventTypesJson`, etc.
  - The emitter/broadcast path (`NotificationEmitter.Emitted`
    → `BroadcastAsync` → `plugin.SendNotificationAsync`) uses
    these destinations indirectly: each plugin reads them at
    send time and routes accordingly.

**The gap:** automatic event-driven notifications (server
started, crashed, etc.) work because they go via the
emitter/broadcast path which respects destinations. But
rule-action-driven notifications (`NotifyAction` from a
user-authored rule) go via
`SendSimpleAsync(NotificationPluginId, ...)` which looks
up by *PluginId*. Users author rules against destinations
(what they see in the Notifications form) but the runtime
can't dispatch to a destination ID.

**Why these systems are NOT redundant:** the Notifications
form is *declarative event routing* — "server crashed”
automatically goes to these destinations. NotifyAction is
*imperative custom messaging* — a rule that sends "realm
update in 5 minutes" at a specific point in a sequence,
with a custom message that no event type covers.

**Phase 4b-1 partial fix (this round):**

- `RuleEditorForm` now reads from `NotificationDestinations`
  (filtered to `Enabled = True`) so users see what they
  actually configured
- The action's `NotificationPluginId` field stores the
  selected `DestinationId` (field name kept for serialiser
  back-compat; rename deferred)
- An inline warning label in the Notify sub-editor explains
  that rules will save but the runtime dispatch won't fire
  until the transport refactor lands
- Validation messages updated to say "destination" not
  "plugin"

**Phase 4b-1.5 fix plan (deferred to its own round):**

1. Rename `NotifyAction.NotificationPluginId` →
   `DestinationId` (with `[JsonPropertyName("NotificationPluginId")]`
   on the property, OR a dual-read in the serialiser, so
   any rules already saved with the old field name still
   load).
2. Add `IRuleContext.SendCustomNotification(destinationId,
   message, severity)` distinct from the existing
   `SendNotification(pluginId, ...)`. Or repurpose
   `SendNotification` and update both callsites.
3. Implement the new context method in `RuleContextImpl` to
   resolve the `NotificationDestinationEntity`, look up its
   `TransportKind`, and dispatch directly via that
   transport's send path (bypassing the per-plugin
   broadcast logic which is event-type-driven).
4. Probably means a new helper on `NotificationService` like
   `SendToDestinationAsync(destinationId, NotificationContext)`
   that the rule context calls.

**Why not done in 4b-1:** scope creep. 4b-1's job was "build
the form." The transport refactor is its own design
conversation — e.g., should NotifyAction support multiple
destinations? Should it use the same template system as
event-driven destinations or always send a literal message?
Better to do deliberately than rush.

**Resolved in 4b-1.5** — see "Phase 4b-1.5 — NotifyAction
transport gap closed" section above for the full
resolution. This section retained for historical context.

### Node abuse prevention / hardened auth

**Threat model**: the node's listen port is typically internet-exposed
(directly or via port forwarding). Bearer-token auth alone is insufficient
against attackers who:
- brute-force the AuthToken via repeated requests
- mount timing attacks against `String.Equals`
- flood `/api/version` (intentionally unauthenticated for connectivity tests)
- DoS legitimate traffic via giant request bodies or connection floods

**Three-layer defense** lives in `GSM.Node\Security\SecurityServices.vb`:

1. **`RequestRateTracker`** — per-IP global request rate limit on a
   sliding 1-minute window. Default 600 req/min/IP. Catches
   `/api/version` flooding and post-auth API hammering. Returns 429
   with `Retry-After: 60`.

2. **`AuthFailureTracker`** — per-IP failed-auth lockout. Default
   thresholds: 10 failures in 5 minutes triggers a 15-minute lockout.
   Successful auth clears the IP's failure history (does NOT clear an
   active lockout — the existing penalty rides out). Returns 429 with
   `Retry-After: <seconds remaining>`.

3. **`SecurityHelpers.FixedTimeStringEquals`** — constant-time token
   comparison. Both inputs are SHA-256 hashed first so
   `CryptographicOperations.FixedTimeEquals` can compare fixed-length
   digests; original lengths are not leaked through the comparison.

Plus a 250ms delay on every failed auth (slows brute force well below
the lockout-detection threshold) and a generic `401 Unauthorized` body
with no distinction between missing header vs invalid token.

**Middleware order** (`AuthAndRateLimitMiddleware` in `NodeProgram.vb`),
cheapest rejection first:

1. Rate-limit check → 429
2. Lockout check → 429 with remaining-seconds Retry-After
3. `/api/version` skip → pass through unauthenticated
4. Bearer token check (constant-time)
   - on failure: record failure, sleep `AuthFailureDelayMs`, return
     generic 401
   - on success: reset IP failure history, call `nextDelegate`

The inline `app.Use(Async Function(...))` lambda was extracted to a
named `Private Async Function AuthAndRateLimitMiddleware(...)` and
wired with `app.Use(AddressOf AuthAndRateLimitMiddleware)` per the
VB.Net async-lambda guidance.

**Kestrel hardening** in `ConfigureKestrel`:

- `AddServerHeader = False` — don't broadcast Kestrel/version in
  responses
- `Limits.MaxRequestBodySize = 4MB` (down from 30MB default)
- `Limits.MaxConcurrentConnections = 100`
- `Limits.RequestHeadersTimeout = 30s`

**Configuration** — new `Security` section in `nodesettings.json`:

```json
"Security": {
    "MaxFailedAttempts": 10,
    "FailureWindowMinutes": 5,
    "LockoutMinutes": 15,
    "AuthFailureDelayMs": 250,
    "RequestsPerMinutePerIp": 600,
    "MaxRequestBodyBytes": 4194304,
    "MaxConcurrentConnections": 100
}
```

Bound to a `SecurityConfiguration` POCO via
`builder.Configuration.GetSection("Security").Bind(secConfig)` and
registered as a singleton alongside the trackers.

**Operational notes**:

- All tracker state is **in-memory only**. Node restart clears all
  lockouts and rate counters — use a restart as the emergency reset
  if a legitimate operator's IP gets locked out (e.g. from an
  AuthToken mismatch during testing).
- IP source is `context.Connection.RemoteIpAddress` — the direct TCP
  peer. If the node is later placed behind a reverse proxy (nginx,
  Cloudflare, IIS ARR), all traffic appears to come from one IP and
  legitimate Manager traffic gets locked out alongside attackers. In
  that scenario, add `app.UseForwardedHeaders` and configure
  `KnownProxies`/`KnownNetworks`.
- The `/api/auth` endpoint also uses `FixedTimeStringEquals` (hygiene),
  but is reachable only after the middleware has already validated
  the Bearer token, so it can't be brute-forced from outside.
- Both trackers `Implements IDisposable` and own a 5-minute
  `Timer`-based cleanup that prunes stale entries to keep memory
  bounded under churn (botnet scans, transient client IPs).
- No new NuGet packages — uses `System.Security.Cryptography` and
  `System.Collections.Concurrent`, both built-in to .NET 8.

**Files added**:

- `GSM.Node\Security\SecurityServices.vb` — `SecurityConfiguration`,
  `SecurityHelpers`, `AuthFailureTracker`, `RequestRateTracker`

**Files modified**:

- `GSM.Node\NodeProgram.vb` — security service registration, Kestrel
  hardening, named `AuthAndRateLimitMiddleware` replacing the inline
  auth lambda
- `GSM.Node\nodesettings.json` — new `Security` section
- `GSM.Node\Endpoints\NodeEndpoints.vb` — `Imports GSM.Node.Security`;
  `/api/auth` uses `FixedTimeStringEquals` instead of `String.Equals`

**Sanity-check from another machine** (with a 36-char base64 token):

- `curl -i http://node:8765/api/status` → `401`, body `{"error":"Unauthorized"}`
- 11x bad-token loop → 10th gets 401, 11th onward gets 429 with
  `Retry-After: 900`
- `curl -i http://node:8765/api/version` → still 200 (intentionally public)
- 601 rapid-fire calls to anything from one IP → 429 from the rate
  tracker on the 601st

### Phase 5 (future) — Manual restart coordination + Shift override

- Route Restart buttons + menu items through coordinator
- `Control.ModifierKeys.HasFlag(Keys.Shift)` at click time = force,
  bypass queue
- Tooltip on Restart button: "Shift+click to bypass restart queue"
- Grey-out restart buttons while instance is in a coordinated
  restart (UX feedback for the queue state)

### Automation refactor — file map (cumulative through Phase 4a closeout)

| Layer | File | New types added across automation refactor |
|---|---|---|
| Contracts | IGamePlugin.vb | ReadySignalKind, ReadySignal, IReadySignalProvider |
| Contracts | IAutomationRule.vb | WaitForReadySignalAction, CoordinatedRestartAction (with skip-when-not-Running guard); AcquireRestartSlot/ReleaseRestartSlot on IRuleContext + RuleContext |
| Manager Core | AutomationRuleSerializer.vb | Polymorphic JSON for ITrigger/ICondition/IAction |
| Manager Core | RestartCoordinator.vb | Singleton with semaphores + TCS-based ready-signal waits |
| Manager Core | RestartRuleMaterializer.vb | Materialize + IsSimpleRestartRule + ExtractCronFromRule |
| Manager Core | AutomationEngine.vb | RuleContextImpl overrides + self-starting ReloadRules |
| Manager Core | InstanceManager.vb | `_restartCoordinator` field + AttachRestartCoordinator + TileLoaded notification |
| Manager Root | ManagerProgram.vb | DI registration for RestartCoordinator + bidirectional Attach calls + engine.Start()/Stop() |
| Manager Data | GsmDbContext.vb | Entity fields: MaxConcurrentRestarts (Node, Installation), RestartEnabled/RestartCron/RestartRuleId (Instance), SortOrder (Instance); NextSortOrder extension |
| Manager UI | MainForm.vb | _suppressTreeAfterSelect flag; tree state preservation; non-modal AutomationRulesForm singleton; delete-instance cascade |
| Manager UI | UiPanels.vb | InstallationPanel reorder UI (Up/Down + # column); InstancePanel state-driven buttons |
| Manager UI | RemainingForms.vb | EditInstanceForm restart section (cron, presets, stagger, propagation radios, enable-on-all); AutomationRulesForm execution history details extraction; ApplyMinuteOffsetToCron helper |
| Plugins | LastOasisPlugin.vb | Implements IReadySignalProvider (TileLoaded kind, 300s timeout) |

---

## PHASE 4C — Configuration UI, saves, and file generation

Multi-phase rework of how users interact with the runtime files an
instance reads (server-settings.json, save files, map-gen-settings).
End state: a Factorio instance has Saves, Generate Map, and Server
Settings tabs in its panel; a save file is a one-click upload, a new
map is a one-click generate, and editing the server name doesn't
require SSH'ing to the node.

Four opt-in plugin interfaces drive everything visible. Plugins that
implement them get the new tabs; plugins that don't (Last Oasis,
for now) are completely unaffected — same three-tab layout it had
before the phase started.

### Design decisions locked upfront

Full rationale lives in `Phase4c_Plan.md` (D1–D6). One-line summary
for each:

- **D1 — file is truth at view/edit time.** No DB caching of
  runtime config files; manager fetches fresh from the node on
  open, writes back on save. Out-of-band edits survive.
- **D2 — saves and runtime configs are install-scoped.** Live in
  `<install>/saves/`, `<install>/server-settings.json`. Per-
  instance scoping reserved via `{InstanceId}` token in path.
- **D3 — map gen is a sibling tab to Saves, not modal.** "Generate
  New..." button on Saves opens a tab; user can monitor logs or
  edit other config while the operation runs.
- **D4 — visibility checkboxes raw, not synthesised.** Original
  spec called for an "auth method" radio; shipped form is flat
  with `[Section]` description prefixes.
- **D5 — hardcoded presets, not data-driven.** 7 presets ship as
  string constants in `FactorioPlugin.vb`. Drift risk documented;
  schema-driven custom presets are a v2 follow-on.
- **D6 — stream uploads, no cap.** Request body streamed to disk
  via `CopyToAsync`. 100MB+ Factorio saves work without buffering.

### Phase 4c-1 — Node-side file operations

Foundation for everything else. New plugin interface declares which
directories on the install are user-manageable; new node endpoints
expose CRUD against those directories.

**New contracts in `GSM.Contracts\IGamePlugin.vb`:**

- `IManagedDirectoriesProvider` opt-in interface with one method:
  `GetManagedDirectories(config) As IReadOnlyList(Of ManagedDirectory)`.
- `ManagedDirectory` data class: `RelativePath`, `DisplayName`,
  `Permissions As DirPermissions`, `AllowedExtensions As List(Of String)`.
- `DirPermissions` enum (Flags): `Read | Write | Delete`.
- `RelativePath` may contain the literal token `{InstanceId}`,
  which the Manager substitutes before sending to the node —
  reserved for future multi-instance-per-installation games.

**New wire DTO in `GSM.Contracts\NodeApiContract.vb`:**

- `FileEntry` (RelativePath, SizeBytes, ModifiedUtc) — returned
  from list and upload endpoints.

**New node endpoints (all under `/api/instances/{id}/files`):**

- `GET ?path=...&allowedRoots=...&allowedExtensions=...` — list
- `GET /download?...` — streamed response body
- `POST /upload?...` — streamed request body, returns `FileEntry`
- `DELETE ?...` — single-file delete (idempotent, 404 → returns false rather than error)
- `POST /rename?...&newPath=...` — atomic rename
- `POST /copy?...&newPath=...` — file copy

Validation runs server-side on every request and rejects:

- Paths containing `..` segments (after normalisation)
- Paths whose resolved relative-to-install location isn't under
  one of the request's `allowedRoots` (equality OR `StartsWith`
  with directory separator)
- Paths whose extension isn't in `allowedExtensions` (when the
  list is non-empty)

The `allowedRoots` and `allowedExtensions` are sent by the Manager
on every call — the node doesn't store the plugin's declarations.
This keeps the security boundary explicit at the wire level: even
if the Manager is compromised it can't widen access beyond what
the plugin's declaration covers, because the Manager-side wrapper
derives both from the plugin's `ManagedDirectory` list.

**Streaming uploads.** The upload endpoint disables ASP.NET Core's
default form-options size limit and reads `Request.Body` directly
via `CopyToAsync` to a `FileStream`. No buffering. A 100MB Factorio
save uploads with constant memory on the node. Manager-side mirror:
`HttpClient.PostAsync(StreamContent(fileStream))` with a
one-shot `HttpClient` whose `Timeout = InfiniteTimeSpan` (manager
passes its own `CancellationToken` for cancel/abort).

**Manager-side wrappers** in `GSM.Manager\Core\NodeHttpClient.vb`
plus `INodeClient` interface members: `ListFilesAsync`,
`DownloadFileAsync` (caller-provided destination Stream),
`UploadFileAsync` (caller-provided source Stream),
`DeleteFileAsync`, `RenameFileAsync`, `CopyFileAsync`. Each
forwards `installPath`, `path`, `allowedRoots`, and
`allowedExtensions` verbatim to the node — the wrapper does
zero validation of its own.

### Phase 4c-2 — Saves UI + ManagedFilePicker

First user-visible delivery. Lists files on the node, picks saves
from a dropdown, validates configs before launch.

**Scope narrowed from the original plan.** Phase4c_Plan.md called
for a full `StructuredConfigSchema` with sections, nested groups,
`VisibleWhen` expressions, and StringList/IntegerList field types.
What shipped is a single new `ConfigFieldType.ManagedFilePicker`
value plus a 3-arg overload on `SchemaFormBuilder.Build` that
accepts a file-list provider. Reasoning: the flat
`ConfigFieldDescriptor` schema turned out to cover every use case
4c needed (save selection, server-settings editing) without
introducing a parallel system. Section headers / nested groups
stayed in the v2 follow-on bin.

**New file `GSM.Manager\UI\ManagedFilesPanel.vb`** — the file
management UserControl. ListView with one row per file (name,
size, modified time), button column with Upload / Download /
Delete / Rename / Copy. Resolves the node client + install path
at the start of every operation rather than caching, so a
node-config edit takes effect on the next click without panel
rebuild.

**New `ConfigFieldType.ManagedFilePicker`** field type plus two
descriptor properties: `ManagedDirectoryRef` (which directory
to list) and (existing) `IsRequired`. `SchemaFormBuilder` renders
it as a `ComboBox` with `DropDownStyle = DropDown` (free-form
text allowed) and `AutoCompleteMode = SuggestAppend` so typing
narrows the list. Free-form is intentional — a user can type
the name of a save they're about to upload, or a save listed by
an SCP they did out of band. The `ValueExtractor` reads the
combo's `.Text` (not `.SelectedItem`).

**`SchemaFormBuilder.Build` grew a 3-arg overload** taking a
`Func(Of String, Task(Of IReadOnlyList(Of String)))` file-list
provider. The form-build loop calls it once per
ManagedFilePicker field on a background thread and re-marshals
back to populate the combo. Doesn't block form construction —
the combo is fully usable for free-text entry while the listing
is in flight; only the dropdown items arrive late. The 2-arg
overload still works for read-only Configuration tabs that don't
have a node connection.

**Async-population gotcha:** the lambda that calls the provider
and fills the combo can't be a multi-line async lambda —
VB.Net infers `Task(Of Object)` and complains about "doesn't
return value on all paths". Extracted to a named
`Private Async Function PopulateManagedFilePickerAsync(...)
As Task` per the existing gotcha-table guidance.

**`InstancePanel.BuildManagedFilesTabs`** in `UiPanels.vb` — builds
one tab per declared managed directory between Configuration and
Chat, so display order is `Overview | Configuration | [managed
dirs...] | Chat`. Uses `_tabs.TabPages.IndexOf(_chatTab)` as
insertion anchor. No-op when the plugin doesn't implement
`IManagedDirectoriesProvider`. `{InstanceId}` token substitution
happens here on the manager side per the contract — plugins
return literal tokens and never see the substituted form.

**Pre-flight `ValidateConfig` hook on instance start.** Existing
`IGamePlugin.ValidateConfig(config)` method that previously had
no caller now runs in `InstancePanel.OnStartInstance` BEFORE
`InstanceManager.StartInstanceAsync`. Returned warnings surface
as a warn-and-confirm `MessageBox` ("Start anyway?"); user can
click through. The merge logic builds a case-insensitive dict
from install ConfigJson + instance ConfigJson (instance overrides
install) so the plugin sees the same merged view it sees at
runtime. Failures in the validation lookup itself fall through
to a normal start — we don't want a transient DB error to brick
the Start button. Canonical use case: Factorio with
`UseLatestSave = false` and no `SaveFile` set — the engine
crashes immediately with `"File save.zip does not exist"`, which
is impenetrable to a user; the warning explains it in one line.

**Factorio plugin updates:** `SaveFile` field on the instance
config schema is now `ManagedFilePicker` with
`ManagedDirectoryRef = "saves"`. `GetManagedDirectories` returns
one entry for `saves/` with `Read|Write|Delete` permissions and
`AllowedExtensions = {".zip"}`. `ValidateConfig` returns a
warning when neither `SaveFile` nor `UseLatestSave=true` is set.

### Phase 4c-3 — Generic file generation

Generalised version of what the plan called "Phase 4c-5 map
generation." Schema-driven, plugin-defined, runs against an
instance's install dir for any one-off file-producing operation.
Map generation is the first — and so far only — use case, but
the contract carries no map-specific assumptions.

**Renamed during implementation:** the original
`IMapGenerationProvider` interface became `IFileGenerationProvider`
before shipping, when it became clear the contract shape
(plugin's schema + plugin's step-list) applies generically. Wire
DTOs (`GenerateMapRequest`, `GenerateMapResponse`, endpoint URL
`/api/instances/{id}/generate-map`) kept their original names
for back-compat with already-deployed nodes — a `NAMING NOTE`
comment block in `NodeApiContract.vb` explains. Read those names
as "GenerateFile."

**New contracts in `GSM.Contracts\IGamePlugin.vb`:**

- `IFileGenerationProvider` opt-in interface, five methods:
  `GetTargetDirectoryRef()` — which managed directory the output
  belongs in (used by `ManagedFilesPanel` to decide whether to
  show the "Generate New..." button on its tab);
  `GetButtonLabel()` / `GetTabTitle()` — user-facing strings;
  `GetGenerationSchema(config)` — returns a flat
  `ConfigFieldDescriptor` list rendered by `SchemaFormBuilder`;
  `BuildGenerationSteps(values, config)` — returns a
  `GenerationStepBundle` from the user's filled-in form values.
- `GenerationStepBundle` data class: `Steps As List(Of InstallStep)`
  (only `WriteFileStep` and `RunProcessStep` are currently
  supported), `ExpectedOutputRelativePath`, `TimeoutSeconds`.
- The plugin's `BuildGenerationSteps` is allowed to throw on
  validation failure ("Save name is required"); the panel
  surfaces the message without firing the request.

**New wire DTOs in `GSM.Contracts\NodeApiContract.vb`:**

- `GenerateMapRequest` — `InstallPath`, `Steps As List(Of InstallStep)`,
  `ExpectedOutputRelativePath`, `TimeoutSeconds`.
- `GenerateMapResponse` — `Success`, `OutputRelativePath`,
  `OutputSizeBytes`, `FailedStepIndex`, `ErrorMessage`,
  `Output` (captured stdout truncated to 16KB).

**New node endpoint** `POST /api/instances/{id}/generate-map` —
synchronous, blocks until the steps complete or `TimeoutSeconds`
elapses. Validates that every step is one of the supported types
(rejects `SteamCmdStep`, `DownloadFileStep`, etc. with 400). Runs
them sequentially via the existing install-runner step
mechanics, then verifies `ExpectedOutputRelativePath` exists on
disk before returning success. Manager-side wrapper
`INodeClient.GenerateMapAsync` uses a one-shot
`HttpClient(Timeout=InfiniteTimeSpan)` with a caller-supplied
`CancellationToken` since map generation can run for minutes on
large worlds.

**Why a separate endpoint** (not just the install runner): we
don't want a half-failed generation cluttering install
operation history; the supported step types are a strict
subset; the operation runs against an existing install so
doesn't need credential handling or install lifecycle states.

**New file `GSM.Manager\UI\FileGenerationPanel.vb`** — generic
shell. Renders the plugin's `GetGenerationSchema()` via
`SchemaFormBuilder`, calls `BuildGenerationSteps()` on Generate,
posts to the node, displays progress and completion state.
Replaces the earlier `MapGenerationPanel.vb` (which now contains
an empty namespace stub kept in tree for diff-clarity; safe to
delete on next pass).

**`ManagedFilesPanel` integration:** the previous
`HasMapGenerationProvider` boolean was replaced with
`ResolveFileGenerationInfo()` which returns a `FileGenInfo`
bundle (provider + button label + tab title + target dir ref).
The "Generate New..." button only appears when
`info.GetTargetDirectoryRef()` matches the panel's directory.
Clicking it opens a sibling `FileGenerationPanel` tab; the
user can monitor the generation while still browsing the file
list.

**Factorio plugin updates** — implements
`IFileGenerationProvider` with:

- 7 hardcoded presets in `BuiltinPresets()`: Default, Death
  World, Rail World, Ribbon World, Rich Resources, Lakes,
  Island. Each backed by a `*Json()` method returning the
  corresponding `map-gen-settings.json` blob as a VB string
  constant. Drift risk vs. Factorio's in-engine presets
  documented in source.
- Schema: `Preset` (Enum, populated from preset display names),
  `SaveName` (Text, required), `Seed` (Text, optional, uint32
  validated locally before request).
- `BuildGenerationSteps` writes the preset JSON to a per-
  generation `map-gen-settings-{timestamp}.json` (so concurrent
  generations on the same install can't stomp each other),
  then runs `factorio.exe --create saves/<name>.zip
  --map-gen-settings <path> [--map-gen-seed <seed>]`. Filename
  normalised: leading paths stripped, `.zip` extension
  auto-appended.

**Deferred to v2:**

- Schema-driven custom presets (every map-gen-settings parameter
  exposed as form fields under `GetGenerationSchema`). Hardcoded
  presets stay valuable as starting points; this augments.
- Map exchange string import (D5 v2 note in the plan).
- Factorio scenarios. Scenarios use different CLI semantics
  (`--start-server-load-scenario` at runtime, not `--create`),
  and the documented behaviour of arguments like
  `--map2scenario` is unclear. Considered and shelved during the
  preset round.

### Phase 4c-4 — Structured config file editor

Last user-visible piece. Lets a plugin expose a known config
file (Factorio's `server-settings.json` is the canonical case)
as a structured form rather than raw text. File-as-truth per D1:
Manager fetches fresh from the node on tab open, writes back
on Save, never caches in the DB.

**Originally specced as Phase 4c-3 "Server config editing" in
the plan.** Renumbered during implementation — file ops (4c-1)
and saves UI (4c-2) needed to be solid first since the editor
rides on top of both. The original "Phase 4c-3 = server config"
numbering survives in some commit messages from earlier in the
phase.

**New contracts in `GSM.Contracts\IGamePlugin.vb`:**

- `IInstanceFileEditorProvider` opt-in interface with three
  methods:
  - `GetInstanceFileEditors(config) As IReadOnlyList(Of InstanceFileEditor)`
    — plugin returns one entry per file it can edit. Cheap;
    invoked once when the InstancePanel builds its tabs.
  - `ReadFileToValues(editorKey, fileText) As Dictionary(Of String, String)`
    — plugin parses the on-disk content into a flat values
    dict the schema form can render. Empty/null `fileText` is
    handled by returning an empty dict; schema defaults take
    over for missing keys.
  - `WriteValuesToFile(editorKey, values, existingText) As String`
    — plugin builds the new file text from form values.
    `existingText` is the verbatim file content last read;
    plugin parses it, updates schema-managed keys, re-serialises.
    **Unknown top-level fields the user added by hand outside
    the schema MUST round-trip unchanged.**
- `InstanceFileEditor` data class: `Key` (plugin-defined stable
  id, used to dispatch in multi-editor plugins), `TabTitle`,
  `RelativePath` (relative to install root; may contain
  `{InstanceId}` for future multi-instance games),
  `Schema As IReadOnlyList(Of ConfigFieldDescriptor)`.

**New file `GSM.Manager\UI\InstanceFileEditorPanel.vb`** —
generic shell. Header label, path label, scrollable form host,
bottom strip with Save / Reload / status. Logic:

- **On open** (`LoadAsync`): downloads the file via
  `INodeClient.DownloadFileAsync`. `allowedRoots` and
  `allowedExtensions` are auto-derived from `RelativePath` —
  for files at the install root (e.g. `server-settings.json`)
  the root is the filename itself (the file endpoint's
  equality check matches just that one file); for files under
  a subdirectory (e.g. `config/world.json`) the root is the
  parent dir. 404 → treats as empty file, renders form with
  schema defaults, status reads "doesn't exist yet — schema
  defaults shown. Save will create the file."
- **On Save** (`SaveClicked`): runs `_schemaResult.ValueExtractor`,
  calls plugin's `WriteValuesToFile(values, _lastDownloadedText)`,
  uploads via `INodeClient.UploadFileAsync(overwrite:=True)`,
  caches new text as `_lastDownloadedText` so a follow-up Save
  without an intervening Reload still has the up-to-date
  "existing" content.
- **On Reload**: confirms via MessageBox.YesNo (Reload is
  destructive of in-progress edits), re-runs LoadAsync.
- 404 detected via `IsNotFound(NodeApiException)` checking
  `InnerException` is `HttpRequestException` with
  `StatusCode = NotFound`. Anything else is a real error.
- `_disposeCts` cancellation-token-source tripped on Dispose so
  in-flight async resumptions bail out before touching disposed
  controls.

**`InstancePanel.BuildEditorTabs`** in `UiPanels.vb` — mirrors
`BuildManagedFilesTabs`. Resolves the plugin's
`IInstanceFileEditorProvider`, builds a merged install+instance
`InstanceConfig` (case-insensitive dict, instance overlays
install — same merge logic as `BuildPreFlightValidationWarnings`)
so the plugin sees the same merged view it sees at start time.
`{InstanceId}` substitution applied. Inserts editor tabs at
`_tabs.TabPages.IndexOf(_chatTab)` BEFORE `BuildManagedFilesTabs`
runs; the managed-files pass then finds Chat shifted by N and
inserts after, giving final order:
`Overview | Configuration | [editor tabs] | [managed dirs] | Chat`.
`TryFindInstall` helper opens a fresh DB scope rather than
holding the caller's scope across tab construction.

**Factorio plugin updates** — implements
`IInstanceFileEditorProvider` with one editor for
`server-settings.json`. Schema is 18 flat fields ordered
identity → visibility → auth → gameplay → saves:

```
Identity:    Name, Description, Tags, MaxPlayers
Visibility:  VisibilityPublic, VisibilityLan
Auth:        Username, Token, GamePassword, RequireUserVerification
Gameplay:    AllowCommands (Enum: true/false/admins-only),
             AutoPause, OnlyAdminsCanPause, AfkAutokickInterval
Saves:       AutosaveInterval, AutosaveSlots,
             AutosaveOnlyOnServer, NonBlockingSaving
```

Descriptions carry `[Section]` prefixes since `SchemaFormBuilder`
doesn't support section headers yet; visual grouping is
communicated via the prefix and field ordering. Adding
section-break support to SchemaFormBuilder is a v2 follow-on.

**JSON handling — `JsonNode`, not `JsonDocument`.** The plugin
imports `System.Text.Json.Nodes`. `JsonDocument` is read-only;
`JsonNode` (specifically `JsonObject` and `JsonArray`) is
mutable and supports the unknown-fields-round-trip requirement.
`ReadFileToValues` parses the file text into a `JsonNode` tree
and pulls each schema field via small typed helpers
(`ReadString` / `ReadInt` / `ReadBool` / `ReadAllowCommands`).
`WriteValuesToFile` parses `existingText` into a `JsonObject`
(starts fresh if missing or malformed), then `Set*` helpers
overwrite only the schema-managed keys via `JsonValue.Create`.
Unknown top-level fields (`segment_size_*`, `max_upload_*`,
anything else) round-trip verbatim because the JsonObject's
other properties are untouched. Output via `ToJsonString` with
`WriteIndented = True` so the file stays human-readable.

**Three Factorio-specific flattenings:**

- `visibility:{public, lan}` nested object → two top-level form
  fields (`VisibilityPublic`, `VisibilityLan`). Reader pulls
  from the nested object; writer reconstructs it, preserving
  any other sub-fields if present (e.g. `steam` on older
  Factorio versions).
- `tags` array → comma-separated text field. Reader does
  `String.Join(", ", tagList)`; writer splits on `,`, trims, and
  builds a fresh `JsonArray`. No new `StringList` field type
  introduced — wasn't worth the contract addition for one use
  case.
- `allow_commands` may legitimately serialise as either a JSON
  string ("admins-only" — modern docs' canonical form) or a
  JSON boolean (`true`/`false` — older form). `ReadAllowCommands`
  tries string first then bool. `SetAllowCommands` writes
  `"admins-only"` as a string but writes `"true"`/`"false"`
  values as actual booleans — Factorio rejects the strings
  `"true"`/`"false"` as invalid for that field.

**Deferred to v2:**

- Section-header support in `SchemaFormBuilder`. New
  `ConfigFieldType.SectionHeader` value, ~30 lines of rendering.
  Would let Factorio drop the `[Section]` description prefixes.
- Per-instance editor scope via `{InstanceId}` token —
  contract supports it, no current plugin uses it.

### Phase 4c file map

| Layer | File | Role |
|---|---|---|
| Contracts | IGamePlugin.vb | `IManagedDirectoriesProvider` + `ManagedDirectory` + `DirPermissions` (4c-1); `IFileGenerationProvider` + `GenerationStepBundle` (4c-3); `IInstanceFileEditorProvider` + `InstanceFileEditor` (4c-4); `ConfigFieldType.ManagedFilePicker` + `ManagedDirectoryRef` property on `ConfigFieldDescriptor` (4c-2) |
| Contracts | NodeApiContract.vb | `FileEntry` DTO (4c-1); file-ops methods on `INodeClient` (4c-1); `GenerateMapRequest` / `GenerateMapResponse` + `GenerateMapAsync` on `INodeClient` (4c-3 — NAMING NOTE in source) |
| Node | Endpoints\FileEndpoints.vb (new) | `/api/instances/{id}/files` CRUD + rename + copy with path validation, root allowlist, extension allowlist, streamed body for upload (4c-1) |
| Node | Endpoints\InstanceEndpoints.vb | `/api/instances/{id}/generate-map` synchronous endpoint with output-existence verification (4c-3) |
| Node | MapGenerationRunner.vb (new) | Sequential `WriteFileStep` + `RunProcessStep` runner with stdout capture and per-step timeout enforcement (4c-3) |
| Manager Core | NodeHttpClient.vb | `INodeClient` file-ops wrappers (4c-1); `GenerateMapAsync` with InfiniteTimeSpan one-shot HttpClient (4c-3) |
| Manager UI | UiPanels.vb | `InstancePanel.BuildManagedFilesTabs` (4c-2); `InstancePanel.BuildEditorTabs` + `TryFindInstall` helper (4c-4); `BuildPreFlightValidationWarnings` for `OnStartInstance` (4c-2); `SchemaFormBuilder.Build` 3-arg overload + `PopulateManagedFilePickerAsync` named helper (4c-2) |
| Manager UI | ManagedFilesPanel.vb (new) | File-list ListView + Upload/Download/Delete/Rename/Copy buttons; `ResolveFileGenerationInfo` integration for the "Generate New..." button (4c-2/4c-3) |
| Manager UI | FileGenerationPanel.vb (new) | Generic schema-driven generation UI hosting `SchemaFormBuilder` (4c-3) |
| Manager UI | InstanceFileEditorPanel.vb (new) | Generic structured file editor: download → plugin parse → schema render → plugin serialise → upload (4c-4) |
| Plugins | FactorioPlugin.vb | Implements `IManagedDirectoriesProvider` (saves/, .zip allowlist, R/W/D); `SaveFile` field as `ManagedFilePicker` (4c-2); `ValidateConfig` warns on missing save selection (4c-2); implements `IFileGenerationProvider` with 7 presets + uint32 seed validation (4c-3); implements `IInstanceFileEditorProvider` for server-settings.json with 18 fields, JsonNode-based parse/serialise preserving unknown fields, allow_commands string-or-bool dual handling (4c-4) |

---

## PHASE 7 — GSM.NodeSetup (companion configuration tool)

Goal: a setup companion that ships next to `GSM.Node.exe` so end users
do not have to hand-edit `nodesettings.json`. Two UIs: a WinForms GUI on
Windows and an interactive console UI on Linux. Both surfaces share one
config schema, one validator set, and one service-installer module.

**This is a standalone project.** No project references, no shared
assemblies, no plugin contracts. The setup tool reads the same JSON the
node reads, but is otherwise independent.

### Project layout

```
GSM.NodeSetup/
├── GSM.NodeSetup.vbproj      multi-targeted: net8.0;net8.0-windows
├── Program.vb                entry point + arg parsing + GUI/CLI dispatch
├── NodeSetupConfig.vb        POCOs mirroring nodesettings.json + load/save
├── ConfigHelpers.vb          token gen, validators, elevation detection
├── ServiceManager.vb         sc.exe (Windows) + systemd unit gen (Linux)
├── ConsoleUi.vb              interactive menu + 5-step wizard (cross-platform)
└── Windows/                  excluded from net8.0 build via Compile Remove
    ├── GuiBootstrap.vb       ApplicationConfiguration.Initialize + Application.Run
    └── MainSetupForm.vb      single TabControl form, all in code
```

### Multi-targeting strategy

```xml
<TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks>

<PropertyGroup Condition="'$(TargetFramework)'=='net8.0-windows'">
  <UseWindowsForms>true</UseWindowsForms>
  <DefineConstants>$(DefineConstants),WINDOWS_GUI=True</DefineConstants>
</PropertyGroup>

<ItemGroup Condition="'$(TargetFramework)'!='net8.0-windows'">
  <Compile Remove="Windows\**\*.vb" />
  <None Include="Windows\**\*.vb" />
</ItemGroup>
```

- Linux build (`net8.0`) compiles only the cross-platform files. The
  `Windows\` folder is removed from the Compile item group entirely.
  No WinForms type ever enters the Linux build, so it runs on a
  bare .NET 8 runtime with no GUI dependencies.
- Windows build (`net8.0-windows`) compiles everything. The
  `WINDOWS_GUI=True` constant is defined for that TFM only, so
  `Program.vb` can `#If WINDOWS_GUI Then ... #End If`-guard the call
  into `Windows.GuiBootstrap.Run`.
- The VB.Net `<DefineConstants>` syntax for boolean constants is
  `NAME=True` joined with commas. Plain `WINDOWS_GUI` (no `=True`)
  also works in newer SDKs, but the `=True` form is unambiguous and
  matches Microsoft's own documentation.

### Mode selection (Program.vb)

| Argument | Behaviour |
|---|---|
| (none) on Windows | GUI |
| (none) on Linux | Console wizard |
| `--cli` / `-c` | Force console |
| `--gui` | Force GUI (errors on Linux build) |
| `--auto-init` | Non-interactive: write fresh config, generate token, print to stdout, exit. For Docker / cloud-init / Ansible. |
| `--config <path>` | Override path to `nodesettings.json`. Default is `AppContext.BaseDirectory\nodesettings.json`. |
| `--help` / `-h` | Usage |

If the GUI bootstrap throws, the tool falls back to console mode
instead of failing — useful when a user double-clicks the exe on a
Windows Server Core install with no graphical session.

### Config round-trip (NodeSetupConfig.vb)

- POCOs mirror the exact shape of `nodesettings.json`: `Node`,
  `Security`, `Logging` sections.
- `LoadOrCreate(path)` returns a populated default when the file is
  missing or empty (first-run state). Throws on a malformed file —
  silent corruption recovery would mask the user's misconfiguration.
- `Save(path, backupExisting)` does an atomic write: write to
  `<path>.tmp`, then `File.Move` with `overwrite:=True` to the real
  path. Optionally copies the existing file to `<path>.bak` first
  (best-effort — backup failure does not block the save).
- `JsonSerializerOptions` uses `PropertyNameCaseInsensitive = True`
  on read (so users editing the JSON by hand can be sloppy with case)
  and `WriteIndented = True` on write (so the saved file stays
  human-editable).
- `NeedsAuthTokenSetup` returns True when the token is missing or
  still the literal `CHANGE_ME_BEFORE_FIRST_RUN` placeholder. Both UIs
  surface this as the "NOT CONFIGURED" status indicator.

### Token generation (ConfigHelpers.vb)

- 36 random bytes from `RandomNumberGenerator.Create()` →
  `Convert.ToBase64String` produces a 48-char token. Matches the
  `openssl rand -base64 36` suggestion from the original reference doc.
- `IsAuthTokenPlaceholder` is case-insensitive on the literal value
  to catch users who typed lowercase.
- `RunningElevated()` returns True when the process can perform
  privileged service ops: `WindowsPrincipal.IsInRole(Administrator)`
  on Windows, `Environment.UserName == "root"` on Linux. Used to
  short-circuit service-install attempts that would otherwise fail
  with `[SC] OpenSCManager FAILED 5` or systemctl permission errors.

### Service installation (ServiceManager.vb)

**Windows path** — shells out to `sc.exe`:
- `sc create <name> binPath= "<exe>" DisplayName= "<name>" start= auto`
- The space after `binPath=` and `start=` is **required**; without it,
  sc.exe creates a service that never starts. (sc.exe parses `KEY=`
  as a separate token from the value.)
- `sc description` is best-effort (failure is non-fatal).
- `sc start` runs after create; if it fails the install is still
  reported as successful with a hint to start manually.
- All `sc` invocations use `ProcessStartInfo.ArgumentList` (NOT
  `Arguments`) so each argument is escaped independently and we avoid
  the same trailing-backslash + escaped-quote root-cause that bit
  SteamCMD.
- `GetWindowsServiceStatus` parses the `STATE` line from `sc query`,
  returning `Running` / `Stopped` / `Starting` / `Stopping` /
  `NotInstalled` / `Unknown`. NotInstalled is detected by the `1060`
  error code in the output.

**Linux path** — generates a `gsmnode.service` systemd unit:
- `Type=simple`, `Restart=on-failure`, `RestartSec=5`,
  `StandardOutput=journal`, `StandardError=journal`.
- `User=` line is included only when the user supplied a value (via
  GUI textbox or CLI prompt). Empty user → admin must edit the unit
  before installing.
- The unit is written to `<output-dir>/gsmnode.service`. The tool
  does NOT shell out to `systemctl` itself — it prints the three-line
  copy/enable/start instruction block for the admin to run as root.
  Reasons: systemctl needs root, sudo non-interactive cannot be
  assumed, distro-specific paths vary, and container-only
  environments may not have systemd at all.
- `GetSystemdStatus` shells out to `systemctl is-active gsmnode` for
  status display. Returns `Running` / `Stopped` / `Failed` /
  `Starting` / `Stopping` / `NotInstalled` / `Unknown`.

### Console UI (ConsoleUi.vb)

- First-run heuristic: if the file does not exist OR the auth token
  is the placeholder, the tool launches straight into the wizard.
  Otherwise it shows the main menu.
- Five-step wizard: identity, storage, operations, authentication,
  review. Defaults shown in `[brackets]`; Enter accepts the default.
- Validator helpers return `Nothing` for valid input, an error
  string for invalid input, or a `"Warning: ..."` / `"Note: ..."`
  prefixed string for non-fatal issues. The prompt loop accepts
  warnings and re-prompts on errors.
- After save, the new auth token is displayed prominently in a
  highlighted block so the user can copy it into the Manager.
- Color via `Console.ForegroundColor`. Honors the `NO_COLOR` env
  var convention and disables color when `Console.IsOutputRedirected`
  is True (don't dump ANSI codes into log files).
- Edit submenu lets users tweak individual fields. Security settings
  are behind a sub-submenu with a banner warning that defaults are
  fine for most setups and a Reset-to-defaults shortcut.

### WinForms GUI (Windows\MainSetupForm.vb)

- Single Form with TabControl: General, Auth Token, Security, Service.
- Built entirely in code — no `.resx` designer file (matches
  `MainForm.vb` in GSM.Manager).
- `TableLayoutPanel` for two-column field grids (label | control)
  with absolute-width label column and percent-fill control column.
- Auth Token tab uses `UseSystemPasswordChar` masking by default
  with a "Show token" checkbox to unmask. "Copy" button writes to
  `Clipboard.SetText` with a 1.2-second "Copied!" feedback flash via
  a one-shot `Timer` (which then disposes itself in its `Tick`
  handler — clean enough for a transient effect).
- Service tab buttons short-circuit with friendly MessageBox dialogs
  when not elevated, instead of letting `sc.exe` print
  `Access is denied`.
- Bottom action bar uses `FlowDirection.RightToLeft` so the buttons
  read Cancel / Save and Exit / Save / Generate Token from right to
  left as added — matches the Windows convention.
- `AcceptButton = SaveButton`, `CancelButton = CancelButton`.

### Files added in this phase

- `GSM.NodeSetup\GSM.NodeSetup.vbproj` — multi-targeted project
- `GSM.NodeSetup\Program.vb` — entry point
- `GSM.NodeSetup\NodeSetupConfig.vb` — config schema + JSON I/O
- `GSM.NodeSetup\ConfigHelpers.vb` — pure functions used by both UIs
- `GSM.NodeSetup\ServiceManager.vb` — service install/uninstall/status
- `GSM.NodeSetup\ConsoleUi.vb` — interactive console UI
- `GSM.NodeSetup\Windows\GuiBootstrap.vb` — WinForms init helper
- `GSM.NodeSetup\Windows\MainSetupForm.vb` — main GUI form
- `GSM.Node\install-service.bat` — fallback for headless deployments
  (was referenced by the vbproj but missing from disk)
- `GSM.Node\uninstall-service.bat` — fallback for headless deployments

### Files modified in this phase

- `PowerGSM.sln` — added GSM.NodeSetup project entry with new
  project GUID `{A1B2C3D4-4444-4444-4444-000000000004}` and
  Debug/Release configuration mappings.

### Build / publish notes

- Build the whole solution as before. The new project produces TWO
  output folders under `bin\Debug\` and `bin\Release\`:
  `net8.0\` (cross-platform) and `net8.0-windows\` (Windows GUI).
- For Windows distribution, publish the `net8.0-windows` TFM:
  `dotnet publish GSM.NodeSetup -c Release -f net8.0-windows -r win-x64 --self-contained`
- For Linux distribution, publish the `net8.0` TFM:
  `dotnet publish GSM.NodeSetup -c Release -f net8.0 -r linux-x64 --self-contained`
- The setup binary belongs next to `GSM.Node.exe` / `GSM.Node` in
  the deployment package — the tool resolves both `nodesettings.json`
  and `GSM.Node.exe` via `AppContext.BaseDirectory`.

### VB.Net gotchas encountered (additions to the table at the top)

| Pattern | Wrong | Right |
|---|---|---|
| Multi-target boolean DefineConstants | `<DefineConstants>WINDOWS_GUI</DefineConstants>` (ambiguous) | `<DefineConstants>$(DefineConstants),WINDOWS_GUI=True</DefineConstants>` (comma-separated, `=True`) |
| Excluding TFM-specific files from SDK auto-discovery | Conditional `<Compile Include>` blocks | `<ItemGroup Condition="..."><Compile Remove="..."/><None Include="..."/></ItemGroup>` so the SDK still tracks the files but doesn't compile them |
| sc.exe binPath parsing | `sc create svc binPath="path" ...` (no space) | `sc create svc binPath= "path" ...` — the space after `=` is required by sc.exe's tokenizer |
| Process invocation with paths containing quotes | `psi.Arguments = "..."` (manual escaping) | `psi.ArgumentList.Add(...)` — each arg escaped independently, sidesteps the trailing-backslash + escaped-quote class of bug entirely |
| **`ApplicationConfiguration.Initialize()` in VB.Net WinForms** | **Calling it directly — BC30451 "is not declared"** | **The C# WinForms SDK source-generates that helper; the VB.Net SDK does NOT. Call the three calls it would have generated directly: `Application.SetHighDpiMode(HighDpiMode.SystemAware)` → `Application.EnableVisualStyles()` → `Application.SetCompatibleTextRenderingDefault(False)`. Skipping them gives a low-DPI, classic-themed, visually broken form on Windows 10 / 11.** |
| **VB.Net case-insensitivity — parameter shadowing type names** | **`Public Sub Save(path As String) ... Path.GetDirectoryName(path)`** | **VB.Net is case-insensitive, so the `path` parameter shadows `System.IO.Path` and the call resolves as `path.GetDirectoryName(...)` → BC30456 "is not a member of String". Rename the parameter (`filePath`) or fully qualify the type (`Global.System.IO.Path.GetDirectoryName(...)`).** |
| **CA1416 platform-compatibility analyzer doesn't follow indirected guards** | **`If RunningOnWindows() Then [Windows-only API call]` → CA1416 warning even though the function returns `OperatingSystem.IsWindows()`** | **Decorate the wrapper with `<SupportedOSPlatformGuard("windows")>` from `System.Runtime.Versioning`. Now the analyzer treats `If RunningOnWindows() Then ...` as a valid platform guard and Windows-only calls inside the block compile cleanly.** |
| **`AutoSize=True, AutoSizeMode=GrowAndShrink` collapses TableLayoutPanel `Absolute` columns** | **`Absolute=220` column on a `GrowAndShrink` panel — column shrinks to the content's natural width (~90px for a short label) instead of staying at 220** | **Drop `AutoSizeMode=GrowAndShrink`, leave just `AutoSize=True`. The column then honors its absolute width, and content in adjacent columns lines up where you'd expect. Affects any control whose horizontal position you've calculated against the column boundary (sibling button rows, downstream label alignments).** |
| **`System.Text.Json` serializes computed read-only properties** | **`Public ReadOnly Property NeedsAuthTokenSetup As Boolean ... Get ... End Property` — ends up in the JSON output as `"NeedsAuthTokenSetup": false` even though it's a computed-from-other-fields property** | **STJ serializes any public readable property by default. For computed/derived properties that should never appear in the file, decorate with `<JsonIgnore>` from `System.Text.Json.Serialization`. The property remains usable in code but the serializer skips it.** |

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

---

## PHASE 5g-1 follow-up — Cross-platform hardening (May 2026)

After Phase 5g-1 shipped, real-world testing against a Linux
node running Last Oasis surfaced a series of platform-specific
and stability bugs that don't fit any single existing phase but
represent durable architectural knowledge. They're collected
here as one phase-follow-up section because they share a
common theme — cross-platform hardening of the spawn / tail /
stream / persist pipeline.

### Linux UE4 file tailing via libc.open

UE4 dedicated servers on Linux hold an advisory `flock(LOCK_EX)`
on their `.log` file (lsof shows `MistServ ... 3uW` — fd 3,
mode u r+w, capital W = write lock on the entire file). .NET
8's `FileStream` consults the advisory lock and refuses to
open even with `FileShare.ReadWrite Or FileShare.Delete`. The
node bypasses this via P/Invoke:

```vb
<DllImport("libc", EntryPoint:="open", SetLastError:=True)>
Private Shared Function LibcOpen(path As String, flags As Integer) As Integer
End Function
```

`ProcessManager.OpenLogFileForTailing(path)` calls `LibcOpen`
with `O_RDONLY` (= 0), wraps the returned fd in
`SafeFileHandle(handle, ownsHandle:=True)`, and constructs
`New FileStream(handle, FileAccess.Read)`. Windows falls
through to the normal FileStream ctor since flock semantics
don't apply. Subsequent reads go through normal stream APIs;
only the open path differs.

### Cross-platform spawn-strategy / file-tailer duality

Linux forces `SpawnStrategy.StdoutCapture` (Strategy A) for
all games via `ResolveStrategy`. The implication is non-
obvious: on a fresh spawn under Strategy A, stdout is the
authoritative log source and the file tailer MUST NOT also
run (produces per-line duplicates). On adoption under
Strategy A, stdout was owned by the previous node process
and is no longer connected — file tailer is the only
option. The asymmetry is gated in `FinalizeStart`:

```vb
If managed.Strategy <> SpawnStrategy.StdoutCapture Then
    StartFileTailers(managed, managed.LogFilePaths)
End If
```

`TryAdoptOne` (adoption path) unconditionally starts the
tailer. Windows Strategy B (HiddenConsoleDirect) and Linux
Factorio Strategy C (NativeTerminal) don't capture stdout for
the log buffer regardless and don't trigger the gate.

### Chat dedup via UE4 timestamp + UNIQUE INDEX

Node-side `EventStore.ProcessLine` extracts the
`[YYYY.MM.DD-HH.MM.SS:fff]` UE4 prefix via
`TryParseUe4Timestamp` and uses the parsed value as the
persisted `timestamp_utc` rather than `DateTime.UtcNow`
from `EmitTailLine`. Lines without a parseable UE4
timestamp (Factorio, plain text) fall back to `UtcNow`.
`chat_messages` has a `ux_chat_dedup` UNIQUE INDEX on
`(instance_id, timestamp_utc, display_name, text)`;
persistence uses `INSERT OR IGNORE`. Makes adoption replay
(`skipResume:=True`) idempotent — the entire ring buffer's
chat lines re-flow through ProcessLine on every adoption,
but each row collides with the already-persisted row by
source timestamp and gets silently dropped.

UE4 timestamp regex note: the seconds-to-millis separator
is `:` not `.` (e.g. `[12.34.56:789]`), which requires a
capture-group split — `DateTime.ParseExact` with format
`yyyy.MM.dd-HH.mm.ss:fff` doesn't work because `:` is
parsed as a time-component separator. Regex captures the
date-seconds half and the millis half separately, then
concatenates with `.` for the final `DateTime.Parse`.

### Atomic SSE subscription (SubscribeAndGetTail)

When a manager subscribes to a node SSE log stream, both
the backfill ("give me the last N lines") and the live-
stream start point ("deliver everything from here on") must
come from one consistent `_writePos` snapshot. Old code
took the buffer's SyncLock twice in sequence — once via
`AddSubscription` setting `LastSequence = _writePos - 1`,
once via `GetTail` — and an `Append` firing between the
two acquisitions placed the new line in BOTH halves.
`InstanceBuffer.SubscribeAndGetTail(subscription, tailCount)`
does both under a single lock: tail returns
`(_writePos - take)..(_writePos - 1)`, live stream starts
at `_writePos`. No overlap, no gap. Legacy two-call entry
points remain with deprecation comments for callers that
don't need both halves.

### Manager-side log stream idempotency

`InstanceManager.StartLogStream` is idempotent under a
private `_logStreamLock` SyncLock. Before installing a new
cts, it `TryRemove`s any existing entry from
`_logStreamCancellations`, calls `Cancel()` + `Dispose()`
on it, and clears the stale `_logParsers` entry. The
orphaned task's existing compare-and-remove in its Finally
block bails correctly when it sees a mismatched cts in the
dict. `Task.Run` is INSIDE the lock so parser registration
in `_logParsers` happens before the streaming task starts —
otherwise the new task could read lines while a previous
parser is still registered.

Two callers race here under normal operation:
`StartInstanceAsync`'s success path and
`BackgroundPollLoopAsync`'s stream-health check. The
background poll runs every 3 seconds and observes whichever
state `_liveStates` has at the moment it reads. Between
`_liveStates(id) = result` (Running) and `StartLogStream(...)`
in the start path, the dict slot is briefly empty — if the
poll's stream-health check runs in that window, it also
calls `StartLogStream`. Pre-fix the dict assignment was a
naked upsert with no cancellation of the orphaned cts, and
both background tasks ran forever in parallel, producing
permanent every-line-doubled output for the rest of the
instance's session. This was the headline bug behind the
user-reported "logs doubling after restart" symptom.

### Manager parser state vs node EventStore

Node-side `EventStore` rules are STATELESS line-by-line
matchers — each rule's regex runs against each line in
isolation, no cross-line state. This is what makes chat
dedup work cleanly across adoption replay: the replay
re-runs ProcessLine against the same lines and produces
the same persistence calls, dropped by `INSERT OR IGNORE`.

Manager-side log parser is DIFFERENT — it has STATEFUL
sequences. The Last Oasis tile-load identity is committed
by a 4-line sequence (`Started hosting tile` → realm_id →
tile_name → tile_id), and the in-memory parser state
threads context across those four lines until all are
seen. On adoption, that sequence can be hours old and has
rotated out of the node SSE ring buffer (4096 lines), so
the manager parser comes up with `CurrentSessionIdentity =
Nothing` and any chat / player-activity rows persisted in
that window would orphan from session context.

Solution: DB-as-source-of-truth fallback.
`InstanceManager.ResolveSessionIdentity(instanceId)` walks
a lookup chain: parser-committed identity (live path,
unchanged) → in-memory cache (`_adoptedSessionIdentities`)
→ SQLite query against `SessionHosts WHERE InstanceId = ?
AND HostedUntilUtc IS NULL ORDER BY HostedFromUtc DESC
LIMIT 1` for the most recent open hosting record →
synthesized `{gameId}:{instanceId}` if nothing matches.
Self-healing: parser commit invalidates the cache, future
lookups bypass the DB. Cache is dropped on instance stop
via `ClearPlayerTracking`.

General pattern: two paths reading/writing the same
logical state at different moments without atomicity — or
with different state-of-truth assumptions — is a recurring
bug shape across the manager-node boundary. Solutions are
either (1) collapse the two ops into one atomic operation
(SubscribeAndGetTail, StartLogStream SyncLock) or (2)
rehydrate from DB-as-source-of-truth lookup (SessionIdentity
fallback). Pick based on which side of the boundary owns
the authoritative state.

### Linux signal isolation via setsid

The kernel routes SIGINT (Ctrl+C) to every process in the
controlling terminal's process group. Game children
spawned by the node default to the same process group, so
Ctrl+C delivered to the node's terminal would also kill
every running game-server child. `ProcessManager.WrapInSetsidIfLinux(psi)`
rewrites `ProcessStartInfo` so children spawn as
`setsid <exe> <args>`, detaching them into a new session
and process group. The node's own Ctrl+C handler still
signals game children explicitly via gsm-broker when an
instance stop is requested; the only thing setsid blocks
is incidental terminal propagation. Idempotent — re-
wrapping a setsid-wrapped psi is detected (FileName ==
setsid + first arg already the original exe) and is a no-op.

### WinForms RichTextBox quirks (additions)

**`MessageBeep` on EM_REPLACESEL against ReadOnly.**
`RichTextBox.AppendText`, `SelectedText = "..."`, and the
trim's `Select() + SelectedText = ""` all funnel through
`EM_REPLACESEL`. On a `ReadOnly = True` rich-edit (class
name `RICHEDIT50W`), Windows calls `MessageBeep` BEFORE
performing the replacement — the append still succeeds,
but every call rings the system bell. Rapid programmatic
appends during a high-throughput log burst produce a
continuous ding cascade. Workaround: bracket the
programmatic-mutation block with `_logTextBox.ReadOnly =
False` and restore in Finally. The existing `WM_SETREDRAW
= 0` window across the same span prevents user input from
reaching the control during the toggle, so the brief
`ReadOnly = False` state is invisible.

**`Lines` property is O(N) on read and full-reparse on
write.** `RichTextBox.Lines.Length` walks the entire
control's text and allocates a fresh `String()` array each
call; `RichTextBox.Lines = newArray` re-parses the
assignment as RTF. NEVER read `.Lines.Length` in a hot
path. Track line count and per-line offsets manually — see
`_logLineCount` and the `_logLineEndAbsoluteOffsets` queue
in InstancePanel for the canonical pattern: monotonic
char-written counter, queue of absolute newline offsets,
trim by dequeuing offsets and computing relative cut via
`cutOffset - _logBaseCharOffset`, then `Select(0, relativeCut)`
+ `SelectedText = ""`.

### UE4 stdout vs stderr mental model

UE4 dedicated servers emit log lines to stdout exclusively.
Stderr carries SteamAPI initialisation noise only
(`[S_API] SteamAPI_Init()`,
`RecordSteamInterfaceCreation (PID N)`) — not a mirror of
stdout. Confirmed empirically via diff test on Linux LO:
2613 stdout lines vs 15 stderr lines during one start
cycle. Node drops stderr in `AttachProcessHandlers` (still
drains the pipe to prevent the kernel buffer from filling
and blocking writes, just doesn't append to the ring
buffer). When investigating a log-doubling symptom, stderr
is NOT the suspect; look elsewhere (SSE subscription
lifecycle, file tailer gating, ring-buffer atomicity).

### UI preference persistence patterns

Two scopes used across `InstancePanel` and
`InstallationPanel`:

**Per-entity** (e.g., Show Logs toggle on InstancePanel,
keyed by InstanceId): class-shared
`ConcurrentDictionary(Of String, Boolean)`. Each user
toggle writes the dict; an `OnLoad` override reads the
saved value and applies it. Guarded by a `_restoringShowLogs`
flag that suppresses the echo write-back in the toggle
handler AND the auto-select-Logs-tab side effect in
`ShowLogsTab`.

**Per-panel-type** (e.g., last-selected tab on both
InstancePanel and InstallationPanel): two separate
`Private Shared` String fields (one per panel class)
storing the last-selected tab's `.Text`.
`SelectedIndexChanged` handler hooked AFTER the initial
tab Add calls in `InitializeControls` — the synthetic
`-1 → 0` event that fires on the first Add would otherwise
pre-write the default tab name. Identity by `.Text` not
index because dynamic tabs (Logs, plugin-supplied managed-
files / editor tabs, Progress tab on InstallationPanel)
shift indices across panels. Tabs that exist on only some
panels fall through cleanly to the default selection when
the saved name doesn't match.

Both patterns share key mechanics: `OnLoad` rather than
the constructor (so `Me.BeginInvoke` works — the handle
isn't created until parenting); a `_restoring...` Boolean
flag to suppress side effects during restore; manager-
restart scope by design (fresh session starts on defaults).
Per-instance vs per-panel-type is a deliberate choice per
preference: Show Logs is per-instance because log-watching
is instance-specific; tab is per-panel-type because the
user wants "compare configurations across instances" to
keep them on Configuration.

The per-panel-type tab persistence removed about 80–90% of
the navigation clicks involved in comparing configurations
or logs across instances during live operation, per
May 2026 user feedback. Worth knowing as a baseline cost-
benefit data point for any future "is this UX feature
worth it" question on similar persistence patterns.

### Host-side prerequisite checks (Phase 5g side-feature)

Lets a plugin declare host-side runtime dependencies its
game needs to launch, so missing prereqs surface as
pre-install notices BEFORE the user spends 15–30 minutes
downloading a depot only to watch the process exit silently
at launch. Motivating case was Conan Exiles — ships no
`_CommonRedist` folder, links against Microsoft VC++ 2015–2022
x64 at runtime, fails with `STATUS_DLL_NOT_FOUND`
(-1073741515) and no log file when the runtime is missing.

**Architecture: "Manager declares, Node detects, Manager
renders."** Plugin names opaque prereq strings via the new
`IPrerequisiteProvider` interface; Manager passes the list
to the node's `/api/system/prerequisites` endpoint; node
owns the catalog of recognised names + detection logic +
display metadata and returns enriched results. Manager
renders the response as Warning-severity notices in
NewInstallationForm. No catalog duplication across the
boundary — adding a new prereq is a node-side change that
requires no plugin-contracts version bump (older nodes
return `Recognized=False` for unknown names and the
Manager silently skips them).

**Plugin contract (`GSM.Contracts/IGamePlugin.vb`).**
New optional interface alongside `IInstallationNoticeProvider`:

```vb
Public Interface IPrerequisiteProvider
    Function GetRequiredPrerequisites() As IReadOnlyList(Of String)
End Interface
```

Names are lowercase kebab-case, version-suffixed when the
runtime has multiple incompatible major versions. Initial
catalog entry: `vcredist-2015-2022-x64`. Plugins that don't
implement the interface skip the prereq check entirely.

**Wire DTOs (`GSM.Contracts/NodeApiContract.vb`).**
`PrerequisiteCheckResult` carries: `Name`, `Recognized`,
`Installed`, `Version`, `DisplayName`, `DownloadUrl`,
`Instructions`. `PrerequisiteCheckResponse` wraps a
`List(Of PrerequisiteCheckResult)` parallel to the request's
names list. `INodeClient.CheckPrerequisitesAsync(names,
cancellation)` is the typed wrapper.

**Node side (`GSM.Node/PrerequisiteProbe.vb` + endpoint in
`NodeEndpoints.vb`).** Static `Private Shared ReadOnly
_catalog As Dictionary(Of String, CatalogEntry)` keyed
case-insensitively on the name; each entry carries
DisplayName + DownloadUrl + Instructions. `CheckSingle`
dispatches by `name.ToLowerInvariant()` to a per-prereq
probe helper. Adding a new prereq is two edits: an entry
in `_catalog` AND a matching `Case` clause in `CheckSingle`
pointing at a new probe helper.

Probe for `vcredist-2015-2022-x64` reads
`HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64`
under `RegistryView.Registry64` and checks `Installed = 1`
(DWORD). This is Microsoft's own canonical "is it installed"
key — the 14.x ABI is shared by VC++ 2015, 2017, 2019, 2022,
so the single probe covers all four marketing names. Also
reads `Version` (e.g. `"14.38.33135.0"`) for diagnostics;
the notice fires off `Installed` alone but the version
round-trips through the response for future use.

Registry calls are guarded by `OperatingSystem.IsWindows()`
so a Linux node querying the same prereq returns
`Installed=False, Version=""` without throwing. Registry
permission failures (or missing-hive on non-standard
systems) are swallowed and treated as "not installed" —
false positives on the missing side are vastly preferable
to false negatives (which would let the user proceed into
a silent-crash install).

Endpoint: `GET /api/system/prerequisites?names=a,b,c`.
Comma-separated single query value rather than repeated
`?names=a&names=b` — simpler on both sides for the small
finite list, and individual names are escaped via
`Uri.EscapeDataString` on the Manager side so embedded
commas survive. Authenticated by the standard middleware
(anything under `/api/` that isn't `/api/version` or
`/api/auth`).

**Manager side (`NewInstallationForm.RefreshNoticesAsync`).**
Replaces the previous sync notice-fetch in `OnGameChanged`
with a two-phase async pattern, also hooked from
`OnNodeChanged`:

  1. **Phase 1 (sync, immediate)**: render notices from
     `IInstallationNoticeProvider.GetPreInstallNotices()`.
     User sees visual feedback for the game change without
     waiting on the wire call.
  2. **Phase 2 (async, wire call)**: if the plugin
     implements `IPrerequisiteProvider` AND a node is
     selected, call `CheckPrerequisitesAsync` on the node.
     For each result that's `Recognized=True AndAlso
     Installed=False`, synthesise a Warning notice using
     the node-supplied display fields. Re-render combined
     (static first, then dynamic).

If nothing's missing (or the plugin doesn't implement
the interface, or no node is selected), the Phase-1 render
is the final state — no re-render needed.

Cancellation: each `RefreshNoticesAsync` call cancels and
replaces a class-level `_prereqCheckCts` so fast game- or
node-switching doesn't let a stale wire response clobber
notices for the current selection. Resumes on the captured
UI SyncContext so the final `RebuildNoticesPanel` call
lands on the UI thread without an explicit marshal.

All failure modes are silent — prereq notices are
quality-of-life, never gating:
- `OperationCanceledException` from CTS replacement: bail
- `NodeApiException` with `StatusCode = NotFound` (pre-
  prereq-feature node, older binary, endpoint doesn't
  exist): bail with static notices left in place
- Generic catch (connection timeout, HTTP-level failure,
  deserialisation): bail with static notices left in
  place

The Phase-1 static notices always render even when Phase 2
fails, so a network hiccup at form-open doesn't strip the
user of game-level context they should see regardless of
prereq state.

**Notice rendering.** Body combines the node-supplied
`Instructions` text with a separate `Download: <url>` line
separated by a blank line, so the URL is visually distinct
from prose. Today the notice renderer doesn't make URLs
clickable (user copies the link) but the separation makes
that trivial.

**Conan plugin opt-in (`ConanExilesPlugin.vb`).** Adds
`Implements IPrerequisiteProvider` and a one-line
`GetRequiredPrerequisites` returning
`{"vcredist-2015-2022-x64"}`. The static
`IInstallationNoticeProvider` notices stay (Windows-only
advisory, Enhanced-vs-Legacy build context); the new
prereq notice is purely additive when the runtime is
missing.

**Why not bake VC++ install into the SteamCMD pipeline
(`_CommonRedist` synthesis)?** Considered and rejected:
Conan ships no `_CommonRedist` folder by design, and the
existing node-side scanner only runs after SteamCMD
completes. Synthesising a fake folder pre-SteamCMD risks
Steam's verify-integrity sweep deleting unrecognised
files. Bundling Microsoft's installer with the manager
adds ~25 MB per platform plus licensing concerns; downloading
on-demand from `aka.ms` is a per-install cost that's better
amortised as a one-shot manual install. Detection-only
notice was chosen as v1; auto-install ("Install for me"
button on the notice that downloads + runs silently) is
deferred to a future mini-phase since it adds non-trivial
moving parts: download progress UI, signature
verification, admin-elevation handling, retry-on-failure.

**Self-suggesting install path on form open
(`NewInstallationForm.OnShown` override).** Bundled in the
same work as the prereq feature: the form's default install
path suggestion (fetched from
`NodeStatusResponse.ServersDirectory` + the selected
plugin's `GameId`) wasn't populating at form open — only
appeared after the user changed game or node. Root cause
was that `RefreshSuggestedInstallPathAsync` used `Me.Invoke`
to read the combo selections from its background thread,
and the form's window handle doesn't exist when the
constructor's `Task.Run` fires it. `Me.Invoke` threw
`InvalidOperationException`; the outer `Try/Catch`
swallowed it; the path stayed empty. Subsequent event-
driven refreshes (Node Windows↔Linux switch, plugin
change) succeeded because by then the form was shown and
the handle existed. Fix: removed the constructor's
`Task.Run` and added a `Protected Overrides Sub OnShown(e)`
that fires `RefreshSuggestedInstallPathAsync()` directly
(UI thread, no `Task.Run` wrapper needed — the first `Await`
in the chain yields the UI thread back to the message pump
naturally). See also the matching VB.NET gotchas-table
entry for the underlying pattern.

**Future catalog growth.** The architecture admits arbitrary
runtime-dependency types (DirectX, .NET Desktop Runtime
versions, OpenAL, OS-bundled prerequisites for non-Windows
nodes when cross-platform plugins land) without contract
changes — only the node's catalog + a matching probe helper
per entry. Plugin names are opaque strings; older Manager
builds talking to a newer node still work because the
response shape is forward-compatible.

### Manager-side identity propagation (Phase 5g-2)

Closes the asymmetry from 5g-1 where `ChatMessageEntity`
rows carried full identity (`CharacterId` + `PlatformUserId`
+ `DisplayName`) but `PlayerActivityEntity` rows carried
only the raw parser-verdict name. History timeline rendering
showed the same player under two different names on the
same screen — chosen character name on chat rows, Steam
persona on join/leave rows — because on Last Oasis those
two strings differ by default at character creation for
nearly every player. Not a rename problem; just how LO
identity is structured.

**Architectural choice: write-time snapshot.**
`PlayerActivityEntity` gains three columns — `CharacterId`,
`PlatformUserId`, `DisplayName` — populated at the moment
the join/leave row is persisted via a wire call to the
Node's `/players` endpoint. History rendering coalesces
these against the raw `PlayerName` via `IdentityFormatter`
for display. No Manager-side mirror of the Node's `players`
table, no render-time wire calls per row. Snapshot
semantics across both row kinds matches
`ChatMessageEntity`'s existing approach and is correct in
nearly every case because a character's chosen name is
generally stable across its lifetime; the rare myrealm
admin-rename leaves old rows showing the old name, which
is arguably more honest about what the player was actually
called at that moment than retroactive rewriting would be.

**Schema additions (`GsmDbContext.PlayerActivityEntity`).**
Three new nullable columns: `CharacterId`, `PlatformUserId`,
`DisplayName`. Caps in `PlayerActivityEntityConfig` mirror
`ChatMessageEntityConfig`: 64 chars for the two numeric ID
columns, 100 for `DisplayName`. New non-unique index on
`CharacterId` for future cross-time-range queries per
character — same shape as `ix_chat_character` from 5g-1.
SQLite excludes NULLs from index, so the index doesn't
bloat with pre-migration rows.

**Write-time enrichment
(`InstanceManager.PersistPlayerObservationAsync`).** The
pre-5g-2 sync `PersistPlayerObservation` got split into a
sync entry point that resolves session identity
synchronously up-front, plus an async core that does the
wire call before writing the row. Sync wrapper is what
the SSE log-stream callback calls; the async core runs on
the thread pool so a slow `/players` call can't stall the
SSE reader.

Identity matching tries either `PlatformPersona == playerName`
(the LO common case — the parser delivers the raw login
string) or `DisplayName == playerName` (forward-compatibility
for any future plugin that routes verdicts through the
in-game name). First hit wins. Misses on either surface
leave the identity columns as NULL; the History renderer
falls back to PlayerName via `IdentityFormatter`. The
common miss case is PlayerLeave: the Node's `EventStore`
removes the session from its in-memory dict on the same
log line the Manager is processing, so by the time the
Manager's HTTP request resolves, `/players` no longer
contains the leaving player. PlayerJoin almost always hits.
Documented fallback rather than a bug.

`sessionIdentity` is captured synchronously up-front and
passed in as a parameter so the `ClearPlayerTracking` flush
path (which fires immediately before `StopLogStream` tears
down the parser) doesn't end up with synthetic leave rows
stamped under the `{gameId}:{instanceId}` fallback identity
instead of the actual `realm:tile` identity. The parser-
read happens while the parser is alive; the async wire
call + DB write can run whenever the thread pool schedules
it.

**Shared `IdentityFormatter` helper
(`GSM.Manager.Core/IdentityFormatter.vb`).** New module
with one method: `Format(displayName, platformPersona,
fallback)` returning the first non-empty value. Three
consumers: `HistoryQueryService.LoadTimeline`'s activity-
row assembly, `GsmSlashCommands.BuildPlayersResponse`, and
(implicit) any future caller that needs the same
"DisplayName → PlatformPersona → fallback" decision. The
rule is one line of logic, but inline duplication across
several consumers had already produced subtly different
renderings in 5g-1 testing — having one Format method to
point at when this comes up again keeps the fix in one
place.

The formatter is intentionally a Module, not an injected
service, because the function is pure and stateless.
Forcing every consumer to take a constructor dependency
on a service that wraps a one-line If chain would be
over-engineered.

**Consumer updates.** `HistoryQueryService.LoadTimeline`'s
activity-slice append now populates `TimelineRow.PlayerName`
via `IdentityFormatter.Format(r.DisplayName, Nothing,
r.PlayerName)` and surfaces the snapshotted
`PlatformUserId` + `CharacterId` columns on the row —
previously those were Chat-only. `TimelineRow`'s doc
comments updated to document the new dual-kind
population. `GsmSlashCommands.BuildPlayersResponse`
switched its inline coalesce to
`IdentityFormatter.Format(p.DisplayName, p.PlatformPersona,
"(unknown)")` so the Discord `/players` slash command and
the History window render the same player identically.

**Backfill from `ChatMessages`: dropped.** The original
plan called for a one-shot startup migration that walked
old `PlayerActivity` rows where `CharacterId IS NULL` and
attempted to attribute identity from single-occupant chat
windows on the same session. Discarded during scoping
because on Last Oasis `PlayerActivity.PlayerName` is the
platform persona (Steam handle) while
`ChatMessages.DisplayName` is the chosen character name,
and those differ by default for nearly every player — not
just after admin renames. Name-equality matching across
the two tables would only recover the edge case of players
who happened to pick their Steam handle as their character
name, at non-trivial false-positive risk on busier tiles.
Old rows render via `IdentityFormatter`'s fallback to
PlayerName (unchanged from pre-5g-2 behaviour); new rows
benefit from the snapshot columns going forward.

**Node side: turned out to be already done.** The scoping
conversation discovered that every node-side bullet in the
original 5g-2 plan was already implemented — likely shipped
quietly alongside 5g-1 without a plan update. `players`
+ `instance_state` SQLite tables, `PersistPlayer` upserts,
`PersistInstanceStateSnapshot` upserts, `LoadInstanceState`
+ `RegisterInstance(..., hydrateState:=True)` hydration,
tile-clearing on `EnteringMap`/`LeavingMap`,
`LookupPlayerDisplayName` cached-name lookup. No node-side
code changes needed in 5g-2; the Manager side was the
entire remaining scope.

**Terminology note.** Earlier drafts described the
identity-resolution problem as bridging "the rename gap"
or "renamed characters". That's wrong nomenclature for
Last Oasis: character names are chosen at character
creation and are generally permanent over the character's
lifetime (the CharacterId is stable; the chosen name CAN
change via myrealm admin action but that's a rare edge
case, not a routine player action). The default state —
DisplayName ≠ PlatformPersona — holds for nearly every LO
player from character creation onward, not just after a
rename event. Code comments and doc strings written
during 5g-2 use the corrected terminology; older comments
elsewhere in the tree may still mention renames as the
driver and will be cleaned up opportunistically.

**Migration step.** After source changes land, run
`Add-Migration Phase5g2_PlayerActivity_Identity` in Visual
Studio Package Manager Console, then `Update-Database` to
apply. The migration is purely additive (three new nullable
columns + one index) so existing rows read fine with NULL
identity columns and render via the IdentityFormatter
fallback path.

**Residual gap captured in Backlog as Phase 5g-3.** Short
LO sessions where a player joins, leaves before chatting
AND before the autosave tick, AND no prior session has
cached their (PlatformUserId → DisplayName) mapping —
activity rows for those sessions carry `CharacterId` +
`PlatformUserId` from join/leave events but never resolve
`DisplayName`. Hypothesis: LO log lines emit a richer
actor identity surface (`Player_0_C` /
`OasisPlayerController_0_C` entity names, `{UUID}`-shaped
`ActorGuid` fields) that could bridge the gap via a
transitive identity graph. Investigation requires log
samples; see Backlog.md for the pickup checklist.

### Conan-specific identity corrections (Phase 5g-2b)

Live-tested 5g-2 against a Conan Exiles instance and the
History window showed the FLS handle `losno420#72569` on
join/leave rows for a character whose chat rows correctly
rendered as `Gina`. Investigation surfaced two distinct
problems: a Conan-plugin parse-rule labelling error, and a
remaining edge-case gap that the 5g-2 write-time snapshot
doesn't cover.

**Root cause: Conan's `Join succeeded:` carries the FLS
handle, not the character name.** The post-colon token on
`LogNet: Join succeeded: <token>` is structurally a
platform-account identifier — the FLS handle, sometimes
bare (`losno420`) and sometimes with a discriminator
(`losno420#72569`), depending on how Funcom's identity
service has provisioned that account. It is NOT the in-game
character name. The character name only appears later, via
`ConanSandbox: Display: Character ID <n> has name <Name>`
(spawn line, fires ~100-200ms after Join succeeded) and on
every chat line. The Conan plugin's original parse rule
captured this token into `DisplayName`, so the Node's
`PlayerSession.DisplayName` got polluted with the FLS
handle until chat eventually overwrote it. Manager's
write-time snapshot at join caught the bad DisplayName;
at leave time it caught either the bad value or, if chat
had flipped it, found no session match because
`FindExistingSession` was trying to match DisplayName ==
FLS_handle against a session whose DisplayName had become
"Gina".

**Fix 1: Conan parse-rule capture renames.** In
`ConanExilesPlugin.vb`, both the `Join succeeded:` and
`Player disconnected:` rules' capture groups renamed from
`DisplayName` to `PlatformPersona`. Slot semantics now
match Last Oasis: the platform-identity surface goes into
`PlatformPersona` (stable for the session's lifetime),
leaving `DisplayName` free for the actual character name
to land via chat or the Node's `LookupPlayerDisplayName`
cache. The leave-side rename also closes a latent bug:
after chat has flipped DisplayName to the character name,
the leave event's FLS-handle token would no longer match
the session via the DisplayName key, falling through to a
RemoteAddress match (which works, but is fragile);
matching by PlatformPersona is stable across chat updates.

**Fix 2: render-time chat fallback in
`HistoryQueryService.LoadTimeline`.** New helper
`ApplyChatFallbackDisplayNames`. For activity TimelineRows
where the write-time snapshot's `DisplayName` was empty
or equal to the raw `PlayerName`, AND `PlatformUserId` is
populated, the helper looks up the most recent
`ChatMessages.DisplayName` for that (SessionIdentity,
PlatformUserId) pair and overrides `TimelineRow.PlayerName`
with the result. One indexed query per distinct (sid, pid)
pair, leveraging the `IX_chat_pid` index from 5g-1. Handles
the edge case where a player joins on a Node whose
`players` table cache doesn't have them (first-time on
this Node, cross-Node migration, etc.) and the snapshot
comes back with NULL DisplayName.

**Why no Character ID parse rule on Conan.** The
`Character ID <n> has name <Name>` spawn line is tempting
as a `PlayerIdentity` rule — it carries both pieces of the
binding the Node needs. Investigation of EventStore.vb's
`PlayerIdentity` handler showed it'd be a no-op: the
handler has stash paths for `(pid + display, no cid)` and
`(cid + pid, no display)` but not for `(cid + display, no
pid)`. The spawn line's data shape falls in the third
bucket, so `FindExistingSession` misses across all keys
(no session has CharacterId bound yet at that moment) and
the event silently drops. Closing this gap would require
a third stash path on the EventStore side plus a heuristic
to drain it when a session later gains the matching cid
via chat. Deferred to a follow-up; the chat-fallback
Mechanism above covers the common case (returning players
whose chat history has the binding).

**Residual gap for Conan.** First-time-ever players who
join, never chat, and then leave: their join/leave rows
show the FLS handle permanently. The chat-fallback has no
chat to bridge through, and the Character ID line's
spawn-time data shape isn't currently consumable by the
Node's stash machinery. Acceptable trade-off; the binding
lands correctly on the player's first chat in a future
session, and that future session is then a returning-
player scenario the Node's `LookupPlayerDisplayName`
cache handles cleanly.

**Deployment note: living sessions need to reconnect.**
When the plugin hot-reloads, the Manager pushes new parse
rules to the Node via `UpdateParseRulesAsync`, but the
Node's in-memory `PlayerSession` state for currently-
connected players doesn't get re-evaluated. Sessions that
bound under the old rules still have PlatformPersona empty
and DisplayName = FLS_handle. Players need to disconnect
and reconnect once for the new rules to take effect on
their session. Pre-5g-2b History rows showing the FLS
handle stay as-is permanently — no backfill (same
rationale as 5g-2 dropped its backfill: false-positive
risk on player-to-player matching outweighs the value of
recovering edge-case rows).

**Cosmetic follow-up on InstancePanel.** The
"Steam name" column label on the Conan InstancePanel
Overview currently shows whatever lives in `PlatformPersona`
— which post-fix is the FLS handle (not the Steam name).
The label is technically misleading for Conan even though
the data being shown is the most useful platform-identity
string available. Worth either renaming the column to
"Persona" generically or making the column label
plugin-driven; not done in 5g-2b to keep scope focused.

### Plugin-defined shared config groups (Phase 5h-1 through 5h-5)

Motivated by the operator running three Last Oasis installations
on a single realm with different tile pools: each install
needed its own copy of `CustomerKey` + `ProviderKey` in its
`InstallationEntity.ConfigJson`, and rotating credentials
required editing three installations. The generalisation is
plugin-driven — LO's Realm concept is one instance of a
broader "shared config above the installation level" pattern
any future plugin can opt into (Cluster for an Ark cluster
setup, League for a competitive Factorio league, etc.).

**Interface contract.** `ISharedConfigProvider` in
`GSM.Contracts/IGamePlugin.vb` is the plugin's opt-in surface:

- `SharedConfigKey As String` — lowercase identifier for the
  group type (e.g. `"realm"`). Used as `GroupType` on the
  storage row.
- `SharedConfigLabel As String` — user-facing singular name
  (e.g. `"Realm"`). The management UI pluralises with a bare
  `+"s"` to label its tabs.
- `GetSharedConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)`
  — the field shape, same descriptor type used by
  `GetInstallConfigSchema()` and `GetInstanceConfigSchema()`.
  Sensitive fields use `IsSensitive=True` to opt into
  encryption-at-rest.
- `DiscriminatorFieldKey As String` — the field key whose
  value identifies the group across installs (e.g.
  `"CustomerKey"`). Currently used by no consumer; would have
  driven the dropped 5h-5b auto-migration prompt and is kept
  for any future tooling that needs to identify "which group
  matches this install's config".

**Storage.** `SharedConfigGroupEntity` (in
`GSM.Manager/Data/GsmDbContext.vb`) is a plain row table with
`GroupId` (GUID-string PK), `PluginId` (game id of the
declaring plugin), `GroupType` (the plugin's
`SharedConfigKey`), `DisplayName` (user-set), `ConfigJson`
(serialised field dict with sensitive fields wrapped per the
encryption sentinel — see below), `CreatedUtc`, `UpdatedUtc`.
Composite `HasIndex` on `(PluginId, GroupType)` so the manager
can efficiently list all groups for a given plugin's shared
config type. `InstallationEntity` gains a nullable
`SharedConfigGroupId` FK with `OnDelete=SetNull` — deleting a
group leaves its installations intact, just unlinked. Migration:
`20260522145126_Phase5h_SharedConfigGroups`, auto-applied at
Manager startup via the existing `Database.Migrate()` path.

**Encryption-at-rest.** `SharedConfigService` (in
`GSM.Manager/Core/SharedConfigService.vb`) owns the
CRUD surface (`ListGroups`, `GetGroup`, `CreateGroup`,
`UpdateGroup`, `DeleteGroup`) plus `LoadGroupFieldsPlaintext`.
Field-level encryption: when writing, each field marked
`IsSensitive=True` in the plugin's schema gets wrapped with a
sentinel prefix `__GSM_ENC__:` followed by base64 DPAPI bytes
(via `CredentialService.ProtectString`). On read,
`LoadGroupFieldsPlaintext` detects the sentinel and decrypts
via `UnprotectString` before handing values back to the schema
renderer. Same DPAPI mechanism as the existing
Steam-credentials flow; the sentinel approach (rather than a
sibling encrypted column) keeps the storage shape uniform and
the encryption decision schema-driven.

**Three-layer merge.** New
`InstanceManager.MergeConfigLayers(db, installation, instance)`
overlays three layers in precedence order (highest wins):
layer 0 = group (decrypted via SharedConfigService), layer 1
= installation (`InstallationEntity.ConfigJson`), layer 2 =
instance (`InstanceEntity.ConfigJson`). The transition
discipline is **"empty upper layer doesn't clobber non-empty
lower layer"** at each overlay step — critical for
backwards-compat with the LO transition where the same
field keys (`CustomerKey`, `ProviderKey`) live at both group
and install levels during the migration period. An install
with non-empty install-level CustomerKey + a linked Realm
with CustomerKey on the group: the install value wins (operator
hasn't migrated yet). The same install with install-level
CustomerKey blanked: the group value wins (operator has
migrated). Layer 0 is skipped entirely when the plugin
doesn't implement `ISharedConfigProvider` OR the installation
has `SharedConfigGroupId = NULL`; load errors are logged and
treated as "no group layer". Plugins see the merged result via
`InstanceConfig.CustomFields` exactly as before; the layering
is transparent to consumer code.

**LO opt-in.** `LastOasisPlugin` implements
`ISharedConfigProvider` with three fields: `CustomerKey`
(required, sensitive), `ProviderKey` (required, sensitive),
`RealmName` (optional, cosmetic — used in the History Source
column). Schema rendering uses `FieldType.Text` (visible) for
all three; encryption is purely a storage concern and the
operator can still read their credentials in the editor for
verification. The same three fields stay in
`GetInstallConfigSchema()` during the transition for
backwards-compat; existing installs continue to work
unchanged until the operator manually links and clears.

**Management UI.** Tools → Shared Resources opens
`SharedConfigGroupsForm` (in `RemainingForms.vb`). TabControl
with one tab per loaded plugin implementing
`ISharedConfigProvider`; empty state when no plugins opt in.
Each tab contains a ListView (Name / Linked installations /
Updated columns) plus Add / Edit / Delete buttons. The
Delete button warns when installations are linked (FK becomes
NULL, not a cascade) so the operator knows what happens.
Per-item editor is `SharedConfigGroupEditForm`, which renders
the plugin's schema via the existing `SchemaFormBuilder` and
exposes `SavedGroupId` after a successful save so calling
forms can re-select the just-created group in a dropdown.

**Installation editor integration.** Both `NewInstallationForm`
and `EditInstallationForm` gained a "Realm:" row between the
Steam Account and Run _CommonRedist rows. The row is hidden
until the selected plugin implements `ISharedConfigProvider`
(NewInstallationForm's `OnGameChanged` calls `RefreshRealmPicker`;
EditInstallationForm reads the installation's plugin once on
load). A ComboBox lists `(none)` + all existing groups for the
plugin; the "New..." button opens `SharedConfigGroupEditForm`
in create-new mode and re-selects the new group on return
via `dlg.SavedGroupId`. Save writes the selection to
`InstallationEntity.SharedConfigGroupId` (NULL for `(none)`).

**Skipped scope.** Phase 5h-5b (auto-migration prompt) was
reviewed and dropped. The detection logic ("find installations
sharing a DiscriminatorFieldKey value, offer to consolidate")
was straightforward; the UX (per-group dialog, per-install
opt-out, status report) was the bulk of the work, and with
zero deployed copies in the wild plus a sub-minute manual
migration path through 5h-5, not worth shipping.

**Operator workflow for migrating the LO setup.** (1) Tools
→ Shared Resources → Realms tab → Add, name it "Site's World",
paste CustomerKey + ProviderKey, optionally set RealmName,
Save. (2) For each of the three existing LO installs: Edit
Installation → Realm picker → select "Site's World" → Save.
(3) Optional, to start using the realm-layer values rather
than the install-layer copies: re-Edit each installation,
blank out install-level CustomerKey + ProviderKey, Save.
Until step 3, the merge keeps install-level values winning
per precedence — functional but redundant.

**VB.NET gotcha encountered.** First cut of
`SharedConfigGroupsForm.PopulateTabs` used a named
`List(Of (Plugin As IGamePlugin, Provider As ISharedConfigProvider))`
tuple for the per-tab provider list. With `Imports GSM.Plugin`
active in the file (needed for the interface types), VB.NET's
case-insensitive identifier resolution treated bare references
to a same-scope loop variable as if they referenced the
imported `GSM.Plugin` namespace — producing BC30112
("'GSM.Plugin' is a namespace and cannot be used as an
expression"). Renaming the loop variable alone wasn't
sufficient; the named tuple element `Plugin` participated in
the same case-insensitive shadow. Final fix: replace the
named tuple with a small private nested class
(`ProviderEntry { Game, Provider }`) and use a short
non-conflicting loop variable name (`gp`). The reserved-keyword
table below has the row.

### Plugin-defined Source column for History (Phase 5h-6)

Motivated by two observations during 5h-5 testing:

1. The History window's "Tile / Session" column showed the
   truncated realm_id substring even when the installation
   was linked to a Realm with a human-readable DisplayName.
2. The "Instance" column showed `Node:Instance:GUID` with no
   indication of which realm the rows belonged to, making
   cross-realm filtering visually noisy.

Fix shape: merge the two columns into a single "Source"
column whose content is plugin-formatted, and move the raw
InstanceId (previously embedded in the Instance column
for grep-the-log workflows) into a hover tooltip + right-
click action.

**Interface contract.** `ISourceLabelProvider` in
`GSM.Contracts/IGamePlugin.vb` is the plugin's opt-in:

- `FormatSourceLabel(context As SourceLabelContext) As String`
  — invoked once per row at render time. Should be cheap
  (no I/O, no expensive lookups). Returning Nothing or empty
  falls back to the manager-supplied default, so a plugin
  that opts in but bails out under some condition gets a
  sensible default rather than a blank cell.

**Context shape.** `SourceLabelContext` (also in
`IGamePlugin.vb`) carries everything the plugin might need
for labelling without exposing EF or storage internals:

- `SessionIdentity` — raw, game-defined (e.g.
  `"lastoasis:{realm_id}:{tile_id}"`); Nothing for games
  without a session concept.
- `TileName` — friendly tile name observed via parse rules
  (e.g. `"[N5][PvE] Ikronic Pain"`); empty when not yet known.
- `NodeName`, `InstallationName`, `InstanceName` — display
  names of the host node, installation, and instance.
- `InstanceId` — full GUID. Plugins typically don't render
  this in the label (the UI exposes it via tooltip and
  right-click); available for plugins that want a short
  prefix.
- `SharedConfigGroupName` — the user-set DisplayName of the
  linked SharedConfigGroup, Nothing if not linked. Plugins
  should prefer this over digging `RealmName`-like fields
  out of merged config because the user picked it as their
  friendly label.

**LO implementation.** Three em-dash-separated segments
(`{TileName} — {RealmDisplay} — {Node}/{Install}`), dropping
any segment with no data. RealmDisplay prefers
`context.SharedConfigGroupName` and falls back to
`"realm {first-8-of-realm_id}…"` parsed out of
`SessionIdentity` — matching pre-5h-6 `FormatSessionLabel`
output for unlinked installs so the visual experience for
unlinked rows is unchanged. The instance-path segment is
intentionally Node/Install (NOT Node/Install/Instance)
because the LO backend reassigns tiles across instances
within an installation freely; the on-disk installation is
the meaningful disambiguator at the History level. The full
InstanceId is reachable via the row tooltip and right-click
"Copy instance ID" action for log-grep workflows.

**Manager dispatch.**
`HistoryQueryService.LoadResolvedInstances` does a two-query
pre-pass:

1. Inner join Instance + Installation + Node for all
   distinct InstanceIds in the result set, projecting
   NodeName + InstallationName + InstanceName + GameId +
   `install.SharedConfigGroupId`.
2. For installs whose `SharedConfigGroupId` is non-null,
   pull the SharedConfigGroup DisplayName in a single query
   (LEFT JOIN expressed as a second query + in-memory merge
   since typical N is tiny).

The result is a `Dictionary(Of String, ResolvedInstance)` (a
private nested class) keyed by InstanceId. Per row,
`ResolveSourceLabel` builds a `SourceLabelContext`, looks up
the plugin via `PluginRegistry.GetPlugin(GameId)`, casts to
`ISourceLabelProvider` if available, and dispatches — catching
plugin exceptions defensively (a misbehaving plugin's
formatting bug shouldn't kill the whole query). Plugins not
opting in OR returning Nothing/empty get a manager-supplied
default: `BuildDefaultSourceLabel` produces
`"Node/Install/Instance"`, skipping empty segments, falling
back to the raw SessionIdentity if nothing resolves.

The same machinery runs for both `TimelineRow` and
`SnapshotRow`. `SnapshotRow` previously didn't carry
`InstanceId`; added during this phase, captured from the
join event during activity replay.

**Session dropdown fix-up.** Added late in the phase after
the user noticed that the session-filter ComboBox at the top
of the History window still showed the truncated realm_id
substring even after the Source column had been switched to
realm DisplayName. Root cause: `LoadKnownSessions` builds
`SessionSummary.DisplayLabel` via `FormatSessionLabel`, which
only knew about tile name + parsed realm_id from
SessionIdentity. Fix: new pre-pass in `LoadKnownSessions`
joins SessionHosts → Instance → Installation →
SharedConfigGroup to build a `session-identity → realm-
DisplayName` map (first-write-wins per identity);
`FormatSessionLabel` gained an optional `realmDisplayName`
parameter and uses it in place of the truncated realm_id
substring when present. Unlinked installs continue to render
`tile — realm {hash}` as before; session-host rows pre-dating
the realm link stay on the legacy format until the session
is hosted again under the new linkage (no backfill).

**HistoryWindow column changes.** `BuildTimelineColumns`
dropped "Tile / Session" (260 px) + "Instance" (280 px) and
added "Source" (540 px, the merged width).
`BuildSnapshotColumns` renamed "Tile / Session" to "Source"
(400 px). The renderers read `r.SourceLabel` instead of
`r.TileDisplayName` + `r.InstanceDisplay`; the legacy
properties are kept on the row classes for backwards-compat
but no longer rendered.

**Row tooltip + right-click context menu.** Per-row
`ToolTipText` shows multi-line `"Session: {full identity}\n
Instance: {full GUID}"`, skipping either line when empty.
`ListView.ShowItemToolTips = True` enables the WinForms
built-in row tooltip rather than a separately-managed
`ToolTip` control — simpler, and the tooltip text just
updates per render. `ListViewItem.Tag` carries the
underlying `TimelineRow` / `SnapshotRow` so the context
menu actions can read SessionIdentity / InstanceId from the
row object via a small `ExtractRowIdentifiers` helper.

`ContextMenuStrip` has two items: "Copy &instance ID" and
"Copy &session identity" — accelerator keys I and S. The
`Opening` handler reads the selected row's identifiers via
`ExtractRowIdentifiers` and enables/disables each item
based on whether the corresponding identifier is non-empty,
so accidental no-op clicks can't happen. The copy actions
use `Clipboard.SetText` wrapped in `Try/Catch` (clipboard
can be transiently locked by another process), and confirm
via the status bar (`"Copied instance ID: 0a1b2c3d..."`).
Tooltip + Tag are set fresh on every render call —
including the UTC-toggle cache replay — so both stay
consistent with what's actually displayed.

### VB.NET gotchas — Phase 5h additions

New rows for the gotcha table:

| Pattern | Wrong | Right |
|---|---|---|
| Imported namespace clashing with same-case-insensitive identifier | `Imports GSM.Plugin` + bare `plugin` variable or named-tuple element `Plugin` in same scope | Use a non-clashing identifier (`gp`, `gamePlugin`); for named tuples that would have a clashing element, use a small private nested class instead |
| Plugin namespace shadows in tuple-element-name position | `List(Of (Plugin As IGamePlugin, ...))` with `Imports GSM.Plugin` active | Replace named tuple with a private nested class with renamed fields, OR use a clearly-different element name (`Game` rather than `Plugin`) |
| Reserved word `node` as parameter name in LINQ context | `Join node In db.Nodes` (compiles, but reads ambiguously next to the `node`/`Node` namespace conventions in WinForms code) | Use `nodeRow` or `nodeEnt` to avoid confusion |


