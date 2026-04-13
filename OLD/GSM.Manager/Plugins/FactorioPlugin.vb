Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin

' ============================================================
'  Factorio Game Plugin
'
'  Key characteristics:
'    - Two install methods: SteamCMD (AppID 427520) or direct
'      download of the headless server package from
'      factorio.com/get-download/{version}/headless/linux64
'    - Stable executable name: factorio (Linux) / factorio.exe (Win)
'    - One installation = one instance (no multi-instance-per-
'      install pattern like Last Oasis). Each instance gets its
'      own installation, or shares one with separate --server-
'      settings and --port flags if the operator chooses.
'    - RCON uses the Source RCON protocol (Factorio deliberately
'      implemented it for compatibility)
'    - Mod support via the Factorio Mod Portal API (public,
'      no auth required for download)
'    - Player tracking via stdout and/or factorio-current.log
'    - Version detection: factorio --version output or
'      info.json in the install directory
'    - Direct download requires a factorio.com account token
'      for non-experimental builds (stored as SteamCredential
'      analogue - see FactorioCredential below)
'
'  Installation config keys:
'    SteamCredentialId       - Steam account (SteamCMD method only)
'    FactorioCredentialId    - factorio.com account (direct download)
'    InstallMethod           - "SteamCMD" or "DirectDownload"
'    TargetVersion           - Specific version or "latest"
'
'  Instance config keys:
'    Port                    - Game port (UDP/TCP). Default 34197.
'    RconPort                - RCON port. Default 27015.
'    RconPassword            - Required for RCON. No default.
'    ServerSettingsPath      - Path to server-settings.json.
'                              Relative to working dir or absolute.
'    MapPath                 - Path to the save file to load.
'    ServerAdminlistPath     - Optional. Path to server-adminlist.json
'    ServerBanlistPath       - Optional. Path to server-banlist.json
'    ServerWhitelistPath     - Optional. Path to server-whitelist.json
'    UseModsDir              - Custom mods directory path. Blank =
'                              default per-instance mods dir.
'    MaxUploadSpeed          - Kbps. 0 = unlimited.
'    MaxUploadSlots          - 0 = unlimited.
' ============================================================

