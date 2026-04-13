Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin

' ============================================================
'  GSM Automation Engine Contract
'
'  Each rule binds:
'    one Trigger  -> what fires the rule
'    zero or more Conditions -> gates that must pass
'    one Action (or SequenceAction) -> what actually happens
'
'  Rules have a Scope that determines what they reason about:
'    Instance       -> acts on one specific instance
'    Installation   -> acts on all instances sharing one install
'    AllInstances   -> can reason across any instance or install
'
'  Rules are persisted in the manager's SQLite as JSON and
'  re-hydrated at startup. Plugins may contribute additional
'  trigger/condition/action types via the provider interfaces.
'
'  The automation engine runs entirely in the manager process.
'  It issues commands to nodes via the existing REST API.
'  Nodes never execute rule logic directly.
' ============================================================

Namespace GSM.Automation

    ' ============================================================
    '  Enums
    ' ============================================================

    ''' <summary>
    ''' Scope of an automation rule. AllInstances replaces the
    ''' reserved keyword "Global".
    ''' </summary>
    Public Enum RuleScope
        ''' <summary>
        ''' Fires per instance. Conditions and actions reference
        ''' one specific instance by InstanceId.
        ''' </summary>
        Instance

        ''' <summary>
        ''' Fires once per installation. Conditions and actions
        ''' reason about ALL instances sharing that InstallationId.
        ''' Required for coordinated update/restart across a
        ''' shared install (e.g. Last Oasis multi-instance).
        ''' </summary>
        Installation

        ''' <summary>
        ''' Fires once globally. Can reference any instance or
        ''' installation. Used for cross-instance dependencies.
        ''' </summary>
        AllInstances
    End Enum

    ''' <summary>
    ''' What triggered the rule evaluation.
    ''' </summary>
    Public Enum TriggerKind
        Schedule
        StateChange
        VersionMismatch
        Manual
        PlayerCountChange
    End Enum

    ''' <summary>
    ''' How multiple conditions are combined.
    ''' </summary>
    Public Enum ConditionMode
        All
        Any
    End Enum

    ''' <summary>
    ''' What to do if a rule fires while a previous execution
    ''' of the same rule is still in progress.
    ''' </summary>
    Public Enum OverlapPolicy
        ''' <summary>Skip this firing — previous execution wins.</summary>
        SkipIfRunning
        ''' <summary>Queue and execute after the current run finishes.</summary>
        QueueNext
        ''' <summary>Cancel the running execution and start fresh.</summary>
        CancelAndRestart
    End Enum

    ''' <summary>
    ''' Severity level for notifications sent by automation actions.
    ''' </summary>
    Public Enum NotificationSeverity
        Info
        Warning
        ErrorLevel
        Critical
    End Enum

    ' ============================================================
    '  Rule definition (persisted as JSON in SQLite)
    ' ============================================================

    ''' <summary>
    ''' A complete automation rule. Serialised to/from JSON for
    ''' database persistence.
    ''' </summary>
    Public Class AutomationRule
        Public Property RuleId As String
        Public Property DisplayName As String
        Public Property IsEnabled As Boolean = True
        Public Property Scope As RuleScope
        Public Property TargetId As String
        Public Property Trigger As ITrigger
        Public Property Conditions As List(Of ICondition)
        Public Property ConditionMode As ConditionMode
        Public Property Action As IAction
        Public Property Overlap As OverlapPolicy
    End Class

    ' ============================================================
    '  Trigger interface
    ' ============================================================

    Public Interface ITrigger
        ReadOnly Property TriggerId As String
        ReadOnly Property DisplayLabel As String
        ReadOnly Property Kind As TriggerKind
    End Interface

    ' ============================================================
    '  Condition interface + result
    ' ============================================================

    Public Interface ICondition
        ReadOnly Property ConditionId As String
        ReadOnly Property DisplayLabel As String

        ''' <summary>
        ''' Evaluate this condition. MUST always return a reason.
        ''' </summary>
        Function Evaluate(ctx As RuleContext,
                          cancellation As CancellationToken) As Task(Of ConditionResult)
    End Interface

    ''' <summary>
    ''' Result of a condition evaluation. Reason is never optional.
    ''' </summary>
    Public Class ConditionResult
        Public Property Passed As Boolean
        Public Property Reason As String

        Public Shared Function Pass(reason As String) As ConditionResult
            Return New ConditionResult With {
                .Passed = True,
                .Reason = reason
            }
        End Function

        Public Shared Function Fail(reason As String) As ConditionResult
            Return New ConditionResult With {
                .Passed = False,
                .Reason = reason
            }
        End Function
    End Class

    ' ============================================================
    '  Action interface + result
    ' ============================================================

    Public Interface IAction
        ReadOnly Property ActionId As String
        ReadOnly Property DisplayLabel As String
        Function Execute(ctx As RuleContext,
                         cancellation As CancellationToken) As Task(Of ActionResult)
    End Interface

    ''' <summary>
    ''' Result of an action execution.
    ''' </summary>
    Public Class ActionResult
        Public Property Success As Boolean
        Public Property Message As String
        Public Property Details As String()

        Public Shared Function Ok(message As String,
                                  Optional details As String() = Nothing) As ActionResult
            Return New ActionResult With {
                .Success = True,
                .Message = message,
                .Details = If(details, Array.Empty(Of String)())
            }
        End Function

        Public Shared Function Fail(message As String,
                                    Optional details As String() = Nothing) As ActionResult
            Return New ActionResult With {
                .Success = False,
                .Message = message,
                .Details = If(details, Array.Empty(Of String)())
            }
        End Function
    End Class

    ' ============================================================
    '  Rule execution record (for audit trail)
    ' ============================================================

    Public Class RuleExecutionRecord
        Public Property ExecutionId As String
        Public Property RuleId As String
        Public Property StartedAt As DateTime
        Public Property CompletedAt As DateTime?
        Public Property TriggerReason As String
        Public Property ConditionResults As List(Of ConditionEvaluation)
        Public Property ActionResult As ActionResult
        Public Property WasSkipped As Boolean
        Public Property SkipReason As String
    End Class

    Public Class ConditionEvaluation
        Public Property ConditionId As String
        Public Property Passed As Boolean
        Public Property Reason As String
    End Class

    ' ============================================================
    '  IRuleContext — abstracts manager internals for actions
    ' ============================================================

    ''' <summary>
    ''' Provided to conditions and actions at execution time.
    ''' Abstracts manager internals so conditions and actions
    ''' never talk to nodes or the database directly.
    ''' Implemented by the automation engine in Core.
    ''' </summary>
    Public Interface IRuleContext
        ReadOnly Property RuleId As String
        ReadOnly Property TargetInstanceId As String
        ReadOnly Property TargetInstallationId As String
        ReadOnly Property Scope As RuleScope

        ' ---- Instance operations ----
        Function GetInstanceState(instanceId As String) As Task(Of InstanceStateInfo)
        Function GetPlayerCount(instanceId As String) As Task(Of Integer)
        Function StartInstance(instanceId As String) As Task(Of Boolean)
        Function StopInstance(instanceId As String, Optional gracefulTimeoutMs As Integer = 10000) As Task(Of Boolean)
        Function SendRconCommand(instanceId As String, command As String) As Task(Of String)

        ' ---- Installation operations ----
        Function GetInstanceIdsForInstallation(installationId As String) As Task(Of IReadOnlyList(Of String))
        Function UpdateInstallation(installationId As String) As Task(Of Boolean)

        ' ---- Notification ----
        Function SendNotification(pluginId As String, message As String,
                                  severity As NotificationSeverity) As Task

        ' ---- Logging ----
        Sub LogProgress(message As String)
    End Interface

    ''' <summary>
    ''' Abstract base class for IRuleContext. Implemented by
    ''' RuleContextImpl in GSM.Manager.Core.
    ''' All IRuleContext members are MustOverride stubs here.
    ''' </summary>
    Public MustInherit Class RuleContext
        Implements IRuleContext

        Public MustOverride ReadOnly Property RuleId As String Implements IRuleContext.RuleId
        Public MustOverride ReadOnly Property TargetInstanceId As String Implements IRuleContext.TargetInstanceId
        Public MustOverride ReadOnly Property TargetInstallationId As String Implements IRuleContext.TargetInstallationId
        Public MustOverride ReadOnly Property Scope As RuleScope Implements IRuleContext.Scope

        Public MustOverride Function GetInstanceState(instanceId As String) As Task(Of InstanceStateInfo) Implements IRuleContext.GetInstanceState
        Public MustOverride Function GetPlayerCount(instanceId As String) As Task(Of Integer) Implements IRuleContext.GetPlayerCount
        Public MustOverride Function StartInstance(instanceId As String) As Task(Of Boolean) Implements IRuleContext.StartInstance
        Public MustOverride Function StopInstance(instanceId As String, Optional gracefulTimeoutMs As Integer = 10000) As Task(Of Boolean) Implements IRuleContext.StopInstance
        Public MustOverride Function SendRconCommand(instanceId As String, command As String) As Task(Of String) Implements IRuleContext.SendRconCommand
        Public MustOverride Function GetInstanceIdsForInstallation(installationId As String) As Task(Of IReadOnlyList(Of String)) Implements IRuleContext.GetInstanceIdsForInstallation
        Public MustOverride Function UpdateInstallation(installationId As String) As Task(Of Boolean) Implements IRuleContext.UpdateInstallation
        Public MustOverride Function SendNotification(pluginId As String, message As String, severity As NotificationSeverity) As Task Implements IRuleContext.SendNotification
        Public MustOverride Sub LogProgress(message As String) Implements IRuleContext.LogProgress
    End Class

    ''' <summary>
    ''' Snapshot of an instance's state for condition evaluation.
    ''' </summary>
    Public Class InstanceStateInfo
        Public Property CurrentState As InstanceState
        Public Property StateEnteredAt As DateTime
        Public Property PreviousState As InstanceState
        Public Property CrashCountInWindow As Integer
        Public Property LastExitCode As Integer?
    End Class

    ' ============================================================
    '  Built-in Triggers
    ' ============================================================

    ''' <summary>
    ''' Fires on a cron schedule (NCrontab format).
    ''' </summary>
    Public Class ScheduleTrigger
        Implements ITrigger

        Public ReadOnly Property TriggerId As String = "schedule" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String = "Scheduled" Implements ITrigger.DisplayLabel
        Public ReadOnly Property Kind As TriggerKind = TriggerKind.Schedule Implements ITrigger.Kind
        Public Property CronExpression As String
    End Class

    ''' <summary>
    ''' Fires when an instance changes state.
    ''' </summary>
    Public Class StateChangeTrigger
        Implements ITrigger

        Public ReadOnly Property TriggerId As String = "state_change" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String = "State Changed" Implements ITrigger.DisplayLabel
        Public ReadOnly Property Kind As TriggerKind = TriggerKind.StateChange Implements ITrigger.Kind
        Public Property FromState As InstanceState?
        Public Property ToState As InstanceState?
    End Class

    ''' <summary>
    ''' Fires when an installed version differs from available.
    ''' </summary>
    Public Class VersionMismatchTrigger
        Implements ITrigger

        Public ReadOnly Property TriggerId As String = "version_mismatch" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String = "Update Available" Implements ITrigger.DisplayLabel
        Public ReadOnly Property Kind As TriggerKind = TriggerKind.VersionMismatch Implements ITrigger.Kind
    End Class

    ''' <summary>
    ''' Fires on manual invocation from the UI or remote command.
    ''' </summary>
    Public Class ManualTrigger
        Implements ITrigger

        Public ReadOnly Property TriggerId As String = "manual" Implements ITrigger.TriggerId
        Public ReadOnly Property DisplayLabel As String = "Manual" Implements ITrigger.DisplayLabel
        Public ReadOnly Property Kind As TriggerKind = TriggerKind.Manual Implements ITrigger.Kind
    End Class

    ' ============================================================
    '  Built-in Conditions
    ' ============================================================

    ''' <summary>
    ''' Passes when an instance is in the specified state.
    ''' </summary>
    Public Class InstanceStateCondition
        Implements ICondition

        Public ReadOnly Property ConditionId As String = "instance_state" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String = "Instance State Check" Implements ICondition.DisplayLabel
        Public Property RequiredState As InstanceState
        Public Property InstanceId As String

        Public Async Function Evaluate(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            Dim info = Await ctx.GetInstanceState(InstanceId)
            If info.CurrentState = RequiredState Then
                Return ConditionResult.Pass($"Instance {InstanceId} is in {RequiredState} state")
            End If
            Return ConditionResult.Fail($"Instance {InstanceId} is {info.CurrentState}, required {RequiredState}")
        End Function
    End Class

    ''' <summary>
    ''' Passes when player count satisfies a threshold.
    ''' Long-running — polls until the condition is met or cancelled.
    ''' </summary>
    Public Class WaitForPlayerCountCondition
        Implements ICondition

        Public ReadOnly Property ConditionId As String = "wait_player_count" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String = "Wait For Player Count" Implements ICondition.DisplayLabel
        Public Property InstanceId As String
        Public Property MaxPlayers As Integer = 0
        Public Property PollIntervalMs As Integer = 15000
        Public Property TimeoutMs As Integer = 0

        Public Async Function Evaluate(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            Dim deadline = If(TimeoutMs > 0,
                              DateTime.UtcNow.AddMilliseconds(TimeoutMs),
                              DateTime.MaxValue)

            While Not cancellation.IsCancellationRequested
                Dim count = Await ctx.GetPlayerCount(InstanceId)
                If count <= MaxPlayers Then
                    Return ConditionResult.Pass($"Player count is {count} (threshold: {MaxPlayers})")
                End If
                If DateTime.UtcNow >= deadline Then
                    Return ConditionResult.Fail($"Timed out waiting for player count <= {MaxPlayers} (current: {count})")
                End If
                Await Task.Delay(PollIntervalMs, cancellation)
            End While

            Return ConditionResult.Fail("Cancelled while waiting for player count")
        End Function
    End Class

    ''' <summary>
    ''' Passes when ALL instances sharing an installation have
    ''' zero (or below threshold) players. Installation-scoped.
    ''' </summary>
    Public Class AllInstancesEmptyCondition
        Implements ICondition

        Public ReadOnly Property ConditionId As String = "all_instances_empty" Implements ICondition.ConditionId
        Public ReadOnly Property DisplayLabel As String = "All Instances Empty" Implements ICondition.DisplayLabel
        Public Property InstallationId As String
        Public Property MaxPlayers As Integer = 0
        Public Property PollIntervalMs As Integer = 15000
        Public Property TimeoutMs As Integer = 0

        Public Async Function Evaluate(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ConditionResult) Implements ICondition.Evaluate
            Dim deadline = If(TimeoutMs > 0,
                              DateTime.UtcNow.AddMilliseconds(TimeoutMs),
                              DateTime.MaxValue)

            Dim instanceIds = Await ctx.GetInstanceIdsForInstallation(InstallationId)

            While Not cancellation.IsCancellationRequested
                Dim allEmpty = True
                For Each instId In instanceIds
                    Dim count = Await ctx.GetPlayerCount(instId)
                    If count > MaxPlayers Then
                        allEmpty = False
                        Exit For
                    End If
                Next
                If allEmpty Then
                    Return ConditionResult.Pass($"All {instanceIds.Count} instances at or below {MaxPlayers} players")
                End If
                If DateTime.UtcNow >= deadline Then
                    Return ConditionResult.Fail($"Timed out waiting for all instances to empty")
                End If
                Await Task.Delay(PollIntervalMs, cancellation)
            End While

            Return ConditionResult.Fail("Cancelled while waiting for instances to empty")
        End Function
    End Class

    ' ============================================================
    '  Built-in Actions
    ' ============================================================

    ''' <summary>
    ''' Starts a single instance.
    ''' </summary>
    Public Class StartInstanceAction
        Implements IAction

        Public ReadOnly Property ActionId As String = "start_instance" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Start Instance" Implements IAction.DisplayLabel
        Public Property InstanceId As String

        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Dim ok = Await ctx.StartInstance(InstanceId)
            If ok Then Return ActionResult.Ok($"Started instance {InstanceId}")
            Return ActionResult.Fail($"Failed to start instance {InstanceId}")
        End Function
    End Class

    ''' <summary>
    ''' Stops a single instance.
    ''' </summary>
    Public Class StopInstanceAction
        Implements IAction

        Public ReadOnly Property ActionId As String = "stop_instance" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Stop Instance" Implements IAction.DisplayLabel
        Public Property InstanceId As String
        Public Property GracefulTimeoutMs As Integer = 10000

        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Dim stopResult = Await ctx.StopInstance(InstanceId, GracefulTimeoutMs)
            If stopResult Then Return ActionResult.Ok($"Stopped instance {InstanceId}")
            Return ActionResult.Fail($"Failed to stop instance {InstanceId}")
        End Function
    End Class

    ''' <summary>
    ''' Restarts a single instance (stop then start).
    ''' </summary>
    Public Class RestartInstanceAction
        Implements IAction

        Public ReadOnly Property ActionId As String = "restart_instance" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Restart Instance" Implements IAction.DisplayLabel
        Public Property InstanceId As String
        Public Property GracefulTimeoutMs As Integer = 10000
        Public Property DelayBetweenMs As Integer = 2000

        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Dim stopResult = Await ctx.StopInstance(InstanceId, GracefulTimeoutMs)
            If Not stopResult Then
                Return ActionResult.Fail($"Failed to stop instance {InstanceId}")
            End If
            If DelayBetweenMs > 0 Then
                Await Task.Delay(DelayBetweenMs, cancellation)
            End If
            Dim startOk = Await ctx.StartInstance(InstanceId)
            If startOk Then Return ActionResult.Ok($"Restarted instance {InstanceId}")
            Return ActionResult.Fail($"Stopped but failed to start instance {InstanceId}")
        End Function
    End Class

    ''' <summary>
    ''' Stops all instances sharing an installation.
    ''' Installation-scoped.
    ''' </summary>
    Public Class StopAllInstancesAction
        Implements IAction

        Public ReadOnly Property ActionId As String = "stop_all_instances" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Stop All Instances" Implements IAction.DisplayLabel
        Public Property InstallationId As String
        Public Property GracefulTimeoutMs As Integer = 10000

        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Dim instanceIds = Await ctx.GetInstanceIdsForInstallation(InstallationId)
            Dim details As New List(Of String)
            Dim allOk = True
            For Each instId In instanceIds
                Dim ok = Await ctx.StopInstance(instId, GracefulTimeoutMs)
                details.Add($"{instId}: {If(ok, "stopped", "FAILED")}")
                If Not ok Then allOk = False
            Next
            If allOk Then
                Return ActionResult.Ok($"Stopped all {instanceIds.Count} instances", details.ToArray())
            End If
            Return ActionResult.Fail("Some instances failed to stop", details.ToArray())
        End Function
    End Class

    ''' <summary>
    ''' Starts all instances sharing an installation.
    ''' Installation-scoped.
    ''' </summary>
    Public Class StartAllInstancesAction
        Implements IAction

        Public ReadOnly Property ActionId As String = "start_all_instances" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Start All Instances" Implements IAction.DisplayLabel
        Public Property InstallationId As String

        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Dim instanceIds = Await ctx.GetInstanceIdsForInstallation(InstallationId)
            Dim details As New List(Of String)
            Dim allOk = True
            For Each instId In instanceIds
                Dim ok = Await ctx.StartInstance(instId)
                details.Add($"{instId}: {If(ok, "started", "FAILED")}")
                If Not ok Then allOk = False
            Next
            If allOk Then
                Return ActionResult.Ok($"Started all {instanceIds.Count} instances", details.ToArray())
            End If
            Return ActionResult.Fail("Some instances failed to start", details.ToArray())
        End Function
    End Class

    ''' <summary>
    ''' Updates an installation (stop all instances, update, restart).
    ''' </summary>
    Public Class UpdateInstallationAction
        Implements IAction

        Public ReadOnly Property ActionId As String = "update_installation" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Update Installation" Implements IAction.DisplayLabel
        Public Property InstallationId As String

        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Dim ok = Await ctx.UpdateInstallation(InstallationId)
            If ok Then Return ActionResult.Ok($"Updated installation {InstallationId}")
            Return ActionResult.Fail($"Failed to update installation {InstallationId}")
        End Function
    End Class

    ''' <summary>
    ''' Sends an RCON command to an instance.
    ''' </summary>
    Public Class SendRconCommandAction
        Implements IAction

        Public ReadOnly Property ActionId As String = "send_rcon" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Send RCON Command" Implements IAction.DisplayLabel
        Public Property InstanceId As String
        Public Property Command As String

        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Dim response = Await ctx.SendRconCommand(InstanceId, Command)
            Return ActionResult.Ok($"RCON response: {response}")
        End Function
    End Class

    ''' <summary>
    ''' Sends a notification via a notification plugin.
    ''' </summary>
    Public Class NotifyAction
        Implements IAction

        Public ReadOnly Property ActionId As String = "notify" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Send Notification" Implements IAction.DisplayLabel
        Public Property NotificationPluginId As String
        Public Property Message As String
        Public Property Severity As NotificationSeverity

        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            Await ctx.SendNotification(NotificationPluginId, Message, Severity)
            Return ActionResult.Ok($"Notification sent via {NotificationPluginId}")
        End Function
    End Class

    ''' <summary>
    ''' Waits for a fixed duration.
    ''' </summary>
    Public Class WaitAction
        Implements IAction

        Public ReadOnly Property ActionId As String = "wait" Implements IAction.ActionId
        Public ReadOnly Property DisplayLabel As String = "Wait" Implements IAction.DisplayLabel
        Public Property DurationMs As Integer

        Public Async Function Execute(ctx As RuleContext, cancellation As CancellationToken) As Task(Of ActionResult) Implements IAction.Execute
            ctx.LogProgress($"Waiting {DurationMs}ms...")
            Await Task.Delay(DurationMs, cancellation)
            Return ActionResult.Ok($"Waited {DurationMs}ms")
        End Function
    End Class

    ''' <summary>
    ''' Executes a sequence of actions in order. The heart of
    ''' coordinated multi-step operations.
    '''
    ''' Example: coordinated Last Oasis multi-instance update
    '''   1. NotifyAction          "Server updating in 15 min"
    '''   2. WaitAction            900s
    '''   3. NotifyAction          "Server updating in 5 min"
    '''   4. WaitAction            240s
    '''   5. SendRconCommandAction "Server updating in 1 min"
    '''   6. WaitAction            60s
    '''   7. WaitForPlayerCount    (inline condition re-eval as action)
    '''   8. StopAllInstancesAction
    '''   9. UpdateInstallationAction
    '''  10. StartAllInstancesAction
    '''  11. NotifyAction          "Servers back online"
    ''' </summary>
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
    '  Plugin extension points
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
