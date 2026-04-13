Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin

' ============================================================
'  Last Oasis Game Plugin
'
'  Key quirks this plugin accounts for:
'    - SteamCMD install only (AppID 920720 for the dedicated server)
'    - Executable name is dynamic: MistServer*.exe (glob required)
'    - Multiple instances share one installation, differentiated
'      by -identifier (must be unique per server on the realm)
'    - CustomerKey is realm-wide → lives on InstallationConfig
'    - ProviderKey is also realm-wide but per-provider →
'      lives on InstallationConfig. Revoking a ProviderKey locks
'      out any server using it without affecting other keys or
'      the realm itself.
'    - Player tracking via stdout only - no query protocol exists
'    - Source RCON protocol
'    - No mod support (mods are client-side only)
'    - Linux requires steam_appid.txt in Mist/Binaries/Linux/
'
'  Installation config keys (realm-wide, shared by all instances):
'    CustomerKey     - Required. "Game server registration key" on
'                      MyRealm Settings. One per realm, never changes.
'    ProviderKey     - Required. "Self hosted game servers registration
'                      key" on MyRealm Settings. Can be shared across
'                      all instances or you can generate separate ones
'                      per hosting entity. Revoke to lock out without
'                      touching the realm or other keys.
'    SteamBranch     - Optional beta branch name (blank = default).
'    SteamBranchPassword
'
'  Instance config keys (per instance, stored in PluginConfig JSON):
'    Identifier      - Required. Unique name for this server on the
'                      realm e.g. "neon_server1". Differentiates
'                      instances sharing one installation.
'                      Visible in the MyRealm dashboard.
'    Port            - Game port (UDP). Default 5555.
'                      Must be unique per instance on this node.
'    QueryPort       - Steam query port. Default 27015.
'                      Must be unique per instance on this node.
'    RconPort        - RCON port. Default 8081.
'    RconPassword    - RCON password. Blank = RCON disabled.
'    Slots           - Tile slot count. Default 5. Max 100 per docs.
'    OverrideConnectionAddress - External IP for player connections.
'                      Blank = server detects its own address.
'                      Per docs: omit the flag entirely when blank
'                      rather than passing an empty value.
'    LogVerbosity    - UE4 log level. Default "Log".
' ============================================================

