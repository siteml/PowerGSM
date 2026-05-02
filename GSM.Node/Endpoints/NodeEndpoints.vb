Imports System
Imports System.IO
Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Http
Imports Microsoft.Extensions.Logging
Imports GSM.Node.Api
Imports GSM.Node
Imports GSM.Node.Security

' ============================================================
'  System endpoints — version, status, auth
'  Install endpoints — start/cancel/progress, prompt response
' ============================================================

Namespace GSM.Node.Endpoints

    ''' <summary>
    ''' System-level endpoints: version check (unauthenticated),
    ''' node status, authentication.
    ''' </summary>
    Module SystemEndpoints

        Public Sub Map(app As WebApplication)

            ' Version — unauthenticated, used as health check
            app.MapGet("/api/version",
                Function() As IResult
                    Return Results.Ok(New With {
                        .application = "PowerGSM.Node",
                        .version = GetType(ProcessManager).Assembly.
                            GetName().Version?.ToString(),
                        .runtime = System.Runtime.InteropServices.
                            RuntimeInformation.FrameworkDescription
                    })
                End Function)

            ' Node status — authenticated
            app.MapGet("/api/status",
                Function(pm As ProcessManager,
                         config As NodeConfiguration) As IResult
                    Return Results.Ok(pm.GetNodeStatus(config))
                End Function)

            ' Auth handshake — manager calls this to establish session
            app.MapPost("/api/auth",
                Function(request As NodeAuthRequest,
                         config As NodeConfiguration) As IResult

                    If SecurityHelpers.FixedTimeStringEquals(request.SharedSecret, config.AuthToken) Then
                        Return Results.Ok(New NodeAuthResponse With {
                            .Accepted = True,
                            .SessionToken = Guid.NewGuid().ToString("N"),
                            .Reason = "Authenticated"
                        })
                    End If

                    Return Results.Ok(New NodeAuthResponse With {
                        .Accepted = False,
                        .Reason = "Invalid shared secret"
                    })
                End Function)

        End Sub

    End Module

    ''' <summary>
    ''' Installation endpoints: start install/update, check progress,
    ''' cancel, respond to interactive prompts.
    ''' </summary>
    Module InstallEndpoints

        Public Sub Map(app As WebApplication)

            ' Start an install/update operation
            app.MapPost("/api/install",
                Function(request As InstallRequest,
                         runner As InstallRunner) As IResult
                    Dim progress = runner.StartInstall(request)
                    If progress.OperationState = InstallationOperationState.Failed Then
                        Return Results.Conflict(progress)
                    End If
                    Return Results.Accepted(Nothing, progress)
                End Function)

            ' Fast non-destructive version check
            app.MapPost("/api/install/version-check",
                Async Function(request As AppVersionCheckRequest,
                               runner As InstallRunner,
                               context As HttpContext) As Task(Of IResult)
                    Dim result = Await runner.CheckAppVersionAsync(request, context.RequestAborted)
                    Return Results.Ok(result)
                End Function)

            ' Get install progress
            app.MapGet("/api/install/{installationId}/progress",
                Function(installationId As String,
                         runner As InstallRunner) As IResult
                    Return Results.Ok(runner.GetProgress(installationId))
                End Function)

            ' Cancel an install
            app.MapPost("/api/install/{installationId}/cancel",
                Function(installationId As String,
                         runner As InstallRunner) As IResult
                    Dim cancelled = runner.CancelInstall(installationId)
                    If cancelled Then
                        Return Results.Ok(New With {.cancelled = True})
                    End If
                    Return Results.NotFound(New With {
                        .error = "No active operation for this installation"
                    })
                End Function)

            ' Respond to interactive prompt (Steam Guard, 2FA)
            app.MapPost("/api/install/{installationId}/prompt",
                Function(installationId As String,
                         response As PromptResponse,
                         runner As InstallRunner) As IResult
                    response.OperationId = installationId
                    Dim ok = runner.ProvideInput(installationId, response.Value)
                    If ok Then
                        Return Results.Ok(New With {.accepted = True})
                    End If
                    Return Results.NotFound(New With {
                        .error = "No pending prompt for this installation"
                    })
                End Function)

            ' Uninstall — optionally delete game server files
            app.MapPost("/api/install/uninstall",
                Function(request As UninstallRequest,
                         nodeLogger As ILogger(Of InstallRunner)) As IResult
                    If request.DeleteFiles AndAlso
                       Not String.IsNullOrEmpty(request.InstallPath) Then
                        Dim fullPath = Path.GetFullPath(
                            request.InstallPath.TrimEnd("\"c, "/"c))
                        If Directory.Exists(fullPath) Then
                            Try
                                Directory.Delete(fullPath, True)
                                nodeLogger.LogInformation(
                                    "Deleted install files at {Path}", fullPath)
                            Catch ex As Exception
                                nodeLogger.LogWarning(ex,
                                    "Failed to delete {Path}", fullPath)
                                Return Results.Problem(
                                    $"Failed to delete files: {ex.Message}")
                            End Try
                        End If
                    End If
                    Return Results.Ok(New With {.success = True})
                End Function)

        End Sub

    End Module

End Namespace