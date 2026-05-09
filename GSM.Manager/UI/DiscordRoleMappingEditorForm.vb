Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports GSM.Manager.Core
Imports GSM.Notification

' ============================================================
'  DiscordRoleMappingEditorForm — small modal picker for one
'  role-to-permission mapping (Phase 5d-3). Used by
'  DiscordRoleMappingsForm for both the Add and Edit flows.
'
'  Add mode (single-arg constructor): the operator is creating
'  a new mapping. The role dropdown is enabled and pre-filtered
'  by the parent form to roles that don't already have a mapping
'  in this guild. Permission defaults to ServerOperator (the
'  more common elevation tier).
'
'  Edit mode (four-arg constructor): the operator is changing
'  the permission for an existing mapping. The role dropdown
'  is pre-selected and disabled — switching the role would
'  effectively be "remove and re-add", which the operator
'  should do explicitly via the Remove + Add buttons. Only the
'  permission dropdown is editable.
'
'  Permission dropdown only offers ServerOperator and
'  Administrator. Everyone is the implicit default for unmapped
'  roles, so it never appears as a stored mapping value, and
'  surfacing it in the UI would only invite confusion.
'
'  OK populates ResultRoleId / ResultRoleName / ResultPermission;
'  Cancel leaves them at defaults and the parent treats the
'  dialog as a no-op.
' ============================================================

