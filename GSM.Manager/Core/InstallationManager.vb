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
        Private ReadOnly _logger As ILogger(Of InstallationManager)
        Private ReadOnly _installLocks As New ConcurrentDictionary(Of String, SemaphoreSlim)

        Public Sub New(clientFactory As NodeHttpClientFactory,
                       pluginRegistry As PluginRegistry,
                       credentialService As CredentialService,
                       instanceManager As InstanceManager,
                       logger As ILogger(Of InstallationManager))
            _clientFactory = clientFactory
            _pluginRegistry = pluginRegistry
            _credentialService = credentialService
            _instanceManager = instanceManager
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

            Try
                ' Stop all instances using this installation
                instanceIds = _instanceManager.GetInstanceIdsForInstallation(installationId)
                For Each instId In instanceIds
                    Await _instanceManager.StopInstanceAsync(instId)
                Next
                _logger.LogInformation("Stopped {Count} instances for update of {Id}",
                                       instanceIds.Count, installationId)

                ' Wait a moment for processes to fully exit
                Await Task.Delay(2000, cancellation)

                ' Run update
                Dim ok = Await ExecuteInstallInternal(installationId, isUpdate:=True,
                                                      steamCredentialId:=steamCredentialId,
                                                      promptHandler:=promptHandler,
                                                      cancellation:=cancellation)

                ' Restart instances if requested and update succeeded
                If ok AndAlso restartAfter Then
                    For Each instId In instanceIds
                        Await _instanceManager.StartInstanceAsync(instId)
                    Next
                    _logger.LogInformation("Restarted {Count} instances after update",
                                           instanceIds.Count)
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
                Dim request As New InstallRequest With {
                    .InstallationId = installationId,
                    .GameId = installEntity.GameId,
                    .InstallPath = installEntity.InstallPath,
                    .Steps = steps.ToList(),
                    .SteamCredentials = steamCred
                }

                ' Send to node
                Dim client = _clientFactory.GetClient(
                    nodeEntity.NodeId, nodeEntity.HostAddress,
                    nodeEntity.Port, nodeEntity.AuthToken)

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
                        ' Update database with new version info
                        installEntity.UpdatedUtc = DateTime.UtcNow
                        db.SaveChanges()

                        _logger.LogInformation("{Op} completed for {Id}",
                                               If(isUpdate, "Update", "Install"), installationId)
                        Return True
                    Else
                        _logger.LogError("{Op} failed for {Id}: {Err}",
                                         If(isUpdate, "Update", "Install"), installationId,
                                         progress.ErrorMessage)
                        Return False
                    End If

                Catch ex As Exception
                    _logger.LogError(ex, "{Op} exception for {Id}",
                                     If(isUpdate, "Update", "Install"), installationId)
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

    End Class

End Namespace
