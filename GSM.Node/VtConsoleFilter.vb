' ============================================================
'  GSM.Node — VtConsoleFilter (ConPTY, EventStream Slice 4)
'
'  Stateful byte-level VT/ANSI stripper for pseudoconsole output. A
'  ConPTY game's "stdout" is conhost's rendered VT stream: CSI
'  sequences (colors, cursor movement), OSC sequences (window title,
'  terminated by BEL or ESC \), other short escape forms, CR games
'  (progress lines), and C0 controls. The shim relays it RAW (dumb
'  relay by design — stripping fidelity fixes ride a plain node
'  redeploy, never a shim generation); this filter runs node-side in
'  ShimSession, BEFORE the LineSplitter, so the buffer / EventStore /
'  log viewer see clean text lines.
'
'  Why stateful across chunks: the pty chunks its stream arbitrarily,
'  so an escape sequence can straddle two Stdout frames — a stateless
'  per-chunk regex would leak halves of sequences into the text. One
'  filter instance per stream direction, fed in arrival order.
'
'  Rules:
'    - ESC [ ...  (CSI): dropped through the final byte 0x40–0x7E.
'      Row-moving finals (CUP H/f, CNL E, CPL F, CUD B, CUU A,
'      VPA d) additionally emit ONE LF: conhost renders a new
'      console row by cursor-positioning at least as often as by
'      emitting a literal newline, so silently dropping these
'      sequences fuses consecutive rows into one never-flushed
'      "line" that the LineSplitter holds forever (observed live
'      with Palworld: minutes of output arriving as one giant
'      delayed line). A same-row reposition also emits an LF —
'      that turns an in-place progress redraw into extra lines,
'      the same class of artifact as the CR rule below, and empty
'      lines are dropped downstream.
'      Cursor-forward (CUF, CSI n C) becomes n SPACES: conhost's
'      delta renderer skips UNCHANGED cells with CUF instead of
'      re-sending them, so dropping it silently GLUES words
'      together ("accessed endpoint" -> "accessedendpoint",
'      observed live 2026-08-12) — and a swallowed space inside a
'      player/log line would break parse rules. Skipped cells are
'      overwhelmingly blanks in append-style console output;
'      emitting spaces is the standard pty-scraper heuristic.
'    - ESC ] ...  (OSC): dropped through BEL (0x07) or ESC \ (ST).
'    - ESC + intermediates (0x20–0x2F) + final (0x30–0x7E): dropped
'      (covers ESC ( 0 charset selection etc.).
'    - Any other ESC <byte>: both dropped (two-char forms).
'    - CR and CRLF normalise to a single LF (a bare-CR progress line
'      becomes a line break; empty lines are dropped downstream).
'    - C0 controls other than LF/TAB, and DEL, are dropped.
'    - Everything else — including multi-byte UTF-8 — passes through
'      untouched (the filter never splits inside a UTF-8 char because
'      it only ever REMOVES bytes below 0x80).
'
'  Replay tolerance: the shim ring trims at frame boundaries, so a
'  post-adopt replay can START mid-sequence — the leaked tail of a
'  truncated sequence is printable garbage on one line at worst (same
'  class as today's mid-line ring trim), and the filter re-syncs at
'  the next ESC. The existing SanitizeCapturedLine still runs
'  downstream as a second net.
' ============================================================
Imports System

