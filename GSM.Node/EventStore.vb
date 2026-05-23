Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Data
Imports System.Linq
Imports System.Text.RegularExpressions
Imports Microsoft.Data.Sqlite
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  EventStore — applies parse rules to log lines, tracks
'  per-instance player state and server state in memory, and
'  persists chat history to SQLite.
'
'  Phase 5g-1 identity model:
'    A PlayerSession is keyed in the in-memory Players dict by
'    whatever identifier was first sighted (preferring CharacterId
'    since it's stable for the whole UE4 connection lifecycle).
'    Subsequent events enrich the same record via partial-key
'    correlation across CharacterId, PlatformUserId, DisplayName,
'    PlatformPersona, and RemoteAddress.
'
'    Two name surfaces:
'      PlatformPersona — Steam handle / Xbox gamertag, from the
'        login URL's Name parameter. Known on first sighting.
'      DisplayName — in-game character name, from LogPersistence
'        "Persisting <name>" lines. Lags join by up to one
'        autosave-tick interval (LO ~2 min).
'
'    Pending-identity stash bridges the race where a Persisting
'    line fires (DisplayName + PlatformUserId) before the session
'    has a known PlatformUserId — e.g. Processing-character-update
'    hasn't arrived yet, or this is a player whose connection
'    landed mid-tick. Stashed by PlatformUserId; drained when a
'    subsequent event resolves PlatformUserId on a session.
' ============================================================

