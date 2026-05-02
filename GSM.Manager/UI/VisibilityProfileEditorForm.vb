Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Text.Json
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Data

' ============================================================
'  VisibilityProfileEditorForm — CRUD over VisibilityProfileEntity.
'  Each profile is a name plus a checklist of allowed field names.
'  Built-in profiles (Public / Admin) can be edited but not deleted.
' ============================================================

Namespace GSM.Manager.UI

    Public Class VisibilityProfileEditorForm
        Inherits Form

        ' Master list of field names that can appear in notifications.
        ' Keep this in sync with what NotificationTokens exposes and
        ' what NotificationEmitter writes to CustomTokens.
        Private Shared ReadOnly AllFields As String() = {
            "InstanceName", "NodeName", "InstallationName", "GameName",
            "PlayerName", "PlayerCount", "MaxPlayers",
            "PID", "ExitCode",
            "MapPath", "TileName", "TileId", "MatchState",
            "IPAddress", "Port", "InstallPath", "SteamID",
            "BuildId", "ErrorMessage",
            "CustomerKey", "ProviderKey",
            "RuleName", "Timestamp", "EventType", "Message",
            "CrashCount", "WindowMinutes"
        }

        Private _profileList As ListBox
        Private _nameTextBox As TextBox
        Private _fieldChecks As CheckedListBox
        Private _addButton As Button
        Private _removeButton As Button
        Private _saveButton As Button
        Private _closeButton As Button

        Private _profiles As New List(Of ProfileEdit)
        Private _selected As ProfileEdit
        Private _suppressEvents As Boolean = False

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            LoadProfilesAsync()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Visibility Profiles"
            Me.Size = New Size(640, 560)
            Me.StartPosition = FormStartPosition.CenterParent

            ' Left: profile list
            Dim leftPanel As New Panel() With {.Dock = DockStyle.Left, .Width = 200, .Padding = New Padding(8)}

            _profileList = New ListBox() With {.Dock = DockStyle.Fill}
            AddHandler _profileList.SelectedIndexChanged, AddressOf OnProfileSelected

            Dim leftBtnRow As New Panel() With {.Dock = DockStyle.Bottom, .Height = 38}
            _addButton = New Button() With {.Text = "Add", .Location = New Point(0, 4), .Size = New Size(85, 28)}
            AddHandler _addButton.Click, AddressOf OnAddClicked
            _removeButton = New Button() With {.Text = "Remove", .Location = New Point(90, 4), .Size = New Size(85, 28)}
            AddHandler _removeButton.Click, AddressOf OnRemoveClicked
            leftBtnRow.Controls.AddRange(New Control() {_addButton, _removeButton})

            leftPanel.Controls.Add(_profileList)
            leftPanel.Controls.Add(leftBtnRow)

            ' Right: profile details
            Dim rightPanel As New Panel() With {.Dock = DockStyle.Fill, .Padding = New Padding(16, 8, 16, 8)}

            Dim nameLabel As New Label() With {.Text = "Name:", .Location = New Point(0, 8), .AutoSize = True}
            rightPanel.Controls.Add(nameLabel)
            _nameTextBox = New TextBox() With {.Location = New Point(60, 5), .Size = New Size(320, 24)}
            AddHandler _nameTextBox.TextChanged, AddressOf OnNameChanged
            rightPanel.Controls.Add(_nameTextBox)

            Dim fieldsLbl As New Label() With {
                .Text = "Fields allowed in notifications:",
                .Location = New Point(0, 40), .AutoSize = True,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold)
            }
            rightPanel.Controls.Add(fieldsLbl)

            _fieldChecks = New CheckedListBox() With {
                .Location = New Point(0, 62),
                .Size = New Size(380, 380),
                .CheckOnClick = True
            }
            AddHandler _fieldChecks.ItemCheck, AddressOf OnFieldChecked
            rightPanel.Controls.Add(_fieldChecks)

            ' Footer
            Dim footer As New Panel() With {.Dock = DockStyle.Bottom, .Height = 48, .Padding = New Padding(8)}
            _saveButton = New Button() With {.Text = "Save", .Size = New Size(100, 30), .Dock = DockStyle.Right}
            AddHandler _saveButton.Click, AddressOf OnSaveClicked
            _closeButton = New Button() With {.Text = "Cancel", .Size = New Size(100, 30), .Dock = DockStyle.Right}
            _closeButton.DialogResult = DialogResult.Cancel
            footer.Controls.Add(_saveButton)
            footer.Controls.Add(_closeButton)

            Me.Controls.Add(rightPanel)
            Me.Controls.Add(leftPanel)
            Me.Controls.Add(footer)
            Me.CancelButton = _closeButton
        End Sub

        Private Async Sub LoadProfilesAsync()
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim entities = Await db.VisibilityProfiles.ToListAsync()
                    _profiles = entities.Select(AddressOf ProfileEdit.FromEntity).ToList()
                End Using

                RefreshList()
                If _profiles.Count > 0 Then _profileList.SelectedIndex = 0
            Catch ex As Exception
                MessageBox.Show($"Failed to load profiles: {ex.Message}")
            End Try
        End Sub

        Private Sub RefreshList()
            _suppressEvents = True
            _profileList.Items.Clear()
            For Each p In _profiles
                _profileList.Items.Add(p)
            Next
            _suppressEvents = False
        End Sub

        Private Sub OnProfileSelected(sender As Object, e As EventArgs)
            If _suppressEvents Then Return
            _selected = TryCast(_profileList.SelectedItem, ProfileEdit)
            LoadSelectedIntoControls()
        End Sub

        Private Sub LoadSelectedIntoControls()
            _suppressEvents = True
            Try
                _fieldChecks.Items.Clear()
                If _selected Is Nothing Then
                    _nameTextBox.Text = ""
                    _nameTextBox.Enabled = False
                    _removeButton.Enabled = False
                    Return
                End If
                _nameTextBox.Text = _selected.DisplayName
                _nameTextBox.Enabled = True
                _removeButton.Enabled = Not _selected.IsBuiltIn
                For Each f In AllFields
                    Dim idx = _fieldChecks.Items.Add(f)
                    If _selected.AllowedFields.Contains(f) Then _fieldChecks.SetItemChecked(idx, True)
                Next
            Finally
                _suppressEvents = False
            End Try
        End Sub

        Private Sub OnNameChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selected Is Nothing Then Return
            _selected.DisplayName = _nameTextBox.Text
            ' Refresh the item in the list without re-selecting.
            Dim idx = _profileList.SelectedIndex
            If idx >= 0 Then
                _suppressEvents = True
                _profileList.Items(idx) = _selected
                _profileList.SelectedIndex = idx
                _suppressEvents = False
            End If
        End Sub

        Private Sub OnFieldChecked(sender As Object, e As ItemCheckEventArgs)
            If _suppressEvents OrElse _selected Is Nothing Then Return
            Dim name = DirectCast(_fieldChecks.Items(e.Index), String)
            If e.NewValue = CheckState.Checked Then
                _selected.AllowedFields.Add(name)
            Else
                _selected.AllowedFields.Remove(name)
            End If
        End Sub

        Private Sub OnAddClicked(sender As Object, e As EventArgs)
            Dim p As New ProfileEdit() With {
                .ProfileId = Guid.NewGuid().ToString("N"),
                .DisplayName = "New Profile",
                .IsBuiltIn = False
            }
            _profiles.Add(p)
            RefreshList()
            _profileList.SelectedIndex = _profiles.Count - 1
            _nameTextBox.Focus()
            _nameTextBox.SelectAll()
        End Sub

        Private Sub OnRemoveClicked(sender As Object, e As EventArgs)
            If _selected Is Nothing OrElse _selected.IsBuiltIn Then Return
            Dim result = MessageBox.Show(
                $"Remove profile '{_selected.DisplayName}'? " &
                "Destinations using it will fall back to no profile (all fields shown).",
                "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If result <> DialogResult.Yes Then Return
            _profiles.Remove(_selected)
            _selected = Nothing
            RefreshList()
            LoadSelectedIntoControls()
        End Sub

        Private Async Sub OnSaveClicked(sender As Object, e As EventArgs)
            _saveButton.Enabled = False
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    Dim existingIds = Await db.VisibilityProfiles.
                        Select(Function(p) p.ProfileId).ToListAsync()

                    Dim editIds = _profiles.Select(Function(p) p.ProfileId).ToHashSet()

                    For Each id In existingIds
                        If Not editIds.Contains(id) Then
                            Dim ent = Await db.VisibilityProfiles.FindAsync(id)
                            If ent IsNot Nothing AndAlso Not ent.IsBuiltIn Then
                                db.VisibilityProfiles.Remove(ent)
                            End If
                        End If
                    Next

                    For Each p In _profiles
                        Dim ent = Await db.VisibilityProfiles.FindAsync(p.ProfileId)
                        If ent Is Nothing Then
                            ent = New VisibilityProfileEntity() With {
                                .ProfileId = p.ProfileId,
                                .IsBuiltIn = p.IsBuiltIn,
                                .CreatedUtc = DateTime.UtcNow
                            }
                            db.VisibilityProfiles.Add(ent)
                        End If
                        ent.DisplayName = p.DisplayName
                        ent.AllowedFieldsJson = JsonSerializer.Serialize(p.AllowedFields.ToList())
                        ent.UpdatedUtc = DateTime.UtcNow
                    Next

                    Await db.SaveChangesAsync()
                End Using

                Me.DialogResult = DialogResult.OK
                Me.Close()
            Catch ex As Exception
                MessageBox.Show($"Save failed: {ex.Message}")
            Finally
                _saveButton.Enabled = True
            End Try
        End Sub

        ' ---- Edit model ----

        Private Class ProfileEdit
            Public Property ProfileId As String
            Public Property DisplayName As String
            Public Property IsBuiltIn As Boolean
            Public Property AllowedFields As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            Public Overrides Function ToString() As String
                If IsBuiltIn Then Return DisplayName & " (built-in)"
                Return DisplayName
            End Function

            Public Shared Function FromEntity(e As VisibilityProfileEntity) As ProfileEdit
                Dim p As New ProfileEdit() With {
                    .ProfileId = e.ProfileId,
                    .DisplayName = e.DisplayName,
                    .IsBuiltIn = e.IsBuiltIn
                }
                If Not String.IsNullOrEmpty(e.AllowedFieldsJson) Then
                    Try
                        Dim list = JsonSerializer.Deserialize(Of List(Of String))(e.AllowedFieldsJson)
                        If list IsNot Nothing Then
                            For Each f In list
                                p.AllowedFields.Add(f)
                            Next
                        End If
                    Catch
                    End Try
                End If
                Return p
            End Function
        End Class

    End Class

End Namespace