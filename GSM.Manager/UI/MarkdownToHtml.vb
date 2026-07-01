Imports System
Imports System.Collections.Generic
Imports System.Text
Imports System.Text.RegularExpressions

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 5l-1 — minimal Markdown → HTML converter for release
    ''' notes, feeding HtmlRenderer's HtmlPanel. Not a full CommonMark
    ''' implementation; it covers the subset PowerGSM's release bodies
    ''' (sourced from CHANGELOG.md) use:
    '''   • ATX headings (#..######)
    '''   • unordered/ordered lists with indent-based nesting
    '''   • fenced code blocks (```), inline code (`)
    '''   • bold (**/__), italic (*/_)
    '''   • links [text](url)
    '''   • blockquotes (&gt;) and horizontal rules (---)
    ''' Everything is HTML-escaped before markup is applied, so release
    ''' text can't inject tags. Anything unrecognised renders as a
    ''' paragraph. Pairs with a stylesheet supplied by the caller.
    ''' </summary>
    Public Class MarkdownToHtml

        Public Shared Function Convert(markdown As String) As String
            If String.IsNullOrEmpty(markdown) Then Return ""

            Dim text = markdown.Replace(vbCrLf, vbLf).Replace(ChrW(13), ChrW(10))
            Dim lines = text.Split(ChrW(10))
            Dim sb As New StringBuilder()
            Dim para As New List(Of String)()
            Dim i = 0
            Dim n = lines.Length

            While i < n
                Dim line = lines(i)
                Dim trimmed = line.Trim()

                ' Fenced code block.
                If trimmed.StartsWith("```") Then
                    FlushPara(sb, para)
                    i += 1
                    Dim code As New List(Of String)()
                    While i < n AndAlso Not lines(i).Trim().StartsWith("```")
                        code.Add(lines(i))
                        i += 1
                    End While
                    If i < n Then i += 1   ' consume closing fence
                    sb.Append("<pre><code>").Append(HtmlEscape(String.Join(vbLf, code))).Append("</code></pre>")
                    Continue While
                End If

                ' Blank line ends a paragraph.
                If trimmed.Length = 0 Then
                    FlushPara(sb, para)
                    i += 1
                    Continue While
                End If

                ' Horizontal rule (check before lists: --- isn't a bullet).
                If Regex.IsMatch(trimmed, "^([-*_])\1{2,}$") Then
                    FlushPara(sb, para)
                    sb.Append("<hr/>")
                    i += 1
                    Continue While
                End If

                ' Heading.
                Dim hm = Regex.Match(line, "^(#{1,6})\s+(.*)$")
                If hm.Success Then
                    FlushPara(sb, para)
                    Dim lvl = hm.Groups(1).Value.Length
                    sb.Append("<h").Append(lvl).Append(">").
                       Append(InlineToHtml(hm.Groups(2).Value.Trim())).
                       Append("</h").Append(lvl).Append(">")
                    i += 1
                    Continue While
                End If

                ' Blockquote (one or more consecutive > lines).
                If trimmed.StartsWith(">") Then
                    FlushPara(sb, para)
                    Dim quote As New List(Of String)()
                    While i < n AndAlso lines(i).Trim().StartsWith(">")
                        quote.Add(Regex.Replace(lines(i).Trim(), "^>\s?", ""))
                        i += 1
                    End While
                    sb.Append("<blockquote>").Append(InlineToHtml(String.Join(" ", quote))).Append("</blockquote>")
                    Continue While
                End If

                ' List (consumes consecutive items, recursing for nesting).
                If IsListItem(line) Then
                    FlushPara(sb, para)
                    i = AppendList(sb, lines, i)
                    Continue While
                End If

                ' Otherwise: accumulate into the current paragraph.
                para.Add(trimmed)
                i += 1
            End While

            FlushPara(sb, para)
            Return sb.ToString()
        End Function

        Private Shared Sub FlushPara(sb As StringBuilder, para As List(Of String))
            If para.Count = 0 Then Return
            sb.Append("<p>").Append(InlineToHtml(String.Join(" ", para))).Append("</p>")
            para.Clear()
        End Sub

        ''' <summary>
        ''' Emit one list (ul/ol) starting at <paramref name="start"/>,
        ''' recursing for more deeply-indented items. Returns the index
        ''' of the first line not consumed.
        ''' </summary>
        Private Shared Function AppendList(sb As StringBuilder, lines As String(), start As Integer) As Integer
            Dim i = start
            Dim baseIndent = IndentOf(lines(i))
            Dim ordered = IsOrdered(lines(i))
            sb.Append(If(ordered, "<ol>", "<ul>"))

            While i < lines.Length
                Dim line = lines(i)

                ' Skip a blank line only if a same-or-deeper item follows.
                If line.Trim().Length = 0 Then
                    Dim j = i + 1
                    While j < lines.Length AndAlso lines(j).Trim().Length = 0
                        j += 1
                    End While
                    If j < lines.Length AndAlso IsListItem(lines(j)) AndAlso IndentOf(lines(j)) >= baseIndent Then
                        i = j
                        Continue While
                    End If
                    Exit While
                End If

                If Not IsListItem(line) Then Exit While
                Dim ind = IndentOf(line)
                If ind < baseIndent Then Exit While

                If ind > baseIndent Then
                    ' Defensive: a deeper item with no shallower parent.
                    i = AppendList(sb, lines, i)
                    Continue While
                End If

                ' Same-level item. Its text is the marker line plus any
                ' lazy continuation lines (non-blank, non-item, non-block)
                ' that follow — a wrapped bullet continues on the next
                ' physical line(s), and an inline span such as **bold** may
                ' open on the marker line and close on a continuation line,
                ' so the parts must be joined before inline parsing.
                Dim parts As New List(Of String)()
                parts.Add(ListItemContent(line))
                Dim k = i + 1
                While k < lines.Length
                    Dim cont = lines(k)
                    If cont.Trim().Length = 0 Then Exit While
                    If IsListItem(cont) Then Exit While
                    If IsBlockStart(cont) Then Exit While
                    parts.Add(cont.Trim())
                    k += 1
                End While

                sb.Append("<li>").Append(InlineToHtml(String.Join(" ", parts)))
                If k < lines.Length AndAlso IsListItem(lines(k)) AndAlso IndentOf(lines(k)) > baseIndent Then
                    k = AppendList(sb, lines, k)   ' nested list inside this <li>
                End If
                sb.Append("</li>")
                i = k
            End While

            sb.Append(If(ordered, "</ol>", "</ul>"))
            Return i
        End Function

        ''' <summary>Inline spans: code, links, bold, italic. Recursive for nesting.</summary>
        Private Shared Function InlineToHtml(s As String) As String
            Dim sb As New StringBuilder()
            Dim i = 0
            Dim n = s.Length

            While i < n
                Dim c = s(i)

                ' Inline code.
                If c = "`"c Then
                    Dim close = s.IndexOf("`"c, i + 1)
                    If close > i Then
                        sb.Append("<code>").Append(HtmlEscape(s.Substring(i + 1, close - (i + 1)))).Append("</code>")
                        i = close + 1
                        Continue While
                    End If
                End If

                ' Link [text](url).
                If c = "["c Then
                    Dim mid = s.IndexOf("](", i + 1, StringComparison.Ordinal)
                    If mid > 0 Then
                        Dim closeP = s.IndexOf(")"c, mid + 2)
                        If closeP > 0 Then
                            Dim linkText = s.Substring(i + 1, mid - (i + 1))
                            Dim url = s.Substring(mid + 2, closeP - (mid + 2))
                            sb.Append("<a href=""").Append(HtmlAttrEscape(url)).Append(""">").
                               Append(InlineToHtml(linkText)).Append("</a>")
                            i = closeP + 1
                            Continue While
                        End If
                    End If
                End If

                ' Bold (** or __).
                If (c = "*"c AndAlso Peek(s, i + 1) = "*"c) OrElse (c = "_"c AndAlso Peek(s, i + 1) = "_"c) Then
                    Dim marker = s.Substring(i, 2)
                    Dim close = s.IndexOf(marker, i + 2, StringComparison.Ordinal)
                    If close > 0 Then
                        sb.Append("<strong>").Append(InlineToHtml(s.Substring(i + 2, close - (i + 2)))).Append("</strong>")
                        i = close + 2
                        Continue While
                    End If
                End If

                ' Italic (single * or _).
                If c = "*"c OrElse c = "_"c Then
                    Dim close = s.IndexOf(c, i + 1)
                    If close > i Then
                        sb.Append("<em>").Append(InlineToHtml(s.Substring(i + 1, close - (i + 1)))).Append("</em>")
                        i = close + 1
                        Continue While
                    End If
                End If

                sb.Append(HtmlEscapeChar(c))
                i += 1
            End While

            Return sb.ToString()
        End Function

        ' ---- small helpers ----

        Private Shared Function Peek(s As String, idx As Integer) As Char
            Return If(idx >= 0 AndAlso idx < s.Length, s(idx), ChrW(0))
        End Function

        Private Shared Function IndentOf(line As String) As Integer
            Dim k = 0
            Dim col = 0
            While k < line.Length AndAlso (line(k) = " "c OrElse line(k) = ChrW(9))
                col += If(line(k) = ChrW(9), 4, 1)
                k += 1
            End While
            Return col
        End Function

        Private Shared Function IsListItem(line As String) As Boolean
            Return Regex.IsMatch(line, "^\s*([-*+]|\d+\.)\s+\S")
        End Function

        Private Shared Function IsOrdered(line As String) As Boolean
            Return Regex.IsMatch(line, "^\s*\d+\.\s+")
        End Function

        ''' <summary>
        ''' True if the line begins a block construct (fence, blockquote,
        ''' heading, or rule). Used to stop lazy continuation of a list
        ''' item from swallowing the next block.
        ''' </summary>
        Private Shared Function IsBlockStart(line As String) As Boolean
            Dim t = line.Trim()
            If t.StartsWith("```") Then Return True
            If t.StartsWith(">") Then Return True
            If Regex.IsMatch(line, "^(#{1,6})\s+") Then Return True
            If Regex.IsMatch(t, "^([-*_])\1{2,}$") Then Return True
            Return False
        End Function

        Private Shared Function ListItemContent(line As String) As String
            Return Regex.Replace(line, "^\s*([-*+]|\d+\.)\s+", "")
        End Function

        Private Shared Function HtmlEscape(s As String) As String
            Return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        End Function

        Private Shared Function HtmlAttrEscape(s As String) As String
            Return HtmlEscape(s).Replace("""", "&quot;")
        End Function

        Private Shared Function HtmlEscapeChar(c As Char) As String
            Select Case c
                Case "&"c : Return "&amp;"
                Case "<"c : Return "&lt;"
                Case ">"c : Return "&gt;"
                Case Else : Return c.ToString()
            End Select
        End Function

    End Class

End Namespace
