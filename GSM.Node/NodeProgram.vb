Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Reflection
Imports System.Threading.Tasks
Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Hosting
Imports Microsoft.AspNetCore.Http
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Data.Sqlite
Imports GSM.Node.Api
Imports GSM.Node.Security

' ============================================================
'  GSM.Node — Entry point and infrastructure
' ============================================================

Namespace GSM.Node

    ''' <summary>
    ''' Entry point. Configures the ASP.NET Core Minimal API host,
    ''' registers services, wires hardened auth middleware, maps endpoints.
    ''' </summary>
    Module NodeProgram

        ' Windows: suppress blocking error dialogs from child processes
        ' (e.g. "The program can't start because MSVCP140.dll is missing")
        ' Child processes inherit this error mode.
        <Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True)>
        Private Function SetErrorMode(uMode As UInteger) As UInteger
        End Function

        Private Const SEM_FAILCRITICALERRORS As UInteger = &H1
        Private Const SEM_NOGPFAULTERRORBOX As UInteger = &H2
        Private Const SEM_NOOPENFILEERRORBOX As UInteger = &H8000

        ' Console-control-event isolation between the node and its
        ' game-server children:
        '
        '   When the node is launched from cmd (or any console host),
        '   the OS attaches it to that console. Game children spawned
        '   later with CREATE_NEW_CONSOLE — even with bInheritHandles
        '   set so STARTF_USESTDHANDLES can pass NUL handles — have
        '   been observed sharing the parent's console group on some
        '   Windows configurations. When that happens, GSM.CtrlCSender's
        '   GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0) propagates to
        '   every process attached to the console, including the node
        '   itself. Symptom: hitting Stop on a Factorio instance
        '   shuts the entire node down, identical to typing Ctrl+C
        '   into the terminal.
        '
        '   FreeConsole() at startup would technically resolve this,
        '   but it has a bad side effect: when the node is launched
        '   by double-clicking the .exe, Windows allocates a fresh
        '   console for it. FreeConsole then leaves that console with
        '   no attached processes, so Windows tears it down — and the
        '   visible window disappears, costing the user any way to
        '   read troubleshooting output.
        '
        '   The portable alternative is a process-local console
        '   control handler that ignores CTRL_C_EVENT *only while the
        '   node is itself firing one at a child* (tracked by
        '   ConsoleCtrlSuppression, set around the GSM.CtrlCSender
        '   call). The node's console stays attached and visible; the
        '   CTRL_C our helper fires (intended for a child) may reach
        '   the node but the handler returns TRUE meaning "handled,
        '   don't terminate", so the child's own handler runs the
        '   graceful-shutdown path while the node survives. Crucially,
        '   a user-typed Ctrl+C arrives with suppression INACTIVE, so
        '   the handler returns FALSE and the event falls through to
        '   ASP.NET Core's ConsoleLifetime — which calls
        '   StopApplication() for a graceful node shutdown (and that
        '   fires ApplicationStopping -> the shim-detach hook). This
        '   is why Ctrl+C closing the node works again now that games
        '   run under shims with their own consoles.
        '
        '   ORDERING TRAP: Win32 console-control handlers are LIFO
        '   (last registered, first called). ASP.NET Core's
        '   ConsoleLifetime subscribes to Console.CancelKeyPress
        '   during app.Run() host startup, which causes .NET to
        '   register its own Win32 handler at that point. If we only
        '   register ours BEFORE that happens, ASP.NET's handler ends
        '   up on top of the LIFO stack — it runs first, triggers
        '   ApplicationLifetime.StopApplication(), and the host shuts
        '   down. Ours never gets called.
        '
        '   Fix: register our handler a SECOND time after the host
        '   has started, via IHostApplicationLifetime.ApplicationStarted.
        '   By then ASP.NET has already inserted its handler; our late
        '   registration goes on top of that, so LIFO calls ours first
        '   for CTRL_C. When suppression is active we return TRUE to
        '   stop the chain before ASP.NET's handler can run; when it's
        '   inactive we return FALSE so ASP.NET's handler DOES run and
        '   gracefully stops the node. The early registration is kept
        '   too as a defence-in-depth for any CTRL_C that arrives
        '   during the brief startup window before ApplicationStarted
        '   fires.
        '
        '   We deliberately let CTRL_BREAK_EVENT, CTRL_CLOSE_EVENT,
        '   CTRL_LOGOFF_EVENT, and CTRL_SHUTDOWN_EVENT through to the
        '   default handler so the user can still terminate the node
        '   by closing the window (the natural gesture for a
        '   service-shaped process), pressing Ctrl+Break, or
        '   triggering a logoff/shutdown. Only the path our own
        '   helper exercises is suppressed.
        '
        '   For services (Windows Service / systemd), there's no
        '   console to begin with and SetConsoleCtrlHandler attaches
        '   to whatever's there (or a no-op if there's nothing),
        '   keeping cmd-launched and service-launched nodes on the
        '   same code path.
        Private Const CTRL_C_EVENT As UInteger = 0UI

        ' Module-level field roots the delegate so the GC can't move
        ' or collect it while the OS still holds the function pointer.
        Private ReadOnly _consoleCtrlHandler As ConsoleCtrlDelegate =
            Function(ctrlType As UInteger) As Boolean
                If ctrlType = CTRL_C_EVENT AndAlso ConsoleCtrlSuppression.Active Then
                    ' Our own GSM.CtrlCSender broadcast bouncing back: swallow it
                    ' so stopping a game doesn't terminate the node.
                    Return True   ' "handled, do not terminate"
                End If
                ' User-typed Ctrl+C (suppression inactive) falls through so
                ' ASP.NET Core's ConsoleLifetime runs StopApplication() and the
                ' node shuts down gracefully (firing ApplicationStopping -> the
                ' shim-detach hook). Break/Close/Logoff/Shutdown also fall
                ' through to default handling.
                Return False
            End Function

        Private Delegate Function ConsoleCtrlDelegate(ctrlType As UInteger) As Boolean

        <Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True)>
        Private Function SetConsoleCtrlHandler(handler As ConsoleCtrlDelegate,
                                                add As Boolean) As Boolean
        End Function

        ' For --shim-self-test: a WinExe has no console of its own, so attach
        ' to the launching cmd's console (if any) before writing results.
        <Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True)>
        Private Function AttachConsole(dwProcessId As UInteger) As Boolean
        End Function

        Private Const ATTACH_PARENT_PROCESS As UInteger = &HFFFFFFFFUI

        Sub Main(args As String())

            ' Diagnostic mode: drive a real ShimSession against the deployed
            ' shim and exit, without spinning up the web host. See ShimSelfTest.
            If args IsNot Nothing AndAlso Array.IndexOf(args, "--shim-self-test") >= 0 Then
                If OperatingSystem.IsWindows() Then
                    Try
                        AttachConsole(ATTACH_PARENT_PROCESS)
                    Catch
                    End Try
                End If
                Dim rc As Integer = ShimSelfTest.RunAsync().GetAwaiter().GetResult()
                Environment.Exit(rc)
            End If

            ' Diagnostic mode: prove adopt-on-restart (start under a shim,
            ' detach leaving it alive, adopt from a fresh ShimSession, assert
            ' same game pid + replayed output). See ShimReconnectTest.
            If args IsNot Nothing AndAlso Array.IndexOf(args, "--shim-reconnect-test") >= 0 Then
                If OperatingSystem.IsWindows() Then
                    Try
                        AttachConsole(ATTACH_PARENT_PROCESS)
                    Catch
                    End Try
                End If
                Dim rc As Integer = ShimReconnectTest.RunAsync().GetAwaiter().GetResult()
                Environment.Exit(rc)
            End If

            ' Diagnostic mode: stage the live binary as GSM.Node.new through the
            ' real chunked staging path, then (unless --stage-only) trigger the
            ' running node's apply-update over loopback. Exercises slice-6
            ' self-update end to end without the Manager or any manual HTTP.
            ' See SelfUpdateDryRun.
            If args IsNot Nothing AndAlso Array.IndexOf(args, "--self-update-dry-run") >= 0 Then
                If OperatingSystem.IsWindows() Then
                    Try
                        AttachConsole(ATTACH_PARENT_PROCESS)
                    Catch
                    End Try
                End If
                Dim stageOnly As Boolean = Array.IndexOf(args, "--stage-only") >= 0
                Dim rc As Integer = SelfUpdateDryRun.RunAsync(stageOnly).GetAwaiter().GetResult()
                Environment.Exit(rc)
            End If

            If OperatingSystem.IsWindows() Then
                SetErrorMode(SEM_FAILCRITICALERRORS Or SEM_NOGPFAULTERRORBOX Or SEM_NOOPENFILEERRORBOX)

                ' Install the CTRL_C_EVENT-ignoring handler before
                ' anything else can spawn a child. See the long
                ' comment block above for rationale.
                SetConsoleCtrlHandler(_consoleCtrlHandler, True)
            End If

            Dim builder = WebApplication.CreateBuilder(args)

            builder.Configuration.AddJsonFile("nodesettings.json",
                                              optional:=False,
                                              reloadOnChange:=True)

            ' ---- File logging ----
            ' WebApplication.CreateBuilder wires up Console (visible
            ' in the node's terminal) and Debug providers by default.
            ' We add a file sink alongside so a node running unattended
            ' — spotty wifi at 3am, service-managed startup, etc. —
            ' leaves a diagnostic trail. Daily rotation keeps file size
            ' manageable; PruneOldLogs at startup deletes anything
            ' older than 30 days.
            '
            ' Category filters: we want our own GSM.Node.* lines
            ' (instance starts/stops, install runner steps, version-
            ' check results) but not every ASP.NET Core request log.
            ' Microsoft.* and System.* clamped to Warning gives us a
            ' file dominated by application activity rather than
            ' framework chatter.
            Dim logsDir = IO.Path.Combine(AppContext.BaseDirectory, "logs")
            Dim fileLogProvider As New FileLoggerProvider(logsDir, LogLevel.Information, "node-")
            fileLogProvider.PruneOldLogs(retentionDays:=30)
            builder.Logging.AddProvider(fileLogProvider)
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning)
            builder.Logging.AddFilter("System", LogLevel.Warning)

            ' ---- Bind configuration ----
            Dim nodeConfig As New NodeConfiguration()
            builder.Configuration.GetSection("Node").Bind(nodeConfig)
            nodeConfig.EnsureDefaults()
            builder.Services.AddSingleton(nodeConfig)

            Dim secConfig As New SecurityConfiguration()
            builder.Configuration.GetSection("Security").Bind(secConfig)
            builder.Services.AddSingleton(secConfig)

            ' Support running as Windows Service or systemd unit
            builder.Host.UseWindowsService()
            builder.Host.UseSystemd()

            ' ---- Core services ----
            Dim db As New NodeDatabase(nodeConfig.DataDirectory)
            db.EnsureCreated()
            builder.Services.AddSingleton(db)
            builder.Services.AddSingleton(Of EventStore)()
            builder.Services.AddSingleton(Of ProcessManager)()
            builder.Services.AddSingleton(Of RingBufferStore)()
            builder.Services.AddSingleton(Of RconClientManager)()
            builder.Services.AddSingleton(Of InstallRunner)()
            builder.Services.AddSingleton(Of MapGenerationRunner)()
            builder.Services.AddSingleton(Of SelfUpdateService)()

            ' ---- Security services ----
            builder.Services.AddSingleton(Of AuthFailureTracker)()
            builder.Services.AddSingleton(Of RequestRateTracker)()

            ' ---- Kestrel hardening ----
            builder.WebHost.ConfigureKestrel(Sub(options)
                                                 options.ListenAnyIP(nodeConfig.ListenPort)
                                                 options.AddServerHeader = False
                                                 options.Limits.MaxRequestBodySize = secConfig.MaxRequestBodyBytes
                                                 options.Limits.MaxConcurrentConnections = secConfig.MaxConcurrentConnections
                                                 options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30)
                                             End Sub)

            Dim app = builder.Build()

            ' ---- Hardened auth + abuse-prevention middleware ----
            app.Use(AddressOf AuthAndRateLimitMiddleware)

            ' ---- Map endpoints ----
            Endpoints.SystemEndpoints.Map(app)
            Endpoints.InstanceEndpoints.Map(app)
            Endpoints.InstallEndpoints.Map(app)
            Endpoints.FileEndpoints.Map(app)
            Endpoints.MapGenEndpoints.Map(app)

            ' Re-register our CTRL_C handler ONCE the host has fully
            ' started — by then ASP.NET Core's ConsoleLifetime has
            ' already inserted its own Win32 handler (during
            ' Console.CancelKeyPress subscription on host start), so
            ' this late registration ends up on top of the LIFO chain
            ' and runs first when CTRL_C arrives. Without this step,
            ' ASP.NET's handler would get there first and call
            ' StopApplication(), shutting the host down on every Stop
            ' command — the visible symptom that prompted this fix.
            ' See the long comment block on _consoleCtrlHandler for
            ' the full rationale.
            If OperatingSystem.IsWindows() Then
                Dim lifetime = app.Services.GetRequiredService(Of IHostApplicationLifetime)()
                lifetime.ApplicationStarted.Register(
                    Sub() SetConsoleCtrlHandler(_consoleCtrlHandler, True))
            End If

            ' Self-identifying startup line so log files name what
            ' produced them. Logged at Information so it survives
            ' the Microsoft/System -> Warning category clamp above.
            ' The build string is read off this assembly's
            ' InformationalVersion attribute (set indirectly via
            ' Directory.Build.props' Version property), with the
            ' "+gitsha" suffix the SDK appends in source-linked
            ' builds preserved here — in logs the SHA is useful for
            ' diagnosis even though /api/version strips it.
            Try
                Dim startupLogger = app.Services.
                    GetRequiredService(Of ILoggerFactory)().
                    CreateLogger("GSM.Node")
                Dim asm = GetType(NodeConfiguration).Assembly
                Dim infoAttr = asm.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()
                Dim build As String = If(infoAttr?.InformationalVersion,
                                          asm.GetName().Version?.ToString(3))
                If String.IsNullOrEmpty(build) Then build = "0.0.0"
                startupLogger.LogInformation(
                    "GSM.Node {Build} starting (Protocol v{Protocol}, Contracts v{Contracts}) on port {Port}",
                    build, NodeApiContract.ProtocolVersion,
                    NodeApiContract.ContractsVersion, nodeConfig.ListenPort)
            Catch
                ' Logging failure must never block startup.
            End Try

            ' If node.db was found corrupt and reset during EnsureCreated, say so
            ' loudly now that the logger exists. The lost rows (snapshots, crash
            ' counts, chat mirror, tailer cursors) are node-local cache: the
            ' Manager re-pushes config/rules and the shim sweep below rediscovers
            ' any running games, so a reset is recoverable, not catastrophic.
            If Not String.IsNullOrEmpty(db.LastCorruptionBackup) Then
                Try
                    app.Services.
                        GetRequiredService(Of ILoggerFactory)().
                        CreateLogger("GSM.Node").
                        LogWarning(
                            "node.db was corrupt on startup and has been reset to an empty database. " &
                            "The damaged file was preserved at {Backup}. Node-local cache (instance " &
                            "snapshots, crash history, chat mirror, tailer cursors) was lost; running games " &
                            "will be rediscovered from their shims and the Manager will re-push configuration.",
                            db.LastCorruptionBackup)
                Catch
                End Try
            End If

            ' ---- Adopt previously-spawned game processes ----
            ' Re-attach to game-server processes that outlived the
            ' last node session. Reads InstanceSnapshots, verifies
            ' each saved PID via Process.GetProcessById +
            ' start-time match, and rebuilds the ManagedInstance
            ' record with the live Process handle. Tailers resume
            ' from saved TailerPositions cursors so log events
            ' written during the node-down window stream in once
            ' the new node accepts requests. Runs synchronously
            ' BEFORE app.Run() so endpoint requests never see a
            ' transient "everything is Stopped" view that would
            ' fire false instance-stopped notifications on the
            ' manager side. Per-snapshot failure logs and removes
            ' the offending row; pass-level exceptions are caught
            ' so a wholesale adoption failure can't prevent the
            ' node from accepting requests at all.
            Try
                Dim pm = app.Services.GetRequiredService(Of ProcessManager)()
                pm.AdoptSnapshots()

                ' Phase 8-3 — rediscover live shims the snapshot pass didn't
                ' cover (e.g. a lost or corrupt node.db) by enumerating the OS
                ' shim namespace and lean-adopting any running shim not already
                ' adopted. Runs AFTER the snapshot pass so snapshot-backed
                ' instances keep their full recovery payload; the sweep only
                ' fills the gaps. Same synchronous startup context, and the
                ' sweep is internally try-guarded per endpoint.
                pm.SweepAdoptLiveShims()
            Catch ex As Exception
                Try
                    Dim adoptLogger = app.Services.
                        GetRequiredService(Of ILoggerFactory)().
                        CreateLogger("GSM.Node.Adoption")
                    adoptLogger.LogError(ex,
                        "Snapshot adoption pass threw at top level — startup continues with empty _instances dict")
                Catch
                    ' If even logging fails, swallow rather than
                    ' refusing to start the node.
                End Try
            End Try

            ' ---- Clean-shutdown shim detach (Phase 8-1) ----
            ' On a graceful Node stop/restart (Ctrl+C, SIGTERM, service stop),
            ' tell each shim to Detach so its game keeps running and the shim
            ' waits for the next Node. Registered after adoption so the pm is
            ' built. ApplicationStopping does NOT fire on a hard kill — but a
            ' hard kill just drops the pipe, which the shim also treats as
            ' "keep the game and wait", so survival doesn't depend on this hook;
            ' it only suppresses a spurious lost-Node on the shim side.
            Try
                Dim lifetimeForShim = app.Services.GetRequiredService(Of IHostApplicationLifetime)()
                Dim pmForShutdown = app.Services.GetRequiredService(Of ProcessManager)()
                lifetimeForShim.ApplicationStopping.Register(
                    Sub() pmForShutdown.DetachShimsForShutdown())
            Catch
                ' Non-fatal: without this, a clean shutdown still drops the
                ' pipe and the shim keeps the game; we just skip the tidy Detach.
            End Try

            ' ---- Phase 8-2 slice 8b-2 — confirm a systemd self-update is healthy ----
            ' Under systemd the unit's ExecStartPre drops a "<node>.update-pending"
            ' marker when it applies a staged update, and reverts to .old on the
            ' next start if the marker is still there (the update never proved
            ' healthy). Clear the marker once the host has been up for a short
            ' grace period, so a binary that comes up then crashes inside the
            ' window still rolls back. The file only exists right after a systemd
            ' update apply; the delete is a harmless no-op otherwise (Windows /
            ' bare nodes use the NodeSetup survivor's own health gate instead).
            Try
                Dim lifetimeForUpdate = app.Services.GetRequiredService(Of IHostApplicationLifetime)()
                lifetimeForUpdate.ApplicationStarted.Register(AddressOf ScheduleUpdateMarkerClear)
            Catch
                ' Non-fatal: a lingering marker only affects the NEXT start after
                ' an update apply, and the operator can delete it by hand.
            End Try

            ' ---- Phase 8-2 — Node self-update exit code (capture before Run) ----
            ' app.Run() disposes the host and its DI container when it returns,
            ' so the SelfUpdateService must be resolved BEFORE Run — resolving it
            ' afterwards throws ObjectDisposedException (which previously got
            ' swallowed, so the node always exited 0 and systemd never restarted
            ' it after an update-exit). We hold the reference here; the instance
            ' stays alive and reading its flags post-shutdown is fine.
            Dim selfUpdateForExit As SelfUpdateService = Nothing
            Try
                selfUpdateForExit = app.Services.GetService(Of SelfUpdateService)()
            Catch
                ' If resolution fails, the update-exit simply falls back to a
                ' clean exit; never block startup over it.
            End Try

            app.Run()

            ' app.Run() returns once the host has fully stopped. If the stop was
            ' an update-exit relying on systemd's Restart=on-failure to bring us
            ' back (Linux under systemd), exit non-zero so systemd relaunches;
            ' the idempotent ExecStartPre swap moves GSM.Node.new into place
            ' first. A clean exit (0) here is either a normal shutdown or an
            ' update-exit where NodeSetup owns the relaunch.
            If selfUpdateForExit IsNot Nothing AndAlso
               selfUpdateForExit.UpdateExitRequested AndAlso
               selfUpdateForExit.ExitNonZeroForSystemd Then
                Environment.Exit(SelfUpdateService.UpdateExitCode)
            End If

        End Sub

        ' ---- Phase 8-2 slice 8b-2 — systemd self-update health marker ----

        Private Const UpdatePendingMarkerSuffix As String = ".update-pending"
        Private Const UpdateHealthyGraceMs As Integer = 15000

        ''' <summary>
        ''' Fired on ApplicationStarted. Schedules a delayed clear of the
        ''' systemd self-update marker so a node that comes up and then crashes
        ''' inside the grace window still leaves the marker for the unit's
        ''' ExecStartPre to roll back on the next start.
        ''' </summary>
        Private Sub ScheduleUpdateMarkerClear()
            ' Fire-and-forget; the delay + delete runs off the startup thread.
            Task.Run(AddressOf ClearUpdatePendingMarkerAfterGraceAsync)
        End Sub

        ''' <summary>Waits the grace period, then clears the update marker.</summary>
        Private Async Function ClearUpdatePendingMarkerAfterGraceAsync() As Task
            Try
                Await Task.Delay(UpdateHealthyGraceMs)
                ClearUpdatePendingMarker()
            Catch
                ' Best effort: a lingering marker only affects the NEXT start
                ' after an update, and the operator can remove it manually.
            End Try
        End Function

        ''' <summary>
        ''' Deletes the "&lt;node&gt;.update-pending" marker beside the live binary
        ''' if present — the signal the systemd survivor uses to know the applied
        ''' update came up healthy. No-op when absent (every non-update start,
        ''' and all Windows / bare nodes).
        ''' </summary>
        Private Sub ClearUpdatePendingMarker()
            Try
                Dim exeName = If(OperatingSystem.IsWindows(), "GSM.Node.exe", "GSM.Node")
                Dim marker = Path.Combine(AppContext.BaseDirectory, exeName & UpdatePendingMarkerSuffix)
                If File.Exists(marker) Then File.Delete(marker)
            Catch
                ' Best effort.
            End Try
        End Sub

        ''' <summary>
        ''' Combined rate-limit + lockout + auth middleware.
        ''' Order of checks (cheapest rejection first):
        '''   1. Per-IP rate limit  -> 429
        '''   2. Per-IP lockout     -> 429 with Retry-After
        '''   3. /api/version skip  -> pass through
        '''   4. Bearer token check (constant-time)
        '''      - on failure: record + delay + generic 401
        '''      - on success: reset failure history, pass through
        ''' </summary>
        Private Async Function AuthAndRateLimitMiddleware(
                context As HttpContext,
                nextDelegate As Func(Of Task)) As Task

            Dim ip = context.Connection.RemoteIpAddress?.ToString()
            If String.IsNullOrEmpty(ip) Then ip = "unknown"

            Dim rateTracker = context.RequestServices.GetRequiredService(Of RequestRateTracker)()
            Dim authTracker = context.RequestServices.GetRequiredService(Of AuthFailureTracker)()
            Dim secCfg = context.RequestServices.GetRequiredService(Of SecurityConfiguration)()

            ' 1. Global per-IP rate limit
            If rateTracker.IsOverLimit(ip) Then
                context.Response.StatusCode = 429
                context.Response.Headers("Retry-After") = "60"
                Await context.Response.WriteAsJsonAsync(New With {.error = "Rate limit exceeded"})
                Return
            End If

            ' 2. Auth-failure lockout
            Dim lockoutExpiry = authTracker.GetLockoutExpiry(ip)
            If lockoutExpiry > DateTime.UtcNow Then
                Dim retryAfter = CInt(Math.Ceiling((lockoutExpiry - DateTime.UtcNow).TotalSeconds))
                context.Response.StatusCode = 429
                context.Response.Headers("Retry-After") = retryAfter.ToString()
                Await context.Response.WriteAsJsonAsync(New With {.error = "Too many failed attempts"})
                Return
            End If

            ' 3. /api/version is intentionally unauthenticated for connectivity tests.
            If context.Request.Path.StartsWithSegments("/api/version") Then
                Await nextDelegate()
                Return
            End If

            ' 4. Bearer token check
            Dim cfg = context.RequestServices.GetRequiredService(Of NodeConfiguration)()
            Dim authHeader = context.Request.Headers("Authorization").ToString()
            Dim ok As Boolean = False

            If Not String.IsNullOrEmpty(authHeader) AndAlso
               authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) Then
                Dim token = authHeader.Substring(7).Trim()
                ok = SecurityHelpers.FixedTimeStringEquals(token, cfg.AuthToken)
            End If

            If Not ok Then
                authTracker.RecordFailure(ip)
                If secCfg.AuthFailureDelayMs > 0 Then
                    Await Task.Delay(secCfg.AuthFailureDelayMs)
                End If
                ' Generic response — do not differentiate missing vs invalid.
                context.Response.StatusCode = 401
                Await context.Response.WriteAsJsonAsync(New With {.error = "Unauthorized"})
                Return
            End If

            authTracker.Reset(ip)
            Await nextDelegate()

        End Function

    End Module

    ' ============================================================
    '  NodeConfiguration — bound from nodesettings.json
    ' ============================================================

    Public Class NodeConfiguration
        Public Property NodeId As String
        Public Property ListenPort As Integer = 8765
        Public Property AuthToken As String
        Public Property DataDirectory As String = "./data"
        Public Property MaxConcurrentInstalls As Integer = 2
        Public Property LogRetentionDays As Integer = 30
        Public Property MetricsIntervalSeconds As Integer = 5

        ''' <summary>
        ''' Kill-switch for the Phase 8-1 per-instance shim supervisor.
        ''' When False (default), Strategy A (stdout-captured) instances
        ''' run their game under a GSM.Shim supervisor so a Node restart
        ''' doesn't sever the game's stdio. When True, Strategy A falls
        ''' back to the legacy in-Node spawn. Strategy B/C are unaffected
        ''' (they move under the shim in a later slice).
        '''
        ''' NOTE: until graceful stop lands (Phase 8 slice 5), a shim-mode
        ''' Stop hard-kills the game (no save-and-quit). Set this True on
        ''' nodes hosting save-sensitive games until then.
        ''' </summary>
        Public Property DisableShim As Boolean = False

        ''' <summary>
        ''' Default parent directory for new game-server installations.
        ''' Defaults to a sibling "servers" folder next to the node
        ''' executable so a fresh install just works without the user
        ''' picking a path. Override in nodesettings.json by setting
        ''' Node:ServersDirectory — e.g. when the node runs as a service
        ''' and game files belong on a separate volume.
        '''
        ''' Resolved to absolute by EnsureDefaults so the manager
        ''' receives a usable path even when the node is launched
        ''' from a different working directory than its binary.
        ''' </summary>
        Public Property ServersDirectory As String = "./servers"

        Public Sub EnsureDefaults()
            If String.IsNullOrEmpty(NodeId) Then
                NodeId = Environment.MachineName
            End If
            If String.IsNullOrEmpty(DataDirectory) Then
                DataDirectory = "./data"
            End If
            If String.IsNullOrEmpty(ServersDirectory) Then
                ServersDirectory = "./servers"
            End If
            ' Resolve to absolute against the binary's directory rather
            ' than the process working directory — services and shortcuts
            ' often start with a different cwd than where the exe lives,
            ' and the user expects "./servers" or "./data" to mean "next
            ' to the node binary". Path.GetFullPath uses
            ' Environment.CurrentDirectory which would give the wrong
            ' answer there.
            '
            ' GSM.NodeSetup writes absolute paths into nodesettings.json
            ' by default (see NodeSection.DefaultDataDirectory), so this
            ' branch is mostly a fallback for hand-written configs and
            ' upgrades from older versions where the defaults were
            ' relative.
            Try
                If Not Path.IsPathRooted(DataDirectory) Then
                    DataDirectory = Path.GetFullPath(DataDirectory, AppContext.BaseDirectory)
                Else
                    DataDirectory = Path.GetFullPath(DataDirectory)
                End If
            Catch
                ' If GetFullPath throws on a malformed value, leave the
                ' raw setting untouched; NodeDatabase will fail loudly
                ' on the bad path which is better than silently writing
                ' the wrong location.
            End Try
            Try
                If Not Path.IsPathRooted(ServersDirectory) Then
                    ServersDirectory = Path.GetFullPath(ServersDirectory, AppContext.BaseDirectory)
                Else
                    ServersDirectory = Path.GetFullPath(ServersDirectory)
                End If
            Catch
                ' If GetFullPath throws on a malformed value, leave the
                ' raw setting untouched; the manager will fall back to
                ' its placeholder when it sees something unusable.
            End Try
        End Sub
    End Class

    ' ============================================================
    '  NodeDatabase — raw SQLite via Microsoft.Data.Sqlite
    '  No EF Core. Stores crash events, instance state snapshots,
    '  and install history for node-local persistence.
    ' ============================================================

    Public Class NodeDatabase

        Private ReadOnly _connectionString As String
        Private ReadOnly _dataDir As String

        Public Sub New(dataDirectory As String)
            _dataDir = dataDirectory
            Directory.CreateDirectory(_dataDir)
            Dim dbPath = Path.Combine(_dataDir, "node.db")
            _connectionString = $"Data Source={dbPath}"
        End Sub

        ' SQLite extended-result codes for a damaged / foreign database file.
        Private Const SQLITE_CORRUPT As Integer = 11   ' "database disk image is malformed"
        Private Const SQLITE_NOTADB As Integer = 26    ' "file is not a database"

        Private _lastCorruptionBackup As String

        ''' <summary>
        ''' If EnsureCreated found node.db corrupt and reset it, the path the bad
        ''' file was preserved at; Nothing otherwise. NodeProgram logs this once
        ''' the logger is up.
        ''' </summary>
        Public ReadOnly Property LastCorruptionBackup As String
            Get
                Return _lastCorruptionBackup
            End Get
        End Property

        ''' <summary>
        ''' Creates tables if they do not exist. Self-heals a corrupt node.db:
        ''' SQLITE_CORRUPT / SQLITE_NOTADB on open or first DDL would otherwise
        ''' crash the node on startup (and crash-loop it under systemd). On those
        ''' two codes ONLY, the bad file is renamed aside (node.db.corrupt-&lt;ts&gt;)
        ''' and recreated empty — losing only node-local cache (snapshots, crash
        ''' counts, chat mirror, tailer cursors), which the Manager re-pushes and
        ''' the shim sweep rediscovers. Any other SqliteException (locked, busy,
        ''' readonly, ...) is NOT corruption and propagates unchanged.
        ''' </summary>
        Public Sub EnsureCreated()
            Try
                EnsureCreatedCore()
            Catch ex As SqliteException When ex.SqliteErrorCode = SQLITE_CORRUPT OrElse
                                              ex.SqliteErrorCode = SQLITE_NOTADB
                BackupAndDeleteCorruptDb()
                EnsureCreatedCore()   ' retry once on a fresh, empty file
            End Try
        End Sub

        ''' <summary>
        ''' Renames a corrupt node.db aside (preserved for forensics) and clears
        ''' its sidecars so EnsureCreatedCore can recreate an empty DB. Records
        ''' the backup path in LastCorruptionBackup for NodeProgram to log once
        ''' the logger exists.
        ''' </summary>
        Private Sub BackupAndDeleteCorruptDb()
            ' Drop pooled handles so the file isn't locked when we move it.
            Try
                SqliteConnection.ClearAllPools()
            Catch
            End Try
            Dim dbPath = Path.Combine(_dataDir, "node.db")
            Dim stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
            Dim backup = Path.Combine(_dataDir, $"node.db.corrupt-{stamp}")
            Try
                If File.Exists(dbPath) Then
                    File.Move(dbPath, backup)
                    _lastCorruptionBackup = backup
                End If
            Catch
                ' If the move fails, fall back to delete so the node can at least
                ' start on a fresh DB.
                Try
                    If File.Exists(dbPath) Then File.Delete(dbPath)
                Catch
                End Try
            End Try
            ' Best-effort: clear transient sidecars so the new DB starts clean.
            For Each suffix In {"-wal", "-shm", "-journal"}
                Try
                    Dim sidecar = dbPath & suffix
                    If File.Exists(sidecar) Then File.Delete(sidecar)
                Catch
                End Try
            Next
        End Sub

        ''' <summary>
        ''' Creates tables if they do not exist (the original body, now wrapped
        ''' by EnsureCreated's corruption guard).
        ''' </summary>
        Private Sub EnsureCreatedCore()
            Using conn As New SqliteConnection(_connectionString)
                conn.Open()

                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "
                        CREATE TABLE IF NOT EXISTS CrashEvents (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            InstanceId TEXT NOT NULL,
                            Timestamp TEXT NOT NULL,
                            ExitCode INTEGER,
                            DetectionMethod TEXT,
                            RestartDecision TEXT,
                            Reason TEXT
                        );

                        CREATE TABLE IF NOT EXISTS InstanceSnapshots (
                            InstanceId TEXT PRIMARY KEY,
                            State TEXT NOT NULL,
                            Pid INTEGER,
                            StartedAtUtc TEXT,
                            CrashPolicyJson TEXT,
                            StopIntentPending INTEGER DEFAULT 0
                        );

                        CREATE TABLE IF NOT EXISTS InstallHistory (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            InstallationId TEXT NOT NULL,
                            GameId TEXT,
                            StartedAtUtc TEXT NOT NULL,
                            CompletedAtUtc TEXT,
                            Success INTEGER,
                            StepCount INTEGER,
                            ErrorMessage TEXT
                        );

                        -- Per-instance, per-log-file tailer cursor. Lets the
                        -- node skip log-history replay across instance restarts:
                        -- Factorio appends to the same file across runs and
                        -- without this, the tailer re-reads the entire file
                        -- on every start, re-firing chat/join/leave events
                        -- and producing duplicate rows in chat_messages.
                        --
                        -- Fingerprint is SHA-256 of the file's first 256 bytes,
                        -- used to discriminate ''same file, more bytes appended''
                        -- (resume from saved position) from ''file replaced at
                        -- same path'' (e.g. LO archives the old log and starts
                        -- a new one; resume isn't safe because the saved byte
                        -- offset means nothing in the new content).
                        --
                        -- Composite primary key on (InstanceId, LogPath) since
                        -- one instance may tail multiple files (Factorio tails
                        -- both factorio-current.log and factorio-console.log).
                        CREATE TABLE IF NOT EXISTS TailerPositions (
                            InstanceId TEXT NOT NULL,
                            LogPath TEXT NOT NULL,
                            BytePosition INTEGER NOT NULL,
                            Fingerprint TEXT NOT NULL,
                            UpdatedAtUtc TEXT NOT NULL,
                            PRIMARY KEY (InstanceId, LogPath)
                        );

                        CREATE INDEX IF NOT EXISTS IX_CrashEvents_Instance
                            ON CrashEvents(InstanceId, Timestamp);
                    "
                    cmd.ExecuteNonQuery()
                End Using

                ' ----------------------------------------------------
                ' InstanceSnapshots schema migration (additive only)
                '
                ' Adds columns needed for process re-adoption on node
                ' restart: ExePath, Arguments, WorkingDirectory,
                ' LogFilePathsJson, ParseRulesJson, Strategy,
                ' StdoutIsLog, RequiresConsoleIsolation,
                ' LogTailerStartDelayMs; and the Phase 8-1 shim columns
                ' ShimEndpoint, ShimPid, ShimProtocolVersion, ExecutionMode
                ' (ExecutionMode 0 = Direct, the legacy path). Discovered via
                ' PRAGMA table_info so an upgraded node with
                ' pre-existing snapshots from the prior schema
                ' picks up the additions exactly once, and a fresh
                ' install (CREATE TABLE just ran with the old shape)
                ' converges to the same final schema.
                '
                ' Defaults chosen so a row created under the old
                ' schema still reads sensibly via
                ' LoadAllInstanceSnapshots: text columns are NULL
                ' (which adoption treats as "insufficient data,
                ' discard this snapshot"), Strategy defaults to 0
                ' (StdoutCapture), booleans default to 0,
                ' LogTailerStartDelayMs defaults to 5000ms matching
                ' the in-memory default.
                ' ----------------------------------------------------
                Dim existing As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "PRAGMA table_info(InstanceSnapshots)"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            ' Column 1 of PRAGMA table_info output
                            ' is the column name.
                            existing.Add(reader.GetString(1))
                        End While
                    End Using
                End Using

                Dim columnsToAdd As New List(Of (Name As String, Definition As String)) From {
                    ("ExePath", "TEXT"),
                    ("Arguments", "TEXT"),
                    ("WorkingDirectory", "TEXT"),
                    ("LogFilePathsJson", "TEXT"),
                    ("ParseRulesJson", "TEXT"),
                    ("Strategy", "INTEGER NOT NULL DEFAULT 0"),
                    ("StdoutIsLog", "INTEGER NOT NULL DEFAULT 0"),
                    ("RequiresConsoleIsolation", "INTEGER NOT NULL DEFAULT 0"),
                    ("LogTailerStartDelayMs", "INTEGER NOT NULL DEFAULT 5000"),
                    ("ShimEndpoint", "TEXT"),
                    ("ShimPid", "INTEGER NOT NULL DEFAULT 0"),
                    ("ShimProtocolVersion", "INTEGER NOT NULL DEFAULT 0"),
                    ("ExecutionMode", "INTEGER NOT NULL DEFAULT 0")
                }

                For Each col In columnsToAdd
                    If existing.Contains(col.Name) Then Continue For
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = $"ALTER TABLE InstanceSnapshots ADD COLUMN {col.Name} {col.Definition}"
                        cmd.ExecuteNonQuery()
                    End Using
                Next
            End Using
        End Sub

        ''' <summary>
        ''' Opens a new connection to the node database.
        ''' Caller is responsible for disposing.
        ''' </summary>
        Public Function OpenConnection() As SqliteConnection
            Dim conn As New SqliteConnection(_connectionString)
            conn.Open()
            Return conn
        End Function

        ''' <summary>
        ''' Records a crash event for sliding window calculations.
        ''' </summary>
        Public Sub RecordCrashEvent(instanceId As String,
                                    exitCode As Integer,
                                    detectionMethod As String,
                                    restartDecision As String,
                                    reason As String)
            Using conn = OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "INSERT INTO CrashEvents
                        (InstanceId, Timestamp, ExitCode, DetectionMethod, RestartDecision, Reason)
                        VALUES (@id, @ts, @exit, @detect, @decision, @reason)"
                    cmd.Parameters.AddWithValue("@id", instanceId)
                    cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"))
                    cmd.Parameters.AddWithValue("@exit", exitCode)
                    cmd.Parameters.AddWithValue("@detect", detectionMethod)
                    cmd.Parameters.AddWithValue("@decision", restartDecision)
                    cmd.Parameters.AddWithValue("@reason", reason)
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>
        ''' Returns the number of crashes in the given window for
        ''' sliding window crash loop detection.
        ''' </summary>
        Public Function GetCrashCountInWindow(instanceId As String,
                                              windowMinutes As Integer) As Integer
            Using conn = OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT COUNT(*) FROM CrashEvents
                        WHERE InstanceId = @id
                          AND Timestamp >= @since"
                    cmd.Parameters.AddWithValue("@id", instanceId)
                    cmd.Parameters.AddWithValue("@since",
                        DateTime.UtcNow.AddMinutes(-windowMinutes).ToString("o"))
                    Return Convert.ToInt32(cmd.ExecuteScalar())
                End Using
            End Using
        End Function

        ''' <summary>
        ''' Saves an instance state snapshot for persistence across
        ''' node restarts. The recovery columns (ExePath, Arguments,
        ''' WorkingDirectory, LogFilePathsJson, ParseRulesJson,
        ''' Strategy, StdoutIsLog, RequiresConsoleIsolation,
        ''' LogTailerStartDelayMs) carry everything the node needs
        ''' to re-adopt a running game process on the next startup
        ''' — see ProcessManager.AdoptSnapshots.
        ''' </summary>
        Public Sub SaveInstanceSnapshot(instanceId As String,
                                        state As String,
                                        pid As Integer,
                                        startedAtUtc As DateTime,
                                        crashPolicyJson As String,
                                        stopIntentPending As Boolean,
                                        exePath As String,
                                        arguments As String,
                                        workingDirectory As String,
                                        logFilePathsJson As String,
                                        parseRulesJson As String,
                                        strategy As Integer,
                                        stdoutIsLog As Boolean,
                                        requiresConsoleIsolation As Boolean,
                                        logTailerStartDelayMs As Integer,
                                        Optional shimEndpoint As String = Nothing,
                                        Optional shimPid As Integer = 0,
                                        Optional shimProtocolVersion As Integer = 0,
                                        Optional executionMode As Integer = 0)
            Using conn = OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "INSERT OR REPLACE INTO InstanceSnapshots
                        (InstanceId, State, Pid, StartedAtUtc, CrashPolicyJson, StopIntentPending,
                         ExePath, Arguments, WorkingDirectory, LogFilePathsJson, ParseRulesJson,
                         Strategy, StdoutIsLog, RequiresConsoleIsolation, LogTailerStartDelayMs,
                         ShimEndpoint, ShimPid, ShimProtocolVersion, ExecutionMode)
                        VALUES (@id, @state, @pid, @started, @policy, @intent,
                                @exe, @args, @cwd, @logs, @rules,
                                @strategy, @stdoutLog, @iso, @delay,
                                @shimEndpoint, @shimPid, @shimProto, @execMode)"
                    cmd.Parameters.AddWithValue("@id", instanceId)
                    cmd.Parameters.AddWithValue("@state", state)
                    cmd.Parameters.AddWithValue("@pid", pid)
                    cmd.Parameters.AddWithValue("@started", startedAtUtc.ToString("o"))
                    cmd.Parameters.AddWithValue("@policy", If(crashPolicyJson, CObj(DBNull.Value)))
                    cmd.Parameters.AddWithValue("@intent", If(stopIntentPending, 1, 0))
                    cmd.Parameters.AddWithValue("@exe", If(exePath, CObj(DBNull.Value)))
                    cmd.Parameters.AddWithValue("@args", If(arguments, CObj(DBNull.Value)))
                    cmd.Parameters.AddWithValue("@cwd", If(workingDirectory, CObj(DBNull.Value)))
                    cmd.Parameters.AddWithValue("@logs", If(logFilePathsJson, CObj(DBNull.Value)))
                    cmd.Parameters.AddWithValue("@rules", If(parseRulesJson, CObj(DBNull.Value)))
                    cmd.Parameters.AddWithValue("@strategy", strategy)
                    cmd.Parameters.AddWithValue("@stdoutLog", If(stdoutIsLog, 1, 0))
                    cmd.Parameters.AddWithValue("@iso", If(requiresConsoleIsolation, 1, 0))
                    cmd.Parameters.AddWithValue("@delay", logTailerStartDelayMs)
                    cmd.Parameters.AddWithValue("@shimEndpoint", If(shimEndpoint, CObj(DBNull.Value)))
                    cmd.Parameters.AddWithValue("@shimPid", shimPid)
                    cmd.Parameters.AddWithValue("@shimProto", shimProtocolVersion)
                    cmd.Parameters.AddWithValue("@execMode", executionMode)
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>
        ''' Reads every persisted instance snapshot. Called once on
        ''' node startup by ProcessManager.AdoptSnapshots to try to
        ''' re-adopt game processes that survived the last node
        ''' restart. Returns an empty list when no rows exist (fresh
        ''' install or all snapshots cleared by graceful stops).
        '''
        ''' Null tolerance on every column except InstanceId / State
        ''' is intentional — snapshots written under the pre-
        ''' migration schema have NULLs for every recovery field,
        ''' and the caller treats those as "insufficient data, can't
        ''' adopt this one" rather than crashing on a DBNull cast.
        ''' </summary>
        Public Function LoadAllInstanceSnapshots() As IReadOnlyList(Of InstanceSnapshotRow)
            Dim rows As New List(Of InstanceSnapshotRow)
            Using conn = OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT InstanceId, State, Pid, StartedAtUtc, CrashPolicyJson, StopIntentPending,
                                              ExePath, Arguments, WorkingDirectory, LogFilePathsJson, ParseRulesJson,
                                              Strategy, StdoutIsLog, RequiresConsoleIsolation, LogTailerStartDelayMs,
                                              ShimEndpoint, ShimPid, ShimProtocolVersion, ExecutionMode
                                       FROM InstanceSnapshots"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim row As New InstanceSnapshotRow()
                            row.InstanceId = reader.GetString(0)
                            row.State = If(reader.IsDBNull(1), Nothing, reader.GetString(1))
                            row.Pid = If(reader.IsDBNull(2), 0, reader.GetInt32(2))
                            If reader.IsDBNull(3) Then
                                row.StartedAtUtc = DateTime.MinValue
                            Else
                                ' RoundtripKind alone is the correct
                                ' style here — SaveInstanceSnapshot
                                ' writes startedAtUtc.ToString("o")
                                ' which encodes Kind=Utc as a trailing
                                ' "Z", and RoundtripKind preserves that
                                ' on parse. Earlier this also OR'd in
                                ' DateTimeStyles.AssumeUniversal as
                                ' belt-and-braces, but the BCL treats
                                ' RoundtripKind and the Assume*/Adjust*
                                ' values as mutually exclusive and
                                ' throws ArgumentException up front
                                ' from DateTime.Parse — which on the
                                ' first call from AdoptSnapshots tore
                                ' down the entire load, causing every
                                ' snapshot to be skipped with an empty
                                ' _instances dict and no instances
                                ' getting re-adopted on startup.
                                row.StartedAtUtc = DateTime.Parse(reader.GetString(3),
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind)
                            End If
                            row.CrashPolicyJson = If(reader.IsDBNull(4), Nothing, reader.GetString(4))
                            row.StopIntentPending = Not reader.IsDBNull(5) AndAlso reader.GetInt32(5) <> 0
                            row.ExePath = If(reader.IsDBNull(6), Nothing, reader.GetString(6))
                            row.Arguments = If(reader.IsDBNull(7), Nothing, reader.GetString(7))
                            row.WorkingDirectory = If(reader.IsDBNull(8), Nothing, reader.GetString(8))
                            row.LogFilePathsJson = If(reader.IsDBNull(9), Nothing, reader.GetString(9))
                            row.ParseRulesJson = If(reader.IsDBNull(10), Nothing, reader.GetString(10))
                            row.Strategy = If(reader.IsDBNull(11), 0, reader.GetInt32(11))
                            row.StdoutIsLog = Not reader.IsDBNull(12) AndAlso reader.GetInt32(12) <> 0
                            row.RequiresConsoleIsolation = Not reader.IsDBNull(13) AndAlso reader.GetInt32(13) <> 0
                            row.LogTailerStartDelayMs = If(reader.IsDBNull(14), 5000, reader.GetInt32(14))
                            row.ShimEndpoint = If(reader.IsDBNull(15), Nothing, reader.GetString(15))
                            row.ShimPid = If(reader.IsDBNull(16), 0, reader.GetInt32(16))
                            row.ShimProtocolVersion = If(reader.IsDBNull(17), 0, reader.GetInt32(17))
                            row.ExecutionMode = If(reader.IsDBNull(18), 0, reader.GetInt32(18))
                            rows.Add(row)
                        End While
                    End Using
                End Using
            End Using
            Return rows
        End Function

        ''' <summary>
        ''' Removes an instance snapshot when an instance is fully stopped.
        ''' </summary>
        Public Sub RemoveInstanceSnapshot(instanceId As String)
            Using conn = OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "DELETE FROM InstanceSnapshots WHERE InstanceId = @id"
                    cmd.Parameters.AddWithValue("@id", instanceId)
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>
        ''' Records a completed install operation.
        ''' </summary>
        Public Sub RecordInstallHistory(installationId As String,
                                        gameId As String,
                                        startedAtUtc As DateTime,
                                        completedAtUtc As DateTime,
                                        success As Boolean,
                                        stepCount As Integer,
                                        errorMessage As String)
            Using conn = OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "INSERT INTO InstallHistory
                        (InstallationId, GameId, StartedAtUtc, CompletedAtUtc, Success, StepCount, ErrorMessage)
                        VALUES (@iid, @gid, @started, @completed, @ok, @steps, @err)"
                    cmd.Parameters.AddWithValue("@iid", installationId)
                    cmd.Parameters.AddWithValue("@gid", If(gameId, CObj(DBNull.Value)))
                    cmd.Parameters.AddWithValue("@started", startedAtUtc.ToString("o"))
                    cmd.Parameters.AddWithValue("@completed", completedAtUtc.ToString("o"))
                    cmd.Parameters.AddWithValue("@ok", If(success, 1, 0))
                    cmd.Parameters.AddWithValue("@steps", stepCount)
                    cmd.Parameters.AddWithValue("@err", If(errorMessage, CObj(DBNull.Value)))
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ' ============================================================
        '  Tailer position persistence
        '
        '  See the comment on the TailerPositions table in
        '  EnsureCreated for the why. The shape here is deliberately
        '  small — two reads (Get on tailer first-open, Save after
        '  every successful read iteration) and no batching. SQLite
        '  writes are sub-millisecond on local disks, and persistence
        '  on every iteration is what lets a node crash mid-tail lose
        '  at most one poll cycle's worth of position drift.
        ' ============================================================

        ''' <summary>
        ''' Returns the saved tailer cursor for (instanceId, logPath),
        ''' or Nothing when no row exists. Caller compares the saved
        ''' Fingerprint against a fresh hash of the current file's
        ''' first bytes to decide whether the position is still valid
        ''' for the file present at that path.
        ''' </summary>
        Public Function GetTailerPosition(instanceId As String,
                                          logPath As String) As TailerPositionRow
            Using conn = OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT BytePosition, Fingerprint
                        FROM TailerPositions
                        WHERE InstanceId = @id AND LogPath = @path"
                    cmd.Parameters.AddWithValue("@id", instanceId)
                    cmd.Parameters.AddWithValue("@path", logPath)
                    Using reader = cmd.ExecuteReader()
                        If Not reader.Read() Then Return Nothing
                        Return New TailerPositionRow With {
                            .BytePosition = reader.GetInt64(0),
                            .Fingerprint = reader.GetString(1)
                        }
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>
        ''' Upserts the tailer cursor for (instanceId, logPath).
        ''' Called after every successful read iteration in the
        ''' tailer loop — cheap on local SQLite, and persistence
        ''' density determines how much progress is lost if the node
        ''' is killed mid-tail. (Worst case: ~500ms of unread tail
        ''' that we re-read on next start, which is harmless since
        ''' the position cursor catches up immediately.)
        ''' </summary>
        Public Sub SaveTailerPosition(instanceId As String,
                                       logPath As String,
                                       bytePosition As Long,
                                       fingerprint As String)
            Using conn = OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "INSERT OR REPLACE INTO TailerPositions
                        (InstanceId, LogPath, BytePosition, Fingerprint, UpdatedAtUtc)
                        VALUES (@id, @path, @pos, @fp, @ts)"
                    cmd.Parameters.AddWithValue("@id", instanceId)
                    cmd.Parameters.AddWithValue("@path", logPath)
                    cmd.Parameters.AddWithValue("@pos", bytePosition)
                    cmd.Parameters.AddWithValue("@fp", fingerprint)
                    cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"))
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

    End Class

    ''' <summary>
    ''' Single row from the TailerPositions table. Returned by
    ''' NodeDatabase.GetTailerPosition; Nothing means no saved cursor.
    ''' </summary>
    Public Class TailerPositionRow
        Public Property BytePosition As Long
        Public Property Fingerprint As String
    End Class

    ''' <summary>
    ''' Single row from the InstanceSnapshots table. Returned by
    ''' NodeDatabase.LoadAllInstanceSnapshots and consumed by
    ''' ProcessManager.AdoptSnapshots to re-adopt running game
    ''' processes after a node restart. All recovery-payload
    ''' fields are nullable / defaulted because pre-migration rows
    ''' have NULL for them — the consumer treats those rows as
    ''' undadoptable rather than crashing on a missing field.
    ''' </summary>
    Public Class InstanceSnapshotRow
        Public Property InstanceId As String
        Public Property State As String
        Public Property Pid As Integer
        Public Property StartedAtUtc As DateTime
        Public Property CrashPolicyJson As String
        Public Property StopIntentPending As Boolean
        Public Property ExePath As String
        Public Property Arguments As String
        Public Property WorkingDirectory As String
        Public Property LogFilePathsJson As String
        Public Property ParseRulesJson As String
        Public Property Strategy As Integer
        Public Property StdoutIsLog As Boolean
        Public Property RequiresConsoleIsolation As Boolean
        Public Property LogTailerStartDelayMs As Integer
        Public Property ShimEndpoint As String
        Public Property ShimPid As Integer
        Public Property ShimProtocolVersion As Integer
        Public Property ExecutionMode As Integer
    End Class

End Namespace
