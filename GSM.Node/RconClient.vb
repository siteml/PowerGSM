Imports System
Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin
Imports GSM.Node.Api
Imports Microsoft.Extensions.Logging

' ============================================================
'  RconClientManager — manages persistent RCON connections
'
'  Each running instance that supports RCON gets a persistent
'  TCP connection. The manager sends commands through the node's
'  REST API, which routes them here.
'
'  Currently implements Source RCON protocol (Valve).
'  Factorio RCON uses the same wire format.
' ============================================================

Namespace GSM.Node

    ''' <summary>
    ''' Manages RCON connections for all running instances.
    ''' </summary>
    Public Class RconClientManager

        Private ReadOnly _connections As New ConcurrentDictionary(Of String, RconConnection)
        Private ReadOnly _logger As Microsoft.Extensions.Logging.ILogger(Of RconClientManager)

        Public Sub New(logger As Microsoft.Extensions.Logging.ILogger(Of RconClientManager))
            _logger = logger
        End Sub

        ''' <summary>
        ''' Connects to a game server's RCON port and authenticates.
        ''' </summary>
        Public Async Function ConnectAsync(instanceId As String,
                                           host As String,
                                           port As Integer,
                                           password As String,
                                           protocol As RconProtocol,
                                           cancellation As CancellationToken) As Task(Of Boolean)

            ' Disconnect existing if any
            Await DisconnectAsync(instanceId)

            If protocol <> RconProtocol.SourceRcon Then
                _logger.LogWarning("Protocol {Protocol} not yet implemented for {InstanceId}",
                                   protocol, instanceId)
                Return False
            End If

            Dim conn As New RconConnection()
            conn.InstanceId = instanceId
            conn.Host = host
            conn.Port = port
            conn.Protocol = protocol

            Try
                conn.TcpClient = New TcpClient()
                Await conn.TcpClient.ConnectAsync(host, port, cancellation)
                conn.Stream = conn.TcpClient.GetStream()
                conn.State = RconState.Connecting

                ' Authenticate
                Dim authPacket = RconPacket.CreateAuth(password)
                Await SendPacketAsync(conn, authPacket, cancellation)
                Dim response = Await ReadPacketAsync(conn, cancellation)

                If response IsNot Nothing AndAlso response.Id = authPacket.Id Then
                    conn.State = RconState.Authenticated
                    _connections(instanceId) = conn
                    _logger.LogInformation("RCON connected to {InstanceId} at {Host}:{Port}",
                                           instanceId, host, port)
                    Return True
                Else
                    conn.State = RconState.Failed
                    conn.TcpClient.Dispose()
                    _logger.LogWarning("RCON auth failed for {InstanceId}", instanceId)
                    Return False
                End If

            Catch ex As Exception
                conn.State = RconState.Failed
                Try
                    conn.TcpClient?.Dispose()
                Catch
                End Try
                _logger.LogError(ex, "RCON connect failed for {InstanceId}", instanceId)
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Sends a command and returns the response string.
        ''' </summary>
        Public Async Function SendCommandAsync(instanceId As String,
                                                command As String,
                                                cancellation As CancellationToken) As Task(Of RconCommandResponse)

            Dim conn As RconConnection = Nothing
            If Not _connections.TryGetValue(instanceId, conn) Then
                Return New RconCommandResponse With {
                    .InstanceId = instanceId,
                    .Success = False,
                    .ErrorMessage = "No RCON connection for this instance"
                }
            End If

            If conn.State <> RconState.Authenticated Then
                Return New RconCommandResponse With {
                    .InstanceId = instanceId,
                    .Success = False,
                    .ErrorMessage = $"RCON state is {conn.State}, not Authenticated"
                }
            End If

            Try
                Dim packet = RconPacket.CreateCommand(command)
                Await SendPacketAsync(conn, packet, cancellation)
                Dim response = Await ReadPacketAsync(conn, cancellation)

                Return New RconCommandResponse With {
                    .InstanceId = instanceId,
                    .Success = True,
                    .Response = If(response?.Body, "")
                }

            Catch ex As Exception
                conn.State = RconState.Failed
                _logger.LogError(ex, "RCON command failed for {InstanceId}", instanceId)
                Return New RconCommandResponse With {
                    .InstanceId = instanceId,
                    .Success = False,
                    .ErrorMessage = ex.Message
                }
            End Try
        End Function

        ''' <summary>
        ''' Returns the RCON connection status for an instance.
        ''' </summary>
        Public Function GetStatus(instanceId As String) As RconStatusResponse
            Dim conn As RconConnection = Nothing
            If Not _connections.TryGetValue(instanceId, conn) Then
                Return New RconStatusResponse With {
                    .InstanceId = instanceId,
                    .IsConnected = False,
                    .Protocol = RconProtocol.SourceRcon
                }
            End If
            Return New RconStatusResponse With {
                .InstanceId = instanceId,
                .IsConnected = (conn.State = RconState.Authenticated),
                .Protocol = conn.Protocol
            }
        End Function

        ''' <summary>
        ''' Disconnects and disposes the RCON connection for an instance.
        ''' </summary>
        Public Function DisconnectAsync(instanceId As String) As Task
            Dim conn As RconConnection = Nothing
            If _connections.TryRemove(instanceId, conn) Then
                Try
                    conn.TcpClient?.Dispose()
                Catch
                End Try
                conn.State = RconState.Disconnected
            End If
            Return Task.CompletedTask
        End Function

        ' ============================================================
        '  Source RCON packet I/O
        ' ============================================================

        Private Shared Async Function SendPacketAsync(conn As RconConnection,
                                                       packet As RconPacket,
                                                       cancellation As CancellationToken) As Task
            Dim payload = Encoding.UTF8.GetBytes(packet.Body)
            Dim packetSize = 4 + 4 + payload.Length + 2  ' id + type + body + two null terminators

            Using ms As New MemoryStream()
                Using writer As New BinaryWriter(ms)
                    writer.Write(packetSize)          ' Size (Int32 LE)
                    writer.Write(packet.Id)           ' Id (Int32 LE)
                    writer.Write(packet.PacketType)   ' Type (Int32 LE)
                    writer.Write(payload)             ' Body (UTF-8)
                    writer.Write(CByte(0))            ' Null terminator for body
                    writer.Write(CByte(0))            ' Null terminator for packet
                End Using
                Dim data = ms.ToArray()
                Await conn.Stream.WriteAsync(data, 0, data.Length, cancellation)
                Await conn.Stream.FlushAsync(cancellation)
            End Using
        End Function

        Private Shared Async Function ReadPacketAsync(conn As RconConnection,
                                                       cancellation As CancellationToken) As Task(Of RconPacket)
            Dim sizeBytes(3) As Byte
            Await ReadExactAsync(conn.Stream, sizeBytes, 4, cancellation)
            Dim packetSize = BitConverter.ToInt32(sizeBytes, 0)

            If packetSize < 10 OrElse packetSize > 4096 Then
                Throw New InvalidOperationException($"Invalid RCON packet size: {packetSize}")
            End If

            Dim bodyBytes(packetSize - 1) As Byte
            Await ReadExactAsync(conn.Stream, bodyBytes, packetSize, cancellation)

            Dim id = BitConverter.ToInt32(bodyBytes, 0)
            Dim pType = BitConverter.ToInt32(bodyBytes, 4)
            Dim bodyLen = packetSize - 10  ' minus id(4) + type(4) + two nulls(2)
            Dim body = ""
            If bodyLen > 0 Then
                body = Encoding.UTF8.GetString(bodyBytes, 8, bodyLen)
            End If

            Return New RconPacket With {
                .Id = id,
                .PacketType = pType,
                .Body = body
            }
        End Function

        Private Shared Async Function ReadExactAsync(stream As NetworkStream,
                                                      buffer() As Byte,
                                                      count As Integer,
                                                      cancellation As CancellationToken) As Task
            Dim offset = 0
            While offset < count
                Dim read = Await stream.ReadAsync(buffer, offset, count - offset, cancellation)
                If read = 0 Then Throw New EndOfStreamException("RCON connection closed")
                offset += read
            End While
        End Function

    End Class

    ' ============================================================
    '  RconConnection — internal state per RCON connection
    ' ============================================================

    Friend Class RconConnection
        Public Property InstanceId As String
        Public Property Host As String
        Public Property Port As Integer
        Public Property Protocol As RconProtocol
        Public Property State As RconState
        Public Property TcpClient As TcpClient
        Public Property Stream As NetworkStream
    End Class

    ' ============================================================
    '  RconPacket — Source RCON packet structure
    ' ============================================================

    Public Class RconPacket

        Private Shared _nextId As Integer = 1

        Public Property Id As Integer
        Public Property PacketType As Integer
        Public Property Body As String

        ' Source RCON packet types
        Public Const TypeAuth As Integer = 3
        Public Const TypeAuthResponse As Integer = 2
        Public Const TypeCommand As Integer = 2
        Public Const TypeCommandResponse As Integer = 0

        Public Shared Function CreateAuth(password As String) As RconPacket
            Return New RconPacket With {
                .Id = Interlocked.Increment(_nextId),
                .PacketType = TypeAuth,
                .Body = password
            }
        End Function

        Public Shared Function CreateCommand(command As String) As RconPacket
            Return New RconPacket With {
                .Id = Interlocked.Increment(_nextId),
                .PacketType = TypeCommand,
                .Body = command
            }
        End Function

    End Class

End Namespace
