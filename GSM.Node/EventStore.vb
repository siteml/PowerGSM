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
        End Sub

        Private Sub EnsureChatTable()
            Using conn = _database.OpenConnection()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "
                        CREATE TABLE IF NOT EXISTS chat_messages (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            instance_id TEXT NOT NULL,
                            timestamp_utc TEXT NOT NULL,
                            player_name TEXT NOT NULL,
                            text TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS ix_chat_instance_time
                            ON chat_messages(instance_id, timestamp_utc);
                    "
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>
        ''' Registers parse rules for an instance. Called when the
        ''' instance is started. Also resets in-memory state for this
        ''' instance since a fresh start means no players online.
        ''' </summary>
        Public Sub RegisterInstance(instanceId As String, rules As IList(Of LogParseRule))
            Dim state As New InstanceEventState()
            state.InstanceId = instanceId
            state.CompiledRules = New List(Of CompiledRule)()

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
        ''' Apply all registered rules to a single log line. Called by
        ''' ProcessManager as lines flow through either stdout or tailers.
        ''' </summary>
        Public Sub ProcessLine(instanceId As String, timestampUtc As DateTime, text As String)
            If String.IsNullOrEmpty(text) Then Return
            Dim state As InstanceEventState = Nothing
            If Not _instances.TryGetValue(instanceId, state) Then Return
            If state.CompiledRules Is Nothing OrElse state.CompiledRules.Count = 0 Then Return

            For Each rule In state.CompiledRules
                Dim m = rule.Regex.Match(text)
                If Not m.Success Then Continue For
                Try
                    ApplyMatch(state, rule, m, timestampUtc, text)
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
                    '   - Name + CharacterId (from login / join succeeded)
                    ' The session is created once (first sighting) and then
                    ' progressively enriched by later matches.
                    Dim name = GetGroup(m, "Name")
                    Dim cid = GetGroup(m, "CharacterId")
                    Dim addr = GetGroup(m, "RemoteAddress")
                    Dim pid = GetGroup(m, "PlatformUserId")
                    Dim platform = GetGroup(m, "Platform")

                    ' A connection-accept line arrives with JUST RemoteAddress
                    ' and nothing else — don't create a session for it, just
                    ' buffer the IP so the next login line can claim it.
                    If Not String.IsNullOrEmpty(addr) AndAlso
                       String.IsNullOrEmpty(cid) AndAlso
                       String.IsNullOrEmpty(name) AndAlso
                       String.IsNullOrEmpty(pid) Then
                        SyncLock state.Lock
                            state.PendingRemoteAddress = addr
                            state.PendingRemoteAddressStampUtc = timestampUtc
                        End SyncLock
                        Return
                    End If

                    ' Need at least one correlation key to track the session.
                    If String.IsNullOrEmpty(cid) AndAlso
                       String.IsNullOrEmpty(name) AndAlso
                       String.IsNullOrEmpty(pid) Then Return

                    SyncLock state.Lock
                        ' If no IP came with this event, claim the most recent
                        ' pending IP (within 10 seconds of this event).
                        If String.IsNullOrEmpty(addr) AndAlso
                           Not String.IsNullOrEmpty(state.PendingRemoteAddress) AndAlso
                           (timestampUtc - state.PendingRemoteAddressStampUtc).TotalSeconds < 10 Then
                            addr = state.PendingRemoteAddress
                            state.PendingRemoteAddress = Nothing
                        End If

                        Dim sess = FindOrCreateSession(state, cid, pid, name, addr, timestampUtc)
                        ApplyFields(sess, name, cid, addr, pid, platform)
                    End SyncLock

                Case ParsedEventKind.PlayerIdentity
                    ' Enrichment-only: does not create a session if none exists
                    ' (otherwise we'd track ghosts for random LogPersistence
                    ' lines that mention SteamIDs of disconnected players).
                    Dim name = GetGroup(m, "Name")
                    Dim cid = GetGroup(m, "CharacterId")
                    Dim pid = GetGroup(m, "PlatformUserId")
                    Dim platform = GetGroup(m, "Platform")
                    Dim addr = GetGroup(m, "RemoteAddress")

                    SyncLock state.Lock
                        Dim sess = FindExistingSession(state, cid, pid, name, addr)
                        If sess IsNot Nothing Then
                            ApplyFields(sess, name, cid, addr, pid, platform)
                        End If
                    End SyncLock

                Case ParsedEventKind.PlayerLeave
                    ' Correlate by any key available: CharacterId (strongest),
                    ' then RemoteAddress, then SteamID, then Name.
                    Dim name = GetGroup(m, "Name")
                    Dim cid = GetGroup(m, "CharacterId")
                    Dim addr = GetGroup(m, "RemoteAddress")
                    Dim pid = GetGroup(m, "PlatformUserId")

                    SyncLock state.Lock
                        Dim target = FindExistingSession(state, cid, pid, name, addr)
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
                        End If
                    End SyncLock

                Case ParsedEventKind.ChatMessage
                    Dim name = GetGroup(m, "Name")
                    Dim msg = GetGroup(m, "Message")
                    If String.IsNullOrEmpty(name) OrElse String.IsNullOrEmpty(msg) Then Return
                    PersistChat(state.InstanceId, timestampUtc, name, msg)

                Case ParsedEventKind.ServerStateChange
                    SyncLock state.Lock
                        Dim ms = GetGroup(m, "MatchState")
                        If Not String.IsNullOrEmpty(ms) Then state.ServerState.MatchState = ms
                        Dim reg = GetGroup(m, "Registered")
                        If Not String.IsNullOrEmpty(reg) Then state.ServerState.BackendRegistered = True
                        state.ServerState.LastUpdatedUtc = timestampUtc
                    End SyncLock

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

            End Select

            ' Harvest any "Custom_*" capture groups regardless of rule.Kind —
            ' lets plugins surface game-specific state alongside (or instead of)
            ' the well-known fields driven by the Select Case above.
            HarvestCustomFields(state, m, timestampUtc)
        End Sub

        Private Sub PersistChat(instanceId As String,
                                 timestampUtc As DateTime,
                                 playerName As String,
                                 text As String)
            Try
                Using conn = _database.OpenConnection()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "
                            INSERT INTO chat_messages (instance_id, timestamp_utc, player_name, text)
                            VALUES ($id, $ts, $name, $text)
                        "
                        cmd.Parameters.AddWithValue("$id", instanceId)
                        cmd.Parameters.AddWithValue("$ts", timestampUtc.ToString("o"))
                        cmd.Parameters.AddWithValue("$name", playerName)
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
            If Not String.IsNullOrEmpty(sess.Name) Then Return "nm:" & sess.Name
            If Not String.IsNullOrEmpty(sess.RemoteAddress) Then Return "ip:" & sess.RemoteAddress
            Return Nothing
        End Function

        Private Shared Function FindExistingSession(state As InstanceEventState,
                                                      cid As String,
                                                      pid As String,
                                                      name As String,
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
            If Not String.IsNullOrEmpty(name) Then
                Dim m = state.Players.Values.FirstOrDefault(
                    Function(p) String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                If m IsNot Nothing Then Return m
            End If
            Return Nothing
        End Function

        Private Shared Function FindOrCreateSession(state As InstanceEventState,
                                                      cid As String,
                                                      pid As String,
                                                      name As String,
                                                      addr As String,
                                                      timestampUtc As DateTime) As PlayerSession
            Dim existing = FindExistingSession(state, cid, pid, name, addr)
            If existing IsNot Nothing Then Return existing

            Dim sess As New PlayerSession() With {
                .Name = name,
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

            ' Track the session's dict key so FindExistingSession can
            ' re-key if we learn a stronger identifier later. We stash
            ' the key by looking it up on demand via SessionKeyOf — no
            ' need to store it on the session itself.
            Return sess
        End Function

        Private Shared Sub ApplyFields(sess As PlayerSession,
                                        name As String,
                                        cid As String,
                                        addr As String,
                                        pid As String,
                                        platform As String)
            If sess Is Nothing Then Return
            If Not String.IsNullOrEmpty(name) Then sess.Name = name
            If Not String.IsNullOrEmpty(cid) Then sess.CharacterId = cid
            If Not String.IsNullOrEmpty(addr) Then sess.RemoteAddress = addr
            If Not String.IsNullOrEmpty(pid) AndAlso
               Not String.Equals(pid, "UNKNOWN", StringComparison.OrdinalIgnoreCase) Then
                sess.PlatformUserId = pid
            End If
            If Not String.IsNullOrEmpty(platform) Then sess.Platform = platform
        End Sub

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
                                SELECT timestamp_utc, player_name, text
                                FROM chat_messages
                                WHERE instance_id = $id AND timestamp_utc > $since
                                ORDER BY timestamp_utc ASC
                                LIMIT $limit
                            "
                            cmd.Parameters.AddWithValue("$since", sinceUtc.Value.ToString("o"))
                        Else
                            cmd.CommandText = "
                                SELECT timestamp_utc, player_name, text
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
                                    .PlayerName = reader.GetString(1),
                                    .Text = reader.GetString(2)
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
    End Class

End Namespace