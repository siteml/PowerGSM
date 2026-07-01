Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Text.Json
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data

' ============================================================
'  DiscordPanelEditorForm — modal Add/Edit dialog for one
'  DiscordPanelEntity row.
'
'  Caller passes either a fresh entity (for Add) or a copy of
'  an existing row (for Edit). The form mutates the supplied
'  instance in place and exposes it via ResultPanel; the caller
'  decides whether to save it (Add → new row in DB; Edit →
'  update existing row).
'
'  The Guild and Channel dropdowns are populated from the live
'  bot — when the bot isn't connected they show a "(connect
'  the bot first)" placeholder and disable the form. ScopeTarget
'  reuses a single ComboBox whose contents change with the
'  selected ScopeKind.
' ============================================================

Namespace GSM.Manager.UI

    Public Class DiscordPanelEditorForm
        Inherits Form

        Private ReadOnly _panel As DiscordPanelEntity
        Private ReadOnly _isAdd As Boolean
        Private ReadOnly _guilds As IReadOnlyList(Of GuildInfo)

        Private _nameTextBox As TextBox
        Private _guildCombo As ComboBox
        Private _channelCombo As ComboBox
        Private _scopeKindCombo As ComboBox
        Private _scopeTargetLabel As Label
        Private _scopeTargetCombo As ComboBox
        Private _refreshIntervalNumeric As NumericUpDown
        Private _layoutListBox As ListBox
        Private _layoutAddButton As Button
        Private _layoutAddMenu As ContextMenuStrip
        Private _layoutRemoveButton As Button
        Private _layoutUpButton As Button
        Private _layoutDownButton As Button
        Private _layoutResetButton As Button
        Private _groupingCombo As ComboBox
        Private _kindCombo As ComboBox
        Private _showEmptyCheck As CheckBox
        Private _showJoinTimeCheck As CheckBox
        Private _showTotalCheck As CheckBox
        Private _overridesButton As Button
        Private _overridesStatusLabel As Label
        Private _saveButton As Button
        Private _cancelButton As Button

        ' Editor-side mirror of the layout. Rebuilt on every
        ' Add/Remove/Up/Down/Reset; serialised on Save. Decoupled
        ' from the renderer's LayoutElement classes (which are
        ' Private inside DiscordBotPlugin) — the JSON shape is the
        ' contract between the two sides.
        Private _layoutElements As List(Of LayoutElementSpec)
        ' True = saved as NULL LayoutJson (use renderer default).
        ' Reset sets True; any structural edit clears it. Lets a
        ' user open + save without dirtying a row that's already
        ' on the default.
        Private _isLayoutDefault As Boolean

        ''' <summary>
        ''' The edited panel. Populated only on DialogResult = OK.
        ''' </summary>
        Public ReadOnly Property ResultPanel As DiscordPanelEntity
            Get
                Return _panel
            End Get
        End Property

        Public Sub New(panel As DiscordPanelEntity,
                       isAdd As Boolean,
                       guilds As IReadOnlyList(Of GuildInfo))
            FormIconHelper.ApplyTo(Me)
            If panel Is Nothing Then Throw New ArgumentNullException(NameOf(panel))
            _panel = panel
            _isAdd = isAdd
            _guilds = If(guilds, New List(Of GuildInfo))
            InitializeControls()
            LoadFromEntity()
        End Sub

        Private Sub InitializeControls()
            Me.Text = If(_isAdd, "Add Panel", "Edit Panel")
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            ' Wider + shorter than the original single-column 832-tall
            ' layout, which overflowed a 768px screen and hid the
            ' Save/Cancel buttons. The tall "Layout" list now lives in
            ' a right-hand column that spans the full height of the
            ' field stack, so the form height is driven by the (short)
            ' field rows instead of stacking the list above them.
            Me.Size = New Size(940, 580)

            ' Outer: two content columns (fields | layout list) over a
            ' full-width button row.
            Dim outer As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 2,
                .Padding = New Padding(10),
                .AutoSize = False
            }
            outer.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 430))
            outer.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            outer.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            outer.RowStyles.Add(New RowStyle(SizeType.Absolute, 44))

            ' Left column: the field stack. Row 6 is an empty spacer
            ' (0px) where the Layout list used to sit before it moved
            ' to the right column — kept so the rows below retain their
            ' original indices.
            Dim fields As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 13,
                .Padding = New Padding(0),
                .AutoSize = False
            }
            fields.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 120))
            fields.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            ' Rows 0..3: name/guild/channel/scope-kind (fixed 36).
            For i = 0 To 3
                fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            Next
            ' Row 4: scope target + the Phase 5d-7b slash-command
            ' visibility hint stacked beneath it — taller for the wrap.
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 64))
            ' Row 5: refresh interval.
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            ' Row 6: empty spacer (Layout moved to the right column).
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 0))
            ' Row 7: group-by combo.
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            ' Row 8: override roles button + status label.
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            ' Row 9: panel kind (Phase 5k).
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            ' Row 10: show-empty toggle (Phase 5k).
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            ' Row 11: show-join-time toggle (Phase 5k-2c).
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            ' Row 12: show-total-in-title toggle (Phase 5k-2c).
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))

            ' Right column: the layout list (label over listbox +
            ' button stack), filling the full content height.
            Dim layoutPane As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 2,
                .Padding = New Padding(0)
            }
            layoutPane.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            layoutPane.RowStyles.Add(New RowStyle(SizeType.Absolute, 22))
            layoutPane.RowStyles.Add(New RowStyle(SizeType.Percent, 100))

            ' Name
            fields.Controls.Add(MakeLabel("Display name:"), 0, 0)
            _nameTextBox = New TextBox With {.Dock = DockStyle.Fill}
            fields.Controls.Add(_nameTextBox, 1, 0)

            ' Guild
            fields.Controls.Add(MakeLabel("Guild:"), 0, 1)
            _guildCombo = New ComboBox With {
                .Dock = DockStyle.Fill,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            AddHandler _guildCombo.SelectedIndexChanged, AddressOf OnGuildChanged
            fields.Controls.Add(_guildCombo, 1, 1)

            ' Channel
            fields.Controls.Add(MakeLabel("Channel:"), 0, 2)
            _channelCombo = New ComboBox With {
                .Dock = DockStyle.Fill,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            fields.Controls.Add(_channelCombo, 1, 2)

            ' Scope kind
            fields.Controls.Add(MakeLabel("Scope:"), 0, 3)
            _scopeKindCombo = New ComboBox With {
                .Dock = DockStyle.Fill,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            _scopeKindCombo.Items.AddRange(New Object() {
                New ScopeItem("AllInstances", "All instances"),
                New ScopeItem("Game", "By game"),
                New ScopeItem("Installation", "By installation"),
                New ScopeItem("InstanceSet", "By instance set tag")
            })
            AddHandler _scopeKindCombo.SelectedIndexChanged, AddressOf OnScopeKindChanged
            fields.Controls.Add(_scopeKindCombo, 1, 3)

            ' Scope target — label updates with kind
            _scopeTargetLabel = MakeLabel("Target:")
            _scopeTargetLabel.TextAlign = ContentAlignment.TopLeft
            fields.Controls.Add(_scopeTargetLabel, 0, 4)
            _scopeTargetCombo = New ComboBox With {
                .Dock = DockStyle.Fill,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            ' Phase 5d-7b — the panel's scope (kind + target) is
            ' also exactly what this guild's slash commands can
            ' see, but nothing here said so. Stack a grey hint
            ' under the target combo so the coupling is visible
            ' where the operator actually sets the scope.
            Dim scopeTargetHost As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 2,
                .Padding = New Padding(0)
            }
            scopeTargetHost.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            scopeTargetHost.RowStyles.Add(New RowStyle(SizeType.Absolute, 26))
            scopeTargetHost.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            scopeTargetHost.Controls.Add(_scopeTargetCombo, 0, 0)
            Dim scopeSlashHint As New Label With {
                .Dock = DockStyle.Fill,
                .ForeColor = SystemColors.GrayText,
                .AutoSize = False,
                .TextAlign = ContentAlignment.TopLeft,
                .Text = "Instances in this panel's scope are also what this guild's slash commands (/players, and /lastseen once added) can see."
            }
            scopeTargetHost.Controls.Add(scopeSlashHint, 0, 1)
            fields.Controls.Add(scopeTargetHost, 1, 4)

            ' Refresh interval
            fields.Controls.Add(MakeLabel("Drift refresh (sec):"), 0, 5)
            _refreshIntervalNumeric = New NumericUpDown With {
                .Dock = DockStyle.Fill,
                .Minimum = 10,
                .Maximum = 3600,
                .Value = 60
            }
            fields.Controls.Add(_refreshIntervalNumeric, 1, 5)

            ' Layout (Phase 5d-5 item 3) — listbox of element
            ' specs with a vertical button stack. The listbox
            ' shows a preview that includes each element's natural
            ' prefix so the operator sees how the rendered line
            ' will join: ", Player count" rather than just "Player
            ' count". The first element's prefix is dropped from
            ' its preview to match what the renderer actually
            ' outputs (the leading prefix is suppressed for the
            ' first non-empty element on the line).
            layoutPane.Controls.Add(MakeLabel("Layout:"), 0, 0)
            Dim layoutHost As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 1,
                .Padding = New Padding(0)
            }
            layoutHost.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            layoutHost.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 100))
            layoutHost.RowStyles.Add(New RowStyle(SizeType.Percent, 100))

            _layoutListBox = New ListBox With {
                .Dock = DockStyle.Fill,
                .IntegralHeight = False
            }
            AddHandler _layoutListBox.SelectedIndexChanged, AddressOf OnLayoutSelectionChanged
            layoutHost.Controls.Add(_layoutListBox, 0, 0)

            Dim buttonStack As New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.TopDown,
                .WrapContents = False,
                .Padding = New Padding(6, 0, 0, 0)
            }
            _layoutAddButton = New Button With {.Text = "Add ▾", .Width = 90, .Height = 26}
            _layoutRemoveButton = New Button With {.Text = "Remove", .Width = 90, .Height = 26}
            _layoutUpButton = New Button With {.Text = "▲", .Width = 90, .Height = 26}
            _layoutDownButton = New Button With {.Text = "▼", .Width = 90, .Height = 26}
            _layoutResetButton = New Button With {.Text = "Reset to default", .Width = 90, .Height = 26}

            ' Add menu — one entry per element type. FreeText opens
            ' a small input dialog before adding; everything else
            ' is parameterless.
            _layoutAddMenu = New ContextMenuStrip()
            _layoutAddMenu.Items.Add("State icon", Nothing,
                Sub(s, e) AddElement("StateEmoji", Nothing))
            _layoutAddMenu.Items.Add("Instance name", Nothing,
                Sub(s, e) AddElement("InstanceName", Nothing))
            _layoutAddMenu.Items.Add("State text", Nothing,
                Sub(s, e) AddElement("StateText", Nothing))
            _layoutAddMenu.Items.Add("Player count", Nothing,
                Sub(s, e) AddElement("PlayerCount", Nothing))
            _layoutAddMenu.Items.Add("Game-specific context", Nothing,
                Sub(s, e) AddElement("ContextLine", Nothing))
            _layoutAddMenu.Items.Add("Next restart", Nothing,
                Sub(s, e) AddElement("NextRestart", Nothing))
            _layoutAddMenu.Items.Add("Node name", Nothing,
                Sub(s, e) AddElement("NodeName", Nothing))
            _layoutAddMenu.Items.Add("Free text…", Nothing,
                Sub(s, e)
                    Dim text = PromptForFreeText(Nothing)
                    If text IsNot Nothing Then AddElement("FreeText", text)
                End Sub)

            AddHandler _layoutAddButton.Click, AddressOf OnLayoutAddClicked
            AddHandler _layoutRemoveButton.Click, AddressOf OnLayoutRemoveClicked
            AddHandler _layoutUpButton.Click, AddressOf OnLayoutUpClicked
            AddHandler _layoutDownButton.Click, AddressOf OnLayoutDownClicked
            AddHandler _layoutResetButton.Click, AddressOf OnLayoutResetClicked
            AddHandler _layoutListBox.DoubleClick, AddressOf OnLayoutListBoxDoubleClick

            buttonStack.Controls.Add(_layoutAddButton)
            buttonStack.Controls.Add(_layoutRemoveButton)
            buttonStack.Controls.Add(_layoutUpButton)
            buttonStack.Controls.Add(_layoutDownButton)
            buttonStack.Controls.Add(_layoutResetButton)
            layoutHost.Controls.Add(buttonStack, 1, 0)
            layoutPane.Controls.Add(layoutHost, 0, 1)

            ' Group-by combo. Stored as a discriminator string;
            ' values match the renderer's grouping switch.
            fields.Controls.Add(MakeLabel("Group by:"), 0, 7)
            _groupingCombo = New ComboBox With {
                .Dock = DockStyle.Fill,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            _groupingCombo.Items.AddRange(New Object() {
                New GroupingItem("None", "None"),
                New GroupingItem("ByNode", "By node"),
                New GroupingItem("ByGame", "By game"),
                New GroupingItem("ByNodeThenGame", "By node, then by game")
            })
            fields.Controls.Add(_groupingCombo, 1, 7)

            ' Override roles row (Phase 5d-5 item 4). The button
            ' opens DiscordPanelRoleOverridesForm; the status
            ' label to its right reflects the current count of
            ' overrides ("3 override(s)" / "using guild default").
            ' Button is disabled in Add mode — the panel row
            ' doesn't exist in the DB yet, and saving overrides
            ' against an unsaved panel ID would leave orphan rows
            ' if the user then cancels the panel editor.
            fields.Controls.Add(MakeLabel("Override roles:"), 0, 8)
            Dim overridesRow As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 1,
                .Padding = New Padding(0)
            }
            overridesRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 130))
            overridesRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            overridesRow.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            _overridesButton = New Button With {
                .Text = "Configure…",
                .Anchor = AnchorStyles.Left Or AnchorStyles.Top Or AnchorStyles.Bottom,
                .Width = 120
            }
            AddHandler _overridesButton.Click, AddressOf OnOverridesClicked
            overridesRow.Controls.Add(_overridesButton, 0, 0)
            _overridesStatusLabel = New Label With {
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft,
                .ForeColor = SystemColors.GrayText,
                .Text = "—"
            }
            overridesRow.Controls.Add(_overridesStatusLabel, 1, 0)
            fields.Controls.Add(overridesRow, 1, 8)

            ' Panel kind (Phase 5k) — InstanceManager vs PlayerList.
            fields.Controls.Add(MakeLabel("Panel kind:"), 0, 9)
            _kindCombo = New ComboBox With {
                .Dock = DockStyle.Fill,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            _kindCombo.Items.Add(New IdItem("InstanceManager", "Instance manager"))
            _kindCombo.Items.Add(New IdItem("PlayerList", "Player list"))
            AddHandler _kindCombo.SelectedIndexChanged, AddressOf OnKindChanged
            fields.Controls.Add(_kindCombo, 1, 9)

            ' Show-empty toggle (Phase 5k) — player-list panels only.
            _showEmptyCheck = New CheckBox With {
                .Text = "Show instances with nobody online (player list only)",
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft
            }
            fields.Controls.Add(_showEmptyCheck, 1, 10)

            ' Player-list display toggles (Phase 5k-2c).
            _showJoinTimeCheck = New CheckBox With {
                .Text = "Show each player's join time (player list only)",
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft
            }
            fields.Controls.Add(_showJoinTimeCheck, 1, 11)

            _showTotalCheck = New CheckBox With {
                .Text = "Show total online count in the title (player list only)",
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft
            }
            fields.Controls.Add(_showTotalCheck, 1, 12)

            ' Buttons
            Dim buttonRow As New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.RightToLeft,
                .Padding = New Padding(0, 8, 0, 0)
            }
            _saveButton = New Button With {.Text = "Save", .Width = 90}
            _cancelButton = New Button With {.Text = "Cancel", .Width = 90}
            AddHandler _saveButton.Click, AddressOf OnSave
            AddHandler _cancelButton.Click, Sub(s, e)
                                                 Me.DialogResult = DialogResult.Cancel
                                                 Me.Close()
                                             End Sub
            buttonRow.Controls.Add(_saveButton)
            buttonRow.Controls.Add(_cancelButton)
            ' Assemble: fields left, layout list right, buttons across
            ' the bottom.
            outer.Controls.Add(fields, 0, 0)
            outer.Controls.Add(layoutPane, 1, 0)
            outer.Controls.Add(buttonRow, 0, 1)
            outer.SetColumnSpan(buttonRow, 2)

            Me.Controls.Add(outer)
            Me.AcceptButton = _saveButton
            Me.CancelButton = _cancelButton
        End Sub

        Private Function MakeLabel(text As String) As Label
            Return New Label With {
                .Text = text,
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft
            }
        End Function

        ''' <summary>
        ''' Phase 5k — a player-list panel uses a fixed per-instance
        ''' layout, so the layout-composition controls don't apply;
        ''' the show-empty toggle applies only to it. Grouping (5k-2b)
        ''' applies to both kinds, so the grouping combo stays enabled.
        ''' Enable/disable accordingly when the kind changes.
        ''' </summary>
        Private Sub OnKindChanged(sender As Object, e As EventArgs)
            Dim kindItem = TryCast(_kindCombo.SelectedItem, IdItem)
            Dim isPlayer = kindItem IsNot Nothing AndAlso
                           String.Equals(kindItem.Id, "PlayerList", StringComparison.OrdinalIgnoreCase)
            _layoutListBox.Enabled = Not isPlayer
            _layoutAddButton.Enabled = Not isPlayer
            _layoutRemoveButton.Enabled = Not isPlayer
            _layoutUpButton.Enabled = Not isPlayer
            _layoutDownButton.Enabled = Not isPlayer
            _layoutResetButton.Enabled = Not isPlayer
            _groupingCombo.Enabled = True
            _showEmptyCheck.Enabled = isPlayer
            _showJoinTimeCheck.Enabled = isPlayer
            _showTotalCheck.Enabled = isPlayer
        End Sub

        ' ============================================================
        '  Loading existing entity into controls
        ' ============================================================

        Private Sub LoadFromEntity()
            ' Populate guild dropdown.
            _guildCombo.Items.Clear()
            If _guilds.Count = 0 Then
                _guildCombo.Items.Add(New IdItem("", "(connect the bot first)"))
                _guildCombo.SelectedIndex = 0
                _guildCombo.Enabled = False
                _channelCombo.Enabled = False
                _saveButton.Enabled = False
            Else
                For Each g In _guilds
                    _guildCombo.Items.Add(New IdItem(g.GuildId, g.Name))
                Next
                ' Try to select the configured guild on edit; fall
                ' back to the first one for add.
                Dim selectedIndex As Integer = 0
                If Not String.IsNullOrEmpty(_panel.GuildId) Then
                    For i = 0 To _guildCombo.Items.Count - 1
                        Dim item = CType(_guildCombo.Items(i), IdItem)
                        If String.Equals(item.Id, _panel.GuildId, StringComparison.Ordinal) Then
                            selectedIndex = i
                            Exit For
                        End If
                    Next
                End If
                _guildCombo.SelectedIndex = selectedIndex
            End If

            ' Channel dropdown is populated by OnGuildChanged.
            ' OnGuildChanged runs synchronously from the
            ' SelectedIndex assignment above, so by here channels
            ' are loaded for the chosen guild.
            If Not String.IsNullOrEmpty(_panel.ChannelId) AndAlso _channelCombo.Enabled Then
                For i = 0 To _channelCombo.Items.Count - 1
                    Dim item = CType(_channelCombo.Items(i), IdItem)
                    If String.Equals(item.Id, _panel.ChannelId, StringComparison.Ordinal) Then
                        _channelCombo.SelectedIndex = i
                        Exit For
                    End If
                Next
            End If

            ' Display name.
            _nameTextBox.Text = If(_panel.DisplayName, "")

            ' Scope kind — match against canonical values, default
            ' to AllInstances.
            Dim kindToSelect = If(_panel.ScopeKind, "AllInstances")
            For i = 0 To _scopeKindCombo.Items.Count - 1
                Dim item = CType(_scopeKindCombo.Items(i), ScopeItem)
                If String.Equals(item.Kind, kindToSelect, StringComparison.OrdinalIgnoreCase) Then
                    _scopeKindCombo.SelectedIndex = i
                    Exit For
                End If
            Next
            ' VB.Net SelectedIndexChanged doesn't fire on assigning
            ' the same value; ensure the target combo is populated.
            If _scopeKindCombo.SelectedIndex = -1 Then _scopeKindCombo.SelectedIndex = 0
            OnScopeKindChanged(_scopeKindCombo, EventArgs.Empty)

            If Not String.IsNullOrEmpty(_panel.ScopeTargetId) AndAlso _scopeTargetCombo.Enabled Then
                For i = 0 To _scopeTargetCombo.Items.Count - 1
                    Dim item = CType(_scopeTargetCombo.Items(i), IdItem)
                    If String.Equals(item.Id, _panel.ScopeTargetId, StringComparison.Ordinal) Then
                        _scopeTargetCombo.SelectedIndex = i
                        Exit For
                    End If
                Next
            End If

            ' Refresh interval.
            Dim seconds = If(_panel.RefreshIntervalSeconds > 0, _panel.RefreshIntervalSeconds, 60)
            If seconds < _refreshIntervalNumeric.Minimum Then seconds = CInt(_refreshIntervalNumeric.Minimum)
            If seconds > _refreshIntervalNumeric.Maximum Then seconds = CInt(_refreshIntervalNumeric.Maximum)
            _refreshIntervalNumeric.Value = seconds

            ' Panel kind + show-empty toggle (Phase 5k).
            Dim panelKindToSelect = If(_panel.PanelKind, "InstanceManager")
            For i = 0 To _kindCombo.Items.Count - 1
                Dim kItem = CType(_kindCombo.Items(i), IdItem)
                If String.Equals(kItem.Id, panelKindToSelect, StringComparison.OrdinalIgnoreCase) Then
                    _kindCombo.SelectedIndex = i
                    Exit For
                End If
            Next
            If _kindCombo.SelectedIndex = -1 Then _kindCombo.SelectedIndex = 0
            _showEmptyCheck.Checked = _panel.ShowEmptyGroups
            _showJoinTimeCheck.Checked = _panel.ShowJoinTime
            _showTotalCheck.Checked = _panel.ShowTotalInTitle
            OnKindChanged(_kindCombo, EventArgs.Empty)

            ' Layout (Phase 5d-5 item 3). Parse the saved JSON if
            ' present; fall back to the default-equivalent specs
            ' if NULL/empty/parse-failure. _isLayoutDefault tracks
            ' whether the listbox is currently at the default — a
            ' user who opens + saves an unmodified default-layout
            ' panel writes NULL back, not a serialised default.
            _layoutElements = New List(Of LayoutElementSpec)
            If String.IsNullOrWhiteSpace(_panel.LayoutJson) Then
                _layoutElements.AddRange(BuildDefaultLayoutSpecs())
                _isLayoutDefault = True
            Else
                Dim parsed = TryParseLayoutJson(_panel.LayoutJson)
                If parsed Is Nothing OrElse parsed.Count = 0 Then
                    _layoutElements.AddRange(BuildDefaultLayoutSpecs())
                    _isLayoutDefault = True
                Else
                    _layoutElements.AddRange(parsed)
                    _isLayoutDefault = False
                End If
            End If
            RefreshLayoutListBox()

            ' Group-by. Default to "None" if the saved value is
            ' missing or unrecognised; the renderer behaves the
            ' same way for unknown discriminators, so this stays
            ' aligned.
            Dim groupingToSelect = If(_panel.GroupingKind, "None")
            Dim groupingIndex As Integer = 0
            For i = 0 To _groupingCombo.Items.Count - 1
                Dim item = CType(_groupingCombo.Items(i), GroupingItem)
                If String.Equals(item.Kind, groupingToSelect, StringComparison.OrdinalIgnoreCase) Then
                    groupingIndex = i
                    Exit For
                End If
            Next
            _groupingCombo.SelectedIndex = groupingIndex

            ' Override-roles status hint (Phase 5d-5 item 4).
            ' In Add mode the panel row doesn't exist in the DB
            ' yet, so there can't be any override rows pointing
            ' at it — disable the button entirely so the operator
            ' isn't tempted to configure overrides against an
            ' unsaved panel ID. The hint label explains why.
            If _isAdd Then
                _overridesButton.Enabled = False
                _overridesStatusLabel.Text = "Save the panel first to configure overrides"
            Else
                _overridesButton.Enabled = True
                RefreshOverrideStatusLabel()
            End If
        End Sub

        ' ============================================================
        '  Override roles (Phase 5d-5 item 4)
        ' ============================================================

        Private Sub OnOverridesClicked(sender As Object, e As EventArgs)
            ' Defensive: button is disabled in Add mode so this
            ' shouldn't fire there, but guard anyway.
            If _isAdd Then Return
            If String.IsNullOrEmpty(_panel.GuildId) OrElse String.IsNullOrEmpty(_panel.PanelId) Then Return

            ' Connection check: a guild appears in _guildCombo
            ' only if it's in _guilds, which is sourced from the
            ' bot's connected-guilds list. If the user opens this
            ' editor while the bot is offline they're stuck on
            ' the placeholder "(connect the bot first)" item; the
            ' Save button is already disabled in that case.
            ' Anything else here means the guild is connected.
            Dim selectedGuild = TryCast(_guildCombo.SelectedItem, IdItem)
            Dim isConnected = selectedGuild IsNot Nothing AndAlso
                              Not String.IsNullOrEmpty(selectedGuild.Id)

            Using dialog As New DiscordPanelRoleOverridesForm(
                    _panel.GuildId,
                    _panel.PanelId,
                    _panel.DisplayName,
                    isConnected)
                dialog.ShowDialog(Me)
                ' Refresh from the OverrideCountAtClose value the
                ' modal exposes; cheaper than re-querying the DB
                ' from here, and the modal already counted them
                ' for its own listview.
                UpdateOverrideStatusLabel(dialog.OverrideCountAtClose)
            End Using
        End Sub

        Private Sub RefreshOverrideStatusLabel()
            ' Edit-mode initial population: query the DB once on
            ' open to render the count. Subsequent refreshes after
            ' the modal closes go through UpdateOverrideStatusLabel
            ' with the count the modal already had in memory.
            Dim count As Integer = 0
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    count = db.DiscordRoleMappings.
                        Where(Function(m) m.GuildId = _panel.GuildId AndAlso
                                          m.PanelId = _panel.PanelId).
                        Count()
                End Using
            Catch
                ' DB read failure here is non-fatal — the override
                ' button still works; the hint just shows the "—"
                ' fallback. Most likely cause is the migration
                ' having never been run, in which case there are
                ' no overrides to count anyway.
                count = 0
            End Try
            UpdateOverrideStatusLabel(count)
        End Sub

        Private Sub UpdateOverrideStatusLabel(count As Integer)
            If count = 0 Then
                _overridesStatusLabel.Text = "Using guild default"
            Else
                _overridesStatusLabel.Text = $"{count} override(s) — guild default ignored for this panel"
            End If
        End Sub

        ' ============================================================
        '  Combo change handlers
        ' ============================================================

        Private Sub OnGuildChanged(sender As Object, e As EventArgs)
            _channelCombo.Items.Clear()
            Dim sel = TryCast(_guildCombo.SelectedItem, IdItem)
            If sel Is Nothing OrElse String.IsNullOrEmpty(sel.Id) Then
                _channelCombo.Enabled = False
                Return
            End If

            Dim guild = _guilds.FirstOrDefault(Function(g) g.GuildId = sel.Id)
            If guild Is Nothing OrElse guild.Channels Is Nothing OrElse guild.Channels.Count = 0 Then
                _channelCombo.Items.Add(New IdItem("", "(no postable channels)"))
                _channelCombo.SelectedIndex = 0
                _channelCombo.Enabled = False
                Return
            End If

            For Each ch In guild.Channels
                _channelCombo.Items.Add(New IdItem(ch.ChannelId, "#" & ch.Name))
            Next
            _channelCombo.SelectedIndex = 0
            _channelCombo.Enabled = True
        End Sub

        Private Sub OnScopeKindChanged(sender As Object, e As EventArgs)
            Dim sel = TryCast(_scopeKindCombo.SelectedItem, ScopeItem)
            If sel Is Nothing Then Return

            _scopeTargetCombo.Items.Clear()
            Select Case sel.Kind
                Case "AllInstances"
                    _scopeTargetLabel.Text = "Target:"
                    _scopeTargetCombo.Items.Add(New IdItem("", "(all instances)"))
                    _scopeTargetCombo.SelectedIndex = 0
                    _scopeTargetCombo.Enabled = False

                Case "Game"
                    _scopeTargetLabel.Text = "Game:"
                    Dim items = LoadDistinctGameIds()
                    PopulateTargetCombo(items, "(no games installed)")

                Case "Installation"
                    _scopeTargetLabel.Text = "Installation:"
                    Dim items = LoadInstallations()
                    PopulateTargetCombo(items, "(no installations)")

                Case "InstanceSet"
                    _scopeTargetLabel.Text = "Set tag:"
                    Dim items = LoadDistinctInstanceSetTags()
                    PopulateTargetCombo(items, "(no instance-set tags in use)")
            End Select
        End Sub

        Private Sub PopulateTargetCombo(items As List(Of IdItem), emptyHint As String)
            If items Is Nothing OrElse items.Count = 0 Then
                _scopeTargetCombo.Items.Add(New IdItem("", emptyHint))
                _scopeTargetCombo.SelectedIndex = 0
                _scopeTargetCombo.Enabled = False
            Else
                For Each it In items
                    _scopeTargetCombo.Items.Add(it)
                Next
                _scopeTargetCombo.SelectedIndex = 0
                _scopeTargetCombo.Enabled = True
            End If
        End Sub

        ' ============================================================
        '  DB lookups for scope target dropdown
        ' ============================================================

        Private Function LoadInstallations() As List(Of IdItem)
            Dim result As New List(Of IdItem)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim rows = db.Installations.OrderBy(Function(i) i.DisplayName).ToList()
                    For Each r In rows
                        result.Add(New IdItem(r.InstallationId,
                            $"{r.DisplayName} ({r.GameId})"))
                    Next
                End Using
            Catch
            End Try
            Return result
        End Function

        Private Function LoadDistinctGameIds() As List(Of IdItem)
            Dim result As New List(Of IdItem)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim ids = db.Installations.
                        Select(Function(i) i.GameId).
                        Distinct().
                        OrderBy(Function(g) g).
                        ToList()
                    For Each g In ids
                        If Not String.IsNullOrEmpty(g) Then
                            result.Add(New IdItem(g, g))
                        End If
                    Next
                End Using
            Catch
            End Try
            Return result
        End Function

        Private Function LoadDistinctInstanceSetTags() As List(Of IdItem)
            Dim result As New List(Of IdItem)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim tags = db.Instances.
                        Where(Function(i) i.InstanceSetTag <> Nothing AndAlso
                                          i.InstanceSetTag <> "").
                        Select(Function(i) i.InstanceSetTag).
                        Distinct().
                        OrderBy(Function(t) t).
                        ToList()
                    For Each t In tags
                        result.Add(New IdItem(t, t))
                    Next
                End Using
            Catch
            End Try
            Return result
        End Function

        ' ============================================================
        '  Save
        ' ============================================================

        ' ============================================================
        '  Layout editor (Phase 5d-5 item 3)
        ' ============================================================

        Private Sub OnLayoutAddClicked(sender As Object, e As EventArgs)
            ' Show the menu just below the Add button. Without an
            ' explicit anchor it appears at the cursor, which can
            ' land off the form when the operator clicked via
            ' keyboard.
            _layoutAddMenu.Show(_layoutAddButton, New Point(0, _layoutAddButton.Height))
        End Sub

        Private Sub AddElement(typeKey As String, freeText As String)
            Dim spec As New LayoutElementSpec With {
                .TypeKey = typeKey,
                .Text = If(freeText, "")
            }
            ' Insert after the current selection if there is one;
            ' otherwise append. Operators reaching for Add usually
            ' want "after this one", not "at the end of an unrelated
            ' list".
            Dim insertAt = _layoutListBox.SelectedIndex + 1
            If insertAt <= 0 OrElse insertAt > _layoutElements.Count Then
                _layoutElements.Add(spec)
                insertAt = _layoutElements.Count - 1
            Else
                _layoutElements.Insert(insertAt, spec)
            End If
            _isLayoutDefault = False
            RefreshLayoutListBox()
            If insertAt < _layoutListBox.Items.Count Then
                _layoutListBox.SelectedIndex = insertAt
            End If
        End Sub

        Private Sub OnLayoutRemoveClicked(sender As Object, e As EventArgs)
            Dim idx = _layoutListBox.SelectedIndex
            If idx < 0 OrElse idx >= _layoutElements.Count Then Return
            _layoutElements.RemoveAt(idx)
            _isLayoutDefault = False
            RefreshLayoutListBox()
            ' Re-select the same slot if anything's left there;
            ' otherwise the previous slot. Avoids dumping selection
            ' to nothing on every Remove.
            If _layoutElements.Count = 0 Then Return
            Dim newIdx = idx
            If newIdx >= _layoutElements.Count Then newIdx = _layoutElements.Count - 1
            _layoutListBox.SelectedIndex = newIdx
        End Sub

        Private Sub OnLayoutUpClicked(sender As Object, e As EventArgs)
            Dim idx = _layoutListBox.SelectedIndex
            If idx <= 0 OrElse idx >= _layoutElements.Count Then Return
            Dim tmp = _layoutElements(idx)
            _layoutElements(idx) = _layoutElements(idx - 1)
            _layoutElements(idx - 1) = tmp
            _isLayoutDefault = False
            RefreshLayoutListBox()
            _layoutListBox.SelectedIndex = idx - 1
        End Sub

        Private Sub OnLayoutDownClicked(sender As Object, e As EventArgs)
            Dim idx = _layoutListBox.SelectedIndex
            If idx < 0 OrElse idx >= _layoutElements.Count - 1 Then Return
            Dim tmp = _layoutElements(idx)
            _layoutElements(idx) = _layoutElements(idx + 1)
            _layoutElements(idx + 1) = tmp
            _isLayoutDefault = False
            RefreshLayoutListBox()
            _layoutListBox.SelectedIndex = idx + 1
        End Sub

        Private Sub OnLayoutResetClicked(sender As Object, e As EventArgs)
            Dim confirm = MessageBox.Show(
                "Replace the current layout with the default?",
                "Reset layout", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
            If confirm <> DialogResult.OK Then Return
            _layoutElements.Clear()
            _layoutElements.AddRange(BuildDefaultLayoutSpecs())
            _isLayoutDefault = True
            RefreshLayoutListBox()
            If _layoutElements.Count > 0 Then _layoutListBox.SelectedIndex = 0
        End Sub

        Private Sub OnLayoutListBoxDoubleClick(sender As Object, e As EventArgs)
            ' Double-click on a FreeText row opens the prompt to
            ' edit its text. Other element types are parameterless,
            ' so double-click is a no-op there.
            Dim idx = _layoutListBox.SelectedIndex
            If idx < 0 OrElse idx >= _layoutElements.Count Then Return
            Dim spec = _layoutElements(idx)
            If Not String.Equals(spec.TypeKey, "FreeText", StringComparison.Ordinal) Then Return
            Dim text = PromptForFreeText(spec.Text)
            If text Is Nothing Then Return
            spec.Text = text
            _isLayoutDefault = False
            RefreshLayoutListBox()
            _layoutListBox.SelectedIndex = idx
        End Sub

        Private Sub OnLayoutSelectionChanged(sender As Object, e As EventArgs)
            Dim idx = _layoutListBox.SelectedIndex
            Dim hasSelection = (idx >= 0 AndAlso idx < _layoutElements.Count)
            _layoutRemoveButton.Enabled = hasSelection
            _layoutUpButton.Enabled = hasSelection AndAlso idx > 0
            _layoutDownButton.Enabled = hasSelection AndAlso idx < _layoutElements.Count - 1
        End Sub

        Private Sub RefreshLayoutListBox()
            _layoutListBox.BeginUpdate()
            Try
                _layoutListBox.Items.Clear()
                For i = 0 To _layoutElements.Count - 1
                    _layoutListBox.Items.Add(BuildLayoutPreview(_layoutElements(i), i = 0))
                Next
            Finally
                _layoutListBox.EndUpdate()
            End Try
            OnLayoutSelectionChanged(_layoutListBox, EventArgs.Empty)
        End Sub

        ''' <summary>
        ''' Build the listbox preview string for one element. The
        ''' preview hints at the natural prefix the element will
        ''' contribute when the line is rendered, so the operator
        ''' can predict joining behaviour without knowing the rules.
        ''' isFirst drops the leading prefix to match what the
        ''' renderer outputs (the first non-empty element's prefix
        ''' is suppressed). These strings are display-only — the
        ''' saved JSON only carries TypeKey + Text.
        ''' </summary>
        Private Shared Function BuildLayoutPreview(spec As LayoutElementSpec, isFirst As Boolean) As String
            Dim prefix As String = ""
            Dim label As String = ""
            Select Case spec.TypeKey
                Case "StateEmoji"
                    label = "State icon"
                Case "InstanceName"
                    prefix = " "
                    label = "Instance name"
                Case "StateText"
                    prefix = " — "
                    label = "State text"
                Case "PlayerCount"
                    prefix = ", "
                    label = "Player count"
                Case "ContextLine"
                    prefix = " · "
                    label = "Game-specific context"
                Case "NextRestart"
                    prefix = ", restart "
                    label = "[time]"
                Case "NodeName"
                    prefix = " · "
                    label = "Node name"
                Case "FreeText"
                    label = $"Free text: ""{If(spec.Text, "")}"""
                Case Else
                    label = $"(unknown: {spec.TypeKey})"
            End Select
            If isFirst Then Return label
            Return prefix & label
        End Function

        ''' <summary>
        ''' Editor-side mirror of the renderer's DefaultLayout().
        ''' Kept duplicated rather than reaching into the renderer,
        ''' since those classes are Private inside
        ''' DiscordBotPlugin. The two must stay in sync; the JSON
        ''' shape (stable across both sides) is what enforces it.
        ''' </summary>
        Private Shared Function BuildDefaultLayoutSpecs() As List(Of LayoutElementSpec)
            Return New List(Of LayoutElementSpec) From {
                New LayoutElementSpec With {.TypeKey = "StateEmoji"},
                New LayoutElementSpec With {.TypeKey = "InstanceName"},
                New LayoutElementSpec With {.TypeKey = "StateText"},
                New LayoutElementSpec With {.TypeKey = "PlayerCount"},
                New LayoutElementSpec With {.TypeKey = "ContextLine"},
                New LayoutElementSpec With {.TypeKey = "NextRestart"}
            }
        End Function

        ''' <summary>
        ''' Parse a LayoutJson value into a list of editor-side
        ''' specs. Symmetric with the renderer's ParseLayout (same
        ''' JSON shape, same forward-compat "unknown type" handling)
        ''' but produces editor-friendly LayoutElementSpec instead
        ''' of the renderer's element classes. Returns Nothing on
        ''' parse failure so the caller can fall back to default.
        ''' </summary>
        Private Shared Function TryParseLayoutJson(json As String) As List(Of LayoutElementSpec)
            Try
                Using doc = JsonDocument.Parse(json)
                    Dim root = doc.RootElement
                    Dim elementsProp As JsonElement = Nothing
                    If Not root.TryGetProperty("elements", elementsProp) Then Return Nothing
                    Dim out As New List(Of LayoutElementSpec)
                    For Each el In elementsProp.EnumerateArray()
                        Dim typeProp As JsonElement = Nothing
                        If Not el.TryGetProperty("type", typeProp) Then Continue For
                        Dim spec As New LayoutElementSpec With {.TypeKey = typeProp.GetString()}
                        Dim textProp As JsonElement = Nothing
                        If el.TryGetProperty("text", textProp) Then
                            spec.Text = If(textProp.GetString(), "")
                        End If
                        out.Add(spec)
                    Next
                    Return out
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Serialise the current editor state to JSON in the
        ''' shape the renderer's ParseLayout reads. Only emits
        ''' "text" for FreeText elements — keeps the saved JSON
        ''' tidy for the common case.
        ''' </summary>
        Private Function SerializeLayout() As String
            Dim items = _layoutElements.Select(
                Function(spec)
                    If String.Equals(spec.TypeKey, "FreeText", StringComparison.Ordinal) Then
                        Return CType(New With {
                            Key .type = spec.TypeKey,
                            Key .text = If(spec.Text, "")
                        }, Object)
                    End If
                    Return CType(New With {Key .type = spec.TypeKey}, Object)
                End Function).ToList()
            Dim doc = New With {Key .elements = items}
            Return JsonSerializer.Serialize(doc)
        End Function

        ''' <summary>
        ''' Modal prompt for FreeText element content. Returns
        ''' Nothing on cancel; otherwise the typed string (which
        ''' may be empty — the operator may want pure whitespace
        ''' as a separator). InputBox would do but pulls in
        ''' Microsoft.VisualBasic.Interaction; the project doesn't
        ''' otherwise depend on it, and a 30-line modal is cheap.
        ''' </summary>
        Private Function PromptForFreeText(initialValue As String) As String
            Using dlg As New Form()
                dlg.Text = "Free text"
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog
                dlg.StartPosition = FormStartPosition.CenterParent
                dlg.MinimizeBox = False
                dlg.MaximizeBox = False
                dlg.ClientSize = New Size(360, 110)

                Dim lbl As New Label With {
                    .Text = "Text to insert in the rendered line:",
                    .Location = New Point(12, 12),
                    .Size = New Size(336, 18)
                }
                Dim tb As New TextBox With {
                    .Location = New Point(12, 34),
                    .Size = New Size(336, 22),
                    .Text = If(initialValue, "")
                }
                Dim okBtn As New Button With {
                    .Text = "OK",
                    .DialogResult = DialogResult.OK,
                    .Location = New Point(176, 70),
                    .Width = 80
                }
                Dim cancelBtn As New Button With {
                    .Text = "Cancel",
                    .DialogResult = DialogResult.Cancel,
                    .Location = New Point(264, 70),
                    .Width = 80
                }

                dlg.Controls.AddRange(New Control() {lbl, tb, okBtn, cancelBtn})
                dlg.AcceptButton = okBtn
                dlg.CancelButton = cancelBtn

                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    Return tb.Text
                Else
                    Return Nothing
                End If
            End Using
        End Function

        Private Sub OnSave(sender As Object, e As EventArgs)
            Dim name = (_nameTextBox.Text & "").Trim()
            If String.IsNullOrEmpty(name) Then
                MessageBox.Show("Enter a display name for the panel.",
                    "Name required", MessageBoxButtons.OK, MessageBoxIcon.Information)
                _nameTextBox.Focus()
                Return
            End If

            Dim guild = TryCast(_guildCombo.SelectedItem, IdItem)
            If guild Is Nothing OrElse String.IsNullOrEmpty(guild.Id) Then
                MessageBox.Show("Pick a guild.",
                    "Guild required", MessageBoxButtons.OK, MessageBoxIcon.Information)
                _guildCombo.Focus()
                Return
            End If

            Dim channel = TryCast(_channelCombo.SelectedItem, IdItem)
            If channel Is Nothing OrElse String.IsNullOrEmpty(channel.Id) Then
                MessageBox.Show("Pick a channel.",
                    "Channel required", MessageBoxButtons.OK, MessageBoxIcon.Information)
                _channelCombo.Focus()
                Return
            End If

            Dim scope = TryCast(_scopeKindCombo.SelectedItem, ScopeItem)
            If scope Is Nothing Then Return

            Dim targetId As String = ""
            If scope.Kind <> "AllInstances" Then
                Dim target = TryCast(_scopeTargetCombo.SelectedItem, IdItem)
                If target Is Nothing OrElse String.IsNullOrEmpty(target.Id) Then
                    MessageBox.Show($"Pick a {scope.Label.ToLowerInvariant()} target.",
                        "Scope target required", MessageBoxButtons.OK, MessageBoxIcon.Information)
                    _scopeTargetCombo.Focus()
                    Return
                End If
                targetId = target.Id
            End If

            ' Mutate the panel in place — the caller decides whether
            ' to insert (Add) or save changes (Edit).
            Dim now = DateTime.UtcNow
            _panel.DisplayName = name
            ' Guild change on an existing panel invalidates the
            ' MessageId — the bot can't edit a message in a
            ' channel it didn't post in. Same for channel changes.
            ' Clear MessageId so the next refresh posts fresh in
            ' the new location.
            If Not String.Equals(_panel.GuildId, guild.Id, StringComparison.Ordinal) OrElse
               Not String.Equals(_panel.ChannelId, channel.Id, StringComparison.Ordinal) Then
                _panel.MessageId = Nothing
            End If
            _panel.GuildId = guild.Id
            _panel.ChannelId = channel.Id
            _panel.ScopeKind = scope.Kind
            _panel.ScopeTargetId = targetId
            _panel.RefreshIntervalSeconds = CInt(_refreshIntervalNumeric.Value)

            ' Panel kind + show-empty toggle (Phase 5k).
            Dim kindItem = TryCast(_kindCombo.SelectedItem, IdItem)
            _panel.PanelKind = If(kindItem IsNot Nothing, kindItem.Id, "InstanceManager")
            _panel.ShowEmptyGroups = _showEmptyCheck.Checked
            _panel.ShowJoinTime = _showJoinTimeCheck.Checked
            _panel.ShowTotalInTitle = _showTotalCheck.Checked

            ' Layout (Phase 5d-5 item 3). Save NULL when the
            ' layout is the default — keeps default-layout rows
            ' clean in the DB and means future renderer tweaks to
            ' default behaviour automatically apply to those rows.
            ' Otherwise serialise the current editor state.
            If _isLayoutDefault Then
                _panel.LayoutJson = Nothing
            Else
                _panel.LayoutJson = SerializeLayout()
            End If

            ' Group-by.
            Dim grouping = TryCast(_groupingCombo.SelectedItem, GroupingItem)
            _panel.GroupingKind = If(grouping IsNot Nothing, grouping.Kind, "None")

            _panel.UpdatedUtc = now
            If _isAdd Then _panel.CreatedUtc = now

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        ' ============================================================
        '  Combo item helpers
        ' ============================================================

        Private Class IdItem
            Public ReadOnly Property Id As String
            Public ReadOnly Property Display As String
            Public Sub New(id As String, display As String)
                Me.Id = id
                Me.Display = display
            End Sub
            Public Overrides Function ToString() As String
                Return Display
            End Function
        End Class

        Private Class ScopeItem
            Public ReadOnly Property Kind As String
            Public ReadOnly Property Label As String
            Public Sub New(kind As String, label As String)
                Me.Kind = kind
                Me.Label = label
            End Sub
            Public Overrides Function ToString() As String
                Return Label
            End Function
        End Class

        Private Class GroupingItem
            Public ReadOnly Property Kind As String
            Public ReadOnly Property Label As String
            Public Sub New(kind As String, label As String)
                Me.Kind = kind
                Me.Label = label
            End Sub
            Public Overrides Function ToString() As String
                Return Label
            End Function
        End Class

        ''' <summary>
        ''' Editor-side spec for one layout element. Carries only
        ''' what survives serialisation: a TypeKey discriminator and
        ''' (for FreeText only) a Text payload. The renderer's
        ''' actual element classes (StateEmojiElement etc.) live
        ''' inside DiscordBotPlugin and are Private, so the editor
        ''' deliberately doesn't reference them — the JSON shape is
        ''' the contract.
        ''' </summary>
        Private Class LayoutElementSpec
            Public Property TypeKey As String
            Public Property Text As String
        End Class

    End Class

End Namespace
