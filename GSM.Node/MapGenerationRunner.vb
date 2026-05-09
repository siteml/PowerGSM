Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin
Imports GSM.Node.Api
Imports Microsoft.Extensions.Logging

' ============================================================
'  MapGenerationRunner — executes one-off map generation steps
'
'  Phase 4c-3. Runs a small subset of InstallStep types
'  (WriteFileStep, RunProcessStep) synchronously against an
'  installation directory to produce a new save. Distinct from
'  InstallRunner because:
'
'    1. Lifecycle is request-scoped, not long-lived. There's
'       no operations-table entry, no progress polling, no
'       interactive prompt machinery — the call blocks until
'       the steps complete or the timeout fires.
'
'    2. Step coverage is intentionally narrow. SteamCMD,
'       DownloadFileStep, CopyFileStep aren't supported because
'       map generation against an existing install never needs
'       them; rejecting the request up-front beats discovering
'       at execution time that some plugin slipped a CopyFileStep
'       in by accident.
'
'    3. RunProcessStep here uses HasExited polling rather than
'       Process.WaitForExitAsync, matching the pattern used by
'       SteamCMD execution in InstallRunner. WaitForExitAsync
'       deadlocks when stdio is redirected (it waits for the
'       streams to close, which won't happen until we've drained
'       them).
'
'  Local variable / parameter names avoid the bare `step` token
'  because Step is a VB.Net reserved keyword (For...Step). We
'  use `currentStep` / `writeStep` / `runStep` instead.
'
'  When map generation grows beyond v1 (long-running with
'  progress, cancellable from the UI mid-run), the natural move
'  is to fold this into a renamed OperationRunner and reuse the
'  install lifecycle states + polling endpoint. Until then the
'  small focused class is easier to reason about.
' ============================================================

