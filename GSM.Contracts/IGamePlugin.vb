Imports System
Imports System.Collections.Generic
Imports System.Text.Json.Serialization
Imports System.Threading
Imports System.Threading.Tasks

' ============================================================
'  GSM Plugin Contract
'  Drop a .vb file implementing IGamePlugin into plugins\
'  The PluginRegistry will compile and load it via Roslyn.
'
'  CRITICAL: Nothing in Core may hold a reference to a concrete
'  plugin type. Always IGamePlugin. Always resolve through
'  PluginRegistry.GetPlugin(gameId). This is what makes
'  hot-reload safe.
' ============================================================

Namespace GSM.Plugin

    ' ============================================================
    '  Enums
    ' ============================================================

    ''' <summary>
    ''' The lifecycle state of a game server instance on a node.
    ''' </summary>
    Public Enum InstanceState
        Stopped
        Starting
        Running
        Stopping
        Crashed
        CrashLoopHalted
        Updating
        WaitingForInput
    End Enum

    ''' <summary>
    ''' RCON connection state.
    ''' </summary>
    Public Enum RconState
        Disconnected
        Connecting
        Authenticated
        Failed
    End Enum

    ''' <summary>
    ''' RCON protocol variant.
    ''' </summary>
    Public Enum RconProtocol
        SourceRcon
        WebSocket
        Custom
    End Enum

    ''' <summary>
    ''' How the crash detector determined a crash occurred.
    ''' </summary>
    Public Enum CrashDetectionState
        None
        ProcessExitNonZero
        LogPatternMatch
        Unresponsive
    End Enum

    ''' <summary>
    ''' Policy the node follows when an instance crashes.
    ''' </summary>
    Public Enum CrashRestartPolicy
        AlwaysRestart
        RestartWithBackoff
        RestartLimited
        NeverRestart
    End Enum

    ''' <summary>
    ''' Config field types for dynamic UI generation.
    ''' IntegerField and BooleanField avoid VB reserved keyword clash.
    ''' </summary>
    Public Enum ConfigFieldType
        Text
        IntegerField
        BooleanField
        [Enum]
        Password
        FilePath
        FolderPath
    End Enum

    ''' <summary>
    ''' How a game server installation is acquired.
    ''' </summary>
    Public Enum InstallMethod
        SteamCmd
        DirectDownload
        Manual
    End Enum

    ' ============================================================
    '  Install step hierarchy
    ' ============================================================

    ''' <summary>
    ''' Base class for installation steps. Each subclass represents
    ''' one discrete step the node executes during install/update.
    ''' </summary>
    <JsonPolymorphic(TypeDiscriminatorPropertyName:="$type")>
    <JsonDerivedType(GetType(SteamCmdStep), "steamcmd")>
    <JsonDerivedType(GetType(DownloadFileStep), "download")>
    <JsonDerivedType(GetType(CopyFileStep), "copy")>
    <JsonDerivedType(GetType(WriteFileStep), "writefile")>
    <JsonDerivedType(GetType(RunProcessStep), "runprocess")>
    Public MustInherit Class InstallStep
        Public Property StepName As String
        Public Property Description As String
    End Class

    ''' <summary>
    ''' Download and install via SteamCMD.
    ''' </summary>
    Public Class SteamCmdStep
        Inherits InstallStep

        Public Property AppId As Integer
        Public Property BetaBranch As String
        Public Property BetaPassword As String
        Public Property ValidateFiles As Boolean = True
        Public Property RequiresLogin As Boolean = False
        Public Property Platform As String = "windows"
    End Class

    ''' <summary>
    ''' Download a file from a URL.
    ''' </summary>
    Public Class DownloadFileStep
        Inherits InstallStep

        Public Property Url As String
        Public Property DestinationRelativePath As String
        Public Property ExtractArchive As Boolean = False
    End Class

    ''' <summary>
    ''' Copy or move a file within the installation directory.
    ''' </summary>
    Public Class CopyFileStep
        Inherits InstallStep

        Public Property SourceRelativePath As String
        Public Property DestinationRelativePath As String
        Public Property Overwrite As Boolean = True
    End Class

    ''' <summary>
    ''' Write content to a text file (config templates, etc).
    ''' </summary>
    Public Class WriteFileStep
        Inherits InstallStep

        Public Property RelativePath As String
        Public Property Content As String
        Public Property OverwriteExisting As Boolean = False
    End Class

    ''' <summary>
    ''' Run an executable or script on the node.
    ''' </summary>
    Public Class RunProcessStep
        Inherits InstallStep

        Public Property ExecutablePath As String
        Public Property Arguments As String
        Public Property WorkingDirectory As String
        Public Property TimeoutMs As Integer = 120000
        Public Property ExpectedExitCode As Integer = 0
    End Class

    ' ============================================================
    '  Config field descriptor (drives dynamic UI)
    ' ============================================================

    ''' <summary>
    ''' Describes a single configuration field for dynamic form
    ''' generation. Plugins return arrays of these to tell the UI
    ''' what settings to show.
    ''' </summary>
    Public Class ConfigFieldDescriptor
        Public Property Key As String
        Public Property Label As String
        Public Property Description As String
        Public Property FieldType As ConfigFieldType
        Public Property DefaultValue As String
        Public Property IsRequired As Boolean = False
        Public Property IsSensitive As Boolean = False
        Public Property ValidationRegex As String
        Public Property EnumValues As List(Of String)
        Public Property MinValue As Integer?
        Public Property MaxValue As Integer?
    End Class

    ' ============================================================
    '  Restart decision (crash handling)
    ' ============================================================

    ''' <summary>
    ''' Returned by the plugin when the node asks whether a crashed
    ''' instance should be restarted.
    ''' </summary>
    Public Class RestartDecision
        Public Property ShouldRestart As Boolean
        Public Property DelayMs As Integer
        Public Property Reason As String
        Public Property ModifyArguments As String

        Public Shared Function Restart(Optional delayMs As Integer = 0,
                                       Optional reason As String = Nothing) As RestartDecision
            Return New RestartDecision With {
                .ShouldRestart = True,
                .DelayMs = delayMs,
                .Reason = If(reason, "Restart approved")
            }
        End Function

        Public Shared Function Halt(reason As String) As RestartDecision
            Return New RestartDecision With {
                .ShouldRestart = False,
                .Reason = reason
            }
        End Function
    End Class

    ' ============================================================
    '  Player info
    ' ============================================================

    Public Class PlayerInfo
        Public Property PlayerId As String
        Public Property PlayerName As String
        Public Property JoinedAt As DateTime
        Public Property Metadata As Dictionary(Of String, String)
    End Class

    ' ============================================================
    '  Instance and installation configuration DTOs
    ' ============================================================

    ''' <summary>
    ''' Configuration values for a running instance. Passed from
    ''' the manager to the node when starting a server.
    ''' </summary>
    Public Class InstanceConfig
        Public Property InstanceId As String
        Public Property GameId As String
        Public Property DisplayName As String
        Public Property InstallationId As String
        Public Property ExePath As String
        Public Property LaunchArguments As String
        Public Property WorkingDirectory As String
        Public Property EnvironmentVars As Dictionary(Of String, String)
        Public Property RconPort As Integer?
        Public Property RconPassword As String
        Public Property RconProtocol As RconProtocol
        Public Property CrashPolicy As CrashRestartPolicy
        Public Property MaxCrashCount As Integer = 5
        Public Property CrashWindowMinutes As Integer = 60
        Public Property CustomFields As Dictionary(Of String, String)
    End Class

    ''' <summary>
    ''' Configuration values for a game server installation.
    ''' Describes how the installation was set up and how to update it.
    ''' </summary>
    Public Class InstallationConfig
        Public Property InstallationId As String
        Public Property GameId As String
        Public Property DisplayName As String
        Public Property InstallPath As String
        Public Property InstallMethod As InstallMethod
        Public Property NodeId As String
        Public Property CustomFields As Dictionary(Of String, String)
    End Class

    ' ============================================================
    '  Log types
    ' ============================================================

    ''' <summary>
    ''' A single parsed line of log output.
    ''' </summary>
    Public Class LogLine
        Public Property Timestamp As DateTime
        Public Property SourceId As String
        Public Property Text As String
        Public Property IsError As Boolean
    End Class

    ' ============================================================
    '  ILogSource — where log output comes from
    ' ============================================================

    Public Interface ILogSource
        ReadOnly Property SourceId As String
    End Interface

    Public Class StdoutLogSource
        Implements ILogSource

        Public ReadOnly Property SourceId As String = "stdout" Implements ILogSource.SourceId
        Public Property CaptureStderr As Boolean = True
    End Class

    Public Class FileLogSource
        Implements ILogSource

        Private ReadOnly _sourceId As String
        Private ReadOnly _pathPattern As String

        Public Sub New(sourceId As String, pathPattern As String)
            _sourceId = sourceId
            _pathPattern = pathPattern
        End Sub

        Public ReadOnly Property SourceId As String Implements ILogSource.SourceId
            Get
                Return _sourceId
            End Get
        End Property

        Public ReadOnly Property PathPattern As String
            Get
                Return _pathPattern
            End Get
        End Property

        Public Property FollowRotation As Boolean = False
    End Class

    ' ============================================================
    '  ILogParser — plugin-supplied log analysis
    ' ============================================================

    ''' <summary>
    ''' Parses raw log lines and extracts structured events.
    ''' The manager creates one per instance and feeds lines to it.
    ''' </summary>
    Public Interface ILogParser
        ReadOnly Property GameId As String

        Function ParseLine(line As LogLine) As ParsedLogEvent

        ''' <summary>
        ''' Returns crash patterns the node should watch for.
        ''' </summary>
        Function GetCrashPatterns() As IReadOnlyList(Of String)
    End Interface

    ''' <summary>
    ''' Structured event extracted from a log line.
    ''' </summary>
    Public Class ParsedLogEvent
        Public Property EventType As LogEventType
        Public Property Message As String
        Public Property PlayerInfo As PlayerInfo
        Public Property Metadata As Dictionary(Of String, String)

        Public Shared ReadOnly NoMatch As New ParsedLogEvent With {
            .EventType = LogEventType.None
        }
    End Class

    Public Enum LogEventType
        None
        PlayerJoin
        PlayerLeave
        ChatMessage
        ServerReady
        ServerShutdown
        Warning
        ErrorOccurred
        CrashIndicator
        Custom
    End Enum

    ' ============================================================
    '  IModManager — optional mod/workshop support
    ' ============================================================

    ''' <summary>
    ''' Optional interface for games that support mods or
    ''' Steam Workshop. Not all plugins implement this.
    ''' </summary>
    Public Interface IModManager
        ReadOnly Property GameId As String

        Function GetInstalledModsAsync(installPath As String,
                                       cancellation As CancellationToken) As Task(Of IReadOnlyList(Of ModInfo))

        Function InstallModAsync(installPath As String,
                                  modId As String,
                                  cancellation As CancellationToken) As Task(Of Boolean)

        Function RemoveModAsync(installPath As String,
                                 modId As String,
                                 cancellation As CancellationToken) As Task(Of Boolean)

        Function GetModConfigSchema(modId As String) As IReadOnlyList(Of ConfigFieldDescriptor)
    End Interface

    Public Class ModInfo
        Public Property ModId As String
        Public Property ModName As String
        Public Property Version As String
        Public Property IsEnabled As Boolean
    End Class

    ' ============================================================
    '  IGamePlugin — the primary interface
    ' ============================================================

    ''' <summary>
    ''' Core contract for game server plugins. Each supported game
    ''' implements this interface. The manager compiles and loads
    ''' plugins via Roslyn and resolves all game-specific logic
    ''' through this contract. Nodes receive plain data only.
    ''' </summary>
    Public Interface IGamePlugin

        ''' <summary>
        ''' Stable identifier — used as FK in all instance/install
        ''' records. Never change once installs exist.
        ''' e.g. "lastoasis", "factorio"
        ''' </summary>
        ReadOnly Property GameId As String

        ''' <summary>
        ''' Human-readable name for UI display.
        ''' </summary>
        ReadOnly Property DisplayName As String

        ' ---- Install ----

        ''' <summary>
        ''' Which install methods this game supports.
        ''' </summary>
        Function GetSupportedInstallMethods() As IReadOnlyList(Of InstallMethod)

        ''' <summary>
        ''' Returns ordered steps the node executes during install/update.
        ''' Plugin resolves logic; steps are plain data sent to the node.
        ''' </summary>
        Function GetInstallSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep)

        ''' <summary>
        ''' Returns ordered steps for updating an existing installation.
        ''' May differ from install steps (e.g. skip initial config write).
        ''' </summary>
        Function GetUpdateSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep)

        ' ---- Instance ----

        ''' <summary>
        ''' Build the command-line arguments for launching an instance.
        ''' </summary>
        Function BuildLaunchArguments(config As InstanceConfig) As String

        ''' <summary>
        ''' Validates an instance configuration. Returns a list of
        ''' validation errors, or empty if valid.
        ''' </summary>
        Function ValidateConfig(config As InstanceConfig) As IReadOnlyList(Of String)

        ' ---- Config schema ----

        ''' <summary>
        ''' Schema for installation-level custom fields.
        ''' </summary>
        Function GetInstallConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)

        ''' <summary>
        ''' Schema for instance-level custom fields.
        ''' </summary>
        Function GetInstanceConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)

        ' ---- Crash handling ----

        ''' <summary>
        ''' Called when the node detects a crash. Returns whether
        ''' to restart and with what parameters.
        ''' </summary>
        Function EvaluateCrash(exitCode As Integer,
                               crashCount As Integer,
                               policy As CrashRestartPolicy) As RestartDecision

        ' ---- Log parsing ----

        ''' <summary>
        ''' Creates a log parser for this game. Returns Nothing
        ''' if the plugin has no log parsing support.
        ''' </summary>
        Function CreateLogParser() As ILogParser

        ''' <summary>
        ''' Returns the log sources for this game (stdout, files).
        ''' </summary>
        Function GetLogSources(config As InstanceConfig) As IReadOnlyList(Of ILogSource)

        ' ---- RCON ----

        ''' <summary>
        ''' Returns the RCON protocol this game uses, or Nothing
        ''' if the game does not support RCON.
        ''' </summary>
        Function GetRconProtocol() As RconProtocol?

        ' ---- Mods ----

        ''' <summary>
        ''' Creates a mod manager for this game. Returns Nothing
        ''' if the game does not support mods.
        ''' </summary>
        Function CreateModManager() As IModManager

    End Interface

    ' ============================================================
    '  Plugin hot-reload support
    ' ============================================================

    ''' <summary>
    ''' Summarises the result of a plugin hot-reload cycle.
    ''' </summary>
    Public Class PluginReloadSummary
        Public Property LoadedPlugins As List(Of String)
        Public Property AddedGameIds As List(Of String)
        Public Property RemovedGameIds As List(Of String)
        Public Property UpdatedGameIds As List(Of String)
        Public Property CompilationErrors As List(Of PluginCompilationError)
        Public Property OrphanedInstallationIds As List(Of String)
        Public Property OrphanedInstanceIds As List(Of String)
    End Class

    Public Class PluginCompilationError
        Public Property FileName As String
        Public Property Line As Integer
        Public Property Column As Integer
        Public Property ErrorCode As String
        Public Property Message As String
    End Class

    Public Enum PluginLoadStatus
        Loaded
        CompilationFailed
        InterfaceMismatch
        DuplicateGameId
        Unloaded
    End Enum

    ''' <summary>
    ''' Detects orphaned data when a plugin is removed during
    ''' hot-reload. The manager calls this to determine what to
    ''' do with data that referenced the now-missing plugin.
    ''' </summary>
    Public Interface IOrphanDetector
        Function GetOrphanedInstallationIds(gameId As String) As IReadOnlyList(Of String)
        Function GetOrphanedInstanceIds(gameId As String) As IReadOnlyList(Of String)
    End Interface

End Namespace