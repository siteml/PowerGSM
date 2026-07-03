Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Net.Http
Imports System.Reflection
Imports System.Security.Cryptography
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data

' ============================================================
'  UpdateOrchestrator — Phase 5l-2 (download + stage)
'
'  Pulls a release's Manager zip from GitHub, verifies it against
'  the release's SHA256SUMS, and extracts it into a per-version
'  staging folder under <AppDir>\.updates\. Nothing here ever
'  touches the running install — staging is fully reversible
'  (DiscardStaged wipes the folder). Applying the staged update
'  (the risky binary swap) is Phase 5l-3 and lives elsewhere.
'
'  Release source is the same `update.source` setting the checker
'  uses (default siteml/PowerGSM) — NOT a gsmsettings.json field;
'  5l-1 settled update config into the AppSettings key-value bag.
'
'  The checker's GitHubRelease DTO doesn't carry assets, so this
'  service fetches the release by tag (/releases/tags/{tag}) to
'  read asset download URLs.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>Download progress + coarse phase, reported to the UI.</summary>
    Public Structure StageProgress
        Public Property Phase As String          ' "Downloading" / "Verifying" / "Extracting"
        Public Property BytesReceived As Long
        Public Property TotalBytes As Long       ' -1 when unknown

        Public ReadOnly Property HasTotal As Boolean
            Get
                Return TotalBytes > 0
            End Get
        End Property

        Public ReadOnly Property Percent As Integer
            Get
                If TotalBytes <= 0 Then Return 0
                Return CInt(Math.Min(100L, (BytesReceived * 100L) \ TotalBytes))
            End Get
        End Property
    End Structure

    ''' <summary>Outcome of a stage attempt.</summary>
    Public Class StageResult
        Public Property Success As Boolean
        Public Property Canceled As Boolean
        Public Property Version As String
        Public Property ExtractedPath As String
        Public Property ErrorMessage As String
    End Class

    ''' <summary>What's currently staged on disk (if anything).</summary>
    Public Class StagedState
        Public Property HasStaged As Boolean
        Public Property Version As String
        Public Property ExtractedPath As String
    End Class

    Public Class UpdateOrchestrator

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger(Of UpdateOrchestrator)
        Private ReadOnly _http As HttpClient

        ' Phase 5l-3 — set by RequestApply, consumed by LaunchPendingApply
        ' (called from ManagerProgram after the window closes).
        Private _pendingApplyScript As String
        Private _pendingVersion As String

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of UpdateOrchestrator))
            _serviceProvider = serviceProvider
            _logger = logger

            ' Infinite timeout: a release zip can be tens of MB; we rely
            ' on the CancellationToken (the dialog's Cancel button) to
            ' abort, not on a wall-clock timeout that would kill a slow
            ' but healthy download.
            _http = New HttpClient() With {.Timeout = Timeout.InfiniteTimeSpan}
            Try
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("PowerGSM")
            Catch
            End Try
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json")
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
        End Sub

        ' ---- staging paths ----

        Private Shared Function UpdatesRoot() As String
            Return Path.Combine(InstallEnvironment.InstallDirectory(), ".updates")
        End Function

        Private Shared Function VersionDir(version As String) As String
            Return Path.Combine(UpdatesRoot(), version)
        End Function

        Private Shared Function DownloadDir(version As String) As String
            Return Path.Combine(VersionDir(version), "download")
        End Function

        Private Shared Function ExtractedDir(version As String) As String
            Return Path.Combine(VersionDir(version), "extracted")
        End Function

        ' ============================================================
        '  Stage: download → verify → extract
        ' ============================================================

        ''' <summary>
        ''' Download the Manager zip for <paramref name="status"/>'s
        ''' latest release, verify its SHA256 against the release's
        ''' SHA256SUMS, and extract it to the staging folder. Never
        ''' throws — outcome (incl. cancellation) comes back in the
        ''' result. Reports progress through <paramref name="progress"/>.
        ''' </summary>
        Public Async Function StageAsync(status As UpdateStatus,
                                         progress As IProgress(Of StageProgress),
                                         token As CancellationToken) As Task(Of StageResult)
            Dim result As New StageResult With {.Version = If(status?.LatestVersion, "")}

            If status Is Nothing OrElse String.IsNullOrEmpty(status.LatestVersion) OrElse String.IsNullOrEmpty(status.LatestTag) Then
                result.ErrorMessage = "No release is selected to download."
                Return result
            End If

            Dim version = status.LatestVersion
            Dim tag = status.LatestTag
            Dim source = ReadSource()
            Dim vdir = VersionDir(version)

            Try
                ' 1) Resolve the release's assets. The zip is named with
                ' the version minus any leading "v" (the pipeline derives
                ' it from the tag as ${GITHUB_REF#refs/tags/v}), but
                ' LatestVersion can carry the raw tag form ("v0.3.0"),
                ' so strip a leading "v" for the asset name.
                Dim assets = Await ReleaseAssetHelpers.FetchAssetsAsync(_http, source, tag, token)
                Dim assetVersion = version
                If assetVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase) Then assetVersion = assetVersion.Substring(1)
                Dim zipName = $"PowerGSM-Manager-{assetVersion}-win-x64.zip"
                Dim zipUrl = ReleaseAssetHelpers.FindAssetUrl(assets, zipName)
                Dim sumsUrl = ReleaseAssetHelpers.FindAssetUrl(assets, "SHA256SUMS")
                If String.IsNullOrEmpty(zipUrl) Then
                    result.ErrorMessage = $"Release {tag} has no asset named {zipName}."
                    Return result
                End If

                ' Fresh staging dir for this version.
                If Directory.Exists(vdir) Then Directory.Delete(vdir, True)
                Directory.CreateDirectory(DownloadDir(version))

                ' 2) Fetch + parse SHA256SUMS (small text asset).
                Dim expectedHash As String = Nothing
                If Not String.IsNullOrEmpty(sumsUrl) Then
                    Dim sumsText = Await _http.GetStringAsync(sumsUrl, token)
                    expectedHash = ReleaseAssetHelpers.ParseSumsFor(sumsText, zipName)
                End If

                ' 3) Download the zip with progress.
                Dim zipPath = Path.Combine(DownloadDir(version), zipName)
                Await ReleaseAssetHelpers.DownloadFileAsync(_http, zipUrl, zipPath, progress, token)

                ' 4) Verify integrity.
                progress?.Report(New StageProgress With {.Phase = "Verifying", .TotalBytes = -1})
                If Not String.IsNullOrEmpty(expectedHash) Then
                    Dim actual = ReleaseAssetHelpers.ComputeSha256(zipPath)
                    If Not String.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase) Then
                        TryDelete(vdir)
                        result.ErrorMessage =
                            "Checksum mismatch — the download may be corrupt or tampered with. " &
                            $"Expected {Short12(expectedHash)}, got {Short12(actual)}."
                        Return result
                    End If
                    _logger.LogInformation("UpdateOrchestrator: SHA256 verified for {Zip}", zipName)
                Else
                    _logger.LogWarning(
                        "UpdateOrchestrator: no SHA256SUMS entry for {Zip}; proceeding WITHOUT checksum verification", zipName)
                End If

                ' 5) Extract.
                token.ThrowIfCancellationRequested()
                progress?.Report(New StageProgress With {.Phase = "Extracting", .TotalBytes = -1})
                Dim edir = ExtractedDir(version)
                Directory.CreateDirectory(edir)
                ZipFile.ExtractToDirectory(zipPath, edir, overwriteFiles:=True)

                ' Sanity: the new Manager binary must be present.
                If Not File.Exists(Path.Combine(edir, "GSM.Manager.exe")) Then
                    TryDelete(vdir)
                    result.ErrorMessage = "The extracted update doesn't contain GSM.Manager.exe."
                    Return result
                End If

                PersistStaged(version)
                result.Success = True
                result.ExtractedPath = edir
                _logger.LogInformation("UpdateOrchestrator: staged {Version} at {Path}", version, edir)
                Return result

            Catch ex As OperationCanceledException
                result.Canceled = True
                TryDelete(vdir)
                _logger.LogInformation("UpdateOrchestrator: staging of {Version} canceled", version)
                Return result
            Catch ex As Exception
                _logger.LogWarning(ex, "UpdateOrchestrator: staging {Version} failed", version)
                result.ErrorMessage = ex.Message
                TryDelete(vdir)
                Return result
            End Try
        End Function

        ' ============================================================
        '  Staged-state query + discard
        ' ============================================================

        ''' <summary>
        ''' What's staged on disk right now. Confirms the recorded
        ''' version's extracted folder still actually contains the new
        ''' Manager exe (so a half-deleted folder reads as not-staged).
        ''' </summary>
        Public Function GetStagedState() As StagedState
            Dim st As New StagedState()
            Try
                Dim version = ""
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    version = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateStagedVersion, "")
                End Using
                If String.IsNullOrEmpty(version) Then Return st
                Dim edir = ExtractedDir(version)
                If File.Exists(Path.Combine(edir, "GSM.Manager.exe")) Then
                    st.HasStaged = True
                    st.Version = version
                    st.ExtractedPath = edir
                End If
            Catch ex As Exception
                _logger.LogDebug(ex, "UpdateOrchestrator: reading staged state failed")
            End Try
            Return st
        End Function

        ''' <summary>Wipe the staged folder and clear the recorded version.</summary>
        Public Sub DiscardStaged()
            Try
                Dim version = ""
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    version = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateStagedVersion, "")
                    db.SetSetting(GsmDataExtensions.SettingKeys.UpdateStagedVersion, "")
                    db.SaveChanges()
                End Using
                If Not String.IsNullOrEmpty(version) Then TryDelete(VersionDir(version))
                ' Tidy the root if it's now empty.
                Try
                    If Directory.Exists(UpdatesRoot()) AndAlso
                       Directory.GetFileSystemEntries(UpdatesRoot()).Length = 0 Then
                        Directory.Delete(UpdatesRoot())
                    End If
                Catch
                End Try
            Catch ex As Exception
                _logger.LogDebug(ex, "UpdateOrchestrator: discard failed")
            End Try
        End Sub

        ' ============================================================
        '  Internals
        ' ============================================================

        ' FetchAssetsAsync / FindAssetUrl / ParseSumsFor / ComputeSha256 /
        ' DownloadFileAsync moved to the shared ReleaseAssetHelpers module
        ' (ReleaseAssets.vb) in slice 7-source-b.

        Private Shared Function Short12(hash As String) As String
            If String.IsNullOrEmpty(hash) Then Return "?"
            Return If(hash.Length <= 12, hash, hash.Substring(0, 12) & "…")
        End Function

        Private Sub PersistStaged(version As String)
            Using scope = _serviceProvider.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                db.SetSetting(GsmDataExtensions.SettingKeys.UpdateStagedVersion, version)
                db.SaveChanges()
            End Using
        End Sub

        Private Function ReadSource() As String
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim s = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateSource, GsmDataExtensions.DefaultUpdateSource)
                    Return If(String.IsNullOrWhiteSpace(s), GsmDataExtensions.DefaultUpdateSource, s)
                End Using
            Catch
                Return GsmDataExtensions.DefaultUpdateSource
            End Try
        End Function

        Private Shared Sub TryDelete(dir As String)
            Try
                If Not String.IsNullOrEmpty(dir) AndAlso Directory.Exists(dir) Then Directory.Delete(dir, True)
            Catch
            End Try
        End Sub

        Private Shared Sub TryDeleteFile(path As String)
            Try
                If Not String.IsNullOrEmpty(path) AndAlso File.Exists(path) Then File.Delete(path)
            Catch
            End Try
        End Sub

        ' ============================================================
        '  Apply (Phase 5l-3) — swap the staged binaries in place
        '
        '  The Manager can't overwrite its own running .exe, so the
        '  swap is done by a tiny generated apply.cmd that: waits for
        '  this process to exit, backs up the current binaries to
        '  .updates\rollback\, copies the staged GSM.Manager.exe +
        '  GSM.Contracts.dll over the install, relaunches with
        '  --post-update, and deletes itself. The Manager spawns it
        '  detached and exits cleanly (code 0) so the 5m-3 watchdog
        '  stands down rather than relaunching the old binary mid-swap;
        '  apply.cmd owns the relaunch.
        '
        '  Only the two binaries are touched — never gsm.db, settings,
        '  Plugins\, Logs\, or the (possibly-running, possibly-locked)
        '  watchdog. The DB path is anchored to the binary folder, so
        '  the relaunch working directory can't point at the wrong DB.
        ' ============================================================

        ''' <summary>True once <see cref="RequestApply"/> has staged an apply.cmd.</summary>
        Public ReadOnly Property HasPendingApply As Boolean
            Get
                Return Not String.IsNullOrEmpty(_pendingApplyScript) AndAlso File.Exists(_pendingApplyScript)
            End Get
        End Property

        Public ReadOnly Property PendingApplyVersion As String
            Get
                Return _pendingVersion
            End Get
        End Property

        ''' <summary>
        ''' Compare a staged version against the running one. Returns
        ''' &lt;0 (staged older), 0 (same), &gt;0 (staged newer), or
        ''' Nothing when either can't be parsed (caller shouldn't block
        ''' on an unknown).
        ''' </summary>
        Public Function StagedVersusRunning(stagedVersion As String) As Integer?
            Dim staged = SemanticVersion.TryParse(stagedVersion)
            Dim running = SemanticVersion.TryParse(GetRunningVersionString())
            If staged Is Nothing OrElse running Is Nothing Then Return Nothing
            Return staged.CompareTo(running)
        End Function

        Private Shared Function GetRunningVersionString() As String
            Dim asm = Assembly.GetExecutingAssembly()
            Dim info = asm.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()
            If info IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(info.InformationalVersion) Then
                Return info.InformationalVersion
            End If
            Dim v = asm.GetName().Version
            Return If(v IsNot Nothing, v.ToString(), "0.0.0")
        End Function

        ''' <summary>Read the staged build's BUILD-INFO.json (Nothing if absent/unparseable).</summary>
        Public Function ReadBuildInfo(extractedPath As String) As BuildInfo
            Try
                If String.IsNullOrEmpty(extractedPath) Then Return Nothing
                Dim p = Path.Combine(extractedPath, "BUILD-INFO.json")
                If Not File.Exists(p) Then Return Nothing
                Dim json = File.ReadAllText(p)
                Dim opts As New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}
                Return JsonSerializer.Deserialize(Of BuildInfo)(json, opts)
            Catch ex As Exception
                _logger.LogDebug(ex, "UpdateOrchestrator: ReadBuildInfo failed")
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Pre-flight + generate the apply.cmd for the staged update.
        ''' Refuses a downgrade (Site's belt-and-suspenders guard) and
        ''' a staged folder missing its binaries. On success the script
        ''' path is held in <see cref="HasPendingApply"/>; ManagerProgram
        ''' launches it on exit.
        ''' </summary>
        Public Function RequestApply(staged As StagedState) As ApplyPrepareResult
            Dim r As New ApplyPrepareResult()
            If staged Is Nothing OrElse Not staged.HasStaged OrElse String.IsNullOrEmpty(staged.Version) Then
                r.ErrorMessage = "Nothing is staged to apply."
                Return r
            End If

            Dim edir = staged.ExtractedPath
            If String.IsNullOrEmpty(edir) Then edir = ExtractedDir(staged.Version)
            If Not File.Exists(Path.Combine(edir, "GSM.Manager.exe")) OrElse
               Not File.Exists(Path.Combine(edir, "GSM.Contracts.dll")) Then
                r.ErrorMessage = "The staged update is missing GSM.Manager.exe or GSM.Contracts.dll."
                Return r
            End If

            ' Downgrade guard: never apply an older (or unparseable-as-
            ' newer) build over a DB a newer build may have migrated.
            Dim cmp = StagedVersusRunning(staged.Version)
            If cmp.HasValue AndAlso cmp.Value < 0 Then
                r.ErrorMessage = $"The staged update ({staged.Version}) is older than the running version. Applying it would downgrade PowerGSM."
                Return r
            End If

            Try
                ' Pre-flight 1 (informational): authoritative contracts
                ' version from the staged build, if present.
                Dim bi = ReadBuildInfo(edir)
                If bi IsNot Nothing Then
                    _logger.LogInformation("UpdateOrchestrator: staged build {Version}, contracts {Contracts}",
                                           bi.Version, bi.ContractsVersion)
                End If

                Dim script = GenerateApplyScript(staged.Version)
                _pendingApplyScript = script
                _pendingVersion = staged.Version

                ' Stash the running version so the post-update binary can
                ' record a from→to row in update history.
                Try
                    Using scope = _serviceProvider.CreateScope()
                        Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                        db.SetSetting(GsmDataExtensions.SettingKeys.UpdatePendingFromVersion, GetRunningVersionString())
                        db.SaveChanges()
                    End Using
                Catch
                End Try

                r.Ok = True
                r.ScriptPath = script
            Catch ex As Exception
                r.ErrorMessage = ex.Message
                _logger.LogWarning(ex, "UpdateOrchestrator: generating apply.cmd failed")
            End Try
            Return r
        End Function

        ''' <summary>Spawn the prepared apply.cmd, detached. Caller then exits cleanly.</summary>
        Public Sub LaunchPendingApply()
            If Not HasPendingApply Then Return
            Dim q = Chr(34)
            Dim psi As New ProcessStartInfo() With {
                .FileName = "cmd.exe",
                .Arguments = "/c " & q & q & _pendingApplyScript & q & q,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .WorkingDirectory = InstallEnvironment.InstallDirectory()
            }
            Process.Start(psi)
            _logger.LogInformation("UpdateOrchestrator: launched apply.cmd for {Version}", _pendingVersion)
        End Sub

        ''' <summary>
        ''' Post-update startup cleanup: drop the staging version folder
        ''' (keep .updates\rollback\), and clear the staged + latest
        ''' version keys so the next poll re-detects fresh.
        ''' </summary>
        Public Sub CompletePostUpdate(version As String)
            Try
                _logger.LogInformation("UpdateOrchestrator: post-update startup for {Version}", version)
                If Not String.IsNullOrEmpty(version) Then TryDelete(VersionDir(version))
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim fromV = db.GetSetting(GsmDataExtensions.SettingKeys.UpdatePendingFromVersion, "")
                    db.UpdateHistory.Add(New UpdateHistoryEntity With {
                        .HistoryId = Guid.NewGuid().ToString("N"),
                        .AppliedAtUtc = DateTime.UtcNow,
                        .FromVersion = fromV,
                        .ToVersion = version,
                        .Outcome = "Success",
                        .Detail = Nothing
                    })
                    db.SetSetting(GsmDataExtensions.SettingKeys.UpdateStagedVersion, "")
                    db.SetSetting(GsmDataExtensions.SettingKeys.UpdateLatestVersion, "")
                    db.SetSetting(GsmDataExtensions.SettingKeys.UpdatePendingFromVersion, "")
                    db.SaveChanges()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "UpdateOrchestrator: post-update cleanup failed")
            End Try
        End Sub

        ''' <summary>
        ''' Record a Failed apply in history — called on startup when a
        ''' prior apply left an apply-error.log. From/To come from the
        ''' stashed pending-from version and the still-staged target.
        ''' </summary>
        Public Sub RecordFailedApply(detail As String)
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim fromV = db.GetSetting(GsmDataExtensions.SettingKeys.UpdatePendingFromVersion, "")
                    Dim toV = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateStagedVersion, "")
                    db.UpdateHistory.Add(New UpdateHistoryEntity With {
                        .HistoryId = Guid.NewGuid().ToString("N"),
                        .AppliedAtUtc = DateTime.UtcNow,
                        .FromVersion = fromV,
                        .ToVersion = toV,
                        .Outcome = "Failed",
                        .Detail = If(String.IsNullOrWhiteSpace(detail), Nothing, detail.Trim())
                    })
                    db.SetSetting(GsmDataExtensions.SettingKeys.UpdatePendingFromVersion, "")
                    db.SaveChanges()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "UpdateOrchestrator: RecordFailedApply failed")
            End Try
        End Sub

        ''' <summary>Update history, most-recent first (capped).</summary>
        Public Function GetHistory(Optional max As Integer = 100) As List(Of UpdateHistoryEntity)
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Return db.UpdateHistory.
                        OrderByDescending(Function(h) h.AppliedAtUtc).
                        Take(max).
                        ToList()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "UpdateOrchestrator: GetHistory failed")
                Return New List(Of UpdateHistoryEntity)()
            End Try
        End Function

        ''' <summary>
        ''' Read + clear .updates\apply-error.log (written by apply.cmd's
        ''' fail path). Checked on every startup so a failed swap that
        ''' never relaunched still surfaces on the next manual launch.
        ''' </summary>
        Public Function TakeApplyError() As String
            Try
                Dim p = Path.Combine(UpdatesRoot(), "apply-error.log")
                If File.Exists(p) Then
                    Dim txt = File.ReadAllText(p)
                    TryDeleteFile(p)
                    Return txt
                End If
            Catch ex As Exception
                _logger.LogDebug(ex, "UpdateOrchestrator: TakeApplyError failed")
            End Try
            Return Nothing
        End Function

        ''' <summary>
        ''' Generate &lt;install&gt;\.updates\apply.cmd. cd's into the
        ''' install dir so all copies use relative paths; waits (via
        ''' tasklist) for GSM.Manager.exe to fully exit before swapping
        ''' so the locked running binary is gone; backs up to rollback\;
        ''' relaunches with --post-update; self-deletes. Written without
        ''' a BOM — a UTF-8 BOM ahead of "@echo off" breaks cmd parsing.
        ''' </summary>
        Private Function GenerateApplyScript(version As String) As String
            Dim install = InstallEnvironment.InstallDirectory()
            Dim scriptPath = Path.Combine(UpdatesRoot(), "apply.cmd")
            Dim managerExe = Path.Combine(install, "GSM.Manager.exe")
            Dim q = Chr(34)
            Dim L As New List(Of String)

            L.Add("@echo off")
            L.Add("setlocal")
            L.Add("cd /d " & q & install & q)
            L.Add("")
            L.Add("rem Wait for the running Manager to fully exit (its .exe/.dll")
            L.Add("rem are locked while it runs; this also covers an AV pause).")
            L.Add(":waitloop")
            L.Add("timeout /t 1 /nobreak >nul")
            L.Add("tasklist /fi " & q & "imagename eq GSM.Manager.exe" & q & " 2>nul | findstr /i " & q & "GSM.Manager.exe" & q & " >nul")
            L.Add("if not errorlevel 1 goto waitloop")
            L.Add("")
            L.Add("rem Back up current binaries for manual rollback.")
            L.Add("if not exist " & q & ".updates\rollback" & q & " mkdir " & q & ".updates\rollback" & q)
            L.Add("copy /Y " & q & "GSM.Manager.exe" & q & " " & q & ".updates\rollback\GSM.Manager.exe" & q & " >nul")
            L.Add("copy /Y " & q & "GSM.Contracts.dll" & q & " " & q & ".updates\rollback\GSM.Contracts.dll" & q & " >nul")
            L.Add("")
            L.Add("rem Swap in the staged build. Mirror the WHOLE extracted")
            L.Add("rem payload (not just exe + contracts) so new support files")
            L.Add("rem such as WebView2Loader.dll, the runtimes\ tree, and any")
            L.Add("rem added dependency reach the install too. No /MIR, so the")
            L.Add("rem DB and other install-only files are left untouched. /XF")
            L.Add("rem and /XD hard-exclude stateful files so even a packaging")
            L.Add("rem mistake that put gsm.db in the zip can't clobber the live")
            L.Add("rem DB, saved web sessions, logs, or the staging tree.")
            L.Add("robocopy " & q & ".updates\" & version & "\extracted" & q & " " & q & "." & q & " /E /IS /IT /XF gsm.db gsm.db-wal gsm.db-shm nodesettings.json /XD .updates WebView2Data logs /NFL /NDL /NJH /NJS /NP >nul")
            L.Add("if errorlevel 8 goto fail")
            L.Add("")
            L.Add("rem Relaunch the new Manager (unwatched until the next logon")
            L.Add("rem restarts the watchdog, which then monitors via the mutex).")
            L.Add("start " & q & q & " /D " & q & install & q & " " & q & managerExe & q & " --post-update " & version)
            L.Add("")
            L.Add("del " & q & "%~f0" & q)
            L.Add("exit /b 0")
            L.Add("")
            L.Add(":fail")
            L.Add("echo [%DATE% %TIME%] Update apply failed copying new binaries for " & version & ". Restore from .updates\rollback if needed. >> " & q & ".updates\apply-error.log" & q)
            L.Add("exit /b 1")

            File.WriteAllText(scriptPath, String.Join(vbCrLf, L), New System.Text.UTF8Encoding(False))
            Return scriptPath
        End Function

    End Class

    ''' <summary>BUILD-INFO.json sidecar (written into the Manager zip by release.yml).</summary>
    Public Class BuildInfo
        <JsonPropertyName("product")> Public Property Product As String
        <JsonPropertyName("version")> Public Property Version As String
        <JsonPropertyName("tag")> Public Property Tag As String
        <JsonPropertyName("commit")> Public Property Commit As String
        <JsonPropertyName("buildUtc")> Public Property BuildUtc As String
        <JsonPropertyName("targetFramework")> Public Property TargetFramework As String
        <JsonPropertyName("runtimeIdentifier")> Public Property RuntimeIdentifier As String
        <JsonPropertyName("contractsVersion")> Public Property ContractsVersion As String
    End Class

    ''' <summary>Outcome of preparing an apply (generating apply.cmd).</summary>
    Public Class ApplyPrepareResult
        Public Property Ok As Boolean
        Public Property ScriptPath As String
        Public Property ErrorMessage As String
    End Class

End Namespace
