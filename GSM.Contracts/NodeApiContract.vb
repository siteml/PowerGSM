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
        Public Property RconPort As Integer?
        Public Property RconPassword As String
        Public Property RconProtocol As RconProtocol
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

        ' ---- Interactive prompts ----
        Function RespondToPromptAsync(response As PromptResponse, cancellation As CancellationToken) As Task(Of Boolean)
        Function UninstallAsync(request As UninstallRequest, cancellation As CancellationToken) As Task(Of Boolean)

    End Interface

End Namespace
