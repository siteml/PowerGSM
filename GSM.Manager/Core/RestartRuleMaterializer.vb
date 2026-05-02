Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports GSM.Automation
Imports GSM.Manager.Data

' ============================================================
'  RestartRuleMaterializer
'
'  Phase 4a of the automation refactor. Translates between the
'  "quick config" fields on InstanceEntity (RestartEnabled +
'  RestartCron) and the full AutomationRule that drives the
'  scheduled restart behaviour.
'
'  Invariants:
'    - When Instance.RestartEnabled is True, there exists a
'      corresponding AutomationRuleEntity keyed by
'      Instance.RestartRuleId.
'    - When Instance.RestartEnabled is False, there is no rule
'      with that ID (and RestartRuleId is null).
'    - The auto-generated rule has exactly one shape:
'        Scope    = Instance
'        Trigger  = ScheduleTrigger(cron)
'        Conditions = empty
'        Action   = CoordinatedRestartAction(instanceId)
'      See BuildSimpleRestartRule for the canonical form.
'
'  Drift handling: if a power user goes into the Automation
'  Rules window and modifies the generated rule beyond this
'  canonical shape (adds conditions, changes trigger, swaps
'  the action), IsSimpleRestartRule returns False. The caller
'  is expected to have detected drift at form-load time and
'  NOT asked us to materialise; Materialize still defends
'  against this by refusing to stomp a drifted rule and
'  returning NoChange.
'
'  Transaction boundary: all methods here mutate the tracked
'  DbContext but never call SaveChanges. Callers batch our
'  changes with their own InstanceEntity mutations and commit
'  once. This keeps the materialisation atomic with whatever
'  else the caller is doing (e.g. saving DisplayName + restart
'  fields in one EditInstanceForm OK click).
' ============================================================

