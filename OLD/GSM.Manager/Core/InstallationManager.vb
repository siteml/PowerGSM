Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports GSM.Data
Imports GSM.Node.Api
Imports GSM.Plugin

' ============================================================
'  InstallationManager
'
'  Coordinates everything that touches installation files:
'    - Fresh installs and updates via the node InstallRunner
'    - Installation lock management (reader/writer mutex)
'    - Multi-instance coordination for the Last Oasis pattern
'      (stop all → update files → start all)
'    - Version polling (is a newer version available?)
'
'  The lock model:
'    Each installation has a LockState in the manager DB:
'      None        - no operation in progress, instances can start
'      WriteLocked - update in progress, no instances can start
'
'    Running instances hold an implicit read lock. Before starting
'    an update, the manager checks that no instances are running.
'    The node enforces this as a safety net, but the manager is
'    the primary enforcer via StopAllInstancesAsync.
'
'    The lock is persisted to SQLite so it survives a manager
'    restart mid-update. On startup the manager checks for stale
'    write locks and surfaces them as warnings.
' ============================================================

Namespace GSM.Core

    Public Class InstallationManager

        Private ReadOnly _dbFactory As IDbContextFactory(Of GsmDbContext)
        Private ReadOnly _instanceManager As InstanceManager
        Private ReadOnly _nodeClientFactory As NodeHttpClientFactory
        Private ReadOnly _credentials As CredentialService
        Private ReadOnly _pluginRegistry As PluginRegistry
        Private ReadOnly _logger As ILogger(Of InstallationManager)

        Public Sub New(dbFactory As IDbContextFactory(Of GsmDbContext),
                       instanceManager As InstanceManager,
                       nodeClientFactory As NodeHttpClientFactory,
                       credentials As CredentialService,
                       pluginRegistry As PluginRegistry,
                       logger As ILogger(Of InstallationManager))
            _dbFactory = dbFactory
            _instanceManager = instanceManager
            _nodeClientFactory = nodeClientFactory
            _credentials = credentials
            _pluginRegistry = pluginRegistry
            _logger = logger
        End Sub


        ' ============================================================
        '  STARTUP - CHECK FOR STALE LOCKS
        ' ============================================================

        Public Async Function CheckStaleLocks(
                cancellation As CancellationToken) As Task(Of List(Of String))

            Dim warnings As New List(Of String)()
            Using db = _dbFactory.CreateDbContext()
                Dim locked = Await db.Installations.
                    Where(Function(i) i.LockState = "WriteLocked").
                    ToListAsync(cancellation)

                For Each install In locked
                    Dim msg = $"Installation '{install.DisplayName}' " &
                              $"(id: {install.InstallationId}) has a stale write lock " &
                              $"from before the manager was restarted. " &
                              $"It was locked at {install.WriteLockHeldSince}. " &
                              "Check the node's install status and release the lock manually."
                    warnings.Add(msg)
                    _logger.LogWarning("InstallationManager: {Warning}", msg)
                Next
            End Using
            Return warnings
        End Function


        ' ============================================================
        '  INSTALL + UPDATE
        ' ============================================================

        Public Async Function StartInstallAsync(
                installationId As String,
                cancellation As CancellationToken) As Task

            Using db = _dbFactory.CreateDbContext()
                Dim installation = Await LoadInstallation(db, installationId, cancellation)
                Dim plugin = RequirePlugin(installation.GameId)

                Dim installConfig = BuildInstallationConfig(installation)
                Dim steps = plugin.GetInstallSteps(
                    installation.InstallPath,
                    ParseInstallMethod(installation.InstallMethod),
                    installConfig)

                Dim steamUser = String.Empty
                Dim steamPass = String.Empty
                If installation.SteamCredential IsNot Nothing Then
                    steamUser = installation.SteamCredential.Username
                    steamPass = _credentials.DecryptSteamPassword(installation.SteamCredential)
                End If

                Dim request As New InstallRequest With {
                    .InstallationId = installationId,
                    .InstallPath = installation.InstallPath,
                    .Steps = steps.Select(AddressOf ToStepDto).ToList(),
                    .SteamUsername = steamUser,
                    .SteamPassword = steamPass
                }

                Dim client = Await _nodeClientFactory.GetClientAsync(
                    installation.NodeId, cancellation)
                Await client.StartInstallAsync(request, cancellation)

                _logger.LogInformation(
                    "InstallationManager: started install for '{Name}'",
                    installation.DisplayName)
            End Using
        End Function

        ' Full coordinated update workflow for an installation.
        ' Handles the multi-instance Last Oasis pattern:
        '   1. Acquire write lock (prevents new instance starts)
        '   2. Warn players via RCON
        '   3. Wait for all instances to empty (optional)
        '   4. Stop all instances
        '   5. Run update on the node
        '   6. Restart all instances
        '   7. Release write lock
        Public Async Function RunUpdateAsync(
                installationId As String,
                cancellation As CancellationToken) As Task

            _logger.LogInformation(
                "InstallationManager: starting coordinated update for '{Id}'",
                installationId)

            ' 1. Acquire write lock.
            Await AcquireWriteLockAsync(installationId,
                "Automated update", cancellation)

            ' VB.Net does not support Await in Finally blocks,
            ' so we capture any exception, release the lock, then re-throw.
            Dim caughtEx As Exception = Nothing
            Try
                ' 2. Stop all instances.
                Await StopAllInstancesAsync(installationId, graceful:=True, cancellation)

                ' 3. Run the update steps on the node.
                Using db = _dbFactory.CreateDbContext()
                    Dim installation = Await LoadInstallation(db, installationId, cancellation)
                    Dim plugin = RequirePlugin(installation.GameId)

                    Dim installConfig = BuildInstallationConfig(installation)
                    Dim steps = plugin.GetInstallSteps(
                        installation.InstallPath,
                        ParseInstallMethod(installation.InstallMethod),
                        installConfig)

                    Dim steamUser = String.Empty
                    Dim steamPass = String.Empty
                    If installation.SteamCredential IsNot Nothing Then
                        steamUser = installation.SteamCredential.Username
                        steamPass = _credentials.DecryptSteamPassword(installation.SteamCredential)
                    End If

                    Dim request As New UpdateRequest With {
                        .InstallationId = installationId,
                        .InstallPath = installation.InstallPath,
                        .Steps = steps.Select(AddressOf ToStepDto).ToList(),
                        .SteamUsername = steamUser,
                        .SteamPassword = steamPass
                    }

                    Dim client = Await _nodeClientFactory.GetClientAsync(
                        installation.NodeId, cancellation)
                    Await client.StartUpdateAsync(request, cancellation)

                    ' Poll until the update completes.
                    Await WaitForInstallCompletionAsync(
                        client, installationId, cancellation)

                    ' Refresh version in DB.
                    Dim status = Await client.GetInstallationStatusAsync(
                        installationId, cancellation)
                    installation.LastUpdatedAt = DateTime.UtcNow
                    Await db.SaveChangesAsync(cancellation)
                End Using

                ' 4. Restart all instances.
                Await StartAllInstancesAsync(installationId, cancellation)

                _logger.LogInformation(
                    "InstallationManager: update complete for '{Id}'", installationId)

            Catch ex As Exception
                caughtEx = ex
            End Try

            ' Always release the lock even if something went wrong.
            Await ReleaseWriteLockAsync(installationId, cancellation)

            If caughtEx IsNot Nothing Then
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(caughtEx).Throw()
            End If
        End Function

        ' Polls the node until the install operation finishes.
        Private Async Function WaitForInstallCompletionAsync(
                client As INodeClient,
                installationId As String,
                cancellation As CancellationToken) As Task

            Dim pollInterval = TimeSpan.FromSeconds(5)
            Dim timeout = DateTime.UtcNow.AddMinutes(120)

            Do
                If DateTime.UtcNow > timeout Then
                    Throw New TimeoutException(
                        "Install operation did not complete within 120 minutes.")
                End If

                Await Task.Delay(pollInterval, cancellation)

                Dim status = Await client.GetInstallationStatusAsync(
                    installationId, cancellation)

                Select Case status.State
                    Case InstallationOperationState.Succeeded
                        Return
                    Case InstallationOperationState.Failed
                        Throw New InvalidOperationException(
                            $"Install operation failed: {status.ErrorMessage}")
                    Case InstallationOperationState.Cancelled
                        Throw New OperationCanceledException("Install was cancelled.")
                End Select

                _logger.LogDebug(
                    "InstallationManager: update progress {N}/{Total}: {Desc}",
                    status.CurrentStepIndex, status.TotalSteps,
                    status.CurrentStepDescription)
            Loop
        End Function


        ' ============================================================
        '  INSTALLATION LOCK
        ' ============================================================

        Private Async Function AcquireWriteLockAsync(
                installationId As String,
                reason As String,
                cancellation As CancellationToken) As Task

            Using db = _dbFactory.CreateDbContext()
                Dim installation = Await db.Installations.FindAsync(
                    New Object() {installationId}, cancellation)
                If installation Is Nothing Then
                    Throw New InvalidOperationException(
                        $"Installation '{installationId}' not found.")
                End If

                If installation.LockState = "WriteLocked" Then
                    Throw New InvalidOperationException(
                        $"Installation '{installation.DisplayName}' is already write-locked. " &
                        $"Reason: {installation.WriteLockReason}")
                End If

                installation.LockState = "WriteLocked"
                installation.WriteLockHeldSince = DateTime.UtcNow
                installation.WriteLockReason = reason
                Await db.SaveChangesAsync(cancellation)

                _logger.LogInformation(
                    "InstallationManager: write lock acquired for '{Name}' ({Reason})",
                    installation.DisplayName, reason)
            End Using
        End Function

        Private Async Function ReleaseWriteLockAsync(
                installationId As String,
                cancellation As CancellationToken) As Task

            Using db = _dbFactory.CreateDbContext()
                Dim installation = Await db.Installations.FindAsync(
                    New Object() {installationId}, cancellation)
                If installation Is Nothing Then Return

                installation.LockState = "None"
                installation.WriteLockHeldSince = Nothing
                installation.WriteLockReason = ""
                Await db.SaveChangesAsync(cancellation)

                _logger.LogInformation(
                    "InstallationManager: write lock released for '{Name}'",
                    installation.DisplayName)
            End Using
        End Function

        Public Async Function ReleaseStaleLockAsync(
                installationId As String,
                cancellation As CancellationToken) As Task
            ' Called by the operator from the UI to clear a stale lock.
            Await ReleaseWriteLockAsync(installationId, cancellation)
        End Function

        Public Async Function IsLockedAsync(
                installationId As String,
                cancellation As CancellationToken) As Task(Of Boolean)
            Using db = _dbFactory.CreateDbContext()
                Return Await db.Installations.
                    Where(Function(i) i.InstallationId = installationId AndAlso
                                      i.LockState = "WriteLocked").
                    AnyAsync(cancellation)
            End Using
        End Function


        ' ============================================================
        '  MULTI-INSTANCE COORDINATION
        ' ============================================================

        Public Async Function StopAllInstancesAsync(
                installationId As String,
                graceful As Boolean,
                cancellation As CancellationToken) As Task

            Dim instances = Await GetInstancesForInstallationAsync(
                installationId, cancellation)

            _logger.LogInformation(
                "InstallationManager: stopping {Count} instance(s) for installation '{Id}'",
                instances.Count, installationId)

            Dim tasks = instances.Select(
                Async Function(inst)
                    Try
                        Await _instanceManager.StopInstanceAsync(
                            inst.InstanceId, graceful, cancellation)
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "InstallationManager: error stopping instance '{Name}'",
                            inst.DisplayName)
                    End Try
                End Function)

            Await Task.WhenAll(tasks)

            ' Wait for all instances to reach a stopped state.
            Dim deadline = DateTime.UtcNow.AddSeconds(
                If(graceful, 60, 10))

            Do While DateTime.UtcNow < deadline
                Dim allStopped = True
                For Each inst In instances
                    Try
                        Dim metrics = Await _instanceManager.GetMetricsAsync(
                            inst.InstanceId, cancellation)
                        If metrics.State = InstanceState.Running OrElse
                           metrics.State = InstanceState.Stopping Then
                            allStopped = False
                            Exit For
                        End If
                    Catch
                    End Try
                Next
                If allStopped Then Return
                Await Task.Delay(1000, cancellation)
            Loop

            _logger.LogWarning(
                "InstallationManager: not all instances stopped within timeout " &
                "for installation '{Id}'", installationId)
        End Function

        Public Async Function StartAllInstancesAsync(
                installationId As String,
                cancellation As CancellationToken) As Task

            Dim instances = Await GetInstancesForInstallationAsync(
                installationId, cancellation)

            _logger.LogInformation(
                "InstallationManager: starting {Count} instance(s) for installation '{Id}'",
                instances.Count, installationId)

            ' Start instances sequentially with a short delay between each.
            ' Starting them all simultaneously can overload the node on startup.
            For Each inst In instances.OrderBy(Function(i) i.SortOrder)
                Try
                    Await _instanceManager.StartInstanceAsync(
                        inst.InstanceId, cancellation)
                    Await Task.Delay(2000, cancellation)   ' Brief gap between starts
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "InstallationManager: error starting instance '{Name}'",
                        inst.DisplayName)
                End Try
            Next
        End Function

        Public Async Function SendRconToAllInstancesAsync(
                installationId As String,
                command As String,
                cancellation As CancellationToken) As Task

            Dim instances = Await GetInstancesForInstallationAsync(
                installationId, cancellation)

            Dim tasks = instances.Select(
                Async Function(inst)
                    Try
                        Await _instanceManager.SendRconCommandAsync(
                            inst.InstanceId, command, cancellation)
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "InstallationManager: RCON error for instance '{Name}'",
                            inst.DisplayName)
                    End Try
                End Function)

            Await Task.WhenAll(tasks)
        End Function

        Public Async Function GetTotalPlayerCountAsync(
                installationId As String,
                cancellation As CancellationToken) As Task(Of Integer)

            Dim instances = Await GetInstancesForInstallationAsync(
                installationId, cancellation)

            Dim total = 0
            For Each inst In instances
                Try
                    total += Await _instanceManager.GetPlayerCountAsync(
                        inst.InstanceId, cancellation)
                Catch
                End Try
            Next
            Return total
        End Function

        Private Async Function GetInstancesForInstallationAsync(
                installationId As String,
                cancellation As CancellationToken) As Task(Of List(Of InstanceEntity))

            Using db = _dbFactory.CreateDbContext()
                Return Await db.Instances.
                    Where(Function(i) i.InstallationId = installationId AndAlso
                                      i.IsEnabled).
                    OrderBy(Function(i) i.SortOrder).
                    ToListAsync(cancellation)
            End Using
        End Function


        ' ============================================================
        '  VERSION POLLING
        ' ============================================================

        ' Returns True if a newer version is available than what's installed.
        ' Returns False on any error (never trigger an update on failure).
        Public Async Function CheckForUpdateAsync(
                installationId As String,
                cancellation As CancellationToken) As Task(Of Boolean)

            Using db = _dbFactory.CreateDbContext()
                Dim installation = Await db.Installations.
                    Include(Function(i) i.Node).
                    FirstOrDefaultAsync(Function(i) i.InstallationId = installationId,
                                        cancellation)
                If installation Is Nothing Then Return False

                Dim plugin = _pluginRegistry.GetPlugin(installation.GameId)
                If plugin Is Nothing Then Return False

                Try
                    Dim installConfig = BuildInstallationConfig(installation)
                    Dim latestVersion = Await plugin.GetLatestVersion(
                        installConfig, cancellation)

                    If String.IsNullOrEmpty(latestVersion) Then Return False

                    ' Get the current version from the node.
                    Dim client = Await _nodeClientFactory.GetClientAsync(
                        installation.NodeId, cancellation)
                    Dim status = Await client.GetInstallationStatusAsync(
                        installationId, cancellation)

                    Dim currentVersion = installation.InstalledVersion
                    If String.IsNullOrEmpty(currentVersion) Then Return False

                    Dim hasUpdate = latestVersion <> currentVersion
                    If hasUpdate Then
                        _logger.LogInformation(
                            "InstallationManager: update available for '{Name}': " &
                            "{Current} → {Latest}",
                            installation.DisplayName, currentVersion, latestVersion)
                    End If
                    Return hasUpdate

                Catch ex As Exception
                    _logger.LogDebug(ex,
                        "InstallationManager: version check failed for '{Name}'",
                        installation.DisplayName)
                    Return False
                End Try
            End Using
        End Function


        ' ============================================================
        '  INSTALLATION CRUD
        ' ============================================================

        Public Async Function CreateInstallationAsync(
                entity As InstallationEntity,
                cancellation As CancellationToken) As Task(Of InstallationEntity)

            entity.InstallationId = Guid.NewGuid().ToString()
            entity.CreatedAt = DateTime.UtcNow
            entity.LockState = "None"

            Using db = _dbFactory.CreateDbContext()
                db.Installations.Add(entity)
                Await db.SaveChangesAsync(cancellation)
            End Using

            _logger.LogInformation(
                "InstallationManager: created installation '{Name}' for game '{Game}'",
                entity.DisplayName, entity.GameId)

            Return entity
        End Function

        Public Async Function GetInstallationAsync(
                installationId As String,
                cancellation As CancellationToken) As Task(Of InstallationEntity)
            Using db = _dbFactory.CreateDbContext()
                Return Await db.Installations.
                    Include(Function(i) i.Node).
                    Include(Function(i) i.RealmCredential).
                    Include(Function(i) i.SteamCredential).
                    Include(Function(i) i.Instances).
                    FirstOrDefaultAsync(Function(i) i.InstallationId = installationId,
                                        cancellation)
            End Using
        End Function

        Public Async Function GetAllInstallationsAsync(
                cancellation As CancellationToken) As Task(Of List(Of InstallationEntity))
            Using db = _dbFactory.CreateDbContext()
                Return Await db.Installations.
                    Include(Function(i) i.Node).
                    Include(Function(i) i.Instances).
                    OrderBy(Function(i) i.DisplayName).
                    ToListAsync(cancellation)
            End Using
        End Function


        ' ============================================================
        '  PRIVATE HELPERS
        ' ============================================================

        Private Async Function LoadInstallation(db As GsmDbContext,
                                                  installationId As String,
                                                  cancellation As CancellationToken) As Task(Of InstallationEntity)
            Dim installation = Await db.Installations.
                Include(Function(i) i.Node).
                Include(Function(i) i.RealmCredential).
                Include(Function(i) i.SteamCredential).
                FirstOrDefaultAsync(Function(i) i.InstallationId = installationId,
                                    cancellation)
            If installation Is Nothing Then
                Throw New InvalidOperationException(
                    $"Installation '{installationId}' not found.")
            End If
            Return installation
        End Function

        Private Function RequirePlugin(gameId As String) As IGamePlugin
            Dim plugin = _pluginRegistry.GetPlugin(gameId)
            If plugin Is Nothing Then
                Throw New InvalidOperationException(
                    $"No plugin loaded for game '{gameId}'. " &
                    "Load the plugin via Reload Plugins before managing this installation.")
            End If
            Return plugin
        End Function

        Private Shared Function BuildInstallationConfig(
                installation As InstallationEntity) As InstallationConfig
            Return New InstallationConfig With {
                .GameId = installation.GameId,
                .RawJson = installation.PluginConfig,
                .SteamBranch = ExtractJsonField(installation.PluginConfig, "SteamBranch"),
                .SteamBranchPassword = ExtractJsonField(installation.PluginConfig,
                                                         "SteamBranchPassword")
            }
        End Function

        Private Shared Function ExtractJsonField(json As String, key As String) As String
            If String.IsNullOrEmpty(json) Then Return String.Empty
            Try
                Dim doc = JsonDocument.Parse(json)
                Dim val As JsonElement
                If doc.RootElement.TryGetProperty(key, val) Then
                    Return If(val.GetString(), String.Empty)
                End If
            Catch
            End Try
            Return String.Empty
        End Function

        Private Shared Function ParseInstallMethod(s As String) As InstallMethod
            Select Case s
                Case "DirectDownload" : Return InstallMethod.DirectDownload
                Case "Manual"         : Return InstallMethod.Manual
                Case Else             : Return InstallMethod.SteamCMD
            End Select
        End Function

        ' Convert IGamePlugin InstallStep hierarchy to serialisable DTOs.
        Private Shared Function ToStepDto(installStep As InstallStep) As InstallStepDto
            If TypeOf installStep Is SteamCmdInstallStep Then
                Dim s = CType(installStep, SteamCmdInstallStep)
                Return New InstallStepDto With {
                    .StepType = InstallStepType.SteamCmd,
                    .Description = s.Description,
                    .AppId = s.AppId,
                    .InstallDir = s.InstallDir,
                    .Branch = s.Branch,
                    .BranchPassword = s.BranchPassword,
                    .ValidateFiles = s.ValidateFiles
                }
            ElseIf TypeOf installStep Is DownloadInstallStep Then
                Dim s = CType(installStep, DownloadInstallStep)
                Return New InstallStepDto With {
                    .StepType = InstallStepType.Download,
                    .Description = s.Description,
                    .Url = s.Url,
                    .Sha256 = s.Sha256,
                    .ExtractToPath = s.ExtractToPath
                }
            ElseIf TypeOf installStep Is RunCommandStep Then
                Dim s = CType(installStep, RunCommandStep)
                Return New InstallStepDto With {
                    .StepType = InstallStepType.RunCommand,
                    .Description = s.Description,
                    .Executable = s.Executable,
                    .Arguments = s.Arguments,
                    .WorkingDirectory = s.WorkingDirectory,
                    .ExpectExitCode = s.ExpectExitCode
                }
            End If
            Return New InstallStepDto With {
                .Description = installStep.Description,
                .StepType = InstallStepType.RunCommand
            }
        End Function

    End Class

End Namespace
