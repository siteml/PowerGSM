Imports System
Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin
Imports GSM.Node.Api
Imports Microsoft.Extensions.Logging

' ============================================================
'  InstallRunner — executes installation/update operations
'
'  Receives a list of InstallStep from the manager (resolved by
'  the plugin on the manager side) and executes them sequentially.
'
'  Supported step types:
'    SteamCmdStep     — run SteamCMD with app ID, branch, creds
'    DownloadFileStep — HTTP download, optional archive extraction
'    CopyFileStep     — copy/move files within install dir
'    WriteFileStep    — write text content to a file
'    RunProcessStep   — run an arbitrary process with timeout
'
'  Long-running operations report progress. The manager polls
'  for status via GET /api/install/{id}/progress.
' ============================================================

Namespace GSM.Node

    Public Class InstallRunner

        Private ReadOnly _operations As New ConcurrentDictionary(Of String, ActiveOperation)
        Private ReadOnly _database As NodeDatabase
        Private ReadOnly _config As NodeConfiguration
        Private ReadOnly _logger As Microsoft.Extensions.Logging.ILogger(Of InstallRunner)
        Private ReadOnly _httpClient As New HttpClient(New HttpClientHandler With {
            .AllowAutoRedirect = True
        })
        Private _runningCount As Integer = 0
        Private _steamCmdSawSuccess As Boolean
        Private _steamCmdCurrentOp As ActiveOperation

        Public Sub New(database As NodeDatabase,
                       config As NodeConfiguration,
                       logger As Microsoft.Extensions.Logging.ILogger(Of InstallRunner))
            _database = database
            _config = config
            _logger = logger
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PowerGSM/1.0")
        End Sub

        ''' <summary>
        ''' Starts an install/update operation. Returns immediately
        ''' with the initial progress state.
        ''' </summary>
        Public Function StartInstall(request As InstallRequest) As InstallProgressResponse

            If _runningCount >= _config.MaxConcurrentInstalls Then
                Return New InstallProgressResponse With {
                    .InstallationId = request.InstallationId,
                    .OperationState = InstallationOperationState.Failed,
                    .ErrorMessage = $"Max concurrent installs ({_config.MaxConcurrentInstalls}) reached"
                }
            End If

            Dim op As New ActiveOperation()
            op.InstallationId = request.InstallationId
            op.GameId = request.GameId
            op.InstallPath = request.InstallPath
            op.Steps = If(request.Steps, New List(Of InstallStep))
            op.SteamCredentials = request.SteamCredentials
            op.State = InstallationOperationState.Queued
            op.StartedAt = DateTime.UtcNow
            op.TotalSteps = op.Steps.Count
            op.CancellationSource = New CancellationTokenSource()

            _operations(request.InstallationId) = op

            ' Run in background
            Task.Run(Function() ExecuteOperationAsync(op))

            Return BuildProgress(op)
        End Function

        ''' <summary>
        ''' Returns the current progress of an operation.
        ''' </summary>
        Public Function GetProgress(installationId As String) As InstallProgressResponse
            Dim op As ActiveOperation = Nothing
            If Not _operations.TryGetValue(installationId, op) Then
                Return New InstallProgressResponse With {
                    .InstallationId = installationId,
                    .OperationState = InstallationOperationState.Failed,
                    .ErrorMessage = "No operation found for this installation"
                }
            End If
            Return BuildProgress(op)
        End Function

        ''' <summary>
        ''' Cancels a running operation.
        ''' </summary>
        Public Function CancelInstall(installationId As String) As Boolean
            Dim op As ActiveOperation = Nothing
            If Not _operations.TryGetValue(installationId, op) Then
                Return False
            End If
            op.CancellationSource.Cancel()
            op.State = InstallationOperationState.Cancelled
            Return True
        End Function

        ''' <summary>
        ''' Provides input to a waiting operation (e.g. Steam Guard code).
        ''' </summary>
        Public Function ProvideInput(installationId As String, value As String) As Boolean
            Dim op As ActiveOperation = Nothing
            If Not _operations.TryGetValue(installationId, op) Then
                Return False
            End If

            ' Signal the waiting task with the user's input
            ' (the retry loop will relaunch SteamCMD with +set_steam_guard_code)
            If op.PendingInputTcs IsNot Nothing Then
                _logger.LogInformation("Received input for {Id}", installationId)
                op.PendingInputTcs.TrySetResult(value)
                Return True
            End If
            Return False
        End Function

        ' ============================================================
        '  Step execution
        ' ============================================================

        Private Async Function ExecuteOperationAsync(op As ActiveOperation) As Task
            Interlocked.Increment(_runningCount)
            Try
                Directory.CreateDirectory(op.InstallPath)
                op.State = InstallationOperationState.Downloading

                For i = 0 To op.Steps.Count - 1
                    If op.CancellationSource.IsCancellationRequested Then
                        op.State = InstallationOperationState.Cancelled
                        Return
                    End If

                    op.CurrentStepIndex = i
                    op.CurrentStepName = If(op.Steps(i).StepName, $"Step {i + 1}")
                    op.ProgressPercent = CDbl(i) / CDbl(op.TotalSteps) * 100.0

                    _logger.LogInformation("Install {Id}: step {Step}/{Total} - {Name}",
                                           op.InstallationId, i + 1, op.TotalSteps, op.CurrentStepName)

                    Await ExecuteStepAsync(op, op.Steps(i))
                Next

                op.State = InstallationOperationState.Completed
                op.ProgressPercent = 100
                op.CompletedAt = DateTime.UtcNow

                _database.RecordInstallHistory(op.InstallationId, op.GameId,
                                               op.StartedAt, op.CompletedAt.Value,
                                               True, op.TotalSteps, Nothing)

                _logger.LogInformation("Install {Id}: completed successfully", op.InstallationId)

            Catch ex As OperationCanceledException
                op.State = InstallationOperationState.Cancelled
                _logger.LogInformation("Install {Id}: cancelled", op.InstallationId)

            Catch ex As Exception
                op.State = InstallationOperationState.Failed
                op.ErrorMessage = ex.Message
                op.CompletedAt = DateTime.UtcNow

                _database.RecordInstallHistory(op.InstallationId, op.GameId,
                                               op.StartedAt, op.CompletedAt.Value,
                                               False, op.TotalSteps, ex.Message)

                _logger.LogError(ex, "Install {Id}: failed", op.InstallationId)
            Finally
                Interlocked.Decrement(_runningCount)
            End Try
        End Function

        Private Async Function ExecuteStepAsync(op As ActiveOperation,
                                                 currentStep As InstallStep) As Task
            Dim token = op.CancellationSource.Token

            If TypeOf currentStep Is SteamCmdStep Then
                Await ExecuteSteamCmdStepAsync(op, DirectCast(currentStep, SteamCmdStep), token)

            ElseIf TypeOf currentStep Is DownloadFileStep Then
                Await ExecuteDownloadStepAsync(op, DirectCast(currentStep, DownloadFileStep), token)

            ElseIf TypeOf currentStep Is CopyFileStep Then
                ExecuteCopyStep(op, DirectCast(currentStep, CopyFileStep))

            ElseIf TypeOf currentStep Is WriteFileStep Then
                ExecuteWriteFileStep(op, DirectCast(currentStep, WriteFileStep))

            ElseIf TypeOf currentStep Is RunProcessStep Then
                Await ExecuteRunProcessStepAsync(op, DirectCast(currentStep, RunProcessStep), token)

            Else
                Throw New NotSupportedException($"Unknown install step type: {currentStep.GetType().Name}")
            End If
        End Function

        Private Async Function ExecuteSteamCmdStepAsync(op As ActiveOperation,
                                                         steamStep As SteamCmdStep,
                                                         cancellation As CancellationToken) As Task
            op.State = InstallationOperationState.Downloading

            ' Find or download SteamCMD
            Dim steamCmdPath = Await FindOrDownloadSteamCmdAsync(cancellation)
            Dim steamCmdDir = Path.GetFullPath(Path.GetDirectoryName(steamCmdPath))

            ' Self-update SteamCMD first (no redirection, loop until code 0)
            Await RunSteamCmdSelfUpdateAsync(steamCmdPath, steamCmdDir, cancellation)
            KillSteamProcesses()
            Await Task.Delay(3000, cancellation)

            ' Pre-create steamapps directory in install path (SteamCMD fix)
            Dim installPath = Path.GetFullPath(op.InstallPath).TrimEnd("\"c, "/"c)
            Directory.CreateDirectory(installPath)
            Directory.CreateDirectory(Path.Combine(installPath, "steamapps"))

            ' Build command-line arguments
            Dim args As New StringBuilder()
            args.Append("+force_install_dir ")
            args.Append(QuoteForSteamCmd(installPath))

            If steamStep.RequiresLogin AndAlso op.SteamCredentials IsNot Nothing AndAlso
               Not op.SteamCredentials.IsAnonymous Then
                args.Append(" +login ")
                args.Append(QuoteForSteamCmd(op.SteamCredentials.Username))
                args.Append(" ")
                args.Append(QuoteForSteamCmd(op.SteamCredentials.Password))
            Else
                args.Append(" +login anonymous")
            End If

            args.Append(" +app_update ")
            args.Append(steamStep.AppId.ToString())
            If Not String.IsNullOrEmpty(steamStep.BetaBranch) Then
                args.Append(" -beta ")
                args.Append(QuoteForSteamCmd(steamStep.BetaBranch))
                If Not String.IsNullOrEmpty(steamStep.BetaPassword) Then
                    args.Append(" -betapassword ")
                    args.Append(QuoteForSteamCmd(steamStep.BetaPassword))
                End If
            End If
            If steamStep.ValidateFiles Then
                args.Append(" validate")
            End If
            args.Append(" +quit")

            _logger.LogInformation("SteamCMD starting install for AppID {AppId}", steamStep.AppId)

            _steamCmdSawSuccess = False
            _steamCmdCurrentOp = op
            Dim guardCode As String = Nothing
            Dim maxRetries = 3
            For attempt = 1 To maxRetries
                ' Build final argument string, prepending guard code if we have one
                Dim finalArgs As String
                If Not String.IsNullOrEmpty(guardCode) Then
                    finalArgs = $"+set_steam_guard_code {guardCode} " & args.ToString()
                Else
                    finalArgs = args.ToString()
                End If

                Dim exitCode = Await RunSteamCmdProcessAsync(
                    steamCmdPath, steamCmdDir, finalArgs, op, cancellation)

                _logger.LogInformation("SteamCMD exited with code {Code} (attempt {Attempt})",
                                       exitCode, attempt)

                If exitCode = 0 Then Return

                ' Code 7 = SteamCMD self-updated after install.
                ' If the app install succeeded, treat as success.
                If exitCode = 7 AndAlso _steamCmdSawSuccess Then
                    _logger.LogInformation("SteamCMD exited code 7 but app install succeeded")
                    Return
                End If

                If exitCode = 7 AndAlso attempt < maxRetries Then
                    _logger.LogInformation("SteamCMD exited code 7, cleaning up before retry...")
                    Await Task.Delay(3000, cancellation)
                    KillSteamProcesses()
                    Await Task.Delay(2000, cancellation)
                    Continue For
                End If

                ' Code 5 = login failure (Steam Guard / 2FA required)
                ' Ask user for the code and retry with +set_steam_guard_code
                If exitCode = 5 AndAlso attempt < maxRetries Then
                    Dim accountName = ""
                    If op.SteamCredentials IsNot Nothing Then
                        accountName = op.SteamCredentials.Username
                    End If
                    _logger.LogInformation("SteamCMD login failed (code 5) — requesting Steam Guard code for {User}", accountName)
                    op.PendingPromptType = PromptType.SteamGuardCode
                    op.PendingPromptMessage = $"Steam Guard or two-factor code required for account '{accountName}'."
                    op.State = InstallationOperationState.WaitingForInput
                    op.PendingInputTcs = New TaskCompletionSource(Of String)

                    ' Wait for user to provide the code (via Manager polling + prompt)
                    guardCode = Await op.PendingInputTcs.Task

                    op.PendingPromptType = Nothing
                    op.PendingPromptMessage = Nothing
                    op.PendingInputTcs = Nothing
                    op.State = InstallationOperationState.Downloading

                    If String.IsNullOrEmpty(guardCode) Then
                        Throw New Exception("Steam Guard code not provided — install cancelled")
                    End If

                    _logger.LogInformation("Received Steam Guard code, retrying with +set_steam_guard_code")
                    Await Task.Delay(2000, cancellation)
                    KillSteamProcesses()
                    Await Task.Delay(2000, cancellation)
                    Continue For
                End If

                Throw New Exception($"SteamCMD exited with code {exitCode} after {attempt} attempt(s)")
            Next
        End Function

        ''' <summary>
        ''' Runs a single SteamCMD process with full stream redirection
        ''' and event-based output capture. Uses class-level handlers
        ''' wired via AddressOf. Stdin stays open. Polls HasExited.
        ''' </summary>
        Private Async Function RunSteamCmdProcessAsync(steamCmdPath As String,
                                                        workingDir As String,
                                                        arguments As String,
                                                        op As ActiveOperation,
                                                        cancellation As CancellationToken) As Task(Of Integer)
            Dim proc As Process = Nothing

            Try
                Dim psi As New ProcessStartInfo()
                psi.FileName = steamCmdPath
                psi.Arguments = arguments
                psi.WorkingDirectory = workingDir
                psi.UseShellExecute = False
                psi.CreateNoWindow = True
                psi.RedirectStandardInput = True
                psi.RedirectStandardOutput = True
                psi.RedirectStandardError = True
                psi.StandardOutputEncoding = Encoding.UTF8
                psi.StandardErrorEncoding = Encoding.UTF8

                proc = New Process()
                proc.StartInfo = psi
                proc.EnableRaisingEvents = True

                AddHandler proc.OutputDataReceived, AddressOf SteamCmd_OutputDataReceived
                AddHandler proc.ErrorDataReceived, AddressOf SteamCmd_ErrorDataReceived

                proc.Start()
                proc.BeginOutputReadLine()
                proc.BeginErrorReadLine()

                op.Message = "SteamCMD running..."
                _logger.LogInformation("SteamCMD started (PID {Pid})", proc.Id)

                ' Poll for exit. Send a newline every 20 seconds —
                ' this forces SteamCMD to reject an empty Steam Guard
                ' code and exit with code 5 if it's waiting for one.
                ' Has no effect during normal download/install operations.
                Dim tickCount = 0
                While Not proc.HasExited
                    cancellation.ThrowIfCancellationRequested()
                    Await Task.Delay(1000, cancellation)
                    tickCount += 1

                    If tickCount Mod 20 = 0 Then
                        Try
                            proc.StandardInput.WriteLine("")
                            proc.StandardInput.Flush()
                        Catch
                        End Try
                    End If
                End While

                ' Check manifest file as backup success indicator
                Dim manifestMatch = Text.RegularExpressions.Regex.Match(
                    arguments, "app_update\s+(\d+)")
                If manifestMatch.Success Then
                    Dim manifestPath = Path.Combine(
                        op.InstallPath, "steamapps",
                        $"appmanifest_{manifestMatch.Groups(1).Value}.acf")
                    If File.Exists(manifestPath) Then
                        _steamCmdSawSuccess = True
                    End If
                End If

                Return proc.ExitCode

            Finally
                If proc IsNot Nothing Then
                    Try
                        RemoveHandler proc.OutputDataReceived, AddressOf SteamCmd_OutputDataReceived
                        RemoveHandler proc.ErrorDataReceived, AddressOf SteamCmd_ErrorDataReceived
                    Catch
                    End Try
                    Try
                        proc.Dispose()
                    Catch
                    End Try
                End If
                _steamCmdCurrentOp = Nothing
            End Try
        End Function

        ' ============================================================
        '  SteamCMD event handlers (class-level)
        ' ============================================================

        Private Sub SteamCmd_OutputDataReceived(sender As Object, e As DataReceivedEventArgs)
            If e.Data IsNot Nothing Then
                If _steamCmdCurrentOp IsNot Nothing Then
                    _steamCmdCurrentOp.Message = e.Data
                End If
                _logger.LogInformation("SteamCMD: {Line}", e.Data)

                If e.Data.Contains("Success! App") Then
                    _steamCmdSawSuccess = True
                End If
            End If
        End Sub

        Private Sub SteamCmd_ErrorDataReceived(sender As Object, e As DataReceivedEventArgs)
            If e.Data IsNot Nothing Then
                _logger.LogWarning("SteamCMD [ERR]: {Line}", e.Data)
            End If
        End Sub

        ''' <summary>
        ''' Runs "steamcmd +quit" in a loop until exit code 0.
        ''' No stream redirection — SteamCMD hangs during self-update
        ''' with redirected streams. Kills child processes between passes.
        ''' </summary>
        Private Async Function RunSteamCmdSelfUpdateAsync(steamCmdPath As String,
                                                           steamCmdDir As String,
                                                           cancellation As CancellationToken) As Task
            For pass = 1 To 10
                _logger.LogInformation("SteamCMD self-update pass {Pass}", pass)

                Dim psi As New ProcessStartInfo()
                psi.FileName = steamCmdPath
                psi.Arguments = "+quit"
                psi.WorkingDirectory = steamCmdDir
                psi.UseShellExecute = False
                psi.RedirectStandardOutput = False
                psi.RedirectStandardError = False
                psi.RedirectStandardInput = False
                psi.CreateNoWindow = True

                Dim proc As Process = Nothing
                Try
                    proc = New Process()
                    proc.StartInfo = psi
                    proc.EnableRaisingEvents = True
                    proc.Start()

                    ' Timeout: 10 minutes per pass
                    Dim exited = proc.WaitForExit(600000)
                    If Not exited Then
                        Try
                            proc.Kill(entireProcessTree:=True)
                        Catch
                            proc.Kill()
                        End Try
                        Throw New TimeoutException("SteamCMD self-update timed out")
                    End If

                    _logger.LogInformation("SteamCMD self-update pass {Pass} exited with code {Code}",
                                           pass, proc.ExitCode)

                    If proc.ExitCode = 0 Then
                        _logger.LogInformation("SteamCMD is fully updated")
                        Return
                    End If
                Finally
                    If proc IsNot Nothing Then
                        proc.Dispose()
                    End If
                End Try

                ' Kill any lingering Steam child processes before next pass
                Await Task.Delay(3000, cancellation)
                KillSteamProcesses()
                Await Task.Delay(2000, cancellation)
            Next

            _logger.LogWarning("SteamCMD did not reach exit code 0 after 10 passes")
        End Function

        ''' <summary>
        ''' Kills any lingering steamcmd or Steam bootstrapper processes.
        ''' </summary>
        Private Sub KillSteamProcesses()
            For Each procName In {"steamcmd", "SteamClientBootstrapper"}
                Try
                    For Each staleProc In Process.GetProcessesByName(procName)
                        Try
                            staleProc.Kill()
                            staleProc.WaitForExit(3000)
                        Catch
                        End Try
                        staleProc.Dispose()
                    Next
                Catch
                End Try
            Next
        End Sub

        Private Async Function ExecuteDownloadStepAsync(op As ActiveOperation,
                                                         dlStep As DownloadFileStep,
                                                         cancellation As CancellationToken) As Task
            op.State = InstallationOperationState.Downloading
            Dim destPath = Path.Combine(op.InstallPath, dlStep.DestinationRelativePath)
            Directory.CreateDirectory(Path.GetDirectoryName(destPath))

            _logger.LogInformation("Downloading {Url} to {Dest}", dlStep.Url, destPath)

            Using response = Await _httpClient.GetAsync(dlStep.Url,
                    HttpCompletionOption.ResponseHeadersRead, cancellation)
                _logger.LogInformation("Download response: {Status} ({Url})",
                    response.StatusCode, response.RequestMessage?.RequestUri)
                response.EnsureSuccessStatusCode()
                Using fileStream As New FileStream(destPath, FileMode.Create,
                                                    FileAccess.Write, FileShare.None)
                    Await response.Content.CopyToAsync(fileStream, cancellation)
                End Using
            End Using

            If dlStep.ExtractArchive Then
                op.State = InstallationOperationState.Extracting

                ' Validate the download is actually an archive, not an error page
                Dim fileInfo As New FileInfo(destPath)
                _logger.LogInformation("Downloaded {File} ({Size} bytes)", destPath, fileInfo.Length)
                If fileInfo.Length < 1024 Then
                    ' Likely an error page, not an archive
                    Dim content = File.ReadAllText(destPath)
                    File.Delete(destPath)
                    Throw New Exception($"Download appears to be an error page ({fileInfo.Length} bytes): {content.Substring(0, Math.Min(200, content.Length))}")
                End If

                If destPath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) OrElse
                   destPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) OrElse
                   destPath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) Then
                    ' Use tar — auto-detect compression with -xf (works on
                    ' both Linux tar and Windows bsdtar)
                    _logger.LogInformation("Extracting archive with tar: {File}", destPath)
                    Dim tarPsi As New ProcessStartInfo("tar")
                    tarPsi.Arguments = $"-xf ""{destPath}"" -C ""{op.InstallPath}"""
                    tarPsi.UseShellExecute = False
                    tarPsi.CreateNoWindow = True
                    tarPsi.RedirectStandardError = True
                    Dim tarProc = Process.Start(tarPsi)
                    Dim tarErr = tarProc.StandardError.ReadToEnd()
                    tarProc.WaitForExit(300000)
                    If tarProc.ExitCode <> 0 Then
                        If tarErr.Contains("xz") OrElse tarErr.Contains("initialize filter") Then
                            Throw New Exception(
                                "Cannot extract .tar.xz on this system — the 'xz' tool is not installed. " &
                                "This archive format is supported on Linux only. " &
                                "On Windows, use SteamCMD install method instead.")
                        End If
                        Throw New Exception($"tar extraction failed with code {tarProc.ExitCode}: {tarErr}")
                    End If
                Else
                    ' Assume zip
                    _logger.LogInformation("Extracting zip: {File}", destPath)
                    IO.Compression.ZipFile.ExtractToDirectory(destPath, op.InstallPath, True)
                End If

                File.Delete(destPath)
            End If
        End Function

        Private Sub ExecuteCopyStep(op As ActiveOperation, cpStep As CopyFileStep)
            op.State = InstallationOperationState.Configuring
            Dim srcPath = Path.Combine(op.InstallPath, cpStep.SourceRelativePath)
            Dim dstPath = Path.Combine(op.InstallPath, cpStep.DestinationRelativePath)
            Directory.CreateDirectory(Path.GetDirectoryName(dstPath))
            File.Copy(srcPath, dstPath, cpStep.Overwrite)
        End Sub

        Private Sub ExecuteWriteFileStep(op As ActiveOperation, wfStep As WriteFileStep)
            op.State = InstallationOperationState.Configuring
            Dim filePath = Path.Combine(op.InstallPath, wfStep.RelativePath)
            If File.Exists(filePath) AndAlso Not wfStep.OverwriteExisting Then
                Return
            End If
            Directory.CreateDirectory(Path.GetDirectoryName(filePath))
            File.WriteAllText(filePath, wfStep.Content)
        End Sub

        Private Async Function ExecuteRunProcessStepAsync(op As ActiveOperation,
                                                           rpStep As RunProcessStep,
                                                           cancellation As CancellationToken) As Task
            op.State = InstallationOperationState.Configuring
            Dim psi As New ProcessStartInfo()
            psi.FileName = rpStep.ExecutablePath
            psi.Arguments = If(rpStep.Arguments, "")
            psi.WorkingDirectory = If(rpStep.WorkingDirectory, op.InstallPath)
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.CreateNoWindow = True

            Using proc As New Process()
                proc.StartInfo = psi
                proc.Start()

                Using cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
                    cts.CancelAfter(rpStep.TimeoutMs)
                    Try
                        Await proc.WaitForExitAsync(cts.Token)
                    Catch ex As OperationCanceledException
                        Try
                            proc.Kill(entireProcessTree:=True)
                        Catch
                            proc.Kill()
                        End Try
                        Throw New TimeoutException(
                            $"Process step '{rpStep.StepName}' timed out after {rpStep.TimeoutMs}ms")
                    End Try
                End Using

                If proc.ExitCode <> rpStep.ExpectedExitCode Then
                    Throw New Exception(
                        $"Process step '{rpStep.StepName}' exited with code {proc.ExitCode} (expected {rpStep.ExpectedExitCode})")
                End If
            End Using
        End Function

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Async Function FindOrDownloadSteamCmdAsync(cancellation As CancellationToken) As Task(Of String)
            ' Check common locations first
            ' Use absolute paths — SteamCMD can fail with relative paths
            Dim steamCmdDir = Path.GetFullPath(Path.Combine(_config.DataDirectory, "steamcmd"))
            Dim isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows)
            Dim exeName = If(isWindows, "steamcmd.exe", "steamcmd.sh")
            Dim localPath = Path.Combine(steamCmdDir, exeName)

            If File.Exists(localPath) Then Return localPath

            ' Check other common locations
            Dim candidates = New String() {
                "steamcmd.exe",
                "steamcmd",
                "/usr/games/steamcmd"
            }
            For Each candidate In candidates
                Try
                    If File.Exists(candidate) Then Return candidate
                Catch
                End Try
            Next

            ' Try PATH (Linux)
            If Not isWindows Then
                Try
                    Dim psi As New ProcessStartInfo("which", "steamcmd")
                    psi.UseShellExecute = False
                    psi.RedirectStandardOutput = True
                    psi.CreateNoWindow = True
                    Using proc = Process.Start(psi)
                        Dim pathResult = proc.StandardOutput.ReadToEnd().Trim()
                        proc.WaitForExit()
                        If proc.ExitCode = 0 AndAlso Not String.IsNullOrEmpty(pathResult) Then
                            Return pathResult
                        End If
                    End Using
                Catch
                End Try
            End If

            ' Not found anywhere — download it
            _logger.LogInformation("SteamCMD not found, downloading to {Dir}", steamCmdDir)
            Directory.CreateDirectory(steamCmdDir)

            Dim downloadUrl As String
            Dim archiveName As String
            If isWindows Then
                downloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip"
                archiveName = "steamcmd.zip"
            Else
                downloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz"
                archiveName = "steamcmd_linux.tar.gz"
            End If

            Dim archivePath = Path.Combine(steamCmdDir, archiveName)

            ' Download
            Using response = Await _httpClient.GetAsync(downloadUrl,
                    HttpCompletionOption.ResponseHeadersRead, cancellation)
                response.EnsureSuccessStatusCode()
                Using fileStream As New FileStream(archivePath, FileMode.Create,
                                                    FileAccess.Write, FileShare.None)
                    Await response.Content.CopyToAsync(fileStream, cancellation)
                End Using
            End Using

            ' Extract
            If isWindows Then
                IO.Compression.ZipFile.ExtractToDirectory(archivePath, steamCmdDir, True)
            Else
                ' tar.gz on Linux
                Dim tarPsi As New ProcessStartInfo("tar")
                tarPsi.Arguments = $"-xzf ""{archivePath}"" -C ""{steamCmdDir}"""
                tarPsi.UseShellExecute = False
                tarPsi.CreateNoWindow = True
                Using tarProc = Process.Start(tarPsi)
                    Await tarProc.WaitForExitAsync(cancellation)
                End Using
                ' Make executable
                Try
                    Dim chmodPsi As New ProcessStartInfo("chmod", $"+x ""{localPath}""")
                    chmodPsi.UseShellExecute = False
                    chmodPsi.CreateNoWindow = True
                    Using chmodProc = Process.Start(chmodPsi)
                        Await chmodProc.WaitForExitAsync(cancellation)
                    End Using
                Catch
                End Try
            End If

            ' Clean up archive
            Try
                File.Delete(archivePath)
            Catch
            End Try

            If File.Exists(localPath) Then
                _logger.LogInformation("SteamCMD downloaded successfully to {Path}", localPath)
                Return localPath
            End If

            Throw New FileNotFoundException(
                $"Failed to download SteamCMD. Check network connectivity and try again.")
        End Function

        ''' <summary>
        ''' Quotes a value for SteamCMD command-line arguments.
        ''' Handles internal quotes by doubling them.
        ''' </summary>
        Private Shared Function QuoteForSteamCmd(value As String) As String
            If value Is Nothing Then value = ""
            ' Trailing backslash before closing quote would be interpreted
            ' as an escaped quote, corrupting all subsequent arguments
            value = value.TrimEnd("\"c)
            Return """" & value.Replace("""", """""") & """"
        End Function

        Private Shared Function BuildProgress(op As ActiveOperation) As InstallProgressResponse
            Return New InstallProgressResponse With {
                .InstallationId = op.InstallationId,
                .OperationState = op.State,
                .CurrentStepIndex = op.CurrentStepIndex,
                .TotalSteps = op.TotalSteps,
                .CurrentStepName = op.CurrentStepName,
                .ProgressPercent = op.ProgressPercent,
                .Message = op.Message,
                .ErrorMessage = op.ErrorMessage,
                .PendingPromptType = op.PendingPromptType,
                .PendingPromptMessage = op.PendingPromptMessage
            }
        End Function

    End Class

    ' ============================================================
    '  ActiveOperation — tracks a running install/update
    ' ============================================================

    Friend Class ActiveOperation
        Public Property InstallationId As String
        Public Property GameId As String
        Public Property InstallPath As String
        Public Property Steps As List(Of InstallStep)
        Public Property SteamCredentials As SteamCredential
        Public Property State As InstallationOperationState
        Public Property StartedAt As DateTime
        Public Property CompletedAt As DateTime?
        Public Property CurrentStepIndex As Integer
        Public Property TotalSteps As Integer
        Public Property CurrentStepName As String
        Public Property ProgressPercent As Double
        Public Property Message As String
        Public Property ErrorMessage As String
        Public Property CancellationSource As CancellationTokenSource
        Public Property PendingInputTcs As TaskCompletionSource(Of String)
        Public Property PendingPromptType As PromptType?
        Public Property PendingPromptMessage As String
        Public Property ActiveProcess As Process
    End Class

End Namespace