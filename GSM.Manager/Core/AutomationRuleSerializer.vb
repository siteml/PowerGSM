Imports System
Imports System.Collections.Generic
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.Json.Serialization
Imports GSM.Automation
Imports GSM.Plugin

' ============================================================
'  AutomationRuleSerializer
'
'  Phase 2 of the automation refactor. Produces and consumes
'  JSON for the three polymorphic slots on AutomationRule:
'    - Trigger    (ITrigger)
'    - Conditions (List(Of ICondition))
'    - Action     (IAction, possibly a SequenceAction containing
'                  more IActions recursively)
'
'  WHY NOT JsonConverter(Of T): System.Text.Json's public
'  extension point for custom serialisation is the Read/Write
'  override on JsonConverter(Of T), and Read takes a
'  Utf8JsonReader BYREF. Utf8JsonReader is a ref struct
'  (System.Text.Json stores a Span(Of Byte) inside it), and
'  VB.Net's compiler does not support ref structs at all:
'  trying to reference Utf8JsonReader produces BC30668
'  "obsolete: Types with embedded references are not
'  supported in this version of your compiler." So we cannot
'  implement JsonConverter(Of T) in VB.
'
'  The workaround: we never hook into STJ's converter
'  pipeline. Instead we parse into a JsonNode tree (which is
'  a regular class, VB-friendly), inspect "$type", look up
'  the concrete type in a dispatch table, and let STJ
'  deserialise that specific node into that specific type.
'  Symmetric on write: serialise the concrete type into a
'  JsonNode, inject "$type" as the first property, and emit.
'
'  Nested polymorphism (SequenceAction.Steps containing more
'  IActions) is handled by post-processing the JsonNode tree
'  on the way in and the way out \u2014 see ConvertNodeToAction /
'  ConvertActionToNode. A small amount of manual recursion
'  replaces the converter re-entry that would have happened
'  automatically with a C# JsonConverter.
'
'  Discriminators: each concrete type carries a stable ID via
'  TriggerId / ConditionId / ActionId. We use those values
'  verbatim as the "$type" field. Adding a new type is a
'  three-line change: implement the interface, pick an ID,
'  register it in the dispatch dictionary below.
'
'  Backward compatibility: pre-Phase-2 triggers were stored
'  as flat dictionaries without a "$type" envelope (e.g.
'  { "cronExpression": "..." }). We honour that shape on
'  read so existing DB rows keep working; on next save the
'  rule rewrites in the new format.
' ============================================================

