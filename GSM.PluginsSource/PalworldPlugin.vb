' <plugin id="palworld" name="Palworld Dedicated Server" version="0.3.0" author="siteml" requiresContracts="4">
' <RequiresContracts: 4>
Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports System.Threading.Tasks
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
    Implements IManagedDirectoriesProvider
    Implements IRemoteControlProvider
    Implements IStatePollProvider

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

        ' Steam query port (2026-08-29). UNDOCUMENTED by Pocketpair
        ' but widely confirmed (Steam forums, LinuxGSM users): the
        ' server initialises Steam GameServer on 27015/UDP and there
        ' is no INI key for it. Two instances on one host without
        ' this both fight for 27015 — the loser spams
        ' "CreateBoundSocket: ::bind couldn't find an open port
        ' between 27015 and 27015" every few ms (live-observed
        ' 08-28 with Palworld 1 + Palworld - stream up together).
        ' Blank/0 = don't pass the flag (game default 27015).
        Dim queryPort As Integer
        If Integer.TryParse(GetField(fields, "QueryPort"), queryPort) AndAlso queryPort > 0 Then
            args.Add($"-queryport={queryPort}")
        End If

        Dim publicLobby As Boolean
        If Boolean.TryParse(GetField(fields, "PublicLobby"), publicLobby) AndAlso publicLobby Then
            args.Add("-publiclobby")
        End If

        Return String.Join(" ", args)
    End Function

    Public Function ValidateConfig(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.ValidateConfig
        Dim errors As New List(Of String)
        Dim fields = If(config IsNot Nothing, config.CustomFields, Nothing)
        Dim port, queryPort As Integer
        If Integer.TryParse(GetField(fields, "Port"), port) AndAlso
           Integer.TryParse(GetField(fields, "QueryPort"), queryPort) AndAlso
           port > 0 AndAlso queryPort > 0 AndAlso port = queryPort Then
            errors.Add("Steam query port must differ from the game port.")
        End If
        Return errors
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
                .Key = "QueryPort",
                .Label = "Steam query port (UDP)",
                .Description = "Steam server-browser/query port (-queryport=). Palworld defaults to 27015 and does NOT expose this in PalWorldSettings.ini; every Palworld instance on the same node needs its own value or the second one floods its console with 'CreateBoundSocket: ::bind couldn't find an open port between 27015 and 27015'. Undocumented switch, but confirmed working by the community. PowerGSM allocates and clash-checks it across all instances on the node.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "27015",
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
        ' a redirected pipe only sees stray CRT/stderr output.
        '
        ' ConPTY (0.3.0, EventStream Slice 4): Windows capture now
        ' comes from the pseudoconsole spawn — GetLaunchOptions sets
        ' NeedsPseudoConsole, the node gives the game a console it
        ' can ALSO read, and the console text flows into the log
        ' buffer + parse pipeline as SourceId "conpty". The OLD
        ' Windows entry here — an inert FileLogSource whose path
        ' never exists — existed purely to force the hidden-console
        ' spawn; it is DELETED because (a) NeedsPseudoConsole now
        ' states that intent directly, and (b) a declared file log
        ' would switch CaptureStdout off under the node's
        ' capture rule, silencing the pty text.
        '
        ' Windows: no sources — the ConPTY stream is the log surface.
        ' Linux: stdout capture — no console/stdio split there, the
        '   server's output goes to real stdout. VERIFY on the
        '   linux-test node with the Linux run.
        Select Case If(config IsNot Nothing, config.Platform, NodePlatform.Unknown)
            Case NodePlatform.Linux
                Return New ILogSource() {
                    New StdoutLogSource()
                }
            Case Else
                Return New ILogSource() {}
        End Select
    End Function

    Public Function GetLogParseRules() As IReadOnlyList(Of LogParseRule) Implements IGamePlugin.GetLogParseRules
        ' C4b (2026-08-11): authored from LIVE ConPTY captures. PalServer's
        ' console (post spawn-fix) logs everything player-shaped:
        '
        '   [2026-08-11 21:06:07] [LOG] site_ml 98.215.196.176 connected the server. (User id: steam_76561198198302619)
        '   [LOG] Etise joined the server. (User id: steam_76561198198302619, Player id: C4409177000000000000000000000000)
        '   [CHAT] <Etise> testing
        '   [LOG] Etise left the server. (User id: steam_76561198198302619)
        '
        ' The [YYYY-MM-DD hh:mm:ss] prefix is PALWORLD'S OWN (boot lines
        ' lack it; [LOG]/[CHAT] lines carry it) — the optional leading
        ' group tolerates both. (First live run failed on exactly this:
        ' the prefix was misread as viewer decoration in the captures.)
        '
        ' Two-stage join, mapped onto the EventStore's progressive-
        ' enrichment fold: the CONNECT line carries the platform persona +
        ' IP + platform user id, the JOINED line adds the character
        ' DisplayName + CharacterId. The JOINED line CREATES the session
        ' (kind=PlayerJoin — exactly one Join event/History row per
        ' player); the CONNECT line is kind=PlayerIdentity (double-Join
        ' fix, 2026-08-13): enrich-or-stash only — its persona/IP/
        ' platform stash by PlatformUserId and drain into the session
        ' when pal-join creates it. A client that connects but never
        ' completes join produces no session and no Join event (by
        ' design). Leave correlates by PlatformUserId (strong key). Chat
        ' carries the character name — FindExistingSession resolves via
        ' DisplayName.
        '
        ' These rules fire on the conpty stream on Windows and the stdout
        ' stream on Linux (same console text, different capture source).
        ' With console rules present, the node demotes pal-rest to FALLBACK
        ' authority (console-first flip, same date) — see GetStatePollSources.
        '
        ' Platform split (2026-08-12): Palworld's console User id is
        ' "steam_7656..." / "xbox_..." — the rules split the prefix into
        ' a Platform capture and keep the BARE id as PlatformUserId,
        ' matching the pal-rest poller's PlatformPrefixField semantics
        ' (so console and REST eras key sessions identically, and the
        ' Manager's Platform column populates). The prefix group is
        ' optional: an unprefixed id lands whole in PlatformUserId.
        Return New LogParseRule() {
            New LogParseRule With {
                .Name = "pal-connect",
                .Kind = ParsedEventKind.PlayerIdentity,
                .Pattern = "^\s*(?:\[\d{4}-\d{2}-\d{2}[^\]]*\]\s*)?\[LOG\]\s+(?<PlatformPersona>.+?)\s+(?<RemoteAddress>\d{1,3}(?:\.\d{1,3}){3})\s+connected the server\.\s+\(User id:\s*(?:(?<Platform>[A-Za-z][A-Za-z0-9]*)_)?(?<PlatformUserId>[^)]+)\)"
            },
            New LogParseRule With {
                .Name = "pal-join",
                .Kind = ParsedEventKind.PlayerJoin,
                .Pattern = "^\s*(?:\[\d{4}-\d{2}-\d{2}[^\]]*\]\s*)?\[LOG\]\s+(?<DisplayName>.+?)\s+joined the server\.\s+\(User id:\s*(?:(?<Platform>[A-Za-z][A-Za-z0-9]*)_)?(?<PlatformUserId>[^,)]+),\s*Player id:\s*(?<CharacterId>[^)]+)\)"
            },
            New LogParseRule With {
                .Name = "pal-leave",
                .Kind = ParsedEventKind.PlayerLeave,
                .Pattern = "^\s*(?:\[\d{4}-\d{2}-\d{2}[^\]]*\]\s*)?\[LOG\]\s+(?<DisplayName>.+?)\s+left the server\.\s+\(User id:\s*(?:(?<Platform>[A-Za-z][A-Za-z0-9]*)_)?(?<PlatformUserId>[^)]+)\)"
            },
            New LogParseRule With {
                .Name = "pal-chat",
                .Kind = ParsedEventKind.ChatMessage,
                .Pattern = "^\s*(?:\[\d{4}-\d{2}-\d{2}[^\]]*\]\s*)?\[CHAT\]\s+<(?<DisplayName>[^>]+)>\s(?<Message>.*)$"
            }
        }
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
            .GracefulShutdownTimeoutMs = 60000,
            .NeedsPseudoConsole = True
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
    Private Const WorldEditorKey As String = "palworld-world"
    Private Const OptionSettingsPrefix As String = "OptionSettings=("
    Private Const SettingsSectionHeader As String = "[/Script/Pal.PalGameWorldSettings]"

    ' Full default OptionSettings payload (the text between the
    ' outer parens), captured verbatim from DefaultPalWorldSettings.ini
    ' of a live install, 12 Jul 2026. Fallback skeleton only.
    Private Const DefaultOptionSettings As String =
        "Difficulty=None,RandomizerType=None,RandomizerSeed="""",bIsRandomizerPalLevelRandom=False,DayTimeSpeedRate=1.000000,NightTimeSpeedRate=1.000000,ExpRate=1.000000,PalCaptureRate=1.000000,PalSpawnNumRate=1.000000,PalDamageRateAttack=1.000000,PalDamageRateDefense=1.000000,PlayerDamageRateAttack=1.000000,PlayerDamageRateDefense=1.000000,PlayerStomachDecreaceRate=1.000000,PlayerStaminaDecreaceRate=1.000000,PlayerAutoHPRegeneRate=1.000000,PlayerAutoHpRegeneRateInSleep=1.000000,PalStomachDecreaceRate=1.000000,PalStaminaDecreaceRate=1.000000,PalAutoHPRegeneRate=1.000000,PalAutoHpRegeneRateInSleep=1.000000,BuildObjectHpRate=1.000000,BuildObjectDamageRate=1.000000,BuildObjectDeteriorationDamageRate=1.000000,CollectionDropRate=1.000000,CollectionObjectHpRate=1.000000,CollectionObjectRespawnSpeedRate=1.000000,EnemyDropItemRate=1.000000,DeathPenalty=Item,bEnablePlayerToPlayerDamage=False,bEnableFriendlyFire=False,bEnableInvaderEnemy=True,bActiveUNKO=False,bEnableAimAssistPad=True,bEnableAimAssistKeyboard=False,DropItemMaxNum=3000,PhysicsActiveDropItemMaxNum=-1,DropItemMaxNum_UNKO=100,BaseCampMaxNum=128,BaseCampWorkerMaxNum=15,DropItemAliveMaxHours=1.000000,bAutoResetGuildNoOnlinePlayers=False,AutoResetGuildTimeNoOnlinePlayers=72.000000,GuildPlayerMaxNum=20,BaseCampMaxNumInGuild=4,PalEggDefaultHatchingTime=1.000000,WorkSpeedRate=1.000000,AutoSaveSpan=30.000000,bIsMultiplay=False,bIsPvP=False,bHardcore=False,bPalLost=False,bCharacterRecreateInHardcore=False,bCanPickupOtherGuildDeathPenaltyDrop=False,bEnableNonLoginPenalty=True,bEnableFastTravel=True,bEnableFastTravelOnlyBaseCamp=False,bIsStartLocationSelectByMap=False,bExistPlayerAfterLogout=False,bEnableDefenseOtherGuildPlayer=False,bInvisibleOtherGuildBaseCampAreaFX=False,bBuildAreaLimit=False,ItemWeightRate=1.000000,CoopPlayerMaxNum=4,ServerPlayerMaxNum=32,ServerName=""Default Palworld Server"",ServerDescription="""",AdminPassword="""",ServerPassword="""",bAllowClientMod=True,PublicPort=8211,PublicIP="""",RCONEnabled=False,RCONPort=25575,Region="""",bUseAuth=True,BanListURL=""https://b.palworldgame.com/api/banlist.txt"",RESTAPIEnabled=False,RESTAPIPort=8212,bShowPlayerList=False,ChatPostLimitPerMinute=30,CrossplayPlatforms=(Steam,Xbox,PS5,Mac),bIsUseBackupSaveData=True,LogFormatType=Text,bIsShowJoinLeftMessage=True,SupplyDropSpan=180,EnablePredatorBossPal=True,MaxBuildingLimitNum=0,ServerReplicatePawnCullDistance=15000.000000,bAllowGlobalPalboxExport=True,bAllowGlobalPalboxImport=False,EquipmentDurabilityDamageRate=1.000000,ItemContainerForceMarkDirtyInterval=1.000000,PlayerDataPalStorageUpdateCheckTickInterval=1.000000,ItemCorruptionMultiplier=1.000000,MonsterFarmActionSpeedRate=1.000000,DenyTechnologyList=,GuildRejoinCooldownMinutes=0,AutoTransferMasterCheckIntervalSeconds=3600.000000,AutoTransferMasterThresholdDays=14,MaxGuildsPerFrame=10,BlockRespawnTime=5.000000,RespawnPenaltyDurationThreshold=0.000000,RespawnPenaltyTimeScale=2.000000,bDisplayPvPItemNumOnWorldMap_BaseCamp=False,bDisplayPvPItemNumOnWorldMap_Player=False,AdditionalDropItemWhenPlayerKillingInPvPMode=""PlayerDropItem"",AdditionalDropItemNumWhenPlayerKillingInPvPMode=1,bAdditionalDropItemWhenPlayerKillingInPvPMode=False,bEnableVoiceChat=False,VoiceChatMaxVolumeDistance=3000.000000,VoiceChatZeroVolumeDistance=15000.000000,bAllowEnhanceStat_Health=True,bAllowEnhanceStat_Attack=True,bAllowEnhanceStat_Stamina=True,bAllowEnhanceStat_Weight=True,bAllowEnhanceStat_WorkSpeed=True,bEnableBuildingPlayerUIdDisplay=False,BuildingNameDisplayCacheTTLSeconds=60"

    ' Managed keys and how each serialises back into the tuple.
    ' Quoted = Text/Password (write wrapped in double quotes);
    ' Float = written as a fixed 6-decimal number (the format the
    ' game itself writes); everything else writes bare (ints,
    ' True/False, enum tokens).
    Private Shared ReadOnly QuotedKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "ServerName", "ServerDescription", "AdminPassword", "ServerPassword", "PublicIP", "RandomizerSeed"
    }

    ' Rate/multiplier keys. There is no float ConfigFieldType, so
    ' these render as Text and are validated here on save
    ' (invariant-culture decimal, written as F6). An unparseable
    ' entry is left at its existing value rather than corrupting
    ' the tuple.
    Private Shared ReadOnly FloatKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "DayTimeSpeedRate", "NightTimeSpeedRate", "ExpRate", "PalCaptureRate", "PalSpawnNumRate",
        "PalDamageRateAttack", "PalDamageRateDefense", "PlayerDamageRateAttack", "PlayerDamageRateDefense",
        "PlayerStomachDecreaceRate", "PlayerStaminaDecreaceRate", "PlayerAutoHPRegeneRate", "PlayerAutoHpRegeneRateInSleep",
        "PalStomachDecreaceRate", "PalStaminaDecreaceRate", "PalAutoHPRegeneRate", "PalAutoHpRegeneRateInSleep",
        "BuildObjectHpRate", "BuildObjectDamageRate", "BuildObjectDeteriorationDamageRate",
        "CollectionDropRate", "CollectionObjectHpRate", "CollectionObjectRespawnSpeedRate", "EnemyDropItemRate",
        "PalEggDefaultHatchingTime", "WorkSpeedRate", "ItemWeightRate", "AutoSaveSpan", "DropItemAliveMaxHours",
        "AutoResetGuildTimeNoOnlinePlayers", "EquipmentDurabilityDamageRate",
        "ItemCorruptionMultiplier", "MonsterFarmActionSpeedRate", "BlockRespawnTime",
        "RespawnPenaltyDurationThreshold", "RespawnPenaltyTimeScale", "ServerReplicatePawnCullDistance"
    }

    ''' <summary>Both editor schemas keyed by editor key.</summary>
    Private Shared Function SchemaForEditor(editorKey As String) As IReadOnlyList(Of ConfigFieldDescriptor)
        If String.Equals(editorKey, WorldEditorKey, StringComparison.OrdinalIgnoreCase) Then
            Return BuildWorldSchema()
        End If
        Return BuildSettingsSchema()
    End Function

    Public Function GetInstanceFileEditors(config As InstanceConfig) _
            As IReadOnlyList(Of InstanceFileEditor) _
            Implements IInstanceFileEditorProvider.GetInstanceFileEditors
        Dim isLinux = config IsNot Nothing AndAlso config.Platform = NodePlatform.Linux
        Dim relPath = If(isLinux,
            "Pal/Saved/Config/LinuxServer/PalWorldSettings.ini",
            "Pal/Saved/Config/WindowsServer/PalWorldSettings.ini")
        ' Two tabs, ONE file. Each tab manages only its own schema's
        ' keys and round-trips everything else untouched, so the
        ' splices compose. The Manager's editor panel re-reads the
        ' file right before each save (2026-08-29) so saving one tab
        ' after the other never reverts the first tab's keys.
        Return New InstanceFileEditor() {
            New InstanceFileEditor With {
                .Key = SettingsEditorKey,
                .TabTitle = "Server Settings",
                .RelativePath = relPath,
                .Schema = BuildSettingsSchema(),
                .RequiresExistingFile = False
            },
            New InstanceFileEditor With {
                .Key = WorldEditorKey,
                .TabTitle = "World Settings",
                .RelativePath = relPath,
                .Schema = BuildWorldSchema(),
                .RequiresExistingFile = False
            }
        }
    End Function

    Private Shared Function BuildSettingsSchema() As IReadOnlyList(Of ConfigFieldDescriptor)
        ' Server/admin management set. Gameplay/world tuning lives on
        ' the World Settings tab (BuildWorldSchema). Official per-key
        ' reference: docs.palworldgame.com/settings-and-operation/configuration.
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

    ' Small builders keep BuildWorldSchema readable.
    Private Shared Function Rate(key As String, label As String, Optional desc As String = Nothing, Optional def As String = "1.000000") As ConfigFieldDescriptor
        Return New ConfigFieldDescriptor With {
            .Key = key, .Label = label, .Description = desc,
            .FieldType = ConfigFieldType.Text, .DefaultValue = def,
            .ValidationRegex = "^-?\d+(\.\d+)?$"
        }
    End Function

    Private Shared Function Flag(key As String, label As String, def As Boolean, Optional desc As String = Nothing) As ConfigFieldDescriptor
        Return New ConfigFieldDescriptor With {
            .Key = key, .Label = label, .Description = desc,
            .FieldType = ConfigFieldType.BooleanField,
            .DefaultValue = If(def, "true", "false")
        }
    End Function

    Private Shared Function IntField(key As String, label As String, def As Integer, min As Integer, max As Integer, Optional desc As String = Nothing) As ConfigFieldDescriptor
        Return New ConfigFieldDescriptor With {
            .Key = key, .Label = label, .Description = desc,
            .FieldType = ConfigFieldType.IntegerField,
            .DefaultValue = def.ToString(), .MinValue = min, .MaxValue = max
        }
    End Function

    Private Shared Function Section(title As String, body As String) As ConfigFieldDescriptor
        Return New ConfigFieldDescriptor With {
            .Key = "notice-" & title.ToLowerInvariant().Replace(" ", "-"),
            .Label = title, .Description = body,
            .FieldType = ConfigFieldType.Notice
        }
    End Function

    ''' <summary>
    ''' World Settings tab: the gameplay/tuning half of OptionSettings
    ''' (rates, PvP, guilds, building, hardcore, randomizer, QoL
    ''' toggles). Every key here is written bare except the quoted
    ''' RandomizerSeed; rates are validated as decimals on save. A
    ''' server restart is required for changes to apply — the game
    ''' reads the INI once at boot.
    ''' </summary>
    Private Shared Function BuildWorldSchema() As IReadOnlyList(Of ConfigFieldDescriptor)
        Return New ConfigFieldDescriptor() {
            Section("Restart required",
                    "Palworld reads PalWorldSettings.ini once at boot. Save here, then restart the instance for changes to take effect. Rates are multipliers (1.0 = default)."),
            Section("Progression rates", "Multipliers for player and pal growth."),
            Rate("ExpRate", "EXP rate"),
            Rate("PalCaptureRate", "Pal capture rate"),
            Rate("PalSpawnNumRate", "Pal spawn count rate"),
            Rate("WorkSpeedRate", "Work speed rate"),
            Rate("PalEggDefaultHatchingTime", "Egg hatching time (hours)", "Base incubation time for the largest eggs; smaller eggs scale down. 0 = instant."),
            Rate("ItemWeightRate", "Item weight rate"),
            Rate("EnemyDropItemRate", "Enemy drop rate"),
            Rate("CollectionDropRate", "Gathering drop rate"),
            Rate("CollectionObjectHpRate", "Gatherable object HP rate"),
            Rate("CollectionObjectRespawnSpeedRate", "Gatherable respawn speed rate"),
            Rate("MonsterFarmActionSpeedRate", "Ranch production speed rate", "Item production speed multiplier from grazing pals."),
            Section("Day / night", "Time-of-day speed."),
            Rate("DayTimeSpeedRate", "Day speed rate"),
            Rate("NightTimeSpeedRate", "Night speed rate"),
            Section("Damage & survival", "Combat and hunger/stamina tuning."),
            Rate("PlayerDamageRateAttack", "Player damage dealt"),
            Rate("PlayerDamageRateDefense", "Player damage taken"),
            Rate("PalDamageRateAttack", "Pal damage dealt"),
            Rate("PalDamageRateDefense", "Pal damage taken"),
            Rate("PlayerStomachDecreaceRate", "Player hunger rate"),
            Rate("PlayerStaminaDecreaceRate", "Player stamina drain rate"),
            Rate("PlayerAutoHPRegeneRate", "Player HP regen rate"),
            Rate("PlayerAutoHpRegeneRateInSleep", "Player HP regen rate (sleeping)"),
            Rate("PalStomachDecreaceRate", "Pal hunger rate"),
            Rate("PalStaminaDecreaceRate", "Pal stamina drain rate"),
            Rate("PalAutoHPRegeneRate", "Pal HP regen rate"),
            Rate("PalAutoHpRegeneRateInSleep", "Pal HP regen rate (sleeping)"),
            Rate("EquipmentDurabilityDamageRate", "Equipment durability loss rate"),
            Rate("ItemCorruptionMultiplier", "Food spoilage rate", "Item corruption (food decay) speed multiplier. Lower = food lasts longer; 0 disables spoilage."),
            Flag("bEnableInvaderEnemy", "Enable raids on bases", True),
            Flag("EnablePredatorBossPal", "Enable predator boss pals", True),
            Section("Death & PvP", "What happens on death, and player-versus-player rules."),
            New ConfigFieldDescriptor With {
                .Key = "DeathPenalty",
                .Label = "Death penalty",
                .Description = "None = keep everything; Item = drop items except equipment; ItemAndEquipment = drop items and equipment; All = drop items, equipment and pals.",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"None", "Item", "ItemAndEquipment", "All"},
                .DefaultValue = "Item"
            },
            Flag("bIsPvP", "PvP mode", False),
            Flag("bEnablePlayerToPlayerDamage", "Player-to-player damage", False),
            Flag("bEnableFriendlyFire", "Friendly fire", False),
            Flag("bCanPickupOtherGuildDeathPenaltyDrop", "Others can loot death drops", False, "Allow players outside the guild to pick up another player's death-penalty drop."),
            Flag("bEnableDefenseOtherGuildPlayer", "Pals defend against other guilds", False),
            Flag("bEnableNonLoginPenalty", "Non-login penalty", True),
            Rate("BlockRespawnTime", "Respawn cooldown (seconds)", "Cooldown before you can respawn after death.", "5.000000"),
            Rate("RespawnPenaltyDurationThreshold", "Respawn penalty threshold (seconds)", "Dying again within this window scales the cooldown by the factor below. 0 = off.", "0.000000"),
            Rate("RespawnPenaltyTimeScale", "Respawn penalty scale", Nothing, "2.000000"),
            Section("Hardcore", "Permadeath-style rules. bPalLost applies with or without hardcore."),
            Flag("bHardcore", "Hardcore mode", False, "Characters are deleted on death."),
            Flag("bPalLost", "Pals are lost on death", False),
            Flag("bCharacterRecreateInHardcore", "Allow re-creating a character after hardcore death", False),
            Section("Building & bases", "Base and structure limits."),
            IntField("BaseCampMaxNum", "Max bases on the server", 128, 1, 4096),
            IntField("BaseCampMaxNumInGuild", "Max bases per guild", 4, 1, 64),
            IntField("BaseCampWorkerMaxNum", "Max working pals per base", 15, 1, 50),
            IntField("MaxBuildingLimitNum", "Max buildings per player (0 = unlimited)", 0, 0, 100000),
            Flag("bBuildAreaLimit", "Restrict building near bases/structures", False),
            Rate("BuildObjectHpRate", "Structure HP rate"),
            Rate("BuildObjectDamageRate", "Structure damage taken rate"),
            Rate("BuildObjectDeteriorationDamageRate", "Structure decay rate"),
            Flag("bInvisibleOtherGuildBaseCampAreaFX", "Hide other guilds' base-area effects", False),
            Flag("bEnableBuildingPlayerUIdDisplay", "Show builder name on structures", False),
            Section("Guilds", "Guild membership and cleanup."),
            IntField("GuildPlayerMaxNum", "Max players per guild", 20, 1, 100),
            IntField("CoopPlayerMaxNum", "Co-op player limit", 4, 1, 32, "Only meaningful for co-op sessions; dedicated servers use Max players on the Server Settings tab."),
            IntField("GuildRejoinCooldownMinutes", "Guild rejoin cooldown (minutes)", 0, 0, 100000),
            Flag("bAutoResetGuildNoOnlinePlayers", "Auto-reset guilds with no online players", False),
            Rate("AutoResetGuildTimeNoOnlinePlayers", "Guild auto-reset after (hours)", Nothing, "72.000000"),
            Section("Items & world", "Dropped items, saving, supply drops."),
            IntField("DropItemMaxNum", "Max dropped items in world", 3000, 0, 100000),
            Rate("DropItemAliveMaxHours", "Dropped items despawn after (hours)"),
            Rate("AutoSaveSpan", "Auto-save interval (seconds)", Nothing, "30.000000"),
            IntField("SupplyDropSpan", "Supply drop interval (minutes)", 180, 1, 100000),
            Rate("ServerReplicatePawnCullDistance", "Pal sync distance (cm)", "How far from players pals are replicated. 5000–15000.", "15000.000000"),
            Flag("bExistPlayerAfterLogout", "Keep player body in world after logout", False),
            Flag("bEnableFastTravel", "Enable fast travel", True),
            Flag("bEnableFastTravelOnlyBaseCamp", "Fast travel only to bases", False),
            Flag("bIsStartLocationSelectByMap", "Choose start location on map", False),
            Flag("bAllowGlobalPalboxExport", "Allow global Palbox export", True),
            Flag("bAllowGlobalPalboxImport", "Allow global Palbox import", False),
            Flag("bAllowClientMod", "Allow client mods", True),
            Flag("bEnableVoiceChat", "Enable proximity voice chat", False),
            IntField("ChatPostLimitPerMinute", "Chat posts per minute limit", 30, 1, 1000),
            Section("Stat enhancement", "Which stats players may raise with stat points."),
            Flag("bAllowEnhanceStat_Health", "Allow HP enhancement", True),
            Flag("bAllowEnhanceStat_Attack", "Allow attack enhancement", True),
            Flag("bAllowEnhanceStat_Stamina", "Allow stamina enhancement", True),
            Flag("bAllowEnhanceStat_Weight", "Allow weight enhancement", True),
            Flag("bAllowEnhanceStat_WorkSpeed", "Allow work-speed enhancement", True),
            Section("Randomizer", "Pal-spawn randomizer. Changing type or seed on an existing world changes what spawns."),
            New ConfigFieldDescriptor With {
                .Key = "RandomizerType",
                .Label = "Randomizer type",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"None", "Region", "All"},
                .DefaultValue = "None"
            },
            New ConfigFieldDescriptor With {
                .Key = "RandomizerSeed",
                .Label = "Randomizer seed",
                .Description = "Blank = random seed each start.",
                .FieldType = ConfigFieldType.Text
            },
            Flag("bIsRandomizerPalLevelRandom", "Randomize pal levels", False)
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

        ' Schema-driven: only this editor's keys are surfaced, typed
        ' by the descriptor (quoted text unwrapped, booleans
        ' lower-cased for the form, everything else verbatim).
        Dim schema As New Dictionary(Of String, ConfigFieldDescriptor)(StringComparer.OrdinalIgnoreCase)
        For Each fld In SchemaForEditor(editorKey)
            If fld Is Nothing OrElse fld.FieldType = ConfigFieldType.Notice OrElse String.IsNullOrEmpty(fld.Key) Then Continue For
            schema(fld.Key) = fld
        Next

        Dim entries = SplitTupleEntries(payload)
        For Each entry In entries
            Dim eq = entry.IndexOf("="c)
            If eq <= 0 Then Continue For
            Dim key = entry.Substring(0, eq).Trim()
            Dim raw = entry.Substring(eq + 1).Trim()
            Dim fld As ConfigFieldDescriptor = Nothing
            If Not schema.TryGetValue(key, fld) Then Continue For
            Select Case fld.FieldType
                Case ConfigFieldType.Text, ConfigFieldType.Password
                    values(fld.Key) = If(QuotedKeys.Contains(key), Unquote(raw), raw)
                Case ConfigFieldType.BooleanField
                    values(fld.Key) = raw.ToLowerInvariant()
                Case Else
                    values(fld.Key) = raw
            End Select
        Next

        Return values
    End Function

    Public Function WriteValuesToFile(editorKey As String,
                                       values As Dictionary(Of String, String),
                                       existingText As String) As String _
            Implements IInstanceFileEditorProvider.WriteValuesToFile

        ' Base tuple: the live file's when present (all unknown keys
        ' + operator hand-edits + the OTHER tab's keys preserved in
        ' original order), the embedded default otherwise (blank-
        ' file build).
        Dim basePayload = ExtractTuplePayload(existingText)
        Dim buildingFresh = basePayload Is Nothing
        If buildingFresh Then basePayload = DefaultOptionSettings

        Dim entries = SplitTupleEntries(basePayload)

        ' Only THIS editor's keys are managed on this save; a value
        ' the form didn't submit (or that fails validation) leaves
        ' the existing entry alone.
        Dim schema As New Dictionary(Of String, ConfigFieldDescriptor)(StringComparer.OrdinalIgnoreCase)
        For Each fld In SchemaForEditor(editorKey)
            If fld Is Nothing OrElse fld.FieldType = ConfigFieldType.Notice OrElse String.IsNullOrEmpty(fld.Key) Then Continue For
            schema(fld.Key) = fld
        Next

        ' Rewrite managed keys in place, preserving position.
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For i = 0 To entries.Count - 1
            Dim eq = entries(i).IndexOf("="c)
            If eq <= 0 Then Continue For
            Dim key = entries(i).Substring(0, eq).Trim()
            Dim fld As ConfigFieldDescriptor = Nothing
            If Not schema.TryGetValue(key, fld) Then Continue For
            Dim newRaw = RenderManagedValue(fld, values)
            If newRaw IsNot Nothing Then
                entries(i) = key & "=" & newRaw
            End If
            seen.Add(key)
        Next

        ' Any managed key missing from the base tuple (older default,
        ' hand-pruned file) is appended at the end.
        For Each fld In schema.Values
            If Not seen.Contains(fld.Key) Then
                Dim newRaw = RenderManagedValue(fld, values)
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
    ''' values, or Nothing when the key wasn't submitted or fails
    ''' validation (the existing entry is then kept). Text values
    ''' get embedded double quotes stripped (no escape syntax exists)
    ''' then wrapped; rates parse as invariant decimals and write as
    ''' F6; booleans normalise to True/False; ints validate-or-skip;
    ''' enums must be one of the declared values.
    ''' </summary>
    Private Shared Function RenderManagedValue(fld As ConfigFieldDescriptor,
                                                values As Dictionary(Of String, String)) As String
        If fld Is Nothing OrElse values Is Nothing OrElse Not values.ContainsKey(fld.Key) Then Return Nothing
        Dim raw = If(values(fld.Key), "").Trim()

        If QuotedKeys.Contains(fld.Key) Then
            Return """" & raw.Replace("""", "") & """"
        End If

        If FloatKeys.Contains(fld.Key) Then
            Dim dbl As Double
            If Double.TryParse(raw, Globalization.NumberStyles.Float,
                               Globalization.CultureInfo.InvariantCulture, dbl) Then
                Return dbl.ToString("F6", Globalization.CultureInfo.InvariantCulture)
            End If
            Return Nothing
        End If

        Select Case fld.FieldType
            Case ConfigFieldType.BooleanField
                Dim boolVal As Boolean
                If Boolean.TryParse(raw, boolVal) Then Return If(boolVal, "True", "False")
                Return Nothing
            Case ConfigFieldType.IntegerField
                Dim intVal As Integer
                If Integer.TryParse(raw, intVal) Then Return intVal.ToString()
                Return Nothing
            Case ConfigFieldType.[Enum]
                If fld.EnumValues IsNot Nothing Then
                    For Each v In fld.EnumValues
                        If String.Equals(v, raw, StringComparison.OrdinalIgnoreCase) Then Return v
                    Next
                    Return Nothing
                End If
                Return raw
            Case Else
                ' Unquoted free text (none today) — write bare.
                Return raw
        End Select
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
    '  IManagedDirectoriesProvider (SLICE 5, revised)
    '
    '  World saves are NOT surfaced. Pal/Saved/SaveGames is a
    '  nested tree (0/<worldid>/{Level.sav, LevelMeta.sav,
    '  Players/, backup/}) and the node's managed-files listing is
    '  flat (Directory.EnumerateFiles, top level only) — the tab
    '  showed permanently blank against the tree (verified 13 Jul
    '  2026). Proper saves support is a future slice: SDV-style
    '  archive/restore packing a whole world into a flat backups
    '  dir (IFileGenerationProvider pattern) — better for whole-
    '  world download/migration than a recursive file list anyway.
    '  See Palworld_Plugin_Plan.md.
    '
    '  Config dir surfaced read-only for debugging convenience
    '  (grab PalWorldSettings.ini without shell access); edits go
    '  through the Server Settings tab.
    ' ============================================================

    Public Function GetManagedDirectories(config As InstanceConfig) As IReadOnlyList(Of ManagedDirectory) Implements IManagedDirectoriesProvider.GetManagedDirectories
        Dim isLinux = config IsNot Nothing AndAlso config.Platform = NodePlatform.Linux
        Dim cfgDir = If(isLinux, "Pal/Saved/Config/LinuxServer", "Pal/Saved/Config/WindowsServer")
        Return New ManagedDirectory() {
            New ManagedDirectory With {
                .RelativePath = cfgDir,
                .DisplayName = "Config files",
                .Permissions = DirPermissions.Read
            }
        }
    End Function

    ' ============================================================
    '  IRemoteControlProvider — REST API client (SLICE 4)
    '
    '  Palworld's sanctioned admin channel (RCON deprecated). HTTP
    '  Basic auth, user 'admin', password = AdminPassword from the
    '  game's own settings file — fetched live via
    '  ctx.FetchInstanceFile so nothing is duplicated into
    '  PowerGSM's database and a password change applies on the
    '  next call. Both methods decline fast (False / Nothing) when
    '  the REST API is disabled, unconfigured, or unreachable —
    '  the Manager's normal paths then take over.
    '
    '  Stop flow: /announce (heads-up to players) → /save (belt +
    '  braces; /shutdown saves too) → /shutdown waittime=1. The
    '  Manager then runs its normal node stop path regardless —
    '  the node sets stop-intent (no crash misclassification) and
    '  its CtrlC/force-kill ladder covers a hung game. CTRL_C
    '  against a REST-initiated exiting process is harmless.
    ' ============================================================

    ' One client for all instances of this plugin; generous
    ' HttpClient timeout — real deadlines come from the caller's
    ' CancellationToken (Manager caps the whole stop hook at 10s).
    Private Shared ReadOnly _http As New HttpClient() With {
        .Timeout = TimeSpan.FromSeconds(30)
    }

    Private Class RestSettings
        Public Enabled As Boolean
        Public Port As Integer
        Public AdminPassword As String
    End Class

    ' Per-instance settings cache (30s TTL). GetPlayersAsync gets
    ' polled every few seconds by the UI; without this every tick
    ' would round-trip the settings file through the node before
    ' even reaching the REST call. 30s keeps an operator's REST
    ' toggle / password change near-immediate (and those need a
    ' server restart to take effect anyway).
    Private Shared ReadOnly _restSettingsCache As New ConcurrentDictionary(Of String, Tuple(Of DateTime, RestSettings))

    ''' <summary>
    ''' Read RESTAPIEnabled / RESTAPIPort / AdminPassword out of the
    ''' live settings file on the node (30s cache). Nothing = can't
    ''' determine (file unreadable/absent) — treat as REST
    ''' unavailable. Negative results are cached too, so a game
    ''' with REST off doesn't re-fetch the file every poll tick.
    ''' </summary>
    Private Shared Async Function ReadRestSettingsAsync(ctx As RemoteControlContext) As Task(Of RestSettings)
        If ctx Is Nothing OrElse ctx.FetchInstanceFile Is Nothing Then Return Nothing

        Dim cacheKey = If(ctx.Config?.InstanceId, "")
        Dim cached As Tuple(Of DateTime, RestSettings) = Nothing
        If cacheKey.Length > 0 AndAlso _restSettingsCache.TryGetValue(cacheKey, cached) AndAlso
           (DateTime.UtcNow - cached.Item1) < TimeSpan.FromSeconds(30) Then
            Return cached.Item2
        End If

        Dim result = Await ReadRestSettingsCoreAsync(ctx.Config, ctx.FetchInstanceFile)

        If cacheKey.Length > 0 Then
            _restSettingsCache(cacheKey) = Tuple.Create(DateTime.UtcNow, result)
        End If
        Return result
    End Function

    ''' <summary>
    ''' Uncached fetch+parse of RESTAPIEnabled / RESTAPIPort /
    ''' AdminPassword from the live settings file. Shared by the
    ''' remote-control path (30s-cached wrapper above, polled by the
    ''' UI) and the state-poll declaration (called once per instance
    ''' start — no cache wanted: a stale negative there would cost a
    ''' whole game run of polling).
    ''' </summary>
    Private Shared Async Function ReadRestSettingsCoreAsync(config As InstanceConfig,
                                                            fetchFile As Func(Of String, Task(Of String))) _
            As Task(Of RestSettings)
        If fetchFile Is Nothing Then Return Nothing

        Dim isLinux = config IsNot Nothing AndAlso config.Platform = NodePlatform.Linux
        Dim relPath = If(isLinux,
            "Pal/Saved/Config/LinuxServer/PalWorldSettings.ini",
            "Pal/Saved/Config/WindowsServer/PalWorldSettings.ini")

        Dim text = Await fetchFile(relPath)
        Dim payload = ExtractTuplePayload(text)

        Dim result As RestSettings = Nothing
        If payload IsNot Nothing Then
            result = New RestSettings With {.Port = 8212, .AdminPassword = ""}
            For Each entry In SplitTupleEntries(payload)
                Dim eq = entry.IndexOf("="c)
                If eq <= 0 Then Continue For
                Dim key = entry.Substring(0, eq).Trim()
                Dim raw = entry.Substring(eq + 1).Trim()
                If key.Equals("RESTAPIEnabled", StringComparison.OrdinalIgnoreCase) Then
                    Boolean.TryParse(raw, result.Enabled)
                ElseIf key.Equals("RESTAPIPort", StringComparison.OrdinalIgnoreCase) Then
                    Integer.TryParse(raw, result.Port)
                ElseIf key.Equals("AdminPassword", StringComparison.OrdinalIgnoreCase) Then
                    result.AdminPassword = Unquote(raw)
                End If
            Next
        End If
        Return result
    End Function

    ' ============================================================
    '  IStatePollProvider — node-side player polling (EventStream
    '  Slice 2)
    '
    '  Declares the pal-rest source: the NODE polls
    '  /v1/api/players on loopback every 5s, diffs snapshots, and
    '  synthesizes PlayerJoin/PlayerLeave into the canonical event
    '  stream — Palworld player tracking with log capture still
    '  dark (UE console output is invisible to piped stdio until
    '  ConPTY lands). Field semantics identical to GetPlayersAsync
    '  above (verified live 13 Jul 2026): name = character name,
    '  accountName = Steam persona, playerId = character id,
    '  userId = "steam_<SteamID64>" (split into Platform + id via
    '  PlatformPrefixField), iP = remote address (erratic casing —
    '  the node matches payload keys case-insensitively).
    '
    '  pal-rest OWNS PlayerJoin/PlayerLeave (AuthoritativeKinds):
    '  poll diffing is steadier than log text across game patches —
    '  Pocketpair can reword a log line any Tuesday; the REST
    '  payload is a stable contract. When ConPTY log capture lands,
    '  its parsed joins get suppressed at append (observable in
    '  /events/sources) instead of double-counting.
    '
    '  Declining is cheap and normal: REST disabled, no password,
    '  or unreadable settings file → Nothing → no poller — the
    '  operator enables RESTAPIEnabled + AdminPassword (settings
    '  editor) and restarts the instance to light it up.
    ' ============================================================

    Public Async Function GetStatePollSourcesAsync(context As StatePollResolveContext) _
            As Task(Of IReadOnlyList(Of GSM.Node.Api.ResolvedStatePollSource)) _
            Implements IStatePollProvider.GetStatePollSourcesAsync

        If context Is Nothing OrElse context.FetchInstanceFile Is Nothing Then Return Nothing

        Dim settings = Await ReadRestSettingsCoreAsync(context.Config, context.FetchInstanceFile)
        If settings Is Nothing OrElse Not settings.Enabled OrElse
           String.IsNullOrEmpty(settings.AdminPassword) Then
            Return Nothing ' REST off/unconfigured — no poller this run
        End If

        ' Console-first flip (0.3.0 / C4b): the AuthoritativeKinds claims
        ' below are now the FALLBACK. With console parse rules present for
        ' the same kinds, the node hands authority to the capture source
        ' (conpty/stdout) and demotes this poller to reconciliation cadence
        ' — REST is the backup, not the default. On an instance with no
        ' console capture (or a pre-0.3.0 rule set) these claims take
        ' effect exactly as before.
        Return New GSM.Node.Api.ResolvedStatePollSource() {
            New GSM.Node.Api.ResolvedStatePollSource With {
                .SourceId = "pal-rest",
                .Url = $"http://127.0.0.1:{settings.Port}/v1/api/players",
                .AuthKind = GSM.Node.Api.PollAuthKind.Basic,
                .AuthUsername = "admin",
                .AuthPassword = settings.AdminPassword,
                .IntervalSeconds = 5,
                .PayloadShape = GSM.Node.Api.PollPayloadShape.PlayerArray,
                .PlayersArrayPath = "players",
                .KeyField = "PlatformUserId",
                .PlatformPrefixField = "PlatformUserId",
                .FieldMap = New Dictionary(Of String, String) From {
                    {"name", "DisplayName"},
                    {"accountName", "PlatformPersona"},
                    {"playerId", "CharacterId"},
                    {"userId", "PlatformUserId"},
                    {"ip", "RemoteAddress"}
                },
                .AuthoritativeKinds = New List(Of ParsedEventKind) From {
                    ParsedEventKind.PlayerJoin,
                    ParsedEventKind.PlayerLeave
                }
            }
        }
    End Function

    Private Shared Function BuildRestRequest(method As HttpMethod,
                                              ctx As RemoteControlContext,
                                              settings As RestSettings,
                                              endpoint As String,
                                              Optional jsonBody As String = Nothing) As HttpRequestMessage
        Dim req As New HttpRequestMessage(method,
            $"http://{ctx.NodeHost}:{settings.Port}/v1/api/{endpoint}")
        Dim cred = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" & settings.AdminPassword))
        req.Headers.Authorization = New AuthenticationHeaderValue("Basic", cred)
        If jsonBody IsNot Nothing Then
            req.Content = New StringContent(jsonBody, Encoding.UTF8, "application/json")
        End If
        Return req
    End Function

    Public Async Function RequestStopAsync(context As RemoteControlContext,
                                            ct As CancellationToken) As Task(Of Boolean) _
            Implements IRemoteControlProvider.RequestStopAsync

        Dim settings = Await ReadRestSettingsAsync(context)
        If settings Is Nothing OrElse Not settings.Enabled OrElse
           String.IsNullOrEmpty(settings.AdminPassword) Then
            Return False ' REST off/unconfigured — node CtrlC path handles it
        End If

        ' Announce + save are best-effort niceties; only /shutdown
        ' decides handled/not. Failures on them don't abort.
        Try
            Using resp = Await _http.SendAsync(BuildRestRequest(HttpMethod.Post, context, settings,
                "announce", "{""message"":""Server is shutting down""}"), ct)
            End Using
        Catch
        End Try
        Try
            Using resp = Await _http.SendAsync(BuildRestRequest(HttpMethod.Post, context, settings,
                "save"), ct)
            End Using
        Catch
        End Try

        Try
            Using resp = Await _http.SendAsync(BuildRestRequest(HttpMethod.Post, context, settings,
                "shutdown", "{""waittime"":1,""message"":""Server is shutting down""}"), ct)
                Return resp.IsSuccessStatusCode
            End Using
        Catch
            Return False ' unreachable — fall through to node stop path
        End Try
    End Function

    Public Async Function GetPlayersAsync(context As RemoteControlContext,
                                           ct As CancellationToken) As Task(Of IReadOnlyList(Of GSM.Node.Api.PlayerSession)) _
            Implements IRemoteControlProvider.GetPlayersAsync

        Dim settings = Await ReadRestSettingsAsync(context)
        If settings Is Nothing OrElse Not settings.Enabled OrElse
           String.IsNullOrEmpty(settings.AdminPassword) Then
            Return Nothing
        End If

        Try
            Using resp = Await _http.SendAsync(BuildRestRequest(HttpMethod.Get, context, settings,
                "players"), ct)
                If Not resp.IsSuccessStatusCode Then Return Nothing
                Dim body = Await resp.Content.ReadAsStringAsync(ct)

                Dim root = TryCast(JsonNode.Parse(body), JsonObject)
                If root Is Nothing Then Return Nothing
                Dim arr = TryCast(root("players"), JsonArray)
                If arr Is Nothing Then Return Nothing

                Dim players As New List(Of GSM.Node.Api.PlayerSession)
                For Each node In arr
                    Dim obj = TryCast(node, JsonObject)
                    If obj Is Nothing Then Continue For
                    ' Copy properties into a case-insensitive dict
                    ' first — Palworld's key casing is erratic (the
                    ' live 1.0 API returns "iP", not "ip") and
                    ' JsonObject lookups are case-sensitive.
                    Dim props As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                    For Each kvp In obj
                        Dim v = If(kvp.Value?.ToString(), "").Trim(""""c)
                        If v.Length > 0 Then props(kvp.Key) = v
                    Next

                    ' Field semantics (verified live, 13 Jul 2026):
                    '   name        = in-world CHARACTER name
                    '   accountName = platform persona (Steam name)
                    '   playerId    = in-world character id (hex)
                    '   userId      = "steam_<SteamID64>" — stable
                    '   iP          = remote address
                    Dim userId As String = Nothing
                    props.TryGetValue("userId", userId)
                    Dim platform As String = Nothing
                    Dim platformUserId = If(userId, "")
                    Dim sep = platformUserId.IndexOf("_"c)
                    If sep > 0 AndAlso sep < platformUserId.Length - 1 Then
                        platform = platformUserId.Substring(0, sep)
                        platformUserId = platformUserId.Substring(sep + 1)
                    End If

                    Dim characterName As String = Nothing
                    props.TryGetValue("name", characterName)
                    Dim persona As String = Nothing
                    If Not props.TryGetValue("accountName", persona) OrElse String.IsNullOrEmpty(persona) Then
                        persona = characterName
                    End If
                    Dim characterId As String = Nothing
                    props.TryGetValue("playerId", characterId)
                    Dim remoteAddr As String = Nothing
                    props.TryGetValue("ip", remoteAddr)

                    ' JoinedUtc unknown to a point-in-time poll —
                    ' stays default.
                    players.Add(New GSM.Node.Api.PlayerSession With {
                        .DisplayName = characterName,
                        .PlatformPersona = persona,
                        .Platform = platform,
                        .PlatformUserId = platformUserId,
                        .CharacterId = characterId,
                        .RemoteAddress = remoteAddr
                    })
                Next
                Return players
            End Using
        Catch
            Return Nothing
        End Try
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
