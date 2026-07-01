Imports System
Imports System.Drawing
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Windows.Forms

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 5l-1 — minimal Markdown → RichTextBox renderer for
    ''' release notes. Not a full CommonMark implementation; it covers
    ''' the subset that PowerGSM's release bodies (sourced from
    ''' CHANGELOG.md) actually use:
    '''   • ATX headings (`#`..`######`)
    '''   • unordered bullets (`-`, `*`, `+`)
    '''   • inline bold (`**x**`, `__x__`)
    '''   • inline code (`` `x` ``)
    '''   • links (`[text](url)` → coloured text, url dropped)
    '''   • horizontal rules (`---`, `***`, `___`)
    ''' Anything else renders as plain text. RichTextBox formatting
    ''' requires a created window handle, so call this after the host
    ''' form's Load, not during control construction.
    ''' </summary>
    Public Class MarkdownRenderer

        Private Const FontName As String = "Segoe UI"
        Private Const CodeFontName As String = "Consolas"
        Private Const BodySize As Single = 9.5F

        Public Shared Sub Render(rtb As RichTextBox, markdown As String)
            rtb.Clear()
            If String.IsNullOrEmpty(markdown) Then Return

            ' Normalise line endings to a single LF.
            Dim text = markdown.Replace(vbCrLf, vbLf).Replace(ChrW(13), ChrW(10))
            Dim lines = text.Split(ChrW(10))

            For Each line In lines
                RenderLine(rtb, line)
            Next

            ' Land at the top with nothing selected.
            rtb.SelectionStart = 0
            rtb.SelectionLength = 0
            Try
                rtb.ScrollToCaret()
            Catch
            End Try
        End Sub

        Private Shared Sub RenderLine(rtb As RichTextBox, line As String)
            Dim trimmed = line.Trim()

            ' Horizontal rule: 3+ of the same -, * or _.
            If Regex.IsMatch(trimmed, "^([-*_])\1{2,}$") Then
                AppendRun(rtb, New String("—"c, 24) & vbLf, MakeFont(BodySize, False), Color.Silver)
                Return
            End If

            ' ATX heading.
            Dim hm = Regex.Match(line, "^(#{1,6})\s+(.*)$")
            If hm.Success Then
                Dim level = hm.Groups(1).Value.Length
                Dim size = If(level <= 1, 13.0F, If(level = 2, 11.5F, 10.5F))
                AppendInline(rtb, hm.Groups(2).Value, size, True)
                AppendRun(rtb, vbLf, MakeFont(size, True), Color.Empty)
                Return
            End If

            ' Unordered bullet (with rough indent preservation).
            Dim bm = Regex.Match(line, "^(\s*)[-*+]\s+(.*)$")
            If bm.Success Then
                Dim indent = Math.Min(bm.Groups(1).Value.Length, 8)
                AppendRun(rtb, New String(" "c, indent) & "•  ", MakeFont(BodySize, False), Color.Empty)
                AppendInline(rtb, bm.Groups(2).Value, BodySize, False)
                AppendRun(rtb, vbLf, MakeFont(BodySize, False), Color.Empty)
                Return
            End If

            ' Blank line.
            If trimmed.Length = 0 Then
                AppendRun(rtb, vbLf, MakeFont(BodySize, False), Color.Empty)
                Return
            End If

            ' Normal paragraph.
            AppendInline(rtb, line.TrimEnd(), BodySize, False)
            AppendRun(rtb, vbLf, MakeFont(BodySize, False), Color.Empty)
        End Sub

        ''' <summary>
        ''' Append a line's worth of inline spans, toggling bold on
        ''' "**"/"__", code on "`", and rendering "[text](url)" as
        ''' coloured text.
        ''' </summary>
        Private Shared Sub AppendInline(rtb As RichTextBox, s As String, size As Single, baseBold As Boolean)
            Dim i = 0
            Dim n = s.Length
            Dim sb As New StringBuilder()
            Dim bold = baseBold
            Dim code = False

            While i < n
                Dim c = s(i)

                ' Inline code toggles on backtick.
                If c = "`"c Then
                    FlushRun(rtb, sb, size, bold, code)
                    code = Not code
                    i += 1
                    Continue While
                End If

                ' Bold toggles on ** or __ (not inside code).
                If Not code AndAlso i + 1 < n AndAlso
                   ((c = "*"c AndAlso s(i + 1) = "*"c) OrElse (c = "_"c AndAlso s(i + 1) = "_"c)) Then
                    FlushRun(rtb, sb, size, bold, code)
                    bold = Not bold
                    i += 2
                    Continue While
                End If

                ' Link [text](url): render the text, drop the url.
                If Not code AndAlso c = "["c Then
                    Dim mid = s.IndexOf("](", i + 1, StringComparison.Ordinal)
                    If mid > 0 Then
                        Dim close = s.IndexOf(")"c, mid + 2)
                        If close > 0 Then
                            Dim linkText = s.Substring(i + 1, mid - (i + 1))
                            FlushRun(rtb, sb, size, bold, code)
                            AppendRun(rtb, linkText, MakeFont(size, bold), Color.FromArgb(0, 102, 170))
                            i = close + 1
                            Continue While
                        End If
                    End If
                End If

                sb.Append(c)
                i += 1
            End While

            FlushRun(rtb, sb, size, bold, code)
        End Sub

        Private Shared Sub FlushRun(rtb As RichTextBox, sb As StringBuilder, size As Single, bold As Boolean, code As Boolean)
            If sb.Length = 0 Then Return
            If code Then
                AppendRun(rtb, sb.ToString(), New Font(CodeFontName, size), Color.FromArgb(150, 40, 40))
            Else
                AppendRun(rtb, sb.ToString(), MakeFont(size, bold), Color.Empty)
            End If
            sb.Clear()
        End Sub

        Private Shared Sub AppendRun(rtb As RichTextBox, runText As String, font As Font, color As Color)
            If String.IsNullOrEmpty(runText) Then Return
            rtb.SelectionStart = rtb.TextLength
            rtb.SelectionLength = 0
            rtb.SelectionFont = font
            rtb.SelectionColor = If(color.IsEmpty, rtb.ForeColor, color)
            rtb.AppendText(runText)
        End Sub

        Private Shared Function MakeFont(size As Single, bold As Boolean) As Font
            Return New Font(FontName, size, If(bold, FontStyle.Bold, FontStyle.Regular))
        End Function

    End Class

End Namespace
