Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text

' ============================================================
'  ConsoleUi — interactive console UI shared by Linux and the
'              Windows --cli mode
'
'  Two distinct entry experiences:
'    - First run (auth token is the placeholder) -> wizard
'    - Subsequent runs -> top-level menu
'
'  Color: minimal, optional, respects the NO_COLOR env var
'  (https://no-color.org/) and falls back gracefully when the
'  console doesn't support color setting (e.g. piped output).
' ============================================================

Public Module ConsoleUi

    Private _useColor As Boolean = True
    Private _configPath As String

    Public Sub Run(configPath As String)

        _configPath = configPath
        DetectColorSupport()

        ' On Linux, the sibling GSM.Node binary may have arrived without
        ' +x (typical of files SCP'd from a Windows publish). Fix it up
        ' once at startup; this is a no-op on Windows and on already-
        ' executable binaries.
        ServiceManager.EnsureNodeExecutable()

        PrintBanner()

        Dim cfg = NodeSetupConfig.LoadOrCreate(configPath)

        ' First-run heuristic: launch the wizard automatically when the
        ' file does not exist OR the auth token is still the placeholder.
        ' The user can still get to the menu afterwards.
        If Not File.Exists(configPath) OrElse cfg.NeedsAuthTokenSetup Then
            Console.WriteLine()
            WriteColored("This node has not been configured yet.", ConsoleColor.Yellow)
            Console.WriteLine()
            Console.WriteLine("Starting the setup wizard. Press Ctrl+C at any time to exit.")
            Console.WriteLine()
            cfg = RunWizard(cfg)
        End If

        ' Main menu loop.
        Do
            Dim quit = ShowMainMenu(cfg)
            If quit Then Exit Do
            ' Reload config in case the user just edited or reinstalled
            ' something — keeps the displayed status accurate.
            cfg = NodeSetupConfig.LoadOrCreate(configPath)
        Loop

    End Sub

    ' --------------------------------------------------------
    ' Top-level menu
    ' --------------------------------------------------------

    ''' <summary>
    ''' Returns True when the user chose to quit.
    ''' </summary>
    Private Function ShowMainMenu(cfg As NodeSetupConfig) As Boolean
        Console.WriteLine()
        PrintHeader("Main Menu")
        Console.WriteLine()
        Console.WriteLine("  Configuration file: " & _configPath)

        Dim status = If(cfg.NeedsAuthTokenSetup,
                        "NOT CONFIGURED (auth token is the default)",
                        "Configured")
        Dim statusColor = If(cfg.NeedsAuthTokenSetup, ConsoleColor.Yellow, ConsoleColor.Green)
        Console.Write("  Status:             ")
        WriteLineColored(status, statusColor)

        Console.WriteLine()
        Console.WriteLine("  1) Run setup wizard")
        Console.WriteLine("  2) View current configuration")
        Console.WriteLine("  3) Edit configuration")
        Console.WriteLine("  4) Generate a new authentication token")
        Console.WriteLine("  5) Set up service user (Linux: create user, chown directories)")
        Console.WriteLine("  6) Install as system service")
        Console.WriteLine("  7) Uninstall system service")
        Console.WriteLine("  8) Show service status")
        Console.WriteLine("  Q) Quit")
        Console.WriteLine()

        Dim choice = Prompt("Choice")
        If choice Is Nothing Then Return True

        Select Case choice.Trim().ToLowerInvariant()
            Case "1"
                cfg = RunWizard(cfg)
            Case "2"
                ShowCurrentConfig(cfg)
            Case "3"
                EditConfigMenu(cfg)
            Case "4"
                RegenerateToken(cfg)
            Case "5"
                SetupServiceUserFlow()
            Case "6"
                InstallServiceFlow()
            Case "7"
                UninstallServiceFlow()
            Case "8"
                ShowServiceStatus()
            Case "q", "quit", "exit"
                Return True
            Case Else
                WriteLineColored("Unknown choice. Try 1-8 or Q.", ConsoleColor.Yellow)
        End Select

        Return False
    End Function

    ' --------------------------------------------------------
    ' Wizard
    ' --------------------------------------------------------

    Private Function RunWizard(cfg As NodeSetupConfig) As NodeSetupConfig

        PrintHeader("Setup Wizard")
        Console.WriteLine()
        Console.WriteLine("Press Enter to accept the default value shown in [brackets].")
        Console.WriteLine()

        ' --- Step 1: identity ---
        WriteLineColored("Step 1 of 5: Node identity", ConsoleColor.Cyan)
        Console.WriteLine("  This name identifies the node when the Manager connects.")
        Dim defaultId = If(String.IsNullOrEmpty(cfg.Node.NodeId), Environment.MachineName, cfg.Node.NodeId)
        cfg.Node.NodeId = PromptWithDefault("Node ID", defaultId,
                                            AddressOf ConfigHelpers.ValidateNodeId)
        cfg.Node.ListenPort = PromptInt("Listen port", cfg.Node.ListenPort,
                                        AddressOf ConfigHelpers.ValidatePort)

        ' --- Step 2: storage ---
        Console.WriteLine()
        WriteLineColored("Step 2 of 5: Storage", ConsoleColor.Cyan)
        Console.WriteLine("  Where the node stores its data, and where new game-server")
        Console.WriteLine("  installations live by default.")
        cfg.Node.DataDirectory = PromptWithDefault("Data directory",
                                                   cfg.Node.DataDirectory,
                                                   AddressOf ConfigHelpers.ValidateDataDirectory)
        cfg.Node.ServersDirectory = PromptWithDefault("Servers directory",
                                                      cfg.Node.ServersDirectory,
                                                      AddressOf ConfigHelpers.ValidateServersDirectory)

        ' --- Step 3: operations ---
        Console.WriteLine()
        WriteLineColored("Step 3 of 5: Operations", ConsoleColor.Cyan)
        cfg.Node.MaxConcurrentInstalls = PromptInt("Max concurrent installs",
                                                   cfg.Node.MaxConcurrentInstalls,
                                                   AddressOf ConfigHelpers.ValidateConcurrentInstalls)
        cfg.Node.LogRetentionDays = PromptInt("Log retention (days)",
                                              cfg.Node.LogRetentionDays,
                                              AddressOf ConfigHelpers.ValidateLogRetentionDays)

        ' --- Step 4: token ---
        Console.WriteLine()
        WriteLineColored("Step 4 of 5: Authentication", ConsoleColor.Cyan)
        Console.WriteLine("  The Manager needs an auth token to connect to this node.")
        Dim regen = True
        If Not cfg.NeedsAuthTokenSetup Then
            regen = PromptYesNo("A token is already set. Generate a new one?", defaultYes:=False)
        End If
        If regen Then
            cfg.Node.AuthToken = ConfigHelpers.GenerateAuthToken()
            Console.WriteLine("  New token generated.")
        Else
            Console.WriteLine("  Keeping the existing token.")
        End If

        ' --- Step 5: review and save ---
        Console.WriteLine()
        WriteLineColored("Step 5 of 5: Review", ConsoleColor.Cyan)
        Console.WriteLine()
        PrintConfigSummary(cfg, includeToken:=False)

        Console.WriteLine()
        Dim save = PromptYesNo("Save this configuration?", defaultYes:=True)
        If Not save Then
            WriteLineColored("Aborted. No changes written.", ConsoleColor.Yellow)
            Return cfg
        End If

        Try
            cfg.Save(_configPath, backupExisting:=True)
            WriteLineColored("Configuration saved to: " & _configPath, ConsoleColor.Green)
        Catch ex As Exception
            WriteLineColored("Failed to save: " & ex.Message, ConsoleColor.Red)
            Return cfg
        End Try

        ' Show the token prominently after save so the user can copy it
        ' into the Manager. This is the only place we display it in full
        ' during the wizard flow.
        Console.WriteLine()
        WriteLineColored("============================================================", ConsoleColor.Yellow)
        WriteLineColored("   Copy this auth token into the Manager:", ConsoleColor.Yellow)
        WriteLineColored("============================================================", ConsoleColor.Yellow)
        Console.WriteLine()
        Console.WriteLine("   " & cfg.Node.AuthToken)
        Console.WriteLine()
        WriteLineColored("============================================================", ConsoleColor.Yellow)

        Console.WriteLine()
        Dim doInstall = PromptYesNo("Install GSM.Node as a system service now?", defaultYes:=False)
        If doInstall Then
            InstallServiceFlow()
        End If

        PressAnyKey()
        Return cfg
    End Function

    ' --------------------------------------------------------
    ' View / edit config
    ' --------------------------------------------------------

    Private Sub ShowCurrentConfig(cfg As NodeSetupConfig)
        Console.WriteLine()
        PrintHeader("Current Configuration")
        Console.WriteLine()
        PrintConfigSummary(cfg, includeToken:=True)
        Console.WriteLine()
        PressAnyKey()
    End Sub

    Private Sub PrintConfigSummary(cfg As NodeSetupConfig, includeToken As Boolean)
        Console.WriteLine($"  Node ID:                 {cfg.Node.NodeId}")
        Console.WriteLine($"  Listen port:             {cfg.Node.ListenPort}")
        Console.WriteLine($"  Data directory:          {cfg.Node.DataDirectory}")
        Console.WriteLine($"  Servers directory:       {cfg.Node.ServersDirectory}")
        Console.WriteLine($"  Max concurrent installs: {cfg.Node.MaxConcurrentInstalls}")
        Console.WriteLine($"  Log retention (days):    {cfg.Node.LogRetentionDays}")
        Console.WriteLine($"  Metrics interval (s):    {cfg.Node.MetricsIntervalSeconds}")
        If includeToken Then
            Dim tok = If(cfg.NeedsAuthTokenSetup,
                         "*** NOT SET (placeholder) ***",
                         cfg.Node.AuthToken)
            Console.WriteLine($"  Auth token:              {tok}")
        Else
            Console.WriteLine($"  Auth token:              {If(cfg.NeedsAuthTokenSetup, "(will be generated)", "(unchanged)")}")
        End If
        Console.WriteLine()
        Console.WriteLine("  Security:")
        Console.WriteLine($"    Max failed attempts:        {cfg.Security.MaxFailedAttempts}")
        Console.WriteLine($"    Failure window (min):       {cfg.Security.FailureWindowMinutes}")
        Console.WriteLine($"    Lockout (min):              {cfg.Security.LockoutMinutes}")
        Console.WriteLine($"    Auth failure delay (ms):    {cfg.Security.AuthFailureDelayMs}")
        Console.WriteLine($"    Requests/min/IP:            {cfg.Security.RequestsPerMinutePerIp}")
        Console.WriteLine($"    Max request body (bytes):   {cfg.Security.MaxRequestBodyBytes}")
        Console.WriteLine($"    Max concurrent connections: {cfg.Security.MaxConcurrentConnections}")
    End Sub

    Private Sub EditConfigMenu(cfg As NodeSetupConfig)
        Do
            Console.WriteLine()
            PrintHeader("Edit Configuration")
            Console.WriteLine()
            Console.WriteLine($"  1) Node ID                  [{cfg.Node.NodeId}]")
            Console.WriteLine($"  2) Listen port              [{cfg.Node.ListenPort}]")
            Console.WriteLine($"  3) Data directory           [{cfg.Node.DataDirectory}]")
            Console.WriteLine($"  4) Servers directory        [{cfg.Node.ServersDirectory}]")
            Console.WriteLine($"  5) Max concurrent installs  [{cfg.Node.MaxConcurrentInstalls}]")
            Console.WriteLine($"  6) Log retention (days)     [{cfg.Node.LogRetentionDays}]")
            Console.WriteLine($"  7) Metrics interval (s)     [{cfg.Node.MetricsIntervalSeconds}]")
            Console.WriteLine($"  8) Security settings (advanced)")
            Console.WriteLine($"  S) Save and return")
            Console.WriteLine($"  B) Discard changes and return")
            Console.WriteLine()

            Dim choice = Prompt("Choice")
            If choice Is Nothing Then Return

            Select Case choice.Trim().ToLowerInvariant()
                Case "1"
                    cfg.Node.NodeId = PromptWithDefault("Node ID", cfg.Node.NodeId, AddressOf ConfigHelpers.ValidateNodeId)
                Case "2"
                    cfg.Node.ListenPort = PromptInt("Listen port", cfg.Node.ListenPort, AddressOf ConfigHelpers.ValidatePort)
                Case "3"
                    cfg.Node.DataDirectory = PromptWithDefault("Data directory", cfg.Node.DataDirectory, AddressOf ConfigHelpers.ValidateDataDirectory)
                Case "4"
                    cfg.Node.ServersDirectory = PromptWithDefault("Servers directory", cfg.Node.ServersDirectory, AddressOf ConfigHelpers.ValidateServersDirectory)
                Case "5"
                    cfg.Node.MaxConcurrentInstalls = PromptInt("Max concurrent installs", cfg.Node.MaxConcurrentInstalls, AddressOf ConfigHelpers.ValidateConcurrentInstalls)
                Case "6"
                    cfg.Node.LogRetentionDays = PromptInt("Log retention (days)", cfg.Node.LogRetentionDays, AddressOf ConfigHelpers.ValidateLogRetentionDays)
                Case "7"
                    cfg.Node.MetricsIntervalSeconds = PromptInt("Metrics interval (seconds)", cfg.Node.MetricsIntervalSeconds, AddressOf ConfigHelpers.ValidateMetricsInterval)
                Case "8"
                    EditSecurityMenu(cfg)
                Case "s", "save"
                    Try
                        cfg.Save(_configPath, backupExisting:=True)
                        WriteLineColored("Configuration saved to: " & _configPath, ConsoleColor.Green)
                    Catch ex As Exception
                        WriteLineColored("Failed to save: " & ex.Message, ConsoleColor.Red)
                    End Try
                    Return
                Case "b", "back", "cancel"
                    Return
                Case Else
                    WriteLineColored("Unknown choice.", ConsoleColor.Yellow)
            End Select
        Loop
    End Sub

    Private Sub EditSecurityMenu(cfg As NodeSetupConfig)
        Do
            Console.WriteLine()
            PrintHeader("Security Settings (advanced)")
            Console.WriteLine()
            WriteLineColored("  Defaults are good for most setups. Change with care.", ConsoleColor.Yellow)
            Console.WriteLine()
            Console.WriteLine($"  1) Max failed auth attempts        [{cfg.Security.MaxFailedAttempts}]")
            Console.WriteLine($"  2) Failure window (minutes)        [{cfg.Security.FailureWindowMinutes}]")
            Console.WriteLine($"  3) Lockout duration (minutes)      [{cfg.Security.LockoutMinutes}]")
            Console.WriteLine($"  4) Auth failure delay (ms)         [{cfg.Security.AuthFailureDelayMs}]")
            Console.WriteLine($"  5) Requests per minute per IP      [{cfg.Security.RequestsPerMinutePerIp}]")
            Console.WriteLine($"  6) Max request body (bytes)        [{cfg.Security.MaxRequestBodyBytes}]")
            Console.WriteLine($"  7) Max concurrent connections      [{cfg.Security.MaxConcurrentConnections}]")
            Console.WriteLine($"  R) Reset to defaults")
            Console.WriteLine($"  B) Back")
            Console.WriteLine()

            Dim choice = Prompt("Choice")
            If choice Is Nothing Then Return

            Select Case choice.Trim().ToLowerInvariant()
                Case "1" : cfg.Security.MaxFailedAttempts = PromptInt("Max failed auth attempts", cfg.Security.MaxFailedAttempts, Nothing)
                Case "2" : cfg.Security.FailureWindowMinutes = PromptInt("Failure window (minutes)", cfg.Security.FailureWindowMinutes, Nothing)
                Case "3" : cfg.Security.LockoutMinutes = PromptInt("Lockout duration (minutes)", cfg.Security.LockoutMinutes, Nothing)
                Case "4" : cfg.Security.AuthFailureDelayMs = PromptInt("Auth failure delay (ms)", cfg.Security.AuthFailureDelayMs, Nothing)
                Case "5" : cfg.Security.RequestsPerMinutePerIp = PromptInt("Requests per minute per IP (0 = unlimited)", cfg.Security.RequestsPerMinutePerIp, Nothing)
                Case "6" : cfg.Security.MaxRequestBodyBytes = PromptLong("Max request body (bytes)", cfg.Security.MaxRequestBodyBytes)
                Case "7" : cfg.Security.MaxConcurrentConnections = PromptInt("Max concurrent connections", cfg.Security.MaxConcurrentConnections, Nothing)
                Case "r", "reset"
                    cfg.Security = New SecuritySection()
                    WriteLineColored("Security settings reset to defaults.", ConsoleColor.Green)
                Case "b", "back"
                    Return
                Case Else
                    WriteLineColored("Unknown choice.", ConsoleColor.Yellow)
            End Select
        Loop
    End Sub

    ' --------------------------------------------------------
    ' Token regeneration
    ' --------------------------------------------------------

    Private Sub RegenerateToken(cfg As NodeSetupConfig)
        Console.WriteLine()
        PrintHeader("Generate New Auth Token")
        Console.WriteLine()
        WriteLineColored("Warning: regenerating the token will disconnect any existing Manager", ConsoleColor.Yellow)
        WriteLineColored("until the new token is entered there.", ConsoleColor.Yellow)
        Console.WriteLine()
        Dim go = PromptYesNo("Generate a new token now?", defaultYes:=False)
        If Not go Then Return

        cfg.Node.AuthToken = ConfigHelpers.GenerateAuthToken()
        Try
            cfg.Save(_configPath, backupExisting:=True)
        Catch ex As Exception
            WriteLineColored("Failed to save: " & ex.Message, ConsoleColor.Red)
            Return
        End Try

        Console.WriteLine()
        WriteLineColored("New token saved. Update the Manager to use this value:", ConsoleColor.Green)
        Console.WriteLine()
        Console.WriteLine("   " & cfg.Node.AuthToken)
        Console.WriteLine()
        PressAnyKey()
    End Sub

    ' --------------------------------------------------------
    ' Service install / uninstall / status
    ' --------------------------------------------------------

    Private Sub InstallServiceFlow()
        Console.WriteLine()
        PrintHeader("Install as Service")
        Console.WriteLine()

        If Not ServiceManager.NodeExecutableExists() Then
            WriteLineColored("GSM.Node executable not found at: " & ServiceManager.GetNodeExecutablePath(), ConsoleColor.Red)
            Console.WriteLine("The setup tool must be deployed alongside GSM.Node.")
            PressAnyKey()
            Return
        End If

        If ConfigHelpers.RunningOnWindows() Then
            InstallWindowsServiceInteractive()
        Else
            InstallSystemdInteractive()
        End If

        PressAnyKey()
    End Sub

    Private Sub InstallWindowsServiceInteractive()
        Console.WriteLine("Detected platform: Windows")
        Console.WriteLine()

        If Not ConfigHelpers.RunningElevated() Then
            WriteLineColored("Administrator rights are required to install a Windows service.", ConsoleColor.Red)
            Console.WriteLine("Re-run the setup tool from an elevated command prompt or right-click " &
                              "the executable and choose 'Run as administrator'.")
            Return
        End If

        Dim serviceName = PromptWithDefault("Service name", ServiceManager.DefaultServiceName, Nothing)
        Dim displayName = PromptWithDefault("Display name", ServiceManager.DefaultDisplayName, Nothing)

        Console.WriteLine()
        Dim go = PromptYesNo("Install service '" & serviceName & "' now?", defaultYes:=True)
        If Not go Then Return

        Console.WriteLine("Installing...")
        Dim result = ServiceManager.InstallWindowsService(serviceName, displayName, ServiceManager.DefaultDescription)
        PrintServiceResult(result)
    End Sub

    Private Sub InstallSystemdInteractive()
        Console.WriteLine("Detected platform: Linux (systemd)")
        Console.WriteLine()

        Dim runAsUser = PromptAndPrepareServiceUser()
        If runAsUser Is Nothing Then Return

        Dim unitPath As String
        Try
            unitPath = ServiceManager.WriteSystemdUnit(runAsUser)
        Catch ex As Exception
            WriteLineColored("Failed to write unit file: " & ex.Message, ConsoleColor.Red)
            Return
        End Try

        Console.WriteLine()
        WriteLineColored("Unit file written: " & unitPath, ConsoleColor.Green)
        Console.WriteLine()

        ' If we're already root we can do the entire install directly
        ' rather than making the operator copy and paste three commands.
        ' Otherwise, fall back to printing the instructions — we don't
        ' want to assume sudo is configured for non-interactive use.
        If ConfigHelpers.RunningElevated() Then
            Dim go = PromptYesNo(
                "Running as root. Install, enable, and (re)start the service now?",
                defaultYes:=True)
            If go Then
                Console.WriteLine("Installing...")
                Dim result = ServiceManager.InstallSystemdServiceAsRoot(unitPath)
                PrintServiceResult(result)
                If result.Success Then
                    Console.WriteLine()
                    Console.WriteLine("Status and logs:")
                    Console.WriteLine("  systemctl status gsmnode")
                    Console.WriteLine("  journalctl -u gsmnode -f")
                End If
                Return
            End If
            ' User declined the auto-install — print the instructions for
            ' manual completion below.
            Console.WriteLine()
        Else
            Console.WriteLine("Installing the unit requires root, so the install commands are")
            Console.WriteLine("printed below for you to run yourself. (Re-run this tool with sudo")
            Console.WriteLine("to have it perform the install automatically.)")
            Console.WriteLine()
        End If

        Console.WriteLine(ServiceManager.GetSystemdInstallInstructions(unitPath))
    End Sub

    ''' <summary>
    ''' Top-level menu option for setting up the service account
    ''' WITHOUT installing systemd. Useful when the operator wants to
    ''' run the node manually for testing (foreground over SSH, etc.)
    ''' but still wants the dedicated user and the right ownership on
    ''' the install / data / servers directories.
    '''
    ''' Reuses PromptAndPrepareServiceUser so the experience is
    ''' identical to the one inside Install-as-service. After success,
    ''' prints the exact `sudo -u` invocation so the operator can
    ''' launch the node directly without touching systemd.
    ''' </summary>
    Private Sub SetupServiceUserFlow()
        Console.WriteLine()
        PrintHeader("Set Up Service User")
        Console.WriteLine()

        If ConfigHelpers.RunningOnWindows() Then
            WriteLineColored("This option configures a Linux service account and isn't applicable on Windows.",
                             ConsoleColor.Yellow)
            Console.WriteLine("On Windows the service runs under the LocalSystem account by default;")
            Console.WriteLine("use the Install-as-service option to register it.")
            PressAnyKey()
            Return
        End If

        Dim user = PromptAndPrepareServiceUser()
        If user Is Nothing Then
            PressAnyKey()
            Return
        End If

        Console.WriteLine()
        WriteLineColored("Service account ready.", ConsoleColor.Green)
        Console.WriteLine()
        Console.WriteLine("To run the node manually as this user (foreground, easy to Ctrl+C):")
        Console.WriteLine()
        WriteLineColored("  sudo -u " & user & " " & ServiceManager.GetNodeExecutablePath(),
                         ConsoleColor.Cyan)
        Console.WriteLine()
        Console.WriteLine("To open a shell as the user (handy for poking around the install dir):")
        Console.WriteLine()
        WriteLineColored("  sudo -u " & user & " bash",
                         ConsoleColor.Cyan)
        Console.WriteLine()
        Console.WriteLine("To run in the background and capture stdout/stderr to a file:")
        Console.WriteLine()
        WriteLineColored("  sudo -u " & user & " nohup " & ServiceManager.GetNodeExecutablePath() & " > /tmp/gsmnode.log 2>&1 &",
                         ConsoleColor.Cyan)
        Console.WriteLine()
        Console.WriteLine("When you're ready for unattended operation, use option 6 (Install as")
        Console.WriteLine("system service) to register the systemd unit with the same User= setting.")
        PressAnyKey()
    End Sub

    ''' <summary>
    ''' Prompts for the service-account username (defaulting to
    ''' 'powergsm' under root, the current user otherwise), and — if
    ''' running as root — ensures the account exists (offering useradd)
    ''' and that the install / data / servers directories are owned by
    ''' it (offering mkdir -p + chown -R).
    '''
    ''' Returns the chosen username on success, or Nothing if the
    ''' operator declined a required step (e.g. "don't create the
    ''' user"). Both the standalone Set-Up-Service-User flow and
    ''' Install-as-service share this so the experience is identical
    ''' across entry points.
    '''
    ''' Non-root callers skip the create/chown steps with a brief
    ''' note — those operations require root to begin with, and the
    ''' operator presumably already has whatever ownership they need
    ''' since they're running unprivileged.
    ''' </summary>
    Private Function PromptAndPrepareServiceUser() As String
        ' Service-account recommendation.
        '
        ' The node doesn't need root for any of its operations
        ' (port 8765 is unprivileged, SteamCMD doesn't need it,
        ' and game servers run as the same user as the parent
        ' process). Several game servers — notably UE4-based ones
        ' like Last Oasis, ARK, Squad — actively REFUSE to run as
        ' root and will exit with a clear "Refusing to run with
        ' root privileges" error. So the right default is a
        ' dedicated 'powergsm' system user.
        '
        ' If the operator launched this tool unprivileged, they
        ' presumably ARE the user the service should run as, so we
        ' default to Environment.UserName in that case. Either way
        ' the operator can override the default at the prompt.
        Dim runningAsRoot = ConfigHelpers.RunningElevated()
        Dim defaultUser As String
        If runningAsRoot Then
            defaultUser = "powergsm"
            Console.WriteLine("Recommendation: do not run the node as root. Game servers like")
            Console.WriteLine("Last Oasis (and other UE4 titles) refuse to start under root, and")
            Console.WriteLine("the node itself doesn't need elevated privileges for any of its")
            Console.WriteLine("work. A dedicated 'powergsm' system user is the suggested default.")
            Console.WriteLine()
        Else
            defaultUser = Environment.UserName
            Console.WriteLine("Not running as root — user creation and chown will be skipped. The")
            Console.WriteLine("current user will be used as the service account. Re-run with sudo")
            Console.WriteLine("if you want this tool to provision a separate account for you.")
            Console.WriteLine()
        End If

        Dim runAsUser = PromptWithDefault("Run service as user", defaultUser, Nothing)

        ' Without root we can't create users or chown anything; just
        ' return the chosen name. The caller decides what to do with it.
        If Not runningAsRoot Then
            Return runAsUser
        End If

        ' Check the user exists; if not, offer to create.
        If Not ServiceManager.CheckLinuxUserExists(runAsUser) Then
            Console.WriteLine()
            WriteLineColored($"User '{runAsUser}' does not exist on this system.",
                             ConsoleColor.Yellow)
            Dim createIt = PromptYesNo($"Create user '{runAsUser}' now?", defaultYes:=True)
            If Not createIt Then
                WriteLineColored("Aborting — the user must exist before proceeding.",
                                 ConsoleColor.Yellow)
                Return Nothing
            End If
            Dim createResult = ServiceManager.CreateLinuxSystemUser(runAsUser)
            If Not createResult.Success Then
                WriteLineColored("Failed to create user: " & createResult.Message,
                                 ConsoleColor.Red)
                If Not String.IsNullOrWhiteSpace(createResult.Output) Then
                    Console.WriteLine(createResult.Output)
                End If
                Return Nothing
            End If
            WriteLineColored("  " & createResult.Message, ConsoleColor.Green)
        Else
            Console.WriteLine()
            Console.WriteLine($"User '{runAsUser}' already exists. Skipping useradd.")
        End If

        ' Chown the directories the node will read and write so it
        ' can do its job after dropping root. Three candidates:
        '   - install dir (where GSM.Node lives, typically /opt/PowerGSM)
        '   - DataDirectory (SteamCMD cache, gsm.db, etc.)
        '   - ServersDirectory (game-server installs)
        ' These often nest — a fresh install puts data and servers
        ' under the install dir — so we dedupe descendant paths
        ' before chowning to avoid redundant work.
        Dim cfg = NodeSetupConfig.LoadOrCreate(_configPath)
        Dim chownPaths As New List(Of String) From {
            AppContext.BaseDirectory.TrimEnd("/"c, "\"c)
        }
        If Not String.IsNullOrWhiteSpace(cfg.Node.DataDirectory) Then
            chownPaths.Add(cfg.Node.DataDirectory)
        End If
        If Not String.IsNullOrWhiteSpace(cfg.Node.ServersDirectory) Then
            chownPaths.Add(cfg.Node.ServersDirectory)
        End If
        Dim deduped = DedupeAncestors(chownPaths)

        Console.WriteLine()
        Console.WriteLine($"The following paths will be created (if missing) and chowned to {runAsUser}:{runAsUser}:")
        For Each p In deduped
            Console.WriteLine("  " & p)
        Next
        Console.WriteLine()
        Dim doChown = PromptYesNo("Apply ownership now?", defaultYes:=True)
        If doChown Then
            Dim allOk = True
            For Each p In deduped
                Dim r = ServiceManager.PrepareDirAndChown(p, runAsUser)
                If r.Success Then
                    WriteLineColored("  " & r.Message, ConsoleColor.Green)
                Else
                    WriteLineColored("  " & r.Message, ConsoleColor.Red)
                    allOk = False
                End If
            Next
            If Not allOk Then
                Console.WriteLine()
                WriteLineColored("Some chown operations failed; the service may not be able to read/write its directories.",
                                 ConsoleColor.Yellow)
                Dim cont = PromptYesNo("Continue anyway?", defaultYes:=False)
                If Not cont Then Return Nothing
            End If
        End If

        Return runAsUser
    End Function

    ''' <summary>
    ''' Removes paths from the list that are descendants of another path
    ''' in the same list — e.g. if the input contains /opt/PowerGSM and
    ''' /opt/PowerGSM/servers, only /opt/PowerGSM is kept because chown -R
    ''' on the parent already covers the child. Empty/null entries are
    ''' dropped. Comparison uses Path.GetFullPath to canonicalize paths
    ''' so /opt/PowerGSM/ and /opt/PowerGSM compare equal.
    ''' </summary>
    Private Function DedupeAncestors(paths As List(Of String)) As List(Of String)
        Dim normalized As New List(Of String)
        For Each raw In paths
            If String.IsNullOrWhiteSpace(raw) Then Continue For
            Dim full As String
            Try
                full = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar)
            Catch
                ' Path can't be canonicalized (e.g. invalid char); use as-is.
                full = raw.Trim().TrimEnd("/"c, "\"c)
            End Try
            If Not normalized.Contains(full) Then normalized.Add(full)
        Next

        Dim result As New List(Of String)
        For Each p In normalized
            Dim isDescendant = False
            For Each other In normalized
                If p Is other Then Continue For
                If p.StartsWith(other & "/") OrElse p.StartsWith(other & "\") Then
                    isDescendant = True
                    Exit For
                End If
            Next
            If Not isDescendant Then result.Add(p)
        Next
        Return result
    End Function

    Private Sub UninstallServiceFlow()
        Console.WriteLine()
        PrintHeader("Uninstall Service")
        Console.WriteLine()

        If ConfigHelpers.RunningOnWindows() Then
            If Not ConfigHelpers.RunningElevated() Then
                WriteLineColored("Administrator rights are required to remove a Windows service.", ConsoleColor.Red)
                PressAnyKey()
                Return
            End If

            Dim serviceName = PromptWithDefault("Service name", ServiceManager.DefaultServiceName, Nothing)
            Dim go = PromptYesNo("Stop and remove service '" & serviceName & "'?", defaultYes:=False)
            If Not go Then Return

            Dim result = ServiceManager.UninstallWindowsService(serviceName)
            PrintServiceResult(result)
        Else
            Console.WriteLine("On Linux, removal is a manual operation. Run as root:")
            Console.WriteLine()
            Console.WriteLine("  sudo systemctl disable --now gsmnode")
            Console.WriteLine("  sudo rm /etc/systemd/system/gsmnode.service")
            Console.WriteLine("  sudo systemctl daemon-reload")
        End If

        PressAnyKey()
    End Sub

    Private Sub ShowServiceStatus()
        Console.WriteLine()
        PrintHeader("Service Status")
        Console.WriteLine()

        If ConfigHelpers.RunningOnWindows() Then
            Dim status = ServiceManager.GetWindowsServiceStatus(ServiceManager.DefaultServiceName)
            Console.WriteLine("Service: " & ServiceManager.DefaultServiceName)
            Console.Write("Status:  ")
            WriteLineColored(status, StatusColor(status))
        Else
            Dim status = ServiceManager.GetSystemdStatus("gsmnode")
            Console.WriteLine("Unit:   gsmnode")
            Console.Write("Status: ")
            WriteLineColored(status, StatusColor(status))
            If status = "Unknown" OrElse status = "NotInstalled" Then
                Console.WriteLine()
                Console.WriteLine("Run `systemctl status gsmnode` for details.")
            End If
        End If

        PressAnyKey()
    End Sub

    Private Function StatusColor(status As String) As ConsoleColor
        Select Case status
            Case "Running" : Return ConsoleColor.Green
            Case "Stopped", "NotInstalled" : Return ConsoleColor.Yellow
            Case "Failed" : Return ConsoleColor.Red
            Case Else : Return ConsoleColor.Gray
        End Select
    End Function

    Private Sub PrintServiceResult(result As ServiceManager.ServiceResult)
        Console.WriteLine()
        If result.Success Then
            WriteLineColored(result.Message, ConsoleColor.Green)
        Else
            WriteLineColored(result.Message, ConsoleColor.Red)
        End If
        If Not String.IsNullOrWhiteSpace(result.Output) Then
            Console.WriteLine()
            Console.WriteLine("Command output:")
            Console.WriteLine(result.Output)
        End If
    End Sub

    ' --------------------------------------------------------
    ' Prompts and IO helpers
    ' --------------------------------------------------------

    ''' <summary>
    ''' Reads a line from stdin. Returns Nothing on EOF (Ctrl+D / closed
    ''' stdin) so the caller can treat it as a quit signal.
    ''' </summary>
    Private Function Prompt(promptText As String) As String
        Console.Write(promptText & ": ")
        Return Console.ReadLine()
    End Function

    Private Function PromptWithDefault(promptText As String,
                                       defaultValue As String,
                                       validator As Func(Of String, String)) As String
        While True
            Console.Write($"{promptText} [{defaultValue}]: ")
            Dim input = Console.ReadLine()
            If input Is Nothing Then Return defaultValue
            If String.IsNullOrEmpty(input) Then input = defaultValue

            If validator IsNot Nothing Then
                Dim err = validator(input)
                If err IsNot Nothing Then
                    If err.StartsWith("Warning", StringComparison.OrdinalIgnoreCase) OrElse
                       err.StartsWith("Note", StringComparison.OrdinalIgnoreCase) Then
                        WriteLineColored("  " & err, ConsoleColor.Yellow)
                        Return input  ' warnings are accepted
                    End If
                    WriteLineColored("  " & err, ConsoleColor.Red)
                    Continue While
                End If
            End If

            Return input
        End While
        Return defaultValue
    End Function

    Private Function PromptInt(promptText As String,
                               defaultValue As Integer,
                               validator As Func(Of Integer, String)) As Integer
        While True
            Console.Write($"{promptText} [{defaultValue}]: ")
            Dim input = Console.ReadLine()
            If input Is Nothing Then Return defaultValue
            If String.IsNullOrEmpty(input) Then Return defaultValue

            Dim parsed As Integer
            If Not Integer.TryParse(input, parsed) Then
                WriteLineColored("  Not a valid integer.", ConsoleColor.Red)
                Continue While
            End If

            If validator IsNot Nothing Then
                Dim err = validator(parsed)
                If err IsNot Nothing Then
                    If err.StartsWith("Warning", StringComparison.OrdinalIgnoreCase) Then
                        WriteLineColored("  " & err, ConsoleColor.Yellow)
                        Return parsed
                    End If
                    WriteLineColored("  " & err, ConsoleColor.Red)
                    Continue While
                End If
            End If

            Return parsed
        End While
        Return defaultValue
    End Function

    Private Function PromptLong(promptText As String, defaultValue As Long) As Long
        While True
            Console.Write($"{promptText} [{defaultValue}]: ")
            Dim input = Console.ReadLine()
            If input Is Nothing Then Return defaultValue
            If String.IsNullOrEmpty(input) Then Return defaultValue
            Dim parsed As Long
            If Long.TryParse(input, parsed) Then Return parsed
            WriteLineColored("  Not a valid number.", ConsoleColor.Red)
        End While
        Return defaultValue
    End Function

    Private Function PromptYesNo(promptText As String, defaultYes As Boolean) As Boolean
        Dim hint = If(defaultYes, "[Y/n]", "[y/N]")
        While True
            Console.Write($"{promptText} {hint}: ")
            Dim input = Console.ReadLine()
            If input Is Nothing Then Return defaultYes
            If String.IsNullOrEmpty(input.Trim()) Then Return defaultYes
            Select Case input.Trim().ToLowerInvariant()
                Case "y", "yes" : Return True
                Case "n", "no" : Return False
                Case Else
                    WriteLineColored("  Please answer y or n.", ConsoleColor.Yellow)
            End Select
        End While
        Return defaultYes
    End Function

    Private Sub PressAnyKey()
        Console.WriteLine()
        Console.Write("Press Enter to continue...")
        Try
            Console.ReadLine()
        Catch
            ' If stdin is closed (e.g. piped input ran out), don't hang.
        End Try
    End Sub

    ' --------------------------------------------------------
    ' Banner / formatting
    ' --------------------------------------------------------

    Private Sub PrintBanner()
        Console.WriteLine()
        WriteLineColored("============================================================", ConsoleColor.Cyan)
        WriteLineColored("   " & Program.ProductName & " " & Program.ProductVersion, ConsoleColor.Cyan)
        WriteLineColored("============================================================", ConsoleColor.Cyan)
    End Sub

    Private Sub PrintHeader(title As String)
        WriteLineColored("------------------------------------------------------------", ConsoleColor.DarkCyan)
        WriteLineColored("  " & title, ConsoleColor.Cyan)
        WriteLineColored("------------------------------------------------------------", ConsoleColor.DarkCyan)
    End Sub

    Private Sub DetectColorSupport()
        ' Honor the NO_COLOR convention: if the variable is set to any
        ' non-empty value, suppress all color codes.
        Dim noColor = Environment.GetEnvironmentVariable("NO_COLOR")
        If Not String.IsNullOrEmpty(noColor) Then
            _useColor = False
            Return
        End If
        ' Also turn off color when stdout is redirected — pipes shouldn't
        ' receive ANSI codes by default.
        Try
            If Console.IsOutputRedirected Then _useColor = False
        Catch
            _useColor = False
        End Try
    End Sub

    Private Sub WriteColored(text As String, color As ConsoleColor)
        If _useColor Then
            Dim prev = Console.ForegroundColor
            Try
                Console.ForegroundColor = color
                Console.Write(text)
            Finally
                Console.ForegroundColor = prev
            End Try
        Else
            Console.Write(text)
        End If
    End Sub

    Private Sub WriteLineColored(text As String, color As ConsoleColor)
        WriteColored(text, color)
        Console.WriteLine()
    End Sub

End Module
