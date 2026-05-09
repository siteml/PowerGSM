Imports System
Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Runtime.InteropServices
Imports SharpCompress.Archives
Imports SharpCompress.Common
Imports SharpCompress.Compressors.Xz
Imports SharpCompress.Readers
Imports SharpCompress.Readers.Tar
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
            op.RunCommonRedist = request.RunCommonRedist
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
                    ' Weighted progress at step entry: completed-step
                    ' weights summed, current step's contribution at 0%.
                    ' Replaces the old equal-weight i/N math so the bar
                    ' tracks time-spent rather than steps-completed (a
                    ' multi-minute SteamCMD download visibly dominates a
                    ' 3-step plan rather than competing for an equal
                    ' slice with seconds-long copy/finalise steps).
                    op.ProgressPercent = ComputeWeightedProgress(op, 0.0)

                    _logger.LogInformation("Install {Id}: step {Step}/{Total} - {Name}",
                                           op.InstallationId, i + 1, op.TotalSteps, op.CurrentStepName)

                    Await ExecuteStepAsync(op, op.Steps(i))
                Next

                op.State = InstallationOperationState.Completed
                op.ProgressPercent = 100
                ' Replace whatever the last stdout / progress line
                ' left in op.Message with a clean completion
                ' message. Otherwise the post-completion display
                ' reads as whatever the very last stdout fragment
                ' was — a structured progress line at best, the
                ' "-- type 'quit' to exit --" SteamCMD REPL prompt
                ' at worst. Either way the operator wants to see
                ' "completed", not "99.9% verifying".
                op.Message = "Installation completed."
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

            ' Pre-flight: detect missing 32-bit runtime libraries on
            ' Linux before grinding through retries that produce no
            ' useful diagnostic for the operator.
            Await PreflightSteamCmdAsync(steamCmdPath, steamCmdDir, cancellation)

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

                If exitCode = 0 Then
                    EnsureSteamClientSdkSymlink(steamCmdPath)
                    Await CapturePostInstallBuildIdAsync(op, steamStep.AppId, cancellation)
                    If op.RunCommonRedist Then
                        Await RunCommonRedistAsync(op, cancellation)
                    Else
                        _logger.LogInformation("Skipping _CommonRedist (disabled in installation settings)")
                    End If
                    Return
                End If

                ' Code 7 = SteamCMD self-updated after install.
                ' If the app install succeeded, treat as success.
                If exitCode = 7 AndAlso _steamCmdSawSuccess Then
                    _logger.LogInformation("SteamCMD exited code 7 but app install succeeded")
                    EnsureSteamClientSdkSymlink(steamCmdPath)
                    Await CapturePostInstallBuildIdAsync(op, steamStep.AppId, cancellation)
                    If op.RunCommonRedist Then
                        Await RunCommonRedistAsync(op, cancellation)
                    Else
                        _logger.LogInformation("Skipping _CommonRedist (disabled in installation settings)")
                    End If
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

            ' Tailer for SteamCMD's content_log.txt. Runs in
            ' parallel with the SteamCMD process and parses
            ' progress lines into op fields. The tailer block
            ' comment further down explains why we tail instead
            ' of relying on stdout. Declared at function scope
            ' so the Finally can cancel them.
            Dim tailerCts As CancellationTokenSource = Nothing
            Dim tailerTask As Task = Nothing

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

                ' Extract appId from the SteamCMD arguments —
                ' needed by the manifest-existence backup-success
                ' check near the bottom of this function. Empty
                ' string when arguments don't carry an app_update
                ' verb (e.g. a bare login pass); the consumer below
                ' handles that defensively.
                Dim appId As String = ""
                Dim appIdMatch = Regex.Match(arguments, "app_update\s+(\d+)")
                If appIdMatch.Success Then
                    appId = appIdMatch.Groups(1).Value
                End If

                ' Snapshot content_log.txt's current size so the
                ' tailer doesn't replay history from prior SteamCMD
                ' operations that share the same logs/ directory.
                ' Best-effort — if the snapshot fails (missing file,
                ' permissions), the tailer starts at 0 and may
                ' parse one batch of stale lines on first read.
                ' Harmless beyond a transient out-of-date display.
                Dim contentLogPath = Path.Combine(workingDir, "logs", "content_log.txt")
                Dim contentLogStartPos As Long = 0
                Try
                    If File.Exists(contentLogPath) Then
                        contentLogStartPos = New FileInfo(contentLogPath).Length
                    End If
                Catch
                End Try

                ' Spawn the tailer before proc.Start so it's
                ' draining from the very first SteamCMD writes.
                ' Cancellation in the Finally winds it down.
                tailerCts = New CancellationTokenSource()
                tailerTask = RunSteamCmdContentLogTailerAsync(
                    contentLogPath, contentLogStartPos, op, tailerCts.Token)

                proc.Start()
                proc.BeginOutputReadLine()
                proc.BeginErrorReadLine()

                ' Process I/O counter poller for smooth Downloading-
                ' phase percent. Reads the kernel's per-process
                ' WriteTransferCount via GetProcessIoCounters, which
                ' tracks every byte the process passes to WriteFile
                ' (and similar) since it started. Counts writes into
                ' preallocated holes that don't change file size,
                ' which is exactly what disk-walking can't see.
                ' Spawned after proc.Start so proc.Handle is valid.
                ' Shares tailerCts so a single Cancel in the Finally
                ' hits both; we don't keep a reference to its task
                ' since we never await it.
                Dim _unusedDownloadPoller = RunDownloadProgressPollerAsync(
                    proc, op, tailerCts.Token)

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
                If Not String.IsNullOrEmpty(appId) Then
                    Dim manifestPath = Path.Combine(
                        op.InstallPath, "steamapps",
                        $"appmanifest_{appId}.acf")
                    If File.Exists(manifestPath) Then
                        _steamCmdSawSuccess = True
                    End If
                End If

                Return proc.ExitCode

            Finally
                ' Stop the tailer first. We don't await
                ' tailerTask here — Await-in-Finally is
                ' unsupported in VB.Net — and we don't dispose
                ' the CTS either, since disposing while the
                ' tailer's Task.Delay is still observing the
                ' token would throw ObjectDisposedException in
                ' the tailer. Plain Cancel is enough: the
                ' tailer self-terminates on its next iteration
                ' (~500ms max) and the CTS is GC-eligible after.
                If tailerCts IsNot Nothing Then
                    Try
                        tailerCts.Cancel()
                    Catch
                    End Try
                End If

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

                ' Clear tailer-set fields so a subsequent step
                ' (or the next install on this op) doesn't show
                ' stale phase/byte values. Per the contract on
                ' InstallProgressResponse, these are only
                ' meaningful during a SteamCMD step. A briefly-
                ' alive tailer from this attempt may re-set them
                ' for up to ~500ms after we clear; the next 2s
                ' manager poll picks up whichever values are
                ' current at that moment.
                op.SteamCmdPhase = Nothing
                op.BytesDownloaded = Nothing
                op.BytesTotal = Nothing
                op.LastLoggedSteamCmdPhase = Nothing
                op.LastLoggedSteamCmdPct = -100.0

                _steamCmdCurrentOp = Nothing
            End Try
        End Function

        ' ============================================================
        '  SteamCMD event handlers (class-level)
        ' ============================================================

        Private Sub SteamCmd_OutputDataReceived(sender As Object, e As DataReceivedEventArgs)
            If e.Data Is Nothing Then Return

            ' Strip ANSI colour escapes before doing anything with
            ' the line. Linux SteamCMD emits CSI sequences
            ' ("\x1b[0m...", "\x1b[32m...", etc.) when writing to
            ' a pipe; the ESC byte itself doesn't render in
            ' WinForms labels and the user is left staring at
            ' visible "[0m" artefacts. Windows SteamCMD doesn't
            ' colour its output, so this is a no-op there.
            ' Stripping at the source means the log file, the
            ' message field, and the regex matching below all
            ' work on the same clean text.
            Dim cleanLine = StripAnsi(e.Data)

            _logger.LogInformation("SteamCMD: {Line}", cleanLine)

            If _steamCmdCurrentOp IsNot Nothing Then
                ' Try to parse a structured "Update state (0xN)
                ' PHASE, progress: PCT (BYTES / TOTAL)" line. On
                ' Linux this is the workable path because the
                ' content_log.txt tailer doesn't reliably observe
                ' phase transitions there — SteamCMD's binary
                ' appears to flush them only to stdout on Linux,
                ' leaving the file empty or stale even mid-install.
                ' On Windows content_log.txt remains the primary
                ' source and any agreement here is harmless
                ' redundancy; both sources write the same op fields.
                Dim handled = ApplyStdoutProgressLineToOp(
                    cleanLine, _steamCmdCurrentOp)

                If Not handled Then
                    ' Not a structured progress line. Use it as the
                    ' generic message until a structured source
                    ' (this stdout parser or the content_log
                    ' tailer) has set SteamCmdPhase — after which
                    ' the structured sources own the message and
                    ' this fallback shouldn't clobber their
                    ' formatted text.
                    '
                    ' Skip known SteamCMD decoration / prompt
                    ' lines (the "-- type 'quit' to exit --"
                    ' REPL prompt being the worst offender:
                    ' SteamCMD writes it right before consuming
                    ' the +quit verb, so it lands in op.Message
                    ' as the last thing the handler sees, and
                    ' Finally's phase-clear in
                    ' RunSteamCmdProcessAsync doesn't clear the
                    ' message, so it sticks as the post-completion
                    ' display. Filtering at the source means
                    ' op.Message holds the last useful update
                    ' — typically the last structured progress
                    ' line or an informational connect/login line.
                    If Not IsSteamCmdNoiseLine(cleanLine) Then
                        If String.IsNullOrEmpty(_steamCmdCurrentOp.SteamCmdPhase) Then
                            _steamCmdCurrentOp.Message = cleanLine
                        End If
                    End If
                End If
            End If

            If cleanLine.Contains("Success! App") Then
                _steamCmdSawSuccess = True
            End If
        End Sub

        Private Sub SteamCmd_ErrorDataReceived(sender As Object, e As DataReceivedEventArgs)
            If e.Data IsNot Nothing Then
                _logger.LogWarning("SteamCMD [ERR]: {Line}", StripAnsi(e.Data))
            End If
        End Sub

        ' ============================================================
        '  SteamCMD content_log.txt tailer
        '
        '  SteamCMD's stdout is block-buffered when redirected (libc
        '  detects no tty and switches off line buffering), so capturing
        '  progress from stdout produces minutes of silence punctuated by
        '  bursts — useless for live progress feedback. SteamCMD's
        '  internal logging into content_log.txt does fflush per write,
        '  though, so tailing that file is the workable path. Same
        '  open-read-close pattern as ProcessManager's game-log tailer
        '  (no persistent FileStream, FileShare.ReadWrite|Delete to
        '  coexist with SteamCMD's exclusive write).
        '
        '  Lifecycle is hooked off a CancellationTokenSource owned by
        '  RunSteamCmdProcessAsync — cancellation fires when SteamCMD
        '  exits, the tailer self-terminates on its next read or
        '  Task.Delay. The caller does NOT await the returned Task
        '  (Await-in-Finally is unsupported in VB.Net), so the tailer
        '  has up to ~500ms to wind down after cancel. That window
        '  is harmless — all writes go to op fields the next caller
        '  resets at SteamCMD step entry.
        ' ============================================================

        ' Regex set for parsing SteamCMD's content_log.txt. The
        ' format isn't documented and varies between SteamCMD
        ' versions, so we match permissive patterns and ignore lines
        ' that don't fit any of them. Sample lines this parser is
        ' designed against (from a Last Oasis install on Windows):
        '
        '   AppID 920720 App update changed : Running Update,Downloading,Staging,
        '   AppID 920720 update started : download 0/1507283968, store 0/0, reuse 0/0, delta 0/0, stage 0/7148022215
        '   Verified 1931 MB in 'C:\PowerGSM\data\steamcmd', clean bytes tally is now 2049 MB
        '
        ' Note: the "Update state (0x61) downloading, progress: ..."
        ' lines that show up in stdout DO NOT appear in
        ' content_log.txt — those are a separate output channel
        ' SteamCMD writes only to stdout. content_log.txt instead
        ' carries phase transitions, the initial expected sizes,
        ' verify progress, and commit boundaries.
        '
        ' Smooth download-phase progress is therefore not derivable
        ' from content_log alone. The companion disk poller (added
        ' separately) handles that case by sampling the
        ' steamapps/downloading directory size during Downloading.

        ' Phase-change line. Group 1 = appid, Group 2 = comma-
        ' separated phase list ("Running Update,Downloading,Staging,"
        ' or just "None" or "Running Update," for transient empty
        ' states between secondary phases).
        Private Shared ReadOnly s_PhaseChangeRegex As _
            New System.Text.RegularExpressions.Regex(
                "AppID\s+(\d+)\s+App update changed\s*:\s*(.+?)\s*$",
                System.Text.RegularExpressions.RegexOptions.Compiled)

        ' Update-started line. Carries the full set of byte budgets;
        ' we only capture the "stage" total (Group 2) which is the
        ' uncompressed install size. Same denominator the stdout
        ' progress lines use after stdout flushes at process exit,
        ' so disk-polling against this total gives a percent that
        ' would line up with stdout's view if stdout were live.
        Private Shared ReadOnly s_UpdateStartedRegex As _
            New System.Text.RegularExpressions.Regex(
                "AppID\s+(\d+)\s+update started\s*:\s*download\s+\d+/\d+,\s*store\s+\d+/\d+,\s*reuse\s+\d+/\d+,\s*delta\s+\d+/\d+,\s*stage\s+\d+/(\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled)

        ' Verified line during the Verifying phase. Group 1 is the
        ' cumulative byte tally in MB — each line gives a running
        ' total since verify started, so we treat it as bytes-
        ' verified-so-far. The path that follows "in '...'" is
        ' SteamCMD's own data directory, not the install path,
        ' which is why the tally caps below the install's stage
        ' total: Steam verifies its compressed depot cache, not
        ' the uncompressed install. Useful as a progress signal
        ' but the percent reflects depot-cache verification, not
        ' install verification.
        Private Shared ReadOnly s_VerifiedRegex As _
            New System.Text.RegularExpressions.Regex(
                "Verified\s+\d+\s+MB.*?clean bytes tally is now\s+(\d+)\s+MB",
                System.Text.RegularExpressions.RegexOptions.Compiled)

        ' ANSI CSI sequence stripper. Matches the standard
        ' "\x1b[ ... <final-byte>" form used for SGR (colour),
        ' cursor movement, erase, etc. — anything ending in a
        ' letter A–Z or a–z. Linux SteamCMD emits SGR resets
        ' ("\x1b[0m") around every line and an SGR sequence at
        ' the start; without stripping, those land in the
        ' message field as visible "[0m" artefacts (the ESC byte
        ' itself doesn't render in WinForms labels). Windows
        ' SteamCMD doesn't colour its output so this is a no-op
        ' there. Compiled regex; allocates once.
        Private Shared ReadOnly s_AnsiRegex As _
            New System.Text.RegularExpressions.Regex(
                Convert.ToChar(27) & "\[[0-9;?]*[a-zA-Z]",
                System.Text.RegularExpressions.RegexOptions.Compiled)

        ' Stdout progress line. SteamCMD writes lines like
        '   "Update state (0x61) downloading, progress: 47.3 (12345 / 26789)"
        '   "Update state (0x5) verifying install, progress: 81.12 (3917856596 / 4829994498)"
        ' to stdout (and only stdout — these don't appear in
        ' content_log.txt). Group 1 = phase string ("downloading",
        ' "verifying install", etc.), Group 2 = percent,
        ' Group 3 = bytes done, Group 4 = bytes total.
        Private Shared ReadOnly s_StdoutUpdateStateRegex As _
            New System.Text.RegularExpressions.Regex(
                "Update state\s*\(0x[0-9a-fA-F]+\)\s+([^,]+?),\s*progress:\s*([0-9.]+)\s*\(\s*(\d+)\s*/\s*(\d+)\s*\)",
                System.Text.RegularExpressions.RegexOptions.Compiled)

        ''' <summary>
        ''' Strip ANSI CSI escape sequences from a string. Returns
        ''' the input unchanged when no ESC byte is present (cheap
        ''' fast-path that avoids the regex engine on Windows where
        ''' SteamCMD's stdout is plain ASCII).
        ''' </summary>
        Private Shared Function StripAnsi(s As String) As String
            If String.IsNullOrEmpty(s) Then Return s
            If s.IndexOf(Convert.ToChar(27)) < 0 Then Return s
            Return s_AnsiRegex.Replace(s, "")
        End Function

        ''' <summary>
        ''' Try to parse a SteamCMD stdout "Update state (0xN)
        ''' PHASE, progress: PCT (BYTES / TOTAL)" line and apply
        ''' the extracted fields to the active operation. Returns
        ''' True on a successful parse so the caller can skip the
        ''' generic-message fallback.
        '''
        ''' This is the primary progress source on Linux: the
        ''' content_log.txt tailer that drives the Windows path
        ''' doesn't reliably observe phase transitions on Linux,
        ''' which leaves SteamCmdPhase empty, the disk-IO poller's
        ''' phase gate closed, ProgressPercent stuck at the step-
        ''' boundary value, and the message field falling through
        ''' to the raw stdout-line fallback ("[0m..." gibberish).
        ''' Parsing this same data structurally fixes all of those
        ''' downstream symptoms with one source change.
        '''
        ''' Phase mapping aligns with NormalizePhase's folded
        ''' label set so a stdout-set phase looks identical to
        ''' a content_log-set one in the UI — "verifying install"
        ''' and "validating" both fold into "Verifying", etc.
        ''' </summary>
        Private Function ApplyStdoutProgressLineToOp(line As String,
                                                       op As ActiveOperation) As Boolean
            If String.IsNullOrEmpty(line) OrElse op Is Nothing Then Return False

            Dim m = s_StdoutUpdateStateRegex.Match(line)
            If Not m.Success Then Return False

            Dim phase = NormalizeStdoutPhase(m.Groups(1).Value)
            Dim pct As Double = 0
            Double.TryParse(m.Groups(2).Value,
                              Globalization.NumberStyles.Float,
                              Globalization.CultureInfo.InvariantCulture, pct)
            Dim bytesDone As Long = 0
            Long.TryParse(m.Groups(3).Value, bytesDone)
            Dim bytesTotal As Long = 0
            Long.TryParse(m.Groups(4).Value, bytesTotal)

            ' Phase comes directly from the stdout structure;
            ' overwrite whatever was there. Throttle the log to
            ' phase transitions only.
            If Not String.IsNullOrEmpty(phase) Then
                If Not String.Equals(phase, op.LastLoggedSteamCmdPhase,
                                       StringComparison.Ordinal) Then
                    _logger.LogInformation("SteamCMD phase: {Phase}", phase)
                    op.LastLoggedSteamCmdPhase = phase
                    op.LastLoggedSteamCmdPct = -100.0
                End If
                op.SteamCmdPhase = phase
            End If

            ' Bytes: the stdout total can shadow content_log's
            ' "update started" total. Both should produce the
            ' same denominator on a single-app install; on a
            ' multi-app one we keep the larger of the two so the
            ' percent reflects total work, not the smaller
            ' subtask. BytesDownloaded is shared with the disk-
            ' IO poller (RunDownloadProgressPollerAsync) on a
            ' max-wins basis: the poller only writes when its
            ' wchar-derived delta is ahead of the value we set
            ' here. On Windows that means the I/O counter is the
            ' denser source during Downloading; on Linux this
            ' stdout parser is, since wchar lags SteamCMD's
            ' mmap-based writes there until page-cache flushes
            ' catch up.
            If bytesTotal > 0 Then
                If Not op.BytesTotal.HasValue OrElse bytesTotal > op.BytesTotal.Value Then
                    op.BytesTotal = bytesTotal
                End If
            End If
            If bytesDone >= 0 Then
                op.BytesDownloaded = bytesDone
            End If

            ' ProgressPercent and Message: route through
            ' ComputeWeightedProgress so the bar respects step
            ' weighting (SteamCMD step normally weights ~10x
            ' a follow-on copy step). Cap percent at 99 — the
            ' phase transition out of Downloading/Verifying is
            ' what officially marks completion of that work; a
            ' bar that hits 100% but stays in the same phase
            ' label reads as stuck.
            Dim cappedPct = pct
            If cappedPct > 99.0 Then cappedPct = 99.0
            op.ProgressPercent = ComputeWeightedProgress(op, cappedPct)

            Dim totalMb = bytesTotal / 1048576.0
            Dim doneMb = bytesDone / 1048576.0
            Dim labelPhase = If(String.IsNullOrEmpty(phase), "Working", phase)
            If bytesTotal > 0 Then
                op.Message = $"{labelPhase}: {doneMb:F0} / {totalMb:F0} MB ({pct:F1}%)"
            Else
                op.Message = $"{labelPhase}: {doneMb:F0} MB"
            End If

            Return True
        End Function

        ''' <summary>
        ''' True for SteamCMD stdout lines that carry no useful
        ''' information for the user-facing message field —
        ''' decoration rows, blank lines, and the interactive
        ''' "-- type 'quit' to exit --" REPL prompt. Filtered
        ''' from the fallback Message setter in
        ''' SteamCmd_OutputDataReceived so they don't stick as the
        ''' last-shown message after a successful install.
        '''
        ''' The fallback path is what catches lines BEFORE any
        ''' structured progress source has set SteamCmdPhase, so
        ''' filtering here removes noise without losing the genuinely
        ''' useful early-stage messages ("Logging in user 'foo'...OK",
        ''' "Connecting anonymously to Steam Public...OK",
        ''' "Loading Steam API...OK") that operators rely on to see
        ''' that the install is actually progressing.
        ''' </summary>
        Private Shared Function IsSteamCmdNoiseLine(line As String) As Boolean
            If String.IsNullOrWhiteSpace(line) Then Return True

            Dim trimmed = line.Trim()

            ' Pure decoration row (e.g. a divider of dashes,
            ' equals, or underscores). Common between SteamCMD
            ' subsystem outputs and never carries info.
            Dim allDecoration = True
            For Each ch In trimmed
                If ch <> "-"c AndAlso ch <> "="c AndAlso ch <> "_"c AndAlso ch <> " "c Then
                    allDecoration = False
                    Exit For
                End If
            Next
            If allDecoration Then Return True

            ' Interactive REPL prompt — SteamCMD emits this as
            ' part of its command-loop machinery whether or not
            ' it's actually waiting for stdin. Shape is
            ' "-- ... --" wrapping one of type/quit/exit.
            If trimmed.StartsWith("--") AndAlso trimmed.EndsWith("--") Then
                If trimmed.IndexOf("quit", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   trimmed.IndexOf("exit", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   trimmed.IndexOf("type ", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    Return True
                End If
            End If

            Return False
        End Function

        ''' <summary>
        ''' Map SteamCMD's stdout phase strings ("downloading",
        ''' "verifying install", "verifying staged", "preallocating",
        ''' "validating", "committing", "staging", "reconfiguring")
        ''' to the same folded label set NormalizePhase produces
        ''' for content_log.txt, so the UI reads the same regardless
        ''' of which source set the phase. Unknown phases pass
        ''' through capitalised so an unfamiliar SteamCMD state
        ''' shows up as itself rather than disappearing.
        ''' </summary>
        Private Shared Function NormalizeStdoutPhase(phase As String) As String
            If String.IsNullOrEmpty(phase) Then Return Nothing
            Dim lower = phase.Trim().ToLowerInvariant()
            If lower.StartsWith("downloading") Then Return "Downloading"
            If lower.StartsWith("verifying") OrElse lower.StartsWith("validating") Then Return "Verifying"
            If lower.StartsWith("preallocating") Then Return "Preallocating"
            If lower.StartsWith("committing") Then Return "Committing"
            If lower.StartsWith("staging") Then Return "Staging"
            If lower.StartsWith("reconfiguring") Then Return "Reconfiguring"
            ' Capitalise first letter of the unknown phase so the
            ' UI label matches the convention of the known ones.
            Dim t = phase.Trim()
            If t.Length = 0 Then Return Nothing
            Return Char.ToUpperInvariant(t(0)) & t.Substring(1).ToLowerInvariant()
        End Function

        ''' <summary>
        ''' Outer tailer loop: ticks every 500ms, reads one chunk
        ''' of new content per tick via ReadAndApplyChunkOnceAsync,
        ''' exits when cancellation fires. IO failures (transient
        ''' file lock, short read) are caught and retried; only
        ''' OperationCanceledException terminates the loop.
        ''' </summary>
        Private Async Function RunSteamCmdContentLogTailerAsync(
                contentLogPath As String,
                startPosition As Long,
                op As ActiveOperation,
                cancellation As CancellationToken) As Task
            Dim position As Long = startPosition

            While Not cancellation.IsCancellationRequested
                Try
                    If File.Exists(contentLogPath) Then
                        position = Await ReadAndApplyChunkOnceAsync(
                            contentLogPath, position, op, cancellation)
                    End If
                Catch ex As OperationCanceledException
                    Return
                Catch ex As IOException
                    ' Transient — retry next tick.
                Catch ex As Exception
                    _logger.LogDebug(ex, "Content log tailer iteration error")
                End Try

                Try
                    Await Task.Delay(500, cancellation)
                Catch ex As OperationCanceledException
                    Return
                End Try
            End While
        End Function

        ''' <summary>
        ''' Single read pass: open the file, read up to 256KB of new
        ''' content from `position`, parse complete lines, apply each
        ''' to op via ApplyContentLogLineToOp, return the new position.
        '''
        ''' Only consumes up to the last newline in the chunk — if
        ''' SteamCMD is mid-write of a line at the moment we read,
        ''' the partial trailing data stays unconsumed and is
        ''' re-read on the next iteration once it's complete.
        '''
        ''' Truncation handling: if the file shrinks below the
        ''' tracked position, reset to 0 and re-read from the new
        ''' start. Catches the rare case where SteamCMD rotates the
        ''' log mid-install.
        ''' </summary>
        Private Async Function ReadAndApplyChunkOnceAsync(
                contentLogPath As String,
                position As Long,
                op As ActiveOperation,
                cancellation As CancellationToken) As Task(Of Long)

            Using fs As New FileStream(
                    contentLogPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite Or FileShare.Delete)
                If fs.Length < position Then position = 0
                If fs.Length <= position Then Return position

                fs.Position = position
                Dim available As Long = fs.Length - position
                Dim chunkSize As Integer = CInt(Math.Min(available, 262144))
                Dim buffer(chunkSize - 1) As Byte
                Dim bytesRead = Await fs.ReadAsync(buffer, 0, chunkSize, cancellation)
                If bytesRead <= 0 Then Return position

                Dim text = Encoding.UTF8.GetString(buffer, 0, bytesRead)
                Dim lastNewline = text.LastIndexOf(Chr(10))
                If lastNewline < 0 Then
                    ' No complete line in this chunk — wait for more.
                    Return position
                End If

                Dim complete = text.Substring(0, lastNewline + 1)
                Dim consumedBytes = Encoding.UTF8.GetByteCount(complete)

                For Each line In complete.Split(
                        {vbCrLf, vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
                    If cancellation.IsCancellationRequested Then Exit For
                    ApplyContentLogLineToOp(line, op)
                Next

                Return position + consumedBytes
            End Using
        End Function

        ''' <summary>
        ''' Parse one log line and apply any progress data to op.
        ''' Lines that match none of the regex patterns are silently
        ''' ignored — content_log.txt carries plenty of HTTP traffic,
        ''' depot manifest fetches, and other noise we don't try to
        ''' make sense of.
        '''
        ''' Updates op.SteamCmdPhase, op.BytesDownloaded,
        ''' op.BytesTotal, and op.Message. Does NOT update
        ''' op.ProgressPercent in this iteration — that's the disk
        ''' poller's job, since content_log alone has no continuous
        ''' bytes signal during the long Downloading phase.
        '''
        ''' Phase changes always log to Information (rare events,
        ''' valuable as alive-signal). Verify-progress lines log on
        ''' ≥1% movement to keep node log volume bounded.
        ''' </summary>
        Private Sub ApplyContentLogLineToOp(line As String, op As ActiveOperation)
            If String.IsNullOrEmpty(line) Then Return

            ' Defensive ANSI strip — content_log.txt is normally
            ' plain ASCII (Steam writes it directly to a file, not
            ' through a TTY), but stripping here costs nothing on
            ' the no-ESC fast-path and protects the regex match
            ' if a future SteamCMD release ever decides to colour
            ' its file logs.
            line = StripAnsi(line)

            ' Phase transition? Most common shape, check first.
            Dim m = s_PhaseChangeRegex.Match(line)
            If m.Success Then
                Dim newPhase = NormalizePhase(m.Groups(2).Value)
                If String.IsNullOrEmpty(newPhase) Then Return

                op.SteamCmdPhase = newPhase

                ' Verify-phase message gets formatted by the
                ' Verified-line handler below (with a byte count);
                ' other phases just show "<phase>..." until something
                ' more specific arrives.
                If Not newPhase.Equals("Verifying", StringComparison.OrdinalIgnoreCase) Then
                    op.Message = $"{newPhase}..."
                End If

                If Not String.Equals(newPhase, op.LastLoggedSteamCmdPhase, StringComparison.Ordinal) Then
                    _logger.LogInformation("SteamCMD phase: {Phase}", newPhase)
                    op.LastLoggedSteamCmdPhase = newPhase
                    op.LastLoggedSteamCmdPct = -100.0  ' reset percent throttle
                End If
                Return
            End If

            ' Total expected install size — emitted once near the
            ' start of the update.
            m = s_UpdateStartedRegex.Match(line)
            If m.Success Then
                Dim totalBytes As Long = 0
                If Long.TryParse(m.Groups(2).Value, totalBytes) AndAlso totalBytes > 0 Then
                    ' If multiple appIds emit "update started"
                    ' (the install pulls a small dependency
                    ' alongside the main app), keep the larger
                    ' total. Using the main app's stage size gives
                    ' a slightly-too-low percent on combined
                    ' downloads; small dependencies typically eat
                    ' under 2% of total install bytes so the error
                    ' is bounded.
                    If Not op.BytesTotal.HasValue OrElse totalBytes > op.BytesTotal.Value Then
                        op.BytesTotal = totalBytes
                        _logger.LogInformation(
                            "SteamCMD: total install size {TotalMb:F0} MB",
                            totalBytes / 1048576.0)
                    End If
                End If
                Return
            End If

            ' Verifying-phase progress.
            m = s_VerifiedRegex.Match(line)
            If m.Success Then
                Dim verifiedMb As Long = 0
                If Long.TryParse(m.Groups(1).Value, verifiedMb) Then
                    Dim verifiedBytes As Long = verifiedMb * 1048576L
                    op.BytesDownloaded = verifiedBytes

                    If op.BytesTotal.HasValue AndAlso op.BytesTotal.Value > 0 Then
                        Dim totalMb = op.BytesTotal.Value / 1048576.0
                        ' Verify percent reflects depot-cache
                        ' verification, which caps below 100% of
                        ' the install's stage total (often ≈30%
                        ' for a fresh install). Cap at 99% so the
                        ' bar doesn't claim completion before commit.
                        Dim pct = CDbl(verifiedBytes) / CDbl(op.BytesTotal.Value) * 100.0
                        If pct > 99.0 Then pct = 99.0
                        op.Message = $"Verifying: {verifiedMb} / {totalMb:F0} MB"

                        If Math.Abs(pct - op.LastLoggedSteamCmdPct) >= 1.0 Then
                            _logger.LogInformation(
                                "SteamCMD: verifying {Verified} / {Total:F0} MB ({Pct:F1}%)",
                                verifiedMb, totalMb, pct)
                            op.LastLoggedSteamCmdPct = pct
                        End If
                    Else
                        op.Message = $"Verifying: {verifiedMb} MB"
                    End If
                End If
                Return
            End If
        End Sub

        ''' <summary>
        ''' Reduce SteamCMD's phase-list strings to a short user-
        ''' facing label. Skips the always-present "Running Update"
        ''' envelope, picks the first non-empty secondary phase, and
        ''' folds the multi-variant Verifying states ("Verifying
        ''' Installed", "Verifying Staged") into a single "Verifying"
        ''' so the UI doesn't flicker between them.
        '''
        ''' Returns Nothing for transient empty-list states. Those
        ''' show up between secondary-phase changes ("Running Update,"
        ''' alone) and we want to keep the previous phase displayed
        ''' across them rather than blinking back to a blank state.
        ''' Also returns Nothing for "None" — emitted at the very
        ''' end after the install completes; the install-runner
        ''' state machine handles the completion transition itself.
        ''' </summary>
        Private Shared Function NormalizePhase(phaseList As String) As String
            If String.IsNullOrEmpty(phaseList) Then Return Nothing
            Dim trimmed = phaseList.TrimEnd(","c).Trim()
            If String.IsNullOrEmpty(trimmed) Then Return Nothing
            If String.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase) Then Return Nothing

            For Each rawPart In trimmed.Split(","c)
                Dim part = rawPart.Trim()
                If String.IsNullOrEmpty(part) Then Continue For
                If String.Equals(part, "Running Update", StringComparison.OrdinalIgnoreCase) Then Continue For

                ' Fold Verifying variants into one stable label so
                ' the UI doesn't flicker between Verifying Installed
                ' / Verifying Staged on a single install.
                If part.StartsWith("Verifying", StringComparison.OrdinalIgnoreCase) Then
                    Return "Verifying"
                End If
                Return part
            Next

            Return Nothing
        End Function

        ''' <summary>
        ''' Process I/O counter poller — fills in the smooth-
        ''' progress gap during the Downloading phase by reading
        ''' the kernel's per-process WriteTransferCount via the
        ''' Win32 GetProcessIoCounters API.
        '''
        ''' Why I/O counters and not file/directory size: SteamCMD
        ''' preallocates the full install size on disk before any
        ''' download starts (visible as the "Preallocating" phase),
        ''' and the preallocation isn't sparse — the disk extents
        ''' are real, zero-filled. As Steam fetches chunks it
        ''' overwrites those zeros in place, so file sizes don't
        ''' grow during Downloading. Both FileInfo.Length and the
        ''' sparse-aware GetCompressedFileSize see the full
        ''' preallocated total from phase entry onward.
        '''
        ''' GetProcessIoCounters reports a different number: every
        ''' byte the process has handed to WriteFile (and similar)
        ''' since it started. Writes that go into preallocated
        ''' extents — invisible to size queries — are counted here.
        ''' That's our continuous signal.
        '''
        ''' Baseline subtraction: we capture the WriteTransferCount
        ''' at the moment the phase first becomes Downloading and
        ''' subtract it from each subsequent reading. This excludes
        ''' the writes from earlier phases (login, manifest setup,
        ''' etc.) so the byte count starts at zero when downloading
        ''' begins. Baseline is captured once and never reset; on a
        ''' multi-pass update where Downloading recurs, we keep
        ''' counting from the original baseline so the displayed
        ''' total matches op.BytesTotal which is also cumulative.
        '''
        ''' Active only while op.SteamCmdPhase = "Downloading".
        ''' Other phases own op.Message and have their own data
        ''' sources (the content_log Verified-line handler for
        ''' Verifying, the phase-change handler for Committing).
        ''' Phase check is per-tick rather than CTS-gated so the
        ''' poller re-engages cleanly if the phase ever returns to
        ''' Downloading on a multi-pass update.
        '''
        ''' Slight over-count caveat: WriteTransferCount also
        ''' includes log writes, .acf updates, and .patch state
        ''' writes during Downloading. These are kilobytes per
        ''' second against a download stream measured in megabytes
        ''' per second — sub-1% noise floor, won't move the percent
        ''' display.
        '''
        ''' 1Hz cadence is plenty — humans don't perceive faster,
        ''' the syscall is microseconds, and the throttle on the
        ''' LogInformation call keeps log volume bounded.
        ''' Cancellation observed via Task.Delay; same lifecycle as
        ''' the content_log tailer (CTS shared between them so
        ''' cancellation in the Finally hits both).
        ''' </summary>
        Private Async Function RunDownloadProgressPollerAsync(
                proc As Process,
                op As ActiveOperation,
                cancellation As CancellationToken) As Task

            While Not cancellation.IsCancellationRequested
                Try
                    If String.Equals(op.SteamCmdPhase, "Downloading",
                                       StringComparison.OrdinalIgnoreCase) Then
                        Dim totalWritten = TryGetProcessWriteBytes(proc)
                        If totalWritten >= 0 Then
                            ' Set baseline once on the first Downloading
                            ' tick. ActiveOperation.WriteTransferBaseline
                            ' is a Friend nullable Long; defaults to
                            ' Nothing on a fresh op.
                            If Not op.WriteTransferBaseline.HasValue Then
                                op.WriteTransferBaseline = totalWritten
                            End If

                            Dim downloadedBytes = totalWritten - op.WriteTransferBaseline.Value
                            If downloadedBytes < 0 Then downloadedBytes = 0

                            ' Only adopt our derived value when it
                            ' would advance op.BytesDownloaded. We
                            ' share that field with the stdout parser
                            ' (ApplyStdoutProgressLineToOp), which on
                            ' Linux is the authoritative dense source
                            ' — line-buffered stdout carries SteamCMD's
                            ' per-second progress reports there.
                            ' /proc/<pid>/io's wchar counter doesn't
                            ' reliably track SteamCMD's mmap-based
                            ' writes on Linux until page-cache flushes
                            ' catch up, which leaves it lagging actual
                            ' progress badly during early Downloading.
                            ' Without this guard the I/O poller
                            ' perpetually overwrote the parser's
                            ' correct values with stale near-zero
                            ' deltas and the bar sat at "0 / N MB
                            ' (0.0%)" until wchar caught up around the
                            ' 50% mark. Skipping the write when our
                            ' value is behind lets whichever source is
                            ' denser win on each platform without a
                            ' platform branch.
                            If Not op.BytesDownloaded.HasValue OrElse
                               downloadedBytes > op.BytesDownloaded.Value Then
                                op.BytesDownloaded = downloadedBytes

                                If op.BytesTotal.HasValue AndAlso op.BytesTotal.Value > 0 Then
                                    Dim totalMb = op.BytesTotal.Value / 1048576.0
                                    Dim doneMb = downloadedBytes / 1048576.0
                                    Dim pct = CDbl(downloadedBytes) / CDbl(op.BytesTotal.Value) * 100.0
                                    ' Cap at 99% — the phase transition
                                    ' to Verifying is what officially
                                    ' marks download completion. A bar
                                    ' that hits 100% but stays in
                                    ' "Downloading" until the phase flips
                                    ' reads as stuck.
                                    If pct > 99.0 Then pct = 99.0

                                    op.Message = $"Downloading: {doneMb:F0} / {totalMb:F0} MB ({pct:F1}%)"

                                    ' Weighted overall progress — the
                                    ' helper handles the no-steps fallback
                                    ' (returns within-step pct) and the
                                    ' 0/negative-weight defensive cases.
                                    op.ProgressPercent = ComputeWeightedProgress(op, pct)

                                    If Math.Abs(pct - op.LastLoggedSteamCmdPct) >= 1.0 Then
                                        _logger.LogInformation(
                                            "SteamCMD: downloading {DoneMb:F0} / {TotalMb:F0} MB ({Pct:F1}%)",
                                            doneMb, totalMb, pct)
                                        op.LastLoggedSteamCmdPct = pct
                                    End If
                                Else
                                    ' Total not yet captured by the
                                    ' content_log tailer ("update started"
                                    ' hasn't been parsed). Show a bytes-
                                    ' only message until it lands.
                                    op.Message = $"Downloading: {downloadedBytes / 1048576.0:F0} MB"
                                End If
                            End If
                        End If
                    End If
                Catch ex As Exception
                    ' Best-effort — process may have exited between
                    ' our handle check and the API call, and a
                    ' transient failure shouldn't kill the poller.
                    ' Don't log per-tick failures (would flood at
                    ' 1Hz on any persistent issue).
                End Try

                Try
                    Await Task.Delay(1000, cancellation)
                Catch ex As OperationCanceledException
                    Return
                End Try
            End While
        End Function

        ''' <summary>
        ''' Read the cumulative bytes-written-by-the-process counter
        ''' for the given process. On Windows that's the
        ''' WriteTransferCount field of the IO_COUNTERS struct
        ''' returned by GetProcessIoCounters — a kernel-maintained
        ''' tally of every byte the process has handed to WriteFile
        ''' (and similar) since it started. On Linux we read the
        ''' write_bytes field of /proc/&lt;pid&gt;/io which is the
        ''' equivalent kernel counter.
        '''
        ''' Returns -1 when the value can't be obtained (process
        ''' exited, handle invalid, file vanished, etc.) so callers
        ''' can skip the tick rather than treating absence as zero.
        ''' </summary>
        Private Shared Function TryGetProcessWriteBytes(proc As Process) As Long
            If proc Is Nothing Then Return -1

            Try
                If proc.HasExited Then Return -1
            Catch
                Return -1
            End Try

            If OperatingSystem.IsWindows() Then
                Try
                    Dim ioCounters As IO_COUNTERS
                    If GetProcessIoCounters(proc.Handle, ioCounters) Then
                        ' WriteTransferCount is ULONGLONG. Realistic
                        ' install bytes top out around tens of GB,
                        ' nowhere near Long.MaxValue (~9 EB), so the
                        ' signed-cast is safe.
                        Return CLng(ioCounters.WriteTransferCount)
                    End If
                Catch
                End Try
                Return -1
            End If

            ' Linux fallback: read /proc/<pid>/io. Format is
            ' line-oriented "key: value". We want wchar (bytes the
            ' process has handed to write(2) syscalls) rather than
            ' write_bytes (bytes actually flushed to the storage
            ' layer). The two differ because Linux's page cache
            ' holds dirty pages and writeback runs every ~5s by
            ' default — write_bytes lags behind wchar in bursts and
            ' would produce visible 5-second stalls in the progress
            ' display. wchar is the closer semantic analog to
            ' Windows' WriteTransferCount which also counts at
            ' write-syscall time, not disk-flush time.
            '
            ' Requires CONFIG_TASK_IO_ACCOUNTING in the kernel,
            ' which is enabled on every mainstream distro. If the
            ' file doesn't exist (or wchar line is missing) we
            ' return -1 and the poller skips the tick — percent
            ' display will be missing during Downloading but no
            ' worse than that.
            '
            ' Permission: /proc/<pid>/io requires PTRACE_MODE_READ
            ' (same euid or CAP_SYS_PTRACE). Since SteamCMD is our
            ' direct child it inherits our uid, so the read always
            ' succeeds for our own spawned processes.
            Try
                Dim ioPath = $"/proc/{proc.Id}/io"
                If Not File.Exists(ioPath) Then Return -1
                For Each line In File.ReadLines(ioPath)
                    If line.StartsWith("wchar:") Then
                        Dim colonIdx = line.IndexOf(":"c)
                        If colonIdx >= 0 AndAlso colonIdx < line.Length - 1 Then
                            Dim val As Long = 0
                            If Long.TryParse(line.Substring(colonIdx + 1).Trim(), val) Then
                                Return val
                            End If
                        End If
                    End If
                Next
            Catch
            End Try
            Return -1
        End Function

        ' Win32 GetProcessIoCounters — returns a kernel-maintained
        ' tally of bytes read/written by the process since it
        ' started. Requires PROCESS_QUERY_INFORMATION (or _LIMITED_
        ' INFORMATION on Vista+) on the handle; the handle returned
        ' by Process.Start has all access by default, so we're fine.
        '
        ' IO_COUNTERS layout is documented as 6 contiguous ULONGLONG
        ' fields with no padding. Sequential layout matches.
        <StructLayout(LayoutKind.Sequential)>
        Private Structure IO_COUNTERS
            Public ReadOperationCount As ULong
            Public WriteOperationCount As ULong
            Public OtherOperationCount As ULong
            Public ReadTransferCount As ULong
            Public WriteTransferCount As ULong
            Public OtherTransferCount As ULong
        End Structure

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function GetProcessIoCounters(
                hProcess As IntPtr,
                ByRef lpIoCounters As IO_COUNTERS) As Boolean
        End Function

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

        ''' <summary>
        ''' On Linux, ensures ~/.steam/sdk64/steamclient.so is a
        ''' symlink to SteamCMD's bundled steamclient.so. Required
        ''' for any Steamworks-using dedicated server launched with
        ''' -force_steamclient_link — the SDK does dlopen("~/.steam/
        ''' sdk64/steamclient.so") at SteamAPI_Init, and SteamCMD
        ''' ships the .so under its own linux64/ subdirectory but
        ''' doesn't put it where the SDK looks.
        '''
        ''' UE4 servers (Last Oasis, ARK, Squad, etc.) hard-fail
        ''' SteamAPI_Init without this and exit before completing
        ''' engine init. Without the symlink the failure mode is
        ''' the dlopen "cannot open shared object file" error in
        ''' the server's stderr followed by a fast crash; the
        ''' crash-restart loop then halts after MaxCrashCount
        ''' attempts.
        '''
        ''' Idempotent and best-effort:
        '''   - No-op on Windows (the equivalent there is
        '''     steamclient.dll on PATH, which Steam handles).
        '''   - No-op if the SteamCMD-side .so isn't present (a
        '''     fresh SteamCMD install hasn't unpacked linux64/
        '''     yet — will be retried on the next SteamCMD run).
        '''   - No-op if HOME isn't set (rare — systemd User= units
        '''     get HOME populated automatically).
        '''   - If the destination already points at the right
        '''     source, leaves it alone. If it points elsewhere
        '''     (or is a stale regular file from a copy-based
        '''     setup), replaces it.
        '''
        ''' Failures here log a warning but do NOT throw — the rest
        ''' of the install is still valuable, and a missing symlink
        ''' only matters for games that consume the SteamSDK at
        ''' runtime. Games that don't (like Factorio) install fine
        ''' regardless.
        ''' </summary>
        Private Sub EnsureSteamClientSdkSymlink(steamCmdPath As String)
            If Not OperatingSystem.IsLinux() Then Return

            Try
                Dim steamCmdDir = Path.GetDirectoryName(steamCmdPath)
                If String.IsNullOrEmpty(steamCmdDir) Then Return

                Dim sourcePath = Path.Combine(steamCmdDir, "linux64", "steamclient.so")
                If Not File.Exists(sourcePath) Then
                    _logger.LogDebug(
                        "steamclient.so not found at {Path}; SDK symlink skipped (will retry next install)",
                        sourcePath)
                    Return
                End If

                Dim home = Environment.GetEnvironmentVariable("HOME")
                If String.IsNullOrEmpty(home) Then
                    _logger.LogDebug("$HOME not set; SDK symlink skipped")
                    Return
                End If

                Dim sdkDir = Path.Combine(home, ".steam", "sdk64")
                Dim destPath = Path.Combine(sdkDir, "steamclient.so")

                Directory.CreateDirectory(sdkDir)

                ' If the link already points to the right place,
                ' nothing to do. FileSystemInfo.LinkTarget returns
                ' the link's target string (or Nothing for a
                ' regular file); compare directly. A wrong target
                ' or a regular-file copy from a previous setup
                ' gets replaced.
                If File.Exists(destPath) OrElse Directory.Exists(destPath) Then
                    Try
                        Dim existing As New FileInfo(destPath)
                        If String.Equals(existing.LinkTarget, sourcePath, StringComparison.Ordinal) Then
                            _logger.LogDebug(
                                "steamclient.so symlink already points at {Source}", sourcePath)
                            Return
                        End If
                    Catch
                        ' Fall through to the replace path.
                    End Try
                    Try
                        File.Delete(destPath)
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "Could not remove stale {Path}; symlink not updated", destPath)
                        Return
                    End Try
                End If

                File.CreateSymbolicLink(destPath, sourcePath)
                _logger.LogInformation(
                    "Created Steam SDK symlink: {Dest} -> {Source}", destPath, sourcePath)
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Could not set up steamclient.so symlink — UE4 servers may fail SteamAPI_Init at runtime")
            End Try
        End Sub

        ''' <summary>
        ''' After a SteamCMD install, many Steam games ship VC++ redistributables,
        ''' DirectX, and .NET runtimes under _CommonRedist. Steam doesn't run
        ''' these for dedicated server installs, so we run them ourselves.
        ''' All common redists accept /quiet /norestart or /silent.
        ''' Idempotent — safe to run repeatedly.
        ''' </summary>
        Private Async Function RunCommonRedistAsync(op As ActiveOperation,
                                                     cancellation As CancellationToken) As Task
            If Not OperatingSystem.IsWindows() Then Return

            Dim installPath = Path.GetFullPath(op.InstallPath).TrimEnd("\"c, "/"c)
            Dim redistRoot = Path.Combine(installPath, "_CommonRedist")
            If Not Directory.Exists(redistRoot) Then
                _logger.LogInformation("No _CommonRedist folder at {Path}, skipping redist install", redistRoot)
                Return
            End If

            op.State = InstallationOperationState.Configuring
            op.Message = "Installing redistributables..."
            _logger.LogInformation("Running redistributables from {Path}", redistRoot)

            Dim installedCount = 0
            For Each exePath In Directory.EnumerateFiles(redistRoot, "*.exe", SearchOption.AllDirectories)
                cancellation.ThrowIfCancellationRequested()

                Dim name = Path.GetFileName(exePath)
                Dim nameLower = name.ToLowerInvariant()

                ' DirectX setup uses /silent; most others accept /install /quiet /norestart
                Dim silentArgs As String
                If nameLower.Contains("dxsetup") Then
                    silentArgs = "/silent"
                ElseIf nameLower.Contains("vcredist") OrElse nameLower.StartsWith("vc_redist") Then
                    silentArgs = "/install /quiet /norestart"
                Else
                    silentArgs = "/quiet /norestart"
                End If

                op.Message = $"Installing {name}..."
                _logger.LogInformation("Running redist: {Exe} {Args}", exePath, silentArgs)

                Try
                    Dim psi As New ProcessStartInfo(exePath, silentArgs)
                    psi.UseShellExecute = False
                    psi.CreateNoWindow = True
                    psi.WorkingDirectory = Path.GetDirectoryName(exePath)

                    Using p = Process.Start(psi)
                        ' 5-minute timeout per redist. Linked with the
                        ' outer install cancellation so if the user
                        ' cancels the whole install mid-redist we bail
                        ' promptly instead of sitting on WaitForExit.
                        Dim timedOut = False
                        Try
                            Using timeoutCts As New CancellationTokenSource(300000)
                                Using linked = CancellationTokenSource.
                                        CreateLinkedTokenSource(cancellation, timeoutCts.Token)
                                    Await p.WaitForExitAsync(linked.Token)
                                End Using
                            End Using
                        Catch ex As OperationCanceledException
                            Try : p.Kill(entireProcessTree:=True) : Catch : End Try
                            ' Outer cancel? Propagate. Timeout? Log and move on.
                            If cancellation.IsCancellationRequested Then
                                Throw
                            End If
                            timedOut = True
                        End Try

                        If timedOut Then
                            _logger.LogWarning("Redist {Exe} timed out after 5 minutes", name)
                            Continue For
                        End If

                        ' Exit codes meaning "success" or "already installed":
                        '   0    = success
                        '   1638 = newer version already installed
                        '   3010 = success, reboot required
                        '   5100 = system requirements not met (skip, not fatal)
                        Select Case p.ExitCode
                            Case 0, 1638, 3010
                                installedCount += 1
                                _logger.LogInformation("Redist {Exe} installed (code {Code})", name, p.ExitCode)
                            Case 5100
                                _logger.LogInformation("Redist {Exe} skipped: system requirements not met", name)
                            Case Else
                                _logger.LogWarning("Redist {Exe} returned code {Code}", name, p.ExitCode)
                        End Select
                    End Using
                Catch ex As Exception
                    _logger.LogWarning(ex, "Failed to run redist {Exe}", name)
                End Try
            Next

            _logger.LogInformation("Redistributable install complete ({Count} processed)", installedCount)
        End Function

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
                    Dim content = File.ReadAllText(destPath)
                    File.Delete(destPath)
                    Throw New Exception($"Download appears to be an error page ({fileInfo.Length} bytes): {content.Substring(0, Math.Min(200, content.Length))}")
                End If

                _logger.LogInformation("Extracting archive: {File}", destPath)

                Dim isTarXz = destPath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) OrElse
                              destPath.EndsWith(".txz", StringComparison.OrdinalIgnoreCase)

                ' Native tar on Linux/macOS handles every tar variant
                ' correctly — GNU long-name extensions, Pax extended
                ' headers (BSD tar's variant included), and unix file
                ' modes. SharpCompress 0.36.0 doesn't process Pax
                ' headers in the BSD-tar format Factorio's build
                ' pipeline emits, so any entry with a path longer
                ' than the 100-char standard tar name limit lands
                ' on disk with its name truncated at boundary 100
                ' (and the full name silently dropped). Symptom in
                ' practice: "rail-chain-signal-elevated.lua" lands
                ' as "rail-chain-signal-elevated.l", elevated-rails
                ' fails to load via require(), engine exits during
                ' map generation. Native tar reads the Pax records
                ' and applies the long names; nothing else does.
                '
                ' --strip-components=1 collapses the top-level
                ' wrapper directory in one flag, replacing the
                ' staging-and-hoist dance the SharpCompress branch
                ' below has to do manually. Permissions on the
                ' executable bit also round-trip correctly without
                ' our ApplyUnixModeIfNeeded helper because tar
                ' applies the mode field directly during extraction.
                '
                ' Linux + macOS only — Windows tar.xz still routes
                ' through SharpCompress because we don't have a
                ' direct-download path that hits a Windows node
                ' yet. If that changes, the equivalent on modern
                ' Windows is the bundled bsdtar ("tar -xf" works
                ' for .tar.xz since Windows 10 1803).
                If isTarXz AndAlso (OperatingSystem.IsLinux() OrElse OperatingSystem.IsMacOS()) Then
                    Await ExtractWithNativeTarAsync(
                        destPath, op.InstallPath,
                        dlStep.StripTopLevelDirectory, cancellation)
                    _logger.LogInformation("Extraction complete")
                    File.Delete(destPath)
                    Return
                End If

                ' SharpCompress fallback path — used for .zip, for
                ' Windows .tar.xz, and for any other archive format
                ' the plugin throws at us. The remainder of this
                ' function is the previous extraction implementation
                ' with its staging-and-hoist machinery; the native-
                ' tar branch above sidesteps it for the Linux
                ' direct-download case where SharpCompress's pax
                ' bug bites.

                ' When the plugin asks for the top-level directory
                ' to be stripped (Factorio's headless tarball ships
                ' everything under "factorio/", and the rest of
                ' PowerGSM expects entries directly under the
                ' install root), extract to a staging directory
                ' first and then hoist contents up. The two-stage
                ' approach keeps the inspection logic separate from
                ' the per-format extraction logic, and Directory.
                ' Move on the same volume is a near-free metadata
                ' op so the doubled work is cheap.
                '
                ' staging is created under op.InstallPath rather
                ' than under TEMP so File.Move at hoist time stays
                ' on the same volume — a cross-volume Move would
                ' silently degrade to copy + delete and the cost
                ' becomes O(archive size) instead of O(entries).
                Dim extractDest = op.InstallPath
                Dim staging As String = Nothing
                If dlStep.StripTopLevelDirectory Then
                    staging = Path.Combine(op.InstallPath, ".gsm-staging")
                    Try
                        If Directory.Exists(staging) Then
                            Directory.Delete(staging, recursive:=True)
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "Could not clear staging dir {Dir}; extraction may pick up stale files",
                            staging)
                    End Try
                    Directory.CreateDirectory(staging)
                    extractDest = staging
                End If

                Try
                    If destPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then
                        ' Zip uses System.IO.Compression. Pax headers
                        ' don't exist in the zip format, so no entry
                        ' filtering needed here.
                        IO.Compression.ZipFile.ExtractToDirectory(destPath, extractDest, True)
                    ElseIf destPath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) OrElse
                           destPath.EndsWith(".txz", StringComparison.OrdinalIgnoreCase) Then
                        ' SharpCompress's ArchiveFactory.Open doesn't
                        ' auto-detect XZ — the supported-format error
                        ' lists only Zip / Rar / 7Zip / GZip / Tar.
                        ' XZ is streaming-only (no central directory),
                        ' so we wrap the FileStream in an XZStream
                        ' decompressor and feed the resulting tar bytes
                        ' into TarReader. This is structurally what
                        ' `tar -xJf` does on Linux.
                        '
                        ' Forward-only streaming, hence TarReader
                        ' (which advances entry-by-entry) rather than
                        ' TarArchive (which assumes seekable input).
                        ' Factorio's headless Linux build ships as
                        ' tar.xz; this branch is what makes the direct
                        ' download path work for it.
                        Dim opts As New ExtractionOptions() With {
                            .ExtractFullPath = True,
                            .Overwrite = True
                        }
                        Using fs As FileStream = File.OpenRead(destPath)
                            Using xz As New XZStream(fs)
                                Using reader = TarReader.Open(xz)
                                    While reader.MoveToNextEntry()
                                        If reader.Entry.IsDirectory Then Continue While
                                        ' Filter Pax extended-header
                                        ' pseudo-entries. SharpCompress
                                        ' 0.36.0 normally consumes these
                                        ' internally, but BSD tar (which
                                        ' Factorio's build pipeline
                                        ' appears to use) emits a
                                        ' top-level "@PaxHeader" entry
                                        ' that slips through and lands
                                        ' on disk as a junk file.
                                        If IsPaxHeaderEntryKey(reader.Entry.Key) Then Continue While

                                        ' Snapshot the entry's path and
                                        ' mode BEFORE WriteEntryToDirectory
                                        ' — once the reader advances past
                                        ' the entry these properties are
                                        ' no longer reliable.
                                        Dim entryKey = reader.Entry.Key
                                        Dim entryMode = reader.Entry.Mode

                                        reader.WriteEntryToDirectory(extractDest, opts)

                                        ApplyUnixModeIfNeeded(extractDest, entryKey, entryMode)
                                    End While
                                End Using
                            End Using
                        End Using
                    Else
                        ' Use SharpCompress for everything else
                        ' (tar.gz, tgz, 7z, rar, etc.). XZ would land
                        ' here too if the factory could detect it — the
                        ' explicit branch above exists because it can't.
                        Using archive = ArchiveFactory.Open(destPath)
                            Dim opts As New ExtractionOptions() With {
                                .ExtractFullPath = True,
                                .Overwrite = True
                            }
                            For Each entry In archive.Entries
                                If entry.IsDirectory Then Continue For
                                If IsPaxHeaderEntryKey(entry.Key) Then Continue For
                                entry.WriteToDirectory(extractDest, opts)
                            Next
                        End Using
                    End If

                    ' Hoist staged contents into op.InstallPath if
                    ' the strip-top-level path was requested.
                    If staging IsNot Nothing Then
                        HoistStagedContents(staging, op.InstallPath)
                    End If
                Finally
                    ' Best-effort staging cleanup. HoistStagedContents
                    ' should have emptied staging already; this guards
                    ' against a partial-failure path leaving junk on
                    ' disk under the install root.
                    If staging IsNot Nothing Then
                        Try
                            If Directory.Exists(staging) Then
                                Directory.Delete(staging, recursive:=True)
                            End If
                        Catch ex As Exception
                            _logger.LogWarning(ex,
                                "Could not remove staging dir {Dir}; leftover files may remain",
                                staging)
                        End Try
                    End If
                End Try

                _logger.LogInformation("Extraction complete")
                File.Delete(destPath)
            End If
        End Function

        ''' <summary>
        ''' Run the platform's native tar binary to extract a
        ''' .tar.xz archive into the given destination. Used in
        ''' preference to SharpCompress on Linux and macOS
        ''' because SharpCompress 0.36.0's Pax-extended-header
        ''' handling drops long filenames for BSD-tar-produced
        ''' archives (Factorio's build pipeline produces these),
        ''' silently truncating any path over 100 chars. Native
        ''' tar reads the Pax records correctly and applies the
        ''' long names.
        '''
        ''' Behaviour matches the SharpCompress path:
        '''   - extracts directly into destDir
        '''   - if stripTopLevel is True, applies
        '''     --strip-components=1 to drop the archive's
        '''     wrapper directory (Factorio's tarball wraps
        '''     everything in "factorio/")
        '''   - preserves unix permissions from the archive
        '''     (the executable bit on bin/x64/factorio survives
        '''     without our ApplyUnixModeIfNeeded shim)
        '''   - merges with existing files at destDir, overwriting
        '''     conflicts (matches MergeDirectoryRecursive's
        '''     behaviour for the update flow)
        '''
        ''' Process management mirrors RunSteamCmdProcessAsync:
        ''' redirected stdout/stderr drained via DataReceived
        ''' handlers so a chatty extractor can't fill the pipe
        ''' buffer; HasExited polled rather than
        ''' WaitForExitAsync because the latter deadlocks on
        ''' redirected streams in .NET 8.
        '''
        ''' Throws on non-zero exit. The caller's outer try
        ''' surfaces the message into the install-error response,
        ''' which the manager renders verbatim to the user.
        ''' </summary>
        Private Async Function ExtractWithNativeTarAsync(archivePath As String,
                                                          destDir As String,
                                                          stripTopLevel As Boolean,
                                                          cancellation As CancellationToken) As Task
            ' Make sure the destination exists before tar tries to
            ' chdir into it (tar will create files inside it but
            ' won't create the directory itself unless it's there).
            Directory.CreateDirectory(destDir)

            Dim psi As New ProcessStartInfo()
            psi.FileName = "tar"
            ' ArgumentList passes each arg as a separate argv entry,
            ' so spaces or special chars in archivePath/destDir
            ' don't need quoting. -x extract, -J xz decompress,
            ' -f file, -C chdir-to-target.
            psi.ArgumentList.Add("-xJf")
            psi.ArgumentList.Add(archivePath)
            psi.ArgumentList.Add("-C")
            psi.ArgumentList.Add(destDir)
            If stripTopLevel Then
                psi.ArgumentList.Add("--strip-components=1")
            End If
            psi.UseShellExecute = False
            psi.CreateNoWindow = True
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True

            Dim outputBuilder As New StringBuilder()
            Dim outputLock As New Object()

            Using proc As New Process() With {.StartInfo = psi}
                AddHandler proc.OutputDataReceived,
                    Sub(sender, args)
                        If args.Data IsNot Nothing Then
                            SyncLock outputLock
                                outputBuilder.AppendLine(args.Data)
                            End SyncLock
                        End If
                    End Sub
                AddHandler proc.ErrorDataReceived,
                    Sub(sender, args)
                        If args.Data IsNot Nothing Then
                            SyncLock outputLock
                                outputBuilder.AppendLine(args.Data)
                            End SyncLock
                        End If
                    End Sub

                If Not proc.Start() Then
                    Throw New InvalidOperationException(
                        "Could not start native tar; verify it's on PATH.")
                End If
                proc.BeginOutputReadLine()
                proc.BeginErrorReadLine()

                _logger.LogInformation(
                    "Native tar extracting {Archive} -> {Dest} (strip-top={Strip})",
                    archivePath, destDir, stripTopLevel)

                ' HasExited polling — same pattern as
                ' RunSteamCmdProcessAsync. WaitForExitAsync
                ' deadlocks waiting for the redirected streams to
                ' close, which doesn't happen until we've drained
                ' them; HasExited returns True the moment the
                ' process terminates regardless of stream state.
                While Not proc.HasExited
                    If cancellation.IsCancellationRequested Then
                        Try
                            proc.Kill(entireProcessTree:=True)
                        Catch
                        End Try
                        cancellation.ThrowIfCancellationRequested()
                    End If
                    Await Task.Delay(100, cancellation)
                End While

                ' Brief flush wait so the trailing stderr from a
                ' failed extraction makes it into outputBuilder
                ' before we read it for the error message.
                Try
                    proc.WaitForExit(2000)
                Catch
                End Try

                If proc.ExitCode <> 0 Then
                    Dim tail = outputBuilder.ToString().Trim()
                    If tail.Length > 1024 Then
                        tail = tail.Substring(tail.Length - 1024)
                    End If
                    Throw New Exception(
                        $"Native tar exited with code {proc.ExitCode}: {tail}")
                End If
            End Using
        End Function

        ''' <summary>
        ''' Apply a tar entry's unix permission bits to the
        ''' on-disk file SharpCompress just wrote. No-op on
        ''' Windows (NTFS doesn't have unix modes; UnixFileMode
        ''' would throw PlatformNotSupportedException there).
        '''
        ''' WriteEntryToDirectory in SharpCompress 0.36.0 honours
        ''' the entry's mtime but not its mode — it creates the
        ''' file with the process default (umask-influenced, so
        ''' typically 0644 with no execute bit). For executable
        ''' content this is fatal: Factorio's headless binary
        ''' ships as 0755, lands on disk as 0664, and Process.Start
        ''' against it fails with errno 13 (EACCES) before the
        ''' game ever starts loading.
        '''
        ''' Mask is the lower 12 bits (0o7777) so suid/sgid/sticky
        ''' bits round-trip if the archive set them. Higher bits
        ''' (file type) are 0 in tar's mode field by spec, but the
        ''' mask is defensive.
        '''
        ''' Failures here log a warning but do not throw. A
        ''' permission write that fails after the file content
        ''' itself extracted successfully is recoverable — the
        ''' user can chmod by hand if needed — and a hard fail
        ''' here would abort the whole install for what's almost
        ''' always a transient FS quirk.
        ''' </summary>
        Private Sub ApplyUnixModeIfNeeded(extractDest As String,
                                            entryKey As String,
                                            entryMode As Integer)
            If OperatingSystem.IsWindows() Then Return
            If String.IsNullOrEmpty(entryKey) Then Return
            If entryMode <= 0 Then Return

            Dim destFilePath = Path.Combine(
                extractDest,
                entryKey.Replace("/"c, Path.DirectorySeparatorChar))

            Try
                Dim mode As UnixFileMode = CType(entryMode And &HFFF, UnixFileMode)
                File.SetUnixFileMode(destFilePath, mode)
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to apply unix mode {Mode} to {Path}; executable bits may be lost",
                    entryMode, destFilePath)
            End Try
        End Sub

        ''' <summary>
        ''' True if the archive entry is a Pax extended-header
        ''' pseudo-entry rather than a real file. Pax headers
        ''' carry per-entry attributes (long filenames, large
        ''' sizes, atime/mtime) that follow the standard ustar
        ''' header. SharpCompress normally consumes these and
        ''' applies them to the next entry, but BSD tar
        ''' implementations (notably Apple's bsdtar / libarchive,
        ''' which Factorio's build appears to use) sometimes emit
        ''' a standalone "@PaxHeader" entry that the reader treats
        ''' as a regular file — result: a garbage file appears in
        ''' the install root.
        '''
        ''' Filter matches:
        '''   - any path component literally "PaxHeader" or "@PaxHeader"
        '''   - any path component starting with "PaxHeaders"
        '''     (GNU tar's "PaxHeaders/<name>" form)
        '''
        ''' Match is case-insensitive even on Linux because the
        ''' header name is a tar-format convention, not a path
        ''' the user controls. False on null/empty.
        ''' </summary>
        Private Shared Function IsPaxHeaderEntryKey(entryKey As String) As Boolean
            If String.IsNullOrEmpty(entryKey) Then Return False
            Dim segments = entryKey.Split({"/"c, "\"c}, StringSplitOptions.RemoveEmptyEntries)
            For Each segment In segments
                If segment.Equals("PaxHeader", StringComparison.OrdinalIgnoreCase) Then Return True
                If segment.Equals("@PaxHeader", StringComparison.OrdinalIgnoreCase) Then Return True
                If segment.StartsWith("PaxHeaders", StringComparison.OrdinalIgnoreCase) Then Return True
                If segment.StartsWith("@PaxHeader", StringComparison.OrdinalIgnoreCase) Then Return True
            Next
            Return False
        End Function

        ''' <summary>
        ''' After extracting to a staging directory, promote its
        ''' contents into the final install path. Two cases:
        '''
        '''   1. Staging contains a single top-level directory —
        '''      hoist that directory's contents up one level
        '''      (the common autotools-style tarball case).
        '''
        '''   2. Staging contains multiple top-level entries —
        '''      copy them up as-is. The plugin asked for stripping
        '''      but the archive doesn't actually have a single
        '''      wrapper directory; we honour the spirit of the
        '''      request (extract to install root) without
        '''      arbitrarily picking one of N entries.
        '''
        ''' Existing files at the destination get overwritten.
        ''' Existing directories get recursively merged — update
        ''' flows hit this path, where the previous install's
        ''' files are still on disk and the new archive has the
        ''' same directory tree shape.
        '''
        ''' Same-volume Directory.Move and File.Move are O(1)
        ''' metadata ops, so even multi-GB archives hoist in well
        ''' under a second after the actual extraction finishes.
        ''' </summary>
        Private Sub HoistStagedContents(staging As String, finalDest As String)
            Dim children = Directory.GetFileSystemEntries(staging)
            Dim sourceDir = staging

            If children.Length = 1 AndAlso Directory.Exists(children(0)) Then
                sourceDir = children(0)
                _logger.LogInformation(
                    "Stripping top-level directory: {Dir}",
                    Path.GetFileName(sourceDir))
            Else
                _logger.LogInformation(
                    "StripTopLevelDirectory requested but archive has {Count} top-level entries; promoting all up to install root",
                    children.Length)
            End If

            For Each child In Directory.GetFileSystemEntries(sourceDir)
                Dim name = Path.GetFileName(child)
                Dim dest = Path.Combine(finalDest, name)

                If Directory.Exists(child) Then
                    If Directory.Exists(dest) Then
                        ' Merge into existing directory — the
                        ' update path lands here when a previous
                        ' install's bin/, data/, etc. already
                        ' exists at the install root.
                        MergeDirectoryRecursive(child, dest)
                    Else
                        Directory.Move(child, dest)
                    End If
                Else
                    ' File. File.Move with overwrite:=True replaces
                    ' the destination atomically on the same volume.
                    If File.Exists(dest) Then
                        Try
                            File.Delete(dest)
                        Catch ex As Exception
                            _logger.LogWarning(ex,
                                "Could not replace {Dest}; new file will not be installed", dest)
                            Continue For
                        End Try
                    End If
                    File.Move(child, dest)
                End If
            Next
        End Sub

        ''' <summary>
        ''' Recursively merge files and directories from source
        ''' into target. Files at matching paths are overwritten;
        ''' subdirectories are recursed into. Source becomes empty
        ''' as the merge progresses, and is removed at the end.
        '''
        ''' Used by HoistStagedContents to handle the update case
        ''' where the target directory already exists from a
        ''' previous install.
        ''' </summary>
        Private Sub MergeDirectoryRecursive(source As String, target As String)
            Directory.CreateDirectory(target)

            For Each filePath In Directory.GetFiles(source)
                Dim destFile = Path.Combine(target, Path.GetFileName(filePath))
                Try
                    File.Move(filePath, destFile, overwrite:=True)
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "Failed to move {Src} -> {Dst} during merge; new file will not be installed",
                        filePath, destFile)
                End Try
            Next

            For Each subDir In Directory.GetDirectories(source)
                Dim destSubDir = Path.Combine(target, Path.GetFileName(subDir))
                If Directory.Exists(destSubDir) Then
                    MergeDirectoryRecursive(subDir, destSubDir)
                Else
                    Try
                        Directory.Move(subDir, destSubDir)
                    Catch ex As Exception
                        _logger.LogWarning(ex,
                            "Failed to move directory {Src} -> {Dst}; falling back to recursive merge",
                            subDir, destSubDir)
                        MergeDirectoryRecursive(subDir, destSubDir)
                    End Try
                End If
            Next

            ' Source should now be empty. Delete it; tolerate
            ' "not empty" if some sub-move failed above (the merge
            ' is best-effort and the warnings already surfaced).
            Try
                Directory.Delete(source)
            Catch
            End Try
        End Sub

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

        ''' <summary>
        ''' Fast version check. Reads the local appmanifest ACF for
        ''' the installed buildid and runs SteamCMD app_info_print
        ''' to get the latest published buildid. No files are
        ''' downloaded or modified. Roughly 10-20s including SteamCMD
        ''' startup overhead.
        ''' </summary>
        Public Async Function CheckAppVersionAsync(request As AppVersionCheckRequest,
                                                     cancellation As CancellationToken) As Task(Of AppVersionCheckResponse)
            Dim response As New AppVersionCheckResponse()
            Dim branch = If(String.IsNullOrWhiteSpace(request.BetaBranch), "public", request.BetaBranch)

            ' ---- Installed buildid from ACF ----
            Try
                Dim acfPath = Path.Combine(request.InstallPath,
                                            "steamapps",
                                            $"appmanifest_{request.AppId}.acf")
                If File.Exists(acfPath) Then
                    Dim acfText = Await File.ReadAllTextAsync(acfPath, cancellation)
                    response.InstalledBuildId = ExtractAcfBuildId(acfText)
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to read appmanifest for {AppId}", request.AppId)
            End Try

            ' ---- Latest buildid from SteamCMD ----
            Try
                Dim steamCmdPath = Await FindOrDownloadSteamCmdAsync(cancellation)
                If String.IsNullOrEmpty(steamCmdPath) Then
                    response.ErrorMessage = "SteamCMD not available on node"
                    Return response
                End If

                ' Pre-flight: detect missing 32-bit runtime libraries on Linux
                Await PreflightSteamCmdAsync(steamCmdPath,
                                              Path.GetDirectoryName(steamCmdPath),
                                              cancellation)

                Dim args As New StringBuilder()
                If request.SteamCredentials IsNot Nothing AndAlso
                   Not request.SteamCredentials.IsAnonymous AndAlso
                   Not String.IsNullOrEmpty(request.SteamCredentials.Username) Then
                    args.Append("+login ")
                    args.Append(QuoteForSteamCmd(request.SteamCredentials.Username))
                    args.Append(" "c)
                    args.Append(QuoteForSteamCmd(request.SteamCredentials.Password))
                Else
                    args.Append("+login anonymous")
                End If
                args.Append($" +app_info_update 1 +app_info_print {request.AppId} +quit")

                Dim psi As New ProcessStartInfo()
                psi.FileName = steamCmdPath
                psi.Arguments = args.ToString()
                psi.UseShellExecute = False
                psi.RedirectStandardOutput = True
                psi.RedirectStandardError = True
                psi.CreateNoWindow = True
                psi.WorkingDirectory = Path.GetDirectoryName(steamCmdPath)

                Dim stdout As New StringBuilder()
                Dim stderr As New StringBuilder()
                Using proc As New Process()
                    proc.StartInfo = psi
                    AddHandler proc.OutputDataReceived, Sub(s, e)
                                                            If e.Data IsNot Nothing Then stdout.AppendLine(e.Data)
                                                        End Sub
                    AddHandler proc.ErrorDataReceived, Sub(s, e)
                                                           If e.Data IsNot Nothing Then stderr.AppendLine(e.Data)
                                                       End Sub
                    proc.Start()
                    proc.BeginOutputReadLine()
                    proc.BeginErrorReadLine()

                    ' Poll with timeout — no WaitForExitAsync (deadlocks
                    ' with redirected streams in .NET 8).
                    Dim deadline = DateTime.UtcNow.AddSeconds(90)
                    While Not proc.HasExited
                        If cancellation.IsCancellationRequested Then
                            Try : proc.Kill(True) : Catch : End Try
                            response.ErrorMessage = "Version check cancelled"
                            Return response
                        End If
                        If DateTime.UtcNow > deadline Then
                            Try : proc.Kill(True) : Catch : End Try
                            response.ErrorMessage = "Version check timed out after 90s"
                            Return response
                        End If
                        Await Task.Delay(250, cancellation)
                    End While
                End Using

                response.LatestBuildId = ExtractAppInfoBuildId(stdout.ToString(), branch)
                If String.IsNullOrEmpty(response.LatestBuildId) Then
                    response.ErrorMessage = $"Could not find buildid for branch '{branch}' in app_info output"
                End If
            Catch ex As Exception
                _logger.LogError(ex, "Version check failed for {AppId}", request.AppId)
                response.ErrorMessage = ex.Message
            End Try

            ' ---- Compare ----
            response.UpdateAvailable = Not String.IsNullOrEmpty(response.InstalledBuildId) AndAlso
                                        Not String.IsNullOrEmpty(response.LatestBuildId) AndAlso
                                        response.InstalledBuildId <> response.LatestBuildId

            Return response
        End Function

        ''' <summary>
        ''' Read the buildid out of appmanifest_{appid}.acf after a
        ''' successful SteamCMD step and stash it on the op so
        ''' BuildProgress surfaces it to the manager via
        ''' InstallProgressResponse.InstalledBuildId. Lets the
        ''' manager stamp InstalledVersion in the buildid-bearing
        ''' format the version-check comparison expects, instead of
        ''' the timestamp placeholder that used to require a
        ''' fire-and-forget version check to upgrade.
        '''
        ''' Best-effort: file-read or parse failures log at Warning
        ''' but don't fail the install. The manager falls back to
        ''' the synthetic stamp when InstalledBuildId is empty, so a
        ''' missing capture degrades to the previous behaviour
        ''' rather than breaking anything.
        ''' </summary>
        Private Async Function CapturePostInstallBuildIdAsync(op As ActiveOperation,
                                                                appId As Integer,
                                                                cancellation As CancellationToken) As Task
            Try
                Dim manifestPath = Path.Combine(
                    op.InstallPath, "steamapps", $"appmanifest_{appId}.acf")
                If Not File.Exists(manifestPath) Then
                    _logger.LogDebug("appmanifest not found at {Path}; skipping buildid capture", manifestPath)
                    Return
                End If
                Dim acfText = Await File.ReadAllTextAsync(manifestPath, cancellation)
                Dim buildId = ExtractAcfBuildId(acfText)
                If Not String.IsNullOrEmpty(buildId) Then
                    op.InstalledBuildId = buildId
                    _logger.LogInformation(
                        "Captured installed buildid {BuildId} for app {AppId}",
                        buildId, appId)
                Else
                    _logger.LogDebug("appmanifest at {Path} had no buildid line", manifestPath)
                End If
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "Failed to capture buildid post-install for app {AppId}", appId)
            End Try
        End Function

        ''' <summary>
        ''' Parses the installed buildid from Steam's appmanifest ACF.
        ''' Format is a simple key-value VDF; we only care about one
        ''' line so a regex beats pulling in a VDF parser library.
        ''' </summary>
        Private Shared Function ExtractAcfBuildId(acfText As String) As String
            If String.IsNullOrEmpty(acfText) Then Return Nothing
            Dim m = Regex.Match(acfText, "^\s*""buildid""\s+""(\d+)""",
                                 RegexOptions.Multiline Or RegexOptions.IgnoreCase)
            If m.Success Then Return m.Groups(1).Value
            Return Nothing
        End Function

        ''' <summary>
        ''' Parses the latest buildid for a branch out of SteamCMD's
        ''' app_info_print output. Output is nested VDF; we look for
        ''' the branches section and the requested branch's buildid.
        ''' </summary>
        Private Shared Function ExtractAppInfoBuildId(output As String, branch As String) As String
            If String.IsNullOrEmpty(output) Then Return Nothing
            ' Try to find the "branches" section, then look for the
            ' specific branch name followed by its buildid. The output
            ' structure is approximately:
            '   "branches"
            '   {
            '       "public"
            '       {
            '           "buildid"        "17234567"
            '           ...
            '       }
            '       "beta"
            '       {
            '           "buildid"        "17234599"
            '           ...
            '       }
            '   }
            Dim pattern = """" & Regex.Escape(branch) & """[^{]*\{[^}]*?""buildid""\s+""(\d+)"""
            Dim m = Regex.Match(output, pattern, RegexOptions.Singleline)
            If m.Success Then Return m.Groups(1).Value

            ' Fallback: first buildid in output (works if only one branch)
            m = Regex.Match(output, """buildid""\s+""(\d+)""", RegexOptions.Singleline)
            If m.Success Then Return m.Groups(1).Value

            Return Nothing
        End Function

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
        ''' Quick test launch of SteamCMD to detect missing 32-bit
        ''' runtime libraries on Linux. Without this, an install on a
        ''' fresh box grinds through 10 self-update passes and 3 install
        ''' retries before failing with a buried [WRN] line.
        ''' Throws with a distro-tailored fix message if missing libs
        ''' are detected. No-op on Windows.
        ''' </summary>
        Private Async Function PreflightSteamCmdAsync(steamCmdPath As String,
                                                       steamCmdDir As String,
                                                       cancellation As CancellationToken) As Task
            If OperatingSystem.IsWindows() Then Return

            Dim psi As New ProcessStartInfo()
            psi.FileName = steamCmdPath
            psi.Arguments = "+quit"
            psi.WorkingDirectory = steamCmdDir
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.CreateNoWindow = True

            Dim stderrText As New StringBuilder()
            Dim stdoutText As New StringBuilder()
            Dim exitCode As Integer = -1
            Dim exited As Boolean = False

            Try
                Using proc As New Process()
                    proc.StartInfo = psi
                    AddHandler proc.OutputDataReceived, Sub(s, e)
                                                            If e.Data IsNot Nothing Then stdoutText.AppendLine(e.Data)
                                                        End Sub
                    AddHandler proc.ErrorDataReceived, Sub(s, e)
                                                           If e.Data IsNot Nothing Then stderrText.AppendLine(e.Data)
                                                       End Sub
                    proc.Start()
                    proc.BeginOutputReadLine()
                    proc.BeginErrorReadLine()

                    Dim deadline = DateTime.UtcNow.AddSeconds(15)
                    While Not proc.HasExited
                        If DateTime.UtcNow > deadline Then
                            Try : proc.Kill(True) : Catch : End Try
                            Return ' Inconclusive — fall through to normal flow
                        End If
                        Await Task.Delay(100, cancellation)
                    End While

                    exitCode = proc.ExitCode
                    exited = True
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                ' If the process won't even start, that's a different
                ' failure — let the caller surface it via normal flow.
                _logger.LogWarning(ex, "SteamCMD pre-flight could not run")
                Return
            End Try

            If Not exited Then Return

            ' Exit code 127 + bash's "cannot execute" / "not found" /
            ' "No such file" is the textbook signature of missing
            ' 32-bit runtime libraries (the i386 dynamic linker isn't
            ' installed, so the kernel can't load the steamcmd binary).
            Dim combined = stderrText.ToString() & stdoutText.ToString()
            Dim looksLikeMissingLibs = exitCode = 127 AndAlso
                (combined.IndexOf("cannot execute", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                 combined.IndexOf("No such file", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                 combined.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)

            If looksLikeMissingLibs Then
                Dim hint = BuildLinuxDependencyHint()
                _logger.LogError("SteamCMD pre-flight failed: 32-bit runtime libraries are missing")
                _logger.LogError("SteamCMD output: {Output}", combined.Trim())
                Throw New Exception(
                    "SteamCMD requires 32-bit runtime libraries that aren't installed on this system." &
                    vbCrLf & vbCrLf &
                    hint & vbCrLf & vbCrLf &
                    "After installing the libraries, retry the operation.")
            End If
        End Function

        ''' <summary>
        ''' Reads /etc/os-release and produces a distro-specific install
        ''' command for the 32-bit runtime libraries SteamCMD needs.
        ''' Falls back to a generic table if the distro isn't recognized.
        ''' </summary>
        Private Shared Function BuildLinuxDependencyHint() As String
            Dim distroId As String = ""
            Dim distroIdLike As String = ""
            Dim prettyName As String = ""

            Try
                If File.Exists("/etc/os-release") Then
                    For Each rawLine In File.ReadAllLines("/etc/os-release")
                        Dim line = rawLine.Trim()
                        If line.StartsWith("ID=") Then
                            distroId = line.Substring(3).Trim().Trim(""""c).ToLowerInvariant()
                        ElseIf line.StartsWith("ID_LIKE=") Then
                            distroIdLike = line.Substring(8).Trim().Trim(""""c).ToLowerInvariant()
                        ElseIf line.StartsWith("PRETTY_NAME=") Then
                            prettyName = line.Substring(12).Trim().Trim(""""c)
                        End If
                    Next
                End If
            Catch
            End Try

            Dim header = If(String.IsNullOrEmpty(prettyName),
                            "Linux distribution detected.",
                            "Detected: " & prettyName)

            ' Prefer ID, fall back to ID_LIKE for derivatives we don't list explicitly
            Dim key = distroId
            If String.IsNullOrEmpty(key) Then key = distroIdLike

            Dim cmd As String
            Select Case key
                Case "ubuntu", "debian", "linuxmint", "pop", "elementary", "kali", "raspbian"
                    cmd = "    sudo dpkg --add-architecture i386" & vbCrLf &
                          "    sudo apt update" & vbCrLf &
                          "    sudo apt install lib32gcc-s1"
                Case "fedora", "rhel", "centos", "rocky", "almalinux"
                    cmd = "    sudo dnf install glibc.i686 libstdc++.i686"
                Case "arch", "manjaro", "endeavouros"
                    cmd = "    Enable [multilib] in /etc/pacman.conf, then:" & vbCrLf &
                          "    sudo pacman -Sy lib32-gcc-libs"
                Case "opensuse", "opensuse-leap", "opensuse-tumbleweed", "suse", "sles"
                    cmd = "    sudo zypper install glibc-32bit libgcc_s1-32bit"
                Case "alpine"
                    cmd = "    SteamCMD does not officially support Alpine/musl." & vbCrLf &
                          "    Use a glibc-based distribution instead."
                Case Else
                    cmd = "    Debian/Ubuntu:   sudo apt install lib32gcc-s1" & vbCrLf &
                          "    RHEL/Fedora:     sudo dnf install glibc.i686 libstdc++.i686" & vbCrLf &
                          "    Arch:            Enable multilib, then sudo pacman -Sy lib32-gcc-libs" & vbCrLf &
                          "    openSUSE:        sudo zypper install glibc-32bit libgcc_s1-32bit"
            End Select

            Return header & vbCrLf & "To fix, run:" & vbCrLf & cmd
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

        ''' <summary>
        ''' Compute weighted overall-install progress percent from
        ''' op.CurrentStepIndex, op.Steps, and the within-step pct
        ''' supplied by the caller. Each step contributes
        ''' Weight / totalWeight of the bar; the current step's
        ''' contribution is scaled by withinStepPct.
        '''
        ''' Math:
        '''   totalWeight     = sum of every step's Weight
        '''   completedWeight = sum of weights for steps before current
        '''   currentWeight   = current step's Weight
        '''   bar = (completedWeight + currentWeight × pct÷100) /
        '''         totalWeight × 100
        '''
        ''' For a 3-step plan with weights [10, 1, 1] (SteamCmdStep
        ''' followed by two short steps), the SteamCMD download at 50%
        ''' shows as (0 + 10×0.5)/12×100 = 41.7% on the bar; download
        ''' complete = 83.3%; second step done = 91.7%; third = 100%.
        ''' That's the visible-time-tracking the equal-weight math can't
        ''' produce (where each step would be a flat 33% slice no matter
        ''' how long it took).
        '''
        ''' Defensive against missing/empty step lists, out-of-range
        ''' CurrentStepIndex, and 0/negative Weight values (treated as
        ''' 1.0). The empty-step-list path falls back to passing
        ''' withinStepPct through verbatim so a plugin that produces
        ''' a zero-step plan still reports something sane.
        ''' </summary>
        Private Shared Function ComputeWeightedProgress(op As ActiveOperation,
                                                          withinStepPct As Double) As Double
            Dim pctClamped = Math.Max(0.0, Math.Min(100.0, withinStepPct))

            If op.Steps Is Nothing OrElse op.Steps.Count = 0 Then
                Return pctClamped
            End If

            Dim totalWeight As Double = 0
            For Each s In op.Steps
                Dim w = If(s IsNot Nothing AndAlso s.Weight > 0, s.Weight, 1.0)
                totalWeight += w
            Next
            ' Final defensive guard — if every step somehow ended up
            ' with weight 0, fall back to step-count so the math
            ' still divides by something positive.
            If totalWeight <= 0 Then totalWeight = op.Steps.Count

            Dim completedWeight As Double = 0
            Dim upToIdx = Math.Min(op.CurrentStepIndex - 1, op.Steps.Count - 1)
            For i = 0 To upToIdx
                Dim s = op.Steps(i)
                Dim w = If(s IsNot Nothing AndAlso s.Weight > 0, s.Weight, 1.0)
                completedWeight += w
            Next

            Dim currentWeight As Double = 1.0
            If op.CurrentStepIndex >= 0 AndAlso op.CurrentStepIndex < op.Steps.Count Then
                Dim s = op.Steps(op.CurrentStepIndex)
                If s IsNot Nothing AndAlso s.Weight > 0 Then
                    currentWeight = s.Weight
                End If
            End If

            Dim absolute = (completedWeight + currentWeight * pctClamped / 100.0) /
                             totalWeight * 100.0
            Return Math.Max(0.0, Math.Min(100.0, absolute))
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
                .PendingPromptMessage = op.PendingPromptMessage,
                .SteamCmdPhase = op.SteamCmdPhase,
                .BytesDownloaded = op.BytesDownloaded,
                .BytesTotal = op.BytesTotal,
                .InstalledBuildId = op.InstalledBuildId
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
        Public Property RunCommonRedist As Boolean
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

        ' SteamCMD content_log.txt tailer surfaces parsed progress
        ' onto these fields. Reads/writes are from different threads
        ' (tailer task vs. the API handler that calls BuildProgress)
        ' but each property is a primitive or reference assignment;
        ' on x64 those are atomic for properly-aligned memory and
        ' the worst-case race is reading a slightly stale value
        ' that gets corrected on the next 2s manager poll. No
        ' SyncLock needed for the read/write side.
        Public Property SteamCmdPhase As String
        Public Property BytesDownloaded As Long?
        Public Property BytesTotal As Long?

        ' Captured by CapturePostInstallBuildIdAsync after a
        ' successful SteamCMD step — the buildid extracted from
        ' appmanifest_{appid}.acf. Surfaces to the manager via
        ' BuildProgress so InstalledVersion can be stamped in the
        ' same "steam:{appId}@{branch} build {N}" format
        ' VersionCheckService produces. Nothing on a fresh op or
        ' before the SteamCMD step finishes.
        Public Property InstalledBuildId As String

        ' Throttling state for the per-line LogInformation in
        ' ApplyContentLogLineToOp. Not exposed through BuildProgress
        ' — only the parser reads/writes them. Initial pct is set
        ' to a value below any real reading so the very first
        ' parsed line clears the threshold.
        Friend Property LastLoggedSteamCmdPhase As String
        Friend Property LastLoggedSteamCmdPct As Double = -100.0

        ' Baseline cumulative bytes-written-by-process counter at
        ' the moment the SteamCMD phase first becomes Downloading.
        ' Subtracted from each subsequent reading by the I/O counter
        ' poller so the displayed byte count starts at zero when
        ' the actual download begins, excluding self-update / login
        ' / manifest setup writes that happened earlier in the same
        ' process. Set once on first Downloading-phase tick; never
        ' reset. Nothing on a fresh op.
        Friend Property WriteTransferBaseline As Long?

        Public Property ActiveProcess As Process
    End Class

End Namespace