Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Net
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
        Private ReadOnly _sharedConfigService As SharedConfigService
        Private ReadOnly _emitter As NotificationEmitter
        Private ReadOnly _logger As ILogger(Of InstanceManager)
        Private ReadOnly _logParsers As New ConcurrentDictionary(Of String, ActiveLogParser)
        Private ReadOnly _logStreamCancellations As New ConcurrentDictionary(Of String, CancellationTokenSource)
        Private ReadOnly _liveStates As New ConcurrentDictionary(Of String, InstanceStatusResponse)

        ' Guards the cancel-old / install-new transition inside
        ' StartLogStream so two concurrent callers (typically
        ' StartInstanceAsync's success path racing with
        ' BackgroundPollLoopAsync's stream-health check) can't
        ' both end up with a live background task. The dict-set
        ' is technically atomic on its own, but "cancel previous,
        ' then install new" is two operations and needs to be one.
        ' Without this, the previous task keeps streaming SSE
        ' indefinitely — its cts is no longer referenced by the
        ' dict, but nothing has called Cancel() on it, so the
        ' task just runs forever. Every log line then arrives via
        ' two independent SSE subscribers, both write to the
        ' manager ring buffer, and the manager UI sees every
        ' line twice for the rest of the instance's lifetime.
        ' Symptom is uniform per-line doubling that persists
        ' across slow steady-state lines (not just startup
        ' bursts), starting right after an instance restart.
        Private ReadOnly _logStreamLock As New Object()

        ' Per-instance gate so that concurrent RefreshInstanceStateAsync
        ' calls (the UI panel + the background poller) can't both
        ' observe the same Running->Crashed transition and double-fire
        ' the crash notification.
        Private ReadOnly _refreshLocks As New ConcurrentDictionary(Of String, SemaphoreSlim)

        ' Per-node connection-failure suppression state for log
        ' dedup. When a node goes offline, every 3-second poll
        ' (UI panel timer + background loop, on every instance
        ' that node hosts) would otherwise produce a fresh
        ' "Failed to refresh state" warning — for an operator
        ' with a 4-instance node down, that's 8 warnings per 3s
        ' going to disk. We log the first failure once, suppress
        ' subsequent identical failures from any instance on the
        ' same node, and emit a "back online" line on first
        ' success after a downtime. A heartbeat warning every
        ' FailureHeartbeatMinutes ensures an operator who
        ' arrives later still sees the node's still down rather
        ' than only seeing the original offline-event line
        ' scrolled off the top of the log.
        '
        ' Keyed by NodeId so the dedup is per-node, not per-
        ' instance — a 4-instance node going down produces ONE
        ' warning, not four.
        Private ReadOnly _nodeFailureStates As New ConcurrentDictionary(Of String, NodeFailureState)
        Private Const FailureHeartbeatMinutes As Integer = 5

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

        ' Adoption fallback cache for ResolveSessionIdentity.
        '
        ' Filled from an open SessionHost row when the in-memory
        ' parser hasn't (yet) observed the 4-line tile-load sequence
        ' that drives CurrentSessionIdentity. Typical trigger: the
        ' manager creates a fresh parser when reconnecting to a
        ' running instance (adoption, or manager restart against a
        ' long-lived game), and UE4 won't re-emit "Started hosting
        ' tile" / realm_id / tile_name / tile_id because the tile
        ' was loaded hours ago. The lines exist in the node's log
        ' file but get rotated out of the SSE ring buffer (4096
        ' lines) by the time the manager subscribes — so the
        ' parser never sees them.
        '
        ' Without this cache, every chat row, join, and leave
        ' persisted post-reconnect gets stamped with the
        ' {gameId}:{instanceId} fallback, orphaning them from the
        ' original session's History timeline. With it, we look
        ' up the SessionHost row left over from the original tile
        ' load — it carries the real lastoasis:realm:tile identity
        ' — and keep using it until the parser produces its own.
        '
        ' Invalidated automatically the moment the parser's
        ' CurrentSessionIdentity becomes non-empty (parser has
        ' caught up and is authoritative again) and explicitly on
        ' instance stop via ClearPlayerTracking.
        Private ReadOnly _adoptedSessionIdentities As New ConcurrentDictionary(Of String, String)

        ' Restart coordinator handle — late-bound by ManagerProgram
        ' after both singletons exist, to break the construction
        ' cycle documented on RestartCoordinator itself. Nothing
        ' until attached; all usage sites null-check.
        Private _restartCoordinator As RestartCoordinator

        Public Sub New(clientFactory As NodeHttpClientFactory,
                       pluginRegistry As PluginRegistry,
                       credentialService As CredentialService,
                       sharedConfigService As SharedConfigService,
                       emitter As NotificationEmitter,
                       logger As ILogger(Of InstanceManager))
            _clientFactory = clientFactory
            _pluginRegistry = pluginRegistry
            _credentialService = credentialService
            _sharedConfigService = sharedConfigService
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

                ' Resolve the node's HTTP client up front so we can
                ' fetch its OS platform before invoking any plugin
                ' methods. The platform answer goes onto InstanceConfig
                ' so plugins return platform-specific paths /
                ' executable names directly instead of emitting dual
                ' candidates the manager has to probe. The version-
                ' fetch is cached per-client by NodeHttpClient, so
                ' second-and-subsequent starts against the same node
                ' don't hit the wire.
                Dim client = _clientFactory.GetClient(
                    nodeEntity.NodeId, nodeEntity.HostAddress,
                    nodeEntity.Port, nodeEntity.AuthToken)
                Dim nodePlatform = Await NodePlatformResolver.ResolveAsync(client, CancellationToken.None)

                ' Resolve plugin
                Dim plugin = _pluginRegistry.GetPlugin(instanceEntity.GameId)

                ' Phase 5h three-layer merge: shared-config group
                ' (e.g. LO Realm — when plugin implements
                ' ISharedConfigProvider and the installation links
                ' to a group) → installation → instance. Higher
                ' layers override lower ones on key collision, but
                ' an empty value at an upper layer doesn't wipe
                ' a non-empty value already set by a lower one —
                ' otherwise a blank field in the Edit Instance
                ' form would clobber the shared install / group
                ' value the operator carefully set elsewhere.
                Dim customFields = MergeConfigLayers(db, installEntity, instanceEntity)

                ' Build instance config
                Dim instanceConfig As New InstanceConfig With {
                    .InstanceId = instanceId,
                    .GameId = instanceEntity.GameId,
                    .DisplayName = instanceEntity.DisplayName,
                    .InstallationId = instanceEntity.InstallationId,
                    .WorkingDirectory = installEntity.InstallPath,
                    .CustomFields = customFields,
                    .Platform = nodePlatform
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
                                    ' JoinNodePath instead of
                                    ' Path.Combine: the manager's
                                    ' Path.DirectorySeparatorChar is
                                    ' '\' (Windows), but the install
                                    ' path lives on the NODE — which
                                    ' may be Linux. Pass the resolved
                                    ' nodePlatform so the join uses
                                    ' the right separator without
                                    ' guessing from path shape.
                                    candidates.Add(JoinNodePath(nodePlatform, installEntity.InstallPath, relPath))
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
                                   Not IsRootedOnEitherPlatform(resolved) Then
                                    ' Same JoinNodePath rationale as
                                    ' the executable candidates above.
                                    resolved = JoinNodePath(nodePlatform, installEntity.InstallPath, resolved)
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
                '
                ' On MinRestartDelayMs default (5000 below): floors
                ' the post-crash respawn delay so the manager's
                ' 3-second background poller (BackgroundPollIntervalMs)
                ' reliably observes the Running → Crashed transition
                ' and fires the InstanceCrashed notification BEFORE
                ' the node respawns the process. Worst case the
                ' previous poll happened immediately before the
                ' crash, so the next poll is up to 3000ms away —
                ' 5000ms gives a 2000ms safety margin for that poll
                ' plus the automation engine's dispatch latency. The
                ' same margin covers the Running → Crashed → CrashLoopHalted
                ' fast path: each Crashed visit must outlast one poll
                ' interval so the per-crash notifications fire
                ' individually instead of collapsing into just the
                ' terminal CrashLoopDetected event.
                '
                ' Without this floor, RestartImmediately is essentially
                ' instantaneous and even RestartWithBackoff has only
                ' 2^0 = 1s on the first crash — under the 3s poll
                ' cadence, the poller often never sees the Crashed
                ' state at all and the notification is lost.
                '
                ' Users who want different timing (a game that should
                ' respawn instantly because crashes are expected, or
                ' a game that needs longer settle time) override via
                ' customFields["MinRestartDelayMs"] on the instance.
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
                            "MinRestartDelayMs", 5000),
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
        ''' resolved in priority order:
        '''
        '''   1. Instance-level "GracefulTimeoutMs" custom field. Lets
        '''      operators override per-instance without changing the
        '''      plugin (e.g. a Conan realm with a small world that
        '''      doesn't need the plugin's default 90s).
        '''
        '''   2. Plugin's ILaunchOptionsProvider.GracefulShutdownTimeoutMs.
        '''      Engine-specific default the plugin set in its
        '''      LaunchOptions — e.g. Conan returns 90000 because
        '''      UE5 shutdown with persistent world state has a
        '''      30–60-second productive ceiling and Conan
        '''      frequently hangs requiring force-kill anyway,
        '''      so a too-long timeout just delays the inevitable.
        '''
        '''   3. Universal 25000ms fallback. Matches what every
        '''      pre-LaunchOptions plugin gets and what the contract's
        '''      StopInstanceRequest documents.
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
                ' Look up the per-instance custom override first.
                ' Distinguish "field unset" from "field set to a
                ' parseable value" so the plugin fallback only fires
                ' when the user genuinely hasn't expressed a
                ' preference. GetIntField conflates both into its
                ' default-value return, which is fine when the
                ' default IS the fallback you want, but wrong here
                ' because we want a different fallback (plugin) when
                ' the field is missing.
                Dim fields = GetMergedCustomFields(instanceId)
                Dim raw As String = Nothing
                Dim userOverride As Integer = 0
                Dim hasUserOverride = fields IsNot Nothing AndAlso
                                       fields.TryGetValue("GracefulTimeoutMs", raw) AndAlso
                                       Not String.IsNullOrWhiteSpace(raw) AndAlso
                                       Integer.TryParse(raw.Trim(), userOverride) AndAlso
                                       userOverride > 0

                If hasUserOverride Then
                    effectiveTimeoutMs = userOverride
                Else
                    effectiveTimeoutMs = ResolvePluginGracefulTimeoutMs(instanceId, 25000)
                End If
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
                '
                ' Order within Finally: ClearPlayerTracking BEFORE
                ' StopLogStream. ClearPlayerTracking flushes any
                ' tracked players as synthetic leave events via
                ' PersistPlayerObservation, which calls
                ' ResolveSessionIdentity — and that resolver reads the
                ' parser's CurrentSessionIdentity from _logParsers.
                ' StopLogStream removes that parser entry, after which
                ' the resolver falls back to {gameId}:{instanceId}.
                ' For LO that fallback differs from the real
                ' realm:tile session identity the joins were stamped
                ' with, so flushing AFTER StopLogStream would orphan
                ' the synthetic leaves in the History timeline. The
                ' Factorio fallback happens to match the real format,
                ' so this only bites LO — but cheap to get right for
                ' both regardless.
                ClearPlayerTracking(instanceId)
                StopLogStream(instanceId)
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
                    ' Refresh the node's parse rule set before
                    ' streaming. The node's EventStore is in-memory
                    ' only; a node binary update or restart between
                    ' the original StartInstance call and this
                    ' reconnect wiped the rule list, so without this
                    ' push the instance would keep running with zero
                    ' rules registered — players would appear to
                    ' vanish, chat wouldn't persist, server-state
                    ' transitions wouldn't be detected. Failure is
                    ' silent on nodes older than the parse-rules
                    ' endpoint (logged at Debug, reconnect proceeds).
                    Await ReregisterParseRulesAsync(client, instanceId)
                    StartLogStream(instanceId, client)
                    _logger.LogInformation("Reconnected log stream for {Id}", instanceId)
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to ensure log stream for {Id}", instanceId)
            End Try
        End Function

        ''' <summary>
        ''' Push the plugin-derived parse rule set to the node for
        ''' an instance that's already running on it. Used by the
        ''' reconnect path in EnsureLogStreamAsync so a node binary
        ''' update or Manager restart doesn't strand running game
        ''' processes with an empty EventStore rule list. Replaces
        ''' the older operator-facing workflow of stopping and
        ''' restarting every instance just to refresh rules — a
        ''' game-server restart kicks every player off, and the
        ''' new path doesn't.
        '''
        ''' Silently no-ops in three cases:
        '''   1. Plugin unavailable or its GetLogParseRules throws.
        '''   2. Node returns 404 from the parse-rules endpoint —
        '''      older node binaries don't have it. The manager
        '''      treats this as "this older node needs an instance
        '''      restart to refresh rules" and proceeds with the
        '''      reconnect; rules already on the node from the
        '''      previous StartInstance keep applying.
        '''   3. Any other exception — logged at Warning and
        '''      swallowed so reconnect can still complete.
        ''' </summary>
        Private Async Function ReregisterParseRulesAsync(client As INodeClient,
                                                          instanceId As String) As Task
            Try
                Dim gameId = GetGameIdForInstance(instanceId)
                If String.IsNullOrEmpty(gameId) Then Return
                Dim plugin = _pluginRegistry.GetPlugin(gameId)
                If plugin Is Nothing Then Return

                Dim rules As New List(Of LogParseRule)
                Try
                    Dim pluginRules = plugin.GetLogParseRules()
                    If pluginRules IsNot Nothing Then
                        For Each r In pluginRules
                            rules.Add(r)
                        Next
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex, "Plugin failed to report parse rules for {Id}", instanceId)
                    Return
                End Try

                ' Empty rule set is meaningless to push — cheaper
                ' to skip than to risk wiping whatever the node
                ' currently has registered, which on a normal
                ' Manager-restart scenario could still be the right
                ' set from a previous (now-reloaded) plugin version.
                If rules.Count = 0 Then Return

                Await client.UpdateParseRulesAsync(instanceId, rules, CancellationToken.None)
                _logger.LogDebug("Re-pushed {Count} parse rule(s) to node for {Id}",
                                 rules.Count, instanceId)
            Catch ex As NodeApiException When ex.StatusCode.HasValue AndAlso
                                              ex.StatusCode.Value = HttpStatusCode.NotFound
                ' Older node without the parse-rules endpoint.
                ' Non-fatal: reconnect proceeds, instance keeps
                ' using whatever rules the node had at last
                ' StartInstance. Operator's fix is to upgrade the
                ' node binary or restart the instance manually.
                _logger.LogDebug(
                    "Parse-rules endpoint not available on node for {Id} — older node, skipping rule refresh",
                    instanceId)
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to re-push parse rules for {Id}", instanceId)
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

                ' Successful poll — clear any prior connection-
                ' failure state for this node and announce "back
                ' online" if we'd previously suppressed warnings.
                NoteNodeReachable(instanceId)

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

                ' Transition INTO any terminal state — flush any
                ' tracked players as synthetic leave events. Catches
                ' the crash and crash-loop paths where StopInstanceAsync's
                ' Finally (which also calls ClearPlayerTracking) wasn't
                ' the path that took the instance down. Idempotent: a
                ' user-initiated stop flushes via the Finally first,
                ' and this callsite then sees an empty bucket and no-ops.
                ' Doesn't depend on _emitter being non-null — the flush
                ' is about persistence, not notifications.
                If previous IsNot Nothing AndAlso result IsNot Nothing Then
                    Dim prevState2 = previous.CurrentState
                    Dim newState2 = result.CurrentState
                    Dim isTerminal = (newState2 = GSM.Plugin.InstanceState.Stopped OrElse
                                      newState2 = GSM.Plugin.InstanceState.Crashed OrElse
                                      newState2 = GSM.Plugin.InstanceState.CrashLoopHalted)
                    Dim wasNotTerminal = (prevState2 <> GSM.Plugin.InstanceState.Stopped AndAlso
                                          prevState2 <> GSM.Plugin.InstanceState.Crashed AndAlso
                                          prevState2 <> GSM.Plugin.InstanceState.CrashLoopHalted)
                    If isTerminal AndAlso wasNotTerminal Then
                        Try
                            ClearPlayerTracking(instanceId)
                        Catch ex As Exception
                            _logger.LogDebug(ex,
                                "ClearPlayerTracking on terminal-state transition threw for {Id}",
                                instanceId)
                        End Try
                    End If
                End If

                Return result
            Catch ex As Exception
                ' Connection-level failures get suppressed per-node
                ' so a downed node doesn't spam this warning every
                ' 3s on every instance it hosts; API-level failures
                ' (HTTP 500/404/etc.) log normally since they're
                ' usually one-offs worth seeing every time.
                If IsConnectionFailure(ex) Then
                    NoteNodeNetworkFailure(instanceId, ex)
                Else
                    _logger.LogWarning(ex, "Failed to refresh state for {Id}", instanceId)
                End If
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

                            ' Stream-health reconnect. If the state
                            ' poll just returned Running but we have
                            ' no active log stream for this instance,
                            ' the previous stream died (typically
                            ' because the node restarted out from
                            ' under us) and the StreamLogsInBackgroundAsync
                            ' Finally already cleaned up its dict
                            ' entry. Trigger a reconnect attempt now
                            ' so the manager catches back up to a
                            ' live stream without waiting for the
                            ' user to manually reopen the log viewer.
                            ' EnsureLogStreamAsync is idempotent —
                            ' if a stream is already active it returns
                            ' immediately, so calling it every poll
                            ' tick for healthy instances is cheap.
                            Try
                                Dim live = GetLiveState(id)
                                If live IsNot Nothing AndAlso
                                   live.CurrentState = GSM.Plugin.InstanceState.Running AndAlso
                                   Not _logStreamCancellations.ContainsKey(id) Then
                                    Await EnsureLogStreamAsync(id)
                                End If
                            Catch ex As Exception
                                _logger.LogDebug(ex,
                                    "Stream-health reconnect check failed for {Id}", id)
                            End Try
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
                        .DisplayName = msg.DisplayName,
                        .PlatformUserId = msg.PlatformUserId,
                        .CharacterId = msg.CharacterId,
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
        '''
        ''' UTC kind is FORCED on the returned value. EF Core's
        ''' SQLite provider stores DateTime as TEXT and reads it
        ''' back with Kind=Unspecified — the kind information from
        ''' the original Utc value is dropped on the round trip.
        ''' That kind matters downstream: NodeHttpClient serializes
        ''' cursors via DateTime.ToString("o"), which only emits the
        ''' "Z" suffix when Kind=Utc. An Unspecified-kind cursor
        ''' serializes as "2026-05-03T00:03:57.0000000" (no Z), the
        ''' node parses it with RoundtripKind and then calls
        ''' ToUniversalTime() — which TREATS UNSPECIFIED AS LOCAL
        ''' and shifts the time by the manager's UTC offset. For a
        ''' user in CDT, a cursor of 00:03:57 becomes 05:03:57 on
        ''' the node's side, and any chat persisted between those
        ''' two times gets silently filtered out of the response.
        ''' Tagging the cursor as Utc here propagates the right
        ''' suffix all the way through the chain. Column is named
        ''' TimestampUtc and is always written from DateTime.UtcNow,
        ''' so this isn't a guess — it's restoring lost metadata.
        ''' </summary>
        Private Function SeedChatCursor(instanceId As String) As DateTime
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim latest = db.ChatMessages.
                        Where(Function(c) c.InstanceId = instanceId).
                        Select(Function(c) CType(c.TimestampUtc, DateTime?)).
                        Max()
                    If Not latest.HasValue Then Return DateTime.MinValue
                    Return DateTime.SpecifyKind(latest.Value, DateTimeKind.Utc)
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
                ' Skip instances whose node is detached — a
                ' detached node has explicitly opted out of the
                ' background polling cycle. Existing log streams
                ' to a detached node continue independently; this
                ' only suppresses the 3-second status refresh
                ' loop that would otherwise spam disconnect
                ' banners when the operator points the manager
                ' at a remote node that's offline. Explicit
                ' navigation to an InstancePanel still triggers
                ' on-demand refresh — the gate only applies here
                ' in the background loop.
                Dim pollable = From inst In db.Instances
                               Join install In db.Installations
                                   On inst.InstallationId Equals install.InstallationId
                               Join nodeEnt In db.Nodes
                                   On install.NodeId Equals nodeEnt.NodeId
                               Where nodeEnt.IsEnabled
                               Select inst.InstanceId
                For Each id In pollable.ToList()
                    ids.Add(id)
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

        ''' <summary>
        ''' Establish a new SSE log stream for an instance, atomically
        ''' cancelling any previous stream first. Idempotent: redundant
        ''' calls (e.g. StartInstanceAsync's success path racing with
        ''' BackgroundPollLoopAsync's stream-health check) collapse into
        ''' a single live task instead of leaking the previous one.
        '''
        ''' Concurrency model: the cancel-and-replace transition is
        ''' wrapped in SyncLock(_logStreamLock) so concurrent callers
        ''' serialise on the same lock. Whichever caller enters second
        ''' sees the first caller's cts in the dict, cancels it, and
        ''' installs its own. The first caller's task receives the
        ''' cancellation, exits, and its Finally block's compare-and-
        ''' remove finds a mismatched cts (the second caller's now in
        ''' the dict) and bails — exactly the right semantics.
        '''
        ''' Task.Run is INSIDE the lock so the parser registration in
        ''' _logParsers happens before the task starts streaming.
        ''' Otherwise the task could start reading lines while parser
        ''' state from a previous run is still in the dict, producing
        ''' transient mis-classification of the first few lines.
        ''' </summary>
        Private Sub StartLogStream(instanceId As String, client As INodeClient)
            SyncLock _logStreamLock
                ' Cancel any existing stream BEFORE installing the
                ' new one. TryRemove + Cancel + Dispose explicitly,
                ' rather than just overwriting the dict and hoping
                ' GC closes the previous cts — the previous task is
                ' awaiting a cancellation that will never come if
                ' we don't Cancel(), and would stream forever.
                Dim existingCts As CancellationTokenSource = Nothing
                If _logStreamCancellations.TryRemove(instanceId, existingCts) Then
                    Try
                        existingCts.Cancel()
                    Catch
                    End Try
                    Try
                        existingCts.Dispose()
                    Catch
                    End Try
                    _logger.LogDebug(
                        "StartLogStream replaced an existing stream for {Id}", instanceId)
                End If

                ' Drop any stale parser entry too. A previous run's
                ' parser may still be in _logParsers if the previous
                ' task hadn't yet reached its Finally — explicit
                ' removal here keeps the parser slot in sync with
                ' the cts slot. CreateParser below installs the
                ' fresh one.
                Dim staleParser As ActiveLogParser = Nothing
                _logParsers.TryRemove(instanceId, staleParser)

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

                ' Stream in background. Pass the cts itself (not just its
                ' token) so the background task can clean up its OWN entry
                ' from _logStreamCancellations when the stream ends — see
                ' the Finally block in StreamLogsInBackgroundAsync for why
                ' the compare-and-remove pattern matters.
                Task.Run(Function() StreamLogsInBackgroundAsync(instanceId, client, parser, cts))
            End SyncLock
        End Sub

        Private Async Function StreamLogsInBackgroundAsync(instanceId As String,
                                                            client As INodeClient,
                                                            parser As ILogParser,
                                                            ourCts As CancellationTokenSource) As Task
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
                    End Sub, ourCts.Token)
            Catch ex As OperationCanceledException
                ' Normal
            Catch ex As Exception
                _logger.LogWarning(ex, "Log stream ended for {Id}", instanceId)
            Finally
                ' Clean up our entry from _logStreamCancellations so a
                ' subsequent EnsureLogStreamAsync call (from the
                ' background poll-loop's stream-health check, or from
                ' a user reopening the log viewer) can establish a
                ' fresh stream. Without this, the dict still holds
                ' the dead cts after the stream task terminates,
                ' EnsureLogStreamAsync's "already-streaming" guard
                ' returns early, and the manager silently never
                ' reconnects — visible symptom: node restart → logs
                ' freeze → stay frozen forever even after the node is
                ' fully back up.
                '
                ' Compare-and-remove: only remove if the entry in the
                ' dict is still OUR cts. If a concurrent reconnect
                ' (e.g. background poll noticed the stream gap and
                ' already called EnsureLogStreamAsync) has already
                ' swapped in a fresh cts, leave that fresh one alone.
                ' Without this guard the new stream would inherit a
                ' missing-entry state on the very first poll cycle.
                Try
                    Dim current As CancellationTokenSource = Nothing
                    If _logStreamCancellations.TryGetValue(instanceId, current) AndAlso
                       ReferenceEquals(current, ourCts) Then
                        _logStreamCancellations.TryRemove(instanceId, current)
                        Try : current.Dispose() : Catch : End Try
                    End If
                Catch
                End Try
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
        '''
        ''' Before clearing, flushes each tracked player as a
        ''' synthetic "leave" event into PlayerActivity so the
        ''' History timeline shows matching leaves for the joins
        ''' instead of dangling joins-with-no-leaves. When an
        ''' instance stops — gracefully, by crash, or by force-kill
        ''' — the players that were online are functionally
        ''' disconnected; the History should reflect that.
        '''
        ''' Persist-only — the InstanceStopped / InstanceCrashed
        ''' notification already covers "what happened" at the
        ''' event level, so per-player PlayerLeft notifications on
        ''' top of that would just be noise (and could spam Discord
        ''' badly when a populated server stops).
        '''
        ''' SessionIdentity caveat: this method must be called
        ''' BEFORE StopLogStream tears down the parser, because
        ''' PersistPlayerObservation relies on ResolveSessionIdentity
        ''' which reads the parser's CurrentSessionIdentity. Once
        ''' the parser is removed from _logParsers, that resolver
        ''' falls back to {gameId}:{instanceId}, which would
        ''' persist the synthetic leave under a different
        ''' SessionIdentity than the join — orphaning both in the
        ''' History timeline. StopInstanceAsync.Finally calls
        ''' ClearPlayerTracking before StopLogStream for this
        ''' reason; RefreshInstanceStateAsync's terminal-state
        ''' callsite doesn't tear down the stream at all.
        ''' </summary>
        Private Sub ClearPlayerTracking(instanceId As String)
            ' Drain the bucket atomically. Removing the entry from
            ' _activePlayers under the same SyncLock prevents a
            ' join arriving mid-flush from re-populating the same
            ' bucket reference we're about to clear — a join after
            ' this point creates a fresh bucket via GetOrAdd and
            ' is preserved for the next stop (rare but possible if
            ' the log stream is still feeding events during the
            ' graceful-shutdown window).
            Dim namesToFlush As List(Of String) = Nothing
            Dim bucket As HashSet(Of String) = Nothing
            If _activePlayers.TryRemove(instanceId, bucket) AndAlso
               bucket IsNot Nothing Then
                SyncLock bucket
                    If bucket.Count > 0 Then
                        namesToFlush = bucket.ToList()
                        bucket.Clear()
                    End If
                End SyncLock
            End If

            If namesToFlush IsNot Nothing AndAlso namesToFlush.Count > 0 Then
                For Each name In namesToFlush
                    Try
                        PersistPlayerObservation(instanceId, name, isJoin:=False)
                    Catch ex As Exception
                        _logger.LogDebug(ex,
                            "Synthetic leave persist failed for {Id}/{Name}",
                            instanceId, name)
                    End Try
                Next
                _logger.LogInformation(
                    "Flushed {Count} player(s) as synthetic leave on stop for {Id}",
                    namesToFlush.Count, instanceId)
            End If

            Dim removedTs As DateTime
            _lastEmptyLeaveAt.TryRemove(instanceId, removedTs)

            ' Also drop the adoption-fallback session-identity cache
            ' so a subsequent start (fresh tile load) doesn't pick
            ' up the stale identity from the previous run before
            ' the new parser commits its own.
            Dim removedAdoptedIdentity As String = Nothing
            _adoptedSessionIdentities.TryRemove(instanceId, removedAdoptedIdentity)

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
        ''' Between those two there's a third source: an open
        ''' SessionHost row from a previous parser instance. Used
        ''' when the current parser hasn't observed the tile-load
        ''' sequence — typical on reconnect/adoption since UE4 only
        ''' emits "Started hosting tile" once per tile and that line
        ''' has long since rotated out of the SSE ring buffer. This
        ''' lookup keeps post-reconnect persistence stamped with the
        ''' same identity the original session used, so History
        ''' timeline rows for one logical session stay grouped.
        '''
        ''' Returns Nothing ONLY when we can't determine the gameId —
        ''' shouldn't happen in practice because every running
        ''' instance has a parser registered, but defensive nulls
        ''' mean persistence silently no-ops rather than crashing.
        ''' </summary>
        Private Function ResolveSessionIdentity(instanceId As String) As String
            ' Primary: in-memory parser state (set live by observing
            ' the plugin-specific tile-load sequence).
            Dim activeParser As ActiveLogParser = Nothing
            _logParsers.TryGetValue(instanceId, activeParser)
            If activeParser IsNot Nothing AndAlso activeParser.Parser IsNot Nothing Then
                Dim pluginIdentity = activeParser.Parser.CurrentSessionIdentity
                If Not String.IsNullOrEmpty(pluginIdentity) Then
                    ' Parser caught up — invalidate any adoption
                    ' fallback cache for this instance so future
                    ' tile switches the parser picks up will be
                    ' the authoritative source. Cheap no-op when
                    ' the cache was empty.
                    Dim discarded As String = Nothing
                    _adoptedSessionIdentities.TryRemove(instanceId, discarded)
                    Return pluginIdentity
                End If
            End If

            ' Cached adoption fallback — avoids hitting the DB on
            ' every chat-mirror tick (every 5s while a tile is
            ' loaded post-reconnect).
            Dim cached As String = Nothing
            If _adoptedSessionIdentities.TryGetValue(instanceId, cached) AndAlso
               Not String.IsNullOrEmpty(cached) Then
                Return cached
            End If

            ' DB lookup — the open SessionHost row left over from
            ' the original tile load carries the realm:tile identity
            ' the parser would have committed if it had observed the
            ' sequence live.
            Dim resolved = LookupOpenSessionHostIdentity(instanceId)
            If Not String.IsNullOrEmpty(resolved) Then
                _adoptedSessionIdentities(instanceId) = resolved
                Return resolved
            End If

            ' Final fallback: {gameId}:{instanceId}
            Dim gameId = GetGameIdForInstance(instanceId)
            If String.IsNullOrEmpty(gameId) Then Return Nothing
            Return $"{gameId}:{instanceId}"
        End Function

        ''' <summary>
        ''' Look up the SessionIdentity of the most recent open
        ''' SessionHost row for this instance. Used by
        ''' ResolveSessionIdentity's adoption fallback path. Returns
        ''' Nothing when no open row exists — either the instance
        ''' was stopped cleanly (HostedUntilUtc closed by
        ''' HandleTileUnloaded / ClearPlayerTracking) or it was
        ''' never running long enough to commit a session.
        ''' </summary>
        Private Function LookupOpenSessionHostIdentity(instanceId As String) As String
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = db.SessionHosts.
                        Where(Function(h) h.InstanceId = instanceId AndAlso
                                          h.HostedUntilUtc Is Nothing).
                        OrderByDescending(Function(h) h.HostedFromUtc).
                        FirstOrDefault()
                    If row IsNot Nothing Then Return row.SessionIdentity
                    Return Nothing
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex,
                    "LookupOpenSessionHostIdentity failed for {Id}", instanceId)
                Return Nothing
            End Try
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
        ''' Sync entry point invoked from the SSE log-stream
        ''' callback (HandlePlayerJoin / HandlePlayerLeave) and
        ''' from the stop-path flush (ClearPlayerTracking).
        ''' Resolves session identity synchronously up-front and
        ''' fires the rest — the Node /players wire call + DB
        ''' writes — asynchronously on the thread pool.
        '''
        ''' Fire-and-forget is deliberate. The wire call to the
        ''' Node's /players endpoint can take tens to hundreds of
        ''' milliseconds on a slow link, and we cannot stall the
        ''' SSE log-stream reader on it: the reader is single-
        ''' threaded and any per-line latency directly delays
        ''' downstream log processing. The async path's exceptions
        ''' are logged inside it; there is nothing for the caller
        ''' to wait on.
        '''
        ''' Snapshotting sessionIdentity here matters specifically
        ''' for the ClearPlayerTracking flush path. ClearPlayerTracking
        ''' runs in the Finally of StopInstanceAsync and is followed
        ''' immediately by StopLogStream, which tears down the
        ''' parser that ResolveSessionIdentity reads from. If the
        ''' async persist did its own resolution later, it would
        ''' miss the parser and fall back to the
        ''' {gameId}:{instanceId} fallback identity for the
        ''' synthetic leave rows — orphaning them from the join
        ''' rows in the History timeline. Capturing the identity
        ''' here, while the parser is still alive, makes the
        ''' async path's identity assignment deterministic
        ''' regardless of stop-sequence timing.
        ''' </summary>
        Private Sub PersistPlayerObservation(instanceId As String,
                                              playerName As String,
                                              isJoin As Boolean)
            Dim sessionIdentity = ResolveSessionIdentity(instanceId)
            If String.IsNullOrEmpty(sessionIdentity) Then Return

            ' Fire-and-forget. Discard variable holds the Task
            ' reference so VB's "Function called as Sub" doesn't
            ' fail compile; we have nothing to await on. Errors
            ' inside the async path are logged there.
            Dim _persistTask = PersistPlayerObservationAsync(
                instanceId, playerName, isJoin, sessionIdentity)
        End Sub

        ''' <summary>
        ''' Async core for PersistPlayerObservation. Three steps:
        '''
        '''   1. Enrich identity columns via wire call to the
        '''      Node's /players endpoint. Best-effort: a missed
        '''      lookup falls back to NULL CharacterId /
        '''      PlatformUserId / DisplayName on the activity row
        '''      and HistoryQueryService rendering's
        '''      IdentityFormatter.Format coalesces gracefully to
        '''      PlayerName.
        '''
        '''      The common miss case is PlayerLeave: the Node's
        '''      EventStore removes the session from its in-memory
        '''      dict on the same log line the Manager is
        '''      processing, so by the time our HTTP request
        '''      resolves, /players no longer contains the leaving
        '''      player. PlayerJoin almost always hits because the
        '''      Node adds the session BEFORE the SSE forwarding
        '''      reaches the Manager. We accept the leave-time
        '''      asymmetry rather than maintain a Manager-side
        '''      identity cache for what is a documented
        '''      fallback-to-NULL path.
        '''
        '''   2. UPSERT PlayerSessions for the per-player
        '''      aggregate summary (first/last seen, last host
        '''      instance). Unchanged from pre-5g-2 — the summary
        '''      table is name-keyed; the new identity columns
        '''      live only on PlayerActivity.
        '''
        '''   3. Append a PlayerActivity row stamped with the
        '''      resolved identity columns from step 1.
        '''
        ''' sessionIdentity is passed in by the sync wrapper
        ''' (snapshotted while the parser was still alive) rather
        ''' than resolved here — see PersistPlayerObservation's
        ''' doc comment for the rationale.
        '''
        ''' Identity match by either PlatformPersona OR DisplayName:
        ''' the Manager's parser delivers PlayerName as the raw
        ''' login-line string (Steam persona on LO), which matches
        ''' PlayerSession.PlatformPersona. Chat-derived verdicts
        ''' (if a future plugin routes them through PlayerJoin)
        ''' would carry the in-game DisplayName instead. Trying
        ''' both keeps the match working across both surfaces.
        ''' </summary>
        Private Async Function PersistPlayerObservationAsync(instanceId As String,
                                                              playerName As String,
                                                              isJoin As Boolean,
                                                              sessionIdentity As String) As Task
            Try
                Dim now = DateTime.UtcNow
                Dim nodeId = GetNodeIdForInstance(instanceId)

                ' ---- Identity enrichment from Node's /players ----
                Dim resolvedCharacterId As String = Nothing
                Dim resolvedPlatformUserId As String = Nothing
                Dim resolvedDisplayName As String = Nothing
                Try
                    Dim client = GetClientForInstance(instanceId)
                    If client IsNot Nothing Then
                        Dim sessions = Await client.GetPlayersAsync(instanceId, CancellationToken.None)
                        If sessions IsNot Nothing Then
                            Dim matched As PlayerSession = Nothing
                            For Each s In sessions
                                If s Is Nothing Then Continue For
                                Dim personaMatch = Not String.IsNullOrEmpty(s.PlatformPersona) AndAlso
                                                    String.Equals(s.PlatformPersona, playerName,
                                                                  StringComparison.OrdinalIgnoreCase)
                                Dim displayMatch = Not String.IsNullOrEmpty(s.DisplayName) AndAlso
                                                    String.Equals(s.DisplayName, playerName,
                                                                  StringComparison.OrdinalIgnoreCase)
                                If personaMatch OrElse displayMatch Then
                                    matched = s
                                    Exit For
                                End If
                            Next
                            If matched IsNot Nothing Then
                                resolvedCharacterId = matched.CharacterId
                                resolvedPlatformUserId = matched.PlatformUserId
                                resolvedDisplayName = matched.DisplayName
                            End If
                        End If
                    End If
                Catch ex As Exception
                    ' Wire-call failures (node unreachable,
                    ' timeout, etc.) degrade gracefully to NULL
                    ' identity columns. Logged at Debug because
                    ' they're expected on PlayerLeave events and
                    ' during transient network failures — Warning
                    ' would spam the log uselessly.
                    _logger.LogDebug(ex,
                        "Identity enrichment failed for {Id}/{Name} — persisting with NULL identity",
                        instanceId, playerName)
                End Try

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
                        .EventKind = If(isJoin, "join", "leave"),
                        .CharacterId = resolvedCharacterId,
                        .PlatformUserId = resolvedPlatformUserId,
                        .DisplayName = resolvedDisplayName
                    })

                    db.SaveChanges()
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex, "PersistPlayerObservation failed for {Id}/{Name}",
                                 instanceId, playerName)
            End Try
        End Function

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
        ''' Cross-platform path join for paths that target the NODE
        ''' rather than the manager's local filesystem. Plain
        ''' IO.Path.Combine bakes in the manager's
        ''' Path.DirectorySeparatorChar — always '\' on the
        ''' Windows-hosted manager — which corrupts paths bound for
        ''' a Linux node ("/opt/factorio" + "\" + "bin/x64/factorio"
        ''' → "/opt/factorio\bin/x64/factorio", which Linux opens
        ''' as a literal filename containing backslashes).
        '''
        ''' The platform parameter is the node's authoritative OS
        ''' answer from /api/version. NodePlatform.Unknown falls
        ''' back to path-shape detection so older nodes that don't
        ''' carry the field still get a reasonable answer.
        ''' </summary>
        Private Shared Function JoinNodePath(platform As NodePlatform,
                                              installPath As String,
                                              relPath As String) As String
            If String.IsNullOrEmpty(installPath) Then Return If(relPath, "")
            If String.IsNullOrEmpty(relPath) Then Return installPath

            Dim sep As Char
            Select Case platform
                Case NodePlatform.Linux
                    sep = "/"c
                Case NodePlatform.Windows
                    sep = "\"c
                Case Else
                    ' Unknown — fall back to path-shape detection
                    ' so older nodes that don't carry the Platform
                    ' field still get the right answer.
                    Dim hasForward = installPath.IndexOf("/"c) >= 0
                    Dim hasBack = installPath.IndexOf("\"c) >= 0
                    If (hasForward AndAlso Not hasBack) OrElse installPath.StartsWith("/"c) Then
                        sep = "/"c
                    Else
                        sep = "\"c
                    End If
            End Select

            Dim normalizedRel As String
            If sep = "/"c Then
                normalizedRel = relPath.Replace("\"c, "/"c)
            Else
                normalizedRel = relPath.Replace("/"c, "\"c)
            End If

            Return installPath.TrimEnd("/"c, "\"c) & sep & normalizedRel.TrimStart("/"c, "\"c)
        End Function

        ''' <summary>
        ''' Path.IsPathRooted respects only the manager's host OS
        ''' rules, so a Linux-style "/opt/foo" path coming back from
        ''' a node config is correctly recognised as rooted on a
        ''' Windows manager (Path.IsPathRooted does treat '/' as
        ''' rooted on Windows), but a Windows-style "C:\foo" coming
        ''' from a Linux-hosted manager would NOT be — we don't ship
        ''' a Linux manager today, but the symmetry costs a line and
        ''' future-proofs the check. Returns True for any path that
        ''' starts with '/', '\', or a drive letter followed by ':'.
        ''' </summary>
        Private Shared Function IsRootedOnEitherPlatform(path As String) As Boolean
            If String.IsNullOrEmpty(path) Then Return False
            If path(0) = "/"c OrElse path(0) = "\"c Then Return True
            If path.Length >= 2 AndAlso path(1) = ":"c AndAlso
               ((path(0) >= "a"c AndAlso path(0) <= "z"c) OrElse
                (path(0) >= "A"c AndAlso path(0) <= "Z"c)) Then
                Return True
            End If
            Return False
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
        ''' Resolves the plugin's preferred graceful-shutdown
        ''' timeout for an instance via the opt-in
        ''' ILaunchOptionsProvider interface. Returns the fallback
        ''' value on any of:
        '''   • instance row not found (deleted between stop
        '''     request and resolution)
        '''   • plugin not loaded (was unloaded or never compiled)
        '''   • plugin doesn't implement ILaunchOptionsProvider
        '''   • plugin implements it but left
        '''     GracefulShutdownTimeoutMs at its -1 sentinel
        '''   • plugin's GetLaunchOptions threw
        '''
        ''' Called from StopInstanceAsync after the per-instance
        ''' "GracefulTimeoutMs" custom-field lookup misses, so the
        ''' plugin's static preference fills the gap instead of
        ''' the universal 25-second hardcoded fallback. See the
        ''' priority block at the top of StopInstanceAsync for
        ''' the full ordering.
        '''
        ''' Plugin's GetLaunchOptions takes an InstanceConfig.
        ''' We pass a minimal shell (InstanceId + merged custom
        ''' fields) since (a) existing plugins don't read any
        ''' other field off InstanceConfig when reporting launch
        ''' options, and (b) reconstructing the full config the
        ''' way StartInstanceAsync does it would mean duplicating
        ''' the schema-merge logic for a fallback path that runs
        ''' once per stop. If a future plugin needs richer
        ''' context here, the shell can grow without changing
        ''' the interface signature.
        ''' </summary>
        Private Function ResolvePluginGracefulTimeoutMs(instanceId As String,
                                                          fallback As Integer) As Integer
            Try
                Dim plugin As IGamePlugin = Nothing
                Dim mergedFields As Dictionary(Of String, String) = Nothing
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim inst = db.Instances.Find(instanceId)
                    If inst Is Nothing Then Return fallback
                    plugin = _pluginRegistry.GetPlugin(inst.GameId)
                End Using
                If plugin Is Nothing Then Return fallback

                Dim provider = TryCast(plugin, ILaunchOptionsProvider)
                If provider Is Nothing Then Return fallback

                mergedFields = GetMergedCustomFields(instanceId)
                Dim shell As New InstanceConfig With {
                    .InstanceId = instanceId,
                    .CustomFields = mergedFields
                }
                Dim opts = provider.GetLaunchOptions(shell)
                If opts Is Nothing Then Return fallback
                If opts.GracefulShutdownTimeoutMs < 0 Then Return fallback
                Return opts.GracefulShutdownTimeoutMs
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to resolve plugin graceful timeout for {Id} — using fallback {Ms}ms",
                    instanceId, fallback)
                Return fallback
            End Try
        End Function

        ''' <summary>
        ''' Phase 5h three-layer config merge. Builds the merged
        ''' CustomFields dict for an instance by stacking up to
        ''' three layers, lowest precedence first:
        '''
        '''   Layer 0 — shared-config group (only when the plugin
        '''            implements ISharedConfigProvider AND the
        '''            installation has a SharedConfigGroupId set).
        '''            E.g. LO's Realm group supplies CustomerKey /
        '''            ProviderKey / RealmName here.
        '''   Layer 1 — installation (Installation.ConfigJson).
        '''   Layer 2 — instance (Instance.ConfigJson).
        '''
        ''' At each transition between layers, an empty value at
        ''' the upper layer does NOT overwrite a non-empty value
        ''' set by a lower layer. This preserves the original
        ''' two-layer rule (a blank override in the Edit Instance
        ''' form doesn't wipe a shared value) symmetrically across
        ''' the new three-layer stack — a blank field in the Edit
        ''' Installation form doesn't wipe a realm-group value
        ''' either.
        '''
        ''' If the plugin isn't loaded, doesn't implement
        ''' ISharedConfigProvider, or the installation has no
        ''' SharedConfigGroupId, Layer 0 is skipped and the merge
        ''' behaves identically to the pre-5h two-layer version.
        ''' Errors loading the group (decrypt failure, missing
        ''' row) log a warning and continue with Layer 1+2 only;
        ''' a half-merged config is preferable to a failed instance
        ''' start.
        ''' </summary>
        Private Function MergeConfigLayers(db As GsmDbContext,
                                           installation As InstallationEntity,
                                           instance As InstanceEntity) As Dictionary(Of String, String)
            Dim merged As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

            ' Layer 0 — shared-config group (opt-in via plugin
            ' interface + installation link).
            If installation IsNot Nothing AndAlso
               Not String.IsNullOrEmpty(installation.SharedConfigGroupId) Then
                Dim plugin = _pluginRegistry.GetPlugin(installation.GameId)
                Dim provider = TryCast(plugin, ISharedConfigProvider)
                If provider IsNot Nothing Then
                    Try
                        Dim schema = provider.GetSharedConfigSchema()
                        Dim groupFields = _sharedConfigService.LoadGroupFieldsPlaintext(
                            db, installation.SharedConfigGroupId, schema)
                        For Each kvp In groupFields
                            merged(kvp.Key) = kvp.Value
                        Next
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "Failed to load shared-config group {GroupId} for installation {Inst}; continuing with install + instance config only",
                            installation.SharedConfigGroupId, installation.InstallationId)
                    End Try
                End If
            End If

            ' Layer 1 — installation.
            If installation IsNot Nothing Then
                Dim installFields = DeserializeConfig(installation.ConfigJson)
                For Each kvp In installFields
                    If String.IsNullOrEmpty(kvp.Value) AndAlso
                       merged.ContainsKey(kvp.Key) AndAlso
                       Not String.IsNullOrEmpty(merged(kvp.Key)) Then
                        Continue For
                    End If
                    merged(kvp.Key) = kvp.Value
                Next
            End If

            ' Layer 2 — instance.
            If instance IsNot Nothing Then
                Dim instanceFields = DeserializeConfig(instance.ConfigJson)
                For Each kvp In instanceFields
                    If String.IsNullOrEmpty(kvp.Value) AndAlso
                       merged.ContainsKey(kvp.Key) AndAlso
                       Not String.IsNullOrEmpty(merged(kvp.Key)) Then
                        Continue For
                    End If
                    merged(kvp.Key) = kvp.Value
                Next
            End If

            Return merged
        End Function

        ''' <summary>
        ''' Loads the instance's CustomFields via the Phase 5h
        ''' three-layer merge (group → installation → instance).
        ''' Both StartInstanceAsync's resolve path and ad-hoc
        ''' callers (refresh, automation evaluation, etc.) use
        ''' MergeConfigLayers so the merge rules can't drift
        ''' between the two routes.
        ''' </summary>
        Private Function GetMergedCustomFields(instanceId As String) As Dictionary(Of String, String)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                Dim inst = db.Instances.Find(instanceId)
                If inst Is Nothing Then Return New Dictionary(Of String, String)

                Dim install = db.Installations.Find(inst.InstallationId)
                Return MergeConfigLayers(db, install, inst)
            End Using
        End Function

        ''' <summary>
        ''' True when the manager has a recent connection-failure
        ''' record for this node (i.e. an entry in
        ''' _nodeFailureStates that hasn't been cleared by a
        ''' subsequent successful poll). Returns False when the
        ''' node is currently reachable, has never been probed,
        ''' or recovered since the last failure was logged.
        '''
        ''' Exposed for the MainForm tree-icon refresh — lets the
        ''' UI render a red status badge for unreachable nodes
        ''' without taking on its own polling responsibility. The
        ''' background poll loop already does the work, this is
        ''' just an authoritative readout.
        ''' </summary>
        Public Function IsNodeKnownUnreachable(nodeId As String) As Boolean
            If String.IsNullOrEmpty(nodeId) Then Return False
            Return _nodeFailureStates.ContainsKey(nodeId)
        End Function

        ' ============================================================
        '  Connection-failure log dedup
        ' ============================================================

        ''' <summary>
        ''' Per-node failure state used by the connection-failure
        ''' log dedup. See _nodeFailureStates for the rationale.
        ''' Mutable fields are touched only inside
        ''' ConcurrentDictionary.AddOrUpdate factory closures, which
        ''' the dictionary serialises per-key — so no SyncLock is
        ''' needed here.
        ''' </summary>
        Private Class NodeFailureState
            Public Property FirstFailureUtc As DateTime
            Public Property LastLoggedUtc As DateTime
            Public Property SuppressedSinceLog As Integer = 0
        End Class

        ''' <summary>
        ''' True when an exception represents a connection-level
        ''' failure (node unreachable, network down, request
        ''' timeout) rather than an API-level one (HTTP 500, 404,
        ''' deserialisation error, etc.). Connection failures are
        ''' suppression-eligible because a downed node produces
        ''' the same error every poll; API failures usually
        ''' represent something an operator wants to see every
        ''' time it happens.
        '''
        ''' HttpClient timeouts surface as TaskCanceledException
        ''' (a subclass of OperationCanceledException) anywhere in
        ''' the inner-exception chain. NodeHttpClient's
        ''' WrapException doesn't recognise those as
        ''' HttpRequestException, so they end up wrapped in
        ''' NodeApiException with the cancellation in the inner.
        ''' Walking the chain catches that case.
        ''' </summary>
        Private Shared Function IsConnectionFailure(ex As Exception) As Boolean
            If ex Is Nothing Then Return False
            If TypeOf ex Is NodeConnectionException Then Return True
            Dim cur = ex
            While cur IsNot Nothing
                If TypeOf cur Is OperationCanceledException Then Return True
                cur = cur.InnerException
            End While
            Return False
        End Function

        ''' <summary>
        ''' Called from RefreshInstanceStateAsync's success path.
        ''' If a failure state was previously recorded for this
        ''' node, emit a "back online" line summarising the
        ''' downtime + suppressed-warning count and clear the
        ''' state. No-op when the node has been up all along.
        ''' </summary>
        Private Sub NoteNodeReachable(instanceId As String)
            Dim nodeId = GetNodeIdForInstance(instanceId)
            If String.IsNullOrEmpty(nodeId) Then Return

            Dim removed As NodeFailureState = Nothing
            If _nodeFailureStates.TryRemove(nodeId, removed) Then
                Dim downtime = DateTime.UtcNow - removed.FirstFailureUtc
                _logger.LogInformation(
                    "Node {NodeId} reachable again (was unreachable for {Downtime}; suppressed {Count} duplicate warning(s))",
                    nodeId, FormatDowntime(downtime), removed.SuppressedSinceLog)
            End If
        End Sub

        ''' <summary>
        ''' Called from RefreshInstanceStateAsync's catch when the
        ''' caught exception is a connection-level failure. Logs
        ''' the first failure for a node at Warning, suppresses
        ''' subsequent failures on the same node until either a
        ''' success arrives (NoteNodeReachable clears the state)
        ''' or the heartbeat interval lapses, at which point a
        ''' fresh Warning summarising the suppressed count goes
        ''' out so the log doesn't go silent on a long outage.
        ''' </summary>
        Private Sub NoteNodeNetworkFailure(instanceId As String, ex As Exception)
            Dim nodeId = GetNodeIdForInstance(instanceId)
            If String.IsNullOrEmpty(nodeId) Then
                ' Can't dedupe without a node key — fall back to
                ' the original behaviour rather than swallowing.
                _logger.LogWarning(ex, "Failed to refresh state for {Id}", instanceId)
                Return
            End If

            Dim nowUtc = DateTime.UtcNow
            Dim shouldLog As Boolean = False
            Dim isFirst As Boolean = False
            Dim suppressedAtLog As Integer = 0

            _nodeFailureStates.AddOrUpdate(nodeId,
                Function(k)
                    shouldLog = True
                    isFirst = True
                    Return New NodeFailureState With {
                        .FirstFailureUtc = nowUtc,
                        .LastLoggedUtc = nowUtc,
                        .SuppressedSinceLog = 0
                    }
                End Function,
                Function(k, existing)
                    Dim sinceLog = (nowUtc - existing.LastLoggedUtc).TotalMinutes
                    If sinceLog >= FailureHeartbeatMinutes Then
                        shouldLog = True
                        suppressedAtLog = existing.SuppressedSinceLog
                        existing.LastLoggedUtc = nowUtc
                        existing.SuppressedSinceLog = 0
                    Else
                        existing.SuppressedSinceLog += 1
                    End If
                    Return existing
                End Function)

            If shouldLog Then
                If isFirst Then
                    _logger.LogWarning(ex,
                        "Node {NodeId} unreachable while polling instance {Id} (further failures from this node will be suppressed for up to {Window} minute(s))",
                        nodeId, instanceId, FailureHeartbeatMinutes)
                Else
                    _logger.LogWarning(ex,
                        "Node {NodeId} still unreachable (suppressed {Count} similar warning(s) over the last ~{Window} minute(s))",
                        nodeId, suppressedAtLog, FailureHeartbeatMinutes)
                End If
            End If
        End Sub

        ''' <summary>
        ''' Format a downtime span as "Xs", "Xm", "Xh Ym", or
        ''' "Xd" for the back-online log line. Keeps it tight
        ''' rather than the default TimeSpan.ToString format
        ''' which renders as "00:00:42.1234567".
        ''' </summary>
        Private Shared Function FormatDowntime(span As TimeSpan) As String
            If span.TotalSeconds < 60 Then Return $"{CInt(span.TotalSeconds)}s"
            If span.TotalMinutes < 60 Then Return $"{CInt(Math.Floor(span.TotalMinutes))}m"
            If span.TotalHours < 24 Then
                Dim hrs = CInt(Math.Floor(span.TotalHours))
                Dim mins = CInt(Math.Floor(span.TotalMinutes - hrs * 60))
                Return $"{hrs}h {mins}m"
            End If
            Return $"{CInt(Math.Floor(span.TotalDays))}d"
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