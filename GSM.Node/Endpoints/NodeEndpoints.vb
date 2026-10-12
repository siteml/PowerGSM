Imports System
Imports System.IO
Imports System.Reflection
Imports System.Text.Json.Nodes
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
                Function(config As NodeConfiguration,
                         tls As TlsCertificateService) As IResult
                    Dim asm = GetType(ProcessManager).Assembly
                    Dim build As String = ReadBuildVersion(asm)
                    ' TLS advertisement (0.7.0): fingerprint only included
                    ' when TLS is actually enabled — it is public info under
                    ' the pinning trust model (the *pin* is obtained over the
                    ' authenticated enable call, not from here).
                    Dim fingerprint As String = Nothing
                    If config.EnableTls Then
                        fingerprint = tls.TryGetFingerprint(config.NodeId)
                    End If
                    Return Results.Ok(New With {
                        .application = "PowerGSM.Node",
                        .version = build,
                        .build = build,
                        .protocolVersion = NodeApiContract.ProtocolVersion,
                        .contractsVersion = NodeApiContract.ContractsVersion,
                        .runtime = System.Runtime.InteropServices.
                            RuntimeInformation.FrameworkDescription,
                        .platform = ProcessManager.ResolveNodePlatform(),
                        .tlsSupported = True,
                        .tlsEnabled = config.EnableTls,
                        .tlsPort = If(config.EnableTls, config.EffectiveTlsPort, 0),
                        .singlePort = config.UsesSinglePort,
                        .certFingerprintSha256 = fingerprint,
                        .eventStreamSupported = True
                    })
                End Function)

            ' ---- TLS onboarding (0.7.0) ----
            ' Both authenticated by the standard bearer middleware (only
            ' /api/version and /api/auth are exempt).

            app.MapGet("/api/tls/status",
                Function(config As NodeConfiguration,
                         tls As TlsCertificateService) As IResult
                    Dim resp As New TlsStatusResponse With {
                        .TlsSupported = True,
                        .TlsEnabled = config.EnableTls,
                        .TlsPort = If(config.EnableTls, config.EffectiveTlsPort, 0),
                        .SinglePort = config.UsesSinglePort
                    }
                    resp.HttpMode = config.HttpMode
                    If config.EnableTls Then
                        Try
                            Dim cert = tls.GetOrCreateCertificate(config.NodeId)
                            resp.CertFingerprintSha256 = TlsCertificateService.FingerprintSha256(cert)
                            resp.CertNotAfterUtc = cert.NotAfter.ToUniversalTime()
                        Catch
                        End Try
                    End If
                    Return Results.Ok(resp)
                End Function)

            ' Change the plain-HTTP listener policy (0.7.0 hardening).
            ' Persists Node.HttpMode and restarts so Kestrel rebinds.
            ' "Disabled" requires TLS on — a node must always keep at
            ' least one reachable listener. Sent by the Manager over
            ' pinned HTTPS once TLS is active (sending it over HTTP
            ' works too and is the recovery path for re-opening HTTP).
            app.MapPost("/api/tls/http-mode",
                Function(request As SetHttpModeRequest,
                         config As NodeConfiguration,
                         selfUpdate As SelfUpdateService,
                         lifetime As IHostApplicationLifetime,
                         nodeLogger As ILogger(Of SelfUpdateService)) As IResult

                    Dim mode = If(request?.HttpMode, "").Trim()
                    Dim valid = mode.Equals("Open", StringComparison.OrdinalIgnoreCase) OrElse
                                mode.Equals("LoopbackOnly", StringComparison.OrdinalIgnoreCase) OrElse
                                mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                    If Not valid Then
                        Return Results.BadRequest(New With {
                            .error = "httpMode must be Open, LoopbackOnly, or Disabled"})
                    End If
                    If mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase) AndAlso
                       Not config.EnableTls Then
                        Return Results.Conflict(New With {
                            .error = "Cannot disable HTTP while TLS is not enabled — the node would become unreachable."})
                    End If

                    Try
                        NodeSettingsFile.WriteHttpMode(mode)
                    Catch ex As Exception
                        Return Results.Problem(
                            title:="Could not persist HttpMode to nodesettings.json",
                            detail:=ex.Message, statusCode:=500)
                    End Try

                    If Not selfUpdate.RequestRestart() Then
                        Try
                            NodeSettingsFile.WriteHttpMode(config.HttpMode)
                        Catch
                        End Try
                        Return Results.Conflict(New With {
                            .error = "No restart mechanism available; HttpMode unchanged."})
                    End If

                    nodeLogger.LogInformation("HttpMode change accepted: {Mode}; restarting.", mode)
                    selfUpdate.ScheduleStop(lifetime)
                    Return Results.Ok(New With {.httpMode = mode, .restartRequired = True})
                End Function)

            ' Enable HTTPS: generate/load the cert NOW (so the fingerprint
            ' in the response is exactly what the listener will present),
            ' persist EnableTls + TlsPort into nodesettings.json, reply,
            ' then restart via the self-update relauncher machinery so
            ' Kestrel comes back with the HTTPS listener bound.
            app.MapPost("/api/tls/enable",
                Function(request As EnableTlsRequest,
                         config As NodeConfiguration,
                         tls As TlsCertificateService,
                         selfUpdate As SelfUpdateService,
                         lifetime As IHostApplicationLifetime,
                         nodeLogger As ILogger(Of SelfUpdateService)) As IResult

                    ' Single-port semantics (HTTPS_SinglePort_Plan): 0 / omitted /
                    ' = ListenPort → HTTPS shares ListenPort (persisted as 0).
                    ' Any other port → dedicated HTTPS listener there. A 0.7.0
                    ' Manager sends an empty request and therefore lands on
                    ' the shared port; it then connects to the TlsPort we
                    ' advertise back (= ListenPort), which just works.
                    Dim requestedPort As Integer = 0
                    If request IsNot Nothing AndAlso request.TlsPort > 0 AndAlso
                       request.TlsPort <> config.ListenPort Then
                        requestedPort = request.TlsPort
                    End If
                    Dim advertisedPort As Integer = If(requestedPort > 0, requestedPort, config.ListenPort)

                    ' Already enabled: same effective port → nothing to do
                    ' (Conflict, as before). Different port → a PORT MOVE:
                    ' same certificate, persist the new TlsPort, restart. This
                    ' is how nodes onboarded on a dedicated alternate port
                    ' (pre-slice-1) migrate to the shared listen port without
                    ' anyone touching nodesettings.json / NodeSetup
                    ' (2026-10-10). The Manager calls it over its current
                    ' pinned HTTPS channel, then re-verifies on the new port.
                    Dim isPortMove As Boolean = False
                    Dim previousPort As Integer = 0
                    If config.EnableTls Then
                        If advertisedPort = config.EffectiveTlsPort Then
                            Return Results.Conflict(New With {
                                .error = "TLS already enabled",
                                .errorCode = NodeErrorCodes.TlsAlreadyEnabled
                            })
                        End If
                        isPortMove = True
                        previousPort = config.TlsPort
                    End If

                    Dim cert As System.Security.Cryptography.X509Certificates.X509Certificate2
                    Try
                        cert = tls.GetOrCreateCertificate(config.NodeId)
                    Catch ex As Exception
                        Return Results.Problem(
                            title:="Certificate generation failed",
                            detail:=ex.Message,
                            statusCode:=500)
                    End Try

                    Try
                        NodeSettingsFile.WriteTlsSettings(True, requestedPort)
                    Catch ex As Exception
                        Return Results.Problem(
                            title:="Could not persist TLS settings to nodesettings.json",
                            detail:=ex.Message,
                            statusCode:=500)
                    End Try

                    ' Restart vehicle check BEFORE replying: if there is no
                    ' way to come back up, revert the config write and refuse
                    ' rather than leaving the node down or the config lying.
                    If Not selfUpdate.RequestRestart() Then
                        Try
                            ' Revert: a port move keeps TLS on at the old port.
                            NodeSettingsFile.WriteTlsSettings(isPortMove, If(isPortMove, previousPort, 0))
                        Catch
                        End Try
                        Return Results.Conflict(New With {
                            .error = If(isPortMove,
                                        "No restart mechanism available (GSM.NodeSetup missing?); HTTPS port unchanged.",
                                        "No restart mechanism available (GSM.NodeSetup missing?); TLS not enabled.")
                        })
                    End If

                    nodeLogger.LogInformation(
                        "TLS {Verb} accepted: port={Port} singlePort={Single} fingerprint={Fp}; restarting.",
                        If(isPortMove, "port move", "enable"),
                        advertisedPort, requestedPort = 0, TlsCertificateService.FingerprintSha256(cert))

                    selfUpdate.ScheduleStop(lifetime)

                    Return Results.Ok(New EnableTlsResponse With {
                        .TlsPort = advertisedPort,
                        .SinglePort = (requestedPort = 0),
                        .CertFingerprintSha256 = TlsCertificateService.FingerprintSha256(cert),
                        .CertNotAfterUtc = cert.NotAfter.ToUniversalTime(),
                        .RestartRequired = True
                    })
                End Function)

            ' Node status — authenticated
            app.MapGet("/api/status",
                Function(pm As ProcessManager,
                         config As NodeConfiguration) As IResult
                    Return Results.Ok(pm.GetNodeStatus(config))
                End Function)

            ' Token rotation (2026-10-10, HTTPS_SinglePort_Plan slice 4's
            ' "Rotate token" step). HTTPS ONLY: the sniffing listener sets
            ' ITlsConnectionFeature so Request.IsHttps is truthful on the
            ' shared port; a plaintext caller gets 403 and nothing is written.
            ' The Manager generates the token (it must hold it before we
            ' restart). We persist, reply, restart; the old token works until
            ' the restart because NodeConfiguration is bound once at startup.
            app.MapPost("/api/auth/rotate",
                Function(request As RotateTokenRequest,
                         context As HttpContext,
                         selfUpdate As SelfUpdateService,
                         lifetime As IHostApplicationLifetime,
                         nodeLogger As ILogger(Of SelfUpdateService)) As IResult

                    If Not context.Request.IsHttps Then
                        Return Results.Json(New With {
                            .error = "Token rotation is only accepted over HTTPS."}, statusCode:=403)
                    End If
                    Dim newToken = If(request?.NewToken, "").Trim()
                    If newToken.Length < 32 Then
                        Return Results.BadRequest(New With {
                            .error = "newToken must be at least 32 characters."})
                    End If

                    ' Restart vehicle BEFORE persisting: a token the node can't
                    ' restart into would lock the Manager out of nothing yet,
                    ' but would desync the file from the live config.
                    If Not selfUpdate.RequestRestart() Then
                        Return Results.Conflict(New With {
                            .error = "No restart mechanism available (GSM.NodeSetup missing?); token unchanged."})
                    End If

                    Try
                        NodeSettingsFile.WriteAuthToken(newToken)
                    Catch ex As Exception
                        ' RequestRestart already armed the survivor; it will
                        ' relaunch us with the OLD token (file unchanged), which
                        ' is the safe outcome. Tell the Manager so it reverts.
                        nodeLogger.LogError(ex, "Token rotation: could not persist new token; restarting with the old one.")
                        selfUpdate.ScheduleStop(lifetime)
                        Return Results.Problem(
                            title:="Could not persist the new token to nodesettings.json",
                            detail:=ex.Message, statusCode:=500)
                    End Try

                    nodeLogger.LogWarning("Auth token rotated by Manager over HTTPS; restarting to apply.")
                    selfUpdate.ScheduleStop(lifetime)
                    Return Results.Ok(New RotateTokenResponse With {.Accepted = True, .RestartRequired = True})
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

            ' ---- Process control (2026-10-10) ----
            ' For user-mode nodes (no service, no systemd) there was no way to
            ' stop or bounce the node from the Manager. Both authenticated.
            '
            ' restart: same vehicle as a TLS-enable / HttpMode bounce — NodeSetup
            ' survivor relaunches us (service: sc start; bare: direct launch,
            ' with a console if we had one). Shims detach and are re-adopted.
            '
            ' shutdown: exit cleanly with NO survivor. Under a Windows service
            ' the SCM sees a stopped service; under systemd Restart=on-failure
            ' does NOT fire on exit 0, so it stays down there too. The node
            ' will not answer again until someone starts it on that machine —
            ' the Manager's confirm dialog says so in bold.
            app.MapPost("/api/system/restart",
                Function(selfUpdate As SelfUpdateService,
                         lifetime As IHostApplicationLifetime,
                         nodeLogger As ILogger(Of SelfUpdateService)) As IResult
                    If Not selfUpdate.RequestRestart() Then
                        Return Results.Conflict(New With {
                            .error = "No restart mechanism available (GSM.NodeSetup missing?); node left running."})
                    End If
                    nodeLogger.LogInformation("Restart requested by Manager; exiting for relaunch.")
                    selfUpdate.ScheduleStop(lifetime)
                    Return Results.Accepted(Nothing, New With {.accepted = True, .restarting = True})
                End Function)

            app.MapPost("/api/system/shutdown",
                Function(selfUpdate As SelfUpdateService,
                         lifetime As IHostApplicationLifetime,
                         nodeLogger As ILogger(Of SelfUpdateService)) As IResult
                    nodeLogger.LogWarning("Shutdown requested by Manager; exiting WITHOUT relaunch. " &
                                          "The node must be started manually on this machine.")
                    selfUpdate.ScheduleStop(lifetime)
                    Return Results.Accepted(Nothing, New With {.accepted = True, .restarting = False})
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
    ''' Read-modify-write of nodesettings.json for the TLS enable flow
    ''' (0.7.0). The file lives beside the node binary (the same file the
    ''' host loads via AddJsonFile with the content root pinned to the
    ''' binary directory by UseWindowsService/UseSystemd). Preserves every
    ''' other key: the JSON is parsed as a mutable JsonNode tree, only
    ''' Node.EnableTls / Node.TlsPort are touched, then the whole document
    ''' is rewritten (write-temp + replace so a crash never leaves a torn
    ''' half-written config).
    ''' </summary>
    Friend Module NodeSettingsFile

        Private ReadOnly _gate As New Object()

        Public Sub WriteTlsSettings(enableTls As Boolean, tlsPort As Integer)
            SyncLock _gate
                Dim path As String = IO.Path.Combine(AppContext.BaseDirectory, "nodesettings.json")
                Dim root As JsonNode
                If File.Exists(path) Then
                    root = JsonNode.Parse(File.ReadAllText(path))
                Else
                    root = New JsonObject()
                End If
                If root Is Nothing Then root = New JsonObject()

                Dim nodeSection = TryCast(root("Node"), JsonObject)
                If nodeSection Is Nothing Then
                    nodeSection = New JsonObject()
                    root("Node") = nodeSection
                End If

                nodeSection("EnableTls") = JsonValue.Create(enableTls)
                nodeSection("TlsPort") = JsonValue.Create(tlsPort)

                SaveDocument(path, root)
            End SyncLock
        End Sub

        ''' <summary>Persist only Node.AuthToken (token rotation).</summary>
        Public Sub WriteAuthToken(authToken As String)
            SyncLock _gate
                Dim path As String = IO.Path.Combine(AppContext.BaseDirectory, "nodesettings.json")
                Dim root As JsonNode = Nothing
                If File.Exists(path) Then root = JsonNode.Parse(File.ReadAllText(path))
                If root Is Nothing Then root = New JsonObject()

                Dim nodeSection = TryCast(root("Node"), JsonObject)
                If nodeSection Is Nothing Then
                    nodeSection = New JsonObject()
                    root("Node") = nodeSection
                End If

                nodeSection("AuthToken") = JsonValue.Create(authToken)
                SaveDocument(path, root)
            End SyncLock
        End Sub

        ''' <summary>Persist only Node.HttpMode (0.7.0 hardening).</summary>
        Public Sub WriteHttpMode(httpMode As String)
            SyncLock _gate
                Dim path As String = IO.Path.Combine(AppContext.BaseDirectory, "nodesettings.json")
                Dim root As JsonNode = Nothing
                If File.Exists(path) Then root = JsonNode.Parse(File.ReadAllText(path))
                If root Is Nothing Then root = New JsonObject()

                Dim nodeSection = TryCast(root("Node"), JsonObject)
                If nodeSection Is Nothing Then
                    nodeSection = New JsonObject()
                    root("Node") = nodeSection
                End If

                nodeSection("HttpMode") = JsonValue.Create(httpMode)
                SaveDocument(path, root)
            End SyncLock
        End Sub

        Private Sub SaveDocument(path As String, root As JsonNode)
            Dim opts As New Text.Json.JsonSerializerOptions With {.WriteIndented = True}
            Dim tmp = path & ".tmp"
            File.WriteAllText(tmp, root.ToJsonString(opts))
            File.Move(tmp, path, overwrite:=True)
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