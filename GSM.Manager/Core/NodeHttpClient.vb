Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Net.Http
Imports System.Net.Http.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  NodeHttpClient — implements INodeClient over HTTP
'
'  Each node connection gets its own HttpClient instance with
'  the auth token pre-configured. The factory manages the pool.
'
'  Log streaming uses SSE (Server-Sent Events) consumed via
'  StreamReader with a callback Action(Of LogLine) — not
'  IAsyncEnumerable.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>
    ''' HTTP-based implementation of INodeClient.
    ''' One instance per node, created by NodeHttpClientFactory.
    ''' </summary>
    Public Class NodeHttpClient
        Implements INodeClient

        Private ReadOnly _httpClient As HttpClient
        Private ReadOnly _baseUrl As String
        Private ReadOnly _logger As ILogger(Of NodeHttpClient)

        ' Phase 5f-2 — in-memory cache of the last successful
        ' /api/version response. Hits are silent (no log spam),
        ' misses re-query. The cache is per-client-instance and
        ' lives as long as the factory keeps the client alive
        ' (see NodeHttpClientFactory: clients are evicted only on
        ' RemoveClient calls). Lock guards the read-modify-write
        ' against concurrent callers — the panel may issue an
        ' on-load fetch while a background poller is also
        ' refreshing.
        Private _cachedVersion As NodeVersionResponse
        Private ReadOnly _versionLock As New Object()

        Public Sub New(hostAddress As String, port As Integer,
                       authToken As String,
                       logger As ILogger(Of NodeHttpClient))
            _baseUrl = $"http://{hostAddress}:{port}"
            _logger = logger

            _httpClient = New HttpClient()
            _httpClient.BaseAddress = New Uri(_baseUrl)
            _httpClient.Timeout = TimeSpan.FromSeconds(30)
            If Not String.IsNullOrEmpty(authToken) Then
                _httpClient.DefaultRequestHeaders.Authorization =
                    New System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken)
            End If
        End Sub

        ' ---- Node ----

        Public Async Function GetStatusAsync(cancellation As CancellationToken) As Task(Of NodeStatusResponse) Implements INodeClient.GetStatusAsync
            Try
                Return Await _httpClient.GetFromJsonAsync(Of NodeStatusResponse)(
                    "/api/status", cancellation)
            Catch ex As Exception
                Throw WrapException("GetStatus", ex)
            End Try
        End Function

        Public Async Function AuthenticateAsync(request As NodeAuthRequest,
                                                cancellation As CancellationToken) As Task(Of NodeAuthResponse) Implements INodeClient.AuthenticateAsync
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync("/api/auth", request, cancellation)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of NodeAuthResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("Authenticate", ex)
            End Try
        End Function

        ''' <summary>
        ''' Hit the unauthenticated /api/version endpoint and
        ''' return the node's identity + version axes. Caches the
        ''' result in-memory so a typical UI flow (panel opens,
        ''' fetches version, renders compat indicator) doesn't
        ''' re-hit the wire on every navigation. Pass force=True
        ''' to bypass the cache — e.g. when the user has reason
        ''' to believe the node was upgraded out from under us.
        '''
        ''' Failure modes are reported as NodeConnectionException
        ''' (network/host issue) or NodeApiException (HTTP-level
        ''' problem) via WrapException, so callers can disambiguate
        ''' "node is offline" from "node returned 500". The cache
        ''' is populated only on success, so a transient failure
        ''' doesn't poison subsequent calls.
        ''' </summary>
        Public Async Function GetApiVersionAsync(force As Boolean,
                                                  cancellation As CancellationToken) As Task(Of NodeVersionResponse) Implements INodeClient.GetApiVersionAsync
            If Not force Then
                SyncLock _versionLock
                    If _cachedVersion IsNot Nothing Then Return _cachedVersion
                End SyncLock
            End If
            Try
                Dim fresh = Await _httpClient.GetFromJsonAsync(Of NodeVersionResponse)(
                    "/api/version", cancellation)
                If fresh IsNot Nothing Then
                    SyncLock _versionLock
                        _cachedVersion = fresh
                    End SyncLock
                End If
                Return fresh
            Catch ex As Exception
                Throw WrapException("GetApiVersion", ex)
            End Try
        End Function

        ''' <summary>
        ''' Synchronous accessor for the in-memory version cache.
        ''' Returns Nothing until GetApiVersionAsync has succeeded
        ''' at least once on this client. Used by feature-gating
        ''' logic that needs to make decisions without an awaited
        ''' round trip — e.g. a button's Enabled state computed at
        ''' menu-render time.
        ''' </summary>
        Public Function TryGetCachedVersion() As NodeVersionResponse
            SyncLock _versionLock
                Return _cachedVersion
            End SyncLock
        End Function

        ' ---- Instance lifecycle ----

        Public Async Function StartInstanceAsync(request As StartInstanceRequest,
                                                  cancellation As CancellationToken) As Task(Of InstanceStatusResponse) Implements INodeClient.StartInstanceAsync
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync("/api/instances/start", request, cancellation)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of InstanceStatusResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("StartInstance", ex)
            End Try
        End Function

        Public Async Function StopInstanceAsync(request As StopInstanceRequest,
                                                 cancellation As CancellationToken) As Task(Of InstanceStatusResponse) Implements INodeClient.StopInstanceAsync
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync("/api/instances/stop", request, cancellation)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of InstanceStatusResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("StopInstance", ex)
            End Try
        End Function

        Public Async Function GetInstanceStatusAsync(instanceId As String,
                                                      cancellation As CancellationToken) As Task(Of InstanceStatusResponse) Implements INodeClient.GetInstanceStatusAsync
            Try
                Return Await _httpClient.GetFromJsonAsync(Of InstanceStatusResponse)(
                    $"/api/instances/{instanceId}/status", cancellation)
            Catch ex As Exception
                Throw WrapException("GetInstanceStatus", ex)
            End Try
        End Function

        Public Async Function GetAllInstanceStatusesAsync(cancellation As CancellationToken) As Task(Of IReadOnlyList(Of InstanceStatusResponse)) Implements INodeClient.GetAllInstanceStatusesAsync
            Try
                Dim result = Await _httpClient.GetFromJsonAsync(Of List(Of InstanceStatusResponse))(
                    "/api/instances", cancellation)
                Return If(result, New List(Of InstanceStatusResponse))
            Catch ex As Exception
                Throw WrapException("GetAllInstanceStatuses", ex)
            End Try
        End Function

        Public Async Function GetRecentLogsAsync(instanceId As String,
                                                  count As Integer,
                                                  cancellation As CancellationToken) As Task(Of IReadOnlyList(Of LogLine)) Implements INodeClient.GetRecentLogsAsync
            Try
                Dim result = Await _httpClient.GetFromJsonAsync(Of List(Of LogLine))(
                    $"/api/instances/{instanceId}/logs/recent?count={count}", cancellation)
                Return If(result, New List(Of LogLine))
            Catch ex As Exception
                Throw WrapException("GetRecentLogs", ex)
            End Try
        End Function

        Public Async Function GetPlayersAsync(instanceId As String,
                                                cancellation As CancellationToken) As Task(Of IReadOnlyList(Of PlayerSession)) Implements INodeClient.GetPlayersAsync
            Try
                Dim result = Await _httpClient.GetFromJsonAsync(Of List(Of PlayerSession))(
                    $"/api/instances/{instanceId}/players", cancellation)
                Return If(result, New List(Of PlayerSession))
            Catch ex As Exception
                Throw WrapException("GetPlayers", ex)
            End Try
        End Function

        Public Async Function GetServerStateAsync(instanceId As String,
                                                    cancellation As CancellationToken) As Task(Of ServerStateResponse) Implements INodeClient.GetServerStateAsync
            Try
                Return Await _httpClient.GetFromJsonAsync(Of ServerStateResponse)(
                    $"/api/instances/{instanceId}/server-state", cancellation)
            Catch ex As Exception
                Throw WrapException("GetServerState", ex)
            End Try
        End Function

        Public Async Function GetChatHistoryAsync(instanceId As String,
                                                    sinceUtc As DateTime?,
                                                    limit As Integer,
                                                    cancellation As CancellationToken) As Task(Of IReadOnlyList(Of ChatMessage)) Implements INodeClient.GetChatHistoryAsync
            Try
                Dim url = $"/api/instances/{instanceId}/chat?limit={limit}"
                If sinceUtc.HasValue Then
                    url &= "&since=" & Uri.EscapeDataString(sinceUtc.Value.ToString("o"))
                End If
                Dim result = Await _httpClient.GetFromJsonAsync(Of List(Of ChatMessage))(url, cancellation)
                Return If(result, New List(Of ChatMessage))
            Catch ex As Exception
                Throw WrapException("GetChatHistory", ex)
            End Try
        End Function

        ' ---- Installation ----

        Public Async Function StartInstallAsync(request As InstallRequest,
                                                 cancellation As CancellationToken) As Task(Of InstallProgressResponse) Implements INodeClient.StartInstallAsync
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync("/api/install", request, cancellation)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of InstallProgressResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("StartInstall", ex)
            End Try
        End Function

        Public Async Function CheckAppVersionAsync(request As AppVersionCheckRequest,
                                                     cancellation As CancellationToken) As Task(Of AppVersionCheckResponse) Implements INodeClient.CheckAppVersionAsync
            Try
                ' Version check can take a while because it runs
                ' SteamCMD app_info_print. Give it a generous timeout.
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
                    cts.CancelAfter(TimeSpan.FromMinutes(3))
                    Dim resp = Await _httpClient.PostAsJsonAsync("/api/install/version-check",
                                                                   request, cts.Token)
                    resp.EnsureSuccessStatusCode()
                    Return Await resp.Content.ReadFromJsonAsync(Of AppVersionCheckResponse)(cancellationToken:=cts.Token)
                End Using
            Catch ex As Exception
                Throw WrapException("CheckAppVersion", ex)
            End Try
        End Function

        Public Async Function GetInstallProgressAsync(installationId As String,
                                                       cancellation As CancellationToken) As Task(Of InstallProgressResponse) Implements INodeClient.GetInstallProgressAsync
            Try
                Return Await _httpClient.GetFromJsonAsync(Of InstallProgressResponse)(
                    $"/api/install/{installationId}/progress", cancellation)
            Catch ex As Exception
                Throw WrapException("GetInstallProgress", ex)
            End Try
        End Function

        Public Async Function CancelInstallAsync(installationId As String,
                                                  cancellation As CancellationToken) As Task(Of Boolean) Implements INodeClient.CancelInstallAsync
            Try
                Dim resp = Await _httpClient.PostAsync(
                    $"/api/install/{installationId}/cancel", Nothing, cancellation)
                Return resp.IsSuccessStatusCode
            Catch ex As Exception
                Throw WrapException("CancelInstall", ex)
            End Try
        End Function

        ' ---- RCON ----

        Public Async Function SendRconCommandAsync(request As RconCommandRequest,
                                                    cancellation As CancellationToken) As Task(Of RconCommandResponse) Implements INodeClient.SendRconCommandAsync
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync(
                    $"/api/instances/{request.InstanceId}/rcon/command", request, cancellation)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of RconCommandResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("SendRconCommand", ex)
            End Try
        End Function

        Public Async Function GetRconStatusAsync(instanceId As String,
                                                  cancellation As CancellationToken) As Task(Of RconStatusResponse) Implements INodeClient.GetRconStatusAsync
            Try
                Return Await _httpClient.GetFromJsonAsync(Of RconStatusResponse)(
                    $"/api/instances/{instanceId}/rcon/status", cancellation)
            Catch ex As Exception
                Throw WrapException("GetRconStatus", ex)
            End Try
        End Function

        ' ---- Log streaming (callback-based) ----

        Public Async Function StreamLogsAsync(instanceId As String,
                                               onLine As Action(Of LogLine),
                                               cancellation As CancellationToken) As Task Implements INodeClient.StreamLogsAsync
            Try
                Dim requestMsg As New HttpRequestMessage(HttpMethod.Get,
                    $"/api/instances/{instanceId}/logs")

                Using response = Await _httpClient.SendAsync(requestMsg,
                        HttpCompletionOption.ResponseHeadersRead, cancellation)
                    response.EnsureSuccessStatusCode()

                    Using stream = Await response.Content.ReadAsStreamAsync(cancellation)
                        Using reader As New StreamReader(stream)
                            While Not cancellation.IsCancellationRequested
                                Dim sseData = Await reader.ReadLineAsync(cancellation)
                                If sseData Is Nothing Then Exit While

                                If sseData.StartsWith("data: ") Then
                                    Dim json = sseData.Substring(6)
                                    ' Parse the JSON log line
                                    Try
                                        Dim parsed = System.Text.Json.JsonSerializer.Deserialize(Of SseLogLine)(json)
                                        If parsed IsNot Nothing Then
                                            Dim logLine As New LogLine With {
                                                .Timestamp = If(parsed.timestamp <> DateTime.MinValue,
                                                                parsed.timestamp, DateTime.UtcNow),
                                                .Text = If(parsed.text, ""),
                                                .IsError = parsed.isError,
                                                .SourceId = "sse"
                                            }
                                            onLine(logLine)
                                        End If
                                    Catch
                                        ' Skip malformed SSE data
                                    End Try
                                End If
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As OperationCanceledException
                ' Normal — caller cancelled the stream
            Catch ex As Exception
                _logger.LogWarning(ex, "Log stream disconnected for {InstanceId}", instanceId)
            End Try
        End Function

        ' ---- Interactive prompts ----

        Public Async Function RespondToPromptAsync(response As PromptResponse,
                                                    cancellation As CancellationToken) As Task(Of Boolean) Implements INodeClient.RespondToPromptAsync
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync(
                    $"/api/install/{response.OperationId}/prompt", response, cancellation)
                Return resp.IsSuccessStatusCode
            Catch ex As Exception
                Throw WrapException("RespondToPrompt", ex)
            End Try
        End Function

        Public Async Function UninstallAsync(request As UninstallRequest,
                                              cancellation As CancellationToken) As Task(Of Boolean) Implements INodeClient.UninstallAsync
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync(
                    "/api/install/uninstall", request, cancellation)
                Return resp.IsSuccessStatusCode
            Catch ex As Exception
                Throw WrapException("Uninstall", ex)
            End Try
        End Function

        ' ---- Helpers ----

        Private Function WrapException(operation As String, ex As Exception) As Exception
            If TypeOf ex Is HttpRequestException Then
                Return New NodeConnectionException(
                    $"Connection failed during {operation}: {ex.Message}", ex)
            End If
            Return New NodeApiException(
                $"API error during {operation}: {ex.Message}", ex)
        End Function

    End Class

    ''' <summary>
    ''' Helper class for deserializing SSE log line JSON.
    ''' </summary>
    Friend Class SseLogLine
        Public Property timestamp As DateTime
        Public Property text As String
        Public Property isError As Boolean
        Public Property seq As Long
    End Class

    ' ============================================================
    '  NodeHttpClientFactory — manages client instances per node
    ' ============================================================

    Public Class NodeHttpClientFactory

        Private ReadOnly _clients As New ConcurrentDictionary(Of String, NodeHttpClient)
        Private ReadOnly _logger As ILogger(Of NodeHttpClient)

        Public Sub New(logger As ILogger(Of NodeHttpClient))
            _logger = logger
        End Sub

        ''' <summary>
        ''' Gets or creates an INodeClient for the given node.
        ''' </summary>
        Public Function GetClient(nodeId As String,
                                  hostAddress As String,
                                  port As Integer,
                                  authToken As String) As INodeClient
            Return _clients.GetOrAdd(nodeId,
                Function(id) New NodeHttpClient(hostAddress, port, authToken, _logger))
        End Function

        ''' <summary>
        ''' Removes and disposes the client for a node (e.g. when
        ''' node is removed or auth token changes).
        ''' </summary>
        Public Sub RemoveClient(nodeId As String)
            Dim removed As NodeHttpClient = Nothing
            _clients.TryRemove(nodeId, removed)
        End Sub

    End Class

    ' ============================================================
    '  Exceptions
    ' ============================================================

    ''' <summary>
    ''' Thrown when a node REST API call returns an error.
    ''' </summary>
    Public Class NodeApiException
        Inherits Exception

        Public Sub New(message As String, Optional inner As Exception = Nothing)
            MyBase.New(message, inner)
        End Sub
    End Class

    ''' <summary>
    ''' Thrown when the node cannot be reached (network error).
    ''' </summary>
    Public Class NodeConnectionException
        Inherits Exception

        Public Sub New(message As String, Optional inner As Exception = Nothing)
            MyBase.New(message, inner)
        End Sub
    End Class

End Namespace