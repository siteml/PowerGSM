Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  StatePollerHost — the node's generic REST/state poller
'  (EventStream Unification, Slice 2).
'
'  One poller loop per (instance, ResolvedStatePollSource). The
'  loop GETs the game's own admin/status endpoint, extracts the
'  player array (PayloadShape.PlayerArray), maps payload keys
'  through the source's FieldMap into the SAME capture-field
'  names the parse rules produce, diffs successive snapshots by
'  KeyField, and synthesizes PlayerJoin / PlayerLeave events into
'  the canonical stream via EventStore.IngestSynthesizedEvent.
'  Downstream cannot tell a polled event from a parsed one.
'
'  The node stays game-agnostic: everything game-specific (URL,
'  credentials, field semantics) arrives resolved from the game
'  plugin via StartInstanceRequest.PollSources.
'
'  Poller hygiene (per the plan — learned from the July Palworld
'  incident's diagnostics work):
'  - Poll failures are NOT leave events. Leaves are synthesized
'    only from a SUCCESSFUL poll that lacks the player. An API
'    that stops answering emits ONE Custom source-degraded event
'    (after a small failure streak), and ONE recovered event
'    when it answers again — never a burst of fake leaves.
'  - Restart debounce: each poller run establishes its own
'    baseline on its FIRST successful poll before any diffing.
'    Pollers are per-game-run (started in FinalizeStart, stopped
'    in HandleProcessExited), so a crash-restart naturally
'    re-baselines — and Palworld's world-load window (API answers
'    with an empty player list while loading) simply produces an
'    empty baseline followed by joins as players re-appear.
'  - Baseline joins are ROSTER-AWARE: on the first successful
'    poll, joins are emitted only for players not already in the
'    in-memory roster (which stream hydration rebuilt on
'    adoption), so a node restart doesn't fabricate join events
'    for players who never left.
'  - The poller touches the game's REST API only — never the
'    filesystem. (No new watchers. Ever.)
' ============================================================

Namespace GSM.Node

    Public Class StatePollerHost
        Implements IDisposable

        ''' <summary>Consecutive failed polls before the single
        ''' source-degraded event is emitted.</summary>
        Private Const DegradedThreshold As Integer = 3

        ' One client for every poller: loopback calls, small
        ' payloads. Timeout well under the minimum poll interval so
        ' a hung API can't stack requests.
        Private Shared ReadOnly _http As New HttpClient() With {
            .Timeout = TimeSpan.FromSeconds(4)
        }

        ' SourceIds reserved by the node's own ingestion paths — a
        ' plugin-declared poller may not impersonate them (the
        ' authority map trusts SourceId strings).
        Private Shared ReadOnly _reservedSourceIds As String() = {"stdout", "tailer", "lifecycle", "conpty"}

        Private ReadOnly _eventStore As EventStore
        Private ReadOnly _logger As ILogger(Of StatePollerHost)
        Private ReadOnly _active As New ConcurrentDictionary(Of String, List(Of PollerRun))

        Public Sub New(eventStore As EventStore, logger As ILogger(Of StatePollerHost))
            _eventStore = eventStore
            _logger = logger
        End Sub

        ''' <summary>
        ''' Starts pollers for an instance's declared sources,
        ''' replacing any previous set (idempotent per game run).
        ''' Invalid declarations are skipped with a warning rather
        ''' than failing the start — polling is quality-of-life,
        ''' never gating.
        ''' </summary>
        Public Sub StartPollers(instanceId As String,
                                sources As IList(Of ResolvedStatePollSource))
            StopPollers(instanceId)
            If String.IsNullOrEmpty(instanceId) Then Return
            If sources Is Nothing OrElse sources.Count = 0 Then Return

            Dim runs As New List(Of PollerRun)
            For Each src In sources
                If src Is Nothing OrElse
                   String.IsNullOrWhiteSpace(src.SourceId) OrElse
                   String.IsNullOrWhiteSpace(src.Url) OrElse
                   String.IsNullOrWhiteSpace(src.KeyField) Then
                    _logger.LogWarning(
                        "Skipping malformed poll source for {Id} (SourceId/Url/KeyField required)",
                        instanceId)
                    Continue For
                End If
                Dim reserved = False
                For Each r In _reservedSourceIds
                    If String.Equals(r, src.SourceId, StringComparison.OrdinalIgnoreCase) Then
                        reserved = True
                        Exit For
                    End If
                Next
                If reserved Then
                    _logger.LogWarning(
                        "Skipping poll source '{Source}' for {Id}: SourceId is reserved",
                        src.SourceId, instanceId)
                    Continue For
                End If

                Dim run As New PollerRun(instanceId, src, _eventStore, _logger)
                runs.Add(run)
                run.Start()
            Next

            If runs.Count > 0 Then
                _active(instanceId) = runs
                _logger.LogInformation(
                    "Started {Count} state poller(s) for {Id}", runs.Count, instanceId)
            End If
        End Sub

        ''' <summary>Stops and forgets all pollers for an instance.
        ''' Safe to call when none are running.</summary>
        Public Sub StopPollers(instanceId As String)
            If String.IsNullOrEmpty(instanceId) Then Return
            Dim runs As List(Of PollerRun) = Nothing
            If Not _active.TryRemove(instanceId, runs) Then Return
            For Each pr As PollerRun In runs
                pr.Cancel()
            Next
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            For Each kvp In _active
                For Each pr As PollerRun In kvp.Value
                    pr.Cancel()
                Next
            Next
            _active.Clear()
        End Sub

        ' ========================================================
        '  One poll loop
        ' ========================================================

        Private Class PollerRun

            Private ReadOnly _instanceId As String
            Private ReadOnly _src As ResolvedStatePollSource
            Private ReadOnly _eventStore As EventStore
            Private ReadOnly _logger As ILogger
            Private ReadOnly _cts As New CancellationTokenSource()

            Public Sub New(instanceId As String,
                           src As ResolvedStatePollSource,
                           eventStore As EventStore,
                           logger As ILogger)
                _instanceId = instanceId
                _src = src
                _eventStore = eventStore
                _logger = logger
            End Sub

            Public Sub Start()
                ' Fire-and-forget loop; owns its lifetime via _cts.
                Task.Run(Function() LoopAsync(_cts.Token))
            End Sub

            ''' <summary>Cancel-and-forget: the loop observes the
            ''' token at its next await and exits quietly.</summary>
            Public Sub Cancel()
                Try
                    _cts.Cancel()
                    _cts.Dispose()
                Catch
                End Try
            End Sub

            Private Async Function LoopAsync(ct As CancellationToken) As Task
                Dim intervalSeconds = If(_src.IntervalSeconds < 1, 5, _src.IntervalSeconds)
                Dim baselineDone As Boolean = False
                Dim lastGood As Dictionary(Of String, Dictionary(Of String, String)) = Nothing
                Dim failStreak As Integer = 0
                Dim degraded As Boolean = False

                Try
                    While Not ct.IsCancellationRequested
                        Dim snapshot As Dictionary(Of String, Dictionary(Of String, String)) = Nothing
                        Try
                            snapshot = Await FetchSnapshotAsync(ct)
                        Catch ex As OperationCanceledException When ct.IsCancellationRequested
                            Exit While
                        Catch ex As Exception
                            snapshot = Nothing
                        End Try

                        If snapshot Is Nothing Then
                            ' HYGIENE: a failed poll synthesizes NOTHING.
                            ' One degraded event after a short streak so
                            ' the outage is observable without 32 fake
                            ' leaves.
                            failStreak += 1
                            If Not degraded AndAlso failStreak >= DegradedThreshold Then
                                degraded = True
                                _logger.LogWarning(
                                    "Poll source '{Source}' for {Id} is not answering — polling continues, no leaves synthesized",
                                    _src.SourceId, _instanceId)
                                EmitStatus("degraded")
                            End If
                        Else
                            If degraded Then
                                _logger.LogInformation(
                                    "Poll source '{Source}' for {Id} recovered",
                                    _src.SourceId, _instanceId)
                                EmitStatus("ok")
                                degraded = False
                            End If
                            failStreak = 0

                            If Not baselineDone Then
                                ' First successful poll of this game run:
                                ' establish the baseline. Emit joins only
                                ' for players the roster doesn't already
                                ' know (roster-aware — see file header).
                                baselineDone = True
                                Dim roster = _eventStore.GetPlayers(_instanceId)
                                For Each kvp In snapshot
                                    If Not RosterContains(roster, kvp.Value) Then
                                        Emit(ParsedEventKind.PlayerJoin, kvp.Value)
                                    End If
                                Next
                            Else
                                ' Present now, absent before -> join.
                                For Each kvp In snapshot
                                    If Not lastGood.ContainsKey(kvp.Key) Then
                                        Emit(ParsedEventKind.PlayerJoin, kvp.Value)
                                    End If
                                Next
                                ' Absent now, present before -> leave —
                                ' ONLY because this poll SUCCEEDED. The
                                ' leave carries the player's last-known
                                ' fields so correlation matches the join.
                                For Each kvp In lastGood
                                    If Not snapshot.ContainsKey(kvp.Key) Then
                                        Emit(ParsedEventKind.PlayerLeave, kvp.Value)
                                    End If
                                Next
                            End If
                            lastGood = snapshot
                        End If

                        Await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct)
                    End While
                Catch ex As OperationCanceledException
                    ' Normal stop.
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "State poller '{Source}' for {Id} terminated unexpectedly",
                        _src.SourceId, _instanceId)
                End Try
            End Function

            ' ----------------------------------------------------
            '  Fetch + payload mapping
            ' ----------------------------------------------------

            ''' <summary>
            ''' One poll: GET + parse + FieldMap. Returns key ->
            ''' capture-fields per player, or Nothing on ANY failure
            ''' (transport, non-2xx, unparseable body) — the caller
            ''' treats Nothing as "no information", never as "empty
            ''' server".
            ''' </summary>
            Private Async Function FetchSnapshotAsync(ct As CancellationToken) _
                    As Task(Of Dictionary(Of String, Dictionary(Of String, String)))
                Dim req As New HttpRequestMessage(HttpMethod.Get, _src.Url)
                req.Headers.Accept.Add(New MediaTypeWithQualityHeaderValue("application/json"))
                If _src.AuthKind = PollAuthKind.Basic Then
                    Dim cred = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        If(_src.AuthUsername, "") & ":" & If(_src.AuthPassword, "")))
                    req.Headers.Authorization = New AuthenticationHeaderValue("Basic", cred)
                End If

                Using resp = Await _http.SendAsync(req, ct)
                    If Not resp.IsSuccessStatusCode Then Return Nothing
                    Dim body = Await resp.Content.ReadAsStringAsync(ct)

                    Dim root = TryCast(JsonNode.Parse(body), JsonObject)
                    If root Is Nothing Then Return Nothing
                    Dim arrayPath = If(String.IsNullOrEmpty(_src.PlayersArrayPath),
                                       "players", _src.PlayersArrayPath)
                    Dim arr = TryCast(root(arrayPath), JsonArray)
                    If arr Is Nothing Then Return Nothing

                    Dim result As New Dictionary(Of String, Dictionary(Of String, String))(StringComparer.Ordinal)
                    ' NOTE: loop variable must not be called "node" —
                    ' inside Namespace GSM.Node that identifier resolves
                    ' to the namespace itself (BC30112).
                    For Each element In arr
                        Dim obj = TryCast(element, JsonObject)
                        If obj Is Nothing Then Continue For

                        ' Case-insensitive property copy — the live
                        ' Palworld API's key casing is erratic ("iP").
                        Dim props As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                        For Each kvp In obj
                            Dim v = If(kvp.Value?.ToString(), "").Trim(""""c)
                            If v.Length > 0 Then props(kvp.Key) = v
                        Next

                        Dim fields As New Dictionary(Of String, String)(StringComparer.Ordinal)
                        If _src.FieldMap IsNot Nothing Then
                            For Each map In _src.FieldMap
                                Dim payloadVal As String = Nothing
                                If props.TryGetValue(map.Key, payloadVal) AndAlso
                                   Not String.IsNullOrEmpty(payloadVal) Then
                                    fields(map.Value) = payloadVal
                                End If
                            Next
                        End If

                        ' Optional platform-prefix split: "steam_7656..."
                        ' -> Platform="steam", field="7656..." (same
                        ' semantics as the remote-control player path).
                        If Not String.IsNullOrEmpty(_src.PlatformPrefixField) Then
                            Dim raw As String = Nothing
                            If fields.TryGetValue(_src.PlatformPrefixField, raw) Then
                                Dim sep = raw.IndexOf("_"c)
                                If sep > 0 AndAlso sep < raw.Length - 1 Then
                                    If Not fields.ContainsKey("Platform") Then
                                        fields("Platform") = raw.Substring(0, sep)
                                    End If
                                    fields(_src.PlatformPrefixField) = raw.Substring(sep + 1)
                                End If
                            End If
                        End If

                        Dim key As String = Nothing
                        If Not fields.TryGetValue(_src.KeyField, key) OrElse
                           String.IsNullOrEmpty(key) Then
                            Continue For ' unkeyable entry — can't diff it
                        End If
                        result(key) = fields
                    Next
                    Return result
                End Using
            End Function

            ' ----------------------------------------------------
            '  Emission
            ' ----------------------------------------------------

            Private Sub Emit(kind As ParsedEventKind, fields As Dictionary(Of String, String))
                Try
                    _eventStore.IngestSynthesizedEvent(
                        _instanceId, kind,
                        New Dictionary(Of String, String)(fields, StringComparer.Ordinal),
                        DateTime.UtcNow, _src.SourceId)
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "Failed to ingest polled {Kind} for {Id}", kind, _instanceId)
                End Try
            End Sub

            ''' <summary>Source health as a Custom event: folds into
            ''' ServerState.CustomFields as
            ''' "PollSource_&lt;sourceId&gt;" = degraded|ok, so the
            ''' outage is visible on /server-state as well as in the
            ''' stream.</summary>
            Private Sub EmitStatus(status As String)
                Dim fields As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
                    {"Custom_PollSource_" & _src.SourceId, status}
                }
                Try
                    _eventStore.IngestSynthesizedEvent(
                        _instanceId, ParsedEventKind.Custom, fields,
                        DateTime.UtcNow, _src.SourceId)
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "Failed to ingest poll-source status for {Id}", _instanceId)
                End Try
            End Sub

            Private Function RosterContains(roster As IReadOnlyList(Of PlayerSession),
                                            fields As Dictionary(Of String, String)) As Boolean
                If roster Is Nothing OrElse roster.Count = 0 Then Return False
                Dim key As String = Nothing
                If Not fields.TryGetValue(_src.KeyField, key) OrElse
                   String.IsNullOrEmpty(key) Then Return False
                For Each sess In roster
                    Dim sessVal = GetSessionField(sess, _src.KeyField)
                    If Not String.IsNullOrEmpty(sessVal) AndAlso
                       String.Equals(sessVal, key, StringComparison.OrdinalIgnoreCase) Then
                        Return True
                    End If
                Next
                Return False
            End Function

            Private Shared Function GetSessionField(sess As PlayerSession, name As String) As String
                Select Case name
                    Case "PlatformUserId" : Return sess.PlatformUserId
                    Case "CharacterId" : Return sess.CharacterId
                    Case "DisplayName" : Return sess.DisplayName
                    Case "PlatformPersona" : Return sess.PlatformPersona
                    Case "RemoteAddress" : Return sess.RemoteAddress
                    Case Else : Return Nothing
                End Select
            End Function

        End Class

    End Class

End Namespace
