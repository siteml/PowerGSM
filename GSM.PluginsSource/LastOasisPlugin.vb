' <plugin id="lastoasis" name="Last Oasis Dedicated Server" version="1.0.0" author="siteml" requiresContracts="1">
' <RequiresContracts: 1>
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text.RegularExpressions
Imports GSM.Plugin

' ============================================================
'  Last Oasis Dedicated Server Plugin
'
'  AppID: 920720 (free dedicated server)
'  Engine: Unreal Engine 4
'  Install: SteamCMD only
'  RCON: Not supported — Last Oasis does not expose a Source
'         RCON listener (or any other admin protocol over the
'         network). In-game admin commands go through the chat
'         system with the -EnableCheats flag instead. PowerGSM
'         used to expose RconPort / RconPassword fields and
'         pass -RconPort= / -RconPassword= to the binary; both
'         were silently ignored by MistServer and removed in
'         Phase 5j-ish cleanup.
'  Mods: None (client-side only)
'
'  Key config fields (realm-level, since Phase 5h — stored on
'  a SharedConfigGroup row, shared across all installations
'  on the same realm):
'    CustomerKey  — realm-wide auth key from MyRealm dashboard
'    ProviderKey  — provider auth key (revocable per provider)
'    RealmName    — human-readable name ("Site's World"); used
'                   in History Source column instead of the
'                   raw numeric realm_id from logs
'
'  Key config fields (installation-level):
'    CustomerKey  — (legacy, still readable for backwards-compat
'                   with pre-5h installs; new installs leave
'                   blank and use the Realm group instead)
'    ProviderKey  — (legacy, same as above)
'    SteamBranch  — beta branch name (blank = default)
'
'  Key config fields (instance-level):
'    Identifier   — unique per instance on this realm
'    Port         — game port (UDP), default 5555
'    QueryPort    — Steam query port, default 27015
'    Slots        — tile slot count, default 5
'    OverrideConnectionAddress — external IP override
' ============================================================

