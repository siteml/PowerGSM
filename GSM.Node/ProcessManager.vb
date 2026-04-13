Imports System
Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin
Imports GSM.Node.Api
Imports Microsoft.Extensions.Logging

' ============================================================
'  ProcessManager — owns all game server processes on this node.
'
'  Responsibilities:
'    - Start/stop/restart game server processes
'    - Capture stdout/stderr and feed to RingBufferStore
'    - Detect crashes (exit code + log pattern)
'    - Apply CrashRestartPolicy (backoff, crash loop detection)
'    - Persist state to NodeDatabase for node restart recovery
'    - Report per-process metrics (PID, CPU, memory)
'
'  The ProcessManager never interprets game-specific logic.
'  It receives plain data (exe path, args, env vars) from the
'  manager via REST and executes it.
' ============================================================

Namespace GSM.Node

    Public Class ProcessManager

        Private ReadOnly _instances As New ConcurrentDictionary(Of String, ManagedInstance)
        Private ReadOnly _logStore As RingBufferStore
        Private ReadOnly _database As NodeDatabase
        Private ReadOnly _logger As Microsoft.Extensions.Logging.ILogger(Of ProcessManager)

        Public Sub New(logStore As RingBufferStore,
                       database As NodeDatabase,
                       logger As Microsoft.Extensions.Logging.ILogger(Of ProcessManager))
            _logStore = logStore
            _database = database
            _logger = logger
        End Sub

        ' ============================================================
        '  Node status
        ' ============================================================

        Public Function GetNodeStatus(config As NodeConfiguration) As NodeStatusResponse
            Dim resp As New NodeStatusResponse()
            resp.NodeId = config.NodeId
            resp.MachineName = Environment.MachineName
            resp.OsDescription = RuntimeInformation.OSDescription
            resp.RunningInstanceCount = _instances.Values.
                Where(Function(m) m.State = InstanceState.Running).Count()
            resp.NodeVersion = GetType(ProcessManager).Assembly.
                GetName().Version?.ToString()

            ' Metrics — best-effort, platform-dependent
            Try
                Dim proc = Process.GetCurrentProcess()
                resp.UptimeSeconds = CLng((DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds)
                resp.MemoryUsedMb = proc.WorkingSet64 \ (1024L * 1024L)
            Catch ex As Exception
                ' Ignore metric collection failures on some platforms
            End Try

            Return resp
        End Function

        ' ============================================================
        '  Instance lifecycle
        ' ============================================================

        Public Async Function StartInstanceAsync(request As StartInstanceRequest) As Task(Of InstanceStatusResponse)

            ' Check if already tracked
            Dim existing As ManagedInstance = Nothing
            If _instances.TryGetValue(request.InstanceId, existing) Then
                If existing.State = InstanceState.Running OrElse
                   existing.State = InstanceState.Starting Then
                    Return BuildStatusResponse(existing, NodeErrorCodes.InstanceAlreadyRunning,
                                               "Instance is already running")
                End If
            End If

            Dim managed As New ManagedInstance()
            managed.InstanceId = request.InstanceId
            managed.State = InstanceState.Starting
            managed.StateChangedAt = DateTime.UtcNow
            managed.CrashPolicy = request.CrashPolicy
            managed.MaxCrashCount = request.MaxCrashCount
            managed.CrashWindowMinutes = request.CrashWindowMinutes
            managed.StopIntentPending = False
            managed.CrashCount = 0

            ' Build process start info
            Dim psi As New ProcessStartInfo()
            psi.FileName = request.ExePath
            psi.Arguments = If(request.Arguments, "")
            psi.WorkingDirectory = If(request.WorkingDirectory,
                                      Path.GetDirectoryName(request.ExePath))
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.CreateNoWindow = True

            ' Apply environment variables
            If request.EnvironmentVars IsNot Nothing Then
                For Each kvp In request.EnvironmentVars
                    psi.EnvironmentVariables(kvp.Key) = kvp.Value
                Next
            End If

            Try
                Dim proc As New Process()
                proc.StartInfo = psi
                proc.EnableRaisingEvents = True

                ' Wire stdout/stderr capture
                AddHandler proc.OutputDataReceived, Sub(sender, e)
                                                        If e.Data IsNot Nothing Then
                                                            _logStore.Append(request.InstanceId,
                                                                New BufferedLogLine With {
                                                                    .Timestamp = DateTime.UtcNow,
                                                                    .Text = e.Data,
                                                                    .IsError = False
                                                                })
                                                        End If
                                                    End Sub

                AddHandler proc.ErrorDataReceived, Sub(sender, e)
                                                       If e.Data IsNot Nothing Then
                                                           _logStore.Append(request.InstanceId,
                                                               New BufferedLogLine With {
                                                                   .Timestamp = DateTime.UtcNow,
                                                                   .Text = e.Data,
                                                                   .IsError = True
                                                               })
                                                       End If
                                                   End Sub

                ' Wire exit handler
                AddHandler proc.Exited, Sub(sender, e)
                                            HandleProcessExited(managed)
                                        End Sub

                If Not proc.Start() Then
                    Return BuildErrorResponse(request.InstanceId,
                                              NodeErrorCodes.ProcessStartFailed,
                                              "Process.Start() returned false")
                End If

                proc.BeginOutputReadLine()
                proc.BeginErrorReadLine()

                managed.Process = proc
                managed.Pid = proc.Id
                managed.StartedAt = DateTime.UtcNow
                managed.State = InstanceState.Running
                managed.StateChangedAt = DateTime.UtcNow

                _instances(request.InstanceId) = managed

                ' Persist snapshot
                _database.SaveInstanceSnapshot(
                    managed.InstanceId, managed.State.ToString(),
                    managed.Pid, managed.StartedAt,
                    JsonSerializer.Serialize(New With {
                        .Policy = managed.CrashPolicy.ToString(),
                        .MaxCrash = managed.MaxCrashCount,
                        .WindowMin = managed.CrashWindowMinutes
                    }),
                    managed.StopIntentPending)

                _logger.LogInformation("Started instance {InstanceId} (PID {Pid})",
                                       request.InstanceId, managed.Pid)

                Return BuildStatusResponse(managed)

            Catch ex As Exception
                _logger.LogError(ex, "Failed to start instance {InstanceId}", request.InstanceId)
                Return BuildErrorResponse(request.InstanceId,
                                          NodeErrorCodes.ProcessStartFailed,
                                          ex.Message)
            End Try
        End Function

        Public Async Function StopInstanceAsync(request As StopInstanceRequest) As Task(Of InstanceStatusResponse)

            Dim managed As ManagedInstance = Nothing
            If Not _instances.TryGetValue(request.InstanceId, managed) Then
                Return BuildErrorResponse(request.InstanceId,
                                          NodeErrorCodes.InstanceNotFound,
                                          "Instance not found")
            End If

            If managed.State = InstanceState.Stopped OrElse
               managed.State = InstanceState.CrashLoopHalted Then
                Return BuildStatusResponse(managed, NodeErrorCodes.InstanceAlreadyStopped,
                                           "Instance is already stopped")
            End If

            ' Set stop intent so crash handler knows not to restart
            managed.StopIntentPending = True
            managed.State = InstanceState.Stopping
            managed.StateChangedAt = DateTime.UtcNow

            If managed.Process IsNot Nothing AndAlso Not managed.Process.HasExited Then
                Try
                    ' Attempt graceful shutdown
                    Dim timeout = If(request.GracefulTimeoutMs > 0,
                                     request.GracefulTimeoutMs, 10000)

                    ' Try closing stdin first (some servers treat this as shutdown)
                    Try
                        managed.Process.StandardInput.Close()
                    Catch
                        ' stdin may not be redirected
                    End Try

                    ' Wait for graceful exit
                    Dim exited = Await WaitForExitAsync(managed.Process, timeout)

                    If Not exited Then
                        ' Force kill
                        _logger.LogWarning("Instance {InstanceId} did not exit gracefully, killing",
                                           request.InstanceId)
                        Try
                            managed.Process.Kill(entireProcessTree:=True)
                        Catch
                            managed.Process.Kill()
                        End Try
                        Await WaitForExitAsync(managed.Process, 5000)
                    End If
                Catch ex As Exception
                    _logger.LogError(ex, "Error stopping instance {InstanceId}",
                                     request.InstanceId)
                End Try
            End If

            managed.State = InstanceState.Stopped
            managed.StateChangedAt = DateTime.UtcNow
            _database.RemoveInstanceSnapshot(request.InstanceId)

            _logger.LogInformation("Stopped instance {InstanceId}", request.InstanceId)
            Return BuildStatusResponse(managed)
        End Function

        Public Function GetInstanceStatus(instanceId As String) As InstanceStatusResponse
            Dim managed As ManagedInstance = Nothing
            If Not _instances.TryGetValue(instanceId, managed) Then
                Return BuildErrorResponse(instanceId, NodeErrorCodes.InstanceNotFound,
                                          "Instance not found")
            End If
            Return BuildStatusResponse(managed)
        End Function

        Public Function GetAllInstanceStatuses() As IReadOnlyList(Of InstanceStatusResponse)
            Return _instances.Values.
                Select(Function(m) BuildStatusResponse(m)).
                ToList()
        End Function

        ' ============================================================
        '  Crash handling
        ' ============================================================

        Private Sub HandleProcessExited(managed As ManagedInstance)
            If managed.StopIntentPending Then
                ' Intentional stop — don't treat as crash
                managed.State = InstanceState.Stopped
                managed.StateChangedAt = DateTime.UtcNow
                _database.RemoveInstanceSnapshot(managed.InstanceId)
                Return
            End If

            Dim exitCode = 0
            Try
                exitCode = managed.Process.ExitCode
            Catch
            End Try

            managed.LastExitCode = exitCode
            _logger.LogWarning("Instance {InstanceId} exited unexpectedly (code {ExitCode})",
                               managed.InstanceId, exitCode)

            ' Evaluate restart policy
            Dim decision = EvaluateRestartPolicy(managed, exitCode)

            _database.RecordCrashEvent(managed.InstanceId, exitCode,
                                       "ProcessExit", decision.Action.ToString(),
                                       decision.Reason)

            Select Case decision.Action
                Case PolicyAction.Restart
                    managed.State = InstanceState.Crashed
                    managed.StateChangedAt = DateTime.UtcNow
                    managed.CrashCount += 1

                    ' Schedule delayed restart
                    Task.Run(Async Function()
                                 If decision.DelayMs > 0 Then
                                     Await Task.Delay(decision.DelayMs)
                                 End If
                                 Await RestartInstanceAsync(managed)
                             End Function)

                Case PolicyAction.Halt
                    managed.State = InstanceState.CrashLoopHalted
                    managed.StateChangedAt = DateTime.UtcNow
                    _logger.LogError("Instance {InstanceId} entered CrashLoopHalted: {Reason}",
                                     managed.InstanceId, decision.Reason)

                Case Else
                    managed.State = InstanceState.Stopped
                    managed.StateChangedAt = DateTime.UtcNow
            End Select
        End Sub

        Private Function EvaluateRestartPolicy(managed As ManagedInstance,
                                               exitCode As Integer) As PolicyDecision

            If managed.CrashPolicy = CrashRestartPolicy.NeverRestart Then
                Return New PolicyDecision With {
                    .Action = PolicyAction.NoRestart,
                    .Reason = "Policy is NeverRestart"
                }
            End If

            ' Check crash loop window
            Dim windowCrashes = _database.GetCrashCountInWindow(
                managed.InstanceId, managed.CrashWindowMinutes)

            If windowCrashes >= managed.MaxCrashCount Then
                Return New PolicyDecision With {
                    .Action = PolicyAction.Halt,
                    .Reason = $"Crash loop: {windowCrashes} crashes in {managed.CrashWindowMinutes} minutes (max {managed.MaxCrashCount})"
                }
            End If

            ' Calculate backoff delay
            Dim delayMs = 0
            If managed.CrashPolicy = CrashRestartPolicy.RestartWithBackoff Then
                ' Simple exponential backoff: 2^crashCount seconds, capped at 5 min
                Dim delaySec = Math.Min(CInt(Math.Pow(2, managed.CrashCount)), 300)
                delayMs = delaySec * 1000
            End If

            Return New PolicyDecision With {
                .Action = PolicyAction.Restart,
                .DelayMs = delayMs,
                .Reason = $"Restarting (attempt {managed.CrashCount + 1}, delay {delayMs}ms)"
            }
        End Function

        Private Async Function RestartInstanceAsync(managed As ManagedInstance) As Task
            _logger.LogInformation("Restarting instance {InstanceId}", managed.InstanceId)

            Try
                ' Re-use the original start info
                Dim proc As New Process()
                proc.StartInfo = managed.OriginalStartInfo
                proc.EnableRaisingEvents = True

                AddHandler proc.OutputDataReceived, Sub(sender, e)
                                                        If e.Data IsNot Nothing Then
                                                            _logStore.Append(managed.InstanceId,
                                                                New BufferedLogLine With {
                                                                    .Timestamp = DateTime.UtcNow,
                                                                    .Text = e.Data,
                                                                    .IsError = False
                                                                })
                                                        End If
                                                    End Sub

                AddHandler proc.ErrorDataReceived, Sub(sender, e)
                                                       If e.Data IsNot Nothing Then
                                                           _logStore.Append(managed.InstanceId,
                                                               New BufferedLogLine With {
                                                                   .Timestamp = DateTime.UtcNow,
                                                                   .Text = e.Data,
                                                                   .IsError = True
                                                               })
                                                       End If
                                                   End Sub

                AddHandler proc.Exited, Sub(sender, e)
                                            HandleProcessExited(managed)
                                        End Sub

                If proc.Start() Then
                    proc.BeginOutputReadLine()
                    proc.BeginErrorReadLine()
                    managed.Process = proc
                    managed.Pid = proc.Id
                    managed.StartedAt = DateTime.UtcNow
                    managed.State = InstanceState.Running
                    managed.StateChangedAt = DateTime.UtcNow
                    managed.StopIntentPending = False
                Else
                    managed.State = InstanceState.CrashLoopHalted
                    managed.StateChangedAt = DateTime.UtcNow
                    _logger.LogError("Failed to restart instance {InstanceId}", managed.InstanceId)
                End If
            Catch ex As Exception
                managed.State = InstanceState.CrashLoopHalted
                managed.StateChangedAt = DateTime.UtcNow
                _logger.LogError(ex, "Exception restarting instance {InstanceId}", managed.InstanceId)
            End Try
        End Function

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Shared Async Function WaitForExitAsync(proc As Process,
                                                       timeoutMs As Integer) As Task(Of Boolean)
            Try
                Using cts As New CancellationTokenSource(timeoutMs)
                    Await proc.WaitForExitAsync(cts.Token)
                    Return True
                End Using
            Catch ex As OperationCanceledException
                Return False
            End Try
        End Function

        Private Shared Function BuildStatusResponse(managed As ManagedInstance,
                                                    Optional errCode As NodeErrorCodes = NodeErrorCodes.None,
                                                    Optional errMsg As String = Nothing) As InstanceStatusResponse
            Dim resp As New InstanceStatusResponse()
            resp.InstanceId = managed.InstanceId
            resp.CurrentState = managed.State
            resp.Pid = managed.Pid
            resp.CrashCount = managed.CrashCount
            resp.LastExitCode = managed.LastExitCode
            resp.StateChangedAt = managed.StateChangedAt
            resp.ErrorMessage = errMsg

            If managed.State = InstanceState.Running AndAlso managed.StartedAt <> DateTime.MinValue Then
                resp.UptimeSeconds = CLng((DateTime.UtcNow - managed.StartedAt).TotalSeconds)
            End If

            ' Best-effort process metrics
            If managed.Process IsNot Nothing AndAlso Not managed.Process.HasExited Then
                Try
                    managed.Process.Refresh()
                    resp.MemoryMb = managed.Process.WorkingSet64 \ (1024L * 1024L)
                Catch
                End Try
            End If

            Return resp
        End Function

        Private Shared Function BuildErrorResponse(instanceId As String,
                                                   errCode As NodeErrorCodes,
                                                   errMsg As String) As InstanceStatusResponse
            Return New InstanceStatusResponse With {
                .InstanceId = instanceId,
                .CurrentState = InstanceState.Stopped,
                .ErrorMessage = errMsg
            }
        End Function

    End Class

    ' ============================================================
    '  ManagedInstance — internal state per tracked process
    ' ============================================================

    Friend Class ManagedInstance
        Public Property InstanceId As String
        Public Property State As InstanceState
        Public Property Process As Process
        Public Property Pid As Integer
        Public Property StartedAt As DateTime
        Public Property StateChangedAt As DateTime
        Public Property CrashPolicy As CrashRestartPolicy
        Public Property MaxCrashCount As Integer = 5
        Public Property CrashWindowMinutes As Integer = 60
        Public Property StopIntentPending As Boolean
        Public Property CrashCount As Integer
        Public Property LastExitCode As Integer?

        ''' <summary>
        ''' Preserved from the first start so restarts use the
        ''' same configuration.
        ''' </summary>
        Public ReadOnly Property OriginalStartInfo As ProcessStartInfo
            Get
                If Process IsNot Nothing Then
                    Return Process.StartInfo
                End If
                Return Nothing
            End Get
        End Property
    End Class

    ' ============================================================
    '  PolicyDecision — result of crash policy evaluation
    ' ============================================================

    Public Class PolicyDecision
        Public Property Action As PolicyAction
        Public Property DelayMs As Integer
        Public Property Reason As String
    End Class

    Public Enum PolicyAction
        Restart
        Halt
        NoRestart
    End Enum

End Namespace