Namespace GSM.Node

    Public Class MapGenerationRunner

        Private Const DefaultTimeoutSeconds As Integer = 300
        Private Const MaxOutputBytes As Integer = 16 * 1024  ' 16KB cap on captured stdout

        Private ReadOnly _logger As ILogger(Of MapGenerationRunner)

        Public Sub New(logger As ILogger(Of MapGenerationRunner))
            _logger = logger
        End Sub

        ''' <summary>
        ''' Run the request's step list against installPath.
        ''' Returns a GenerateMapResponse describing the outcome.
        ''' Never throws — every error path produces a populated
        ''' response with Success=False, an error message, and
        ''' (where applicable) the failing step index.
        ''' </summary>
        Public Async Function RunAsync(instanceId As String,
                                        installPath As String,
                                        request As GenerateMapRequest,
                                        cancellation As CancellationToken) As Task(Of GenerateMapResponse)
            Dim response As New GenerateMapResponse With {.FailedStepIndex = -1}

            If String.IsNullOrEmpty(installPath) OrElse Not Directory.Exists(installPath) Then
                response.Success = False
                response.ErrorMessage = $"Install path does not exist: {installPath}"
                Return response
            End If

            If request Is Nothing OrElse request.Steps Is Nothing OrElse request.Steps.Count = 0 Then
                response.Success = False
                response.ErrorMessage = "Request has no steps"
                Return response
            End If

            ' Pre-validate every step type so we fail fast rather
            ' than discovering an unsupported step partway through.
            For i = 0 To request.Steps.Count - 1
                Dim s = request.Steps(i)
                If s Is Nothing Then
                    response.Success = False
                    response.FailedStepIndex = i
                    response.ErrorMessage = $"Step {i + 1} is null"
                    Return response
                End If
                If TypeOf s Is WriteFileStep OrElse TypeOf s Is RunProcessStep Then
                    Continue For
                End If
                response.Success = False
                response.FailedStepIndex = i
                response.ErrorMessage =
                    $"Step type {s.GetType().Name} is not supported by map generation. " &
                    "Use WriteFileStep or RunProcessStep."
                Return response
            Next

            ' Apply the sequence-level timeout via a linked CTS so
            ' both the caller-cancellation and the timeout can fire.
            Dim timeoutSec = If(request.TimeoutSeconds > 0, request.TimeoutSeconds, DefaultTimeoutSeconds)
            Using timeoutCts As New CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec))
                Using linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellation, timeoutCts.Token)
                    Dim token = linkedCts.Token
                    Dim outputBuilder As New StringBuilder()

                    Dim stepIndex = 0
                    Try
                        For stepIndex = 0 To request.Steps.Count - 1
                            token.ThrowIfCancellationRequested()
                            Dim currentStep = request.Steps(stepIndex)
                            _logger.LogInformation("MapGen {InstanceId}: step {Index}/{Total} {Name}",
                                instanceId, stepIndex + 1, request.Steps.Count,
                                If(currentStep.StepName, currentStep.GetType().Name))

                            If TypeOf currentStep Is WriteFileStep Then
                                ExecuteWriteFile(installPath, DirectCast(currentStep, WriteFileStep))
                            ElseIf TypeOf currentStep Is RunProcessStep Then
                                Await ExecuteRunProcessAsync(installPath,
                                    DirectCast(currentStep, RunProcessStep), outputBuilder, token)
                            End If
                        Next
                    Catch ex As OperationCanceledException
                        response.Success = False
                        response.FailedStepIndex = stepIndex
                        response.ErrorMessage = If(timeoutCts.IsCancellationRequested,
                            $"Timed out after {timeoutSec} seconds",
                            "Cancelled")
                        response.Output = TrimOutput(outputBuilder)
                        Return response
                    Catch ex As Exception
                        response.Success = False
                        response.FailedStepIndex = stepIndex
                        response.ErrorMessage = ex.Message
                        response.Output = TrimOutput(outputBuilder)
                        _logger.LogError(ex, "MapGen {InstanceId} step {Index} failed",
                            instanceId, stepIndex)
                        Return response
                    End Try

                    ' Optional output verification — the engine
                    ' may exit 0 yet produce nothing if the user's
                    ' arguments were subtly wrong (Factorio
                    ' silently no-ops on some bad --create paths
                    ' rather than erroring).
                    If Not String.IsNullOrEmpty(request.ExpectedOutputRelativePath) Then
                        Dim expectedAbs = Path.Combine(installPath,
                            request.ExpectedOutputRelativePath.Replace("/"c, Path.DirectorySeparatorChar))
                        If Not File.Exists(expectedAbs) Then
                            response.Success = False
                            response.ErrorMessage =
                                $"Steps completed but expected output file did not appear: {request.ExpectedOutputRelativePath}"
                            response.Output = TrimOutput(outputBuilder)
                            Return response
                        End If
                        Dim info As New FileInfo(expectedAbs)
                        response.OutputRelativePath = request.ExpectedOutputRelativePath
                        response.OutputSizeBytes = info.Length
                    End If

                    response.Success = True
                    response.Output = TrimOutput(outputBuilder)
                    Return response
                End Using
            End Using
        End Function

        Private Sub ExecuteWriteFile(installPath As String, writeStep As WriteFileStep)
            If String.IsNullOrEmpty(writeStep.RelativePath) Then
                Throw New InvalidOperationException("WriteFileStep requires RelativePath")
            End If
            Dim dest = Path.Combine(installPath,
                writeStep.RelativePath.Replace("/"c, Path.DirectorySeparatorChar))
            If File.Exists(dest) AndAlso Not writeStep.OverwriteExisting Then
                Return
            End If
            Dim parent = Path.GetDirectoryName(dest)
            If Not String.IsNullOrEmpty(parent) AndAlso Not Directory.Exists(parent) Then
                Directory.CreateDirectory(parent)
            End If
            File.WriteAllText(dest, If(writeStep.Content, ""))
        End Sub

        Private Async Function ExecuteRunProcessAsync(installPath As String,
                                                       runStep As RunProcessStep,
                                                       outputBuilder As StringBuilder,
                                                       token As CancellationToken) As Task
            If String.IsNullOrEmpty(runStep.ExecutablePath) Then
                Throw New InvalidOperationException("RunProcessStep requires ExecutablePath")
            End If

            ' Resolve relative ExecutablePath against installPath.
            ' This matches how the install runner treats relative
            ' executables (plugins typically pass "bin/x64/factorio.exe"
            ' rather than the absolute path so the same step works
            ' across nodes).
            '
            ' Plugin convention is forward-slashed paths so the same
            ' string works on both OSes. Normalise to the local
            ' separator before testing existence so File.Exists on
            ' Linux doesn't get tripped up if a legacy plugin emits
            ' a backslash. Same normalisation pattern WriteFileStep
            ' and ExpectedOutputRelativePath already use elsewhere
            ' in this file.
            Dim exePath = runStep.ExecutablePath.Replace("/"c, Path.DirectorySeparatorChar) _
                                                .Replace("\"c, Path.DirectorySeparatorChar)
            If Not Path.IsPathRooted(exePath) Then
                exePath = Path.Combine(installPath, exePath)
            End If

            ' OS-aware extension fallback. Plugins are recompiled on
            ' the Windows-hosted manager and have no clean way to
            ' know the node's OS at the time they emit a
            ' RunProcessStep, so they tend to embed the Windows
            ' executable name (`factorio.exe`). On Linux the file
            ' is the same binary without the .exe suffix; if the
            ' literal path doesn't exist, try stripping `.exe` once
            ' before giving up. Inverse on Windows: a plugin that
            ' chose to emit the bare name still works against a .exe
            ' on disk.
            If Not File.Exists(exePath) Then
                Dim alt As String = Nothing
                If OperatingSystem.IsWindows() Then
                    If Not exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then
                        alt = exePath & ".exe"
                    End If
                Else
                    If exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then
                        alt = exePath.Substring(0, exePath.Length - 4)
                    End If
                End If
                If alt IsNot Nothing AndAlso File.Exists(alt) Then
                    exePath = alt
                End If
            End If

            If Not File.Exists(exePath) Then
                Throw New FileNotFoundException($"Executable not found: {exePath}")
            End If

            Dim workingDir = If(String.IsNullOrEmpty(runStep.WorkingDirectory),
                                installPath,
                                runStep.WorkingDirectory)

            Dim psi As New ProcessStartInfo() With {
                .FileName = exePath,
                .Arguments = If(runStep.Arguments, ""),
                .WorkingDirectory = workingDir,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .UseShellExecute = False,
                .CreateNoWindow = True
            }

            Using proc As New Process() With {.StartInfo = psi}
                ' Capture output asynchronously so a chatty engine
                ' (Factorio's --create produces dozens of progress
                ' lines) can't fill the OS pipe buffer and block
                ' the child. The DataReceived handlers append into
                ' the shared StringBuilder under a lock since both
                ' stdout and stderr arrive on threadpool threads.
                Dim outputLock As New Object()
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
                    Throw New InvalidOperationException($"Failed to start process: {exePath}")
                End If
                proc.BeginOutputReadLine()
                proc.BeginErrorReadLine()

                ' Per-step timeout floor independent of the
                ' sequence-level timeout. Step's TimeoutMs is the
                ' upper bound for THIS process; the linked token
                ' fires earlier if the sequence-level timeout
                ' or caller cancellation triggers.
                Dim stepTimeoutMs = If(runStep.TimeoutMs > 0, runStep.TimeoutMs, 300000)
                Dim deadline = DateTime.UtcNow.AddMilliseconds(stepTimeoutMs)

                ' Polling loop — we don't use WaitForExitAsync
                ' because it deadlocks waiting for the redirected
                ' streams to close. HasExited goes True as soon as
                ' the process terminates regardless of stream state.
                While Not proc.HasExited
                    If token.IsCancellationRequested Then
                        Try : proc.Kill(True) : Catch : End Try
                        token.ThrowIfCancellationRequested()
                    End If
                    If DateTime.UtcNow > deadline Then
                        Try : proc.Kill(True) : Catch : End Try
                        Throw New TimeoutException(
                            $"Process exceeded step timeout ({stepTimeoutMs}ms): {exePath}")
                    End If
                    Await Task.Delay(100, token)
                End While

                ' Give the async stream readers a moment to flush
                ' the final lines after exit. Without this the
                ' captured output sometimes misses the last few
                ' lines on fast-completing processes.
                Try
                    proc.WaitForExit(2000)
                Catch
                End Try

                Dim expectedExit = runStep.ExpectedExitCode
                If proc.ExitCode <> expectedExit Then
                    Throw New InvalidOperationException(
                        $"Process exited with code {proc.ExitCode} (expected {expectedExit}): {exePath}")
                End If
            End Using
        End Function

        Private Shared Function TrimOutput(builder As StringBuilder) As String
            If builder Is Nothing OrElse builder.Length = 0 Then Return ""
            If builder.Length <= MaxOutputBytes Then Return builder.ToString()
            ' Keep the tail rather than the head — errors typically
            ' show up at the end of a process's output, and the
            ' opening lines are usually banner/version noise.
            Return "...[truncated]..." & vbCrLf &
                   builder.ToString(builder.Length - MaxOutputBytes, MaxOutputBytes)
        End Function

    End Class

End Namespace
