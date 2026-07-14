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
        ''' <summary>
        ''' Renders as a free-text combo box whose dropdown items are
        ''' populated at edit time from the contents of one of the
        ''' plugin's IManagedDirectoriesProvider entries (specified
        ''' via ConfigFieldDescriptor.ManagedDirectoryRef). Edit-form
        ''' implementations that don't supply a file-list provider
        ''' show an empty dropdown but still accept free text — the
        ''' field round-trips like Text in the absence of a provider,
        ''' which keeps the read-only Configuration tab on InstancePanel
        ''' working unchanged. Underlying storage is a plain string
        ''' (the chosen filename), same wire shape as Text.
        ''' </summary>
        ManagedFilePicker

        ''' <summary>
        ''' Renders as a read-only inline banner/callout instead of an
        ''' input control — a prominent, can't-miss message box drawn
        ''' inline between the surrounding fields. The descriptor's
        ''' Label is the bold banner heading and Description is the
        ''' body; no value is collected or persisted for a Notice
        ''' field (it never appears in the extracted values dict, so
        ''' it needs no real storage Key). Use it for "special
        ''' criteria" the operator must not miss — e.g. Conan's
        ''' reserved pinger port at game port + 1. Place the
        ''' descriptor in schema order where the banner should appear.
        ''' </summary>
        Notice
    End Enum

    ''' <summary>
    ''' How a game server installation is acquired.
    ''' </summary>
    Public Enum InstallMethod
        SteamCmd
        DirectDownload
        Manual
    End Enum

    ''' <summary>
    ''' OS platform of a GSM node, surfaced on /api/version and
    ''' /api/status responses and propagated to plugins via
    ''' InstanceConfig.Platform / InstallationConfig.Platform. Lets
    ''' a plugin pick the right executable name, archive type, post-
    ''' install steps, etc. without sniffing path-shape heuristics or
    ''' assuming the node OS matches the manager's.
    '''
    ''' Wire format is the string name ("Windows", "Linux", "Unknown")
    ''' via the JsonStringEnumConverter attribute, so adding new
    ''' platforms in the future doesn't depend on integer ordering.
    ''' Unknown is the natural default for old nodes that pre-date
    ''' this contract field — the manager treats Unknown as "fall back
    ''' to legacy best-effort behaviour" (e.g. emit dual-candidate
    ''' executable paths so its candidate-probe loop can find the
    ''' right one).
    ''' </summary>
    <JsonConverter(GetType(JsonStringEnumConverter))>
    Public Enum NodePlatform
        Unknown = 0
        Windows = 1
        Linux = 2
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

        ''' <summary>
        ''' Relative weight this step contributes to the overall
        ''' install progress bar. The runner converts step indices
        ''' to bar percent using a weighted sum: bar = (sum of
        ''' completed-step weights + current-step weight ×
        ''' within-step pct) / total weight × 100. So a step with
        ''' weight 10 occupies ten times as much of the bar as a
        ''' weight-1 step.
        '''
        ''' Defaults are tuned for time-to-completion ratios: the
        ''' download/SteamCMD steps that dominate install time get
        ''' a 10× weight via their constructors, and the
        ''' seconds-long copy/write/finalise steps stay at the
        ''' base 1.0. The result is a bar that visually tracks
        ''' real elapsed time rather than steps-completed — a
        ''' multi-minute download fills most of the bar instead
        ''' of competing for equal slices with a 5-second copy.
        '''
        ''' Plugins may override per construction site for cases
        ''' the defaults don't fit (e.g. a tiny 2 MB ancillary
        ''' download alongside the main game archive can be
        ''' constructed with Weight = 1).
        '''
        ''' 0 or negative weights are treated as 1.0 by the runner
        ''' so a plugin can't accidentally produce a divide-by-
        ''' zero or a step that contributes nothing.
        ''' </summary>
        Public Property Weight As Double = 1.0
    End Class

    ''' <summary>
    ''' Download and install via SteamCMD.
    ''' </summary>
    Public Class SteamCmdStep
        Inherits InstallStep

        ' Default Weight to 10× — SteamCMD download dominates
        ' install time on every game we ship for, so the bar
        ' should reflect that. Plugins can still set Weight
        ' explicitly via the With initializer to override.
        Public Sub New()
            Weight = 10.0
        End Sub

        Public Property AppId As Integer
        Public Property BetaBranch As String
        Public Property BetaPassword As String
        Public Property ValidateFiles As Boolean = True
        Public Property RequiresLogin As Boolean = False
        Public Property Platform As String = "windows"
    End Class

    ''' <summary>
    ''' Download a file from a URL.
    '''
    ''' Archive extraction notes:
    '''
    '''   - ExtractArchive=True triggers the node's archive
    '''     extractor, which uses the file extension to dispatch
    '''     (.zip via System.IO.Compression, .tar.xz / .txz via
    '''     SharpCompress's XZStream + TarReader, everything else
    '''     via SharpCompress.ArchiveFactory). Pax extended-header
    '''     pseudo-entries are filtered out automatically — they
    '''     used to leak onto disk as garbage "@PaxHeader" files
    '''     in earlier versions.
    '''
    '''   - StripTopLevelDirectory=True asks the extractor to
    '''     hoist contents up one level when the archive's entries
    '''     all sit under a single top-level directory. Many
    '''     release tarballs follow this convention (autotools-
    '''     style "factorio_2.0.76.tar.xz" → everything under
    '''     factorio/) and without stripping, the install ends up
    '''     a level deeper than every other code path expects:
    '''     plugin-relative paths like "bin/x64/factorio" wouldn't
    '''     resolve, version-detection reads of
    '''     "data/base/info.json" wouldn't find the file, and
    '''     working-directory-relative launch args wouldn't match
    '''     the binary's actual location. When the flag is set
    '''     and the archive does NOT have a single top-level
    '''     directory (multiple top-level entries), the extractor
    '''     leaves the layout alone — the flag is a request, not
    '''     a guarantee.
    ''' </summary>
    Public Class DownloadFileStep
        Inherits InstallStep

        ' Default Weight to 10× — large game-archive downloads
        ' (Factorio's ~600 MB tarball, etc.) dominate install
        ' time. Plugins doing small ancillary downloads should
        ' set Weight = 1 explicitly.
        Public Sub New()
            Weight = 10.0
        End Sub

        Public Property Url As String
        Public Property DestinationRelativePath As String
        Public Property ExtractArchive As Boolean = False
        Public Property StripTopLevelDirectory As Boolean = False

        ''' <summary>
        ''' Install-root-relative directory to extract the archive
        ''' into. Nothing/empty (the default) preserves the legacy
        ''' behaviour of extracting to the install root. Set it when
        ''' the archive's contents belong in a subdirectory — e.g.
        ''' Stardew Valley's dedicated-server mod zip (root folder
        ''' "DedicatedServer/") extracting into "Mods". The directory
        ''' is created if missing. StripTopLevelDirectory composes
        ''' with this: the hoist target becomes the subdirectory
        ''' instead of the install root. Ignored when ExtractArchive
        ''' is False. Only meaningful for nodes new enough to carry
        ''' this field — older nodes deserialize-and-drop it and
        ''' extract to the root as before.
        ''' </summary>
        Public Property ExtractToRelativePath As String

        ''' <summary>
        ''' Optional allowlist of archive entry paths (relative,
        ''' forward slashes, case-insensitive) to extract; every
        ''' other entry is skipped. Empty/Nothing extracts
        ''' everything (legacy behaviour). Motivating case: the
        ''' mesa-dist-win 7z is ~1 GB unpacked but Stardew needs
        ''' exactly two DLLs — filtering skips writing the rest
        ''' and lets the extractor STOP as soon as all listed
        ''' entries have been produced, which on large archives
        ''' cuts minutes down to seconds. Relative directory
        ''' structure of matched entries is preserved under the
        ''' extraction target. Older nodes deserialize-and-drop
        ''' the field and extract everything as before.
        ''' </summary>
        Public Property ExtractOnlyPaths As List(Of String)
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

        ''' <summary>
        ''' Spawn with a real (invisible) console and NO stream
        ''' redirection. Console-UI installers that call
        ''' Console.Clear()/ReadKey — SMAPI's does — crash with
        ''' "handle is invalid" when stdout is a redirected pipe,
        ''' because those APIs need a screen buffer. With this set the
        ''' child gets its own console (CreateNoWindow keeps it
        ''' invisible), all console APIs work, and stdout is not
        ''' captured. Pair with the tool's non-interactive flags
        ''' (e.g. SMAPI's --no-prompt) since nothing can answer
        ''' prompts; the TimeoutMs guard remains the backstop.
        ''' </summary>
        Public Property RequiresRealConsole As Boolean = False
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

        ''' <summary>
        ''' Additional ports this field reserves relative to its own
        ''' value, expressed as integer offsets. For each offset N,
        ''' the value (this field's port + N) is treated by the
        ''' manager's PortAllocator as occupied — folded into the
        ''' node-wide "in use" set for both new-instance suggestion
        ''' and save-time conflict validation — even though no
        ''' editable field holds it.
        '''
        ''' The motivating case is a game that hard-codes a companion
        ''' port at a fixed offset from a configurable one and gives
        ''' the operator no way to move it: Conan Exiles' "pinger"
        ''' port is always game port + 1 (UDP), so the Conan plugin
        ''' sets ReservedPortOffsets = {1} on its Port field. Without
        ''' it, the allocator would happily suggest or accept a
        ''' second instance's game/query port on a neighbour's pinger
        ''' and that server would silently fail to list.
        '''
        ''' Only meaningful on fields with IsPort = True; ignored
        ''' otherwise. The reservation is protocol-agnostic, matching
        ''' how the allocator treats every port (bare integers, no
        ''' TCP/UDP distinction) — a deliberate slight over-
        ''' reservation in exchange for simplicity. Nothing/empty
        ''' means the field reserves only its own value, which is
        ''' exactly what every existing port field already did.
        ''' </summary>
        Public Property ReservedPortOffsets As List(Of Integer)

        ''' <summary>
        ''' For FieldType=ManagedFilePicker: the RelativePath of the
        ''' ManagedDirectory whose contents populate the dropdown.
        ''' Must match one of the entries returned by the plugin's
        ''' IManagedDirectoriesProvider.GetManagedDirectories — the
        ''' edit form looks up by RelativePath equality (case
        ''' insensitive). Ignored for any other FieldType.
        '''
        ''' Example: a Factorio SaveFile field sets this to "saves"
        ''' so the dropdown lists every .zip in the install's saves/
        ''' directory. The user can still type a name that doesn't
        ''' exist in the dropdown — the combo is DropDown style, not
        ''' DropDownList — because save files can be uploaded out-of-
        ''' band (manual SCP, future ManagedFilesPanel upload before
        ''' the form refreshes its dropdown, etc.).
        ''' </summary>
        Public Property ManagedDirectoryRef As String
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

        ''' <summary>
        ''' OS platform of the node this instance lives on. The
        ''' Manager populates this from the node's /api/version
        ''' response before invoking plugin methods so plugins can
        ''' return platform-specific paths (e.g. Windows vs Linux
        ''' executable names) directly. NodePlatform.Unknown means
        ''' the node is too old to surface the field — plugins should
        ''' fall back to platform-agnostic best-effort behaviour
        ''' (e.g. emit candidates for both platforms).
        ''' </summary>
        Public Property Platform As NodePlatform
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

        ''' <summary>
        ''' OS platform of the node this installation lives on. See
        ''' InstanceConfig.Platform for full rationale; the same
        ''' Manager-side resolution applies before GetInstallSteps /
        ''' GetUpdateSteps invocations.
        ''' </summary>
        Public Property Platform As NodePlatform
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
    ''' Opt-in capability a log parser implements when it keeps
    ''' connection-correlation state that must SURVIVE parser
    ''' recreation across log-stream reconnects.
    '''
    ''' The motivating case is Last Oasis: the parser binds each
    ''' connection's RemoteAddr to the player name seen at
    ''' "Join succeeded", so a later close line (which carries only
    ''' the address, not the name) can be attributed to the right
    ''' player. That binding lives on the parser instance — but the
    ''' Manager recreates the parser on every log-stream reconnect.
    ''' A fresh parser starts with an empty binding table, so a
    ''' disconnect arriving after a reconnect loses its name. For a
    ''' clean quit (UNetConnection::Close) the Manager's nameless-
    ''' leave heuristic still catches it; for a timeout/kick whose
    ''' only signal is UChannel::Close the parser drops it as a
    ''' no-match, and the session is left open in history forever.
    '''
    ''' Implementing this interface lets the Manager own ONE binding
    ''' store per instance and hand the same dictionary to each
    ''' parser it (re)creates, so bindings persist across reconnects
    ''' and are cleared only when the instance actually stops.
    ''' Parsers with no cross-reconnect state simply don't implement
    ''' it and are completely unaffected.
    ''' </summary>
    Public Interface IConnectionBindingAware
        ''' <summary>
        ''' Manager-supplied, instance-scoped store of
        ''' RemoteAddr -> resolved player name. The Manager assigns
        ''' this once, right after creating the parser and before
        ''' feeding it any line; the SAME dictionary instance is
        ''' reused across parser recreations for a given instance.
        ''' Implementations use it as their backing store for
        ''' connection bindings instead of a private field, so the
        ''' state outlives any single parser. Never assigned Nothing.
        ''' </summary>
        Property ConnectionBindings As IDictionary(Of String, String)
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

        ''' <summary>
        ''' Read the currently-installed version off the node's
        ''' filesystem in the same format GetLatestVersionAsync
        ''' returns. Format consistency is critical: the Manager
        ''' compares the two strings by inequality to detect
        ''' "out of date", so a plugin that returns "2.0.76" from
        ''' GetLatestVersionAsync MUST return a comparable string
        ''' ("2.0.76", not "installed 2026-05-08") from this method.
        '''
        ''' Plugins typically read a version-bearing file inside the
        ''' install directory — Factorio reads data/base/info.json,
        ''' a Minecraft plugin would read version.json, etc. The
        ''' INodeClient parameter exposes the node's file ops API
        ''' (DownloadFileAsync) so the plugin can pull files without
        ''' having direct filesystem access; allowedRoots /
        ''' allowedExtensions on those calls scope the access to
        ''' just what's needed.
        '''
        ''' Called by the Manager:
        '''   - At install/update completion to stamp
        '''     InstalledVersion with a value that compares cleanly
        '''     against future GetLatestVersionAsync results.
        '''   - On VersionCheckService poll cycles to opportunistically
        '''     re-read — catches drift and upgrades legacy rows that
        '''     pre-date this method.
        '''
        ''' Called only for non-SteamCmd installs. SteamCmd installs
        ''' have their version tracked via the appmanifest ACF
        ''' buildid and don't go through this code path even if the
        ''' plugin implements IVersionAwarePlugin.
        '''
        ''' Should return Nothing on any failure (file missing,
        ''' parse failure, network error talking to the node) so
        ''' the caller falls back to a synthetic provenance stamp
        ''' rather than recording a meaningless value.
        ''' </summary>
        Function GetInstalledVersionAsync(
            config As InstallationConfig,
            client As GSM.Node.Api.INodeClient,
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
    '  IPrerequisiteProvider — opt-in host-runtime declarations
    '
    '  Lets a plugin declare host-side runtime dependencies its
    '  game needs to launch (e.g. the Microsoft VC++ 2015-2022
    '  Redistributable for Conan Exiles, which crashes with a
    '  silent -1073741515 exit code on machines lacking it). The
    '  Manager queries the node for each declared name during the
    '  new-installation flow; any reported as missing surface as
    '  Warning-severity pre-install notices with the node-supplied
    '  download URL and install instructions, alongside the
    '  plugin's static IInstallationNoticeProvider notices.
    '
    '  Why the names are opaque strings (not enum members or
    '  typed objects):
    '   - The node owns the catalog of recognised names + their
    '     detection logic + their display metadata. Plugins just
    '     name what they need; the node returns enriched results.
    '   - Adding new prereqs (DirectX, .NET runtime versions,
    '     OpenAL, etc.) is a node-side change without bumping the
    '     plugin contract version.
    '   - Plugins that target a prereq the node doesn't recognise
    '     get Recognized=False back and the Manager silently
    '     skips it — graceful fallback for older nodes.
    '
    '  Plugins that don't implement this interface skip the
    '  prerequisite-check step entirely. Returning an empty list
    '  is equivalent (and the right answer for plugins that
    '  conditionally need a prereq — return Nothing/[] when the
    '  current configuration doesn't need it).
    ' ============================================================

    Public Interface IPrerequisiteProvider
        ''' <summary>
        ''' Well-known names of host-side runtime dependencies this
        ''' game requires. See the node's PrerequisiteProbe catalog
        ''' for the current set of recognised names. Initial entry:
        '''
        '''   "vcredist-2015-2022-x64"
        '''     Microsoft Visual C++ 2015-2022 Redistributable (x64).
        '''     Detected via the canonical
        '''     HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64
        '''     registry key (Installed = 1).
        '''
        ''' Names should be lowercase, kebab-case, version-suffixed
        ''' when the runtime ships multiple incompatible major
        ''' versions (e.g. "vcredist-2015-2022-x64" vs a future
        ''' "vcredist-2026-x64" with a different runtime ABI).
        ''' </summary>
        Function GetRequiredPrerequisites() As IReadOnlyList(Of String)
    End Interface

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

        ''' <summary>
        ''' Plugin's preferred default for the time the Manager
        ''' should wait for a graceful exit signal (Ctrl+C on
        ''' Windows, SIGTERM on Linux) to take effect before
        ''' force-killing the process. Used by
        ''' InstanceManager.StopInstanceAsync as the fallback
        ''' when the instance's own "GracefulTimeoutMs" custom
        ''' field is unset, replacing the universal 25-second
        ''' default for plugins that opt in.
        '''
        ''' Why per-plugin: graceful-shutdown duration is
        ''' game-engine-specific, not generic. UE4 dedicated
        ''' servers with small world state finish RequestEngineExit
        ''' in a few seconds (Last Oasis tile state is small,
        ''' Factorio's autosave is fast). UE-based survival
        ''' games with persistent worlds run minutes of cleanup
        ''' before exiting — Conan Exiles writes the entire
        ''' game.db to disk and waits for every connected client
        ''' to acknowledge the shutdown packet, both of which
        ''' scale with world size and player count and routinely
        ''' exceed 60 seconds on a populated server. Surfacing
        ''' the timeout through this opt-in lets each plugin
        ''' calibrate against its engine's actual shutdown cost.
        '''
        ''' Users can still override per-instance by setting a
        ''' "GracefulTimeoutMs" custom field on the instance —
        ''' that takes precedence over the plugin's preference.
        '''
        ''' Negative values (including the -1 default) mean
        ''' "plugin doesn't have an opinion" and let the Manager
        ''' apply its universal 25-second fallback. 0 or positive
        ''' values are honoured verbatim.
        ''' </summary>
        Public Property GracefulShutdownTimeoutMs As Integer = -1

        ''' <summary>
        ''' Environment variables to set on the spawned game process,
        ''' merged over the node process's inherited environment.
        ''' StartInstanceRequest.EnvironmentVars has carried this on
        ''' the wire since the original contract; this property gives
        ''' plugins a way to populate it (the Manager previously always
        ''' sent an empty dictionary). First consumer: Stardew Valley
        ''' on GPU-less Windows nodes, which needs
        ''' GALLIUM_DRIVER=llvmpipe for Mesa software rendering.
        ''' Nothing or empty means no additions.
        ''' </summary>
        Public Property EnvironmentVars As Dictionary(Of String, String)
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

    ' ============================================================
    '  IManagedDirectoriesProvider — opt-in file management capability
    '
    '  Phase 4c-1: lets a plugin declare which subdirectories of an
    '  installation are exposed for end-user file management (saves,
    '  config dumps, mods, screenshots, etc.). Manager-side code
    '  uses this list to populate the per-instance file management
    '  UI; the node-side /api/instances/{id}/files/* endpoints
    '  validate that incoming requests target one of these
    '  whitelisted roots before touching disk.
    '
    '  Plugins that don't implement this interface have no managed
    '  directories — the manager hides file-management UI for
    '  instances of that game and rejects file ops requests before
    '  ever calling the node. New plugins opt every directory in
    '  explicitly; nothing is exposed by default.
    '
    '  Per-directory permission flags let a plugin distinguish
    '  read-only diagnostic dirs (Read alone) from writable saves
    '  (Read|Write|Delete). The node enforces the manager's
    '  declared permissions request-by-request — it does not
    '  cache them across calls.
    '
    '  Token "{InstanceId}" in RelativePath is reserved for future
    '  multi-instance-per-installation games (see Phase 4c plan,
    '  D2). The manager substitutes the live instance id before
    '  sending the path to the node. Today's plugins return static
    '  paths because MaxInstancesPerInstallation = 1 is the norm.
    ' ============================================================

    ''' <summary>
    ''' Permission flags for ManagedDirectory entries. Combine
    ''' with Or to grant multiple permissions on one directory.
    ''' Read covers both listing the directory and downloading
    ''' files from it; Write covers creating or overwriting
    ''' files; Delete covers removing files. None effectively
    ''' hides the directory — the manager skips it when building
    ''' UI.
    ''' </summary>
    <Flags()>
    Public Enum DirPermissions
        None = 0
        Read = 1
        Write = 2
        Delete = 4
    End Enum

    ''' <summary>
    ''' One whitelisted directory under an installation root that
    ''' end users can browse and manage via the file ops endpoints.
    ''' Returned by IManagedDirectoriesProvider.GetManagedDirectories.
    ''' </summary>
    Public Class ManagedDirectory
        ''' <summary>
        ''' Path relative to the installation root. Forward or
        ''' backward slashes accepted; the manager normalises to
        ''' the node's native separator before sending. Examples:
        ''' "saves", "config", "mods". The token "{InstanceId}"
        ''' anywhere in the path is substituted by the manager
        ''' for the live instance id at request time — reserved
        ''' for future games that share an installation across
        ''' multiple instances and need per-instance subdirs.
        ''' </summary>
        Public Property RelativePath As String

        ''' <summary>
        ''' Friendly label for UI tabs and headings. e.g.
        ''' "Saves", "Server Config", "Installed Mods".
        ''' </summary>
        Public Property DisplayName As String

        ''' <summary>
        ''' Permissions granted on this directory. Defaults to
        ''' Read so a plugin that returns a bare-minimum entry
        ''' (RelativePath + DisplayName) gets a safe read-only
        ''' view rather than accidentally writable storage.
        ''' </summary>
        Public Property Permissions As DirPermissions = DirPermissions.Read

        ''' <summary>
        ''' Optional file extension allowlist (each entry leading
        ''' with "."). When non-empty, the node and manager reject
        ''' any download/upload/delete on a file whose extension
        ''' isn't in this list. Listings filter to matching
        ''' extensions only. Leave Nothing/empty to allow any
        ''' extension. Comparison is case-insensitive.
        ''' </summary>
        Public Property AllowedExtensions As List(Of String)
    End Class

    ''' <summary>
    ''' Opt-in interface plugins implement to expose a set of
    ''' directories under each instance's installation that can be
    ''' managed (listed, downloaded, uploaded, deleted) through
    ''' the Manager UI. Plugins that don't implement this
    ''' interface have no managed directories — file management
    ''' UI is suppressed for instances of that game.
    ''' </summary>
    Public Interface IManagedDirectoriesProvider
        ''' <summary>
        ''' Returns the list of directories the user can manage
        ''' for an instance. Called every time the manager
        ''' initiates a file op, so implementations should be
        ''' cheap (no I/O, no blocking work). May return an empty
        ''' list to temporarily hide all file management — the
        ''' manager treats that the same as "no provider".
        ''' </summary>
        Function GetManagedDirectories(config As InstanceConfig) _
            As IReadOnlyList(Of ManagedDirectory)
    End Interface

    ' ============================================================
    '  IFileGenerationProvider — opt-in file-producing operations
    '
    '  Phase 4c-3 (generic). Lets a plugin expose schema-driven
    '  one-off operations that produce a file under one of the
    '  plugin's managed directories. The canonical case is map
    '  generation (Factorio's `factorio.exe --create`), but the
    '  contract knows nothing about maps, presets, or seeds — the
    '  plugin owns the entire question of "what does the user need
    '  to fill in" via a ConfigFieldDescriptor schema, and the
    '  manager just renders the schema with SchemaFormBuilder.
    '
    '  The same shape works for any plugin-defined file-producing
    '  operation: "generate map", "generate ARK INI from template",
    '  "create blank world", "convert save format", etc. A future
    '  plugin that wants "edit a 30-field server-settings.json then
    '  run a regeneration step" returns a 30-field schema and a
    '  builder that writes a WriteFileStep for the JSON.
    '
    '  The Manager UI offers a button (label plugin-defined,
    '  default "Generate New...") on the ManagedFilesPanel whose
    '  RelativePath equals the plugin's GetTargetDirectoryRef.
    '  Clicking opens a sibling tab containing the rendered form;
    '  on Generate the panel collects values, calls
    '  BuildGenerationSteps, ships the resulting bundle to the
    '  node via the existing /generate-map endpoint (named for
    '  history; the node-side machinery is fully generic).
    '
    '  Plugins that don't implement this interface get no
    '  Generate button on any ManagedFilesPanel.
    ' ============================================================

    ''' <summary>
    ''' Bundle returned by IFileGenerationProvider.BuildGenerationSteps:
    ''' the step list the node executes plus the relative path of
    ''' the file the steps are expected to produce. The expected-
    ''' output path lets the node verify the file actually appeared
    ''' on disk after the steps complete (an engine that exits 0
    ''' but produces no output is detected as a failure).
    ''' </summary>
    Public Class GenerationStepBundle
        ''' <summary>
        ''' Steps the node runs sequentially. Currently must be
        ''' WriteFileStep or RunProcessStep instances — other
        ''' types are rejected by the node before execution.
        ''' </summary>
        Public Property Steps As List(Of InstallStep)

        ''' <summary>
        ''' Relative path of the file the steps are expected to
        ''' produce, e.g. "saves/my-world.zip". The node verifies
        ''' this exists on disk after the steps run; absence is
        ''' a failure even if every step exited 0. Leave Nothing
        ''' or empty to skip output verification (some operations
        ''' don't have a single canonical output file — though the
        ''' "file generation" framing implies they usually do).
        ''' </summary>
        Public Property ExpectedOutputRelativePath As String

        ''' <summary>
        ''' Hard timeout for the whole step sequence in seconds.
        ''' 0 falls back to the node's default (300s). Per-
        ''' RunProcessStep TimeoutMs is honoured independently.
        ''' </summary>
        Public Property TimeoutSeconds As Integer = 0
    End Class

    ''' <summary>
    ''' Opt-in interface plugins implement to expose a schema-
    ''' driven file-producing operation in the Manager UI. The
    ''' contract is deliberately game-agnostic: the plugin
    ''' declares which managed directory the operation targets,
    ''' supplies a schema describing what to ask the user, and
    ''' converts the filled-in values into a step list. The
    ''' Manager renders the schema with the same SchemaFormBuilder
    ''' it uses for instance configuration — no game-specific
    ''' UI code lives on the manager side.
    ''' </summary>
    Public Interface IFileGenerationProvider
        ''' <summary>
        ''' RelativePath of the ManagedDirectory this operation
        ''' produces files under. The Manager shows the Generate
        ''' button only on the matching ManagedFilesPanel —
        ''' suppressed elsewhere even if the same plugin
        ''' implements both this interface and
        ''' IManagedDirectoriesProvider with multiple entries.
        '''
        ''' Must match one of the plugin's ManagedDirectory
        ''' RelativePath values; the manager looks up by
        ''' case-insensitive equality.
        ''' </summary>
        Function GetTargetDirectoryRef() As String

        ''' <summary>
        ''' Optional label for the button. Returning Nothing or
        ''' empty falls back to "Generate New...". Use this to
        ''' say "New Map...", "New World...", or "Create from
        ''' Template..." as appropriate to the plugin's domain.
        ''' </summary>
        Function GetButtonLabel() As String

        ''' <summary>
        ''' Optional title for the generated tab and the form
        ''' header inside it. Returning Nothing or empty falls
        ''' back to "Generate File". Same domain-specific
        ''' labelling as GetButtonLabel.
        ''' </summary>
        Function GetTabTitle() As String

        ''' <summary>
        ''' Schema for the form rendered in the generation tab.
        ''' Plugins return whatever fields the user needs to fill
        ''' in: a preset enum, a numeric seed, a save name, a
        ''' batch of nested config values, etc. ManagedFilePicker
        ''' fields work too if the operation needs to pick from
        ''' an existing file.
        '''
        ''' Implementations should be cheap — invoked once when
        ''' the tab opens. May vary returned schema by
        ''' instanceConfig if the operation's field set is
        ''' instance-dependent (rarely needed; most plugins
        ''' return a static list).
        ''' </summary>
        Function GetGenerationSchema(instanceConfig As InstanceConfig) _
            As IReadOnlyList(Of ConfigFieldDescriptor)

        ''' <summary>
        ''' Build the step bundle from the form values the user
        ''' submitted. Keys in `values` match the schema's field
        ''' Keys; values are the strings produced by the form's
        ''' ValueExtractor (enum dropdowns produce the displayed
        ''' string, integer fields the numeric value as string,
        ''' booleans "true"/"false", etc.).
        '''
        ''' Plugin owns all interpretation — naming, validation,
        ''' default-filling, derivation of the output filename.
        ''' Throwing here is acceptable on bad input; the panel
        ''' surfaces the exception message to the user. Returning
        ''' a bundle with no steps causes the panel to bail with
        ''' "plugin produced no steps" (treated the same as a
        ''' silently-empty step list, the more common bug shape).
        ''' </summary>
        Function BuildGenerationSteps(values As Dictionary(Of String, String),
                                       instanceConfig As InstanceConfig) _
            As GenerationStepBundle
    End Interface

    ' ============================================================
    '  IInstanceFileEditorProvider — opt-in structured file editing
    '
    '  Phase 4c-4. Lets a plugin expose a known config file as a
    '  structured form rather than raw text. The canonical case is
    '  Factorio's server-settings.json (server name, visibility,
    '  auth, autosave intervals, etc.) but the contract works for
    '  any single-file configuration whose fields the plugin can
    '  describe via ConfigFieldDescriptor.
    '
    '  The Manager renders one tab per editor on the InstancePanel
    '  (between Configuration and the managed-files tabs). The tab
    '  hosts a SchemaFormBuilder-rendered form plus Save/Reload
    '  buttons. Plugin owns:
    '    - Which fields the form has (GetInstanceFileEditors → schema)
    '    - How file text maps to form values (ReadFileToValues)
    '    - How form values + existing file text become new file
    '      text (WriteValuesToFile) — the existing text is passed
    '      back so plugin can preserve unknown fields the user
    '      added by hand outside the schema
    '
    '  File access: the Manager reads/writes via the existing
    '  /api/instances/{id}/files endpoints, deriving allowedRoots
    '  from the editor's RelativePath (parent dir, or the filename
    '  itself for files at the install root). Plugins don't need
    '  to declare the file as a managed directory.
    '
    '  Missing-file behaviour: if the file doesn't exist on the
    '  node, the Manager calls ReadFileToValues with empty text;
    '  the schema falls back to DefaultValue per field. On Save,
    '  WriteValuesToFile is called with empty existingText and
    '  must produce a valid full file (plugin builds from scratch).
    ' ============================================================

    ''' <summary>
    ''' One file editor entry returned by
    ''' IInstanceFileEditorProvider.GetInstanceFileEditors.
    ''' Plain data DTO so the plugin/manager boundary stays on
    ''' DTOs only — schema parsing/serialisation logic lives
    ''' behind ReadFileToValues/WriteValuesToFile on the interface.
    ''' </summary>
    Public Class InstanceFileEditor
        ''' <summary>
        ''' Stable plugin-defined identifier passed back to
        ''' Read/WriteValuesToFile so a plugin with multiple
        ''' editors can dispatch on it. Single-editor plugins can
        ''' use any constant value.
        ''' </summary>
        Public Property Key As String

        ''' <summary>
        ''' Tab title shown on InstancePanel. Should be a short
        ''' user-facing label like "Server Settings" or
        ''' "World Configuration".
        ''' </summary>
        Public Property TabTitle As String

        ''' <summary>
        ''' File path relative to the install root, e.g.
        ''' "server-settings.json" or "config/world.json". Forward
        ''' or backward slashes both accepted; Manager normalises
        ''' before sending to the node. The token "{InstanceId}"
        ''' is substituted by the Manager for future multi-instance-
        ''' per-installation games.
        ''' </summary>
        Public Property RelativePath As String

        ''' <summary>
        ''' Schema rendered by SchemaFormBuilder. Same
        ''' ConfigFieldDescriptor list shape used by Edit Instance
        ''' and IFileGenerationProvider.GetGenerationSchema.
        ''' </summary>
        Public Property Schema As IReadOnlyList(Of ConfigFieldDescriptor)

        ''' <summary>
        ''' Opt-in (default False). When True, the Manager treats the
        ''' editor as a read-only LOCKOUT until the target file exists
        ''' on the node: fields and Save are disabled and a hint tells
        ''' the user to start the server once to generate the file,
        ''' then edit. Leave False (the historical behaviour) for any
        ''' file the plugin or user legitimately creates from empty —
        ''' the Manager then renders schema DefaultValues and lets
        ''' WriteValuesToFile build a fresh file.
        '''
        ''' Set True only when writing a partial file before the game
        ''' has authored its own copy would BREAK the server. Windrose
        ''' is the motivating case: ServerDescription.json carries
        ''' server-owned fields (Version / DeploymentId /
        ''' PersistentServerId / P2p*) the plugin can't synthesise;
        ''' a defaults-only partial write makes the server reject the
        ''' file and fatally fail vendor registration on next launch.
        ''' </summary>
        Public Property RequiresExistingFile As Boolean = False
    End Class

    ''' <summary>
    ''' Opt-in interface plugins implement to surface a structured
    ''' editor for a known config file. Plugins that don't
    ''' implement this interface simply have no editor tabs;
    ''' users edit those files (if any) by hand.
    ''' </summary>
    Public Interface IInstanceFileEditorProvider
        ''' <summary>
        ''' Returns the list of editors for this instance.
        ''' Implementations should be cheap — invoked once when
        ''' the InstancePanel builds its tabs. May vary the
        ''' returned RelativePath by instanceConfig.CustomFields
        ''' if the file location is configurable per instance
        ''' (Factorio's ServerSettings field is the canonical
        ''' example). Returning an empty list is equivalent to
        ''' not implementing the interface.
        ''' </summary>
        Function GetInstanceFileEditors(config As InstanceConfig) _
            As IReadOnlyList(Of InstanceFileEditor)

        ''' <summary>
        ''' Convert the on-disk file text into a flat values
        ''' dictionary the schema form can render. Keys must
        ''' match the schema's ConfigFieldDescriptor.Key values;
        ''' missing entries fall back to the schema's DefaultValue.
        '''
        ''' fileText may be empty/null when the file doesn't exist
        ''' yet — implementations should handle that by returning
        ''' an empty (or partially-populated) dictionary rather
        ''' than throwing. The schema's defaults take over for
        ''' missing keys.
        '''
        ''' editorKey identifies which editor when the plugin
        ''' returns multiple from GetInstanceFileEditors.
        ''' </summary>
        Function ReadFileToValues(editorKey As String,
                                   fileText As String) As Dictionary(Of String, String)

        ''' <summary>
        ''' Build the new file text from the user's edited values.
        ''' existingText is the verbatim file content that was last
        ''' read — implementations should parse it, update only
        ''' the schema-managed keys, and re-serialise. Unknown
        ''' top-level fields the user added by hand outside the
        ''' schema MUST round-trip unchanged.
        '''
        ''' existingText is empty/null when the file didn't exist
        ''' yet; implementations build a fresh file in that case.
        ''' Throwing here is acceptable on bad input; the panel
        ''' surfaces the exception message to the user without
        ''' uploading.
        ''' </summary>
        Function WriteValuesToFile(editorKey As String,
                                    values As Dictionary(Of String, String),
                                    existingText As String) As String
    End Interface

    ''' <summary>
    ''' Opt-in interface (ContractsVersion 2) for plugins that need
    ''' instance config written into the game's OWN config file(s) just
    ''' before launch — rather than passed on the command line.
    '''
    ''' Two motivating cases, same shape:
    '''   - File-only games (no launch args) whose port must come from
    '''     the node port allocator. The allocator stores the chosen
    '''     port in CustomFields; BuildLaunchArguments can't carry it
    '''     (there are no args), so the plugin renders it into the
    '''     config file here. (Windrose DirectConnectionServerPort.)
    '''   - Text values that garble through command-line quoting
    '''     (spaces / unicode) but read cleanly from a config file.
    '''     (Conan ServerName.)
    '''
    ''' The Manager calls this in StartInstanceAsync after merging the
    ''' config layers into CustomFields and before sending the start
    ''' request: for each path from GetStartupFiles it reads the current
    ''' file from the node (empty string if absent), calls
    ''' RenderStartupFile, and writes the result back via the same node
    ''' file endpoints IInstanceFileEditorProvider uses — only when the
    ''' rendered text differs. A read/write failure is logged and the
    ''' launch proceeds (the file keeps its last value); it does not
    ''' block start.
    '''
    ''' Single-ownership rule: a value rendered here must NOT also be
    ''' editable in an IInstanceFileEditorProvider schema for the same
    ''' file, or the two fight — this render runs last and would revert
    ''' editor edits. Move such a field to the instance Configuration
    ''' schema (GetInstanceConfigSchema) and drop it from the file
    ''' editor.
    ''' </summary>
    Public Interface IStartupFileProvider
        ''' <summary>
        ''' Relative paths (under the installation directory, forward
        ''' slashes) of the config files this plugin wants to (re)write
        ''' from instance config at start. Cheap; called once per start.
        ''' Empty list = nothing to do.
        ''' </summary>
        Function GetStartupFiles(instanceConfig As InstanceConfig) _
            As IReadOnlyList(Of String)

        ''' <summary>
        ''' Produce the new content for one file, given its CURRENT
        ''' on-disk text (empty string when the file doesn't exist yet).
        ''' Inject the instance-config values this plugin owns and
        ''' preserve everything else (round-trip unknown fields). Return
        ''' Nothing — or text equal to existingText — to skip the write.
        '''
        ''' Return Nothing when existingText is empty if the game must
        ''' create the file itself first (e.g. Windrose generates
        ''' ServerDescription.json on first launch; the rendered values
        ''' then apply from the second launch on). The plugin owns all
        ''' parsing/serialisation — reuse the same helpers as its file
        ''' editor; nothing format-specific lives on the Manager side.
        ''' </summary>
        Function RenderStartupFile(relativePath As String,
                                    instanceConfig As InstanceConfig,
                                    existingText As String) As String
    End Interface

    ''' <summary>
    ''' Opt-in interface plugins implement to declare a "shared
    ''' config group" — a set of fields that multiple
    ''' installations of the same plugin can reference via a
    ''' foreign-key association rather than re-entering across
    ''' installations. Same precedent as the Steam credentials
    ''' (one credential row, many installations point at it), but
    ''' plugin-defined so the manager schema stays game-agnostic.
    '''
    ''' Last Oasis is the canonical motivating case: CustomerKey,
    ''' ProviderKey, and RealmName are realm-wide values; an
    ''' operator typically runs multiple installations against
    ''' the same realm and shouldn't have to enter the same values
    ''' on each one. With this interface, LO declares a "Realm"
    ''' group containing those fields, installations link to a
    ''' realm row, and rotation happens once per realm rather than
    ''' once per installation.
    '''
    ''' Plugins that don't implement this interface have no shared-
    ''' config concept; all of their config lives at the
    ''' installation level (the only behaviour that existed before
    ''' Phase 5h).
    '''
    ''' Runtime semantics: the manager merges the shared group's
    ''' fields into InstanceConfig.CustomFields before invoking
    ''' plugin methods, with precedence shared-group → installation
    ''' → instance (instance overrides install, install overrides
    ''' group). Plugin code reads CustomFields("<key>") the same
    ''' way regardless of which layer supplied the value, so plugin
    ''' authors writing this interface mainly think about WHERE the
    ''' user enters each field, not HOW it's read.
    ''' </summary>
    Public Interface ISharedConfigProvider
        ''' <summary>
        ''' Stable lowercase identifier for the kind of shared
        ''' entity this plugin defines. Persisted as the
        ''' group_type column on shared_config_groups; used to
        ''' filter rows that belong to this plugin's concept of
        ''' "group". One plugin = one shared-config type for now;
        ''' multi-type support would require a richer linkage
        ''' column on installations than the current single-FK
        ''' design.
        '''
        ''' Examples: "realm" for Last Oasis, "cluster" for any
        ''' future MMO plugin that groups instances by shard.
        ''' </summary>
        ReadOnly Property SharedConfigKey As String

        ''' <summary>
        ''' User-facing label for the entity in menus, dialogs,
        ''' management screens. Title case, singular form.
        ''' Plural is derived by the UI (typically by appending
        ''' "s") so plugins shouldn't supply it. Example: "Realm".
        ''' </summary>
        ReadOnly Property SharedConfigLabel As String

        ''' <summary>
        ''' Field schema for the shared entity. Same descriptor
        ''' shape as GetInstallConfigSchema returns, so the
        ''' existing SchemaFormBuilder renders the editor for
        ''' the shared-group fields with zero extra UI code.
        ''' Fields flagged IsSensitive are encrypted at rest via
        ''' the same DPAPI helpers (CredentialService.ProtectString
        ''' / UnprotectString) that protect Steam credential
        ''' passwords.
        '''
        ''' Plugins should NOT include the same field key in both
        ''' GetSharedConfigSchema and GetInstallConfigSchema — the
        ''' merge would still work (install overrides group), but
        ''' the install editor would surface duplicate inputs and
        ''' confuse the user about which layer the value lives at.
        ''' Move the field to whichever layer matches its scope.
        ''' </summary>
        Function GetSharedConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)

        ''' <summary>
        ''' Name of the schema field whose value identifies a
        ''' group during one-time migration of pre-existing
        ''' installations. Installations whose ConfigJson contains
        ''' identical values for this field get auto-grouped into
        ''' a single shared-config row at migration time, with the
        ''' user prompted to name the group. Nothing disables
        ''' auto-migration; the user creates and links groups
        ''' manually.
        '''
        ''' Typical choice is the field representing the broadest
        ''' identity — LO uses CustomerKey because one CustomerKey
        ''' equals one realm in MyRealm's identity model.
        ''' Discriminator comparison runs on plaintext values, so
        ''' the field may be IsSensitive without affecting matching.
        ''' </summary>
        ReadOnly Property DiscriminatorFieldKey As String
    End Interface

    ''' <summary>
    ''' Phase 5h opt-in capability — plugins implement this to
    ''' control how their rows render in the History window's
    ''' "Source" column. The column merges the legacy
    ''' "Tile / Session" and "Instance" columns into a single
    ''' label, and the plugin chooses what goes in it.
    '''
    ''' Last Oasis uses this to put `{TileName} — {RealmName}
    ''' — {NodeName}/{InstallationName}` in the column when
    ''' tile + realm are both known, falling back through
    ''' shorter formats when context is missing. Conan and
    ''' Factorio could implement it to add their own context
    ''' (world name, mod set, etc.).
    '''
    ''' Plugins NOT implementing this interface fall back to a
    ''' manager-supplied default: `{NodeName}/{InstallationName}/{InstanceName}`.
    ''' </summary>
    Public Interface ISourceLabelProvider
        ''' <summary>
        ''' Render the Source-column label for one History row.
        ''' Called once per row at render time; should be cheap
        ''' (no I/O, no expensive lookups) and return a single
        ''' line of plain text. The manager wraps the result in
        ''' a ListViewItem's main text; tooltips and copy-id
        ''' actions go through ctx.InstanceId / ctx.SessionIdentity
        ''' separately, so this output is purely the visible
        ''' label.
        '''
        ''' Returning Nothing or empty falls back to the
        ''' manager-supplied default label (same as if the
        ''' plugin didn't implement the interface), so a
        ''' plugin that opts in but bails out under some
        ''' condition gets a sensible default instead of a
        ''' blank cell.
        ''' </summary>
        Function FormatSourceLabel(context As SourceLabelContext) As String
    End Interface

    ''' <summary>
    ''' Phase 5h — the bundle of context the manager passes to
    ''' ISourceLabelProvider.FormatSourceLabel for each History
    ''' row. All fields are pre-resolved by the manager so the
    ''' plugin doesn't need to know about EF, the session-host
    ''' table, or the shared-config storage layer.
    ''' </summary>
    Public Class SourceLabelContext
        ''' <summary>
        ''' Game-defined session identity, e.g. for LO this is
        ''' the raw "lastoasis:{realm_id}:{tile_id}" string
        ''' assembled by the log parser. Plugins that want to
        ''' surface a piece of this (e.g. the realm_id
        ''' substring as a fallback when the group's display
        ''' name isn't set) can parse it themselves. Nothing
        ''' for games that don't have a session concept.
        ''' </summary>
        Public Property SessionIdentity As String

        ''' <summary>
        ''' Friendly tile name observed from the parse-rule
        ''' stream (e.g. "[N5][PvE] Ikronic Pain"). Empty or
        ''' Nothing when the row's tile isn't known yet — e.g.
        ''' rows that landed before the "Started hosting tile"
        ''' sequence emitted a name, or non-session-based
        ''' games. LO plugin renders "{TileName} — …" when
        ''' set and drops the tile segment when not.
        ''' </summary>
        Public Property TileName As String

        ''' <summary>Display name of the node hosting the
        ''' instance — same string that appears in the
        ''' Nodes tree.</summary>
        Public Property NodeName As String

        ''' <summary>Display name of the installation.</summary>
        Public Property InstallationName As String

        ''' <summary>Display name of the specific instance
        ''' within the installation.</summary>
        Public Property InstanceName As String

        ''' <summary>Full instance GUID. Used by the History UI
        ''' for the right-click "Copy instance ID" action and
        ''' for the hover tooltip; plugins typically don't need
        ''' to render this in the label since the UI exposes it
        ''' separately, but it's available for plugins that
        ''' want to embed a short prefix.</summary>
        Public Property InstanceId As String

        ''' <summary>
        ''' DisplayName of the SharedConfigGroup the
        ''' installation links to, or Nothing if not linked.
        ''' For LO this is the user-set realm name from the
        ''' "Add Realm" dialog (e.g. "Site's World"). Plugins
        ''' should prefer this over digging RealmName-like
        ''' fields out of MergedConfig because the user picked
        ''' it as their friendly label for the group.
        ''' </summary>
        Public Property SharedConfigGroupName As String

        ''' <summary>
        ''' Phase 7-6 — the linked SharedConfigGroup's NON-SENSITIVE
        ''' field values (sensitive fields like keys are omitted, not
        ''' decrypted, so this stays cheap and leaks nothing into the
        ''' label path), keyed by the plugin's own shared-config field
        ''' keys. Empty/Nothing when the installation links to no
        ''' group. Lets a plugin render a field-level value distinct
        ''' from the group's DisplayName — e.g. Last Oasis reads
        ''' "RealmName" here for the canonical realm name (identical
        ''' across a realm's several per-provider-key groups), while
        ''' DisplayName carries the per-group "realm (provider)" label
        ''' used in pickers.
        ''' </summary>
        Public Property SharedConfigFields As IReadOnlyDictionary(Of String, String)
    End Class

    ' ============================================================
    '  Remote control (REST/HTTP-administered games) — opt-in
    ' ============================================================

    ''' <summary>
    ''' Context handed to IRemoteControlProvider calls. Carries what
    ''' the plugin needs to reach a game's own admin surface (HTTP
    ''' REST API or similar) on the node machine — InstanceConfig
    ''' deliberately does not know the node's address, and admin
    ''' credentials often live in the game's own config file rather
    ''' than in PowerGSM's database.
    ''' </summary>
    Public Class RemoteControlContext
        ''' <summary>
        ''' Host (name or IP, no scheme/port) the Manager uses to
        ''' reach the node this instance runs on. A game admin API
        ''' listening on the node is reachable at this host + the
        ''' game-configured port — provided it binds non-localhost
        ''' and any firewall allows the Manager through.
        ''' </summary>
        Public Property NodeHost As String

        ''' <summary>
        ''' Merged install+instance config for the instance (same
        ''' shape the lifecycle methods receive).
        ''' </summary>
        Public Property Config As InstanceConfig

        ''' <summary>
        ''' Fetch a file's text content from the instance's install
        ''' directory on the node (relative path, forward slashes).
        ''' Returns Nothing when the file doesn't exist or the fetch
        ''' fails. This is how a plugin reads admin credentials or
        ''' ports out of the game's own config file (e.g. Palworld's
        ''' AdminPassword / RESTAPIPort in PalWorldSettings.ini)
        ''' without duplicating them into PowerGSM's database.
        ''' </summary>
        Public Property FetchInstanceFile As Func(Of String, Task(Of String))
    End Class

    ''' <summary>
    ''' Opt-in interface (additive, post-0.5.0) for games whose
    ''' remote administration goes through their own out-of-band
    ''' admin channel (HTTP REST API etc.) rather than RCON or
    ''' stdin. The plugin owns the protocol entirely; the Manager
    ''' only supplies context and decides when to call.
    '''
    ''' Motivating case: Palworld — RCON deprecated upstream, REST
    ''' API (HTTP Basic, admin password from the game's own config
    ''' file) is the sanctioned channel for announce / save /
    ''' graceful shutdown / player list.
    '''
    ''' Both methods must be cheap to DECLINE: a provider whose
    ''' admin channel is disabled or unreachable returns the
    ''' decline value quickly (False / Nothing) rather than
    ''' throwing, so the Manager's fallback paths stay fast.
    ''' Exceptions are treated as declines by the Manager.
    ''' </summary>
    Public Interface IRemoteControlProvider
        ''' <summary>
        ''' Called by the Manager at the START of StopInstanceAsync,
        ''' before the node's stop endpoint. A True return means the
        ''' plugin has asked the game to shut itself down cleanly
        ''' (e.g. REST announce + save + shutdown); the Manager then
        ''' proceeds with the normal node stop call REGARDLESS of
        ''' the return value — the node must still set its stop-
        ''' intent flag so the self-initiated exit isn't classified
        ''' as a crash, and its CtrlC/SIGINT + force-kill ladder
        ''' remains the safety net if the game ignores the request.
        ''' Return False to decline (admin channel off/unreachable)
        ''' — the node path then does all the work, as today.
        ''' Keep it fast: this sits on the user's Stop click.
        ''' </summary>
        Function RequestStopAsync(context As RemoteControlContext,
                                   ct As CancellationToken) As Task(Of Boolean)

        ''' <summary>
        ''' Current online players from the game's admin channel, or
        ''' Nothing when unavailable (channel disabled, unreachable,
        ''' instance not up yet). The Manager may poll this for
        ''' running instances and use it as the player-list source
        ''' for games with no log-based player tracking. Returns the
        ''' node/UI PlayerSession shape directly — the plugin owns
        ''' the entire mapping from its game's API fields (which
        ''' name is the character vs the platform persona, id
        ''' formats, etc.); the Manager passes the list through
        ''' untouched.
        ''' </summary>
        Function GetPlayersAsync(context As RemoteControlContext,
                                  ct As CancellationToken) As Task(Of IReadOnlyList(Of GSM.Node.Api.PlayerSession))
    End Interface

End Namespace
