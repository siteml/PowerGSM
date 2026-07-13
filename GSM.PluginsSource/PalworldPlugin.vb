' <plugin id="palworld" name="Palworld Dedicated Server" version="0.1.0" author="siteml" requiresContracts="2">
' <RequiresContracts: 2>
Imports System
Imports System.Collections.Generic
Imports GSM.Plugin

' ============================================================
'  Palworld Dedicated Server Plugin — SLICE 1 (core: install +
'  launch + stop)
'
'  AppID: 2394010 (free dedicated server, anonymous SteamCMD) —
'         separate from the game client app (1623730).
'  Engine: Unreal Engine 5 (project "Pal"; Pal\Saved\... layout)
'  Install: SteamCMD only, anonymous
'  Platform: native Windows AND native Linux
'  RCON: exists but DEPRECATED by Pocketpair ("scheduled to stop
'        functioning in an upcoming update") and mangles
'        multi-byte player names — intentionally not used.
'        Remote control targets the HTTP REST API instead
'        (Slice 4 of Palworld_Plugin_Plan.md).
'
'  SLICE SCOPE (Slice 1 of Palworld_Plugin_Plan.md):
'    install via SteamCMD (per-platform depot), launch, lifecycle,
'    crash handling, graceful CtrlC/SIGINT stop, prereqs +
'    install notices, and the allocator-managed LISTENING port
'    (a plain -port= launch arg per the official docs — no tuple
'    render needed for it).
'    NOT here yet: OptionSettings tuple editor (Slice 2), the
'    tuple-resident ports (RESTAPIPort; Slice 2/3), REST control
'    / player list (Slice 4), saves managed dir (Slice 5),
'    stdout log capture strategy (with Slice 4 — see
'    GetLogSources).
'
'  VERIFIED on live install 12 Jul 2026 (Windows) + official
'  docs (docs.palworldgame.com):
'    Q1: Shipping-Cmd exe is the resident server process. PASS.
'    Q2: Manager Stop cleanly stops the server. PASS.
'    Q3: NO file log exists, even with -log — console/stdout
'        only (mods can add one). File source below is inert;
'        kept only to keep the hidden-console spawn (see
'        GetLogSources for the strategy).
'    Q5: config dirs are created only by the first server run —
'        an install-time copy step can never work (official docs
'        confirm). Seed copy REMOVED; Slice 2's editor builds
'        from an embedded default tuple instead.
'    Q7: -port= is the ONLY way to change the listening port —
'        the tuple's PublicPort/PublicIP are community-browser
'        advertise values and do NOT change the bind. -players=
'        also exists; perf flags are v1.0-deprecated ("leaving
'        this parameter unset may improve performance").
'    Still open: Linux run (Q1/Q2 Linux half), REST bind (Q4).
'
'  Why MaxInstancesPerInstallation = 1:
'    PalWorldSettings.ini and Pal\Saved\SaveGames are shared per
'    install with no per-instance selector — two instances would
'    fight over one config and one world. Multiple Palworld
'    servers on a node = multiple Installations.
' ============================================================

