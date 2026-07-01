Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin

' ============================================================
'  GSM Node API Contract
'
'  All request/response DTOs shared between the Manager (client)
'  and the Node (server). The Manager serialises these to JSON
'  and sends them over HTTP. The Node deserialises and acts on
'  them.
'
'  Log streaming uses SSE (Server-Sent Events) consumed via
'  StreamReader with a callback Action(Of LogLine) — not
'  IAsyncEnumerable (unsupported in VB.Net).
' ============================================================

Namespace GSM.Node.Api

    ' ============================================================
    '  Version constants
    '
    '  These are the canonical integers driving phase 5f's three-
    '  axis versioning scheme. See VERSIONING.md at the solution
    '  root for the full policy. Both bump only on BREAKING changes:
    '  additive changes (new endpoints, new optional DTO fields,
    '  new interface members) do NOT bump them.
    '
    '  ProtocolVersion governs the Manager↔Node REST contract.
    '  Node returns it from /api/version; Manager compares against
    '  its own compiled-in copy on connect (mismatch surfacing
    '  ships in phase 5f-2).
    '
    '  ContractsVersion governs plugin-facing types in GSM.Contracts.
    '  Plugins declare what version they target via a magic comment;
    '  Manager validates on load (validation ships in phase 5f-3).
    '
    '  These live in NodeApiContract.vb because both Manager and Node
    '  need them and Contracts is referenced by everything. Bumping
    '  is one edit per integer.
    ' ============================================================

    ''' <summary>
    ''' Canonical version integers shared by Manager and Node. See
    ''' the comment block above and VERSIONING.md for the full policy.
    ''' </summary>
    Public Module NodeApiContract

        ''' <summary>
        ''' Manager↔Node REST contract version. Bumps only when the
        ''' wire shape changes in a breaking way (endpoint removed,
        ''' DTO field removed/renamed/semantically changed). New
        ''' endpoints and new optional fields do NOT bump it.
        ''' </summary>
        Public Const ProtocolVersion As Integer = 1

        ''' <summary>
        ''' Plugin-facing contracts version. Bumps only when types
        ''' in GSM.Contracts change in a breaking way (member removed,
        ''' signature changed, enum member removed) — OR when a major
        ''' new plugin-facing surface is introduced (e.g. the Phase 7
        ''' utility-plugin kind) so plugins requiring it can fail fast
        ''' on older Managers. Routine new members and new types do
        ''' NOT bump it. v2 = utility-plugin surface (GSM.Utility) +
        ''' startup config render (IStartupFileProvider). v2 has not
        ''' shipped, so the render surface folds into it rather than
        ''' bumping — there's no released v2 Manager to gate against.
        ''' (Additive members on existing plugin-facing interfaces —
        ''' e.g. the 7-5 shared web-session methods on IUtilityContext
        ''' — are routine and do NOT bump: their only consumer ships
        ''' with them, so there's no version skew to gate.)
        ''' </summary>
        Public Const ContractsVersion As Integer = 2

    End Module

    ' ============================================================
    '  Enums
    ' ============================================================

    ''' <summary>
    ''' Standard error codes returned by the node REST API.
    ''' </summary>
    Public Enum NodeErrorCodes
        None
        InstanceNotFound
        InstanceAlreadyRunning
        InstanceAlreadyStopped
        InstallationNotFound
        InstallationInProgress
        InstallationLocked
        RconNotConnected
        RconCommandFailed
        AuthenticationFailed
        InternalError
        InvalidRequest
        DiskSpaceInsufficient
        ProcessStartFailed
    End Enum

    ''' <summary>
    ''' State of a long-running installation operation on the node.
    ''' </summary>
    Public Enum InstallationOperationState
        Queued
        Downloading
        Extracting
        Configuring
        Validating
        WaitingForInput
        Completed
        Failed
        Cancelled
    End Enum

    ''' <summary>
    ''' Type of interactive prompt that a step may require.
    ''' Used by SteamCMD when guard codes or 2FA are needed.
    ''' </summary>
    Public Enum PromptType
        SteamGuardCode
        TwoFactorCode
        Password
        Confirmation
    End Enum

    ''' <summary>
    ''' Source of log lines as reported by the node.
    ''' </summary>
    Public Enum LogSourceType
        Stdout
        Stderr
        FileWatcher
        SystemEvent
    End Enum

    ' ============================================================
    '  Node identity and status
    ' ============================================================

    ''' <summary>
    ''' Response shape from GET /api/version. Unauthenticated and
    ''' lightweight — used both as a connectivity probe and to
    ''' negotiate the three version axes (build, protocol,
    ''' contracts) at connect time. See VERSIONING.md.
    '''
    ''' The wire format also carries an "application" string and
    ''' a "runtime" string for diagnostics. The legacy "version"
    ''' field is preserved as an alias for "build" so any pre-
    ''' 5f-1 manager calling against a current node still gets a
    ''' usable answer. Older nodes that don't know about Protocol/
    ''' ContractsVersion will leave those fields at zero.
    ''' </summary>
    Public Class NodeVersionResponse
        Public Property Application As String

        ''' <summary>
        ''' Legacy alias for Build. Pre-5f-1 nodes only populated
        ''' this field; current nodes populate both with the same
        ''' value. Prefer Build for new code.
        ''' </summary>
        Public Property Version As String

        Public Property Build As String

        ''' <summary>
        ''' Manager↔Node REST contract version. Compare against
        ''' NodeApiContract.ProtocolVersion to decide compatibility.
        ''' Zero indicates a pre-5f-1 node that didn't carry this
        ''' field — treat as "older than this manager".
        ''' </summary>
        Public Property ProtocolVersion As Integer

        ''' <summary>
        ''' Plugin contracts version on the node side. Mostly
        ''' informational on the Manager since plugins run
        ''' Manager-side; surfaced for diagnostics. Zero indicates
        ''' a pre-5f-1 node.
        ''' </summary>
        Public Property ContractsVersion As Integer

        Public Property Runtime As String

        ''' <summary>
        ''' OS platform of the node, populated from RuntimeInformation
        ''' on the node side. NodePlatform.Unknown when the node is
        ''' older than this contract addition. Carried here on the
        ''' /api/version response so the Manager picks it up via the
        ''' existing NodeHttpClient version cache without an extra
        ''' round trip.
        ''' </summary>
        Public Property Platform As NodePlatform
    End Class

    Public Class NodeStatusResponse
        Public Property NodeId As String
        Public Property MachineName As String
        Public Property OsDescription As String
        Public Property UptimeSeconds As Long
        Public Property CpuPercent As Double
        Public Property MemoryUsedMb As Long
        Public Property MemoryTotalMb As Long
        Public Property DiskFreeGb As Long
        Public Property RunningInstanceCount As Integer
        Public Property NodeVersion As String

        ''' <summary>
        ''' Absolute path to the directory the node prefers as the
        ''' parent of game-server installations. The manager uses this
        ''' to suggest install paths on the New Installation form
        ''' ("{ServersDirectory}/{gameId}") rather than hardcoding
        ''' "C:\GameServers\" — which doesn't make sense for Linux
        ''' nodes and clashes with the per-machine convention of
        ''' keeping data inside the node's own working tree.
        '''
        ''' Resolved on the node side via NodeConfiguration. May be
        ''' empty if the node is older than this contract — the
        ''' manager falls back to a generic placeholder in that case.
        ''' </summary>
        Public Property ServersDirectory As String

        ''' <summary>
        ''' OS platform of the node. Same value as the matching
        ''' field on NodeVersionResponse — mirrored here so callers
        ''' that already hit /api/status (NodePanel, etc.) don't
        ''' need to issue a second request just to learn the
        ''' platform. Stays Unknown on nodes older than this
        ''' contract addition.
        ''' </summary>
        Public Property Platform As NodePlatform
    End Class

    ' ============================================================
    '  Instance management
    ' ============================================================

    Public Class StartInstanceRequest
        Public Property InstanceId As String
        Public Property ExePath As String
        Public Property Arguments As String
        Public Property WorkingDirectory As String
        Public Property EnvironmentVars As Dictionary(Of String, String)
        Public Property CrashPolicy As CrashRestartPolicy
        Public Property MaxCrashCount As Integer = 5
        Public Property CrashWindowMinutes As Integer = 60

        ''' <summary>
        ''' If the instance stays in Running state continuously for at
        ''' least this many seconds after a (re)start, the in-memory
        ''' CrashCount is reset to 0. Lets a long-lived server return
        ''' to a clean backoff baseline after a past hiccup.
        ''' 0 disables the reset.
        ''' </summary>
        Public Property CrashCountResetAfterSeconds As Integer = 300

        ''' <summary>
        ''' Floor applied to the restart backoff delay. The computed
        ''' backoff (2^crashCount seconds for RestartWithBackoff) is
        ''' clamped to be at least this many milliseconds. Primary
        ''' use is ensuring the Crashed state stays visible long
        ''' enough for the manager's 3-second poller to observe it
        ''' before the node auto-restarts — without this, fast
        ''' first-crash restarts can slip between two polls and the
        ''' crash notification gets skipped.
        ''' 0 = no floor (raw backoff formula).
        ''' </summary>
        Public Property MinRestartDelayMs As Integer = 0

        Public Property RconPort As Integer?
        Public Property RconPassword As String
        Public Property RconProtocol As RconProtocol

        ''' <summary>
        ''' Absolute file paths the node should tail and merge into the
        ''' instance's log buffer. Useful for engines that suppress parts
        ''' of their logging from stdout (e.g. UE4 LogNet) but write
        ''' everything to a file.
        ''' </summary>
        Public Property LogFilePaths As List(Of String)

        ''' <summary>
        ''' Declarative regex rules the node applies to every log line
        ''' (from either stdout or tailed files). Produces structured
        ''' events that feed into the node's player/state/chat stores.
        ''' Lets the node track server activity without the plugin loaded.
        ''' </summary>
        Public Property LogParseRules As List(Of LogParseRule)

        ''' <summary>
        ''' True when the plugin's stdout is the authoritative log
        ''' stream and the node should capture it for the manager's
        ''' log buffer. Set by the manager from
        ''' LaunchOptions.StdoutIsLog (when the plugin implements
        ''' ILaunchOptionsProvider), False otherwise. When False,
        ''' the node decides between Strategy A (no LogFilePaths)
        ''' and the appropriate Strategy B/C (LogFilePaths
        ''' declared) based on RequiresConsoleIsolation and
        ''' LogFilePaths.
        ''' </summary>
        Public Property StdoutIsLog As Boolean = False

        ''' <summary>
        ''' True when the plugin needs the node to insulate its
        ''' game executable from the node's own console — for games
        ''' that defeat CREATE_NEW_CONSOLE by reattaching to their
        ''' parent's console at startup. Set by the manager from
        ''' LaunchOptions.RequiresConsoleIsolation. The node
        ''' implements this today by spawning through cmd.exe so
        ''' the game's parent (and thus reattach target) is cmd's
        ''' hidden console rather than the node's terminal.
        ''' Ignored when StdoutIsLog is True.
        ''' </summary>
        Public Property RequiresConsoleIsolation As Boolean = False

        ''' <summary>
        ''' Per-instance override for the file-tailer's startup
        ''' delay (the wait after a tailed log file first appears
        ''' on disk before the node opens it for reading). Set by
        ''' the manager from the plugin's LaunchOptions when
        ''' provided. Negative means "plugin did not specify" — the
        ''' node falls back to its 5000ms legacy default. 0 means
        ''' the plugin explicitly opted into immediate tailing
        ''' (Factorio-class engines that crash faster than the
        ''' legacy delay would tolerate).
        ''' </summary>
        Public Property LogTailerStartDelayMs As Integer = -1
    End Class

    Public Class StopInstanceRequest
        Public Property InstanceId As String
        Public Property GracefulTimeoutMs As Integer = 10000
    End Class

    Public Class InstanceStatusResponse
        Public Property InstanceId As String
        Public Property CurrentState As InstanceState
        Public Property Pid As Integer?

        ''' <summary>
        ''' PID of the per-instance GSM.Shim supervisor when the instance
        ''' runs under one (Phase 8-1); Nothing for directly-spawned
        ''' instances. Pid stays the GAME pid in both modes. Optional/
        ''' additive — older managers ignore it.
        ''' </summary>
        Public Property SupervisorPid As Integer?
        Public Property UptimeSeconds As Long
        Public Property CpuPercent As Double
        Public Property MemoryMb As Long
        Public Property CrashCount As Integer
        Public Property LastExitCode As Integer?
        Public Property StateChangedAt As DateTime
        Public Property ErrorMessage As String
    End Class

    ' ============================================================
    '  Installation / update operations
    ' ============================================================

    Public Class InstallRequest
        Public Property InstallationId As String
        Public Property GameId As String
        Public Property InstallPath As String
        Public Property Steps As List(Of InstallStep)
        Public Property SteamCredentials As SteamCredential

        ''' <summary>
        ''' When true, the node will execute every .exe found under
        ''' _CommonRedist in the install directory after SteamCMD
        ''' completes. Requires administrator on the node — leave
        ''' off unless the node service is elevated, otherwise each
        ''' redist triggers a UAC prompt.
        ''' </summary>
        Public Property RunCommonRedist As Boolean = False
    End Class

    ''' <summary>
    ''' Ask the node what the currently-installed Steam buildid is for
    ''' a given installation, and what the latest available buildid is
    ''' from SteamCMD app_info. Used for non-destructive update checks.
    ''' </summary>
    Public Class AppVersionCheckRequest
        Public Property InstallationId As String
        Public Property InstallPath As String
        Public Property AppId As Integer
        Public Property BetaBranch As String
        Public Property SteamCredentials As SteamCredential
    End Class

    Public Class AppVersionCheckResponse
        ''' <summary>Buildid from appmanifest_&lt;appid&gt;.acf on disk.</summary>
        Public Property InstalledBuildId As String
        ''' <summary>Buildid from SteamCMD app_info for the branch.</summary>
        Public Property LatestBuildId As String
        Public Property UpdateAvailable As Boolean
        Public Property ErrorMessage As String
    End Class

    Public Class InstallProgressResponse
        Public Property InstallationId As String
        Public Property OperationState As InstallationOperationState
        Public Property CurrentStepIndex As Integer
        Public Property TotalSteps As Integer
        Public Property CurrentStepName As String
        Public Property ProgressPercent As Double
        Public Property Message As String
        Public Property ErrorMessage As String
        Public Property PendingPromptType As PromptType?
        Public Property PendingPromptMessage As String

        ''' <summary>
        ''' Current SteamCMD phase, parsed from the node's tail of
        ''' SteamCMD's own content_log.txt during a SteamCMD step.
        ''' Free-form string lifted verbatim from the log line
        ''' ("downloading", "verifying update", "reconfiguring",
        ''' "committing", etc.) — we don't normalize to an enum
        ''' because SteamCMD's phase taxonomy isn't documented and
        ''' shifts between versions, so a permissive pass-through
        ''' is more robust than a translation table that goes
        ''' stale. Nothing outside SteamCMD steps and before the
        ''' first progress line is parsed.
        '''
        ''' This whole field group exists because SteamCMD's stdout
        ''' is block-buffered when redirected (libc detects no tty
        ''' and switches off line buffering), so capturing progress
        ''' from stdout is hopeless without ConPTY. SteamCMD's own
        ''' logging code in content_log.txt does flush per write,
        ''' though, so we tail that file instead. See the tailer
        ''' implementation in InstallRunner for the wire-up.
        ''' </summary>
        Public Property SteamCmdPhase As String

        ''' <summary>
        ''' Bytes downloaded so far for the current SteamCMD step,
        ''' parsed from content_log.txt. Nothing when no progress
        ''' line has been observed yet (and outside SteamCMD steps).
        ''' </summary>
        Public Property BytesDownloaded As Long?

        ''' <summary>
        ''' Total bytes for the current SteamCMD step. Nothing when
        ''' no progress line has been observed yet, or when SteamCMD
        ''' reports 0 — some early phases (reconfiguring, validating
        ''' against an already-installed copy) emit progress lines
        ''' with a 0/0 byte count, which we treat as "not meaningful".
        ''' </summary>
        Public Property BytesTotal As Long?

        ''' <summary>
        ''' For successful SteamCMD-based installs, the build id
        ''' the node read out of appmanifest_{appid}.acf right
        ''' after the SteamCMD step completed. Lets the manager
        ''' stamp InstalledVersion in the same
        ''' "steam:{appId}@{branch} build {N}" format that
        ''' VersionCheckService produces from app_info_print, so
        ''' the version-comparison flips to "up to date"
        ''' immediately after an install — without the
        ''' fire-and-forget upgrade pass that previously left a
        ''' synthetic "steam:{appId} (timestamp)" placeholder
        ''' visible for the 10-20s window it took to query
        ''' upstream.
        '''
        ''' Empty/null when the install was a non-SteamCMD method
        ''' (DirectDownload, Manual), when SteamCMD failed before
        ''' producing the ACF, or when the node is older than this
        ''' field. Old nodes silently drop the unknown property
        ''' on the wire so adding this is backward-compatible.
        ''' </summary>
        Public Property InstalledBuildId As String
    End Class

    Public Class SteamCredential
        Public Property Username As String
        Public Property Password As String
        Public Property IsAnonymous As Boolean
    End Class

    Public Class UninstallRequest
        Public Property InstallationId As String
        Public Property InstallPath As String
        Public Property DeleteFiles As Boolean = False
    End Class

    ' ============================================================
    '  RCON
    ' ============================================================

    Public Class RconCommandRequest
        Public Property InstanceId As String
        Public Property Command As String
    End Class

    Public Class RconCommandResponse
        Public Property InstanceId As String
        Public Property Response As String
        Public Property Success As Boolean
        Public Property ErrorMessage As String
    End Class

    Public Class RconStatusResponse
        Public Property InstanceId As String
        Public Property IsConnected As Boolean
        Public Property Protocol As RconProtocol
    End Class

    ' ============================================================
    '  Log streaming (callback-based, not IAsyncEnumerable)
    ' ============================================================

    Public Class LogStreamRequest
        Public Property InstanceId As String
        Public Property TailLines As Integer = 100
        Public Property Follow As Boolean = True
        Public Property Sources As List(Of LogSourceType)
    End Class

    ' ============================================================
    '  Authentication
    ' ============================================================

    Public Class NodeAuthRequest
        Public Property SharedSecret As String
        Public Property ManagerId As String
    End Class

    Public Class NodeAuthResponse
        Public Property Accepted As Boolean
        Public Property SessionToken As String
        Public Property Reason As String
    End Class

    ' ============================================================
    '  Interactive prompt (SteamCMD guard code, 2FA, etc)
    ' ============================================================

    Public Class PromptRequest
        Public Property OperationId As String
        Public Property PromptKind As PromptType
        Public Property Message As String
    End Class

    Public Class PromptResponse
        Public Property OperationId As String
        Public Property Value As String
        Public Property Cancelled As Boolean
    End Class

    ' ============================================================
    '  Error envelope
    ' ============================================================

    Public Class NodeErrorResponse
        Public Property ErrorCode As NodeErrorCodes
        Public Property Message As String
        Public Property Detail As String
    End Class

    ' ============================================================
    '  INodeClient — interface for manager-side node communication
    ' ============================================================

    ' ============================================================
    '  Parsed event DTOs — populated from log parsing, exposed via
    '  node endpoints so Managers can see current state at any time.
    ' ============================================================

    Public Class PlayerSession
        ''' <summary>
        ''' Steam handle / Xbox gamertag — what the platform reports
        ''' for this account. Known immediately on join from the
        ''' login URL's Name parameter. For Factorio this also matches
        ''' the in-game name (no separate character identity); for
        ''' Last Oasis it's the Steam persona which may differ from
        ''' the renamed in-game character (see DisplayName below).
        ''' </summary>
        Public Property PlatformPersona As String

        ''' <summary>
        ''' In-game character display name — what shows in chat and
        ''' on join/leave messages. For Last Oasis this comes from
        ''' the LogPersistence "Persisting &lt;name&gt;" line and may
        ''' differ from PlatformPersona if the player renamed their
        ''' character via myrealm. Nothing until the first persistence
        ''' tick lands (~2 min from join). UIs default to
        ''' DisplayName ?? PlatformPersona when surfacing
        ''' "who is this player".
        ''' </summary>
        Public Property DisplayName As String

        Public Property Platform As String
        Public Property PlatformUserId As String   ' SteamID64, Xbox GUID, etc.
        Public Property CharacterId As String
        Public Property RemoteAddress As String
        Public Property JoinedUtc As DateTime
    End Class

    Public Class ServerStateResponse
        Public Property MatchState As String
        Public Property CurrentMapPath As String
        Public Property TileId As String
        Public Property TileName As String
        Public Property BackendRegistered As Boolean
        Public Property LastUpdatedUtc As DateTime

        ''' <summary>
        ''' Plugin-defined runtime state harvested from log lines. Any
        ''' parse rule whose regex includes a named capture group whose
        ''' name starts with "Custom_" produces an entry here, with the
        ''' prefix stripped — e.g. "(?&lt;Custom_InviteCode&gt;[A-Z0-9]+)"
        ''' yields key "InviteCode". Lets plugins surface game-specific
        ''' state without growing this contract every time a new field
        ''' is needed. Keys are case-insensitive. Nothing/null when no
        ''' custom fields have been observed for this instance.
        ''' </summary>
        Public Property CustomFields As Dictionary(Of String, String)
    End Class

    Public Class ChatMessage
        Public Property TimestampUtc As DateTime

        ''' <summary>
        ''' In-game character name as it appeared in the chat line.
        ''' Stored as-text per message — historical entries retain
        ''' the name the player was using when they spoke, even if
        ''' they've renamed their character since. Always populated.
        ''' </summary>
        Public Property DisplayName As String

        ''' <summary>
        ''' Resolved Steam/Xbox/etc. ID for the speaker, attached via
        ''' EventStore lookup by DisplayName at the moment the chat
        ''' line was parsed. Nothing for messages whose speaker had
        ''' not yet been identity-resolved (chat arriving before the
        ''' first Persisting tick on a new join). Historical messages
        ''' written before this contract addition will be Nothing.
        ''' </summary>
        Public Property PlatformUserId As String

        ''' <summary>
        ''' Resolved CharacterId for the speaker, same provenance as
        ''' PlatformUserId. Same Nothing-on-race-or-old-history caveat.
        ''' </summary>
        Public Property CharacterId As String

        Public Property Text As String
    End Class

    ' ============================================================
    '  File operations — wire DTOs for /api/instances/{id}/files
    '
    '  Phase 4c-1: file CRUD endpoints scoped to an instance's
    '  install directory, validated against a manager-supplied
    '  whitelist of root subdirectories (saves/, config/, mods/,
    '  etc.) sourced from the plugin's IManagedDirectoriesProvider.
    '
    '  Wire shape is intentionally minimal — relative path back
    '  to the install root, byte size, last-write timestamp.
    '  Listings are non-recursive and return files only;
    '  subdirectories are not traversed.
    ' ============================================================

    ''' <summary>
    ''' One file entry returned by the listing endpoint. Returned
    ''' as a JSON array. Also returned individually as the success
    ''' body of the upload endpoint so the manager can refresh its
    ''' view without re-listing.
    ''' </summary>
    Public Class FileEntry
        ''' <summary>
        ''' Path relative to the installation root, using forward
        ''' slashes regardless of node OS. e.g. "saves/foo.zip".
        ''' Stable wire format the manager can compare verbatim
        ''' against the relative paths it constructed when issuing
        ''' the request.
        ''' </summary>
        Public Property RelativePath As String

        Public Property SizeBytes As Long
        Public Property ModifiedUtc As DateTime
    End Class

    ' ============================================================
    '  Map generation — wire DTOs for /api/instances/{id}/generate-map
    '
    '  Phase 4c-3: lets a manager-side plugin (via
    '  IFileGenerationProvider) ship a step list to the node for
    '  one-off file-producing operations. Distinct from the
    '  install runner because (a) we don't want a half-failed
    '  generation to look like a half-failed install in the
    '  operations history, (b) the step types we need are a
    '  strict subset (WriteFileStep + RunProcessStep — no
    '  SteamCMD, no archive extraction), and (c) the operation
    '  runs against an existing install and so doesn't need
    '  credential handling or the install lifecycle states.
    '
    '  NAMING NOTE: "map" / "GenerateMap" in the type names and
    '  endpoint URL is historical. The original IMapGenerationProvider
    '  was generalised to IFileGenerationProvider to support any
    '  schema-driven file-producing operation (not just maps), but
    '  the wire shape didn't change so we kept the old names to
    '  avoid a breaking change for already-deployed nodes. Read
    '  these as "GenerateFile" — the runner doesn't care what the
    '  steps produce.
    '
    '  The endpoint runs synchronously — the request blocks until
    '  the steps complete or the timeout fires. Map generation on
    '  Factorio's default-sized worlds takes seconds; ribbon worlds
    '  with extreme widths can take a couple of minutes. Manager
    '  uses a long-timeout one-shot HttpClient (same pattern as
    '  upload) to wait for the response.
    ' ============================================================

    Public Class GenerateMapRequest
        ''' <summary>
        ''' Absolute path to the install directory on the node
        ''' (e.g. "C:\GameServers\factorio"). The node executes the
        ''' steps relative to this directory. Travels in the
        ''' request body rather than as a query parameter for
        ''' parity with the rest of the body-typed contract.
        ''' </summary>
        Public Property InstallPath As String

        ''' <summary>
        ''' Steps the node executes against the install directory.
        ''' Built by the plugin's IMapGenerationProvider on the
        ''' Manager side; node treats them as opaque DTOs and runs
        ''' them sequentially. The endpoint validates that every
        ''' step is one of the supported types (currently
        ''' WriteFileStep, RunProcessStep) and rejects the request
        ''' with 400 BadRequest otherwise.
        ''' </summary>
        Public Property Steps As List(Of InstallStep)

        ''' <summary>
        ''' Hard timeout for the entire step sequence in seconds.
        ''' 0 or negative falls back to the node's default (300s).
        ''' Per-RunProcessStep TimeoutMs is honoured independently;
        ''' this caps the whole sequence so a stuck step doesn't
        ''' tie up the connection forever.
        ''' </summary>
        Public Property TimeoutSeconds As Integer = 0

        ''' <summary>
        ''' Optional: relative path of the file the steps are
        ''' expected to produce (e.g. "saves/foo.zip"). The node
        ''' echoes it back in the response on success and uses it
        ''' to verify the file actually appeared on disk — a
        ''' RunProcessStep that exits 0 but produced no output is
        ''' detected as a failure here. Leave empty to skip the
        ''' verification step.
        ''' </summary>
        Public Property ExpectedOutputRelativePath As String
    End Class

    Public Class GenerateMapResponse
        ''' <summary>
        ''' True when every step ran to completion and (if
        ''' specified) ExpectedOutputRelativePath exists on disk
        ''' afterwards.
        ''' </summary>
        Public Property Success As Boolean

        ''' <summary>
        ''' Echoed back from the request when present and the
        ''' file exists on disk. Lets the Manager UI verify the
        ''' new save is where it expects without a separate
        ''' listing call.
        ''' </summary>
        Public Property OutputRelativePath As String

        Public Property OutputSizeBytes As Long

        ''' <summary>
        ''' Index of the step that failed, or -1 on full success.
        ''' </summary>
        Public Property FailedStepIndex As Integer = -1

        ''' <summary>
        ''' Human-readable error — the failing step's exception
        ''' message, or the engine's stderr for a RunProcessStep
        ''' that exited non-zero. Empty on success.
        ''' </summary>
        Public Property ErrorMessage As String

        ''' <summary>
        ''' Captured stdout from any RunProcessStep that ran.
        ''' Useful for surfacing engine-side details (the
        ''' "generated map XYZ tiles" line on Factorio, etc.).
        ''' Truncated to 16KB to bound the response size.
        ''' </summary>
        Public Property Output As String
    End Class

    ' ============================================================
    '  Prerequisite checks (Phase 5g side-feature)
    '
    '  Host-side runtime-dependency probing: the Manager declares
    '  named prereqs via the plugin's IPrerequisiteProvider, the
    '  node returns whether each is installed plus user-facing
    '  display fields. The node owns the catalog so adding new
    '  prereqs doesn't bump the plugin-contracts version.
    ' ============================================================

    ''' <summary>
    ''' One entry in a PrerequisiteCheckResponse — the node returns
    ''' one of these per name in the request, in the same order.
    '''
    ''' Recognized=False means the node's catalog doesn't know this
    ''' name (older node, newer plugin); Installed and the display
    ''' fields are meaningless in that case and the Manager silently
    ''' skips the result.
    '''
    ''' Recognized=True + Installed=True means the runtime is present;
    ''' Manager renders nothing. Recognized=True + Installed=False is
    ''' the case that drives a pre-install Warning notice; Manager
    ''' renders it using DisplayName / DownloadUrl / Instructions.
    ''' </summary>
    Public Class PrerequisiteCheckResult
        Public Property Name As String
        Public Property Recognized As Boolean
        Public Property Installed As Boolean

        ''' <summary>
        ''' Detected version string when known (e.g. "14.38.33135.0"
        ''' for VC++). Empty when not detected or not applicable.
        ''' Currently surfaces only in diagnostics; the notice fires
        ''' off Installed alone.
        ''' </summary>
        Public Property Version As String

        ''' <summary>
        ''' Human-readable name suitable for a notice title, e.g.
        ''' "Microsoft Visual C++ 2015-2022 Redistributable (x64)".
        ''' </summary>
        Public Property DisplayName As String

        ''' <summary>
        ''' Direct download URL for the missing runtime. Typically
        ''' an aka.ms short link that resolves to Microsoft's latest
        ''' installer for that runtime line.
        ''' </summary>
        Public Property DownloadUrl As String

        ''' <summary>
        ''' Body text for the pre-install notice. Plain prose; the
        ''' Manager appends the DownloadUrl on its own line when
        ''' rendering, so this string should NOT include the URL.
        ''' </summary>
        Public Property Instructions As String
    End Class

    ''' <summary>
    ''' Response from GET /api/system/prerequisites. Results list
    ''' is parallel to the request's names list — same count, same
    ''' order, one Result per name.
    ''' </summary>
    Public Class PrerequisiteCheckResponse
        Public Property Results As List(Of PrerequisiteCheckResult)
    End Class

    ''' <summary>
    ''' Interface for communicating with a GSM Node. The manager
    ''' resolves all game-specific logic via IGamePlugin, builds
    ''' plain DTOs, and sends them to the node through this interface.
    '''
    ''' Implementations handle HTTP transport, auth headers,
    ''' retry logic, and connection pooling.
    ''' </summary>
    Public Interface INodeClient

        ' ---- Host-side prerequisite checks (Phase 5g side-feature) ----

        ''' <summary>
        ''' Query the node for the install state of named host-side
        ''' runtime dependencies (Microsoft VC++ redistributable,
        ''' DirectX runtimes, etc). Used by the new-installation
        ''' flow to surface missing prereqs as pre-install notices
        ''' before the install attempt actually starts.
        '''
        ''' Names are opaque strings owned by the node's catalog;
        ''' the node returns Recognized=False for any it doesn't
        ''' know about (older node + newer plugin) and the Manager
        ''' silently skips those. Endpoint may 404 on pre-feature
        ''' nodes; callers should treat that as "no prereq info
        ''' available" (silently proceed without notices) rather
        ''' than failure — the prereq check is a quality-of-life
        ''' enhancement, not a gate.
        ''' </summary>
        Function CheckPrerequisitesAsync(names As IReadOnlyList(Of String),
                                          cancellation As CancellationToken) As Task(Of PrerequisiteCheckResponse)

        ' ---- Node ----
        Function GetStatusAsync(cancellation As CancellationToken) As Task(Of NodeStatusResponse)
        Function AuthenticateAsync(request As NodeAuthRequest, cancellation As CancellationToken) As Task(Of NodeAuthResponse)

        ''' <summary>
        ''' Hits the unauthenticated /api/version endpoint and
        ''' returns the node's identity + version axes. Used by
        ''' the Manager for connectivity probing and
        ''' protocol-compatibility negotiation. Implementations
        ''' may cache successful results in-memory; pass force=True
        ''' to bypass the cache.
        ''' </summary>
        Function GetApiVersionAsync(force As Boolean,
                                     cancellation As CancellationToken) As Task(Of NodeVersionResponse)

        ' ---- Instance lifecycle ----
        Function StartInstanceAsync(request As StartInstanceRequest, cancellation As CancellationToken) As Task(Of InstanceStatusResponse)
        Function StopInstanceAsync(request As StopInstanceRequest, cancellation As CancellationToken) As Task(Of InstanceStatusResponse)
        Function GetInstanceStatusAsync(instanceId As String, cancellation As CancellationToken) As Task(Of InstanceStatusResponse)
        Function GetAllInstanceStatusesAsync(cancellation As CancellationToken) As Task(Of IReadOnlyList(Of InstanceStatusResponse))

        ' ---- Installation ----
        Function StartInstallAsync(request As InstallRequest, cancellation As CancellationToken) As Task(Of InstallProgressResponse)

        ''' <summary>
        ''' Fast non-destructive version check. The node reads the local
        ''' appmanifest ACF and queries SteamCMD app_info for the latest.
        ''' </summary>
        Function CheckAppVersionAsync(request As AppVersionCheckRequest, cancellation As CancellationToken) As Task(Of AppVersionCheckResponse)
        Function GetInstallProgressAsync(installationId As String, cancellation As CancellationToken) As Task(Of InstallProgressResponse)
        Function CancelInstallAsync(installationId As String, cancellation As CancellationToken) As Task(Of Boolean)

        ' ---- RCON ----
        Function SendRconCommandAsync(request As RconCommandRequest, cancellation As CancellationToken) As Task(Of RconCommandResponse)
        Function GetRconStatusAsync(instanceId As String, cancellation As CancellationToken) As Task(Of RconStatusResponse)

        ' ---- Log streaming (callback-based) ----
        ''' <summary>
        ''' Connects to the node's SSE log stream and invokes
        ''' onLine for each received log line. Runs until cancelled.
        ''' </summary>
        Function StreamLogsAsync(instanceId As String,
                                 onLine As Action(Of LogLine),
                                 cancellation As CancellationToken) As Task

        ''' <summary>
        ''' Pulls the most recent N lines of log history from the node's
        ''' ring buffer for an instance. Used to populate log viewer
        ''' windows with context before the streaming connection delivers
        ''' new lines.
        ''' </summary>
        Function GetRecentLogsAsync(instanceId As String,
                                    count As Integer,
                                    cancellation As CancellationToken) As Task(Of IReadOnlyList(Of LogLine))

        ' ---- Parsed events: players, server state, chat ----

        ''' <summary>
        ''' Re-pushes the declarative parse rule set to the node
        ''' for an instance that is already running. The node
        ''' replaces the compiled rule list atomically while
        ''' preserving the in-memory player/server-state caches
        ''' for that instance — used by the Manager on reconnect
        ''' (after a node binary update or a Manager restart) so
        ''' running game processes don't need to be stopped and
        ''' restarted just to refresh rules. Returns silently on
        ''' nodes older than this contract (HTTP 404 surfaces as
        ''' NodeApiException with StatusCode = NotFound; callers
        ''' that want to support older nodes catch that
        ''' specifically and proceed).
        ''' </summary>
        Function UpdateParseRulesAsync(instanceId As String,
                                        rules As IList(Of LogParseRule),
                                        cancellation As CancellationToken) As Task

        ''' <summary>
        ''' Returns the list of players the node currently believes
        ''' are online for this instance, based on log parsing.
        ''' </summary>
        Function GetPlayersAsync(instanceId As String,
                                  cancellation As CancellationToken) As Task(Of IReadOnlyList(Of PlayerSession))

        ''' <summary>
        ''' Returns derived server state for this instance (match state,
        ''' current tile, backend registration) based on log parsing.
        ''' </summary>
        Function GetServerStateAsync(instanceId As String,
                                      cancellation As CancellationToken) As Task(Of ServerStateResponse)

        ''' <summary>
        ''' Returns stored chat messages for this instance, optionally
        ''' filtered by timestamp. Messages persist until the instance
        ''' is deleted.
        ''' </summary>
        Function GetChatHistoryAsync(instanceId As String,
                                      sinceUtc As DateTime?,
                                      limit As Integer,
                                      cancellation As CancellationToken) As Task(Of IReadOnlyList(Of ChatMessage))

        ' ---- File operations (Phase 4c-1) ----
        '
        '  Scoped to a single installation directory and validated
        '  against a manager-supplied whitelist of root subdirectories
        '  (sourced from the plugin's IManagedDirectoriesProvider) and
        '  optional extension allowlist. The wrapper does no validation
        '  of its own — every parameter is forwarded verbatim to the
        '  node, which is the security boundary.
        '
        '  installPath / path / allowedRoots / allowedExtensions are
        '  identical to the wire-level query parameters. instanceId
        '  is included on every call for symmetry with other endpoints
        '  and to support per-instance tokens (D2 — {InstanceId}
        '  reservation in ManagedDirectory.RelativePath); the node
        '  routes file ops by installPath, not by running instance
        '  state, so calls succeed regardless of whether the instance
        '  is currently up.

        ''' <summary>
        ''' List files in a managed subdirectory of an instance's
        ''' installation. Non-recursive — files only, no descent into
        ''' nested directories. When allowedExtensions is non-empty,
        ''' the node filters the returned list by extension.
        ''' </summary>
        Function ListFilesAsync(instanceId As String,
                                 installPath As String,
                                 path As String,
                                 allowedRoots As IReadOnlyList(Of String),
                                 allowedExtensions As IReadOnlyList(Of String),
                                 cancellation As CancellationToken) As Task(Of IReadOnlyList(Of FileEntry))

        ''' <summary>
        ''' Stream a single file from the node into the supplied
        ''' destination stream. The wrapper performs CopyToAsync; the
        ''' caller owns the stream's lifetime. For small files (config
        ''' JSON), a MemoryStream works. For large files (100MB+ saves),
        ''' pass a FileStream straight to disk to keep memory flat.
        ''' </summary>
        Function DownloadFileAsync(instanceId As String,
                                    installPath As String,
                                    path As String,
                                    allowedRoots As IReadOnlyList(Of String),
                                    allowedExtensions As IReadOnlyList(Of String),
                                    destination As Stream,
                                    cancellation As CancellationToken) As Task

        ''' <summary>
        ''' Upload a file by streaming the supplied source stream as
        ''' the request body. No buffering — a FileStream over a 100MB
        ''' save uploads without growing the manager's working set.
        ''' Returns the FileEntry the node persisted, suitable for
        ''' refreshing a listing without a follow-up call.
        '''
        ''' overwrite=False causes the node to reject when the
        ''' destination already exists; the wrapper surfaces this as
        ''' NodeApiException (the inner HttpRequestException carries
        ''' StatusCode = Conflict for callers that want to disambiguate).
        ''' </summary>
        Function UploadFileAsync(instanceId As String,
                                  installPath As String,
                                  path As String,
                                  allowedRoots As IReadOnlyList(Of String),
                                  allowedExtensions As IReadOnlyList(Of String),
                                  source As Stream,
                                  overwrite As Boolean,
                                  cancellation As CancellationToken) As Task(Of FileEntry)

        ''' <summary>
        ''' Delete a single file. Idempotent — returns False without
        ''' raising when the file is already gone, which lets the
        ''' UI's optimistic "delete then refresh" flow not fight a
        ''' concurrent deletion from another manager session.
        ''' </summary>
        Function DeleteFileAsync(instanceId As String,
                                  installPath As String,
                                  path As String,
                                  allowedRoots As IReadOnlyList(Of String),
                                  allowedExtensions As IReadOnlyList(Of String),
                                  cancellation As CancellationToken) As Task(Of Boolean)

        ''' <summary>
        ''' Atomically rename (or move within the install) a file.
        ''' Both `path` and `newPath` are validated server-side
        ''' against allowedRoots and allowedExtensions — a rename
        ''' across managed roots is permitted as long as both
        ''' endpoints satisfy the whitelist (UIs that constrain a
        ''' panel to one directory simply send only that root).
        ''' Returns the FileEntry of the renamed file under its new
        ''' name so the caller can update its listing without a
        ''' follow-up call. overwrite=False rejects when newPath
        ''' already exists; the wrapper surfaces this as
        ''' NodeApiException whose inner HttpRequestException
        ''' carries StatusCode = Conflict for callers that want
        ''' to disambiguate.
        ''' </summary>
        Function RenameFileAsync(instanceId As String,
                                  installPath As String,
                                  path As String,
                                  newPath As String,
                                  allowedRoots As IReadOnlyList(Of String),
                                  allowedExtensions As IReadOnlyList(Of String),
                                  overwrite As Boolean,
                                  cancellation As CancellationToken) As Task(Of FileEntry)

        ''' <summary>
        ''' Copy a file within the install. Source is preserved;
        ''' a new file appears under newPath. Useful for backup-
        ''' before-modify workflows (e.g. duplicating a save before
        ''' loading it on a server). Validation, allowedRoots, and
        ''' allowedExtensions semantics match RenameFileAsync. The
        ''' source and destination must differ — the node returns
        ''' 400 BadRequest for same-path requests rather than
        ''' silently no-op'ing, since copy-onto-self is unambiguously
        ''' a caller bug.
        ''' </summary>
        Function CopyFileAsync(instanceId As String,
                                installPath As String,
                                path As String,
                                newPath As String,
                                allowedRoots As IReadOnlyList(Of String),
                                allowedExtensions As IReadOnlyList(Of String),
                                overwrite As Boolean,
                                cancellation As CancellationToken) As Task(Of FileEntry)

        ' ---- Map generation (Phase 4c-3) ----

        ''' <summary>
        ''' Run a plugin-supplied step list against an instance's
        ''' install directory to produce a new map/save. Synchronous:
        ''' the call doesn't return until every step completes or
        ''' the timeout fires. Implementations should use a
        ''' long-timeout HttpClient (Timeout.InfiniteTimeSpan with a
        ''' caller-supplied CancellationToken) since map generation
        ''' can run for minutes on large worlds.
        ''' </summary>
        Function GenerateMapAsync(instanceId As String,
                                   request As GenerateMapRequest,
                                   cancellation As CancellationToken) As Task(Of GenerateMapResponse)

        ' ---- Interactive prompts ----
        Function RespondToPromptAsync(response As PromptResponse, cancellation As CancellationToken) As Task(Of Boolean)
        Function UninstallAsync(request As UninstallRequest, cancellation As CancellationToken) As Task(Of Boolean)

    End Interface

End Namespace