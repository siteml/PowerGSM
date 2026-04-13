Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports GSM.Automation
Imports GSM.Data
Imports GSM.Plugin

' ============================================================
'  AutomationEngine
'
'  Executes automation rules defined in the manager database.
'  Rules are loaded at startup and re-evaluated whenever:
'    - An instance state changes (InstanceStateChanged event)
'    - A cron trigger fires (background scheduler)
'    - A player count threshold is crossed (log parser output)
'    - A version update is detected (version poller)
'    - The operator fires a rule manually via UI or Discord
'
'  Execution model:
'    Each rule execution runs on the thread pool. Long-running
'    actions (WaitForPlayerCount, UpdateInstallation) may run
'    for minutes — they respect CancellationToken throughout.
'
'    Concurrent executions of the same rule are governed by
'    the rule's OnConcurrentFire setting:
'      Skip  - ignore new fire if already running (default)
'      Queue - hold the new fire, execute after current finishes
'      Cancel - cancel current execution, start fresh
'
'  Rule serialisation:
'    Rules are stored in the DB as JSON blobs. The engine
'    deserialises triggers, conditions, and actions using
'    a type-discriminated JSON approach: each JSON object
'    carries a "type" field that names the concrete class.
'    This is how the automation engine stays extensible -
'    new trigger/condition/action types can be added in
'    plugins without changing the engine's deserialisation code.
' ============================================================

