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

        Public Sub New(pluginRegistry As PluginRegistry,
                       instanceManager As InstanceManager,
                       logger As ILogger(Of NotificationService))
            _pluginRegistry = pluginRegistry
            _instanceManager = instanceManager
            _logger = logger
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
