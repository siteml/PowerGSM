# Writing plugins

PowerGSM's game support is entirely plugin-driven. A plugin is a **single
VB.NET source file** dropped into the Manager's `Plugins\` folder; the Manager
compiles it at runtime with Roslyn and hot-reloads it whenever it changes — no
Manager rebuild, no restart.

This guide covers both plugin kinds:

- **Game plugins** (`IGamePlugin`, namespace `GSM.Plugin`) — teach PowerGSM to
  install, launch, monitor, and update a game's dedicated server.
- **Utility plugins** (`IUtilityPlugin`, namespace `GSM.Utility`) — don't host
  anything; they react to Manager-wide events (player joins, chat, server
  state) through a capability-gated context. Example: the shipped `lo-myrealm`
  name resolver.

The best reference is the shipped plugins themselves — they live in
[`GSM.PluginsSource/`](../../GSM.PluginsSource/) in the repo and are heavily
commented. `FactorioPlugin.vb` is the broadest example (two install methods,
managed files, map generation, structured config editing);
`WindrosePlugin.vb` is the smallest complete game plugin and the best starting
skeleton. The single authoritative reference for every interface and DTO is
[`GSM.Contracts/IGamePlugin.vb`](../../GSM.Contracts/IGamePlugin.vb) — all of
it documented inline.

---

## The architecture you're writing for

**"Manager interprets, Node executes."** Your plugin runs **only on the
Manager**. The Node never loads plugin code — it receives plain data (a
command line, environment variables, file paths to tail, regex rules to apply)
and executes it. Everything your plugin does must therefore reduce to
serializable instructions:

- Installation = a list of declarative **install steps** the Node runs.
- Launch = an **argument string** (plus env vars / config files written at
  launch) for an executable path you name.
- Log intelligence = **regex parse rules** the Node applies to log lines
  itself, so player/chat/state tracking keeps working while the Manager is
  closed.

Keep this in mind constantly: if a design requires your code to run on the
host machine at game runtime, it doesn't fit the model — express it as data.

---

## Anatomy of a plugin file

One `.vb` file, self-contained. Structure:

```vb
' <plugin id="mygame" name="My Game Dedicated Server" version="1.0.0" author="you" requiresContracts="2">

Imports System
Imports System.Collections.Generic
Imports GSM.Plugin

Public Class MyGamePlugin
    Implements IGamePlugin

    ' ... members ...
End Class
```

### The manifest line

The first-line comment `' <plugin ... >` is the **manifest**. It's parsed
before compilation:

