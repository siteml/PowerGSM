Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  ProcessManager
'
'  The heart of the node. Responsible for:
'    - Launching game server processes
'    - Monitoring them for unexpected exit (crash detection)
'    - Applying restart policy autonomously, even when the
'      manager is unreachable
'    - Routing stdout/stderr lines into the ring buffer and
'      the crash signal detector
'    - Maintaining the authoritative in-memory instance state
'      (persisted to NodeDatabase on every transition)
'
'  State machine per instance (mirrors InstanceState enum):
'    Stopped → Starting → Running → Stopping → Stopped
'                                 ↘ Crashed → Restarting → Starting
'                                           ↘ CrashLoopHalted
'
'  Thread model:
'    Each running instance has:
'      - One stdout reader Task (reads lines from the process)
'      - One monitor Task (watches for process exit)
'    All state mutations go through TransitionState() which
'    serialises them via a per-instance SemaphoreSlim.
'    The _instances dictionary itself is a ConcurrentDictionary
'    so reads never block.
' ============================================================

Public Class ProcessManager

    Private ReadOnly _db As NodeDatabase
    Private ReadOnly _ringBuffer As RingBufferStore
    Private ReadOnly _config As NodeConfiguration
    Private ReadOnly _logger As ILogger(Of ProcessManager)

    ' Live instance tracking. Key = InstanceId.
    ' ConcurrentDictionary = safe to read from any thread without locking.
    Private ReadOnly _instances As New ConcurrentDictionary(Of String, ManagedInstance)(
        StringComparer.OrdinalIgnoreCase)

    Public Sub New(db As NodeDatabase,
                   ringBuffer As RingBufferStore,
                   config As NodeConfiguration,
                   logger As ILogger(Of ProcessManager))
        _db = db
        _ringBuffer = ringBuffer
        _config = config
        _logger = logger
    End Sub

    ' ============================================================
    '  STARTUP RE-ATTACH
    '  Called once when the node service starts. Reads persisted
    '  instance state from the database and re-attaches to any
    '  processes that were running before the node restarted.
    ' ============================================================

    Public Sub Initialise()
        _logger.LogInformation("ProcessManager: initialising")

        Dim persistedStates = _db.GetAllInstanceStates()
        For Each state In persistedStates
            Dim managed As New ManagedInstance With {
                .InstanceId = state.InstanceId,
                .DisplayName = state.DisplayName,
                .GameId = state.GameId,
                .InstallationId = state.InstallationId,
                .State = InstanceState.Stopped,    ' Assume stopped until proven otherwise
                .CrashRestartPolicy = DeserializePolicy(state.CrashRestartJson),
                .LastStartParams = DeserializeStartParams(state.LastStartParamsJson)
            }

            ' Try to re-attach to the process if we have a PID.
            If state.Pid.HasValue Then
                Try
                    Dim proc = Process.GetProcessById(state.Pid.Value)
                    If Not proc.HasExited Then
                        ' Process is still alive. Re-attach stdout monitoring.
                        managed.State = InstanceState.Running
                        managed.Process = proc
                        managed.Pid = state.Pid.Value
                        managed.StartedAt = state.StartedAt
                        _logger.LogInformation(
                            "ProcessManager: re-attached to instance {Name} (PID {Pid})",
                            state.DisplayName, state.Pid.Value)
                        StartMonitoring(managed)
                    Else
                        ' Process exited while node was down. Treat as crashed
                        ' if stop intent wasn't set.
                        If Not state.StopIntentPending Then
                            _logger.LogWarning(
                                "ProcessManager: instance {Name} (PID {Pid}) exited " &
                                "while node was offline - treating as crash",
                                state.DisplayName, state.Pid.Value)
                            ' Schedule a restart check after a short delay.
                            Task.Run(Async Function()
                                         Await Task.Delay(2000)
                                         Await HandleExitAsync(managed, proc.ExitCode,
                                                               CancellationToken.None)
                                     End Function)
                        End If
                    End If
                Catch ex As ArgumentException
                    ' PID no longer exists in the OS.
                    If Not state.StopIntentPending Then
                        _logger.LogWarning(
                            "ProcessManager: instance {Name} PID {Pid} not found " &
                            "after node restart - was it killed externally?",
                            state.DisplayName, state.Pid.Value)
                    End If
                End Try
            End If

            ' Clear the stop intent flag now that we've handled the state.
            ' If we're re-attaching to a live process, the intent is irrelevant.
            ' If the process is gone, we've already decided what to do above.
            _db.SetStopIntent(state.InstanceId, False)
            _instances.TryAdd(state.InstanceId, managed)
        Next

        _logger.LogInformation(
            "ProcessManager: initialised with {Count} instance(s)", _instances.Count)
    End Sub


    ' ============================================================
    '  START
    ' ============================================================

    Public Async Function StartAsync(request As StartInstanceRequest,
                                     cancellation As CancellationToken) As Task(Of StartInstanceResponse)

        ' Get or create the managed instance entry.
        Dim managed = _instances.GetOrAdd(request.InstanceId,
            Function(id) New ManagedInstance With {
                .InstanceId = id,
                .DisplayName = request.DisplayName,
                .GameId = request.GameId,
                .InstallationId = request.InstallationId
            })

        Await managed.Lock.WaitAsync(cancellation)
        Try
            If managed.State = InstanceState.Running OrElse
               managed.State = InstanceState.Starting Then
                Return New StartInstanceResponse With {
                    .InstanceId = request.InstanceId,
                    .State = managed.State,
                    .Pid = managed.Pid,
                    .Message = $"Instance is already {managed.State}."
                }
            End If

            ' Store the start params so we can restart with the same config
            ' if the process crashes and the manager is unreachable.
            managed.LastStartParams = request
            managed.CrashRestartPolicy = request.CrashRestartPolicy
            managed.CrashSignalPatterns = request.CrashSignalPatterns
            managed.CleanExitCodes = request.CleanExitCodes

            Return Await LaunchProcessAsync(managed, request, cancellation)
        Finally
            managed.Lock.Release()
        End Try
    End Function

    Private Async Function LaunchProcessAsync(managed As ManagedInstance,
                                               request As StartInstanceRequest,
                                               cancellation As CancellationToken) As Task(Of StartInstanceResponse)

        Await TransitionStateAsync(managed, InstanceState.Starting,
                                    "Launch requested by manager")

        ' Log startup warnings from the plugin before touching the process.
        If request.StartupWarnings?.Count > 0 Then
            For Each warning In request.StartupWarnings
                _logger.LogWarning("[{Name}] Startup warning: {Warning}",
                                    managed.DisplayName, warning)
            Next
        End If

        Try
            Dim psi As New ProcessStartInfo With {
                .FileName = request.ExecutablePath,
                .Arguments = request.Arguments,
                .WorkingDirectory = request.WorkingDirectory,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .RedirectStandardInput = True   ' Needed for stdin (Steam Guard etc)
            }

            _logger.LogInformation(
                "[{Name}] Launching: {Exe} {Args}",
                managed.DisplayName, request.ExecutablePath, request.Arguments)

            Dim proc = Process.Start(psi)
            If proc Is Nothing Then
                Throw New InvalidOperationException("Process.Start returned Nothing.")
            End If

            managed.Process = proc
            managed.Pid = proc.Id
            managed.StartedAt = DateTime.UtcNow
            managed.CrashDetectionState = CrashDetectionState.None

            ' Persist the new state immediately.
            _db.UpsertInstanceState(ToPersistedState(managed))

            Await TransitionStateAsync(managed, InstanceState.Running,
                                        $"Process started (PID {proc.Id})")

            ' Start background tasks for stdout reading and exit monitoring.
            StartMonitoring(managed)

            _logger.LogInformation(
                "[{Name}] Running (PID {Pid})", managed.DisplayName, proc.Id)

            Return New StartInstanceResponse With {
                .InstanceId = managed.InstanceId,
                .State = InstanceState.Running,
                .Pid = proc.Id,
                .StartedAt = managed.StartedAt,
                .Message = $"Started (PID {proc.Id})"
            }

        Catch ex As Exception
            _logger.LogError(ex, "[{Name}] Failed to start process: {Message}",
                             managed.DisplayName, ex.Message)
            Dim errMsg = ex.Message
            Task.Run(Function() TransitionStateAsync(managed, InstanceState.StartFailed, errMsg))
            ' Attempt auto-restart per policy if this was a start failure.
            Task.Run(Function() HandleExitAsync(managed, -1, CancellationToken.None))
            Return New StartInstanceResponse With {
                .InstanceId = managed.InstanceId,
                .State = InstanceState.StartFailed,
                .Message = $"Failed to start: {ex.Message}"
            }
        End Try
    End Function


    ' ============================================================
    '  STOP
    ' ============================================================

    Public Async Function StopAsync(instanceId As String,
                                     graceful As Boolean,
                                     timeoutMs As Integer,
                                     cancellation As CancellationToken) As Task(Of StopInstanceResponse)

        Dim managed As ManagedInstance = Nothing
        If Not _instances.TryGetValue(instanceId, managed) Then
            Return New StopInstanceResponse With {
                .InstanceId = instanceId,
                .State = InstanceState.Stopped,
                .Message = "Instance not found on this node."
            }
        End If

        Await managed.Lock.WaitAsync(cancellation)
        Try
            If managed.State = InstanceState.Stopped OrElse
               managed.State = InstanceState.Stopping Then
                Return New StopInstanceResponse With {
                    .InstanceId = instanceId,
                    .State = managed.State,
                    .Message = $"Instance is already {managed.State}."
                }
            End If

            ' Set the stop intent BEFORE touching the process.
            ' This is the flag that distinguishes a clean stop from a crash.
            ' It must be persisted so it survives a node restart.
            managed.StopIntentPending = True
            _db.SetStopIntent(instanceId, True)

            Await TransitionStateAsync(managed, InstanceState.Stopping,
                                        "Stop requested by manager")

            If managed.Process IsNot Nothing AndAlso Not managed.Process.HasExited Then
                If graceful Then
                    ' Ask nicely first.
                    managed.Process.CloseMainWindow()

                    ' Wait up to the timeout for the process to exit.
                    Dim exited = Await WaitForExitAsync(managed.Process,
                                                         timeoutMs, cancellation)
                    If Not exited Then
                        _logger.LogWarning(
                            "[{Name}] Graceful stop timed out after {Ms}ms - force killing",
                            managed.DisplayName, timeoutMs)
                        managed.Process.Kill(entireProcessTree:=True)
                    End If
                Else
                    managed.Process.Kill(entireProcessTree:=True)
                End If
            End If

            ' The monitoring task will detect the exit and call HandleExitAsync.
            ' Because StopIntentPending = True, it will transition to Stopped
            ' rather than Crashed.

            Return New StopInstanceResponse With {
                .InstanceId = instanceId,
                .State = InstanceState.Stopping,
                .Message = "Stop signal sent."
            }
        Finally
            managed.Lock.Release()
        End Try
    End Function

    Public Async Function KillAsync(instanceId As String,
                                     cancellation As CancellationToken) As Task(Of KillInstanceResponse)

        Dim managed As ManagedInstance = Nothing
        If Not _instances.TryGetValue(instanceId, managed) Then
            Return New KillInstanceResponse With {
                .InstanceId = instanceId,
                .State = InstanceState.Stopped,
                .Message = "Instance not found."
            }
        End If

        managed.StopIntentPending = True
        _db.SetStopIntent(instanceId, True)

        If managed.Process IsNot Nothing AndAlso Not managed.Process.HasExited Then
            managed.Process.Kill(entireProcessTree:=True)
        End If

        Return New KillInstanceResponse With {
            .InstanceId = instanceId,
            .State = InstanceState.Stopping,
            .Message = "Force killed."
        }
    End Function


    ' ============================================================
    '  STDIN
    '  Used for Steam Guard codes and other interactive prompts.
    ' ============================================================

    Public Function WriteStdin(instanceId As String,
                                line As String,
                                isSensitive As Boolean) As StdinResponse

        Dim managed As ManagedInstance = Nothing
        If Not _instances.TryGetValue(instanceId, managed) OrElse
           managed.Process Is Nothing OrElse managed.Process.HasExited Then
            Return New StdinResponse With {
                .Accepted = False,
                .Message = "Instance is not running or has no stdin pipe."
            }
        End If

        Try
            If Not isSensitive Then
                _logger.LogDebug("[{Name}] stdin: {Line}", managed.DisplayName, line)
            Else
                _logger.LogDebug("[{Name}] stdin: [sensitive - not logged]",
                                 managed.DisplayName)
            End If

            managed.Process.StandardInput.WriteLine(line)
            managed.Process.StandardInput.Flush()

            Return New StdinResponse With {.Accepted = True, .Message = "Written."}
        Catch ex As Exception
            Return New StdinResponse With {
                .Accepted = False,
                .Message = $"Failed to write to stdin: {ex.Message}"
            }
        End Try
    End Function


    ' ============================================================
    '  STATE QUERIES
    ' ============================================================

    Public Function GetAll() As IReadOnlyList(Of ManagedInstance)
        Return _instances.Values.ToList().AsReadOnly()
    End Function

    Public Function GetInstance(instanceId As String) As ManagedInstance
        Dim result As ManagedInstance = Nothing
        _instances.TryGetValue(instanceId, result)
        Return result
    End Function

    Public Function GetRunningCount() As Integer
        Return _instances.Values.Where(Function(i) i.State = InstanceState.Running).Count()
    End Function

    Public Function GetMetrics(instanceId As String) As InstanceMetricsResponse
        Dim managed = GetInstance(instanceId)
        If managed Is Nothing Then Return Nothing

        Dim cpuPct As Double? = Nothing
        Dim memMb As Long? = Nothing

        If managed.Process IsNot Nothing AndAlso Not managed.Process.HasExited Then
            Try
                managed.Process.Refresh()
                memMb = managed.Process.WorkingSet64 \ (1024 * 1024)
                ' CPU % is expensive to calculate accurately - approximate from
                ' total processor time over a 1-second window.
                cpuPct = Nothing    ' Populated by a background sampler (future work)
            Catch
            End Try
        End If

        Return New InstanceMetricsResponse With {
            .InstanceId = instanceId,
            .SampledAt = DateTime.UtcNow,
            .State = managed.State,
            .RconState = managed.RconState,
            .PlayerCount = managed.PlayerCount,
            .Players = managed.Players.ToList(),
            .CustomMetrics = New Dictionary(Of String, String)(managed.CustomMetrics),
            .UptimeSeconds = If(managed.StartedAt.HasValue,
                                CLng((DateTime.UtcNow - managed.StartedAt.Value).TotalSeconds),
                                CType(Nothing, Long?)),
            .ProcessCpuPercent = cpuPct,
            .ProcessMemoryMb = memMb,
            .CrashCountInWindow = GetCrashCountInWindow(managed)
        }
    End Function

    ' Update player list and custom metrics - called by the log parser coordinator
    ' when the parser produces new data.
    Public Sub UpdateParserOutput(instanceId As String,
                                   players As IReadOnlyList(Of PlayerInfo),
                                   metrics As IReadOnlyDictionary(Of String, String))
        Dim managed As ManagedInstance = Nothing
        If Not _instances.TryGetValue(instanceId, managed) Then Return
        managed.Players = players
        managed.CustomMetrics = metrics
        managed.PlayerCount = players.Count
    End Sub

    Public Sub UpdateRconState(instanceId As String, state As RconState)
        Dim managed As ManagedInstance = Nothing
        If Not _instances.TryGetValue(instanceId, managed) Then Return
        managed.RconState = state
    End Sub


    ' ============================================================
    '  CRASH HANDLING
    '  Called by the monitor task when the process exits.
    ' ============================================================

    Private Async Function HandleExitAsync(managed As ManagedInstance,
                                            exitCode As Integer,
                                            cancellation As CancellationToken) As Task

        Await managed.Lock.WaitAsync(cancellation)
        Try
            Dim wasStopIntent = managed.StopIntentPending

            ' Clear the stop intent now that we've consumed it.
            managed.StopIntentPending = False
            _db.SetStopIntent(managed.InstanceId, False)

            If wasStopIntent Then
                ' This was a deliberate stop - transition cleanly to Stopped.
                _logger.LogInformation(
                    "[{Name}] Stopped cleanly (exit code {Code})",
                    managed.DisplayName, exitCode)
                managed.Process = Nothing
                managed.Pid = Nothing
                managed.StartedAt = Nothing
                Await TransitionStateAsync(managed, InstanceState.Stopped,
                                            $"Stopped cleanly (exit {exitCode})")
                Return
            End If

            ' The process died without us asking it to. This is a crash.
            ' Check if the exit code is in the "clean" list (e.g. in-game /quit).
            If managed.CleanExitCodes?.Contains(exitCode) = True Then
                _logger.LogInformation(
                    "[{Name}] Exited with clean exit code {Code} - not restarting",
                    managed.DisplayName, exitCode)
                managed.Process = Nothing
                managed.Pid = Nothing
                managed.StartedAt = Nothing
                Await TransitionStateAsync(managed, InstanceState.Stopped,
                                            $"Exited with clean code {exitCode}")
                Return
            End If

            ' Genuine crash.
            _logger.LogWarning(
                "[{Name}] Crashed (exit code {Code})", managed.DisplayName, exitCode)
            Await TransitionStateAsync(managed, InstanceState.Crashed,
                                        $"Unexpected exit (code {exitCode})")

            managed.Process = Nothing
            managed.Pid = Nothing
            managed.StartedAt = Nothing

            ' Consult the restart policy.
            Dim decision = EvaluateRestartPolicy(managed, exitCode)

            ' Record the crash event regardless of decision.
            managed.AttemptNumber += 1
            _db.InsertCrashEvent(New CrashEventRecord With {
                .CrashEventId = Guid.NewGuid().ToString(),
                .InstanceId = managed.InstanceId,
                .OccurredAt = DateTime.UtcNow,
                .ExitCode = exitCode,
                .StopIntentWasSet = False,
                .Decision = decision.Decision.ToString(),
                .DecisionReason = decision.Reason,
                .AttemptNumber = managed.AttemptNumber,
                .BackoffAppliedMs = decision.BackoffMs
            })

            Select Case decision.Decision
                Case RestartDecision.WillRestart
                    _logger.LogInformation(
                        "[{Name}] Restarting in {Ms}ms (attempt {N}): {Reason}",
                        managed.DisplayName, decision.BackoffMs,
                        managed.AttemptNumber, decision.Reason)

                    Await TransitionStateAsync(managed, InstanceState.Restarting,
                                                decision.Reason)
                    _db.UpsertInstanceState(ToPersistedState(managed))

                    ' Wait the backoff period then restart.
                    ' This runs on the current background thread.
                    If decision.BackoffMs > 0 Then
                        Await Task.Delay(decision.BackoffMs, cancellation)
                    End If

                    If managed.LastStartParams IsNot Nothing Then
                        Await LaunchProcessAsync(managed, managed.LastStartParams,
                                                  cancellation)
                    End If

                Case RestartDecision.HaltedCrashLoop
                    _logger.LogError(
                        "[{Name}] CrashLoopHalted: {Reason}", managed.DisplayName, decision.Reason)
                    Await TransitionStateAsync(managed, InstanceState.CrashLoopHalted,
                                                decision.Reason)

                Case Else
                    ' HaltedCleanExit, HaltedAutoRestartOff, HaltedInstallLocked
                    _logger.LogInformation(
                        "[{Name}] Not restarting ({Decision}): {Reason}",
                        managed.DisplayName, decision.Decision, decision.Reason)
                    Await TransitionStateAsync(managed, InstanceState.Stopped,
                                                decision.Reason)
            End Select

            _db.UpsertInstanceState(ToPersistedState(managed))
        Finally
            managed.Lock.Release()
        End Try
    End Function

    Private Function EvaluateRestartPolicy(managed As ManagedInstance,
                                            exitCode As Integer) As PolicyDecision

        Dim policy = managed.CrashRestartPolicy

        If policy Is Nothing OrElse Not policy.AutoRestart Then
            Return New PolicyDecision With {
                .Decision = RestartDecision.HaltedAutoRestartOff,
                .Reason = "AutoRestart is disabled for this instance.",
                .BackoffMs = 0
            }
        End If

        ' Count crashes in the sliding window.
        Dim windowStart = DateTime.UtcNow.AddMinutes(-policy.WindowMinutes)
        Dim recentCrashes = _db.GetRecentCrashes(managed.InstanceId, windowStart)
        Dim crashCount = recentCrashes.Count + 1   ' +1 for the current crash

        If policy.MaxRestartsInWindow > 0 AndAlso
           crashCount > policy.MaxRestartsInWindow Then
            Return New PolicyDecision With {
                .Decision = RestartDecision.HaltedCrashLoop,
                .Reason = $"Crash loop detected: {crashCount} crashes in " &
                          $"{policy.WindowMinutes} minutes " &
                          $"(limit: {policy.MaxRestartsInWindow}). " &
                          $"Last exit code: {exitCode}. " &
                          "Manual intervention required.",
                .BackoffMs = 0
            }
        End If

        ' Determine backoff delay from the schedule.
        ' The schedule is an array; the last value repeats for any
        ' attempt beyond the array length.
        Dim schedule = If(policy.BackoffScheduleSeconds,
                          {0, 10, 30, 60, 300})
        Dim attemptIndex = Math.Max(0, managed.AttemptNumber)   ' 0-based
        Dim backoffSec = If(attemptIndex < schedule.Length,
                            schedule(attemptIndex),
                            schedule(schedule.Length - 1))

        Return New PolicyDecision With {
            .Decision = RestartDecision.WillRestart,
            .Reason = $"Crash {crashCount}/{If(policy.MaxRestartsInWindow = 0, "∞", policy.MaxRestartsInWindow.ToString())} " &
                      $"in window. Restarting after {backoffSec}s backoff.",
            .BackoffMs = backoffSec * 1000
        }
    End Function


    ' ============================================================
    '  PROCESS MONITORING
    '  Started once per running instance. Two tasks per instance:
    '  one reads stdout, one waits for the process to exit.
    ' ============================================================

    Private Sub StartMonitoring(managed As ManagedInstance)
        ' Cancel token tied to this specific instance's process.
        ' Cancelled when the process exits or when we stop it.
        managed.MonitorCts = New CancellationTokenSource()
        Dim ct = managed.MonitorCts.Token

        ' Task 1: read stdout and feed lines into the ring buffer
        ' and crash signal detector.
        managed.StdoutTask = ReadStdoutAsync(managed, ct)

        ' Task 2: wait for the process to exit, then handle it.
        managed.MonitorTask = MonitorExitAsync(managed, ct)
    End Sub

    Private Async Function ReadStdoutAsync(managed As ManagedInstance,
                                            cancellation As CancellationToken) As Task
        If managed.Process?.StandardOutput Is Nothing Then Return

        Try
            Dim reader = managed.Process.StandardOutput
            Do While Not cancellation.IsCancellationRequested
                Dim line = Await reader.ReadLineAsync(cancellation)
                If line Is Nothing Then Exit Do     ' Stream closed = process exited

                ' Feed into ring buffer.
                _ringBuffer.Append(managed.InstanceId, "stdout",
                                   DateTime.UtcNow, line)

                ' Check for crash signal patterns BEFORE the process dies.
                ' This lets us pre-enter CrashDetected and capture context
                ' for Discord notifications.
                If managed.CrashDetectionState = CrashDetectionState.None AndAlso
                   managed.CrashSignalPatterns IsNot Nothing Then
                    For Each pattern In managed.CrashSignalPatterns
                        If line.IndexOf(pattern,
                                        StringComparison.OrdinalIgnoreCase) >= 0 Then
                            _logger.LogWarning(
                                "[{Name}] Crash signal detected in stdout: {Pattern}",
                                managed.DisplayName, pattern)
                            managed.CrashDetectionState = CrashDetectionState.CrashSignalDetected
                            ' TODO: fire CrashDetected notification event to manager
                            Exit For
                        End If
                    Next
                End If
            Loop
        Catch ex As OperationCanceledException
            ' Normal - monitoring was cancelled because we stopped the process.
        Catch ex As Exception
            _logger.LogError(ex, "[{Name}] stdout reader error", managed.DisplayName)
        End Try

        ' Also drain stderr if the process has it.
        Try
            If managed.Process?.StandardError IsNot Nothing Then
                Dim reader = managed.Process.StandardError
                Dim line = Await reader.ReadLineAsync()
                Do While line IsNot Nothing
                    _ringBuffer.Append(managed.InstanceId, "stderr", DateTime.UtcNow, line)
                    line = Await reader.ReadLineAsync()
                Loop
            End If
        Catch
        End Try
    End Function

    Private Async Function MonitorExitAsync(managed As ManagedInstance,
                                             cancellation As CancellationToken) As Task
        If managed.Process Is Nothing Then Return
        Try
            ' WaitForExitAsync is a .NET 5+ method that returns when
            ' the process exits, without blocking a thread.
            Await managed.Process.WaitForExitAsync(cancellation)

            Dim exitCode = 0
            Try
                exitCode = managed.Process.ExitCode
            Catch
            End Try

            _logger.LogDebug("[{Name}] Process exited with code {Code}",
                             managed.DisplayName, exitCode)

            ' Cancel the stdout reader task.
            If managed.MonitorCts IsNot Nothing Then managed.MonitorCts.Cancel()

            ' Give stdout a moment to flush remaining lines.
            Await Task.Delay(500, CancellationToken.None)

            ' Handle the exit (crash detection + restart policy).
            Await HandleExitAsync(managed, exitCode, CancellationToken.None)

        Catch ex As OperationCanceledException
            ' Monitoring was cancelled - we initiated the stop.
        Catch ex As Exception
            _logger.LogError(ex, "[{Name}] Monitor error", managed.DisplayName)
        End Try
    End Function


    ' ============================================================
    '  STATE TRANSITIONS
    '  Every state change goes through here so it's logged
    '  consistently and persisted atomically.
    ' ============================================================

    Private Async Function TransitionStateAsync(managed As ManagedInstance,
                                                 newState As InstanceState,
                                                 reason As String) As Task
        Dim oldState = managed.State
        managed.State = newState
        managed.LastStateChangeAt = DateTime.UtcNow

        _logger.LogInformation(
            "[{Name}] {OldState} → {NewState}: {Reason}",
            managed.DisplayName, oldState, newState, reason)

        _db.UpsertInstanceState(ToPersistedState(managed))

        ' TODO: push state change notification to manager via a callback/webhook
        ' This will be wired up when we implement the manager-side event receiver.
        Await Task.CompletedTask
    End Function


    ' ============================================================
    '  PRIVATE HELPERS
    ' ============================================================

    Private Async Function WaitForExitAsync(proc As Process,
                                             timeoutMs As Integer,
                                             cancellation As CancellationToken) As Task(Of Boolean)
        Try
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
                cts.CancelAfter(timeoutMs)
                Await proc.WaitForExitAsync(cts.Token)
                Return True
            End Using
        Catch ex As OperationCanceledException
            Return False
        End Try
    End Function

    Private Function GetCrashCountInWindow(managed As ManagedInstance) As Integer
        If managed.CrashRestartPolicy Is Nothing Then Return 0
        Dim windowStart = DateTime.UtcNow.AddMinutes(
            -managed.CrashRestartPolicy.WindowMinutes)
        Return _db.GetRecentCrashes(managed.InstanceId, windowStart).Count
    End Function

    Private Function ToPersistedState(managed As ManagedInstance) As PersistedInstanceState
        Return New PersistedInstanceState With {
            .InstanceId = managed.InstanceId,
            .DisplayName = managed.DisplayName,
            .GameId = managed.GameId,
            .InstallationId = managed.InstallationId,
            .State = managed.State,
            .Pid = managed.Pid,
            .StopIntentPending = managed.StopIntentPending,
            .StartedAt = managed.StartedAt,
            .LastStateChangeAt = managed.LastStateChangeAt,
            .CrashRestartJson = If(managed.CrashRestartPolicy IsNot Nothing,
                                    JsonSerializer.Serialize(managed.CrashRestartPolicy),
                                    "{}"),
            .LastStartParamsJson = If(managed.LastStartParams IsNot Nothing,
                                       JsonSerializer.Serialize(managed.LastStartParams),
                                       "{}")
        }
    End Function

    Private Function DeserializePolicy(json As String) As CrashRestartPolicy
        If String.IsNullOrWhiteSpace(json) OrElse json = "{}" Then
            Return New CrashRestartPolicy()
        End If
        Try
            Return JsonSerializer.Deserialize(Of CrashRestartPolicy)(json)
        Catch
            Return New CrashRestartPolicy()
        End Try
    End Function

    Private Function DeserializeStartParams(json As String) As StartInstanceRequest
        If String.IsNullOrWhiteSpace(json) OrElse json = "{}" Then Return Nothing
        Try
            Return JsonSerializer.Deserialize(Of StartInstanceRequest)(json)
        Catch
            Return Nothing
        End Try
    End Function

