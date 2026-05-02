Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text
Imports System.Text.Json
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Data
Imports GSM.Plugin

' ============================================================
'  PortAllocator — suggests port values for new instances and
'  validates port assignments against the rest of the node.
'
'  Two responsibilities, intentionally bundled:
'
'    1. SUGGESTION (SuggestPortsForNewInstance):
'         Best-effort "what's a good default?" answer. Form
'         pre-fills with these so the user doesn't have to
'         hand-pick free ports for each new instance.
'
'    2. VALIDATION (FindPortConflicts):
'         Hard "is this config safe to save?" answer. Run on
'         OnSave for any form that takes port values, after
'         the user has had their chance to edit the suggested
'         values. Returns a list of detected conflicts; the
'         caller decides whether to warn-and-confirm or block.
'
'  Why bundled: both operations need exactly the same input
'  (the global port-usage list across all instances on a
'  node) and use the same plugin-schema lookup. Splitting
'  them would mean duplicating the relatively expensive
'  CollectAllPortsOnNode helper.
'
'  Allocation is per-node, not per-plugin
'  =====================================================
'
'  An earlier draft scoped allocation to "same plugin, same
'  node" on the assumption that different games would use
'  non-overlapping port spaces (Factorio's 34197, LO's 5555,
'  etc). That assumption falls apart fast: most operators
'  rebase ports into a per-host range like 7777-7900 and
'  share that space across every game. A new Factorio
'  instance starting at default 34197 would dodge a check
'  scoped to "same plugin", but would still collide with an
'  LO instance that the user moved to e.g. RconPort=34197.
'
'  The current algorithm walks the plugin-declared port
'  fields of EVERY instance on the node, regardless of game,
'  to build the global "in use" set. Suggestions and
'  validation both consult that set.
'
'  Allocation handles two real-world layout patterns
'  without the user having to declare which they're using:
'
'    A. Per-port-type ranges (the conventional pattern):
'         Port      = 7777, 7778, 7779, ...
'         RconPort  = 8001, 8002, 8003, ...
'         QueryPort = 27015, 27016, 27017, ...
'       Allocator: max+1 per field. Each field has its
'       own range so max+1 lands in that range. ✓
'
'    B. Per-instance clustered (each instance gets a small
'       contiguous block):
'         Instance 1: Port=7777, RconPort=7778, QueryPort=7779
'         Instance 2: Port=7780, RconPort=7781, QueryPort=7782
'       Allocator: max+1 for Port = 7780 (good). For
'       RconPort, max-same-key is 7778 → +1 = 7779, but
'       7779 is taken globally by instance 1's QueryPort,
'       so the bump-until-free walks to 7781. Same for
'       QueryPort: max-same-key = 7779 → +1 = 7780, taken
'       by instance 2's Port we just allocated, walks to
'       7782. ✓
'
'  Same-plugin same-key max+1 (rather than blind global max+1)
'  preserves user intent when patterns DO have per-key ranges:
'  with pattern A above, allocating a new RconPort by global
'  max would jump to 27018 (past the QueryPort range), but
'  same-key max+1 correctly picks 8004.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>
    ''' One observed port assignment on the node. Carries enough
    ''' identity (instance + key) to render a clear conflict
    ''' message back to the user.
    ''' </summary>
    Public Class PortObservation
        Public Property InstanceId As String
        Public Property InstanceName As String
        Public Property GameId As String
        Public Property Key As String
        Public Property Port As Integer
    End Class

    ''' <summary>
    ''' A detected port conflict. Either a duplicate port between
    ''' two fields on the proposed config (intra-config, where
    ''' ConflictingInstanceId is empty), or a clash with a port
    ''' already in use by another instance on the same node.
    ''' </summary>
    Public Class PortConflict
        Public Property ProposedKey As String
        Public Property ProposedPort As Integer
        ''' <summary>Empty when the conflict is intra-config (two
        ''' fields on the proposed instance share a port).</summary>
        Public Property ConflictingInstanceId As String
        Public Property ConflictingInstanceName As String
        Public Property ConflictingKey As String
    End Class

    Public Class PortAllocator

        ' ============================================================
        '  Public API
        ' ============================================================

        ''' <summary>
        ''' Suggest port values for a new instance of the given plugin
        ''' on the given node. Returns a dictionary of field-key →
        ''' string-formatted port. Caller passes this to
        ''' SchemaFormBuilder as the `existing` dict to pre-fill the
        ''' form, or merges directly into ConfigJson when creating
        ''' silently (NewInstallationForm's "Create first instance").
        '''
        ''' Algorithm: per-field baseline = max(same-plugin same-key)
        ''' + 1, or the field's DefaultValue when no same-plugin
        ''' instances exist. Then bump-until-free against the global
        ''' node-wide port set, also avoiding ports allocated
        ''' earlier in this same call so a clustered-pattern user
        ''' doesn't get a within-call self-collision.
        '''
        ''' Returns an empty dict for keys where the plugin has no
        ''' port fields, no usable default exists, or every port in
        ''' the field's [MinValue, MaxValue] range is taken. Caller
        ''' should treat absent keys as "use the schema's default",
        ''' which is what SchemaFormBuilder.Build already does.
        ''' </summary>
        Public Shared Function SuggestPortsForNewInstance(
                plugin As IGamePlugin,
                nodeId As String,
                db As GsmDbContext) As Dictionary(Of String, String)

            Dim suggestions As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            If plugin Is Nothing OrElse db Is Nothing OrElse String.IsNullOrEmpty(nodeId) Then
                Return suggestions
            End If

            Dim schema As IReadOnlyList(Of ConfigFieldDescriptor) = Nothing
            Try
                schema = plugin.GetInstanceConfigSchema()
            Catch
                Return suggestions
            End Try
            If schema Is Nothing OrElse schema.Count = 0 Then Return suggestions
            Dim portFields = schema.Where(Function(f) f IsNot Nothing AndAlso f.IsPort).ToList()
            If portFields.Count = 0 Then Return suggestions

            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            Dim observations = CollectAllPortsOnNode(nodeId, "", db, registry)

            ' HashSet for O(1) collision check during the bump-until-
            ' free loop. Includes ports allocated earlier in this call
            ' so the second port field doesn't reuse the first's
            ' newly-allocated value.
            Dim taken As New HashSet(Of Integer)(observations.Select(Function(o) o.Port))

            For Each field In portFields
                Dim baseline As Integer? = ComputeBaseline(field, plugin.GameId, observations)
                If Not baseline.HasValue Then Continue For

                Dim minAllowed = If(field.MinValue, 1)
                Dim maxAllowed = If(field.MaxValue, 65535)

                Dim candidate = Math.Max(baseline.Value, minAllowed)

                ' Bump until we find a free port. The cap protects
                ' against pathological cases (every port taken in the
                ' field's range) — bail without a suggestion rather
                ' than infinite-loop.
                While candidate <= maxAllowed AndAlso taken.Contains(candidate)
                    candidate += 1
                End While

                If candidate > maxAllowed Then
                    ' No port available — skip suggestion. The form's
                    ' DefaultValue rendering will take over, and the
                    ' user is told via FindPortConflicts at save time
                    ' if the default itself collides.
                    Continue For
                End If

                suggestions(field.Key) = candidate.ToString()
                taken.Add(candidate)
            Next

            Return suggestions
        End Function

        ''' <summary>
        ''' Validate a proposed instance config against every other
        ''' instance on the same node. Returns a list of conflicts
        ''' (empty = config is safe). Detects two kinds of conflict:
        '''
        '''   1. Intra-config: two fields on the proposed instance
        '''      have the same port value (e.g. user typed Port=7777
        '''      and forgot to change RconPort=7777). Reported with
        '''      ConflictingInstanceId = "" and ConflictingInstanceName
        '''      = "this same instance".
        '''
        '''   2. Cross-instance: a proposed port matches a port
        '''      already in use by another instance on the same node,
        '''      regardless of which game that other instance runs.
        '''
        ''' Pass selfInstanceId when validating an EDIT (so the
        ''' instance's own current ports don't appear as conflicts
        ''' against its proposed ones). Pass empty string for ADD.
        ''' </summary>
        Public Shared Function FindPortConflicts(
                plugin As IGamePlugin,
                nodeId As String,
                selfInstanceId As String,
                proposedConfig As Dictionary(Of String, String),
                db As GsmDbContext) As List(Of PortConflict)

            Dim conflicts As New List(Of PortConflict)
            If plugin Is Nothing OrElse db Is Nothing OrElse
               String.IsNullOrEmpty(nodeId) OrElse proposedConfig Is Nothing Then
                Return conflicts
            End If

            Dim schema As IReadOnlyList(Of ConfigFieldDescriptor) = Nothing
            Try
                schema = plugin.GetInstanceConfigSchema()
            Catch
                Return conflicts
            End Try
            If schema Is Nothing Then Return conflicts

            ' Pull each port-typed field's current value from the
            ' proposed config. Skip fields that aren't present /
            ' aren't parseable — they'll be caught by other
            ' validation if required.
            Dim ciProposed As New Dictionary(Of String, String)(
                proposedConfig, StringComparer.OrdinalIgnoreCase)
            Dim proposedPorts As New List(Of (Key As String, Port As Integer))
            For Each f In schema
                If f Is Nothing OrElse Not f.IsPort Then Continue For
                Dim raw As String = Nothing
                If Not ciProposed.TryGetValue(f.Key, raw) Then Continue For
                If String.IsNullOrWhiteSpace(raw) Then Continue For
                Dim n As Integer
                If Not Integer.TryParse(raw.Trim(), n) Then Continue For
                proposedPorts.Add((f.Key, n))
            Next
            If proposedPorts.Count = 0 Then Return conflicts

            ' 1. Intra-config: compare every pair on the proposed
            ' instance. Order by Key for stable output so users see
            ' the same conflict listed the same way each time.
            For i = 0 To proposedPorts.Count - 1
                For j = i + 1 To proposedPorts.Count - 1
                    If proposedPorts(i).Port = proposedPorts(j).Port Then
                        conflicts.Add(New PortConflict With {
                            .ProposedKey = proposedPorts(i).Key,
                            .ProposedPort = proposedPorts(i).Port,
                            .ConflictingInstanceId = "",
                            .ConflictingInstanceName = "this same instance",
                            .ConflictingKey = proposedPorts(j).Key
                        })
                    End If
                Next
            Next

            ' 2. Cross-instance: compare against every observation
            ' from the rest of the node.
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            Dim observations = CollectAllPortsOnNode(nodeId, selfInstanceId, db, registry)

            For Each pp In proposedPorts
                For Each obs In observations
                    If obs.Port = pp.Port Then
                        conflicts.Add(New PortConflict With {
                            .ProposedKey = pp.Key,
                            .ProposedPort = pp.Port,
                            .ConflictingInstanceId = obs.InstanceId,
                            .ConflictingInstanceName = obs.InstanceName,
                            .ConflictingKey = obs.Key
                        })
                    End If
                Next
            Next

            Return conflicts
        End Function

        ''' <summary>
        ''' Render a conflict list as a multi-line message body
        ''' suitable for a MessageBox. Returns empty string for an
        ''' empty list so the caller can use the result as a guard
        ''' (no message → no conflicts).
        ''' </summary>
        Public Shared Function FormatConflictsForDisplay(conflicts As List(Of PortConflict)) As String
            If conflicts Is Nothing OrElse conflicts.Count = 0 Then Return ""
            Dim sb As New StringBuilder()
            For Each c In conflicts
                If String.IsNullOrEmpty(c.ConflictingInstanceId) Then
                    ' Intra-config — both keys are on the proposed
                    ' instance.
                    sb.Append("  • Port ").Append(c.ProposedPort).
                       Append(": ").Append(c.ProposedKey).
                       Append(" and ").Append(c.ConflictingKey).
                       AppendLine(" on this instance share the same value")
                Else
                    sb.Append("  • Port ").Append(c.ProposedPort).
                       Append(" (").Append(c.ProposedKey).
                       Append(") is already used by ").
                       Append(c.ConflictingInstanceName).
                       Append(" (").Append(c.ConflictingKey).AppendLine(")")
                End If
            Next
            Return sb.ToString()
        End Function

        ' ============================================================
        '  Internal helpers
        ' ============================================================

        ''' <summary>
        ''' Per-field baseline for SuggestPortsForNewInstance: the
        ''' starting candidate before bump-until-free. Same-plugin
        ''' same-key max+1 is the right anchor for both layout
        ''' patterns the algorithm aims to support — see the file
        ''' header comment for the analysis.
        ''' </summary>
        Private Shared Function ComputeBaseline(
                field As ConfigFieldDescriptor,
                gameId As String,
                observations As List(Of PortObservation)) As Integer?
            Dim sameKeySamePluginMax = observations.
                Where(Function(o) String.Equals(o.GameId, gameId,
                                                  StringComparison.OrdinalIgnoreCase) AndAlso
                                  String.Equals(o.Key, field.Key,
                                                  StringComparison.OrdinalIgnoreCase)).
                Select(Function(o) CType(o.Port, Integer?)).
                Max()
            If sameKeySamePluginMax.HasValue Then
                Return sameKeySamePluginMax.Value + 1
            End If
            ' No same-plugin same-key precedent — try the field's
            ' DefaultValue. If that's missing or non-numeric, return
            ' Nothing so the caller skips this field (form's default
            ' rendering takes over).
            Dim defParsed As Integer
            If Integer.TryParse(field.DefaultValue, defParsed) Then
                Return defParsed
            End If
            Return Nothing
        End Function

        ''' <summary>
        ''' Walk every instance on the given node, look up its
        ''' plugin's port-typed instance fields, parse the values,
        ''' return the flat list. Pass excludeInstanceId to skip a
        ''' specific instance — used by edit flows so an instance
        ''' doesn't conflict with itself.
        '''
        ''' Plugin-not-loaded fallback: if the registry can't
        ''' resolve an instance's gameId (plugin compile error,
        ''' game removed but instance still in DB), we use a
        ''' conservative hard-coded list of conventional port-field
        ''' names. Better to over-include and catch most collisions
        ''' than skip the instance entirely and hide problems.
        ''' </summary>
        Private Shared Function CollectAllPortsOnNode(
                nodeId As String,
                excludeInstanceId As String,
                db As GsmDbContext,
                registry As PluginRegistry) As List(Of PortObservation)

            Dim results As New List(Of PortObservation)
            If String.IsNullOrEmpty(nodeId) OrElse db Is Nothing Then Return results

            Dim rows As List(Of InstanceEntity) = Nothing
            Try
                rows = (
                    From inst In db.Instances
                    Join install In db.Installations
                        On inst.InstallationId Equals install.InstallationId
                    Where install.NodeId = nodeId
                    Select inst).ToList()
            Catch
                Return results
            End Try

            ' Cache per-game port-field key list — we'd otherwise call
            ' GetInstanceConfigSchema once per instance, which is
            ' wasteful when a node has many instances of the same game.
            Dim portKeysByGame As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)

            For Each inst In rows
                If Not String.IsNullOrEmpty(excludeInstanceId) AndAlso
                   String.Equals(inst.InstanceId, excludeInstanceId, StringComparison.Ordinal) Then
                    Continue For
                End If
                Dim gameId = If(inst.GameId, "")
                If String.IsNullOrEmpty(gameId) Then Continue For

                Dim portKeys = ResolvePortKeysForGame(gameId, registry, portKeysByGame)
                If portKeys.Count = 0 Then Continue For

                If String.IsNullOrEmpty(inst.ConfigJson) Then Continue For
                Dim cfg As Dictionary(Of String, String) = Nothing
                Try
                    cfg = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(inst.ConfigJson)
                Catch
                End Try
                If cfg Is Nothing Then Continue For
                Dim ciCfg As New Dictionary(Of String, String)(cfg, StringComparer.OrdinalIgnoreCase)

                For Each key In portKeys
                    Dim raw As String = Nothing
                    If Not ciCfg.TryGetValue(key, raw) Then Continue For
                    If String.IsNullOrWhiteSpace(raw) Then Continue For
                    Dim n As Integer
                    If Not Integer.TryParse(raw.Trim(), n) Then Continue For
                    results.Add(New PortObservation With {
                        .InstanceId = inst.InstanceId,
                        .InstanceName = inst.DisplayName,
                        .GameId = gameId,
                        .Key = key,
                        .Port = n
                    })
                Next
            Next

            Return results
        End Function

        ''' <summary>
        ''' Look up the list of port-typed field keys for a game,
        ''' caching the result. Returns the conventional fallback
        ''' list when the plugin isn't resolvable so we still detect
        ''' obvious collisions for orphan instances.
        ''' </summary>
        Private Shared Function ResolvePortKeysForGame(
                gameId As String,
                registry As PluginRegistry,
                cache As Dictionary(Of String, List(Of String))) As List(Of String)
            Dim cached As List(Of String) = Nothing
            If cache.TryGetValue(gameId, cached) Then Return cached

            Dim keys As New List(Of String)
            If registry IsNot Nothing Then
                Try
                    Dim plugin = registry.GetPlugin(gameId)
                    If plugin IsNot Nothing Then
                        Dim schema = plugin.GetInstanceConfigSchema()
                        If schema IsNot Nothing Then
                            For Each f In schema
                                If f IsNot Nothing AndAlso f.IsPort AndAlso
                                   Not String.IsNullOrEmpty(f.Key) Then
                                    keys.Add(f.Key)
                                End If
                            Next
                        End If
                    End If
                Catch
                End Try
            End If

            ' Plugin not loaded / no port-typed fields declared.
            ' Fall back to convention so we don't silently miss
            ' collisions on an orphan instance.
            If keys.Count = 0 Then
                keys.AddRange({"Port", "RconPort", "QueryPort", "GamePort"})
            End If

            cache(gameId) = keys
            Return keys
        End Function

    End Class

End Namespace
