Imports System
Imports System.Collections.Generic
Imports System.IO

' ============================================================
'  GSM.NodeSetup — Companion configuration tool for GSM.Node
'
'  Mode selection:
'    - Default on Windows .................. WinForms GUI
'    - Default on Linux .................... interactive console
'    - --cli / -c .......................... force console
'    - --gui ............................... force GUI (Windows only)
'    - --auto-init ......................... non-interactive: write a
'                                            fresh config with a generated
'                                            auth token and exit. Useful
'                                            for automated/Docker setups.
'    - --config <path> ..................... override the path to
'                                            nodesettings.json
'    - --help / -h ......................... show usage and exit
'
'  By default the tool reads/writes nodesettings.json in the same
'  directory as the executable, which is where it sits next to
'  GSM.Node.exe in a published deployment.
' ============================================================

Module Program

    Public Const ProductName As String = "PowerGSM Node Setup"
    Public Const ProductVersion As String = "1.0"

    Sub Main(args As String())

        ' Fast path (Phase 8-2 follow-up 5b): --version prints the same
        ' identity axes the node's /api/version reports and exits 0 before
        ' ANY other work — before arg parsing, config resolution, or GUI
        ' bootstrap. Constants come from the MSBuild-generated build marker
        ' (follow-up 5a) + GSM.Contracts, so the printed identity and the
        ' byte-scannable PGSMBUILD marker never drift.
        If args IsNot Nothing AndAlso Array.IndexOf(args, "--version") >= 0 Then
            Console.WriteLine($"{PowerGSM.BuildInfo.PgsmBuildMarker.Component} {PowerGSM.BuildInfo.PgsmBuildMarker.BuildVersion} ({PowerGSM.BuildInfo.PgsmBuildMarker.Rid})")
            Console.WriteLine($"ProtocolVersion: {GSM.Node.Api.NodeApiContract.ProtocolVersion}")
            Console.WriteLine($"ContractsVersion: {GSM.Node.Api.NodeApiContract.ContractsVersion}")
            Return
        End If

        Dim parsed = ParseArgs(args)

        If parsed.ShowHelp Then
            PrintUsage()
            Return
        End If

        ' --apply-update: the self-update survivor. Runs headless (never the
        ' GUI/console wizard): wait for the exiting node's PID to die, swap the
        ' staged GSM.Node.new over the live binary (keeping GSM.Node.old), then
        ' relaunch via the service or directly. See SelfUpdateApply.
        If parsed.ApplyUpdate Then
            Environment.ExitCode = SelfUpdateApply.Run(parsed.WaitPid, parsed.RelaunchWithConsole)
            Return
        End If

        ' Resolve the config path. Default: nodesettings.json next to this
        ' executable. AppContext.BaseDirectory is the published-output dir
        ' both in development and in dotnet publish output.
        Dim configPath = parsed.ConfigPath
        If String.IsNullOrEmpty(configPath) Then
            configPath = Path.Combine(AppContext.BaseDirectory, "nodesettings.json")
        End If

        ' --auto-init: silently write a fresh config (or refresh just the
        ' AuthToken if one already exists with the placeholder) and exit.
        ' Prints the new token to stdout so a deployment script can capture it.
        If parsed.AutoInit Then
            RunAutoInit(configPath, parsed.AutoInitTls, parsed.AutoInitHttpMode)
            Return
        End If

        Dim forceCli = parsed.ForceCli
        Dim forceGui = parsed.ForceGui

#If WINDOWS_GUI Then
        If Not forceCli AndAlso (forceGui OrElse OperatingSystem.IsWindows()) Then
            Try
                Windows.GuiBootstrap.Run(configPath)
                Return
            Catch ex As Exception
                Console.Error.WriteLine("GUI failed to start: " & ex.Message)
                Console.Error.WriteLine("Falling back to console mode.")
                Console.Error.WriteLine()
            End Try
        End If
#Else
        If forceGui Then
            Console.Error.WriteLine("--gui is not available in this build (Linux / non-Windows).")
            Environment.ExitCode = 2
            Return
        End If
