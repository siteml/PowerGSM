Imports System
Imports System.Buffers
Imports System.IO
Imports System.IO.Pipelines
Imports System.Net
Imports System.Net.Security
Imports System.Security.Authentication
Imports System.Security.Cryptography.X509Certificates
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.AspNetCore.Connections
Imports Microsoft.AspNetCore.Http.Features
Imports Microsoft.Extensions.Logging

' ============================================================
'  GSM.Node.Security — single-port TLS sniffing
'  (docs/plans/HTTPS_SinglePort_Plan.md, slice 1)
'
'  Kestrel binds one listener per protocol and cannot serve HTTP
'  and HTTPS on the same port natively. This connection middleware
'  peeks the first byte of every accepted connection on ListenPort:
'
'    &H16 (TLS record type = Handshake)  → wrap the transport in an
'         SslStream (server auth with the node's self-signed cert)
'         and hand the decrypted duplex pipe to Kestrel's HTTP/1.1
'         parser, with ITlsConnectionFeature set so
'         HttpContext.Request.IsHttps is truthful.
'
'    anything else                       → plaintext HTTP, subject
'         to HttpMode: Open = pass through; LoopbackOnly = pass
'         through only when the peer is loopback; Disabled = abort
'         the connection at byte 1 (nothing is parsed, the bearer
'         token can never be read off a plaintext socket).
'
'  Kestrel's own HttpsConnectionMiddleware is internal, so the TLS
'  half is ours. HTTP/1.1 only on this listener (no ALPN / h2) —
'  the node is HTTP/1.1 (SSE, chunked uploads) and the listener is
'  pinned to HttpProtocols.Http1 by NodeProgram.
' ============================================================

