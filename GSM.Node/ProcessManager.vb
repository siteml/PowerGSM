Imports System
Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Plugin
Imports GSM.Node.Api
Imports Microsoft.Extensions.Logging

' ============================================================
'  ProcessManager — owns all game server processes on this node.
'
'  Responsibilities:
'    - Start/stop/restart game server processes
'    - Capture stdout/stderr and feed to RingBufferStore
'    - Detect crashes (exit code + log pattern)
'    - Apply CrashRestartPolicy (backoff, crash loop detection)
'    - Persist state to NodeDatabase for node restart recovery
'    - Report per-process metrics (PID, CPU, memory)
'
'  The ProcessManager never interprets game-specific logic.
'  It receives plain data (exe path, args, env vars) from the
'  manager via REST and executes it.
'
'  Two spawn paths:
'
'    1. Stdout-captured (CaptureStdout=True): plugin has not
'       declared file log sources, so stdout/stderr are the
'       authoritative log stream. Spawn via the standard
'       ProcessStartInfo path with RedirectStandardOutput +
'       RedirectStandardError. .NET reads from the pipes and
'       forwards every line to the ring buffer + EventStore.
'
'    2. File-logged hidden console (CaptureStdout=False, Windows
'       only): plugin has declared file log sources. Spawn via
'       native CreateProcessW with CREATE_NEW_CONSOLE plus an
'       SW_HIDE startupinfo so the child has its own (invisible)
'       console. The child's stdout writes to that hidden console
'       buffer rather than to the node's terminal; file tailers
'       handle log capture, and the hidden console exists so
'       AttachConsole + GenerateConsoleCtrlEvent can deliver
'       CTRL_C_EVENT to the child for graceful shutdown — UE4
'       servers respond to CTRL_C_EVENT and nothing else.
'
'       Strategy B has two sub-variants chosen by the plugin's
'       LaunchOptions:
'
'         Direct (default for file-logged plugins): spawn the
'         game's exe directly. CREATE_NEW_CONSOLE gives it a
'         fresh hidden console; its stdout writes go there. Last
'         Oasis runs on this path. Verified by the fact that a
'         conhost.exe spawns alongside MistServer in Task Manager
'         — proof that CREATE_NEW_CONSOLE took effect.
'
'         Wrapped (RequiresConsoleIsolation = True, e.g. Factorio):
'         spawn cmd.exe /S /c "…" instead of the game's exe
'         directly. cmd owns the new hidden console; the game
'         inherits it from cmd. This works around games that do
'         FreeConsole + AttachConsole(ATTACH_PARENT_PROCESS) at
'         startup — a common headless-Windows pattern. With direct
'         spawn, the "parent" they reattach to is the node, so
'         output ends up on the node's terminal. With the cmd
'         wrapper, the "parent" is cmd, and reattaching lands on
'         cmd's hidden console — invisible. Tracking cmd.exe as the
'         managed process is sufficient: cmd /c forwards the
'         child's exit code, Process.Kill(entireProcessTree:=True)
'         takes both down together, and CtrlCSender's AttachConsole
'         + GenerateConsoleCtrlEvent(0) reaches every process in
'         cmd's console group — i.e. cmd AND the game.
'
'       The plugin contract is two booleans on LaunchOptions:
'       StdoutIsLog (forces Strategy A) and RequiresConsoleIsolation
'       (forces Strategy C, when Strategy B/C is in scope). That
'       way plugins describe what their game needs in concrete
'       terms; node-side strategy names like "A/B/C" stay on the
'       node. Future implementation alternatives — e.g. true NUL
'       stdio via STARTUPINFOEX + PROC_THREAD_ATTRIBUTE_HANDLE_LIST
'       — can be added as new strategies without touching the
'       contract, because the contract describes intent rather than
'       mechanism.
'
'       The cmd wrapper for Factorio was identified empirically:
'       Factorio launched directly never spawned a conhost.exe
'       (whereas LO always does), proving CREATE_NEW_CONSOLE was
'       being defeated by an AttachConsole trick. An earlier
'       attempt at "true NUL redirection" via STARTF_USESTDHANDLES
'       with inheritable NUL handles also failed —
'       bInheritHandles=True in combination with CREATE_NEW_CONSOLE
'       silently broke the new-console allocation on this test
'       bench. The cmd wrapper sidesteps both issues by not
'       changing what we pass to CreateProcess at all — only what
'       we point CreateProcess at.
' ============================================================

