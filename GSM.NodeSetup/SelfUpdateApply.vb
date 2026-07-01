Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Net.Http
Imports System.Threading

' ============================================================
'  SelfUpdateApply — the external survivor that swaps + relaunches
'  the node binary while the node is down (Phase 8-2, slice 6).
'
'  Invoked as: GSM.NodeSetup --apply-update --wait-pid <nodePid>
'
'  The node cannot replace the binary it is currently executing, so on a
'  self-update it stages GSM.Node.new beside itself, spawns this survivor
'  detached, and exits. We outlive the node, wait for its PID to die (which
'  unlocks the binary on Windows), swap the staged .new over the live binary
'  (keeping the previous one as .old for slice-8 rollback), and relaunch:
'    - Windows with the GSMNode service installed -> sc start GSMNode
'    - Windows bare / Linux bare                  -> launch GSM.Node directly
'
'  Linux-under-systemd never reaches here: there the node defers the swap to
'  systemd's ExecStartPre and the relaunch to Restart=on-failure. This path
'  is the universal *fallback* survivor for every non-systemd case.
'
'  Naming rule mirrors the node staging side: the live binary filename with
'  ".new" / ".old" appended (GSM.Node -> GSM.Node.new on Linux,
'  GSM.Node.exe -> GSM.Node.exe.new on Windows).
'
'  Progress is written to stderr and appended to nodesetup-apply.log beside
'  the binary, so there's a trail even when this runs fully detached.
' ============================================================

