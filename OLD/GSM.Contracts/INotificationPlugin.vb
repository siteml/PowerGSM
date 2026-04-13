Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin
Imports GSM.Automation

' ============================================================
'  GSM Notification Plugin Contract
'
'  Notification plugins are loaded by the same Roslyn
'  PluginRegistry as IGamePlugin. Drop a .vb file implementing
'  INotificationPlugin into plugins\ and it will be picked up
'  on the next hot-reload cycle.
'
'  A notification plugin has two responsibilities:
'    1. Outbound: receive NotificationContext from the automation
'       engine (via NotifyAction) and deliver it somewhere -
'       Discord, Telegram, a webhook, email, etc.
'    2. Inbound: declare commands that remote users can issue
'       and handle them when they arrive. The plugin owns all
'       platform-specific auth and routing (Discord roles,
'       Telegram chat IDs, etc). The manager only sees
'       RemoteCommand and CommandResult.
'
'  The manager never imports Discord, Telegram, or any other
'  platform SDK. All platform dependencies live inside the
'  plugin assembly, isolated in its AssemblyLoadContext.
'
'  CRITICAL: Same hot-reload rule as IGamePlugin - nothing in
'  Core may hold a reference to a concrete notification plugin
'  type. Always INotificationPlugin. Always resolve through
'  PluginRegistry.GetNotificationPlugin(pluginId).
' ============================================================

