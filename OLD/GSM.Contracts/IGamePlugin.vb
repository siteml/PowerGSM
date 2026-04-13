Imports System.Collections.Generic
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

    ' ------------------------------------------------------------
    '  Primary interface - every game plugin implements this
    ' ------------------------------------------------------------
    Public Interface IGamePlugin

        ' Stable identifier - used as FK in all instance/install
        ' records. Never change this once installs exist.
        ' e.g. "lastoasis", "factorio"
        ReadOnly Property GameId As String

        ' Human-readable name for UI
        ReadOnly Property DisplayName As String

        ' ---- Install ----

        ' Which install methods this game supports.
        ' Drives what the UI offers when creating an Installation.
        Function GetSupportedInstallMethods() As IReadOnlyList(Of InstallMethod)

        ' Returns ordered steps for the node to execute.
        ' path      = target install directory on the node
        ' method    = whichever method the user chose
        ' config    = the Installation's config blob (deserialized
        '             by the plugin into its own typed class)
        Function GetInstallSteps(path As String,
                                 method As InstallMethod,
                                 config As InstallationConfig) As IReadOnlyList(Of InstallStep)

        ' Called after install/update to confirm files look right.
        ' Return False + a reason string if something is wrong.
        Function ValidateInstall(path As String) As ValidationResult

        ' Optional. Return Nothing for non-Steam games.
        ' Returns the branch name e.g. "experimental", "" for default.
        ' config carries SteamBranch / SteamBranchPassword fields.
        Function GetSteamBranch(config As InstallationConfig) As String

        ' ---- Launch ----

        ' Resolve the actual executable path.
        ' For Last Oasis: glob installPath for MistServer*.exe
        ' For Factorio:   return installPath\bin\x64\factorio.exe
        ' instance.ExeOverride takes precedence if set - check it first.
        Function GetExecutablePath(installPath As String,
                                   instance As InstanceConfig) As String

        ' Build the full argument string for the process.
        ' Everything game-specific lives here:
        '   LO:      -RealmKey=X -ServerName=Y -Port=Z ...
        '   Factorio: --port Z --server-settings path ...
        Function BuildCommandLine(instance As InstanceConfig) As String

        ' Working directory for the launched process.
        ' Often installPath, but some games want a per-instance subdir.
        Function GetWorkingDirectory(installPath As String,
                                     instance As InstanceConfig) As String

        ' ---- Logging ----

        ' Declares where this game's log output comes from.
        ' Return one or more sources - node monitors all of them
        ' and feeds lines into the shared ring buffer.
        ' e.g. Last Oasis:  { New StdoutLogSource() }
        '      Factorio:    { New StdoutLogSource(),
        '                     New FileLogSource("factorio-current.log") }
        Function GetLogSources(installPath As String,
                               instance As InstanceConfig) As IReadOnlyList(Of ILogSource)

        ' Live log line parser. Receives every line from all
        ' log sources and maintains derived state (player list etc).
        ' Return Nothing if the game needs no log parsing.
        Function GetLogParser() As ILogParser

        ' ---- Install monitoring (SteamCMD prompts etc) ----

        ' Monitors stdout of install/update processes for prompts
        ' that need user input (Steam Guard, Y/N confirmations...).
        ' Return Nothing if the install method never blocks on input.
        Function GetInstallMonitor() As IInstallMonitor

        ' ---- RCON ----

        ' Returns connection info for this instance's RCON endpoint.
        ' Return Nothing if the game has no RCON support.
        ' The node connects locally - RCON port need not be external.
        Function GetRconInfo(instance As InstanceConfig) As RconInfo

        ' ---- Mods ----

        ' Return Nothing if the game has no mod support.
        Function GetModManager() As IModManager

        ' ---- Startup warnings ----

        ' Called by the node just before launching an instance.
        ' Returns a list of human-readable warnings to log and surface
        ' in the manager UI. These are non-fatal - the launch proceeds -
        ' but they flag configuration issues that will likely cause
        ' problems at runtime. Return an empty list if all is well.
        ' e.g. Last Oasis: warn if OverrideConnectionAddress is not set,
        '      because the server will advertise a local IP and players
        '      outside the LAN will be unable to connect.
        Function GetStartupWarnings(installPath As String,
                                    instance As InstanceConfig) As IReadOnlyList(Of String)

        ' ---- Crash handling ----

        ' Exit codes that represent a clean, intentional shutdown.
        ' The node will NOT attempt auto-restart if the process exits
        ' with one of these codes, regardless of AutoRestart setting.
        ' Nearly all games should include 0. Some games exit with
        ' non-zero codes on a clean in-game 'quit' command - add those
        ' here rather than teaching the node about game internals.
        ' Default implementation: return {0}
        Function GetCleanExitCodes() As IReadOnlyList(Of Integer)

        ' Log line patterns that signal the process is about to crash,
        ' detected BEFORE the process has actually exited.
        ' The node pre-enters a CrashDetected sub-state when any pattern
        ' matches, allowing Discord notifications to include the relevant
        ' log context (stack trace etc) before the process is confirmed dead.
        ' Patterns are treated as case-insensitive substring matches.
        ' Return an empty list if the game gives no crash forewarning.
        ' e.g. Last Oasis: {"FATAL ERROR", "Access violation"}
        Function GetCrashSignalPatterns() As IReadOnlyList(Of String)

        ' ---- Version detection (for update polling) ----

        ' Read the currently installed version from local files.
        ' Use whatever the game provides: build.txt, app manifest,
        ' a version binary flag, the exe file version, etc.
        ' Return String.Empty if the version cannot be determined
        ' (do not throw - the caller handles empty gracefully).
        Function GetCurrentVersion(installPath As String) As String

        ' Query the upstream source for the latest available version.
        ' For SteamCMD games: call Steam's app info API.
        ' For direct download games: check the game's version endpoint.
        ' Return String.Empty on any failure - a failed check must
        ' never trigger an update. Cancellation must be respected.
        Function GetLatestVersion(config As InstallationConfig,
                                  cancellation As CancellationToken) As Task(Of String)

        ' ---- Config schema ----

        ' Returns a schema descriptor so the UI can render a config
        ' form for this game without knowing anything about it.
        ' Each entry describes one field: key, label, type, default.
        Function GetInstanceConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)
        Function GetInstallationConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)

    End Interface


    ' ------------------------------------------------------------
    '  Steam credentials
    '  Stored as named records in the manager's SQLite.
    '  Passwords are encrypted at rest using Windows DPAPI
    '  (System.Security.Cryptography.ProtectedData).
    '  The plaintext password is never written to disk, never
    '  logged, and only transmitted to a node transiently inside
    '  an install job payload over the secured REST channel.
    '  The node never persists it - it is used once for SteamCMD
    '  stdin and discarded.
    '
    '  One credential = one Steam account. A single account can
    '  be referenced by any number of install steps on any node.
    '  Accounts that require game ownership are configured here
    '  alongside anonymous/free-server accounts.
    '
    '  Credential resolution at install time (manager side):
    '    1. Decrypt password using DPAPI
    '    2. Include plaintext in the install job payload
    '       (transmitted over TLS-secured REST to the node)
    '    3. Node passes credentials to SteamCMD via stdin
    '    4. Plaintext discarded after use - never stored on node
    ' ------------------------------------------------------------

    Public Class SteamCredential
        Public Property CredentialId As String          ' GUID · stable
        Public Property DisplayName As String           ' e.g. "Dedicated Server Account"
        Public Property Username As String              ' Stored plaintext - not sensitive
        Public Property EncryptedPassword As Byte()    ' DPAPI-encrypted · never plaintext
        Public Property GameId As String               ' Optional - which game this is for
        ' Empty = usable for any game
        Public Property Notes As String                ' e.g. "Owns LO licence, use for LO only"
        Public Property CreatedAt As DateTime
        Public Property LastUsedAt As DateTime?

        ' Anonymous = True means SteamCMD will be invoked with
        ' "+login anonymous" - no password needed or stored.
        ' Many dedicated server packages are available anonymously.
        Public Property IsAnonymous As Boolean = False

        ' Populated by manager on load - not stored in DB.
        ' How many install steps currently reference this credential.
        Public Property ReferenceCount As Integer
    End Class


    ' ------------------------------------------------------------
    '  Install
    ' ------------------------------------------------------------

    Public Enum InstallMethod
        SteamCMD
        DirectDownload
        Manual          ' User places files themselves; we just validate
    End Enum

    ' Represents one step the node executes during install/update.
    ' The node executes these in order; any step failure aborts.
    Public MustInherit Class InstallStep
        Public Property Description As String   ' Shown in UI progress
    End Class

    Public Class SteamCmdInstallStep
        Inherits InstallStep
        Public Property AppId As String
        Public Property InstallDir As String
        Public Property Branch As String        ' "" = default
        Public Property BranchPassword As String
        Public Property ValidateFiles As Boolean = True

        ' Which Steam credential to use for this install step.
        ' The manager resolves this to a SteamCredential record,
        ' decrypts the password via DPAPI, and includes the
        ' plaintext credentials in the install job payload sent
        ' to the node. The node feeds them to SteamCMD stdin
        ' and never writes them to disk.
        '
        ' Empty = attempt anonymous login (+login anonymous).
        ' Works for dedicated server apps that don't require
        ' game ownership (e.g. many Source engine servers).
        ' Check Steam's app depot config to confirm whether a
        ' given AppID supports anonymous download.
        Public Property SteamCredentialId As String = ""
    End Class

    Public Class DownloadInstallStep
        Inherits InstallStep
        Public Property Url As String
        Public Property Sha256 As String        ' Optional checksum
        Public Property ExtractToPath As String
    End Class

    Public Class RunCommandStep
        Inherits InstallStep
        Public Property Executable As String
        Public Property Arguments As String
        Public Property WorkingDirectory As String
        Public Property ExpectExitCode As Integer = 0
    End Class

    Public Class ValidationResult
        Public Property IsValid As Boolean
        Public Property Reason As String

        Public Shared Function Ok() As ValidationResult
            Return New ValidationResult With {.IsValid = True}
        End Function

        Public Shared Function Fail(reason As String) As ValidationResult
            Return New ValidationResult With {.IsValid = False, .Reason = reason}
        End Function
    End Class


    ' ------------------------------------------------------------
    '  Config blobs
    '  These are thin wrappers the plugin deserializes from the
    '  JSON stored in the DB. Core never reads inside them.
    ' ------------------------------------------------------------

    ' Carries the JSON blob stored on an Installation row.
    ' Plugin deserializes into its own typed class internally.
    Public Class InstallationConfig
        Public Property GameId As String
        Public Property RawJson As String           ' Full blob from DB
        Public Property SteamBranch As String       ' Promoted for convenience
        Public Property SteamBranchPassword As String
    End Class

    ' Carries the JSON blob stored on an Instance row.
    Public Class InstanceConfig
        Public Property GameId As String
        Public Property InstanceId As String
        Public Property DisplayName As String
        Public Property ExeOverride As String       ' Blank = let plugin resolve
        Public Property RawJson As String           ' Full blob from DB
    End Class


    ' ------------------------------------------------------------
    '  Log sources
    ' ------------------------------------------------------------

    Public Interface ILogSource
        ' Unique within an instance - used as a label in the buffer
        ReadOnly Property SourceId As String
    End Interface

    ' Capture the game process's stdout and stderr
    Public Class StdoutLogSource
        Implements ILogSource
        Public ReadOnly Property SourceId As String = "stdout" _
            Implements ILogSource.SourceId
        Public Property CaptureStderr As Boolean = True
    End Class

    ' Watch a file or glob pattern for new lines
    Public Class FileLogSource
        Implements ILogSource

        Public ReadOnly Property SourceId As String _
            Implements ILogSource.SourceId

        ' Path relative to working directory, or absolute.
        ' Glob ok: "logs\*.log"
        Public Property PathPattern As String

        ' How to handle log rotation (new file matching the glob)
        Public Property FollowRotation As Boolean = True

        Public Sub New(sourceId As String, pathPattern As String)
            Me.SourceId = sourceId
            Me.PathPattern = pathPattern
        End Sub
    End Class


    ' ------------------------------------------------------------
    '  Log parser
    '  Receives every line from every log source for the instance.
    '  Maintains derived state the manager can query.
    ' ------------------------------------------------------------

    Public Interface ILogParser

        ' Called on every incoming line (thread-safe required).
        ' sourceid = which ILogSource the line came from.
        Sub ProcessLine(sourceId As String, timestamp As DateTime, line As String)

        ' Current connected player list. Empty list if none/unknown.
        ReadOnly Property ActivePlayers As IReadOnlyList(Of PlayerInfo)

        ' Arbitrary key/value metrics this plugin wants to surface
        ' e.g. "TileCount" -> "8192", "WorldSeed" -> "abc123"
        ReadOnly Property CustomMetrics As IReadOnlyDictionary(Of String, String)

        ' Reset state (called when instance is restarted)
        Sub Reset()

    End Interface

    Public Class PlayerInfo
        Public Property Name As String
        Public Property JoinedAt As DateTime
        Public Property Platform As String      ' "Steam", "EOS" etc - optional
    End Class


    ' ------------------------------------------------------------
    '  Install monitor
    '  Watches stdout of install/update processes for prompts.
    ' ------------------------------------------------------------

    Public Interface IInstallMonitor

        ' Called for every stdout line during install/update.
        ' Returns Nothing if the line isn't a recognized prompt.
        ' Returns a PromptInfo if user input is needed.
        Function DetectPrompt(line As String) As PromptInfo

        ' Called when a subsequent line confirms the prompt was
        ' answered correctly (e.g. SteamCMD proceeds past auth).
        ' Use to clear waiting state if needed.
        Sub NotifyPromptResolved(promptType As PromptType)

    End Interface

    Public Class PromptInfo
        Public Property PromptType As PromptType

        ' Shown to user in the manager UI
        Public Property DisplayMessage As String

        ' Hint for the input control
        Public Property InputPlaceholder As String

        ' True = render as password field (mask input)
        Public Property IsSensitive As Boolean

        ' For Yes/No prompts - the exact strings to send
        Public Property YesValue As String = "y"
        Public Property NoValue As String = "n"
    End Class

    Public Enum PromptType
        SteamGuardEmail
        SteamGuardMobile
        SteamGuardTwoFactor
        YesNoConfirmation
        FreeText
    End Enum


    ' ------------------------------------------------------------
    '  RCON
    ' ------------------------------------------------------------

    Public Class RconInfo
        Public Property Protocol As RconProtocol

        ' Port and password come from the instance config.
        ' The node connects on localhost - RCON port never needs
        ' to be reachable externally. Manager sends commands via
        ' the node's REST API (/instance/{id}/rcon/send).
        Public Property Port As Integer
        Public Property Password As String

        ' Connection tuning
        Public Property ConnectTimeoutMs As Integer = 5000
        Public Property MaxPacketSize As Integer = 4096

        ' If True, the node attempts RCON connection automatically
        ' once the instance reaches Running state, with retries.
        ' If False, connection is only opened on an explicit
        ' POST /instance/{id}/rcon/connect from the manager.
        Public Property AutoConnect As Boolean = True

        ' How long to wait after process start before the first
        ' connection attempt. Some games need time to initialise
        ' their RCON listener before it will accept connections.
        ' Factorio: ~0ms. Others may need 10000ms or more.
        Public Property StartupDelayMs As Integer = 3000

        ' Retry behaviour when the initial connection fails.
        ' The node will retry up to MaxConnectRetries times at
        ' RetryIntervalMs apart before marking RCON as Unavailable.
        ' The instance remains in Running state regardless - RCON
        ' availability is tracked independently of process state.
        Public Property MaxConnectRetries As Integer = 5
        Public Property RetryIntervalMs As Integer = 2000
    End Class

    ' Node-side RCON state machine per instance.
    ' Transitions:  NotAvailable
    '                   ↓  (AutoConnect=True, instance starts)
    '               Connecting
    '                   ↓  (TCP open)
    '               Authenticating
    '                   ↓  (auth accepted)
    '               Connected
    '                   ↓  (timeout / server-side close)
    '               Disconnected
    '                   ↓  (retry loop)
    '               Connecting  ...
    '
    ' MaxConnectRetries exhausted → Unavailable (no more retries)
    ' Manual POST /rcon/connect resets retries and re-enters Connecting.
    Public Enum RconState
        NotAvailable    ' Plugin returned Nothing from GetRconInfo
        Connecting      ' TCP connection attempt in progress
        Authenticating  ' TCP open, auth handshake in progress
        Connected       ' Session live, ready to send commands
        Disconnected    ' Was connected, lost connection - retrying
        Unavailable     ' Retry limit hit - manual reconnect required
    End Enum

    Public Enum RconProtocol
        SourceRcon     ' Valve Source Engine protocol - also used by Factorio
        RawTcp         ' Line-delimited raw TCP - fallback for custom games
    End Enum


    ' ------------------------------------------------------------
    '  Mod manager
    ' ------------------------------------------------------------

    Public Interface IModManager

        ReadOnly Property ModSource As ModSource

        ' List mods currently installed for this instance
        Function ListInstalledMods(instanceConfig As InstanceConfig) As Task(Of IReadOnlyList(Of ModInfo))

        ' Install a mod by its source ID (Workshop ID, portal slug etc)
        Function InstallMod(instanceConfig As InstanceConfig,
                            modId As String,
                            version As String,
                            cancellation As CancellationToken) As Task(Of ModInstallResult)

        Function RemoveMod(instanceConfig As InstanceConfig,
                           modId As String) As Task(Of Boolean)

        ' Check for and return available updates (don't apply yet)
        Function CheckForUpdates(instanceConfig As InstanceConfig) As Task(Of IReadOnlyList(Of ModUpdateInfo))

    End Interface

    Public Enum ModSource
        SteamWorkshop
        FactorioModPortal
        LocalDirectory
        ZipFile
    End Enum

    Public Class ModInfo
        Public Property ModId As String
        Public Property DisplayName As String
        Public Property Version As String
        Public Property InstalledAt As DateTime
        Public Property Source As ModSource
    End Class

    Public Class ModInstallResult
        Public Property Success As Boolean
        Public Property InstalledVersion As String
        Public Property ErrorMessage As String
    End Class

    Public Class ModUpdateInfo
        Public Property ModId As String
        Public Property CurrentVersion As String
        Public Property AvailableVersion As String
    End Class


    ' ------------------------------------------------------------
    '  Realm credentials
    '  Stored as named records in the manager's SQLite.
    '  Referenced by Installation and Instance configs via
    '  CredentialPicker fields. Resolved at launch time by the
    '  manager before passing config to the plugin.
    '
    '  One credential = one realm. Multiple installations and
    '  instances on any node can share the same credential record.
    '  To host instances from different realms on one node, create
    '  a separate RealmCredential per realm and assign accordingly.
    ' ------------------------------------------------------------

    Public Class RealmCredential
        Public Property CredentialId As String      ' GUID · stable
        Public Property DisplayName As String       ' e.g. "My Oasis Realm - Main Key"
        Public Property CustomerKey As String       ' Sensitive · encrypted at rest
        Public Property ProviderKey As String       ' Sensitive · encrypted at rest
        Public Property GameId As String            ' Which plugin this belongs to
        Public Property CreatedAt As DateTime
        Public Property LastUsedAt As DateTime?
        ' How many installations/instances currently reference this credential.
        ' Populated by the manager on load - not stored in DB.
        Public Property ReferenceCount As Integer
    End Class


    ' ------------------------------------------------------------
    '  Config schema
    '  Lets the UI render a form for any game without knowing
    '  anything about it. Plugin owns the field definitions.
    ' ------------------------------------------------------------

    Public Class ConfigFieldDescriptor
        ' Key used in the JSON blob
        Public Property Key As String

        ' Label shown in UI
        Public Property Label As String

        ' Tooltip / help text
        Public Property Description As String

        Public Property FieldType As ConfigFieldType
        Public Property DefaultValue As String
        Public Property IsRequired As Boolean = False
        Public Property IsSensitive As Boolean = False     ' Render as password

        ' For FieldType.Choice
        Public Property Choices As List(Of String)

        ' For FieldType.Integer
        Public Property MinValue As Integer?
        Public Property MaxValue As Integer?
    End Class

    Public Enum ConfigFieldType
        Text
        IntegerField
        BooleanField
        Choice              ' Dropdown from Choices list
        FilePath
        DirectoryPath
        Password
        CredentialPicker    ' Dropdown of RealmCredential records stored in manager DB.
        ' The UI resolves the selected credential and promotes
        ' CustomerKey and ProviderKey into the config at launch time.
        ' The stored value is the RealmCredentialId (GUID).
        SteamCredentialPicker ' Dropdown of SteamCredential records stored in manager DB.
        ' The stored value is the SteamCredentialId (GUID).
        ' Includes an "Anonymous" option for free server apps.
    End Enum


    ' ------------------------------------------------------------
    '  Instance state
    '  Shared vocabulary used by both node and manager.
    '  The node reports this on every /status response.
    '  The manager persists it and fires automation triggers on
    '  any transition.
    ' ------------------------------------------------------------

    Public Enum InstanceState
        ' Normal lifecycle
        Stopped             ' Intentionally stopped · clean
        Starting            ' Process launching · not yet confirmed alive
        Running             ' Process alive and healthy
        Stopping            ' Stop intent set · waiting for process to exit

        ' Crash paths
        Crashed             ' Unintended exit · no stop intent was set
        StartFailed         ' Process never launched successfully
        Restarting          ' In backoff delay before next start attempt

        ' Terminal crash state - explicit, never silent
        ' Carries full context: crash count, window, policy, last exit code
        CrashLoopHalted

        ' Installation coordination
        InstallationLocked  ' Write lock held by an update · cannot start
    End Enum

    ' Sub-state set by the log parser when a crash signal pattern
    ' is matched BEFORE the process has actually exited.
    ' Allows early notification with log context attached.
    Public Enum CrashDetectionState
        None
        CrashSignalDetected ' Pattern matched · process still running
    End Enum


    ' ------------------------------------------------------------
    '  Crash restart policy
    '  Defined per instance in the manager · pushed to and persisted
    '  on the node so it survives manager restarts and operates
    '  autonomously during network gaps.
    ' ------------------------------------------------------------

    Public Class CrashRestartPolicy

        ' Master switch. When False the node never auto-restarts,
        ' regardless of all other settings.
        Public Property AutoRestart As Boolean = True

        ' Crash loop detection window. If MaxRestartsInWindow crashes
        ' occur within WindowMinutes, enter CrashLoopHalted.
        ' The window slides - crashes older than WindowMinutes are
        ' not counted even if they happened in this session.
        Public Property MaxRestartsInWindow As Integer = 5
        Public Property WindowMinutes As Integer = 10

        ' Backoff delay in seconds before each restart attempt.
        ' Index 0 = first restart, 1 = second, etc.
        ' The last value repeats for any attempt beyond the array length.
        ' {0, 10, 30, 60, 300} = immediate, 10s, 30s, 60s, 5min, 5min...
        Public Property BackoffScheduleSeconds As Integer() = {0, 10, 30, 60, 300}

        ' What happens when CrashLoopHalted is entered.
        ' NotifyOnly    = fire notifications, stay halted until manual resume
        ' NotifyAndWait = as above, but a ManualTrigger can resume retries
        Public Property OnCrashLoopHalted As CrashLoopBehaviour = CrashLoopBehaviour.NotifyOnly

        ' How long to wait for the process to respond to a graceful
        ' stop signal before force-killing it. Prevents a hung process
        ' from blocking restart or update operations indefinitely.
        Public Property StopIntentTimeoutMs As Integer = 30000

    End Class

    Public Enum CrashLoopBehaviour
        NotifyOnly      ' Halt and alert · human must manually resume
        NotifyAndWait   ' Halt and alert · automation rule can resume
    End Enum


    ' ------------------------------------------------------------
    '  Crash event record
    '  Persisted to node-local SQLite per crash/restart event.
    '  Survives node restarts · used for sliding window calculation
    '  and for surfacing history in the manager UI.
    ' ------------------------------------------------------------

    Public Class CrashEvent
        Public Property CrashEventId As String      ' GUID
        Public Property InstanceId As String
        Public Property OccurredAt As DateTime
        Public Property ExitCode As Integer
        Public Property StopIntentWasSet As Boolean ' False = genuine crash
        Public Property CrashState As InstanceState ' Crashed or StartFailed
        Public Property RestartDecision As RestartDecision
        Public Property RestartDecisionReason As String  ' Human-readable · never empty
        Public Property AttemptNumber As Integer    ' Which retry this was
        Public Property BackoffAppliedSeconds As Integer
        Public Property LogContextLines As List(Of String) ' Lines before crash signal
    End Class

    Public Enum RestartDecision
        WillRestart         ' Backoff applied · restart scheduled
        HaltedCrashLoop     ' Window limit hit · entering CrashLoopHalted
        HaltedCleanExit     ' Exit code in GetCleanExitCodes · not restarting
        HaltedAutoRestartOff ' AutoRestart = False
        HaltedInstallLocked ' Write lock held · cannot restart right now
    End Enum

End Namespace