Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Text

' ============================================================
'  ServiceManager — cross-platform service plumbing
'
'  Windows path:
'    sc.exe create / delete / query / start / stop
'    Requires elevation. The setup tool detects elevation up front
'    and reports a friendly message instead of letting sc print
'    "[SC] OpenSCManager FAILED 5: Access is denied."
'
'  Linux path:
'    Generates a systemd unit file at <output-dir>/gsmnode.service
'    and prints copy/enable/start instructions. We do NOT shell out
'    to systemctl ourselves because:
'      a) systemctl needs root and we don't want to assume sudo
'         is configured non-interactively
'      b) Many distros bury units in /etc/systemd/system/ vs
'         /usr/lib/systemd/system/ and the right answer depends
'         on the user's policy
'      c) Container-only environments don't have systemd at all
'    The user runs three short commands themselves; this is the
'    standard packaging pattern for systemd-targeting tools.
' ============================================================

Public Module ServiceManager

    Public Const DefaultServiceName As String = "GSMNode"
    Public Const DefaultDisplayName As String = "PowerGSM Node"
    Public Const DefaultDescription As String = "PowerGSM Node — game server management agent"

    Public Class ServiceResult
        Public Property Success As Boolean
        Public Property Message As String
        Public Property Output As String
    End Class

    ''' <summary>
    ''' Returns the absolute path to the GSM.Node executable that should
    ''' sit next to the setup tool. Adds the .exe extension on Windows.
    ''' Used both to validate that we are deployed correctly and to
    ''' build the binPath for sc.exe / ExecStart= for systemd.
    ''' </summary>
    Public Function GetNodeExecutablePath() As String
        Dim baseDir = AppContext.BaseDirectory
        Dim exeName = If(ConfigHelpers.RunningOnWindows(), "GSM.Node.exe", "GSM.Node")
        Return Path.Combine(baseDir, exeName)
    End Function

    Public Function NodeExecutableExists() As Boolean
        Return File.Exists(GetNodeExecutablePath())
    End Function

    ' --------------------------------------------------------
    ' Windows path
    ' --------------------------------------------------------

    ''' <summary>
    ''' Creates the GSMNode Windows service via sc.exe. On Windows only.
    ''' Returns success=False with a descriptive message on any failure,
    ''' including the most common one: not running elevated.
    ''' </summary>
    Public Function InstallWindowsService(serviceName As String,
                                          displayName As String,
                                          description As String) As ServiceResult
        If Not ConfigHelpers.RunningOnWindows() Then
            Return New ServiceResult With {.Success = False, .Message = "Not on Windows."}
        End If

        If String.IsNullOrWhiteSpace(serviceName) Then serviceName = DefaultServiceName
        If String.IsNullOrWhiteSpace(displayName) Then displayName = DefaultDisplayName
        If String.IsNullOrWhiteSpace(description) Then description = DefaultDescription

        If Not NodeExecutableExists() Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "GSM.Node.exe was not found next to the setup tool. Expected at: " & GetNodeExecutablePath()
            }
        End If

        If Not ConfigHelpers.RunningElevated() Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "Administrator rights are required to install a Windows service. Re-run the setup tool from an elevated command prompt or right-click and 'Run as administrator'."
            }
        End If

        ' Note the deliberate space between "binPath=" and the value.
        ' sc.exe is one of the few tools that REQUIRES the space; without
        ' it the command parses but creates a service that won't start.
        Dim nodePath = GetNodeExecutablePath()
        Dim createOutput As String = Nothing
        Dim createOk = RunSc({
            "create", serviceName,
            "binPath=", """" & nodePath & """",
            "DisplayName=", displayName,
            "start=", "auto"
        }, createOutput)

        If Not createOk Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "sc create failed.",
                .Output = createOutput
            }
        End If

        ' Best-effort description (failure is not fatal).
        Dim descOutput As String = Nothing
        RunSc({"description", serviceName, description}, descOutput)

        Dim startOutput As String = Nothing
        Dim startOk = RunSc({"start", serviceName}, startOutput)

        Dim msg As String
        If startOk Then
            msg = $"Service '{serviceName}' installed and started."
        Else
            msg = $"Service '{serviceName}' installed but could not be started automatically. " &
                  "Check the configuration and try starting it manually."
        End If

        Return New ServiceResult With {
            .Success = True,
            .Message = msg,
            .Output = createOutput & vbCrLf & descOutput & vbCrLf & startOutput
        }
    End Function

    Public Function UninstallWindowsService(serviceName As String) As ServiceResult
        If Not ConfigHelpers.RunningOnWindows() Then
            Return New ServiceResult With {.Success = False, .Message = "Not on Windows."}
        End If

        If String.IsNullOrWhiteSpace(serviceName) Then serviceName = DefaultServiceName

        If Not ConfigHelpers.RunningElevated() Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "Administrator rights are required to remove a Windows service."
            }
        End If

        ' Stop is best-effort: if the service is already stopped sc returns
        ' a non-zero exit but the output explains why and we proceed to delete.
        Dim stopOutput As String = Nothing
        RunSc({"stop", serviceName}, stopOutput)

        Dim deleteOutput As String = Nothing
        Dim deleteOk = RunSc({"delete", serviceName}, deleteOutput)

        Return New ServiceResult With {
            .Success = deleteOk,
            .Message = If(deleteOk,
                          $"Service '{serviceName}' removed.",
                          $"Failed to remove service '{serviceName}'."),
            .Output = stopOutput & vbCrLf & deleteOutput
        }
    End Function

    ''' <summary>
    ''' Returns "Running", "Stopped", "NotInstalled", or "Unknown".
    ''' Parses the STATE line from `sc query SERVICE_NAME`.
    ''' </summary>
    Public Function GetWindowsServiceStatus(serviceName As String) As String
        If Not ConfigHelpers.RunningOnWindows() Then Return "NotApplicable"
        If String.IsNullOrWhiteSpace(serviceName) Then serviceName = DefaultServiceName

        Dim output As String = Nothing
        Dim ok = RunSc({"query", serviceName}, output)
        If Not ok Then
            ' Most common cause: 1060 — "service does not exist".
            If output IsNot Nothing AndAlso output.Contains("1060") Then
                Return "NotInstalled"
            End If
            Return "Unknown"
        End If

        If output Is Nothing Then Return "Unknown"
        For Each line In output.Split({vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
            Dim trimmed = line.Trim()
            If trimmed.StartsWith("STATE", StringComparison.OrdinalIgnoreCase) Then
                Dim upper = trimmed.ToUpperInvariant()
                If upper.Contains("RUNNING") Then Return "Running"
                If upper.Contains("STOPPED") Then Return "Stopped"
                If upper.Contains("START_PENDING") Then Return "Starting"
                If upper.Contains("STOP_PENDING") Then Return "Stopping"
                Return "Unknown"
            End If
        Next
        Return "Unknown"
    End Function

    Private Function RunSc(args As String(), ByRef output As String) As Boolean
        Try
            Dim psi As New ProcessStartInfo("sc.exe") With {
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .UseShellExecute = False,
                .CreateNoWindow = True
            }
            For Each a In args
                psi.ArgumentList.Add(a)
            Next

            Using proc = Process.Start(psi)
                Dim stdOut = proc.StandardOutput.ReadToEnd()
                Dim stdErr = proc.StandardError.ReadToEnd()
                proc.WaitForExit()
                output = (stdOut & stdErr).Trim()
                Return proc.ExitCode = 0
            End Using
        Catch ex As Exception
            output = ex.Message
            Return False
        End Try
    End Function

    ' --------------------------------------------------------
    ' Linux path (systemd)
    ' --------------------------------------------------------

    ''' <summary>
    ''' Builds a systemd unit file as a string. The service is configured
    ''' as Type=simple (the node runs in the foreground), restarts on
    ''' failure, and runs as the supplied user. If runAsUser is empty the
    ''' unit is generated WITHOUT a User= line, letting the admin set it
    ''' before installing.
    ''' </summary>
    Public Function BuildSystemdUnit(runAsUser As String) As String
        Dim nodePath = GetNodeExecutablePath()
        Dim workingDir = Path.GetDirectoryName(nodePath)

        Dim sb As New StringBuilder()
        sb.AppendLine("[Unit]")
        sb.AppendLine("Description=PowerGSM Node — game server management agent")
        sb.AppendLine("After=network-online.target")
        sb.AppendLine("Wants=network-online.target")
        sb.AppendLine()
        sb.AppendLine("[Service]")
        sb.AppendLine("Type=simple")
        sb.AppendLine("ExecStart=" & nodePath)
        sb.AppendLine("WorkingDirectory=" & workingDir)
        If Not String.IsNullOrWhiteSpace(runAsUser) Then
            sb.AppendLine("User=" & runAsUser.Trim())
        End If
        sb.AppendLine("Restart=on-failure")
        sb.AppendLine("RestartSec=5")
        sb.AppendLine("StandardOutput=journal")
        sb.AppendLine("StandardError=journal")
        sb.AppendLine()
        sb.AppendLine("[Install]")
        sb.AppendLine("WantedBy=multi-user.target")
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' Writes the systemd unit to <output-dir>/gsmnode.service and
    ''' returns the absolute path. The caller is expected to print
    ''' user-facing instructions for moving the file into place.
    ''' </summary>
    Public Function WriteSystemdUnit(runAsUser As String) As String
        Dim unitPath = Path.Combine(AppContext.BaseDirectory, "gsmnode.service")
        File.WriteAllText(unitPath, BuildSystemdUnit(runAsUser))
        Return unitPath
    End Function

    ''' <summary>
    ''' Returns the three-line copy/enable/start instruction block users
    ''' must run as root after WriteSystemdUnit.
    ''' </summary>
    Public Function GetSystemdInstallInstructions(unitPath As String) As String
        Dim sb As New StringBuilder()
        sb.AppendLine("To install the systemd unit, run as root:")
        sb.AppendLine()
        sb.AppendLine("  sudo cp """ & unitPath & """ /etc/systemd/system/gsmnode.service")
        sb.AppendLine("  sudo systemctl daemon-reload")
        sb.AppendLine("  sudo systemctl enable --now gsmnode")
        sb.AppendLine()
        sb.AppendLine("Status and logs:")
        sb.AppendLine("  systemctl status gsmnode")
        sb.AppendLine("  journalctl -u gsmnode -f")
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' Returns "Running", "Stopped", "NotInstalled", or "Unknown" by
    ''' shelling out to `systemctl is-active`. Best-effort: returns
    ''' "Unknown" on any error so callers can fall back to the
    ''' user's own systemctl invocations.
    ''' </summary>
    Public Function GetSystemdStatus(serviceName As String) As String
        If ConfigHelpers.RunningOnWindows() Then Return "NotApplicable"
        If String.IsNullOrWhiteSpace(serviceName) Then serviceName = "gsmnode"

        Try
            Dim psi As New ProcessStartInfo("systemctl") With {
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .UseShellExecute = False,
                .CreateNoWindow = True
            }
            psi.ArgumentList.Add("is-active")
            psi.ArgumentList.Add(serviceName)

            Using proc = Process.Start(psi)
                Dim stdOut = proc.StandardOutput.ReadToEnd().Trim()
                proc.WaitForExit()
                Select Case stdOut
                    Case "active" : Return "Running"
                    Case "inactive" : Return "Stopped"
                    Case "failed" : Return "Failed"
                    Case "activating" : Return "Starting"
                    Case "deactivating" : Return "Stopping"
                    Case Else
                        ' systemctl returns "inactive" with exit 3 for an
                        ' unknown unit on some distros; check a sentinel.
                        If stdOut.Contains("could not be found", StringComparison.OrdinalIgnoreCase) Then
                            Return "NotInstalled"
                        End If
                        Return "Unknown"
                End Select
            End Using
        Catch
            Return "Unknown"
        End Try
    End Function

End Module
