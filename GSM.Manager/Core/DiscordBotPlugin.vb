Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports DSharpPlus
Imports DSharpPlus.Entities
Imports DSharpPlus.EventArgs
Imports DSharpPlus.Exceptions
Imports DSharpPlus.SlashCommands
' The webhook plugin defines its own helper module named
' DiscordEmbedBuilder in this same namespace (GSM.Manager.Core).
' Same-namespace declarations beat aliased imports in VB's name
' resolution, so we can't reuse the simple name — alias the
' DSharpPlus class under a unique name and use that instead.
Imports DPEmbedBuilder = DSharpPlus.Entities.DiscordEmbedBuilder
Imports DPEmbed = DSharpPlus.Entities.DiscordEmbed
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports NCrontab
Imports GSM.Automation
Imports GSM.Manager.Data
Imports GSM.Node.Api
Imports GSM.Notification
Imports GSM.Plugin

' ============================================================
'  DiscordBotPlugin — INotificationPlugin maintaining persistent
'  control panels in Discord channels via a long-lived gateway
'  connection (DSharpPlus 4.x).
'
'  Design goals:
'    - Coexists with DiscordWebhookPlugin. Both plugins receive
'      every notification context; the bot uses state-change
'      events as cues to refresh its panels but does NOT (in 5d-1)
'      send notification messages — outbound parity ships in 5d-4.
'    - Failure-isolated: connect/disconnect/edit failures log a
'      warning and never tear down the rest of the manager. A
'      missing token, an offline Discord, or a deleted channel
'      degrades to "panel doesn't refresh" rather than a crash.
'    - Rate-limit aware. At most one Discord edit per panel per
'      five seconds; a global 1-second tick coalesces queued
'      refresh requests with that floor and also drives per-panel
'      drift refreshes (player-count and time-relative cells that
'      don't fire NotificationEmitter events).
'    - Persists message IDs. First successful post stores the
'      MessageId on DiscordPanelEntity; subsequent refreshes edit
'      the same message in place, preserving permalinks. A 404
'      on edit (channel purge / manual deletion) re-posts and
'      updates the stored MessageId.
'
'  5d-1 scope intentionally STOPS at:
'    - Read-only panels (status table + a single Manage button)
'    - Manage button click → ephemeral "5d-2" placeholder reply
'    - No outbound notification messages (returns False from
'      SendNotificationAsync — webhook plugin still fans those out)
'    - No slash commands (GetSupportedCommands returns empty)
'    - Single bot identity ("default" config row); multi-bot
'      future is a column-only future, no schema change
' ============================================================

