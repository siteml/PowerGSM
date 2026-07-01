' ============================================================
'  GSM.Node — --shim-reconnect-test harness (Phase 8-1, slice 3a)
'
'  Proves the adopt-on-restart mechanism on Windows, without a live game
'  or the Linux node. Flow:
'
'    1. ShimSession #1 launches the deployed shim + a cmd.exe dummy that
'       prints a marker then stays alive; wait for the marker live.
'    2. #1 Detaches (sends Detach, drops its connection) but LEAVES the
'       shim + game running — simulating a clean Node shutdown.
'    3. ShimSession #2 Adopts the same endpoint (no relaunch): it
'       reconnects to the still-running shim, handshakes, and must learn
'       the SAME game pid and receive the marker again via the shim's
'       replayed output ring.
'    4. #2 StopGame(kill) -> Exited.
'
'  PASS requires: started, live marker, adopted, same game pid, REPLAYED
'  marker on the second connection, and a clean exit. That exercises the
'  whole slice-3 reconnect path (shim multi-connection + ring replay +
'  ShimSession.AdoptAsync) end to end. The shim's own --self-test covers
'  single-connect; this covers reconnect.
' ============================================================
Imports System
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Shim.Protocol

Namespace GSM.Node

    Friend Module ShimReconnectTest

        Private Const Marker As String = "NODE_SHIM_RECONNECT_MARKER"
        Private ReadOnly _transcript As New StringBuilder()

        Public Async Function RunAsync() As Task(Of Integer)
            Report($"[shim-reconnect-test] starting (OS={OsName()})")

            If Not OperatingSystem.IsWindows() Then
                Report("[shim-reconnect-test] skipped (cmd.exe dummy is Windows-only until slice 1c)")
                Flush(0)
                Return 0
            End If

            Dim instanceId As String = "reconnect-" & Guid.NewGuid().ToString("N").Substring(0, 8)
            Dim socketDir As String = Path.GetTempPath()

            Dim spec As New SpawnSpec With {
                .ExePath = "cmd.exe",
                .Arguments = "/c ""echo " & Marker & " & pause >nul""",
                .WorkingDirectory = Nothing,
                .Environment = Nothing,
                .Strategy = "StdoutCapture"
            }

            Dim sawMarker1 As Integer = 0
            Dim sawMarker2 As Integer = 0

            Dim onLine1 As Action(Of DateTime, String, Boolean) =
                Sub(ts, text, isErr)
                    Report($"[shim-reconnect-test] #1 line(err={isErr}): {text}")
                    If text IsNot Nothing AndAlso text.Contains(Marker) Then Interlocked.Exchange(sawMarker1, 1)
                End Sub
            Dim onExited1 As Action(Of Integer) =
                Sub(code) Report($"[shim-reconnect-test] #1 exited/detached: code={code}")

            Dim onLine2 As Action(Of DateTime, String, Boolean) =
                Sub(ts, text, isErr)
                    Report($"[shim-reconnect-test] #2 line(err={isErr}): {text}")
                    If text IsNot Nothing AndAlso text.Contains(Marker) Then Interlocked.Exchange(sawMarker2, 1)
                End Sub
            Dim exitTcs As New TaskCompletionSource(Of Integer)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim onExited2 As Action(Of Integer) =
                Sub(code)
                    Report($"[shim-reconnect-test] #2 exited: code={code}")
                    exitTcs.TrySetResult(code)
                End Sub

            Dim started1 As Boolean = False
            Dim gamePid1 As Integer = -1
            Dim adopted2 As Boolean = False
            Dim samePid As Boolean = False
            Dim replayedMarker As Boolean = False
            Dim exitSeen As Boolean = False
            Dim exitCode As Integer = -999

            Using session1 As New ShimSession(instanceId, socketDir, onLine1, onExited1, Nothing)
                started1 = Await session1.StartAsync(spec, 10000, CancellationToken.None).ConfigureAwait(False)
                If Not started1 Then
                    Report("[shim-reconnect-test] FAIL: session #1 did not start")
                    Flush(1)
                    Return 1
                End If
                gamePid1 = session1.GamePid
                Report($"[shim-reconnect-test] #1 started: shimPid={session1.ShimPid} gamePid={gamePid1} endpoint={session1.Endpoint}")

                ' Wait for the live marker on the first connection.
                Await WaitFlagAsync(Function() Interlocked.CompareExchange(sawMarker1, 0, 0) = 1, 10).ConfigureAwait(False)

                ' Simulate a clean Node shutdown: detach but leave shim + game alive.
                Await session1.DetachAsync(CancellationToken.None).ConfigureAwait(False)
                Report("[shim-reconnect-test] #1 detached (shim + game left running)")
                Await Task.Delay(300).ConfigureAwait(False)   ' let the shim loop back to accept

                Using session2 As New ShimSession(instanceId, socketDir, onLine2, onExited2, Nothing)
                    adopted2 = Await session2.AdoptAsync(session1.Endpoint, 10000, CancellationToken.None).ConfigureAwait(False)
                    If Not adopted2 Then
                        Report("[shim-reconnect-test] FAIL: session #2 did not adopt")
                        Flush(1)
                        Return 1
                    End If
                    samePid = (session2.GamePid = gamePid1 AndAlso gamePid1 > 0)
                    Report($"[shim-reconnect-test] #2 adopted: gamePid={session2.GamePid} shimPid={session2.ShimPid} proto=v{session2.ProtocolVersion} shimVer={session2.ShimVersion} samePid={samePid}")

                    ' The shim replays its output ring on reconnect -> we should see the marker again.
                    Await WaitFlagAsync(Function() Interlocked.CompareExchange(sawMarker2, 0, 0) = 1, 8).ConfigureAwait(False)
                    replayedMarker = Interlocked.CompareExchange(sawMarker2, 0, 0) = 1

                    Await session2.SendStopAsync("kill", 5000, CancellationToken.None).ConfigureAwait(False)
                    Dim finished As Task = Await Task.WhenAny(exitTcs.Task, Task.Delay(10000)).ConfigureAwait(False)
                    If finished Is exitTcs.Task Then
                        exitSeen = True
                        exitCode = exitTcs.Task.Result
                    End If
                End Using
            End Using

            Dim marker1Seen As Boolean = Interlocked.CompareExchange(sawMarker1, 0, 0) = 1
            Dim ok As Boolean = started1 AndAlso marker1Seen AndAlso adopted2 AndAlso samePid AndAlso replayedMarker AndAlso exitSeen
            Report($"[shim-reconnect-test] started={started1} liveMarker={marker1Seen} adopted={adopted2} samePid={samePid} replayedMarker={replayedMarker} exit={exitSeen} code={exitCode}")
            Report(If(ok, "[shim-reconnect-test] PASS", "[shim-reconnect-test] FAIL"))
            Flush(If(ok, 0, 1))
            Return If(ok, 0, 1)
        End Function

        Private Async Function WaitFlagAsync(predicate As Func(Of Boolean), timeoutSeconds As Integer) As Task
            Dim deadline As DateTime = DateTime.UtcNow.AddSeconds(timeoutSeconds)
            While Not predicate() AndAlso DateTime.UtcNow < deadline
                Await Task.Delay(50).ConfigureAwait(False)
            End While
        End Function

        Private Sub Report(line As String)
            _transcript.AppendLine(line)
            Try
                Console.WriteLine(line)
            Catch
            End Try
        End Sub

        Private Sub Flush(resultCode As Integer)
            Try
                Dim resultPath As String = Path.Combine(AppContext.BaseDirectory, "shim-reconnect-result.txt")
                _transcript.AppendLine($"[shim-reconnect-test] result code = {resultCode}")
                File.WriteAllText(resultPath, _transcript.ToString())
                Try
                    Console.WriteLine($"[shim-reconnect-test] transcript written to {resultPath}")
                Catch
                End Try
            Catch
                ' best-effort
            End Try
        End Sub

        Private Function OsName() As String
            If OperatingSystem.IsWindows() Then Return "Windows"
            If OperatingSystem.IsLinux() Then Return "Linux"
            Return "other"
        End Function

    End Module

End Namespace
