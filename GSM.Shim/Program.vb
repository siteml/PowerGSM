' ============================================================
'  GSM.Shim — per-instance supervisor (Phase 8-1)
'
'  A tiny, rarely-updated process the Node launches per game instance.
'  It owns the game's stdin/stdout/stderr and relays them to the Node
'  over a named pipe (Windows) / Unix domain socket (Linux), so a Node
'  restart never severs the game's pipes (the live gap on Linux, where
'  every instance is Strategy A today).
'
'  This file: argument parsing, the endpoint listener, and handoff to
'  the Supervisor (handshake + serve loop). Native spawn, stdout/stderr
'  pumping, and stop live in Supervisor.vb / NativeSpawn.vb (slice 1b).
' ============================================================
Imports System
Imports System.Reflection
Imports System.Threading
Imports System.Threading.Tasks
Imports GSM.Shim.Protocol

Module Program

    Function Main(args As String()) As Integer
        Try
            Return MainAsync(args).GetAwaiter().GetResult()
        Catch ex As Exception
            Console.Error.WriteLine("GSM.Shim fatal: " & ex.ToString())
            Return 1
        End Try
    End Function

    Private Async Function MainAsync(args As String()) As Task(Of Integer)
        Dim opts As ShimArgs = ShimArgs.Parse(args)

        If opts.ShowHelp Then
            PrintUsage()
            Return 0
        End If

        If opts.SelfTest Then
            Return Await SelfTest.RunAsync().ConfigureAwait(False)
        End If

        If String.IsNullOrEmpty(opts.Endpoint) Then
            Console.Error.WriteLine("GSM.Shim: --endpoint is required (e.g. pipe:powergsm-shim-<id> or unix:/path.sock)")
            Return 2
        End If

        Return Await RunSupervisorAsync(opts).ConfigureAwait(False)
    End Function

    ''' <summary>
    ''' Normal mode: listen on the endpoint and run the supervisor accept
    ''' loop. The supervisor serves each Node connection and, on a clean
    ''' drop/detach with the game still alive, loops back to accept the next
    ''' (re)connecting Node — replaying buffered output. It returns only when
    ''' the game exits or a Shutdown is received.
    ''' </summary>
    Private Async Function RunSupervisorAsync(opts As ShimArgs) As Task(Of Integer)
        Using listener As IShimListener = ShimTransport.CreateListener(opts.Endpoint)
            Console.Error.WriteLine($"GSM.Shim: listening on {opts.Endpoint}, instance {opts.InstanceId}")
            Await Supervisor.RunAcceptLoopAsync(listener, ShimVersionInfo.Version, opts.InstanceId, CancellationToken.None).ConfigureAwait(False)
        End Using
        Return 0
    End Function

    Private Sub PrintUsage()
        Console.WriteLine("GSM.Shim — PowerGSM per-instance supervisor")
        Console.WriteLine("Usage:")
        Console.WriteLine("  GSM.Shim --instance-id <id> --endpoint <pipe:name|unix:path>")
        Console.WriteLine("  GSM.Shim --self-test")
        Console.WriteLine($"Protocol version: {ProtocolConstants.ProtocolVersion}   Shim version: {ShimVersionInfo.Version}")
    End Sub

End Module

''' <summary>Parsed command line.</summary>
Friend NotInheritable Class ShimArgs
    Public Property InstanceId As String = ""
    Public Property Endpoint As String = ""
    Public Property SelfTest As Boolean
    Public Property ShowHelp As Boolean

    Public Shared Function Parse(args As String()) As ShimArgs
        Dim r As New ShimArgs()
        If args Is Nothing Then Return r
        Dim i As Integer = 0
        While i < args.Length
            Dim a As String = args(i)
            Select Case a.ToLowerInvariant()
                Case "--instance-id", "-i"
                    i += 1
                    If i < args.Length Then r.InstanceId = args(i)
                Case "--endpoint", "-e"
                    i += 1
                    If i < args.Length Then r.Endpoint = args(i)
                Case "--self-test"
                    r.SelfTest = True
                Case "--help", "-h", "/?"
                    r.ShowHelp = True
                Case Else
                    ' ignore unknown args (forward-compat with future flags)
            End Select
            i += 1
        End While
        Return r
    End Function
End Class

''' <summary>Resolves the shim's own version for the HelloAck / side-by-side scheme.</summary>
Friend Module ShimVersionInfo

    Private ReadOnly _version As String = Resolve()

    Public ReadOnly Property Version As String
        Get
            Return _version
        End Get
    End Property

    Private Function Resolve() As String
        Try
            Dim asm As Assembly = GetType(ShimArgs).Assembly
            Dim info As AssemblyInformationalVersionAttribute =
                asm.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()
            If info IsNot Nothing AndAlso Not String.IsNullOrEmpty(info.InformationalVersion) Then
                Dim v As String = info.InformationalVersion
                Dim plus As Integer = v.IndexOf("+"c)   ' strip "+<git-sha>" build metadata
                If plus >= 0 Then v = v.Substring(0, plus)
                Return v
            End If
            Dim ver As Version = asm.GetName().Version
            If ver IsNot Nothing Then Return ver.ToString()
        Catch
            ' fall through to a safe default
        End Try
        Return "0.0.0"
    End Function

End Module