#End If

        Try
            ConsoleUi.Run(configPath)
        Catch ex As Exception
            Console.Error.WriteLine("Setup failed: " & ex.Message)
            Console.Error.WriteLine(ex.StackTrace)
            Environment.ExitCode = 1
        End Try

    End Sub

    ''' <summary>
    ''' Non-interactive first-run. HTTPS defaults ON (slice 2): the cert is
    ''' minted here with the same service the node runs and its fingerprint
    ''' printed after the token, so a deployment script can hand both to the
    ''' Manager and the node's first contact is pinned HTTPS with plain HTTP
    ''' off. --no-tls keeps the pre-0.7.0 plaintext shape; --http-mode
    ''' overrides the plain-HTTP policy (Disabled / LoopbackOnly / Open).
    ''' </summary>
    Private Sub RunAutoInit(configPath As String, enableTls As Boolean, httpMode As String)
        Try
            Dim cfg = NodeSetupConfig.LoadOrCreate(configPath)

            ' Always (re)generate the token in auto-init mode so deployments
            ' don't accidentally inherit a development token.
            cfg.Node.AuthToken = ConfigHelpers.GenerateAuthToken()

            If String.IsNullOrEmpty(cfg.Node.NodeId) Then
                cfg.Node.NodeId = Environment.MachineName
            End If

            cfg.Node.EnableTls = enableTls
            If enableTls Then
                Dim mode = ConfigHelpers.NormalizeHttpMode(httpMode)
                cfg.Node.HttpMode = If(mode, "Disabled")
            Else
                cfg.Node.HttpMode = "Open"
            End If
            ConfigHelpers.NormalizeTransport(cfg.Node)

            cfg.Save(configPath, backupExisting:=True)

            Dim fingerprint As String = Nothing
            If cfg.Node.EnableTls Then
                Dim certErr As String = Nothing
                fingerprint = ConfigHelpers.EnsureTlsCertificate(cfg, certErr)
                If fingerprint Is Nothing Then
                    Console.Error.WriteLine("auto-init: TLS certificate could not be created: " & certErr)
                    Console.Error.WriteLine("           The node will mint one at startup; read it with: GSM.NodeSetup --cli (option 9).")
                End If
            End If

            Console.WriteLine("Configuration written to: " & configPath)
            For Each line In ConfigHelpers.BuildManagerHandoffLines(cfg, fingerprint)
                Console.WriteLine(line)
            Next
            Console.WriteLine()
            Console.WriteLine("Provide the values above to the Manager when adding this node.")
        Catch ex As Exception
            Console.Error.WriteLine("auto-init failed: " & ex.Message)
            Environment.ExitCode = 1
        End Try
    End Sub

    Private Function ParseArgs(args As String()) As ParsedArgs
        Dim result As New ParsedArgs()
        Dim i As Integer = 0
        While i < args.Length
            Dim a = args(i)
            Select Case a.ToLowerInvariant()
                Case "--help", "-h", "/?"
                    result.ShowHelp = True
                Case "--cli", "-c"
                    result.ForceCli = True
                Case "--gui"
                    result.ForceGui = True
                Case "--auto-init"
                    result.AutoInit = True
                Case "--tls"
                    result.AutoInitTls = True
                Case "--no-tls"
                    result.AutoInitTls = False
                Case "--http-mode"
                    If i + 1 < args.Length Then
                        result.AutoInitHttpMode = args(i + 1)
                        i += 1
                    End If
                Case "--apply-update"
                    result.ApplyUpdate = True
                Case "--console"
                    result.RelaunchWithConsole = True
                Case "--wait-pid"
                    If i + 1 < args.Length Then
                        Dim pidVal As Integer
                        If Integer.TryParse(args(i + 1), pidVal) Then
                            result.WaitPid = pidVal
                        End If
                        i += 1
                    End If
                Case "--config"
                    If i + 1 < args.Length Then
                        result.ConfigPath = args(i + 1)
                        i += 1
                    End If
                Case Else
                    ' Silently ignore unknown args; matches typical Unix tools
                    ' that pass through extra args from wrapper scripts.
            End Select
            i += 1
        End While
        Return result
    End Function

    Private Sub PrintUsage()
        Console.WriteLine($"{ProductName} {ProductVersion}")
        Console.WriteLine()
        Console.WriteLine("Usage: GSM.NodeSetup [options]")
        Console.WriteLine()
        Console.WriteLine("Options:")
        Console.WriteLine("  --cli, -c           Force interactive console mode")
        Console.WriteLine("  --gui               Force WinForms GUI mode (Windows only)")
        Console.WriteLine("  --auto-init         Non-interactive: write a fresh config with")
        Console.WriteLine("                      a generated auth token (and, by default, a")
        Console.WriteLine("                      TLS certificate), then exit. Token and cert")
        Console.WriteLine("                      fingerprint are printed to stdout for capture")
        Console.WriteLine("                      by a deployment script.")
        Console.WriteLine("  --tls | --no-tls    With --auto-init: HTTPS on (default) or off.")
        Console.WriteLine("  --http-mode <m>     With --auto-init --tls: plain-HTTP policy,")
        Console.WriteLine("                      Disabled (default) | LoopbackOnly | Open.")
        Console.WriteLine("  --config <path>     Path to nodesettings.json (default: next")
        Console.WriteLine("                      to the executable)")
        Console.WriteLine("  --apply-update      Internal: swap in a staged node update and")
        Console.WriteLine("                      relaunch. Spawned by the node during a")
        Console.WriteLine("                      self-update; pair with --wait-pid.")
        Console.WriteLine("  --wait-pid <pid>    Internal: PID of the exiting node to wait for")
        Console.WriteLine("                      before swapping the binary (with --apply-update).")
        Console.WriteLine("  --console           Internal: the exiting node had a console window;")
        Console.WriteLine("                      relaunch it with one (direct launch only).")
        Console.WriteLine("  --help, -h          Show this message")
        Console.WriteLine()
        Console.WriteLine("With no arguments, the tool launches the GUI on Windows and the")
        Console.WriteLine("interactive console wizard on Linux.")
    End Sub

    Private Class ParsedArgs
        Public Property ShowHelp As Boolean
        Public Property ForceCli As Boolean
        Public Property ForceGui As Boolean
        Public Property AutoInit As Boolean
        Public Property AutoInitTls As Boolean = True
        Public Property AutoInitHttpMode As String
        Public Property ApplyUpdate As Boolean
        Public Property RelaunchWithConsole As Boolean
        Public Property WaitPid As Integer
        Public Property ConfigPath As String
    End Class

End Module