Namespace GSM.Manager.Core

    Public Module AutomationRuleSerializer

        ''' <summary>
        ''' Key used for the polymorphic type discriminator.
        ''' Matches System.Text.Json's default. Keep consistent
        ''' with the existing InstallStep polymorphism that uses
        ''' the same key via [JsonPolymorphic].
        ''' </summary>
        Public Const DiscriminatorKey As String = "$type"

        Private ReadOnly _options As JsonSerializerOptions = BuildOptions()

        Private Function BuildOptions() As JsonSerializerOptions
            Dim opts As New JsonSerializerOptions With {
                .PropertyNameCaseInsensitive = True,
                .PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                .WriteIndented = False,
                .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }
            ' Store enums as strings so the stored JSON is
            ' human-readable and robust against enum reordering.
            opts.Converters.Add(New JsonStringEnumConverter())
            Return opts
        End Function

        ' ============================================================
        '  Public API \u2014 what AutomationEngine calls
        ' ============================================================

        Public Function SerializeTrigger(trigger As ITrigger) As String
            If trigger Is Nothing Then Return Nothing
            Dim node = ConvertTriggerToNode(trigger)
            If node Is Nothing Then Return Nothing
            Return node.ToJsonString(_options)
        End Function

        Public Function DeserializeTrigger(json As String) As ITrigger
            If String.IsNullOrWhiteSpace(json) Then Return Nothing
            Try
                Dim node = JsonNode.Parse(json)
                If node Is Nothing Then Return Nothing
                Dim fromNew = ConvertNodeToTrigger(node)
                If fromNew IsNot Nothing Then Return fromNew
                ' No "$type"? Try the pre-Phase-2 dictionary shape.
                Return DeserializeTriggerLegacy(json)
            Catch
                Return DeserializeTriggerLegacy(json)
            End Try
        End Function

        ''' <summary>
        ''' Old-shape fallback. Pre-Phase-2 triggers were stored
        ''' as flat dictionaries without a "$type" envelope. On
        ''' first save after upgrade the rule rewrites in the
        ''' new shape, so this code only runs during the
        ''' transition.
        ''' </summary>
        Private Function DeserializeTriggerLegacy(json As String) As ITrigger
            Try
                Dim asDict = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(
                    json, New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True})
                If asDict Is Nothing Then Return Nothing

                Dim cronVal As String = Nothing
                If asDict.TryGetValue("cronExpression", cronVal) OrElse
                   asDict.TryGetValue("cronexpression", cronVal) OrElse
                   asDict.TryGetValue("CronExpression", cronVal) Then
                    Return New ScheduleTrigger With {.CronExpression = cronVal}
                End If

                Dim idVal As String = Nothing
                If asDict.TryGetValue("triggerId", idVal) OrElse
                   asDict.TryGetValue("triggerid", idVal) OrElse
                   asDict.TryGetValue("TriggerId", idVal) Then
                    Select Case idVal
                        Case "schedule" : Return New ScheduleTrigger()
                        Case "manual" : Return New ManualTrigger()
                        Case "state_change" : Return New StateChangeTrigger()
                        Case "version_mismatch" : Return New VersionMismatchTrigger()
                    End Select
                End If
            Catch
                ' Malformed legacy JSON yields Nothing, handled
                ' by the engine as "rule has no trigger".
            End Try
            Return Nothing
        End Function

        Public Function SerializeConditions(conditions As List(Of ICondition)) As String
            If conditions Is Nothing OrElse conditions.Count = 0 Then Return Nothing
            Dim arr As New JsonArray()
            For Each c In conditions
                Dim n = ConvertConditionToNode(c)
                If n IsNot Nothing Then arr.Add(n)
            Next
            Return arr.ToJsonString(_options)
        End Function

        Public Function DeserializeConditions(json As String) As List(Of ICondition)
            Dim result As New List(Of ICondition)
            If String.IsNullOrWhiteSpace(json) Then Return result
            Try
                Dim node = JsonNode.Parse(json)
                If node Is Nothing Then Return result
                Dim arr = TryCast(node, JsonArray)
                If arr Is Nothing Then Return result
                For Each elem In arr
                    Dim c = ConvertNodeToCondition(elem)
                    If c IsNot Nothing Then result.Add(c)
                Next
            Catch
            End Try
            Return result
        End Function

        Public Function SerializeAction(action As IAction) As String
            If action Is Nothing Then Return Nothing
            Dim node = ConvertActionToNode(action)
            If node Is Nothing Then Return Nothing
            Return node.ToJsonString(_options)
        End Function

        Public Function DeserializeAction(json As String) As IAction
            If String.IsNullOrWhiteSpace(json) Then Return Nothing
            Try
                Dim node = JsonNode.Parse(json)
                If node Is Nothing Then Return Nothing
                Return ConvertNodeToAction(node)
            Catch
                Return Nothing
            End Try
        End Function

        ' ============================================================
        '  Node <-> concrete conversion
        '
        '  These are the handwritten equivalents of the JsonConverter
        '  Read/Write pair, but operating on JsonNode instead of
        '  Utf8JsonReader so VB can compile them.
        ' ============================================================

        Private Function ConvertTriggerToNode(trigger As ITrigger) As JsonNode
            If trigger Is Nothing Then Return Nothing
            ' Serialise the concrete runtime type into a node,
            ' then inject the "$type" discriminator as the first
            ' property. Serialising at the runtime type means STJ
            ' emits every declared property naturally; we don't
            ' need custom reflection.
            Dim concrete = JsonSerializer.SerializeToNode(trigger, trigger.GetType(), _options)
            Dim obj = TryCast(concrete, JsonObject)
            If obj Is Nothing Then Return Nothing
            Return PrependDiscriminator(obj, trigger.TriggerId)
        End Function

        Private Function ConvertConditionToNode(cond As ICondition) As JsonNode
            If cond Is Nothing Then Return Nothing
            Dim concrete = JsonSerializer.SerializeToNode(cond, cond.GetType(), _options)
            Dim obj = TryCast(concrete, JsonObject)
            If obj Is Nothing Then Return Nothing
            Return PrependDiscriminator(obj, cond.ConditionId)
        End Function

        Private Function ConvertActionToNode(action As IAction) As JsonNode
            If action Is Nothing Then Return Nothing

            ' SequenceAction is the only action whose properties
            ' include nested IActions. STJ doesn't know our
            ' polymorphic contract, so a vanilla SerializeToNode
            ' on a SequenceAction would emit the Steps list as
            ' empty objects (STJ would see declared type IAction,
            ' have no converter, and fall back to reflecting
            ' properties \u2014 of which IAction has none). We
            ' handle this case explicitly by emitting Steps
            ' ourselves via recursive ConvertActionToNode calls.
            If TypeOf action Is SequenceAction Then
                Dim seq = DirectCast(action, SequenceAction)
                Dim obj As New JsonObject()
                obj(DiscriminatorKey) = JsonValue.Create(seq.ActionId)
                obj("actionId") = JsonValue.Create(seq.ActionId)
                obj("displayLabel") = JsonValue.Create(seq.DisplayLabel)
                obj("continueOnFailure") = JsonValue.Create(seq.ContinueOnFailure)
                Dim stepsArr As New JsonArray()
                If seq.Steps IsNot Nothing Then
                    For Each stepAction In seq.Steps
                        Dim stepNode = ConvertActionToNode(stepAction)
                        If stepNode IsNot Nothing Then stepsArr.Add(stepNode)
                    Next
                End If
                obj("steps") = stepsArr
                Return obj
            End If

            ' Leaf actions: STJ can serialise the concrete type
            ' directly because they have no nested IAction / ICondition
            ' / ITrigger fields. We just inject "$type".
            Dim concrete = JsonSerializer.SerializeToNode(action, action.GetType(), _options)
            Dim concreteObj = TryCast(concrete, JsonObject)
            If concreteObj Is Nothing Then Return Nothing
            Return PrependDiscriminator(concreteObj, action.ActionId)
        End Function

        Private Function ConvertNodeToTrigger(node As JsonNode) As ITrigger
            Dim obj = TryCast(node, JsonObject)
            If obj Is Nothing Then Return Nothing
            Dim discriminator = GetDiscriminator(obj)
            If discriminator Is Nothing Then Return Nothing
            Dim targetType As Type = Nothing
            If Not TriggerTypes.TryGetValue(discriminator, targetType) Then Return Nothing
            ' Let STJ deserialise the object as the concrete type.
            ' JsonNode.Deserialize handles the enumeration of
            ' properties; the extra "$type" property we put in is
            ' harmlessly ignored because no target property
            ' matches it.
            Return CType(obj.Deserialize(targetType, _options), ITrigger)
        End Function

        Private Function ConvertNodeToCondition(node As JsonNode) As ICondition
            Dim obj = TryCast(node, JsonObject)
            If obj Is Nothing Then Return Nothing
            Dim discriminator = GetDiscriminator(obj)
            If discriminator Is Nothing Then Return Nothing
            Dim targetType As Type = Nothing
            If Not ConditionTypes.TryGetValue(discriminator, targetType) Then Return Nothing
            Return CType(obj.Deserialize(targetType, _options), ICondition)
        End Function

        Private Function ConvertNodeToAction(node As JsonNode) As IAction
            Dim obj = TryCast(node, JsonObject)
            If obj Is Nothing Then Return Nothing
            Dim discriminator = GetDiscriminator(obj)
            If discriminator Is Nothing Then Return Nothing
            Dim targetType As Type = Nothing
            If Not ActionTypes.TryGetValue(discriminator, targetType) Then Return Nothing

            ' Mirror the write path: handle SequenceAction's
            ' Steps specially. If we just called Deserialize on
            ' the object as SequenceAction, STJ would see the
            ' Steps property as List(Of IAction), have no
            ' converter, and populate it with empty objects
            ' reflected from the IAction interface. Instead we
            ' read the rest of the properties via Deserialize
            ' onto a temp, then walk the steps array ourselves.
            If targetType Is GetType(SequenceAction) Then
                Dim seq As New SequenceAction()
                Dim continueElem As JsonNode = Nothing
                If obj.TryGetPropertyValue("continueOnFailure", continueElem) AndAlso continueElem IsNot Nothing Then
                    Try
                        seq.ContinueOnFailure = continueElem.GetValue(Of Boolean)()
                    Catch
                    End Try
                End If
                Dim stepsElem As JsonNode = Nothing
                seq.Steps = New List(Of IAction)()
                If obj.TryGetPropertyValue("steps", stepsElem) Then
                    Dim stepsArr = TryCast(stepsElem, JsonArray)
                    If stepsArr IsNot Nothing Then
                        For Each stepElem In stepsArr
                            If stepElem Is Nothing Then Continue For
                            Dim stepAction = ConvertNodeToAction(stepElem)
                            If stepAction IsNot Nothing Then seq.Steps.Add(stepAction)
                        Next
                    End If
                End If
                Return seq
            End If

            Return CType(obj.Deserialize(targetType, _options), IAction)
        End Function

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Function GetDiscriminator(obj As JsonObject) As String
            Dim node As JsonNode = Nothing
            If Not obj.TryGetPropertyValue(DiscriminatorKey, node) Then Return Nothing
            If node Is Nothing Then Return Nothing
            Try
                Return node.GetValue(Of String)()
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Produce a new JsonObject that starts with the "$type"
        ''' property followed by every property from the source.
        ''' JsonObject preserves insertion order on ToJsonString,
        ''' so this gives us the "$type" first convention without
        ''' mutating the STJ-generated object (which would be a
        ''' move operation since it's already inserted).
        ''' </summary>
        Private Function PrependDiscriminator(source As JsonObject,
                                               discriminator As String) As JsonObject
            Dim result As New JsonObject()
            result(DiscriminatorKey) = JsonValue.Create(discriminator)
            ' Need to detach each child from `source` before
            ' inserting into `result` \u2014 JsonNode enforces single-
            ' parent ownership and will throw on double-parenting.
            ' ToArray + iterate avoids "collection modified" on
            ' the live Remove loop.
            Dim entries = source.ToArray()
            For Each kv In entries
                If String.Equals(kv.Key, DiscriminatorKey, StringComparison.Ordinal) Then
                    Continue For
                End If
                Dim child = kv.Value
                source.Remove(kv.Key)
                result(kv.Key) = child
            Next
            Return result
        End Function

        ' ============================================================
        '  Dispatch tables \u2014 single source of truth for which
        '  concrete types exist and what their "$type" values are.
        '  To add a new trigger/condition/action type, register it
        '  here.
        ' ============================================================

        Friend ReadOnly TriggerTypes As Dictionary(Of String, Type) = New Dictionary(Of String, Type)(StringComparer.OrdinalIgnoreCase) From {
            {"schedule", GetType(ScheduleTrigger)},
            {"state_change", GetType(StateChangeTrigger)},
            {"version_mismatch", GetType(VersionMismatchTrigger)},
            {"manual", GetType(ManualTrigger)}
        }

        Friend ReadOnly ConditionTypes As Dictionary(Of String, Type) = New Dictionary(Of String, Type)(StringComparer.OrdinalIgnoreCase) From {
            {"instance_state", GetType(InstanceStateCondition)},
            {"wait_player_count", GetType(WaitForPlayerCountCondition)},
            {"all_instances_empty", GetType(AllInstancesEmptyCondition)}
        }

        Friend ReadOnly ActionTypes As Dictionary(Of String, Type) = New Dictionary(Of String, Type)(StringComparer.OrdinalIgnoreCase) From {
            {"start_instance", GetType(StartInstanceAction)},
            {"stop_instance", GetType(StopInstanceAction)},
            {"restart_instance", GetType(RestartInstanceAction)},
            {"stop_all_instances", GetType(StopAllInstancesAction)},
            {"start_all_instances", GetType(StartAllInstancesAction)},
            {"update_installation", GetType(UpdateInstallationAction)},
            {"send_rcon", GetType(SendRconCommandAction)},
            {"notify", GetType(NotifyAction)},
            {"wait", GetType(WaitAction)},
            {"wait_for_ready", GetType(WaitForReadySignalAction)},
            {"coordinated_restart", GetType(CoordinatedRestartAction)},
            {"sequence", GetType(SequenceAction)}
        }

    End Module

End Namespace
