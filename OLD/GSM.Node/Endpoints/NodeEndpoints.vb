Imports Microsoft.AspNetCore.Builder
Imports Microsoft.AspNetCore.Http
Imports Microsoft.AspNetCore.Routing
Imports Microsoft.Extensions.DependencyInjection
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Node.Api

' ============================================================
'  InstallEndpoints
'
'  Registers all /api/v1/installations/* routes.
'  Delegates all real work to InstallRunner.
' ============================================================

Public Module InstallEndpoints

    Public Sub Register(app As WebApplication)

        Dim api = app.MapGroup("/api/v1/installations")

        ' ---- Installation status ----
        api.MapGet("{id}/status",
            Function(id As String,
                     runner As InstallRunner) As IResult

                Dim status = runner.GetStatus(id)
                If status Is Nothing Then
                    Return Results.Ok(New InstallationStatusResponse With {
                        .InstallationId = id,
                        .State = InstallationOperationState.Idle
                    })
                End If
                Return Results.Ok(status)
            End Function)

        ' ---- Start install ----
        api.MapPost("{id}/install",
            Function(id As String,
                     request As InstallRequest,
                     runner As InstallRunner) As IResult

                request.InstallationId = id
                Dim response = runner.StartInstall(request)

                If response.State = InstallationOperationState.Failed Then
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.InstallAlreadyInProgress,
                        .Message = response.Message
                    })
                End If

                Return Results.Ok(response)
            End Function)

        ' ---- Start update ----
        ' Identical to install but enforces the installation read lock check:
        ' no running instances may hold the files we're about to overwrite.
        api.MapPost("{id}/update",
            Function(id As String,
                     request As UpdateRequest,
                     runner As InstallRunner) As IResult

                request.InstallationId = id
                Dim response = runner.StartUpdate(request)

                If response.State = InstallationOperationState.Failed Then
                    ' Check if this was a lock conflict vs a general failure.
                    Dim errorCode = If(response.Message.Contains("running"),
                                       NodeErrorCodes.InstallationReadLocked,
                                       NodeErrorCodes.InstallAlreadyInProgress)
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = errorCode,
                        .Message = response.Message
                    })
                End If

                Return Results.Ok(response)
            End Function)

        ' ---- Validate install ----
        ' Checks that expected files exist without running a full install.
        ' The manager calls this after a manual file placement (InstallMethod.Manual)
        ' or to verify the state of an existing install.
        api.MapPost("{id}/validate",
            Function(id As String,
                     context As HttpContext) As IResult

                ' Validation logic is game-specific and lives in the plugin.
                ' The node can't call plugin code, so it does a basic existence
                ' check on the install path provided in the query string.
                ' Full validation is done by the manager after calling this.
                Dim installPath = context.Request.Query("installPath").ToString()

                If String.IsNullOrWhiteSpace(installPath) Then
                    Return Results.BadRequest(New NodeErrorResponse With {
                        .ErrorCode = "MISSING_PARAMETER",
                        .Message = "installPath query parameter is required."
                    })
                End If

                Dim exists = IO.Directory.Exists(installPath)
                Return Results.Ok(New ValidateInstallResponse With {
                    .InstallationId = id,
                    .IsValid = exists,
                    .Reason = If(exists, "Install directory exists.",
                                 $"Directory not found: {installPath}")
                })
            End Function)

        ' ---- Cancel ----
        api.MapPost("{id}/cancel",
            Function(id As String,
                     runner As InstallRunner) As IResult

                Dim response = runner.Cancel(id)
                Return Results.Ok(response)
            End Function)

        ' ---- Get pending prompt ----
        ' The manager polls this at ~1s intervals while an install is
        ' in WaitingForInput state. Returns null/404 if no prompt waiting.
        api.MapGet("{id}/prompt",
            Function(id As String,
                     runner As InstallRunner) As IResult

                Dim prompt = runner.GetPendingPrompt(id)
                If prompt Is Nothing Then
                    Return Results.NotFound(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.NoPromptWaiting,
                        .Message = "No prompt is currently waiting for input."
                    })
                End If
                Return Results.Ok(prompt)
            End Function)

        ' ---- Respond to prompt ----
        ' The manager sends the user's input here after surfacing the
        ' prompt in the UI. The node feeds it to the install process stdin.
        api.MapPost("{id}/prompt",
            Function(id As String,
                     request As RespondToPromptRequest,
                     runner As InstallRunner) As IResult

                Dim response = runner.RespondToPrompt(
                    id, request.Response, request.IsSensitive)

                If Not response.Accepted Then
                    Return Results.UnprocessableEntity(New NodeErrorResponse With {
                        .ErrorCode = NodeErrorCodes.NoPromptWaiting,
                        .Message = response.Message
                    })
                End If
                Return Results.Ok(response)
            End Function)

    End Sub

