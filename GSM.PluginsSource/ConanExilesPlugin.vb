' <plugin id="conanexiles" name="Conan Exiles Dedicated Server" version="1.0.0" author="siteml" requiresContracts="2">
' <RequiresContracts: 2>
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text.RegularExpressions
Imports GSM.Plugin

' ============================================================
'  Conan Exiles Dedicated Server Plugin
'
'  AppID: 443030 (free dedicated server, anonymous SteamCMD)
'  Engine: Unreal Engine 4 (Legacy build) / Unreal Engine 5
'          (Enhanced build, default since May 5 2026)
'  Install: SteamCMD only
'  RCON: Source RCON protocol (native, configurable via cmd line)
'  Platform: Windows-only — Conan ships no native Linux binary.
'            Linux setups in the wild run via Wine+xvfb, which is
'            outside this plugin's scope.
'
'  Branches:
'    Enhanced (UE5) = default branch (BetaBranch left empty)
'    Legacy   (UE4) = "conan-exiles-legacy" beta branch
'  Both share the same AppID, install layout, binary name,
'  config files, log format, and RCON surface; the only
'  difference is the depot pulled by SteamCMD. So this is one
'  plugin with a Build dropdown rather than two plugins.
'
'  Per-installation Build means you can run a Legacy server and
'  an Enhanced server on the same node by creating two separate
'  Installations and picking different Builds. Switching an
'  existing install between branches is a Steam update — change
'  the Build field, click Update, SteamCMD repoints the depot.
'
'  Install layout (relative to install root):
'    ConanSandbox/Binaries/Win64/ConanSandboxServer-Win64-Shipping.exe
'    ConanSandbox/Saved/Config/WindowsServer/Engine.ini
'    ConanSandbox/Saved/Config/WindowsServer/Game.ini
'    ConanSandbox/Saved/Config/WindowsServer/ServerSettings.ini
'    ConanSandbox/Saved/Logs/ConanSandbox.log
'    ConanSandbox/Saved/game.db                  ' world state
'    ConanSandbox/Saved/game_backup_##.db        ' autosaves
'
'  Why MaxInstancesPerInstallation = 1:
'    1. game.db is SQLite, locked exclusively when the server runs.
'    2. Per Funcom docs, Engine.ini's ServerPort/Port/QueryPort
'       don't actually re-bind ports — those MUST be on the
'       command line, and secondary instances "won't show on the
'       official server list" even when bound to distinct ports.
'    3. Logs and config files all live under
'       ConanSandbox/Saved/* with no per-instance subdivision the
'       game itself recognises.
'    A user wanting multiple Conan servers on one node creates
'    multiple Installations, one per install path.
'
'  Key config fields (installation-level):
'    Build               — Enhanced (UE5) / Legacy (UE4)
'    SteamBranchPassword — only used for the rare cases Funcom
'                           gates an internal beta with a password
'
'  Key config fields (instance-level):
'    ServerName          — in-game server browser. Rendered into
'                           Engine.ini [OnlineSubsystem] at launch
'                           (off the URL, which mangled names with
'                           spaces/unicode).
'    ServerPassword      — connect password. Rendered into Engine.ini
'                           [OnlineSubsystem] at launch; blank keeps
'                           the file value, Clear checkbox empties it.
'    Port                — game UDP, default 7777
'    QueryPort           — Steam query UDP, default 27015
'    RconPort            — RCON TCP, default 25575 (via InstanceConfig)
'    RconPassword        — RCON password (via InstanceConfig)
'    RconMaxKarma        — RCON anti-DDoS karma limit, default 60
'    MaxPlayers          — server slot count, default 40
'    Multihome           — optional IP bind override
'
'  Where ServerName + ServerPassword go (rendered, not on args):
'    Conan reads both from Engine.ini's [OnlineSubsystem] section.
'    A launch-URL ?ServerPassword= silently fails — the value never
'    reaches the OnlineSubsystem code path, so the AES key drifts
'    and connects die with PacketHandlerLog AESDecryptionFailed at
'    the network layer instead of a clean "wrong password". And a
'    ?ServerName= mangles names with spaces/unicode through UE's
'    URL parser. So both are Configuration-tab fields that
'    IStartupFileProvider.RenderStartupFile writes into Engine.ini
'    [OnlineSubsystem] just before launch; neither is on the
'    command line. ServerName always writes (blank → default name);
'    ServerPassword is preserve-if-blank with an explicit Clear
'    checkbox, so upgrading from the old Engine.ini-editor version
'    doesn't wipe a set password (re-enter it on the Configuration
'    tab, or tick Clear to open the server). Single-ownership: the
'    old structured "Network (Engine.ini)" editor tab is removed;
'    raw Engine.ini stays editable via the .ini file browser.
'
'  Where AdminPassword goes (NOT a Configuration-tab field):
'    Conan reads AdminPassword from ServerSettings.ini's
'    [ServerSettings] AdminPassword. Older versions of this
'    plugin exposed it as an instance-level Configuration-tab
'    field that got appended to the launch URL as
'    ?AdminPassword=X; that worked at spawn time but split the
'    canonical INI value from PowerGSM's stored value, creating
'    two places to keep in sync (and a footgun if an operator
'    edited the INI directly and then re-saved the instance
'    config). Surfaced now via the Server Settings tab
'    (ServerSettings.ini structured editor) where Conan
'    natively stores it. Operators upgrading from a plugin
'    version that had AdminPassword on the Configuration tab
'    need to re-enter the password in the Server Settings tab;
'    the legacy value in ConfigJson is ignored on launch.
' ============================================================

Public Class ConanExilesPlugin
    Implements IGamePlugin
    Implements IInstallationNoticeProvider
    Implements IPrerequisiteProvider
    Implements IManagedDirectoriesProvider
    Implements IInstanceFileEditorProvider
    Implements ILaunchOptionsProvider
    Implements IStartupFileProvider

    Public ReadOnly Property GameId As String = "conanexiles" Implements IGamePlugin.GameId
    Public ReadOnly Property DisplayName As String = "Conan Exiles" Implements IGamePlugin.DisplayName

    ' One instance per install — game.db file-locked + per-Funcom
    ' docs secondary servers off the same depot don't surface on
    ' the official list anyway. See the file-header comment for
    ' the full rationale; users who want multiple Conan servers
    ' on one node make multiple Installations.
    Public ReadOnly Property MaxInstancesPerInstallation As Integer? Implements IGamePlugin.MaxInstancesPerInstallation
        Get
            Return 1
        End Get
    End Property

    ' ============================================================
    '  Build → Steam branch mapping
    '
    '  Centralised so GetInstallSteps / GetUpdateSteps / the UI
    '  notice text all agree on what the values mean. The
    '  DisplayName values are what the user sees in the
    '  GetInstallConfigSchema Enum dropdown; mapping back to a
    '  branch happens here.
    '
    '  Default fallthrough is Enhanced — matches Steam's "no
    '  branch chosen" = public depot, which since May 5 2026 is
    '  the UE5 Enhanced build. Anyone with a missing/blank Build
    '  field gets Enhanced rather than a confusing failure.
    ' ============================================================

    Private Const BuildEnhanced As String = "Enhanced (UE5)"
    Private Const BuildLegacy As String = "Legacy (UE4)"

    Private Shared Function ResolveSteamBranch(buildValue As String) As String
        Select Case If(buildValue, "").Trim()
            Case BuildLegacy
                Return "conan-exiles-legacy"
            Case Else
                ' Enhanced — default branch, no override needed
                Return ""
        End Select
    End Function

    ' ============================================================
    '  Install
    ' ============================================================

    Public Function GetSupportedInstallMethods() As IReadOnlyList(Of InstallMethod) Implements IGamePlugin.GetSupportedInstallMethods
        Return New InstallMethod() {InstallMethod.SteamCmd}
    End Function

    Public Function GetInstallSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetInstallSteps
        Dim steps As New List(Of InstallStep)

        Dim steamStep As New SteamCmdStep()
        steamStep.StepName = "Download Conan Exiles Server"
        steamStep.Description = "Download/update via SteamCMD (AppID 443030)"
        steamStep.AppId = 443030
        steamStep.ValidateFiles = True
        ' Anonymous install — Conan Exiles Dedicated Server is a
        ' free Steam tool that doesn't require account login. The
        ' Manager's installation flow lets the user pick
        ' "(Anonymous — no login)" in the Steam credential
        ' dropdown; setting this to False is the plugin saying
        ' "you don't need to enter creds" — informational, the
        ' actual login decision lives in the install request.
        steamStep.RequiresLogin = False

        ' Branch selection. Enhanced (UE5) = no branch (default
        ' depot); Legacy (UE4) = "conan-exiles-legacy". Branch
        ' password is rarely needed but supported in case Funcom
        ' ever gates an internal preview behind one.
        If config IsNot Nothing AndAlso config.CustomFields IsNot Nothing Then
            Dim branch = ResolveSteamBranch(GetField(config.CustomFields, "Build"))
            If Not String.IsNullOrEmpty(branch) Then
                steamStep.BetaBranch = branch
                steamStep.BetaPassword = GetField(config.CustomFields, "SteamBranchPassword")
            End If
        End If

        steps.Add(steamStep)

        Return steps
    End Function

    Public Function GetUpdateSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetUpdateSteps
        ' Update is the same as install for SteamCMD games —
        ' SteamCMD's app_update reconciles to the chosen branch
        ' regardless of what was previously installed, so flipping
        ' Build and clicking Update is the supported path to move
        ' an existing installation between Enhanced and Legacy.
        Return GetInstallSteps(config)
    End Function

    ' ============================================================
    '  Instance
    ' ============================================================

    Public Function GetExecutablePath(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.GetExecutablePath
        ' Windows-only. Conan ships no native Linux server binary
        ' — community Linux setups run the Windows binary under
        ' Wine+xvfb, which is outside this plugin's scope.
        '
        ' Forward slashes throughout: Windows file APIs accept
        ' them, and the string survives the Manager → Node
        ' marshalling boundary unchanged regardless of which OS
        ' the node lives on.
        '
        ' Two Conan executables exist in a typical install and
        ' it matters which one we launch:
        '
        '   <install>/ConanSandboxServer.exe
        '       Top-level launcher wrapper. Spawns the real
        '       shipping binary as a child process, then exits.
        '       Launching this means PowerGSM ends up tracking
        '       the wrapper PID, which dies immediately — the
        '       Node would mark the instance as crashed while the
        '       real server happily runs as a detached orphan,
        '       breaking stop/restart and crash detection.
        '
        '   <install>/ConanSandbox/Binaries/Win64/ConanSandboxServer-Win64-Shipping.exe
        '       The actual UE Shipping-config server binary.
        '       Same exe across Legacy (UE4) and Enhanced (UE5)
        '       builds; both Steam depots ship it under this
        '       path with this name. Launch THIS one directly so
        '       our process handle owns the real server and -log
        '       graceful shutdown routes to the right place.
        '
        ' If we ever see a Conan node on Linux (i.e. someone
        ' wiring Wine in below PowerGSM's level), the install
        ' will fail at the "binary not found" check and surface a
        ' clear message — better than silently launching nothing.
        ' The platform-specific Select keeps that diagnostic clean
        ' rather than emitting Linux candidates that don't exist.
        Select Case If(config IsNot Nothing, config.Platform, NodePlatform.Unknown)
            Case NodePlatform.Linux
                ' Empty list → InstanceManager surfaces "no
                ' executable candidates" as the failure reason.
                Return New String() {}
            Case Else
                Return New String() {
                    "ConanSandbox/Binaries/Win64/ConanSandboxServer-Win64-Shipping.exe"
                }
        End Select
    End Function

    Public Function BuildLaunchArguments(config As InstanceConfig) As String Implements IGamePlugin.BuildLaunchArguments
        ' Conan Exiles uses UE4's URL-style positional arg for
        ' the project + per-instance settings, followed by
        ' standard dash-flags for RCON and the UE engine.
        '
        ' Funcom's docs explicitly warn that Engine.ini's Port /
        ' QueryPort / ServerPort do NOT take effect — they MUST
        ' be on the command line. That's why we always emit them
        ' here even when the user hasn't changed defaults.
        '
        ' Why we don't bother with a "ConanSandbox" positional
        ' on Linux the way Last Oasis does: Conan has no Linux
        ' binary, so the Linux path never runs.

        Dim args As New List(Of String)

        ' --- UE4 URL ---
        ' Project + ?-separated key=value pairs, all in a single
        ' token. ConanSandbox is the project name embedded in the
        ' binary; the ? params override defaults the engine would
        ' otherwise pull from the (non-functional for ports)
        ' Engine.ini.

        Dim url As New System.Text.StringBuilder()
        url.Append("ConanSandbox")

        Dim port = GetFieldInt(config.CustomFields, "Port", 7777)
        Dim queryPort = GetFieldInt(config.CustomFields, "QueryPort", 27015)
        Dim maxPlayers = GetFieldInt(config.CustomFields, "MaxPlayers", 40)
        url.Append("?Port=")
        url.Append(port)
        url.Append("?QueryPort=")
        url.Append(queryPort)
        url.Append("?MaxPlayers=")
        url.Append(maxPlayers)

        ' ServerName and ServerPassword are intentionally NOT emitted
        ' on the launch URL. Conan reads both from Engine.ini's
        ' [OnlineSubsystem] section; IStartupFileProvider.RenderStartupFile
        ' writes them there just before launch. A URL ?ServerName=
        ' mangles names with spaces/unicode through UE's URL parser,
        ' and a URL ?ServerPassword= silently fails to reach the
        ' OnlineSubsystem code path (AESDecryptionFailed at connect).
        ' See the file-header comment for the full rationale.

        ' AdminPassword used to be appended here as ?AdminPassword=X
        ' on the launch URL. It now lives in ServerSettings.ini's
        ' [ServerSettings] AdminPassword and is set via the Server
        ' Settings file editor tab. Conan reads it from the INI at
        ' startup; no launch-URL plumbing needed. See the file-
        ' header "Where AdminPassword goes" comment for the
        ' rationale.

        Dim multihome = GetField(config.CustomFields, "Multihome")
        If Not String.IsNullOrEmpty(multihome) Then
            url.Append("?Multihome=")
            url.Append(multihome)
        End If

        ' ?listen tells the engine this is a listen-capable
        ' (multiplayer) server. Some setup guides skip it because
        ' ConanSandboxServer.exe implicitly listens, but Funcom's
        ' own example startup line includes it and it costs us
        ' nothing to be explicit.
        url.Append("?listen")

        args.Add(url.ToString())

        ' --- Dash flags ---

        ' RCON. Conan accepts RCON config from either Game.ini /
        ' ServerSettings.ini (under [RconPlugin] / [ServerSettings])
        ' or directly from the command line. Command-line wins
        ' the race against config files written by an earlier run
        ' and avoids touching INIs the user may have hand-edited,
        ' so we always pass via flags when the user has provided
        ' a password.
        If Not String.IsNullOrEmpty(config.RconPassword) Then
            Dim rconPort = If(config.RconPort, 25575)
            args.Add("-RconEnabled=1")
            args.Add($"-RconPassword={config.RconPassword}")
            args.Add($"-RconPort={rconPort}")
            Dim maxKarma = GetFieldInt(config.CustomFields, "RconMaxKarma", 60)
            args.Add($"-RconMaxKarma={maxKarma}")
        End If

        ' UE4/UE5 -log: required for graceful shutdown via
        ' AttachConsole + CTRL_C_EVENT, same rationale as the
        ' Last Oasis plugin's matching comment block. Without
        ' -log, UE doesn't install SetConsoleCtrlHandler against
        ' the inherited console; CTRL_C_EVENT then routes to the
        ' OS default handler instead of UE's RequestEngineExit,
        ' and our graceful-stop path falls through to force kill.
        ' PowerGSM's spawn path applies STARTF_USESHOWWINDOW +
        ' SW_HIDE so the AllocConsole window UE creates during
        ' -log init stays hidden — handler armed, no visible
        ' console.
        args.Add("-log")

        Return String.Join(" ", args)
    End Function

    Public Function ValidateConfig(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.ValidateConfig
        ' The one instance-level validation Conan needs is the
        ' "pinger" port reservation. Funcom hard-codes the pinger
        ' port — the UDP port the in-game server browser pings to
        ' decide whether a server is alive — to GAME PORT + 1. There
        ' is no command-line flag or INI entry to move it. If any
        ' other UDP port the operator configures lands on game port
        ' + 1, the bind collides with the pinger and the server
        ' silently never appears in the browser: no error, no log
        ' line, just an invisible-but-running server. So we treat
        ' game port + 1 as reserved and reject a Query Port that
        ' lands on it (or on the game port itself).
        '
        ' NOTE (cross-instance gap): this catches THIS instance's
        ' Query Port against its OWN pinger. The global PortAllocator
        ' still doesn't know game+1 is occupied, so it can't stop a
        ' DIFFERENT instance's game/query port from landing on this
        ' one's pinger. Closing that needs an allocator/contract
        ' change (a derived-/reserved-port declaration) rather than
        ' a plugin-only edit — low frequency given Conan is one
        ' instance per installation, parked as a follow-up.
        '
        ' AdminPassword validation that lived here through Phase
        ' 5g-2 moved with the field to ServerSettings.ini — the
        ' file-editor schema's IsRequired flag covers that now.
        Dim issues As New List(Of String)
        If config Is Nothing Then Return issues

        Dim port = GetFieldInt(config.CustomFields, "Port", 7777)
        Dim queryPort = GetFieldInt(config.CustomFields, "QueryPort", 27015)
        Dim pingerPort = port + 1

        If queryPort = pingerPort Then
            issues.Add($"Query Port ({queryPort}) lands on the hard-coded pinger port (game port + 1 = {pingerPort}). The pinger port is fixed by the engine and used by the server browser to detect the server — a Query Port that collides with it leaves the server invisible in the list even though it's running. Move the Query Port off {pingerPort} (the 27015 default, or game port + 2, are both safe).")
        ElseIf queryPort = port Then
            issues.Add($"Query Port and Game Port are both {port}. They must be different UDP ports — and note the engine also reserves game port + 1 ({pingerPort}) for the pinger, so keep the Query Port clear of that too.")
        End If

        Return issues
    End Function

    ' ============================================================
    '  Config schema
    ' ============================================================

    Public Function GetInstallConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstallConfigSchema
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "Build",
                .Label = "Build",
                .Description = "Which Conan Exiles build to install. Enhanced is the default UE5 build (May 2026 onward); Legacy is the older UE4 build available via Steam's 'conan-exiles-legacy' beta branch. Switching after install requires an Update.",
                .FieldType = ConfigFieldType.[Enum],
                .DefaultValue = BuildEnhanced,
                .EnumValues = New List(Of String) From {
                    BuildEnhanced,
                    BuildLegacy
                }
            },
            New ConfigFieldDescriptor With {
                .Key = "SteamBranchPassword",
                .Label = "Branch password",
                .Description = "Only needed if Funcom temporarily gates an internal preview behind a password. Leave blank for both Enhanced and Legacy public branches.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True
            }
        }
    End Function

    Public Function GetInstanceConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstanceConfigSchema
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "ServerName",
                .Label = "Server Name",
                .Description = "Appears in the in-game server browser. PowerGSM writes this into Engine.ini [OnlineSubsystem] at launch, so spaces and special characters are fine (it's no longer parsed off the launch URL).",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "PowerGSM Conan Server"
            },
            New ConfigFieldDescriptor With {
                .Key = "ServerPassword",
                .Label = "Server Password (connect)",
                .Description = "Password players must enter to join. Leave blank for an open server. PowerGSM writes this into Engine.ini [OnlineSubsystem] at launch (the file Conan actually reads — a launch-URL value causes AESDecryptionFailed). Leaving it blank KEEPS whatever password is already in Engine.ini; to remove a password, blank this and tick ""Clear server password"" below.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True
            },
            New ConfigFieldDescriptor With {
                .Key = "ClearServerPassword",
                .Label = "Clear server password",
                .Description = "Only acts when the password box above is blank. Tick this to write an EMPTY password into Engine.ini at launch (making the server open). Leave unticked to keep the existing password — this is what stops an upgrade from wiping a previously-set password.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "MaxPlayers",
                .Label = "Max Players",
                .Description = "Server slot count. Conan's official cap is 40; higher values work but aren't supported.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "40",
                .MinValue = 1,
                .MaxValue = 200
            },
            New ConfigFieldDescriptor With {
                .Key = "Port",
                .Label = "Game Port (UDP)",
                .Description = "Game traffic port. Funcom warns: Engine.ini's Port setting does NOT work — this command-line value is what actually takes effect. (See the pinger note below about game port + 1.)",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "7777",
                .MinValue = 1024,
                .MaxValue = 65535,
                .IsPort = True,
                .ReservedPortOffsets = New List(Of Integer) From {1}
            },
            New ConfigFieldDescriptor With {
                .Key = "_pinger_notice",
                .Label = "Game port + 1 is reserved for the pinger",
                .Description = "The next UDP port after the game port (game port + 1 — e.g. 7778 when the game port is 7777) is hard-coded as the ""pinger"" and MUST be left open. The in-game server browser pings it to detect the server; there is no config or command-line way to move it. Don't assign it to the Query Port or to another instance, or the server won't appear in the list even though it's running.",
                .FieldType = ConfigFieldType.Notice
            },
            New ConfigFieldDescriptor With {
                .Key = "QueryPort",
                .Label = "Query Port (UDP)",
                .Description = "Steam query port. Used by the server browser to find the server. Keep it off game port + 1 (the pinger — see the note above). The 27015 default is safe.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "27015",
                .MinValue = 1024,
                .MaxValue = 65535,
                .IsPort = True
            },
            New ConfigFieldDescriptor With {
                .Key = "RconPort",
                .Label = "RCON Port (TCP)",
                .Description = "Source RCON port. Conan implements RCON natively — no external tool needed.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "25575",
                .MinValue = 1024,
                .MaxValue = 65535,
                .IsPort = True
            },
            New ConfigFieldDescriptor With {
                .Key = "RconPassword",
                .Label = "RCON Password",
                .Description = "Leave blank to disable RCON entirely. Set a strong value if exposed to the internet — RCON grants full server control.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True
            },
            New ConfigFieldDescriptor With {
                .Key = "RconMaxKarma",
                .Label = "RCON Max Karma",
                .Description = "Anti-DDoS karma cap for RCON. Default 60 is what Funcom recommends.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "60",
                .MinValue = 0,
                .MaxValue = 10000
            },
            New ConfigFieldDescriptor With {
                .Key = "Multihome",
                .Label = "Multihome (bind IP)",
                .Description = "Bind the server to a specific local IP. Useful on multi-NIC hosts. Blank = listen on all interfaces.",
                .FieldType = ConfigFieldType.Text
            }
        }
    End Function

    ' ============================================================
    '  Crash handling
    '
    '  Same policy delegation pattern as Last Oasis / Factorio.
    '  Conan's exit codes aren't well-documented but the basic
    '  "zero = clean, non-zero = crash" assumption holds for all
    '  three engines.
    ' ============================================================

    Public Function EvaluateCrash(exitCode As Integer,
                                   crashCount As Integer,
                                   policy As CrashRestartPolicy) As RestartDecision Implements IGamePlugin.EvaluateCrash
        If exitCode = 0 Then
            Return RestartDecision.Halt("Clean exit (code 0)")
        End If

        Select Case policy
            Case CrashRestartPolicy.NeverRestart
                Return RestartDecision.Halt($"NeverRestart policy (exit code {exitCode})")

            Case CrashRestartPolicy.AlwaysRestart
                Return RestartDecision.Restart(2000, $"AlwaysRestart (exit code {exitCode})")

            Case CrashRestartPolicy.RestartWithBackoff
                Dim delayMs = Math.Min(CInt(Math.Pow(2, crashCount)) * 1000, 300000)
                Return RestartDecision.Restart(delayMs,
                    $"Backoff restart (attempt {crashCount + 1}, delay {delayMs}ms)")

            Case CrashRestartPolicy.RestartLimited
                If crashCount < 5 Then
                    Return RestartDecision.Restart(5000,
                        $"Limited restart (attempt {crashCount + 1}/5)")
                End If
                Return RestartDecision.Halt($"Crash limit reached ({crashCount} crashes)")

            Case Else
                Return RestartDecision.Restart(5000, $"Default restart (exit code {exitCode})")
        End Select
    End Function

    ' ============================================================
    '  Log parsing
    ' ============================================================

    Public Function CreateLogParser() As ILogParser Implements IGamePlugin.CreateLogParser
        Return New ConanExilesLogParser()
    End Function

    Public Function GetLogSources(config As InstanceConfig) As IReadOnlyList(Of ILogSource) Implements IGamePlugin.GetLogSources
        ' Conan writes its primary log to a fixed path under the
        ' install root, with no per-instance variation hook the
        ' UE4 engine exposes. Because MaxInstancesPerInstallation
        ' is 1 anyway, the fixed location is all we need.
        '
        ' We don't pass -AbsLog the way Last Oasis does — LO's
        ' multi-instance-per-install design requires per-instance
        ' log files, but Conan's single-instance model means the
        ' canonical ConanSandbox.log is unambiguous.
        '
        ' Stdout is intentionally absent: with -log set, UE writes
        ' the same content to both stdout and the file, and the
        ' file is the authoritative source. Node still drains
        ' stdout pipes to prevent UE from blocking on writes to
        ' an unread pipe — see the LO plugin's matching comment
        ' for the full mechanism.
        Return New ILogSource() {
            New FileLogSource("conansandbox", "{InstallPath}/ConanSandbox/Saved/Logs/ConanSandbox.log")
        }
    End Function

    Public Function GetLogParseRules() As IReadOnlyList(Of LogParseRule) Implements IGamePlugin.GetLogParseRules
        ' Conan Exiles uses standard UE4 LogNet patterns. The
        ' rules below were refined against a real ConanSandbox.log
        ' captured from a PowerGSM-managed Enhanced (UE5) server
        ' during a test join/play/disconnect cycle — every rule
        ' has been confirmed against actual emitted lines from
        ' that capture.
        '
        ' The Conan connect/disconnect dance emits enough rich
        ' data across multiple lines to fully identify a player
        ' (Steam ID, FLS handle, IP:port) but no single line
        ' carries the in-game character name at join — that
        ' lands later via chat (or via the persistent players-
        ' table cache on the Node, for returning players whose
        ' DisplayName was bound in a prior session). Mirroring
        ' the Last Oasis approach, we fire multiple rules per
        ' logical event — EventStore correlates them on the
        ' connection's RemoteAddress, enriching the same player
        ' record as each line lands.
        '
        ' Connect sequence (in log order):
        '   LogNet: NotifyAcceptingConnection accepted from: <IP>:<port>
        '       (fires TWICE per join — pre-challenge then post —
        '       so we deliberately key on the AcceptedConnection
        '       line below instead, which fires once)
        '   LogNet: NotifyAcceptedConnection: ... RemoteAddr: <IP>:<port>, ...
        '       → PlayerJoin event, RemoteAddress only — EventStore
        '         buffers this as PendingRemoteAddress (10s TTL).
        '   LogNet: Login request: userId: STEAM:<id> platform: Fls
        '       → PlayerJoin event, Platform + PlatformUserId.
        '         Claims the buffered IP and creates the session.
        '   LogNet: Join succeeded: <PlatformPersona>
        '       → PlayerJoin event, PlatformPersona only. The
        '         post-colon token is the FLS handle (e.g.
        '         "losno420" or "losno420#72569"), NOT the
        '         in-game character name — character names
        '         arrive later via the chat rule. The handle is
        '         stable for the lifetime of the session, so we
        '         bind it to PlatformPersona (the platform-
        '         identity slot) and leave DisplayName free for
        '         the actual character name.
        '         Re-claims the still-live buffered IP (EventStore
        '         keeps PendingRemoteAddress alive within the TTL
        '         window for exactly this multi-event-per-connection
        '         pattern) and enriches the session by RemoteAddress
        '         match. Without the buffer-preservation behaviour
        '         this would create a second orphan session.
        '
        ' Disconnect sequence:
        '   LogNet: UNetConnection::Close: ... RemoteAddr: <IP>:<port>, ... UniqueId: STEAM:<id>, ...
        '       → enrichment only — has both IP and SteamID, kept
        '         so disconnects without the named line still
        '         resolve to a known player.
        '   LogNet: Player disconnected: <PlatformPersona>
        '       → PlayerLeave with PlatformPersona. Symmetric
        '         with the Join succeeded rule — the post-colon
        '         token is the same FLS handle that bound to
        '         PlatformPersona at join. Matching by
        '         PlatformPersona is stable across the chat-
        '         driven DisplayName updates that happen during
        '         the session; matching by DisplayName would
        '         miss after the first chat line lands and
        '         flipped DisplayName to the character name.
        '         Note: this line also fires with the literal
        '         string "Unknown" for failed handshakes /
        '         internal connection drops that never bound an
        '         FLS identity; Manager-side HandlePlayerLeave
        '         swallows those via the active-players dedup
        '         (no session named "Unknown" was ever joined),
        '         so no special filtering needed here.
        '
        ' Server lifecycle:
        '   LogGameMode: Display: Match State Changed from <from> to <to>
        '       → ServerStateChange, MatchState. Transition to
        '         InProgress = server is accepting players;
        '         transition away = shutting down.
        '   LogWorld: Bringing World <MapPath> up for play
        '       → TileLoaded, MapPath. Single load — Conan
        '         doesn't switch maps mid-session, but routing
        '         through the TileLoaded dispatch populates
        '         ServerState.CurrentMapPath (same as LO's
        '         identical rule does) and mirrors the value
        '         to instance_state for node-restart survival.
        '         TileId / TileName stay empty since Conan
        '         doesn't use LO's tile model. The prior
        '         classification as Custom was a no-op: the
        '         Custom kind has no Select Case branch and
        '         the capture group is named MapPath, not
        '         Custom_MapPath, so HarvestCustomFields
        '         ignored it too — the rule fired but did
        '         nothing.
        '
        ' Chat:
        '   ChatWindow: Character <DisplayName> (uid <CharacterId>, player <PlatformUserId>) said: <Message>
        '       → ChatMessage. Conan DOES log chat after all — the
        '         first sample I worked from happened to have zero
        '         chat traffic, which led me to mis-classify chat
        '         as undetected. The line carries everything in
        '         one shot: in-game character name (the "Gina" or
        '         "blingess" rather than the FLS handle), the
        '         CharacterId (uid), the Steam ID (player), and
        '         the message body. EventStore's ChatMessage
        '         handler resolves the speaker back to the active
        '         session via CharacterId/PlatformUserId match
        '         (both present in the chat line), falling back
        '         to the single-player heuristic on small
        '         servers — unchanged from LO's chat path.
        '         Chat is also where the session's DisplayName
        '         gets flipped from empty (or cached) to the
        '         current in-game character name, which then
        '         flows into subsequent leave-row snapshots from
        '         the Manager.
        '
        ' What this plugin partially detects:
        '   - Character spawn → character-name binding for
        '     silent (never-chats) players. The line
        '     "ConanSandbox: Display: Character ID <n> has name
        '     <CharacterName> and guild ID <g>." fires ~100-
        '     200ms after Join succeeded (and again on every
        '     respawn) and carries CharacterId + the in-game
        '     character name. The spawn rule below classifies
        '     as PlayerIdentity. EventStore handles the
        '     (cid + display, no pid) shape with a temporal
        '     heuristic: among recently-joined sessions with
        '     no CharacterId, the one joined in the last 3
        '     seconds is overwhelmingly likely to be this
        '     spawn's session — bind directly. Falls back to a
        '     cid-keyed stash when the heuristic is ambiguous
        '     (concurrent joins). The stash drains if a chat
        '     line later binds cid to the session via the
        '     ChatMessage → DrainPendingCidIdentity path.
        '
        '     Remaining limitation: busy-server scenarios
        '     with concurrent joins where multiple sessions
        '     match the temporal window AND no chat ever
        '     fires for one of them — those rows still render
        '     as the FLS handle. Bounded by concurrent join
        '     rate; not visible on the operator's typical
        '     low-population servers.
        '
        ' Named capture groups are built via string concat to
        ' defeat an editor-tooling issue that lowercases
        ' (?<Name> to (?<n>. Same workaround pattern Last Oasis
        ' and Factorio use.
        Dim gDisplayName = "(?<" & "DisplayName" & ">"
        Dim gPlatform = "(?<" & "Platform" & ">"
        Dim gPlatformPersona = "(?<" & "PlatformPersona" & ">"
        Dim gPlatformUserId = "(?<" & "PlatformUserId" & ">"
        Dim gRemoteAddress = "(?<" & "RemoteAddress" & ">"
        Dim gMatchState = "(?<" & "MatchState" & ">"
        Dim gMapPath = "(?<" & "MapPath" & ">"
        Dim gCharacterId = "(?<" & "CharacterId" & ">"
        Dim gMessage = "(?<" & "Message" & ">"

        Return New LogParseRule() {
            New LogParseRule With {
                .Name = "Player Connect (AcceptedConnection, IP only)",
                .Kind = ParsedEventKind.PlayerJoin,
                .Pattern = "LogNet: NotifyAcceptedConnection:.*?RemoteAddr: " & gRemoteAddress & "[\d.]+:\d+),"
            },
            New LogParseRule With {
                .Name = "Player Connect (Login request → Steam ID)",
                .Kind = ParsedEventKind.PlayerJoin,
                .Pattern = "LogNet: Login request: userId: " & gPlatform & "\w+):" & gPlatformUserId & "\d+) platform:"
            },
            New LogParseRule With {
                .Name = "Player Connect (Join succeeded → PlatformPersona)",
                .Kind = ParsedEventKind.PlayerJoin,
                .Pattern = "LogNet: Join succeeded: " & gPlatformPersona & "[^\r\n]+)$"
            },
            New LogParseRule With {
                .Name = "Character Spawn (Character ID → CharacterId + DisplayName)",
                .Kind = ParsedEventKind.PlayerIdentity,
                .Pattern = "ConanSandbox: Display: Character ID " & gCharacterId & "\d+) has name " & gDisplayName & ".+?) and guild ID \d+"
            },
            New LogParseRule With {
                .Name = "Player Disconnect Enrichment (UNetConnection::Close → IP + Steam ID)",
                .Kind = ParsedEventKind.PlayerLeave,
                .Pattern = "LogNet: UNetConnection::Close:.*?RemoteAddr: " & gRemoteAddress & "[\d.]+:\d+),.*?UniqueId: " & gPlatform & "\w+):" & gPlatformUserId & "\d+),"
            },
            New LogParseRule With {
                .Name = "Player Disconnect (Player disconnected → PlatformPersona)",
                .Kind = ParsedEventKind.PlayerLeave,
                .Pattern = "LogNet: Player disconnected: " & gPlatformPersona & "[^\r\n]+)$"
            },
            New LogParseRule With {
                .Name = "Server State (GameMode match state change)",
                .Kind = ParsedEventKind.ServerStateChange,
                .Pattern = "LogGameMode: Display: Match State Changed from \w+ to " & gMatchState & "\w+)"
            },
            New LogParseRule With {
                .Name = "Map Loaded (LogWorld: Bringing World)",
                .Kind = ParsedEventKind.TileLoaded,
                .Pattern = "LogWorld: Bringing World " & gMapPath & "/Game/Maps/\S+) up for play"
            },
            New LogParseRule With {
                .Name = "Chat Message (ChatWindow: Character ... said:)",
                .Kind = ParsedEventKind.ChatMessage,
                .Pattern = "ChatWindow: Character " & gDisplayName & ".+?) \(uid " & gCharacterId & "\d+), player " & gPlatformUserId & "\d+)\) said: " & gMessage & ".+)$"
            }
        }
    End Function

    ' ============================================================
    '  RCON
    ' ============================================================

    Public Function GetRconProtocol() As RconProtocol? Implements IGamePlugin.GetRconProtocol
        Return RconProtocol.SourceRcon
    End Function

    ' ============================================================
    '  Mods — deferred
    '
    '  Conan Exiles supports Steam Workshop mods, but the
    '  server-side modlist mechanism (a modlist.txt pointing at
    '  workshop content paths) and the Enhanced/Legacy compat
    '  rules (UE4 mods need recompilation for UE5) deserve their
    '  own pass. v1 returns Nothing so the file-management UI
    '  still exposes the mod directory via ManagedDirectories
    '  below for manual mod placement.
    ' ============================================================

    Public Function CreateModManager() As IModManager Implements IGamePlugin.CreateModManager
        Return Nothing
    End Function

    ' ============================================================
    '  IInstallationNoticeProvider
    '
    '  Two notices the new-install screen needs to surface:
    '
    '   1. Windows-only. Conan ships no native Linux binary; a
    '      Linux node will fail at launch with "binary not found"
    '      (GetExecutablePath returns empty for Linux). The
    '      notice front-runs that confusion.
    '
    '   2. Build (Enhanced vs Legacy) explainer. New users coming
    '      to PowerGSM after May 2026 may not know Enhanced =
    '      UE5 (current) vs Legacy = UE4 (older). The notice
    '      links the two halves of that nomenclature.
    '
    '  We don't probe the node for actual OS — the Manager's
    '  Platform resolution already drives the GetExecutablePath
    '  fallback. This notice is informational, not gating.
    ' ============================================================

    Public Function GetPreInstallNotices() As IReadOnlyList(Of InstallationNotice) Implements IInstallationNoticeProvider.GetPreInstallNotices
        Return New InstallationNotice() {
            New InstallationNotice With {
                .Severity = NoticeSeverity.Warning,
                .Title = "Windows nodes only",
                .Body = "Funcom doesn't ship a native Linux build of the Conan Exiles dedicated server. Install on a Linux node will fail at launch. The community workaround (Wine + xvfb) is outside this plugin's scope — if you need Linux hosting, run a Windows VM."
            },
            New InstallationNotice With {
                .Severity = NoticeSeverity.Warning,
                .Title = "Reserve the pinger port (game port + 1)",
                .Body = "Conan hard-codes a 'pinger' port at game port + 1 (UDP) — the server browser pings it to decide whether the server is alive, and there's no config or command-line way to move it. Leave game port + 1 free: don't set the Query Port (or any other UDP port) to it, or the server won't appear in the browser even though it's running. The default ports (game 7777, query 27015) are already clear of it."
            },
            New InstallationNotice With {
                .Severity = NoticeSeverity.Information,
                .Title = "Pick your Build before installing",
                .Body = "Enhanced (UE5) is the current default — Funcom's May 2026 upgrade. Legacy (UE4) stays on Steam under the 'conan-exiles-legacy' beta branch, useful if you have a mod-heavy server that isn't yet UE5-compatible. Both branches share saves and config layout; switching later is an Update operation, not a fresh install."
            }
        }
    End Function

    ' ============================================================
    '  IPrerequisiteProvider
    '
    '  Conan ships no _CommonRedist folder, so SteamCMD doesn't
    '  run any redistributable installers as part of the install.
    '  The game binary links against the Microsoft VC++ 2015-2022
    '  x64 runtime; on a machine that doesn't have it, launching
    '  ConanSandboxServer-Win64-Shipping.exe fails immediately
    '  with STATUS_DLL_NOT_FOUND (-1073741515) and no log file is
    '  produced — the OS-level loader fails before any of the
    '  game's own logging machinery comes up.
    '
    '  Surfacing this as a pre-install notice (driven by the
    '  Manager's prereq-check round trip to the node) means the
    '  user finds out BEFORE they spend 15-30 minutes downloading
    '  the dedicated-server depot and then watching the process
    '  exit silently with no diagnostic.
    '
    '  Always declared regardless of whether the node turns out to
    '  be Windows or Linux; the node's PrerequisiteProbe handles
    '  the Linux case (returns Installed=False, which would surface
    '  the notice — but the Windows-only static notice above also
    '  fires, so the user has two signals that Linux won't work).
    ' ============================================================

    Public Function GetRequiredPrerequisites() As IReadOnlyList(Of String) Implements IPrerequisiteProvider.GetRequiredPrerequisites
        Return New String() {"vcredist-2015-2022-x64"}
    End Function

    ' ============================================================
    '  IManagedDirectoriesProvider
    '
    '  Three directories we surface for file management:
    '
    '   1. ConanSandbox/Saved — the world. game.db + autosaves
    '      (game_backup_##.db). Read|Write|Delete so users can
    '      back up, restore from backup, or clear a stuck save.
    '      No extension filter — restoration sometimes means
    '      replacing game.db with one of the numbered backups,
    '      and locking to a single extension would prevent that
    '      drag-drop workflow.
    '
    '   2. ConanSandbox/Saved/Config/WindowsServer — the three
    '      INI files (Engine.ini, Game.ini, ServerSettings.ini).
    '      Read|Write|Delete restricted to .ini so unrelated
    '      files in that dir can't be touched. ServerSettings.ini
    '      and Engine.ini both get structured editor tabs via
    '      IInstanceFileEditorProvider further down (Server
    '      Settings + Network (Engine.ini) respectively); Game.ini
    '      stays as raw-file access until someone writes a schema
    '      for it. The raw-file access for the schema'd files
    '      remains useful for power-user edits beyond what the
    '      forms expose — e.g. clearing a stale BuildIdOverride
    '      line in Engine.ini that the structured editor
    '      intentionally doesn't touch.
    '
    '   3. ConanSandbox/Saved/Logs — log files for download.
    '      Read-only (you can grab them for debugging but
    '      shouldn't delete or replace the running ones; UE4
    '      writes to ConanSandbox.log unconditionally and
    '      replacing it during runtime is asking for trouble).
    '      No extension filter — both the live .log and the
    '      rotated .log.bak / .log.txt variants are useful.
    '
    '  Workshop mods would belong here too, but Conan's mod
    '  layout (modlist.txt + workshop downloads paths) is
    '  involved enough that exposing the directory without the
    '  modlist editing logic would mislead — users would drop
    '  .pak files and wonder why nothing loaded. Defer until
    '  the mod manager is real.
    ' ============================================================

    Public Function GetManagedDirectories(config As InstanceConfig) As IReadOnlyList(Of ManagedDirectory) Implements IManagedDirectoriesProvider.GetManagedDirectories
        Return New ManagedDirectory() {
            New ManagedDirectory With {
                .RelativePath = "ConanSandbox/Saved",
                .DisplayName = "World Data (game.db + backups)",
                .Permissions = DirPermissions.Read Or DirPermissions.Write Or DirPermissions.Delete
            },
            New ManagedDirectory With {
                .RelativePath = "ConanSandbox/Saved/Config/WindowsServer",
                .DisplayName = "Server Config (INIs)",
                .Permissions = DirPermissions.Read Or DirPermissions.Write Or DirPermissions.Delete,
                .AllowedExtensions = New List(Of String) From {".ini"}
            },
            New ManagedDirectory With {
                .RelativePath = "ConanSandbox/Saved/Logs",
                .DisplayName = "Log files",
                .Permissions = DirPermissions.Read
            }
        }
    End Function

    ' ============================================================
    '  IInstanceFileEditorProvider — ServerSettings.ini editor
    '
    '  One editor surfaces as a structured form on the InstancePanel:
    '
    '    Server Settings tab → ServerSettings.ini, [ServerSettings]
    '      section. Gameplay-rule knobs (PvP, multipliers, decay).
    '      The 21 fields below cover the most commonly-edited
    '      values; the other 80+ in that file round-trip
    '      unchanged via WriteValuesToFile's preserve-existing-
    '      text behaviour.
    '
    '  Engine.ini's [OnlineSubsystem] identity fields (ServerName,
    '  ServerPassword) used to have their own "Network (Engine.ini)"
    '  editor tab. They're now Configuration-tab fields rendered into
    '  Engine.ini at launch (see the IStartupFileProvider block), so
    '  that tab is gone. Other [OnlineSubsystem] entries — notably
    '  BuildIdOverride / bUseBuildIdOverride, which per Inflexion's
    '  troubleshooting guide can block clients connecting after
    '  Enhanced-build upgrades — round-trip verbatim through the
    '  render's preserve-existing-text writer; operators who need to
    '  edit them do so on raw Engine.ini via the Server Config (INIs)
    '  directory.
    '
    '  ServerName / ServerPassword routing:
    '    Both are Configuration-tab fields rendered into Engine.ini
    '    [OnlineSubsystem] at launch (IStartupFileProvider), not on
    '    the command line — a URL ?ServerPassword= dies with
    '    AESDecryptionFailed and a URL ?ServerName= mangles on
    '    spaces/unicode. See the file-header and IStartupFileProvider
    '    comments for the full rationale and blank-handling.
    '
    '  Why this set of ServerSettings fields:
    '    21 of the 100+ ServerSettings.ini knobs, chosen for how
    '    often they actually get edited (community/MOTD, PvP
    '    toggle, the damage/XP/harvest multipliers that define
    '    a server's difficulty, day-cycle speed, structure decay
    '    rules). Surfacing all 100+ would create a form that's
    '    worse than the raw file.
    '
    '  Fields NOT in the ServerSettings schema:
    '    - ServerName / ServerPassword. Configuration-tab fields
    '      rendered into Engine.ini [OnlineSubsystem] at launch
    '      (not ServerSettings.ini). See the IStartupFileProvider
    '      block.
    '    - The 80-odd other ServerSettings.ini fields a power
    '      user might want. They round-trip verbatim through
    '      WriteValuesToFile's preserve-existing-text behaviour,
    '      so hand edits to anything outside this schema survive
    '      saves from the form.
    '
    '  Floats are stored as Text rather than a dedicated Float
    '  type because PowerGSM's ConfigFieldType enum has no Float
    '  member — multipliers like "1.5" or "0.75" go through as
    '  string values, validated only by Conan parsing them at
    '  launch. The description on each field calls out the
    '  default and what direction the knob points.
    '
    '  Booleans use the schema's BooleanField type (lowercase
    '  "true"/"false" in the values dict, matching Factorio's
    '  convention) and are written back to the INI as Conan's
    '  conventional True/False capitalisation.
    '
    '  Multi-section dispatch:
    '    ReadFileToValues / WriteValuesToFile dispatch on
    '    editorKey to resolve the section name + schema for the
    '    file being edited. Read is section-scoped (only keys
    '    inside the target section's [Header] are considered),
    '    write replaces only schema keys inside the target
    '    section, and lines outside the target section pass
    '    through unchanged. Engine.ini has many sections
    '    ([Core.System], [URL], [/Script/...], etc.) that
    '    PowerGSM has no opinion about; the section-scoped
    '    approach guarantees we never accidentally rewrite an
    '    unrelated key just because it shares a name with a
    '    schema field.
    ' ============================================================

    Private Const ServerSettingsEditorKey As String = "server-settings"
    Private Const ServerSettingsRelativePath As String = "ConanSandbox/Saved/Config/WindowsServer/ServerSettings.ini"
    Private Const ServerSettingsSectionName As String = "ServerSettings"

    Private Const EngineIniRelativePath As String = "ConanSandbox/Saved/Config/WindowsServer/Engine.ini"
    Private Const OnlineSubsystemSectionName As String = "OnlineSubsystem"

    ' VB runtime shims. Roslyn compiles each plugin file without
    ' Microsoft.VisualBasic.dll in the reference set, so the
    ' usual built-ins (vbCrLf, vbLf, vbCr, AscW, ChrW) aren't in
    ' scope here. Reconstructing them through System.Convert
    ' keeps the INI line-ending and BOM handling on pure BCL
    ' types. The values are computed once at class init; their
    ' identities never change so reusing them is cheap.
    Private Shared ReadOnly _crlf As String =
        Convert.ToChar(13).ToString() & Convert.ToChar(10).ToString()
    Private Shared ReadOnly _lf As String = Convert.ToChar(10).ToString()
    Private Shared ReadOnly _cr As String = Convert.ToChar(13).ToString()
    Private Shared ReadOnly _bomChar As Char = Convert.ToChar(&HFEFF)

    Public Function GetInstanceFileEditors(config As InstanceConfig) _
            As IReadOnlyList(Of InstanceFileEditor) _
            Implements IInstanceFileEditorProvider.GetInstanceFileEditors
        Return New InstanceFileEditor() {
            New InstanceFileEditor With {
                .Key = ServerSettingsEditorKey,
                .TabTitle = "Server Settings",
                .RelativePath = ServerSettingsRelativePath,
                .Schema = BuildServerSettingsSchema()
            }
        }
    End Function

    ''' <summary>
    ''' Maps an editor key back to its INI section name. Used by
    ''' Read/WriteValuesToFile to scope INI parsing/serialisation
    ''' to one section so multi-section files like Engine.ini
    ''' don't accidentally pick up keys from sections the editor
    ''' has no opinion about. Returns Nothing for unknown keys
    ''' — callers treat that as "no values" / "no write".
    ''' </summary>
    Private Shared Function ResolveSectionName(editorKey As String) As String
        Select Case editorKey
            Case ServerSettingsEditorKey
                Return ServerSettingsSectionName
            Case Else
                Return Nothing
        End Select
    End Function

    ''' <summary>
    ''' Maps an editor key back to its schema. Same dispatch
    ''' pattern as ResolveSectionName — unknown keys return an
    ''' empty schema so the read/write methods short-circuit
    ''' to no-op behaviour rather than reaching for fields that
    ''' don't exist.
    ''' </summary>
    Private Shared Function ResolveSchema(editorKey As String) As IReadOnlyList(Of ConfigFieldDescriptor)
        Select Case editorKey
            Case ServerSettingsEditorKey
                Return BuildServerSettingsSchema()
            Case Else
                Return New ConfigFieldDescriptor() {}
        End Select
    End Function

    Private Shared Function BuildServerSettingsSchema() As IReadOnlyList(Of ConfigFieldDescriptor)
        ' Order is read-order: identity → PvP → combat →
        ' progression → day-cycle → buildings → land/ownership.
        ' Descriptions carry [Section] prefixes since
        ' SchemaFormBuilder doesn't render headers (per
        ' Factorio's matching note).
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "AdminPassword",
                .Label = "Admin password",
                .Description = "[Identity] Type this in-game (Settings → Server Settings → Make Me Admin) to grant admin rights. Also required for RCON karma to function above 0 — without it RCON is effectively non-functional. Different from the connect-time ServerPassword on the Engine.ini (Network) tab.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True,
                .IsRequired = True
            },
            New ConfigFieldDescriptor With {
                .Key = "ServerCommunity",
                .Label = "Server community",
                .Description = "[Identity] Playstyle tag shown in the server browser. Conan accepts an integer here: 0=None, 1=Purist, 2=Relaxed, 3=Hard Core, 4=Role Playing, 5=Experimental.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "0"
            },
            New ConfigFieldDescriptor With {
                .Key = "ServerMessageOfTheDay",
                .Label = "Message of the day",
                .Description = "[Identity] Shown to players on connect. Plain text — newlines in the INI value will not render.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = ""
            },
            New ConfigFieldDescriptor With {
                .Key = "MaxNudity",
                .Label = "Max nudity",
                .Description = "[Identity] 0 = None, 1 = Partial, 2 = Full. The server caps client preference at this value.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "0",
                .MinValue = 0,
                .MaxValue = 2
            },
            New ConfigFieldDescriptor With {
                .Key = "PVPEnabled",
                .Label = "PvP enabled",
                .Description = "[PvP] Allow players to damage each other. Independent of ServerCommunity tag.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "PlayerDamageMultiplier",
                .Label = "Player damage dealt ×",
                .Description = "[Combat] Scales damage players deal to anything. Default 1.0. Higher = players hit harder.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "PlayerDamageTakenMultiplier",
                .Label = "Player damage taken ×",
                .Description = "[Combat] Scales damage players take. Default 1.0. Lower = players are tankier.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "NPCDamageMultiplier",
                .Label = "NPC damage dealt ×",
                .Description = "[Combat] Scales damage NPCs deal to players. Default 1.0.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "NPCDamageTakenMultiplier",
                .Label = "NPC damage taken ×",
                .Description = "[Combat] Scales damage NPCs take from players. Default 1.0. Higher = NPCs die faster.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "PlayerXPRateMultiplier",
                .Label = "XP rate ×",
                .Description = "[Progression] Global multiplier on all XP gain. Default 1.0.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "HarvestAmountMultiplier",
                .Label = "Harvest amount ×",
                .Description = "[Progression] Resources gathered per swing. Default 1.0. The single biggest pacing knob — most servers run 2.0 to 5.0.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "ResourceRespawnSpeedMultiplier",
                .Label = "Resource respawn speed ×",
                .Description = "[Progression] How fast harvested nodes (trees, ore, etc.) regrow. Default 1.0. Lower = world stays barer longer.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "StaminaCostMultiplier",
                .Label = "Stamina cost ×",
                .Description = "[Progression] Scales stamina drain from sprinting, attacking, jumping. Default 1.0. Lower = stamina lasts longer.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "ItemSpoilRateScale",
                .Label = "Food spoil rate ×",
                .Description = "[Progression] How fast food/perishables decay in inventory. Default 1.0. Lower = food keeps longer.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "DayCycleSpeedScale",
                .Label = "Day cycle speed ×",
                .Description = "[Time] Overall day/night cycle speed. Default 1.0. Lower = longer days AND nights.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "StructureDamageMultiplier",
                .Label = "Structure damage ×",
                .Description = "[Buildings] Damage dealt to player-built structures. Default 1.0. Lower = builds harder to raid.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "StructureHealthMultiplier",
                .Label = "Structure health ×",
                .Description = "[Buildings] HP multiplier for player-built structures. Default 1.0.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "StructureDecayDisabled",
                .Label = "Disable structure decay",
                .Description = "[Buildings] When on, builds never decay. Convenient for small PvE servers; on long-running public servers you usually want decay enabled so abandoned bases clean themselves up.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "LandClaimRadiusMultiplier",
                .Label = "Land claim radius ×",
                .Description = "[Buildings] Scales the no-build bubble around each foundation. Default 1.0. Lower = neighbours can build closer.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "1.0"
            },
            New ConfigFieldDescriptor With {
                .Key = "CanDamagePlayerOwnedStructures",
                .Label = "Can damage player structures",
                .Description = "[Buildings] Off = nothing damages player builds (purelife PvE). On = raids/explosives possible.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "ContainersIgnoreOwnership",
                .Label = "Containers ignore ownership",
                .Description = "[Rules] On = any player can open any chest/cabinet. Off = only the owner and clanmates. PvE-Conflict servers often want this on.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "LogoutCharactersRemainInTheWorld",
                .Label = "Bodies remain on logout",
                .Description = "[Rules] On = logged-out players leave a sleeper body that can be killed/looted. Off = bodies vanish on disconnect. PvP servers usually want this on for raid risk.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            }
        }
    End Function

    Public Function ReadFileToValues(editorKey As String, fileText As String) _
            As Dictionary(Of String, String) _
            Implements IInstanceFileEditorProvider.ReadFileToValues

        ' Section-scoped INI reader. Walks fileText once tracking
        ' the current [Section] context, and only considers
        ' key=value lines inside the target section. This matters
        ' for Engine.ini where the same key name can legitimately
        ' appear in several sections — we only want the one under
        ' [OnlineSubsystem]. For ServerSettings.ini the section
        ' discipline is also correct: Conan's own writer emits
        ' everything under [ServerSettings], so legitimate edits
        ' never put schema-managed keys outside that section.
        '
        ' Returns lowercase "true"/"false" for booleans (Factorio
        ' convention, what BooleanField round-trips through the
        ' form). All other values pass through as the raw INI
        ' string. Missing keys are simply absent from the dict
        ' — the schema's DefaultValue takes over.
        '
        ' Comments use `;` or sometimes `#`.

        Dim values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        If String.IsNullOrWhiteSpace(fileText) Then Return values

        Dim sectionName = ResolveSectionName(editorKey)
        Dim schema = ResolveSchema(editorKey)
        If String.IsNullOrEmpty(sectionName) OrElse schema.Count = 0 Then
            Return values  ' unknown editor key — no values to surface
        End If

        Dim schemaKeys = BuildSchemaKeySet(schema)
        Dim boolKeys = BuildBoolKeySet(schema)

        Dim inTargetSection As Boolean = False
        Dim lines = SplitLinesPreservingOrder(fileText)
        For Each rawLine In lines
            Dim line = rawLine.Trim()
            If line.Length = 0 Then Continue For
            If line.StartsWith(";") OrElse line.StartsWith("#") Then Continue For

            ' Section header — update the in/out flag and move on.
            ' Conan's INI writer (and most editors) emit headers
            ' as the trimmed line literally starting with `[` and
            ' ending with `]` on the same line; we don't try to
            ' handle multi-line or commented headers because they
            ' don't appear in practice.
            If line.Length >= 2 AndAlso line.StartsWith("[") AndAlso line.EndsWith("]") Then
                Dim header = line.Substring(1, line.Length - 2).Trim()
                inTargetSection = header.Equals(sectionName, StringComparison.OrdinalIgnoreCase)
                Continue For
            End If

            If Not inTargetSection Then Continue For

            Dim eqIdx = line.IndexOf("="c)
            If eqIdx <= 0 Then Continue For

            Dim key = line.Substring(0, eqIdx).Trim()
            Dim rawValue = line.Substring(eqIdx + 1).Trim()

            If Not schemaKeys.Contains(key) Then Continue For

            ' Last write wins — mirrors Conan's own parser, which
            ' takes the final occurrence of a duplicated key.
            If boolKeys.Contains(key) Then
                values(key) = NormalizeBoolToLower(rawValue)
            Else
                values(key) = rawValue
            End If
        Next

        Return values
    End Function

    Public Function WriteValuesToFile(editorKey As String,
                                       values As Dictionary(Of String, String),
                                       existingText As String) As String _
            Implements IInstanceFileEditorProvider.WriteValuesToFile

        ' Strategy: walk existingText line by line; when we hit
        ' a schema-managed key inside the target section, replace
        ' its value. Comments, blank lines, unknown keys, and
        ' other sections all pass through unchanged. After the
        ' target section ends (or EOF), append any schema keys
        ' that weren't already present.
        '
        ' Duplicates: if the user had two lines for the same
        ' schema key, the first replacement wins and subsequent
        ' lines are dropped — otherwise the engine's last-wins
        ' parser would silently revert the form's edit.
        '
        ' Bools are written as Conan-style True/False even
        ' though the values dict carries lowercase "true"/"false"
        ' so generated files match what Conan itself writes when
        ' the in-game admin panel rewrites the file.
        '
        ' Section + schema are resolved from editorKey so the
        ' same code drives both ServerSettings.ini (single-section
        ' file, [ServerSettings]) and Engine.ini (multi-section
        ' file, schema scoped to [OnlineSubsystem]). For Engine.ini
        ' specifically, every section outside [OnlineSubsystem]
        ' — there are many — falls into the pass-through branch
        ' and round-trips byte-for-byte.

        Dim targetSection = ResolveSectionName(editorKey)
        Dim schema = ResolveSchema(editorKey)
        If String.IsNullOrEmpty(targetSection) OrElse schema.Count = 0 Then
            ' Unknown editor key — don't risk mangling the file.
            ' Returning existingText verbatim mirrors a successful
            ' no-op save.
            Return If(existingText, "")
        End If

        Return WriteIniSection(targetSection, schema, values, existingText)
    End Function

    ''' <summary>
    ''' Core INI section writer shared by the file editor
    ''' (WriteValuesToFile) and the startup render
    ''' (RenderStartupFile). Writes every key in `schema` into
    ''' `targetSection` of `existingText`, preserving comments,
    ''' blank lines, unknown keys, and every other section verbatim.
    ''' The caller controls which keys are in `schema`: the render
    ''' OMITS a key to leave the file's existing value untouched
    ''' (its preserve-if-blank path).
    ''' </summary>
    Private Shared Function WriteIniSection(targetSection As String,
                                             schema As IReadOnlyList(Of ConfigFieldDescriptor),
                                             values As Dictionary(Of String, String),
                                             existingText As String) As String
        Dim safeValues As Dictionary(Of String, String) = If(values, New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase))
        Dim boolKeys = BuildBoolKeySet(schema)

        ' Resolve final emitted value for each schema key once,
        ' so the section walker and the trailing append both
        ' agree on what to write.
        Dim resolved As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For Each desc In schema
            Dim raw As String = Nothing
            safeValues.TryGetValue(desc.Key, raw)
            If raw Is Nothing Then raw = desc.DefaultValue
            If raw Is Nothing Then raw = ""

            If boolKeys.Contains(desc.Key) Then
                resolved(desc.Key) = NormalizeBoolToConanIni(raw)
            Else
                resolved(desc.Key) = raw.Trim()
            End If
        Next

        ' Fresh file: build from scratch with section header +
        ' every schema key in declaration order.
        If String.IsNullOrWhiteSpace(existingText) Then
            Dim sbFresh As New System.Text.StringBuilder()
            sbFresh.Append("[")
            sbFresh.Append(targetSection)
            sbFresh.Append("]")
            sbFresh.Append(_crlf)
            For Each desc In schema
                sbFresh.Append(desc.Key)
                sbFresh.Append("="c)
                sbFresh.Append(resolved(desc.Key))
                sbFresh.Append(_crlf)
            Next
            Return sbFresh.ToString()
        End If

        ' Existing file: line-by-line walk.
        Dim originalEndedWithNewline = existingText.EndsWith(_lf) OrElse existingText.EndsWith(_cr)
        Dim lines = SplitLinesPreservingOrder(existingText)

        Dim sb As New System.Text.StringBuilder()
        Dim emitted As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim inTargetSection As Boolean = False
        Dim targetSectionSeen As Boolean = False

        For Each line In lines
            Dim trimmed = line.Trim()
            Dim isSectionHeader = trimmed.Length >= 2 AndAlso trimmed.StartsWith("[") AndAlso trimmed.EndsWith("]")

            If isSectionHeader Then
                ' Leaving the target section — flush any schema
                ' keys we haven't written yet before the new
                ' section header lands.
                If inTargetSection Then
                    AppendMissingSchemaKeys(sb, schema, resolved, emitted)
                    inTargetSection = False
                End If
                Dim headerName = trimmed.Substring(1, trimmed.Length - 2).Trim()
                If headerName.Equals(targetSection, StringComparison.OrdinalIgnoreCase) Then
                    inTargetSection = True
                    targetSectionSeen = True
                End If
                sb.Append(line)
                sb.Append(_crlf)
                Continue For
            End If

            If inTargetSection AndAlso Not (trimmed.Length = 0 OrElse trimmed.StartsWith(";") OrElse trimmed.StartsWith("#")) Then
                Dim eqIdx = line.IndexOf("="c)
                If eqIdx > 0 Then
                    Dim key = line.Substring(0, eqIdx).Trim()
                    If resolved.ContainsKey(key) Then
                        If emitted.Contains(key) Then
                            ' Duplicate schema key — drop the
                            ' extra so the engine's last-wins
                            ' parser doesn't shadow our edit.
                            Continue For
                        End If
                        sb.Append(key)
                        sb.Append("="c)
                        sb.Append(resolved(key))
                        sb.Append(_crlf)
                        emitted.Add(key)
                        Continue For
                    End If
                End If
            End If

            ' Pass-through: comments, blanks, unknown keys, lines
            ' outside the target section.
            sb.Append(line)
            sb.Append(_crlf)
        Next

        ' EOF reached while still inside the target section (no
        ' trailing section header) — flush missing keys here.
        If inTargetSection Then
            AppendMissingSchemaKeys(sb, schema, resolved, emitted)
        End If

        ' Target section never appeared in the file — append a
        ' fresh section at the end with every schema key.
        If Not targetSectionSeen Then
            sb.Append("[")
            sb.Append(targetSection)
            sb.Append("]")
            sb.Append(_crlf)
            AppendMissingSchemaKeys(sb, schema, resolved, emitted)
        End If

        Dim result = sb.ToString()

        ' Match the original's trailing-newline behaviour so the
        ' diff against a freshly-launched server stays minimal.
        If Not originalEndedWithNewline AndAlso result.EndsWith(_crlf) Then
            result = result.Substring(0, result.Length - _crlf.Length)
        End If

        Return result
    End Function

    ' ============================================================
    '  IStartupFileProvider — ServerName + ServerPassword render
    '
    '  Conan reads both from Engine.ini [OnlineSubsystem], and
    '  neither survives the launch command line cleanly (ServerName
    '  mangles on spaces/unicode; ServerPassword fails the
    '  OnlineSubsystem handshake → AESDecryptionFailed). So they're
    '  dropped from BuildLaunchArguments and written into Engine.ini
    '  here, just before launch, via the same section-scoped writer
    '  the file editor uses. Best-effort on the Manager side: a
    '  write failure warns and the launch proceeds with the file's
    '  last values.
    '
    '  Blank-handling: ServerName always writes (blank → default
    '  name, so a server is never nameless). ServerPassword is
    '  preserve-if-blank — a blank field is simply omitted from the
    '  render schema so the file's existing value round-trips
    '  untouched — UNLESS ClearServerPassword is set, which writes an
    '  empty password (open server). That keeps an upgrade from the
    '  old Engine.ini-editor version from wiping a set password.
    ' ============================================================

    Public Function GetStartupFiles(instanceConfig As InstanceConfig) _
            As IReadOnlyList(Of String) _
            Implements IStartupFileProvider.GetStartupFiles
        Return New String() {EngineIniRelativePath}
    End Function

    Public Function RenderStartupFile(relativePath As String,
                                       instanceConfig As InstanceConfig,
                                       existingText As String) As String _
            Implements IStartupFileProvider.RenderStartupFile

        ' Only Engine.ini.
        If Not String.Equals(relativePath, EngineIniRelativePath, StringComparison.OrdinalIgnoreCase) Then
            Return Nothing
        End If

        ' Conan creates Engine.ini (with all its sections) on first
        ' launch. Don't fabricate a minimal one before it exists —
        ' skip the render and let the server write the file; the
        ' values apply from the second launch onward.
        If String.IsNullOrWhiteSpace(existingText) Then Return Nothing

        Dim fields = If(instanceConfig IsNot Nothing, instanceConfig.CustomFields, Nothing)

        ' Build a render schema + values on the fly. A key present in
        ' the schema is written into [OnlineSubsystem]; a key omitted
        ' is left untouched in the file (preserve-if-blank).
        Dim renderSchema As New List(Of ConfigFieldDescriptor)
        Dim renderValues As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        ' ServerName: always write; blank falls back to the default
        ' so the server is never left nameless.
        Dim serverName = GetField(fields, "ServerName")
        If String.IsNullOrEmpty(serverName) Then serverName = "PowerGSM Conan Server"
        renderSchema.Add(New ConfigFieldDescriptor With {.Key = "ServerName", .FieldType = ConfigFieldType.Text})
        renderValues("ServerName") = serverName

        ' ServerPassword: set / clear / preserve.
        Dim serverPassword = GetField(fields, "ServerPassword")
        Dim clearPassword As Boolean
        If Not Boolean.TryParse(GetField(fields, "ClearServerPassword"), clearPassword) Then clearPassword = False
        If Not String.IsNullOrEmpty(serverPassword) Then
            renderSchema.Add(New ConfigFieldDescriptor With {.Key = "ServerPassword", .FieldType = ConfigFieldType.Password})
            renderValues("ServerPassword") = serverPassword
        ElseIf clearPassword Then
            renderSchema.Add(New ConfigFieldDescriptor With {.Key = "ServerPassword", .FieldType = ConfigFieldType.Password})
            renderValues("ServerPassword") = ""
        End If
        ' else: omit ServerPassword -> existing file value preserved.

        Return WriteIniSection(OnlineSubsystemSectionName, renderSchema, renderValues, existingText)
    End Function

    ' ---- INI helpers ----

    Private Shared Sub AppendMissingSchemaKeys(sb As System.Text.StringBuilder,
                                                schema As IReadOnlyList(Of ConfigFieldDescriptor),
                                                resolved As Dictionary(Of String, String),
                                                emitted As HashSet(Of String))
        For Each desc In schema
            If emitted.Contains(desc.Key) Then Continue For
            sb.Append(desc.Key)
            sb.Append("="c)
            sb.Append(resolved(desc.Key))
            sb.Append(_crlf)
            emitted.Add(desc.Key)
        Next
    End Sub

    Private Shared Function BuildSchemaKeySet(schema As IReadOnlyList(Of ConfigFieldDescriptor)) As HashSet(Of String)
        Dim set_ As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each desc In schema
            set_.Add(desc.Key)
        Next
        Return set_
    End Function

    Private Shared Function BuildBoolKeySet(schema As IReadOnlyList(Of ConfigFieldDescriptor)) As HashSet(Of String)
        Dim set_ As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each desc In schema
            If desc.FieldType = ConfigFieldType.BooleanField Then
                set_.Add(desc.Key)
            End If
        Next
        Return set_
    End Function

    ''' <summary>
    ''' Split text into lines while stripping CR so the writer
    ''' can emit clean CRLF-terminated output regardless of
    ''' what line endings the source file used. If the file
    ''' ended with a newline, the final empty entry is trimmed
    ''' so the writer doesn't double up newlines.
    ''' </summary>
    Private Shared Function SplitLinesPreservingOrder(text As String) As List(Of String)
        Dim result As New List(Of String)
        If String.IsNullOrEmpty(text) Then Return result

        ' Strip UTF-8 BOM if present — Conan INIs sometimes get
        ' written with one by editors like Notepad.
        If text.Length > 0 AndAlso text(0) = _bomChar Then
            text = text.Substring(1)
        End If

        Dim parts = text.Split({_crlf, _lf, _cr}, StringSplitOptions.None)
        ' Drop trailing empty entry that comes from a file-final
        ' newline so callers don't see a phantom blank line.
        Dim lastIdx = parts.Length - 1
        Dim limit = If(lastIdx >= 0 AndAlso parts(lastIdx) = "", lastIdx, parts.Length)
        For i = 0 To limit - 1
            result.Add(parts(i))
        Next
        Return result
    End Function

    ''' <summary>
    ''' Normalise whatever boolean-shaped string the user (or
    ''' the game) put in the INI to the lowercase "true"/"false"
    ''' the form layer expects.
    ''' </summary>
    Private Shared Function NormalizeBoolToLower(raw As String) As String
        If raw Is Nothing Then Return "false"
        Select Case raw.Trim().ToLowerInvariant()
            Case "true", "1", "yes", "on"
                Return "true"
            Case Else
                Return "false"
        End Select
    End Function

    ''' <summary>
    ''' Convert the form's lowercase boolean back to the
    ''' capital-first True/False Conan's INI writer uses
    ''' natively. Keeps round-trips through the in-game admin
    ''' panel diff-clean against PowerGSM-saved files.
    ''' </summary>
    Private Shared Function NormalizeBoolToConanIni(raw As String) As String
        If NormalizeBoolToLower(raw) = "true" Then Return "True"
        Return "False"
    End Function

    ' ============================================================
    '  ILaunchOptionsProvider
    '
    '  Conan needs a longer graceful-shutdown window than
    '  PowerGSM's universal 25-second default. The UE5 (Enhanced)
    '  / UE4 (Legacy) dedicated server, on receiving CTRL_C_EVENT,
    '  runs through RequestEngineExit:
    '
    '    1. Send shutdown-warning packet to every connected
    '       client and wait for ack (or per-client timeout).
    '    2. Persist game.db — the entire SQLite world state
    '       including builder claims, structure components,
    '       inventories, character states. Scales linearly with
    '       the world's build complexity; populated servers
    '       routinely persist 50–300 MB.
    '    3. Flush in-flight RCON sessions and close listeners.
    '    4. Tear down the UE world (LogExit lines in the log).
    '
    '  On a 40-player populated realm with mature builds, the
    '  productive part of graceful shutdown observed in the wild
    '  is 30–60 seconds. The 25-second default cuts the process
    '  off mid-step 2, which can leave game.db in a partially-
    '  written state (SQLite's WAL recovers most cases, but the
    '  next launch loses any state written after the last
    '  checkpoint — typically multiple minutes of play).
    '
    '  Why 90 seconds (rather than the 120 we briefly tried):
    '    Operational reality is that Conan servers frequently
    '    HANG during step 2/3 rather than running long — a stuck
    '    PersistAll() or a wedged RCON socket holds the process
    '    open indefinitely, and no amount of additional waiting
    '    rescues it. 120 seconds turned out to just be 120
    '    seconds of waiting before the force-kill that was
    '    always going to happen. 90 is comfortably above the
    '    60-second productive ceiling for a successful shutdown
    '    on a populated realm while keeping the dead time on a
    '    hung shutdown to something operators don't curse at.
    '    60 would catch the productive ceiling — plausible if
    '    measurements show it's enough; current default favours
    '    margin until that's confirmed.
    '
    '  Operators on smaller realms can override down per-instance
    '  by setting a "GracefulTimeoutMs" custom field on the
    '  instance — the per-instance value takes precedence over
    '  this plugin default.
    '
    '  Why opt into ILaunchOptionsProvider just for this:
    '    The interface already exists for engine-specific spawn
    '    customisation (Factorio's RequiresConsoleIsolation, etc.)
    '    and graceful-shutdown duration is engine-specific in
    '    the same way. Adding a new dedicated interface for one
    '    integer wasn't justified. The other booleans on
    '    LaunchOptions stay at defaults: Conan's stdio is not the
    '    log stream (file log is canonical), its server binary
    '    doesn't defeat CREATE_NEW_CONSOLE the way Factorio does,
    '    and the legacy 5000ms log-tailer start delay is correct
    '    for UE-class engines.
    ' ============================================================

    Public Function GetLaunchOptions(config As InstanceConfig) As LaunchOptions Implements ILaunchOptionsProvider.GetLaunchOptions
        Return New LaunchOptions With {
            .GracefulShutdownTimeoutMs = 90000
        }
    End Function

    ' ============================================================
    '  Helpers
    ' ============================================================

    Private Shared Function GetField(fields As Dictionary(Of String, String),
                                      key As String) As String
        If fields Is Nothing Then Return ""
        Dim result As String = Nothing
        If fields.TryGetValue(key, result) Then Return If(result, "")
        Return ""
    End Function

    Private Shared Function GetFieldInt(fields As Dictionary(Of String, String),
                                         key As String,
                                         defaultValue As Integer) As Integer
        Dim strVal = GetField(fields, key)
        Dim parsed As Integer
        If Integer.TryParse(strVal, parsed) Then Return parsed
        Return defaultValue
    End Function

    ''' <summary>
    ''' Escape a value for use inside a UE4 URL launch arg —
    ''' i.e. one of the ?Key=Value pairs concatenated into the
    ''' positional first arg.
    '''
    ''' UE4's URL parser splits on `?` and treats the literal
    ''' characters `?`, ` ` (space), and `"` as terminators or
    ''' breakers. A ServerName like `My ? Server` would otherwise
    ''' become `?ServerName=My`, `?`, `Server` — three broken
    ''' tokens. Mapping the offending characters to `%XX`-style
    ''' escapes is what the engine's own URL handling decodes
    ''' back to the original characters.
    '''
    ''' Conservative scope: encode the small set of characters
    ''' that demonstrably break parsing. Two of these are subtle
    ''' and easy to miss:
    '''
    '''   `%` MUST be escaped (to `%25`) FIRST in URL encoding,
    '''   since `%` is the percent-encoding sigil itself. A raw
    '''   `%` in the value gets misread by UE4 as the start of
    '''   a `%XX` sequence and either consumes the next two
    '''   chars or undefined-behaves if they aren't hex. This
    '''   is the root cause of Conan's "AESDecryptionFailed"
    '''   error on a password-protected server when the
    '''   password contains a `%` — the server stores a
    '''   mangled password, the AES key derived from it differs
    '''   from the one the client derives from the user-typed
    '''   password, and packets fail at the network layer
    '''   instead of at "wrong password" at the app layer.
    '''
    '''   `+` is escaped (to `%2B`) because HTTP-form URL
    '''   encoding treats `+` as an alias for space. UE4's
    '''   parser is inconsistent about which convention it
    '''   follows; escaping defensively avoids the
    '''   space-substitution failure mode without breaking
    '''   anything when the parser ignores form-encoding.
    '''
    ''' Everything else is passed through unchanged — Conan
    ''' happily renders names with most special characters once
    ''' they're not parser-significant. Underscore, apostrophe,
    ''' dash, dot, etc. pass through fine.
    '''
    ''' Quotes around the whole URL are NOT a substitute: UE4
    ''' strips outer quotes and re-parses the content, so
    ''' embedded `?`s still split it.
    ''' </summary>
    Private Shared Function EscapeUrlValue(value As String) As String
        If String.IsNullOrEmpty(value) Then Return ""
        Dim sb As New System.Text.StringBuilder(value.Length)
        For Each ch In value
            Select Case ch
                Case "%"c : sb.Append("%25")
                Case " "c : sb.Append("%20")
                Case "?"c : sb.Append("%3F")
                Case """"c : sb.Append("%22")
                Case "&"c : sb.Append("%26")
                Case "="c : sb.Append("%3D")
                Case "#"c : sb.Append("%23")
                Case "+"c : sb.Append("%2B")
                Case Else : sb.Append(ch)
            End Select
        Next
        Return sb.ToString()
    End Function

End Class

' ============================================================
'  Conan Exiles Log Parser
'  Detects player joins/leaves via UE4 LogNet patterns and
'  identifies UE4/UE5 crash markers.
'
'  Single-instance per install means no session-identity state
'  machine like Last Oasis has — the parser stays simple. The
'  name-to-IP correlation between NotifyAcceptedConnection and
'  Join succeeded mirrors what LO does, because UE4 splits the
'  same data across the same two log lines regardless of which
'  UE4 game runs.
'
'  Note: the post-colon token on "Join succeeded:" is the FLS
'  handle (e.g. "losno420#72569"), not the in-game character
'  name. The parser surfaces it as PlayerName so the broadcast
'  layer has SOMETHING to show on join notifications; the
'  Manager's snapshot enriches PlayerActivity rows with
'  DisplayName from the Node session (cached for returning
'  players, set by chat for first-time ones).
' ============================================================

Public Class ConanExilesLogParser
    Implements ILogParser

    Public ReadOnly Property GameId As String = "conanexiles" Implements ILogParser.GameId

    ' ------------------------------------------------------------
    ' Name↔IP correlation for resolving player names on disconnect.
    '
    ' Same mechanism as Last Oasis — UE4 emits the accepted
    ' connection's RemoteAddr on one line and the player name on
    ' a subsequent Join succeeded line. We bind them by
    ' remembering the most recent pending RemoteAddr.
    '
    ' Per-parser state, single-threaded callback per instance
    ' — no locking needed. Manager-side reconnects clear the
    ' bindings; HandlePlayerLeave's nameless-leave heuristic
    ' covers that gap.
    ' ------------------------------------------------------------

    Private _pendingRemoteAddr As String = Nothing
    Private ReadOnly _connectionsByAddr As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Private Shared ReadOnly _remoteAddrRegex As New Regex(
        "RemoteAddr:\s*([^\s,]+)",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)

    ''' <summary>
    ''' Conan Exiles doesn't have cross-instance session identity
    ''' the way Last Oasis does (no realm/tile model) — one
    ''' instance hosts one game.db world, and that world's
    ''' identity is the instance itself. Returning Nothing tells
    ''' downstream code to fall back to "{gameId}:{instanceId}",
    ''' which is the right default.
    ''' </summary>
    Public ReadOnly Property CurrentSessionIdentity As String _
        Implements ILogParser.CurrentSessionIdentity
        Get
            Return Nothing
        End Get
    End Property

    Public Function ParseLine(line As LogLine) As ParsedLogEvent Implements ILogParser.ParseLine
        If line Is Nothing OrElse String.IsNullOrEmpty(line.Text) Then
            Return ParsedLogEvent.NoMatch
        End If

        Dim text = line.Text

        ' ----- Connection tracking -----
        If text.Contains("LogNet: NotifyAcceptedConnection") Then
            Dim addr = ExtractRemoteAddr(text)
            If Not String.IsNullOrEmpty(addr) Then
                _pendingRemoteAddr = addr
            End If
            Return ParsedLogEvent.NoMatch
        End If

        ' ----- Player join (Join succeeded: FLS handle) -----
        ' Confirmed against multiple ConanSandbox.log captures:
        ' UE4 emits this line once per logical join, after the
        ' engine finishes loading the player into the world.
        ' The format is exactly "LogNet: Join succeeded:
        ' <FLS_handle>" with nothing else on the line. The
        ' handle is the platform-account identifier (e.g.
        ' "losno420" or "losno420#72569"), NOT the in-game
        ' character name — character names arrive via chat
        ' lines. The PlayerInfo.PlayerName here is the handle,
        ' used only by the broadcast layer for join notifications;
        ' History row PlayerName comes from the Manager's
        ' write-time snapshot of the Node session, which has
        ' the character name from the players-table cache or
        ' will get it from chat.
        If text.Contains("LogNet: Join succeeded:") Then
            Dim playerName = ExtractAfter(text, "Join succeeded: ")

            If Not String.IsNullOrEmpty(_pendingRemoteAddr) AndAlso
               Not String.IsNullOrWhiteSpace(playerName) Then
                _connectionsByAddr(_pendingRemoteAddr) = playerName
                _pendingRemoteAddr = Nothing
            End If

            Return New ParsedLogEvent With {
                .EventType = LogEventType.PlayerJoin,
                .Message = $"Player joined: {playerName}",
                .PlayerInfo = New PlayerInfo With {
                    .PlayerName = playerName,
                    .JoinedAt = line.Timestamp
                }
            }
        End If

        ' ----- Player leave (Player disconnected: NAME) -----
        ' Cleanest disconnect signal — fires once with the
        ' player name inline. Confirmed against the real log
        ' capture. We catch this before the UChannel/UNetConnection
        ' fallback below so it takes precedence and yields a
        ' named PlayerInfo without needing the IP↔name binding.
        If text.Contains("LogNet: Player disconnected:") Then
            Dim playerName = ExtractAfter(text, "Player disconnected: ")
            Dim info As PlayerInfo = Nothing
            If Not String.IsNullOrWhiteSpace(playerName) Then
                info = New PlayerInfo With {.PlayerName = playerName}
            End If

            Return New ParsedLogEvent With {
                .EventType = LogEventType.PlayerLeave,
                .Message = If(String.IsNullOrWhiteSpace(playerName),
                              "Player disconnected",
                              $"Player left: {playerName}"),
                .PlayerInfo = info
            }
        End If

        ' ----- Player leave (UChannel/UNetConnection::Close fallback) -----
        ' UChannel::Close (ChIndex == 0) and UNetConnection::Close
        ' both signal player disconnect. The latter is the
        ' terminal one — the connection object is gone after
        ' this — so we drop the addr→name binding only on
        ' UNetConnection::Close.
        If text.Contains("LogNet: UChannel::Close:") OrElse
           text.Contains("LogNet: UNetConnection::Close") Then

            Dim addr = ExtractRemoteAddr(text)
            Dim resolvedName As String = Nothing

            If Not String.IsNullOrEmpty(addr) Then
                _connectionsByAddr.TryGetValue(addr, resolvedName)

                If text.Contains("LogNet: UNetConnection::Close") Then
                    _connectionsByAddr.Remove(addr)
                End If
            End If

            Dim info As PlayerInfo = Nothing
            If Not String.IsNullOrEmpty(resolvedName) Then
                info = New PlayerInfo With {.PlayerName = resolvedName}
            End If

            Return New ParsedLogEvent With {
                .EventType = LogEventType.PlayerLeave,
                .Message = If(String.IsNullOrEmpty(resolvedName),
                              "Player disconnected",
                              $"Player left: {resolvedName}"),
                .PlayerInfo = info
            }
        End If

        ' ----- Server ready -----
        ' UE4's GameMode emits a state transition to InProgress
        ' once the world is loaded and accepting players. This
        ' is the canonical "server is ready" moment — fires once
        ' per startup, after the LogInit "Game Engine
        ' Initialized." line (which fires too early, before the
        ' world is up). Confirmed against the real log capture.
        If text.Contains("LogGameMode: Display: Match State Changed") AndAlso
           text.Contains("to InProgress") Then
            Return New ParsedLogEvent With {
                .EventType = LogEventType.ServerReady,
                .Message = "Server is ready (match state: InProgress)"
            }
        End If

        ' ----- UE4/UE5 crash patterns -----
        ' Same set as Last Oasis — these are engine-level markers
        ' that fire regardless of which UE-based game crashed.
        If text.Contains("Fatal error!") OrElse
           text.Contains("Unhandled Exception:") OrElse
           text.Contains("LowLevelFatalError") OrElse
           text.Contains("Access violation") OrElse
           text.Contains("=== Critical error: ===") OrElse
           text.Contains("Assertion failed:") Then
            Return New ParsedLogEvent With {
                .EventType = LogEventType.CrashIndicator,
                .Message = text
            }
        End If

        Return ParsedLogEvent.NoMatch
    End Function

    Public Function GetCrashPatterns() As IReadOnlyList(Of String) Implements ILogParser.GetCrashPatterns
        Return New String() {
            "Fatal error!",
            "Unhandled Exception:",
            "Access violation",
            "LowLevelFatalError",
            "=== Critical error: ===",
            "Assertion failed:"
        }
    End Function

    Private Shared Function ExtractAfter(text As String, marker As String) As String
        Dim idx = text.IndexOf(marker, StringComparison.Ordinal)
        If idx < 0 Then Return ""
        Return text.Substring(idx + marker.Length).Trim()
    End Function

    Private Shared Function ExtractRemoteAddr(text As String) As String
        Dim m = _remoteAddrRegex.Match(text)
        If m.Success Then Return m.Groups(1).Value
        Return ""
    End Function

End Class
