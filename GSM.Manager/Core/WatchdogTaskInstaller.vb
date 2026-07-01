Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Xml.Linq

Namespace GSM.Manager.Core

    ' ============================================================
    '  WatchdogTaskInstaller  (Phase 5m-3)
    '
    '  Installs / queries / removes the per-user Task Scheduler
    '  logon task that launches GSM.Watchdog.exe at sign-in. The
    '  watchdog in turn launches and supervises the Manager.
    '
    '  Class with Shared members (not a Module) on purpose: a Module
    '  would hoist Install / IsInstalled / Uninstall into namespace
    '  scope and risk an ambiguity collision. Callers qualify as
    '  WatchdogTaskInstaller.X.
    '
    '  Design choices (see Phase5m_Plan.md "5m-3 design update"):
    '
    '    * Per-user ONLOGON task, RunLevel = LeastPrivilege. A
    '      normal-privilege task running as the current interactive
    '      user needs NO elevation to create, so the Settings toggle
    '      never triggers a UAC prompt. It runs with the interactive
    '      token (LogonType = InteractiveToken) so the Manager's
    '      window is visible and its DPAPI / per-user state is the
    '      same as a hand-launched Manager.
    '
    '    * Created via `schtasks /Create /XML`, NOT inline `/TR`.
    '      The install path contains spaces ("PowerGSM stuff"), and
    '      /TR quoting around a spaced path is fragile; an XML
    '      definition separates Command from Arguments cleanly and
    '      is the only way to set the RestartOnFailure backstop.
    '
    '    * NOT a Windows service. A true service install is the
    '      separate (parked) 5m-4. This is a logon task on purpose:
    '      interactive, no stored credentials, no UAC.
    '
    '  The watchdog is co-located next to GSM.Manager.exe by the
    '  Manager's build/publish targets, so the task points at
    '  AppContext.BaseDirectory\GSM.Watchdog.exe.
    ' ============================================================

    Public Class WatchdogTaskInstaller

        ''' <summary>Task Scheduler task name (and \-rooted URI).</summary>
        Public Const TaskName As String = "PowerGSM Watchdog"

        ''' <summary>Full path to the co-located watchdog exe.</summary>
        Public Shared Function WatchdogExePath() As String
            Return Path.Combine(AppContext.BaseDirectory, "GSM.Watchdog.exe")
        End Function

        ''' <summary>True if the watchdog exe is present next to the Manager.</summary>
        Public Shared Function WatchdogExeExists() As Boolean
            Try
                Return File.Exists(WatchdogExePath())
            Catch
                Return False
            End Try
        End Function

        ''' <summary>
        ''' True if the logon task exists. Probe via `schtasks /Query`;
        ''' exit code 0 means it's registered.
        ''' </summary>
        Public Shared Function IsInstalled() As Boolean
            Try
                Dim outText As String = "", errText As String = ""
                Dim code = RunSchtasks($"/Query /TN ""{TaskName}""", outText, errText)
                Return code = 0
            Catch
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Reconcile the task with the desired state: install if
        ''' wanted and absent, remove if unwanted and present, no-op
        ''' if already matching. Returns Nothing on success, or a
        ''' human-readable error string on failure.
        ''' </summary>
        Public Shared Function SetInstalled(desired As Boolean) As String
            Try
                Dim installed = IsInstalled()
                If desired AndAlso Not installed Then
                    Return Install()
                ElseIf (Not desired) AndAlso installed Then
                    Return Uninstall()
                End If
                Return Nothing
            Catch ex As Exception
                Return ex.Message
            End Try
        End Function

        ''' <summary>
        ''' Register the logon task. Writes the XML definition to a
        ''' temp file (UTF-16, which schtasks expects) and feeds it to
        ''' `schtasks /Create /XML /F`.
        ''' </summary>
        Public Shared Function Install() As String
            Dim exe = WatchdogExePath()
            If Not File.Exists(exe) Then
                Return $"The watchdog wasn't found next to the Manager (expected at {exe})." &
                       " Rebuild so it's co-located, then try again."
            End If

            Dim xml = BuildTaskXml(exe)
            Dim tmp = Path.Combine(Path.GetTempPath(), "powergsm-watchdog-task.xml")
            Try
                ' schtasks wants UTF-16 to match the XML declaration.
                File.WriteAllText(tmp, xml, Encoding.Unicode)

                Dim outText As String = "", errText As String = ""
                Dim code = RunSchtasks($"/Create /TN ""{TaskName}"" /XML ""{tmp}"" /F", outText, errText)
                If code <> 0 Then
                    Dim detail = PickMessage(outText, errText)
                    Return $"schtasks /Create returned {code}." & If(detail = "", "", " " & detail)
                End If
                Return Nothing
            Finally
                Try : File.Delete(tmp) : Catch : End Try
            End Try
        End Function

        ''' <summary>Remove the logon task.</summary>
        Public Shared Function Uninstall() As String
            Dim outText As String = "", errText As String = ""
            Dim code = RunSchtasks($"/Delete /TN ""{TaskName}"" /F", outText, errText)
            If code <> 0 Then
                Dim detail = PickMessage(outText, errText)
                Return $"schtasks /Delete returned {code}." & If(detail = "", "", " " & detail)
            End If
            Return Nothing
        End Function

        ' ---- internals ----

        ''' <summary>
        ''' Build the Task Scheduler 1.2 XML. VB XML literals auto-
        ''' escape the embedded path / user values, so no manual
        ''' escaping is needed. The literal carries the default task
        ''' namespace on the root element. The XML declaration is
        ''' prepended separately (XElement.ToString omits it) and
        ''' must say UTF-16 to match the file encoding we write.
        ''' </summary>
        Private Shared Function BuildTaskXml(exe As String) As String
            Dim user = Environment.UserDomainName & "\" & Environment.UserName
            Dim workDir = Path.GetDirectoryName(exe)

            ' NOTE: the child-element ORDER inside <Settings> is enforced
            ' by the Task Scheduler schema — schtasks rejects an
            ' out-of-order node with "unexpected node". Keep this sequence
            ' matching a real Windows task export. UseUnifiedSchedulingEngine
            ' and DisallowStartOnRemoteAppSession are intentionally omitted:
            ' both are optional with sensible defaults, and their placement
            ' tripped the validator (the (36,7) DisallowStartOnRemoteAppSession
            ' error). Don't reintroduce them without matching the exact
            ' schema position.
            Dim doc =
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                    <RegistrationInfo>
                        <Description>Launches and supervises the PowerGSM Manager, relaunching it if it exits unexpectedly.</Description>
                        <Author>PowerGSM</Author>
                        <URI>\<%= TaskName %></URI>
                    </RegistrationInfo>
                    <Triggers>
                        <LogonTrigger>
                            <Enabled>true</Enabled>
                            <UserId><%= user %></UserId>
                        </LogonTrigger>
                    </Triggers>
                    <Principals>
                        <Principal id="Author">
                            <UserId><%= user %></UserId>
                            <LogonType>InteractiveToken</LogonType>
                            <RunLevel>LeastPrivilege</RunLevel>
                        </Principal>
                    </Principals>
                    <Settings>
                        <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                        <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                        <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                        <AllowHardTerminate>true</AllowHardTerminate>
                        <StartWhenAvailable>true</StartWhenAvailable>
                        <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                        <IdleSettings>
                            <StopOnIdleEnd>false</StopOnIdleEnd>
                            <RestartOnIdle>false</RestartOnIdle>
                        </IdleSettings>
                        <AllowStartOnDemand>true</AllowStartOnDemand>
                        <Enabled>true</Enabled>
                        <Hidden>false</Hidden>
                        <RunOnlyIfIdle>false</RunOnlyIfIdle>
                        <WakeToRun>false</WakeToRun>
                        <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                        <Priority>7</Priority>
                        <RestartOnFailure>
                            <Interval>PT1M</Interval>
                            <Count>3</Count>
                        </RestartOnFailure>
                    </Settings>
                    <Actions Context="Author">
                        <Exec>
                            <Command><%= exe %></Command>
                            <WorkingDirectory><%= workDir %></WorkingDirectory>
                        </Exec>
                    </Actions>
                </Task>

            Return "<?xml version=""1.0"" encoding=""UTF-16""?>" & Environment.NewLine & doc.ToString()
        End Function

        ''' <summary>
        ''' Run schtasks.exe with no window, capturing stdout/stderr
        ''' and the exit code.
        ''' </summary>
        Private Shared Function RunSchtasks(arguments As String,
                                            ByRef stdOut As String,
                                            ByRef stdErr As String) As Integer
            Dim psi As New ProcessStartInfo() With {
                .FileName = "schtasks.exe",
                .Arguments = arguments,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True
            }
            Using p = Process.Start(psi)
                ' Read before WaitForExit so a chatty schtasks can't
                ' fill a pipe and deadlock.
                stdOut = p.StandardOutput.ReadToEnd()
                stdErr = p.StandardError.ReadToEnd()
                p.WaitForExit()
                Return p.ExitCode
            End Using
        End Function

        ''' <summary>Prefer stderr, fall back to stdout, trimmed.</summary>
        Private Shared Function PickMessage(outText As String, errText As String) As String
            Dim s = If(String.IsNullOrWhiteSpace(errText), outText, errText)
            If s Is Nothing Then Return ""
            Return s.Trim()
        End Function

    End Class

End Namespace
