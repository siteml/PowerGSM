Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager
Imports GSM.Manager.Data
Imports GSM.Node.Api

' ============================================================
'  IdentityResolver — Phase 5g-2d
'
'  Centralised player-identity enrichment for the Manager.
'  Solves the "Overview panel shows site_ml when History shows
'  site's character" asymmetry (and the broader class of
'  /players-snapshot-misses-DisplayName timing races) by
'  promoting the Manager to system-of-record for resolved
'  identity. Every consumer of PlayerSession data passes its
'  session through Enrich(...) before rendering, and every
'  observer of identity information pushes the observation
'  through Observe(...) so downstream Enrich calls return
'  better answers.
'
'  The design challenge is "schizophrenic keys": identity
'  observations arrive piecemeal — Login request gives us
'  PlatformUserId + CharacterId, Join succeeded gives us
'  PlatformPersona, chat lines may give DisplayName tied to a
'  CharacterId. A naive cache keyed on each individual field
'  ends up with multiple records per actual person, one per
'  key kind, each carrying partial info. The resolver instead
'  uses a small union-find: each IdentityRecord carries a SET
'  of alias keys, and any new observation that matches ANY
'  existing key merges into that record. When an observation
'  arrives that connects two previously-separate records
'  (e.g. a chat line links a DisplayName-keyed record with a
'  CharacterId-keyed record), the records fuse.
'
'  Field-level conflict resolution:
'    - PlatformUserId, CharacterId: never change for the same
'      identity. Conflict attempts log a warning and keep the
'      existing value (a should-never-happen merge indicates
'      an upstream bug).
'    - DisplayName, PlatformPersona: newest-write-wins, to
'      support legitimate renames (myrealm character rename
'      on LO, Steam persona changes, etc.).
'    - Empty observations never overwrite non-empty fields.
'
'  Scope model is opaque: the resolver doesn't interpret what
'  (gameId, sessionScope) mean — they're keys. Plugins decide
'  how to derive sessionScope from their game's state (LO:
'  realmId; Conan: installId for v1; Factorio: installId).
'  See Phase5g-2d_Plan.md for the architectural framing.
'
'  Thread safety: a single ReaderWriterLockSlim guards the
'  records list and the alias index. Reads (Enrich, FindByKey,
'  diagnostic GetAllRecords) take the read lock; mutating
'  operations (Observe, hydration) take the write lock.
'  Hydration runs once at startup; steady-state writes are
'  rare (one per identity-bearing observation), so reads
'  dominate and reader-writer is the right pattern.
'
'  Lifecycle: registered as DI singleton in ManagerProgram.
'  HydrateAsync is called explicitly from ManagerProgram after
'  the DB migrations land and before any service that might
'  Observe is started. After hydration, the cache is populated
'  from the most recent PlayerActivity rows; subsequent
'  observations from the running Manager keep it current.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>
    ''' Identifies which kind of value a cache alias key carries.
    ''' Used both in the internal alias index and exposed via
    ''' FindByKey for diagnostic / lookup-by-known-name surfaces.
    ''' </summary>
    Public Enum IdentityKeyKind
        PlatformUserId
        CharacterId
        PlatformPersona
        DisplayName
    End Enum

    ''' <summary>
    ''' One identity observation contributed by some upstream
    ''' source — a parser event, a /players snapshot, a hydration
    ''' replay from PlayerActivity, a future plugin-side
    ''' authoritative-source read (e.g. Conan game.db hydration).
    ''' Any subset of fields may be populated; empty fields are
    ''' ignored by the merge logic.
    ''' </summary>
    Public Class IdentityObservation
        Public Property PlatformUserId As String
        Public Property CharacterId As String
        Public Property PlatformPersona As String
        Public Property DisplayName As String

        ''' <summary>
        ''' The platform the session is on (e.g. "Steam", "Xbox").
        ''' Carried as an identity attribute, not used as an alias
        ''' key — a platform name alone identifies no one. Stable
        ''' per identity in practice (an account lives on one
        ''' platform). Phase 5g-2d Round-3c-followup / 5d-2.
        ''' </summary>
        Public Property Platform As String

        ''' <summary>
        ''' Wall-clock timestamp at which this observation was
        ''' captured. Used for the newest-wins arbitration on
        ''' DisplayName and PlatformPersona renames. Default
        ''' DateTime.MinValue is interpreted as "no timestamp,
        ''' treat as oldest" so existing field values win
        ''' against an undated observation.
        ''' </summary>
        Public Property ObservedAtUtc As DateTime
    End Class

    ''' <summary>
    ''' The cache's canonical record for one resolved identity in
    ''' a given scope. Holds whichever identity fields the cache
    ''' has observed so far. Mutated only by the resolver under
    ''' its write lock; consumers receive copies via Enrich /
    ''' GetAllRecords / FindByKey.
    ''' </summary>
    Public Class IdentityRecord
        ''' <summary>The game-specific identifier (e.g. "lastoasis").</summary>
        Public Property GameId As String

        ''' <summary>
        ''' The within-game scope. Opaque to the resolver — the
        ''' plugin decides what makes a unique identity context.
        ''' Combined with GameId, two records with the same
        ''' (GameId, SessionScope) tuple are considered to live in
        ''' the same identity universe; different tuples are
        ''' isolated.
        ''' </summary>
        Public Property SessionScope As String

        Public Property PlatformUserId As String
        Public Property CharacterId As String
        Public Property PlatformPersona As String
        Public Property DisplayName As String

        ''' <summary>
        ''' Platform the identity is on (e.g. "Steam"). Carried
        ''' attribute, not an alias key. Surfaced via Enrich so
        ''' consumers (the /players panel, notification labels)
        ''' can render "character (Platform: persona)". Populated
        ''' only from live observations — PlayerActivity has no
        ''' Platform column, so a freshly-hydrated cache learns it
        ''' within a poll cycle of the player being online.
        ''' </summary>
        Public Property Platform As String

        ''' <summary>
        ''' Timestamp of the most-recent observation that touched
        ''' this record. Used for newest-wins arbitration on
        ''' rename-eligible fields.
        ''' </summary>
        Public Property LastObservedUtc As DateTime
    End Class

    ' ============================================================
    '  Internal alias key
    ' ============================================================

    ''' <summary>
    ''' Composite key for the alias index. Identifies an entry
    ''' as "for game G, in scope S, a record exists whose K-kind
    ''' value is V". Implemented as a Structure (value type) with
    ''' explicit equality so it works as a Dictionary key without
    ''' boxing.
    ''' </summary>
    Friend Structure AliasKey
        Implements IEquatable(Of AliasKey)

        Public ReadOnly GameId As String
        Public ReadOnly SessionScope As String
        Public ReadOnly Kind As IdentityKeyKind
        Public ReadOnly Value As String

        Public Sub New(gameId As String, sessionScope As String,
                       kind As IdentityKeyKind, value As String)
            Me.GameId = If(gameId, "")
            Me.SessionScope = If(sessionScope, "")
            Me.Kind = kind
            Me.Value = If(value, "")
        End Sub

        Public Overloads Function Equals(other As AliasKey) As Boolean Implements IEquatable(Of AliasKey).Equals
            Return GameId = other.GameId AndAlso
                   SessionScope = other.SessionScope AndAlso
                   Kind = other.Kind AndAlso
                   Value = other.Value
        End Function

        Public Overrides Function Equals(obj As Object) As Boolean
            If TypeOf obj Is AliasKey Then
                Return Equals(CType(obj, AliasKey))
            End If
            Return False
        End Function

        Public Overrides Function GetHashCode() As Integer
            ' System.HashCode.Combine handles the mixing and is
            ' overflow-safe. This matters in VB.Net specifically:
            ' integer overflow checking is ON by default (unlike
            ' C#, which silently wraps), so a manual
            ' h = h * 31 + component.GetHashCode() accumulator
            ' throws OverflowException once the component hash
            ' codes get large — which they do for real string
            ' values. (Empty strings hash to 0, so it survived an
            ' empty cache but blew up the moment hydration replayed
            ' a row with actual identity content.) Combine
            ' sidesteps the manual arithmetic entirely.
            Return HashCode.Combine(GameId, SessionScope, Kind, Value)
        End Function
    End Structure

    ' ============================================================
    '  IdentityResolver service
    ' ============================================================

    Public Class IdentityResolver

        Private ReadOnly _logger As ILogger(Of IdentityResolver)
        Private ReadOnly _lock As New ReaderWriterLockSlim()

        ''' <summary>
        ''' Canonical record list. Each record is unique; merges
        ''' and fusions collapse multiple records into one and
        ''' remove the absorbed ones. Reads under read lock,
        ''' writes under write lock.
        ''' </summary>
        Private ReadOnly _records As New List(Of IdentityRecord)

        ''' <summary>
        ''' Alias index: AliasKey -> the record currently
        ''' representing that alias. After a fusion, the absorbed
        ''' record's aliases get rewritten to point at the
        ''' surviving record. Same lock as _records.
        ''' </summary>
        Private ReadOnly _index As New Dictionary(Of AliasKey, IdentityRecord)

        Private _hydrated As Boolean = False

        Public Sub New(logger As ILogger(Of IdentityResolver))
            _logger = logger
        End Sub

        ' --------------------------------------------------------
        '  Public API
        ' --------------------------------------------------------

        ''' <summary>
        ''' Records an identity observation, merging into any
        ''' existing record(s) that share one or more aliases with
        ''' the observation, or creating a new record if no
        ''' aliases match. Idempotent: replaying the same
        ''' observation is a no-op.
        '''
        ''' gameId and sessionScope together define the identity
        ''' universe — observations under different (gameId,
        ''' sessionScope) tuples never merge with each other, even
        ''' if their PlatformUserId / CharacterId match (the same
        ''' Steam account on two different LO realms is
        ''' deliberately two different identities).
        ''' </summary>
        Public Sub Observe(gameId As String,
                           sessionScope As String,
                           observation As IdentityObservation)
            If observation Is Nothing Then Return
            If String.IsNullOrEmpty(gameId) Then Return

            ' sessionScope may be empty for plugins that haven't
            ' adopted scoping yet — treat empty as a single
            ' game-wide scope rather than rejecting the observation.
            Dim scope = If(sessionScope, "")

            Dim newKeys = BuildAliasKeys(gameId, scope, observation)
            If newKeys.Count = 0 Then Return  ' empty observation

            _lock.EnterWriteLock()
            Try
                ' Find existing records matched by any of the new
                ' aliases. ReferenceEqualityComparer ensures we
                ' dedup by identity rather than by content (two
                ' different records that happen to have identical
                ' fields are still two different records here).
                Dim matched As New HashSet(Of IdentityRecord)
                For Each k In newKeys
                    Dim existing As IdentityRecord = Nothing
                    If _index.TryGetValue(k, existing) Then
                        matched.Add(existing)
                    End If
                Next

                Dim target As IdentityRecord

                Select Case matched.Count
                    Case 0
                        ' Brand-new identity. Create a fresh record
                        ' carrying the observation's fields, indexed
                        ' under all its alias keys.
                        target = New IdentityRecord With {
                            .GameId = gameId,
                            .SessionScope = scope,
                            .PlatformUserId = observation.PlatformUserId,
                            .CharacterId = observation.CharacterId,
                            .PlatformPersona = observation.PlatformPersona,
                            .DisplayName = observation.DisplayName,
                            .Platform = observation.Platform,
                            .LastObservedUtc = observation.ObservedAtUtc
                        }
                        _records.Add(target)

                    Case 1
                        ' Single existing record — merge observation
                        ' into it. New alias keys (if any) get added
                        ' to the index at the end.
                        target = matched.First()
                        ApplyObservation(target, observation)

                    Case Else
                        ' Two or more existing records share aliases
                        ' with the observation — they're the same
                        ' identity. Fuse them into one (the first by
                        ' enumeration order is arbitrary but
                        ' deterministic for the duration of the
                        ' Observe call).
                        target = matched.First()
                        For Each other In matched.Skip(1)
                            FuseInto(target, other)
                            _records.Remove(other)
                        Next
                        ApplyObservation(target, observation)
                End Select

                ' Index any alias keys that weren't already pointing
                ' at target. This includes the brand-new-identity
                ' case where ALL keys need indexing.
                For Each k In newKeys
                    _index(k) = target
                Next
            Finally
                _lock.ExitWriteLock()
            End Try
        End Sub

        ''' <summary>
        ''' Returns a copy of the passed PlayerSession with any
        ''' fields the cache has resolved for the matching identity
        ''' filled in. The input session is not mutated; the
        ''' returned session is a new instance. If no record in
        ''' the cache shares any alias key with the input, the
        ''' returned copy is identity-preserving (just a clone).
        '''
        ''' Caller is responsible for supplying gameId and
        ''' sessionScope — the resolver doesn't infer them from
        ''' PlayerSession content.
        ''' </summary>
        Public Function Enrich(gameId As String,
                                sessionScope As String,
                                session As PlayerSession) As PlayerSession
            If session Is Nothing Then Return Nothing
            If String.IsNullOrEmpty(gameId) Then Return Clone(session)
            Dim scope = If(sessionScope, "")

            _lock.EnterReadLock()
            Try
                Dim rec = LookupBySessionKeys(gameId, scope, session)
                If rec Is Nothing Then Return Clone(session)

                Return New PlayerSession With {
                    .PlatformPersona = CoalesceField(session.PlatformPersona, rec.PlatformPersona),
                    .DisplayName = CoalesceField(session.DisplayName, rec.DisplayName),
                    .Platform = CoalesceField(session.Platform, rec.Platform),
                    .PlatformUserId = CoalesceField(session.PlatformUserId, rec.PlatformUserId),
                    .CharacterId = CoalesceField(session.CharacterId, rec.CharacterId),
                    .RemoteAddress = session.RemoteAddress,
                    .JoinedUtc = session.JoinedUtc
                }
            Finally
                _lock.ExitReadLock()
            End Try
        End Function

        ''' <summary>
        ''' Lookup-by-known-key surface. Used by /lastseen-style
        ''' commands where the caller has a name (or other single
        ''' field) and wants the resolver's best-guess identity
        ''' for it. Returns the matched record or Nothing.
        '''
        ''' Caller receives a snapshot copy — mutating the returned
        ''' record has no effect on the cache. This is intentional;
        ''' the cache is a write-controlled internal structure and
        ''' external mutation would bypass the merge invariants.
        ''' </summary>
        Public Function FindByKey(gameId As String,
                                   sessionScope As String,
                                   keyKind As IdentityKeyKind,
                                   keyValue As String) As IdentityRecord
            If String.IsNullOrEmpty(gameId) Then Return Nothing
            If String.IsNullOrEmpty(keyValue) Then Return Nothing
            Dim scope = If(sessionScope, "")

            Dim k = New AliasKey(gameId, scope, keyKind, keyValue)
            _lock.EnterReadLock()
            Try
                Dim rec As IdentityRecord = Nothing
                If _index.TryGetValue(k, rec) Then
                    Return CloneRecord(rec)
                End If
                Return Nothing
            Finally
                _lock.ExitReadLock()
            End Try
        End Function

        ''' <summary>
        ''' Convenience wrapper over Observe for callers holding a
        ''' combined SessionIdentity string (PlayerActivity.
        ''' SessionIdentity, InstanceManager.ResolveSessionIdentity
        ''' output, etc.) rather than separate gameId / sessionScope
        ''' values. Splits on the first colon — "gameId:scope" — and
        ''' delegates. The InstanceManager persistence + resync
        ''' paths use this; a future plugin that knows its gameId
        ''' and scope separately (e.g. Conan game.db hydration)
        ''' would call Observe directly.
        ''' </summary>
        Public Sub ObserveBySessionIdentity(sessionIdentity As String,
                                            observation As IdentityObservation)
            Dim gameId As String = Nothing
            Dim sessionScope As String = Nothing
            SplitSessionIdentity(sessionIdentity, gameId, sessionScope)
            Observe(gameId, sessionScope, observation)
        End Sub

        ''' <summary>
        ''' Convenience wrapper over Enrich for callers holding a
        ''' combined SessionIdentity string. Same split rule as
        ''' ObserveBySessionIdentity.
        ''' </summary>
        Public Function EnrichBySessionIdentity(sessionIdentity As String,
                                                session As PlayerSession) As PlayerSession
            Dim gameId As String = Nothing
            Dim sessionScope As String = Nothing
            SplitSessionIdentity(sessionIdentity, gameId, sessionScope)
            Return Enrich(gameId, sessionScope, session)
        End Function

        ''' <summary>
        ''' Total count of resolved records currently in the
        ''' cache. Useful for diagnostics and tests.
        ''' </summary>
        Public ReadOnly Property RecordCount As Integer
            Get
                _lock.EnterReadLock()
                Try
                    Return _records.Count
                Finally
                    _lock.ExitReadLock()
                End Try
            End Get
        End Property

        ''' <summary>
        ''' Snapshot of every record currently in the cache.
        ''' Returns a copy; the cache continues to mutate
        ''' independently after this call returns. Used by the
        ''' Tools menu diagnostic surface (Decision #5 in the
        ''' phase plan).
        ''' </summary>
        Public Function GetAllRecords() As IReadOnlyList(Of IdentityRecord)
            _lock.EnterReadLock()
            Try
                Return _records.Select(AddressOf CloneRecord).ToList()
            Finally
                _lock.ExitReadLock()
            End Try
        End Function

        ' --------------------------------------------------------
        '  Hydration
        ' --------------------------------------------------------

        ''' <summary>
        ''' Scans the PlayerActivity table for recent rows
        ''' carrying at least one populated identity column and
        ''' replays each as an observation. Idempotent — multiple
        ''' calls produce the same state as one call. Called
        ''' explicitly from ManagerProgram during startup, after
        ''' DB migrations and before any service that might
        ''' Observe.
        '''
        ''' Bounds: the most recent 5000 PlayerActivity rows
        ''' carrying any identity (DESC by TimestampUtc), filtered
        ''' to rows from the last 30 days. Configurable later if
        ''' real deployments need different windows.
        '''
        ''' SessionIdentity from the DB row is split on the first
        ''' colon to derive (gameId, sessionScope). Rows whose
        ''' SessionIdentity doesn't contain a colon fall through
        ''' with empty sessionScope — works fine, just less
        ''' isolated than expected.
        ''' </summary>
        Public Async Function HydrateAsync() As Task
            Const Limit = 5000
            Dim earliestUtc = DateTime.UtcNow.AddDays(-30)

            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.
                        GetRequiredService(Of GsmDbContext)()

                    Dim rows = Await db.PlayerActivity.
                        Where(Function(a) a.TimestampUtc >= earliestUtc).
                        Where(Function(a) a.SessionIdentity <> "" AndAlso
                                          (a.CharacterId <> "" OrElse
                                           a.PlatformUserId <> "" OrElse
                                           a.DisplayName <> "")).
                        OrderByDescending(Function(a) a.TimestampUtc).
                        Take(Limit).
                        ToListAsync()

                    For Each row In rows
                        Dim gameId As String = Nothing
                        Dim sessionScope As String = Nothing
                        SplitSessionIdentity(row.SessionIdentity, gameId, sessionScope)
                        If String.IsNullOrEmpty(gameId) Then Continue For

                        Dim obs = New IdentityObservation With {
                            .PlatformUserId = row.PlatformUserId,
                            .CharacterId = row.CharacterId,
                            .PlatformPersona = row.PlayerName,
                            .DisplayName = row.DisplayName,
                            .ObservedAtUtc = DateTime.SpecifyKind(
                                row.TimestampUtc, DateTimeKind.Utc)
                        }
                        Observe(gameId, sessionScope, obs)
                    Next

                    _hydrated = True
                    _logger.LogInformation(
                        "IdentityResolver hydrated from PlayerActivity: scanned {Scanned} rows, holding {Records} records",
                        rows.Count, RecordCount)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "IdentityResolver hydration failed; cache will fill from live observations only")
            End Try
        End Function

        ''' <summary>True after HydrateAsync has completed at
        ''' least once. Exposed for diagnostic surfaces only;
        ''' Enrich and Observe work correctly regardless of
        ''' hydration state (the cache is just emptier).</summary>
        Public ReadOnly Property IsHydrated As Boolean
            Get
                Return _hydrated
            End Get
        End Property

        ' --------------------------------------------------------
        '  Internal helpers
        ' --------------------------------------------------------

        ''' <summary>
        ''' Builds the list of alias keys an observation
        ''' contributes. Empty fields are skipped — they don't
        ''' generate alias entries. Result list size is between
        ''' 0 (no fields) and 4 (all four).
        ''' </summary>
        Private Shared Function BuildAliasKeys(gameId As String,
                                                sessionScope As String,
                                                obs As IdentityObservation) As List(Of AliasKey)
            Dim keys As New List(Of AliasKey)
            If Not String.IsNullOrEmpty(obs.PlatformUserId) Then
                keys.Add(New AliasKey(gameId, sessionScope, IdentityKeyKind.PlatformUserId, obs.PlatformUserId))
            End If
            If Not String.IsNullOrEmpty(obs.CharacterId) Then
                keys.Add(New AliasKey(gameId, sessionScope, IdentityKeyKind.CharacterId, obs.CharacterId))
            End If
            If Not String.IsNullOrEmpty(obs.PlatformPersona) Then
                keys.Add(New AliasKey(gameId, sessionScope, IdentityKeyKind.PlatformPersona, obs.PlatformPersona))
            End If
            If Not String.IsNullOrEmpty(obs.DisplayName) Then
                keys.Add(New AliasKey(gameId, sessionScope, IdentityKeyKind.DisplayName, obs.DisplayName))
            End If
            Return keys
        End Function

        ''' <summary>
        ''' Field-level merge of an observation into an existing
        ''' record. Caller holds the write lock.
        ''' </summary>
        Private Sub ApplyObservation(rec As IdentityRecord,
                                      obs As IdentityObservation)
            ' PlatformUserId / CharacterId: never change for the
            ' same identity. Fill if empty; warn on conflict and
            ' keep existing value.
            If Not String.IsNullOrEmpty(obs.PlatformUserId) Then
                If String.IsNullOrEmpty(rec.PlatformUserId) Then
                    rec.PlatformUserId = obs.PlatformUserId
                ElseIf rec.PlatformUserId <> obs.PlatformUserId Then
                    _logger.LogWarning(
                        "IdentityResolver: PlatformUserId conflict in {Game}:{Scope} — keeping {Existing}, ignoring {New}",
                        rec.GameId, rec.SessionScope,
                        rec.PlatformUserId, obs.PlatformUserId)
                End If
            End If

            If Not String.IsNullOrEmpty(obs.CharacterId) Then
                If String.IsNullOrEmpty(rec.CharacterId) Then
                    rec.CharacterId = obs.CharacterId
                ElseIf rec.CharacterId <> obs.CharacterId Then
                    _logger.LogWarning(
                        "IdentityResolver: CharacterId conflict in {Game}:{Scope} — keeping {Existing}, ignoring {New}",
                        rec.GameId, rec.SessionScope,
                        rec.CharacterId, obs.CharacterId)
                End If
            End If

            ' Platform: stable per identity (a given account lives on
            ' one platform). Fill if empty; warn on the
            ' should-never-happen conflict and keep existing. Not an
            ' alias key — carried as an attribute, never indexed.
            ' Comparison is case-INSENSITIVE: the same platform
            ' arrives with different casings from different log-line
            ' sources (LO Login request says "STEAM", Persisting says
            ' "Steam"), and a case-only difference is the same
            ' platform, not a conflict — treating it as one re-warned
            ' on every enrich pass for as long as the player stayed
            ' connected.
            If Not String.IsNullOrEmpty(obs.Platform) Then
                If String.IsNullOrEmpty(rec.Platform) Then
                    rec.Platform = obs.Platform
                ElseIf Not String.Equals(rec.Platform, obs.Platform, StringComparison.OrdinalIgnoreCase) Then
                    _logger.LogWarning(
                        "IdentityResolver: Platform conflict in {Game}:{Scope} — keeping {Existing}, ignoring {New}",
                        rec.GameId, rec.SessionScope,
                        rec.Platform, obs.Platform)
                End If
            End If

            ' DisplayName / PlatformPersona: newest-wins. Fill if
            ' empty regardless of timestamp; otherwise update only
            ' if the observation is at least as recent as the
            ' current value.
            If Not String.IsNullOrEmpty(obs.PlatformPersona) Then
                If String.IsNullOrEmpty(rec.PlatformPersona) OrElse
                   obs.ObservedAtUtc >= rec.LastObservedUtc Then
                    rec.PlatformPersona = obs.PlatformPersona
                End If
            End If

            If Not String.IsNullOrEmpty(obs.DisplayName) Then
                If String.IsNullOrEmpty(rec.DisplayName) OrElse
                   obs.ObservedAtUtc >= rec.LastObservedUtc Then
                    rec.DisplayName = obs.DisplayName
                End If
            End If

            ' Bump LastObservedUtc if the observation is newer.
            If obs.ObservedAtUtc > rec.LastObservedUtc Then
                rec.LastObservedUtc = obs.ObservedAtUtc
            End If
        End Sub

        ''' <summary>
        ''' Fuses 'other' into 'primary'. Caller holds the write
        ''' lock. Other's fields are merged via the same rules
        ''' Observe uses, and other's alias-index entries are
        ''' rewritten to point at primary. The caller is
        ''' responsible for removing 'other' from _records.
        ''' </summary>
        Private Sub FuseInto(primary As IdentityRecord,
                              other As IdentityRecord)
            ' Synthesise an observation from other and apply it.
            Dim synth As New IdentityObservation With {
                .PlatformUserId = other.PlatformUserId,
                .CharacterId = other.CharacterId,
                .PlatformPersona = other.PlatformPersona,
                .DisplayName = other.DisplayName,
                .Platform = other.Platform,
                .ObservedAtUtc = other.LastObservedUtc
            }
            ApplyObservation(primary, synth)

            ' Redirect every alias that currently points at other
            ' to point at primary instead. Snapshot the keys first
            ' since we're mutating the dict.
            Dim toRedirect = _index.
                Where(Function(kv) ReferenceEquals(kv.Value, other)).
                Select(Function(kv) kv.Key).
                ToList()
            For Each k In toRedirect
                _index(k) = primary
            Next
        End Sub

        ''' <summary>
        ''' Looks up the record matching any non-empty alias from
        ''' the passed PlayerSession. Caller holds the read lock.
        ''' Returns the first match (sessions are expected to
        ''' represent one identity, so multiple matches indicates
        ''' a pre-fusion state that the next Observe will clean
        ''' up).
        ''' </summary>
        Private Function LookupBySessionKeys(gameId As String,
                                              sessionScope As String,
                                              session As PlayerSession) As IdentityRecord
            Dim rec As IdentityRecord = Nothing

            If Not String.IsNullOrEmpty(session.PlatformUserId) AndAlso
               _index.TryGetValue(New AliasKey(gameId, sessionScope,
                                                IdentityKeyKind.PlatformUserId,
                                                session.PlatformUserId), rec) Then
                Return rec
            End If
            If Not String.IsNullOrEmpty(session.CharacterId) AndAlso
               _index.TryGetValue(New AliasKey(gameId, sessionScope,
                                                IdentityKeyKind.CharacterId,
                                                session.CharacterId), rec) Then
                Return rec
            End If
            If Not String.IsNullOrEmpty(session.PlatformPersona) AndAlso
               _index.TryGetValue(New AliasKey(gameId, sessionScope,
                                                IdentityKeyKind.PlatformPersona,
                                                session.PlatformPersona), rec) Then
                Return rec
            End If
            If Not String.IsNullOrEmpty(session.DisplayName) AndAlso
               _index.TryGetValue(New AliasKey(gameId, sessionScope,
                                                IdentityKeyKind.DisplayName,
                                                session.DisplayName), rec) Then
                Return rec
            End If

            Return Nothing
        End Function

        ''' <summary>
        ''' Splits a SessionIdentity string of the form
        ''' "gameId:sessionScope" into its two parts. Strings
        ''' without a colon yield gameId only (sessionScope is
        ''' empty), which is a tolerable degraded state — the
        ''' resolver still works, just with less scope isolation.
        ''' </summary>
        Private Shared Sub SplitSessionIdentity(sessionIdentity As String,
                                                  ByRef gameId As String,
                                                  ByRef sessionScope As String)
            gameId = ""
            sessionScope = ""
            If String.IsNullOrEmpty(sessionIdentity) Then Return

            Dim colonIdx = sessionIdentity.IndexOf(":"c)
            If colonIdx < 0 Then
                gameId = sessionIdentity
                Return
            End If
            gameId = sessionIdentity.Substring(0, colonIdx)
            sessionScope = sessionIdentity.Substring(colonIdx + 1)
        End Sub

        ''' <summary>
        ''' "Prefer existing non-empty; fall back to candidate."
        ''' Used by Enrich to populate enriched-PlayerSession
        ''' fields while preserving any explicit values the
        ''' caller already had.
        ''' </summary>
        Private Shared Function CoalesceField(existing As String,
                                                candidate As String) As String
            If Not String.IsNullOrEmpty(existing) Then Return existing
            Return candidate
        End Function

        ''' <summary>Shallow copy of a PlayerSession — used when
        ''' Enrich has nothing to add but still must return a
        ''' fresh instance per the immutable-output contract.</summary>
        Private Shared Function Clone(session As PlayerSession) As PlayerSession
            Return New PlayerSession With {
                .PlatformPersona = session.PlatformPersona,
                .DisplayName = session.DisplayName,
                .Platform = session.Platform,
                .PlatformUserId = session.PlatformUserId,
                .CharacterId = session.CharacterId,
                .RemoteAddress = session.RemoteAddress,
                .JoinedUtc = session.JoinedUtc
            }
        End Function

        ''' <summary>Shallow copy of an IdentityRecord — used by
        ''' FindByKey and GetAllRecords so callers can't mutate
        ''' cache state by editing returned objects.</summary>
        Private Shared Function CloneRecord(rec As IdentityRecord) As IdentityRecord
            Return New IdentityRecord With {
                .GameId = rec.GameId,
                .SessionScope = rec.SessionScope,
                .PlatformUserId = rec.PlatformUserId,
                .CharacterId = rec.CharacterId,
                .PlatformPersona = rec.PlatformPersona,
                .DisplayName = rec.DisplayName,
                .Platform = rec.Platform,
                .LastObservedUtc = rec.LastObservedUtc
            }
        End Function

    End Class

End Namespace
