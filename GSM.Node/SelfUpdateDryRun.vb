' ============================================================
'  GSM.Node — --self-update-dry-run harness (Phase 8-2, slice 6d)
'
'  Exercises the slice-6 self-update end to end without the Manager and
'  without any hand-driven HTTP. Two modes:
'
'    GSM.Node --self-update-dry-run --stage-only
'        Stages the *currently-running* binary as GSM.Node.new beside itself,
'        driving the real chunked staging path in-process (Begin -> AppendChunk*
'        -> Commit, including SHA-256 + size verify and the atomic .part->.new
'        rename). Stops there. Apply it by restarting the node: under systemd
'        the ExecStartPre swap moves .new into place on the next start; a bare
'        node picks it up via NodeSetup on its next self-update, or you can just
'        re-launch the swapped binary.
'
'    GSM.Node --self-update-dry-run
'        Same staging, then POSTs apply-update to the *already-running* node on
'        loopback (reading port + token from nodesettings.json), triggering the
'        real graceful update-exit -> survivor swap -> relaunch -> re-adopt.
'
'  The "newer" payload is the live binary itself — byte-identical is fine here:
'  slice 6 proves the swap/relaunch/re-adopt mechanics, not version detection
'  (that's slice 7). Results go to console and self-update-dryrun-result.txt
'  beside the binary, matching the other --*-test harnesses.
' ============================================================
Imports System
Imports System.IO
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging.Abstractions

