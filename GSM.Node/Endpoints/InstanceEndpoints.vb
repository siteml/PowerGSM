Imports System
Imports System.Threading
Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Http
Imports GSM.Node.Api
Imports GSM.Node

' ============================================================
'  Instance endpoints — start/stop/status, RCON, log streaming
' ============================================================

Namespace GSM.Node.Endpoints

    Module InstanceEndpoints

        Public Sub Map(app As WebApplication)

            ' ---- Instance lifecycle ----

            app.MapPost("/api/instances/start",
                Async Function(request As StartInstanceRequest,
                               pm As ProcessManager) As Task(Of IResult)
                    Dim result = Await pm.StartInstanceAsync(request)
                    Return Results.Ok(result)
                End Function)

            app.MapPost("/api/instances/stop",
                Async Function(request As StopInstanceRequest,
                               pm As ProcessManager) As Task(Of IResult)
                    Dim result = Await pm.StopInstanceAsync(request)
                    Return Results.Ok(result)
                End Function)

            app.MapGet("/api/instances/{instanceId}/status",
                Function(instanceId As String,
                         pm As ProcessManager) As IResult
                    Dim result = pm.GetInstanceStatus(instanceId)
                    If result Is Nothing Then
                        Return Results.NotFound(New With {
                            .error = "Instance not found"
                        })
                    End If
                    Return Results.Ok(result)
                End Function)

            app.MapGet("/api/instances",
                Function(pm As ProcessManager) As IResult
                    Return Results.Ok(pm.GetAllInstanceStatuses())
                End Function)

            ' ---- RCON ----

            app.MapPost("/api/instances/{instanceId}/rcon/connect",
                Async Function(instanceId As String,
                               context As HttpContext,
                               rm As RconClientManager) As Task(Of IResult)

                    ' Expect JSON body with host, port, password, protocol
                    Dim body = Await context.Request.ReadFromJsonAsync(Of RconConnectRequest)()
                    If body Is Nothing Then
                        Return Results.BadRequest(New With {.error = "Invalid request body"})
                    End If

                    Dim ok = Await rm.ConnectAsync(instanceId,
                                                   body.Host, body.Port,
                                                   body.Password,
                                                   body.Protocol,
                                                   context.RequestAborted)
                    If ok Then
                        Return Results.Ok(New With {.connected = True})
                    End If
                    Return Results.Ok(New With {.connected = False,
                                                .error = "Authentication failed"})
                End Function)

            app.MapPost("/api/instances/{instanceId}/rcon/command",
                Async Function(instanceId As String,
                               request As RconCommandRequest,
                               rm As RconClientManager) As Task(Of IResult)
                    ' Override instanceId from route
                    request.InstanceId = instanceId
                    Dim result = Await rm.SendCommandAsync(instanceId, request.Command,
                                                           CancellationToken.None)
                    Return Results.Ok(result)
                End Function)

            app.MapGet("/api/instances/{instanceId}/rcon/status",
                Function(instanceId As String,
                         rm As RconClientManager) As IResult
                    Return Results.Ok(rm.GetStatus(instanceId))
                End Function)

            app.MapPost("/api/instances/{instanceId}/rcon/disconnect",
                Async Function(instanceId As String,
                               rm As RconClientManager) As Task(Of IResult)
                    Await rm.DisconnectAsync(instanceId)
                    Return Results.Ok(New With {.disconnected = True})
                End Function)

            ' ---- Log streaming (SSE) ----

            app.MapGet("/api/instances/{instanceId}/logs",
                Async Function(instanceId As String,
                               context As HttpContext,
                               logStore As RingBufferStore) As Task

                    context.Response.ContentType = "text/event-stream"
                    context.Response.Headers("Cache-Control") = "no-cache"
                    context.Response.Headers("Connection") = "keep-alive"

                    ' Parse optional query param for tail count
                    Dim tailStr = context.Request.Query("tail").ToString()
                    Dim tailCount = 100
                    Integer.TryParse(tailStr, tailCount)

                    Await logStore.StreamToResponseAsync(
                        instanceId,
                        context.Response,
                        tailCount,
                        context.RequestAborted)
                End Function)

            app.MapGet("/api/instances/{instanceId}/logs/recent",
                Function(instanceId As String,
                         context As HttpContext,
                         logStore As RingBufferStore) As IResult
                    Dim countStr = context.Request.Query("count").ToString()
                    Dim count = 100
                    Integer.TryParse(countStr, count)
                    Return Results.Ok(logStore.GetTail(instanceId, count))
                End Function)

        End Sub

    End Module

    ' ============================================================
    '  Helper DTO for RCON connect (not in contracts because
    '  it's node-internal, not sent by the manager)
    ' ============================================================

    Friend Class RconConnectRequest
        Public Property Host As String
        Public Property Port As Integer
        Public Property Password As String
        Public Property Protocol As GSM.Plugin.RconProtocol
    End Class

End Namespace
