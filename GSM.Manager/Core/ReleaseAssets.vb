Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports System.Threading.Tasks

' ============================================================
'  ReleaseAssets — shared GitHub release-asset plumbing
'
'  Phase 8-2 slice 7-source-b extracted these from UpdateOrchestrator
'  so both the Manager's own self-update (UpdateOrchestrator, Phase 5l)
'  and node binary sourcing (NodeReleaseSource) share one copy of the
'  fetch / find / verify / download primitives. Pure relocation — no
'  behaviour change from the originals.
'
'  All members are static and take the HttpClient as a parameter, so
'  each caller keeps its own client (timeout + header policy).
' ============================================================

Namespace GSM.Manager.Core

    ' ---- minimal release-assets DTOs (the checker's GitHubRelease omits assets) ----

    Friend Class ReleaseWithAssets
        <JsonPropertyName("assets")>
        Public Property Assets As List(Of ReleaseAsset)
    End Class

    Friend Class ReleaseAsset
        <JsonPropertyName("name")>
        Public Property Name As String
        <JsonPropertyName("browser_download_url")>
        Public Property BrowserDownloadUrl As String
    End Class

    ''' <summary>
    ''' Static GitHub release-asset helpers shared by UpdateOrchestrator
    ''' (Manager self-update) and NodeReleaseSource (node binary sourcing):
    ''' fetch a release's assets by tag, find an asset URL by name, parse a
    ''' SHA256SUMS entry, hash a file, and stream a download with progress.
    ''' </summary>
    Friend Module ReleaseAssetHelpers

        ''' <summary>
        ''' Fetch a release by tag (/releases/tags/{tag}) and return its
        ''' assets. The checker's GitHubRelease DTO omits assets, so callers
        ''' that need download URLs come through here.
        ''' </summary>
        Friend Async Function FetchAssetsAsync(http As HttpClient, source As String, tag As String,
                                               token As CancellationToken) As Task(Of List(Of ReleaseAsset))
            Dim url = $"https://api.github.com/repos/{source}/releases/tags/{Uri.EscapeDataString(tag)}"
            Using resp = Await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                If Not resp.IsSuccessStatusCode Then
                    Throw New InvalidOperationException(
                        $"GitHub API returned {CInt(resp.StatusCode)} {resp.ReasonPhrase} for release {tag}.")
                End If
                Dim json = Await resp.Content.ReadAsStringAsync(token)
                Dim opts As New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}
                Dim rel = JsonSerializer.Deserialize(Of ReleaseWithAssets)(json, opts)
                Return If(rel?.Assets, New List(Of ReleaseAsset)())
            End Using
        End Function

        ''' <summary>Browser download URL for the named asset, or Nothing.</summary>
        Friend Function FindAssetUrl(assets As List(Of ReleaseAsset), name As String) As String
            For Each a In assets
                If a IsNot Nothing AndAlso String.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase) Then
                    Return a.BrowserDownloadUrl
                End If
            Next
            Return Nothing
        End Function

        ''' <summary>
        ''' Find the hash for <paramref name="fileName"/> in a
        ''' `sha256sum`-format file ("&lt;hash&gt;  &lt;name&gt;" or
        ''' "&lt;hash&gt; *&lt;name&gt;"). Returns Nothing when absent.
        ''' </summary>
        Friend Function ParseSumsFor(sumsText As String, fileName As String) As String
            If String.IsNullOrEmpty(sumsText) Then Return Nothing
            For Each line In sumsText.Replace(vbCrLf, vbLf).Split(ChrW(10))
                Dim t = line.Trim()
                If t.Length = 0 Then Continue For
                Dim parts = t.Split(New Char() {" "c, ChrW(9)}, 2, StringSplitOptions.RemoveEmptyEntries)
                If parts.Length = 2 Then
                    Dim nm = parts(1).TrimStart("*"c, " "c)
                    If String.Equals(nm, fileName, StringComparison.OrdinalIgnoreCase) Then
                        Return parts(0).Trim()
                    End If
                End If
            Next
            Return Nothing
        End Function

        ''' <summary>Lowercase hex SHA-256 of a file's contents.</summary>
        Friend Function ComputeSha256(path As String) As String
            Using fs = File.OpenRead(path)
                Return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant()
            End Using
        End Function

        ''' <summary>
        ''' Stream a download to <paramref name="destPath"/>, reporting
        ''' coarse byte progress through <paramref name="progress"/> (reuses
        ''' the StageProgress shape). Honors the cancellation token.
        ''' </summary>
        Friend Async Function DownloadFileAsync(http As HttpClient, url As String, destPath As String,
                                                progress As IProgress(Of StageProgress),
                                                token As CancellationToken) As Task
            Using resp = Await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                resp.EnsureSuccessStatusCode()
                Dim total = If(resp.Content.Headers.ContentLength.HasValue, resp.Content.Headers.ContentLength.Value, -1L)
                Using src = Await resp.Content.ReadAsStreamAsync(token)
                    Using dst As New FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync:=True)
                        Dim buffer(81919) As Byte
                        Dim received As Long = 0
                        Dim lastReport As Long = 0
                        While True
                            Dim n = Await src.ReadAsync(buffer, 0, buffer.Length, token)
                            If n = 0 Then Exit While
                            Await dst.WriteAsync(buffer, 0, n, token)
                            received += n
                            If progress IsNot Nothing AndAlso (received - lastReport) >= 65536 Then
                                lastReport = received
                                progress.Report(New StageProgress With {
                                    .Phase = "Downloading", .BytesReceived = received, .TotalBytes = total})
                            End If
                        End While
                        progress?.Report(New StageProgress With {
                            .Phase = "Downloading", .BytesReceived = received, .TotalBytes = total})
                    End Using
                End Using
            End Using
        End Function

    End Module

End Namespace
