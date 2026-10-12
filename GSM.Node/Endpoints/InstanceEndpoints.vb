Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Http
Imports GSM.Node.Api
Imports GSM.Node
Imports GSM.Plugin

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

            ' Live boot-flag update ("Recover state on node restart" /
            ' "Auto start with node") — applies checkbox changes from
            ' the Edit Instance form without a start/stop cycle.
            ' Additive endpoint (2026-08-10); older Managers simply
            ' never call it.
            app.MapPost("/api/instances/{instanceId}/boot-flags",
                Async Function(instanceId As String,
                               context As HttpContext,
                               pm As ProcessManager) As Task(Of IResult)
                    Dim request As BootFlagsRequest = Nothing
                    Try
                        request = Await context.Request.ReadFromJsonAsync(Of BootFlagsRequest)()
                    Catch ex As Exception
                        Return Results.BadRequest(New With {.error = "Invalid request body: " & ex.Message})
                    End Try
                    If request Is Nothing Then
                        Return Results.BadRequest(New With {.error = "Request body missing"})
                    End If
                    Return Results.Ok(pm.UpdateBootFlags(instanceId,
                                                         request.ResurrectOnNodeBoot,
                                                         request.AutoStartOnNodeBoot,
                                                         request.BootStartDelaySeconds))
                End Function)

            ' Park launch settings without spawning: arms "Auto start
            ' with node" for a stopped instance that has no snapshot
            ' here yet. Body = the same StartInstanceRequest a real
            ' start sends. Additive endpoint (2026-08-10).
            app.MapPost("/api/instances/park",
                Function(request As StartInstanceRequest,
                         pm As ProcessManager) As IResult
                    Dim result = pm.ParkInstance(request)
                    If result.Refused Then
                        Return Results.Conflict(result)
                    End If
                    Return Results.Ok(result)
                End Function)

            ' Forget an instance the Manager is deleting: snapshot row
            ' (incl. parked "Auto start with node" rows) + flap-guard
            ' row go away so nothing can auto-start it at a later node
            ' boot. 409 while the instance is running here.
            app.MapPost("/api/instances/{instanceId}/forget",
                Function(instanceId As String,
                         pm As ProcessManager) As IResult
                    Dim result = pm.ForgetInstance(instanceId)
                    If result.Refused Then
                        Return Results.Conflict(result)
                    End If
                    Return Results.Ok(result)
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

            ' ---- Parsed events ----

            app.MapGet("/api/instances/{instanceId}/players",
                Function(instanceId As String,
                         eventStore As EventStore) As IResult
                    Return Results.Ok(eventStore.GetPlayers(instanceId))
                End Function)

            app.MapGet("/api/instances/{instanceId}/server-state",
                Function(instanceId As String,
                         eventStore As EventStore) As IResult
                    Return Results.Ok(eventStore.GetServerState(instanceId))
                End Function)

            app.MapGet("/api/instances/{instanceId}/chat",
                Function(instanceId As String,
                         context As HttpContext,
                         eventStore As EventStore) As IResult
                    Dim limit = 500
                    Integer.TryParse(context.Request.Query("limit").ToString(), limit)

                    Dim sinceUtc As DateTime? = Nothing
                    Dim sinceStr = context.Request.Query("since").ToString()
                    If Not String.IsNullOrEmpty(sinceStr) Then
                        Dim parsed As DateTime
                        If DateTime.TryParse(sinceStr, Nothing,
                                              Globalization.DateTimeStyles.RoundtripKind,
                                              parsed) Then
                            ' Treat Unspecified as Utc rather than calling
                            ' ToUniversalTime(). The parameter is named "since"
                            ' against a column named timestamp_utc; an
                            ' offset-less ISO string here means "this is the
                            ' UTC value, the sender just didn't put a Z on it."
                            ' ToUniversalTime() would interpret Unspecified as
                            ' Local and shift by the node's offset — silently
                            ' filtering out chats whose actual UTC times are
                            ' between the cursor and (cursor + offset). The
                            ' manager's SeedChatCursor was hitting this path
                            ' after every manager restart because EF Core's
                            ' SQLite provider drops DateTimeKind on read-back.
                            ' Even with that fixed at the source, a stricter
                            ' contract here is cheap defense in depth.
                            Select Case parsed.Kind
                                Case DateTimeKind.Utc
                                    sinceUtc = parsed
                                Case DateTimeKind.Local
                                    sinceUtc = parsed.ToUniversalTime()
                                Case Else
                                    sinceUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                            End Select
                        End If
                    End If
                    Return Results.Ok(eventStore.GetChatHistory(instanceId, sinceUtc, limit))
                End Function)

            ' Re-push declarative parse rules to a running instance
            ' without resetting the EventStore's in-memory state.
            ' Used by the Manager on reconnect after a node binary
            ' update or Manager restart — replaces the older
            ' "stop+start every instance to refresh rules" workflow,
            ' which kicked all players off as a side effect.
            ' EventStore.UpdateParseRules logs a warning and no-ops
            ' if the instance isn't currently registered (which
            ' surfaces here as 200 OK with updated=true, count=0 —
            ' rules-by-themselves can't bootstrap state, only
            ' StartInstance can, so there's nothing useful for the
            ' Manager to do with a distinguished response).
            app.MapPost("/api/instances/{instanceId}/parse-rules",
                Async Function(instanceId As String,
                               context As HttpContext,
                               eventStore As EventStore) As Task(Of IResult)
                    Dim rules As List(Of LogParseRule) = Nothing
                    Try
                        rules = Await context.Request.ReadFromJsonAsync(Of List(Of LogParseRule))()
                    Catch ex As Exception
                        Return Results.BadRequest(New With {.error = "Invalid request body: " & ex.Message})
                    End Try
                    If rules Is Nothing Then
                        Return Results.BadRequest(New With {.error = "Request body missing or not a JSON array"})
                    End If
                    eventStore.UpdateParseRules(instanceId, rules)
                    Return Results.Ok(New With {.updated = True, .count = rules.Count})
                End Function)

            ' ---- Canonical event stream (EventStream Slice 1) ----
            ' Cursor read + SSE live-follow over the per-instance
            ' append-only stream. Both authenticated by the standard
            ' bearer middleware (anything under /api/ that isn't
            ' /api/version or /api/auth) and served on the same
            ' Kestrel listeners as everything else, so TLS-aware
            ' Manager clients consume them through the existing
            ' NodeHttpClient paths — no ad-hoc HttpClient anywhere.
            '
            ' since = last seq the caller has (0 = from the start of
            ' retained history); the response contains events with
            ' Seq > since, ascending.

            app.MapGet("/api/instances/{instanceId}/events",
                Function(instanceId As String,
                         context As HttpContext,
                         events As EventStreamStore) As IResult
                    ' head=1 (Slice 3): return the stream's current
                    ' head Seq with no events — lets a fresh sync
                    ' consumer seed its cursor at "now" instead of
                    ' replaying retained history its legacy ingestion
                    ' already covered.
                    Dim headParam = context.Request.Query("head").ToString()
                    If String.Equals(headParam, "1", StringComparison.Ordinal) OrElse
                       String.Equals(headParam, "true", StringComparison.OrdinalIgnoreCase) Then
                        Return Results.Ok(New EventBatchResponse With {
                            .Events = New List(Of EventRecord),
                            .LastSeq = events.GetHeadSeq(instanceId)
                        })
                    End If

                    Dim since As Long = 0
                    Long.TryParse(context.Request.Query("since").ToString(), since)
                    Dim max As Integer = 500
                    Integer.TryParse(context.Request.Query("max").ToString(), max)
                    If max <= 0 OrElse max > 5000 Then max = 500

                    Dim batch = events.GetEvents(instanceId, since, max)
                    Dim lastSeq As Long = since
                    If batch.Count > 0 Then lastSeq = batch(batch.Count - 1).Seq
                    Return Results.Ok(New EventBatchResponse With {
                        .Events = batch,
                        .LastSeq = lastSeq
                    })
                End Function)

            ' Manager→node enrichment append (Slice 3): the
            ' manager:identity channel. Kind-restricted to
            ' PlayerIdentity — this is an enrichment feed for the two
            ' things the node cannot know (realm-scoped fusion
            ' results, myrealm-resolved names), NOT a general write
            ' surface. SourceId is forced server-side; the event
            ' rides the normal ingestion path (append + fold), so
            ' replay dedup, purge watermarks, and the enrich-or-stash
            ' identity rules all apply unchanged. First-come-append
            ' alongside parse-emitted PlayerIdentity (both are
            ' merge-based enrichment folds — no authority claim).
            app.MapPost("/api/instances/{instanceId}/events",
                Function(instanceId As String,
                         request As AppendEventRequest,
                         store As EventStore) As IResult
                    If request Is Nothing Then Return Results.BadRequest("Missing body")
                    If request.Kind <> ParsedEventKind.PlayerIdentity Then
                        Return Results.BadRequest(
                            "Only PlayerIdentity events may be appended via this endpoint")
                    End If
                    If request.Fields Is Nothing OrElse request.Fields.Count = 0 Then
                        Return Results.BadRequest("Fields required")
                    End If

                    Dim utcAt = request.UtcAt
                    If utcAt = DateTime.MinValue Then utcAt = DateTime.UtcNow

                    Dim inserted = store.IngestSynthesizedEvent(
                        instanceId, request.Kind, request.Fields,
                        utcAt, "manager:identity")
                    Return Results.Ok(New AppendEventResponse With {
                        .Inserted = inserted
                    })
                End Function)

            ' SSE variant: replays the gap after ?since first, then
            ' goes live. Backfill and live are the SAME representation
            ' (one EventRecord JSON per data: frame), so a Manager
            ' that was offline for a week and one that never
            ' disconnected run identical consumption code.
            app.MapGet("/api/instances/{instanceId}/events/stream",
                Async Function(instanceId As String,
                               context As HttpContext,
                               events As EventStreamStore) As Task
                    context.Response.ContentType = "text/event-stream"
                    context.Response.Headers("Cache-Control") = "no-cache"
                    context.Response.Headers("Connection") = "keep-alive"

                    Dim since As Long = 0
                    Long.TryParse(context.Request.Query("since").ToString(), since)

                    Await events.StreamToResponseAsync(
                        instanceId,
                        since,
                        context.Response,
                        context.RequestAborted)
                End Function)

            ' Per-source stream diagnostics (Slice 2): appended /
            ' suppressed counters + the active authority map, so a
            ' double-sourced setup (or a misconfigured one) is
            ' observable instead of silently double-counting or
            ' silently dropping. Counters are per node session.
            app.MapGet("/api/instances/{instanceId}/events/sources",
                Function(instanceId As String,
                         events As EventStreamStore,
                         eventStore As EventStore) As IResult
                    Dim sources = events.GetSourceDiagnostics(instanceId)
                    ' Diagnostics Wave 2: informational ParsesKinds on
                    ' the line-capture sources — distinct from
                    ' OwnedKinds (authority-enforced, contested-only).
                    ' Fills the single-source display gap (LO tailer
                    ' with join/leave rules showed no kinds at all).
                    Try
                        Dim ruleKinds = eventStore.GetRuleKindNames(instanceId)
                        If ruleKinds IsNot Nothing AndAlso ruleKinds.Count > 0 Then
                            For Each s In sources
                                If s Is Nothing Then Continue For
                                Select Case s.SourceId
                                    Case "conpty", "stdout", "tailer"
                                        s.ParsesKinds = ruleKinds
                                End Select
                            Next
                        End If
                    Catch
                        ' Informational only — never fail the endpoint.
                    End Try
                    Return Results.Ok(sources)
                End Function)

            ' ---- Diagnostics Wave 2 (2026-08-15, all additive) ----

            ' Purge-watermark gate + per-kind counts (endpoint-ified
            ' --dump-watermarks; the CLI stays for node-box use).
            app.MapGet("/api/instances/{instanceId}/events/watermarks",
                Function(instanceId As String,
                         events As EventStreamStore) As IResult
                    Return Results.Ok(events.GetWatermarkDiagnostics(instanceId))
                End Function)

            ' Always-on parse-trace ring (last ~100 processed lines
            ' with match verdict + append outcome).
            app.MapGet("/api/instances/{instanceId}/events/parsetrace",
                Function(instanceId As String,
                         eventStore As EventStore) As IResult
                    Return Results.Ok(eventStore.GetParseTrace(instanceId))
                End Function)

            ' Re-arm the 20-line log-side trace (ring is always on).
            app.MapPost("/api/instances/{instanceId}/events/parsetrace/rearm",
                Function(instanceId As String,
                         eventStore As EventStore) As IResult
                    Return Results.Ok(eventStore.RearmParseTrace(instanceId))
                End Function)

            ' Rule dry-run: one console line through the REGISTERED
            ' rules — regex verdicts + captures, no append, no fold.
            app.MapPost("/api/instances/{instanceId}/events/test-line",
                Function(instanceId As String,
                         request As RuleDryRunRequest,
                         eventStore As EventStore) As IResult
                    If request Is Nothing OrElse String.IsNullOrEmpty(request.Line) Then
                        Return Results.BadRequest(New With {.error = "Line is required"})
                    End If
                    Return Results.Ok(eventStore.TestLine(instanceId, request.Line))
                End Function)

            ' Node-boot resurrection state: snapshot/parked row, boot
            ' flags, flap-guard chain.
            app.MapGet("/api/instances/{instanceId}/boot-state",
                Function(instanceId As String,
                         pm As ProcessManager) As IResult
                    Return Results.Ok(pm.GetBootStateDiagnostics(instanceId))
                End Function)

            ' Shim link state: mode/strategy/pids/protocol/ring cursors.
            app.MapGet("/api/instances/{instanceId}/shim-state",
                Function(instanceId As String,
                         pm As ProcessManager) As IResult
                    Return Results.Ok(pm.GetShimStateDiagnostics(instanceId))
                End Function)

            ' Hard purge (operator action): delete a kind's events up
            ' to a cutoff AND set the append-time purge watermark so
            ' tailer backfill / adoption replay of the same log lines
            ' cannot re-append them. This is what makes a purge stick
            ' — the pre-stream chat purge came back on the next
            ' replay because nothing remembered the purge. For
            ' kind=ChatMessage the legacy chat_messages projection is
            ' purged too, so /chat and the stream stay consistent.
            '
            '   POST /api/instances/{id}/events/purge?kind=ChatMessage[&throughUtc=...]
            '
            ' throughUtc defaults to now. This endpoint is also the
            ' sanctioned removal path for PlayerJoin/PlayerLeave
            ' (which the automatic pruner NEVER touches — last-seen
            ' is built from them); purging those is an explicit
            ' last-seen reset and the caller is expected to mean it.
            app.MapPost("/api/instances/{instanceId}/events/purge",
                Function(instanceId As String,
                         context As HttpContext,
                         events As EventStreamStore,
                         eventStore As EventStore) As IResult
                    Dim kindStr = context.Request.Query("kind").ToString()
                    Dim kind As ParsedEventKind
                    If String.IsNullOrWhiteSpace(kindStr) OrElse
                       Not [Enum].TryParse(Of ParsedEventKind)(kindStr, ignoreCase:=True, result:=kind) Then
                        Return Results.BadRequest(New With {
                            .error = "kind is required (a ParsedEventKind name, e.g. ChatMessage)"})
                    End If

                    Dim throughUtc As DateTime = DateTime.UtcNow
                    Dim throughStr = context.Request.Query("throughUtc").ToString()
                    If Not String.IsNullOrEmpty(throughStr) Then
                        Dim parsed As DateTime
                        If Not DateTime.TryParse(throughStr, Nothing,
                                                  Globalization.DateTimeStyles.RoundtripKind,
                                                  parsed) Then
                            Return Results.BadRequest(New With {
                                .error = "throughUtc must be an ISO-8601 timestamp"})
                        End If
                        ' Same Unspecified-means-UTC contract as /chat's
                        ' since parameter.
                        Select Case parsed.Kind
                            Case DateTimeKind.Utc : throughUtc = parsed
                            Case DateTimeKind.Local : throughUtc = parsed.ToUniversalTime()
                            Case Else : throughUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                        End Select
                    End If

                    ' Guard (2026-08-12): watermarks only move FORWARD, so a
                    ' future cutoff would permanently blind the kind on this
                    ' instance — every later append drops as Purged with no
                    ' way back short of deleting the row. Clamp to now; the
                    ' echoed throughUtc in the response shows the clamp.
                    If throughUtc > DateTime.UtcNow Then throughUtc = DateTime.UtcNow

                    Dim purgedEvents = events.PurgeKind(
                        EventScopeType.Instance, instanceId, kind, throughUtc)
                    Dim purgedChatRows = 0
                    If kind = ParsedEventKind.ChatMessage Then
                        purgedChatRows = eventStore.PurgeChatMessages(instanceId, throughUtc)
                    End If

                    Return Results.Ok(New With {
                        .kind = kind.ToString(),
                        .throughUtc = throughUtc,
                        .purgedEvents = purgedEvents,
                        .purgedChatRows = purgedChatRows
                    })
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