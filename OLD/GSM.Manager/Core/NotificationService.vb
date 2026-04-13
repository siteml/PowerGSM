Imports System.Collections.Generic
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports GSM.Automation
Imports GSM.Data
Imports GSM.Notification
Imports GSM.Plugin

' ============================================================
'  NotificationService
'
'  Dispatches outbound notifications to all registered
'  INotificationPlugin instances (Discord, Telegram, etc).
'
'  Called from two places:
'    1. AutomationEngine via RuleContextImpl.SendNotification
'       when a NotifyAction fires in a rule sequence.
'    2. NotificationSubscriptionDispatcher (below) when a
'       system event (crash, state change, player join etc)
'       fires an automatic subscription.
'
'  Token resolution:
'    The {InstanceName}, {State}, {PlayerCount} etc tokens
'    in notification message templates are resolved here,
'    once, before being handed to each plugin. Plugins receive
'    a fully resolved NotificationContext - they never need to
'    query the manager for additional data.
'
'  Failure isolation:
'    If one plugin fails to send (Discord rate limit, network
'    error etc), the others still receive the notification.
'    Errors are logged but never propagate to the caller.
' ============================================================

Namespace GSM.Core

    Public Class NotificationService

        Private ReadOnly _pluginRegistry As PluginRegistry
        Private ReadOnly _instanceManager As InstanceManager
        Private ReadOnly _dbFactory As IDbContextFactory(Of GsmDbContext)
        Private ReadOnly _logger As ILogger(Of NotificationService)

        Public Sub New(pluginRegistry As PluginRegistry,
                       instanceManager As InstanceManager,
                       dbFactory As IDbContextFactory(Of GsmDbContext),
                       logger As ILogger(Of NotificationService))
            _pluginRegistry = pluginRegistry
            _instanceManager = instanceManager
            _dbFactory = dbFactory
            _logger = logger
        End Sub


        ' ============================================================
        '  SEND - called by RuleContextImpl.SendNotification
        ' ============================================================

        ' Resolve the message template, build the context, and dispatch
        ' to the requested plugins (or all plugins if targetPluginIds is empty).
        Public Async Function SendAsync(messageTemplate As String,
                                         severity As NotificationSeverity,
                                         targetInstanceId As String,
                                         targetInstallationId As String,
                                         scope As RuleScope,
                                         ruleId As String,
                                         ruleName As String,
                                         targetPluginIds As List(Of String),
                                         cancellation As CancellationToken) As Task(Of Boolean)

            ' Build the token set by fetching current instance state.
            Dim tokens = Await BuildTokensAsync(targetInstanceId, cancellation)

            ' Resolve template tokens.
            Dim resolvedMessage = ResolveTemplate(messageTemplate, tokens)

            ' Build the context passed to each plugin.
            Dim context As New NotificationContext With {
                .RuleId = ruleId,
                .RuleName = ruleName,
                .Severity = severity,
                .ResolvedMessage = resolvedMessage,
                .MessageTemplate = messageTemplate,
                .Tokens = tokens,
                .Scope = scope,
                .TargetInstanceId = targetInstanceId,
                .TargetInstallationId = targetInstallationId,
                .ShouldAlert = severity = NotificationSeverity.Critical,
                .RoutingHints = New Dictionary(Of String, String)(),
                .ExecutionLog = New List(Of String)(),
                .RecentLogLines = New List(Of String)()
            }

            Return Await DispatchAsync(context, targetPluginIds, cancellation)
        End Function

        ' Overload used by NotificationSubscriptionDispatcher for system events.
        Public Async Function SendEventAsync(
                eventType As NotificationEventType,
                targetInstanceId As String,
                targetInstallationId As String,
                scope As RuleScope,
                cancellation As CancellationToken) As Task

            ' Find all subscriptions that watch this event type and target.
            Using db = _dbFactory.CreateDbContext()
                Dim subscriptions = Await db.NotificationSubscriptions.
                    Where(Function(s) s.IsEnabled AndAlso
                                      s.TargetId = If(scope = RuleScope.Instance,
                                                       targetInstanceId,
                                                       targetInstallationId)).
                    ToListAsync(cancellation)

                For Each subscription In subscriptions
                    ' Check if this subscription watches this event type.
                    Dim eventTypes As List(Of String)
                    Try
                        eventTypes = JsonSerializer.Deserialize(Of List(Of String))(
                            subscription.EventTypesJson)
                    Catch
                        Continue For
                    End Try

                    If Not eventTypes.Contains(eventType.ToString()) Then Continue For

                    ' Build tokens and dispatch to this subscription's plugin.
                    Dim tokens = Await BuildTokensAsync(targetInstanceId, cancellation)
                    Dim message = BuildEventMessage(eventType, tokens)

                    Dim severity = EventSeverity(eventType)
                    Dim context As New NotificationContext With {
                        .Severity = severity,
                        .ResolvedMessage = message,
                        .MessageTemplate = message,
                        .Tokens = tokens,
                        .Scope = scope,
                        .TargetInstanceId = targetInstanceId,
                        .TargetInstallationId = targetInstallationId,
                        .ShouldAlert = severity = NotificationSeverity.Critical,
                        .RoutingHints = New Dictionary(Of String, String) From {
                            {"routeName", subscription.RouteName}
                        },
                        .ExecutionLog = New List(Of String)(),
                        .RecentLogLines = New List(Of String)()
                    }

                    Await DispatchAsync(context,
                        New List(Of String) From {subscription.PluginId},
                        cancellation)
                Next
            End Using
        End Function


        ' ============================================================
        '  DISPATCH
        ' ============================================================

        Private Async Function DispatchAsync(context As NotificationContext,
                                              targetPluginIds As List(Of String),
                                              cancellation As CancellationToken) As Task(Of Boolean)

            Dim plugins = _pluginRegistry.GetAllNotificationPlugins()

            ' Filter to requested plugins if specified.
            If targetPluginIds IsNot Nothing AndAlso targetPluginIds.Any() Then
                plugins = plugins.Where(
                    Function(p) targetPluginIds.Contains(p.PluginId,
                        StringComparer.OrdinalIgnoreCase)).ToList().AsReadOnly()
            End If

            If Not plugins.Any() Then
                _logger.LogDebug(
                    "NotificationService: no plugins to dispatch to " &
                    "(target: {Targets})",
                    If(targetPluginIds?.Any() = True,
                       String.Join(", ", targetPluginIds), "all"))
                Return True
            End If

            Dim allSucceeded = True

            ' Dispatch to each plugin independently.
            ' One plugin failing does not prevent others from receiving.
            Dim tasks = plugins.Select(
                Async Function(plugin)
                    Try
                        _logger.LogDebug(
                            "NotificationService: sending [{Sev}] to plugin '{Id}'",
                            context.Severity, plugin.PluginId)

                        Dim result = Await plugin.SendNotificationAsync(
                            context, cancellation)

                        If Not result.Success Then
                            _logger.LogWarning(
                                "NotificationService: plugin '{Id}' failed: {Err}",
                                plugin.PluginId, result.ErrorMessage)
                            allSucceeded = False
                        End If
                    Catch ex As OperationCanceledException
                        Throw
                    Catch ex As Exception
                        _logger.LogError(ex,
                            "NotificationService: plugin '{Id}' threw an exception",
                            plugin.PluginId)
                        allSucceeded = False
                    End Try
                End Function)

            Await Task.WhenAll(tasks)
            Return allSucceeded
        End Function


        ' ============================================================
        '  TOKEN RESOLUTION
        ' ============================================================

        Private Async Function BuildTokensAsync(
                instanceId As String,
                cancellation As CancellationToken) As Task(Of NotificationTokens)

            Dim tokens As New NotificationTokens With {
                .Timestamp = DateTime.UtcNow
            }

            If String.IsNullOrEmpty(instanceId) Then Return tokens

            Try
                Dim metrics = Await _instanceManager.GetMetricsAsync(
                    instanceId, cancellation)
                tokens.InstanceId = instanceId
                tokens.State = metrics.State.ToString()
                tokens.PlayerCount = metrics.PlayerCount.ToString()
                Dim playerNames As New List(Of String)()
                If metrics.Players IsNot Nothing Then
                    For Each player In metrics.Players
                        If player IsNot Nothing AndAlso player.Name IsNot Nothing Then
                            playerNames.Add(player.Name)
                        End If
                    Next
                End If
                tokens.PlayerList = playerNames

                If metrics.UptimeSeconds.HasValue Then
                    Dim uptime = TimeSpan.FromSeconds(metrics.UptimeSeconds.Value)
                    tokens.UptimeFormatted = $"{CInt(uptime.TotalHours)}h {uptime.Minutes}m"
                Else
                    tokens.UptimeFormatted = String.Empty
                End If

                ' Load display name from DB.
                Using db = _dbFactory.CreateDbContext()
                    Dim inst = Await db.Instances.
                        Include(Function(i) i.Installation).
                            ThenInclude(Function(n) n.Node).
                        FirstOrDefaultAsync(
                            Function(i) i.InstanceId = instanceId, cancellation)
                    If inst IsNot Nothing Then
                        tokens.InstanceName = inst.DisplayName
                        tokens.InstallationName = inst.Installation?.DisplayName
                        tokens.NodeName = inst.Installation?.Node?.DisplayName
                        Dim gamePlugin = _pluginRegistry.GetPlugin(inst.GameId)
                        If gamePlugin IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(gamePlugin.DisplayName) Then
                            tokens.GameDisplayName = gamePlugin.DisplayName
                        Else
                            tokens.GameDisplayName = inst.GameId
                        End If
                    End If
                End Using

            Catch ex As Exception
                _logger.LogDebug(ex,
                    "NotificationService: could not build full tokens for {Id}",
                    instanceId)
            End Try

            Return tokens
        End Function

        ' Replace {Token} placeholders in the message template.
        Private Shared Function ResolveTemplate(template As String,
                                                  tokens As NotificationTokens) As String
            If String.IsNullOrEmpty(template) Then Return template

            Return template.
                Replace("{InstanceName}",    If(tokens.InstanceName, "")).
                Replace("{InstanceId}",      If(tokens.InstanceId, "")).
                Replace("{GameDisplayName}", If(tokens.GameDisplayName, "")).
                Replace("{NodeName}",        If(tokens.NodeName, "")).
                Replace("{State}",           If(tokens.State, "")).
                Replace("{PreviousState}",   If(tokens.PreviousState, "")).
                Replace("{PlayerCount}",     If(tokens.PlayerCount, "0")).
                Replace("{CrashCount}",      If(tokens.CrashCount, "")).
                Replace("{ExitCode}",        If(tokens.ExitCode, "")).
                Replace("{Reason}",          If(tokens.Reason, "")).
                Replace("{Version}",         If(tokens.Version, "")).
                Replace("{AvailableVersion}", If(tokens.AvailableVersion, "")).
                Replace("{Timestamp}",       tokens.Timestamp.ToString("yyyy-MM-dd HH:mm:ss UTC"))
        End Function

        ' Build a default message for system events (subscriptions).
        Private Shared Function BuildEventMessage(eventType As NotificationEventType,
                                                    tokens As NotificationTokens) As String
            Dim name = If(tokens.InstanceName, tokens.InstanceId)
            Select Case eventType
                Case NotificationEventType.InstanceStarted
                    Return $"✅ **{name}** has started."
                Case NotificationEventType.InstanceStopped
                    Return $"⏹ **{name}** has stopped."
                Case NotificationEventType.InstanceCrashed
                    Return $"💥 **{name}** has crashed (exit code {tokens.ExitCode})."
                Case NotificationEventType.InstanceStartFailed
                    Return $"❌ **{name}** failed to start."
                Case NotificationEventType.InstanceCrashLoopHalted
                    Return $"🚨 **{name}** has entered crash loop halt after " &
                           $"{tokens.CrashCount} crashes. Manual intervention required."
                Case NotificationEventType.InstanceRestarting
                    Return $"🔄 **{name}** is restarting."
                Case NotificationEventType.PlayerJoined
                    Return $"👋 A player joined **{name}**. " &
                           $"Players online: {tokens.PlayerCount}"
                Case NotificationEventType.PlayerLeft
                    Return $"🚪 A player left **{name}**. " &
                           $"Players online: {tokens.PlayerCount}"
                Case NotificationEventType.UpdateAvailable
                    Return $"📦 Update available for **{name}**: " &
                           $"{tokens.Version} → {tokens.AvailableVersion}"
                Case NotificationEventType.UpdateStarted
                    Return $"⬇ Update started for **{name}**."
                Case NotificationEventType.UpdateCompleted
                    Return $"✅ Update completed for **{name}**."
                Case NotificationEventType.UpdateFailed
                    Return $"❌ Update failed for **{name}**."
                Case NotificationEventType.RconConnected
                    Return $"🔌 RCON connected for **{name}**."
                Case NotificationEventType.RconUnavailable
                    Return $"⚠ RCON unavailable for **{name}**."
                Case NotificationEventType.InstallPromptRequired
                    Return $"⌨ **{name}** install is waiting for input " &
                           "(Steam Guard or similar). Check the manager."
                Case Else
                    Return $"ℹ **{name}**: {eventType}"
            End Select
        End Function

        Private Shared Function EventSeverity(
                eventType As NotificationEventType) As NotificationSeverity
            Select Case eventType
                Case NotificationEventType.InstanceCrashed,
                     NotificationEventType.InstanceCrashLoopHalted,
                     NotificationEventType.InstanceStartFailed,
                     NotificationEventType.UpdateFailed
                    Return NotificationSeverity.Critical
                Case NotificationEventType.InstanceStopped,
                     NotificationEventType.RconUnavailable,
                     NotificationEventType.InstallPromptRequired
                    Return NotificationSeverity.Warning
                Case Else
                    Return NotificationSeverity.Info
            End Select
        End Function

    End Class

End Namespace
