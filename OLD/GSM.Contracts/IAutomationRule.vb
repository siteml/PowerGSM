Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin

' ============================================================
'  GSM Automation Engine Contract
'
'  Rules are the unit of automation. Each rule binds:
'    one Trigger  → what fires the rule
'    zero or more Conditions → gates that must pass
'    one Action (or Sequence) → what actually happens
'
'  Rules have a Scope that determines what they reason about:
'    Instance     → acts on one specific instance
'    Installation → acts on all instances sharing one install
'    Global       → can reason across any instance or install
'
'  Rules are persisted in the manager's SQLite as JSON and
'  re-hydrated at startup. Plugins may contribute additional
'  trigger/condition/action types via ITriggerProvider,
'  IConditionProvider, IActionProvider - loaded by the same
'  Roslyn PluginRegistry as IGamePlugin.
'
'  The automation engine runs entirely in the manager process.
'  It issues commands to nodes via the existing REST API.
'  Nodes never execute rule logic directly.
' ============================================================

Namespace GSM.Automation

    ' ------------------------------------------------------------
    '  Rule scope
    ' ------------------------------------------------------------

    Public Enum RuleScope
        ' Fires per instance · conditions and actions reference
        ' one specific instance by InstanceId
        Instance

        ' Fires once per installation · conditions and actions
        ' reason about ALL instances sharing that InstallationId
        ' as a group. Required for coordinated update/restart
        ' across a shared install (e.g. Last Oasis multi-instance).
        Installation

        ' Fires once globally · can reference any instance or
        ' installation. Used for cross-instance dependencies
        ' e.g. "restart lobby only when all game servers are empty"
        AllInstances
    End Enum


    ' ------------------------------------------------------------
    '  The rule itself
    ' ------------------------------------------------------------

    Public Class AutomationRule
        Public Property RuleId As String            ' GUID · stable
        Public Property DisplayName As String
        Public Property IsEnabled As Boolean = True
        Public Property Scope As RuleScope

        ' For Instance scope: the instance this rule watches
        ' For Installation scope: the installation this rule watches
        ' For Global scope: empty - rule sees everything
        Public Property TargetId As String

        Public Property Trigger As ITrigger
        Public Property Conditions As List(Of ICondition)
        Public Property ConditionMode As ConditionMode = ConditionMode.All
        Public Property Action As IAction

        ' What to do if this rule fires while a previous execution
        ' of the same rule is still in progress (e.g. a long update)
        Public Property OnConcurrentFire As ConcurrentFireBehaviour =
            ConcurrentFireBehaviour.Skip

        ' Audit trail - every execution result appended here (capped)
        Public Property ExecutionHistory As List(Of RuleExecutionRecord)
    End Class

    Public Enum ConditionMode
        All     ' All conditions must pass (AND)
        Any     ' At least one condition must pass (OR)
    End Enum

    Public Enum ConcurrentFireBehaviour
        Skip    ' Ignore the new fire · log it
        Queue   ' Hold it · execute when current run finishes
        Cancel  ' Cancel the running execution · start fresh
    End Enum

    Public Class RuleExecutionRecord
        Public Property ExecutedAt As DateTime
        Public Property TriggerSource As String     ' Human label of what fired it
        Public Property ConditionResults As List(Of String)  ' Per-condition pass/fail + reason
        Public Property ActionResult As ActionResult
        Public Property DurationMs As Long
        Public Property Notes As String             ' Any decision explanations
    End Class


    ' ============================================================
    '  TRIGGERS
    '  A trigger watches for a condition in the world and fires
    '  the rule when it occurs. Triggers are passive - they do
    '  not gate execution, they merely initiate it.
    ' ============================================================

    Public Interface ITrigger
        ReadOnly Property TriggerId As String
        ReadOnly Property DisplayLabel As String    ' Shown in UI rule summary
    End Interface

    ' -- Time-based --

    ' Standard cron expression (5 or 6 field, via NCrontab).
    ' e.g. "0 3 * * *" = 3am daily
    '      "0 */6 * * *" = every 6 hours
    Public Class CronTrigger
        Implements ITrigger
        Public ReadOnly Property TriggerId As String = "cron" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String Implements ITrigger.DisplayLabel
            Get
                Return $"Schedule: {CronExpression}"
            End Get
        End Property
        Public Property CronExpression As String
        ' IANA timezone name e.g. "America/New_York"
        ' Empty = UTC
        Public Property TimeZoneId As String = ""
    End Class

    ' -- Instance state --

    ' Fires when an instance transitions to any of the watched states.
    ' Most commonly used to react to Crashed, CrashLoopHalted, Running.
    Public Class InstanceStateChangedTrigger
        Implements ITrigger
        Public ReadOnly Property TriggerId As String = "instanceStateChanged" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String Implements ITrigger.DisplayLabel
            Get
                Return $"State changes to: {String.Join(", ", WatchedStates)}"
            End Get
        End Property
        ' Empty = fire on any state change
        Public Property WatchedStates As List(Of InstanceState)
    End Class

    ' Fires when the crash restart policy enters CrashLoopHalted.
    ' Shorthand for InstanceStateChangedTrigger({CrashLoopHalted}).
    ' Provided separately for clarity in rule lists.
    Public Class CrashLoopHaltedTrigger
        Implements ITrigger
        Public ReadOnly Property TriggerId As String = "crashLoopHalted" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String = "Crash loop halted" Implements ITrigger.DisplayLabel
    End Class

    ' -- Update detection --

    ' Fires when GetLatestVersion() != GetCurrentVersion() for an
    ' installation. The engine polls on a configurable interval.
    ' Scoped to Installation or Global only - not meaningful per-instance.
    Public Class UpdateAvailableTrigger
        Implements ITrigger
        Public ReadOnly Property TriggerId As String = "updateAvailable" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String = "Update available" Implements ITrigger.DisplayLabel
        ' How often to poll for a new version (minutes)
        Public Property PollIntervalMinutes As Integer = 15
    End Class

    ' -- Player activity --

    ' Fires when player count crosses a threshold.
    ' e.g. drops to 0 (server empty), rises above 0 (first player joins)
    Public Class PlayerCountThresholdTrigger
        Implements ITrigger
        Public ReadOnly Property TriggerId As String = "playerCountThreshold" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String Implements ITrigger.DisplayLabel
            Get
                Return $"Player count {CrossingDirection} {Threshold}"
            End Get
        End Property
        Public Property Threshold As Integer = 0
        Public Property CrossingDirection As ThresholdCrossing = ThresholdCrossing.FallsTo
    End Class

    Public Enum ThresholdCrossing
        FallsTo     ' Count reaches or drops below threshold
        RisesTo     ' Count reaches or exceeds threshold
    End Enum

    ' -- Manual / remote --

    ' Fired explicitly by a user action: UI button, Discord command,
    ' REST API call to POST /automation/rules/{id}/fire
    Public Class ManualTrigger
        Implements ITrigger
        Public ReadOnly Property TriggerId As String = "manual" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String = "Manual / on demand" Implements ITrigger.DisplayLabel
    End Class

    ' -- Log event --

    ' Fires when the log parser emits a named event.
    ' Games can emit named events from ILogParser beyond player tracking
    ' e.g. "WorldSaved", "BackupComplete", "AdminCommand"
    Public Class LogEventTrigger
        Implements ITrigger
        Public ReadOnly Property TriggerId As String = "logEvent" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String Implements ITrigger.DisplayLabel
            Get
                Return $"Log event: {EventName}"
            End Get
        End Property
        Public Property EventName As String
    End Class


    ' ============================================================
    '  CONDITIONS
    '  Conditions gate execution after a trigger fires.
    '  All must return True (in All mode) for the action to proceed.
    '  Conditions are evaluated in order - first failure short-circuits.
    ' ============================================================

    Public Interface ICondition
        ReadOnly Property ConditionId As String
        ReadOnly Property DisplayLabel As String

        ' Returns True if the condition passes.
        ' ctx provides access to current instance/installation state.
        ' Must not perform long-running operations - use actions for that.
        Function Evaluate(ctx As RuleContext,
                          cancellation As CancellationToken) As Task(Of ConditionResult)
    End Interface

    Public Class ConditionResult
        Public Property Passed As Boolean
        ' Always populated - explains the decision even when True.
        ' e.g. "Player count is 0 (threshold: 0)" or
        '      "Player count is 3 - condition not met (threshold: 0)"
        Public Property Reason As String

        Public Shared Function Pass(reason As String) As ConditionResult
            Return New ConditionResult With {.Passed = True, .Reason = reason}
        End Function

        Public Shared Function Fail(reason As String) As ConditionResult
            Return New ConditionResult With {.Passed = False, .Reason = reason}
        End Function
    End Class

    ' -- Instance state conditions --

    Public Class InstanceStateIsCondition
        Implements ICondition
        Public ReadOnly Property ConditionId As String = "instanceStateIs" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String Implements ICondition.DisplayLabel
            Get
                Return $"Instance state is {String.Join(" or ", States)}"
            End Get
        End Property
        Public Property States As List(Of InstanceState)
        Public Async Function Evaluate(ctx As RuleContext,
                                       cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            Dim current = Await ctx.GetInstanceState(cancellation)
            If States.Contains(current) Then
                Return ConditionResult.Pass($"Instance state is {current}")
            End If
            Return ConditionResult.Fail($"Instance state is {current}, expected one of: {String.Join(", ", States)}")
        End Function
    End Class

    ' -- Player count conditions --

    ' Passes immediately if player count is already at or below threshold.
    ' Does NOT wait - use WaitForPlayerCountCondition for blocking behaviour.
    Public Class PlayerCountCondition
        Implements ICondition
        Public ReadOnly Property ConditionId As String = "playerCount" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String Implements ICondition.DisplayLabel
            Get
                Return $"Player count {Comparison} {Threshold}"
            End Get
        End Property
        Public Property Threshold As Integer = 0
        Public Property Comparison As CountComparison = CountComparison.LessOrEqual

        Public Async Function Evaluate(ctx As RuleContext,
                                       cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            Dim count = Await ctx.GetTotalPlayerCount(cancellation)
            Dim passes = Comparison = CountComparison.LessOrEqual AndAlso count <= Threshold OrElse
                         Comparison = CountComparison.GreaterOrEqual AndAlso count >= Threshold OrElse
                         Comparison = CountComparison.Exactly AndAlso count = Threshold
            If passes Then
                Return ConditionResult.Pass($"Player count is {count}")
            End If
            Return ConditionResult.Fail($"Player count is {count}, condition requires {Comparison} {Threshold}")
        End Function
    End Class

    Public Enum CountComparison
        LessOrEqual
        GreaterOrEqual
        Exactly
    End Enum

    ' Blocks execution until player count reaches threshold OR timeout elapses.
    ' This IS a long-running condition - it polls until satisfied or timed out.
    ' OnTimeout controls whether to pass anyway (force) or fail (abort).
    Public Class WaitForPlayerCountCondition
        Implements ICondition
        Public ReadOnly Property ConditionId As String = "waitForPlayerCount" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String Implements ICondition.DisplayLabel
            Get
                Return $"Wait for player count {Comparison} {Threshold} (timeout {TimeoutMinutes}min, {OnTimeout})"
            End Get
        End Property
        Public Property Threshold As Integer = 0
        Public Property Comparison As CountComparison = CountComparison.LessOrEqual
        Public Property TimeoutMinutes As Integer = 60
        Public Property PollIntervalSeconds As Integer = 30
        Public Property OnTimeout As TimeoutBehaviour = TimeoutBehaviour.Force

        Public Async Function Evaluate(ctx As RuleContext,
                                       cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            Dim deadline = DateTime.UtcNow.AddMinutes(TimeoutMinutes)
            Do
                cancellation.ThrowIfCancellationRequested()
                Dim count = Await ctx.GetTotalPlayerCount(cancellation)
                Dim met = (Comparison = CountComparison.LessOrEqual AndAlso count <= Threshold) OrElse
                          (Comparison = CountComparison.GreaterOrEqual AndAlso count >= Threshold) OrElse
                          (Comparison = CountComparison.Exactly AndAlso count = Threshold)
                If met Then Return ConditionResult.Pass($"Player count reached {count}")
                If DateTime.UtcNow >= deadline Then
                    If OnTimeout = TimeoutBehaviour.Force Then
                        Return ConditionResult.Pass($"Timeout elapsed · forcing (player count was {count})")
                    Else
                        Return ConditionResult.Fail($"Timeout elapsed · aborting (player count was {count})")
                    End If
                End If
                Await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), cancellation)
            Loop
        End Function
    End Class

    Public Enum TimeoutBehaviour
        Force   ' Condition passes anyway when timeout elapses
        Abort   ' Condition fails · action does not proceed
    End Enum

    ' -- Time-in-state condition --

    ' Passes only if the instance has been in the given state
    ' for at least MinDurationMinutes. Useful for "only restart
    ' if it's been running for more than 1 hour" style rules.
    Public Class TimeInStateCondition
        Implements ICondition
        Public ReadOnly Property ConditionId As String = "timeInState" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String Implements ICondition.DisplayLabel
            Get
                Return $"Has been {TargetState} for at least {MinDurationMinutes} min"
            End Get
        End Property
        Public Property TargetState As InstanceState = InstanceState.Running
        Public Property MinDurationMinutes As Integer = 60

        Public Async Function Evaluate(ctx As RuleContext,
                                       cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            Dim info = Await ctx.GetInstanceStateInfo(cancellation)
            If info.CurrentState <> TargetState Then
                Return ConditionResult.Fail($"Instance is {info.CurrentState}, not {TargetState}")
            End If
            Dim elapsed = DateTime.UtcNow - info.StateEnteredAt
            If elapsed.TotalMinutes >= MinDurationMinutes Then
                Return ConditionResult.Pass($"Has been {TargetState} for {elapsed.TotalMinutes:F0} min")
            End If
            Return ConditionResult.Fail($"Only been {TargetState} for {elapsed.TotalMinutes:F0} min, need {MinDurationMinutes}")
        End Function
    End Class

    ' -- Installation lock condition --

    ' Passes only if no write lock is currently held on the installation.
    ' Prevents starting instances into a mid-update installation.
    Public Class InstallationNotLockedCondition
        Implements ICondition
        Public ReadOnly Property ConditionId As String = "installationNotLocked" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String = "Installation is not locked" Implements ICondition.DisplayLabel

        Public Async Function Evaluate(ctx As RuleContext,
                                       cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            Dim locked = Await ctx.IsInstallationLocked(cancellation)
            If locked Then
                Return ConditionResult.Fail("Installation write lock is held (update in progress)")
            End If
            Return ConditionResult.Pass("Installation is not locked")
        End Function
    End Class

    ' -- Logical combinators --

    ' Passes if ALL inner conditions pass (AND grouping within a rule)
    Public Class AllCondition
        Implements ICondition
        Public ReadOnly Property ConditionId As String = "all" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String = "All of:" Implements ICondition.DisplayLabel
        Public Property Inner As List(Of ICondition)
        Public Async Function Evaluate(ctx As RuleContext,
                                       cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            For Each c In Inner
                Dim r = Await c.Evaluate(ctx, cancellation)
                If Not r.Passed Then Return ConditionResult.Fail($"[{c.DisplayLabel}] {r.Reason}")
            Next
            Return ConditionResult.Pass("All conditions passed")
        End Function
    End Class

    ' Passes if ANY inner condition passes (OR grouping)
    Public Class AnyCondition
        Implements ICondition
        Public ReadOnly Property ConditionId As String = "any" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String = "Any of:" Implements ICondition.DisplayLabel
        Public Property Inner As List(Of ICondition)
        Public Async Function Evaluate(ctx As RuleContext,
                                       cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            Dim reasons As New List(Of String)
            For Each c In Inner
                Dim r = Await c.Evaluate(ctx, cancellation)
                If r.Passed Then Return ConditionResult.Pass($"[{c.DisplayLabel}] {r.Reason}")
                reasons.Add($"[{c.DisplayLabel}] {r.Reason}")
            Next
            Return ConditionResult.Fail($"No conditions passed: {String.Join("; ", reasons)}")
        End Function
    End Class

    ' Inverts a condition
    Public Class NotCondition
        Implements ICondition
        Public ReadOnly Property ConditionId As String = "not" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String Implements ICondition.DisplayLabel
            Get
                Return $"Not: {Inner.DisplayLabel}"
            End Get
        End Property
        Public Property Inner As ICondition
        Public Async Function Evaluate(ctx As RuleContext,
                                       cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            Dim r = Await Inner.Evaluate(ctx, cancellation)
            If r.Passed Then
                Return ConditionResult.Fail($"NOT: inner passed when it should not ({r.Reason})")
            End If
            Return ConditionResult.Pass($"NOT: inner correctly did not pass ({r.Reason})")
        End Function
    End Class


    ' ============================================================
    '  ACTIONS
    '  Actions are what the engine actually does when a rule fires
    '  and all conditions pass. Actions may be long-running.
    '  Every action must log its progress and outcome.
    ' ============================================================

    Public Interface IAction
        ReadOnly Property ActionId As String
        ReadOnly Property DisplayLabel As String

        ' Execute the action. ctx provides manager API access.
        ' Must respect cancellation (rule abort, shutdown, etc).
        ' Must never throw unhandled exceptions - catch and return Failure.
        Function Execute(ctx As RuleContext,
                         cancellation As CancellationToken) As Task(Of ActionResult)
    End Interface

    Public Class ActionResult
        Public Property Success As Boolean
        Public Property Message As String           ' Always populated
        Public Property Details As List(Of String)  ' Step-by-step log

        Public Shared Function Ok(message As String,
                                  ParamArray details As String()) As ActionResult
            Return New ActionResult With {
                .Success = True,
                .Message = message,
                .Details = New List(Of String)(details)
            }
        End Function

        Public Shared Function Fail(message As String,
                                    ParamArray details As String()) As ActionResult
            Return New ActionResult With {
                .Success = False,
                .Message = message,
                .Details = New List(Of String)(details)
            }
        End Function
    End Class

    ' -- Instance lifecycle actions --

    Public Class StartInstanceAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "startInstance" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Start instance" Implements IAction.DisplayLabel
        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Return Await ctx.StartInstance(cancellation)
        End Function
    End Class

    Public Class StopInstanceAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "stopInstance" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Stop instance" Implements IAction.DisplayLabel
        Public Property Graceful As Boolean = True
        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Return Await ctx.StopInstance(Graceful, cancellation)
        End Function
    End Class

    Public Class RestartInstanceAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "restartInstance" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Restart instance" Implements IAction.DisplayLabel
        Public Property Graceful As Boolean = True
        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Dim stopResult = Await ctx.StopInstance(Graceful, cancellation)
            If Not stopResult.Success Then Return stopResult
            Return Await ctx.StartInstance(cancellation)
        End Function
    End Class

    ' Resume retries on a halted instance.
    ' Resets crash count in window and re-enters Restarting state.
    Public Class ResumeCrashLoopAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "resumeCrashLoop" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Resume crash loop retries" Implements IAction.DisplayLabel
        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Return Await ctx.ResumeCrashRetries(cancellation)
        End Function
    End Class

    ' -- Installation-scoped actions --

    ' Stop ALL instances sharing the target installation.
    ' Waits for each to exit before proceeding.
    ' Used as a prerequisite step before UpdateInstallationAction.
    Public Class StopAllInstancesAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "stopAllInstances" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Stop all instances (installation)" Implements IAction.DisplayLabel
        Public Property Graceful As Boolean = True
        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Return Await ctx.StopAllInstancesForInstallation(Graceful, cancellation)
        End Function
    End Class

    ' Restart ALL instances sharing the target installation.
    ' Typically called after UpdateInstallationAction completes.
    Public Class StartAllInstancesAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "startAllInstances" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Start all instances (installation)" Implements IAction.DisplayLabel
        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Return Await ctx.StartAllInstancesForInstallation(cancellation)
        End Function
    End Class

    ' Acquire write lock on installation, run update steps, release lock.
    ' Returns Failure immediately if write lock cannot be acquired.
    ' Combine with StopAllInstancesAction and WaitForPlayerCountCondition
    ' to build a full coordinated update sequence.
    Public Class UpdateInstallationAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "updateInstallation" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Update installation files" Implements IAction.DisplayLabel
        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Return Await ctx.UpdateInstallation(cancellation)
        End Function
    End Class

    ' -- Communication actions --

    ' Send a command via RCON to one or all instances.
    ' Commonly used to warn players before a restart or update.
    Public Class SendRconCommandAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "sendRconCommand" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String Implements IAction.DisplayLabel
            Get
                Return $"RCON: {Command}"
            End Get
        End Property
        Public Property Command As String
        ' True = send to all instances sharing the installation
        ' False = send to the specific instance in scope
        Public Property BroadcastToAllInstances As Boolean = False
        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            If BroadcastToAllInstances Then
                Return Await ctx.SendRconToAllInstances(Command, cancellation)
            End If
            Return Await ctx.SendRcon(Command, cancellation)
        End Function
    End Class

    ' Fire a notification via all registered INotificationPlugins.
    ' Message supports tokens: {InstanceName}, {State}, {PlayerCount},
    ' {CrashCount}, {ExitCode}, {Reason}
    Public Class NotifyAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "notify" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String Implements IAction.DisplayLabel
            Get
                Return $"Notify: {MessageTemplate}"
            End Get
        End Property
        Public Property MessageTemplate As String
        Public Property Severity As NotificationSeverity = NotificationSeverity.Info
        ' Specific plugin IDs to target · empty = all registered plugins
        Public Property TargetPluginIds As List(Of String)
        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Return Await ctx.SendNotification(MessageTemplate, Severity, TargetPluginIds, cancellation)
        End Function
    End Class

    Public Enum NotificationSeverity
        Info
        Warning
        Critical
    End Enum

    ' -- Timing actions --

    ' Pause execution for a fixed duration.
    ' Used in sequences to space out warnings before a restart.
    Public Class WaitAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "wait" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String Implements IAction.DisplayLabel
            Get
                Return $"Wait {DurationSeconds}s"
            End Get
        End Property
        Public Property DurationSeconds As Integer
        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Await Task.Delay(TimeSpan.FromSeconds(DurationSeconds), cancellation)
            Return ActionResult.Ok($"Waited {DurationSeconds}s")
        End Function
    End Class

    ' -- Sequence --

    ' Executes a list of actions in order. Stops on first failure
    ' unless ContinueOnFailure is True. This is how complex workflows
    ' like "warn → wait → warn → wait → update → restart" are built.
    '
    ' Example: coordinated Last Oasis multi-instance update
    '   1. NotifyAction          "Server updating in 15 min"
    '   2. WaitAction            900s
    '   3. NotifyAction          "Server updating in 5 min"
    '   4. WaitAction            240s
    '   5. SendRconCommandAction "Server updating in 1 min" (all instances)
    '   6. WaitAction            60s
    '   7. WaitForPlayerCount    (inline condition re-evaluated as action)
    '   8. StopAllInstancesAction
    '   9. UpdateInstallationAction
    '  10. StartAllInstancesAction
    '  11. NotifyAction          "Servers back online"
    Public Class SequenceAction
        Implements IAction
        Public ReadOnly Property ActionId As String = "sequence" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Sequence" Implements IAction.DisplayLabel
        Public Property Steps As List(Of IAction)
        Public Property ContinueOnFailure As Boolean = False

        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Dim details As New List(Of String)
            For i = 0 To Steps.Count - 1
                Dim stepAction = Steps(i)
                ctx.LogProgress($"Step {i + 1}/{Steps.Count}: {stepAction.DisplayLabel}")
                Dim result = Await stepAction.Execute(ctx, cancellation)
                details.Add($"[{If(result.Success, "OK", "FAIL")}] {stepAction.DisplayLabel}: {result.Message}")
                If Not result.Success AndAlso Not ContinueOnFailure Then
                    Return ActionResult.Fail($"Sequence aborted at step {i + 1}: {result.Message}",
                                            details.ToArray())
                End If
            Next
            Return ActionResult.Ok("Sequence completed", details.ToArray())
        End Function
    End Class


    ' ============================================================
    '  RULE CONTEXT
    '  Provided to conditions and actions at execution time.
    '  Abstracts manager internals - conditions and actions never
    '  talk to nodes or the database directly.
    '  Implemented by the automation engine in Core.
    ' ============================================================

    Public Interface IRuleContext
        ' -- State queries --
        Function GetInstanceState(cancellation As CancellationToken) As Task(Of InstanceState)
        Function GetInstanceStateInfo(cancellation As CancellationToken) As Task(Of InstanceStateInfo)
        Function GetTotalPlayerCount(cancellation As CancellationToken) As Task(Of Integer)
        Function GetPlayerCountForInstance(instanceId As String,
                                           cancellation As CancellationToken) As Task(Of Integer)
        Function IsInstallationLocked(cancellation As CancellationToken) As Task(Of Boolean)

        ' -- Instance lifecycle --
        Function StartInstance(cancellation As CancellationToken) As Task(Of ActionResult)
        Function StopInstance(graceful As Boolean,
                              cancellation As CancellationToken) As Task(Of ActionResult)
        Function ResumeCrashRetries(cancellation As CancellationToken) As Task(Of ActionResult)

        ' -- Installation-scoped operations --
        Function StopAllInstancesForInstallation(graceful As Boolean,
                                                  cancellation As CancellationToken) As Task(Of ActionResult)
        Function StartAllInstancesForInstallation(cancellation As CancellationToken) As Task(Of ActionResult)
        Function UpdateInstallation(cancellation As CancellationToken) As Task(Of ActionResult)

        ' -- RCON --
        Function SendRcon(command As String,
                          cancellation As CancellationToken) As Task(Of ActionResult)
        Function SendRconToAllInstances(command As String,
                                        cancellation As CancellationToken) As Task(Of ActionResult)

        ' -- Notifications --
        Function SendNotification(messageTemplate As String,
                                   severity As NotificationSeverity,
                                   targetPluginIds As List(Of String),
                                   cancellation As CancellationToken) As Task(Of ActionResult)

        ' -- Progress logging (streamed to UI and persisted) --
        Sub LogProgress(message As String)
    End Interface

    ' Abstract base class passed to conditions/actions at execution time.
    ' Concrete implementation is RuleContextImpl in GSM.Core.
    ' Plugin authors use this type in their conditions/actions.
    Public MustInherit Class RuleContext
        Implements IRuleContext

        ' Identity properties set by the engine before execution.
        Public Property RuleId As String
        Public Property TargetInstanceId As String
        Public Property TargetInstallationId As String
        Public Property Scope As RuleScope

        ' -- Abstract members - implemented by RuleContextImpl in Core --
        Public MustOverride Function GetInstanceState(
            cancellation As CancellationToken) As Task(Of InstanceState) _
            Implements IRuleContext.GetInstanceState

        Public MustOverride Function GetInstanceStateInfo(
            cancellation As CancellationToken) As Task(Of InstanceStateInfo) _
            Implements IRuleContext.GetInstanceStateInfo

        Public MustOverride Function GetTotalPlayerCount(
            cancellation As CancellationToken) As Task(Of Integer) _
            Implements IRuleContext.GetTotalPlayerCount

        Public MustOverride Function GetPlayerCountForInstance(
            instanceId As String,
            cancellation As CancellationToken) As Task(Of Integer) _
            Implements IRuleContext.GetPlayerCountForInstance

        Public MustOverride Function IsInstallationLocked(
            cancellation As CancellationToken) As Task(Of Boolean) _
            Implements IRuleContext.IsInstallationLocked

        Public MustOverride Function StartInstance(
            cancellation As CancellationToken) As Task(Of ActionResult) _
            Implements IRuleContext.StartInstance

        Public MustOverride Function StopInstance(
            graceful As Boolean,
            cancellation As CancellationToken) As Task(Of ActionResult) _
            Implements IRuleContext.StopInstance

        Public MustOverride Function ResumeCrashRetries(
            cancellation As CancellationToken) As Task(Of ActionResult) _
            Implements IRuleContext.ResumeCrashRetries

        Public MustOverride Function StopAllInstancesForInstallation(
            graceful As Boolean,
            cancellation As CancellationToken) As Task(Of ActionResult) _
            Implements IRuleContext.StopAllInstancesForInstallation

        Public MustOverride Function StartAllInstancesForInstallation(
            cancellation As CancellationToken) As Task(Of ActionResult) _
            Implements IRuleContext.StartAllInstancesForInstallation

        Public MustOverride Function UpdateInstallation(
            cancellation As CancellationToken) As Task(Of ActionResult) _
            Implements IRuleContext.UpdateInstallation

        Public MustOverride Function SendRcon(
            command As String,
            cancellation As CancellationToken) As Task(Of ActionResult) _
            Implements IRuleContext.SendRcon

        Public MustOverride Function SendRconToAllInstances(
            command As String,
            cancellation As CancellationToken) As Task(Of ActionResult) _
            Implements IRuleContext.SendRconToAllInstances

        Public MustOverride Function SendNotification(
            messageTemplate As String,
            severity As NotificationSeverity,
            targetPluginIds As List(Of String),
            cancellation As CancellationToken) As Task(Of ActionResult) _
            Implements IRuleContext.SendNotification

        Public MustOverride Sub LogProgress(message As String) _
            Implements IRuleContext.LogProgress

    End Class

    Public Class InstanceStateInfo
        Public Property CurrentState As InstanceState
        Public Property StateEnteredAt As DateTime
        Public Property PreviousState As InstanceState
        Public Property CrashCountInWindow As Integer
        Public Property LastExitCode As Integer?
    End Class


    ' ============================================================
    '  PLUGIN EXTENSION POINTS
    '  Plugins may contribute custom triggers, conditions, and
    '  actions by implementing these provider interfaces.
    '  Loaded by the same Roslyn PluginRegistry as IGamePlugin.
    ' ============================================================

    Public Interface ITriggerProvider
        ReadOnly Property ProviderId As String
        Function GetTriggers() As IReadOnlyList(Of ITrigger)
    End Interface

    Public Interface IConditionProvider
        ReadOnly Property ProviderId As String
        Function GetConditions() As IReadOnlyList(Of ICondition)
    End Interface

    Public Interface IActionProvider
        ReadOnly Property ProviderId As String
        Function GetActions() As IReadOnlyList(Of IAction)
    End Interface

End Namespace