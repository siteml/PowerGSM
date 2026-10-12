Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text
Imports Microsoft.Extensions.Logging

' ============================================================
'  OrphanReaper — kill game processes that outlived their
'  managed instance (Palworld incident follow-up #4).
'
'  Context: during the July 2026 incident a game process
'  survived a "Crash loop halted" transition still holding its
'  ports, so neither a manual restart nor the next crash-restart
'  could bind them. The tracked process is already DEAD by the
'  time the crash path runs, so Process.Kill(entireProcessTree)
'  has nothing to walk — the survivors are ex-children (UE4
'  bootstrap chains, cmd-wrapper children whose cmd died, shim
'  leftovers) that Windows re-parented when the tracked pid
'  exited.
'
'  Approach (Windows only; no-op elsewhere):
'    1. Candidates = every live process whose executable image
'       path sits under one of the instance's roots (game exe
'       directory + working directory). Image path is the
'       strong ownership signal: game binaries live inside the
'       install tree, and nothing else legitimately runs from
'       there while the instance is down.
'    2. Protection: any pid currently tracked by ANY managed
'       instance (game pid, shim supervisor, metrics process,
'       the node itself) is excluded — TOGETHER WITH ITS LIVE
'       DESCENDANTS via a toolhelp parent-pid walk. That is
'       what makes reaping safe when two instances share one
'       installation: the sibling's cmd-wrapper CHILD (the
'       actual game exe, not directly tracked) is a descendant
'       of a protected pid and survives.
'    3. _CommonRedist executables are excluded — a concurrent
'       redist pass (InstallRunner) runs installers from inside
'       the install tree and must not be shot mid-flight.
'    4. Port verification: the candidates' bound ports
'       (listening TCP + UDP, v4/v6) are snapshotted BEFORE the
'       kill and re-checked after — the follow-up's "verify
'       ports free" without needing to know the game's port
'       config. Still-bound ports are reported so a stuck port
'       is a visible fact instead of a mystery bind failure on
'       the next start.
'
'  PID-reuse note: candidates are enumerated fresh at reap time
'  and matched by image path, so a recycled pid can only be
'  killed if the NEW process also runs from the instance's own
'  install tree — in which case it is exactly the kind of
'  process the reaper exists to remove.
' ============================================================

Namespace GSM.Node

    ''' <summary>Result of one reap pass, for logging + the
    ''' orphan-reap stream event.</summary>
    Public Class OrphanReapReport
        Public ReadOnly Property KilledPids As New List(Of Integer)
        Public ReadOnly Property SurvivorPids As New List(Of Integer)
        Public ReadOnly Property FreedPorts As New List(Of String)
        Public ReadOnly Property BusyPorts As New List(Of String)
        Public Property CandidateCount As Integer

        Public ReadOnly Property AnyActivity As Boolean
            Get
                Return KilledPids.Count > 0 OrElse SurvivorPids.Count > 0 OrElse BusyPorts.Count > 0
            End Get
        End Property
    End Class

    Public Module OrphanReaper

        ''' <summary>
        ''' Scans for orphaned processes under the given roots,
        ''' kills them (entire tree), and verifies the ports they
        ''' held are free afterwards. Never throws — a reap failure
        ''' must not break crash handling (node autonomy).
        ''' </summary>
        Public Function Reap(roots As IEnumerable(Of String),
                             protectedPids As IEnumerable(Of Integer),
                             logger As ILogger) As OrphanReapReport
            Dim report As New OrphanReapReport()
            Try
                If Not OperatingSystem.IsWindows() Then Return report

                ' ---- Normalize roots ----
                Dim normRoots As New List(Of String)
                If roots IsNot Nothing Then
                    For Each r In roots
                        If String.IsNullOrWhiteSpace(r) Then Continue For
                        Try
                            Dim full = Path.GetFullPath(r).TrimEnd("\"c, "/"c)
                            If full.Length <= 3 Then Continue For ' never a drive root
                            normRoots.Add(full & "\")
                        Catch
                        End Try
                    Next
                End If
                If normRoots.Count = 0 Then Return report

                ' ---- Live process table (pid -> ppid) ----
                Dim parentByPid = SnapshotParentPids()
                If parentByPid.Count = 0 Then Return report

                ' ---- Protected closure: tracked pids + descendants ----
                Dim childrenByPid As New Dictionary(Of Integer, List(Of Integer))
                For Each kvp In parentByPid
                    Dim siblings As List(Of Integer) = Nothing
                    If Not childrenByPid.TryGetValue(kvp.Value, siblings) Then
                        siblings = New List(Of Integer)
                        childrenByPid(kvp.Value) = siblings
                    End If
                    siblings.Add(kvp.Key)
                Next
                Dim closure As New HashSet(Of Integer)
                Dim queue As New Queue(Of Integer)
                closure.Add(Environment.ProcessId)
                queue.Enqueue(Environment.ProcessId)
                If protectedPids IsNot Nothing Then
                    For Each p In protectedPids
                        If p > 0 AndAlso closure.Add(p) Then queue.Enqueue(p)
                    Next
                End If
                While queue.Count > 0
                    Dim cur = queue.Dequeue()
                    Dim kids As List(Of Integer) = Nothing
                    If childrenByPid.TryGetValue(cur, kids) Then
                        For Each kid In kids
                            If closure.Add(kid) Then queue.Enqueue(kid)
                        Next
                    End If
                End While

                ' ---- Candidates: image path under a root ----
                Dim candidates As New List(Of Integer)
                For Each pid In parentByPid.Keys
                    If pid <= 4 OrElse closure.Contains(pid) Then Continue For
                    Dim image = TryGetImagePath(pid)
                    If String.IsNullOrEmpty(image) Then Continue For
                    If image.IndexOf("\_CommonRedist\", StringComparison.OrdinalIgnoreCase) >= 0 Then Continue For
                    For Each root In normRoots
                        If image.StartsWith(root, StringComparison.OrdinalIgnoreCase) Then
                            candidates.Add(pid)
                            Exit For
                        End If
                    Next
                Next
                report.CandidateCount = candidates.Count
                If candidates.Count = 0 Then Return report

                ' ---- Ports the orphans hold, pre-kill ----
                Dim heldPorts As New HashSet(Of String)(StringComparer.Ordinal)
                Try
                    Dim bound = SnapshotBoundPorts()
                    For Each entry In bound
                        If candidates.Contains(entry.Value) Then heldPorts.Add(entry.Key)
                    Next
                Catch ex As Exception
                    logger?.LogDebug(ex, "Orphan reap: port snapshot failed; skipping port verification")
                End Try

                ' ---- Kill ----
                For Each pid In candidates
                    Try
                        Using proc = Process.GetProcessById(pid)
                            Dim image = TryGetImagePath(pid)
                            logger?.LogWarning(
                                "Orphan reap: killing pid {Pid} ({Image})", pid, image)
                            proc.Kill(entireProcessTree:=True)
                            If proc.WaitForExit(5000) Then
                                report.KilledPids.Add(pid)
                            Else
                                report.SurvivorPids.Add(pid)
                            End If
                        End Using
                    Catch ex As ArgumentException
                        ' Already gone between snapshot and kill.
                        report.KilledPids.Add(pid)
                    Catch ex As Exception
                        logger?.LogWarning(ex, "Orphan reap: failed to kill pid {Pid}", pid)
                        report.SurvivorPids.Add(pid)
                    End Try
                Next

                ' ---- Verify the freed ports really freed ----
                If heldPorts.Count > 0 Then
                    Try
                        Dim after = SnapshotBoundPorts()
                        For Each port In heldPorts
                            If after.ContainsKey(port) Then
                                report.BusyPorts.Add(port)
                            Else
                                report.FreedPorts.Add(port)
                            End If
                        Next
                    Catch ex As Exception
                        logger?.LogDebug(ex, "Orphan reap: post-kill port snapshot failed")
                    End Try
                End If
            Catch ex As Exception
                logger?.LogWarning(ex, "Orphan reap pass failed")
            End Try
            Return report
        End Function

        ' --------------------------------------------------------
        '  Toolhelp snapshot: pid -> parent pid
        ' --------------------------------------------------------

        Private Const TH32CS_SNAPPROCESS As UInteger = &H2UI

        <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
        Private Structure PROCESSENTRY32W
            Public dwSize As UInteger
            Public cntUsage As UInteger
            Public th32ProcessID As UInteger
            Public th32DefaultHeapID As UIntPtr
            Public th32ModuleID As UInteger
            Public cntThreads As UInteger
            Public th32ParentProcessID As UInteger
            Public pcPriClassBase As Integer
            Public dwFlags As UInteger
            <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=260)>
            Public szExeFile As String
        End Structure

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Function CreateToolhelp32Snapshot(dwFlags As UInteger, th32ProcessID As UInteger) As IntPtr
        End Function

        <DllImport("kernel32.dll", SetLastError:=True, CharSet:=CharSet.Unicode, EntryPoint:="Process32FirstW")>
        Private Function Process32FirstW(hSnapshot As IntPtr, ByRef lppe As PROCESSENTRY32W) As Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True, CharSet:=CharSet.Unicode, EntryPoint:="Process32NextW")>
        Private Function Process32NextW(hSnapshot As IntPtr, ByRef lppe As PROCESSENTRY32W) As Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Function CloseHandle(hObject As IntPtr) As Boolean
        End Function

        Private Function SnapshotParentPids() As Dictionary(Of Integer, Integer)
            Dim map As New Dictionary(Of Integer, Integer)
            Dim snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0UI)
            If snap = IntPtr.Zero OrElse snap = New IntPtr(-1) Then Return map
            Try
                Dim entry As New PROCESSENTRY32W With {
                    .dwSize = CUInt(Marshal.SizeOf(Of PROCESSENTRY32W)())
                }
                If Process32FirstW(snap, entry) Then
                    Do
                        map(CInt(entry.th32ProcessID)) = CInt(entry.th32ParentProcessID)
                    Loop While Process32NextW(snap, entry)
                End If
            Finally
                CloseHandle(snap)
            End Try
            Return map
        End Function

        ' --------------------------------------------------------
        '  Image path (QueryFullProcessImageName — works with a
        '  limited-information handle, unlike Process.MainModule)
        ' --------------------------------------------------------

        Private Const PROCESS_QUERY_LIMITED_INFORMATION As UInteger = &H1000UI

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Function OpenProcess(dwDesiredAccess As UInteger,
                                     bInheritHandle As Boolean,
                                     dwProcessId As UInteger) As IntPtr
        End Function

        <DllImport("kernel32.dll", SetLastError:=True, CharSet:=CharSet.Unicode, EntryPoint:="QueryFullProcessImageNameW")>
        Private Function QueryFullProcessImageNameW(hProcess As IntPtr,
                                                    dwFlags As UInteger,
                                                    lpExeName As StringBuilder,
                                                    ByRef lpdwSize As UInteger) As Boolean
        End Function

        Private Function TryGetImagePath(pid As Integer) As String
            Dim handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, CUInt(pid))
            If handle = IntPtr.Zero Then Return Nothing
            Try
                Dim sb As New StringBuilder(1024)
                Dim size As UInteger = CUInt(sb.Capacity)
                If QueryFullProcessImageNameW(handle, 0UI, sb, size) Then
                    Return sb.ToString()
                End If
                Return Nothing
            Finally
                CloseHandle(handle)
            End Try
        End Function

        ' --------------------------------------------------------
        '  Bound ports (listening TCP + UDP, v4/v6) -> owning pid
        '  via GetExtendedTcpTable / GetExtendedUdpTable. Keys are
        '  "tcp:7777" / "udp:8211" — protocol + host-order port.
        ' --------------------------------------------------------

        Private Const AF_INET As Integer = 2
        Private Const AF_INET6 As Integer = 23
        Private Const TCP_TABLE_OWNER_PID_ALL As Integer = 5
        Private Const UDP_TABLE_OWNER_PID As Integer = 1
        Private Const MIB_TCP_STATE_LISTEN As Integer = 2
        Private Const ERROR_INSUFFICIENT_BUFFER As Integer = 122

        <DllImport("iphlpapi.dll", SetLastError:=True)>
        Private Function GetExtendedTcpTable(pTcpTable As IntPtr,
                                             ByRef pdwSize As Integer,
                                             bOrder As Boolean,
                                             ulAf As Integer,
                                             tableClass As Integer,
                                             reserved As Integer) As Integer
        End Function

        <DllImport("iphlpapi.dll", SetLastError:=True)>
        Private Function GetExtendedUdpTable(pUdpTable As IntPtr,
                                             ByRef pdwSize As Integer,
                                             bOrder As Boolean,
                                             ulAf As Integer,
                                             tableClass As Integer,
                                             reserved As Integer) As Integer
        End Function

        Private Function NtohsPort(rawDword As Integer) As Integer
            ' The port field carries the 16-bit network-order port
            ' in the low bytes of a DWORD.
            Return ((rawDword And &HFF) << 8) Or ((rawDword >> 8) And &HFF)
        End Function

        ''' <summary>
        ''' One row-walk over a native table: reads dwNumEntries,
        ''' then per-row (port, pid, optional state) at the given
        ''' byte offsets. Row layouts are fixed 4-byte-aligned
        ''' structures documented with the iphlpapi tables.
        ''' </summary>
        Private Sub WalkTable(map As Dictionary(Of String, Integer),
                              proto As String,
                              fetch As Func(Of IntPtr, Integer, (Size As Integer, Result As Integer)),
                              rowSize As Integer,
                              portOffset As Integer,
                              pidOffset As Integer,
                              stateOffset As Integer)
            Dim size As Integer = 0
            Dim probe = fetch(IntPtr.Zero, size)
            size = probe.Size
            If probe.Result <> ERROR_INSUFFICIENT_BUFFER OrElse size <= 4 Then Return
            Dim buffer = Marshal.AllocHGlobal(size)
            Try
                Dim res = fetch(buffer, size)
                If res.Result <> 0 Then Return
                Dim count = Marshal.ReadInt32(buffer)
                Dim rowBase = buffer + 4
                For i = 0 To count - 1
                    Dim row = rowBase + i * rowSize
                    If stateOffset >= 0 Then
                        Dim state = Marshal.ReadInt32(row, stateOffset)
                        If state <> MIB_TCP_STATE_LISTEN Then Continue For
                    End If
                    Dim port = NtohsPort(Marshal.ReadInt32(row, portOffset))
                    Dim pid = Marshal.ReadInt32(row, pidOffset)
                    If port > 0 AndAlso pid > 0 Then
                        map($"{proto}:{port}") = pid
                    End If
                Next
            Finally
                Marshal.FreeHGlobal(buffer)
            End Try
        End Sub

        ''' <summary>
        ''' Snapshot of bound ports: "tcp:port"/"udp:port" -> pid.
        ''' TCP restricted to LISTEN rows so TIME_WAIT ghosts of
        ''' just-killed processes don't read as "port busy".
        ''' </summary>
        Private Function SnapshotBoundPorts() As Dictionary(Of String, Integer)
            Dim map As New Dictionary(Of String, Integer)(StringComparer.Ordinal)

            ' TCP v4: dwState@0, dwLocalPort@8, dwOwningPid@20; row 24.
            WalkTable(map, "tcp",
                      Function(buf, sz)
                          Dim s = sz
                          Dim r = GetExtendedTcpTable(buf, s, False, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0)
                          Return (s, r)
                      End Function,
                      24, 8, 20, 0)

            ' TCP v6: localPort@20, state@48, pid@52; row 56.
            WalkTable(map, "tcp",
                      Function(buf, sz)
                          Dim s = sz
                          Dim r = GetExtendedTcpTable(buf, s, False, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0)
                          Return (s, r)
                      End Function,
                      56, 20, 52, 48)

            ' UDP v4: localPort@4, pid@8; row 12 (no state).
            WalkTable(map, "udp",
                      Function(buf, sz)
                          Dim s = sz
                          Dim r = GetExtendedUdpTable(buf, s, False, AF_INET, UDP_TABLE_OWNER_PID, 0)
                          Return (s, r)
                      End Function,
                      12, 4, 8, -1)

            ' UDP v6: localPort@20, pid@24; row 28 (no state).
            WalkTable(map, "udp",
                      Function(buf, sz)
                          Dim s = sz
                          Dim r = GetExtendedUdpTable(buf, s, False, AF_INET6, UDP_TABLE_OWNER_PID, 0)
                          Return (s, r)
                      End Function,
                      28, 20, 24, -1)

            Return map
        End Function

    End Module

End Namespace
