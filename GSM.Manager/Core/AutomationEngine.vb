Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports NCrontab
Imports GSM.Plugin
Imports GSM.Automation
Imports GSM.Notification
Imports GSM.Manager
Imports GSM.Manager.Data

' ============================================================
'  AutomationEngine — evaluates rules, runs sequences
'
'  The engine manages:
'    - Loading rules from the database
'    - Scheduling cron-triggered rules via CronTimer
'    - Evaluating conditions and executing actions
'    - Providing RuleContextImpl to conditions/actions
'    - Recording execution history
'
'  All rule logic runs in the manager process. The engine
'  communicates with nodes only through InstanceManager and
'  InstallationManager.
' ============================================================

Namespace GSM.Manager.Core

    Public Class AutomationEngine

        Private ReadOnly _instanceManager As InstanceManager
        Private ReadOnly _installationManager As InstallationManager
        Private ReadOnly _notificationService As NotificationService
        Private ReadOnly _logger As ILogger(Of AutomationEngine)
        Private ReadOnly _cronTimers As New ConcurrentDictionary(Of String, CronTimer)
        Private ReadOnly _runningExecutions As New ConcurrentDictionary(Of String, CancellationTokenSource)
        Private ReadOnly _rules As New ConcurrentDictionary(Of String, AutomationRule)
        Private _engineCts As CancellationTokenSource

        Public Sub New(instanceManager As InstanceManager,
                       installationManager As InstallationManager,
                       notificationService As NotificationService,
                       logger As ILogger(Of AutomationEngine))
            _instanceManager = instanceManager
            _installationManager = installationManager
            _notificationService = notificationService
            _logger = logger
        End Sub

        ' ============================================================
        '  Engine lifecycle
        ' ============================================================

        ''' <summary>
        ''' Starts the automation engine. Loads rules from the
        ''' database and starts cron timers.
        ''' </summary>
        Public Sub Start()
            _engineCts = New CancellationTokenSource()
            LoadRulesFromDatabase()
            _logger.LogInformation("Automation engine started with {Count} rules", _rules.Count)
        End Sub

        ''' <summary>
        ''' Stops the engine and cancels all running executions.
        ''' </summary>
        Public Sub [Stop]()
            _engineCts?.Cancel()
            For Each kvp In _cronTimers
                kvp.Value.Stop()
            Next
            _cronTimers.Clear()
            For Each kvp In _runningExecutions
                kvp.Value.Cancel()
            Next
            _runningExecutions.Clear()
            _logger.LogInformation("Automation engine stopped")
        End Sub

        ''' <summary>
        ''' Reloads rules from the database.
        '''
        ''' Self-starting: if the engine hasn't been Start()ed yet,
        ''' or was previously Stop()ped, ReloadRules now synthesises
        ''' a fresh CTS so cron timers can arm against it. This
        ''' makes ReloadRules safe to call from UI paths (e.g.
        ''' EditInstanceForm after save) without requiring the
        ''' caller to know about engine lifecycle state. Saved one
        ''' prior crash where the engine was never started at all.
        ''' </summary>
        Public Sub ReloadRules()
            If _engineCts Is Nothing OrElse _engineCts.IsCancellationRequested Then
                _engineCts = New CancellationTokenSource()
            End If

            ' Stop existing timers
            For Each kvp In _cronTimers
                kvp.Value.Stop()
            Next
            _cronTimers.Clear()
            _rules.Clear()

            LoadRulesFromDatabase()
            _logger.LogInformation("Reloaded {Count} rules", _rules.Count)
        End Sub

        ' ============================================================
        '  Rule loading
        ' ============================================================

        Private Sub LoadRulesFromDatabase()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim ruleEntities = db.AutomationRules.
                    Where(Function(r) r.IsEnabled).
                    ToList()

                For Each entity In ruleEntities
                    Try
                        Dim rule = DeserializeRule(entity)
                        If rule IsNot Nothing Then
                            _rules(rule.RuleId) = rule
                            SetupTrigger(rule)
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex, "Failed to load rule {Id}", entity.RuleId)
                    End Try
                Next
            End Using
        End Sub

        Private Sub SetupTrigger(rule As AutomationRule)
            If rule.Trigger Is Nothing Then Return

            If TypeOf rule.Trigger Is ScheduleTrigger Then
                Dim schedTrigger = DirectCast(rule.Trigger, ScheduleTrigger)
                If Not String.IsNullOrEmpty(schedTrigger.CronExpression) Then
                    Dim timer As New CronTimer(
                        schedTrigger.CronExpression,
                        Sub() Task.Run(Function() FireRuleAsync(rule, "Scheduled")),
                        _logger)
                    _cronTimers(rule.RuleId) = timer
                    timer.Start(_engineCts.Token)
                End If
            End If
            ' StateChange triggers are event-driven and will be
            ' wired up when state-change events are implemented.
            ' VersionMismatch triggers are event-driven via
            ' RaiseVersionMismatchAsync — callers (plugins,
            ' future version-check polling service) invoke that
            ' method when they detect a mismatch, and the engine
            ' fires matching rules. No per-rule setup needed here
            ' since the matching happens at raise time.
        End Sub

        ' ============================================================
        '  Rule execution
        ' ============================================================

        ''' <summary>
        ''' Fires a rule manually (from UI or remote command).
        ''' </summary>
        Public Async Function FireRuleManuallyAsync(ruleId As String) As Task(Of Boolean)
            Dim rule As AutomationRule = Nothing
            If Not _rules.TryGetValue(ruleId, rule) Then
                _logger.LogWarning("Rule {Id} not found", ruleId)
                Return False
            End If
            Await FireRuleAsync(rule, "Manual")
            Return True
        End Function

        ''' <summary>
        ''' Raise a version-mismatch event for an installation.
        ''' Fires every enabled rule with a VersionMismatchTrigger
        ''' whose scope and target are affected by the change.
        '''
        ''' Phase 5 skeleton: this method is the integration point
        ''' for future version-check polling. The actual polling
        ''' service (SteamCMD app_info_print, Factorio API, etc.)
        ''' is not yet implemented — this method exists so that
        ''' when polling is added, version-mismatch rules already
        ''' authored via the rule editor will fire automatically
        ''' without further engine changes. Plugins that detect
        ''' updates out-of-band can also call this directly.
        '''
        ''' Scope-matching logic mirrors the rest of the engine:
        '''   Instance     - rule fires if the instance's
        '''                  installation matches the parameter
        '''                  (one rule fire per matching instance)
        '''   Installation - rule fires if TargetId matches
        '''   Node         - rule fires if the installation lives
        '''                  on that node (with optional GameFilter)
        '''   InstanceSet  - rule fires if any tagged instance
        '''                  belongs to the installation
        '''   AllInstances - always fires, optionally narrowed by
        '''                  GameFilter
        '''
        ''' Idempotency / throttling is the caller's concern: this
        ''' method has no "don't refire if user hasn't updated"
        ''' logic. Callers that poll on a timer should track which
        ''' installations they've already raised for and only call
        ''' again when the upstream version changes again.
        ''' </summary>
        Public Async Function RaiseVersionMismatchAsync(installationId As String) As Task
            If String.IsNullOrEmpty(installationId) Then Return

            ' Resolve installation context once — we need the
            ' GameId for filter matching, and the NodeId for
            ' Node-scope rules.
            Dim installGameId As String = Nothing
            Dim installNodeId As String = Nothing
            Dim setTagsForInstall As New HashSet(Of String)(StringComparer.Ordinal)
            Dim instanceIdsForInstall As New List(Of String)

            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim install = db.Installations.
                        FirstOrDefault(Function(i) i.InstallationId = installationId)
                    If install Is Nothing Then
                        _logger.LogWarning(
                            "RaiseVersionMismatchAsync: installation {Id} not found",
                            installationId)
                        Return
                    End If
                    installGameId = install.GameId
                    installNodeId = install.NodeId

                    ' Pre-compute the instances under this install
                    ' and their set tags so we can match Instance
                    ' and InstanceSet scopes without re-querying.
                    For Each inst In db.Instances.
                        Where(Function(i) i.InstallationId = installationId)
                        instanceIdsForInstall.Add(inst.InstanceId)
                        If Not String.IsNullOrEmpty(inst.InstanceSetTag) Then
                            setTagsForInstall.Add(inst.InstanceSetTag)
                        End If
                    Next
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to resolve installation {Id} for version-mismatch raise",
                    installationId)
                Return
            End Try

            ' Iterate rules, match scope, fire matches. Snapshot
            ' the rule list to a local List first so the dictionary
            ' iteration doesn't fight a concurrent ReloadRules call.
            Dim rulesSnapshot = _rules.Values.ToList()
            Dim firedCount = 0

            For Each rule In rulesSnapshot
                If Not rule.IsEnabled Then Continue For
                If Not (TypeOf rule.Trigger Is VersionMismatchTrigger) Then Continue For
                If Not VersionMismatchRuleMatches(
                    rule, installationId, installGameId, installNodeId,
                    instanceIdsForInstall, setTagsForInstall) Then Continue For

                ' Instance-scope rules fire once per matching
                ' instance (since the rule's TargetId is one
                ' specific instance). All other scopes fire once
                ' for the rule itself — the rule's action will
                ' do the multi-instance fan-out via
                ' GetInstanceIdsForScope.
                firedCount += 1
                Try
                    Await FireRuleAsync(rule, $"VersionMismatch:{installationId}")
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "Failed to fire version-mismatch rule {Id}", rule.RuleId)
                End Try
            Next

            _logger.LogInformation(
                "Version mismatch raised for installation {Id}: {Count} rule(s) fired",
                installationId, firedCount)
        End Function

        ''' <summary>
        ''' Scope-aware match check for a version-mismatch rule
        ''' against an affected installation. Returns true if the
        ''' rule should fire for this installation.
        '''
        ''' GameFilter handling: applied only to multi-instance
        ''' scopes (Installation, Node, InstanceSet, AllInstances).
        ''' Instance scope already pins the game via the target
        ''' instance, so GameFilter is ignored there.
        ''' </summary>
        Private Function VersionMismatchRuleMatches(
                rule As AutomationRule,
                installationId As String,
                installGameId As String,
                installNodeId As String,
                instanceIdsForInstall As List(Of String),
                setTagsForInstall As HashSet(Of String)) As Boolean

            ' GameFilter pre-check (skipped for Instance scope).
            If rule.Scope <> RuleScope.Instance AndAlso
               Not String.IsNullOrEmpty(rule.GameFilter) AndAlso
               Not String.Equals(rule.GameFilter, installGameId, StringComparison.Ordinal) Then
                Return False
            End If

            Select Case rule.Scope
                Case RuleScope.Instance
                    ' Rule's TargetId is a specific instance; fires
                    ' if that instance is under this installation.
                    Return instanceIdsForInstall.Contains(rule.TargetId)

                Case RuleScope.Installation
                    Return String.Equals(
                        rule.TargetId, installationId, StringComparison.Ordinal)

                Case RuleScope.Node
                    Return String.Equals(
                        rule.TargetId, installNodeId, StringComparison.Ordinal)

                Case RuleScope.InstanceSet
                    ' Set is a string tag; rule fires if any
                    ' instance under this installation carries
                    ' the rule's TargetId tag.
                    Return setTagsForInstall.Contains(If(rule.TargetId, ""))

                Case RuleScope.AllInstances
                    Return True

                Case Else
                    Return False
            End Select
        End Function

        ''' <summary>
        ''' Core rule execution: evaluate conditions, execute action,
        ''' record result.
        ''' </summary>
        Private Async Function FireRuleAsync(rule As AutomationRule,
                                              triggerReason As String) As Task

            ' Check overlap policy
            Dim existingCts As CancellationTokenSource = Nothing
            If _runningExecutions.TryGetValue(rule.RuleId, existingCts) Then
                Select Case rule.Overlap
                    Case OverlapPolicy.SkipIfRunning
                        _logger.LogDebug("Rule {Id} skipped — already running", rule.RuleId)
                        RecordExecution(rule.RuleId, triggerReason, Nothing, Nothing,
                                       wasSkipped:=True, skipReason:="SkipIfRunning")
                        Return
                    Case OverlapPolicy.CancelAndRestart
                        existingCts.Cancel()
                        Dim removed As CancellationTokenSource = Nothing
                        _runningExecutions.TryRemove(rule.RuleId, removed)
                    Case OverlapPolicy.QueueNext
                        ' Wait for current to finish
                        Await Task.Delay(1000)
                End Select
            End If

            Dim cts As New CancellationTokenSource()
            _runningExecutions(rule.RuleId) = cts

            Try
                _logger.LogInformation("Firing rule '{Name}' ({Id}) — {Reason}",
                                       rule.DisplayName, rule.RuleId, triggerReason)

                ' Build context
                Dim ctx As New RuleContextImpl(
                    rule, _instanceManager, _installationManager,
                    _notificationService, _logger)

                ' Evaluate conditions
                Dim conditionResults As New List(Of ConditionEvaluation)
                Dim allPassed = True

                If rule.Conditions IsNot Nothing Then
                    For Each condition In rule.Conditions
                        Dim result = Await condition.Evaluate(ctx, cts.Token)
                        conditionResults.Add(New ConditionEvaluation With {
                            .ConditionId = condition.ConditionId,
                            .Passed = result.Passed,
                            .Reason = result.Reason
                        })

                        If Not result.Passed Then
                            If rule.ConditionMode = ConditionMode.All Then
                                allPassed = False
                                Exit For
                            End If
                        Else
                            If rule.ConditionMode = ConditionMode.Any Then
                                allPassed = True
                                Exit For
                            End If
                        End If
                    Next

                    If rule.ConditionMode = ConditionMode.Any AndAlso
                       conditionResults.Count > 0 AndAlso
                       Not conditionResults.Any(Function(c) c.Passed) Then
                        allPassed = False
                    End If
                End If

                If Not allPassed Then
                    _logger.LogInformation("Rule '{Name}' conditions not met", rule.DisplayName)
                    RecordExecution(rule.RuleId, triggerReason, conditionResults, Nothing,
                                   wasSkipped:=True, skipReason:="Conditions not met")
                    Return
                End If

                ' Execute action
                Dim actionResult As ActionResult = Nothing
                If rule.Action IsNot Nothing Then
                    actionResult = Await rule.Action.Execute(ctx, cts.Token)
                    _logger.LogInformation("Rule '{Name}' action result: {Ok} — {Msg}",
                                           rule.DisplayName, actionResult.Success, actionResult.Message)
                End If

                RecordExecution(rule.RuleId, triggerReason, conditionResults, actionResult,
                               wasSkipped:=False, skipReason:=Nothing)

            Catch ex As OperationCanceledException
                _logger.LogInformation("Rule '{Name}' execution cancelled", rule.DisplayName)

            Catch ex As Exception
                _logger.LogError(ex, "Rule '{Name}' execution failed", rule.DisplayName)
                RecordExecution(rule.RuleId, triggerReason, Nothing,
                               ActionResult.Fail($"Exception: {ex.Message}"),
                               wasSkipped:=False, skipReason:=Nothing)
            Finally
                Dim removedCts As CancellationTokenSource = Nothing
                _runningExecutions.TryRemove(rule.RuleId, removedCts)
            End Try
        End Function

        ' ============================================================
        '  Execution recording
        ' ============================================================

        Private Sub RecordExecution(ruleId As String,
                                    triggerReason As String,
                                    conditionResults As List(Of ConditionEvaluation),
                                    actionResult As ActionResult,
                                    wasSkipped As Boolean,
                                    skipReason As String)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim entity As New RuleExecutionEntity With {
                        .ExecutionId = Guid.NewGuid().ToString("N"),
                        .RuleId = ruleId,
                        .StartedAtUtc = DateTime.UtcNow,
                        .CompletedAtUtc = DateTime.UtcNow,
                        .TriggerReason = triggerReason,
                        .ConditionResultsJson = If(conditionResults IsNot Nothing,
                            JsonSerializer.Serialize(conditionResults), Nothing),
                        .ActionResultJson = If(actionResult IsNot Nothing,
                            JsonSerializer.Serialize(actionResult), Nothing),
                        .WasSkipped = wasSkipped,
                        .SkipReason = skipReason
                    }
                    db.RuleExecutions.Add(entity)
                    db.SaveChanges()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to record execution for rule {Id}", ruleId)
            End Try
        End Sub

        ' ============================================================
        '  Rule deserialization / serialization
        ' ============================================================

        ''' <summary>
        ''' Hydrate an AutomationRule from a persisted entity.
        ''' Phase 2: uses AutomationRuleSerializer for polymorphic
        ''' trigger / conditions / action JSON. Falls back to the
        ''' legacy dictionary shape for triggers written before
        ''' Phase 2; on next save the rule rewrites in the new
        ''' format automatically.
        ''' </summary>
        Private Function DeserializeRule(entity As AutomationRuleEntity) As AutomationRule
            Dim rule As New AutomationRule With {
                .RuleId = entity.RuleId,
                .DisplayName = entity.RuleName,
                .IsEnabled = entity.IsEnabled,
                .TargetId = entity.TargetId,
                .GameFilter = entity.GameFilter
            }

            Dim scopeVal As RuleScope
            If [Enum].TryParse(entity.ScopeKind, True, scopeVal) Then
                rule.Scope = scopeVal
            End If

            rule.Trigger = AutomationRuleSerializer.DeserializeTrigger(entity.TriggerJson)
            rule.Conditions = AutomationRuleSerializer.DeserializeConditions(entity.ConditionsJson)
            rule.Action = AutomationRuleSerializer.DeserializeAction(entity.ActionJson)

            Return rule
        End Function

        ''' <summary>
        ''' Serialise an AutomationRule into its persistence form.
        ''' Returns a fresh entity with JSON columns populated; the
        ''' caller is responsible for adding/updating and saving it.
        ''' Primary key fields (RuleId, timestamps) are NOT set by
        ''' this method — the caller owns identity and audit fields.
        ''' </summary>
        Public Shared Function SerializeRuleToEntity(rule As AutomationRule,
                                                      Optional existing As AutomationRuleEntity = Nothing) As AutomationRuleEntity
            If rule Is Nothing Then Throw New ArgumentNullException(NameOf(rule))
            Dim entity = If(existing, New AutomationRuleEntity())
            entity.RuleName = rule.DisplayName
            entity.IsEnabled = rule.IsEnabled
            entity.ScopeKind = rule.Scope.ToString()
            entity.TargetId = rule.TargetId
            entity.GameFilter = rule.GameFilter
            entity.TriggerJson = AutomationRuleSerializer.SerializeTrigger(rule.Trigger)
            entity.ConditionsJson = AutomationRuleSerializer.SerializeConditions(rule.Conditions)
            entity.ActionJson = AutomationRuleSerializer.SerializeAction(rule.Action)
            Return entity
        End Function

    End Class

    ' ============================================================
    '  RuleContextImpl — concrete implementation of RuleContext
    '  Wires automation actions to manager services.
    ' ============================================================

    Public Class RuleContextImpl
        Inherits RuleContext

        Private ReadOnly _rule As AutomationRule
        Private ReadOnly _instanceManager As InstanceManager
        Private ReadOnly _installationManager As InstallationManager
        Private ReadOnly _notificationService As NotificationService
        Private ReadOnly _logger As ILogger

        Public Sub New(rule As AutomationRule,
                       instanceManager As InstanceManager,
                       installationManager As InstallationManager,
                       notificationService As NotificationService,
                       logger As ILogger)
            _rule = rule
            _instanceManager = instanceManager
            _installationManager = installationManager
            _notificationService = notificationService
            _logger = logger
        End Sub

        Public Overrides ReadOnly Property RuleId As String
            Get
                Return _rule.RuleId
            End Get
        End Property

        Public Overrides ReadOnly Property TargetInstanceId As String
            Get
                If _rule.Scope = RuleScope.Instance Then Return _rule.TargetId
                Return Nothing
            End Get
        End Property

        Public Overrides ReadOnly Property TargetInstallationId As String
            Get
                If _rule.Scope = RuleScope.Installation Then Return _rule.TargetId
                Return Nothing
            End Get
        End Property

        Public Overrides ReadOnly Property Scope As RuleScope
            Get
                Return _rule.Scope
            End Get
        End Property

        Public Overrides Async Function GetInstanceState(instanceId As String) As Task(Of InstanceStateInfo)
            Dim status = Await _instanceManager.RefreshInstanceStateAsync(instanceId)
            If status Is Nothing Then
                Return New InstanceStateInfo With {
                    .CurrentState = InstanceState.Stopped
                }
            End If
            Return New InstanceStateInfo With {
                .CurrentState = status.CurrentState,
                .StateEnteredAt = status.StateChangedAt,
                .CrashCountInWindow = status.CrashCount,
                .LastExitCode = status.LastExitCode
            }
        End Function

        Public Overrides Async Function GetPlayerCount(instanceId As String) As Task(Of Integer)
            Return Await _instanceManager.GetPlayerCountAsync(instanceId)
        End Function

        Public Overrides Async Function StartInstance(instanceId As String) As Task(Of Boolean)
            Return Await _instanceManager.StartInstanceAsync(instanceId)
        End Function

        Public Overrides Async Function StopInstance(instanceId As String,
                                                     Optional gracefulTimeoutMs As Integer = 10000) As Task(Of Boolean)
            Return Await _instanceManager.StopInstanceAsync(instanceId, gracefulTimeoutMs)
        End Function

        Public Overrides Async Function SendRconCommand(instanceId As String,
                                                         command As String) As Task(Of String)
            Return Await _instanceManager.SendRconCommandAsync(instanceId, command)
        End Function

        ''' <summary>
        ''' Delegates to the RestartCoordinator singleton for
        ''' ready-signal waits. Resolved from the DI container
        ''' lazily so this class doesn't need a ctor dep on the
        ''' coordinator (which would add another node to the
        ''' dependency graph around construction time).
        ''' </summary>
        Public Overrides Async Function WaitForReadySignal(instanceId As String,
                                                            timeoutSeconds As Integer) As Task(Of Boolean)
            Dim coordinator = ManagerProgram.Services?.GetService(Of RestartCoordinator)()
            If coordinator Is Nothing Then
                ' No coordinator wired — surface as a non-error
                ' failure so the enclosing sequence still
                ' progresses via the caller's fallback path.
                _logger.LogWarning(
                    "WaitForReadySignal called but RestartCoordinator is not registered")
                Return False
            End If
            Return Await coordinator.WaitForReadySignalAsync(instanceId, timeoutSeconds)
        End Function

        ''' <summary>
        ''' Delegates to RestartCoordinator.AcquireForInstanceAsync.
        ''' Same lazy-resolve pattern as WaitForReadySignal — no
        ''' ctor dep on the coordinator.
        ''' </summary>
        Public Overrides Async Function AcquireRestartSlot(instanceId As String) As Task(Of Boolean)
            Dim coordinator = ManagerProgram.Services?.GetService(Of RestartCoordinator)()
            If coordinator Is Nothing Then
                _logger.LogWarning(
                    "AcquireRestartSlot called but RestartCoordinator is not registered")
                Return False
            End If
            Return Await coordinator.AcquireForInstanceAsync(instanceId)
        End Function

        ''' <summary>
        ''' Delegates to RestartCoordinator.ReleaseForInstance.
        ''' Synchronous (sub, not function) because
        ''' CoordinatedRestartAction calls this from a Finally
        ''' block and VB doesn't permit Await in Finally. The
        ''' underlying release is synchronous anyway — just
        ''' semaphore.Release() calls.
        ''' </summary>
        Public Overrides Sub ReleaseRestartSlot(instanceId As String)
            Try
                Dim coordinator = ManagerProgram.Services?.GetService(Of RestartCoordinator)()
                If coordinator IsNot Nothing Then
                    coordinator.ReleaseForInstance(instanceId)
                End If
            Catch ex As Exception
                ' Release must never throw — it runs in Finally
                ' paths and an exception here would mask the
                ' original failure that caused the sequence to
                ' bail. Swallow + log.
                _logger.LogWarning(ex,
                    "ReleaseRestartSlot threw for {Id}", instanceId)
            End Try
        End Sub

        Public Overrides Function GetInstanceIdsForInstallation(installationId As String) As Task(Of IReadOnlyList(Of String))
            Dim ids = _instanceManager.GetInstanceIdsForInstallation(installationId)
            Return Task.FromResult(ids)
        End Function

        ''' <summary>
        ''' Resolve instance IDs for any rule scope. Implemented
        ''' as a direct DB query rather than going through
        ''' InstanceManager because the manager doesn't expose
        ''' Node/InstanceSet/AllInstances lookups (and adding
        ''' four near-identical helpers there for one caller
        ''' would be churn).
        '''
        ''' For Installation scope we still delegate to
        ''' InstanceManager so the existing call path keeps
        ''' running through the same code (any future caching
        ''' there benefits both). For other scopes we hit the
        ''' DB directly.
        '''
        ''' Returns Task(Of) for interface symmetry even though
        ''' we run synchronously — EF Core's sync APIs are fine
        ''' here (no await chain to preserve), and wrapping the
        ''' result in Task.FromResult avoids polluting the
        ''' interface with a sync variant.
        ''' </summary>
        Public Overrides Function GetInstanceIdsForScope(scope As RuleScope,
                                                          targetId As String,
                                                          gameFilter As String) As Task(Of IReadOnlyList(Of String))
            ' Instance scope is trivial — a single ID, no DB hit.
            ' GameFilter is ignored here per contract.
            If scope = RuleScope.Instance Then
                Dim singleton As IReadOnlyList(Of String) =
                    If(String.IsNullOrEmpty(targetId),
                       CType(New List(Of String)(), IReadOnlyList(Of String)),
                       CType(New List(Of String) From {targetId}, IReadOnlyList(Of String)))
                Return Task.FromResult(singleton)
            End If

            ' Installation scope keeps going through the
            ' manager so existing wiring is unchanged.
            If scope = RuleScope.Installation Then
                Dim ids = _instanceManager.GetInstanceIdsForInstallation(targetId)
                If Not String.IsNullOrEmpty(gameFilter) Then
                    ' GameFilter on Installation scope is normally
                    ' redundant (an installation has one game),
                    ' but apply it defensively in case the rule's
                    ' filter was set when the scope changed.
                    ids = ApplyGameFilter(ids, gameFilter)
                End If
                Return Task.FromResult(ids)
            End If

            ' Node, InstanceSet, AllInstances — direct DB query.
            Dim result As New List(Of String)
            Try
                Using dbScope = ManagerProgram.Services.CreateScope()
                    Dim db = dbScope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    Dim query = db.Instances.AsQueryable()

                    Select Case scope
                        Case RuleScope.Node
                            ' Node scope filters via the parent
                            ' Installation's NodeId. EF generates
                            ' the JOIN automatically.
                            If Not String.IsNullOrEmpty(targetId) Then
                                query = query.Where(Function(i) i.Installation.NodeId = targetId)
                            Else
                                ' Empty TargetId on Node scope is
                                ' a misconfigured rule — return
                                ' empty rather than every instance.
                                Return Task.FromResult(
                                    CType(result, IReadOnlyList(Of String)))
                            End If

                        Case RuleScope.InstanceSet
                            If Not String.IsNullOrEmpty(targetId) Then
                                query = query.Where(Function(i) i.InstanceSetTag = targetId)
                            Else
                                Return Task.FromResult(
                                    CType(result, IReadOnlyList(Of String)))
                            End If

                        Case RuleScope.AllInstances
                            ' No scope-level filter; gameFilter
                            ' applied below if set.

                        Case Else
                            ' Unknown scope — return empty rather
                            ' than guessing.
                            Return Task.FromResult(
                                CType(result, IReadOnlyList(Of String)))
                    End Select

                    If Not String.IsNullOrEmpty(gameFilter) Then
                        query = query.Where(Function(i) i.GameId = gameFilter)
                    End If

                    result = query.Select(Function(i) i.InstanceId).ToList()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "GetInstanceIdsForScope failed for scope={Scope} target={Target}",
                    scope, targetId)
            End Try

            Return Task.FromResult(CType(result, IReadOnlyList(Of String)))
        End Function

        ''' <summary>
        ''' Filter an existing list of instance IDs by GameId via
        ''' a DB lookup. Used by the Installation-scope path which
        ''' gets its IDs from the InstanceManager (without GameId)
        ''' but needs to honour the rule's GameFilter.
        ''' </summary>
        Private Function ApplyGameFilter(ids As IReadOnlyList(Of String),
                                          gameFilter As String) As IReadOnlyList(Of String)
            If ids Is Nothing OrElse ids.Count = 0 Then Return ids
            Try
                Using dbScope = ManagerProgram.Services.CreateScope()
                    Dim db = dbScope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim filtered = db.Instances.
                        Where(Function(i) ids.Contains(i.InstanceId) AndAlso
                                          i.GameId = gameFilter).
                        Select(Function(i) i.InstanceId).
                        ToList()
                    Return CType(filtered, IReadOnlyList(Of String))
                End Using
            Catch
                ' On failure, return the unfiltered list — rule
                ' execution proceeds rather than silently no-oping.
                Return ids
            End Try
        End Function

        Public Overrides Async Function UpdateInstallation(installationId As String) As Task(Of Boolean)
            Return Await _installationManager.UpdateAsync(installationId)
        End Function

        Public Overrides Async Function SendNotification(destinationId As String,
                                                          message As String,
                                                          severity As NotificationSeverity) As Task
            ' Phase 4b-1.5: build a NotificationTokens bundle from
            ' the firing rule's context so {Token} substitutions in
            ' the user-authored message can resolve to actual
            ' values (rule name, target instance/installation/node
            ' display names, etc.). The bundle is built per-call
            ' rather than cached on the context because rule
            ' contexts are short-lived (one per firing) so caching
            ' wouldn't help, and we'd rather pay the DB hit only
            ' for rules that actually invoke a NotifyAction.
            Dim tokens = BuildTokensFromContext()
            Await _notificationService.SendToDestinationAsync(
                destinationId, message, severity, tokens)
        End Function

        ''' <summary>
        ''' Constructs a NotificationTokens bundle reflecting the
        ''' firing rule's scope and target. Resolves IDs to display
        ''' names by looking up the relevant entity. Tolerates
        ''' missing entities (e.g. instance was deleted between
        ''' rule arming and firing) by leaving the corresponding
        ''' name token empty.
        '''
        ''' For multi-instance scopes (Installation, Node,
        ''' InstanceSet, AllInstances) we don't populate per-
        ''' instance tokens — the rule's action targets the whole
        ''' set, so {InstanceName} would be ambiguous. Tokens
        ''' that don't apply substitute to empty string in
        ''' SubstituteTokens.
        ''' </summary>
        Private Function BuildTokensFromContext() As NotificationTokens
            Dim t As New NotificationTokens With {
                .RuleName = _rule.DisplayName
            }

            Try
                Using dbScope = ManagerProgram.Services.CreateScope()
                    Dim db = dbScope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    Select Case _rule.Scope
                        Case RuleScope.Instance
                            Dim inst = db.Instances.
                                Where(Function(i) i.InstanceId = _rule.TargetId).
                                FirstOrDefault()
                            If inst IsNot Nothing Then
                                t.InstanceId = inst.InstanceId
                                t.InstanceName = inst.DisplayName
                                t.GameId = inst.GameId
                                t.InstallationId = inst.InstallationId
                                ' Walk up to installation + node for
                                ' richer token coverage. The user's
                                ' message might reference {NodeName}
                                ' even on an Instance-scoped rule.
                                Dim install = db.Installations.
                                    Where(Function(x) x.InstallationId = inst.InstallationId).
                                    FirstOrDefault()
                                If install IsNot Nothing Then
                                    t.InstallationName = install.DisplayName
                                    Dim node = db.Nodes.
                                        Where(Function(n) n.NodeId = install.NodeId).
                                        FirstOrDefault()
                                    If node IsNot Nothing Then
                                        t.NodeId = node.NodeId
                                        t.NodeName = node.DisplayName
                                    End If
                                End If
                            End If

                        Case RuleScope.Installation
                            Dim install = db.Installations.
                                Where(Function(i) i.InstallationId = _rule.TargetId).
                                FirstOrDefault()
                            If install IsNot Nothing Then
                                t.InstallationId = install.InstallationId
                                t.InstallationName = install.DisplayName
                                t.GameId = install.GameId
                                Dim node = db.Nodes.
                                    Where(Function(n) n.NodeId = install.NodeId).
                                    FirstOrDefault()
                                If node IsNot Nothing Then
                                    t.NodeId = node.NodeId
                                    t.NodeName = node.DisplayName
                                End If
                            End If

                        Case RuleScope.Node
                            Dim node = db.Nodes.
                                Where(Function(n) n.NodeId = _rule.TargetId).
                                FirstOrDefault()
                            If node IsNot Nothing Then
                                t.NodeId = node.NodeId
                                t.NodeName = node.DisplayName
                            End If
                            ' GameFilter, when set, is the GameId
                            ' for this rule's effective scope.
                            If Not String.IsNullOrEmpty(_rule.GameFilter) Then
                                t.GameId = _rule.GameFilter
                            End If

                        Case RuleScope.InstanceSet
                            ' No first-class entity for sets — the
                            ' tag IS the identity. Surface it via
                            ' GameId only when a filter pins it down,
                            ' otherwise leave per-target tokens empty.
                            If Not String.IsNullOrEmpty(_rule.GameFilter) Then
                                t.GameId = _rule.GameFilter
                            End If

                        Case RuleScope.AllInstances
                            If Not String.IsNullOrEmpty(_rule.GameFilter) Then
                                t.GameId = _rule.GameFilter
                            End If
                    End Select
                End Using
            Catch ex As Exception
                ' Token resolution failure is non-fatal — the
                ' notification still goes out, just with empty
                ' substitutions where DB lookups failed.
                _logger.LogWarning(ex,
                    "Failed to resolve notification tokens for rule {Id}", _rule.RuleId)
            End Try

            Return t
        End Function

        Public Overrides Sub LogProgress(message As String)
            _logger.LogInformation("[Rule {RuleId}] {Message}", _rule.RuleId, message)
        End Sub

    End Class

    ' ============================================================
    '  CronTimer — fires a callback on a cron schedule
    ' ============================================================

    Public Class CronTimer

        Private ReadOnly _cronExpression As String
        Private ReadOnly _callback As Action
        Private ReadOnly _logger As ILogger
        Private ReadOnly _schedule As CrontabSchedule
        Private _timerTask As Task
        Private _running As Boolean

        Public Sub New(cronExpression As String,
                       callback As Action,
                       logger As ILogger)
            _cronExpression = cronExpression
            _callback = callback
            _logger = logger
            _schedule = CrontabSchedule.Parse(cronExpression)
        End Sub

        Public Sub Start(cancellation As CancellationToken)
            _running = True
            _timerTask = Task.Run(Function() RunLoopAsync(cancellation))
        End Sub

        Public Sub [Stop]()
            _running = False
        End Sub

        Private Async Function RunLoopAsync(cancellation As CancellationToken) As Task
            While _running AndAlso Not cancellation.IsCancellationRequested
                Dim hadError = False
                Try
                    Dim now = DateTime.Now
                    Dim nextOccurrence = _schedule.GetNextOccurrence(now)
                    Dim delayMs = CInt(Math.Max((nextOccurrence - now).TotalMilliseconds, 1000))

                    Await Task.Delay(delayMs, cancellation)

                    If _running AndAlso Not cancellation.IsCancellationRequested Then
                        Try
                            _callback()
                        Catch ex As Exception
                            _logger.LogWarning(ex, "Cron callback failed for '{Expr}'", _cronExpression)
                        End Try
                    End If
                Catch ex As OperationCanceledException
                    Exit While
                Catch ex As Exception
                    _logger.LogError(ex, "CronTimer error for '{Expr}'", _cronExpression)
                    hadError = True
                End Try

                ' Back off after error (outside Catch — VB.Net disallows Await in Catch)
                If hadError Then
                    Try
                        Await Task.Delay(60000, cancellation)
                    Catch
                        Exit While
                    End Try
                End If
            End While
        End Function

    End Class

End Namespace