Namespace GSM.Node

    Friend NotInheritable Class VtConsoleFilter

        Private Enum FilterState
            Normal
            Esc          ' saw ESC, dispatching on the next byte
            EscInter     ' ESC + intermediate bytes (0x20-0x2F), awaiting final
            Csi          ' inside ESC [ ... , awaiting final 0x40-0x7E
            Osc          ' inside ESC ] ... , awaiting BEL or ESC \
            OscEsc       ' inside OSC, saw ESC, awaiting the ST backslash
        End Enum

        Private _state As FilterState = FilterState.Normal
        Private _pendingCr As Boolean
        ''' <summary>Numeric parameter accumulator for the current CSI
        ''' sequence (persists across chunk boundaries like the state).
        ''' Needed for CUF (CSI n C) — see the rules above.</summary>
        Private _csiParam As Integer
        Private _csiHasParam As Boolean
        ''' <summary>True while the last byte this filter EMITTED was an
        ''' LF — used to suppress a CSI-synthesized line break right
        ''' after a real one (CR/LF + cursor reposition is a single
        ''' visual row change, not two). Literal LFs are never
        ''' suppressed.</summary>
        Private _lastOutWasLf As Boolean = True

        ''' <summary>
        ''' Filters one raw chunk, returning only the bytes that survive.
        ''' Never returns Nothing; may return an empty array. Feed chunks in
        ''' arrival order — the sequence state carries across calls.
        ''' </summary>
        Public Function Filter(chunk As Byte()) As Byte()
            If chunk Is Nothing OrElse chunk.Length = 0 Then Return Array.Empty(Of Byte)()

            ' List, not a fixed array: CUF expansion can emit MORE bytes
            ' than arrived (CSI 5 C = 5 spaces from 4 input bytes).
            Dim out As New List(Of Byte)(chunk.Length + 8)

            For Each b As Byte In chunk
                Select Case _state
                    Case FilterState.Normal
                        If _pendingCr Then
                            ' CR seen: CRLF collapses to one LF; bare CR
                            ' becomes an LF of its own (progress-line break).
                            _pendingCr = False
                            out.Add(10)
                            _lastOutWasLf = True
                            If b = 10 Then Continue For   ' the LF of a CRLF — already emitted
                        End If

                        If b = 27 Then                    ' ESC
                            _state = FilterState.Esc
                        ElseIf b = 13 Then                ' CR — defer until we see what follows
                            _pendingCr = True
                        ElseIf b = 10 OrElse b = 9 Then   ' LF / TAB pass
                            out.Add(b)
                            _lastOutWasLf = (b = 10)
                        ElseIf b < 32 OrElse b = 127 Then ' other C0 + DEL drop (incl. stray BEL)
                            ' dropped
                        Else
                            out.Add(b)                    ' printable + UTF-8 continuation bytes
                            _lastOutWasLf = False
                        End If

                    Case FilterState.Esc
                        If b = Asc("["c) Then
                            _state = FilterState.Csi
                            _csiParam = 0
                            _csiHasParam = False
                        ElseIf b = Asc("]"c) Then
                            _state = FilterState.Osc
                        ElseIf b >= 32 AndAlso b <= 47 Then
                            _state = FilterState.EscInter ' ESC ( ... etc.
                        Else
                            _state = FilterState.Normal   ' two-char form; byte consumed
                        End If

                    Case FilterState.EscInter
                        If b >= 48 AndAlso b <= 126 Then
                            _state = FilterState.Normal   ' final byte consumed
                        End If
                        ' intermediates (0x20-0x2F) stay in EscInter

                    Case FilterState.Csi
                        If b >= 48 AndAlso b <= 57 Then           ' digit — accumulate param
                            _csiParam = Math.Min(_csiParam * 10 + (b - 48), 1024)
                            _csiHasParam = True
                        ElseIf b = 59 Then                        ' ';' — next param segment
                            _csiParam = 0
                            _csiHasParam = False
                        End If
                        If b >= 64 AndAlso b <= 126 Then
                            _state = FilterState.Normal   ' final byte of the CSI sequence
                            ' Row-moving finals become a line break (see
                            ' header): CUP 'H'/'f', CNL 'E', CPL 'F',
                            ' CUD 'B', CUU 'A', VPA 'd'. Suppressed when
                            ' the last emitted byte was already an LF
                            ' (CR/LF + reposition = ONE row change), and
                            ' any pending CR folds into this same break.
                            ' CUF 'C' becomes spaces (see header).
                            Select Case b
                                Case Asc("H"c), Asc("f"c), Asc("E"c), Asc("F"c),
                                     Asc("B"c), Asc("A"c), Asc("d"c)
                                    If _pendingCr Then
                                        ' The deferred CR becomes this break.
                                        _pendingCr = False
                                        out.Add(10)
                                        _lastOutWasLf = True
                                    ElseIf Not _lastOutWasLf Then
                                        out.Add(10)
                                        _lastOutWasLf = True
                                    End If
                                    ' _lastOutWasLf already True → no extra break
                                Case Asc("C"c)
                                    ' Cursor forward n cells = n skipped
                                    ' (unchanged) cells — emit as spaces.
                                    If _pendingCr Then
                                        _pendingCr = False
                                        out.Add(10)
                                        _lastOutWasLf = True
                                    End If
                                    Dim cells As Integer = If(_csiHasParam AndAlso _csiParam > 0, _csiParam, 1)
                                    If cells > 512 Then cells = 512
                                    For i As Integer = 1 To cells
                                        out.Add(32)
                                    Next
                                    _lastOutWasLf = False
                            End Select
                        End If
                        ' other params/intermediates consumed silently

                    Case FilterState.Osc
                        If b = 7 Then                     ' BEL terminator
                            _state = FilterState.Normal
                        ElseIf b = 27 Then                ' possible ESC \ (ST)
                            _state = FilterState.OscEsc
                        End If
                        ' payload consumed silently

                    Case FilterState.OscEsc
                        If b = Asc("\"c) Then
                            _state = FilterState.Normal   ' ST complete
                        ElseIf b = Asc("["c) Then
                            _state = FilterState.Csi      ' odd but seen: OSC aborted by a new CSI
                            _csiParam = 0
                            _csiHasParam = False
                        ElseIf b = Asc("]"c) Then
                            _state = FilterState.Osc      ' OSC aborted by a new OSC
                        Else
                            _state = FilterState.Normal   ' treat as a two-char escape; consumed
                        End If
                End Select
            Next

            If out.Count = 0 Then Return Array.Empty(Of Byte)()
            Return out.ToArray()
        End Function

    End Class

End Namespace
