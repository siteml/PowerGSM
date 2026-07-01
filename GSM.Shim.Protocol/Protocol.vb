' ============================================================
'  GSM.Shim.Protocol — wire protocol definitions
'
'  The Node<->shim link is a length-prefixed binary frame stream:
'
'      [ payloadLength : UInt32 little-endian ]
'      [ frameType     : Byte                 ]
'      [ payload       : payloadLength bytes  ]
'
'  Control frames (Hello, HelloAck, Spawn, Stop, Exited, ...) carry a
'  JSON payload (System.Text.Json) so the protocol is append-only:
'  a newer sender can add fields and an older reader ignores them
'  (Phase 8 decision 5 — versioned, never-remove). Stream frames
'  (Stdin/Stdout/Stderr) carry raw bytes as the payload, no JSON.
'
'  This assembly is referenced by BOTH GSM.Node and GSM.Shim, so the
'  format lives in exactly one place.
' ============================================================
Imports System
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization

Namespace GSM.Shim.Protocol

    ''' <summary>Solution-wide protocol constants.</summary>
    Public Module ProtocolConstants

        ''' <summary>
        ''' Current protocol version this build speaks. The handshake
        ''' negotiates down to Math.Min(node, shim) so a new Node can
        ''' always talk to an older shim and vice-versa. Bump ONLY when
        ''' adding capabilities; never remove or repurpose a frame type
        ''' or a field.
        ''' </summary>
        Public Const ProtocolVersion As Integer = 1

        ''' <summary>
        ''' Sanity cap on a single frame's payload so a corrupt or
        ''' hostile length prefix can't drive an unbounded allocation.
        ''' Stream frames are chunked well below this; control payloads
        ''' are tiny.
        ''' </summary>
        Public Const MaxFrameBytes As Integer = 16 * 1024 * 1024

    End Module

    ''' <summary>
    ''' Frame type tag (the single byte after the length prefix).
    ''' Values are explicit and frozen — append new ones, never renumber.
    ''' </summary>
    Public Enum FrameType As Byte
        ' --- handshake ---
        Hello = 1          ' node -> shim : HelloMessage
        HelloAck = 2       ' shim -> node : HelloAckMessage
        ' --- node -> shim control ---
        Spawn = 3          ' SpawnSpec (start the game)
        Stdin = 4          ' raw bytes -> game stdin
        StopGame = 5       ' StopMessage (graceful/forceful stop)
        Detach = 6         ' node leaving cleanly; keep the game running
        Shutdown = 7       ' kill the game and exit the shim
        ' --- shim -> node stream/notifications ---
        Stdout = 8         ' raw bytes from game stdout
        Stderr = 9         ' raw bytes from game stderr
        Exited = 10        ' ExitedMessage (game process ended)
        Heartbeat = 11     ' liveness ping (either direction)
        SpawnAck = 12      ' shim -> node : SpawnAckMessage (reply to Spawn, carries the game pid)
    End Enum

    ''' <summary>A decoded frame: its type tag and raw payload bytes.</summary>
    Public Structure Frame
        Public ReadOnly Kind As FrameType
        Public ReadOnly Payload As Byte()

        Public Sub New(kind As FrameType, payload As Byte())
            Me.Kind = kind
            Me.Payload = If(payload, Array.Empty(Of Byte)())
        End Sub
    End Structure

    ''' <summary>Raised on a malformed frame or an unexpected handshake step.</summary>
    Public Class ProtocolException
        Inherits Exception
        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ' ---------- control-frame payloads (JSON) ----------

    ''' <summary>node -> shim, first frame.</summary>
    Public Class HelloMessage
        Public Property ProtocolVersion As Integer
        Public Property Role As String   ' "node"
    End Class

    ''' <summary>shim -> node, reply to Hello.</summary>
    Public Class HelloAckMessage
        Public Property ProtocolVersion As Integer   ' negotiated (min of both)
        Public Property ShimVersion As String        ' shim build, for the side-by-side scheme
        Public Property GamePid As Integer           ' -1 when no game is running yet
        Public Property GameState As String          ' "none" | "running" | "exited"
        Public Property ShimPid As Integer           ' the shim's own PID (adoption anchor); 0 if unknown
        Public Property InstanceId As String         ' the id the shim was launched with (--instance-id); lets a Node rediscover/adopt a live shim without node.db (Phase 8-3). Null/empty from older shims.
        Public Property LogFilePaths As System.Collections.Generic.List(Of String)  ' echoed from the Spawn's SpawnSpec so a node.db-less adopt can recover where to tail (Phase 8-3); null/empty for stdout-streamed games or pre-8-3 shims
    End Class

    ''' <summary>
    ''' Everything the shim needs to launch the game, mirroring what the
    ''' Node's ProcessManager already resolves. Expanded in slice 1b; the
    ''' shape is intentionally additive.
    ''' </summary>
    Public Class SpawnSpec
        Public Property ExePath As String
        ''' <summary>Single Win32-quoted argument string, as ProcessManager builds psi.Arguments.</summary>
        Public Property Arguments As String
        Public Property WorkingDirectory As String
        Public Property Environment As System.Collections.Generic.Dictionary(Of String, String)
        ''' <summary>"StdoutCapture" | "HiddenConsoleDirect" | "HiddenConsoleWrapped".</summary>
        Public Property Strategy As String
        ''' <summary>
        ''' Absolute log-file paths the Node tails for this game (Phase 8-3).
        ''' The shim does NOT tail them — it just remembers them and echoes them
        ''' back in HelloAck, so a Node that adopts WITHOUT node.db (lost or
        ''' corrupt) can recover where to tail. Null/empty for stdout-streamed
        ''' (Strategy A) games.
        ''' </summary>
        Public Property LogFilePaths As System.Collections.Generic.List(Of String)
    End Class

    ''' <summary>node -> shim, stop request.</summary>
    Public Class StopMessage
        ''' <summary>"ctrlc" | "sigterm" | "stdin-line" | "kill".</summary>
        Public Property Kind As String
        Public Property TimeoutMs As Integer
        ''' <summary>For Kind = "stdin-line": the line to write (e.g. Factorio /quit).</summary>
        Public Property StdinLine As String
    End Class

    ''' <summary>shim -> node, the game process ended.</summary>
    Public Class ExitedMessage
        Public Property Code As Integer
    End Class

    ''' <summary>shim -> node, reply to Spawn with the launched game's PID (or the failure reason).</summary>
    Public Class SpawnAckMessage
        Public Property GamePid As Integer
        Public Property Success As Boolean
        Public Property ErrorMessage As String
    End Class

    ''' <summary>JSON encode/decode for control-frame payloads.</summary>
    Public Module ProtocolCodec

        Private ReadOnly _opts As JsonSerializerOptions = BuildOptions()

        Private Function BuildOptions() As JsonSerializerOptions
            Dim o As New JsonSerializerOptions()
            o.PropertyNameCaseInsensitive = True
            o.DefaultIgnoreCondition = JsonIgnoreCondition.Never
            Return o
        End Function

        Public Function Encode(Of T)(value As T) As Byte()
            Return JsonSerializer.SerializeToUtf8Bytes(value, _opts)
        End Function

        Public Function Decode(Of T)(payload As Byte()) As T
            If payload Is Nothing OrElse payload.Length = 0 Then
                Return Nothing
            End If
            Return JsonSerializer.Deserialize(Of T)(Encoding.UTF8.GetString(payload), _opts)
        End Function

    End Module

End Namespace
