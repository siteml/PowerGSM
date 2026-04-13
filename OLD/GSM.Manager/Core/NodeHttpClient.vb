Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Runtime.CompilerServices
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  NodeHttpClient
'
'  Implements INodeClient by making HTTP calls to a node's
'  REST API as defined in NodeApiContract.vb.
'
'  One instance per node connection. The manager creates these
'  via NodeHttpClientFactory, which is registered in DI.
'
'  Key design decisions:
'    - One HttpClient per node (not shared) so per-node
'      auth tokens and base URLs are encapsulated cleanly
'    - All requests carry the bearer token automatically via
'      a default request header set at construction time
'    - Errors from the node (non-2xx) are deserialised into
'      NodeErrorResponse and wrapped in NodeApiException so
'      callers get structured error information, not raw HTTP
'    - StreamLogsAsync returns IAsyncEnumerable so the caller
'      gets a clean async sequence and disposes it to close
'      the SSE connection - no manual stream management needed
'
'  Timeout strategy:
'    - Short timeout for quick queries (health, metrics, status)
'    - Long timeout for operations that may take a while
'      (start, stop, install) - these are bounded by the
'      operation itself, not an HTTP timeout
'    - No timeout for SSE streams - they're open-ended
' ============================================================

Namespace GSM.Core

    Public Class NodeHttpClient
        Implements INodeClient

        Private ReadOnly _http As HttpClient
        Private ReadOnly _logger As ILogger(Of NodeHttpClient)
        Private ReadOnly _jsonOptions As JsonSerializerOptions

        ' Shared JSON options - configured once, reused for all requests.
        Private Shared ReadOnly DefaultJsonOptions As JsonSerializerOptions = CreateDefaultJsonOptions()

        Private Shared Function CreateDefaultJsonOptions() As JsonSerializerOptions
            Dim options As New JsonSerializerOptions With {
                .PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }
            options.Converters.Add(New JsonStringEnumConverter(JsonNamingPolicy.CamelCase))
            Return options
        End Function

        Public Sub New(http As HttpClient,
                       logger As ILogger(Of NodeHttpClient))
            _http = http
            _logger = logger
            _jsonOptions = DefaultJsonOptions
        End Sub

        ' ============================================================
        '  NODE HEALTH
        ' ============================================================

        Public Async Function GetVersionAsync(
                cancellation As CancellationToken) As Task(Of NodeVersionResponse) _
                Implements INodeClient.GetVersionAsync
            Return Await GetAsync(Of NodeVersionResponse)("/api/version", cancellation)
        End Function

        Public Async Function GetHealthAsync(
                cancellation As CancellationToken) As Task(Of NodeHealthResponse) _
                Implements INodeClient.GetHealthAsync
            Return Await GetAsync(Of NodeHealthResponse)("/api/v1/health", cancellation)
        End Function

        ' ============================================================
        '  INSTANCES
        ' ============================================================

        Public Async Function GetInstancesAsync(
                cancellation As CancellationToken) As Task(Of NodeInstanceListResponse) _
                Implements INodeClient.GetInstancesAsync
            Return Await GetAsync(Of NodeInstanceListResponse)(
                "/api/v1/instances", cancellation)
        End Function

        Public Async Function GetInstanceAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of NodeInstanceDetailResponse) _
                Implements INodeClient.GetInstanceAsync
            Return Await GetAsync(Of NodeInstanceDetailResponse)(
                $"/api/v1/instances/{instanceId}", cancellation)
        End Function

        Public Async Function StartInstanceAsync(
                request As StartInstanceRequest,
                cancellation As CancellationToken) As Task(Of StartInstanceResponse) _
                Implements INodeClient.StartInstanceAsync
            Return Await PostAsync(Of StartInstanceRequest, StartInstanceResponse)(
                $"/api/v1/instances/{request.InstanceId}/start", request, cancellation)
        End Function

        Public Async Function StopInstanceAsync(
                instanceId As String,
                request As StopInstanceRequest,
                cancellation As CancellationToken) As Task(Of StopInstanceResponse) _
                Implements INodeClient.StopInstanceAsync
            Return Await PostAsync(Of StopInstanceRequest, StopInstanceResponse)(
                $"/api/v1/instances/{instanceId}/stop", request, cancellation)
        End Function

        Public Async Function RestartInstanceAsync(
                instanceId As String,
                request As RestartInstanceRequest,
                cancellation As CancellationToken) As Task(Of RestartInstanceResponse) _
                Implements INodeClient.RestartInstanceAsync
            Return Await PostAsync(Of RestartInstanceRequest, RestartInstanceResponse)(
                $"/api/v1/instances/{instanceId}/restart", request, cancellation)
        End Function

        Public Async Function KillInstanceAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of KillInstanceResponse) _
                Implements INodeClient.KillInstanceAsync
            Return Await PostAsync(Of Object, KillInstanceResponse)(
                $"/api/v1/instances/{instanceId}/kill", Nothing, cancellation)
        End Function

        Public Async Function GetMetricsAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of InstanceMetricsResponse) _
                Implements INodeClient.GetMetricsAsync
            Return Await GetAsync(Of InstanceMetricsResponse)(
                $"/api/v1/instances/{instanceId}/metrics", cancellation)
        End Function

        ' ============================================================
        '  LOGS
        ' ============================================================

        Public Async Function GetLogsAsync(
                instanceId As String,
                lines As Integer,
                sourceId As String,
                cancellation As CancellationToken) As Task(Of InstanceLogsResponse) _
                Implements INodeClient.GetLogsAsync

            Dim query = $"?lines={lines}"
            If Not String.IsNullOrEmpty(sourceId) Then
                query &= $"&sourceId={Uri.EscapeDataString(sourceId)}"
            End If
            Return Await GetAsync(Of InstanceLogsResponse)(
                $"/api/v1/instances/{instanceId}/logs{query}", cancellation)
        End Function

        ' Streams log lines from the node's SSE endpoint.
        ' Returns IAsyncEnumerable - iterate with Await For Each.
        ' Dispose the enumerator (or cancel the token) to close the stream.
        '
        ' Usage example:
        '   Await client.StreamLogsAsync(id, -1, "",
        '       Sub(line) Console.WriteLine(line.Content), ct)
        Public Async Function StreamLogsAsync(
                instanceId As String,
                fromIndex As Long,
                sourceId As String,
                onLine As Action(Of LogLine),
                cancellation As CancellationToken) As Task _
                Implements INodeClient.StreamLogsAsync

            Await SseStreamInternalAsync(instanceId, fromIndex, sourceId, onLine, cancellation)
        End Function

        Private Async Function SseStreamInternalAsync(
                instanceId As String,
                fromIndex As Long,
                sourceId As String,
                onLine As Action(Of LogLine),
                cancellation As CancellationToken) As Task

            ' Build the SSE stream URL.
            Dim query = $"?fromIndex={fromIndex}"
            If Not String.IsNullOrEmpty(sourceId) Then
                query &= $"&sourceId={Uri.EscapeDataString(sourceId)}"
            End If
            Dim url = $"/api/v1/instances/{instanceId}/logs/stream{query}"

            ' Open the response without loading the body - we'll stream it.
            Using response = Await _http.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, cancellation)

                If Not response.IsSuccessStatusCode Then
                    Await ThrowApiException(response, cancellation)
                End If

                Using stream = Await response.Content.ReadAsStreamAsync(cancellation)
                Using reader As New IO.StreamReader(stream, Encoding.UTF8)
                    Dim line = Await reader.ReadLineAsync(cancellation)
                    Do While line IsNot Nothing AndAlso
                             Not cancellation.IsCancellationRequested

                        ' SSE event lines start with "data: "
                        ' Comment lines start with ": " (keepalives)
                        ' Blank lines separate events - we ignore them
                        If line.StartsWith("data: ") Then
                            Dim json = line.Substring(6)
                            Dim logLine As LogLine = Nothing
                            Try
                                logLine = JsonSerializer.Deserialize(Of LogLine)(
                                    json, _jsonOptions)
                            Catch ex As JsonException
                                _logger.LogWarning(
                                    "SSE: failed to parse log line JSON: {Json}", json)
                            End Try

                            If logLine IsNot Nothing Then
                                onLine(logLine)
                            End If
                        End If

                        line = Await reader.ReadLineAsync(cancellation)
                    Loop
                End Using
                End Using
            End Using
        End Function

        ' ============================================================
        '  STDIN
        ' ============================================================

        Public Async Function WriteStdinAsync(
                instanceId As String,
                request As StdinRequest,
                cancellation As CancellationToken) As Task(Of StdinResponse) _
                Implements INodeClient.WriteStdinAsync
            Return Await PostAsync(Of StdinRequest, StdinResponse)(
                $"/api/v1/instances/{instanceId}/stdin", request, cancellation)
        End Function

        ' ============================================================
        '  RCON
        ' ============================================================

        Public Async Function ConnectRconAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of RconConnectResponse) _
                Implements INodeClient.ConnectRconAsync
            Return Await PostAsync(Of Object, RconConnectResponse)(
                $"/api/v1/instances/{instanceId}/rcon/connect", Nothing, cancellation)
        End Function

        Public Async Function DisconnectRconAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of RconDisconnectResponse) _
                Implements INodeClient.DisconnectRconAsync
            Return Await PostAsync(Of Object, RconDisconnectResponse)(
                $"/api/v1/instances/{instanceId}/rcon/disconnect", Nothing, cancellation)
        End Function

        Public Async Function GetRconStatusAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of RconStatusResponse) _
                Implements INodeClient.GetRconStatusAsync
            Return Await GetAsync(Of RconStatusResponse)(
                $"/api/v1/instances/{instanceId}/rcon/status", cancellation)
        End Function

        Public Async Function SendRconAsync(
                instanceId As String,
                request As RconSendRequest,
                cancellation As CancellationToken) As Task(Of RconSendResponse) _
                Implements INodeClient.SendRconAsync
            Return Await PostAsync(Of RconSendRequest, RconSendResponse)(
                $"/api/v1/instances/{instanceId}/rcon/send", request, cancellation)
        End Function

        ' ============================================================
        '  INSTALLATIONS
        ' ============================================================

        Public Async Function GetInstallationStatusAsync(
                installationId As String,
                cancellation As CancellationToken) As Task(Of InstallationStatusResponse) _
                Implements INodeClient.GetInstallationStatusAsync
            Return Await GetAsync(Of InstallationStatusResponse)(
                $"/api/v1/installations/{installationId}/status", cancellation)
        End Function

        Public Async Function StartInstallAsync(
                request As InstallRequest,
                cancellation As CancellationToken) As Task(Of InstallOperationResponse) _
                Implements INodeClient.StartInstallAsync
            Return Await PostAsync(Of InstallRequest, InstallOperationResponse)(
                $"/api/v1/installations/{request.InstallationId}/install",
                request, cancellation)
        End Function

        Public Async Function StartUpdateAsync(
                request As UpdateRequest,
                cancellation As CancellationToken) As Task(Of InstallOperationResponse) _
                Implements INodeClient.StartUpdateAsync
            Return Await PostAsync(Of UpdateRequest, InstallOperationResponse)(
                $"/api/v1/installations/{request.InstallationId}/update",
                request, cancellation)
        End Function

        Public Async Function ValidateInstallAsync(
                installationId As String,
                cancellation As CancellationToken) As Task(Of ValidateInstallResponse) _
                Implements INodeClient.ValidateInstallAsync
            Return Await PostAsync(Of Object, ValidateInstallResponse)(
                $"/api/v1/installations/{installationId}/validate",
                Nothing, cancellation)
        End Function

        Public Async Function CancelInstallAsync(
                installationId As String,
                cancellation As CancellationToken) As Task(Of CancelInstallResponse) _
                Implements INodeClient.CancelInstallAsync
            Return Await PostAsync(Of Object, CancelInstallResponse)(
                $"/api/v1/installations/{installationId}/cancel",
                Nothing, cancellation)
        End Function

        ' ============================================================
        '  INSTALL PROMPTS
        ' ============================================================

        Public Async Function GetInstallPromptAsync(
                installationId As String,
                cancellation As CancellationToken) As Task(Of InstallPromptInfo) _
                Implements INodeClient.GetInstallPromptAsync
            Try
                Return Await GetAsync(Of InstallPromptInfo)(
                    $"/api/v1/installations/{installationId}/prompt", cancellation)
            Catch ex As NodeApiException When ex.ErrorCode = NodeErrorCodes.NoPromptWaiting
                Return Nothing
            End Try
        End Function

        Public Async Function RespondToInstallPromptAsync(
                installationId As String,
                request As RespondToPromptRequest,
                cancellation As CancellationToken) As Task(Of RespondToPromptResponse) _
                Implements INodeClient.RespondToInstallPromptAsync
            Return Await PostAsync(Of RespondToPromptRequest, RespondToPromptResponse)(
                $"/api/v1/installations/{installationId}/prompt", request, cancellation)
        End Function

        ' ============================================================
        '  SYSTEM
        ' ============================================================

        Public Async Function GetSystemInfoAsync(
                cancellation As CancellationToken) As Task(Of NodeSystemInfoResponse) _
                Implements INodeClient.GetSystemInfoAsync
            Return Await GetAsync(Of NodeSystemInfoResponse)(
                "/api/v1/system/info", cancellation)
        End Function

        Public Async Function GetDrivesAsync(
                cancellation As CancellationToken) As Task(Of NodeDrivesResponse) _
                Implements INodeClient.GetDrivesAsync
            Return Await GetAsync(Of NodeDrivesResponse)(
                "/api/v1/system/drives", cancellation)
        End Function

        ' ============================================================
        '  PRIVATE HTTP HELPERS
        '  Keep all the repetitive request/response boilerplate here
        '  so the endpoint methods above are concise.
        ' ============================================================

        Private Async Function GetAsync(Of TResponse As Class)(
                path As String,
                cancellation As CancellationToken) As Task(Of TResponse)
            Try
                Using response = Await _http.GetAsync(path, cancellation)
                    If response.IsSuccessStatusCode Then
                        Return Await DeserializeAsync(Of TResponse)(response, cancellation)
                    End If
                    Await ThrowApiException(response, cancellation)
                    Return Nothing  ' Unreachable - ThrowApiException always throws
                End Using
            Catch ex As HttpRequestException
                Dim errMsg = "Could not reach node: " & ex.Message
                Throw New NodeConnectionException(errMsg, ex)
            End Try
        End Function

        Private Async Function PostAsync(Of TRequest, TResponse As Class)(
                path As String,
                request As TRequest,
                cancellation As CancellationToken) As Task(Of TResponse)
            Try
                Dim content As HttpContent

                If request Is Nothing Then
                    content = New StringContent("{}", Encoding.UTF8, "application/json")
                Else
                    Dim json = JsonSerializer.Serialize(request, _jsonOptions)
                    content = New StringContent(json, Encoding.UTF8, "application/json")
                End If

                Using response = Await _http.PostAsync(path, content, cancellation)
                    If response.IsSuccessStatusCode Then
                        Return Await DeserializeAsync(Of TResponse)(response, cancellation)
                    End If
                    Await ThrowApiException(response, cancellation)
                    Return Nothing
                End Using
            Catch ex As HttpRequestException
                Dim errMsg = "Could not reach node: " & ex.Message
                Throw New NodeConnectionException(errMsg, ex)
            End Try
        End Function

        Private Async Function DeserializeAsync(Of T As Class)(
                response As HttpResponseMessage,
                cancellation As CancellationToken) As Task(Of T)
            Dim json = Await response.Content.ReadAsStringAsync(cancellation)
            Try
                Return JsonSerializer.Deserialize(Of T)(json, _jsonOptions)
            Catch ex As JsonException
                Dim errMsg = "Node returned invalid JSON: " & ex.Message
                Throw New NodeApiException(
                    "INVALID_RESPONSE",
                    errMsg,
                    CInt(response.StatusCode))
            End Try
        End Function

        ' Reads the error response body and throws NodeApiException.
        Private Async Function ThrowApiException(
                response As HttpResponseMessage,
                cancellation As CancellationToken) As Task

            Dim json = Await response.Content.ReadAsStringAsync(cancellation)
            Dim errorResponse As NodeErrorResponse = Nothing
            Try
                errorResponse = JsonSerializer.Deserialize(Of NodeErrorResponse)(json, _jsonOptions)
            Catch
            End Try

            Throw New NodeApiException(
                If(errorResponse?.ErrorCode, "HTTP_ERROR"),
                If(errorResponse?.Message, $"HTTP {CInt(response.StatusCode)}: {response.ReasonPhrase}"),
                CInt(response.StatusCode),
                errorResponse?.Details)
        End Function

    End Class


    ' ============================================================
    '  NODE HTTP CLIENT FACTORY
    '  Creates NodeHttpClient instances configured for a specific
    '  node. Registered as a singleton in DI. The manager calls
    '  GetClient(nodeId) to get a client for a specific node.
    ' ============================================================

    Public Class NodeHttpClientFactory

        Private ReadOnly _db As Data.GsmDbContext
        Private ReadOnly _credentials As CredentialService
        Private ReadOnly _logger As ILogger(Of NodeHttpClient)
        ' Cache clients so we don't create a new HttpClient per request.
        ' Key = NodeId.
        Private ReadOnly _cache As New System.Collections.Concurrent.ConcurrentDictionary(
            Of String, NodeHttpClient)(StringComparer.OrdinalIgnoreCase)

        Public Sub New(db As Data.GsmDbContext,
                       credentials As CredentialService,
                       logger As ILogger(Of NodeHttpClient))
            _db = db
            _credentials = credentials
            _logger = logger
        End Sub

        ' Returns a cached NodeHttpClient for the given node.
        ' Creates one on first call. Recreates it if the node config has changed.
        Public Async Function GetClientAsync(nodeId As String,
                                              cancellation As CancellationToken) As Task(Of NodeHttpClient)

            ' Check cache first.
            Dim cached As NodeHttpClient = Nothing
            If _cache.TryGetValue(nodeId, cached) Then Return cached

            ' Load node from DB.
            Dim node = Await _db.Nodes.FindAsync(
                New Object() {nodeId}, cancellation)
            If node Is Nothing Then
                Throw New InvalidOperationException($"Node '{nodeId}' not found in database.")
            End If

            ' Decrypt the auth token.
            Dim token = _credentials.DecryptString(node.AuthToken)

            ' Build the HttpClient.
            Dim http As New HttpClient() With {
                .BaseAddress = New Uri($"http://{node.Hostname}:{node.Port}"),
                .Timeout = TimeSpan.FromSeconds(30)
            }
            http.DefaultRequestHeaders.Authorization =
                New AuthenticationHeaderValue("Bearer", token)

            Dim client As New NodeHttpClient(http, _logger)
            _cache.TryAdd(nodeId, client)
            Return client
        End Function

        ' Call this when a node's hostname, port, or token changes
        ' to force the next call to GetClientAsync to rebuild.
        Public Sub InvalidateCache(nodeId As String)
            Dim removed As NodeHttpClient = Nothing
            _cache.TryRemove(nodeId, removed)
        End Sub

    End Class


    ' ============================================================
    '  EXCEPTIONS
    ' ============================================================

    ' Thrown when the node returns a non-2xx HTTP response.
    ' Contains the structured ErrorCode from NodeErrorResponse.
    Public Class NodeApiException
        Inherits Exception

        Public ReadOnly Property ErrorCode As String
        Public ReadOnly Property HttpStatusCode As Integer
        Public ReadOnly Property Details As String

        Public Sub New(errorCode As String,
                       message As String,
                       httpStatusCode As Integer,
                       Optional details As String = Nothing)
            MyBase.New(message)
            Me.ErrorCode = errorCode
            Me.HttpStatusCode = httpStatusCode
            Me.Details = details
        End Sub
    End Class

    ' Thrown when the HTTP request itself fails (network unreachable,
    ' connection refused, DNS failure, etc). Distinct from NodeApiException
    ' which means the node responded but with an error.
    Public Class NodeConnectionException
        Inherits Exception

        Public Sub New(message As String, innerException As Exception)
            MyBase.New(message, innerException)
        End Sub
    End Class

End Namespace
