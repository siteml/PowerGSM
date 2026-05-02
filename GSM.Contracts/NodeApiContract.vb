Imports System
Imports System.Collections.Generic
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
        Public Property Name As String
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
        Public Property PlayerName As String
        Public Property Text As String
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

        ' ---- Node ----
        Function GetStatusAsync(cancellation As CancellationToken) As Task(Of NodeStatusResponse)
        Function AuthenticateAsync(request As NodeAuthRequest, cancellation As CancellationToken) As Task(Of NodeAuthResponse)

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

        ' ---- Interactive prompts ----
        Function RespondToPromptAsync(response As PromptResponse, cancellation As CancellationToken) As Task(Of Boolean)
        Function UninstallAsync(request As UninstallRequest, cancellation As CancellationToken) As Task(Of Boolean)

    End Interface

End Namespace