Namespace GSM.Manager.Core

    Public Class DiscordBotPlugin
        Implements INotificationPlugin
        Implements IDestinationTargetingPlugin

        ' Refresh budget per the design doc. Discord's per-channel
        ' edit rate limit allows roughly 5 edits per 5 seconds; we
        ' keep a healthy floor of one edit per 5s per panel so a
        ' burst of state changes (e.g. a sequence action stopping
        ' four instances in series) coalesces into at most one
        ' edit per 5s window per affected panel.
        Private Const PanelEditCooldownMs As Integer = 5000

        ' Tick interval for the unified refresh loop. One second is
        ' fine-grained enough for the 5s rate-limit gate to feel
        ' responsive without burning cycles when nothing is pending.
        Private Const RefreshTickMs As Integer = 1000

        ' Default per-panel drift refresh in seconds. Overridden
        ' per-panel via DiscordPanelEntity.RefreshIntervalSeconds.
        Private Const DefaultDriftRefreshSeconds As Integer = 60

        ' How long to cache the Factorio server name read from
        ' server-settings.json. The file is effectively immutable
        ' for the lifetime of a running server — Factorio loads
        ' it once on start and never re-reads it. So we don't
        ' need a time-based TTL: the cache entry is valid as long
        ' as the instance keeps running, and we evict it whenever
        ' the instance enters a non-running state (the next start
        ' will pick up whatever the user edited in the meantime).
        ' Eviction is driven from BuildInScopeRow, which already
        ' has the live state on hand for every panel render — no
        ' separate poller needed.

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger(Of DiscordBotPlugin)
        Private ReadOnly _instanceManager As InstanceManager
        Private ReadOnly _credentialService As CredentialService

        ' DSharpPlus client. Built on connect, torn down on
        ' disconnect; both happen under _connectionLock so a UI
        ' "Test Connection" reload from the configuration form
        ' doesn't race against the in-flight refresh loop.
        Private _client As DiscordClient
        Private ReadOnly _connectionLock As New SemaphoreSlim(1, 1)

        ' Phase 5d-4 — slash command extension. Built once per
        ' DiscordClient (the extension hooks the client's Ready
        ' event), torn down with the client. Lives alongside
        ' _client under _connectionLock; nullable while
        ' disconnected.
        Private _slashCommands As SlashCommandsExtension

        Private _initialized As Boolean = False
        Private _connected As Boolean = False
        ' Phase 5d-5 item 5 — timestamp of the most recent
        ' successful gateway connect. Set on connect-success,
        ' nulled on disconnect or connect-failure. Surfaced via
        ' ConnectedSinceUtc for the configuration form's uptime
        ' display.
        Private _connectedSinceUtc As DateTime?

        ' Per-panel runtime state — independent of the DB row so
        ' rate-limit clocks and pending-refresh flags survive
        ' DB-config edits. Rebuilt from the DB on ReloadConfigAsync.
        Private ReadOnly _panels As New ConcurrentDictionary(Of String, PanelRuntime)

        ' Per-instance cache of the Factorio server name resolved
        ' from server-settings.json. Keyed by InstanceId; entries
        ' live for as long as the instance keeps running and are
        ' evicted on the first non-running observation (see
        ' BuildInScopeRow). Persists across panel refreshes,
        ' cleared implicitly on plugin shutdown.
        Private ReadOnly _factorioServerNameCache As _
            New ConcurrentDictionary(Of String, String)

        ' Phase 5d-3 + 5d-5 item 4 — role-to-permission mapping
        ' cache, two-level keyed by (GuildId, PanelId).
        '   Outer key: GuildId.
        '   Middle key: PanelId, where "" is the guild-default
        '     scope and a non-empty value is a panel-scoped
        '     override.
        '   Inner dict: RoleId → the permission tier granted by
        '     that role under the (guild, panel) scope.
        ' Roles not present in the inner dict contribute nothing
        ' — Everyone is the implicit default for unmapped roles,
        ' so the table only holds elevations.
        '
        ' Resolver semantics (whole-mapping override): if a
        ' panel-scoped inner dict exists for (guildId, panelId),
        ' it is authoritative — the guild-default mapping is NOT
        ' consulted for that panel. This lets an operator deny
        ' access to a role at panel scope by simply not including
        ' it in the override mapping. If no panel-scoped inner
        ' dict exists (or panelId is ""), the resolver uses the
        ' guild-default inner dict at key "".
        '
        ' Loaded from the DB on every successful connect and
        ' refreshed by the configuration UI via
        ' ReloadRoleMappingsAsync. ConcurrentDictionary at the
        ' outer level is enough for our concurrency profile:
        ' middle dict instances are built fresh during reload
        ' and atomically swapped in via the outer dict's indexer,
        ' so callers reading a guild snapshot always see a
        ' consistent view of that guild's scopes. The
        ' reload-vs-read race window is microseconds; worst-case
        ' effect is one click resolving to Everyone during the
        ' swap, with the user's next click seeing the new state.
        Private ReadOnly _roleMappings As _
            New ConcurrentDictionary(Of String, Dictionary(Of String, Dictionary(Of String, CommandPermission)))

        ' Phase 5d-4 — outbound destinations cache. Mirror of
        ' DiscordWebhookPlugin's destinations cache pattern: rows
        ' from NotificationDestinations where TransportKind =
        ' "DiscordBot" are loaded into BotDestinationCacheEntry,
        ' filtered against incoming events in SendNotificationAsync,
        ' and dispatched via per-destination queues that batch +
        ' rate-limit (matching the 5/5s per-channel Discord limit).
        '
        ' Visibility profiles are shared with the webhook plugin
        ' (same VisibilityProfileCacheEntry type from
        ' DestinationQueue.vb) so a profile defined for webhook
        ' destinations applies identically to bot destinations.
        ' Same applies to per-event template overrides — the
        ' DiscordEmbedBuilder module is shared, so a template
        ' string written for a webhook destination renders
        ' identically when dropped on a bot destination.
        Private _destinationsCache As IReadOnlyList(Of BotDestinationCacheEntry) = New List(Of BotDestinationCacheEntry)
        Private _destProfilesCache As New Dictionary(Of String, VisibilityProfileCacheEntry)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _destCacheLock As New Object()

        ' Per-destination dispatch queue. Keyed by DestinationId
        ' since each destination has its own (guild, channel) pair
        ' and its own rate-limit posture; mixing destinations into
        ' a shared queue would cause one slow channel to back up
        ' another's events.
        Private ReadOnly _destQueues As _
            New ConcurrentDictionary(Of String, BotDestinationQueue)

        ' Command handler supplied by NotificationService at
        ' InitialiseAsync time. Used by the Manage flow's action
        ' buttons to dispatch start/stop/restart commands back to
        ' the manager. Nothing until init has run; all dispatch
        ' sites null-check.
        Private _commandHandler As IRemoteCommandHandler

        Private _refreshCts As CancellationTokenSource
        Private _refreshLoopTask As Task

        ' ============================================================
        '  Adaptive reconnect state
        '
        '  Discord rate-limits the /gateway/bot REST endpoint
        '  aggressively after rapid build/test cycles or other
        '  identify-rate-limit triggers, and DSharpPlus 4.5.0
        '  reacts by retrying internally every ~3 seconds with no
        '  exit condition. Without external bounding, the first
        '  ConnectAsync at startup hangs indefinitely AND keeps
        '  hammering Discord (which can extend the cooldown
        '  further). Two mechanisms address that:
        '
        '    1. ConnectAttemptTimeoutSec — each ConnectAsync call
        '       races newClient.ConnectAsync() against a delay;
        '       if the delay wins, we dispose the client to halt
        '       DSharpPlus's internal loop and treat the attempt
        '       as a transient failure.
        '
        '    2. ConnectBackoffSchedule + ReconnectLoopAsync —
        '       after each failed attempt the loop waits an
        '       increasing amount of time before retrying.
        '       Capped at 30 minutes so prolonged outages don't
        '       leave the bot offline forever; the operator can
        '       also force an immediate retry via the bot form's
        '       Test Connection (calls ReloadConfigAsync →
        '       restarts the loop).
        ' ============================================================

        Private Const ConnectAttemptTimeoutSec As Integer = 20

        Private Shared ReadOnly ConnectBackoffSchedule As Integer() = _
            {30, 60, 120, 300, 600, 1200, 1800}

        Private _reconnectCts As CancellationTokenSource
        Private _reconnectLoopTask As Task

        ' Probe-driven retry hints. Set by ConnectAsync at the
        ' start of each attempt; updated by the pre-flight
        ' /gateway/bot probe (see ProbeGatewayAsync).
        '   _lastConnectRateLimitWaitSec > 0 → last probe saw
        '     HTTP 429; loop should wait that many seconds
        '     (plus a small buffer) instead of using
        '     ConnectBackoffSchedule.
        '   _lastConnectFatalAuth = True → last probe saw HTTP
        '     401; loop should EXIT, not retry. Operator must
        '     update token via the bot form (which calls
        '     ReloadConfigAsync → restarts the loop).
        Private _lastConnectRateLimitWaitSec As Double = 0
        Private _lastConnectFatalAuth As Boolean = False

        ' Wall-clock time of the loop's next ConnectAsync call.
        ' Set immediately before Task.Delay; cleared when the
        ' delay returns or the loop exits. Nothing while a
        ' connect attempt is in flight or before the loop
        ' starts. Read by the bot configuration form to render
        ' a "next attempt in Ns" countdown.
        Private _nextConnectAttemptUtc As DateTime?

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of DiscordBotPlugin),
                       instanceManager As InstanceManager,
                       credentialService As CredentialService)
            _serviceProvider = serviceProvider
            _logger = logger
            _instanceManager = instanceManager
            _credentialService = credentialService
        End Sub

        ' ============================================================
        '  INotificationPlugin
        ' ============================================================

        Public ReadOnly Property PluginId As String =
            "discord-bot" Implements INotificationPlugin.PluginId

        Public ReadOnly Property DisplayName As String =
            "Discord Bot" Implements INotificationPlugin.DisplayName

        Public Function GetConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor) _
                Implements INotificationPlugin.GetConfigSchema
            ' Bot config lives on its own form (DiscordBotForm) rather
            ' than the generic schema-driven config UI — the panel
            ' editor + role mapping (5d-3) need richer UI than the
            ' schema can express. Returning empty here means
            ' NotificationService doesn't try to render anything.
            Return New ConfigFieldDescriptor() {}
        End Function

        Public Function GetSupportedCommands() As IReadOnlyList(Of RemoteCommandDescriptor) _
                Implements INotificationPlugin.GetSupportedCommands
            ' Slash commands (5d-4) ship through DSharpPlus's
            ' SlashCommandsExtension and route directly inside
            ' GsmSlashCommands; they don't use the
            ' IRemoteCommandHandler / InboundCommand pathway.
            ' Action button clicks on panels DO go through that
            ' pathway (HandleActionClickAsync), but those are
            ' identified by component custom-id rather than by
            ' a RemoteCommandDescriptor entry. Returning empty
            ' here is correct: NotificationService scans this
            ' list to advertise the bot as a source of inbound
            ' commands, and we have nothing to advertise.
            Return New RemoteCommandDescriptor() {}
        End Function

        Public Async Function InitialiseAsync(config As Dictionary(Of String, String),
                                               handler As IRemoteCommandHandler,
                                               cancellation As CancellationToken) As Task _
                Implements INotificationPlugin.InitialiseAsync
            ' 'config' is unused — bot config lives in
            ' DiscordBotConfigEntity (encrypted token) and
            ' DiscordPanelEntity. 'handler' is the
            ' NotificationService for routing inbound commands;
            ' stored for the Manage flow's action dispatch (5d-2).
            _commandHandler = handler
            _initialized = True

            ' Connect in the BACKGROUND, not inline. ManagerProgram
            ' sync-blocks startup on this call's completion via
            ' .GetAwaiter().GetResult() to ensure the plugin is
            ' initialised before any events fire — but the actual
            ' Discord connect is unbounded. If Discord rate-limits
            ' the /gateway/bot endpoint with HTTP 429, DSharpPlus
            ' (4.5.0) reacts by retrying every ~3 seconds with no
            ' exit condition. Awaiting that inline would freeze
            ' MainForm forever — observed in production after a
            ' rapid build/test cycle tripped Discord's identify-
            ' rate-limit ceiling.
            '
            ' Fire-and-forget here is safe because:
            '   - _initialized = True above is the gate that
            '     SendNotificationAsync checks; events flow as
            '     soon as the plugin reports initialised, even if
            '     the connect is still in flight.
            '   - _connected = False (set inside DisconnectInternalAsync
            '     and only flipped to True on a successful connect)
            '     short-circuits the destination-dispatch path,
            '     so events fired during a not-yet-connected
            '     window are silently skipped rather than queued.
            '   - The connect succeeds on its own once the rate
            '     limit clears or transient network issues resolve.
            '
            ' If you need to stop the bot from trying altogether,
            ' set DiscordBotConfigs.Enabled = 0 in gsm.db. The
            ' loop still polls on its backoff schedule, but each
            ' poll is just one DB read — no network activity until
            ' the operator re-enables and triggers ReloadConfigAsync.
            _reconnectCts = New CancellationTokenSource()
            _reconnectLoopTask = ReconnectLoopAsync(_reconnectCts.Token)

            StartRefreshLoop()
        End Function

        ''' <summary>
        ''' Background reconnect loop. Calls ConnectAsync, and if
        ''' the call returns without _connected being set
        ''' (transient failure, rate limit, timeout, missing
        ''' config), waits per the backoff schedule before
        ''' retrying. Exits cleanly on cancellation
        ''' (ShutdownAsync / ReloadConfigAsync).
        '''
        ''' On successful connect the loop exits immediately —
        ''' from that point DSharpPlus's AutoReconnect = True
        ''' owns post-connect WebSocket recovery. If THAT layer
        ''' falls over (e.g. a separate rate limit on resume),
        ''' the bot stays disconnected until the operator
        ''' triggers ReloadConfigAsync via the bot form. Bounding
        ''' DSharpPlus's auto-reconnect is a separate concern
        ''' not addressed here.
        ''' </summary>
        Private Async Function ReconnectLoopAsync(ct As CancellationToken) As Task
            Dim attemptIndex As Integer = 0
            ' Consecutive-429 streak counter. Discord's
            ' /gateway/bot endpoint can return scope=shared 429s
            ' (code 40062, "Service resource is being rate
            ' limited") that come from a Discord-internal shared
            ' bucket — not our bot's per-user bucket. The
            ' Retry-After in those responses is the shared
            ' bucket's window (typically 3-5s) and tells us
            ' nothing about when WE'LL be free, since other bots
            ' hammering the same bucket keep it hot. Naively
            ' trusting Retry-After here means we wake every
            ' ~5s and immediately re-incur the same response
            ' indefinitely. After {RateLimitTrustLimit} attempts
            ' in a row we abandon Retry-After and fall back to
            ' the fixed ConnectBackoffSchedule, which gives the
            ' shared bucket time to actually clear. Reset on any
            ' non-rate-limit outcome (success, 401, network).
            Dim rateLimitStreak As Integer = 0
            Const RateLimitTrustLimit As Integer = 2
            Try
                While Not ct.IsCancellationRequested
                    Try
                        Await ConnectAsync()
                    Catch ex As OperationCanceledException
                        Return
                    Catch
                        ' ConnectAsync swallows internally; this
                        ' catch is defensive in case a future
                        ' change lets an exception escape.
                    End Try

                    If _connected Then Return

                    ' Fatal auth failure — token won't fix itself
                    ' on retry. Operator must update it via the
                    ' bot form, which triggers ReloadConfigAsync
                    ' and restarts this loop.
                    If _lastConnectFatalAuth Then
                        _logger.LogWarning(
                            "Discord bot reconnect halting; update the token via the bot form to resume.")
                        Return
                    End If

                    ' Pick the wait: Discord's Retry-After when
                    ' the probe gave us one, otherwise the fixed
                    ' backoff schedule. Retry-After path doesn't
                    ' increment attemptIndex — a rate-limit window
                    ' isn't an escalating-failure signal, so we
                    ' want the fixed schedule to start fresh from
                    ' the beginning if a non-rate-limit failure
                    ' eventually occurs.
                    Dim waitSec As Double
                    If _lastConnectRateLimitWaitSec > 0 Then
                        rateLimitStreak += 1
                        If rateLimitStreak <= RateLimitTrustLimit Then
                            ' Trust Retry-After for the first
                            ' couple of hits. +2s buffer for
                            ' clock skew between our wait and
                            ' Discord's reset — prevents an
                            ' immediate second 429 if our clocks
                            ' are a hair fast.
                            waitSec = _lastConnectRateLimitWaitSec + 2
                            _logger.LogInformation(
                                "Discord rate-limited (streak {Streak}/{Limit}); retrying in {Sec}s (Retry-After + 2s buffer)",
                                rateLimitStreak, RateLimitTrustLimit, CInt(waitSec))
                        Else
                            ' Streak past the trust limit — the
                            ' shared bucket is clearly sustained
                            ' hot and Retry-After isn't going to
                            ' free us. Switch to the fixed
                            ' schedule, escalating with each
                            ' additional 429 in the streak.
                            ' Schedule index starts at 0 once the
                            ' streak first crosses the trust
                            ' limit and climbs from there.
                            Dim escIdx = Math.Min(
                                rateLimitStreak - RateLimitTrustLimit - 1,
                                ConnectBackoffSchedule.Length - 1)
                            waitSec = ConnectBackoffSchedule(escIdx)
                            _logger.LogWarning(
                                "Discord rate-limit streak {Streak} exceeds trust limit; ignoring Retry-After ({Retry}s) and waiting {Sec}s instead. Likely Discord shared-bucket congestion (scope=shared, code=40062).",
                                rateLimitStreak, CInt(_lastConnectRateLimitWaitSec), CInt(waitSec))
                        End If
                    Else
                        rateLimitStreak = 0
                        Dim idx = Math.Min(attemptIndex, ConnectBackoffSchedule.Length - 1)
                        waitSec = ConnectBackoffSchedule(idx)
                        attemptIndex += 1
                        _logger.LogInformation(
                            "Discord bot not connected after attempt {N}; next try in {Sec}s",
                            attemptIndex, CInt(waitSec))
                    End If

                    Try
                        _nextConnectAttemptUtc = DateTime.UtcNow.AddSeconds(waitSec)
                        Await Task.Delay(TimeSpan.FromSeconds(waitSec), ct)
                    Catch ex As OperationCanceledException
                        _nextConnectAttemptUtc = Nothing
                        Return
                    End Try
                    _nextConnectAttemptUtc = Nothing
                End While
            Catch ex As Exception
                _logger.LogWarning(ex, "Discord bot reconnect loop exited unexpectedly")
            End Try
        End Function

        Public Async Function ShutdownAsync(cancellation As CancellationToken) As Task _
                Implements INotificationPlugin.ShutdownAsync
            ' Cancel the reconnect loop and wait for it to drain.
            ' Worst-case wait is ConnectAttemptTimeoutSec if the
            ' loop is mid-attempt — we can't pre-empt DSharpPlus's
            ' in-flight HTTP work, only abandon it after the
            ' deadline. If the loop is in its backoff sleep,
            ' cancellation breaks it out of Task.Delay immediately.
            Try
                _reconnectCts?.Cancel()
            Catch
            End Try
            If _reconnectLoopTask IsNot Nothing Then
                Try
                    Await _reconnectLoopTask
                Catch
                End Try
            End If
            Try
                _reconnectCts?.Dispose()
            Catch
            End Try
            _reconnectCts = Nothing
            _reconnectLoopTask = Nothing

            StopRefreshLoop()
            ' Drain destination queues before tearing down the
            ' Discord connection — otherwise in-flight notifications
            ' would lose their transport mid-send. FlushAsync
            ' polls until the queue worker drains or hits its 10s
            ' deadline; failures are swallowed so a slow queue
            ' can't block manager shutdown.
            For Each q In _destQueues.Values
                Try
                    Await q.FlushAsync(cancellation)
                Catch
                End Try
            Next
            Await DisconnectAsync()
            _initialized = False
        End Function

        Public Function SendNotificationAsync(context As NotificationContext,
                                                cancellation As CancellationToken) As Task(Of Boolean) _
                Implements INotificationPlugin.SendNotificationAsync
            ' Two parallel paths from a single NotificationContext:
            '   1. Panel refresh trigger — cue the persistent
            '      control panels for an event-driven refresh.
            '   2. Destination dispatch — route the notification
            '      to any configured DiscordBot-transport
            '      destinations whose filters match the event.
            ' These are independent: a panel refresh happens
            ' regardless of whether any destinations exist, and a
            ' destination dispatch happens regardless of whether
            ' any panels match.
            If Not _initialized Then Return Task.FromResult(False)
            If context Is Nothing Then Return Task.FromResult(False)

            ' --- Panel refresh path ---
            ' Only state-change events earn an event-driven panel
            ' refresh. Player join/leave events also drive panels
            ' (player count is one of the four columns) but are
            ' high-volume — fold them into the 60s drift refresh
            ' instead of triggering an immediate edit each time.
            Select Case context.EventType
                Case NotificationEventType.InstanceStarted,
                     NotificationEventType.InstanceStopped,
                     NotificationEventType.InstanceCrashed,
                     NotificationEventType.CrashLoopDetected,
                     NotificationEventType.UpdateCompleted,
                     NotificationEventType.UpdateFailed
                    Try
                        RequestRefreshForAffected(context)
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "RequestRefreshForAffected threw for {Event}",
                            context.EventType)
                    End Try
            End Select

            ' --- Destination dispatch path ---
            ' Skip silently when not connected: queues would just
            ' pile up undeliverable messages. Matches Q6 "drop on
            ' failure for v1" decided in the design doc.
            If Not _connected Then Return Task.FromResult(False)

            Dim destinations As IReadOnlyList(Of BotDestinationCacheEntry)
            Dim profiles As Dictionary(Of String, VisibilityProfileCacheEntry)
            SyncLock _destCacheLock
                destinations = _destinationsCache
                profiles = _destProfilesCache
            End SyncLock

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
                    _logger.LogWarning(ex,
                        "Failed to enqueue notification for destination {Name}",
                        dest.DisplayName)
                End Try
            Next

            Return Task.FromResult(dispatched)
        End Function

        ' ============================================================
        '  Public API for DiscordBotForm
        ' ============================================================

        ''' <summary>
        ''' Called by the Discord Bot form after the operator saves
        ''' bot config or panel changes. Tears down the existing
        ''' connection (if any), reloads from the DB, reconnects.
        ''' Best-effort — never throws.
        ''' </summary>
        Public Async Function ReloadConfigAsync() As Task
            Try
                ' Stop the in-flight reconnect loop so its next
                ' attempt doesn't race ours for _connectionLock.
                ' Worst-case wait here is ConnectAttemptTimeoutSec
                ' (~20s) if the loop is mid-attempt — acceptable
                ' since this is a manual operation (Save / Test
                ' Connection click in the bot form).
                Dim oldCts = _reconnectCts
                Dim oldTask = _reconnectLoopTask
                _reconnectCts = Nothing
                _reconnectLoopTask = Nothing
                Try
                    oldCts?.Cancel()
                Catch
                End Try
                If oldTask IsNot Nothing Then
                    Try
                        Await oldTask
                    Catch
                    End Try
                End If
                Try
                    oldCts?.Dispose()
                Catch
                End Try

                Await DisconnectAsync()

                ' Restart the loop. If the new config still can't
                ' connect (token still wrong, rate limit still
                ' active), the backoff schedule keeps trying
                ' without further operator intervention. The loop
                ' resets attemptIndex to 0, so the operator's
                ' explicit reload bypasses any prior backoff
                ' state — first retry happens after 30s, not the
                ' prior cap.
                _reconnectCts = New CancellationTokenSource()
                _reconnectLoopTask = ReconnectLoopAsync(_reconnectCts.Token)
            Catch ex As Exception
                _logger.LogWarning(ex, "Discord bot reload failed")
            End Try
        End Function

        ''' <summary>
        ''' Tries connecting with the supplied token without
        ''' touching the saved config. Used by the Test Connection
        ''' button on the bot form so the operator can verify a
        ''' token before saving. Returns a result describing the
        ''' outcome and (on success) the list of guilds the bot
        ''' is in — handy for the panel editor's guild dropdown.
        ''' </summary>
        Public Async Function TestConnectionAsync(token As String,
                                                   cancellation As CancellationToken) As Task(Of TestConnectionResult)
            If String.IsNullOrWhiteSpace(token) Then
                Return New TestConnectionResult With {
                    .Success = False,
                    .Message = "Bot token is empty."
                }
            End If

            Dim test As DiscordClient = Nothing
            Dim result As TestConnectionResult = Nothing
            Try
                test = New DiscordClient(New DiscordConfiguration With {
                    .Token = token,
                    .TokenType = TokenType.Bot,
                    .Intents = DiscordIntents.Guilds,
                    .MinimumLogLevel = LogLevel.Warning,
                    .AutoReconnect = False
                })

                Await test.ConnectAsync()

                ' Connect returns as soon as the WebSocket handshake
                ' completes, but guild data arrives over the next
                ' few hundred ms via GUILD_CREATE events. The
                ' obvious approach — subscribe to the Ready event
                ' and Await its TCS — hits a VB.Net snag: the
                ' DSharpPlus AsyncEventHandler(Of TSender, TArgs)
                ' delegate sits in a namespace that isn't picked
                ' up by the standard DSharpPlus imports, and VB
                ' doesn't auto-convert Func(Of ...) into
                ' differently-named compatible delegates.
                ' Sidestepping all of that with a fixed 3-second
                ' wait — this is the one-shot Test Connection
                ' path, not a hot loop, and 3s is comfortably
                ' longer than typical guild-fetch latency.
                Try
                    Await Task.Delay(TimeSpan.FromSeconds(3), cancellation)
                Catch
                    ' Cancellation: the user closed the form mid-test.
                    ' Continue to enumerate whatever guilds we have
                    ' so they get something useful in the UI.
                End Try

                Dim guilds As New List(Of TestGuildInfo)
                For Each kvp In test.Guilds
                    guilds.Add(New TestGuildInfo With {
                        .GuildId = kvp.Key.ToString(),
                        .Name = kvp.Value.Name
                    })
                Next

                Dim botName = If(test.CurrentUser?.Username, "(unknown)")

                result = New TestConnectionResult With {
                    .Success = True,
                    .Message = $"Connected as {botName}. {guilds.Count} guild(s) visible.",
                    .BotUsername = botName,
                    .Guilds = guilds
                }
            Catch ex As UnauthorizedException
                result = New TestConnectionResult With {
                    .Success = False,
                    .Message = "Token rejected by Discord (401 Unauthorized). Check that the token is correct and hasn't been regenerated."
                }
            Catch ex As Exception
                result = New TestConnectionResult With {
                    .Success = False,
                    .Message = $"Connection failed: {ex.Message}"
                }
            End Try

            ' Cleanup runs OUTSIDE the Try because Await is illegal
            ' inside a VB.Net Finally block (BC36943). Both calls
            ' are wrapped in their own Try/Catch so a teardown
            ' failure can't override the result we already produced.
            If test IsNot Nothing Then
                Try
                    Await test.DisconnectAsync()
                Catch
                End Try
                Try
                    test.Dispose()
                Catch
                End Try
            End If

            Return result
        End Function

        ''' <summary>
        ''' Returns the guilds the bot is currently in, with their
        ''' channels — used by the panel editor to populate the
        ''' guild + channel dropdowns without the operator having
        ''' to type IDs by hand.
        ''' </summary>
        Public Function GetGuildsAndChannels() As IReadOnlyList(Of GuildInfo)
            Dim result As New List(Of GuildInfo)
            Dim client = _client
            If client Is Nothing OrElse Not _connected Then Return result

            Try
                For Each kvp In client.Guilds
                    Dim guild = kvp.Value
                    Dim info As New GuildInfo With {
                        .GuildId = guild.Id.ToString(),
                        .Name = guild.Name,
                        .Channels = New List(Of ChannelInfo)
                    }
                    For Each channel In guild.Channels.Values
                        ' Restrict to text-style channels the bot
                        ' could plausibly post a panel into. Voice,
                        ' category, and stage channels are filtered
                        ' out so the panel-editor dropdown isn't
                        ' littered with non-postable options.
                        If channel.Type = ChannelType.Text OrElse
                           channel.Type = ChannelType.News OrElse
                           channel.Type = ChannelType.PublicThread OrElse
                           channel.Type = ChannelType.PrivateThread Then
                            info.Channels.Add(New ChannelInfo With {
                                .ChannelId = channel.Id.ToString(),
                                .Name = channel.Name
                            })
                        End If
                    Next
                    info.Channels = info.Channels.OrderBy(Function(c) c.Name).ToList()
                    result.Add(info)
                Next
            Catch ex As Exception
                _logger.LogWarning(ex, "GetGuildsAndChannels enumeration threw")
            End Try

            Return result.OrderBy(Function(g) g.Name).ToList()
        End Function

        ''' <summary>
        ''' True when the bot is currently logged in to Discord.
        ''' Used by the configuration form to show connection state.
        ''' </summary>
        Public ReadOnly Property IsConnected As Boolean
            Get
                Return _connected
            End Get
        End Property

        ''' <summary>
        ''' UTC timestamp of the most recent successful gateway
        ''' connect, or Nothing if not currently connected. Phase
        ''' 5d-5 item 5 — surfaced by the Discord Bot configuration
        ''' form for uptime display, refreshed by that form's poll
        ''' timer. Reset to Nothing on disconnect or connect-failure
        ''' so a brief reconnect produces a fresh "connected for"
        ''' counter rather than a misleading running total across
        ''' the gap.
        ''' </summary>
        Public ReadOnly Property ConnectedSinceUtc As DateTime?
            Get
                Return _connectedSinceUtc
            End Get
        End Property

        ''' <summary>
        ''' Wall-clock time of the next scheduled reconnect
        ''' attempt, or Nothing if a connect is currently in
        ''' flight, the loop has stopped (Connected,
        ''' TokenRejected, post-Shutdown), or the loop hasn't
        ''' started yet. Read by the bot configuration form to
        ''' display a live "next attempt in Ns" countdown.
        ''' </summary>
        Public ReadOnly Property NextConnectAttemptUtc As DateTime?
            Get
                Return _nextConnectAttemptUtc
            End Get
        End Property

        ''' <summary>
        ''' True when the most recent connect attempt hit a
        ''' Discord rate limit (HTTP 429 on /gateway/bot). The
        ''' reconnect loop is waiting on Discord's Retry-After
        ''' value; NextConnectAttemptUtc gives the wake-up time.
        ''' Cleared when a subsequent attempt succeeds or fails
        ''' for a non-rate-limit reason.
        ''' </summary>
        Public ReadOnly Property IsRateLimited As Boolean
            Get
                Return Not _connected AndAlso _lastConnectRateLimitWaitSec > 0
            End Get
        End Property

        ''' <summary>
        ''' True when Discord rejected the configured token
        ''' (HTTP 401 on /gateway/bot). The reconnect loop has
        ''' exited; the operator must update the token via the
        ''' bot form (which calls ReloadConfigAsync and restarts
        ''' the loop) to recover.
        ''' </summary>
        Public ReadOnly Property IsTokenRejected As Boolean
            Get
                Return _lastConnectFatalAuth
            End Get
        End Property

        ''' <summary>
        ''' Marks every loaded panel as needing a refresh. Used
        ''' after the operator saves panel configuration changes
        ''' so visible state on Discord catches up immediately
        ''' rather than waiting for the next 60s tick.
        ''' </summary>
        Public Sub RequestRefreshAllPanels()
            For Each rt In _panels.Values
                rt.PendingRefresh = True
            Next
        End Sub

        ''' <summary>
        ''' Public reload entry point for the role mappings UI.
        ''' Re-reads DiscordRoleMappings from the DB and refreshes
        ''' the in-memory cache. Called after Add/Edit/Remove
        ''' operations in DiscordRoleMappingsForm so the bot picks
        ''' up the change without waiting for the next reconnect.
        ''' Sync work wrapped in a Task for API consistency with
        ''' ReloadConfigAsync — LoadRoleMappingsFromDb already
        ''' handles its own exceptions, so callers won't see them.
        ''' </summary>
        Public Function ReloadRoleMappingsAsync() As Task
            LoadRoleMappingsFromDb()
            Return Task.CompletedTask
        End Function

        ''' <summary>
        ''' Returns the list of assignable roles in the given
        ''' guild, for use by the role mapping configuration UI.
        ''' Reads from DSharpPlus's local guild cache (populated
        ''' via the Guilds intent we open the connection with)
        ''' so no REST call is needed and the call is cheap to
        ''' use during UI dropdown population. Returns an empty
        ''' list when the bot isn't connected or isn't in the
        ''' guild — the UI should treat empty as "can't add new
        ''' mappings" and surface a hint.
        '''
        ''' Filters out:
        '''   - The @everyone role (RoleId == GuildId): mapping
        '''     it to ServerOperator/Administrator would grant
        '''     that elevation to literally every guild member,
        '''     a footgun the UI shouldn't make easy to fire.
        '''   - Managed roles (DiscordRole.IsManaged): integration
        '''     roles automatically managed by bots / boosters /
        '''     subscriptions. Can't be assigned manually, so a
        '''     mapping on them would never match a real user.
        '''
        ''' Sorted alphabetically by name for stable UI ordering.
        ''' </summary>
        Public Function GetGuildRoles(guildId As String) As IReadOnlyList(Of GuildRoleInfo)
            Dim result As New List(Of GuildRoleInfo)
            Dim client = _client
            If client Is Nothing OrElse Not _connected Then Return result

            Try
                Dim parsed As ULong
                If Not ULong.TryParse(guildId, parsed) Then Return result

                Dim guild As DiscordGuild = Nothing
                If Not client.Guilds.TryGetValue(parsed, guild) Then Return result

                For Each kvp In guild.Roles
                    Dim role = kvp.Value
                    If role Is Nothing Then Continue For
                    If role.Id = guild.Id Then Continue For
                    If role.IsManaged Then Continue For
                    result.Add(New GuildRoleInfo With {
                        .RoleId = role.Id.ToString(),
                        .Name = role.Name
                    })
                Next
            Catch ex As Exception
                _logger.LogWarning(ex, "GetGuildRoles failed for {Guild}", guildId)
            End Try

            Return result.OrderBy(Function(r) r.Name).ToList()
        End Function

        ' ============================================================
        '  Connection management
        ' ============================================================

        ' --- Pre-flight rate-limit probe ---
        '
        ' DSharpPlus 4.5.0's ConnectAsync swallows HTTP 429
        ' responses on /gateway/bot and retries internally with
        ' no exit. To observe the actual Retry-After value
        ' Discord sends, we probe the same endpoint ourselves
        ' with a plain HttpClient before invoking DSharpPlus.
        ' The probe result lets us:
        '   - Skip the DSharpPlus connect entirely when we know
        '     we're rate-limited (no point hammering further).
        '   - Wait exactly as long as Discord asks via
        '     Retry-After, instead of guessing with a fixed
        '     schedule.
        '   - Detect a rejected token (401) and halt the
        '     reconnect loop instead of retrying uselessly
        '     until the operator updates the token.
        '
        ' Reference: https://docs.discord.com/developers/topics/rate-limits
        '
        ' HttpClient is a static singleton because per-instance
        ' HttpClients leak sockets via TIME_WAIT exhaustion under
        ' repeated reconnect cycles. Authorization is per-request
        ' (set on each HttpRequestMessage) since the token can
        ' change when the operator updates config.

        Private Shared ReadOnly s_probeHttp As HttpClient = BuildProbeHttpClient()

        Private Shared Function BuildProbeHttpClient() As HttpClient
            Dim client As New HttpClient()
            client.Timeout = TimeSpan.FromSeconds(15)
            ' Discord requires User-Agent on every request.
            ' Format from https://docs.discord.com/developers/reference#user-agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "DiscordBot (https://github.com/Site/PowerGSM, 0.1.0)")
            Return client
        End Function

        Private Enum ProbeStatus
            Ready
            RateLimited
            Unauthorized
            NetworkOrOtherError
        End Enum

        Private Class GatewayProbeResult
            Public Property Status As ProbeStatus
            Public Property RetryAfterSeconds As Double
            Public Property StatusCode As Integer
        End Class

        Private Async Function ProbeGatewayAsync(token As String) As Task(Of GatewayProbeResult)
            Dim result As New GatewayProbeResult With {.Status = ProbeStatus.NetworkOrOtherError}
            Try
                Using request As New HttpRequestMessage(
                    HttpMethod.Get,
                    "https://discord.com/api/v10/gateway/bot")
                    request.Headers.Authorization = New AuthenticationHeaderValue("Bot", token)
                    Dim response = Await s_probeHttp.SendAsync(request)
                    Try
                        result.StatusCode = CInt(response.StatusCode)
                        If response.StatusCode = HttpStatusCode.OK Then
                            result.Status = ProbeStatus.Ready
                        ElseIf CInt(response.StatusCode) = 429 Then
                            result.Status = ProbeStatus.RateLimited
                            ' Read body once — used both for
                            ' Retry-After fallback parsing AND for
                            ' the diagnostic dump below.
                            Dim bodyText As String = ""
                            Try
                                bodyText = Await response.Content.ReadAsStringAsync()
                            Catch
                            End Try
                            result.RetryAfterSeconds = ParseRetryAfterSeconds(response, bodyText)
                            DumpRateLimitResponseToLog(response, bodyText, result.RetryAfterSeconds)
                        ElseIf response.StatusCode = HttpStatusCode.Unauthorized Then
                            result.Status = ProbeStatus.Unauthorized
                        Else
                            result.Status = ProbeStatus.NetworkOrOtherError
                        End If
                    Finally
                        response.Dispose()
                    End Try
                End Using
            Catch ex As Exception
                ' Network error, DNS failure, timeout, etc. The
                ' probe is advisory — fall through to letting
                ' DSharpPlus attempt the connect anyway. Logged
                ' at Debug level to avoid file-log noise on
                ' transient blips.
                _logger.LogDebug(ex, "Discord gateway probe failed; will let DSharpPlus attempt anyway")
                result.Status = ProbeStatus.NetworkOrOtherError
            End Try
            Return result
        End Function

        ''' <summary>
        ''' Parses Retry-After from a 429 response. Discord can
        ''' return the value in any of three places, and we've
        ''' seen real cases where the .NET-parsed header value
        ''' under-reports the actual reset window (e.g. integer
        ''' truncation of a fractional value, or proxy
        ''' interference). Tries all three sources:
        '''
        '''   1. .NET's RetryAfterHeaderValue.Delta (RFC 7231
        '''      integer-seconds form)
        '''   2. Raw "Retry-After" header value parsed as Double
        '''      (handles Discord's fractional form, which
        '''      .NET's strict parser rejects)
        '''   3. JSON body's "retry_after" field (always a Number,
        '''      typically the most precise of the three)
        '''
        ''' Returns the LARGEST value found across all sources —
        ''' under-waiting lands us right back on the same bucket
        ''' for another 429 (the 5s-reset symptom seen in the
        ''' field). Better to over-wait by a fraction of a
        ''' second than wake too soon. Falls back to 60s if no
        ''' source produces a value.
        ''' </summary>
        Private Function ParseRetryAfterSeconds(response As HttpResponseMessage, bodyText As String) As Double
            Dim values As New List(Of Double)

            ' Source 1: .NET strict parser (integer only).
            If response.Headers.RetryAfter IsNot Nothing AndAlso
               response.Headers.RetryAfter.Delta.HasValue Then
                values.Add(response.Headers.RetryAfter.Delta.Value.TotalSeconds)
            End If

            ' Source 2: raw header value, handles fractional
            ' seconds the strict parser rejects.
            Dim raw As IEnumerable(Of String) = Nothing
            If response.Headers.TryGetValues("Retry-After", raw) Then
                Dim s = raw.FirstOrDefault()
                If Not String.IsNullOrEmpty(s) Then
                    Dim parsed As Double
                    If Double.TryParse(s,
                                       Globalization.NumberStyles.Float,
                                       Globalization.CultureInfo.InvariantCulture,
                                       parsed) Then
                        values.Add(parsed)
                    End If
                End If
            End If

            ' Source 3: JSON body's retry_after.
            If Not String.IsNullOrEmpty(bodyText) Then
                Try
                    Using doc = JsonDocument.Parse(bodyText)
                        Dim el As JsonElement
                        If doc.RootElement.TryGetProperty("retry_after", el) AndAlso
                           el.ValueKind = JsonValueKind.Number Then
                            values.Add(el.GetDouble())
                        End If
                    End Using
                Catch
                End Try
            End If

            If values.Count = 0 Then Return 60
            Return values.Max()
        End Function

        ''' <summary>
        ''' Dumps a 429 probe response to the manager log at
        ''' Warning level so the operator can see exactly what
        ''' Discord returned. Includes every response and content
        ''' header (X-RateLimit-Bucket, X-RateLimit-Scope,
        ''' X-RateLimit-Reset-After, etc.), the body, and our
        ''' parsed Retry-After value. Warning level keeps it
        ''' visible at default log filtering — rate-limit hits
        ''' are rare enough that the noise is justified for
        ''' diagnostic value.
        ''' </summary>
        Private Sub DumpRateLimitResponseToLog(response As HttpResponseMessage,
                                                bodyText As String,
                                                parsedRetrySec As Double)
            Dim headers As New StringBuilder()
            For Each h In response.Headers
                headers.AppendLine($"  {h.Key}: {String.Join("; ", h.Value)}")
            Next
            For Each h In response.Content.Headers
                headers.AppendLine($"  {h.Key}: {String.Join("; ", h.Value)}")
            Next
            _logger.LogWarning(
                "Discord /gateway/bot 429 response dump:" & vbCrLf &
                "  Status: {Status}" & vbCrLf &
                "  Headers:" & vbCrLf &
                "{Headers}" &
                "  Body: {Body}" & vbCrLf &
                "  Parsed Retry-After (max across header/raw/body): {RetrySec}s",
                CInt(response.StatusCode),
                headers.ToString(),
                bodyText,
                parsedRetrySec)
        End Sub

        Private Async Function ConnectAsync() As Task
            ' Clear retry-hint state at the top of every attempt
            ' so ReconnectLoopAsync sees only what THIS attempt
            ' produced, not stale values from a prior attempt.
            _lastConnectRateLimitWaitSec = 0
            _lastConnectFatalAuth = False

            Await _connectionLock.WaitAsync()
            Try
                ' Tear down any prior client.
                Await DisconnectInternalAsync()

                ' Read fresh config from DB.
                Dim cfg As DiscordBotConfigEntity = Nothing
                Dim panels As List(Of DiscordPanelEntity) = Nothing
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    cfg = db.DiscordBotConfigs.FirstOrDefault()
                    panels = db.DiscordPanels.ToList()
                End Using

                If cfg Is Nothing OrElse Not cfg.Enabled Then
                    _logger.LogInformation("Discord bot not enabled — skipping connect")
                    LoadPanelRuntimes(panels)
                    Return
                End If

                Dim token As String = ""
                If cfg.EncryptedToken IsNot Nothing AndAlso cfg.EncryptedToken.Length > 0 Then
                    Try
                        token = CredentialService.UnprotectString(cfg.EncryptedToken)
                    Catch ex As Exception
                        _logger.LogWarning(ex, "Discord bot token decrypt failed — was it encrypted under a different Windows account?")
                    End Try
                End If

                If String.IsNullOrWhiteSpace(token) Then
                    _logger.LogInformation("Discord bot has no token configured — staying disconnected")
                    LoadPanelRuntimes(panels)
                    Return
                End If

                ' Pre-flight rate-limit probe. See class-level
                ' comment block above for design rationale.
                Dim probe = Await ProbeGatewayAsync(token)
                Select Case probe.Status
                    Case ProbeStatus.RateLimited
                        _lastConnectRateLimitWaitSec = probe.RetryAfterSeconds
                        _logger.LogWarning(
                            "Discord rate-limited on /gateway/bot; backing off {Sec}s per Retry-After",
                            CInt(probe.RetryAfterSeconds))
                        LoadPanelRuntimes(panels)
                        Return
                    Case ProbeStatus.Unauthorized
                        _lastConnectFatalAuth = True
                        _logger.LogWarning(
                            "Discord rejected token (401 on /gateway/bot probe); halting reconnect loop")
                        LoadPanelRuntimes(panels)
                        Return
                    Case ProbeStatus.NetworkOrOtherError
                        ' Probe failed for non-rate-limit reasons.
                        ' Don't pre-empt — let DSharpPlus try; it
                        ' has its own DNS/network resilience and
                        ' might succeed where our probe didn't.
                End Select

                Dim loggerFactory = _serviceProvider.GetService(Of ILoggerFactory)()
                Dim newClient As New DiscordClient(New DiscordConfiguration With {
                    .Token = token,
                    .TokenType = TokenType.Bot,
                    .Intents = DiscordIntents.Guilds,
                    .LoggerFactory = loggerFactory,
                    .MinimumLogLevel = LogLevel.Warning,
                    .AutoReconnect = True
                })

                ' Component (button / select) interactions go through
                ' a single handler. The Manage button is the only
                ' interactive component in 5d-1; 5d-2 will route
                ' instance-selection and action-button clicks through
                ' the same handler keyed by custom-id namespace.
                AddHandler newClient.ComponentInteractionCreated, AddressOf OnComponentInteractionCreated

                ' Slash command framework setup. Must happen BEFORE
                ' ConnectAsync so the extension's Ready hook is in
                ' place when the gateway reports ready. Per-guild
                ' command registration runs post-connect once the
                ' guild list has populated (TryRegisterSlashCommandsAsync
                ' below); the extension queues registrations and
                ' RefreshCommands triggers the actual REST upload.
                ' Services flow lets command modules and the
                ' autocomplete provider resolve DiscordBotPlugin /
                ' InstanceManager / GsmDbContext via ctx.Services.
                Dim newSlash As SlashCommandsExtension = Nothing
                Try
                    newSlash = newClient.UseSlashCommands(New SlashCommandsConfiguration With {
                        .Services = _serviceProvider
                    })
                Catch ex As Exception
                    ' Extension setup failure is non-fatal — the
                    ' bot's panel surface keeps working without
                    ' slash commands. Log and continue.
                    _logger.LogWarning(ex, "UseSlashCommands threw — slash commands will be unavailable")
                End Try

                ' Bound the connect attempt externally. DSharpPlus
                ' 4.5.0's newClient.ConnectAsync() has no internal
                ' timeout — under HTTP 429 on /gateway/bot it
                ' retries every ~3s indefinitely. We race it
                ' against a delay, and if the delay wins, dispose
                ' the client to halt DSharpPlus's retry loop and
                ' throw a TimeoutException. The existing Catch
                ' below logs at warning and leaves _connected =
                ' False for ReconnectLoopAsync to notice and back
                ' off on.
                Dim connectTask = newClient.ConnectAsync()
                Dim timeoutTask = Task.Delay(TimeSpan.FromSeconds(ConnectAttemptTimeoutSec))
                Dim winner = Await Task.WhenAny(connectTask, timeoutTask)
                If winner Is timeoutTask Then
                    Try
                        newClient.Dispose()
                    Catch
                    End Try
                    Throw New TimeoutException(
                        $"Discord bot connect did not complete within {ConnectAttemptTimeoutSec}s")
                End If
                ' Surface any exception the connect raised
                ' (UnauthorizedException, network errors, etc).
                Await connectTask

                _client = newClient
                _slashCommands = newSlash
                _connected = True
                _connectedSinceUtc = DateTime.UtcNow
                _logger.LogInformation("Discord bot connected as {User} — {GuildCount} guild(s)",
                    newClient.CurrentUser?.Username, newClient.Guilds?.Count)

                LoadPanelRuntimes(panels)
                LoadRoleMappingsFromDb()
                LoadDestinationsFromDb()

                ' Mark every panel as pending so the next loop tick
                ' posts/edits them. We don't fire posts here because
                ' on a fresh connect, guilds arrive asynchronously
                ' over the next ~second via GUILD_CREATE events;
                ' deferring to the loop lets the gateway settle.
                RequestRefreshAllPanels()

                ' Set the bot's avatar from the PowerGSM icon if it
                ' doesn't already have a custom one. Fire-and-forget
                ' — the call defers via Task.Delay to let CurrentUser
                ' populate, then checks AvatarHash before uploading.
                ' Never blocks ConnectAsync.
                Dim _unused = TrySetBotAvatarAsync()

                ' Register slash commands per-guild. Same
                ' fire-and-forget pattern as the avatar setup
                ' — needs a brief delay for GUILD_CREATE events
                ' to populate _client.Guilds, then iterates and
                ' registers. Failures degrade to "slash commands
                ' don't appear in this guild" rather than
                ' breaking the rest of the bot.
                Dim _unused2 = TryRegisterSlashCommandsAsync()
            Catch ex As Exception
                _logger.LogWarning(ex, "Discord bot connect failed")
                _connected = False
                _connectedSinceUtc = Nothing
            Finally
                _connectionLock.Release()
            End Try
        End Function

        Private Async Function DisconnectAsync() As Task
            Await _connectionLock.WaitAsync()
            Try
                Await DisconnectInternalAsync()
            Finally
                _connectionLock.Release()
            End Try
        End Function

        ' Caller MUST hold _connectionLock.
        Private Async Function DisconnectInternalAsync() As Task
            Dim client = _client
            _client = Nothing
            ' Clearing _slashCommands here is enough — the extension
            ' is owned by the DiscordClient and disposes with it,
            ' so we don't (and can't) dispose it directly.
            _slashCommands = Nothing
            _connected = False
            _connectedSinceUtc = Nothing
            If client Is Nothing Then Return
            Try
                RemoveHandler client.ComponentInteractionCreated, AddressOf OnComponentInteractionCreated
            Catch
            End Try
            Try
                Await client.DisconnectAsync()
            Catch ex As Exception
                _logger.LogDebug(ex, "Discord bot disconnect threw — ignoring")
            End Try
            Try
                client.Dispose()
            Catch
            End Try
        End Function

        ''' <summary>
        ''' Fire-and-forget background helper: set the bot's profile
        ''' picture to the PowerGSM icon if it doesn't already have
        ''' a custom avatar. Called once after each successful
        ''' connect. Idempotent across reconnects via the
        ''' AvatarHash gate, which is important because Discord
        ''' rate-limits avatar changes hard (≈5-10/hour); blindly
        ''' uploading on every connect would lock us out.
        '''
        ''' Behaviour:
        '''   - AvatarHash null/empty (Discord default avatar) →
        '''     load the embedded PowerGSM.ico, render its largest
        '''     size as a Bitmap, encode to PNG, upload.
        '''   - AvatarHash non-empty (any avatar already set) →
        '''     skip silently. This respects custom avatars set
        '''     by the operator through the Discord developer
        '''     portal.
        '''
        ''' Edge case: an operator who explicitly removed our
        ''' avatar to revert to Discord's default would see us
        ''' re-apply it on the next manager restart. Acceptable
        ''' v1 trade-off — the dev portal is the canonical place
        ''' to override if they want a different image; reverting
        ''' to the Discord default while running PowerGSM is
        ''' unusual.
        '''
        ''' All exceptions are caught and logged at warning. The
        ''' bot is fully functional without an avatar; this is
        ''' branding polish, not a hard requirement.
        ''' </summary>
        Private Async Function TrySetBotAvatarAsync() As Task
            Try
                ' Brief delay so CurrentUser has time to populate
                ' fully. ConnectAsync returns once the WebSocket
                ' handshake completes, but Ready/READY-derived
                ' fields (including the user's own AvatarHash)
                ' arrive over the next few hundred ms. Same 3s
                ' wait pattern TestConnectionAsync uses for guild
                ' enumeration.
                Await Task.Delay(TimeSpan.FromSeconds(3))

                Dim client = _client
                If client Is Nothing OrElse Not _connected Then
                    ' Disconnected during the delay window — typically
                    ' caused by a startup-time double-init or a manual
                    ' Test Connection cycle. The next successful
                    ' connect will fire its own TrySetBotAvatarAsync.
                    _logger.LogInformation("Bot avatar setup skipped: not connected after 3s warm-up (likely reconnect in progress)")
                    Return
                End If

                Dim botUser = client.CurrentUser
                If botUser Is Nothing Then
                    _logger.LogInformation("Bot avatar setup skipped: CurrentUser not yet populated")
                    Return
                End If

                ' AvatarHash is null/empty when the bot is using
                ' Discord's default avatar; any custom avatar
                ' (set by us or by the operator via the dev
                ' portal) populates it. Skip the upload in that
                ' case to respect operator overrides AND avoid
                ' the avatar-change rate limit on every connect.
                If Not String.IsNullOrEmpty(botUser.AvatarHash) Then
                    _logger.LogInformation("Bot avatar already set ({Hash}); skipping default-icon upload",
                        botUser.AvatarHash)
                    Return
                End If

                ' Load the largest variant of PowerGSM.ico as a
                ' Bitmap. FormIconHelper handles the embedded-
                ' resource lookup and the Icon → Bitmap conversion;
                ' returns Nothing if the resource is missing or
                ' fails to load. We dispose the Bitmap via Using
                ' since GetLargeBitmap transfers ownership.
                Dim bmp = GSM.Manager.UI.FormIconHelper.GetLargeBitmap()
                If bmp Is Nothing Then
                    _logger.LogWarning("Bot avatar setup skipped: PowerGSM icon resource not available")
                    Return
                End If

                ' Render to PNG bytes in memory. Discord accepts
                ' PNG/JPG/GIF for avatars; PNG preserves the icon's
                ' alpha channel cleanly. The MemoryStream stays
                ' open across the upload Await because
                ' UpdateCurrentUserAsync reads the stream and
                ' base64-encodes it before sending.
                Dim pngBytes As Integer = 0
                Using bmp
                    Using ms As New MemoryStream()
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png)
                        pngBytes = CInt(ms.Length)
                        ms.Position = 0
                        ' Optional(Of Stream) is fully qualified to
                        ' avoid VB.Net confusing the type name with
                        ' the Optional parameter keyword in some
                        ' positions. Constructor wraps the stream
                        ' so DSharpPlus's ImageTool can read+encode
                        ' it on the way out to the Discord API.
                        Await client.UpdateCurrentUserAsync(
                            avatar:=New DSharpPlus.Entities.Optional(Of Stream)(ms))
                    End Using
                End Using

                _logger.LogInformation("Set Discord bot avatar to PowerGSM icon ({Bytes} bytes PNG)",
                    pngBytes)
            Catch ex As Exception
                ' Most likely failure modes:
                '   • Discord rate limit (HTTP 429) — we shouldn't
                '     hit this with the AvatarHash gate, but a
                '     burst of disconnect/reconnect cycles could
                '     in theory.
                '   • Permission error — not expected for editing
                '     own profile, but logged here for diagnosis.
                '   • Image-format rejection — PNG is supported,
                '     so this would only happen if the icon load
                '     produced an unusable bitmap.
                ' Log at warning rather than error — the bot is
                ' fully functional without the avatar.
                _logger.LogWarning(ex, "Failed to set Discord bot avatar")
            End Try
        End Function

        ''' <summary>
        ''' Phase 5d-4 — fire-and-forget background helper:
        ''' register the slash command set per-guild after
        ''' connect. Per-guild registration gives instant
        ''' propagation (vs global's up-to-an-hour cache);
        ''' the trade-off is that we have to wait for
        ''' GUILD_CREATE events to populate _client.Guilds before
        ''' we know which guilds to target.
        '''
        ''' Same 3-second warm-up trick as TestConnectionAsync
        ''' and TrySetBotAvatarAsync — sidesteps the AsyncEventHandler
        ''' delegate quirk in VB.Net by using a fixed delay
        ''' instead of subscribing to Ready.
        '''
        ''' RegisterCommands accumulates registrations on the
        ''' extension's internal list; RefreshCommands triggers
        ''' the actual REST upload to Discord. Without
        ''' RefreshCommands, registrations queued post-connect
        ''' would sit unused (the extension's own Ready hook
        ''' has already fired and processed an empty list by
        ''' the time we add to it).
        '''
        ''' Failures are isolated per-guild: a single guild that
        ''' the bot can't register against doesn't prevent other
        ''' guilds from getting commands. The whole helper is
        ''' wrapped in a top-level Try so an unexpected failure
        ''' degrades to "no slash commands" rather than crashing
        ''' the connect path.
        ''' </summary>
        Private Async Function TryRegisterSlashCommandsAsync() As Task
            Try
                Await Task.Delay(TimeSpan.FromSeconds(3))

                Dim slash = _slashCommands
                Dim client = _client
                If slash Is Nothing OrElse client Is Nothing OrElse Not _connected Then
                    _logger.LogInformation(
                        "Slash command registration skipped: not connected after warm-up")
                    Return
                End If

                Dim registered = 0
                For Each kvp In client.Guilds
                    Try
                        slash.RegisterCommands(Of GsmSlashCommands)(kvp.Key)
                        registered += 1
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "Failed to queue slash commands for guild {Guild}",
                            kvp.Key)
                    End Try
                Next

                If registered = 0 Then
                    _logger.LogInformation(
                        "Slash command registration: no guilds visible after warm-up")
                    Return
                End If

                Try
                    Await slash.RefreshCommands()
                    _logger.LogInformation(
                        "Registered slash commands for {Count} guild(s)", registered)
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "Slash command upload (RefreshCommands) failed")
                End Try
            Catch ex As Exception
                _logger.LogWarning(ex, "TryRegisterSlashCommandsAsync threw")
            End Try
        End Function

        ''' <summary>
        ''' Phase 5d-4 — enumerate the instances visible in a
        ''' specific guild via that guild's panels. Used by
        ''' /players' autocomplete provider (each keystroke) and
        ''' the command body (eligibility re-check after the user
        ''' picks an option). Per-guild scoping respects the
        ''' operator's per-guild visibility choices: a user in
        ''' guild A only sees instances exposed via panels in
        ''' guild A, even if they have ServerOperator there
        ''' — per-panel role overrides (the v2 follow-on for Q5)
        ''' would refine this further.
        '''
        ''' Returns deduped (InstanceId, DisplayName) pairs sorted
        ''' by DisplayName. Empty when no panels exist in the
        ''' guild or none of their scopes resolve to anything.
        ''' Friend so GsmSlashCommands and InstanceAutocompleteProvider
        ''' in the same namespace can reach it.
        ''' </summary>
        Friend Function GetInstancesVisibleInGuild(guildId As String) _
                As List(Of InstanceLookupEntry)
            Dim result As New Dictionary(Of String, InstanceLookupEntry)(StringComparer.OrdinalIgnoreCase)
            If String.IsNullOrEmpty(guildId) Then
                Return New List(Of InstanceLookupEntry)
            End If

            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim panels = db.DiscordPanels.
                        Where(Function(p) p.GuildId = guildId).ToList()

                    For Each p In panels
                        Dim query = db.Instances.AsQueryable()
                        Select Case (If(p.ScopeKind, "")).ToLowerInvariant()
                            Case "allinstances", ""
                                ' no filter
                            Case "game"
                                query = query.Where(Function(i) i.GameId = p.ScopeTargetId)
                            Case "installation"
                                query = query.Where(Function(i) i.InstallationId = p.ScopeTargetId)
                            Case "instanceset"
                                query = query.Where(Function(i) i.InstanceSetTag = p.ScopeTargetId)
                            Case Else
                                ' Unknown scope kind — same
                                ' defensive skip ResolveInScopeInstances
                                ' uses for the panel rendering path.
                                Continue For
                        End Select
                        For Each inst In query.ToList()
                            If Not result.ContainsKey(inst.InstanceId) Then
                                result(inst.InstanceId) = New InstanceLookupEntry With {
                                    .InstanceId = inst.InstanceId,
                                    .DisplayName = If(inst.DisplayName, inst.InstanceId)
                                }
                            End If
                        Next
                    Next
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "GetInstancesVisibleInGuild failed for guild {Guild}", guildId)
            End Try

            Return result.Values.OrderBy(Function(x) x.DisplayName).ToList()
        End Function

        ' ============================================================
        '  Panel refresh loop
        ' ============================================================

        Private Sub LoadPanelRuntimes(panels As List(Of DiscordPanelEntity))
            _panels.Clear()
            If panels Is Nothing Then Return
            For Each p In panels
                _panels(p.PanelId) = New PanelRuntime With {
                    .PanelId = p.PanelId,
                    .PendingRefresh = True,
                    .LastRefreshUtc = DateTime.MinValue
                }
            Next
        End Sub

        ''' <summary>
        ''' Load DiscordRoleMappings from the DB into the
        ''' in-memory cache. Called from ConnectAsync after a
        ''' successful connect and from ReloadRoleMappingsAsync
        ''' on UI save. Builds a fresh staging dict, then reconciles
        ''' with the live cache: each guild's mappings are replaced
        ''' wholesale (atomic value-swap on the outer ConcurrentDictionary
        ''' indexer); guilds present in the cache but not in the
        ''' staging set are removed. The window where a read could
        ''' see partial state is microseconds at most — see the
        ''' _roleMappings field comment for the trade-off discussion.
        '''
        ''' Catches all exceptions internally: a failed load (e.g.
        ''' the migration for the table hasn't been run yet)
        ''' surfaces as an empty cache, which means everyone
        ''' resolves to the Everyone tier and no elevations
        ''' work. The operator notices when their action buttons
        ''' don't respond, runs the migration, and reconnects.
        ''' Better than failing the connect over a missing table.
        ''' </summary>
        Private Sub LoadRoleMappingsFromDb()
            Try
                ' Two-level staging structure: outer keyed by
                ' GuildId, middle keyed by PanelId ("" for
                ' guild-default), inner Dictionary as before.
                Dim staging As New Dictionary(Of String, Dictionary(Of String, Dictionary(Of String, CommandPermission)))(StringComparer.Ordinal)

                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim rows = db.DiscordRoleMappings.ToList()
                    For Each m In rows
                        Dim guildScope As Dictionary(Of String, Dictionary(Of String, CommandPermission)) = Nothing
                        If Not staging.TryGetValue(m.GuildId, guildScope) Then
                            guildScope = New Dictionary(Of String, Dictionary(Of String, CommandPermission))(StringComparer.Ordinal)
                            staging(m.GuildId) = guildScope
                        End If
                        ' Defensive: NULL shouldn't reach us (the
                        ' migration set NOT NULL with default ""),
                        ' but normalise anyway so the dict key is
                        ' never Nothing.
                        Dim panelKey = If(m.PanelId, "")
                        Dim panelScope As Dictionary(Of String, CommandPermission) = Nothing
                        If Not guildScope.TryGetValue(panelKey, panelScope) Then
                            panelScope = New Dictionary(Of String, CommandPermission)(StringComparer.Ordinal)
                            guildScope(panelKey) = panelScope
                        End If
                        panelScope(m.RoleId) = CType(m.Permission, CommandPermission)
                    Next
                End Using

                ' Apply staging to the live cache. Replace each
                ' guild's two-level inner structure with the
                ' freshly-built one, then drop any guilds that
                ' vanished from staging.
                For Each kvp In staging
                    _roleMappings(kvp.Key) = kvp.Value
                Next
                For Each existingGuildId In _roleMappings.Keys.ToList()
                    If Not staging.ContainsKey(existingGuildId) Then
                        Dim removed As Dictionary(Of String, Dictionary(Of String, CommandPermission)) = Nothing
                        _roleMappings.TryRemove(existingGuildId, removed)
                    End If
                Next

                Dim totalMappings = staging.Sum(
                    Function(g) g.Value.Sum(Function(p) p.Value.Count))
                Dim totalScopes = staging.Sum(Function(g) g.Value.Count)
                _logger.LogInformation(
                    "Loaded {Count} Discord role mapping(s) across {Guilds} guild(s) and {Scopes} scope(s) (incl. panel overrides)",
                    totalMappings, staging.Count, totalScopes)
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to load Discord role mappings from DB")
            End Try
        End Sub

        Private Sub StartRefreshLoop()
            If _refreshCts IsNot Nothing Then Return
            _refreshCts = New CancellationTokenSource()
            Dim token = _refreshCts.Token
            _refreshLoopTask = Task.Run(Function() RefreshLoopAsync(token))
        End Sub

        Private Sub StopRefreshLoop()
            Dim cts = _refreshCts
            If cts Is Nothing Then Return
            _refreshCts = Nothing
            Try
                cts.Cancel()
            Catch
            End Try
            Try
                If _refreshLoopTask IsNot Nothing Then _refreshLoopTask.Wait(TimeSpan.FromSeconds(5))
            Catch
            End Try
            Try
                cts.Dispose()
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' Single global tick (1s). Iterates every panel runtime
        ''' and decides whether to refresh based on:
        '''   a) PendingRefresh AND (now - LastRefreshUtc) >= 5s
        '''      — event-driven refresh respecting the rate-limit
        '''      floor, OR
        '''   b) (now - LastRefreshUtc) >= panel.RefreshIntervalSeconds
        '''      — time-driven drift refresh for player-count and
        '''      time-relative cells that don't fire emitter events.
        ''' Refreshes serialise via PanelRuntime.RefreshLock so a
        ''' slow Discord request can't pile up overlapping edits
        ''' for the same panel.
        ''' </summary>
        Private Async Function RefreshLoopAsync(token As CancellationToken) As Task
            Try
                While Not token.IsCancellationRequested
                    Try
                        If _connected Then Await TickAsync(token)
                    Catch ex As Exception
                        _logger.LogWarning(ex, "Discord refresh loop tick threw")
                    End Try
                    Try
                        Await Task.Delay(RefreshTickMs, token)
                    Catch
                        Exit While
                    End Try
                End While
            Catch
            End Try
        End Function

        Private Async Function TickAsync(token As CancellationToken) As Task
            ' Snapshot the panel list and read each panel's row
            ' once per tick. Reading from the DB on every tick is
            ' fine — SQLite is local and these reads are cheap —
            ' and it keeps Edit-while-running scenarios honest:
            ' if the operator just changed a panel's scope, the
            ' next refresh sees the new scope.
            Dim panelEntities As List(Of DiscordPanelEntity) = Nothing
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    panelEntities = db.DiscordPanels.ToList()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Discord tick: failed to load panels")
                Return
            End Try

            ' Reconcile runtime state with DB state. Panels added
            ' since last tick get a fresh PanelRuntime; panels
            ' removed get their runtime dropped. Avoids stale
            ' state if the operator deletes a panel between ticks.
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            For Each p In panelEntities
                seen.Add(p.PanelId)
                If Not _panels.ContainsKey(p.PanelId) Then
                    _panels(p.PanelId) = New PanelRuntime With {
                        .PanelId = p.PanelId,
                        .PendingRefresh = True,
                        .LastRefreshUtc = DateTime.MinValue
                    }
                End If
            Next
            For Each existing In _panels.Keys.ToList()
                If Not seen.Contains(existing) Then
                    Dim removed As PanelRuntime = Nothing
                    _panels.TryRemove(existing, removed)
                End If
            Next

            For Each p In panelEntities
                If token.IsCancellationRequested Then Return

                Dim rt As PanelRuntime = Nothing
                If Not _panels.TryGetValue(p.PanelId, rt) Then Continue For

                Dim now = DateTime.UtcNow
                Dim cooldownPassed = (now - rt.LastRefreshUtc).TotalMilliseconds >= PanelEditCooldownMs
                Dim driftSeconds = If(p.RefreshIntervalSeconds > 0,
                                       p.RefreshIntervalSeconds, DefaultDriftRefreshSeconds)
                Dim driftDue = (now - rt.LastRefreshUtc).TotalSeconds >= driftSeconds

                Dim shouldRefresh = (rt.PendingRefresh AndAlso cooldownPassed) OrElse driftDue
                If Not shouldRefresh Then Continue For

                ' Take the per-panel lock without waiting; if a
                ' previous refresh is still in flight, skip this
                ' tick — the next tick will pick up the work.
                If Not rt.RefreshLock.Wait(0) Then Continue For
                Try
                    rt.PendingRefresh = False
                    rt.LastRefreshUtc = now
                    Try
                        Await RefreshPanelAsync(p, token)
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "RefreshPanelAsync threw for panel {Id}", p.PanelId)
                    End Try
                Finally
                    rt.RefreshLock.Release()
                End Try
            Next
        End Function

        Private Async Function RefreshPanelAsync(p As DiscordPanelEntity,
                                                  token As CancellationToken) As Task
            Dim client = _client
            If client Is Nothing OrElse Not _connected Then Return

            Dim guildId As ULong
            Dim channelId As ULong
            If Not ULong.TryParse(p.GuildId, guildId) OrElse
               Not ULong.TryParse(p.ChannelId, channelId) Then
                _logger.LogWarning("Panel {Id} has unparseable guild/channel ID", p.PanelId)
                Return
            End If

            Dim guild As DiscordGuild = Nothing
            If Not client.Guilds.TryGetValue(guildId, guild) Then
                _logger.LogDebug("Panel {Id}: bot is not in guild {Guild}", p.PanelId, guildId)
                Return
            End If

            Dim channel As DiscordChannel = Nothing
            Try
                channel = guild.GetChannel(channelId)
            Catch ex As Exception
                _logger.LogDebug(ex, "Panel {Id}: GetChannel({Channel}) threw", p.PanelId, channelId)
            End Try
            If channel Is Nothing Then
                _logger.LogDebug("Panel {Id}: channel {Channel} not found in guild", p.PanelId, channelId)
                Return
            End If

            ' Build the rendered message. Resolution failures here
            ' (e.g. an InstanceSet tag that no longer matches anything)
            ' produce a visible "no instances in scope" embed rather
            ' than a silent skip — operators need to notice broken
            ' panels.
            Dim builder As DiscordMessageBuilder = Nothing
            Try
                builder = BuildPanelMessage(p)
            Catch ex As Exception
                _logger.LogWarning(ex, "BuildPanelMessage threw for panel {Id}", p.PanelId)
                Return
            End Try

            ' Edit if MessageId is set and the message still exists,
            ' otherwise post fresh and persist the new MessageId.
            Dim posted As DiscordMessage = Nothing
            Dim needsNewPost As Boolean = String.IsNullOrEmpty(p.MessageId)

            If Not needsNewPost Then
                Dim messageId As ULong
                If ULong.TryParse(p.MessageId, messageId) Then
                    Try
                        Dim existing = Await channel.GetMessageAsync(messageId)
                        If existing IsNot Nothing Then
                            posted = Await existing.ModifyAsync(builder)
                        Else
                            needsNewPost = True
                        End If
                    Catch ex As NotFoundException
                        ' Channel purge or operator deleted the
                        ' panel message manually — recover by
                        ' re-posting fresh.
                        needsNewPost = True
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "Panel {Id} edit failed — leaving MessageId in place for next tick",
                            p.PanelId)
                        Return
                    End Try
                Else
                    needsNewPost = True
                End If
            End If

            If needsNewPost Then
                Try
                    posted = Await channel.SendMessageAsync(builder)
                Catch ex As UnauthorizedException
                    _logger.LogWarning(
                        "Panel {Id}: bot lacks Send Messages / Embed Links permission in #{Channel}",
                        p.PanelId, channel.Name)
                    Return
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "Panel {Id} post failed in #{Channel}", p.PanelId, channel.Name)
                    Return
                End Try

                If posted IsNot Nothing Then
                    Try
                        Using scope = _serviceProvider.CreateScope()
                            Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                            Dim row = db.DiscordPanels.Find(p.PanelId)
                            If row IsNot Nothing Then
                                row.MessageId = posted.Id.ToString()
                                row.UpdatedUtc = DateTime.UtcNow
                                db.SaveChanges()
                            End If
                        End Using
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "Failed to persist new MessageId for panel {Id}", p.PanelId)
                    End Try
                End If
            End If
        End Function

        ' ============================================================
        '  Panel rendering
        ' ============================================================

        Private Function BuildPanelMessage(p As DiscordPanelEntity) As DiscordMessageBuilder
            Dim instances = ResolveInScopeInstances(p)
            Dim title = If(String.IsNullOrEmpty(p.DisplayName), "PowerGSM", p.DisplayName)

            ' Render as plain message content rather than as an
            ' embed. Embeds are capped at ~480-520px wide regardless
            ' of channel width, which forces wrapping on lines
            ' longer than ~75 characters — instance summaries with
            ' tile/save context routinely run 80-100 chars and
            ' wrapped mid-line as a result. Plain message content
            ' uses the full channel width, so each instance fits
            ' on a single line.
            '
            ' Trade-offs we accept by dropping the embed:
            '   • No coloured left sidebar (the Discord blurple).
            '     Decorative; not informational.
            '   • No styled footer block. Recreated as a `-# `
            '     subtext line at the bottom — same content ("last
            '     updated") in a slightly less prominent visual.
            '   • No embed timestamp. The `<t:UNIX:R>` tag in the
            '     subtext gives Discord viewers a localized,
            '     auto-updating relative timestamp instead.
            '
            ' Trade-offs we live with:
            '   • Message content is capped at 2000 chars (vs.
            '     embed description's 4096). The truncation cap
            '     below uses 1800 to leave headroom for the
            '     header and footer.
            '
            ' On first refresh after this code lands, ModifyAsync
            ' replaces the prior embed-style message with this
            ' content-style one transparently — a builder without
            ' an embed represents "no embed should be present".
            Dim sb As New StringBuilder()
            sb.AppendLine($"## {Escape(title)}")
            sb.AppendLine()

            If instances Is Nothing OrElse instances.Count = 0 Then
                sb.AppendLine("_No instances in scope._")
            Else
                ' Phase 5d-5 item 3: layout + grouping. Layout is
                ' parsed once per render and reused for every row;
                ' grouping is applied as a sort + header-emit pass
                ' on top. The truncation cap below is structural-
                ' agnostic: it cuts at line boundaries (group
                ' header or instance row, whichever is current)
                ' and emits the marker, regardless of whether the
                ' group is half-rendered or whole.
                Dim layout = ParseLayout(p.LayoutJson)
                Dim groupingKind = If(p.GroupingKind, "None")
                Dim sortedInstances = SortInstancesForGrouping(instances, groupingKind)
                Dim renderItems = BuildPanelRenderItems(sortedInstances, layout, groupingKind)

                For i = 0 To renderItems.Count - 1
                    Dim line = renderItems(i)
                    ' 1800-char cap leaves room for the header,
                    ' footer, and a potential truncation marker
                    ' under Discord's 2000-char message content
                    ' limit. (Embed descriptions had a 4096 cap;
                    ' the cap here is tighter as the cost of full
                    ' channel width.) The Manage dropdown
                    ' paginates separately (gsm:page:* clicks);
                    ' this character-based cap on the public
                    ' panel is a known limitation — splitting
                    ' the panel across multiple messages is its
                    ' own design problem, deferred indefinitely.
                    If sb.Length + line.Length + 1 > 1800 Then
                        Dim remaining = renderItems.Count - i
                        sb.AppendLine($"_…and {remaining} more line(s)_")
                        Exit For
                    End If
                    sb.AppendLine(line)
                Next
            End If

            sb.AppendLine()
            Dim footerTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            sb.Append($"-# PowerGSM • last updated <t:{footerTs}:R>")

            Dim manageButton As New DiscordButtonComponent(
                ButtonStyle.Primary,
                customId:=$"gsm:panel:{p.PanelId}:manage",
                label:="Manage")

            Dim msg As New DiscordMessageBuilder()
            msg.Content = sb.ToString()
            msg.AddComponents(manageButton)
            Return msg
        End Function

        ' ============================================================
        '  Panel layout (Phase 5d-5 item 3)
        '
        '  Per-instance line composition is driven by a list of
        '  ILayoutElement instances rather than the hardcoded v1
        '  template. The layout is stored on DiscordPanelEntity
        '  as a JSON document (LayoutJson; NULL = use the default).
        '
        '  Each element renders its own self-contained content,
        '  including any Markdown markup it owns (the InstanceName
        '  element renders **bold**, NextRestart renders the full
        '  <t:UNIX:R> tag, etc.). Each element also declares a
        '  NaturalPrefix — the separator that precedes it in the
        '  rendered line. The first non-empty element on the line
        '  drops its prefix; subsequent elements join via their
        '  prefixes. Elements whose Render() returns empty are
        '  skipped entirely along with their prefix — e.g. a
        '  stopped instance has no PlayerCount and no ContextLine,
        '  and the line collapses to just the elements with content.
        '
        '  The default layout reproduces the v1 BuildInstanceLine
        '  output byte-for-byte: existing panels render identically
        '  until an operator customises them.
        ' ============================================================

        Private MustInherit Class LayoutElement
            ''' <summary>Discriminator string used in JSON.</summary>
            Public MustOverride ReadOnly Property TypeKey As String
            ''' <summary>Separator inserted before this element when it follows another non-empty element.</summary>
            Public MustOverride ReadOnly Property NaturalPrefix As String
            ''' <summary>Render this element's content for the given instance. Empty string = skip.</summary>
            Public MustOverride Function Render(inst As InScopeInstance) As String
        End Class

        Private Class StateEmojiElement
            Inherits LayoutElement
            Public Overrides ReadOnly Property TypeKey As String = "StateEmoji"
            Public Overrides ReadOnly Property NaturalPrefix As String = " "
            Public Overrides Function Render(inst As InScopeInstance) As String
                Return StateEmoji(inst.State)
            End Function
        End Class

        Private Class InstanceNameElement
            Inherits LayoutElement
            Public Overrides ReadOnly Property TypeKey As String = "InstanceName"
            Public Overrides ReadOnly Property NaturalPrefix As String = " "
            Public Overrides Function Render(inst As InScopeInstance) As String
                Return $"**{Escape(inst.DisplayName)}**"
            End Function
        End Class

        Private Class StateTextElement
            Inherits LayoutElement
            Public Overrides ReadOnly Property TypeKey As String = "StateText"
            Public Overrides ReadOnly Property NaturalPrefix As String = " — "
            Public Overrides Function Render(inst As InScopeInstance) As String
                Return StateText(inst.State)
            End Function
        End Class

        Private Class PlayerCountElement
            Inherits LayoutElement
            Public Overrides ReadOnly Property TypeKey As String = "PlayerCount"
            Public Overrides ReadOnly Property NaturalPrefix As String = ", "
            Public Overrides Function Render(inst As InScopeInstance) As String
                ' Player count only meaningful when the server is
                ' actually receiving players. Stopped/Crashed/etc.
                ' return empty so the separator drops too.
                If inst.State <> InstanceState.Running AndAlso
                   inst.State <> InstanceState.Starting Then Return ""
                If Not inst.PlayerCount.HasValue Then Return ""
                Return $"{inst.PlayerCount.Value} player(s)"
            End Function
        End Class

        Private Class ContextLineElement
            Inherits LayoutElement
            Public Overrides ReadOnly Property TypeKey As String = "ContextLine"
            Public Overrides ReadOnly Property NaturalPrefix As String = " · "
            Public Overrides Function Render(inst As InScopeInstance) As String
                If String.IsNullOrEmpty(inst.ContextLine) Then Return ""
                Return Escape(inst.ContextLine)
            End Function
        End Class

        Private Class NextRestartElement
            Inherits LayoutElement
            Public Overrides ReadOnly Property TypeKey As String = "NextRestart"
            Public Overrides ReadOnly Property NaturalPrefix As String = ", restart "
            Public Overrides Function Render(inst As InScopeInstance) As String
                If Not inst.NextRestart.HasValue Then Return ""
                ' Same Local-vs-UTC handling as v1: PowerGSM stores
                ' cron schedules as local time (matching
                ' AutomationEngine), so a Local-kind value gets
                ' converted via DateTimeOffset's local-aware
                ' constructor. Unspecified is treated as UTC.
                Dim raw = inst.NextRestart.Value
                Dim dto As DateTimeOffset
                If raw.Kind = DateTimeKind.Local Then
                    dto = New DateTimeOffset(raw)
                Else
                    dto = New DateTimeOffset(
                        DateTime.SpecifyKind(raw, DateTimeKind.Utc),
                        TimeSpan.Zero)
                End If
                Return $"<t:{dto.ToUnixTimeSeconds()}:R>"
            End Function
        End Class

        Private Class NodeNameElement
            Inherits LayoutElement
            Public Overrides ReadOnly Property TypeKey As String = "NodeName"
            Public Overrides ReadOnly Property NaturalPrefix As String = " · "
            Public Overrides Function Render(inst As InScopeInstance) As String
                If String.IsNullOrEmpty(inst.NodeName) Then Return ""
                Return Escape(inst.NodeName)
            End Function
        End Class

        Private Class FreeTextElement
            Inherits LayoutElement
            ''' <summary>Literal text typed by the operator. Not Markdown-escaped — operators may want to use formatting in their separators.</summary>
            Public Property Text As String
            Public Overrides ReadOnly Property TypeKey As String = "FreeText"
            ' FreeText carries its content directly; no separator
            ' before it. If the operator wants spacing they include
            ' it in Text.
            Public Overrides ReadOnly Property NaturalPrefix As String = ""
            Public Overrides Function Render(inst As InScopeInstance) As String
                Return If(Text, "")
            End Function
        End Class

        ''' <summary>
        ''' v1-equivalent layout. Reproduces BuildInstanceLine's
        ''' output byte-for-byte: existing panels with NULL
        ''' LayoutJson render identically to before this code
        ''' shipped.
        ''' </summary>
        Private Shared Function DefaultLayout() As List(Of LayoutElement)
            Return New List(Of LayoutElement) From {
                New StateEmojiElement(),
                New InstanceNameElement(),
                New StateTextElement(),
                New PlayerCountElement(),
                New ContextLineElement(),
                New NextRestartElement()
            }
        End Function

        ''' <summary>
        ''' Parse a LayoutJson value into element instances. Falls
        ''' back to DefaultLayout on null/empty/parse-failure — we'd
        ''' rather show a working default panel than render "_layout
        ''' broken_" if a future code path ever writes a malformed
        ''' document.
        ''' </summary>
        Private Function ParseLayout(json As String) As List(Of LayoutElement)
            If String.IsNullOrWhiteSpace(json) Then Return DefaultLayout()
            Try
                Using doc = JsonDocument.Parse(json)
                    Dim root = doc.RootElement
                    Dim elementsProp As JsonElement = Nothing
                    If Not root.TryGetProperty("elements", elementsProp) Then
                        Return DefaultLayout()
                    End If
                    Dim out As New List(Of LayoutElement)
                    For Each el In elementsProp.EnumerateArray()
                        Dim typeProp As JsonElement = Nothing
                        If Not el.TryGetProperty("type", typeProp) Then Continue For
                        Dim typeKey = typeProp.GetString()
                        Select Case typeKey
                            Case "StateEmoji" : out.Add(New StateEmojiElement())
                            Case "InstanceName" : out.Add(New InstanceNameElement())
                            Case "StateText" : out.Add(New StateTextElement())
                            Case "PlayerCount" : out.Add(New PlayerCountElement())
                            Case "ContextLine" : out.Add(New ContextLineElement())
                            Case "NextRestart" : out.Add(New NextRestartElement())
                            Case "NodeName" : out.Add(New NodeNameElement())
                            Case "FreeText"
                                Dim textProp As JsonElement = Nothing
                                Dim textVal As String = ""
                                If el.TryGetProperty("text", textProp) Then
                                    textVal = If(textProp.GetString(), "")
                                End If
                                out.Add(New FreeTextElement With {.Text = textVal})
                            Case Else
                                ' Unknown element type — skip silently.
                                ' Forward-compat: a future code
                                ' version might write element types
                                ' this version doesn't know about.
                        End Select
                    Next
                    If out.Count = 0 Then Return DefaultLayout()
                    Return out
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "ParseLayout failed; using default")
                Return DefaultLayout()
            End Try
        End Function

        ''' <summary>
        ''' Sort instances for grouping. None = preserves the
        ''' DisplayName ordering already applied by
        ''' ResolveInScopeInstances. ByNode / ByGame inserts the
        ''' grouping key as a primary sort, with DisplayName as a
        ''' stable secondary so within-group ordering matches the
        ''' flat case. ByNodeThenGame sorts by node first then
        ''' game then name. Empty group keys (unresolved NodeName
        ''' or empty GameId) sort last via a leading high-codepoint
        ''' marker, so the "unknown" bucket appears at the end.
        ''' </summary>
        Private Shared Function SortInstancesForGrouping(
                instances As List(Of InScopeInstance),
                groupingKind As String) As List(Of InScopeInstance)
            Dim kind = If(groupingKind, "None")
            Select Case kind
                Case "ByNode"
                    Return instances.
                        OrderBy(Function(i) GroupSortKey(i.NodeName)).
                        ThenBy(Function(i) i.DisplayName).
                        ToList()
                Case "ByGame"
                    Return instances.
                        OrderBy(Function(i) GroupSortKey(i.GameId)).
                        ThenBy(Function(i) i.DisplayName).
                        ToList()
                Case "ByNodeThenGame"
                    Return instances.
                        OrderBy(Function(i) GroupSortKey(i.NodeName)).
                        ThenBy(Function(i) GroupSortKey(i.GameId)).
                        ThenBy(Function(i) i.DisplayName).
                        ToList()
                Case Else
                    ' "None" or anything unrecognised — leave
                    ' ordering as-is. ResolveInScopeInstances
                    ' already sorted by DisplayName.
                    Return instances
            End Select
        End Function

        ''' <summary>
        ''' Sort key wrapper that pushes empty/null values to the
        ''' end of the ordering. Empty group keys typically mean
        ''' "node row missing" or "GameId blank" — surfacing them
        ''' last keeps the panel's main content at the top.
        ''' </summary>
        Private Shared Function GroupSortKey(value As String) As String
            If String.IsNullOrEmpty(value) Then Return ChrW(&HFFFD) & ""
            Return value
        End Function

        ''' <summary>
        ''' Walk the (already-grouping-sorted) instance list and
        ''' produce the ordered list of rendered lines, including
        ''' group header lines emitted on group transitions. Headers
        ''' use Markdown H3 (### ) for the primary grouping key and
        ''' bold (**...**) for the inner key in two-level grouping.
        ''' Empty group keys render under "_(unknown)_".
        ''' </summary>
        Private Shared Function BuildPanelRenderItems(
                instances As List(Of InScopeInstance),
                layout As List(Of LayoutElement),
                groupingKind As String) As List(Of String)
            Dim out As New List(Of String)
            Dim kind = If(groupingKind, "None")

            If kind = "None" Then
                For Each inst In instances
                    out.Add(BuildInstanceLineFromLayout(inst, layout))
                Next
                Return out
            End If

            Dim lastPrimary As String = Nothing
            Dim lastSecondary As String = Nothing
            For Each inst In instances
                Dim primaryKey As String = ""
                Dim secondaryKey As String = ""
                Select Case kind
                    Case "ByNode" : primaryKey = If(inst.NodeName, "")
                    Case "ByGame" : primaryKey = If(inst.GameId, "")
                    Case "ByNodeThenGame"
                        primaryKey = If(inst.NodeName, "")
                        secondaryKey = If(inst.GameId, "")
                End Select

                ' Primary header on first iteration, or whenever
                ' the primary key changes. lastPrimary starts at
                ' Nothing (distinct from "") so the first row
                ' always emits a header even if its key is empty.
                If lastPrimary Is Nothing OrElse Not String.Equals(lastPrimary, primaryKey, StringComparison.Ordinal) Then
                    out.Add($"### {GroupHeaderLabel(primaryKey)}")
                    lastPrimary = primaryKey
                    ' Force secondary re-emit when primary
                    ' changes — the secondary header should
                    ' appear under the new primary even if its
                    ' key value happens to repeat across primaries.
                    lastSecondary = Nothing
                End If

                If kind = "ByNodeThenGame" Then
                    If lastSecondary Is Nothing OrElse Not String.Equals(lastSecondary, secondaryKey, StringComparison.Ordinal) Then
                        out.Add($"**{GroupHeaderLabel(secondaryKey)}**")
                        lastSecondary = secondaryKey
                    End If
                End If

                out.Add(BuildInstanceLineFromLayout(inst, layout))
            Next
            Return out
        End Function

        Private Shared Function GroupHeaderLabel(key As String) As String
            If String.IsNullOrEmpty(key) Then Return "_(unknown)_"
            Return Escape(key)
        End Function

        Private Function BuildInstanceLine(inst As InScopeInstance) As String
            Return BuildInstanceLineFromLayout(inst, DefaultLayout())
        End Function

        Private Shared Function BuildInstanceLineFromLayout(
                inst As InScopeInstance,
                layout As List(Of LayoutElement)) As String
            Dim sb As New StringBuilder()
            Dim isFirstNonEmpty As Boolean = True
            For Each el In layout
                Dim content = el.Render(inst)
                If String.IsNullOrEmpty(content) Then Continue For
                If isFirstNonEmpty Then
                    sb.Append(content)
                    isFirstNonEmpty = False
                Else
                    sb.Append(el.NaturalPrefix)
                    sb.Append(content)
                End If
            Next
            Return sb.ToString()
        End Function

        Private Shared Function Escape(s As String) As String
            If String.IsNullOrEmpty(s) Then Return ""
            ' Minimal Markdown escape for instance display names —
            ' covers the cases we'd actually see (asterisks,
            ' underscores, backticks). Tildes and pipes left alone
            ' since they're rare in identifier-style names.
            Return s.Replace("\", "\\").
                     Replace("*", "\*").
                     Replace("_", "\_").
                     Replace("`", "\`")
        End Function

        Private Shared Function StateEmoji(s As InstanceState) As String
            Select Case s
                Case InstanceState.Running : Return "🟢"
                Case InstanceState.Starting : Return "🟡"
                Case InstanceState.Stopping : Return "🟡"
                Case InstanceState.Stopped : Return "⚪"
                Case InstanceState.Crashed : Return "🔴"
                Case InstanceState.CrashLoopHalted : Return "🔴"
                Case InstanceState.Updating : Return "🔵"
                Case InstanceState.WaitingForInput : Return "🟠"
                Case Else : Return "⚪"
            End Select
        End Function

        Private Shared Function StateText(s As InstanceState) As String
            Select Case s
                Case InstanceState.Running : Return "Running"
                Case InstanceState.Starting : Return "Starting"
                Case InstanceState.Stopping : Return "Stopping"
                Case InstanceState.Stopped : Return "Stopped"
                Case InstanceState.Crashed : Return "Crashed"
                Case InstanceState.CrashLoopHalted : Return "Crash loop halted"
                Case InstanceState.Updating : Return "Updating"
                Case InstanceState.WaitingForInput : Return "Waiting for input"
                Case Else : Return s.ToString()
            End Select
        End Function

        ' ============================================================
        '  Scope resolution + per-instance state harvest
        ' ============================================================

        Private Function ResolveInScopeInstances(p As DiscordPanelEntity) As List(Of InScopeInstance)
            Dim result As New List(Of InScopeInstance)
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim query = db.Instances.AsQueryable()

                    Select Case (If(p.ScopeKind, "")).ToLowerInvariant()
                        Case "allinstances", ""
                            ' all
                        Case "game"
                            query = query.Where(Function(i) i.GameId = p.ScopeTargetId)
                        Case "installation"
                            query = query.Where(Function(i) i.InstallationId = p.ScopeTargetId)
                        Case "instanceset"
                            query = query.Where(Function(i) i.InstanceSetTag = p.ScopeTargetId)
                        Case Else
                            _logger.LogWarning("Panel {Id}: unknown ScopeKind {Kind}",
                                p.PanelId, p.ScopeKind)
                    End Select

                    Dim entities = query.OrderBy(Function(i) i.DisplayName).ToList()

                    ' Batched node-name resolution (Phase 5d-5 item
                    ' 3). InstanceEntity.NodeId doesn't exist — the
                    ' join goes Instance → Installation → Node, so
                    ' we collect distinct InstallationIds, batch-load
                    ' the corresponding Installations to get their
                    ' NodeIds, then batch-load Nodes to get display
                    ' names. Two queries regardless of instance
                    ' count, vs. the 2N queries we'd get from
                    ' per-row Find() calls.
                    Dim nodeNameByInstallation As New Dictionary(Of String, String)(StringComparer.Ordinal)
                    If entities.Count > 0 Then
                        Dim installIds = entities.
                            Where(Function(i) Not String.IsNullOrEmpty(i.InstallationId)).
                            Select(Function(i) i.InstallationId).
                            Distinct().
                            ToList()
                        If installIds.Count > 0 Then
                            Dim installs = db.Installations.
                                Where(Function(x) installIds.Contains(x.InstallationId)).
                                Select(Function(x) New With {x.InstallationId, x.NodeId}).
                                ToList()
                            Dim nodeIds = installs.
                                Where(Function(x) Not String.IsNullOrEmpty(x.NodeId)).
                                Select(Function(x) x.NodeId).
                                Distinct().
                                ToList()
                            Dim nodeDisplayById As New Dictionary(Of String, String)(StringComparer.Ordinal)
                            If nodeIds.Count > 0 Then
                                Dim nodes = db.Nodes.
                                    Where(Function(n) nodeIds.Contains(n.NodeId)).
                                    Select(Function(n) New With {n.NodeId, n.DisplayName}).
                                    ToList()
                                For Each n In nodes
                                    nodeDisplayById(n.NodeId) = If(n.DisplayName, "")
                                Next
                            End If
                            For Each ins In installs
                                Dim displayName As String = Nothing
                                If Not String.IsNullOrEmpty(ins.NodeId) Then
                                    nodeDisplayById.TryGetValue(ins.NodeId, displayName)
                                End If
                                nodeNameByInstallation(ins.InstallationId) = If(displayName, "")
                            Next
                        End If
                    End If

                    For Each e In entities
                        Dim resolvedNodeName As String = ""
                        If Not String.IsNullOrEmpty(e.InstallationId) Then
                            nodeNameByInstallation.TryGetValue(e.InstallationId, resolvedNodeName)
                        End If
                        result.Add(BuildInScopeRow(e, If(resolvedNodeName, "")))
                    Next
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "ResolveInScopeInstances threw for panel {Id}", p.PanelId)
            End Try
            Return result
        End Function

        Private Function BuildInScopeRow(e As InstanceEntity, nodeName As String) As InScopeInstance
            Dim row As New InScopeInstance With {
                .InstanceId = e.InstanceId,
                .DisplayName = If(e.DisplayName, e.InstanceId),
                .State = InstanceState.Stopped,
                .NextRestart = ComputeNextRestart(e),
                .NodeName = If(nodeName, ""),
                .GameId = If(e.GameId, "")
            }

            ' Live state — uses the cached snapshot maintained by
            ' the instance manager's background poller (3s tick).
            ' Avoids hitting nodes from the bot's hot path.
            Dim live = _instanceManager.GetLiveState(e.InstanceId)
            If live IsNot Nothing Then
                row.State = live.CurrentState
            End If

            ' Player count + game-specific context line — only
            ' meaningful when running. Both pulled via the instance
            ' manager: GetPlayersAsync hits the node's parsed-player
            ' set; GetServerStateAsync returns the node's derived
            ' server state (TileName for LO, MatchState, etc.).
            ' Failures degrade to "no extra info shown" rather than
            ' breaking the row.
            If row.State = InstanceState.Running OrElse row.State = InstanceState.Starting Then
                Try
                    Dim players = _instanceManager.GetPlayersAsync(e.InstanceId).
                        GetAwaiter().GetResult()
                    row.PlayerCount = If(players Is Nothing, 0, players.Count)
                Catch ex As Exception
                    _logger.LogDebug(ex,
                        "Panel render: GetPlayersAsync threw for {Id}", e.InstanceId)
                End Try

                row.ContextLine = BuildContextLine(e)
            Else
                ' Instance is in a non-running state. Evict any
                ' Factorio server-name cache entry so the next
                ' run re-reads server-settings.json — the user
                ' may have edited it during the downtime, and
                ' Factorio only re-reads the file at start. We
                ' don't bother game-typing the eviction: a
                ' missing key is a no-op for non-Factorio
                ' instances, and the dictionary is tiny.
                Dim removed As String = Nothing
                _factorioServerNameCache.TryRemove(e.InstanceId, removed)
            End If

            Return row
        End Function

        ''' <summary>
        ''' Compute a short "what's this server doing right now"
        ''' line for the panel, game-specific. LO gets the loaded
        ''' tile name from ServerStateResponse (the plugin's parser
        ''' tracks LogPersistence tile_name and the node exposes it
        ''' via /server-state). Factorio doesn't have a log-derived
        ''' "current save" field — we read SaveFile from the merged
        ''' install+instance ConfigJson so users see which save the
        ''' instance is configured to load. Returns empty string
        ''' for unknown games or when the relevant data isn't
        ''' available; the renderer skips the line in that case.
        ''' </summary>
        Private Function BuildContextLine(e As InstanceEntity) As String
            Try
                Select Case (If(e.GameId, "")).ToLowerInvariant()
                    Case "lastoasis"
                        Dim srv = _instanceManager.GetServerStateAsync(e.InstanceId).
                            GetAwaiter().GetResult()
                        If srv IsNot Nothing AndAlso
                           Not String.IsNullOrEmpty(srv.TileName) Then
                            Return $"tile: {srv.TileName}"
                        End If
                        Return ""

                    Case "factorio"
                        Dim merged = LoadMergedConfig(e)
                        If merged Is Nothing Then merged = New Dictionary(Of String, String)

                        Dim parts As New List(Of String)

                        ' Server name from server-settings.json. This
                        ' is the human-readable name shown in
                        ' Factorio's server browser, distinct from the
                        ' instance's PowerGSM display name. Cached for
                        ' 5 minutes per instance to avoid hitting the
                        ' node's file API on every panel refresh — the
                        ' file rarely changes and a 5-minute lag after
                        ' an edit is acceptable.
                        Dim serverName = GetFactorioServerName(e)
                        If Not String.IsNullOrEmpty(serverName) Then
                            parts.Add(serverName)
                        End If

                        ' Save info — either "latest" flag or specific
                        ' filename. The .zip suffix is stripped because
                        ' Factorio saves are always .zip; showing it
                        ' adds noise without information.
                        Dim useLatest As String = Nothing
                        merged.TryGetValue("UseLatestSave", useLatest)
                        If Not String.IsNullOrEmpty(useLatest) AndAlso
                           useLatest.Equals("true", StringComparison.OrdinalIgnoreCase) Then
                            parts.Add("save: latest")
                        Else
                            Dim saveFile As String = Nothing
                            merged.TryGetValue("SaveFile", saveFile)
                            If Not String.IsNullOrEmpty(saveFile) Then
                                If saveFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then
                                    saveFile = saveFile.Substring(0, saveFile.Length - 4)
                                End If
                                parts.Add($"save: {saveFile}")
                            End If
                        End If

                        Return String.Join(" · ", parts)

                    Case Else
                        Return ""
                End Select
            Catch ex As Exception
                _logger.LogDebug(ex,
                    "BuildContextLine threw for {Id}", e.InstanceId)
                Return ""
            End Try
        End Function

        ''' <summary>
        ''' Load and merge an instance's install+instance ConfigJson
        ''' into a single dict, applying the same "empty instance
        ''' value doesn't overwrite non-empty install value" rule as
        ''' InstanceManager.StartInstanceAsync. Used by BuildContextLine
        ''' for Factorio's SaveFile lookup; can be reused by future
        ''' game-specific context rendering.
        ''' </summary>
        Private Function LoadMergedConfig(e As InstanceEntity) As Dictionary(Of String, String)
            Dim merged As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim install = db.Installations.Find(e.InstallationId)
                    If install IsNot Nothing AndAlso
                       Not String.IsNullOrEmpty(install.ConfigJson) Then
                        Try
                            Dim d = JsonSerializer.Deserialize(
                                Of Dictionary(Of String, String))(install.ConfigJson)
                            If d IsNot Nothing Then
                                For Each kvp In d
                                    merged(kvp.Key) = kvp.Value
                                Next
                            End If
                        Catch
                        End Try
                    End If
                    If Not String.IsNullOrEmpty(e.ConfigJson) Then
                        Try
                            Dim d = JsonSerializer.Deserialize(
                                Of Dictionary(Of String, String))(e.ConfigJson)
                            If d IsNot Nothing Then
                                For Each kvp In d
                                    If String.IsNullOrEmpty(kvp.Value) AndAlso
                                       merged.ContainsKey(kvp.Key) AndAlso
                                       Not String.IsNullOrEmpty(merged(kvp.Key)) Then
                                        Continue For
                                    End If
                                    merged(kvp.Key) = kvp.Value
                                Next
                            End If
                        Catch
                        End Try
                    End If
                End Using
            Catch
            End Try
            Return merged
        End Function

        ''' <summary>
        ''' Resolve the human-readable server name from Factorio's
        ''' server-settings.json. The file lives at the install root
        ''' (or wherever the merged ServerSettings field points);
        ''' we download it via the node's file API and pull the
        ''' "name" property out. Cached per-instance for the
        ''' lifetime of the running server: Factorio only reads
        ''' server-settings.json on start, so the value is
        ''' effectively immutable while the server is up. Cache
        ''' eviction is driven by BuildInScopeRow on the first
        ''' non-running state observation — the next start re-reads.
        ''' Returns empty string when the file is missing,
        ''' malformed, or the lookup fails for any reason; the
        ''' caller treats empty as "no server name to show" rather
        ''' than rendering an awkward placeholder.
        ''' </summary>
        Private Function GetFactorioServerName(e As InstanceEntity) As String
            ' Cache hit — return cached value (which may be empty
            ' from a previous failed lookup; we cache empties too
            ' so transient errors during a single instance run
            ' don't hammer the node every render. The next stop
            ' clears the cache and gives transient errors a fresh
            ' chance on the following start).
            Dim cached As String = Nothing
            If _factorioServerNameCache.TryGetValue(e.InstanceId, cached) Then
                Return If(cached, "")
            End If

            Dim resolvedName As String = ""
            Try
                Dim factory = _serviceProvider.GetService(Of NodeHttpClientFactory)()
                If factory Is Nothing Then
                    _factorioServerNameCache(e.InstanceId) = ""
                    Return ""
                End If

                Dim installPath As String = Nothing
                Dim client As INodeClient = Nothing
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim install = db.Installations.Find(e.InstallationId)
                    If install Is Nothing Then
                        _factorioServerNameCache(e.InstanceId) = ""
                        Return ""
                    End If
                    Dim nodeEntity = db.Nodes.Find(install.NodeId)
                    If nodeEntity Is Nothing Then
                        _factorioServerNameCache(e.InstanceId) = ""
                        Return ""
                    End If
                    client = factory.GetClient(nodeEntity.NodeId,
                                                nodeEntity.HostAddress,
                                                nodeEntity.Port,
                                                nodeEntity.AuthToken)
                    installPath = install.InstallPath
                End Using
                If client Is Nothing OrElse String.IsNullOrEmpty(installPath) Then
                    _factorioServerNameCache(e.InstanceId) = ""
                    Return ""
                End If

                ' Resolve the settings path from the merged config.
                ' Falls back to "server-settings.json" at the install
                ' root — same default the FactorioPlugin uses when
                ' building launch arguments, so the path the bot
                ' reads always matches the path Factorio is actually
                ' loading.
                Dim merged = LoadMergedConfig(e)
                Dim settingsPath As String = ""
                If merged IsNot Nothing Then merged.TryGetValue("ServerSettings", settingsPath)
                If String.IsNullOrEmpty(settingsPath) Then settingsPath = "server-settings.json"

                ' For files at the install root the file API
                ' expects the filename itself as the allowedRoot
                ' (FileEndpoints does an exact-match check rather
                ' than a path-prefix one for top-level files);
                ' allowedExtensions locks the request to JSON.
                ' Same pattern the InstanceFileEditorPanel uses for
                ' the same file via the editor UI.
                Using ms As New MemoryStream()
                    client.DownloadFileAsync(
                        e.InstanceId,
                        installPath,
                        settingsPath,
                        New String() {settingsPath},
                        New String() {".json"},
                        ms,
                        CancellationToken.None).GetAwaiter().GetResult()
                    Dim json = Encoding.UTF8.GetString(ms.ToArray())
                    If Not String.IsNullOrEmpty(json) Then
                        Try
                            Using doc = JsonDocument.Parse(json)
                                Dim nameEl As JsonElement
                                If doc.RootElement.TryGetProperty("name", nameEl) AndAlso
                                   nameEl.ValueKind = JsonValueKind.String Then
                                    resolvedName = If(nameEl.GetString(), "")
                                End If
                            End Using
                        Catch
                            ' Malformed JSON — leave name empty.
                            ' The cache entry below means we won't
                            ' retry until the next stop+start, which
                            ' is the right behaviour: a malformed
                            ' file won't fix itself while the server
                            ' is up, and editing it won't take
                            ' effect anyway until next launch.
                        End Try
                    End If
                End Using
            Catch ex As Exception
                ' Includes the 404 case for a not-yet-existent
                ' server-settings.json (rare — our installer writes
                ' a default — but possible if a user deleted it).
                _logger.LogDebug(ex,
                    "GetFactorioServerName failed for {Id}", e.InstanceId)
            End Try

            _factorioServerNameCache(e.InstanceId) = resolvedName
            Return resolvedName
        End Function

        Private Shared Function ComputeNextRestart(e As InstanceEntity) As DateTime?
            If e Is Nothing Then Return Nothing
            If Not e.RestartEnabled Then Return Nothing
            If String.IsNullOrWhiteSpace(e.RestartCron) Then Return Nothing
            Try
                ' PowerGSM stores cron expressions in LOCAL time —
                ' AutomationEngine schedules against DateTime.Now
                ' (not UtcNow), so a cron of "0 4 * * *" fires at
                ' 4 AM in the manager's local timezone, not 4 AM
                ' UTC. Compute the next occurrence using local now
                ' and tag the result as Local kind so downstream
                ' renderers (BuildInstanceLine's Discord <t:UNIX:R>
                ' emission) convert correctly via DateTimeOffset's
                ' local-offset constructor. Earlier code passed
                ' DateTime.UtcNow here and later treated the result
                ' as UTC — which produced restart times five hours
                ' off in CDT (cron 04:00 local rendered as 04:00
                ' UTC → 23:00 the previous day local).
                Dim sched = CrontabSchedule.Parse(e.RestartCron)
                Dim nextLocal = sched.GetNextOccurrence(DateTime.Now)
                Return DateTime.SpecifyKind(nextLocal, DateTimeKind.Local)
            Catch
                Return Nothing
            End Try
        End Function

        ' ============================================================
        '  Event-driven refresh trigger
        ' ============================================================

        Private Sub RequestRefreshForAffected(context As NotificationContext)
            Dim instanceId = If(context.Tokens?.InstanceId, "")
            Dim installationId = If(context.Tokens?.InstallationId, "")
            Dim gameId = If(context.Tokens?.GameId, "")

            If String.IsNullOrEmpty(instanceId) AndAlso
               String.IsNullOrEmpty(installationId) Then
                Return
            End If

            ' Resolve the InstanceSet tag the affected instance
            ' belongs to (if any) so InstanceSet-scoped panels
            ' can match against this event without a per-tick
            ' DB join.
            Dim instanceSetTag As String = ""
            If Not String.IsNullOrEmpty(instanceId) Then
                Try
                    Using scope = _serviceProvider.CreateScope()
                        Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                        Dim inst = db.Instances.Find(instanceId)
                        If inst IsNot Nothing Then
                            instanceSetTag = If(inst.InstanceSetTag, "")
                            If String.IsNullOrEmpty(installationId) Then
                                installationId = If(inst.InstallationId, "")
                            End If
                            If String.IsNullOrEmpty(gameId) Then
                                gameId = If(inst.GameId, "")
                            End If
                        End If
                    End Using
                Catch
                End Try
            End If

            ' Walk the panel cache. We don't need to read the DB
            ' here — every PanelRuntime corresponds to a row that
            ' was loaded last tick, and the next tick will reload
            ' the row anyway. We just flag pending; the tick loop
            ' decides whether the cooldown has elapsed.
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim panels = db.DiscordPanels.ToList()
                    For Each p In panels
                        If MatchesScope(p, instanceId, installationId, gameId, instanceSetTag) Then
                            Dim rt As PanelRuntime = Nothing
                            If _panels.TryGetValue(p.PanelId, rt) Then
                                rt.PendingRefresh = True
                            End If
                        End If
                    Next
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "RequestRefreshForAffected DB walk threw")
            End Try
        End Sub

        Private Shared Function MatchesScope(p As DiscordPanelEntity,
                                              instanceId As String,
                                              installationId As String,
                                              gameId As String,
                                              instanceSetTag As String) As Boolean
            Select Case (If(p.ScopeKind, "")).ToLowerInvariant()
                Case "allinstances", ""
                    Return True
                Case "game"
                    Return Not String.IsNullOrEmpty(gameId) AndAlso
                           String.Equals(gameId, p.ScopeTargetId, StringComparison.Ordinal)
                Case "installation"
                    Return Not String.IsNullOrEmpty(installationId) AndAlso
                           String.Equals(installationId, p.ScopeTargetId, StringComparison.Ordinal)
                Case "instanceset"
                    Return Not String.IsNullOrEmpty(instanceSetTag) AndAlso
                           String.Equals(instanceSetTag, p.ScopeTargetId, StringComparison.Ordinal)
                Case Else
                    Return False
            End Select
        End Function

        ' ============================================================
        '  Component interactions (Manage flow)
        '
        '  5d-2 wires up the full management ephemeral. State is
        '  encoded entirely in custom_id strings so the bot stays
        '  stateless across interactions:
        '
        '    gsm:panel:{panelId}:manage
        '       → click on public panel's Manage button. Permission
        '         check; if OK, ephemeral with instance dropdown.
        '
        '    gsm:select:{panelId}
        '       → dropdown selection. e.Values(0) carries the
        '         picked InstanceId; ephemeral is edited to show
        '         the action-button row.
        '
        '    gsm:action:{panelId}:{instanceId}:{action}
        '       → action button click. Permission re-check
        '         (panel-scoped, falling through to guild-default
        '         when no panel override exists), defer, dispatch
        '         via IRemoteCommandHandler, edit ephemeral with
        '         result. {action} is start/stop/restart — no
        '         update in 5d-2 (Update needs SteamGuard prompt
        '         handling that doesn't fit a Discord ephemeral;
        '         the manager UI keeps that responsibility).
        '         Phase 5d-5 item 4 added the {panelId} segment
        '         so per-panel role overrides can be enforced at
        '         action-click time.
        '
        '  Permission model (5d-3): per-guild role mapping table.
        '  Each interaction's originating guild is looked up in
        '  _roleMappings, the acting user's roles are intersected
        '  with the per-guild dict, and the highest permission
        '  tier found wins (Administrator > ServerOperator >
        '  Everyone). Roles not in the mapping table contribute
        '  Everyone (the implicit default), so the table only
        '  ever stores elevations.
        ' ============================================================

        Private Async Function OnComponentInteractionCreated(c As DiscordClient,
                                                              e As ComponentInteractionCreateEventArgs) As Task
            Try
                Dim id = If(e.Id, "")
                If Not id.StartsWith("gsm:", StringComparison.Ordinal) Then Return

                Dim parts = id.Split(":"c)
                If parts.Length < 2 Then Return
                Dim verb = parts(1)

                Select Case verb
                    Case "panel"
                        ' gsm:panel:{panelId}:manage — the only
                        ' panel-scoped sub-action today.
                        If parts.Length < 4 Then Return
                        If parts(3) = "manage" Then
                            Await HandleManageClickAsync(e, parts(2))
                        End If

                    Case "page"
                        ' gsm:page:{panelId}:{pageIndex} — Prev/Next
                        ' nav on the Manage ephemeral when the
                        ' panel has more than 25 in-scope
                        ' instances.
                        If parts.Length < 4 Then Return
                        Await HandlePageClickAsync(e, parts(2), parts(3))

                    Case "select"
                        ' gsm:select:{panelId}
                        If parts.Length < 3 Then Return
                        Await HandleSelectClickAsync(e, parts(2))

                    Case "action"
                        ' gsm:action:{panelId}:{instanceId}:{action}
                        ' Phase 5d-5 item 4 added the panelId
                        ' segment so per-panel role overrides can
                        ' be enforced at action-click time. Strict
                        ' parsing: stale buttons in the wild from
                        ' before this change use the 4-part format
                        ' and won't match — the dispatcher silently
                        ' drops them. Acceptable cost; the next
                        ' panel refresh re-renders any visible
                        ' panel buttons, and ephemeral action
                        ' buttons live for one Manage flow only
                        ' (the user re-opens Manage to re-render).
                        If parts.Length < 5 Then Return
                        Await HandleActionClickAsync(e, parts(2), parts(3), parts(4))
                End Select
            Catch ex As Exception
                _logger.LogWarning(ex, "OnComponentInteractionCreated threw")
            End Try
        End Function

        ''' <summary>
        ''' Manage button on the public panel. Permission gate:
        ''' unauthorised users get a brief ephemeral and stop here
        ''' (they never see the dropdown). Authorised users get an
        ''' ephemeral with the in-scope instance dropdown.
        ''' </summary>
        Private Async Function HandleManageClickAsync(
                e As ComponentInteractionCreateEventArgs,
                panelId As String) As Task
            ' Permission check first — cheap, doesn't hit the DB.
            If ResolvePermissionForInteraction(e, panelId) < CommandPermission.ServerOperator Then
                Try
                    Await e.Interaction.CreateResponseAsync(
                        InteractionResponseType.ChannelMessageWithSource,
                        New DiscordInteractionResponseBuilder().
                            WithContent("You don't have permission to manage instances on this panel. Ask an admin to grant you a role with operator permissions.").
                            AsEphemeral(True))
                Catch ex As Exception
                    _logger.LogDebug(ex, "Failed to send permission-denied response for manage click")
                End Try
                Return
            End If

            ' Look up the panel and its in-scope instances. The DB
            ' lookup is fast enough that we can do it before
            ' responding (within Discord's 3-second initial-response
            ' window).
            Dim panel As DiscordPanelEntity = Nothing
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    panel = db.DiscordPanels.Find(panelId)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Manage: failed to look up panel {Id}", panelId)
            End Try

            If panel Is Nothing Then
                Try
                    Await e.Interaction.CreateResponseAsync(
                        InteractionResponseType.ChannelMessageWithSource,
                        New DiscordInteractionResponseBuilder().
                            WithContent("This panel no longer exists. Ask an admin to refresh it.").
                            AsEphemeral(True))
                Catch
                End Try
                Return
            End If

            Dim instances = ResolveInScopeInstances(panel)
            Dim builder = BuildManageEphemeralBuilder(panel, instances)
            Try
                Await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.ChannelMessageWithSource, builder)
            Catch ex As Exception
                _logger.LogWarning(ex, "Manage: failed to send ephemeral for panel {Id}", panelId)
            End Try
        End Function

        ''' <summary>
        ''' Prev/Next pagination button on the Manage ephemeral.
        ''' Re-renders the same ephemeral with the requested
        ''' page of 25 instances. Permission is rechecked (cheap;
        ''' same defence-in-depth pattern as HandleSelectClickAsync
        ''' uses against role revocation mid-flow). The target
        ''' page is encoded in the custom ID so this handler is
        ''' purely a render request — no per-user state lives on
        ''' the manager between clicks. The page index is clamped
        ''' inside BuildManageEphemeralBuilder, so a stale ID
        ''' pointing past the new last page (instances removed
        ''' between clicks) lands on the now-final page rather
        ''' than producing an empty dropdown.
        ''' </summary>
        Private Async Function HandlePageClickAsync(
                e As ComponentInteractionCreateEventArgs,
                panelId As String,
                pageStr As String) As Task
            If ResolvePermissionForInteraction(e, panelId) < CommandPermission.ServerOperator Then
                Try
                    Await e.Interaction.CreateResponseAsync(
                        InteractionResponseType.UpdateMessage,
                        New DiscordInteractionResponseBuilder().
                            WithContent("Permission revoked since you opened Manage. Close this and try again."))
                Catch
                End Try
                Return
            End If

            Dim pageIndex As Integer = 0
            Integer.TryParse(pageStr, pageIndex)

            Dim panel As DiscordPanelEntity = Nothing
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    panel = db.DiscordPanels.Find(panelId)
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Page: failed to look up panel {Id}", panelId)
            End Try

            If panel Is Nothing Then
                Try
                    Await e.Interaction.CreateResponseAsync(
                        InteractionResponseType.UpdateMessage,
                        New DiscordInteractionResponseBuilder().
                            WithContent("This panel no longer exists. Ask an admin to refresh it."))
                Catch
                End Try
                Return
            End If

            Dim instances = ResolveInScopeInstances(panel)
            Dim builder = BuildManageEphemeralBuilder(panel, instances, pageIndex)
            Try
                Await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.UpdateMessage, builder)
            Catch ex As Exception
                _logger.LogWarning(ex, "Page: failed to update ephemeral for panel {Id}", panelId)
            End Try
        End Function

        ''' <summary>
        ''' Dropdown selection. Edits the ephemeral that contained
        ''' the dropdown to show the action button row for the
        ''' picked instance. Permission re-check is technically
        ''' redundant (we already checked on Manage click and the
        ''' user's role can't change in the second between Manage
        ''' and Select) but cheap and good defence.
        ''' </summary>
        Private Async Function HandleSelectClickAsync(
                e As ComponentInteractionCreateEventArgs,
                panelId As String) As Task
            If e.Values Is Nothing OrElse e.Values.Length = 0 Then Return
            Dim instanceId = e.Values(0)

            If ResolvePermissionForInteraction(e, panelId) < CommandPermission.ServerOperator Then
                ' Edge case — user lost their role between Manage
                ' and Select. Replace the ephemeral with a permission
                ' message; clears the dropdown so they can't pick
                ' again from the stale UI.
                Try
                    Await e.Interaction.CreateResponseAsync(
                        InteractionResponseType.UpdateMessage,
                        New DiscordInteractionResponseBuilder().
                            WithContent("Permission revoked since you opened Manage. Close this and try again."))
                Catch
                End Try
                Return
            End If

            ' Look up display name + current state for the header.
            ' GetLiveState reads the InstanceManager's cached state
            ' (3s background poller) so this stays synchronous and
            ' fast.
            Dim displayName As String = instanceId
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim inst = db.Instances.Find(instanceId)
                    If inst IsNot Nothing AndAlso Not String.IsNullOrEmpty(inst.DisplayName) Then
                        displayName = inst.DisplayName
                    End If
                End Using
            Catch
            End Try
            Dim state As InstanceState = InstanceState.Stopped
            Dim live = _instanceManager.GetLiveState(instanceId)
            If live IsNot Nothing Then state = live.CurrentState

            Dim builder = BuildActionsEphemeralBuilder(panelId, instanceId, displayName, state)
            Try
                Await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.UpdateMessage, builder)
            Catch ex As Exception
                _logger.LogWarning(ex, "Select: failed to update ephemeral for {Instance}", instanceId)
            End Try
        End Function

        ''' <summary>
        ''' Action button click. Defers the response (so the user
        ''' sees the spinner while we work — Stop with graceful
        ''' shutdown can take 25+ seconds), dispatches via
        ''' IRemoteCommandHandler, edits the ephemeral with the
        ''' result.
        ''' </summary>
        Private Async Function HandleActionClickAsync(
                e As ComponentInteractionCreateEventArgs,
                panelId As String,
                instanceId As String,
                action As String) As Task
            Dim permission = ResolvePermissionForInteraction(e, panelId)
            If permission < CommandPermission.ServerOperator Then
                Try
                    Await e.Interaction.CreateResponseAsync(
                        InteractionResponseType.ChannelMessageWithSource,
                        New DiscordInteractionResponseBuilder().
                            WithContent("Permission denied.").
                            AsEphemeral(True))
                Catch
                End Try
                Return
            End If

            ' Defer the response immediately so we have up to 15
            ' minutes to follow up via EditOriginalResponseAsync.
            ' DeferredMessageUpdate keeps the existing ephemeral
            ' visible (with the buttons — Discord shows them as
            ' the originating message during the defer window).
            Try
                Await e.Interaction.CreateResponseAsync(
                    InteractionResponseType.DeferredMessageUpdate)
            Catch ex As Exception
                ' If the defer fails, we can't proceed — Discord
                ' will show "interaction failed" to the user.
                _logger.LogWarning(ex, "Action defer failed for {Action} {Instance}", action, instanceId)
                Return
            End Try

            Dim resultMsg As String = ""
            Try
                If _commandHandler Is Nothing Then
                    resultMsg = "✗ Command handler not available."
                Else
                    Dim cmd As New InboundCommand With {
                        .SourcePluginId = PluginId,
                        .CommandName = If(action, "").ToLowerInvariant(),
                        .Arguments = New List(Of String) From {instanceId},
                        .RemoteUserId = e.User.Id.ToString(),
                        .RemoteUserName = If(e.User.Username, "(unknown)"),
                        .UserPermission = permission
                    }
                    Dim result = Await _commandHandler.HandleCommandAsync(
                        cmd, CancellationToken.None)
                    If result Is Nothing Then
                        resultMsg = "✗ No response from command handler."
                    ElseIf result.Success Then
                        resultMsg = $"✓ {If(result.ResponseMessage, "Done.")}"
                    Else
                        resultMsg = $"✗ {If(result.ErrorMessage, "Failed.")}"
                    End If
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "Action {Action} threw for {Instance}", action, instanceId)
                resultMsg = $"✗ {ex.Message}"
            End Try

            ' Edit the ephemeral with the result. No components on
            ' the result builder — absence clears the action
            ' buttons so the user can't re-fire the same action by
            ' double-clicking. They click Manage on the public
            ' panel again to do another action.
            Try
                Dim wb As New DiscordWebhookBuilder()
                wb.WithContent(resultMsg)
                Await e.Interaction.EditOriginalResponseAsync(wb)
            Catch ex As Exception
                _logger.LogWarning(ex, "Action result edit failed for {Action} {Instance}", action, instanceId)
            End Try
        End Function

        ''' <summary>
        ''' Resolve the acting user's permission level by
        ''' intersecting their guild roles with the per-guild role
        ''' mapping cache. Returns the highest permission tier
        ''' found across all of the user's roles; unmapped roles
        ''' contribute nothing (Everyone is the implicit default),
        ''' so a user with no mapped roles resolves to Everyone.
        '''
        ''' Failure modes all degrade to Everyone (least privilege):
        ''' missing guild context, member-fetch failure, empty
        ''' mappings cache, or any unexpected exception all mean
        ''' "can't confirm the user is elevated, so don't let them
        ''' through." If the operator hasn't run the role-mapping
        ''' migration yet, the cache stays empty and every user
        ''' resolves to Everyone — visible to operators as "my
        ''' Manage button does nothing" rather than a hard error.
        ''' </summary>
        Private Function ResolvePermissionForInteraction(
                e As ComponentInteractionCreateEventArgs,
                Optional panelId As String = "") As CommandPermission
            Try
                ' For guild interactions, e.User is actually a
                ' DiscordMember (DiscordMember : DiscordUser) carrying
                ' the role list inline — no REST call needed.
                Dim member = TryCast(e.User, DiscordMember)
                If member Is Nothing AndAlso e.Guild IsNot Nothing Then
                    ' Fallback path: fetch via REST. Sync-over-async
                    ' is tolerable here — this runs on a Task.Run
                    ' continuation from the gateway, not on a UI
                    ' sync context.
                    Try
                        member = e.Guild.GetMemberAsync(e.User.Id).
                            GetAwaiter().GetResult()
                    Catch
                    End Try
                End If
                Dim guildId = e.Guild?.Id.ToString()
                Return ResolveUserPermission(member, guildId, panelId)
            Catch ex As Exception
                _logger.LogDebug(ex, "ResolvePermissionForInteraction threw")
            End Try
            Return CommandPermission.Everyone
        End Function

        ''' <summary>
        ''' Phase 5d-4 — shared permission resolver used by both
        ''' the component-interaction surface (Manage panel
        ''' buttons) and the slash command surface (/players).
        ''' Pulled out of ResolvePermissionForInteraction so
        ''' GsmSlashCommands can call it directly without
        ''' synthesising a fake ComponentInteractionCreateEventArgs.
        '''
        ''' Phase 5d-5 item 4 — panelId parameter selects between
        ''' the panel-scoped override mapping and the guild-default
        ''' mapping. Whole-mapping override semantics: if a
        ''' panel-scoped mapping exists for (guildId, panelId),
        ''' it's authoritative and the guild-default is NOT
        ''' consulted. This lets an operator deny access to a role
        ''' at panel scope by omitting it from the override
        ''' mapping. Pass panelId = "" (or omit) to resolve
        ''' against the guild-default; the slash command surface
        ''' uses this path since it has no panel context.
        '''
        ''' Friend so callers in the same namespace (the slash
        ''' command class) can reach it; the underlying role
        ''' mapping cache is private state and shouldn't leak
        ''' to the rest of the assembly.
        '''
        ''' All failure modes return Everyone — see the
        ''' ResolvePermissionForInteraction summary for the
        ''' rationale.
        ''' </summary>
        Friend Function ResolveUserPermission(member As DiscordMember,
                                               guildId As String,
                                               Optional panelId As String = "") As CommandPermission
            Try
                If member Is Nothing Then Return CommandPermission.Everyone
                If String.IsNullOrEmpty(guildId) Then Return CommandPermission.Everyone

                Dim guildScope As Dictionary(Of String, Dictionary(Of String, CommandPermission)) = Nothing
                If Not _roleMappings.TryGetValue(guildId, guildScope) Then
                    Return CommandPermission.Everyone
                End If
                If guildScope Is Nothing OrElse guildScope.Count = 0 Then
                    Return CommandPermission.Everyone
                End If

                ' Pick the authoritative scope for this lookup.
                ' Whole-mapping override: if a panel override exists,
                ' use only that; otherwise fall back to guild-default.
                ' Both lookups can miss (panel without override and
                ' no guild-default — valid "nobody can manage this
                ' guild" config).
                Dim authoritativeScope As Dictionary(Of String, CommandPermission) = Nothing
                Dim panelKey = If(panelId, "")
                If panelKey <> "" AndAlso guildScope.TryGetValue(panelKey, authoritativeScope) Then
                    ' Found a panel-scoped override; use it.
                Else
                    guildScope.TryGetValue("", authoritativeScope)
                End If
                If authoritativeScope Is Nothing OrElse authoritativeScope.Count = 0 Then
                    Return CommandPermission.Everyone
                End If

                ' Walk the user's roles and track the highest
                ' tier any of them maps to. We can't early-out
                ' on first hit because a user with both a
                ' ServerOperator-mapped role and an Administrator-
                ' mapped role should resolve to Administrator —
                ' the highest applicable tier wins. Loop is short
                ' (typical guild member has < 10 roles) and the
                ' inner dict lookup is O(1).
                Dim highest = CommandPermission.Everyone
                For Each role In member.Roles
                    If role Is Nothing Then Continue For
                    Dim mapped As CommandPermission
                    If authoritativeScope.TryGetValue(role.Id.ToString(), mapped) Then
                        If mapped > highest Then highest = mapped
                    End If
                Next
                Return highest
            Catch ex As Exception
                _logger.LogDebug(ex, "ResolveUserPermission threw")
            End Try
            Return CommandPermission.Everyone
        End Function

        ''' <summary>
        ''' Build the ephemeral shown after Manage — a brief
        ''' header plus a single dropdown listing in-scope
        ''' instances. Discord caps select-component options at
        ''' 25, so panels with more than 25 in-scope instances
        ''' paginate: the dropdown shows one page of 25, prev/next
        ''' buttons step between pages, and a "Page X of Y"
        ''' indicator joins the prompt. Single-page panels see no
        ''' page indicator and no nav buttons (small-panel UX is
        ''' unchanged from before pagination shipped). The page
        ''' index is encoded in the prev/next button custom IDs
        ''' (gsm:page:{panelId}:{n}) so the handler is purely a
        ''' render request — no per-user paging state lives on
        ''' the manager between clicks. pageIndex is clamped here
        ''' so a stale ID pointing past the new last page (when
        ''' instances are removed mid-flow) lands on the final
        ''' page rather than producing an empty dropdown.
        ''' </summary>
        Private Function BuildManageEphemeralBuilder(
                p As DiscordPanelEntity,
                instances As List(Of InScopeInstance),
                Optional pageIndex As Integer = 0) As DiscordInteractionResponseBuilder
            ' Hard Discord limit on select-component options.
            ' Inlined as a literal because it's an external
            ' constraint, not a tunable.
            Const PageSize As Integer = 25

            Dim wb As New DiscordInteractionResponseBuilder()
            wb.AsEphemeral(True)

            Dim title = If(String.IsNullOrEmpty(p.DisplayName), "PowerGSM", p.DisplayName)

            If instances Is Nothing OrElse instances.Count = 0 Then
                wb.WithContent($"## Managing: {Escape(title)}{vbLf}{vbLf}_No instances in scope._")
                Return wb
            End If

            ' Compute paging state. totalPages is at least 1 so
            ' the "Page X of Y" math doesn't divide by zero on
            ' single-page panels (the > 1 guards below also keep
            ' those clean of nav UI). Clamp pageIndex into
            ' [0, totalPages - 1] in case the instance list shrank
            ' between Manage click and a Prev/Next click — a stale
            ' page index lands on the new last page instead of
            ' producing an empty dropdown (Discord rejects empty
            ' select components).
            Dim total = instances.Count
            Dim totalPages = Math.Max(1, CInt(Math.Ceiling(total / CDbl(PageSize))))
            If pageIndex < 0 Then pageIndex = 0
            If pageIndex > totalPages - 1 Then pageIndex = totalPages - 1

            Dim pageNote As String
            If totalPages > 1 Then
                pageNote = $" (Page {pageIndex + 1} of {totalPages})"
            Else
                pageNote = ""
            End If
            wb.WithContent($"## Managing: {Escape(title)}{vbLf}Pick an instance{pageNote}:")

            Dim pageStart = pageIndex * PageSize
            Dim pageEnd = Math.Min(pageStart + PageSize, total)

            Dim opts As New List(Of DiscordSelectComponentOption)
            For i = pageStart To pageEnd - 1
                Dim inst = instances(i)
                Dim emoji = StateEmoji(inst.State)
                Dim stateLabel = StateText(inst.State)
                ' Discord limits: option label 100 chars,
                ' description 100 chars. Truncate before adding
                ' so a long instance name doesn't reject the
                ' whole response.
                opts.Add(New DiscordSelectComponentOption(
                    label:=Truncate(If(inst.DisplayName, inst.InstanceId), 100),
                    value:=inst.InstanceId,
                    description:=Truncate($"{emoji} {stateLabel}", 100)))
            Next

            Dim dropdownPlaceholder As String
            If totalPages > 1 Then
                dropdownPlaceholder = $"Choose an instance (page {pageIndex + 1})..."
            Else
                dropdownPlaceholder = "Choose an instance..."
            End If
            Dim dropdown As New DiscordSelectComponent(
                customId:=$"gsm:select:{p.PanelId}",
                placeholder:=dropdownPlaceholder,
                options:=opts)
            wb.AddComponents(dropdown)

            ' Pagination row only when there's more than one page.
            ' With ≤25 instances the dropdown alone is enough; a
            ' row of two disabled buttons just adds visual noise.
            ' When >1, both buttons render but the one pointing
            ' at an out-of-range page is disabled (Discord shows
            ' it greyed out, which signals "end of list" cleanly).
            If totalPages > 1 Then
                Dim prevBtn As New DiscordButtonComponent(
                    ButtonStyle.Secondary,
                    customId:=$"gsm:page:{p.PanelId}:{pageIndex - 1}",
                    label:="◀ Prev",
                    disabled:=(pageIndex = 0))
                Dim nextBtn As New DiscordButtonComponent(
                    ButtonStyle.Secondary,
                    customId:=$"gsm:page:{p.PanelId}:{pageIndex + 1}",
                    label:="Next ▶",
                    disabled:=(pageIndex >= totalPages - 1))
                wb.AddComponents(prevBtn, nextBtn)
            End If

            Return wb
        End Function

        ''' <summary>
        ''' Build the ephemeral shown after the user picks an
        ''' instance from the dropdown — a header naming the
        ''' selection and three action buttons (Start/Stop/Restart).
        ''' Button enabled-state mirrors
        ''' InstancePanel.RefreshButtonsFromState in UiPanels.vb:
        ''' only the actions applicable to the instance's current
        ''' state are enabled. Inapplicable buttons stay visible
        ''' but greyed out, which keeps the layout stable across
        ''' states and makes the available actions obvious.
        ''' </summary>
        Private Function BuildActionsEphemeralBuilder(
                panelId As String,
                instanceId As String,
                displayName As String,
                state As InstanceState) As DiscordInteractionResponseBuilder
            Dim wb As New DiscordInteractionResponseBuilder()
            wb.AsEphemeral(True)

            Dim emoji = StateEmoji(state)
            Dim stateLabel = StateText(state)

            Dim startEnabled As Boolean
            Dim stopEnabled As Boolean
            Dim restartEnabled As Boolean
            ResolveActionAvailability(state, startEnabled, stopEnabled, restartEnabled)

            Dim hint As String
            If Not startEnabled AndAlso Not stopEnabled AndAlso Not restartEnabled Then
                hint = "_No actions available in this state._"
            Else
                hint = "Pick an action:"
            End If
            wb.WithContent($"## Selected: {Escape(displayName)}{vbLf}State: {emoji} {stateLabel}{vbLf}{vbLf}{hint}")

            ' Phase 5d-5 item 4: action custom_ids carry the
            ' panel ID so the click handler can enforce per-panel
            ' role overrides. Format: gsm:action:{panelId}:{instanceId}:{action}.
            ' (Pre-5d-5 the format was gsm:action:{instanceId}:{action};
            ' the dispatcher now requires the 5-part variant.)
            Dim startBtn As New DiscordButtonComponent(
                ButtonStyle.Success,
                customId:=$"gsm:action:{panelId}:{instanceId}:start",
                label:="Start",
                disabled:=Not startEnabled)
            Dim stopBtn As New DiscordButtonComponent(
                ButtonStyle.Danger,
                customId:=$"gsm:action:{panelId}:{instanceId}:stop",
                label:="Stop",
                disabled:=Not stopEnabled)
            Dim restartBtn As New DiscordButtonComponent(
                ButtonStyle.Primary,
                customId:=$"gsm:action:{panelId}:{instanceId}:restart",
                label:="Restart",
                disabled:=Not restartEnabled)
            wb.AddComponents(startBtn, stopBtn, restartBtn)
            Return wb
        End Function

        ''' <summary>
        ''' Map an instance state to which of (start, stop, restart)
        ''' should be enabled. CANONICAL SOURCE:
        ''' InstancePanel.RefreshButtonsFromState in UiPanels.vb —
        ''' keep in lockstep with that method's Select Case if the
        ''' manager's button policy ever changes. Both surfaces
        ''' should expose identical action gating so a Discord user
        ''' and a Manager-UI user see the same set of valid moves
        ''' for a given state.
        '''
        ''' Policy:
        '''   Running                    → Stop + Restart
        '''   Crashed                    → Start + Stop (Stop kept
        '''                                 enabled to break
        '''                                 crash-restart loops)
        '''   Stopped / CrashLoopHalted  → Start
        '''   Anything else (Starting,
        '''     Stopping, Updating,
        '''     WaitingForInput, unknown)→ all disabled
        ''' </summary>
        Private Shared Sub ResolveActionAvailability(
                state As InstanceState,
                ByRef startEnabled As Boolean,
                ByRef stopEnabled As Boolean,
                ByRef restartEnabled As Boolean)
            Select Case state
                Case InstanceState.Running
                    startEnabled = False
                    stopEnabled = True
                    restartEnabled = True
                Case InstanceState.Crashed
                    startEnabled = True
                    stopEnabled = True
                    restartEnabled = False
                Case InstanceState.Stopped, InstanceState.CrashLoopHalted
                    startEnabled = True
                    stopEnabled = False
                    restartEnabled = False
                Case Else
                    startEnabled = False
                    stopEnabled = False
                    restartEnabled = False
            End Select
        End Sub

        Private Shared Function Truncate(s As String, max As Integer) As String
            If String.IsNullOrEmpty(s) Then Return ""
            If s.Length <= max Then Return s
            If max <= 1 Then Return s.Substring(0, max)
            Return s.Substring(0, max - 1) & "…"
        End Function

        ' ============================================================
        '  Outbound destinations (Phase 5d-4)
        '
        '  Bot becomes a viable transport for the existing
        '  notification destination system — operators can route
        '  events to a (guild, channel) tuple via the bot identity
        '  alongside (or instead of) a webhook URL. Cache, filter,
        '  and dispatch logic mirror DiscordWebhookPlugin's
        '  patterns; embed rendering reuses the shared
        '  DiscordEmbedBuilder module directly so a template
        '  written for a webhook destination renders identically
        '  when applied to a bot destination, including custom
        '  templates and visibility profiles.
        ' ============================================================

        ''' <summary>
        ''' Public reload entry point for the Notifications form.
        ''' Re-reads NotificationDestinations + VisibilityProfiles
        ''' from the DB and rebuilds the in-memory cache. Called
        ''' after the form saves changes so the bot picks them up
        ''' without a reconnect cycle. Sync work wrapped in a Task
        ''' for API consistency with ReloadConfigAsync — the
        ''' underlying LoadDestinationsFromDb already handles its
        ''' own exceptions, so callers won't see them.
        ''' </summary>
        Public Function RefreshDestinationsConfigAsync() As Task
            LoadDestinationsFromDb()
            Return Task.CompletedTask
        End Function

        ''' <summary>
        ''' Synchronously fires a test embed to a (guild, channel)
        ''' pair without touching the DB — used by the Test button
        ''' in the Notifications form before the destination is
        ''' saved. Returns Nothing on success or a human-readable
        ''' error message on failure (mirrors
        ''' DiscordWebhookPlugin.SendTestAsync's contract).
        '''
        ''' Doesn't go through the destination queue — the queue's
        ''' debounce window would delay the test feedback by 1.5s
        ''' and add no value for a single one-shot send.
        ''' </summary>
        Public Async Function SendDestinationTestAsync(guildIdStr As String,
                                                        channelIdStr As String,
                                                        displayName As String,
                                                        cancellation As CancellationToken) As Task(Of String)
            If Not _connected OrElse _client Is Nothing Then
                Return "Bot is not connected. Open the Discord Bot tab and connect first."
            End If

            Dim guildId As ULong, channelId As ULong
            If Not ULong.TryParse(guildIdStr, guildId) Then Return "Invalid guild ID."
            If Not ULong.TryParse(channelIdStr, channelId) Then Return "Invalid channel ID."

            Dim guild As DiscordGuild = Nothing
            If Not _client.Guilds.TryGetValue(guildId, guild) Then
                Return "Bot is not in the selected guild."
            End If

            Dim channel As DiscordChannel = Nothing
            Try
                channel = guild.GetChannel(channelId)
            Catch
            End Try
            If channel Is Nothing Then Return "Channel not found in this guild."

            Try
                Dim builder As New DPEmbedBuilder()
                builder.WithTitle("📣 PowerGSM test message")
                builder.WithDescription(
                    $"If you're reading this, the bot destination for **{If(displayName, "")}** is working.")
                builder.WithColor(New DiscordColor(&H5865F2))
                Dim msg As New DiscordMessageBuilder()
                msg.AddEmbed(builder.Build())
                Await channel.SendMessageAsync(msg)
                Return Nothing
            Catch ex As UnauthorizedException
                Return $"Bot lacks permission to send in #{channel.Name}."
            Catch ex As Exception
                Return ex.Message
            End Try
        End Function

        ''' <summary>
        ''' Load DiscordBot-transport destinations and shared
        ''' visibility profiles from the DB. Called from
        ''' ConnectAsync after a successful connect, and from
        ''' RefreshDestinationsConfigAsync after the UI saves.
        ''' Builds fresh staging state, then atomic-swaps the
        ''' caches under _destCacheLock; concurrent SendNotificationAsync
        ''' calls always see a consistent snapshot.
        '''
        ''' Catches all exceptions internally — a missing table or
        ''' malformed JSON surfaces as an empty cache (no
        ''' destinations dispatched) rather than throwing, which
        ''' keeps Connect / Reload paths from failing over a
        ''' transient DB issue.
        ''' </summary>
        Private Sub LoadDestinationsFromDb()
            Try
                Dim newDestinations As New List(Of BotDestinationCacheEntry)
                Dim newProfiles As New Dictionary(Of String, VisibilityProfileCacheEntry)(StringComparer.OrdinalIgnoreCase)

                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    ' Profiles — same shape the webhook plugin uses,
                    ' so a profile defined for one transport applies
                    ' identically to the other.
                    For Each p In db.VisibilityProfiles.ToList()
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

                    ' Destinations filtered to TransportKind = "DiscordBot".
                    For Each d In db.NotificationDestinations.ToList()
                        If Not String.Equals(d.TransportKind, "DiscordBot",
                                              StringComparison.OrdinalIgnoreCase) Then
                            Continue For
                        End If
                        Dim entry = BuildBotDestinationCacheEntry(d)
                        If entry IsNot Nothing Then newDestinations.Add(entry)
                    Next
                End Using

                ' Atomic cache swap.
                SyncLock _destCacheLock
                    _destinationsCache = newDestinations
                    _destProfilesCache = newProfiles
                End SyncLock

                ' Drop queues for destinations that no longer exist.
                ' Queues for destinations that still exist are kept
                ' — their workers naturally pick up the (potentially
                ' updated) cache entry on the next dispatch via the
                ' BotQueuedMessage.Destination reference, which is
                ' captured at enqueue time.
                Dim aliveIds As New HashSet(Of String)(
                    newDestinations.Select(Function(d) d.DestinationId),
                    StringComparer.OrdinalIgnoreCase)
                For Each existingId In _destQueues.Keys.ToList()
                    If Not aliveIds.Contains(existingId) Then
                        Dim removed As BotDestinationQueue = Nothing
                        _destQueues.TryRemove(existingId, removed)
                    End If
                Next

                _logger.LogInformation(
                    "Discord bot outbound config reloaded: {Count} destination(s)",
                    newDestinations.Count)
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to reload Discord bot destinations")
            End Try
        End Sub

        ' ---- IDestinationTargetingPlugin ----

        Public Function OwnsDestination(destinationId As String) As Boolean _
                Implements IDestinationTargetingPlugin.OwnsDestination
            If String.IsNullOrEmpty(destinationId) Then Return False
            Dim destinations As IReadOnlyList(Of BotDestinationCacheEntry)
            SyncLock _destCacheLock
                destinations = _destinationsCache
            End SyncLock
            For Each d In destinations
                If String.Equals(d.DestinationId, destinationId,
                                  StringComparison.OrdinalIgnoreCase) Then
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
                cancellation As CancellationToken) As Task(Of Boolean) _
                Implements IDestinationTargetingPlugin.SendCustomToDestinationAsync

            If Not _initialized Then Return Task.FromResult(False)
            If Not _connected Then Return Task.FromResult(False)
            If String.IsNullOrEmpty(destinationId) Then Return Task.FromResult(False)

            Dim destinations As IReadOnlyList(Of BotDestinationCacheEntry)
            Dim profiles As Dictionary(Of String, VisibilityProfileCacheEntry)
            SyncLock _destCacheLock
                destinations = _destinationsCache
                profiles = _destProfilesCache
            End SyncLock

            Dim target As BotDestinationCacheEntry = Nothing
            For Each d In destinations
                If String.Equals(d.DestinationId, destinationId,
                                  StringComparison.OrdinalIgnoreCase) Then
                    target = d
                    Exit For
                End If
            Next
            If target Is Nothing Then
                _logger.LogWarning(
                    "SendCustomToDestinationAsync: bot destination {Id} not found in cache",
                    destinationId)
                Return Task.FromResult(False)
            End If
            If Not target.Enabled Then Return Task.FromResult(False)

            ' Build a Custom-event NotificationContext mirroring
            ' the webhook plugin's pattern. DiscordEmbedBuilder's
            ' Custom-event branch handles the literal-prose case
            ' without applying templates / visibility profiles —
            ' the rule author wrote final prose and presumably
            ' meant it.
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
                    "Failed to enqueue custom notification for {Name}",
                    target.DisplayName)
                Return Task.FromResult(False)
            End Try
        End Function

        ' ---- Dispatch internals ----

        Private Sub EnqueueForDestination(dest As BotDestinationCacheEntry,
                                           profile As VisibilityProfileCacheEntry,
                                           context As NotificationContext)
            ' Per-destination queue, lazily created. Worker reads
            ' the live _client through the Func closure on every
            ' dispatch, so reconnects don't strand the queue on a
            ' stale client reference.
            Dim q = _destQueues.GetOrAdd(dest.DestinationId,
                Function(id) New BotDestinationQueue(id,
                                                       Function() _client,
                                                       _logger))
            q.Enqueue(New BotQueuedMessage With {
                .Destination = dest,
                .Profile = profile,
                .Context = context
            })
        End Sub

        Private Shared Function BuildBotDestinationCacheEntry(
                e As NotificationDestinationEntity) As BotDestinationCacheEntry
            If e Is Nothing OrElse String.IsNullOrEmpty(e.TransportConfigJson) Then Return Nothing

            Dim guildId As String = Nothing
            Dim channelId As String = Nothing
            Try
                Dim transport = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(e.TransportConfigJson)
                If transport IsNot Nothing Then
                    transport.TryGetValue("GuildId", guildId)
                    transport.TryGetValue("ChannelId", channelId)
                End If
            Catch
            End Try
            ' Either missing field is fatal for dispatch — don't
            ' add to the cache, the UI will surface the misconfig.
            If String.IsNullOrWhiteSpace(guildId) OrElse String.IsNullOrWhiteSpace(channelId) Then
                Return Nothing
            End If

            Dim entry As New BotDestinationCacheEntry With {
                .DestinationId = e.DestinationId,
                .DisplayName = If(e.DisplayName, "(unnamed)"),
                .Enabled = e.Enabled,
                .GuildId = guildId,
                .ChannelId = channelId,
                .VisibilityProfileId = e.VisibilityProfileId
            }
            entry.EnabledEventTypes = ParseDestEnumSet(e.EnabledEventTypesJson)
            entry.InstallationFilter = ParseDestStringSet(e.InstallationFilterJson)
            entry.InstanceFilter = ParseDestStringSet(e.InstanceFilterJson)
            entry.TemplateOverrides = ParseDestTemplateOverrides(e.TemplateOverridesJson)
            Return entry
        End Function

        ' Filter-set parsers — same shape as the webhook plugin's
        ' privates but kept here to avoid coupling. Renamed with a
        ' "Dest" prefix so they don't collide with anything else
        ' in the (large) DiscordBotPlugin class.
        Private Shared Function ParseDestEnumSet(json As String) As HashSet(Of NotificationEventType)
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

        Private Shared Function ParseDestStringSet(json As String) As HashSet(Of String)
            Dim result As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
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

        Private Shared Function ParseDestTemplateOverrides(
                json As String) As Dictionary(Of NotificationEventType, String)
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

        ' ============================================================
        '  Supporting types
        ' ============================================================

        Private Class PanelRuntime
            Public Property PanelId As String
            Public Property PendingRefresh As Boolean = False
            Public Property LastRefreshUtc As DateTime = DateTime.MinValue
            Public ReadOnly Property RefreshLock As New SemaphoreSlim(1, 1)
        End Class

        Private Class InScopeInstance
            Public Property InstanceId As String
            Public Property DisplayName As String
            Public Property State As InstanceState
            Public Property PlayerCount As Integer?
            ''' <summary>
            ''' Game-specific "what's this server doing" descriptor.
            ''' Populated by BuildContextLine; empty when no useful
            ''' info is available (stopped instance, unknown game,
            ''' Factorio without SaveFile, LO before tile load).
            ''' </summary>
            Public Property ContextLine As String
            ''' <summary>
            ''' Next scheduled restart, in the kind it was computed
            ''' in. PowerGSM stores cron expressions in local time,
            ''' so the value is typically Local-kind — the renderer
            ''' converts to UTC via DateTimeOffset for the Discord
            ''' Unix-timestamp tag. Renamed from NextRestartUtc which
            ''' was misleading: the underlying source isn't UTC.
            ''' </summary>
            Public Property NextRestart As DateTime?
            ''' <summary>
            ''' Resolved node display name (Phase 5d-5 item 3).
            ''' Populated by ResolveInScopeInstances via a batched
            ''' Installations + Nodes lookup; empty string when the
            ''' instance's installation or node row can't be found.
            ''' Used by the NodeName layout element and by the
            ''' ByNode / ByNodeThenGame grouping passes.
            ''' </summary>
            Public Property NodeName As String
            ''' <summary>
            ''' Game ID (the same string used to dispatch plugins,
            ''' e.g. "lastoasis", "factorio"). Copied directly from
            ''' InstanceEntity.GameId; no display-name resolution
            ''' lookup, since plugins only expose their ID, not a
            ''' separate friendly label. Used by the ByGame /
            ''' ByNodeThenGame grouping passes.
            ''' </summary>
            Public Property GameId As String
        End Class

    End Class

    ' ============================================================
    '  DTOs returned from public API methods (consumed by the
    '  Discord Bot form). Kept in the same namespace as the
    '  plugin to avoid an extra Imports on the form side.
    ' ============================================================

    Public Class TestConnectionResult
        Public Property Success As Boolean
        Public Property Message As String
        Public Property BotUsername As String
        Public Property Guilds As List(Of TestGuildInfo) = New List(Of TestGuildInfo)
    End Class

    Public Class TestGuildInfo
        Public Property GuildId As String
        Public Property Name As String
    End Class

    Public Class GuildInfo
        Public Property GuildId As String
        Public Property Name As String
        Public Property Channels As List(Of ChannelInfo) = New List(Of ChannelInfo)
    End Class

    Public Class ChannelInfo
        Public Property ChannelId As String
        Public Property Name As String
    End Class

    ''' <summary>
    ''' Phase 5d-3 — one role within a guild, returned by
    ''' DiscordBotPlugin.GetGuildRoles for the role mapping UI.
    ''' Excludes @everyone and managed (integration) roles.
    ''' </summary>
    Public Class GuildRoleInfo
        Public Property RoleId As String
        Public Property Name As String
    End Class

    ''' <summary>
    ''' Phase 5d-4 — (InstanceId, DisplayName) pair returned by
    ''' DiscordBotPlugin.GetInstancesVisibleInGuild for the
    ''' /players slash command and its autocomplete provider.
    ''' Friend so it stays scoped to the bot integration; the
    ''' rest of the manager has no use for it.
    ''' </summary>
    Friend Class InstanceLookupEntry
        Public Property InstanceId As String
        Public Property DisplayName As String
    End Class

    ' ============================================================
    '  Bot destination dispatch — cache entry, queued message,
    '  per-destination worker (Phase 5d-4). Friend-scoped, used
    '  only inside this namespace by DiscordBotPlugin and
    '  BotDestinationQueue. Mirrors the webhook transport's
    '  DestinationCacheEntry / QueuedMessage / DestinationQueue
    '  trio in DestinationQueue.vb — same debounce window, same
    '  embed chunking, same exponential backoff. Different
    '  underlying transport (DSharpPlus client send vs HTTP POST).
    ' ============================================================

    Friend Class BotDestinationCacheEntry
        Public Property DestinationId As String
        Public Property DisplayName As String
        Public Property Enabled As Boolean
        ''' <summary>Discord guild ID (snowflake as decimal string).</summary>
        Public Property GuildId As String
        ''' <summary>Discord channel ID (snowflake as decimal string).</summary>
        Public Property ChannelId As String
        Public Property VisibilityProfileId As String
        Public Property EnabledEventTypes As HashSet(Of NotificationEventType)
        Public Property InstallationFilter As HashSet(Of String)
        Public Property InstanceFilter As HashSet(Of String)
        Public Property TemplateOverrides As Dictionary(Of NotificationEventType, String)

        ''' <summary>
        ''' True if this destination's filters match the event.
        ''' Identical logic to DestinationCacheEntry.MatchesEvent in
        ''' DiscordWebhookPlugin — kept duplicated rather than
        ''' factored to a shared helper because the cache entries
        ''' are otherwise transport-shaped (URL vs guild/channel)
        ''' and the filter rules are stable enough that drift is
        ''' unlikely.
        ''' </summary>
        Public Function MatchesEvent(context As NotificationContext) As Boolean
            If EnabledEventTypes IsNot Nothing AndAlso EnabledEventTypes.Count > 0 Then
                If Not EnabledEventTypes.Contains(context.EventType) Then Return False
            End If

            Dim tokens = context.Tokens
            If InstallationFilter IsNot Nothing AndAlso InstallationFilter.Count > 0 Then
                Dim installId = If(tokens Is Nothing, "", If(tokens.InstallationId, ""))
                If String.IsNullOrEmpty(installId) OrElse
                   Not InstallationFilter.Contains(installId) Then
                    Return False
                End If
            End If
            If InstanceFilter IsNot Nothing AndAlso InstanceFilter.Count > 0 Then
                Dim instanceId = If(tokens Is Nothing, "", If(tokens.InstanceId, ""))
                If String.IsNullOrEmpty(instanceId) OrElse
                   Not InstanceFilter.Contains(instanceId) Then
                    Return False
                End If
            End If
            Return True
        End Function
    End Class

    Friend Class BotQueuedMessage
        Public Property Destination As BotDestinationCacheEntry
        Public Property Profile As VisibilityProfileCacheEntry
        Public Property Context As NotificationContext
    End Class

    ''' <summary>
    ''' Per-destination batching/dispatch worker for the bot
    ''' transport. Mirrors DestinationQueue (which serves the
    ''' webhook transport) in structure and timings: 1.5s
    ''' debounce, max 10 embeds per message, exponential backoff
    ''' on transient failures. The transport itself is different
    ''' — sends via DSharpPlus's gateway+REST stack instead of an
    ''' HTTP webhook POST.
    '''
    ''' Lifecycle: created lazily on first Enqueue per destination,
    ''' worker exits only when both the live queue and the retry
    ''' buffer (see below) are empty. Receives a Func returning
    ''' the live DiscordClient so reconnects (which replace the
    ''' client instance) don't strand the queue on a stale
    ''' reference.
    '''
    ''' Failure handling (Phase 5d-5 v2):
    '''   • Permanent failures (403 Unauthorized, 404 NotFound,
    '''     400 BadRequest) are dropped silently with a single
    '''     warning log — retrying won't help and the operator
    '''     needs to fix the underlying configuration.
    '''   • Transient failures (rate limits, network blips, 5xx,
    '''     gateway disconnect) push the failed messages into a
    '''     bounded in-memory ring buffer (cap 100). The worker
    '''     drains the retry buffer first on every tick, ahead
    '''     of fresh queue contents, so event ordering is
    '''     preserved across the failure boundary.
    '''   • Buffer overflow (Discord down for long enough that
    '''     more than 100 events accumulate) drops the OLDEST
    '''     entries to bound memory growth; we'd rather lose
    '''     the start of an outage than OOM the manager. A
    '''     single warning log fires the first time the buffer
    '''     overflows during an outage; an info log fires when
    '''     the buffer drains back to empty.
    ''' </summary>
    Friend Class BotDestinationQueue

        ''' <summary>
        ''' Outcome of a single SendMessageAsync attempt. Drives
        ''' DispatchBatchAsync's per-slice fate decision: Sent and
        ''' PermanentFailure both move on to the next slice (the
        ''' messages are gone either way); TransientFailure pushes
        ''' the slice plus all remaining unsent slices into the
        ''' retry buffer and bails out of the dispatch.
        ''' </summary>
        Private Enum SendOutcome
            Sent
            PermanentFailure
            TransientFailure
        End Enum

        Private Const DebounceMillis As Integer = 1500
        Private Const MaxEmbedsPerMessage As Integer = 10
        Private Const MaxBackoffSeconds As Integer = 60
        ' Bounded retry buffer cap. ~100 events × ~5KB per
        ' rendered embed ≈ 500KB worst case per destination,
        ' fine even with many destinations. At the typical event
        ' rate (a handful per minute), 100 entries covers ~15-30
        ' minutes of buffered traffic — beyond that we'd rather
        ' drop the oldest than OOM.
        Private Const RetryBufferCapacity As Integer = 100

        Private ReadOnly _destinationId As String
        Private ReadOnly _clientGetter As Func(Of DiscordClient)
        Private ReadOnly _logger As ILogger
        Private ReadOnly _queue As New ConcurrentQueue(Of BotQueuedMessage)
        ' _retryBuffer is touched only by the worker thread (no
        ' concurrent writers from Enqueue, which always hits
        ' _queue), so it doesn't need a lock.
        Private ReadOnly _retryBuffer As New Queue(Of BotQueuedMessage)
        Private ReadOnly _workerLock As New Object()
        Private _workerRunning As Boolean = False
        Private _currentBackoff As Integer = 0
        ' Tracks whether we've already logged a "buffer full"
        ' warning for the ongoing outage. Reset to False the
        ' next time the retry buffer drains to empty after a
        ' successful dispatch — gives one warning per outage
        ' rather than one per dropped event.
        Private _retryBufferOverflowed As Boolean = False

        Public Sub New(destinationId As String,
                        clientGetter As Func(Of DiscordClient),
                        logger As ILogger)
            _destinationId = destinationId
            _clientGetter = clientGetter
            _logger = logger
        End Sub

        Public Sub Enqueue(msg As BotQueuedMessage)
            _queue.Enqueue(msg)
            EnsureWorkerRunning()
        End Sub

        Public Async Function FlushAsync(cancellation As CancellationToken) As Task
            ' Polling loop with a 10s deadline. Same shape as
            ' DestinationQueue.FlushAsync — messages can't queue
            ' faster than the worker drains them in normal
            ' shutdown conditions, and the deadline guards
            ' against a hung dispatch blocking manager exit.
            Dim deadline = DateTime.UtcNow.AddSeconds(10)
            While _workerRunning OrElse Not _queue.IsEmpty
                If DateTime.UtcNow > deadline Then Return
                If cancellation.IsCancellationRequested Then Return
                Await Task.Delay(100, cancellation)
            End While
        End Function

        Private Sub EnsureWorkerRunning()
            SyncLock _workerLock
                If _workerRunning Then Return
                _workerRunning = True
            End SyncLock
            Task.Run(Function() WorkerLoopAsync())
        End Sub

        Private Async Function WorkerLoopAsync() As Task
            Try
                While True
                    Await Task.Delay(DebounceMillis)

                    Dim batch As New List(Of BotQueuedMessage)
                    ' Drain the retry buffer first so previously
                    ' failed events ship ahead of newly enqueued
                    ' ones — preserves event order across the
                    ' failure boundary.
                    While _retryBuffer.Count > 0
                        batch.Add(_retryBuffer.Dequeue())
                    End While
                    Dim msg As BotQueuedMessage = Nothing
                    While _queue.TryDequeue(msg)
                        batch.Add(msg)
                    End While

                    If batch.Count = 0 Then
                        SyncLock _workerLock
                            ' Both inputs empty AND nothing's been
                            ' enqueued since we drained — safe to
                            ' stop. Re-check both because Enqueue
                            ' could've fired between the drain and
                            ' here, and DispatchBatchAsync might
                            ' have repopulated _retryBuffer.
                            If _queue.IsEmpty AndAlso _retryBuffer.Count = 0 Then
                                _workerRunning = False
                                Return
                            End If
                        End SyncLock
                        Continue While
                    End If

                    Await DispatchBatchAsync(batch)

                    ' Recovery log: if we previously warned about
                    ' buffer overflow and we've now successfully
                    ' drained back to empty, note that we're back
                    ' to normal operation. Counterpart to the
                    ' overflow warning in EnqueueRetry.
                    If _retryBuffer.Count = 0 AndAlso _retryBufferOverflowed Then
                        _logger.LogInformation(
                            "Bot destination queue {Id}: retry buffer drained, normal operation resumed",
                            _destinationId)
                        _retryBufferOverflowed = False
                    End If
                End While
            Catch ex As Exception
                _logger.LogError(ex,
                    "Bot destination queue {Id} worker faulted", _destinationId)
                SyncLock _workerLock
                    _workerRunning = False
                End SyncLock
            End Try
        End Function

        Private Async Function DispatchBatchAsync(batch As List(Of BotQueuedMessage)) As Task
            If batch Is Nothing OrElse batch.Count = 0 Then Return

            Dim client = _clientGetter()
            If client Is Nothing Then
                ' Gateway disconnected (intentional disconnect or
                ' brief reconnect window). Buffer everything for
                ' retry — the next worker tick will probably find
                ' a live client. No backoff bump: this is normal
                ' lifecycle, not a network blip.
                _logger.LogDebug(
                    "Bot destination queue {Id}: client disconnected, buffering {Count} message(s) for retry",
                    _destinationId, batch.Count)
                For Each m In batch
                    EnqueueRetry(m)
                Next
                Return
            End If

            Dim dest = batch(0).Destination

            ' Resolve guild + channel via the live client cache.
            ' Cheap — no REST calls. All three failure modes here
            ' (unparseable IDs, bot not in guild, channel not
            ' found) are operator-fixable misconfiguration or
            ' deliberately deleted resources, so we drop rather
            ' than buffer. If we're wrong about a particular case
            ' (e.g. a partial-cache reconnect transiently misses
            ' a guild we're actually in) the worst case is one
            ' batch lost, which matches v1.
            Dim guildId As ULong, channelId As ULong
            If Not ULong.TryParse(dest.GuildId, guildId) OrElse
               Not ULong.TryParse(dest.ChannelId, channelId) Then
                _logger.LogWarning(
                    "Bot destination {Name} has unparseable guild/channel ID — dropping batch",
                    dest.DisplayName)
                Return
            End If

            Dim guild As DiscordGuild = Nothing
            If Not client.Guilds.TryGetValue(guildId, guild) Then
                _logger.LogWarning(
                    "Bot destination {Name}: bot not in guild {Guild}",
                    dest.DisplayName, guildId)
                Return
            End If

            Dim channel As DiscordChannel = Nothing
            Try
                channel = guild.GetChannel(channelId)
            Catch ex As Exception
                _logger.LogDebug(ex,
                    "Bot destination {Name}: GetChannel({Channel}) threw",
                    dest.DisplayName, channelId)
            End Try
            If channel Is Nothing Then
                _logger.LogWarning(
                    "Bot destination {Name}: channel {Channel} not found",
                    dest.DisplayName, channelId)
                Return
            End If

            ' Build (msg, embed) pairs in parallel lists. Keeping
            ' the original BotQueuedMessage alongside its built
            ' embed lets us push the right messages back to the
            ' retry buffer on transient failure — without the
            ' parallel mapping we'd have to either rebuild on
            ' retry (cheap but redundant) or re-buffer the whole
            ' batch (causes duplicates if some slices already
            ' shipped). Embeds use the SHARED DiscordEmbedBuilder
            ' so the bot transport renders identically to the
            ' webhook transport for the same input.
            Dim itemMsgs As New List(Of BotQueuedMessage)
            Dim itemEmbeds As New List(Of DPEmbed)
            For Each m In batch
                Dim e = DiscordEmbedBuilder.Build(m.Context, m.Profile, dest.TemplateOverrides)
                If e Is Nothing Then Continue For
                Dim converted = ConvertToDSharpPlusEmbed(e)
                If converted IsNot Nothing Then
                    itemMsgs.Add(m)
                    itemEmbeds.Add(converted)
                End If
            Next
            If itemEmbeds.Count = 0 Then Return

            ' Walk slices via index (rather than For/Step + Skip)
            ' so the TransientFailure branch can cleanly re-buffer
            ' the failing slice + everything after it.
            Dim sliceStart = 0
            While sliceStart < itemEmbeds.Count
                Dim sliceLen = Math.Min(MaxEmbedsPerMessage, itemEmbeds.Count - sliceStart)
                Dim sliceEmbeds = itemEmbeds.GetRange(sliceStart, sliceLen)
                Dim outcome = Await SendWithBackoffAsync(channel, sliceEmbeds)
                Select Case outcome
                    Case SendOutcome.Sent
                        sliceStart += sliceLen
                    Case SendOutcome.PermanentFailure
                        ' Slice is gone for good (logged once
                        ' inside SendWithBackoffAsync). Continue
                        ' on to the next slice — different slices
                        ' might not all hit the same permanent
                        ' issue, though in practice one usually
                        ' implies the rest will too.
                        sliceStart += sliceLen
                    Case SendOutcome.TransientFailure
                        ' Buffer this slice and every later one.
                        ' Don't keep hammering Discord during
                        ' what's probably an outage; the worker
                        ' tick will retry on the next pass with
                        ' the bumped backoff already set on
                        ' _currentBackoff.
                        For i = sliceStart To itemMsgs.Count - 1
                            EnqueueRetry(itemMsgs(i))
                        Next
                        Return
                End Select
            End While
        End Function

        Private Async Function SendWithBackoffAsync(
                channel As DiscordChannel,
                embeds As List(Of DPEmbed)) As Task(Of SendOutcome)
            If _currentBackoff > 0 Then
                Await Task.Delay(TimeSpan.FromSeconds(_currentBackoff))
            End If

            Try
                Dim msg As New DiscordMessageBuilder()
                For Each emb In embeds
                    msg.AddEmbed(emb)
                Next
                Await channel.SendMessageAsync(msg)
                _currentBackoff = 0
                Return SendOutcome.Sent
            Catch ex As UnauthorizedException
                ' 403 — bot lacks send-message in this channel.
                ' Operator config issue; retrying won't help.
                _logger.LogWarning(
                    "Bot destination queue {Id}: unauthorized in #{Channel} — dropping slice",
                    _destinationId, channel.Name)
                Return SendOutcome.PermanentFailure
            Catch ex As NotFoundException
                ' 404 — channel/guild deleted between resolution
                ' and send. Won't recover; drop.
                _logger.LogWarning(
                    "Bot destination queue {Id}: target not found for #{Channel} — dropping slice",
                    _destinationId, channel.Name)
                Return SendOutcome.PermanentFailure
            Catch ex As BadRequestException
                ' 400 — malformed request. Almost always a bug in
                ' our embed building (e.g. embed > 6000 chars
                ' total, field value > 1024). Retrying won't fix
                ' it; drop and log loudly so the bug surfaces.
                _logger.LogError(ex,
                    "Bot destination queue {Id}: bad request sending to #{Channel} — dropping slice (likely an embed-building bug)",
                    _destinationId, channel.Name)
                Return SendOutcome.PermanentFailure
            Catch ex As Exception
                ' Catch-all for transient failures: rate limits
                ' (429 — DSharpPlus normally handles these
                ' internally and only throws after exhausting
                ' retries), 5xx responses, and network/HTTP
                ' exceptions. Exponential backoff up to 60s; the
                ' caller (DispatchBatchAsync) buffers the slice
                ' for retry on the next worker tick.
                _currentBackoff = If(_currentBackoff = 0, 1,
                                      Math.Min(_currentBackoff * 2, MaxBackoffSeconds))
                _logger.LogWarning(ex,
                    "Bot destination queue {Id} send failed; backing off {Secs}s",
                    _destinationId, _currentBackoff)
                Return SendOutcome.TransientFailure
            End Try
        End Function

        ''' <summary>
        ''' Push one failed message into the bounded retry buffer.
        ''' On overflow evicts the OLDEST entry (FIFO) so a long
        ''' Discord outage can't OOM the manager — we'd rather lose
        ''' the start of an outage's events than crash. Logs a
        ''' single warning the first time the buffer overflows in
        ''' an outage; the recovery log in WorkerLoopAsync pairs
        ''' with this one to bookend the outage in the logs without
        ''' spamming once-per-event during the gap.
        '''
        ''' Called only from the worker thread (DispatchBatchAsync),
        ''' so concurrent access to _retryBuffer is impossible by
        ''' construction — no lock needed.
        ''' </summary>
        Private Sub EnqueueRetry(msg As BotQueuedMessage)
            If _retryBuffer.Count >= RetryBufferCapacity Then
                _retryBuffer.Dequeue()
                If Not _retryBufferOverflowed Then
                    _logger.LogWarning(
                        "Bot destination queue {Id}: retry buffer full (cap {Cap}); dropping oldest events during ongoing outage",
                        _destinationId, RetryBufferCapacity)
                    _retryBufferOverflowed = True
                End If
            End If
            _retryBuffer.Enqueue(msg)
        End Sub

        ' ---- Embed translation (our JSON shape → DSharpPlus shape) ----

        Private Shared Function ConvertToDSharpPlusEmbed(
                src As DiscordEmbed) As DPEmbed
            If src Is Nothing Then Return Nothing
            Dim builder As New DPEmbedBuilder()
            If Not String.IsNullOrEmpty(src.Title) Then builder.WithTitle(src.Title)
            If Not String.IsNullOrEmpty(src.Description) Then builder.WithDescription(src.Description)
            builder.WithColor(New DiscordColor(src.Color))
            If Not String.IsNullOrEmpty(src.Timestamp) Then
                Dim parsedTs As DateTimeOffset
                If DateTimeOffset.TryParse(src.Timestamp, parsedTs) Then
                    builder.WithTimestamp(parsedTs)
                End If
            End If
            If src.Fields IsNot Nothing Then
                For Each f In src.Fields
                    builder.AddField(f.Name, f.Value, f.Inline)
                Next
            End If
            Return builder.Build()
        End Function

    End Class

End Namespace