Namespace GSM.Manager.Core

    Public Module RestartRuleMaterializer

        ''' <summary>
        ''' What Materialize did to the rule. Returned so callers
        ''' can decide whether to reload the AutomationEngine
        ''' (skip the reload when nothing changed — reload is
        ''' cheap but not free, and skipping keeps logs clean).
        ''' </summary>
        Public Enum MaterializationAction
            NoChange
            Created
            Updated
            Deleted
        End Enum

        Public Class MaterializationResult
            Public Property Action As MaterializationAction
            ''' <summary>
            ''' The rule's ID after the operation. Nothing when
            ''' the rule was just deleted or when no rule exists.
            ''' </summary>
            Public Property RuleId As String
        End Class

        ' ============================================================
        '  Public API
        ' ============================================================

        ''' <summary>
        ''' Create, update, or delete the auto-generated restart
        ''' rule for an instance based on its current
        ''' RestartEnabled / RestartCron / DisplayName values.
        ''' Mutates the DB context but does NOT SaveChanges —
        ''' the caller owns the transaction boundary.
        '''
        ''' Behaviour matrix:
        '''   RestartEnabled=False, no rule       → NoChange
        '''   RestartEnabled=False, rule exists   → Deleted (clears RestartRuleId)
        '''   RestartEnabled=True,  no rule       → Created (sets RestartRuleId)
        '''   RestartEnabled=True,  simple rule, identical fields → NoChange
        '''   RestartEnabled=True,  simple rule, different fields → Updated
        '''   RestartEnabled=True,  drifted rule                  → NoChange (defensive)
        ''' </summary>
        Public Function Materialize(db As GsmDbContext,
                                     instance As InstanceEntity) As MaterializationResult
            If db Is Nothing Then Throw New ArgumentNullException(NameOf(db))
            If instance Is Nothing Then Throw New ArgumentNullException(NameOf(instance))

            Dim existing As AutomationRuleEntity = Nothing
            If Not String.IsNullOrEmpty(instance.RestartRuleId) Then
                existing = db.AutomationRules.Find(instance.RestartRuleId)
            End If

            ' ---- Delete path ----
            If Not instance.RestartEnabled Then
                If existing Is Nothing Then
                    ' Make sure the cached RuleId is cleared even
                    ' when there's no entity to remove — otherwise
                    ' a dangling ID would persist on the instance
                    ' and confuse future form loads.
                    instance.RestartRuleId = Nothing
                    Return New MaterializationResult With {
                        .Action = MaterializationAction.NoChange
                    }
                End If
                db.AutomationRules.Remove(existing)
                instance.RestartRuleId = Nothing
                Return New MaterializationResult With {
                    .Action = MaterializationAction.Deleted
                }
            End If

            ' ---- Guard: refuse to stomp a drifted rule ----
            ' The caller should already have checked this at form
            ' load time and presented the greyed-out section; this
            ' is a defensive second line.
            If existing IsNot Nothing AndAlso Not IsSimpleRestartRule(existing) Then
                Return New MaterializationResult With {
                    .Action = MaterializationAction.NoChange,
                    .RuleId = existing.RuleId
                }
            End If

            ' ---- Build the desired shape ----
            Dim ruleModel = BuildSimpleRestartRule(instance)
            ' Serialize into a FRESH temp entity so we can compare
            ' to the existing one without mutating it first.
            Dim desired = AutomationEngine.SerializeRuleToEntity(ruleModel)
            desired.RuleName = BuildRuleName(instance)

            ' ---- Create path ----
            If existing Is Nothing Then
                desired.RuleId = Guid.NewGuid().ToString("N")
                desired.CreatedUtc = DateTime.UtcNow
                desired.UpdatedUtc = DateTime.UtcNow
                ' Auto-generated rules go at the end of the list
                ' just like manually-created ones, so the user's
                ' display order doesn't get disrupted by a
                ' background materialisation.
                desired.SortOrder = db.NextRuleSortOrder()
                db.AutomationRules.Add(desired)
                instance.RestartRuleId = desired.RuleId
                Return New MaterializationResult With {
                    .Action = MaterializationAction.Created,
                    .RuleId = desired.RuleId
                }
            End If

            ' ---- Update path ----
            ' Compare the desired entity to the existing one by
            ' all fields we ever set. Any mismatch means we need
            ' to write. EF's change tracking would technically
            ' skip the UPDATE statement if nothing actually
            ' changed, but we still want the Return value to
            ' distinguish Updated from NoChange so the caller
            ' only triggers engine.ReloadRules() when warranted.
            Dim changed = desired.TriggerJson <> existing.TriggerJson OrElse
                          desired.ActionJson <> existing.ActionJson OrElse
                          desired.ConditionsJson <> existing.ConditionsJson OrElse
                          desired.RuleName <> existing.RuleName OrElse
                          desired.ScopeKind <> existing.ScopeKind OrElse
                          desired.TargetId <> existing.TargetId OrElse
                          Not String.Equals(If(desired.GameFilter, ""),
                                            If(existing.GameFilter, ""),
                                            StringComparison.Ordinal) OrElse
                          Not existing.IsEnabled

            If Not changed Then
                Return New MaterializationResult With {
                    .Action = MaterializationAction.NoChange,
                    .RuleId = existing.RuleId
                }
            End If

            existing.TriggerJson = desired.TriggerJson
            existing.ActionJson = desired.ActionJson
            existing.ConditionsJson = desired.ConditionsJson
            existing.RuleName = desired.RuleName
            existing.ScopeKind = desired.ScopeKind
            existing.TargetId = desired.TargetId
            existing.GameFilter = desired.GameFilter
            existing.IsEnabled = True
            existing.UpdatedUtc = DateTime.UtcNow

            Return New MaterializationResult With {
                .Action = MaterializationAction.Updated,
                .RuleId = existing.RuleId
            }
        End Function

        ''' <summary>
        ''' True when the rule entity matches the exact shape that
        ''' EditInstanceForm produces. Used by the form at load
        ''' time to decide whether to show the quick-config
        ''' section or gray it out and direct the user to the
        ''' full rule editor.
        '''
        ''' The shape:
        '''   - ScopeKind = "Instance"
        '''   - GameFilter null/empty (Phase 4b-pre1: any
        '''     non-null GameFilter is drift, since the simple
        '''     form can't express it — the auto-generated rule
        '''     targets one specific instance whose game is
        '''     already determined)
        '''   - Conditions empty (null JSON OR deserialises to zero conditions)
        '''   - Trigger is a ScheduleTrigger
        '''   - Action is a CoordinatedRestartAction whose InstanceId matches TargetId
        '''
        ''' Notably does NOT check the cron value or the action's
        ''' timeout settings — those are value-level differences
        ''' that still round-trip cleanly through the quick-config
        ''' fields. Drift is purely structural.
        ''' </summary>
        Public Function IsSimpleRestartRule(entity As AutomationRuleEntity) As Boolean
            If entity Is Nothing Then Return False

            ' Scope must be Instance
            If Not String.Equals(entity.ScopeKind,
                                  RuleScope.Instance.ToString(),
                                  StringComparison.OrdinalIgnoreCase) Then
                Return False
            End If

            ' GameFilter must be unset. A simple restart rule
            ' targets one specific instance, so a GameFilter is
            ' redundant at best and contradictory at worst (e.g.
            ' user picks GameFilter=factorio for a Last Oasis
            ' instance — the rule would then fire but never
            ' resolve any instance). Either way, it's drift.
            If Not String.IsNullOrEmpty(entity.GameFilter) Then Return False

            ' Conditions must be empty. Null/empty JSON is fine
            ' (that's what SerializeConditions produces for an
            ' empty list); a non-null JSON must parse to zero
            ' conditions.
            If Not String.IsNullOrEmpty(entity.ConditionsJson) Then
                Try
                    Dim conds = AutomationRuleSerializer.DeserializeConditions(entity.ConditionsJson)
                    If conds IsNot Nothing AndAlso conds.Count > 0 Then Return False
                Catch
                    ' Unparseable conditions JSON = drifted / corrupt
                    Return False
                End Try
            End If

            ' Trigger must be a bare ScheduleTrigger
            Dim trig As ITrigger
            Try
                trig = AutomationRuleSerializer.DeserializeTrigger(entity.TriggerJson)
            Catch
                Return False
            End Try
            If trig Is Nothing Then Return False
            If TypeOf trig IsNot ScheduleTrigger Then Return False

            ' Action must be a bare CoordinatedRestartAction
            ' pointing at the same instance as TargetId.
            Dim action As IAction
            Try
                action = AutomationRuleSerializer.DeserializeAction(entity.ActionJson)
            Catch
                Return False
            End Try
            If action Is Nothing Then Return False
            If TypeOf action IsNot CoordinatedRestartAction Then Return False

            Dim cra = DirectCast(action, CoordinatedRestartAction)
            If Not String.Equals(cra.InstanceId, entity.TargetId, StringComparison.Ordinal) Then
                Return False
            End If

            Return True
        End Function

        ''' <summary>
        ''' Extract the cron expression from a simple rule's
        ''' ScheduleTrigger. Returns Nothing if the rule doesn't
        ''' match the simple shape or has no cron. Used by
        ''' EditInstanceForm to populate its cron field from the
        ''' rule (the authoritative source) rather than from
        ''' Instance.RestartCron (which is a cache that can drift
        ''' if the rule was edited elsewhere).
        ''' </summary>
        Public Function ExtractCronFromRule(entity As AutomationRuleEntity) As String
            If entity Is Nothing OrElse String.IsNullOrEmpty(entity.TriggerJson) Then Return Nothing
            Try
                Dim trig = AutomationRuleSerializer.DeserializeTrigger(entity.TriggerJson)
                Dim schedTrigger = TryCast(trig, ScheduleTrigger)
                If schedTrigger Is Nothing Then Return Nothing
                Return schedTrigger.CronExpression
            Catch
                Return Nothing
            End Try
        End Function

        ' ============================================================
        '  Internal builders
        ' ============================================================

        ''' <summary>
        ''' Build the canonical AutomationRule for a quick-config
        ''' restart schedule. Produces the exact shape that
        ''' IsSimpleRestartRule accepts. If you change this, also
        ''' update IsSimpleRestartRule so drift detection stays
        ''' aligned.
        ''' </summary>
        Private Function BuildSimpleRestartRule(instance As InstanceEntity) As AutomationRule
            Return New AutomationRule With {
                .RuleId = If(instance.RestartRuleId, ""),
                .DisplayName = BuildRuleName(instance),
                .IsEnabled = True,
                .Scope = RuleScope.Instance,
                .TargetId = instance.InstanceId,
                .Trigger = New ScheduleTrigger With {
                    .CronExpression = instance.RestartCron
                },
                .Conditions = New List(Of ICondition),
                .ConditionMode = ConditionMode.All,
                .Action = New CoordinatedRestartAction With {
                    .InstanceId = instance.InstanceId,
                    .GracefulTimeoutMs = 10000,
                    .DelayBetweenMs = 2000,
                    .ReadyTimeoutSeconds = 0
                },
                .Overlap = OverlapPolicy.SkipIfRunning
            }
        End Function

        ''' <summary>
        ''' Human-readable rule name shown in the Automation Rules
        ''' window. Regenerated on every Materialize so renaming
        ''' the instance automatically updates its rule's name.
        ''' </summary>
        Private Function BuildRuleName(instance As InstanceEntity) As String
            Dim shown = If(instance.DisplayName, instance.InstanceId)
            Return $"Restart schedule for {shown}"
        End Function

    End Module

End Namespace