Namespace GSM.Plugins.LastOasis

    Public Class LastOasisPlugin
        Implements IGamePlugin

        ' ---- Identity ----

        Public ReadOnly Property GameId As String = "lastoasis" _
            Implements IGamePlugin.GameId

        Public ReadOnly Property DisplayName As String = "Last Oasis" _
            Implements IGamePlugin.DisplayName


        ' ============================================================
        '  INSTALL
        ' ============================================================

        Public Function GetSupportedInstallMethods() As IReadOnlyList(Of InstallMethod) _
                Implements IGamePlugin.GetSupportedInstallMethods
            Return {InstallMethod.SteamCMD}
        End Function

        Public Function GetInstallSteps(path As String,
                                        method As InstallMethod,
                                        config As InstallationConfig) As IReadOnlyList(Of InstallStep) _
                Implements IGamePlugin.GetInstallSteps

            Dim steps As New List(Of InstallStep)

            ' Dedicated server AppID 920720 (free). Client AppID is 903950.
            steps.Add(New SteamCmdInstallStep With {
                .Description = "Download/update Last Oasis dedicated server via SteamCMD",
                .AppId = "920720",
                .InstallDir = path,
                .Branch = GetSteamBranch(config),
                .BranchPassword = config.SteamBranchPassword,
                .ValidateFiles = True
            })

            ' Linux only: steam_appid.txt must contain the CLIENT AppID (903950)
            ' in Mist/Binaries/Linux/ or the server cannot connect to Steam.
            ' The node skips bash steps on Windows nodes gracefully.
            Dim linuxBinDir = IO.Path.Combine(path, "Mist", "Binaries", "Linux")
            steps.Add(New RunCommandStep With {
                .Description = "Create steam_appid.txt for Linux (skipped on Windows nodes)",
                .Executable = "bash",
                .Arguments = $"-c ""mkdir -p '{linuxBinDir}' && " &
                             $"echo 903950 > '{IO.Path.Combine(linuxBinDir, "steam_appid.txt")}'""",
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
                    $"No MistServer*.exe found in {path}. " &
                    "The install may be incomplete or the executable has been renamed. " &
                    "Use ExeOverride on the instance if the current name is known.")
            End If

            If Not Directory.Exists(IO.Path.Combine(path, "Mist")) Then
                Return ValidationResult.Fail(
                    $"Content directory 'Mist\' not found under {path}. " &
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
                Return IO.Path.Combine(installPath, "MistServer*.exe [NOT FOUND]")
            End If
            Return resolved
        End Function

        Public Function BuildCommandLine(instance As InstanceConfig) As String _
                Implements IGamePlugin.BuildCommandLine

            ' Instance JSON carries both instance fields AND installation fields
            ' (CustomerKey, ProviderKey) which the manager promotes from the
            ' Installation record into the instance context before calling here.
            Dim cfg = ParseInstanceConfig(instance.RawJson)
            Dim args As New List(Of String)

            ' Validate required fields with clear, actionable messages.
            If String.IsNullOrWhiteSpace(cfg.CustomerKey) Then
                Throw New InvalidOperationException(
                    $"Instance '{instance.DisplayName}': CustomerKey was not resolved. " &
                    "Ensure a Realm Credential is assigned to the Installation or this instance " &
                    "in Manager → Settings → Realm Credentials.")
            End If
            If String.IsNullOrWhiteSpace(cfg.ProviderKey) Then
                Throw New InvalidOperationException(
                    $"Instance '{instance.DisplayName}': ProviderKey was not resolved. " &
                    "Ensure a Realm Credential is assigned to the Installation or this instance " &
                    "in Manager → Settings → Realm Credentials.")
            End If
            If String.IsNullOrWhiteSpace(cfg.Identifier) Then
                Throw New InvalidOperationException(
                    $"Instance '{instance.DisplayName}': Identifier is not set. " &
                    "Each instance needs a unique identifier e.g. 'neon_server1'. " &
                    "This differentiates instances sharing the same installation.")
            End If

            ' Required backend/engine flags.
            args.Add("-log")
            args.Add("-force_steamclient_link")
            args.Add("-messaging")
            args.Add("-NoLiveServer")
            args.Add($"-backendapiurloverride=""backend-production.last-oasis.com""")

            ' Realm authentication - both resolved from RealmCredential by manager.
            ' CustomerKey: same for every instance on this realm.
            ' ProviderKey: identifies the hosting provider. The effective key is
            '              already resolved (instance credential → installation
            '              credential) before this method is called.
            args.Add($"-CustomerKey={cfg.CustomerKey}")
            args.Add($"-ProviderKey={cfg.ProviderKey}")

            ' Instance identifier - unique per server on this realm.
            ' Primary differentiator between instances sharing one installation.
            args.Add($"-identifier={cfg.Identifier}")

            ' Ports - each unique per instance on this node.
            args.Add($"-port={cfg.Port}")
            args.Add($"-QueryPort={cfg.QueryPort}")

            ' RCON - only include flags when a password is configured.
            If Not String.IsNullOrWhiteSpace(cfg.RconPassword) Then
                args.Add($"-RconPort={cfg.RconPort}")
                args.Add($"-RconPassword={cfg.RconPassword}")
            End If

            ' Tile slots. Docs recommend max 100.
            args.Add($"-slots={cfg.Slots}")

            ' External connection address.
            ' Without this flag the server advertises its local/private IP,
            ' which is unreachable for players connecting from the internet.
            ' In virtually all real deployments this must be set to the node's
            ' public IP or domain name. We warn but do not hard-fail so that
            ' LAN-only setups can still work.
            If Not String.IsNullOrWhiteSpace(cfg.OverrideConnectionAddress) Then
                args.Add($"-OverrideConnectionAddress={cfg.OverrideConnectionAddress}")
            End If
            ' When blank: the node's LaunchInstance path detects the missing
            ' value on Last Oasis instances and emits a startup warning:
            '   "OverrideConnectionAddress is not set on instance '{name}'.
            '    The server will advertise its local IP. Players outside the
            '    local network will be unable to connect. Set this to the
            '    node's public IP or domain name in the instance config."
            ' This is handled in node Core, not here, because the plugin
            ' cannot write to the node log directly. The flag below signals
            ' the node to emit that warning.
            ' (Implemented via IGamePlugin.GetStartupWarnings - to be added)

            If Not String.IsNullOrWhiteSpace(cfg.LogVerbosity) Then
                args.Add($"-LogCmds=""global {cfg.LogVerbosity}""")
            End If

            ' Standard UE4 headless flags.
            args.Add("-nullrhi")
            args.Add("-nosound")
            args.Add("-nographicsadapter")

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
            Return {
                New StdoutLogSource With {.CaptureStderr = True},
                New FileLogSource(
                    sourceId:="logfile",
                    pathPattern:=IO.Path.Combine("Mist", "Saved", "Logs", "*.log")
                ) With {.FollowRotation = True}
            }
        End Function

        Public Function GetLogParser() As ILogParser _
                Implements IGamePlugin.GetLogParser
            Return New LastOasisLogParser()
        End Function


        ' ============================================================
        '  INSTALL MONITOR
        ' ============================================================

        Public Function GetInstallMonitor() As IInstallMonitor _
                Implements IGamePlugin.GetInstallMonitor
            Return New SteamCmdInstallMonitor()
        End Function


        ' ============================================================
        '  RCON
        ' ============================================================

        Public Function GetRconInfo(instance As InstanceConfig) As RconInfo _
                Implements IGamePlugin.GetRconInfo

            Dim cfg = ParseInstanceConfig(instance.RawJson)
            If String.IsNullOrWhiteSpace(cfg.RconPassword) Then Return Nothing

            Return New RconInfo With {
                .Protocol = RconProtocol.SourceRcon,
                .Port = cfg.RconPort,
                .Password = cfg.RconPassword,
                .AutoConnect = True,
                .StartupDelayMs = 15000,    ' LO takes time to init RCON listener
                .MaxConnectRetries = 10,
                .RetryIntervalMs = 3000,
                .ConnectTimeoutMs = 5000
            }
        End Function


        ' ============================================================
        '  STARTUP WARNINGS
        ' ============================================================

        Public Function GetStartupWarnings(installPath As String,
                                           instance As InstanceConfig) As IReadOnlyList(Of String) _
                Implements IGamePlugin.GetStartupWarnings

            Dim warnings As New List(Of String)
            Dim cfg = ParseInstanceConfig(instance.RawJson)

            If String.IsNullOrWhiteSpace(cfg.OverrideConnectionAddress) Then
                warnings.Add(
                    $"OverrideConnectionAddress is not set on instance '{instance.DisplayName}'. " &
                    "Without this, Last Oasis will advertise the server's local/private IP address. " &
                    "Players outside the local network will be unable to connect. " &
                    "Set this to the node's public IP or domain name in the instance config.")
            End If

            Return warnings
        End Function


        ' ============================================================
        '  MODS
        ' ============================================================

        Public Function GetModManager() As IModManager _
                Implements IGamePlugin.GetModManager
            Return Nothing  ' Mods are client-side only
        End Function


        ' ============================================================
        '  CRASH HANDLING
        ' ============================================================

        Public Function GetCleanExitCodes() As IReadOnlyList(Of Integer) _
                Implements IGamePlugin.GetCleanExitCodes
            Return {0}
        End Function

        Public Function GetCrashSignalPatterns() As IReadOnlyList(Of String) _
                Implements IGamePlugin.GetCrashSignalPatterns
            Return {
                "Fatal error!",
                "Unhandled Exception:",
                "Access violation",
                "LowLevelFatalError",
                "Windows GetLastError",
                "=== Critical error: ===",
                "begin: stack for UAT",
                "Assertion failed:"
            }
        End Function


        ' ============================================================
        '  VERSION DETECTION
        ' ============================================================

        Public Function GetCurrentVersion(installPath As String) As String _
                Implements IGamePlugin.GetCurrentVersion
            Try
                Dim manifestPath = FindAppManifest(installPath)
                If manifestPath IsNot Nothing Then
                    Dim buildId = ParseAcfField(manifestPath, "buildid")
                    If Not String.IsNullOrEmpty(buildId) Then Return buildId
                End If
                Dim exe = FindExecutable(installPath)
                If exe IsNot Nothing Then
                    Dim vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe)
                    If Not String.IsNullOrEmpty(vi.FileVersion) Then Return vi.FileVersion
                End If
            Catch
            End Try
            Return String.Empty
        End Function

        Public Async Function GetLatestVersion(config As InstallationConfig,
                                               cancellation As CancellationToken) As Task(Of String) _
                Implements IGamePlugin.GetLatestVersion
            Try
                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(10)
                    Dim url = "https://api.steampowered.com/ISteamApps/UpToDateCheck/v1/" &
                              "?appid=920720&version=0"
                    Dim response = Await client.GetStringAsync(url, cancellation)
                    Dim match = Regex.Match(response, """required_version""\s*:\s*(\d+)")
                    If match.Success Then Return match.Groups(1).Value
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
                    .Key = "RealmCredentialId",
                    .Label = "Realm credential (override)",
                    .Description = "Optional. Overrides the realm credential set on the Installation. " &
                                   "Use this when this instance belongs to a different realm than the " &
                                   "other instances sharing this installation, or to assign a different " &
                                   "ProviderKey to instances on this specific node for easier revocation. " &
                                   "Leave blank to inherit the Installation's credential.",
                    .FieldType = ConfigFieldType.CredentialPicker,
                    .IsRequired = False
                },
                New ConfigFieldDescriptor With {
                    .Key = "Identifier",
                    .Label = "Server identifier",
                    .Description = "Unique name for this server on the realm, e.g. 'neon_server1'. " &
                                   "Differentiates instances sharing one installation. " &
                                   "Visible in the MyRealm dashboard.",
                    .FieldType = ConfigFieldType.Text,
                    .IsRequired = True
                },
                New ConfigFieldDescriptor With {
                    .Key = "Port",
                    .Label = "Game port (UDP)",
                    .Description = "Main game port. Must be unique per instance on this node.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "5555",
                    .MinValue = 1024,
                    .MaxValue = 65535,
                    .IsRequired = True
                },
                New ConfigFieldDescriptor With {
                    .Key = "QueryPort",
                    .Label = "Steam query port (UDP)",
                    .Description = "Steam server browser query port. Must be unique per instance. " &
                                   "Required when running multiple servers on the same machine.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "27015",
                    .MinValue = 1024,
                    .MaxValue = 65535,
                    .IsRequired = True
                },
                New ConfigFieldDescriptor With {
                    .Key = "RconPort",
                    .Label = "RCON port (TCP)",
                    .Description = "RCON management port. Leave RconPassword blank to disable RCON.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "8081",
                    .MinValue = 1024,
                    .MaxValue = 65535
                },
                New ConfigFieldDescriptor With {
                    .Key = "RconPassword",
                    .Label = "RCON password",
                    .Description = "Leave blank to disable RCON for this instance.",
                    .FieldType = ConfigFieldType.Password,
                    .IsSensitive = True
                },
                New ConfigFieldDescriptor With {
                    .Key = "Slots",
                    .Label = "Tile slots",
                    .Description = "Number of map tile slots this server can host. " &
                                   "Official docs recommend a maximum of 100.",
                    .FieldType = ConfigFieldType.IntegerField,
                    .DefaultValue = "5",
                    .MinValue = 1,
                    .MaxValue = 100
                },
                New ConfigFieldDescriptor With {
                    .Key = "OverrideConnectionAddress",
                    .Label = "Connection address (public IP or domain)",
                    .Description = "The public IP address or domain name that players use to connect. " &
                                   "Without this, the server advertises its local/private IP and " &
                                   "players outside the local network cannot connect. " &
                                   "This must be set for any internet-facing server. " &
                                   "Leave blank only for LAN-only deployments.",
                    .FieldType = ConfigFieldType.Text,
                    .IsRequired = False
                },
                New ConfigFieldDescriptor With {
                    .Key = "LogVerbosity",
                    .Label = "Log verbosity",
                    .Description = "'Log' is recommended for production. " &
                                   "'Verbose' produces significantly more output.",
                    .FieldType = ConfigFieldType.Choice,
                    .DefaultValue = "Log",
                    .Choices = New List(Of String) From {"Error", "Warning", "Log", "Verbose", "VeryVerbose"}
                }
            }
        End Function

        Public Function GetInstallationConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) _
                Implements IGamePlugin.GetInstallationConfigSchema
            Return {
                New ConfigFieldDescriptor With {
                    .Key = "RealmCredentialId",
                    .Label = "Realm credential",
                    .Description = "The CustomerKey and ProviderKey for this realm. " &
                                   "Select an existing credential or create a new one. " &
                                   "All instances sharing this installation inherit this credential " &
                                   "unless overridden at the instance level. " &
                                   "Manage credentials at Manager → Settings → Realm Credentials.",
                    .FieldType = ConfigFieldType.CredentialPicker,
                    .IsRequired = True
                },
                New ConfigFieldDescriptor With {
                    .Key = "SteamCredentialId",
                    .Label = "Steam account",
                    .Description = "Steam account used to download and update this installation via SteamCMD. " &
                                   "Last Oasis dedicated server (AppID 920720) may support anonymous " &
                                   "download - select 'Anonymous' if so. Otherwise select an account " &
                                   "that owns the required licence. " &
                                   "Passwords are encrypted at rest and never stored on the node. " &
                                   "Manage accounts at Manager → Settings → Steam Accounts.",
                    .FieldType = ConfigFieldType.SteamCredentialPicker,
                    .IsRequired = False     ' Blank = anonymous - manager will attempt +login anonymous
                },
                New ConfigFieldDescriptor With {
                    .Key = "SteamBranch",
                    .Label = "Steam branch",
                    .Description = "Steam beta branch to install from. Blank = default release branch.",
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
            If Not Directory.Exists(installPath) Then Return Nothing
            Return Directory.GetFiles(installPath, "MistServer*.exe",
                                      SearchOption.TopDirectoryOnly).FirstOrDefault()
        End Function

        Private Function FindAppManifest(installPath As String) As String
            Try
                Dim dir = New DirectoryInfo(installPath)
                Do While dir IsNot Nothing
                    Dim candidate = IO.Path.Combine(dir.FullName, "steamapps",
                                                 "appmanifest_920720.acf")
                    If File.Exists(candidate) Then Return candidate
                    dir = dir.Parent
                Loop
            Catch
            End Try
            Return Nothing
        End Function

        Private Function ParseAcfField(manifestPath As String, key As String) As String
            Try
                For Each line In File.ReadLines(manifestPath)
                    Dim m = Regex.Match(line, $"""{key}""\s+""([^""]+)""",
                                        RegexOptions.IgnoreCase)
                    If m.Success Then Return m.Groups(1).Value
                Next
            Catch
            End Try
            Return String.Empty
        End Function

        Private Function ParseInstanceConfig(rawJson As String) As LastOasisInstanceConfig
            If String.IsNullOrWhiteSpace(rawJson) Then Return New LastOasisInstanceConfig()
            Try
                Return System.Text.Json.JsonSerializer.Deserialize(Of LastOasisInstanceConfig)(rawJson)
            Catch
                Return New LastOasisInstanceConfig()
            End Try
        End Function

    End Class


    ' ============================================================
    '  TYPED CONFIG CLASSES
    '  Internal to this plugin. Deserialized from RawJson blobs.
    ' ============================================================

    ' Per-instance config. Deserialized from InstanceConfig.RawJson.
    '
    ' CustomerKey and ProviderKey are not stored here directly.
    ' The manager resolves the effective RealmCredential (instance
    ' override → installation default) before calling BuildCommandLine,
    ' and promotes the resolved keys into the merged JSON under these
    ' field names. The plugin always receives them pre-resolved.
    Friend Class LastOasisInstanceConfig
        ' Resolved by manager from RealmCredential record
        Public Property CustomerKey As String = String.Empty
        Public Property ProviderKey As String = String.Empty

        ' Per-instance fields
        Public Property Identifier As String = String.Empty
        Public Property Port As Integer = 5555
        Public Property QueryPort As Integer = 27015
        Public Property RconPort As Integer = 8081
        Public Property RconPassword As String = String.Empty
        Public Property Slots As Integer = 5
        Public Property OverrideConnectionAddress As String = String.Empty
        Public Property LogVerbosity As String = "Log"
    End Class


    ' ============================================================
    '  LOG PARSER
    ' ============================================================

    Public Class LastOasisLogParser
        Implements ILogParser

        Private ReadOnly _lock As New Object()
        Private ReadOnly _players As New Dictionary(Of String, PlayerInfo)(
            StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _metrics As New Dictionary(Of String, String)

        Private ReadOnly _joinPattern As New Regex(
            "LogNet.*Join(?:ed| request).*[?&]Name=([^?&\s]+)",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        Private ReadOnly _leavePatterns As Regex() = {
            New Regex("UNetConnection::Close.*PlayerName=([^\s,]+)",
                      RegexOptions.Compiled Or RegexOptions.IgnoreCase),
            New Regex("LogOnline.*Player\s+([^\s]+)\s+(?:disconnected|left)",
                      RegexOptions.Compiled Or RegexOptions.IgnoreCase)
        }

        Private ReadOnly _serverReadyPattern As New Regex(
            "LogWorld.*Bringing up level|ServerTravel.*completed",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        Private ReadOnly _tilePattern As New Regex(
            "Tile\s+([^\s]+)\s+(loaded|unloaded)",
            RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        Public Sub ProcessLine(sourceId As String,
                               timestamp As DateTime,
                               line As String) _
                Implements ILogParser.ProcessLine

            If sourceId <> "stdout" Then Return

            Dim joinMatch = _joinPattern.Match(line)
            If joinMatch.Success Then
                Dim name = joinMatch.Groups(1).Value
                SyncLock _lock
                    If Not _players.ContainsKey(name) Then
                        _players(name) = New PlayerInfo With {
                            .Name = name,
                            .JoinedAt = timestamp,
                            .Platform = "Steam"
                        }
                    End If
                    UpdatePlayerCountMetric()
                End SyncLock
                Return
            End If

            For Each pattern In _leavePatterns
                Dim leaveMatch = pattern.Match(line)
                If leaveMatch.Success Then
                    Dim name = leaveMatch.Groups(1).Value
                    SyncLock _lock
                        _players.Remove(name)
                        UpdatePlayerCountMetric()
                    End SyncLock
                    Return
                End If
            Next

            If _serverReadyPattern.IsMatch(line) Then
                SyncLock _lock
                    _metrics("ServerStatus") = "Ready"
                End SyncLock
                Return
            End If

            Dim tileMatch = _tilePattern.Match(line)
            If tileMatch.Success Then
                SyncLock _lock
                    _metrics("LastTileEvent") =
                        $"{tileMatch.Groups(1).Value} {tileMatch.Groups(2).Value}"
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

        Private Sub UpdatePlayerCountMetric()
            _metrics("PlayerCount") = _players.Count.ToString()
        End Sub

    End Class


    ' ============================================================
    '  INSTALL MONITOR
    ' ============================================================

    Public Class SteamCmdInstallMonitor
        Implements IInstallMonitor

        Private ReadOnly _promptPatterns As (Pattern As String, Info As PromptInfo)() = {
            (
                "Steam Guard code",
                New PromptInfo With {
                    .PromptType = PromptType.SteamGuardEmail,
                    .DisplayMessage = "SteamCMD requires a Steam Guard code. " &
                                      "Check the email associated with your Steam account.",
                    .InputPlaceholder = "Steam Guard code",
                    .IsSensitive = False
                }
            ),
            (
                "Two-factor code",
                New PromptInfo With {
                    .PromptType = PromptType.SteamGuardMobile,
                    .DisplayMessage = "SteamCMD requires a Steam Guard mobile authenticator code.",
                    .InputPlaceholder = "Authenticator code",
                    .IsSensitive = False
                }
            ),
            (
                "Please enter the current code",
                New PromptInfo With {
                    .PromptType = PromptType.SteamGuardTwoFactor,
                    .DisplayMessage = "SteamCMD requires a two-factor authentication code.",
                    .InputPlaceholder = "2FA code",
                    .IsSensitive = False
                }
            ),
            (
                "password:",
                New PromptInfo With {
                    .PromptType = PromptType.FreeText,
                    .DisplayMessage = "SteamCMD is requesting a Steam account password.",
                    .InputPlaceholder = "Steam password",
                    .IsSensitive = True
                }
            ),
            (
                "FAILED (Incorrect login)",
                New PromptInfo With {
                    .PromptType = PromptType.FreeText,
                    .DisplayMessage = "SteamCMD login failed. " &
                                      "Check your Steam credentials in the node configuration.",
                    .InputPlaceholder = "",
                    .IsSensitive = False
                }
            )
        }

        Public Function DetectPrompt(line As String) As PromptInfo _
                Implements IInstallMonitor.DetectPrompt
            If String.IsNullOrWhiteSpace(line) Then Return Nothing
            For Each entry In _promptPatterns
                If line.IndexOf(entry.Pattern,
                                StringComparison.OrdinalIgnoreCase) >= 0 Then
                    Return entry.Info
                End If
            Next
            Return Nothing
        End Function

        Public Sub NotifyPromptResolved(promptType As PromptType) _
                Implements IInstallMonitor.NotifyPromptResolved
        End Sub

    End Class

End Namespace
