' ============================================================
'  GSM.Shim.Protocol — framed connection + handshake
'
'  FrameConnection wraps a duplex byte Stream (named pipe on Windows,
'  Unix domain socket on Linux) and reads/writes length-prefixed frames.
'  Writes are serialized through a semaphore so stdout/stderr pumps and
'  control replies can't interleave on the wire.
'
'  ShimHandshake performs the Hello/HelloAck exchange and returns the
'  negotiated protocol version (Math.Min of the two sides).
' ============================================================
Imports System
Imports System.Buffers.Binary
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks

Namespace GSM.Shim.Protocol

    Public NotInheritable Class FrameConnection
        Implements IDisposable

        Private Const HeaderBytes As Integer = 5   ' UInt32 length + 1 type byte

        Private ReadOnly _stream As Stream
        Private ReadOnly _ownsStream As Boolean
        Private ReadOnly _writeLock As New SemaphoreSlim(1, 1)
        Private _disposed As Boolean

        Public Sub New(stream As Stream, Optional ownsStream As Boolean = True)
            If stream Is Nothing Then Throw New ArgumentNullException(NameOf(stream))
            _stream = stream
            _ownsStream = ownsStream
        End Sub

        ''' <summary>Write a single frame. Thread-safe against concurrent writers.</summary>
        Public Async Function WriteFrameAsync(kind As FrameType, payload As Byte(), ct As CancellationToken) As Task
            If payload Is Nothing Then payload = Array.Empty(Of Byte)()
            If payload.Length > ProtocolConstants.MaxFrameBytes Then
                Throw New ProtocolException($"Outbound frame too large: {payload.Length} bytes")
            End If

            Dim header(HeaderBytes - 1) As Byte
            Dim len As Integer = payload.Length
            header(0) = CByte(len And &HFF)
            header(1) = CByte((len >> 8) And &HFF)
            header(2) = CByte((len >> 16) And &HFF)
            header(3) = CByte((len >> 24) And &HFF)
            header(4) = CByte(kind)

            Await _writeLock.WaitAsync(ct).ConfigureAwait(False)
            Try
                Await _stream.WriteAsync(header, 0, HeaderBytes, ct).ConfigureAwait(False)
                If payload.Length > 0 Then
                    Await _stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(False)
                End If
                Await _stream.FlushAsync(ct).ConfigureAwait(False)
            Finally
                _writeLock.Release()
            End Try
        End Function

        ''' <summary>
        ''' Read a single frame. Throws EndOfStreamException when the peer
        ''' closes cleanly mid-stream (ReadExactlyAsync's contract), which
        ''' callers treat as "disconnected".
        ''' </summary>
        Public Async Function ReadFrameAsync(ct As CancellationToken) As Task(Of Frame)
            Dim header(HeaderBytes - 1) As Byte
            Await _stream.ReadExactlyAsync(header, 0, HeaderBytes, ct).ConfigureAwait(False)

            Dim length As UInteger = CUInt(header(0)) Or (CUInt(header(1)) << 8) Or (CUInt(header(2)) << 16) Or (CUInt(header(3)) << 24)
            Dim kind As FrameType = CType(header(4), FrameType)

            If length = 0UI Then
                Return New Frame(kind, Array.Empty(Of Byte)())
            End If
            If length > CUInt(ProtocolConstants.MaxFrameBytes) Then
                Throw New ProtocolException($"Inbound frame too large: {length} bytes")
            End If

            Dim payload(CInt(length) - 1) As Byte
            Await _stream.ReadExactlyAsync(payload, 0, CInt(length), ct).ConfigureAwait(False)
            Return New Frame(kind, payload)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            _writeLock.Dispose()
            If _ownsStream Then
                Try
                    _stream.Dispose()
                Catch
                    ' best-effort close
                End Try
            End If
        End Sub

    End Class

    ''' <summary>
    ''' Hello/HelloAck handshake. The shim accepts (reads Hello, replies
    ''' HelloAck); the node connects (sends Hello, reads HelloAck). Both
    ''' adopt the negotiated min version.
    ''' </summary>
    Public Module ShimHandshake

        ''' <summary>
        ''' Shim side. Reads the node's Hello and replies with HelloAck
        ''' carrying the supplied shim version, game state, and instance
        ''' id. Returns the negotiated protocol version.
        ''' </summary>
        Public Async Function AcceptAsync(conn As FrameConnection,
                                          shimVersion As String,
                                          gamePid As Integer,
                                          gameState As String,
                                          instanceId As String,
                                          logFilePaths As System.Collections.Generic.List(Of String),
                                          ct As CancellationToken) As Task(Of Integer)
            Dim f As Frame = Await conn.ReadFrameAsync(ct).ConfigureAwait(False)
            If f.Kind <> FrameType.Hello Then
                Throw New ProtocolException($"Expected Hello, got {f.Kind}")
            End If

            Dim hello As HelloMessage = ProtocolCodec.Decode(Of HelloMessage)(f.Payload)
            Dim peerVersion As Integer = If(hello Is Nothing, ProtocolConstants.ProtocolVersion, hello.ProtocolVersion)
            Dim negotiated As Integer = Math.Min(ProtocolConstants.ProtocolVersion, peerVersion)

            Dim ack As New HelloAckMessage With {
                .ProtocolVersion = negotiated,
                .ShimVersion = shimVersion,
                .GamePid = gamePid,
                .GameState = gameState,
                .ShimPid = Environment.ProcessId,
                .InstanceId = instanceId,
                .LogFilePaths = logFilePaths
            }
            Await conn.WriteFrameAsync(FrameType.HelloAck, ProtocolCodec.Encode(ack), ct).ConfigureAwait(False)
            Return negotiated
        End Function

        ''' <summary>
        ''' Shim side, ring-cursor aware (v2 additive). Same handshake as
        ''' AcceptAsync, but negotiates the ring replay window: the node's
        ''' Hello.ReplayAfterSeq is clamped into the ring's available range
        ''' [ringFirstSeq, ringNextSeq] and the resulting ReplayStartSeq is
        ''' both sent in the ack and returned so the caller replays exactly
        ''' the frames the node has not consumed. Callers pass the ring's
        ''' CURRENT bounds (hold the send lock so they can't move before
        ''' the replay).
        ''' </summary>
        Public Async Function AcceptExAsync(conn As FrameConnection,
                                            shimVersion As String,
                                            gamePid As Integer,
                                            gameState As String,
                                            instanceId As String,
                                            logFilePaths As System.Collections.Generic.List(Of String),
                                            ringFirstSeq As Long,
                                            ringNextSeq As Long,
                                            ct As CancellationToken) As Task(Of HandshakeAccept)
            Dim f As Frame = Await conn.ReadFrameAsync(ct).ConfigureAwait(False)
            If f.Kind <> FrameType.Hello Then
                Throw New ProtocolException($"Expected Hello, got {f.Kind}")
            End If

            Dim hello As HelloMessage = ProtocolCodec.Decode(Of HelloMessage)(f.Payload)
            Dim peerVersion As Integer = If(hello Is Nothing, ProtocolConstants.ProtocolVersion, hello.ProtocolVersion)
            Dim negotiated As Integer = Math.Min(ProtocolConstants.ProtocolVersion, peerVersion)

            Dim replayAfter As Long = If(hello Is Nothing, 0L, hello.ReplayAfterSeq)
            ' First frame to replay: everything after the node's cursor that
            ' is still in the ring. Cursor behind the ring (trimmed while the
            ' node was away) -> ringFirstSeq (partial history, as today).
            ' Cursor at/past the end (or empty ring) -> ringNextSeq
            ' (nothing to replay; live frames start there).
            Dim replayStart As Long = Math.Max(ringFirstSeq, replayAfter + 1L)
            If replayStart > ringNextSeq Then replayStart = ringNextSeq

            Dim ack As New HelloAckMessage With {
                .ProtocolVersion = negotiated,
                .ShimVersion = shimVersion,
                .GamePid = gamePid,
                .GameState = gameState,
                .ShimPid = Environment.ProcessId,
                .InstanceId = instanceId,
                .LogFilePaths = logFilePaths,
                .ReplayStartSeq = replayStart
            }
            Await conn.WriteFrameAsync(FrameType.HelloAck, ProtocolCodec.Encode(ack), ct).ConfigureAwait(False)
            Return New HandshakeAccept With {.NegotiatedVersion = negotiated, .ReplayStartSeq = replayStart}
        End Function

        ''' <summary>
        ''' Node side. Sends Hello and reads the shim's HelloAck. Returns
        ''' the ack (negotiated version + reported game state).
        ''' </summary>
        Public Async Function ConnectAsync(conn As FrameConnection, ct As CancellationToken,
                                           Optional replayAfterSeq As Long = 0) As Task(Of HelloAckMessage)
            Dim hello As New HelloMessage With {
                .ProtocolVersion = ProtocolConstants.ProtocolVersion,
                .Role = "node",
                .ReplayAfterSeq = replayAfterSeq
            }
            Await conn.WriteFrameAsync(FrameType.Hello, ProtocolCodec.Encode(hello), ct).ConfigureAwait(False)

            Dim f As Frame = Await conn.ReadFrameAsync(ct).ConfigureAwait(False)
            If f.Kind <> FrameType.HelloAck Then
                Throw New ProtocolException($"Expected HelloAck, got {f.Kind}")
            End If
            Return ProtocolCodec.Decode(Of HelloAckMessage)(f.Payload)
        End Function

    End Module

    ''' <summary>Result of ShimHandshake.AcceptExAsync.</summary>
    Public Class HandshakeAccept
        Public Property NegotiatedVersion As Integer
        ''' <summary>Seq of the first ring frame to replay to this node
        ''' (= what the ack's ReplayStartSeq told it).</summary>
        Public Property ReplayStartSeq As Long
    End Class

End Namespace