| Attribute | Meaning |
|---|---|
| `id` | Unique plugin id, one keyspace across game **and** utility plugins. Third-party plugins are expected to prefix with their source owner (`yourname_mygame`) — bare ids trigger a warn-and-confirm at install. |
| `name` | Display name. |
| `version` | Semantic version — this is what update detection compares. |
| `author` | Pure credit. Free text, displayed, never used for trust. |
| `requiresContracts` | Contracts version the plugin needs (currently **2**). A plugin requiring a newer contracts version than the Manager has fails fast with one clear message instead of a compile-error cascade. |
| `requires` | Utility plugins only — capability list (see [Utility plugins](#utility-plugins)). |

A file with **no** manifest still loads, as an untracked "local" plugin — fine
while developing, but add the manifest before distributing, and note utility
plugins **must** have one.

Optional: a `' <dependencies>` comment block with `<depends id min />` entries
if your plugin needs another plugin present.

### Compilation environment

Each `.vb` file compiles as its **own assembly**; one plugin failing to
compile never blocks the others (failures show in *Tools → Manage Plugins →
Status*). Constraints that will bite you:

- **No `Microsoft.VisualBasic` auto-import.** Avoid `vbLf`, `vbCrLf`, `Chr`,
  `ChrW` — use `Convert.ToChar(10)`, `Environment.NewLine`, etc.
- **Named regex groups via concatenation.** Write
  `"(?<" & "Name" & ">...)"` rather than the literal `(?<Name>` — protects the
  group-name casing from tooling, and casing matters (the node matches
  `Custom_*` groups with an ordinal check).
- **Extension methods need explicit `Imports`** of their defining namespace.
- **Reserved keywords** can't be identifiers — the contracts already dodge
  these (`IntegerField` not `Integer`, `AllInstances` not `Global`…); do the
  same in your code.
- Full-trust code: your plugin is ordinary compiled .NET running inside the
  Manager. There is no sandbox (see [Capabilities](#capabilities-are-consent-not-a-sandbox)).

### Development loop

1. Edit the file in the Manager's `Plugins\` folder (or edit in
   `GSM.PluginsSource\` and copy over — that project exists so plugin source
   gets IDE support; it is **not** compiled into the product).
2. **Tools → Reload Plugins.**
3. Check *Manage Plugins → Status* for compile errors; iterate.

---

## Game plugins: the `IGamePlugin` members

Every member below is required (return `Nothing` / empty where noted). Grouped
by lifecycle.

### Identity

```vb
ReadOnly Property GameId As String
ReadOnly Property DisplayName As String
ReadOnly Property MaxInstancesPerInstallation As Integer?
```

- **`GameId`** — stable identifier used as the foreign key in every
  installation/instance record. **Never change it once installs exist** —
  existing databases reference it. Match the manifest id.
- **`DisplayName`** — UI label only.
- **`MaxInstancesPerInstallation`** — how many instances one installation can
  host. Most games file-lock their save/config/mod state, so `1` is typical.
  Return `Nothing` for no limit — appropriate when per-instance state lives
  entirely in command-line args and the binary is genuinely shared (Last Oasis
  hosting many tiles from one MistServer install). The Manager enforces this
  hard at instance creation (greys out *Add Instance…* and re-checks on save);
  `Nothing` skips both checks.

### Installation

```vb
Function GetSupportedInstallMethods() As IReadOnlyList(Of InstallMethod)
Function GetInstallSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep)
Function GetUpdateSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep)
```

- **`GetSupportedInstallMethods`** — `InstallMethod.SteamCmd` and/or
  `InstallMethod.DirectDownload`. The chosen method arrives back in
  `config.InstallMethod`; branch on it in the step builders (see Factorio).
- **`GetInstallSteps`** — the declarative recipe, executed by the Node in
  order. **This is where "Manager interprets" happens**: read
  `config.CustomFields` (your install-schema values), resolve all logic here,
  and emit plain-data steps. Step types:

  | Step | Key properties | Use |
  |---|---|---|
  | `SteamCmdInstallStep` | `AppId`, `BetaBranch`, `BetaPassword`, `ValidateFiles` (default True), `RequiresLogin` (default False → anonymous), `Platform` | Steam depot install/update. |
  | `DownloadStep` | `Url`, `DestinationRelativePath`, `ExtractArchive`, `StripTopLevelDirectory` | HTTP fetch; extraction handles zip/tar.gz/tar.xz/7z/rar. |
  | `WriteFileStep` | `RelativePath`, `Content`, `OverwriteExisting` (default False) | Drop a literal file — e.g. LO writes `903950` into `Mist/Binaries/Linux/steam_appid.txt`. Use forward slashes in relative paths; both platforms accept them. |
  | `CopyStep` | `SourceRelativePath`, `DestinationRelativePath`, `Overwrite` | Copy within the install. |
  | `RunProcessStep` | `ExecutablePath`, `Arguments`, `WorkingDirectory`, `TimeoutMs` (default 120000), `ExpectedExitCode` (default 0) | Run something once at install time. |

  Every step carries `StepName`, `Description` (shown in the progress UI) and
  a `Weight` (default 1.0) that apportions the progress bar.
- **`GetUpdateSteps`** — usually the same list (SteamCMD's `app_update`
  reconciles to the chosen branch), but you can differ — e.g. skip an initial
  config write so an update doesn't clobber user edits. Factorio's SteamCMD
  update also repairs `config-path.cfg`, which `app_update` rewrites.

`InstallationConfig` gives you: `InstallationId`, `GameId`, `DisplayName`,
`InstallPath`, `InstallMethod`, `NodeId`, `CustomFields`
(merged install-level values), and `Platform` (`NodePlatform` — branch for
Windows vs Linux steps).

### Configuration schemas

```vb
Function GetInstallConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)
Function GetInstanceConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)
```

Install-level fields are set once per installation and shared by its
instances; instance-level fields are per instance. Same key at both scopes =
instance wins; you receive the merged dictionary in
`InstanceConfig.CustomFields`. LO uses this deliberately: CustomerKey lives on
the installation, an instance can override it to host a second realm.

`ConfigFieldDescriptor` properties:

| Property | Notes |
|---|---|
| `Key` | Dictionary key you'll read back. |
| `Label`, `Description` | UI text. Description supports full sentences — the shipped plugins write mini-docs here; do the same. |
| `FieldType` | `Text`, `Password` (masked), `IntegerField`, `BooleanField`, `[Enum]` (+ `EnumValues`), `FilePath`, `ManagedFilePicker` (picks from a managed directory — pair with `ManagedDirectoryRef`), `Notice` (renders Description as inline explanatory text; no value). |
| `DefaultValue` | String, even for ints/bools (`"7777"`, `"true"`). |
| `IsRequired`, `IsSensitive`, `ValidationRegex`, `MinValue`/`MaxValue` | Form validation. |
| `IsPort` | Manager auto-allocates + clash-checks the value across every instance on the Node. |
| `ReservedPortOffsets` | Derived ports to reserve alongside an `IsPort` field — Conan reserves offset `+1` for its immovable "pinger". |

### Launch

```vb
Function GetExecutablePath(config As InstanceConfig) As IReadOnlyList(Of String)
Function BuildLaunchArguments(config As InstanceConfig) As String
Function ValidateConfig(config As InstanceConfig) As IReadOnlyList(Of String)
Function GetRconProtocol() As RconProtocol?
```

- **`GetExecutablePath`** — one or more candidate paths **relative to the
  install directory**; the Manager tries them in order and remembers which
  exists on the node. Multiple entries cover binary variants (LO: shipping vs
  dev exe, Windows vs Linux names).
- **`BuildLaunchArguments`** — the command line, from `config.CustomFields`.
  Runs Manager-side immediately before each start, so it always sees current
  config. `InstanceConfig` also carries `EnvironmentVars`, `RconPort` /
  `RconPassword` / `RconProtocol`, and the crash policy fields (`CrashPolicy`,
  `MaxCrashCount`, `CrashWindowMinutes`) — populate what applies; it's all
  forwarded to the Node in the start request. `config.Platform` tells you
  Windows vs Linux where launch differs.
- **`ValidateConfig`** — return human-readable problems; non-empty blocks the
  start behind a "Start anyway?" prompt. Cheap pre-flight for likely-fatal
  misconfig (Factorio: no save selected and UseLatestSave off).
- **`GetRconProtocol`** — `RconProtocol.Source` etc., or `Nothing` for no
  RCON.

Config files written at launch (Conan's `Engine.ini`, Windrose's
`ServerDescription.json`) are **not** written by reaching into the node's
disk — implement [`IStartupFileProvider`](#istartupfileprovider) and the file
contents travel with the start request.

### Crash handling

```vb
Function EvaluateCrash(exitCode As Integer,
                       crashCount As Integer,
                       policy As CrashRestartPolicy) As RestartDecision
