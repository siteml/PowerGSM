Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node.Api
Imports GSM.Manager
Imports GSM.Manager.Data

' ============================================================
'  InstanceManager — orchestrates instance lifecycle
'
'  Bridges the gap between the database (what's configured)
'  and the nodes (what's running). All instance operations
'  go through this class.
'
'  Responsibilities:
'    - Start/stop/restart instances via NodeHttpClient
'    - Track live state per instance
'    - Coordinate log streaming from nodes to manager buffer
'    - Resolve plugin config into plain DTOs for the node
' ============================================================

Namespace GSM.Manager.Core

    Public Class InstanceManager

        Private ReadOnly _clientFactory As NodeHttpClientFactory
        Private ReadOnly _pluginRegistry As PluginRegistry
        Private ReadOnly _credentialService As CredentialService
        Private ReadOnly _emitter As NotificationEmitter
        Private ReadOnly _logger As ILogger(Of InstanceManager)
        Private ReadOnly _logParsers As New ConcurrentDictionary(Of String, ActiveLogParser)
        Private ReadOnly _logStreamCancellations As New ConcurrentDictionary(Of String, CancellationTokenSource)
        Private ReadOnly _liveStates As New ConcurrentDictionary(Of String, InstanceStatusResponse)

        ' Per-instance gate so that concurrent RefreshInstanceStateAsync
        ' calls (the UI panel + the background poller) can't both
        ' observe the same Running->Crashed transition and double-fire
        ' the crash notification.
        Private ReadOnly _refreshLocks As New ConcurrentDictionary(Of String, SemaphoreSlim)

        ' Background poller — iterates every known instance every few
        ' seconds so crash/crashloop state transitions get detected
        ' regardless of which UI tab the user has focused. Previously
        ' the ONLY caller of RefreshInstanceStateAsync in normal
        ' operation was the instance-detail panel's timer, so crashes
        ' on backgrounded instances silently dropped their notifications.
        Private _pollingCts As CancellationTokenSource
        Private _pollingTask As Task
        Private Const BackgroundPollIntervalMs As Integer = 3000

        ' ----------------------------------------------------------------
        ' Manager-side player tracking for notification dedup.
        '
        ' The log stream produces join/leave verdicts via the game
        ' plugin's ILogParser, but that output is noisy for two reasons:
        '   1. UE4 fires BOTH UChannel::Close AND UNetConnection::Close
        '      on a real disconnect, and the LastOasis parser matches
        '      either one. Net result: two "player left" events per
        '      actual leave.
        '   2. UE4 also fires those same log lines for server-internal
        '      channels (EOS auth, backend telemetry) that aren't
        '      player connections at all, so we get spurious leaves
        '      with no matching join.
        '
        ' On top of that, when a log stream reconnects or the node's
        ' ring buffer replays a tail, previously-seen join/leave lines
        ' come through the parser again and would refire notifications.
        '
        ' We solve all three by gating notifications on an actual state
        ' transition: only emit PlayerJoined if the name wasn't already
        ' in the active set, only emit PlayerLeft if the name was in
        ' the set. Nameless leaves (UE4's typical case) fall back to a
        ' debounce + "one player online means it was that player"
        ' heuristic.
        ' ----------------------------------------------------------------
        Private ReadOnly _activePlayers As _
            New ConcurrentDictionary(Of String, HashSet(Of String))
        Private ReadOnly _lastEmptyLeaveAt As _
            New ConcurrentDictionary(Of String, DateTime)
        Private Const EmptyLeaveCooldownMs As Integer = 2000

        ' Round C — chat mirror state.
        ' The chat-mirror poller tracks a per-instance cursor (most
        ' recent TimestampUtc persisted) so each poll fetches only
        ' the delta from the node. Cursor is seeded from max(ChatMessages.TimestampUtc)
        ' for that instance on first run after a manager restart,
        ' so we don't re-ingest history the node's still holding.
        Private ReadOnly _chatCursors As New ConcurrentDictionary(Of String, DateTime)
        Private Const ChatMirrorIntervalMs As Integer = 5000
        Private Const ChatMirrorBatchLimit As Integer = 500

        ' Restart coordinator handle — late-bound by ManagerProgram
        ' after both singletons exist, to break the construction
        ' cycle documented on RestartCoordinator itself. Nothing
        ' until attached; all usage sites null-check.
        Private _restartCoordinator As RestartCoordinator

        Public Sub New(clientFactory As NodeHttpClientFactory,
                       pluginRegistry As PluginRegistry,
                       credentialService As CredentialService,
                       emitter As NotificationEmitter,
                       logger As ILogger(Of InstanceManager))
            _clientFactory = clientFactory
            _pluginRegistry = pluginRegistry
            _credentialService = credentialService
            _emitter = emitter
            _logger = logger
        End Sub

        ''' <summary>
        ''' Late-bound setter used by ManagerProgram to connect
        ''' the restart coordinator after both singletons are
        ''' resolved. See RestartCoordinator.AttachInstanceManager
        ''' for the rationale (mutual-dependency construction cycle).
        ''' </summary>
        Public Sub AttachRestartCoordinator(coordinator As RestartCoordinator)
            _restartCoordinator = coordinator
        End Sub

        ' ============================================================
        '  Instance lifecycle
        ' ============================================================

        ''' <summary>
        ''' Starts an instance on its node. Resolves plugin config,
        ''' builds launch arguments, sends StartInstanceRequest.
        ''' </summary>
        Public Async Function StartInstanceAsync(instanceId As String) As Task(Of Boolean)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(instanceId)
                If instanceEntity Is Nothing Then
                    _logger.LogError("Instance {Id} not found", instanceId)
                    Return False
                End If

                Dim installEntity = db.Installations.Find(instanceEntity.InstallationId)
                If installEntity Is Nothing Then
                    _logger.LogError("Installation {Id} not found for instance {Inst}",
                                     instanceEntity.InstallationId, instanceId)
                    Return False
                End If

                Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                If nodeEntity Is Nothing Then
                    _logger.LogError("Node {Id} not found", installEntity.NodeId)
                    Return False
                End If

                ' Resolve plugin
                Dim plugin = _pluginRegistry.GetPlugin(instanceEntity.GameId)

                ' Merge installation-level config (shared — e.g. realm credentials)
                ' with instance-level config (per-server — e.g. port, identifier).
                ' Instance values win on key collision so overrides work —
                ' BUT empty strings at instance level do NOT override non-empty
                ' install-level values, otherwise a blank override field in
                ' the Edit Instance form would wipe the shared install value.
                Dim installFields = DeserializeConfig(installEntity.ConfigJson)
                Dim instanceFields = DeserializeConfig(instanceEntity.ConfigJson)
                Dim customFields As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                For Each kvp In installFields
                    customFields(kvp.Key) = kvp.Value
                Next
                For Each kvp In instanceFields
                    If String.IsNullOrEmpty(kvp.Value) AndAlso
                       customFields.ContainsKey(kvp.Key) AndAlso
                       Not String.IsNullOrEmpty(customFields(kvp.Key)) Then
                        ' Instance has a blank value but install has one — keep install's.
                        Continue For
                    End If
                    customFields(kvp.Key) = kvp.Value
                Next

                ' Build instance config
                Dim instanceConfig As New InstanceConfig With {
                    .InstanceId = instanceId,
                    .GameId = instanceEntity.GameId,
                    .DisplayName = instanceEntity.DisplayName,
                    .InstallationId = instanceEntity.InstallationId,
                    .WorkingDirectory = installEntity.InstallPath,
                    .CustomFields = customFields
                }

                ' Build launch arguments from plugin
                Dim launchArgs = ""
                If plugin IsNot Nothing Then
                    Try
                        launchArgs = plugin.BuildLaunchArguments(instanceConfig)
                    Catch ex As Exception
                        _logger.LogWarning(ex, "Plugin failed to build launch arguments for {Id}", instanceId)
                    End Try
                End If

                ' Resolve executable candidates — ExeOverride wins,
                ' then plugin's candidate list, then InstanceConfig.ExePath
                Dim candidates As New List(Of String)
                If Not String.IsNullOrEmpty(instanceEntity.ExeOverride) Then
                    candidates.Add(instanceEntity.ExeOverride)
                End If
                If plugin IsNot Nothing Then
                    Try
                        Dim pluginCandidates = plugin.GetExecutablePath(instanceConfig)
                        If pluginCandidates IsNot Nothing Then
                            For Each relPath In pluginCandidates
                                If Not String.IsNullOrEmpty(relPath) Then
                                    candidates.Add(IO.Path.Combine(installEntity.InstallPath, relPath))
                                End If
                            Next
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex, "Plugin failed to report executable paths for {Id}", instanceId)
                    End Try
                End If
                If Not String.IsNullOrEmpty(instanceConfig.ExePath) Then
                    candidates.Add(instanceConfig.ExePath)
                End If

                If candidates.Count = 0 Then
                    _logger.LogError("No executable candidates available for instance {Id}", instanceId)
                    Return False
                End If

                ' RCON settings
                Dim rconPort As Integer? = Nothing
                Dim rconPassword = ""
                Dim rconProtocol = GSM.Plugin.RconProtocol.SourceRcon
                If plugin IsNot Nothing Then
                    Dim rp = plugin.GetRconProtocol()
                    If rp.HasValue Then rconProtocol = rp.Value
                End If
                If customFields.ContainsKey("RconPort") Then
                    Dim portVal As Integer = 0
                    If Integer.TryParse(customFields("RconPort"), portVal) Then
                        rconPort = portVal
                    End If
                End If
                If customFields.ContainsKey("RconPassword") Then
                    rconPassword = customFields("RconPassword")
                End If

                Dim client = _clientFactory.GetClient(
                    nodeEntity.NodeId, nodeEntity.HostAddress,
                    nodeEntity.Port, nodeEntity.AuthToken)

                ' Resolve file log sources the plugin declared. These
                ' get tailed on the node and merged into the instance's
                ' log buffer. Path patterns can contain {InstallPath}
                ' and {InstanceId} tokens. After token replacement, any
                ' resolved path that still isn't rooted (no drive
                ' letter, no leading separator) is treated as relative
                ' to the installation directory — so plugins that just
                ' write the file name ("factorio-current.log") get the
                ' right answer without having to remember to prefix
                ' {InstallPath} every time.
                Dim logFilePaths As New List(Of String)
                If plugin IsNot Nothing Then
                    Try
                        Dim sources = plugin.GetLogSources(instanceConfig)
                        If sources IsNot Nothing Then
                            For Each src In sources
                                Dim fileSrc = TryCast(src, FileLogSource)
                                If fileSrc Is Nothing Then Continue For
                                Dim resolved = fileSrc.PathPattern _
                                    .Replace("{InstallPath}", installEntity.InstallPath) _
                                    .Replace("{InstanceId}", instanceId)
                                If Not String.IsNullOrEmpty(resolved) AndAlso
                                   Not IO.Path.IsPathRooted(resolved) Then
                                    resolved = IO.Path.Combine(installEntity.InstallPath, resolved)
                                End If
                                logFilePaths.Add(resolved)
                            Next
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex, "Plugin failed to report log sources for {Id}", instanceId)
                    End Try
                End If

                ' Collect declarative parse rules from plugin. The node
                ' applies these to every log line so it can track players
                ' and server state without the plugin loaded.
                Dim parseRules As New List(Of LogParseRule)
                If plugin IsNot Nothing Then
                    Try
                        Dim rules = plugin.GetLogParseRules()
                        If rules IsNot Nothing Then
                            For Each r In rules
                                parseRules.Add(r)
                            Next
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex, "Plugin failed to report parse rules for {Id}", instanceId)
                    End Try
                End If

                ' Query the plugin for spawn-time launch options if it
                ' implements the opt-in ILaunchOptionsProvider interface.
                ' Plugins that don't implement it leave both booleans
                ' False, which the node treats the same as a plugin
                ' that didn't implement the interface at all — it
                ' decides spawn details from declared log sources.
                ' Concrete True values let plugins describe specific
                ' needs of their game (e.g. Factorio sets
                ' RequiresConsoleIsolation to handle its
                ' AttachConsole(parent) startup trick).
                Dim resolvedStdoutIsLog As Boolean = False
                Dim resolvedRequiresConsoleIsolation As Boolean = False
                Dim resolvedTailerDelayMs As Integer = -1
                If plugin IsNot Nothing Then
                    Dim launchOptsProvider = TryCast(plugin, ILaunchOptionsProvider)
                    If launchOptsProvider IsNot Nothing Then
                        Try
                            Dim opts = launchOptsProvider.GetLaunchOptions(instanceConfig)
                            If opts IsNot Nothing Then
                                resolvedStdoutIsLog = opts.StdoutIsLog
                                resolvedRequiresConsoleIsolation = opts.RequiresConsoleIsolation
                                resolvedTailerDelayMs = opts.LogTailerStartDelayMs
                            End If
                        Catch ex As Exception
                            _logger.LogWarning(ex,
                                "Plugin failed to report launch options for {Id} — falling back to defaults",
                                instanceId)
                        End Try
                    End If
                End If

                ' Try each candidate until one succeeds.
                ' Remember winner in ExeOverride so next start goes straight to it.
                For i = 0 To candidates.Count - 1
                    Dim candidate = candidates(i)

                    Dim request As New StartInstanceRequest With {
                        .InstanceId = instanceId,
                        .ExePath = candidate,
                        .Arguments = launchArgs,
                        .WorkingDirectory = installEntity.InstallPath,
                        .EnvironmentVars = New Dictionary(Of String, String),
                        .CrashPolicy = CrashRestartPolicy.RestartWithBackoff,
                        .MaxCrashCount = GetIntField(customFields, "MaxCrashCount",
                            If(instanceConfig.MaxCrashCount > 0, instanceConfig.MaxCrashCount, 5)),
                        .CrashWindowMinutes = GetIntField(customFields, "CrashWindowMinutes",
                            If(instanceConfig.CrashWindowMinutes > 0, instanceConfig.CrashWindowMinutes, 60)),
                        .CrashCountResetAfterSeconds = GetIntField(customFields,
                            "CrashCountResetAfterSeconds", 300),
                        .MinRestartDelayMs = GetIntField(customFields,
                            "MinRestartDelayMs", 0),
                        .RconPort = rconPort,
                        .RconPassword = rconPassword,
                        .RconProtocol = rconProtocol,
                        .LogFilePaths = logFilePaths,
                        .LogParseRules = parseRules,
                        .StdoutIsLog = resolvedStdoutIsLog,
                        .RequiresConsoleIsolation = resolvedRequiresConsoleIsolation,
                        .LogTailerStartDelayMs = resolvedTailerDelayMs
                    }

                    Try
                        Dim result = Await client.StartInstanceAsync(request, CancellationToken.None)

                        ' The node may return HTTP 200 with a failure state
                        ' (e.g. Process.Start threw Win32Exception). Check both
                        ' the state and the error message for file-not-found.
                        Dim resultErr = If(result IsNot Nothing, If(result.ErrorMessage, ""), "").ToLowerInvariant()
                        Dim resultState = If(result IsNot Nothing, result.CurrentState, GSM.Plugin.InstanceState.Stopped)
                        Dim responseFailed =
                            resultState = GSM.Plugin.InstanceState.Stopped OrElse
                            resultState = GSM.Plugin.InstanceState.Crashed OrElse
                            Not String.IsNullOrEmpty(resultErr)

                        Dim isNotFound =
                            resultErr.Contains("not found") OrElse
                            resultErr.Contains("cannot find") OrElse
                            resultErr.Contains("no such file") OrElse
                            resultErr.Contains("does not exist")

                        If responseFailed Then
                            If isNotFound AndAlso i < candidates.Count - 1 Then
                                _logger.LogInformation(
                                    "Candidate {Exe} not found on node, trying next. Error: {Err}",
                                    candidate, resultErr)
                                If i = 0 AndAlso Not String.IsNullOrEmpty(instanceEntity.ExeOverride) AndAlso
                                   String.Equals(instanceEntity.ExeOverride, candidate, StringComparison.OrdinalIgnoreCase) Then
                                    instanceEntity.ExeOverride = ""
                                    db.SaveChanges()
                                End If
                                Continue For
                            End If

                            _logger.LogError("Node rejected start for {Id}: {Err}",
                                             instanceId, resultErr)
                            Return False
                        End If

                        ' Success
                        _liveStates(instanceId) = result
                        _logger.LogInformation("Started instance {Id} on node {Node} using {Exe}",
                                               instanceId, nodeEntity.DisplayName, candidate)

                        If _emitter IsNot Nothing Then _emitter.InstanceStarted(instanceId, result.Pid)

                        If Not String.Equals(instanceEntity.ExeOverride, candidate, StringComparison.OrdinalIgnoreCase) Then
                            instanceEntity.ExeOverride = candidate
                            instanceEntity.UpdatedUtc = DateTime.UtcNow
                            db.SaveChanges()
                            _logger.LogInformation("Saved ExeOverride={Exe} for {Id}", candidate, instanceId)
                        End If

                        StartLogStream(instanceId, client)
                        Return True

                    Catch ex As Exception
                        Dim msg = ex.Message.ToLowerInvariant()
                        Dim isNotFoundEx =
                            msg.Contains("not found") OrElse
                            msg.Contains("cannot find") OrElse
                            msg.Contains("no such file") OrElse
                            msg.Contains("does not exist")

                        If isNotFoundEx AndAlso i < candidates.Count - 1 Then
                            _logger.LogInformation("Candidate {Exe} not found (exception), trying next", candidate)
                            If i = 0 AndAlso Not String.IsNullOrEmpty(instanceEntity.ExeOverride) AndAlso
                               String.Equals(instanceEntity.ExeOverride, candidate, StringComparison.OrdinalIgnoreCase) Then
                                instanceEntity.ExeOverride = ""
                                db.SaveChanges()
                            End If
                            Continue For
                        End If

                        _logger.LogError(ex, "Failed to start instance {Id}", instanceId)
                        Return False
                    End Try
                Next

                _logger.LogError("All executable candidates failed for instance {Id}", instanceId)
                Return False
            End Using
        End Function

        ''' <summary>
        ''' Stops an instance on its node. If gracefulTimeoutMs is not
        ''' explicitly supplied (left at the default -1), the value is
        ''' looked up from the instance's merged config via the
        ''' "GracefulTimeoutMs" custom field, falling back to 25000.
        ''' </summary>
        Public Async Function StopInstanceAsync(instanceId As String,
                                                Optional gracefulTimeoutMs As Integer = -1) As Task(Of Boolean)
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then
                ' No client available — still clean up local stream
                ' and player tracking so any stale state from a prior
                ' run goes away. Nothing to ask the node to do.
                StopLogStream(instanceId)
                ClearPlayerTracking(instanceId)
                Return False
            End If

            Dim effectiveTimeoutMs = gracefulTimeoutMs
            If effectiveTimeoutMs < 0 Then
                effectiveTimeoutMs = GetIntField(GetMergedCustomFields(instanceId),
                                                  "GracefulTimeoutMs", 25000)
            End If

            Dim succeeded = False
            Try
                Dim request As New StopInstanceRequest With {
                    .InstanceId = instanceId,
                    .GracefulTimeoutMs = effectiveTimeoutMs
                }
                Dim result = Await client.StopInstanceAsync(request, CancellationToken.None)
                _liveStates(instanceId) = result
                _logger.LogInformation("Stopped instance {Id} (graceful timeout {Ms}ms)",
                                       instanceId, effectiveTimeoutMs)
                If _emitter IsNot Nothing Then _emitter.InstanceStopped(instanceId, result.Pid, result.LastExitCode)
                succeeded = True
            Catch ex As Exception
                _logger.LogError(ex, "Failed to stop instance {Id}", instanceId)
            Finally
                ' Tear down log streaming + player tracking AFTER the
                ' stop attempt completes (or fails). Doing it BEFORE
                ' — as the previous implementation did — meant users
                ' never saw the graceful-shutdown log lines the game
                ' emits while its Ctrl+C handler walks through cleanup,
                ' because the manager had already disconnected from
                ' the node's log stream by the time the node forwarded
                ' them. Keeping the stream alive across the node's
                ' StopInstanceAsync call lets those lines flow through
                ' in real time. We close the stream once the call
                ' returns — the node's response signals either
                ' graceful exit or hard kill, both of which mean no
                ' more new lines are coming.
                '
                ' ClearPlayerTracking moves to the same point so the
                ' SessionHost row close timestamp matches the actual
                ' stop-completion time rather than the stop-request
                ' time, and so any join/leave events fired during the
                ' graceful-shutdown window still get processed against
                ' the live tracking set instead of being dropped.
                StopLogStream(instanceId)
                ClearPlayerTracking(instanceId)
            End Try

            Return succeeded
        End Function

        ''' <summary>
        ''' Restarts an instance (stop then start).
        ''' </summary>
        Public Async Function RestartInstanceAsync(instanceId As String) As Task(Of Boolean)
            Dim stopped = Await StopInstanceAsync(instanceId)
            If Not stopped Then Return False

            ' Wait for the node to actually report Stopped state.
            ' StopInstanceAsync returns as soon as the HTTP call completes,
            ' but graceful shutdown (Ctrl+C) can take several seconds.
            Dim deadline = DateTime.UtcNow.AddSeconds(30)
            While DateTime.UtcNow < deadline
                Await Task.Delay(500)
                Dim state = Await RefreshInstanceStateAsync(instanceId)
                If state Is Nothing Then Exit While
                If state.CurrentState = GSM.Plugin.InstanceState.Stopped OrElse
                   state.CurrentState = GSM.Plugin.InstanceState.Crashed OrElse
                   state.CurrentState = GSM.Plugin.InstanceState.CrashLoopHalted Then
                    Exit While
                End If
            End While

            Await Task.Delay(1000)
            Return Await StartInstanceAsync(instanceId)
        End Function

        ''' <summary>
        ''' Returns the last known live state for an instance.
        ''' </summary>
        ''' <summary>
        ''' Ensures a log stream is active for the given instance. If one
        ''' is already running, does nothing. Otherwise, queries the node
        ''' for state and starts a stream if the instance is running.
        ''' Safe to call from UI (e.g. when the user opens a log viewer).
        ''' </summary>
        Public Async Function EnsureLogStreamAsync(instanceId As String) As Task
            If _logStreamCancellations.ContainsKey(instanceId) Then Return

            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return

            Try
                Dim state = Await client.GetInstanceStatusAsync(instanceId, CancellationToken.None)
                If state Is Nothing Then Return
                If state.CurrentState = GSM.Plugin.InstanceState.Running OrElse
                   state.CurrentState = GSM.Plugin.InstanceState.Starting Then
                    StartLogStream(instanceId, client)
                    _logger.LogInformation("Reconnected log stream for {Id}", instanceId)
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to ensure log stream for {Id}", instanceId)
            End Try
        End Function

        ''' <summary>
        ''' Reconnects log streams for every instance the database says
        ''' exists. Called on Manager startup so that instances still
        ''' running on their nodes resume having their logs buffered.
        ''' </summary>
        Public Async Function ReconnectLogStreamsAsync() As Task
            Dim instanceIds As New List(Of String)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                For Each inst In db.Instances.ToList()
                    instanceIds.Add(inst.InstanceId)
                Next
            End Using

            For Each id In instanceIds
                Await EnsureLogStreamAsync(id)
            Next
        End Function

        ''' <summary>
        ''' Fetches the most recent log lines directly from the node's
        ''' ring buffer. Used to populate a log viewer when the Manager
        ''' has no local buffer (e.g. just after Manager startup).
        ''' </summary>
        Public Async Function GetRecentLogsAsync(instanceId As String,
                                                  count As Integer) As Task(Of IReadOnlyList(Of LogLine))
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return New List(Of LogLine)()
            Try
                Return Await client.GetRecentLogsAsync(instanceId, count, CancellationToken.None)
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to fetch recent logs for {Id}", instanceId)
                Return New List(Of LogLine)()
            End Try
        End Function

        ''' <summary>
        ''' Returns the list of players the node currently believes are
        ''' online for this instance. Backed by the node's log parser.
        ''' </summary>
        Public Async Function GetPlayersAsync(instanceId As String) As Task(Of IReadOnlyList(Of PlayerSession))
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return New List(Of PlayerSession)()
            Try
                Return Await client.GetPlayersAsync(instanceId, CancellationToken.None)
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to fetch players for {Id}", instanceId)
                Return New List(Of PlayerSession)()
            End Try
        End Function

        ''' <summary>
        ''' Returns derived server state (match state, current tile,
        ''' backend registration) for this instance.
        ''' </summary>
        Public Async Function GetServerStateAsync(instanceId As String) As Task(Of ServerStateResponse)
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return New ServerStateResponse()
            Try
                Return Await client.GetServerStateAsync(instanceId, CancellationToken.None)
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to fetch server state for {Id}", instanceId)
                Return New ServerStateResponse()
            End Try
        End Function

        ''' <summary>
        ''' Returns stored chat messages for this instance. Pass
        ''' sinceUtc to fetch only new messages; pass Nothing to get
        ''' the most recent {limit} messages.
        ''' </summary>
        Public Async Function GetChatHistoryAsync(instanceId As String,
                                                    sinceUtc As DateTime?,
                                                    limit As Integer) As Task(Of IReadOnlyList(Of ChatMessage))
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return New List(Of ChatMessage)()
            Try
                Return Await client.GetChatHistoryAsync(instanceId, sinceUtc, limit, CancellationToken.None)
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to fetch chat history for {Id}", instanceId)
                Return New List(Of ChatMessage)()
            End Try
        End Function

        Public Function GetLiveState(instanceId As String) As InstanceStatusResponse
            Dim result As InstanceStatusResponse = Nothing
            _liveStates.TryGetValue(instanceId, result)
            Return result
        End Function

        ''' <summary>
        ''' Polls the node for the current state of an instance.
        ''' Also detects state transitions (into Crashed or
        ''' CrashLoopHalted) and emits notification events — this is
        ''' the only place those notifications fire, since the node is
        ''' the authority on instance state.
        '''
        ''' Concurrent callers are serialised per-instance via
        ''' _refreshLocks so that the UI poller and the background
        ''' poller can't both observe the same transition.
        ''' </summary>
        Public Async Function RefreshInstanceStateAsync(instanceId As String) As Task(Of InstanceStatusResponse)
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return Nothing

            Dim gate = _refreshLocks.GetOrAdd(instanceId, Function(id) New SemaphoreSlim(1, 1))
            Await gate.WaitAsync()
            Try
                Dim result = Await client.GetInstanceStatusAsync(instanceId, CancellationToken.None)

                ' Compare against previous cached state to detect
                ' transitions worth announcing.
                Dim previous As InstanceStatusResponse = Nothing
                _liveStates.TryGetValue(instanceId, previous)
                _liveStates(instanceId) = result

                If _emitter IsNot Nothing AndAlso previous IsNot Nothing AndAlso result IsNot Nothing Then
                    Dim prevState = previous.CurrentState
                    Dim newState = result.CurrentState

                    ' Transition INTO Crashed — the node moved the
                    ' instance from Running/Starting/Stopping to Crashed.
                    If newState = GSM.Plugin.InstanceState.Crashed AndAlso
                       prevState <> GSM.Plugin.InstanceState.Crashed Then
                        _emitter.InstanceCrashed(instanceId, result.LastExitCode, result.ErrorMessage)
                    End If

                    ' Transition INTO CrashLoopHalted. Trust the
                    ' node's authoritative state rather than guessing
                    ' from a CrashCount >= 3 heuristic — the node
                    ' uses a WINDOWED count against MaxCrashCount
                    ' (default 5, not 3), so the old heuristic both
                    ' mismatched the threshold and reset a counter
                    ' the node never resets. Symptom was spurious
                    ' "crash loop halted" notifications for instances
                    ' that had auto-restarted just fine.
                    If newState = GSM.Plugin.InstanceState.CrashLoopHalted AndAlso
                       prevState <> GSM.Plugin.InstanceState.CrashLoopHalted Then
                        _emitter.CrashLoopDetected(instanceId, result.CrashCount, 10)
                    End If
                End If

                Return result
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to refresh state for {Id}", instanceId)
                Return Nothing
            Finally
                gate.Release()
            End Try
        End Function

        ' ============================================================
        '  Background polling
        ' ============================================================

        ''' <summary>
        ''' Starts a background task that periodically refreshes every
        ''' known instance's state. Safe to call more than once —
        ''' subsequent calls are no-ops. Call from Manager startup
        ''' after services are built.
        ''' </summary>
        Public Sub StartBackgroundPolling()
            If _pollingCts IsNot Nothing Then Return
            _pollingCts = New CancellationTokenSource()
            Dim token = _pollingCts.Token
            _pollingTask = Task.Run(Function() BackgroundPollLoopAsync(token))
            _logger.LogInformation("InstanceManager background polling started ({Interval}ms)",
                                   BackgroundPollIntervalMs)
        End Sub

        ''' <summary>
        ''' Signals the background poller to stop and awaits completion.
        ''' Called on Manager shutdown.
        ''' </summary>
        Public Async Function StopBackgroundPollingAsync() As Task
            Dim cts = _pollingCts
            If cts Is Nothing Then Return
            _pollingCts = Nothing
            cts.Cancel()
            Try
                If _pollingTask IsNot Nothing Then
                    Await _pollingTask
                End If
            Catch
            End Try
            cts.Dispose()
        End Function

        Private Async Function BackgroundPollLoopAsync(token As CancellationToken) As Task
            ' Chat mirror runs slower than state polling. State needs
            ' to react to crashes quickly; chat is user-readable
            ' history where 5s latency is fine. We track the last
            ' chat poll time and only poll chat when enough time
            ' has passed, while still running state polling at the
            ' faster BackgroundPollIntervalMs cadence.
            Dim lastChatPoll As DateTime = DateTime.MinValue

            While Not token.IsCancellationRequested
                Try
                    Dim ids = FetchAllInstanceIds()
                    For Each id In ids
                        If token.IsCancellationRequested Then Return
                        Try
                            Await RefreshInstanceStateAsync(id)
                        Catch ex As Exception
                            _logger.LogDebug(ex, "Background poll failed for {Id}", id)
                        End Try
                    Next

                    ' Chat mirror — only once per ChatMirrorIntervalMs
                    If (DateTime.UtcNow - lastChatPoll).TotalMilliseconds >= ChatMirrorIntervalMs Then
                        lastChatPoll = DateTime.UtcNow
                        For Each id In ids
                            If token.IsCancellationRequested Then Return
                            Try
                                Await MirrorChatForInstanceAsync(id)
                            Catch ex As Exception
                                _logger.LogDebug(ex, "Chat mirror failed for {Id}", id)
                            End Try
                        Next
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex, "Background poll loop iteration failed")
                End Try

                Try
                    Await Task.Delay(BackgroundPollIntervalMs, token)
                Catch
                    Return
                End Try
            End While
        End Function

        ''' <summary>
        ''' Pulls any new chat messages from the node since our
        ''' last-persisted timestamp for this instance, and writes
        ''' them to ChatMessages keyed by session identity. Idempotent:
        ''' cursor advancement means repeated calls without new chat
        ''' are cheap. If the manager restarts, the cursor is seeded
        ''' on first call from max(TimestampUtc) already in the DB
        ''' so we don't re-ingest history.
        ''' </summary>
        Private Async Function MirrorChatForInstanceAsync(instanceId As String) As Task
            ' Seed cursor on first call after manager start. The
            ' cursor tracks ONLY this instance's last-persisted
            ' chat timestamp — cross-instance history queries come
            ' later via SessionIdentity joins.
            Dim cursor As DateTime
            If Not _chatCursors.TryGetValue(instanceId, cursor) Then
                cursor = SeedChatCursor(instanceId)
                _chatCursors(instanceId) = cursor
            End If

            ' Only poll if the instance is known to this manager.
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return

            Dim nodeMessages As IReadOnlyList(Of ChatMessage) = Nothing
            Try
                ' Use Nothing (not cursor) on the initial pass so the
                ' node returns its most recent batch and we can catch
                ' up to the present without paging backwards. On
                ' subsequent passes, pass the cursor for delta-only.
                Dim sinceParam As DateTime? = Nothing
                If cursor > DateTime.MinValue Then sinceParam = cursor

                nodeMessages = Await client.GetChatHistoryAsync(
                    instanceId, sinceParam, ChatMirrorBatchLimit, CancellationToken.None)
            Catch
                Return
            End Try

            If nodeMessages Is Nothing OrElse nodeMessages.Count = 0 Then Return

            Dim sessionIdentity = ResolveSessionIdentity(instanceId)
            If String.IsNullOrEmpty(sessionIdentity) Then Return

            Dim nodeId = GetNodeIdForInstance(instanceId)

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                Dim newCursor = cursor
                Dim added As Integer = 0
                For Each msg In nodeMessages
                    ' Defensive dedup — if the cursor was Nothing on
                    ' first call, the node may send messages we've
                    ' already got from a previous manager run. Skip
                    ' any timestamp <= cursor.
                    If msg.TimestampUtc <= cursor Then Continue For

                    db.ChatMessages.Add(New ChatMessageEntity With {
                        .MessageId = Guid.NewGuid().ToString("N"),
                        .SessionIdentity = sessionIdentity,
                        .NodeId = nodeId,
                        .InstanceId = instanceId,
                        .TimestampUtc = msg.TimestampUtc,
                        .PlayerName = msg.PlayerName,
                        .Text = msg.Text
                    })
                    added += 1
                    If msg.TimestampUtc > newCursor Then newCursor = msg.TimestampUtc
                Next

                If added > 0 Then
                    db.SaveChanges()
                    _chatCursors(instanceId) = newCursor
                    _logger.LogDebug("Mirrored {Count} chat message(s) for {Id}",
                                     added, instanceId)
                End If
            End Using
        End Function

        ''' <summary>
        ''' On first mirror call after manager start, look up the
        ''' last-persisted chat timestamp for this instance in
        ''' ChatMessages and return it as the cursor. Prevents
        ''' re-ingesting history that was already mirrored before
        ''' the manager restart. Returns DateTime.MinValue if no
        ''' rows exist for this instance yet.
        ''' </summary>
        Private Function SeedChatCursor(instanceId As String) As DateTime
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim latest = db.ChatMessages.
                        Where(Function(c) c.InstanceId = instanceId).
                        Select(Function(c) CType(c.TimestampUtc, DateTime?)).
                        Max()
                    Return If(latest.HasValue, latest.Value, DateTime.MinValue)
                End Using
            Catch
                Return DateTime.MinValue
            End Try
        End Function

        ''' <summary>
        ''' Resolves the NodeId that owns a given instance, for
        ''' stamping into ChatMessages.NodeId. Returns empty string
        ''' if the lookup fails — persistence still happens, just
        ''' without the denormalized node reference.
        ''' </summary>
        Private Function GetNodeIdForInstance(instanceId As String) As String
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = (From inst In db.Instances
                               Join install In db.Installations
                                   On inst.InstallationId Equals install.InstallationId
                               Where inst.InstanceId = instanceId
                               Select install.NodeId).FirstOrDefault()
                    Return If(row, "")
                End Using
            Catch
                Return ""
            End Try
        End Function

        Private Function FetchAllInstanceIds() As List(Of String)
            Dim ids As New List(Of String)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                For Each inst In db.Instances.ToList()
                    ids.Add(inst.InstanceId)
                Next
            End Using
            Return ids
        End Function

        ''' <summary>
        ''' Sends an RCON command to an instance.
        ''' </summary>
        Public Async Function SendRconCommandAsync(instanceId As String,
                                                    command As String) As Task(Of String)
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return "No node client available"

            Try
                Dim request As New RconCommandRequest With {
                    .InstanceId = instanceId,
                    .Command = command
                }
                Dim result = Await client.SendRconCommandAsync(request, CancellationToken.None)
                If result.Success Then
                    Return result.Response
                End If
                Return $"RCON error: {result.ErrorMessage}"
            Catch ex As Exception
                Return $"RCON exception: {ex.Message}"
            End Try
        End Function

        ''' <summary>
        ''' Gets the player count for an instance (via RCON or node query).
        ''' Returns 0 if unavailable.
        ''' </summary>
        Public Function GetPlayerCountAsync(instanceId As String) As Task(Of Integer)
            ' For now, return 0 — proper implementation requires
            ' game-specific RCON commands via the plugin
            Return Task.FromResult(0)
        End Function

        ''' <summary>
        ''' Returns all instance IDs for an installation.
        ''' </summary>
        Public Function GetInstanceIdsForInstallation(installationId As String) As IReadOnlyList(Of String)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Return db.Instances.
                    Where(Function(i) i.InstallationId = installationId).
                    Select(Function(i) i.InstanceId).
                    ToList()
            End Using
        End Function

        ' ============================================================
        '  Log streaming
        ' ============================================================

        Private Sub StartLogStream(instanceId As String, client As INodeClient)
            Dim cts As New CancellationTokenSource()
            _logStreamCancellations(instanceId) = cts

            ' Create a log parser for this instance's game
            Dim gameId = GetGameIdForInstance(instanceId)
            Dim parser As ILogParser = Nothing
            If gameId IsNot Nothing Then
                parser = _pluginRegistry.CreateParser(gameId)
            End If

            If parser IsNot Nothing Then
                _logParsers(instanceId) = New ActiveLogParser With {
                    .Parser = parser,
                    .InstanceId = instanceId
                }
            End If

            ' Stream in background
            Task.Run(Function() StreamLogsInBackgroundAsync(instanceId, client, parser, cts.Token))
        End Sub

        Private Async Function StreamLogsInBackgroundAsync(instanceId As String,
                                                            client As INodeClient,
                                                            parser As ILogParser,
                                                            cancellation As CancellationToken) As Task
            Try
                Await client.StreamLogsAsync(instanceId,
                    Sub(line)
                        ' Store in manager-side buffer
                        Dim logStore = ManagerProgram.Services.GetService(Of ManagerRingBufferStore)()
                        If logStore IsNot Nothing Then
                            logStore.Append(instanceId, line)
                        End If

                        ' Run through parser and fire notification
                        ' events for classifiable lines. Raw parser
                        ' verdicts are noisy (see _activePlayers
                        ' comment) so we route through HandlePlayer*
                        ' helpers that gate on actual state transitions.
                        If parser IsNot Nothing AndAlso _emitter IsNot Nothing Then
                            Try
                                Dim parsed = parser.ParseLine(line)
                                If parsed IsNot Nothing Then
                                    Select Case parsed.EventType
                                        Case LogEventType.PlayerJoin
                                            Dim name = If(parsed.PlayerInfo IsNot Nothing,
                                                          parsed.PlayerInfo.PlayerName, "")
                                            HandlePlayerJoin(instanceId, name)
                                        Case LogEventType.PlayerLeave
                                            Dim name = If(parsed.PlayerInfo IsNot Nothing,
                                                          parsed.PlayerInfo.PlayerName, "")
                                            HandlePlayerLeave(instanceId, name)
                                        Case LogEventType.TileLoaded
                                            ' Session identity was committed by
                                            ' the parser just before this event;
                                            ' the event's SessionIdentity field
                                            ' carries the freshly-committed value.
                                            ' TileName is in Metadata if the plugin
                                            ' supplied one (LO does, Factorio doesn't).
                                            Dim tileName As String = Nothing
                                            If parsed.Metadata IsNot Nothing Then
                                                parsed.Metadata.TryGetValue("TileName", tileName)
                                            End If
                                            HandleTileLoaded(instanceId, parsed.SessionIdentity, tileName)
                                        Case LogEventType.TileUnloaded
                                            ' SessionIdentity here is the identity
                                            ' that just ENDED — the parser cleared
                                            ' CurrentSessionIdentity before emitting
                                            ' this event, so we read from the event
                                            ' field rather than the parser property.
                                            HandleTileUnloaded(instanceId, parsed.SessionIdentity)
                                    End Select
                                End If
                            Catch
                                ' Parser blow-ups must not take down
                                ' the log stream — swallow and move on.
                            End Try
                        End If
                    End Sub, cancellation)
            Catch ex As OperationCanceledException
                ' Normal
            Catch ex As Exception
                _logger.LogWarning(ex, "Log stream ended for {Id}", instanceId)
            End Try
        End Function

        Private Sub StopLogStream(instanceId As String)
            Dim cts As CancellationTokenSource = Nothing
            If _logStreamCancellations.TryRemove(instanceId, cts) Then
                cts.Cancel()
                cts.Dispose()
            End If
            Dim removedParser As ActiveLogParser = Nothing
            _logParsers.TryRemove(instanceId, removedParser)
        End Sub

        ' ============================================================
        '  Player state tracking (notification dedup layer)
        ' ============================================================

        ''' <summary>
        ''' Called when the parser reports a join. Emits the
        ''' PlayerJoined notification only if the name wasn't already
        ''' in the active set for that instance — this is what
        ''' suppresses duplicate joins from log replay on stream
        ''' reconnect. Empty names are ignored; a join without a name
        ''' isn't useful to anyone.
        ''' </summary>
        Private Sub HandlePlayerJoin(instanceId As String, playerName As String)
            If String.IsNullOrWhiteSpace(playerName) Then Return

            Dim bucket = _activePlayers.GetOrAdd(instanceId,
                Function(id) New HashSet(Of String)(StringComparer.OrdinalIgnoreCase))

            Dim added As Boolean
            SyncLock bucket
                added = bucket.Add(playerName)
            End SyncLock

            If added Then
                _emitter.PlayerJoined(instanceId, playerName)
                PersistPlayerObservation(instanceId, playerName, isJoin:=True)
            End If
        End Sub

        ''' <summary>
        ''' Called when the parser reports a leave. Three cases:
        '''
        '''   1. Named leave AND the name is in our active set →
        '''      remove + emit (real leave, possibly already deduped
        '''      by the set membership check).
        '''   2. Named leave AND the name ISN'T in our active set →
        '''      skip (log replay or cross-session leftover).
        '''   3. Nameless leave (UE4's UChannel/UNetConnection close) →
        '''      apply per-instance cooldown to swallow the paired
        '''      duplicate, then: if exactly one player is tracked for
        '''      this instance, attribute the leave to that player;
        '''      otherwise skip. The "exactly one" guard avoids
        '''      guessing wrong when multiple players are online, and
        '''      the "0 tracked" guard drops the internal-channel-close
        '''      noise that fires when no-one's connected.
        ''' </summary>
        Private Sub HandlePlayerLeave(instanceId As String, playerName As String)
            Dim bucket = _activePlayers.GetOrAdd(instanceId,
                Function(id) New HashSet(Of String)(StringComparer.OrdinalIgnoreCase))

            If Not String.IsNullOrWhiteSpace(playerName) Then
                Dim removed As Boolean
                SyncLock bucket
                    removed = bucket.Remove(playerName)
                End SyncLock
                If removed Then
                    _emitter.PlayerLeft(instanceId, playerName)
                    PersistPlayerObservation(instanceId, playerName, isJoin:=False)
                End If
                Return
            End If

            ' Nameless leave — apply cooldown to absorb the paired fire
            Dim now = DateTime.UtcNow
            Dim lastEmit As DateTime
            If _lastEmptyLeaveAt.TryGetValue(instanceId, lastEmit) Then
                If (now - lastEmit).TotalMilliseconds < EmptyLeaveCooldownMs Then Return
            End If

            Dim inferredName As String = Nothing
            SyncLock bucket
                If bucket.Count = 1 Then
                    inferredName = bucket.First()
                    bucket.Remove(inferredName)
                End If
            End SyncLock

            If inferredName IsNot Nothing Then
                _lastEmptyLeaveAt(instanceId) = now
                _emitter.PlayerLeft(instanceId, inferredName)
                PersistPlayerObservation(instanceId, inferredName, isJoin:=False)
            End If
            ' bucket.Count = 0: server-internal channel close, ignore
            ' bucket.Count >= 2: can't disambiguate, wait for a named
            '   leave or for the set to drain via subsequent events
        End Sub

        ''' <summary>
        ''' Called on instance stop. Drops all tracked players and
        ''' the empty-leave cooldown for this instance so a later
        ''' fresh start begins from a clean slate.
        ''' </summary>
        Private Sub ClearPlayerTracking(instanceId As String)
            Dim removed As HashSet(Of String) = Nothing
            _activePlayers.TryRemove(instanceId, removed)
            Dim removedTs As DateTime
            _lastEmptyLeaveAt.TryRemove(instanceId, removedTs)

            ' Also close any open session-host row for this instance.
            ' A stop that happens while actively hosting a tile (rare
            ' but possible — e.g. user clicks Stop during gameplay)
            ' would otherwise leave an open-ended SessionHost row.
            Try
                CloseOpenSessionHostForInstance(instanceId, DateTime.UtcNow)
            Catch ex As Exception
                _logger.LogDebug(ex, "CloseOpenSessionHostForInstance failed on stop for {Id}", instanceId)
            End Try
        End Sub

        ' ============================================================
        '  Round C — session history persistence
        ' ============================================================

        ''' <summary>
        ''' Resolve the effective session identity for an instance.
        ''' If the plugin's parser tracks one (e.g. Last Oasis), use
        ''' that. Otherwise fall back to "{gameId}:{instanceId}" so
        ''' games without migration semantics still produce stable
        ''' session keys and all downstream queries (chat history by
        ''' session, player history by session) work uniformly.
        '''
        ''' Returns Nothing ONLY when we can't determine the gameId —
        ''' shouldn't happen in practice because every running
        ''' instance has a parser registered, but defensive nulls
        ''' mean persistence silently no-ops rather than crashing.
        ''' </summary>
        Private Function ResolveSessionIdentity(instanceId As String) As String
            Dim activeParser As ActiveLogParser = Nothing
            _logParsers.TryGetValue(instanceId, activeParser)
            If activeParser IsNot Nothing AndAlso activeParser.Parser IsNot Nothing Then
                Dim pluginIdentity = activeParser.Parser.CurrentSessionIdentity
                If Not String.IsNullOrEmpty(pluginIdentity) Then Return pluginIdentity
            End If

            ' Fallback: {gameId}:{instanceId}
            Dim gameId = GetGameIdForInstance(instanceId)
            If String.IsNullOrEmpty(gameId) Then Return Nothing
            Return $"{gameId}:{instanceId}"
        End Function

        ''' <summary>
        ''' Public wrapper on ResolveSessionIdentity for UI consumers
        ''' — the History window uses this to pre-fill the session
        ''' filter when launched from an instance panel. Returns the
        ''' session identity currently active for the instance, or
        ''' Nothing if the instance isn't running / we can't resolve
        ''' a gameId.
        ''' </summary>
        Public Function GetCurrentSessionIdentity(instanceId As String) As String
            Return ResolveSessionIdentity(instanceId)
        End Function

        ''' <summary>
        ''' UPSERT into PlayerSessions for a single observation AND
        ''' append an individual PlayerActivity row for the same
        ''' event. PlayerSessions is the summary; PlayerActivity is
        ''' the full event stream that powers History timeline
        ''' replay and snapshot-at-instant queries.
        '''
        ''' First sighting creates the PlayerSessions row; subsequent
        ''' ones bump LastSeenUtc (and LastHostInstanceId if the tile
        ''' has migrated to a different instance since last sight).
        ''' Swallows errors so DB failures can't cascade into the
        ''' event pipeline.
        ''' </summary>
        Private Sub PersistPlayerObservation(instanceId As String,
                                              playerName As String,
                                              isJoin As Boolean)
            Try
                Dim sessionIdentity = ResolveSessionIdentity(instanceId)
                If String.IsNullOrEmpty(sessionIdentity) Then Return

                Dim now = DateTime.UtcNow
                Dim nodeId = GetNodeIdForInstance(instanceId)
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    ' ---- PlayerSessions UPSERT (aggregate summary) ----
                    Dim row = db.PlayerSessions.FirstOrDefault(
                        Function(p) p.SessionIdentity = sessionIdentity AndAlso
                                    p.PlayerName = playerName)

                    If row Is Nothing Then
                        db.PlayerSessions.Add(New PlayerSessionEntity With {
                            .PlayerSessionId = Guid.NewGuid().ToString("N"),
                            .SessionIdentity = sessionIdentity,
                            .PlayerName = playerName,
                            .FirstSeenUtc = now,
                            .LastSeenUtc = now,
                            .LastHostInstanceId = instanceId
                        })
                    Else
                        row.LastSeenUtc = now
                        row.LastHostInstanceId = instanceId
                    End If

                    ' ---- PlayerActivity append (event stream) ----
                    db.PlayerActivity.Add(New PlayerActivityEntity With {
                        .ActivityId = Guid.NewGuid().ToString("N"),
                        .SessionIdentity = sessionIdentity,
                        .NodeId = nodeId,
                        .InstanceId = instanceId,
                        .TimestampUtc = now,
                        .PlayerName = playerName,
                        .EventKind = If(isJoin, "join", "leave")
                    })

                    db.SaveChanges()
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex, "PersistPlayerObservation failed for {Id}/{Name}",
                                 instanceId, playerName)
            End Try
        End Sub

        ''' <summary>
        ''' Called when the parser commits a new session identity
        ''' (e.g. Last Oasis finished loading a tile). Closes any
        ''' previously-open SessionHost row for this instance —
        ''' shouldn't exist but defensive — and opens a new one for
        ''' the new identity. Safe to call multiple times for the
        ''' same identity on the same instance (e.g. if TileLoaded
        ''' fires twice due to log replay): the existence check
        ''' prevents duplicates.
        ''' </summary>
        Private Sub HandleTileLoaded(instanceId As String,
                                      sessionIdentity As String,
                                      tileName As String)
            If String.IsNullOrEmpty(sessionIdentity) Then Return

            Try
                Dim now = DateTime.UtcNow
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    ' Close any open row that belongs to a DIFFERENT
                    ' session (instance switching tiles without a
                    ' clean TileUnloaded in between).
                    Dim openRows = db.SessionHosts.
                        Where(Function(h) h.InstanceId = instanceId AndAlso
                                          h.HostedUntilUtc Is Nothing AndAlso
                                          h.SessionIdentity <> sessionIdentity).ToList()
                    For Each row In openRows
                        row.HostedUntilUtc = now
                    Next

                    ' Check if we already have an open row for this
                    ' exact (instance, session) — if so, don't create
                    ' a duplicate (log replay safety). Do refresh
                    ' TileName on the existing row if the plugin
                    ' supplied one and we didn't have one stored.
                    Dim existing = db.SessionHosts.FirstOrDefault(
                        Function(h) h.InstanceId = instanceId AndAlso
                                    h.SessionIdentity = sessionIdentity AndAlso
                                    h.HostedUntilUtc Is Nothing)

                    If existing Is Nothing Then
                        db.SessionHosts.Add(New SessionHostEntity With {
                            .HostId = Guid.NewGuid().ToString("N"),
                            .SessionIdentity = sessionIdentity,
                            .InstanceId = instanceId,
                            .HostedFromUtc = now,
                            .HostedUntilUtc = Nothing,
                            .TileName = tileName
                        })
                    ElseIf String.IsNullOrEmpty(existing.TileName) AndAlso
                           Not String.IsNullOrEmpty(tileName) Then
                        existing.TileName = tileName
                    End If

                    db.SaveChanges()
                End Using

                ' Notify the restart coordinator so that any
                ' in-flight WaitForReadySignal(TileLoaded) for
                ' this instance can complete. Safe to call on
                ' every tile load — the coordinator no-ops when
                ' nothing is waiting.
                If _restartCoordinator IsNot Nothing Then
                    Try
                        _restartCoordinator.NotifySignalObserved(
                            instanceId, ReadySignalKind.TileLoaded, Nothing)
                    Catch ex As Exception
                        _logger.LogDebug(ex,
                            "RestartCoordinator.NotifySignalObserved threw for {Id}",
                            instanceId)
                    End Try
                End If
            Catch ex As Exception
                _logger.LogDebug(ex, "HandleTileLoaded failed for {Id}/{Session}",
                                 instanceId, sessionIdentity)
            End Try
        End Sub

        ''' <summary>
        ''' Called when the parser observes TileUnloaded. Closes the
        ''' open SessionHost row for (instance, session) by stamping
        ''' HostedUntilUtc. If no open row matches (e.g. manager
        ''' restarted between TileLoaded and TileUnloaded) this is
        ''' a silent no-op.
        ''' </summary>
        Private Sub HandleTileUnloaded(instanceId As String, sessionIdentity As String)
            If String.IsNullOrEmpty(sessionIdentity) Then Return

            Try
                Dim now = DateTime.UtcNow
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    Dim row = db.SessionHosts.FirstOrDefault(
                        Function(h) h.InstanceId = instanceId AndAlso
                                    h.SessionIdentity = sessionIdentity AndAlso
                                    h.HostedUntilUtc Is Nothing)

                    If row IsNot Nothing Then
                        row.HostedUntilUtc = now
                        db.SaveChanges()
                    End If
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex, "HandleTileUnloaded failed for {Id}/{Session}",
                                 instanceId, sessionIdentity)
            End Try
        End Sub

        ''' <summary>
        ''' Closes any open SessionHost row owned by this instance,
        ''' regardless of session identity. Called from
        ''' ClearPlayerTracking (which runs on instance stop) so we
        ''' don't leave phantom "still hosting" rows after a stop.
        ''' </summary>
        Private Sub CloseOpenSessionHostForInstance(instanceId As String, endTime As DateTime)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim openRows = db.SessionHosts.
                    Where(Function(h) h.InstanceId = instanceId AndAlso
                                      h.HostedUntilUtc Is Nothing).ToList()
                If openRows.Count = 0 Then Return
                For Each row In openRows
                    row.HostedUntilUtc = endTime
                Next
                db.SaveChanges()
            End Using
        End Sub

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Function GetClientForInstance(instanceId As String) As INodeClient
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(instanceId)
                If instanceEntity Is Nothing Then Return Nothing

                Dim installEntity = db.Installations.Find(instanceEntity.InstallationId)
                If installEntity Is Nothing Then Return Nothing

                Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                If nodeEntity Is Nothing Then Return Nothing

                Return _clientFactory.GetClient(
                    nodeEntity.NodeId, nodeEntity.HostAddress,
                    nodeEntity.Port, nodeEntity.AuthToken)
            End Using
        End Function

        Private Function GetGameIdForInstance(instanceId As String) As String
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(instanceId)
                Return instanceEntity?.GameId
            End Using
        End Function

        Private Shared Function DeserializeConfig(json As String) As Dictionary(Of String, String)
            If String.IsNullOrEmpty(json) Then Return New Dictionary(Of String, String)
            Try
                Return JsonSerializer.Deserialize(Of Dictionary(Of String, String))(json)
            Catch
                Return New Dictionary(Of String, String)
            End Try
        End Function

        ''' <summary>
        ''' Reads a positive integer from a custom-fields dict, falling
        ''' back to defaultValue if missing, empty, non-numeric, or
        ''' non-positive. Used for the per-instance knobs exposed via
        ''' the InstanceConfig/InstallationConfig JSON:
        '''   MaxCrashCount, CrashWindowMinutes,
        '''   CrashCountResetAfterSeconds, MinRestartDelayMs,
        '''   GracefulTimeoutMs
        ''' </summary>
        Private Shared Function GetIntField(fields As Dictionary(Of String, String),
                                            key As String,
                                            defaultValue As Integer) As Integer
            If fields Is Nothing Then Return defaultValue
            Dim raw As String = Nothing
            If Not fields.TryGetValue(key, raw) Then Return defaultValue
            If String.IsNullOrWhiteSpace(raw) Then Return defaultValue
            Dim parsed As Integer
            If Integer.TryParse(raw.Trim(), parsed) AndAlso parsed > 0 Then Return parsed
            Return defaultValue
        End Function

        ''' <summary>
        ''' Loads the instance's ConfigJson merged on top of the
        ''' installation's ConfigJson (same merge rules as
        ''' StartInstanceAsync: instance wins on key collision, but
        ''' an empty instance value does not overwrite a non-empty
        ''' installation value).
        ''' </summary>
        Private Function GetMergedCustomFields(instanceId As String) As Dictionary(Of String, String)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                Dim inst = db.Instances.Find(instanceId)
                If inst Is Nothing Then Return New Dictionary(Of String, String)

                Dim install = db.Installations.Find(inst.InstallationId)
                Dim installFields = If(install IsNot Nothing,
                                       DeserializeConfig(install.ConfigJson),
                                       New Dictionary(Of String, String))
                Dim instanceFields = DeserializeConfig(inst.ConfigJson)

                Dim merged As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                For Each kvp In installFields
                    merged(kvp.Key) = kvp.Value
                Next
                For Each kvp In instanceFields
                    If String.IsNullOrEmpty(kvp.Value) AndAlso
                       merged.ContainsKey(kvp.Key) AndAlso
                       Not String.IsNullOrEmpty(merged(kvp.Key)) Then
                        Continue For
                    End If
                    merged(kvp.Key) = kvp.Value
                Next
                Return merged
            End Using
        End Function

    End Class

    ' ============================================================
    '  ActiveLogParser — tracks a running parser per instance
    ' ============================================================

    Public Class ActiveLogParser
        Public Property Parser As ILogParser
        Public Property InstanceId As String
    End Class

End Namespace