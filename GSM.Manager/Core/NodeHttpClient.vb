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