Namespace GSM.Plugins.Factorio

    Public Class FactorioPlugin
        Implements IGamePlugin

        ' ---- Identity ----

        Public ReadOnly Property GameId As String = "factorio" _
            Implements IGamePlugin.GameId

        Public ReadOnly Property DisplayName As String = "Factorio" _
            Implements IGamePlugin.DisplayName

        ' Base URL for headless server downloads.
        ' Full URL: {Base}/{version}/headless/{platform}
        Private Const DirectDownloadBase As String =
            "https://factorio.com/get-download"

        ' Steam AppID for Factorio (same for client and dedicated -
        ' Factorio does not have a separate server AppID).
        Private Const SteamAppId As String = "427520"


        ' ============================================================
        '  INSTALL
        ' ============================================================

        Public Function GetSupportedInstallMethods() As IReadOnlyList(Of InstallMethod) _
                Implements IGamePlugin.GetSupportedInstallMethods
            Return {InstallMethod.SteamCMD, InstallMethod.DirectDownload}
        End Function

        Public Function GetInstallSteps(path As String,
                                        method As InstallMethod,
                                        config As InstallationConfig) As IReadOnlyList(Of InstallStep) _
                Implements IGamePlugin.GetInstallSteps

            Dim cfg = FactorioConfig.ParseInstallationConfig(config.RawJson)
            Dim steps As New List(Of InstallStep)

            Select Case method

                Case InstallMethod.SteamCMD
                    ' Factorio on Steam uses the same AppID for client and
                    ' dedicated server. The headless server is launched with
                    ' --start-server rather than opening the GUI.
                    ' A Steam account that owns Factorio is required -
                    ' anonymous download is NOT supported for AppID 427520.
                    steps.Add(New SteamCmdInstallStep With {
                        .Description = "Download/update Factorio via SteamCMD",
                        .AppId = SteamAppId,
                        .InstallDir = path,
                        .Branch = If(config.SteamBranch, ""),
                        .BranchPassword = config.SteamBranchPassword,
                        .ValidateFiles = True,
                        .SteamCredentialId = cfg.SteamCredentialId  ' Ownership required
                    })

                Case InstallMethod.DirectDownload
                    ' Factorio provides a headless Linux server package at
                    ' factorio.com/get-download/{version}/headless/linux64
                    ' A factorio.com account token is required.
                    ' Windows headless is not officially supported - direct
                    ' download installs target Linux nodes only.
                    Dim version = If(String.IsNullOrWhiteSpace(cfg.TargetVersion),
                                     "latest", cfg.TargetVersion)
                    Dim platform = "linux64"
                    Dim url = $"{DirectDownloadBase}/{version}/headless/{platform}"

                    ' Sha256 omitted - factorio.com doesn't publish checksums
                    ' for headless packages. ValidateInstall checks the binary.
                    steps.Add(New DownloadInstallStep With {
                        .Description = $"Download Factorio headless server {version}",
                        .Url = url,
                        .ExtractToPath = path
                    })

                    ' After extraction, make the binary executable on Linux.
                    steps.Add(New RunCommandStep With {
                        .Description = "Set executable permission on factorio binary",
                        .Executable = "bash",
                        .Arguments = $"-c ""chmod +x '{IO.Path.Combine(path, "bin", "x64", "factorio")}'""",
                        .WorkingDirectory = path,
                        .ExpectExitCode = 0
                    })

            End Select

            ' Create a default server-settings.json if one doesn't exist yet.
            ' Factorio refuses to start without this file.
            Dim settingsTarget = IO.Path.Combine(path, "data", "server-settings.example.json")
            steps.Add(New RunCommandStep With {
                .Description = "Copy example server-settings.json if not already present",
                .Executable = If(IsWindowsPath(path), "cmd.exe", "bash"),
                .Arguments = If(IsWindowsPath(path),
                    $"/c if not exist ""{IO.Path.Combine(path, "server-settings.json")}"" " &
                    $"copy ""{settingsTarget}"" ""{IO.Path.Combine(path, "server-settings.json")}""",
                    $"-c ""[ -f '{IO.Path.Combine(path, "server-settings.json")}' ] || " &
                    $"cp '{settingsTarget}' '{IO.Path.Combine(path, "server-settings.json")}'"""),
                .WorkingDirectory = path,
                .ExpectExitCode = 0
            })

            Return steps
        End Function

        Public Function ValidateInstall(path As String) As ValidationResult _
                Implements IGamePlugin.ValidateInstall

            If Not Directory.Exists(path) Then
                Return ValidationResult.Fail($"Install directory does not exist: {path}")
            End If

            If FindExecutable(path) Is Nothing Then
                Return ValidationResult.Fail(
                    $"Factorio executable not found under {path}. " &
                    "Expected at bin\x64\factorio.exe (Windows) or " &
                    "bin/x64/factorio (Linux).")
            End If

            ' data\ directory must exist - contains base game data.
            If Not Directory.Exists(IO.Path.Combine(path, "data")) Then
                Return ValidationResult.Fail(
                    $"Data directory not found under {path}. " &
                    "The install appears incomplete.")
            End If

            Return ValidationResult.Ok()
        End Function

        Public Function GetSteamBranch(config As InstallationConfig) As String _
                Implements IGamePlugin.GetSteamBranch
            If config Is Nothing Then Return String.Empty
            Return If(config.SteamBranch, String.Empty)
        End Function


        ' ============================================================
        '  LAUNCH
        ' ============================================================

        Public Function GetExecutablePath(installPath As String,
                                          instance As InstanceConfig) As String _
                Implements IGamePlugin.GetExecutablePath

            If Not String.IsNullOrWhiteSpace(instance.ExeOverride) Then
                Return IO.Path.Combine(installPath, instance.ExeOverride)
            End If

            Dim resolved = FindExecutable(installPath)
            If resolved Is Nothing Then
                Return IO.Path.Combine(installPath, "bin", "x64", "factorio [NOT FOUND]")
            End If
            Return resolved
        End Function

        Public Function BuildCommandLine(instance As InstanceConfig) As String _
                Implements IGamePlugin.BuildCommandLine

            Dim cfg = FactorioConfig.ParseInstanceConfig(instance.RawJson)
            Dim args As New List(Of String)

            ' Headless dedicated server mode.
            ' --start-server loads a save and begins hosting.
            ' --start-server-load-latest loads the most recent save.
            If Not String.IsNullOrWhiteSpace(cfg.MapPath) Then
                args.Add($"--start-server ""{cfg.MapPath}""")
            Else
                args.Add("--start-server-load-latest")
            End If

            ' Server settings - required. Factorio will not start without it.
            If Not String.IsNullOrWhiteSpace(cfg.ServerSettingsPath) Then
                args.Add($"--server-settings ""{cfg.ServerSettingsPath}""")
            End If

            ' Network
            args.Add($"--port {cfg.Port}")

            ' RCON - only if password is configured.
            If Not String.IsNullOrWhiteSpace(cfg.RconPassword) Then
                args.Add($"--rcon-port {cfg.RconPort}")
                args.Add($"--rcon-password ""{cfg.RconPassword}""")
            End If

            ' Optional player management lists.
            If Not String.IsNullOrWhiteSpace(cfg.ServerAdminlistPath) Then
                args.Add($"--server-adminlist ""{cfg.ServerAdminlistPath}""")
            End If
            If Not String.IsNullOrWhiteSpace(cfg.ServerBanlistPath) Then
                args.Add($"--server-banlist ""{cfg.ServerBanlistPath}""")
            End If
            If Not String.IsNullOrWhiteSpace(cfg.ServerWhitelistPath) Then
                args.Add($"--server-whitelist ""{cfg.ServerWhitelistPath}""")
                args.Add("--use-server-whitelist")
            End If

            ' Custom mods directory (allows per-instance mod sets).
            If Not String.IsNullOrWhiteSpace(cfg.UseModsDir) Then
                args.Add($"--mod-directory ""{cfg.UseModsDir}""")
            End If

            ' Bandwidth limits.
            If cfg.MaxUploadSpeed > 0 Then
                args.Add($"--max-upload-speed {cfg.MaxUploadSpeed}")
            End If
            If cfg.MaxUploadSlots > 0 Then
                args.Add($"--max-upload-slots {cfg.MaxUploadSlots}")
            End If

            Return String.Join(" ", args)
        End Function

        Public Function GetWorkingDirectory(installPath As String,
                                            instance As InstanceConfig) As String _
                Implements IGamePlugin.GetWorkingDirectory
            Return installPath
        End Function


        ' ============================================================
        '  LOGGING
        ' ============================================================

        Public Function GetLogSources(installPath As String,
                                      instance As InstanceConfig) As IReadOnlyList(Of ILogSource) _
                Implements IGamePlugin.GetLogSources

            ' Factorio writes to both stdout and a log file.
            ' stdout is the primary real-time source.
            ' factorio-current.log persists across restarts and is
            ' useful for post-crash analysis and ring buffer replay.
            Return {
                New StdoutLogSource With {
                    .CaptureStderr = True
                },
                New FileLogSource(
                    sourceId:="logfile",
                    pathPattern:="factorio-current.log"
                ) With {
                    .FollowRotation = False  ' Factorio appends to the same file
                }
            }
        End Function

        Public Function GetLogParser() As ILogParser _
                Implements IGamePlugin.GetLogParser
            Return New FactorioLogParser()
        End Function


        ' ============================================================
        '  INSTALL MONITOR
        ' ============================================================

        Public Function GetInstallMonitor() As IInstallMonitor _
                Implements IGamePlugin.GetInstallMonitor
            ' SteamCMD install needs Steam Guard handling.
            ' Direct download has no interactive prompts.
            Return New GSM.Plugins.LastOasis.SteamCmdInstallMonitor()
        End Function


        ' ============================================================
        '  RCON
        ' ============================================================

        Public Function GetRconInfo(instance As InstanceConfig) As RconInfo _
                Implements IGamePlugin.GetRconInfo

            Dim cfg = FactorioConfig.ParseInstanceConfig(instance.RawJson)
            If String.IsNullOrWhiteSpace(cfg.RconPassword) Then Return Nothing

            ' Factorio implements Source RCON protocol.
            ' RCON listener is ready almost immediately after startup.
            Return New RconInfo With {
                .Protocol = RconProtocol.SourceRcon,
                .Port = cfg.RconPort,
                .Password = cfg.RconPassword,
                .AutoConnect = True,
                .StartupDelayMs = 2000,
                .MaxConnectRetries = 5,
                .RetryIntervalMs = 1000,
                .ConnectTimeoutMs = 3000
            }
        End Function


        ' ============================================================
        '  MODS
        ' ============================================================

        Public Function GetModManager() As IModManager _
                Implements IGamePlugin.GetModManager
            Return New FactorioModManager()
        End Function


        ' ============================================================
        '  STARTUP WARNINGS
        ' ============================================================

        Public Function GetStartupWarnings(installPath As String,
                                           instance As InstanceConfig) As IReadOnlyList(Of String) _
                Implements IGamePlugin.GetStartupWarnings

            Dim warnings As New List(Of String)
            Dim cfg = FactorioConfig.ParseInstanceConfig(instance.RawJson)

            If String.IsNullOrWhiteSpace(cfg.ServerSettingsPath) Then
                warnings.Add(
                    $"ServerSettingsPath is not set on instance '{instance.DisplayName}'. " &
                    "Factorio will not start without a server-settings.json file. " &
                    "A default was created at the install root during installation. " &
                    "Set ServerSettingsPath to its location and edit it before starting.")
            End If

            If String.IsNullOrWhiteSpace(cfg.MapPath) Then
                warnings.Add(
                    $"MapPath is not set on instance '{instance.DisplayName}'. " &
                    "The server will attempt to load the most recent save " &
                    "via --start-server-load-latest. This will fail if no saves exist. " &
                    "Consider generating a map first or setting MapPath explicitly.")
            End If

            If String.IsNullOrWhiteSpace(cfg.RconPassword) Then
                warnings.Add(
                    $"RconPassword is not set on instance '{instance.DisplayName}'. " &
                    "RCON will be disabled. In-game commands from the manager " &
                    "and automation rules that use SendRconCommand will not work.")
            End If

            Return warnings
        End Function


        ' ============================================================
        '  CRASH HANDLING
        ' ============================================================

        Public Function GetCleanExitCodes() As IReadOnlyList(Of Integer) _
                Implements IGamePlugin.GetCleanExitCodes
            ' 0 = clean shutdown.
            ' Factorio exits 0 on /quit command and on clean SIGTERM.
            Return {0}
        End Function

        Public Function GetCrashSignalPatterns() As IReadOnlyList(Of String) _
                Implements IGamePlugin.GetCrashSignalPatterns
            Return {
                "Error Util.cpp:",
                "Error Main.cpp:",
                "Segmentation fault",
                "Aborted",
                "terminate called",
                "Signal: ",                ' Factorio logs the signal before dying
                "Error while loading mods:" ' Mod load failure - process exits after this
            }
        End Function


        ' ============================================================
        '  VERSION DETECTION
        ' ============================================================

        Public Function GetCurrentVersion(installPath As String) As String _
                Implements IGamePlugin.GetCurrentVersion
            Try
                ' Primary: read from data/base/info.json - always present.
                Dim infoPath = IO.Path.Combine(installPath, "data", "base", "info.json")
                If File.Exists(infoPath) Then
                    Dim json = File.ReadAllText(infoPath)
                    Using doc = JsonDocument.Parse(json)
                        Try
                            Return doc.RootElement.GetProperty("version").GetString()
                        Catch
                        End Try
                    End Using
                End If

                ' Fallback: run factorio --version and parse output.
                ' Only used if info.json is missing (unusual).
                Dim exe = FindExecutable(installPath)
                If exe IsNot Nothing Then
                    Dim output = RunAndCapture(exe, "--version")
                    Dim m = Regex.Match(output, "Version:\s+([\d.]+)")
                    If m.Success Then Return m.Groups(1).Value
                End If
            Catch
            End Try
            Return String.Empty
        End Function

        Public Async Function GetLatestVersion(config As InstallationConfig,
                                               cancellation As CancellationToken) As Task(Of String) _
                Implements IGamePlugin.GetLatestVersion

            Dim cfg = FactorioConfig.ParseInstallationConfig(config.RawJson)

            ' For SteamCMD installs, query the Steam API.
            If cfg.InstallMethodEnum = InstallMethod.SteamCMD Then
                Try
                    Using client As New HttpClient()
                        client.Timeout = TimeSpan.FromSeconds(10)
                        Dim url = "https://api.steampowered.com/ISteamApps/UpToDateCheck/v1/" &
                                  $"?appid={SteamAppId}&version=0"
                        Dim response = Await client.GetStringAsync(url, cancellation)
                        Dim m = Regex.Match(response, """required_version""\s*:\s*(\d+)")
                        If m.Success Then Return m.Groups(1).Value
                    End Using
                Catch ex As OperationCanceledException
                    Throw
                Catch
                End Try
                Return String.Empty
            End If

            ' For direct download installs, query the Factorio version API.
            ' Returns JSON like: {"stable":{"headless":"1.1.107"},...}
            Try
                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(10)
                    Dim response = Await client.GetStringAsync(
                        "https://factorio.com/api/latest-releases", cancellation)
                    Using doc = JsonDocument.Parse(response)
                        Dim channel = If(cfg.UseExperimental, "experimental", "stable")
                        Try
                            Return doc.RootElement.GetProperty(channel).GetProperty("headless").GetString()
                        Catch
                        End Try
                    End Using
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch
            End Try

            Return String.Empty
        End Function


        ' ============================================================
        '  CONFIG SCHEMA
        ' ============================================================

        Public Function GetInstanceConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) _
                Implements IGamePlugin.GetInstanceConfigSchema
            Return {
                New ConfigFieldDescriptor With {
                    .Key = "ServerSettingsPath",
                    .Label = "Server settings file",
                    .Description = "Path to server-settings.json. " &
                                   "A default copy was created in the install directory during setup. " &
                                   "Edit it to set your server name, description, and other options.",
                    .FieldType = ConfigFieldType.FilePath,
                    .IsRequired = True
                },
                New ConfigFieldDescriptor With {
                    .Key = "MapPath",
                    .Label = "Save file path",
                    .Description = "Path to the .zip save file to load on startup. " &
                                   "Leave blank to load the most recent save automatically. " &
                                   "If no saves exist the server will fail to start.",
                    .FieldType = ConfigFieldType.FilePath
                },
                New ConfigFieldDescriptor With {
                    .Key = "Port",
                    .Label = "Game port (UDP/TCP)",
                    .Description = "Port for player connections. Default 34197.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "34197",
                    .MinValue = 1024,
                    .MaxValue = 65535,
                    .IsRequired = True
                },
                New ConfigFieldDescriptor With {
                    .Key = "RconPort",
                    .Label = "RCON port (TCP)",
                    .Description = "RCON management port. Leave RconPassword blank to disable RCON.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "27015",
                    .MinValue = 1024,
                    .MaxValue = 65535
                },
                New ConfigFieldDescriptor With {
                    .Key = "RconPassword",
                    .Label = "RCON password",
                    .Description = "Required to enable RCON. Without this, in-game commands " &
                                   "from the manager and automation rules will not work.",
                    .FieldType = ConfigFieldType.Password,
                    .IsSensitive = True
                },
                New ConfigFieldDescriptor With {
                    .Key = "ServerAdminlistPath",
                    .Label = "Admin list file",
                    .Description = "Path to server-adminlist.json. Optional.",
                    .FieldType = ConfigFieldType.FilePath
                },
                New ConfigFieldDescriptor With {
                    .Key = "ServerBanlistPath",
                    .Label = "Ban list file",
                    .Description = "Path to server-banlist.json. Optional.",
                    .FieldType = ConfigFieldType.FilePath
                },
                New ConfigFieldDescriptor With {
                    .Key = "ServerWhitelistPath",
                    .Label = "Whitelist file",
                    .Description = "Path to server-whitelist.json. " &
                                   "When set, only whitelisted players can join.",
                    .FieldType = ConfigFieldType.FilePath
                },
                New ConfigFieldDescriptor With {
                    .Key = "UseModsDir",
                    .Label = "Mods directory",
                    .Description = "Custom mods directory for this instance. " &
                                   "Allows each instance to have its own mod set. " &
                                   "Leave blank to use Factorio's default mods location.",
                    .FieldType = ConfigFieldType.DirectoryPath
                },
                New ConfigFieldDescriptor With {
                    .Key = "MaxUploadSpeed",
                    .Label = "Max upload speed (kbps)",
                    .Description = "Upload bandwidth limit in kbps. 0 = unlimited.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "0",
                    .MinValue = 0,
                    .MaxValue = 1000000
                },
                New ConfigFieldDescriptor With {
                    .Key = "MaxUploadSlots",
                    .Label = "Max upload slots",
                    .Description = "Maximum simultaneous upload connections. 0 = unlimited.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "0",
                    .MinValue = 0,
                    .MaxValue = 1000
                }
            }
        End Function

        Public Function GetInstallationConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) _
                Implements IGamePlugin.GetInstallationConfigSchema
            Return {
                New ConfigFieldDescriptor With {
                    .Key = "InstallMethod",
                    .Label = "Install method",
                    .Description = "SteamCMD requires a Steam account that owns Factorio. " &
                                   "DirectDownload fetches the headless Linux server package " &
                                   "from factorio.com and requires a factorio.com account token. " &
                                   "DirectDownload targets Linux nodes only.",
                    .FieldType = ConfigFieldType.Choice,
                    .DefaultValue = "SteamCMD",
                    .Choices = New List(Of String) From {"SteamCMD", "DirectDownload"},
                    .IsRequired = True
                },
                New ConfigFieldDescriptor With {
                    .Key = "SteamCredentialId",
                    .Label = "Steam account",
                    .Description = "Steam account that owns Factorio (AppID 427520). " &
                                   "Required for SteamCMD install. Anonymous login is NOT " &
                                   "supported - Factorio requires ownership. " &
                                   "Manage accounts at Manager → Settings → Steam Accounts.",
                    .FieldType = ConfigFieldType.SteamCredentialPicker,
                    .IsRequired = False     ' Only required when method = SteamCMD
                },
                New ConfigFieldDescriptor With {
                    .Key = "FactorioCredentialId",
                    .Label = "Factorio.com account",
                    .Description = "factorio.com account used to download the headless server. " &
                                   "Required for DirectDownload install. " &
                                   "Manage accounts at Manager → Settings → Factorio Accounts.",
                    .FieldType = ConfigFieldType.SteamCredentialPicker, ' Reuses picker UI
                    .IsRequired = False     ' Only required when method = DirectDownload
                },
                New ConfigFieldDescriptor With {
                    .Key = "TargetVersion",
                    .Label = "Target version",
                    .Description = "Specific version to install e.g. '1.1.107', or leave " &
                                   "blank for the latest stable release.",
                    .FieldType = ConfigFieldType.Text,
                    .DefaultValue = ""
                },
                New ConfigFieldDescriptor With {
                    .Key = "UseExperimental",
                    .Label = "Use experimental branch",
                    .Description = "Install the latest experimental release instead of stable. " &
                                   "Applies to DirectDownload only. For SteamCMD, use the " &
                                   "Steam branch field instead.",
                    .FieldType = ConfigFieldType.BooleanField,
                    .DefaultValue = "false"
                },
                New ConfigFieldDescriptor With {
                    .Key = "SteamBranch",
                    .Label = "Steam branch",
                    .Description = "Steam beta branch. Blank = stable. " &
                                   "Applies to SteamCMD install only.",
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


        ' ============================================================
        '  PRIVATE HELPERS
        ' ============================================================

        Private Function FindExecutable(installPath As String) As String
            ' Windows
            Dim win = IO.Path.Combine(installPath, "bin", "x64", "factorio.exe")
            If File.Exists(win) Then Return win
            ' Linux
            Dim linux = IO.Path.Combine(installPath, "bin", "x64", "factorio")
            If File.Exists(linux) Then Return linux
            Return Nothing
        End Function

        Private Function IsWindowsPath(path As String) As Boolean
            Return path.Length > 1 AndAlso path(1) = ":"c OrElse path.Contains("\"c)
        End Function

        Private Function RunAndCapture(exe As String, args As String) As String
            Try
                Dim psi As New System.Diagnostics.ProcessStartInfo(exe, args) With {
                    .RedirectStandardOutput = True,
                    .UseShellExecute = False,
                    .CreateNoWindow = True
                }
                Using p = System.Diagnostics.Process.Start(psi)
                    Dim output = p.StandardOutput.ReadToEnd()
                    p.WaitForExit(5000)
                    Return output
                End Using
            Catch
                Return String.Empty
            End Try
        End Function

    End Class


    ' ============================================================
    '  SHARED CONFIG MODULE
    '  Typed config classes and parse helpers at namespace level
    '  so both FactorioPlugin and FactorioModManager can use them
    '  without either reaching into the other's internals.
    ' ============================================================

    Friend Module FactorioConfig

        Friend Function ParseInstanceConfig(rawJson As String) As FactorioInstanceConfig
            If String.IsNullOrWhiteSpace(rawJson) Then Return New FactorioInstanceConfig()
            Try
                Return JsonSerializer.Deserialize(Of FactorioInstanceConfig)(rawJson)
            Catch
                Return New FactorioInstanceConfig()
            End Try
        End Function

        Friend Function ParseInstallationConfig(rawJson As String) As FactorioInstallationConfig
            If String.IsNullOrWhiteSpace(rawJson) Then Return New FactorioInstallationConfig()
            Try
                Return JsonSerializer.Deserialize(Of FactorioInstallationConfig)(rawJson)
            Catch
                Return New FactorioInstallationConfig()
            End Try
        End Function

    End Module


    ' ============================================================
    '  TYPED CONFIG CLASSES
    ' ============================================================

    Friend Class FactorioInstanceConfig
        Public Property ServerSettingsPath As String = String.Empty
        Public Property MapPath As String = String.Empty
        Public Property Port As Integer = 34197
        Public Property RconPort As Integer = 27015
        Public Property RconPassword As String = String.Empty
        Public Property ServerAdminlistPath As String = String.Empty
        Public Property ServerBanlistPath As String = String.Empty
        Public Property ServerWhitelistPath As String = String.Empty
        Public Property UseModsDir As String = String.Empty
        Public Property MaxUploadSpeed As Integer = 0
        Public Property MaxUploadSlots As Integer = 0
    End Class

    Friend Class FactorioInstallationConfig
        Public Property InstallMethod As String = "SteamCMD"
        Public Property SteamCredentialId As String = String.Empty
        Public Property FactorioCredentialId As String = String.Empty
        Public Property TargetVersion As String = String.Empty
        Public Property UseExperimental As Boolean = False
        Public Property SteamBranch As String = String.Empty
        Public Property SteamBranchPassword As String = String.Empty

        Public ReadOnly Property InstallMethodEnum As InstallMethod
            Get
                If InstallMethod.Equals("DirectDownload",
                                        StringComparison.OrdinalIgnoreCase) Then
                    Return GSM.Plugin.InstallMethod.DirectDownload
                End If
                Return GSM.Plugin.InstallMethod.SteamCMD
            End Get
        End Property
    End Class


    ' ============================================================
    '  LOG PARSER
    ' ============================================================

    Public Class FactorioLogParser
        Implements ILogParser

        Private ReadOnly _lock As New Object()
        Private ReadOnly _players As New Dictionary(Of String, PlayerInfo)(
            StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _metrics As New Dictionary(Of String, String)

        ' Factorio log patterns (stdout)
        ' Join:  Info ServerMultiplayerManager.cpp:...  PlayerName joined the game
        Private ReadOnly _joinPattern As New Regex(
            "PlayerName\s+(.+?)\s+joined the game",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        ' Leave: Info ServerMultiplayerManager.cpp:...  PlayerName left the game
        Private ReadOnly _leavePattern As New Regex(
            "PlayerName\s+(.+?)\s+left the game",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        ' Alternative join/leave patterns from different log versions
        Private ReadOnly _joinPattern2 As New Regex(
            "\[JOIN\]\s+(.+?)\s+joined the game",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        Private ReadOnly _leavePattern2 As New Regex(
            "\[LEAVE\]\s+(.+?)\s+left the game",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        ' Server ready signal
        Private ReadOnly _readyPattern As New Regex(
            "Hosting game at IP ADDR",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        ' Save completed signal
        Private ReadOnly _savePattern As New Regex(
            "Saving finished",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        Public Sub ProcessLine(sourceId As String,
                               timestamp As DateTime,
                               line As String) _
                Implements ILogParser.ProcessLine

            ' Process both stdout and logfile - Factorio's [JOIN]/[LEAVE]
            ' tags appear in the log file, while verbose join messages
            ' appear in stdout. Dedup by player name in the dictionary.

            ' Try all join patterns
            For Each pattern In {_joinPattern, _joinPattern2}
                Dim m = pattern.Match(line)
                If m.Success Then
                    Dim name = m.Groups(1).Value.Trim()
                    SyncLock _lock
                        If Not _players.ContainsKey(name) Then
                            _players(name) = New PlayerInfo With {
                                .Name = name,
                                .JoinedAt = timestamp,
                                .Platform = "Factorio"
                            }
                        End If
                        _metrics("PlayerCount") = _players.Count.ToString()
                    End SyncLock
                    Return
                End If
            Next

            ' Try all leave patterns
            For Each pattern In {_leavePattern, _leavePattern2}
                Dim m = pattern.Match(line)
                If m.Success Then
                    Dim name = m.Groups(1).Value.Trim()
                    SyncLock _lock
                        _players.Remove(name)
                        _metrics("PlayerCount") = _players.Count.ToString()
                    End SyncLock
                    Return
                End If
            Next

            If _readyPattern.IsMatch(line) Then
                SyncLock _lock
                    _metrics("ServerStatus") = "Ready"
                End SyncLock
                Return
            End If

            If _savePattern.IsMatch(line) Then
                SyncLock _lock
                    _metrics("LastSaveAt") = timestamp.ToString("o")
                End SyncLock
            End If
        End Sub

        Public ReadOnly Property ActivePlayers As IReadOnlyList(Of PlayerInfo) _
                Implements ILogParser.ActivePlayers
            Get
                SyncLock _lock
                    Return _players.Values.ToList().AsReadOnly()
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property CustomMetrics As IReadOnlyDictionary(Of String, String) _
                Implements ILogParser.CustomMetrics
            Get
                SyncLock _lock
                    Return New Dictionary(Of String, String)(_metrics)
                End SyncLock
            End Get
        End Property

        Public Sub Reset() Implements ILogParser.Reset
            SyncLock _lock
                _players.Clear()
                _metrics.Clear()
            End SyncLock
        End Sub

    End Class


    ' ============================================================
    '  MOD MANAGER
    '  Factorio Mod Portal API - public, no auth for downloads.
    '  https://mods.factorio.com/api/mods
    ' ============================================================

    Public Class FactorioModManager
        Implements IModManager

        Public ReadOnly Property ModSource As ModSource = ModSource.FactorioModPortal _
            Implements IModManager.ModSource

        ' Mod list is stored in the instance's mods directory as mod-list.json.
        ' Each entry: { "name": "...", "enabled": true }
        ' The actual mod files are .zip packages in the same directory.

        Public Async Function ListInstalledMods(
                instanceConfig As InstanceConfig) As Task(Of IReadOnlyList(Of ModInfo)) _
                Implements IModManager.ListInstalledMods

            Dim cfg = FactorioConfig.ParseInstanceConfig(instanceConfig.RawJson)
            Dim modsDir = GetModsDirectory(cfg, instanceConfig)
            Dim result As New List(Of ModInfo)

            If Not Directory.Exists(modsDir) Then Return result

            Try
                Dim modListPath = IO.Path.Combine(modsDir, "mod-list.json")
                If Not File.Exists(modListPath) Then Return result

                Dim json = Await File.ReadAllTextAsync(modListPath)
                Dim doc = JsonDocument.Parse(json)
                Dim mods = doc.RootElement.GetProperty("mods")

                For Each entry In mods.EnumerateArray()
                    Dim name = entry.GetProperty("name").GetString()
                    If name = "base" Then Continue For  ' Skip the base game

                    ' Find the installed .zip to get the version
                    Dim zips = Directory.GetFiles(modsDir, $"{name}_*.zip")
                    Dim version = If(zips.Any(),
                        ExtractVersionFromZipName(zips.First(), name),
                        "unknown")

                    result.Add(New ModInfo With {
                        .ModId = name,
                        .DisplayName = name,
                        .Version = version,
                        .Source = ModSource.FactorioModPortal
                    })
                Next
            Catch
            End Try

            Return result
        End Function

        Public Async Function InstallMod(instanceConfig As InstanceConfig,
                                         modId As String,
                                         version As String,
                                         cancellation As CancellationToken) As Task(Of ModInstallResult) _
                Implements IModManager.InstallMod
            Try
                Dim cfg = FactorioConfig.ParseInstanceConfig(instanceConfig.RawJson)
                Dim modsDir = GetModsDirectory(cfg, instanceConfig)
                Directory.CreateDirectory(modsDir)

                ' Query the mod portal for the download URL.
                ' GET https://mods.factorio.com/api/mods/{modId}/full
                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(30)
                    Dim infoUrl = $"https://mods.factorio.com/api/mods/{modId}/full"
                    Dim infoJson = Await client.GetStringAsync(infoUrl, cancellation)
                    Dim doc = JsonDocument.Parse(infoJson)

                    ' Find the release matching the requested version (or latest).
                    Dim releases = doc.RootElement.GetProperty("releases")
                    Dim targetRelease As JsonElement = Nothing

                    If String.IsNullOrWhiteSpace(version) OrElse
                       version.Equals("latest", StringComparison.OrdinalIgnoreCase) Then
                        ' Last release in the array is the latest.
                        targetRelease = releases.EnumerateArray().Last()
                    Else
                        For Each rel In releases.EnumerateArray()
                            If rel.GetProperty("version").GetString() = version Then
                                targetRelease = rel
                                Exit For
                            End If
                        Next
                    End If

                    Dim downloadUrl = "https://mods.factorio.com" &
                                      targetRelease.GetProperty("download_url").GetString()
                    Dim fileName = targetRelease.GetProperty("file_name").GetString()
                    Dim resolvedVersion = targetRelease.GetProperty("version").GetString()

                    ' Download the mod zip.
                    Dim zipBytes = Await client.GetByteArrayAsync(downloadUrl, cancellation)
                    Dim zipPath = IO.Path.Combine(modsDir, fileName)
                    Await File.WriteAllBytesAsync(zipPath, zipBytes, cancellation)

                    ' Update mod-list.json to enable the mod.
                    Await AddToModList(modsDir, modId, cancellation)

                    Return New ModInstallResult With {
                        .Success = True,
                        .InstalledVersion = resolvedVersion
                    }
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                Return New ModInstallResult With {
                    .Success = False,
                    .ErrorMessage = ex.Message
                }
            End Try
        End Function

        Public Async Function RemoveMod(instanceConfig As InstanceConfig,
                                        modId As String) As Task(Of Boolean) _
                Implements IModManager.RemoveMod
            Try
                Dim cfg = FactorioConfig.ParseInstanceConfig(instanceConfig.RawJson)
                Dim modsDir = GetModsDirectory(cfg, instanceConfig)

                ' Remove all zip files for this mod.
                For Each zip In Directory.GetFiles(modsDir, $"{modId}_*.zip")
                    File.Delete(zip)
                Next

                ' Remove from mod-list.json.
                Await RemoveFromModList(modsDir, modId, CancellationToken.None)
                Return True
            Catch
                Return False
            End Try
        End Function

        Public Async Function CheckForUpdates(
                instanceConfig As InstanceConfig) As Task(Of IReadOnlyList(Of ModUpdateInfo)) _
                Implements IModManager.CheckForUpdates

            Dim installed = Await ListInstalledMods(instanceConfig)
            Dim updates As New List(Of ModUpdateInfo)

            Using client As New HttpClient()
                client.Timeout = TimeSpan.FromSeconds(10)
                For Each modItem In installed
                    Try
                        Dim url = $"https://mods.factorio.com/api/mods/{modItem.ModId}"
                        Dim json = Await client.GetStringAsync(url)
                        Using doc = JsonDocument.Parse(json)
                            Dim releases = doc.RootElement.GetProperty("releases").EnumerateArray().ToList()
                            Dim latest As String = Nothing
                            If releases.Count > 0 Then
                                latest = releases(releases.Count - 1).GetProperty("version").GetString()
                            End If

                            If Not String.IsNullOrWhiteSpace(latest) AndAlso latest <> modItem.Version Then
                                updates.Add(New ModUpdateInfo With {
                                    .ModId = modItem.ModId,
                                    .CurrentVersion = modItem.Version,
                                    .AvailableVersion = latest
                                })
                            End If
                        End Using
                    Catch
                        ' Skip this mod if the API call fails
                    End Try
                Next
            End Using

            Return updates
        End Function

        Private Function GetModsDirectory(cfg As FactorioInstanceConfig,
                                          instance As InstanceConfig) As String
            If Not String.IsNullOrWhiteSpace(cfg.UseModsDir) Then
                Return cfg.UseModsDir
            End If
            ' Default: {instanceWorkingDir}/mods
            Return IO.Path.Combine(instance.InstanceId, "mods")
        End Function

        Private Function ExtractVersionFromZipName(zipPath As String,
                                                    modId As String) As String
            ' Zip names follow the pattern: ModName_1.2.3.zip
            Dim fileName = Path.GetFileNameWithoutExtension(zipPath)
            If fileName.StartsWith(modId & "_") Then
                Return fileName.Substring(modId.Length + 1)
            End If
            Return "unknown"
        End Function

        Private Async Function AddToModList(modsDir As String,
                                             modId As String,
                                             cancellation As CancellationToken) As Task
            Dim modListPath = IO.Path.Combine(modsDir, "mod-list.json")
            ' Create a minimal mod-list.json if it doesn't exist.
            If Not File.Exists(modListPath) Then
                Await File.WriteAllTextAsync(modListPath,
                    "{""mods"":[{""name"":""base"",""enabled"":true}]}", cancellation)
            End If
            ' A full mod-list.json parser is out of scope here -
            ' the node implementation will handle JSON manipulation properly.
        End Function

        Private Async Function RemoveFromModList(modsDir As String,
                                                  modId As String,
                                                  cancellation As CancellationToken) As Task
            ' Full implementation in node Core using System.Text.Json.
            Await Task.CompletedTask
        End Function

    End Class

End Namespace
