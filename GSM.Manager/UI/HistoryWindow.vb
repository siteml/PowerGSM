Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Drawing
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager.Core

' ============================================================
'  HistoryWindow — non-modal history search window.
'
'  Launched from MainForm (Tools menu) with no pre-fill, or
'  from an InstancePanel with session + recent-time pre-fill.
'
'  Single window, two display modes:
'    Range mode (both start + end set): timeline ListView
'      shows chat/join/leave rows merged chronologically.
'    Snapshot mode (end unchecked): the same ListView shows
'      the player list present at StartUtc, with last-chat
'      context.
'  The mode toggle is the "Enable end time" checkbox.
'
'  Time handling: by default the UI operates in LOCAL time.
'  The "Use UTC" checkbox flips both the pickers AND the
'  result display to UTC. Internally all queries and cached
'  data remain in UTC; the mode only affects presentation
'  and input interpretation. This avoids the mismatch where
'  the input looks like "Apr 22 00:00" and the results look
'  like "Apr 21 21:20" because of a silent timezone offset.
' ============================================================

Namespace GSM.Manager.UI

    Public Class HistoryWindow
        Inherits Form

        ' ---- Services ----
        Private ReadOnly _service As HistoryQueryService

        ' ---- Filter controls ----
        Private _sessionCombo As ComboBox
        Private _playerCombo As ComboBox
        Private _chatText As TextBox
        Private _startLabel As Label
        Private _startPicker As DateTimePicker
        Private _endPicker As DateTimePicker
        Private _endEnabledCheck As CheckBox
        Private _utcCheck As CheckBox
        Private _chatCheck As CheckBox
        Private _joinCheck As CheckBox
        Private _leaveCheck As CheckBox
        Private _searchButton As Button
        Private _cancelButton As Button

        ' ---- Results ----
        Private _resultsList As ListView
        Private _statusStrip As StatusStrip
        Private _statusLabel As ToolStripStatusLabel
        Private _progressBar As ToolStripProgressBar

        ' Phase 5h-6 — right-click context menu on result rows.
        ' Two items: copy the row's InstanceId, or copy the raw
        ' SessionIdentity. Both source from the ListViewItem's
        ' Tag (set to the underlying TimelineRow / SnapshotRow
        ' during render).
        Private _rowContextMenu As ContextMenuStrip
        Private _copyInstanceIdItem As ToolStripMenuItem
        Private _copySessionIdentityItem As ToolStripMenuItem

        ' ---- Query lifecycle ----
        Private _queryCts As CancellationTokenSource
        Private _currentMode As DisplayMode = DisplayMode.Timeline

        ' ---- Cached metadata for combos ----
        Private _knownSessions As IReadOnlyList(Of SessionSummary)

        ' ---- Cached last results so we can re-render on
        ' ---- time-mode toggle without re-querying the DB.
        Private _lastTimelineResult As TimelineResult
        Private _lastSnapshotRows As IReadOnlyList(Of SnapshotRow)
        Private _lastSnapshotInstantUtc As DateTime

        Private Enum DisplayMode
            Timeline
            Snapshot
        End Enum

        ''' <summary>
        ''' Launch with optional pre-filled filter. Pass Nothing for
        ''' a fully unfiltered open (Tools menu path). Pass a filter
        ''' object with SessionIdentity set and a recent time range
        ''' for instance-panel launches.
        ''' </summary>
        Public Sub New(initialFilter As HistoryFilter)
            FormIconHelper.ApplyTo(Me)
            _service = ManagerProgram.Services.GetRequiredService(Of HistoryQueryService)()
            InitializeControls()
            ApplyInitialFilter(initialFilter)
            AddHandler Me.Load, AddressOf OnLoaded
        End Sub

        ' ============================================================
        '  Layout
        ' ============================================================

        Private Sub InitializeControls()
            Me.Text = "History Search"
            Me.Size = New Size(1100, 720)
            Me.StartPosition = FormStartPosition.CenterScreen
            Me.MinimumSize = New Size(900, 500)

            ' ---- Filter panel (top) ----
            Dim filterPanel As New Panel() With {
                .Dock = DockStyle.Top,
                .Height = 180,
                .Padding = New Padding(10)
            }

            Dim row = 10

            ' Session row
            Dim sessionLbl As New Label() With {
                .Text = "Tile / Session:",
                .AutoSize = True,
                .Location = New Point(10, row + 4)
            }
            _sessionCombo = New ComboBox() With {
                .Location = New Point(130, row),
                .Width = 400,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            AddHandler _sessionCombo.SelectedIndexChanged, AddressOf OnSessionChanged
            filterPanel.Controls.Add(sessionLbl)
            filterPanel.Controls.Add(_sessionCombo)

            ' Player row — editable combo so user can type a partial
            Dim playerLbl As New Label() With {
                .Text = "Player (partial):",
                .AutoSize = True,
                .Location = New Point(560, row + 4)
            }
            _playerCombo = New ComboBox() With {
                .Location = New Point(680, row),
                .Width = 250,
                .DropDownStyle = ComboBoxStyle.DropDown
            }
            filterPanel.Controls.Add(playerLbl)
            filterPanel.Controls.Add(_playerCombo)

            row += 35

            ' Start time. Label text is updated by UpdateTimeLabels()
            ' based on the "Use UTC" checkbox state.
            _startLabel = New Label() With {
                .Text = "Start:",
                .AutoSize = True,
                .Location = New Point(10, row + 4)
            }
            _startPicker = New DateTimePicker() With {
                .Location = New Point(130, row),
                .Width = 200,
                .Format = DateTimePickerFormat.Custom,
                .CustomFormat = "yyyy-MM-dd HH:mm:ss",
                .Value = DateTime.Now.AddDays(-1)
            }
            filterPanel.Controls.Add(_startLabel)
            filterPanel.Controls.Add(_startPicker)

            ' End-enabled checkbox + end time. Same label-update story.
            _endEnabledCheck = New CheckBox() With {
                .Text = "End:",
                .AutoSize = True,
                .Location = New Point(360, row + 4),
                .Checked = True
            }
            AddHandler _endEnabledCheck.CheckedChanged, AddressOf OnEndEnabledChanged
            _endPicker = New DateTimePicker() With {
                .Location = New Point(470, row),
                .Width = 200,
                .Format = DateTimePickerFormat.Custom,
                .CustomFormat = "yyyy-MM-dd HH:mm:ss",
                .Value = DateTime.Now
            }
            filterPanel.Controls.Add(_endEnabledCheck)
            filterPanel.Controls.Add(_endPicker)

            Dim snapshotHint As New Label() With {
                .Text = "(uncheck End for snapshot at Start time)",
                .AutoSize = True,
                .ForeColor = Color.Gray,
                .Location = New Point(690, row + 4)
            }
            filterPanel.Controls.Add(snapshotHint)

            row += 35

            ' Chat text
            Dim chatLbl As New Label() With {
                .Text = "Chat contains:",
                .AutoSize = True,
                .Location = New Point(10, row + 4)
            }
            _chatText = New TextBox() With {
                .Location = New Point(130, row),
                .Width = 540
            }
            filterPanel.Controls.Add(chatLbl)
            filterPanel.Controls.Add(_chatText)

            row += 35

            ' Event-kind checkboxes + Use UTC + buttons
            Dim kindLbl As New Label() With {
                .Text = "Include:",
                .AutoSize = True,
                .Location = New Point(10, row + 4)
            }
            _chatCheck = New CheckBox() With {
                .Text = "Chat",
                .AutoSize = True,
                .Location = New Point(130, row + 2),
                .Checked = True
            }
            _joinCheck = New CheckBox() With {
                .Text = "Joins",
                .AutoSize = True,
                .Location = New Point(200, row + 2),
                .Checked = True
            }
            _leaveCheck = New CheckBox() With {
                .Text = "Leaves",
                .AutoSize = True,
                .Location = New Point(270, row + 2),
                .Checked = True
            }

            ' "Use UTC" — off by default (show local time in pickers
            ' and results). Toggle converts the picker values to the
            ' other mode and re-renders cached results live, so the
            ' user can flip between views without re-querying.
            _utcCheck = New CheckBox() With {
                .Text = "Use UTC",
                .AutoSize = True,
                .Location = New Point(400, row + 2),
                .Checked = False
            }
            AddHandler _utcCheck.CheckedChanged, AddressOf OnUtcModeChanged

            filterPanel.Controls.Add(kindLbl)
            filterPanel.Controls.Add(_chatCheck)
            filterPanel.Controls.Add(_joinCheck)
            filterPanel.Controls.Add(_leaveCheck)
            filterPanel.Controls.Add(_utcCheck)

            ' Search / Cancel
            _searchButton = New Button() With {
                .Text = "Search",
                .Location = New Point(900, row - 2),
                .Width = 80,
                .Height = 26
            }
            AddHandler _searchButton.Click, AddressOf OnSearchClicked
            _cancelButton = New Button() With {
                .Text = "Cancel",
                .Location = New Point(990, row - 2),
                .Width = 80,
                .Height = 26,
                .Enabled = False
            }
            AddHandler _cancelButton.Click, AddressOf OnCancelClicked
            filterPanel.Controls.Add(_searchButton)
            filterPanel.Controls.Add(_cancelButton)

            Me.Controls.Add(filterPanel)

            ' ---- Status strip (bottom) ----
            _statusStrip = New StatusStrip()
            _statusLabel = New ToolStripStatusLabel() With {
                .Text = "Ready.",
                .Spring = True,
                .TextAlign = ContentAlignment.MiddleLeft
            }
            _progressBar = New ToolStripProgressBar() With {
                .Style = ProgressBarStyle.Marquee,
                .Visible = False,
                .Width = 120
            }
            _statusStrip.Items.Add(_statusLabel)
            _statusStrip.Items.Add(_progressBar)
            Me.Controls.Add(_statusStrip)

            ' ---- Results list (fills middle) ----
            _resultsList = New ListView() With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .GridLines = True,
                .Font = New Font("Segoe UI", 9),
                .ShowItemToolTips = True
            }
            BuildTimelineColumns()

            ' Phase 5h-6 — right-click context menu. Built once,
            ' assigned to the ListView; the Opening handler
            ' enables/disables items based on selection so the
            ' user can't pick "Copy…" when nothing is selected.
            _rowContextMenu = New ContextMenuStrip()
            _copyInstanceIdItem = New ToolStripMenuItem("Copy &instance ID")
            AddHandler _copyInstanceIdItem.Click, AddressOf OnCopyInstanceId
            _rowContextMenu.Items.Add(_copyInstanceIdItem)
            _copySessionIdentityItem = New ToolStripMenuItem("Copy &session identity")
            AddHandler _copySessionIdentityItem.Click, AddressOf OnCopySessionIdentity
            _rowContextMenu.Items.Add(_copySessionIdentityItem)
            AddHandler _rowContextMenu.Opening, AddressOf OnRowContextMenuOpening
            _resultsList.ContextMenuStrip = _rowContextMenu

            Me.Controls.Add(_resultsList)
            _resultsList.BringToFront()

            UpdateTimeLabels()
        End Sub

        Private Sub BuildTimelineColumns()
            _resultsList.Columns.Clear()
            ' Time content tops out at "yyyy-MM-dd HH:mm:ss" —
            ' 19 fixed-width characters — so 130px is plenty
            ' even with the column-header sort caret.
            _resultsList.Columns.Add("Time", 130)
            ' Kind values are always one of {Join, Leave, Chat}.
            ' "Leave" is the widest; ~55px holds it with header
            ' padding to spare.
            _resultsList.Columns.Add("Kind", 55)
            ' Phase 5h-6 — Source column replaces the old
            ' "Tile / Session" and "Instance" columns. The label
            ' is plugin-formatted via ISourceLabelProvider; for
            ' LO it reads "{TileName} — {RealmName} —
            ' {Node}/{Install}" so the operator gets all the
            ' "where did this come from" context in one cell.
            ' Plugins that don't opt in get a default
            ' "{Node}/{Install}/{Instance}" label.
            _resultsList.Columns.Add("Source", 480)
            ' Character + Player split. Character holds the
            ' in-game character display name; Player holds the
            ' platform persona (Steam handle, FLS handle, etc.).
            ' Two columns rather than one coalesced "Player" so
            ' the operator can trace either identity
            ' independently — even when one is missing on the
            ' row, the other still shows. Matches the
            ' InstancePanel's Character + Platform name layout
            ' for consistency across surfaces.
            _resultsList.Columns.Add("Character", 140)
            _resultsList.Columns.Add("Player", 120)
            ' Message gets the extra space the trimmed Time and
            ' Kind columns gave back — long chat lines tended
            ' to clip with the old 400px allocation.
            _resultsList.Columns.Add("Message", 445)
        End Sub

        Private Sub BuildSnapshotColumns()
            _resultsList.Columns.Clear()
            ' Same Character + Player split as the timeline
            ' columns — see BuildTimelineColumns for the
            ' rationale.
            _resultsList.Columns.Add("Character", 160)
            _resultsList.Columns.Add("Player", 140)
            _resultsList.Columns.Add("Joined", 160)
            ' Phase 5h-6 — same plugin-formatted Source label as
            ' the timeline view (see BuildTimelineColumns).
            _resultsList.Columns.Add("Source", 360)
            _resultsList.Columns.Add("Last chat", 360)
            _resultsList.Columns.Add("Last chat time", 160)
        End Sub

        ''' <summary>
        ''' Update the "Start / End" labels to reflect the current
        ''' time mode. Called on init and whenever the UTC checkbox
        ''' is toggled.
        ''' </summary>
        Private Sub UpdateTimeLabels()
            If _utcCheck IsNot Nothing AndAlso _utcCheck.Checked Then
                _startLabel.Text = "Start (UTC):"
                _endEnabledCheck.Text = "End (UTC):"
            Else
                _startLabel.Text = "Start (local):"
                _endEnabledCheck.Text = "End (local):"
            End If
        End Sub

        ' ============================================================
        '  Initial state + load
        ' ============================================================

        Private Sub ApplyInitialFilter(filter As HistoryFilter)
            If filter Is Nothing Then Return

            ' Time range — convert UTC inputs to the currently-active
            ' display mode (default local). The internal HistoryFilter
            ' always carries UTC; the picker shows whatever mode the
            ' user is looking at.
            Dim showAsUtc = _utcCheck IsNot Nothing AndAlso _utcCheck.Checked
            If filter.StartUtc > DateTime.MinValue Then
                _startPicker.Value = If(showAsUtc,
                                         filter.StartUtc,
                                         filter.StartUtc.ToLocalTime())
            End If
            If filter.EndUtc.HasValue Then
                _endEnabledCheck.Checked = True
                _endPicker.Value = If(showAsUtc,
                                       filter.EndUtc.Value,
                                       filter.EndUtc.Value.ToLocalTime())
            End If
            _endPicker.Enabled = _endEnabledCheck.Checked

            ' Event-kind checkboxes
            _chatCheck.Checked = filter.IncludeChat
            _joinCheck.Checked = filter.IncludeJoins
            _leaveCheck.Checked = filter.IncludeLeaves

            ' Player / chat patterns
            If Not String.IsNullOrEmpty(filter.PlayerNamePattern) Then
                _playerCombo.Text = filter.PlayerNamePattern
            End If
            If Not String.IsNullOrEmpty(filter.ChatTextPattern) Then
                _chatText.Text = filter.ChatTextPattern
            End If

            ' Session combo can't be set until OnLoaded populates it;
            ' stash the preferred identity and apply after load.
            Me.Tag = filter.SessionIdentity
        End Sub

        Private Async Sub OnLoaded(sender As Object, e As EventArgs)
            Try
                Await LoadSessionsAsync()
                Await LoadPlayerNamesAsync(SelectedSessionIdentity())

                ' Apply any stashed initial session selection.
                Dim preferred = TryCast(Me.Tag, String)
                If Not String.IsNullOrEmpty(preferred) Then
                    For i = 0 To _sessionCombo.Items.Count - 1
                        Dim item = TryCast(_sessionCombo.Items(i), SessionComboItem)
                        If item IsNot Nothing AndAlso item.Identity = preferred Then
                            _sessionCombo.SelectedIndex = i
                            Exit For
                        End If
                    Next
                End If

                ' If we were launched with a filter, run the search
                ' immediately for QoL — user clicked "history on this
                ' instance", they want to see results now.
                If Me.Tag IsNot Nothing Then
                    Me.Tag = Nothing
                    OnSearchClicked(Nothing, EventArgs.Empty)
                End If
            Catch ex As Exception
                _statusLabel.Text = $"Failed to load filters: {ex.Message}"
            End Try
        End Sub

        Private Async Function LoadSessionsAsync() As Task
            _knownSessions = Await _service.GetKnownSessionsAsync()
            _sessionCombo.Items.Clear()
            _sessionCombo.Items.Add(New SessionComboItem With {
                .Identity = Nothing,
                .DisplayLabel = "(all sessions)"
            })
            For Each s In _knownSessions
                _sessionCombo.Items.Add(New SessionComboItem With {
                    .Identity = s.Identity,
                    .DisplayLabel = s.DisplayLabel
                })
            Next
            _sessionCombo.SelectedIndex = 0
        End Function

        Private Async Function LoadPlayerNamesAsync(sessionIdentity As String) As Task
            Dim names = Await _service.GetKnownPlayerNamesAsync(sessionIdentity)
            Dim currentText = _playerCombo.Text
            _playerCombo.Items.Clear()
            For Each n In names
                _playerCombo.Items.Add(n)
            Next
            ' Preserve free-text input if the user had typed something.
            _playerCombo.Text = currentText
        End Function

        Private Function SelectedSessionIdentity() As String
            Dim item = TryCast(_sessionCombo.SelectedItem, SessionComboItem)
            If item Is Nothing Then Return Nothing
            Return item.Identity
        End Function

        ' ============================================================
        '  Event handlers
        ' ============================================================

        Private Async Sub OnSessionChanged(sender As Object, e As EventArgs)
            ' Refresh player dropdown scoped to selected session.
            Try
                Await LoadPlayerNamesAsync(SelectedSessionIdentity())
            Catch
                ' Non-fatal.
            End Try
        End Sub

        Private Sub OnEndEnabledChanged(sender As Object, e As EventArgs)
            _endPicker.Enabled = _endEnabledCheck.Checked
        End Sub

        ''' <summary>
        ''' When the user toggles "Use UTC", convert both picker
        ''' values to the new mode (preserving the same instant in
        ''' time) and re-render any current results. No re-query
        ''' needed — the cached UTC data is replayed through the
        ''' new display mode.
        ''' </summary>
        Private Sub OnUtcModeChanged(sender As Object, e As EventArgs)
            ' Rebase picker values. Checkbox has ALREADY toggled, so
            ' _utcCheck.Checked holds the NEW state; the picker values
            ' reflect the OLD mode until we convert them.
            Try
                If _utcCheck.Checked Then
                    ' Was local, now UTC — treat picker values as local
                    ' and convert to UTC for display.
                    _startPicker.Value = DateTime.SpecifyKind(
                        _startPicker.Value, DateTimeKind.Local).ToUniversalTime()
                    _endPicker.Value = DateTime.SpecifyKind(
                        _endPicker.Value, DateTimeKind.Local).ToUniversalTime()
                Else
                    ' Was UTC, now local.
                    _startPicker.Value = DateTime.SpecifyKind(
                        _startPicker.Value, DateTimeKind.Utc).ToLocalTime()
                    _endPicker.Value = DateTime.SpecifyKind(
                        _endPicker.Value, DateTimeKind.Utc).ToLocalTime()
                End If
            Catch
                ' Conversion out-of-range at the edges (MinValue /
                ' MaxValue). Leave pickers alone rather than crash.
            End Try

            UpdateTimeLabels()

            ' Re-render cached results in the new mode.
            If _currentMode = DisplayMode.Timeline AndAlso _lastTimelineResult IsNot Nothing Then
                RenderTimeline(_lastTimelineResult)
            ElseIf _currentMode = DisplayMode.Snapshot AndAlso _lastSnapshotRows IsNot Nothing Then
                RenderSnapshot(_lastSnapshotRows, _lastSnapshotInstantUtc)
            End If
        End Sub

        Private Sub OnCancelClicked(sender As Object, e As EventArgs)
            Dim cts = _queryCts
            If cts IsNot Nothing Then cts.Cancel()
        End Sub

        Private Async Sub OnSearchClicked(sender As Object, e As EventArgs)
            ' Cancel any in-flight query first.
            Dim prev = _queryCts
            If prev IsNot Nothing Then prev.Cancel()
            _queryCts = New CancellationTokenSource()
            Dim token = _queryCts.Token

            Dim filter = BuildFilter()
            SetBusy(True)

            Try
                If filter.IsSnapshot Then
                    Await RunSnapshotAsync(filter, token)
                Else
                    Await RunTimelineAsync(filter, token)
                End If
            Catch ex As OperationCanceledException
                _statusLabel.Text = "Cancelled."
            Catch ex As Exception
                _statusLabel.Text = $"Query failed: {ex.Message}"
            Finally
                SetBusy(False)
            End Try
        End Sub

        ''' <summary>
        ''' Construct the HistoryFilter that will be sent to the
        ''' query service. Picker values are interpreted based on
        ''' the "Use UTC" checkbox — local by default, UTC when
        ''' checked. Internally the filter always carries UTC.
        ''' </summary>
        Private Function BuildFilter() As HistoryFilter
            Dim f As New HistoryFilter With {
                .StartUtc = PickerToUtc(_startPicker.Value),
                .SessionIdentity = SelectedSessionIdentity(),
                .PlayerNamePattern = _playerCombo.Text.Trim(),
                .ChatTextPattern = _chatText.Text.Trim(),
                .IncludeChat = _chatCheck.Checked,
                .IncludeJoins = _joinCheck.Checked,
                .IncludeLeaves = _leaveCheck.Checked
            }
            If _endEnabledCheck.Checked Then
                f.EndUtc = PickerToUtc(_endPicker.Value)
            End If
            Return f
        End Function

        ''' <summary>
        ''' Converts a DateTimePicker value into UTC based on the
        ''' current "Use UTC" checkbox state. When checked, the
        ''' value is taken as UTC directly; when unchecked, treated
        ''' as local and converted. SpecifyKind is used because
        ''' DateTimePicker returns Kind=Unspecified, which makes
        ''' ToUniversalTime / ToLocalTime behave unpredictably.
        ''' </summary>
        Private Function PickerToUtc(pickerValue As DateTime) As DateTime
            If _utcCheck.Checked Then
                Return DateTime.SpecifyKind(pickerValue, DateTimeKind.Utc)
            End If
            Return DateTime.SpecifyKind(pickerValue, DateTimeKind.Local).ToUniversalTime()
        End Function

        ''' <summary>
        ''' Format a UTC timestamp for display, converting to local
        ''' time when the "Use UTC" checkbox is unchecked.
        ''' </summary>
        Private Function FormatDisplayTime(utc As DateTime) As String
            Dim toShow = utc
            If Not _utcCheck.Checked Then toShow = utc.ToLocalTime()
            Return toShow.ToString("yyyy-MM-dd HH:mm:ss")
        End Function

        ' ============================================================
        '  Query runners (DB → cache → render)
        ' ============================================================

        Private Async Function RunTimelineAsync(filter As HistoryFilter,
                                                 token As CancellationToken) As Task
            Dim result = Await _service.QueryTimelineAsync(filter, token)
            If token.IsCancellationRequested Then Return

            _lastTimelineResult = result
            _lastSnapshotRows = Nothing
            RenderTimeline(result)
        End Function

        Private Async Function RunSnapshotAsync(filter As HistoryFilter,
                                                 token As CancellationToken) As Task
            Dim rows = Await _service.QuerySnapshotAsync(filter, token)
            If token.IsCancellationRequested Then Return

            _lastSnapshotRows = rows
            _lastSnapshotInstantUtc = filter.StartUtc
            _lastTimelineResult = Nothing
            RenderSnapshot(rows, filter.StartUtc)
        End Function

        ' ============================================================
        '  Render helpers (cache → ListView). Called from query
        '  runners on fresh data and from OnUtcModeChanged when
        '  re-rendering cached results in a different time mode.
        ' ============================================================

        Private Sub RenderTimeline(result As TimelineResult)
            If _currentMode <> DisplayMode.Timeline Then
                _currentMode = DisplayMode.Timeline
                BuildTimelineColumns()
            End If

            _resultsList.BeginUpdate()
            Try
                _resultsList.Items.Clear()
                For Each r In result.Rows
                    Dim item As New ListViewItem(FormatDisplayTime(r.TimestampUtc))
                    item.SubItems.Add(RowKindLabel(r.Kind))
                    item.SubItems.Add(If(r.SourceLabel, ""))
                    ' Character + Player split. Character is the
                    ' in-game name (taken raw from the underlying
                    ' entity's DisplayName, with chat-fallback for
                    ' rows where it was empty at write time);
                    ' Player is the platform persona (raw from
                    ' PlayerActivity.PlayerName for activity rows,
                    ' looked up from a matching PlayerActivity for
                    ' chat rows). Either may be empty if the
                    ' relevant source had no value to bind.
                    item.SubItems.Add(If(r.CharacterName, ""))
                    item.SubItems.Add(If(r.PlatformPersona, ""))
                    item.SubItems.Add(If(r.Text, ""))
                    item.Tag = r
                    item.ToolTipText = BuildRowTooltip(r.SessionIdentity, r.InstanceId)
                    ColorCodeRow(item, r.Kind)
                    _resultsList.Items.Add(item)
                Next
            Finally
                _resultsList.EndUpdate()
            End Try

            If result.Truncated Then
                _statusLabel.Text = $"{result.Rows.Count} rows shown (truncated at {result.Limit} — narrow filters)."
            Else
                _statusLabel.Text = $"{result.Rows.Count} rows."
            End If
        End Sub

        Private Sub RenderSnapshot(rows As IReadOnlyList(Of SnapshotRow),
                                    instantUtc As DateTime)
            If _currentMode <> DisplayMode.Snapshot Then
                _currentMode = DisplayMode.Snapshot
                BuildSnapshotColumns()
            End If

            _resultsList.BeginUpdate()
            Try
                _resultsList.Items.Clear()
                For Each r In rows
                    ' Snapshot rows lead with Character now — the
                    ' in-game name is the more identifying signal
                    ' for "who was online". The platform persona
                    ' sits next to it for cases where the join
                    ' event didn't have DisplayName resolved.
                    Dim item As New ListViewItem(If(r.CharacterName, ""))
                    item.SubItems.Add(If(r.PlatformPersona, ""))
                    item.SubItems.Add(FormatDisplayTime(r.JoinedAtUtc))
                    item.SubItems.Add(If(r.SourceLabel, ""))
                    item.SubItems.Add(If(r.LastChatText, ""))
                    item.SubItems.Add(If(r.LastChatTimeUtc.HasValue,
                                          FormatDisplayTime(r.LastChatTimeUtc.Value),
                                          ""))
                    item.Tag = r
                    item.ToolTipText = BuildRowTooltip(r.SessionIdentity, r.InstanceId)
                    _resultsList.Items.Add(item)
                Next
            Finally
                _resultsList.EndUpdate()
            End Try

            _statusLabel.Text = $"{rows.Count} player(s) online at {FormatDisplayTime(instantUtc)}."
        End Sub

        ' ============================================================
        '  UI helpers
        ' ============================================================

        Private Sub SetBusy(busy As Boolean)
            _searchButton.Enabled = Not busy
            _cancelButton.Enabled = busy
            _progressBar.Visible = busy
            If busy Then _statusLabel.Text = "Searching..."
        End Sub

        Private Shared Function RowKindLabel(k As TimelineRow.RowKind) As String
            Select Case k
                Case TimelineRow.RowKind.Chat : Return "Chat"
                Case TimelineRow.RowKind.Join : Return "Join"
                Case TimelineRow.RowKind.Leave : Return "Leave"
                Case Else : Return ""
            End Select
        End Function

        Private Shared Sub ColorCodeRow(item As ListViewItem, k As TimelineRow.RowKind)
            Select Case k
                Case TimelineRow.RowKind.Join
                    item.ForeColor = Color.DarkGreen
                Case TimelineRow.RowKind.Leave
                    item.ForeColor = Color.DarkRed
                Case TimelineRow.RowKind.Chat
                    ' Leave default — chat is the common case, no tint.
            End Select
        End Sub

        ' ============================================================
        '  Phase 5h-6 — row tooltip + context menu plumbing.
        '
        '  Both renderers (RenderTimeline, RenderSnapshot) stash the
        '  underlying row object on ListViewItem.Tag and set
        '  ToolTipText via BuildRowTooltip. The two copy actions
        '  pull the data back via ExtractRowIdentifiers; the
        '  Opening handler enables the menu items only when a row
        '  is selected so the user can't trigger a copy that
        '  silently does nothing.
        ' ============================================================

        ''' <summary>
        ''' Format the hover tooltip shown on each result row.
        ''' Skips lines that have no value rather than emit
        ''' "Session: " with an empty body — a row with no
        ''' SessionIdentity (rare, but possible for rows from
        ''' games that don't have a session concept) just shows
        ''' the InstanceId line. Empty inputs yield an empty
        ''' tooltip; ListView treats empty ToolTipText as
        ''' "no tooltip".
        ''' </summary>
        Private Shared Function BuildRowTooltip(sessionIdentity As String, instanceId As String) As String
            Dim parts As New List(Of String)
            If Not String.IsNullOrEmpty(sessionIdentity) Then
                parts.Add("Session: " & sessionIdentity)
            End If
            If Not String.IsNullOrEmpty(instanceId) Then
                parts.Add("Instance: " & instanceId)
            End If
            If parts.Count = 0 Then Return ""
            Return String.Join(Environment.NewLine, parts)
        End Function

        ''' <summary>
        ''' Pull SessionIdentity + InstanceId off whichever row
        ''' type is stashed on the ListViewItem.Tag. Both
        ''' TimelineRow (timeline mode) and SnapshotRow (snapshot
        ''' mode) carry the same field names; we just have to
        ''' know which type we're holding to read them out.
        ''' </summary>
        Private Shared Function ExtractRowIdentifiers(item As ListViewItem) _
                As (SessionIdentity As String, InstanceId As String)
            If item Is Nothing Then Return (Nothing, Nothing)
            Dim t = TryCast(item.Tag, TimelineRow)
            If t IsNot Nothing Then Return (t.SessionIdentity, t.InstanceId)
            Dim s = TryCast(item.Tag, SnapshotRow)
            If s IsNot Nothing Then Return (s.SessionIdentity, s.InstanceId)
            Return (Nothing, Nothing)
        End Function

        Private Sub OnRowContextMenuOpening(sender As Object, e As CancelEventArgs)
            Dim hasSelection = _resultsList.SelectedItems.Count > 0
            Dim ids = (SessionIdentity:=CStr(Nothing), InstanceId:=CStr(Nothing))
            If hasSelection Then ids = ExtractRowIdentifiers(_resultsList.SelectedItems(0))
            _copyInstanceIdItem.Enabled = hasSelection AndAlso Not String.IsNullOrEmpty(ids.InstanceId)
            _copySessionIdentityItem.Enabled = hasSelection AndAlso Not String.IsNullOrEmpty(ids.SessionIdentity)
        End Sub

        Private Sub OnCopyInstanceId(sender As Object, e As EventArgs)
            If _resultsList.SelectedItems.Count = 0 Then Return
            Dim ids = ExtractRowIdentifiers(_resultsList.SelectedItems(0))
            If String.IsNullOrEmpty(ids.InstanceId) Then Return
            Try
                Clipboard.SetText(ids.InstanceId)
                _statusLabel.Text = $"Copied instance ID: {ids.InstanceId}"
            Catch
                ' Clipboard.SetText can throw if another process is
                ' holding the clipboard open. Silent fallback — the
                ' user can retry, or copy-from-tooltip-by-eye.
            End Try
        End Sub

        Private Sub OnCopySessionIdentity(sender As Object, e As EventArgs)
            If _resultsList.SelectedItems.Count = 0 Then Return
            Dim ids = ExtractRowIdentifiers(_resultsList.SelectedItems(0))
            If String.IsNullOrEmpty(ids.SessionIdentity) Then Return
            Try
                Clipboard.SetText(ids.SessionIdentity)
                _statusLabel.Text = $"Copied session identity: {ids.SessionIdentity}"
            Catch
                ' Same defensive stance as OnCopyInstanceId.
            End Try
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                Dim cts = _queryCts
                If cts IsNot Nothing Then
                    Try
                        cts.Cancel()
                        cts.Dispose()
                    Catch
                    End Try
                End If
            End If
            MyBase.Dispose(disposing)
        End Sub

        ' ============================================================
        '  Session combo item wrapper — ToString() is what the combo
        '  displays, Identity is what filtering uses.
        ' ============================================================
        Private Class SessionComboItem
            Public Property Identity As String
            Public Property DisplayLabel As String
            Public Overrides Function ToString() As String
                Return If(DisplayLabel, "")
            End Function
        End Class

    End Class

End Namespace