```

Called when the node reports a crash. Return
`RestartDecision.Restart(delayMs)` (optionally with `ModifyArguments`) or
`RestartDecision.Halt(reason)`. `crashCount` is the count within the policy's
window — use it for backoff or give-up logic. Mind platform exit-code
semantics: Linux clean SIGINT stop = **130**, not a crash. Note the node also
enforces the declarative crash policy autonomously (so restarts work with the
Manager offline); `EvaluateCrash` is the Manager-side refinement.

### Monitoring

```vb
Function GetLogSources(config As InstanceConfig) As IReadOnlyList(Of ILogSource)
Function GetLogParseRules() As IReadOnlyList(Of LogParseRule)
Function CreateLogParser() As ILogParser
```

- **`GetLogSources`** — where the server's output comes from:
  - `StdoutLogSource` (`CaptureStderr` default True) — the process pipes.
  - `FileLogSource(sourceId, pathPattern)` — a file the node tails.
    `{InstallPath}` and `{InstanceId}` tokens are substituted;
    `FollowRotation` opts into rotation handling. If any file source is
    present, the file becomes the authoritative log (stdout still drained —
    the node handles that; you don't care).
- **`GetLogParseRules`** — the standalone-node magic. Each `LogParseRule` is a
  `Kind` (`PlayerJoin`, `PlayerLeave`, `PlayerIdentity`, `ChatMessage`,
  `ServerStateChange`, `TileLoaded`, `Custom`) plus a regex `Pattern` with
  named capture groups. Sent once at instance start; the node applies them to
  every line thereafter — players/chat/state stay tracked with the Manager
  closed. Recognized group names map straight onto node-side state: `Name`,
  `Platform`, `PlatformUserId`, `CharacterId`, `RemoteAddress`, `Message`,
  `MatchState`, `TileId`, `TileName`, `MapPath`, `Registered`. Groups named
  `Custom_*` pass through as extra fields. Remember the concatenation trick
  for group names.
- **`CreateLogParser`** — the Manager-side parser (`ILogParser`), and **not
  optional in practice** if your game has player/chat/session events: the
  Manager's live pipeline — History timeline rows, join/leave notifications,
  session identity, utility-plugin events — is driven by `ParseLine` on the
  Manager's log stream. The declarative rules feed the **node's** standalone
  state (its `/players`, `/server-state`, `/chat`, kept while the Manager is
  closed); the parser feeds the **Manager's**. Ship both, classifying the
  same lines. Members: `ParseLine(line) As ParsedLogEvent` (stateful parsing;
  called from a single thread per instance); `CurrentSessionIdentity` for
  cross-instance entity identity (LO returns `lastoasis:{realm}:{tile}`;
  `Nothing` falls back to `{gameId}:{instanceId}` — fine when that fallback
  matches your real format, as Factorio's does); `GetCrashPatterns()` for
  log-based crash detection. Returning `Nothing` is only appropriate for a
  game with no player/session semantics at all.

### Mods

```vb
Function CreateModManager() As IModManager
```

`IModManager` or `Nothing`. (Factorio's is the shipped example.)

---

## Opt-in side interfaces

Everything beyond the core contract is an **opt-in interface** — implement it
on the same class and the Manager detects it. This pattern exists because
VB.NET has no default interface members: adding to `IGamePlugin` itself would
break every existing plugin, so new surface ships as side interfaces.

### `IInstallationNoticeProvider`

```vb
Function GetPreInstallNotices() As IReadOnlyList(Of InstallationNotice)
```

Notices (`Severity` Warning/Information, `Title`, `Body`) shown in the New
Installation form before install. Use for platform caveats ("Windows nodes
only"), irreversible pre-install choices (Conan: "pick your Build now"), and
networking surprises (Windrose's UPnP default).

### `IPrerequisiteProvider`

```vb
Function GetRequiredPrerequisites() As IReadOnlyList(Of String)
```

Named host prerequisites — currently `"vcredist-2015-2022-x64"` is the
recognized id. The node probes for them; the Manager surfaces missing ones
with a download link before install.

### `IVersionAwarePlugin`

```vb
Function GetLatestVersionAsync(config As InstallationConfig,
                               cancellation As CancellationToken) As Task(Of String)
