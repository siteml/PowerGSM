Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  RconClientManager
'
'  Manages one persistent RCON connection per instance.
'  The node holds these connections locally - the manager
'  never connects to RCON ports directly.
'
'  Source RCON protocol (used by Factorio and most Source
'  engine games):
'    - TCP connection to the game's RCON port
'    - Authentication: send SERVERDATA_AUTH packet with password
'    - Commands: send SERVERDATA_EXECCOMMAND, receive response
'    - Packet format: 4-byte little-endian size, then body:
'        [4 bytes] request ID (int32, little-endian)
'        [4 bytes] type (int32, little-endian)
'        [N bytes] body string (null-terminated UTF-8)
'        [1 byte]  empty string terminator (null byte)
'
'  Connection lifecycle:
'    - AutoConnect: node connects after StartupDelayMs when
'      instance starts, retries up to MaxConnectRetries times
'    - Persistent: one TCP connection per instance, reused for
'      all commands (not open/close per command)
'    - Auto-reconnect: if connection drops, node retries
'    - Manual: manager can force connect/disconnect via API
'
'  Thread safety:
'    Each RconConnection has its own lock (_sendLock) because
'    RCON doesn't support concurrent requests on one connection
'    - commands must be sent and received sequentially.
' ============================================================

Public Class RconClientManager

    Private ReadOnly _connections As New ConcurrentDictionary(Of String, RconConnection)(
        StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _logger As ILogger(Of RconClientManager)

    Public Sub New(logger As ILogger(Of RconClientManager))
        _logger = logger
    End Sub


    ' ============================================================
    '  CONNECTION MANAGEMENT
    ' ============================================================

    ' Called after an instance starts. Schedules auto-connect if configured.
    Public Sub RegisterInstance(instanceId As String, config As NodeRconConfig)
        If config Is Nothing Then Return

        Dim conn = _connections.GetOrAdd(instanceId,
            Function(id) New RconConnection(id, config, _logger))

        If config.AutoConnect Then
            Task.Run(Async Function()
                         Await Task.Delay(config.StartupDelayMs)
                         Await ConnectWithRetriesAsync(conn, CancellationToken.None)
                     End Function)
        End If
    End Sub

    ' Called when an instance stops. Closes the connection.
    Public Async Function UnregisterInstanceAsync(instanceId As String) As Task
        Dim conn As RconConnection = Nothing
        If _connections.TryRemove(instanceId, conn) Then
            Await conn.DisconnectAsync()
        End If
    End Function

    ' Explicit connect request from manager (POST /rcon/connect).
    Public Async Function ConnectAsync(instanceId As String,
                                        cancellation As CancellationToken) As Task(Of RconConnectResponse)

        Dim conn As RconConnection = Nothing
        If Not _connections.TryGetValue(instanceId, conn) Then
            Return New RconConnectResponse With {
                .RconState = RconState.NotAvailable,
                .Message = "RCON is not configured for this instance."
            }
        End If

        conn.ResetRetries()
        Await ConnectWithRetriesAsync(conn, cancellation)

        Return New RconConnectResponse With {
            .RconState = conn.State,
            .Message = conn.State.ToString()
        }
    End Function

    ' Explicit disconnect from manager (POST /rcon/disconnect).
    Public Async Function DisconnectAsync(instanceId As String) As Task(Of RconDisconnectResponse)
        Dim conn As RconConnection = Nothing
        If Not _connections.TryGetValue(instanceId, conn) Then
            Return New RconDisconnectResponse With {
                .RconState = RconState.NotAvailable,
                .Message = "RCON is not configured for this instance."
            }
        End If

        Await conn.DisconnectAsync()
        Return New RconDisconnectResponse With {
            .RconState = conn.State,
            .Message = "Disconnected."
        }
    End Function

    Public Function GetStatus(instanceId As String) As RconStatusResponse
        Dim conn As RconConnection = Nothing
        If Not _connections.TryGetValue(instanceId, conn) Then
            Return New RconStatusResponse With {
                .InstanceId = instanceId,
                .RconState = RconState.NotAvailable
            }
        End If
        Return New RconStatusResponse With {
            .InstanceId = instanceId,
            .RconState = conn.State,
            .ConnectedAt = conn.ConnectedAt,
            .LastCommandAt = conn.LastCommandAt,
            .RetriesAttempted = conn.RetriesAttempted
        }
    End Function

    ' Send a command (POST /rcon/send).
    Public Async Function SendAsync(instanceId As String,
                                     request As RconSendRequest,
                                     cancellation As CancellationToken) As Task(Of RconSendResponse)

        Dim conn As RconConnection = Nothing
        If Not _connections.TryGetValue(instanceId, conn) Then
            Return New RconSendResponse With {
                .Success = False,
                .ErrorMessage = "RCON is not configured for this instance."
            }
        End If

        If conn.State <> RconState.Connected Then
            Return New RconSendResponse With {
                .Success = False,
                .ErrorMessage = $"RCON is not connected (state: {conn.State}). " &
                                "Use POST /rcon/connect to reconnect."
            }
        End If

        Return Await conn.SendCommandAsync(request.Command,
                                            request.TimeoutMs,
                                            cancellation)
    End Function

    ' Returns the current RCON state for a given instance.
    ' Called by ProcessManager to update the ManagedInstance.
    Public Function GetState(instanceId As String) As RconState
        Dim conn As RconConnection = Nothing
        If Not _connections.TryGetValue(instanceId, conn) Then
            Return RconState.NotAvailable
        End If
        Return conn.State
    End Function


    ' ============================================================
    '  INTERNAL CONNECT WITH RETRY
    ' ============================================================

    Private Async Function ConnectWithRetriesAsync(conn As RconConnection,
                                                    cancellation As CancellationToken) As Task

        Dim config = conn.Config

        For attempt = 1 To config.MaxConnectRetries + 1
            If cancellation.IsCancellationRequested Then Return
            Try
                Await conn.ConnectAsync(cancellation)
                Return  ' Connected successfully
            Catch ex As Exception
                conn.RetriesAttempted = attempt
                _logger.LogWarning(
                    "RCON [{Id}]: connect attempt {N}/{Max} failed: {Msg}",
                    conn.InstanceId, attempt, config.MaxConnectRetries, ex.Message)

                If attempt > config.MaxConnectRetries Then
                    _logger.LogError(
                        "RCON [{Id}]: giving up after {N} attempts - marking Unavailable",
                        conn.InstanceId, config.MaxConnectRetries)
                    conn.State = RconState.Unavailable
                    Return
                End If

                Await Task.Delay(config.RetryIntervalMs, cancellation)
            End Try
        Next
    End Function

End Class


' ============================================================
'  RCON CONNECTION
'  One per instance. Holds the TCP connection and handles
'  the Source RCON protocol framing.
' ============================================================

Friend Class RconConnection

    Public ReadOnly Property InstanceId As String
    Public ReadOnly Property Config As NodeRconConfig
    Public Property State As RconState = RconState.NotAvailable
    Public Property ConnectedAt As DateTime?
    Public Property LastCommandAt As DateTime?
    Public Property RetriesAttempted As Integer = 0

    Private ReadOnly _logger As ILogger
    Private _client As TcpClient
    Private _stream As NetworkStream
    ' RCON doesn't support concurrent requests - one at a time.
    Private ReadOnly _sendLock As New SemaphoreSlim(1, 1)
    ' Request IDs must be unique per session. Interlocked for thread safety.
    Private _requestId As Integer = 0

    ' Source RCON packet type constants
    Private Const TypeAuth As Integer = 3
    Private Const TypeAuthResponse As Integer = 2
    Private Const TypeExecCommand As Integer = 2
    Private Const TypeResponseValue As Integer = 0

    Public Sub New(instanceId As String,
                   config As NodeRconConfig,
                   logger As ILogger)
        InstanceId = instanceId
        Config = config
        _logger = logger
    End Sub

    Public Sub ResetRetries()
        RetriesAttempted = 0
    End Sub

    Public Async Function ConnectAsync(cancellation As CancellationToken) As Task
        State = RconState.Connecting
        _logger.LogDebug("RCON [{Id}]: connecting to localhost:{Port}", InstanceId, Config.Port)

        ' Close any existing connection first.
        CloseClient()

        _client = New TcpClient()
        _client.SendTimeout = Config.ConnectTimeoutMs
        _client.ReceiveTimeout = Config.ConnectTimeoutMs

        Using cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
            cts.CancelAfter(Config.ConnectTimeoutMs)
            Await _client.ConnectAsync("127.0.0.1", Config.Port, cts.Token)
        End Using

        _stream = _client.GetStream()
        State = RconState.Authenticating
        _logger.LogDebug("RCON [{Id}]: authenticating", InstanceId)

        ' Send authentication packet.
        Dim authId = Interlocked.Increment(_requestId)
        Await SendPacketAsync(authId, TypeAuth, Config.Password, cancellation)

        ' Read the auth response. Source RCON sends two responses to auth:
        ' first a SERVERDATA_RESPONSE_VALUE (type 0) then a SERVERDATA_AUTH_RESPONSE (type 2).
        ' A request ID of -1 in the auth response means authentication failed.
        Dim response1 = Await ReadPacketAsync(cancellation)
        Dim response2 = Await ReadPacketAsync(cancellation)

        ' The meaningful response is the one with type TypeAuthResponse.
        Dim authResponse = If(response1.Type = TypeAuthResponse, response1, response2)

        If authResponse.RequestId = -1 Then
            State = RconState.Unavailable
            Throw New InvalidOperationException(
                "RCON authentication failed. Check the RCON password.")
        End If

        State = RconState.Connected
        ConnectedAt = DateTime.UtcNow
        RetriesAttempted = 0
        _logger.LogInformation("RCON [{Id}]: connected", InstanceId)
    End Function

    Public Async Function SendCommandAsync(command As String,
                                            timeoutMs As Integer,
                                            cancellation As CancellationToken) As Task(Of RconSendResponse)

        ' Acquire the send lock - only one command in flight at a time.
        Await _sendLock.WaitAsync(cancellation)
        Try
            Dim sw = Stopwatch.StartNew()
            Dim reqId = Interlocked.Increment(_requestId)

            Using cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
                cts.CancelAfter(timeoutMs)
                Try
                    Await SendPacketAsync(reqId, TypeExecCommand, command, cts.Token)
                    Dim response = Await ReadPacketAsync(cts.Token)

                    ' Some servers send multiple packets for long responses.
                    ' We read until we get a packet matching our request ID.
                    Dim responseText = response.Body
                    Do While response.RequestId <> reqId
                        response = Await ReadPacketAsync(cts.Token)
                        responseText &= response.Body
                    Loop

                    sw.Stop()
                    LastCommandAt = DateTime.UtcNow

                    _logger.LogDebug("RCON [{Id}]: '{Cmd}' → '{Resp}' ({Ms}ms)",
                                     InstanceId, command, responseText, sw.ElapsedMilliseconds)

                    Return New RconSendResponse With {
                        .Success = True,
                        .Response = responseText,
                        .RoundTripMs = sw.ElapsedMilliseconds
                    }

                Catch ex As OperationCanceledException
                    sw.Stop()
                    State = RconState.Disconnected
                    Return New RconSendResponse With {
                        .Success = False,
                        .ErrorMessage = $"Command timed out after {timeoutMs}ms.",
                        .RoundTripMs = sw.ElapsedMilliseconds
                    }
                Catch ex As Exception
                    sw.Stop()
                    State = RconState.Disconnected
                    _logger.LogWarning("RCON [{Id}]: send error: {Msg}",
                                       InstanceId, ex.Message)
                    Return New RconSendResponse With {
                        .Success = False,
                        .ErrorMessage = ex.Message,
                        .RoundTripMs = sw.ElapsedMilliseconds
                    }
                End Try
            End Using
        Finally
            _sendLock.Release()
        End Try
    End Function

    Public Async Function DisconnectAsync() As Task
        Await _sendLock.WaitAsync()
        Try
            CloseClient()
            State = RconState.NotAvailable
            ConnectedAt = Nothing
            _logger.LogInformation("RCON [{Id}]: disconnected", InstanceId)
        Finally
            _sendLock.Release()
        End Try
    End Function

    Private Sub CloseClient()
        Try
            If _stream IsNot Nothing Then _stream.Close()
            If _client IsNot Nothing Then _client.Close()
        Catch
        End Try
        _stream = Nothing
        _client = Nothing
    End Sub


    ' ============================================================
    '  SOURCE RCON PACKET FRAMING
    '
    '  Packet layout (all integers are little-endian):
    '    [4 bytes] Packet size (size of everything AFTER these 4 bytes)
    '    [4 bytes] Request ID
    '    [4 bytes] Type
    '    [N bytes] Body string (UTF-8, null-terminated)
    '    [1 byte]  Empty string terminator (null byte)
    '
    '  So total wire size = 4 (size field) + 4 (id) + 4 (type) + N (body) + 2 (nulls)
    '                     = 10 + N bytes
    ' ============================================================

    Private Async Function SendPacketAsync(requestId As Integer,
                                            packetType As Integer,
                                            body As String,
                                            cancellation As CancellationToken) As Task

        Dim bodyBytes = Encoding.UTF8.GetBytes(body)
        ' Packet body = id (4) + type (4) + body bytes + two null bytes
        Dim packetSize = 4 + 4 + bodyBytes.Length + 2

        If packetSize > Config.MaxPacketSize Then
            Throw New InvalidOperationException(
                $"RCON command too large ({packetSize} bytes, max {Config.MaxPacketSize}).")
        End If

        Dim buffer(packetSize + 3) As Byte   ' +4 for the size field itself

        ' Write the size field (4 bytes, little-endian)
        BitConverter.GetBytes(packetSize).CopyTo(buffer, 0)
        ' Write request ID (4 bytes, little-endian)
        BitConverter.GetBytes(requestId).CopyTo(buffer, 4)
        ' Write type (4 bytes, little-endian)
        BitConverter.GetBytes(packetType).CopyTo(buffer, 8)
        ' Write body bytes
        bodyBytes.CopyTo(buffer, 12)
        ' Two null terminators
        buffer(12 + bodyBytes.Length) = 0
        buffer(12 + bodyBytes.Length + 1) = 0

        Await _stream.WriteAsync(buffer, 0, buffer.Length, cancellation)
        Await _stream.FlushAsync(cancellation)
    End Function

    Private Async Function ReadPacketAsync(cancellation As CancellationToken) As Task(Of RconPacket)

        ' Read the 4-byte size field first.
        Dim sizeBuffer(3) As Byte
        Await ReadExactAsync(sizeBuffer, cancellation)
        Dim packetSize = BitConverter.ToInt32(sizeBuffer, 0)

        If packetSize < 10 OrElse packetSize > Config.MaxPacketSize Then
            Throw New InvalidOperationException(
                $"RCON: invalid packet size {packetSize}. Connection may be corrupt.")
        End If

        ' Read the rest of the packet.
        Dim bodyBuffer(packetSize - 1) As Byte
        Await ReadExactAsync(bodyBuffer, cancellation)

        Dim requestId = BitConverter.ToInt32(bodyBuffer, 0)
        Dim packetType = BitConverter.ToInt32(bodyBuffer, 4)
        ' Body starts at offset 8, ends before the two null terminators.
        Dim bodyLength = packetSize - 4 - 4 - 2   ' minus id, type, two nulls
        Dim body = If(bodyLength > 0,
                      Encoding.UTF8.GetString(bodyBuffer, 8, bodyLength),
                      String.Empty)

        Return New RconPacket With {
            .RequestId = requestId,
            .Type = packetType,
            .Body = body
        }
    End Function

    ' Read exactly N bytes from the stream. Keeps reading until the buffer
    ' is full because TCP may deliver data in smaller chunks than we expect.
    Private Async Function ReadExactAsync(buffer As Byte(),
                                           cancellation As CancellationToken) As Task
        Dim offset = 0
        Dim remaining = buffer.Length
        Do While remaining > 0
            Dim read = Await _stream.ReadAsync(buffer, offset, remaining, cancellation)
            If read = 0 Then
                Throw New EndOfStreamException(
                    "RCON: connection closed by server.")
            End If
            offset += read
            remaining -= read
        Loop
    End Function

End Class


' ============================================================
'  RCON PACKET
'  Internal representation of a decoded Source RCON packet.
' ============================================================

Friend Class RconPacket
    Public Property RequestId As Integer
    Public Property Type As Integer
    Public Property Body As String
End Class
