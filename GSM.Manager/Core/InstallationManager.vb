Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node.Api
Imports GSM.Manager
Imports GSM.Manager.Data

' ============================================================
'  InstallationManager — install/update operations
'
'  Coordinates between the plugin (which resolves install steps),
'  the credential service (which decrypts Steam passwords), and
'  the node (which executes the steps).
'
'  Uses a reader/writer lock per installation:
'    - Running instances are "readers" (many can coexist)
'    - Install/update operations are "writers" (exclusive)
'  This prevents updating files while instances are using them.
' ============================================================

Namespace GSM.Manager.Core

    Public Class InstallationManager

        Private ReadOnly _clientFactory As NodeHttpClientFactory
        Private ReadOnly _pluginRegistry As PluginRegistry
        Private ReadOnly _credentialService As CredentialService
        Private ReadOnly _instanceManager As InstanceManager
        Private ReadOnly _emitter As NotificationEmitter
        Private ReadOnly _logger As ILogger(Of InstallationManager)
        Private ReadOnly _installLocks As New ConcurrentDictionary(Of String, SemaphoreSlim)

        Public Sub New(clientFactory As NodeHttpClientFactory,
                       pluginRegistry As PluginRegistry,
                       credentialService As CredentialService,
                       instanceManager As InstanceManager,
                       emitter As NotificationEmitter,
                       logger As ILogger(Of InstallationManager))
            _clientFactory = clientFactory
            _pluginRegistry = pluginRegistry
            _credentialService = credentialService
            _instanceManager = instanceManager
            _emitter = emitter
            _logger = logger
        End Sub

        ' ============================================================
        '  Install
        ' ============================================================

        ''' <summary>
        ''' Performs a fresh installation. Resolves install steps
        ''' from the plugin, sends them to the node, polls for progress.
        ''' </summary>
        ''' <summary>
        ''' Callback for interactive prompts (Steam Guard, 2FA).
        ''' Parameters: promptType, message. Returns: user's response (or Nothing to cancel).
        ''' </summary>
        Public Delegate Function PromptHandlerDelegate(promptType As PromptType,
                                                        message As String) As Task(Of String)

        Public Async Function InstallAsync(installationId As String,
                                            Optional steamCredentialId As String = Nothing,
                                            Optional promptHandler As PromptHandlerDelegate = Nothing,
                                            Optional cancellation As CancellationToken = Nothing) As Task(Of Boolean)

            Dim lockSem = GetLock(installationId)
            Dim acquired = Await lockSem.WaitAsync(0)
            If Not acquired Then
                _logger.LogWarning("Installation {Id} is locked (in use or updating)", installationId)
                Return False
            End If

            Try
                Return Await ExecuteInstallInternal(installationId, isUpdate:=False,
                                                    steamCredentialId:=steamCredentialId,
                                                    promptHandler:=promptHandler,
                                                    cancellation:=cancellation)
            Finally
                lockSem.Release()
            End Try
        End Function

        ''' <summary>
        ''' Updates an existing installation. Stops all instances first,
        ''' runs update steps, then optionally restarts instances.
        ''' </summary>
        Public Async Function UpdateAsync(installationId As String,
                                           Optional steamCredentialId As String = Nothing,
                                           Optional promptHandler As PromptHandlerDelegate = Nothing,
                                           Optional restartAfter As Boolean = True,
                                           Optional cancellation As CancellationToken = Nothing) As Task(Of Boolean)

            Dim lockSem = GetLock(installationId)
            Dim acquired = Await lockSem.WaitAsync(0)
            If Not acquired Then
                _logger.LogWarning("Installation {Id} is locked", installationId)
                Return False
            End If

            Dim instanceIds As IReadOnlyList(Of String) = Nothing
            Dim runningBeforeUpdate As New List(Of String)

            Try
                ' Take inventory of what's using this installation.
                instanceIds = _instanceManager.GetInstanceIdsForInstallation(installationId)

                ' Capture pre-update state. We only want to touch
                ' instances that were actually Running (or Starting —
                ' those count as "user wanted this on") so we don't
                ' fire spurious Stopped notifications for instances
                ' that were already off, and we don't auto-launch
                ' instances the user had deliberately left stopped.
                For Each instId In instanceIds
                    Dim state = _instanceManager.GetLiveState(instId)
                    If state IsNot Nothing AndAlso
                       (state.CurrentState = GSM.Plugin.InstanceState.Running OrElse
                        state.CurrentState = GSM.Plugin.InstanceState.Starting) Then
                        runningBeforeUpdate.Add(instId)
                    End If
                Next

                ' Stop only the running ones.
                For Each instId In runningBeforeUpdate
                    Await _instanceManager.StopInstanceAsync(instId)
                Next
                _logger.LogInformation(
                    "Stopped {Running}/{Total} instances for update of {Id}",
                    runningBeforeUpdate.Count, instanceIds.Count, installationId)

                ' Wait a moment for processes to fully exit
                Await Task.Delay(2000, cancellation)

                ' Run update
                Dim ok = Await ExecuteInstallInternal(installationId, isUpdate:=True,
                                                      steamCredentialId:=steamCredentialId,
                                                      promptHandler:=promptHandler,
                                                      cancellation:=cancellation)

                ' Restart only the instances that were running before.
                ' Instances the user had intentionally stopped stay stopped.
                If ok AndAlso restartAfter Then
                    For Each instId In runningBeforeUpdate
                        Await _instanceManager.StartInstanceAsync(instId)
                    Next
                    _logger.LogInformation("Restarted {Count} instances after update",
                                           runningBeforeUpdate.Count)
                End If

                Return ok
            Finally
                lockSem.Release()
            End Try
        End Function

        ' ============================================================
        '  Internal install logic
        ' ============================================================

        Private Async Function ExecuteInstallInternal(installationId As String,
                                                       isUpdate As Boolean,
                                                       steamCredentialId As String,
                                                       promptHandler As PromptHandlerDelegate,
                                                       cancellation As CancellationToken) As Task(Of Boolean)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installEntity = db.Installations.Find(installationId)
                If installEntity Is Nothing Then
                    _logger.LogError("Installation {Id} not found", installationId)
                    Return False
                End If

                Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                If nodeEntity Is Nothing Then
                    _logger.LogError("Node {Id} not found for installation {InstId}",
                                     installEntity.NodeId, installationId)
                    Return False
                End If

                ' Resolve plugin
                Dim plugin = _pluginRegistry.GetPlugin(installEntity.GameId)
                If plugin Is Nothing Then
                    _logger.LogError("No plugin loaded for game {GameId}", installEntity.GameId)
                    Return False
                End If

                ' Build installation config
                Dim installConfig As New InstallationConfig With {
                    .InstallationId = installationId,
                    .GameId = installEntity.GameId,
                    .DisplayName = installEntity.DisplayName,
                    .InstallPath = installEntity.InstallPath,
                    .NodeId = installEntity.NodeId,
                    .CustomFields = DeserializeConfig(installEntity.ConfigJson)
                }
                installConfig.InstallMethod = ParseInstallMethod(installEntity.InstallMethod)

                ' Get install steps from plugin
                Dim steps As IReadOnlyList(Of InstallStep)
                If isUpdate Then
                    steps = plugin.GetUpdateSteps(installConfig)
                Else
                    steps = plugin.GetInstallSteps(installConfig)
                End If

                ' Resolve Steam credentials — use provided ID, or fall back
                ' to the credential stored on the installation entity
                Dim credId = steamCredentialId
                If String.IsNullOrEmpty(credId) Then
                    credId = installEntity.SteamCredentialId
                End If

                Dim steamCred As SteamCredential = Nothing
                If Not String.IsNullOrEmpty(credId) Then
                    steamCred = _credentialService.GetSteamCredentialForTransmit(db, credId)
                End If

                ' Build request
                ' Whether to run _CommonRedist after install. Default OFF —
                ' on non-elevated nodes this spawns UAC prompts for each
                ' bundled redist. The user opts in explicitly in the
                ' installation settings when the node runs elevated.
                Dim runRedist = installEntity.RunCommonRedist

                Dim request As New InstallRequest With {
                    .InstallationId = installationId,
                    .GameId = installEntity.GameId,
                    .InstallPath = installEntity.InstallPath,
                    .Steps = steps.ToList(),
                    .SteamCredentials = steamCred,
                    .RunCommonRedist = runRedist
                }

                ' Send to node
                Dim client = _clientFactory.GetClient(
                    nodeEntity.NodeId, nodeEntity.HostAddress,
                    nodeEntity.Port, nodeEntity.AuthToken)

                ' Emit UpdateStarted event — we fire this even for the
                ' fresh-install path; consumers that only care about
                ' updates can filter on isUpdate themselves (not exposed
                ' today). The "Started" signal is useful either way.
                If _emitter IsNot Nothing Then _emitter.UpdateStarted(installationId)

                Try
                    Dim progress = Await client.StartInstallAsync(request,
                        cancellation)

                    ' Poll for completion
                    While progress.OperationState <> InstallationOperationState.Completed AndAlso
                          progress.OperationState <> InstallationOperationState.Failed AndAlso
                          progress.OperationState <> InstallationOperationState.Cancelled

                        Await Task.Delay(2000, cancellation)
                        progress = Await client.GetInstallProgressAsync(installationId,
                            cancellation)

                        ' Handle interactive prompts (Steam Guard, 2FA)
                        If progress.OperationState = InstallationOperationState.WaitingForInput AndAlso
                           progress.PendingPromptType.HasValue Then

                            _logger.LogInformation("Install {Id} waiting for input: {Type}",
                                                   installationId, progress.PendingPromptType.Value)

                            Dim userResponse As String = Nothing
                            If promptHandler IsNot Nothing Then
                                userResponse = Await promptHandler.Invoke(
                                    progress.PendingPromptType.Value,
                                    If(progress.PendingPromptMessage, "Enter code:"))
                            End If

                            If Not String.IsNullOrEmpty(userResponse) Then
                                Dim promptResp As New PromptResponse With {
                                    .OperationId = installationId,
                                    .Value = userResponse,
                                    .Cancelled = False
                                }
                                Await client.RespondToPromptAsync(promptResp, cancellation)
                                _logger.LogInformation("Sent prompt response for {Id}", installationId)
                            Else
                                ' User cancelled or no handler — cancel the install
                                _logger.LogWarning("No response to prompt, cancelling install {Id}", installationId)
                                Return False
                            End If
                        End If

                        _logger.LogDebug("Install {Id}: {State} - {Step} ({Pct:F0}%)",
                                         installationId, progress.OperationState,
                                         progress.CurrentStepName, progress.ProgressPercent)
                    End While

                    If progress.OperationState = InstallationOperationState.Completed Then
                        ' Stamp a version string on the install. True
                        ' version tracking (e.g. Steam buildid from the
                        ' ACF manifest on the node) is a TODO — for now
                        ' we record a provenance string that tells us
                        ' the install method, the AppId (if SteamCMD),
                        ' and when this install completed. That's enough
                        ' to know whether a "Check for Updates" run is
                        ' called for based on install age, and it won't
                        ' falsely match against a future real buildid.
                        ' Stamp a provenance placeholder now — the real
                        ' Steam buildid will overwrite it on the first
                        ' Check for Updates (or we attempt it below if
                        ' the install included a SteamCmdStep).
                        installEntity.InstalledVersion = BuildVersionStamp(steps)
                        installEntity.UpdatedUtc = DateTime.UtcNow
                        db.SaveChanges()

                        _logger.LogInformation("{Op} completed for {Id}",
                                               If(isUpdate, "Update", "Install"), installationId)

                        ' Fire UpdateCompleted — consumers don't care
                        ' whether it was a fresh install or an update,
                        ' both boil down to "installation is now ready".
                        If _emitter IsNot Nothing Then _emitter.UpdateCompleted(installationId, Nothing)

                        ' Fire-and-forget a version check so the stored
                        ' InstalledVersion gets upgraded to the real
                        ' buildid without requiring the user to click
                        ' Check for Updates. We don't block the install
                        ' return on this — if the version check fails
                        ' for any reason, the synthetic stamp stays put.
                        Try
                            Dim _unused = Task.Run(Async Function()
                                                       Try
                                                           Await CheckForUpdatesAsync(installationId, CancellationToken.None)
                                                       Catch
                                                       End Try
                                                   End Function)
                        Catch
                        End Try

                        Return True
                    Else
                        _logger.LogError("{Op} failed for {Id}: {Err}",
                                         If(isUpdate, "Update", "Install"), installationId,
                                         progress.ErrorMessage)
                        If _emitter IsNot Nothing Then _emitter.UpdateFailed(installationId, progress.ErrorMessage)
                        Return False
                    End If

                Catch ex As Exception
                    _logger.LogError(ex, "{Op} exception for {Id}",
                                     If(isUpdate, "Update", "Install"), installationId)
                    If _emitter IsNot Nothing Then _emitter.UpdateFailed(installationId, ex.Message)
                    Return False
                End Try
            End Using
        End Function

        ' ============================================================
        '  Locking
        ' ============================================================

        Private Function GetLock(installationId As String) As SemaphoreSlim
            Return _installLocks.GetOrAdd(installationId,
                Function(id) New SemaphoreSlim(1, 1))
        End Function

        ''' <summary>
        ''' Returns whether an installation is currently locked
        ''' (being installed or updated).
        ''' </summary>
        Public Function IsLocked(installationId As String) As Boolean
            Dim sem As SemaphoreSlim = Nothing
            If Not _installLocks.TryGetValue(installationId, sem) Then Return False
            Return sem.CurrentCount = 0
        End Function

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Shared Function DeserializeConfig(json As String) As Dictionary(Of String, String)
            If String.IsNullOrEmpty(json) Then Return New Dictionary(Of String, String)
            Try
                Return JsonSerializer.Deserialize(Of Dictionary(Of String, String))(json)
            Catch
                Return New Dictionary(Of String, String)
            End Try
        End Function

        Private Shared Function ParseInstallMethod(value As String) As InstallMethod
            If String.IsNullOrEmpty(value) Then Return InstallMethod.Manual
            Dim result As InstallMethod
            If [Enum].TryParse(value, True, result) Then Return result
            Return InstallMethod.Manual
        End Function

        ''' <summary>
        ''' Builds a provenance version stamp after a successful
        ''' install/update. Prefers the Steam AppId + install timestamp
        ''' when SteamCMD was used; falls back to just a timestamp for
        ''' other install methods. True version tracking (Steam
        ''' buildid from the ACF manifest on the node) is a TODO that
        ''' will replace this once the node exposes an endpoint for it.
        ''' </summary>
        Private Shared Function BuildVersionStamp(steps As IReadOnlyList(Of InstallStep)) As String
            Dim timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")

            If steps IsNot Nothing Then
                For Each s In steps
                    Dim steamStep = TryCast(s, SteamCmdStep)
                    If steamStep IsNot Nothing Then
                        If Not String.IsNullOrEmpty(steamStep.BetaBranch) Then
                            Return $"steam:{steamStep.AppId}@{steamStep.BetaBranch} ({timestamp})"
                        End If
                        Return $"steam:{steamStep.AppId} ({timestamp})"
                    End If
                Next

                For Each s In steps
                    Dim dlStep = TryCast(s, DownloadFileStep)
                    If dlStep IsNot Nothing Then
                        Return $"download ({timestamp})"
                    End If
                Next
            End If

            Return $"installed ({timestamp})"
        End Function

        ''' <summary>
        ''' Reads the "RunCommonRedist" flag from the installation's
        ''' ConfigJson. Stored there rather than on the entity itself
        ''' so existing installations don't need an EF migration.
        ''' <summary>
        ''' Asks the node for a fast version check — compares installed
        ''' buildid against the latest available on Steam. Requires a
        ''' plugin that emits a SteamCmdStep in its update steps; other
        ''' install methods return a "not supported" result.
        ''' </summary>
        Public Async Function CheckForUpdatesAsync(installationId As String,
                                                     cancellation As CancellationToken) As Task(Of UpdateCheckResult)
            Dim result As New UpdateCheckResult()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installEntity = db.Installations.Find(installationId)
                If installEntity Is Nothing Then
                    result.ErrorMessage = "Installation not found"
                    Return result
                End If
                Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                If nodeEntity Is Nothing Then
                    result.ErrorMessage = "Node not found"
                    Return result
                End If

                Dim plugin = _pluginRegistry.GetPlugin(installEntity.GameId)
                If plugin Is Nothing Then
                    result.ErrorMessage = $"Plugin '{installEntity.GameId}' is not loaded"
                    Return result
                End If

                ' Build minimal InstallationConfig for plugin
                Dim installConfig As New InstallationConfig With {
                    .InstallationId = installationId,
                    .GameId = installEntity.GameId,
                    .DisplayName = installEntity.DisplayName,
                    .InstallPath = installEntity.InstallPath,
                    .NodeId = installEntity.NodeId,
                    .CustomFields = DeserializeConfig(installEntity.ConfigJson)
                }
                installConfig.InstallMethod = ParseInstallMethod(installEntity.InstallMethod)

                ' Pull first SteamCmdStep from update steps to get AppId + branch
                Dim updateSteps = plugin.GetUpdateSteps(installConfig)
                Dim steamStep As SteamCmdStep = Nothing
                If updateSteps IsNot Nothing Then
                    For Each s In updateSteps
                        steamStep = TryCast(s, SteamCmdStep)
                        If steamStep IsNot Nothing Then Exit For
                    Next
                End If
                If steamStep Is Nothing Then
                    result.ErrorMessage = "Update check is only supported for Steam-installed games"
                    Return result
                End If

                ' Resolve Steam credentials
                Dim steamCred As SteamCredential = Nothing
                If Not String.IsNullOrEmpty(installEntity.SteamCredentialId) Then
                    steamCred = _credentialService.GetSteamCredentialForTransmit(
                        db, installEntity.SteamCredentialId)
                End If

                Dim req As New AppVersionCheckRequest With {
                    .InstallationId = installationId,
                    .InstallPath = installEntity.InstallPath,
                    .AppId = steamStep.AppId,
                    .BetaBranch = steamStep.BetaBranch,
                    .SteamCredentials = steamCred
                }

                Try
                    Dim client = _clientFactory.GetClient(
                        nodeEntity.NodeId, nodeEntity.HostAddress,
                        nodeEntity.Port, nodeEntity.AuthToken)
                    Dim resp = Await client.CheckAppVersionAsync(req, cancellation)
                    result.InstalledBuildId = resp.InstalledBuildId
                    result.LatestBuildId = resp.LatestBuildId
                    result.UpdateAvailable = resp.UpdateAvailable
                    result.ErrorMessage = resp.ErrorMessage

                    ' Opportunistically update the stored InstalledVersion
                    ' with the real buildid read from the ACF manifest —
                    ' fixes up legacy rows that still have the synthetic
                    ' "steam:<appid> (timestamp)" placeholder, and keeps
                    ' it current without requiring a full reinstall.
                    If Not String.IsNullOrEmpty(resp.InstalledBuildId) Then
                        Dim newStamp = $"steam:{steamStep.AppId}@{If(steamStep.BetaBranch, "public")} build {resp.InstalledBuildId}"
                        If installEntity.InstalledVersion <> newStamp Then
                            installEntity.InstalledVersion = newStamp
                            installEntity.UpdatedUtc = DateTime.UtcNow
                            db.SaveChanges()
                        End If
                    End If
                Catch ex As Exception
                    result.ErrorMessage = ex.Message
                End Try
            End Using
            Return result
        End Function

    End Class

    ''' <summary>
    ''' Result of an update check — either a populated comparison or
    ''' an error message explaining why the check couldn't run.
    ''' </summary>
    Public Class UpdateCheckResult
        Public Property InstalledBuildId As String
        Public Property LatestBuildId As String
        Public Property UpdateAvailable As Boolean
        Public Property ErrorMessage As String
    End Class

End Namespace