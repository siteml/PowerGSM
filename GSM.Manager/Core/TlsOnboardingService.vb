Imports System
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data
Imports GSM.Node.Api

' ============================================================
'  TlsOnboardingService — HTTP -> pinned-HTTPS onboarding (0.7.0)
'
'  Flow per node:
'    1. Probe /api/version over the node's current transport.
'       No TLS fields   -> pre-0.7.0 node: "update node first".
'       Supported, off  -> eligible for EnableTlsAsync.
'       Enabled already -> AdoptExistingTlsAsync (pin via the
'                          authenticated HTTP /api/tls/status).
'    2. EnableTlsAsync: POST /api/tls/enable over authenticated
'       HTTP; the returned fingerprint IS the pin (TOFU over the
'       already-trusted, token-authenticated channel). Store pin +
'       port, TlsState=Enabling. The node restarts itself.
'    3. Wait for the node to come back over pinned HTTPS
'       (bounded retry). First successful pinned probe flips
'       UseTls=True / TlsState=Active and evicts the cached
'       client so every subsequent call rides HTTPS.
'
'  Rollback = RollbackToHttpAsync (UseTls=False; the node's HTTP
'  listener stayed up). Fingerprint mismatch at any later time is
'  surfaced by NodeHttpClient as NodeCertificateException; callers
'  report it here via MarkMismatch, and recovery is an explicit
'  user decision (RetrustAsync) — never automatic.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>Result of a TLS eligibility probe for the UI.</summary>
    Public Enum TlsProbeResult
        NodeUnreachable
        NotSupported        ' pre-0.7.0 node — offer node update
        SupportedNotEnabled ' show "Enable HTTPS"
        EnabledNotAdopted   ' node serves TLS but Manager has no pin — offer adopt
        ActiveHere          ' Manager already using pinned HTTPS
    End Enum

    Public Class TlsOnboardingService

        ' How long we wait for the node to come back after the
        ' enable-restart. The node's own restart cycle (graceful stop,
        ' NodeSetup relaunch, Kestrel bind) is typically < 15s; 90s
        ' covers slow service boxes without hanging the UI forever.
        Private Const ReconnectTimeoutSeconds As Integer = 90
        Private Const ReconnectPollSeconds As Integer = 3

        Private ReadOnly _clientFactory As NodeHttpClientFactory
        Private ReadOnly _logger As ILogger(Of TlsOnboardingService)

        Public Sub New(clientFactory As NodeHttpClientFactory,
                       logger As ILogger(Of TlsOnboardingService))
            _clientFactory = clientFactory
            _logger = logger
        End Sub

        ' --------------------------------------------------------
        '  Probe
        ' --------------------------------------------------------

        ''' <summary>
        ''' Classifies a node's TLS posture for the UI. Uses the
        ''' factory-cached client (whatever transport is current), so
        ''' this is cheap when the version response is already cached;
        ''' pass a fresh force probe from UI refresh buttons.
        ''' </summary>
        Public Async Function ProbeAsync(nodeId As String,
                                         cancellation As CancellationToken) As Task(Of TlsProbeResult)
            Dim node = LoadNode(nodeId)
            If node Is Nothing Then Return TlsProbeResult.NodeUnreachable
            If node.UseTls AndAlso String.Equals(node.TlsState, "Active", StringComparison.OrdinalIgnoreCase) Then
                Return TlsProbeResult.ActiveHere
            End If

            Dim client = _clientFactory.GetClient(node.NodeId, node.HostAddress, node.Port, node.AuthToken)
            Dim ver As NodeVersionResponse
            Try
                ver = Await client.GetApiVersionAsync(True, cancellation)
            Catch
                Return TlsProbeResult.NodeUnreachable
            End Try
            If ver Is Nothing OrElse Not ver.TlsSupported Then Return TlsProbeResult.NotSupported
            If ver.TlsEnabled Then Return TlsProbeResult.EnabledNotAdopted
            Return TlsProbeResult.SupportedNotEnabled
        End Function

        ' --------------------------------------------------------
        '  Enable (node not yet serving TLS)
        ' --------------------------------------------------------

        ''' <summary>
        ''' Full onboarding: enable TLS on the node, pin the returned
        ''' fingerprint, wait for the restart, verify over pinned HTTPS,
        ''' and activate. Returns Nothing on success or a human-readable
        ''' error message on failure (state is left consistent either
        ''' way: Enabling with a stored pin on a reconnect timeout —
        ''' retryable via VerifyAndActivateAsync — or untouched on an
        ''' enable-call failure).
        ''' </summary>
        Public Async Function EnableTlsAsync(nodeId As String,
                                             cancellation As CancellationToken) As Task(Of String)
            Dim node = LoadNode(nodeId)
            If node Is Nothing Then Return "Node not found."

            ' Explicit plain-HTTP client: the entity may carry stale TLS
            ' settings from an earlier attempt, and the enable call MUST
            ' ride the authenticated HTTP channel.
            Dim httpClient = _clientFactory.CreateExplicitClient(
                node.HostAddress, node.Port, node.AuthToken, useTls:=False, pinnedCertFingerprint:=Nothing)

            Dim enableResp As EnableTlsResponse
            Try
                enableResp = Await httpClient.EnableTlsAsync(New EnableTlsRequest(), cancellation)
            Catch ex As NodeApiException When ex.StatusCode.HasValue AndAlso CInt(ex.StatusCode.Value) = 404
                Return "This node does not support HTTPS yet — update the node to 0.7.0 or later first."
            Catch ex As Exception
                Return "Enable call failed: " & ex.Message
            End Try

            If enableResp Is Nothing OrElse String.IsNullOrWhiteSpace(enableResp.CertFingerprintSha256) Then
                Return "Node accepted the enable call but returned no certificate fingerprint."
            End If

            ' Pin obtained over the authenticated channel — the TOFU step.
            UpdateNode(nodeId,
                Sub(n)
                    n.TlsPort = enableResp.TlsPort
                    n.PinnedCertFingerprint = enableResp.CertFingerprintSha256.Trim().ToLowerInvariant()
                    n.TlsState = "Enabling"
                    n.UseTls = False   ' not until the pinned probe succeeds
                End Sub)

            _logger.LogInformation(
                "TLS enable accepted by node {NodeId}: port={Port} fingerprint={Fp}. Waiting for restart.",
                nodeId, enableResp.TlsPort, enableResp.CertFingerprintSha256)

            Return Await VerifyAndActivateAsync(nodeId, cancellation)
        End Function

        ''' <summary>
        ''' Step 3 alone: probe pinned HTTPS until the node answers (or
        ''' the bounded window closes), then activate. Safe to call again
        ''' after a timeout — used both inline by EnableTlsAsync and as
        ''' the retry path when the node took too long to come back.
        ''' </summary>
        Public Async Function VerifyAndActivateAsync(nodeId As String,
                                                     cancellation As CancellationToken) As Task(Of String)
            Dim node = LoadNode(nodeId)
            If node Is Nothing Then Return "Node not found."
            ' Slice 3: TlsPort = 0 is legal (single-port — HTTPS on the
            ' listen port); only the pin is mandatory.
            If String.IsNullOrWhiteSpace(node.PinnedCertFingerprint) Then
                Return "No pinned certificate stored for this node — run Enable HTTPS first."
            End If
            Dim dialPort As Integer = EffectiveTlsPort(node)

            Dim probeClient = _clientFactory.CreateExplicitClient(
                node.HostAddress, dialPort, node.AuthToken,
                useTls:=True, pinnedCertFingerprint:=node.PinnedCertFingerprint)

            Dim deadline = DateTime.UtcNow.AddSeconds(ReconnectTimeoutSeconds)
            Dim lastError As String = "no response"
            While DateTime.UtcNow < deadline
                cancellation.ThrowIfCancellationRequested()
                Try
                    Dim ver = Await probeClient.GetApiVersionAsync(True, cancellation)
                    If ver IsNot Nothing Then
                        ' Authenticated confirmation (version is unauthenticated):
                        ' /api/tls/status proves the bearer token works over TLS too.
                        Await probeClient.GetTlsStatusAsync(cancellation)

                        UpdateNode(nodeId,
                            Sub(n)
                                n.UseTls = True
                                n.TlsState = "Active"
                            End Sub)
                        ' Evict so the next factory GetClient anywhere in the
                        ' app rebuilds on pinned HTTPS.
                        _clientFactory.RemoveClient(nodeId)
                        _logger.LogInformation("Node {NodeId} is now on pinned HTTPS (port {Port}).",
                                               nodeId, dialPort)
                        Return Nothing
                    End If
                Catch ex As NodeCertificateException
                    ' Wrong cert DURING onboarding — do not keep retrying
                    ' into a possible MITM.
                    UpdateNode(nodeId, Sub(n) n.TlsState = "FingerprintMismatch")
                    Return "The certificate presented over HTTPS does not match the fingerprint " &
                           "the node reported during enable. Aborting — verify the node and re-run onboarding."
                Catch ex As Exception
                    lastError = ex.Message
                End Try
                Await Task.Delay(TimeSpan.FromSeconds(ReconnectPollSeconds), cancellation)
            End While

            ' Timeout: keep Enabling (+pin) so the user can retry;
            ' HTTP transport remains in use, nothing is broken.
            Return $"Node did not answer over HTTPS within {ReconnectTimeoutSeconds}s (last error: {lastError}). " &
                   "The node may still be restarting — use Verify again in a moment, or Roll back to HTTP."
        End Function

        ' --------------------------------------------------------
        '  Adopt (node already serving TLS, e.g. re-added node)
        ' --------------------------------------------------------

        ''' <summary>
        ''' Fetches the node's current fingerprint over the AUTHENTICATED
        ''' HTTP channel (the trustworthy path for a first pin), stores
        ''' it, then verifies + activates. Used when a node already has
        ''' TLS enabled but this Manager has no pin for it.
        ''' </summary>
        Public Async Function AdoptExistingTlsAsync(nodeId As String,
                                                    cancellation As CancellationToken) As Task(Of String)
            Dim node = LoadNode(nodeId)
            If node Is Nothing Then Return "Node not found."

            Dim httpClient = _clientFactory.CreateExplicitClient(
                node.HostAddress, node.Port, node.AuthToken, useTls:=False, pinnedCertFingerprint:=Nothing)

            Dim status As TlsStatusResponse
            Try
                status = Await httpClient.GetTlsStatusAsync(cancellation)
            Catch ex As Exception
                Return "Could not read TLS status from the node over HTTP: " & ex.Message
            End Try
            If status Is Nothing OrElse Not status.TlsEnabled OrElse
               String.IsNullOrWhiteSpace(status.CertFingerprintSha256) Then
                Return "The node does not report an active HTTPS listener."
            End If

            UpdateNode(nodeId,
                Sub(n)
                    n.TlsPort = status.TlsPort
                    n.PinnedCertFingerprint = status.CertFingerprintSha256.Trim().ToLowerInvariant()
                    n.TlsState = "Enabling"
                    n.UseTls = False
                End Sub)

            Return Await VerifyAndActivateAsync(nodeId, cancellation)
        End Function

        ' --------------------------------------------------------
        '  Port move (alternate port → shared listen port)
        ' --------------------------------------------------------

        ''' <summary>
        ''' Moves an Active node's HTTPS listener from its dedicated
        ''' alternate port onto the shared listen port (single-port
        ''' sniffing). Same certificate, same pin. Issues
        ''' POST /api/tls/enable {TlsPort=0} over the CURRENT pinned
        ''' channel (the node treats enable-while-enabled with a
        ''' different port as a move), records the advertised port,
        ''' then re-verifies on it. Returns Nothing on success or an
        ''' error message. On a timeout the entity is left Enabling
        ''' with the NEW port so "Verify HTTPS now" retries there.
        ''' Requires a node build that understands port moves
        ''' (2026-10-10+); older nodes answer TlsAlreadyEnabled.
        ''' </summary>
        Public Async Function MoveTlsToSharedPortAsync(nodeId As String,
                                                       cancellation As CancellationToken) As Task(Of String)
            Dim node = LoadNode(nodeId)
            If node Is Nothing Then Return "Node not found."
            If Not node.UseTls OrElse String.IsNullOrWhiteSpace(node.PinnedCertFingerprint) Then
                Return "HTTPS must be active before its port can be moved."
            End If
            If node.TlsPort <= 0 OrElse node.TlsPort = node.Port Then
                Return "HTTPS already shares the listen port."
            End If

            Dim client = _clientFactory.CreateExplicitClient(
                node.HostAddress, node.TlsPort, node.AuthToken,
                useTls:=True, pinnedCertFingerprint:=node.PinnedCertFingerprint)

            Dim resp As EnableTlsResponse
            Try
                resp = Await client.EnableTlsAsync(New EnableTlsRequest With {.TlsPort = 0}, cancellation)
            Catch ex As NodeApiException When ex.StatusCode.HasValue AndAlso CInt(ex.StatusCode.Value) = 409
                Return "This node build cannot move its HTTPS port in place — update the node first " &
                       "(or set Node:TlsPort to 0 in nodesettings.json and restart it)."
            Catch ex As Exception
                Return "Port move call failed: " & ex.Message
            End Try
            If resp Is Nothing OrElse resp.TlsPort <= 0 Then
                Return "Node accepted the port move but did not advertise a port."
            End If

            ' Fingerprint must be unchanged — a move never re-keys.
            Dim reported = If(resp.CertFingerprintSha256, "").Trim().ToLowerInvariant()
            If reported.Length > 0 AndAlso
               Not String.Equals(reported, node.PinnedCertFingerprint, StringComparison.OrdinalIgnoreCase) Then
                UpdateNode(nodeId, Sub(n) n.TlsState = "FingerprintMismatch")
                Return "The node reported a different certificate during the port move. Aborting; " &
                       "use Re-trust from the node panel if the change is expected."
            End If

            UpdateNode(nodeId,
                Sub(n)
                    n.TlsPort = resp.TlsPort     ' = listen port when SinglePort
                    n.TlsState = "Enabling"
                    n.UseTls = False
                End Sub)
            _clientFactory.RemoveClient(nodeId)
            _logger.LogInformation("Node {NodeId}: HTTPS moving to port {Port} (shared={Shared}); waiting for restart.",
                                   nodeId, resp.TlsPort, resp.SinglePort)

            ' Node restarts; give it a beat before the first pinned probe.
            Await Task.Delay(TimeSpan.FromSeconds(ReconnectPollSeconds), cancellation)
            Return Await VerifyAndActivateAsync(nodeId, cancellation)
        End Function

        ' --------------------------------------------------------
        '  HTTP restriction (post-activation hardening)
        ' --------------------------------------------------------

        ''' <summary>
        ''' Reads the node's current HttpMode over pinned HTTPS.
        ''' Returns Nothing on failure or on pre-HttpMode nodes.
        '''
        ''' A certificate-pin failure here is recorded via MarkMismatch
        ''' (2026-10-10, found live): this is the first pinned call the
        ''' node panel makes for an Active node, and previously the
        ''' exception was swallowed, so a node that regenerated its cert
        ''' kept rendering as "active — HTTP still open" with no re-trust
        ''' link. Callers re-read TlsState after a Nothing result.
        ''' </summary>
        Public Async Function GetHttpModeAsync(nodeId As String,
                                               cancellation As CancellationToken) As Task(Of String)
            Dim node = LoadNode(nodeId)
            If node Is Nothing OrElse Not node.UseTls Then Return Nothing
            Try
                Dim client = _clientFactory.CreateExplicitClient(
                    node.HostAddress, EffectiveTlsPort(node), node.AuthToken,
                    useTls:=True, pinnedCertFingerprint:=node.PinnedCertFingerprint)
                Dim status = Await client.GetTlsStatusAsync(cancellation)
                Return status?.HttpMode
            Catch ex As NodeCertificateException
                _logger.LogWarning("Node {NodeId}: pinned HTTPS status call failed certificate validation — " &
                                   "marking FingerprintMismatch. {Message}", nodeId, ex.Message)
                MarkMismatch(nodeId)
                Return Nothing
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Sets the node's plain-HTTP policy over pinned HTTPS, then
        ''' waits for the node to come back (it restarts). Only valid
        ''' while TLS is Active — the command channel must survive the
        ''' change. Returns Nothing on success or an error message.
        ''' </summary>
        Public Async Function SetHttpModeAsync(nodeId As String,
                                               httpMode As String,
                                               cancellation As CancellationToken) As Task(Of String)
            Dim node = LoadNode(nodeId)
            If node Is Nothing Then Return "Node not found."
            If Not node.UseTls OrElse String.IsNullOrWhiteSpace(node.PinnedCertFingerprint) Then
                Return "HTTP restriction requires active pinned HTTPS first — otherwise this change " &
                       "would cut off the only working channel."
            End If

            Dim client = _clientFactory.CreateExplicitClient(
                node.HostAddress, EffectiveTlsPort(node), node.AuthToken,
                useTls:=True, pinnedCertFingerprint:=node.PinnedCertFingerprint)
            Try
                Await client.SetHttpModeAsync(httpMode, cancellation)
            Catch ex As Exception
                Return "HttpMode change failed: " & ex.Message
            End Try

            _logger.LogInformation("Node {NodeId}: HttpMode -> {Mode}; waiting for restart.", nodeId, httpMode)

            ' Node restarts; wait for it to answer over pinned HTTPS again.
            Dim deadline = DateTime.UtcNow.AddSeconds(ReconnectTimeoutSeconds)
            Dim lastError As String = "no response"
            While DateTime.UtcNow < deadline
                cancellation.ThrowIfCancellationRequested()
                Await Task.Delay(TimeSpan.FromSeconds(ReconnectPollSeconds), cancellation)
                Try
                    Dim probe = _clientFactory.CreateExplicitClient(
                        node.HostAddress, EffectiveTlsPort(node), node.AuthToken,
                        useTls:=True, pinnedCertFingerprint:=node.PinnedCertFingerprint)
                    Dim ver = Await probe.GetApiVersionAsync(True, cancellation)
                    If ver IsNot Nothing Then Return Nothing
                Catch ex As Exception
                    lastError = ex.Message
                End Try
            End While
            Return $"Node did not come back within {ReconnectTimeoutSeconds}s after the HttpMode change " &
                   $"(last error: {lastError}). It may still be restarting."
        End Function

        ' --------------------------------------------------------
        '  Guided "Secure this node" (HTTPS_SinglePort_Plan slice 4)
        ' --------------------------------------------------------

        ''' <summary>
        ''' One-shot hardening sequence: enable HTTPS (shared port) →
        ''' verify over pinned HTTPS → HttpMode=Disabled. Idempotent per
        ''' step — a node already Active skips to the HttpMode step (after
        ''' moving off a legacy alternate port if it is on one), a node
        ''' already serving TLS without a pin is adopted instead of
        ''' enabled, and a node already on HttpMode=Disabled is a no-op.
        ''' Returns Nothing on success, else a message naming the step
        ''' that failed; earlier steps are left in place (each is
        ''' independently recoverable from the node panel's Advanced
        ''' links). <paramref name="progress"/> receives one short line
        ''' per step for the UI (may be Nothing).
        '''
        ''' Final step (when <paramref name="rotateToken"/>): rotate the auth
        ''' token over the now-pinned channel — the old one travelled in
        ''' cleartext before HTTPS. One more restart. Other Managers attached
        ''' to the node must re-enter the token (Edit Node).
        ''' </summary>
        Public Async Function SecureNodeAsync(nodeId As String,
                                              progress As Action(Of String),
                                              cancellation As CancellationToken,
                                              Optional rotateToken As Boolean = True) As Task(Of String)
            Dim report = Sub(line As String)
                             Try
                                 If progress IsNot Nothing Then progress(line)
                             Catch
                             End Try
                         End Sub

            report("Probing node...")
            Dim probe = Await ProbeAsync(nodeId, cancellation)
            Dim err As String = Nothing

            Select Case probe
                Case TlsProbeResult.NodeUnreachable
                    Return "Node unreachable — nothing changed."
                Case TlsProbeResult.NotSupported
                    Return "Node build does not support HTTPS — update the node first."
                Case TlsProbeResult.SupportedNotEnabled
                    report("Enabling HTTPS (node restarts)...")
                    err = Await EnableTlsAsync(nodeId, cancellation)
                    If err IsNot Nothing Then Return "Enable HTTPS failed: " & err
                Case TlsProbeResult.EnabledNotAdopted
                    report("Node already serves HTTPS — adopting its certificate...")
                    err = Await AdoptExistingTlsAsync(nodeId, cancellation)
                    If err IsNot Nothing Then Return "Adopt HTTPS failed: " & err
                Case TlsProbeResult.ActiveHere
                    ' Pre-slice-1 nodes sit on a dedicated alternate port;
                    ' the secured end state is the shared listen port.
                    Dim cur = LoadNode(nodeId)
                    If cur IsNot Nothing AndAlso cur.TlsPort > 0 AndAlso cur.TlsPort <> cur.Port Then
                        report($"Moving HTTPS from port {cur.TlsPort} to shared port {cur.Port} (node restarts)...")
                        err = Await MoveTlsToSharedPortAsync(nodeId, cancellation)
                        If err IsNot Nothing Then Return "Move to shared port failed: " & err
                    Else
                        report("HTTPS already active — checking HTTP listener...")
                    End If
            End Select

            ' Re-read: Enable/Adopt flipped the entity to Active on success.
            Dim node = LoadNode(nodeId)
            If node Is Nothing OrElse Not node.UseTls Then
                Return "HTTPS did not activate — see the node panel (Verify HTTPS now)."
            End If

            Dim mode = Await GetHttpModeAsync(nodeId, cancellation)
            If String.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase) Then
                report("Plain HTTP already off.")
            ElseIf String.IsNullOrEmpty(mode) Then
                ' Pre-HttpMode node build, or status call failed over TLS.
                Return "HTTPS is active but the node did not report its HTTP mode — " &
                       "cannot turn plain HTTP off (older node build?)."
            Else
                report("Turning plain HTTP off (node restarts)...")
                err = Await SetHttpModeAsync(nodeId, "Disabled", cancellation)
                If err IsNot Nothing Then Return "HTTPS is active, but turning HTTP off failed: " & err
            End If

            If rotateToken Then
                report("Rotating auth token (node restarts)...")
                err = Await RotateTokenAsync(nodeId, cancellation)
                If err IsNot Nothing Then Return "Secured (HTTPS pinned, HTTP off), but token rotation failed: " & err
            End If

            report("Secured.")
            Return Nothing
        End Function

        ' --------------------------------------------------------
        '  Token rotation (slice 4 "Rotate token" step)
        ' --------------------------------------------------------

        ''' <summary>
        ''' Rotates the node's auth token over pinned HTTPS. Lockout-safe
        ''' ordering: the NEW token is stored locally FIRST, then sent; a
        ''' refusal restores the old one; a lost reply after acceptance is
        ''' resolved by probing which token the node restarted with. Any
        ''' OTHER Manager attached to the node is locked out until its token
        ''' is re-entered via Edit Node — the UI says so. Returns Nothing on
        ''' success (node back over pinned HTTPS answering to the new token)
        ''' or an error message.
        ''' </summary>
        Public Async Function RotateTokenAsync(nodeId As String,
                                               cancellation As CancellationToken) As Task(Of String)
            Dim node = LoadNode(nodeId)
            If node Is Nothing Then Return "Node not found."
            If Not node.UseTls OrElse String.IsNullOrWhiteSpace(node.PinnedCertFingerprint) Then
                Return "Token rotation requires active pinned HTTPS — the new token must never travel in the clear."
            End If

            Dim oldToken As String = node.AuthToken
            Dim newToken As String = GenerateToken()

            ' Client on the OLD token (it authenticates this call).
            Dim client = _clientFactory.CreateExplicitClient(
                node.HostAddress, EffectiveTlsPort(node), oldToken,
                useTls:=True, pinnedCertFingerprint:=node.PinnedCertFingerprint)

            UpdateNode(nodeId, Sub(n) n.AuthToken = newToken)
            _clientFactory.RemoveClient(nodeId)

            Dim refused As String = Nothing
            Dim replyLost As Boolean = False
            Try
                Await client.RotateTokenAsync(newToken, cancellation)
            Catch ex As NodeApiException
                refused = If(ex.StatusCode.HasValue AndAlso CInt(ex.StatusCode.Value) = 404,
                             "This node build has no token-rotation endpoint — update the node first.",
                             "Node refused the rotation: " & ex.Message)
            Catch ex As Exception
                replyLost = True
                _logger.LogWarning(ex, "Node {NodeId}: rotation reply lost; probing which token the node restarted with.", nodeId)
            End Try

            If refused IsNot Nothing Then
                UpdateNode(nodeId, Sub(n) n.AuthToken = oldToken)
                _clientFactory.RemoveClient(nodeId)
                Return refused
            End If

            Await Task.Delay(TimeSpan.FromSeconds(ReconnectPollSeconds), cancellation)
            Dim winner = Await ProbeTokenAfterRestartAsync(node, newToken, If(replyLost, oldToken, Nothing), cancellation)
            If winner Is Nothing Then
                ' Keep NEW stored: on a clean accept the node restarted into it
                ' and is merely slow; the operator can Edit Node if not.
                Return $"Token sent, but the node did not answer with it within {ReconnectTimeoutSeconds}s. " &
                       "It may still be restarting — reopen the panel in a moment. If it stays unreachable, " &
                       "check nodesettings.json on the node and enter that token via Edit Node."
            End If
            If winner <> newToken Then
                UpdateNode(nodeId, Sub(n) n.AuthToken = oldToken)
                _clientFactory.RemoveClient(nodeId)
                Return "Node restarted with the OLD token; rotation did not apply."
            End If
            _logger.LogWarning("Node {NodeId}: auth token rotated and confirmed over pinned HTTPS.", nodeId)
            Return Nothing
        End Function

        ''' <summary>
        ''' Polls pinned HTTPS until an AUTHENTICATED call succeeds with one
        ''' of the candidate tokens (second may be Nothing). Returns the
        ''' token that worked, or Nothing on timeout. A pin failure
        ''' propagates (it is never a token problem).
        ''' </summary>
        Private Async Function ProbeTokenAfterRestartAsync(node As NodeEntity,
                                                           first As String,
                                                           second As String,
                                                           cancellation As CancellationToken) As Task(Of String)
            Dim deadline = DateTime.UtcNow.AddSeconds(ReconnectTimeoutSeconds)
            While DateTime.UtcNow < deadline
                cancellation.ThrowIfCancellationRequested()
                For Each candidate In New String() {first, second}
                    If String.IsNullOrEmpty(candidate) Then Continue For
                    Dim ok As Boolean = False
                    Try
                        Dim probe = _clientFactory.CreateExplicitClient(
                            node.HostAddress, EffectiveTlsPort(node), candidate,
                            useTls:=True, pinnedCertFingerprint:=node.PinnedCertFingerprint)
                        Await probe.GetTlsStatusAsync(cancellation)   ' authenticated
                        ok = True
                    Catch ex As NodeCertificateException
                        Throw
                    Catch
                        ' Down, restarting, or wrong token — try the other / wait.
                    End Try
                    If ok Then Return candidate
                Next
                Await Task.Delay(TimeSpan.FromSeconds(ReconnectPollSeconds), cancellation)
            End While
            Return Nothing
        End Function

        ''' <summary>64 hex chars from 32 CSPRNG bytes.</summary>
        Private Shared Function GenerateToken() As String
            Dim bytes(31) As Byte
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes)
            Return Convert.ToHexString(bytes).ToLowerInvariant()
        End Function

        ' --------------------------------------------------------
        '  Rollback / mismatch / re-trust
        ' --------------------------------------------------------

        ''' <summary>Drop back to plain HTTP (the node's HTTP listener is
        ''' still up unless the operator restricted it node-side). Keeps
        ''' the pin so re-activation is one Verify away.</summary>
        Public Sub RollbackToHttp(nodeId As String)
            UpdateNode(nodeId,
                Sub(n)
                    n.UseTls = False
                    n.TlsState = If(String.IsNullOrWhiteSpace(n.PinnedCertFingerprint), "None", "Enabling")
                End Sub)
            _clientFactory.RemoveClient(nodeId)
            _logger.LogWarning("Node {NodeId} rolled back to plain HTTP by operator.", nodeId)
        End Sub

        ''' <summary>Record that a pinned connection failed certificate
        ''' validation. Called by UI/polling paths that catch
        ''' NodeCertificateException. Idempotent.</summary>
        Public Sub MarkMismatch(nodeId As String)
            UpdateNode(nodeId, Sub(n) n.TlsState = "FingerprintMismatch")
        End Sub

        ''' <summary>
        ''' Fetches the fingerprint the node CURRENTLY presents, for
        ''' display in the adopt / re-trust dialogs. Returns Nothing on
        ''' failure.
        '''
        ''' Two paths, in order:
        '''   1. Authenticated plain HTTP /api/tls/status (original path —
        '''      proves the token too, so prefer it when HTTP is open).
        '''   2. Unpinned HTTPS handshake on the TLS port, reading the hash
        '''      of the certificate actually presented
        '''      (NodeHttpClient.ProbePresentedFingerprintAsync). Added
        '''      2026-10-10 after a live lockout: a node regenerated its
        '''      cert while its HTTP listener was restricted, so path 1
        '''      had no channel and re-trust was impossible from the
        '''      Manager. The fingerprint is public by design; the trust
        '''      decision remains the operator's confirmation dialog.
        ''' </summary>
        Public Async Function GetReportedFingerprintAsync(nodeId As String,
                                                          cancellation As CancellationToken) As Task(Of String)
            Dim node = LoadNode(nodeId)
            If node Is Nothing Then Return Nothing

            Try
                Dim httpClient = _clientFactory.CreateExplicitClient(
                    node.HostAddress, node.Port, node.AuthToken, useTls:=False, pinnedCertFingerprint:=Nothing)
                Dim status = Await httpClient.GetTlsStatusAsync(cancellation)
                If Not String.IsNullOrWhiteSpace(status?.CertFingerprintSha256) Then
                    Return status.CertFingerprintSha256.Trim().ToLowerInvariant()
                End If
            Catch
                ' HTTP restricted / token stale / listener down — fall through.
            End Try

            ' TLS port: what we pinned against if known, else the listen
            ' port (single-port nodes serve HTTPS there too).
            Dim tlsPort As Integer = EffectiveTlsPort(node)
            Dim presented = Await NodeHttpClient.ProbePresentedFingerprintAsync(
                node.HostAddress, tlsPort, cancellation)
            If String.IsNullOrWhiteSpace(presented) AndAlso tlsPort <> node.Port Then
                ' Node may have moved to single-port since the pin was taken.
                presented = Await NodeHttpClient.ProbePresentedFingerprintAsync(
                    node.HostAddress, node.Port, cancellation)
            End If
            If Not String.IsNullOrWhiteSpace(presented) Then
                _logger.LogInformation(
                    "Node {NodeId}: fingerprint read via unpinned HTTPS probe (HTTP path unavailable): {Fp}",
                    nodeId, presented)
            End If
            Return presented
        End Function

        ''' <summary>
        ''' Explicit user decision to trust a NEW fingerprint after a
        ''' mismatch (node reinstall, cert regeneration). The caller must
        ''' have shown the fingerprint to the user first. Re-pins, then
        ''' verifies + activates.
        ''' </summary>
        Public Async Function RetrustAsync(nodeId As String,
                                           newFingerprint As String,
                                           cancellation As CancellationToken) As Task(Of String)
            If String.IsNullOrWhiteSpace(newFingerprint) Then Return "No fingerprint provided."
            UpdateNode(nodeId,
                Sub(n)
                    n.PinnedCertFingerprint = newFingerprint.Trim().ToLowerInvariant()
                    n.TlsState = "Enabling"
                    n.UseTls = False
                End Sub)
            _clientFactory.RemoveClient(nodeId)
            _logger.LogWarning("Node {NodeId}: operator re-trusted new certificate fingerprint {Fp}.",
                               nodeId, newFingerprint)
            Return Await VerifyAndActivateAsync(nodeId, cancellation)
        End Function

        ' --------------------------------------------------------
        '  Helpers
        ' --------------------------------------------------------

        ''' <summary>Port to dial for HTTPS on this node: recorded TlsPort,
        ''' else the listen port (single-port node). Mirrors
        ''' NodeTlsSettings.EffectivePort.</summary>
        Private Shared Function EffectiveTlsPort(node As NodeEntity) As Integer
            Return If(node.TlsPort > 0, node.TlsPort, node.Port)
        End Function

        Private Function LoadNode(nodeId As String) As NodeEntity
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Return db.Nodes.Find(nodeId)
            End Using
        End Function

        Private Sub UpdateNode(nodeId As String, mutate As Action(Of NodeEntity))
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim n = db.Nodes.Find(nodeId)
                If n Is Nothing Then Return
                mutate(n)
                db.SaveChanges()
            End Using
        End Sub

    End Class

End Namespace
