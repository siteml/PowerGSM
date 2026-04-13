Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Http
Imports Microsoft.AspNetCore.Routing
Imports Microsoft.Extensions.DependencyInjection
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  InstanceEndpoints
'
'  Registers all /api/v1/instances/* routes.
'  Each handler is a short function that resolves the required
'  service from the DI container, calls it, and returns the
'  result. Business logic lives in ProcessManager and
'  RconClientManager - not here.
'
'  ASP.NET Core Minimal API pattern:
'    app.MapGet("/path/{param}", Function(param As String, service As MyService)
'        Return service.DoThing(param)
'    End Function)
'
'  Parameters declared in the lambda are injected automatically:
'    - Route parameters (e.g. {id}) by name match
'    - Services (ProcessManager, etc) from the DI container
'    - [FromBody] for request body deserialisation
'    - HttpContext for low-level access (used for SSE)
' ============================================================

Public Module InstanceEndpoints

    Public Sub Register(app As WebApplication)

        Dim api = app.MapGroup("/api/v1/instances")

        ' ---- List all instances ----
        api.MapGet("", Function(pm As ProcessManager) As NodeInstanceListResponse
            Dim instances = pm.GetAll()
            Return New NodeInstanceListResponse With {
                .Instances = instances.Select(Function(i) ToSummary(i)).ToList()
            }
        End Function)

        ' ---- Get one instance (full detail) ----
        api.MapGet("{id}", Function(id As String,
                                    pm As ProcessManager,
                                    db As NodeDatabase) As IResult

            Dim managed = pm.GetInstance(id)
            If managed Is Nothing Then
                Return Results.NotFound(New NodeErrorResponse With {
                    .ErrorCode = NodeErrorCodes.InstanceNotFound,
                    .Message = $"Instance '{id}' not found on this node."
                })
            End If

            Dim crashHistory = db.GetRecentCrashes(id,
                DateTime.UtcNow.AddDays(-1)).Take(20).ToList()

            Return Results.Ok(New NodeInstanceDetailResponse With {
                .InstanceId = managed.InstanceId,
                .DisplayName = managed.DisplayName,
                .GameId = managed.GameId,
                .State = managed.State,
                .CrashDetectionState = managed.CrashDetectionState,
                .RconState = managed.RconState,
                .PlayerCount = managed.PlayerCount,
                .Players = managed.Players.ToList(),
                .CustomMetrics = New Dictionary(Of String, String)(managed.CustomMetrics),
                .UptimeSeconds = If(managed.StartedAt.HasValue,
                                    CLng((DateTime.UtcNow - managed.StartedAt.Value).TotalSeconds),
                                    CType(Nothing, Long?)),
                .Pid = managed.Pid,
                .LastStateChangeAt = managed.LastStateChangeAt,
                .CrashHistory = crashHistory.Select(Function(c) New CrashEventSummary With {
                    .OccurredAt = c.OccurredAt,
                    .ExitCode = c.ExitCode,
                    .Decision = [Enum].Parse(GetType(RestartDecision), c.Decision),
                    .DecisionReason = c.DecisionReason,
                    .AttemptNumber = c.AttemptNumber
                }).ToList(),
                .StartupWarnings = If(managed.LastStartParams?.StartupWarnings?.ToList(),
                                   New List(Of String)())
            })
        End Function)

        ' ---- Start ----
        api.MapPost("{id}/start",
            Async Function(id As String,
                           request As StartInstanceRequest,
                           pm As ProcessManager,
                           rcon As RconClientManager,
                           cancellation As CancellationToken) As Task(Of IResult)

                ' Ensure the request InstanceId matches the route.
                request.InstanceId = id

                Dim response = Await pm.StartAsync(request, cancellation)

                ' Register RCON config if provided.
                If request.RconConfig IsNot Nothing Then
                    rcon.RegisterInstance(id, request.RconConfig)
                End If

                If response.State = InstanceState.StartFailed Then
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.InstanceStartFailed,
                        .Message = response.Message
                    })
                End If

                Return Results.Ok(response)
            End Function)

        ' ---- Stop ----
        api.MapPost("{id}/stop",
            Async Function(id As String,
                           request As StopInstanceRequest,
                           pm As ProcessManager,
                           rcon As RconClientManager,
                           cancellation As CancellationToken) As Task(Of IResult)

                Dim managed = pm.GetInstance(id)
                If managed Is Nothing Then
                    Return Results.NotFound(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.InstanceNotFound,
                        .Message = $"Instance '{id}' not found."
                    })
                End If

                ' Disconnect RCON before stopping.
                Await rcon.UnregisterInstanceAsync(id)

                Dim response = Await pm.StopAsync(id,
                    request.Graceful,
                    request.GracefulTimeoutMs,
                    cancellation)

                Return Results.Ok(response)
            End Function)

        ' ---- Restart ----
        api.MapPost("{id}/restart",
            Async Function(id As String,
                           request As RestartInstanceRequest,
                           pm As ProcessManager,
                           rcon As RconClientManager,
                           cancellation As CancellationToken) As Task(Of IResult)

                Dim managed = pm.GetInstance(id)
                If managed Is Nothing Then
                    Return Results.NotFound(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.InstanceNotFound,
                        .Message = $"Instance '{id}' not found."
                    })
                End If

                ' Stop first.
                Await rcon.UnregisterInstanceAsync(id)
                Await pm.StopAsync(id, request.Graceful,
                                   request.GracefulTimeoutMs, cancellation)

                ' Wait for the process to actually exit.
                Dim waited = 0
                Do While pm.GetInstance(id)?.State = InstanceState.Stopping AndAlso
                         waited < request.GracefulTimeoutMs + 5000
                    Await Task.Delay(250, cancellation)
                    waited += 250
                Loop

                ' Use updated params if provided, otherwise re-use last known params.
                Dim startParams = If(request.UpdatedStartParams,
                                     managed.LastStartParams)
                If startParams Is Nothing Then
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.InstanceStartFailed,
                        .Message = "No start parameters available for restart."
                    })
                End If

                Dim startResponse = Await pm.StartAsync(startParams, cancellation)

                If startResponse.State = InstanceState.StartFailed Then
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.InstanceStartFailed,
                        .Message = startResponse.Message
                    })
                End If

                If startParams.RconConfig IsNot Nothing Then
                    rcon.RegisterInstance(id, startParams.RconConfig)
                End If

                Return Results.Ok(New RestartInstanceResponse With {
                    .InstanceId = id,
                    .State = startResponse.State,
                    .Message = "Restarted."
                })
            End Function)

        ' ---- Force kill ----
        api.MapPost("{id}/kill",
            Async Function(id As String,
                           pm As ProcessManager,
                           rcon As RconClientManager,
                           cancellation As CancellationToken) As Task(Of IResult)

                Await rcon.UnregisterInstanceAsync(id)
                Dim response = Await pm.KillAsync(id, cancellation)
                Return Results.Ok(response)
            End Function)

        ' ---- Metrics ----
        api.MapGet("{id}/metrics",
            Function(id As String,
                     pm As ProcessManager,
                     rcon As RconClientManager) As IResult

                Dim metrics = pm.GetMetrics(id)
                If metrics Is Nothing Then
                    Return Results.NotFound(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.InstanceNotFound,
                        .Message = $"Instance '{id}' not found."
                    })
                End If

                ' Enrich with current RCON state.
                metrics.RconState = rcon.GetState(id)

                Return Results.Ok(metrics)
            End Function)

        ' ---- Recent logs ----
        api.MapGet("{id}/logs",
            Function(id As String,
                     ringBuffer As RingBufferStore,
                     context As HttpContext) As IResult

                Dim lines As Integer
                If Not Integer.TryParse(context.Request.Query("lines").ToString(), lines) Then
                    lines = 200
                End If
                lines = Math.Min(lines, 5000)

                Dim sourceId = context.Request.Query("since").ToString()
                Dim sinceStr = context.Request.Query("since").ToString()
                Dim sourceFilter = context.Request.Query("sourceId").ToString()

                Dim allLines = ringBuffer.GetRecent(id, lines, sourceFilter)

                ' Apply since filter if provided.
                Dim since As DateTime
                If DateTime.TryParse(sinceStr, since) Then
                    allLines = allLines.Where(Function(l) l.Timestamp > since).ToList()
                End If

                Return Results.Ok(New InstanceLogsResponse With {
                    .InstanceId = id,
                    .Lines = allLines.Select(Function(l) New LogLine With {
                        .LineIndex = l.LineIndex,
                        .SourceId = l.SourceId,
                        .Timestamp = l.Timestamp,
                        .Content = l.Content
                    }).ToList()
                })
            End Function)

        ' ---- SSE log stream ----
        ' Streams log lines as Server-Sent Events.
        ' The client connects and receives a continuous stream of events
        ' until it disconnects. Each event is a JSON-serialised LogLine.
        '
        ' Query params:
        '   fromIndex = start streaming from this ring buffer position
        '               -1 (default) = live tail only
        '   sourceId  = filter to specific source, empty = all
        api.MapGet("{id}/logs/stream",
            Async Function(id As String,
                           ringBuffer As RingBufferStore,
                           context As HttpContext,
                           cancellation As CancellationToken) As Task

                Dim fromIndex As Long
                If Not Long.TryParse(context.Request.Query("fromIndex").ToString(), fromIndex) Then
                    fromIndex = -1L
                End If
                Dim sourceFilter = context.Request.Query("sourceId").ToString()

                ' SSE requires specific headers.
                context.Response.Headers("Content-Type") = "text/event-stream"
                context.Response.Headers("Cache-Control") = "no-cache"
                context.Response.Headers("X-Accel-Buffering") = "no"  ' Disable nginx buffering

                Dim writer = context.Response.BodyWriter

                ' Helper: write one SSE event.
                Dim WriteEvent = Async Function(line As BufferedLogLine) As Task
                    If Not String.IsNullOrEmpty(sourceFilter) AndAlso
                       line.SourceId <> sourceFilter Then Return

                    Dim json = JsonSerializer.Serialize(New LogLine With {
                        .LineIndex = line.LineIndex,
                        .SourceId = line.SourceId,
                        .Timestamp = line.Timestamp,
                        .Content = line.Content
                    })
                    Dim eventText = $"data: {json}{vbCrLf}{vbCrLf}"
                    Dim bytes = Encoding.UTF8.GetBytes(eventText)
                    Await writer.WriteAsync(bytes, cancellation)
                    Await writer.FlushAsync(cancellation)
                End Function

                ' Send historical lines first if fromIndex specified.
                If fromIndex >= 0 Then
                    Dim historical = Await ringBuffer.GetFromIndexAsync(
                        id, fromIndex, cancellation)
                    For Each line In historical
                        Await WriteEvent(line)
                    Next
                End If

                ' Subscribe to live lines.
                Using subscription = ringBuffer.Subscribe(id,
                    Sub(line)
                        Try
                            WriteEvent(line).GetAwaiter().GetResult()
                        Catch
                            ' Client disconnected - subscription will be cleaned up
                        End Try
                    End Sub)

                    ' Keep the connection alive with periodic keep-alives.
                    ' Proxies will close idle SSE connections otherwise.
                    Do While Not cancellation.IsCancellationRequested
                        Try
                            Await Task.Delay(15000, cancellation)
                            Dim keepAlive = Encoding.UTF8.GetBytes($": keepalive{vbCrLf}{vbCrLf}")
                            Await writer.WriteAsync(keepAlive, cancellation)
                            Await writer.FlushAsync(cancellation)
                        Catch ex As OperationCanceledException
                            Exit Do
                        Catch
                            Exit Do  ' Client disconnected
                        End Try
                    Loop
                End Using
            End Function)

        ' ---- Stdin ----
        api.MapPost("{id}/stdin",
            Function(id As String,
                     request As StdinRequest,
                     pm As ProcessManager) As IResult

                Dim response = pm.WriteStdin(id, request.Line, request.IsSensitive)
                If Not response.Accepted Then
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.StdinNotAvailable,
                        .Message = response.Message
                    })
                End If
                Return Results.Ok(response)
            End Function)

        ' ---- RCON connect ----
        api.MapPost("{id}/rcon/connect",
            Async Function(id As String,
                           rcon As RconClientManager,
                           cancellation As CancellationToken) As Task(Of IResult)

                Dim response = Await rcon.ConnectAsync(id, cancellation)
                If response.RconState = RconState.NotAvailable Then
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.RconNotConfigured,
                        .Message = response.Message
                    })
                End If
                Return Results.Ok(response)
            End Function)

        ' ---- RCON disconnect ----
        api.MapPost("{id}/rcon/disconnect",
            Async Function(id As String,
                           rcon As RconClientManager) As Task(Of IResult)

                Dim response = Await rcon.DisconnectAsync(id)
                Return Results.Ok(response)
            End Function)

        ' ---- RCON status ----
        api.MapGet("{id}/rcon/status",
            Function(id As String, rcon As RconClientManager) As IResult
                Dim response = rcon.GetStatus(id)
                Return Results.Ok(response)
            End Function)

        ' ---- RCON send ----
        api.MapPost("{id}/rcon/send",
            Async Function(id As String,
                           request As RconSendRequest,
                           rcon As RconClientManager,
                           cancellation As CancellationToken) As Task(Of IResult)

                Dim status = rcon.GetStatus(id)
                If status.RconState = RconState.NotAvailable Then
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.RconNotConfigured,
                        .Message = "RCON is not configured for this instance."
                    })
                End If
                If status.RconState <> RconState.Connected Then
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.RconNotConnected,
                        .Message = $"RCON is not connected (state: {status.RconState})."
                    })
                End If

                Dim response = Await rcon.SendAsync(id, request, cancellation)
                If Not response.Success Then
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.RconCommandFailed,
                        .Message = response.ErrorMessage
                    })
                End If
                Return Results.Ok(response)
            End Function)

    End Sub


    ' ============================================================
    '  MAPPING HELPERS
    ' ============================================================

    Private Function ToSummary(i As ManagedInstance) As NodeInstanceSummary
        Return New NodeInstanceSummary With {
            .InstanceId = i.InstanceId,
            .DisplayName = i.DisplayName,
            .GameId = i.GameId,
            .State = i.State,
            .RconState = i.RconState,
            .PlayerCount = i.PlayerCount,
            .UptimeSeconds = If(i.StartedAt.HasValue,
                                CLng((DateTime.UtcNow - i.StartedAt.Value).TotalSeconds),
                                CType(Nothing, Long?)),
            .Pid = i.Pid,
            .LastStateChangeAt = i.LastStateChangeAt,
            .CrashCountInWindow = 0,   ' Filled from DB only on full detail endpoint
            .InstallationId = i.InstallationId
        }
    End Function

End Module
