Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Net.Http
Imports System.Net.Http.Json
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports GSM.Automation
Imports GSM.Notification

' ============================================================
'  DestinationQueue — per-webhook background worker with a
'  short debounce window. Events arriving within ~2 seconds
'  of each other are combined into a single Discord message
'  with multiple embeds (up to Discord's limit of 10 per post).
'
'  Discord's rate limit is 5 messages / 2s per webhook, so the
'  2s debounce means we'll only ever hit one POST per window
'  regardless of how many events came in. No token bucket math.
'
'  Safety posture: every exception is caught and logged. A
'  webhook that consistently returns HTTP errors is backed off
'  exponentially (1s, 2s, 4s, up to 60s) and never impacts
'  other destinations or the Manager itself.
' ============================================================

Namespace GSM.Manager.Core

    Friend Class DestinationQueue

        Private Const DebounceMillis As Integer = 1500
        Private Const MaxEmbedsPerMessage As Integer = 10
        Private Const MaxBackoffSeconds As Integer = 60

        Private ReadOnly _destinationId As String
        Private ReadOnly _httpClient As HttpClient
        Private ReadOnly _logger As ILogger
        Private ReadOnly _queue As New ConcurrentQueue(Of QueuedMessage)
        Private ReadOnly _workerLock As New Object()
        Private _workerRunning As Boolean = False
        Private _currentBackoff As Integer = 0

        Public Sub New(destinationId As String, httpClient As HttpClient, logger As ILogger)
            _destinationId = destinationId
            _httpClient = httpClient
            _logger = logger
        End Sub

        Public Sub Enqueue(msg As QueuedMessage)
            _queue.Enqueue(msg)
            EnsureWorkerRunning()
        End Sub

        Public Async Function FlushAsync(cancellation As CancellationToken) As Task
            ' Wait for the worker to drain. Simple polling loop —
            ' messages can't be added from the Manager shutdown path
            ' faster than the worker drains them in normal conditions.
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
                    ' Wait for the debounce window so batching kicks in.
                    Await Task.Delay(DebounceMillis)

                    ' Drain whatever's accumulated.
                    Dim batch As New List(Of QueuedMessage)
                    Dim msg As QueuedMessage = Nothing
                    While _queue.TryDequeue(msg)
                        batch.Add(msg)
                    End While

                    If batch.Count = 0 Then
                        ' Nothing left — release worker lock and exit.
                        SyncLock _workerLock
                            If _queue.IsEmpty Then
                                _workerRunning = False
                                Return
                            End If
                        End SyncLock
                        Continue While
                    End If

                    ' All batched messages share a destination (since the
                    ' queue is per-destination) so we can group freely.
                    Await DispatchBatchAsync(batch)
                End While
            Catch ex As Exception
                _logger.LogError(ex, "Discord destination queue {Id} worker faulted", _destinationId)
                SyncLock _workerLock
                    _workerRunning = False
                End SyncLock
            End Try
        End Function

        Private Async Function DispatchBatchAsync(batch As List(Of QueuedMessage)) As Task
            If batch Is Nothing OrElse batch.Count = 0 Then Return

            Dim dest = batch(0).Destination
            Dim profile = batch(0).Profile

            ' Discord caps embeds at 10 per message — chunk if needed.
            Dim chunks = batch.
                Select(Function(m) DiscordEmbedBuilder.Build(m.Context, profile, dest.TemplateOverrides)).
                Where(Function(e) e IsNot Nothing).
                ToList()

            For i = 0 To chunks.Count - 1 Step MaxEmbedsPerMessage
                Dim slice = chunks.Skip(i).Take(MaxEmbedsPerMessage).ToList()
                Await PostWithBackoffAsync(dest.WebhookUrl, slice)
            Next
        End Function

        Private Async Function PostWithBackoffAsync(webhookUrl As String,
                                                      embeds As List(Of DiscordEmbed)) As Task
            If _currentBackoff > 0 Then
                Await Task.Delay(TimeSpan.FromSeconds(_currentBackoff))
            End If

            Dim payload As New DiscordWebhookPayload With {
                .Username = "PowerGSM",
                .Embeds = embeds
            }

            Try
                Using resp = Await _httpClient.PostAsJsonAsync(webhookUrl, payload)
                    If resp.IsSuccessStatusCode Then
                        _currentBackoff = 0
                        Return
                    End If

                    ' 429 = rate limited, 5xx = retry-worthy.
                    Dim code = CInt(resp.StatusCode)
                    If code = 429 OrElse code >= 500 Then
                        _currentBackoff = If(_currentBackoff = 0, 1,
                                              Math.Min(_currentBackoff * 2, MaxBackoffSeconds))
                        _logger.LogWarning("Discord webhook {Id} rate-limited/transient ({Code}); backing off {Secs}s",
                                            _destinationId, code, _currentBackoff)
                    Else
                        ' 4xx non-429 = bad config. Don't retry, but
                        ' don't crash either — log and drop.
                        Dim body = ""
                        Try : body = Await resp.Content.ReadAsStringAsync() : Catch : End Try
                        _logger.LogWarning("Discord webhook {Id} rejected ({Code} {Reason}): {Body}",
                                            _destinationId, code, resp.ReasonPhrase, body)
                    End If
                End Using
            Catch ex As Exception
                _currentBackoff = If(_currentBackoff = 0, 1,
                                      Math.Min(_currentBackoff * 2, MaxBackoffSeconds))
                _logger.LogWarning(ex, "Discord webhook {Id} post failed; backing off {Secs}s",
                                    _destinationId, _currentBackoff)
            End Try
        End Function

    End Class

    ' ============================================================
    '  DiscordEmbedBuilder — turns a NotificationContext into a
    '  Discord embed. Applies visibility profile filtering and
    '  per-event template overrides.
    ' ============================================================

    Friend Module DiscordEmbedBuilder

        ' Embed color per event kind (24-bit RGB).
        Private Const ColorSuccess As Integer = &H43B581  ' green
        Private Const ColorWarning As Integer = &HFAA61A  ' orange
        Private Const ColorDanger As Integer = &HF04747   ' red
        Private Const ColorInfo As Integer = &H5865F2     ' blurple

        Public Function Build(context As NotificationContext,
                              profile As VisibilityProfileCacheEntry,
                              templateMap As Dictionary(Of NotificationEventType, String)) As DiscordEmbed
            If context Is Nothing Then Return Nothing

            ' Custom event type — used exclusively by NotifyAction
            ' (rule-authored messages). Bypass the structured-field
            ' rendering entirely: the user wrote prose, that prose
            ' IS the message, and {Token} placeholders have already
            ' been substituted by NotificationService.SubstituteTokens
            ' before this point. Auto-rendering Node/Instance/
            ' Installation fields here would duplicate any context
            ' the user already referenced in their message, and
            ' clutter messages where they didn't.
            '
            ' Templates are also intentionally bypassed for Custom —
            ' the template system is for transforming structured
            ' event data into prose, but custom messages are already
            ' prose. (See "Phase 4b-1.5" in the reference doc.)
            '
            ' Color is derived from Severity rather than EventType
            ' since Custom doesn't carry inherent semantics (an
            ' Info note vs a Critical alert look the same at the
            ' EventType level but should look different visually).
            If context.EventType = NotificationEventType.Custom Then
                Dim body As String = If(context.Message, "")
                ' Defensive fallback: if the user's message used
                ' only tokens that all resolved to empty (e.g. wrong
                ' scope picked at authoring time), avoid posting an
                ' empty embed (Discord rejects those). Fall back to
                ' the rule name + a placeholder so the user can see
                ' which rule fired and fix the message.
                If String.IsNullOrWhiteSpace(body) Then
                    Dim ruleHint = If(context.Tokens?.RuleName, "(unknown rule)")
                    body = $"_(Rule '{ruleHint}' produced an empty message — check token usage.)_"
                End If
                Dim customEmbed As New DiscordEmbed With {
                    .Description = TruncateForDiscord(body, 4096),
                    .Color = ColorForSeverity(context.Severity),
                    .Timestamp = context.Timestamp.ToUniversalTime().ToString("o")
                }
                Return customEmbed
            End If

            ' Resolve override template first — decides whether we
            ' own the embed shell (default rendering) or the user's
            ' template does (template rendering).
            Dim templateOverride As String = Nothing
            If templateMap IsNot Nothing Then templateMap.TryGetValue(context.EventType, templateOverride)

            Dim embed As New DiscordEmbed()
            embed.Color = ColorFor(context.EventType)

            If Not String.IsNullOrEmpty(templateOverride) Then
                ' User-supplied template takes over the body completely.
                ' We deliberately leave Title and Timestamp unset so the
                ' template controls the whole rendered message — otherwise
                ' the auto-title ("⚪ Server stopped: …") and Discord's
                ' "Today at HH:MM" footer stack above/below the user's
                ' content, which was reported as confusing duplication.
                embed.Description = ApplyTokens(templateOverride, context, profile)
            Else
                embed.Title = BuildTitle(context)
                embed.Timestamp = context.Timestamp.ToUniversalTime().ToString("o")
                embed.Fields = BuildDefaultFields(context, profile)
            End If

            Return embed
        End Function

        ''' <summary>
        ''' Color for Custom events. Maps NotificationSeverity to
        ''' the existing ColorXxx palette. ErrorLevel and Critical
        ''' both render danger red — they're both "something is
        ''' wrong" signals; Discord doesn't really benefit from a
        ''' second red shade for Critical.
        ''' </summary>
        Private Function ColorForSeverity(s As NotificationSeverity) As Integer
            Select Case s
                Case NotificationSeverity.Critical, NotificationSeverity.ErrorLevel
                    Return ColorDanger
                Case NotificationSeverity.Warning
                    Return ColorWarning
                Case Else
                    Return ColorInfo
            End Select
        End Function

        Private Function BuildTitle(context As NotificationContext) As String
            Dim prefix = IconFor(context.EventType)
            Dim label = LabelFor(context.EventType)
            Dim tokens = context.Tokens
            Dim suffix = ""
            If tokens IsNot Nothing Then
                If Not String.IsNullOrEmpty(tokens.InstanceName) Then
                    suffix = $": {tokens.InstanceName}"
                ElseIf Not String.IsNullOrEmpty(tokens.InstallationName) Then
                    suffix = $": {tokens.InstallationName}"
                End If
            End If
            Return $"{prefix} {label}{suffix}"
        End Function

        Private Function IconFor(t As NotificationEventType) As String
            Select Case t
                Case NotificationEventType.InstanceStarted : Return "🟢"
                Case NotificationEventType.InstanceStopped : Return "⚪"
                Case NotificationEventType.InstanceCrashed : Return "🔴"
                Case NotificationEventType.CrashLoopDetected : Return "🛑"
                Case NotificationEventType.UpdateAvailable : Return "📦"
                Case NotificationEventType.UpdateStarted : Return "⬇️"
                Case NotificationEventType.UpdateCompleted : Return "✅"
                Case NotificationEventType.UpdateFailed : Return "❌"
                Case NotificationEventType.PlayerJoined : Return "👋"
                Case NotificationEventType.PlayerLeft : Return "👋"
                Case NotificationEventType.NodeOnline : Return "🟢"
                Case NotificationEventType.NodeOffline : Return "⚪"
                Case Else : Return "•"
            End Select
        End Function

        Private Function LabelFor(t As NotificationEventType) As String
            Select Case t
                Case NotificationEventType.InstanceStarted : Return "Server started"
                Case NotificationEventType.InstanceStopped : Return "Server stopped"
                ' InstanceCrashed is ONLY emitted when the node's
                ' crash policy decides to restart (see ProcessManager
                ' HandleProcessExited — PolicyAction.Halt produces
                ' CrashLoopDetected instead). So "restarting" is
                ' always accurate for this event.
                Case NotificationEventType.InstanceCrashed : Return "Server crashed, restarting"
                Case NotificationEventType.CrashLoopDetected : Return "Crash loop halted"
                Case NotificationEventType.UpdateAvailable : Return "Update available"
                Case NotificationEventType.UpdateStarted : Return "Update started"
                Case NotificationEventType.UpdateCompleted : Return "Update completed"
                Case NotificationEventType.UpdateFailed : Return "Update failed"
                Case NotificationEventType.PlayerJoined : Return "Player joined"
                Case NotificationEventType.PlayerLeft : Return "Player left"
                Case NotificationEventType.NodeOnline : Return "Node online"
                Case NotificationEventType.NodeOffline : Return "Node offline"
                Case Else : Return t.ToString()
            End Select
        End Function

        Private Function ColorFor(t As NotificationEventType) As Integer
            Select Case t
                Case NotificationEventType.InstanceStarted,
                     NotificationEventType.UpdateCompleted,
                     NotificationEventType.NodeOnline,
                     NotificationEventType.PlayerJoined
                    Return ColorSuccess
                Case NotificationEventType.InstanceCrashed,
                     NotificationEventType.CrashLoopDetected,
                     NotificationEventType.UpdateFailed,
                     NotificationEventType.NodeOffline
                    Return ColorDanger
                Case NotificationEventType.UpdateAvailable,
                     NotificationEventType.UpdateStarted
                    Return ColorWarning
                Case Else
                    Return ColorInfo
            End Select
        End Function

        ' ---- Default field layout ----

        Private Function BuildDefaultFields(context As NotificationContext,
                                             profile As VisibilityProfileCacheEntry) As List(Of DiscordEmbedField)
            Dim fields As New List(Of DiscordEmbedField)
            Dim tokens = context.Tokens

            If tokens IsNot Nothing Then
                AddIfAllowed(fields, profile, "NodeName", "Node", tokens.NodeName, inline:=True)
                AddIfAllowed(fields, profile, "InstanceName", "Instance", tokens.InstanceName, inline:=True)
                AddIfAllowed(fields, profile, "InstallationName", "Installation", tokens.InstallationName, inline:=True)
                AddIfAllowed(fields, profile, "GameName", "Game", tokens.GameName, inline:=True)
                AddIfAllowed(fields, profile, "PlayerName", "Player", tokens.PlayerName, inline:=True)
                AddIfAllowed(fields, profile, "TileName", "Tile", tokens.TileName, inline:=True)
                If tokens.PlayerCount.HasValue Then
                    Dim pc = tokens.PlayerCount.Value.ToString()
                    If tokens.MaxPlayers.HasValue Then pc &= $" / {tokens.MaxPlayers.Value}"
                    AddIfAllowed(fields, profile, "PlayerCount", "Players", pc, inline:=True)
                End If
                AddIfAllowed(fields, profile, "RuleName", "Rule", tokens.RuleName, inline:=True)
                AddIfAllowed(fields, profile, "ErrorMessage", "Error", tokens.ErrorMessage, inline:=False)

                ' Custom tokens — per-plugin/per-event payload
                If tokens.CustomTokens IsNot Nothing Then
                    For Each kvp In tokens.CustomTokens
                        AddIfAllowed(fields, profile, kvp.Key,
                                      PrettifyFieldName(kvp.Key), kvp.Value, inline:=True)
                    Next
                End If
            End If

            ' Fall back to the message text as description if no
            ' fields ended up visible (e.g. all filtered by profile).
            If fields.Count = 0 AndAlso Not String.IsNullOrEmpty(context.Message) Then
                fields.Add(New DiscordEmbedField With {
                    .Name = "Details",
                    .Value = TruncateForDiscord(context.Message, 1024),
                    .Inline = False
                })
            End If

            Return fields
        End Function

        Private Sub AddIfAllowed(fields As List(Of DiscordEmbedField),
                                  profile As VisibilityProfileCacheEntry,
                                  fieldName As String,
                                  label As String,
                                  value As String,
                                  inline As Boolean)
            If String.IsNullOrEmpty(value) Then Return
            If profile IsNot Nothing AndAlso Not profile.AllowsField(fieldName) Then Return
            fields.Add(New DiscordEmbedField With {
                .Name = label,
                .Value = TruncateForDiscord(value, 1024),
                .Inline = inline
            })
        End Sub

        Private Function PrettifyFieldName(raw As String) As String
            If String.IsNullOrEmpty(raw) Then Return raw
            ' Split "CamelCase" → "Camel Case" for display.
            Dim sb As New StringBuilder()
            For i = 0 To raw.Length - 1
                Dim c = raw(i)
                If i > 0 AndAlso Char.IsUpper(c) AndAlso Not Char.IsUpper(raw(i - 1)) Then
                    sb.Append(" "c)
                End If
                sb.Append(c)
            Next
            Return sb.ToString()
        End Function

        ' ---- Token substitution for custom templates ----

        Private Function ApplyTokens(template As String,
                                      context As NotificationContext,
                                      profile As VisibilityProfileCacheEntry) As String
            If String.IsNullOrEmpty(template) Then Return ""
            Dim sb As New StringBuilder(template)
            Dim tokens = context.Tokens
            ReplaceToken(sb, "{EventType}", context.EventType.ToString(), profile, "EventType")
            ReplaceToken(sb, "{Timestamp}", context.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), profile, "Timestamp")
            ReplaceToken(sb, "{Message}", context.Message, profile, "Message")
            If tokens IsNot Nothing Then
                ReplaceToken(sb, "{NodeName}", tokens.NodeName, profile, "NodeName")
                ReplaceToken(sb, "{NodeId}", tokens.NodeId, profile, "NodeId")
                ReplaceToken(sb, "{InstanceName}", tokens.InstanceName, profile, "InstanceName")
                ReplaceToken(sb, "{InstanceId}", tokens.InstanceId, profile, "InstanceId")
                ReplaceToken(sb, "{InstallationName}", tokens.InstallationName, profile, "InstallationName")
                ReplaceToken(sb, "{InstallationId}", tokens.InstallationId, profile, "InstallationId")
                ReplaceToken(sb, "{GameName}", tokens.GameName, profile, "GameName")
                ReplaceToken(sb, "{GameId}", tokens.GameId, profile, "GameId")
                ReplaceToken(sb, "{BuildId}", tokens.BuildId, profile, "BuildId")
                ReplaceToken(sb, "{TileName}", tokens.TileName, profile, "TileName")
                ReplaceToken(sb, "{TileId}", tokens.TileId, profile, "TileId")
                ReplaceToken(sb, "{PlayerName}", tokens.PlayerName, profile, "PlayerName")
                ReplaceToken(sb, "{PlayerCount}",
                              If(tokens.PlayerCount.HasValue, tokens.PlayerCount.Value.ToString(), ""),
                              profile, "PlayerCount")
                ReplaceToken(sb, "{MaxPlayers}",
                              If(tokens.MaxPlayers.HasValue, tokens.MaxPlayers.Value.ToString(), ""),
                              profile, "MaxPlayers")
                ReplaceToken(sb, "{RuleName}", tokens.RuleName, profile, "RuleName")
                ReplaceToken(sb, "{ErrorMessage}", tokens.ErrorMessage, profile, "ErrorMessage")
                If tokens.CustomTokens IsNot Nothing Then
                    For Each kvp In tokens.CustomTokens
                        ReplaceToken(sb, "{" & kvp.Key & "}", kvp.Value, profile, kvp.Key)
                    Next
                End If
            End If
            Return TruncateForDiscord(sb.ToString(), 4096)
        End Function

        Private Sub ReplaceToken(sb As StringBuilder,
                                  token As String,
                                  value As String,
                                  profile As VisibilityProfileCacheEntry,
                                  fieldName As String)
            Dim display As String
            If profile IsNot Nothing AndAlso Not profile.AllowsField(fieldName) Then
                display = "—"
            Else
                display = If(value, "")
            End If
            sb.Replace(token, display)
        End Sub

        Private Function TruncateForDiscord(s As String, maxLen As Integer) As String
            If String.IsNullOrEmpty(s) Then Return s
            If s.Length <= maxLen Then Return s
            Return s.Substring(0, maxLen - 1) & "…"
        End Function

    End Module

    ' ============================================================
    '  Discord webhook JSON payload types
    ' ============================================================

    Friend Class DiscordWebhookPayload
        <JsonPropertyName("username")>
        Public Property Username As String

        <JsonPropertyName("embeds")>
        Public Property Embeds As List(Of DiscordEmbed)
    End Class

    Friend Class DiscordEmbed
        <JsonPropertyName("title")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Title As String

        <JsonPropertyName("description")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Description As String

        <JsonPropertyName("color")>
        Public Property Color As Integer

        <JsonPropertyName("timestamp")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Timestamp As String

        <JsonPropertyName("fields")>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Fields As List(Of DiscordEmbedField)
    End Class

    Friend Class DiscordEmbedField
        <JsonPropertyName("name")>
        Public Property Name As String

        <JsonPropertyName("value")>
        Public Property Value As String

        <JsonPropertyName("inline")>
        Public Property Inline As Boolean
    End Class

End Namespace