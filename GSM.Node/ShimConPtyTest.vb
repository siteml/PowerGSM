' ============================================================
'  GSM.Node — --shim-conpty-test harness (ConPTY, EventStream Slice 4)
'
'  Proves the whole Node-side ConPTY chain against the DEPLOYED shim,
'  without a live game: pseudoconsole spawn (protocol v2), the raw VT
'  stream arriving as Stdout frames, the node-side VtConsoleFilter
'  producing CLEAN lines (no ESC bytes), the ring replay across a
'  detach/adopt staying clean (the filter is per-session state — the
'  adopting session gets its own), and stop -> Exited (which also
'  exercises the shim's ClosePseudoConsole-before-drain ordering — a
'  hang at stop means the pty release regressed). Flow mirrors
'  --shim-reconnect-test:
'
'    1. ShimSession #1 (filterVt) launches the deployed shim with a
'       Strategy=ConPty cmd.exe dummy; wait for the CLEAN marker line.
'    2. Detach (shim + game keep running), adopt from ShimSession #2
'       (filterVt): same game pid + the marker again via ring replay,
'       still clean through #2's fresh filter.
'    3. StopGame(kill) -> Exited.
'
'  PASS additionally requires that no emitted line contained a raw
'  ESC (0x1B) — the filter's whole job. Requires the deployed shim to
'  be protocol v2+ (a pre-ConPTY shim fails the version gate with a
'  clear "shim protocol" error — redeploy the shim first).
' ============================================================
Imports System
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Shim.Protocol
Imports Microsoft.Extensions.Logging

