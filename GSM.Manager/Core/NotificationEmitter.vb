Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data
Imports GSM.Notification
Imports GSM.Automation
Imports GSM.Node.Api

' ============================================================
'  NotificationEmitter — builds NotificationContext objects and
'  raises the Emitted event. Subscribers (e.g. NotificationService)
'  handle the actual dispatch to plugins.
'
'  This class deliberately does NOT depend on NotificationService.
'  That's what breaks the old circular dependency:
'     NotificationService -> InstanceManager -> NotificationEmitter
'  Now the arrow from emitter back to service is gone; the service
'  subscribes to the Emitted event in its own constructor instead.
'
'  Each call looks up the relevant entities (node, installation,
'  instance) once and builds a fully-populated NotificationContext
'  so plugin call sites don't repeat the lookup logic. All methods
'  are fire-and-forget — emissions never throw and never block.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>
    ''' EventArgs carrying a fully-built NotificationContext ready
    ''' for dispatch.
    ''' </summary>
    Public Class NotificationEmittedEventArgs
        Inherits EventArgs

        Public ReadOnly Property Context As NotificationContext

        Public Sub New(context As NotificationContext)
            Me.Context = context
        End Sub
    End Class

    Public Class NotificationEmitter

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger(Of NotificationEmitter)

        ''' <summary>
        ''' Raised once a NotificationContext has been built on a
        ''' background task. Handlers are invoked synchronously from
        ''' that task, so subscribers should do their own error
        ''' handling around any async work they kick off.
        ''' </summary>
        Public Event Emitted As EventHandler(Of NotificationEmittedEventArgs)

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of NotificationEmitter))
            _serviceProvider = serviceProvider
            _logger = logger
        End Sub

        Public Sub InstanceStarted(instanceId As String, pid As Integer?)
            FireAsync(NotificationEventType.InstanceStarted, instanceId, Nothing,
                      Sub(tokens, custom)
                          If pid.HasValue Then custom("PID") = pid.Value.ToString()
                      End Sub,
                      message:=Nothing)
        End Sub

        Public Sub InstanceStopped(instanceId As String, pid As Integer?, exitCode As Integer?)
            FireAsync(NotificationEventType.InstanceStopped, instanceId, Nothing,
                      Sub(tokens, custom)
                          If pid.HasValue Then custom("PID") = pid.Value.ToString()
                          If exitCode.HasValue Then custom("ExitCode") = exitCode.Value.ToString()
                      End Sub,
                      message:=Nothing)
        End Sub

        Public Sub InstanceCrashed(instanceId As String, exitCode As Integer?, errorMessage As String)
            FireAsync(NotificationEventType.InstanceCrashed, instanceId, Nothing,
                      Sub(tokens, custom)
                          tokens.ErrorMessage = errorMessage
                          If exitCode.HasValue Then custom("ExitCode") = exitCode.Value.ToString()
                      End Sub,
                      message:=errorMessage)
        End Sub

        Public Sub CrashLoopDetected(instanceId As String, crashCount As Integer, windowMinutes As Integer)
            FireAsync(NotificationEventType.CrashLoopDetected, instanceId, Nothing,
                      Sub(tokens, custom)
                          custom("CrashCount") = crashCount.ToString()
                          custom("WindowMinutes") = windowMinutes.ToString()
                      End Sub,
                      message:=$"Crash loop halted after {crashCount} crashes within {windowMinutes} minutes")
        End Sub

        Public Sub UpdateStarted(installationId As String)
            FireAsync(NotificationEventType.UpdateStarted, Nothing, installationId, Nothing, Nothing)
        End Sub

        Public Sub UpdateCompleted(installationId As String, buildId As String)
            FireAsync(NotificationEventType.UpdateCompleted, Nothing, installationId,
                      Sub(tokens, custom)
                          ' Override the stamp-extracted BuildId with
                          ' the one this update job just installed —
                          ' InstalledVersion may not have been rewritten
                          ' on disk by the time this notification fires.
                          If Not String.IsNullOrEmpty(buildId) Then tokens.BuildId = buildId
                      End Sub,
                      message:=Nothing)
        End Sub

        Public Sub UpdateFailed(installationId As String, errorMessage As String)
            FireAsync(NotificationEventType.UpdateFailed, Nothing, installationId,
                      Sub(tokens, custom)
                          tokens.ErrorMessage = errorMessage
                      End Sub,
                      message:=errorMessage)
        End Sub

        Public Sub PlayerJoined(instanceId As String, playerName As String)
            FireAsync(NotificationEventType.PlayerJoined, instanceId, Nothing,
                      Sub(tokens, custom) tokens.PlayerName = playerName,
                      message:=Nothing)
        End Sub

        Public Sub PlayerLeft(instanceId As String, playerName As String)
            FireAsync(NotificationEventType.PlayerLeft, instanceId, Nothing,
                      Sub(tokens, custom) tokens.PlayerName = playerName,
                      message:=Nothing)
        End Sub

        ' ---- Internal dispatch ----

        ''' <summary>
        ''' Raises the Emitted event. Wrapped in a helper so the
        ''' RaiseEvent call stays in method scope (lambdas can't host
        ''' RaiseEvent directly) and so subclasses/tests have a hook.
        ''' </summary>
        Protected Overridable Sub OnEmitted(args As NotificationEmittedEventArgs)
            RaiseEvent Emitted(Me, args)
        End Sub

        ' We take both instanceId and installationId because some events
        ' are instance-scoped and some are installation-scoped. When
        ' instanceId is provided we populate both (via FK lookup);
        ' when only installationId is provided we populate just that.
        Private Sub FireAsync(eventType As NotificationEventType,
                               instanceId As String,
                               installationId As String,
                               tokenCustomizer As Action(Of NotificationTokens, Dictionary(Of String, String)),
                               message As String)
            ' Background task — never blocks the caller, never throws.
            Task.Run(Async Function()
                         Try
                             Dim context = Await BuildContextAsync(eventType, instanceId,
                                                                     installationId, message,
                                                                     tokenCustomizer)
                             If context Is Nothing Then Return
                             OnEmitted(New NotificationEmittedEventArgs(context))
                         Catch ex As Exception
                             _logger.LogWarning(ex, "NotificationEmitter failed for {Event}", eventType)
                         End Try
                     End Function)
        End Sub

        Private Async Function BuildContextAsync(eventType As NotificationEventType,
                                                   instanceId As String,
                                                   installationId As String,
                                                   message As String,
                                                   tokenCustomizer As Action(Of NotificationTokens, Dictionary(Of String, String))) As Task(Of NotificationContext)
            Dim tokens As New NotificationTokens()
            tokens.CustomTokens = New Dictionary(Of String, String)

            Using scope = _serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                If Not String.IsNullOrEmpty(instanceId) Then
                    Dim inst = Await db.Instances.
                        Include(Function(i) i.Installation).
                        ThenInclude(Function(insta) insta.Node).
                        FirstOrDefaultAsync(Function(i) i.InstanceId = instanceId)
                    If inst IsNot Nothing Then
                        tokens.InstanceId = inst.InstanceId
                        tokens.InstanceName = inst.DisplayName
                        tokens.GameId = inst.GameId
                        If inst.Installation IsNot Nothing Then
                            tokens.InstallationId = inst.Installation.InstallationId
                            tokens.InstallationName = inst.Installation.DisplayName
                            tokens.BuildId = ExtractBuildId(inst.Installation.InstalledVersion)
                            If inst.Installation.Node IsNot Nothing Then
                                tokens.NodeId = inst.Installation.Node.NodeId
                                tokens.NodeName = inst.Installation.Node.DisplayName

                                ' Best-effort tile lookup. We hit the
                                ' node's /server-state endpoint to get
                                ' the current tile so Discord embeds
                                ' can show where a player joined/left.
                                ' Failures are silent — missing tile
                                ' info isn't worth losing the whole
                                ' notification over.
                                Try
                                    Dim factory = scope.ServiceProvider.GetService(Of NodeHttpClientFactory)()
                                    If factory IsNot Nothing Then
                                        Dim client = factory.GetClient(
                                            inst.Installation.Node.NodeId,
                                            inst.Installation.Node.HostAddress,
                                            inst.Installation.Node.Port,
                                            inst.Installation.Node.AuthToken)
                                        Dim state = Await client.GetServerStateAsync(instanceId, CancellationToken.None)
                                        If state IsNot Nothing Then
                                            tokens.TileId = If(state.TileId, "")
                                            tokens.TileName = If(state.TileName, "")
                                        End If
                                    End If
                                Catch
                                    ' Node unreachable or state not yet
                                    ' known; leave tile tokens empty.
                                End Try
                            End If
                        End If
                    End If
                ElseIf Not String.IsNullOrEmpty(installationId) Then
                    Dim install = Await db.Installations.
                        Include(Function(i) i.Node).
                        FirstOrDefaultAsync(Function(i) i.InstallationId = installationId)
                    If install IsNot Nothing Then
                        tokens.InstallationId = install.InstallationId
                        tokens.InstallationName = install.DisplayName
                        tokens.GameId = install.GameId
                        tokens.BuildId = ExtractBuildId(install.InstalledVersion)
                        If install.Node IsNot Nothing Then
                            tokens.NodeId = install.Node.NodeId
                            tokens.NodeName = install.Node.DisplayName
                        End If
                    End If
                End If
            End Using

            If tokenCustomizer IsNot Nothing Then
                Try
                    tokenCustomizer.Invoke(tokens, tokens.CustomTokens)
                Catch
                End Try
            End If

            Return New NotificationContext With {
                .EventType = eventType,
                .Severity = SeverityFor(eventType),
                .Message = If(message, ""),
                .Tokens = tokens,
                .Metadata = New Dictionary(Of String, String),
                .Timestamp = DateTime.UtcNow
            }
        End Function

        ''' <summary>
        ''' Maps event type to severity level. Uses the
        ''' NotificationSeverity enum from GSM.Automation — note the
        ''' ErrorLevel naming (Error is a VB reserved word).
        ''' </summary>
        Private Shared Function SeverityFor(t As NotificationEventType) As NotificationSeverity
            Select Case t
                Case NotificationEventType.CrashLoopDetected
                    Return NotificationSeverity.Critical
                Case NotificationEventType.InstanceCrashed,
                     NotificationEventType.UpdateFailed,
                     NotificationEventType.NodeOffline
                    Return NotificationSeverity.ErrorLevel
                Case NotificationEventType.UpdateAvailable,
                     NotificationEventType.UpdateStarted
                    Return NotificationSeverity.Warning
                Case Else
                    Return NotificationSeverity.Info
            End Select
        End Function

        ''' <summary>
        ''' Pulls the Steam buildid out of an InstalledVersion stamp.
        ''' The stamp is written by InstallationManager and (once a
        ''' version check has completed) looks like
        '''   "steam:440@public build 12345678"
        ''' Returns the captured buildid, or empty string if the stamp
        ''' predates a version check and carries only a timestamp
        ''' (e.g. "steam:440 (2025-01-01T00:00:00Z)") — the token will
        ''' render as empty in that case.
        ''' </summary>
        Private Shared Function ExtractBuildId(stamp As String) As String
            If String.IsNullOrEmpty(stamp) Then Return ""
            Dim m = System.Text.RegularExpressions.Regex.Match(stamp, "\bbuild\s+(\S+)")
            If m.Success Then Return m.Groups(1).Value
            Return ""
        End Function

    End Class

End Namespace