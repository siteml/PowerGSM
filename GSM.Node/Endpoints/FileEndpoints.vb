Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Threading.Tasks
Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Http
Imports Microsoft.AspNetCore.Http.Features
Imports GSM.Node.Api

' ============================================================
'  File operations endpoints
'
'  Phase 4c-1: list, download, upload, delete files inside a
'  whitelisted subdirectory of an instance's installation
'  directory. The manager owns the bookkeeping — every call
'  carries the install path, the relative path to act on, the
'  whitelist of allowed root subdirectories (sourced from the
'  plugin's IManagedDirectoriesProvider), and an optional
'  extension allowlist. The node validates and executes.
'
'  Wire shape (query parameters on every endpoint):
'
'    installPath        absolute path on the node, URL-encoded
'    path               relative path under installPath; for LIST
'                       a directory, otherwise a file
'    allowedRoots       semicolon-separated relative root paths
'                       sourced from ManagedDirectory.RelativePath;
'                       at least one must be a prefix of `path`
'    allowedExtensions  comma-separated extension allowlist (each
'                       entry may include the leading dot or not);
'                       optional. When set, LIST results are
'                       filtered and DOWNLOAD/UPLOAD/DELETE
'                       reject mismatched extensions
'    overwrite          UPLOAD only; "false" rejects when the
'                       destination exists. Defaults to true.
'
'  Validation floor (always enforced on the node, regardless of
'  what the manager sent):
'
'    1. The four required parameters above must be present.
'    2. `path` must be relative (not rooted).
'    3. `installPath` must exist as a directory.
'    4. The resolved absolute path must equal or sit beneath
'       the resolved installPath. ".." traversal is rejected by
'       the prefix check.
'    5. The resolved relative path must equal or sit beneath one
'       of the allowedRoots entries.
'    6. For non-LIST ops with allowedExtensions set, the file's
'       extension must be present in the allowlist.
'
'  All four endpoints sit behind the same auth middleware as
'  every other authenticated route. The instance-id segment in
'  the URL is purely organisational — file ops act on
'  installPath, not on a running ManagedInstance, so they work
'  whether or not the instance is currently up.
' ============================================================

