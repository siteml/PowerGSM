Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Channels
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data
Imports GSM.Notification
Imports GSM.Automation
Imports GSM.Utility

' ============================================================
'  UtilityPluginHost — Phase 7-2
'
'  Hosts utility plugins (IUtilityPlugin): lifecycle + queued,
'  isolated event dispatch.
'
'  Lifecycle: subscribes to PluginRegistry.Reloaded; on every
'  reload (including the startup load) it shuts down the previous
'  plugin set and initialises the new one. Initialize/Shutdown
'  exceptions are caught and logged — a broken plugin never
'  affects the Manager or other plugins.
'
'  Events (Phase 7-4a): instance lifecycle (InstanceStarted/
'  InstanceStopped/InstanceCrashed) still arrives via
'  NotificationEmitter.Emitted. PlayerJoin/PlayerLeave,
'  ChatMessage, and ServerStateChange are published DIRECTLY by
'  InstanceManager (PublishPlayerEvent / PublishChatMessage /
'  PublishServerStateChange) — the emitter fires before identity
'  resolution completes and carries only a decorated label, while
'  the persist/mirror/tile paths hold the fully-resolved identity
'  (CharacterId, PlatformUserId, Platform, CharacterName) and the
'  SessionIdentity. ServerStateChange is tile bind/unbind only —
'  the only server-state signal the Manager's parsers observe;
'  Node-side MatchState polling waits for a real consumer.
'
'  Dispatch: per-plugin bounded Channel (drop-oldest on overflow
'  with a warning) drained by a background task, so a slow or
'  broken plugin can never block the Manager's event paths.
'  HandleEventAsync exceptions are counted; after
'  MaxConsecutiveFailures the plugin's delivery is SUSPENDED
'  until the next reload (surfaced in Plugin Status).
' ============================================================

