Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Manager.Data

' ============================================================
'  VersionCheckService — periodic upstream version polling
'
'  Phase 5 (full): polls each installation on a fixed interval
'  to detect when the upstream version of its game has moved
'  past the installed version. When a mismatch is newly
'  detected, raises a VersionMismatch event via AutomationEngine
'  so user-authored rules with VersionMismatchTrigger fire.
'
'  Two paths per installation, picked by InstallMethod (mutually
'  exclusive — never fall back from one to the other):
'
'    Steam path (when InstallMethod=SteamCmd):
'      Calls InstallationManager.CheckForUpdatesAsync, which
'      already does an end-to-end Steam ACF + app_info_print
'      comparison via the node. The result tells us "installed
'      buildid X, latest buildid Y, update available T/F".
'      Authoritative for Steam installs — we never substitute a
'      different source on transient failure.
'
'    Plugin path (when NOT Steam-installed):
'      If the plugin implements IVersionAwarePlugin, calls
'      GetLatestVersionAsync and compares against InstalledVersion.
'      Used by Factorio (factorio.com API) and any future plugin
'      that fetches versions from a non-Steam source.
'
'  Why the paths must be mutually exclusive even when both are
'  technically available (Factorio installed via SteamCmd is the
'  motivating case): the version-string formats don't match. The
'  Steam path produces "steam:appid@branch build NNNNNNNN"; the
'  plugin path produces a game-native version like "2.0.76". If
'  Steam path errors transiently and the code falls through to
'  plugin path on a Steam install, the format mismatch makes
'  isOutOfDate compute true (steam stamp ≠ "2.0.76") AND
'  isNewlyDetected compute true ("2.0.76" ≠ previously-stored
'  steam stamp), firing a spurious VersionMismatch event for a
'  build that hasn't actually changed. With auto-update rules
'  configured, this triggers a redundant reinstall. Observed in
'  the wild on a node with spotty internet — same buildid
'  reinstalled twice within ~90 minutes on alternating Steam-
'  path failure/success cycles. Fix: Steam-installed games use
'  the Steam path exclusively; transient Steam failures skip the
'  cycle and retry on the next interval.
'
'  Installations whose plugin is neither SteamCmd-based nor
'  IVersionAwarePlugin are silently skipped — there's nothing
'  to check.
'
'  Throttling logic:
'    - Poll interval is 60 minutes (Phase 5 default; settable
'      later via AppSetting if needed)
'    - LatestKnownVersion records what was seen on the previous
'      successful poll. The mismatch event fires only when a
'      newly-fetched value differs from BOTH the InstalledVersion
'      AND the previously-recorded LatestKnownVersion. This means:
'        - First detection of a new upstream build → fires
'        - Subsequent polls finding the same upstream build →
'          updates timestamp but does NOT refire (user already
'          knows; they haven't updated yet)
'        - Upstream advances again → fires again (new mismatch)
'    - Restart-tolerance: LastVersionCheckUtc lets the service
'      skip installations that were checked within the last poll
'      interval. Means a Manager restart doesn't trigger an
'      immediate fresh poll of every installation — useful when
'      developing/iterating with frequent restarts.
'    - Failure-friendly: LastVersionCheckUtc is updated only on
'      successful checks (where we got a usable latestVersion).
'      A transient failure leaves it untouched, so the next pass
'      retries promptly rather than waiting out the throttle
'      window.
'
'  KNOWN ISSUE — transient bad readings: on a spotty network,
'  SteamCMD's app_info_print can return a stale or inconsistent
'  buildid that still parses cleanly. With the current
'  first-detect-fires logic, that single bad reading can promote
'  to a real VersionMismatch event — observed in the wild as two
'  redundant 7GB reinstalls within 90 minutes for the same
'  buildid (4/29/2026 on a wifi-connected node with severe
'  network instability during the relevant window). Fix is
'  scoped but deferred pending diagnostic data: the next
'  occurrence with file-logging in place will let us confirm
'  the failure mode before locking in a confirmation-across-
'  cycles fix that adds 60 min of detection latency. See the
'  reverted commit / Phase4c_Plan or work journal for context.
'
'  This service runs as a long-lived background task started
'  from ManagerProgram, mirroring ChatRetentionPruner's lifecycle.
' ============================================================