End Class


' ============================================================
'  MANAGED INSTANCE
'  In-memory representation of one tracked instance.
'  Contains everything the ProcessManager needs to manage it.
'  NOT shared outside the node - callers get DTOs instead.
' ============================================================

Public Class ManagedInstance

    ' Identity
    Public Property InstanceId As String
    Public Property DisplayName As String
    Public Property GameId As String
    Public Property InstallationId As String

    ' State
    Public Property State As InstanceState = InstanceState.Stopped
    Public Property RconState As RconState = RconState.NotAvailable
    Public Property CrashDetectionState As CrashDetectionState = CrashDetectionState.None
    Public Property LastStateChangeAt As DateTime = DateTime.UtcNow
    Public Property StopIntentPending As Boolean = False

    ' Process
    Public Property Process As Process
    Public Property Pid As Integer?
    Public Property StartedAt As DateTime?

    ' Restart tracking
    Public Property AttemptNumber As Integer = 0
    Public Property CrashRestartPolicy As CrashRestartPolicy
    Public Property CleanExitCodes As List(Of Integer)
    Public Property CrashSignalPatterns As List(Of String)

    ' Last start parameters - used to restart autonomously
    Public Property LastStartParams As StartInstanceRequest

    ' Parser output (updated by log parser coordinator)
    Public Property Players As IReadOnlyList(Of PlayerInfo) = New List(Of PlayerInfo)()
    Public Property CustomMetrics As IReadOnlyDictionary(Of String, String) =
        New Dictionary(Of String, String)()
    Public Property PlayerCount As Integer = 0

    ' Background monitoring tasks
    Public Property StdoutTask As Task
    Public Property MonitorTask As Task
    Public Property MonitorCts As CancellationTokenSource

    ' Per-instance lock. One at a time for state transitions.
    ' SemaphoreSlim(1,1) = a mutex that works with Await.
    Public ReadOnly Lock As New SemaphoreSlim(1, 1)

End Class


' ============================================================
'  POLICY DECISION
'  Result of evaluating a crash restart policy.
' ============================================================

Friend Class PolicyDecision
    Public Property Decision As RestartDecision
    Public Property Reason As String
    Public Property BackoffMs As Integer
End Class