Namespace GSM.Node

    Friend Module ShimConPtyTest

        Private Const Marker As String = "NODE_CONPTY_MARKER"
        Private ReadOnly _transcript As New StringBuilder()

        Public Async Function RunAsync() As Task(Of Integer)
            Report($"[shim-conpty-test] starting (OS={OsName()})")
            ' Build provenance: the harness runs whatever GSM.Node.exe sits in
            ' the deploy dir — print enough to spot a stale binary instantly.
            Try
                Dim exePath = Environment.ProcessPath
                Report($"[shim-conpty-test] binary: {exePath} (modified {File.GetLastWriteTime(exePath):yyyy-MM-dd HH:mm:ss})")
            Catch
            End Try

            If Not OperatingSystem.IsWindows() Then
                Report("[shim-conpty-test] skipped (ConPTY is Windows-only; Linux resolves to stdout capture)")
                Flush(0)
                Return 0
            End If

            Dim instanceId As String = "conpty-" & Guid.NewGuid().ToString("N").Substring(0, 8)
            Dim socketDir As String = Path.GetTempPath()

            ' The dummy must write CONTINUOUSLY, like a real game server.
            ' A one-shot "echo X & pause" is a bad ConPTY canary: conhost
            ' withholds its first paint for an idle one-shot writer under
            ' node parentage (observed 2026-08-11 — the init burst only
            ' flushed in the detach gap), while a continuous writer flows
            ' fine (the live Palworld run streamed). One marker line per
            ' ~1s until killed.
            Dim spec As New SpawnSpec With {
                .ExePath = "cmd.exe",
                .Arguments = "/c ""for /l %i in (1,1,1000) do @(echo " & Marker & "_%i & ping -n 2 127.0.0.1 >nul)""",
                .WorkingDirectory = Nothing,
                .Environment = Nothing,
                .Strategy = "ConPty"
            }

            Report($"[shim-conpty-test] dummy: {spec.ExePath} {spec.Arguments}")

            Dim sawMarker1 As Integer = 0
            Dim sawMarker2 As Integer = 0
            Dim sawEsc As Integer = 0      ' any raw ESC in an emitted line = filter failure

            Dim onLine1 As Action(Of DateTime, String, Boolean) =
                Sub(ts, text, isErr)
                    Report($"[shim-conpty-test] #1 line(err={isErr}): {Printable(text)}")
                    If text IsNot Nothing Then
                        If text.Contains(Marker) Then Interlocked.Exchange(sawMarker1, 1)
                        If text.IndexOf(ChrW(27)) >= 0 Then Interlocked.Exchange(sawEsc, 1)
                    End If
                End Sub
            Dim onExited1 As Action(Of Integer) =
                Sub(code) Report($"[shim-conpty-test] #1 exited/detached: code={code}")

            Dim onLine2 As Action(Of DateTime, String, Boolean) =
                Sub(ts, text, isErr)
                    Report($"[shim-conpty-test] #2 line(err={isErr}): {Printable(text)}")
                    If text IsNot Nothing Then
                        If text.Contains(Marker) Then Interlocked.Exchange(sawMarker2, 1)
                        If text.IndexOf(ChrW(27)) >= 0 Then Interlocked.Exchange(sawEsc, 1)
                    End If
                End Sub
            Dim exitTcs As New TaskCompletionSource(Of Integer)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim onExited2 As Action(Of Integer) =
                Sub(code)
                    Report($"[shim-conpty-test] #2 exited: code={code}")
                    exitTcs.TrySetResult(code)
                End Sub

            Dim started1 As Boolean = False
            Dim gamePid1 As Integer = -1
            Dim adopted2 As Boolean = False
            Dim samePid As Boolean = False
            Dim replayedMarker As Boolean = False
            Dim exitSeen As Boolean = False
            Dim exitCode As Integer = -999

            ' Surface EVERYTHING the session logs — including the shim's own
            ' stderr diagnostics, which a Nothing logger silently swallowed.
            Dim sessionLogger As New ReportLogger()

            Using session1 As New ShimSession(instanceId, socketDir, onLine1, onExited1, sessionLogger, filterVt:=True)
                ' Raw-frame diagnostic: record the exact pty bytes reaching the
                ' node BEFORE the VT filter, so a filter/splitter issue is
                ' distinguishable from frames never arriving.
                session1.StdoutTap = Sub(raw) Report($"[shim-conpty-test] #1 raw({raw.Length}): {PrintableBytes(raw)}")
                started1 = Await session1.StartAsync(spec, 10000, CancellationToken.None,
                                                     requireProtocolVersion:=2).ConfigureAwait(False)
                If Not started1 Then
                    Report($"[shim-conpty-test] FAIL: session #1 did not start: {session1.LastError}")
                    Report("[shim-conpty-test] (a 'shim protocol' error means the deployed shim predates ConPTY — rebuild/redeploy GSM.Shim first)")
                    Flush(1)
                    Return 1
                End If
                gamePid1 = session1.GamePid
                Report($"[shim-conpty-test] #1 started: shimPid={session1.ShimPid} gamePid={gamePid1} proto=v{session1.ProtocolVersion} endpoint={session1.Endpoint}")

                Await WaitFlagAsync(Function() Interlocked.CompareExchange(sawMarker1, 0, 0) = 1, 10).ConfigureAwait(False)

                Await session1.DetachAsync(CancellationToken.None).ConfigureAwait(False)
                Report("[shim-conpty-test] #1 detached (shim + game + pty left running)")
                Await Task.Delay(300).ConfigureAwait(False)

                Using session2 As New ShimSession(instanceId, socketDir, onLine2, onExited2, sessionLogger, filterVt:=True)
                    session2.StdoutTap = Sub(raw) Report($"[shim-conpty-test] #2 raw({raw.Length}): {PrintableBytes(raw)}")
                    adopted2 = Await session2.AdoptAsync(session1.Endpoint, 10000, CancellationToken.None).ConfigureAwait(False)
                    If Not adopted2 Then
                        Report("[shim-conpty-test] FAIL: session #2 did not adopt")
                        Flush(1)
                        Return 1
                    End If
                    samePid = (session2.GamePid = gamePid1 AndAlso gamePid1 > 0)
                    Report($"[shim-conpty-test] #2 adopted: gamePid={session2.GamePid} shimPid={session2.ShimPid} samePid={samePid}")

                    ' Ring replay: the raw VT ring re-flows through #2's fresh
                    ' filter — the marker must appear again, still clean.
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
            Dim escLeaked As Boolean = Interlocked.CompareExchange(sawEsc, 0, 0) = 1
            Dim ok As Boolean = started1 AndAlso marker1Seen AndAlso adopted2 AndAlso samePid AndAlso
                                replayedMarker AndAlso exitSeen AndAlso Not escLeaked
            Report($"[shim-conpty-test] started={started1} cleanMarker={marker1Seen} adopted={adopted2} samePid={samePid} replayedMarker={replayedMarker} exit={exitSeen} code={exitCode} escLeaked={escLeaked}")
            Report(If(ok, "[shim-conpty-test] PASS", "[shim-conpty-test] FAIL"))
            Flush(If(ok, 0, 1))
            Return If(ok, 0, 1)
        End Function

        ''' <summary>Forwards every ShimSession log line (all levels — incl.
        ''' the Debug-level relays of the shim's own stderr) into the
        ''' harness transcript.</summary>
        Private NotInheritable Class ReportLogger
            Implements ILogger

            Public Function BeginScope(Of TState)(state As TState) As IDisposable Implements ILogger.BeginScope
                Return Nothing
            End Function

            Public Function IsEnabled(logLevel As LogLevel) As Boolean Implements ILogger.IsEnabled
                Return True
            End Function

            Public Sub Log(Of TState)(logLevel As LogLevel, eventId As EventId, state As TState,
                                      exception As Exception,
                                      formatter As Func(Of TState, Exception, String)) Implements ILogger.Log
                Try
                    Dim msg = If(formatter IsNot Nothing, formatter(state, exception), If(state IsNot Nothing, state.ToString(), ""))
                    Report($"[shim-conpty-test] [{logLevel}] {msg}{If(exception IsNot Nothing, " :: " & exception.Message, "")}")
                Catch
                End Try
            End Sub
        End Class

        ''' <summary>Escape a raw byte payload for the transcript (\xNN for
        ''' non-printables), capped so a big replay doesn't flood it.</summary>
        Private Function PrintableBytes(raw As Byte()) As String
            If raw Is Nothing OrElse raw.Length = 0 Then Return "(empty)"
            Dim sb As New StringBuilder(Math.Min(raw.Length, 600) * 4)
            For i As Integer = 0 To Math.Min(raw.Length, 600) - 1
                Dim b As Byte = raw(i)
                If b >= 32 AndAlso b < 127 Then
                    sb.Append(ChrW(b))
                Else
                    sb.Append("\x").Append(b.ToString("X2"))
                End If
            Next
            If raw.Length > 600 Then sb.Append($"...(+{raw.Length - 600} bytes)")
            Return sb.ToString()
        End Function

        ''' <summary>Escape control chars so a filter leak is visible in the transcript.</summary>
        Private Function Printable(text As String) As String
            If String.IsNullOrEmpty(text) Then Return ""
            Dim sb As New StringBuilder(text.Length)
            For Each ch In text
                If AscW(ch) < 32 AndAlso ch <> ChrW(9) Then
                    sb.Append("\x").Append(AscW(ch).ToString("X2"))
                Else
                    sb.Append(ch)
                End If
            Next
            Return sb.ToString()
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
                Dim resultPath As String = Path.Combine(AppContext.BaseDirectory, "shim-conpty-result.txt")
                _transcript.AppendLine($"[shim-conpty-test] result code = {resultCode}")
                File.WriteAllText(resultPath, _transcript.ToString())
                Try
                    Console.WriteLine($"[shim-conpty-test] transcript written to {resultPath}")
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
