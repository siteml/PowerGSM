' <plugin id="windrose" name="Windrose Dedicated Server" version="0.1.0" author="siteml" requiresContracts="2">
' <RequiresContracts: 2>
Imports System
Imports System.Collections.Generic
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports GSM.Plugin

' ============================================================
'  Windrose Dedicated Server Plugin — SLICE 1 (core: install + launch)
'
'  AppID: 4129620 (free dedicated server, anonymous SteamCMD) —
'         separate from the game client app (3041230).
'  Engine: "R5" (Unreal-style; R5\Saved\... layout, RocksDB saves)
'  Install: SteamCMD only, anonymous (no purchased game needed)
'  RCON: none
'  Platform: Windows-only — no native Linux server binary.
'  EA: Steam Early Access since 14 Apr 2026 (Kraken Express /
'      Pocketpair). Paths, keys and numbers are version-dependent
'      and may shift between patches — re-verify against a live
'      install before trusting them.
'
'  SLICE SCOPE (Slices 1-2 of Windrose_Plugin_Plan.md):
'    1: install via SteamCMD, launch, lifecycle, crash handling,
'       install notices + prereqs.
'    2: ServerDescription.json structured editor + managed Logs
'       dir + graceful-shutdown timeout + the D1 direct-connection
'       defaults. NO world management (Slice 3) yet. Player-event
'       parsing (Slice 4) is now DONE — both the node-side
'       GetLogParseRules and the Manager-side WindroseLogParser.
'
'  CONFIRMED (StartServerForeground.bat + a live server log run,
'  21 Jun 2026):
'    - Engine: Unreal Engine 5.6.1, project "R5", build
'      0.10.0.6.213. So UE log/shutdown conventions apply.
'    - Exe: R5\Binaries\Win64\WindroseServer-Win64-Shipping.exe
'      (the UE Shipping binary, NOT the root WindroseServer.exe
'      wrapper), launched with -log.
'    - Log dir: R5\Saved\Logs (filename assumed R5.log).
'    - ServerDescription.json: R5\ServerDescription.json.
'    - VC++ runtime: genuinely required (UE5.6.1 Shipping).
'  RESOLVED (Slice 4 — live join/play/leave captures):
'    - Log file IS R5\Saved\Logs\R5.log (the node tailer finds it;
'      player events flow end-to-end to History).
'    - Player join/leave log lines confirmed — see WindroseLogParser
'      and GetLogParseRules below.
'
'  Why MaxInstancesPerInstallation = 1:
'    The server file-locks its RocksDB world database; the docs
'    ship CanLaunchMultipleServerInstances=false and warn that
'    running multiple instances against the same DB corrupts
'    saves. One install hosts many WORLDS (switched via the
'    WorldIslandId field) but only one runs at a time. Multiple
'    Windrose servers on a node = multiple Installations, one
'    install path each.
'
'  Networking note (Slice 1 caveat — see Decision D1 in the plan):
'    With no config editor yet, a freshly-installed server runs
'    in the GAME's default mode: ICE/P2P with UPnP NAT punch-
'    through (UseDirectConnection=false) — it opens ports on the
'    router by itself, and players join via an invite code.
'    Operator-controlled networking (one fixed port, no UPnP)
'    requires editing ServerDescription.json, which lands in
'    Slice 2 and defaults UseDirectConnection=true. The pre-
'    install notice below spells this out so nobody exposes a
'    UPnP-punching server unintentionally.
' ============================================================

