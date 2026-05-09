Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Notification

' ============================================================
'  DiscordPanelRoleOverridesForm (Phase 5d-5 item 4)
'
'  Modal for editing the panel-scoped role override mapping.
'  Mirrors DiscordRoleMappingsForm's UX with two simplifications:
'    (a) the guild is fixed by the panel, so no guild combo;
'    (b) DB scope is (GuildId, PanelId) instead of (GuildId, "").
'
'  Whole-mapping override semantics: when ANY rows exist here,
'  they entirely replace the guild-default for this panel —
'  the guild-default mapping is NOT consulted. The hint label
'  surfaces this so operators don't accidentally lock themselves
'  out by adding one override and assuming guild-default still
'  applies to other roles.
'
'  Operations:
'    Add    → DiscordRoleMappingEditorForm with the panel's
'             guild's roles, filtering out roles already mapped
'             at panel scope (a role can be at guild-default AND
'             have a panel override; we only filter dupes within
'             the same scope).
'    Edit   → same form, pre-populated with the existing row's
'             values.
'    Remove → drop the row. If this was the last row, the panel
'             reverts to guild-default behaviour automatically
'             (the resolver's "no panel scope → fall back to
'             guild-default" path takes over).
'
'  After every commit/remove the bot's role-mapping cache is
'  reloaded so the next interaction sees fresh state without
'  needing a manager restart.
' ============================================================

