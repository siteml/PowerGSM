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
'  DiscordRoleMappingsForm — modal configuration window for
'  per-guild Discord role mappings (Phase 5d-3).
'
'  Replaces the hardcoded "PowerGSM Operator" role-name match
'  with proper data: each row in DiscordRoleMappings maps a
'  specific (GuildId, RoleId) pair to a permission tier
'  (ServerOperator or Administrator). Roles not in the table
'  resolve to Everyone (the implicit default for unmapped
'  roles), so the table only ever stores elevations.
'
'  UX layout:
'     Guild dropdown at top
'       (sources: bot's connected guilds + any guilds with
'        existing DB mappings, so disconnected/kicked guilds
'        are still visible for cleanup)
'     Mappings ListView middle (Role | Permission)
'     Add / Edit / Remove buttons on the right
'     Hint label below
'     Close button at bottom
'
'  Each Add/Edit/Remove operation commits immediately to the
'  DB and triggers DiscordBotPlugin.ReloadRoleMappingsAsync so
'  the bot's in-memory cache picks up the change without a
'  reconnect cycle. There's intentionally no global Save button:
'  with auto-commit, "close without saving" has no meaning, and
'  the dirty-state tracking that a Save model would require is
'  pure overhead.
'
'  Degraded mode: if the bot isn't connected to the selected
'  guild, the form still shows existing mappings (read straight
'  from the DB) and Remove still works. Add and Edit are
'  disabled because both need a fresh role list from Discord
'  to populate the editor's role dropdown — we don't store
'  role catalogues in the DB, only the snapshot for displayed
'  rows.
' ============================================================

