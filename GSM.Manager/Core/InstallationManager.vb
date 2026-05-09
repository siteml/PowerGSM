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

        ' Live operations table for UI subscribers — populated when
        ' an install/update reaches the polling phase, updated on
        ' every poll tick, removed in the Finally that wraps the
        ' polling block. Panels query this on open to render any
        ' operation already in flight, then subscribe to the events
        ' below for incremental updates. Keyed by installationId
        ' since at most one operation per installation runs at a
        ' time (enforced by _installLocks).
        Private ReadOnly _activeOps As New ConcurrentDictionary(Of String, InstallProgressResponse)

        ' UI-subscription events. These run alongside the existing
        ' _emitter notifications (which feed Discord/webhooks) but
        ' target a different audience — InstallationPanel in the
        ' Manager UI, where operators want live byte/percent progress
        ' and tab-switching based on whether they kicked the
        ' operation off themselves vs. an automation rule firing in
        ' the background. Both audiences fire from the same lifecycle
        ' points so events stay consistent.
        '
        ' Raised from the polling thread (background ThreadPool) —
        ' subscribers that touch UI controls must marshal back to the
        ' UI thread on their own (Control.BeginInvoke). The Raise*
        ' helpers below wrap each event in a try/catch so a single
        ' subscriber that throws can't take down the polling loop.
        Public Event OperationStarted As EventHandler(Of InstallationOperationStartedEventArgs)
        Public Event ProgressChanged As EventHandler(Of InstallationProgressEventArgs)
        Public Event OperationCompleted As EventHandler(Of InstallationOperationCompletedEventArgs)

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
                                            Optional userInitiated As Boolean = False,
                                            Optional cancellation As CancellationToken = Nothing) As Task(Of InstallationOperationResult)

            Dim lockSem = GetLock(installationId)
            Dim acquired = Await lockSem.WaitAsync(0)
            If Not acquired Then
                _logger.LogWarning("Installation {Id} is locked (in use or updating)", installationId)
                Return InstallationOperationResult.Fail(
                    "This installation is currently in use by another operation (an install, update, or running instance).")
            End If

            Try
                Return Await ExecuteInstallInternal(installationId, isUpdate:=False,
                                                    steamCredentialId:=steamCredentialId,
                                                    promptHandler:=promptHandler,
                                                    userInitiated:=userInitiated,
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
                                           Optional userInitiated As Boolean = False,
                                           Optional cancellation As CancellationToken = Nothing) As Task(Of InstallationOperationResult)

            Dim lockSem = GetLock(installationId)
            Dim acquired = Await lockSem.WaitAsync(0)
            If Not acquired Then
                _logger.LogWarning("Installation {Id} is locked", installationId)
                Return InstallationOperationResult.Fail(
                    "This installation is currently in use by another operation (an install, update, or running instance).")
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
                                                      userInitiated:=userInitiated,
                                                      cancellation:=cancellation)

                ' Restart only the instances that were running before.
                ' Instances the user had intentionally stopped stay stopped.
                If ok.Success AndAlso restartAfter Then
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
                                                       userInitiated As Boolean,
                                                       cancellation As CancellationToken) As Task(Of InstallationOperationResult)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installEntity = db.Installations.Find(installationId)
                If installEntity Is Nothing Then
                    _logger.LogError("Installation {Id} not found", installationId)
                    Return InstallationOperationResult.Fail("Installation not found in the database.")
                End If

                Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                If nodeEntity Is Nothing Then
                    _logger.LogError("Node {Id} not found for installation {InstId}",
                                     installEntity.NodeId, installationId)
                    Return InstallationOperationResult.Fail(
                        "The node assigned to this installation could not be found in the database.")
                End If

                ' Resolve the node client up front so we can fetch
                ' its OS platform before invoking plugin methods.
                ' Plugins use the platform answer to pick install
                ' steps and post-install touch-ups specific to the
                ' target OS (e.g. UE4-on-Linux's steamclient.so
                ' symlink dance). NodeHttpClient caches the version
                ' response per-client, so this stays cheap on every
                ' call past the first per node lifetime.
                Dim client = _clientFactory.GetClient(
                    nodeEntity.NodeId, nodeEntity.HostAddress,
                    nodeEntity.Port, nodeEntity.AuthToken)
                Dim nodePlatform = Await NodePlatformResolver.ResolveAsync(client, cancellation)

                ' Resolve plugin
                Dim plugin = _pluginRegistry.GetPlugin(installEntity.GameId)
                If plugin Is Nothing Then
                    _logger.LogError("No plugin loaded for game {GameId}", installEntity.GameId)
                    Return InstallationOperationResult.Fail(
                        $"No plugin is loaded for game '{installEntity.GameId}'. Reload plugins and try again.")
                End If

                ' Build installation config
                Dim installConfig As New InstallationConfig With {
                    .InstallationId = installationId,
                    .GameId = installEntity.GameId,
                    .DisplayName = installEntity.DisplayName,
                    .InstallPath = installEntity.InstallPath,
                    .NodeId = installEntity.NodeId,
                    .CustomFields = DeserializeConfig(installEntity.ConfigJson),
                    .Platform = nodePlatform
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
                ' (client already resolved above, before the plugin
                ' call, so we could pre-fetch the node's platform.)

                ' Emit UpdateStarted event — we fire this even for the
                ' fresh-install path; consumers that only care about
                ' updates can filter on isUpdate themselves (not exposed
                ' today). The "Started" signal is useful either way.
                If _emitter IsNot Nothing Then _emitter.UpdateStarted(installationId)

                ' Local state for the Finally block: `started` flips
                ' to True once we're past the StartInstallAsync call
                ' (so the Finally knows whether to fire
                ' OperationCompleted and clean up _activeOps); `result`
                ' holds the eventual return value so we can capture
                ' it in early-exit paths and surface it through the
                ' Finally cleanup before returning.
                Dim started As Boolean = False
                Dim result As InstallationOperationResult = Nothing

                Try
                    Dim progress = Await client.StartInstallAsync(request,
                        cancellation)

                    ' Mark active and notify UI subscribers. Order
                    ' matters: _activeOps populated first so a
                    ' subscriber querying GetActiveProgress from
                    ' inside its own OperationStarted handler sees
                    ' a populated state; `started = True` last so
                    ' the Finally only triggers cleanup after the
                    ' dictionary is in a consistent state.
                    _activeOps(installationId) = progress
                    started = True
                    RaiseOperationStarted(installationId, isUpdate, userInitiated)
                    RaiseProgressChanged(installationId, progress)

                    ' Poll for completion
                    While progress.OperationState <> InstallationOperationState.Completed AndAlso
                          progress.OperationState <> InstallationOperationState.Failed AndAlso
                          progress.OperationState <> InstallationOperationState.Cancelled

                        Await Task.Delay(2000, cancellation)
                        progress = Await client.GetInstallProgressAsync(installationId,
                            cancellation)

                        ' Surface this tick to UI subscribers. The
                        ' panel uses ProgressChanged to update its
                        ' progress bar / phase label / byte counter
                        ' between polls — same data the existing
                        ' _logger.LogDebug below also reports.
                        _activeOps(installationId) = progress
                        RaiseProgressChanged(installationId, progress)

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
                                ' User cancelled or no handler — cancel
                                ' the install. Exit Try (rather than
                                ' Return) so the Finally below fires
                                ' OperationCompleted and cleans up
                                ' _activeOps before we return.
                                _logger.LogWarning("No response to prompt, cancelling install {Id}", installationId)
                                result = InstallationOperationResult.Fail(
                                    "Cancelled: no response provided to a Steam Guard / two-factor prompt.")
                                Exit Try
                            End If
                        End If

                        _logger.LogDebug("Install {Id}: {State} - {Step} ({Pct:F0}%)",
                                         installationId, progress.OperationState,
                                         progress.CurrentStepName, progress.ProgressPercent)
                    End While

                    If progress.OperationState = InstallationOperationState.Completed Then
                        ' Stamp a version string on the install. Three
                        ' paths in priority order:
                        '
                        '   1. Non-SteamCMD installs with an
                        '      IVersionAwarePlugin (e.g. Factorio):
                        '      ask the plugin to read the actual
                        '      version off disk in the same format its
                        '      GetLatestVersionAsync returns. Critical
                        '      for the version-check comparison
                        '      ("up to date" vs "update available")
                        '      to render correctly — a synthetic
                        '      "download (timestamp)" stamp can
                        '      never match upstream's "2.0.76".
                        '
                        '   2. SteamCMD installs: use the buildid the
                        '      node captured from appmanifest_{appid}.acf
                        '      after the SteamCMD step finished. Builds
                        '      "steam:{appId}@{branch} build {N}" —
                        '      the same shape VersionCheckService
                        '      produces from app_info_print, so the
                        '      label flips to "up to date"
                        '      immediately. Replaces the previous
                        '      timestamp-placeholder + fire-and-forget
                        '      upgrade pattern, which left the UI
                        '      showing "update available" for the
                        '      ~10-20s window between completion and
                        '      the upgrade landing (and didn't refresh
                        '      the UI when it did).
                        '
                        '   3. Fallback: BuildVersionStamp produces a
                        '      synthetic "installed (timestamp)"
                        '      placeholder. Used when neither path
                        '      above produced a stamp — e.g. an old
                        '      node that doesn't populate
                        '      InstalledBuildId, a SteamCMD install
                        '      that didn't write an ACF, a plugin
                        '      whose GetInstalledVersionAsync threw.
                        Dim stampedVersion As String = Nothing
                        Dim hasRealStamp As Boolean = False

                        ' Path 1: plugin-driven version read for
                        ' non-SteamCMD installs.
                        If installConfig.InstallMethod <> InstallMethod.SteamCmd Then
                            Dim versionAware = TryCast(plugin, IVersionAwarePlugin)
                            If versionAware IsNot Nothing Then
                                Try
                                    stampedVersion = Await versionAware.GetInstalledVersionAsync(
                                        installConfig, client, cancellation)
                                Catch ex As Exception
                                    _logger.LogDebug(ex,
                                        "Plugin GetInstalledVersionAsync threw for {Id}; falling back",
                                        installationId)
                                End Try
                            End If
                            If Not String.IsNullOrEmpty(stampedVersion) Then hasRealStamp = True
                        End If

                        ' Path 2: SteamCMD buildid captured by the node.
                        If String.IsNullOrEmpty(stampedVersion) Then
                            Dim capturedBuildId = If(progress IsNot Nothing, progress.InstalledBuildId, "")
                            If Not String.IsNullOrEmpty(capturedBuildId) Then
                                Dim steamStep As SteamCmdStep = Nothing
                                For Each s In steps
                                    steamStep = TryCast(s, SteamCmdStep)
                                    If steamStep IsNot Nothing Then Exit For
                                Next
                                If steamStep IsNot Nothing Then
                                    Dim branchName = If(String.IsNullOrEmpty(steamStep.BetaBranch),
                                                          "public", steamStep.BetaBranch)
                                    stampedVersion = $"steam:{steamStep.AppId}@{branchName} build {capturedBuildId}"
                                    hasRealStamp = True
                                End If
                            End If
                        End If

                        ' Path 3: synthetic placeholder fallback.
                        If String.IsNullOrEmpty(stampedVersion) Then
                            stampedVersion = BuildVersionStamp(steps)
                            ' hasRealStamp stays False — a synthetic
                            ' stamp won't compare cleanly against
                            ' upstream values, so we don't propagate
                            ' it to LatestKnownVersion below.
                        End If

                        installEntity.InstalledVersion = stampedVersion

                        ' By definition the install just succeeded,
                        ' so the installed version IS the latest of
                        ' the requested branch as of right now.
                        ' Update LatestKnownVersion + LastVersionCheckUtc
                        ' to match so the version label flips from
                        ' "update available, checked Nh ago" to
                        ' "up to date, just now" without waiting on
                        ' a separate Check for Updates click. The
                        ' next scheduled VersionCheckService poll
                        ' will pick up any new upstream version
                        ' published after this point.
                        '
                        ' Skipped for the synthetic-placeholder path
                        ' since that string can't compare cleanly
                        ' against upstream values — mirroring it to
                        ' LatestKnownVersion would just produce a
                        ' false "up to date" until the next poll
                        ' overwrote it.
                        If hasRealStamp Then
                            installEntity.LatestKnownVersion = stampedVersion
                            installEntity.LastVersionCheckUtc = DateTime.UtcNow
                        End If

                        installEntity.UpdatedUtc = DateTime.UtcNow
                        db.SaveChanges()

                        _logger.LogInformation("{Op} completed for {Id}",
                                               If(isUpdate, "Update", "Install"), installationId)

                        ' Fire UpdateCompleted — consumers don't care
                        ' whether it was a fresh install or an update,
                        ' both boil down to "installation is now ready".
                        If _emitter IsNot Nothing Then _emitter.UpdateCompleted(installationId, Nothing)

                        ' No fire-and-forget version check here — we
                        ' already stamped InstalledVersion correctly
                        ' from progress.InstalledBuildId (or the
                        ' plugin's IVersionAwarePlugin) and synced
                        ' LatestKnownVersion to match. A redundant
                        ' app_info_print round trip would just spawn
                        ' SteamCMD on the node for ~10-20s to
                        ' confirm what we already know.

                        result = InstallationOperationResult.Ok()
                    Else
                        _logger.LogError("{Op} failed for {Id}: {Err}",
                                         If(isUpdate, "Update", "Install"), installationId,
                                         progress.ErrorMessage)
                        If _emitter IsNot Nothing Then _emitter.UpdateFailed(installationId, progress.ErrorMessage)
                        ' Surface the node-side error message back to the
                        ' caller. Without this, the UI just sees a Boolean
                        ' false and falls back to a generic "check the
                        ' logs" message — losing the carefully-crafted
                        ' diagnostics the node prepared (e.g. SteamCMD's
                        ' distro-tailored "32-bit runtime libraries are
                        ' missing" hint).
                        result = InstallationOperationResult.Fail(
                            If(progress.ErrorMessage,
                               $"{If(isUpdate, "Update", "Install")} failed (no error message returned by node)."))
                    End If

                Catch ex As Exception
                    _logger.LogError(ex, "{Op} exception for {Id}",
                                     If(isUpdate, "Update", "Install"), installationId)
                    If _emitter IsNot Nothing Then _emitter.UpdateFailed(installationId, ex.Message)
                    result = InstallationOperationResult.Fail(ex.Message)

                Finally
                    ' Pair every OperationStarted with an
                    ' OperationCompleted for UI subscribers, and
                    ' clear the active-ops entry so GetActiveProgress
                    ' returns Nothing post-completion. Guarded by
                    ' `started` so a failure inside StartInstallAsync
                    ' (before we put anything into _activeOps and
                    ' before OperationStarted fired) doesn't fire a
                    ' phantom OperationCompleted with no matching
                    ' OperationStarted.
                    If started Then
                        Dim _tmp As InstallProgressResponse = Nothing
                        _activeOps.TryRemove(installationId, _tmp)

                        Dim success = (result IsNot Nothing AndAlso result.Success)
                        Dim errMsg As String = Nothing
                        If Not success Then
                            If result IsNot Nothing Then errMsg = result.ErrorMessage
                            If String.IsNullOrEmpty(errMsg) Then errMsg = "Operation failed"
                        End If
                        RaiseOperationCompleted(installationId, isUpdate, success, errMsg)
                    End If
                End Try

                Return result
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
        '  Live operation lookup (for panels opened mid-flight)
        ' ============================================================

        ''' <summary>
        ''' Returns the latest progress snapshot for an in-flight
        ''' install or update, or Nothing if no operation is active.
        ''' Intended for UI panels that open while an operation is
        ''' already running — they call this once on construction to
        ''' render the current state, then subscribe to ProgressChanged
        ''' for incremental updates.
        '''
        ''' Distinct from GetProgress on the node: this is the
        ''' last-polled snapshot the manager already holds, so it's
        ''' free to call (no HTTP round trip) and safe to call from
        ''' the UI thread.
        ''' </summary>
        Public Function GetActiveProgress(installationId As String) As InstallProgressResponse
            Dim p As InstallProgressResponse = Nothing
            _activeOps.TryGetValue(installationId, p)
            Return p
        End Function

        ''' <summary>
        ''' Returns whether an install or update is currently in the
        ''' polling phase (between StartInstallAsync returning and
        ''' the operation reaching a terminal state). Distinct from
        ''' IsLocked, which also stays True during the pre-start
        ''' lock acquisition and instance-stop steps that precede
        ''' the actual install activity — IsActive is narrower and
        ''' matches the window during which OperationStarted has
        ''' fired but OperationCompleted hasn't yet.
        ''' </summary>
        Public Function IsActive(installationId As String) As Boolean
            Return _activeOps.ContainsKey(installationId)
        End Function

        ' ============================================================
        '  Event raising helpers
        ' ============================================================

        ' Each helper wraps RaiseEvent in a try/catch so a single
        ' subscriber that throws can't take down the polling loop
        ' or block subsequent subscribers in the invocation list.
        ' Logged at Warning level so the issue surfaces without
        ' looking like a fatal error — the operation itself isn't
        ' affected, just one consumer's reaction to it.

        Private Sub RaiseOperationStarted(installationId As String,
                                            isUpdate As Boolean,
                                            userInitiated As Boolean)
            Try
                RaiseEvent OperationStarted(Me, New InstallationOperationStartedEventArgs With {
                    .InstallationId = installationId,
                    .IsUpdate = isUpdate,
                    .UserInitiated = userInitiated
                })
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "OperationStarted handler threw for {Id}", installationId)
            End Try
        End Sub

        Private Sub RaiseProgressChanged(installationId As String,
                                           progress As InstallProgressResponse)
            Try
                RaiseEvent ProgressChanged(Me, New InstallationProgressEventArgs With {
                    .InstallationId = installationId,
                    .Progress = progress
                })
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "ProgressChanged handler threw for {Id}", installationId)
            End Try
        End Sub

        Private Sub RaiseOperationCompleted(installationId As String,
                                              isUpdate As Boolean,
                                              success As Boolean,
                                              errorMessage As String)
            Try
                RaiseEvent OperationCompleted(Me, New InstallationOperationCompletedEventArgs With {
                    .InstallationId = installationId,
                    .IsUpdate = isUpdate,
                    .Success = success,
                    .ErrorMessage = errorMessage
                })
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "OperationCompleted handler threw for {Id}", installationId)
            End Try
        End Sub

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

                ' Resolve client + platform up front so the InstallationConfig
                ' we hand the plugin carries the right Platform value
                ' — plugins keying their update steps off platform
                ' (e.g. UE4 on Linux needing different SteamCMD args)
                ' need the answer before GetUpdateSteps runs.
                Dim client = _clientFactory.GetClient(
                    nodeEntity.NodeId, nodeEntity.HostAddress,
                    nodeEntity.Port, nodeEntity.AuthToken)
                Dim nodePlatform = Await NodePlatformResolver.ResolveAsync(client, cancellation)

                ' Build minimal InstallationConfig for plugin
                Dim installConfig As New InstallationConfig With {
                    .InstallationId = installationId,
                    .GameId = installEntity.GameId,
                    .DisplayName = installEntity.DisplayName,
                    .InstallPath = installEntity.InstallPath,
                    .NodeId = installEntity.NodeId,
                    .CustomFields = DeserializeConfig(installEntity.ConfigJson),
                    .Platform = nodePlatform
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
                    ' Client already resolved above the schema-build
                    ' so we could pre-fetch the platform.
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

    ' ============================================================
    '  Event arguments for InstallationManager UI events
    ' ============================================================

    ''' <summary>
    ''' Fired when an install or update reaches the polling phase
    ''' (after StartInstallAsync returns, before the first poll).
    ''' Carries the discriminator (isUpdate) so subscribers that only
    ''' care about one direction can filter, and userInitiated so the
    ''' panel knows whether to auto-select its Progress tab.
    ''' </summary>
    Public Class InstallationOperationStartedEventArgs
        Inherits EventArgs
        Public Property InstallationId As String
        Public Property IsUpdate As Boolean

        ''' <summary>
        ''' True when the operation was kicked off by an explicit
        ''' user action (right-click → Update, New Installation form,
        ''' etc.) rather than by an automation rule firing in the
        ''' background. The InstallationPanel uses this to decide
        ''' whether to switch to its Progress tab on receipt —
        ''' user-initiated operations get focus, automation-initiated
        ''' ones run quietly.
        ''' </summary>
        Public Property UserInitiated As Boolean
    End Class

    ''' <summary>
    ''' Fired on every poll tick during the operation. Carries the
    ''' full progress snapshot so subscribers can update bytes /
    ''' percent / phase / message without a separate fetch.
    ''' </summary>
    Public Class InstallationProgressEventArgs
        Inherits EventArgs
        Public Property InstallationId As String
        Public Property Progress As InstallProgressResponse
    End Class

    ''' <summary>
    ''' Fired exactly once per OperationStarted, in the Finally block
    ''' of ExecuteInstallInternal. Success is true only when the
    ''' operation reached the Completed state cleanly; cancellation,
    ''' failure, and exceptions all surface as Success=False with the
    ''' best-available error message.
    ''' </summary>
    Public Class InstallationOperationCompletedEventArgs
        Inherits EventArgs
        Public Property InstallationId As String
        Public Property IsUpdate As Boolean
        Public Property Success As Boolean
        Public Property ErrorMessage As String
    End Class

    ''' <summary>
    ''' Result of an install or update operation. Carries success +
    ''' the failure reason so callers can surface meaningful messages
    ''' to the user instead of the generic "check the logs" fallback
    ''' that a Boolean return would force.
    '''
    ''' Success path is constructed via Ok(); failure via Fail(msg).
    ''' The Boolean operator overload preserves the historical
    ''' `If result Then` convenience for callers that don't need the
    ''' message (the IRuleContext.UpdateInstallation contract on the
    ''' automation side, in particular).
    ''' </summary>
    Public Class InstallationOperationResult
        Public Property Success As Boolean
        Public Property ErrorMessage As String

        Public Shared Function Ok() As InstallationOperationResult
            Return New InstallationOperationResult With {.Success = True}
        End Function

        Public Shared Function Fail(message As String) As InstallationOperationResult
            Return New InstallationOperationResult With {
                .Success = False,
                .ErrorMessage = message
            }
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