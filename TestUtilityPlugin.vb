' <plugin id="testutility" name="Test Utility Plugin" version="1.0.0" author="powergsm" requiresContracts="2" requires="events">
' <RequiresContracts: 2>
Imports System
Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports GSM.Plugin
Imports GSM.Utility

' ============================================================
'  TestUtilityPlugin — Phase 7 dev/test logger
'
'  Subscribes to every UtilityEventKind and logs ALL event
'  fields per event, so one glance at the Manager log verifies
'  the 7-4a tap end-to-end: SessionIdentity (realm:tile on LO),
'  raw PlayerName vs resolved CharacterName, CharacterId,
'  PlatformUserId, Platform, and the ServerState/Message pair
'  on tile bind/unbind. Empty fields render as "-" so a gap is
'  visible rather than silently blank.
'
'  ⚠ RELEASE GATE (0.4.0): dev/test artifact. It lives in
'  GSM.PluginsSource ONLY so builds auto-deploy it to Plugins\
'  during Phase 7 — remove before tagging 0.4.0 or it ships in
'  the official catalog (see Phase7_Plan.md).
' ============================================================

Public Class TestUtilityPlugin
    Implements IUtilityPlugin

    Public ReadOnly Property PluginId As String = "testutility" Implements IUtilityPlugin.PluginId

    Public ReadOnly Property DisplayName As String = "Test Utility Plugin" Implements IUtilityPlugin.DisplayName

    Public ReadOnly Property SubscribedEvents As IReadOnlyList(Of UtilityEventKind) _
        Implements IUtilityPlugin.SubscribedEvents
        Get
            Return New UtilityEventKind() {
                UtilityEventKind.PlayerJoin,
                UtilityEventKind.PlayerLeave,
                UtilityEventKind.ChatMessage,
                UtilityEventKind.ServerStateChange,
                UtilityEventKind.InstanceStarted,
                UtilityEventKind.InstanceStopped,
                UtilityEventKind.InstanceCrashed
            }
        End Get
    End Property

    Public Function GetConfigSchema() As List(Of ConfigFieldDescriptor) Implements IUtilityPlugin.GetConfigSchema
        Return New List(Of ConfigFieldDescriptor)
    End Function

    Public Function InitializeAsync(context As IUtilityContext) As Task Implements IUtilityPlugin.InitializeAsync
        context.LogInformation("TestUtilityPlugin initialised (7-4a field-surface logger)")
        Return Task.CompletedTask
    End Function

    Public Function HandleEventAsync(evt As UtilityEvent, context As IUtilityContext) As Task _
        Implements IUtilityPlugin.HandleEventAsync
        context.LogInformation(
            $"[{evt.Kind}] instance={F(evt.InstanceDisplayName)} ({F(evt.InstanceId)}) " &
            $"game={F(evt.GameId)} node={F(evt.NodeId)} install={F(evt.InstallationId)} " &
            $"session={F(evt.SessionIdentity)} " &
            $"player={F(evt.PlayerName)} character={F(evt.CharacterName)} " &
            $"cid={F(evt.CharacterId)} pid={F(evt.PlatformUserId)} platform={F(evt.Platform)} " &
            $"state={F(evt.ServerState)} msg={F(evt.Message)} ts={evt.TimestampUtc:o}")
        Return Task.CompletedTask
    End Function

    Public Function ShutdownAsync() As Task Implements IUtilityPlugin.ShutdownAsync
        Return Task.CompletedTask
    End Function

    ''' <summary>Render empty/missing fields as "-" so identity
    ''' gaps are visible in the log line rather than blank.</summary>
    Private Shared Function F(value As String) As String
        Return If(String.IsNullOrEmpty(value), "-", value)
    End Function

End Class
