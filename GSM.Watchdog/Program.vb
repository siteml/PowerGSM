Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Threading

' ============================================================
'  GSM.Watchdog  (Phase 5m-3)
'
'  A tiny supervisor whose only job is "keep GSM.Manager.exe
'  running": launch it, restart it if it crashes, escalate to
'  safe mode after repeated fast crashes, and give up after too
'  many restarts in a window (so a hard-broken Manager doesn't
'  spin forever). Installed as a per-user Task Scheduler logon
'  task by the Manager itself (Settings toggle); can also be run
'  directly for dev / debug.
'
'  Single-instance + duplicate-Manager handling
'  --------------------------------------------
'  - The watchdog holds its own named mutex; a second watchdog
'    exits immediately.
'  - The MANAGER is single-instance too (its own named mutex,
'    same name hardcoded in both projects — the watchdog does
'    NOT reference the Manager assembly). The watchdog detects a
'    running Manager by probing that mutex, and MONITORS it
'    rather than launching a duplicate.
'
'  Manager exit-code contract (must match GSM.Manager's
'  ManagerProgram). When the Manager is launched by the watchdog
'  it gets POWERGSM_WATCHDOG=1 in its environment, which tells it
'  to defer relaunch decisions to the watchdog via these codes
'  instead of self-spawning:
'
'    0   clean quit  ........ user closed the app; watchdog stands down
'    10  deferred  .......... a Manager was already running; this
'                            instance bowed out — watchdog monitors
'                            the existing one (NOT a crash)
'    20  relaunch normal  ... user picked Restart Normally
'    21  relaunch safe  ..... user picked Restart in Safe Mode
'    other non-zero  ....... crash; apply restart / safe-mode / give-up
'
'  Until the Manager-side changes land it simply won't emit 10 /
'  20 / 21, and the watchdog's clean-exit (0) and crash (non-zero)
'  paths still work.
'
'  Watchdog exit codes:
'    0  stood down cleanly — Manager quit, another watchdog/Manager
'       was already running, OR we gave up after the rapid-restart
'       limit. Give-up is deliberately 0 (not a failure) so the Task
'       Scheduler RestartOnFailure backstop does NOT relaunch the
'       watchdog straight back into the same crash loop. A genuine
'       watchdog crash (unhandled exception → non-zero) still trips
'       the backstop, which is what it's there for.
'    1  Manager binary not found
' ============================================================

