Imports System
Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data
Imports GSM.Utility

Namespace GSM.Manager.Core

    ''' <summary>Read-only view of a stored web session for UI
    ''' listing — never carries the cookie header itself.</summary>
    Public Class WebSessionInfo
        Public Property SessionKey As String
        Public Property CapturedByPluginId As String
        Public Property CapturedAtUtc As DateTime
        Public Property LastUsedUtc As DateTime?
        ''' <summary>True when the session is live in the in-memory
        ''' cache this run (vs. only persisted on disk).</summary>
        Public Property Cached As Boolean
    End Class

    ' ============================================================
    '  WebSessionStore — Phase 7-5 shared web-session store
    '
    '  Holds named web sessions (cookie headers) on behalf of
    '  utility plugins, so capture/persist/expiry plumbing lives in
    '  ONE place instead of being reimplemented per portal plugin.
    '  Sessions are keyed by a plugin-chosen convention
    '  "{site}:{account}" (e.g. "myrealm:default"); plugins sharing
    '  a key share the session — that IS the cross-plugin provision,
    '  with zero plugin→plugin coupling.
    '
    '  Storage: web_sessions table, cookie header DPAPI-encrypted
    '  via CredentialService.ProtectString (CurrentUser scope, same
    '  as Steam credentials) — retires the 7-4b plaintext-in-plugin-
    '  config wart. In-memory cache in front; LastUsedUtc is touched
    '  on DB load and save, not on every cache hit.
    '
    '  Prompt discipline (host-owned, was per-plugin in 7-4b):
    '  - One in-flight capture per key — concurrent requesters
    '    await the same dialog's result, never two dialogs.
    '  - After a cancelled/failed capture, the key is prompt-blocked
    '    for the rest of the Manager run; Invalidate clears the
    '    block (the manual re-arm path) — mirrors 7-4b's
    '    once-per-run semantics without a per-plugin flag.
    '
    '  NOT auto-login: captures are always the interactive WebView2
    '  dialog. Steam Guard/2FA is exactly what stored-password
    '  automation can't survive; CredentialService passwords are
    '  never used here.
    ' ============================================================

    Public Class WebSessionStore

        Private ReadOnly _logger As ILogger(Of WebSessionStore)
        Private ReadOnly _serviceProvider As IServiceProvider

        Private ReadOnly _gate As New Object()
        Private ReadOnly _cache As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _promptBlocked As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _inflight As New Dictionary(Of String, Task(Of String))(StringComparer.OrdinalIgnoreCase)

        Public Sub New(logger As ILogger(Of WebSessionStore), serviceProvider As IServiceProvider)
            _logger = logger
            _serviceProvider = serviceProvider
        End Sub

        ''' <summary>Lists stored sessions for the management UI.
        ''' Returns key/owner/timestamps only — NEVER the cookie
        ''' header. Reads from the DB (the durable record) and marks
        ''' which keys are also live in this run's cache.</summary>
        Public Function ListSessions() As List(Of WebSessionInfo)
            Dim result As New List(Of WebSessionInfo)
            Try
                Dim cachedKeys As HashSet(Of String)
                SyncLock _gate
                    cachedKeys = New HashSet(Of String)(_cache.Keys, StringComparer.OrdinalIgnoreCase)
                End SyncLock
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    For Each row In db.WebSessions.ToList()
                        result.Add(New WebSessionInfo With {
                            .SessionKey = row.SessionKey,
                            .CapturedByPluginId = row.CapturedByPluginId,
                            .CapturedAtUtc = row.CapturedAtUtc,
                            .LastUsedUtc = row.LastUsedUtc,
                            .Cached = cachedKeys.Contains(row.SessionKey)
                        })
                    Next
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to list web sessions")
            End Try
            Return result
        End Function

        ''' <summary>Per-plugin overload: only sessions captured by
        ''' pluginId. Backs IUtilityContext.ListWebSessions so a plugin
        ''' sees only its own accounts. Same key/owner/timestamps-only
        ''' contract as ListSessions() — never the cookie header.</summary>
        Public Function ListSessions(pluginId As String) As List(Of WebSessionInfo)
            If String.IsNullOrWhiteSpace(pluginId) Then Return New List(Of WebSessionInfo)
            Return ListSessions().Where(
                Function(s) String.Equals(s.CapturedByPluginId, pluginId, StringComparison.OrdinalIgnoreCase)).ToList()
        End Function

        ''' <summary>Host/UI use only (session validation): returns
        ''' the stored cookie header for a key WITHOUT ever opening a
        ''' capture dialog — cache first, then DB. Nothing when no
        ''' session is stored.</summary>
        Public Function PeekHeader(sessionKey As String) As String
            If String.IsNullOrWhiteSpace(sessionKey) Then Return Nothing
            SyncLock _gate
                Dim cached As String = Nothing
                If _cache.TryGetValue(sessionKey, cached) Then Return cached
            End SyncLock
            Dim stored = LoadFromDb(sessionKey)
            If stored IsNot Nothing Then
                SyncLock _gate
                    _cache(sessionKey) = stored
                End SyncLock
            End If
            Return stored
        End Function

        ''' <summary>Returns the session's cookie header, capturing it
        ''' first when absent (if allowed). Nothing = no session
        ''' available. Safe to call concurrently from any thread.</summary>
        Public Async Function GetOrCaptureAsync(pluginId As String,
                                                sessionKey As String,
                                                startUrl As String,
                                                completionUrlPattern As String,
                                                cookieDomain As String,
                                                allowPrompt As Boolean) As Task(Of String)
            If String.IsNullOrWhiteSpace(sessionKey) Then Return Nothing

            SyncLock _gate
                Dim cached As String = Nothing
                If _cache.TryGetValue(sessionKey, cached) Then Return cached
            End SyncLock

            ' Not cached — try the DB (decrypts; touches LastUsedUtc).
            Dim stored = LoadFromDb(sessionKey)
            If stored IsNot Nothing Then
                SyncLock _gate
                    _cache(sessionKey) = stored
                End SyncLock
                Return stored
            End If

            ' Absent — capture if allowed. Inside one lock so two
            ' concurrent callers can't start two dialogs: the second
            ' joins the first's in-flight task.
            Dim captureTask As Task(Of String)
            SyncLock _gate
                If Not _inflight.TryGetValue(sessionKey, captureTask) Then
                    If Not allowPrompt OrElse _promptBlocked.Contains(sessionKey) Then
                        Return Nothing
                    End If
                    captureTask = CaptureCoreAsync(pluginId, sessionKey, startUrl, completionUrlPattern, cookieDomain)
                    _inflight(sessionKey) = captureTask
                End If
            End SyncLock

            Return Await captureTask
        End Function

        ''' <summary>Removes the session (cache + DB) and clears its
        ''' prompt-block so the next allowPrompt request can open the
        ''' login dialog again.</summary>
        Public Sub Invalidate(pluginId As String, sessionKey As String)
            If String.IsNullOrWhiteSpace(sessionKey) Then Return
            SyncLock _gate
                _cache.Remove(sessionKey)
                _promptBlocked.Remove(sessionKey)
            End SyncLock
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = db.WebSessions.Find(sessionKey)
                    If row IsNot Nothing Then
                        db.WebSessions.Remove(row)
                        db.SaveChanges()
                    End If
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to delete web session '{Key}' from the database", sessionKey)
            End Try
            _logger.LogInformation("[{Plugin}] invalidated web session '{Key}'", pluginId, sessionKey)
        End Sub

        ''' <summary>Store-under-key: encrypt + persist + cache the
        ''' cookie header for sessionKey, stamped as captured by
        ''' pluginId. Backs IUtilityContext.StoreWebSession — the
        ''' explicit-save completion of a plugin-driven capture (vs.
        ''' GetOrCaptureAsync's dialog-driven save). Clears any
        ''' prompt-block (a live session makes the block moot).
        ''' Best-effort persist: a DB failure still leaves the header
        ''' usable in memory this run.</summary>
        Public Sub Store(pluginId As String, sessionKey As String, cookieHeader As String)
            If String.IsNullOrWhiteSpace(sessionKey) Then Return
            If String.IsNullOrEmpty(cookieHeader) Then
                _logger.LogWarning(
                    "[{Plugin}] Store called for '{Key}' with an empty cookie header — ignored",
                    pluginId, sessionKey)
                Return
            End If
            SaveToDb(sessionKey, cookieHeader, pluginId)
            SyncLock _gate
                _cache(sessionKey) = cookieHeader
                _promptBlocked.Remove(sessionKey)
            End SyncLock
            _logger.LogInformation("[{Plugin}] stored web session '{Key}'", pluginId, sessionKey)
        End Sub

        ' ------------------------------------------------------------

        Private Async Function CaptureCoreAsync(pluginId As String,
                                                sessionKey As String,
                                                startUrl As String,
                                                completionUrlPattern As String,
                                                cookieDomain As String) As Task(Of String)
            Dim header As String = Nothing
            Try
                _logger.LogInformation(
                    "[{Plugin}] requested shared web session '{Key}': start={Start}, completion contains '{Pattern}', cookies for {Domain}",
                    pluginId, sessionKey, startUrl, completionUrlPattern, cookieDomain)

                ' The form runs its own STA thread + modal pump — safe
                ' from any thread, never blocks the Manager's UI.
                Dim capture = Await UI.WebSessionCaptureForm.CaptureAsync(
                    pluginId, startUrl, completionUrlPattern, cookieDomain)

                If capture Is Nothing OrElse Not capture.Ok Then
                    BlockPrompts(sessionKey)
                    Dim reason = If(capture IsNot Nothing, capture.ErrorMessage, "no result")
                    _logger.LogInformation(
                        "Web-session capture for '{Key}' cancelled/failed ({Reason}) — prompts for this key are blocked until invalidation or restart.",
                        sessionKey, If(String.IsNullOrEmpty(reason), "cancelled", reason))
                Else
                    header = BuildHeader(capture)
                    If String.IsNullOrEmpty(header) Then
                        BlockPrompts(sessionKey)
                        _logger.LogWarning(
                            "Web-session capture for '{Key}' completed but yielded no cookies — prompts blocked until invalidation or restart.",
                            sessionKey)
                    Else
                        SaveToDb(sessionKey, header, pluginId)
                        SyncLock _gate
                            _cache(sessionKey) = header
                            _promptBlocked.Remove(sessionKey)
                        End SyncLock
                        _logger.LogInformation(
                            "Web session '{Key}' captured and stored (requested by {Plugin})",
                            sessionKey, pluginId)
                    End If
                End If
            Catch ex As Exception
                BlockPrompts(sessionKey)
                _logger.LogWarning(ex, "Web-session capture for '{Key}' threw", sessionKey)
            End Try

            SyncLock _gate
                _inflight.Remove(sessionKey)
            End SyncLock
            Return header
        End Function

        Private Sub BlockPrompts(sessionKey As String)
            SyncLock _gate
                _promptBlocked.Add(sessionKey)
            End SyncLock
        End Sub

        Private Shared Function BuildHeader(capture As WebSessionCaptureResult) As String
            Dim parts As New List(Of String)
            If capture.Cookies IsNot Nothing Then
                For Each c In capture.Cookies
                    If c Is Nothing OrElse String.IsNullOrEmpty(c.Name) Then Continue For
                    parts.Add(c.Name & "=" & If(c.Value, ""))
                Next
            End If
            If parts.Count = 0 Then Return Nothing
            Return String.Join("; ", parts)
        End Function

        Private Function LoadFromDb(sessionKey As String) As String
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = db.WebSessions.Find(sessionKey)
                    If row Is Nothing Then Return Nothing
                    Dim header = CredentialService.UnprotectString(row.EncryptedCookieHeader)
                    row.LastUsedUtc = DateTime.UtcNow
                    db.SaveChanges()
                    Return header
                End Using
            Catch ex As Exception
                ' DPAPI decrypt fails if the Windows user profile
                ' changed — treat as absent so a fresh capture heals it.
                _logger.LogWarning(ex,
                    "Failed to load/decrypt web session '{Key}' — treating as absent", sessionKey)
                Return Nothing
            End Try
        End Function

        Private Sub SaveToDb(sessionKey As String, header As String, pluginId As String)
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = db.WebSessions.Find(sessionKey)
                    If row Is Nothing Then
                        row = New WebSessionEntity With {.SessionKey = sessionKey}
                        db.WebSessions.Add(row)
                    End If
                    row.EncryptedCookieHeader = CredentialService.ProtectString(header)
                    row.CapturedAtUtc = DateTime.UtcNow
                    row.CapturedByPluginId = pluginId
                    row.LastUsedUtc = DateTime.UtcNow
                    db.SaveChanges()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to persist web session '{Key}' — it remains usable in memory this run", sessionKey)
            End Try
        End Sub

    End Class

End Namespace