Namespace GSM.Core

    Public Class AutomationEngine

        Private ReadOnly _dbFactory As IDbContextFactory(Of GsmDbContext)
        Private ReadOnly _instanceManager As InstanceManager
        Private ReadOnly _installationManager As InstallationManager
        Private ReadOnly _notificationService As NotificationService
        Private ReadOnly _logger As ILogger(Of AutomationEngine)

        ' Live execution tracking. Key = RuleId.
        Private ReadOnly _running As New ConcurrentDictionary(Of String, RuleExecution)(
            StringComparer.OrdinalIgnoreCase)

        ' Queued fires waiting for a current execution to finish.
        ' Key = RuleId, Value = queued trigger context.
        Private ReadOnly _queued As New ConcurrentDictionary(Of String, QueuedFire)(
            StringComparer.OrdinalIgnoreCase)

        ' Cron scheduler - one timer per rule with a CronTrigger.
        Private ReadOnly _cronTimers As New ConcurrentDictionary(Of String, CronTimer)(
            StringComparer.OrdinalIgnoreCase)

        ' Version poll - one timer per installation with UpdateAvailableTrigger.
        Private ReadOnly _versionPollTimers As New ConcurrentDictionary(Of String, Timer)(
            StringComparer.OrdinalIgnoreCase)

        Private ReadOnly _shutdownCts As New CancellationTokenSource()

        Public Sub New(dbFactory As IDbContextFactory(Of GsmDbContext),
                       instanceManager As InstanceManager,
                       installationManager As InstallationManager,
                       notificationService As NotificationService,
                       logger As ILogger(Of AutomationEngine))
            _dbFactory = dbFactory
            _instanceManager = instanceManager
            _installationManager = installationManager
            _notificationService = notificationService
            _logger = logger
        End Sub


        ' ============================================================
        '  STARTUP
        ' ============================================================

        Public Async Function StartAsync(cancellation As CancellationToken) As Task
            _logger.LogInformation("AutomationEngine: starting")

            ' Load all enabled rules and register their triggers.
            Using db = _dbFactory.CreateDbContext()
                Dim rules = Await db.AutomationRules.
                    Where(Function(r) r.IsEnabled).
                    ToListAsync(cancellation)

                For Each rule In rules
                    RegisterTriggers(rule)
                Next

                _logger.LogInformation(
                    "AutomationEngine: loaded {Count} rule(s)", rules.Count)
            End Using

            ' Subscribe to instance state changes from InstanceManager.
            AddHandler _instanceManager.InstanceStateChanged,
                AddressOf OnInstanceStateChanged
        End Function

        Public Async Function StopAsync() As Task
            _logger.LogInformation("AutomationEngine: stopping")
            _shutdownCts.Cancel()

            ' Cancel all running executions.
            For Each exec In _running.Values
                exec.Cts.Cancel()
            Next

            ' Wait for all running executions to finish.
            Dim runningTasks = _running.Values.Select(Function(e) e.Task).ToList()
            If runningTasks.Any() Then
                Await Task.WhenAll(runningTasks).ConfigureAwait(False)
            End If

            ' Dispose all cron timers.
            For Each timer In _cronTimers.Values
                timer.Dispose()
            Next
            For Each timer In _versionPollTimers.Values
                timer.Dispose()
            Next

            RemoveHandler _instanceManager.InstanceStateChanged,
                AddressOf OnInstanceStateChanged
        End Function


        ' ============================================================
        '  TRIGGER REGISTRATION
        '  Called for each rule at startup and when rules are created/
        '  modified. Sets up the appropriate trigger mechanism.
        ' ============================================================

        Private Sub RegisterTriggers(rule As AutomationRuleEntity)
            Dim trigger = DeserializeTrigger(rule.TriggerJson)
            If trigger Is Nothing Then
                _logger.LogWarning(
                    "AutomationEngine: could not deserialise trigger for rule '{Name}'",
                    rule.DisplayName)
                Return
            End If

            Select Case trigger.GetType().Name

                Case NameOf(CronTrigger)
                    RegisterCronTrigger(rule, CType(trigger, CronTrigger))

                Case NameOf(UpdateAvailableTrigger)
                    RegisterVersionPollTrigger(rule, CType(trigger, UpdateAvailableTrigger))

                Case NameOf(InstanceStateChangedTrigger),
                     NameOf(CrashLoopHaltedTrigger),
                     NameOf(PlayerCountThresholdTrigger),
                     NameOf(LogEventTrigger)
                    ' These are event-driven - registered via OnInstanceStateChanged
                    ' or log parser output. No timer needed.

                Case NameOf(ManualTrigger)
                    ' Only fires via FireManualAsync - no registration needed.

            End Select
        End Sub

        Private Sub RegisterCronTrigger(rule As AutomationRuleEntity,
                                         trigger As CronTrigger)
            ' Unregister any existing timer for this rule.
            Dim existing As CronTimer = Nothing
            If _cronTimers.TryRemove(rule.RuleId, existing) Then
                existing.Dispose()
            End If

            Try
                Dim schedule = NCrontab.CrontabSchedule.Parse(trigger.CronExpression)
                Dim timer As New CronTimer(rule.RuleId, schedule, trigger.TimeZoneId,
                    Sub()
                        _logger.LogDebug(
                            "AutomationEngine: cron trigger fired for rule '{Name}'",
                            rule.DisplayName)
                        Task.Run(Async Function()
                                     Await FireRuleAsync(rule.RuleId,
                                         $"Cron: {trigger.CronExpression}",
                                         _shutdownCts.Token)
                                 End Function)
                    End Sub)
                _cronTimers.TryAdd(rule.RuleId, timer)
                _logger.LogInformation(
                    "AutomationEngine: registered cron trigger for '{Name}': {Expr}",
                    rule.DisplayName, trigger.CronExpression)
            Catch ex As Exception
                _logger.LogError(ex,
                    "AutomationEngine: invalid cron expression '{Expr}' in rule '{Name}'",
                    trigger.CronExpression, rule.DisplayName)
            End Try
        End Sub

        Private Sub RegisterVersionPollTrigger(rule As AutomationRuleEntity,
                                                trigger As UpdateAvailableTrigger)
            ' Version polling for each installation-scoped rule.
            ' Fires when GetLatestVersion() != GetCurrentVersion() for the target.
            Dim interval = TimeSpan.FromMinutes(trigger.PollIntervalMinutes)

            Dim timer As New Timer(
                Async Sub(state)
                    Await CheckForVersionUpdateAsync(rule, _shutdownCts.Token)
                End Sub,
                Nothing,
                interval, interval)

            Dim old As Timer = Nothing
            If _versionPollTimers.TryRemove(rule.RuleId, old) Then old.Dispose()
            _versionPollTimers.TryAdd(rule.RuleId, timer)

            _logger.LogInformation(
                "AutomationEngine: registered version poll for rule '{Name}' " &
                "(every {Min} min)", rule.DisplayName, trigger.PollIntervalMinutes)
        End Sub


        ' ============================================================
        '  EVENT-DRIVEN TRIGGER DISPATCH
        '  Checks all enabled rules to see if a state change event
        '  matches any of their triggers.
        ' ============================================================

        Private Sub OnInstanceStateChanged(instanceId As String,
                                            newState As InstanceState,
                                            reason As String)
            Task.Run(Async Function()
                         Await DispatchStateChangeTriggerAsync(
                             instanceId, newState, reason, _shutdownCts.Token)
                     End Function)
        End Sub

        Private Async Function DispatchStateChangeTriggerAsync(
                instanceId As String,
                newState As InstanceState,
                reason As String,
                cancellation As CancellationToken) As Task

            Using db = _dbFactory.CreateDbContext()
                ' Find all enabled rules whose trigger matches this event.
                Dim rules = Await db.AutomationRules.
                    Where(Function(r) r.IsEnabled AndAlso
                                      (r.TargetId = instanceId OrElse
                                       r.Scope = "Global")).
                    ToListAsync(cancellation)

                For Each rule In rules
                    Dim trigger = DeserializeTrigger(rule.TriggerJson)

                    Dim shouldFire = False
                    Dim triggerLabel = String.Empty

                    If TypeOf trigger Is InstanceStateChangedTrigger Then
                        Dim t = CType(trigger, InstanceStateChangedTrigger)
                        If Not t.WatchedStates.Any() OrElse
                           t.WatchedStates.Contains(newState) Then
                            shouldFire = True
                            triggerLabel = $"State changed to {newState}"
                        End If

                    ElseIf TypeOf trigger Is CrashLoopHaltedTrigger Then
                        If newState = InstanceState.CrashLoopHalted Then
                            shouldFire = True
                            triggerLabel = "Crash loop halted"
                        End If
                    End If

                    If shouldFire Then
                        Await FireRuleAsync(rule.RuleId, triggerLabel, cancellation)
                    End If
                Next
            End Using
        End Function

        Private Async Function CheckForVersionUpdateAsync(
                rule As AutomationRuleEntity,
                cancellation As CancellationToken) As Task

            If String.IsNullOrEmpty(rule.TargetId) Then Return

            Try
                Dim hasUpdate = Await _installationManager.CheckForUpdateAsync(
                    rule.TargetId, cancellation)
                If hasUpdate Then
                    _logger.LogInformation(
                        "AutomationEngine: update available for installation " &
                        "'{Id}' - firing rule '{Name}'",
                        rule.TargetId, rule.DisplayName)
                    Await FireRuleAsync(rule.RuleId, "Update available", cancellation)
                End If
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "AutomationEngine: version check error for rule '{Name}'",
                    rule.DisplayName)
            End Try
        End Function


        ' ============================================================
        '  MANUAL FIRE
        '  Called by the UI "Run now" button and Discord commands.
        ' ============================================================

        Public Async Function FireManualAsync(ruleId As String,
                                               issuedBy As String,
                                               cancellation As CancellationToken) As Task(Of RuleExecutionRecord)

            _logger.LogInformation(
                "AutomationEngine: manual fire of rule '{Id}' by '{By}'",
                ruleId, issuedBy)

            Return Await FireRuleAsync(ruleId,
                $"Manual: {issuedBy}", cancellation)
        End Function


        ' ============================================================
        '  CORE EXECUTION PIPELINE
        '  Trigger → Conditions → Action
        ' ============================================================

        Private Async Function FireRuleAsync(ruleId As String,
                                              triggerSource As String,
                                              cancellation As CancellationToken) As Task(Of RuleExecutionRecord)

            ' Load the rule from DB.
            Dim rule As AutomationRuleEntity
            Using db = _dbFactory.CreateDbContext()
                rule = Await db.AutomationRules.FindAsync(
                    New Object() {ruleId}, cancellation)
                If rule Is Nothing OrElse Not rule.IsEnabled Then Return Nothing
            End Using

            ' Handle concurrent fire policy.
            Dim existing As RuleExecution = Nothing
            If _running.TryGetValue(ruleId, existing) Then
                Select Case rule.OnConcurrentFire
                    Case "Skip"
                        _logger.LogDebug(
                            "AutomationEngine: rule '{Name}' already running - skipping",
                            rule.DisplayName)
                        Return Nothing

                    Case "Queue"
                        _queued(ruleId) = New QueuedFire With {
                            .TriggerSource = triggerSource
                        }
                        _logger.LogDebug(
                            "AutomationEngine: rule '{Name}' queued", rule.DisplayName)
                        Return Nothing

                    Case "Cancel"
                        _logger.LogDebug(
                            "AutomationEngine: cancelling running rule '{Name}'",
                            rule.DisplayName)
                        existing.Cts.Cancel()
                        Await existing.Task.ConfigureAwait(False)
                End Select
            End If

            ' Build the execution context.
            Dim cts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellation, _shutdownCts.Token)
            Dim exec As New RuleExecution With {.Cts = cts}

            exec.Task = Task.Run(
                Async Function() As Task(Of RuleExecutionRecord)
                    Return Await ExecuteRuleAsync(rule, triggerSource, cts.Token)
                End Function, cts.Token)

            _running.TryAdd(ruleId, exec)

            Try
                Dim result = Await exec.Task
                Return result
            Finally
                Dim removedExecution As RuleExecution = Nothing
                _running.TryRemove(ruleId, removedExecution)
                cts.Dispose()

                ' If a queued fire is waiting, execute it now.
                Dim queued As QueuedFire = Nothing
                If _queued.TryRemove(ruleId, queued) Then
                    Dim ignoredTask = Task.Run(
                        Async Function() As Task
                            Await FireRuleAsync(ruleId,
                                queued.TriggerSource,
                                _shutdownCts.Token).ConfigureAwait(False)
                        End Function)
                End If
            End Try
        End Function

        Private Async Function ExecuteRuleAsync(rule As AutomationRuleEntity,
                                                 triggerSource As String,
                                                 cancellation As CancellationToken) As Task(Of RuleExecutionRecord)

            Dim sw = System.Diagnostics.Stopwatch.StartNew()
            Dim conditionResults As New List(Of String)
            Dim record As New RuleExecutionRecord With {
                .ExecutionId = Guid.NewGuid().ToString(),
                .RuleId = rule.RuleId,
                .ExecutedAt = DateTime.UtcNow,
                .TriggerSource = triggerSource
            }

            _logger.LogInformation(
                "AutomationEngine: executing rule '{Name}' (trigger: {Trigger})",
                rule.DisplayName, triggerSource)

            Try
                ' Build the rule context for this execution.
                Dim ctx = BuildRuleContext(rule)

                ' ---- Evaluate conditions ----
                Dim conditions = DeserializeConditions(rule.ConditionsJson)
                Dim conditionsPassed = True

                For Each condition In conditions
                    Dim result = Await condition.Evaluate(ctx, cancellation)
                    Dim label = $"[{condition.DisplayLabel}] " &
                                $"{If(result.Passed, "PASS", "FAIL")}: {result.Reason}"
                    conditionResults.Add(label)
                    _logger.LogDebug("AutomationEngine: condition {Label}", label)

                    If Not result.Passed Then
                        If rule.ConditionMode = "All" Then
                            conditionsPassed = False
                            Exit For
                        End If
                    Else
                        If rule.ConditionMode = "Any" Then
                            ' One pass is enough in Any mode.
                            conditionsPassed = True
                            Exit For
                        End If
                    End If
                Next

                record.ConditionResultsJson = JsonSerializer.Serialize(conditionResults)

                If Not conditionsPassed Then
                    _logger.LogInformation(
                        "AutomationEngine: rule '{Name}' conditions not met - not executing",
                        rule.DisplayName)
                    record.ActionSuccess = False
                    record.ActionMessage = "Conditions not met."
                    Return Await SaveExecutionRecord(record, rule, sw.ElapsedMilliseconds)
                End If

                ' ---- Execute action ----
                Dim action = DeserializeAction(rule.ActionJson)
                If action Is Nothing Then
                    record.ActionSuccess = False
                    record.ActionMessage = "Could not deserialise action."
                    Return Await SaveExecutionRecord(record, rule, sw.ElapsedMilliseconds)
                End If

                _logger.LogInformation(
                    "AutomationEngine: running action '{Label}' for rule '{Name}'",
                    action.DisplayLabel, rule.DisplayName)

                Dim actionResult = Await action.Execute(ctx, cancellation)

                record.ActionSuccess = actionResult.Success
                record.ActionMessage = actionResult.Message
                record.ActionDetailsJson = JsonSerializer.Serialize(
                    If(actionResult.Details, New List(Of String)()))

                _logger.LogInformation(
                    "AutomationEngine: rule '{Name}' {Result}: {Msg}",
                    rule.DisplayName,
                    If(actionResult.Success, "succeeded", "failed"),
                    actionResult.Message)

            Catch ex As OperationCanceledException
                record.ActionSuccess = False
                record.ActionMessage = "Execution cancelled."
                _logger.LogInformation(
                    "AutomationEngine: rule '{Name}' cancelled", rule.DisplayName)
            Catch ex As Exception
                record.ActionSuccess = False
                record.ActionMessage = "Unhandled error: " & ex.Message
                _logger.LogError(ex,
                    "AutomationEngine: rule '{Name}' threw an exception",
                    rule.DisplayName)
            End Try

            Return Await SaveExecutionRecord(record, rule, sw.ElapsedMilliseconds)
        End Function


        ' ============================================================
        '  RULE CONTEXT CONSTRUCTION
        '  Builds the IRuleContext passed to conditions and actions.
        '  Wires up the manager services the context needs.
        ' ============================================================

        Private Function BuildRuleContext(rule As AutomationRuleEntity) As RuleContext
            Return New RuleContextImpl(
                rule.RuleId,
                rule.TargetId,
                rule.Scope,
                _instanceManager,
                _installationManager,
                _notificationService,
                _logger)
        End Function


        ' ============================================================
        '  PERSISTENCE
        ' ============================================================

        Private Async Function SaveExecutionRecord(
                record As RuleExecutionRecord,
                rule As AutomationRuleEntity,
                durationMs As Long) As Task(Of RuleExecutionRecord)

            record.DurationMs = durationMs

            Using db = _dbFactory.CreateDbContext()
                ' Update rule stats.
                Dim ruleEntity = Await db.AutomationRules.FindAsync(rule.RuleId)
                If ruleEntity IsNot Nothing Then
                    ruleEntity.LastFiredAt = record.ExecutedAt
                    ruleEntity.FireCount += 1
                End If

                ' Save execution history.
                db.RuleExecutionHistory.Add(New RuleExecutionHistoryEntity With {
                    .ExecutionId = record.ExecutionId,
                    .RuleId = record.RuleId,
                    .ExecutedAt = record.ExecutedAt,
                    .TriggerSource = record.TriggerSource,
                    .ConditionResultsJson = If(record.ConditionResultsJson, "[]"),
                    .ActionSuccess = record.ActionSuccess,
                    .ActionMessage = If(record.ActionMessage, ""),
                    .ActionDetailsJson = If(record.ActionDetailsJson, "[]"),
                    .DurationMs = record.DurationMs
                })

                ' Prune history cap (max 100 per rule).
                Dim historyCount = Await db.RuleExecutionHistory.
                    CountAsync(Function(h) h.RuleId = record.RuleId)
                If historyCount > 100 Then
                    Dim oldest = Await db.RuleExecutionHistory.
                        Where(Function(h) h.RuleId = record.RuleId).
                        OrderBy(Function(h) h.ExecutedAt).
                        Take(historyCount - 100).
                        ToListAsync()
                    db.RuleExecutionHistory.RemoveRange(oldest)
                End If

                Await db.SaveChangesAsync()
            End Using

            Return record
        End Function


        ' ============================================================
        '  RULE CRUD
        '  Called by the UI to create/update/delete rules.
        ' ============================================================

        Public Async Function CreateRuleAsync(
                rule As AutomationRuleEntity,
                cancellation As CancellationToken) As Task(Of AutomationRuleEntity)

            rule.RuleId = Guid.NewGuid().ToString()
            rule.CreatedAt = DateTime.UtcNow
            rule.LastModifiedAt = DateTime.UtcNow

            Using db = _dbFactory.CreateDbContext()
                db.AutomationRules.Add(rule)
                Await db.SaveChangesAsync(cancellation)
            End Using

            If rule.IsEnabled Then RegisterTriggers(rule)

            Return rule
        End Function

        Public Async Function UpdateRuleAsync(
                rule As AutomationRuleEntity,
                cancellation As CancellationToken) As Task

            ' Cancel and remove any existing execution for this rule.
            Dim exec As RuleExecution = Nothing
            If _running.TryRemove(rule.RuleId, exec) Then
                exec.Cts.Cancel()
            End If

            ' Remove old trigger registration.
            Dim cron As CronTimer = Nothing
            If _cronTimers.TryRemove(rule.RuleId, cron) Then cron.Dispose()
            Dim vp As Timer = Nothing
            If _versionPollTimers.TryRemove(rule.RuleId, vp) Then vp.Dispose()

            Using db = _dbFactory.CreateDbContext()
                Dim existing = Await db.AutomationRules.FindAsync(
                    New Object() {rule.RuleId}, cancellation)
                If existing Is Nothing Then Return

                existing.DisplayName = rule.DisplayName
                existing.IsEnabled = rule.IsEnabled
                existing.Scope = rule.Scope
                existing.TargetId = rule.TargetId
                existing.TriggerJson = rule.TriggerJson
                existing.ConditionsJson = rule.ConditionsJson
                existing.ConditionMode = rule.ConditionMode
                existing.ActionJson = rule.ActionJson
                existing.OnConcurrentFire = rule.OnConcurrentFire
                existing.LastModifiedAt = DateTime.UtcNow

                Await db.SaveChangesAsync(cancellation)
            End Using

            ' Re-register triggers if the rule is still enabled.
            If rule.IsEnabled Then RegisterTriggers(rule)
        End Function

        Public Async Function DeleteRuleAsync(ruleId As String,
                                               cancellation As CancellationToken) As Task

            ' Cancel running execution.
            Dim exec As RuleExecution = Nothing
            If _running.TryRemove(ruleId, exec) Then exec.Cts.Cancel()

            ' Remove timers.
            Dim cron As CronTimer = Nothing
            If _cronTimers.TryRemove(ruleId, cron) Then cron.Dispose()
            Dim vp As Timer = Nothing
            If _versionPollTimers.TryRemove(ruleId, vp) Then vp.Dispose()

            Using db = _dbFactory.CreateDbContext()
                Dim rule = Await db.AutomationRules.FindAsync(
                    New Object() {ruleId}, cancellation)
                If rule IsNot Nothing Then
                    db.AutomationRules.Remove(rule)
                    Await db.SaveChangesAsync(cancellation)
                End If
            End Using
        End Function

        Public Async Function GetRulesAsync(
                cancellation As CancellationToken) As Task(Of List(Of AutomationRuleEntity))
            Using db = _dbFactory.CreateDbContext()
                Return Await db.AutomationRules.
                    OrderBy(Function(r) r.DisplayName).
                    ToListAsync(cancellation)
            End Using
        End Function

        Public Async Function GetExecutionHistoryAsync(
                ruleId As String,
                cancellation As CancellationToken) As Task(Of List(Of RuleExecutionHistoryEntity))
            Using db = _dbFactory.CreateDbContext()
                Return Await db.RuleExecutionHistory.
                    Where(Function(h) h.RuleId = ruleId).
                    OrderByDescending(Function(h) h.ExecutedAt).
                    Take(100).
                    ToListAsync(cancellation)
            End Using
        End Function


        ' ============================================================
        '  JSON DESERIALISATION
        '  Each type carries a "type" discriminator field.
        '  New trigger/condition/action types are added here as
        '  the system grows.
        ' ============================================================

        Private Shared ReadOnly JsonOpts As New JsonSerializerOptions With {
            .PropertyNameCaseInsensitive = True
        }

        Private Shared Function DeserializeTrigger(json As String) As ITrigger
            If String.IsNullOrEmpty(json) Then Return Nothing
            Try
                Dim doc = JsonDocument.Parse(json)
                Dim typeName = doc.RootElement.GetProperty("type").GetString()
                Select Case typeName
                    Case "cron"             : Return JsonSerializer.Deserialize(Of CronTrigger)(json, JsonOpts)
                    Case "updateAvailable"  : Return JsonSerializer.Deserialize(Of UpdateAvailableTrigger)(json, JsonOpts)
                    Case "instanceStateChanged" : Return JsonSerializer.Deserialize(Of InstanceStateChangedTrigger)(json, JsonOpts)
                    Case "crashLoopHalted"  : Return JsonSerializer.Deserialize(Of CrashLoopHaltedTrigger)(json, JsonOpts)
                    Case "playerCountThreshold" : Return JsonSerializer.Deserialize(Of PlayerCountThresholdTrigger)(json, JsonOpts)
                    Case "manual"           : Return JsonSerializer.Deserialize(Of ManualTrigger)(json, JsonOpts)
                    Case "logEvent"         : Return JsonSerializer.Deserialize(Of LogEventTrigger)(json, JsonOpts)
                    Case Else
                        Return Nothing
                End Select
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function DeserializeConditions(json As String) As List(Of ICondition)
            Dim result As New List(Of ICondition)()
            If String.IsNullOrEmpty(json) OrElse json = "[]" Then Return result
            Try
                Dim arr = JsonDocument.Parse(json).RootElement
                For Each elem In arr.EnumerateArray()
                    Dim typeName = elem.GetProperty("conditionId").GetString()
                    Dim condJson = elem.GetRawText()
                    Dim cond As ICondition = Nothing
                    Select Case typeName
                        Case "instanceStateIs"   : cond = JsonSerializer.Deserialize(Of InstanceStateIsCondition)(condJson, JsonOpts)
                        Case "playerCount"       : cond = JsonSerializer.Deserialize(Of PlayerCountCondition)(condJson, JsonOpts)
                        Case "waitForPlayerCount" : cond = JsonSerializer.Deserialize(Of WaitForPlayerCountCondition)(condJson, JsonOpts)
                        Case "timeInState"       : cond = JsonSerializer.Deserialize(Of TimeInStateCondition)(condJson, JsonOpts)
                        Case "installationNotLocked" : cond = JsonSerializer.Deserialize(Of InstallationNotLockedCondition)(condJson, JsonOpts)
                        Case "all"               : cond = JsonSerializer.Deserialize(Of AllCondition)(condJson, JsonOpts)
                        Case "any"               : cond = JsonSerializer.Deserialize(Of AnyCondition)(condJson, JsonOpts)
                        Case "not"               : cond = JsonSerializer.Deserialize(Of NotCondition)(condJson, JsonOpts)
                    End Select
                    If cond IsNot Nothing Then result.Add(cond)
                Next
            Catch
            End Try
            Return result
        End Function

        Private Shared Function DeserializeAction(json As String) As IAction
            If String.IsNullOrEmpty(json) Then Return Nothing
            Try
                Dim doc = JsonDocument.Parse(json)
                Dim actionId = doc.RootElement.GetProperty("actionId").GetString()
                Select Case actionId
                    Case "startInstance"     : Return JsonSerializer.Deserialize(Of StartInstanceAction)(json, JsonOpts)
                    Case "stopInstance"      : Return JsonSerializer.Deserialize(Of StopInstanceAction)(json, JsonOpts)
                    Case "restartInstance"   : Return JsonSerializer.Deserialize(Of RestartInstanceAction)(json, JsonOpts)
                    Case "resumeCrashLoop"   : Return JsonSerializer.Deserialize(Of ResumeCrashLoopAction)(json, JsonOpts)
                    Case "stopAllInstances"  : Return JsonSerializer.Deserialize(Of StopAllInstancesAction)(json, JsonOpts)
                    Case "startAllInstances" : Return JsonSerializer.Deserialize(Of StartAllInstancesAction)(json, JsonOpts)
                    Case "updateInstallation" : Return JsonSerializer.Deserialize(Of UpdateInstallationAction)(json, JsonOpts)
                    Case "sendRconCommand"   : Return JsonSerializer.Deserialize(Of SendRconCommandAction)(json, JsonOpts)
                    Case "notify"            : Return JsonSerializer.Deserialize(Of NotifyAction)(json, JsonOpts)
                    Case "wait"              : Return JsonSerializer.Deserialize(Of WaitAction)(json, JsonOpts)
                    Case "sequence"          : Return JsonSerializer.Deserialize(Of SequenceAction)(json, JsonOpts)
                    Case Else
                        Return Nothing
                End Select
            Catch
                Return Nothing
            End Try
        End Function

    End Class


    ' ============================================================
    '  RULE CONTEXT IMPLEMENTATION
    '  Provided to conditions and actions at execution time.
    '  Wires automation contract calls to real manager services.
    ' ============================================================

    Friend Class RuleContextImpl
        Inherits RuleContext

        Private ReadOnly _instanceManager As InstanceManager
        Private ReadOnly _installationManager As InstallationManager
        Private ReadOnly _notificationService As NotificationService
        Private ReadOnly _logger As ILogger
        Private ReadOnly _progressLog As New List(Of String)()

        Public Sub New(ruleId As String,
                       targetId As String,
                       scope As String,
                       instanceManager As InstanceManager,
                       installationManager As InstallationManager,
                       notificationService As NotificationService,
                       logger As ILogger)
            Me.RuleId = ruleId
            Me.TargetInstanceId = If(scope = "Instance", targetId, "")
            Me.TargetInstallationId = If(scope = "Installation", targetId, "")
            Me.Scope = [Enum].Parse(GetType(RuleScope), scope)
            _instanceManager = instanceManager
            _installationManager = installationManager
            _notificationService = notificationService
            _logger = logger
        End Sub

        Public Overrides Async Function GetInstanceState(
                cancellation As CancellationToken) As Task(Of InstanceState)
            Try
                Dim metrics = Await _instanceManager.GetMetricsAsync(
                    TargetInstanceId, cancellation)
                Return metrics.State
            Catch
                Return InstanceState.Stopped
            End Try
        End Function

        Public Overrides Async Function GetInstanceStateInfo(
                cancellation As CancellationToken) As Task(Of InstanceStateInfo)
            Try
                Dim metrics = Await _instanceManager.GetMetricsAsync(
                    TargetInstanceId, cancellation)
                Return New InstanceStateInfo With {
                    .CurrentState = metrics.State,
                    .StateEnteredAt = DateTime.UtcNow.AddSeconds(
                        -(If(metrics.UptimeSeconds, 0))),
                    .CrashCountInWindow = metrics.CrashCountInWindow
                }
            Catch
                Return New InstanceStateInfo With {
                    .CurrentState = InstanceState.Stopped,
                    .StateEnteredAt = DateTime.UtcNow
                }
            End Try
        End Function

        Public Overrides Async Function GetTotalPlayerCount(
                cancellation As CancellationToken) As Task(Of Integer)

            If Scope = RuleScope.Instance Then
                Return Await _instanceManager.GetPlayerCountAsync(
                    TargetInstanceId, cancellation)
            End If

            ' Installation scope: sum players across all instances.
            Return Await _installationManager.GetTotalPlayerCountAsync(
                TargetInstallationId, cancellation)
        End Function

        Public Overrides Async Function GetPlayerCountForInstance(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of Integer)
            Return Await _instanceManager.GetPlayerCountAsync(instanceId, cancellation)
        End Function

        Public Overrides Async Function IsInstallationLocked(
                cancellation As CancellationToken) As Task(Of Boolean)
            Return Await _installationManager.IsLockedAsync(
                TargetInstallationId, cancellation)
        End Function

        Public Overrides Async Function StartInstance(
                cancellation As CancellationToken) As Task(Of ActionResult)
            LogProgress($"Starting instance {TargetInstanceId}")
            Try
                Await _instanceManager.StartInstanceAsync(TargetInstanceId, cancellation)
                Return ActionResult.Ok("Instance started.")
            Catch ex As Exception
                Dim failMsg = "Start failed: " & ex.Message
                Return ActionResult.Fail(failMsg)
            End Try
        End Function

        Public Overrides Async Function StopInstance(
                graceful As Boolean,
                cancellation As CancellationToken) As Task(Of ActionResult)
            LogProgress($"Stopping instance {TargetInstanceId} (graceful={graceful})")
            Try
                Await _instanceManager.StopInstanceAsync(TargetInstanceId, graceful, cancellation)
                Return ActionResult.Ok("Stop signal sent.")
            Catch ex As Exception
                Dim failMsg = "Stop failed: " & ex.Message
                Return ActionResult.Fail(failMsg)
            End Try
        End Function

        Public Overrides Async Function ResumeCrashRetries(
                cancellation As CancellationToken) As Task(Of ActionResult)
            LogProgress("Resuming crash retries")
            Try
                Await _instanceManager.ResumeCrashRetriesAsync(
                    TargetInstanceId, cancellation)
                Return ActionResult.Ok("Crash retries resumed.")
            Catch ex As Exception
                Dim failMsg = "Resume failed: " & ex.Message
                Return ActionResult.Fail(failMsg)
            End Try
        End Function

        Public Overrides Async Function StopAllInstancesForInstallation(
                graceful As Boolean,
                cancellation As CancellationToken) As Task(Of ActionResult)
            LogProgress($"Stopping all instances for installation {TargetInstallationId}")
            Try
                Await _installationManager.StopAllInstancesAsync(
                    TargetInstallationId, graceful, cancellation)
                Return ActionResult.Ok("All instances stopped.")
            Catch ex As Exception
                Dim failMsg = "StopAll failed: " & ex.Message
                Return ActionResult.Fail(failMsg)
            End Try
        End Function

        Public Overrides Async Function StartAllInstancesForInstallation(
                cancellation As CancellationToken) As Task(Of ActionResult)
            LogProgress($"Starting all instances for installation {TargetInstallationId}")
            Try
                Await _installationManager.StartAllInstancesAsync(
                    TargetInstallationId, cancellation)
                Return ActionResult.Ok("All instances started.")
            Catch ex As Exception
                Dim failMsg = "StartAll failed: " & ex.Message
                Return ActionResult.Fail(failMsg)
            End Try
        End Function

        Public Overrides Async Function UpdateInstallation(
                cancellation As CancellationToken) As Task(Of ActionResult)
            LogProgress($"Updating installation {TargetInstallationId}")
            Try
                Await _installationManager.RunUpdateAsync(
                    TargetInstallationId, cancellation)
                Return ActionResult.Ok("Update completed.")
            Catch ex As Exception
                Dim failMsg = "Update failed: " & ex.Message
                Return ActionResult.Fail(failMsg)
            End Try
        End Function

        Public Overrides Async Function SendRcon(
                command As String,
                cancellation As CancellationToken) As Task(Of ActionResult)
            LogProgress($"RCON: {command}")
            Try
                Dim response = Await _instanceManager.SendRconCommandAsync(
                    TargetInstanceId, command, cancellation)
                If response.Success Then
                    Return ActionResult.Ok($"RCON response: {response.Response}")
                End If
                Return ActionResult.Fail($"RCON failed: {response.ErrorMessage}")
            Catch ex As Exception
                Dim failMsg = "RCON error: " & ex.Message
                Return ActionResult.Fail(failMsg)
            End Try
        End Function

        Public Overrides Async Function SendRconToAllInstances(
                command As String,
                cancellation As CancellationToken) As Task(Of ActionResult)
            LogProgress($"RCON broadcast: {command}")
            Try
                Await _installationManager.SendRconToAllInstancesAsync(
                    TargetInstallationId, command, cancellation)
                Return ActionResult.Ok("RCON broadcast sent.")
            Catch ex As Exception
                Dim failMsg = "RCON broadcast error: " & ex.Message
                Return ActionResult.Fail(failMsg)
            End Try
        End Function

        Public Overrides Async Function SendNotification(
                messageTemplate As String,
                severity As NotificationSeverity,
                targetPluginIds As List(Of String),
                cancellation As CancellationToken) As Task(Of ActionResult)
            LogProgress($"Notification [{severity}]: {messageTemplate}")
            Try
                Dim success = Await _notificationService.SendAsync(
                    messageTemplate, severity,
                    TargetInstanceId, TargetInstallationId,
                    Scope, RuleId, "Automation Rule",
                    targetPluginIds, cancellation)
                Return If(success,
                    ActionResult.Ok("Notification dispatched."),
                    ActionResult.Fail("One or more notification plugins failed. Check logs."))
            Catch ex As Exception
                Dim failMsg = "Notification error: " & ex.Message
                Return ActionResult.Fail(failMsg)
            End Try
        End Function

        Public Overrides Sub LogProgress(message As String)
            _progressLog.Add($"[{DateTime.UtcNow:HH:mm:ss}] {message}")
            _logger.LogDebug("Rule [{RuleId}]: {Msg}", RuleId, message)
        End Sub

    End Class


    ' ============================================================
    '  CRON TIMER
    '  Wraps NCrontab to fire a callback at the scheduled time.
    '  Calculates the next occurrence and sets a one-shot timer
    '  that reschedules itself on each fire.
    ' ============================================================

    Friend Class CronTimer
        Implements IDisposable

        Private ReadOnly _ruleId As String
        Private ReadOnly _schedule As NCrontab.CrontabSchedule
        Private ReadOnly _timeZoneId As String
        Private ReadOnly _callback As Action
        Private _timer As Timer
        Private _disposed As Boolean

        Public Sub New(ruleId As String,
                       schedule As NCrontab.CrontabSchedule,
                       timeZoneId As String,
                       callback As Action)
            _ruleId = ruleId
            _schedule = schedule
            _timeZoneId = timeZoneId
            _callback = callback
            ScheduleNext()
        End Sub

        Private Sub ScheduleNext()
            If _disposed Then Return
            Dim tz = If(String.IsNullOrEmpty(_timeZoneId),
                        TimeZoneInfo.Utc,
                        TimeZoneInfo.FindSystemTimeZoneById(_timeZoneId))
            Dim nowInTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz)
            Dim nextInTz = _schedule.GetNextOccurrence(nowInTz)
            Dim delay = nextInTz - nowInTz
            If delay < TimeSpan.Zero Then delay = TimeSpan.Zero

            If _timer IsNot Nothing Then _timer.Dispose()
            _timer = New Timer(
                Sub(state)
                    If Not _disposed Then
                        _callback()
                        ScheduleNext()
                    End If
                End Sub,
                Nothing,
                CLng(delay.TotalMilliseconds),
                Timeout.Infinite)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _disposed = True
            If _timer IsNot Nothing Then _timer.Dispose()
        End Sub
    End Class


    ' ============================================================
    '  INTERNAL TRACKING TYPES
    ' ============================================================

    Friend Class RuleExecution
        Public Property Task As Task(Of RuleExecutionRecord)
        Public Property Cts As CancellationTokenSource
    End Class

    Friend Class QueuedFire
        Public Property TriggerSource As String
    End Class

    ' Mirrors IAutomationRule.RuleExecutionRecord for the engine's
    ' internal use before it's persisted to DB.
    Public Class RuleExecutionRecord
        Public Property ExecutionId As String
        Public Property RuleId As String
        Public Property ExecutedAt As DateTime
        Public Property TriggerSource As String
        Public Property ConditionResultsJson As String
        Public Property ActionSuccess As Boolean
        Public Property ActionMessage As String
        Public Property ActionDetailsJson As String
        Public Property DurationMs As Long
    End Class

End Namespace