Namespace GSM.Node.Security

    ''' <summary>Plain-HTTP policy on the sniffed listener.</summary>
    Public Enum PlainHttpPolicy
        Open
        LoopbackOnly
        Disabled
    End Enum

    Public NotInheritable Class TlsSniffConnectionMiddleware

        Private Const TlsHandshakeRecordType As Byte = &H16

        ''' <summary>
        ''' Bound on the wait for the first byte AND on the TLS
        ''' handshake. Kestrel's RequestHeadersTimeout only starts once
        ''' its parser owns the transport, i.e. after we hand over —
        ''' without this a peer could hold a socket open forever by
        ''' sending nothing.
        ''' </summary>
        Private Shared ReadOnly FirstByteTimeout As TimeSpan = TimeSpan.FromSeconds(10)
        Private Shared ReadOnly HandshakeTimeout As TimeSpan = TimeSpan.FromSeconds(10)

        Private ReadOnly _next As ConnectionDelegate
        Private ReadOnly _certificate As X509Certificate2
        Private ReadOnly _policy As PlainHttpPolicy
        Private ReadOnly _logger As ILogger

        Public Sub New(nextMiddleware As ConnectionDelegate,
                       certificate As X509Certificate2,
                       policy As PlainHttpPolicy,
                       logger As ILogger)
            If nextMiddleware Is Nothing Then Throw New ArgumentNullException(NameOf(nextMiddleware))
            If certificate Is Nothing Then Throw New ArgumentNullException(NameOf(certificate))
            _next = nextMiddleware
            _certificate = certificate
            _policy = policy
            _logger = logger
        End Sub

        ''' <summary>Parses the config string; unknown/blank → Open (matches NodeConfiguration.EnsureDefaults).</summary>
        Public Shared Function ParsePolicy(httpMode As String) As PlainHttpPolicy
            Dim m = If(httpMode, "").Trim()
            If m.Equals("Disabled", StringComparison.OrdinalIgnoreCase) Then Return PlainHttpPolicy.Disabled
            If m.Equals("LoopbackOnly", StringComparison.OrdinalIgnoreCase) Then Return PlainHttpPolicy.LoopbackOnly
            Return PlainHttpPolicy.Open
        End Function

        Public Async Function InvokeAsync(context As ConnectionContext) As Task
            Dim input = context.Transport.Input

            ' ---- 1. Peek the first byte without consuming it ----
            Dim firstByte As Integer = -1
            Try
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed)
                    cts.CancelAfter(FirstByteTimeout)
                    Do
                        Dim result = Await input.ReadAsync(cts.Token)
                        Dim buffer = result.Buffer
                        If buffer.Length > 0 Then
                            ' ToArray on a 1-byte slice: avoids a Span local, which VB
                            ' cannot hold inside an Async method.
                            firstByte = buffer.Slice(0, 1).ToArray()(0)
                            ' consumed = examined = start: nothing consumed, and the
                            ' next ReadAsync (ours or Kestrel's) returns immediately
                            ' with the same bytes.
                            input.AdvanceTo(buffer.Start, buffer.Start)
                            Exit Do
                        End If
                        input.AdvanceTo(buffer.Start, buffer.End)
                        If result.IsCompleted OrElse result.IsCanceled Then Exit Do
                    Loop
                End Using
            Catch ex As OperationCanceledException
                _logger?.LogDebug("Sniff: no first byte from {Remote} within {Timeout}s; aborting.",
                                  context.RemoteEndPoint, FirstByteTimeout.TotalSeconds)
                context.Abort(New ConnectionAbortedException("No data received before first-byte timeout."))
                Return
            End Try

            If firstByte < 0 Then
                ' Peer closed before sending anything. Nothing to do.
                Return
            End If

            ' ---- 2. Route ----
            If firstByte = TlsHandshakeRecordType Then
                Await ServeTlsAsync(context)
                Return
            End If

            Select Case _policy
                Case PlainHttpPolicy.Disabled
                    _logger?.LogDebug("Sniff: plaintext connection from {Remote} refused (HttpMode=Disabled).",
                                      context.RemoteEndPoint)
                    context.Abort(New ConnectionAbortedException("Plain HTTP is disabled on this node."))
                    Return
                Case PlainHttpPolicy.LoopbackOnly
                    If Not IsLoopback(context.RemoteEndPoint) Then
                        _logger?.LogDebug("Sniff: plaintext connection from {Remote} refused (HttpMode=LoopbackOnly).",
                                          context.RemoteEndPoint)
                        context.Abort(New ConnectionAbortedException("Plain HTTP is loopback-only on this node."))
                        Return
                    End If
            End Select

            Await _next(context)
        End Function

        Private Async Function ServeTlsAsync(context As ConnectionContext) As Task
            Dim originalTransport = context.Transport
            Dim rawStream As New DuplexPipeStream(originalTransport)
            Dim ssl As New SslStream(rawStream, leaveInnerStreamOpen:=False)

            ' VB (unlike C#) forbids Await inside Catch/Finally (BC36943) —
            ' same gotcha as EventStream Slice 3. Pattern: capture the
            ' failure inside the Try, do the async cleanup after it.
            Dim handshakeError As Exception = Nothing
            Try
                Dim authOptions As New SslServerAuthenticationOptions With {
                    .ServerCertificate = _certificate,
                    .ClientCertificateRequired = False,
                    .EnabledSslProtocols = SslProtocols.Tls12 Or SslProtocols.Tls13,
                    .CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    .ApplicationProtocols = New List(Of SslApplicationProtocol) From {SslApplicationProtocol.Http11}
                }

                Using cts = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed)
                    cts.CancelAfter(HandshakeTimeout)
                    Await ssl.AuthenticateAsServerAsync(authOptions, cts.Token)
                End Using
            Catch ex As Exception
                handshakeError = ex
            End Try

            If handshakeError IsNot Nothing Then
                ' Handshake failure or timeout. Common causes: a client that
                ' doesn't trust/pin us and aborts, a scanner, a truncated
                ' hello. Debug level — this is not an auth failure and must
                ' not feed the lockout tracker.
                _logger?.LogDebug(handshakeError, "Sniff: TLS handshake with {Remote} failed.", context.RemoteEndPoint)
                Await ssl.DisposeAsync()
                context.Abort(New ConnectionAbortedException("TLS handshake failed.", handshakeError))
                Return
            End If

            _logger?.LogDebug("Sniff: TLS {Protocol} established with {Remote}.", ssl.SslProtocol, context.RemoteEndPoint)

            Dim sslPipe As New StreamDuplexPipe(ssl)
            context.Transport = sslPipe
            context.Features.Set(Of ITlsConnectionFeature)(New SniffedTlsConnectionFeature())

            Dim pipelineError As Exception = Nothing
            Try
                Await _next(context)
            Catch ex As Exception
                pipelineError = ex
            End Try

            ' Restore + tear down in the same order Kestrel's own HTTPS
            ' middleware uses: our pipes over the SslStream first, then
            ' the SslStream (which closes DuplexPipeStream, which leaves
            ' the underlying Kestrel pipes to Kestrel). Runs on both the
            ' normal and the faulted path — this block is the Finally.
            Try
                Await sslPipe.Input.CompleteAsync()
            Catch
            End Try
            Try
                Await sslPipe.Output.CompleteAsync()
            Catch
            End Try
            Try
                Await ssl.DisposeAsync()
            Catch
            End Try
            context.Transport = originalTransport

            If pipelineError IsNot Nothing Then
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pipelineError).Throw()
            End If
        End Function

        Private Shared Function IsLoopback(remote As EndPoint) As Boolean
            Dim ip = TryCast(remote, IPEndPoint)
            If ip Is Nothing Then
                ' Unix domain socket / named pipe / unknown: local by construction.
                Return True
            End If
            Dim addr = ip.Address
            If addr.IsIPv4MappedToIPv6 Then addr = addr.MapToIPv4()
            Return IPAddress.IsLoopback(addr)
        End Function

    End Class

    ' ------------------------------------------------------------
    '  Helpers
    ' ------------------------------------------------------------

    ''' <summary>
    ''' Minimal ITlsConnectionFeature so Kestrel reports IsHttps = True and
    ''' selects HTTP/1.1 on the sniffed listener. No client certificates.
    ''' </summary>
    Friend NotInheritable Class SniffedTlsConnectionFeature
        Implements ITlsConnectionFeature

        Public Property ClientCertificate As X509Certificate2 Implements ITlsConnectionFeature.ClientCertificate

        Public Function GetClientCertificateAsync(cancellationToken As CancellationToken) As Task(Of X509Certificate2) _
            Implements ITlsConnectionFeature.GetClientCertificateAsync
            Return Task.FromResult(Of X509Certificate2)(Nothing)
        End Function
    End Class

    ''' <summary>
    ''' A Stream over Kestrel's IDuplexPipe (reads from Input, writes to
    ''' Output) so SslStream can sit on top. The pipes are left open —
    ''' Kestrel owns their lifetime.
    ''' </summary>
    Friend NotInheritable Class DuplexPipeStream
        Inherits Stream

        Private ReadOnly _readStream As Stream
        Private ReadOnly _writeStream As Stream

        Public Sub New(pipe As IDuplexPipe)
            _readStream = pipe.Input.AsStream(leaveOpen:=True)
            _writeStream = pipe.Output.AsStream(leaveOpen:=True)
        End Sub

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return True
            End Get
        End Property

        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return True
            End Get
        End Property

        Public Overrides ReadOnly Property Length As Long
            Get
                Throw New NotSupportedException()
            End Get
        End Property

        Public Overrides Property Position As Long
            Get
                Throw New NotSupportedException()
            End Get
            Set(value As Long)
                Throw New NotSupportedException()
            End Set
        End Property

        Public Overrides Sub Flush()
            _writeStream.Flush()
        End Sub

        Public Overrides Function FlushAsync(cancellationToken As CancellationToken) As Task
            Return _writeStream.FlushAsync(cancellationToken)
        End Function

        Public Overrides Function Read(buffer() As Byte, offset As Integer, count As Integer) As Integer
            Return _readStream.Read(buffer, offset, count)
        End Function

        Public Overrides Function ReadAsync(buffer() As Byte, offset As Integer, count As Integer,
                                            cancellationToken As CancellationToken) As Task(Of Integer)
            Return _readStream.ReadAsync(buffer, offset, count, cancellationToken)
        End Function

        Public Overrides Function ReadAsync(buffer As Memory(Of Byte),
                                            Optional cancellationToken As CancellationToken = Nothing) As ValueTask(Of Integer)
            Return _readStream.ReadAsync(buffer, cancellationToken)
        End Function

        Public Overrides Sub Write(buffer() As Byte, offset As Integer, count As Integer)
            _writeStream.Write(buffer, offset, count)
        End Sub

        Public Overrides Function WriteAsync(buffer() As Byte, offset As Integer, count As Integer,
                                             cancellationToken As CancellationToken) As Task
            Return _writeStream.WriteAsync(buffer, offset, count, cancellationToken)
        End Function

        Public Overrides Function WriteAsync(buffer As ReadOnlyMemory(Of Byte),
                                             Optional cancellationToken As CancellationToken = Nothing) As ValueTask
            Return _writeStream.WriteAsync(buffer, cancellationToken)
        End Function

        Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
            Throw New NotSupportedException()
        End Function

        Public Overrides Sub SetLength(value As Long)
            Throw New NotSupportedException()
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                _readStream.Dispose()
                _writeStream.Dispose()
            End If
            MyBase.Dispose(disposing)
        End Sub
    End Class

    ''' <summary>
    ''' IDuplexPipe over a Stream (the SslStream). PipeReader.Create /
    ''' PipeWriter.Create are the public .NET 8 stream adapters.
    ''' </summary>
    Friend NotInheritable Class StreamDuplexPipe
        Implements IDuplexPipe

        Private ReadOnly _input As PipeReader
        Private ReadOnly _output As PipeWriter

        Public Sub New(stream As Stream)
            _input = PipeReader.Create(stream, New StreamPipeReaderOptions(leaveOpen:=True))
            _output = PipeWriter.Create(stream, New StreamPipeWriterOptions(leaveOpen:=True))
        End Sub

        Public ReadOnly Property Input As PipeReader Implements IDuplexPipe.Input
            Get
                Return _input
            End Get
        End Property

        Public ReadOnly Property Output As PipeWriter Implements IDuplexPipe.Output
            Get
                Return _output
            End Get
        End Property
    End Class

End Namespace
