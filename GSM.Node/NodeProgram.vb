Imports System
Imports System.IO
Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Hosting
Imports Microsoft.AspNetCore.Http
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Data.Sqlite

' ============================================================
'  GSM.Node — Entry point and infrastructure
' ============================================================

Namespace GSM.Node

    ''' <summary>
    ''' Entry point. Configures the ASP.NET Core Minimal API host,
    ''' registers services, wires middleware, maps endpoints.
    ''' </summary>
    Module NodeProgram

        Sub Main(args As String())

            Dim builder = WebApplication.CreateBuilder(args)

            ' Load nodesettings.json
            builder.Configuration.AddJsonFile("nodesettings.json",
                                              optional:=False,
                                              reloadOnChange:=True)

            ' Bind configuration
            Dim nodeConfig As New NodeConfiguration()
            builder.Configuration.GetSection("Node").Bind(nodeConfig)
            nodeConfig.EnsureDefaults()
            builder.Services.AddSingleton(nodeConfig)

            ' Support running as Windows Service or systemd unit
            builder.Host.UseWindowsService()
            builder.Host.UseSystemd()

            ' Register core services
            Dim db As New NodeDatabase(nodeConfig.DataDirectory)
            db.EnsureCreated()
            builder.Services.AddSingleton(db)
            builder.Services.AddSingleton(Of ProcessManager)()
            builder.Services.AddSingleton(Of RingBufferStore)()
            builder.Services.AddSingleton(Of RconClientManager)()
            builder.Services.AddSingleton(Of InstallRunner)()

            ' Configure Kestrel to listen on configured port
            builder.WebHost.ConfigureKestrel(Sub(options)
                                                 options.ListenAnyIP(nodeConfig.ListenPort)
                                             End Sub)

            Dim app = builder.Build()

            ' ---- Auth middleware ----
            app.Use(Async Function(context, nextDelegate)
                        ' Skip auth for version endpoint
                        If context.Request.Path.StartsWithSegments("/api/version") Then
                            Await nextDelegate()
                            Return
                        End If

                        Dim cfg = context.RequestServices.GetRequiredService(Of NodeConfiguration)()
                        Dim authHeader = context.Request.Headers("Authorization").ToString()

                        If String.IsNullOrEmpty(authHeader) OrElse
                           Not authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) Then
                            context.Response.StatusCode = 401
                            Await context.Response.WriteAsJsonAsync(New With {
                                .error = "Missing or invalid Authorization header"
                            })
                            Return
                        End If

                        Dim token = authHeader.Substring(7).Trim()
                        If Not String.Equals(token, cfg.AuthToken, StringComparison.Ordinal) Then
                            context.Response.StatusCode = 403
                            Await context.Response.WriteAsJsonAsync(New With {
                                .error = "Invalid auth token"
                            })
                            Return
                        End If

                        Await nextDelegate()
                    End Function)

            ' ---- Map endpoints ----
            Endpoints.SystemEndpoints.Map(app)
            Endpoints.InstanceEndpoints.Map(app)
            Endpoints.InstallEndpoints.Map(app)

            app.Run()

        End Sub

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

        Public Sub EnsureDefaults()
            If String.IsNullOrEmpty(NodeId) Then
                NodeId = Environment.MachineName
            End If
            If String.IsNullOrEmpty(DataDirectory) Then
                DataDirectory = "./data"
            End If
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