Public Module SelfUpdateApply

    Private Const NewSuffix As String = ".new"
    Private Const OldSuffix As String = ".old"
    Private Const FailedSuffix As String = ".failed"
    Private Const ApplyLogFile As String = "nodesetup-apply.log"

    ' Phase 8-2 slice 8b-1: after relaunch, confirm the node answers
    ' /api/version before declaring success; if it never does, roll back to
    ' .old. Grace lets the host bind before the first poll.
    Private Const HealthGraceMs As Integer = 2000
    Private Const HealthPollIntervalMs As Integer = 2000
    Private Const HealthTimeoutMs As Integer = 60000

    ' The node's graceful shutdown is quick; 60s is a generous ceiling that
    ' still bounds a stuck shutdown so we don't wait forever.
    Private Const PidWaitTimeoutMs As Integer = 60000

    ' Cover handle-linger / AV real-time scan briefly holding the just-exited
    ' binary on Windows. ~6s total.
    Private Const SwapRetries As Integer = 12
    Private Const SwapRetryDelayMs As Integer = 500

    ''' <summary>
    ''' Entry point for --apply-update. Returns a process exit code:
    ''' 0 success, 2 node didn't exit in time, 3 swap failed, 4 relaunch failed,
    ''' 5 update unhealthy and rolled back to the previous binary, 6 rollback
    ''' itself failed (node may be down — manual intervention).
    ''' </summary>
    Public Function Run(waitPid As Integer) As Integer
        Log($"apply-update starting (wait-pid={waitPid})")

        Dim live = ServiceManager.GetNodeExecutablePath()
        Dim newPath = live & NewSuffix
        Dim oldPath = live & OldSuffix

        ' 1. Wait for the node to exit so the binary is unlocked (Windows) and
        '    we don't double-launch it.
        If waitPid > 0 Then
            If Not WaitForExit(waitPid, PidWaitTimeoutMs) Then
                Log($"ERROR: node pid {waitPid} did not exit within {PidWaitTimeoutMs}ms; aborting without swap or relaunch.")
                Return 2
            End If
        End If

        ' 2. Swap the staged .new over the live binary, keeping .old. If nothing
        '    is staged we still relaunch — the node is down and expects us to.
        Dim swapped = False
        If File.Exists(newPath) Then
            If Not SwapNewOverLive(live, newPath, oldPath) Then
                Log("ERROR: failed to swap staged binary into place after retries; aborting.")
                Return 3
            End If
            swapped = True
            Log("swap complete: .new -> live (previous kept as .old)")
        Else
            Log($"no staged update found at {newPath}; relaunching existing binary.")
        End If

        ' 3. Relaunch.
        Dim launch = Relaunch(live)
        If Not launch.Success Then
            Log("ERROR: relaunch failed.")
            Return 4
        End If
        Log("relaunch requested successfully.")

        ' 4. Health-gate (only when we actually applied an update — a plain
        '    relaunch of the existing binary has nothing to verify or revert).
        If Not swapped Then Return 0
        If WaitForHealthy() Then
            Log("health check passed; update confirmed healthy.")
            Return 0
        End If
        Log($"new binary did not answer /api/version within {HealthTimeoutMs}ms; rolling back to .old.")
        Return Rollback(live, oldPath, launch)
    End Function

    Private Function WaitForExit(pid As Integer, timeoutMs As Integer) As Boolean
        Try
            Dim p = Process.GetProcessById(pid)
            Log($"waiting for node pid {pid} to exit...")
            If p.WaitForExit(timeoutMs) Then
                Log($"node pid {pid} exited.")
                Return True
            End If
            Return False
        Catch ex As ArgumentException
            ' No process with that id — already gone.
            Log($"node pid {pid} already gone.")
            Return True
        Catch ex As Exception
            Log($"could not open node pid {pid} ({ex.Message}); assuming gone.")
            Return True
        End Try
    End Function

    ''' <summary>
    ''' Moves live -> .old (replacing any prior .old) then .new -> live, both as
    ''' atomic same-filesystem renames. Retries on a transient IO/lock error.
    ''' Across retries it self-heals: once live has been moved to .old, the
    ''' first move is skipped and only the .new -> live rename is retried, so a
    ''' mid-swap lock doesn't lose the previous binary.
    ''' </summary>
    Private Function SwapNewOverLive(live As String, newPath As String, oldPath As String) As Boolean
        For attempt = 1 To SwapRetries
            Try
                If File.Exists(live) Then
                    If File.Exists(oldPath) Then File.Delete(oldPath)
                    File.Move(live, oldPath)
                End If
                File.Move(newPath, live)
                EnsureExecutable(live)
                Return True
            Catch ex As Exception When (TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException)
                Log($"swap attempt {attempt}/{SwapRetries} blocked ({ex.Message}); retrying in {SwapRetryDelayMs}ms.")
                Thread.Sleep(SwapRetryDelayMs)
            Catch ex As Exception
                Log($"swap failed unexpectedly: {ex.Message}")
                Return False
            End Try
        Next
        Return False
    End Function

    Private Function Relaunch(live As String) As LaunchOutcome
        Try
            If ConfigHelpers.RunningOnWindows() AndAlso WindowsServiceInstalled() Then
                Log("starting Windows service '" & ServiceManager.DefaultServiceName & "'.")
                Dim r = ServiceManager.StartWindowsService(ServiceManager.DefaultServiceName)
                Log(r.Message)
                If r.Success Then Return New LaunchOutcome With {.Success = True, .ViaService = True}
                Log("service start failed; falling back to direct launch.")
            End If
            ' Linux-bare, or Windows without the service installed (or a failed
            ' service start): launch the node binary directly. It reparents to
            ' init when we exit, so it outlives this survivor.
            Dim proc = LaunchDirect(live)
            Return New LaunchOutcome With {.Success = proc IsNot Nothing, .ViaService = False, .Process = proc}
        Catch ex As Exception
            Log($"relaunch error: {ex.Message}")
            Return New LaunchOutcome With {.Success = False}
        End Try
    End Function

    Private Function WindowsServiceInstalled() As Boolean
        Dim status = ServiceManager.GetWindowsServiceStatus(ServiceManager.DefaultServiceName)
        ' Treat only a concrete service state as "installed"; an Unknown/error
        ' result falls through to a direct launch (the safe default).
        Return status = "Running" OrElse status = "Stopped" OrElse
               status = "Starting" OrElse status = "Stopping"
    End Function

    Private Function LaunchDirect(live As String) As Process
        Try
            Dim psi As New ProcessStartInfo(live) With {
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .WorkingDirectory = Path.GetDirectoryName(live)
            }
            Return Process.Start(psi)
        Catch ex As Exception
            Log($"direct launch failed: {ex.Message}")
            Return Nothing
        End Try
    End Function

    ' --------------------------------------------------------
    ' Phase 8-2 slice 8b-1: health-gate + auto-revert
    ' --------------------------------------------------------

    ''' <summary>How the node was (re)launched, so a rollback can stop it.</summary>
    Private Class LaunchOutcome
        Public Property Success As Boolean
        Public Property ViaService As Boolean
        Public Property Process As Process
    End Class

    ''' <summary>
    ''' Polls http://127.0.0.1:&lt;port&gt;/api/version (unauthenticated) until it
    ''' answers 2xx or the timeout elapses. The node binds ListenAnyIP, so
    ''' loopback reaches it. If the port can't be read we can't verify, so we
    ''' return True — don't roll a working node back over a config-read blip.
    ''' </summary>
    Private Function WaitForHealthy() As Boolean
        Dim port = ReadListenPort()
        If port <= 0 Then
            Log("could not determine node port; skipping health check (treating as healthy).")
            Return True
        End If
        Dim url = $"http://127.0.0.1:{port}/api/version"
        Log($"health check: polling {url} for up to {HealthTimeoutMs}ms...")
        Thread.Sleep(HealthGraceMs)
        Dim deadline = Environment.TickCount64 + HealthTimeoutMs
        Using client As New HttpClient()
            client.Timeout = TimeSpan.FromSeconds(4)
            Do
                Try
                    Using resp = client.GetAsync(url).GetAwaiter().GetResult()
                        If resp.IsSuccessStatusCode Then Return True
                    End Using
                Catch
                    ' Node still starting / down — keep polling.
                End Try
                If Environment.TickCount64 >= deadline Then Exit Do
                Thread.Sleep(HealthPollIntervalMs)
            Loop
        End Using
        Return False
    End Function

    Private Function ReadListenPort() As Integer
        Try
            Dim cfgPath = Path.Combine(AppContext.BaseDirectory, "nodesettings.json")
            Dim cfg = NodeSetupConfig.LoadOrCreate(cfgPath)
            Return cfg.Node.ListenPort
        Catch ex As Exception
            Log($"could not read nodesettings.json ({ex.Message}).")
            Return 0
        End Try
    End Function

    ''' <summary>
    ''' The new binary didn't come up. Stop it, quarantine it as .failed,
    ''' restore .old over live, and relaunch the previous (known-good) binary.
    ''' Returns 5 on a successful rollback, 6 if the rollback itself failed.
    ''' </summary>
    Private Function Rollback(live As String, oldPath As String, badLaunch As LaunchOutcome) As Integer
        If Not File.Exists(oldPath) Then
            Log("ERROR: no .old to roll back to; leaving the new binary in place. Manual intervention needed.")
            Return 6
        End If

        ' Stop the just-launched (bad) node so the binary unlocks (Windows) and
        ' we don't leave a broken node running.
        StopRelaunchedNode(badLaunch)

        Dim failedPath = live & FailedSuffix
        If Not RollbackSwap(live, failedPath, oldPath) Then
            Log("ERROR: rollback swap failed after retries; node may be down. Manual intervention needed.")
            Return 6
        End If
        Log("rolled back: bad binary -> .failed, previous .old -> live.")

        Dim relaunchResult = Relaunch(live)
        If Not relaunchResult.Success Then
            Log("ERROR: relaunch after rollback failed; node is down. Manual intervention needed.")
            Return 6
        End If
        Log("relaunched the previous binary after rollback.")

        ' Best-effort: confirm the reverted binary actually answers. It was
        ' healthy before the update, so this is a sanity check, not a gate.
        If WaitForHealthy() Then
            Log("rollback confirmed healthy.")
        Else
            Log("WARNING: reverted binary did not answer /api/version in time; check the node.")
        End If
        Return 5
    End Function

    ''' <summary>
    ''' Moves live -> .failed (replacing any prior .failed) then .old -> live,
    ''' both atomic same-filesystem renames, with the same retry-on-lock loop as
    ''' the forward swap so a lingering handle on Windows doesn't lose the swap.
    ''' </summary>
    Private Function RollbackSwap(live As String, failedPath As String, oldPath As String) As Boolean
        For attempt = 1 To SwapRetries
            Try
                If File.Exists(live) Then
                    If File.Exists(failedPath) Then File.Delete(failedPath)
                    File.Move(live, failedPath)
                End If
                File.Move(oldPath, live)
                EnsureExecutable(live)
                Return True
            Catch ex As Exception When (TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException)
                Log($"rollback swap attempt {attempt}/{SwapRetries} blocked ({ex.Message}); retrying in {SwapRetryDelayMs}ms.")
                Thread.Sleep(SwapRetryDelayMs)
            Catch ex As Exception
                Log($"rollback swap failed unexpectedly: {ex.Message}")
                Return False
            End Try
        Next
        Return False
    End Function

    ''' <summary>
    ''' Stops the node we just relaunched so its binary unlocks before we swap.
    ''' Service path: sc stop. Direct path: kill just the node process (NOT the
    ''' tree — the per-instance shims + games must survive and be re-adopted).
    ''' </summary>
    Private Sub StopRelaunchedNode(launch As LaunchOutcome)
        Try
            If launch Is Nothing Then Return
            If launch.ViaService Then
                Dim r = ServiceManager.StopWindowsService(ServiceManager.DefaultServiceName)
                Log(r.Message)
                ' Give SCM a moment to transition to Stopped; the RollbackSwap
                ' retry loop covers any remaining unlock lag.
                Thread.Sleep(1000)
            ElseIf launch.Process IsNot Nothing Then
                If Not launch.Process.HasExited Then
                    launch.Process.Kill() ' just this process; children reparent + survive
                    launch.Process.WaitForExit(10000)
                End If
            End If
        Catch ex As Exception
            Log($"could not stop the relaunched node ({ex.Message}); proceeding to swap with retries.")
        End Try
    End Sub

    ''' <summary>Adds +x to the swapped-in binary on Linux. No-op on Windows.</summary>
    Private Sub EnsureExecutable(path As String)
        If ConfigHelpers.RunningOnWindows() Then Return
        Try
            Dim mode = File.GetUnixFileMode(path)
            Dim newMode = mode Or UnixFileMode.UserExecute Or
                          UnixFileMode.GroupExecute Or UnixFileMode.OtherExecute
            If mode <> newMode Then
                File.SetUnixFileMode(path, newMode)
            End If
        Catch
            ' Best effort; the operator can chmod manually if needed.
        End Try
    End Sub

    Private Sub Log(message As String)
        Dim line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] apply-update: {message}"
        Try
            Console.Error.WriteLine(line)
        Catch
        End Try
        Try
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, ApplyLogFile),
                               line & Environment.NewLine)
        Catch
            ' Best effort; never let logging abort the apply.
        End Try
    End Sub

End Module
