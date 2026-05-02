Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Automation
Imports GSM.Notification
Imports GSM.Manager
Imports GSM.Manager.Data

' ============================================================
'  NotificationService — routes events to notification plugins
'
'  Manages notification plugin lifecycle (init/shutdown) and
'  provides a unified API for sending notifications from
'  the automation engine and other services.
'
'  Subscribes to NotificationEmitter.Emitted so events built by
'  the emitter (from InstanceManager, update workflows, etc.)
'  reach plugins without the emitter having to hold a reference
'  back to this service. That one-way arrow is what keeps us out
'  of a DI cycle.
'
'  Also implements IRemoteCommandHandler so notification plugins
'  (e.g. Discord bot) can route inbound commands back to the
'  manager for execution.
' ============================================================

Namespace GSM.Manager.Core

    Public Class NotificationService
        Implements IRemoteCommandHandler

        Private ReadOnly _plugins As New ConcurrentDictionary(Of String, INotificationPlugin)
        Private ReadOnly _pluginRegistry As PluginRegistry
        Private ReadOnly _logger As ILogger(Of NotificationService)
        Private ReadOnly _instanceManager As InstanceManager
        Private ReadOnly _emitter As NotificationEmitter

        Public Sub New(pluginRegistry As PluginRegistry,
                       instanceManager As InstanceManager,
                       emitter As NotificationEmitter,
                       logger As ILogger(Of NotificationService))
            _pluginRegistry = pluginRegistry
            _instanceManager = instanceManager
            _emitter = emitter
            _logger = logger

            ' Subscribe once, for the life of this singleton. No
            ' corresponding RemoveHandler — service and emitter live
            ' together for the life of the app. If that ever changes,
            ' implement IDisposable and unsubscribe there.
            AddHandler _emitter.Emitted, AddressOf OnEmitted
        End Sub

        ''' <summary>
        ''' Handles events raised by NotificationEmitter. Fire-and-
        ''' forget: the emitter raises on a background task and does
        ''' not await us, so any exception must be caught here or it
        ''' becomes an unobserved task exception.
        ''' </summary>
        Private Async Sub OnEmitted(sender As Object, e As NotificationEmittedEventArgs)
            Try
                Await BroadcastAsync(e.Context, CancellationToken.None)
            Catch ex As Exception
                _logger.LogWarning(ex, "Broadcast failed while handling emitter event for {Event}",
                                   e.Context?.EventType)
            End Try
        End Sub

        ' ============================================================
        '  Plugin management
        ' ============================================================

        ''' <summary>
        ''' Registers and initialises a notification plugin.
        ''' Config is loaded from the database.
        ''' </summary>
        Public Async Function RegisterPluginAsync(plugin As INotificationPlugin,
                                                   cancellation As CancellationToken) As Task
            ' Load config from database
            Dim config As New Dictionary(Of String, String)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim entity = db.NotificationPlugins.Find(plugin.PluginId)
                If entity IsNot Nothing AndAlso Not String.IsNullOrEmpty(entity.ConfigJson) Then
                    Try
                        config = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(entity.ConfigJson)
                    Catch
                    End Try
                End If
            End Using

            Try
                Await plugin.InitialiseAsync(config, Me, cancellation)
                _plugins(plugin.PluginId) = plugin
                _logger.LogInformation("Registered notification plugin: {Id}", plugin.PluginId)
            Catch ex As Exception
                _logger.LogError(ex, "Failed to initialise notification plugin {Id}", plugin.PluginId)
            End Try
        End Function

        ''' <summary>
        ''' Shuts down all registered notification plugins.
        ''' </summary>
        Public Async Function ShutdownAllAsync(cancellation As CancellationToken) As Task
            For Each kvp In _plugins
                Try
                    Await kvp.Value.ShutdownAsync(cancellation)
                Catch ex As Exception
                    _logger.LogWarning(ex, "Error shutting down notification plugin {Id}", kvp.Key)
                End Try
            Next
            _plugins.Clear()
        End Function

        ' ============================================================
        '  Sending notifications
        ' ============================================================

        ''' <summary>
        ''' Sends a notification to a specific plugin.
        ''' </summary>
        Public Async Function SendAsync(pluginId As String,
                                         context As NotificationContext,
                                         cancellation As CancellationToken) As Task(Of Boolean)
            Dim plugin As INotificationPlugin = Nothing
            If Not _plugins.TryGetValue(pluginId, plugin) Then
                _logger.LogWarning("Notification plugin {Id} not found", pluginId)
                Return False
            End If

            Try
                Return Await plugin.SendNotificationAsync(context, cancellation)
            Catch ex As Exception
                _logger.LogError(ex, "Failed to send notification via {Id}", pluginId)
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Sends a notification to all enabled plugins that subscribe
        ''' to the given event type.
        ''' </summary>
        Public Async Function BroadcastAsync(context As NotificationContext,
                                              cancellation As CancellationToken) As Task
            For Each kvp In _plugins
                Try
                    Await kvp.Value.SendNotificationAsync(context, cancellation)
                Catch ex As Exception
                    _logger.LogWarning(ex, "Notification broadcast failed for {Id}", kvp.Key)
                End Try
            Next
        End Function

        ''' <summary>
        ''' Sends a simple notification by message and severity.
        ''' Convenience wrapper for automation actions.
        '''
        ''' DEPRECATED in Phase 4b-1.5 — use SendToDestinationAsync
        ''' instead. Kept for any caller that still routes via
        ''' PluginId; new automation rules go through the
        ''' destination-aware path. Will be removed once no
        ''' callers remain.
        ''' </summary>
        Public Async Function SendSimpleAsync(pluginId As String,
                                               message As String,
                                               severity As NotificationSeverity) As Task
            Dim context As New NotificationContext With {
                .EventType = NotificationEventType.Custom,
                .Severity = severity,
                .Title = "PowerGSM",
                .Message = message,
                .Timestamp = DateTime.UtcNow,
                .Tokens = New NotificationTokens(),
                .Metadata = New Dictionary(Of String, String)
            }
            Await SendAsync(pluginId, context, CancellationToken.None)
        End Function

        ''' <summary>
        ''' Phase 4b-1.5: send a custom message to one specific
        ''' NotificationDestination by ID. Used by the automation
        ''' engine's NotifyAction.
        '''
        ''' Resolution: iterates registered plugins asking each
        ''' which one OwnsDestination(destinationId). The first
        ''' to claim ownership handles the dispatch. Plugins that
        ''' don't implement IDestinationTargetingPlugin are
        ''' skipped — they don't support custom dispatch.
        '''
        ''' Token substitution is performed here BEFORE handing
        ''' the message to the plugin, so transports always see
        ''' final literal text. The set of supported tokens is
        ''' defined by SubstituteTokens; see its comment for the
        ''' full list.
        ''' </summary>
        Public Async Function SendToDestinationAsync(
                destinationId As String,
                message As String,
                severity As NotificationSeverity,
                tokens As NotificationTokens) As Task(Of Boolean)

            If String.IsNullOrEmpty(destinationId) Then
                _logger.LogWarning("SendToDestinationAsync called with empty destinationId")
                Return False
            End If

            ' Resolve {Token} placeholders in the message body
            ' against the supplied token bundle. Tokens not
            ' applicable to the rule's scope substitute as empty
            ' string — quieter than printing "(n/a)" into the
            ' user's Discord post.
            Dim resolvedMessage = SubstituteTokens(message, tokens)

            Dim attempted = False
            For Each kvp In _plugins
                Dim targeting = TryCast(kvp.Value, IDestinationTargetingPlugin)
                If targeting Is Nothing Then Continue For
                If Not targeting.OwnsDestination(destinationId) Then Continue For

                attempted = True
                Try
                    Dim ok = Await targeting.SendCustomToDestinationAsync(
                        destinationId, resolvedMessage, severity,
                        If(tokens, New NotificationTokens()),
                        CancellationToken.None)
                    If ok Then Return True
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "Plugin {Plugin} threw while dispatching to destination {Dest}",
                        kvp.Key, destinationId)
                End Try
            Next

            If Not attempted Then
                _logger.LogWarning(
                    "No registered plugin owns destination {Dest}. Is the plugin loaded and config refreshed?",
                    destinationId)
            End If
            Return False
        End Function

        ' ============================================================
        '  Token substitution
        ' ============================================================

        ''' <summary>
        ''' Replaces {Token} placeholders in a message with values
        ''' from the supplied NotificationTokens. Case-insensitive
        ''' on token names, but token names themselves stay in
        ''' canonical CamelCase below for documentation purposes.
        '''
        ''' Supported tokens:
        '''   {RuleName}         - the firing rule's display name
        '''   {InstanceId}       - target instance's ID (Instance scope)
        '''   {InstanceName}     - target instance's display name
        '''   {InstallationId}   - target installation's ID
        '''   {InstallationName} - target installation's display name
        '''   {NodeId}           - target node's ID
        '''   {NodeName}         - target node's display name
        '''   {GameId}           - game ID of the target instance/installation
        '''   {Time}             - current local time, HH:mm
        '''   {Date}             - current local date, yyyy-MM-dd
        '''
        ''' Unknown tokens are left unmodified — if a user typed
        ''' {SomethingMisspelled} we'd rather have it visible in
        ''' the output than silently disappear, so they notice
        ''' and fix it. Empty/null values for known tokens
        ''' substitute as empty string.
        '''
        ''' Public so the rule editor's preview helper (future)
        ''' can use the same logic.
        ''' </summary>
        Public Shared Function SubstituteTokens(message As String,
                                                 tokens As NotificationTokens) As String
            If String.IsNullOrEmpty(message) Then Return message
            If message.IndexOf("{"c) < 0 Then Return message

            ' Build a case-insensitive lookup of supported tokens.
            ' Time/Date are always available regardless of the
            ' tokens bundle (and override any same-named entries
            ' in CustomTokens, which would be weird but possible).
            Dim now = DateTime.Now
            Dim values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            values("Time") = now.ToString("HH:mm")
            values("Date") = now.ToString("yyyy-MM-dd")

            If tokens IsNot Nothing Then
                values("RuleName") = If(tokens.RuleName, "")
                values("InstanceId") = If(tokens.InstanceId, "")
                values("InstanceName") = If(tokens.InstanceName, "")
                values("InstallationId") = If(tokens.InstallationId, "")
                values("InstallationName") = If(tokens.InstallationName, "")
                values("NodeId") = If(tokens.NodeId, "")
                values("NodeName") = If(tokens.NodeName, "")
                values("GameId") = If(tokens.GameId, "")
            End If

            ' Single regex pass: \{(\w+)\}. Use a MatchEvaluator
            ' so unknown tokens stay literal in the output.
            Return System.Text.RegularExpressions.Regex.Replace(
                message,
                "\{([A-Za-z][A-Za-z0-9_]*)\}",
                Function(m)
                    Dim key = m.Groups(1).Value
                    Dim val As String = Nothing
                    If values.TryGetValue(key, val) Then Return val
                    Return m.Value  ' unknown token, leave literal
                End Function)
        End Function

        ' ============================================================
        '  IRemoteCommandHandler — inbound commands from plugins
        ' ============================================================

        Public Async Function HandleCommandAsync(command As InboundCommand,
                                                  cancellation As CancellationToken) As Task(Of CommandResult) Implements IRemoteCommandHandler.HandleCommandAsync
            _logger.LogInformation("Remote command from {User}: {Cmd} {Args}",
                                   command.RemoteUserName, command.CommandName,
                                   String.Join(" ", If(command.Arguments, New List(Of String))))

            Select Case command.CommandName.ToLower()
                Case "status"
                    Return Await HandleStatusCommand(command)

                Case "start"
                    Return Await HandleStartCommand(command)

                Case "stop"
                    Return Await HandleStopCommand(command)

                Case "restart"
                    Return Await HandleRestartCommand(command)

                Case "help"
                    Return HandleHelpCommand()

                Case Else
                    Return CommandResult.Fail($"Unknown command: {command.CommandName}")
            End Select
        End Function

        Public Function GetAvailableCommands() As IReadOnlyList(Of RemoteCommandDescriptor) Implements IRemoteCommandHandler.GetAvailableCommands
            Return New List(Of RemoteCommandDescriptor) From {
                New RemoteCommandDescriptor With {
                    .CommandName = "status",
                    .Description = "Show instance status",
                    .RequiredPermission = CommandPermission.Everyone,
                    .ParameterDescriptions = New List(Of String) From {"[instanceId]"}
                },
                New RemoteCommandDescriptor With {
                    .CommandName = "start",
                    .Description = "Start an instance",
                    .RequiredPermission = CommandPermission.ServerOperator,
                    .ParameterDescriptions = New List(Of String) From {"instanceId"}
                },
                New RemoteCommandDescriptor With {
                    .CommandName = "stop",
                    .Description = "Stop an instance",
                    .RequiredPermission = CommandPermission.ServerOperator,
                    .ParameterDescriptions = New List(Of String) From {"instanceId"}
                },
                New RemoteCommandDescriptor With {
                    .CommandName = "restart",
                    .Description = "Restart an instance",
                    .RequiredPermission = CommandPermission.ServerOperator,
                    .ParameterDescriptions = New List(Of String) From {"instanceId"}
                },
                New RemoteCommandDescriptor With {
                    .CommandName = "help",
                    .Description = "Show available commands",
                    .RequiredPermission = CommandPermission.Everyone,
                    .ParameterDescriptions = New List(Of String)
                }
            }
        End Function

        ' ============================================================
        '  Command handlers
        ' ============================================================

        Private Async Function HandleStatusCommand(command As InboundCommand) As Task(Of CommandResult)
            If command.Arguments IsNot Nothing AndAlso command.Arguments.Count > 0 Then
                Dim instId = command.Arguments(0)
                Dim state = Await _instanceManager.RefreshInstanceStateAsync(instId)
                If state IsNot Nothing Then
                    Return CommandResult.Ok($"{instId}: {state.CurrentState} (PID: {state.Pid}, Uptime: {state.UptimeSeconds}s)")
                End If
                Return CommandResult.Fail($"Could not get status for {instId}")
            End If

            ' Summary of all tracked instances
            Return CommandResult.Ok("Use: status <instanceId>")
        End Function

        Private Async Function HandleStartCommand(command As InboundCommand) As Task(Of CommandResult)
            If command.UserPermission < CommandPermission.ServerOperator Then
                Return CommandResult.Fail("Insufficient permissions")
            End If
            If command.Arguments Is Nothing OrElse command.Arguments.Count = 0 Then
                Return CommandResult.Fail("Usage: start <instanceId>")
            End If
            Dim ok = Await _instanceManager.StartInstanceAsync(command.Arguments(0))
            Return If(ok, CommandResult.Ok("Instance started"),
                          CommandResult.Fail("Failed to start instance"))
        End Function

        Private Async Function HandleStopCommand(command As InboundCommand) As Task(Of CommandResult)
            If command.UserPermission < CommandPermission.ServerOperator Then
                Return CommandResult.Fail("Insufficient permissions")
            End If
            If command.Arguments Is Nothing OrElse command.Arguments.Count = 0 Then
                Return CommandResult.Fail("Usage: stop <instanceId>")
            End If
            Dim ok = Await _instanceManager.StopInstanceAsync(command.Arguments(0))
            Return If(ok, CommandResult.Ok("Instance stopped"),
                          CommandResult.Fail("Failed to stop instance"))
        End Function

        Private Async Function HandleRestartCommand(command As InboundCommand) As Task(Of CommandResult)
            If command.UserPermission < CommandPermission.ServerOperator Then
                Return CommandResult.Fail("Insufficient permissions")
            End If
            If command.Arguments Is Nothing OrElse command.Arguments.Count = 0 Then
                Return CommandResult.Fail("Usage: restart <instanceId>")
            End If
            Dim ok = Await _instanceManager.RestartInstanceAsync(command.Arguments(0))
            Return If(ok, CommandResult.Ok("Instance restarted"),
                          CommandResult.Fail("Failed to restart instance"))
        End Function

        Private Function HandleHelpCommand() As CommandResult
            Dim cmds = GetAvailableCommands()
            Dim lines = cmds.Select(Function(c) $"  {c.CommandName} — {c.Description}")
            Return CommandResult.Ok("Available commands:" & vbCrLf & String.Join(vbCrLf, lines))
        End Function

    End Class

End Namespace