Namespace GSM.Manager.Core

    Public Class VersionCheckService

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _pluginRegistry As PluginRegistry
        Private ReadOnly _installationManager As InstallationManager
        Private ReadOnly _automationEngine As AutomationEngine
        Private ReadOnly _clientFactory As NodeHttpClientFactory
        Private ReadOnly _logger As ILogger(Of VersionCheckService)

        Private _cts As CancellationTokenSource
        Private _task As Task

        ' Poll interval. 60 minutes is a balance: short enough that
        ' a typical user catches updates the same workday they ship,
        ' long enough that SteamCMD invocations don't pile up (each
        ' Steam check spawns a SteamCMD process on the node and runs
        ' for 5–10 seconds against valve's CDN). Adjustable later
        ' via AppSetting if needed.
        Private Const PollIntervalMs As Integer = 60 * 60 * 1000

        ' Skip installations checked within this window. Keeps
        ' Manager-restart cycles from refreshing every installation
        ' immediately on each restart — dev iteration would otherwise
        ' burn through Steam quota on every F5.
        Private Shared ReadOnly RestartGracePeriod As TimeSpan = TimeSpan.FromMinutes(55)

        ' Startup delay before the first pass. Lets other DI services
        ' finish their own startup work first. Same value as the
        ' ChatRetentionPruner uses, for consistency.
        Private Const StartupDelayMs As Integer = 30 * 1000

        Public Sub New(serviceProvider As IServiceProvider,
                       pluginRegistry As PluginRegistry,
                       installationManager As InstallationManager,
                       automationEngine As AutomationEngine,
                       clientFactory As NodeHttpClientFactory,
                       logger As ILogger(Of VersionCheckService))
            _serviceProvider = serviceProvider
            _pluginRegistry = pluginRegistry
            _installationManager = installationManager
            _automationEngine = automationEngine
            _clientFactory = clientFactory
            _logger = logger
        End Sub

        ''' <summary>
        ''' Starts the version-check background loop. Idempotent.
        ''' </summary>
        Public Sub Start()
            If _cts IsNot Nothing Then Return
            _cts = New CancellationTokenSource()
            Dim token = _cts.Token
            _task = Task.Run(Function() RunAsync(token))
            _logger.LogInformation(
                "VersionCheckService started (poll interval {Interval}ms)", PollIntervalMs)
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

        ''' <summary>
        ''' Manual one-shot version check for a specific installation.
        ''' Bypasses the throttling window. Returns true on success,
        ''' false on any error. Called from the InstallationPanel
        ''' "Check Now" button (and by the polling loop itself for
        ''' each installation).
        ''' </summary>
        Public Async Function CheckInstallationAsync(installationId As String,
                                                      respectThrottle As Boolean,
                                                      cancellation As CancellationToken) As Task(Of VersionCheckResult)
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim install = db.Installations.Find(installationId)
                    If install Is Nothing Then
                        _logger.LogWarning("VersionCheck: installation {Id} not found", installationId)
                        Return VersionCheckResult.Fail("Installation not found in the database.")
                    End If

                    ' Throttle check (manual "Check Now" passes
                    ' respectThrottle=False to bypass).
                    If respectThrottle AndAlso install.LastVersionCheckUtc.HasValue Then
                        Dim sinceLast = DateTime.UtcNow - install.LastVersionCheckUtc.Value
                        If sinceLast < RestartGracePeriod Then
                            _logger.LogDebug(
                                "VersionCheck: skipping {Id} — checked {Mins} minutes ago",
                                installationId, CInt(sinceLast.TotalMinutes))
                            Return VersionCheckResult.Ok()
                        End If
                    End If

                    Dim plugin = _pluginRegistry.GetPlugin(install.GameId)
                    If plugin Is Nothing Then
                        _logger.LogDebug(
                            "VersionCheck: no plugin for game {Game}, skipping {Id}",
                            install.GameId, installationId)
                        Return VersionCheckResult.Fail(
                            $"Plugin '{install.GameId}' is not loaded.")
                    End If

                    ' Fetch the latest upstream version. Steam-
                    ' installed games use the Steam path exclusively
                    ' — see the file-header comment for why we don't
                    ' fall back to IVersionAwarePlugin on transient
                    ' Steam failures.
                    Dim latestVersion As String = Nothing
                    Dim usedSteamPath = False
                    Dim steamUpdateAvailable As Boolean = False

                    Dim isSteamInstall = String.Equals(install.InstallMethod,
                                                        InstallMethod.SteamCmd.ToString(),
                                                        StringComparison.OrdinalIgnoreCase)

                    If isSteamInstall Then
                        Try
                            Dim result = Await _installationManager.CheckForUpdatesAsync(
                                installationId, cancellation)
                            If Not String.IsNullOrEmpty(result.ErrorMessage) Then
                                ' Steam path errored on a Steam install.
                                ' Skip this cycle entirely — don't fall
                                ' through to plugin path (format mismatch
                                ' would cause spurious VersionMismatch),
                                ' don't update LastVersionCheckUtc (so
                                ' the next pass retries promptly rather
                                ' than waiting out the throttle window).
                                _logger.LogDebug(
                                    "VersionCheck: Steam path failed for {Id}: {Err} — skipping cycle, will retry next interval",
                                    installationId, result.ErrorMessage)
                                Return VersionCheckResult.Fail(
                                    If(String.IsNullOrEmpty(result.ErrorMessage),
                                        "Steam version check failed (no error message reported).",
                                        result.ErrorMessage))
                            End If

                            usedSteamPath = True
                            ' Authoritative answer for the firing
                            ' decision — InstallationManager has
                            ' already done apples-to-apples
                            ' comparison via the node's ACF manifest
                            ' read, so we trust its verdict instead
                            ' of doing string comparison on the
                            ' formatted stamps (which previously
                            ' broke because raw buildid "22526048"
                            ' never equals stamp "steam:920720@public
                            ' build 22526048").
                            steamUpdateAvailable = result.UpdateAvailable

                            ' CheckForUpdatesAsync runs in its own
                            ' DbContext scope and may have just
                            ' updated InstalledVersion to the
                            ' canonical "steam:{appId}@{branch}
                            ' build {buildid}" stamp. Reload our
                            ' local entity view so we see those
                            ' changes — we're about to splice the
                            ' latest buildid into the same prefix
                            ' for the LatestKnownVersion stamp.
                            Try
                                db.Entry(install).Reload()
                            Catch
                                ' Reload failure is non-fatal —
                                ' splice falls through below.
                            End Try

                            ' Format LatestKnownVersion to match
                            ' the stamp format InstalledVersion
                            ' uses, so the UI's string-equality
                            ' check correctly says "up to date"
                            ' when buildids match. Splice the
                            ' latest buildid into the same prefix.
                            If Not String.IsNullOrEmpty(install.InstalledVersion) Then
                                Dim buildIdx = install.InstalledVersion.LastIndexOf(" build ")
                                If buildIdx > 0 Then
                                    latestVersion = install.InstalledVersion.Substring(0, buildIdx) &
                                                    " build " & result.LatestBuildId
                                Else
                                    latestVersion = result.LatestBuildId
                                End If
                            Else
                                latestVersion = result.LatestBuildId
                            End If
                        Catch ex As Exception
                            ' Same reasoning as the ErrorMessage
                            ' branch above: don't fall through, don't
                            ' update timestamp, just skip this cycle.
                            _logger.LogWarning(ex,
                                "VersionCheck: Steam path threw for {Id} — skipping cycle, will retry next interval",
                                installationId)
                            Return VersionCheckResult.Fail(ex.Message)
                        End Try
                    Else
                        ' Non-Steam install — use IVersionAwarePlugin
                        ' if the plugin implements it. Plugins that
                        ' don't implement it leave nothing to check
                        ' for non-Steam installs, which is a benign
                        ' no-op.
                        Dim versionAware = TryCast(plugin, IVersionAwarePlugin)
                        If versionAware Is Nothing Then
                            _logger.LogDebug(
                                "VersionCheck: {Id} is non-Steam and plugin doesn't implement IVersionAwarePlugin — no version-check path",
                                installationId)
                            Return VersionCheckResult.Fail(
                                $"Plugin '{install.GameId}' does not support version checking for non-Steam installs (no IVersionAwarePlugin implementation).")
                        End If
                        Try
                            Dim installConfig As New InstallationConfig With {
                                .InstallationId = install.InstallationId,
                                .GameId = install.GameId,
                                .DisplayName = install.DisplayName,
                                .InstallPath = install.InstallPath,
                                .NodeId = install.NodeId,
                                .CustomFields = ParseConfigJson(install.ConfigJson)
                            }
                            installConfig.InstallMethod = ParseInstallMethodSafe(install.InstallMethod)
                            latestVersion = Await versionAware.GetLatestVersionAsync(
                                installConfig, cancellation)

                            ' Opportunistically refresh InstalledVersion
                            ' from disk via the plugin's reader. Catches
                            ' two situations the install-time stamp can't:
                            '   - Legacy rows pre-dating IVersionAwarePlugin.
                            '     GetInstalledVersionAsync still carry the
                            '     synthetic "download (timestamp)" stamp
                            '     and would never compare equal to a real
                            '     upstream version like "2.0.76". The
                            '     first poll after this code ships
                            '     upgrades them in place.
                            '   - Drift after manual file changes (someone
                            '     ssh'd in and replaced the binaries
                            '     out-of-band). The next poll picks up
                            '     the new on-disk version automatically.
                            ' All best-effort — a failure here doesn't
                            ' bubble up; we still have a usable latestVersion
                            ' for the mismatch decision below.
                            Try
                                Dim nodeEntity = db.Nodes.Find(install.NodeId)
                                If nodeEntity IsNot Nothing Then
                                    Dim nodeClient = _clientFactory.GetClient(
                                        nodeEntity.NodeId, nodeEntity.HostAddress,
                                        nodeEntity.Port, nodeEntity.AuthToken)
                                    Dim freshInstalled = Await versionAware.GetInstalledVersionAsync(
                                        installConfig, nodeClient, cancellation)
                                    If Not String.IsNullOrEmpty(freshInstalled) AndAlso
                                       Not String.Equals(freshInstalled,
                                                          install.InstalledVersion,
                                                          StringComparison.Ordinal) Then
                                        _logger.LogInformation(
                                            "VersionCheck: refreshed InstalledVersion for {Id} from {Old} to {New}",
                                            installationId,
                                            If(install.InstalledVersion, "(none)"),
                                            freshInstalled)
                                        install.InstalledVersion = freshInstalled
                                    End If
                                End If
                            Catch ex As Exception
                                _logger.LogDebug(ex,
                                    "VersionCheck: InstalledVersion refresh failed for {Id} (continuing with existing stamp)",
                                    installationId)
                            End Try
                        Catch ex As Exception
                            _logger.LogWarning(ex,
                                "VersionCheck: plugin path threw for {Id}", installationId)
                            Return VersionCheckResult.Fail(ex.Message)
                        End Try
                    End If

                    ' If the upstream check returned nothing (transient
                    ' failure, plugin returned null) we don't update
                    ' anything — next poll will retry.
                    If String.IsNullOrEmpty(latestVersion) Then
                        _logger.LogDebug(
                            "VersionCheck: {Id} returned no version (transient failure?)",
                            installationId)
                        Return VersionCheckResult.Fail(
                            "Upstream returned no version (transient network failure or unsupported plugin response).")
                    End If

                    ' Decision time: do we fire a mismatch event?
                    '
                    ' Fire only when:
                    '   - The freshly-fetched latest != installed (so
                    '     we're genuinely out of date), AND
                    '   - The freshly-fetched latest != previously-known
                    '     latest (so we haven't already raised for
                    '     this same upstream version)
                    '
                    ' This produces exactly one event per detected
                    ' upstream advance, no spam if the user takes
                    ' time to act on the notification.
                    Dim previouslyKnown = install.LatestKnownVersion
                    ' Steam path uses the authoritative UpdateAvailable
                    ' flag from the node's ACF read; plugin path falls
                    ' back to string comparison. Plugin authors are
                    ' responsible for returning a string that matches
                    ' the format InstalledVersion uses for their game
                    ' — otherwise this comparison spuriously reports
                    ' out-of-date forever.
                    Dim isOutOfDate As Boolean
                    If usedSteamPath Then
                        isOutOfDate = steamUpdateAvailable
                    Else
                        isOutOfDate = Not String.Equals(
                            latestVersion, install.InstalledVersion,
                            StringComparison.Ordinal)
                    End If
                    Dim isNewlyDetected = Not String.Equals(
                        latestVersion, previouslyKnown, StringComparison.Ordinal)

                    install.LatestKnownVersion = latestVersion
                    install.LastVersionCheckUtc = DateTime.UtcNow
                    db.SaveChanges()

                    If isOutOfDate AndAlso isNewlyDetected Then
                        _logger.LogInformation(
                            "VersionCheck: {Id} mismatch detected (installed: {Inst}, latest: {Latest}, source: {Path})",
                            installationId,
                            If(install.InstalledVersion, "(none)"),
                            latestVersion,
                            If(usedSteamPath, "Steam", "Plugin"))
                        Try
                            Await _automationEngine.RaiseVersionMismatchAsync(installationId)
                        Catch ex As Exception
                            _logger.LogWarning(ex,
                                "VersionCheck: failed to raise mismatch for {Id}", installationId)
                        End Try
                    Else
                        ' Promoted from Debug to Information so each
                        ' poll's result reaches the file logger — the
                        ' file logger's default min-level is Information,
                        ' and visibility into the actual values being
                        ' fetched is the whole reason the file logger
                        ' was wired up. Volume cost is one line per
                        ' installation per poll interval (60 min
                        ' default), so a node tracking even a dozen
                        ' installations writes maybe ~300 of these
                        ' lines per day. Trivial.
                        _logger.LogInformation(
                            "VersionCheck: {Id} latest={Latest} (out-of-date={OOD}, new={New})",
                            installationId, latestVersion, isOutOfDate, isNewlyDetected)
                    End If

                    Return VersionCheckResult.Ok()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "VersionCheck for {Id} threw", installationId)
                Return VersionCheckResult.Fail(ex.Message)
            End Try
        End Function

        ' ============================================================
        '  Background loop
        ' ============================================================

        Private Async Function RunAsync(token As CancellationToken) As Task
            Try
                Await Task.Delay(StartupDelayMs, token)
            Catch
                Return
            End Try

            While Not token.IsCancellationRequested
                Try
                    Await RunOnePassAsync(token)
                Catch ex As Exception
                    _logger.LogWarning(ex, "VersionCheck pass threw")
                End Try

                Try
                    Await Task.Delay(PollIntervalMs, token)
                Catch
                    Return
                End Try
            End While
        End Function

        ''' <summary>
        ''' Runs one full poll pass over all installations. Each
        ''' installation is checked sequentially; running them in
        ''' parallel was considered but rejected because each
        ''' Steam-path check spawns a SteamCMD process on the node
        ''' and we don't want to fork-bomb a small node host.
        ''' </summary>
        Private Async Function RunOnePassAsync(token As CancellationToken) As Task
            Dim installationIds As List(Of String)
            Using scope = _serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                ' Skip installations whose node is detached. Same
                ' rationale as InstanceManager.FetchAllInstanceIds:
                ' version polling shouldn't ping-pong a node the
                ' operator has opted out of background traffic
                ' for. Manual "Check for Updates" via the
                ' InstallationPanel goes through
                ' CheckInstallationAsync directly without this
                ' filter, so the operator can still force a check
                ' on a detached install if they really want one.
                installationIds = (From install In db.Installations
                                   Join nodeEnt In db.Nodes
                                       On install.NodeId Equals nodeEnt.NodeId
                                   Where nodeEnt.IsEnabled
                                   Select install.InstallationId).ToList()
            End Using

            For Each id In installationIds
                If token.IsCancellationRequested Then Exit For
                Try
                    Await CheckInstallationAsync(id, respectThrottle:=True, cancellation:=token)
                Catch
                    ' CheckInstallationAsync already logs; swallow
                    ' here so one bad installation doesn't stop
                    ' the rest of the pass.
                End Try
            Next

            _logger.LogDebug("VersionCheck pass complete: {Count} installation(s) processed",
                             installationIds.Count)
        End Function

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Shared Function ParseConfigJson(json As String) As Dictionary(Of String, String)
            If String.IsNullOrEmpty(json) Then Return New Dictionary(Of String, String)
            Try
                Return System.Text.Json.JsonSerializer.Deserialize(
                    Of Dictionary(Of String, String))(json)
            Catch
                Return New Dictionary(Of String, String)
            End Try
        End Function

        Private Shared Function ParseInstallMethodSafe(value As String) As InstallMethod
            If String.IsNullOrEmpty(value) Then Return InstallMethod.Manual
            Dim result As InstallMethod
            If [Enum].TryParse(value, True, result) Then Return result
            Return InstallMethod.Manual
        End Function

    End Class

    ' ============================================================
    '  Version-check result type
    ' ============================================================

    ''' <summary>
    ''' Result of a single CheckInstallationAsync pass. Replaces
    ''' the previous Task(Of Boolean) return so callers (most
    ''' importantly the manual "Check for Updates" button) can
    ''' surface the actual failure reason instead of falling back
    ''' to the "see log for details" placeholder the boolean
    ''' return forced.
    '''
    ''' Success = True covers both "fresh latest version fetched
    ''' and persisted" AND "deliberately skipped this cycle
    ''' (throttle window not yet elapsed)". The manual button
    ''' bypasses throttle so it only sees the former; the
    ''' background poller doesn't inspect the result either way.
    '''
    ''' Success = False carries ErrorMessage so the UI can render
    ''' it. Sources include: SteamCMD-level errors from the node
    ''' (multi-line distro-tailored library-missing hint, etc.),
    ''' plugin-side exceptions, and missing-installation /
    ''' missing-plugin lookups. ErrorMessage is never null on
    ''' failure but may be empty if no message was available.
    ''' </summary>
    Public Class VersionCheckResult
        Public Property Success As Boolean
        Public Property ErrorMessage As String

        Public Shared Function Ok() As VersionCheckResult
            Return New VersionCheckResult With {.Success = True}
        End Function

        Public Shared Function Fail(message As String) As VersionCheckResult
            Return New VersionCheckResult With {
                .Success = False,
                .ErrorMessage = If(message, "")
            }
        End Function
    End Class

End Namespace