Namespace GSM.Manager.UI

    Public Class DiscordRoleMappingsForm
        Inherits Form

        Private _guildCombo As ComboBox
        Private _mappingsList As ListView
        Private _addButton As Button
        Private _editButton As Button
        Private _removeButton As Button
        Private _closeButton As Button
        Private _hintLabel As Label

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            LoadGuilds()
        End Sub

        ' ============================================================
        '  Layout
        ' ============================================================

        Private Sub InitializeControls()
            Me.Text = "Discord Role Mappings"
            Me.Size = New Size(720, 524)
            Me.StartPosition = FormStartPosition.CenterParent
            Me.MinimumSize = New Size(640, 424)

            Dim root As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 4,
                .Padding = New Padding(10)
            }
            root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 52))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 48))

            ' Guild picker row
            Dim guildRow As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 1
            }
            guildRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 50))
            guildRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            guildRow.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            guildRow.Controls.Add(New Label With {
                .Text = "Guild:",
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0)
            _guildCombo = New ComboBox With {
                .Dock = DockStyle.Fill,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            AddHandler _guildCombo.SelectedIndexChanged, AddressOf OnGuildChanged
            guildRow.Controls.Add(_guildCombo, 1, 0)
            root.Controls.Add(guildRow, 0, 0)

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

            ' Hint area — dynamic per-guild state hint on top, plus
            ' a static Phase 5d-7b note making explicit that these
            ' guild-default mappings also gate slash commands.
            _hintLabel = New Label With {
                .Dock = DockStyle.Fill,
                .ForeColor = SystemColors.GrayText,
                .TextAlign = ContentAlignment.MiddleLeft
            }
            Dim hintHost As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 2,
                .Padding = New Padding(0)
            }
            hintHost.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            hintHost.RowStyles.Add(New RowStyle(SizeType.Absolute, 22))
            hintHost.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            hintHost.Controls.Add(_hintLabel, 0, 0)
            Dim slashNote As New Label With {
                .Dock = DockStyle.Fill,
                .ForeColor = SystemColors.GrayText,
                .AutoSize = False,
                .TextAlign = ContentAlignment.TopLeft,
                .Text = "These role mappings also govern who can run slash commands (e.g. /players) in this guild — not just the panel buttons."
            }
            hintHost.Controls.Add(slashNote, 0, 1)
            root.Controls.Add(hintHost, 0, 2)

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
        End Sub

        ' ============================================================
        '  Guild dropdown — combine connected guilds (from the bot)
        '  with any DB-only guilds (mappings exist but bot isn't in
        '  them anymore) so disconnected/kicked guilds remain visible
        '  for cleanup.
        ' ============================================================

        Private Sub LoadGuilds()
            _guildCombo.Items.Clear()

            Dim dropdownItems As New List(Of GuildDropdownItem)
            Dim seenGuildIds As New HashSet(Of String)(StringComparer.Ordinal)

            ' Source 1: bot's connected guilds. Preferred — gives
            ' fresh names and tells us we can Add/Edit (which need
            ' a live role list from Discord).
            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            If plugin IsNot Nothing Then
                For Each g In plugin.GetGuildsAndChannels()
                    dropdownItems.Add(New GuildDropdownItem(g.GuildId, g.Name, isConnected:=True))
                    seenGuildIds.Add(g.GuildId)
                Next
            End If

            ' Source 2: any guild ID present in the DB that wasn't
            ' covered above. We can only show the raw GuildId (no
            ' name) for these — but they're still listable for the
            ' "remove dangling mappings" use case.
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim dbGuildIds = db.DiscordRoleMappings.
                        Select(Function(m) m.GuildId).
                        Distinct().
                        ToList()
                    For Each gid In dbGuildIds
                        If seenGuildIds.Contains(gid) Then Continue For
                        dropdownItems.Add(New GuildDropdownItem(gid, $"(disconnected) {gid}", isConnected:=False))
                        seenGuildIds.Add(gid)
                    Next
                End Using
            Catch
                ' DB read failure here is non-fatal — connected-guilds
                ' source already populated the dropdown. Most likely
                ' cause is the migration not having been run, in
                ' which case there's nothing to enumerate anyway.
            End Try

            For Each item In dropdownItems.OrderBy(Function(d) d.DisplayName)
                _guildCombo.Items.Add(item)
            Next

            If _guildCombo.Items.Count > 0 Then
                _guildCombo.SelectedIndex = 0
            Else
                _hintLabel.Text = "Bot is not connected to any guilds, and no mappings exist in the database."
                _addButton.Enabled = False
            End If
        End Sub

        Private Sub OnGuildChanged(sender As Object, e As EventArgs)
            ReloadMappingsList()
        End Sub

        ' ============================================================
        '  ListView refresh + selection
        ' ============================================================

        Private Sub ReloadMappingsList()
            _mappingsList.Items.Clear()
            Dim guildItem = TryCast(_guildCombo.SelectedItem, GuildDropdownItem)
            If guildItem Is Nothing Then
                _hintLabel.Text = ""
                _addButton.Enabled = False
                Return
            End If

            ' Add availability tracks bot connection to this guild:
            ' both Add and Edit need to enumerate roles from
            ' Discord, which requires the bot to be in the guild.
            _addButton.Enabled = guildItem.IsConnected

            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    ' Guild-default mappings only (PanelId = "").
                    ' Panel overrides live alongside in the same
                    ' table, distinguished by PanelId; the panel
                    ' editor's "Override roles…" modal handles those.
                    Dim mappings = db.DiscordRoleMappings.
                        Where(Function(m) m.GuildId = guildItem.GuildId AndAlso m.PanelId = "").
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
                MessageBox.Show("Failed to load role mappings:" & vbCrLf & ex.Message,
                    "Discord Role Mappings", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End Try

            UpdateHintLabel(guildItem)
            OnSelectionChanged(Me, EventArgs.Empty)
        End Sub

        Private Sub UpdateHintLabel(guildItem As GuildDropdownItem)
            If Not guildItem.IsConnected Then
                _hintLabel.Text = "Bot is not connected to this guild — Add and Edit are disabled, but you can still Remove existing mappings."
                Return
            End If
            If _mappingsList.Items.Count = 0 Then
                _hintLabel.Text = "No role mappings yet. Click Add... to grant operator permissions to a role."
            Else
                _hintLabel.Text = $"{_mappingsList.Items.Count} mapping(s). Roles not listed here resolve to Everyone (no elevation)."
            End If
        End Sub

        Private Sub OnSelectionChanged(sender As Object, e As EventArgs)
            Dim hasSelection = _mappingsList.SelectedItems.Count > 0
            Dim guildItem = TryCast(_guildCombo.SelectedItem, GuildDropdownItem)
            Dim isConnected = guildItem IsNot Nothing AndAlso guildItem.IsConnected
            _editButton.Enabled = hasSelection AndAlso isConnected
            _removeButton.Enabled = hasSelection
        End Sub

        ' ============================================================
        '  Add / Edit / Remove handlers
        ' ============================================================

        Private Sub OnAdd(sender As Object, e As EventArgs)
            Dim guildItem = TryCast(_guildCombo.SelectedItem, GuildDropdownItem)
            If guildItem Is Nothing OrElse Not guildItem.IsConnected Then Return

            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            If plugin Is Nothing Then Return

            Dim allRoles = plugin.GetGuildRoles(guildItem.GuildId)

            ' Filter out roles that already have a mapping in this
            ' guild — the user can't add a duplicate; they Edit the
            ' existing one to change its permission, or Remove it.
            Dim mappedRoleIds As New HashSet(Of String)(StringComparer.Ordinal)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim existing = db.DiscordRoleMappings.
                        Where(Function(x) x.GuildId = guildItem.GuildId AndAlso x.PanelId = "").
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
                MessageBox.Show("All assignable roles in this guild already have mappings. Remove one first or use Edit to change its permission.",
                    "Add Role Mapping", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            Using dialog As New DiscordRoleMappingEditorForm(available)
                If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                CommitMapping(guildItem.GuildId,
                              dialog.ResultRoleId,
                              dialog.ResultRoleName,
                              dialog.ResultPermission)
            End Using
        End Sub

        Private Sub OnEdit(sender As Object, e As EventArgs)
            If _mappingsList.SelectedItems.Count = 0 Then Return
            Dim roleId = TryCast(_mappingsList.SelectedItems(0).Tag, String)
            If String.IsNullOrEmpty(roleId) Then Return

            Dim guildItem = TryCast(_guildCombo.SelectedItem, GuildDropdownItem)
            If guildItem Is Nothing OrElse Not guildItem.IsConnected Then Return

            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            If plugin Is Nothing Then Return

            Dim existingName As String = ""
            Dim existingPermission As CommandPermission = CommandPermission.ServerOperator
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = db.DiscordRoleMappings.
                        Where(Function(m) m.GuildId = guildItem.GuildId AndAlso m.PanelId = "" AndAlso m.RoleId = roleId).
                        FirstOrDefault()
                    If row IsNot Nothing Then
                        existingName = If(row.RoleName, "")
                        existingPermission = CType(row.Permission, CommandPermission)
                    End If
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to load mapping: " & ex.Message,
                    "Edit Role Mapping", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End Try

            Dim allRoles = plugin.GetGuildRoles(guildItem.GuildId)
            Using dialog As New DiscordRoleMappingEditorForm(allRoles, roleId, existingName, existingPermission)
                If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                CommitMapping(guildItem.GuildId,
                              dialog.ResultRoleId,
                              dialog.ResultRoleName,
                              dialog.ResultPermission)
            End Using
        End Sub

        Private Sub OnRemove(sender As Object, e As EventArgs)
            If _mappingsList.SelectedItems.Count = 0 Then Return
            Dim roleId = TryCast(_mappingsList.SelectedItems(0).Tag, String)
            If String.IsNullOrEmpty(roleId) Then Return

            Dim guildItem = TryCast(_guildCombo.SelectedItem, GuildDropdownItem)
            If guildItem Is Nothing Then Return

            Dim roleName = _mappingsList.SelectedItems(0).Text
            Dim confirm = MessageBox.Show(
                $"Remove the mapping for ""{roleName}""? Members with this role will lose any elevation it granted.",
                "Remove Role Mapping", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If confirm <> DialogResult.Yes Then Return

            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = db.DiscordRoleMappings.
                        Where(Function(m) m.GuildId = guildItem.GuildId AndAlso m.PanelId = "" AndAlso m.RoleId = roleId).
                        FirstOrDefault()
                    If row IsNot Nothing Then
                        db.DiscordRoleMappings.Remove(row)
                        db.SaveChanges()
                    End If
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to remove mapping: " & ex.Message,
                    "Remove Role Mapping", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End Try

            ReloadBotCache()
            ReloadMappingsList()
        End Sub

        ' ============================================================
        '  DB write + cache reload (shared between Add and Edit)
        ' ============================================================

        Private Sub CommitMapping(guildId As String,
                                  roleId As String,
                                  roleName As String,
                                  permission As CommandPermission)
            If String.IsNullOrEmpty(guildId) OrElse String.IsNullOrEmpty(roleId) Then Return
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim now = DateTime.UtcNow
                    Dim row = db.DiscordRoleMappings.
                        Where(Function(m) m.GuildId = guildId AndAlso m.PanelId = "" AndAlso m.RoleId = roleId).
                        FirstOrDefault()
                    If row Is Nothing Then
                        ' Add: new row. PanelId left as the
                        ' empty-string default (entity initialiser)
                        ' — this form only manages guild-default
                        ' mappings.
                        row = New DiscordRoleMappingEntity With {
                            .GuildId = guildId,
                            .RoleId = roleId,
                            .CreatedUtc = now
                        }
                        db.DiscordRoleMappings.Add(row)
                    End If
                    ' Both Add and Edit refresh RoleName + Permission.
                    ' RoleName is a snapshot — keeping it in lockstep
                    ' with the role's current Discord name on every
                    ' save means the listview stays accurate without
                    ' a separate "refresh names" step.
                    row.RoleName = roleName
                    row.Permission = CInt(permission)
                    row.UpdatedUtc = now
                    db.SaveChanges()
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to save mapping: " & ex.Message,
                    "Save Role Mapping", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End Try

            ReloadBotCache()
            ReloadMappingsList()
        End Sub

        Private Sub ReloadBotCache()
            ' Sync-over-async on a Task.CompletedTask is essentially
            ' free, but wrap defensively — if the plugin's reload
            ' implementation later grows real async work and throws,
            ' we still want the UI to feel responsive.
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

        Private Class GuildDropdownItem
            Public ReadOnly GuildId As String
            Public ReadOnly DisplayName As String
            Public ReadOnly IsConnected As Boolean
            Public Sub New(guildId As String, displayName As String, isConnected As Boolean)
                Me.GuildId = guildId
                Me.DisplayName = displayName
                Me.IsConnected = isConnected
            End Sub
            Public Overrides Function ToString() As String
                Return DisplayName
            End Function
        End Class

    End Class

End Namespace
