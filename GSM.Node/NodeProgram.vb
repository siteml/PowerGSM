Imports System
Imports System.IO
Imports System.Threading.Tasks
Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Hosting
Imports Microsoft.AspNetCore.Http
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Data.Sqlite
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
        '   control handler that ignores CTRL_C_EVENT. The node's
        '   console stays attached and visible; the CTRL_C our
        '   GSM.CtrlCSender helper fires (intended for a child) still
        '   reaches the node but the handler returns TRUE meaning
        '   "handled, don't terminate"; the child's own handler runs
        '   the graceful-shutdown path as intended.
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
        '   for CTRL_C. Returning TRUE stops the chain before
        '   ASP.NET's handler can run. The early registration is kept
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
                If ctrlType = CTRL_C_EVENT Then
                    Return True   ' "handled, do not terminate"
                End If
                Return False      ' Let the default handler run
            End Function

        Private Delegate Function ConsoleCtrlDelegate(ctrlType As UInteger) As Boolean

        <Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True)>
        Private Function SetConsoleCtrlHandler(handler As ConsoleCtrlDelegate,
                                                add As Boolean) As Boolean
        End Function

        Sub Main(args As String())

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

            app.Run()

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
            ' and the user expects "./servers" to mean "next to the node
            ' binary". Path.GetFullPath uses Environment.CurrentDirectory
            ' which would give the wrong answer there.
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

        ''' <summary>
        ''' Creates tables if they do not exist.
        ''' </summary>
        Public Sub EnsureCreated()
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

                        CREATE INDEX IF NOT EXISTS IX_CrashEvents_Instance
                            ON CrashEvents(InstanceId, Timestamp);
                    "
                    cmd.ExecuteNonQuery()
                End Using
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
        ''' node restarts.
        ''' </summary>
        Public Sub SaveInstanceSnapshot(instanceId As String,
                                        state As String,
                                        pid As Integer,
                                        startedAtUtc As DateTime,
                                        crashPolicyJson As String,
                                        stopIntentPending As Boolean)
            Using conn = OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "INSERT OR REPLACE INTO InstanceSnapshots
                        (InstanceId, State, Pid, StartedAtUtc, CrashPolicyJson, StopIntentPending)
                        VALUES (@id, @state, @pid, @started, @policy, @intent)"
                    cmd.Parameters.AddWithValue("@id", instanceId)
                    cmd.Parameters.AddWithValue("@state", state)
                    cmd.Parameters.AddWithValue("@pid", pid)
                    cmd.Parameters.AddWithValue("@started", startedAtUtc.ToString("o"))
                    cmd.Parameters.AddWithValue("@policy", If(crashPolicyJson, CObj(DBNull.Value)))
                    cmd.Parameters.AddWithValue("@intent", If(stopIntentPending, 1, 0))
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

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

    End Class

End Namespace
