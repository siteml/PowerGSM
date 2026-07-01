Imports System
Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Runtime.Versioning
Imports System.Security.Cryptography
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Hosting.Systemd
Imports Microsoft.Extensions.Logging

' ============================================================
'  GSM.Node — Self-update staging + update-exit (Phase 8-2, slice 6)
'
'  The node's whole job in a self-update is:
'    receive verified bytes -> atomic-rename to GSM.Node.new -> detach
'    shims (graceful) -> exit.
'  It does NOT version-check, does NOT decide whether to update, and does
'  NOT swap its own running image (a process cannot replace the binary it
'  is executing). Intelligence lives in the Manager (decide + verify); the
'  swap lives in a survivor that outlives the node:
'    - Linux under systemd: systemd's Restart=on-failure relaunches, and an
'      idempotent ExecStartPre swap step moves GSM.Node.new into place first.
'    - Everything else (Windows service, Windows bare, Linux bare): a
'      detached GSM.NodeSetup --apply-update --wait-pid <self> waits for this
'      process to die, swaps .new over the (now-unlocked) live binary, and
'      relaunches.
'
'  Staging transport is a chunked session: begin -> chunk* -> commit. Each
'  chunk is a small streamed body written append-only to a temp .part file;
'  commit verifies SHA-256 + size over the whole .part then atomic-renames
'  it to GSM.Node.new. The Manager pushes only bytes it has already verified
'  (D3) — the node re-verifies what it received as integrity insurance, not
'  as the trust boundary.
'
'  Naming rule (must match the survivor side): the staged/kept files are the
'  live binary filename with ".new" / ".old" appended, beside the live binary
'  in AppContext.BaseDirectory. So on Linux GSM.Node -> GSM.Node.new /
'  GSM.Node.old; on Windows GSM.Node.exe -> GSM.Node.exe.new / .old. In-flight
'  uploads use GSM.Node[.exe].<uploadId>.part.
' ============================================================

