' ============================================================
'  GSM.Node — --shim-self-test harness (Phase 8-1, slice 2b)
'
'  Drives a real ShimSession against the actually-deployed shim
'  (GSM.Shim\<ver>\GSM.Shim.exe) spawning a cmd.exe dummy that prints a
'  marker then stays alive. Asserts the whole Node->shim client path:
'  launch shim -> connect -> handshake -> SpawnAck (game pid) -> a stdout
'  line callback carrying the marker -> StopGame(kill) -> exit callback.
'
'  Windows-runnable (GSM.Node.exe --shim-self-test, output via
'  AttachConsole + a result file), so the Node-side client gets a real
'  integration test without a live game or the Linux node. The shim's own
'  --self-test covers the shim side; this covers our side of the wire.
' ============================================================
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Shim.Protocol

Namespace GSM.Node

    Friend Module ShimSelfTest

        Private Const Marker As String = "NODE_SHIM_SELFTEST_MARKER"
        Private ReadOnly _transcript As New StringBuilder()

        Public Async Function RunAsync() As Task(Of Integer)
            Report($"[shim-self-test] starting (OS={OsName()})")

            Dim sawMarker As Integer = 0
            Dim exitTcs As New TaskCompletionSource(Of Integer)(TaskCreationOptions.RunContinuationsAsynchronously)

            Dim onLine As Action(Of DateTime, String, Boolean) =
                Sub(ts, text, isErr)
                    Report($"[shim-self-test] line(err={isErr}): {text}")
                    If text IsNot Nothing AndAlso text.Contains(Marker) Then
                        Interlocked.Exchange(sawMarker, 1)
                    End If
                End Sub

            Dim onExited As Action(Of Integer) =
                Sub(code)
                    Report($"[shim-self-test] exited: code={code}")
                    exitTcs.TrySetResult(code)
                End Sub

            Dim spec As New SpawnSpec With {
                .ExePath = "cmd.exe",
                .Arguments = "/c ""echo " & Marker & " & pause >nul""",
                .WorkingDirectory = Nothing,
                .Environment = Nothing,
                .Strategy = "StdoutCapture"
            }

            Dim ok As Boolean = False
            Dim exitSeen As Boolean = False
            Dim exitCode As Integer = -999

            Using session As New ShimSession("selftest", Path.GetTempPath(), onLine, onExited, Nothing)
                Dim started As Boolean = Await session.StartAsync(spec, 10000, CancellationToken.None).ConfigureAwait(False)
                If Not started Then
                    Report("[shim-self-test] FAIL: session did not start")
                    Flush(1)
                    Return 1
                End If

                Report($"[shim-self-test] started: shimPid={session.ShimPid} gamePid={session.GamePid} proto=v{session.ProtocolVersion} shimVer={session.ShimVersion}")

                ' Wait for the marker line (or time out), then stop the game.
                Dim deadline As DateTime = DateTime.UtcNow.AddSeconds(10)
                While Interlocked.CompareExchange(sawMarker, 0, 0) = 0 AndAlso DateTime.UtcNow < deadline
                    Await Task.Delay(50).ConfigureAwait(False)
                End While

                Await session.SendStopAsync("kill", 5000, CancellationToken.None).ConfigureAwait(False)

                Dim finished As Task = Await Task.WhenAny(exitTcs.Task, Task.Delay(10000)).ConfigureAwait(False)
                If finished Is exitTcs.Task Then
                    exitSeen = True
                    exitCode = exitTcs.Task.Result
                End If
            End Using

            Dim markerSeen As Boolean = Interlocked.CompareExchange(sawMarker, 0, 0) = 1
            ok = markerSeen AndAlso exitSeen
            Report($"[shim-self-test] marker={markerSeen} exit={exitSeen} code={exitCode}")
            Report(If(ok, "[shim-self-test] PASS", "[shim-self-test] FAIL"))
            Flush(If(ok, 0, 1))
            Return If(ok, 0, 1)
        End Function

        Private Sub Report(line As String)
            _transcript.AppendLine(line)
            Try
                Console.WriteLine(line)
            Catch
                ' No console attached (WinExe launched without a parent console);
                ' the result file is the reliable channel.
            End Try
        End Sub

        Private Sub Flush(resultCode As Integer)
            Try
                Dim resultPath As String = Path.Combine(AppContext.BaseDirectory, "shim-selftest-result.txt")
                _transcript.AppendLine($"[shim-self-test] result code = {resultCode}")
                File.WriteAllText(resultPath, _transcript.ToString())
                Try
                    Console.WriteLine($"[shim-self-test] transcript written to {resultPath}")
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