Function GetInstalledVersionAsync(config As InstallationConfig,
                                  client As GSM.Node.Api.INodeClient,
                                  cancellation As CancellationToken) As Task(Of String)
```

Powers update-available detection. `GetLatestVersionAsync` asks upstream
(Factorio hits factorio.com's version JSON); `GetInstalledVersionAsync` reads
the deployed version off the node's disk via the passed `INodeClient` (file
endpoints). Plugins without this interface fall back to the Steam buildid path
(SteamCmd installs) or are skipped.

### `IReadySignalProvider`

```vb
Function GetReadyForNextSignal() As ReadySignal
ReadOnly Property DefaultReadyTimeoutSeconds As Integer
```

"Actually ready" detection beyond process-started — a signal (typically a log
pattern) the restart coordinator waits for before considering the instance up,
with the timeout as fallback (or as the sole wait when the signal is
`Nothing`). Matters for sequenced restarts of multi-instance games.

### `IManagedDirectoriesProvider`

```vb
Function GetManagedDirectories(config As InstanceConfig) As IReadOnlyList(Of ManagedDirectory)
```

Exposes folders (saves, configs, logs) as file managers in the instance panel
— upload/download/delete/rename/copy, with an allowed-extension filter
(Factorio: `saves/`, `.zip` only). The directory's key is also what
`ManagedFilePicker` config fields and `IFileGenerationProvider` reference.

### `IFileGenerationProvider`

```vb
Function GetTargetDirectoryRef() As String
Function GetButtonLabel() As String
Function GetTabTitle() As String
Function GetGenerationSchema(instanceConfig As InstanceConfig) As IReadOnlyList(Of ConfigFieldDescriptor)
Function BuildGenerationSteps(values As Dictionary(Of String, String), ...) As GenerationPlan
```

Schema-driven "generate a file into a managed directory" — the user fills a
form (your schema), you return a plan of `InstallStep`s plus
`ExpectedOutputRelativePath`; the node runs it as a tracked operation.
Factorio's map generation (presets, seed, save name) is the worked example.

### `IInstanceFileEditorProvider`

```vb
Function GetInstanceFileEditors(config As InstanceConfig) As IReadOnlyList(Of InstanceFileEditor)
Function ReadFileToValues(editorKey As String, ...) As Dictionary(Of String, String)
Function WriteValuesToFile(editorKey As String, ...) As String
```

Structured form editing of a known config file. Each editor declares
`RelativePath`, a `Schema`, and `RequiresExistingFile`; you translate file
content ↔ field values, which lets you preserve hand-added keys the form
doesn't know (Factorio's `server-settings.json` editor round-trips unknown
JSON fields untouched — copy that behavior).

### `IStartupFileProvider`

```vb
Function GetStartupFiles(instanceConfig As InstanceConfig) As IReadOnlyList(Of ...)
Function RenderStartupFile(relativePath As String, ...) As String
```

Files written on the node at every instance start, content rendered
Manager-side from current config. This is how Conan gets `ServerName` /
`ServerPassword` into `Engine.ini` and Windrose writes
`ServerDescription.json` — launch-time config that can't ride the command
line.

### `ISharedConfigProvider`, `ISourceLabelProvider`, `IConnectionBindingAware`, `IOrphanDetector`

Config groups shared across installations; custom Source-column labels in
History; connection-address binding awareness; orphaned-process detection.
Niche — see the contracts source.

---

## Utility plugins

`IUtilityPlugin` (namespace `GSM.Utility`) is the second plugin kind: no
installations, no instances — a background participant receiving Manager-wide
events.

- **Manifest is mandatory**, `requiresContracts="2"` minimum, and `PluginId`
  must match the manifest id.
- Declare needed capabilities in the manifest:
  `requires="events, identity-read, identity-write, notifications, network,
  config, web-capture"` (pick what you need). Undeclared access through the
  context throws a named error.
