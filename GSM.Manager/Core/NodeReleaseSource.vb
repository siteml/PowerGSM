Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  NodeReleaseSource — Phase 8-2 slice 7-source-b
'
'  Sources a node binary from the GitHub release feed so the operator can
'  push "latest" to a node without hand-picking a file. For a target
'  platform + release tag it finds the per-platform node zip
'  (PowerGSM-Node-{ver}-{rid}.zip), downloads it, verifies it against the
'  release SHA256SUMS, extracts the inner GSM.Node[.exe], and returns the
'  local path — ready to feed the same StageBinaryAsync push the manual
'  file path uses.
'
'  Trust chain: release SHA256SUMS -> verified zip -> extracted binary ->
'  push (StageBinaryAsync re-SHAs the binary on the wire) -> node commit
'  re-verify. The checksum covers the zip; the extracted binary rides the
'  existing push integrity.
'
'  The node zip also carries GSM.Shim/ and GSM.NodeSetup, so the later
'  shim (7b) / NodeSetup (7c) co-updates source from the same download.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>Outcome of sourcing a node binary from the release feed.</summary>
    Public Class NodeSourceResult
        Public Property Success As Boolean
        Public Property Canceled As Boolean
        Public Property BinaryPath As String
        Public Property Version As String
        Public Property ErrorMessage As String
    End Class

    Public Class NodeReleaseSource

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger(Of NodeReleaseSource)
        Private ReadOnly _http As HttpClient

        ' One download shared across same-platform nodes in a batch:
        ' (cleanVersion|rid) -> extracted binary path. Guarded by _gate so
        ' two concurrent sources for the same key don't double-fetch.
        Private ReadOnly _cache As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _gate As New SemaphoreSlim(1, 1)

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of NodeReleaseSource))
            _serviceProvider = serviceProvider
            _logger = logger
            ' Infinite timeout: a node zip is tens of MB; cancellation is the
            ' token, not a wall-clock timeout (mirrors UpdateOrchestrator).
            _http = New HttpClient() With {.Timeout = Timeout.InfiniteTimeSpan}
            Try
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("PowerGSM")
            Catch
            End Try
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json")
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
        End Sub

        ''' <summary>
        ''' Source the node binary for <paramref name="platform"/> at the
        ''' release <paramref name="tag"/>. Cached per (version, rid) so a
        ''' batch of same-platform nodes downloads once. Never throws on a
        ''' sourcing failure — the outcome (incl. cancellation) is on the
        ''' result. Reports download / verify / extract progress.
        ''' </summary>
        Public Async Function SourceAsync(platform As NodePlatform, tag As String, target As String,
                                          progress As IProgress(Of StageProgress),
                                          token As CancellationToken) As Task(Of NodeSourceResult)
            Dim result As New NodeSourceResult()
            Dim rid = RidFor(platform)
            If rid Is Nothing Then
                result.ErrorMessage = "Can't source a binary for an unknown-platform node."
                Return result
            End If
            If String.IsNullOrWhiteSpace(tag) Then
                result.ErrorMessage = "No release tag to source from."
                Return result
            End If

            Dim version = StripLeadingV(tag)
            result.Version = version
            Dim normTarget = If(String.IsNullOrWhiteSpace(target), "node", target.Trim().ToLowerInvariant())
            Dim key = $"{version}|{rid}|{normTarget}"

            Try
                Await _gate.WaitAsync(token)
            Catch ex As OperationCanceledException
                result.Canceled = True
                Return result
            End Try

            Dim ridDirPath = RidDir(version, rid)
            Try
                ' Cache hit: reuse the already-extracted binary.
                Dim cached As String = Nothing
                If _cache.TryGetValue(key, cached) AndAlso Not String.IsNullOrEmpty(cached) AndAlso File.Exists(cached) Then
                    result.Success = True
                    result.BinaryPath = cached
                    Return result
                End If

                Dim exDir = Path.Combine(ridDirPath, "extracted")

                ' Reuse an already-extracted zip for this (version, rid): node,
                ' shim, and nodesetup all ride the SAME node zip, so if a sibling
                ' target already downloaded + extracted it, just locate our binary
                ' inside (no second download, no destructive re-extract).
                Dim binPath As String = Nothing
                If Directory.Exists(exDir) Then
                    binPath = LocateTargetBinary(exDir, normTarget, platform)
                End If

                If String.IsNullOrEmpty(binPath) OrElse Not File.Exists(binPath) Then
                    Dim source = ReadSource()
                    Dim zipName = $"PowerGSM-Node-{version}-{rid}.zip"

                    ' 1) Resolve assets + URLs.
                    Dim assets = Await ReleaseAssetHelpers.FetchAssetsAsync(_http, source, tag, token)
                    Dim zipUrl = ReleaseAssetHelpers.FindAssetUrl(assets, zipName)
                    Dim sumsUrl = ReleaseAssetHelpers.FindAssetUrl(assets, "SHA256SUMS")
                    If String.IsNullOrEmpty(zipUrl) Then
                        result.ErrorMessage = $"Release {tag} has no asset named {zipName}."
                        Return result
                    End If

                    ' Fresh dir for this (version, rid).
                    If Directory.Exists(ridDirPath) Then Directory.Delete(ridDirPath, True)
                    Dim dlDir = Path.Combine(ridDirPath, "download")
                    Directory.CreateDirectory(dlDir)

                    ' 2) Fetch + parse SHA256SUMS for the zip.
                    Dim expectedHash As String = Nothing
                    If Not String.IsNullOrEmpty(sumsUrl) Then
                        Dim sumsText = Await _http.GetStringAsync(sumsUrl, token)
                        expectedHash = ReleaseAssetHelpers.ParseSumsFor(sumsText, zipName)
                    End If

                    ' 3) Download the zip with progress.
                    Dim zipPath = Path.Combine(dlDir, zipName)
                    Await ReleaseAssetHelpers.DownloadFileAsync(_http, zipUrl, zipPath, progress, token)

                    ' 4) Verify the zip against the release checksum.
                    progress?.Report(New StageProgress With {.Phase = "Verifying", .TotalBytes = -1})
                    If Not String.IsNullOrEmpty(expectedHash) Then
                        Dim actual = ReleaseAssetHelpers.ComputeSha256(zipPath)
                        If Not String.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase) Then
                            TryDelete(ridDirPath)
                            result.ErrorMessage = "Checksum mismatch — the node download may be corrupt or tampered with."
                            Return result
                        End If
                        _logger.LogInformation("NodeReleaseSource: SHA256 verified for {Zip}", zipName)
                    Else
                        _logger.LogWarning(
                            "NodeReleaseSource: no SHA256SUMS entry for {Zip}; proceeding WITHOUT checksum verification", zipName)
                    End If

                    ' 5) Extract + locate the requested target binary.
                    token.ThrowIfCancellationRequested()
                    progress?.Report(New StageProgress With {.Phase = "Extracting", .TotalBytes = -1})
                    Directory.CreateDirectory(exDir)
                    ZipFile.ExtractToDirectory(zipPath, exDir, overwriteFiles:=True)

                    binPath = LocateTargetBinary(exDir, normTarget, platform)
                    If String.IsNullOrEmpty(binPath) OrElse Not File.Exists(binPath) Then
                        TryDelete(ridDirPath)
                        result.ErrorMessage = $"The {rid} node zip doesn't contain the {normTarget} binary."
                        Return result
                    End If
                End If

                _cache(key) = binPath
                result.Success = True
                result.BinaryPath = binPath
                _logger.LogInformation("NodeReleaseSource: sourced {Rid} {Target} {Version} at {Path}", rid, normTarget, version, binPath)
                Return result

            Catch ex As OperationCanceledException
                result.Canceled = True
                TryDelete(ridDirPath)
                Return result
            Catch ex As Exception
                _logger.LogWarning(ex, "NodeReleaseSource: sourcing {Rid} node {Version} failed", rid, version)
                result.ErrorMessage = ex.Message
                TryDelete(ridDirPath)
                Return result
            Finally
                _gate.Release()
            End Try
        End Function

        ' ---- helpers ----

        Private Shared Function RidFor(platform As NodePlatform) As String
            Select Case platform
                Case NodePlatform.Windows
                    Return "win-x64"
                Case NodePlatform.Linux
                    Return "linux-x64"
                Case Else
                    Return Nothing
            End Select
        End Function

        ''' <summary>
        ''' Locates the requested target's binary inside an extracted node zip:
        '''   node      -> GSM.Node[.exe]       at the zip root
        '''   nodesetup -> GSM.NodeSetup[.exe]  at the zip root
        '''   shim      -> GSM.Shim\&lt;ver&gt;\GSM.Shim[.exe] (the versioned
        '''                folder the publish drops it into; pick the one that
        '''                actually holds the exe). Returns Nothing if absent.
        ''' </summary>
        Private Shared Function LocateTargetBinary(exDir As String, target As String, platform As NodePlatform) As String
            Dim isWin = (platform = NodePlatform.Windows)
            Select Case target
                Case "shim"
                    Dim exe = If(isWin, "GSM.Shim.exe", "GSM.Shim")
                    Dim shimRoot = Path.Combine(exDir, "GSM.Shim")
                    If Directory.Exists(shimRoot) Then
                        For Each d In Directory.GetDirectories(shimRoot)
                            Dim cand = Path.Combine(d, exe)
                            If File.Exists(cand) Then Return cand
                        Next
                        Dim flat = Path.Combine(shimRoot, exe)
                        If File.Exists(flat) Then Return flat
                    End If
                    Return Nothing
                Case "nodesetup"
                    Dim p = Path.Combine(exDir, If(isWin, "GSM.NodeSetup.exe", "GSM.NodeSetup"))
                    Return If(File.Exists(p), p, Nothing)
                Case Else
                    Dim p = Path.Combine(exDir, If(isWin, "GSM.Node.exe", "GSM.Node"))
                    Return If(File.Exists(p), p, Nothing)
            End Select
        End Function

        Private Shared Function StripLeadingV(tag As String) As String
            Dim t = If(tag, "").Trim()
            If t.StartsWith("v", StringComparison.OrdinalIgnoreCase) Then t = t.Substring(1)
            Return t
        End Function

        Private Shared Function CacheRoot() As String
            Return Path.Combine(InstallEnvironment.InstallDirectory(), ".node-updates")
        End Function

        Private Shared Function RidDir(version As String, rid As String) As String
            Return Path.Combine(CacheRoot(), version, rid)
        End Function

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

    End Class

End Namespace
