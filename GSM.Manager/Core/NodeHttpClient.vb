Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net
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

        ' ---- TLS (0.7.0) ----
        ' When _useTls, every HttpClient this class creates (the shared
        ' one AND every one-shot: staged-binary push, file upload, map
        ' gen) is built over a handler whose server-certificate callback
        ' compares the presented cert's SHA-256 fingerprint against the
        ' pinned value — chain and hostname are deliberately ignored;
        ' the pin IS the trust anchor (TOFU, like SSH host keys).
        Private ReadOnly _useTls As Boolean
        Private ReadOnly _pinnedFingerprintBytes As Byte()

        Public Sub New(hostAddress As String, port As Integer,
                       authToken As String,
                       logger As ILogger(Of NodeHttpClient),
                       Optional useTls As Boolean = False,
                       Optional pinnedCertFingerprint As String = Nothing)
            _useTls = useTls AndAlso Not String.IsNullOrWhiteSpace(pinnedCertFingerprint)
            If _useTls Then
                _pinnedFingerprintBytes = Convert.FromHexString(pinnedCertFingerprint.Trim())
                _baseUrl = $"https://{hostAddress}:{port}"
            Else
                _baseUrl = $"http://{hostAddress}:{port}"
            End If
            _logger = logger

            _httpClient = New HttpClient(CreateHandler(), disposeHandler:=True)
            _httpClient.BaseAddress = New Uri(_baseUrl)
            _httpClient.Timeout = TimeSpan.FromSeconds(30)
            If Not String.IsNullOrEmpty(authToken) Then
                _httpClient.DefaultRequestHeaders.Authorization =
                    New System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken)
            End If
        End Sub

        ' Set (1) by the pin callback when it REJECTS a presented cert;
        ' consumed (reset to 0) by WrapException. Distinguishes a genuine
        ' fingerprint mismatch from every other AuthenticationException
        ' the TLS stack raises — above all "unexpected packet format",
        ' which is what dialling HTTPS at a listener that is still (or
        ' only) speaking plaintext throws. Found live 2026-10-10: every
        ' fresh enable reported a false MISMATCH because the pinned
        ' verify probe hit the old plaintext process during the ~30 s
        ' restart gap, and VerifyAndActivateAsync aborted on it as a MITM.
        Private _pinRejected As Integer = 0

        ''' <summary>
        ''' Single source of truth for transport-level trust. EVERY
        ''' HttpClient created by this class must be built over this
        ''' handler — a missed call site would fail only against TLS
        ''' nodes and only on that feature's path.
        ''' </summary>
        Private Function CreateHandler() As HttpClientHandler
            Dim handler As New HttpClientHandler()
            If _useTls Then
                handler.ServerCertificateCustomValidationCallback =
                    Function(request, cert, chain, errors)
                        If cert Is Nothing Then Return False
                        Dim presented = cert.GetCertHash(Security.Cryptography.HashAlgorithmName.SHA256)
                        Dim ok = Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                            presented, _pinnedFingerprintBytes)
                        If Not ok Then
                            ' Mark BEFORE logging: WrapException reads this to tell a
                            ' real pin rejection from any other TLS handshake failure.
                            Threading.Interlocked.Exchange(_pinRejected, 1)
                            _logger.LogError(
                                "Certificate PIN MISMATCH for {BaseUrl}: presented {Presented}. " &
                                "Connection refused — possible MITM or node cert regeneration.",
                                _baseUrl, Convert.ToHexString(presented).ToLowerInvariant())
                        End If
                        Return ok
                    End Function
            End If
            Return handler
        End Function

        ''' <summary>
        ''' One-shot client for long-running transfers (infinite timeout,
        ''' same base address, auth header, and — critically — the same
        ''' pinned-TLS handler as the shared client).
        ''' </summary>
        Private Function CreateOneShotClient() As HttpClient
            Dim c As New HttpClient(CreateHandler(), disposeHandler:=True)
            c.BaseAddress = _httpClient.BaseAddress
            c.Timeout = Timeout.InfiniteTimeSpan
            If _httpClient.DefaultRequestHeaders.Authorization IsNot Nothing Then
                c.DefaultRequestHeaders.Authorization = _httpClient.DefaultRequestHeaders.Authorization
            End If
            Return c
        End Function

        ''' <summary>True when this client is talking pinned HTTPS.</summary>
        Public ReadOnly Property IsTls As Boolean
            Get
                Return _useTls
            End Get
        End Property

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

        ' ---- Host-side prerequisite checks (Phase 5g side-feature) ----

        ''' <summary>
        ''' Query the node for the install state of named host-side
        ''' runtime dependencies. See INodeClient.CheckPrerequisitesAsync
        ''' for the contract; this implementation joins names as a
        ''' single comma-separated query value, individually escaped
        ''' via Uri.EscapeDataString so embedded commas (none today,
        ''' but defensive) survive the round trip without colliding
        ''' with the delimiter.
        '''
        ''' Empty / Nothing names list short-circuits with an empty
        ''' response — no point round-tripping when there's nothing
        ''' to ask. Other failures (network, 404 on older nodes,
        ''' deserialisation) flow through WrapException so callers
        ''' can catch NodeConnectionException / NodeApiException
        ''' specifically.
        ''' </summary>
        Public Async Function CheckPrerequisitesAsync(names As IReadOnlyList(Of String),
                                                       cancellation As CancellationToken) As Task(Of PrerequisiteCheckResponse) Implements INodeClient.CheckPrerequisitesAsync
            If names Is Nothing OrElse names.Count = 0 Then
                Return New PrerequisiteCheckResponse With {
                    .Results = New List(Of PrerequisiteCheckResult)()
                }
            End If

            Try
                Dim joined = String.Join(",",
                    names.Select(Function(n) Uri.EscapeDataString(If(n, ""))))
                Dim url = $"/api/system/prerequisites?names={joined}"
                Dim result = Await _httpClient.GetFromJsonAsync(Of PrerequisiteCheckResponse)(
                    url, cancellation)
                If result Is Nothing Then
                    Return New PrerequisiteCheckResponse With {
                        .Results = New List(Of PrerequisiteCheckResult)()
                    }
                End If
                If result.Results Is Nothing Then
                    result.Results = New List(Of PrerequisiteCheckResult)()
                End If
                Return result
            Catch ex As Exception
                Throw WrapException("CheckPrerequisites", ex)
            End Try
        End Function

        ' ---- Self-update push (Phase 8-2 slice 7) ----

        ''' <summary>
        ''' Stage a binary on the node via the chunked staged-binary endpoint:
        ''' SHA-256 + size the local file, POST begin, stream it in fixed-size
        ''' chunks (append-only, offset-validated — a 409 mismatch re-seeks to
        ''' the node's reported offset and resumes), then commit (the node
        ''' re-verifies size + SHA-256 over the whole file before atomic-renaming
        ''' it to the target's ".new"). Sourcing-decoupled: the caller supplies a
        ''' local file path; release-feed download/verify is layered on later.
        ''' targetName is the node-side target ("node" today; "shim" /
        ''' "nodesetup" land in later slices). Throws NodeApiException /
        ''' NodeConnectionException on failure; returns the node's commit result
        ''' (target + staged path + version) on success. Uses a one-shot
        ''' infinite-timeout client (same rationale as UploadFileAsync) so a
        ''' tens-of-MB push isn't chopped by the shared 30s timeout.
        ''' </summary>
        Public Async Function StageBinaryAsync(targetName As String,
                                               localFilePath As String,
                                               version As String,
                                               cancellation As CancellationToken) As Task(Of NodeStageResult)
            If String.IsNullOrEmpty(localFilePath) OrElse Not File.Exists(localFilePath) Then
                Throw New NodeApiException($"StageBinary: local file not found: {localFilePath}")
            End If

            Dim target = If(String.IsNullOrWhiteSpace(targetName), "node", targetName.Trim())
            Dim totalBytes As Long = New FileInfo(localFilePath).Length
            Dim sha As String = Await ComputeFileSha256Async(localFilePath, cancellation)

            Using pushClient As HttpClient = CreateOneShotClient()

                Try
                    ' --- begin ---
                    Dim beginBody = New With {.targetName = target, .totalBytes = totalBytes, .sha256 = sha, .version = version}
                    Dim uploadId As String
                    Using beginResp = Await pushClient.PostAsJsonAsync("/api/system/staged-binary/begin", beginBody, cancellation)
                        beginResp.EnsureSuccessStatusCode()
                        Dim begun = Await beginResp.Content.ReadFromJsonAsync(Of StageBeginResponse)(cancellationToken:=cancellation)
                        If begun Is Nothing OrElse String.IsNullOrEmpty(begun.uploadId) Then
                            Throw New NodeApiException("StageBinary: begin returned no uploadId")
                        End If
                        uploadId = begun.uploadId
                    End Using

                    _logger.LogInformation("Staging '{Target}' to node ({Bytes} bytes, id {Id})...", target, totalBytes, uploadId)

                    ' --- chunk* ---
                    Const chunkSize As Integer = 8 * 1024 * 1024
                    Dim buffer(chunkSize - 1) As Byte
                    Dim offset As Long = 0
                    Using fs As New FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync:=True)
                        While offset < totalBytes
                            ' Re-seek defensively (a prior 409 may have moved offset).
                            If fs.Position <> offset Then fs.Seek(offset, SeekOrigin.Begin)
                            Dim toRead As Integer = CInt(Math.Min(CLng(chunkSize), totalBytes - offset))
                            Dim read As Integer = 0
                            While read < toRead
                                Dim n = Await fs.ReadAsync(buffer, read, toRead - read, cancellation)
                                If n <= 0 Then Exit While
                                read += n
                            End While
                            If read <= 0 Then Exit While

                            Dim chunkUrl = $"/api/system/staged-binary/{uploadId}/chunk?offset={offset}"
                            Using content As New ByteArrayContent(buffer, 0, read)
                                content.Headers.ContentType = New System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream")
                                Using chunkResp = Await pushClient.PostAsync(chunkUrl, content, cancellation)
                                    If chunkResp.StatusCode = HttpStatusCode.Conflict Then
                                        ' Node reports where it actually is — resync and retry this chunk.
                                        Dim resync = Await ReadExpectedOffsetAsync(chunkResp, cancellation)
                                        If resync.HasValue Then
                                            _logger.LogWarning("Stage chunk offset resync for '{Target}': {Old} -> {New}", target, offset, resync.Value)
                                            offset = resync.Value
                                            Continue While
                                        End If
                                    End If
                                    chunkResp.EnsureSuccessStatusCode()
                                End Using
                            End Using
                            offset += read
                        End While
                    End Using

                    ' --- commit ---
                    Using commitResp = Await pushClient.PostAsync($"/api/system/staged-binary/{uploadId}/commit", Nothing, cancellation)
                        commitResp.EnsureSuccessStatusCode()
                        Dim committed = Await commitResp.Content.ReadFromJsonAsync(Of StageCommitResponse)(cancellationToken:=cancellation)
                        _logger.LogInformation("Staged '{Target}' on node -> {Path}", target, If(committed?.path, "(unknown)"))
                        Return New NodeStageResult With {
                            .Target = If(committed?.target, target),
                            .StagedPath = committed?.path,
                            .Version = If(committed?.version, version)
                        }
                    End Using

                Catch ex As Exception
                    Throw WrapException("StageBinary", ex)
                End Try
            End Using
        End Function

        ''' <summary>
        ''' Trigger the node's graceful update-exit for a staged target. The node
        ''' replies 202 (it detaches its shims, then a survivor swaps ".new" over
        ''' the live binary and relaunches) or 409 if nothing is staged. Returns
        ''' the survivor the node chose; throws NodeApiException (StatusCode =
        ''' Conflict) when there's no staged update. The node tears down right
        ''' after replying, so calls immediately after this fail until it
        ''' relaunches — the caller polls /api/version to confirm the new build.
        ''' </summary>
        Public Async Function ApplyUpdateAsync(targetName As String,
                                               cancellation As CancellationToken) As Task(Of NodeApplyUpdateResult)
            Dim target = If(String.IsNullOrWhiteSpace(targetName), "node", targetName.Trim())
            Try
                Dim url = $"/api/system/apply-update?target={Uri.EscapeDataString(target)}"
                Using resp = Await _httpClient.PostAsync(url, Nothing, cancellation)
                    resp.EnsureSuccessStatusCode()   ' 202 = success; 409 throws -> NodeApiException(Conflict)
                    Dim applied = Await resp.Content.ReadFromJsonAsync(Of StageApplyResponse)(cancellationToken:=cancellation)
                    _logger.LogInformation("Apply-update accepted by node for '{Target}' (survivor {Survivor})", target, If(applied?.survivor, "(unknown)"))
                    Return New NodeApplyUpdateResult With {.Survivor = applied?.survivor}
                End Using
            Catch ex As Exception
                Throw WrapException("ApplyUpdate", ex)
            End Try
        End Function

        ''' <summary>SHA-256 (lowercase hex) of a file, streamed.</summary>
        Private Shared Async Function ComputeFileSha256Async(path As String, ct As CancellationToken) As Task(Of String)
            Using sha = System.Security.Cryptography.SHA256.Create()
                Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync:=True)
                    Dim hash = Await sha.ComputeHashAsync(fs, ct)
                    Return Convert.ToHexString(hash).ToLowerInvariant()
                End Using
            End Using
        End Function

        ''' <summary>Pull the node's reported expectedOffset off a 409 chunk response, or Nothing.</summary>
        Private Shared Async Function ReadExpectedOffsetAsync(resp As HttpResponseMessage, ct As CancellationToken) As Task(Of Long?)
            Try
                Dim s = Await resp.Content.ReadAsStringAsync(ct)
                Using doc = System.Text.Json.JsonDocument.Parse(s)
                    Dim el As System.Text.Json.JsonElement = Nothing
                    If doc.RootElement.TryGetProperty("expectedOffset", el) Then
                        Return el.GetInt64()
                    End If
                End Using
            Catch
            End Try
            Return Nothing
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

        Public Async Function UpdateParseRulesAsync(instanceId As String,
                                                     rules As IList(Of LogParseRule),
                                                     cancellation As CancellationToken) As Task Implements INodeClient.UpdateParseRulesAsync
            ' Manager-side caller is the reconnect path — if the
            ' node is older than this endpoint we want a clean
            ' NodeApiException(StatusCode=NotFound) bubbling up
            ' for the caller's catch-and-proceed handler, not a
            ' generic connection error. WrapException already
            ' translates HTTP-status errors into NodeApiException
            ' with the StatusCode populated, so the caller can
            ' branch on that.
            Try
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/parse-rules"
                Dim resp = Await _httpClient.PostAsJsonAsync(url, rules, cancellation)
                resp.EnsureSuccessStatusCode()
            Catch ex As Exception
                Throw WrapException("UpdateParseRules", ex)
            End Try
        End Function

        Public Async Function UpdateBootFlagsAsync(instanceId As String,
                                                    request As BootFlagsRequest,
                                                    cancellation As CancellationToken) As Task(Of BootFlagsResponse) Implements INodeClient.UpdateBootFlagsAsync
            ' Older nodes 404 here — WrapException turns that into a
            ' NodeApiException(StatusCode=NotFound) the caller catches
            ' and treats as "flags apply from the next start".
            Try
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/boot-flags"
                Dim resp = Await _httpClient.PostAsJsonAsync(url, request, cancellation)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of BootFlagsResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("UpdateBootFlags", ex)
            End Try
        End Function

        Public Async Function ForgetInstanceAsync(instanceId As String,
                                                   cancellation As CancellationToken) As Task(Of ForgetInstanceResponse) Implements INodeClient.ForgetInstanceAsync
            ' 409 Conflict carries a ForgetInstanceResponse body with
            ' Refused=True — surface it as a value, not an exception,
            ' so the delete flow can tell "stop it first" apart from
            ' "node unreachable". Other failures (incl. 404 on older
            ' nodes) wrap as usual.
            Try
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/forget"
                Dim resp = Await _httpClient.PostAsync(url, Nothing, cancellation)
                If resp.StatusCode = HttpStatusCode.Conflict Then
                    Dim refused = Await resp.Content.ReadFromJsonAsync(Of ForgetInstanceResponse)(cancellationToken:=cancellation)
                    If refused Is Nothing Then
                        refused = New ForgetInstanceResponse With {
                            .Removed = False, .Refused = True,
                            .Message = "Instance is running on the node"
                        }
                    End If
                    Return refused
                End If
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of ForgetInstanceResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("ForgetInstance", ex)
            End Try
        End Function

        Public Async Function ParkInstanceAsync(request As StartInstanceRequest,
                                                 cancellation As CancellationToken) As Task(Of ParkInstanceResponse) Implements INodeClient.ParkInstanceAsync
            ' 409 = the node tracks the instance as running (flags go
            ' through /boot-flags there) — surfaced as a value. 404 on
            ' older nodes wraps as NodeApiException(NotFound) for the
            ' caller's fall-back-to-next-start handling.
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync("/api/instances/park", request, cancellation)
                If resp.StatusCode = HttpStatusCode.Conflict Then
                    Dim refused = Await resp.Content.ReadFromJsonAsync(Of ParkInstanceResponse)(cancellationToken:=cancellation)
                    If refused Is Nothing Then
                        refused = New ParkInstanceResponse With {
                            .Parked = False, .Refused = True,
                            .Message = "Instance is running on the node"
                        }
                    End If
                    Return refused
                End If
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of ParkInstanceResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("ParkInstance", ex)
            End Try
        End Function

        Public Async Function GetEventSourceDiagnosticsAsync(instanceId As String,
                                                              cancellation As CancellationToken) As Task(Of IReadOnlyList(Of EventSourceDiagnostics)) Implements INodeClient.GetEventSourceDiagnosticsAsync
            ' Diagnostics window plumbing (2026-08-14). 404 on
            ' pre-stream nodes wraps as NodeApiException(NotFound);
            ' the caller renders "not supported by this node".
            Try
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/events/sources"
                Dim result = Await _httpClient.GetFromJsonAsync(Of List(Of EventSourceDiagnostics))(url, cancellation)
                Return If(result, New List(Of EventSourceDiagnostics))
            Catch ex As Exception
                Throw WrapException("GetEventSourceDiagnostics", ex)
            End Try
        End Function

        ' ---- Diagnostics Wave 2 (2026-08-15). All 404 on older
        '      nodes → NodeApiException(NotFound) → the Diagnostics
        '      window renders "not supported by this node". ----

        Public Async Function GetEventWatermarksAsync(instanceId As String,
                                                       cancellation As CancellationToken) As Task(Of EventWatermarkDiagnosticsResponse) Implements INodeClient.GetEventWatermarksAsync
            Try
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/events/watermarks"
                Return Await _httpClient.GetFromJsonAsync(Of EventWatermarkDiagnosticsResponse)(url, cancellation)
            Catch ex As Exception
                Throw WrapException("GetEventWatermarks", ex)
            End Try
        End Function

        Public Async Function GetParseTraceAsync(instanceId As String,
                                                  cancellation As CancellationToken) As Task(Of ParseTraceResponse) Implements INodeClient.GetParseTraceAsync
            Try
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/events/parsetrace"
                Return Await _httpClient.GetFromJsonAsync(Of ParseTraceResponse)(url, cancellation)
            Catch ex As Exception
                Throw WrapException("GetParseTrace", ex)
            End Try
        End Function

        Public Async Function RearmParseTraceAsync(instanceId As String,
                                                    cancellation As CancellationToken) As Task(Of ParseTraceResponse) Implements INodeClient.RearmParseTraceAsync
            Try
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/events/parsetrace/rearm"
                Dim resp = Await _httpClient.PostAsync(url, Nothing, cancellation)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of ParseTraceResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("RearmParseTrace", ex)
            End Try
        End Function

        Public Async Function TestParseLineAsync(instanceId As String,
                                                  request As RuleDryRunRequest,
                                                  cancellation As CancellationToken) As Task(Of RuleDryRunResponse) Implements INodeClient.TestParseLineAsync
            Try
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/events/test-line"
                Dim resp = Await _httpClient.PostAsJsonAsync(url, request, cancellation)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of RuleDryRunResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("TestParseLine", ex)
            End Try
        End Function

        Public Async Function GetBootStateAsync(instanceId As String,
                                                 cancellation As CancellationToken) As Task(Of BootStateResponse) Implements INodeClient.GetBootStateAsync
            Try
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/boot-state"
                Return Await _httpClient.GetFromJsonAsync(Of BootStateResponse)(url, cancellation)
            Catch ex As Exception
                Throw WrapException("GetBootState", ex)
            End Try
        End Function

        Public Async Function GetShimStateAsync(instanceId As String,
                                                 cancellation As CancellationToken) As Task(Of ShimStateResponse) Implements INodeClient.GetShimStateAsync
            Try
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/shim-state"
                Return Await _httpClient.GetFromJsonAsync(Of ShimStateResponse)(url, cancellation)
            Catch ex As Exception
                Throw WrapException("GetShimState", ex)
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

        ' ---- File operations (Phase 4c-1) ----

        Public Async Function ListFilesAsync(instanceId As String,
                                              installPath As String,
                                              path As String,
                                              allowedRoots As IReadOnlyList(Of String),
                                              allowedExtensions As IReadOnlyList(Of String),
                                              cancellation As CancellationToken) As Task(Of IReadOnlyList(Of FileEntry)) Implements INodeClient.ListFilesAsync
            Try
                Dim qs = BuildFilesQueryString(installPath, path, allowedRoots, allowedExtensions)
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/files?{qs}"
                Dim result = Await _httpClient.GetFromJsonAsync(Of List(Of FileEntry))(url, cancellation)
                Return If(result, New List(Of FileEntry)())
            Catch ex As Exception
                Throw WrapException("ListFiles", ex)
            End Try
        End Function

        Public Async Function DownloadFileAsync(instanceId As String,
                                                 installPath As String,
                                                 path As String,
                                                 allowedRoots As IReadOnlyList(Of String),
                                                 allowedExtensions As IReadOnlyList(Of String),
                                                 destination As Stream,
                                                 cancellation As CancellationToken) As Task Implements INodeClient.DownloadFileAsync
            Try
                Dim qs = BuildFilesQueryString(installPath, path, allowedRoots, allowedExtensions)
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/files/download?{qs}"

                ' ResponseHeadersRead returns once headers arrive; the
                ' body stream that follows is bounded only by the
                ' caller's cancellation token, NOT by the shared
                ' HttpClient.Timeout. Important for big saves where
                ' the whole transfer takes longer than the 30s default.
                Using requestMsg As New HttpRequestMessage(HttpMethod.Get, url)
                    Using response = Await _httpClient.SendAsync(requestMsg,
                            HttpCompletionOption.ResponseHeadersRead, cancellation)
                        response.EnsureSuccessStatusCode()
                        Using stream = Await response.Content.ReadAsStreamAsync(cancellation)
                            Await stream.CopyToAsync(destination, cancellation)
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Throw WrapException("DownloadFile", ex)
            End Try
        End Function

        Public Async Function UploadFileAsync(instanceId As String,
                                               installPath As String,
                                               path As String,
                                               allowedRoots As IReadOnlyList(Of String),
                                               allowedExtensions As IReadOnlyList(Of String),
                                               source As Stream,
                                               overwrite As Boolean,
                                               cancellation As CancellationToken) As Task(Of FileEntry) Implements INodeClient.UploadFileAsync
            ' The shared _httpClient has Timeout = 30s, which applies
            ' to the WHOLE send operation including the request body.
            ' A 100MB save over a slow link would trip it. Use a
            ' one-shot client with InfiniteTimeSpan so the body send
            ' is bounded only by the caller's cancellation token.
            ' The cost is one extra TCP/TLS handshake per upload —
            ' negligible compared to file-transfer time at the sizes
            ' that motivated this design.
            Using uploadClient As HttpClient = CreateOneShotClient()

                Try
                    Dim qs = BuildFilesQueryString(installPath, path, allowedRoots, allowedExtensions)
                    Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/files/upload?{qs}&overwrite={If(overwrite, "true", "false")}"

                    Using content As New StreamContent(source)
                        content.Headers.ContentType =
                            New System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream")
                        Using resp = Await uploadClient.PostAsync(url, content, cancellation)
                            resp.EnsureSuccessStatusCode()
                            Return Await resp.Content.ReadFromJsonAsync(Of FileEntry)(cancellationToken:=cancellation)
                        End Using
                    End Using
                Catch ex As Exception
                    Throw WrapException("UploadFile", ex)
                End Try
            End Using
        End Function

        Public Async Function DeleteFileAsync(instanceId As String,
                                               installPath As String,
                                               path As String,
                                               allowedRoots As IReadOnlyList(Of String),
                                               allowedExtensions As IReadOnlyList(Of String),
                                               cancellation As CancellationToken) As Task(Of Boolean) Implements INodeClient.DeleteFileAsync
            Try
                Dim qs = BuildFilesQueryString(installPath, path, allowedRoots, allowedExtensions)
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/files?{qs}"
                Using resp = Await _httpClient.DeleteAsync(url, cancellation)
                    resp.EnsureSuccessStatusCode()
                    ' Endpoint returns { deleted: true } when it removed
                    ' the file or { deleted: false, reason: ... } when
                    ' the file was already gone. Surface the boolean
                    ' for the caller; reason is informational only.
                    Dim body = Await resp.Content.ReadFromJsonAsync(Of DeleteFileResult)(cancellationToken:=cancellation)
                    Return body IsNot Nothing AndAlso body.deleted
                End Using
            Catch ex As Exception
                Throw WrapException("DeleteFile", ex)
            End Try
        End Function

        Public Async Function RenameFileAsync(instanceId As String,
                                               installPath As String,
                                               path As String,
                                               newPath As String,
                                               allowedRoots As IReadOnlyList(Of String),
                                               allowedExtensions As IReadOnlyList(Of String),
                                               overwrite As Boolean,
                                               cancellation As CancellationToken) As Task(Of FileEntry) Implements INodeClient.RenameFileAsync
            Try
                ' Standard query-string fragment + the rename-specific
                ' newPath / overwrite knobs. Server validates both
                ' source and destination against allowedRoots and
                ' allowedExtensions; the wrapper does no validation
                ' of its own — same trust model as the other file ops.
                Dim qs = BuildFilesQueryString(installPath, path, allowedRoots, allowedExtensions)
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/files/rename?{qs}" &
                          $"&newPath={Uri.EscapeDataString(If(newPath, ""))}" &
                          $"&overwrite={If(overwrite, "true", "false")}"

                ' POST with empty body — all parameters travel in the
                ' query string. Shared _httpClient is fine (no body to
                ' bound the timeout against, unlike upload).
                Using req As New HttpRequestMessage(HttpMethod.Post, url)
                    Using resp = Await _httpClient.SendAsync(req, cancellation)
                        resp.EnsureSuccessStatusCode()
                        Return Await resp.Content.ReadFromJsonAsync(Of FileEntry)(cancellationToken:=cancellation)
                    End Using
                End Using
            Catch ex As Exception
                Throw WrapException("RenameFile", ex)
            End Try
        End Function

        Public Async Function CopyFileAsync(instanceId As String,
                                             installPath As String,
                                             path As String,
                                             newPath As String,
                                             allowedRoots As IReadOnlyList(Of String),
                                             allowedExtensions As IReadOnlyList(Of String),
                                             overwrite As Boolean,
                                             cancellation As CancellationToken) As Task(Of FileEntry) Implements INodeClient.CopyFileAsync
            Try
                ' Identical wire shape to rename: query-string
                ' params, no body, returns FileEntry of the new file.
                ' The shared _httpClient's 30s timeout is fine here
                ' too — no body to upload, and even a 100MB local
                ' File.Copy completes in well under a second on any
                ' modern disk.
                Dim qs = BuildFilesQueryString(installPath, path, allowedRoots, allowedExtensions)
                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/files/copy?{qs}" &
                          $"&newPath={Uri.EscapeDataString(If(newPath, ""))}" &
                          $"&overwrite={If(overwrite, "true", "false")}"

                Using req As New HttpRequestMessage(HttpMethod.Post, url)
                    Using resp = Await _httpClient.SendAsync(req, cancellation)
                        resp.EnsureSuccessStatusCode()
                        Return Await resp.Content.ReadFromJsonAsync(Of FileEntry)(cancellationToken:=cancellation)
                    End Using
                End Using
            Catch ex As Exception
                Throw WrapException("CopyFile", ex)
            End Try
        End Function

        Public Async Function GenerateMapAsync(instanceId As String,
                                                 request As GenerateMapRequest,
                                                 cancellation As CancellationToken) As Task(Of GenerateMapResponse) Implements INodeClient.GenerateMapAsync
            ' Map gen can run for minutes on large worlds. The
            ' shared _httpClient.Timeout is 30s, which would chop
            ' the call off long before Factorio finishes a Ribbon
            ' World preset. Use the same one-shot pattern we use
            ' for upload: a fresh HttpClient with InfiniteTimeSpan,
            ' bounded only by the caller's CancellationToken.
            Dim oneShot As HttpClient = Nothing
            Try
                oneShot = CreateOneShotClient()

                Dim url = $"/api/instances/{Uri.EscapeDataString(instanceId)}/generate-map"
                Using resp = Await oneShot.PostAsJsonAsync(url, request, cancellation)
                    resp.EnsureSuccessStatusCode()
                    Return Await resp.Content.ReadFromJsonAsync(Of GenerateMapResponse)(
                        cancellationToken:=cancellation)
                End Using
            Catch ex As Exception
                Throw WrapException("GenerateMap", ex)
            Finally
                oneShot?.Dispose()
            End Try
        End Function

        ''' <summary>
        ''' Build the query-string fragment shared by all four file
        ''' operations. Each value is URL-encoded; the manager-supplied
        ''' arrays are joined with the separators the node expects
        ''' (";" for roots, "," for extensions). allowedExtensions is
        ''' omitted entirely when empty so the node falls back to
        ''' "any extension" rather than "empty allowlist".
        ''' </summary>
        Private Function BuildFilesQueryString(installPath As String,
                                                path As String,
                                                allowedRoots As IReadOnlyList(Of String),
                                                allowedExtensions As IReadOnlyList(Of String)) As String
            Dim parts As New List(Of String) From {
                "installPath=" & Uri.EscapeDataString(If(installPath, "")),
                "path=" & Uri.EscapeDataString(If(path, "")),
                "allowedRoots=" & Uri.EscapeDataString(
                    If(allowedRoots Is Nothing, "", String.Join(";"c, allowedRoots)))
            }
            If allowedExtensions IsNot Nothing AndAlso allowedExtensions.Count > 0 Then
                parts.Add("allowedExtensions=" & Uri.EscapeDataString(
                    String.Join(","c, allowedExtensions)))
            End If
            Return String.Join("&"c, parts)
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

        ' ---- Event stream (EventStream Slice 3) ----

        Public Async Function GetEventsAsync(instanceId As String,
                                              sinceSeq As Long,
                                              max As Integer,
                                              cancellation As CancellationToken) As Task(Of EventBatchResponse) Implements INodeClient.GetEventsAsync
            Try
                Dim result = Await _httpClient.GetFromJsonAsync(Of EventBatchResponse)(
                    $"/api/instances/{instanceId}/events?since={sinceSeq}&max={max}", cancellation)
                If result Is Nothing Then
                    result = New EventBatchResponse With {
                        .Events = New List(Of EventRecord),
                        .LastSeq = sinceSeq
                    }
                End If
                If result.Events Is Nothing Then result.Events = New List(Of EventRecord)
                Return result
            Catch ex As Exception
                Throw WrapException("GetEvents", ex)
            End Try
        End Function

        Public Async Function GetEventsHeadAsync(instanceId As String,
                                                  cancellation As CancellationToken) As Task(Of Long) Implements INodeClient.GetEventsHeadAsync
            Try
                Dim result = Await _httpClient.GetFromJsonAsync(Of EventBatchResponse)(
                    $"/api/instances/{instanceId}/events?head=1", cancellation)
                Return If(result IsNot Nothing, result.LastSeq, 0L)
            Catch ex As Exception
                Throw WrapException("GetEventsHead", ex)
            End Try
        End Function

        ''' <summary>
        ''' JSON options for SSE event frames — the node serializes
        ''' each frame with JsonSerializerDefaults.Web (camelCase),
        ''' the same shape GetFromJsonAsync uses for /events, so
        ''' backfill-by-GET and live-by-SSE deserialize identically.
        ''' </summary>
        Private Shared ReadOnly _sseEventJsonOptions As New System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web)

        ''' <summary>
        ''' Idle cutoff for the SSE event stream. The node writes a
        ''' ": keepalive" comment every ~15s when no events flow, so
        ''' a healthy connection never goes this long without bytes.
        ''' A read that exceeds it means the TCP path died silently
        ''' (no RST reached us) — treated as a stream failure so the
        ''' caller can fall back to pull. Older stream-capable nodes
        ''' without the keep-alive still work: their idle streams
        ''' recycle through this timeout and the caller reconnects,
        ''' which the cursor makes harmless.
        ''' </summary>
        Private Shared ReadOnly SseIdleTimeout As TimeSpan = TimeSpan.FromSeconds(45)

        Public Async Function StreamEventsAsync(instanceId As String,
                                                 sinceSeq As Long,
                                                 onEvent As Func(Of EventRecord, Task),
                                                 cancellation As CancellationToken) As Task Implements INodeClient.StreamEventsAsync
            Try
                Using requestMsg As New HttpRequestMessage(HttpMethod.Get,
                        $"/api/instances/{instanceId}/events/stream?since={sinceSeq}")
                    ' ResponseHeadersRead: SendAsync returns at the
                    ' headers; subsequent body reads are bounded by
                    ' the caller's token + the per-read idle timeout
                    ' below, NOT by the shared client's 30s Timeout
                    ' (same long-lived-stream pattern as
                    ' StreamLogsAsync / DownloadFileAsync).
                    Using response = Await _httpClient.SendAsync(requestMsg,
                            HttpCompletionOption.ResponseHeadersRead, cancellation)
                        response.EnsureSuccessStatusCode()
                        Using stream = Await response.Content.ReadAsStreamAsync(cancellation)
                            Using reader As New StreamReader(stream)
                                While Not cancellation.IsCancellationRequested
                                    Dim line As String = Nothing
                                    Dim idleTimedOut = False
                                    ' Per-read idle timeout via a linked
                                    ' CTS — keep-alive comments count as
                                    ' traffic, so only a dead transport
                                    ' trips this.
                                    Using idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
                                        idleCts.CancelAfter(SseIdleTimeout)
                                        Try
                                            line = Await reader.ReadLineAsync(idleCts.Token)
                                        Catch ex As OperationCanceledException
                                            If cancellation.IsCancellationRequested Then Throw
                                            idleTimedOut = True
                                        End Try
                                    End Using
                                    If idleTimedOut Then
                                        Throw New TimeoutException(
                                            $"Event stream for {instanceId} idle beyond {SseIdleTimeout.TotalSeconds:F0}s (no data or keep-alive)")
                                    End If
                                    If line Is Nothing Then Exit While ' server closed the stream

                                    If line.StartsWith("data: ") Then
                                        Dim rec As EventRecord = Nothing
                                        Try
                                            rec = System.Text.Json.JsonSerializer.Deserialize(Of EventRecord)(
                                                line.Substring(6), _sseEventJsonOptions)
                                        Catch
                                            ' Malformed frame — skip it; the
                                            ' cursor-addressed re-read on the
                                            ' next reconnect can recover it.
                                        End Try
                                        If rec IsNot Nothing Then
                                            ' Awaited: consumer exceptions
                                            ' propagate and tear the stream
                                            ' down by design.
                                            Await onEvent(rec)
                                        End If
                                    End If
                                    ' ": keepalive" comments and blank
                                    ' separator lines fall through here.
                                End While
                            End Using
                        End Using
                    End Using
                End Using
            Catch ex As OperationCanceledException
                ' Normal — caller cancelled; return quietly like the
                ' other streaming methods.
            End Try
        End Function

        Public Async Function AppendIdentityEventAsync(instanceId As String,
                                                        request As AppendEventRequest,
                                                        cancellation As CancellationToken) As Task(Of AppendEventResponse) Implements INodeClient.AppendIdentityEventAsync
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/events", request, cancellation)
                resp.EnsureSuccessStatusCode()
                Dim body = Await resp.Content.ReadFromJsonAsync(Of AppendEventResponse)(
                    cancellationToken:=cancellation)
                Return If(body, New AppendEventResponse With {.Inserted = False})
            Catch ex As Exception
                Throw WrapException("AppendIdentityEvent", ex)
            End Try
        End Function

        ' ---- Helpers ----

        Private Function WrapException(operation As String, ex As Exception) As Exception
            ' HttpRequestException covers TWO different failure
            ' modes that callers want to disambiguate:
            '   - Request never reached the server (DNS failure,
            '     connection refused, network unreachable). The
            '     exception's StatusCode is Nothing.
            '   - Server returned a non-success HTTP status (404,
            '     409, 500). EnsureSuccessStatusCode /
            '     GetFromJsonAsync raise this one with StatusCode
            '     populated.
            '
            ' UI-side handlers (InstanceFileEditorPanel's
            ' "missing file is fine, render defaults" path,
            ' UploadFile's overwrite=false 409 disambiguation,
            ' etc.) need the latter category exposed as
            ' NodeApiException carrying the status code. Treating
            ' both as NodeConnectionException — as we did before —
            ' makes 404 look like "the node is offline" and bypasses
            ' those graceful-handling paths.
            ' Pin-validation failures surface as HttpRequestException
            ' wrapping an AuthenticationException ("The remote
            ' certificate is invalid according to the validation
            ' procedure"). Expose them as NodeCertificateException so
            ' the UI can say "certificate changed — re-trust required"
            ' instead of a generic "node offline".
            If ContainsAuthenticationFailure(ex) Then
                ' Only a pin REJECTION is a certificate problem. Any other
                ' handshake failure (plaintext listener on the port, node
                ' mid-restart, protocol mismatch) is a connection problem
                ' and must not trip the MITM/re-trust path.
                If Threading.Interlocked.Exchange(_pinRejected, 0) <> 0 Then
                    Return New NodeCertificateException(
                        $"TLS certificate validation failed during {operation}: the node's certificate " &
                        "does not match the pinned fingerprint. Possible causes: the node regenerated " &
                        "its certificate, or the connection is being intercepted.", ex)
                End If
                Return New NodeConnectionException(
                    $"TLS handshake failed during {operation}: the port did not answer as HTTPS " &
                    "(node restarting, or plain HTTP on this port). {ex.Message}", ex)
            End If

            Dim httpEx = TryCast(ex, HttpRequestException)
            If httpEx IsNot Nothing Then
                If httpEx.StatusCode.HasValue Then
                    Return New NodeApiException(
                        $"API error during {operation}: HTTP {CInt(httpEx.StatusCode.Value)} ({httpEx.StatusCode.Value})",
                        httpEx, httpEx.StatusCode)
                End If
                Return New NodeConnectionException(
                    $"Connection failed during {operation}: {ex.Message}", ex)
            End If
            Return New NodeApiException(
                $"API error during {operation}: {ex.Message}", ex)
        End Function

        Private Shared Function ContainsAuthenticationFailure(ex As Exception) As Boolean
            Dim cur = ex
            Dim depth = 0
            While cur IsNot Nothing AndAlso depth < 8
                If TypeOf cur Is System.Security.Authentication.AuthenticationException Then
                    Return True
                End If
                cur = cur.InnerException
                depth += 1
            End While
            Return False
        End Function

        ' ---- TLS onboarding (0.7.0) ----

        Public Async Function EnableTlsAsync(request As EnableTlsRequest,
                                             cancellation As CancellationToken) As Task(Of EnableTlsResponse) Implements INodeClient.EnableTlsAsync
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync("/api/tls/enable", request, cancellation)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadFromJsonAsync(Of EnableTlsResponse)(cancellationToken:=cancellation)
            Catch ex As Exception
                Throw WrapException("EnableTls", ex)
            End Try
        End Function

        Public Async Function GetTlsStatusAsync(cancellation As CancellationToken) As Task(Of TlsStatusResponse) Implements INodeClient.GetTlsStatusAsync
            Try
                Return Await _httpClient.GetFromJsonAsync(Of TlsStatusResponse)(
                    "/api/tls/status", cancellation)
            Catch ex As Exception
                Throw WrapException("GetTlsStatus", ex)
            End Try
        End Function

        ''' <summary>
        ''' Re-trust / adopt fallback (2026-10-10): read the SHA-256
        ''' fingerprint of whatever certificate the node is CURRENTLY
        ''' presenting on <paramref name="port"/>, over an UNPINNED,
        ''' UNAUTHENTICATED HTTPS GET of /api/version. Previously the only
        ''' way to learn a changed fingerprint was the authenticated plain-
        ''' HTTP /api/tls/status call — which is exactly the channel the
        ''' recommended hardening (Restrict HTTP) closes, so a cert
        ''' regeneration on a hardened node locked the Manager out with no
        ''' in-app recovery.
        '''
        ''' Safe by construction: no bearer token is attached, the only
        ''' request is the public version endpoint, nothing is stored, and
        ''' the value returned is the hash of the certificate actually
        ''' presented on the wire (not a JSON field the peer could lie
        ''' about). The TRUST decision stays with the operator's explicit
        ''' re-trust confirmation; this merely tells them what they would
        ''' be trusting — the same information the mismatch log line
        ''' already prints. Shared: it deliberately bypasses the instance
        ''' pinned handler. Returns Nothing when nothing answered TLS.
        ''' </summary>
        Public Shared Async Function ProbePresentedFingerprintAsync(hostAddress As String,
                                                                    port As Integer,
                                                                    cancellation As CancellationToken) As Task(Of String)
            Dim presentedHex As String = Nothing
            Dim handler As New HttpClientHandler()
            handler.ServerCertificateCustomValidationCallback =
                Function(request, cert, chain, errors)
                    If cert IsNot Nothing Then
                        presentedHex = Convert.ToHexString(
                            cert.GetCertHash(Security.Cryptography.HashAlgorithmName.SHA256)).ToLowerInvariant()
                    End If
                    Return True   ' observe only — this client never carries a token
                End Function
            Try
                Using http As New HttpClient(handler, disposeHandler:=True)
                    http.Timeout = TimeSpan.FromSeconds(10)
                    Using resp = Await http.GetAsync($"https://{hostAddress}:{port}/api/version",
                                                     HttpCompletionOption.ResponseHeadersRead,
                                                     cancellation).ConfigureAwait(False)
                        ' Status is irrelevant: the handshake already ran.
                    End Using
                End Using
            Catch
                ' Not listening TLS on this port / network error — caller
                ' decides what to tell the operator.
            End Try
            Return presentedHex
        End Function

        ''' <summary>
        ''' Changes the node's plain-HTTP listener policy (0.7.0
        ''' hardening). Concrete-class-only (not on INodeClient — it is
        ''' an onboarding/hardening concern, always invoked through
        ''' TlsOnboardingService with an explicit client). The node
        ''' restarts after replying.
        ''' </summary>
        Public Async Function SetHttpModeAsync(httpMode As String,
                                               cancellation As CancellationToken) As Task
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync("/api/tls/http-mode",
                    New SetHttpModeRequest With {.HttpMode = httpMode}, cancellation)
                resp.EnsureSuccessStatusCode()
            Catch ex As Exception
                Throw WrapException("SetHttpMode", ex)
            End Try
        End Function

        ''' <summary>
        ''' Rotates the node's auth token (POST /api/auth/rotate). Only
        ''' meaningful on a pinned-HTTPS client — the node refuses the call
        ''' over plaintext (403). The node persists + restarts after replying.
        ''' Concrete-class-only; invoked via TlsOnboardingService.
        ''' </summary>
        Public Async Function RotateTokenAsync(newToken As String,
                                               cancellation As CancellationToken) As Task
            Try
                Dim resp = Await _httpClient.PostAsJsonAsync("/api/auth/rotate",
                    New RotateTokenRequest With {.NewToken = newToken}, cancellation)
                resp.EnsureSuccessStatusCode()
            Catch ex As Exception
                Throw WrapException("RotateToken", ex)
            End Try
        End Function

        ' ---- Process control (2026-10-10) ----
        ' Concrete-class-only like SetHttpModeAsync: operator actions from the
        ' node panel, never reached by plugins. Older nodes answer 404.

        ''' <summary>
        ''' Asks the node to exit and be relaunched by its survivor
        ''' (NodeSetup / sc start / systemd). The node replies 202 then tears
        ''' down; the caller polls /api/version to see it come back.
        ''' </summary>
        Public Async Function RestartNodeAsync(cancellation As CancellationToken) As Task
            Try
                Using resp = Await _httpClient.PostAsync("/api/system/restart", Nothing, cancellation)
                    resp.EnsureSuccessStatusCode()
                End Using
            Catch ex As Exception
                Throw WrapException("RestartNode", ex)
            End Try
        End Function

        ''' <summary>
        ''' Asks the node to exit WITHOUT relaunch. It stays down until
        ''' started by hand on that machine. Running instances keep running
        ''' under their shims and are re-adopted when the node returns.
        ''' </summary>
        Public Async Function ShutdownNodeAsync(cancellation As CancellationToken) As Task
            Try
                Using resp = Await _httpClient.PostAsync("/api/system/shutdown", Nothing, cancellation)
                    resp.EnsureSuccessStatusCode()
                End Using
            Catch ex As Exception
                Throw WrapException("ShutdownNode", ex)
            End Try
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

    ''' <summary>
    ''' Deserialization shape for the DELETE /files endpoint's
    ''' response body. The endpoint always returns 200 — the
    ''' boolean disambiguates "actually deleted" from "file was
    ''' already gone" (idempotent path), and reason is a free-form
    ''' message used only when deleted=False. Field names are
    ''' lowercase to match the anonymous-object shape the node
    ''' returns.
    ''' </summary>
    Friend Class DeleteFileResult
        Public Property deleted As Boolean
        Public Property reason As String
    End Class

    ''' <summary>
    ''' Deserialization shape for the staged-binary begin response
    ''' (POST /api/system/staged-binary/begin -> { uploadId }). Field
    ''' name is lowercase to match the node's anonymous-object JSON;
    ''' ReadFromJsonAsync binds case-insensitively regardless.
    ''' </summary>
    Friend Class StageBeginResponse
        Public Property uploadId As String
    End Class

    ''' <summary>
    ''' Deserialization shape for the commit response
    ''' ({ staged, target, path, version }) the node returns after it
    ''' re-verifies size + SHA-256 and renames the .part to the
    ''' target's .new.
    ''' </summary>
    Friend Class StageCommitResponse
        Public Property staged As Boolean
        Public Property target As String
        Public Property path As String
        Public Property version As String
    End Class

    ''' <summary>
    ''' Deserialization shape for the apply-update 202 body
    ''' ({ accepted, survivor }).
    ''' </summary>
    Friend Class StageApplyResponse
        Public Property accepted As Boolean
        Public Property survivor As String
    End Class

    ''' <summary>Outcome of a successful StageBinaryAsync (the node's commit echo).</summary>
    Public Class NodeStageResult
        Public Property Target As String
        Public Property StagedPath As String
        Public Property Version As String
    End Class

    ''' <summary>Outcome of a successful ApplyUpdateAsync (the survivor the node chose).</summary>
    Public Class NodeApplyUpdateResult
        Public Property Survivor As String
    End Class

    ' ============================================================
    '  NodeHttpClientFactory — manages client instances per node
    ' ============================================================

    ''' <summary>
    ''' Per-node TLS transport settings, resolved by the factory's
    ''' TlsSettingsResolver (wired in ManagerProgram to a DB lookup of
    ''' NodeEntity.UseTls / TlsPort / PinnedCertFingerprint). Kept as a
    ''' dedicated class rather than a tuple for VB readability.
    ''' </summary>
    Public Class NodeTlsSettings
        Public Property UseTls As Boolean
        Public Property TlsPort As Integer
        Public Property PinnedCertFingerprint As String

        ''' <summary>
        ''' Single-port (HTTPS_SinglePort_Plan slice 3): TlsPort = 0 now
        ''' means "HTTPS on the node's listen port", so a pin alone makes
        ''' TLS usable. Callers resolve the port via EffectivePort(listen).
        ''' </summary>
        Public ReadOnly Property IsUsable As Boolean
            Get
                Return UseTls AndAlso Not String.IsNullOrWhiteSpace(PinnedCertFingerprint)
            End Get
        End Property

        ''' <summary>Port to dial for HTTPS: the dedicated TLS port when one
        ''' is recorded, else the node's plain listen port (shared).</summary>
        Public Function EffectivePort(listenPort As Integer) As Integer
            Return If(TlsPort > 0, TlsPort, listenPort)
        End Function
    End Class

    Public Class NodeHttpClientFactory

        Private ReadOnly _clients As New ConcurrentDictionary(Of String, NodeHttpClient)
        ' Transport signature each cached client was built with
        ' (scheme|port|pin). A mismatch means settings changed since the
        ' client was created — evict and rebuild.
        Private ReadOnly _signatures As New ConcurrentDictionary(Of String, String)
        Private ReadOnly _logger As ILogger(Of NodeHttpClient)

        Public Sub New(logger As ILogger(Of NodeHttpClient))
            _logger = logger
        End Sub

        ''' <summary>
        ''' Resolves a node's CURRENT TLS settings by nodeId. Wired once
        ''' at startup (ManagerProgram) to a GsmDbContext lookup. When
        ''' Nothing (e.g. unit-ish contexts), all clients are plain HTTP
        ''' — exactly the pre-0.7.0 behavior. Making the factory consult
        ''' this on every GetClient means the MANY existing 4-argument
        ''' call sites throughout Core/UI become TLS-aware without being
        ''' touched, and an onboarding flip takes effect on the next
        ''' GetClient call anywhere in the app.
        ''' </summary>
        Public Property TlsSettingsResolver As Func(Of String, NodeTlsSettings)

        ''' <summary>
        ''' Gets or creates an INodeClient for the given node.
        ''' </summary>
        Public Function GetClient(nodeId As String,
                                  hostAddress As String,
                                  port As Integer,
                                  authToken As String) As INodeClient
            ' Resolve the node's current transport settings. TLS wins
            ' when fully configured: scheme flips to https and the
            ' effective port becomes the TLS port.
            Dim tls As NodeTlsSettings = Nothing
            Dim resolver = TlsSettingsResolver
            If resolver IsNot Nothing Then
                Try
                    tls = resolver(nodeId)
                Catch
                    ' DOWNGRADE GUARD: a resolver failure (transient DB
                    ' hiccup) must not silently rebuild this node's client
                    ' on plain HTTP when we were already on pinned HTTPS —
                    ' that would leak the token in cleartext. Keep serving
                    ' the existing client, whatever transport it has; only
                    ' a node with no cached client at all falls back to
                    ' HTTP (matching a fresh pre-TLS start).
                    Dim cached As NodeHttpClient = Nothing
                    If _clients.TryGetValue(nodeId, cached) Then Return cached
                    tls = Nothing
                End Try
            End If

            Dim wantTls As Boolean = tls IsNot Nothing AndAlso tls.IsUsable
            Dim effectivePort As Integer = If(wantTls, tls.EffectivePort(port), port)
            Dim pin As String = If(wantTls, tls.PinnedCertFingerprint.Trim().ToLowerInvariant(), Nothing)
            Dim signature As String =
                If(wantTls, "https|", "http|") & effectivePort.ToString() & "|" & If(pin, "")

            ' Evict when the cached client's transport signature no
            ' longer matches (onboarding flip, rollback, re-pin, port
            ' change) so no caller can keep using a stale transport.
            Dim oldSig As String = Nothing
            If _signatures.TryGetValue(nodeId, oldSig) AndAlso
               Not String.Equals(oldSig, signature, StringComparison.Ordinal) Then
                RemoveClient(nodeId)
            End If

            Dim client = _clients.GetOrAdd(nodeId,
                Function(id) New NodeHttpClient(hostAddress, effectivePort, authToken, _logger,
                                                wantTls, pin))
            _signatures(nodeId) = signature
            Return client
        End Function

        ''' <summary>
        ''' Builds a NON-cached client with explicit transport settings.
        ''' Used by the TLS onboarding flow, which needs to talk plain
        ''' HTTP to a node whose entity may already carry TLS settings
        ''' (or probe HTTPS with a pin before committing it to the DB).
        ''' Caller owns the instance; nothing is cached or evicted.
        ''' </summary>
        Public Function CreateExplicitClient(hostAddress As String,
                                             port As Integer,
                                             authToken As String,
                                             useTls As Boolean,
                                             pinnedCertFingerprint As String) As NodeHttpClient
            Return New NodeHttpClient(hostAddress, port, authToken, _logger,
                                      useTls, pinnedCertFingerprint)
        End Function

        ''' <summary>
        ''' Removes and disposes the client for a node (e.g. when
        ''' node is removed or auth token changes).
        ''' </summary>
        Public Sub RemoveClient(nodeId As String)
            Dim removed As NodeHttpClient = Nothing
            _clients.TryRemove(nodeId, removed)
            Dim removedSig As String = Nothing
            _signatures.TryRemove(nodeId, removedSig)
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

        ''' <summary>
        ''' HTTP status code from the server response, when the
        ''' exception originated from a non-success status (404,
        ''' 409, 500, ...). Nothing when the exception came from a
        ''' non-HTTP source (e.g. JSON deserialization failure on
        ''' an otherwise-successful response). Lets callers branch
        ''' precisely — e.g. InstanceFileEditorPanel's IsNotFound
        ''' check, UploadFile's overwrite=false Conflict handling.
        ''' </summary>
        Public ReadOnly Property StatusCode As HttpStatusCode?

        Public Sub New(message As String,
                       Optional inner As Exception = Nothing,
                       Optional statusCode As HttpStatusCode? = Nothing)
            MyBase.New(message, inner)
            Me.StatusCode = statusCode
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

    ''' <summary>
    ''' Thrown when a pinned-HTTPS connection fails certificate
    ''' validation (0.7.0). Subclass of NodeConnectionException so
    ''' existing catch blocks still treat it as "can't reach the
    ''' node", while TLS-aware callers (onboarding service, NodePanel)
    ''' catch it specifically to surface "certificate changed —
    ''' re-trust required" and set TlsState=FingerprintMismatch.
    ''' </summary>
    Public Class NodeCertificateException
        Inherits NodeConnectionException

        Public Sub New(message As String, Optional inner As Exception = Nothing)
            MyBase.New(message, inner)
        End Sub
    End Class

    ' ============================================================
    '  NodePlatformResolver
    '
    '  Single-line wrapper over INodeClient.GetApiVersionAsync
    '  whose only job is hiding the boilerplate of "call the
    '  version endpoint, swallow failures, return Unknown when
    '  anything goes wrong". Used by InstanceManager,
    '  InstallationManager, and FileGenerationPanel before they
    '  invoke plugin methods so plugins can pick platform-specific
    '  paths (executable names, archive types, etc.) directly
    '  from InstanceConfig.Platform / InstallationConfig.Platform.
    '
    '  Cache-friendly: NodeHttpClient already caches the
    '  NodeVersionResponse in-memory after the first successful
    '  /api/version call, so subsequent invocations of this
    '  resolver against the same client are essentially free.
    '  No additional cache layer is needed here.
    '
    '  Returns NodePlatform.Unknown on any failure mode — network
    '  error, missing field on an old node, deserialization
    '  trouble. Plugins are expected to treat Unknown as "fall
    '  back to legacy best-effort behaviour" rather than aborting,
    '  which keeps cross-version manager/node combinations working.
    ' ============================================================

    ''' <summary>
    ''' Helper for callers that need the node's OS platform before
    ''' invoking plugin methods. Wraps INodeClient.GetApiVersionAsync
    ''' with try/catch so the call site doesn't have to repeat the
    ''' boilerplate. See the comment block above for full rationale.
    ''' </summary>
    Public Module NodePlatformResolver
        Public Async Function ResolveAsync(client As INodeClient,
                                            cancellation As CancellationToken) As Task(Of NodePlatform)
            If client Is Nothing Then Return NodePlatform.Unknown
            Try
                Dim ver = Await client.GetApiVersionAsync(False, cancellation)
                If ver Is Nothing Then Return NodePlatform.Unknown
                Return ver.Platform
            Catch
                Return NodePlatform.Unknown
            End Try
        End Function
    End Module

End Namespace