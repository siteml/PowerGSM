' ============================================================
'  GSM.Shim — native game-process spawn (Phase 8-1, slice 1b)
'
'  The shim owns the game's stdio at the raw-handle level rather than
'  via System.Diagnostics.Process redirection, which is what lets a Node
'  restart never sever the pipes. On Windows this is CreatePipe +
'  CreateProcessW with STARTF_USESTDHANDLES + CREATE_NO_WINDOW (the
'  Strategy A "redirected stdio, no console" shape). The struct/PInvoke
'  declarations and the env-block / command-line quoting mirror the
'  validated versions in GSM.Node\ProcessManager.vb.
'
'  Linux uses posix_spawn + pipe2 (slice 1c): the same redirected-stdio
'  (Strategy A) shape, with the child placed in a new session via
'  POSIX_SPAWN_SETSID so a Ctrl+C in the Node's terminal can't deliver
'  SIGINT to the game. The B/C hidden-console strategies are Windows-only
'  and never reach the Linux path.
' ============================================================
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Win32.SafeHandles
Imports GSM.Shim.Protocol

''' <summary>
''' A spawned game process whose stdio the shim owns directly. The three
''' streams are the PARENT ends of the redirected pipes (read stdout/stderr,
''' write stdin).
''' </summary>
Friend Interface IGameProcess
    Inherits IDisposable
    ReadOnly Property Pid As Integer
    ReadOnly Property StdIn As Stream
    ReadOnly Property StdOut As Stream
    ReadOnly Property StdErr As Stream
    ''' <summary>Blocks (off-thread) until the game exits; returns its exit code.</summary>
    Function WaitForExitAsync(ct As CancellationToken) As Task(Of Integer)
    ''' <summary>Force-terminate (basic kill; graceful CTRL_C is slice 5).</summary>
    Sub Kill()
End Interface

''' <summary>Windows IGameProcess backed by a raw process handle from CreateProcessW.</summary>
Friend NotInheritable Class WindowsGameProcess
    Implements IGameProcess

    Private ReadOnly _pid As Integer
    Private _hProcess As IntPtr
    Private ReadOnly _stdin As Stream
    Private ReadOnly _stdout As Stream
    Private ReadOnly _stderr As Stream
    Private _disposed As Boolean

    Public Sub New(pid As Integer, hProcess As IntPtr, stdinStream As Stream, stdoutStream As Stream, stderrStream As Stream)
        _pid = pid
        _hProcess = hProcess
        _stdin = stdinStream
        _stdout = stdoutStream
        _stderr = stderrStream
    End Sub

    Public ReadOnly Property Pid As Integer Implements IGameProcess.Pid
        Get
            Return _pid
        End Get
    End Property

    Public ReadOnly Property StdIn As Stream Implements IGameProcess.StdIn
        Get
            Return _stdin
        End Get
    End Property

    Public ReadOnly Property StdOut As Stream Implements IGameProcess.StdOut
        Get
            Return _stdout
        End Get
    End Property

    Public ReadOnly Property StdErr As Stream Implements IGameProcess.StdErr
        Get
            Return _stderr
        End Get
    End Property

    Public Async Function WaitForExitAsync(ct As CancellationToken) As Task(Of Integer) Implements IGameProcess.WaitForExitAsync
        Await Task.Run(Sub() NativeSpawn.WaitForSingleObject(_hProcess, NativeSpawn.INFINITE)).ConfigureAwait(False)
        Dim code As UInteger = 0UI
        NativeSpawn.GetExitCodeProcess(_hProcess, code)
        Return CInt(code And &H7FFFFFFFUI)
    End Function

    Public Sub Kill() Implements IGameProcess.Kill
        If _hProcess <> IntPtr.Zero Then
            NativeSpawn.TerminateProcess(_hProcess, 1UI)
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        TryDispose(_stdin)
        TryDispose(_stdout)
        TryDispose(_stderr)
        If _hProcess <> IntPtr.Zero Then
            NativeSpawn.CloseHandle(_hProcess)
            _hProcess = IntPtr.Zero
        End If
    End Sub

    Private Shared Sub TryDispose(s As Stream)
        Try
            If s IsNot Nothing Then s.Dispose()
        Catch
            ' best-effort
        End Try
    End Sub

End Class