Namespace GSM.Node

    ''' <summary>
    ''' DI singleton. Owns the chunked staging sessions and the update-exit
    ''' orchestration. Registered in NodeProgram; consumed by SystemEndpoints
    ''' and read once by NodeProgram after app.Run() to decide the Linux
    ''' non-zero exit code.
    ''' </summary>
    Public Class SelfUpdateService

        ''' <summary>
        ''' Linux non-zero exit code used when relying on systemd's
        ''' Restart=on-failure to relaunch the node after an update-exit.
        ''' A clean exit (not a signal), small and distinctive, so journald
        ''' doesn't read it as a crash but Restart=on-failure still fires.
        ''' </summary>
        Public Const UpdateExitCode As Integer = 10

        ''' <summary>
        ''' Per-request body cap lifted on the chunk endpoint. Individual
        ''' chunks stream straight to disk and are bounded against the
        ''' declared totalBytes in AppendChunkAsync, so this is just a sane
        ''' ceiling rather than a correctness boundary. Generous enough that
        ''' the Manager can pick a large chunk size without tripping it.
        ''' </summary>
        Public Const ChunkBodyCapBytes As Long = 256L * 1024L * 1024L

        Private Const NewSuffix As String = ".new"
        Private Const OldSuffix As String = ".old"
        Private Const PartSuffix As String = ".part"

        Private ReadOnly _logger As ILogger(Of SelfUpdateService)
        Private ReadOnly _sessions As New ConcurrentDictionary(Of String, StagingSession)(StringComparer.Ordinal)

        Private _updateExitRequested As Boolean
        Private _exitNonZeroForSystemd As Boolean

        Public Sub New(logger As ILogger(Of SelfUpdateService))
            _logger = logger
            ' Sweep any leftover .part files from a crashed prior upload so a
            ' restart doesn't accumulate orphaned partials beside the binary.
            Try
                For Each f In Directory.EnumerateFiles(AppContext.BaseDirectory, "GSM.Node*" & PartSuffix)
                    Try
                        File.Delete(f)
                    Catch
                    End Try
                Next
            Catch
                ' Best effort; a stale .part is harmless until the next begin
                ' for the same target supersedes it.
            End Try

            ' Sweep leftover shim staging artifacts (.part / .superseded-*) under
            ' GSM.Shim\<version>\ from a crashed or interrupted shim push.
            Try
                Dim shimRoot = Path.Combine(AppContext.BaseDirectory, "GSM.Shim")
                If Directory.Exists(shimRoot) Then
                    For Each f In Directory.EnumerateFiles(shimRoot, "*" & PartSuffix, SearchOption.AllDirectories)
                        Try
                            File.Delete(f)
                        Catch
                        End Try
                    Next
                    For Each f In Directory.EnumerateFiles(shimRoot, "*.superseded-*", SearchOption.AllDirectories)
                        Try
                            File.Delete(f)
                        Catch
                        End Try
                    Next
                End If
            Catch
            End Try
        End Sub

        ''' <summary>True once an update-exit has been requested.</summary>
        Public ReadOnly Property UpdateExitRequested As Boolean
            Get
                Return _updateExitRequested
            End Get
        End Property

        ''' <summary>
        ''' True when the requested update-exit is relying on systemd to
        ''' relaunch the node (Linux under systemd) — NodeProgram exits
        ''' non-zero in that case so Restart=on-failure fires.
        ''' </summary>
        Public ReadOnly Property ExitNonZeroForSystemd As Boolean
            Get
                Return _exitNonZeroForSystemd
            End Get
        End Property

        ' --------------------------------------------------------
        ' Staging — begin / chunk / commit
        ' --------------------------------------------------------

        ''' <summary>
        ''' Opens a staging session for a target. Supersedes any in-flight
        ''' session for the same target (one upload per target at a time).
        ''' Returns 200 {uploadId} or 400 {error}.
        ''' </summary>
        Public Function Begin(request As StageBeginRequest) As StageOpResult
            If request Is Nothing Then
                Return StageOpResult.Make(400, New With {.error = "missing request body"})
            End If
            If request.TotalBytes <= 0 Then
                Return StageOpResult.Make(400, New With {.error = "totalBytes must be greater than zero"})
            End If
            If String.IsNullOrWhiteSpace(request.Sha256) Then
                Return StageOpResult.Make(400, New With {.error = "sha256 is required"})
            End If

            Dim paths = ResolveTarget(request.TargetName, request.Version)
            If paths Is Nothing Then
                Return StageOpResult.Make(400, New With {.error = "unknown target"})
            End If
            If paths.Shape = TargetShape.VersionedInstall AndAlso String.IsNullOrEmpty(paths.InstallPath) Then
                Return StageOpResult.Make(400, New With {.error = "a valid version is required to stage the shim"})
            End If

            ' A versioned install (shim) lands directly in GSM.Shim\<version>\;
            ' create that folder up front so both the .part and the final binary
            ' live inside it.
            If paths.Shape = TargetShape.VersionedInstall Then
                Try
                    Directory.CreateDirectory(Path.GetDirectoryName(paths.InstallPath))
                Catch ex As Exception
                    Return StageOpResult.Make(400, New With {.error = "could not create shim version folder: " & ex.Message})
                End Try
            End If

            ' Supersede any existing session for the same target (same
            ' destination) and clean up its partial.
            SupersedeTarget(paths.NewPath)

            Dim uploadId = Guid.NewGuid().ToString("N")
            Dim partPath = paths.LivePath & "." & uploadId & PartSuffix
            Try
                If File.Exists(partPath) Then File.Delete(partPath)
            Catch
            End Try

            Dim session As New StagingSession With {
                .UploadId = uploadId,
                .TargetName = paths.TargetName,
                .Shape = paths.Shape,
                .PartPath = partPath,
                .NewPath = paths.NewPath,
                .OldPath = paths.OldPath,
                .TotalBytes = request.TotalBytes,
                .ExpectedSha = request.Sha256.Trim().ToLowerInvariant(),
                .Version = request.Version
            }
            _sessions(uploadId) = session

            _logger.LogInformation(
                "Self-update staging begun: target={Target} id={Id} bytes={Bytes}",
                session.TargetName, uploadId, request.TotalBytes)
            Dim beginResult = StageOpResult.Make(200, New With {.uploadId = uploadId})
            beginResult.UploadId = uploadId
            Return beginResult
        End Function

        ''' <summary>
        ''' Appends a chunk at <paramref name="offset"/>, which must equal the
        ''' current length of the .part file (append-only / resumable). Streams
        ''' the body straight to disk, bounded against the session's totalBytes.
        ''' 404 unknown id, 409 offset mismatch, 413 overshoot, 200 {received}.
        ''' </summary>
        Public Async Function AppendChunkAsync(uploadId As String,
                                               offset As Long,
                                               body As Stream,
                                               ct As CancellationToken) As Task(Of StageOpResult)
            Dim session As StagingSession = Nothing
            If Not _sessions.TryGetValue(uploadId, session) Then
                Return StageOpResult.Make(404, New With {.error = "unknown uploadId"})
            End If

            Await session.Gate.WaitAsync(ct)
            Try
                Dim currentLen As Long = If(File.Exists(session.PartPath),
                                            New FileInfo(session.PartPath).Length, 0L)
                If offset <> currentLen Then
                    ' Tell the caller where we actually are so it can resync.
                    Return StageOpResult.Make(409, New With {
                        .error = "offset mismatch", .expectedOffset = currentLen})
                End If

                Dim remaining As Long = session.TotalBytes - currentLen
                If remaining <= 0 Then
                    Return StageOpResult.Make(409, New With {
                        .error = "upload already complete", .expectedOffset = currentLen})
                End If

                Dim written As Long = 0
                Using fs As New FileStream(session.PartPath, FileMode.Append, FileAccess.Write,
                                           FileShare.None, 1024 * 1024, useAsync:=True)
                    Dim buffer(262143) As Byte ' 256 KB
                    Do
                        Dim toRead As Integer = CInt(Math.Min(buffer.Length, remaining - written))
                        If toRead <= 0 Then Exit Do
                        Dim n = Await body.ReadAsync(buffer.AsMemory(0, toRead), ct)
                        If n <= 0 Then Exit Do
                        Await fs.WriteAsync(buffer.AsMemory(0, n), ct)
                        written += n
                    Loop

                    ' If the body still has bytes beyond what totalBytes allows,
                    ' the Manager mis-sized the push — reject rather than silently
                    ' overrunning the declared size.
                    Dim overflow = Await body.ReadAsync(buffer.AsMemory(0, 1), ct)
                    Await fs.FlushAsync(ct)
                    If overflow > 0 Then
                        Return StageOpResult.Make(413, New With {
                            .error = "chunk exceeds declared totalBytes"})
                    End If
                End Using

                Dim newLen = currentLen + written
                Return StageOpResult.Make(200, New With {
                    .received = newLen, .totalBytes = session.TotalBytes})
            Finally
                session.Gate.Release()
            End Try
        End Function

        ''' <summary>
        ''' Verifies size + SHA-256 over the whole .part, then atomic-renames it
        ''' to the target's .new (overwriting any stale .new). On Linux the
        ''' staged binary is marked +x so it is runnable regardless of which
        ''' survivor swaps it. 404 unknown id, 422 size/sha mismatch,
        ''' 200 {staged}.
        ''' </summary>
        Public Async Function CommitAsync(uploadId As String,
                                          ct As CancellationToken) As Task(Of StageOpResult)
            Dim session As StagingSession = Nothing
            If Not _sessions.TryGetValue(uploadId, session) Then
                Return StageOpResult.Make(404, New With {.error = "unknown uploadId"})
            End If

            Await session.Gate.WaitAsync(ct)
            Try
                If Not File.Exists(session.PartPath) Then
                    Return StageOpResult.Make(422, New With {.error = "no data staged"})
                End If

                Dim actualLen = New FileInfo(session.PartPath).Length
                If actualLen <> session.TotalBytes Then
                    Return StageOpResult.Make(422, New With {
                        .error = "size mismatch",
                        .expected = session.TotalBytes,
                        .actual = actualLen})
                End If

                Dim actualSha = Await ComputeSha256HexAsync(session.PartPath, ct)
                If Not actualSha.Equals(session.ExpectedSha, StringComparison.OrdinalIgnoreCase) Then
                    Try
                        File.Delete(session.PartPath)
                    Catch
                    End Try
                    _logger.LogWarning(
                        "Self-update commit rejected (sha mismatch): target={Target} id={Id}",
                        session.TargetName, uploadId)
                    Return StageOpResult.Make(422, New With {.error = "sha256 mismatch"})
                End If

                ' Defense-in-depth platform guard. The staged bytes must be a
                ' native executable for THIS node's OS, or the swap would put a
                ' binary the box can't run into place and the node wouldn't come
                ' back. The Manager already magic-byte-matches before pushing, so
                ' this is the last line — a direct API call or a Manager bug must
                ' not be able to brick the node. Only a *recognized wrong-OS*
                ' format is rejected; an unrecognized format passes (mirrors the
                ' Manager policy — the operator owns it and the 8b health-gate is
                ' the backstop).
                Dim stagedFormat = DetectStagedFormat(session.PartPath)
                Dim expectedFormat = If(OperatingSystem.IsWindows(), StagedFormat.WindowsPe, StagedFormat.LinuxElf)
                If stagedFormat <> StagedFormat.Unknown AndAlso stagedFormat <> expectedFormat Then
                    Try
                        File.Delete(session.PartPath)
                    Catch
                    End Try
                    _logger.LogWarning(
                        "Self-update commit rejected (platform mismatch): target={Target} id={Id} staged={Staged} node={Node}",
                        session.TargetName, uploadId, stagedFormat, expectedFormat)
                    Return StageOpResult.Make(422, New With {
                        .error = "platform mismatch",
                        .staged = If(stagedFormat = StagedFormat.WindowsPe, "windows", "linux"),
                        .node = If(OperatingSystem.IsWindows(), "windows", "linux")})
                End If

                ' Place the verified bytes. Swap shapes (node / nodesetup) write
                ' the side-by-side .new that the apply step swaps; a versioned
                ' install (shim) lands the binary directly in its version folder.
                ' A brand-new version folder is conflict-free; a same-version
                ' RE-push (e.g. replacing a corrupted shim) replaces the existing
                ' binary when the OS lets it be freed, and otherwise FAILS CLEANLY
                ' (see PlaceLockSafe) — never a half-applied or torn binary.
                If session.Shape = TargetShape.VersionedInstall Then
                    Dim placeErr = PlaceLockSafe(session.PartPath, session.NewPath)
                    If placeErr IsNot Nothing Then
                        _logger.LogWarning(
                            "Self-update commit could not place shim: target={Target} id={Id} reason={Reason}",
                            session.TargetName, uploadId, placeErr)
                        ' Leave the .part for the startup sweep; nothing was torn.
                        Return StageOpResult.Make(409, New With {.error = placeErr})
                    End If
                Else
                    ' Atomic rename within one filesystem: the file is only ever
                    ' the old part or the final .new, never torn.
                    File.Move(session.PartPath, session.NewPath, overwrite:=True)
                End If
                If Not OperatingSystem.IsWindows() Then
                    EnsureExecutable(session.NewPath)
                End If

                Dim removed As StagingSession = Nothing
                _sessions.TryRemove(uploadId, removed)

                _logger.LogInformation(
                    "Self-update staged: target={Target} -> {Path}",
                    session.TargetName, session.NewPath)
                Return StageOpResult.Make(200, New With {
                    .staged = True,
                    .target = session.TargetName,
                    .path = session.NewPath,
                    .version = session.Version})
            Finally
                session.Gate.Release()
            End Try
        End Function

        ' --------------------------------------------------------
        ' Update-exit
        ' --------------------------------------------------------

        ''' <summary>
        ''' Applies a staged update for a target. Dispatches by target shape:
        '''   node      -> graceful update-exit; a survivor swaps + relaunches
        '''                (RequiresExit = True — the endpoint then stops the host)
        '''   nodesetup -> in-process swap of the idle NodeSetup binary; the node
        '''                keeps running (no survivor, no exit)
        '''   shim      -> no-op: commit already installed the new version folder
        ''' Refuses (Accepted = False) when nothing is staged for a swap target.
        ''' </summary>
        Public Function ApplyUpdate(targetName As String) As ApplyResult
            Dim name = If(String.IsNullOrWhiteSpace(targetName), "node", targetName.Trim().ToLowerInvariant())
            Select Case name
                Case "node"
                    Return RequestUpdateExit(ResolveTarget("node", Nothing))
                Case "nodesetup"
                    Return ApplyInPlaceSwap(ResolveTarget("nodesetup", Nothing))
                Case "shim"
                    ' The versioned shim binary is installed at commit time; there
                    ' is nothing to swap and the node does not bounce. New spawns
                    ' pick the highest version folder.
                    Return ApplyResult.Ok("installed")
                Case Else
                    Return ApplyResult.Fail("unknown target")
            End Select
        End Function

        ''' <summary>
        ''' Node target: graceful update-exit. Refuses if no .new is staged.
        ''' Under systemd we defer the swap + relaunch to systemd (non-zero
        ''' exit); otherwise we spawn the detached NodeSetup relauncher and exit
        ''' clean. RequiresExit = True so the endpoint schedules the host stop
        ''' (which fires ApplicationStopping -> DetachShimsForShutdown).
        ''' </summary>
        Private Function RequestUpdateExit(paths As TargetPaths) As ApplyResult
            If paths Is Nothing Then
                Return ApplyResult.Fail("unknown target")
            End If
            If Not File.Exists(paths.NewPath) Then
                Return ApplyResult.Fail("no staged update (" & Path.GetFileName(paths.NewPath) & " not found)")
            End If

            Dim underSystemd As Boolean = (Not OperatingSystem.IsWindows()) AndAlso IsUnderSystemd()
            If underSystemd Then
                _exitNonZeroForSystemd = True
                _updateExitRequested = True
                _logger.LogInformation(
                    "Update-exit: deferring swap + relaunch to systemd (ExecStartPre + Restart=on-failure).")
                Return ApplyResult.Ok("systemd", requiresExit:=True)
            End If

            ' Windows service, Windows bare, or Linux bare: NodeSetup is the
            ' universal fallback survivor. Launch it BEFORE we exit so it is
            ' already waiting on our PID.
            If Not LaunchNodeSetupRelauncher() Then
                Return ApplyResult.Fail("failed to launch NodeSetup relauncher")
            End If
            _updateExitRequested = True
            _logger.LogInformation(
                "Update-exit: NodeSetup relauncher launched; node will exit clean and NodeSetup owns the swap + relaunch.")
            Return ApplyResult.Ok("nodesetup", requiresExit:=True)
        End Function

        ''' <summary>
        ''' NodeSetup target: swap the staged .new over the idle NodeSetup binary
        ''' in-process, keeping the previous as .old. NodeSetup runs only during a
        ''' node apply-update, so it is idle on disk here and the file isn't
        ''' locked. The node does NOT exit. There is no auto-revert for a bad
        ''' NodeSetup (it is exercised only on the next node apply); .old is kept
        ''' for manual restore.
        ''' </summary>
        Private Function ApplyInPlaceSwap(paths As TargetPaths) As ApplyResult
            If paths Is Nothing Then
                Return ApplyResult.Fail("unknown target")
            End If
            If Not File.Exists(paths.NewPath) Then
                Return ApplyResult.Fail("no staged update (" & Path.GetFileName(paths.NewPath) & " not found)")
            End If
            Try
                If File.Exists(paths.OldPath) Then File.Delete(paths.OldPath)
                If File.Exists(paths.LivePath) Then File.Move(paths.LivePath, paths.OldPath, overwrite:=True)
                File.Move(paths.NewPath, paths.LivePath, overwrite:=True)
                If Not OperatingSystem.IsWindows() Then EnsureExecutable(paths.LivePath)
                _logger.LogInformation("In-place update applied: {Target} -> {Path}", paths.TargetName, paths.LivePath)
                Return ApplyResult.Ok("in-place")
            Catch ex As Exception
                ' Best-effort restore if we moved live aside but failed to place .new.
                Try
                    If Not File.Exists(paths.LivePath) AndAlso File.Exists(paths.OldPath) Then
                        File.Move(paths.OldPath, paths.LivePath, overwrite:=True)
                    End If
                Catch
                End Try
                _logger.LogError(ex, "In-place update swap failed for {Target}.", paths.TargetName)
                Return ApplyResult.Fail("swap failed: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Schedules a graceful host stop shortly after the current HTTP
        ''' response has had a chance to flush, so the Manager gets its 202
        ''' before the node tears down. The stop fires ApplicationStopping,
        ''' which detaches the shims (games survive) per Phase 8-1.
        ''' </summary>
        Public Sub ScheduleStop(lifetime As IHostApplicationLifetime)
            ' Fire-and-forget the delayed stop on the thread pool. The delay lets
            ' the HTTP 202 flush before the host tears down. StopAfterDelayAsync
            ' is a named async function (not an inline async lambda) to avoid
            ' VB's Task(Of Object) inference warning; wrapping in Task.Run with a
            ' plain (non-async) lambda cleanly discards the awaitable without an
            ' unawaited-call warning.
            Task.Run(Function() StopAfterDelayAsync(lifetime))
        End Sub

        Private Async Function StopAfterDelayAsync(lifetime As IHostApplicationLifetime) As Task
            Try
                Await Task.Delay(300)
            Catch
            End Try
            Try
                lifetime.StopApplication()
            Catch ex As Exception
                _logger.LogError(ex, "StopApplication during update-exit failed.")
            End Try
        End Function

        ' --------------------------------------------------------
        ' Helpers
        ' --------------------------------------------------------

        ''' <summary>
        ''' Resolves a target name (+ version, for the shim) to its paths and
        ''' update shape. Three shapes:
        '''   node      (SwapWithSurvivor) — live GSM.Node[.exe]; stage .new, a
        '''             survivor swaps it over live and relaunches on exit.
        '''   nodesetup (SwapInPlace)      — live GSM.NodeSetup[.exe]; stage .new,
        '''             the node swaps it in-process (idle binary), keeps .old.
        '''   shim      (VersionedInstall) — install GSM.Shim\<version>\GSM.Shim[.exe]
        '''             directly at commit; no .new, no swap, no exit. Needs a
        '''             path-safe version (InstallPath = Nothing when absent).
        ''' Returns Nothing for an unknown target.
        ''' </summary>
        Private Function ResolveTarget(targetName As String, version As String) As TargetPaths
            Dim name = If(String.IsNullOrWhiteSpace(targetName), "node", targetName.Trim().ToLowerInvariant())
            Select Case name
                Case "node"
                    Dim live = NodeExePath("GSM.Node")
                    Return New TargetPaths With {
                        .TargetName = "node",
                        .Shape = TargetShape.SwapWithSurvivor,
                        .LivePath = live,
                        .NewPath = live & NewSuffix,
                        .OldPath = live & OldSuffix}
                Case "nodesetup"
                    Dim live = NodeExePath("GSM.NodeSetup")
                    Return New TargetPaths With {
                        .TargetName = "nodesetup",
                        .Shape = TargetShape.SwapInPlace,
                        .LivePath = live,
                        .NewPath = live & NewSuffix,
                        .OldPath = live & OldSuffix}
                Case "shim"
                    Dim install As String = ShimInstallPath(version)
                    ' install is Nothing when the version is missing/unsafe; Begin
                    ' rejects that. For apply (version unused) the shim path is a
                    ' no-op anyway.
                    Return New TargetPaths With {
                        .TargetName = "shim",
                        .Shape = TargetShape.VersionedInstall,
                        .LivePath = install,
                        .NewPath = install,
                        .OldPath = Nothing,
                        .InstallPath = install}
                Case Else
                    Return Nothing
            End Select
        End Function

        ''' <summary>Path to a sibling exe in the node's base dir (OS-suffixed).</summary>
        Private Shared Function NodeExePath(baseName As String) As String
            Dim exeName = If(OperatingSystem.IsWindows(), baseName & ".exe", baseName)
            Return Path.Combine(AppContext.BaseDirectory, exeName)
        End Function

        ''' <summary>
        ''' Resolves GSM.Shim\&lt;version&gt;\GSM.Shim[.exe] for a path-safe
        ''' version, mirroring ShimSession.ResolveShimExePath's layout. Returns
        ''' Nothing when the version is missing or not path-safe (defends the
        ''' version folder against traversal — it comes off a release tag / file
        ''' metadata).
        ''' </summary>
        Private Shared Function ShimInstallPath(version As String) As String
            Dim safe = SanitizeVersionForPath(version)
            If safe Is Nothing Then Return Nothing
            Dim exeName = If(OperatingSystem.IsWindows(), "GSM.Shim.exe", "GSM.Shim")
            Return Path.Combine(AppContext.BaseDirectory, "GSM.Shim", safe, exeName)
        End Function

        ''' <summary>
        ''' Accepts only a version made of [0-9A-Za-z.+-] (covers X.Y.Z and
        ''' X.Y.Z-rc1+sha), with no path separators or dot-dot segments. Returns
        ''' the trimmed value, or Nothing if empty/unsafe.
        ''' </summary>
        Private Shared Function SanitizeVersionForPath(version As String) As String
            If String.IsNullOrWhiteSpace(version) Then Return Nothing
            Dim v = version.Trim()
            If v = "." OrElse v = ".." OrElse v.Contains("..") Then Return Nothing
            For Each ch In v
                If Not (Char.IsLetterOrDigit(ch) OrElse ch = "."c OrElse ch = "-"c OrElse ch = "+"c) Then
                    Return Nothing
                End If
            Next
            Return v
        End Function

        ''' <summary>
        ''' Places <paramref name="partPath"/> at <paramref name="finalPath"/>,
        ''' tolerating an in-use destination only as far as the OS actually
        ''' allows. Order:
        '''   1. Nothing holds the destination -> delete it, move the part in.
        '''   2. Held open -> try to rename the live file aside (*.superseded-*),
        '''      then move the part in. Renaming an in-use file within the same
        '''      volume is usually permitted on Windows (image files are opened
        '''      FILE_SHARE_DELETE), but an AV/indexer/other opener can pin it, so
        '''      this is best-effort, NOT guaranteed.
        '''   3. OS refuses both -> return an error string. The caller fails the
        '''      commit cleanly (the verified .part is left for the sweep); the
        '''      operator restarts the instances on that shim version or pushes a
        '''      higher version.
        ''' Returns Nothing on success, or an error message on failure. The final
        ''' move only runs once the destination is free, so the binary is never
        ''' torn. *.superseded-* leftovers are swept on the next node start.
        ''' </summary>
        Private Shared Function PlaceLockSafe(partPath As String, finalPath As String) As String
            If File.Exists(finalPath) Then
                Dim freed As Boolean = False
                Try
                    File.Delete(finalPath)
                    freed = True
                Catch
                    ' Held open: try to move it aside instead of overwriting.
                    Try
                        Dim aside = finalPath & ".superseded-" & Guid.NewGuid().ToString("N")
                        File.Move(finalPath, aside)
                        freed = True
                    Catch ex As Exception
                        Return "destination shim binary is in use and could not be replaced " &
                               "(restart the instances on that shim version, or push a higher version): " & ex.Message
                    End Try
                End Try
                If Not freed Then
                    Return "destination shim binary is in use and could not be replaced"
                End If
            End If
            File.Move(partPath, finalPath)
            Return Nothing
        End Function

        Private Sub SupersedeTarget(newPath As String)
            For Each kvp In _sessions.ToArray()
                If kvp.Value.NewPath.Equals(newPath, StringComparison.OrdinalIgnoreCase) Then
                    Dim removed As StagingSession = Nothing
                    If _sessions.TryRemove(kvp.Key, removed) Then
                        Try
                            If File.Exists(removed.PartPath) Then File.Delete(removed.PartPath)
                        Catch
                        End Try
                    End If
                End If
            Next
        End Sub

        Private Shared Async Function ComputeSha256HexAsync(path As String,
                                                            ct As CancellationToken) As Task(Of String)
            Using sha = SHA256.Create()
                Using fs As New FileStream(path, FileMode.Open, FileAccess.Read,
                                           FileShare.Read, 1024 * 1024, useAsync:=True)
                    Dim hash = Await sha.ComputeHashAsync(fs, ct)
                    Return Convert.ToHexString(hash).ToLowerInvariant()
                End Using
            End Using
        End Function

        ''' <summary>Adds +x (user/group/other) to a staged binary on Linux.</summary>
        Private Shared Sub EnsureExecutable(path As String)
            Try
                Dim mode = File.GetUnixFileMode(path)
                Dim newMode = mode Or UnixFileMode.UserExecute Or
                              UnixFileMode.GroupExecute Or UnixFileMode.OtherExecute
                If mode <> newMode Then
                    File.SetUnixFileMode(path, newMode)
                End If
            Catch
                ' Best effort; the survivor's swap step also chmods on Linux.
            End Try
        End Sub

        ''' <summary>Executable format sniffed from a file's first bytes.</summary>
        Private Enum StagedFormat
            Unknown = 0
            WindowsPe = 1
            LinuxElf = 2
        End Enum

        ''' <summary>
        ''' Sniffs a file's executable format from its first bytes: ELF
        ''' (0x7F 'E' 'L' 'F') -> Linux, PE/MZ ('M' 'Z') -> Windows, else
        ''' Unknown. OS-level only (no architecture) — enough to reject a
        ''' recognized wrong-OS binary at commit. Never throws.
        ''' </summary>
        Private Shared Function DetectStagedFormat(path As String) As StagedFormat
            Try
                Dim head(3) As Byte
                Dim got As Integer
                Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                    got = fs.Read(head, 0, 4)
                End Using
                If got >= 4 AndAlso head(0) = &H7F AndAlso head(1) = &H45 AndAlso head(2) = &H4C AndAlso head(3) = &H46 Then
                    Return StagedFormat.LinuxElf
                End If
                If got >= 2 AndAlso head(0) = &H4D AndAlso head(1) = &H5A Then
                    Return StagedFormat.WindowsPe
                End If
            Catch
            End Try
            Return StagedFormat.Unknown
        End Function

        Private Function IsUnderSystemd() As Boolean
            Try
                Return SystemdHelpers.IsSystemdService()
            Catch
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Launches GSM.NodeSetup --apply-update --wait-pid &lt;self&gt; detached
        ''' so it outlives this node and can swap + relaunch. On Windows uses
        ''' native CreateProcessW with CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS
        ''' | CREATE_NEW_PROCESS_GROUP so a service/job can't reap it with us,
        ''' falling back to Process.Start if breakaway is refused. On Linux-bare a
        ''' plain detached Process.Start suffices (it reparents to PID 1 when we
        ''' exit cleanly).
        ''' </summary>
        Private Function LaunchNodeSetupRelauncher() As Boolean
            Dim setupName = If(OperatingSystem.IsWindows(), "GSM.NodeSetup.exe", "GSM.NodeSetup")
            Dim setupPath = Path.Combine(AppContext.BaseDirectory, setupName)
            If Not File.Exists(setupPath) Then
                _logger.LogError("NodeSetup not found at {Path}; cannot self-update via relauncher.", setupPath)
                Return False
            End If

            Dim pid = Environment.ProcessId

            If OperatingSystem.IsWindows() Then
                If LaunchDetachedWindows(setupPath, "--apply-update --wait-pid " & pid.ToString()) Then
                    Return True
                End If
                _logger.LogWarning("Native detached launch failed; falling back to Process.Start.")
            End If

            Try
                Dim psi As New ProcessStartInfo(setupPath) With {
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .WorkingDirectory = AppContext.BaseDirectory
                }
                psi.ArgumentList.Add("--apply-update")
                psi.ArgumentList.Add("--wait-pid")
                psi.ArgumentList.Add(pid.ToString())
                Dim proc = Process.Start(psi)
                Return proc IsNot Nothing
            Catch ex As Exception
                _logger.LogError(ex, "Failed to start NodeSetup relauncher.")
                Return False
            End Try
        End Function

        ' ---- Win32 detached launch (Windows only) ----

        Private Const CREATE_BREAKAWAY_FROM_JOB As UInteger = &H1000000UI
        Private Const DETACHED_PROCESS As UInteger = &H8UI
        Private Const CREATE_NEW_PROCESS_GROUP As UInteger = &H200UI

        <SupportedOSPlatform("windows")>
        Private Function LaunchDetachedWindows(exePath As String, arguments As String) As Boolean
            ' CreateProcessW may write into lpCommandLine; the interop marshaler
            ' hands it a temporary native copy, so passing a String is safe here.
            Dim cmdLine = """" & exePath & """ " & arguments
            Dim si As New STARTUPINFOW()
            si.cb = Marshal.SizeOf(GetType(STARTUPINFOW))
            Dim pi As PROCESS_INFORMATION = Nothing
            Dim workingDir = Path.GetDirectoryName(exePath)

            Dim flags As UInteger = CREATE_BREAKAWAY_FROM_JOB Or DETACHED_PROCESS Or CREATE_NEW_PROCESS_GROUP
            Dim ok = CreateProcessW(exePath, cmdLine, IntPtr.Zero, IntPtr.Zero, False,
                                    flags, IntPtr.Zero, workingDir, si, pi)
            If Not ok Then
                Dim err = Marshal.GetLastWin32Error()
                ' Breakaway can be refused when there's no job (or a job that
                ' disallows it). Retry without it — a node not in a kill-on-close
                ' job doesn't need it anyway.
                flags = DETACHED_PROCESS Or CREATE_NEW_PROCESS_GROUP
                ok = CreateProcessW(exePath, cmdLine, IntPtr.Zero, IntPtr.Zero, False,
                                    flags, IntPtr.Zero, workingDir, si, pi)
                If Not ok Then
                    _logger.LogWarning("CreateProcessW failed (err={Err}) launching relauncher.", err)
                    Return False
                End If
            End If

            If pi.hThread <> IntPtr.Zero Then CloseHandle(pi.hThread)
            If pi.hProcess <> IntPtr.Zero Then CloseHandle(pi.hProcess)
            Return True
        End Function

        <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Private Shared Function CreateProcessW(lpApplicationName As String,
                                               lpCommandLine As String,
                                               lpProcessAttributes As IntPtr,
                                               lpThreadAttributes As IntPtr,
                                               bInheritHandles As Boolean,
                                               dwCreationFlags As UInteger,
                                               lpEnvironment As IntPtr,
                                               lpCurrentDirectory As String,
                                               ByRef lpStartupInfo As STARTUPINFOW,
                                               ByRef lpProcessInformation As PROCESS_INFORMATION) As Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function CloseHandle(hObject As IntPtr) As Boolean
        End Function

        <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
        Private Structure STARTUPINFOW
            Public cb As Integer
            Public lpReserved As String
            Public lpDesktop As String
            Public lpTitle As String
            Public dwX As Integer
            Public dwY As Integer
            Public dwXSize As Integer
            Public dwYSize As Integer
            Public dwXCountChars As Integer
            Public dwYCountChars As Integer
            Public dwFillAttribute As Integer
            Public dwFlags As Integer
            Public wShowWindow As Short
            Public cbReserved2 As Short
            Public lpReserved2 As IntPtr
            Public hStdInput As IntPtr
            Public hStdOutput As IntPtr
            Public hStdError As IntPtr
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Private Structure PROCESS_INFORMATION
            Public hProcess As IntPtr
            Public hThread As IntPtr
            Public dwProcessId As Integer
            Public dwThreadId As Integer
        End Structure

        ' --------------------------------------------------------
        ' Nested types
        ' --------------------------------------------------------

        Private Class StagingSession
            Public Property UploadId As String
            Public Property TargetName As String
            Public Property Shape As TargetShape
            Public Property PartPath As String
            Public Property NewPath As String
            Public Property OldPath As String
            Public Property TotalBytes As Long
            Public Property ExpectedSha As String
            Public Property Version As String
            Public ReadOnly Gate As New SemaphoreSlim(1, 1)
        End Class

        Private Enum TargetShape
            SwapWithSurvivor = 0   ' node: stage .new, survivor swaps + relaunches on exit
            SwapInPlace = 1        ' nodesetup: stage .new, swap in-process (idle), no exit
            VersionedInstall = 2   ' shim: install directly into GSM.Shim\<version>\, no swap
        End Enum

        Private Class TargetPaths
            Public Property TargetName As String
            Public Property Shape As TargetShape
            Public Property LivePath As String
            Public Property NewPath As String
            Public Property OldPath As String
            Public Property InstallPath As String   ' VersionedInstall: final exe path
        End Class

    End Class

    ''' <summary>JSON body for POST /api/system/staged-binary/begin.</summary>
    Public Class StageBeginRequest
        ''' <summary>Target to stage. Defaults to "node" when omitted.</summary>
        Public Property TargetName As String
        ''' <summary>Total size of the binary in bytes.</summary>
        Public Property TotalBytes As Long
        ''' <summary>Lowercase/uppercase hex SHA-256 of the whole binary.</summary>
        Public Property Sha256 As String
        ''' <summary>Optional version string of the staged binary (for logs).</summary>
        Public Property Version As String
    End Class

    ''' <summary>
    ''' Uniform staging-operation result: an HTTP status code + a JSON payload.
    ''' The endpoint renders it via Results.Json(Payload, statusCode:=Code).
    ''' </summary>
    Public Class StageOpResult
        Public Property Code As Integer
        Public Property Payload As Object
        ''' <summary>Set on a successful Begin so in-process callers (the
        ''' --self-update-dry-run harness) can resume the session without
        ''' parsing the JSON payload. Not serialized over HTTP.</summary>
        Public Property UploadId As String

        Public Shared Function Make(code As Integer, payload As Object) As StageOpResult
            Return New StageOpResult With {.Code = code, .Payload = payload}
        End Function
    End Class

    ''' <summary>Result of an apply-update request.</summary>
    Public Class ApplyResult
        Public Property Accepted As Boolean
        Public Property Reason As String
        Public Property Survivor As String

        ''' <summary>
        ''' True when applying this target requires the node to exit so a survivor
        ''' can swap + relaunch (the "node" target). False for in-place (nodesetup)
        ''' and no-op (shim) applies, where the node keeps running.
        ''' </summary>
        Public Property RequiresExit As Boolean

        Public Shared Function Ok(survivor As String, Optional requiresExit As Boolean = False) As ApplyResult
            Return New ApplyResult With {.Accepted = True, .Survivor = survivor, .RequiresExit = requiresExit}
        End Function

        Public Shared Function Fail(reason As String) As ApplyResult
            Return New ApplyResult With {.Accepted = False, .Reason = reason}
        End Function
    End Class

End Namespace
