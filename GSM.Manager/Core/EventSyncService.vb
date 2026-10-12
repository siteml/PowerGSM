Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Node.Api
Imports GSM.Plugin
Imports GSM.Manager.Data

' ============================================================
'  EventSyncService — Manager-side cursor sync over the node's
'  per-instance event stream (EventStream Slice 3).
'
'  One pull loop per stream-capable instance:
'
'    1. Cursor from AppSettings ("eventsync:cursor:{id}").
'       First connect seeds at the node's CURRENT head seq
'       (?head=1) — everything older was already ingested by
'       the legacy path, and join/leave is never pruned
'       node-side, so replaying from 0 could mean months of
'       duplicate History rows.
'    2. Catch-up: page /events?since={cursor} until a partial
'       batch. Applied with liveMode:=False — projection rows
'       are written (at their TRUE event times), notifications
'       are suppressed (the backfill guardrail: an hour of
'       missed joins must not spam Discord an hour late).
'    3. Bucket seed-merge from /players — pre-cursor sessions
'       (players who joined before this Manager run) get
'       represented in the in-memory roster so the stop-flush
'       and nameless-leave machinery keep working.
'    4. Live: SSE push (GET /events/stream?since={cursor},
'       consumed via INodeClient.StreamEventsAsync — the same
'       replay-then-live channel the node has exposed since
'       Slice 1). Events apply one at a time with
'       liveMode:=True — fresh events notify; anything an SSE
'       replay delivers late is still guarded by the 5-minute
'       notification age check in ApplyStreamPlayerEventsAsync.
'       When the stream drops (node restart, network blip,
'       silent TCP death caught by the 45s idle timeout), the
'       loop falls back to the original 3s cursor pull for a
'       bounded window, then retries SSE — pull is the
'       permanent fallback, SSE the preferred transport. The
'       cursor's exactly-once transaction makes the handoffs
'       harmless in both directions: reconnects replay from the
'       committed cursor (nothing lost), and everything already
'       applied sits at-or-below the cursor (nothing duplicated).
'
'  Exactly-once (gate Q6) is the cursor update riding the SAME
'  SaveChanges as the projected rows — both or neither. The
'  apply itself lives on InstanceManager
'  (ApplyStreamPlayerEventsAsync), which owns every store the
'  events project into; this class owns only loop lifecycle,
'  cursor paging, and the live/backfill distinction.
'
'  While a loop is active, InstanceManager suppresses its
'  legacy join/leave ingestion for that instance (parser
'  dispatch + resync synthesis) — the stream is authoritative.
'  Legacy nodes (EventStreamSupported=False) never get a loop
'  and keep the legacy path, per the mixed-fleet rule.
'
'  Also home to the manager:identity push (Slice 3's second
'  half): utility-plugin identity contributions (LoMyrealm's
'  myrealm-resolved names) are mapped (GameId, SessionScope) →
'  running instances and appended to stream-capable nodes via
'  POST /events, making Manager-side name knowledge a stream
'  fact the node's folds (and any consumer) can see.
' ============================================================

