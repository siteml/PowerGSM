Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin

' ============================================================
'  Factorio Headless Server Plugin
'
'  AppID: 427520 (free dedicated server via SteamCMD)
'  Also available as direct download from factorio.com
'  RCON: Source RCON protocol (Factorio implements it natively)
'  Mods: Factorio Mod Portal API
'
'  Install config:
'    SteamBranch     — beta branch (blank = stable)
'    UseExperimental — if true, version-check tracks the
'                       experimental headless build instead of
'                       stable. Default false.
'
'  Instance config:
'    Port            — UDP game port, default 34197
'    RconPort        — RCON port, default 27015
'    RconPassword    — RCON password (required for RCON)
'    SaveFile        — path to save file, default "save.zip"
'    ServerSettings  — path to server-settings.json
'    MapGenSettings  — path to map-gen-settings.json
'    MapSettings     — path to map-settings.json
'    UseLatestSave   — use most recent save, default true
' ============================================================

Public Class FactorioPlugin
    Implements IGamePlugin
    Implements IVersionAwarePlugin
    Implements IInstallationNoticeProvider
    Implements ILaunchOptionsProvider

    Public ReadOnly Property GameId As String = "factorio" Implements IGamePlugin.GameId
    Public ReadOnly Property DisplayName As String = "Factorio" Implements IGamePlugin.DisplayName

    ' Factorio file-locks its save, config, and mods directory — a
    ' second instance launched against the same file set fails on
    ' the lock with a confusing error before even reaching the
    ' "port already in use" step. Hard-limit to one instance per
    ' install; users who want multiple servers create separate
    ' installations.
    Public ReadOnly Property MaxInstancesPerInstallation As Integer? Implements IGamePlugin.MaxInstancesPerInstallation
        Get
            Return 1
        End Get
    End Property

    ' ============================================================
    '  Install
    ' ============================================================

    Public Function GetSupportedInstallMethods() As IReadOnlyList(Of InstallMethod) Implements IGamePlugin.GetSupportedInstallMethods
        Return New InstallMethod() {InstallMethod.SteamCmd, InstallMethod.DirectDownload}
    End Function

    Public Function GetInstallSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetInstallSteps
        Dim steps As New List(Of InstallStep)

        If config.InstallMethod = InstallMethod.SteamCmd Then
            Dim steamStep As New SteamCmdStep()
            steamStep.StepName = "Download Factorio Server"
            steamStep.Description = "Download/update via SteamCMD (AppID 427520)"
            steamStep.AppId = 427520
            steamStep.ValidateFiles = True
            steamStep.RequiresLogin = True

            If config.CustomFields IsNot Nothing Then
                Dim branch = GetField(config.CustomFields, "SteamBranch")
                If Not String.IsNullOrEmpty(branch) Then
                    steamStep.BetaBranch = branch
                End If
            End If

            steps.Add(steamStep)

        ElseIf config.InstallMethod = InstallMethod.DirectDownload Then
            ' Factorio headless server (Linux nodes only — .tar.xz archive)
            ' This URL redirects to the latest stable headless build
            Dim dlUrl = GetField(config.CustomFields, "DownloadUrl")
            If String.IsNullOrEmpty(dlUrl) Then
                dlUrl = "https://factorio.com/get-download/stable/headless/linux64"
            End If
            Dim dlStep As New DownloadFileStep()
            dlStep.StepName = "Download Factorio Headless"
            dlStep.Description = "Download headless server from factorio.com"
            dlStep.Url = dlUrl
            dlStep.DestinationRelativePath = "factorio-headless.tar.xz"
            dlStep.ExtractArchive = True
            steps.Add(dlStep)
        End If

        ' SteamCMD's app_update writes a fresh config-path.cfg with
        ' upstream defaults that point Factorio's config to
        ' %APPDATA%\Factorio — colliding system-wide across every
        ' Factorio server on the machine and putting state outside
        ' the install dir where PowerGSM can't see or manage it. We
        ' overwrite the file immediately after install so the FIRST
        ' run generates a config.ini at <install>/config/config.ini.
        ' See the comment block on BuildConfigPathStep for the
        ' macro-resolution lessons that motivate the specific path
        ' expression we write.
        steps.Add(BuildConfigPathStep())

        ' Create default server-settings.json if it doesn't exist
        Dim writeSettings As New WriteFileStep()
        writeSettings.StepName = "Create default server settings"
        writeSettings.Description = "Write server-settings.json with defaults"
        writeSettings.RelativePath = "server-settings.json"
        writeSettings.OverwriteExisting = False
        writeSettings.Content = DefaultServerSettings()
        steps.Add(writeSettings)

        Return steps
    End Function

    Public Function GetUpdateSteps(config As InstallationConfig) As IReadOnlyList(Of InstallStep) Implements IGamePlugin.GetUpdateSteps
        ' Update skips the server-settings write step
        Dim steps As New List(Of InstallStep)

        If config.InstallMethod = InstallMethod.SteamCmd Then
            Dim steamStep As New SteamCmdStep()
            steamStep.StepName = "Update Factorio Server"
            steamStep.Description = "Update via SteamCMD (AppID 427520)"
            steamStep.AppId = 427520
            steamStep.ValidateFiles = True
            steamStep.RequiresLogin = True

            If config.CustomFields IsNot Nothing Then
                Dim branch = GetField(config.CustomFields, "SteamBranch")
                If Not String.IsNullOrEmpty(branch) Then
                    steamStep.BetaBranch = branch
                End If
            End If

            steps.Add(steamStep)
        End If

        ' SteamCMD's app_update rewrites config-path.cfg back to its
        ' upstream defaults on every update — which point at
        ' %APPDATA% — so we have to re-write our version here too.
        ' Factorio's populated config.ini at <install>/config/
        ' config.ini already wins at runtime via its [path] section
        ' once it exists, but a future delete-and-reinstall of the
        ' game files would re-bootstrap from system dirs without
        ' this. One disk write per update is cheap insurance. Only
        ' emit when there's actually something to update against —
        ' a DirectDownload installation has no update step list and
        ' shouldn't get a stray config write.
        If steps.Count > 0 Then
            steps.Add(BuildConfigPathStep())
        End If

        Return steps
    End Function

    ' ============================================================
    '  Instance
    ' ============================================================

    Public Function GetExecutablePath(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.GetExecutablePath
        ' Factorio headless server ships a single binary on Windows.
        Return New String() {
            IO.Path.Combine("bin", "x64", "factorio.exe")
        }
    End Function

    Public Function BuildLaunchArguments(config As InstanceConfig) As String Implements IGamePlugin.BuildLaunchArguments
        Dim args As New List(Of String)

        ' Save selection. Factorio's CLI has two distinct surfaces here:
        '   --start-server <SAVE>          — explicit save file by path
        '   --start-server-load-latest     — flag, NO argument; picks the
        '                                     newest .zip under saves/
        ' Earlier this code emitted `--start-server !!latest`, which
        ' Factorio interprets as a literal save path of "!!latest" —
        ' producing the "File C:/.../!!latest does not exist" crash.
        ' UseLatestSave wins over an explicit SaveFile when both are
        ' set, matching prior intent.
        Dim saveFile = GetField(config.CustomFields, "SaveFile")
        Dim useLatest = GetField(config.CustomFields, "UseLatestSave")
        If Not String.IsNullOrEmpty(useLatest) AndAlso
           useLatest.Equals("true", StringComparison.OrdinalIgnoreCase) Then
            args.Add("--start-server-load-latest")
        ElseIf Not String.IsNullOrEmpty(saveFile) Then
            args.Add("--start-server")
            args.Add(saveFile)
        Else
            args.Add("--start-server")
            args.Add("save.zip")
        End If

        ' Port
        Dim port = GetFieldInt(config.CustomFields, "Port", 34197)
        args.Add($"--port {port}")

        ' RCON
        If Not String.IsNullOrEmpty(config.RconPassword) Then
            Dim rconPort = If(config.RconPort, 27015)
            args.Add($"--rcon-port {rconPort}")
            args.Add($"--rcon-password {config.RconPassword}")
        End If

        ' Server settings
        Dim settingsPath = GetField(config.CustomFields, "ServerSettings")
        If Not String.IsNullOrEmpty(settingsPath) Then
            args.Add($"--server-settings {settingsPath}")
        Else
            args.Add("--server-settings server-settings.json")
        End If

        ' Map generation settings
        Dim mapGenPath = GetField(config.CustomFields, "MapGenSettings")
        If Not String.IsNullOrEmpty(mapGenPath) Then
            args.Add($"--map-gen-settings {mapGenPath}")
        End If

        ' Map settings
        Dim mapSettingsPath = GetField(config.CustomFields, "MapSettings")
        If Not String.IsNullOrEmpty(mapSettingsPath) Then
            args.Add($"--map-settings {mapSettingsPath}")
        End If

        Return String.Join(" ", args)
    End Function

    Public Function ValidateConfig(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.ValidateConfig
        Dim errors As New List(Of String)
        ' Factorio has sensible defaults for most things
        ' Just ensure the save file concept is addressed
        Return errors
    End Function

    ' ============================================================
    '  Config schema
    ' ============================================================

    Public Function GetInstallConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstallConfigSchema
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "SteamBranch",
                .Label = "Steam beta branch",
                .Description = "Beta branch name. Leave blank for stable.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = ""
            },
            New ConfigFieldDescriptor With {
                .Key = "DownloadUrl",
                .Label = "Direct download URL",
                .Description = "Factorio headless download URL (only for direct download method).",
                .FieldType = ConfigFieldType.Text
            },
            New ConfigFieldDescriptor With {
                .Key = "UseExperimental",
                .Label = "Track experimental version",
                .Description = "When checked, the version-check service tracks the experimental headless build instead of the stable one. Doesn't affect installation — only the version-mismatch detection.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            }
        }
    End Function

    Public Function GetInstanceConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements IGamePlugin.GetInstanceConfigSchema
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "Port",
                .Label = "Game Port (UDP)",
                .Description = "Default 34197. Must be unique per instance.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "34197",
                .MinValue = 1024,
                .MaxValue = 65535,
                .IsPort = True
            },
            New ConfigFieldDescriptor With {
                .Key = "RconPort",
                .Label = "RCON Port",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "27015",
                .MinValue = 1024,
                .MaxValue = 65535,
                .IsPort = True
            },
            New ConfigFieldDescriptor With {
                .Key = "RconPassword",
                .Label = "RCON Password",
                .Description = "Required for RCON. Factorio uses Source RCON protocol.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True
            },
            New ConfigFieldDescriptor With {
                .Key = "SaveFile",
                .Label = "Save File",
                .Description = "Save file name or path. Default: save.zip",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "save.zip"
            },
            New ConfigFieldDescriptor With {
                .Key = "UseLatestSave",
                .Label = "Use latest save",
                .Description = "Start with the most recent save file.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "ServerSettings",
                .Label = "Server settings file",
                .Description = "Path to server-settings.json. Default: server-settings.json",
                .FieldType = ConfigFieldType.FilePath,
                .DefaultValue = "server-settings.json"
            },
            New ConfigFieldDescriptor With {
                .Key = "MapGenSettings",
                .Label = "Map generation settings",
                .Description = "Path to map-gen-settings.json (optional).",
                .FieldType = ConfigFieldType.FilePath
            },
            New ConfigFieldDescriptor With {
                .Key = "MapSettings",
                .Label = "Map settings",
                .Description = "Path to map-settings.json (optional).",
                .FieldType = ConfigFieldType.FilePath
            }
        }
    End Function

    ' ============================================================
    '  Crash handling
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
        Return New FactorioLogParser()
    End Function

    Public Function GetLogSources(config As InstanceConfig) As IReadOnlyList(Of ILogSource) Implements IGamePlugin.GetLogSources
        ' Factorio outputs to both stdout and factorio-current.log
        Return New ILogSource() {
            New StdoutLogSource(),
            New FileLogSource("factorio-log", "factorio-current.log")
        }
    End Function

    Public Function GetLogParseRules() As IReadOnlyList(Of LogParseRule) Implements IGamePlugin.GetLogParseRules
        ' Factorio's headless server writes clear, parseable lines for
        ' player join/leave and chat. All of these appear in stdout and
        ' in factorio-current.log with the same format.
        Return New LogParseRule() {
            New LogParseRule With {
                .Name = "Player Join",
                .Kind = ParsedEventKind.PlayerJoin,
                .Pattern = "\[JOIN\] (?<" & "Name" & ">\S+) joined the game"
            },
            New LogParseRule With {
                .Name = "Player Leave",
                .Kind = ParsedEventKind.PlayerLeave,
                .Pattern = "\[LEAVE\] (?<" & "Name" & ">\S+) left the game"
            },
            New LogParseRule With {
                .Name = "Chat Message",
                .Kind = ParsedEventKind.ChatMessage,
                .Pattern = "\[CHAT\] (?<" & "Name" & ">[^:]+): (?<Message>.+)$"
            },
            New LogParseRule With {
                .Name = "Server Ready",
                .Kind = ParsedEventKind.ServerStateChange,
                .Pattern = "Hosting game at IP ADDR.*?port\(s\) (?<" & "MatchState" & ">\d+)"
            }
        }
    End Function

    ' ============================================================
    '  RCON — Factorio implements Source RCON natively
    ' ============================================================

    Public Function GetRconProtocol() As RconProtocol? Implements IGamePlugin.GetRconProtocol
        Return RconProtocol.SourceRcon
    End Function

    ' ============================================================
    '  Mods
    ' ============================================================

    Public Function CreateModManager() As IModManager Implements IGamePlugin.CreateModManager
        Return New FactorioModManager()
    End Function

    ' ============================================================
    '  IVersionAwarePlugin — fetch latest from factorio.com API
    '
    '  Endpoint: https://factorio.com/api/latest-releases
    '  Returns JSON like:
    '    {
    '      "experimental": { "alpha": "...", "demo": "...", "headless": "2.0.43" },
    '      "stable":       { "alpha": "...", "demo": "...", "headless": "2.0.42" }
    '    }
    '  We pull the headless field from whichever channel matches
    '  the install's UseExperimental setting.
    '
    '  This works whether the installation was set up via SteamCMD
    '  or via direct download. The version string is the canonical
    '  Factorio version (e.g. "2.0.42") regardless of install path.
    '
    '  HttpClient: shared static instance with a 10-second timeout.
    '  Reused across plugin instances and across calls. Adding
    '  User-Agent because some Factorio backends 403 without one.
    ' ============================================================

    Private Shared ReadOnly _httpClient As HttpClient = CreateHttpClient()

    Private Shared Function CreateHttpClient() As HttpClient
        Dim client As New HttpClient() With {
            .Timeout = TimeSpan.FromSeconds(10)
        }
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PowerGSM/1.0")
        Return client
    End Function

    Public Async Function GetLatestVersionAsync(
            config As InstallationConfig,
            cancellation As CancellationToken) As Task(Of String) Implements IVersionAwarePlugin.GetLatestVersionAsync

        Dim useExperimental As Boolean = False
        If config IsNot Nothing AndAlso config.CustomFields IsNot Nothing Then
            Dim raw = GetField(config.CustomFields, "UseExperimental")
            useExperimental = (Not String.IsNullOrEmpty(raw)) AndAlso
                              raw.Equals("true", StringComparison.OrdinalIgnoreCase)
        End If

        Try
            Using resp = Await _httpClient.GetAsync(
                "https://factorio.com/api/latest-releases", cancellation)
                If Not resp.IsSuccessStatusCode Then
                    ' Transient — return Nothing so the caller skips
                    ' updating LatestKnownVersion this cycle.
                    Return Nothing
                End If
                Dim json = Await resp.Content.ReadAsStringAsync(cancellation)
                Using doc = JsonDocument.Parse(json)
                    Dim root = doc.RootElement
                    Dim channel As String = If(useExperimental, "experimental", "stable")
                    Dim channelEl As JsonElement
                    If Not root.TryGetProperty(channel, channelEl) Then Return Nothing
                    Dim headlessEl As JsonElement
                    If Not channelEl.TryGetProperty("headless", headlessEl) Then Return Nothing
                    Dim version = headlessEl.GetString()
                    If String.IsNullOrEmpty(version) Then Return Nothing
                    Return version
                End Using
            End Using
        Catch ex As OperationCanceledException
            ' Cancelled — propagate. The Manager handles this gracefully.
            Throw
        Catch
            ' Network errors, JSON parse failures, etc. — transient.
            ' Return Nothing so the caller skips this cycle.
            Return Nothing
        End Try
    End Function

    ' ============================================================
    '  IInstallationNoticeProvider
    '
    '  Surface the AppData/saves caveat to anyone setting up a new
    '  Factorio installation. Most users won't have run standalone
    '  Factorio on their server box, so this is just background
    '  context — not a warning, not blocking. The form renders it
    '  above the action buttons.
    '
    '  We don't probe the node for an actual %APPDATA%\Factorio
    '  directory: that'd require a node-side check round-trip on
    '  every game-selection change in the form, and the message is
    '  cheap enough to show unconditionally that the conditional
    '  version isn't worth the plumbing.
    ' ============================================================

    Public Function GetPreInstallNotices() As IReadOnlyList(Of InstallationNotice) Implements IInstallationNoticeProvider.GetPreInstallNotices
        Return New InstallationNotice() {
            New InstallationNotice With {
                .Severity = NoticeSeverity.Information,
                .Title = "Migrating from a standalone Factorio install?",
                .Body = "Saves and mods from any previous standalone Factorio installs on this machine live in %APPDATA%\Factorio and won't be available here automatically. After install completes, copy them to <install>\saves\ and <install>\mods\ if you want them."
            }
        }
    End Function

    ' ============================================================
    '  ILaunchOptionsProvider
    '
    '  Factorio's headless server defeats the standard
    '  CREATE_NEW_CONSOLE + SW_HIDE spawn that Last Oasis uses
    '  cleanly: at startup it does FreeConsole +
    '  AttachConsole(ATTACH_PARENT_PROCESS), reattaching its
    '  stdout/stderr to whatever console its parent has. Without
    '  isolation that's the node's terminal — Factorio's log
    '  output ends up there, and the node and Factorio share a
    '  console group (Stop signals propagate back to the node).
    '
    '  Setting RequiresConsoleIsolation tells the node we need
    '  insulation between the game executable and the node's own
    '  console. The node currently implements this by spawning
    '  through cmd.exe so Factorio's reattach target is cmd's
    '  hidden console rather than the node's terminal. That detail
    '  isn't part of the contract; if a future implementation
    '  achieves the same outcome differently (e.g. STARTUPINFOEX
    '  with PROC_THREAD_ATTRIBUTE_HANDLE_LIST), this plugin's
    '  declaration doesn't need to change.
    '
    '  LogTailerStartDelayMs=0 because Factorio crashes faster
    '  than UE4's 5-second "don't open the file during init"
    '  default would tolerate — we want the tailer to start reading
    '  immediately so init-time errors reach the manager's log
    '  buffer before the process exits.
    '
    '  StdoutIsLog stays False: factorio-current.log is the
    '  authoritative log source (declared via FileLogSource
    '  above), and stdout is just a duplicate. Setting it True
    '  would force Strategy A (captured stdio, no console),
    '  which would also work on the leak front but break our
    '  AttachConsole-based graceful shutdown path.
    '
    '  Diagnostic clue that originally identified Factorio's
    '  AttachConsole behaviour: launching LO under the direct
    '  hidden-console spawn caused a conhost.exe to spawn alongside
    '  Mist in Task Manager (proof CREATE_NEW_CONSOLE took effect),
    '  while launching Factorio under the same path produced no
    '  conhost — evidence that Factorio was abandoning its newly-
    '  allocated console immediately after start.
    '
    '  Last Oasis doesn't implement ILaunchOptionsProvider at all,
    '  which gets the same defaults as a LaunchOptions with
    '  everything left unset — the node picks the direct hidden-
    '  console path because LO declares file logs, and avoids the
    '  cmd.exe overhead.
    ' ============================================================

    Public Function GetLaunchOptions(config As InstanceConfig) As LaunchOptions Implements ILaunchOptionsProvider.GetLaunchOptions
        Return New LaunchOptions With {
            .RequiresConsoleIsolation = True,
            .LogTailerStartDelayMs = 0
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

    ' ============================================================
    '  config-path.cfg helpers
    '
    '  See the rationale comment at the GetInstallSteps call site
    '  for why this exists. The content below is the minimal
    '  config-path.cfg Factorio will accept: a config-path line and
    '  the use-system-read-write-data-directories flag.
    '
    '  Two non-obvious things about config-path's value, both
    '  learned the hard way from observing Factorio's behaviour:
    '
    '    1. Macro resolution. Factorio supports two relevant path
    '       macros in config-path.cfg:
    '         __PATH__executable__       → directory containing
    '                                       factorio.exe
    '                                       (i.e. <install>/bin/x64)
    '         __PATH__system-write-data__→ the per-user system data
    '                                       location (always
    '                                       %APPDATA%\Factorio on
    '                                       Windows)
    '
    '       The use-system-read-write-data-directories flag does
    '       NOT affect macro substitution. It controls implicit
    '       defaults elsewhere (Factorio's choice of [path]/read-
    '       data and [path]/write-data when config.ini doesn't set
    '       them). When this code originally wrote
    '       `__PATH__system-write-data__/config` expecting the flag
    '       to redirect that macro, Factorio kept creating
    '       config.ini at %APPDATA%\Factorio\config\config.ini —
    '       because that's literally what we asked for.
    '
    '       Use __PATH__executable__/../../config to walk up two
    '       levels from <install>/bin/x64 into the install root,
    '       then down into the config directory.
    '
    '    2. Path semantics. config-path is a DIRECTORY path, not
    '       a file path. Factorio appends "/config.ini" itself when
    '       resolving the actual config file location. A subsequent
    '       attempt to "fix" macro #1 by writing
    '       `__PATH__executable__/../../config/config.ini` produced
    '       the doubled path config\config.ini\config.ini in
    '       Factorio's startup log, and a `create_directories`
    '       failure when something already occupied that slot.
    '
    '       Drop the .ini suffix; let Factorio append.
    '
    '    Forward slashes used throughout (Windows accepts both;
    '    Linux only accepts forward slashes) so this content is
    '    correct on both platforms.
    ' ============================================================

    Private Shared Function BuildConfigPathStep() As WriteFileStep
        Return New WriteFileStep With {
            .StepName = "Configure Factorio paths",
            .Description = "Point config-path at <install>/config so Factorio doesn't write its config to %APPDATA%.",
            .RelativePath = "config-path.cfg",
            .OverwriteExisting = True,
            .Content = ConfigPathCfgContent()
        }
    End Function

    Private Shared Function ConfigPathCfgContent() As String
        ' Use Convert.ToChar(10) rather than vbLf or ChrW: both of
        ' those live in Microsoft.VisualBasic.Strings, which Roslyn
        ' doesn't auto-import when compiling plugin .vb files at
        ' runtime. (The VS-time build of GSM.PluginsSource works
        ' because the VB project SDK adds the import implicitly.)
        ' Convert is in System and resolves uniformly in both
        ' contexts. Produces an LF byte, which Factorio's config
        ' parser accepts on every platform.
        Dim LF As String = Convert.ToChar(10)
        Return "# Managed by PowerGSM. Tells Factorio to put its config under" & LF &
               "# <install>/config (it appends /config.ini itself) instead of" & LF &
               "# %APPDATA%\Factorio. __PATH__executable__ resolves to" & LF &
               "# <install>/bin/x64; walking up two levels reaches the install root." & LF &
               "# The use-system-read-write-data-directories flag controls Factorio's" & LF &
               "# implicit defaults for read-data and write-data when config.ini doesn't" & LF &
               "# override them — setting it false keeps saves, mods, and scripts under" & LF &
               "# the install dir too." & LF &
               "config-path=__PATH__executable__/../../config" & LF &
               "use-system-read-write-data-directories=false" & LF
    End Function

    Private Shared Function DefaultServerSettings() As String
        Return "{
  ""name"": ""Factorio Server"",
  ""description"": ""Managed by PowerGSM"",
  ""tags"": [""game""],
  ""max_players"": 0,
  ""visibility"": {
    ""public"": false,
    ""lan"": true
  },
  ""require_user_verification"": true,
  ""max_upload_in_kilobytes_per_second"": 0,
  ""max_upload_slots"": 5,
  ""minimum_latency_in_ticks"": 0,
  ""ignore_player_limit_for_returning_players"": false,
  ""allow_commands"": ""admins-only"",
  ""autosave_interval"": 10,
  ""autosave_slots"": 5,
  ""afk_autokick_interval"": 0,
  ""auto_pause"": true,
  ""only_admins_can_pause_the_game"": true
}"
    End Function

