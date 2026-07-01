' ============================================================
'  GSM.Shim — Supervisor (Phase 8-1, slice 3a)
'
'  Owns the one game process under this shim and the *current* Node
'  connection, which can change over the game's lifetime: when the Node
'  drops or detaches (a Node restart/update), the game and its stdout/
'  stderr pumps keep running and the supervisor loops back to accept the
'  next (re)connecting Node, replaying recently-buffered output so the new
'  Node catches up. The shim only exits on Shutdown (kill + exit) or when
'  the game itself ends. This is what makes a Node restart non-fatal to a
'  running game.
'
'  Per connection, after the Hello/HelloAck handshake (which reports the
'  live game pid + state so an adopting Node learns what it reconnected
'  to), it serves control frames:
'
'      Spawn      -> native-spawn the game, reply SpawnAck(pid), start
'                    stdout/stderr pumps + an exit watcher. On a reconnect
'                    where the game is already running it just re-acks.
'      Stdin      -> write bytes to the game's stdin
'      StopGame   -> terminate (basic; graceful CTRL_C is slice 5) or
'                    write a stdin line
'      Shutdown   -> kill the game and exit the shim
'      Detach     -> end this connection, leave the game running
'
'  Output flows back as Stdout/Stderr frames and is mirrored into a
'  bounded OutputRing; Exited(code) is sent when the game ends (and, if a
'  Node connects after the game already exited, replayed right after the
'  handshake). The send semaphore serialises ring-append + live-write in
'  the pumps against snapshot + replay on a connection swap, so no chunk
'  is lost or duplicated across a reconnect.
' ============================================================
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Shim.Protocol