Namespace GSM.Manager.Core

    Public Class EventSyncService
        Implements IDisposable

        Private ReadOnly _logger As ILogger(Of EventSyncService)
        Private ReadOnly _active As New ConcurrentDictionary(Of String, SyncRun)(StringComparer.Ordinal)

        ''' <summary>Poll cadence for catch-up paging gaps and the
        ''' pull-fallback window while SSE is down.</summary>
        Private Const LivePollIntervalMs As Integer = 3000

        ''' <summary>Backoff after a failed poll iteration.</summary>
        Private Const ErrorBackoffMs As Integer = 10000

        ''' <summary>Page size for catch-up and live pulls.</summary>
        Private Const BatchMax As Integer = 500

        ''' <summary>Base pull-fallback window after an SSE drop,
        ''' doubled per consecutive quick failure (15s, 30s, 60s,
        ''' 2m, 4m) and capped by SseRetryMaxMs — so a node whose
        ''' SSE endpoint is persistently broken costs one failed
        ''' connect per few minutes, not one per poll.</summary>
        Private Const SseRetryBaseMs As Integer = 15000

        ''' <summary>Cap on the pull-fallback window.</summary>
        Private Const SseRetryMaxMs As Integer = 300000

        ''' <summary>An SSE session that lived at least this long
        ''' (or applied any event) counts as healthy and resets the
        ''' retry ladder.</summary>
        Private Shared ReadOnly HealthySseSession As TimeSpan = TimeSpan.FromSeconds(60)

        Public Sub New(logger As ILogger(Of EventSyncService))
            _logger = logger
        End Sub

        Public Shared Function CursorKey(instanceId As String) As String
            Return $"eventsync:cursor:{instanceId}"
        End Function

        Public Enum SyncState
            ''' <summary>No loop registered — legacy ingestion owns
            ''' join/leave for this instance.</summary>
            Inactive
            ''' <summary>Loop registered, capability not yet
            ''' confirmed. Transient (one cached /api/version read);
            ''' resolves to Active or Inactive.</summary>
            Pending
            ''' <summary>Stream-capable node, sync loop running —
            ''' the stream owns join/leave.</summary>
            Active
        End Enum

        Public Function GetState(instanceId As String) As SyncState
            If String.IsNullOrEmpty(instanceId) Then Return SyncState.Inactive
            Dim run As SyncRun = Nothing
            If Not _active.TryGetValue(instanceId, run) Then Return SyncState.Inactive
            Return If(run.Confirmed, SyncState.Active, SyncState.Pending)
        End Function

        ''' <summary>
        ''' True when the stream owns join/leave ingestion for this
        ''' instance. The legacy parser dispatch treats Pending as
        ''' active too (suppress) — for a stream-capable node a
        ''' Pending-window legacy write would DUPLICATE the sync
        ''' replay's write and nothing would repair it, while for a
        ''' legacy node a Pending-window suppression is repaired by
        ''' the resync synthesis machinery. Suppression is the safe
        ''' direction; the window is one cached version read wide.
        ''' </summary>
        Public Function SuppressesLegacyIngestion(instanceId As String) As Boolean
            Return GetState(instanceId) <> SyncState.Inactive
        End Function

        ''' <summary>
        ''' Wait (bounded) for a Pending registration to resolve.
        ''' Used by the log-stream resync gate, which must not run
        ''' concurrently with a sync catch-up replay (both would
        ''' synthesize the same missed joins).
        ''' </summary>
        Public Async Function WaitForDecisionAsync(instanceId As String,
                                                   timeout As TimeSpan) As Task(Of SyncState)
            Dim deadline = DateTime.UtcNow + timeout
            Dim state = GetState(instanceId)
            While state = SyncState.Pending AndAlso DateTime.UtcNow < deadline
                Await Task.Delay(100)
                state = GetState(instanceId)
            End While
            Return state
        End Function

        ''' <summary>
        ''' Idempotent: starts a sync loop for the instance if none
        ''' is registered. Capability (EventStreamSupported) is
        ''' checked inside the loop — a legacy node's loop exits
        ''' immediately and deregisters, so the periodic Ensure from
        ''' the background poll costs one cached /api/version read.
        ''' </summary>
        Public Sub EnsureSync(instanceId As String)
            If String.IsNullOrEmpty(instanceId) Then Return
            If _active.ContainsKey(instanceId) Then Return
            Dim run As New SyncRun With {.Cts = New CancellationTokenSource()}
            If Not _active.TryAdd(instanceId, run) Then
                run.Cts.Dispose()
                Return
            End If
            run.LoopTask = Task.Run(Function() RunLoopAsync(instanceId, run, run.Cts.Token))
        End Sub

        Public Sub StopSync(instanceId As String)
            If String.IsNullOrEmpty(instanceId) Then Return
            Dim run As SyncRun = Nothing
            If Not _active.TryRemove(instanceId, run) Then Return
            Try : run.Cts.Cancel() : Catch : End Try
        End Sub

        ''' <summary>
        ''' Stop the loop, wait it out, then run ONE cursor drain —
        ''' the stop-tail fix (2026-08-15). Manager-initiated stops
        ''' call StopLogStream→StopSync at the exact moment the node
        ''' has just appended InstanceStopRequested/ProcessExited;
        ''' the SSE delivery loses the race to the cancel, and with
        ''' the loop gone the stop rows only reached History via
        ''' cursor replay at the instance's NEXT start (live-observed:
        ''' "when I *start* it again, both the stop and start show
        ''' up"). The drain pages /events?since={committed cursor}
        ''' through the SAME transactional apply (liveMode:=False —
        ''' a stop must not fire join/leave notifications late), so
        ''' exactly-once holds. Awaiting the loop task (bounded)
        ''' before draining keeps a mid-apply SSE callback from
        ''' racing the drain into duplicate rows.
        '''
        ''' Best-effort: node unreachable / pre-stream node / no
        ''' cursor yet → logs at Debug and returns; next start's
        ''' replay remains the backstop.
        ''' </summary>
        Public Async Function StopSyncAndDrainAsync(instanceId As String) As Task
            If String.IsNullOrEmpty(instanceId) Then Return
            Dim run As SyncRun = Nothing
            If _active.TryRemove(instanceId, run) Then
                Try : run.Cts.Cancel() : Catch : End Try
                If run.LoopTask IsNot Nothing Then
                    Try
                        Await Task.WhenAny(run.LoopTask, Task.Delay(5000))
                    Catch
                        ' Loop faulted — its exception is already
                        ' logged by RunLoopAsync; drain regardless.
                    End Try
                End If
            End If

            Try
                Dim im = ManagerProgram.Services.GetService(Of InstanceManager)()
                If im Is Nothing Then Return
                Dim client = im.GetNodeClientForInstance(instanceId)
                If client Is Nothing Then Return

                Dim version = Await client.GetApiVersionAsync(False, CancellationToken.None)
                If version Is Nothing OrElse Not version.EventStreamSupported Then Return

                Dim cursor As Long
                If Not TryLoadCursor(instanceId, cursor) Then Return

                ' The node's ProcessExited is appended by its exit
                ' handler, which can land AFTER the stop API has
                ' already responded — a single immediate drain can
                ' beat it and find nothing (live-observed: stop row
                ' missing again despite the drain). So: drain in
                ' rounds, and keep going until a round delivers a
                ' process-exit event or the retry budget runs out
                ' (4 rounds, 2s apart, ~8s worst case — the exit
                ' handler normally lands well inside that).
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(15))
                    Dim drained = 0
                    Dim sawExit = False
                    For round = 1 To 4
                        Dim caughtUp = False
                        While Not caughtUp
                            Dim batch = Await client.GetEventsAsync(instanceId, cursor, BatchMax, cts.Token)
                            If batch.Events.Count > 0 Then
                                For Each ev In batch.Events
                                    If ev IsNot Nothing AndAlso
                                       (ev.Kind = ParsedEventKind.ProcessExited OrElse
                                        ev.Kind = ParsedEventKind.ProcessExitedUnexpectedly) Then
                                        sawExit = True
                                    End If
                                Next
                                cursor = Await im.ApplyStreamPlayerEventsAsync(
                                    instanceId, batch.Events, liveMode:=False,
                                    cursorKey:=CursorKey(instanceId), cursorSeq:=batch.LastSeq)
                                drained += batch.Events.Count
                            End If
                            caughtUp = batch.Events.Count < BatchMax
                        End While
                        If sawExit OrElse round = 4 Then Exit For
                        Await Task.Delay(2000, cts.Token)
                    Next
                    If drained > 0 Then
                        _logger.LogInformation(
                            "Event sync stop-drain for {Id}: applied {Count} tail event(s), exit-event seen={SawExit} (cursor {Seq})",
                            instanceId, drained, sawExit, cursor)
                    End If

                    ' No exit event inside the fast window — the
                    ' abnormal paths (Manager-side HTTP timeout while
                    ' the game is still in its graceful window, node
                    ' force-kill later) can put the exit tens of
                    ' seconds out. One deferred re-drain at +60s
                    ' covers every plugin's graceful ceiling (max
                    ' 90s from stop REQUEST, and this code runs
                    ' after the stop call already spent that);
                    ' fire-and-forget so the stop flow isn't held.
                    ' Next start's replay remains the final
                    ' backstop if even this misses.
                    If Not sawExit Then
                        _logger.LogDebug(
                            "Event sync stop-drain for {Id}: no exit event in the fast window — deferred re-drain in 60s",
                            instanceId)
                        Dim t = Task.Run(Async Function()
                                             Try
                                                 Await Task.Delay(TimeSpan.FromSeconds(60))
                                                 ' Abort if a new sync loop took over
                                                 ' meanwhile (instance restarted) — its
                                                 ' catch-up owns the tail then.
                                                 If _active.ContainsKey(instanceId) Then Return
                                                 ' Re-load the COMMITTED cursor rather
                                                 ' than the captured local — anything
                                                 ' that applied in the meantime moved it.
                                                 Dim lateCursor As Long
                                                 If Not TryLoadCursor(instanceId, lateCursor) Then Return
                                                 Dim lateDrained = 0
                                                 Using lateCts As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                                                     Dim caughtUp = False
                                                     While Not caughtUp
                                                         Dim batch = Await client.GetEventsAsync(instanceId, lateCursor, BatchMax, lateCts.Token)
                                                         If batch.Events.Count > 0 Then
                                                             lateCursor = Await im.ApplyStreamPlayerEventsAsync(
                                                                 instanceId, batch.Events, liveMode:=False,
                                                                 cursorKey:=CursorKey(instanceId), cursorSeq:=batch.LastSeq)
                                                             lateDrained += batch.Events.Count
                                                         End If
                                                         caughtUp = batch.Events.Count < BatchMax
                                                     End While
                                                 End Using
                                                 If lateDrained > 0 Then
                                                     _logger.LogInformation(
                                                         "Event sync deferred stop-drain for {Id}: applied {Count} late tail event(s)",
                                                         instanceId, lateDrained)
                                                 End If
                                             Catch ex As Exception
                                                 _logger.LogDebug(ex,
                                                     "Deferred stop-drain failed for {Id}", instanceId)
                                             End Try
                                         End Function)
                    End If
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex,
                    "Event sync stop-drain failed for {Id} — next start's cursor replay will backfill",
                    instanceId)
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            For Each kvp In _active
                Try : kvp.Value.Cts.Cancel() : Catch : End Try
            Next
            _active.Clear()
        End Sub

        Private Class SyncRun
            Public Cts As CancellationTokenSource
            Public LoopTask As Task
            ''' <summary>Set once the node's EventStreamSupported is
            ''' confirmed — flips GetState Pending → Active. Plain
            ''' Boolean (VB has no volatile fields); a marginally
            ''' stale read only extends the Pending window by one
            ''' check, which is already the safe direction.</summary>
            Public Confirmed As Boolean
        End Class

        ' --------------------------------------------------------
        '  Sync loop
        ' --------------------------------------------------------

        Private Async Function RunLoopAsync(instanceId As String,
                                            run As SyncRun,
                                            token As CancellationToken) As Task
            Try
                Dim im = ManagerProgram.Services.GetService(Of InstanceManager)()
                If im Is Nothing Then Return

                Dim client = im.GetNodeClientForInstance(instanceId)
                If client Is Nothing Then Return

                ' Capability gate (mixed-fleet rule). The version
                ' response is cached in the client, so the re-probe
                ' from each background-poll Ensure is in-memory.
                Try
                    Dim version = Await client.GetApiVersionAsync(False, token)
                    If version Is Nothing OrElse Not version.EventStreamSupported Then Return
                Catch
                    Return
                End Try
                run.Confirmed = True

                ' ---- Cursor ----
                Dim cursor As Long
                Dim seeded As Boolean = False
                If Not TryLoadCursor(instanceId, cursor) Then
                    ' First connect: seed at the node's current head.
                    ' Join/leave/chat older than the seed stay
                    ' unreplayed (the legacy path already ingested
                    ' them — replaying from 0 could mean months of
                    ' duplicate History rows; see file header).
                    cursor = Await client.GetEventsHeadAsync(instanceId, token)

                    ' First-seed LIFECYCLE backfill (2026-08-15,
                    ' operator: "can't the stream head filter in
                    ' ONLY starts?"): lifecycle events were NEVER
                    ' ingested by the legacy path, so unlike
                    ' join/leave they cannot duplicate — and the
                    ' head seed was eating the ProcessStarted that
                    ' landed moments before the very first sync
                    ' ("the very first start didn't show"). Pull
                    ' the last page behind head, keep ONLY
                    ' lifecycle-kind events from the last 10
                    ' minutes (the fresh-start window — NOT months
                    ' of historic starts/stops), and apply them as
                    ' backfill with the cursor committed at head in
                    ' the same transaction.
                    Dim seedFallback = True
                    Try
                        Dim seedSince = Math.Max(0L, cursor - BatchMax)
                        Dim seedBatch = Await client.GetEventsAsync(instanceId, seedSince, BatchMax, token)
                        Dim cutoff = DateTime.UtcNow.AddMinutes(-10)
                        Dim lifecycleTail As New List(Of EventRecord)
                        For Each ev In seedBatch.Events
                            If ev IsNot Nothing AndAlso
                               InstanceManager.LifecycleActivityKind(ev.Kind) IsNot Nothing AndAlso
                               ev.UtcAt >= cutoff Then
                                lifecycleTail.Add(ev)
                            End If
                        Next
                        If lifecycleTail.Count > 0 Then
                            cursor = Await im.ApplyStreamPlayerEventsAsync(
                                instanceId, lifecycleTail, liveMode:=False,
                                cursorKey:=CursorKey(instanceId), cursorSeq:=cursor)
                            seedFallback = False
                            _logger.LogInformation(
                                "Event sync for {Id}: cursor seeded at head seq {Seq} with {Count} recent lifecycle event(s) backfilled",
                                instanceId, cursor, lifecycleTail.Count)
                        End If
                    Catch ex As Exception
                        _logger.LogDebug(ex,
                            "First-seed lifecycle backfill failed for {Id} — seeding plain head", instanceId)
                    End Try
                    If seedFallback Then
                        SaveSeededCursor(instanceId, cursor)
                        _logger.LogInformation(
                            "Event sync for {Id}: cursor seeded at head seq {Seq}",
                            instanceId, cursor)
                    End If
                    seeded = True
                End If

                ' ---- Catch-up (backfill mode) ----
                If Not seeded Then
                    Dim caughtUp = False
                    While Not caughtUp AndAlso Not token.IsCancellationRequested
                        Dim batch = Await client.GetEventsAsync(instanceId, cursor, BatchMax, token)
                        If batch.Events.Count > 0 Then
                            cursor = Await ApplyBatchResilientAsync(
                                instanceId, im, batch.Events, liveMode:=False,
                                lastSeq:=batch.LastSeq)
                        End If
                        caughtUp = batch.Events.Count < BatchMax
                    End While
                End If

                ' ---- Bucket seed-merge from /players ----
                ' AFTER catch-up so replayed joins/leaves dedup
                ' against an empty bucket, then the node's current
                ' roster fills in pre-cursor sessions.
                Try
                    Dim players = Await client.GetPlayersAsync(instanceId, token)
                    im.SeedActivePlayersBucket(instanceId, players)
                Catch
                    ' Best-effort — the next join/leave event keeps
                    ' the bucket converging anyway.
                End Try

                _logger.LogInformation(
                    "Event sync live for {Id} (cursor {Seq})", instanceId, cursor)

                ' ---- Live phase: SSE push, pull fallback ----
                ' Each outer iteration is one SSE session. The
                ' session's since = the committed cursor, so the
                ' node replays whatever the Manager missed while
                ' disconnected, then goes live — replay and live
                ' apply through the same per-event transactional
                ' path (cursor rides the SaveChanges), preserving
                ' the Q6 exactly-once property across drops and
                ' transport handoffs. On SSE failure the loop
                ' pulls by cursor (the pre-SSE 3s cadence) for a
                ' bounded window before retrying SSE.
                Dim cursorBox As New StreamCursor With {.Value = cursor}
                Dim sseFailures As Integer = 0
                While Not token.IsCancellationRequested
                    ' Re-resolve the client on every (re)connect. This loop
                    ' runs for the life of the instance; the client captured
                    ' at loop entry carries the auth token of THAT moment.
                    ' After a token rotation (TlsOnboardingService evicts the
                    ' cached client) the stale one kept hitting the node with
                    ' the old token: 401s logged only at Debug, 10 inside 5
                    ' min -> the node's AuthFailureTracker locked the Manager's
                    ' IP for 15 min -> every status poll 429'd -> repeat until
                    ' the Manager was restarted (2026-10-11, all six nodes).
                    Dim refreshed = im.GetNodeClientForInstance(instanceId)
                    If refreshed IsNot Nothing Then client = refreshed

                    Dim sseStarted = DateTime.UtcNow
                    Dim startCursor = cursorBox.Value
                    Dim sseError As Exception = Nothing
                    Try
                        ' Non-async lambda returning Task — the
                        ' named async helper keeps VB's async-lambda
                        ' inference quirks out of the picture.
                        Await client.StreamEventsAsync(
                            instanceId, cursorBox.Value,
                            Function(rec) ApplyLiveEventAsync(instanceId, im, rec, cursorBox),
                            token)
                    Catch ex As OperationCanceledException
                        Return
                    Catch ex As Exception
                        ' Transport failure, idle timeout, or an
                        ' apply exception from inside the callback —
                        ' all safe to retry: the committed cursor is
                        ' the source of truth for what applied.
                        sseError = ex
                    End Try
                    If token.IsCancellationRequested Then Return

                    ' Healthy session (lived a while, or made
                    ' cursor progress) resets the retry ladder;
                    ' quick deaths climb it.
                    If (DateTime.UtcNow - sseStarted) >= HealthySseSession OrElse
                       cursorBox.Value > startCursor Then
                        sseFailures = 0
                    Else
                        sseFailures = Math.Min(sseFailures + 1, 5)
                    End If
                    If sseError IsNot Nothing Then
                        _logger.LogDebug(sseError,
                            "Event SSE dropped for {Id} (cursor {Seq}); pull fallback engaged",
                            instanceId, cursorBox.Value)
                    Else
                        _logger.LogDebug(
                            "Event SSE ended for {Id} (cursor {Seq}); pull fallback engaged",
                            instanceId, cursorBox.Value)
                    End If

                    ' ---- Pull-fallback window ----
                    Dim windowMs = Math.Min(SseRetryBaseMs << sseFailures, SseRetryMaxMs)
                    Dim fallbackUntil = DateTime.UtcNow.AddMilliseconds(windowMs)
                    While DateTime.UtcNow < fallbackUntil AndAlso Not token.IsCancellationRequested
                        ' VB disallows Await inside Catch, so the
                        ' error backoff is flagged here and awaited
                        ' after the Try block instead.
                        Dim backoff = False
                        Try
                            Await Task.Delay(LivePollIntervalMs, token)
                            ' Same stale-token hazard as the SSE path above.
                            Dim pullClient = im.GetNodeClientForInstance(instanceId)
                            If pullClient IsNot Nothing Then client = pullClient
                            Dim batch = Await client.GetEventsAsync(instanceId, cursorBox.Value, BatchMax, token)
                            If batch.Events.Count > 0 Then
                                cursorBox.Value = Await ApplyBatchResilientAsync(
                                    instanceId, im, batch.Events, liveMode:=True,
                                    lastSeq:=batch.LastSeq)
                            End If
                        Catch ex As OperationCanceledException
                            Return
                        Catch ex As Exception
                            ' Node unreachable / transient API error —
                            ' back off and keep the loop alive. The
                            ' cursor makes retries harmless.
                            ' 401 is NOT transient: it means this loop's token
                            ' is wrong, and each attempt counts toward the
                            ' node's lockout. Surface it.
                            Dim api = TryCast(ex, NodeApiException)
                            If api IsNot Nothing AndAlso api.StatusCode.HasValue AndAlso
                               api.StatusCode.Value = System.Net.HttpStatusCode.Unauthorized Then
                                _logger.LogWarning(
                                    "Event sync for {Id}: node rejected auth token (401) — stale client after token rotation?",
                                    instanceId)
                            Else
                                _logger.LogDebug(ex,
                                    "Event sync iteration failed for {Id}", instanceId)
                            End If
                            backoff = True
                        End Try
                        If backoff Then
                            Try
                                Await Task.Delay(ErrorBackoffMs, token)
                            Catch
                                Return
                            End Try
                        End If
                    End While
                End While
            Catch ex As OperationCanceledException
                ' Normal.
            Catch ex As Exception
                _logger.LogWarning(ex, "Event sync loop ended for {Id}", instanceId)
            Finally
                ' Deregister so the background poll's Ensure can spin
                ' up a fresh loop (same self-cleanup pattern as
                ' StreamLogsInBackgroundAsync). Compare-and-remove:
                ' only remove our own registration.
                Dim current As SyncRun = Nothing
                If _active.TryGetValue(instanceId, current) AndAlso
                   ReferenceEquals(current, run) Then
                    _active.TryRemove(instanceId, current)
                    Try : current.Cts.Dispose() : Catch : End Try
                End If
            End Try
        End Function

        Private Function TryLoadCursor(instanceId As String, ByRef cursor As Long) As Boolean
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of Data.GsmDbContext)()
                    Dim raw = db.GetSetting(CursorKey(instanceId), Nothing)
                    If String.IsNullOrEmpty(raw) Then Return False
                    Return Long.TryParse(raw, cursor)
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex, "Cursor load failed for {Id}", instanceId)
                Return False
            End Try
        End Function

        Private Sub SaveSeededCursor(instanceId As String, cursor As Long)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of Data.GsmDbContext)()
                    db.SetSetting(CursorKey(instanceId), cursor.ToString())
                    db.SaveChanges()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Cursor seed persist failed for {Id}", instanceId)
            End Try
        End Sub

        ''' <summary>
        ''' Mutable cursor cell shared between the live loop and
        ''' the per-event SSE apply callback. A tiny class (not a
        ''' ByRef local) because the callback is a lambda closure
        ''' crossing an await boundary.
        ''' </summary>
        Private Class StreamCursor
            Public Value As Long
        End Class

        ''' <summary>
        ''' Batch apply that cannot WEDGE the loop (2026-08-29).
        ''' Live-observed failure mode: one batch whose apply threw
        ''' (a duplicate PlayerSessions insert — see
        ''' InstanceManager.UpsertPlayerSession) left the cursor
        ''' un-advanced, the loop ended, the background Ensure
        ''' restarted it, and it re-pulled the SAME batch forever:
        ''' ~20 stack traces a minute, zero History rows for days,
        ''' while the Manager looked healthy. Whatever the root
        ''' cause of a bad batch, the loop must make progress.
        '''
        ''' Strategy: try the batch as one transaction (the normal,
        ''' fast path). On failure, re-apply event by event — each in
        ''' its own transaction with the cursor at that event's Seq —
        ''' so every good event lands exactly once. An event that
        ''' STILL fails alone is poison: log it at Error with its
        ''' Seq/Kind/raw line, commit the cursor PAST it (it is never
        ''' retried; the raw line stays in the node's event table and
        ''' console log for forensics), and continue. The Q6
        ''' exactly-once property holds for every applied event; the
        ''' skipped one is the explicit, logged exception.
        ''' </summary>
        Private Async Function ApplyBatchResilientAsync(instanceId As String,
                                                        im As InstanceManager,
                                                        events As IReadOnlyList(Of EventRecord),
                                                        liveMode As Boolean,
                                                        lastSeq As Long) As Task(Of Long)
            Dim batchFailed = False
            Try
                Return Await im.ApplyStreamPlayerEventsAsync(
                    instanceId, events, liveMode,
                    cursorKey:=CursorKey(instanceId), cursorSeq:=lastSeq)
            Catch ex As OperationCanceledException
                Throw
            Catch batchEx As Exception
                batchFailed = True
                _logger.LogWarning(batchEx,
                    "Event sync batch apply failed for {Id} ({Count} event(s), through seq {Seq}) — retrying event-by-event",
                    instanceId, events.Count, lastSeq)
            End Try
            If Not batchFailed Then Return lastSeq

            Dim cursor As Long = 0
            Dim haveCursor = TryLoadCursor(instanceId, cursor)
            For Each ev In events
                If ev Is Nothing Then Continue For
                If haveCursor AndAlso ev.Seq <= cursor Then Continue For
                Dim poison = False
                Try
                    cursor = Await im.ApplyStreamPlayerEventsAsync(
                        instanceId, New List(Of EventRecord) From {ev}, liveMode,
                        cursorKey:=CursorKey(instanceId), cursorSeq:=ev.Seq)
                    haveCursor = True
                Catch ex As OperationCanceledException
                    Throw
                Catch evEx As Exception
                    poison = True
                    _logger.LogError(evEx,
                        "Event sync POISON event skipped for {Id}: seq {Seq} kind {Kind} at {At} raw=[{Raw}] — cursor advanced past it",
                        instanceId, ev.Seq, ev.Kind, ev.UtcAt, If(ev.Raw, ""))
                End Try
                If poison Then
                    SaveSeededCursor(instanceId, ev.Seq)
                    cursor = ev.Seq
                    haveCursor = True
                End If
            Next
            Return Math.Max(cursor, lastSeq)
        End Function

        ''' <summary>
        ''' Applies ONE SSE-delivered event through the same
        ''' transactional path the pull loop uses (projection rows
        ''' + cursor in one SaveChanges — the Q6 mechanism), then
        ''' advances the in-memory cursor. Join/leave events are
        ''' rare enough that a transaction per event is fine even
        ''' during an SSE gap replay; non-player kinds still move
        ''' the cursor so the stream can't wedge on them. Throws on
        ''' apply failure — StreamEventsAsync propagates it, the
        ''' live loop tears the session down, and the reconnect
        ''' replays from the last COMMITTED cursor.
        ''' </summary>
        Private Function ApplyLiveEventAsync(instanceId As String,
                                             im As InstanceManager,
                                             rec As EventRecord,
                                             cursor As StreamCursor) As Task
            Return ApplyLiveEventCoreAsync(instanceId, im, rec, cursor)
        End Function

        Private Async Function ApplyLiveEventCoreAsync(instanceId As String,
                                                       im As InstanceManager,
                                                       rec As EventRecord,
                                                       cursor As StreamCursor) As Task
            Dim newCursor = Await im.ApplyStreamPlayerEventsAsync(
                instanceId,
                New List(Of EventRecord) From {rec},
                liveMode:=True,
                cursorKey:=CursorKey(instanceId),
                cursorSeq:=rec.Seq)
            cursor.Value = newCursor
        End Function

        ' --------------------------------------------------------
        '  manager:identity push
        ' --------------------------------------------------------

        ''' <summary>
        ''' Push a utility-plugin identity contribution (LoMyrealm's
        ''' myrealm-resolved names being the motivating case) to
        ''' every running, stream-capable instance currently serving
        ''' the contribution's (GameId, SessionScope). Fire-and-
        ''' forget from the caller's perspective; failures log at
        ''' Debug — enrichment must never break the contribution
        ''' path that feeds the Manager's own resolver.
        ''' </summary>
        Public Async Function PushIdentityContributionAsync(gameId As String,
                                                            sessionScope As String,
                                                            characterId As String,
                                                            platformUserId As String,
                                                            displayName As String,
                                                            platform As String) As Task
            Try
                If String.IsNullOrEmpty(gameId) Then Return

                Dim fields As New Dictionary(Of String, String)(StringComparer.Ordinal)
                If Not String.IsNullOrEmpty(characterId) Then fields("CharacterId") = characterId
                If Not String.IsNullOrEmpty(platformUserId) Then fields("PlatformUserId") = platformUserId
                If Not String.IsNullOrEmpty(displayName) Then fields("DisplayName") = displayName
                If Not String.IsNullOrEmpty(platform) Then fields("Platform") = platform
                If fields.Count = 0 Then Return

                Dim im = ManagerProgram.Services.GetService(Of InstanceManager)()
                If im Is Nothing Then Return

                For Each instanceId In im.FindInstancesForIdentityScope(gameId, sessionScope)
                    Try
                        Dim client = im.GetNodeClientForInstance(instanceId)
                        If client Is Nothing Then Continue For
                        Dim version = Await client.GetApiVersionAsync(False, CancellationToken.None)
                        If version Is Nothing OrElse Not version.EventStreamSupported Then Continue For

                        Await client.AppendIdentityEventAsync(
                            instanceId,
                            New AppendEventRequest With {
                                .Kind = ParsedEventKind.PlayerIdentity,
                                .UtcAt = DateTime.UtcNow,
                                .Fields = fields
                            },
                            CancellationToken.None)
                    Catch ex As Exception
                        _logger.LogDebug(ex,
                            "Identity push failed for {Id}", instanceId)
                    End Try
                Next
            Catch ex As Exception
                _logger.LogDebug(ex,
                    "Identity push failed for scope {Game}/{Scope}", gameId, sessionScope)
            End Try
        End Function

    End Class

End Namespace