''' <summary>
''' Linux IGameProcess backed by a PID from posix_spawn. The three streams
''' are the PARENT ends of the redirected pipes; exit is reaped via waitpid,
''' kill via SIGKILL.
''' </summary>
Friend NotInheritable Class LinuxGameProcess
    Implements IGameProcess

    Private ReadOnly _pid As Integer
    Private ReadOnly _stdin As Stream
    Private ReadOnly _stdout As Stream
    Private ReadOnly _stderr As Stream
    Private _disposed As Boolean
    Private _reaped As Boolean

    Public Sub New(pid As Integer, stdinStream As Stream, stdoutStream As Stream, stderrStream As Stream)
        _pid = pid
        _stdin = stdinStream
        _stdout = stdoutStream
        _stderr = stderrStream
    End Sub

    Public ReadOnly Property Pid As Integer Implements IGameProcess.Pid
        Get
            Return _pid
        End Get
    End Property

    Public ReadOnly Property StdIn As Stream Implements IGameProcess.StdIn
        Get
            Return _stdin
        End Get
    End Property

    Public ReadOnly Property StdOut As Stream Implements IGameProcess.StdOut
        Get
            Return _stdout
        End Get
    End Property

    Public ReadOnly Property StdErr As Stream Implements IGameProcess.StdErr
        Get
            Return _stderr
        End Get
    End Property

    ''' <summary>
    ''' Blocks (off-thread) on waitpid until the game exits, returning a
    ''' shell-style code: the exit status for a normal exit, or 128+signal
    ''' when terminated by a signal. The shim never uses
    ''' System.Diagnostics.Process, so the runtime installs no SIGCHLD reaper
    ''' to race this waitpid for our child.
    ''' </summary>
    Public Function WaitForExitAsync(ct As CancellationToken) As Task(Of Integer) Implements IGameProcess.WaitForExitAsync
        Return Task.Run(Function() WaitBlocking())
    End Function

    Private Function WaitBlocking() As Integer
        Dim status As Integer = 0
        Do
            Dim r As Integer = NativeSpawn.waitpid(_pid, status, 0)
            If r = _pid Then
                _reaped = True
                Dim termSig As Integer = status And &H7F
                If termSig = 0 Then
                    Return (status >> 8) And &HFF        ' WEXITSTATUS
                End If
                Return 128 + termSig                     ' killed by signal
            End If
            ' r < 0: inspect errno; retry on EINTR, otherwise give up.
            If Marshal.GetLastWin32Error() = NativeSpawn.EINTR Then Continue Do
            _reaped = True
            Return -1
        Loop
    End Function

    Public Sub Kill() Implements IGameProcess.Kill
        ' Hard kill (parity with Windows TerminateProcess). The graceful stop
        ' is the Node delivering SIGTERM to the PID directly (slice 5); this is
        ' the last-resort path the supervisor's StopGame/Shutdown drives.
        If Not _reaped Then
            NativeSpawn.kill(_pid, NativeSpawn.SIGKILL)
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        TryDispose(_stdin)
        TryDispose(_stdout)
        TryDispose(_stderr)
        ' No process handle to close on Linux; the pid is reaped by the
        ' supervisor's exit watcher (WaitForExitAsync), so we don't waitpid
        ' here and risk a double-reap.
    End Sub

    Private Shared Sub TryDispose(s As Stream)
        Try
            If s IsNot Nothing Then s.Dispose()
        Catch
            ' best-effort
        End Try
    End Sub

End Class