- Events arrive via `HandleEventAsync` on a bounded queue (256, drop-oldest).
  **Five consecutive unhandled exceptions suspends the plugin** until the next
  reload (visible in Plugin Status) — handle your errors.
- `UtilityEvent` carries resolved identity: `CharacterId`, `PlatformUserId`,
  `Platform`, `CharacterName` (resolved name), raw `PlayerName` (persona), and
  the instance's `SessionIdentity` (`lastoasis:{realm}:{tile}` on LO;
  `{gameId}:{instanceId}` elsewhere). Synthetic leaves (stop-flush, downtime
  reconcile) are delivered.
- **Web-session capture**: `CaptureWebSessionAsync(startUrl,
  completionUrlPattern, cookieDomain)` opens a Manager-owned WebView2 login
  dialog and harvests cookies for the domain — including **HttpOnly** session
  cookies. Sessions live in the Manager's shared encrypted store under your
  chosen key; any plugin using the same key shares the session. Call
  `InvalidateWebSession` when you detect expiry. (Requires the WebView2
  runtime on the Manager machine; absence is reported, not a crash.)

`LoMyrealmPlugin.vb` is the worked example for all of the above.

### Capabilities are consent, not a sandbox

The `requires` list is **informed consent**, not containment — plugins are
full-trust code. Two real static gates back it up: a capability-declaring
plugin *without* `network` is compiled against a reference set with
`System.Net.*` stripped (undeclared network use = compile error), and staged
plugin source is scanned for DllImport / `Process.Start` / reflection, with
findings surfaced in the install-consent dialog. The actual defenses are
provenance, readable source, and the fact that nothing ever auto-installs.

---

## Testing your plugin

1. Drop the file in `Plugins\`, **Tools → Reload Plugins**, fix compile errors
   via *Manage Plugins → Status*.
2. Create a real installation against a test Node and run the full lifecycle:
   install → configure instance → start → watch logs → stop → update.
3. Verify your parse rules against **real log captures** — regex written from
   memory of a game's log format is the most common plugin bug. Check
   `/api/instances/{id}/players` on the Node reflects joins/leaves.
4. Test on both platforms if you claim both; launch arguments and binary
   names commonly differ.

---

## Distributing

Plugins distribute through **plugin sources** — GitHub repos the Manager
browses (*Manage Plugins → Sources*). To publish:

- Put your `.vb` file(s) in a repo folder; only files with a `<plugin>`
  manifest are catalogued.
- Version bumps in the manifest are what users' update checks see.
- Users always review-and-consent; nothing auto-installs or auto-updates.
- Use a `{yourGitHubName}_` id prefix to avoid colliding with official ids.

To propose a plugin for the official source, PR it into `GSM.PluginsSource/`
on `siteml/PowerGSM` — official-ness is a property of the source, and the
`author` field keeps crediting you.

---

Next: the [API & protocol guide](api-protocol.md) — what the Node actually
receives from all of this, and how to build your own Manager.
