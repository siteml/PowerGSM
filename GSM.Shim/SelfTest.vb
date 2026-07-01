' ============================================================
'  GSM.Shim — self-test harness (`GSM.Shim --self-test`)
'
'  Spins the real listener + a real client over an actual named pipe
'  (Windows) / Unix domain socket (Linux). The server side runs the real
'  Supervisor (handshake + serve loop), so the test exercises the actual
'  spawn/stream/stop code path, not a stub.
'
'  Checks:
'    1. Hello/HelloAck handshake (proto match, state "none").
'    2. Spawn a dummy (cmd.exe on Windows, /bin/sh on Linux) -> SpawnAck(
'       success, pid>0) -> a Stdout frame carrying a marker -> StopGame(kill)
'       -> Exited. Exercises the real native spawn on whichever OS runs it.
'  No Node and no real game involved. Disposable scaffolding.
' ============================================================
Imports System
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Shim.Protocol

Friend Module SelfTest

    Private Const Marker As String = "SHIM_SELFTEST_MARKER"

    Public Async Function RunAsync() As Task(Of Integer)
        Dim endpoint As String = MakeTempEndpoint()
        Console.WriteLine($"[self-test] OS={GetOsName()}  endpoint={endpoint}")

        Dim ok As Boolean = False
        Using listener As IShimListener = ShimTransport.CreateListener(endpoint)
            ' Server (shim role): accept one connection, run the real Supervisor.
            Dim serverTask As Task = ServerOnceAsync(listener)

            ' Client (node role): connect + handshake.
            Dim client As Stream = Await ShimTransport.ConnectAsync(endpoint, CancellationToken.None).ConfigureAwait(False)
            Using conn As New FrameConnection(client)
                Dim ack As HelloAckMessage = Await ShimHandshake.ConnectAsync(conn, CancellationToken.None).ConfigureAwait(False)
                Console.WriteLine($"[self-test] HelloAck: proto=v{ack.ProtocolVersion} shim={ack.ShimVersion} gamePid={ack.GamePid} state={ack.GameState}")
                Dim handshakeOk As Boolean = (ack.ProtocolVersion = ProtocolConstants.ProtocolVersion) AndAlso
                                             String.Equals(ack.GameState, "none", StringComparison.Ordinal)

                Dim spawnOk As Boolean = Await RunSpawnRoundTripAsync(conn, CancellationToken.None).ConfigureAwait(False)

                ' Tell the supervisor to exit so its serve loop returns.
                Await conn.WriteFrameAsync(FrameType.Shutdown, Nothing, CancellationToken.None).ConfigureAwait(False)
                ok = handshakeOk AndAlso spawnOk
            End Using

            Await serverTask.ConfigureAwait(False)
        End Using

        Console.WriteLine(If(ok, "[self-test] PASS", "[self-test] FAIL"))
        Return If(ok, 0, 1)
    End Function

    ''' <summary>
    ''' Drives a real spawn: start a cmd.exe dummy that prints a marker then
    ''' stays alive (pause), assert SpawnAck + a marker-bearing Stdout frame,
    ''' then StopGame(kill) and assert Exited.
    ''' </summary>
    Private Async Function RunSpawnRoundTripAsync(conn As FrameConnection, ct As CancellationToken) As Task(Of Boolean)
        Dim spec As SpawnSpec = BuildDummySpec()
        Await conn.WriteFrameAsync(FrameType.Spawn, ProtocolCodec.Encode(spec), ct).ConfigureAwait(False)

        ' First frame after Spawn must be SpawnAck.
        Dim ackFrame As Frame = Await conn.ReadFrameAsync(ct).ConfigureAwait(False)
        If ackFrame.Kind <> FrameType.SpawnAck Then
            Console.WriteLine($"[self-test] spawn: expected SpawnAck, got {ackFrame.Kind}")
            Return False
        End If
        Dim ack As SpawnAckMessage = ProtocolCodec.Decode(Of SpawnAckMessage)(ackFrame.Payload)
        Console.WriteLine($"[self-test] SpawnAck: success={ack.Success} gamePid={ack.GamePid} err={ack.ErrorMessage}")
        If ack Is Nothing OrElse Not ack.Success OrElse ack.GamePid <= 0 Then Return False

        Dim sawMarker As Boolean = False
        Dim sawExited As Boolean = False
        Dim sentStop As Boolean = False

        Do
            Dim f As Frame
            Try
                f = Await conn.ReadFrameAsync(ct).ConfigureAwait(False)
            Catch
                Exit Do
            End Try

            Select Case f.Kind
                Case FrameType.Stdout, FrameType.Stderr
                    Dim text As String = Encoding.UTF8.GetString(f.Payload)
                    If text.Contains(Marker) Then sawMarker = True
                    If sawMarker AndAlso Not sentStop Then
                        sentStop = True
                        Dim stopMsg As New StopMessage With {.Kind = "kill", .TimeoutMs = 5000}
                        Await conn.WriteFrameAsync(FrameType.StopGame, ProtocolCodec.Encode(stopMsg), ct).ConfigureAwait(False)
                    End If
                Case FrameType.Exited
                    Dim exited As ExitedMessage = ProtocolCodec.Decode(Of ExitedMessage)(f.Payload)
                    Console.WriteLine($"[self-test] Exited: code={If(exited Is Nothing, -1, exited.Code)}")
                    sawExited = True
                    Exit Do
            End Select
        Loop

        Console.WriteLine($"[self-test] spawn: marker={sawMarker} exited={sawExited}")
        Return sawMarker AndAlso sawExited
    End Function

    ''' <summary>
    ''' A dummy that prints the marker then stays alive until StopGame(kill):
    ''' cmd.exe on Windows, /bin/sh on Linux (which execs sleep so the tracked
    ''' pid is the sleep itself, leaving no orphan after the kill). Exercises
    ''' the real native spawn + stdout pump + stop/exit path on each OS.
    ''' </summary>
    Private Function BuildDummySpec() As SpawnSpec
        If OperatingSystem.IsWindows() Then
            Return New SpawnSpec With {
                .ExePath = "cmd.exe",
                .Arguments = "/c ""echo " & Marker & " & pause >nul""",
                .WorkingDirectory = Nothing,
                .Environment = Nothing,
                .Strategy = "StdoutCapture"
            }
        End If
        Return New SpawnSpec With {
            .ExePath = "/bin/sh",
            .Arguments = "-c ""echo " & Marker & "; exec sleep 60""",
            .WorkingDirectory = Nothing,
            .Environment = Nothing,
            .Strategy = "StdoutCapture"
        }
    End Function

    ''' <summary>Server side: accept one connection, run the real Supervisor.</summary>
    Private Async Function ServerOnceAsync(listener As IShimListener) As Task
        Dim stream As Stream = Await listener.AcceptAsync(CancellationToken.None).ConfigureAwait(False)
        Using conn As New FrameConnection(stream)
            Await Supervisor.RunServerAsync(conn, "selftest", "selftest-instance", CancellationToken.None).ConfigureAwait(False)
        End Using
    End Function

    Private Function MakeTempEndpoint() As String
        Dim id As String = Guid.NewGuid().ToString("N").Substring(0, 12)
        If OperatingSystem.IsWindows() Then
            Return "pipe:powergsm-shim-selftest-" & id
        Else
            Return "unix:" & Path.Combine(Path.GetTempPath(), "pgsm-shim-selftest-" & id & ".sock")
        End If
    End Function

    Private Function GetOsName() As String
        If OperatingSystem.IsWindows() Then Return "Windows"
        If OperatingSystem.IsLinux() Then Return "Linux"
        Return "other"
    End Function

End Module