Namespace GSM.Manager.UI

    Public Class DiscordRoleMappingEditorForm
        Inherits Form

        Public Property ResultRoleId As String
        Public Property ResultRoleName As String
        Public Property ResultPermission As CommandPermission

        Private ReadOnly _availableRoles As IReadOnlyList(Of GuildRoleInfo)
        Private ReadOnly _editingRoleId As String  ' Nothing for Add, role ID for Edit
        Private ReadOnly _editingRoleName As String
        Private ReadOnly _initialPermission As CommandPermission

        Private _roleCombo As ComboBox
        Private _permissionCombo As ComboBox
        Private _okButton As Button
        Private _cancelButton As Button

        ''' <summary>
        ''' Add mode. availableRoles should contain only roles
        ''' that don't yet have a mapping in this guild — the
        ''' parent form filters before calling here.
        ''' </summary>
        Public Sub New(availableRoles As IReadOnlyList(Of GuildRoleInfo))
            _availableRoles = availableRoles
            _editingRoleId = Nothing
            _editingRoleName = Nothing
            _initialPermission = CommandPermission.ServerOperator
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
        End Sub

        ''' <summary>
        ''' Edit mode. allRoles is passed as a fallback so we can
        ''' look up the editing role's current Discord name; the
        ''' role dropdown ends up locked to editingRoleId regardless.
        ''' </summary>
        Public Sub New(allRoles As IReadOnlyList(Of GuildRoleInfo),
                       editingRoleId As String,
                       editingRoleName As String,
                       editingPermission As CommandPermission)
            _availableRoles = allRoles
            _editingRoleId = editingRoleId
            _editingRoleName = editingRoleName
            _initialPermission = editingPermission
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
        End Sub

        Private Sub InitializeControls()
            Me.Text = If(_editingRoleId Is Nothing, "Add Role Mapping", "Edit Role Mapping")
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MinimizeBox = False
            Me.MaximizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.Size = New Size(420, 200)

            Dim layout As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 3,
                .Padding = New Padding(10)
            }
            layout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 110))
            layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100))

            ' Role row
            layout.Controls.Add(MakeLabel("Role:"), 0, 0)
            _roleCombo = New ComboBox With {
                .Dock = DockStyle.Fill,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            For Each r In _availableRoles
                _roleCombo.Items.Add(New RoleDropdownItem(r.RoleId, r.Name))
            Next

            If _editingRoleId IsNot Nothing Then
                ' Edit mode: lock the dropdown to the role being
                ' edited. If for some reason the role doesn't appear
                ' in availableRoles (e.g. it was deleted from Discord
                ' since the mapping was created), inject a synthetic
                ' entry so the operator sees what they're editing
                ' rather than a blank dropdown.
                Dim found = False
                For i = 0 To _roleCombo.Items.Count - 1
                    Dim item = TryCast(_roleCombo.Items(i), RoleDropdownItem)
                    If item IsNot Nothing AndAlso item.RoleId = _editingRoleId Then
                        _roleCombo.SelectedIndex = i
                        found = True
                        Exit For
                    End If
                Next
                If Not found Then
                    Dim label = If(String.IsNullOrEmpty(_editingRoleName),
                                    $"(role no longer in Discord)",
                                    $"(role no longer in Discord) {_editingRoleName}")
                    _roleCombo.Items.Add(New RoleDropdownItem(_editingRoleId, label))
                    _roleCombo.SelectedIndex = _roleCombo.Items.Count - 1
                End If
                _roleCombo.Enabled = False
            ElseIf _roleCombo.Items.Count > 0 Then
                _roleCombo.SelectedIndex = 0
            End If
            layout.Controls.Add(_roleCombo, 1, 0)

            ' Permission row
            layout.Controls.Add(MakeLabel("Permission:"), 0, 1)
            _permissionCombo = New ComboBox With {
                .Dock = DockStyle.Fill,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            ' Only ServerOperator and Administrator are valid
            ' choices — Everyone is the implicit default for
            ' unmapped roles, so it never appears as a stored
            ' mapping. Including it would invite confusion ("did
            ' I just demote everyone with this role?").
            _permissionCombo.Items.Add(New PermissionDropdownItem(CommandPermission.ServerOperator, "Server Operator"))
            _permissionCombo.Items.Add(New PermissionDropdownItem(CommandPermission.Administrator, "Administrator"))
            For i = 0 To _permissionCombo.Items.Count - 1
                Dim item = TryCast(_permissionCombo.Items(i), PermissionDropdownItem)
                If item IsNot Nothing AndAlso item.Permission = _initialPermission Then
                    _permissionCombo.SelectedIndex = i
                    Exit For
                End If
            Next
            If _permissionCombo.SelectedIndex < 0 Then _permissionCombo.SelectedIndex = 0
            layout.Controls.Add(_permissionCombo, 1, 1)

            ' Buttons row
            Dim buttonPanel As New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.RightToLeft,
                .Padding = New Padding(0, 4, 0, 0)
            }
            _cancelButton = New Button With {
                .Text = "Cancel",
                .Width = 90,
                .DialogResult = DialogResult.Cancel
            }
            _okButton = New Button With {
                .Text = "OK",
                .Width = 90,
                .Margin = New Padding(0, 0, 8, 0)
            }
            AddHandler _okButton.Click, AddressOf OnOk
            buttonPanel.Controls.Add(_cancelButton)
            buttonPanel.Controls.Add(_okButton)
            layout.SetColumnSpan(buttonPanel, 2)
            layout.Controls.Add(buttonPanel, 0, 2)

            Me.Controls.Add(layout)
            Me.AcceptButton = _okButton
            Me.CancelButton = _cancelButton
        End Sub

        Private Sub OnOk(sender As Object, e As EventArgs)
            Dim role = TryCast(_roleCombo.SelectedItem, RoleDropdownItem)
            Dim perm = TryCast(_permissionCombo.SelectedItem, PermissionDropdownItem)
            If role Is Nothing OrElse perm Is Nothing Then
                MessageBox.Show("Pick a role and a permission level.",
                    Me.Text, MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            ResultRoleId = role.RoleId
            ResultRoleName = role.RoleName
            ResultPermission = perm.Permission
            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        Private Function MakeLabel(text As String) As Label
            Return New Label With {
                .Text = text,
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft
            }
        End Function

        Private Class RoleDropdownItem
            Public ReadOnly RoleId As String
            Public ReadOnly RoleName As String
            Public Sub New(roleId As String, roleName As String)
                Me.RoleId = roleId
                Me.RoleName = roleName
            End Sub
            Public Overrides Function ToString() As String
                Return RoleName
            End Function
        End Class

        Private Class PermissionDropdownItem
            Public ReadOnly Permission As CommandPermission
            Public ReadOnly DisplayLabel As String
            Public Sub New(permission As CommandPermission, displayLabel As String)
                Me.Permission = permission
                Me.DisplayLabel = displayLabel
            End Sub
            Public Overrides Function ToString() As String
                Return DisplayLabel
            End Function
        End Class

    End Class

End Namespace
