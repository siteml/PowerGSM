Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Manager
Imports GSM.Manager.Data

' ============================================================
'  RestartCoordinator
'
'  Phase 3a of the automation refactor. Owns two concerns:
'
'    1. Restart slot allocation (installation + node scoped
'       semaphores). Acquire/Release are defined here so
'       Phase 3b can wire them into InstanceManager's restart
'       path without further coordinator changes. In Phase 3a
'       nothing calls Acquire/Release yet \u2014 the API is in
'       place but dormant.
'
'    2. Ready-signal waits. WaitForReadySignalAsync blocks
'       until the plugin's declared "ready for next" signal
'       fires for a given instance, the timeout elapses, or
'       the instance reaches a terminal state (Crashed /
'       CrashLoopHalted / Stopped). This is the piece
'       WaitForReadySignalAction hits via RuleContext; it's
'       live in Phase 3a.
'
'  Signal model: when callers start a wait, we register a
'  PendingSignal entry in a per-instance dictionary. Each
'  entry holds a TaskCompletionSource plus the signal kind
'  + match value the caller is waiting for. InstanceManager,
'  on observing a parsed event that could be a match (e.g.
'  TileLoaded), calls NotifySignalObserved; the coordinator
'  checks pending entries and completes whichever TCS match.
'
'  Terminal-state watching: a separate background probe polls
'  InstanceManager.GetLiveState for the instance every 1s
'  while the wait is active, completing the TCS with false
'  if the instance hits a terminal state. Keeps a dead-or-
'  stuck instance from holding a restart queue indefinitely.
'
'  Why TCS and not events: waits are rare (one per
'  coordinated restart, so maybe a few per day) and
'  transient. A dictionary-of-TCS is simpler than plumbing
'  another public event on InstanceManager, and it gives us
'  clean per-wait cancellation + timeout semantics via
'  Task.WhenAny.
' ============================================================