Namespace GSM.Node

    Friend Module SelfUpdateDryRun

        ' 4 MB chunks so a normal node publish takes a handful of AppendChunk
        ' calls — enough to exercise the multi-chunk offset path, not just a
        ' single-shot body.
        Private Const ChunkSize As Integer = 4 * 1024 * 1024

        Private ReadOnly _transcript As New StringBuilder()

        Public Async Function RunAsync(stageOnly As Boolean) As Task(Of Integer)
            Report($"[self-update-dry-run] starting (OS={OsName()} stageOnly={stageOnly})")

            Dim live As String = NodeLivePath()
            If Not File.Exists(live) Then
                Report($"[self-update-dry-run] FAIL: live binary not found at {live}")
                Flush(1)
                Return 1
            End If

            Dim newPath As String = live & ".new"
            Dim size As Long = New FileInfo(live).Length
            Dim sha As String

            Try
                sha = Await Sha256HexAsync(live, CancellationToken.None)
            Catch ex As Exception
                Report($"[self-update-dry-run] FAIL: could not hash live binary: {ex.Message}")
                Flush(1)
                Return 1
            End Try

            Report($"[self-update-dry-run] live={live}")
            Report($"[self-update-dry-run] size={size} sha256={sha}")

            ' ---- Stage via the real chunked path ----
            Dim svc As New SelfUpdateService(NullLogger(Of SelfUpdateService).Instance)

            Dim begin = svc.Begin(New StageBeginRequest With {
                .TargetName = "node",
                .TotalBytes = size,
                .Sha256 = sha,
                .Version = "dry-run"})
            If begin.Code <> 200 OrElse String.IsNullOrEmpty(begin.UploadId) Then
                Report($"[self-update-dry-run] FAIL: begin returned {begin.Code}")
                Flush(1)
                Return 1
            End If
            Dim uploadId As String = begin.UploadId
            Report($"[self-update-dry-run] begin ok: uploadId={uploadId}")

            Dim chunks As Integer = 0
            Try
                Using src As New FileStream(live, FileMode.Open, FileAccess.Read,
                                            FileShare.Read, 1024 * 1024, useAsync:=True)
                    Dim buffer(ChunkSize - 1) As Byte
                    Dim offset As Long = 0
                    Do
                        Dim n As Integer = Await src.ReadAsync(buffer.AsMemory(0, buffer.Length), CancellationToken.None)
                        If n <= 0 Then Exit Do
                        Using chunkStream As New MemoryStream(buffer, 0, n, writable:=False)
                            Dim cr = Await svc.AppendChunkAsync(uploadId, offset, chunkStream, CancellationToken.None)
                            If cr.Code <> 200 Then
                                Report($"[self-update-dry-run] FAIL: chunk at offset {offset} returned {cr.Code}")
                                Flush(1)
                                Return 1
                            End If
                        End Using
                        offset += n
                        chunks += 1
                    Loop
                End Using
            Catch ex As Exception
                Report($"[self-update-dry-run] FAIL: chunk upload threw: {ex.Message}")
                Flush(1)
                Return 1
            End Try
            Report($"[self-update-dry-run] uploaded {chunks} chunk(s)")

            Dim commit = Await svc.CommitAsync(uploadId, CancellationToken.None)
            If commit.Code <> 200 Then
                Report($"[self-update-dry-run] FAIL: commit returned {commit.Code} (sha/size verify failed?)")
                Flush(1)
                Return 1
            End If

            ' ---- Assert the staged file landed ----
            If Not File.Exists(newPath) Then
                Report($"[self-update-dry-run] FAIL: commit ok but {Path.GetFileName(newPath)} missing")
                Flush(1)
                Return 1
            End If
            Dim stagedSize As Long = New FileInfo(newPath).Length
            If stagedSize <> size Then
                Report($"[self-update-dry-run] FAIL: staged size {stagedSize} <> {size}")
                Flush(1)
                Return 1
            End If
            Report($"[self-update-dry-run] staged ok: {newPath} ({stagedSize} bytes)")

            If stageOnly Then
                Report("[self-update-dry-run] PASS (stage-only).")
                Report("[self-update-dry-run] To apply: restart the node. Under systemd the")
                Report("[self-update-dry-run] ExecStartPre swap moves .new into place on the next")
                Report("[self-update-dry-run] start (previous kept as .old).")
                Flush(0)
                Return 0
            End If

            ' ---- Trigger the real update-exit on the running node ----
            Dim triggered = Await TriggerApplyUpdateAsync()
            If Not triggered.Ok Then
                Report($"[self-update-dry-run] FAIL: apply-update trigger: {triggered.Detail}")
                Flush(1)
                Return 1
            End If
            Report($"[self-update-dry-run] apply-update accepted: {triggered.Detail}")
            Report("[self-update-dry-run] PASS. The running node will now detach shims, exit,")
            Report("[self-update-dry-run] swap .new into place, relaunch, and re-adopt instances.")
            Report("[self-update-dry-run] Watch the node log / journal and confirm game PIDs are")
            Report("[self-update-dry-run] unchanged across the bounce.")
            Flush(0)
            Return 0
        End Function

        ''' <summary>
        ''' POSTs apply-update to the running node on loopback, reading the port
        ''' and bearer token from nodesettings.json beside the binary.
        ''' </summary>
        Private Async Function TriggerApplyUpdateAsync() As Task(Of (Ok As Boolean, Detail As String))
            Dim port As Integer
            Dim token As String = Nothing
            Try
                Dim cfgPath = Path.Combine(AppContext.BaseDirectory, "nodesettings.json")
                Using doc = JsonDocument.Parse(File.ReadAllText(cfgPath))
                    Dim nodeEl As JsonElement
                    If Not doc.RootElement.TryGetProperty("Node", nodeEl) Then
                        Return (False, "nodesettings.json has no Node section")
                    End If
                    Dim portEl As JsonElement
                    port = If(nodeEl.TryGetProperty("ListenPort", portEl), portEl.GetInt32(), 8765)
                    Dim tokEl As JsonElement
                    If nodeEl.TryGetProperty("AuthToken", tokEl) Then token = tokEl.GetString()
                End Using
            Catch ex As Exception
                Return (False, "could not read nodesettings.json: " & ex.Message)
            End Try

            If String.IsNullOrEmpty(token) Then
                Return (False, "no AuthToken in nodesettings.json")
            End If

            Try
                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(15)
                    client.DefaultRequestHeaders.Authorization = New AuthenticationHeaderValue("Bearer", token)
                    Dim url = $"http://127.0.0.1:{port}/api/system/apply-update"
                    Using content As New StringContent("", Encoding.UTF8, "application/json")
                        Dim resp = Await client.PostAsync(url, content)
                        Dim body = Await resp.Content.ReadAsStringAsync()
                        Dim code = CInt(resp.StatusCode)
                        If code = 202 Then
                            Return (True, $"HTTP {code} {body}")
                        End If
                        Return (False, $"HTTP {code} {body}")
                    End Using
                End Using
            Catch ex As Exception
                Return (False, "POST failed (is the node running?): " & ex.Message)
            End Try
        End Function

        Private Function NodeLivePath() As String
            Dim exeName = If(OperatingSystem.IsWindows(), "GSM.Node.exe", "GSM.Node")
            Return Path.Combine(AppContext.BaseDirectory, exeName)
        End Function

        Private Async Function Sha256HexAsync(path As String, ct As CancellationToken) As Task(Of String)
            Using sha = SHA256.Create()
                Using fs As New FileStream(path, FileMode.Open, FileAccess.Read,
                                           FileShare.Read, 1024 * 1024, useAsync:=True)
                    Dim hash = Await sha.ComputeHashAsync(fs, ct)
                    Return Convert.ToHexString(hash).ToLowerInvariant()
                End Using
            End Using
        End Function

        Private Sub Report(line As String)
            _transcript.AppendLine(line)
            Try
                Console.WriteLine(line)
            Catch
                ' No console attached (WinExe launched without a parent console);
                ' the result file is the reliable channel.
            End Try
        End Sub

        Private Sub Flush(resultCode As Integer)
            Try
                Dim resultPath As String = Path.Combine(AppContext.BaseDirectory, "self-update-dryrun-result.txt")
                _transcript.AppendLine($"[self-update-dry-run] result code = {resultCode}")
                File.WriteAllText(resultPath, _transcript.ToString())
                Try
                    Console.WriteLine($"[self-update-dry-run] transcript written to {resultPath}")
                Catch
                End Try
            Catch
                ' best-effort
            End Try
        End Sub

        Private Function OsName() As String
            If OperatingSystem.IsWindows() Then Return "Windows"
            If OperatingSystem.IsLinux() Then Return "Linux"
            Return "other"
        End Function

    End Module

End Namespace
