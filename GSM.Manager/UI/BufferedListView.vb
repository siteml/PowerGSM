Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Windows.Forms

' ============================================================
'  BufferedListView — custom owner-drawn list control
'
'  Why this exists (history): the first version of this class
'  inherited from native WinForms ListView and tried to suppress
'  paint quirks via LVS_EX_DOUBLEBUFFER + WM_ERASEBKGND + a
'  ResizeBegin/End WM_SETREDRAW dance. Each layer addressed one
'  paint path but the combination interacted badly inside
'  comctl32 — the final iteration left the control in an endless
'  repaint loop after every window resize.
'
'  The root cause was that native ListView's paint pipeline is
'  largely a black box: column auto-layout, item invalidation
'  on size change, and the WM_PAINT-vs-comctl32 internal redraw
'  loop don't all honour the same flags. Fighting the comctl32
'  paint behaviour from outside is a losing game.
'
'  This version walks away from native ListView entirely.
'  Inherits from System.Windows.Forms.Control and renders the
'  whole control — header bar, rows, gridlines, selection
'  highlight, scroll bar — in a single OnPaint pass against an
'  off-screen double buffer. The only places paint can be
'  triggered are Invalidate calls we make ourselves; the only
'  layout pass is one ApplyLayout method we control. Every
'  paint produces one bitmap and one BitBlt to screen, so
'  there's nothing for the user to perceive as "row-by-row
'  redraw" — the row content either all appears, all changes,
'  or doesn't change at all.
'
'  Public surface kept deliberately small to keep this
'  maintainable; it's not trying to be ListView-compatible.
'  What's here:
'
'    Columns:
'      AddColumn(header, width)            — append a column
'      Columns                              — IReadOnlyList of Column
'
'    Rows:
'      AddRow(cell, cell, ...)              — append a row
'      ClearRows()                          — remove all rows
'      Rows                                 — IReadOnlyList of Row
'
'    Update batching:
'      BeginUpdate() / EndUpdate()          — suppress paint
'                                              between calls;
'                                              one repaint on
'                                              EndUpdate
'
'    Selection (minimal — mouse click only):
'      SelectedIndex                        — get/set, -1 = none
'      SelectedIndexChanged                 — event
'
'    Visual config:
'      HeaderHeight, RowHeight,
'      ShowGridLines                        — properties
'
'  What's NOT here (intentionally):
'    - Keyboard navigation. Read-only display; users don't tab
'      into the player list. Add if a future surface needs it.
'    - Column sort. Player list isn't sortable today.
'    - Column resize / reorder. Fixed widths via AddColumn.
'    - Multi-select. Single-row click selection only.
'    - Owner-draw events. Painting is internal; if you need
'      custom rendering, edit DrawRow.
'    - Tooltips / hover. Skipped to keep paint pipeline simple.
'    - Drag and drop, in-place editing, label editing. N/A.
'
'  If any of those is needed later, add it here rather than
'  bringing back native ListView. The paint stability of this
'  control is the entire value proposition.
' ============================================================

