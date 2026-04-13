Imports System.Collections.Generic
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports GSM.Data
Imports GSM.Node.Api
Imports GSM.Plugin

' ============================================================
'  InstanceManager
'
'  The central coordinator in the manager process.
'  Callers (the UI, automation engine, Discord plugin) talk
'  to this class and never touch nodes or plugins directly.
'
'  Responsibilities:
'    - Build StartInstanceRequests by combining:
'        · Plugin-resolved exe path and command line
'        · Decrypted realm credentials merged into config
'        · Crash restart policy from the instance record
'        · RCON config from the plugin
'        · Log source config from the plugin
'        · Startup warnings from the plugin
'    - Send commands to the right node via INodeClient
'    - Keep the manager DB in sync with node-reported state
'    - Act as ILogParserCoordinator for the PluginRegistry
'      (drain parsers before hot-reload, register new parsers)
'    - Poll nodes for metrics on a background timer
'
'  What InstanceManager does NOT do:
'    - Execute automation rules (that's AutomationEngine)
'    - Send notifications (that's NotificationService)
'    - Manage installations (that's InstallationManager,
'      a sibling class not yet written)
'
'  EF Core note (since you're new to it):
'    Each async method that touches the DB creates its own
'    short-lived GsmDbContext via IDbContextFactory.
'    This is the recommended pattern for non-web apps where
'    you can't use the scoped lifetime that ASP.NET Core provides.
'    Think of it as: one context per logical operation, then
'    dispose it. Never hold a context open across awaits.
' ============================================================