Namespace GSM.Manager.UI

    Public Class DiscordPanelRoleOverridesForm
        Inherits Form

        Private ReadOnly _guildId As String
        Private ReadOnly _panelId As String
        Private ReadOnly _panelDisplayName As String
        Private ReadOnly _isGuildConnected As Boolean

        Private _mappingsList As ListView
        Private _addButton As Button
        Private _editButton As Button
        Private _removeButton As Button
        Private _closeButton As Button
        Private _hintLabel As Label
        Private _headerLabel As Label

        ''' <summary>
        ''' True if any overrides existed when the form closed.
        ''' Caller (DiscordPanelEditorForm) reads this to update its
        ''' "Override roles… (N override(s))" status hint without
        ''' re-querying the DB itself.
        ''' </summary>
        Public ReadOnly Property OverrideCountAtClose As Integer

        Public Sub New(guildId As String,
                       panelId As String,
                       panelDisplayName As String,
                       isGuildConnected As Boolean)
            _guildId = guildId
            _panelId = panelId
            _panelDisplayName = If(panelDisplayName, "(unnamed panel)")
            _isGuildConnected = isGuildConnected
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            ReloadMappingsList()
        End Sub

        ' ============================================================
        '  Layout
        ' ============================================================

        Private Sub InitializeControls()
            Me.Text = "Panel role overrides"
            Me.Size = New Size(720, 500)
            Me.StartPosition = FormStartPosition.CenterParent
            Me.MinimumSize = New Size(640, 400)

            Dim root As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 4,
                .Padding = New Padding(10)
            }
            root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 56))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 48))

            ' Header row — names the panel scope so the operator
            ' knows what they're editing.
            _headerLabel = New Label With {
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft,
                .Font = New Font(Me.Font, FontStyle.Bold),
                .Text = $"Overrides for panel: {_panelDisplayName}"
            }
            root.Controls.Add(_headerLabel, 0, 0)

            ' ListView + button column
            Dim middleRow As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 1
            }
            middleRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            middleRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 110))
            middleRow.RowStyles.Add(New RowStyle(SizeType.Percent, 100))

            _mappingsList = New ListView With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .HideSelection = False,
                .MultiSelect = False
            }
            _mappingsList.Columns.Add("Role", 280)
            _mappingsList.Columns.Add("Permission", 220)
            AddHandler _mappingsList.DoubleClick, AddressOf OnEdit
            AddHandler _mappingsList.SelectedIndexChanged, AddressOf OnSelectionChanged
            middleRow.Controls.Add(_mappingsList, 0, 0)

            Dim buttonStack As New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.TopDown,
                .Padding = New Padding(0, 0, 0, 0)
            }
            _addButton = New Button With {.Text = "Add...", .Width = 100, .Margin = New Padding(0, 0, 0, 6)}
            _editButton = New Button With {.Text = "Edit...", .Width = 100, .Margin = New Padding(0, 0, 0, 6), .Enabled = False}
            _removeButton = New Button With {.Text = "Remove", .Width = 100, .Margin = New Padding(0, 0, 0, 6), .Enabled = False}
            AddHandler _addButton.Click, AddressOf OnAdd
            AddHandler _editButton.Click, AddressOf OnEdit
            AddHandler _removeButton.Click, AddressOf OnRemove
            buttonStack.Controls.Add(_addButton)
            buttonStack.Controls.Add(_editButton)
            buttonStack.Controls.Add(_removeButton)
            middleRow.Controls.Add(buttonStack, 1, 0)
            root.Controls.Add(middleRow, 0, 1)

            ' Hint label — multi-line so the override semantics
            ' fit. AutoSize off + fixed height in the row style
            ' so the listview area stays predictable.
            _hintLabel = New Label With {
                .Dock = DockStyle.Fill,
                .ForeColor = SystemColors.GrayText,
                .TextAlign = ContentAlignment.TopLeft
            }
            root.Controls.Add(_hintLabel, 0, 2)

            ' Close button
            Dim closeRow As New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.RightToLeft,
                .Padding = New Padding(0, 8, 0, 0)
            }
            _closeButton = New Button With {.Text = "Close", .Width = 100}
            AddHandler _closeButton.Click, Sub(s, e) Me.Close()
            closeRow.Controls.Add(_closeButton)
            root.Controls.Add(closeRow, 0, 3)

            Me.Controls.Add(root)
            Me.AcceptButton = _closeButton
            Me.CancelButton = _closeButton
        End Sub

        ' ============================================================
        '  Listview population — scoped to (GuildId, PanelId)
        ' ============================================================

        Private Sub ReloadMappingsList()
            _mappingsList.Items.Clear()

            ' Add availability tracks bot connection: enumerating
            ' the guild's roles needs a live Discord query, which
            ' the bot only supports for guilds it's connected to.
            ' (Disconnect is rare in practice — the bot's only
            ' offline when the operator hasn't configured the
            ' token yet or the manager just started — but the
            ' guard matches the guild-scope form's behaviour.)
            _addButton.Enabled = _isGuildConnected

            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim mappings = db.DiscordRoleMappings.
                        Where(Function(m) m.GuildId = _guildId AndAlso m.PanelId = _panelId).
                        OrderBy(Function(m) m.RoleName).
                        ToList()
                    For Each m In mappings
                        Dim item As New ListViewItem(If(m.RoleName, "(unknown role)"))
                        item.SubItems.Add(FormatPermission(m.Permission))
                        item.Tag = m.RoleId
                        _mappingsList.Items.Add(item)
                    Next
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to load role overrides:" & vbCrLf & ex.Message,
                    "Panel Role Overrides", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End Try

            UpdateHintLabel()
            OnSelectionChanged(Me, EventArgs.Empty)
            _OverrideCountAtClose = _mappingsList.Items.Count
        End Sub

        Private Sub UpdateHintLabel()
            If Not _isGuildConnected Then
                _hintLabel.Text = "Bot is not connected to this guild — Add and Edit are disabled. You can still Remove existing overrides."
                Return
            End If
            If _mappingsList.Items.Count = 0 Then
                _hintLabel.Text = "No overrides yet. This panel uses the guild-default role mapping. Click Add… to set panel-specific permissions."
            Else
                ' Critical UX point: when overrides exist, the
                ' guild-default is IGNORED for this panel. Spelling
                ' that out here so operators don't accidentally
                ' lock themselves out by adding one override and
                ' assuming guild-default still grants other roles.
                _hintLabel.Text = $"{_mappingsList.Items.Count} override(s). When overrides exist, ONLY these mappings apply to this panel — the guild-default mapping is ignored."
            End If
        End Sub

        Private Sub OnSelectionChanged(sender As Object, e As EventArgs)
            Dim hasSelection = _mappingsList.SelectedItems.Count > 0
            _editButton.Enabled = hasSelection AndAlso _isGuildConnected
            _removeButton.Enabled = hasSelection
        End Sub

        ' ============================================================
        '  Add / Edit / Remove
        ' ============================================================

        Private Sub OnAdd(sender As Object, e As EventArgs)
            If Not _isGuildConnected Then Return

            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            If plugin Is Nothing Then Return

            Dim allRoles = plugin.GetGuildRoles(_guildId)

            ' Filter out roles already mapped at THIS panel scope.
            ' A role mapped at guild-default is intentionally NOT
            ' filtered — adding a panel override for that role is
            ' the whole point of this dialog.
            Dim mappedRoleIds As New HashSet(Of String)(StringComparer.Ordinal)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim existing = db.DiscordRoleMappings.
                        Where(Function(x) x.GuildId = _guildId AndAlso x.PanelId = _panelId).
                        Select(Function(x) x.RoleId).
                        ToList()
                    For Each rid In existing
                        mappedRoleIds.Add(rid)
                    Next
                End Using
            Catch
                ' DB read failure shouldn't block the Add flow —
                ' worst case we offer a role that's already mapped,
                ' and the unique-key constraint catches it on save.
            End Try

            Dim available = allRoles.
                Where(Function(r) Not mappedRoleIds.Contains(r.RoleId)).
                ToList()
            If available.Count = 0 Then
                MessageBox.Show("All assignable roles in this guild already have overrides on this panel. Remove one first or use Edit to change its permission.",
                    "Add Panel Override", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            Using dialog As New DiscordRoleMappingEditorForm(available)
                If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                CommitOverride(dialog.ResultRoleId,
                               dialog.ResultRoleName,
                               dialog.ResultPermission)
            End Using
        End Sub

        Private Sub OnEdit(sender As Object, e As EventArgs)
            If _mappingsList.SelectedItems.Count = 0 Then Return
            Dim roleId = TryCast(_mappingsList.SelectedItems(0).Tag, String)
            If String.IsNullOrEmpty(roleId) Then Return
            If Not _isGuildConnected Then Return

            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            If plugin Is Nothing Then Return

            Dim existingName As String = ""
            Dim existingPermission As CommandPermission = CommandPermission.ServerOperator
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    ' Filter all three components of the composite
                    ' PK — without PanelId we'd risk picking up the
                    ' guild-default row for the same role, which
                    ' has different semantics.
                    Dim row = db.DiscordRoleMappings.
                        Where(Function(m) m.GuildId = _guildId AndAlso
                                          m.PanelId = _panelId AndAlso
                                          m.RoleId = roleId).
                        FirstOrDefault()
                    If row IsNot Nothing Then
                        existingName = If(row.RoleName, "")
                        existingPermission = CType(row.Permission, CommandPermission)
                    End If
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to load override: " & ex.Message,
                    "Edit Panel Override", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End Try

            Dim allRoles = plugin.GetGuildRoles(_guildId)
            Using dialog As New DiscordRoleMappingEditorForm(allRoles, roleId, existingName, existingPermission)
                If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                CommitOverride(dialog.ResultRoleId,
                               dialog.ResultRoleName,
                               dialog.ResultPermission)
            End Using
        End Sub

        Private Sub OnRemove(sender As Object, e As EventArgs)
            If _mappingsList.SelectedItems.Count = 0 Then Return
            Dim roleId = TryCast(_mappingsList.SelectedItems(0).Tag, String)
            If String.IsNullOrEmpty(roleId) Then Return

            Dim roleName = _mappingsList.SelectedItems(0).Text
            Dim confirm = MessageBox.Show(
                $"Remove the override for ""{roleName}"" on this panel? Role members will revert to guild-default behaviour for this panel.",
                "Remove Panel Override", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If confirm <> DialogResult.Yes Then Return

            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = db.DiscordRoleMappings.
                        Where(Function(m) m.GuildId = _guildId AndAlso
                                          m.PanelId = _panelId AndAlso
                                          m.RoleId = roleId).
                        FirstOrDefault()
                    If row IsNot Nothing Then
                        db.DiscordRoleMappings.Remove(row)
                        db.SaveChanges()
                    End If
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to remove override: " & ex.Message,
                    "Remove Panel Override", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End Try

            ReloadBotCache()
            ReloadMappingsList()
        End Sub

        ' ============================================================
        '  DB write + cache reload
        ' ============================================================

        Private Sub CommitOverride(roleId As String,
                                   roleName As String,
                                   permission As CommandPermission)
            If String.IsNullOrEmpty(_guildId) OrElse String.IsNullOrEmpty(roleId) Then Return
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim now = DateTime.UtcNow
                    Dim row = db.DiscordRoleMappings.
                        Where(Function(m) m.GuildId = _guildId AndAlso
                                          m.PanelId = _panelId AndAlso
                                          m.RoleId = roleId).
                        FirstOrDefault()
                    If row Is Nothing Then
                        ' Insert with PanelId set — distinguishes
                        ' from the guild-default row (PanelId="")
                        ' that may exist for the same role.
                        row = New DiscordRoleMappingEntity With {
                            .GuildId = _guildId,
                            .PanelId = _panelId,
                            .RoleId = roleId,
                            .CreatedUtc = now
                        }
                        db.DiscordRoleMappings.Add(row)
                    End If
                    row.RoleName = roleName
                    row.Permission = CInt(permission)
                    row.UpdatedUtc = now
                    db.SaveChanges()
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to save override: " & ex.Message,
                    "Save Panel Override", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End Try

            ReloadBotCache()
            ReloadMappingsList()
        End Sub

        Private Sub ReloadBotCache()
            Try
                Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
                If plugin IsNot Nothing Then
                    plugin.ReloadRoleMappingsAsync().GetAwaiter().GetResult()
                End If
            Catch
            End Try
        End Sub

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Shared Function FormatPermission(value As Integer) As String
            Select Case CType(value, CommandPermission)
                Case CommandPermission.Administrator : Return "Administrator"
                Case CommandPermission.ServerOperator : Return "Server Operator"
                Case CommandPermission.Everyone : Return "Everyone (no elevation)"
                Case Else : Return $"(unknown: {value})"
            End Select
        End Function

    End Class

End Namespace