Namespace GSM.Node.Endpoints

    Module FileEndpoints

        Public Sub Map(app As WebApplication)

            ' List entries under the resolved directory.
            app.MapGet("/api/instances/{instanceId}/files",
                Function(instanceId As String,
                         context As HttpContext) As IResult
                    Return ListFiles(context)
                End Function)

            ' Stream a file back to the manager.
            app.MapGet("/api/instances/{instanceId}/files/download",
                Function(instanceId As String,
                         context As HttpContext) As IResult
                    Return DownloadFile(context)
                End Function)

            ' Stream the request body to disk.
            app.MapPost("/api/instances/{instanceId}/files/upload",
                Async Function(instanceId As String,
                               context As HttpContext) As Task(Of IResult)
                    Return Await UploadFile(context)
                End Function)

            ' Delete a single file.
            app.MapDelete("/api/instances/{instanceId}/files",
                Function(instanceId As String,
                         context As HttpContext) As IResult
                    Return DeleteFile(context)
                End Function)

            ' Rename / move a file within the install. Validates
            ' both source and destination against allowedRoots and
            ' allowedExtensions, then performs an atomic File.Move.
            app.MapPost("/api/instances/{instanceId}/files/rename",
                Function(instanceId As String,
                         context As HttpContext) As IResult
                    Return RenameFile(context)
                End Function)

            ' Copy a file within the install. Source survives; a
            ' new file appears at newPath. Same validation as rename;
            ' useful for save-backup-before-play workflows.
            app.MapPost("/api/instances/{instanceId}/files/copy",
                Function(instanceId As String,
                         context As HttpContext) As IResult
                    Return CopyFile(context)
                End Function)

        End Sub

        ' ====================================================
        '  Endpoint handlers
        ' ====================================================

        Private Function ListFiles(context As HttpContext) As IResult
            Dim req = ParseRequest(context, requireFile:=False)
            If req.ErrorResult IsNot Nothing Then Return req.ErrorResult

            ' Treat a missing managed subdirectory as an empty
            ' listing rather than a 404. Several common cases
            ' produce a not-yet-existing directory that the
            ' manager UI would otherwise show as a scary error:
            '   - Factorio's saves/ doesn't exist until the
            '     server has completed a map load.
            '   - Last Oasis's Saved/Logs/ appears on first crash
            '     or first server run, not at install time.
            '   - Any plugin's mods/ before the user has installed
            '     a mod.
            ' The path has already been validated against the
            ' manager-supplied allowedRoots, so we know we're not
            ' covering up a path-traversal attempt — the directory
            ' is just not on disk yet. Upload's create-on-demand
            ' parent-dir handling means a subsequent upload still
            ' works against this directory without any extra
            ' bootstrap step.
            If Not Directory.Exists(req.AbsolutePath) Then
                Return Results.Ok(New List(Of FileEntry))
            End If

            Try
                Dim entries As New List(Of FileEntry)
                For Each filePath In Directory.EnumerateFiles(req.AbsolutePath)
                    If req.AllowedExtensions.Count > 0 Then
                        Dim ext = Path.GetExtension(filePath)
                        If String.IsNullOrEmpty(ext) Then Continue For
                        If Not req.AllowedExtensions.Contains(ext) Then Continue For
                    End If
                    Dim info As New FileInfo(filePath)
                    Dim relativeFromInstall = Path.GetRelativePath(req.AbsoluteInstallPath, filePath).
                        Replace("\"c, "/"c)
                    entries.Add(New FileEntry With {
                        .RelativePath = relativeFromInstall,
                        .SizeBytes = info.Length,
                        .ModifiedUtc = info.LastWriteTimeUtc
                    })
                Next
                Return Results.Ok(entries)
            Catch ex As Exception
                Return Results.Problem($"Failed to list files: {ex.Message}")
            End Try
        End Function

        Private Function DownloadFile(context As HttpContext) As IResult
            Dim req = ParseRequest(context, requireFile:=True)
            If req.ErrorResult IsNot Nothing Then Return req.ErrorResult

            If Not File.Exists(req.AbsolutePath) Then
                Return Results.NotFound(New With {.error = "File not found"})
            End If

            ' Open with FileShare.ReadWrite Or FileShare.Delete so a
            ' running instance writing to a save (or rotating logs)
            ' doesn't deny our read. Stream-based Results.File
            ' disposes the FileStream after the response completes.
            Try
                Dim fs = File.Open(req.AbsolutePath,
                                   FileMode.Open,
                                   FileAccess.Read,
                                   FileShare.ReadWrite Or FileShare.Delete)
                Return Results.File(fs,
                                    "application/octet-stream",
                                    Path.GetFileName(req.AbsolutePath))
            Catch ex As IOException
                Return Results.Problem($"Failed to open file: {ex.Message}")
            End Try
        End Function

        Private Async Function UploadFile(context As HttpContext) As Task(Of IResult)
            Dim req = ParseRequest(context, requireFile:=True)
            If req.ErrorResult IsNot Nothing Then Return req.ErrorResult

            ' overwrite=false rejects when the destination exists
            Dim overwrite = True
            Dim overwriteStr = context.Request.Query("overwrite").ToString()
            If Not String.IsNullOrEmpty(overwriteStr) Then
                Dim parsed As Boolean
                If Boolean.TryParse(overwriteStr, parsed) Then
                    overwrite = parsed
                End If
            End If

            If File.Exists(req.AbsolutePath) AndAlso Not overwrite Then
                Return Results.Conflict(New With {.error = "File already exists"})
            End If

            ' Disable the per-request body size limit so saves > the
            ' Kestrel / SecurityConfiguration default (4MB) succeed.
            ' D6: stream uploads, no cap. The feature must be set
            ' before the body starts being read.
            Try
                Dim feature = context.Features.Get(Of IHttpMaxRequestBodySizeFeature)()
                If feature IsNot Nothing AndAlso Not feature.IsReadOnly Then
                    feature.MaxRequestBodySize = Nothing
                End If
            Catch
                ' Feature unavailable on this server impl; fall back
                ' to whatever Kestrel is configured for. Worst case
                ' the upload fails fast on the size check rather than
                ' silently corrupting state.
            End Try

            ' Parent directory might not exist yet on the very first
            ' upload (e.g. saves/ before the game has ever booted).
            Dim parentDir = Path.GetDirectoryName(req.AbsolutePath)
            If Not String.IsNullOrEmpty(parentDir) AndAlso Not Directory.Exists(parentDir) Then
                Try
                    Directory.CreateDirectory(parentDir)
                Catch ex As Exception
                    Return Results.Problem($"Failed to create directory: {ex.Message}")
                End Try
            End If

            ' Stream to a sibling temp file first; rename atomically
            ' on success. A dropped connection or partial transfer
            ' leaves only the .uploading file behind, never a
            ' corrupted destination. Cleanup on cancel / exception.
            Dim tempPath = req.AbsolutePath & ".uploading"
            Try
                Using outFs As FileStream = File.Create(tempPath)
                    Await context.Request.Body.CopyToAsync(outFs, context.RequestAborted)
                End Using

                If File.Exists(req.AbsolutePath) Then
                    File.Delete(req.AbsolutePath)
                End If
                File.Move(tempPath, req.AbsolutePath)

                Dim info As New FileInfo(req.AbsolutePath)
                Return Results.Ok(New FileEntry With {
                    .RelativePath = req.RelativeFromInstall.Replace("\"c, "/"c),
                    .SizeBytes = info.Length,
                    .ModifiedUtc = info.LastWriteTimeUtc
                })
            Catch ex As OperationCanceledException
                TryDeleteTempFile(tempPath)
                Return Results.StatusCode(499)
            Catch ex As Exception
                TryDeleteTempFile(tempPath)
                Return Results.Problem($"Upload failed: {ex.Message}")
            End Try
        End Function

        Private Function DeleteFile(context As HttpContext) As IResult
            Dim req = ParseRequest(context, requireFile:=True)
            If req.ErrorResult IsNot Nothing Then Return req.ErrorResult

            If Not File.Exists(req.AbsolutePath) Then
                ' Idempotent: a missing target is not an error so the
                ' manager's optimistic "delete then refresh" flow
                ' doesn't fight a concurrent deletion.
                Return Results.Ok(New With {.deleted = False, .reason = "File not found"})
            End If

            Try
                File.Delete(req.AbsolutePath)
                Return Results.Ok(New With {.deleted = True})
            Catch ex As Exception
                Return Results.Problem($"Delete failed: {ex.Message}")
            End Try
        End Function

        ''' <summary>
        ''' Rename a file within the install directory. The source
        ''' path goes through the standard ParseRequest validation
        ''' (under installPath, in an allowed root, with allowed
        ''' extension). The destination gets the same treatment
        ''' inline below — ParseRequest only handles one path, and
        ''' refactoring it to handle two would touch the existing
        ''' four endpoints with no practical benefit.
        '''
        ''' Atomicity: File.Move with overwrite=True is atomic on
        ''' NTFS, ext4, and ReFS, which covers every supported
        ''' deployment platform. A crash mid-rename leaves either
        ''' the source or the destination on disk, never neither
        ''' and never both.
        ''' </summary>
        Private Function RenameFile(context As HttpContext) As IResult
            ' Source-path validation (matches the other file ops).
            Dim req = ParseRequest(context, requireFile:=True)
            If req.ErrorResult IsNot Nothing Then Return req.ErrorResult

            If Not File.Exists(req.AbsolutePath) Then
                Return Results.NotFound(New With {.error = "Source file not found"})
            End If

            ' Destination-path validation — mirrors what ParseRequest
            ' does for the source `path`, but for `newPath`.
            Dim newRel = context.Request.Query("newPath").ToString()
            If String.IsNullOrWhiteSpace(newRel) Then
                Return Results.BadRequest(New With {.error = "newPath is required"})
            End If

            newRel = newRel.Replace("/"c, Path.DirectorySeparatorChar).
                            Replace("\"c, Path.DirectorySeparatorChar)

            If Path.IsPathRooted(newRel) Then
                Return Results.BadRequest(New With {.error = "newPath must be relative"})
            End If

            Dim newAbsolute As String
            Try
                newAbsolute = Path.GetFullPath(Path.Combine(req.AbsoluteInstallPath, newRel))
            Catch ex As Exception
                Return Results.BadRequest(New With {.error = "Invalid newPath"})
            End Try

            Dim cmp = If(OperatingSystem.IsWindows(),
                         StringComparison.OrdinalIgnoreCase,
                         StringComparison.Ordinal)
            Dim installPrefix = req.AbsoluteInstallPath & Path.DirectorySeparatorChar
            If Not (newAbsolute.Equals(req.AbsoluteInstallPath, cmp) OrElse
                    newAbsolute.StartsWith(installPrefix, cmp)) Then
                Return Results.BadRequest(
                    New With {.error = "newPath escapes install directory"})
            End If

            Dim newRelativeFromInstall = newAbsolute.Substring(req.AbsoluteInstallPath.Length).
                TrimStart(Path.DirectorySeparatorChar)

            ' Re-parse allowedRoots for the destination check. The
            ' parsed list isn't on ParsedFileRequest, and re-splitting
            ' the same string is cheap.
            Dim allowedRoots = context.Request.Query("allowedRoots").ToString()
            Dim roots = allowedRoots.Split(";"c,
                StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries)
            Dim newOk = False
            For Each rawRoot In roots
                Dim root = rawRoot.Replace("/"c, Path.DirectorySeparatorChar).
                                   Replace("\"c, Path.DirectorySeparatorChar).
                                   TrimEnd(Path.DirectorySeparatorChar)
                If String.IsNullOrEmpty(root) Then Continue For
                If newRelativeFromInstall.Equals(root, cmp) OrElse
                   newRelativeFromInstall.StartsWith(root & Path.DirectorySeparatorChar, cmp) Then
                    newOk = True
                    Exit For
                End If
            Next
            If Not newOk Then
                Return Results.BadRequest(
                    New With {.error = "newPath is not under any allowed root directory"})
            End If

            ' Extension allowlist applies to the destination too.
            ' ParseRequest already populated AllowedExtensions on req.
            If req.AllowedExtensions.Count > 0 Then
                Dim ext = Path.GetExtension(newAbsolute)
                If String.IsNullOrEmpty(ext) OrElse Not req.AllowedExtensions.Contains(ext) Then
                    Return Results.BadRequest(
                        New With {.error = "newPath extension is not allowed"})
                End If
            End If

            ' Same-path no-op. The compare is filesystem-aware
            ' (case-insensitive on Windows) so a UI that round-trips
            ' the same name doesn't fail spuriously.
            If newAbsolute.Equals(req.AbsolutePath, cmp) Then
                Dim info As New FileInfo(req.AbsolutePath)
                Return Results.Ok(New FileEntry With {
                    .RelativePath = req.RelativeFromInstall.Replace("\"c, "/"c),
                    .SizeBytes = info.Length,
                    .ModifiedUtc = info.LastWriteTimeUtc
                })
            End If

            ' Overwrite flag — same default semantics as upload.
            Dim overwrite = False
            Dim overwriteStr = context.Request.Query("overwrite").ToString()
            If Not String.IsNullOrEmpty(overwriteStr) Then
                Dim parsed As Boolean
                If Boolean.TryParse(overwriteStr, parsed) Then
                    overwrite = parsed
                End If
            End If

            If File.Exists(newAbsolute) AndAlso Not overwrite Then
                Return Results.Conflict(New With {.error = "Destination file already exists"})
            End If

            ' Parent dir for the destination might not exist yet
            ' (cross-root rename, or first file in a never-used
            ' managed dir). Match upload's create-on-demand behaviour.
            Dim parentDir = Path.GetDirectoryName(newAbsolute)
            If Not String.IsNullOrEmpty(parentDir) AndAlso Not Directory.Exists(parentDir) Then
                Try
                    Directory.CreateDirectory(parentDir)
                Catch ex As Exception
                    Return Results.Problem($"Failed to create directory: {ex.Message}")
                End Try
            End If

            Try
                File.Move(req.AbsolutePath, newAbsolute, overwrite)
            Catch ex As Exception
                Return Results.Problem($"Rename failed: {ex.Message}")
            End Try

            Dim newInfo As New FileInfo(newAbsolute)
            Return Results.Ok(New FileEntry With {
                .RelativePath = newRelativeFromInstall.Replace("\"c, "/"c),
                .SizeBytes = newInfo.Length,
                .ModifiedUtc = newInfo.LastWriteTimeUtc
            })
        End Function

        ''' <summary>
        ''' Copy a file within the install directory. Mirrors the
        ''' validation flow of RenameFile but uses File.Copy so the
        ''' source survives. Designed for backup-before-modify use
        ''' cases (e.g. duplicating a save before loading it on a
        ''' running server).
        '''
        ''' Same-path is rejected with 400 BadRequest rather than
        ''' treated as a no-op — unlike rename, copy-onto-self has
        ''' no meaningful interpretation. The validation duplication
        ''' between this handler and RenameFile is intentional;
        ''' factoring out a shared helper would touch more code
        ''' than it saves until a third two-path operation appears.
        '''
        ''' Atomicity: File.Copy is not atomic — a crash mid-copy
        ''' leaves a partial destination on disk. Acceptable here
        ''' because the source is untouched, so retrying after a
        ''' failure simply overwrites the partial copy with a fresh
        ''' one. If atomic copy becomes important (e.g. another
        ''' tool watching for new files in the directory), switch
        ''' to Copy-to-temp + File.Move pattern.
        ''' </summary>
        Private Function CopyFile(context As HttpContext) As IResult
            Dim req = ParseRequest(context, requireFile:=True)
            If req.ErrorResult IsNot Nothing Then Return req.ErrorResult

            If Not File.Exists(req.AbsolutePath) Then
                Return Results.NotFound(New With {.error = "Source file not found"})
            End If

            Dim newRel = context.Request.Query("newPath").ToString()
            If String.IsNullOrWhiteSpace(newRel) Then
                Return Results.BadRequest(New With {.error = "newPath is required"})
            End If

            newRel = newRel.Replace("/"c, Path.DirectorySeparatorChar).
                            Replace("\"c, Path.DirectorySeparatorChar)

            If Path.IsPathRooted(newRel) Then
                Return Results.BadRequest(New With {.error = "newPath must be relative"})
            End If

            Dim newAbsolute As String
            Try
                newAbsolute = Path.GetFullPath(Path.Combine(req.AbsoluteInstallPath, newRel))
            Catch ex As Exception
                Return Results.BadRequest(New With {.error = "Invalid newPath"})
            End Try

            Dim cmp = If(OperatingSystem.IsWindows(),
                         StringComparison.OrdinalIgnoreCase,
                         StringComparison.Ordinal)
            Dim installPrefix = req.AbsoluteInstallPath & Path.DirectorySeparatorChar
            If Not (newAbsolute.Equals(req.AbsoluteInstallPath, cmp) OrElse
                    newAbsolute.StartsWith(installPrefix, cmp)) Then
                Return Results.BadRequest(
                    New With {.error = "newPath escapes install directory"})
            End If

            Dim newRelativeFromInstall = newAbsolute.Substring(req.AbsoluteInstallPath.Length).
                TrimStart(Path.DirectorySeparatorChar)

            Dim allowedRoots = context.Request.Query("allowedRoots").ToString()
            Dim roots = allowedRoots.Split(";"c,
                StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries)
            Dim newOk = False
            For Each rawRoot In roots
                Dim root = rawRoot.Replace("/"c, Path.DirectorySeparatorChar).
                                   Replace("\"c, Path.DirectorySeparatorChar).
                                   TrimEnd(Path.DirectorySeparatorChar)
                If String.IsNullOrEmpty(root) Then Continue For
                If newRelativeFromInstall.Equals(root, cmp) OrElse
                   newRelativeFromInstall.StartsWith(root & Path.DirectorySeparatorChar, cmp) Then
                    newOk = True
                    Exit For
                End If
            Next
            If Not newOk Then
                Return Results.BadRequest(
                    New With {.error = "newPath is not under any allowed root directory"})
            End If

            If req.AllowedExtensions.Count > 0 Then
                Dim ext = Path.GetExtension(newAbsolute)
                If String.IsNullOrEmpty(ext) OrElse Not req.AllowedExtensions.Contains(ext) Then
                    Return Results.BadRequest(
                        New With {.error = "newPath extension is not allowed"})
                End If
            End If

            ' Same-path is a caller bug for copy. Distinguish from
            ' rename's lenient behaviour by returning 400.
            If newAbsolute.Equals(req.AbsolutePath, cmp) Then
                Return Results.BadRequest(
                    New With {.error = "newPath must differ from source path"})
            End If

            Dim overwrite = False
            Dim overwriteStr = context.Request.Query("overwrite").ToString()
            If Not String.IsNullOrEmpty(overwriteStr) Then
                Dim parsed As Boolean
                If Boolean.TryParse(overwriteStr, parsed) Then
                    overwrite = parsed
                End If
            End If

            If File.Exists(newAbsolute) AndAlso Not overwrite Then
                Return Results.Conflict(New With {.error = "Destination file already exists"})
            End If

            Dim parentDir = Path.GetDirectoryName(newAbsolute)
            If Not String.IsNullOrEmpty(parentDir) AndAlso Not Directory.Exists(parentDir) Then
                Try
                    Directory.CreateDirectory(parentDir)
                Catch ex As Exception
                    Return Results.Problem($"Failed to create directory: {ex.Message}")
                End Try
            End If

            Try
                File.Copy(req.AbsolutePath, newAbsolute, overwrite)
            Catch ex As Exception
                Return Results.Problem($"Copy failed: {ex.Message}")
            End Try

            Dim copiedInfo As New FileInfo(newAbsolute)
            Return Results.Ok(New FileEntry With {
                .RelativePath = newRelativeFromInstall.Replace("\"c, "/"c),
                .SizeBytes = copiedInfo.Length,
                .ModifiedUtc = copiedInfo.LastWriteTimeUtc
            })
        End Function

        Private Sub TryDeleteTempFile(tempPath As String)
            Try
                If File.Exists(tempPath) Then File.Delete(tempPath)
            Catch
                ' Best-effort cleanup; nothing useful we can do if
                ' the OS won't let us remove the temp file.
            End Try
        End Sub

        ' ====================================================
        '  Request parsing & validation
        ' ====================================================

        ''' <summary>
        ''' Bundle of validated state shared by all four endpoint
        ''' handlers. ErrorResult is non-Nothing exactly when the
        ''' caller should bail and return that result instead of
        ''' proceeding with the file operation.
        ''' </summary>
        Private Class ParsedFileRequest
            Public AbsoluteInstallPath As String
            Public AbsolutePath As String
            Public RelativeFromInstall As String
            Public AllowedExtensions As HashSet(Of String)
            Public ErrorResult As IResult
        End Class

        ''' <summary>
        ''' Pulls installPath / path / allowedRoots /
        ''' allowedExtensions from the query string, normalises
        ''' separators, rejects path traversal, enforces the
        ''' allowed-roots whitelist, and (for file ops) enforces
        ''' the extension allowlist.
        ''' </summary>
        Private Function ParseRequest(context As HttpContext,
                                      requireFile As Boolean) As ParsedFileRequest
            Dim req As New ParsedFileRequest() With {
                .AllowedExtensions = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            }

            Dim installPath = context.Request.Query("installPath").ToString()
            Dim relPath = context.Request.Query("path").ToString()
            Dim allowedRoots = context.Request.Query("allowedRoots").ToString()
            Dim allowedExts = context.Request.Query("allowedExtensions").ToString()

            If String.IsNullOrWhiteSpace(installPath) Then
                req.ErrorResult = Results.BadRequest(New With {.error = "installPath is required"})
                Return req
            End If

            If String.IsNullOrWhiteSpace(relPath) Then
                req.ErrorResult = Results.BadRequest(New With {.error = "path is required"})
                Return req
            End If

            If String.IsNullOrWhiteSpace(allowedRoots) Then
                req.ErrorResult = Results.BadRequest(New With {.error = "allowedRoots is required"})
                Return req
            End If

            ' Normalise the user-supplied path to native separators
            ' so StartsWith comparisons work regardless of how the
            ' manager formed the URL.
            relPath = relPath.Replace("/"c, Path.DirectorySeparatorChar).
                              Replace("\"c, Path.DirectorySeparatorChar)

            If Path.IsPathRooted(relPath) Then
                req.ErrorResult = Results.BadRequest(New With {.error = "path must be relative"})
                Return req
            End If

            If Not Directory.Exists(installPath) Then
                req.ErrorResult = Results.NotFound(New With {.error = "Install path not found"})
                Return req
            End If

            req.AbsoluteInstallPath = Path.GetFullPath(installPath).
                TrimEnd(Path.DirectorySeparatorChar)

            Dim candidate As String
            Try
                candidate = Path.GetFullPath(Path.Combine(req.AbsoluteInstallPath, relPath))
            Catch ex As Exception
                req.ErrorResult = Results.BadRequest(New With {.error = "Invalid path"})
                Return req
            End Try

            ' Path-traversal floor: the resolved path must equal or
            ' sit beneath the install directory. Trailing-separator
            ' guard prevents "C:\Foo" matching as a prefix of
            ' "C:\FooEvil".
            Dim cmp = If(OperatingSystem.IsWindows(),
                         StringComparison.OrdinalIgnoreCase,
                         StringComparison.Ordinal)
            Dim installPrefix = req.AbsoluteInstallPath & Path.DirectorySeparatorChar
            If Not (candidate.Equals(req.AbsoluteInstallPath, cmp) OrElse
                    candidate.StartsWith(installPrefix, cmp)) Then
                req.ErrorResult = Results.BadRequest(
                    New With {.error = "Path escapes install directory"})
                Return req
            End If

            req.AbsolutePath = candidate
            req.RelativeFromInstall = candidate.Substring(req.AbsoluteInstallPath.Length).
                TrimStart(Path.DirectorySeparatorChar)

            ' Whitelist root check: at least one of the manager-
            ' supplied allowedRoots must be a prefix of the resolved
            ' relative path. ; separates roots; / and \ both
            ' accepted as the in-root path separator.
            Dim roots = allowedRoots.Split(";"c,
                StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries)
            If roots.Length = 0 Then
                req.ErrorResult = Results.BadRequest(New With {.error = "allowedRoots is required"})
                Return req
            End If

            Dim ok = False
            For Each rawRoot In roots
                Dim root = rawRoot.Replace("/"c, Path.DirectorySeparatorChar).
                                   Replace("\"c, Path.DirectorySeparatorChar).
                                   TrimEnd(Path.DirectorySeparatorChar)
                If String.IsNullOrEmpty(root) Then Continue For
                If req.RelativeFromInstall.Equals(root, cmp) OrElse
                   req.RelativeFromInstall.StartsWith(root & Path.DirectorySeparatorChar, cmp) Then
                    ok = True
                    Exit For
                End If
            Next
            If Not ok Then
                req.ErrorResult = Results.BadRequest(
                    New With {.error = "Path is not under any allowed root directory"})
                Return req
            End If

            ' Extension allowlist. Each entry is normalised to start
            ' with a leading dot for a consistent comparison against
            ' Path.GetExtension's output.
            If Not String.IsNullOrWhiteSpace(allowedExts) Then
                Dim parts = allowedExts.Split(","c,
                    StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries)
                For Each rawExt In parts
                    Dim e = rawExt.Trim()
                    If e.Length = 0 Then Continue For
                    If Not e.StartsWith(".") Then e = "." & e
                    req.AllowedExtensions.Add(e)
                Next
            End If

            If requireFile AndAlso req.AllowedExtensions.Count > 0 Then
                Dim ext = Path.GetExtension(candidate)
                If String.IsNullOrEmpty(ext) OrElse Not req.AllowedExtensions.Contains(ext) Then
                    req.ErrorResult = Results.BadRequest(
                        New With {.error = "File extension is not allowed"})
                    Return req
                End If
            End If

            Return req
        End Function

    End Module

End Namespace