Namespace GSM.Manager.Core

    Public Class RestartCoordinator

        ''' <summary>
        ''' Fallback wait used when a plugin doesn't implement
        ''' IReadySignalProvider and no explicit timeout was
        ''' supplied on the WaitForReadySignalAction. Short
        ''' enough that a misconfigured rule doesn't hang a
        ''' restart queue forever; long enough that a
        ''' slow-booting server still gets a real grace period.
        ''' </summary>
        Private Const DefaultGraceDelaySeconds As Integer = 30

        Private ReadOnly _logger As ILogger(Of RestartCoordinator)
        Private ReadOnly _pluginRegistry As PluginRegistry

        ' Injected lazily to avoid a ctor cycle: InstanceManager
        ' needs to call INTO the coordinator from its log-event
        ' handlers, and the coordinator needs to READ state from
        ' InstanceManager for terminal-state detection. DI would
        ' happily resolve both as singletons, but if we declared
        ' InstanceManager as a ctor dep here and coordinator as a
        ' ctor dep there, we'd deadlock construction. The setter
        ' pattern is ugly but avoids that entirely. ManagerProgram
        ' wires it after both singletons exist.
        Private _instanceManager As InstanceManager

        ' Per-installation semaphores for restart concurrency.
        ' Created lazily on first acquire so we don't have to
        ' pre-populate from the DB on startup. Keyed by
        ' installation ID. Count is InstallationEntity.MaxConcurrentRestarts
        ' (defaults to 1 per installation).
        Private ReadOnly _installationSemaphores As _
            New ConcurrentDictionary(Of String, SemaphoreSlim)

        ' Per-node semaphores. Only populated for nodes whose
        ' MaxConcurrentRestarts is > 0 (i.e. "enforce a node-wide
        ' cap"). Zero means "no node-wide limit", in which case
        ' Acquire skips the node gate entirely.
        Private ReadOnly _nodeSemaphores As _
            New ConcurrentDictionary(Of String, SemaphoreSlim)

        ' Active ready-signal waits. Keyed by instance ID. The
        ' coordinator only supports ONE in-flight wait per
        ' instance at a time \u2014 in the coordinated-restart flow
        ' this is the natural shape (you don't restart the same
        ' instance twice concurrently), and the simpler data
        ' structure is worth the restriction.
        Private ReadOnly _pendingSignals As _
            New ConcurrentDictionary(Of String, PendingSignal)

        ' Currently-held slots, keyed by instance ID. Enables
        ' the released-by-instance-id API: AcquireForInstanceAsync
        ' stashes the slot here, ReleaseForInstance looks it up
        ' and releases. Same 'one coordinated restart per
        ' instance' invariant as _pendingSignals \u2014 a second
        ' acquire while a first is still held would either have
        ' to wait (the semaphore handles that naturally) or fail
        ' at the dictionary level (we choose the latter to surface
        ' buggy rule authoring).
        Private ReadOnly _heldSlots As _
            New ConcurrentDictionary(Of String, RestartSlot)

        Public Sub New(pluginRegistry As PluginRegistry,
                       logger As ILogger(Of RestartCoordinator))
            _pluginRegistry = pluginRegistry
            _logger = logger
        End Sub

        ''' <summary>
        ''' Late-bound setter for InstanceManager to break the
        ''' ctor cycle described above. Called once from
        ''' ManagerProgram after both singletons exist. Calling
        ''' it twice is harmless but unnecessary.
        ''' </summary>
        Public Sub AttachInstanceManager(instanceManager As InstanceManager)
            _instanceManager = instanceManager
        End Sub

        ' ============================================================
        '  Ready-signal waits (live in Phase 3a)
        ' ============================================================

        ''' <summary>
        ''' Blocks until the plugin's declared ready-for-next
        ''' signal fires for the instance, the timeout elapses,
        ''' or the instance reaches a terminal state. Returns
        ''' True iff the signal fired; False otherwise (timeout,
        ''' terminal state, or unresolvable plugin). Safe to
        ''' call even when the plugin doesn't implement
        ''' IReadySignalProvider \u2014 falls back to a grace delay.
        ''' </summary>
        ''' <param name="instanceId">Instance to watch.</param>
        ''' <param name="timeoutSeconds">Explicit timeout. Zero
        ''' means "use the plugin's DefaultReadyTimeoutSeconds".
        ''' When the plugin doesn't provide one, this falls back
        ''' to DefaultGraceDelaySeconds.</param>
        Public Async Function WaitForReadySignalAsync(instanceId As String,
                                                       timeoutSeconds As Integer) As Task(Of Boolean)
            If String.IsNullOrEmpty(instanceId) Then Return False

            ' Resolve plugin + signal spec. If we can't find a
            ' plugin or the plugin doesn't advertise a signal,
            ' fall back to a grace delay (no event to wait for).
            Dim gameId = ResolveGameId(instanceId)
            Dim plugin As IGamePlugin = Nothing
            If Not String.IsNullOrEmpty(gameId) Then
                plugin = _pluginRegistry.GetPlugin(gameId)
            End If

            Dim signalProvider = TryCast(plugin, IReadySignalProvider)
            Dim spec As ReadySignal = Nothing
            Dim pluginDefaultTimeout As Integer = 0
            If signalProvider IsNot Nothing Then
                Try
                    spec = signalProvider.GetReadyForNextSignal()
                    pluginDefaultTimeout = signalProvider.DefaultReadyTimeoutSeconds
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "Plugin {GameId} threw from IReadySignalProvider; treating as no signal",
                        gameId)
                    spec = Nothing
                End Try
            End If

            ' Resolve effective timeout.
            Dim effectiveTimeoutSeconds As Integer
            If timeoutSeconds > 0 Then
                effectiveTimeoutSeconds = timeoutSeconds
            ElseIf pluginDefaultTimeout > 0 Then
                effectiveTimeoutSeconds = pluginDefaultTimeout
            Else
                effectiveTimeoutSeconds = DefaultGraceDelaySeconds
            End If

            ' No spec means the plugin hasn't declared a signal.
            ' Do a plain delay so the caller still gets serialised
            ' behaviour, just on a timer instead of an event.
            If spec Is Nothing Then
                _logger.LogInformation(
                    "WaitForReadySignal: no signal declared for {GameId}, using {Sec}s grace delay",
                    gameId, effectiveTimeoutSeconds)
                Try
                    Await Task.Delay(TimeSpan.FromSeconds(effectiveTimeoutSeconds))
                Catch
                End Try
                Return True
            End If

            ' Register a pending wait. Only one per instance at a
            ' time \u2014 if someone else is already waiting, we
            ' refuse rather than silently clobber their TCS.
            Dim pending As New PendingSignal With {
                .InstanceId = instanceId,
                .Kind = spec.Kind,
                .MatchValue = spec.MatchValue,
                .Tcs = New TaskCompletionSource(Of Boolean)(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            }

            If Not _pendingSignals.TryAdd(instanceId, pending) Then
                _logger.LogWarning(
                    "WaitForReadySignal: another wait is already in progress for {Id}; returning false",
                    instanceId)
                Return False
            End If

            Try
                _logger.LogInformation(
                    "WaitForReadySignal: watching {Id} for {Kind}={Match}, timeout {Sec}s",
                    instanceId, spec.Kind, spec.MatchValue, effectiveTimeoutSeconds)

                Dim timeoutTask = Task.Delay(TimeSpan.FromSeconds(effectiveTimeoutSeconds))
                Dim terminalTask = WatchTerminalStateAsync(instanceId, pending.Tcs.Task)
                Dim signalTask = pending.Tcs.Task

                Dim completed = Await Task.WhenAny(signalTask, timeoutTask, terminalTask)

                If completed Is signalTask Then
                    Return Await signalTask
                ElseIf completed Is timeoutTask Then
                    _logger.LogInformation("WaitForReadySignal: timeout for {Id}", instanceId)
                    Return False
                Else
                    ' Terminal-state watcher completed. It completes
                    ' the TCS with False internally, so the signalTask
                    ' is already done by the time we get here \u2014 but we
                    ' came out of WhenAny via terminalTask first, so
                    ' just return False directly.
                    _logger.LogInformation(
                        "WaitForReadySignal: instance {Id} reached terminal state",
                        instanceId)
                    Return False
                End If
            Finally
                Dim removed As PendingSignal = Nothing
                _pendingSignals.TryRemove(instanceId, removed)
            End Try
        End Function

        ''' <summary>
        ''' Called by InstanceManager when a parsed log event
        ''' arrives that might match somebody's pending signal.
        ''' The specific event kinds routed here are the subset
        ''' that correspond to ReadySignalKind values:
        '''   - TileLoaded \u2192 for ReadySignalKind.TileLoaded
        '''   - ServerStateChange \u2192 for ReadySignalKind.ServerStateEquals
        '''   - Custom (with ReadyMarker metadata) \u2192 for ReadySignalKind.CustomMarker
        '''
        ''' Unknown combinations silently no-op. Cheap to call
        ''' on every event; expected call volume is low (handful
        ''' per minute per instance).
        ''' </summary>
        ''' <param name="instanceId">Instance the event came from.</param>
        ''' <param name="kind">Which ReadySignalKind this event
        ''' represents. Caller (InstanceManager) is responsible for
        ''' mapping its own event taxonomy (LogEventType, etc.)
        ''' into one of these values.</param>
        ''' <param name="observedValue">For ServerStateEquals: the
        ''' actual MatchState value observed. Compared to the
        ''' pending wait's MatchValue. Ignored for other kinds.</param>
        Public Sub NotifySignalObserved(instanceId As String,
                                         kind As ReadySignalKind,
                                         observedValue As String)
            Dim pending As PendingSignal = Nothing
            If Not _pendingSignals.TryGetValue(instanceId, pending) Then Return
            If pending.Kind <> kind Then Return

            ' Kind-specific match check.
            Dim matched As Boolean = False
            Select Case kind
                Case ReadySignalKind.TileLoaded
                    ' Any TileLoaded counts.
                    matched = True
                Case ReadySignalKind.ServerStateEquals
                    matched = String.Equals(observedValue, pending.MatchValue,
                                             StringComparison.Ordinal)
                Case ReadySignalKind.CustomMarker
                    ' Any CustomMarker counts; InstanceManager only
                    ' routes ReadyMarker-tagged events here.
                    matched = True
            End Select

            If matched Then
                ' TrySetResult so a late-arriving timeout doesn't
                ' throw on a previously-completed TCS.
                pending.Tcs.TrySetResult(True)
                _logger.LogInformation(
                    "WaitForReadySignal: signal matched for {Id} ({Kind}={Match})",
                    instanceId, kind, observedValue)
            End If
        End Sub

        ' ============================================================
        '  Terminal-state watchdog
        ' ============================================================

        ''' <summary>
        ''' Polls InstanceManager.GetLiveState for instanceId once
        ''' per second until either the passed-in signalTask
        ''' completes (normal exit, handled by the caller) or we
        ''' observe a terminal state. On terminal state, completes
        ''' the caller's TCS with False. Exits cleanly either way.
        '''
        ''' Why polling instead of an InstanceManager event
        ''' subscription: InstanceManager already polls state every
        ''' 3s in its background poller, so a dedicated 1s poll here
        ''' for the rare coordinated-restart case is cheap. Adding
        ''' a public state-change event just for this would bloat
        ''' InstanceManager's surface for a one-caller concern.
        ''' </summary>
        Private Async Function WatchTerminalStateAsync(instanceId As String,
                                                        signalTask As Task(Of Boolean)) As Task
            If _instanceManager Is Nothing Then
                ' Coordinator not wired to InstanceManager yet.
                ' Can't watch for terminal state; fall through.
                Return
            End If

            While Not signalTask.IsCompleted
                Try
                    Dim state = _instanceManager.GetLiveState(instanceId)
                    If state IsNot Nothing Then
                        Select Case state.CurrentState
                            Case InstanceState.Stopped,
                                 InstanceState.Crashed,
                                 InstanceState.CrashLoopHalted
                                ' Complete the wait with False.
                                Dim pending As PendingSignal = Nothing
                                If _pendingSignals.TryGetValue(instanceId, pending) Then
                                    pending.Tcs.TrySetResult(False)
                                End If
                                Return
                        End Select
                    End If
                Catch
                End Try
                Try
                    Await Task.Delay(1000)
                Catch
                    Return
                End Try
            End While
        End Function

        ' ============================================================
        '  Instance-keyed acquire/release (used by CoordinatedRestartAction
        '  via RuleContextImpl). Wraps AcquireAsync/Release with a
        '  dictionary keyed by instanceId so callers don't have to
        '  marshal a RestartSlot across the Contracts-side IRuleContext
        '  boundary — they just say "acquire for this instance" and
        '  "release for this instance".
        ' ============================================================

        ''' <summary>
        ''' Acquire a slot scoped to an instance, stashing it in
        ''' an internal dictionary keyed by instanceId. Callers
        ''' release via ReleaseForInstance(instanceId) rather
        ''' than handing a RestartSlot around.
        '''
        ''' Returns True if acquired and stashed, False if
        ''' another coordinated restart is already in flight for
        ''' this instance. If acquired, the caller MUST
        ''' eventually call ReleaseForInstance; Try/Finally in
        ''' CoordinatedRestartAction guarantees this.
        ''' </summary>
        Public Async Function AcquireForInstanceAsync(instanceId As String) As Task(Of Boolean)
            If String.IsNullOrEmpty(instanceId) Then Return False

            ' Reject a second concurrent acquire for the same
            ' instance up front. Otherwise two slots get held
            ' for one instance and only one dict entry tracks
            ' them — a leak on release.
            If _heldSlots.ContainsKey(instanceId) Then
                _logger.LogWarning(
                    "AcquireForInstanceAsync: slot already held for {Id}; refusing second acquire",
                    instanceId)
                Return False
            End If

            Dim slot As RestartSlot = Nothing
            Try
                slot = Await AcquireAsync(instanceId, CancellationToken.None)
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "AcquireForInstanceAsync: AcquireAsync threw for {Id}", instanceId)
                Return False
            End Try

            If slot Is Nothing Then Return False

            ' Defensive: if a racing acquire stashed a slot while
            ' we were blocked on AcquireAsync, release the late
            ' arrival so we don't leak.
            If Not _heldSlots.TryAdd(instanceId, slot) Then
                Release(slot)
                _logger.LogWarning(
                    "AcquireForInstanceAsync: racing acquire lost for {Id}; released late arrival",
                    instanceId)
                Return False
            End If

            Return True
        End Function

        ''' <summary>
        ''' Release the slot previously acquired for this
        ''' instance via AcquireForInstanceAsync. No-op if
        ''' nothing is held (safe from Finally blocks that
        ''' don't know whether acquire succeeded).
        ''' </summary>
        Public Sub ReleaseForInstance(instanceId As String)
            If String.IsNullOrEmpty(instanceId) Then Return
            Dim slot As RestartSlot = Nothing
            If _heldSlots.TryRemove(instanceId, slot) Then
                Release(slot)
            End If
        End Sub

        ' ============================================================
        '  Slot acquisition (scaffolding \u2014 not yet wired in Phase 3a)
        '
        '  AcquireAsync and Release are fully functional but no
        '  existing call site invokes them yet. Phase 3b wires
        '  them into InstanceManager.RestartInstanceAsync so
        '  manual restarts and (via SequenceAction) scheduled
        '  restarts start queueing through the coordinator.
        ' ============================================================

        ''' <summary>
        ''' Acquire a restart slot for the instance. Blocks on
        ''' (1) the installation's semaphore, then (2) the node's
        ''' semaphore if the node's MaxConcurrentRestarts > 0.
        ''' Order matters: installation gate first, node gate
        ''' second \u2014 prevents a rare deadlock where two
        ''' installations on the same node both hold their own
        ''' installation semaphore and wait for the shared node
        ''' semaphore in opposite order.
        ''' </summary>
        ''' <param name="instanceId">Instance being restarted.</param>
        ''' <param name="cancellation">Token to abort the wait.
        ''' If cancelled while holding one but not both gates,
        ''' the held gate is released before the exception
        ''' propagates.</param>
        Public Async Function AcquireAsync(instanceId As String,
                                            cancellation As CancellationToken) As Task(Of RestartSlot)
            Dim installationId As String = Nothing
            Dim nodeId As String = Nothing
            Dim nodeLimit As Integer = 0
            Dim installationLimit As Integer = 1

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim inst = db.Instances.Find(instanceId)
                If inst Is Nothing Then
                    Throw New InvalidOperationException($"Instance {instanceId} not found")
                End If
                installationId = inst.InstallationId
                Dim install = db.Installations.Find(installationId)
                If install Is Nothing Then
                    Throw New InvalidOperationException($"Installation {installationId} not found")
                End If
                nodeId = install.NodeId
                If install.MaxConcurrentRestarts > 0 Then
                    installationLimit = install.MaxConcurrentRestarts
                End If
                Dim node = db.Nodes.Find(nodeId)
                If node IsNot Nothing AndAlso node.MaxConcurrentRestarts > 0 Then
                    nodeLimit = node.MaxConcurrentRestarts
                End If
            End Using

            Dim installSem = _installationSemaphores.GetOrAdd(
                installationId,
                Function(id) New SemaphoreSlim(installationLimit, installationLimit))

            Dim nodeSem As SemaphoreSlim = Nothing
            If nodeLimit > 0 Then
                nodeSem = _nodeSemaphores.GetOrAdd(
                    nodeId,
                    Function(id) New SemaphoreSlim(nodeLimit, nodeLimit))
            End If

            Await installSem.WaitAsync(cancellation)

            If nodeSem IsNot Nothing Then
                Try
                    Await nodeSem.WaitAsync(cancellation)
                Catch
                    ' Release the installation gate if we can't
                    ' acquire the node gate \u2014 otherwise we leak
                    ' the installation slot forever.
                    installSem.Release()
                    Throw
                End Try
            End If

            Return New RestartSlot With {
                .InstanceId = instanceId,
                .InstallationSemaphore = installSem,
                .NodeSemaphore = nodeSem
            }
        End Function

        ''' <summary>
        ''' Release a slot previously returned from AcquireAsync.
        ''' Calling with a Nothing slot is a no-op. Safe to call
        ''' more than once on the same slot via the sentinel flag
        ''' \u2014 double-release would otherwise push a semaphore
        ''' above its initial count.
        ''' </summary>
        Public Sub Release(slot As RestartSlot)
            If slot Is Nothing OrElse slot.Released Then Return
            slot.Released = True
            Try
                slot.NodeSemaphore?.Release()
            Catch
            End Try
            Try
                slot.InstallationSemaphore?.Release()
            Catch
            End Try
        End Sub

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Function ResolveGameId(instanceId As String) As String
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim inst = db.Instances.Find(instanceId)
                    If inst Is Nothing Then Return Nothing
                    Return inst.GameId
                End Using
            Catch
                Return Nothing
            End Try
        End Function

    End Class

    ' ============================================================
    '  Supporting types
    ' ============================================================

    ''' <summary>
    ''' One in-flight ready-signal wait.
    ''' </summary>
    Friend Class PendingSignal
        Public Property InstanceId As String
        Public Property Kind As ReadySignalKind
        Public Property MatchValue As String
        Public Property Tcs As TaskCompletionSource(Of Boolean)
    End Class

    ''' <summary>
    ''' Returned by AcquireAsync; passed back to Release to drop
    ''' the held gates. Public so Phase 3b wiring in InstanceManager
    ''' can hold the token across its stop+start sequence.
    ''' </summary>
    Public Class RestartSlot
        Public Property InstanceId As String
        Friend Property InstallationSemaphore As SemaphoreSlim
        Friend Property NodeSemaphore As SemaphoreSlim
        Friend Property Released As Boolean
    End Class

End Namespace
