Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin

' ============================================================
'  Node REST API Contract
'
'  This file defines the complete HTTP API surface that every
'  GSM node exposes. The manager communicates exclusively
'  through this API - it never SSH's into nodes or touches
'  the filesystem directly.
'
'  All types in this file are used on both sides of the wire:
'    - Node: deserializes requests, serializes responses
'    - Manager: serializes requests, deserializes responses
'
'  Transport:
'    - HTTP/1.1 with JSON bodies (Content-Type: application/json)
'    - TLS required in production (self-signed cert acceptable
'      on LAN, managed by the node on first run)
'    - Default port: 8765 (configurable per node)
'    - Authentication: shared secret bearer token
'      Authorization: Bearer {token}
'      Token is configured on the node at setup time and stored
'      in the manager's node record. Tokens are never logged.
'
'  Log streaming:
'    - Server-Sent Events (SSE) on GET /instances/{id}/logs/stream
'    - Simple to consume in VB.Net with StreamReader on HttpClient
'    - No WebSocket dependency
'
'  Error responses:
'    All error responses use NodeErrorResponse with a stable
'    ErrorCode string so the manager can handle errors
'    programmatically without parsing message strings.
'
'  Versioning:
'    All endpoints are prefixed /api/v1/
'    The node reports its API version on GET /api/version
'    The manager checks compatibility on first connect.
'
'  Endpoint summary:
'    Node health:
'      GET  /api/version
'      GET  /api/v1/health
'
'    Instances:
'      GET  /api/v1/instances
'      GET  /api/v1/instances/{id}
'      POST /api/v1/instances/{id}/start
'      POST /api/v1/instances/{id}/stop
'      POST /api/v1/instances/{id}/restart
'      POST /api/v1/instances/{id}/kill          (force, no graceful)
'      GET  /api/v1/instances/{id}/metrics
'
'    Logs:
'      GET  /api/v1/instances/{id}/logs          (recent buffered lines)
'      GET  /api/v1/instances/{id}/logs/stream   (SSE live stream)
'
'    Stdin:
'      POST /api/v1/instances/{id}/stdin         (Steam Guard etc)
'
'    RCON:
'      POST /api/v1/instances/{id}/rcon/connect
'      POST /api/v1/instances/{id}/rcon/disconnect
'      GET  /api/v1/instances/{id}/rcon/status
'      POST /api/v1/instances/{id}/rcon/send
'
'    Installations:
'      GET  /api/v1/installations/{id}/status
'      POST /api/v1/installations/{id}/install
'      POST /api/v1/installations/{id}/update
'      POST /api/v1/installations/{id}/validate
'      POST /api/v1/installations/{id}/cancel    (cancel in-progress op)
'
'    Install interaction:
'      GET  /api/v1/installations/{id}/prompt    (poll for pending prompt)
'      POST /api/v1/installations/{id}/prompt    (respond to prompt)
'
'    Node system:
'      GET  /api/v1/system/info
'      GET  /api/v1/system/drives
' ============================================================

