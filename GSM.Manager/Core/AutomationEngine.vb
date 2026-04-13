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
        ''' </summary>
        Public Sub ReloadRules()
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
            ' StateChange and VersionMismatch triggers are event-driven
            ' and will be wired up when those events are implemented
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
        '  Rule deserialization
        ' ============================================================

        Private Function DeserializeRule(entity As AutomationRuleEntity) As AutomationRule
            ' For now, create a minimal rule structure.
            ' Full JSON deserialization of polymorphic triggers/conditions/actions
            ' would require a custom JsonConverter — left as a TODO.
            Dim rule As New AutomationRule With {
                .RuleId = entity.RuleId,
                .DisplayName = entity.RuleName,
                .IsEnabled = entity.IsEnabled,
                .TargetId = entity.TargetId
            }

            ' Parse scope
            Dim scopeVal As RuleScope
            If [Enum].TryParse(entity.ScopeKind, True, scopeVal) Then
                rule.Scope = scopeVal
            End If

            ' Trigger, conditions, and action deserialization
            ' requires polymorphic JSON handling — placeholder for now
            If Not String.IsNullOrEmpty(entity.TriggerJson) Then
                Try
                    ' Simple: check if it's a schedule trigger
                    Dim triggerDoc = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(entity.TriggerJson)
                    If triggerDoc IsNot Nothing AndAlso triggerDoc.ContainsKey("cronExpression") Then
                        rule.Trigger = New ScheduleTrigger With {
                            .CronExpression = triggerDoc("cronExpression")
                        }
                    ElseIf triggerDoc IsNot Nothing AndAlso triggerDoc.ContainsKey("triggerId") Then
                        Select Case triggerDoc("triggerId")
                            Case "manual"
                                rule.Trigger = New ManualTrigger()
                            Case "version_mismatch"
                                rule.Trigger = New VersionMismatchTrigger()
                        End Select
                    End If
                Catch
                End Try
            End If

            Return rule
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

        Public Overrides Function GetInstanceIdsForInstallation(installationId As String) As Task(Of IReadOnlyList(Of String))
            Dim ids = _instanceManager.GetInstanceIdsForInstallation(installationId)
            Return Task.FromResult(ids)
        End Function

        Public Overrides Async Function UpdateInstallation(installationId As String) As Task(Of Boolean)
            Return Await _installationManager.UpdateAsync(installationId)
        End Function

        Public Overrides Async Function SendNotification(pluginId As String,
                                                          message As String,
                                                          severity As NotificationSeverity) As Task
            Await _notificationService.SendSimpleAsync(pluginId, message, severity)
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
