Imports System.Threading

Namespace GSM.Node

    ''' <summary>
    ''' Tiny shared flag that marks the windows during which the node is
    ''' *intentionally* firing a CTRL_C_EVENT at a child process (via the
    ''' GSM.CtrlCSender helper, for a graceful game stop).
    '''
    ''' On some Windows console configurations that GenerateConsoleCtrlEvent
    ''' broadcast bounces back to the node's own console. The node's
    ''' console-control handler (see NodeProgram) swallows CTRL_C_EVENT — i.e.
    ''' reports it "handled, do not terminate" — ONLY while this flag is active,
    ''' so stopping a game can never take the node down. Outside these windows a
    ''' user-typed Ctrl+C falls through to ASP.NET Core's ConsoleLifetime and
    ''' shuts the node down gracefully (which also fires ApplicationStopping →
    ''' the shim-detach hook).
    '''
    ''' Re-entrant via a depth counter: overlapping stops (several instances
    ''' stopped at once) each Push/Pop, and suppression stays active until the
    ''' last one releases.
    ''' </summary>
    Friend Module ConsoleCtrlSuppression

        Private _depth As Integer

        ''' <summary>True while at least one CtrlCSender invocation is in flight.</summary>
        Public ReadOnly Property Active As Boolean
            Get
                Return Volatile.Read(_depth) > 0
            End Get
        End Property

        ''' <summary>Enter a suppression window (balance with <see cref="Pop"/>).</summary>
        Public Sub Push()
            Interlocked.Increment(_depth)
        End Sub

        ''' <summary>Leave a suppression window. Never drops below zero.</summary>
        Public Sub Pop()
            If Interlocked.Decrement(_depth) < 0 Then
                ' Defensive: an unbalanced Pop shouldn't latch suppression off
                ' permanently (which would let a bounce kill the node). Clamp.
                Interlocked.Exchange(_depth, 0)
            End If
        End Sub

    End Module

End Namespace