Namespace GSM.Node.Api


    ' ============================================================
    '  NODE HEALTH + VERSION
    ' ============================================================

    ' GET /api/version
    ' Not authenticated - used for initial compatibility check.
    Public Class NodeVersionResponse
        Public Property ApiVersion As String        ' e.g. "1.0"
        Public Property NodeVersion As String       ' GSM node build version
        Public Property RuntimeVersion As String    ' .NET runtime version
        Public Property Os As String               ' "Windows" or "Linux"
        Public Property Hostname As String
    End Class

    ' GET /api/v1/health
    ' Quick liveness check. Returns 200 OK if the node is running.
    Public Class NodeHealthResponse
        Public Property Status As String = "ok"
        Public Property UptimeSeconds As Long
        Public Property RunningInstanceCount As Integer
        Public Property ActiveInstallOperationCount As Integer
    End Class


    ' ============================================================
    '  INSTANCE ENDPOINTS
    ' ============================================================

    ' GET /api/v1/instances
    ' Returns the state of all instances the manager has registered
    ' on this node. The node tracks instances by ID - it learns
    ' about them when the manager sends a start request.
    Public Class NodeInstanceListResponse
        Public Property Instances As List(Of NodeInstanceSummary)
    End Class

    Public Class NodeInstanceSummary
        Public Property InstanceId As String
        Public Property DisplayName As String
        Public Property GameId As String
        Public Property State As InstanceState
        Public Property RconState As RconState
        Public Property PlayerCount As Integer      ' -1 = unknown
        Public Property UptimeSeconds As Long?      ' Nothing if not running
        Public Property Pid As Integer?             ' OS process ID if running
        Public Property LastStateChangeAt As DateTime
        Public Property CrashCountInWindow As Integer
        Public Property InstallationId As String
    End Class

    ' GET /api/v1/instances/{id}
    ' Full detail for one instance.
    Public Class NodeInstanceDetailResponse
        Public Property InstanceId As String
        Public Property DisplayName As String
        Public Property GameId As String
        Public Property State As InstanceState
        Public Property CrashDetectionState As CrashDetectionState
        Public Property RconState As RconState
        Public Property PlayerCount As Integer
        Public Property Players As List(Of PlayerInfo)
        Public Property CustomMetrics As Dictionary(Of String, String)
        Public Property UptimeSeconds As Long?
        Public Property Pid As Integer?
        Public Property ExeResolved As String       ' Actual exe path used
        Public Property CommandLineUsed As String   ' Actual args used at last start
        Public Property WorkingDirectory As String
        Public Property LastStateChangeAt As DateTime
        Public Property StateHistory As List(Of InstanceStateEvent)
        Public Property CrashHistory As List(Of CrashEventSummary)
        Public Property InstallationId As String
        Public Property StartupWarnings As List(Of String)  ' From IGamePlugin.GetStartupWarnings
    End Class

    Public Class InstanceStateEvent
        Public Property OccurredAt As DateTime
        Public Property FromState As InstanceState
        Public Property ToState As InstanceState
        Public Property Reason As String
    End Class

    Public Class CrashEventSummary
        Public Property OccurredAt As DateTime
        Public Property ExitCode As Integer
        Public Property Decision As RestartDecision
        Public Property DecisionReason As String
        Public Property AttemptNumber As Integer
    End Class

    ' POST /api/v1/instances/{id}/start
    ' The manager sends everything the node needs to launch the process.
    ' The node never calls plugin code - the manager resolves all
    ' plugin logic and sends the result as plain strings.
    Public Class StartInstanceRequest
        ' Process launch parameters - fully resolved by the manager.
        ' The node executes exactly what it receives here.
        Public Property ExecutablePath As String        ' Absolute path
        Public Property Arguments As String            ' Full arg string
        Public Property WorkingDirectory As String

        ' Identity - stored on the node for state tracking.
        Public Property InstanceId As String
        Public Property DisplayName As String
        Public Property GameId As String
        Public Property InstallationId As String

        ' Log source configuration - resolved from IGamePlugin.GetLogSources()
        Public Property LogSources As List(Of LogSourceConfig)

        ' Crash restart policy - pushed from manager, executed autonomously
        ' by the node even if the manager is unreachable.
        Public Property CrashRestartPolicy As CrashRestartPolicy

        ' RCON configuration - resolved from IGamePlugin.GetRconInfo()
        ' Nothing/null = no RCON for this instance.
        Public Property RconConfig As NodeRconConfig

        ' Crash signal patterns - from IGamePlugin.GetCrashSignalPatterns()
        ' Used by the node to pre-detect crashes in the log stream.
        Public Property CrashSignalPatterns As List(Of String)

        ' Exit codes that indicate a clean shutdown (not a crash).
        ' From IGamePlugin.GetCleanExitCodes()
        Public Property CleanExitCodes As List(Of Integer)

        ' Startup warnings to log before launch.
        ' From IGamePlugin.GetStartupWarnings()
        Public Property StartupWarnings As List(Of String)
    End Class

    ' Log source configuration sent to the node.
    ' Mirrors ILogSource but serialization-friendly.
    Public Class LogSourceConfig
        Public Property SourceId As String
        Public Property SourceType As LogSourceType     ' Stdout or File
        Public Property PathPattern As String           ' For File sources
        Public Property CaptureStderr As Boolean        ' For Stdout sources
        Public Property FollowRotation As Boolean       ' For File sources
    End Class

    Public Enum LogSourceType
        Stdout
        File
    End Enum

    ' RCON configuration sent to the node with a start request.
    Public Class NodeRconConfig
        Public Property Protocol As RconProtocol
        Public Property Port As Integer
        Public Property Password As String          ' Transmitted transiently, never persisted
        Public Property AutoConnect As Boolean
        Public Property StartupDelayMs As Integer
        Public Property MaxConnectRetries As Integer
        Public Property RetryIntervalMs As Integer
        Public Property ConnectTimeoutMs As Integer
        Public Property MaxPacketSize As Integer
    End Class

    ' Response to start request.
    Public Class StartInstanceResponse
        Public Property InstanceId As String
        Public Property State As InstanceState
        Public Property Pid As Integer?
        Public Property StartedAt As DateTime?
        Public Property Message As String
    End Class

    ' POST /api/v1/instances/{id}/stop
    Public Class StopInstanceRequest
        ' If True, send a graceful stop signal and wait for the process
        ' to exit cleanly up to GracefulTimeoutMs before force-killing.
        ' If False, kill immediately (SIGKILL / TerminateProcess).
        Public Property Graceful As Boolean = True
        Public Property GracefulTimeoutMs As Integer = 30000
    End Class

    Public Class StopInstanceResponse
        Public Property InstanceId As String
        Public Property State As InstanceState
        Public Property Message As String
    End Class

    ' POST /api/v1/instances/{id}/restart
    ' Combines stop + start. The node uses the last-known start
    ' parameters. The manager may send updated parameters to
    ' apply on the restart.
    Public Class RestartInstanceRequest
        Public Property Graceful As Boolean = True
        Public Property GracefulTimeoutMs As Integer = 30000
        ' If set, replace the previous start parameters before restarting.
        ' Allows the manager to push config changes without a separate
        ' stop + reconfigure + start sequence.
        Public Property UpdatedStartParams As StartInstanceRequest
    End Class

    Public Class RestartInstanceResponse
        Public Property InstanceId As String
        Public Property State As InstanceState
        Public Property Message As String
    End Class

    ' POST /api/v1/instances/{id}/kill
    ' Immediate force kill. No graceful shutdown. Use for hung processes.
    Public Class KillInstanceResponse
        Public Property InstanceId As String
        Public Property State As InstanceState
        Public Property Message As String
    End Class

    ' GET /api/v1/instances/{id}/metrics
    ' Current runtime metrics. Polled by the manager on a schedule.
    Public Class InstanceMetricsResponse
        Public Property InstanceId As String
        Public Property SampledAt As DateTime
        Public Property State As InstanceState
        Public Property RconState As RconState
        Public Property PlayerCount As Integer
        Public Property Players As List(Of PlayerInfo)
        Public Property CustomMetrics As Dictionary(Of String, String)
        Public Property UptimeSeconds As Long?
        Public Property ProcessCpuPercent As Double?
        Public Property ProcessMemoryMb As Long?
        Public Property CrashCountInWindow As Integer
    End Class


    ' ============================================================
    '  LOG ENDPOINTS
    ' ============================================================

    ' GET /api/v1/instances/{id}/logs
    ' Query parameters:
    '   lines    = number of recent lines to return (default 200, max 5000)
    '   sourceId = filter to a specific source ("stdout", "logfile", etc)
    '              omit for all sources
    '   since    = ISO 8601 datetime - only return lines after this time
    Public Class InstanceLogsResponse
        Public Property InstanceId As String
        Public Property Lines As List(Of LogLine)
        Public Property TotalLinesInBuffer As Long
        Public Property OldestLineAt As DateTime?
        Public Property NewestLineAt As DateTime?
    End Class

    Public Class LogLine
        Public Property LineIndex As Long           ' Ring buffer position
        Public Property SourceId As String          ' Which log source
        Public Property Timestamp As DateTime
        Public Property Content As String
    End Class

    ' GET /api/v1/instances/{id}/logs/stream
    ' Server-Sent Events stream. Each event is a JSON-encoded LogLine.
    ' The manager consumes this with a StreamReader loop:
    '
    '   Using response = Await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
    '   Using stream = Await response.Content.ReadAsStreamAsync()
    '   Using reader As New StreamReader(stream)
    '       Do While Not reader.EndOfStream
    '           Dim line = Await reader.ReadLineAsync()
    '           If line.StartsWith("data: ") Then
    '               Dim logLine = JsonSerializer.Deserialize(Of LogLine)(line.Substring(6))
    '               ' Feed to log parser coordinator
    '           End If
    '       Loop
    '   End Using
    '
    ' SSE event format:
    '   data: {"lineIndex":42,"sourceId":"stdout","timestamp":"...","content":"..."}
    '   (blank line)
    '
    ' Query parameters:
    '   fromIndex = ring buffer position to start streaming from.
    '               0 = from the beginning of the buffer.
    '               Omit or -1 = live tail only (no historical lines).
    '               Use the lineIndex from a recent /logs response to
    '               resume without gaps after a manager reconnect.
    '
    ' The stream stays open until the client disconnects or the instance
    ' is removed from the node. The node sends a keep-alive comment
    ' every 15 seconds to prevent proxy timeouts:
    '   : keepalive
    Public Class LogStreamQueryParams
        Public Property FromIndex As Long = -1
        Public Property SourceId As String = ""     ' Empty = all sources
    End Class


    ' ============================================================
    '  STDIN ENDPOINT
    ' ============================================================

    ' POST /api/v1/instances/{id}/stdin
    ' Writes a line to the process's stdin pipe.
    ' Used for Steam Guard codes, Y/N confirmations, and any
    ' other interactive prompts the process blocks on.
    ' Also used for install processes (see installation endpoints).
    Public Class StdinRequest
        ' The line to write. A newline is appended automatically.
        Public Property Line As String

        ' If True, mask this value in all logs (passwords, auth codes).
        Public Property IsSensitive As Boolean = False
    End Class

    Public Class StdinResponse
        Public Property Accepted As Boolean
        Public Property Message As String
    End Class


    ' ============================================================
    '  RCON ENDPOINTS
    ' ============================================================

    ' POST /api/v1/instances/{id}/rcon/connect
    ' Opens and authenticates the RCON session using the config
    ' sent with the original start request. Resets retry counter.
    ' No request body needed.
    Public Class RconConnectResponse
        Public Property RconState As RconState
        Public Property Message As String
    End Class

    ' POST /api/v1/instances/{id}/rcon/disconnect
    ' Closes the RCON session without affecting the game process.
    Public Class RconDisconnectResponse
        Public Property RconState As RconState
        Public Property Message As String
    End Class

    ' GET /api/v1/instances/{id}/rcon/status
    Public Class RconStatusResponse
        Public Property InstanceId As String
        Public Property RconState As RconState
        Public Property ConnectedAt As DateTime?
        Public Property LastCommandAt As DateTime?
        Public Property RetriesAttempted As Integer
    End Class

    ' POST /api/v1/instances/{id}/rcon/send
    ' Sends a command over the existing RCON session and returns
    ' the response synchronously. The node holds the persistent
    ' connection - no new TCP handshake per command.
    Public Class RconSendRequest
        Public Property Command As String
        ' Timeout for this specific command in ms.
        ' Default 5000. Some commands (e.g. save-all) may need longer.
        Public Property TimeoutMs As Integer = 5000
    End Class

    Public Class RconSendResponse
        Public Property Success As Boolean
        Public Property Response As String      ' The server's response text
        Public Property RoundTripMs As Long
        Public Property ErrorMessage As String  ' Populated on failure
    End Class


    ' ============================================================
    '  INSTALLATION ENDPOINTS
    ' ============================================================

    ' GET /api/v1/installations/{id}/status
    ' Current state of an installation - used by the manager to
    ' poll progress during install/update operations.
    Public Class InstallationStatusResponse
        Public Property InstallationId As String
        Public Property State As InstallationOperationState
        Public Property CurrentStepIndex As Integer     ' 0-based
        Public Property TotalSteps As Integer
        Public Property CurrentStepDescription As String
        Public Property ProgressPercent As Integer?     ' 0-100, Nothing if unknown
        Public Property StartedAt As DateTime?
        Public Property CompletedAt As DateTime?
        Public Property ErrorMessage As String
        Public Property PendingPrompt As InstallPromptInfo  ' Nothing if no prompt waiting
        ' Recent stdout from the install process for live display
        Public Property RecentOutput As List(Of String)
    End Class

    Public Enum InstallationOperationState
        Idle            ' No operation in progress
        Running         ' Install/update executing
        WaitingForInput ' Blocked on a prompt (Steam Guard etc)
        Succeeded
        Failed
        Cancelled
    End Enum

    ' POST /api/v1/installations/{id}/install
    ' Starts a fresh installation. Fails if an operation is already
    ' in progress on this installation.
    Public Class InstallRequest
        Public Property InstallationId As String
        ' Ordered steps from IGamePlugin.GetInstallSteps().
        ' Serialised to plain data so the node executes them
        ' without needing any plugin code.
        Public Property Steps As List(Of InstallStepDto)
        ' Steam credentials if needed - decrypted by manager,
        ' transmitted transiently, never persisted on the node.
        Public Property SteamUsername As String
        Public Property SteamPassword As String     ' Sensitive - not logged
        ' Target install path on this node.
        Public Property InstallPath As String
    End Class

    ' POST /api/v1/installations/{id}/update
    ' Same as install but called when updating an existing installation.
    ' The node stops any running instances that hold a read lock on
    ' this installation before proceeding - but the manager is
    ' responsible for having already drained them via StopAllInstances.
    ' The node enforces the write lock; it will reject an update request
    ' if any running instance still holds a read lock.
    Public Class UpdateRequest
        Public Property InstallationId As String
        Public Property Steps As List(Of InstallStepDto)
        Public Property SteamUsername As String
        Public Property SteamPassword As String     ' Sensitive - not logged
        Public Property InstallPath As String
    End Class

    Public Class InstallOperationResponse
        Public Property InstallationId As String
        Public Property State As InstallationOperationState
        Public Property Message As String
    End Class

    ' POST /api/v1/installations/{id}/validate
    ' Runs ValidateInstall logic (file existence checks) without
    ' starting a full install. Returns pass/fail + reason.
    Public Class ValidateInstallResponse
        Public Property InstallationId As String
        Public Property IsValid As Boolean
        Public Property Reason As String
    End Class

    ' POST /api/v1/installations/{id}/cancel
    ' Cancels an in-progress install or update operation.
    ' The node terminates the SteamCMD or download process cleanly.
    Public Class CancelInstallResponse
        Public Property InstallationId As String
        Public Property State As InstallationOperationState
        Public Property Message As String
    End Class


    ' ============================================================
    '  INSTALL STEP DTOs
    '  Plain serialisable versions of the InstallStep class hierarchy.
    '  The node dispatches on StepType to execute each step.
    '  The manager serialises IGamePlugin.GetInstallSteps() output
    '  into these before sending to the node.
    ' ============================================================

    Public Class InstallStepDto
        Public Property StepType As InstallStepType
        Public Property Description As String

        ' SteamCmd fields
        Public Property AppId As String
        Public Property InstallDir As String
        Public Property Branch As String
        Public Property BranchPassword As String
        Public Property ValidateFiles As Boolean

        ' Download fields
        Public Property Url As String
        Public Property Sha256 As String
        Public Property ExtractToPath As String

        ' RunCommand fields
        Public Property Executable As String
        Public Property Arguments As String
        Public Property WorkingDirectory As String
        Public Property ExpectExitCode As Integer
    End Class

    Public Enum InstallStepType
        SteamCmd
        Download
        RunCommand
    End Enum


    ' ============================================================
    '  INSTALL PROMPT ENDPOINTS
    '  When SteamCMD blocks waiting for a Steam Guard code or
    '  other interactive input, the node transitions the install
    '  to WaitingForInput and exposes the prompt details here.
    '  The manager polls for prompts and surfaces them in the UI.
    ' ============================================================

    ' GET /api/v1/installations/{id}/prompt
    ' Poll for a pending prompt. Returns Nothing/null if no prompt
    ' is currently waiting. The manager polls this at ~1s intervals
    ' while an install is in WaitingForInput state.
    Public Class InstallPromptInfo
        Public Property PromptType As PromptType
        Public Property DisplayMessage As String
        Public Property InputPlaceholder As String
        Public Property IsSensitive As Boolean
        Public Property WaitingForInputSince As DateTime
    End Class

    ' POST /api/v1/installations/{id}/prompt
    ' Respond to a pending prompt. Writes the value to the
    ' install process's stdin pipe (e.g. SteamCMD stdin).
    Public Class RespondToPromptRequest
        Public Property Response As String
        ' If True the node will not log the response value.
        Public Property IsSensitive As Boolean = False
    End Class

    Public Class RespondToPromptResponse
        Public Property Accepted As Boolean
        Public Property Message As String
    End Class


    ' ============================================================
    '  INSTALLATION LOCK
    '  The node enforces read/write locking on installations.
    '  The manager controls the workflow; the node enforces safety.
    ' ============================================================

    ' Embedded in error responses when the node rejects an operation
    ' due to lock contention.
    Public Class InstallationLockInfo
        Public Property InstallationId As String
        Public Property LockType As LockType
        Public Property HeldByInstanceIds As List(Of String)  ' For read locks
        Public Property HeldSince As DateTime
    End Class

    Public Enum LockType
        Read    ' Held by running instances - prevents write operations
        Write   ' Held by an install/update - prevents new instance starts
    End Enum


    ' ============================================================
    '  NODE SYSTEM INFO
    ' ============================================================

    ' GET /api/v1/system/info
    ' Hardware and OS information about the node machine.
    Public Class NodeSystemInfoResponse
        Public Property Hostname As String
        Public Property Os As String                    ' "Windows 10", "Ubuntu 22.04" etc
        Public Property Architecture As String          ' "x64"
        Public Property CpuName As String
        Public Property CpuCoreCount As Integer
        Public Property TotalMemoryMb As Long
        Public Property FreeMemoryMb As Long
        Public Property DotNetRuntime As String
        Public Property NodeServiceVersion As String
        Public Property NodeStartedAt As DateTime
    End Class

    ' GET /api/v1/system/drives
    ' Available drives/mounts on the node - used by the manager UI
    ' when the operator is configuring an install path.
    Public Class NodeDrivesResponse
        Public Property Drives As List(Of DriveInfo)
    End Class

    Public Class DriveInfo
        Public Property RootPath As String          ' "C:\" or "/mnt/data"
        Public Property Label As String             ' Volume label if available
        Public Property TotalSizeGb As Double
        Public Property FreeSpaceGb As Double
        Public Property DriveFormat As String       ' "NTFS", "ext4" etc
    End Class


    ' ============================================================
    '  ERROR RESPONSE
    '  All non-2xx responses use this shape.
    '  ErrorCode is a stable string the manager can switch on.
    '  Message is human-readable for logging and UI display.
    ' ============================================================

    Public Class NodeErrorResponse
        Public Property ErrorCode As String
        Public Property Message As String
        Public Property Details As String           ' Stack trace or extended info
        Public Property InstallationLock As InstallationLockInfo  ' Populated for lock errors
    End Class

    ' Stable error codes used by the manager to handle errors
    ' programmatically. Never parse the Message string for logic.
    Public Module NodeErrorCodes
        Public Const InstanceNotFound As String = "INSTANCE_NOT_FOUND"
        Public Const InstanceAlreadyRunning As String = "INSTANCE_ALREADY_RUNNING"
        Public Const InstanceNotRunning As String = "INSTANCE_NOT_RUNNING"
        Public Const InstanceStartFailed As String = "INSTANCE_START_FAILED"
        Public Const ExecutableNotFound As String = "EXECUTABLE_NOT_FOUND"
        Public Const RconNotConnected As String = "RCON_NOT_CONNECTED"
        Public Const RconNotConfigured As String = "RCON_NOT_CONFIGURED"
        Public Const RconCommandFailed As String = "RCON_COMMAND_FAILED"
        Public Const InstallAlreadyInProgress As String = "INSTALL_ALREADY_IN_PROGRESS"
        Public Const InstallNotInProgress As String = "INSTALL_NOT_IN_PROGRESS"
        Public Const InstallationWriteLocked As String = "INSTALLATION_WRITE_LOCKED"
        Public Const InstallationReadLocked As String = "INSTALLATION_READ_LOCKED"
        Public Const NoPromptWaiting As String = "NO_PROMPT_WAITING"
        Public Const StdinNotAvailable As String = "STDIN_NOT_AVAILABLE"
        Public Const Unauthorised As String = "UNAUTHORISED"
        Public Const InternalError As String = "INTERNAL_ERROR"
    End Module


    ' ============================================================
    '  NODE CLIENT INTERFACE
    '  The abstraction the manager's communication layer implements.
    '  Defined here alongside the DTOs so the contract is in one
    '  place. Implemented by NodeHttpClient in manager Core.
    '  The InstanceManager calls this interface - it never touches
    '  HttpClient directly.
    ' ============================================================

    Public Interface INodeClient

        ' ---- Node health ----
        Function GetVersionAsync(cancellation As CancellationToken) As Task(Of NodeVersionResponse)
        Function GetHealthAsync(cancellation As CancellationToken) As Task(Of NodeHealthResponse)

        ' ---- Instances ----
        Function GetInstancesAsync(cancellation As CancellationToken) As Task(Of NodeInstanceListResponse)
        Function GetInstanceAsync(instanceId As String,
                                  cancellation As CancellationToken) As Task(Of NodeInstanceDetailResponse)
        Function StartInstanceAsync(request As StartInstanceRequest,
                                    cancellation As CancellationToken) As Task(Of StartInstanceResponse)
        Function StopInstanceAsync(instanceId As String,
                                   request As StopInstanceRequest,
                                   cancellation As CancellationToken) As Task(Of StopInstanceResponse)
        Function RestartInstanceAsync(instanceId As String,
                                      request As RestartInstanceRequest,
                                      cancellation As CancellationToken) As Task(Of RestartInstanceResponse)
        Function KillInstanceAsync(instanceId As String,
                                   cancellation As CancellationToken) As Task(Of KillInstanceResponse)
        Function GetMetricsAsync(instanceId As String,
                                 cancellation As CancellationToken) As Task(Of InstanceMetricsResponse)

        ' ---- Logs ----
        Function GetLogsAsync(instanceId As String,
                              lines As Integer,
                              sourceId As String,
                              cancellation As CancellationToken) As Task(Of InstanceLogsResponse)

        ' Streams log lines from the SSE endpoint, invoking onLine
        ' for each line as it arrives. Returns when the stream ends
        ' or the cancellation token fires.
        ' VB.Net does not support Async Iterator / Await For Each,
        ' so we use a callback pattern instead of IAsyncEnumerable.
        Function StreamLogsAsync(instanceId As String,
                                 fromIndex As Long,
                                 sourceId As String,
                                 onLine As Action(Of LogLine),
                                 cancellation As CancellationToken) As Task

        ' ---- Stdin ----
        Function WriteStdinAsync(instanceId As String,
                                 request As StdinRequest,
                                 cancellation As CancellationToken) As Task(Of StdinResponse)

        ' ---- RCON ----
        Function ConnectRconAsync(instanceId As String,
                                  cancellation As CancellationToken) As Task(Of RconConnectResponse)
        Function DisconnectRconAsync(instanceId As String,
                                     cancellation As CancellationToken) As Task(Of RconDisconnectResponse)
        Function GetRconStatusAsync(instanceId As String,
                                    cancellation As CancellationToken) As Task(Of RconStatusResponse)
        Function SendRconAsync(instanceId As String,
                               request As RconSendRequest,
                               cancellation As CancellationToken) As Task(Of RconSendResponse)

        ' ---- Installations ----
        Function GetInstallationStatusAsync(installationId As String,
                                            cancellation As CancellationToken) As Task(Of InstallationStatusResponse)
        Function StartInstallAsync(request As InstallRequest,
                                   cancellation As CancellationToken) As Task(Of InstallOperationResponse)
        Function StartUpdateAsync(request As UpdateRequest,
                                  cancellation As CancellationToken) As Task(Of InstallOperationResponse)
        Function ValidateInstallAsync(installationId As String,
                                      cancellation As CancellationToken) As Task(Of ValidateInstallResponse)
        Function CancelInstallAsync(installationId As String,
                                    cancellation As CancellationToken) As Task(Of CancelInstallResponse)

        ' ---- Install prompts ----
        Function GetInstallPromptAsync(installationId As String,
                                       cancellation As CancellationToken) As Task(Of InstallPromptInfo)
        Function RespondToInstallPromptAsync(installationId As String,
                                             request As RespondToPromptRequest,
                                             cancellation As CancellationToken) As Task(Of RespondToPromptResponse)

        ' ---- System ----
        Function GetSystemInfoAsync(cancellation As CancellationToken) As Task(Of NodeSystemInfoResponse)
        Function GetDrivesAsync(cancellation As CancellationToken) As Task(Of NodeDrivesResponse)

    End Interface

End Namespace
