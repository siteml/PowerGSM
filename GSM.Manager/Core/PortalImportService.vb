Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Utility
Imports GSM.Manager.Data

Namespace GSM.Manager.Core

    ''' <summary>What importing a record would do to the shared-config
    ''' store.</summary>
    Public Enum PortalImportAction
        ''' <summary>No existing group matches the record's identity
        ''' fields — a new group will be created.</summary>
        CreateNew
        ''' <summary>A group matches but its name or field values
        ''' differ — it will be updated.</summary>
        Update
        ''' <summary>A group matches and already has identical name +
        ''' fields — importing would change nothing.</summary>
        Unchanged
    End Enum

    ''' <summary>One classified record in an import plan. The UI shows
    ''' these (with checkboxes) and passes the chosen subset back to
    ''' ApplyImportPlan.</summary>
    Public Class PortalImportPlanItem
        Public Property Record As WebPortalImportRecord
        Public Property Action As PortalImportAction
        ''' <summary>The display name the group will get (the record's
        ''' SuggestedDisplayName, resolved).</summary>
        Public Property DisplayName As String
        ''' <summary>Set for Update/Unchanged — the matched group id.</summary>
        Public Property ExistingGroupId As String
        ''' <summary>Set for Update/Unchanged — its current display name
        ''' (so the UI can show the rename).</summary>
        Public Property ExistingDisplayName As String
    End Class

    ' ============================================================
    '  PortalImportService — Phase 7-6
    '
    '  Turns the generic WebPortalImportRecords a utility plugin
    '  scraped (via IWebPortalDataProvider, routed through
    '  UtilityPluginHost.DiscoverAllPortalRecordsAsync) into 5h
    '  shared-config groups.
    '
    '  The matching is GENERIC: a record names its target game
    '  plugin + shared-config key and the field keys that constitute
    '  its identity (MatchFieldKeys). This service matches a record
    '  against existing groups of that plugin/key on ALL of those
    '  fields (decrypted plaintext, Ordinal) — full match => update
    '  that group, no match => create a new one. It never hard-codes
    '  a game field name, so it works for any future portal/plugin,
    '  not just Last Oasis realms.
    '
    '  Per-provider-key design (7-6): lo-myrealm emits one record per
    '  (CustomerKey, ProviderKey) pair with those two as the match
    '  keys, so a realm hosted from several providers becomes several
    '  groups sharing a RealmName but differing by ProviderKey — no
    '  list-typed shared-config schema needed.
    ' ============================================================

    Public Class PortalImportService

        Private ReadOnly _registry As PluginRegistry
        Private ReadOnly _sharedConfig As SharedConfigService
        Private ReadOnly _logger As ILogger(Of PortalImportService)

        Public Sub New(registry As PluginRegistry,
                       sharedConfig As SharedConfigService,
                       logger As ILogger(Of PortalImportService))
            _registry = registry
            _sharedConfig = sharedConfig
            _logger = logger
        End Sub

        ''' <summary>Classify each record as CreateNew / Update /
        ''' Unchanged against the existing shared-config groups.
        ''' Records whose target game plugin isn't loaded or doesn't
        ''' define shared config are skipped (logged). Never throws.</summary>
        Public Function ComputeImportPlan(db As GsmDbContext,
                                          records As IEnumerable(Of WebPortalImportRecord)) As IReadOnlyList(Of PortalImportPlanItem)
            Dim plan As New List(Of PortalImportPlanItem)
            If records Is Nothing Then Return plan

            ' Cache schema + existing-group snapshots per (gameId, key)
            ' so a batch of records for one realm-type does one lookup.
            Dim schemaCache As New Dictionary(Of String, IReadOnlyList(Of ConfigFieldDescriptor))(StringComparer.OrdinalIgnoreCase)
            Dim groupsCache As New Dictionary(Of String, List(Of GroupSnapshot))(StringComparer.OrdinalIgnoreCase)

            For Each record In records
                If record Is Nothing OrElse String.IsNullOrEmpty(record.GameId) OrElse
                   String.IsNullOrEmpty(record.SharedConfigKey) Then Continue For
                Dim cacheKey = record.GameId & "|" & record.SharedConfigKey

                Dim schema As IReadOnlyList(Of ConfigFieldDescriptor) = Nothing
                If Not schemaCache.TryGetValue(cacheKey, schema) Then
                    schema = ResolveSchema(record.GameId)
                    schemaCache(cacheKey) = schema
                End If
                If schema Is Nothing Then
                    _logger.LogWarning(
                        "Portal import: no loaded shared-config provider for game '{Game}' — skipping a '{Key}' record",
                        record.GameId, record.SharedConfigKey)
                    Continue For
                End If

                Dim existing As List(Of GroupSnapshot) = Nothing
                If Not groupsCache.TryGetValue(cacheKey, existing) Then
                    existing = LoadGroupSnapshots(db, record.GameId, record.SharedConfigKey, schema)
                    groupsCache(cacheKey) = existing
                End If

                plan.Add(ClassifyRecord(record, existing))
            Next
            Return plan
        End Function

        ''' <summary>Apply the chosen items (Unchanged skipped). Each
        ''' create/update persists immediately via SharedConfigService.
        ''' Returns counts for the UI summary. Never throws — a single
        ''' failing item is logged and skipped.</summary>
        Public Function ApplyImportPlan(db As GsmDbContext,
                                        items As IEnumerable(Of PortalImportPlanItem)) As (Created As Integer, Updated As Integer)
            Dim created = 0
            Dim updated = 0
            If items Is Nothing Then Return (created, updated)

            Dim schemaCache As New Dictionary(Of String, IReadOnlyList(Of ConfigFieldDescriptor))(StringComparer.OrdinalIgnoreCase)

            For Each item In items
                If item Is Nothing OrElse item.Record Is Nothing Then Continue For
                If item.Action = PortalImportAction.Unchanged Then Continue For
                Dim record = item.Record

                Dim schema As IReadOnlyList(Of ConfigFieldDescriptor) = Nothing
                If Not schemaCache.TryGetValue(record.GameId, schema) Then
                    schema = ResolveSchema(record.GameId)
                    schemaCache(record.GameId) = schema
                End If
                If schema Is Nothing Then Continue For

                Dim displayName = If(Not String.IsNullOrEmpty(item.DisplayName),
                                     item.DisplayName, record.SuggestedDisplayName)

                Try
                    If item.Action = PortalImportAction.CreateNew Then
                        _sharedConfig.CreateGroup(db, record.GameId, record.SharedConfigKey,
                                                  displayName, record.Fields, schema)
                        created += 1
                    ElseIf item.Action = PortalImportAction.Update AndAlso
                           Not String.IsNullOrEmpty(item.ExistingGroupId) Then
                        _sharedConfig.UpdateGroup(db, item.ExistingGroupId,
                                                  displayName, record.Fields, schema)
                        updated += 1
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex, "Portal import: failed to {Action} group '{Name}'",
                                       item.Action, displayName)
                End Try
            Next
            Return (created, updated)
        End Function

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Function ResolveSchema(gameId As String) As IReadOnlyList(Of ConfigFieldDescriptor)
            If _registry Is Nothing OrElse String.IsNullOrEmpty(gameId) Then Return Nothing
            Dim plugin = _registry.GetPlugin(gameId)
            Dim provider = TryCast(plugin, ISharedConfigProvider)
            If provider Is Nothing Then Return Nothing
            Try
                Return provider.GetSharedConfigSchema()
            Catch ex As Exception
                _logger.LogWarning(ex, "Portal import: shared-config schema threw for game '{Game}'", gameId)
                Return Nothing
            End Try
        End Function

        ''' <summary>Existing group + its decrypted fields, snapshotted
        ''' once for matching.</summary>
        Private Class GroupSnapshot
            Public Property GroupId As String
            Public Property DisplayName As String
            Public Property Fields As Dictionary(Of String, String)
        End Class

        Private Function LoadGroupSnapshots(db As GsmDbContext,
                                            gameId As String,
                                            sharedConfigKey As String,
                                            schema As IReadOnlyList(Of ConfigFieldDescriptor)) As List(Of GroupSnapshot)
            Dim snapshots As New List(Of GroupSnapshot)
            For Each g In _sharedConfig.ListGroups(db, gameId, sharedConfigKey)
                snapshots.Add(New GroupSnapshot With {
                    .GroupId = g.GroupId,
                    .DisplayName = g.DisplayName,
                    .Fields = _sharedConfig.LoadGroupFieldsPlaintext(db, g.GroupId, schema)
                })
            Next
            Return snapshots
        End Function

        Private Shared Function ClassifyRecord(record As WebPortalImportRecord,
                                               existing As List(Of GroupSnapshot)) As PortalImportPlanItem
            Dim displayName = If(Not String.IsNullOrEmpty(record.SuggestedDisplayName),
                                 record.SuggestedDisplayName, BestEffortName(record))
            Dim item As New PortalImportPlanItem With {
                .Record = record,
                .DisplayName = displayName
            }

            Dim match = FindMatch(record, existing)
            If match Is Nothing Then
                item.Action = PortalImportAction.CreateNew
                Return item
            End If

            item.ExistingGroupId = match.GroupId
            item.ExistingDisplayName = match.DisplayName
            item.Action = If(GroupMatchesRecord(record, displayName, match),
                             PortalImportAction.Unchanged, PortalImportAction.Update)
            Return item
        End Function

        ''' <summary>The existing group whose plaintext values equal the
        ''' record's on ALL MatchFieldKeys (Ordinal). Nothing if none
        ''' matches, or the record declared no match keys (then it's
        ''' treated as new — defensive).</summary>
        Private Shared Function FindMatch(record As WebPortalImportRecord,
                                          existing As List(Of GroupSnapshot)) As GroupSnapshot
            If record.MatchFieldKeys Is Nothing OrElse record.MatchFieldKeys.Count = 0 Then Return Nothing
            For Each snap In existing
                Dim allMatch = True
                For Each key In record.MatchFieldKeys
                    Dim recVal As String = Nothing
                    record.Fields.TryGetValue(key, recVal)
                    Dim grpVal As String = Nothing
                    snap.Fields.TryGetValue(key, grpVal)
                    If Not String.Equals(If(recVal, ""), If(grpVal, ""), StringComparison.Ordinal) Then
                        allMatch = False
                        Exit For
                    End If
                Next
                If allMatch Then Return snap
            Next
            Return Nothing
        End Function

        ''' <summary>True when the matched group already has the record's
        ''' display name AND every record field value — re-importing
        ''' would change nothing.</summary>
        Private Shared Function GroupMatchesRecord(record As WebPortalImportRecord,
                                                   displayName As String,
                                                   snap As GroupSnapshot) As Boolean
            If Not String.Equals(If(displayName, ""), If(snap.DisplayName, ""), StringComparison.Ordinal) Then Return False
            For Each kvp In record.Fields
                Dim grpVal As String = Nothing
                snap.Fields.TryGetValue(kvp.Key, grpVal)
                If Not String.Equals(If(kvp.Value, ""), If(grpVal, ""), StringComparison.Ordinal) Then Return False
            Next
            Return True
        End Function

        Private Shared Function BestEffortName(record As WebPortalImportRecord) As String
            Return If(Not String.IsNullOrEmpty(record.SourceRef), record.SourceRef, "(imported)")
        End Function

    End Class

End Namespace