Module Program

    ' Shared with GSM.Manager.ManagerProgram — keep in sync.
    ' Plain (session-local) names: watchdog + Manager run in the
    ' same interactive logon session, so a Global\ scope (and its
    ' privilege quirks) isn't needed.
    Private Const ManagerMutexName As String = "PowerGSM.Manager.SingleInstance"
    Private Const WatchdogMutexName As String = "PowerGSM.Watchdog.SingleInstance"
    Private Const EnvWatched As String = "POWERGSM_WATCHDOG"

    ' Manager exit-code contract (see header).
    Private Const ExitCleanQuit As Integer = 0
    Private Const ExitDeferred As Integer = 10
    Private Const ExitRelaunchNormal As Integer = 20
    Private Const ExitRelaunchSafe As Integer = 21

    Private Const ManagerPollMs As Integer = 3000
    Private Const RestartBackoffMs As Integer = 2000

    Private _config As WatchdogConfig
    Private _logPath As String

    Function Main(args As String()) As Integer
        Dim baseDir = AppContext.BaseDirectory
        _logPath = Path.Combine(baseDir, "watchdog.log")
        _config = LoadConfig(baseDir)

        ' Single-instance: only one watchdog at a time.
        Dim createdNew As Boolean = False
        Using wdMutex As New Mutex(True, WatchdogMutexName, createdNew)
            If Not createdNew Then
                Log("Another watchdog is already running; exiting.")
                Return 0
            End If

            Return RunLoop(baseDir)
        End Using
    End Function

    Private Function RunLoop(baseDir As String) As Integer
        Dim managerPath = ResolveManagerPath(baseDir)
        If Not File.Exists(managerPath) Then
            Log($"Manager not found at '{managerPath}'; exiting.")
            Return 1
        End If
        Log($"Watchdog started. Target: {managerPath}")

        Dim crashTimes As New List(Of DateTime)()
        Dim nextArgs As String = ""   ' "--safe-mode" when escalating

        Do
            ' If a Manager is already up (manual launch, or a prior
            ' loop's relaunch we lost the handle to), monitor it
            ' instead of spawning a duplicate.
            If ManagerIsRunning() Then
                Log("A Manager is already running; monitoring it.")
                WaitForManagerToExit()
                Log("Monitored Manager exited; watchdog standing down.")
                Return 0
            End If

            Log($"Launching Manager{If(String.IsNullOrEmpty(nextArgs), "", " " & nextArgs)}.")
            Dim exitCode = LaunchManagerAndWait(managerPath, nextArgs)
            nextArgs = ""
            Log($"Manager exited with code {exitCode}.")

            Select Case exitCode
                Case ExitCleanQuit
                    Log("Clean quit; watchdog standing down.")
                    Return 0

                Case ExitDeferred
                    Log("Manager deferred to an existing instance; monitoring that one.")
                    WaitForManagerToExit()
                    Log("Monitored Manager exited; watchdog standing down.")
                    Return 0

                Case ExitRelaunchNormal
                    Log("Relaunch (normal) requested.")
                    Continue Do

                Case ExitRelaunchSafe
                    Log("Relaunch (safe mode) requested.")
                    nextArgs = "--safe-mode"
                    Continue Do

                Case Else
                    ' Crash path.
                    crashTimes.Add(DateTime.UtcNow)
                    PruneOlderThan(crashTimes, _config.WindowSeconds)

                    If crashTimes.Count > _config.MaxRestartsInWindow Then
                        Log($"Rapid-restart limit hit ({crashTimes.Count} crashes in " &
                            $"{_config.WindowSeconds}s, limit {_config.MaxRestartsInWindow}); giving up. " &
                            "Standing down (exit 0) so the Task Scheduler backstop does not relaunch " &
                            "the watchdog into the same loop — fix the Manager and sign in again, or " &
                            "start the watchdog manually.")
                        Return 0
                    End If

                    Dim rapid = CountWithin(crashTimes, _config.SafeModeRapidWindowSeconds)
                    If rapid >= _config.SafeModeAfterRapidCount Then
                        Log($"{rapid} crashes within {_config.SafeModeRapidWindowSeconds}s; " &
                            "next launch adds --safe-mode.")
                        nextArgs = "--safe-mode"
                    End If

                    Thread.Sleep(RestartBackoffMs)
                    Continue Do
            End Select
        Loop
    End Function

    ' ---- Manager detection / launch ----

    ''' <summary>
    ''' True if a Manager is holding its single-instance mutex.
    ''' Probe-only: open the existing mutex and immediately release.
    ''' An access-denied result still means it EXISTS (ACL-protected),
    ''' so treat that as running too.
    ''' </summary>
    Private Function ManagerIsRunning() As Boolean
        Dim m As Mutex = Nothing
        Try
            If Mutex.TryOpenExisting(ManagerMutexName, m) Then
                If m IsNot Nothing Then m.Dispose()
                Return True
            End If
            Return False
        Catch ex As UnauthorizedAccessException
            Return True
        Catch
            Return False
        End Try
    End Function

    Private Sub WaitForManagerToExit()
        Do While ManagerIsRunning()
            Thread.Sleep(ManagerPollMs)
        Loop
    End Sub

    Private Function LaunchManagerAndWait(managerPath As String, args As String) As Integer
        Try
            Dim psi As New ProcessStartInfo()
            psi.FileName = managerPath
            psi.Arguments = If(args, "")
            psi.WorkingDirectory = Path.GetDirectoryName(managerPath)
            ' UseShellExecute=False so we can inject the env var and
            ' read ExitCode. A WinForms app still shows its own window.
            psi.UseShellExecute = False
            psi.EnvironmentVariables(EnvWatched) = "1"

            Dim p = Process.Start(psi)
            If p Is Nothing Then
                Log("Process.Start returned Nothing.")
                Return -1
            End If
            p.WaitForExit()
            Return p.ExitCode
        Catch ex As Exception
            Log($"Failed to launch Manager: {ex.Message}")
            Return -1
        End Try
    End Function

    Private Function ResolveManagerPath(baseDir As String) As String
        Dim p = _config.ManagerPath
        If String.IsNullOrWhiteSpace(p) Then p = "GSM.Manager.exe"
        If Path.IsPathRooted(p) Then Return p
        Return Path.Combine(baseDir, p)
    End Function

    ' ---- Crash-history helpers ----

    Private Sub PruneOlderThan(times As List(Of DateTime), windowSeconds As Integer)
        Dim cutoff = DateTime.UtcNow.AddSeconds(-windowSeconds)
        times.RemoveAll(Function(t) t < cutoff)
    End Sub

    Private Function CountWithin(times As List(Of DateTime), seconds As Integer) As Integer
        Dim cutoff = DateTime.UtcNow.AddSeconds(-seconds)
        ' Enumerable.Count(...) explicitly: `times.Count(lambda)` binds to
        ' List(Of T).Count (the Integer property) and fails to index.
        Return Enumerable.Count(times, Function(t) t >= cutoff)
    End Function

    ' ---- Config + logging ----

    Private Function LoadConfig(baseDir As String) As WatchdogConfig
        ' Note: local is cfgPath, not 'path' — a local named 'path' would
        ' shadow the System.IO.Path type used on the same line.
        Dim cfgPath = Path.Combine(baseDir, "watchdogsettings.json")
        Try
            If File.Exists(cfgPath) Then
                Dim json = File.ReadAllText(cfgPath)
                Dim cfg = JsonSerializer.Deserialize(Of WatchdogConfig)(
                    json, New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True})
                If cfg IsNot Nothing Then Return cfg
            End If
        Catch ex As Exception
            Log($"Failed to read watchdogsettings.json ({ex.Message}); using defaults.")
        End Try
        Return New WatchdogConfig()
    End Function

    Private Sub Log(message As String)
        Dim line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}"
        Try
            File.AppendAllText(_logPath, line & Environment.NewLine)
        Catch
            ' Logging must never take the watchdog down.
        End Try
        Try
            Console.WriteLine(line)
        Catch
        End Try
    End Sub

End Module

Public Class WatchdogConfig
    Public Property ManagerPath As String = "GSM.Manager.exe"
    Public Property MaxRestartsInWindow As Integer = 5
    Public Property WindowSeconds As Integer = 300
    Public Property SafeModeAfterRapidCount As Integer = 2
    Public Property SafeModeRapidWindowSeconds As Integer = 60
End Class