Namespace GSM.Core

    Public Class InstanceManager
        Implements ILogParserCoordinator

        Private ReadOnly _dbFactory As IDbContextFactory(Of GsmDbContext)
        Private ReadOnly _pluginRegistry As PluginRegistry
        Private ReadOnly _nodeClientFactory As NodeHttpClientFactory
        Private ReadOnly _credentials As CredentialService
        Private ReadOnly _logger As ILogger(Of InstanceManager)

        ' Live log parser state. Key = InstanceId.
        ' Protected by _parserLock.
        Private ReadOnly _parsers As New Dictionary(Of String, ActiveLogParser)(
            StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _parserLock As New SemaphoreSlim(1, 1)

        ' Background metrics polling.
        Private _metricsPollTimer As System.Threading.Timer
        Private ReadOnly _metricsPollLock As New SemaphoreSlim(1, 1)

        ' Event raised when an instance's state changes.
        ' The UI subscribes to this to refresh without polling.
        Public Event InstanceStateChanged(instanceId As String,
                                           newState As InstanceState,
                                           reason As String)

        Public Sub New(dbFactory As IDbContextFactory(Of GsmDbContext),
                       pluginRegistry As PluginRegistry,
                       nodeClientFactory As NodeHttpClientFactory,
                       credentials As CredentialService,
                       logger As ILogger(Of InstanceManager))
            _dbFactory = dbFactory
            _pluginRegistry = pluginRegistry
            _nodeClientFactory = nodeClientFactory
            _credentials = credentials
            _logger = logger
        End Sub


        ' ============================================================
        '  STARTUP
        ' ============================================================

        Public Sub StartMetricsPoll(intervalSeconds As Integer)
            ' Poll all nodes for metrics every N seconds.
            ' Timer fires on a thread pool thread.
            Dim interval = TimeSpan.FromSeconds(intervalSeconds)
            _metricsPollTimer = New System.Threading.Timer(
                AddressOf MetricsPollCallback, Nothing,
                interval, interval)
            _logger.LogInformation(
                "InstanceManager: metrics poll started ({Interval}s)", intervalSeconds)
        End Sub

        Private Sub MetricsPollCallback(state As Object)
            ' Fire-and-forget but log exceptions.
            Task.Run(Async Function()
                         Try
                             Await PollAllNodesAsync(CancellationToken.None)
                         Catch ex As Exception
                             _logger.LogError(ex, "InstanceManager: metrics poll error")
                         End Try
                     End Function)
        End Sub

        Private Async Function PollAllNodesAsync(
                cancellation As CancellationToken) As Task

            If Not _metricsPollLock.Wait(0) Then Return  ' Skip if still polling
            Try
                Using db = _dbFactory.CreateDbContext()
                    Dim instances = Await db.Instances.
                        Include(Function(i) i.Installation).
                        Where(Function(i) i.IsEnabled).
                        ToListAsync(cancellation)

                    ' Group by node to batch per-node requests.
                    Dim byNode = instances.GroupBy(
                        Function(i) i.Installation.NodeId)

                    For Each nodeGroup In byNode
                        Try
                            Dim client = Await _nodeClientFactory.GetClientAsync(
                                nodeGroup.Key, cancellation)

                            For Each instance In nodeGroup
                                Try
                                    Dim metrics = Await client.GetMetricsAsync(
                                        instance.InstanceId, cancellation)

                                    Await UpdateCachedStateAsync(
                                        instance.InstanceId,
                                        metrics.State,
                                        metrics.RconState,
                                        metrics.PlayerCount,
                                        cancellation)

                                    ' Forward parser output from metrics.
                                    If metrics.Players IsNot Nothing Then
                                        Await _parserLock.WaitAsync(cancellation)
                                        Try
                                            Dim parser As ActiveLogParser = Nothing
                                            If _parsers.TryGetValue(
                                                    instance.InstanceId, parser) Then
                                                ' Update player list from metrics response.
                                                ' The node's log parser runs locally -
                                                ' we receive its output via the metrics poll.
                                                ' (In production this will be pushed via SSE.)
                                            End If
                                        Finally
                                            _parserLock.Release()
                                        End Try
                                    End If

                                Catch ex As NodeConnectionException
                                    _logger.LogWarning(
                                        "InstanceManager: node {NodeId} unreachable: {Msg}",
                                        nodeGroup.Key, ex.Message)
                                Catch ex As NodeApiException
                                    ' Instance not found on node yet - normal during startup.
                                    If ex.ErrorCode <> NodeErrorCodes.InstanceNotFound Then
                                        _logger.LogWarning(
                                            "InstanceManager: metrics error for {Id}: {Msg}",
                                            instance.InstanceId, ex.Message)
                                    End If
                                End Try
                            Next

                        Catch ex As NodeConnectionException
                            _logger.LogWarning(
                                "InstanceManager: cannot reach node {NodeId}", nodeGroup.Key)
                        End Try
                    Next
                End Using
            Finally
                _metricsPollLock.Release()
            End Try
        End Function


        ' ============================================================
        '  INSTANCE LIFECYCLE
        '  The core operations the UI and automation engine call.
        ' ============================================================

        ' Start an instance.
        ' This method:
        '   1. Loads the instance and installation from the DB
        '   2. Resolves the plugin for this game
        '   3. Decrypts and merges realm credentials into the config
        '   4. Calls the plugin to resolve exe path and command line
        '   5. Builds the StartInstanceRequest with all resolved data
        '   6. Sends it to the node
        Public Async Function StartInstanceAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of StartInstanceResponse)

            Using db = _dbFactory.CreateDbContext()

                ' Load instance with all related data we'll need.
                ' .Include() tells EF to also load the related Installation
                ' and the Installation's Node. Without Include, those
                ' navigation properties would be Nothing (null).
                Dim instance = Await db.Instances.
                    Include(Function(i) i.Installation).
                        ThenInclude(Function(inst) inst.Node).
                    Include(Function(i) i.RealmCredential).
                    Include(Function(i) i.Installation.RealmCredential).
                    Include(Function(i) i.Installation.SteamCredential).
                    FirstOrDefaultAsync(Function(i) i.InstanceId = instanceId, cancellation)

                If instance Is Nothing Then
                    Throw New InvalidOperationException(
                        $"Instance '{instanceId}' not found.")
                End If

                If Not instance.IsEnabled Then
                    Throw New InvalidOperationException(
                        $"Instance '{instance.DisplayName}' is disabled.")
                End If

                ' Resolve the plugin.
                Dim plugin = _pluginRegistry.GetPlugin(instance.GameId)
                If plugin Is Nothing Then
                    Throw New InvalidOperationException(
                        $"No plugin loaded for game '{instance.GameId}'. " &
                        "Check that the plugin file is in the plugins\ directory " &
                        "and has been loaded via Reload Plugins.")
                End If

                ' Resolve realm credentials.
                ' Instance credential overrides installation credential.
                Dim realmCred = If(instance.RealmCredential,
                                    instance.Installation.RealmCredential)

                Dim customerKey = String.Empty
                Dim providerKey = String.Empty
                If realmCred IsNot Nothing Then
                    Dim keys = _credentials.DecryptRealmCredential(realmCred)
                    customerKey = keys.CustomerKey
                    providerKey = keys.ProviderKey
                End If

                ' Merge credentials into the plugin config JSON.
                ' The plugin's typed config class has CustomerKey and ProviderKey
                ' properties that BuildCommandLine expects to find pre-populated.
                Dim mergedConfig = MergeCredentialsIntoConfig(
                    instance.PluginConfig,
                    customerKey,
                    providerKey)

                ' Build an InstanceConfig to pass to the plugin.
                Dim instanceConfig As New InstanceConfig With {
                    .GameId = instance.GameId,
                    .InstanceId = instance.InstanceId,
                    .DisplayName = instance.DisplayName,
                    .ExeOverride = instance.ExeOverride,
                    .RawJson = mergedConfig
                }

                Dim installPath = instance.Installation.InstallPath

                ' Ask the plugin to resolve exe path and command line.
                Dim exePath As String
                Dim commandLine As String
                Dim workingDir As String
                Dim logSources As IReadOnlyList(Of ILogSource)
                Dim rconInfo As RconInfo
                Dim startupWarnings As IReadOnlyList(Of String)

                Try
                    exePath = plugin.GetExecutablePath(installPath, instanceConfig)
                    commandLine = plugin.BuildCommandLine(instanceConfig)
                    workingDir = plugin.GetWorkingDirectory(installPath, instanceConfig)
                    logSources = plugin.GetLogSources(installPath, instanceConfig)
                    rconInfo = plugin.GetRconInfo(instanceConfig)
                    startupWarnings = plugin.GetStartupWarnings(installPath, instanceConfig)
                Catch ex As Exception
                    Dim errMsg = "Plugin error while preparing instance " & instance.DisplayName & ": " & ex.Message
                    Throw New InvalidOperationException(errMsg, ex)
                End Try

                ' Deserialise crash restart policy.
                Dim policy = If(String.IsNullOrEmpty(instance.CrashRestartPolicy) OrElse
                                instance.CrashRestartPolicy = "{}",
                                New CrashRestartPolicy(),
                                JsonSerializer.Deserialize(Of CrashRestartPolicy)(
                                    instance.CrashRestartPolicy))

                ' Convert ILogSource list to serialisable DTOs.
                Dim logSourceDtos = logSources.Select(Function(s)
                    If TypeOf s Is StdoutLogSource Then
                        Dim src = CType(s, StdoutLogSource)
                        Return New LogSourceConfig With {
                            .SourceId = src.SourceId,
                            .SourceType = LogSourceType.Stdout,
                            .CaptureStderr = src.CaptureStderr
                        }
                    ElseIf TypeOf s Is FileLogSource Then
                        Dim src = CType(s, FileLogSource)
                        Return New LogSourceConfig With {
                            .SourceId = src.SourceId,
                            .SourceType = LogSourceType.File,
                            .PathPattern = src.PathPattern,
                            .FollowRotation = src.FollowRotation
                        }
                    End If
                    Return Nothing
                End Function).Where(Function(s) s IsNot Nothing).ToList()

                ' Build RCON config DTO.
                Dim rconConfig As NodeRconConfig = Nothing
                If rconInfo IsNot Nothing Then
                    rconConfig = New NodeRconConfig With {
                        .Protocol = rconInfo.Protocol,
                        .Port = rconInfo.Port,
                        .Password = rconInfo.Password,
                        .AutoConnect = rconInfo.AutoConnect,
                        .StartupDelayMs = rconInfo.StartupDelayMs,
                        .MaxConnectRetries = rconInfo.MaxConnectRetries,
                        .RetryIntervalMs = rconInfo.RetryIntervalMs,
                        .ConnectTimeoutMs = rconInfo.ConnectTimeoutMs,
                        .MaxPacketSize = rconInfo.MaxPacketSize
                    }
                End If

                ' Build the request.
                Dim request As New StartInstanceRequest With {
                    .InstanceId = instanceId,
                    .DisplayName = instance.DisplayName,
                    .GameId = instance.GameId,
                    .InstallationId = instance.InstallationId,
                    .ExecutablePath = exePath,
                    .Arguments = commandLine,
                    .WorkingDirectory = workingDir,
                    .LogSources = logSourceDtos,
                    .CrashRestartPolicy = policy,
                    .RconConfig = rconConfig,
                    .CrashSignalPatterns = plugin.GetCrashSignalPatterns().ToList(),
                    .CleanExitCodes = plugin.GetCleanExitCodes().ToList(),
                    .StartupWarnings = startupWarnings.ToList()
                }

                ' Log startup warnings before sending.
                For Each warning In startupWarnings
                    _logger.LogWarning(
                        "InstanceManager [{Name}]: {Warning}",
                        instance.DisplayName, warning)
                Next

                ' Send to the node.
                Dim client = Await _nodeClientFactory.GetClientAsync(
                    instance.Installation.NodeId, cancellation)

                Dim response = Await client.StartInstanceAsync(request, cancellation)

                ' Update cached state in DB.
                Await UpdateCachedStateAsync(instanceId,
                    response.State, RconState.NotAvailable, 0, cancellation)

                _logger.LogInformation(
                    "InstanceManager: started '{Name}' → {State}",
                    instance.DisplayName, response.State)

                Return response
            End Using
        End Function

        Public Async Function StopInstanceAsync(
                instanceId As String,
                graceful As Boolean,
                cancellation As CancellationToken) As Task(Of StopInstanceResponse)

            Dim nodeId = Await GetNodeIdForInstanceAsync(instanceId, cancellation)
            Dim client = Await _nodeClientFactory.GetClientAsync(nodeId, cancellation)
            Dim response = Await client.StopInstanceAsync(instanceId,
                New StopInstanceRequest With {.Graceful = graceful},
                cancellation)
            Await UpdateCachedStateAsync(instanceId,
                response.State, RconState.NotAvailable, 0, cancellation)
            Return response
        End Function

        Public Async Function RestartInstanceAsync(
                instanceId As String,
                graceful As Boolean,
                cancellation As CancellationToken) As Task(Of RestartInstanceResponse)

            Dim nodeId = Await GetNodeIdForInstanceAsync(instanceId, cancellation)
            Dim client = Await _nodeClientFactory.GetClientAsync(nodeId, cancellation)
            Return Await client.RestartInstanceAsync(instanceId,
                New RestartInstanceRequest With {.Graceful = graceful},
                cancellation)
        End Function

        Public Async Function SendRconCommandAsync(
                instanceId As String,
                command As String,
                cancellation As CancellationToken) As Task(Of RconSendResponse)

            Dim nodeId = Await GetNodeIdForInstanceAsync(instanceId, cancellation)
            Dim client = Await _nodeClientFactory.GetClientAsync(nodeId, cancellation)
            Return Await client.SendRconAsync(instanceId,
                New RconSendRequest With {.Command = command},
                cancellation)
        End Function

        Public Async Function GetMetricsAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of InstanceMetricsResponse)

            Dim nodeId = Await GetNodeIdForInstanceAsync(instanceId, cancellation)
            Dim client = Await _nodeClientFactory.GetClientAsync(nodeId, cancellation)
            Return Await client.GetMetricsAsync(instanceId, cancellation)
        End Function

        Public Async Function GetPlayerCountAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of Integer)

            Try
                Dim metrics = Await GetMetricsAsync(instanceId, cancellation)
                Return metrics.PlayerCount
            Catch
                Return 0
            End Try
        End Function

        ' Fetch recent buffered log lines for an instance.
        ' Called by LogViewerForm on initial load to populate history.
        Public Async Function GetLogsAsync(
                instanceId As String,
                count As Integer,
                cancellation As CancellationToken) As Task(Of InstanceLogsResponse)

            Dim nodeId = Await GetNodeIdForInstanceAsync(instanceId, cancellation)
            Dim client = Await _nodeClientFactory.GetClientAsync(nodeId, cancellation)
            Return Await client.GetLogsAsync(instanceId, count, "", cancellation)
        End Function

        ' Stream live log lines from the node's SSE endpoint.
        ' Returns IAsyncEnumerable - iterate with Await For Each.
        ' The stream stays open until the CancellationToken fires
        ' (which happens when LogViewerForm is closed).
        '
        ' Usage in LogViewerForm:
        '   Await _instanceManager.StreamLogsAsync(id, fromIndex,
        '       Sub(line) AppendLine(line.SourceId, line.Timestamp, line.Content), ct)
        Public Async Function StreamLogsAsync(
                instanceId As String,
                fromIndex As Long,
                onLine As Action(Of LogLine),
                cancellation As CancellationToken) As Task

            Dim nodeId = Await GetNodeIdForInstanceAsync(instanceId, cancellation)
            Dim client = Await _nodeClientFactory.GetClientAsync(nodeId, cancellation)

            Await client.StreamLogsAsync(instanceId, fromIndex, "", onLine, cancellation)
        End Function

        ' Tells the node to clear the CrashLoopHalted state for an instance
        ' and schedule a new restart attempt. Called by the automation engine's
        ' ResumeCrashLoopAction and by the UI "Resume" button.
        '
        ' The node clears the crash count in its sliding window and
        ' re-enters the Restarting state, which triggers a fresh launch.
        Public Async Function ResumeCrashRetriesAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task

            ' The node doesn't have a dedicated /resume endpoint yet -
            ' we send a start request which the node handles by resetting
            ' the crash loop state and attempting a new launch using the
            ' last known start parameters persisted in its local SQLite.
            '
            ' This is safe because:
            '   - If the instance is in CrashLoopHalted the node will accept
            '     the start request and reset its attempt counter.
            '   - If the instance is already running the node returns
            '     InstanceAlreadyRunning which we treat as success.
            Try
                Await StartInstanceAsync(instanceId, cancellation)
                _logger.LogInformation(
                    "InstanceManager: resume crash retries requested for {Id}",
                    instanceId)
            Catch ex As NodeApiException _
                    When ex.ErrorCode = NodeErrorCodes.InstanceAlreadyRunning
                ' Already running - nothing to do.
            End Try
        End Function


        ' ============================================================
        '  ILOGPARSERCOORDINATOR IMPLEMENTATION
        '  Called by PluginRegistry during hot-reload.
        ' ============================================================

        ' Drain all active parsers before the plugin swap.
        ' Returns a checkpoint per instance so the new parsers
        ' can replay from where the old ones left off.
        Public Async Function DrainAllParsersAsync(
                cancellation As CancellationToken) As Task(Of IReadOnlyDictionary(Of String, LogParserCheckpoint)) _
                Implements ILogParserCoordinator.DrainAllParsersAsync

            Await _parserLock.WaitAsync(cancellation)
            Try
                Dim checkpoints As New Dictionary(Of String, LogParserCheckpoint)(
                    StringComparer.OrdinalIgnoreCase)

                For Each kvp In _parsers
                    ' Signal the parser to stop receiving new lines.
                    kvp.Value.IsDraining = True

                    ' Wait briefly for any in-flight ProcessLine calls to complete.
                    Await Task.Delay(100, cancellation)

                    checkpoints(kvp.Key) = New LogParserCheckpoint With {
                        .InstanceId = kvp.Key,
                        .GameId = kvp.Value.GameId,
                        .LineIndex = kvp.Value.LastProcessedLineIndex
                    }
                Next

                ' Clear the old parsers.
                _parsers.Clear()

                _logger.LogInformation(
                    "InstanceManager: drained {Count} log parsers for hot-reload",
                    checkpoints.Count)

                Return checkpoints
            Finally
                _parserLock.Release()
            End Try
        End Function

        ' Register a new parser after the plugin swap.
        Public Async Function RegisterParserAsync(
                instanceId As String,
                gameId As String,
                parser As ILogParser,
                cancellation As CancellationToken) As Task _
                Implements ILogParserCoordinator.RegisterParserAsync

            Await _parserLock.WaitAsync(cancellation)
            Try
                _parsers(instanceId) = New ActiveLogParser With {
                    .InstanceId = instanceId,
                    .GameId = gameId,
                    .Parser = parser,
                    .LastProcessedLineIndex = 0,
                    .IsDraining = False
                }
                _logger.LogDebug(
                    "InstanceManager: registered log parser for {Id}", instanceId)
            Finally
                _parserLock.Release()
            End Try
        End Function

        ' Called by the SSE log stream consumer (or metrics poller)
        ' when a new log line arrives. Feeds it to the active parser.
        Public Async Function ProcessLogLineAsync(instanceId As String,
                                                   sourceId As String,
                                                   timestamp As DateTime,
                                                   content As String) As Task

            Await _parserLock.WaitAsync()
            Try
                Dim active As ActiveLogParser = Nothing
                If Not _parsers.TryGetValue(instanceId, active) OrElse
                   active.IsDraining Then Return

                active.Parser.ProcessLine(sourceId, timestamp, content)
                active.LastProcessedLineIndex += 1
            Finally
                _parserLock.Release()
            End Try
        End Function


        ' ============================================================
        '  PRIVATE HELPERS
        ' ============================================================

        ' Merge CustomerKey and ProviderKey into the instance config JSON.
        ' The plugin's BuildCommandLine expects these fields to be present.
        Private Shared Function MergeCredentialsIntoConfig(
                pluginConfigJson As String,
                customerKey As String,
                providerKey As String) As String

            Dim config As Dictionary(Of String, Object)
            Try
                config = JsonSerializer.Deserialize(Of Dictionary(Of String, Object))(
                    If(String.IsNullOrEmpty(pluginConfigJson), "{}", pluginConfigJson))
            Catch
                config = New Dictionary(Of String, Object)()
            End Try

            ' Overwrite with the decrypted values.
            config("CustomerKey") = customerKey
            config("ProviderKey") = providerKey

            Return JsonSerializer.Serialize(config)
        End Function

        ' Look up which node hosts a given instance.
        ' Used by stop/restart/metrics methods which don't need
        ' to load the full instance record.
        Private Async Function GetNodeIdForInstanceAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of String)

            Using db = _dbFactory.CreateDbContext()
                ' We only need the NodeId, so select just that field.
                ' This is more efficient than loading the whole entity.
                Dim nodeId = Await db.Instances.
                    Where(Function(i) i.InstanceId = instanceId).
                    Select(Function(i) i.Installation.NodeId).
                    FirstOrDefaultAsync(cancellation)

                If nodeId Is Nothing Then
                    Throw New InvalidOperationException(
                        $"Instance '{instanceId}' not found.")
                End If

                Return nodeId
            End Using
        End Function

        ' Update the cached state in the manager DB after a node reports
        ' a state change. The node is authoritative; this is just a cache
        ' so the UI can display something without hitting the node.
        Private Async Function UpdateCachedStateAsync(
                instanceId As String,
                state As InstanceState,
                rconState As RconState,
                playerCount As Integer,
                cancellation As CancellationToken) As Task

            Using db = _dbFactory.CreateDbContext()
                Dim instance = Await db.Instances.FindAsync(
                    New Object() {instanceId}, cancellation)
                If instance Is Nothing Then Return

                Dim oldState = instance.LastKnownState
                instance.LastKnownState = state.ToString()
                instance.LastKnownRconState = rconState.ToString()
                instance.LastKnownPlayerCount = playerCount
                instance.LastStateReportAt = DateTime.UtcNow

                Await db.SaveChangesAsync(cancellation)

                ' Fire event if state changed.
                If oldState <> state.ToString() Then
                    RaiseEvent InstanceStateChanged(instanceId, state,
                        $"Reported by node (was {oldState})")
                End If
            End Using
        End Function

    End Class


    ' ============================================================
    '  ACTIVE LOG PARSER
    '  Tracks one live parser and its position in the ring buffer.
    ' ============================================================

    Friend Class ActiveLogParser
        Public Property InstanceId As String
        Public Property GameId As String
        Public Property Parser As ILogParser
        Public Property LastProcessedLineIndex As Long
        Public Property IsDraining As Boolean
    End Class

End Namespace