End Class

' ============================================================
'  Factorio Log Parser
'  Detects player joins/leaves, chat, server state, and errors
' ============================================================

Public Class FactorioLogParser
    Implements ILogParser

    Public ReadOnly Property GameId As String = "factorio" Implements ILogParser.GameId

    ''' <summary>
    ''' Factorio doesn't currently derive a session identity from
    ''' logs — one instance hosts one save, and handoffs between
    ''' instances don't happen. Returning Nothing tells downstream
    ''' code to fall back to "{gameId}:{instanceId}" as the session
    ''' key. If we later want save-file-aware identity (so the
    ''' history of a particular save survives deleting and recreating
    ''' its instance), this property would be where that logic lives.
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

        ' Player join: "Player Name joined the game"
        If text.Contains(" joined the game") Then
            Dim playerName = text.Substring(0, text.IndexOf(" joined the game", StringComparison.Ordinal)).Trim()
            ' Strip timestamp prefix if present (e.g. "2024-01-01 12:00:00 [JOIN] PlayerName")
            Dim bracketIdx = playerName.LastIndexOf("]"c)
            If bracketIdx >= 0 Then
                playerName = playerName.Substring(bracketIdx + 1).Trim()
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

        ' Player leave: "Player Name left the game"
        If text.Contains(" left the game") Then
            Dim playerName = text.Substring(0, text.IndexOf(" left the game", StringComparison.Ordinal)).Trim()
            Dim bracketIdx = playerName.LastIndexOf("]"c)
            If bracketIdx >= 0 Then
                playerName = playerName.Substring(bracketIdx + 1).Trim()
            End If
            Return New ParsedLogEvent With {
                .EventType = LogEventType.PlayerLeave,
                .Message = $"Player left: {playerName}",
                .PlayerInfo = New PlayerInfo With {
                    .PlayerName = playerName
                }
            }
        End If

        ' Chat message: "[CHAT] Player: message"
        If text.Contains("[CHAT]") Then
            Return New ParsedLogEvent With {
                .EventType = LogEventType.ChatMessage,
                .Message = text
            }
        End If

        ' Server ready: "Hosting game at IP"
        If text.Contains("Hosting game at") Then
            Return New ParsedLogEvent With {
                .EventType = LogEventType.ServerReady,
                .Message = "Server is ready and hosting"
            }
        End If

        ' Errors
        If text.Contains("Error ") OrElse text.Contains("FATAL") Then
            Return New ParsedLogEvent With {
                .EventType = LogEventType.ErrorOccurred,
                .Message = text
            }
        End If

        Return ParsedLogEvent.NoMatch
    End Function

    Public Function GetCrashPatterns() As IReadOnlyList(Of String) Implements ILogParser.GetCrashPatterns
        Return New String() {
            "FATAL",
            "Error: MultiplayerManager",
            "Couldn't load the map",
            "Map version ",
            "Segmentation fault"
        }
    End Function

