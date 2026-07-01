Imports System
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
'       engine (via NotifyAction) and deliver it somewhere —
'       Discord, Telegram, a webhook, email, etc.
'    2. Inbound: declare commands that remote users can issue
'       and handle them when they arrive. The plugin owns all
'       platform-specific auth and routing (Discord roles,
'       Telegram chat IDs, etc). The manager only sees
'       InboundCommand and CommandResult.
'
'  The manager never imports Discord, Telegram, or any other
'  platform SDK. All platform-specific code lives in the plugin.
' ============================================================

Namespace GSM.Notification

    ' ============================================================
    '  Enums
    ' ============================================================

    ''' <summary>
    ''' What kind of event triggered the notification.
    ''' </summary>
    Public Enum NotificationEventType
        InstanceStarted
        InstanceStopped
        InstanceCrashed
        CrashLoopDetected
        UpdateAvailable
        UpdateStarted
        UpdateCompleted
        UpdateFailed
        PlayerJoined
        PlayerLeft
        AutomationRuleFired
        AutomationRuleCompleted
        AutomationRuleFailed
        NodeOnline
        NodeOffline
        Custom
    End Enum

    ''' <summary>
    ''' Permission level required to execute a remote command.
    ''' ServerOperator avoids the VB reserved keyword "Operator".
    ''' </summary>
    Public Enum CommandPermission
        ''' <summary>Anyone can execute this command.</summary>
        Everyone
        ''' <summary>Requires operator role (Discord role, Telegram admin, etc).</summary>
        ServerOperator
        ''' <summary>Requires administrator role.</summary>
        Administrator
    End Enum

    ' ============================================================
    '  INotificationPlugin
    ' ============================================================

    ''' <summary>
    ''' Core contract for notification/remote command plugins.
    ''' </summary>
    Public Interface INotificationPlugin

        ''' <summary>
        ''' Unique identifier for this notification plugin.
        ''' e.g. "discord", "telegram", "webhook"
        ''' </summary>
        ReadOnly Property PluginId As String

        ''' <summary>
        ''' Human-readable name for UI display.
        ''' </summary>
        ReadOnly Property DisplayName As String

        ' ---- Lifecycle ----

        ''' <summary>
        ''' Initialise the plugin with its configuration and a
        ''' reference to the command handler for inbound commands.
        ''' </summary>
        Function InitialiseAsync(config As Dictionary(Of String, String),
                                 handler As IRemoteCommandHandler,
                                 cancellation As CancellationToken) As Task

        ''' <summary>
        ''' Shut down the plugin cleanly (disconnect bots, etc).
        ''' </summary>
        Function ShutdownAsync(cancellation As CancellationToken) As Task

        ' ---- Outbound ----

        ''' <summary>
        ''' Send a notification to the configured destination.
        ''' </summary>
        Function SendNotificationAsync(context As NotificationContext,
                                       cancellation As CancellationToken) As Task(Of Boolean)

        ' ---- Inbound ----

        ''' <summary>
        ''' Returns descriptors for all commands this plugin supports.
        ''' </summary>
        Function GetSupportedCommands() As IReadOnlyList(Of RemoteCommandDescriptor)

        ' ---- Config ----

        ''' <summary>
        ''' Returns the configuration schema for this plugin
        ''' (API keys, webhook URLs, channel IDs, etc).
        ''' </summary>
        Function GetConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)
    End Interface

    ' ============================================================
    '  IDestinationTargetingPlugin — opt-in extension for plugins
    '  that can dispatch a custom message directly to ONE specific
    '  destination (bypassing event-type / scope filtering).
    '
    '  Phase 4b-1.5: lets the automation engine's NotifyAction send
    '  a literal message to a user-picked destination from a rule,
    '  distinct from the event-driven broadcast path which fans
    '  out to every destination matching an event's scope.
    '
    '  Plugins that don't implement this interface are still valid
    '  notification plugins — they just don't support custom
    '  message dispatch from automation actions. Currently only
    '  DiscordWebhookPlugin implements it; future transports
    '  (Slack, Telegram) will add their own implementations.
    '
    '  The plugin owns the lookup: NotificationService asks each
    '  registered plugin in turn whether it can target the given
    '  destinationId. The first one to return True/successfully
    '  dispatch wins. This avoids NotificationService needing to
    '  know which transports own which destinations.
    ' ============================================================

    ''' <summary>
    ''' Optional capability interface for notification plugins
    ''' that support direct destination targeting.
    ''' </summary>
    Public Interface IDestinationTargetingPlugin

        ''' <summary>
        ''' True if this plugin owns the given destination ID
        ''' (i.e. its TransportKind matches and the destination
        ''' is in this plugin's cache). Cheap synchronous check
        ''' so NotificationService can skip plugins that don't
        ''' own the destination without an async round trip.
        ''' </summary>
        Function OwnsDestination(destinationId As String) As Boolean

        ''' <summary>
        ''' Send a custom message to one specific destination.
        ''' Returns True on enqueue success, False on lookup
        ''' failure or transport error. Does NOT apply event-
        ''' type or scope filtering — the caller has explicitly
        ''' chosen this destination.
        '''
        ''' Token substitution should already be done by the
        ''' caller; the plugin treats the message string as
        ''' literal final text.
        ''' </summary>
        Function SendCustomToDestinationAsync(
            destinationId As String,
            message As String,
            severity As NotificationSeverity,
            tokens As NotificationTokens,
            cancellation As CancellationToken) As Task(Of Boolean)

    End Interface

    ' ============================================================
    '  IRemoteCommandHandler — manager implements this
    ' ============================================================

    ''' <summary>
    ''' The manager provides this to notification plugins so they
    ''' can route inbound commands (e.g. Discord "!restart lobby")
    ''' back to the manager for execution.
    ''' </summary>
    Public Interface IRemoteCommandHandler

        ''' <summary>
        ''' Handle an inbound command from a remote user.
        ''' </summary>
        Function HandleCommandAsync(command As InboundCommand,
                                    cancellation As CancellationToken) As Task(Of CommandResult)

        ''' <summary>
        ''' Returns the list of all registered remote commands
        ''' across all notification plugins, for help/discovery.
        ''' </summary>
        Function GetAvailableCommands() As IReadOnlyList(Of RemoteCommandDescriptor)
    End Interface

    ' ============================================================
    '  Notification context and tokens
    ' ============================================================

    ''' <summary>
    ''' Payload passed to notification plugins when sending outbound
    ''' notifications. Contains all information the plugin needs to
    ''' format and deliver the notification.
    ''' </summary>
    Public Class NotificationContext
        Public Property EventType As NotificationEventType
        Public Property Severity As NotificationSeverity
        Public Property Title As String
        Public Property Message As String
        Public Property Tokens As NotificationTokens
        Public Property Metadata As Dictionary(Of String, String)
        Public Property Timestamp As DateTime

        ''' <summary>
        ''' Scope fan-out (Phase 5n): every instance ID this event
        ''' pertains to. One entry for instance-level events; for
        ''' installation-level events (e.g. updates) it is every
        ''' instance under the installation, so an instance- or
        ''' set-scoped destination still matches. Empty when neither
        ''' applies. Used by notification scope matching, not templates.
        ''' </summary>
        Public Property ScopeInstanceIds As List(Of String)

        ''' <summary>
        ''' Scope fan-out (Phase 5n): the distinct non-empty instance
        ''' set tags of the instances in ScopeInstanceIds. Matched
        ''' case-sensitively against the instance-set scope filter.
        ''' </summary>
        Public Property ScopeInstanceSetTags As List(Of String)
    End Class

    ''' <summary>
    ''' Token values that can be substituted into notification
    ''' templates. Plugins use these to format messages with
    ''' contextual information.
    ''' </summary>
    Public Class NotificationTokens
        Public Property InstanceId As String
        Public Property InstanceName As String
        Public Property InstallationId As String
        Public Property InstallationName As String
        Public Property GameId As String
        Public Property GameName As String
        Public Property NodeId As String
        Public Property NodeName As String
        Public Property PlayerName As String
        Public Property PlayerCount As Integer?
        Public Property MaxPlayers As Integer?
        Public Property RuleName As String
        Public Property ErrorMessage As String

        ''' <summary>
        ''' The Steam buildid of the installation, when known —
        ''' extracted from InstallationEntity.InstalledVersion's
        ''' " build <id>" suffix (written by InstallationManager
        ''' after a version check). Empty string if the stamp
        ''' doesn't carry one yet.
        ''' </summary>
        Public Property BuildId As String

        ''' <summary>
        ''' Current tile ID for the instance at the time the event
        ''' fired (game-specific — Last Oasis uses this for tiles;
        ''' other games leave it empty). Token: {TileId}.
        ''' </summary>
        Public Property TileId As String

        ''' <summary>
        ''' Human-readable tile name at the time the event fired.
        ''' For Last Oasis this is the tile's display name from
        ''' the MapPath. Token: {TileName}.
        ''' </summary>
        Public Property TileName As String

        ''' <summary>
        ''' The instance's free-form set tag
        ''' (InstanceEntity.InstanceSetTag) at the time the event
        ''' fired, consumed by notification scope's instance-set
        ''' dimension. Empty when the event isn't tied to a specific
        ''' instance (e.g. installation-level events). Token:
        ''' {InstanceSetTag}.
        ''' </summary>
        Public Property InstanceSetTag As String

        Public Property CustomTokens As Dictionary(Of String, String)
    End Class

    ' ============================================================
    '  Inbound commands
    ' ============================================================

    ''' <summary>
    ''' An inbound command received from a remote user via a
    ''' notification plugin (e.g. Discord bot command).
    ''' </summary>
    Public Class InboundCommand
        ''' <summary>Plugin that received this command.</summary>
        Public Property SourcePluginId As String

        ''' <summary>The command name (e.g. "restart", "status").</summary>
        Public Property CommandName As String

        ''' <summary>Arguments passed with the command.</summary>
        Public Property Arguments As List(Of String)

        ''' <summary>
        ''' Platform-specific user identifier (Discord user ID,
        ''' Telegram chat ID, etc).
        ''' </summary>
        Public Property RemoteUserId As String

        ''' <summary>Display name of the remote user.</summary>
        Public Property RemoteUserName As String

        ''' <summary>
        ''' Permission level of the remote user, as determined
        ''' by the notification plugin (Discord roles, etc).
        ''' </summary>
        Public Property UserPermission As CommandPermission
    End Class

    ''' <summary>
    ''' Descriptor for a remote command — what it's called, what
    ''' it does, and who can run it.
    ''' </summary>
    Public Class RemoteCommandDescriptor
        Public Property CommandName As String
        Public Property Description As String
        Public Property RequiredPermission As CommandPermission
        Public Property ParameterDescriptions As List(Of String)
    End Class

    ''' <summary>
    ''' Result of executing a remote command.
    ''' </summary>
    Public Class CommandResult
        Public Property Success As Boolean
        Public Property ResponseMessage As String
        Public Property ErrorMessage As String

        Public Shared Function Ok(message As String) As CommandResult
            Return New CommandResult With {
                .Success = True,
                .ResponseMessage = message
            }
        End Function

        Public Shared Function Fail(errorMsg As String) As CommandResult
            Return New CommandResult With {
                .Success = False,
                .ErrorMessage = errorMsg
            }
        End Function
    End Class

End Namespace