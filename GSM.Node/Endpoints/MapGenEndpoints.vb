Imports System
Imports System.Net.Http.Json
Imports System.Threading.Tasks
Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Http
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Node.Api

' ============================================================
'  Map generation endpoint
'
'  Phase 4c-3. Single endpoint that runs a plugin-supplied step
'  list against an instance's install directory to produce a
'  new save file. Synchronous: the request blocks until every
'  step completes or the per-request timeout fires.
'
'  Like FileEndpoints, the manager owns the bookkeeping — the
'  request body carries InstallPath plus the typed step list
'  the manager-side plugin built. The node treats steps as
'  opaque DTOs, validates the supported subset (WriteFileStep,
'  RunProcessStep), and executes via MapGenerationRunner.
'
'  The instance id segment in the URL is purely organisational
'  (mirrors FileEndpoints) — execution is keyed off InstallPath,
'  so map gen works whether the instance is running or not.
' ============================================================

Namespace GSM.Node.Endpoints

    Module MapGenEndpoints

        Public Sub Map(app As WebApplication)

            ' Run the plugin-supplied steps to produce a new save.
            app.MapPost("/api/instances/{instanceId}/generate-map",
                Async Function(instanceId As String,
                               context As HttpContext) As Task(Of IResult)
                    Return Await GenerateMap(instanceId, context)
                End Function)

        End Sub

        Private Async Function GenerateMap(instanceId As String,
                                            context As HttpContext) As Task(Of IResult)
            Dim request As GenerateMapRequest = Nothing
            Try
                request = Await context.Request.ReadFromJsonAsync(Of GenerateMapRequest)()
            Catch ex As Exception
                Return Results.BadRequest(New With {.error = $"Malformed request body: {ex.Message}"})
            End Try

            If request Is Nothing Then
                Return Results.BadRequest(New With {.error = "Missing request body"})
            End If
            If String.IsNullOrEmpty(request.InstallPath) Then
                Return Results.BadRequest(New With {.error = "InstallPath is required"})
            End If

            Dim runner = context.RequestServices.
                GetRequiredService(Of GSM.Node.MapGenerationRunner)()

            Dim result = Await runner.RunAsync(
                instanceId,
                request.InstallPath,
                request,
                context.RequestAborted)

            ' MapGenerationRunner never throws — every error path
            ' produces a populated GenerateMapResponse with
            ' Success=False. We use 200 even on failure so the
            ' manager can read the structured ErrorMessage /
            ' FailedStepIndex rather than the generic "request
            ' failed" wrapping that EnsureSuccessStatusCode would
            ' produce on a non-2xx.
            Return Results.Ok(result)
        End Function

    End Module

End Namespace