Public Class LastOasisPlugin
    Implements IGamePlugin
    Implements IReadySignalProvider
    Implements ISharedConfigProvider
    Implements ISourceLabelProvider

    Public ReadOnly Property GameId As String = "lastoasis" Implements IGamePlugin.GameId
    Public ReadOnly Property DisplayName As String = "Last Oasis" Implements IGamePlugin.DisplayName

    ' Last Oasis genuinely supports multi-instance off one install:
    ' UE4 dedicated server with per-instance state living entirely
    ' in command-line args (-port, -identifier, -RconPort, etc.) and
    ' a per-instance log file driven by -AbsLog. Returning Nothing
    ' means "no limit" — the Manager will never block creating
    ' another instance against this install.
    Public ReadOnly Property MaxInstancesPerInstallation As Integer? Implements IGamePlugin.MaxInstancesPerInstallation
        Get
            Return Nothing
        End Get
    End Property

    ' ============================================================
    '  IReadySignalProvider — restart coordinator integration
    '
    '  The LO plugin's log parser already emits TileLoaded events
    '  when the server's match state enters LeavingMap (i.e. the
    '  tile has finished pre-loading and is ready to accept
    '  players). The restart coordinator watches for TileLoaded
    '  on the newly-restarted instance and releases the slot so
    '  the next queued instance can begin its stop+start cycle.
    '
    '  Timeout: 5 minutes. LO tile preload on a typical realm
    '  runs 30-90s on warm cache, 2-4min on cold start after an
    '  update. 300s gives cold-start enough headroom without
    '  letting a hung preload hold the queue forever.
    ' ============================================================

    Public Function GetReadyForNextSignal() As ReadySignal Implements IReadySignalProvider.GetReadyForNextSignal
        Return New ReadySignal With {
            .Kind = ReadySignalKind.TileLoaded,
            .MatchValue = Nothing
        }
    End Function

    Public ReadOnly Property DefaultReadyTimeoutSeconds As Integer = 300 Implements IReadySignalProvider.DefaultReadyTimeoutSeconds

    ' ============================================================
    '  Install
    ' ============================================================

    Public Function GetSupportedInstallMethods() As IReadOnlyList(Of InstallMethod) Implements IGamePlugin.GetSupportedInstallMethods
        Return New InstallMethod() {InstallMethod.SteamCmd}
    End Function

    Public Function GetInstallSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetInstallSteps
        Dim steps As New List(Of InstallStep)

        Dim steamStep As New SteamCmdStep()
        steamStep.StepName = "Download Last Oasis Server"
        steamStep.Description = "Download/update via SteamCMD (AppID 920720)"
        steamStep.AppId = 920720
        steamStep.ValidateFiles = True
        steamStep.RequiresLogin = True

        ' Beta branch support
        If config.CustomFields IsNot Nothing Then
            Dim branch = GetField(config.CustomFields, "SteamBranch")
            If Not String.IsNullOrEmpty(branch) Then
                steamStep.BetaBranch = branch
                steamStep.BetaPassword = GetField(config.CustomFields, "SteamBranchPassword")
            End If
        End If

        steps.Add(steamStep)

        ' Steamworks identity hint — Linux-only.
        '
        ' AppID 920720 is the *dedicated-server tool* on Steam;
        ' AppID 903950 is the *Last Oasis game* itself. The server
        ' binary needs to authenticate as the game (903950) so
        ' Steamworks calls (presence, EOS auth, telemetry) align
        ' with the game's app identity rather than the server
        ' tool's. On Windows the server picks up the right
        ' identity through some other mechanism (Steam client /
        ' installed-app registry) and the file isn't required;
        ' per LO's Linux dedicated-server documentation, on Linux
        ' the file IS required — without it the server fails
        ' Steamworks init and never registers with the backend.
        '
        ' Location: NEXT TO THE BINARY, not at the install root.
        ' Last Oasis's official Linux docs say:
        '
        '   "navigate to Mist/Binaries/Linux/ (in the dedicated
        '    server app root folder), and create a file called
        '    steam_appid.txt [...] paste 903950. Save and close."
        '
        ' The Steamworks SDK on Linux resolves steam_appid.txt
        ' relative to the CALLING BINARY's directory (effectively
        ' /proc/self/exe's dirname), not the process working
        ' directory — a Linux-specific divergence from the
        ' SDK's Windows behaviour, where cwd is honoured. An
        ' earlier version of this plugin wrote the file at the
        ' install root assuming cwd-based lookup; the binary
        ' never saw it and reported [AppId: 0] regardless. Even
        ' though the rest of LO's install layout (steamclient.so
        ' at the install root) suggested otherwise, the SDK is
        ' doing two different lookups for two different files.
        '
        ' Skip explicitly on Windows. Unknown platform falls
        ' through to the write branch as a safe default — better
        ' to drop a harmless 6-byte file on an old Windows node
        ' than to leave a Linux node broken because we couldn't
        ' read its platform.
        '
        ' SteamCMD's install order guarantees the parent dir
        ' (Mist/Binaries/Linux/) exists by the time this step
        ' runs — it's where the binary itself was just downloaded.
        If config.Platform <> NodePlatform.Windows Then
            steps.Add(New WriteFileStep With {
                .StepName = "Write steam_appid.txt",
                .Description = "Steamworks identity (903950 = LO game; required on Linux)",
                .RelativePath = "Mist/Binaries/Linux/steam_appid.txt",
                .Content = "903950",
                .OverwriteExisting = True
            })
        End If

        Return steps
    End Function

    Public Function GetUpdateSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetUpdateSteps
        ' Update is the same as install for SteamCMD games
        Return GetInstallSteps(config)
    End Function

    ' ============================================================
    '  Instance
    ' ============================================================

    Public Function GetExecutablePath(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.GetExecutablePath
        ' Pick the right binary set based on the node's OS, which
        ' the manager populated on InstanceConfig.Platform from
        ' /api/version before invoking us. Forward slashes throughout
        ' — both Windows and Linux file APIs accept them, and they
        ' survive the Manager (Windows) → Node (Linux) marshalling
        ' boundary unchanged.
        '
        ' Windows builds: MistServer-Win64-Shipping.exe is the
        '   shipping-config build; MistServer.exe is the development
        '   or test-config name. The probe loop tries them in order.
        ' Linux builds: MistServer-Linux-Shipping is the standard
        '   UE4 Linux dedicated-server binary name (no extension).
        '   MistServer is the dev/test alternate. Some builds also
        '   ship a .sh launcher that sets LD_LIBRARY_PATH — included
        '   as a last-resort fallback so the user has a path that
        '   works even if the direct binary names don't match.
        '
        ' NodePlatform.Unknown (older nodes that don't surface the
        ' field) emits the union so the manager's probe-and-remember
        ' loop can still find the right one.
        Select Case config.Platform
            Case NodePlatform.Linux
                Return New String() {
                    "Mist/Binaries/Linux/MistServer-Linux-Shipping",
                    "Mist/Binaries/Linux/MistServer",
                    "MistServer.sh"
                }
            Case NodePlatform.Windows
                Return New String() {
                    "Mist/Binaries/Win64/MistServer-Win64-Shipping.exe",
                    "Mist/Binaries/Win64/MistServer.exe"
                }
            Case Else
                Return New String() {
                    "Mist/Binaries/Win64/MistServer-Win64-Shipping.exe",
                    "Mist/Binaries/Win64/MistServer.exe",
                    "Mist/Binaries/Linux/MistServer-Linux-Shipping",
                    "Mist/Binaries/Linux/MistServer",
                    "MistServer.sh"
                }
        End Select
    End Function

    Public Function BuildLaunchArguments(config As InstanceConfig) As String Implements IGamePlugin.BuildLaunchArguments
        Dim args As New List(Of String)

        ' Project name as positional arg 0 — Linux only.
        '
        ' UE4 dedicated servers take the project name as their
        ' first positional argument. On Windows the binary name
        ' itself encodes it (MistServer-Win64-Shipping.exe → UE4's
        ' loader derives "Mist" from the prefix), so passing it on
        ' the command line is redundant. On Linux, the build's
        ' command-line parser does NOT do that name-derived lookup
        ' and falls through to "no project loaded" if the argument
        ' is missing. Visible symptom: Mist/Config/DefaultEngine.ini
        ' is never read, so the
        '   [OnlineSubsystemSteam]
        '   SteamDedicatedServerGameAppId=903950
        ' entry is invisible to UE4's OnlineSubsystemSteam. UE4
        ' then logs "[AppId: 0] Game Server API initialized 0",
        ' Steamworks Game Server init fails, and connecting clients
        ' see "authentication failed" because there's no live Game
        ' Server session for Steam to authenticate them against.
        '
        ' LO's own MistServer.sh launcher passes "Mist" the same
        ' way (line 5 of the script):
        '   "$UE4_PROJECT_ROOT/Mist/Binaries/Linux/MistServer-Linux-Shipping" Mist "$@"
        ' so we're matching what their docs assume.
        '
        ' Must come BEFORE any flag args — UE4's parser treats the
        ' first non-dash token as the project name and everything
        ' after it as switches/options. Putting -force_steamclient_link
        ' first and Mist second would parse Mist as a value for the
        ' previous flag instead.
        '
        ' Note that steam_appid.txt (written by the install step
        ' above) and this argument feed two DIFFERENT Steam-AppId
        ' mechanisms: steam_appid.txt is what the underlying
        ' Steamworks C SDK reads at SteamAPI_Init time;
        ' SteamDedicatedServerGameAppId is what UE4's
        ' OnlineSubsystemSteam reads from the loaded project's
        ' DefaultEngine.ini. Both have to work for clients to
        ' authenticate, which is why dropping just the .txt file
        ' wasn't enough.
        If config.Platform = NodePlatform.Linux Then
            args.Add("Mist")
        End If

        ' Required backend flags
        args.Add("-force_steamclient_link")
        args.Add("-messaging")
        args.Add("-NoLiveServer")
        args.Add("-backendapiurloverride=""backend-production.last-oasis.com""")

        ' Admin chat commands (cheats). Required for in-game admin
        ' operations issued through chat — kick, ban, give, teleport,
        ' etc. Without this flag the command parser is disabled and
        ' admin chat lines are silently ignored.
        args.Add("-EnableCheats")

        ' Realm authentication (installation-level, shared across instances)
        Dim customerKey = GetField(config.CustomFields, "CustomerKey")
        Dim providerKey = GetField(config.CustomFields, "ProviderKey")
        If Not String.IsNullOrEmpty(customerKey) Then
            args.Add($"-CustomerKey={customerKey}")
        End If
        If Not String.IsNullOrEmpty(providerKey) Then
            args.Add($"-ProviderKey={providerKey}")
        End If

        ' Instance identifier — unique per server on this realm
        Dim identifier = GetField(config.CustomFields, "Identifier")
        If String.IsNullOrEmpty(identifier) Then
            identifier = config.InstanceId
        End If
        args.Add($"-identifier={identifier}")

        ' Ports
        Dim port = GetFieldInt(config.CustomFields, "Port", 5555)
        Dim queryPort = GetFieldInt(config.CustomFields, "QueryPort", 27015)
        args.Add($"-port={port}")
        args.Add($"-QueryPort={queryPort}")

        ' RCON: Last Oasis doesn't support RCON — see header
        ' comment for the why. No -RconPort= / -RconPassword=
        ' args added to the command line; the binary ignores
        ' those switches in any case. The InstanceManager may
        ' still propagate stale RconPort / RconPassword values
        ' from legacy ConfigJson into the Node's spawn request
        ' (its resolve block reads customFields unconditionally),
        ' but with no listener on the game side those values are
        ' dead-letter — any Manager-initiated SendRconCommand
        ' would just fail to connect. A future cleanup could gate
        ' the InstanceManager RCON-resolve block on
        ' GetRconProtocol().HasValue so even the spawn request
        ' stays clean, but that's a wider change than the
        ' user-facing schema cleanup here.

        ' Slots
        Dim slots = GetFieldInt(config.CustomFields, "Slots", 5)
        args.Add($"-slots={slots}")

        ' Connection address override — omit flag entirely when blank
        Dim overrideAddr = GetField(config.CustomFields, "OverrideConnectionAddress")
        If Not String.IsNullOrEmpty(overrideAddr) Then
            args.Add($"-OverrideConnectionAddress={overrideAddr}")
        End If

        ' UE4 headless flags
        ' Note: do NOT add -nullrhi / -nosound / -nographicsadapter here.
        ' Those keep UE4's -log console visible permanently because
        ' there's no main window to take over after engine init.

        ' -log is REQUIRED for graceful shutdown.
        '
        ' Without -log, UE4's console-aware shutdown path is never wired
        ' up: SetConsoleCtrlHandler is not installed against the inherited
        ' console, and CTRL_C_EVENT delivered via AttachConsole +
        ' GenerateConsoleCtrlEvent goes to the OS default handler instead
        ' of triggering RequestEngineExit. Symptom in the LO log is the
        ' total absence of any "LogCore: Engine exit requested (reason:
        ' ConsoleCtrl RequestExit)" line on stop — the file just ends at
        ' the last init line because the engine never sees the signal.
        '
        ' This is independent of CREATE_NEW_CONSOLE: giving the process
        ' a console isn't enough; UE4 only arms its handler when -log
        ' tells it logging-to-console mode was requested.
        '
        ' WindowsGSM passes -log unconditionally for this exact reason
        ' (see their LastOasis plugin), and tolerates the resulting
        ' visible console window. PowerGSM's spawn path uses
        ' STARTF_USESHOWWINDOW + SW_HIDE on the parent's STARTUPINFO,
        ' which keeps the window hidden when UE4's -log code calls
        ' AllocConsole + ShowWindow during init — i.e. we get the
        ' handler installation without the visible window.
        args.Add("-log")

        ' Mirror server state and persistence events to stdout. UE4
        ' suppresses LogNet from stdout for server builds regardless of
        ' LogCmds — for player connect/disconnect we rely on tailing
        ' the log file instead.
        args.Add("-LogCmds=""Log LogGame Display, Log LogGameMode Display, Log LogGameState Display, Log LogWorld Display, Log LogNet Verbose, Log LogPersistence Verbose""")

        ' Write a per-instance log file so each instance sharing this
        ' install folder gets its own log we can tail independently.
        ' Forward slashes throughout so the path is valid on both
        ' Windows (UE4 accepts /) and Linux (where \ is a literal
        ' filename character, not a separator). config.WorkingDirectory
        ' is the install root as the node sees it — already in the
        ' node's native path style by the time it reaches us.
        Dim absLogPath = $"{config.WorkingDirectory}/Mist/Saved/Logs/{config.InstanceId}.log"
        args.Add($"-AbsLog=""{absLogPath}""")

        Return String.Join(" ", args)
    End Function

    Public Function ValidateConfig(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.ValidateConfig
        Dim errors As New List(Of String)

        If String.IsNullOrEmpty(GetField(config.CustomFields, "CustomerKey")) Then
            errors.Add("CustomerKey is required. Get it from the MyRealm dashboard.")
        End If
        If String.IsNullOrEmpty(GetField(config.CustomFields, "ProviderKey")) Then
            errors.Add("ProviderKey is required. Get it from the MyRealm dashboard.")
        End If
        If String.IsNullOrEmpty(GetField(config.CustomFields, "Identifier")) Then
            errors.Add("Identifier is required. Must be unique per instance on this realm.")
        End If

        Return errors
    End Function

    ' ============================================================
    '  Config schema
    ' ============================================================

    Public Function GetInstallConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstallConfigSchema
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "CustomerKey",
                .Label = "Customer Key",
                .Description = "Realm-wide authentication key from MyRealm dashboard.",
                .FieldType = ConfigFieldType.Text,
                .IsRequired = True
            },
            New ConfigFieldDescriptor With {
                .Key = "ProviderKey",
                .Label = "Provider Key",
                .Description = "Provider authentication key. Revoke to lock out servers using this key.",
                .FieldType = ConfigFieldType.Text,
                .IsRequired = True
            },
            New ConfigFieldDescriptor With {
                .Key = "SteamBranch",
                .Label = "Steam beta branch",
                .Description = "Beta branch name. Leave blank for the default release branch.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = ""
            },
            New ConfigFieldDescriptor With {
                .Key = "SteamBranchPassword",
                .Label = "Branch password",
                .Description = "Password for the Steam beta branch, if required.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True
            }
        }
    End Function

    Public Function GetInstanceConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstanceConfigSchema
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "Identifier",
                .Label = "Server Identifier",
                .Description = "Unique identifier for this instance on the realm. Visible in MyRealm dashboard.",
                .FieldType = ConfigFieldType.Text,
                .IsRequired = True
            },
            New ConfigFieldDescriptor With {
                .Key = "CustomerKey",
                .Label = "Customer Key (override)",
                .Description = "Leave blank to use the installation-level CustomerKey. Set here to override for this instance only.",
                .FieldType = ConfigFieldType.Text
            },
            New ConfigFieldDescriptor With {
                .Key = "ProviderKey",
                .Label = "Provider Key (override)",
                .Description = "Leave blank to use the installation-level ProviderKey. Set here to override for this instance only.",
                .FieldType = ConfigFieldType.Text
            },
            New ConfigFieldDescriptor With {
                .Key = "ServerBinary",
                .Label = "Server Binary",
                .Description = "Which executable to launch. The name varies between Last Oasis builds.",
                .FieldType = ConfigFieldType.[Enum],
                .DefaultValue = "MistServer-Win64-Shipping.exe",
                .EnumValues = New List(Of String) From {
                    "MistServer-Win64-Shipping.exe",
                    "MistServer.exe"
                }
            },
            New ConfigFieldDescriptor With {
                .Key = "Port",
                .Label = "Game Port (UDP)",
                .Description = "Must be unique per instance on this node.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "5555",
                .MinValue = 1024,
                .MaxValue = 65535,
                .IsPort = True
            },
            New ConfigFieldDescriptor With {
                .Key = "QueryPort",
                .Label = "Query Port",
                .Description = "Steam query port. Must be unique per instance on this node.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "27015",
                .MinValue = 1024,
                .MaxValue = 65535,
                .IsPort = True
            },
            New ConfigFieldDescriptor With {
                .Key = "Slots",
                .Label = "Tile Slots",
                .Description = "Number of tile slots. Max 100 per official docs.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "5",
                .MinValue = 1,
                .MaxValue = 100
            },
            New ConfigFieldDescriptor With {
                .Key = "OverrideConnectionAddress",
                .Label = "Override Connection Address",
                .Description = "External IP for player connections. Blank = server auto-detects.",
                .FieldType = ConfigFieldType.Text
            }
        }
    End Function

    ' ============================================================
    '  ISharedConfigProvider (Phase 5h)
    '
    '  Declares Last Oasis's "Realm" concept. A Realm holds the
    '  fields that are realm-scoped rather than per-install:
    '  CustomerKey + ProviderKey (Funcom-issued realm auth
    '  credentials) and the human-readable RealmName used by
    '  the History Source column. One realm typically backs
    '  multiple installations (each install hosting a different
    '  tile-pool on different nodes); the SharedConfigGroup row
    '  stores those credentials once and all linked installs
    '  inherit them via the three-layer merge in
    '  InstanceManager.MergeConfigLayers.
    '
    '  DURING TRANSITION: CustomerKey + ProviderKey still appear
    '  in GetInstallConfigSchema too, so existing installations
    '  (which have those values stored at install level) keep
    '  working unchanged — the merge favours install over group
    '  on key collision. The Phase 5h-5 migration UI walks the
    '  user through promoting per-install values into a Realm
    '  group, after which the install-level entries become
    '  redundant and can be cleared. A future cleanup will drop
    '  these from GetInstallConfigSchema once all known users
    '  have migrated; for now leaving them in keeps the install
    '  editor familiar to anyone who hasn't migrated yet.
    ' ============================================================

    Public ReadOnly Property SharedConfigKey As String = "realm" _
        Implements ISharedConfigProvider.SharedConfigKey

    Public ReadOnly Property SharedConfigLabel As String = "Realm" _
        Implements ISharedConfigProvider.SharedConfigLabel

    ' Discriminator for auto-migration: two installations sharing
    ' the same CustomerKey value belong to the same realm. This
    ' is reliable because MyRealm issues one CustomerKey per
    ' realm — if the user pasted the same string into multiple
    ' installs, those installs really are on the same realm.
    Public ReadOnly Property DiscriminatorFieldKey As String = "CustomerKey" _
        Implements ISharedConfigProvider.DiscriminatorFieldKey

    Public Function GetSharedConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) _
            Implements ISharedConfigProvider.GetSharedConfigSchema
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "CustomerKey",
                .Label = "Customer Key",
                .Description = "Realm-wide authentication key from the MyRealm dashboard. Encrypted at rest.",
                .FieldType = ConfigFieldType.Text,
                .IsRequired = True,
                .IsSensitive = True
            },
            New ConfigFieldDescriptor With {
                .Key = "ProviderKey",
                .Label = "Provider Key",
                .Description = "Provider authentication key. Revoking this on MyRealm locks out every server using it. Encrypted at rest.",
                .FieldType = ConfigFieldType.Text,
                .IsRequired = True,
                .IsSensitive = True
            },
            New ConfigFieldDescriptor With {
                .Key = "RealmName",
                .Label = "Realm name",
                .Description = "Human-readable realm name as it appears on the MyRealm dashboard (e.g. ""Site's World""). Used wherever PowerGSM would otherwise show the raw numeric realm_id from logs — most visibly in the History window's Source column. Cosmetic only; leave blank and you'll just see the realm_id instead.",
                .FieldType = ConfigFieldType.Text,
                .IsRequired = False
            }
        }
    End Function

    ' ============================================================
    '  ISourceLabelProvider (Phase 5h)
    '
    '  Renders the History window's "Source" column. Format is
    '  three em-dash-separated segments, dropping any segment
    '  that has no data:
    '
    '    {TileName} — {RealmDisplay} — {NodeName}/{InstallationName}
    '
    '  RealmDisplay prefers the linked SharedConfigGroup's
    '  DisplayName (the user-friendly realm name picked from the
    '  realm picker) and falls back to "realm {first-8-chars-of-
    '  realm_id}…" parsed out of SessionIdentity when no group is
    '  linked. The fallback matches what FormatSessionLabel used
    '  to render before 5h-6 so the visual experience for
    '  un-linked installs is unchanged — the upgrade is that
    '  linked installs now show the realm by name.
    '
    '  The instance-path segment is intentionally Node /
    '  Installation — NOT Node / Installation / Instance —
    '  because LO's backend reassigns tiles across instances
    '  within an installation freely; the disambiguation that
    '  matters at the History level is which on-disk install
    '  the row's logs live in, not which specific child
    '  process. The full InstanceId is reachable via the
    '  History window's right-click "Copy instance ID" action
    '  for grep flows.
    ' ============================================================

    Public Function FormatSourceLabel(context As SourceLabelContext) As String _
            Implements ISourceLabelProvider.FormatSourceLabel
        If context Is Nothing Then Return Nothing

        ' --- Realm-display segment ---
        Dim realmDisplay As String = Nothing
        If Not String.IsNullOrEmpty(context.SharedConfigGroupName) Then
            realmDisplay = context.SharedConfigGroupName
        ElseIf Not String.IsNullOrEmpty(context.SessionIdentity) Then
            ' SessionIdentity shape: "lastoasis:{realm_id}:{tile_id}".
            ' parts(1) is realm_id; truncate to 8 chars + ellipsis
            ' for readability, matching pre-5h FormatSessionLabel.
            Dim parts = context.SessionIdentity.Split(":"c)
            If parts.Length >= 2 Then
                Dim rid = parts(1)
                Dim ridShort = If(rid.Length > 8, rid.Substring(0, 8) & "…", rid)
                realmDisplay = "realm " & ridShort
            End If
        End If

        ' --- Instance-path segment (Node/Install) ---
        Dim pathParts As New List(Of String)
        If Not String.IsNullOrEmpty(context.NodeName) Then pathParts.Add(context.NodeName)
        If Not String.IsNullOrEmpty(context.InstallationName) Then pathParts.Add(context.InstallationName)
        Dim instancePath = String.Join("/", pathParts)

        ' --- Compose. Skip empty segments so a partial context
        ' (e.g. no tile yet, no realm group set) still produces
        ' a clean label rather than awkward em-dash gaps. ---
        Dim segments As New List(Of String)
        If Not String.IsNullOrEmpty(context.TileName) Then segments.Add(context.TileName)
        If Not String.IsNullOrEmpty(realmDisplay) Then segments.Add(realmDisplay)
        If Not String.IsNullOrEmpty(instancePath) Then segments.Add(instancePath)

        If segments.Count = 0 Then Return Nothing
        Return String.Join(" — ", segments)
    End Function

    ' ============================================================
    '  Crash handling
    ' ============================================================

    Public Function EvaluateCrash(exitCode As Integer,
                                   crashCount As Integer,
                                   policy As CrashRestartPolicy) As RestartDecision Implements IGamePlugin.EvaluateCrash
        ' Exit code 0 = clean shutdown
        If exitCode = 0 Then
            Return RestartDecision.Halt("Clean exit (code 0)")
        End If

        ' Delegate to policy
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
        Return New LastOasisLogParser()
    End Function

    Public Function GetLogSources(config As InstanceConfig) As IReadOnlyList(Of ILogSource) Implements IGamePlugin.GetLogSources
        ' Tail the per-instance log file written by -AbsLog. The file
        ' contains everything stdout does plus LogNet categories that
        ' UE4 suppresses from stdout on server builds — player connect,
        ' disconnect, login request with name/platform/steamid etc.
        ' Stdout is NOT listed here because it would be redundant — the
        ' file captures a superset. Node still drains stdout pipes to
        ' prevent UE4 from blocking on writes to an unread pipe.
        '
        ' Forward slashes in the pattern so InstanceManager's path
        ' resolution can sit on either OS without rewriting separators.
        ' The {InstallPath} token is replaced with the install path as
        ' the node sees it, and {InstanceId} with the live id.
        Return New ILogSource() {
            New FileLogSource("mistlog", "{InstallPath}/Mist/Saved/Logs/{InstanceId}.log")
        }
    End Function

    Public Function GetLogParseRules() As IReadOnlyList(Of LogParseRule) Implements IGamePlugin.GetLogParseRules
        ' UE4 login flow across multiple log lines. The ordering
        ' is NOT fixed — different join paths interleave them
        ' differently:
        '
        '   Fresh connect (player launches game, joins this tile
        '   for the first time in the current session):
        '     1. NotifyAcceptingConnection — RemoteAddress only.
        '     2. Login request — CharacterId + PlatformPersona (Steam
        '        handle / Xbox gamertag from the URL's Name parameter)
        '        + Platform. PlatformUserId is "UNKNOWN" here because
        '        Steam/Xbox auth hasn't finished yet.
        '     3. Processing character update — the real PlatformUserId
        '        (SteamID64 / Xbox ID) paired with the CharacterId
        '        from step 2. Fires after persistence subsystem loads
        '        the character into memory.
        '
        '   World-travel arrival (player tiles in from another tile
        '   on the same realm; their session was already alive on the
        '   originating tile):
        '     1. Processing character update — fires FIRST, several
        '        seconds before the network connection arrives.
        '        Persistence loads the character data as soon as the
        '        cross-tile handoff packet hits the destination server.
        '     2. NotifyAcceptingConnection — RemoteAddress only,
        '        firing seconds AFTER (1).
        '     3. Login request — CharacterId + PlatformPersona.
        '
        ' We can't predict which path a given player is on, but we
        ' don't have to: the Processing-character-update rule is
        ' registered as Kind=PlayerIdentity (enrichment-only) and
        ' the node's EventStore carries a cid-keyed pending-identity
        ' stash for the cross-event correlation.
        '
        '   World-travel arrivals: Processing-character-update fires
        '   first with (cid, pid). No session exists yet, so the
        '   (pid, Platform) pair stashes under cid in
        '   PendingIdentitiesByCharacterId. The subsequent Login
        '   line creates the session (cid + persona) via
        '   FindOrCreateSession; DrainPendingCidIdentity then
        '   applies the stashed pid to the session, and the
        '   pid-keyed Persisting stash (if any) drains in turn.
        '
        '   Fresh connects: Login fires first and creates the
        '   session. Processing-character-update arrives next,
        '   FindExistingSession by cid hits the existing session,
        '   and the pid is applied directly via the enrichment
        '   branch.
        '
        '   Offline-but-on-tile characters: Processing-character-
        '   update fires on server boot or autosave for every
        '   persisted character on the tile, even ones whose
        '   players aren't connected. With PlayerIdentity
        '   classification this stashes by cid and produces no
        '   visible session in the player list — unlike the
        '   PlayerJoin classification we tried first in 5g-2,
        '   which materialised a ghost "Unknown" entry for every
        '   such character.
        '
        ' PlayerJoin classification was the initial 5g-2 design;
        ' it closed the world-travel race correctly but
        ' over-materialised sessions for every persistence-system
        ' line that mentioned a CharacterId. The cid-keyed stash
        ' added in EventStore closes the same race without that
        ' side effect.
        '
        '   4. LogPersistence Persisting fires every ~2 minutes per
        '      active player as part of the autosave tick. Format is
        '      `Persisting <CharacterName>, UniqueNetId =
        '      <Platform>:<PlatformUserId>` — CharacterName is the
        '      literal in-game character name, no suffix appended
        '      by UE4. Two distinct shapes share the `Persisting `
        '      prefix: actor lines (`Persisting <ActorClass>,
        '      ActorGuid = {GUID}`, used for inventory and world
        '      actors), and character lines (`Persisting <Name>,
        '      UniqueNetId = <Platform>:<UID>`). The `, UniqueNetId`
        '      literal in the pattern below is what discriminates
        '      the character line from the actor line.
        '      An earlier version of this regex assumed UE4
        '      appended a literal `'s character` to character
        '      names and stopped the capture at that literal. That
        '      worked for typical names like "andrekop" but
        '      silently chopped any name actually ending in `'s
        '      character` — a user with the in-game name
        '      `site's character` got captured as just `site`. The
        '      current pattern stops at `, UniqueNetId` instead so
        '      the full literal name is captured regardless of
        '      what punctuation it contains, including names
        '      with embedded commas (the regex engine backtracks
        '      through commas in the name until the `, UniqueNetId`
        '      anchor matches).
        '      This is the canonical source for the renamed-
        '      character display name that shows in chat (e.g.
        '      "andre(qc)" vs the Steam persona "andrekop").
        '   5. UNetConnection::Close on disconnect gives RemoteAddress
        '      which correlates back to the session.
        '
        ' Named capture groups are built via string concat to defeat
        ' an editor-tooling issue that lowercases `(?<Name>` → `(?<n>`.
        Dim gPlatformPersona = "(?<" & "PlatformPersona" & ">"
        Dim gDisplayName = "(?<" & "DisplayName" & ">"
        Dim gPlatform = "(?<" & "Platform" & ">"
        Dim gPlatformUserId = "(?<" & "PlatformUserId" & ">"
        Dim gCharacterId = "(?<" & "CharacterId" & ">"
        Dim gRemoteAddress = "(?<" & "RemoteAddress" & ">"
        Dim gMessage = "(?<" & "Message" & ">"
        Dim gMatchState = "(?<" & "MatchState" & ">"
        Dim gTileId = "(?<" & "TileId" & ">"
        Dim gTileName = "(?<" & "TileName" & ">"
        Dim gMapPath = "(?<" & "MapPath" & ">"
        Dim gRegistered = "(?<" & "Registered" & ">"

        Return New LogParseRule() {
            New LogParseRule With {
                .Name = "Connection accepted (IP only, buffered for next login)",
                .Kind = ParsedEventKind.PlayerJoin,
                .Pattern = "LogNet: NotifyAcceptingConnection accepted from: " & gRemoteAddress & "[0-9.]+:\d+)"
            },
            New LogParseRule With {
                .Name = "Login request (PlatformPersona + CharacterId)",
                .Kind = ParsedEventKind.PlayerJoin,
                .Pattern = "LogNet: Login request: \?CharacterId=" & gCharacterId & "\d+).*?\?Name=" & gPlatformPersona & "[^?]+?) userId: " & gPlatform & "\w+):"
            },
            New LogParseRule With {
                .Name = "Character update (PlatformUserId to CharacterId pairing)",
                .Kind = ParsedEventKind.PlayerIdentity,
                .Pattern = "LogPersistence: .*Processing character update.*UniqueId = " & gPlatformUserId & "\d+), CharacterId = " & gCharacterId & "\d+)"
            },
            New LogParseRule With {
                .Name = "Persisting (DisplayName to PlatformUserId pairing)",
                .Kind = ParsedEventKind.PlayerIdentity,
                .Pattern = "LogPersistence: .*Persisting " & gDisplayName & ".+?), UniqueNetId = " & gPlatform & "\w+):" & gPlatformUserId & "\d+)"
            },
            New LogParseRule With {
                .Name = "Player Disconnect (NetConnection close)",
                .Kind = ParsedEventKind.PlayerLeave,
                .Pattern = "LogNet: UNetConnection::Close.*?RemoteAddr: " & gRemoteAddress & "[0-9.]+:\d+),"
            },
            New LogParseRule With {
                .Name = "Player Disconnect (UChannel control-channel close — idle kick / server-initiated)",
                .Kind = ParsedEventKind.PlayerLeave,
                .Pattern = "LogNet: UChannel::Close: Sending CloseBunch\. ChIndex == 0\..*?RemoteAddr: " & gRemoteAddress & "[0-9.]+:\d+),"
            },
            New LogParseRule With {
                .Name = "Chat Message",
                .Kind = ParsedEventKind.ChatMessage,
                .Pattern = "LogGame: Chat message from " & gDisplayName & "[^:]+): " & gMessage & ".+)$"
            },
            New LogParseRule With {
                .Name = "Match State Transition",
                .Kind = ParsedEventKind.ServerStateChange,
                .Pattern = "LogGameMode: Display: Match State Changed from \w+ to " & gMatchState & "\w+)"
            },
            New LogParseRule With {
                .Name = "Backend Registered",
                .Kind = ParsedEventKind.ServerStateChange,
                .Pattern = "LogGame: Successfully " & gRegistered & "registered) with backend"
            },
            New LogParseRule With {
                .Name = "Map Loaded",
                .Kind = ParsedEventKind.TileLoaded,
                .Pattern = "LogWorld: Bringing World " & gMapPath & "/Game/Mist/Maps/\S+?) up for play"
            },
            New LogParseRule With {
                .Name = "Tile Id",
                .Kind = ParsedEventKind.TileLoaded,
                .Pattern = "LogPersistence: tile_id: " & gTileId & "\d+)"
            },
            New LogParseRule With {
                .Name = "Tile Name",
                .Kind = ParsedEventKind.TileLoaded,
                .Pattern = "LogPersistence: tile_name: " & gTileName & ".+)$"
            }
        }
    End Function

    ' ============================================================
    '  RCON
    '
    '  Last Oasis doesn't have one. Returning Nothing here
    '  flows into two places: (1) the InstanceManager's
    '  start-time block that resolves rconProtocol /
    '  rconPassword from the merged CustomFields skips the
    '  resolve when GetRconProtocol returns no value, so no
    '  RCON-related fields get pushed to the Node; (2) the
    '  InstancePanel's PopulateConfigTab and the Edit Instance
    '  form both filter the RconPort/RconPassword schema fields
    '  out when GetRconProtocol returns no value, so the
    '  Configuration tab no longer shows them either. The
    '  schema itself no longer declares those fields (see
    '  GetInstanceConfigSchema above), so even on a plugin
    '  reload after this change the UI stays consistent.
    ' ============================================================

    Public Function GetRconProtocol() As RconProtocol? Implements IGamePlugin.GetRconProtocol
        Return Nothing
    End Function

    ' ============================================================
    '  Mods — Last Oasis has no server-side mod support
    ' ============================================================

    Public Function CreateModManager() As IModManager Implements IGamePlugin.CreateModManager
        Return Nothing
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

End Class

' ============================================================
'  Last Oasis Log Parser
'  Detects player joins/leaves and UE4 crash patterns
' ============================================================

Public Class LastOasisLogParser
    Implements ILogParser, IConnectionBindingAware

    Public ReadOnly Property GameId As String = "lastoasis" Implements ILogParser.GameId

    ' ------------------------------------------------------------
    ' Name↔IP correlation for resolving player names on disconnect.
    '
    ' UE4's Join succeeded line carries the player name but not the
    ' remote address; the close lines (UChannel::Close /
    ' UNetConnection::Close) carry the remote address but not the
    ' name. We bridge the two by remembering every accepted
    ' connection's RemoteAddr (from NotifyAcceptedConnection) and
    ' binding it to the next player name we see in Join succeeded.
    '
    ' The parser runs inside the log-stream callback, which is
    ' single-threaded per instance, so no locking is needed.
    '
    ' The binding store (_connectionsByAddr) is the Manager-owned,
    ' per-instance dictionary injected via IConnectionBindingAware,
    ' so it PERSISTS across mid-session log-stream reconnects even
    ' though the parser object itself is recreated. That's what lets
    ' a close arriving after a reconnect still resolve its name —
    ' including a UChannel::Close-only timeout/kick, which the leave
    ' path drops entirely when it can't resolve a name. (Before this
    ' was externalised, a reconnect wiped the bindings and only clean
    ' UNetConnection::Close disconnects survived — via InstanceManager's
    ' nameless-leave heuristic; UChannel-only closes were silently
    ' lost, leaving the session open in history.) _pendingRemoteAddr
    ' stays per-parser and transient: losing it across a reconnect
    ' only affects a connection caught mid-handshake, which self-heals
    ' on the next close.
    ' ------------------------------------------------------------

    Private _pendingRemoteAddr As String = Nothing

    ' RemoteAddr -> player-name bindings, bound at "Join succeeded"
    ' and consumed on the close line. Externalised behind
    ' IConnectionBindingAware so the Manager hands us the SAME
    ' dictionary across stream reconnects — a recreated parser would
    ' otherwise start empty and drop the next close (notably a
    ' UChannel::Close-only timeout, which we no-match when we can't
    ' resolve a name) as a never-recorded leave. Defaults to a
    ' private dict for standalone use; the Manager overwrites it via
    ' the ConnectionBindings setter before the first line is parsed.
    Private _connectionsByAddr As IDictionary(Of String, String) =
        New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Public Property ConnectionBindings As IDictionary(Of String, String) _
        Implements IConnectionBindingAware.ConnectionBindings
        Get
            Return _connectionsByAddr
        End Get
        Set(value As IDictionary(Of String, String))
            If value IsNot Nothing Then _connectionsByAddr = value
        End Set
    End Property

    Private Shared ReadOnly _remoteAddrRegex As New Regex(
        "RemoteAddr:\s*([^\s,]+)",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)

    ' ------------------------------------------------------------
    ' Session-identity state machine.
    '
    ' Last Oasis logs four consecutive LogPersistence lines when a
    ' server transitions into tile-hosting mode:
    '
    '   LogPersistence: Started hosting tile
    '   LogPersistence: realm_id: 281474976945369
    '   LogPersistence: tile_name: Forested Wetlands
    '   LogPersistence: tile_id: 1688850325142345
    '
    ' All four arrive in the same millisecond (same engine hook).
    ' We reset the accumulator on the sentinel and commit once all
    ' three identity fields have been captured. The exit signal is
    ' the match-state transition OUT of InProgress — that's LO's
    ' reliable tell that the tile is being unloaded and the server
    ' is about to pick up (or re-host) a different one.
    '
    ' Identity format: "lastoasis:{realm_id}:{tile_id}". Tile name is
    ' carried as Metadata on the emitted TileLoaded event and as
    ' _currentTileName for display metadata on subsequent events,
    ' but is NOT part of the identity key (names can change across
    ' game updates without breaking the underlying history).
    ' ------------------------------------------------------------

    Private _currentSessionIdentity As String = Nothing
    Private _currentTileName As String = Nothing

    ' Accumulation state — active between "Started hosting tile"
    ' and the moment we've captured all three identity fields.
    Private _accumulating As Boolean = False
    Private _accRealmId As String = Nothing
    Private _accTileId As String = Nothing
    Private _accTileName As String = Nothing

    Private Shared ReadOnly _realmIdRegex As New Regex(
        "LogPersistence:\s*realm_id:\s*(\S+)",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)
    Private Shared ReadOnly _tileIdRegex As New Regex(
        "LogPersistence:\s*tile_id:\s*(\S+)",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)
    Private Shared ReadOnly _tileNameRegex As New Regex(
        "LogPersistence:\s*tile_name:\s*(.+?)\s*$",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)
    Private Shared ReadOnly _leavingInProgressRegex As New Regex(
        "Match State Changed from InProgress to \S+",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)

    Public ReadOnly Property CurrentSessionIdentity As String _
        Implements ILogParser.CurrentSessionIdentity
        Get
            Return _currentSessionIdentity
        End Get
    End Property

    Public Function ParseLine(line As LogLine) As ParsedLogEvent Implements ILogParser.ParseLine
        If line Is Nothing OrElse String.IsNullOrEmpty(line.Text) Then
            Return ParsedLogEvent.NoMatch
        End If

        Dim text = line.Text

        ' ----- Session identity: accumulation entry -----
        If text.Contains("LogPersistence: Started hosting tile") Then
            BeginAccumulating()
            Return ParsedLogEvent.NoMatch
        End If

        ' ----- Session identity: accumulation body -----
        ' Only probe the identity regexes while we're actively
        ' accumulating, and only on lines that carry the
        ' LogPersistence category — saves a lot of wasted regex
        ' work on the common heartbeat lines.
        If _accumulating AndAlso text.Contains("LogPersistence:") Then
            Dim m = _realmIdRegex.Match(text)
            If m.Success Then _accRealmId = m.Groups(1).Value

            m = _tileIdRegex.Match(text)
            If m.Success Then _accTileId = m.Groups(1).Value

            m = _tileNameRegex.Match(text)
            If m.Success Then _accTileName = m.Groups(1).Value

            ' Commit as soon as all three fields are populated.
            ' Order doesn't matter — LO currently emits them
            ' realm_id → tile_name → tile_id in consecutive lines,
            ' but committing on "all present" rather than "saw
            ' tile_id last" means we stay robust if the game ever
            ' adds more fields or reorders.
            If Not String.IsNullOrEmpty(_accRealmId) AndAlso
               Not String.IsNullOrEmpty(_accTileId) AndAlso
               Not String.IsNullOrEmpty(_accTileName) Then
                Return CommitSession(line.Timestamp)
            End If
            Return ParsedLogEvent.NoMatch
        End If

        ' ----- Session identity: exit -----
        ' Leaving InProgress means the tile is being dropped. Emit
        ' TileUnloaded stamped with the identity that just ended,
        ' then clear current state so subsequent events (if any)
        ' flow with no identity until the next Started-hosting-tile.
        If _currentSessionIdentity IsNot Nothing AndAlso
           _leavingInProgressRegex.IsMatch(text) Then
            Return ClearSession()
        End If

        ' ----- Connection tracking (unchanged) -----
        If text.Contains("LogNet: NotifyAcceptedConnection") Then
            Dim addr = ExtractRemoteAddr(text)
            If Not String.IsNullOrEmpty(addr) Then
                _pendingRemoteAddr = addr
            End If
            Return ParsedLogEvent.NoMatch
        End If

        ' ----- Player join -----
        If text.Contains("LogNet: Join succeeded:") Then
            Dim playerName = ExtractAfter(text, "Join succeeded: ")

            If Not String.IsNullOrEmpty(_pendingRemoteAddr) AndAlso
               Not String.IsNullOrWhiteSpace(playerName) Then
                _connectionsByAddr(_pendingRemoteAddr) = playerName
                _pendingRemoteAddr = Nothing
            End If

            Return StampIdentity(New ParsedLogEvent With {
                .EventType = LogEventType.PlayerJoin,
                .Message = $"Player joined: {playerName}",
                .PlayerInfo = New PlayerInfo With {
                    .PlayerName = playerName,
                    .JoinedAt = line.Timestamp
                }
            })
        End If

        ' ----- Player leave -----
        '
        ' Two flavours of disconnect emit different log shapes:
        '
        '   (1) Client-initiated disconnect (normal quit, alt-F4,
        '       crash, network drop): UE4 fires
        '         LogNet: UNetConnection::Close
        '       once, then iterates the connection's open channels
        '       and fires LogNet: UChannel::Close: Sending
        '       CloseBunch... for each, INCLUDING the control
        '       channel at ChIndex == 0. Total: one
        '       UNetConnection::Close + many UChannel::Close.
        '
        '   (2) Server-initiated kick (idle kick, manual kick, ban):
        '       UE4 destroys the PlayerController via KickPlayer,
        '       which closes the control channel directly via
        '         LogNet: UChannel::Close: Sending CloseBunch.
        '         ChIndex == 0. ...
        '       and DOES NOT emit UNetConnection::Close at all.
        '       The Steamworks session is already torn down so
        '       UniqueId reads as "Steam:UNKNOWN" on this line.
        '
        ' To catch both flavours with exactly one named leave per
        ' real disconnect, we match each pattern but dedup against
        ' _connectionsByAddr: the FIRST match for a given remote
        ' address removes the entry, so the second match (the
        ' UChannel::Close after UNetConnection::Close in flavour 1)
        ' finds no entry and is suppressed as NoMatch. In flavour
        ' 2 only UChannel::Close fires, the entry is found, and
        ' the leave is emitted normally.
        '
        ' Order of the two branches doesn't matter — either match
        ' first wins, the other dedups itself out. Listing
        ' UNetConnection::Close first because it's the more
        ' common path and exits the function earlier on hit.

        If text.Contains("LogNet: UNetConnection::Close") Then

            Dim addr = ExtractRemoteAddr(text)
            Dim resolvedName As String = Nothing

            If Not String.IsNullOrEmpty(addr) Then
                _connectionsByAddr.TryGetValue(addr, resolvedName)
                _connectionsByAddr.Remove(addr)
            End If

            Dim info As PlayerInfo = Nothing
            If Not String.IsNullOrEmpty(resolvedName) Then
                info = New PlayerInfo With {.PlayerName = resolvedName}
            End If

            Return StampIdentity(New ParsedLogEvent With {
                .EventType = LogEventType.PlayerLeave,
                .Message = If(String.IsNullOrEmpty(resolvedName),
                              "Player disconnected",
                              $"Player left: {resolvedName}"),
                .PlayerInfo = info
            })
        End If

        ' Server-initiated kick path — idle kick, manual kick,
        ' ban. ChIndex == 0 restricts us to the control channel
        ' close (one per connection); without that restriction
        ' every actor/voice channel close on a normal disconnect
        ' would also flow through here. "Sending" (not
        ' "Receiving") because the SERVER is initiating the close
        ' — client-initiated closes still appear in flavour-1
        ' UChannel::Close lines but those are already deduped
        ' away by the entry removal in the UNetConnection::Close
        ' branch above.
        '
        ' Dedup discipline: only emit if _connectionsByAddr has
        ' an entry for this address. If not, either (a) flavour-1
        ' already removed it (UNetConnection::Close fired first),
        ' or (b) the parser was created mid-session and never
        ' saw the corresponding Join — in both cases NoMatch is
        ' safer than a nameless leave that InstanceManager's
        ' "exactly one player online" heuristic might misattribute.
        If text.Contains("LogNet: UChannel::Close: Sending CloseBunch. ChIndex == 0") Then

            Dim addr = ExtractRemoteAddr(text)
            If String.IsNullOrEmpty(addr) Then
                Return ParsedLogEvent.NoMatch
            End If

            Dim resolvedName As String = Nothing
            If Not _connectionsByAddr.TryGetValue(addr, resolvedName) Then
                ' Either deduped (regular disconnect, UNetConnection::Close
                ' branch above already handled it) or we don't have a
                ' name binding to attach. Either way, drop.
                Return ParsedLogEvent.NoMatch
            End If
            _connectionsByAddr.Remove(addr)

            Return StampIdentity(New ParsedLogEvent With {
                .EventType = LogEventType.PlayerLeave,
                .Message = $"Player kicked or disconnected: {resolvedName}",
                .PlayerInfo = New PlayerInfo With {.PlayerName = resolvedName}
            })
        End If

        ' ----- Server ready -----
        If text.Contains("LogOnline: OSS: Created online async task") OrElse
           text.Contains("LogInit: Display: Engine is initialized") Then
            Return StampIdentity(New ParsedLogEvent With {
                .EventType = LogEventType.ServerReady,
                .Message = "Server is ready"
            })
        End If

        ' ----- UE4 crash patterns -----
        If text.Contains("Fatal error!") OrElse
           text.Contains("Unhandled Exception:") OrElse
           text.Contains("LowLevelFatalError") OrElse
           text.Contains("Access violation") OrElse
           text.Contains("=== Critical error: ===") OrElse
           text.Contains("Assertion failed:") Then
            Return StampIdentity(New ParsedLogEvent With {
                .EventType = LogEventType.CrashIndicator,
                .Message = text
            })
        End If

        Return ParsedLogEvent.NoMatch
    End Function

    ''' <summary>
    ''' Start (or restart) accumulation of session identity fields.
    ''' Called on "Started hosting tile" sentinel lines. Defensive
    ''' against the sentinel firing twice in a row by resetting the
    ''' accumulator unconditionally.
    ''' </summary>
    Private Sub BeginAccumulating()
        _accumulating = True
        _accRealmId = Nothing
        _accTileId = Nothing
        _accTileName = Nothing
    End Sub

    ''' <summary>
    ''' Finalize the pending identity, set CurrentSessionIdentity,
    ''' and return a TileLoaded event to be emitted downstream.
    ''' Called when all three accumulated fields are populated.
    ''' </summary>
    Private Function CommitSession(timestamp As DateTime) As ParsedLogEvent
        _currentSessionIdentity = $"lastoasis:{_accRealmId}:{_accTileId}"
        _currentTileName = _accTileName

        Dim metadata As New Dictionary(Of String, String) From {
            {"RealmId", _accRealmId},
            {"TileId", _accTileId},
            {"TileName", _accTileName}
        }

        Dim ev = New ParsedLogEvent With {
            .EventType = LogEventType.TileLoaded,
            .Message = $"Hosting tile: {_accTileName}",
            .Metadata = metadata,
            .SessionIdentity = _currentSessionIdentity
        }

        _accumulating = False
        _accRealmId = Nothing
        _accTileId = Nothing
        _accTileName = Nothing

        Return ev
    End Function

    ''' <summary>
    ''' End the current session identity and emit a TileUnloaded
    ''' event stamped with the identity that just ended. Called
    ''' when the server leaves the InProgress match state.
    ''' </summary>
    Private Function ClearSession() As ParsedLogEvent
        Dim endingIdentity = _currentSessionIdentity
        Dim endingTileName = _currentTileName

        Dim metadata As New Dictionary(Of String, String)
        If Not String.IsNullOrEmpty(endingTileName) Then
            metadata("TileName") = endingTileName
        End If

        _currentSessionIdentity = Nothing
        _currentTileName = Nothing

        Return New ParsedLogEvent With {
            .EventType = LogEventType.TileUnloaded,
            .Message = If(String.IsNullOrEmpty(endingTileName),
                          "Tile unloaded",
                          $"Tile unloaded: {endingTileName}"),
            .Metadata = metadata,
            .SessionIdentity = endingIdentity
        }
    End Function

    ''' <summary>
    ''' Stamp the parser's current session identity onto an outgoing
    ''' event. Preserves an identity the caller may have already set
    ''' (used by TileLoaded/TileUnloaded which manage their own
    ''' identity fields).
    ''' </summary>
    Private Function StampIdentity(ev As ParsedLogEvent) As ParsedLogEvent
        If String.IsNullOrEmpty(ev.SessionIdentity) Then
            ev.SessionIdentity = _currentSessionIdentity
        End If
        Return ev
    End Function

    Public Function GetCrashPatterns() As IReadOnlyList(Of String) Implements ILogParser.GetCrashPatterns
        Return New String() {
            "Fatal error!",
            "Unhandled Exception:",
            "Access violation",
            "LowLevelFatalError",
            "=== Critical error: ===",
            "begin: stack for UAT",
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