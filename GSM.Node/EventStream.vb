Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.AspNetCore.Http
Imports Microsoft.Data.Sqlite
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  EventStreamStore — the canonical per-instance append-only
'  event stream (EventStream Unification, Slice 1).
'
'  One stream per scope, owned by the NODE (node autonomy:
'  everything here works with the Manager offline). Ingestion
'  sources (parse-rule hits in EventStore, ProcessManager
'  lifecycle emitters, future REST pollers) append EventRecords;
'  everything downstream — the in-memory player/state folds, the
'  chat table, Manager sync (Slice 3) — is a projection or a
'  cursor consumer of this stream.
'
'  Keying: (ScopeType, ScopeId, Seq) from day one, per the plan
'  ("retrofitting a key is misery"), even though only
'  ScopeType.Instance is used in Slice 1. Seq is per-scope,
'  strictly increasing, assigned under the per-scope append lock,
'  and continues from MAX(seq) across node restarts (lazy-loaded
'  on the first append for a scope).
'
'  Replay dedup: parse-derived events re-flow on adoption (the
'  shim replays its ring buffer; tailers backfill with
'  skipResume) with STABLE timestamps (the UE4 log-line stamp,
'  same trick ux_chat_dedup uses). A UNIQUE index on
'  (scope_type, scope_id, source_id, kind, utc_at, raw) +
'  INSERT OR IGNORE makes those replays append nothing — but the
'  caller still applies the event to its in-memory fold, so
'  adoption rebuilds the player list exactly as before. This is
'  REPLAY dedup only. Cross-SOURCE duplicates are deliberately
'  kept (first-come-append is the plan's default; per-kind source
'  authority is a Slice 2 opt-in).
'
'  Seq only advances on a real insert, so replays create neither
'  duplicates nor gaps (verification gate Q1).
'
'  Retention: per-kind pruning on the existing node pruner
'  pattern (startup + periodic timer). Defaults per the plan
'  (decided 2026-08-02): join/leave NEVER pruned (last-seen is
'  built from them; hard purge via the /events/purge endpoint is
'  the only sanctioned removal), lifecycle 365d, chat 90d,
'  state/identity/custom 30d.
' ============================================================