End Class

' ============================================================
'  Factorio Mod Manager
'  Manages mods via the local mod directory
' ============================================================

Public Class FactorioModManager
    Implements IModManager

    Public ReadOnly Property GameId As String = "factorio" Implements IModManager.GameId

    Public Function GetInstalledModsAsync(installPath As String,
                                           cancellation As CancellationToken) As Task(Of IReadOnlyList(Of ModInfo)) Implements IModManager.GetInstalledModsAsync
        Dim mods As New List(Of ModInfo)

        ' Factorio mods live in <installPath>/mods/
        Dim modsDir = Path.Combine(installPath, "mods")
        If Not Directory.Exists(modsDir) Then
            Return Task.FromResult(Of IReadOnlyList(Of ModInfo))(mods)
        End If

        ' Each mod is a .zip file in the mods directory
        For Each zipFile In Directory.GetFiles(modsDir, "*.zip")
            Dim fileName = Path.GetFileNameWithoutExtension(zipFile)
            ' Factorio mod zips are named "modname_version.zip"
            Dim underscoreIdx = fileName.LastIndexOf("_"c)
            Dim modName = fileName
            Dim modVersion = ""
            If underscoreIdx > 0 Then
                modName = fileName.Substring(0, underscoreIdx)
                modVersion = fileName.Substring(underscoreIdx + 1)
            End If

            mods.Add(New ModInfo With {
                .ModId = modName,
                .ModName = modName,
                .Version = modVersion,
                .IsEnabled = True
            })
        Next

        Return Task.FromResult(Of IReadOnlyList(Of ModInfo))(mods)
    End Function

    Public Function InstallModAsync(installPath As String,
                                     modId As String,
                                     cancellation As CancellationToken) As Task(Of Boolean) Implements IModManager.InstallModAsync
        ' Full implementation would download from the Factorio Mod Portal API:
        '   https://mods.factorio.com/api/mods/{modId}
        ' For now, return False — manual mod installation is supported
        ' by placing .zip files in the mods/ directory.
        Return Task.FromResult(False)
    End Function

    Public Function RemoveModAsync(installPath As String,
                                    modId As String,
                                    cancellation As CancellationToken) As Task(Of Boolean) Implements IModManager.RemoveModAsync
        Dim modsDir = Path.Combine(installPath, "mods")
        If Not Directory.Exists(modsDir) Then Return Task.FromResult(False)

        ' Find and delete matching zip files
        For Each zipFile In Directory.GetFiles(modsDir, $"{modId}_*.zip")
            Try
                File.Delete(zipFile)
                Return Task.FromResult(True)
            Catch
                Return Task.FromResult(False)
            End Try
        Next

        Return Task.FromResult(False)
    End Function

    Public Function GetModConfigSchema(modId As String) As IReadOnlyList(Of ConfigFieldDescriptor) Implements IModManager.GetModConfigSchema
        ' Factorio mod settings are game-global, not per-mod configurable
        Return Array.Empty(Of ConfigFieldDescriptor)()
    End Function

End Class
