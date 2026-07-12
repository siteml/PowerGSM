Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Linq
Imports System.IO
Imports System.Net
Imports System.Text
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
        Private ReadOnly _identityResolver As IdentityResolver
        Private ReadOnly _logger As ILogger(Of InstanceManager)
        Private ReadOnly _logParsers As New ConcurrentDictionary(Of String, ActiveLogParser)
        Private ReadOnly _logStreamCancellations As New ConcurrentDictionary(Of String, CancellationTokenSource)

        ' Per-instance connection-binding store handed to the game
        ' parser when it implements IConnectionBindingAware (LO), so
        ' the parser's RemoteAddr -> player-name bindings SURVIVE
        ' parser recreation on log-stream reconnects. Without this a
        ' recreated parser starts empty and a close arriving after a
        ' reconnect — notably a UChannel::Close-only timeout, which
        ' the LO parser drops when it can't resolve a name — never
        ' produces a leave, leaving the session open in history.
        ' Preserved across reconnects (StartLogStream does not clear
        ' it) and dropped only on a real stop (StopLogStream).
        Private ReadOnly _connectionBindings As _
            New ConcurrentDictionary(Of String, IDictionary(Of String, String))
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

        ' Phase 5j — serialises concurrent invocations of
        ' PurgeAndRebuildHistoryAsync so a double-clicked menu
        ' item or a UI-plus-slash-command race can't interleave
        ' two purge+rebuild flows against the same history
        ' tables. WaitAsync(timeout) bails out cleanly if a
        ' second caller arrives while the first is mid-flight,
        ' returning a warning rather than blocking the UI
        ' thread forever.
        Private ReadOnly _purgeLock As New SemaphoreSlim(1, 1)
        Private Const PurgeLockTimeoutMs As Integer = 5000
        Private Const ChatRebuildFetchLimit As Integer = 10000

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
        ' plugin's ILogParser. Three sources of noise to filter:
        '   1. Plugins may emit nameless leaves when their IP->name
        '      binding has been lost (e.g. Manager reconnected mid-
        '      session and missed the original Join). These need to
        '      attach to *some* tracked session to be persisted
        '      meaningfully — hence the "one player online means it
        '      was that player" heuristic, debounced via
        '      EmptyLeaveCooldownMs to avoid attributing a burst of
        '      nameless leaves to the same player repeatedly.
        '   2. UE4 also fires connection-close log lines for server-
        '      internal channels (EOS auth, backend telemetry) that
        '      aren't player connections at all. Plugins that match
        '      broadly enough to catch those produce spurious leaves
        '      with no matching join.
        '   3. When a log stream reconnects or the node's ring buffer
        '      replays a tail, previously-seen join/leave lines come
        '      through the parser again and would refire notifications.
        '
        ' We solve all three by gating notifications on an actual
        ' state transition: only emit PlayerJoined if the name wasn't
        ' already in the active set, only emit PlayerLeft if the name
        ' was in the set. Nameless leaves fall back to the
        ' single-player-attribution heuristic above.
        '
        ' Plugin-side note: LO previously matched both UChannel::Close
        ' AND UNetConnection::Close on a real disconnect, producing
        ' two leave events per actual leave. The second was usually
        ' nameless (UNetConnection::Close cleared the IP->name dict
        ' first, the subsequent UChannel::Close failed name lookup)
        ' and the single-player heuristic would misattribute it to an
        ' unrelated player still on the tile — a real false-positive
        ' bug. The LO parser now matches UNetConnection::Close only,
        ' which eliminates both the duplication and the misattribution
        ' for that plugin. The heuristic remains for the manager-
        ' reconnect case (case 1 above) and for plugins that may
        ' produce nameless leaves by other means.
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

        ' Identity-backfill cadence — the periodic pass that
        ' updates PlayerActivity identity columns (CharacterId /
        ' PlatformUserId / DisplayName) for rows that were
        ' written with NULL identity at observation time.
        '
        ' Driver: Last Oasis's first LogPersistence Persisting
        ' line for a player can lag the Login by up to ~2 minutes
        ' (Persisting fires on the autosave tick, every ~2min
        ' per active player). PersistPlayerObservationAsync's
        ' /players enrichment runs immediately on Join, so any
        ' join that beats the first Persisting tick gets written
        ' with NULL DisplayName, and the eventual Leave's /players
        ' query usually misses too (Node evicts the session
        ' before our HTTP request resolves). Without this
        ' backfill the History row would show an empty Character
        ' column despite the Node having figured out the in-game
        ' name within the session.
        '
        ' 10s strikes the balance: fast enough that a user
        ' watching History sees identity fill in within seconds
        ' of the first Persisting tick firing, slow enough that
        ' the per-tick /players call + DB scan stays well below
        ' the per-instance polling budget.
        Private Const IdentityBackfillIntervalMs As Integer = 10000

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
                       identityResolver As IdentityResolver,
                       logger As ILogger(Of InstanceManager))
            _clientFactory = clientFactory
            _pluginRegistry = pluginRegistry
            _credentialService = credentialService
            _sharedConfigService = sharedConfigService
            _emitter = emitter
            _identityResolver = identityResolver
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

                ' Phase 5m-2e — hard guard: refuse to start an instance
                ' whose game plugin isn't loaded. Every plugin call below
                ' is null-guarded, so without the plugin we'd build EMPTY
                ' launch arguments and NO parse rules — yet a persisted
                ' ExeOverride from a previous (plugin-loaded) start still
                ' supplies a valid executable candidate, which would let
                ' the node launch the bare binary: an unmanageable,
                ' untracked, crash-looping process. Block it here so the
                ' backstop covers the panel buttons, the tree context
                ' menu, autostart, and scheduled restart rules alike.
                If _pluginRegistry.GetPlugin(instanceEntity.GameId) Is Nothing Then
                    _logger.LogError(
                        "Refusing to start instance {Id}: no plugin loaded for game '{Game}'. " &
                        "Restore the plugin file and reload plugins before starting.",
                        instanceId, instanceEntity.GameId)
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

                ' Startup config render: if the plugin implements
                ' IStartupFileProvider, push the merged instance config
                ' into the game's own config file(s) on the node before
                ' launch. Best-effort — failures warn and the launch
                ' proceeds (see ApplyStartupFileRendersAsync).
                Await ApplyStartupFileRendersAsync(plugin, instanceConfig,
                                                   client, instanceId,
                                                   installEntity.InstallPath)

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
                                    ' Rooted candidates (e.g. Stardew's
                                    ' /usr/bin/xvfb-run wrapper on Linux)
                                    ' pass through untouched — joining a
                                    ' rooted path onto the install root
                                    ' produces garbage like
                                    ' /opt/.../stardewvalley/usr/bin/xvfb-run.
                                    If IsRootedOnEitherPlatform(relPath) Then
                                        candidates.Add(relPath)
                                        Continue For
                                    End If
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
                Dim resolvedEnvVars As Dictionary(Of String, String) = Nothing
                If plugin IsNot Nothing Then
                    Dim launchOptsProvider = TryCast(plugin, ILaunchOptionsProvider)
                    If launchOptsProvider IsNot Nothing Then
                        Try
                            Dim opts = launchOptsProvider.GetLaunchOptions(instanceConfig)
                            If opts IsNot Nothing Then
                                resolvedStdoutIsLog = opts.StdoutIsLog
                                resolvedRequiresConsoleIsolation = opts.RequiresConsoleIsolation
                                resolvedTailerDelayMs = opts.LogTailerStartDelayMs
                                resolvedEnvVars = opts.EnvironmentVars
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
                        .EnvironmentVars = If(resolvedEnvVars, New Dictionary(Of String, String)),
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
                            resultErr.Contains("does not exist") OrElse
                            resultErr.Contains("(error=2)")  ' shim posix_spawn ENOENT

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

                        ' Persist the winning candidate as ExeOverride only
                        ' after the instance SURVIVES its first 30 seconds.
                        ' Persisting immediately was a footgun: a spawn that
                        ' "succeeds" but dies moments later (Stardew's GL
                        ' init crash under a bad exe choice) locked in the
                        ' bad candidate, and every later start reused it
                        ' without ever consulting the plugin's updated list.
                        SchedulePersistExeOverride(instanceId, candidate)

                        StartLogStream(instanceId, client)
                        Return True

                    Catch ex As Exception
                        Dim msg = ex.Message.ToLowerInvariant()
                        Dim isNotFoundEx =
                            msg.Contains("not found") OrElse
                            msg.Contains("cannot find") OrElse
                            msg.Contains("no such file") OrElse
                            msg.Contains("does not exist") OrElse
                            msg.Contains("(error=2)")  ' shim posix_spawn ENOENT

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
        ''' Fire-and-forget: persist ExeOverride=candidate for the
        ''' instance only if it is still Running 30 seconds after the
        ''' start succeeded. Guards against locking in an executable
        ''' choice whose process "started" but crashed during init —
        ''' the candidate loop never gets re-consulted once an
        ''' override exists, so a bad early save is sticky. If the
        ''' instance stopped/crashed within the window, nothing is
        ''' saved and the next start resolves candidates fresh.
        ''' </summary>
        Private Sub SchedulePersistExeOverride(instanceId As String, candidate As String)
            Dim _unused = PersistExeOverrideAfterSurvivalAsync(instanceId, candidate)
        End Sub

        Private Async Function PersistExeOverrideAfterSurvivalAsync(instanceId As String,
                                                                    candidate As String) As Task
            Try
                Await Task.Delay(TimeSpan.FromSeconds(30))

                Dim live As InstanceStatusResponse = Nothing
                If Not _liveStates.TryGetValue(instanceId, live) OrElse live Is Nothing Then Return
                If live.CurrentState <> GSM.Plugin.InstanceState.Running AndAlso
                   live.CurrentState <> GSM.Plugin.InstanceState.Starting Then
                    _logger.LogInformation(
                        "Not persisting ExeOverride={Exe} for {Id}: instance did not survive startup (state={State})",
                        candidate, instanceId, live.CurrentState)
                    Return
                End If

                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim entity = db.Instances.Find(instanceId)
                    If entity Is Nothing Then Return
                    If Not String.Equals(entity.ExeOverride, candidate, StringComparison.OrdinalIgnoreCase) Then
                        entity.ExeOverride = candidate
                        entity.UpdatedUtc = DateTime.UtcNow
                        db.SaveChanges()
                        _logger.LogInformation("Saved ExeOverride={Exe} for {Id} (survived 30s)", candidate, instanceId)
                    End If
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "ExeOverride persistence check failed for {Id}", instanceId)
            End Try
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
                ' Connection-level failures (node offline) route through
                ' the shared per-node dedup — same as
                ' RefreshInstanceStateAsync — so a down node doesn't dump
                ' a stack trace here on every reconnect attempt. API-level
                ' errors log in full (rare, worth seeing each time).
                If IsConnectionFailure(ex) Then
                    NoteNodeNetworkFailure(instanceId, ex)
                Else
                    _logger.LogWarning(ex, "Failed to ensure log stream for {Id}", instanceId)
                End If
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
        ''' Reconnects log streams for every instance on an ATTACHED
        ''' node, so instances still running resume having their logs
        ''' buffered. Called on Manager startup. Instances on detached
        ''' nodes (NodeEntity.IsEnabled = False) are skipped: startup
        ''' reconnect is a background operation, and a detached node has
        ''' opted out of those — otherwise a detached-and-offline node
        ''' draws a failed connection attempt (and a logged error) for
        ''' every instance it hosts on every Manager start. Re-attaching
        ''' restores reconnect; explicit log-viewer opens are unaffected
        ''' (they don't route through here).
        ''' </summary>
        Public Async Function ReconnectLogStreamsAsync() As Task
            Dim instanceIds As New List(Of String)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim attached = From inst In db.Instances
                               Join install In db.Installations
                                   On inst.InstallationId Equals install.InstallationId
                               Join nodeEnt In db.Nodes
                                   On install.NodeId Equals nodeEnt.NodeId
                               Where nodeEnt.IsEnabled
                               Select inst.InstanceId
                For Each id In attached.ToList()
                    instanceIds.Add(id)
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
        ''' Phase 5g-2d — returns a copy of the passed player list
        ''' with each session enriched against the IdentityResolver
        ''' cache. Kept separate from GetPlayersAsync (which stays a
        ''' faithful accessor of raw Node state) so enrichment is an
        ''' explicit, opt-in step per consumer rather than a hidden
        ''' side effect of fetching. The Overview panel calls this
        ''' right after GetPlayersAsync; History and Discord
        ''' consumers can adopt it in later rounds.
        '''
        ''' Resolves the instance's current SessionIdentity (the
        ''' same value the persistence path stamps and the resolver
        ''' keys on) and enriches every session under it. When the
        ''' identity can't be resolved (no parser, stopped
        ''' instance), the input list is returned unchanged.
        ''' </summary>
        Public Function EnrichPlayers(instanceId As String,
                                       sessions As IReadOnlyList(Of PlayerSession)) As IReadOnlyList(Of PlayerSession)
            If sessions Is Nothing OrElse sessions.Count = 0 Then Return sessions
            Dim sessionIdentity = ResolveSessionIdentity(instanceId)
            If String.IsNullOrEmpty(sessionIdentity) Then Return sessions

            Dim enriched As New List(Of PlayerSession)(sessions.Count)
            For Each s In sessions
                enriched.Add(_identityResolver.EnrichBySessionIdentity(sessionIdentity, s))
            Next
            Return enriched
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
        ''' Count of instances the Manager currently believes are
        ''' Running, from last-known live state. Used by the self-
        ''' update apply pre-flight (Phase 5l-3) to note that a brief
        ''' log-stream disconnect is coming (the instances keep
        ''' running on the node; the new Manager reconnects and
        ''' resyncs on startup).
        ''' </summary>
        Public Function GetRunningInstanceCount() As Integer
            Dim n = 0
            For Each kvp In _liveStates
                If kvp.Value IsNot Nothing AndAlso
                   kvp.Value.CurrentState = GSM.Plugin.InstanceState.Running Then
                    n += 1
                End If
            Next
            Return n
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

            ' Identity backfill runs on its own cadence — see
            ' IdentityBackfillIntervalMs for the rationale. Separate
            ' timer from lastChatPoll so the two passes interleave
            ' independently without clipping each other; both share
            ' the same per-iteration FetchAllInstanceIds() result so
            ' no extra DB round trip is incurred for the second pass.
            Dim lastIdentityBackfill As DateTime = DateTime.MinValue

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

                    ' Identity backfill — only once per
                    ' IdentityBackfillIntervalMs. Catches up
                    ' PlayerActivity identity columns that weren't
                    ' resolved at write time (typical: LO join row
                    ' written before the first Persisting tick
                    ' surfaces the in-game DisplayName).
                    If (DateTime.UtcNow - lastIdentityBackfill).TotalMilliseconds >= IdentityBackfillIntervalMs Then
                        lastIdentityBackfill = DateTime.UtcNow
                        For Each id In ids
                            If token.IsCancellationRequested Then Return
                            Try
                                Await BackfillIdentitiesForInstanceAsync(id)
                            Catch ex As Exception
                                _logger.LogDebug(ex, "Identity backfill failed for {Id}", id)
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

            ' Hoisted above the Using — the 7-4a publish block below
            ' runs after End Using and needs the collected rows.
            Dim mirrored As List(Of ChatMessage) = Nothing

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
                    If mirrored Is Nothing Then mirrored = New List(Of ChatMessage)
                    mirrored.Add(msg)
                    If msg.TimestampUtc > newCursor Then newCursor = msg.TimestampUtc
                Next

                If added > 0 Then
                    db.SaveChanges()
                    _chatCursors(instanceId) = newCursor
                    _logger.LogDebug("Mirrored {Count} chat message(s) for {Id}",
                                     added, instanceId)
                End If
            End Using

            ' ---- Phase 7-4a utility event tap (ChatMessage) ----
            ' Publish each newly-mirrored chat line to utility
            ' plugins. The mirror is the Manager's single chat
            ' ingestion point and the cursor already provides
            ' replay dedup, so publishing here can't double-fire;
            ' rows skipped by the dedup are never in `mirrored`.
            ' Identity: the row carries the Node-resolved cid/pid;
            ' one resolver consult per message fills Platform and
            ' any gaps. Chat's speaker string IS the in-game
            ' character name, so it rides both PlayerName and
            ' CharacterName. HasSubscribers is checked once up
            ' front so the per-message resolver work costs nothing
            ' when no plugin subscribes. Runs after SaveChanges —
            ' a persist failure means no publish.
            If mirrored IsNot Nothing Then
                Try
                    Dim utilityHost = ManagerProgram.Services.GetService(Of UtilityPluginHost)()
                    If utilityHost IsNot Nothing AndAlso
                       utilityHost.HasSubscribers(GSM.Utility.UtilityEventKind.ChatMessage) Then
                        For Each msg In mirrored
                            Dim cid = msg.CharacterId
                            Dim pid = msg.PlatformUserId
                            Dim platform As String = Nothing
                            Try
                                Dim probe = New PlayerSession With {
                                    .DisplayName = msg.DisplayName,
                                    .CharacterId = cid,
                                    .PlatformUserId = pid
                                }
                                Dim hit = _identityResolver.EnrichBySessionIdentity(sessionIdentity, probe)
                                If hit IsNot Nothing Then
                                    platform = hit.Platform
                                    If String.IsNullOrEmpty(cid) Then cid = hit.CharacterId
                                    If String.IsNullOrEmpty(pid) Then pid = hit.PlatformUserId
                                End If
                            Catch
                                ' Enrichment is best-effort — publish
                                ' with whatever the row carried.
                            End Try
                            utilityHost.PublishChatMessage(
                                instanceId, msg.DisplayName, cid, pid, platform,
                                msg.DisplayName, msg.Text, sessionIdentity, msg.TimestampUtc)
                        Next
                    End If
                Catch ex As Exception
                    _logger.LogDebug(ex,
                        "Utility chat publish failed for {Id}", instanceId)
                End Try
            End If
        End Function

        ''' <summary>
        ''' Periodic per-instance pass that catches up
        ''' PlayerActivity identity columns (CharacterId,
        ''' PlatformUserId, DisplayName) that weren't fully
        ''' resolved at observation-write time. See
        ''' IdentityBackfillIntervalMs's comment for the
        ''' driving timing problem; this method is the executor.
        '''
        ''' Strategy: for each currently-connected player on the
        ''' instance whose Node-side session DOES carry a resolved
        ''' DisplayName, find any PlayerActivity rows for the same
        ''' (session, persona) pair that have NULL/empty DisplayName
        ''' and update them. Idempotent — rows with DisplayName
        ''' already populated are filtered out by the WHERE clause
        ''' and skipped on subsequent passes. CharacterId and
        ''' PlatformUserId fill only when currently NULL so a row
        ''' that got partial enrichment at write time (CharacterId
        ''' resolved, DisplayName not) keeps its existing values
        ''' and just gains the missing one.
        '''
        ''' Bounded by the number of currently-connected players
        ''' per instance (typically &lt; 20), so the cost per tick
        ''' is dominated by the /players HTTP round trip itself.
        ''' A 0-player instance early-exits before any DB work.
        '''
        ''' Plays nicely with the Leave-time inheritance fallback
        ''' in PersistPlayerObservationAsync: that fallback inherits
        ''' identity from the most recent identity-resolved Join
        ''' row in the same session, so once the backfill catches
        ''' the Join, the eventual Leave inherits cleanly even
        ''' though Node /players returns empty at leave time (the
        ''' session is evicted by then).
        '''
        ''' Failure modes degrade gracefully:
        '''   • /players unreachable → catch returns, retry next tick.
        '''   • Session identity unresolvable → return, retry next tick
        '''     once the parser or adoption-fallback catches up.
        '''   • Concurrent PersistPlayerObservationAsync writing the
        '''     same rows → EF Core's row-level tracking + SQLite's
        '''     locking serialise; worst case one of the two writes
        '''     wins and the other re-converges on the next pass.
        ''' </summary>
        Private Async Function BackfillIdentitiesForInstanceAsync(instanceId As String) As Task
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return

            Dim players As IReadOnlyList(Of PlayerSession) = Nothing
            Try
                players = Await client.GetPlayersAsync(instanceId, CancellationToken.None)
            Catch
                ' Node unreachable / transient HTTP failure. The
                ' connection-failure dedup layer already handles
                ' logging cadence for offline nodes; this method
                ' just needs to bow out quietly so the loop
                ' continues.
                Return
            End Try

            If players Is Nothing OrElse players.Count = 0 Then Return

            Dim sessionIdentity = ResolveSessionIdentity(instanceId)
            If String.IsNullOrEmpty(sessionIdentity) Then Return

            ' ---- Phase 5g-2d write-through (background-poll tick) ----
            ' Feed every /players session into the resolver on the
            ' identity-backfill cadence (10s). This is the steady-
            ' state observation path for players who were already
            ' connected before the Manager started and haven't
            ' triggered a join event or stream reconnect since —
            ' their identity still reaches the cache here. Observe
            ' all sessions, not just those carrying a DisplayName:
            ' a persona- or CharacterId-only observation still
            ' enriches the record's alias set.
            For Each obsSess In players
                If obsSess Is Nothing Then Continue For
                Try
                    _identityResolver.ObserveBySessionIdentity(
                        sessionIdentity,
                        New IdentityObservation With {
                            .PlatformPersona = obsSess.PlatformPersona,
                            .CharacterId = obsSess.CharacterId,
                            .PlatformUserId = obsSess.PlatformUserId,
                            .DisplayName = obsSess.DisplayName,
                            .Platform = obsSess.Platform,
                            .ObservedAtUtc = If(obsSess.JoinedUtc > DateTime.MinValue,
                                                DateTime.SpecifyKind(obsSess.JoinedUtc, DateTimeKind.Utc),
                                                DateTime.UtcNow)
                        })
                Catch ex As Exception
                    _logger.LogDebug(ex,
                        "IdentityResolver.Observe failed during backfill for {Id}", instanceId)
                End Try
            Next

            ' Build (PlatformPersona → resolved session) map.
            ' PlatformPersona is the join key because that's what
            ' PersistPlayerObservationAsync wrote as PlayerName —
            ' the raw login-line string is the Steam handle on LO,
            ' which mirrors PlayerSession.PlatformPersona. Only
            ' include sessions with a resolved DisplayName since
            ' those are the ones with new info worth propagating;
            ' sessions still without DisplayName have nothing to
            ' backfill into the row that already has NULL.
            Dim resolvedByName As New Dictionary(Of String, PlayerSession)(StringComparer.OrdinalIgnoreCase)
            For Each sess In players
                If sess Is Nothing Then Continue For
                If String.IsNullOrEmpty(sess.DisplayName) Then Continue For
                If String.IsNullOrEmpty(sess.PlatformPersona) Then Continue For
                resolvedByName(sess.PlatformPersona) = sess
            Next
            If resolvedByName.Count = 0 Then Return

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim updated As Integer = 0

                For Each kvp In resolvedByName
                    Dim personaName = kvp.Key
                    Dim sess = kvp.Value

                    Dim unresolvedRows = db.PlayerActivity.
                        Where(Function(a) a.SessionIdentity = sessionIdentity AndAlso
                                          a.PlayerName = personaName AndAlso
                                          (a.DisplayName Is Nothing OrElse a.DisplayName = "")).
                        ToList()

                    For Each row In unresolvedRows
                        ' Only fill columns that are currently
                        ' NULL — a partial earlier enrichment
                        ' (CharacterId resolved, DisplayName not)
                        ' keeps its existing CharacterId and just
                        ' gains DisplayName. The WHERE clause
                        ' already established DisplayName is
                        ' empty, so overwriting it is safe and
                        ' the loop body matches the WHERE shape.
                        If String.IsNullOrEmpty(row.CharacterId) Then
                            row.CharacterId = sess.CharacterId
                        End If
                        If String.IsNullOrEmpty(row.PlatformUserId) Then
                            row.PlatformUserId = sess.PlatformUserId
                        End If
                        row.DisplayName = sess.DisplayName
                        updated += 1
                    Next
                Next

                If updated > 0 Then
                    Await db.SaveChangesAsync()
                    _logger.LogDebug(
                        "Backfilled {Count} PlayerActivity identity row(s) for {Id}",
                        updated, instanceId)
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
                    ' Hand the parser the persistent per-instance
                    ' connection-binding store so its RemoteAddr->name
                    ' bindings survive THIS (re)creation. Only parsers
                    ' that opt in via IConnectionBindingAware (LO) use
                    ' it; everything else is untouched.
                    Dim bindingAware = TryCast(parser, IConnectionBindingAware)
                    If bindingAware IsNot Nothing Then
                        bindingAware.ConnectionBindings = _connectionBindings.GetOrAdd(
                            instanceId,
                            Function(id) CType(New ConcurrentDictionary(Of String, String)(
                                StringComparer.OrdinalIgnoreCase), IDictionary(Of String, String)))
                    End If
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
                ' Resync from the Node's authoritative /players list
                ' before processing any log lines. StartLogStream just
                ' handed us a fresh parser with a fresh _pendingRemoteAddr;
                ' its _connectionsByAddr is now the Manager-owned store
                ' that PERSISTS across reconnects (IConnectionBindingAware),
                ' _activePlayers lives on InstanceManager across parser
                ' recreates, and History lives in the DB across Manager
                ' restarts. The resync
                ' has two jobs:
                '
                '   1. Catch up History for joins the Manager missed.
                '      Scenario observed 2026-05-25: Manager was down
                '      from 15:42:12 to 15:45:05; LO player joined at
                '      15:44:19 (during downtime); Manager came back
                '      and saw the tail replay, but the join never
                '      persisted to PlayerActivity. Cycle 1 (before
                '      downtime) and Cycle 3 (after) were captured
                '      cleanly; Cycle 2 was a black hole. The resync
                '      synthesizes a Join row stamped at the Node's
                '      sess.JoinedUtc so the downtime gap fills in.
                '
                '   2. Stop the dedup bucket from blocking legitimate
                '      joins. _activePlayers may carry stale names
                '      from the previous parser run (Manager restart
                '      that missed a leave; transient SSE reconnect
                '      from BackgroundPollLoopAsync's stream-health
                '      check; plugin reload; attach/detach toggle).
                '      Without resync, HandlePlayerJoin's bucket.Add
                '      returns False for the next real join, silently
                '      drops the PlayerActivity row, AND leaves the
                '      bucket in a state where the subsequent leave's
                '      bucket.Remove also fails — an entire pair
                '      vanishes from History (the original symptom).
                '
                ' Bucket sync runs AFTER synthesis so the imperative
                ' parser's tail-replay of the same Join succeeded
                ' lines correctly dedups against the synthesized row
                ' instead of inserting a duplicate.
                '
                ' Best-effort: failures inside the resync (transient
                ' node error, cancellation from a second reconnect
                ' racing ahead, DB write conflict) fall through to
                ' streaming with whatever state we have. The next
                ' stream restart's resync retries; the next real
                ' leave or instance stop cleans up bucket state.
                Try
                    Dim players = Await client.GetPlayersAsync(instanceId, ourCts.Token)
                    Await ResyncActivePlayersFromNodeAsync(instanceId, players, ourCts.Token)
                Catch
                End Try

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
            ' Drop the persistent connection bindings on a REAL stop.
            ' Only stop/purge paths call StopLogStream; reconnects go
            ' through StartLogStream, which preserves the store. A
            ' fresh game process gets fresh connections, so stale
            ' addr->name entries must not carry across a restart.
            Dim removedBindings As IDictionary(Of String, String) = Nothing
            _connectionBindings.TryRemove(instanceId, removedBindings)
        End Sub

        ''' <summary>
        ''' Brings History and _activePlayers up to date with the
        ''' Node's current /players list at log-stream (re)start.
        ''' Two passes:
        '''
        '''   1. Identity synthesis. For every currently-online
        '''      player whose join is NOT already represented in
        '''      PlayerActivity (most-recent row for the (session,
        '''      name) tuple is a Leave, or older than the Node's
        '''      JoinedUtc, or absent entirely), insert a synthesized
        '''      Join row stamped at sess.JoinedUtc with identity
        '''      columns straight from /players. UPSERTs the
        '''      matching PlayerSessions aggregate row in the
        '''      same scope. This catches up History when the
        '''      Manager was offline during a real Join — the
        '''      Cycle-2-missing scenario observed on 2026-05-25.
        '''
        '''   2. Bucket sync. Replace _activePlayers[instanceId]
        '''      with the exact set of currently-online names.
        '''      Done AFTER synthesis so the subsequent tail
        '''      replay's bucket.Add dedup correctly suppresses
        '''      duplicate Join rows for the entries we just
        '''      synthesized.
        '''
        ''' Idempotent: a second invocation — e.g. from the
        ''' BackgroundPollLoopAsync stream-health check racing
        ''' with ReconnectLogStreamsAsync at startup — finds the
        ''' synthesized Join already present in History and
        ''' skips synthesis, just refreshes the bucket.
        '''
        ''' Best-effort: synthesis runs inside Try/Catch so a
        ''' transient DB error degrades to bucket-only sync
        ''' instead of failing the stream restart. Cancellation
        ''' partway through (second reconnect arrives mid-DB-work)
        ''' is treated as a successful early exit — the next
        ''' stream restart's synthesis pass will retry.
        ''' </summary>
        Private Async Function ResyncActivePlayersFromNodeAsync(instanceId As String,
                                                                 players As IReadOnlyList(Of PlayerSession),
                                                                 cancellation As CancellationToken) As Task
            Dim bucket = _activePlayers.GetOrAdd(instanceId,
                Function(id) New HashSet(Of String)(StringComparer.OrdinalIgnoreCase))

            ' Build the (name, session) list of currently-online
            ' players. PlatformPersona is the same key
            ' HandlePlayerJoin uses for bucket membership; fall
            ' back to DisplayName when the platform doesn't expose
            ' a persona. Names that resolve to empty get dropped
            ' — they'd never produce a useful Join row anyway.
            Dim online As New List(Of KeyValuePair(Of String, PlayerSession))
            If players IsNot Nothing Then
                For Each sess In players
                    If sess Is Nothing Then Continue For
                    Dim playerName = If(Not String.IsNullOrEmpty(sess.PlatformPersona),
                                        sess.PlatformPersona,
                                        If(Not String.IsNullOrEmpty(sess.DisplayName),
                                           sess.DisplayName, ""))
                    If String.IsNullOrEmpty(playerName) Then Continue For
                    online.Add(New KeyValuePair(Of String, PlayerSession)(playerName, sess))
                Next
            End If

            ' --- Pass 1: synthesis ---
            Dim sessionIdentity = ResolveSessionIdentity(instanceId)

            ' --- Phase 5g-2d write-through ---
            ' Feed every online player's authoritative /players
            ' identity into the resolver. Runs independently of the
            ' synthesis logic below (which skips players already
            ' covered in History via Continue For), so the cache
            ' stays current even when nothing needs synthesizing.
            ' /players data is Node-resolved and carries all four
            ' identity facets where the game exposes them.
            If Not String.IsNullOrEmpty(sessionIdentity) Then
                For Each kvp In online
                    Dim sess = kvp.Value
                    Try
                        _identityResolver.ObserveBySessionIdentity(
                            sessionIdentity,
                            New IdentityObservation With {
                                .PlatformPersona = sess.PlatformPersona,
                                .CharacterId = sess.CharacterId,
                                .PlatformUserId = sess.PlatformUserId,
                                .DisplayName = sess.DisplayName,
                                .Platform = sess.Platform,
                                .ObservedAtUtc = If(sess.JoinedUtc > DateTime.MinValue,
                                                    DateTime.SpecifyKind(sess.JoinedUtc, DateTimeKind.Utc),
                                                    DateTime.UtcNow)
                            })
                    Catch ex As Exception
                        _logger.LogDebug(ex,
                            "IdentityResolver.Observe failed during resync for {Id}/{Name}",
                            instanceId, kvp.Key)
                    End Try
                Next
            End If

            If Not String.IsNullOrEmpty(sessionIdentity) AndAlso online.Count > 0 Then
                Try
                    Dim nodeId = GetNodeIdForInstance(instanceId)
                    Using scope = ManagerProgram.Services.CreateScope()
                        Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                        Dim addedAny = False

                        For Each kvp In online
                            If cancellation.IsCancellationRequested Then Exit For
                            Dim playerName = kvp.Key
                            Dim sess = kvp.Value

                            ' No reliable join timestamp → skip. The
                            ' next live Join through the normal
                            ' callback path will represent the player
                            ' correctly; we'd rather have a missing
                            ' row than a wrong-timestamp one.
                            If sess.JoinedUtc <= DateTime.MinValue Then Continue For
                            Dim joinedUtc = DateTime.SpecifyKind(sess.JoinedUtc, DateTimeKind.Utc)

                            Dim mostRecent = db.PlayerActivity.
                                Where(Function(a) a.SessionIdentity = sessionIdentity AndAlso
                                                  a.PlayerName = playerName).
                                OrderByDescending(Function(a) a.TimestampUtc).
                                FirstOrDefault()

                            If mostRecent IsNot Nothing AndAlso
                               mostRecent.EventKind = "join" Then
                                ' Most-recent row is an OPEN join, so
                                ' the player is already represented as
                                ' online in History — synthesis only
                                ' fills a MISSING join, so there's
                                ' nothing to add. The old check also
                                ' required TimestampUtc >= joinedUtc,
                                ' but that compared two clocks: the
                                ' live join row is stamped
                                ' DateTime.UtcNow (Manager processing
                                ' time) while joinedUtc is the Node's
                                ' JoinedUtc. When the Manager clock was
                                ' marginally behind the Node's, the
                                ' ">=" failed and a duplicate join was
                                ' synthesised (the blank-character +
                                ' resolved-character pair seen
                                ' 2026-05-30).
                                Continue For
                            End If

                            ' Synthesize. Identity columns come
                            ' straight from /players — the Node has
                            ' already done identity resolution and
                            ' those values are authoritative.
                            db.PlayerActivity.Add(New PlayerActivityEntity With {
                                .ActivityId = Guid.NewGuid().ToString("N"),
                                .SessionIdentity = sessionIdentity,
                                .NodeId = nodeId,
                                .InstanceId = instanceId,
                                .TimestampUtc = joinedUtc,
                                .PlayerName = playerName,
                                .EventKind = "join",
                                .CharacterId = sess.CharacterId,
                                .PlatformUserId = sess.PlatformUserId,
                                .DisplayName = sess.DisplayName
                            })

                            ' UPSERT PlayerSessions, mirroring
                            ' PersistPlayerObservationAsync's pattern.
                            ' New row when there's no existing
                            ' (session, name) summary; otherwise touch
                            ' LastSeenUtc/LastHostInstanceId so the
                            ' aggregate stays current.
                            Dim summary = db.PlayerSessions.FirstOrDefault(
                                Function(p) p.SessionIdentity = sessionIdentity AndAlso
                                            p.PlayerName = playerName)
                            If summary Is Nothing Then
                                db.PlayerSessions.Add(New PlayerSessionEntity With {
                                    .PlayerSessionId = Guid.NewGuid().ToString("N"),
                                    .SessionIdentity = sessionIdentity,
                                    .PlayerName = playerName,
                                    .FirstSeenUtc = joinedUtc,
                                    .LastSeenUtc = joinedUtc,
                                    .LastHostInstanceId = instanceId
                                })
                            Else
                                If summary.LastSeenUtc < joinedUtc Then
                                    summary.LastSeenUtc = joinedUtc
                                End If
                                summary.LastHostInstanceId = instanceId
                            End If

                            addedAny = True
                            _logger.LogInformation(
                                "Synthesized Join for {Id}/{Name} at stream restart (TimestampUtc={Ts:o}) — Manager-downtime catch-up",
                                instanceId, playerName, joinedUtc)
                        Next

                        If addedAny Then
                            Await db.SaveChangesAsync(cancellation)
                        End If
                    End Using
                Catch ex As OperationCanceledException
                    ' Stream was cancelled mid-synthesis (e.g. second
                    ' reconnect raced ahead). Next stream restart's
                    ' resync will catch up.
                Catch ex As Exception
                    _logger.LogDebug(ex,
                        "Identity synthesis failed during resync for {Id}", instanceId)
                End Try
            End If

            ' --- Pass 1.5: rehydrate parser connection bindings ---
            ' The LO parser turns an address-only close line
            ' (UChannel::Close / UNetConnection::Close carry only
            ' RemoteAddr, not a name) into a NAMED leave by looking the
            ' address up in its RemoteAddr -> name table. That table is
            ' empty on a fresh parser, so after a Manager restart the
            ' parser can't attribute a close for a player who joined
            ' BEFORE the restart: the leave is dropped, then it cascades
            ' through the name-dedup bucket (the next reconnect's join is
            ' swallowed as a duplicate). The Node tracks RemoteAddress
            ' per online player and returns it in /players, so rebuild
            ' the table from there. _connectionBindings(instanceId) IS
            ' the dictionary the parser was handed via
            ' IConnectionBindingAware, so writes here are visible to the
            ' parser immediately. Only LO populates RemoteAddress, so
            ' this is a no-op for other games (and we don't create an
            ' entry unless there's at least one address to store).
            Dim bindings As IDictionary(Of String, String) = Nothing
            For Each kvp In online
                If String.IsNullOrEmpty(kvp.Value.RemoteAddress) Then Continue For
                If bindings Is Nothing Then
                    bindings = _connectionBindings.GetOrAdd(
                        instanceId,
                        Function(id) CType(New ConcurrentDictionary(Of String, String)(
                            StringComparer.OrdinalIgnoreCase), IDictionary(Of String, String)))
                End If
                bindings(kvp.Value.RemoteAddress) = kvp.Key
            Next

            ' --- Pass 1.7: leave reconcile (departures the Manager missed) ---
            ' Catches a player who left ENTIRELY while the Manager wasn't
            ' watching (closed, or stream disconnected) — there is no close
            ' line for the parser to process when it comes back, so without
            ' this the player's Join sits open in History forever. The Node's
            ' /players is authoritative for who is still connected, so anyone
            ' whose most-recent activity row on THIS instance is a Join but
            ' who is absent from /players has left.
            '
            ' Scoped by InstanceId, NOT SessionIdentity: on LO the session
            ' identity is realm-wide and spans every tile/instance on the
            ' realm, so diffing realm-wide open-joins against ONE instance's
            ' /players would falsely "leave" players online on a sibling
            ' tile. InstanceId keeps it to players seen on this instance.
            '
            ' Gated on Node uptime: right after a NODE restart the Node's
            ' /players under-reports still-connected players (it resumes log
            ' tailing from a byte offset and never replays old Join lines),
            ' so a diff then would synthesise false leaves. Only trust the
            ' absence signal once the Node has been up long enough that a
            ' transient post-restart empty/partial /players has passed.
            ' Known residual edge: a player who stays connected but totally
            ' silent across a Node restart never re-appears in /players and
            ' would eventually be reconciled as left — same class as the
            ' existing node-restart player-state gap; rare, accepted.
            '
            ' Persist-only (PersistPlayerObservation, not HandlePlayerLeave):
            ' no Discord notification fires, since the leave happened in the
            ' past while we were away — a "left" ping now would be misleading
            ' (mirrors the terminal-state synthetic-leave policy).
            If Not String.IsNullOrEmpty(sessionIdentity) Then
                Dim nodeTrustworthy = False
                Try
                    Dim statusClient = GetClientForInstance(instanceId)
                    If statusClient IsNot Nothing Then
                        Dim nodeStatus = Await statusClient.GetStatusAsync(cancellation)
                        ' 300s: Node up long enough that a transient
                        ' post-restart /players gap has passed.
                        If nodeStatus IsNot Nothing AndAlso
                           nodeStatus.UptimeSeconds >= 300 Then
                            nodeTrustworthy = True
                        End If
                    End If
                Catch ex As Exception
                    _logger.LogDebug(ex,
                        "Leave-reconcile node-uptime gate fetch failed for {Id} — skipping",
                        instanceId)
                End Try

                If nodeTrustworthy Then
                    Try
                        Dim onlineNames As New HashSet(Of String)(
                            online.Select(Function(kv) kv.Key),
                            StringComparer.OrdinalIgnoreCase)
                        Dim recentCutoff = DateTime.UtcNow.AddHours(-48)
                        Using scope = ManagerProgram.Services.CreateScope()
                            Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                            ' Bounded 48h pull of this instance's activity,
                            ' grouped client-side to the latest row per player.
                            Dim rows = db.PlayerActivity.
                                Where(Function(a) a.InstanceId = instanceId AndAlso
                                                  a.TimestampUtc >= recentCutoff).
                                Select(Function(a) New With {
                                    .PlayerName = a.PlayerName,
                                    .EventKind = a.EventKind,
                                    .TimestampUtc = a.TimestampUtc}).
                                ToList()
                            Dim latestPerPlayer = rows.
                                GroupBy(Function(r) r.PlayerName, StringComparer.OrdinalIgnoreCase).
                                Select(Function(g) g.OrderByDescending(
                                    Function(r) r.TimestampUtc).First())
                            For Each r In latestPerPlayer
                                If cancellation.IsCancellationRequested Then Exit For
                                If String.IsNullOrEmpty(r.PlayerName) Then Continue For
                                If onlineNames.Contains(r.PlayerName) Then Continue For
                                If Not String.Equals(r.EventKind, "join",
                                                     StringComparison.OrdinalIgnoreCase) Then Continue For
                                ' Open Join + absent from /players — left while away.
                                PersistPlayerObservation(instanceId, r.PlayerName, isJoin:=False)
                                _logger.LogInformation(
                                    "Leave-reconcile: synthesised Leave for {Id}/{Name} (open join absent from /players)",
                                    instanceId, r.PlayerName)
                            Next
                        End Using
                    Catch ex As Exception
                        _logger.LogDebug(ex,
                            "Leave-reconcile query failed during resync for {Id}", instanceId)
                    End Try
                End If
            End If

            ' --- Pass 2: bucket sync ---
            SyncLock bucket
                bucket.Clear()
                For Each kvp In online
                    bucket.Add(kvp.Key)
                Next
            End SyncLock
        End Function

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
                _emitter.PlayerJoined(instanceId, PlayerLabelForNotification(instanceId, playerName))
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
                    _emitter.PlayerLeft(instanceId, PlayerLabelForNotification(instanceId, playerName))
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
                _emitter.PlayerLeft(instanceId, PlayerLabelForNotification(instanceId, inferredName))
                PersistPlayerObservation(instanceId, inferredName, isJoin:=False)
            End If
            ' bucket.Count = 0: server-internal channel close, ignore
            ' bucket.Count >= 2: can't disambiguate, wait for a named
            '   leave or for the set to drain via subsequent events
        End Sub

        ''' <summary>
        ''' Phase 5d-2 — composes the player label for join/leave
        ''' notifications, matching the /players slash command
        ''' format:
        '''   character (Platform: persona)  when a distinct
        '''     character name is known
        '''   persona (Platform)             when only the persona
        '''     is known
        ''' The platform parenthetical is dropped when Platform is
        ''' unknown (the resolver learns it from live observations —
        ''' it isn't carried in PlayerActivity, so it lands within a
        ''' poll cycle of the player being online). Given the raw
        ''' parser name (persona on LO), consults the resolver for
        ''' character / platform. Falls back to the raw name when the
        ''' resolver has nothing — expected for a brand-new player's
        ''' very first join (the persist that follows feeds the cache
        ''' for next time). Never throws; notification dispatch must
        ''' not be derailed by an enrichment miss.
        ''' </summary>
        Private Function PlayerLabelForNotification(instanceId As String,
                                                     rawName As String) As String
            If String.IsNullOrEmpty(rawName) Then Return rawName
            Try
                Dim sessionIdentity = ResolveSessionIdentity(instanceId)
                If String.IsNullOrEmpty(sessionIdentity) Then Return rawName
                Dim probe = New PlayerSession With {.PlatformPersona = rawName}
                Dim hit = _identityResolver.EnrichBySessionIdentity(sessionIdentity, probe)
                If hit Is Nothing Then Return rawName

                Dim persona = If(Not String.IsNullOrEmpty(hit.PlatformPersona), hit.PlatformPersona, rawName)
                Dim character = hit.DisplayName
                Dim platform = hit.Platform
                Dim hasPlatform = Not String.IsNullOrEmpty(platform)
                Dim characterDistinct = Not String.IsNullOrEmpty(character) AndAlso
                                        Not String.Equals(character, persona, StringComparison.Ordinal)

                If characterDistinct Then
                    If hasPlatform Then Return $"{character} ({platform}: {persona})"
                    Return $"{character} ({persona})"
                Else
                    If hasPlatform Then Return $"{persona} ({platform})"
                    Return persona
                End If
            Catch ex As Exception
                _logger.LogDebug(ex,
                    "Notification label enrichment failed for {Id}/{Name}",
                    instanceId, rawName)
            End Try
            Return rawName
        End Function

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
                Dim resolvedPlatform As String = Nothing
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
                                resolvedPlatform = matched.Platform
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

                ' ---- Resolver consult (Phase 5g-2d Round 3) ----
                ' If /players didn't fully resolve identity — the
                ' common PlayerLeave case, where the Node's
                ' EventStore evicts the session before our HTTP
                ' request lands — consult the in-memory resolver.
                ' It's hydrated from History and continuously fed by
                ' the join/leave write-through, the resync pass, and
                ' the 10s identity-backfill pass, so it usually knows
                ' this player's identity even when /players missed.
                ' Filling resolved* here STAMPS the PlayerActivity
                ' row with the character identity at write time, so
                ' History carries it natively rather than depending
                ' on the render-time inheritance/chat fallbacks.
                ' Only fills empties — a live /players hit above
                ' always wins, since it reflects current Node truth.
                If String.IsNullOrEmpty(resolvedDisplayName) OrElse
                   String.IsNullOrEmpty(resolvedCharacterId) OrElse
                   String.IsNullOrEmpty(resolvedPlatformUserId) Then
                    Try
                        Dim probe = New PlayerSession With {.PlatformPersona = playerName}
                        Dim hit = _identityResolver.EnrichBySessionIdentity(sessionIdentity, probe)
                        If hit IsNot Nothing Then
                            If String.IsNullOrEmpty(resolvedDisplayName) Then resolvedDisplayName = hit.DisplayName
                            If String.IsNullOrEmpty(resolvedCharacterId) Then resolvedCharacterId = hit.CharacterId
                            If String.IsNullOrEmpty(resolvedPlatformUserId) Then resolvedPlatformUserId = hit.PlatformUserId
                            If String.IsNullOrEmpty(resolvedPlatform) Then resolvedPlatform = hit.Platform
                        End If
                    Catch ex As Exception
                        _logger.LogDebug(ex,
                            "Resolver consult failed for {Id}/{Name}",
                            instanceId, playerName)
                    End Try
                End If

                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    ' ---- Leave-time identity inheritance ----
                    '
                    ' When /players above produced nothing on a
                    ' Leave (the typical case — the Node's
                    ' EventStore already evicted the session by
                    ' the time our HTTP request lands), look
                    ' back at the most recent identity-resolved
                    ' Join row for this (session, playerName)
                    ' and inherit its CharacterId / PlatformUserId
                    ' / DisplayName so the Leave row in History
                    ' doesn't render with an empty Character
                    ' column despite the matching Join knowing.
                    '
                    ' Conditioned on isJoin=False AND no resolved
                    ' identity yet — a successful /players
                    ' enrichment already filled the columns and
                    ' the more recent answer wins. Conditioned
                    ' on a non-empty playerName because that's
                    ' the join key for the lookup; a nameless
                    ' leave would match too many rows and pick
                    ' wrong.
                    '
                    ' The empty-check on DisplayName as the
                    ' "has identity" indicator is sufficient —
                    ' if DisplayName is populated the Join row
                    ' went through full identity resolution and
                    ' the other two columns are populated too
                    ' (or correctly null because the platform
                    ' doesn't expose them). DisplayName is the
                    ' field that drives the user-visible
                    ' Character column.
                    If Not isJoin AndAlso
                       String.IsNullOrEmpty(resolvedDisplayName) AndAlso
                       Not String.IsNullOrEmpty(playerName) Then
                        Try
                            Dim recentJoin = db.PlayerActivity.
                                Where(Function(a) a.SessionIdentity = sessionIdentity AndAlso
                                                  a.PlayerName = playerName AndAlso
                                                  a.EventKind = "join" AndAlso
                                                  a.DisplayName IsNot Nothing AndAlso
                                                  a.DisplayName <> "").
                                OrderByDescending(Function(a) a.TimestampUtc).
                                FirstOrDefault()
                            If recentJoin IsNot Nothing Then
                                resolvedCharacterId = If(resolvedCharacterId, recentJoin.CharacterId)
                                resolvedPlatformUserId = If(resolvedPlatformUserId, recentJoin.PlatformUserId)
                                resolvedDisplayName = recentJoin.DisplayName
                            End If
                        Catch ex As Exception
                            ' Lookup failure isn't fatal — leave
                            ' the row with null identity columns
                            ' and let the History query's chat-
                            ' fallback render path fill in the
                            ' character name retroactively if it
                            ' can find a chat row for the same
                            ' (session, pid). Logged at Debug
                            ' because it's a best-effort path.
                            _logger.LogDebug(ex,
                                "Leave-time identity inheritance failed for {Id}/{Name}",
                                instanceId, playerName)
                        End Try
                    End If

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

                ' ---- Phase 5g-2d write-through ----
                ' Feed the resolved identity into the resolver so
                ' downstream Enrich calls (Overview panel, Discord,
                ' future /lastseen) benefit from what we just
                ' learned. Runs after the DB commit so a persist
                ' failure doesn't pollute the cache with an
                ' observation we couldn't durably record. The
                ' resolver is in-memory and cheap; no need to guard
                ' it inside the DB scope. PlayerName maps to
                ' PlatformPersona to match the hydration mapping
                ' (PlayerActivity.PlayerName -> PlatformPersona);
                ' resolved* may be Nothing, which the resolver
                ' simply ignores (empty fields contribute no alias).
                Try
                    _identityResolver.ObserveBySessionIdentity(
                        sessionIdentity,
                        New IdentityObservation With {
                            .PlatformPersona = playerName,
                            .CharacterId = resolvedCharacterId,
                            .PlatformUserId = resolvedPlatformUserId,
                            .DisplayName = resolvedDisplayName,
                            .Platform = resolvedPlatform,
                            .ObservedAtUtc = now
                        })
                Catch ex As Exception
                    _logger.LogDebug(ex,
                        "IdentityResolver.Observe failed for {Id}/{Name}",
                        instanceId, playerName)
                End Try

                ' ---- Phase 7-4a utility event tap (join/leave) ----
                ' Publish to utility plugins HERE, not from the
                ' notification emitter: the emitter fires before this
                ' async identity cascade completes and carries only a
                ' decorated display label. This is the one point where
                ' the resolver's current best answer (/players hit →
                ' resolver consult → leave-time inheritance) plus the
                ' snapshotted sessionIdentity are all in scope — so a
                ' plugin only chases genuine identity gaps. Riding the
                ' persist path also means the stop-flush synthetic
                ' leaves and the leave-reconcile synthesized leaves
                ' reach plugins (correct for programmatic consumers;
                ' the emitter suppresses those only to avoid Discord
                ' noise). Service-located to avoid constructor churn
                ' (same pattern as ManagerRingBufferStore in the
                ' stream callback). Never throws.
                Try
                    Dim utilityHost = ManagerProgram.Services.GetService(Of UtilityPluginHost)()
                    utilityHost?.PublishPlayerEvent(
                        instanceId, isJoin, playerName,
                        resolvedCharacterId, resolvedPlatformUserId,
                        resolvedPlatform, resolvedDisplayName,
                        sessionIdentity, now)
                Catch ex As Exception
                    _logger.LogDebug(ex,
                        "Utility player-event publish failed for {Id}/{Name}",
                        instanceId, playerName)
                End Try
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

                ' ---- Phase 7-4a utility event tap (ServerStateChange) ----
                ' Tile bind is the server-state change the Manager's
                ' parsers actually observe (MatchState lives Node-side
                ' only, and only LO's parser emits TileLoaded today).
                ' The tile name rides the event's Message field.
                Try
                    Dim utilityHost = ManagerProgram.Services.GetService(Of UtilityPluginHost)()
                    utilityHost?.PublishServerStateChange(
                        instanceId, "TileLoaded", tileName, sessionIdentity, now)
                Catch ex As Exception
                    _logger.LogDebug(ex,
                        "Utility server-state publish failed for {Id}", instanceId)
                End Try
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

                ' ---- Phase 7-4a utility event tap (ServerStateChange) ----
                ' The unbind counterpart. sessionIdentity here is the
                ' identity that just ENDED (the parser cleared its
                ' CurrentSessionIdentity before emitting the event).
                Try
                    Dim utilityHost = ManagerProgram.Services.GetService(Of UtilityPluginHost)()
                    utilityHost?.PublishServerStateChange(
                        instanceId, "TileUnloaded", Nothing, sessionIdentity, now)
                Catch ex As Exception
                    _logger.LogDebug(ex,
                        "Utility server-state publish failed for {Id}", instanceId)
                End Try
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
        '  Phase 5j — Purge & rebuild history from current state
        '
        '  Operator-triggered "wipe the Manager's history tables and
        '  re-derive everything from the Node's authoritative current
        '  state" flow. Two use cases drive it: recovering from
        '  parsing-logic bugs that polluted PlayerActivity (e.g. the
        '  Phase 5i UChannel::Close false-leave fix), and clean
        '  baseline for test→prod or DB-corruption recovery scenarios.
        '
        '  Non-negotiable principle: every timestamp written by the
        '  rebuild must come from real data on the Node side (each
        '  PlayerSession.JoinedUtc, instance_state.updated_at_utc,
        '  chat_messages.timestamp_utc). Substituting DateTime.UtcNow
        '  for any of these would be actively misleading — a player
        '  who has been online for two hours would show up as having
        '  just joined. That's worse than no rebuild at all.
        '
        '  Scope:
        '   • Rebuilds for instances currently in _liveStates with
        '     Running state and an active SSE log stream, on attached
        '     nodes. Detached nodes and non-Running instances are
        '     skipped.
        '   • PlayerActivity: one "join" row per currently-online
        '     PlayerSession with TimestampUtc = sess.JoinedUtc. No
        '     leave rows — by definition these players haven't left.
        '   • PlayerSessions: per-player aggregate summary for
        '     currently-online players only.
        '   • SessionHosts: one open row per running instance with a
        '     loaded tile, HostedFromUtc = instance_state.updated_at_utc.
        '   • ChatMessages: full chat fetched from Node, filtered to
        '     (identity match against current PlayerSessions) AND
        '     (chat timestamp >= matched sess.JoinedUtc). Chat from
        '     disconnected players or from earlier sessions of
        '     currently-connected players is dropped — the History
        '     timeline would look incoherent otherwise.
        '
        '  Operations are serialised on _purgeLock so a UI race
        '  doesn't interleave two flows. The whole purge+rebuild
        '  commits in a single DB transaction so a mid-flight
        '  failure rolls back cleanly, leaving the previous state
        '  intact.
        ' ============================================================

        ''' <summary>
        ''' Purges all Manager-side history tables (PlayerActivity,
        ''' PlayerSessions, SessionHosts, ChatMessages) and rebuilds
        ''' them from the Node's authoritative current state for every
        ''' running, attached instance. See the section comment above
        ''' for the scope and design rationale.
        '''
        ''' Progress callback receives human-readable step strings
        ''' ("Pausing log streams...", "Snapshotting instance 2 of
        ''' 3...", etc.) suitable for rendering in a progress dialog.
        ''' Pass Nothing for silent operation.
        '''
        ''' Returns a PurgeAndRebuildResult with row counts per
        ''' table, warning list (Node fetch failures, instances
        ''' skipped, etc.), and total duration. On exception, the
        ''' transaction rolls back and the result carries an
        ''' explanatory warning instead of throwing — the UI caller
        ''' can render it as a "failed and rolled back" outcome.
        '''
        ''' Concurrent invocation: blocks up to PurgeLockTimeoutMs
        ''' waiting for the lock. If the timeout elapses, returns a
        ''' result with one warning and zero rows created — caller
        ''' should treat that as "try again later, another operator
        ''' is purging right now."
        ''' </summary>
        Public Async Function PurgeAndRebuildHistoryAsync(
                progress As IProgress(Of String)) As Task(Of PurgeAndRebuildResult)
            Dim result As New PurgeAndRebuildResult()
            Dim sw = Stopwatch.StartNew()

            Dim acquired = Await _purgeLock.WaitAsync(PurgeLockTimeoutMs)
            If Not acquired Then
                result.Warnings.Add(
                    "Another purge & rebuild operation is already in progress. Wait for it to finish before retrying.")
                sw.Stop()
                result.DurationMs = sw.ElapsedMilliseconds
                Return result
            End If

            Try
                ReportProgress(progress, "Identifying target instances...")
                Dim targets = IdentifyRebuildTargets()

                _logger.LogInformation(
                    "Purge & rebuild requested — {Count} instance(s) targeted",
                    targets.Count)

                ' Pause live writes by cancelling each instance's
                ' SSE log stream. The Node keeps tailing the game's
                ' log file; new events accumulate Node-side until
                ' we resume below.
                If targets.Count > 0 Then
                    ReportProgress(progress,
                        $"Pausing log streams for {targets.Count} instance(s)...")
                    For Each instanceId In targets
                        Try
                            StopLogStream(instanceId)
                        Catch ex As Exception
                            _logger.LogDebug(ex,
                                "StopLogStream during purge prep failed for {Id}", instanceId)
                        End Try
                    Next

                    ' Brief yield to let in-flight
                    ' PersistPlayerObservationAsync tasks drain.
                    ' These are fire-and-forget so we can't await
                    ' them; the small delay lets the thread pool
                    ' process the queue. Any that don't complete
                    ' before our DELETE writes either succeed
                    ' against rows we're about to delete (harmless)
                    ' or fail on a transaction conflict (logged at
                    ' Debug inside their own try/catch).
                    Await Task.Delay(250)
                End If

                ' Snapshot the world from each Node. Failures are
                ' captured into result.Warnings and the instance is
                ' skipped; partial success is preferable to aborting
                ' the whole operation over one Node's outage.
                Dim snapshots As New List(Of InstanceSnapshot)
                For i = 0 To targets.Count - 1
                    Dim instanceId = targets(i)
                    ReportProgress(progress,
                        $"Snapshotting instance {i + 1} of {targets.Count}...")
                    Dim snap = Await SnapshotInstanceStateAsync(instanceId)
                    If snap IsNot Nothing Then
                        snapshots.Add(snap)
                    Else
                        result.Warnings.Add(
                            $"Failed to snapshot instance {instanceId} — skipped during rebuild.")
                        result.InstancesSkipped += 1
                    End If
                Next

                ' Atomic purge + rebuild in one transaction. Either
                ' the whole thing commits or the whole thing rolls
                ' back — no half-purged DB state.
                ReportProgress(progress, "Purging and rebuilding history rows...")
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Using transaction = Await db.Database.BeginTransactionAsync()
                        ' Purge — ExecuteDeleteAsync is single-round-
                        ' trip bulk delete (EF Core 7+), same
                        ' pattern ChatRetentionPruner uses.
                        Await db.PlayerActivity.ExecuteDeleteAsync()
                        Await db.ChatMessages.ExecuteDeleteAsync()
                        Await db.SessionHosts.ExecuteDeleteAsync()
                        Await db.PlayerSessions.ExecuteDeleteAsync()

                        ' Rebuild — tracked Adds + one SaveChanges
                        ' at the end. Both deletes (above) and
                        ' inserts (below) enrol in the open
                        ' transaction automatically.
                        For Each snap In snapshots
                            RebuildInstanceRows(db, snap, result)
                        Next

                        Await db.SaveChangesAsync()
                        Await transaction.CommitAsync()
                    End Using
                End Using

                ' Prime Manager-side caches BEFORE resuming SSE
                ' streams. _activePlayers must contain the current
                ' players' names so the Node's SSE ring-buffer
                ' replay (typically the last ~4096 lines) doesn't
                ' re-fire join events as duplicate PlayerActivity
                ' rows. _chatCursors must be set so the next chat-
                ' mirror tick doesn't re-insert chat rows we just
                ' put in via the rebuild.
                If snapshots.Count > 0 Then
                    ReportProgress(progress, "Priming Manager-side caches...")
                    For Each snap In snapshots
                        Try
                            PrimePostRebuildCaches(snap)
                        Catch ex As Exception
                            _logger.LogDebug(ex,
                                "PrimePostRebuildCaches failed for {Id}", snap.InstanceId)
                        End Try
                    Next
                End If

                ' Resume SSE streams. EnsureLogStreamAsync is
                ' idempotent and handles its own parse-rule
                ' re-registration for nodes that may have restarted
                ' in the meantime.
                If targets.Count > 0 Then
                    ReportProgress(progress,
                        $"Resuming log streams for {targets.Count} instance(s)...")
                    For Each instanceId In targets
                        Try
                            Await EnsureLogStreamAsync(instanceId)
                        Catch ex As Exception
                            _logger.LogWarning(ex,
                                "Failed to resume log stream for {Id} after rebuild", instanceId)
                            result.Warnings.Add(
                                $"Failed to resume log stream for instance {instanceId}: {ex.Message}")
                        End Try
                    Next
                End If

                result.InstancesRebuilt = snapshots.Count
                sw.Stop()
                result.DurationMs = sw.ElapsedMilliseconds

                _logger.LogInformation(
                    "Purge & rebuild completed — {Instances} instance(s), {PA} player activity, {Chat} chat (filtered {Filtered}), {SH} session host, {PS} player session row(s), {W} warning(s), {Ms}ms",
                    result.InstancesRebuilt, result.PlayerActivityRowsCreated,
                    result.ChatRowsCreated, result.ChatRowsFilteredOut,
                    result.SessionHostRowsCreated, result.PlayerSessionRowsCreated,
                    result.Warnings.Count, result.DurationMs)

                Return result

            Catch ex As Exception
                _logger.LogError(ex, "Purge & rebuild failed — transaction rolled back")
                result.Warnings.Add(
                    $"Operation failed and was rolled back; no rows were deleted. Reason: {ex.Message}")
                sw.Stop()
                result.DurationMs = sw.ElapsedMilliseconds
                Return result
            Finally
                _purgeLock.Release()
            End Try
        End Function

        ''' <summary>
        ''' Identifies which instances qualify for the rebuild:
        ''' Running state, active SSE log stream, attached node.
        ''' Synchronous — reads only in-memory state and one DB
        ''' lookup for detached node IDs. Returns an empty list
        ''' rather than Nothing when no instances qualify.
        ''' </summary>
        Private Function IdentifyRebuildTargets() As List(Of String)
            Dim targets As New List(Of String)

            ' Build the detached-nodes set up-front so the per-
            ' instance filter doesn't re-query the DB each pass.
            Dim detachedNodes As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    For Each id In db.Nodes.
                            Where(Function(n) Not n.IsEnabled).
                            Select(Function(n) n.NodeId).
                            ToList()
                        detachedNodes.Add(id)
                    Next
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex,
                    "Failed to load detached node list during rebuild target identification")
            End Try

            For Each kvp In _liveStates
                Dim instanceId = kvp.Key
                Dim state = kvp.Value
                If state Is Nothing Then Continue For
                If state.CurrentState <> GSM.Plugin.InstanceState.Running Then Continue For
                If Not _logStreamCancellations.ContainsKey(instanceId) Then Continue For

                Dim nodeId = GetNodeIdForInstance(instanceId)
                If Not String.IsNullOrEmpty(nodeId) AndAlso
                   detachedNodes.Contains(nodeId) Then Continue For

                targets.Add(instanceId)
            Next

            Return targets
        End Function

        ''' <summary>
        ''' Public wrapper around IdentifyRebuildTargets so UI
        ''' forms can preview which instances will be affected by
        ''' a purge+rebuild before the operator confirms. Used by
        ''' the Tools menu "Purge & Rebuild History..." dialog to
        ''' render the affected-instances list.
        ''' </summary>
        Public Function GetRebuildTargetIds() As IReadOnlyList(Of String)
            Return IdentifyRebuildTargets()
        End Function

        ''' <summary>
        ''' Snapshots one instance's state from the Node — players,
        ''' server state, full chat history. Returns Nothing if any
        ''' of the three calls fails or if session identity can't be
        ''' resolved; caller logs into the result's Warnings and
        ''' skips the instance from the rebuild.
        ''' </summary>
        Private Async Function SnapshotInstanceStateAsync(
                instanceId As String) As Task(Of InstanceSnapshot)
            Try
                Dim sessionIdentity = ResolveSessionIdentity(instanceId)
                If String.IsNullOrEmpty(sessionIdentity) Then
                    _logger.LogWarning(
                        "Cannot resolve session identity for {Id} during rebuild snapshot",
                        instanceId)
                    Return Nothing
                End If

                Dim client = GetClientForInstance(instanceId)
                If client Is Nothing Then
                    _logger.LogWarning(
                        "No node client available for {Id} during rebuild snapshot", instanceId)
                    Return Nothing
                End If

                Dim players = Await client.GetPlayersAsync(instanceId, CancellationToken.None)
                Dim serverState = Await client.GetServerStateAsync(instanceId, CancellationToken.None)
                Dim chat = Await client.GetChatHistoryAsync(
                    instanceId, Nothing, ChatRebuildFetchLimit, CancellationToken.None)

                Return New InstanceSnapshot With {
                    .InstanceId = instanceId,
                    .SessionIdentity = sessionIdentity,
                    .NodeId = GetNodeIdForInstance(instanceId),
                    .Players = If(players, CType(New List(Of PlayerSession)(), IReadOnlyList(Of PlayerSession))),
                    .ServerState = If(serverState, New ServerStateResponse()),
                    .Chat = If(chat, CType(New List(Of ChatMessage)(), IReadOnlyList(Of ChatMessage)))
                }
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to snapshot {Id} during rebuild", instanceId)
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Writes the rebuilt rows for one instance into the open
        ''' DbContext. Caller wraps the whole batch in a transaction
        ''' and calls SaveChangesAsync once at the end across all
        ''' instances. Increments the matching count properties on
        ''' result for the final summary dialog.
        ''' </summary>
        Private Sub RebuildInstanceRows(db As GsmDbContext,
                                         snap As InstanceSnapshot,
                                         result As PurgeAndRebuildResult)
            ' ---- SessionHost (one open row per loaded tile) ----
            If snap.ServerState IsNot Nothing AndAlso
               Not String.IsNullOrEmpty(snap.ServerState.TileName) Then
                Dim hostedFromUtc As DateTime
                If snap.ServerState.LastUpdatedUtc > DateTime.MinValue Then
                    hostedFromUtc = DateTime.SpecifyKind(
                        snap.ServerState.LastUpdatedUtc, DateTimeKind.Utc)
                Else
                    ' Fall back to earliest currently-connected
                    ' player's JoinedUtc as a lower bound. The tile
                    ' must have been loaded before any current
                    ' player joined it — provably correct, even if
                    ' the Node didn't supply an updated_at timestamp.
                    Dim earliestJoin As DateTime = DateTime.MinValue
                    Dim hasJoin As Boolean = False
                    For Each sess In snap.Players
                        If sess Is Nothing Then Continue For
                        If sess.JoinedUtc <= DateTime.MinValue Then Continue For
                        If Not hasJoin OrElse sess.JoinedUtc < earliestJoin Then
                            earliestJoin = sess.JoinedUtc
                            hasJoin = True
                        End If
                    Next
                    If hasJoin Then
                        hostedFromUtc = DateTime.SpecifyKind(earliestJoin, DateTimeKind.Utc)
                    Else
                        ' No real timestamp available at all — skip
                        ' the SessionHost row rather than fabricate
                        ' one. PlayerActivity rows for this instance
                        ' will render with the fallback Source label
                        ' until the next live TileLoaded event fires.
                        Return
                    End If
                End If

                db.SessionHosts.Add(New SessionHostEntity With {
                    .HostId = Guid.NewGuid().ToString("N"),
                    .SessionIdentity = snap.SessionIdentity,
                    .InstanceId = snap.InstanceId,
                    .HostedFromUtc = hostedFromUtc,
                    .HostedUntilUtc = Nothing,
                    .TileName = snap.ServerState.TileName
                })
                result.SessionHostRowsCreated += 1
            End If

            ' ---- PlayerActivity + PlayerSessions ----
            ' One "join" row per currently-connected player, stamped
            ' with the real JoinedUtc from the Node's in-memory
            ' session. Identity columns (CharacterId / PlatformUserId
            ' / DisplayName) come straight from the session — the
            ' Node has already done the identity-resolution work.
            For Each sess In snap.Players
                If sess Is Nothing Then Continue For

                ' PlayerName is the raw login-line string — same
                ' shape PersistPlayerObservationAsync uses. Prefer
                ' PlatformPersona (Steam handle / Xbox gamertag),
                ' fall back to DisplayName if persona is empty.
                Dim playerName = If(Not String.IsNullOrEmpty(sess.PlatformPersona),
                                    sess.PlatformPersona,
                                    If(Not String.IsNullOrEmpty(sess.DisplayName),
                                       sess.DisplayName, ""))
                If String.IsNullOrEmpty(playerName) Then Continue For

                Dim joinedUtc As DateTime
                If sess.JoinedUtc > DateTime.MinValue Then
                    joinedUtc = DateTime.SpecifyKind(sess.JoinedUtc, DateTimeKind.Utc)
                Else
                    ' Node lacks a join timestamp for this session.
                    ' This shouldn't happen for sessions established
                    ' through the normal join path — every PlayerSession
                    ' gets its JoinedUtc stamped at FindOrCreateSession
                    ' time from the parsing log line's timestamp. If
                    ' we see MinValue, something upstream is broken and
                    ' synthesising a timestamp would be misleading. Skip
                    ' the row and log it as a warning.
                    _logger.LogWarning(
                        "PlayerSession for {Id}/{Name} has no JoinedUtc — skipping rebuild row",
                        snap.InstanceId, playerName)
                    Continue For
                End If

                db.PlayerActivity.Add(New PlayerActivityEntity With {
                    .ActivityId = Guid.NewGuid().ToString("N"),
                    .SessionIdentity = snap.SessionIdentity,
                    .NodeId = snap.NodeId,
                    .InstanceId = snap.InstanceId,
                    .TimestampUtc = joinedUtc,
                    .PlayerName = playerName,
                    .EventKind = "join",
                    .CharacterId = sess.CharacterId,
                    .PlatformUserId = sess.PlatformUserId,
                    .DisplayName = sess.DisplayName
                })
                result.PlayerActivityRowsCreated += 1

                db.PlayerSessions.Add(New PlayerSessionEntity With {
                    .PlayerSessionId = Guid.NewGuid().ToString("N"),
                    .SessionIdentity = snap.SessionIdentity,
                    .PlayerName = playerName,
                    .FirstSeenUtc = joinedUtc,
                    .LastSeenUtc = joinedUtc,
                    .LastHostInstanceId = snap.InstanceId
                })
                result.PlayerSessionRowsCreated += 1
            Next

            ' ---- ChatMessages (filtered) ----
            Dim filtered = FilterChatToCurrentSessions(snap.Chat, snap.Players)
            For Each msg In filtered.Kept
                db.ChatMessages.Add(New ChatMessageEntity With {
                    .MessageId = Guid.NewGuid().ToString("N"),
                    .SessionIdentity = snap.SessionIdentity,
                    .NodeId = snap.NodeId,
                    .InstanceId = snap.InstanceId,
                    .TimestampUtc = DateTime.SpecifyKind(msg.TimestampUtc, DateTimeKind.Utc),
                    .DisplayName = msg.DisplayName,
                    .PlatformUserId = msg.PlatformUserId,
                    .CharacterId = msg.CharacterId,
                    .Text = msg.Text
                })
                result.ChatRowsCreated += 1
            Next
            result.ChatRowsFilteredOut += filtered.Dropped
        End Sub

        ''' <summary>
        ''' Filters a chat list down to only rows that belong to a
        ''' currently-connected player AND are timestamped at or
        ''' after that player's most recent join. Identity match
        ''' priority: CharacterId (strongest, stable per-character) →
        ''' PlatformUserId (account-stable) → DisplayName (case-
        ''' insensitive, fallback for older rows where identity
        ''' columns weren't bound at chat-time).
        '''
        ''' Returns kept rows + count of dropped rows. The dropped
        ''' count flows into the result summary so the operator can
        ''' see how much chat was filtered out (high counts may
        ''' indicate a noisy server with lots of disconnected-player
        ''' chat, or a long-running tile with old chat that's no
        ''' longer relevant).
        '''
        ''' Friend Shared so UiPanels.InstancePanel can reuse the
        ''' same filter on its Chat tab — the InstancePanel polls
        ''' the Node's chat history directly (Node holds the full
        ''' cross-session chat log persistently), and without this
        ''' filter would show chat from previous sessions and
        ''' disconnected players alongside current activity. Same
        ''' "current-session only" semantics across both surfaces;
        ''' the History window remains the authoritative cross-
        ''' session view.
        ''' </summary>
        Friend Shared Function FilterChatToCurrentSessions(
                chat As IReadOnlyList(Of ChatMessage),
                players As IReadOnlyList(Of PlayerSession)) As ChatFilterResult
            Dim ret As New ChatFilterResult With {
                .Kept = New List(Of ChatMessage)(),
                .Dropped = 0
            }

            If chat Is Nothing OrElse chat.Count = 0 Then Return ret
            If players Is Nothing OrElse players.Count = 0 Then
                ret.Dropped = chat.Count
                Return ret
            End If

            For Each msg In chat
                If msg Is Nothing Then
                    ret.Dropped += 1
                    Continue For
                End If

                Dim matched As PlayerSession = Nothing
                For Each sess In players
                    If sess Is Nothing Then Continue For

                    ' Tier 1: CharacterId (strongest).
                    If Not String.IsNullOrEmpty(sess.CharacterId) AndAlso
                       Not String.IsNullOrEmpty(msg.CharacterId) AndAlso
                       String.Equals(sess.CharacterId, msg.CharacterId,
                                     StringComparison.Ordinal) Then
                        matched = sess
                        Exit For
                    End If

                    ' Tier 2: PlatformUserId.
                    If Not String.IsNullOrEmpty(sess.PlatformUserId) AndAlso
                       Not String.IsNullOrEmpty(msg.PlatformUserId) AndAlso
                       String.Equals(sess.PlatformUserId, msg.PlatformUserId,
                                     StringComparison.Ordinal) Then
                        matched = sess
                        Exit For
                    End If

                    ' Tier 3: DisplayName fallback for legacy rows
                    ' where identity columns weren't bound at
                    ' chat-time. Case-insensitive because UE4
                    ' display names are case-stable but the chat
                    ' lines and the persona-resolution path don't
                    ' guarantee identical casing across both.
                    If Not String.IsNullOrEmpty(sess.DisplayName) AndAlso
                       Not String.IsNullOrEmpty(msg.DisplayName) AndAlso
                       String.Equals(sess.DisplayName, msg.DisplayName,
                                     StringComparison.OrdinalIgnoreCase) Then
                        matched = sess
                        Exit For
                    End If
                Next

                If matched Is Nothing Then
                    ret.Dropped += 1
                    Continue For
                End If

                ' Reject chat from before the player's current
                ' session began. This drops chat from a prior
                ' session of the same player if they reconnected.
                If msg.TimestampUtc < matched.JoinedUtc Then
                    ret.Dropped += 1
                    Continue For
                End If

                ret.Kept.Add(msg)
            Next

            Return ret
        End Function

        ''' <summary>
        ''' Primes _activePlayers and _chatCursors for one instance
        ''' so that the post-rebuild SSE stream replay (Node ring-
        ''' buffer tail) doesn't re-fire join events as duplicate
        ''' rows and the next chat-mirror tick doesn't re-insert
        ''' chat we just rebuilt.
        '''
        ''' _activePlayers cache: cleared and repopulated with the
        ''' current PlayerNames. HandlePlayerJoin's set-membership
        ''' check then no-ops for any replayed join.
        '''
        ''' _chatCursors cache: set to MAX(snap.Chat.TimestampUtc) —
        ''' the timestamp of the most-recent chat row we fetched
        ''' from the Node. The next MirrorChatForInstanceAsync tick
        ''' fetches only rows newer than this cursor, so the entire
        ''' fetched chat (kept AND filtered-out) is excluded from
        ''' re-mirroring. If no chat was fetched, fall back to
        ''' DateTime.UtcNow so the cursor still floors out future
        ''' chat ingestion at "now or later."
        '''
        ''' UTC kind is explicitly stamped on the cursor for the
        ''' same reason SeedChatCursor stamps it: NodeHttpClient
        ''' serialises DateTime via ToString("o") which only emits
        ''' the trailing "Z" when Kind=Utc; without it, the Node
        ''' parses the cursor as local time and shifts by the
        ''' Manager's UTC offset, filtering out chat that should
        ''' have been returned.
        ''' </summary>
        Private Sub PrimePostRebuildCaches(snap As InstanceSnapshot)
            ' --- _activePlayers ---
            Dim bucket = _activePlayers.GetOrAdd(snap.InstanceId,
                Function(id) New HashSet(Of String)(StringComparer.OrdinalIgnoreCase))
            SyncLock bucket
                bucket.Clear()
                For Each sess In snap.Players
                    If sess Is Nothing Then Continue For
                    Dim playerName = If(Not String.IsNullOrEmpty(sess.PlatformPersona),
                                        sess.PlatformPersona,
                                        If(Not String.IsNullOrEmpty(sess.DisplayName),
                                           sess.DisplayName, ""))
                    If Not String.IsNullOrEmpty(playerName) Then
                        bucket.Add(playerName)
                    End If
                Next
            End SyncLock

            ' --- _chatCursors ---
            Dim cursor As DateTime = DateTime.UtcNow
            If snap.Chat IsNot Nothing Then
                For Each msg In snap.Chat
                    If msg Is Nothing Then Continue For
                    If msg.TimestampUtc > cursor Then cursor = msg.TimestampUtc
                Next
            End If
            _chatCursors(snap.InstanceId) = DateTime.SpecifyKind(cursor, DateTimeKind.Utc)
        End Sub

        ''' <summary>
        ''' Safe wrapper around IProgress.Report — swallows any
        ''' exceptions raised by the progress consumer so a buggy
        ''' UI handler can't abort the purge+rebuild itself.
        ''' </summary>
        Private Shared Sub ReportProgress(progress As IProgress(Of String), msg As String)
            If progress Is Nothing Then Return
            Try
                progress.Report(msg)
            Catch
                ' Progress callback failures don't affect the operation.
            End Try
        End Sub

        ''' <summary>
        ''' Internal DTO bundling everything the rebuild needs for
        ''' one instance into a single object so the per-instance
        ''' rebuild logic can be passed a single argument instead of
        ''' four separate ones. Filled by SnapshotInstanceStateAsync,
        ''' consumed by RebuildInstanceRows and
        ''' PrimePostRebuildCaches.
        ''' </summary>
        Private Class InstanceSnapshot
            Public Property InstanceId As String
            Public Property SessionIdentity As String
            Public Property NodeId As String
            Public Property Players As IReadOnlyList(Of PlayerSession)
            Public Property ServerState As ServerStateResponse
            Public Property Chat As IReadOnlyList(Of ChatMessage)
        End Class

        ''' <summary>
        ''' Result of FilterChatToCurrentSessions — the kept rows
        ''' plus how many were dropped. Using a small named class
        ''' rather than a ValueTuple keeps the call site readable
        ''' in VB.NET, where tuple-element access syntax is less
        ''' visually distinct than C#'s.
        ''' </summary>
        Friend Class ChatFilterResult
            Public Property Kept As List(Of ChatMessage)
            Public Property Dropped As Integer
        End Class

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
        ''' Startup config render. If the plugin implements
        ''' IStartupFileProvider, rewrite its declared config files on the
        ''' node from the merged instance config just before launch — how
        ''' file-only games (no launch args) get allocator-managed values
        ''' into the game, and how text that garbles through the command
        ''' line (server names) reaches the engine cleanly.
        '''
        ''' Best-effort (O1): a read/write failure is logged and the launch
        ''' proceeds with the file's last on-disk value — a transient node
        ''' hiccup shouldn't block an otherwise-fine start. Reuses the same
        ''' /files endpoints (and allowedRoots / extension derivation) as
        ''' the IInstanceFileEditorProvider panel.
        ''' </summary>
        Private Async Function ApplyStartupFileRendersAsync(plugin As IGamePlugin,
                                                            instanceConfig As InstanceConfig,
                                                            client As INodeClient,
                                                            instanceId As String,
                                                            installPath As String) As Task
            Dim provider = TryCast(plugin, IStartupFileProvider)
            If provider Is Nothing Then Return

            Dim paths As IReadOnlyList(Of String)
            Try
                paths = provider.GetStartupFiles(instanceConfig)
            Catch ex As Exception
                _logger.LogWarning(ex, "Startup-file render: GetStartupFiles threw for {Id}", instanceId)
                Return
            End Try
            If paths Is Nothing Then Return

            Dim anyPath As Boolean = False
            Dim allPresent As Boolean = True

            For Each relPath In paths
                If String.IsNullOrWhiteSpace(relPath) Then Continue For
                anyPath = True
                Try
                    Dim roots = StartupFileAllowedRoots(relPath)
                    Dim exts = StartupFileAllowedExtensions(relPath)

                    ' Current on-disk content; a 404 means the file
                    ' doesn't exist yet -> empty string.
                    Dim existing As String = ""
                    Try
                        Using ms As New MemoryStream()
                            Await client.DownloadFileAsync(instanceId, installPath, relPath,
                                                           roots, exts, ms, CancellationToken.None)
                            existing = Encoding.UTF8.GetString(ms.ToArray())
                        End Using
                    Catch ex As NodeApiException When ex.StatusCode.HasValue AndAlso
                                                      ex.StatusCode.Value = HttpStatusCode.NotFound
                        existing = ""
                    End Try

                    ' Slice 5 readiness tracking: a still-absent file
                    ' means the game hasn't generated it yet.
                    If String.IsNullOrEmpty(existing) Then allPresent = False

                    Dim rendered As String = Nothing
                    Try
                        rendered = provider.RenderStartupFile(relPath, instanceConfig, existing)
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "Startup-file render: RenderStartupFile threw for {Path} on {Id}",
                            relPath, instanceId)
                        Continue For
                    End Try

                    ' Nothing or unchanged -> nothing to write.
                    If rendered Is Nothing OrElse
                       String.Equals(rendered, existing, StringComparison.Ordinal) Then
                        Continue For
                    End If

                    Dim bytes = Encoding.UTF8.GetBytes(rendered)
                    Using ms As New MemoryStream(bytes, writable:=False)
                        Await client.UploadFileAsync(instanceId, installPath, relPath,
                                                     roots, exts, ms,
                                                     overwrite:=True,
                                                     cancellation:=CancellationToken.None)
                    End Using
                    _logger.LogInformation("Startup-file render: wrote {Path} for {Id}",
                                           relPath, instanceId)
                Catch ex As Exception
                    ' O1 best-effort: warn and proceed; the file keeps its
                    ' last value and the launch goes ahead.
                    allPresent = False
                    _logger.LogWarning(ex,
                        "Startup-file render: failed for {Path} on {Id}; launch proceeds with last value",
                        relPath, instanceId)
                End Try
            Next

            ' Slice 5: once every declared startup file exists on the node
            ' (the game has generated them — true from the 2nd launch on
            ' for games like Windrose), flip a per-instance readiness flag.
            ' The EditInstanceForm "applies from the 2nd launch" notice
            ' shows until this is set. Stored in AppSettings (not instance
            ' ConfigJson) so a config-edit save can't clobber it and it
            ' never rides along in CustomFields. Best-effort.
            If anyPath AndAlso allPresent Then
                Try
                    Using scope = ManagerProgram.Services.CreateScope()
                        Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                        Dim key = GsmDataExtensions.StartupFilesReadyKey(instanceId)
                        If Not db.GetSettingBool(key, False) Then
                            db.SetSettingBool(key, True)
                            db.SaveChanges()
                        End If
                    End Using
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "Startup-file readiness flag write failed for {Id}", instanceId)
                End Try
            End If
        End Function

        ''' <summary>
        ''' Mirrors InstanceFileEditorPanel.DerivedAllowedRoots: a file at
        ''' the install root uses its own name as the root (exact-match on
        ''' the node); a file under a subdir uses the parent dir.
        ''' </summary>
        Private Shared Function StartupFileAllowedRoots(relPath As String) As IReadOnlyList(Of String)
            Dim rel = If(relPath, "").Replace("\"c, "/"c)
            Dim slashIdx = rel.LastIndexOf("/"c)
            If slashIdx < 0 Then Return New String() {rel}
            Return New String() {rel.Substring(0, slashIdx)}
        End Function

        Private Shared Function StartupFileAllowedExtensions(relPath As String) As IReadOnlyList(Of String)
            Dim ext = Path.GetExtension(relPath)
            If String.IsNullOrEmpty(ext) Then Return New List(Of String)
            Return New String() {ext}
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
        '''
        ''' Public so UI surfaces that need to render or validate
        ''' the same merged view the runtime sees — e.g.
        ''' InstancePanel.BuildPreFlightValidationWarnings, which
        ''' calls plugin.ValidateConfig() before the user confirms
        ''' a Start — don't have to reproduce the merge logic. An
        ''' earlier version of the pre-flight did its own two-
        ''' layer install+instance merge and surfaced spurious
        ''' "CustomerKey is required" warnings for Last Oasis
        ''' installations whose CustomerKey lived on a linked
        ''' Realm SharedConfigGroup instead of in install
        ''' ConfigJson — the group layer was simply absent from
        ''' the validator's view. Routing all merged-config
        ''' reads through this single method prevents that class
        ''' of drift from recurring.
        ''' </summary>
        Public Function GetMergedCustomFields(instanceId As String) As Dictionary(Of String, String)
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
                ' Can't dedupe without a node key — log once per call.
                ' Concise at Warning; full stack only at Debug.
                _logger.LogWarning("Failed to refresh state for {Id}: {Err}", instanceId, ex.Message)
                _logger.LogDebug(ex, "Failed to refresh state for {Id} (connection failure detail)", instanceId)
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
                    _logger.LogWarning(
                        "Node {NodeId} unreachable while polling instance {Id}: {Err} (further failures from this node will be suppressed for up to {Window} minute(s))",
                        nodeId, instanceId, ex.Message, FailureHeartbeatMinutes)
                Else
                    _logger.LogWarning(
                        "Node {NodeId} still unreachable: {Err} (suppressed {Count} similar warning(s) over the last ~{Window} minute(s))",
                        nodeId, ex.Message, suppressedAtLog, FailureHeartbeatMinutes)
                End If
                ' Full stack only at Debug/verbose — keeps the Warning-level
                ' log readable across a long outage.
                _logger.LogDebug(ex, "Node {NodeId} connection-failure detail", nodeId)
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

    ' ============================================================
    '  PurgeAndRebuildResult — outcome of
    '  InstanceManager.PurgeAndRebuildHistoryAsync
    '
    '  Returned to the caller (Tools menu trigger, slash command,
    '  test harness) for rendering a "what happened" summary.
    '  Counts increment as RebuildInstanceRows processes each
    '  per-instance snapshot. Warnings accumulate non-fatal
    '  failures (Node fetch errors, instances skipped because no
    '  tile loaded, log-stream resume failures) so a partial-
    '  success run still tells the operator exactly what didn't
    '  work.
    '
    '  ChatRowsFilteredOut is the count of chat rows the Node
    '  returned that didn't survive the identity + JoinedUtc
    '  filter. High values mean lots of chat from disconnected
    '  players, OR chat from previous sessions of currently-
    '  connected players — either way, the operator can see how
    '  aggressive the filter was for transparency.
    ' ============================================================

    Public Class PurgeAndRebuildResult
        Public Property InstancesRebuilt As Integer
        Public Property InstancesSkipped As Integer
        Public Property PlayerActivityRowsCreated As Integer
        Public Property PlayerSessionRowsCreated As Integer
        Public Property ChatRowsCreated As Integer
        Public Property ChatRowsFilteredOut As Integer
        Public Property SessionHostRowsCreated As Integer
        Public Property DurationMs As Long
        Public Property Warnings As New List(Of String)
    End Class

End Namespace