Namespace GSM.Manager.UI

    Public Class BufferedListView
        Inherits Control

        ' ---- Public data types ----

        ''' <summary>
        ''' Column definition. Header text + pixel width. No
        ''' alignment / format options yet — every cell renders
        ''' left-aligned with end-ellipsis truncation. Add
        ''' properties here if a column ever needs different
        ''' behaviour.
        ''' </summary>
        Public Class Column
            Public Header As String
            Public Width As Integer
        End Class

        ''' <summary>
        ''' Row data. Cells is the raw string array, indexed
        ''' positionally against the Columns list. A row with
        ''' fewer cells than columns renders the missing cells
        ''' as blank; extra cells beyond the column count are
        ''' ignored. This is intentional — callers can pass
        ''' whatever they have without worrying about column
        ''' count mismatch.
        '''
        ''' Tag is the same idiom as ListViewItem.Tag — a free
        ''' slot for the caller to associate arbitrary data
        ''' with the row (e.g. the underlying entity the row was
        ''' built from). Untouched by the control itself.
        ''' </summary>
        Public Class Row
            Public ReadOnly Cells As String()
            Public Property Tag As Object
            Public Sub New(cells As String())
                Me.Cells = If(cells, New String() {})
            End Sub
        End Class

        ' ---- Internal state ----

        Private ReadOnly _columns As New List(Of Column)
        Private ReadOnly _rows As New List(Of Row)

        ' Suppress paint / layout while BeginUpdate is in effect.
        ' Nests correctly — counts begin/end calls and only
        ' flushes when the count returns to zero.
        Private _updateLevel As Integer = 0

        Private _selectedIndex As Integer = -1
        Private _scrollOffset As Integer = 0

        Private WithEvents _vScroll As VScrollBar

        ' ---- Public configuration ----

        ''' <summary>
        ''' Height of the header row in pixels. Default 22 — a
        ''' compromise between the slightly-too-tall native
        ''' ListView header and a flat one-line label. Changing
        ''' this at runtime triggers a relayout and repaint.
        ''' </summary>
        Public Property HeaderHeight As Integer = 22

        ''' <summary>
        ''' Per-row height in pixels. Default 20, sized to fit
        ''' Segoe UI 9pt comfortably with a bit of breathing
        ''' room. Larger fonts will need this bumped or text
        ''' clips vertically.
        ''' </summary>
        Public Property RowHeight As Integer = 20

        Public Property ShowGridLines As Boolean = True

        ' ---- Public accessors ----

        Public ReadOnly Property Columns As IReadOnlyList(Of Column)
            Get
                Return _columns
            End Get
        End Property

        Public ReadOnly Property Rows As IReadOnlyList(Of Row)
            Get
                Return _rows
            End Get
        End Property

        ''' <summary>
        ''' Index of the currently-selected row, or -1 if no
        ''' row is selected. Assigning out-of-range values
        ''' clamps to -1. Raises SelectedIndexChanged when the
        ''' value actually changes.
        ''' </summary>
        Public Property SelectedIndex As Integer
            Get
                Return _selectedIndex
            End Get
            Set(value As Integer)
                If value < -1 OrElse value >= _rows.Count Then value = -1
                If value <> _selectedIndex Then
                    _selectedIndex = value
                    Invalidate()
                    RaiseEvent SelectedIndexChanged(Me, EventArgs.Empty)
                End If
            End Set
        End Property

        ''' <summary>
        ''' Currently-selected Row, or Nothing if no row is
        ''' selected. Convenience accessor so callers don't have
        ''' to check SelectedIndex and index into Rows manually.
        ''' </summary>
        Public ReadOnly Property SelectedRow As Row
            Get
                If _selectedIndex < 0 OrElse _selectedIndex >= _rows.Count Then Return Nothing
                Return _rows(_selectedIndex)
            End Get
        End Property

        ''' <summary>
        ''' True when a row is currently selected. Convenience
        ''' over SelectedIndex >= 0 — reads naturally in button-
        ''' enable expressions.
        ''' </summary>
        Public ReadOnly Property HasSelection As Boolean
            Get
                Return _selectedIndex >= 0 AndAlso _selectedIndex < _rows.Count
            End Get
        End Property

        Public Event SelectedIndexChanged(sender As Object, e As EventArgs)

        ' ---- Construction ----

        Public Sub New()
            ' The four flags below are the canonical recipe for
            ' a fully owner-drawn, flicker-free control:
            '
            '   AllPaintingInWmPaint  : don't fire OnPaintBackground
            '                            via WM_ERASEBKGND. We paint
            '                            our own background in OnPaint.
            '   OptimizedDoubleBuffer : route paint through an
            '                            off-screen bitmap and blit
            '                            the result in one operation.
            '   UserPaint             : we, not the OS, handle WM_PAINT.
            '                            Required for any custom OnPaint.
            '   ResizeRedraw          : invalidate the whole control on
            '                            size change so the new size
            '                            gets a fresh paint pass —
            '                            without this, partial regions
            '                            persist with stale content.
            SetStyle(ControlStyles.AllPaintingInWmPaint Or
                     ControlStyles.OptimizedDoubleBuffer Or
                     ControlStyles.UserPaint Or
                     ControlStyles.ResizeRedraw, True)
            UpdateStyles()

            BackColor = SystemColors.Window

            _vScroll = New VScrollBar()
            _vScroll.Dock = DockStyle.Right
            _vScroll.SmallChange = 1
            Controls.Add(_vScroll)
        End Sub

        ' ---- Mutation API ----

        ''' <summary>
        ''' Suspend layout + paint until the matching EndUpdate.
        ''' Nestable: begin/end pairs count. Use around any
        ''' batch of AddRow / ClearRows / column changes to
        ''' avoid intermediate repaints during the batch.
        ''' </summary>
        Public Sub BeginUpdate()
            _updateLevel += 1
        End Sub

        Public Sub EndUpdate()
            _updateLevel = Math.Max(0, _updateLevel - 1)
            If _updateLevel = 0 Then
                ApplyLayout()
                Invalidate()
            End If
        End Sub

        Public Sub AddColumn(header As String, width As Integer)
            _columns.Add(New Column With {.Header = header, .Width = width})
            If _updateLevel = 0 Then Invalidate()
        End Sub

        ''' <summary>
        ''' Append a row with the supplied cell strings. Cells
        ''' array length need not match Columns.Count — extra
        ''' cells are ignored, missing cells render as blank.
        ''' Returns the created Row so the caller can stamp a
        ''' Tag on it (mirrors ListViewItem usage).
        ''' </summary>
        Public Function AddRow(ParamArray cells As String()) As Row
            Dim row = New Row(cells)
            _rows.Add(row)
            If _updateLevel = 0 Then
                ApplyLayout()
                Invalidate()
            End If
            Return row
        End Function

        Public Sub ClearRows()
            _rows.Clear()
            _selectedIndex = -1
            If _updateLevel = 0 Then
                ApplyLayout()
                Invalidate()
            End If
        End Sub

        ''' <summary>
        ''' Remove the row at the given index. Out-of-range
        ''' values are silently ignored. If the removed row was
        ''' the selected row, selection clears; if it was above
        ''' the selected row, selection decrements to track the
        ''' same logical row.
        '''
        ''' Designed for the chat-list trim-to-N use case where
        ''' the oldest row is removed once the buffer overflows.
        ''' Cheap (O(n) on List(Of T) but n is small in practice);
        ''' callers doing bulk removals should wrap in
        ''' BeginUpdate / EndUpdate to suppress per-call layout
        ''' and paint.
        ''' </summary>
        Public Sub RemoveRowAt(index As Integer)
            If index < 0 OrElse index >= _rows.Count Then Return
            _rows.RemoveAt(index)
            If _selectedIndex = index Then
                _selectedIndex = -1
            ElseIf _selectedIndex > index Then
                _selectedIndex -= 1
            End If
            If _updateLevel = 0 Then
                ApplyLayout()
                Invalidate()
            End If
        End Sub

        ' ---- Layout ----

        Protected Overrides Sub OnSizeChanged(e As EventArgs)
            MyBase.OnSizeChanged(e)
            ApplyLayout()
            ' Invalidate is taken care of by the ResizeRedraw
            ' style — the entire client area is repainted as
            ' one operation.
        End Sub

        ''' <summary>
        ''' Recompute scrollbar range and clamp the current
        ''' scroll offset. Called whenever rows are added /
        ''' removed or the control resizes. Cheap — couple of
        ''' integer divisions, no allocations.
        ''' </summary>
        Private Sub ApplyLayout()
            Dim viewport = Math.Max(0, ClientSize.Height - HeaderHeight)
            Dim visibleRowCount = viewport \ Math.Max(1, RowHeight)

            If _rows.Count <= visibleRowCount Then
                ' Everything fits — scrollbar disabled, no
                ' offset. We keep the scrollbar visible-but-
                ' disabled to avoid a width-jump when rows
                ' grow past the visible area; a hidden bar
                ' would cause the content area to widen and
                ' then narrow as rows come and go.
                _vScroll.Enabled = False
                _vScroll.Value = 0
                _scrollOffset = 0
            Else
                _vScroll.Enabled = True
                _vScroll.LargeChange = Math.Max(1, visibleRowCount)
                _vScroll.Maximum = _rows.Count - 1
                Dim maxValue = Math.Max(0, _vScroll.Maximum - _vScroll.LargeChange + 1)
                _vScroll.Value = Math.Min(_vScroll.Value, maxValue)
                _scrollOffset = _vScroll.Value
            End If
        End Sub

        Private Sub _vScroll_ValueChanged(sender As Object, e As EventArgs) _
                Handles _vScroll.ValueChanged
            _scrollOffset = _vScroll.Value
            Invalidate()
        End Sub

        ' ---- Scroll position queries / commands ----

        ''' <summary>
        ''' Index of the first row currently visible at the top
        ''' of the viewport, or 0 if no rows are present. Equal
        ''' to the scrollbar's Value when scrolling is enabled.
        ''' </summary>
        Public ReadOnly Property FirstVisibleRowIndex As Integer
            Get
                Return _scrollOffset
            End Get
        End Property

        ''' <summary>
        ''' Index of the last row currently visible in the
        ''' viewport (the bottom-most row), clamped to the row
        ''' count. Returns -1 when there are no rows. Useful for
        ''' "is the user looking at the tail of the list" checks
        ''' that drive auto-scroll behaviour.
        ''' </summary>
        Public ReadOnly Property LastVisibleRowIndex As Integer
            Get
                If _rows.Count = 0 Then Return -1
                Dim viewport = Math.Max(0, ClientSize.Height - HeaderHeight)
                Dim visibleRowCount = Math.Max(1, viewport \ Math.Max(1, RowHeight))
                Return Math.Min(_rows.Count - 1, _scrollOffset + visibleRowCount - 1)
            End Get
        End Property

        ''' <summary>
        ''' Scroll so that the row at the given index is in the
        ''' visible viewport. If it's already visible, no-op.
        ''' If above the current viewport, scrolls up so the row
        ''' becomes the topmost visible row. If below, scrolls
        ''' down so it becomes the bottom-most visible row.
        ''' Out-of-range indices are silently ignored.
        '''
        ''' Used by the chat list's auto-scroll path to keep the
        ''' newest message in view when the user hasn't scrolled
        ''' away from the tail.
        ''' </summary>
        Public Sub EnsureRowVisible(index As Integer)
            If index < 0 OrElse index >= _rows.Count Then Return
            Dim viewport = Math.Max(0, ClientSize.Height - HeaderHeight)
            Dim visibleRowCount = Math.Max(1, viewport \ Math.Max(1, RowHeight))

            Dim targetOffset As Integer
            If index < _scrollOffset Then
                ' Above the current viewport — anchor it at the top.
                targetOffset = index
            ElseIf index >= _scrollOffset + visibleRowCount Then
                ' Below the current viewport — anchor it at the
                ' bottom (one visible-row-height up from the
                ' viewport edge so we don't half-clip it).
                targetOffset = index - visibleRowCount + 1
            Else
                Return  ' already visible
            End If

            ' Clamp to valid range. When scrolling is disabled
            ' (all rows fit) the scrollbar's Maximum stays at 0,
            ' so the maxOffset below evaluates to 0 and we don't
            ' try to scroll past nowhere.
            Dim maxOffset = Math.Max(0, _rows.Count - visibleRowCount)
            targetOffset = Math.Max(0, Math.Min(maxOffset, targetOffset))

            If _vScroll.Enabled Then
                ' Route through the scrollbar so its Value stays
                ' in sync with our _scrollOffset. The ValueChanged
                ' handler picks up the new value and invalidates.
                If _vScroll.Value <> targetOffset Then
                    _vScroll.Value = targetOffset
                End If
            ElseIf _scrollOffset <> targetOffset Then
                _scrollOffset = targetOffset
                Invalidate()
            End If
        End Sub

        ' ---- Painting ----
        '
        ' Single OnPaint pass. WinForms routes this through the
        ' OptimizedDoubleBuffer pipeline, so every Graphics
        ' operation goes to an off-screen bitmap that's blitted
        ' to the screen in one BitBlt at the end. The user
        ' never sees intermediate paint state.

        Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
            ' AllPaintingInWmPaint suppresses the WM_ERASEBKGND
            ' path, so this only fires from explicit Invalidate
            ' calls. We fill the whole area in one go rather
            ' than letting individual paint methods clear their
            ' rects — fewer paint primitives, less flicker
            ' opportunity.
            e.Graphics.Clear(BackColor)
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            Dim scrollW = If(_vScroll.Enabled, _vScroll.Width, 0)
            Dim contentRight = Math.Max(0, ClientSize.Width - scrollW)
            DrawHeader(g, contentRight)
            DrawRows(g, contentRight)
        End Sub

        Private Sub DrawHeader(g As Graphics, contentRight As Integer)
            If HeaderHeight <= 0 OrElse contentRight <= 0 Then Return

            ' Header background — flat grey matching system
            ' theme. Subtle bottom border separates header from
            ' body without the heavy 3D look native ListView
            ' uses.
            Using brush As New SolidBrush(SystemColors.Control)
                g.FillRectangle(brush, 0, 0, contentRight, HeaderHeight)
            End Using

            Dim x As Integer = 0
            For Each col In _columns
                If x >= contentRight Then Exit For
                Dim columnRight = Math.Min(x + col.Width, contentRight)

                ' Header text. 6px left padding matches the row
                ' cells below so the header column-label visually
                ' aligns with the row content.
                Dim textRect = New Rectangle(x + 6, 0, columnRight - x - 8, HeaderHeight)
                If textRect.Width > 0 Then
                    TextRenderer.DrawText(g, col.Header, Font, textRect,
                                          SystemColors.ControlText,
                                          TextFormatFlags.Left Or
                                          TextFormatFlags.VerticalCenter Or
                                          TextFormatFlags.EndEllipsis)
                End If

                ' Column separator inside the header. Skip the
                ' last column's separator so we don't draw a
                ' line right at the content-right edge.
                If columnRight < contentRight Then
                    Using pen As New Pen(SystemColors.ControlDark)
                        g.DrawLine(pen, columnRight - 1, 2, columnRight - 1, HeaderHeight - 3)
                    End Using
                End If

                x = columnRight
            Next

            ' Header bottom border across the full content width.
            Using pen As New Pen(SystemColors.ControlDark)
                g.DrawLine(pen, 0, HeaderHeight - 1, contentRight, HeaderHeight - 1)
            End Using
        End Sub

        Private Sub DrawRows(g As Graphics, contentRight As Integer)
            Dim viewport = ClientSize.Height - HeaderHeight
            If viewport <= 0 Then Return

            ' +1 visible row to cover partial last row on
            ' non-integer-divisible heights.
            Dim visibleRowCount = viewport \ Math.Max(1, RowHeight) + 1
            Dim startRow = _scrollOffset
            Dim endRow = Math.Min(_rows.Count, startRow + visibleRowCount)

            For i = startRow To endRow - 1
                Dim rowY = HeaderHeight + (i - startRow) * RowHeight
                DrawRow(g, contentRight, i, rowY)
            Next
        End Sub

        Private Sub DrawRow(g As Graphics, contentRight As Integer,
                             rowIndex As Integer, rowY As Integer)
            Dim row = _rows(rowIndex)
            Dim isSelected = (rowIndex = _selectedIndex)

            ' Full-row selection highlight — paint first so cell
            ' text draws on top with the right contrast.
            If isSelected Then
                Using brush As New SolidBrush(SystemColors.Highlight)
                    g.FillRectangle(brush, 0, rowY, contentRight, RowHeight)
                End Using
            End If

            Dim textColor = If(isSelected, SystemColors.HighlightText, SystemColors.WindowText)

            Dim x As Integer = 0
            For c = 0 To _columns.Count - 1
                If x >= contentRight Then Exit For
                Dim col = _columns(c)
                Dim columnRight = Math.Min(x + col.Width, contentRight)

                Dim cellText = If(c < row.Cells.Length, row.Cells(c), "")
                If Not String.IsNullOrEmpty(cellText) Then
                    Dim cellRect = New Rectangle(x + 6, rowY,
                                                  columnRight - x - 8, RowHeight)
                    If cellRect.Width > 0 Then
                        TextRenderer.DrawText(g, cellText, Font, cellRect, textColor,
                                              TextFormatFlags.Left Or
                                              TextFormatFlags.VerticalCenter Or
                                              TextFormatFlags.EndEllipsis)
                    End If
                End If

                ' Vertical gridline between columns. Subtler
                ' than native ListView's heavier line — uses
                ' Control colour (light grey) rather than
                ' ControlDark.
                If ShowGridLines AndAlso columnRight < contentRight Then
                    Using pen As New Pen(SystemColors.Control)
                        g.DrawLine(pen, columnRight - 1, rowY,
                                   columnRight - 1, rowY + RowHeight - 1)
                    End Using
                End If

                x = columnRight
            Next

            ' Horizontal gridline below the row.
            If ShowGridLines Then
                Using pen As New Pen(SystemColors.Control)
                    g.DrawLine(pen, 0, rowY + RowHeight - 1,
                               contentRight, rowY + RowHeight - 1)
                End Using
            End If
        End Sub

        ' ---- Mouse interaction ----

        Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
            MyBase.OnMouseDown(e)
            If e.Button <> MouseButtons.Left Then Return
            If e.Y < HeaderHeight Then Return  ' clicked the header — ignore

            Dim rowInView = (e.Y - HeaderHeight) \ Math.Max(1, RowHeight)
            Dim rowIndex = _scrollOffset + rowInView
            If rowIndex >= 0 AndAlso rowIndex < _rows.Count Then
                SelectedIndex = rowIndex
            Else
                ' Clicked below the last row — clear selection.
                SelectedIndex = -1
            End If
        End Sub

        Protected Overrides Sub OnMouseWheel(e As MouseEventArgs)
            MyBase.OnMouseWheel(e)
            If Not _vScroll.Enabled Then Return

            ' Three rows per wheel notch — the Windows convention
            ' for ListView. e.Delta is 120 per notch on standard
            ' wheels; negative scroll up, positive scroll down.
            Dim delta = -e.Delta \ 120 * 3
            Dim maxValue = Math.Max(0, _vScroll.Maximum - _vScroll.LargeChange + 1)
            Dim newValue = Math.Max(_vScroll.Minimum,
                                     Math.Min(maxValue, _vScroll.Value + delta))
            _vScroll.Value = newValue
        End Sub

        ' ---- Make the control wheel-scrollable without focus ----
        '
        ' By default a Control without focus doesn't receive
        ' WM_MOUSEWHEEL — the wheel events go to whatever has
        ' focus instead. Override so the listview's wheel scroll
        ' works even when the focus is on a button or text box
        ' elsewhere on the panel; users expect to wheel-scroll
        ' over any list they hover over.

        Protected Overrides Sub OnMouseEnter(e As EventArgs)
            MyBase.OnMouseEnter(e)
            If Not Me.Focused AndAlso Me.CanFocus Then
                ' We don't actually want to steal focus from
                ' wherever it currently is (would disrupt
                ' keyboard input), so do nothing here. The
                ' OnMouseWheel above is invoked via the parent
                ' form's bubbling regardless of focus state.
            End If
        End Sub

    End Class

End Namespace