Namespace GSM.Node

    ''' <summary>
    ''' Scope axis of the stream key. Only Instance is used in
    ''' Slice 1; Installation and Node exist so the SQLite key
    ''' shape is right from day one.
    ''' </summary>
    Public Enum EventScopeType
        Instance = 0
        Installation = 1
        Node = 2
    End Enum

    ''' <summary>
    ''' Result of an append attempt. Record is always populated
    ''' (so folds can consume it); Inserted is False when the
    ''' replay-dedup index suppressed persistence, in which case
    ''' Record.Seq is -1 (no sequence number was consumed).
    ''' </summary>
    Public Class EventAppendResult
        Public Property Record As EventRecord
        Public Property Inserted As Boolean

        ''' <summary>
        ''' True when the event was dropped by a purge watermark:
        ''' its timestamp falls at or before an operator hard-purge
        ''' cutoff for its kind. Unlike a replay-dedup suppression
        ''' (Inserted=False, Purged=False — the event IS in the
        ''' stream, just already persisted), a purged event must
        ''' not be folded either: purged history stays gone.
        ''' </summary>
        Public Property Purged As Boolean

        ''' <summary>
        ''' True when the event was dropped by the source-authority
        ''' map (EventStream Slice 2): its kind has a declared owner
        ''' and this event came from a different source. Not folded
        ''' either — authority IS the dedupe, so the non-owning
        ''' source's copy must not reach projections. Counted in the
        ''' per-source suppressed metric.
        ''' </summary>
        Public Property Suppressed As Boolean
    End Class

    Public Class EventStreamStore
        Implements IDisposable

        Private ReadOnly _database As NodeDatabase
        Private ReadOnly _logger As ILogger(Of EventStreamStore)
        Private ReadOnly _scopes As New ConcurrentDictionary(Of String, ScopeState)
        Private ReadOnly _retentionTimer As Timer

        ' Per-kind retention windows, in days. Kinds not listed
        ' fall back to DefaultRetentionDays. 0 (or negative) means
        ' NEVER pruned. Values per the plan's Node persistence
        ' section (decided 2026-08-02).
        '
        ' PlayerJoin/PlayerLeave are 0 by DESIGN, not oversight:
        ' join/leave history is what last-seen is built from, and
        ' automatic pruning would silently break last-seen for any
        ' player inactive longer than the window. The only
        ' sanctioned removal is an explicit operator hard
        ' purge/rebuild done manually against the events table.
        Private Const DefaultRetentionDays As Integer = 30
        Private Shared ReadOnly _retentionDays As New Dictionary(Of ParsedEventKind, Integer) From {
            {ParsedEventKind.PlayerJoin, 0},
            {ParsedEventKind.PlayerLeave, 0},
            {ParsedEventKind.ChatMessage, 90},
            {ParsedEventKind.ProcessStarted, 365},
            {ParsedEventKind.ProcessExited, 365},
            {ParsedEventKind.ProcessExitedUnexpectedly, 365},
            {ParsedEventKind.CrashRestartScheduled, 365},
            {ParsedEventKind.CrashLoopHalted, 365},
            {ParsedEventKind.InstanceStopRequested, 365},
            {ParsedEventKind.InstanceAdopted, 365}
        }

        Public Sub New(database As NodeDatabase, logger As ILogger(Of EventStreamStore))
            _database = database
            _logger = logger
            EnsureEventsTable()

            ' Existing pruner pattern: prune shortly after startup,
            ' then periodically. 1-minute initial due keeps startup
            ' (adoption pass, first requests) out of the prune's way.
            _retentionTimer = New Timer(AddressOf PruneTimerTick, Nothing,
                                        TimeSpan.FromMinutes(1), TimeSpan.FromHours(6))
        End Sub

        Private Sub EnsureEventsTable()
            Using conn = _database.OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "
                        CREATE TABLE IF NOT EXISTS events (
                            scope_type  INTEGER NOT NULL,
                            scope_id    TEXT NOT NULL,
                            seq         INTEGER NOT NULL,
                            utc_at      TEXT NOT NULL,
                            kind        INTEGER NOT NULL,
                            source_id   TEXT NOT NULL DEFAULT '',
                            fields_json TEXT,
                            raw         TEXT NOT NULL DEFAULT '',
                            PRIMARY KEY (scope_type, scope_id, seq)
                        );
                        CREATE INDEX IF NOT EXISTS ix_events_scope_kind
                            ON events(scope_type, scope_id, kind);
                        CREATE INDEX IF NOT EXISTS ix_events_kind_time
                            ON events(kind, utc_at);
                        CREATE UNIQUE INDEX IF NOT EXISTS ux_events_replay_dedup
                            ON events(scope_type, scope_id, source_id, kind, utc_at, raw);

                        -- Operator hard-purge watermarks. A row here
                        -- means: events of this kind with utc_at at or
                        -- before purged_through_utc are PURGED for this
                        -- scope — deleted from the table AND blocked
                        -- from re-appending. The append-time gate is
                        -- what stops purged history from crawling back
                        -- in via tailer backfill / adoption replay of
                        -- old log lines (the pre-stream chat purge
                        -- suffered exactly that comeback).
                        CREATE TABLE IF NOT EXISTS event_purge_watermarks (
                            scope_type INTEGER NOT NULL,
                            scope_id   TEXT NOT NULL,
                            kind       INTEGER NOT NULL,
                            purged_through_utc TEXT NOT NULL,
                            PRIMARY KEY (scope_type, scope_id, kind)
                        );
                    "
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ' --------------------------------------------------------
        '  Append
        ' --------------------------------------------------------

        ''' <summary>
        ''' Appends one event to a scope's stream. Seq is assigned
        ''' under the per-scope lock and persists in the same
        ''' critical section, so seq order == persistence order ==
        ''' notification order. Returns the record (for fold
        ''' application) plus whether it was actually inserted —
        ''' False means the replay-dedup index recognised this as a
        ''' re-flow of an already-persisted event (same scope,
        ''' source, kind, timestamp, raw line); callers should
        ''' still fold it into in-memory state but nothing was
        ''' persisted and no subscriber was notified.
        '''
        ''' Never throws on storage failure — the parse path and
        ''' lifecycle emitters must not die because of a disk
        ''' hiccup. On failure the record comes back with Seq -1
        ''' and Inserted False, and a warning is logged.
        ''' </summary>
        Public Function Append(scopeType As EventScopeType,
                               scopeId As String,
                               utcAt As DateTime,
                               kind As ParsedEventKind,
                               sourceId As String,
                               fields As IDictionary(Of String, String),
                               raw As String) As EventAppendResult
            Dim record As New EventRecord With {
                .Seq = -1,
                .InstanceId = scopeId,
                .UtcAt = utcAt,
                .Kind = kind,
                .SourceId = If(sourceId, ""),
                .Fields = If(fields Is Nothing OrElse fields.Count = 0,
                             Nothing,
                             New Dictionary(Of String, String)(fields, StringComparer.OrdinalIgnoreCase)),
                .Raw = If(String.IsNullOrEmpty(raw), Nothing, raw)
            }
            Dim result As New EventAppendResult With {.Record = record, .Inserted = False}
            If String.IsNullOrEmpty(scopeId) Then Return result

            Dim state = GetScope(scopeType, scopeId)
            Try
                SyncLock state.Lock
                    If state.NextSeq < 1 Then
                        state.NextSeq = ReadMaxSeq(scopeType, scopeId) + 1
                    End If

                    ' Purge-watermark gate (BEFORE the insert attempt —
                    ' after a purge the dedup rows are gone, so the
                    ' unique index can no longer stop a replay from
                    ' re-inserting purged history). Timestamps compare
                    ' as ISO-8601 text, same convention as retention.
                    EnsureWatermarksLoaded(scopeType, scopeId, state)
                    Dim atString = utcAt.ToString("o")
                    Dim wm As String = Nothing
                    If state.Watermarks.TryGetValue(CInt(kind), wm) AndAlso
                       String.Compare(atString, wm, StringComparison.Ordinal) <= 0 Then
                        ' NEVER silent (guard added 2026-08-12 after a live
                        ' hunt: a future-cutoff purge watermark was dropping
                        ' every new join/leave/chat with no trace). Warn at
                        ' most once a minute per scope.
                        state.WatermarkDropCount += 1
                        If (DateTime.UtcNow - state.LastWatermarkWarnUtc) > TimeSpan.FromMinutes(1) Then
                            state.LastWatermarkWarnUtc = DateTime.UtcNow
                            _logger.LogWarning(
                                "Append dropped {Kind} for {ScopeId}: purge watermark {Wm} >= event {At} " &
                                "({Count} watermark-dropped since node start). A watermark at/after 'now' blinds " &
                                "this kind — inspect with GSM.Node --dump-watermarks",
                                kind, scopeId, wm, atString, state.WatermarkDropCount)
                        End If
                        result.Purged = True
                        Return result
                    End If

                    ' Source-authority gate (Slice 2 dedupe): a kind
                    ' with a declared owner accepts events from that
                    ' owner ONLY; other sources' copies are dropped
                    ' here and counted, so a double-sourced setup is
                    ' observable in /events/sources rather than
                    ' double-counting in every projection. Kinds with
                    ' no owner keep the first-come-append default.
                    Dim srcKey = If(sourceId, "")
                    Dim owner As String = Nothing
                    If state.AuthorityByKind IsNot Nothing AndAlso
                       state.AuthorityByKind.TryGetValue(CInt(kind), owner) AndAlso
                       Not String.Equals(owner, srcKey, StringComparison.Ordinal) Then
                        GetCounter(state, srcKey).Suppressed += 1
                        result.Suppressed = True
                        Return result
                    End If

                    Dim fieldsJson As String = Nothing
                    If record.Fields IsNot Nothing Then
                        fieldsJson = JsonSerializer.Serialize(record.Fields)
                    End If

                    Dim inserted As Integer
                    Using conn = _database.OpenConnection()
                        Using cmd = conn.CreateCommand()
                            cmd.CommandText = "
                                INSERT OR IGNORE INTO events
                                    (scope_type, scope_id, seq, utc_at, kind, source_id, fields_json, raw)
                                VALUES ($stype, $sid, $seq, $at, $kind, $src, $fields, $raw)
                            "
                            cmd.Parameters.AddWithValue("$stype", CInt(scopeType))
                            cmd.Parameters.AddWithValue("$sid", scopeId)
                            cmd.Parameters.AddWithValue("$seq", state.NextSeq)
                            cmd.Parameters.AddWithValue("$at", atString)
                            cmd.Parameters.AddWithValue("$kind", CInt(kind))
                            cmd.Parameters.AddWithValue("$src", If(sourceId, ""))
                            cmd.Parameters.AddWithValue("$fields", If(CObj(fieldsJson), DBNull.Value))
                            cmd.Parameters.AddWithValue("$raw", If(raw, ""))
                            inserted = cmd.ExecuteNonQuery()
                        End Using
                    End Using

                    If inserted = 1 Then
                        record.Seq = state.NextSeq
                        state.NextSeq += 1
                        result.Inserted = True
                        GetCounter(state, srcKey).Appended += 1

                        ' Notify live SSE followers. Subscribers
                        ' re-read from SQLite by cursor, so a signal
                        ' is just a wake-up — no payload handoff, no
                        ' ordering hazard.
                        For Each subscription In state.Subscribers
                            subscription.Signal()
                        Next
                    End If
                End SyncLock
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to append {Kind} event to stream for {ScopeType}/{ScopeId}",
                    kind, scopeType, scopeId)
            End Try
            Return result
        End Function

        ' --------------------------------------------------------
        '  Query
        ' --------------------------------------------------------

        ''' <summary>
        ''' Cursor read: events for an instance with Seq &gt;
        ''' sinceSeq, ascending, capped at max. Seq numbering
        ''' starts at 1, so sinceSeq = 0 reads from the beginning.
        ''' </summary>
        Public Function GetEvents(instanceId As String,
                                  sinceSeq As Long,
                                  max As Integer) As List(Of EventRecord)
            Return GetEventsCore(EventScopeType.Instance, instanceId, sinceSeq, max)
        End Function

        Private Function GetEventsCore(scopeType As EventScopeType,
                                       scopeId As String,
                                       sinceSeq As Long,
                                       max As Integer) As List(Of EventRecord)
            Dim results As New List(Of EventRecord)
            If String.IsNullOrEmpty(scopeId) Then Return results
            If max <= 0 Then max = 500
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            SELECT seq, utc_at, kind, source_id, fields_json, raw
                            FROM events
                            WHERE scope_type = $stype AND scope_id = $sid AND seq > $since
                            ORDER BY seq ASC
                            LIMIT $max
                        "
                        cmd.Parameters.AddWithValue("$stype", CInt(scopeType))
                        cmd.Parameters.AddWithValue("$sid", scopeId)
                        cmd.Parameters.AddWithValue("$since", sinceSeq)
                        cmd.Parameters.AddWithValue("$max", max)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                Dim rec As New EventRecord With {
                                    .Seq = reader.GetInt64(0),
                                    .InstanceId = scopeId,
                                    .Kind = CType(reader.GetInt32(2), ParsedEventKind),
                                    .SourceId = If(reader.IsDBNull(3), "", reader.GetString(3))
                                }
                                Try
                                    rec.UtcAt = DateTime.Parse(reader.GetString(1),
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.RoundtripKind)
                                Catch
                                    ' Malformed timestamp — leave at default
                                    ' rather than dropping the record.
                                End Try
                                If Not reader.IsDBNull(4) Then
                                    Try
                                        rec.Fields = JsonSerializer.Deserialize(
                                            Of Dictionary(Of String, String))(reader.GetString(4))
                                    Catch
                                    End Try
                                End If
                                Dim rawVal = If(reader.IsDBNull(5), "", reader.GetString(5))
                                rec.Raw = If(rawVal.Length = 0, Nothing, rawVal)
                                results.Add(rec)
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to read events for {ScopeId} since {Since}",
                                   scopeId, sinceSeq)
            End Try
            Return results
        End Function

        ' --------------------------------------------------------
        '  Lifecycle fold (projection over lifecycle kinds)
        '
        '  Server-state-adjacent projection demonstrating the
        '  "current state is a fold of the stream" model for the
        '  lifecycle kinds (verification gate Q3). Not yet exposed
        '  on the wire — the existing /status endpoint remains the
        '  wire surface for process state; Slice 3 may surface
        '  this fold for Manager-side history reconstruction.
        ' --------------------------------------------------------

        Public Class LifecycleFoldResult
            Public Property State As String
            Public Property CrashCount As Integer
            Public Property LastExitCode As Integer?
            Public Property LastSeq As Long
        End Class

        ''' <summary>
        ''' Folds the lifecycle events of an instance's stream into
        ''' a current process state: Running / Stopped / Crashed /
        ''' CrashLoopHalted / Unknown, plus the crash count of the
        ''' current run streak (resets on ProcessStarted with no
        ''' intervening crash chain, mirroring the node's in-memory
        ''' CrashCount semantics closely enough for verification).
        ''' </summary>
        Public Function FoldLifecycle(instanceId As String) As LifecycleFoldResult
            Dim fold As New LifecycleFoldResult With {
                .State = "Unknown", .CrashCount = 0, .LastSeq = 0
            }
            Dim cursor As Long = 0
            Do
                Dim batch = GetEvents(instanceId, cursor, 1000)
                If batch.Count = 0 Then Exit Do
                For Each rec In batch
                    cursor = rec.Seq
                    fold.LastSeq = rec.Seq
                    Select Case rec.Kind
                        Case ParsedEventKind.ProcessStarted
                            fold.State = "Running"
                        Case ParsedEventKind.ProcessExited
                            fold.State = "Stopped"
                            fold.CrashCount = 0
                            fold.LastExitCode = TryFieldInt(rec, "ExitCode")
                        Case ParsedEventKind.ProcessExitedUnexpectedly
                            fold.State = "Crashed"
                            fold.LastExitCode = TryFieldInt(rec, "ExitCode")
                        Case ParsedEventKind.CrashRestartScheduled
                            Dim c = TryFieldInt(rec, "CrashCount")
                            If c.HasValue Then fold.CrashCount = c.Value
                        Case ParsedEventKind.CrashLoopHalted
                            fold.State = "CrashLoopHalted"
                        Case ParsedEventKind.InstanceStopRequested
                            ' Intent, not a state transition.
                        Case ParsedEventKind.InstanceAdopted
                            fold.State = "Running"
                    End Select
                Next
            Loop
            Return fold
        End Function

        Private Shared Function TryFieldInt(rec As EventRecord, name As String) As Integer?
            If rec.Fields Is Nothing Then Return Nothing
            Dim v As String = Nothing
            If Not rec.Fields.TryGetValue(name, v) Then Return Nothing
            Dim parsed As Integer
            If Integer.TryParse(v, parsed) Then Return parsed
            Return Nothing
        End Function

        ' --------------------------------------------------------
        '  SSE streaming (replay the gap, then go live)
        ' --------------------------------------------------------

        ''' <summary>
        ''' Streams an instance's events as SSE: everything after
        ''' sinceSeq first (the gap), then live events as they are
        ''' appended, until the client disconnects. Because events
        ''' are durable and cursor-addressed, backfill and live are
        ''' the same representation — the loop just re-reads from
        ''' SQLite past its cursor whenever the append path signals.
        ''' Registering the subscription BEFORE the first read
        ''' means no event can fall between replay and live (a
        ''' signal for an event the read already returned is a
        ''' harmless empty re-read).
        ''' </summary>
        ''' <summary>Idle interval after which the SSE loop writes a
        ''' keep-alive comment. Must stay comfortably under the
        ''' Manager's 45s idle cutoff.</summary>
        Private Shared ReadOnly KeepAliveInterval As TimeSpan = TimeSpan.FromSeconds(15)

        Public Async Function StreamToResponseAsync(instanceId As String,
                                                    sinceSeq As Long,
                                                    response As HttpResponse,
                                                    cancellation As CancellationToken) As Task
            Dim state = GetScope(EventScopeType.Instance, instanceId)
            Dim subscription As New EventSubscription()
            SyncLock state.Lock
                state.Subscribers.Add(subscription)
            End SyncLock

            Try
                Dim cursor As Long = sinceSeq
                While Not cancellation.IsCancellationRequested
                    Dim batch = GetEvents(instanceId, cursor, 500)
                    If batch.Count = 0 Then
                        Await response.Body.FlushAsync(cancellation)
                        ' Bounded wait: when no event arrives within
                        ' the window, emit an SSE comment (": ..."
                        ' lines are protocol-standard and invisible
                        ' to data parsers) so a consumer can tell a
                        ' quiet stream from a dead TCP path. The
                        ' Manager's StreamEventsAsync pairs this with
                        ' a 45s idle timeout. Additive/wire-compatible:
                        ' comments were always legal SSE.
                        Dim signalled = Await subscription.WaitAsync(KeepAliveInterval, cancellation)
                        If Not signalled Then
                            Await response.WriteAsync(": keepalive" & vbLf & vbLf, cancellation)
                            Await response.Body.FlushAsync(cancellation)
                        End If
                        Continue While
                    End If
                    For Each rec In batch
                        Await WriteSseEventAsync(response, rec, cancellation)
                        cursor = rec.Seq
                    Next
                End While
            Catch ex As OperationCanceledException
                ' Normal — client disconnected.
            Finally
                SyncLock state.Lock
                    state.Subscribers.Remove(subscription)
                End SyncLock
            End Try
        End Function

        Private Shared ReadOnly _sseJsonOptions As New JsonSerializerOptions(JsonSerializerDefaults.Web)

        Private Shared Async Function WriteSseEventAsync(response As HttpResponse,
                                                         rec As EventRecord,
                                                         cancellation As CancellationToken) As Task
            ' Same camelCase JSON the /events endpoint produces, so
            ' backfill-by-GET and live-by-SSE deserialise through
            ' one code path on the Manager side.
            Dim json = JsonSerializer.Serialize(rec, _sseJsonOptions)
            Dim sseData = $"data: {json}" & vbLf & vbLf
            Await response.WriteAsync(sseData, cancellation)
        End Function

        ' --------------------------------------------------------
        '  Retention
        ' --------------------------------------------------------

        Private Sub PruneTimerTick(stateObj As Object)
            Try
                PruneExpired()
            Catch ex As Exception
                _logger.LogWarning(ex, "Event stream retention prune failed")
            End Try
        End Sub

        ''' <summary>
        ''' Deletes events older than their kind's retention
        ''' window. ISO-8601 "o" strings compare correctly as text
        ''' for UTC timestamps, so the cutoff is a plain string
        ''' comparison against utc_at (served by ix_events_kind_time).
        '''
        ''' Seq high-water guard: a scope's current MAX(seq) row is
        ''' NEVER deleted, whatever its age. Restart seq-continuity
        ''' lazy-loads from MAX(seq), and an event with an OLD
        ''' utc_at can legitimately hold the newest seq (adoption
        ''' backfill appends old log lines with their embedded
        ''' timestamps) — pruning that row and then restarting
        ''' would re-issue its seq, breaking the never-reused
        ''' invariant (gate Q1). The guard leaves at most one
        ''' expired row per scope alive, which is harmless.
        ''' </summary>
        Public Sub PruneExpired()
            Dim totalDeleted = 0
            Using conn = _database.OpenConnection()
                For Each kind In [Enum].GetValues(Of ParsedEventKind)()
                    Dim days As Integer
                    If Not _retentionDays.TryGetValue(kind, days) Then
                        days = DefaultRetentionDays
                    End If
                    ' 0/negative = never pruned (join/leave: last-seen
                    ' is built from these; see the retention map).
                    If days <= 0 Then Continue For
                    Dim cutoff = DateTime.UtcNow.AddDays(-days).ToString("o")
                    Try
                        Using cmd = conn.CreateCommand()
                            cmd.CommandText = "
                                DELETE FROM events
                                WHERE kind = $kind AND utc_at < $cutoff
                                  AND seq < (SELECT MAX(e2.seq) FROM events e2
                                             WHERE e2.scope_type = events.scope_type
                                               AND e2.scope_id = events.scope_id)
                            "
                            cmd.Parameters.AddWithValue("$kind", CInt(kind))
                            cmd.Parameters.AddWithValue("$cutoff", cutoff)
                            totalDeleted += cmd.ExecuteNonQuery()
                        End Using
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "Retention prune failed for kind {Kind}", kind)
                    End Try
                Next
            End Using
            If totalDeleted > 0 Then
                _logger.LogInformation(
                    "Event stream retention pruned {Count} expired event(s)", totalDeleted)
            End If
        End Sub

        ' --------------------------------------------------------
        '  Internals
        ' --------------------------------------------------------

        ' --------------------------------------------------------
        '  Hard purge (operator action; the sanctioned removal path
        '  for kinds the automatic pruner never touches)
        ' --------------------------------------------------------

        ''' <summary>
        ''' Deletes a kind's events with utc_at at or before
        ''' throughUtc for a scope AND records a purge watermark so
        ''' replays of those log lines can never re-append them —
        ''' this is what makes a purge STICK (pre-stream chat
        ''' purges came back on the next tailer backfill/adoption
        ''' replay). Returns the number of event rows deleted.
        '''
        ''' The scope's current MAX(seq) row survives even if it
        ''' matches (same seq-reuse guard as retention); its raw
        ''' content can be removed by a later purge once newer
        ''' events exist. Watermarks only move FORWARD (a later
        ''' cutoff replaces an earlier one, never the reverse).
        '''
        ''' Consequence the operator accepts: replay below the
        ''' watermark no longer drives folds, so after purging
        ''' PlayerJoin/PlayerLeave an adoption replay cannot
        ''' rebuild the in-memory player list until fresh activity
        ''' arrives — that is what "hard purge/rebuild" means.
        ''' </summary>
        Public Function PurgeKind(scopeType As EventScopeType,
                                  scopeId As String,
                                  kind As ParsedEventKind,
                                  throughUtc As DateTime) As Integer
            If String.IsNullOrEmpty(scopeId) Then Return 0
            Dim state = GetScope(scopeType, scopeId)
            Dim deleted As Integer = 0
            SyncLock state.Lock
                Dim cutoff = throughUtc.ToString("o")
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            DELETE FROM events
                            WHERE scope_type = $stype AND scope_id = $sid
                              AND kind = $kind AND utc_at <= $cutoff
                              AND seq < (SELECT MAX(e2.seq) FROM events e2
                                         WHERE e2.scope_type = $stype
                                           AND e2.scope_id = $sid)
                        "
                        cmd.Parameters.AddWithValue("$stype", CInt(scopeType))
                        cmd.Parameters.AddWithValue("$sid", scopeId)
                        cmd.Parameters.AddWithValue("$kind", CInt(kind))
                        cmd.Parameters.AddWithValue("$cutoff", cutoff)
                        deleted = cmd.ExecuteNonQuery()
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            INSERT INTO event_purge_watermarks
                                (scope_type, scope_id, kind, purged_through_utc)
                            VALUES ($stype, $sid, $kind, $cutoff)
                            ON CONFLICT(scope_type, scope_id, kind) DO UPDATE SET
                                purged_through_utc = MAX(excluded.purged_through_utc,
                                                         event_purge_watermarks.purged_through_utc)
                        "
                        cmd.Parameters.AddWithValue("$stype", CInt(scopeType))
                        cmd.Parameters.AddWithValue("$sid", scopeId)
                        cmd.Parameters.AddWithValue("$kind", CInt(kind))
                        cmd.Parameters.AddWithValue("$cutoff", cutoff)
                        cmd.ExecuteNonQuery()
                    End Using
                End Using

                ' Keep the in-memory cache on the forward-only rule.
                EnsureWatermarksLoaded(scopeType, scopeId, state)
                Dim existing As String = Nothing
                If Not state.Watermarks.TryGetValue(CInt(kind), existing) OrElse
                   String.Compare(cutoff, existing, StringComparison.Ordinal) > 0 Then
                    state.Watermarks(CInt(kind)) = cutoff
                End If
            End SyncLock
            _logger.LogInformation(
                "Hard-purged {Count} {Kind} event(s) for {ScopeId} through {Cutoff}",
                deleted, kind, scopeId, throughUtc)
            Return deleted
        End Function

        ''' <summary>
        ''' Lazily loads a scope's purge watermarks into its state.
        ''' Caller must hold the scope lock. Best-effort: a read
        ''' failure leaves an empty dictionary (no gating) rather
        ''' than blocking appends.
        ''' </summary>
        Private Sub EnsureWatermarksLoaded(scopeType As EventScopeType,
                                           scopeId As String,
                                           state As ScopeState)
            If state.Watermarks IsNot Nothing Then Return
            Dim map As New Dictionary(Of Integer, String)
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            SELECT kind, purged_through_utc
                            FROM event_purge_watermarks
                            WHERE scope_type = $stype AND scope_id = $sid
                        "
                        cmd.Parameters.AddWithValue("$stype", CInt(scopeType))
                        cmd.Parameters.AddWithValue("$sid", scopeId)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                map(reader.GetInt32(0)) = reader.GetString(1)
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to load purge watermarks for {ScopeId}; appends ungated this session",
                    scopeId)
            End Try
            state.Watermarks = map
        End Sub

        Private Function GetScope(scopeType As EventScopeType, scopeId As String) As ScopeState
            Dim key = CInt(scopeType).ToString(CultureInfo.InvariantCulture) & ":" & scopeId
            Return _scopes.GetOrAdd(key, Function(k) New ScopeState())
        End Function

        ' --------------------------------------------------------
        '  Source authority (Slice 2)
        ' --------------------------------------------------------

        Private Shared ReadOnly _lifecycleKinds As ParsedEventKind() = {
            ParsedEventKind.ProcessStarted,
            ParsedEventKind.ProcessExited,
            ParsedEventKind.ProcessExitedUnexpectedly,
            ParsedEventKind.CrashRestartScheduled,
            ParsedEventKind.CrashLoopHalted,
            ParsedEventKind.InstanceStopRequested,
            ParsedEventKind.InstanceAdopted
        }

        ''' <summary>
        ''' Installs the per-kind source-authority map for a scope.
        ''' Called by ProcessManager on every instance
        ''' registration/adoption from the request's poll-source
        ''' declarations. Lifecycle kinds are ALWAYS forced to the
        ''' node's own "lifecycle" source regardless of what the
        ''' caller passes — plugin declarations cannot claim them.
        ''' Passing Nothing/empty installs the lifecycle-only map
        ''' (every kind else stays first-come-append).
        ''' </summary>
        Public Sub SetAuthority(scopeType As EventScopeType,
                                scopeId As String,
                                ownersByKind As IDictionary(Of ParsedEventKind, String))
            If String.IsNullOrEmpty(scopeId) Then Return
            Dim map As New Dictionary(Of Integer, String)
            If ownersByKind IsNot Nothing Then
                For Each kvp In ownersByKind
                    If Array.IndexOf(_lifecycleKinds, kvp.Key) >= 0 Then Continue For
                    If String.IsNullOrEmpty(kvp.Value) Then Continue For
                    map(CInt(kvp.Key)) = kvp.Value
                Next
            End If
            For Each lk In _lifecycleKinds
                map(CInt(lk)) = "lifecycle"
            Next

            Dim state = GetScope(scopeType, scopeId)
            SyncLock state.Lock
                state.AuthorityByKind = map
            End SyncLock
        End Sub

        ''' <summary>
        ''' Per-source appended/suppressed counters + owned kinds
        ''' for an instance's stream — the diagnostics surface that
        ''' makes the authority map observable (gate Q4b). Counters
        ''' are in-memory per node session by design: they answer
        ''' "is dedupe happening right now", not "how much ever".
        ''' </summary>
        Public Function GetSourceDiagnostics(instanceId As String) As List(Of EventSourceDiagnostics)
            Dim results As New List(Of EventSourceDiagnostics)
            If String.IsNullOrEmpty(instanceId) Then Return results
            Dim state = GetScope(EventScopeType.Instance, instanceId)
            SyncLock state.Lock
                Dim owned As New Dictionary(Of String, List(Of String))(StringComparer.Ordinal)
                If state.AuthorityByKind IsNot Nothing Then
                    For Each kvp In state.AuthorityByKind
                        If Not owned.ContainsKey(kvp.Value) Then
                            owned(kvp.Value) = New List(Of String)
                        End If
                        owned(kvp.Value).Add(CType(kvp.Key, ParsedEventKind).ToString())
                    Next
                End If

                Dim sourceIds As New HashSet(Of String)(state.Counters.Keys, StringComparer.Ordinal)
                For Each src In owned.Keys
                    sourceIds.Add(src)
                Next
                For Each src In sourceIds
                    Dim c As SourceCounters = Nothing
                    state.Counters.TryGetValue(src, c)
                    Dim ownedKinds As List(Of String) = Nothing
                    owned.TryGetValue(src, ownedKinds)
                    results.Add(New EventSourceDiagnostics With {
                        .SourceId = src,
                        .Appended = If(c IsNot Nothing, c.Appended, 0L),
                        .Suppressed = If(c IsNot Nothing, c.Suppressed, 0L),
                        .OwnedKinds = If(ownedKinds, New List(Of String))
                    })
                Next
            End SyncLock
            Return results
        End Function

        ''' <summary>
        ''' Diagnostics Wave 2 (2026-08-15): the purge-watermark
        ''' gate + per-kind event counts for one instance —
        ''' endpoint-ified from the --dump-watermarks CLI (which
        ''' stays for node-box use). Plain reads; safe against the
        ''' running append path.
        ''' </summary>
        Public Function GetWatermarkDiagnostics(instanceId As String) As EventWatermarkDiagnosticsResponse
            Dim resp As New EventWatermarkDiagnosticsResponse With {
                .Watermarks = New List(Of EventWatermarkEntry),
                .EventCounts = New List(Of EventKindCount)
            }
            If String.IsNullOrEmpty(instanceId) Then Return resp
            Dim nowStr = DateTime.UtcNow.ToString("o")
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            SELECT kind, purged_through_utc
                            FROM event_purge_watermarks
                            WHERE scope_id = $sid
                            ORDER BY kind
                        "
                        cmd.Parameters.AddWithValue("$sid", instanceId)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                Dim wm = reader.GetString(1)
                                resp.Watermarks.Add(New EventWatermarkEntry With {
                                    .KindName = CType(reader.GetInt32(0), ParsedEventKind).ToString(),
                                    .PurgedThroughUtc = wm,
                                    .IsFuture = String.Compare(wm, nowStr, StringComparison.Ordinal) > 0
                                })
                            End While
                        End Using
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            SELECT kind, COUNT(*), MAX(utc_at)
                            FROM events
                            WHERE scope_id = $sid
                            GROUP BY kind
                            ORDER BY kind
                        "
                        cmd.Parameters.AddWithValue("$sid", instanceId)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                resp.EventCounts.Add(New EventKindCount With {
                                    .KindName = CType(reader.GetInt32(0), ParsedEventKind).ToString(),
                                    .Count = reader.GetInt64(1),
                                    .LastEventUtc = If(reader.IsDBNull(2), Nothing, reader.GetString(2))
                                })
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Watermark diagnostics read failed for {Id}", instanceId)
            End Try
            Return resp
        End Function

        ''' <summary>Caller must hold the scope lock.</summary>
        Private Shared Function GetCounter(state As ScopeState, sourceId As String) As SourceCounters
            Dim c As SourceCounters = Nothing
            If Not state.Counters.TryGetValue(sourceId, c) Then
                c = New SourceCounters()
                state.Counters(sourceId) = c
            End If
            Return c
        End Function

        Private Class SourceCounters
            Public Appended As Long
            Public Suppressed As Long
        End Class

        ''' <summary>
        ''' Highest seq held by an event of the given kind for an
        ''' instance, or 0 when none exists. Used by EventStore's
        ''' roster hydration to bound its fold window at the last
        ''' ProcessStarted (sessions cannot span a game-process
        ''' restart, so nothing before it can still be online).
        ''' </summary>
        Public Function GetLastSeqOfKind(instanceId As String,
                                         kind As ParsedEventKind) As Long
            If String.IsNullOrEmpty(instanceId) Then Return 0
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            SELECT COALESCE(MAX(seq), 0) FROM events
                            WHERE scope_type = $stype AND scope_id = $sid AND kind = $kind
                        "
                        cmd.Parameters.AddWithValue("$stype", CInt(EventScopeType.Instance))
                        cmd.Parameters.AddWithValue("$sid", instanceId)
                        cmd.Parameters.AddWithValue("$kind", CInt(kind))
                        Return Convert.ToInt64(cmd.ExecuteScalar())
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to read last {Kind} seq for {ScopeId}", kind, instanceId)
                Return 0
            End Try
        End Function

        ''' <summary>
        ''' Current head Seq of an instance's stream (0 when the
        ''' scope has no events). Slice 3: lets a fresh Manager sync
        ''' consumer seed its cursor at "now" instead of replaying
        ''' retained history its legacy ingestion already covered.
        ''' </summary>
        Public Function GetHeadSeq(instanceId As String) As Long
            If String.IsNullOrEmpty(instanceId) Then Return 0
            Return ReadMaxSeq(EventScopeType.Instance, instanceId)
        End Function

        Private Function ReadMaxSeq(scopeType As EventScopeType, scopeId As String) As Long
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            SELECT COALESCE(MAX(seq), 0) FROM events
                            WHERE scope_type = $stype AND scope_id = $sid
                        "
                        cmd.Parameters.AddWithValue("$stype", CInt(scopeType))
                        cmd.Parameters.AddWithValue("$sid", scopeId)
                        Return Convert.ToInt64(cmd.ExecuteScalar())
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to read MAX(seq) for {ScopeType}/{ScopeId}; seq will restart from 1",
                    scopeType, scopeId)
                Return 0
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                _retentionTimer.Dispose()
            Catch
            End Try
        End Sub

        Private Class ScopeState
            Public ReadOnly Property Lock As New Object()
            ''' <summary>Next seq to assign. &lt; 1 = not yet
            ''' initialised from MAX(seq); lazily loaded under
            ''' Lock on first append.</summary>
            Public Property NextSeq As Long = 0
            Public ReadOnly Property Subscribers As New List(Of EventSubscription)
            ''' <summary>Watermark-drop observability (2026-08-12):
            ''' count + rate-limit stamp for the Append warning.</summary>
            Public WatermarkDropCount As Long
            Public LastWatermarkWarnUtc As DateTime

            ''' <summary>Purge watermarks by CInt(kind) → ISO-8601
            ''' cutoff. Nothing = not yet loaded (lazily loaded
            ''' under Lock); empty = loaded, no purges.</summary>
            Public Property Watermarks As Dictionary(Of Integer, String)
            ''' <summary>Per-kind source-authority map (CInt(kind) →
            ''' owning SourceId). Nothing = no map installed — all
            ''' kinds first-come-append. Installed by SetAuthority.</summary>
            Public Property AuthorityByKind As Dictionary(Of Integer, String)
            ''' <summary>Per-source appended/suppressed counters,
            ''' in-memory per node session.</summary>
            Public ReadOnly Property Counters As New Dictionary(Of String, SourceCounters)(StringComparer.Ordinal)
        End Class

    End Class

    ' ============================================================
    '  EventSubscription — wake-up signal for SSE followers.
    '  Same shape as RingBufferStore's LineSubscription; kept
    '  separate because subscribers here re-read durable rows by
    '  cursor rather than draining an in-memory ring.
    ' ============================================================

    Friend Class EventSubscription

        Private ReadOnly _semaphore As New SemaphoreSlim(0)

        Public Sub Signal()
            If _semaphore.CurrentCount = 0 Then
                Try
                    _semaphore.Release()
                Catch ex As SemaphoreFullException
                    ' Already signalled.
                End Try
            End If
        End Sub

        Public Async Function WaitAsync(cancellation As CancellationToken) As Task
            Await _semaphore.WaitAsync(cancellation)
        End Function

        ''' <summary>
        ''' Bounded wait — True when signalled, False on timeout.
        ''' Used by the SSE loop's keep-alive cadence.
        ''' </summary>
        Public Async Function WaitAsync(timeout As TimeSpan,
                                        cancellation As CancellationToken) As Task(Of Boolean)
            Return Await _semaphore.WaitAsync(timeout, cancellation)
        End Function

    End Class

End Namespace
