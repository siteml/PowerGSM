' <RequiresContracts: 1>
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Node.Api
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
    Implements IManagedDirectoriesProvider
    Implements IFileGenerationProvider
    Implements IInstanceFileEditorProvider

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
            ' This URL redirects to the latest stable headless build.
            '
            ' StripTopLevelDirectory = True because the tarball wraps
            ' everything in a "factorio/" directory; without stripping,
            ' GetExecutablePath's relative "bin/x64/factorio" wouldn't
            ' resolve and GetInstalledVersionAsync wouldn't find
            ' "data/base/info.json". The Node's archive extractor
            ' detects the single top-level dir and hoists its contents
            ' up to the install root.
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
            dlStep.StripTopLevelDirectory = True
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
        ' Update skips the server-settings write step (preserves
        ' user edits) but otherwise mirrors the install-method
        ' branches in GetInstallSteps.
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

        ElseIf config.InstallMethod = InstallMethod.DirectDownload Then
            ' Re-fetch the latest headless tarball and re-extract over
            ' the existing install. The factorio.com URL auto-redirects
            ' to whatever's current on the chosen channel, so the same
            ' URL we used at install time produces the new version on
            ' update. Saves and mods live under saves/ and mods/ which
            ' aren't in the tarball, so they survive the overwrite.
            '
            ' StripTopLevelDirectory must match the install step —
            ' the tarball still wraps everything in "factorio/" on
            ' update, and the extractor's hoist logic merges the new
            ' files into the existing install layout.
            '
            ' Without this branch GetUpdateSteps returned an empty list
            ' for direct-download installs — the install runner then
            ' executed zero steps and reported "completed successfully"
            ' without doing any actual work. The version-check loop
            ' would re-detect the same upstream version on its next
            ' pass and the cycle would repeat indefinitely; nothing on
            ' disk ever changed. Symptom in the node log was a series
            ' of bare "Install <id>: completed successfully" lines
            ' with no Download/Extract/Configure entries between them.
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
            dlStep.StripTopLevelDirectory = True
            steps.Add(dlStep)
        End If

        ' Re-write config-path.cfg after any successful install/update
        ' work. Both branches need this:
        '
        '   SteamCmd: app_update rewrites config-path.cfg back to its
        '     upstream defaults (which point at %APPDATA%) on every
        '     update, so we restore our customised version. Factorio's
        '     populated config.ini at <install>/config/config.ini
        '     already wins at runtime via its [path] section once it
        '     exists, but a future delete-and-reinstall would re-
        '     bootstrap from system dirs without our cfg in place.
        '
        '   DirectDownload: the headless tarball ships its own
        '     config-path.cfg and tar extraction overwrites our copy.
        '     Re-writing here restores the customisation we put in
        '     during install. Without this, the FIRST post-update run
        '     of Factorio re-creates config.ini under %APPDATA% (or
        '     ~/.factorio on Linux) and PowerGSM loses sight of the
        '     instance's config state.
        '
        ' Manual install method is a pure no-op — we didn't write
        ' the cfg in the first place, so we shouldn't touch it.
        If steps.Count > 0 Then
            steps.Add(BuildConfigPathStep())
        End If

        Return steps
    End Function

    ' ============================================================
    '  Instance
    ' ============================================================

    Public Function GetExecutablePath(config As InstanceConfig) As IReadOnlyList(Of String) Implements IGamePlugin.GetExecutablePath
        ' Pick the right binary name based on the node's OS, which
        ' the manager has already resolved via /api/version and put
        ' on InstanceConfig.Platform. Factorio's headless build is
        ' `factorio.exe` on Windows and `factorio` (no extension)
        ' on Linux. Forward slashes throughout because both Windows
        ' and Linux file APIs accept them, and using them keeps the
        ' string valid across the manager-Windows-host /
        ' node-Linux-target boundary.
        '
        ' NodePlatform.Unknown means the node is older than the
        ' platform-aware contract — emit both candidates and let
        ' the manager's existing "try each, remember the winner"
        ' probe loop find the right one. That keeps cross-version
        ' manager/node combinations working.
        Select Case config.Platform
            Case NodePlatform.Linux
                Return New String() {"bin/x64/factorio"}
            Case NodePlatform.Windows
                Return New String() {"bin/x64/factorio.exe"}
            Case Else
                Return New String() {"bin/x64/factorio.exe", "bin/x64/factorio"}
        End Select
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
        '
        ' Phase 4c follow-up: if neither UseLatestSave nor SaveFile
        ' is set we DO NOT silently fall back to "save.zip" — that
        ' just produces a "File save.zip does not exist" crash on
        ' fresh installs. ValidateConfig surfaces a warning the
        ' Manager UI shows before launch; if the user clicks through
        ' anyway we still emit "--start-server save.zip" so Factorio
        ' produces its own diagnostic message rather than silently
        ' starting against the wrong save. The fallback name stays
        ' for parity with the historical behaviour but the warning
        ' is now the primary signal.
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

        ' Console log. The friendly user-facing lines —
        '   2026-05-02 22:16:13 [JOIN] site_ml joined the game
        '   2026-05-02 22:16:13 [CHAT] site_ml: hi
        '   2026-05-02 22:16:13 [LEAVE] site_ml left the game
        ' — go to stdout only, NOT to factorio-current.log. The engine
        ' log only carries peerID-level details (e.g. "PlayerJoinGame
        ' peerID(1) playerIndex(0)") with no usernames attached for
        ' successful connections, so EventStore can't derive players
        ' or chat from it. RequiresConsoleIsolation = True routes
        ' Factorio's stdout into a hidden cmd.exe console where we
        ' can't capture it either, so we ask Factorio to mirror the
        ' same content to a file we own and tail. Path is relative
        ' to Factorio's working directory (the install root), which
        ' matches how server-settings.json is referenced above.
        args.Add("--console-log factorio-console.log")

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

        ' Save selection — Factorio crashes immediately on launch if
        ' neither --start-server-load-latest is set nor a real save
        ' file path is provided. Surface this as a pre-flight warning
        ' the Manager can show as warn-and-confirm rather than letting
        ' the user discover it via a confusing 30-line stack trace.
        Dim saveFile = GetField(config.CustomFields, "SaveFile")
        Dim useLatest = GetField(config.CustomFields, "UseLatestSave")
        Dim usingLatest = Not String.IsNullOrEmpty(useLatest) AndAlso
                          useLatest.Equals("true", StringComparison.OrdinalIgnoreCase)
        If Not usingLatest AndAlso String.IsNullOrWhiteSpace(saveFile) Then
            errors.Add("No save file is selected and 'Use latest save' is off. " &
                       "Factorio will fail to start. Either pick a save in the " &
                       "Save File field or enable 'Use latest save'.")
        End If

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
                .Description = "Pick a save from the install's saves/ directory, or type a name. Leave blank with 'Use latest save' off to require an explicit choice before starting.",
                .FieldType = ConfigFieldType.ManagedFilePicker,
                .ManagedDirectoryRef = "saves"
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
        ' Two file sources, both relative to the install root
        ' (Factorio's working directory). The Manager merges them
        ' into a single per-instance ring buffer ordered by tailer
        ' read time, so the log view shows both streams interleaved.
        '
        '   factorio-console.log — friendly user-facing output
        '     produced by the --console-log argument we add in
        '     BuildLaunchArguments. This is where [JOIN], [LEAVE],
        '     and [CHAT] lines live; nothing else carries them.
        '
        '   factorio-current.log — verbose engine output (mod
        '     loading, ServerMultiplayerManager state changes,
        '     prototype checksums, FATAL/Error lines for crash
        '     detection). Useful debugging context, but not where
        '     player events surface.
        '
        ' StdoutLogSource is intentionally absent. Factorio's stdout
        ' WOULD carry the same content as factorio-console.log, but
        ' RequiresConsoleIsolation = True (see ILaunchOptionsProvider
        ' below) routes the spawn through cmd.exe with a hidden
        ' console, so the node's stdio redirection can't reach it.
        ' --console-log sidesteps the problem by having Factorio
        ' write the file itself before any console gymnastics.
        Return New ILogSource() {
            New FileLogSource("factorio-console", "factorio-console.log"),
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
                .Name = "Multiplayer State",
                .Kind = ParsedEventKind.ServerStateChange,
                .Pattern = "changing state from\([^)]+\) to\((?<" & "MatchState" & ">[^)]+)\)"
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

    ''' <summary>
    ''' Read the installed Factorio version off the node's
    ''' filesystem by pulling data/base/info.json and parsing its
    ''' "version" field. info.json is the version manifest of
    ''' Factorio's own "base" mod, which the engine ships with
    ''' every install (Steam, direct download, beta, etc.) and
    ''' updates to match the engine version on every patch — the
    ''' canonical local source for "what version is on disk".
    '''
    ''' Returned format matches GetLatestVersionAsync above
    ''' ("2.0.76") so the Manager's string-equality version
    ''' comparison can detect drift cleanly. A previous design
    ''' stamped "download (timestamp)" or "installed (timestamp)"
    ''' from the Manager-side BuildVersionStamp; that string
    ''' could never match the upstream "2.0.76" the API returns,
    ''' so the UI showed "update available" even immediately
    ''' after a fresh install of the latest version.
    '''
    ''' Two on-disk layouts are tried in order:
    '''
    '''   1. data/base/info.json — SteamCMD installs land here,
    '''      because Steam writes files directly into the install
    '''      root (no extra wrapper directory).
    '''
    '''   2. factorio/data/base/info.json — direct-download
    '''      installs land here, because Factorio's headless
    '''      tar.xz ships with a top-level "factorio/" directory
    '''      and the Node's archive extractor preserves entry
    '''      paths verbatim (SharpCompress WriteEntryToDirectory
    '''      with ExtractFullPath=True). Without this fallback,
    '''      every direct-download install would land in the
    '''      "file not found" branch and stamp the placeholder.
    '''
    ''' Returns Nothing only if BOTH paths fail. Callers fall
    ''' back to the synthetic Manager-side stamp in that case.
    ''' </summary>
    Public Async Function GetInstalledVersionAsync(
            config As InstallationConfig,
            client As INodeClient,
            cancellation As CancellationToken) As Task(Of String) _
            Implements IVersionAwarePlugin.GetInstalledVersionAsync

        If config Is Nothing OrElse client Is Nothing Then Return Nothing
        If String.IsNullOrEmpty(config.InstallPath) Then Return Nothing

        ' Two attempts in order. Steam layout first since it's the
        ' more common case across games that ship via both channels;
        ' direct-download layout second. Returns Nothing only if
        ' both reads fail.
        Dim version = Await TryReadVersionAsync(
            client, config,
            relativePath:="data/base/info.json",
            allowedRoot:="data",
            cancellation:=cancellation)
        If Not String.IsNullOrEmpty(version) Then Return version

        version = Await TryReadVersionAsync(
            client, config,
            relativePath:="factorio/data/base/info.json",
            allowedRoot:="factorio",
            cancellation:=cancellation)
        If Not String.IsNullOrEmpty(version) Then Return version

        Return Nothing
    End Function

    ''' <summary>
    ''' Single-attempt read: download the named file under the
    ''' supplied allowedRoot, parse JSON, return the "version"
    ''' field. Returns Nothing on any failure (file missing,
    ''' HTTP error, malformed JSON, missing field). Cancellation
    ''' propagates as OperationCanceledException so the caller's
    ''' shutdown path stays prompt.
    '''
    ''' Pulled out of GetInstalledVersionAsync so the candidate
    ''' loop stays readable — each iteration is one call here.
    ''' </summary>
    Private Shared Async Function TryReadVersionAsync(
            client As INodeClient,
            config As InstallationConfig,
            relativePath As String,
            allowedRoot As String,
            cancellation As CancellationToken) As Task(Of String)

        Try
            Using ms As New MemoryStream()
                Await client.DownloadFileAsync(
                    instanceId:=config.InstallationId,
                    installPath:=config.InstallPath,
                    path:=relativePath,
                    allowedRoots:=New String() {allowedRoot},
                    allowedExtensions:=New String() {".json"},
                    destination:=ms,
                    cancellation:=cancellation)

                If ms.Length = 0 Then Return Nothing
                ms.Position = 0
                Using doc = JsonDocument.Parse(ms)
                    Dim verEl As JsonElement
                    If doc.RootElement.TryGetProperty("version", verEl) Then
                        Dim v = verEl.GetString()
                        If Not String.IsNullOrEmpty(v) Then Return v
                    End If
                End Using
            End Using
        Catch ex As OperationCanceledException
            Throw
        Catch
            ' File missing (404), HTTP failure, JSON parse failure,
            ' etc. — caller falls through to the next candidate or
            ' the synthetic placeholder stamp.
        End Try
        Return Nothing
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
    '  IManagedDirectoriesProvider
    '
    '  Factorio's saves directory is the only thing the manager UI
    '  needs to expose for now — server-settings.json and the map-
    '  gen JSON files are individual files handled by Phase 4c-4's
    '  config UI rather than as listings of a directory. Mods could
    '  be a future addition (the FactorioModManager already knows
    '  the layout) but mod management has its own dedicated workflow
    '  via the Factorio Mod Portal API; lumping it into the generic
    '  file-ops UI would short-change that.
    '
    '  Saves live at <install>\saves\*.zip. The path is static —
    '  Factorio's MaxInstancesPerInstallation = 1 means we never
    '  need {InstanceId} subdivision. AllowedExtensions locks the
    '  endpoint to .zip so a stray drag-drop of an unrelated file
    '  is rejected before it touches the server's save directory.
    '  Read|Write|Delete because users routinely upload save files
    '  from local play, download server saves for backup, and
    '  delete obsolete save slots.
    ' ============================================================

    Public Function GetManagedDirectories(config As InstanceConfig) As IReadOnlyList(Of ManagedDirectory) Implements IManagedDirectoriesProvider.GetManagedDirectories
        Return New ManagedDirectory() {
            New ManagedDirectory With {
                .RelativePath = "saves",
                .DisplayName = "Saves",
                .Permissions = DirPermissions.Read Or DirPermissions.Write Or DirPermissions.Delete,
                .AllowedExtensions = New List(Of String) From {".zip"}
            }
        }
    End Function

    ' ============================================================
    '  IFileGenerationProvider — schema-driven map generation
    '
    '  Phase 4c-3 (generic). The Manager renders the schema with
    '  SchemaFormBuilder (the same one that drives Edit Instance);
    '  Factorio's plugin owns every detail of what fields exist and
    '  what they mean. Nothing about presets, seeds, or maps lives
    '  in the Manager.
    '
    '  Schema fields:
    '    Preset      — Enum dropdown of preset display names. The
    '                  display name maps back to a key via
    '                  ResolvePresetKeyFromDisplay; the JSON blobs
    '                  are looked up by that key.
    '    SaveName    — Text. Filename for the generated save.
    '                  .zip extension auto-appended in
    '                  BuildGenerationSteps.
    '    Seed        — Text (intentionally not IntegerField so
    '                  blank-allowed; the user typing letters
    '                  gets caught when Factorio rejects it,
    '                  surfaced via GenerateMapResponse.Output).
    '
    '  Drift risk on the preset list as of Factorio 2.x is
    '  documented at the JSON-blob site below. Adding presets
    '  later means appending to BuiltinPresets() and the
    '  per-preset JSON list — schema is regenerated from the
    '  preset list on every form open.
    ' ============================================================

    Public Function GetTargetDirectoryRef() As String _
            Implements IFileGenerationProvider.GetTargetDirectoryRef
        Return "saves"
    End Function

    Public Function GetButtonLabel() As String _
            Implements IFileGenerationProvider.GetButtonLabel
        Return "Generate New Map..."
    End Function

    Public Function GetTabTitle() As String _
            Implements IFileGenerationProvider.GetTabTitle
        Return "Generate Map"
    End Function

    Public Function GetGenerationSchema(instanceConfig As InstanceConfig) _
            As IReadOnlyList(Of ConfigFieldDescriptor) _
            Implements IFileGenerationProvider.GetGenerationSchema
        Dim presets = BuiltinPresets()
        Dim presetNames = presets.Select(Function(p) p.DisplayName).ToList()
        Dim defaultPresetName = If(presets.Count > 0, presets(0).DisplayName, "Default")

        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "Preset",
                .Label = "Preset",
                .Description = "Pick a Factorio map generation preset. Default is balanced; Death World ramps biters; Rail World spaces resources for long-range logistics; Ribbon World produces a thin horizontal strip.",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = presetNames,
                .DefaultValue = defaultPresetName
            },
            New ConfigFieldDescriptor With {
                .Key = "SaveName",
                .Label = "Save name",
                .Description = "Filename for the new save. The .zip extension is added automatically if you don't include it.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "new-map",
                .IsRequired = True
            },
            New ConfigFieldDescriptor With {
                .Key = "Seed",
                .Label = "Seed (optional)",
                .Description = "Map generation seed. Leave blank for a random seed. Must be a non-negative integer if provided (Factorio's --map-gen-seed expects a uint32).",
                .FieldType = ConfigFieldType.Text
            }
        }
    End Function

    Public Function BuildGenerationSteps(values As Dictionary(Of String, String),
                                          instanceConfig As InstanceConfig) _
            As GenerationStepBundle _
            Implements IFileGenerationProvider.BuildGenerationSteps

        Dim presetDisplay = GetField(values, "Preset")
        Dim saveNameRaw = GetField(values, "SaveName")
        Dim seedRaw = If(GetField(values, "Seed"), "").Trim()

        Dim presetKey = ResolvePresetKeyFromDisplay(presetDisplay)
        If String.IsNullOrEmpty(presetKey) Then
            Throw New InvalidOperationException("Pick a preset.")
        End If

        ' Normalise the requested filename: strip any path the
        ' user might have typed, ensure .zip extension. Saves
        ' are always under <install>/saves/ for Factorio.
        Dim cleanName = If(saveNameRaw, "").Trim()
        cleanName = Path.GetFileName(cleanName)
        If String.IsNullOrEmpty(cleanName) Then
            Throw New InvalidOperationException("Save name is required.")
        End If
        If Not cleanName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then
            cleanName &= ".zip"
        End If

        ' Pre-flight the seed. Factorio's --map-gen-seed expects
        ' an unsigned 32-bit integer; rejecting locally beats
        ' surfacing a stack trace from the engine.
        If Not String.IsNullOrEmpty(seedRaw) Then
            Dim parsedSeed As UInteger
            If Not UInteger.TryParse(seedRaw, parsedSeed) Then
                Throw New InvalidOperationException(
                    "Seed must be a non-negative integer (0 to 4294967295) or blank.")
            End If
        End If

        Dim relativeOutput = "saves/" & cleanName

        ' Map-gen-settings JSON — written to the install root with
        ' a per-generation filename so concurrent generations on
        ' the same install don't stomp each other's settings file.
        Dim settingsRel = $"map-gen-settings-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json"
        Dim presetJson = ResolvePresetJson(presetKey)

        Dim steps As New List(Of InstallStep)
        steps.Add(New WriteFileStep With {
            .StepName = "Write map generation settings",
            .Description = $"Write {presetKey} preset to {settingsRel}",
            .RelativePath = settingsRel,
            .Content = presetJson,
            .OverwriteExisting = True
        })

        Dim args As New System.Text.StringBuilder()
        args.Append("--create ")
        args.Append(QuoteIfNeeded(relativeOutput))
        args.Append(" --map-gen-settings ")
        args.Append(QuoteIfNeeded(settingsRel))
        If Not String.IsNullOrEmpty(seedRaw) Then
            args.Append(" --map-gen-seed ")
            args.Append(seedRaw)
        End If

        ' Pick the right executable name based on the node's OS,
        ' which arrived on InstanceConfig.Platform from the manager
        ' before this method was called. Forward slashes throughout
        ' so the path stays valid across the manager-Windows-host /
        ' node-Linux-target marshalling boundary. NodePlatform.Unknown
        ' (older nodes) defaults to .exe and relies on the node's
        ' MapGenerationRunner extension fallback to strip it on
        ' Linux — belt-and-braces for cross-version operation.
        Dim exeName As String
        If instanceConfig IsNot Nothing AndAlso instanceConfig.Platform = NodePlatform.Linux Then
            exeName = "bin/x64/factorio"
        Else
            exeName = "bin/x64/factorio.exe"
        End If

        steps.Add(New RunProcessStep With {
            .StepName = "Generate map",
            .Description = $"Run factorio --create {relativeOutput}",
            .ExecutablePath = exeName,
            .Arguments = args.ToString(),
            .WorkingDirectory = "",
            .TimeoutMs = 600000,
            .ExpectedExitCode = 0
        })

        Return New GenerationStepBundle With {
            .Steps = steps,
            .ExpectedOutputRelativePath = relativeOutput,
            .TimeoutSeconds = 600
        }
    End Function

    ''' <summary>
    ''' Internal preset list — single source of truth for both
    ''' the schema's Enum values and the JSON blob lookup.
    ''' Order matters: index 0 is the form's default selection.
    ''' </summary>
    Private Class FactorioPreset
        Public Property Key As String
        Public Property DisplayName As String
    End Class

    Private Shared Function BuiltinPresets() As List(Of FactorioPreset)
        Return New List(Of FactorioPreset) From {
            New FactorioPreset With {.Key = "default", .DisplayName = "Default"},
            New FactorioPreset With {.Key = "death-world", .DisplayName = "Death World"},
            New FactorioPreset With {.Key = "rail-world", .DisplayName = "Rail World"},
            New FactorioPreset With {.Key = "ribbon-world", .DisplayName = "Ribbon World"},
            New FactorioPreset With {.Key = "rich-resources", .DisplayName = "Rich Resources"},
            New FactorioPreset With {.Key = "lakes", .DisplayName = "Lakes"},
            New FactorioPreset With {.Key = "island", .DisplayName = "Island"}
        }
    End Function

    Private Shared Function ResolvePresetKeyFromDisplay(displayName As String) As String
        If String.IsNullOrEmpty(displayName) Then Return "default"
        For Each p In BuiltinPresets()
            If String.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase) Then
                Return p.Key
            End If
        Next
        ' Fallback to default rather than throwing; user changing
        ' the schema source between sessions could leave a stale
        ' display name in their form values.
        Return "default"
    End Function

    ''' <summary>
    ''' Strip-and-quote helper for paths/filenames passed to
    ''' factorio.exe via Arguments. Factorio accepts unquoted
    ''' paths without spaces, but the saves directory may live
    ''' under a path that doesn't — wrap in double quotes if so.
    ''' Forward slashes pass through unchanged on both platforms.
    ''' </summary>
    Private Shared Function QuoteIfNeeded(value As String) As String
        If String.IsNullOrEmpty(value) Then Return ""
        If value.IndexOf(" "c) < 0 AndAlso value.IndexOf(""""c) < 0 Then
            Return value
        End If
        Return """" & value.Replace("""", "\""") & """"
    End Function

    Private Shared Function ResolvePresetJson(presetKey As String) As String
        Select Case presetKey
            Case "death-world" : Return DeathWorldJson()
            Case "rail-world" : Return RailWorldJson()
            Case "ribbon-world" : Return RibbonWorldJson()
            Case "rich-resources" : Return RichResourcesJson()
            Case "lakes" : Return LakesJson()
            Case "island" : Return IslandJson()
            Case Else : Return DefaultPresetJson()
        End Select
    End Function

    ' ============================================================
    '  Preset JSON blobs
    '
    '  These mirror the in-game preset JSONs as of Factorio 2.x.
    '  May drift if the engine adds or revises presets in the
    '  future. To verify against the running engine: launch
    '  Factorio interactively, pick the preset, click "Export to
    '  string" on the map gen settings dialog, then run
    '    /c game.write_file("preset.json",
    '      game.json_to_table(
    '        helpers.parse_map_exchange_string("...")))
    '  to get the canonical JSON.
    '
    '  Indentation is single-spaces to keep the strings compact;
    '  Factorio doesn't care about whitespace.
    ' ============================================================

    Private Shared Function DefaultPresetJson() As String
        Return "{" &
               """terrain_segmentation"":1," &
               """water"":1," &
               """width"":0," &
               """height"":0," &
               """starting_area"":1," &
               """peaceful_mode"":false," &
               """autoplace_controls"":{}" &
               "}"
    End Function

    Private Shared Function DeathWorldJson() As String
        ' Higher biter frequency, faster evolution from time +
        ' pollution + destruction.
        Return "{" &
               """terrain_segmentation"":1," &
               """water"":1," &
               """width"":0," &
               """height"":0," &
               """starting_area"":1," &
               """peaceful_mode"":false," &
               """autoplace_controls"":{" &
               """enemy-base"":{""frequency"":4,""size"":1,""richness"":1}" &
               "}" &
               "}"
    End Function

    Private Shared Function RailWorldJson() As String
        ' Sparse resources spaced far apart, larger starting area.
        Return "{" &
               """terrain_segmentation"":1," &
               """water"":1," &
               """width"":0," &
               """height"":0," &
               """starting_area"":2," &
               """peaceful_mode"":false," &
               """autoplace_controls"":{" &
               """coal"":{""frequency"":0.5,""size"":1,""richness"":3}," &
               """copper-ore"":{""frequency"":0.5,""size"":1,""richness"":3}," &
               """iron-ore"":{""frequency"":0.5,""size"":1,""richness"":3}," &
               """stone"":{""frequency"":0.5,""size"":1,""richness"":3}," &
               """crude-oil"":{""frequency"":0.5,""size"":1,""richness"":3}," &
               """uranium-ore"":{""frequency"":0.5,""size"":1,""richness"":3}," &
               """enemy-base"":{""frequency"":1,""size"":1,""richness"":1}" &
               "}" &
               "}"
    End Function

    Private Shared Function RibbonWorldJson() As String
        ' Height clamped to 64 tiles; width unconstrained.
        Return "{" &
               """terrain_segmentation"":1," &
               """water"":1," &
               """width"":0," &
               """height"":64," &
               """starting_area"":1," &
               """peaceful_mode"":false," &
               """autoplace_controls"":{}" &
               "}"
    End Function

    Private Shared Function RichResourcesJson() As String
        ' All resources at maximum richness, slightly bumped
        ' frequency. Aimed at megabase-style play where running
        ' out of a patch shouldn't be the limiting factor.
        Return "{" &
               """terrain_segmentation"":1," &
               """water"":1," &
               """width"":0," &
               """height"":0," &
               """starting_area"":1," &
               """peaceful_mode"":false," &
               """autoplace_controls"":{" &
               """coal"":{""frequency"":1,""size"":1,""richness"":6}," &
               """copper-ore"":{""frequency"":1,""size"":1,""richness"":6}," &
               """iron-ore"":{""frequency"":1,""size"":1,""richness"":6}," &
               """stone"":{""frequency"":1,""size"":1,""richness"":6}," &
               """crude-oil"":{""frequency"":1,""size"":1,""richness"":6}," &
               """uranium-ore"":{""frequency"":1,""size"":1,""richness"":6}" &
               "}" &
               "}"
    End Function

    Private Shared Function LakesJson() As String
        ' Heavy water coverage: high water frequency and bigger
        ' bodies. Land is still continuous — just chopped up by
        ' pools and rivers — unlike the Island preset below which
        ' isolates the starting area.
        Return "{" &
               """terrain_segmentation"":2," &
               """water"":4," &
               """width"":0," &
               """height"":0," &
               """starting_area"":1," &
               """peaceful_mode"":false," &
               """autoplace_controls"":{}" &
               "}"
    End Function

    Private Shared Function IslandJson() As String
        ' Starting area surrounded by water. Small starting area
        ' so resources are tight; player has to plan an off-island
        ' expansion early. The starting_area=0.5 + high water +
        ' coarse segmentation combo is what produces the classic
        ' "land mass surrounded by ocean" look in Factorio.
        Return "{" &
               """terrain_segmentation"":0.5," &
               """water"":6," &
               """width"":0," &
               """height"":0," &
               """starting_area"":0.5," &
               """peaceful_mode"":false," &
               """autoplace_controls"":{}" &
               "}"
    End Function

    ' ============================================================
    '  IInstanceFileEditorProvider — server-settings.json editor
    '
    '  Phase 4c-4. Surfaces the most commonly-edited fields of
    '  Factorio's server-settings.json as a structured form so
    '  users don't have to crack open the JSON to change the
    '  server name. Schema is intentionally flat — 18 fields
    '  ordered by topic (identity → visibility → auth → gameplay
    '  → saves) since SchemaFormBuilder doesn't support section
    '  headers yet. Adding section breaks is a v2 polish item.
    '
    '  Fields not exposed to the form (segment_size tuning,
    '  upload bandwidth caps, etc.) round-trip verbatim through
    '  WriteValuesToFile's preserve-existing-text behaviour. A
    '  user who hand-edits those keeps them across saves.
    '
    '  Visibility is flattened from the nested
    '  visibility:{public,lan} object into two top-level form
    '  fields (VisibilityPublic, VisibilityLan); the writer
    '  reconstructs the nested shape. Tags are presented as a
    '  comma-separated text field rather than introducing a new
    '  StringList ConfigFieldType for one use case —
    '  WriteValuesToFile splits on comma and trims.
    '
    '  RelativePath comes from the instance's ServerSettings
    '  config field (default "server-settings.json") so users who
    '  point the server at a different settings file get the
    '  editor for THAT file rather than the default location.
    ' ============================================================

    Public Function GetInstanceFileEditors(config As InstanceConfig) _
            As IReadOnlyList(Of InstanceFileEditor) _
            Implements IInstanceFileEditorProvider.GetInstanceFileEditors

        ' Pull the path from the instance's ServerSettings field;
        ' fall back to the default if not set. Matches what
        ' BuildLaunchArguments passes to factorio.exe via
        ' --server-settings, so the editor and the runtime always
        ' see the same file.
        Dim settingsPath = GetField(config?.CustomFields, "ServerSettings")
        If String.IsNullOrEmpty(settingsPath) Then settingsPath = "server-settings.json"

        Return New InstanceFileEditor() {
            New InstanceFileEditor With {
                .Key = "server-settings",
                .TabTitle = "Server Settings File",
                .RelativePath = settingsPath,
                .Schema = BuildServerSettingsSchema()
            }
        }
    End Function

    Private Shared Function BuildServerSettingsSchema() As IReadOnlyList(Of ConfigFieldDescriptor)
        ' Field order is the user-facing read order: identity
        ' first (what the server is called), then who can find it
        ' (visibility), then who can join (auth), then how it
        ' plays (gameplay rules), then how it saves. Descriptions
        ' carry [Section] prefixes since we can't render headers
        ' until SchemaFormBuilder grows that support.
        Return New ConfigFieldDescriptor() {
            New ConfigFieldDescriptor With {
                .Key = "Name",
                .Label = "Server name",
                .Description = "[Identity] Shown in the server browser. This is the field that says 'Factorio Server' until you change it.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "Factorio Server"
            },
            New ConfigFieldDescriptor With {
                .Key = "Description",
                .Label = "Description",
                .Description = "[Identity] Free-form description shown in the server browser.",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "Managed by PowerGSM"
            },
            New ConfigFieldDescriptor With {
                .Key = "Tags",
                .Label = "Tags",
                .Description = "[Identity] Comma-separated tags shown next to the server name. Example: game, survival, beginner-friendly",
                .FieldType = ConfigFieldType.Text,
                .DefaultValue = "game"
            },
            New ConfigFieldDescriptor With {
                .Key = "MaxPlayers",
                .Label = "Max players",
                .Description = "[Identity] Maximum simultaneous players. 0 = unlimited.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "0",
                .MinValue = 0,
                .MaxValue = 1024
            },
            New ConfigFieldDescriptor With {
                .Key = "VisibilityPublic",
                .Label = "Public visibility",
                .Description = "[Visibility] List on Factorio's public server browser. Requires a username + token below to register with the matchmaking servers.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
            },
            New ConfigFieldDescriptor With {
                .Key = "VisibilityLan",
                .Label = "LAN visibility",
                .Description = "[Visibility] Discoverable on the local network without internet matchmaking.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "Username",
                .Label = "factorio.com username",
                .Description = "[Auth] Required when public visibility is on. The account that 'owns' the server in Factorio's matchmaking.",
                .FieldType = ConfigFieldType.Text
            },
            New ConfigFieldDescriptor With {
                .Key = "Token",
                .Label = "factorio.com token",
                .Description = "[Auth] Auth token for the username above. Generate at factorio.com/profile. Token-based auth is preferred over password — it can be revoked without changing your account password.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True
            },
            New ConfigFieldDescriptor With {
                .Key = "GamePassword",
                .Label = "Game password",
                .Description = "[Auth] Password clients must enter to join. Different from the factorio.com auth above. Leave blank for an open server.",
                .FieldType = ConfigFieldType.Password,
                .IsSensitive = True
            },
            New ConfigFieldDescriptor With {
                .Key = "RequireUserVerification",
                .Label = "Require user verification",
                .Description = "[Auth] Verify each connecting client against factorio.com. Recommended on; disable only for offline LAN play.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "AllowCommands",
                .Label = "Allow /commands",
                .Description = "[Gameplay] Who can use Lua console commands. 'admins-only' is the safe default — 'true' lets any player run /c game.player.cheat() type commands.",
                .FieldType = ConfigFieldType.[Enum],
                .EnumValues = New List(Of String) From {"true", "false", "admins-only"},
                .DefaultValue = "admins-only"
            },
            New ConfigFieldDescriptor With {
                .Key = "AutoPause",
                .Label = "Auto-pause when empty",
                .Description = "[Gameplay] Pause the simulation when no players are connected. Saves CPU on idle servers.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "OnlyAdminsCanPause",
                .Label = "Only admins can pause",
                .Description = "[Gameplay] Restrict the manual /pause command to admins.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "AfkAutokickInterval",
                .Label = "AFK auto-kick (minutes)",
                .Description = "[Gameplay] Disconnect players idle this long. 0 = never kick AFK players.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "0",
                .MinValue = 0,
                .MaxValue = 1440
            },
            New ConfigFieldDescriptor With {
                .Key = "AutosaveInterval",
                .Label = "Autosave interval (minutes)",
                .Description = "[Saves] Time between automatic saves.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "10",
                .MinValue = 1,
                .MaxValue = 1440
            },
            New ConfigFieldDescriptor With {
                .Key = "AutosaveSlots",
                .Label = "Autosave slots",
                .Description = "[Saves] Number of rolling autosave files to keep.",
                .FieldType = ConfigFieldType.IntegerField,
                .DefaultValue = "5",
                .MinValue = 1,
                .MaxValue = 100
            },
            New ConfigFieldDescriptor With {
                .Key = "AutosaveOnlyOnServer",
                .Label = "Autosave only on server",
                .Description = "[Saves] When on, only the server (not connected clients) writes autosave files. Recommended on for headless servers.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "true"
            },
            New ConfigFieldDescriptor With {
                .Key = "NonBlockingSaving",
                .Label = "Non-blocking saving",
                .Description = "[Saves] Save in a background thread — the game keeps running. Recommended on systems with fast SSDs; can cause hitches on slower disks.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "false"
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
            ' Malformed JSON — return empty dict so the form falls
            ' back to schema defaults rather than throwing in the
            ' panel's load path. User sees "Loaded" with default
            ' values; on save the malformed file gets replaced with
            ' a clean one (existingText still passed in case they
            ' want to preserve content, but the parse there will
            ' also fail and start fresh).
            Return values
        End Try
        If root Is Nothing Then Return values

        ' Top-level scalars
        values("Name") = ReadString(root, "name")
        values("Description") = ReadString(root, "description")
        values("MaxPlayers") = ReadInt(root, "max_players", 0).ToString()
        values("Username") = ReadString(root, "username")
        values("Token") = ReadString(root, "token")
        values("GamePassword") = ReadString(root, "game_password")
        values("RequireUserVerification") = ReadBool(root, "require_user_verification", True).ToString().ToLower()
        values("AllowCommands") = ReadAllowCommands(root)
        values("AutoPause") = ReadBool(root, "auto_pause", True).ToString().ToLower()
        values("OnlyAdminsCanPause") = ReadBool(root, "only_admins_can_pause_the_game", True).ToString().ToLower()
        values("AfkAutokickInterval") = ReadInt(root, "afk_autokick_interval", 0).ToString()
        values("AutosaveInterval") = ReadInt(root, "autosave_interval", 10).ToString()
        values("AutosaveSlots") = ReadInt(root, "autosave_slots", 5).ToString()
        values("AutosaveOnlyOnServer") = ReadBool(root, "autosave_only_on_server", True).ToString().ToLower()
        values("NonBlockingSaving") = ReadBool(root, "non_blocking_saving", False).ToString().ToLower()

        ' Nested visibility object
        Dim visibility = TryCast(root("visibility"), JsonObject)
        If visibility IsNot Nothing Then
            values("VisibilityPublic") = ReadBool(visibility, "public", False).ToString().ToLower()
            values("VisibilityLan") = ReadBool(visibility, "lan", True).ToString().ToLower()
        End If

        ' Tags array → comma-separated string
        Dim tagsNode = TryCast(root("tags"), JsonArray)
        If tagsNode IsNot Nothing Then
            Dim tagList As New List(Of String)
            For Each t In tagsNode
                If t Is Nothing Then Continue For
                Try
                    Dim s = t.GetValue(Of String)()
                    If Not String.IsNullOrEmpty(s) Then tagList.Add(s)
                Catch
                End Try
            Next
            values("Tags") = String.Join(", ", tagList)
        End If

        Return values
    End Function

    Public Function WriteValuesToFile(editorKey As String,
                                       values As Dictionary(Of String, String),
                                       existingText As String) As String _
            Implements IInstanceFileEditorProvider.WriteValuesToFile

        ' Start from the existing JSON if available so unknown
        ' top-level fields (segment_size_*, upload caps, anything
        ' we don't expose) round-trip unchanged. Malformed or
        ' missing existing text starts fresh.
        Dim root As JsonObject = Nothing
        If Not String.IsNullOrWhiteSpace(existingText) Then
            Try
                root = TryCast(JsonNode.Parse(existingText), JsonObject)
            Catch
                ' Existing text was malformed — build fresh.
            End Try
        End If
        If root Is Nothing Then root = New JsonObject()

        ' Top-level scalars
        SetString(root, "name", If(values, Nothing), "Name", "Factorio Server")
        SetString(root, "description", If(values, Nothing), "Description", "")
        SetInt(root, "max_players", If(values, Nothing), "MaxPlayers", 0)
        SetString(root, "username", If(values, Nothing), "Username", "")
        SetString(root, "token", If(values, Nothing), "Token", "")
        SetString(root, "game_password", If(values, Nothing), "GamePassword", "")
        SetBool(root, "require_user_verification", If(values, Nothing), "RequireUserVerification", True)
        SetAllowCommands(root, If(values, Nothing))
        SetBool(root, "auto_pause", If(values, Nothing), "AutoPause", True)
        SetBool(root, "only_admins_can_pause_the_game", If(values, Nothing), "OnlyAdminsCanPause", True)
        SetInt(root, "afk_autokick_interval", If(values, Nothing), "AfkAutokickInterval", 0)
        SetInt(root, "autosave_interval", If(values, Nothing), "AutosaveInterval", 10)
        SetInt(root, "autosave_slots", If(values, Nothing), "AutosaveSlots", 5)
        SetBool(root, "autosave_only_on_server", If(values, Nothing), "AutosaveOnlyOnServer", True)
        SetBool(root, "non_blocking_saving", If(values, Nothing), "NonBlockingSaving", False)

        ' Nested visibility — preserve other sub-fields if any
        ' (e.g. "steam" on older Factorio versions) by reading
        ' the existing object and only overwriting public/lan.
        Dim visibility = TryCast(root("visibility"), JsonObject)
        If visibility Is Nothing Then
            visibility = New JsonObject()
            root("visibility") = visibility
        End If
        SetBool(visibility, "public", If(values, Nothing), "VisibilityPublic", False)
        SetBool(visibility, "lan", If(values, Nothing), "VisibilityLan", True)

        ' Tags: comma-separated string → array of strings
        Dim tagsRaw = GetField(values, "Tags")
        Dim tagArray As New JsonArray()
        If Not String.IsNullOrEmpty(tagsRaw) Then
            For Each tag In tagsRaw.Split(","c)
                Dim trimmed = tag.Trim()
                If Not String.IsNullOrEmpty(trimmed) Then
                    tagArray.Add(JsonValue.Create(trimmed))
                End If
            Next
        End If
        root("tags") = tagArray

        ' Indented output so the file remains hand-readable.
        Dim opts As New JsonSerializerOptions With {.WriteIndented = True}
        Return root.ToJsonString(opts)
    End Function

    ' ---- JSON read helpers ----

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

    ''' <summary>
    ''' allow_commands is the one Factorio field that's typed
    ''' as either string ("admins-only") or boolean (true/false)
    ''' depending on which form the user wrote. Old example files
    ''' use bool; the modern docs use string. Read whichever the
    ''' file has and normalise to the string form the schema's
    ''' Enum dropdown expects.
    ''' </summary>
    Private Shared Function ReadAllowCommands(node As JsonNode) As String
        If node Is Nothing Then Return "admins-only"
        Dim child = node("allow_commands")
        If child Is Nothing Then Return "admins-only"
        Try
            Dim s = child.GetValue(Of String)()
            If Not String.IsNullOrEmpty(s) Then Return s
        Catch
        End Try
        Try
            Dim b = child.GetValue(Of Boolean)()
            Return If(b, "true", "false")
        Catch
        End Try
        Return "admins-only"
    End Function

    ' ---- JSON write helpers ----

    Private Shared Sub SetString(obj As JsonObject,
                                  jsonKey As String,
                                  values As Dictionary(Of String, String),
                                  formKey As String,
                                  fallback As String)
        Dim raw = If(GetField(values, formKey), fallback)
        obj(jsonKey) = JsonValue.Create(raw)
    End Sub

    Private Shared Sub SetInt(obj As JsonObject,
                               jsonKey As String,
                               values As Dictionary(Of String, String),
                               formKey As String,
                               fallback As Integer)
        Dim raw = GetField(values, formKey)
        Dim parsed As Integer
        If Not Integer.TryParse(raw, parsed) Then parsed = fallback
        obj(jsonKey) = JsonValue.Create(parsed)
    End Sub

    Private Shared Sub SetBool(obj As JsonObject,
                                jsonKey As String,
                                values As Dictionary(Of String, String),
                                formKey As String,
                                fallback As Boolean)
        Dim raw = GetField(values, formKey)
        Dim parsed As Boolean
        If Not Boolean.TryParse(raw, parsed) Then parsed = fallback
        obj(jsonKey) = JsonValue.Create(parsed)
    End Sub

    ''' <summary>
    ''' allow_commands may legitimately be either a string (the
    ''' Factorio docs' canonical form for "admins-only") or a
    ''' boolean (the older form for the on/off cases). The schema's
    ''' Enum offers all three values as strings, but we serialise
    ''' "true"/"false" as actual JSON booleans because that's what
    ''' Factorio's parser expects when those values are chosen —
    ''' it rejects the strings "true"/"false" as invalid for that
    ''' field. Only "admins-only" goes out as a string.
    ''' </summary>
    Private Shared Sub SetAllowCommands(obj As JsonObject,
                                          values As Dictionary(Of String, String))
        Dim raw = If(GetField(values, "AllowCommands"), "admins-only")
        Select Case raw.ToLowerInvariant()
            Case "true"
                obj("allow_commands") = JsonValue.Create(True)
            Case "false"
                obj("allow_commands") = JsonValue.Create(False)
            Case Else
                obj("allow_commands") = JsonValue.Create(raw)
        End Select
    End Sub

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
