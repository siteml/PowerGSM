Imports System
Imports System.IO
Imports System.Reflection
Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Http
Imports Microsoft.AspNetCore.Http.Features
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node
Imports GSM.Node.Api
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

            ' Version — unauthenticated, used as health check.
            '
            ' Returns three version axes:
            '   - build:            human-cited 0.MINOR.PATCH from
            '                       Directory.Build.props
            '   - protocolVersion:  Manager↔Node REST contract integer
            '   - contractsVersion: plugin-facing types integer
            '
            ' The legacy 'version' field carries the same string as
            ' 'build' so any pre-5f-1 Manager that only knew about
            ' 'version' still gets a usable answer. The build string
            ' strips any '+sha' suffix that the SDK appends to
            ' InformationalVersion when SourceRevisionId is set, so
            ' the wire format stays clean for matching across builds.
            app.MapGet("/api/version",
                Function() As IResult
                    Dim asm = GetType(ProcessManager).Assembly
                    Dim build As String = ReadBuildVersion(asm)
                    Return Results.Ok(New With {
                        .application = "PowerGSM.Node",
                        .version = build,
                        .build = build,
                        .protocolVersion = NodeApiContract.ProtocolVersion,
                        .contractsVersion = NodeApiContract.ContractsVersion,
                        .runtime = System.Runtime.InteropServices.
                            RuntimeInformation.FrameworkDescription,
                        .platform = ProcessManager.ResolveNodePlatform()
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

            ' Prerequisite check (Phase 5g side-feature) — query the
            ' install state of named host-side runtime dependencies
            ' (VC++ redistributable, etc). Authenticated by the
            ' standard auth middleware (anything under /api/ that
            ' isn't /api/version or /api/auth requires the bearer
            ' token). Names arrive as a single comma-separated
            ' query value (?names=a,b,c) rather than repeated
            ' ?names=a&names=b because the comma-joined form is
            ' simpler on both sides for the small finite list of
            ' prereqs in play, and individual names are escaped by
            ' the Manager's NodeHttpClient so embedded commas (none
            ' currently, but the escape is correct) survive.
            app.MapGet("/api/system/prerequisites",
                Function(names As String) As IResult
                    Dim parsed As String() = If(String.IsNullOrWhiteSpace(names),
                        New String() {},
                        names.Split(","c, StringSplitOptions.RemoveEmptyEntries))
                    Dim probe As New PrerequisiteProbe()
                    Return Results.Ok(probe.Check(parsed))
                End Function)

            ' ---- Phase 8-2 — Node self-update staging + update-exit ----
            ' Chunked binary push: begin -> chunk* -> commit. The Manager
            ' (slice 7) verifies the bytes first and pushes only vetted data;
            ' the node streams each chunk to a temp .part, verifies SHA-256 +
            ' size on commit, then atomic-renames to GSM.Node.new. apply-update
            ' triggers the graceful update-exit (detach shims, then a survivor
            ' swaps .new over the live binary and relaunches). All authenticated
            ' by the standard bearer middleware (only /api/version is exempt).
            app.MapPost("/api/system/staged-binary/begin",
                Function(request As StageBeginRequest,
                         selfUpdate As SelfUpdateService) As IResult
                    Dim r = selfUpdate.Begin(request)
                    Return Results.Json(r.Payload, statusCode:=r.Code)
                End Function)

            app.MapPost("/api/system/staged-binary/{uploadId}/chunk",
                Async Function(uploadId As String,
                               offset As Long,
                               selfUpdate As SelfUpdateService,
                               context As HttpContext) As Task(Of IResult)
                    ' Lift the per-request body cap for this streamed chunk
                    ' (auth-gated, and bounded against the declared totalBytes
                    ' in the service). Must be set before the body is read.
                    Dim sizeFeature = context.Features.Get(Of IHttpMaxRequestBodySizeFeature)()
                    If sizeFeature IsNot Nothing AndAlso Not sizeFeature.IsReadOnly Then
                        sizeFeature.MaxRequestBodySize = SelfUpdateService.ChunkBodyCapBytes
                    End If
                    Dim r = Await selfUpdate.AppendChunkAsync(uploadId, offset,
                                                              context.Request.Body,
                                                              context.RequestAborted)
                    Return Results.Json(r.Payload, statusCode:=r.Code)
                End Function)

            app.MapPost("/api/system/staged-binary/{uploadId}/commit",
                Async Function(uploadId As String,
                               selfUpdate As SelfUpdateService,
                               context As HttpContext) As Task(Of IResult)
                    Dim r = Await selfUpdate.CommitAsync(uploadId, context.RequestAborted)
                    Return Results.Json(r.Payload, statusCode:=r.Code)
                End Function)

            app.MapPost("/api/system/apply-update",
                Function(target As String,
                         selfUpdate As SelfUpdateService,
                         lifetime As IHostApplicationLifetime) As IResult
                    Dim r = selfUpdate.ApplyUpdate(
                        If(String.IsNullOrWhiteSpace(target), "node", target))
                    If Not r.Accepted Then
                        Return Results.Conflict(New With {.error = r.Reason})
                    End If
                    ' Only the node target bounces the host; nodesetup (in-place
                    ' swap) and shim (no-op) keep the node running.
                    If r.RequiresExit Then
                        selfUpdate.ScheduleStop(lifetime)
                    End If
                    Return Results.Accepted(Nothing, New With {.accepted = True, .survivor = r.Survivor})
                End Function)

        End Sub

        ''' <summary>
        ''' Read the build version off an assembly for /api/version.
        ''' Prefers AssemblyInformationalVersion (clean SemVer string
        ''' set by Directory.Build.props' Version property) over
        ''' AssemblyVersion (which is MAJOR.MINOR.0.0 by .NET
        ''' convention so it's stable across PATCH releases and
        ''' therefore a less informative wire identity). Strips the
        ''' "+gitsha" suffix the SDK appends when SourceRevisionId
        ''' is populated — the SHA is useful in logs but noisy on
        ''' the wire and confuses simple version-string equality
        ''' checks. Falls back to AssemblyVersion if the
        ''' Informational attribute is missing for any reason, then
        ''' to "0.0.0" as a last resort so callers always get a
        ''' string back.
        ''' </summary>
        Private Function ReadBuildVersion(asm As Assembly) As String
            Try
                Dim infoAttr = asm.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()
                If infoAttr IsNot Nothing AndAlso
                   Not String.IsNullOrEmpty(infoAttr.InformationalVersion) Then
                    Dim v = infoAttr.InformationalVersion
                    Dim plus = v.IndexOf("+"c)
                    If plus >= 0 Then v = v.Substring(0, plus)
                    Return v
                End If
            Catch
                ' Reflection failed; fall through to the next source.
            End Try

            Try
                Dim ver = asm.GetName().Version
                If ver IsNot Nothing Then
                    ' ToString(3) gives MAJOR.MINOR.BUILD without the
                    ' trailing .REVISION zero — looks more like the
                    ' 0.MINOR.PATCH the user expects.
                    Return ver.ToString(3)
                End If
            Catch
            End Try

            Return "0.0.0"
        End Function

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