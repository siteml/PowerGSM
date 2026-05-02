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
        Console.WriteLine("  5) Install as system service")
        Console.WriteLine("  6) Uninstall system service")
        Console.WriteLine("  7) Show service status")
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
                InstallServiceFlow()
            Case "6"
                UninstallServiceFlow()
            Case "7"
                ShowServiceStatus()
            Case "q", "quit", "exit"
                Return True
            Case Else
                WriteLineColored("Unknown choice. Try 1-7 or Q.", ConsoleColor.Yellow)
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
        Console.WriteLine("This tool will write a systemd unit file to the current directory.")
        Console.WriteLine("Installing it requires root, so the actual install commands are")
        Console.WriteLine("printed at the end for you to run yourself.")
        Console.WriteLine()

        Dim defaultUser = Environment.UserName
        Dim runAsUser = PromptWithDefault("Run service as user", defaultUser, Nothing)

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
        Console.WriteLine(ServiceManager.GetSystemdInstallInstructions(unitPath))
    End Sub

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
