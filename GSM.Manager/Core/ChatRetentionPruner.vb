Imports System
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data

' ============================================================
'  ChatRetentionPruner — chat history garbage collector
'
'  Reads ChatRetentionDays from the AppSettings KV table
'  (default 90) and deletes ChatMessages rows older than
'  that window on a slow cadence. Runs as a simple long-lived
'  background task kicked off from ManagerProgram on startup
'  and cancelled on shutdown — no IHostedService abstraction
'  needed because the Manager isn't using the generic host.
'
'  Design points:
'    - Runs once per hour. Chat retention isn't time-sensitive.
'    - Each pass is wrapped in try/catch so a transient DB
'      error (SQLite lock contention, disk full, etc.) kills
'      one pass but doesn't kill the pruner.
'    - Uses ExecuteDelete() where available for a single-round-trip
'      bulk delete. EF Core 7+ supports this; we're on 8.
'    - Retention day value is read fresh each pass so changing
'      it via the (future) Settings UI takes effect on the next
'      tick without a restart.
'    - PlayerSessions is never pruned here. PlayerSessions
'      survives until the realm identity itself changes, per
'      the product decision: a nuked realm gets a new realm_id
'      which produces new SessionIdentity strings, naturally
'      orphaning the old rows. If we need to garbage-collect
'      orphaned PlayerSessions later, it'd be a separate
'      policy + pass.
' ============================================================

Namespace GSM.Manager.Core

    Public Class ChatRetentionPruner

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger(Of ChatRetentionPruner)

        Private _cts As CancellationTokenSource
        Private _task As Task

        ' One hour between passes. Fine-grained enough that changing
        ' the retention setting via the UI takes effect within an
        ' hour; coarse enough not to churn the DB.
        Private Const PassIntervalMs As Integer = 60 * 60 * 1000

        ' Delay before the first pass so we don't hit the DB during
        ' app startup when other services are also initializing.
        Private Const StartupDelayMs As Integer = 30 * 1000

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of ChatRetentionPruner))
            _serviceProvider = serviceProvider
            _logger = logger
        End Sub

        ''' <summary>
        ''' Starts the pruner's background task. Idempotent —
        ''' second and subsequent calls are no-ops.
        ''' </summary>
        Public Sub Start()
            If _cts IsNot Nothing Then Return
            _cts = New CancellationTokenSource()
            Dim token = _cts.Token
            _task = Task.Run(Function() RunAsync(token))
            _logger.LogInformation("ChatRetentionPruner started (pass interval {Interval}ms)",
                                   PassIntervalMs)
        End Sub

        ''' <summary>
        ''' Signals cancellation and awaits the background task.
        ''' Called from Manager shutdown.
        ''' </summary>
        Public Async Function StopAsync() As Task
            Dim cts = _cts
            If cts Is Nothing Then Return
            _cts = Nothing
            cts.Cancel()
            Try
                If _task IsNot Nothing Then Await _task
            Catch
            End Try
            cts.Dispose()
        End Function

        Private Async Function RunAsync(token As CancellationToken) As Task
            Try
                Await Task.Delay(StartupDelayMs, token)
            Catch
                Return
            End Try

            While Not token.IsCancellationRequested
                Try
                    Await RunOncePassAsync(token)
                Catch ex As Exception
                    _logger.LogWarning(ex, "ChatRetentionPruner pass failed")
                End Try

                Try
                    Await Task.Delay(PassIntervalMs, token)
                Catch
                    Return
                End Try
            End While
        End Function

        ''' <summary>
        ''' One pruning pass. Public so tests or an admin action
        ''' could trigger it on demand.
        '''
        ''' Scope intentionally LIMITED to ChatMessages. PlayerActivity,
        ''' PlayerSessions, and SessionHosts are all identity-scoped
        ''' (keyed by SessionIdentity) rather than time-scoped — they
        ''' lose value only when the identity itself goes away (realm
        ''' nuked → new realm_id → new SessionIdentity → old rows
        ''' become orphans and could be cleaned up via a separate
        ''' identity-aware pass if ever needed). Time-pruning them
        ''' would break "last seen" lookups months or years after a
        ''' player was on — which is the whole point of keeping that
        ''' data in the first place.
        ''' </summary>
        Public Async Function RunOncePassAsync(token As CancellationToken) As Task
            Using scope = _serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                Dim days = db.GetSettingInt(
                    GsmDataExtensions.SettingKeys.ChatRetentionDays,
                    GsmDataExtensions.DefaultChatRetentionDays)

                ' Sanity floor — a misconfigured 0 would delete
                ' everything on every pass. Treat <1 as "default"
                ' to protect users from themselves.
                If days < 1 Then
                    days = GsmDataExtensions.DefaultChatRetentionDays
                End If

                Dim cutoff = DateTime.UtcNow.AddDays(-days)

                ' EF Core 8 bulk delete — single round trip, no
                ' materialization. Returns count of deleted rows.
                Dim deleted = Await db.ChatMessages.
                    Where(Function(c) c.TimestampUtc < cutoff).
                    ExecuteDeleteAsync(token)

                If deleted > 0 Then
                    _logger.LogInformation(
                        "ChatRetentionPruner deleted {Count} chat message(s) older than {Days} days",
                        deleted, days)
                End If
            End Using
        End Function

    End Class

End Namespace