Public Class PalworldPlugin
    Implements IGamePlugin
    Implements IInstallationNoticeProvider
    Implements IPrerequisiteProvider
    Implements ILaunchOptionsProvider
    Implements IInstanceFileEditorProvider

    Public ReadOnly Property GameId As String = "palworld" Implements IGamePlugin.GameId
    Public ReadOnly Property DisplayName As String = "Palworld" Implements IGamePlugin.DisplayName

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
        Dim isLinux = config IsNot Nothing AndAlso config.Platform = NodePlatform.Linux

        Dim steamStep As New SteamCmdStep()
        steamStep.StepName = "Download Palworld Dedicated Server"
        steamStep.Description = "Download/update via SteamCMD (AppID 2394010, anonymous)"
        steamStep.AppId = 2394010
        steamStep.ValidateFiles = True
        ' No save-wipe-on-validate reports for Palworld (unlike
        ' Dragonwilds) — validate is safe on updates too.
        steamStep.RequiresLogin = False
        ' Single app id, per-platform depots — SteamCMD picks the
        ' right one via the platform type. Native Linux server
        ' exists, no Proton/Wine needed.
        steamStep.Platform = If(isLinux, "linux", "windows")
        steps.Add(steamStep)

        ' NO config seed step. Q5 resolved: the official docs
        ' confirm "the directories will only create once the server
        ' has been started", so an install-time copy of
        ' DefaultPalWorldSettings.ini into Pal/Saved/Config/... can
        ' never work (and the node's CopyFileStep reported success
        ' without producing the file — silent-failure quirk, noted
        ' in Backlog). Instead, Slice 2's editor carries an embedded
        ' copy of the default OptionSettings tuple and builds a
        ' complete valid file whenever the live one is blank/absent.

        Return steps
    End Function

    Public Function GetUpdateSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetUpdateSteps
        ' Update == install. Pocketpair patches frequently and
        ' server/client versions must match for joins, so operators
        ' should update promptly after game patches. The seed copy
        ' is a no-op on update (Overwrite=False).
        Return GetInstallSteps(config)
    End Function

    ' ============================================================
    '  Instance
    ' ============================================================

    Public Function GetExecutablePath(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.GetExecutablePath
        ' Q1 — the Windrose/Conan wrapper-PID lesson applies: the
        ' root PalServer.exe is a small UE bootstrap that launches
        ' the real server and may exit, leaving PowerGSM tracking a
        ' dead PID. The real console server binary is
        '   Pal\Binaries\Win64\PalServer-Win64-Shipping-Cmd.exe
        ' (the -Cmd console variant; the plain
        ' PalServer-Win64-Shipping.exe is the windowed variant).
        ' Candidates are tried in order and the survivor is
        ' remembered, so: Shipping-Cmd first, root wrapper as the
        ' fallback in case a future build drops/renames the -Cmd
        ' variant. If live testing shows the root exe actually
        ' stays resident as the true server process, flip the order.
        '
        ' Linux: PalServer.sh (shebang script wrapping the Linux
        ' Shipping binary). VERIFY on the linux-test node that the
        ' script exec's the binary (PID + SIGINT propagation intact,
        ' same concern the Stardew /bin/sh bootstrap solved with
        ' exec). If it forks instead, switch to the Shipping binary
        '   Pal/Binaries/Linux/PalServer-Linux-Shipping
        ' directly — listed as the fallback candidate.
        Select Case If(config IsNot Nothing, config.Platform, NodePlatform.Unknown)
            Case NodePlatform.Linux
                Return New String() {
                    "PalServer.sh",
                    "Pal/Binaries/Linux/PalServer-Linux-Shipping"
                }
            Case NodePlatform.Windows
                Return New String() {
                    "Pal/Binaries/Win64/PalServer-Win64-Shipping-Cmd.exe",
                    "PalServer.exe"
                }
            Case Else
                ' Unknown platform (old node) — emit all candidates,
                ' the Manager's probe loop finds the one that exists.
                Return New String() {
                    "Pal/Binaries/Win64/PalServer-Win64-Shipping-Cmd.exe",
                    "PalServer.exe",
                    "PalServer.sh",
                    "Pal/Binaries/Linux/PalServer-Linux-Shipping"
                }
        End Select
    End Function

    Public Function BuildLaunchArguments(config As InstanceConfig) As String Implements IGamePlugin.BuildLaunchArguments
        ' Q7 RESOLVED (official docs): the LISTENING port is set by
        ' -port= and ONLY by -port= — the tuple's PublicPort does
        ' not change the bind (it's the community-browser advertise
        ' port). So the allocator-managed port is a plain launch
        ' arg, no startup-file render needed for it. Everything
        ' else stays in the tuple (single source of truth).
        '
        ' -log: UE flag — Palworld writes NO file log regardless
        '   (verified), but -log arms SetConsoleCtrlHandler so
        '   AttachConsole + CTRL_C routes to a clean save-and-exit;
        '   Q2 passed with it, keep it.
        ' Perf flags (-useperfthreads etc.) DROPPED: the official
        '   docs say in v1.0+ "leaving this parameter unset may
        '   improve performance".
        ' -publiclobby: lists the server in the in-game community-
        '   server browser (and lets console players find it).
        '   Advertise IP/port (PublicIP/PublicPort) live in the
        '   tuple (Slice 2); off by default.
        Dim args As New List(Of String) From {"-log"}

        Dim fields = If(config IsNot Nothing, config.CustomFields, Nothing)

        Dim port As Integer
        If Integer.TryParse(GetField(fields, "Port"), port) AndAlso port > 0 Then
            args.Add($"-port={port}")
        End If

        Dim publicLobby As Boolean
        If Boolean.TryParse(GetField(fields, "PublicLobby"), publicLobby) AndAlso publicLobby Then
            args.Add("-publiclobby")
        End If

        Return String.Join(" ", args)
    End Function

    Public Function ValidateConfig(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.ValidateConfig
        ' Slice 1's only instance field is a bool the schema already
        ' constrains. Cross-field rules (e.g. -publiclobby without a
        ' PublicIP) arrive with the Slice 2/3 fields.
        Return New List(Of String)
    End Function

    ' ============================================================
    '  Config schema
    ' ============================================================

    Public Function GetInstallConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstallConfigSchema
        ' Single public depot, no install-time keys.
        Return New ConfigFieldDescriptor() {}
    End Function

    Public Function GetInstanceConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstanceConfigSchema
        ' The listening port is a launch arg (see
        ' BuildLaunchArguments), so it lives here with IsPort and
        ' the allocator manages it. The tuple-resident RESTAPIPort
        ' arrives with the Slice 2/3 file work.
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "Port",
                .Label = "Game port (UDP)",
                .Description = "The port the server listens on (-port=). Forward this UDP port on your router. PowerGSM allocates and clash-checks it across all instances on the node. Note: this is the real bind port — the PublicPort setting in PalWorldSettings.ini is only the advertised port for the community browser.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "8211",
                .MinValue = 1024,
                .MaxValue = 65535,
                .IsPort = True
            },
            New ConfigFieldDescriptor With {
                .Key = "PublicLobby",
                .Label = "List in community server browser",
                .Description = "Adds -publiclobby so the server appears in Palworld's in-game community-server list (also how console players find it). Needs PublicIP and PublicPort set correctly in the server settings; leave off for invite-by-IP servers.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            }
        }
    End Function

    ' ============================================================
    '  Crash handling — standard policy delegation
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
        ' Later slice — needs live log captures. Slice 4's REST
        ' player list may cover most of the need; a Manager-side
        ' parser only lands if History/join-leave gaps remain.
        Return Nothing
    End Function

    Public Function GetLogSources(config As InstanceConfig) As IReadOnlyList(Of ILogSource) Implements IGamePlugin.GetLogSources
        ' Q3 RESOLVED + stdout experiment (12 Jul 2026, Windows):
        ' Palworld writes NO file log (mod required). Stdout capture
        ' on Windows yields NOTHING useful — UE writes its log via
        ' the console API (WriteConsole), not the stdout handle, so
        ' a redirected pipe only sees stray CRT/stderr output (the
        ' only captured lines were Steamworks tier0 thread-
        ' termination asserts at shutdown). Graceful stop stayed
        ' fast with capture on, so no harm — just no signal.
        '
        ' Windows: inert file source (path never exists) — keeps the
        '   hidden-console direct spawn; observability arrives with
        '   Slice 4's REST client (/players, /info, /metrics).
        ' Linux: stdout capture — no console/stdio split there, the
        '   server's output goes to real stdout. VERIFY on the
        '   linux-test node with the Linux run.
        Select Case If(config IsNot Nothing, config.Platform, NodePlatform.Unknown)
            Case NodePlatform.Linux
                Return New ILogSource() {
                    New StdoutLogSource()
                }
            Case Else
                Return New ILogSource() {
                    New FileLogSource("pal", "{InstallPath}/Pal/Saved/Logs/Pal.log")
                }
        End Select
    End Function

    Public Function GetLogParseRules() As IReadOnlyList(Of LogParseRule) Implements IGamePlugin.GetLogParseRules
        ' Later slice — blocked on live captures.
        Return New LogParseRule() {}
    End Function

    ' ============================================================
    '  RCON — deliberately none (deprecated upstream)
    ' ============================================================

    Public Function GetRconProtocol() As RconProtocol? Implements IGamePlugin.GetRconProtocol
        Return Nothing
    End Function

    Public Function CreateModManager() As IModManager Implements IGamePlugin.CreateModManager
        Return Nothing
    End Function

    ' ============================================================
    '  IInstallationNoticeProvider
    ' ============================================================

    Public Function GetPreInstallNotices() As IReadOnlyList(Of InstallationNotice) Implements IInstallationNoticeProvider.GetPreInstallNotices
        Return New InstallationNotice() {
            New InstallationNotice With {
                .Severity = NoticeSeverity.Information,
                .Title = "Settings apply on restart only",
                .Body = "Palworld reads PalWorldSettings.ini once at boot. Any config change — server name, passwords, rates — needs a server restart to take effect. There is no live settings reload."
            },
            New InstallationNotice With {
                .Severity = NoticeSeverity.Information,
                .Title = "Update promptly after game patches",
                .Body = "Pocketpair patches Palworld frequently and clients can't join a version-mismatched server. Run Update on this installation after every game patch."
            },
            New InstallationNotice With {
                .Severity = NoticeSeverity.Warning,
                .Title = "Public visibility needs PublicIP",
                .Body = "To appear in the community server browser (-publiclobby), set Public IP to this node's external IP and Public port to the forwarded external port — both on the instance's Server Settings tab (advertise-only values; the real listening port is on the Configuration tab)."
            }
        }
    End Function

    ' ============================================================
    '  IPrerequisiteProvider
    '
    '  UE5 Shipping on Windows links the VC++ 2015-2022 x64
    '  runtime (Palworld's depot ships a _CommonRedist vcredist,
    '  which the node's post-install pass runs anyway — this
    '  notice just catches hosts where that pass can't run). The
    '  node's probe catalog is Windows-only; on a Linux node the
    '  name comes back unrecognised and is silently skipped.
    ' ============================================================

    Public Function GetRequiredPrerequisites() As IReadOnlyList(Of String) Implements IPrerequisiteProvider.GetRequiredPrerequisites
        Return New String() {"vcredist-2015-2022-x64"}
    End Function

    ' ============================================================
    '  ILaunchOptionsProvider
    '
    '  Defaults otherwise: file log source declared -> hidden-
    '  console direct spawn -> AttachConsole + CTRL_C graceful stop
    '  on Windows, SIGINT on Linux. Palworld is the rare UE server
    '  with a genuinely clean stop (closing the console saves and
    '  exits), so this path should work — Q2 verifies. 60s timeout:
    '  the save flush on a populated world takes a while; force-
    '  kill before the flush completes risks losing progress since
    '  the last autosave (30s default cadence). Per-instance
    '  override via the "GracefulTimeoutMs" custom field as usual.
    ' ============================================================

    Public Function GetLaunchOptions(config As InstanceConfig) As LaunchOptions Implements ILaunchOptionsProvider.GetLaunchOptions
        Return New LaunchOptions With {
            .GracefulShutdownTimeoutMs = 60000
        }
    End Function

    ' ============================================================
    '  IInstanceFileEditorProvider — OptionSettings tuple editor
    '  (SLICE 2)
    '
    '  Palworld's whole config surface is ONE line in
    '  Pal/Saved/Config/<platform>/PalWorldSettings.ini:
    '    [/Script/Pal.PalGameWorldSettings]
    '    OptionSettings=(Key=Val,Key="Val",Nested=(A,B),...)
    '  — a UE struct tuple, NOT normal INI. The parser below is
    '  bespoke: split the payload on commas at paren-depth 0 and
    '  outside double quotes, preserve key order and every unknown
    '  key verbatim, rewrite only the schema-managed keys.
    '
    '  Blank-file handling (Decision D2 revised): a fresh install
    '  has NO usable file (the server creates an empty one on first
    '  run; install-time seeding is impossible). When the live file
    '  lacks an OptionSettings tuple, both read and write fall back
    '  to DefaultOptionSettings — the full ~110-key default tuple
    '  captured verbatim from a live 12 Jul 2026 install — so the
    '  editor always shows real values and Save always produces a
    '  complete valid file. The embedded default WILL drift as
    '  Pocketpair adds keys; it is only the fallback skeleton — a
    '  populated live file always wins, and unknown keys in it
    '  round-trip untouched.
    '
    '  Serialisation is schema-driven: Text/Password fields write
    '  quoted, IntegerField writes bare, BooleanField writes
    '  True/False. Double-quote characters are stripped from
    '  operator input — the tuple format has no escape syntax, so
    '  an embedded quote would corrupt the whole line.
    '
    '  The game listening port is NOT here (launch arg, Configuration
    '  tab). PublicPort here is the community-browser ADVERTISE port
    '  only. RESTAPIPort IS a real TCP listener but lives here (the
    '  allocator only sees Configuration-tab fields) — revisit if
    '  clashes ever matter in practice.
    ' ============================================================

    Private Const SettingsEditorKey As String = "palworld-settings"
    Private Const OptionSettingsPrefix As String = "OptionSettings=("
    Private Const SettingsSectionHeader As String = "[/Script/Pal.PalGameWorldSettings]"

    ' Full default OptionSettings payload (the text between the
    ' outer parens), captured verbatim from DefaultPalWorldSettings.ini
    ' of a live install, 12 Jul 2026. Fallback skeleton only.
    Private Const DefaultOptionSettings As String =
        "Difficulty=None,RandomizerType=None,RandomizerSeed="""",bIsRandomizerPalLevelRandom=False,DayTimeSpeedRate=1.000000,NightTimeSpeedRate=1.000000,ExpRate=1.000000,PalCaptureRate=1.000000,PalSpawnNumRate=1.000000,PalDamageRateAttack=1.000000,PalDamageRateDefense=1.000000,PlayerDamageRateAttack=1.000000,PlayerDamageRateDefense=1.000000,PlayerStomachDecreaceRate=1.000000,PlayerStaminaDecreaceRate=1.000000,PlayerAutoHPRegeneRate=1.000000,PlayerAutoHpRegeneRateInSleep=1.000000,PalStomachDecreaceRate=1.000000,PalStaminaDecreaceRate=1.000000,PalAutoHPRegeneRate=1.000000,PalAutoHpRegeneRateInSleep=1.000000,BuildObjectHpRate=1.000000,BuildObjectDamageRate=1.000000,BuildObjectDeteriorationDamageRate=1.000000,CollectionDropRate=1.000000,CollectionObjectHpRate=1.000000,CollectionObjectRespawnSpeedRate=1.000000,EnemyDropItemRate=1.000000,DeathPenalty=Item,bEnablePlayerToPlayerDamage=False,bEnableFriendlyFire=False,bEnableInvaderEnemy=True,bActiveUNKO=False,bEnableAimAssistPad=True,bEnableAimAssistKeyboard=False,DropItemMaxNum=3000,PhysicsActiveDropItemMaxNum=-1,DropItemMaxNum_UNKO=100,BaseCampMaxNum=128,BaseCampWorkerMaxNum=15,DropItemAliveMaxHours=1.000000,bAutoResetGuildNoOnlinePlayers=False,AutoResetGuildTimeNoOnlinePlayers=72.000000,GuildPlayerMaxNum=20,BaseCampMaxNumInGuild=4,PalEggDefaultHatchingTime=1.000000,WorkSpeedRate=1.000000,AutoSaveSpan=30.000000,bIsMultiplay=False,bIsPvP=False,bHardcore=False,bPalLost=False,bCharacterRecreateInHardcore=False,bCanPickupOtherGuildDeathPenaltyDrop=False,bEnableNonLoginPenalty=True,bEnableFastTravel=True,bEnableFastTravelOnlyBaseCamp=False,bIsStartLocationSelectByMap=False,bExistPlayerAfterLogout=False,bEnableDefenseOtherGuildPlayer=False,bInvisibleOtherGuildBaseCampAreaFX=False,bBuildAreaLimit=False,ItemWeightRate=1.000000,CoopPlayerMaxNum=4,ServerPlayerMaxNum=32,ServerName=""Default Palworld Server"",ServerDescription="""",AdminPassword="""",ServerPassword="""",bAllowClientMod=True,PublicPort=8211,PublicIP="""",RCONEnabled=False,RCONPort=25575,Region="""",bUseAuth=True,BanListURL=""https://b.palworldgame.com/api/banlist.txt"",RESTAPIEnabled=False,RESTAPIPort=8212,bShowPlayerList=False,ChatPostLimitPerMinute=30,CrossplayPlatforms=(Steam,Xbox,PS5,Mac),bIsUseBackupSaveData=True,LogFormatType=Text,bIsShowJoinLeftMessage=True,SupplyDropSpan=180,EnablePredatorBossPal=True,MaxBuildingLimitNum=0,ServerReplicatePawnCullDistance=15000.000000,bAllowGlobalPalboxExport=True,bAllowGlobalPalboxImport=False,EquipmentDurabilityDamageRate=1.000000,ItemContainerForceMarkDirtyInterval=1.000000,PlayerDataPalStorageUpdateCheckTickInterval=1.000000,ItemCorruptionMultiplier=1.000000,MonsterFarmActionSpeedRate=1.000000,DenyTechnologyList=,GuildRejoinCooldownMinutes=0,AutoTransferMasterCheckIntervalSeconds=3600.000000,AutoTransferMasterThresholdDays=14,MaxGuildsPerFrame=10,BlockRespawnTime=5.000000,RespawnPenaltyDurationThreshold=0.000000,RespawnPenaltyTimeScale=2.000000,bDisplayPvPItemNumOnWorldMap_BaseCamp=False,bDisplayPvPItemNumOnWorldMap_Player=False,AdditionalDropItemWhenPlayerKillingInPvPMode=""PlayerDropItem"",AdditionalDropItemNumWhenPlayerKillingInPvPMode=1,bAdditionalDropItemWhenPlayerKillingInPvPMode=False,bEnableVoiceChat=False,VoiceChatMaxVolumeDistance=3000.000000,VoiceChatZeroVolumeDistance=15000.000000,bAllowEnhanceStat_Health=True,bAllowEnhanceStat_Attack=True,bAllowEnhanceStat_Stamina=True,bAllowEnhanceStat_Weight=True,bAllowEnhanceStat_WorkSpeed=True,bEnableBuildingPlayerUIdDisplay=False,BuildingNameDisplayCacheTTLSeconds=60"

    ' Managed keys and how each serialises back into the tuple.
    ' Quoted = Text/Password (write wrapped in double quotes);
    ' everything else writes bare (ints, True/False).
    Private Shared ReadOnly QuotedKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "ServerName", "ServerDescription", "AdminPassword", "ServerPassword", "PublicIP"
    }

    Public Function GetInstanceFileEditors(config As InstanceConfig) _
            As IReadOnlyList(Of InstanceFileEditor) _
            Implements IInstanceFileEditorProvider.GetInstanceFileEditors
        Dim isLinux = config IsNot Nothing AndAlso config.Platform = NodePlatform.Linux
        Dim relPath = If(isLinux,
            "Pal/Saved/Config/LinuxServer/PalWorldSettings.ini",
            "Pal/Saved/Config/WindowsServer/PalWorldSettings.ini")
        Return New InstanceFileEditor() {
            New InstanceFileEditor With {
                .Key = SettingsEditorKey,
                .TabTitle = "Server Settings",
                .RelativePath = relPath,
                .Schema = BuildSettingsSchema(),
                .RequiresExistingFile = False
            }
        }
    End Function

    Private Shared Function BuildSettingsSchema() As IReadOnlyList(Of ConfigFieldDescriptor)
        ' Initial curated management set. The full ~110-key tuple
        ' (rates, gameplay toggles) stays unknown-round-trip for
        ' now; add batches later. Official per-key reference:
        ' docs.palworldgame.com/settings-and-operation/configuration.
        ' Note: double quotes are stripped from text values on save
        ' (the tuple format cannot escape them).
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "ServerName",
                .Label = "Server name",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "Default Palworld Server"
            },
            New ConfigFieldDescriptor With {
                .Key = "ServerDescription",
                .Label = "Server description",
                .FieldType = ConfigFieldType.Text
            },
            New ConfigFieldDescriptor With {
                .Key = "ServerPassword",
                .Label = "Join password",
                .Description = "Password players must enter to join. Blank = open server.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True
            },
            New ConfigFieldDescriptor With {
                .Key = "AdminPassword",
                .Label = "Admin password",
                .Description = "Grants in-game admin privileges, and is the HTTP Basic password (user 'admin') for the REST API below. Set a strong one before enabling the REST API.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True
            },
            New ConfigFieldDescriptor With {
                .Key = "ServerPlayerMaxNum",
                .Label = "Max players",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "32",
                .MinValue = 1,
                .MaxValue = 32
            },
            New ConfigFieldDescriptor With {
                .Key = "bShowPlayerList",
                .Label = "Show player list (ESC menu)",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "bIsShowJoinLeftMessage",
                .Label = "Show join/leave messages in-game",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "bIsUseBackupSaveData",
                .Label = "World save backups",
                .Description = "Server-side rolling world backups (30s/10min/hourly/daily tiers). Increases disk load; recommended on.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "PublicIP",
                .Label = "Public IP (community browser)",
                .Description = "Advertise-only: the external IP shown to the community server browser when 'List in community server browser' is on. Does not change what the server binds. Blank = auto-detect.",
                .FieldType = ConfigFieldType.Text
            },
            New ConfigFieldDescriptor With {
                .Key = "PublicPort",
                .Label = "Public port (community browser)",
                .Description = "Advertise-only: the external port shown to the community server browser. Does NOT change the listening port — that's 'Game port (UDP)' on the Configuration tab. Set this to whatever external port forwards to the game port.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "8211",
                .MinValue = 1,
                .MaxValue = 65535
            },
            New ConfigFieldDescriptor With {
                .Key = "RESTAPIEnabled",
                .Label = "Enable REST API",
                .Description = "HTTP admin API (player list, announce, save, graceful shutdown) — PowerGSM's remote-control features for Palworld will use this. Set an Admin password first; the API authenticates as 'admin' with that password.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "RESTAPIPort",
                .Label = "REST API port (TCP)",
                .Description = "TCP listening port for the REST API. Keep it firewalled from the internet — it only needs to be reachable by the PowerGSM manager.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "8212",
                .MinValue = 1024,
                .MaxValue = 65535
            }
        }
    End Function

    Public Function ReadFileToValues(editorKey As String, fileText As String) _
            As Dictionary(Of String, String) _
            Implements IInstanceFileEditorProvider.ReadFileToValues

        Dim values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        ' Live tuple if present; embedded default otherwise (blank
        ' fresh-install file, or file absent entirely).
        Dim payload = ExtractTuplePayload(fileText)
        If payload Is Nothing Then payload = DefaultOptionSettings

        Dim entries = SplitTupleEntries(payload)
        For Each entry In entries
            Dim eq = entry.IndexOf("="c)
            If eq <= 0 Then Continue For
            Dim key = entry.Substring(0, eq).Trim()
            Dim raw = entry.Substring(eq + 1).Trim()
            Select Case True
                Case key.Equals("ServerName", StringComparison.OrdinalIgnoreCase),
                     key.Equals("ServerDescription", StringComparison.OrdinalIgnoreCase),
                     key.Equals("AdminPassword", StringComparison.OrdinalIgnoreCase),
                     key.Equals("ServerPassword", StringComparison.OrdinalIgnoreCase),
                     key.Equals("PublicIP", StringComparison.OrdinalIgnoreCase)
                    values(key) = Unquote(raw)
                Case key.Equals("ServerPlayerMaxNum", StringComparison.OrdinalIgnoreCase),
                     key.Equals("PublicPort", StringComparison.OrdinalIgnoreCase),
                     key.Equals("RESTAPIPort", StringComparison.OrdinalIgnoreCase)
                    values(key) = raw
                Case key.Equals("RESTAPIEnabled", StringComparison.OrdinalIgnoreCase),
                     key.Equals("bShowPlayerList", StringComparison.OrdinalIgnoreCase),
                     key.Equals("bIsShowJoinLeftMessage", StringComparison.OrdinalIgnoreCase),
                     key.Equals("bIsUseBackupSaveData", StringComparison.OrdinalIgnoreCase)
                    values(key) = raw.ToLowerInvariant()
            End Select
        Next

        Return values
    End Function

    Public Function WriteValuesToFile(editorKey As String,
                                       values As Dictionary(Of String, String),
                                       existingText As String) As String _
            Implements IInstanceFileEditorProvider.WriteValuesToFile

        ' Base tuple: the live file's when present (all unknown keys
        ' + operator hand-edits preserved in original order), the
        ' embedded default otherwise (blank-file build).
        Dim basePayload = ExtractTuplePayload(existingText)
        Dim buildingFresh = basePayload Is Nothing
        If buildingFresh Then basePayload = DefaultOptionSettings

        Dim entries = SplitTupleEntries(basePayload)

        ' Rewrite managed keys in place, preserving position.
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For i = 0 To entries.Count - 1
            Dim eq = entries(i).IndexOf("="c)
            If eq <= 0 Then Continue For
            Dim key = entries(i).Substring(0, eq).Trim()
            Dim newRaw = RenderManagedValue(key, values)
            If newRaw IsNot Nothing Then
                entries(i) = key & "=" & newRaw
                seen.Add(key)
            End If
        Next

        ' Any managed key missing from the base tuple (older default,
        ' hand-pruned file) is appended at the end.
        For Each fld In BuildSettingsSchema()
            If Not seen.Contains(fld.Key) Then
                Dim newRaw = RenderManagedValue(fld.Key, values)
                If newRaw IsNot Nothing Then entries.Add(fld.Key & "=" & newRaw)
            End If
        Next

        Dim newLine = OptionSettingsPrefix & String.Join(",", entries) & ")"

        If Not buildingFresh Then
            ' Splice the rebuilt OptionSettings line into the original
            ' text, leaving everything around it (section header,
            ' comments, other lines) byte-for-byte intact.
            Dim span = FindTupleSpan(existingText)
            Return existingText.Substring(0, span.Item1) & newLine & existingText.Substring(span.Item2)
        End If

        ' Fresh build — complete minimal valid file.
        Return SettingsSectionHeader & Environment.NewLine & newLine & Environment.NewLine
    End Function

    ''' <summary>
    ''' Render the tuple-side value for a managed key from the form
    ''' values, or Nothing when the key isn't managed / wasn't
    ''' submitted. Text values get embedded double quotes stripped
    ''' (no escape syntax exists) then wrapped; booleans normalise
    ''' to True/False; ints validate-or-skip.
    ''' </summary>
    Private Shared Function RenderManagedValue(key As String,
                                                values As Dictionary(Of String, String)) As String
        If values Is Nothing OrElse Not values.ContainsKey(key) Then Return Nothing
        Dim raw = If(values(key), "")

        If QuotedKeys.Contains(key) Then
            Return """" & raw.Replace("""", "") & """"
        End If

        Dim boolVal As Boolean
        If Boolean.TryParse(raw, boolVal) Then
            Return If(boolVal, "True", "False")
        End If

        Dim intVal As Integer
        If Integer.TryParse(raw, intVal) Then
            Return intVal.ToString()
        End If

        Return Nothing ' unparseable — leave the existing value alone
    End Function

    ''' <summary>
    ''' Extract the payload between OptionSettings=( and its matching
    ''' close paren, or Nothing when no tuple exists (blank fresh-
    ''' install file, malformed text).
    ''' </summary>
    Private Shared Function ExtractTuplePayload(fileText As String) As String
        Dim span = FindTupleSpan(fileText)
        If span Is Nothing Then Return Nothing
        Dim payloadStart = fileText.IndexOf(OptionSettingsPrefix, StringComparison.OrdinalIgnoreCase) + OptionSettingsPrefix.Length
        Return fileText.Substring(payloadStart, span.Item2 - 1 - payloadStart)
    End Function

    ''' <summary>
    ''' Locate the full OptionSettings=(...) span in the text.
    ''' Returns (startIndex, endIndexExclusive) — endIndexExclusive is
    ''' one past the closing paren — or Nothing if absent/unbalanced.
    ''' Scanner respects nested parens and double-quoted strings.
    ''' </summary>
    Private Shared Function FindTupleSpan(fileText As String) As Tuple(Of Integer, Integer)
        If String.IsNullOrEmpty(fileText) Then Return Nothing
        Dim start = fileText.IndexOf(OptionSettingsPrefix, StringComparison.OrdinalIgnoreCase)
        If start < 0 Then Return Nothing

        Dim depth = 0
        Dim inQuotes = False
        For i = start + OptionSettingsPrefix.Length - 1 To fileText.Length - 1
            Dim c = fileText(i)
            If c = """"c Then
                inQuotes = Not inQuotes
            ElseIf Not inQuotes Then
                If c = "("c Then
                    depth += 1
                ElseIf c = ")"c Then
                    depth -= 1
                    If depth = 0 Then Return Tuple.Create(start, i + 1)
                End If
            End If
        Next
        Return Nothing ' unbalanced — treat as no tuple
    End Function

    ''' <summary>
    ''' Split a tuple payload into Key=Value entries on commas at
    ''' paren-depth 0 and outside double quotes. Quoted values may
    ''' contain commas; nested tuples (CrossplayPlatforms=(...))
    ''' stay intact as single entries.
    ''' </summary>
    Private Shared Function SplitTupleEntries(payload As String) As List(Of String)
        Dim entries As New List(Of String)
        If String.IsNullOrEmpty(payload) Then Return entries

        Dim depth = 0
        Dim inQuotes = False
        Dim segStart = 0
        For i = 0 To payload.Length - 1
            Dim c = payload(i)
            If c = """"c Then
                inQuotes = Not inQuotes
            ElseIf Not inQuotes Then
                If c = "("c Then
                    depth += 1
                ElseIf c = ")"c Then
                    depth -= 1
                ElseIf c = ","c AndAlso depth = 0 Then
                    entries.Add(payload.Substring(segStart, i - segStart).Trim())
                    segStart = i + 1
                End If
            End If
        Next
        If segStart < payload.Length Then
            entries.Add(payload.Substring(segStart).Trim())
        End If
        Return entries
    End Function

    Private Shared Function Unquote(raw As String) As String
        If raw Is Nothing Then Return ""
        Dim s = raw.Trim()
        If s.Length >= 2 AndAlso s.StartsWith("""") AndAlso s.EndsWith("""") Then
            Return s.Substring(1, s.Length - 2)
        End If
        Return s
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