Namespace GSM.Notification

    ' ------------------------------------------------------------
    '  Primary interface
    ' ------------------------------------------------------------

    Public Interface INotificationPlugin

        ' Stable identifier. Used to target specific plugins from
        ' NotifyAction.TargetPluginIds. Never change once deployed.
        ' e.g. "discord", "telegram", "webhook"
        ReadOnly Property PluginId As String

        ' Human-readable name shown in the manager UI
        ReadOnly Property DisplayName As String

        ' Called once after the plugin is loaded or hot-reloaded.
        ' Use to connect to the platform (Discord gateway, etc),
        ' register slash commands, start polling loops.
        ' Must return promptly - do long-running work on a background task.
        ' cancellation fires when the plugin is being unloaded.
        Function InitialiseAsync(config As NotificationPluginConfig,
                                 handler As IRemoteCommandHandler,
                                 cancellation As CancellationToken) As Task

        ' Called when the plugin is being unloaded (hot-reload or shutdown).
        ' Disconnect from platform, cancel background tasks, flush buffers.
        Function ShutdownAsync() As Task

        ' ---- Outbound ----

        ' Deliver a notification to the platform.
        ' The plugin decides how to format and route it.
        ' Must not throw - catch exceptions and return Failure.
        Function SendNotificationAsync(context As NotificationContext,
                                       cancellation As CancellationToken) As Task(Of NotificationResult)

        ' ---- Inbound ----

        ' Declares what commands remote users can issue via this plugin.
        ' The manager uses this to populate the permissions UI and to
        ' validate incoming commands before passing them to HandleCommandAsync.
        ' Return an empty list if this plugin is outbound-only.
        Function GetSupportedCommands() As IReadOnlyList(Of RemoteCommandDescriptor)

        ' ---- Config schema ----

        ' Describes the config fields this plugin needs (token, channel IDs, etc).
        ' The manager renders a form from these descriptors - the plugin never
        ' asks the user directly.
        Function GetConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)

    End Interface


    ' ------------------------------------------------------------
    '  Plugin config
    '  Thin wrapper carrying the JSON blob from the DB.
    '  Plugin deserializes RawJson into its own typed class.
    ' ------------------------------------------------------------

    Public Class NotificationPluginConfig
        Public Property PluginId As String
        Public Property RawJson As String       ' Full config blob from DB
        Public Property IsEnabled As Boolean
    End Class


    ' ------------------------------------------------------------
    '  Outbound: notification context
    '  Passed to SendNotificationAsync by the automation engine.
    '  The plugin uses what it needs and ignores the rest.
    ' ------------------------------------------------------------

    Public Class NotificationContext

        ' Which rule fired this notification
        Public Property RuleId As String
        Public Property RuleName As String

        ' Severity from the NotifyAction
        Public Property Severity As NotificationSeverity

        ' The message template from NotifyAction, with tokens
        ' already resolved by the engine before passing here.
        ' e.g. "Server MyOasis has crashed (exit code -1)"
        Public Property ResolvedMessage As String

        ' Raw template before token resolution - useful if the
        ' plugin wants to apply its own formatting to tokens
        Public Property MessageTemplate As String

        ' Token values the engine resolved - plugin may use these
        ' to build richer embeds (e.g. player list in a Discord embed field)
        Public Property Tokens As NotificationTokens

        ' The scope and target that fired the rule
        Public Property Scope As RuleScope
        Public Property TargetInstanceId As String      ' Empty if Installation/Global scope
        Public Property TargetInstallationId As String  ' Empty if Global scope

        ' Execution log from the rule's sequence up to this point.
        ' Useful for "here's what happened before the crash" embeds.
        Public Property ExecutionLog As IReadOnlyList(Of String)

        ' Recent log lines from the instance at time of notification.
        ' Populated when Severity = Warning or Critical.
        ' Sourced from the ring buffer - may be empty if unavailable.
        Public Property RecentLogLines As IReadOnlyList(Of String)

        ' Whether the plugin should mention/ping on this notification.
        ' The plugin maps this to whatever the platform calls it
        ' (Discord @here, Telegram reply, etc).
        ' The automation engine sets this based on Severity:
        '   Info → False, Warning → False, Critical → True
        ' Rules may override this explicitly.
        Public Property ShouldAlert As Boolean

        ' Routing hints - the plugin uses these to pick channels/chats.
        ' Keys are plugin-defined (e.g. "channelType": "admin").
        ' Empty = use the plugin's default routing.
        Public Property RoutingHints As Dictionary(Of String, String)

    End Class

    ' Resolved token values for use in rich embeds.
    ' All properties may be empty/Nothing if not applicable.
    Public Class NotificationTokens
        Public Property InstanceName As String
        Public Property InstanceId As String
        Public Property InstallationName As String
        Public Property GameDisplayName As String
        Public Property NodeName As String
        Public Property State As String             ' InstanceState.ToString()
        Public Property PreviousState As String
        Public Property PlayerCount As String       ' "0", "5", "unknown"
        Public Property PlayerList As IReadOnlyList(Of String)
        Public Property CrashCount As String        ' Crashes in current window
        Public Property ExitCode As String          ' Last process exit code
        Public Property Reason As String            ' Human reason for state change
        Public Property Version As String           ' Installed version
        Public Property AvailableVersion As String  ' Latest version if update pending
        Public Property UptimeFormatted As String   ' Human readable uptime, e.g. "5h 12m"
        Public Property Timestamp As DateTime
    End Class

    Public Class NotificationResult
        Public Property Success As Boolean
        Public Property ErrorMessage As String      ' Populated on failure
        ' Platform-specific message ID for later reference (edits, replies)
        Public Property PlatformMessageId As String

        Public Shared Function Ok(Optional platformMessageId As String = "") As NotificationResult
            Return New NotificationResult With {
                .Success = True,
                .PlatformMessageId = platformMessageId
            }
        End Function

        Public Shared Function Fail(errorMessage As String) As NotificationResult
            Return New NotificationResult With {
                .Success = False,
                .ErrorMessage = errorMessage
            }
        End Function
    End Class


    ' ------------------------------------------------------------
    '  Notification routing
    '  Plugins declare named channels/routes in their config.
    '  Each instance subscription maps event types to route names.
    '  The plugin resolves route names to platform destinations
    '  (Discord channel IDs, Telegram chat IDs, webhook URLs etc).
    ' ------------------------------------------------------------

    ' A subscription binds a set of event types to a named route
    ' for one specific instance or installation.
    Public Class NotificationSubscription
        Public Property SubscriptionId As String    ' GUID
        Public Property PluginId As String
        Public Property Scope As RuleScope
        Public Property TargetId As String          ' InstanceId or InstallationId
        Public Property EventTypes As List(Of NotificationEventType)
        Public Property RouteName As String         ' Plugin-defined e.g. "status", "admin"
        Public Property IsEnabled As Boolean = True
    End Class

    ' The set of event types a subscription can watch.
    ' These are fired by the engine independently of rules -
    ' they represent system-level transitions worth notifying.
    ' Rule-fired notifications use NotifyAction and bypass this enum.
    Public Enum NotificationEventType
        ' Instance lifecycle
        InstanceStarted
        InstanceStopped         ' Intentional
        InstanceCrashed         ' Unintentional exit
        InstanceStartFailed
        InstanceCrashLoopHalted
        InstanceRestarting

        ' Players
        PlayerJoined
        PlayerLeft
        PlayerCountChanged      ' Useful for low-population alerts

        ' Updates
        UpdateAvailable
        UpdateStarted
        UpdateCompleted
        UpdateFailed

        ' Installation
        InstallationLockAcquired
        InstallationLockReleased

        ' RCON
        RconConnected
        RconDisconnected
        RconUnavailable

        ' Install process
        InstallPromptRequired   ' Steam Guard etc - human input needed
    End Enum


    ' ------------------------------------------------------------
    '  Inbound: remote commands
    '  Plugins declare what commands their platform users can
    '  issue. The manager validates, permission-checks, and
    '  executes the mapped action. The plugin handles the
    '  platform-side UX (slash command registration, button
    '  interactions, inline keyboards, etc).
    ' ------------------------------------------------------------

    ' Describes a command the plugin exposes to remote users.
    Public Class RemoteCommandDescriptor
        ' Stable command key. Used in permission mappings.
        ' e.g. "status", "restart", "playerlist", "rcon"
        Public Property CommandKey As String

        ' Human label shown in the manager's permissions UI
        Public Property DisplayName As String

        ' What this command does - shown in UI and help text
        Public Property Description As String

        ' Which manager permission level is required to execute.
        ' The plugin config maps platform roles/users to these levels.
        Public Property RequiredPermission As CommandPermission

        ' Whether this command takes free-form arguments
        ' (e.g. an RCON pass-through command needs the command text)
        Public Property AcceptsArguments As Boolean = False

        ' If True, the command targets a specific instance and the
        ' plugin must resolve which instance the user means.
        ' If False, the command is global (e.g. "list all servers").
        Public Property IsInstanceScoped As Boolean = True

        ' Which actions this command can trigger in the manager.
        ' Informational - used to build the permissions UI description.
        Public Property MapsToActions As List(Of String)
    End Class

    ' Permission levels for remote commands.
    ' The plugin config maps platform-specific identities
    ' (Discord role IDs, Telegram user IDs, etc) to these levels.
    ' The manager enforces the level; the plugin enforces the mapping.
    Public Enum CommandPermission
        Everyone        ' Anyone can use · no auth required
        Viewer          ' Can see status · cannot change state
        ServerOperator  ' Can start/stop/restart · cannot update or configure
        Admin           ' Full access including update and RCON pass-through
    End Enum

    ' An inbound command received from a remote user via the plugin.
    ' Passed to IRemoteCommandHandler by the plugin when a user issues a command.
    Public Class InboundCommand
        ' Matches RemoteCommandDescriptor.CommandKey
        Public Property CommandKey As String

        ' Free-form arguments if AcceptsArguments = True
        ' e.g. for an RCON pass-through: {"say Hello world"}
        Public Property Arguments As IReadOnlyList(Of String)

        ' Which instance the command targets.
        ' The plugin is responsible for resolving this from whatever
        ' the user said (instance name, number in a list, etc).
        ' Empty if IsInstanceScoped = False.
        Public Property TargetInstanceId As String

        ' Identity of the issuing user on the platform.
        ' Opaque to the manager - used only for audit logging.
        ' e.g. Discord: "Username#1234 (ID: 123456789)"
        Public Property IssuedBy As String

        ' The permission level the plugin has resolved for this user.
        ' The manager re-validates this against RequiredPermission.
        Public Property ResolvedPermission As CommandPermission

        ' Platform-specific context the plugin may need to
        ' send a response (Discord interaction token, Telegram
        ' message ID, etc). Opaque to the manager.
        Public Property PlatformContext As Object
    End Class

    ' Result of executing an inbound command.
    ' The manager returns this to the plugin, which formats
    ' and delivers it to the platform user.
    Public Class CommandResult
        Public Property Success As Boolean
        Public Property Message As String           ' Plain-text summary
        ' Structured data the plugin can use to build rich responses.
        ' e.g. player list → Discord embed with fields per player
        Public Property Payload As CommandPayload
        Public Property DenialReason As CommandDenialReason

        Public Shared Function Ok(message As String,
                                  Optional payload As CommandPayload = Nothing) As CommandResult
            Return New CommandResult With {
                .Success = True,
                .Message = message,
                .Payload = payload
            }
        End Function

        Public Shared Function Denied(reason As CommandDenialReason,
                                      message As String) As CommandResult
            Return New CommandResult With {
                .Success = False,
                .DenialReason = reason,
                .Message = message
            }
        End Function

        Public Shared Function Fail(message As String) As CommandResult
            Return New CommandResult With {
                .Success = False,
                .DenialReason = CommandDenialReason.ExecutionError,
                .Message = message
            }
        End Function
    End Class

    Public Enum CommandDenialReason
        None                ' Not denied
        InsufficientPermission
        InstanceNotFound
        InstanceNotRunning  ' Command requires Running state
        InstallationLocked
        ExecutionError      ' Manager-side error during execution
    End Enum

    ' Structured payload for rich platform responses.
    ' Plugin uses what it needs; all properties optional.
    Public Class CommandPayload
        ' For status/info commands
        Public Property InstanceName As String
        Public Property State As String
        Public Property PlayerCount As Integer
        Public Property Players As IReadOnlyList(Of PlayerInfo)
        Public Property UptimeSeconds As Long
        Public Property Version As String
        Public Property RconState As String
        Public Property NodeName As String

        ' For list commands (e.g. "list all servers")
        Public Property InstanceSummaries As IReadOnlyList(Of InstanceSummary)
    End Class

    Public Class InstanceSummary
        Public Property InstanceId As String
        Public Property DisplayName As String
        Public Property GameDisplayName As String
        Public Property State As String
        Public Property PlayerCount As Integer
        Public Property NodeName As String
    End Class


    ' ------------------------------------------------------------
    '  Command handler
    '  Provided to the plugin during InitialiseAsync.
    '  The plugin calls this when a remote user issues a command.
    '  The manager validates permission, routes to the correct
    '  instance, executes the action, and returns the result.
    ' ------------------------------------------------------------

    Public Interface IRemoteCommandHandler

        ' Submit an inbound command for execution.
        ' The manager validates, executes, and returns the result.
        ' The plugin then delivers the result to the platform user.
        Function HandleAsync(command As InboundCommand,
                             cancellation As CancellationToken) As Task(Of CommandResult)

        ' Convenience: get a snapshot of all instances the user
        ' might want to target. Used to build selection menus.
        Function GetInstanceListAsync(permission As CommandPermission,
                                      cancellation As CancellationToken) As Task(Of IReadOnlyList(Of InstanceSummary))

    End Interface

End Namespace