Namespace GSM.Manager.Core

    Public Class UtilityPluginHost

        Private Const QueueCapacity As Integer = 256
        Private Const MaxConsecutiveFailures As Integer = 5

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger(Of UtilityPluginHost)
        Private ReadOnly _registry As PluginRegistry

        Private ReadOnly _hosted As New ConcurrentDictionary(Of String, HostedPlugin)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _restartLock As New SemaphoreSlim(1, 1)

        Private Class HostedPlugin
            Public Property Plugin As IUtilityPlugin
            Public Property Context As UtilityContextImpl
            Public Property Queue As Channel(Of UtilityEvent)
            Public Property DrainTask As Task
            Public Property Cts As CancellationTokenSource
            Public Property ConsecutiveFailures As Integer
            Public Property Suspended As Boolean
        End Class

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of UtilityPluginHost),
                       registry As PluginRegistry,
                       emitter As NotificationEmitter)
            _serviceProvider = serviceProvider
            _logger = logger
            _registry = registry

            ' Restart hosted plugins on every registry reload. The
            ' handler fires inside ReloadAll's lock on the caller's
            ' thread, so the actual restart runs on a background task.
            AddHandler registry.Reloaded, Sub(s, e) Task.Run(AddressOf RestartPluginsAsync)

            ' Tap the notification emitter — the same enriched event
            ' stream NotificationService dispatches.
            AddHandler emitter.Emitted, AddressOf OnNotificationEmitted
        End Sub

        ''' <summary>True when a plugin's event delivery was suspended
        ''' after repeated failures (resets on the next reload).</summary>
        Public Function IsSuspended(pluginId As String) As Boolean
            Dim hp As HostedPlugin = Nothing
            If _hosted.TryGetValue(pluginId, hp) Then Return hp.Suspended
            Return False
        End Function

        ' ============================================================
        '  Lifecycle
        ' ============================================================

        Private Async Function RestartPluginsAsync() As Task
            Await _restartLock.WaitAsync()
            Try
                Await ShutdownAllAsync()

                For Each utilityPlugin In _registry.GetUtilityPlugins()
                    Dim declaredCaps = _registry.GetManifest(utilityPlugin.PluginId)?.Requires
                    Dim hp As New HostedPlugin With {
                        .Plugin = utilityPlugin,
                        .Context = New UtilityContextImpl(_serviceProvider, _logger, utilityPlugin.PluginId, declaredCaps),
                        .Queue = Channel.CreateBounded(Of UtilityEvent)(
                            New BoundedChannelOptions(QueueCapacity) With {
                                .FullMode = BoundedChannelFullMode.DropOldest,
                                .SingleReader = True
                            }),
                        .Cts = New CancellationTokenSource()
                    }

                    Try
                        Await utilityPlugin.InitializeAsync(hp.Context)
                        _logger.LogInformation("Utility plugin {Id} initialised", utilityPlugin.PluginId)
                    Catch ex As Exception
                        _logger.LogError(ex, "Utility plugin {Id} failed to initialise — events will still be delivered unless it keeps failing",
                                         utilityPlugin.PluginId)
                    End Try

                    hp.DrainTask = Task.Run(Function() DrainLoopAsync(hp))
                    _hosted(utilityPlugin.PluginId) = hp
                Next
            Finally
                _restartLock.Release()
            End Try
        End Function

        Private Async Function ShutdownAllAsync() As Task
            For Each kvp In _hosted.ToList()
                Dim hp = kvp.Value
                Try
                    hp.Cts.Cancel()
                    hp.Queue.Writer.TryComplete()
                Catch
                End Try
                Try
                    ' Bounded wait — a hung ShutdownAsync must not stall
                    ' the reload.
                    Await Task.WhenAny(hp.Plugin.ShutdownAsync(), Task.Delay(5000))
                Catch ex As Exception
                    _logger.LogWarning(ex, "Utility plugin {Id} threw during shutdown", kvp.Key)
                End Try
            Next
            _hosted.Clear()
        End Function

        ' ============================================================
        '  Event intake + dispatch
        ' ============================================================

        Private Sub OnNotificationEmitted(sender As Object, args As NotificationEmittedEventArgs)
            Try
                Dim ctx = args.Context
                If ctx Is Nothing OrElse ctx.Tokens Is Nothing Then Return

                Dim kind As UtilityEventKind
                Select Case ctx.EventType
                    ' PlayerJoined/PlayerLeft are deliberately NOT
                    ' mapped here (Phase 7-4a): the emitter fires
                    ' before identity resolution completes and only
                    ' carries a decorated display label.
                    ' InstanceManager publishes identity-rich
                    ' PlayerJoin/PlayerLeave via PublishPlayerEvent.
                    Case NotificationEventType.InstanceStarted : kind = UtilityEventKind.InstanceStarted
                    Case NotificationEventType.InstanceStopped : kind = UtilityEventKind.InstanceStopped
                    Case NotificationEventType.InstanceCrashed,
                         NotificationEventType.CrashLoopDetected : kind = UtilityEventKind.InstanceCrashed
                    Case Else
                        Return ' Not a utility-relevant event.
                End Select

                Dim evt As New UtilityEvent With {
                    .Kind = kind,
                    .TimestampUtc = ctx.Timestamp,
                    .NodeId = ctx.Tokens.NodeId,
                    .InstallationId = ctx.Tokens.InstallationId,
                    .InstanceId = ctx.Tokens.InstanceId,
                    .InstanceDisplayName = ctx.Tokens.InstanceName,
                    .GameId = ctx.Tokens.GameId,
                    .PlayerName = ctx.Tokens.PlayerName,
                    .Message = ctx.Message
                }
                Publish(evt)
            Catch ex As Exception
                _logger.LogWarning(ex, "UtilityPluginHost failed to map an emitted event")
            End Try
        End Sub

        ''' <summary>Enqueue an event to every non-suspended plugin
        ''' subscribed to its kind. Never blocks, never throws.</summary>
        Public Sub Publish(evt As UtilityEvent)
            If evt Is Nothing Then Return
            For Each kvp In _hosted
                Dim hp = kvp.Value
                If hp.Suspended Then Continue For
                Dim subscribed = hp.Plugin.SubscribedEvents
                If subscribed Is Nothing OrElse Not subscribed.Contains(evt.Kind) Then Continue For
                If Not hp.Queue.Writer.TryWrite(evt) Then
                    _logger.LogWarning("Utility plugin {Id} event queue overflowed — oldest event dropped",
                                       kvp.Key)
                End If
            Next
        End Sub

        ' ============================================================
        '  Phase 7-4a — direct publishers (identity-rich events)
        '
        '  InstanceManager calls these from the points where the
        '  fully-resolved data actually lives: the tail of
        '  PersistPlayerObservationAsync (join/leave — after the
        '  /players + resolver + leave-inheritance cascade), the
        '  chat mirror (per newly-mirrored row, cursor-deduped),
        '  and the tile-load/unload handlers. Each publisher
        '  early-exits via HasSubscribers so the DB where-lookup
        '  costs nothing when no plugin cares, and never throws.
        ' ============================================================

        ''' <summary>True when at least one non-suspended hosted
        ''' plugin subscribes to the kind. Public so call sites with
        ''' per-event preparation cost (e.g. the chat mirror's
        ''' resolver consult per message) can skip it wholesale.</summary>
        Public Function HasSubscribers(kind As UtilityEventKind) As Boolean
            For Each kvp In _hosted
                Dim hp = kvp.Value
                If hp.Suspended Then Continue For
                Dim subscribed = hp.Plugin.SubscribedEvents
                If subscribed IsNot Nothing AndAlso subscribed.Contains(kind) Then Return True
            Next
            Return False
        End Function

        Private Class EventLocation
            Public Property NodeId As String
            Public Property InstallationId As String
            Public Property InstanceDisplayName As String
            Public Property GameId As String
        End Class

        ''' <summary>Fills the where-fields (node / installation /
        ''' instance display name / game) for a published event — the
        ''' same data BuildContextAsync resolves for emitter events.
        ''' Best-effort: a lookup failure yields empty fields, never
        ''' a lost event.</summary>
        Private Function LookupLocation(instanceId As String) As EventLocation
            Dim loc As New EventLocation()
            If String.IsNullOrEmpty(instanceId) Then Return loc
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim inst = db.Instances.Find(instanceId)
                    If inst IsNot Nothing Then
                        loc.InstanceDisplayName = inst.DisplayName
                        loc.GameId = inst.GameId
                        Dim install = db.Installations.Find(inst.InstallationId)
                        If install IsNot Nothing Then
                            loc.InstallationId = install.InstallationId
                            loc.NodeId = install.NodeId
                        End If
                    End If
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex, "UtilityPluginHost location lookup failed for {Id}", instanceId)
            End Try
            Return loc
        End Function

        ''' <summary>Publish an identity-rich PlayerJoin/PlayerLeave.
        ''' playerName is the RAW parser name (persona); characterName
        ''' is the resolved in-game name (may be Nothing).</summary>
        Public Sub PublishPlayerEvent(instanceId As String,
                                      isJoin As Boolean,
                                      playerName As String,
                                      characterId As String,
                                      platformUserId As String,
                                      platform As String,
                                      characterName As String,
                                      sessionIdentity As String,
                                      timestampUtc As DateTime)
            Try
                Dim kind = If(isJoin, UtilityEventKind.PlayerJoin, UtilityEventKind.PlayerLeave)
                If Not HasSubscribers(kind) Then Return
                Dim loc = LookupLocation(instanceId)
                Publish(New UtilityEvent With {
                    .Kind = kind,
                    .TimestampUtc = timestampUtc,
                    .NodeId = loc.NodeId,
                    .InstallationId = loc.InstallationId,
                    .InstanceId = instanceId,
                    .InstanceDisplayName = loc.InstanceDisplayName,
                    .GameId = loc.GameId,
                    .SessionIdentity = sessionIdentity,
                    .PlayerName = playerName,
                    .Platform = platform,
                    .PlatformUserId = platformUserId,
                    .CharacterId = characterId,
                    .CharacterName = characterName
                })
            Catch ex As Exception
                _logger.LogWarning(ex, "PublishPlayerEvent failed for {Id}", instanceId)
            End Try
        End Sub

        ''' <summary>Publish a ChatMessage. The speaker string is the
        ''' in-game display name (chat is the authoritative name
        ''' surface), so it rides PlayerName AND CharacterName.</summary>
        Public Sub PublishChatMessage(instanceId As String,
                                      playerName As String,
                                      characterId As String,
                                      platformUserId As String,
                                      platform As String,
                                      characterName As String,
                                      message As String,
                                      sessionIdentity As String,
                                      timestampUtc As DateTime)
            Try
                If Not HasSubscribers(UtilityEventKind.ChatMessage) Then Return
                Dim loc = LookupLocation(instanceId)
                Publish(New UtilityEvent With {
                    .Kind = UtilityEventKind.ChatMessage,
                    .TimestampUtc = timestampUtc,
                    .NodeId = loc.NodeId,
                    .InstallationId = loc.InstallationId,
                    .InstanceId = instanceId,
                    .InstanceDisplayName = loc.InstanceDisplayName,
                    .GameId = loc.GameId,
                    .SessionIdentity = sessionIdentity,
                    .PlayerName = playerName,
                    .Platform = platform,
                    .PlatformUserId = platformUserId,
                    .CharacterId = characterId,
                    .CharacterName = characterName,
                    .Message = message
                })
            Catch ex As Exception
                _logger.LogWarning(ex, "PublishChatMessage failed for {Id}", instanceId)
            End Try
        End Sub

        ''' <summary>Publish a ServerStateChange. serverState is the
        ''' transition name ("TileLoaded"/"TileUnloaded"); detail
        ''' (rides Message) carries the tile name when known.</summary>
        Public Sub PublishServerStateChange(instanceId As String,
                                            serverState As String,
                                            detail As String,
                                            sessionIdentity As String,
                                            timestampUtc As DateTime)
            Try
                If Not HasSubscribers(UtilityEventKind.ServerStateChange) Then Return
                Dim loc = LookupLocation(instanceId)
                Publish(New UtilityEvent With {
                    .Kind = UtilityEventKind.ServerStateChange,
                    .TimestampUtc = timestampUtc,
                    .NodeId = loc.NodeId,
                    .InstallationId = loc.InstallationId,
                    .InstanceId = instanceId,
                    .InstanceDisplayName = loc.InstanceDisplayName,
                    .GameId = loc.GameId,
                    .SessionIdentity = sessionIdentity,
                    .ServerState = serverState,
                    .Message = detail
                })
            Catch ex As Exception
                _logger.LogWarning(ex, "PublishServerStateChange failed for {Id}", instanceId)
            End Try
        End Sub

        ''' <summary>True when a loaded, non-suspended utility plugin
        ''' claims it can validate the given session key — drives the
        ''' Web Sessions tab's Validate button enablement.</summary>
        Public Function HasValidatorFor(sessionKey As String) As Boolean
            Return FindValidator(sessionKey) IsNot Nothing
        End Function

        ''' <summary>Routes a Web Sessions UI validation request to the
        ''' first plugin claiming the key. Never throws; a missing
        ''' validator, missing session, or plugin exception comes back
        ''' as a Failed result with detail.</summary>
        Public Async Function ValidateSessionAsync(sessionKey As String) As Task(Of WebSessionValidationResult)
            Dim hp = FindValidator(sessionKey)
            If hp Is Nothing Then
                Return New WebSessionValidationResult With {
                    .State = WebSessionValidationState.Failed,
                    .Detail = "No loaded plugin can validate this session."}
            End If

            Dim header = _serviceProvider.GetRequiredService(Of WebSessionStore)().PeekHeader(sessionKey)
            If String.IsNullOrEmpty(header) Then
                Return New WebSessionValidationResult With {
                    .State = WebSessionValidationState.Failed,
                    .Detail = "No stored session to validate."}
            End If

            Try
                Dim validator = DirectCast(hp.Plugin, IWebSessionValidator)
                Dim result = Await validator.ValidateWebSessionAsync(sessionKey, header, hp.Context)
                If result Is Nothing Then
                    Return New WebSessionValidationResult With {
                        .State = WebSessionValidationState.Failed,
                        .Detail = "The plugin returned no result."}
                End If
                Return result
            Catch ex As Exception
                _logger.LogWarning(ex, "Session validation for '{Key}' threw", sessionKey)
                Return New WebSessionValidationResult With {
                    .State = WebSessionValidationState.Failed,
                    .Detail = $"Validation threw: {ex.Message}"}
            End Try
        End Function

        Private Function FindValidator(sessionKey As String) As HostedPlugin
            If String.IsNullOrWhiteSpace(sessionKey) Then Return Nothing
            For Each kvp In _hosted
                Dim hp = kvp.Value
                If hp.Suspended Then Continue For
                Dim validator = TryCast(hp.Plugin, IWebSessionValidator)
                If validator Is Nothing Then Continue For
                Try
                    If validator.CanValidateWebSession(sessionKey) Then Return hp
                Catch
                    ' A throwing CanValidate just means "no".
                End Try
            Next
            Return Nothing
        End Function

        ''' <summary>True when any loaded, non-suspended utility plugin
        ''' can provide importable portal records — drives the Shared
        ''' Resources "Import…" entry point visibility (Phase 7-6).</summary>
        Public Function HasAnyPortalProvider() As Boolean
            For Each kvp In _hosted
                If kvp.Value.Suspended Then Continue For
                If TypeOf kvp.Value.Plugin Is IWebPortalDataProvider Then Return True
            Next
            Return False
        End Function

        ''' <summary>Discover importable records from EVERY loaded portal
        ''' provider (each obtains its own session; allowPrompt may open
        ''' a login dialog). The caller filters the flat result by
        ''' record.GameId / SharedConfigKey for the target it cares
        ''' about. Never throws — a failing provider contributes
        ''' nothing.</summary>
        Public Async Function DiscoverAllPortalRecordsAsync(allowPrompt As Boolean) As Task(Of IReadOnlyList(Of WebPortalImportRecord))
            Dim all As New List(Of WebPortalImportRecord)
            For Each kvp In _hosted
                Dim hp = kvp.Value
                If hp.Suspended Then Continue For
                Dim provider = TryCast(hp.Plugin, IWebPortalDataProvider)
                If provider Is Nothing Then Continue For
                Try
                    ' sessionKey Nothing = the provider's own default
                    ' session (lo-myrealm uses "myrealm:default").
                    Dim records = Await provider.DiscoverRecordsAsync(Nothing, allowPrompt, hp.Context)
                    If records IsNot Nothing Then all.AddRange(records)
                Catch ex As Exception
                    _logger.LogWarning(ex, "Portal discovery via {Id} threw", hp.Plugin.PluginId)
                End Try
            Next
            Return all
        End Function

        ''' <summary>Route an "add account" request to the first loaded
        ''' portal provider: it forces a fresh interactive login,
        ''' derives + stores the new account's session, and returns the
        ''' account label (Nothing on cancel/failure or when no provider
        ''' is loaded). Drives the Web Sessions form's Add-account button
        ''' (Phase 7-7). Mirrors DiscoverAllPortalRecordsAsync but targets
        ''' a SINGLE provider — adding an account is one portal's login,
        ''' not a fan-out. Never throws.</summary>
        Public Async Function AddPortalAccountAsync() As Task(Of String)
            For Each kvp In _hosted
                Dim hp = kvp.Value
                If hp.Suspended Then Continue For
                Dim provider = TryCast(hp.Plugin, IWebPortalDataProvider)
                If provider Is Nothing Then Continue For
                Try
                    Return Await provider.AddAccountAsync(hp.Context)
                Catch ex As Exception
                    _logger.LogWarning(ex, "Add portal account via {Id} threw", hp.Plugin.PluginId)
                    Return Nothing
                End Try
            Next
            Return Nothing
        End Function

        Private Async Function DrainLoopAsync(hp As HostedPlugin) As Task
            Dim reader = hp.Queue.Reader
            Try
                While Await reader.WaitToReadAsync(hp.Cts.Token)
                    Dim evt As UtilityEvent = Nothing
                    While reader.TryRead(evt)
                        If hp.Cts.Token.IsCancellationRequested Then Return
                        Try
                            Await hp.Plugin.HandleEventAsync(evt, hp.Context)
                            hp.ConsecutiveFailures = 0
                        Catch ex As Exception
                            hp.ConsecutiveFailures += 1
                            _logger.LogWarning(ex,
                                "Utility plugin {Id} threw handling {Kind} (failure {Count}/{Max})",
                                hp.Plugin.PluginId, evt.Kind, hp.ConsecutiveFailures, MaxConsecutiveFailures)
                            If hp.ConsecutiveFailures >= MaxConsecutiveFailures Then
                                hp.Suspended = True
                                _logger.LogError(
                                    "Utility plugin {Id} SUSPENDED after {Max} consecutive failures — reload plugins to reinstate it",
                                    hp.Plugin.PluginId, MaxConsecutiveFailures)
                                Return
                            End If
                        End Try
                    End While
                End While
            Catch ex As OperationCanceledException
                ' Normal shutdown path.
            Catch ex As Exception
                _logger.LogWarning(ex, "Utility plugin {Id} drain loop ended unexpectedly",
                                   hp.Plugin.PluginId)
            End Try
        End Function

    End Class

    ' ============================================================
    '  UtilityPluginConfigStore — Phase 7-3
    '
    '  Per-plugin config bag persisted as a JSON dictionary in the
    '  AppSettings key "plugins.config.{pluginId}". Shared between
    '  UtilityContextImpl (Get/SetConfigValue) and the Status tab's
    '  Configure... dialog so both read and write the same values.
    ' ============================================================

    Public Class UtilityPluginConfigStore

        Private Shared Function KeyFor(pluginId As String) As String
            Return "plugins.config." & If(pluginId, "").ToLowerInvariant()
        End Function

        Public Shared Function Load(serviceProvider As IServiceProvider, pluginId As String) As Dictionary(Of String, String)
            Try
                Using scope = serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim json = db.GetSetting(KeyFor(pluginId), "")
                    If String.IsNullOrWhiteSpace(json) Then Return New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                    Dim parsed = System.Text.Json.JsonSerializer.Deserialize(Of Dictionary(Of String, String))(json)
                    Return If(parsed, New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase))
                End Using
            Catch
                Return New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            End Try
        End Function

        Public Shared Sub Save(serviceProvider As IServiceProvider, pluginId As String, values As Dictionary(Of String, String))
            Using scope = serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                db.SetSetting(KeyFor(pluginId), System.Text.Json.JsonSerializer.Serialize(values))
                db.SaveChanges()
            End Using
        End Sub

    End Class

    ' ============================================================
    '  UtilityContextImpl — Phase 7-3 (capability-gated)
    '
    '  Logging and the read-only instance listing are always
    '  available. Everything else is gated by the capabilities the
    '  plugin's manifest declared (`requires` attribute) — an
    '  undeclared access throws InvalidOperationException naming
    '  the missing capability, so honest plugins stay honest and a
    '  plugin author immediately understands what to declare.
    '  Web-capture's dialog ships in 7-3 round 2.
    ' ============================================================

    Public Class UtilityContextImpl
        Implements IUtilityContext

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger
        Private ReadOnly _pluginId As String
        Private ReadOnly _capabilities As HashSet(Of String)

        Public Sub New(serviceProvider As IServiceProvider, logger As ILogger,
                       pluginId As String, declaredCapabilities As IEnumerable(Of String))
            _serviceProvider = serviceProvider
            _logger = logger
            _pluginId = pluginId
            _capabilities = New HashSet(Of String)(
                If(declaredCapabilities, Enumerable.Empty(Of String)()),
                StringComparer.OrdinalIgnoreCase)
        End Sub

        Private Sub Require(capability As String)
            If Not _capabilities.Contains(capability) Then
                Throw New InvalidOperationException(
                    $"Plugin '{_pluginId}' used a '{capability}' context service without declaring it. " &
                    $"Add requires=""...,{capability}"" to its <plugin> manifest.")
            End If
        End Sub

        ' --- Always available ---

        Public Sub LogInformation(message As String) Implements IUtilityContext.LogInformation
            _logger.LogInformation("[{Plugin}] {Message}", _pluginId, message)
        End Sub

        Public Sub LogWarning(message As String) Implements IUtilityContext.LogWarning
            _logger.LogWarning("[{Plugin}] {Message}", _pluginId, message)
        End Sub

        Public Sub LogError(message As String) Implements IUtilityContext.LogError
            _logger.LogError("[{Plugin}] {Message}", _pluginId, message)
        End Sub

        Public Function GetInstances() As IReadOnlyList(Of UtilityInstanceInfo) Implements IUtilityContext.GetInstances
            Dim result As New List(Of UtilityInstanceInfo)
            Try
                Dim instanceManager = _serviceProvider.GetService(Of InstanceManager)()
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    For Each inst In db.Instances.ToList()
                        Dim live = instanceManager?.GetLiveState(inst.InstanceId)
                        Dim install = db.Installations.Find(inst.InstallationId)
                        result.Add(New UtilityInstanceInfo With {
                            .InstanceId = inst.InstanceId,
                            .DisplayName = inst.DisplayName,
                            .GameId = inst.GameId,
                            .NodeId = install?.NodeId,
                            .State = If(live IsNot Nothing, live.CurrentState.ToString(), "Unknown")
                        })
                    Next
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "[{Plugin}] GetInstances failed", _pluginId)
            End Try
            Return result
        End Function

        ' --- notifications ---

        Public Async Function SendNotificationAsync(title As String, message As String) As Task(Of Boolean) Implements IUtilityContext.SendNotificationAsync
            Require(UtilityCapabilities.Notifications)
            Try
                Dim notifications = _serviceProvider.GetService(Of NotificationService)()
                If notifications Is Nothing Then Return False
                Dim context As New NotificationContext With {
                    .EventType = NotificationEventType.Custom,
                    .Severity = NotificationSeverity.Info,
                    .Title = If(String.IsNullOrEmpty(title), $"Plugin {_pluginId}", title),
                    .Message = If(message, ""),
                    .Timestamp = DateTime.UtcNow,
                    .Tokens = New NotificationTokens(),
                    .Metadata = New Dictionary(Of String, String)
                }
                Await notifications.BroadcastAsync(context, CancellationToken.None)
                Return True
            Catch ex As Exception
                _logger.LogWarning(ex, "[{Plugin}] SendNotificationAsync failed", _pluginId)
                Return False
            End Try
        End Function

        ' --- identity-read / identity-write ---

        Public Function ResolveIdentity(characterId As String) As UtilityIdentityInfo Implements IUtilityContext.ResolveIdentity
            Require(UtilityCapabilities.IdentityRead)
            If String.IsNullOrEmpty(characterId) Then Return Nothing
            Try
                Dim resolver = _serviceProvider.GetService(Of IdentityResolver)()
                If resolver Is Nothing Then Return Nothing
                ' CharacterIds are effectively globally unique per game
                ' backend, so a full-record scan with an exact match is
                ' adequate at cache sizes (thousands).
                For Each rec In resolver.GetAllRecords()
                    If String.Equals(rec.CharacterId, characterId, StringComparison.Ordinal) Then
                        Return New UtilityIdentityInfo With {
                            .GameId = rec.GameId,
                            .SessionScope = rec.SessionScope,
                            .CharacterId = rec.CharacterId,
                            .Platform = rec.Platform,
                            .PlatformUserId = rec.PlatformUserId,
                            .CharacterName = If(rec.DisplayName, rec.PlatformPersona)
                        }
                    End If
                Next
                Return Nothing
            Catch ex As Exception
                _logger.LogWarning(ex, "[{Plugin}] ResolveIdentity failed", _pluginId)
                Return Nothing
            End Try
        End Function

        Public Sub ContributeIdentity(identity As UtilityIdentityInfo) Implements IUtilityContext.ContributeIdentity
            Require(UtilityCapabilities.IdentityWrite)
            If identity Is Nothing Then Return
            If String.IsNullOrEmpty(identity.GameId) Then
                Throw New ArgumentException(
                    "ContributeIdentity requires UtilityIdentityInfo.GameId — identities live per game.")
            End If
            Dim resolver = _serviceProvider.GetService(Of IdentityResolver)()
            If resolver Is Nothing Then Return
            resolver.Observe(identity.GameId, If(identity.SessionScope, ""),
                New IdentityObservation With {
                    .CharacterId = identity.CharacterId,
                    .PlatformUserId = identity.PlatformUserId,
                    .DisplayName = identity.CharacterName,
                    .Platform = identity.Platform,
                    .ObservedAtUtc = DateTime.UtcNow
                })
        End Sub

        ' --- config ---

        Public Function GetConfigValue(key As String) As String Implements IUtilityContext.GetConfigValue
            Require(UtilityCapabilities.Config)
            If String.IsNullOrEmpty(key) Then Return Nothing
            Dim values = UtilityPluginConfigStore.Load(_serviceProvider, _pluginId)
            Dim result As String = Nothing
            values.TryGetValue(key, result)
            Return result
        End Function

        Public Sub SetConfigValue(key As String, value As String) Implements IUtilityContext.SetConfigValue
            Require(UtilityCapabilities.Config)
            If String.IsNullOrEmpty(key) Then Return
            Try
                Dim values = UtilityPluginConfigStore.Load(_serviceProvider, _pluginId)
                values(key) = value
                UtilityPluginConfigStore.Save(_serviceProvider, _pluginId, values)
            Catch ex As Exception
                _logger.LogWarning(ex, "[{Plugin}] SetConfigValue failed", _pluginId)
            End Try
        End Sub

        ' --- web-capture ---

        Public Function CaptureWebSessionAsync(startUrl As String,
                                               completionUrlPattern As String,
                                               cookieDomain As String) As Task(Of WebSessionCaptureResult) Implements IUtilityContext.CaptureWebSessionAsync
            Require(UtilityCapabilities.WebCapture)
            _logger.LogInformation(
                "[{Plugin}] requested a web-session capture: start={Start}, completion contains '{Pattern}', cookies for {Domain}",
                _pluginId, startUrl, completionUrlPattern, cookieDomain)
            ' The form runs its own STA thread + modal pump — safe to
            ' call from any thread, never blocks the Manager's UI.
            Return UI.WebSessionCaptureForm.CaptureAsync(
                _pluginId, startUrl, completionUrlPattern, cookieDomain)
        End Function

        Public Function GetOrCaptureWebSessionAsync(sessionKey As String,
                                                    startUrl As String,
                                                    completionUrlPattern As String,
                                                    cookieDomain As String,
                                                    allowPrompt As Boolean) As Task(Of String) Implements IUtilityContext.GetOrCaptureWebSessionAsync
            Require(UtilityCapabilities.WebCapture)
            Dim store = _serviceProvider.GetRequiredService(Of WebSessionStore)()
            Return store.GetOrCaptureAsync(_pluginId, sessionKey, startUrl,
                                           completionUrlPattern, cookieDomain, allowPrompt)
        End Function

        Public Sub InvalidateWebSession(sessionKey As String) Implements IUtilityContext.InvalidateWebSession
            Require(UtilityCapabilities.WebCapture)
            _serviceProvider.GetRequiredService(Of WebSessionStore)().Invalidate(_pluginId, sessionKey)
        End Sub

        ' --- web-capture: store-under-key + enumerate (Phase 7-7) ---

        Public Sub StoreWebSession(sessionKey As String, cookieHeader As String) Implements IUtilityContext.StoreWebSession
            Require(UtilityCapabilities.WebCapture)
            _serviceProvider.GetRequiredService(Of WebSessionStore)().Store(_pluginId, sessionKey, cookieHeader)
        End Sub

        Public Function ListWebSessions() As IReadOnlyList(Of WebSessionSummary) Implements IUtilityContext.ListWebSessions
            Require(UtilityCapabilities.WebCapture)
            Dim result As New List(Of WebSessionSummary)
            Try
                Dim store = _serviceProvider.GetRequiredService(Of WebSessionStore)()
                For Each info In store.ListSessions(_pluginId)
                    result.Add(New WebSessionSummary With {
                        .SessionKey = info.SessionKey,
                        .CapturedAtUtc = info.CapturedAtUtc,
                        .LastUsedUtc = info.LastUsedUtc
                    })
                Next
            Catch ex As Exception
                _logger.LogWarning(ex, "[{Plugin}] ListWebSessions failed", _pluginId)
            End Try
            Return result
        End Function

    End Class

End Namespace