''' <summary>OS dispatch + the Windows CreateProcessW/CreatePipe implementation.</summary>
Friend Module NativeSpawn

    Public Function Spawn(spec As SpawnSpec) As IGameProcess
        If spec Is Nothing OrElse String.IsNullOrEmpty(spec.ExePath) Then
            Throw New ArgumentException("SpawnSpec.ExePath is required")
        End If
        If OperatingSystem.IsWindows() Then
            Return SpawnWindows(spec)
        ElseIf OperatingSystem.IsLinux() Then
            Return SpawnLinux(spec)
        End If
        Throw New PlatformNotSupportedException("GSM.Shim native spawn supports Windows and Linux only")
    End Function

    ' ---------- Windows ----------

    Private Function SpawnWindows(spec As SpawnSpec) As IGameProcess
        ' Dispatch on the resolved strategy. A = redirected stdio (pipes, no
        ' console); B/C = the game's own hidden console (no redirection — the
        ' Node tails the log file). C additionally wraps in cmd.exe for games
        ' that defeat CREATE_NEW_CONSOLE via AttachConsole(parent) (Factorio).
        Dim strat As String = If(spec.Strategy, "StdoutCapture")
        If String.Equals(strat, "HiddenConsoleDirect", StringComparison.OrdinalIgnoreCase) Then
            Return SpawnWindowsHiddenConsole(spec, wrapInCmd:=False)
        ElseIf String.Equals(strat, "HiddenConsoleWrapped", StringComparison.OrdinalIgnoreCase) Then
            Return SpawnWindowsHiddenConsole(spec, wrapInCmd:=True)
        End If
        Return SpawnWindowsRedirected(spec)
    End Function

    ''' <summary>
    ''' Strategy A: redirected stdio (CreatePipe x3 + STARTF_USESTDHANDLES +
    ''' CREATE_NO_WINDOW). The shim owns the parent pipe ends and pumps
    ''' stdout/stderr to the Node; this is what lets a Node restart never sever
    ''' the stream.
    ''' </summary>
    Private Function SpawnWindowsRedirected(spec As SpawnSpec) As IGameProcess
        Dim sa As New SECURITY_ATTRIBUTES()
        sa.nLength = Marshal.SizeOf(Of SECURITY_ATTRIBUTES)()
        sa.lpSecurityDescriptor = IntPtr.Zero
        sa.bInheritHandle = True

        Dim outRead, outWrite As IntPtr
        Dim errRead, errWrite As IntPtr
        Dim inRead, inWrite As IntPtr

        If Not CreatePipe(outRead, outWrite, sa, 0UI) Then ThrowLastError("CreatePipe(stdout)")
        ' The parent's READ end of stdout must not be inheritable by the child.
        SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0UI)

        If Not CreatePipe(errRead, errWrite, sa, 0UI) Then ThrowLastError("CreatePipe(stderr)")
        SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0UI)

        If Not CreatePipe(inRead, inWrite, sa, 0UI) Then ThrowLastError("CreatePipe(stdin)")
        ' The parent's WRITE end of stdin must not be inheritable by the child.
        SetHandleInformation(inWrite, HANDLE_FLAG_INHERIT, 0UI)

        Dim si As New STARTUPINFOW()
        si.cb = CUInt(Marshal.SizeOf(Of STARTUPINFOW)())
        si.dwFlags = STARTF_USESTDHANDLES
        si.hStdInput = inRead
        si.hStdOutput = outWrite
        si.hStdError = errWrite

        Dim pi As New PROCESS_INFORMATION()

        ' "exe" args  — quote the exe, append the (already Win32-quoted) args.
        Dim cmdLine As New StringBuilder()
        cmdLine.Append(""""c)
        cmdLine.Append(spec.ExePath)
        cmdLine.Append(""""c)
        If Not String.IsNullOrEmpty(spec.Arguments) Then
            cmdLine.Append(" "c)
            cmdLine.Append(spec.Arguments)
        End If

        Dim envBlockPtr As IntPtr = IntPtr.Zero
        Try
            envBlockPtr = BuildEnvironmentBlock(spec.Environment)

            Dim flags As UInteger = CREATE_NO_WINDOW Or CREATE_UNICODE_ENVIRONMENT

            Dim workDir As String = If(String.IsNullOrEmpty(spec.WorkingDirectory), Nothing, spec.WorkingDirectory)

            Dim ok = CreateProcessW(
                Nothing,        ' lpApplicationName: parsed from cmdLine
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                True,           ' bInheritHandles: child inherits the pipe ends
                flags,
                envBlockPtr,
                workDir,
                si,
                pi)

            If Not ok Then
                Dim err = Marshal.GetLastWin32Error()
                ' Close every handle we created before bailing.
                CloseHandle(outRead) : CloseHandle(outWrite)
                CloseHandle(errRead) : CloseHandle(errWrite)
                CloseHandle(inRead) : CloseHandle(inWrite)
                Throw New IOException($"CreateProcessW failed for {spec.ExePath} (Win32Error={err})")
            End If

            ' Don't need the thread handle.
            If pi.hThread <> IntPtr.Zero Then CloseHandle(pi.hThread)

            ' Close the CHILD ends in the parent so EOF propagates when the
            ' game exits / closes its stdio.
            CloseHandle(outWrite)
            CloseHandle(errWrite)
            CloseHandle(inRead)

            ' Wrap the PARENT ends as streams (SafeFileHandle owns + closes them).
            Dim stdoutStream As New FileStream(New SafeFileHandle(outRead, ownsHandle:=True), FileAccess.Read)
            Dim stderrStream As New FileStream(New SafeFileHandle(errRead, ownsHandle:=True), FileAccess.Read)
            Dim stdinStream As New FileStream(New SafeFileHandle(inWrite, ownsHandle:=True), FileAccess.Write)

            Return New WindowsGameProcess(pi.dwProcessId, pi.hProcess, stdinStream, stdoutStream, stderrStream)
        Finally
            If envBlockPtr <> IntPtr.Zero Then Marshal.FreeHGlobal(envBlockPtr)
        End Try
    End Function

    ''' <summary>
    ''' Strategy B/C: native CreateProcessW with CREATE_NEW_CONSOLE +
    ''' STARTF_USESHOWWINDOW/SW_HIDE — the child gets its own (invisible)
    ''' console and writes there. No stdio is redirected: the Node tails the
    ''' game's log file, so the shim neither creates pipes nor pumps output.
    ''' wrapInCmd=True spawns cmd.exe /S /c "&lt;exe&gt; &lt;args&gt;" for games
    ''' that defeat CREATE_NEW_CONSOLE via AttachConsole(parent) (Factorio);
    ''' the tracked process is then cmd.exe, whose exit code /c forwards. The
    ''' returned game process has Nothing for all three streams.
    ''' </summary>
    Private Function SpawnWindowsHiddenConsole(spec As SpawnSpec, wrapInCmd As Boolean) As IGameProcess
        Dim si As New STARTUPINFOW()
        si.cb = CUInt(Marshal.SizeOf(Of STARTUPINFOW)())
        si.dwFlags = STARTF_USESHOWWINDOW
        si.wShowWindow = SW_HIDE

        Dim pi As New PROCESS_INFORMATION()

        ' Build the command line. The game's exe path is quoted; spec.Arguments
        ' is already a single Win32-quoted string (as ProcessManager builds it).
        Dim cmdLine As New StringBuilder()
        If wrapInCmd Then
            ' payload = "&lt;exe&gt;" &lt;args&gt;  ;  full = "cmd.exe" /S /c "&lt;payload&gt;".
            ' /S strips the outer quotes; the inner exe quotes survive so a
            ' game path with spaces parses correctly. Mirrors ProcessManager's
            ' SpawnWrappedConsoleProcess.
            Dim payload As New StringBuilder()
            payload.Append(""""c)
            payload.Append(spec.ExePath)
            payload.Append(""""c)
            If Not String.IsNullOrEmpty(spec.Arguments) Then
                payload.Append(" "c)
                payload.Append(spec.Arguments)
            End If
            cmdLine.Append("""cmd.exe"" /S /c """)
            cmdLine.Append(payload.ToString())
            cmdLine.Append(""""c)
        Else
            cmdLine.Append(""""c)
            cmdLine.Append(spec.ExePath)
            cmdLine.Append(""""c)
            If Not String.IsNullOrEmpty(spec.Arguments) Then
                cmdLine.Append(" "c)
                cmdLine.Append(spec.Arguments)
            End If
        End If

        Dim envBlockPtr As IntPtr = IntPtr.Zero
        Try
            envBlockPtr = BuildEnvironmentBlock(spec.Environment)

            ' CREATE_NEW_CONSOLE gives the child its own console; SW_HIDE keeps
            ' the window invisible. No STARTF_USESTDHANDLES and no inherited
            ' handles — there are no pipes.
            Dim flags As UInteger = CREATE_NEW_CONSOLE Or CREATE_UNICODE_ENVIRONMENT
            Dim workDir As String = If(String.IsNullOrEmpty(spec.WorkingDirectory), Nothing, spec.WorkingDirectory)

            Dim ok = CreateProcessW(
                Nothing,
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                False,          ' bInheritHandles: nothing to inherit
                flags,
                envBlockPtr,
                workDir,
                si,
                pi)

            If Not ok Then
                Dim err = Marshal.GetLastWin32Error()
                Throw New IOException($"CreateProcessW (hidden console) failed for {spec.ExePath} (Win32Error={err})")
            End If

            If pi.hThread <> IntPtr.Zero Then CloseHandle(pi.hThread)

            ' No redirected stdio: the game owns its hidden console and the
            ' Node tails the log file. All three streams are Nothing.
            Return New WindowsGameProcess(pi.dwProcessId, pi.hProcess, Nothing, Nothing, Nothing)
        Finally
            If envBlockPtr <> IntPtr.Zero Then Marshal.FreeHGlobal(envBlockPtr)
        End Try
    End Function

    ''' <summary>
    ''' Unicode env block (KEY=VAL\0...\0\0) for CreateProcess. IntPtr.Zero
    ''' when empty => child inherits the parent's environment. Caller frees a
    ''' non-zero pointer. Mirrors ProcessManager.BuildEnvironmentBlock but
    ''' takes a Dictionary (the SpawnSpec shape).
    ''' </summary>
    Private Function BuildEnvironmentBlock(envVars As System.Collections.Generic.Dictionary(Of String, String)) As IntPtr
        If envVars Is Nothing OrElse envVars.Count = 0 Then Return IntPtr.Zero

        Dim entries As New System.Collections.Generic.List(Of String)
        For Each kvp In envVars
            If String.IsNullOrEmpty(kvp.Key) Then Continue For
            entries.Add(kvp.Key & "=" & If(kvp.Value, ""))
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

    Private Sub ThrowLastError(what As String)
        Throw New IOException($"{what} failed (Win32Error={Marshal.GetLastWin32Error()})")
    End Sub

    ' ---------- Win32 interop (mirrors GSM.Node\ProcessManager.vb) ----------

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

    <StructLayout(LayoutKind.Sequential)>
    Private Structure SECURITY_ATTRIBUTES
        Public nLength As Integer
        Public lpSecurityDescriptor As IntPtr
        Public bInheritHandle As Boolean
    End Structure

    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function CreateProcessW(
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
    Private Function CreatePipe(ByRef hReadPipe As IntPtr, ByRef hWritePipe As IntPtr,
                                ByRef lpPipeAttributes As SECURITY_ATTRIBUTES, nSize As UInteger) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function SetHandleInformation(hObject As IntPtr, dwMask As UInteger, dwFlags As UInteger) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Friend Function CloseHandle(hObject As IntPtr) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Friend Function WaitForSingleObject(hHandle As IntPtr, dwMilliseconds As UInteger) As UInteger
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Friend Function GetExitCodeProcess(hProcess As IntPtr, ByRef lpExitCode As UInteger) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Friend Function TerminateProcess(hProcess As IntPtr, uExitCode As UInteger) As Boolean
    End Function

    Friend Const INFINITE As UInteger = &HFFFFFFFFUI
    Private Const CREATE_NO_WINDOW As UInteger = &H8000000UI
    Private Const CREATE_UNICODE_ENVIRONMENT As UInteger = &H400UI
    Private Const STARTF_USESTDHANDLES As UInteger = &H100UI
    Private Const STARTF_USESHOWWINDOW As UInteger = &H1UI
    Private Const HANDLE_FLAG_INHERIT As UInteger = &H1UI
    Private Const CREATE_NEW_CONSOLE As UInteger = &H10UI
    Private Const SW_HIDE As UShort = 0US

    ' ---------- Linux (posix_spawn + pipe2) ----------

    ''' <summary>
    ''' Linux spawn: redirected stdio via pipe2 + posix_spawn. On Linux every
    ''' instance is Strategy A — the game's stdout/stderr are piped to the shim,
    ''' which relays them to the Node — so spec.Strategy is ignored (B/C are
    ''' Windows-only). The child is started in a NEW SESSION
    ''' (POSIX_SPAWN_SETSID) so it has no controlling terminal: a Ctrl+C in the
    ''' Node's terminal cannot deliver SIGINT to the game (the Node handles its
    ''' own SIGINT and detaches shims). This is the shim-side replacement for
    ''' the old Node `WrapInSetsidIfLinux` command wrapper — now that the shim
    ''' is the direct parent, one syscall flag does the job.
    ''' </summary>
    Private Function SpawnLinux(spec As SpawnSpec) As IGameProcess
        ' Three O_CLOEXEC pipes so no fd leaks past the child's exec. Index 0 is
        ' the read end, index 1 the write end.
        Dim stdinPipe As Integer() = MakePipe()    ' child reads stdin <- [0]; parent writes -> [1]
        Dim stdoutPipe As Integer() = MakePipe()   ' child writes stdout -> [1]; parent reads <- [0]
        Dim stderrPipe As Integer() = MakePipe()   ' child writes stderr -> [1]; parent reads <- [0]

        Dim fa As IntPtr = IntPtr.Zero
        Dim attr As IntPtr = IntPtr.Zero
        Dim argv As IntPtr = IntPtr.Zero
        Dim envp As IntPtr = IntPtr.Zero
        Dim argvPtrs As List(Of IntPtr) = Nothing
        Dim envpPtrs As List(Of IntPtr) = Nothing
        Dim pid As Integer = 0

        Try
            ' File actions: dup the child pipe ends onto fd 0/1/2. The originals
            ' are O_CLOEXEC so they close on exec; the dup2 targets (0/1/2) lose
            ' O_CLOEXEC and survive. pipe2 hands out fds >= 3, so there's no
            ' aliasing with the std fds.
            fa = Marshal.AllocHGlobal(SpawnOpaqueBytes)
            ThrowIfPosixErr(posix_spawn_file_actions_init(fa), "posix_spawn_file_actions_init")
            ThrowIfPosixErr(posix_spawn_file_actions_adddup2(fa, stdinPipe(0), 0), "adddup2(stdin)")
            ThrowIfPosixErr(posix_spawn_file_actions_adddup2(fa, stdoutPipe(1), 1), "adddup2(stdout)")
            ThrowIfPosixErr(posix_spawn_file_actions_adddup2(fa, stderrPipe(1), 2), "adddup2(stderr)")
            If Not String.IsNullOrEmpty(spec.WorkingDirectory) Then
                ' addchdir_np runs the chdir in the child before exec (glibc
                ' 2.29+, i.e. Ubuntu 20.04+), so it doesn't disturb the shim's
                ' own cwd the way a parent-side chdir would.
                ThrowIfPosixErr(posix_spawn_file_actions_addchdir_np(fa, spec.WorkingDirectory),
                                "posix_spawn_file_actions_addchdir_np")
            End If

            ' Attributes: new session so the game has no controlling terminal.
            attr = Marshal.AllocHGlobal(SpawnOpaqueBytes)
            ThrowIfPosixErr(posix_spawnattr_init(attr), "posix_spawnattr_init")
            ThrowIfPosixErr(posix_spawnattr_setflags(attr, POSIX_SPAWN_SETSID), "posix_spawnattr_setflags")

            ' argv = exe + parsed args (the Win32-quoted string the Node built);
            ' envp = the full env block the Node sent (fallback: inherit ours).
            Dim argvList As New List(Of String) From {spec.ExePath}
            argvList.AddRange(ParseWin32Arguments(spec.Arguments))
            argv = BuildCStringArray(argvList, argvPtrs)
            envp = BuildCStringArray(BuildEnvList(spec.Environment), envpPtrs)

            ' posix_spawn returns the error number directly (0 = success); it
            ' does NOT use errno. spec.ExePath is absolute (the Node resolves
            ' it), so the non-PATH-searching posix_spawn is correct.
            Dim rc As Integer = posix_spawn(pid, spec.ExePath, fa, attr, argv, envp)
            If rc <> 0 Then
                Throw New IOException($"posix_spawn failed for {spec.ExePath} (error={rc})")
            End If
        Catch
            ' Nothing took ownership of the pipe fds yet — close all six.
            SafeClose(stdinPipe(0)) : SafeClose(stdinPipe(1))
            SafeClose(stdoutPipe(0)) : SafeClose(stdoutPipe(1))
            SafeClose(stderrPipe(0)) : SafeClose(stderrPipe(1))
            Throw
        Finally
            If fa <> IntPtr.Zero Then
                posix_spawn_file_actions_destroy(fa)
                Marshal.FreeHGlobal(fa)
            End If
            If attr <> IntPtr.Zero Then
                posix_spawnattr_destroy(attr)
                Marshal.FreeHGlobal(attr)
            End If
            FreeCStringArray(argv, argvPtrs)
            FreeCStringArray(envp, envpPtrs)
        End Try

        ' Success: close the child ends in the parent so EOF propagates when the
        ' game closes its stdio, and wrap the parent ends as streams (the
        ' SafeFileHandle owns + closes each fd on stream dispose).
        SafeClose(stdinPipe(0))
        SafeClose(stdoutPipe(1))
        SafeClose(stderrPipe(1))

        Dim stdinStream As New FileStream(New SafeFileHandle(New IntPtr(stdinPipe(1)), ownsHandle:=True), FileAccess.Write)
        Dim stdoutStream As New FileStream(New SafeFileHandle(New IntPtr(stdoutPipe(0)), ownsHandle:=True), FileAccess.Read)
        Dim stderrStream As New FileStream(New SafeFileHandle(New IntPtr(stderrPipe(0)), ownsHandle:=True), FileAccess.Read)

        Return New LinuxGameProcess(pid, stdinStream, stdoutStream, stderrStream)
    End Function

    Private Function MakePipe() As Integer()
        Dim fds(1) As Integer
        fds(0) = -1 : fds(1) = -1
        If pipe2(fds, O_CLOEXEC) <> 0 Then
            Throw New IOException($"pipe2 failed (errno={Marshal.GetLastWin32Error()})")
        End If
        Return fds
    End Function

    Private Sub SafeClose(fd As Integer)
        If fd >= 0 Then
            Try : close(fd) : Catch : End Try
        End If
    End Sub

    Private Sub ThrowIfPosixErr(rc As Integer, what As String)
        ' posix_spawn_* / posix_spawnattr_* return the error number directly.
        If rc <> 0 Then Throw New IOException($"{what} failed (error={rc})")
    End Sub

    ''' <summary>
    ''' Build a NULL-terminated unmanaged char* array of UTF-8 strings for
    ''' argv/envp. The per-string pointers are returned via <paramref name="ptrs"/>
    ''' so the caller can free them after posix_spawn.
    ''' </summary>
    Private Function BuildCStringArray(items As List(Of String), ByRef ptrs As List(Of IntPtr)) As IntPtr
        ptrs = New List(Of IntPtr)()
        For Each it In items
            ptrs.Add(Marshal.StringToCoTaskMemUTF8(If(it, "")))
        Next
        Dim arr As IntPtr = Marshal.AllocHGlobal((ptrs.Count + 1) * IntPtr.Size)
        For idx As Integer = 0 To ptrs.Count - 1
            Marshal.WriteIntPtr(arr, idx * IntPtr.Size, ptrs(idx))
        Next
        Marshal.WriteIntPtr(arr, ptrs.Count * IntPtr.Size, IntPtr.Zero)   ' NULL terminator
        Return arr
    End Function

    Private Sub FreeCStringArray(arrayPtr As IntPtr, ptrs As List(Of IntPtr))
        If ptrs IsNot Nothing Then
            For Each p In ptrs
                If p <> IntPtr.Zero Then Marshal.FreeCoTaskMem(p)
            Next
        End If
        If arrayPtr <> IntPtr.Zero Then Marshal.FreeHGlobal(arrayPtr)
    End Sub

    ''' <summary>
    ''' KEY=VAL list for envp. The Node normally sends a full copy of the
    ''' game's environment in spec.Environment; if it's empty we inherit the
    ''' shim's own environment so the child still has PATH etc.
    ''' </summary>
    Private Function BuildEnvList(env As Dictionary(Of String, String)) As List(Of String)
        Dim list As New List(Of String)
        If env IsNot Nothing AndAlso env.Count > 0 Then
            For Each kvp In env
                If String.IsNullOrEmpty(kvp.Key) Then Continue For
                list.Add(kvp.Key & "=" & If(kvp.Value, ""))
            Next
            Return list
        End If
        For Each de As System.Collections.DictionaryEntry In System.Environment.GetEnvironmentVariables()
            Dim k As String = TryCast(de.Key, String)
            If String.IsNullOrEmpty(k) Then Continue For
            list.Add(k & "=" & If(TryCast(de.Value, String), ""))
        Next
        Return list
    End Function

    ''' <summary>
    ''' Split a Win32-quoted argument string (the form ProcessManager builds for
    ''' psi.Arguments, sent verbatim in SpawnSpec.Arguments) into argv entries,
    ''' following the CommandLineToArgvW rules so what the Node quoted is what
    ''' the game receives. Realistic game args (flags, values, the occasional
    ''' quoted path) round-trip exactly; the backslash/quote handling matches
    ''' Windows for the rare edge cases.
    ''' </summary>
    Friend Function ParseWin32Arguments(s As String) As List(Of String)
        Dim result As New List(Of String)
        If String.IsNullOrEmpty(s) Then Return result

        Dim cur As New StringBuilder()
        Dim inQuotes As Boolean = False
        Dim hasToken As Boolean = False   ' so an explicit "" yields an empty arg
        Dim i As Integer = 0
        Dim n As Integer = s.Length

        While i < n
            Dim c As Char = s(i)
            If c = "\"c Then
                Dim slashes As Integer = 0
                While i < n AndAlso s(i) = "\"c
                    slashes += 1
                    i += 1
                End While
                If i < n AndAlso s(i) = """"c Then
                    cur.Append("\"c, slashes \ 2)
                    If (slashes And 1) = 1 Then
                        cur.Append(""""c)         ' 2k+1 backslashes -> escaped literal quote
                    Else
                        inQuotes = Not inQuotes  ' 2k backslashes -> the quote toggles state
                    End If
                    hasToken = True
                    i += 1
                Else
                    cur.Append("\"c, slashes)
                    hasToken = True
                End If
            ElseIf c = """"c Then
                If inQuotes AndAlso i + 1 < n AndAlso s(i + 1) = """"c Then
                    cur.Append(""""c)            ' "" inside quotes -> one literal quote
                    hasToken = True
                    i += 2
                Else
                    inQuotes = Not inQuotes
                    hasToken = True
                    i += 1
                End If
            ElseIf (c = " "c OrElse c = ChrW(9)) AndAlso Not inQuotes Then
                If hasToken Then
                    result.Add(cur.ToString())
                    cur.Clear()
                    hasToken = False
                End If
                i += 1
            Else
                cur.Append(c)
                hasToken = True
                i += 1
            End If
        End While

        If hasToken Then result.Add(cur.ToString())
        Return result
    End Function

    ' ---------- Linux interop (libc) ----------

    Friend Const EINTR As Integer = 4
    Friend Const SIGKILL As Integer = 9
    Friend Const SIGTERM As Integer = 15
    Private Const O_CLOEXEC As Integer = &H80000          ' octal 02000000
    Private Const POSIX_SPAWN_SETSID As Short = &H80S     ' glibc extension (>= 2.26)
    Private Const SpawnOpaqueBytes As Integer = 1024      ' > sizeof(posix_spawn{attr,_file_actions}_t) on glibc

    <DllImport("libc", SetLastError:=True)>
    Private Function pipe2(<Out> pipefd As Integer(), flags As Integer) As Integer
    End Function

    <DllImport("libc", SetLastError:=True, EntryPoint:="close")>
    Friend Function close(fd As Integer) As Integer
    End Function

    <DllImport("libc", SetLastError:=True, EntryPoint:="waitpid")>
    Friend Function waitpid(pid As Integer, ByRef wstatus As Integer, options As Integer) As Integer
    End Function

    <DllImport("libc", SetLastError:=True, EntryPoint:="kill")>
    Friend Function kill(pid As Integer, sig As Integer) As Integer
    End Function

    <DllImport("libc", SetLastError:=True, EntryPoint:="posix_spawn")>
    Private Function posix_spawn(ByRef pid As Integer,
                                 <MarshalAs(UnmanagedType.LPUTF8Str)> path As String,
                                 fileActions As IntPtr, attrp As IntPtr,
                                 argv As IntPtr, envp As IntPtr) As Integer
    End Function

    <DllImport("libc", EntryPoint:="posix_spawn_file_actions_init")>
    Private Function posix_spawn_file_actions_init(fa As IntPtr) As Integer
    End Function

    <DllImport("libc", EntryPoint:="posix_spawn_file_actions_destroy")>
    Private Function posix_spawn_file_actions_destroy(fa As IntPtr) As Integer
    End Function

    <DllImport("libc", EntryPoint:="posix_spawn_file_actions_adddup2")>
    Private Function posix_spawn_file_actions_adddup2(fa As IntPtr, fd As Integer, newfd As Integer) As Integer
    End Function

    <DllImport("libc", EntryPoint:="posix_spawn_file_actions_addchdir_np")>
    Private Function posix_spawn_file_actions_addchdir_np(fa As IntPtr,
                                 <MarshalAs(UnmanagedType.LPUTF8Str)> path As String) As Integer
    End Function

    <DllImport("libc", EntryPoint:="posix_spawnattr_init")>
    Private Function posix_spawnattr_init(attr As IntPtr) As Integer
    End Function

    <DllImport("libc", EntryPoint:="posix_spawnattr_destroy")>
    Private Function posix_spawnattr_destroy(attr As IntPtr) As Integer
    End Function

    <DllImport("libc", EntryPoint:="posix_spawnattr_setflags")>
    Private Function posix_spawnattr_setflags(attr As IntPtr, flags As Short) As Integer
    End Function

End Module
