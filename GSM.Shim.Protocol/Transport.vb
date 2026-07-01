' ============================================================
'  GSM.Shim.Protocol — transport (named pipe / Unix domain socket)
'
'  Endpoint string forms (the Node builds these; both sides parse them):
'      pipe:<name>     Windows named pipe   (\\.\pipe\<name>)
'      unix:<path>     Unix domain socket   (a filesystem path)
'
'  CreateListener (shim/server side) and ConnectAsync (node/client side)
'  both return a duplex byte Stream that FrameConnection wraps. Keeping
'  the OS-specific stream creation here means the rest of the protocol
'  and both executables stay transport-agnostic.
' ============================================================
Imports System
Imports System.IO
Imports System.IO.Pipes
Imports System.Net.Sockets
Imports System.Threading
Imports System.Threading.Tasks

Namespace GSM.Shim.Protocol

    ''' <summary>Server-side listener that yields one accepted connection at a time.</summary>
    Public Interface IShimListener
        Inherits IDisposable
        Function AcceptAsync(ct As CancellationToken) As Task(Of Stream)
        ReadOnly Property Endpoint As String
    End Interface

    Public Module ShimTransport

        Public Const PipeScheme As String = "pipe"
        Public Const UnixScheme As String = "unix"

        ''' <summary>Split "scheme:address" on the first colon.</summary>
        Public Function ParseEndpoint(endpoint As String) As (Scheme As String, Address As String)
            If String.IsNullOrEmpty(endpoint) Then
                Throw New ArgumentException("Endpoint is empty", NameOf(endpoint))
            End If
            Dim idx As Integer = endpoint.IndexOf(":"c)
            If idx <= 0 Then
                Throw New ArgumentException($"Endpoint '{endpoint}' is not 'scheme:address'", NameOf(endpoint))
            End If
            Dim scheme As String = endpoint.Substring(0, idx).ToLowerInvariant()
            Dim address As String = endpoint.Substring(idx + 1)
            Return (scheme, address)
        End Function

        ''' <summary>Create (and, for sockets, bind) the server side of an endpoint.</summary>
        Public Function CreateListener(endpoint As String) As IShimListener
            Dim parsed = ParseEndpoint(endpoint)
            Select Case parsed.Scheme
                Case PipeScheme
                    Return New NamedPipeListener(endpoint, NormalizePipeName(parsed.Address))
                Case UnixScheme
                    Return New UnixSocketListener(endpoint, parsed.Address)
                Case Else
                    Throw New ArgumentException($"Unknown endpoint scheme '{parsed.Scheme}'", NameOf(endpoint))
            End Select
        End Function

        ''' <summary>Connect the client side of an endpoint.</summary>
        Public Async Function ConnectAsync(endpoint As String, ct As CancellationToken) As Task(Of Stream)
            Dim parsed = ParseEndpoint(endpoint)
            Select Case parsed.Scheme
                Case PipeScheme
                    Dim client As New NamedPipeClientStream(".", NormalizePipeName(parsed.Address),
                                                            PipeDirection.InOut, PipeOptions.Asynchronous)
                    Await client.ConnectAsync(ct).ConfigureAwait(False)
                    Return client
                Case UnixScheme
                    Dim sock As New Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                    Await sock.ConnectAsync(New UnixDomainSocketEndPoint(parsed.Address), ct).ConfigureAwait(False)
                    Return New NetworkStream(sock, ownsSocket:=True)
                Case Else
                    Throw New ArgumentException($"Unknown endpoint scheme '{parsed.Scheme}'", NameOf(endpoint))
            End Select
        End Function

        ''' <summary>Accept a "\\.\pipe\name" or bare "name"; the pipe stream APIs want the bare name.</summary>
        Private Function NormalizePipeName(address As String) As String
            Const prefix As String = "\\.\pipe\"
            If address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then
                Return address.Substring(prefix.Length)
            End If
            Return address
        End Function

    End Module

    Friend NotInheritable Class NamedPipeListener
        Implements IShimListener

        Private ReadOnly _endpoint As String
        Private ReadOnly _pipeName As String

        Public Sub New(endpoint As String, pipeName As String)
            _endpoint = endpoint
            _pipeName = pipeName
        End Sub

        Public ReadOnly Property Endpoint As String Implements IShimListener.Endpoint
            Get
                Return _endpoint
            End Get
        End Property

        Public Async Function AcceptAsync(ct As CancellationToken) As Task(Of Stream) Implements IShimListener.AcceptAsync
            Dim server As New NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                                                    NamedPipeServerStream.MaxAllowedServerInstances,
                                                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous)
            Try
                Await server.WaitForConnectionAsync(ct).ConfigureAwait(False)
            Catch
                server.Dispose()
                Throw
            End Try
            Return server
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            ' Named pipes hold no listening resource between accepts.
        End Sub

    End Class

    Friend NotInheritable Class UnixSocketListener
        Implements IShimListener

        Private ReadOnly _endpoint As String
        Private ReadOnly _path As String
        Private ReadOnly _socket As Socket

        Public Sub New(endpoint As String, path As String)
            _endpoint = endpoint
            _path = path
            TryDeleteStaleSocketFile(path)
            _socket = New Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            _socket.Bind(New UnixDomainSocketEndPoint(path))
            _socket.Listen(16)
        End Sub

        Public ReadOnly Property Endpoint As String Implements IShimListener.Endpoint
            Get
                Return _endpoint
            End Get
        End Property

        Public Async Function AcceptAsync(ct As CancellationToken) As Task(Of Stream) Implements IShimListener.AcceptAsync
            Dim conn As Socket = Await _socket.AcceptAsync(ct).ConfigureAwait(False)
            Return New NetworkStream(conn, ownsSocket:=True)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                _socket.Dispose()
            Catch
                ' best-effort
            End Try
            TryDeleteStaleSocketFile(_path)
        End Sub

        Private Shared Sub TryDeleteStaleSocketFile(path As String)
            Try
                If File.Exists(path) Then
                    File.Delete(path)
                End If
            Catch
                ' a leftover socket file from a crashed shim; bind will fail
                ' loudly if we truly can't clear it.
            End Try
        End Sub

    End Class

End Namespace