Namespace GSM.Node

    Public Class ProcessManager

        Private ReadOnly _instances As New ConcurrentDictionary(Of String, ManagedInstance)
        Private ReadOnly _logStore As RingBufferStore
        Private ReadOnly _database As NodeDatabase
        Private ReadOnly _eventStore As EventStore
        Private ReadOnly _logger As Microsoft.Extensions.Logging.ILogger(Of ProcessManager)

        Public Sub New(logStore As RingBufferStore,
                       database As NodeDatabase,
                       eventStore As EventStore,
                       logger As Microsoft.Extensions.Logging.ILogger(Of ProcessManager))
            _logStore = logStore
            _database = database
            _eventStore = eventStore
            _logger = logger
        End Sub

        ' ============================================================
        '  Node status
        ' ============================================================

        Public Function GetNodeStatus(config As NodeConfiguration) As NodeStatusResponse
            Dim resp As New NodeStatusResponse()
            resp.NodeId = config.NodeId
            resp.MachineName = Environment.MachineName
            resp.OsDescription = RuntimeInformation.OSDescription
            resp.RunningInstanceCount = _instances.Values.
                Where(Function(m) m.State = InstanceState.Running).Count()
            resp.NodeVersion = GetType(ProcessManager).Assembly.
                GetName().Version?.ToString()
            ' Surface the resolved servers directory so the manager
            ' can suggest install paths. NodeConfiguration.EnsureDefaults
            ' has already converted this to absolute by the time we
            ' get here — the manager doesn't have to know the node's
            ' working directory.
            resp.ServersDirectory = config.ServersDirectory

            ' Metrics — best-effort, platform-dependent
            Try
                Dim proc = Process.GetCurrentProcess()
                resp.UptimeSeconds = CLng((DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds)
                resp.MemoryUsedMb = proc.WorkingSet64 \ (1024L * 1024L)
            Catch ex As Exception
                ' Ignore metric collection failures on some platforms
            End Try

            Return resp
        End Function

        ' ============================================================
        '  Instance lifecycle
        ' ============================================================

        Public Function StartInstanceAsync(request As StartInstanceRequest) As Task(Of InstanceStatusResponse)

            ' Check if already tracked
            Dim existing As ManagedInstance = Nothing
            If _instances.TryGetValue(request.InstanceId, existing) Then
                If existing.State = InstanceState.Running OrElse
                   existing.State = InstanceState.Starting Then
                    Return Task.FromResult(BuildStatusResponse(existing, NodeErrorCodes.InstanceAlreadyRunning,
                                               "Instance is already running"))
                End If
            End If

            Dim managed As New ManagedInstance()
            managed.InstanceId = request.InstanceId
            managed.State = InstanceState.Starting
            managed.StateChangedAt = DateTime.UtcNow
            managed.CrashPolicy = request.CrashPolicy
            managed.MaxCrashCount = request.MaxCrashCount
            managed.CrashWindowMinutes = request.CrashWindowMinutes
            managed.CrashCountResetAfterSeconds = request.CrashCountResetAfterSeconds
            managed.MinRestartDelayMs = request.MinRestartDelayMs
            managed.StopIntentPending = False
            managed.CrashCount = 0

            ' Build process start info. Note: the redirection flags
            ' are only honored by the stdout-captured path below;
            ' the hidden-console path bypasses ProcessStartInfo
            ' entirely and feeds these fields to native CreateProcess.
            Dim psi As New ProcessStartInfo()
            psi.FileName = request.ExePath
            psi.Arguments = If(request.Arguments, "")
            psi.WorkingDirectory = If(request.WorkingDirectory,
                                      Path.GetDirectoryName(request.ExePath))
            psi.UseShellExecute = False
            psi.WindowStyle = ProcessWindowStyle.Minimized
            psi.RedirectStandardInput = True
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.CreateNoWindow = True

            ' Apply environment variables
            If request.EnvironmentVars IsNot Nothing Then
                For Each kvp In request.EnvironmentVars
                    psi.EnvironmentVariables(kvp.Key) = kvp.Value
                Next
            End If

            ' Stash everything restart needs. Previously the restart path
            ' re-used managed.Process.StartInfo but never preserved file
            ' tailers, parse rules, or the captureStdout decision — so a
            ' crash-restart would lose all log capture for file-based games.
            managed.StartInfo = psi
            managed.LogFilePaths = request.LogFilePaths
            managed.ParseRules = request.LogParseRules

            ' Resolve the plugin-supplied LaunchOptions (StdoutIsLog
            ' and RequiresConsoleIsolation) into a concrete spawn
            ' strategy. Both booleans False is the implicit default
            ' for plugins that don't implement ILaunchOptionsProvider —
            ' the resolver picks Strategy A or B based on whether file
            ' log sources were declared, matching the legacy
            ' heuristic. CaptureStdout follows from the resolved
            ' strategy: only Strategy A captures stdio for the buffer.
            managed.StdoutIsLog = request.StdoutIsLog
            managed.RequiresConsoleIsolation = request.RequiresConsoleIsolation
            managed.Strategy = ResolveStrategy(request.StdoutIsLog,
                                                request.RequiresConsoleIsolation,
                                                request.LogFilePaths)
            managed.CaptureStdout = (managed.Strategy = SpawnStrategy.StdoutCapture)

            ' Resolve the file-tailer startup delay. Negative values
            ' (including the -1 "plugin didn't specify" sentinel) fall
            ' back to the 5000ms legacy default. 0 is honoured as an
            ' explicit opt-in to immediate tailing for engines that
            ' crash faster than the legacy delay would tolerate.
            If request.LogTailerStartDelayMs < 0 Then
                managed.LogTailerStartDelayMs = 5000
            Else
                managed.LogTailerStartDelayMs = request.LogTailerStartDelayMs
            End If

            Try
                Dim proc As Process = SpawnGameProcess(managed, psi)

                If proc Is Nothing Then
                    Return Task.FromResult(BuildErrorResponse(request.InstanceId,
                                              NodeErrorCodes.ProcessStartFailed,
                                              "Process spawn failed"))
                End If

                _instances(request.InstanceId) = managed
                FinalizeStart(managed, proc)

                _logger.LogInformation("Started instance {InstanceId} (PID {Pid})",
                                       request.InstanceId, managed.Pid)

                Return Task.FromResult(BuildStatusResponse(managed))

            Catch ex As Exception
                _logger.LogError(ex, "Failed to start instance {InstanceId}", request.InstanceId)
                Return Task.FromResult(BuildErrorResponse(request.InstanceId,
                                          NodeErrorCodes.ProcessStartFailed,
                                          ex.Message))
            End Try
        End Function

        Public Async Function StopInstanceAsync(request As StopInstanceRequest) As Task(Of InstanceStatusResponse)

            Dim managed As ManagedInstance = Nothing
            If Not _instances.TryGetValue(request.InstanceId, managed) Then
                Return BuildErrorResponse(request.InstanceId,
                                          NodeErrorCodes.InstanceNotFound,
                                          "Instance not found")
            End If

            If managed.State = InstanceState.Stopped OrElse
               managed.State = InstanceState.CrashLoopHalted Then
                Return BuildStatusResponse(managed, NodeErrorCodes.InstanceAlreadyStopped,
                                           "Instance is already stopped")
            End If

            ' Set stop intent so crash handler knows not to restart
            managed.StopIntentPending = True
            managed.State = InstanceState.Stopping
            managed.StateChangedAt = DateTime.UtcNow

            If managed.Process IsNot Nothing AndAlso Not managed.Process.HasExited Then
                Try
                    Dim timeout = If(request.GracefulTimeoutMs > 0,
                                     request.GracefulTimeoutMs, 20000)

                    Dim gracefulSignalSent = False

                    ' Try a real graceful shutdown signal. On Windows
                    ' this routes through SendCtrlCToProcess, which
                    ' first attempts AttachConsole+CTRL_C_EVENT (the
                    ' only thing UE4 servers actually respond to) and
                    ' falls back to taskkill /PID for plain Win32 GUI
                    ' apps that respond to WM_CLOSE.
                    If OperatingSystem.IsWindows() Then
                        _logger.LogInformation(
                            "Initiating graceful stop of {Id} (PID {Pid}, CaptureStdout={CapStd})",
                            request.InstanceId, managed.Pid, managed.CaptureStdout)
                        gracefulSignalSent = SendCtrlCToProcess(managed.Process)
                    End If

                    ' Fallback: close stdin — some servers (Factorio
                    ' etc.) treat EOF as shutdown. Only meaningful
                    ' for the stdout-captured path where stdin is
                    ' actually redirected; the hidden-console path
                    ' has no stdin pipe to close (the inner Try
                    ' swallows the InvalidOperationException that
                    ' would otherwise throw).
                    If Not gracefulSignalSent Then
                        Try
                            managed.Process.StandardInput.Close()
                        Catch
                        End Try
                    End If

                    ' Wait for graceful exit
                    Dim exited = Await WaitForExitAsync(managed.Process, timeout)

                    If Not exited Then
                        _logger.LogWarning("Instance {InstanceId} did not exit gracefully, killing",
                                           request.InstanceId)
                        Try
                            managed.Process.Kill(entireProcessTree:=True)
                        Catch
                            managed.Process.Kill()
                        End Try
                        Await WaitForExitAsync(managed.Process, 5000)
                    End If
                Catch ex As Exception
                    _logger.LogError(ex, "Error stopping instance {InstanceId}",
                                     request.InstanceId)
                End Try
            End If

            managed.State = InstanceState.Stopped
            managed.StateChangedAt = DateTime.UtcNow
            _database.RemoveInstanceSnapshot(request.InstanceId)

            _logger.LogInformation("Stopped instance {InstanceId}", request.InstanceId)
            Return BuildStatusResponse(managed)
        End Function

        Public Function GetInstanceStatus(instanceId As String) As InstanceStatusResponse
            Dim managed As ManagedInstance = Nothing
            If Not _instances.TryGetValue(instanceId, managed) Then
                Return BuildErrorResponse(instanceId, NodeErrorCodes.InstanceNotFound,
                                          "Instance not found")
            End If
            Return BuildStatusResponse(managed)
        End Function

        Public Function GetAllInstanceStatuses() As IReadOnlyList(Of InstanceStatusResponse)
            Return _instances.Values.
                Select(Function(m) BuildStatusResponse(m)).
                ToList()
        End Function

        ' ============================================================
        '  Spawn dispatch
        '
        '  SpawnGameProcess picks one of three strategies based on
        '  managed.Strategy and the host OS:
        '
        '    A. Stdout-captured: ProcessStartInfo with redirected
        '       stdin/stdout/stderr. .NET creates the child with
        '       CREATE_NO_WINDOW (no console at all) and pipes for
        '       stdio. Used for plugins where stdout IS the log
        '       stream (StdoutIsLog=True), and on non-Windows where
        '       the hidden-console approach below isn't applicable.
        '
        '    B. Hidden console direct: native CreateProcessW with
        '       CREATE_NEW_CONSOLE + STARTF_USESHOWWINDOW + SW_HIDE.
        '       The child gets its own hidden console; stdout goes
        '       there, file tailers feed the manager log buffer,
        '       AttachConsole works for graceful Ctrl+C. Last
        '       Oasis path. Used when the plugin has declared file
        '       log sources but doesn't need console isolation.
        '
        '    C. Hidden console wrapped: same CreateProcess machinery
        '       as B, but pointed at cmd.exe /S /c "<game-exe>
        '       <args>" instead of the game's exe directly. Used by
        '       plugins that set RequiresConsoleIsolation=True —
        '       i.e. their game executable defeats CREATE_NEW_CONSOLE
        '       via AttachConsole(ATTACH_PARENT_PROCESS) at startup
        '       (Factorio). Tracks cmd.exe as the managed process;
        '       cmd /c forwards the game's exit code so crash
        '       detection works unchanged. See the file-header
        '       comment for the full rationale.
        '
        '  Diagnostic logging at this dispatch point is intentional:
        '  if AttachConsole later fails for a UE4-class instance, the
        '  first thing to verify is that Strategy B/C actually ran
        '  (vs. an old build still on Strategy A). The "Spawn
        '  dispatch" log line gives that answer in one place.
        ' ============================================================

        ''' <summary>
        ''' Internal classification of which spawn path a managed
        ''' instance is on. Resolved once at start time from the
        ''' StartInstanceRequest's StdoutIsLog and
        ''' RequiresConsoleIsolation booleans plus the host OS, and
        ''' persisted on ManagedInstance so crash-restart cycles
        ''' re-spawn through the same path. Not exposed in any
        ''' contract — plugins describe their game via the contract
        ''' booleans, and this enum is purely a node-side dispatch
        ''' tag. Public so ManagedInstance (Friend) can hold a
        ''' property of this type without VB.Net visibility issues.
        ''' </summary>
        Public Enum SpawnStrategy
            ''' <summary>Strategy A: redirected stdio, no console.</summary>
            StdoutCapture = 0
            ''' <summary>Strategy B: native CreateProcess with CREATE_NEW_CONSOLE + SW_HIDE, exe spawned directly.</summary>
            HiddenConsoleDirect = 1
            ''' <summary>Strategy C: same as B but spawning cmd.exe /S /c "&lt;exe&gt; &lt;args&gt;" so the game inherits cmd's hidden console.</summary>
            HiddenConsoleWrapped = 2
        End Enum

        Private Function SpawnGameProcess(managed As ManagedInstance,
                                          psi As ProcessStartInfo) As Process

            Dim isWin = OperatingSystem.IsWindows()
            Dim strategy = managed.Strategy

            Dim strategyName As String
            Select Case strategy
                Case SpawnStrategy.StdoutCapture
                    strategyName = "A:RedirectedStdio"
                Case SpawnStrategy.HiddenConsoleWrapped
                    strategyName = "C:CmdWrappedConsole"
                Case Else
                    strategyName = "B:HiddenConsole"
            End Select
            Dim logFileCount = If(managed.LogFilePaths Is Nothing, 0, managed.LogFilePaths.Count)

            _logger.LogInformation(
                "Spawn dispatch for {Id}: strategy={Strategy} (StdoutIsLog={StdoutIsLog}, RequiresConsoleIsolation={Iso}, IsWindows={IsWin}, LogFilePaths={Count})",
                managed.InstanceId, strategyName,
                managed.StdoutIsLog, managed.RequiresConsoleIsolation,
                isWin, logFileCount)

            If strategy = SpawnStrategy.StdoutCapture Then
                ' Strategy A: redirected stdio.
                Dim proc As New Process()
                proc.StartInfo = psi
                proc.EnableRaisingEvents = True

                AttachProcessHandlers(proc, managed)

                If Not proc.Start() Then Return Nothing

                ' Drain both pipes. Without this the redirected pipes
                ' would fill up after a few KB and block the child's
                ' writes — which on UE4 hangs the entire server. (The
                ' hidden-console paths don't have this concern since
                ' they don't redirect at all.)
                proc.BeginOutputReadLine()
                proc.BeginErrorReadLine()
                Return proc
            End If

            ' Strategy B or C: hidden console (direct or cmd-wrapped).
            Dim spawned As Process
            If strategy = SpawnStrategy.HiddenConsoleWrapped Then
                spawned = SpawnWrappedConsoleProcess(psi)
            Else
                spawned = SpawnHiddenConsoleProcess(psi)
            End If
            If spawned Is Nothing Then Return Nothing

            spawned.EnableRaisingEvents = True
            AttachProcessHandlers(spawned, managed)

            Dim wrappedNote = If(strategy = SpawnStrategy.HiddenConsoleWrapped,
                                  "cmd-wrapped, tracking cmd PID", "direct")
            _logger.LogInformation(
                "Strategy {Strategy} spawn complete: {Id} PID={Pid} ({Note})",
                If(strategy = SpawnStrategy.HiddenConsoleWrapped, "C", "B"),
                managed.InstanceId, spawned.Id, wrappedNote)

            Return spawned
        End Function

        ''' <summary>
        ''' Resolves a StartInstanceRequest's plugin-supplied
        ''' StdoutIsLog and RequiresConsoleIsolation booleans into a
        ''' concrete SpawnStrategy. The host OS and declared log
        ''' file paths participate in the decision so plugins that
        ''' don't implement ILaunchOptionsProvider (both booleans
        ''' False) still get sensible defaults:
        '''
        '''   non-Windows           → StdoutCapture (always; the
        '''                          hidden-console paths use native
        '''                          CreateProcessW)
        '''   StdoutIsLog=True       → StdoutCapture (the only path
        '''                          that captures stdio)
        '''   RequiresConsoleIsolation=True → HiddenConsoleWrapped
        '''   has file logs declared → HiddenConsoleDirect (legacy
        '''                          file-logged behaviour, what LO
        '''                          and the pre-LaunchOptions code
        '''                          path used)
        '''   neither, no file logs  → StdoutCapture (safe default
        '''                          for unconfigured plugins)
        ''' </summary>
        Private Shared Function ResolveStrategy(stdoutIsLog As Boolean,
                                                 requiresConsoleIsolation As Boolean,
                                                 logFilePaths As IList(Of String)) As SpawnStrategy
            If Not OperatingSystem.IsWindows() Then Return SpawnStrategy.StdoutCapture
            If stdoutIsLog Then Return SpawnStrategy.StdoutCapture
            If requiresConsoleIsolation Then Return SpawnStrategy.HiddenConsoleWrapped
            Dim hasFileLogs = logFilePaths IsNot Nothing AndAlso logFilePaths.Count > 0
            If hasFileLogs Then Return SpawnStrategy.HiddenConsoleDirect
            Return SpawnStrategy.StdoutCapture
        End Function

        ' ============================================================
        '  Native CreateProcess for the hidden-console path
        '
        '  Why we need this at all (the non-obvious part):
        '
        '    ProcessStartInfo with CreateNoWindow=True maps to the
        '    Win32 CREATE_NO_WINDOW flag. Despite the name,
        '    CREATE_NO_WINDOW means "no console at all" — the child
        '    inherits stdio handles (or pipes when redirected) but
        '    GetConsoleWindow() returns NULL inside it. AttachConsole
        '    against such a child returns ERROR_INVALID_HANDLE.
        '
        '    Even with CreateNoWindow=False, .NET's Process.StartCore
        '    never passes CREATE_NEW_CONSOLE. A WinExe parent (Node)
        '    that has no console of its own spawning a console-
        '    subsystem child without CREATE_NEW_CONSOLE results in a
        '    child with no console either.
        '
        '    To actually give the child a console we have to call
        '    CreateProcess directly with CREATE_NEW_CONSOLE. We add
        '    STARTF_USESHOWWINDOW + SW_HIDE so the console window
        '    isn't visible. The child runs as if you'd opened a
        '    console window for it, except the window is hidden.
        '
        '  Trade-off:
        '
        '    No stdio redirection by default — the child writes to
        '    its own console buffer (which has a generous default
        '    scrollback and can't block on writes the way a 4KB
        '    pipe can). Anyone observing the server gets logs from
        '    the file tailers, not from the console buffer. Plugins
        '    that need stdio routed elsewhere (silenced, in
        '    particular) opt in via ILaunchOptionsProvider.
        '
        '  Compatibility with the rest of ProcessManager:
        '
        '    Process.GetProcessById(pid) gives us a managed Process
        '    handle to the new child. EnableRaisingEvents, HasExited,
        '    Kill, WaitForExitAsync, WorkingSet64, etc. all work
        '    against that handle — .NET re-opens the underlying
        '    handle internally with whatever access rights each
        '    operation needs.
        ' ============================================================

        <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
        Private Structure STARTUPINFOW
            Public cb As UInteger
            Public lpReserved As IntPtr
            Public lpDesktop As IntPtr
            Public lpTitle As IntPtr
            Public dwX As UInteger
            Public dwY As UInteger
            Public dwXSize As UInteger
            Public dwYSize As UInteger
            Public dwXCountChars As UInteger
            Public dwYCountChars As UInteger
            Public dwFillAttribute As UInteger
            Public dwFlags As UInteger
            Public wShowWindow As UShort
            Public cbReserved2 As UShort
            Public lpReserved2 As IntPtr
            Public hStdInput As IntPtr
            Public hStdOutput As IntPtr
            Public hStdError As IntPtr
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Private Structure PROCESS_INFORMATION
            Public hProcess As IntPtr
            Public hThread As IntPtr
            Public dwProcessId As Integer
            Public dwThreadId As Integer
        End Structure

        <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Private Shared Function CreateProcessW(
            <MarshalAs(UnmanagedType.LPWStr)> lpApplicationName As String,
            lpCommandLine As StringBuilder,
            lpProcessAttributes As IntPtr,
            lpThreadAttributes As IntPtr,
            bInheritHandles As Boolean,
            dwCreationFlags As UInteger,
            lpEnvironment As IntPtr,
            <MarshalAs(UnmanagedType.LPWStr)> lpCurrentDirectory As String,
            ByRef lpStartupInfo As STARTUPINFOW,
            ByRef lpProcessInformation As PROCESS_INFORMATION) As Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function CloseHandle(hObject As IntPtr) As Boolean
        End Function

        Private Const CREATE_NEW_CONSOLE As UInteger = &H10UI
        Private Const CREATE_UNICODE_ENVIRONMENT As UInteger = &H400UI
        Private Const STARTF_USESHOWWINDOW As UInteger = &H1UI
        Private Const SW_HIDE As UShort = 0US

        ''' <summary>
        ''' Spawns a child process with its own hidden console window.
        ''' Returns a managed Process handle to the new child.
        '''
        ''' bInheritHandles=False (no STARTF_USESTDHANDLES, no stdio
        ''' redirection). The child's stdout/stderr go to its own
        ''' hidden console buffer; nothing leaks to the node's
        ''' terminal because there's no inheritance path that could
        ''' connect the child back to the node's stdio.
        ''' CREATE_NEW_CONSOLE allocates a fresh console for the
        ''' child, and SW_HIDE keeps its window invisible.
        ''' AttachConsole + GenerateConsoleCtrlEvent still work
        ''' against this hidden console for graceful shutdown via
        ''' CTRL_C_EVENT.
        '''
        ''' Used directly by Strategy B (InheritParent). Strategy C
        ''' (DiscardToNul) calls this with a cmd.exe-wrapped psi via
        ''' SpawnWrappedConsoleProcess; the wrapping is the whole
        ''' difference between B and C from this function's
        ''' perspective — it just spawns whatever exe + args it's
        ''' handed.
        ''' </summary>
        Private Function SpawnHiddenConsoleProcess(psi As ProcessStartInfo) As Process

            ' Build a quoted command line. CreateProcessW needs the
            ' first token to be either a quoted exe path or an
            ' unquoted-with-no-spaces exe path. Quoting is always
            ' safe so we always quote. (lpApplicationName is left
            ' null so the OS does the executable search itself; this
            ' matches ProcessStartInfo behaviour for relative paths.)
            Dim cmdLine As New StringBuilder()
            cmdLine.Append(""""c)
            cmdLine.Append(psi.FileName)
            cmdLine.Append(""""c)
            If Not String.IsNullOrEmpty(psi.Arguments) Then
                cmdLine.Append(" "c)
                cmdLine.Append(psi.Arguments)
            End If

            Dim si As New STARTUPINFOW()
            si.cb = CUInt(Marshal.SizeOf(Of STARTUPINFOW)())
            si.dwFlags = STARTF_USESHOWWINDOW
            si.wShowWindow = SW_HIDE

            Dim pi As New PROCESS_INFORMATION()

            Dim envBlockPtr As IntPtr = IntPtr.Zero
            Try
                envBlockPtr = BuildEnvironmentBlock(psi.EnvironmentVariables)

                Dim flags As UInteger = CREATE_NEW_CONSOLE Or CREATE_UNICODE_ENVIRONMENT

                ' CreateProcessW rejects empty-string lpCurrentDirectory;
                ' pass Nothing (NULL) to mean "inherit parent's cwd".
                Dim workDir As String = If(String.IsNullOrEmpty(psi.WorkingDirectory),
                                           Nothing, psi.WorkingDirectory)

                Dim ok = CreateProcessW(
                    Nothing,        ' lpApplicationName: parsed from cmdLine
                    cmdLine,        ' lpCommandLine
                    IntPtr.Zero,    ' lpProcessAttributes
                    IntPtr.Zero,    ' lpThreadAttributes
                    False,          ' bInheritHandles: see comment block above
                    flags,
                    envBlockPtr,    ' Nothing => IntPtr.Zero => inherit parent env
                    workDir,
                    si,
                    pi)

                If Not ok Then
                    Dim err = Marshal.GetLastWin32Error()
                    Throw New IOException($"CreateProcessW failed for {psi.FileName} (Win32Error={err})")
                End If

                ' We don't need the thread handle.
                If pi.hThread <> IntPtr.Zero Then CloseHandle(pi.hThread)

                Dim pid = pi.dwProcessId

                ' Close our handle to the process — Process.GetProcessById
                ' will open its own handles internally.
                If pi.hProcess <> IntPtr.Zero Then CloseHandle(pi.hProcess)

                Return Process.GetProcessById(pid)
            Finally
                If envBlockPtr <> IntPtr.Zero Then
                    Marshal.FreeHGlobal(envBlockPtr)
                End If
            End Try
        End Function

        ''' <summary>
        ''' Strategy C variant of SpawnHiddenConsoleProcess. Builds a
        ''' cmd.exe /S /c "…" command line that runs the original
        ''' exe + args, and feeds it to SpawnHiddenConsoleProcess.
        ''' Used when the plugin opts into StdioMode.DiscardToNul:
        ''' cmd owns the new hidden console; the game inherits it
        ''' from cmd, so any AttachConsole(ATTACH_PARENT_PROCESS)
        ''' trick the game pulls reattaches to cmd's hidden console
        ''' rather than to the node's terminal.
        '''
        ''' Tracking model: we keep cmd.exe as the managed process.
        ''' cmd /c forwards the child's exit code on exit, so crash
        ''' detection sees the right value. Process.Kill(
        ''' entireProcessTree:=True) kills cmd plus the game
        ''' atomically. CtrlCSender attaches to cmd's hidden console
        ''' and fires CTRL_C_EVENT to process group 0, which reaches
        ''' every process in that console group — i.e. cmd AND the
        ''' game.
        '''
        ''' Quote handling: cmd's /S /c parses the rest of the
        ''' command line by stripping exactly one quote from each end
        ''' of the /c argument. We wrap the entire payload in a
        ''' single outer pair of quotes so the strip leaves the
        ''' original "&lt;exe&gt;" args intact.
        ''' </summary>
        Private Function SpawnWrappedConsoleProcess(psi As ProcessStartInfo) As Process

            ' Build the payload: "<exe>" <args>. The original
            ' psi.Arguments is already in Win32-quoted form (each
            ' argument with its own quotes, separated by spaces) and
            ' we don't touch it — we just glue our quoted exe path on
            ' the front.
            Dim payload As New StringBuilder()
            payload.Append(""""c)
            payload.Append(psi.FileName)
            payload.Append(""""c)
            If Not String.IsNullOrEmpty(psi.Arguments) Then
                payload.Append(" "c)
                payload.Append(psi.Arguments)
            End If

            ' Wrap the payload in cmd's /S /c "…" form. The outer
            ' quotes are what /S strips; the inner quotes survive.
            Dim wrappedArgs As New StringBuilder()
            wrappedArgs.Append("/S /c """)
            wrappedArgs.Append(payload.ToString())
            wrappedArgs.Append(""""c)

            Dim wrappedPsi As New ProcessStartInfo()
            wrappedPsi.FileName = "cmd.exe"
            wrappedPsi.Arguments = wrappedArgs.ToString()
            wrappedPsi.WorkingDirectory = psi.WorkingDirectory

            ' Make wrappedPsi's environment exactly match psi's. Both
            ' StringDictionary collections start as lazy copies of the
            ' parent's environment, so simply overlaying psi onto
            ' wrappedPsi misses the case where a plugin REMOVED a
            ' variable. Clear-then-copy is the safe pattern.
            wrappedPsi.EnvironmentVariables.Clear()
            For Each de As System.Collections.DictionaryEntry In psi.EnvironmentVariables
                Dim k = TryCast(de.Key, String)
                Dim v = TryCast(de.Value, String)
                If String.IsNullOrEmpty(k) Then Continue For
                wrappedPsi.EnvironmentVariables(k) = If(v, "")
            Next

            Return SpawnHiddenConsoleProcess(wrappedPsi)
        End Function

        ''' <summary>
        ''' Builds a Unicode environment block (KEY=VAL\0KEY=VAL\0\0)
        ''' for CreateProcess. Returns IntPtr.Zero when envVars is
        ''' empty, signalling to CreateProcess that the child should
        ''' inherit the parent's environment unchanged. Caller is
        ''' responsible for FreeHGlobal on the returned non-zero ptr.
        ''' </summary>
        Private Function BuildEnvironmentBlock(envVars As System.Collections.Specialized.StringDictionary) As IntPtr
            If envVars Is Nothing OrElse envVars.Count = 0 Then Return IntPtr.Zero

            ' Windows requires environment variable names sorted
            ' (case-insensitive Ordinal). Without sorting some
            ' processes can't find their own variables.
            Dim entries As New List(Of String)
            For Each de As System.Collections.DictionaryEntry In envVars
                Dim k = TryCast(de.Key, String)
                Dim v = TryCast(de.Value, String)
                If String.IsNullOrEmpty(k) Then Continue For
                entries.Add(k & "=" & If(v, ""))
            Next
            entries.Sort(StringComparer.OrdinalIgnoreCase)

            Dim sb As New StringBuilder()
            For Each entry In entries
                sb.Append(entry)
                sb.Append(ChrW(0))
            Next
            sb.Append(ChrW(0))   ' terminating double-null

            Return Marshal.StringToHGlobalUni(sb.ToString())
        End Function

        ' ============================================================
        '  Graceful shutdown signalling (Windows)
        '
        '  UE4 dedicated servers (Last Oasis MistServer, Conan, etc.)
        '  install a SetConsoleCtrlHandler in LaunchWindows.cpp that
        '  routes CTRL_C_EVENT to RequestEngineExit — the engine's
        '  graceful shutdown path. They DO NOT respond to WM_CLOSE,
        '  which is what taskkill /PID (no /F) sends. So taskkill
        '  alone never gracefully shuts a UE4 server down.
        '
        '  To deliver CTRL_C_EVENT we use the GSM.CtrlCSender helper
        '  exe which does AttachConsole + GenerateConsoleCtrlEvent.
        '  Prerequisite: the target must HAVE a console. That's why
        '  file-logged instances spawn through SpawnHiddenConsoleProcess
        '  above (CREATE_NEW_CONSOLE + SW_HIDE) — without that, the
        '  child has no console and AttachConsole fails with
        '  ERROR_INVALID_HANDLE (Win32Error 6).
        '
        '  Stdout-captured instances (Strategy A, no LogFilePaths)
        '  spawn through ProcessStartInfo with CREATE_NO_WINDOW, so
        '  they have no console. The Ctrl+C path will fail for
        '  them, the code falls through to taskkill (WM_CLOSE), and
        '  if that doesn't take, the stdin EOF fallback fires —
        '  closing the redirected stdin pipe is what reaches an app
        '  like Factorio when it's running this way. (The current
        '  Factorio plugin declares file log sources, so it actually
        '  goes through Strategy B and the Ctrl+C path now reaches
        '  it via its hidden console; Strategy A remains the path
        '  for any future plugin that ships only stdout logging.)
        '
        '  Why a separate helper executable instead of doing the
        '  AttachConsole call inline:
        '
        '    AttachConsole is process-global state (a process can
        '    only be attached to one console at a time). Two threads
        '    inside the Node stopping two instances concurrently
        '    would race on that single attachment slot. We'd also
        '    have to carefully save/restore the Node's own console-
        '    control-handler around every call so the CTRL_C_EVENT
        '    we fire doesn't take the Node down. A tiny helper
        '    process makes each invocation isolated.
        '
        '  Why not CREATE_NEW_PROCESS_GROUP at spawn time:
        '
        '    - ProcessStartInfo doesn't expose the flag, and the
        '      reflection workarounds against _standardCreationFlags
        '      don't apply in .NET 8 (field renamed/removed).
        '    - Per MSDN, CTRL_C_EVENT is specifically ignored for
        '      processes in a new process group anyway — only
        '      CTRL_BREAK_EVENT propagates, and UE4's handler
        '      doesn't always treat that as graceful shutdown.
        ' ============================================================

        ''' <summary>
        ''' Best-effort graceful shutdown signal. Tries the real
        ''' Ctrl+C path first, falls back to taskkill /PID. Returns
        ''' True if either succeeded; the caller still has to wait
        ''' for the process to actually exit.
        ''' </summary>
        Private Function SendCtrlCToProcess(proc As Process) As Boolean
            If TrySendConsoleCtrlC(proc) Then Return True
            Return TrySendTaskkill(proc)
        End Function

        ''' <summary>
        ''' Spawns the GSM.CtrlCSender helper to AttachConsole to
        ''' the target's hidden console and fire CTRL_C_EVENT.
        ''' Returns True only if the helper exits with code 0.
        ''' </summary>
        Private Function TrySendConsoleCtrlC(proc As Process) As Boolean
            Try
                Dim helperPath = Path.Combine(AppContext.BaseDirectory, "GSM.CtrlCSender.exe")
                If Not File.Exists(helperPath) Then
                    _logger.LogWarning(
                        "GSM.CtrlCSender.exe not found at {Path} — falling back to taskkill",
                        helperPath)
                    Return False
                End If

                Dim psi As New ProcessStartInfo(helperPath, proc.Id.ToString())
                psi.UseShellExecute = False
                psi.CreateNoWindow = True
                psi.RedirectStandardOutput = True
                psi.RedirectStandardError = True

                Using p = Process.Start(psi)
                    If Not p.WaitForExit(5000) Then
                        Try : p.Kill() : Catch : End Try
                        _logger.LogWarning("GSM.CtrlCSender timed out for PID {Pid}", proc.Id)
                        Return False
                    End If

                    If p.ExitCode <> 0 Then
                        Dim stderr As String = ""
                        Try
                            stderr = p.StandardError.ReadToEnd().Trim()
                        Catch
                        End Try
                        _logger.LogWarning(
                            "GSM.CtrlCSender exit {Code} for PID {Pid}: {Err}",
                            p.ExitCode, proc.Id, stderr)
                        Return False
                    End If

                    _logger.LogInformation(
                        "Sent CTRL_C_EVENT to PID {Pid} via GSM.CtrlCSender", proc.Id)
                    Return True
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "GSM.CtrlCSender invocation failed for PID {Pid}", proc.Id)
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Legacy WM_CLOSE path via taskkill (no /F). Works for
        ''' plain Win32 GUI apps and most non-UE console apps; UE4
        ''' servers ignore it. Kept as a fallback for processes
        ''' that don't have a console for AttachConsole to attach to.
        ''' </summary>
        Private Function TrySendTaskkill(proc As Process) As Boolean
            Try
                Dim psi As New ProcessStartInfo("taskkill", $"/PID {proc.Id}")
                psi.UseShellExecute = False
                psi.CreateNoWindow = True
                psi.RedirectStandardOutput = True
                psi.RedirectStandardError = True
                Using p = Process.Start(psi)
                    p.WaitForExit(5000)
                    Return p.ExitCode = 0
                End Using
            Catch
                Return False
            End Try
        End Function

        ' ============================================================
        '  Log file tailing
        ' ============================================================

        ''' <summary>
        ''' Starts a background tailer for each file path. Each tailer
        ''' polls the file for new bytes and appends lines to the
        ''' instance's ring buffer. Files are opened with FileShare.ReadWrite
        ''' so they don't interfere with the game process writing to them.
        ''' </summary>
        Private Sub StartFileTailers(managed As ManagedInstance, paths As IList(Of String))
            If paths Is Nothing OrElse paths.Count = 0 Then
                _logger.LogInformation("No file log sources for {Id}", managed.InstanceId)
                Return
            End If

            _logger.LogInformation("Starting {Count} file tailer(s) for {Id}: {Paths}",
                                   paths.Count, managed.InstanceId, String.Join(", ", paths))

            managed.TailerCancellations = New List(Of CancellationTokenSource)
            For Each path In paths
                If String.IsNullOrWhiteSpace(path) Then Continue For
                Dim cts As New CancellationTokenSource()
                managed.TailerCancellations.Add(cts)
                Dim capturedPath = path
                Dim capturedId = managed.InstanceId
                Dim capturedDelay = managed.LogTailerStartDelayMs
                Task.Run(Function() TailFileAsync(capturedId, capturedPath, capturedDelay, cts.Token))
            Next
        End Sub

        Private Sub StopFileTailers(managed As ManagedInstance)
            If managed.TailerCancellations Is Nothing Then Return
            For Each cts In managed.TailerCancellations
                Try : cts.Cancel() : Catch : End Try
                Try : cts.Dispose() : Catch : End Try
            Next
            managed.TailerCancellations = Nothing
        End Sub

        ''' <summary>
        ''' Tails a single file. Waits for it to appear (up to 60s)
        ''' since engines like UE4 create the log file after process
        ''' start. Reads from the end, polls every 250ms for new data,
        ''' splits on newlines, writes to the ring buffer.
        '''
        ''' startDelayMs is the wait between detecting the file and
        ''' opening it for the first time. UE4 needs this delay to
        ''' avoid being tripped during init; Factorio-class engines
        ''' set it to 0 so fast-crashing instances still get their
        ''' init log captured before the process exits and the
        ''' tailer is cancelled.
        ''' </summary>
        Private Async Function TailFileAsync(instanceId As String,
                                              path As String,
                                              startDelayMs As Integer,
                                              token As CancellationToken) As Task
            Try
                ' Wait for file to appear (up to 60 seconds)
                Dim deadline = DateTime.UtcNow.AddSeconds(60)
                While Not File.Exists(path)
                    If token.IsCancellationRequested Then Return
                    If DateTime.UtcNow > deadline Then
                        _logger.LogWarning("Log file never appeared: {Path}", path)
                        Return
                    End If
                    Await Task.Delay(500, token)
                End While

                _logger.LogInformation("Tailing log file for {Id}: {Path} (startDelay={Delay}ms)",
                                       instanceId, path, startDelayMs)

                ' Plugin-controlled wait before the first read. UE4
                ' needed several seconds because opening the file
                ' during engine init could trip the server; Factorio
                ' opts into 0 so the tailer reads immediately and
                ' captures init lines before a fast-failing instance
                ' exits. Skip the await entirely when the delay is 0
                ' so a cancellation that arrives in the same tick
                ' doesn't pre-empt the very first read.
                If startDelayMs > 0 Then
                    Await Task.Delay(startDelayMs, token)
                End If

                ' UE4 keeps its log file open exclusively for writing.
                ' Holding a FileStream open while UE4 writes can cause
                ' conflicts that stall the server. Use open-read-close
                ' cycles: on each poll, open the file briefly, read
                ' anything new since our last position, close immediately.
                '
                ' First-open positioning:
                '   - Fresh server start (file small, under a couple MB):
                '     read from position 0 so the engine init / tile load
                '     / backend registration lines all reach the manager.
                '     A fresh UE4 Last Oasis boot is typically well under
                '     this threshold. The node's ring buffer caps at
                '     4096 lines per instance, so even a verbose init
                '     can't blow out memory.
                '   - Re-attaching to an already-running server (file
                '     large): seek to (length - BackfillBytes) so we
                '     pick up recent context without replaying hours of
                '     accumulated log. Without this backstop, restarting
                '     the node while a game is running would trigger a
                '     multi-MB reread that floods the manager.
                Const FirstOpenThresholdBytes As Long = 2L * 1024L * 1024L
                Const BackfillBytes As Long = 512L * 1024L

                Dim position As Long = -1
                Dim pending As New StringBuilder()
                ' True when we started mid-file (backfill path) and haven't
                ' yet consumed the leading partial line. First newline we
                ' see clears this flag; everything before it is discarded
                ' instead of emitted as a truncated "line".
                Dim skipLeadingPartial As Boolean = False

                While Not token.IsCancellationRequested
                    Try
                        Using fs As New FileStream(path, FileMode.Open, FileAccess.Read,
                                                    FileShare.ReadWrite Or FileShare.Delete)
                            ' First open: decide start position from file size
                            If position < 0 Then
                                If fs.Length <= FirstOpenThresholdBytes Then
                                    position = 0
                                Else
                                    position = Math.Max(0L, fs.Length - BackfillBytes)
                                    skipLeadingPartial = position > 0
                                End If
                            End If

                            ' Handle truncation/rotation
                            If fs.Length < position Then
                                position = 0
                                skipLeadingPartial = False
                            End If

                            If fs.Length > position Then
                                fs.Seek(position, SeekOrigin.Begin)
                                Dim endLength = fs.Length
                                Using reader As New StreamReader(fs)
                                    Dim buffer(8191) As Char
                                    Dim read = reader.Read(buffer, 0, buffer.Length)
                                    While read > 0
                                        For i = 0 To read - 1
                                            Dim ch = buffer(i)
                                            If skipLeadingPartial Then
                                                If ch = ChrW(10) Then skipLeadingPartial = False
                                                Continue For
                                            End If
                                            If ch = ChrW(10) Then
                                                EmitTailLine(instanceId, pending.ToString().TrimEnd(ChrW(13)))
                                                pending.Clear()
                                            Else
                                                pending.Append(ch)
                                            End If
                                        Next
                                        read = reader.Read(buffer, 0, buffer.Length)
                                    End While
                                End Using
                                position = endLength
                            End If
                        End Using
                    Catch ioEx As IOException
                        ' Transient — UE4 may be holding the file exclusively
                        ' for a moment. Back off and retry next tick.
                    End Try

                    Await Task.Delay(500, token)
                End While
            Catch ex As OperationCanceledException
                ' Expected on stop
            Catch ex As Exception
                _logger.LogWarning(ex, "Tailer error for {Path}", path)
            End Try
        End Function

        Private Sub EmitTailLine(instanceId As String, text As String)
            If String.IsNullOrEmpty(text) Then Return
            Dim ts = DateTime.UtcNow
            _logStore.Append(instanceId, New BufferedLogLine With {
                .Timestamp = ts,
                .Text = text,
                .IsError = False
            })
            _eventStore.ProcessLine(instanceId, ts, text)
        End Sub

        ' ============================================================
        '  Crash handling
        ' ============================================================

        Private Sub HandleProcessExited(managed As ManagedInstance)
            ' Always stop file tailers when the process exits.
            StopFileTailers(managed)

            ' Clear parsed in-memory state (chat history persists in SQLite)
            _eventStore.UnregisterInstance(managed.InstanceId)

            ' Read the exit code BEFORE the StopIntentPending early
            ' return. Previously we only populated LastExitCode on
            ' the crash path, which meant graceful stops surfaced no
            ' exit code to the manager — and the stop notification's
            ' {ExitCode} token stayed literal because the emitter
            ' only adds it when the value is non-null.
            Dim exitCode = 0
            Try
                exitCode = managed.Process.ExitCode
            Catch
            End Try
            managed.LastExitCode = exitCode

            If managed.StopIntentPending Then
                ' Intentional stop — don't treat as crash
                managed.State = InstanceState.Stopped
                managed.StateChangedAt = DateTime.UtcNow
                _database.RemoveInstanceSnapshot(managed.InstanceId)
                Return
            End If

            _logger.LogWarning("Instance {InstanceId} exited unexpectedly (code {ExitCode})",
                               managed.InstanceId, exitCode)

            ' Evaluate restart policy
            Dim decision = EvaluateRestartPolicy(managed, exitCode)

            _database.RecordCrashEvent(managed.InstanceId, exitCode,
                                       "ProcessExit", decision.Action.ToString(),
                                       decision.Reason)

            Select Case decision.Action
                Case PolicyAction.Restart
                    managed.State = InstanceState.Crashed
                    managed.StateChangedAt = DateTime.UtcNow
                    managed.CrashCount += 1

                    ' Schedule delayed restart. Re-check StopIntentPending
                    ' after the backoff so a Stop request issued during the
                    ' Crashed window (between exit and the next spawn)
                    ' actually halts the cycle. Without this re-check, the
                    ' manager-side Stop button is effectively ignored during
                    ' a crash loop: the dead process can't be gracefully
                    ' killed (HasExited is already True), so StopInstanceAsync
                    ' just flips state to Stopped and returns — and then this
                    ' queued task fires and resurrects the process anyway,
                    ' clearing StopIntentPending in FinalizeStart.
                    Task.Run(Async Function()
                                 If decision.DelayMs > 0 Then
                                     Await Task.Delay(decision.DelayMs)
                                 End If
                                 If managed.StopIntentPending Then
                                     managed.State = InstanceState.Stopped
                                     managed.StateChangedAt = DateTime.UtcNow
                                     _database.RemoveInstanceSnapshot(managed.InstanceId)
                                     _logger.LogInformation(
                                         "Restart cancelled for {Id}: stop requested during backoff",
                                         managed.InstanceId)
                                     Return
                                 End If
                                 Await RestartInstanceAsync(managed)
                             End Function)

                Case PolicyAction.Halt
                    managed.State = InstanceState.CrashLoopHalted
                    managed.StateChangedAt = DateTime.UtcNow
                    _logger.LogError("Instance {InstanceId} entered CrashLoopHalted: {Reason}",
                                     managed.InstanceId, decision.Reason)

                Case Else
                    managed.State = InstanceState.Stopped
                    managed.StateChangedAt = DateTime.UtcNow
            End Select
        End Sub

        Private Function EvaluateRestartPolicy(managed As ManagedInstance,
                                               exitCode As Integer) As PolicyDecision

            If managed.CrashPolicy = CrashRestartPolicy.NeverRestart Then
                Return New PolicyDecision With {
                    .Action = PolicyAction.NoRestart,
                    .Reason = "Policy is NeverRestart"
                }
            End If

            ' Check crash loop window
            Dim windowCrashes = _database.GetCrashCountInWindow(
                managed.InstanceId, managed.CrashWindowMinutes)

            If windowCrashes >= managed.MaxCrashCount Then
                Return New PolicyDecision With {
                    .Action = PolicyAction.Halt,
                    .Reason = $"Crash loop: {windowCrashes} crashes in {managed.CrashWindowMinutes} minutes (max {managed.MaxCrashCount})"
                }
            End If

            ' Calculate backoff delay
            Dim delayMs = 0
            If managed.CrashPolicy = CrashRestartPolicy.RestartWithBackoff Then
                ' Simple exponential backoff: 2^crashCount seconds, capped at 5 min
                Dim delaySec = Math.Min(CInt(Math.Pow(2, managed.CrashCount)), 300)
                delayMs = delaySec * 1000
            End If

            ' Apply the configured floor so fast first-crash restarts
            ' stay visible long enough for the manager poller to see
            ' the Crashed state before the node replaces the process.
            ' Without this floor, 2^0 = 1s can slip between two polls.
            If managed.MinRestartDelayMs > 0 Then
                delayMs = Math.Max(delayMs, managed.MinRestartDelayMs)
            End If

            Return New PolicyDecision With {
                .Action = PolicyAction.Restart,
                .DelayMs = delayMs,
                .Reason = $"Restarting (attempt {managed.CrashCount + 1}, delay {delayMs}ms)"
            }
        End Function

        Private Async Function RestartInstanceAsync(managed As ManagedInstance) As Task
            _logger.LogInformation("Restarting instance {InstanceId}", managed.InstanceId)

            If managed.StartInfo Is Nothing Then
                managed.State = InstanceState.CrashLoopHalted
                managed.StateChangedAt = DateTime.UtcNow
                _logger.LogError("Cannot restart instance {InstanceId}: StartInfo missing",
                                 managed.InstanceId)
                Return
            End If

            Try
                ' Re-spawn through the same dispatch as the initial
                ' start so the file-logged hidden-console path is
                ' preserved across crash-restart cycles. (Pre-refactor,
                ' restart did its own bare Process.Start which would
                ' have put a UE4 server back on the no-console path
                ' and broken graceful shutdown after any crash.)
                Dim proc As Process = SpawnGameProcess(managed, managed.StartInfo)

                If proc Is Nothing Then
                    managed.State = InstanceState.CrashLoopHalted
                    managed.StateChangedAt = DateTime.UtcNow
                    _logger.LogError("Failed to restart instance {InstanceId}", managed.InstanceId)
                    Return
                End If

                FinalizeStart(managed, proc)
            Catch ex As Exception
                managed.State = InstanceState.CrashLoopHalted
                managed.StateChangedAt = DateTime.UtcNow
                _logger.LogError(ex, "Exception restarting instance {InstanceId}", managed.InstanceId)
            End Try

            ' Async is kept on the signature for consistency with the
            ' call site (Await RestartInstanceAsync(managed)), even
            ' though the body is now synchronous — Process.Start is
            ' already non-blocking.
            Await Task.CompletedTask
        End Function

        ' ============================================================
        '  Shared process-wiring helpers
        '
        '  Both StartInstanceAsync and RestartInstanceAsync go through
        '  these so the two code paths can't drift apart again. The
        '  pre-refactor restart path silently skipped EventStore
        '  registration, file tailers, and snapshot persistence, which
        '  is why crashed-and-restarted instances showed as "Running
        '  (PID X)" with no match state, no player list, and empty logs.
        ' ============================================================

        Private Sub AttachProcessHandlers(proc As Process, managed As ManagedInstance)
            ' OutputDataReceived/ErrorDataReceived only fire on the
            ' stdout-captured spawn path (Strategy A). The hidden-
            ' console path doesn't redirect, so .NET never starts a
            ' reader thread and these handlers stay quiet — adding
            ' them anyway is harmless and keeps the wiring uniform.
            AddHandler proc.OutputDataReceived, Sub(sender, e)
                                                    If e.Data Is Nothing Then Return
                                                    Dim ts = DateTime.UtcNow
                                                    If managed.CaptureStdout Then
                                                        _logStore.Append(managed.InstanceId,
                                                            New BufferedLogLine With {
                                                                .Timestamp = ts,
                                                                .Text = e.Data,
                                                                .IsError = False
                                                            })
                                                        _eventStore.ProcessLine(managed.InstanceId, ts, e.Data)
                                                    End If
                                                End Sub

            AddHandler proc.ErrorDataReceived, Sub(sender, e)
                                                   If e.Data Is Nothing Then Return
                                                   Dim ts = DateTime.UtcNow
                                                   If managed.CaptureStdout Then
                                                       _logStore.Append(managed.InstanceId,
                                                           New BufferedLogLine With {
                                                               .Timestamp = ts,
                                                               .Text = e.Data,
                                                               .IsError = True
                                                           })
                                                       _eventStore.ProcessLine(managed.InstanceId, ts, e.Data)
                                                   End If
                                               End Sub

            ' The Exited handler MUST NOT throw. .NET multicast events
            ' invoke subscribers in order, and the FIRST exception aborts
            ' the chain — meaning Process.WaitForExitAsync's internal
            ' subscriber (which sets the TaskCompletionSource that unblocks
            ' our await) never runs. Symptom: process exits cleanly,
            ' StopInstanceAsync's WaitForExitAsync(20000) times out anyway,
            ' "did not exit gracefully, killing" fires against a process
            ' that's been dead for 19+ seconds. Wrap the body so any
            ' throw inside HandleProcessExited gets logged and swallowed
            ' instead of poisoning the event invocation.
            AddHandler proc.Exited, Sub(sender, e)
                                        _logger.LogInformation(
                                            "Exited event fired for {Id} (PID {Pid})",
                                            managed.InstanceId, managed.Pid)
                                        Try
                                            HandleProcessExited(managed)
                                        Catch ex As Exception
                                            _logger.LogError(ex,
                                                "HandleProcessExited threw for {Id}",
                                                managed.InstanceId)
                                        End Try
                                    End Sub
        End Sub

        ''' <summary>
        ''' Shared post-spawn bookkeeping. Registers parse rules with
        ''' the EventStore, spins up file tailers, persists a snapshot,
        ''' and updates the ManagedInstance state fields. Called by
        ''' both StartInstanceAsync and RestartInstanceAsync.
        ''' </summary>
        Private Sub FinalizeStart(managed As ManagedInstance, proc As Process)
            managed.Process = proc
            managed.Pid = proc.Id
            managed.StartedAt = DateTime.UtcNow
            managed.State = InstanceState.Running
            managed.StateChangedAt = DateTime.UtcNow
            managed.StopIntentPending = False

            ' Register parse rules with the event store so it can
            ' track players / server state from log lines. The
            ' matching UnregisterInstance call lives in
            ' HandleProcessExited, so we must re-register on every
            ' (re)start.
            _eventStore.RegisterInstance(managed.InstanceId, managed.ParseRules)

            ' Start file tailers for any log files the plugin wanted
            ' mirrored into the instance's log buffer. The old tailers
            ' were already cancelled by StopFileTailers inside
            ' HandleProcessExited, so this re-creates them.
            StartFileTailers(managed, managed.LogFilePaths)

            _database.SaveInstanceSnapshot(
                managed.InstanceId, managed.State.ToString(),
                managed.Pid, managed.StartedAt,
                JsonSerializer.Serialize(New With {
                    .Policy = managed.CrashPolicy.ToString(),
                    .MaxCrash = managed.MaxCrashCount,
                    .WindowMin = managed.CrashWindowMinutes
                }),
                managed.StopIntentPending)

            ' If the instance stays up long enough, clear CrashCount
            ' so the next crash starts from a clean backoff baseline.
            ScheduleCrashCountReset(managed, proc)
        End Sub

        ''' <summary>
        ''' Schedules a one-shot background task that resets the
        ''' in-memory CrashCount to 0 if the specific Process passed
        ''' in is still the running process after
        ''' CrashCountResetAfterSeconds. Safe across crash-restart
        ''' cycles: the captured targetPid is compared against the
        ''' current process so a replacement process doesn't get
        ''' its counter wiped prematurely.
        ''' </summary>
        Private Sub ScheduleCrashCountReset(managed As ManagedInstance, proc As Process)
            Dim seconds = managed.CrashCountResetAfterSeconds
            If seconds <= 0 Then Return

            Dim instanceId = managed.InstanceId
            Dim targetPid = proc.Id

            Task.Run(Async Function()
                         Try
                             Await Task.Delay(TimeSpan.FromSeconds(seconds))

                             Dim cur As ManagedInstance = Nothing
                             If Not _instances.TryGetValue(instanceId, cur) Then Return

                             ' Bail if the tracked process isn't the
                             ' one we started watching: a crash-restart
                             ' swapped it out, and that new run's
                             ' counter should be judged on its own
                             ' uptime, not this timer.
                             If cur.Process Is Nothing Then Return
                             If cur.Process.Id <> targetPid Then Return
                             If cur.Process.HasExited Then Return
                             If cur.State <> InstanceState.Running Then Return

                             Dim prior = cur.CrashCount
                             If prior > 0 Then
                                 cur.CrashCount = 0
                                 _logger.LogInformation(
                                     "Instance {Id} stable for {Sec}s — resetting crash count (was {Prior})",
                                     instanceId, seconds, prior)
                             End If
                         Catch ex As Exception
                             _logger.LogWarning(ex, "Crash count reset task failed for {Id}", instanceId)
                         End Try
                     End Function)
        End Sub

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Shared Async Function WaitForExitAsync(proc As Process,
                                                       timeoutMs As Integer) As Task(Of Boolean)
            ' Use Process.WaitForExitAsync directly. Earlier in this
            ' file's history we replaced this with a 250ms HasExited
            ' polling loop because we suspected the kernel-handle wait
            ' that EnableRaisingEvents = True registers was unreliable
            ' for Process objects from Process.GetProcessById (our
            ' Strategy B path). That diagnosis was wrong. The actual
            ' bug was the LO MistServer being launched without -log,
            ' which prevents UE4 from installing its CTRL_C handler
            ' — so CTRL_C_EVENT was being delivered correctly but
            ' silently ignored, and the wait was correctly waiting for
            ' an exit that wasn't coming. Once -log was added, the
            ' Exited event (and equivalently WaitForExitAsync) fires
            ' within milliseconds of engine shutdown.
            '
            ' Notes on the two spawn strategies:
            '   Strategy A (redirected stdio): WaitForExitAsync also
            '     awaits stdout/stderr EOF after process exit. Pipes
            '     close when the OS reaps the process, so the EOF
            '     wait completes essentially instantly.
            '   Strategy B (hidden console): no redirection, so the
            '     internal _output/_error are null and the EOF wait
            '     no-ops.
            '
            ' OperationCanceledException is what we get when the
            ' timeout token fires before the process exits. Re-check
            ' HasExited in the catch in case the process raced past
            ' the deadline between cancel firing and us getting here.
            Try
                Using cts As New CancellationTokenSource(timeoutMs)
                    Await proc.WaitForExitAsync(cts.Token)
                End Using
                Return True
            Catch ex As OperationCanceledException
                Return proc.HasExited
            End Try
        End Function

        Private Shared Function BuildStatusResponse(managed As ManagedInstance,
                                                    Optional errCode As NodeErrorCodes = NodeErrorCodes.None,
                                                    Optional errMsg As String = Nothing) As InstanceStatusResponse
            Dim resp As New InstanceStatusResponse()
            resp.InstanceId = managed.InstanceId
            resp.CurrentState = managed.State
            resp.Pid = managed.Pid
            resp.CrashCount = managed.CrashCount
            resp.LastExitCode = managed.LastExitCode
            resp.StateChangedAt = managed.StateChangedAt
            resp.ErrorMessage = errMsg

            If managed.State = InstanceState.Running AndAlso managed.StartedAt <> DateTime.MinValue Then
                resp.UptimeSeconds = CLng((DateTime.UtcNow - managed.StartedAt).TotalSeconds)
            End If

            ' Best-effort process metrics
            If managed.Process IsNot Nothing AndAlso Not managed.Process.HasExited Then
                Try
                    managed.Process.Refresh()
                    resp.MemoryMb = managed.Process.WorkingSet64 \ (1024L * 1024L)
                Catch
                End Try
            End If

            Return resp
        End Function

        Private Shared Function BuildErrorResponse(instanceId As String,
                                                   errCode As NodeErrorCodes,
                                                   errMsg As String) As InstanceStatusResponse
            Return New InstanceStatusResponse With {
                .InstanceId = instanceId,
                .CurrentState = InstanceState.Stopped,
                .ErrorMessage = errMsg
            }
        End Function

    End Class

    ' ============================================================
    '  ManagedInstance — internal state per tracked process
    ' ============================================================

    Friend Class ManagedInstance
        Public Property InstanceId As String
        Public Property State As InstanceState
        Public Property Process As Process
        Public Property Pid As Integer
        Public Property StartedAt As DateTime
        Public Property StateChangedAt As DateTime
        Public Property CrashPolicy As CrashRestartPolicy
        Public Property MaxCrashCount As Integer = 5
        Public Property CrashWindowMinutes As Integer = 60

        ''' <summary>
        ''' See StartInstanceRequest.CrashCountResetAfterSeconds.
        ''' 0 disables the reset timer.
        ''' </summary>
        Public Property CrashCountResetAfterSeconds As Integer = 300

        ''' <summary>
        ''' See StartInstanceRequest.MinRestartDelayMs. Floor for
        ''' restart backoff in milliseconds.
        ''' </summary>
        Public Property MinRestartDelayMs As Integer = 0

        Public Property StopIntentPending As Boolean
        Public Property CrashCount As Integer
        Public Property LastExitCode As Integer?

        ''' <summary>
        ''' File tailer cancellation tokens, one per tailed log file.
        ''' Cancelled when the instance stops.
        ''' </summary>
        Public Property TailerCancellations As List(Of CancellationTokenSource)

        ''' <summary>
        ''' Preserved from the first start so restarts use the
        ''' same configuration. Previously derived from
        ''' Process.StartInfo, but we now stash it explicitly so
        ''' RestartInstanceAsync never silently loses it.
        ''' </summary>
        Public Property StartInfo As ProcessStartInfo

        ''' <summary>
        ''' Log file paths declared by the plugin at start time.
        ''' Preserved so restart can re-spin the file tailers (the
        ''' old restart path skipped this and left UE4-style
        ''' servers with no log capture after a crash-restart).
        ''' </summary>
        Public Property LogFilePaths As IList(Of String)

        ''' <summary>
        ''' Declarative parse rules — preserved so restart can
        ''' re-register them with the EventStore. Without this,
        ''' player/state tracking goes dark after any crash-restart.
        ''' </summary>
        Public Property ParseRules As IList(Of LogParseRule)

        ''' <summary>
        ''' Whether stdout/stderr should be written to the ring
        ''' buffer. Derived from the resolved Strategy — True only
        ''' for Strategy.StdoutCapture. False for both hidden-console
        ''' strategies since neither feeds stdio into the buffer.
        ''' </summary>
        Public Property CaptureStdout As Boolean

        ''' <summary>
        ''' Resolved spawn strategy for this instance. Persisted
        ''' across crash-restart cycles so subsequent re-spawns
        ''' use the same path the original start did. See
        ''' ProcessManager.SpawnStrategy for the values.
        ''' </summary>
        Public Property Strategy As ProcessManager.SpawnStrategy

        ''' <summary>
        ''' Plugin-declared StdoutIsLog flag preserved verbatim from
        ''' the StartInstanceRequest. Mainly used for diagnostic
        ''' logging — the actual dispatch decision is on Strategy.
        ''' </summary>
        Public Property StdoutIsLog As Boolean

        ''' <summary>
        ''' Plugin-declared RequiresConsoleIsolation flag preserved
        ''' verbatim from the StartInstanceRequest. Mainly used for
        ''' diagnostic logging — the actual dispatch decision is on
        ''' Strategy.
        ''' </summary>
        Public Property RequiresConsoleIsolation As Boolean

        ''' <summary>
        ''' Resolved file-tailer startup delay in milliseconds.
        ''' Set from the StartInstanceRequest; -1/negative on the
        ''' wire means "plugin didn't specify" and the resolution
        ''' replaces it with the legacy 5000ms default. 0 means
        ''' the plugin explicitly opted into immediate tailing.
        ''' Persisted across crash-restart cycles.
        ''' </summary>
        Public Property LogTailerStartDelayMs As Integer = 5000
    End Class

    ' ============================================================
    '  PolicyDecision — result of crash policy evaluation
    ' ============================================================

    Public Class PolicyDecision
        Public Property Action As PolicyAction
        Public Property DelayMs As Integer
        Public Property Reason As String
    End Class

    Public Enum PolicyAction
        Restart
        Halt
        NoRestart
    End Enum

End Namespace