Friend NotInheritable Class Supervisor
    Implements IDisposable

    Private ReadOnly _instanceId As String
    Private ReadOnly _shimVersion As String
    Private _logFilePaths As List(Of String)   ' Phase 8-3: the Node's tail paths, from the Spawn's SpawnSpec; echoed in HelloAck so a node.db-less adopt can recover where to tail
    Private ReadOnly _ring As New OutputRing(256 * 1024)
    Private ReadOnly _sendSem As New SemaphoreSlim(1, 1)

    ' The current Node connection, or Nothing between connections. Mutated
    ' only under _sendSem so the pumps never write to a half-swapped conn.
    Private _conn As FrameConnection

    Private _game As IGameProcess
    Private _pumpCts As CancellationTokenSource
    Private _stdoutPump As Task
    Private _stderrPump As Task
    Private _exitWatch As Task
    Private _gameDone As Boolean
    Private _exitCode As Integer = -1
    Private _shutdownRequested As Boolean
    Private _disposed As Boolean

    Public Sub New(instanceId As String, shimVersion As String)
        _instanceId = instanceId
        _shimVersion = shimVersion
    End Sub

    ''' <summary>
    ''' True once a Shutdown was received or the game has exited — the
    ''' accept loop then stops accepting and the shim exits.
    ''' </summary>
    Public ReadOnly Property ShouldExit As Boolean
        Get
            Return _shutdownRequested OrElse _gameDone
        End Get
    End Property

    ''' <summary>
    ''' Full supervisor lifecycle (normal mode). Accept a Node connection,
    ''' serve it, and on a clean drop/detach — game still alive, no Shutdown —
    ''' loop back and accept the next (re)connecting Node, replaying buffered
    ''' output. Exits only on Shutdown or game exit.
    ''' </summary>
    Public Shared Async Function RunAcceptLoopAsync(listener As IShimListener, shimVersion As String,
                                                    instanceId As String, ct As CancellationToken) As Task
        Using sup As New Supervisor(instanceId, shimVersion)
            Do
                Dim stream As Stream
                Try
                    stream = Await listener.AcceptAsync(ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit Do
                End Try

                Using conn As New FrameConnection(stream)
                    Console.Error.WriteLine($"GSM.Shim: connection accepted, instance {instanceId} (game {sup.GameStateString()})")
                    Await sup.ServeConnectionAsync(conn, ct).ConfigureAwait(False)
                End Using

                If sup.ShouldExit Then Exit Do
            Loop
        End Using
    End Function

    ''' <summary>
    ''' Single-connection convenience (used by the self-test): handshake +
    ''' replay + serve one connection, no accept loop.
    ''' </summary>
    Public Shared Async Function RunServerAsync(conn As FrameConnection, shimVersion As String,
                                                instanceId As String, ct As CancellationToken) As Task
        Using sup As New Supervisor(instanceId, shimVersion)
            Await sup.ServeConnectionAsync(conn, ct).ConfigureAwait(False)
        End Using
    End Function

    ''' <summary>
    ''' Handshake (reporting the current game pid/state), replay the output
    ''' ring to this connection, then serve control frames until the link
    ''' drops / Detach / Shutdown. The game + pumps persist across this; only
    ''' the current connection changes.
    ''' </summary>
    Public Async Function ServeConnectionAsync(conn As FrameConnection, ct As CancellationToken) As Task
        Dim pid As Integer = If(_game Is Nothing, -1, _game.Pid)
        Await ShimHandshake.AcceptAsync(conn, _shimVersion, pid, GameStateString(), _instanceId, _logFilePaths, ct).ConfigureAwait(False)

        ' Install this connection and replay buffered output atomically with
        ' respect to the pumps. Holding the send semaphore across snapshot +
        ' replay + the _conn swap means a pump iteration (which takes the same
        ' semaphore around ring-append + live-write) can't interleave, so the
        ' new Node sees each buffered chunk exactly once and live output picks
        ' up cleanly after the replay.
        Await _sendSem.WaitAsync(ct).ConfigureAwait(False)
        Try
            For Each it In _ring.Snapshot()
                Try
                    Await conn.WriteFrameAsync(it.Kind, it.Data, ct).ConfigureAwait(False)
                Catch
                    Exit For   ' the new link is already broken; bail the replay
                End Try
            Next
            _conn = conn
        Finally
            _sendSem.Release()
        End Try

        ' If the game already ended (it exited while no Node was connected),
        ' tell this freshly-connected Node now so it can finalise the instance.
        If _gameDone Then
            Await SendExitedAsync(_exitCode).ConfigureAwait(False)
        End If

        Await ServeAsync(conn, ct).ConfigureAwait(False)

        ' Connection ending (drop / Detach / Shutdown): drop our reference to
        ' it but keep the game + pumps alive for the next Node.
        Await _sendSem.WaitAsync(CancellationToken.None).ConfigureAwait(False)
        Try
            If _conn Is conn Then _conn = Nothing
        Finally
            _sendSem.Release()
        End Try
    End Function

    Private Async Function ServeAsync(conn As FrameConnection, ct As CancellationToken) As Task
        Do
            Dim f As Frame
            Try
                f = Await conn.ReadFrameAsync(ct).ConfigureAwait(False)
            Catch ex As EndOfStreamException
                Exit Do
            Catch ex As IOException
                Exit Do
            End Try

            Select Case f.Kind
                Case FrameType.Spawn
                    Await HandleSpawnAsync(f, conn, ct).ConfigureAwait(False)
                Case FrameType.Stdin
                    Await HandleStdinAsync(f, ct).ConfigureAwait(False)
                Case FrameType.StopGame
                    Await HandleStopAsync(f, ct).ConfigureAwait(False)
                Case FrameType.Shutdown
                    _shutdownRequested = True
                    KillGame()
                    Exit Do
                Case FrameType.Detach
                    Exit Do   ' leave the game running; just end this connection
                Case FrameType.Heartbeat
                    ' liveness only
                Case Else
                    ' forward-compat: ignore unknown frames
            End Select
        Loop
    End Function

    Private Async Function HandleSpawnAsync(f As Frame, conn As FrameConnection, ct As CancellationToken) As Task
        If _game IsNot Nothing Then
            ' Game already running (e.g. a reconnecting Node re-sent Spawn):
            ' re-ack with the live pid; never spawn a second game.
            Await SendSpawnAckOnAsync(conn, _game.Pid, True, Nothing, ct).ConfigureAwait(False)
            Return
        End If

        Dim spec As SpawnSpec = ProtocolCodec.Decode(Of SpawnSpec)(f.Payload)
        ' Phase 8-3: remember the Node's tail paths so a future adopting Node
        ' that has lost its node.db can recover them from our HelloAck.
        _logFilePaths = spec.LogFilePaths
        Dim proc As IGameProcess = Nothing
        Dim spawnError As String = Nothing
        Try
            proc = NativeSpawn.Spawn(spec)
        Catch ex As Exception
            spawnError = ex.Message
        End Try

        If proc Is Nothing Then
            Await SendSpawnAckOnAsync(conn, -1, False, If(spawnError, "spawn failed"), ct).ConfigureAwait(False)
            Return
        End If

        _game = proc
        _gameDone = False
        _exitCode = -1

        ' Ack BEFORE starting the pumps so SpawnAck is the first frame the
        ' Node sees after Spawn (no Stdout can race ahead of it). Written
        ' directly on this connection — the pumps don't exist yet.
        Await SendSpawnAckOnAsync(conn, proc.Pid, True, Nothing, ct).ConfigureAwait(False)

        _pumpCts = New CancellationTokenSource()
        ' Strategy B/C (hidden console) redirect nothing — the game writes to
        ' its own console and the Node tails the log file, so there are no
        ' parent pipe ends to pump. Only start pumps when streams exist
        ' (Strategy A redirected stdio).
        If proc.StdOut IsNot Nothing Then _stdoutPump = PumpAsync(proc.StdOut, FrameType.Stdout, _pumpCts.Token)
        If proc.StdErr IsNot Nothing Then _stderrPump = PumpAsync(proc.StdErr, FrameType.Stderr, _pumpCts.Token)
        _exitWatch = WatchExitAsync(proc)
    End Function

    Private Async Function HandleStdinAsync(f As Frame, ct As CancellationToken) As Task
        Dim g = _game
        If g Is Nothing Then Return
        Try
            Await g.StdIn.WriteAsync(f.Payload, 0, f.Payload.Length, ct).ConfigureAwait(False)
            Await g.StdIn.FlushAsync(ct).ConfigureAwait(False)
        Catch
            ' stdin closed / game gone
        End Try
    End Function

    Private Async Function HandleStopAsync(f As Frame, ct As CancellationToken) As Task
        Dim g = _game
        If g Is Nothing Then Return
        Dim msg As StopMessage = ProtocolCodec.Decode(Of StopMessage)(f.Payload)
        Dim kind As String = "kill"
        If msg IsNot Nothing AndAlso Not String.IsNullOrEmpty(msg.Kind) Then kind = msg.Kind.ToLowerInvariant()

        Select Case kind
            Case "stdin-line"
                Try
                    Dim line As String = If(msg.StdinLine, "")
                    Dim bytes As Byte() = Encoding.UTF8.GetBytes(line & Environment.NewLine)
                    Await g.StdIn.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(False)
                    Await g.StdIn.FlushAsync(ct).ConfigureAwait(False)
                Catch
                End Try
            Case Else
                ' basic terminate. Graceful CTRL_C / SIGTERM is slice 5.
                KillGame()
        End Select
    End Function

    Private Async Function PumpAsync(stream As Stream, kind As FrameType, ct As CancellationToken) As Task
        Dim buf(8191) As Byte
        Do
            Dim n As Integer
            Try
                n = Await Task.Run(Function() stream.Read(buf, 0, buf.Length)).ConfigureAwait(False)
            Catch
                Return   ' stream disposed / pipe broken
            End Try
            If n <= 0 Then Return   ' EOF

            Dim chunk(n - 1) As Byte
            Array.Copy(buf, chunk, n)

            ' ring-append + live-write under the send semaphore so a concurrent
            ' connection swap (snapshot + replay) can't split this chunk across
            ' the old and new conn.
            Try
                Await _sendSem.WaitAsync(ct).ConfigureAwait(False)
            Catch
                Return   ' pumps cancelled (game exiting)
            End Try
            Try
                _ring.Append(kind, chunk)
                Dim c = _conn
                If c IsNot Nothing Then
                    Try
                        Await c.WriteFrameAsync(kind, chunk, ct).ConfigureAwait(False)
                    Catch
                        ' link dropped mid-write; the serve loop will notice and
                        ' detach. The chunk is in the ring for replay.
                    End Try
                End If
            Finally
                _sendSem.Release()
            End Try
        Loop
    End Function

    Private Async Function WatchExitAsync(proc As IGameProcess) As Task
        Dim code As Integer = -1
        Try
            code = Await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(False)
        Catch
            code = -1
        End Try

        _exitCode = code
        _gameDone = True

        ' Drain the pumps to EOF before sending Exited so a *graceful* shutdown's
        ' late-stage output (world save, "shutdown complete", ...) reaches the
        ' Node instead of being dropped. The game closing stdout yields EOF, so
        ' the pumps return on their own. Bound the wait: a *killed* game can
        ' leave a child holding the stdout pipe open (no EOF), which would
        ' otherwise stall Exited forever — on timeout we fall back to cancelling
        ' the pumps and dropping the unread tail (the old hard-kill behaviour).
        Await DrainPumpsAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(False)
        StopPumps()
        Await SendExitedAsync(code).ConfigureAwait(False)
    End Function

    ''' <summary>
    ''' Wait for the stdout/stderr pumps to finish naturally — each returns on
    ''' EOF once the game closes its handle — bounded by <paramref name="timeout"/>.
    ''' Returns when both have completed or the timeout elapses. No-op for
    ''' Strategy B/C (no pumps; the Node tails the log file directly).
    ''' </summary>
    Private Async Function DrainPumpsAsync(timeout As TimeSpan) As Task
        Dim pumps As New List(Of Task)
        If _stdoutPump IsNot Nothing Then pumps.Add(_stdoutPump)
        If _stderrPump IsNot Nothing Then pumps.Add(_stderrPump)
        If pumps.Count = 0 Then Return

        Dim all As Task = Task.WhenAll(pumps)
        Dim finished = Await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(False)
        If finished IsNot all Then
            Console.Error.WriteLine($"GSM.Shim: pump drain timed out after {timeout.TotalSeconds:0.#}s; dropping unread tail")
        End If
    End Function

    Private Async Function SendSpawnAckOnAsync(conn As FrameConnection, pid As Integer, success As Boolean,
                                               err As String, ct As CancellationToken) As Task
        Dim ack As New SpawnAckMessage With {.GamePid = pid, .Success = success, .ErrorMessage = err}
        Try
            Await conn.WriteFrameAsync(FrameType.SpawnAck, ProtocolCodec.Encode(ack), ct).ConfigureAwait(False)
        Catch
        End Try
    End Function

    ''' <summary>
    ''' Send Exited(code) to the current connection (if any), serialised with
    ''' the pumps. No _gameDone guard: this is called both when the game ends
    ''' and again after a post-exit reconnect's handshake, so each Node that
    ''' connects after the game died still learns the exit. _gameDone is set by
    ''' WatchExitAsync.
    ''' </summary>
    Private Async Function SendExitedAsync(code As Integer) As Task
        Await _sendSem.WaitAsync(CancellationToken.None).ConfigureAwait(False)
        Try
            Dim c = _conn
            If c IsNot Nothing Then
                Try
                    Await c.WriteFrameAsync(FrameType.Exited,
                        ProtocolCodec.Encode(New ExitedMessage With {.Code = code}),
                        CancellationToken.None).ConfigureAwait(False)
                Catch
                End Try
            End If
        Finally
            _sendSem.Release()
        End Try
    End Function

    ''' <summary>"none" before a spawn, "running" while alive, "exited" after.</summary>
    Private Function GameStateString() As String
        If _game Is Nothing Then Return "none"
        If _gameDone Then Return "exited"
        Return "running"
    End Function

    Private Sub KillGame()
        Dim g = _game
        If g Is Nothing Then Return
        Try
            g.Kill()
        Catch
        End Try
    End Sub

    Private Sub StopPumps()
        Try
            If _pumpCts IsNot Nothing Then _pumpCts.Cancel()
        Catch
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        StopPumps()
        Try
            If _game IsNot Nothing Then _game.Dispose()
        Catch
        End Try
        Try
            If _pumpCts IsNot Nothing Then _pumpCts.Dispose()
        Catch
        End Try
        Try
            _sendSem.Dispose()
        Catch
        End Try
    End Sub

End Class

''' <summary>Bounded ring of recent output frames, for replay to a reconnecting Node.</summary>
Friend NotInheritable Class OutputRing
    Private ReadOnly _cap As Integer
    Private ReadOnly _items As New Queue(Of RingItem)
    Private _bytes As Integer
    Private ReadOnly _lock As New Object()

    Public Sub New(capBytes As Integer)
        _cap = capBytes
    End Sub

    Public Sub Append(kind As FrameType, data As Byte())
        If data Is Nothing OrElse data.Length = 0 Then Return
        SyncLock _lock
            _items.Enqueue(New RingItem With {.Kind = kind, .Data = data})
            _bytes += data.Length
            While _bytes > _cap AndAlso _items.Count > 0
                Dim old As RingItem = _items.Dequeue()
                _bytes -= old.Data.Length
            End While
        End SyncLock
    End Sub

    Public Function Snapshot() As List(Of RingItem)
        SyncLock _lock
            Return New List(Of RingItem)(_items)
        End SyncLock
    End Function
End Class

Friend NotInheritable Class RingItem
    Public Kind As FrameType
    Public Data As Byte()
End Class
