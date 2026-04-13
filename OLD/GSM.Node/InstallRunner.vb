Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports GSM.Node.Api
Imports GSM.Plugin

' ============================================================
'  InstallRunner
'
'  Executes the ordered list of InstallStepDto objects sent by
'  the manager in an InstallRequest or UpdateRequest.
'
'  Responsibilities:
'    - Run each step in sequence, aborting on first failure
'    - Capture stdout from SteamCMD and other child processes
'    - Detect interactive prompts (Steam Guard, Y/N etc) and
'      surface them via the pending prompt mechanism so the
'      manager can relay them to the UI
'    - Feed install process stdout into the ring buffer so the
'      manager can display live progress
'    - Enforce the installation write lock: reject start/update
'      requests if any running instance holds a read lock
'    - Track active operation count (polled by health endpoint)
'
'  One active operation per installation at a time.
'  Concurrent install requests for the same installation are
'  rejected with INSTALL_ALREADY_IN_PROGRESS.
'
'  Steam credentials:
'    Username and password arrive in the request payload as
'    plaintext strings (decrypted by the manager from DPAPI,
'    transmitted over TLS). The node feeds them to SteamCMD
'    via stdin and never writes them to disk or logs.
' ============================================================

Public Class InstallRunner

    Private ReadOnly _db As NodeDatabase
    Private ReadOnly _ringBuffer As RingBufferStore
    Private ReadOnly _processManager As ProcessManager
    Private ReadOnly _config As NodeConfiguration
    Private ReadOnly _logger As ILogger(Of InstallRunner)

    ' Active operations. Key = InstallationId.
    Private ReadOnly _active As New ConcurrentDictionary(Of String, ActiveOperation)(
        StringComparer.OrdinalIgnoreCase)

    Public Sub New(db As NodeDatabase,
                   ringBuffer As RingBufferStore,
                   processManager As ProcessManager,
                   config As NodeConfiguration,
                   logger As ILogger(Of InstallRunner))
        _db = db
        _ringBuffer = ringBuffer
        _processManager = processManager
        _config = config
        _logger = logger
    End Sub

    Public ReadOnly Property ActiveCount As Integer
        Get
            Return _active.Count
        End Get
    End Property


    ' ============================================================
    '  PUBLIC API
    ' ============================================================

    Public Function StartInstall(request As InstallRequest) As InstallOperationResponse
        Return StartOperation(request.InstallationId,
                              request.Steps,
                              request.InstallPath,
                              request.SteamUsername,
                              request.SteamPassword,
                              isUpdate:=False)
    End Function

    Public Function StartUpdate(request As UpdateRequest) As InstallOperationResponse
        ' Updates require that no running instances hold a read lock
        ' on this installation (they're using the files we'd overwrite).
        ' The manager is responsible for stopping instances first, but
        ' the node enforces it as a safety net.
        Dim runningInstances = _processManager.GetAll().
            Where(Function(i) i.InstallationId = request.InstallationId AndAlso
                               i.State = GSM.Plugin.InstanceState.Running).
            ToList()

        If runningInstances.Any() Then
            Dim names = String.Join(", ",
                runningInstances.Select(Function(i) i.DisplayName))
            Return New InstallOperationResponse With {
                .InstallationId = request.InstallationId,
                .State = GSM.Node.Api.InstallationOperationState.Failed,
                .Message = $"Cannot update while instances are running: {names}. " &
                           "Stop all instances for this installation first."
            }
        End If

        Return StartOperation(request.InstallationId,
                              request.Steps,
                              request.InstallPath,
                              request.SteamUsername,
                              request.SteamPassword,
                              isUpdate:=True)
    End Function

    Private Function StartOperation(installationId As String,
                                     steps As List(Of InstallStepDto),
                                     installPath As String,
                                     steamUsername As String,
                                     steamPassword As String,
                                     isUpdate As Boolean) As InstallOperationResponse

        If _active.ContainsKey(installationId) Then
            Return New InstallOperationResponse With {
                .InstallationId = installationId,
                .State = InstallationOperationState.Running,
                .Message = "An install or update operation is already in progress."
            }
        End If

        Dim op As New ActiveOperation With {
            .InstallationId = installationId,
            .Steps = steps,
            .InstallPath = installPath,
            .SteamUsername = steamUsername,
            .SteamPassword = steamPassword,
            .IsUpdate = isUpdate,
            .Cts = New CancellationTokenSource()
        }

        If Not _active.TryAdd(installationId, op) Then
            Return New InstallOperationResponse With {
                .InstallationId = installationId,
                .State = InstallationOperationState.Running,
                .Message = "Race condition - operation already started."
            }
        End If

        ' Persist initial state.
        _db.UpsertInstallState(New InstallOperationState With {
            .InstallationId = installationId,
            .State = "Running",
            .TotalSteps = steps.Count,
            .CurrentStepIndex = 0,
            .CurrentStepDesc = If(steps.FirstOrDefault()?.Description, ""),
            .StartedAt = DateTime.UtcNow
        })

        ' Start the operation task immediately.
        op.Task = RunStepsAsync(op, op.Cts.Token)

        _logger.LogInformation(
            "Install [{Id}]: started ({Count} steps, update={IsUpdate})",
            installationId, steps.Count, isUpdate)

        Return New InstallOperationResponse With {
            .InstallationId = installationId,
            .State = InstallationOperationState.Running,
            .Message = $"Started ({steps.Count} steps)."
        }
    End Function

    Public Function GetStatus(installationId As String) As InstallationStatusResponse
        Dim dbState = _db.GetInstallState(installationId)
        If dbState Is Nothing Then
            Return New InstallationStatusResponse With {
                .InstallationId = installationId,
                .State = InstallationOperationState.Idle
            }
        End If

        Dim op As ActiveOperation = Nothing
        _active.TryGetValue(installationId, op)

        Dim prompt As InstallPromptInfo = Nothing
        If Not String.IsNullOrEmpty(dbState.PendingPromptJson) Then
            Try
                prompt = JsonSerializer.Deserialize(Of InstallPromptInfo)(
                    dbState.PendingPromptJson)
            Catch
            End Try
        End If

        Return New InstallationStatusResponse With {
            .InstallationId = installationId,
            .State = ParseOperationState(dbState.State),
            .CurrentStepIndex = dbState.CurrentStepIndex,
            .TotalSteps = dbState.TotalSteps,
            .CurrentStepDescription = dbState.CurrentStepDesc,
            .StartedAt = dbState.StartedAt,
            .CompletedAt = dbState.CompletedAt,
            .ErrorMessage = dbState.ErrorMessage,
            .PendingPrompt = prompt,
            .RecentOutput = If(op?.RecentOutput.ToList(), New List(Of String)())
        }
    End Function

    Public Function GetPendingPrompt(installationId As String) As InstallPromptInfo
        Dim op As ActiveOperation = Nothing
        If Not _active.TryGetValue(installationId, op) Then Return Nothing
        Return op.PendingPrompt
    End Function

    Public Function RespondToPrompt(installationId As String,
                                     response As String,
                                     isSensitive As Boolean) As RespondToPromptResponse

        Dim op As ActiveOperation = Nothing
        If Not _active.TryGetValue(installationId, op) Then
            Return New RespondToPromptResponse With {
                .Accepted = False,
                .Message = "No active install operation found."
            }
        End If

        If op.PendingPrompt Is Nothing Then
            Return New RespondToPromptResponse With {
                .Accepted = False,
                .Message = "No prompt is currently waiting for input."
            }
        End If

        ' Write the response to the process's stdin.
        If op.Process IsNot Nothing AndAlso Not op.Process.HasExited Then
            Try
                If Not isSensitive Then
                    _logger.LogDebug("Install [{Id}]: stdin response: {Val}",
                                     installationId, response)
                Else
                    _logger.LogDebug("Install [{Id}]: stdin response: [sensitive]",
                                     installationId)
                End If

                op.Process.StandardInput.WriteLine(response)
                op.Process.StandardInput.Flush()
                op.PendingPrompt = Nothing
                ClearPendingPrompt(installationId)

                Return New RespondToPromptResponse With {
                    .Accepted = True,
                    .Message = "Response sent."
                }
            Catch ex As Exception
                Return New RespondToPromptResponse With {
                    .Accepted = False,
                    .Message = $"Failed to write to process stdin: {ex.Message}"
                }
            End Try
        End If

        Return New RespondToPromptResponse With {
            .Accepted = False,
            .Message = "Install process is not running."
        }
    End Function

    Public Function Cancel(installationId As String) As CancelInstallResponse
        Dim op As ActiveOperation = Nothing
        If Not _active.TryGetValue(installationId, op) Then
            Return New CancelInstallResponse With {
                .InstallationId = installationId,
                .State = InstallationOperationState.Idle,
                .Message = "No active operation found."
            }
        End If

        _logger.LogInformation("Install [{Id}]: cancelling", installationId)
        op.Cts.Cancel()

        Try
            If op.Process IsNot Nothing Then
                op.Process.Kill(entireProcessTree:=True)
            End If
        Catch
        End Try

        Return New CancelInstallResponse With {
            .InstallationId = installationId,
            .State = InstallationOperationState.Cancelled,
            .Message = "Cancellation requested."
        }
    End Function


    ' ============================================================
    '  STEP EXECUTION
    ' ============================================================

    Private Async Function RunStepsAsync(op As ActiveOperation,
                                          cancellation As CancellationToken) As Task
        Try
            For i = 0 To op.Steps.Count - 1
                If cancellation.IsCancellationRequested Then
                    Await CompleteOperationAsync(op, success:=False,
                                                  [error]:="Cancelled by user.")
                    Return
                End If

                Dim installStep = op.Steps(i)
                _logger.LogInformation(
                    "Install [{Id}]: step {N}/{Total}: {Desc}",
                    op.InstallationId, i + 1, op.Steps.Count, installStep.Description)

                UpdateStepProgress(op, i, installStep.Description)

                Dim success = False
                Select Case installStep.StepType
                    Case InstallStepType.SteamCmd
                        success = Await RunSteamCmdStepAsync(op, installStep, cancellation)
                    Case InstallStepType.Download
                        success = Await RunDownloadStepAsync(op, installStep, cancellation)
                    Case InstallStepType.RunCommand
                        success = Await RunCommandStepAsync(op, installStep, cancellation)
                    Case Else
                        _logger.LogWarning(
                            "Install [{Id}]: unknown step type {Type} - skipping",
                            op.InstallationId, installStep.StepType)
                        success = True
                End Select

                If Not success Then
                    Await CompleteOperationAsync(op, success:=False,
                                                  [error]:=$"Step {i + 1} failed: {installStep.Description}")
                    Return
                End If
            Next

            Await CompleteOperationAsync(op, success:=True, [error]:="")
        Catch ex As OperationCanceledException
            Dim msg = "Cancelled."
            Task.Run(Function() CompleteOperationAsync(op, success:=False, [error]:=msg))
        Catch ex As Exception
            _logger.LogError(ex, "Install [{Id}]: unexpected error", op.InstallationId)
            Dim msg = ex.Message
            Task.Run(Function() CompleteOperationAsync(op, success:=False, [error]:=msg))
        Finally
            ' Always remove from active operations when done.
            Dim removed As ActiveOperation = Nothing
            _active.TryRemove(op.InstallationId, removed)
        End Try
    End Function


    ' ---- SteamCMD step ----

    Private Async Function RunSteamCmdStepAsync(op As ActiveOperation,
                                                  installStep As InstallStepDto,
                                                  cancellation As CancellationToken) As Task(Of Boolean)

        ' Build the SteamCMD command line.
        ' We don't pass credentials as arguments (visible in process list).
        ' Instead we pass "+login anonymous" here and feed credentials
        ' via stdin after SteamCMD prompts for them.
        Dim args As New System.Text.StringBuilder()

        If String.IsNullOrEmpty(op.SteamUsername) Then
            args.Append("+login anonymous ")
        Else
            ' Username on the command line is acceptable (not sensitive).
            ' Password will be sent via stdin when prompted.
            args.Append($"+login {op.SteamUsername} ")
        End If

        args.Append($"+force_install_dir ""{installStep.InstallDir}"" ")
        args.Append($"+app_update {installStep.AppId} ")

        If Not String.IsNullOrEmpty(installStep.Branch) Then
            args.Append($"-beta {installStep.Branch} ")
            If Not String.IsNullOrEmpty(installStep.BranchPassword) Then
                args.Append($"-betapassword {installStep.BranchPassword} ")
            End If
        End If

        If installStep.ValidateFiles Then
            args.Append("validate ")
        End If

        args.Append("+quit")

        ' Find SteamCMD executable.
        Dim steamCmdExe = FindSteamCmd()
        If steamCmdExe Is Nothing Then
            LogOutput(op, "[ERROR] SteamCMD not found. Expected at steamcmd/steamcmd.exe " &
                          "(Windows) or steamcmd/steamcmd.sh (Linux).")
            Return False
        End If

        Return Await RunProcessWithPromptHandlingAsync(
            op, steamCmdExe, args.ToString(),
            workingDir:=Path.GetDirectoryName(steamCmdExe),
            expectedExitCode:=0,
            cancellation:=cancellation)
    End Function


    ' ---- Download step ----

    Private Async Function RunDownloadStepAsync(op As ActiveOperation,
                                                  installStep As InstallStepDto,
                                                  cancellation As CancellationToken) As Task(Of Boolean)
        Try
            LogOutput(op, $"Downloading: {installStep.Url}")

            Directory.CreateDirectory(installStep.ExtractToPath)

            Using client As New HttpClient()
                client.Timeout = TimeSpan.FromMinutes(30)

                ' Stream the download to a temp file to avoid loading it all into memory.
                Dim tempFile = Path.Combine(Path.GetTempPath(),
                                            $"gsm_download_{Guid.NewGuid():N}.tmp")
                Try
                    Using response = Await client.GetAsync(installStep.Url,
                        HttpCompletionOption.ResponseHeadersRead, cancellation)

                        If Not response.IsSuccessStatusCode Then
                            LogOutput(op, $"[ERROR] Download failed: HTTP {CInt(response.StatusCode)}")
                            Return False
                        End If

                        Dim totalBytes = response.Content.Headers.ContentLength
                        Dim bytesRead As Long = 0
                        Using fs As New FileStream(tempFile, FileMode.Create, FileAccess.Write)
                        Using contentStream = Await response.Content.ReadAsStreamAsync(cancellation)
                            Dim buffer(81920) As Byte   ' 80KB buffer
                            Dim read = Await contentStream.ReadAsync(buffer, cancellation)
                            Do While read > 0
                                Await fs.WriteAsync(buffer.AsMemory(0, read), cancellation)
                                bytesRead += read
                                If totalBytes.HasValue Then
                                    Dim pct = CInt((bytesRead * 100) / totalBytes.Value)
                                    If pct Mod 10 = 0 Then   ' Log every 10%
                                        LogOutput(op, $"Download: {pct}% ({bytesRead:N0} / {totalBytes:N0} bytes)")
                                    End If
                                End If
                                read = Await contentStream.ReadAsync(buffer, cancellation)
                            Loop
                        End Using
                        End Using

                        LogOutput(op, $"Download complete ({bytesRead:N0} bytes)")
                    End Using

                    ' Verify checksum if provided.
                    If Not String.IsNullOrEmpty(installStep.Sha256) Then
                        LogOutput(op, "Verifying checksum...")
                        Dim actualHash = ComputeSha256(tempFile)
                        If Not actualHash.Equals(installStep.Sha256,
                                                  StringComparison.OrdinalIgnoreCase) Then
                            LogOutput(op, $"[ERROR] Checksum mismatch. " &
                                          $"Expected: {installStep.Sha256}, Got: {actualHash}")
                            Return False
                        End If
                        LogOutput(op, "Checksum verified.")
                    End If

                    ' Extract the archive.
                    LogOutput(op, $"Extracting to {installStep.ExtractToPath}...")
                    ExtractArchive(tempFile, installStep.ExtractToPath, op)
                    LogOutput(op, "Extraction complete.")
                    Return True

                Finally
                    Try
                        If File.Exists(tempFile) Then File.Delete(tempFile)
                    Catch
                    End Try
                End Try
            End Using

        Catch ex As OperationCanceledException
            Throw
        Catch ex As Exception
            LogOutput(op, $"[ERROR] Download step failed: {ex.Message}")
            Return False
        End Try
    End Function


    ' ---- RunCommand step ----

    Private Async Function RunCommandStepAsync(op As ActiveOperation,
                                                 installStep As InstallStepDto,
                                                 cancellation As CancellationToken) As Task(Of Boolean)

        ' Skip bash steps on Windows - they're Linux-only helpers.
        If installStep.Executable = "bash" AndAlso OperatingSystem.IsWindows() Then
            LogOutput(op, $"Skipping bash step on Windows: {installStep.Description}")
            Return True
        End If

        ' Skip cmd.exe steps on Linux.
        If installStep.Executable = "cmd.exe" AndAlso Not OperatingSystem.IsWindows() Then
            LogOutput(op, $"Skipping cmd step on Linux: {installStep.Description}")
            Return True
        End If

        Return Await RunProcessWithPromptHandlingAsync(
            op, installStep.Executable, installStep.Arguments,
            workingDir:=installStep.WorkingDirectory,
            expectedExitCode:=installStep.ExpectExitCode,
            cancellation:=cancellation)
    End Function


    ' ============================================================
    '  PROCESS RUNNER WITH PROMPT HANDLING
    '  Shared by SteamCMD and RunCommand steps.
    '  Captures stdout, detects prompts, and feeds responses.
    ' ============================================================

    Private Async Function RunProcessWithPromptHandlingAsync(
            op As ActiveOperation,
            executable As String,
            arguments As String,
            workingDir As String,
            expectedExitCode As Integer,
            cancellation As CancellationToken) As Task(Of Boolean)

        Dim psi As New ProcessStartInfo With {
            .FileName = executable,
            .Arguments = arguments,
            .WorkingDirectory = If(Directory.Exists(workingDir), workingDir, ""),
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .RedirectStandardInput = True
        }

        _logger.LogDebug("Install [{Id}]: running {Exe} {Args}",
                         op.InstallationId, executable, arguments)

        Dim proc = Process.Start(psi)
        If proc Is Nothing Then
            LogOutput(op, $"[ERROR] Failed to start process: {executable}")
            Return False
        End If

        op.Process = proc

        ' If we have a Steam password, send it to stdin promptly.
        ' SteamCMD expects it before the login prompt times out.
        ' We send it optimistically here; the prompt handler below
        ' will also catch the explicit prompt if this races.
        If Not String.IsNullOrEmpty(op.SteamPassword) Then
            Try
                proc.StandardInput.WriteLine(op.SteamPassword)
                proc.StandardInput.Flush()
            Catch
                ' Process may not have started reading stdin yet - the
                ' prompt handler will retry when SteamCMD asks.
            End Try
        End If

        ' Read stdout on a background task so we can also watch for prompts.
        Dim stdoutTask = Task.Run(
            Async Function()
                Try
                    Dim reader = proc.StandardOutput
                    Dim line = Await reader.ReadLineAsync(cancellation)
                    Do While line IsNot Nothing
                        LogOutput(op, line)
                        CheckForPrompt(op, proc, line)
                        line = Await reader.ReadLineAsync(cancellation)
                    Loop
                Catch ex As OperationCanceledException
                Catch
                End Try
            End Function)

        ' Also drain stderr.
        Dim stderrTask = Task.Run(
            Async Function()
                Try
                    Dim reader = proc.StandardError
                    Dim line = Await reader.ReadLineAsync(cancellation)
                    Do While line IsNot Nothing
                        LogOutput(op, $"[stderr] {line}")
                        line = Await reader.ReadLineAsync(cancellation)
                    Loop
                Catch
                End Try
            End Function)

        ' Wait for the process to exit.
        Try
            Await proc.WaitForExitAsync(cancellation)
        Catch ex As OperationCanceledException
            proc.Kill(entireProcessTree:=True)
            Throw
        End Try

        ' Wait for stdout/stderr readers to finish.
        Await Task.WhenAll(stdoutTask, stderrTask)

        op.Process = Nothing

        Dim exitCode = proc.ExitCode
        _logger.LogDebug("Install [{Id}]: process exited with code {Code}",
                         op.InstallationId, exitCode)

        If exitCode <> expectedExitCode Then
            LogOutput(op,
                $"[ERROR] Process exited with code {exitCode} (expected {expectedExitCode})")
            Return False
        End If

        Return True
    End Function


    ' ============================================================
    '  PROMPT DETECTION
    '  Checks each stdout line against known prompt patterns.
    '  When a prompt is detected, the operation enters
    '  WaitingForInput state until RespondToPrompt is called.
    ' ============================================================

    ' Known SteamCMD prompt patterns.
    Private ReadOnly _promptPatterns As (Pattern As String, PromptType As String,
                                          Message As String, IsSensitive As Boolean)() = {
        ("Steam Guard code", "SteamGuardEmail",
         "Steam Guard code required. Check the email for your Steam account.",
         False),
        ("Two-factor code", "SteamGuardMobile",
         "Steam Guard mobile authenticator code required.",
         False),
        ("Please enter the current code", "SteamGuardTwoFactor",
         "Two-factor authentication code required.",
         False),
        ("password:", "Password",
         "SteamCMD is asking for a Steam account password.",
         True)
    }

    Private Sub CheckForPrompt(op As ActiveOperation,
                                proc As Process,
                                line As String)

        For Each pattern In _promptPatterns
            If line.IndexOf(pattern.Pattern,
                            StringComparison.OrdinalIgnoreCase) < 0 Then Continue For

            ' Special case: if this is a password prompt and we already
            ' have a password, send it automatically without surfacing
            ' a prompt to the UI.
            If pattern.PromptType = "Password" AndAlso
               Not String.IsNullOrEmpty(op.SteamPassword) Then
                Try
                    proc.StandardInput.WriteLine(op.SteamPassword)
                    proc.StandardInput.Flush()
                    LogOutput(op, "[Auto-sent Steam password]")
                Catch
                End Try
                Return
            End If

            ' Surface the prompt to the manager UI.
            _logger.LogWarning(
                "Install [{Id}]: prompt detected: {Type}", op.InstallationId, pattern.PromptType)

            op.PendingPrompt = New InstallPromptInfo With {
                .PromptType = ParsePromptType(pattern.PromptType),
                .DisplayMessage = pattern.Message,
                .IsSensitive = pattern.IsSensitive,
                .WaitingForInputSince = DateTime.UtcNow
            }

            ' Persist to DB so it survives a status poll.
            SetPendingPrompt(op.InstallationId, op.PendingPrompt)

            UpdateOperationState(op.InstallationId, "WaitingForInput",
                                  op.Steps.Count, op.CurrentStepIndex,
                                  $"Waiting for user input: {pattern.Message}")
            Return
        Next
    End Sub

    Private Function ParsePromptType(s As String) As PromptType
        Select Case s
            Case "SteamGuardEmail"     : Return PromptType.SteamGuardEmail
            Case "SteamGuardMobile"    : Return PromptType.SteamGuardMobile
            Case "SteamGuardTwoFactor" : Return PromptType.SteamGuardTwoFactor
            Case Else                  : Return PromptType.FreeText
        End Select
    End Function


    ' ============================================================
    '  HELPERS
    ' ============================================================

    Private Sub LogOutput(op As ActiveOperation, line As String)
        ' Feed into ring buffer under a special "install" source ID.
        _ringBuffer.Append(op.InstallationId, "install", DateTime.UtcNow, line)

        ' Keep a small recent output window for the status endpoint.
        op.RecentOutput.Enqueue(line)
        Do While op.RecentOutput.Count > 100
            Dim dropped As String = Nothing
            op.RecentOutput.TryDequeue(dropped)
        Loop
    End Sub

    Private Sub UpdateStepProgress(op As ActiveOperation,
                                    stepIndex As Integer,
                                    stepDesc As String)
        op.CurrentStepIndex = stepIndex
        UpdateOperationState(op.InstallationId, "Running",
                              op.Steps.Count, stepIndex, stepDesc)
    End Sub

    Private Sub UpdateOperationState(installationId As String,
                                      state As String,
                                      totalSteps As Integer,
                                      currentStep As Integer,
                                      stepDesc As String)
        Dim existing = _db.GetInstallState(installationId)
        _db.UpsertInstallState(New InstallOperationState With {
            .InstallationId = installationId,
            .State = state,
            .TotalSteps = totalSteps,
            .CurrentStepIndex = currentStep,
            .CurrentStepDesc = stepDesc,
            .StartedAt = existing?.StartedAt,
            .Pid = existing?.Pid,
            .PendingPromptJson = If(existing?.PendingPromptJson, "")
        })
    End Sub

    Private Async Function CompleteOperationAsync(op As ActiveOperation,
                                                   success As Boolean,
                                                   [error] As String) As Task
        Dim finalState = If(success, "Succeeded",
                            If(op.Cts.IsCancellationRequested, "Cancelled", "Failed"))

        _logger.LogInformation(
            "Install [{Id}]: {State}{Error}",
            op.InstallationId, finalState,
            If(success, "", $" - {[error]}"))

        _db.UpsertInstallState(New InstallOperationState With {
            .InstallationId = op.InstallationId,
            .State = finalState,
            .TotalSteps = op.Steps.Count,
            .CurrentStepIndex = If(success, op.Steps.Count, op.CurrentStepIndex),
            .CurrentStepDesc = If(success, "Complete", [error]),
            .StartedAt = _db.GetInstallState(op.InstallationId)?.StartedAt,
            .CompletedAt = DateTime.UtcNow,
            .ErrorMessage = If(success, "", [error])
        })

        Await Task.CompletedTask
    End Function

    Private Sub SetPendingPrompt(installationId As String, prompt As InstallPromptInfo)
        Dim existing = _db.GetInstallState(installationId)
        If existing Is Nothing Then Return
        existing.PendingPromptJson = JsonSerializer.Serialize(prompt)
        _db.UpsertInstallState(existing)
    End Sub

    Private Sub ClearPendingPrompt(installationId As String)
        Dim existing = _db.GetInstallState(installationId)
        If existing Is Nothing Then Return
        existing.PendingPromptJson = ""
        _db.UpsertInstallState(existing)
    End Sub

    Private Function FindSteamCmd() As String
        ' Look in a steamcmd\ subdirectory relative to the node's working directory.
        Dim candidates As String() = {
            Path.Combine("steamcmd", "steamcmd.exe"),   ' Windows
            Path.Combine("steamcmd", "steamcmd.sh"),    ' Linux
            Path.Combine("steamcmd", "steamcmd")        ' Linux alternative
        }
        For Each c In candidates
            If File.Exists(c) Then Return Path.GetFullPath(c)
        Next
        Return Nothing
    End Function

    Private Function ComputeSha256(filePath As String) As String
        Using sha = System.Security.Cryptography.SHA256.Create()
        Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read)
            Dim hash = sha.ComputeHash(fs)
            Return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()
        End Using
        End Using
    End Function

    Private Sub ExtractArchive(archivePath As String,
                                targetDir As String,
                                op As ActiveOperation)
        Dim ext = Path.GetExtension(archivePath).ToLowerInvariant()

        If ext = ".zip" Then
            System.IO.Compression.ZipFile.ExtractToDirectory(
                archivePath, targetDir, overwriteFiles:=True)
        ElseIf ext = ".gz" OrElse archivePath.EndsWith(".tar.gz") Then
            ' Use tar on Linux. On Windows, .NET 8 has TarFile support.
            If OperatingSystem.IsLinux() Then
                Dim psi As New ProcessStartInfo("tar",
                    $"-xzf ""{archivePath}"" -C ""{targetDir}""") With {
                    .UseShellExecute = False,
                    .CreateNoWindow = True
                }
                Using proc = Process.Start(psi)
                    proc.WaitForExit(TimeSpan.FromMinutes(10))
                    If proc.ExitCode <> 0 Then
                        Throw New IOException($"tar exited with code {proc.ExitCode}")
                    End If
                End Using
            Else
                ' .NET 8: System.Formats.Tar
                Dim tempTar = archivePath.Replace(".gz", "")
                Using gzStream As New IO.Compression.GZipStream(
                    File.OpenRead(archivePath), IO.Compression.CompressionMode.Decompress)
                Using outStream = File.Create(tempTar)
                    gzStream.CopyTo(outStream)
                End Using
                End Using
                System.Formats.Tar.TarFile.ExtractToDirectory(tempTar, targetDir, True)
                File.Delete(tempTar)
            End If
        Else
            Throw New NotSupportedException(
                $"Unsupported archive format: {ext}. Supported: .zip, .tar.gz")
        End If
    End Sub

    Private Function ParseOperationState(s As String) As InstallationOperationState
        Select Case s
            Case "Running"          : Return InstallationOperationState.Running
            Case "WaitingForInput"  : Return InstallationOperationState.WaitingForInput
            Case "Succeeded"        : Return InstallationOperationState.Succeeded
            Case "Failed"           : Return InstallationOperationState.Failed
            Case "Cancelled"        : Return InstallationOperationState.Cancelled
            Case Else               : Return InstallationOperationState.Idle
        End Select
    End Function

End Class


' ============================================================
'  ACTIVE OPERATION
'  In-memory state for one running install/update operation.
' ============================================================

Friend Class ActiveOperation
    Public Property InstallationId As String
    Public Property Steps As List(Of InstallStepDto)
    Public Property InstallPath As String
    Public Property SteamUsername As String
    Public Property SteamPassword As String     ' In-memory only, never persisted
    Public Property IsUpdate As Boolean
    Public Property Cts As CancellationTokenSource
    Public Property Task As Task
    Public Property Process As Process          ' The currently running child process
    Public Property PendingPrompt As InstallPromptInfo
    Public Property CurrentStepIndex As Integer = 0
    ' Rolling window of recent stdout lines for the status endpoint.
    Public ReadOnly RecentOutput As New ConcurrentQueue(Of String)()
End Class
