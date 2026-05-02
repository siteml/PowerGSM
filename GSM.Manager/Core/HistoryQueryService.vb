Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data

' ============================================================
'  HistoryQueryService — read-only query surface for the
'  History window. Owns all EF queries against the session
'  tables (ChatMessages, PlayerActivity, SessionHosts,
'  PlayerSessions) so the UI layer doesn't need to know EF
'  or session-identity semantics.
'
'  Two primary operations:
'    - QueryTimelineAsync: range query returning chat +
'      activity rows merged into a single chronological list.
'      Used when the user specifies both start and end times.
'    - QuerySnapshotAsync: presence-at-instant query returning
'      the set of players online at a specific moment, derived
'      by replaying the activity event stream up to that time.
'      Used when the user disables the end-time gate.
'
'  Plus metadata lookups for populating filter dropdowns.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>
    ''' Filter criteria for history queries. StartUtc is required;
    ''' when EndUtc is Nothing the caller is in snapshot mode and
    ''' StartUtc is treated as the instant to evaluate presence at.
    ''' PlayerNamePattern and ChatTextPattern are substring matches
    ''' (case-insensitive). SessionIdentity of Nothing means
    ''' "any session".
    ''' </summary>
    Public Class HistoryFilter
        Public Property StartUtc As DateTime
        Public Property EndUtc As DateTime?
        Public Property SessionIdentity As String
        Public Property PlayerNamePattern As String
        Public Property ChatTextPattern As String
        Public Property IncludeChat As Boolean = True
        Public Property IncludeJoins As Boolean = True
        Public Property IncludeLeaves As Boolean = True

        ''' <summary>
        ''' Convenience: True when EndUtc is null → caller wants
        ''' snapshot-at-instant semantics, not a range query.
        ''' </summary>
        Public ReadOnly Property IsSnapshot As Boolean
            Get
                Return Not EndUtc.HasValue
            End Get
        End Property
    End Class

    ''' <summary>
    ''' A single merged row in the timeline: can be a chat
    ''' message, a join, or a leave. Kind discriminates; Text is
    ''' the message body for chat or Nothing for activity.
    ''' </summary>
    Public Class TimelineRow
        Public Enum RowKind
            Chat
            Join
            Leave
        End Enum

        Public Property Kind As RowKind
        Public Property TimestampUtc As DateTime
        Public Property SessionIdentity As String
        Public Property TileDisplayName As String
        Public Property InstanceId As String
        Public Property PlayerName As String
        Public Property Text As String
    End Class

    ''' <summary>
    ''' One player present at the snapshot instant, with optional
    ''' most-recent chat message (up to that instant) for context.
    ''' </summary>
    Public Class SnapshotRow
        Public Property PlayerName As String
        Public Property JoinedAtUtc As DateTime
        Public Property SessionIdentity As String
        Public Property TileDisplayName As String
        Public Property LastChatText As String
        Public Property LastChatTimeUtc As DateTime?
    End Class

    ''' <summary>
    ''' Wraps a timeline result so the caller can show truncation
    ''' warnings when the limit was reached.
    ''' </summary>
    Public Class TimelineResult
        Public Property Rows As IReadOnlyList(Of TimelineRow)
        Public Property Truncated As Boolean
        Public Property Limit As Integer
    End Class

    ''' <summary>
    ''' Lightweight summary of a session for populating the
    ''' session filter dropdown. DisplayLabel is what the user
    ''' sees; Identity is what gets passed back into HistoryFilter.
    ''' </summary>
    Public Class SessionSummary
        Public Property Identity As String
        Public Property DisplayLabel As String
        ''' <summary>Most recent observation of any kind for sort
        ''' purposes — freshest sessions at the top of the dropdown.</summary>
        Public Property LastActivityUtc As DateTime
    End Class

    Public Class HistoryQueryService

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger(Of HistoryQueryService)

        ' Cap on timeline result size. Chosen so that a WinForms
        ' ListView can still paint without visible lag; narrowing
        ' filters is the user's responsibility past this.
        Public Const TimelineRowLimit As Integer = 5000

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of HistoryQueryService))
            _serviceProvider = serviceProvider
            _logger = logger
        End Sub

        ' ============================================================
        '  Metadata: sessions and player names
        ' ============================================================

        ''' <summary>
        ''' Enumerate every session identity that has EVER produced
        ''' a chat, activity, or host row. Returns display-ready
        ''' summaries sorted by most-recently-active first.
        ''' </summary>
        Public Async Function GetKnownSessionsAsync() As Task(Of IReadOnlyList(Of SessionSummary))
            Return Await Task.Run(Function() LoadKnownSessions())
        End Function

        Private Function LoadKnownSessions() As IReadOnlyList(Of SessionSummary)
            Using scope = _serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                ' Collect from the three tables that carry a
                ' SessionIdentity column. Each gives us the "latest
                ' activity" per identity; merge and take max.
                Dim fromChat = db.ChatMessages.
                    GroupBy(Function(c) c.SessionIdentity).
                    Select(Function(g) New With {
                        Key .Identity = g.Key,
                        .Last = g.Max(Function(c) c.TimestampUtc)
                    }).ToList()

                Dim fromActivity = db.PlayerActivity.
                    GroupBy(Function(a) a.SessionIdentity).
                    Select(Function(g) New With {
                        Key .Identity = g.Key,
                        .Last = g.Max(Function(a) a.TimestampUtc)
                    }).ToList()

                ' SessionHosts also tracks identity but doesn't
                ' have a single "last" timestamp — use HostedFromUtc
                ' as the observation time (when it opened).
                Dim fromHosts = db.SessionHosts.
                    GroupBy(Function(h) h.SessionIdentity).
                    Select(Function(g) New With {
                        Key .Identity = g.Key,
                        .Last = g.Max(Function(h) h.HostedFromUtc)
                    }).ToList()

                ' Merge. Dictionary keeps the max across sources.
                Dim merged As New Dictionary(Of String, DateTime)(StringComparer.Ordinal)
                For Each row In fromChat.Concat(fromActivity).Concat(fromHosts)
                    Dim existing As DateTime
                    If merged.TryGetValue(row.Identity, existing) Then
                        If row.Last > existing Then merged(row.Identity) = row.Last
                    Else
                        merged(row.Identity) = row.Last
                    End If
                Next

                ' Tile names come from SessionHosts. Load latest
                ' name per identity in one query.
                Dim nameByIdentity = db.SessionHosts.
                    Where(Function(h) h.TileName IsNot Nothing).
                    GroupBy(Function(h) h.SessionIdentity).
                    Select(Function(g) New With {
                        Key .Identity = g.Key,
                        .Name = g.OrderByDescending(Function(h) h.HostedFromUtc).
                                  Select(Function(h) h.TileName).
                                  First()
                    }).ToDictionary(Function(x) x.Identity, Function(x) x.Name)

                Dim result As New List(Of SessionSummary)(merged.Count)
                For Each kvp In merged
                    Dim tileName As String = Nothing
                    nameByIdentity.TryGetValue(kvp.Key, tileName)
                    result.Add(New SessionSummary With {
                        .Identity = kvp.Key,
                        .DisplayLabel = FormatSessionLabel(kvp.Key, tileName),
                        .LastActivityUtc = kvp.Value
                    })
                Next

                Return result.OrderByDescending(Function(s) s.LastActivityUtc).ToList()
            End Using
        End Function

        ''' <summary>
        ''' Distinct player names known to the history tables.
        ''' Optionally scoped to a single session identity; pass
        ''' Nothing to return names across all sessions. Used to
        ''' populate the History window's player filter combo.
        ''' </summary>
        Public Async Function GetKnownPlayerNamesAsync(
                sessionIdentity As String) As Task(Of IReadOnlyList(Of String))
            Return Await Task.Run(Function() LoadKnownPlayerNames(sessionIdentity))
        End Function

        Private Function LoadKnownPlayerNames(sessionIdentity As String) As IReadOnlyList(Of String)
            Using scope = _serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                Dim activityQ = db.PlayerActivity.AsQueryable()
                Dim chatQ = db.ChatMessages.AsQueryable()
                If Not String.IsNullOrEmpty(sessionIdentity) Then
                    activityQ = activityQ.Where(Function(a) a.SessionIdentity = sessionIdentity)
                    chatQ = chatQ.Where(Function(c) c.SessionIdentity = sessionIdentity)
                End If

                Dim fromActivity = activityQ.
                    Select(Function(a) a.PlayerName).Distinct().ToList()
                Dim fromChat = chatQ.
                    Where(Function(c) c.PlayerName IsNot Nothing).
                    Select(Function(c) c.PlayerName).Distinct().ToList()

                Return fromActivity.Concat(fromChat).
                    Where(Function(n) Not String.IsNullOrEmpty(n)).
                    Distinct(StringComparer.OrdinalIgnoreCase).
                    OrderBy(Function(n) n, StringComparer.OrdinalIgnoreCase).
                    ToList()
            End Using
        End Function

        ' ============================================================
        '  Timeline (range) query
        ' ============================================================

        ''' <summary>
        ''' Range query: returns chat + activity rows matching the
        ''' filter, merged into a single chronological list sorted
        ''' newest-first. Capped at TimelineRowLimit — .Truncated on
        ''' the result indicates the cap was hit.
        ''' </summary>
        Public Async Function QueryTimelineAsync(filter As HistoryFilter,
                                                  token As CancellationToken) As Task(Of TimelineResult)
            Return Await Task.Run(Function() LoadTimeline(filter, token), token)
        End Function

        Private Function LoadTimeline(filter As HistoryFilter,
                                       token As CancellationToken) As TimelineResult
            If filter Is Nothing Then
                Return New TimelineResult With {
                    .Rows = New List(Of TimelineRow),
                    .Truncated = False,
                    .Limit = TimelineRowLimit
                }
            End If

            Dim endUtc = filter.EndUtc.GetValueOrDefault(DateTime.UtcNow)
            Dim merged As New List(Of TimelineRow)

            Using scope = _serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                ' Resolve tile-name map once for the whole query
                ' so we don't do per-row lookups.
                Dim tileNames = LoadTileNameMap(db, filter.SessionIdentity)

                ' ---- Chat slice ----
                If filter.IncludeChat Then
                    token.ThrowIfCancellationRequested()
                    Dim q = db.ChatMessages.AsQueryable().
                        Where(Function(c) c.TimestampUtc >= filter.StartUtc AndAlso
                                           c.TimestampUtc <= endUtc)

                    If Not String.IsNullOrEmpty(filter.SessionIdentity) Then
                        q = q.Where(Function(c) c.SessionIdentity = filter.SessionIdentity)
                    End If
                    If Not String.IsNullOrEmpty(filter.PlayerNamePattern) Then
                        Dim p = filter.PlayerNamePattern
                        q = q.Where(Function(c) c.PlayerName IsNot Nothing AndAlso
                                                  EF.Functions.Like(c.PlayerName, $"%{p}%"))
                    End If
                    If Not String.IsNullOrEmpty(filter.ChatTextPattern) Then
                        Dim p = filter.ChatTextPattern
                        q = q.Where(Function(c) c.Text IsNot Nothing AndAlso
                                                  EF.Functions.Like(c.Text, $"%{p}%"))
                    End If

                    ' Fetch one past the limit to detect truncation
                    ' without two round trips.
                    Dim chatRows = q.OrderByDescending(Function(c) c.TimestampUtc).
                        Take(TimelineRowLimit + 1).ToList()
                    For Each r In chatRows
                        merged.Add(New TimelineRow With {
                            .Kind = TimelineRow.RowKind.Chat,
                            .TimestampUtc = r.TimestampUtc,
                            .SessionIdentity = r.SessionIdentity,
                            .TileDisplayName = ResolveDisplayName(tileNames, r.SessionIdentity),
                            .InstanceId = r.InstanceId,
                            .PlayerName = r.PlayerName,
                            .Text = r.Text
                        })
                    Next
                End If

                ' ---- Activity slice ----
                If filter.IncludeJoins OrElse filter.IncludeLeaves Then
                    token.ThrowIfCancellationRequested()
                    Dim q = db.PlayerActivity.AsQueryable().
                        Where(Function(a) a.TimestampUtc >= filter.StartUtc AndAlso
                                           a.TimestampUtc <= endUtc)

                    If Not filter.IncludeJoins Then
                        q = q.Where(Function(a) a.EventKind <> "join")
                    End If
                    If Not filter.IncludeLeaves Then
                        q = q.Where(Function(a) a.EventKind <> "leave")
                    End If
                    If Not String.IsNullOrEmpty(filter.SessionIdentity) Then
                        q = q.Where(Function(a) a.SessionIdentity = filter.SessionIdentity)
                    End If
                    If Not String.IsNullOrEmpty(filter.PlayerNamePattern) Then
                        Dim p = filter.PlayerNamePattern
                        q = q.Where(Function(a) EF.Functions.Like(a.PlayerName, $"%{p}%"))
                    End If

                    Dim actRows = q.OrderByDescending(Function(a) a.TimestampUtc).
                        Take(TimelineRowLimit + 1).ToList()
                    For Each r In actRows
                        merged.Add(New TimelineRow With {
                            .Kind = If(r.EventKind = "join",
                                        TimelineRow.RowKind.Join,
                                        TimelineRow.RowKind.Leave),
                            .TimestampUtc = r.TimestampUtc,
                            .SessionIdentity = r.SessionIdentity,
                            .TileDisplayName = ResolveDisplayName(tileNames, r.SessionIdentity),
                            .InstanceId = r.InstanceId,
                            .PlayerName = r.PlayerName,
                            .Text = Nothing
                        })
                    Next
                End If
            End Using

            ' Merge-sort by time desc. Two already-sorted inputs,
            ' but concatenate-then-sort is cheap at these sizes and
            ' simpler than writing a dedicated merge.
            merged.Sort(Function(a, b) b.TimestampUtc.CompareTo(a.TimestampUtc))

            Dim truncated = merged.Count > TimelineRowLimit
            If truncated Then merged = merged.Take(TimelineRowLimit).ToList()

            Return New TimelineResult With {
                .Rows = merged,
                .Truncated = truncated,
                .Limit = TimelineRowLimit
            }
        End Function

        ' ============================================================
        '  Snapshot (presence-at-instant) query
        ' ============================================================

        ''' <summary>
        ''' Snapshot query: who was online at filter.StartUtc?
        ''' Derived by replaying PlayerActivity events in order up
        ''' to that instant. Joins add to the set; leaves remove.
        ''' Whoever's still present at the end IS present at the
        ''' instant. Includes each player's most recent chat
        ''' message up to the instant, for context.
        '''
        ''' filter.EndUtc is ignored (snapshot semantics use only
        ''' StartUtc as the instant). All other filters apply.
        ''' </summary>
        Public Async Function QuerySnapshotAsync(filter As HistoryFilter,
                                                  token As CancellationToken) As Task(Of IReadOnlyList(Of SnapshotRow))
            Return Await Task.Run(Function() LoadSnapshot(filter, token), token)
        End Function

        Private Function LoadSnapshot(filter As HistoryFilter,
                                       token As CancellationToken) As IReadOnlyList(Of SnapshotRow)
            If filter Is Nothing Then Return New List(Of SnapshotRow)

            Dim instant = filter.StartUtc

            Using scope = _serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                ' Load activity events up to the instant, session-
                ' filtered if requested. Sort ascending so replay
                ' order is deterministic.
                Dim q = db.PlayerActivity.AsQueryable().
                    Where(Function(a) a.TimestampUtc <= instant)

                If Not String.IsNullOrEmpty(filter.SessionIdentity) Then
                    q = q.Where(Function(a) a.SessionIdentity = filter.SessionIdentity)
                End If
                If Not String.IsNullOrEmpty(filter.PlayerNamePattern) Then
                    Dim p = filter.PlayerNamePattern
                    q = q.Where(Function(a) EF.Functions.Like(a.PlayerName, $"%{p}%"))
                End If

                Dim events = q.OrderBy(Function(a) a.TimestampUtc).ToList()
                token.ThrowIfCancellationRequested()

                ' Replay. Key: (session, name) so multiple realms
                ' don't collapse the same name across tiles.
                Dim present As New Dictionary(Of String, (SessionIdentity As String, PlayerName As String, JoinedUtc As DateTime))
                For Each ev In events
                    Dim key = ev.SessionIdentity & "|" & ev.PlayerName
                    If ev.EventKind = "join" Then
                        present(key) = (ev.SessionIdentity, ev.PlayerName, ev.TimestampUtc)
                    ElseIf ev.EventKind = "leave" Then
                        present.Remove(key)
                    End If
                Next

                If present.Count = 0 Then Return New List(Of SnapshotRow)

                Dim tileNames = LoadTileNameMap(db, filter.SessionIdentity)

                ' For each present player, fetch the most recent
                ' chat message at-or-before the instant. One query
                ' per player is fine at the sizes we expect
                ' (presence count is bounded by active players).
                Dim result As New List(Of SnapshotRow)(present.Count)
                For Each kvp In present
                    token.ThrowIfCancellationRequested()
                    Dim info = kvp.Value
                    Dim lastChat = db.ChatMessages.
                        Where(Function(c) c.SessionIdentity = info.SessionIdentity AndAlso
                                           c.PlayerName = info.PlayerName AndAlso
                                           c.TimestampUtc <= instant).
                        OrderByDescending(Function(c) c.TimestampUtc).
                        FirstOrDefault()

                    result.Add(New SnapshotRow With {
                        .PlayerName = info.PlayerName,
                        .JoinedAtUtc = info.JoinedUtc,
                        .SessionIdentity = info.SessionIdentity,
                        .TileDisplayName = ResolveDisplayName(tileNames, info.SessionIdentity),
                        .LastChatText = If(lastChat IsNot Nothing, lastChat.Text, Nothing),
                        .LastChatTimeUtc = If(lastChat IsNot Nothing,
                                              CType(lastChat.TimestampUtc, DateTime?),
                                              Nothing)
                    })
                Next

                Return result.OrderBy(Function(r) r.PlayerName,
                                       StringComparer.OrdinalIgnoreCase).ToList()
            End Using
        End Function

        ' ============================================================
        '  Display helpers
        ' ============================================================

        ''' <summary>
        ''' Build a "best display name known for session X" map in
        ''' one query. Used by both timeline and snapshot paths.
        ''' If an identity filter is set, we can skip the rest.
        ''' </summary>
        Private Function LoadTileNameMap(db As GsmDbContext,
                                          filterIdentity As String) As Dictionary(Of String, String)
            Dim q = db.SessionHosts.AsQueryable().
                Where(Function(h) h.TileName IsNot Nothing)
            If Not String.IsNullOrEmpty(filterIdentity) Then
                q = q.Where(Function(h) h.SessionIdentity = filterIdentity)
            End If
            Return q.GroupBy(Function(h) h.SessionIdentity).
                Select(Function(g) New With {
                    Key .Identity = g.Key,
                    .Name = g.OrderByDescending(Function(h) h.HostedFromUtc).
                              Select(Function(h) h.TileName).First()
                }).ToDictionary(Function(x) x.Identity, Function(x) x.Name)
        End Function

        Private Shared Function ResolveDisplayName(
                tileNames As Dictionary(Of String, String),
                sessionIdentity As String) As String
            Dim name As String = Nothing
            If tileNames IsNot Nothing Then tileNames.TryGetValue(sessionIdentity, name)
            Return FormatSessionLabel(sessionIdentity, name)
        End Function

        ''' <summary>
        ''' Turn a raw session identity + optional tile name into
        ''' a display string. Last Oasis: "Forested Wetlands — realm 281474…".
        ''' Factorio or anything else without a resolvable tile name:
        ''' shows the identity as-is. Nothing in → empty string out.
        ''' </summary>
        Public Shared Function FormatSessionLabel(sessionIdentity As String,
                                                   tileName As String) As String
            If String.IsNullOrEmpty(sessionIdentity) Then Return ""

            ' Last Oasis format is "lastoasis:{realm_id}:{tile_id}".
            If sessionIdentity.StartsWith("lastoasis:", StringComparison.Ordinal) Then
                Dim parts = sessionIdentity.Split(":"c)
                If parts.Length >= 3 Then
                    Dim realmId = parts(1)
                    Dim realmShort = If(realmId.Length > 8, realmId.Substring(0, 8) & "…", realmId)
                    If Not String.IsNullOrEmpty(tileName) Then
                        Return $"{tileName} — realm {realmShort}"
                    End If
                    Return $"Tile {parts(2)} — realm {realmShort}"
                End If
            End If

            ' Fallback: raw identity.
            Return sessionIdentity
        End Function

    End Class

End Namespace
