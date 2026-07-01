' ============================================================
'  GSM.Node — ShimSession (Phase 8-1, slice 2b)
'
'  The Node-side client for one GSM.Shim supervisor. It:
'    1. launches the versioned shim exe (GSM.Shim\<ver>\GSM.Shim[.exe]),
'       keeping a Process handle for ShimPid / liveness;
'    2. connects to the shim's pipe/socket (node role), handshakes,
'       sends Spawn(spec), and reads SpawnAck to learn the game PID;
'    3. runs a background read loop that turns Stdout/Stderr frames into
'       line callbacks (the same shape ProcessManager.OutputDataReceived
'       feeds today) and an Exited(code) frame into an exit callback +
'       a completed ExitedTask.
'
'  The shim owns the game's raw pipes; the Node never does. That is what
'  makes a Node restart non-fatal to the game's stdout (slice 3 reconnects
'  to a live shim). ProcessManager wires the callbacks; this class has no
'  knowledge of the ring buffer / EventStore.
' ============================================================
Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports GSM.Shim.Protocol

Namespace GSM.Node

    Friend NotInheritable Class ShimSession
        Implements IDisposable

        Private ReadOnly _instanceId As String
        Private ReadOnly _socketDir As String
        Private ReadOnly _onLine As Action(Of DateTime, String, Boolean)
        Private ReadOnly _onExited As Action(Of Integer)
        Private ReadOnly _logger As ILogger

        Private _shimProc As Process
        Private _conn As FrameConnection
        Private _readLoop As Task
        Private ReadOnly _exitTcs As New TaskCompletionSource(Of Integer)(TaskCreationOptions.RunContinuationsAsynchronously)
        Private _exitSignalled As Integer
        Private _detaching As Integer             ' set on a deliberate Detach: suppress the exit cascade + kill-on-dispose
        Private _gamePid As Integer
        Private _protocolVersion As Integer
        Private _endpoint As String
        Private _shimVersion As String
        Private _disposed As Boolean
        Private _ownsShim As Boolean              ' True: we launched the shim (kill on Dispose). False: adopted/detached.
        Private _adoptedShimPid As Integer = -1   ' shim PID from HelloAck when adopted (no _shimProc handle)
        Private _adoptedLogFilePaths As IReadOnlyList(Of String)   ' Phase 8-3: tail paths the shim echoed on adopt (HelloAck.LogFilePaths)

        Private ReadOnly _stdoutSplit As New LineSplitter()
        Private ReadOnly _stderrSplit As New LineSplitter()

        ''' <param name="socketDir">Directory for the Unix socket on Linux (ignored on Windows pipes).</param>
        ''' <param name="onLine">(timestampUtc, line, isError) — one call per complete game output line.</param>
        ''' <param name="onExited">(exitCode) — fired once when the game ends or the link drops.</param>
        Public Sub New(instanceId As String,
                       socketDir As String,
                       onLine As Action(Of DateTime, String, Boolean),
                       onExited As Action(Of Integer),
                       logger As ILogger)
            _instanceId = instanceId
            _socketDir = socketDir
            _onLine = onLine
            _onExited = onExited
            _logger = If(logger, CType(NullLogger.Instance, ILogger))
        End Sub

        Public ReadOnly Property GamePid As Integer
            Get
                Return _gamePid
            End Get
        End Property

        Public ReadOnly Property ShimPid As Integer
            Get
                If _shimProc IsNot Nothing Then
                    Try
                        Return _shimProc.Id
                    Catch
                        Return -1
                    End Try
                End If
                Return _adoptedShimPid
            End Get
        End Property

        Public ReadOnly Property ProtocolVersion As Integer
            Get
                Return _protocolVersion
            End Get
        End Property

        Public ReadOnly Property Endpoint As String
            Get
                Return _endpoint
            End Get
        End Property

        Public ReadOnly Property ShimVersion As String
            Get
                Return _shimVersion
            End Get
        End Property

        ''' <summary>
        ''' Phase 8-3: log-file paths the shim echoed back on AdoptAsync
        ''' (HelloAck.LogFilePaths) — what the Node tails for this game. Lets a
        ''' node.db-less lean adopt recover where to tail. Nothing on a fresh
        ''' start or from a pre-8-3 shim.
        ''' </summary>
        Public ReadOnly Property AdoptedLogFilePaths As IReadOnlyList(Of String)
            Get
                Return _adoptedLogFilePaths
            End Get
        End Property

        ''' <summary>Completes with the game's exit code (or -1 if the link dropped first).</summary>
        Public ReadOnly Property ExitedTask As Task(Of Integer)
            Get
                Return _exitTcs.Task
            End Get
        End Property

        ''' <summary>
        ''' Launch the shim, connect + handshake, send Spawn(spec), read
        ''' SpawnAck. Returns True on success (GamePid is then valid and the
        ''' read loop is running). On any failure returns False and the shim
        ''' process is torn down.
        ''' </summary>
        Public Async Function StartAsync(spec As SpawnSpec, launchTimeoutMs As Integer, ct As CancellationToken) As Task(Of Boolean)
            Try
                _ownsShim = True   ' we are launching the shim; Dispose may kill it
                _endpoint = MakeEndpoint(_instanceId, _socketDir)

                Dim shimExe As String = ResolveShimExePath(_shimVersion)
                If Not File.Exists(shimExe) Then
                    _logger.LogError("Shim exe not found at {Path} for {Id}", shimExe, _instanceId)
                    Return False
                End If

                Dim psi As New ProcessStartInfo() With {
                    .FileName = shimExe,
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True
                }
                psi.ArgumentList.Add("--instance-id")
                psi.ArgumentList.Add(_instanceId)
                psi.ArgumentList.Add("--endpoint")
                psi.ArgumentList.Add(_endpoint)

                _shimProc = New Process() With {.StartInfo = psi, .EnableRaisingEvents = True}
                AddHandler _shimProc.OutputDataReceived, AddressOf OnShimStdout
                AddHandler _shimProc.ErrorDataReceived, AddressOf OnShimStderr
                If Not _shimProc.Start() Then
                    _logger.LogError("Shim process failed to start for {Id}", _instanceId)
                    Return False
                End If
                _shimProc.BeginOutputReadLine()
                _shimProc.BeginErrorReadLine()

                Dim stream As Stream = Await ConnectWithRetryAsync(_endpoint, launchTimeoutMs, ct).ConfigureAwait(False)
                _conn = New FrameConnection(stream)

                Dim ack As HelloAckMessage = Await ShimHandshake.ConnectAsync(_conn, ct).ConfigureAwait(False)
                _protocolVersion = ack.ProtocolVersion

                Await _conn.WriteFrameAsync(FrameType.Spawn, ProtocolCodec.Encode(spec), ct).ConfigureAwait(False)

                Dim ackFrame As Frame = Await _conn.ReadFrameAsync(ct).ConfigureAwait(False)
                If ackFrame.Kind <> FrameType.SpawnAck Then
                    _logger.LogError("Shim {Id}: expected SpawnAck, got {Kind}", _instanceId, ackFrame.Kind)
                    TryKillShim()
                    Return False
                End If

                Dim sack As SpawnAckMessage = ProtocolCodec.Decode(Of SpawnAckMessage)(ackFrame.Payload)
                If sack Is Nothing OrElse Not sack.Success OrElse sack.GamePid <= 0 Then
                    _logger.LogError("Shim {Id}: spawn failed: {Err}",
                                     _instanceId, If(sack IsNot Nothing, sack.ErrorMessage, "no ack"))
                    TryKillShim()
                    Return False
                End If

                _gamePid = sack.GamePid
                _readLoop = ReadLoopAsync()
                Return True

            Catch ex As Exception
                _logger.LogError(ex, "Shim {Id}: StartAsync failed", _instanceId)
                TryKillShim()
                Return False
            End Try
        End Function

        Public Async Function SendStopAsync(kind As String, timeoutMs As Integer, ct As CancellationToken) As Task
            Dim msg As New StopMessage With {.Kind = kind, .TimeoutMs = timeoutMs}
            Await SafeWriteAsync(FrameType.StopGame, ProtocolCodec.Encode(msg), ct).ConfigureAwait(False)
        End Function

        Public Async Function SendStdinAsync(data As Byte(), ct As CancellationToken) As Task
            Await SafeWriteAsync(FrameType.Stdin, data, ct).ConfigureAwait(False)
        End Function

        ''' <summary>Tell the shim to kill the game and exit.</summary>
        Public Async Function SendShutdownAsync(ct As CancellationToken) As Task
            Await SafeWriteAsync(FrameType.Shutdown, Nothing, ct).ConfigureAwait(False)
        End Function

        ''' <summary>Tell the shim to exit but leave the game running (clean Node-down).</summary>
        Public Async Function SendDetachAsync(ct As CancellationToken) As Task
            ' DELIBERATE detach: the shim keeps the game and waits for the next
            ' Node. Mark the session FIRST (before the frame is sent, so the
            ' flags are visible well before the shim closes the pipe and our read
            ' loop notices) so that:
            '   (a) the read loop's subsequent link-drop is NOT surfaced as a
            '       game exit — otherwise it routes through HandleProcessExited,
            '       which disposes this session (tree-killing the still-running
            '       game) and schedules a spurious crash-restart; and
            '   (b) Dispose/TryKillShim won't kill the game we just chose to
            '       leave running.
            _ownsShim = False
            Interlocked.Exchange(_detaching, 1)
            Await SafeWriteAsync(FrameType.Detach, Nothing, ct).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Adopt an ALREADY-RUNNING shim by connecting to its existing
        ''' endpoint (no shim launch). Handshakes, learns the live game PID +
        ''' shim PID from the HelloAck, and starts the read loop — which first
        ''' receives the shim's replayed output ring, then live output, then
        ''' Exited. Sends no Spawn (the game is already running). Returns True
        ''' on success. This is the Node-restart reconnect path (slice 3).
        ''' </summary>
        Public Async Function AdoptAsync(endpoint As String, connectTimeoutMs As Integer, ct As CancellationToken) As Task(Of Boolean)
            Try
                _ownsShim = False   ' we did not launch this shim; never kill it
                _endpoint = endpoint

                Dim stream As Stream = Await ConnectWithRetryAsync(endpoint, connectTimeoutMs, ct).ConfigureAwait(False)
                _conn = New FrameConnection(stream)

                Dim ack As HelloAckMessage = Await ShimHandshake.ConnectAsync(_conn, ct).ConfigureAwait(False)
                _protocolVersion = ack.ProtocolVersion
                _shimVersion = ack.ShimVersion
                _adoptedShimPid = ack.ShimPid
                _adoptedLogFilePaths = ack.LogFilePaths

                If String.Equals(ack.GameState, "running", StringComparison.Ordinal) Then
                    _gamePid = ack.GamePid
                    _readLoop = ReadLoopAsync()
                    Return True
                End If

                If String.Equals(ack.GameState, "exited", StringComparison.Ordinal) Then
                    ' Game ended while we were away: adopt anyway so the read
                    ' loop surfaces the Exited frame the shim replays.
                    _gamePid = ack.GamePid
                    _readLoop = ReadLoopAsync()
                    Return True
                End If

                ' state "none": nothing was ever spawned here — not adoptable.
                _logger.LogWarning("Shim {Id}: adopt found game state '{State}', nothing to adopt", _instanceId, ack.GameState)
                Try
                    If _conn IsNot Nothing Then _conn.Dispose()
                Catch
                End Try
                Return False

            Catch ex As Exception
                _logger.LogError(ex, "Shim {Id}: AdoptAsync failed", _instanceId)
                Try
                    If _conn IsNot Nothing Then _conn.Dispose()
                Catch
                End Try
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Release this shim while leaving it (and the game) running: send
        ''' Detach, tear down the local connection, and relinquish ownership
        ''' so Dispose will NOT kill the shim. Used on a clean Node-down (the
        ''' shim then waits for the next Node to reconnect) and by the
        ''' reconnect self-test.
        ''' </summary>
        Public Async Function DetachAsync(ct As CancellationToken) As Task
            _ownsShim = False
            Interlocked.Exchange(_detaching, 1)   ' deliberate detach: suppress the node-side exit cascade (see SendDetachAsync)
            Await SafeWriteAsync(FrameType.Detach, Nothing, ct).ConfigureAwait(False)
            Try
                If _conn IsNot Nothing Then _conn.Dispose()
            Catch
            End Try
        End Function

        ''' <summary>
        ''' Phase 8-3 rediscovery probe. Connect to a shim endpoint, perform the
        ''' Hello/HelloAck handshake, read what the shim reports about itself
        ''' (its instance id, the live game pid/state, shim pid/version), then
        ''' close — WITHOUT spawning, adopting, or starting a read loop. Lets the
        ''' Node discover and identify live shims straight from the OS namespace
        ''' (node.db-independent). The shim treats this as a brief Node
        ''' connection that drops: it keeps its game and loops back to accept the
        ''' real adopt. Returns Nothing if nothing is listening, the connection
        ''' or handshake times out, or the handshake fails (a dead/stale
        ''' endpoint). Never throws.
        ''' </summary>
        Public Shared Async Function ProbeEndpointAsync(endpoint As String,
                                                        timeoutMs As Integer,
                                                        logger As ILogger) As Task(Of ShimProbeResult)
            Dim log As ILogger = If(logger, CType(NullLogger.Instance, ILogger))
            Dim conn As FrameConnection = Nothing
            Try
                Dim totalMs As Integer = If(timeoutMs > 0, timeoutMs, 3000)
                Using cts As New CancellationTokenSource(totalMs)
                    Dim stream As Stream = Await ShimTransport.ConnectAsync(endpoint, cts.Token).ConfigureAwait(False)
                    conn = New FrameConnection(stream)
                    Dim ack As HelloAckMessage = Await ShimHandshake.ConnectAsync(conn, cts.Token).ConfigureAwait(False)
                    If ack Is Nothing Then Return Nothing
                    Return New ShimProbeResult With {
                        .InstanceId = ack.InstanceId,
                        .GamePid = ack.GamePid,
                        .GameState = ack.GameState,
                        .ShimPid = ack.ShimPid,
                        .ShimVersion = ack.ShimVersion,
                        .ProtocolVersion = ack.ProtocolVersion
                    }
                End Using
            Catch ex As Exception
                log.LogDebug(ex, "Shim probe of {Endpoint} failed (no/stale listener?)", endpoint)
                Return Nothing
            Finally
                Try
                    If conn IsNot Nothing Then conn.Dispose()
                Catch
                End Try
            End Try
        End Function

        ' ---------- internals ----------

        Private Async Function ReadLoopAsync() As Task
            Try
                Do
                    Dim f As Frame
                    Try
                        f = Await _conn.ReadFrameAsync(CancellationToken.None).ConfigureAwait(False)
                    Catch
                        Exit Do   ' link closed / dropped
                    End Try

                    Select Case f.Kind
                        Case FrameType.Stdout
                            EmitLines(_stdoutSplit, f.Payload, isError:=False)
                        Case FrameType.Stderr
                            EmitLines(_stderrSplit, f.Payload, isError:=True)
                        Case FrameType.Exited
                            Dim em As ExitedMessage = ProtocolCodec.Decode(Of ExitedMessage)(f.Payload)
                            FlushPartials()
                            SignalExit(If(em Is Nothing, -1, em.Code))
                            Exit Do
                        Case Else
                            ' forward-compat: ignore unknown frames
                    End Select
                Loop
            Finally
                FlushPartials()
                ' Link dropped without an Exited frame: surface as -1 (idempotent).
                SignalExit(-1)
            End Try
        End Function

        Private Sub EmitLines(splitter As LineSplitter, payload As Byte(), isError As Boolean)
            For Each line In splitter.Push(payload)
                InvokeLine(line, isError)
            Next
        End Sub

        Private Sub FlushPartials()
            Dim a As String = _stdoutSplit.Flush()
            If a IsNot Nothing Then InvokeLine(a, False)
            Dim b As String = _stderrSplit.Flush()
            If b IsNot Nothing Then InvokeLine(b, True)
        End Sub

        Private Sub InvokeLine(line As String, isError As Boolean)
            Try
                If _onLine IsNot Nothing Then _onLine(DateTime.UtcNow, line, isError)
            Catch ex As Exception
                _logger.LogError(ex, "Shim {Id}: onLine callback threw", _instanceId)
            End Try
        End Sub

        Private Sub SignalExit(code As Integer)
            If Interlocked.Exchange(_exitSignalled, 1) <> 0 Then Return
            _exitTcs.TrySetResult(code)
            ' On a deliberate detach the game is intentionally left running and
            ' the link drop is expected — do NOT surface it as an exit, or
            ' HandleProcessExited would tree-kill the live game and crash-restart
            ' it. (_exitTcs is still completed above for any awaiter.)
            If Volatile.Read(_detaching) <> 0 Then Return
            Try
                If _onExited IsNot Nothing Then _onExited(code)
            Catch ex As Exception
                _logger.LogError(ex, "Shim {Id}: onExited callback threw", _instanceId)
            End Try
        End Sub

        Private Async Function SafeWriteAsync(kind As FrameType, payload As Byte(), ct As CancellationToken) As Task
            Dim c = _conn
            If c Is Nothing Then Return
            Try
                Await c.WriteFrameAsync(kind, payload, ct).ConfigureAwait(False)
            Catch ex As Exception
                _logger.LogDebug(ex, "Shim {Id}: write {Kind} failed", _instanceId, kind)
            End Try
        End Function

        Private Async Function ConnectWithRetryAsync(endpoint As String, launchTimeoutMs As Integer, ct As CancellationToken) As Task(Of Stream)
            Dim totalMs As Integer = If(launchTimeoutMs > 0, launchTimeoutMs, 10000)
            Dim deadline As DateTime = DateTime.UtcNow.AddMilliseconds(totalMs)
            Using deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                deadlineCts.CancelAfter(totalMs)
                Do
                    If _shimProc IsNot Nothing AndAlso _shimProc.HasExited Then
                        Throw New IOException($"Shim exited before connect (code {_shimProc.ExitCode})")
                    End If

                    Dim retry As Boolean = False
                    Try
                        Return Await ShimTransport.ConnectAsync(endpoint, deadlineCts.Token).ConfigureAwait(False)
                    Catch ex As OperationCanceledException
                        If ct.IsCancellationRequested Then Throw
                        Throw New TimeoutException($"Timed out connecting to shim endpoint {endpoint}")
                    Catch ex As Exception
                        ' Linux: socket not bound yet (connection refused / not found).
                        ' Windows pipes don't reach here — ConnectAsync waits instead.
                        If DateTime.UtcNow >= deadline Then Throw
                        retry = True
                    End Try

                    If retry Then
                        Await Task.Delay(75, deadlineCts.Token).ConfigureAwait(False)
                    End If
                Loop
            End Using
        End Function

        Private Sub OnShimStdout(sender As Object, e As DataReceivedEventArgs)
            If e.Data IsNot Nothing Then _logger.LogDebug("[shim {Id} out] {Line}", _instanceId, e.Data)
        End Sub

        Private Sub OnShimStderr(sender As Object, e As DataReceivedEventArgs)
            If e.Data IsNot Nothing Then _logger.LogDebug("[shim {Id} err] {Line}", _instanceId, e.Data)
        End Sub

        Private Sub TryKillShim()
            If Not _ownsShim Then Return   ' adopted or detached: never kill the shim
            Try
                If _shimProc IsNot Nothing AndAlso Not _shimProc.HasExited Then
                    _shimProc.Kill(entireProcessTree:=True)
                End If
            Catch
                ' best-effort
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            Try
                If _conn IsNot Nothing Then _conn.Dispose()
            Catch
            End Try
            TryKillShim()
            Try
                If _shimProc IsNot Nothing Then _shimProc.Dispose()
            Catch
            End Try
        End Sub

        ' ---------- endpoint + shim-path resolution ----------

        Private Shared Function MakeEndpoint(instanceId As String, socketDir As String) As String
            Dim safe As String = SanitizeId(instanceId)
            If OperatingSystem.IsWindows() Then
                Return "pipe:powergsm-shim-" & safe
            End If
            Dim dir As String = If(String.IsNullOrEmpty(socketDir),
                                   Path.Combine(Path.GetTempPath(), "powergsm-shims"),
                                   socketDir)
            Try
                Directory.CreateDirectory(dir)
            Catch
            End Try
            Return "unix:" & Path.Combine(dir, safe & ".sock")
        End Function

        Private Shared Function SanitizeId(id As String) As String
            If String.IsNullOrEmpty(id) Then Return "instance"
            Dim sb As New StringBuilder(id.Length)
            For Each ch In id
                If Char.IsLetterOrDigit(ch) OrElse ch = "-"c OrElse ch = "_"c OrElse ch = "."c Then
                    sb.Append(ch)
                Else
                    sb.Append("-"c)
                End If
            Next
            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Resolve the newest deployed shim exe under
        ''' {BaseDir}\GSM.Shim\<version>\GSM.Shim[.exe], falling back to a
        ''' flat GSM.Shim[.exe] next to the node. Sets versionOut to the
        ''' chosen version folder name (or "" for the flat fallback).
        ''' </summary>
        Private Shared Function ResolveShimExePath(ByRef versionOut As String) As String
            versionOut = ""
            Dim baseDir As String = AppContext.BaseDirectory
            Dim exeName As String = If(OperatingSystem.IsWindows(), "GSM.Shim.exe", "GSM.Shim")
            Dim shimRoot As String = Path.Combine(baseDir, "GSM.Shim")

            If Directory.Exists(shimRoot) Then
                Dim best As String = Nothing
                Dim bestVer As Version = Nothing
                Dim bestName As String = ""
                For Each d In Directory.GetDirectories(shimRoot)
                    Dim candidate As String = Path.Combine(d, exeName)
                    If Not File.Exists(candidate) Then Continue For
                    Dim name As String = Path.GetFileName(d)
                    Dim v As Version = Nothing
                    If Version.TryParse(name, v) Then
                        If bestVer Is Nothing OrElse v > bestVer Then
                            bestVer = v
                            best = candidate
                            bestName = name
                        End If
                    ElseIf best Is Nothing Then
                        best = candidate
                        bestName = name
                    End If
                Next
                If best IsNot Nothing Then
                    versionOut = bestName
                    Return best
                End If
            End If

            Return Path.Combine(baseDir, exeName)
        End Function

    End Class

    ''' <summary>
    ''' What a shim reports about itself on a Phase 8-3 rediscovery probe
    ''' (ShimSession.ProbeEndpointAsync). InstanceId is null/empty from a
    ''' pre-8-3 shim that doesn't echo it (such a shim can't be sweep-adopted).
    ''' </summary>
    Friend NotInheritable Class ShimProbeResult
        Public Property InstanceId As String
        Public Property GamePid As Integer
        Public Property GameState As String      ' "none" | "running" | "exited"
        Public Property ShimPid As Integer
        Public Property ShimVersion As String
        Public Property ProtocolVersion As Integer
    End Class

    ''' <summary>
    ''' Splits a byte stream into UTF-8 lines on LF, stripping a trailing CR.
    ''' Buffers across chunks at the byte level so a multi-byte UTF-8 char
    ''' split across two frames is never decoded mid-character.
    ''' </summary>
    Friend NotInheritable Class LineSplitter
        Private ReadOnly _buf As New List(Of Byte)

        Public Function Push(chunk As Byte()) As IEnumerable(Of String)
            Dim lines As New List(Of String)
            If chunk Is Nothing OrElse chunk.Length = 0 Then Return lines
            For Each b In chunk
                If b = 10 Then   ' LF
                    lines.Add(DecodeBuffered())
                    _buf.Clear()
                Else
                    _buf.Add(b)
                End If
            Next
            Return lines
        End Function

        ''' <summary>Decode and clear any buffered tail that has no trailing LF.</summary>
        Public Function Flush() As String
            If _buf.Count = 0 Then Return Nothing
            Dim s As String = DecodeBuffered()
            _buf.Clear()
            Return s
        End Function

        Private Function DecodeBuffered() As String
            Dim n As Integer = _buf.Count
            If n > 0 AndAlso _buf(n - 1) = 13 Then n -= 1   ' strip trailing CR
            If n <= 0 Then Return ""
            Return Encoding.UTF8.GetString(_buf.ToArray(), 0, n)
        End Function
    End Class

End Namespace
