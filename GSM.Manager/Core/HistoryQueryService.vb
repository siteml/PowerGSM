Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data
Imports GSM.Plugin
Imports GSM.Node.Api

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

        ''' <summary>
        ''' Combined "NodeName:InstanceName:InstanceId" string
        ''' resolved once per query by joining the row's InstanceId
        ''' against Instances + Installations + Nodes. The full
        ''' InstanceId is preserved (not truncated) because LO writes
        ''' per-instance log files as {InstanceId}.log — having the
        ''' raw GUID visible lets an operator grep the on-disk log
        ''' for the exact line that produced a given history row.
        ''' Empty when the instance or its node has since been
        ''' deleted, or when the row's InstanceId is itself empty.
        ''' </summary>
        Public Property InstanceDisplay As String

        ''' <summary>
        ''' Display name for the row, resolved via IdentityFormatter
        ''' so the same player renders identically across Chat and
        ''' Join/Leave rows. Coalesce priority: DisplayName (the
        ''' player's chosen in-game character name) → PlayerName
        ''' (raw parser verdict from the underlying entity row).
        '''
        ''' For Chat rows the value is ChatMessages.DisplayName
        ''' directly — chat lines on Last Oasis carry the in-game
        ''' character name natively, so it's already canonical. For
        ''' Join/Leave rows the value is IdentityFormatter.Format(
        ''' activity.DisplayName, Nothing, activity.PlayerName),
        ''' preferring the snapshot DisplayName column that Phase
        ''' 5g-2 added to PlayerActivity and falling back to the
        ''' raw PlayerName (Steam persona on LO, FLS handle on
        ''' Conan) for rows that pre-date the 5g-2 migration or
        ''' where identity enrichment missed at write time.
        ''' PlayerLeave rows in particular tend to miss enrichment
        ''' because the Node removes the session from /players on
        ''' the same log line the Manager processes — see
        ''' InstanceManager.PersistPlayerObservationAsync.
        '''
        ''' Phase 5g-2b render-time chat fallback: for Join/Leave
        ''' rows where DisplayName was empty (or equal to the raw
        ''' PlayerName) AND PlatformUserId is populated,
        ''' LoadTimeline does a render-time lookup against
        ''' ChatMessages by (SessionIdentity, PlatformUserId) and
        ''' overrides PlayerName with the most recent chat
        ''' DisplayName found. Handles the Conan case where the
        ''' FLS handle binds at join before any chat lands, and
        ''' the cross-Node case where a returning player joins on
        ''' a Node whose players-table cache doesn't have them.
        ''' Players who never chatted within the queried scope
        ''' fall through to the raw parser PlayerName.
        ''' </summary>
        Public Property PlayerName As String

        ''' <summary>
        ''' Resolved Steam/Xbox/etc. platform ID of the actor for
        ''' this row. Populated for both Chat and Join/Leave rows
        ''' as of Phase 5g-2 (the activity-row populator captures
        ''' it from the Node's /players response at write time;
        ''' the chat-row populator carries the value chat
        ''' persistence on the Node side bound during parse).
        ''' Nothing for any row whose underlying entity row pre-
        ''' dates the corresponding migration, or where the Node
        ''' couldn't resolve PlatformUserId at the time the line
        ''' was parsed.
        ''' </summary>
        Public Property PlatformUserId As String

        ''' <summary>
        ''' Resolved CharacterId of the actor for this row. Same
        ''' populated-for-both-kinds and Nothing semantics as
        ''' PlatformUserId since Phase 5g-2. Stable across the
        ''' character's lifetime; the durable identity for
        ''' cross-name-change queries (e.g. "every event ever
        ''' from this character regardless of what name they
        ''' were going by").
        ''' </summary>
        Public Property CharacterId As String

        ''' <summary>
        ''' In-game character display name for this row, taken
        ''' RAW from the underlying entity — unlike PlayerName,
        ''' this is NOT coalesced through IdentityFormatter, so
        ''' the column reads empty when the entity row's
        ''' DisplayName was NULL at write time (typical for early
        ''' rows in a session before the Node's players-table
        ''' cache resolved the character name from the Persisting
        ''' tick). Drives the History window's "Character" column,
        ''' which sits alongside the persona-only "Player" column
        ''' so the operator can trace either identity
        ''' independently — a row missing the character name
        ''' still shows the persona, and vice versa.
        '''
        ''' Sources:
        '''   - Chat rows: ChatMessages.DisplayName directly. Chat
        '''     lines on Last Oasis and Conan carry the in-game
        '''     character name natively, so the value is canonical
        '''     at write time.
        '''   - Join/Leave rows: PlayerActivity.DisplayName, with
        '''     the same Phase 5g-2b render-time chat fallback as
        '''     PlayerName — if DisplayName was empty (or equal to
        '''     the raw PlayerName) and the row carries a
        '''     PlatformUserId, ApplyChatFallbackDisplayNames
        '''     overrides it with the most recent chat DisplayName
        '''     for that (session, pid) pair.
        ''' </summary>
        Public Property CharacterName As String

        ''' <summary>
        ''' Platform persona (Steam handle, Funcom FLS handle,
        ''' multiplayer username on Factorio, etc.) for this row
        ''' — the raw platform-level identifier, distinct from the
        ''' character name the player chose in-game. Drives the
        ''' History window's "Player" column.
        '''
        ''' Sources:
        '''   - Join/Leave rows: PlayerActivity.PlayerName, which
        '''     is the raw login-line string the parser captured
        '''     at the moment of the event. On Last Oasis this is
        '''     the Steam persona; on Conan the FLS handle; on
        '''     Factorio the multiplayer username (same as the
        '''     character name there).
        '''   - Chat rows: ChatMessages doesn't carry a persona
        '''     column directly, so this is resolved via a batch
        '''     lookup against PlayerActivity by (SessionIdentity,
        '''     PlatformUserId) — ApplyActivityFallbackPersona
        '''     pulls the most recent PlayerActivity.PlayerName
        '''     for each chat row's pid. Chat rows whose pid never
        '''     appears in PlayerActivity (returning players on a
        '''     Node that hasn't seen them join in scope) stay
        '''     empty.
        ''' </summary>
        Public Property PlatformPersona As String

        Public Property Text As String

        ''' <summary>
        ''' Phase 5h-6 — plugin-formatted label for the History
        ''' window's "Source" column, replacing the legacy
        ''' Tile/Session + Instance columns. Resolved per row by
        ''' the manager: a SourceLabelContext is built from this
        ''' row's SessionIdentity + InstanceId plus the resolved
        ''' node / installation / instance / linked-group display
        ''' names, then dispatched to the plugin's
        ''' ISourceLabelProvider.FormatSourceLabel implementation.
        ''' Plugins not implementing that interface get a default
        ''' "{Node}/{Install}/{Instance}" label.
        ''' </summary>
        Public Property SourceLabel As String
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

        ''' <summary>
        ''' In-game character display name for the player at
        ''' the snapshot instant — same semantics as
        ''' TimelineRow.CharacterName but resolved from the
        ''' join event's DisplayName captured during the
        ''' activity replay. Empty when the join row didn't
        ''' have DisplayName bound (typical for early-session
        ''' joins before the Node resolved the character name).
        ''' </summary>
        Public Property CharacterName As String

        ''' <summary>
        ''' Platform persona for the player at the snapshot
        ''' instant — the join event's PlayerName, which on
        ''' Last Oasis is the Steam persona, on Conan the FLS
        ''' handle, on Factorio the username (which equals the
        ''' character name there). Always populated for snapshot
        ''' rows because the replay key includes PlayerName.
        ''' </summary>
        Public Property PlatformPersona As String

        ''' <summary>
        ''' Phase 5h-6 — InstanceId of the join event the player
        ''' arrived on (the activity replay captures whichever
        ''' instance bore the join up to the snapshot instant).
        ''' Used to dispatch SourceLabel formatting through the
        ''' plugin, and as the value the History window's right-
        ''' click "Copy instance ID" action emits.
        ''' </summary>
        Public Property InstanceId As String

        ''' <summary>
        ''' Phase 5h-6 — plugin-formatted Source label, resolved
        ''' the same way as TimelineRow.SourceLabel.
        ''' </summary>
        Public Property SourceLabel As String
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

                ' Phase 5h-6 — build a session-identity → realm
                ' name map so the session dropdown can
                ' show the friendly realm name (e.g. "Site's World")
                ' instead of the raw realm_id substring for
                ' installations linked to a SharedConfigGroup.
                ' Path: SessionHosts.InstanceId → Instance →
                ' Installation → SharedConfigGroup. First-write-
                ' wins per identity — if the same session
                ' identity has been hosted by installs linked
                ' to different groups (shouldn't happen in
                ' practice; would mean operator misconfiguration),
                ' we just pick one rather than try to disambiguate.
                Dim realmNameByIdentity As New Dictionary(Of String, String)(StringComparer.Ordinal)
                Dim hostMappings = (From h In db.SessionHosts
                                    Join inst In db.Instances
                                        On h.InstanceId Equals inst.InstanceId
                                    Join install In db.Installations
                                        On inst.InstallationId Equals install.InstallationId
                                    Where install.SharedConfigGroupId IsNot Nothing
                                    Select New With {
                                        .Identity = h.SessionIdentity,
                                        .GroupId = install.SharedConfigGroupId
                                    }).Distinct().ToList()
                If hostMappings.Count > 0 Then
                    Dim groupIds = hostMappings.
                        Select(Function(m) m.GroupId).Distinct().ToList()
                    ' Resolve each linked group to a realm label, preferring
                    ' its canonical RealmName field over the DisplayName
                    ' (which may carry a per-provider "(label)" suffix) so
                    ' the dropdown matches the History Source column; falls
                    ' back to DisplayName when no RealmName is set (7-6 2a).
                    Dim groupDisplayNames = db.SharedConfigGroups.
                        Where(Function(g) groupIds.Contains(g.GroupId)).
                        Select(Function(g) New With {.GroupId = g.GroupId, .DisplayName = g.DisplayName}).
                        ToDictionary(Function(x) x.GroupId, Function(x) x.DisplayName)
                    Dim sharedConfigService = scope.ServiceProvider.GetService(Of SharedConfigService)()
                    Dim realmLabelByGroup As New Dictionary(Of String, String)(StringComparer.Ordinal)
                    For Each gid In groupIds
                        Dim label As String = Nothing
                        If sharedConfigService IsNot Nothing Then
                            Dim rn As String = Nothing
                            If sharedConfigService.LoadNonSensitiveFields(db, gid).TryGetValue("RealmName", rn) AndAlso
                               Not String.IsNullOrEmpty(rn) Then
                                label = rn
                            End If
                        End If
                        If String.IsNullOrEmpty(label) Then groupDisplayNames.TryGetValue(gid, label)
                        If Not String.IsNullOrEmpty(label) Then realmLabelByGroup(gid) = label
                    Next
                    For Each m In hostMappings
                        Dim label As String = Nothing
                        If realmLabelByGroup.TryGetValue(m.GroupId, label) AndAlso
                           Not String.IsNullOrEmpty(label) AndAlso
                           Not realmNameByIdentity.ContainsKey(m.Identity) Then
                            realmNameByIdentity(m.Identity) = label
                        End If
                    Next
                End If

                Dim result As New List(Of SessionSummary)(merged.Count)
                For Each kvp In merged
                    Dim tileName As String = Nothing
                    nameByIdentity.TryGetValue(kvp.Key, tileName)
                    Dim realmName As String = Nothing
                    realmNameByIdentity.TryGetValue(kvp.Key, realmName)
                    result.Add(New SessionSummary With {
                        .Identity = kvp.Key,
                        .DisplayLabel = FormatSessionLabel(kvp.Key, tileName, realmName),
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
                    Where(Function(c) c.DisplayName IsNot Nothing).
                    Select(Function(c) c.DisplayName).Distinct().ToList()

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

            ' Hoisted out of the Using so the post-truncate
            ' SourceLabel resolver can reuse the same map. The
            ' Dictionary itself is just in-memory data; the EF
            ' connection that produced it can close behind us.
            Dim tileNames As Dictionary(Of String, String) = Nothing

            Using scope = _serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                ' Resolve tile-name map once for the whole query
                ' so we don't do per-row lookups.
                tileNames = LoadTileNameMap(db, filter.SessionIdentity)

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
                        q = q.Where(Function(c) c.DisplayName IsNot Nothing AndAlso
                                                  EF.Functions.Like(c.DisplayName, $"%{p}%"))
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

                    ' Chat rows track (Row, SessionIdentity, PlatformUserId)
                    ' tuples for a follow-up batch lookup that resolves
                    ' the persona (PlatformPersona) from the matching
                    ' PlayerActivity rows for the same (session, pid).
                    ' Chat lines on Last Oasis and Conan don't carry the
                    ' platform persona on the wire — only the character
                    ' name — so the Player column would be blank for
                    ' every chat row without this lookup. Pulls are
                    ' batched by distinct pid (typically a handful per
                    ' query) so the cost is bounded regardless of how
                    ' many chat rows came back.
                    Dim chatRowsNeedingPersona As _
                        New List(Of (Row As TimelineRow, SessionIdentity As String, PlatformUserId As String))

                    For Each r In chatRows
                        Dim row = New TimelineRow With {
                            .Kind = TimelineRow.RowKind.Chat,
                            .TimestampUtc = r.TimestampUtc,
                            .SessionIdentity = r.SessionIdentity,
                            .TileDisplayName = ResolveDisplayName(tileNames, r.SessionIdentity),
                            .InstanceId = r.InstanceId,
                            .PlayerName = r.DisplayName,
                            .PlatformUserId = r.PlatformUserId,
                            .CharacterId = r.CharacterId,
                            .CharacterName = r.DisplayName,
                            .PlatformPersona = Nothing,
                            .Text = r.Text
                        }
                        merged.Add(row)

                        If Not String.IsNullOrEmpty(r.PlatformUserId) Then
                            chatRowsNeedingPersona.Add((row, r.SessionIdentity, r.PlatformUserId))
                        End If
                    Next

                    If chatRowsNeedingPersona.Count > 0 Then
                        ApplyActivityFallbackPersona(db, chatRowsNeedingPersona)
                    End If
                End If

                ' ---- Activity slice ----
                If filter.IncludeJoins OrElse filter.IncludeLeaves Then
                    token.ThrowIfCancellationRequested()
                    ' Phase 5g-2d Round 3c — render-time identity
                    ' enrichment source. Singleton; GetService is
                    ' cheap and returns Nothing only if somehow
                    ' unregistered (defensive null-check at use).
                    Dim identityResolver = _serviceProvider.GetService(Of IdentityResolver)()
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

                    ' Track activity rows where the write-time
                    ' snapshot couldn't bind a meaningful character
                    ' name — either DisplayName was empty (Node's
                    ' players-table cache missed at the moment of
                    ' the snapshot, common for first-time-on-this-
                    ' Node players) or DisplayName equals the raw
                    ' PlayerName (e.g. Conan's "Join succeeded:"
                    ' line carries the FLS handle, and pre-5g-2b
                    ' rows have the same value in both slots).
                    ' For these, do a render-time lookup against
                    ' ChatMessages by (SessionIdentity,
                    ' PlatformUserId) to pull the most recent
                    ' character name the player chatted under.
                    ' Rows with no chat to bridge through stay on
                    ' the raw parser PlayerName — best-effort.
                    Dim rowsNeedingChatFallback As _
                        New List(Of (Row As TimelineRow, SessionIdentity As String, PlatformUserId As String))

                    For Each r In actRows
                        Dim row = New TimelineRow With {
                            .Kind = If(r.EventKind = "join",
                                        TimelineRow.RowKind.Join,
                                        TimelineRow.RowKind.Leave),
                            .TimestampUtc = r.TimestampUtc,
                            .SessionIdentity = r.SessionIdentity,
                            .TileDisplayName = ResolveDisplayName(tileNames, r.SessionIdentity),
                            .InstanceId = r.InstanceId,
                            .PlayerName = IdentityFormatter.Format(r.DisplayName, Nothing, r.PlayerName),
                            .PlatformUserId = r.PlatformUserId,
                            .CharacterId = r.CharacterId,
                            .CharacterName = r.DisplayName,
                            .PlatformPersona = r.PlayerName,
                            .Text = Nothing
                        }
                        merged.Add(row)

                        ' ---- Resolver render-time enrichment (5g-2d Round 3c) ----
                        ' For rows whose stored snapshot couldn't bind
                        ' a character name (DisplayName empty, or equal
                        ' to the raw PlayerName), consult the in-memory
                        ' resolver before falling back to the chat
                        ' lookup. The resolver is hydrated from History
                        ' and continuously fed, so it often knows the
                        ' character name for OLD rows that were
                        ' persisted NULL before this player's identity
                        ' was ever resolved — including rows for players
                        ' who aren't currently online (which the chat
                        ' fallback can also do, but the resolver is
                        ' in-memory and covers the persona-keyed case
                        ' the chat lookup can't when PlatformUserId is
                        ' absent). Probe carries every known facet so
                        ' the resolver matches on its strongest key.
                        Dim resolvedByResolver = False
                        If identityResolver IsNot Nothing AndAlso
                           (String.IsNullOrEmpty(r.DisplayName) OrElse
                            String.Equals(r.DisplayName, r.PlayerName, StringComparison.Ordinal)) Then
                            Try
                                Dim probe = New PlayerSession With {
                                    .PlatformPersona = r.PlayerName,
                                    .PlatformUserId = r.PlatformUserId,
                                    .CharacterId = r.CharacterId,
                                    .DisplayName = r.DisplayName
                                }
                                Dim hit = identityResolver.EnrichBySessionIdentity(r.SessionIdentity, probe)
                                If hit IsNot Nothing AndAlso
                                   Not String.IsNullOrEmpty(hit.DisplayName) AndAlso
                                   Not String.Equals(hit.DisplayName, r.PlayerName, StringComparison.Ordinal) Then
                                    row.PlayerName = IdentityFormatter.Format(hit.DisplayName, Nothing, r.PlayerName)
                                    If String.IsNullOrEmpty(row.CharacterId) Then row.CharacterId = hit.CharacterId
                                    If String.IsNullOrEmpty(row.PlatformUserId) Then row.PlatformUserId = hit.PlatformUserId
                                    resolvedByResolver = True
                                End If
                            Catch
                                ' Best-effort; fall through to chat fallback.
                            End Try
                        End If

                        If Not resolvedByResolver AndAlso
                           Not String.IsNullOrEmpty(r.PlatformUserId) AndAlso
                           (String.IsNullOrEmpty(r.DisplayName) OrElse
                            String.Equals(r.DisplayName, r.PlayerName, StringComparison.Ordinal)) Then
                            rowsNeedingChatFallback.Add((row, r.SessionIdentity, r.PlatformUserId))
                        End If
                    Next

                    If rowsNeedingChatFallback.Count > 0 Then
                        ApplyChatFallbackDisplayNames(db, rowsNeedingChatFallback)
                    End If
                End If
            End Using

            ' Merge-sort by time desc. Two already-sorted inputs,
            ' but concatenate-then-sort is cheap at these sizes and
            ' simpler than writing a dedicated merge.
            merged.Sort(Function(a, b) b.TimestampUtc.CompareTo(a.TimestampUtc))

            Dim truncated = merged.Count > TimelineRowLimit
            If truncated Then merged = merged.Take(TimelineRowLimit).ToList()

            ' Resolve InstanceDisplay + SourceLabel for every row
            ' in one pass. Done after the truncate so we don't
            ' waste a query on rows we'll discard. Inside a fresh
            ' scope so the lookup uses its own DbContext rather
            ' than reusing one whose connection we already closed.
            ResolveInstanceDisplayNames(merged, tileNames)

            Return New TimelineResult With {
                .Rows = merged,
                .Truncated = truncated,
                .Limit = TimelineRowLimit
            }
        End Function

        ''' <summary>
        ''' Populates TimelineRow.InstanceDisplay AND
        ''' TimelineRow.SourceLabel for every row in the list,
        ''' doing a single JOIN against Instances + Installations
        ''' + Nodes for all distinct InstanceIds plus a follow-up
        ''' lookup against SharedConfigGroups for any installs
        ''' that link to one. Rows whose InstanceId no longer
        ''' resolves to a live (instance, installation, node)
        ''' triple keep the fallback display
        ''' "(deleted):(deleted):{instanceId}" so the operator can
        ''' still see WHICH instance produced the row even after
        ''' the configuration was removed — useful for retrospective
        ''' debugging of long-gone servers.
        '''
        ''' SourceLabel goes through the plugin's
        ''' ISourceLabelProvider implementation if the plugin
        ''' opts in, falling back to a manager-supplied default
        ''' "{Node}/{Install}/{Instance}" otherwise.
        ''' </summary>
        Private Sub ResolveInstanceDisplayNames(rows As List(Of TimelineRow),
                                                  tileNames As Dictionary(Of String, String))
            If rows Is Nothing OrElse rows.Count = 0 Then Return

            Dim distinctIds = rows.
                Where(Function(r) Not String.IsNullOrEmpty(r.InstanceId)).
                Select(Function(r) r.InstanceId).
                Distinct().ToList()

            Dim contexts = LoadResolvedInstances(distinctIds)
            Dim registry = _serviceProvider.GetService(Of PluginRegistry)()

            For Each r In rows
                If Not String.IsNullOrEmpty(r.InstanceId) Then
                    Dim resolved As ResolvedInstance = Nothing
                    If contexts.TryGetValue(r.InstanceId, resolved) Then
                        r.InstanceDisplay =
                            $"{If(resolved.NodeName, "")}:{If(resolved.InstanceName, "")}:{r.InstanceId}"
                    Else
                        ' Instance row no longer exists in the
                        ' configuration (deleted, but its history
                        ' rows linger). Surface that explicitly so
                        ' the operator doesn't think it's a bug.
                        r.InstanceDisplay = $"(deleted):(deleted):{r.InstanceId}"
                    End If
                End If
                r.SourceLabel = ResolveSourceLabel(r.SessionIdentity, r.InstanceId,
                                                     contexts, tileNames, registry)
            Next
        End Sub

        ''' <summary>
        ''' Phase 5h-6 — Pre-resolved per-instance context used by
        ''' the SourceLabel and InstanceDisplay paths. Captures
        ''' enough information about each unique InstanceId to
        ''' build a SourceLabelContext without further DB hits.
        ''' </summary>
        Private Class ResolvedInstance
            Public NodeName As String
            Public InstallationName As String
            Public InstanceName As String
            Public GameId As String
            Public SharedConfigGroupName As String
            Public SharedConfigFields As Dictionary(Of String, String)
        End Class

        ''' <summary>
        ''' Phase 5h-6 — fetch ResolvedInstance for each unique
        ''' InstanceId in distinctIds. Two queries: the first
        ''' walks the (Instance, Installation, Node) chain in a
        ''' single round-trip; the second pulls SharedConfigGroup
        ''' DisplayNames for any installs that link to one. Both
        ''' results are merged into the returned dictionary.
        ''' Empty input returns an empty dictionary without
        ''' touching the database.
        ''' </summary>
        Private Function LoadResolvedInstances(distinctIds As List(Of String)) _
                As Dictionary(Of String, ResolvedInstance)
            Dim map As New Dictionary(Of String, ResolvedInstance)(StringComparer.Ordinal)
            If distinctIds Is Nothing OrElse distinctIds.Count = 0 Then Return map

            Using scope = _serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                Dim instanceData = (From inst In db.Instances
                                    Join install In db.Installations
                                        On inst.InstallationId Equals install.InstallationId
                                    Join nodeRow In db.Nodes
                                        On install.NodeId Equals nodeRow.NodeId
                                    Where distinctIds.Contains(inst.InstanceId)
                                    Select New With {
                                        .InstanceId = inst.InstanceId,
                                        .NodeName = nodeRow.DisplayName,
                                        .InstallationName = install.DisplayName,
                                        .InstanceName = inst.DisplayName,
                                        .GameId = install.GameId,
                                        .SharedConfigGroupId = install.SharedConfigGroupId
                                    }).ToList()

                ' Second pass: load the linked SharedConfigGroup
                ' DisplayNames for any installs that point to one.
                ' LEFT JOIN expressed as two queries + in-memory
                ' merge — simpler than VB's Group Join syntax and
                ' the typical N (number of distinct linked groups)
                ' is tiny.
                Dim groupIds = instanceData.
                    Where(Function(r) Not String.IsNullOrEmpty(r.SharedConfigGroupId)).
                    Select(Function(r) r.SharedConfigGroupId).
                    Distinct().ToList()

                Dim groupNameMap As New Dictionary(Of String, String)(StringComparer.Ordinal)
                Dim groupFieldsMap As New Dictionary(Of String, Dictionary(Of String, String))(StringComparer.Ordinal)
                If groupIds.Count > 0 Then
                    Dim groups = db.SharedConfigGroups.
                        Where(Function(g) groupIds.Contains(g.GroupId)).
                        Select(Function(g) New With {.GroupId = g.GroupId, .DisplayName = g.DisplayName}).
                        ToList()
                    For Each g In groups
                        groupNameMap(g.GroupId) = g.DisplayName
                    Next

                    ' Phase 7-6 — also pull each linked group's
                    ' non-sensitive fields (RealmName et al.) so the
                    ' source label can prefer the canonical RealmName
                    ' over the per-group DisplayName. Non-sensitive
                    ' only: the encrypted keys are never decrypted here.
                    Dim sharedConfigService = scope.ServiceProvider.GetService(Of SharedConfigService)()
                    If sharedConfigService IsNot Nothing Then
                        For Each gid In groupIds
                            groupFieldsMap(gid) = sharedConfigService.LoadNonSensitiveFields(db, gid)
                        Next
                    End If
                End If

                For Each row In instanceData
                    Dim groupName As String = Nothing
                    Dim groupFields As Dictionary(Of String, String) = Nothing
                    If Not String.IsNullOrEmpty(row.SharedConfigGroupId) Then
                        groupNameMap.TryGetValue(row.SharedConfigGroupId, groupName)
                        groupFieldsMap.TryGetValue(row.SharedConfigGroupId, groupFields)
                    End If
                    map(row.InstanceId) = New ResolvedInstance With {
                        .NodeName = row.NodeName,
                        .InstallationName = row.InstallationName,
                        .InstanceName = row.InstanceName,
                        .GameId = row.GameId,
                        .SharedConfigGroupName = groupName,
                        .SharedConfigFields = groupFields
                    }
                Next
            End Using
            Return map
        End Function

        ''' <summary>
        ''' Phase 5h-6 — build a SourceLabelContext and dispatch
        ''' to the plugin's ISourceLabelProvider implementation,
        ''' falling back to a manager-supplied default if the
        ''' plugin doesn't opt in (or returns Nothing/empty).
        ''' Defensive against plugin exceptions — a misbehaving
        ''' plugin's formatting bug shouldn't kill the whole query.
        ''' </summary>
        Private Shared Function ResolveSourceLabel(sessionIdentity As String,
                                                     instanceId As String,
                                                     contexts As Dictionary(Of String, ResolvedInstance),
                                                     tileNames As Dictionary(Of String, String),
                                                     registry As PluginRegistry) As String
            Dim tileName As String = Nothing
            If tileNames IsNot Nothing AndAlso Not String.IsNullOrEmpty(sessionIdentity) Then
                tileNames.TryGetValue(sessionIdentity, tileName)
            End If

            Dim resolved As ResolvedInstance = Nothing
            If contexts IsNot Nothing AndAlso Not String.IsNullOrEmpty(instanceId) Then
                contexts.TryGetValue(instanceId, resolved)
            End If

            Dim ctx As New SourceLabelContext With {
                .SessionIdentity = sessionIdentity,
                .TileName = tileName,
                .InstanceId = instanceId
            }
            If resolved IsNot Nothing Then
                ctx.NodeName = resolved.NodeName
                ctx.InstallationName = resolved.InstallationName
                ctx.InstanceName = resolved.InstanceName
                ctx.SharedConfigGroupName = resolved.SharedConfigGroupName
                ctx.SharedConfigFields = resolved.SharedConfigFields
            End If

            ' Dispatch to plugin if the GameId resolves to a
            ' loaded plugin that implements the interface.
            Dim label As String = Nothing
            If resolved IsNot Nothing AndAlso registry IsNot Nothing AndAlso
               Not String.IsNullOrEmpty(resolved.GameId) Then
                Dim gamePlugin = registry.GetPlugin(resolved.GameId)
                If gamePlugin IsNot Nothing Then
                    Dim provider = TryCast(gamePlugin, ISourceLabelProvider)
                    If provider IsNot Nothing Then
                        Try
                            label = provider.FormatSourceLabel(ctx)
                        Catch
                            ' Plugin bug — fall through to default.
                        End Try
                    End If
                End If
            End If

            If Not String.IsNullOrEmpty(label) Then Return label
            Return BuildDefaultSourceLabel(ctx)
        End Function

        ''' <summary>
        ''' Phase 5h-6 — the manager-supplied fallback label used
        ''' when no plugin opts in. Format is "{Node}/{Install}/
        ''' {Instance}", skipping empty segments so a partially-
        ''' resolved context (e.g. instance deleted but row
        ''' lingers) still produces a clean label rather than
        ''' awkward double-slash gaps. Returns the raw
        ''' SessionIdentity if no instance segments resolve at
        ''' all — better than a blank cell.
        ''' </summary>
        Private Shared Function BuildDefaultSourceLabel(ctx As SourceLabelContext) As String
            Dim segments As New List(Of String)
            If Not String.IsNullOrEmpty(ctx.NodeName) Then segments.Add(ctx.NodeName)
            If Not String.IsNullOrEmpty(ctx.InstallationName) Then segments.Add(ctx.InstallationName)
            If Not String.IsNullOrEmpty(ctx.InstanceName) Then segments.Add(ctx.InstanceName)
            If segments.Count > 0 Then Return String.Join("/", segments)
            Return If(ctx.SessionIdentity, "")
        End Function

        ''' <summary>
        ''' Phase 5g-2b render-time chat fallback. For each
        ''' activity TimelineRow whose write-time snapshot didn't
        ''' bind a meaningful character name (DisplayName empty or
        ''' equal to raw PlayerName), look up the most recent
        ''' ChatMessages.DisplayName for that (SessionIdentity,
        ''' PlatformUserId) pair and override TimelineRow.PlayerName
        ''' if found.
        '''
        ''' Query strategy: one indexed lookup per DISTINCT
        ''' (SessionIdentity, PlatformUserId) pair. The IX_chat_pid
        ''' index added in Phase 5g-1 keys on (PlatformUserId,
        ''' TimestampUtc DESC) so each lookup is a fast seek;
        ''' bounding by distinct pairs (typically a handful, even
        ''' on a busy server) keeps the total query count small.
        ''' Pulled-then-discarded data is minimal because we project
        ''' just DisplayName and TOP 1.
        '''
        ''' Caller is inside an existing scope; we share the
        ''' caller's DbContext rather than opening a new one.
        ''' </summary>
        Private Shared Sub ApplyChatFallbackDisplayNames(
                db As GsmDbContext,
                rowsNeedingFallback As List(Of (Row As TimelineRow, SessionIdentity As String, PlatformUserId As String)))
            If rowsNeedingFallback Is Nothing OrElse rowsNeedingFallback.Count = 0 Then Return

            ' Unique (sessionId, pid) pairs — multiple TimelineRows
            ' for the same player collapse to one query.
            ' Explicit named-tuple syntax (`:=`) on the Select
            ' projection so element names survive the Distinct/
            ' ToList chain regardless of how aggressive VB.NET's
            ' tuple-element-name inference is in any given build.
            Dim uniquePairs = rowsNeedingFallback.
                Select(Function(t) (SessionIdentity:=t.SessionIdentity, PlatformUserId:=t.PlatformUserId)).
                Distinct().ToList()

            Dim displayNameMap As New Dictionary(Of String, String)(StringComparer.Ordinal)
            For Each pair In uniquePairs
                Dim sid = pair.SessionIdentity
                Dim pid = pair.PlatformUserId
                Dim displayName = db.ChatMessages.
                    Where(Function(c) c.SessionIdentity = sid AndAlso
                                        c.PlatformUserId = pid AndAlso
                                        c.DisplayName IsNot Nothing).
                    OrderByDescending(Function(c) c.TimestampUtc).
                    Select(Function(c) c.DisplayName).
                    FirstOrDefault()
                If Not String.IsNullOrEmpty(displayName) Then
                    displayNameMap(sid & "|" & pid) = displayName
                End If
            Next

            For Each tup In rowsNeedingFallback
                Dim key = tup.SessionIdentity & "|" & tup.PlatformUserId
                Dim resolved As String = Nothing
                If displayNameMap.TryGetValue(key, resolved) AndAlso
                   Not String.IsNullOrEmpty(resolved) Then
                    ' Update both the coalesced legacy PlayerName
                    ' (consumed by GsmSlashCommands.BuildPlayersResponse
                    ' and any other caller that wants "best display
                    ' name") AND the new CharacterName column, so the
                    ' History window's Character column also picks up
                    ' the chat-fallback resolution.
                    tup.Row.PlayerName = resolved
                    tup.Row.CharacterName = resolved
                End If
            Next
        End Sub

        ''' <summary>
        ''' Inverse of ApplyChatFallbackDisplayNames: for each chat
        ''' TimelineRow that carries a PlatformUserId but whose
        ''' PlatformPersona is empty (the chat entity has no persona
        ''' column on the wire), look up the most recent
        ''' PlayerActivity.PlayerName for that (SessionIdentity, pid)
        ''' pair and bind it. Lets the History window's Player
        ''' column populate consistently across both chat and
        ''' activity rows even though only activity rows carry the
        ''' persona natively.
        '''
        ''' Query strategy mirrors ApplyChatFallbackDisplayNames —
        ''' one indexed lookup per DISTINCT (session, pid) pair,
        ''' bounded by the small number of distinct chatting
        ''' players in scope. Players who chatted but never had a
        ''' Join row written (returning players on a Node whose
        ''' players-table cache lost the join event — e.g. across
        ''' a Manager restart that missed the join line) stay
        ''' empty; we don't fabricate a persona we can't prove.
        ''' </summary>
        Private Shared Sub ApplyActivityFallbackPersona(
                db As GsmDbContext,
                rowsNeedingFallback As List(Of (Row As TimelineRow, SessionIdentity As String, PlatformUserId As String)))
            If rowsNeedingFallback Is Nothing OrElse rowsNeedingFallback.Count = 0 Then Return

            Dim uniquePairs = rowsNeedingFallback.
                Select(Function(t) (SessionIdentity:=t.SessionIdentity, PlatformUserId:=t.PlatformUserId)).
                Distinct().ToList()

            Dim personaMap As New Dictionary(Of String, String)(StringComparer.Ordinal)
            For Each pair In uniquePairs
                Dim sid = pair.SessionIdentity
                Dim pid = pair.PlatformUserId
                ' Prefer the most-recent activity row for this pid
                ' — if the player rejoined under a different login
                ' string at some point (rare but possible after a
                ' Steam name change), we want the most current
                ' persona, not the historical one.
                Dim persona = db.PlayerActivity.
                    Where(Function(a) a.SessionIdentity = sid AndAlso
                                        a.PlatformUserId = pid AndAlso
                                        a.PlayerName IsNot Nothing).
                    OrderByDescending(Function(a) a.TimestampUtc).
                    Select(Function(a) a.PlayerName).
                    FirstOrDefault()
                If Not String.IsNullOrEmpty(persona) Then
                    personaMap(sid & "|" & pid) = persona
                End If
            Next

            For Each tup In rowsNeedingFallback
                Dim key = tup.SessionIdentity & "|" & tup.PlatformUserId
                Dim resolved As String = Nothing
                If personaMap.TryGetValue(key, resolved) AndAlso
                   Not String.IsNullOrEmpty(resolved) Then
                    tup.Row.PlatformPersona = resolved
                End If
            Next
        End Sub

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
            Dim tileNames As Dictionary(Of String, String) = Nothing
            Dim result As New List(Of SnapshotRow)

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
                ' don't collapse the same name across tiles. The
                ' tuple now also carries InstanceId so the snapshot
                ' row's SourceLabel can resolve through the same
                ' plugin dispatch the timeline uses. DisplayName is
                ' captured too so SnapshotRow.CharacterName can
                ' surface the in-game character name alongside the
                ' platform persona — same dual-column treatment as
                ' the timeline view.
                Dim present As _
                    New Dictionary(Of String, (SessionIdentity As String, PlayerName As String, DisplayName As String, JoinedUtc As DateTime, InstanceId As String))
                For Each ev In events
                    Dim key = ev.SessionIdentity & "|" & ev.PlayerName
                    If ev.EventKind = "join" Then
                        present(key) = (ev.SessionIdentity, ev.PlayerName, ev.DisplayName, ev.TimestampUtc, ev.InstanceId)
                    ElseIf ev.EventKind = "leave" Then
                        present.Remove(key)
                    End If
                Next

                If present.Count = 0 Then Return New List(Of SnapshotRow)

                tileNames = LoadTileNameMap(db, filter.SessionIdentity)

                ' For each present player, fetch the most recent
                ' chat message at-or-before the instant. One query
                ' per player is fine at the sizes we expect
                ' (presence count is bounded by active players).
                For Each kvp In present
                    token.ThrowIfCancellationRequested()
                    Dim info = kvp.Value
                    Dim lastChat = db.ChatMessages.
                        Where(Function(c) c.SessionIdentity = info.SessionIdentity AndAlso
                                           c.DisplayName = info.PlayerName AndAlso
                                           c.TimestampUtc <= instant).
                        OrderByDescending(Function(c) c.TimestampUtc).
                        FirstOrDefault()

                    result.Add(New SnapshotRow With {
                        .PlayerName = info.PlayerName,
                        .JoinedAtUtc = info.JoinedUtc,
                        .SessionIdentity = info.SessionIdentity,
                        .TileDisplayName = ResolveDisplayName(tileNames, info.SessionIdentity),
                        .InstanceId = info.InstanceId,
                        .CharacterName = info.DisplayName,
                        .PlatformPersona = info.PlayerName,
                        .LastChatText = If(lastChat IsNot Nothing, lastChat.Text, Nothing),
                        .LastChatTimeUtc = If(lastChat IsNot Nothing,
                                              CType(lastChat.TimestampUtc, DateTime?),
                                              Nothing)
                    })
                Next
            End Using

            ' Phase 5h-6 — resolve plugin-formatted SourceLabel
            ' in a follow-up pass. Same shape as the timeline
            ' path: load instance contexts once for all distinct
            ' InstanceIds, then dispatch per row.
            Dim distinctInstanceIds = result.
                Where(Function(r) Not String.IsNullOrEmpty(r.InstanceId)).
                Select(Function(r) r.InstanceId).
                Distinct().ToList()
            Dim instanceContexts = LoadResolvedInstances(distinctInstanceIds)
            Dim registry = _serviceProvider.GetService(Of PluginRegistry)()
            For Each r In result
                r.SourceLabel = ResolveSourceLabel(r.SessionIdentity, r.InstanceId,
                                                     instanceContexts, tileNames, registry)
            Next

            Return result.OrderBy(Function(r) r.PlayerName,
                                   StringComparer.OrdinalIgnoreCase).ToList()
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
        ''' Turn a raw session identity + optional tile name +
        ''' optional realm DisplayName into a display string.
        ''' Last Oasis: "Forested Wetlands — Site's World" when
        ''' realmDisplayName is provided (Phase 5h-6, from the
        ''' linked SharedConfigGroup), or "Forested Wetlands —
        ''' realm 281474…" when it isn't (the legacy fallback to
        ''' the realm_id substring). Factorio or anything else
        ''' without a resolvable tile name: shows the identity
        ''' as-is. Nothing in → empty string out.
        ''' </summary>
        Public Shared Function FormatSessionLabel(sessionIdentity As String,
                                                   tileName As String,
                                                   Optional realmDisplayName As String = Nothing) As String
            If String.IsNullOrEmpty(sessionIdentity) Then Return ""

            ' Last Oasis format is "lastoasis:{realm_id}:{tile_id}".
            If sessionIdentity.StartsWith("lastoasis:", StringComparison.Ordinal) Then
                Dim parts = sessionIdentity.Split(":"c)
                If parts.Length >= 3 Then
                    ' Realm segment: prefer the user-set realm name
                    ' over the truncated numeric realm_id. Both end
                    ' up bare-text without a "realm" prefix when the
                    ' DisplayName is set — the friendly name is the
                    ' realm, no qualifier needed.
                    Dim realmSegment As String
                    If Not String.IsNullOrEmpty(realmDisplayName) Then
                        realmSegment = realmDisplayName
                    Else
                        Dim realmId = parts(1)
                        Dim realmShort = If(realmId.Length > 8, realmId.Substring(0, 8) & "…", realmId)
                        realmSegment = "realm " & realmShort
                    End If
                    If Not String.IsNullOrEmpty(tileName) Then
                        Return $"{tileName} — {realmSegment}"
                    End If
                    Return $"Tile {parts(2)} — {realmSegment}"
                End If
            End If

            ' Fallback: raw identity.
            Return sessionIdentity
        End Function

    End Class

End Namespace