End Module


' ============================================================
'  SystemEndpoints
'
'  Registers /api/v1/system/* routes.
'  Returns hardware and OS information about the node machine.
'  Used by the manager UI when the operator is configuring
'  install paths, checking node health, or troubleshooting.
' ============================================================

Public Module SystemEndpoints

    Public Sub Register(app As WebApplication)

        Dim api = app.MapGroup("/api/v1/system")

        ' ---- System info ----
        api.MapGet("info",
            Function(config As NodeConfiguration,
                     pm As ProcessManager) As NodeSystemInfoResponse

                ' Gather what we can cross-platform.
                Dim totalMemMb As Long = 0
                Dim freeMemMb As Long = 0

                If OperatingSystem.IsWindows() Then
                    GetWindowsMemory(totalMemMb, freeMemMb)
                ElseIf OperatingSystem.IsLinux() Then
                    GetLinuxMemory(totalMemMb, freeMemMb)
                End If

                Return New NodeSystemInfoResponse With {
                    .Hostname = Environment.MachineName,
                    .Os = GetOsDescription(),
                    .Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    .CpuName = GetCpuName(),
                    .CpuCoreCount = Environment.ProcessorCount,
                    .TotalMemoryMb = totalMemMb,
                    .FreeMemoryMb = freeMemMb,
                    .DotNetRuntime = RuntimeInformation.FrameworkDescription,
                    .NodeServiceVersion =
                        If(GetType(SystemEndpoints).Assembly.GetName().Version?.ToString(), "0.0.0"),
                    .NodeStartedAt = NodeStartTime
                }
            End Function)

        ' ---- Drives / mounts ----
        ' Returns available drives (Windows) or mount points (Linux).
        ' The manager UI uses this to let the operator browse for
        ' an install path when creating a new installation.
        api.MapGet("drives",
            Function() As NodeDrivesResponse

                Dim drives As New List(Of GSM.Node.Api.DriveInfo)()

                Try
                    For Each drive In IO.DriveInfo.GetDrives()
                        Try
                            If drive.IsReady Then
                                drives.Add(New GSM.Node.Api.DriveInfo With {
                                    .RootPath = drive.RootDirectory.FullName,
                                    .Label = drive.VolumeLabel,
                                    .TotalSizeGb = Math.Round(
                                        drive.TotalSize / (1024.0 ^ 3), 2),
                                    .FreeSpaceGb = Math.Round(
                                        drive.AvailableFreeSpace / (1024.0 ^ 3), 2),
                                    .DriveFormat = drive.DriveFormat
                                })
                            End If
                        Catch
                            ' Some drives may not be readable (e.g. empty optical drives)
                        End Try
                    Next
                Catch
                End Try

                Return New NodeDrivesResponse With {.Drives = drives}
            End Function)

    End Sub


    ' ============================================================
    '  SYSTEM INFO HELPERS
    ' ============================================================

    Private ReadOnly NodeStartTime As DateTime = DateTime.UtcNow

    Private Function GetOsDescription() As String
        If OperatingSystem.IsWindows() Then
            Return $"Windows {Environment.OSVersion.Version}"
        ElseIf OperatingSystem.IsLinux() Then
            ' Try to read the pretty name from os-release.
            Try
                Dim lines = IO.File.ReadAllLines("/etc/os-release")
                Dim prettyLine = lines.FirstOrDefault(
                    Function(l) l.StartsWith("PRETTY_NAME="))
                If prettyLine IsNot Nothing Then
                    Return prettyLine.Substring("PRETTY_NAME=".Length).Trim(""""c)
                End If
            Catch
            End Try
            Return $"Linux {Environment.OSVersion.Version}"
        End If
        Return Environment.OSVersion.ToString()
    End Function

    Private Function GetCpuName() As String
        If OperatingSystem.IsWindows() Then
            Try
                ' Read from registry - most reliable on Windows.
                Using key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    "HARDWARE\DESCRIPTION\System\CentralProcessor\0")
                    Return If(key?.GetValue("ProcessorNameString")?.ToString()?.Trim(),
                           "Unknown")
                End Using
            Catch
            End Try
        ElseIf OperatingSystem.IsLinux() Then
            Try
                Dim lines = IO.File.ReadAllLines("/proc/cpuinfo")
                Dim modelLine = lines.FirstOrDefault(
                    Function(l) l.StartsWith("model name"))
                If modelLine IsNot Nothing Then
                    Return modelLine.Split(":"c)(1).Trim()
                End If
            Catch
            End Try
        End If
        Return "Unknown"
    End Function

    <System.Runtime.Versioning.SupportedOSPlatform("windows")>
    Private Sub GetWindowsMemory(ByRef totalMb As Long, ByRef freeMb As Long)
        Try
            Dim memStatus As New MEMORYSTATUSEX()
            memStatus.dwLength = CUInt(Runtime.InteropServices.Marshal.SizeOf(memStatus))
            If GlobalMemoryStatusEx(memStatus) Then
                totalMb = CLng(memStatus.ullTotalPhys \ (1024 * 1024))
                freeMb = CLng(memStatus.ullAvailPhys \ (1024 * 1024))
            End If
        Catch
        End Try
    End Sub

    Private Sub GetLinuxMemory(ByRef totalMb As Long, ByRef freeMb As Long)
        Try
            ' /proc/meminfo is the standard Linux memory info source.
            Dim lines = IO.File.ReadAllLines("/proc/meminfo")
            For Each line In lines
                If line.StartsWith("MemTotal:") Then
                    ' Format: "MemTotal:       16384000 kB"
                    Dim parts = line.Split(" "c, StringSplitOptions.RemoveEmptyEntries)
                    If parts.Length >= 2 Then
                        totalMb = Long.Parse(parts(1)) \ 1024
                    End If
                ElseIf line.StartsWith("MemAvailable:") Then
                    Dim parts = line.Split(" "c, StringSplitOptions.RemoveEmptyEntries)
                    If parts.Length >= 2 Then
                        freeMb = Long.Parse(parts(1)) \ 1024
                    End If
                End If
            Next
        Catch
        End Try
    End Sub

    ' Windows API for memory info.
    <Runtime.InteropServices.StructLayout(Runtime.InteropServices.LayoutKind.Sequential)>
    Private Structure MEMORYSTATUSEX
        Public dwLength As UInteger
        Public dwMemoryLoad As UInteger
        Public ullTotalPhys As ULong
        Public ullAvailPhys As ULong
        Public ullTotalPageFile As ULong
        Public ullAvailPageFile As ULong
        Public ullTotalVirtual As ULong
        Public ullAvailVirtual As ULong
        Public ullAvailExtendedVirtual As ULong
    End Structure

    <Runtime.InteropServices.DllImport("kernel32.dll")>
    Private Function GlobalMemoryStatusEx(ByRef lpBuffer As MEMORYSTATUSEX) As Boolean
    End Function

End Module
