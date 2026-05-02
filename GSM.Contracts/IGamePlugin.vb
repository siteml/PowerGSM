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

        ''' <summary>
        ''' Marks this field as a network port. Used by the manager's
        ''' PortAllocator to (a) suggest the next free value when
        ''' creating a new instance, and (b) validate that no two
        ''' instances on the same node share a port at save time.
        ''' Only meaningful for IntegerField type fields; ignored
        ''' elsewhere.
        '''
        ''' Validation is GLOBAL across the node, not scoped to one
        ''' game — operators routinely rebase ports into a single
        ''' shared range like 7777–7900 across all games hosted on
        ''' a machine, so a Factorio instance must not collide with
        ''' a Last Oasis instance even though they're different
        ''' plugins. The allocator and validator both walk every
        ''' port-marked field on every instance on the node when
        ''' building their picture of what's in use.
        ''' </summary>
        Public Property IsPort As Boolean = False
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
    '  LogParseRule — declarative rule sent to the node so it can
    '  parse log lines into structured events without needing the
    '  plugin loaded.
    ' ============================================================

    ''' <summary>
    ''' Categorizes a parsed event so the node knows which store
    ''' or handler the event feeds into.
    ''' </summary>
    Public Enum ParsedEventKind
        PlayerJoin
        PlayerLeave
        PlayerIdentity     ' Periodic name↔SteamID mapping
        ChatMessage
        ServerStateChange  ' Match state transitions
        TileLoaded
        Custom
    End Enum

    ''' <summary>
    ''' A single parse rule. The Pattern is a standard .NET regex
    ''' with named capture groups. Group names become field names
    ''' on the emitted ParsedEvent.Fields dictionary.
    ''' </summary>
    Public Class LogParseRule
        Public Property Kind As ParsedEventKind
        Public Property Pattern As String

        ''' <summary>
        ''' Optional rule name for debugging. Does not affect parsing.
        ''' </summary>
        Public Property Name As String
    End Class

    ''' <summary>
    ''' A parsed event produced by applying a LogParseRule to a log
    ''' line. Fields are populated from the regex's named capture groups.
    ''' </summary>
    Public Class ParsedEvent
        Public Property Kind As ParsedEventKind
        Public Property Timestamp As DateTime
        Public Property RawText As String
        Public Property Fields As Dictionary(Of String, String)
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

        ''' <summary>
        ''' The session identity currently in effect for this parser.
        ''' Parsers that track cross-instance entity identity (e.g.
        ''' Last Oasis realm+tile) update this as they observe
        ''' identifying log lines; those that don't can return
        ''' Nothing and downstream code will fall back to
        ''' "{gameId}:{instanceId}". Format is plugin-defined; a
        ''' colon-delimited string is conventional. Thread-safe:
        ''' parsers are called from a single thread per instance.
        ''' </summary>
        ReadOnly Property CurrentSessionIdentity As String

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

        ''' <summary>
        ''' The session identity in effect when this event was
        ''' parsed — stamped by the parser from its
        ''' CurrentSessionIdentity at emission time. Downstream
        ''' persistence keys chat messages and player sessions by
        ''' this so cross-instance history (e.g. a Last Oasis tile
        ''' migrating between hosts) stays consistent. Empty/Nothing
        ''' for events observed before the parser has committed an
        ''' identity, or for games whose plugin doesn't derive one.
        ''' </summary>
        Public Property SessionIdentity As String

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
        ''' <summary>
        ''' Emitted by the parser when a new session identity has
        ''' been committed (e.g. Last Oasis finished loading a tile
        ''' and the realm_id + tile_id pair is now known). The event's
        ''' Metadata dictionary carries "RealmId", "TileId", "TileName"
        ''' where applicable. Downstream code uses this event to
        ''' record session-host transitions.
        ''' </summary>
        TileLoaded
        ''' <summary>
        ''' Emitted when the active session identity has ended for
        ''' this instance (tile deactivated, match ended, etc.). The
        ''' event's SessionIdentity field carries the identity that
        ''' just ended. The parser's CurrentSessionIdentity becomes
        ''' Nothing after this.
        ''' </summary>
        TileUnloaded
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

        ''' <summary>
        ''' Maximum instances that can be created against a single
        ''' installation. Most games file-lock their save/config/mod
        ''' state and can only run one instance per file set, so the
        ''' typical value is 1. Return Nothing for no limit (e.g. UE4
        ''' dedicated servers where per-instance state lives entirely
        ''' in command-line args and the binary is genuinely shared,
        ''' like Last Oasis hosting multiple tiles from one MistServer
        ''' installation).
        '''
        ''' The Manager enforces this as a hard limit at instance-
        ''' creation time — both by greying out the "Add Instance..."
        ''' menu when the limit is reached, and by re-checking inside
        ''' AddInstanceForm.OnSave as defence in depth. Plugins that
        ''' return Nothing skip both checks.
        ''' </summary>
        ReadOnly Property MaxInstancesPerInstallation As Integer?

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

        ''' <summary>
        ''' Returns declarative regex rules that let the node parse
        ''' log lines into structured events without needing the plugin
        ''' loaded. This is what makes a node standalone — the Manager
        ''' sends these rules once at instance start and the node
        ''' applies them as new log lines arrive.
        ''' </summary>
        Function GetLogParseRules() As IReadOnlyList(Of LogParseRule)

        ''' <summary>
        ''' Returns one or more candidate executable paths relative to
        ''' the installation directory. The Manager will try them in
        ''' order when starting an instance and remember which one
        ''' actually exists on the node. Most plugins return a single
        ''' entry; games with multiple binary variants return several.
        ''' </summary>
        Function GetExecutablePath(config As InstanceConfig) As IReadOnlyList(Of String)

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
        ''' <summary>
        ''' Phase 5f-3 — plugin's `' &lt;RequiresContracts: N&gt;'
        ''' magic comment declared a contracts version newer
        ''' than the running manager's NodeApiContract.ContractsVersion.
        ''' The plugin was rejected before Roslyn compile to
        ''' avoid a compile-error spew that would obscure the
        ''' real cause. User-visible fix: update the manager,
        ''' or use a plugin compiled for the running contracts
        ''' version.
        ''' </summary>
        ContractsVersionTooNew
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

    ' ============================================================
    '  Ready-for-next signal
    '
    '  Used by the restart coordinator to know when an instance
    '  has booted far enough that the NEXT instance in the queue
    '  can start restarting. "Booted far enough" is game-specific:
    '  for Last Oasis, the match-state entering "LeavingMap" means
    '  the tile has finished pre-loading and the server is ready
    '  to accept players. For games without such a signal, the
    '  coordinator falls back to a plain timeout.
    '
    '  Plugins opt in by ALSO implementing IReadySignalProvider.
    '  This is a separate interface (rather than new members on
    '  IGamePlugin) so existing compiled plugins keep working
    '  unchanged. If a plugin doesn't implement it, the restart
    '  coordinator just uses the fallback delay.
    ' ============================================================

    ''' <summary>
    ''' Categorises what kind of parsed-log event signals
    ''' "instance has booted far enough".
    ''' </summary>
    Public Enum ReadySignalKind
        ''' <summary>
        ''' The parser emitted a ServerStateChange whose
        ''' "MatchState" field equals MatchValue. Typical for
        ''' UE-based games that publish match-state transitions.
        ''' </summary>
        ServerStateEquals

        ''' <summary>
        ''' The parser emitted a TileLoaded event. MatchValue is
        ''' ignored. Useful for LO-shaped games where "tile
        ''' loaded" itself is the readiness signal.
        ''' </summary>
        TileLoaded

        ''' <summary>
        ''' The parser emitted any ParsedEventKind.Custom event
        ''' whose Metadata contains key "ReadyMarker" (value
        ''' ignored). Escape hatch for games that need a bespoke
        ''' condition — the plugin's parse rule tags its ready
        ''' line with that metadata key.
        ''' </summary>
        CustomMarker
    End Enum

    ''' <summary>
    ''' A plugin's declaration of what "ready for next restart"
    ''' means on this game. Consumed by the restart coordinator
    ''' in the Manager; nodes never see this — the actual event
    ''' matching happens on the Manager side against events the
    ''' node streams up.
    ''' </summary>
    Public Class ReadySignal
        Public Property Kind As ReadySignalKind
        ''' <summary>
        ''' For Kind=ServerStateEquals: the MatchState value that
        ''' indicates readiness (e.g. "LeavingMap"). Case-sensitive.
        ''' Ignored for other Kind values.
        ''' </summary>
        Public Property MatchValue As String
    End Class

    ''' <summary>
    ''' Opt-in interface plugins implement to participate in
    ''' coordinated restarts. Plugins that don't implement this
    ''' still work — the restart coordinator treats them as
    ''' "wait for the fallback delay, then release the slot".
    ''' </summary>
    Public Interface IReadySignalProvider
        ''' <summary>
        ''' Returns the signal the coordinator should watch for
        ''' after a restart, or Nothing to indicate "no signal,
        ''' use timeout only".
        ''' </summary>
        Function GetReadyForNextSignal() As ReadySignal

        ''' <summary>
        ''' Fallback timeout in seconds. The coordinator waits at
        ''' most this long for the signal before giving up and
        ''' releasing the slot anyway. Also the sole wait duration
        ''' when GetReadyForNextSignal returns Nothing.
        ''' </summary>
        ReadOnly Property DefaultReadyTimeoutSeconds As Integer
    End Interface

    ' ============================================================
    '  IVersionAwarePlugin — opt-in version-check capability
    '
    '  Phase 5: lets plugins declare how to fetch the latest
    '  available version of their game from upstream (factorio.com
    '  API, GitHub releases, custom backends, etc.). The Manager's
    '  VersionCheckService polls this on a schedule and raises
    '  version-mismatch automation events when the upstream
    '  version differs from the recorded installed version.
    '
    '  Steam-installed games don't need this interface — the
    '  Manager already does Steam buildid checks via
    '  InstallationManager.CheckForUpdatesAsync, which talks to
    '  the node and reads the ACF manifest. This interface
    '  covers the non-Steam path: factorio.com headless API,
    '  Minecraft version manifests, GitHub release feeds, etc.
    '
    '  Plugins that don't implement this interface still work —
    '  if they're SteamCmd-based, the Steam path covers them; if
    '  they're neither SteamCmd nor IVersionAwarePlugin, version
    '  mismatch rules referencing them simply never fire.
    '
    '  Returned values are opaque strings: build numbers, version
    '  strings, git hashes, dates — whatever makes sense to the
    '  plugin. The Manager treats them as bare strings and only
    '  compares for inequality. Plugins are responsible for
    '  returning a stable representation across calls (don't
    '  return "v1.0.0 fetched at 12:34" because that changes
    '  every poll).
    ' ============================================================

    ''' <summary>
    ''' Opt-in interface for plugins that can fetch the latest
    ''' upstream version of their game. Used by the Manager's
    ''' VersionCheckService to detect updates. Plugins that don't
    ''' implement this interface fall back to the Steam buildid
    ''' path (if SteamCmd-based) or are silently skipped.
    ''' </summary>
    Public Interface IVersionAwarePlugin
        ''' <summary>
        ''' Fetch the latest available version of this game from
        ''' upstream. Return value is plugin-defined — the Manager
        ''' treats it as an opaque string and only compares for
        ''' inequality against the previously-known value.
        '''
        ''' Should return Nothing on transient failures (network
        ''' errors, rate limits, etc.) so the Manager can retry
        ''' on the next poll cycle without recording a stale value.
        ''' Throwing is also acceptable — the Manager catches and
        ''' logs at warning level.
        '''
        ''' Implementations must respect the cancellation token
        ''' so the Manager can shut down promptly during exit.
        ''' </summary>
        Function GetLatestVersionAsync(
            config As InstallationConfig,
            cancellation As CancellationToken) As Task(Of String)
    End Interface

    ''' <summary>
    ''' Opt-in interface for plugins that want to surface
    ''' informational or warning text in the installation /
    ''' configuration UI. Notices render verbatim alongside the
    ''' plugin's schema fields and are read-only — they exist to
    ''' carry context the user benefits from but that the plugin
    ''' can't enforce in code (e.g. "Factorio AppData saves don't
    ''' migrate automatically", "Last Oasis ProviderKey must be set
    ''' in MyRealm before this server registers").
    '''
    ''' Notices are NOT a substitute for code: anything that affects
    ''' install correctness should be enforced by InstallStep
    ''' execution and validation results, not surfaced as text the
    ''' user can ignore. The bar for adding a notice is "a future
    ''' user will benefit from seeing this and there's no clean way
    ''' to make the system handle it for them".
    '''
    ''' Plugins that don't implement this interface display nothing.
    ''' Returning an empty list also displays nothing (and is the
    ''' correct response for plugins that conditionally have notices
    ''' — e.g. only on Windows nodes, only for one install method).
    ''' </summary>
    Public Interface IInstallationNoticeProvider
        ''' <summary>
        ''' Notices shown when a user is creating a new installation,
        ''' before clicking Create. Rendered above the action buttons
        ''' in NewInstallationForm.
        '''
        ''' Implementations should be cheap (no I/O, no blocking work)
        ''' — the form calls this synchronously each time the game
        ''' selection changes.
        ''' </summary>
        Function GetPreInstallNotices() As IReadOnlyList(Of InstallationNotice)
    End Interface

    ''' <summary>
    ''' Severity levels for InstallationNotice. Two values is
    ''' deliberately constrained — anything truly critical should
    ''' be enforced by code (validation result, install step
    ''' failure), not surfaced as text.
    ''' </summary>
    Public Enum NoticeSeverity
        ''' <summary>
        ''' Background context the user benefits from knowing.
        ''' Rendered with neutral styling.
        ''' </summary>
        Information

        ''' <summary>
        ''' Something the user should pay attention to but doesn't
        ''' block the install. Rendered with attention-grabbing
        ''' styling (orange accent), but still passive — the user
        ''' clicks Create as normal.
        ''' </summary>
        Warning
    End Enum

    ''' <summary>
    ''' A single notice for the installation UI. Body is required;
    ''' Title is optional and renders as a bold first line above
    ''' the body when supplied (analogous to MessageBox's caption
    ''' vs. text split).
    ''' </summary>
    Public Class InstallationNotice
        Public Property Severity As NoticeSeverity

        ''' <summary>
        ''' Optional short header rendered as a bold first line.
        ''' Leave Nothing/empty for body-only notices.
        ''' </summary>
        Public Property Title As String

        ''' <summary>
        ''' The notice text. Multi-line content is rendered as-is;
        ''' the form does not reflow, so plugins should hard-wrap
        ''' at a reasonable column if line breaks matter.
        ''' </summary>
        Public Property Body As String
    End Class

    ' ============================================================
    '  ILaunchOptionsProvider — opt-in spawn customisation
    '
    '  Lets a plugin describe what its game needs at spawn time
    '  without baking node-side strategy names into the contract.
    '  The plugin answers a small number of yes/no questions about
    '  its game; the node maps those answers to its current spawn
    '  implementation.
    '
    '  Two questions today:
    '
    '    StdoutIsLog: "my stdout IS the authoritative log stream."
    '      True for plugins like a hypothetical Minecraft server
    '      where the only log output is on stdout. The node will
    '      redirect stdio to pipes and feed every line into the
    '      ring buffer + EventStore. Mutually exclusive with
    '      console-based graceful shutdown — child has no console
    '      at all on this path.
    '
    '    RequiresConsoleIsolation: "my game executable defeats
    '      CREATE_NEW_CONSOLE — typically by doing FreeConsole +
    '      AttachConsole(ATTACH_PARENT_PROCESS) at startup so its
    '      output reattaches to its parent's console. Without
    '      isolation, my output ends up on whoever spawned me."
    '      True for Factorio. The node spawns through cmd.exe so
    '      the game's parent (and therefore reattach target) is
    '      cmd's hidden console rather than the node's terminal.
    '
    '  Both default False, which means "figure it out from my
    '  declared log sources" — if the plugin declared file logs,
    '  the node uses a hidden-console direct spawn so file tailers
    '  feed log capture and AttachConsole still works for graceful
    '  Ctrl+C; otherwise stdout-capture is the safe fallback. This
    '  matches what plugins that don't implement the interface get.
    '
    '  StdoutIsLog wins over RequiresConsoleIsolation if both are
    '  set: the only way to honour "stdout IS the log" is to
    '  redirect stdio to pipes, which precludes any console
    '  arrangement.
    '
    '  Future capabilities go on as additional booleans (e.g.
    '  RequiresJobObject, NeedsPseudoConsole, etc.) without
    '  breaking existing plugins. The contract intentionally keeps
    '  node-side terminology (Strategy A/B/C, NUL redirection,
    '  etc.) on the node side; plugins describe their game, the
    '  node decides what to do.
    ' ============================================================

    ''' <summary>
    ''' Plugin-supplied spawn customisation. Returned from
    ''' ILaunchOptionsProvider.GetLaunchOptions and forwarded to
    ''' the node via StartInstanceRequest. Defaults match what
    ''' plugins that don't implement the interface get — the node
    ''' decides spawn details from declared log sources.
    ''' </summary>
    Public Class LaunchOptions
        ''' <summary>
        ''' True when the game's stdout is the authoritative log
        ''' stream and the node should capture it for the manager's
        ''' log buffer. Implies the child runs without a console at
        ''' all (CREATE_NO_WINDOW), so AttachConsole-based graceful
        ''' shutdown is unavailable on this path — use only for
        ''' games whose shutdown signal is stdin EOF or similar.
        '''
        ''' Wins over RequiresConsoleIsolation if both are set
        ''' (stdio capture and console isolation are mechanically
        ''' incompatible — captured stdio doesn't have a console).
        ''' </summary>
        Public Property StdoutIsLog As Boolean = False

        ''' <summary>
        ''' True when the game executable defeats CREATE_NEW_CONSOLE
        ''' — typically by doing FreeConsole +
        ''' AttachConsole(ATTACH_PARENT_PROCESS) at startup, which
        ''' makes its output reattach to whatever console its parent
        ''' has. Without isolation that's the node's terminal.
        '''
        ''' Setting this to True asks the node to put an
        ''' intermediate process between itself and the game so the
        ''' game's reattach target is the intermediate's hidden
        ''' console, not the node's terminal. Graceful shutdown via
        ''' AttachConsole + CTRL_C_EVENT keeps working because the
        ''' game and intermediate share a console group.
        '''
        ''' Identifying clue that a game needs this: launching it
        ''' with CREATE_NEW_CONSOLE doesn't spawn a conhost.exe in
        ''' Task Manager. Cleanly-behaved games (Last Oasis MistServer)
        ''' do spawn a conhost; Factorio doesn't.
        '''
        ''' Ignored when StdoutIsLog is True — there's no console to
        ''' isolate when stdio is captured.
        ''' </summary>
        Public Property RequiresConsoleIsolation As Boolean = False

        ''' <summary>
        ''' How long the node should wait, after a tailed log file
        ''' first appears on disk, before opening it for reading.
        ''' Originally added as a precaution against UE4 servers
        ''' tripping when the file was opened during their init
        ''' phase, so the legacy default is 5000ms.
        '''
        ''' Plugins for engines that don't share UE4's sensitivity
        ''' — Factorio in particular — should set this to 0 so
        ''' fast-crashing instances still get their initialisation
        ''' log captured into the manager's buffer before the
        ''' process exits and the tailer is cancelled. With the
        ''' tailer's open-read-close + FileShare.ReadWrite |
        ''' FileShare.Delete pattern, opening immediately is safe
        ''' for any engine that doesn't lock the file exclusively
        ''' against readers; the delay only exists for those that
        ''' do.
        '''
        ''' Negative values clamp to 0 on the node side. The
        ''' default of -1 here lets the node distinguish "plugin
        ''' didn't set it" (apply legacy 5000ms) from "plugin
        ''' explicitly chose 0".
        ''' </summary>
        Public Property LogTailerStartDelayMs As Integer = -1
    End Class

    ''' <summary>
    ''' Opt-in interface plugins implement to customise how the
    ''' node spawns their game server process. Plugins that don't
    ''' implement this interface get the same defaults as a
    ''' LaunchOptions with everything left unset — the node
    ''' decides spawn details based on declared log sources. See
    ''' the comment block above LaunchOptions for the full
    ''' rationale.
    ''' </summary>
    Public Interface ILaunchOptionsProvider
        ''' <summary>
        ''' Returns the launch options for a specific instance. The
        ''' Manager calls this once per start, just before sending
        ''' StartInstanceRequest to the node — plugins can vary the
        ''' answer per-instance based on InstanceConfig.CustomFields
        ''' if needed (e.g. a debug toggle). May return Nothing,
        ''' which the manager treats as "all defaults".
        ''' </summary>
        Function GetLaunchOptions(config As InstanceConfig) As LaunchOptions
    End Interface

End Namespace