Namespace GSM.Node

    Public Class EventStore

        Private ReadOnly _instances As New ConcurrentDictionary(Of String, InstanceEventState)
        Private ReadOnly _database As NodeDatabase
        Private ReadOnly _logger As ILogger(Of EventStore)

        Public Sub New(database As NodeDatabase, logger As ILogger(Of EventStore))
            _database = database
            _logger = logger
            EnsureChatTable()
            EnsureStateTables()
        End Sub

        Private Sub EnsureChatTable()
            Using conn = _database.OpenConnection()
                ' Current schema. CREATE-IF-NOT-EXISTS so a fresh
                ' install starts directly here without going through
                ' the migration ALTERs below.
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "
                        CREATE TABLE IF NOT EXISTS chat_messages (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            instance_id TEXT NOT NULL,
                            timestamp_utc TEXT NOT NULL,
                            display_name TEXT NOT NULL,
                            platform_user_id TEXT,
                            character_id TEXT,
                            text TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS ix_chat_instance_time
                            ON chat_messages(instance_id, timestamp_utc);
                    "
                    cmd.ExecuteNonQuery()
                End Using

                ' Phase 5g-1 migration: pre-5g installs have a
                ' chat_messages.player_name column and no
                ' display_name / platform_user_id / character_id
                ' columns. Each ALTER is wrapped in a try/swallow
                ' because the migrations are idempotent — running
                ' on a fresh install or a previously-migrated DB
                ' errors on "duplicate column" or "no such column",
                ' both expected no-ops rather than failures.
                TryExec(conn, "ALTER TABLE chat_messages RENAME COLUMN player_name TO display_name")
                TryExec(conn, "ALTER TABLE chat_messages ADD COLUMN platform_user_id TEXT")
                TryExec(conn, "ALTER TABLE chat_messages ADD COLUMN character_id TEXT")

                ' Replay-dedup unique index. Originally chat rows
                ' were keyed only by an auto-increment id and timestamped
                ' with DateTime.UtcNow at the moment the tailer
                ' happened to process the line. On adoption with
                ' skipResume=True the tailer re-reads the whole log
                ' file, so every historical chat line came back
                ' through PersistChat with a FRESH UtcNow timestamp
                ' and got inserted as a new row. ProcessLine now
                ' extracts the UE4 log-line timestamp (which is
                ' stable across replays) and PersistChat uses
                ' INSERT OR IGNORE against this index, so a replay
                ' that sees the same (instance, log-time, speaker,
                ' text) tuple silently does nothing instead of
                ' inserting a dupe.
                '
                ' Older rows in the table (inserted before this
                ' fix landed) have UtcNow-based timestamps and
                ' will not conflict with their UE4-timestamped
                ' replays — they're not strictly duplicates by
                ' the index criteria. Leaving them in place rather
                ' than running an automated cleanup, because
                ' "same content, different timestamp" can also be
                ' a legitimate repeated message and we don't want
                ' to silently delete those. See the chat-dedup
                ' note in CHANGELOG / Backlog for a manual cleanup
                ' SQL if the noise actually matters in practice.
                TryExec(conn, "
                    CREATE UNIQUE INDEX IF NOT EXISTS ux_chat_dedup
                        ON chat_messages(instance_id, timestamp_utc, display_name, text)
                ")
            End Using
        End Sub

        Private Sub TryExec(conn As SqliteConnection, sql As String)
            Try
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = sql
                    cmd.ExecuteNonQuery()
                End Using
            Catch ex As Exception
                ' Idempotent migration — duplicate-column on second
                ' run or no-such-column on fresh install are both
                ' expected no-ops, not failures.
            End Try
        End Sub

        ''' <summary>
        ''' Creates the persistent state tables introduced in
        ''' Phase 5g-2: `players` (one row per LO character_id
        ''' ever seen, current names + first/last seen) and
        ''' `instance_state` (one row per instance, match state
        ''' and tile/map fields). Pure CREATE-IF-NOT-EXISTS
        ''' since these are net-new schema additions — no pre-
        ''' existing rows to migrate from.
        '''
        ''' Schema notes:
        '''
        ''' players.character_id is the primary key because
        ''' CharacterId is the stable identity over the LO
        ''' connection lifecycle. PlatformUserId and DisplayName
        ''' both can change across sessions (PlatformUserId is
        ''' stable per Steam/Xbox account but distinct from
        ''' CharacterId on a per-character basis; DisplayName
        ''' changes whenever a player renames their character
        ''' on myrealm), so character_id is what unambiguously
        ''' identifies a single in-game character.
        '''
        ''' Players without a CharacterId yet (partial-event
        ''' state during the first few hundred ms of a session)
        ''' don't get a row — the upsert path skips them. They
        ''' acquire one as soon as a join/identity event
        ''' surfaces their character_id; the in-memory record
        ''' tracks them in the meantime.
        '''
        ''' instance_state.instance_id maps to the manager's
        ''' instance GUID. One row per instance ever tracked;
        ''' upsert-on-event keeps it current. Survives node
        ''' restart so RegisterInstance can rehydrate match
        ''' state and tile binding before log replay catches
        ''' up (or instead of it, when the relevant lines are
        ''' outside the tailer's 512KB backfill window).
        '''
        ''' Phase 5g-1's `known_display_names` /
        ''' `known_platform_personas` JSON-array columns from
        ''' the original plan are NOT included in this phase —
        ''' no current acceptance criterion needs the historical
        ''' name set, and adding them later as ALTER TABLE
        ''' additions follows the same pattern chat_messages
        ''' already established.
        ''' </summary>
        Private Sub EnsureStateTables()
            Using conn = _database.OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "
                        CREATE TABLE IF NOT EXISTS players (
                            character_id             TEXT PRIMARY KEY,
                            platform_user_id         TEXT,
                            platform                 TEXT,
                            current_display_name     TEXT,
                            current_platform_persona TEXT,
                            first_seen_utc           TEXT NOT NULL,
                            last_seen_utc            TEXT NOT NULL,
                            last_tile                TEXT
                        );
                        CREATE INDEX IF NOT EXISTS ix_players_platform_user
                            ON players(platform_user_id);

                        CREATE TABLE IF NOT EXISTS instance_state (
                            instance_id     TEXT PRIMARY KEY,
                            match_state     TEXT,
                            tile_id         TEXT,
                            tile_name       TEXT,
                            map_path        TEXT,
                            updated_at_utc  TEXT NOT NULL
                        );
                    "
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>
        ''' Upserts a player row keyed by character_id. Called
        ''' from ApplyMatch handlers after the in-memory session
        ''' update commits, OUTSIDE state.Lock so DB IO doesn't
        ''' block concurrent ProcessLine readers.
        '''
        ''' COALESCE-style merge: non-null fields from the new
        ''' event overwrite the existing row, null fields
        ''' preserve the existing value. This matters because
        ''' events arrive partial — a chat line carries
        ''' DisplayName but no PlatformUserId, a Login line
        ''' carries PlatformPersona + CharacterId but no
        ''' DisplayName — and we don't want each successive
        ''' partial update to wipe out fields that a prior
        ''' event already populated.
        '''
        ''' last_seen_utc uses MAX() to handle the case where
        ''' two ProcessLine threads commit out of order against
        ''' the same character. SQLite's INSERT OR REPLACE
        ''' would otherwise let the older timestamp win.
        ''' first_seen_utc is set only on insert; the ON
        ''' CONFLICT clause leaves it untouched on update.
        '''
        ''' Skipped silently when characterId is empty — partial
        ''' sessions without a CharacterId yet stay in-memory
        ''' only until they acquire one. last_tile is whatever
        ''' tile the session is currently bound to (captured
        ''' from state.ServerState.TileName by the caller);
        ''' Nothing if the instance hasn't loaded a tile yet.
        ''' </summary>
        Private Sub UpsertPlayerRecord(characterId As String,
                                        platformUserId As String,
                                        platform As String,
                                        displayName As String,
                                        platformPersona As String,
                                        lastTile As String,
                                        timestampUtc As DateTime)
            If String.IsNullOrEmpty(characterId) Then Return
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            INSERT INTO players
                                (character_id, platform_user_id, platform,
                                 current_display_name, current_platform_persona,
                                 first_seen_utc, last_seen_utc, last_tile)
                            VALUES
                                ($cid, $pid, $platform, $display, $persona,
                                 $first, $last, $tile)
                            ON CONFLICT(character_id) DO UPDATE SET
                                platform_user_id         = COALESCE(excluded.platform_user_id, players.platform_user_id),
                                platform                 = COALESCE(excluded.platform, players.platform),
                                current_display_name     = COALESCE(excluded.current_display_name, players.current_display_name),
                                current_platform_persona = COALESCE(excluded.current_platform_persona, players.current_platform_persona),
                                last_seen_utc            = MAX(excluded.last_seen_utc, players.last_seen_utc),
                                last_tile                = COALESCE(excluded.last_tile, players.last_tile)
                        "
                        Dim tsString = timestampUtc.ToString("o")
                        cmd.Parameters.AddWithValue("$cid", characterId)
                        cmd.Parameters.AddWithValue("$pid", If(CObj(platformUserId), DBNull.Value))
                        cmd.Parameters.AddWithValue("$platform", If(CObj(platform), DBNull.Value))
                        cmd.Parameters.AddWithValue("$display", If(CObj(displayName), DBNull.Value))
                        cmd.Parameters.AddWithValue("$persona", If(CObj(platformPersona), DBNull.Value))
                        cmd.Parameters.AddWithValue("$first", tsString)
                        cmd.Parameters.AddWithValue("$last", tsString)
                        cmd.Parameters.AddWithValue("$tile", If(CObj(lastTile), DBNull.Value))
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to upsert player record for character {Cid}", characterId)
            End Try
        End Sub

        ''' <summary>
        ''' Upserts the instance_state row for an instance.
        ''' Writes the full current ServerState surface —
        ''' including nulls when fields have been cleared (e.g.
        ''' tile fields blanked on EnteringMap/LeavingMap
        ''' transitions). NOT COALESCE-merged: the persisted row
        ''' should reflect current state, not a high-water mark.
        ''' </summary>
        Private Sub UpsertInstanceState(instanceId As String,
                                          matchState As String,
                                          tileId As String,
                                          tileName As String,
                                          mapPath As String,
                                          timestampUtc As DateTime)
            If String.IsNullOrEmpty(instanceId) Then Return
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            INSERT INTO instance_state
                                (instance_id, match_state, tile_id, tile_name, map_path, updated_at_utc)
                            VALUES
                                ($id, $match, $tid, $tname, $map, $ts)
                            ON CONFLICT(instance_id) DO UPDATE SET
                                match_state    = excluded.match_state,
                                tile_id        = excluded.tile_id,
                                tile_name      = excluded.tile_name,
                                map_path       = excluded.map_path,
                                updated_at_utc = excluded.updated_at_utc
                        "
                        cmd.Parameters.AddWithValue("$id", instanceId)
                        cmd.Parameters.AddWithValue("$match", If(CObj(matchState), DBNull.Value))
                        cmd.Parameters.AddWithValue("$tid", If(CObj(tileId), DBNull.Value))
                        cmd.Parameters.AddWithValue("$tname", If(CObj(tileName), DBNull.Value))
                        cmd.Parameters.AddWithValue("$map", If(CObj(mapPath), DBNull.Value))
                        cmd.Parameters.AddWithValue("$ts", timestampUtc.ToString("o"))
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to upsert instance_state for {Id}", instanceId)
            End Try
        End Sub

        ''' <summary>
        ''' Reads the persisted instance_state row for an
        ''' instance. Returns Nothing if no row exists (fresh
        ''' install, instance never tracked, etc.). Called from
        ''' RegisterInstance to seed in-memory ServerState so a
        ''' newly-tracked instance starts with its last-known
        ''' state rather than empty fields. Live events
        ''' subsequently override these as they fire.
        '''
        ''' Returns a populated ServerStateResponse-shaped
        ''' struct rather than the raw columns so the caller
        ''' can copy fields one-to-one. BackendRegistered isn't
        ''' persisted (it's a per-session flag that doesn't
        ''' meaningfully survive restart) so the returned row
        ''' leaves it at its default False; the next backend
        ''' registration event will flip it.
        ''' </summary>
        Private Function LoadInstanceState(instanceId As String) As ServerStateResponse
            If String.IsNullOrEmpty(instanceId) Then Return Nothing
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            SELECT match_state, tile_id, tile_name, map_path, updated_at_utc
                            FROM instance_state
                            WHERE instance_id = $id
                        "
                        cmd.Parameters.AddWithValue("$id", instanceId)
                        Using reader = cmd.ExecuteReader()
                            If reader.Read() Then
                                Dim resp As New ServerStateResponse()
                                resp.MatchState = If(reader.IsDBNull(0), Nothing, reader.GetString(0))
                                resp.TileId = If(reader.IsDBNull(1), Nothing, reader.GetString(1))
                                resp.TileName = If(reader.IsDBNull(2), Nothing, reader.GetString(2))
                                resp.CurrentMapPath = If(reader.IsDBNull(3), Nothing, reader.GetString(3))
                                If Not reader.IsDBNull(4) Then
                                    Try
                                        resp.LastUpdatedUtc = DateTime.Parse(
                                            reader.GetString(4),
                                            System.Globalization.CultureInfo.InvariantCulture,
                                            System.Globalization.DateTimeStyles.RoundtripKind)
                                    Catch
                                        ' Leave LastUpdatedUtc at default if the
                                        ' stored timestamp is malformed somehow.
                                    End Try
                                End If
                                Return resp
                            End If
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to load instance_state for {Id}", instanceId)
            End Try
            Return Nothing
        End Function

        ''' <summary>
        ''' Best-effort lookup of a player's last-known
        ''' display name by PlatformUserId. Used by the join-
        ''' time hydration path: when a session gains a
        ''' PlatformUserId via Processing-character-update but
        ''' doesn't yet have a DisplayName from log events, the
        ''' cached name from a prior connection lets the player
        ''' show as their renamed character from the start
        ''' rather than as their Steam handle.
        '''
        ''' Returns Nothing on miss or DB error — callers treat
        ''' that as "no cached name available" and fall back to
        ''' the live identity-resolution path (next chat /
        ''' Persisting tick will populate DisplayName the slow
        ''' way). Cheap single-row indexed lookup via
        ''' ix_players_platform_user; called at most once per
        ''' session (the condition that triggers it fires once,
        ''' when pid is first set).
        ''' </summary>
        Private Function LookupPlayerDisplayName(platformUserId As String) As String
            If String.IsNullOrEmpty(platformUserId) Then Return Nothing
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            SELECT current_display_name FROM players
                            WHERE platform_user_id = $pid
                              AND current_display_name IS NOT NULL
                            LIMIT 1
                        "
                        cmd.Parameters.AddWithValue("$pid", platformUserId)
                        Dim r = cmd.ExecuteScalar()
                        If r IsNot Nothing AndAlso Not (TypeOf r Is DBNull) Then
                            Return r.ToString()
                        End If
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to look up cached display name for platform user {Pid}", platformUserId)
            End Try
            Return Nothing
        End Function

        ''' <summary>
        ''' Captures a session's identity field values under
        ''' state.Lock, then performs the player-row upsert OUTSIDE
        ''' the lock so the DB write doesn't serialise with concurrent
        ''' ProcessLine readers. Lightweight: six string reads under
        ''' the lock, one SQLite UPSERT outside it.
        '''
        ''' Caller-side pattern: do whatever in-memory mutations the
        ''' rule handler needs inside its own SyncLock state.Lock,
        ''' then End SyncLock, then call this with the session
        ''' reference. The helper re-acquires the lock briefly for
        ''' the field snapshot; correctness across the two lock
        ''' acquisitions relies on "last write wins on COALESCE-merged
        ''' fields", which the UpsertPlayerRecord SQL already guarantees.
        '''
        ''' Returns silently for null sessions or sessions without a
        ''' CharacterId — partial-event sessions are in-memory-only
        ''' until they acquire one.
        ''' </summary>
        Private Sub PersistPlayer(state As InstanceEventState,
                                    sess As PlayerSession,
                                    timestampUtc As DateTime)
            If sess Is Nothing Then Return
            Dim cid As String = Nothing
            Dim pid As String = Nothing
            Dim platform As String = Nothing
            Dim display As String = Nothing
            Dim persona As String = Nothing
            Dim tile As String = Nothing
            SyncLock state.Lock
                If String.IsNullOrEmpty(sess.CharacterId) Then Return
                cid = sess.CharacterId
                pid = sess.PlatformUserId
                platform = sess.Platform
                display = sess.DisplayName
                persona = sess.PlatformPersona
                tile = state.ServerState.TileName
            End SyncLock
            UpsertPlayerRecord(cid, pid, platform, display, persona, tile, timestampUtc)
        End Sub

        ''' <summary>
        ''' Captures the current ServerState surface under
        ''' state.Lock, then performs the instance_state upsert
        ''' OUTSIDE the lock. Same pattern as PersistPlayer; same
        ''' rationale (no DB IO blocking ProcessLine readers).
        ''' Writes the full current surface including nulls —
        ''' instance_state is a current-state row, not a high-
        ''' water mark, so cleared fields (tile blanked on
        ''' EnteringMap/LeavingMap) must persist as NULL.
        ''' </summary>
        Private Sub PersistInstanceStateSnapshot(state As InstanceEventState,
                                                   timestampUtc As DateTime)
            Dim ms As String = Nothing
            Dim tid As String = Nothing
            Dim tname As String = Nothing
            Dim mpath As String = Nothing
            SyncLock state.Lock
                ms = state.ServerState.MatchState
                tid = state.ServerState.TileId
                tname = state.ServerState.TileName
                mpath = state.ServerState.CurrentMapPath
            End SyncLock
            UpsertInstanceState(state.InstanceId, ms, tid, tname, mpath, timestampUtc)
        End Sub

        ''' <summary>
        ''' Registers parse rules for an instance. Called when the
        ''' instance is started. Also resets in-memory state for this
        ''' instance since a fresh start means no players online.
        '''
        ''' hydrateState toggles whether the persisted instance_state
        ''' row is loaded into state.ServerState before live events
        ''' start arriving. Default False (fresh start): empty match
        ''' state, no tile bound, no stale fields from a prior run —
        ''' subsequent log events will populate the fields naturally
        ''' as the engine reports them. Adoption path passes True so
        ''' the new node session reflects the prior session's last-
        ''' known state immediately, rather than waiting for the next
        ''' TileLoaded / state-change event to land (which can be
        ''' many minutes on a quiet LO realm). Either way, live
        ''' events override the hydrated fields as they fire.
        ''' </summary>
        Public Sub RegisterInstance(instanceId As String,
                                     rules As IList(Of LogParseRule),
                                     Optional hydrateState As Boolean = False)
            Dim state As New InstanceEventState()
            state.InstanceId = instanceId
            state.CompiledRules = New List(Of CompiledRule)()

            ' Hydration runs BEFORE the rule compile loop so it can't
            ' fail in a way that loses the rule registration. Even if
            ' the DB read throws inside LoadInstanceState (handled
            ' internally with a logged warning + Nothing return), the
            ' rest of RegisterInstance proceeds normally.
            If hydrateState Then
                Dim hydrated = LoadInstanceState(instanceId)
                If hydrated IsNot Nothing Then
                    state.ServerState.MatchState = hydrated.MatchState
                    state.ServerState.TileId = hydrated.TileId
                    state.ServerState.TileName = hydrated.TileName
                    state.ServerState.CurrentMapPath = hydrated.CurrentMapPath
                    state.ServerState.LastUpdatedUtc = hydrated.LastUpdatedUtc
                    _logger.LogInformation(
                        "Hydrated instance_state for {Id}: match={Match}, tile={Tile}",
                        instanceId,
                        If(hydrated.MatchState, "(none)"),
                        If(hydrated.TileName, "(none)"))
                End If
            End If

            If rules IsNot Nothing Then
                For Each rule In rules
                    If String.IsNullOrWhiteSpace(rule.Pattern) Then Continue For
                    Try
                        Dim compiled As New CompiledRule()
                        compiled.Kind = rule.Kind
                        compiled.Regex = New Regex(rule.Pattern,
                            RegexOptions.Compiled Or RegexOptions.CultureInvariant)
                        compiled.Name = rule.Name
                        state.CompiledRules.Add(compiled)
                    Catch ex As Exception
                        _logger.LogWarning(ex, "Failed to compile parse rule {Name}: {Pattern}",
                                           rule.Name, rule.Pattern)
                    End Try
                Next
            End If

            _instances(instanceId) = state
            _logger.LogInformation("Registered {Count} parse rule(s) for {Id}",
                                   state.CompiledRules.Count, instanceId)
        End Sub

        ''' <summary>
        ''' Clears in-memory state for an instance. Called on stop/crash.
        ''' Chat history is preserved in SQLite.
        ''' </summary>
        Public Sub UnregisterInstance(instanceId As String)
            Dim dummy As InstanceEventState = Nothing
            _instances.TryRemove(instanceId, dummy)
        End Sub

        ''' <summary>
        ''' Re-registers parse rules for an instance WITHOUT
        ''' resetting any other in-memory state. Used by the
        ''' Manager to refresh rules on a running instance after a
        ''' node binary update or Manager restart — the previous
        ''' fix required stopping and restarting every game
        ''' instance to re-register rules via StartInstance, which
        ''' kicked all players off. This path swaps CompiledRules
        ''' atomically under state.Lock while leaving Players,
        ''' ServerState, PendingRemoteAddress, and
        ''' PendingIdentitiesByPlatformUserId untouched, so the
        ''' running game continues to be tracked correctly across
        ''' the swap.
        '''
        ''' If no existing state is registered for the instance,
        ''' this method logs a warning and returns without
        ''' creating new state. Auto-registering from a bare rule
        ''' push would skip the process-metadata the node needs
        ''' (working dir, exe path, crash policy) that
        ''' StartInstanceRequest carries — so the caller treats
        ''' "instance not registered" as a soft failure and
        ''' proceeds without a re-register.
        ''' </summary>
        Public Sub UpdateParseRules(instanceId As String, rules As IList(Of LogParseRule))
            Dim state As InstanceEventState = Nothing
            If Not _instances.TryGetValue(instanceId, state) Then
                _logger.LogWarning(
                    "UpdateParseRules called for unregistered instance {Id} — ignored. " &
                    "This is expected if the instance was never started on this node or has been stopped; " &
                    "a fresh StartInstance call will register rules and process metadata together.",
                    instanceId)
                Return
            End If

            ' Compile new rules outside the lock — regex
            ' construction can be slow and there's no need to
            ' block ProcessLine readers during it.
            Dim compiled As New List(Of CompiledRule)()
            If rules IsNot Nothing Then
                For Each rule In rules
                    If String.IsNullOrWhiteSpace(rule.Pattern) Then Continue For
                    Try
                        compiled.Add(New CompiledRule() With {
                            .Kind = rule.Kind,
                            .Regex = New Regex(rule.Pattern,
                                RegexOptions.Compiled Or RegexOptions.CultureInvariant),
                            .Name = rule.Name
                        })
                    Catch ex As Exception
                        _logger.LogWarning(ex, "Failed to compile parse rule {Name}: {Pattern}",
                                           rule.Name, rule.Pattern)
                    End Try
                Next
            End If

            ' Swap atomically under the lock. ProcessLine reads
            ' state.CompiledRules without acquiring the lock, but
            ' the reference assignment itself is atomic on .NET
            ' (pointer-sized write on x64) so an in-flight
            ' ProcessLine either sees the old list or the new
            ' list, never a half-built one.
            SyncLock state.Lock
                state.CompiledRules = compiled
            End SyncLock

            _logger.LogInformation("Updated to {Count} parse rule(s) for {Id} (in-memory state preserved)",
                                   compiled.Count, instanceId)
        End Sub

        ' UE4 log lines are prefixed with `[yyyy.MM.dd-HH.mm.ss:fff]`
        ' in UTC (dedicated server default). Capture group 1 is the
        ' date-time portion; group 2 is the millisecond fractional.
        ' Split into two captures because UE4 separates seconds from
        ' milliseconds with `:` rather than `.`, which DateTime's
        ' parsing can't accommodate with a single format string.
        Private Shared ReadOnly _ue4TimestampRegex As New Regex(
            "^\[(\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}):(\d{3})\]",
            RegexOptions.Compiled Or RegexOptions.CultureInvariant)

        ''' <summary>
        ''' Extracts the UE4 log-line timestamp from the line prefix
        ''' if present. Returns Nothing for lines without the
        ''' bracketed prefix (e.g. plain stdout, non-UE4 games)
        ''' so callers can fall back to the wall-clock time they
        ''' already had.
        '''
        ''' Using the embedded timestamp matters for chat dedup
        ''' across adoption replays: wall-clock changes every time
        ''' the tailer re-reads the file, so a UtcNow-based
        ''' timestamp would produce a fresh "distinct" row for
        ''' every replay of a given chat line. The UE4 timestamp
        ''' is stable — same line, same timestamp, every time.
        ''' </summary>
        Private Shared Function TryParseUe4Timestamp(text As String) As DateTime?
            If String.IsNullOrEmpty(text) Then Return Nothing
            Dim m = _ue4TimestampRegex.Match(text)
            If Not m.Success Then Return Nothing
            Dim combined = m.Groups(1).Value & "." & m.Groups(2).Value
            Dim parsed As DateTime
            If DateTime.TryParseExact(combined,
                                       "yyyy.MM.dd-HH.mm.ss.fff",
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.AssumeUniversal Or
                                       System.Globalization.DateTimeStyles.AdjustToUniversal,
                                       parsed) Then
                Return parsed
            End If
            Return Nothing
        End Function

        ''' <summary>
        ''' Apply all registered rules to a single log line. Called by
        ''' ProcessManager as lines flow through either stdout or tailers.
        ''' </summary>
        Public Sub ProcessLine(instanceId As String, timestampUtc As DateTime, text As String)
            If String.IsNullOrEmpty(text) Then Return
            Dim state As InstanceEventState = Nothing
            If Not _instances.TryGetValue(instanceId, state) Then Return
            If state.CompiledRules Is Nothing OrElse state.CompiledRules.Count = 0 Then Return

            ' Prefer the embedded UE4 timestamp over the wall-clock
            ' time the caller passed in. Wall-clock is "when the
            ' tailer processed this line", which drifts on replay;
            ' the embedded timestamp is "when the event actually
            ' happened", which is stable. Fallback to the caller's
            ' value for non-UE4 lines (stdout, other plugins) so
            ' nothing regresses there.
            Dim eventTime = If(TryParseUe4Timestamp(text), timestampUtc)

            For Each rule In state.CompiledRules
                Dim m = rule.Regex.Match(text)
                If Not m.Success Then Continue For
                Try
                    ApplyMatch(state, rule, m, eventTime, text)
                Catch ex As Exception
                    _logger.LogWarning(ex, "Rule {Name} threw while applying", rule.Name)
                End Try
            Next
        End Sub

        Private Sub ApplyMatch(state As InstanceEventState,
                               rule As CompiledRule,
                               m As Match,
                               timestampUtc As DateTime,
                               rawText As String)
            Select Case rule.Kind

                Case ParsedEventKind.PlayerJoin
                    ' Join events can fire with any of these keys available:
                    '   - CharacterId (from login/join request lines)
                    '   - RemoteAddress (from NotifyAcceptedConnection)
                    '   - PlatformUserId + CharacterId (from persistence lines)
                    '   - PlatformPersona + CharacterId (from login request URL)
                    '   - DisplayName (rare on join — typically arrives later
                    '     via PlayerIdentity)
                    ' The session is created once (first sighting) and then
                    ' progressively enriched by later matches.
                    Dim persona = GetGroup(m, "PlatformPersona")
                    Dim display = GetGroup(m, "DisplayName")
                    Dim cid = GetGroup(m, "CharacterId")
                    Dim addr = GetGroup(m, "RemoteAddress")
                    Dim pid = GetGroup(m, "PlatformUserId")
                    Dim platform = GetGroup(m, "Platform")

                    ' A connection-accept line arrives with JUST RemoteAddress
                    ' and nothing else — don't create a session for it, just
                    ' buffer the IP so the next login line can claim it.
                    If Not String.IsNullOrEmpty(addr) AndAlso
                       String.IsNullOrEmpty(cid) AndAlso
                       String.IsNullOrEmpty(persona) AndAlso
                       String.IsNullOrEmpty(display) AndAlso
                       String.IsNullOrEmpty(pid) Then
                        SyncLock state.Lock
                            state.PendingRemoteAddress = addr
                            state.PendingRemoteAddressStampUtc = timestampUtc
                        End SyncLock
                        Return
                    End If

                    ' Need at least one correlation key to track the session.
                    If String.IsNullOrEmpty(cid) AndAlso
                       String.IsNullOrEmpty(persona) AndAlso
                       String.IsNullOrEmpty(display) AndAlso
                       String.IsNullOrEmpty(pid) Then Return

                    ' sess hoisted to outer scope so the post-SyncLock
                    ' PersistPlayer call can reach it. The reference
                    ' itself is assigned under the lock; PersistPlayer
                    ' re-acquires briefly for the field snapshot read.
                    Dim sess As PlayerSession = Nothing
                    SyncLock state.Lock
                        ' If no IP came with this event, claim the most recent
                        ' pending IP (within 10 seconds of this event). The
                        ' buffer is NOT cleared on claim — within the 10s TTL
                        ' window, multiple PlayerJoin events from the same
                        ' connection can each claim the same addr. This lets
                        ' games that split identity across several log lines
                        ' (e.g. Conan Exiles: NotifyAcceptedConnection for IP,
                        ' Login request for Steam ID, Join succeeded for
                        ' display name) correlate to one session via the
                        ' shared IP key in FindExistingSession. The buffer
                        ' clears naturally when a fresh NotifyAcceptedConnection
                        ' overwrites it for a new connection, or when no
                        ' claimant arrives within the TTL.
                        '
                        ' Multi-connect race in this window: if two NTLM-fast
                        ' joins both buffer their IPs before either Login
                        ' fires, last-write wins for the buffer regardless of
                        ' whether we clear on claim. So the multi-claim
                        ' semantics don't worsen that race; they only help the
                        ' common sequential case.
                        If String.IsNullOrEmpty(addr) AndAlso
                           Not String.IsNullOrEmpty(state.PendingRemoteAddress) AndAlso
                           (timestampUtc - state.PendingRemoteAddressStampUtc).TotalSeconds < 10 Then
                            addr = state.PendingRemoteAddress
                        End If

                        sess = FindOrCreateSession(state, cid, pid, persona, display, addr, timestampUtc)
                        ApplyFields(sess, persona, display, cid, addr, pid, platform)
                        DrainPendingCidIdentity(state, sess)
                        DrainPendingIdentity(state, sess)

                        ' DisplayName hydration: if the session now has
                        ' a PlatformUserId but no DisplayName from log
                        ' events, look up the cached name from a prior
                        ' connection. Returning renamed characters show
                        ' their in-game name from first sighting rather
                        ' than the Steam handle. Best-effort — the live
                        ' identity-resolution path (next chat / next
                        ' Persisting tick) still corrects it if the
                        ' cache is stale or missing.
                        '
                        ' DB read happens INSIDE the lock for code
                        ' simplicity. Acceptable trade-off: the
                        ' condition fires at most once per session
                        ' (only when pid is first set without display),
                        ' and a SQLite indexed lookup is typically
                        ' under 1ms.
                        If sess IsNot Nothing AndAlso
                           Not String.IsNullOrEmpty(sess.PlatformUserId) AndAlso
                           String.IsNullOrEmpty(sess.DisplayName) Then
                            Dim cached = LookupPlayerDisplayName(sess.PlatformUserId)
                            If Not String.IsNullOrEmpty(cached) Then sess.DisplayName = cached
                        End If
                    End SyncLock

                    ' Persist the player row outside the lock so SQLite
                    ' write IO doesn't block concurrent ProcessLine
                    ' readers. PersistPlayer no-ops on null sessions or
                    ' sessions without a CharacterId.
                    PersistPlayer(state, sess, timestampUtc)

                Case ParsedEventKind.PlayerIdentity
                    ' Enrichment-only: does not create a session if none
                    ' exists. The trigger lines (LogPersistence Verbose:
                    ' Processing-character-update and Persisting) fire in
                    ' contexts beyond active player connections — server
                    ' startup loads of every persisted character, autosave
                    ' ticks for offline-but-on-tile characters, world-travel
                    ' arrivals where character data pre-loads ~5s before
                    ' the network handshake. Materialising a session from
                    ' any of these would produce ghost "Unknown" entries
                    ' in the player list for every persisted-but-not-
                    ' connected character. So we enrich-or-stash, never
                    ' create.
                    '
                    ' Two stash paths exist for events that arrive before
                    ' the matching session does, plus a third for Conan's
                    ' session-anonymous character-spawn line:
                    '
                    '   1. Persisting-before-Login (display + pid, no
                    '      cid): stash by PlatformUserId, drained when a
                    '      session gains pid.
                    '
                    '   2. CharacterUpdate-before-Login (cid + pid, no
                    '      display): stash by CharacterId, drained when
                    '      a session is created with the matching cid
                    '      (e.g. by a subsequent Login). This closes the
                    '      world-travel race without materialising a
                    '      ghost session in the player list for the
                    '      window between the two events — or forever,
                    '      if no Login ever arrives.
                    '
                    '   3. Conan character-spawn (cid + display, no pid,
                    '      no IP, no persona): the spawn line carries
                    '      no session-identifying info, so we can't
                    '      match by any key. Two strategies, in order:
                    '      (a) Temporal heuristic via
                    '          TryBindRecentSpawn — if exactly one
                    '          session joined within the last 3
                    '          seconds has no CharacterId, that's
                    '          overwhelmingly likely to be this spawn's
                    '          session, bind directly. Works for the
                    '          typical low-population case.
                    '      (b) Fallback stash by CharacterId. Drains
                    '          when a chat line later binds cid to a
                    '          session via the ChatMessage path — so
                    '          chatty-but-late-bound players still
                    '          land their in-game name. Silent
                    '          players on busy servers (concurrent
                    '          joins, no later chat) stay on the FLS
                    '          handle. Bounded edge case.
                    Dim persona = GetGroup(m, "PlatformPersona")
                    Dim display = GetGroup(m, "DisplayName")
                    Dim cid = GetGroup(m, "CharacterId")
                    Dim pid = GetGroup(m, "PlatformUserId")
                    Dim platform = GetGroup(m, "Platform")
                    Dim addr = GetGroup(m, "RemoteAddress")

                    Dim sess As PlayerSession = Nothing
                    SyncLock state.Lock
                        sess = FindExistingSession(state, cid, pid, persona, display, addr)
                        If sess IsNot Nothing Then
                            ApplyFields(sess, persona, display, cid, addr, pid, platform)
                            DrainPendingCidIdentity(state, sess)
                            DrainPendingIdentity(state, sess)

                            ' DisplayName hydration on PlatformUserId
                            ' first-set. Same rationale as the
                            ' PlayerJoin case — returning renamed
                            ' characters get their cached in-game
                            ' name before the first chat / Persisting
                            ' tick lands.
                            If Not String.IsNullOrEmpty(sess.PlatformUserId) AndAlso
                               String.IsNullOrEmpty(sess.DisplayName) Then
                                Dim cached = LookupPlayerDisplayName(sess.PlatformUserId)
                                If Not String.IsNullOrEmpty(cached) Then sess.DisplayName = cached
                            End If
                        ElseIf Not String.IsNullOrEmpty(pid) AndAlso
                               Not String.Equals(pid, "UNKNOWN", StringComparison.OrdinalIgnoreCase) AndAlso
                               Not String.IsNullOrEmpty(display) Then
                            ' Stash path 1: Persisting-before-Login.
                            state.PendingIdentitiesByPlatformUserId(pid) = New PendingIdentity With {
                                .DisplayName = display,
                                .Platform = platform,
                                .StampUtc = timestampUtc
                            }
                        ElseIf Not String.IsNullOrEmpty(cid) AndAlso
                               Not String.IsNullOrEmpty(pid) AndAlso
                               Not String.Equals(pid, "UNKNOWN", StringComparison.OrdinalIgnoreCase) Then
                            ' Stash path 2: CharacterUpdate-before-Login.
                            ' Stash (pid, platform) under cid so a
                            ' subsequent Login can complete the binding
                            ' via DrainPendingCidIdentity.
                            state.PendingIdentitiesByCharacterId(cid) = New PendingIdentity With {
                                .PlatformUserId = pid,
                                .Platform = platform,
                                .StampUtc = timestampUtc
                            }
                        ElseIf Not String.IsNullOrEmpty(cid) AndAlso
                               Not String.IsNullOrEmpty(display) AndAlso
                               String.IsNullOrEmpty(pid) AndAlso
                               String.IsNullOrEmpty(addr) AndAlso
                               String.IsNullOrEmpty(persona) Then
                            ' Stash path 3: Conan character-spawn line.
                            ' Try temporal binding first; if exactly one
                            ' recently-joined session has no
                            ' CharacterId, bind cid+display to it
                            ' directly. Otherwise stash by cid and hope
                            ' a later chat line will trigger the drain.
                            sess = TryBindRecentSpawn(state, cid, display, timestampUtc)
                            If sess Is Nothing Then
                                state.PendingIdentitiesByCharacterId(cid) = New PendingIdentity With {
                                    .DisplayName = display,
                                    .StampUtc = timestampUtc
                                }
                            End If
                        End If
                    End SyncLock

                    ' Persist the player row outside the lock. Skipped
                    ' silently when sess is Nothing (no existing session
                    ' matched and this was a stash-only path) or when
                    ' sess has no CharacterId yet.
                    PersistPlayer(state, sess, timestampUtc)

                Case ParsedEventKind.PlayerLeave
                    ' Correlate by any key available: CharacterId (strongest),
                    ' then PlatformUserId, then RemoteAddress, then DisplayName,
                    ' then PlatformPersona.
                    Dim persona = GetGroup(m, "PlatformPersona")
                    Dim display = GetGroup(m, "DisplayName")
                    Dim cid = GetGroup(m, "CharacterId")
                    Dim addr = GetGroup(m, "RemoteAddress")
                    Dim pid = GetGroup(m, "PlatformUserId")

                    ' target hoisted to outer scope for the post-
                    ' SyncLock PersistPlayer call — the last upsert
                    ' captures the final last_seen_utc and last_tile
                    ' values for this character before we forget the
                    ' in-memory session.
                    Dim target As PlayerSession = Nothing
                    SyncLock state.Lock
                        target = FindExistingSession(state, cid, pid, persona, display, addr)
                        If target IsNot Nothing Then
                            ' Find the dict key this session is stored under
                            ' and remove it. Sessions may have been rekey'd
                            ' since insertion as identity got enriched; the
                            ' reverse lookup by reference is the reliable way.
                            Dim keyToRemove As String = Nothing
                            For Each kvp In state.Players
                                If kvp.Value Is target Then
                                    keyToRemove = kvp.Key
                                    Exit For
                                End If
                            Next
                            If keyToRemove IsNot Nothing Then
                                state.Players.Remove(keyToRemove)
                            End If
                            ' Drop any pending identity stashes linked to
                            ' this player so they don't accumulate across
                            ' long-running sessions. Both stashes can hold
                            ' entries for this character — pid-keyed from
                            ' a Persisting tick that landed before the
                            ' session resolved pid, and cid-keyed from a
                            ' Processing-character-update that landed
                            ' before Login (typically already drained by
                            ' the time we get to PlayerLeave, but remove
                            ' defensively in case of an unusual ordering).
                            If Not String.IsNullOrEmpty(target.PlatformUserId) Then
                                state.PendingIdentitiesByPlatformUserId.Remove(target.PlatformUserId)
                            End If
                            If Not String.IsNullOrEmpty(target.CharacterId) Then
                                state.PendingIdentitiesByCharacterId.Remove(target.CharacterId)
                            End If
                        End If
                    End SyncLock

                    ' Final upsert outside the lock. The target reference
                    ' is still valid even after dict removal — the
                    ' PlayerSession object hasn't been collected. Skipped
                    ' silently when target is Nothing (correlation
                    ' missed; nothing to persist).
                    PersistPlayer(state, target, timestampUtc)

                Case ParsedEventKind.ChatMessage
                    Dim speaker = GetGroup(m, "DisplayName")
                    Dim msg = GetGroup(m, "Message")
                    If String.IsNullOrEmpty(speaker) OrElse String.IsNullOrEmpty(msg) Then Return

                    ' Pull the stronger correlation keys from the chat
                    ' line itself when the game's chat format provides
                    ' them — Conan's "Character X (uid Y, player Z) said:"
                    ' carries both CharacterId and PlatformUserId, which
                    ' resolve to a unique session even on busy multi-
                    ' player servers where the DisplayName/PlatformPersona
                    ' lookup below would miss (Conan's chat carries the
                    ' in-game character name while the session's
                    ' DisplayName starts out as the Steam handle from
                    ' Join succeeded — they don't match until chat itself
                    ' flips the session's DisplayName, which is a
                    ' chicken-and-egg without these stronger keys).
                    ' Games whose chat lines don't include cid/pid get
                    ' empty strings here; FindExistingSession ignores
                    ' empty keys and falls through to display/persona
                    ' lookup as before.
                    Dim chatCid = GetGroup(m, "CharacterId")
                    Dim chatPid = GetGroup(m, "PlatformUserId")

                    ' Two-purpose handler: enrich the chat row's
                    ' identity columns (CharacterId, PlatformUserId)
                    ' AND apply the speaker back to the session's
                    ' DisplayName. Chat is the most authoritative
                    ' source for "what name this player is going by
                    ' right now in-game" — it's literally the string
                    ' rendered to other players' chat windows.
                    '
                    ' On the post-Phase-5g-1 LO build,
                    ' "Persisting <DisplayName>, UniqueNetId = STEAM:<id>"
                    ' only fires at player departure (~250ms before
                    ' disconnect), which the manager's 3-second
                    ' poll cycle almost always misses. Chat is the
                    ' only path that produces a renamed-name
                    ' binding during play; without writing it back
                    ' here, the player list shows the Steam persona
                    ' for the entire session and only flips to the
                    ' renamed name on departure (if at all).
                    '
                    ' Session lookup:
                    '   1. By CharacterId or PlatformUserId from the
                    '      chat line itself (Conan path).
                    '   2. By DisplayName (LO post-rename) or
                    '      PlatformPersona (Factorio, or pre-rename
                    '      LO where speaker == persona).
                    '   3. Single-player fallback: if exactly one
                    '      session is tracked AND name lookup
                    '      missed, attribute to that session. LO
                    '      chat can only be sent by a player on
                    '      the tile, so a single tracked session
                    '      is the unambiguous speaker. Multi-player
                    '      tiles fall through with no attribution
                    '      — better to miss a binding than guess
                    '      wrong.
                    Dim charId As String = Nothing
                    Dim platUid As String = Nothing
                    Dim sess As PlayerSession = Nothing
                    SyncLock state.Lock
                        sess = FindExistingSession(state, chatCid, chatPid, speaker, speaker, Nothing)
                        If sess Is Nothing AndAlso state.Players.Count = 1 Then
                            sess = state.Players.Values.First()
                        End If
                        If sess IsNot Nothing Then
                            ' Apply the chat-line identity back to the
                            ' session in addition to flipping DisplayName.
                            ' For Conan, this means cid/pid get bound on
                            ' the first chat, closing the gap between
                            ' Join succeeded (Steam handle, no cid) and
                            ' a Persisting tick that may never come.
                            ApplyFields(sess, Nothing, speaker, chatCid, Nothing, chatPid, Nothing)
                            ' Phase 5g-2c — chat is the trigger for
                            ' draining the Conan spawn-line stash for
                            ' players where the temporal heuristic at
                            ' spawn time was ambiguous. The cid the
                            ' chat line just bound to the session may
                            ' match a stash entry; the drain removes
                            ' it (and applies any spawn-time
                            ' DisplayName, though chat's display is
                            ' usually identical and already applied).
                            DrainPendingCidIdentity(state, sess)
                            charId = sess.CharacterId
                            platUid = sess.PlatformUserId
                        End If
                    End SyncLock

                    PersistChat(state.InstanceId, timestampUtc, speaker, platUid, charId, msg)
                    ' Also write the player row — chat is the most
                    ' authoritative DisplayName source on post-5g-1 LO
                    ' builds (Persisting only fires at departure), so
                    ' every chat line is also a name-resolution event
                    ' worth persisting.
                    PersistPlayer(state, sess, timestampUtc)

                Case ParsedEventKind.ServerStateChange
                    SyncLock state.Lock
                        Dim ms = GetGroup(m, "MatchState")
                        If Not String.IsNullOrEmpty(ms) Then
                            state.ServerState.MatchState = ms
                            ' Tile fields are only meaningful while the
                            ' engine is hosting a tile (InProgress /
                            ' WaitingToStart phases). Transitions INTO
                            ' EnteringMap or LeavingMap mean the engine
                            ' has detached from the current tile;
                            ' clear the fields so /server-state stops
                            ' reporting stale data. A subsequent
                            ' TileLoaded event repopulates them when a
                            ' new tile binds.
                            If String.Equals(ms, "EnteringMap", StringComparison.Ordinal) OrElse
                               String.Equals(ms, "LeavingMap", StringComparison.Ordinal) Then
                                state.ServerState.TileId = Nothing
                                state.ServerState.TileName = Nothing
                                state.ServerState.CurrentMapPath = Nothing
                            End If
                        End If
                        Dim reg = GetGroup(m, "Registered")
                        If Not String.IsNullOrEmpty(reg) Then state.ServerState.BackendRegistered = True
                        state.ServerState.LastUpdatedUtc = timestampUtc
                    End SyncLock

                    ' Mirror to instance_state so a node restart
                    ' resurrects the latest match state (and the
                    ' fact that tile fields were cleared, when
                    ' applicable).
                    PersistInstanceStateSnapshot(state, timestampUtc)

                Case ParsedEventKind.TileLoaded
                    SyncLock state.Lock
                        Dim tid = GetGroup(m, "TileId")
                        If Not String.IsNullOrEmpty(tid) Then state.ServerState.TileId = tid
                        Dim tname = GetGroup(m, "TileName")
                        If Not String.IsNullOrEmpty(tname) Then state.ServerState.TileName = tname
                        Dim mpath = GetGroup(m, "MapPath")
                        If Not String.IsNullOrEmpty(mpath) Then state.ServerState.CurrentMapPath = mpath
                        state.ServerState.LastUpdatedUtc = timestampUtc
                    End SyncLock

                    ' Mirror to instance_state so the new tile
                    ' binding survives a node restart.
                    PersistInstanceStateSnapshot(state, timestampUtc)

            End Select

            ' Harvest any "Custom_*" capture groups regardless of rule.Kind —
            ' lets plugins surface game-specific state alongside (or instead of)
            ' the well-known fields driven by the Select Case above.
            HarvestCustomFields(state, m, timestampUtc)
        End Sub

        ''' <summary>
        ''' Persist a chat message. Identity fields (platformUserId,
        ''' characterId) may be Nothing if the speaker's session
        ''' hadn't been identity-resolved at the time the chat line
        ''' fired (the "chat before first Persisting tick" race).
        ''' </summary>
        Private Sub PersistChat(instanceId As String,
                                 timestampUtc As DateTime,
                                 displayName As String,
                                 platformUserId As String,
                                 characterId As String,
                                 text As String)
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        ' INSERT OR IGNORE against ux_chat_dedup
                        ' (instance_id, timestamp_utc, display_name, text).
                        ' Adoption replays produce repeated calls
                        ' with the same UE4 timestamp; this just
                        ' drops them silently. Genuine repeated
                        ' messages with the same text from the
                        ' same player are distinguishable by
                        ' timestamp (different log line, different
                        ' UE4 ms-resolution timestamp) so they
                        ' don't get suppressed.
                        cmd.CommandText = "
                            INSERT OR IGNORE INTO chat_messages
                                (instance_id, timestamp_utc, display_name, platform_user_id, character_id, text)
                            VALUES ($id, $ts, $name, $pid, $cid, $text)
                        "
                        cmd.Parameters.AddWithValue("$id", instanceId)
                        cmd.Parameters.AddWithValue("$ts", timestampUtc.ToString("o"))
                        cmd.Parameters.AddWithValue("$name", displayName)
                        cmd.Parameters.AddWithValue("$pid", If(CObj(platformUserId), DBNull.Value))
                        cmd.Parameters.AddWithValue("$cid", If(CObj(characterId), DBNull.Value))
                        cmd.Parameters.AddWithValue("$text", text)
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to persist chat message")
            End Try
        End Sub

        Private Shared Function GetGroup(m As Match, name As String) As String
            If m Is Nothing Then Return Nothing
            Dim g = m.Groups(name)
            If g Is Nothing OrElse Not g.Success Then Return Nothing
            Return g.Value
        End Function

        ''' <summary>
        ''' Scans named capture groups whose names start with "Custom_"
        ''' and writes the captured values into ServerState.CustomFields
        ''' (with the prefix stripped). Runs after the main Select Case in
        ''' ApplyMatch regardless of rule.Kind, so a single regex can both
        ''' drive a known event kind AND harvest custom state in one pass.
        ''' Plugins that just want to scrape state can use Kind=Custom and
        ''' rely entirely on this method.
        ''' </summary>
        Private Shared Sub HarvestCustomFields(state As InstanceEventState,
                                                m As Match,
                                                timestampUtc As DateTime)
            If m Is Nothing OrElse Not m.Success Then Return

            Dim updates As Dictionary(Of String, String) = Nothing
            For Each groupName In m.Groups.Keys
                If String.IsNullOrEmpty(groupName) Then Continue For
                If Not groupName.StartsWith("Custom_", StringComparison.Ordinal) Then Continue For
                Dim grp = m.Groups(groupName)
                If grp Is Nothing OrElse Not grp.Success Then Continue For
                Dim fieldName = groupName.Substring("Custom_".Length)
                If String.IsNullOrEmpty(fieldName) Then Continue For
                If updates Is Nothing Then
                    updates = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                End If
                updates(fieldName) = grp.Value
            Next

            If updates Is Nothing Then Return

            SyncLock state.Lock
                If state.ServerState.CustomFields Is Nothing Then
                    state.ServerState.CustomFields =
                        New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                End If
                For Each kvp In updates
                    state.ServerState.CustomFields(kvp.Key) = kvp.Value
                Next
                state.ServerState.LastUpdatedUtc = timestampUtc
            End SyncLock
        End Sub

        ' ---- Session correlation helpers ----
        ' Sessions are keyed in the Players dict by whatever identifier
        ' we first saw for the player (preferring CharacterId since it's
        ' the most stable across an LO connection lifecycle). Subsequent
        ' events enrich the same record via these lookups.

        Private Shared Function SessionKeyOf(sess As PlayerSession) As String
            If sess Is Nothing Then Return Nothing
            If Not String.IsNullOrEmpty(sess.CharacterId) Then Return "cid:" & sess.CharacterId
            If Not String.IsNullOrEmpty(sess.PlatformUserId) AndAlso
               Not String.Equals(sess.PlatformUserId, "UNKNOWN", StringComparison.OrdinalIgnoreCase) Then
                Return "pid:" & sess.PlatformUserId
            End If
            If Not String.IsNullOrEmpty(sess.DisplayName) Then Return "dn:" & sess.DisplayName
            If Not String.IsNullOrEmpty(sess.PlatformPersona) Then Return "pp:" & sess.PlatformPersona
            If Not String.IsNullOrEmpty(sess.RemoteAddress) Then Return "ip:" & sess.RemoteAddress
            Return Nothing
        End Function

        Private Shared Function FindExistingSession(state As InstanceEventState,
                                                      cid As String,
                                                      pid As String,
                                                      persona As String,
                                                      display As String,
                                                      addr As String) As PlayerSession
            ' Try each identifier in priority order. First match wins.
            If Not String.IsNullOrEmpty(cid) Then
                Dim m = state.Players.Values.FirstOrDefault(Function(p) p.CharacterId = cid)
                If m IsNot Nothing Then Return m
            End If
            If Not String.IsNullOrEmpty(pid) AndAlso
               Not String.Equals(pid, "UNKNOWN", StringComparison.OrdinalIgnoreCase) Then
                Dim m = state.Players.Values.FirstOrDefault(Function(p) p.PlatformUserId = pid)
                If m IsNot Nothing Then Return m
            End If
            If Not String.IsNullOrEmpty(addr) Then
                Dim m = state.Players.Values.FirstOrDefault(Function(p) p.RemoteAddress = addr)
                If m IsNot Nothing Then Return m
            End If
            If Not String.IsNullOrEmpty(display) Then
                Dim m = state.Players.Values.FirstOrDefault(
                    Function(p) String.Equals(p.DisplayName, display, StringComparison.OrdinalIgnoreCase))
                If m IsNot Nothing Then Return m
            End If
            If Not String.IsNullOrEmpty(persona) Then
                Dim m = state.Players.Values.FirstOrDefault(
                    Function(p) String.Equals(p.PlatformPersona, persona, StringComparison.OrdinalIgnoreCase))
                If m IsNot Nothing Then Return m
            End If
            Return Nothing
        End Function

        Private Shared Function FindOrCreateSession(state As InstanceEventState,
                                                      cid As String,
                                                      pid As String,
                                                      persona As String,
                                                      display As String,
                                                      addr As String,
                                                      timestampUtc As DateTime) As PlayerSession
            Dim existing = FindExistingSession(state, cid, pid, persona, display, addr)
            If existing IsNot Nothing Then Return existing

            Dim sess As New PlayerSession() With {
                .PlatformPersona = persona,
                .DisplayName = display,
                .CharacterId = cid,
                .RemoteAddress = addr,
                .JoinedUtc = timestampUtc
            }
            ' Skip the UNKNOWN placeholder — it'd cause false matches
            ' across sessions if we stored it as the PlatformUserId.
            If Not String.IsNullOrEmpty(pid) AndAlso
               Not String.Equals(pid, "UNKNOWN", StringComparison.OrdinalIgnoreCase) Then
                sess.PlatformUserId = pid
            End If

            Dim key = SessionKeyOf(sess)
            If String.IsNullOrEmpty(key) Then Return sess  ' safety — shouldn't happen
            state.Players(key) = sess

            ' Track the session's dict key by looking it up on demand
            ' via SessionKeyOf — no need to store it on the session
            ' itself. FindExistingSession can locate by any field if
            ' the key changes as identity gets enriched later.
            Return sess
        End Function

        Private Shared Sub ApplyFields(sess As PlayerSession,
                                        persona As String,
                                        display As String,
                                        cid As String,
                                        addr As String,
                                        pid As String,
                                        platform As String)
            If sess Is Nothing Then Return
            If Not String.IsNullOrEmpty(persona) Then sess.PlatformPersona = persona
            If Not String.IsNullOrEmpty(display) Then sess.DisplayName = display
            If Not String.IsNullOrEmpty(cid) Then sess.CharacterId = cid
            If Not String.IsNullOrEmpty(addr) Then sess.RemoteAddress = addr
            If Not String.IsNullOrEmpty(pid) AndAlso
               Not String.Equals(pid, "UNKNOWN", StringComparison.OrdinalIgnoreCase) Then
                sess.PlatformUserId = pid
            End If
            If Not String.IsNullOrEmpty(platform) Then sess.Platform = platform
        End Sub

        ''' <summary>
        ''' If a pending-identity entry exists for this session's
        ''' PlatformUserId, apply its DisplayName/Platform to the
        ''' session and remove it from the stash. Called immediately
        ''' after ApplyFields whenever a session may have just
        ''' gained (or already had) a PlatformUserId — the deferred-
        ''' binding completion point.
        '''
        ''' Caller must hold state.Lock.
        ''' </summary>
        Private Shared Sub DrainPendingIdentity(state As InstanceEventState,
                                                  sess As PlayerSession)
            If sess Is Nothing OrElse String.IsNullOrEmpty(sess.PlatformUserId) Then Return
            Dim pending As PendingIdentity = Nothing
            If Not state.PendingIdentitiesByPlatformUserId.TryGetValue(sess.PlatformUserId, pending) Then Return
            If pending Is Nothing Then Return

            If Not String.IsNullOrEmpty(pending.DisplayName) Then
                sess.DisplayName = pending.DisplayName
            End If
            If Not String.IsNullOrEmpty(pending.Platform) Then
                sess.Platform = pending.Platform
            End If

            state.PendingIdentitiesByPlatformUserId.Remove(sess.PlatformUserId)
        End Sub

        ''' <summary>
        ''' If a cid-keyed pending-identity entry exists for this
        ''' session's CharacterId, apply its PlatformUserId/Platform
        ''' to the session and remove it from the stash. Called
        ''' immediately after ApplyFields whenever a session may
        ''' have just gained (or already had) a CharacterId — the
        ''' world-travel race completion point.
        '''
        ''' Must be called BEFORE DrainPendingIdentity in the
        ''' enrichment flow: this method sets PlatformUserId on the
        ''' session, which DrainPendingIdentity then uses to look
        ''' up the pid-keyed (DisplayName, Platform) stash. Calling
        ''' them in the other order means the pid-keyed lookup
        ''' happens against an empty PlatformUserId field and
        ''' silently no-ops.
        '''
        ''' Won't overwrite an existing PlatformUserId on the
        ''' session — if a session already has pid bound (e.g. from
        ''' an earlier in-flight event), the stash entry is still
        ''' removed (we trust the live binding) but the field is
        ''' left alone.
        '''
        ''' Caller must hold state.Lock.
        ''' </summary>
        Private Shared Sub DrainPendingCidIdentity(state As InstanceEventState,
                                                     sess As PlayerSession)
            If sess Is Nothing OrElse String.IsNullOrEmpty(sess.CharacterId) Then Return
            Dim pending As PendingIdentity = Nothing
            If Not state.PendingIdentitiesByCharacterId.TryGetValue(sess.CharacterId, pending) Then Return
            If pending Is Nothing Then Return

            If Not String.IsNullOrEmpty(pending.PlatformUserId) AndAlso
               String.IsNullOrEmpty(sess.PlatformUserId) Then
                sess.PlatformUserId = pending.PlatformUserId
            End If
            If Not String.IsNullOrEmpty(pending.Platform) AndAlso
               String.IsNullOrEmpty(sess.Platform) Then
                sess.Platform = pending.Platform
            End If
            ' Phase 5g-2c — Conan spawn-line stash path also
            ' carries DisplayName. Apply only when the
            ' session's DisplayName is still empty or holds
            ' the join-time PlatformPersona fallback. If
            ' DisplayName has been set to something else
            ' (typically by a chat line that bound the cid in
            ' the first place — the very event that triggered
            ' this drain), leave it alone: chat is more
            ' authoritative than a possibly-stale spawn entry.
            If Not String.IsNullOrEmpty(pending.DisplayName) AndAlso
               (String.IsNullOrEmpty(sess.DisplayName) OrElse
                String.Equals(sess.DisplayName, sess.PlatformPersona, StringComparison.Ordinal)) Then
                sess.DisplayName = pending.DisplayName
            End If

            state.PendingIdentitiesByCharacterId.Remove(sess.CharacterId)
        End Sub

        ''' <summary>
        ''' Temporal-heuristic binding for Conan's character-spawn
        ''' line ("ConanSandbox: Display: Character ID <n> has
        ''' name <X>..."). The line fires ~100-200ms after Join
        ''' succeeded but carries no session-identifying info —
        ''' no IP, no PlatformUserId, no PlatformPersona — just
        ''' CharacterId + DisplayName.
        '''
        ''' Strategy: if exactly one session joined within the
        ''' last 3 seconds has no CharacterId, that's
        ''' overwhelmingly likely to be the spawn's session.
        ''' Bind cid+display to it and return the bound session.
        ''' If zero or multiple candidates match, return Nothing
        ''' so the caller can fall back to the cid-keyed stash.
        '''
        ''' Returns the bound session, or Nothing if ambiguous.
        ''' Caller must hold state.Lock.
        ''' </summary>
        Private Shared Function TryBindRecentSpawn(state As InstanceEventState,
                                                    cid As String,
                                                    display As String,
                                                    timestampUtc As DateTime) As PlayerSession
            Const RecentJoinWindowSeconds As Double = 3
            Dim candidates As New List(Of PlayerSession)
            For Each sess In state.Players.Values
                If Not String.IsNullOrEmpty(sess.CharacterId) Then Continue For
                Dim age = (timestampUtc - sess.JoinedUtc).TotalSeconds
                If age < 0 OrElse age > RecentJoinWindowSeconds Then Continue For
                candidates.Add(sess)
            Next
            If candidates.Count <> 1 Then Return Nothing

            Dim target = candidates(0)
            target.CharacterId = cid
            ' Apply DisplayName only when it's still empty or
            ' equal to the join-time PlatformPersona fallback.
            ' Returning players hydrated from the persistent
            ' players-table cache may already have a DisplayName;
            ' that cached value is current as of the last
            ' confirmed binding and shouldn't be displaced by a
            ' spawn-line value without evidence of a rename.
            If String.IsNullOrEmpty(target.DisplayName) OrElse
               String.Equals(target.DisplayName, target.PlatformPersona, StringComparison.Ordinal) Then
                target.DisplayName = display
            End If
            Return target
        End Function

        ' ---- Query API (used by endpoints) ----

        Public Function GetPlayers(instanceId As String) As IReadOnlyList(Of PlayerSession)
            Dim state As InstanceEventState = Nothing
            If Not _instances.TryGetValue(instanceId, state) Then
                Return Array.Empty(Of PlayerSession)()
            End If
            SyncLock state.Lock
                Return state.Players.Values.ToList()
            End SyncLock
        End Function

        Public Function GetServerState(instanceId As String) As ServerStateResponse
            Dim state As InstanceEventState = Nothing
            If Not _instances.TryGetValue(instanceId, state) Then
                Return New ServerStateResponse()
            End If
            SyncLock state.Lock
                Dim customCopy As Dictionary(Of String, String) = Nothing
                If state.ServerState.CustomFields IsNot Nothing AndAlso
                   state.ServerState.CustomFields.Count > 0 Then
                    customCopy = New Dictionary(Of String, String)(
                        state.ServerState.CustomFields,
                        StringComparer.OrdinalIgnoreCase)
                End If
                Return New ServerStateResponse() With {
                    .MatchState = state.ServerState.MatchState,
                    .CurrentMapPath = state.ServerState.CurrentMapPath,
                    .TileId = state.ServerState.TileId,
                    .TileName = state.ServerState.TileName,
                    .BackendRegistered = state.ServerState.BackendRegistered,
                    .LastUpdatedUtc = state.ServerState.LastUpdatedUtc,
                    .CustomFields = customCopy
                }
            End SyncLock
        End Function

        Public Function GetChatHistory(instanceId As String,
                                        sinceUtc As DateTime?,
                                        limit As Integer) As IReadOnlyList(Of ChatMessage)
            Dim results As New List(Of ChatMessage)
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        If sinceUtc.HasValue Then
                            cmd.CommandText = "
                                SELECT timestamp_utc, display_name, platform_user_id, character_id, text
                                FROM chat_messages
                                WHERE instance_id = $id AND timestamp_utc > $since
                                ORDER BY timestamp_utc ASC
                                LIMIT $limit
                            "
                            cmd.Parameters.AddWithValue("$since", sinceUtc.Value.ToString("o"))
                        Else
                            cmd.CommandText = "
                                SELECT timestamp_utc, display_name, platform_user_id, character_id, text
                                FROM chat_messages
                                WHERE instance_id = $id
                                ORDER BY timestamp_utc DESC
                                LIMIT $limit
                            "
                        End If
                        cmd.Parameters.AddWithValue("$id", instanceId)
                        cmd.Parameters.AddWithValue("$limit", If(limit > 0, limit, 500))

                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                results.Add(New ChatMessage() With {
                                    .TimestampUtc = DateTime.Parse(reader.GetString(0)).ToUniversalTime(),
                                    .DisplayName = reader.GetString(1),
                                    .PlatformUserId = If(reader.IsDBNull(2), Nothing, reader.GetString(2)),
                                    .CharacterId = If(reader.IsDBNull(3), Nothing, reader.GetString(3)),
                                    .Text = reader.GetString(4)
                                })
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to read chat history for {Id}", instanceId)
            End Try
            ' If we fetched DESC (no since filter) give back ascending for display.
            If Not sinceUtc.HasValue Then results.Reverse()
            Return results
        End Function

    End Class

    ' ============================================================

    Friend Class CompiledRule
        Public Property Kind As ParsedEventKind
        Public Property Regex As Regex
        Public Property Name As String
    End Class

    Friend Class InstanceEventState
        Public Property InstanceId As String
        Public Property CompiledRules As List(Of CompiledRule)
        Public ReadOnly Property Players As New Dictionary(Of String, PlayerSession)(StringComparer.OrdinalIgnoreCase)
        Public ReadOnly Property ServerState As New ServerStateResponse()
        Public ReadOnly Property Lock As New Object()

        ' UE4's connection accept line arrives with an IP but no name/CID;
        ' the login line arrives moments later with name/CID but no IP.
        ' We buffer the IP briefly and claim it on the next login line.
        Public Property PendingRemoteAddress As String
        Public Property PendingRemoteAddressStampUtc As DateTime

        ' Phase 5g-1 deferred-binding stash:
        '   Persisting lines fire with (DisplayName + PlatformUserId)
        '   on every autosave tick. If a Persisting tick arrives
        '   before the session has a known PlatformUserId — i.e.
        '   Processing-character-update hasn't landed yet — we
        '   stash the (DisplayName, Platform) pair here keyed by
        '   PlatformUserId. The next event that resolves a session
        '   to PlatformUserId drains the stash, completing the
        '   DisplayName binding.
        '
        '   Cleared on session leave (see PlayerLeave case in
        '   ApplyMatch) so stale entries don't accumulate across
        '   long-running sessions. Bounded only by the number of
        '   concurrent unresolved-on-arrival players; no TTL since
        '   the leave-cleanup path covers normal lifecycle and
        '   process restart resets the in-memory state entirely.
        Public ReadOnly Property PendingIdentitiesByPlatformUserId As _
            New Dictionary(Of String, PendingIdentity)(StringComparer.OrdinalIgnoreCase)

        ' Phase 5g-2 cid-keyed deferred-binding stash:
        '   Processing-character-update lines fire with
        '   (CharacterId + PlatformUserId) but no DisplayName, and
        '   can arrive BEFORE the Login line on world-travel
        '   arrivals (the server pre-loads character data ~5s
        '   before the network handshake) OR can fire purely for
        '   on-tile persistence work with no associated network
        '   connection at all (server-startup loads of every
        '   persisted character, autosave ticks for offline-but-
        '   on-tile characters).
        '
        '   The corresponding rule is classified as PlayerIdentity
        '   (enrichment-only) for that reason — classifying it as
        '   PlayerJoin would materialise ghost "Unknown" sessions
        '   in the player list for every persisted-but-not-
        '   connected character. Instead the (PlatformUserId,
        '   Platform) pair is stashed here keyed by CharacterId.
        '   When a Login fires for that CharacterId and
        '   FindOrCreateSession creates the new session,
        '   DrainPendingCidIdentity completes the binding by
        '   applying the stashed PlatformUserId to the session,
        '   after which the pid-keyed stash above drains in turn
        '   if a subsequent Persisting tick had already landed.
        '
        '   Phase 5g-2c added a second use of this stash: Conan's
        '   "Character ID <n> has name <X>" spawn line carries
        '   (CharacterId + DisplayName, no pid). When the temporal
        '   heuristic in TryBindRecentSpawn can't disambiguate
        '   (concurrent joins on a busy server), the DisplayName
        '   is stashed here keyed by CharacterId, drained when a
        '   chat line later binds the cid to a session.
        '   DrainPendingCidIdentity applies any non-empty
        '   DisplayName from the pending entry under the same
        '   guard the temporal binder uses (empty or persona).
        '
        '   Cleared on session leave; otherwise bounded by the
        '   number of persisted characters on the tile (typically
        '   < 100).
        Public ReadOnly Property PendingIdentitiesByCharacterId As _
            New Dictionary(Of String, PendingIdentity)(StringComparer.OrdinalIgnoreCase)
    End Class

    Friend Class PendingIdentity
        Public Property DisplayName As String
        Public Property Platform As String
        ' Populated only on cid-keyed stash entries (from
        ' Processing-character-update lines that landed before a
        ' session existed for the CharacterId). Unused on
        ' pid-keyed entries since the dict key IS the
        ' PlatformUserId there.
        Public Property PlatformUserId As String
        Public Property StampUtc As DateTime
    End Class

End Namespace
