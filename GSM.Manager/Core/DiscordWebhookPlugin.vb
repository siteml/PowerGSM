Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Net.Http
Imports System.Net.Http.Json
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Automation
Imports GSM.Manager.Data
Imports GSM.Notification
Imports GSM.Plugin
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging

' ============================================================
'  DiscordWebhookPlugin — INotificationPlugin that fans out
'  events to one-or-more Discord webhook destinations.
'
'  Design goals:
'    - Fully isolated from the rest of the Manager: every outbound
'      call is try/catch, rate-limited, batched. A Discord outage
'      or a single bad webhook URL must NEVER take down the app.
'    - Config-driven at runtime: destinations live in SQLite, can
'      be edited via the Notifications UI form while the Manager
'      is running. Cache is reloaded on demand.
'    - Rate-limited per webhook: Discord allows 5 messages per
'      2-second window per webhook. We batch incoming events
'      into a single embed-with-multiple-entries message when
'      several fire in the same window (e.g. a crash loop).
' ============================================================

Namespace GSM.Manager.Core

    Public Class DiscordWebhookPlugin
        Implements INotificationPlugin
        Implements IDestinationTargetingPlugin

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _httpClient As HttpClient
        Private ReadOnly _logger As ILogger(Of DiscordWebhookPlugin)
        Private ReadOnly _queues As New ConcurrentDictionary(Of String, DestinationQueue)

        ' Config cache — reloaded when RefreshConfig() is called
        ' (e.g. after the Notifications form saves changes).
        Private _destinationsCache As IReadOnlyList(Of DestinationCacheEntry) = New List(Of DestinationCacheEntry)
        Private _profilesCache As New Dictionary(Of String, VisibilityProfileCacheEntry)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _cacheLock As New Object()
        Private _initialized As Boolean = False

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of DiscordWebhookPlugin))
            _serviceProvider = serviceProvider
            _logger = logger
            _httpClient = New HttpClient()
            _httpClient.Timeout = TimeSpan.FromSeconds(15)
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "PowerGSM/1.0")
        End Sub

        ' ---- INotificationPlugin interface members ----

        Public ReadOnly Property PluginId As String = "discord-webhook" Implements INotificationPlugin.PluginId
        Public ReadOnly Property DisplayName As String = "Discord Webhooks" Implements INotificationPlugin.DisplayName

        Public Function GetConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) Implements INotificationPlugin.GetConfigSchema
            ' Empty — config is managed through the Notifications UI
            ' form which talks directly to the DB, not via generic
            ' plugin schema fields.
            Return New ConfigFieldDescriptor() {}
        End Function

        Public Function GetSupportedCommands() As IReadOnlyList(Of RemoteCommandDescriptor) Implements INotificationPlugin.GetSupportedCommands
            ' Webhooks are outbound only — no inbound commands.
            Return New RemoteCommandDescriptor() {}
        End Function

        Public Async Function InitialiseAsync(config As Dictionary(Of String, String),
                                                handler As IRemoteCommandHandler,
                                                cancellation As CancellationToken) As Task Implements INotificationPlugin.InitialiseAsync
            ' 'config' is passed in by NotificationService.RegisterPluginAsync
            ' but we ignore it — our config lives in NotificationDestinations
            ' and VisibilityProfiles tables, loaded by RefreshConfigAsync.
            ' 'handler' is ignored because webhooks can't receive commands.
            Await RefreshConfigAsync()
            _initialized = True
        End Function

        Public Async Function ShutdownAsync(cancellation As CancellationToken) As Task Implements INotificationPlugin.ShutdownAsync
            ' Drain outstanding queues gracefully. Each queue has its
            ' own background worker that'll exit when the queue empties.
            For Each q In _queues.Values
                Try
                    Await q.FlushAsync(cancellation)
                Catch
                End Try
            Next
            _initialized = False
        End Function

        Public Function SendNotificationAsync(context As NotificationContext,
                                                cancellation As CancellationToken) As Task(Of Boolean) Implements INotificationPlugin.SendNotificationAsync
            If Not _initialized Then Return Task.FromResult(False)

            Dim destinations As IReadOnlyList(Of DestinationCacheEntry)
            Dim profiles As Dictionary(Of String, VisibilityProfileCacheEntry)
            SyncLock _cacheLock
                destinations = _destinationsCache
                profiles = _profilesCache
            End SyncLock

            ' Route to every destination that matches the event's
            ' scope, event type, and enabled flag. Each destination
            ' is queued independently so a slow / failing webhook
            ' doesn't block others.
            Dim dispatched = False
            For Each dest In destinations
                If Not dest.Enabled Then Continue For
                If Not dest.MatchesEvent(context) Then Continue For
                Try
                    Dim profile As VisibilityProfileCacheEntry = Nothing
                    profiles.TryGetValue(If(dest.VisibilityProfileId, ""), profile)
                    EnqueueForDestination(dest, profile, context)
                    dispatched = True
                Catch ex As Exception
                    _logger.LogWarning(ex, "Failed to enqueue notification for destination {Name}", dest.DisplayName)
                End Try
            Next

            Return Task.FromResult(dispatched)
        End Function

        ' ---- IDestinationTargetingPlugin ----
        '
        ' Phase 4b-1.5: opt-in capability for direct destination
        ' dispatch from automation rules. The event-driven path
        ' (SendNotificationAsync above) keeps fan-out semantics;
        ' this path bypasses event-type and scope filtering and
        ' targets a single destination by ID.

        Public Function OwnsDestination(destinationId As String) As Boolean Implements IDestinationTargetingPlugin.OwnsDestination
            If String.IsNullOrEmpty(destinationId) Then Return False
            ' Cheap cache check: BuildCacheEntry already filtered
            ' to TransportKind = "DiscordWebhook", so any entry
            ' present in _destinationsCache is one we own.
            Dim destinations As IReadOnlyList(Of DestinationCacheEntry)
            SyncLock _cacheLock
                destinations = _destinationsCache
            End SyncLock
            For Each d In destinations
                If String.Equals(d.DestinationId, destinationId, StringComparison.OrdinalIgnoreCase) Then
                    Return True
                End If
            Next
            Return False
        End Function

        Public Function SendCustomToDestinationAsync(
                destinationId As String,
                message As String,
                severity As NotificationSeverity,
                tokens As NotificationTokens,
                cancellation As CancellationToken) As Task(Of Boolean) Implements IDestinationTargetingPlugin.SendCustomToDestinationAsync

            If Not _initialized Then Return Task.FromResult(False)
            If String.IsNullOrEmpty(destinationId) Then Return Task.FromResult(False)

            Dim destinations As IReadOnlyList(Of DestinationCacheEntry)
            Dim profiles As Dictionary(Of String, VisibilityProfileCacheEntry)
            SyncLock _cacheLock
                destinations = _destinationsCache
                profiles = _profilesCache
            End SyncLock

            Dim target As DestinationCacheEntry = Nothing
            For Each d In destinations
                If String.Equals(d.DestinationId, destinationId, StringComparison.OrdinalIgnoreCase) Then
                    target = d
                    Exit For
                End If
            Next

            If target Is Nothing Then
                _logger.LogWarning(
                    "SendCustomToDestinationAsync: destination {Id} not found in cache (deleted or wrong transport?)",
                    destinationId)
                Return Task.FromResult(False)
            End If

            If Not target.Enabled Then
                _logger.LogInformation(
                    "SendCustomToDestinationAsync: destination {Name} is disabled, skipping",
                    target.DisplayName)
                Return Task.FromResult(False)
            End If

            ' Build a Custom-event NotificationContext. We pass
            ' tokens through (caller may have populated them with
            ' rule-context values like RuleName) so the existing
            ' embed renderer has something to work with. Visibility
            ' profile is intentionally NOT applied to the message
            ' body — author wrote literal text and presumably
            ' meant it. Profile would only redact structured
            ' tokens, which a custom message doesn't have.
            Dim ctx As New NotificationContext With {
                .EventType = NotificationEventType.Custom,
                .Severity = severity,
                .Title = "PowerGSM",
                .Message = message,
                .Tokens = If(tokens, New NotificationTokens()),
                .Metadata = New Dictionary(Of String, String),
                .Timestamp = DateTime.UtcNow
            }

            Try
                Dim profile As VisibilityProfileCacheEntry = Nothing
                profiles.TryGetValue(If(target.VisibilityProfileId, ""), profile)
                EnqueueForDestination(target, profile, ctx)
                Return Task.FromResult(True)
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to enqueue custom notification for {Name}", target.DisplayName)
                Return Task.FromResult(False)
            End Try
        End Function

        ' ---- Public API for the Notifications form ----

        ''' <summary>
        ''' Reloads destination and profile caches from the DB.
        ''' Called after the Notifications form saves changes.
        ''' </summary>
        Public Async Function RefreshConfigAsync() As Task
            Dim newDestinations As New List(Of DestinationCacheEntry)
            Dim newProfiles As New Dictionary(Of String, VisibilityProfileCacheEntry)(StringComparer.OrdinalIgnoreCase)

            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    Dim profiles = Await db.VisibilityProfiles.ToListAsync()
                    For Each p In profiles
                        Dim allowedFields As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                        If Not String.IsNullOrEmpty(p.AllowedFieldsJson) Then
                            Try
                                Dim list = JsonSerializer.Deserialize(Of List(Of String))(p.AllowedFieldsJson)
                                If list IsNot Nothing Then
                                    For Each f In list
                                        allowedFields.Add(f)
                                    Next
                                End If
                            Catch
                            End Try
                        End If
                        newProfiles(p.ProfileId) = New VisibilityProfileCacheEntry With {
                            .ProfileId = p.ProfileId,
                            .DisplayName = p.DisplayName,
                            .AllowedFields = allowedFields
                        }
                    Next

                    Dim destinations = Await db.NotificationDestinations.ToListAsync()
                    For Each d In destinations
                        If Not String.Equals(d.TransportKind, "DiscordWebhook",
                                              StringComparison.OrdinalIgnoreCase) Then
                            Continue For
                        End If
                        Dim entry = BuildCacheEntry(d)
                        If entry IsNot Nothing Then newDestinations.Add(entry)
                    Next
                End Using
            Catch ex As Exception
                _logger.LogError(ex, "Failed to refresh Discord notification config")
                Return
            End Try

            SyncLock _cacheLock
                _destinationsCache = newDestinations
                _profilesCache = newProfiles
            End SyncLock
            _logger.LogInformation("Discord notification config reloaded: {Count} destination(s)", newDestinations.Count)
        End Function

        ''' <summary>
        ''' Synchronously fires a test message to a webhook URL without
        ''' touching the DB — used by the Test button in the UI before
        ''' the destination is saved.
        ''' </summary>
        Public Async Function SendTestAsync(webhookUrl As String,
                                             displayName As String,
                                             cancellation As CancellationToken) As Task(Of String)
            If String.IsNullOrWhiteSpace(webhookUrl) Then
                Return "Webhook URL is required"
            End If
            Try
                Dim payload As New DiscordWebhookPayload With {
                    .Username = "PowerGSM",
                    .Embeds = {New DiscordEmbed With {
                        .Title = "📣 PowerGSM test message",
                        .Description = $"If you're reading this, the webhook for **{displayName}** is working.",
                        .Color = &H5865F2
                    }}.ToList()
                }
                Using resp = Await _httpClient.PostAsJsonAsync(webhookUrl, payload, cancellation)
                    If resp.IsSuccessStatusCode Then Return Nothing
                    Dim body = ""
                    Try : body = Await resp.Content.ReadAsStringAsync(cancellation) : Catch : End Try
                    Return $"{CInt(resp.StatusCode)} {resp.ReasonPhrase}: {body}"
                End Using
            Catch ex As Exception
                Return ex.Message
            End Try
        End Function

        ' ---- Internal dispatch ----

        Private Sub EnqueueForDestination(dest As DestinationCacheEntry,
                                           profile As VisibilityProfileCacheEntry,
                                           context As NotificationContext)
            Dim q = _queues.GetOrAdd(dest.DestinationId,
                Function(id) New DestinationQueue(id, _httpClient, _logger))
            q.Enqueue(New QueuedMessage With {
                .Destination = dest,
                .Profile = profile,
                .Context = context
            })
        End Sub

        Private Shared Function BuildCacheEntry(e As NotificationDestinationEntity) As DestinationCacheEntry
            If e Is Nothing OrElse String.IsNullOrEmpty(e.TransportConfigJson) Then Return Nothing
            Dim webhookUrl As String = Nothing
            Try
                Dim transport = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(e.TransportConfigJson)
                If transport IsNot Nothing Then transport.TryGetValue("WebhookUrl", webhookUrl)
            Catch
            End Try
            If String.IsNullOrWhiteSpace(webhookUrl) Then Return Nothing

            Dim entry As New DestinationCacheEntry With {
                .DestinationId = e.DestinationId,
                .DisplayName = If(e.DisplayName, "(unnamed)"),
                .Enabled = e.Enabled,
                .WebhookUrl = webhookUrl,
                .VisibilityProfileId = e.VisibilityProfileId
            }

            entry.EnabledEventTypes = ParseEnumSet(e.EnabledEventTypesJson)
            entry.NodeFilter = ParseStringSet(e.NodeFilterJson)
            entry.InstallationFilter = ParseStringSet(e.InstallationFilterJson)
            entry.InstanceFilter = ParseStringSet(e.InstanceFilterJson)
            entry.InstanceSetFilter = ParseStringSet(e.InstanceSetFilterJson, StringComparer.Ordinal)
            entry.TemplateOverrides = ParseTemplateOverrides(e.TemplateOverridesJson)

            Return entry
        End Function

        Private Shared Function ParseEnumSet(json As String) As HashSet(Of NotificationEventType)
            Dim result As New HashSet(Of NotificationEventType)
            If String.IsNullOrEmpty(json) Then Return result
            Try
                Dim list = JsonSerializer.Deserialize(Of List(Of String))(json)
                If list IsNot Nothing Then
                    For Each name In list
                        Dim parsed As NotificationEventType
                        If [Enum].TryParse(name, True, parsed) Then result.Add(parsed)
                    Next
                End If
            Catch
            End Try
            Return result
        End Function

        Private Shared Function ParseStringSet(json As String,
                Optional comparer As IEqualityComparer(Of String) = Nothing) As HashSet(Of String)
            Dim result As New HashSet(Of String)(If(comparer, StringComparer.OrdinalIgnoreCase))
            If String.IsNullOrEmpty(json) Then Return result
            Try
                Dim list = JsonSerializer.Deserialize(Of List(Of String))(json)
                If list IsNot Nothing Then
                    For Each v In list
                        If Not String.IsNullOrWhiteSpace(v) Then result.Add(v)
                    Next
                End If
            Catch
            End Try
            Return result
        End Function

        Private Shared Function ParseTemplateOverrides(json As String) As Dictionary(Of NotificationEventType, String)
            Dim result As New Dictionary(Of NotificationEventType, String)
            If String.IsNullOrEmpty(json) Then Return result
            Try
                Dim raw = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(json)
                If raw IsNot Nothing Then
                    For Each kvp In raw
                        Dim parsed As NotificationEventType
                        If [Enum].TryParse(kvp.Key, True, parsed) Then result(parsed) = kvp.Value
                    Next
                End If
            Catch
            End Try
            Return result
        End Function

    End Class

    ' ============================================================
    '  Supporting types — cached representations of DB entities
    '  that are cheap to access on the hot path (every notification).
    ' ============================================================

    Friend Class DestinationCacheEntry
        Public Property DestinationId As String
        Public Property DisplayName As String
        Public Property Enabled As Boolean
        Public Property WebhookUrl As String
        Public Property VisibilityProfileId As String
        Public Property EnabledEventTypes As HashSet(Of NotificationEventType)
        Public Property NodeFilter As HashSet(Of String)
        Public Property InstallationFilter As HashSet(Of String)
        Public Property InstanceFilter As HashSet(Of String)
        Public Property InstanceSetFilter As HashSet(Of String)
        Public Property TemplateOverrides As Dictionary(Of NotificationEventType, String)

        Public Function MatchesEvent(context As NotificationContext) As Boolean
            If EnabledEventTypes IsNot Nothing AndAlso EnabledEventTypes.Count > 0 Then
                If Not EnabledEventTypes.Contains(context.EventType) Then Return False
            End If

            ' Scope: union-of-includes across the four dimensions
            ' (Phase 5n). The event is in scope if it matches ANY
            ' non-empty filter; when every filter is empty the
            ' destination has no scope restriction (all instances).
            ' The event-type gate above stays a separate AND.
            Dim tokens = context.Tokens
            Dim anyFilter = HasItems(NodeFilter) OrElse HasItems(InstallationFilter) OrElse
                            HasItems(InstanceFilter) OrElse HasItems(InstanceSetFilter)
            If anyFilter Then
                Dim nodeId = If(tokens Is Nothing, "", If(tokens.NodeId, ""))
                Dim installId = If(tokens Is Nothing, "", If(tokens.InstallationId, ""))
                ' Instance / set dimensions match against every instance
                ' the event pertains to (Phase 5n fan-out): the single
                ' instance for instance-level events, all instances under
                ' the installation for installation-level events.
                Dim inScope = Hit(NodeFilter, nodeId) OrElse
                              Hit(InstallationFilter, installId) OrElse
                              HitAny(InstanceFilter, context.ScopeInstanceIds) OrElse
                              HitAny(InstanceSetFilter, context.ScopeInstanceSetTags)
                If Not inScope Then Return False
            End If
            Return True
        End Function

        ' Union-of-includes helpers. HasItems = "this dimension
        ' contributes a filter"; Hit = "a present token value is in
        ' this filter". InstanceSetFilter carries an Ordinal comparer
        ' (set parity with RuleScope.InstanceSet); the ID filters are
        ' OrdinalIgnoreCase — each set's own comparer applies here.
        Private Shared Function HasItems(items As HashSet(Of String)) As Boolean
            Return items IsNot Nothing AndAlso items.Count > 0
        End Function

        Private Shared Function Hit(items As HashSet(Of String), value As String) As Boolean
            Return items IsNot Nothing AndAlso items.Count > 0 AndAlso
                   Not String.IsNullOrEmpty(value) AndAlso items.Contains(value)
        End Function

        ' Multi-value variant for the fanned-out instance / set
        ' dimensions: true if the filter contains ANY of the values.
        Private Shared Function HitAny(items As HashSet(Of String), values As List(Of String)) As Boolean
            If items Is Nothing OrElse items.Count = 0 OrElse values Is Nothing Then Return False
            For Each v In values
                If Not String.IsNullOrEmpty(v) AndAlso items.Contains(v) Then Return True
            Next
            Return False
        End Function
    End Class

    Friend Class VisibilityProfileCacheEntry
        Public Property ProfileId As String
        Public Property DisplayName As String
        Public Property AllowedFields As HashSet(Of String)

        Public Function AllowsField(fieldName As String) As Boolean
            If AllowedFields Is Nothing OrElse AllowedFields.Count = 0 Then Return True
            Return AllowedFields.Contains(fieldName)
        End Function
    End Class

    Friend Class QueuedMessage
        Public Property Destination As DestinationCacheEntry
        Public Property Profile As VisibilityProfileCacheEntry
        Public Property Context As NotificationContext
    End Class

End Namespace