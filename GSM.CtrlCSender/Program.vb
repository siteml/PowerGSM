Imports System
Imports System.Runtime.InteropServices
Imports System.Threading

' ============================================================
'  GSM.CtrlCSender
'
'  Tiny helper that delivers CTRL_C_EVENT to a target process's
'  (hidden) console. Designed to be invoked by GSM.Node when
'  stopping a UE4 / console-subsystem game-server child.
'
'  Why this works for UE4 dedicated servers (e.g. Last Oasis
'  MistServer) when taskkill does not:
'
'    UE4 installs a SetConsoleCtrlHandler in LaunchWindows.cpp
'    that routes CTRL_C_EVENT (and CTRL_BREAK_EVENT, etc.) to
'    RequestEngineExit — the engine's graceful shutdown path.
'    UE4 servers do NOT respond to WM_CLOSE, which is what
'    taskkill /PID (no /F) sends. Hence taskkill never gives
'    UE4 servers a clean shutdown — only Process.Kill does,
'    and that's a hard-kill.
'
'  Why a separate helper executable instead of doing this
'  inline in the Node:
'
'    AttachConsole is process-global state. If two instances
'    are stopped concurrently, two threads inside the Node
'    would race on the single attachment slot. We'd also have
'    to carefully save/restore the Node's own console-control-
'    handler around every call so the CTRL_C_EVENT we generate
'    doesn't take the Node down. Spinning a tiny helper makes
'    each invocation isolated — a separate process with its
'    own clean handler-table state.
'
'  Why CreateNoWindow=True (the flag the Node already passes)
'  is enough for this to work:
'
'    CREATE_NO_WINDOW means "console allocated, window hidden",
'    not "no console". The child has a console, just invisible.
'    AttachConsole(child_pid) attaches us to that hidden
'    console; GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0) fires
'    the event to every process attached to it.
'
'  Usage:
'    GSM.CtrlCSender.exe <pid>
'
'  Exit codes:
'    0  = signal generated successfully
'    1  = AttachConsole failed (target has no console, or PID
'         doesn't exist, or target already detached)
'    2  = SetConsoleCtrlHandler failed
'    3  = GenerateConsoleCtrlEvent failed
'    64 = bad arguments
' ============================================================

Module Program

    ' ---- Win32 P/Invoke ----

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function FreeConsole() As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function AttachConsole(dwProcessId As UInteger) As Boolean
    End Function

    Private Delegate Function ConsoleCtrlHandler(ctrlType As UInteger) As Boolean

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function SetConsoleCtrlHandler(handler As ConsoleCtrlHandler, add As Boolean) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function GenerateConsoleCtrlEvent(ctrlEvent As UInteger, processGroupId As UInteger) As Boolean
    End Function

    Private Const CTRL_C_EVENT As UInteger = 0UI

    ' Module-level field keeps the delegate rooted for the lifetime
    ' of the process so the GC can't move/collect it while the OS
    ' is still holding the function pointer. Local-to-Main scoping
    ' would technically be enough for our 100ms runtime, but a
    ' module-level field is the conventional safe pattern.
    Private ReadOnly _ignoreHandler As ConsoleCtrlHandler =
        Function(ctrlType) True

    Function Main(args As String()) As Integer
        If args Is Nothing OrElse args.Length < 1 Then
            Console.Error.WriteLine("Usage: GSM.CtrlCSender.exe <pid>")
            Return 64
        End If

        Dim pid As UInteger
        If Not UInteger.TryParse(args(0), pid) Then
            Console.Error.WriteLine($"Invalid PID: {args(0)}")
            Return 64
        End If

        ' Detach from any console we might have inherited (no-op
        ' when there is none, which is the usual case when invoked
        ' from a WinExe parent like GSM.Node.exe).
        FreeConsole()

        ' Attach to the target's hidden console. Fails with
        ' ERROR_INVALID_HANDLE (6) if the target has no console,
        ' ERROR_GEN_FAILURE (31) if the target already has its own
        ' console attached and won't share, or
        ' ERROR_ACCESS_DENIED (5) if we're already attached
        ' somewhere else (shouldn't happen after FreeConsole).
        If Not AttachConsole(pid) Then
            Dim err = Marshal.GetLastWin32Error()
            Console.Error.WriteLine($"AttachConsole({pid}) failed: Win32Error={err}")
            Return 1
        End If

        ' Install a no-op handler in THIS process so the
        ' CTRL_C_EVENT we're about to generate doesn't terminate
        ' us. The event is broadcast to every process attached to
        ' the console (target + ourselves); we want only the
        ' target to act on it.
        If Not SetConsoleCtrlHandler(_ignoreHandler, True) Then
            Dim err = Marshal.GetLastWin32Error()
            Console.Error.WriteLine($"SetConsoleCtrlHandler failed: Win32Error={err}")
            FreeConsole()
            Return 2
        End If

        ' processGroupId=0 means "every process attached to this
        ' console". A specific group ID would only be valid if the
        ' target had been spawned with CREATE_NEW_PROCESS_GROUP,
        ' which it wasn't — and that flag would have made
        ' CTRL_C_EVENT specifically be ignored anyway, per MSDN.
        If Not GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0UI) Then
            Dim err = Marshal.GetLastWin32Error()
            Console.Error.WriteLine($"GenerateConsoleCtrlEvent failed: Win32Error={err}")
            SetConsoleCtrlHandler(_ignoreHandler, False)
            FreeConsole()
            Return 3
        End If

        ' Brief pause so the OS can dispatch the event to the
        ' target before we tear down the console attachment.
        ' Without this, on slow / loaded machines the target can
        ' occasionally miss the event entirely.
        Thread.Sleep(100)

        SetConsoleCtrlHandler(_ignoreHandler, False)
        FreeConsole()
        Return 0
    End Function

End Module