Public Class WindrosePlugin
    Implements IGamePlugin
    Implements IInstallationNoticeProvider
    Implements IPrerequisiteProvider
    Implements IInstanceFileEditorProvider
    Implements IManagedDirectoriesProvider
    Implements ILaunchOptionsProvider
    Implements IStartupFileProvider

    Public ReadOnly Property GameId As String = "windrose" Implements IGamePlugin.GameId
    Public ReadOnly Property DisplayName As String = "Windrose" Implements IGamePlugin.DisplayName

    ' One running instance per install — RocksDB world DB is
    ' file-locked and the game's own multi-instance safeguard is
    ' off by default. Many worlds per install, one active at a
    ' time. Users wanting several Windrose servers on one node
    ' make several Installations.
    Public ReadOnly Property MaxInstancesPerInstallation As Integer? Implements IGamePlugin.MaxInstancesPerInstallation
        Get
            Return 1
        End Get
    End Property

    ' ============================================================
    '  Install
    ' ============================================================

    Public Function GetSupportedInstallMethods() As IReadOnlyList(Of InstallMethod) Implements IGamePlugin.GetSupportedInstallMethods
        Return New InstallMethod() {InstallMethod.SteamCmd}
    End Function

    Public Function GetInstallSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetInstallSteps
        Dim steps As New List(Of InstallStep)

        Dim steamStep As New SteamCmdStep()
        steamStep.StepName = "Download Windrose Dedicated Server"
        steamStep.Description = "Download/update via SteamCMD (AppID 4129620, anonymous)"
        steamStep.AppId = 4129620
        steamStep.ValidateFiles = True
        ' Anonymous install — the Windrose Dedicated Server is a
        ' free Steam tool that doesn't require account login. The
        ' user picks "(Anonymous — no login)" in the Steam
        ' credential dropdown; this flag is the plugin saying
        ' "no creds needed".
        steamStep.RequiresLogin = False

        steps.Add(steamStep)
        Return steps
    End Function

    Public Function GetUpdateSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetUpdateSteps
        ' Update == install for SteamCMD games — app_update 4129620
        ' validate reconciles to the latest build. Keeping the
        ' server version matched to the client version matters in
        ' Early Access: mismatches block joins and cause subtle
        ' bugs, so operators should Update after every game patch.
        Return GetInstallSteps(config)
    End Function

    ' ============================================================
    '  Instance
    ' ============================================================

    Public Function GetExecutablePath(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.GetExecutablePath
        ' Windows-only — no native Linux server binary. An empty
        ' list on Linux surfaces a clean "no executable candidates"
        ' failure instead of silently launching nothing.
        '
        ' Confirmed from StartServerForeground.bat: the real launch
        ' target is the UE Shipping binary
        '   R5\Binaries\Win64\WindroseServer-Win64-Shipping.exe
        ' NOT the root-level WindroseServer.exe. As with Conan, the
        ' root exe is a wrapper/launcher; tracking its PID would
        ' break lifecycle (the wrapper exits while the real server
        ' runs detached). Launch the Shipping binary directly so our
        ' process handle owns the actual server and -log graceful
        ' shutdown routes to the right place.
        '
        ' Forward slashes throughout: Windows file APIs accept them
        ' and the string survives the Manager -> Node marshalling
        ' unchanged.
        Select Case If(config IsNot Nothing, config.Platform, NodePlatform.Unknown)
            Case NodePlatform.Linux
                Return New String() {}
            Case Else
                Return New String() {
                    "R5/Binaries/Win64/WindroseServer-Win64-Shipping.exe"
                }
        End Select
    End Function

    Public Function BuildLaunchArguments(config As InstanceConfig) As String Implements IGamePlugin.BuildLaunchArguments
        ' Windrose's StartServerForeground.bat launches the Shipping
        ' binary with a single flag: "-log". There are no per-setting
        ' args — every setting (active world, ports, password, server
        ' name, region) comes from ServerDescription.json, which the
        ' exe reads from disk at startup. So "-log" is the whole
        ' command line.
        '
        ' Why -log matters beyond log output: it's a UE flag, and
        ' (same as Conan / Last Oasis) it makes the engine install
        ' SetConsoleCtrlHandler against the inherited console, so a
        ' later AttachConsole + CTRL_C_EVENT routes to
        ' RequestEngineExit for a graceful shutdown. Without it,
        ' Ctrl+C hits the OS default handler and stop falls through
        ' to force-kill. PowerGSM's spawn path hides the console
        ' (SW_HIDE) so the handler is armed with no visible window.
        Return "-log"
    End Function

    Public Function ValidateConfig(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.ValidateConfig
        ' The two Configuration fields (UseDirectConnection and the
        ' direct-connection port) are range/type-checked by their
        ' schema (Min/Max 1024-65535) and the port is clash-checked
        ' node-wide by the allocator, so there's no cross-field rule
        ' to enforce here. The descriptive settings (InviteCode
        ' charset/length, the IsPasswordProtected / Password pairing)
        ' live in ServerDescription.json and the file editor owns
        ' their validation via its own schema. Returns empty.
        Return New List(Of String)
    End Function

    ' ============================================================
    '  Config schema
    ' ============================================================

    Public Function GetInstallConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstallConfigSchema
        ' No install-level options — Windrose has a single public
        ' depot (no Enhanced/Legacy-style build choice) and no
        ' install-time keys. Empty schema.
        Return New ConfigFieldDescriptor() {}
    End Function

    Public Function GetInstanceConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstanceConfigSchema
        ' Networking lives HERE (not in the Server Settings file
        ' editor) so the node port allocator can see and clash-check
        ' the direct-connection port. The exe takes no launch args,
        ' so these values can't reach the server on the command line
        ' — instead IStartupFileProvider.RenderStartupFile writes them
        ' into ServerDescription.json just before launch (Decision D3,
        ' closing the old D2 allocator gap). Single-ownership rule:
        ' these two keys are NOT also in the file-editor schema.
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "UseDirectConnection",
                .Label = "Direct connection (no UPnP)",
                .Description = "ON (recommended for a managed host): the server binds the one fixed port below and does NOT use UPnP NAT punch-through. OFF: the server brokers connections through the Windrose vendor service and punches NAT via UPnP, opening ports on your router by itself. PowerGSM writes this into ServerDescription.json at launch.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "DirectConnectionServerPort",
                .Label = "Direct connection port (TCP+UDP)",
                .Description = "The single port the server binds when Direct connection is ON. Forward this one port (both TCP and UDP) on your router. PowerGSM allocates and clash-checks it across all instances on the node. Ignored when Direct connection is OFF (the game's own default is -1 = unset).",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "7777",
                .MinValue = 1024,
                .MaxValue = 65535,
                .IsPort = True
            }
        }
    End Function

    ' ============================================================
    '  Crash handling
    '
    '  Standard policy delegation, identical shape to the other
    '  plugins. Exit-code-based detection is all Slice 1 needs;
    '  log-pattern crash markers wait for Slice 4 since the R5
    '  engine's exact fatal strings aren't confirmed yet.
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
        ' Slice 4 — Manager-side parser for live join/leave History +
        ' notifications, mirroring Conan/LO/Factorio. This is the half
        ' that feeds PlayerActivity (History rows) and PlayerJoined/
        ' PlayerLeft notifications; GetLogParseRules below is the
        ' node-side half that feeds the authoritative /players list +
        ' server-state. BOTH are required and are not redundant: the
        ' node rules keep /players correct even while the Manager is
        ' offline ("Node executes"), and this parser turns the same
        ' lines into live History Manager-side. Windrose shipped with
        ' only the node half (CreateLogParser = Nothing) through Slice
        ' 1; that is why join/leave never reached History until now.
        Return New WindroseLogParser()
    End Function

    Public Function GetLogSources(config As InstanceConfig) As IReadOnlyList(Of ILogSource) Implements IGamePlugin.GetLogSources
        ' Log DIRECTORY confirmed from a live run (the GameInstance
        ' dump logs `LogDir = .../R5/Saved/Logs`). The filename is
        ' assumed R5.log — UE names the log after the project and
        ' everything in this build is "R5" (R5GameInstance, R5.cpp,
        ' /Game maps) — but the exact filename in that dir is still
        ' worth confirming. If it's wrong the tailer just finds
        ' nothing; the server still runs and lifecycle tracks.
        '
        ' Engine confirmed Unreal Engine 5.6.1 (project "R5"), so
        ' the Conan/LO UE log conventions apply: LogNet / LogGameMode
        ' / LogWorld plus R5-specific R5LogNet / R5LogCoopProxy.
        ' Player join/leave parsing (Slice 4) is DONE — see
        ' WindroseLogParser + GetLogParseRules.
        '
        ' Declaring a file source also nudges the node toward a
        ' hidden-console direct spawn, keeping AttachConsole-based
        ' graceful shutdown reachable.
        Return New ILogSource() {
            New FileLogSource("r5", "{InstallPath}/R5/Saved/Logs/R5.log")
        }
    End Function

    Public Function GetLogParseRules() As IReadOnlyList(Of LogParseRule) Implements IGamePlugin.GetLogParseRules
        ' Slice 4 — player tracking + server-state surface, verified
        ' against a real "Windrose R5.log" (UE5.6.1 / project R5).
        '
        ' Player identity keys on AccountId, a 32-char HEX token, so
        ' captures use [0-9A-Fa-f]+ (NOT \d+). It maps to CharacterId
        ' (NOT PlatformUserId): EventStore's players table is keyed by
        ' character_id and PersistPlayer no-ops on an empty CharacterId,
        ' so a pid-only session is tracked live but never persisted.
        ' Co-op has no separate per-character id -- the account IS the
        ' stable identity, so it takes the CharacterId slot.
        ' The connect line (VerifyUeCredentials) carries AccountId +
        ' IP together on one timestamped line, so the session binds
        ' at connect with no correlation buffer needed; the roster
        ' dump later enriches it with the account Name.
        '
        ' Server-state surface is assembled from three startup lines
        ' plus one shutdown line. EventStore's TileLoaded handler
        ' only writes non-empty capture fields, so the map-line and
        ' island-line compose into one surface without clobbering
        ' each other. Custom_* groups are harvested regardless of
        ' Kind (HarvestCustomFields).
        '
        ' No ServerStateChange rules: process liveness already comes
        ' from the node's ProcessManager, and Windrose co-op has no
        ' server-level MatchState string, so a bare ServerStateChange
        ' would only bump a timestamp. The richer signal lives in the
        ' TileLoaded + Custom_* surface below.
        '
        ' Named groups built via concat ("(?<" & "X" & ">") to defeat
        ' any tooling layer that lowercases a literal (?<Name> -- the
        ' same guard Conan uses. Custom_ groups need it too:
        ' HarvestCustomFields does an Ordinal StartsWith("Custom_"),
        ' which a lowercased name would break.

        Dim gDisplayName = "(?<" & "DisplayName" & ">"
        Dim gCharacterId = "(?<" & "CharacterId" & ">"
        Dim gRemoteAddress = "(?<" & "RemoteAddress" & ">"
        Dim gMapPath = "(?<" & "MapPath" & ">"
        Dim gTileName = "(?<" & "TileName" & ">"
        Dim gTileId = "(?<" & "TileId" & ">"
        Dim gMessage = "(?<" & "Message" & ">"
        Dim gTickRate = "(?<" & "Custom_MaxTickRate" & ">"
        Dim gListenPort = "(?<" & "Custom_ListenPort" & ">"
        Dim gShutdown = "(?<" & "Custom_ShutdownReason" & ">"

        Return New LogParseRule() {
            New LogParseRule With {
                .Name = "Player Connect (VerifyUeCredentials -> AccountId + IP)",
                .Kind = ParsedEventKind.PlayerJoin,
                .Pattern = "VerifyUeCredentials\s+UE account verified\. AccountId '" & gCharacterId & "[0-9A-Fa-f]+)' verified on Prelogin\. Address " & gRemoteAddress & "[\d.]+)"
            },
            New LogParseRule With {
                .Name = "Player Identity (roster dump -> Name + AccountId + IP)",
                .Kind = ParsedEventKind.PlayerIdentity,
                .Pattern = "\d+\. Name '" & gDisplayName & "[^']*)'\. AccountId '" & gCharacterId & "[0-9A-Fa-f]+)'\. State '[^']*'\. NetAddress '" & gRemoteAddress & "[^']*)'"
            },
            New LogParseRule With {
                .Name = "Player Leave (OnAccountFarewell -> AccountId + reason)",
                .Kind = ParsedEventKind.PlayerLeave,
                .Pattern = "OnAccountFarewell\s+Account farewell received\. AccountId " & gCharacterId & "[0-9A-Fa-f]+)\. Reason '" & gMessage & "[^']*)'"
            },
            New LogParseRule With {
                .Name = "Player Leave (MoveAccountToListOfDisconnected -> AccountId, hard-drop catch-all)",
                .Kind = ParsedEventKind.PlayerLeave,
                .Pattern = "MoveAccountToListOfDisconnected\s+Account disconnected\. AccountId " & gCharacterId & "[0-9A-Fa-f]+)"
            },
            New LogParseRule With {
                .Name = "Server Ready (world up for play -> MapPath + TileName + tick rate, excludes Lobby)",
                .Kind = ParsedEventKind.TileLoaded,
                .Pattern = "LogWorld: Bringing World " & gMapPath & "/Game/Maps/(?!Lobby/)[^ .]+\." & gTileName & "[^ ]+)) up for play \(max tick rate " & gTickRate & "\d+)\)"
            },
            New LogParseRule With {
                .Name = "World Identity (server initialized -> IslandId as TileId)",
                .Kind = ParsedEventKind.TileLoaded,
                .Pattern = "UR5CoopProxyServer::Init\s+Server initialized\. CurrentIslandId " & gTileId & "[0-9A-Fa-f]+)"
            },
            New LogParseRule With {
                .Name = "Listen Port (IpNetDriver -> actual bound port)",
                .Kind = ParsedEventKind.Custom,
                .Pattern = "IpNetDriver listening on port " & gListenPort & "\d+)"
            },
            New LogParseRule With {
                .Name = "Shutdown Reason (engine exit requested, ignores redundant follow-up)",
                .Kind = ParsedEventKind.Custom,
                .Pattern = "LogCore: Engine exit requested \(reason: " & gShutdown & "(?!EngineExit\(\) was called)[^)]+)\)"
            }
        }
    End Function

    ' ============================================================
    '  RCON
    ' ============================================================

    Public Function GetRconProtocol() As RconProtocol? Implements IGamePlugin.GetRconProtocol
        ' Windrose exposes no RCON surface.
        Return Nothing
    End Function

    ' ============================================================
    '  Mods
    ' ============================================================

    Public Function CreateModManager() As IModManager Implements IGamePlugin.CreateModManager
        ' A community server-side mod framework (Windrose+) exists,
        ' but server-side mod management is out of scope for now.
        Return Nothing
    End Function

    ' ============================================================
    '  IInstallationNoticeProvider
    ' ============================================================

    Public Function GetPreInstallNotices() As IReadOnlyList(Of InstallationNotice) Implements IInstallationNoticeProvider.GetPreInstallNotices
        Return New InstallationNotice() {
            New InstallationNotice With {
                .Severity = NoticeSeverity.Warning,
                .Title = "Windows nodes only",
                .Body = "Windrose ships no native Linux dedicated-server binary. Install on a Linux node will fail at launch. If you need Linux hosting, run a Windows VM."
            },
            New InstallationNotice With {
                .Severity = NoticeSeverity.Warning,
                .Title = "Networking defaults to UPnP NAT punch-through until config editing lands",
                .Body = "Out of the box Windrose uses ICE/P2P with UPnP NAT punch-through — the server opens ports on your router by itself, and players join with an invite code. Operator-controlled networking (one fixed port, no UPnP) requires editing ServerDescription.json, which arrives in the next slice of this plugin. Until then, don't expose this server to the internet if you can't allow UPnP auto-port-opening."
            },
            New InstallationNotice With {
                .Severity = NoticeSeverity.Information,
                .Title = "Config lives in JSON files — edit while stopped",
                .Body = "Windrose has no launch-argument settings. The server reads ServerDescription.json (server-wide) and per-world WorldDescription.json files, both auto-created on first launch. Always edit them with the server fully stopped — the server may overwrite fields on startup. Structured editors for these arrive in later slices; for now, start once, stop, then edit the generated files directly."
            }
        }
    End Function

    ' ============================================================
    '  IPrerequisiteProvider
    '
    '  Defensive declaration: R5 is an Unreal-derived Windows
    '  engine, and those link the Microsoft VC++ 2015-2022 x64
    '  runtime. A host missing it typically fails to launch with a
    '  silent loader error (STATUS_DLL_NOT_FOUND, -1073741515)
    '  before any log appears — surfacing a pre-install notice
    '  saves the operator a confusing silent failure after a long
    '  download. Not confirmed for Windrose specifically (no
    '  _CommonRedist folder observed in the depot) — VERIFY and
    '  drop this if it turns out redundant.
    ' ============================================================

    Public Function GetRequiredPrerequisites() As IReadOnlyList(Of String) Implements IPrerequisiteProvider.GetRequiredPrerequisites
        Return New String() {"vcredist-2015-2022-x64"}
    End Function

    ' ============================================================
    '  IInstanceFileEditorProvider — ServerDescription.json editor
    '
    '  SLICE 2. Windrose's whole config surface is JSON files (no
    '  launch args), so this structured editor IS how an operator
    '  configures the server. One editor for the server-wide
    '  ServerDescription.json; per-world WorldDescription.json
    '  editing is Slice 3 (it needs the R5WorldDescriptionUpdater.exe
    '  post-write run and the escaped-tag-key WorldSettings mapping).
    '
    '  Confirmed from a live run: the file lives at
    '  R5\ServerDescription.json and the editable fields sit one
    '  level down under "ServerDescription_Persistent". The top-
    '  level Version/DeploymentId, the read-only PersistentServerId,
    '  WorldIslandId (Slice 3 owns it), the advanced P2p* /
    '  DirectConnection*Address fields, and
    '  CanLaunchMultipleServerInstances all round-trip UNCHANGED via
    '  JsonNode's preserve-existing-tree behaviour — we only mutate
    '  the keys this schema names.
    '
    '  Edit-while-stopped: the server rewrites this file on startup,
    '  so edits only stick when the server is stopped (the pre-
    '  install notice says so). The Manager writes the file via the
    '  /files endpoint; the server reads it on next launch.
    '
    '  Networking is NOT in this editor (Decision D3, closing the
    '  old D2 allocator gap). UseDirectConnection +
    '  DirectConnectionServerPort live in GetInstanceConfigSchema
    '  (the Configuration tab) so the node port allocator sees and
    '  clash-checks the port; IStartupFileProvider.RenderStartupFile
    '  writes both into ServerDescription_Persistent just before
    '  launch. Single-ownership rule: this editor must not also
    '  expose those two keys, or the start-time render would revert
    '  the operator's edits. The editor owns only the descriptive
    '  fields below; the render preserves UseDirectConnection /
    '  DirectConnectionServerPort untouched here (round-tripped from
    '  the existing file text).
    '
    '  IsPasswordProtected is DERIVED, not exposed: the docs warn a
    '  mismatch between IsPasswordProtected and a (non-)empty
    '  Password may cause unexpected behaviour, so we surface only
    '  Password and set IsPasswordProtected = (Password <> "") on
    '  write.
    ' ============================================================

    Private Const ServerDescEditorKey As String = "server-description"
    Private Const ServerDescRelativePath As String = "R5/ServerDescription.json"
    Private Const PersistentSectionKey As String = "ServerDescription_Persistent"

    ' Region dropdown sentinel for "let the server pick by latency"
    ' (stored as an empty UserSelectedRegion in the file).
    Private Const RegionAuto As String = "Auto (best latency)"

    Public Function GetInstanceFileEditors(config As InstanceConfig) _
            As IReadOnlyList(Of InstanceFileEditor) _
            Implements IInstanceFileEditorProvider.GetInstanceFileEditors
        Return New InstanceFileEditor() {
            New InstanceFileEditor With {
                .Key = ServerDescEditorKey,
                .TabTitle = "Server Settings",
                .RelativePath = ServerDescRelativePath,
                .Schema = BuildServerDescriptionSchema()
            }
        }
    End Function

    Private Shared Function BuildServerDescriptionSchema() As IReadOnlyList(Of ConfigFieldDescriptor)
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "ServerName",
                .Label = "Server name",
                .Description = "Friendly name to tell your servers apart. Edit with the server STOPPED — it rewrites this file on startup.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "PowerGSM Windrose Server"
            },
            New ConfigFieldDescriptor With {
                .Key = "InviteCode",
                .Label = "Invite code",
                .Description = "Code players paste to find the server (Play -> Connect to Server). At least 6 characters, letters and digits only, case-sensitive. The server generates one on first launch; leave blank here to keep it, or set a memorable one.",
                .FieldType = ConfigFieldType.Text,
                .ValidationRegex = "^[0-9A-Za-z]{6,}$"
            },
            New ConfigFieldDescriptor With {
                .Key = "Password",
                .Label = "Server password",
                .Description = "Password players must enter to join. Leave blank for an open server. PowerGSM sets the matching IsPasswordProtected flag for you.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True
            },
            New ConfigFieldDescriptor With {
                .Key = "MaxPlayerCount",
                .Label = "Max players",
                .Description = "Maximum simultaneous players. Windrose co-op caps at 8.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "8",
                .MinValue = 1,
                .MaxValue = 8
            },
            New ConfigFieldDescriptor With {
                .Key = "UserSelectedRegion",
                .Label = "Connection region",
                .Description = "Which Windrose Connection-Service region the server registers with. 'Auto' lets the server pick the lowest-latency region. EU also covers North America.",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {RegionAuto, "EU", "SEA", "CIS"},
                .DefaultValue = RegionAuto
            },
            New ConfigFieldDescriptor With {
                .Key = "AutoLoadLatestBackupIfHasBroken",
                .Label = "Auto-restore from backup if save is broken",
                .Description = "On launch, if the world save is detected as broken, restore the latest backup automatically. Recommended on.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            }
        }
    End Function

    Public Function ReadFileToValues(editorKey As String, fileText As String) _
            As Dictionary(Of String, String) _
            Implements IInstanceFileEditorProvider.ReadFileToValues

        Dim values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        If String.IsNullOrWhiteSpace(fileText) Then Return values

        Dim root As JsonNode = Nothing
        Try
            root = JsonNode.Parse(fileText)
        Catch
            Return values  ' malformed -> schema defaults take over
        End Try
        If root Is Nothing Then Return values

        Dim persistent = TryCast(root(PersistentSectionKey), JsonObject)
        If persistent Is Nothing Then Return values

        values("ServerName") = ReadString(persistent, "ServerName")
        values("InviteCode") = ReadString(persistent, "InviteCode")
        values("Password") = ReadString(persistent, "Password")
        values("MaxPlayerCount") = ReadInt(persistent, "MaxPlayerCount", 8).ToString()
        values("UserSelectedRegion") = RegionFromFile(ReadString(persistent, "UserSelectedRegion"))
        values("AutoLoadLatestBackupIfHasBroken") = ReadBool(persistent, "AutoLoadLatestBackupIfHasBroken", True).ToString().ToLower()

        Return values
    End Function

    Public Function WriteValuesToFile(editorKey As String,
                                       values As Dictionary(Of String, String),
                                       existingText As String) As String _
            Implements IInstanceFileEditorProvider.WriteValuesToFile

        ' Preserve the whole existing tree (Version, DeploymentId,
        ' PersistentServerId, WorldIslandId, P2p*/Address fields,
        ' CanLaunchMultipleServerInstances, anything unknown) and
        ' mutate only the schema keys inside ServerDescription_Persistent.
        Dim root As JsonObject = Nothing
        If Not String.IsNullOrWhiteSpace(existingText) Then
            Try
                root = TryCast(JsonNode.Parse(existingText), JsonObject)
            Catch
            End Try
        End If
        If root Is Nothing Then root = New JsonObject()

        Dim persistent = TryCast(root(PersistentSectionKey), JsonObject)
        If persistent Is Nothing Then
            persistent = New JsonObject()
            root(PersistentSectionKey) = persistent
        End If

        SetString(persistent, "ServerName", values, "ServerName", "")

        ' InviteCode: only overwrite when the operator supplied one.
        ' Blank means "keep the server-generated code" — writing ""
        ' would leave the server with no valid invite code.
        Dim invite = GetField(values, "InviteCode")
        If Not String.IsNullOrEmpty(invite) Then
            persistent("InviteCode") = JsonValue.Create(invite)
        End If

        Dim pw = GetField(values, "Password")
        persistent("Password") = JsonValue.Create(pw)
        ' Derive the flag so it can never disagree with Password.
        persistent("IsPasswordProtected") = JsonValue.Create(Not String.IsNullOrEmpty(pw))

        SetInt(persistent, "MaxPlayerCount", values, "MaxPlayerCount", 8)
        persistent("UserSelectedRegion") = JsonValue.Create(RegionToFile(GetField(values, "UserSelectedRegion")))

        ' UseDirectConnection / DirectConnectionServerPort are NOT
        ' touched here — they're owned by the Configuration schema and
        ' rendered into this file at launch by RenderStartupFile. They
        ' round-trip unchanged via the preserved existing tree.
        SetBool(persistent, "AutoLoadLatestBackupIfHasBroken", values, "AutoLoadLatestBackupIfHasBroken", True)

        Dim opts As New JsonSerializerOptions With {.WriteIndented = True}
        Return root.ToJsonString(opts)
    End Function

    ' Region dropdown <-> file value. The file stores "" for "let
    ' the server choose"; the dropdown shows a friendly sentinel.
    Private Shared Function RegionFromFile(fileValue As String) As String
        If String.IsNullOrEmpty(fileValue) Then Return RegionAuto
        Return fileValue
    End Function

    Private Shared Function RegionToFile(dropdownValue As String) As String
        If String.IsNullOrEmpty(dropdownValue) OrElse dropdownValue = RegionAuto Then Return ""
        Return dropdownValue
    End Function

    ' ---- JSON read/write helpers (same shape as the other plugins) ----

    Private Shared Function ReadString(node As JsonNode, key As String) As String
        If node Is Nothing Then Return ""
        Dim child = node(key)
        If child Is Nothing Then Return ""
        Try
            Return If(child.GetValue(Of String)(), "")
        Catch
            Return ""
        End Try
    End Function

    Private Shared Function ReadInt(node As JsonNode, key As String, defaultValue As Integer) As Integer
        If node Is Nothing Then Return defaultValue
        Dim child = node(key)
        If child Is Nothing Then Return defaultValue
        Try
            Return child.GetValue(Of Integer)()
        Catch
            Return defaultValue
        End Try
    End Function

    Private Shared Function ReadBool(node As JsonNode, key As String, defaultValue As Boolean) As Boolean
        If node Is Nothing Then Return defaultValue
        Dim child = node(key)
        If child Is Nothing Then Return defaultValue
        Try
            Return child.GetValue(Of Boolean)()
        Catch
            Return defaultValue
        End Try
    End Function

    Private Shared Sub SetString(obj As JsonObject, jsonKey As String,
                                  values As Dictionary(Of String, String),
                                  formKey As String, fallback As String)
        Dim raw = GetField(values, formKey)
        If String.IsNullOrEmpty(raw) Then raw = fallback
        obj(jsonKey) = JsonValue.Create(raw)
    End Sub

    Private Shared Sub SetInt(obj As JsonObject, jsonKey As String,
                               values As Dictionary(Of String, String),
                               formKey As String, fallback As Integer)
        Dim raw = GetField(values, formKey)
        Dim parsed As Integer
        If Not Integer.TryParse(raw, parsed) Then parsed = fallback
        obj(jsonKey) = JsonValue.Create(parsed)
    End Sub

    Private Shared Sub SetBool(obj As JsonObject, jsonKey As String,
                                values As Dictionary(Of String, String),
                                formKey As String, fallback As Boolean)
        Dim raw = GetField(values, formKey)
        Dim parsed As Boolean
        If Not Boolean.TryParse(raw, parsed) Then parsed = fallback
        obj(jsonKey) = JsonValue.Create(parsed)
    End Sub

    ' ============================================================
    '  IStartupFileProvider — port + direct-mode render at launch
    '
    '  Closes Decision D2: the direct-connection port lives in the
    '  Configuration schema (so the node allocator manages it) but
    '  the exe takes no launch args, so it can't reach the server on
    '  the command line. Instead, just before launch the Manager
    '  calls RenderStartupFile and we write UseDirectConnection +
    '  DirectConnectionServerPort straight into
    '  ServerDescription_Persistent. Best-effort on the Manager side:
    '  a write failure logs a warning and the launch proceeds with
    '  the file's last values.
    ' ============================================================

    Public Function GetStartupFiles(instanceConfig As InstanceConfig) _
            As IReadOnlyList(Of String) _
            Implements IStartupFileProvider.GetStartupFiles
        Return New String() {ServerDescRelativePath}
    End Function

    Public Function RenderStartupFile(relativePath As String,
                                       instanceConfig As InstanceConfig,
                                       existingText As String) As String _
            Implements IStartupFileProvider.RenderStartupFile

        ' Only our one file.
        If Not String.Equals(relativePath, ServerDescRelativePath, StringComparison.OrdinalIgnoreCase) Then
            Return Nothing
        End If

        ' The server creates ServerDescription.json on first launch.
        ' If it doesn't exist yet, don't fabricate a partial file
        ' (we'd be missing PersistentServerId / DeploymentId / the
        ' P2p* tree); skip and let the server write it. The port
        ' applies from the second launch onward.
        If String.IsNullOrWhiteSpace(existingText) Then Return Nothing

        Dim root As JsonObject = Nothing
        Try
            root = TryCast(JsonNode.Parse(existingText), JsonObject)
        Catch
            Return Nothing  ' malformed — leave it for the server/editor
        End Try
        If root Is Nothing Then Return Nothing

        Dim persistent = TryCast(root(PersistentSectionKey), JsonObject)
        If persistent Is Nothing Then
            persistent = New JsonObject()
            root(PersistentSectionKey) = persistent
        End If

        Dim fields = If(instanceConfig IsNot Nothing, instanceConfig.CustomFields, Nothing)

        ' Direct-mode toggle (default ON per Decision D1).
        Dim directOn As Boolean
        If Not Boolean.TryParse(GetField(fields, "UseDirectConnection"), directOn) Then directOn = True
        persistent("UseDirectConnection") = JsonValue.Create(directOn)

        ' Only stamp the allocated port when direct mode is ON; when
        ' OFF, leave the file's existing port (the game's -1 sentinel)
        ' untouched rather than forcing a value the server ignores.
        If directOn Then
            Dim port As Integer
            If Integer.TryParse(GetField(fields, "DirectConnectionServerPort"), port) AndAlso port > 0 Then
                persistent("DirectConnectionServerPort") = JsonValue.Create(port)
            End If
        End If

        Dim opts As New JsonSerializerOptions With {.WriteIndented = True}
        Return root.ToJsonString(opts)
    End Function

    ' ============================================================
    '  IManagedDirectoriesProvider
    '
    '  Slice 2 surfaces only the log directory (read-only) for
    '  download/debugging. World save data lives under
    '  R5\Saved\SaveProfiles\Default\RocksDB_v2\<ReleaseVersion>\
    '  Worlds\<id>\ (backups under ...RocksDB_v2_Backups\) — but that
    '  tree has a game-version subfolder and the live RocksDB is
    '  touchy, so world/backup management is handled deliberately in
    '  Slice 3 rather than exposed as a raw directory here.
    ' ============================================================

    Public Function GetManagedDirectories(config As InstanceConfig) As IReadOnlyList(Of ManagedDirectory) Implements IManagedDirectoriesProvider.GetManagedDirectories
        Return New ManagedDirectory() {
            New ManagedDirectory With {
                .RelativePath = "R5/Saved/Logs",
                .DisplayName = "Log files",
                .Permissions = DirPermissions.Read
            }
        }
    End Function

    ' ============================================================
    '  ILaunchOptionsProvider
    '
    '  Windrose is UE5.6.1 with -log, so the default direct hidden-
    '  console spawn already works (confirmed live: runs in the
    '  background with no visible window, and AttachConsole + CTRL_C
    '  gives a clean graceful shutdown). So no RequiresConsoleIsolation
    '  and no StdoutIsLog — the file log is authoritative and the
    '  legacy UE log-tailer start delay is fine.
    '
    '  We only raise the graceful-shutdown timeout: on stop the
    '  server flushes the RocksDB world and writes a backup (auto-
    '  backups already run every 60s). Empty worlds shut down in a
    '  second or two, but a populated co-op world needs headroom to
    '  finish the flush before the force-kill fallback. 45s sits
    '  comfortably above the observed clean-shutdown time without
    '  leaving operators staring at a wedged process for long.
    '  Per-instance override still available via a "GracefulTimeoutMs"
    '  custom field.
    ' ============================================================

    Public Function GetLaunchOptions(config As InstanceConfig) As LaunchOptions Implements ILaunchOptionsProvider.GetLaunchOptions
        Return New LaunchOptions With {
            .GracefulShutdownTimeoutMs = 45000
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

End Class

' ============================================================
'  WindroseLogParser — Manager-side ILogParser (Slice 4)
'
'  Verified against real "Windrose R5.log" captures (UE5.6.1,
'  project R5). Windrose is UE-based, so the join line is the
'  same canonical "LogNet: Join succeeded: <name>" that Conan
'  and Last Oasis use. Structure mirrors ConanExilesLogParser.
'
'  Identity wrinkle: the join line carries the in-game NAME but
'  no AccountId; the leave lines (OnAccountFarewell /
'  MoveAccountToListOfDisconnected) carry the AccountId but no
'  name. The roster-dump lines ("N. Name 'X'. AccountId 'Y'.
'  State '...'") carry BOTH and fire at connect, so we harvest
'  an AccountId->Name binding from them and resolve leave names
'  through it. Same shape as Conan's IP->name binding, keyed on
'  AccountId instead of RemoteAddr (Windrose leave lines have no
'  IP to bind on).
'
'  Per-parser state, single-threaded callback per instance — no
'  locking. Numbered regex groups (not named) to match Conan's
'  idiom and sidestep the named-capture tooling quirk.
' ============================================================
Public Class WindroseLogParser
    Implements ILogParser

    Public ReadOnly Property GameId As String = "windrose" Implements ILogParser.GameId

    ' AccountId -> in-game Name, harvested from roster-dump lines.
    ' The leave lines only carry AccountId, so this is how a leave
    ' resolves to a name. Populated well before any leave (roster
    ' fires at connect and on each state transition).
    Private ReadOnly _namesByAccountId As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    ' Roster dump: "  1. Name 'blingity'. AccountId '869F...'. State '...'".
    ' Group 1 = name, group 2 = AccountId (hex).
    Private Shared ReadOnly _rosterRegex As New Regex(
        "Name '([^']*)'\. AccountId '([0-9A-Fa-f]+)'",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)

    ' Voluntary leave (carries a Reason). Group 1 = AccountId.
    Private Shared ReadOnly _farewellRegex As New Regex(
        "OnAccountFarewell\s+Account farewell received\. AccountId ([0-9A-Fa-f]+)",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)

    ' Terminal disconnect (voluntary AND hard drops). Group 1 = AccountId.
    Private Shared ReadOnly _disconnectRegex As New Regex(
        "MoveAccountToListOfDisconnected\s+Account disconnected\. AccountId ([0-9A-Fa-f]+)",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)

    ''' <summary>
    ''' One install hosts one active world (RocksDB is file-locked,
    ''' MaxInstancesPerInstallation = 1), so there's no cross-instance
    ''' session identity the way Last Oasis has tiles/realms. Returning
    ''' Nothing tells downstream code to fall back to
    ''' "{gameId}:{instanceId}", which is the right default.
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

        ' ----- Roster dump: harvest AccountId -> Name -----
        ' Not itself a join/leave event; just keeps the name binding
        ' current so the AccountId-only leave lines can resolve a name.
        Dim rm = _rosterRegex.Match(text)
        If rm.Success Then
            Dim nm = rm.Groups(1).Value
            Dim ac = rm.Groups(2).Value
            If Not String.IsNullOrEmpty(ac) AndAlso Not String.IsNullOrWhiteSpace(nm) Then
                _namesByAccountId(ac) = nm
            End If
            Return ParsedLogEvent.NoMatch
        End If

        ' ----- Player join (Join succeeded: NAME) -----
        ' Canonical UE join line, same one Conan anchors on. Carries
        ' the in-game name directly and fires once when the player
        ' finishes loading into the world. The Manager dedups repeats
        ' via its _activePlayers transition gate.
        If text.Contains("LogNet: Join succeeded:") Then
            Dim playerName = ExtractAfter(text, "Join succeeded: ")
            Return New ParsedLogEvent With {
                .EventType = LogEventType.PlayerJoin,
                .Message = $"Player joined: {playerName}",
                .PlayerInfo = New PlayerInfo With {
                    .PlayerName = playerName,
                    .JoinedAt = line.Timestamp
                }
            }
        End If

        ' ----- Player leave (voluntary farewell, carries Reason) -----
        Dim fm = _farewellRegex.Match(text)
        If fm.Success Then
            Return LeaveFor(fm.Groups(1).Value, removeBinding:=False)
        End If

        ' ----- Player leave (terminal disconnect) -----
        ' Fires for both voluntary and hard drops; the definitive
        ' "gone" line. Drop the name binding here so it doesn't leak
        ' across a later reconnect on the same AccountId.
        Dim dm = _disconnectRegex.Match(text)
        If dm.Success Then
            Return LeaveFor(dm.Groups(1).Value, removeBinding:=True)
        End If

        ' ----- Server ready (game world up for play, excludes Lobby) -----
        If text.Contains("Bringing World /Game/Maps/") AndAlso
           text.Contains("up for play") AndAlso
           Not text.Contains("/Game/Maps/Lobby/") Then
            Return New ParsedLogEvent With {
                .EventType = LogEventType.ServerReady,
                .Message = "Server is ready (world up for play)"
            }
        End If

        ' ----- UE crash markers (same set as Conan / Last Oasis) -----
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

    ''' <summary>
    ''' Build a PlayerLeave event, resolving the in-game name from the
    ''' AccountId binding. A name-less leave (binding missing — e.g.
    ''' Manager reconnected mid-session and never saw the roster line)
    ''' still emits with no PlayerInfo; the Manager's single-player
    ''' attribution heuristic covers that gap, same as Conan.
    ''' </summary>
    Private Function LeaveFor(accountId As String, removeBinding As Boolean) As ParsedLogEvent
        Dim nm As String = Nothing
        If Not String.IsNullOrEmpty(accountId) Then
            _namesByAccountId.TryGetValue(accountId, nm)
            If removeBinding Then _namesByAccountId.Remove(accountId)
        End If
        Dim info As PlayerInfo = Nothing
        If Not String.IsNullOrEmpty(nm) Then
            info = New PlayerInfo With {.PlayerName = nm}
        End If
        Return New ParsedLogEvent With {
            .EventType = LogEventType.PlayerLeave,
            .Message = If(String.IsNullOrEmpty(nm), "Player disconnected", $"Player left: {nm}"),
            .PlayerInfo = info
        }
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

End Class
