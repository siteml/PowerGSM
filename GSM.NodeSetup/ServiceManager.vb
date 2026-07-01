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

    ''' <summary>
    ''' Ensures the GSM.Node binary has +x on Linux. Files copied via
    ''' SCP/SFTP from a Windows publish typically arrive at mode 0644,
    ''' which prevents systemd (and the user) from running them. This
    ''' setup tool is already running so it has +x on itself; from here
    ''' we can fix its sibling Node and per-instance GSM.Shim binaries in
    ''' one shot.
    '''
    ''' Uses File.SetUnixFileMode (.NET 7+). No-op on Windows. Best
    ''' effort — a chmod failure isn't fatal because the operator can
    ''' still chmod manually, and systemd will surface a clear error
    ''' on enable if the bit didn't get set.
    ''' </summary>
    Public Sub EnsureNodeExecutable()
        If ConfigHelpers.RunningOnWindows() Then Return

        EnsureExecutable(GetNodeExecutablePath())
        EnsureShimsExecutable()
    End Sub

    ''' <summary>
    ''' Sets +x on every deployed GSM.Shim binary. The node launches a
    ''' per-instance shim from GSM.Shim/&lt;version&gt;/GSM.Shim (highest
    ''' version wins), and like the Node binary these arrive at mode 0644
    ''' from a Windows SCP/SFTP publish, which makes the node's launch fail
    ''' with a permission error that surfaces as "shim spawn failed". We
    ''' chmod every version folder present, not just the newest, since it's
    ''' cheap and avoids a surprise if an older one is ever selected.
    ''' </summary>
    Private Sub EnsureShimsExecutable()
        Try
            Dim shimRoot = Path.Combine(AppContext.BaseDirectory, "GSM.Shim")
            If Not Directory.Exists(shimRoot) Then Return
            For Each verDir In Directory.EnumerateDirectories(shimRoot)
                Dim shimPath = Path.Combine(verDir, "GSM.Shim")
                If File.Exists(shimPath) Then EnsureExecutable(shimPath)
            Next
        Catch
            ' Best effort; the operator can still chmod manually.
        End Try
    End Sub

    ''' <summary>
    ''' Adds the execute bit (user/group/other) to a single file if it
    ''' isn't already set. No-op when the file is missing or on any error
    ''' (the operator can still chmod manually). Caller guarantees Linux.
    ''' </summary>
    Private Sub EnsureExecutable(path As String)
        If Not File.Exists(path) Then Return
        Try
            Dim currentMode = File.GetUnixFileMode(path)
            Dim newMode = currentMode Or
                          UnixFileMode.UserExecute Or
                          UnixFileMode.GroupExecute Or
                          UnixFileMode.OtherExecute
            If currentMode <> newMode Then
                File.SetUnixFileMode(path, newMode)
            End If
        Catch
            ' Best effort; the operator can still chmod manually.
        End Try
    End Sub

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

    ''' <summary>
    ''' Starts the GSMNode Windows service via `sc start`. Used by the
    ''' self-update relauncher (SelfUpdateApply) after it has swapped the new
    ''' binary into place. On Windows only. Treats sc error 1056 ("an instance
    ''' of the service is already running") as success, since for the relaunch
    ''' use case an already-running service is the desired end state.
    ''' </summary>
    Public Function StartWindowsService(serviceName As String) As ServiceResult
        If Not ConfigHelpers.RunningOnWindows() Then
            Return New ServiceResult With {.Success = False, .Message = "Not on Windows."}
        End If
        If String.IsNullOrWhiteSpace(serviceName) Then serviceName = DefaultServiceName

        Dim out As String = Nothing
        Dim ok = RunSc({"start", serviceName}, out)
        If Not ok AndAlso out IsNot Nothing AndAlso out.Contains("1056") Then
            ok = True
        End If

        Return New ServiceResult With {
            .Success = ok,
            .Message = If(ok,
                          $"Service '{serviceName}' start requested.",
                          $"Failed to start service '{serviceName}'."),
            .Output = out
        }
    End Function

    ''' <summary>
    ''' Stops the GSMNode Windows service via `sc stop`. Used by the self-update
    ''' relauncher's rollback path (SelfUpdateApply) to release the running
    ''' binary before swapping the previous one back. On Windows only. Treats sc
    ''' error 1062 ("the service has not been started") as success, since a
    ''' not-running service is the desired end state for a rollback.
    ''' </summary>
    Public Function StopWindowsService(serviceName As String) As ServiceResult
        If Not ConfigHelpers.RunningOnWindows() Then
            Return New ServiceResult With {.Success = False, .Message = "Not on Windows."}
        End If
        If String.IsNullOrWhiteSpace(serviceName) Then serviceName = DefaultServiceName

        Dim out As String = Nothing
        Dim ok = RunSc({"stop", serviceName}, out)
        If Not ok AndAlso out IsNot Nothing AndAlso out.Contains("1062") Then
            ok = True
        End If

        Return New ServiceResult With {
            .Success = ok,
            .Message = If(ok,
                          $"Service '{serviceName}' stop requested.",
                          $"Failed to stop service '{serviceName}'."),
            .Output = out
        }
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
    ''' as Type=notify (so systemd knows when the node is actually ready —
    ''' the node calls UseSystemd() and sends READY=1 on startup), restarts
    ''' on failure, and runs as the supplied user. If runAsUser is empty the
    ''' unit is generated WITHOUT a User= line, letting the admin set it
    ''' before installing. Emits KillMode=process so the per-instance
    ''' GSM.Shim supervisors and their game servers survive a node restart
    ''' (the node re-adopts them) instead of being cgroup-killed along with
    ''' the service on `systemctl stop`.
    '''
    ''' ExecStartPre is an apply-or-revert step (Phase 8-2 slices 6 + 8b-2):
    ''' a staged GSM.Node.new is applied (keeping the previous binary as
    ''' GSM.Node.old) and marked GSM.Node.update-pending; if a later start
    ''' still sees that marker — i.e. the applied update never became healthy
    ''' and cleared it — the unit rolls GSM.Node.old back over the bad binary
    ''' (quarantined as GSM.Node.failed). StartLimit* bounds the restart loop
    ''' so a hopeless binary stops thrashing once the rollback has had its
    ''' chance.
    ''' </summary>
    Public Function BuildSystemdUnit(runAsUser As String) As String
        Dim nodePath = GetNodeExecutablePath()
        Dim workingDir = Path.GetDirectoryName(nodePath)
        ' Phase 8-2 self-update: the node stages an update next to itself as
        ' "<node>.new" and exits; this unit swaps it into place on the next
        ' start, keeping the previous binary as "<node>.old" for rollback.
        ' 8b-2 adds a health gate: applying a .new drops a ".update-pending"
        ' marker that the node clears once healthy; a marker that outlives a
        ' start means the update failed, so we revert .old and quarantine the
        ' bad binary as ".failed".
        Dim stagedNewPath = nodePath & ".new"
        Dim keptOldPath = nodePath & ".old"
        Dim quarantinedPath = nodePath & ".failed"
        Dim pendingMarker = nodePath & ".update-pending"

        Dim sb As New StringBuilder()
        sb.AppendLine("[Unit]")
        sb.AppendLine("Description=PowerGSM Node — game server management agent")
        sb.AppendLine("After=network-online.target")
        sb.AppendLine("Wants=network-online.target")
        ' Bound the restart loop so a binary that can't come up (or a failed
        ' rollback) stops thrashing. The 8b-2 revert needs ~2 starts (bad ->
        ' rollback -> good), well within the burst; this only trips on a
        ' genuinely hopeless loop.
        sb.AppendLine("StartLimitIntervalSec=200")
        sb.AppendLine("StartLimitBurst=5")
        sb.AppendLine()
        sb.AppendLine("[Service]")
        ' Type=notify: the node integrates systemd (UseSystemd()) and sends
        ' READY=1 once the host is listening, so a binary that starts but never
        ' becomes ready counts as a FAILED start (Restart=on-failure fires) —
        ' the "hung but alive" case Type=simple can't detect. This is what lets
        ' the 8b-2 health gate catch an update that comes up broken.
        sb.AppendLine("Type=notify")
        sb.AppendLine("NotifyAccess=main")
        ' Idempotent apply-or-revert, runs before every ExecStart:
        '   * If GSM.Node.new is staged (Phase 8-2): move the live binary aside
        '     to GSM.Node.old, the staged one into place, re-mark it +x (a
        '     freshly-written .new may arrive 0644), and drop a .update-pending
        '     marker. The node deletes that marker once it has been healthy for
        '     a grace period (NodeProgram).
        '   * Else if a .update-pending marker is still here AND a .old exists
        '     (8b-2): the last applied update never confirmed healthy, so roll
        '     back — quarantine the bad live binary as .failed, restore .old,
        '     +x, and clear the marker. Restart=on-failure drives the restart
        '     that reaches this branch; StartLimit* stops a hopeless loop.
        '   * Else: no-op (every normal start).
        ' The presence of .new is the entire forward-update state, so a
        ' staged-but-unapplied update (crash/reboot/power-loss mid-update) is
        ' simply applied on the next start. All renames are within the node's
        ' own powergsm-owned directory (no extra privilege) and atomic within
        ' one filesystem, so the binary is only ever old/new/failed, never torn.
        ' No `set -e`, and each branch ends on an exit-0 command, so an
        ' interruption self-heals next start and ExecStartPre never blocks
        ' ExecStart.
        sb.AppendLine(
            $"ExecStartPre=/bin/sh -c 'if [ -f ""{stagedNewPath}"" ]; then mv -f ""{nodePath}"" ""{keptOldPath}""; mv -f ""{stagedNewPath}"" ""{nodePath}""; chmod +x ""{nodePath}""; touch ""{pendingMarker}""; elif [ -f ""{pendingMarker}"" ] && [ -f ""{keptOldPath}"" ]; then mv -f ""{nodePath}"" ""{quarantinedPath}""; mv -f ""{keptOldPath}"" ""{nodePath}""; chmod +x ""{nodePath}""; rm -f ""{pendingMarker}""; fi'")
        sb.AppendLine("ExecStart=" & nodePath)
        sb.AppendLine("WorkingDirectory=" & workingDir)
        If Not String.IsNullOrWhiteSpace(runAsUser) Then
            sb.AppendLine("User=" & runAsUser.Trim())
        End If
        sb.AppendLine("Restart=on-failure")
        sb.AppendLine("RestartSec=5")
        ' KillMode=process is load-bearing for restart-survivable instances.
        ' The node launches a per-instance GSM.Shim that owns the actual game
        ' process; both live in this unit's cgroup. Under the systemd default
        ' (KillMode=control-group), `systemctl stop` SIGKILLs the entire
        ' cgroup - node, shim, AND game - so a node bounce takes every server
        ' down. process mode signals only the main node; the shim + game
        ' (reparented to PID 1) survive, and the next node re-adopts them over
        ' their sockets. Trade-off: `systemctl stop gsmnode` now leaves games
        ' running by design - stop instances from the manager first for a
        ' full teardown.
        sb.AppendLine("# Signal only the node on stop so the per-instance shims")
        sb.AppendLine("# and their game servers survive a node restart (re-adopted).")
        sb.AppendLine("KillMode=process")
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
    '''
    ''' Uses `install -m 644` rather than `cp` for two reasons:
    '''   1. Many distros alias `cp` to `cp -i` for safety, which
    '''      prompts on overwrite and silently waits for stdin if a
    '''      previous unit file already exists at the destination —
    '''      this looked like the command "freezing" the terminal.
    '''   2. `install` is the standard systemd-packaging convention
    '''      and sets ownership/permissions explicitly (mode 644 is
    '''      what systemctl expects for unit files).
    ''' Paths under our control don't need quoting (no spaces in
    ''' AppContext.BaseDirectory in any sane Linux deployment), so we
    ''' emit them unquoted to keep the command paste-friendly.
    ''' </summary>
    Public Function GetSystemdInstallInstructions(unitPath As String) As String
        Dim sb As New StringBuilder()
        sb.AppendLine("To install the systemd unit, run as root:")
        sb.AppendLine()
        sb.AppendLine("  sudo install -m 644 " & unitPath & " /etc/systemd/system/gsmnode.service")
        sb.AppendLine("  sudo systemctl daemon-reload")
        sb.AppendLine("  sudo systemctl enable --now gsmnode")
        sb.AppendLine()
        sb.AppendLine("Status and logs:")
        sb.AppendLine("  systemctl status gsmnode")
        sb.AppendLine("  journalctl -u gsmnode -f")
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' Performs the systemd install directly when the setup tool is
    ''' running as root. Replaces the manual three-command sequence
    ''' from GetSystemdInstallInstructions when we have the privileges
    ''' to do it ourselves. Returns the combined output of all three
    ''' commands (install, daemon-reload, enable --now) so the caller
    ''' can show the operator what happened.
    '''
    ''' On any failure, returns Success=False and Output containing
    ''' the failing command's stderr. The unit file at <unitPath> is
    ''' left in place so the operator can retry manually.
    ''' </summary>
    Public Function InstallSystemdServiceAsRoot(unitPath As String) As ServiceResult
        If ConfigHelpers.RunningOnWindows() Then
            Return New ServiceResult With {.Success = False, .Message = "Not on Linux."}
        End If
        If Not ConfigHelpers.RunningElevated() Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "Root privileges are required to install a systemd service."
            }
        End If
        If Not File.Exists(unitPath) Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "Unit file not found at: " & unitPath
            }
        End If

        Dim combinedOutput As New StringBuilder()

        ' Step 1: install -m 644 <unitPath> /etc/systemd/system/gsmnode.service
        Dim installOut As String = Nothing
        If Not RunCommand("install",
                          {"-m", "644", unitPath, "/etc/systemd/system/gsmnode.service"},
                          installOut) Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "Failed to copy unit file to /etc/systemd/system.",
                .Output = installOut
            }
        End If
        combinedOutput.AppendLine("Installed unit file to /etc/systemd/system/gsmnode.service")

        ' Step 2: systemctl daemon-reload
        Dim reloadOut As String = Nothing
        If Not RunCommand("systemctl", {"daemon-reload"}, reloadOut) Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "systemctl daemon-reload failed.",
                .Output = combinedOutput.ToString() & vbLf & reloadOut
            }
        End If
        combinedOutput.AppendLine("systemd daemon reloaded")

        ' Step 3: systemctl enable gsmnode
        '
        ' Just enable, no --now. The original code used `enable --now`,
        ' which starts the unit only if it's not already running — so a
        ' re-run that wanted to apply a new User= or other unit-file change
        ' would silently leave the old process going with the old config.
        ' We split enable from start, then unconditionally `restart` below
        ' so config changes always take effect.
        Dim enableOut As String = Nothing
        If Not RunCommand("systemctl", {"enable", "gsmnode"}, enableOut) Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "systemctl enable failed. Run `systemctl status gsmnode` for details.",
                .Output = combinedOutput.ToString() & vbLf & enableOut
            }
        End If
        combinedOutput.AppendLine("Service enabled")

        ' Step 4: systemctl restart gsmnode
        '
        ' restart works as both "start when stopped" and "stop+start when
        ' running", so the same command path covers a fresh install and a
        ' config-update re-run. Important: `daemon-reload` above only
        ' refreshes systemd's in-memory unit graph; the running process
        ' itself doesn't pick up the new ExecStart / User= / etc. without
        ' an explicit restart.
        Dim restartOut As String = Nothing
        If Not RunCommand("systemctl", {"restart", "gsmnode"}, restartOut) Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "systemctl restart failed. Run `systemctl status gsmnode` for details.",
                .Output = combinedOutput.ToString() & vbLf & restartOut
            }
        End If
        combinedOutput.AppendLine("Service started")

        Return New ServiceResult With {
            .Success = True,
            .Message = "Service 'gsmnode' installed, enabled, and started.",
            .Output = combinedOutput.ToString()
        }
    End Function

    ''' <summary>
    ''' Returns True if a Linux user account with the given name exists.
    ''' Uses `getent passwd` which works without root and consults all
    ''' configured NSS sources (local /etc/passwd, LDAP, etc.) rather
    ''' than just reading the file directly. Empty/whitespace names
    ''' return False without invoking getent.
    ''' </summary>
    Public Function CheckLinuxUserExists(userName As String) As Boolean
        If ConfigHelpers.RunningOnWindows() Then Return False
        If String.IsNullOrWhiteSpace(userName) Then Return False
        Dim output As String = Nothing
        Return RunCommand("getent", {"passwd", userName.Trim()}, output)
    End Function

    ''' <summary>
    ''' Creates a system account suitable for running the node service:
    '''   - --system          UID below 1000 (service-account range)
    '''   - --create-home     /home/&lt;name&gt; for steam_appid.txt,
    '''                       ~/.steam/sdk64/steamclient.so symlinks,
    '''                       Steam content cache
    '''   - --shell /bin/bash so the operator can `sudo -u &lt;name&gt; bash`
    '''                       to debug; not a security concern because
    '''                       no password is set
    ''' useradd's default group behaviour creates a same-named primary
    ''' group, so we don't pass --user-group explicitly.
    '''
    ''' Idempotent against existing accounts: callers should check via
    ''' CheckLinuxUserExists first; this function reports useradd's
    ''' "already exists" exit code as a failure.
    ''' </summary>
    Public Function CreateLinuxSystemUser(userName As String) As ServiceResult
        If ConfigHelpers.RunningOnWindows() Then
            Return New ServiceResult With {.Success = False, .Message = "Not on Linux."}
        End If
        If Not ConfigHelpers.RunningElevated() Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "Root privileges are required to create a system user."
            }
        End If
        If String.IsNullOrWhiteSpace(userName) Then
            Return New ServiceResult With {.Success = False, .Message = "User name cannot be empty."}
        End If

        Dim out As String = Nothing
        Dim ok = RunCommand("useradd",
                             {"--system", "--create-home", "--shell", "/bin/bash", userName.Trim()},
                             out)
        If ok Then
            Return New ServiceResult With {
                .Success = True,
                .Message = $"Created system user '{userName.Trim()}'.",
                .Output = out
            }
        End If
        Return New ServiceResult With {
            .Success = False,
            .Message = $"useradd failed for '{userName.Trim()}'.",
            .Output = out
        }
    End Function

    ''' <summary>
    ''' Ensures &lt;path&gt; exists (mkdir -p) and is owned recursively by
    ''' &lt;userName&gt;:&lt;userName&gt;. Used to hand the install / data /
    ''' servers directories to the service account so the node can read
    ''' and write them after dropping root.
    '''
    ''' We chown to user:user (rather than user:nogroup or anything
    ''' similar) because useradd's default behaviour creates a primary
    ''' group with the same name as the user. If a site has overridden
    ''' that, the operator can fix permissions manually — we don't
    ''' second-guess /etc/login.defs.
    '''
    ''' Idempotent: mkdir -p succeeds on existing dirs; chown -R on an
    ''' already-correct tree is a no-op.
    ''' </summary>
    Public Function PrepareDirAndChown(path As String, userName As String) As ServiceResult
        If ConfigHelpers.RunningOnWindows() Then
            Return New ServiceResult With {.Success = False, .Message = "Not on Linux."}
        End If
        If Not ConfigHelpers.RunningElevated() Then
            Return New ServiceResult With {
                .Success = False,
                .Message = "Root privileges are required to chown directories."
            }
        End If
        If String.IsNullOrWhiteSpace(path) Then
            Return New ServiceResult With {.Success = True, .Message = "(no path supplied; skipped)"}
        End If
        If String.IsNullOrWhiteSpace(userName) Then
            Return New ServiceResult With {.Success = False, .Message = "User name cannot be empty."}
        End If

        Dim p = path.Trim()
        Dim u = userName.Trim()

        ' mkdir -p — create the directory (and parents) if missing.
        Dim mkdirOut As String = Nothing
        If Not RunCommand("mkdir", {"-p", p}, mkdirOut) Then
            Return New ServiceResult With {
                .Success = False,
                .Message = $"mkdir -p failed for {p}.",
                .Output = mkdirOut
            }
        End If

        ' chown -R user:user path. The colon form makes the primary
        ' group match the user's primary group on most distros.
        Dim chownOut As String = Nothing
        If RunCommand("chown", {"-R", $"{u}:{u}", p}, chownOut) Then
            Return New ServiceResult With {
                .Success = True,
                .Message = $"{p} → {u}:{u}",
                .Output = chownOut
            }
        End If
        Return New ServiceResult With {
            .Success = False,
            .Message = $"chown failed for {p}.",
            .Output = chownOut
        }
    End Function

    ''' <summary>
    ''' Generic wrapper around Process.Start with stdout+stderr capture.
    ''' Returns True iff the command exited with code 0. The caller is
    ''' expected to handle non-zero with a meaningful message rather
    ''' than passing the raw output up.
    ''' </summary>
    Private Function RunCommand(fileName As String,
                                 args As String(),
                                 ByRef output As String) As Boolean
        Try
            Dim psi As New ProcessStartInfo(fileName) With {
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
