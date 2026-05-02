Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Notification

' ============================================================
'  NotificationsForm — manages Discord webhook destinations.
'
'  Layout: ListView on the left showing all destinations, a
'  details panel on the right for editing the selected one.
'  Changes are only written to the DB on Save; the form keeps
'  an in-memory edit model so the user can cancel.
'
'  Once saved, we call DiscordWebhookPlugin.RefreshConfigAsync
'  so the live plugin picks up the changes without a restart.
' ============================================================

Namespace GSM.Manager.UI

    Public Class NotificationsForm
        Inherits Form

        Private _destList As ListView
        Private _detailsPanel As Panel
        Private _nameTextBox As TextBox
        Private _webhookTextBox As TextBox
        Private _enabledCheckBox As CheckBox
        Private _installCheckList As CheckedListBox
        Private _instanceSelectorsContainer As Panel
        Private _eventChecks As Dictionary(Of NotificationEventType, CheckBox)
        Private _profileCombo As ComboBox
        Private _manageProfilesButton As Button
        Private _customizeTemplatesButton As Button
        Private _testButton As Button
        Private _addButton As Button
        Private _removeButton As Button
        Private _saveButton As Button
        Private _closeButton As Button

        ' Edit model — in-memory working copies until Save.
        Private _destinations As New List(Of DestinationEdit)
        Private _selectedDestination As DestinationEdit
        Private _allInstallations As New List(Of InstallationEntity)
        Private _suppressEvents As Boolean = False

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            LoadDataAsync()
        End Sub

        ' ---- UI construction ----

        Private Sub InitializeControls()
            Me.Text = "Notifications"
            Me.Size = New Size(980, 720)
            Me.StartPosition = FormStartPosition.CenterParent
            Me.MinimumSize = New Size(900, 600)

            ' Left pane: destination list + Add/Remove/Test buttons
            Dim leftPanel As New Panel() With {.Dock = DockStyle.Left, .Width = 260, .Padding = New Padding(8)}

            _destList = New ListView() With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .HideSelection = False,
                .MultiSelect = False,
                .CheckBoxes = False
            }
            _destList.Columns.Add("Destination", 240)
            AddHandler _destList.SelectedIndexChanged, AddressOf OnDestinationSelected

            Dim leftButtonRow As New Panel() With {.Dock = DockStyle.Bottom, .Height = 40}
            _addButton = New Button() With {.Text = "Add", .Location = New Point(0, 5), .Size = New Size(70, 28)}
            AddHandler _addButton.Click, AddressOf OnAddClicked
            _removeButton = New Button() With {.Text = "Remove", .Location = New Point(75, 5), .Size = New Size(70, 28)}
            AddHandler _removeButton.Click, AddressOf OnRemoveClicked
            _testButton = New Button() With {.Text = "Test", .Location = New Point(150, 5), .Size = New Size(70, 28)}
            AddHandler _testButton.Click, AddressOf OnTestClicked
            leftButtonRow.Controls.AddRange(New Control() {_addButton, _removeButton, _testButton})

            leftPanel.Controls.Add(_destList)
            leftPanel.Controls.Add(leftButtonRow)

            ' Right pane: details editor
            _detailsPanel = New Panel() With {
                .Dock = DockStyle.Fill,
                .Padding = New Padding(16, 8, 16, 8),
                .AutoScroll = True
            }
            BuildDetailsPanel()

            ' Bottom: Save/Close
            Dim footer As New Panel() With {.Dock = DockStyle.Bottom, .Height = 48, .Padding = New Padding(8)}
            _saveButton = New Button() With {.Text = "Save", .Size = New Size(100, 30), .Dock = DockStyle.Right}
            AddHandler _saveButton.Click, AddressOf OnSaveClicked
            _closeButton = New Button() With {.Text = "Close", .Size = New Size(100, 30), .Dock = DockStyle.Right}
            _closeButton.DialogResult = DialogResult.Cancel
            footer.Controls.Add(_saveButton)
            footer.Controls.Add(_closeButton)
            Me.CancelButton = _closeButton

            Me.Controls.Add(_detailsPanel)
            Me.Controls.Add(leftPanel)
            Me.Controls.Add(footer)
        End Sub

        Private Sub BuildDetailsPanel()
            Dim y = 8

            AddSectionHeader("Destination", y) : y += 28

            AddFieldLabel("Name:", y)
            _nameTextBox = New TextBox() With {.Location = New Point(150, y), .Size = New Size(440, 24)}
            AddHandler _nameTextBox.TextChanged, AddressOf OnNameChanged
            _detailsPanel.Controls.Add(_nameTextBox)
            y += 32

            AddFieldLabel("Webhook URL:", y)
            _webhookTextBox = New TextBox() With {.Location = New Point(150, y), .Size = New Size(440, 24)}
            AddHandler _webhookTextBox.TextChanged, AddressOf OnWebhookChanged
            _detailsPanel.Controls.Add(_webhookTextBox)
            y += 32

            _enabledCheckBox = New CheckBox() With {
                .Text = "Enabled", .AutoSize = True,
                .Location = New Point(150, y)
            }
            AddHandler _enabledCheckBox.CheckedChanged, AddressOf OnEnabledCheckBoxChanged
            _detailsPanel.Controls.Add(_enabledCheckBox)
            y += 34

            AddSectionHeader("Scope — installations & instances", y) : y += 26
            Dim hint As New Label() With {
                .Text = "Check installations below; within each, pick instances (or leave all deselected to include every instance). No installation checked = all installations.",
                .Location = New Point(20, y), .Size = New Size(600, 32),
                .ForeColor = Color.DimGray,
                .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic)
            }
            _detailsPanel.Controls.Add(hint)
            y += 38

            AddFieldLabel("Installations:", y)
            _installCheckList = New CheckedListBox() With {
                .Location = New Point(150, y), .Size = New Size(220, 140),
                .CheckOnClick = True
            }
            AddHandler _installCheckList.ItemCheck, AddressOf OnInstallationChecked
            _detailsPanel.Controls.Add(_installCheckList)

            Dim instancesLabel As New Label() With {
                .Text = "Instances:", .AutoSize = True,
                .Location = New Point(380, y), .Font = New Font("Segoe UI", 9)
            }
            _detailsPanel.Controls.Add(instancesLabel)
            _instanceSelectorsContainer = New Panel() With {
                .Location = New Point(380, y + 18),
                .Size = New Size(420, 140),
                .BorderStyle = BorderStyle.FixedSingle,
                .AutoScroll = True,
                .BackColor = Color.White
            }
            _detailsPanel.Controls.Add(_instanceSelectorsContainer)
            y += 150

            AddSectionHeader("Events", y) : y += 26
            BuildEventCheckboxes(y)
            y += 120

            AddSectionHeader("Visibility & templates", y) : y += 26

            AddFieldLabel("Profile:", y)
            _profileCombo = New ComboBox() With {
                .Location = New Point(150, y), .Size = New Size(220, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            AddHandler _profileCombo.SelectedIndexChanged, AddressOf OnProfileChanged
            _detailsPanel.Controls.Add(_profileCombo)

            _manageProfilesButton = New Button() With {
                .Text = "Manage Profiles...", .Location = New Point(380, y - 1), .Size = New Size(140, 26)
            }
            AddHandler _manageProfilesButton.Click, AddressOf OnManageProfilesClicked
            _detailsPanel.Controls.Add(_manageProfilesButton)
            y += 34

            AddFieldLabel("Templates:", y)
            _customizeTemplatesButton = New Button() With {
                .Text = "Customize Message Templates...",
                .Location = New Point(150, y - 1), .Size = New Size(240, 26)
            }
            AddHandler _customizeTemplatesButton.Click, AddressOf OnCustomizeTemplatesClicked
            _detailsPanel.Controls.Add(_customizeTemplatesButton)
        End Sub

        Private Sub AddSectionHeader(text As String, y As Integer)
            Dim lbl As New Label() With {
                .Text = text, .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .AutoSize = True, .Location = New Point(0, y),
                .ForeColor = Color.FromArgb(50, 50, 120)
            }
            _detailsPanel.Controls.Add(lbl)
        End Sub

        Private Sub AddFieldLabel(text As String, y As Integer)
            Dim lbl As New Label() With {
                .Text = text, .AutoSize = True,
                .Location = New Point(20, y + 4)
            }
            _detailsPanel.Controls.Add(lbl)
        End Sub

        Private Sub BuildEventCheckboxes(startY As Integer)
            _eventChecks = New Dictionary(Of NotificationEventType, CheckBox)
            ' Only the event types the Discord integration currently
            ' cares about — skip Custom / AutomationRule* / UpdateAvailable
            ' (manual only per user preference) / Node events.
            Dim wanted = {
                NotificationEventType.InstanceStarted,
                NotificationEventType.InstanceStopped,
                NotificationEventType.InstanceCrashed,
                NotificationEventType.CrashLoopDetected,
                NotificationEventType.UpdateStarted,
                NotificationEventType.UpdateCompleted,
                NotificationEventType.UpdateFailed,
                NotificationEventType.PlayerJoined,
                NotificationEventType.PlayerLeft
            }
            Dim col = 0
            Dim row = 0
            For Each evt In wanted
                Dim cb As New CheckBox() With {
                    .Text = PrettifyEventName(evt),
                    .AutoSize = True,
                    .Location = New Point(20 + col * 200, startY + row * 24),
                    .Tag = evt
                }
                AddHandler cb.CheckedChanged, AddressOf OnEventCheckChanged
                _detailsPanel.Controls.Add(cb)
                _eventChecks(evt) = cb
                col += 1
                If col >= 3 Then
                    col = 0
                    row += 1
                End If
            Next
        End Sub

        Private Shared Function PrettifyEventName(e As NotificationEventType) As String
            Select Case e
                Case NotificationEventType.InstanceStarted : Return "Server started"
                Case NotificationEventType.InstanceStopped : Return "Server stopped"
                Case NotificationEventType.InstanceCrashed : Return "Server crashed"
                Case NotificationEventType.CrashLoopDetected : Return "Crash loop halted"
                Case NotificationEventType.UpdateStarted : Return "Update started"
                Case NotificationEventType.UpdateCompleted : Return "Update completed"
                Case NotificationEventType.UpdateFailed : Return "Update failed"
                Case NotificationEventType.PlayerJoined : Return "Player joined"
                Case NotificationEventType.PlayerLeft : Return "Player left"
                Case Else : Return e.ToString()
            End Select
        End Function

        ' ---- Data loading ----

        Private Async Sub LoadDataAsync()
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    ' Ensure built-in profiles exist
                    Await EnsureDefaultProfilesAsync(db)

                    _allInstallations = Await db.Installations.
                        Include(Function(i) i.Node).
                        Include(Function(i) i.Instances).
                        ToListAsync()

                    ' Load destinations into edit model
                    Dim destEntities = Await db.NotificationDestinations.
                        Where(Function(d) d.TransportKind = "DiscordWebhook").
                        ToListAsync()

                    _destinations = destEntities.Select(AddressOf DestinationEdit.FromEntity).ToList()

                    ' Populate profile dropdown
                    Dim profiles = Await db.VisibilityProfiles.ToListAsync()
                    _suppressEvents = True
                    _profileCombo.Items.Clear()
                    For Each p In profiles
                        _profileCombo.Items.Add(New ProfileItem(p.ProfileId, p.DisplayName))
                    Next
                    _suppressEvents = False
                End Using

                _suppressEvents = True
                _installCheckList.Items.Clear()
                For Each inst In _allInstallations
                    Dim label = $"{inst.DisplayName} ({If(inst.Node IsNot Nothing, inst.Node.DisplayName, "?")})"
                    _installCheckList.Items.Add(New InstallationItem(inst.InstallationId, label))
                Next
                _suppressEvents = False

                RefreshDestinationList()
                If _destinations.Count > 0 Then
                    _destList.Items(0).Selected = True
                Else
                    ClearDetails()
                End If
            Catch ex As Exception
                MessageBox.Show($"Failed to load notifications: {ex.Message}", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Sub

        Private Async Function EnsureDefaultProfilesAsync(db As GsmDbContext) As Task
            Dim existing = Await db.VisibilityProfiles.AnyAsync()
            If existing Then Return

            Dim publicFields As New List(Of String) From {
                "InstanceName", "NodeName", "InstallationName", "GameName",
                "PlayerName", "PlayerCount", "MaxPlayers",
                "MapPath", "TileName", "MatchState"
            }
            Dim adminFields As New List(Of String) From {
                "InstanceName", "NodeName", "InstallationName", "GameName",
                "PlayerName", "PlayerCount", "MaxPlayers", "PID", "ExitCode",
                "MapPath", "TileName", "TileId", "MatchState", "IPAddress",
                "Port", "InstallPath", "SteamID", "BuildId", "ErrorMessage",
                "CustomerKey", "ProviderKey", "RuleName", "Timestamp",
                "EventType", "Message", "CrashCount", "WindowMinutes"
            }

            db.VisibilityProfiles.Add(New VisibilityProfileEntity With {
                .ProfileId = "public",
                .DisplayName = "Public",
                .AllowedFieldsJson = JsonSerializer.Serialize(publicFields),
                .IsBuiltIn = True,
                .CreatedUtc = DateTime.UtcNow,
                .UpdatedUtc = DateTime.UtcNow
            })
            db.VisibilityProfiles.Add(New VisibilityProfileEntity With {
                .ProfileId = "admin",
                .DisplayName = "Admin (all fields)",
                .AllowedFieldsJson = JsonSerializer.Serialize(adminFields),
                .IsBuiltIn = True,
                .CreatedUtc = DateTime.UtcNow,
                .UpdatedUtc = DateTime.UtcNow
            })
            Await db.SaveChangesAsync()
        End Function

        ' ---- Destination list ----

        Private Sub RefreshDestinationList()
            _suppressEvents = True
            _destList.Items.Clear()
            For Each d In _destinations
                Dim item As New ListViewItem(d.DisplayName)
                item.Tag = d
                If Not d.Enabled Then item.ForeColor = Color.Gray
                _destList.Items.Add(item)
            Next
            _suppressEvents = False
        End Sub

        Private Sub OnDestinationSelected(sender As Object, e As EventArgs)
            If _suppressEvents Then Return
            If _destList.SelectedItems.Count = 0 Then
                ClearDetails()
                Return
            End If
            _selectedDestination = TryCast(_destList.SelectedItems(0).Tag, DestinationEdit)
            LoadDetailsFromSelection()
        End Sub

        Private Sub LoadDetailsFromSelection()
            _suppressEvents = True
            Try
                If _selectedDestination Is Nothing Then
                    ClearDetails()
                    Return
                End If

                _nameTextBox.Text = If(_selectedDestination.DisplayName, "")
                _webhookTextBox.Text = If(_selectedDestination.WebhookUrl, "")
                _enabledCheckBox.Checked = _selectedDestination.Enabled

                ' Installations
                For i = 0 To _installCheckList.Items.Count - 1
                    Dim item = TryCast(_installCheckList.Items(i), InstallationItem)
                    Dim checked = item IsNot Nothing AndAlso
                                   _selectedDestination.InstallationFilter.Contains(item.InstallationId)
                    _installCheckList.SetItemChecked(i, checked)
                Next

                ' Build the per-installation instance selectors based on
                ' currently-checked installations.
                RebuildInstanceSelectors()

                ' Events
                For Each kvp In _eventChecks
                    kvp.Value.Checked = _selectedDestination.EnabledEventTypes.Contains(kvp.Key)
                Next

                ' Profile
                Dim profileIdx = 0
                For i = 0 To _profileCombo.Items.Count - 1
                    Dim p = TryCast(_profileCombo.Items(i), ProfileItem)
                    If p IsNot Nothing AndAlso p.ProfileId = _selectedDestination.VisibilityProfileId Then
                        profileIdx = i
                        Exit For
                    End If
                Next
                If _profileCombo.Items.Count > 0 Then _profileCombo.SelectedIndex = profileIdx
            Finally
                _suppressEvents = False
            End Try
        End Sub

        Private Sub ClearDetails()
            _suppressEvents = True
            Try
                _selectedDestination = Nothing
                _nameTextBox.Text = ""
                _webhookTextBox.Text = ""
                _enabledCheckBox.Checked = False
                For i = 0 To _installCheckList.Items.Count - 1
                    _installCheckList.SetItemChecked(i, False)
                Next
                _instanceSelectorsContainer.Controls.Clear()
                For Each cb In _eventChecks.Values
                    cb.Checked = False
                Next
                If _profileCombo.Items.Count > 0 Then _profileCombo.SelectedIndex = -1
            Finally
                _suppressEvents = False
            End Try
        End Sub

        ' ---- Instance selectors: per-installation scrolling listbox ----

        Private Sub RebuildInstanceSelectors()
            _instanceSelectorsContainer.Controls.Clear()
            If _selectedDestination Is Nothing Then Return

            Dim y = 4
            For i = 0 To _installCheckList.Items.Count - 1
                If Not _installCheckList.GetItemChecked(i) Then Continue For
                Dim item = TryCast(_installCheckList.Items(i), InstallationItem)
                If item Is Nothing Then Continue For
                Dim installation = _allInstallations.FirstOrDefault(Function(x) x.InstallationId = item.InstallationId)
                If installation Is Nothing Then Continue For

                Dim header As New Label() With {
                    .Text = installation.DisplayName & ":",
                    .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                    .AutoSize = True,
                    .Location = New Point(6, y)
                }
                _instanceSelectorsContainer.Controls.Add(header)
                y += 18

                Dim listBox As New CheckedListBox() With {
                    .Location = New Point(6, y),
                    .Size = New Size(394, 80),
                    .CheckOnClick = True,
                    .Tag = installation.InstallationId
                }
                If installation.Instances IsNot Nothing Then
                    For Each inst In installation.Instances
                        Dim idx = listBox.Items.Add(New InstanceItem(inst.InstanceId, inst.DisplayName))
                        If _selectedDestination.InstanceFilter.Contains(inst.InstanceId) Then
                            listBox.SetItemChecked(idx, True)
                        End If
                    Next
                End If
                AddHandler listBox.ItemCheck, AddressOf OnInstanceItemChecked
                _instanceSelectorsContainer.Controls.Add(listBox)
                y += 86
            Next
        End Sub

        Private Sub OnInstanceItemChecked(sender As Object, e As ItemCheckEventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            Dim lb = DirectCast(sender, CheckedListBox)
            Dim item = TryCast(lb.Items(e.Index), InstanceItem)
            If item Is Nothing Then Return
            If e.NewValue = CheckState.Checked Then
                _selectedDestination.InstanceFilter.Add(item.InstanceId)
            Else
                _selectedDestination.InstanceFilter.Remove(item.InstanceId)
            End If
        End Sub

        ' ---- Event handlers ----

        Private Sub OnNameChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            _selectedDestination.DisplayName = _nameTextBox.Text
            For Each item As ListViewItem In _destList.Items
                If item.Tag Is _selectedDestination Then
                    item.Text = _selectedDestination.DisplayName
                    Exit For
                End If
            Next
        End Sub

        Private Sub OnWebhookChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            _selectedDestination.WebhookUrl = _webhookTextBox.Text
        End Sub

        Private Sub OnEnabledCheckBoxChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            _selectedDestination.Enabled = _enabledCheckBox.Checked
            For Each item As ListViewItem In _destList.Items
                If item.Tag Is _selectedDestination Then
                    item.ForeColor = If(_selectedDestination.Enabled, Color.Black, Color.Gray)
                    Exit For
                End If
            Next
        End Sub

        Private Sub OnInstallationChecked(sender As Object, e As ItemCheckEventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            Dim item = TryCast(_installCheckList.Items(e.Index), InstallationItem)
            If item Is Nothing Then Return

            If e.NewValue = CheckState.Checked Then
                _selectedDestination.InstallationFilter.Add(item.InstallationId)
            Else
                _selectedDestination.InstallationFilter.Remove(item.InstallationId)
                ' When unchecking an installation, drop its instances
                ' from the instance filter too — otherwise stale IDs
                ' leak forward unexpectedly.
                Dim install = _allInstallations.FirstOrDefault(Function(x) x.InstallationId = item.InstallationId)
                If install IsNot Nothing AndAlso install.Instances IsNot Nothing Then
                    For Each inst In install.Instances
                        _selectedDestination.InstanceFilter.Remove(inst.InstanceId)
                    Next
                End If
            End If

            ' Defer to fire after the check state is actually applied
            Me.BeginInvoke(Sub() RebuildInstanceSelectors())
        End Sub

        Private Sub OnEventCheckChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            Dim cb = DirectCast(sender, CheckBox)
            Dim evt = DirectCast(cb.Tag, NotificationEventType)
            If cb.Checked Then
                _selectedDestination.EnabledEventTypes.Add(evt)
            Else
                _selectedDestination.EnabledEventTypes.Remove(evt)
            End If
        End Sub

        Private Sub OnProfileChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            Dim p = TryCast(_profileCombo.SelectedItem, ProfileItem)
            If p IsNot Nothing Then _selectedDestination.VisibilityProfileId = p.ProfileId
        End Sub

        Private Sub OnAddClicked(sender As Object, e As EventArgs)
            Dim d As New DestinationEdit() With {
                .DestinationId = Guid.NewGuid().ToString("N"),
                .DisplayName = "New Destination",
                .Enabled = True,
                .VisibilityProfileId = "public"
            }
            _destinations.Add(d)
            RefreshDestinationList()
            ' Select the new one
            For Each item As ListViewItem In _destList.Items
                If item.Tag Is d Then
                    item.Selected = True
                    _nameTextBox.Focus()
                    _nameTextBox.SelectAll()
                    Exit For
                End If
            Next
        End Sub

        Private Sub OnRemoveClicked(sender As Object, e As EventArgs)
            If _selectedDestination Is Nothing Then Return
            Dim result = MessageBox.Show(
                $"Remove destination '{_selectedDestination.DisplayName}'?",
                "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If result <> DialogResult.Yes Then Return
            _destinations.Remove(_selectedDestination)
            _selectedDestination = Nothing
            RefreshDestinationList()
            ClearDetails()
        End Sub

        Private Async Sub OnTestClicked(sender As Object, e As EventArgs)
            If _selectedDestination Is Nothing Then Return
            If String.IsNullOrWhiteSpace(_selectedDestination.WebhookUrl) Then
                MessageBox.Show("Enter a webhook URL first.", "Test",
                                MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            _testButton.Enabled = False
            _testButton.Text = "Sending..."
            Try
                Dim plugin = ManagerProgram.Services.GetService(Of DiscordWebhookPlugin)()
                If plugin Is Nothing Then
                    MessageBox.Show("Discord plugin not registered.", "Test",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error)
                    Return
                End If
                Dim err = Await plugin.SendTestAsync(
                    _selectedDestination.WebhookUrl,
                    _selectedDestination.DisplayName,
                    CancellationToken.None)
                If String.IsNullOrEmpty(err) Then
                    MessageBox.Show("Test message sent successfully.", "Test",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information)
                Else
                    MessageBox.Show($"Test failed: {err}", "Test",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error)
                End If
            Finally
                _testButton.Text = "Test"
                _testButton.Enabled = True
            End Try
        End Sub

        Private Sub OnManageProfilesClicked(sender As Object, e As EventArgs)
            Using dlg As New VisibilityProfileEditorForm()
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    ' Reload profiles into combo
                    ReloadProfilesAsync()
                End If
            End Using
        End Sub

        Private Async Sub ReloadProfilesAsync()
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim profiles = Await db.VisibilityProfiles.ToListAsync()
                    Dim currentId = If(_selectedDestination IsNot Nothing,
                                        _selectedDestination.VisibilityProfileId, "")
                    _suppressEvents = True
                    _profileCombo.Items.Clear()
                    Dim idx = 0
                    Dim selectIdx = 0
                    For Each p In profiles
                        _profileCombo.Items.Add(New ProfileItem(p.ProfileId, p.DisplayName))
                        If p.ProfileId = currentId Then selectIdx = idx
                        idx += 1
                    Next
                    If _profileCombo.Items.Count > 0 Then _profileCombo.SelectedIndex = selectIdx
                    _suppressEvents = False
                End Using
            Catch ex As Exception
                MessageBox.Show($"Failed to reload profiles: {ex.Message}")
            End Try
        End Sub

        Private Sub OnCustomizeTemplatesClicked(sender As Object, e As EventArgs)
            If _selectedDestination Is Nothing Then
                MessageBox.Show("Select a destination first.")
                Return
            End If
            Using dlg As New TemplateEditorForm(_selectedDestination.TemplateOverrides)
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    _selectedDestination.TemplateOverrides = dlg.ResultTemplates
                End If
            End Using
        End Sub

        ' ---- Save ----

        Private Async Sub OnSaveClicked(sender As Object, e As EventArgs)
            _saveButton.Enabled = False
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    Dim existingIds = Await db.NotificationDestinations.
                        Where(Function(d) d.TransportKind = "DiscordWebhook").
                        Select(Function(d) d.DestinationId).ToListAsync()

                    Dim editIds = _destinations.Select(Function(d) d.DestinationId).ToHashSet()

                    ' Deletions
                    For Each id In existingIds
                        If Not editIds.Contains(id) Then
                            Dim ent = Await db.NotificationDestinations.FindAsync(id)
                            If ent IsNot Nothing Then db.NotificationDestinations.Remove(ent)
                        End If
                    Next

                    ' Upserts
                    For Each d In _destinations
                        Dim ent = Await db.NotificationDestinations.FindAsync(d.DestinationId)
                        If ent Is Nothing Then
                            ent = New NotificationDestinationEntity() With {
                                .DestinationId = d.DestinationId,
                                .TransportKind = "DiscordWebhook",
                                .CreatedUtc = DateTime.UtcNow
                            }
                            db.NotificationDestinations.Add(ent)
                        End If
                        ent.DisplayName = d.DisplayName
                        ent.Enabled = d.Enabled
                        ent.TransportConfigJson = JsonSerializer.Serialize(
                            New Dictionary(Of String, String) From {{"WebhookUrl", If(d.WebhookUrl, "")}})
                        ent.EnabledEventTypesJson = JsonSerializer.Serialize(
                            d.EnabledEventTypes.Select(Function(x) x.ToString()).ToList())
                        ent.InstallationFilterJson = JsonSerializer.Serialize(d.InstallationFilter.ToList())
                        ent.InstanceFilterJson = JsonSerializer.Serialize(d.InstanceFilter.ToList())
                        ent.VisibilityProfileId = d.VisibilityProfileId
                        If d.TemplateOverrides Is Nothing OrElse d.TemplateOverrides.Count = 0 Then
                            ent.TemplateOverridesJson = Nothing
                        Else
                            Dim asStrings = d.TemplateOverrides.ToDictionary(
                                Function(kv) kv.Key.ToString(),
                                Function(kv) kv.Value)
                            ent.TemplateOverridesJson = JsonSerializer.Serialize(asStrings)
                        End If
                        ent.UpdatedUtc = DateTime.UtcNow
                    Next

                    Await db.SaveChangesAsync()
                End Using

                ' Reload the live plugin cache.
                Dim plugin = ManagerProgram.Services.GetService(Of DiscordWebhookPlugin)()
                If plugin IsNot Nothing Then Await plugin.RefreshConfigAsync()

                Me.DialogResult = DialogResult.OK
                Me.Close()
            Catch ex As Exception
                MessageBox.Show($"Save failed: {ex.Message}", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error)
            Finally
                _saveButton.Enabled = True
            End Try
        End Sub

        ' ---- Internal edit model + combo item holders ----

        Private Class DestinationEdit
            Public Property DestinationId As String
            Public Property DisplayName As String
            Public Property Enabled As Boolean
            Public Property WebhookUrl As String
            Public Property VisibilityProfileId As String
            Public Property EnabledEventTypes As New HashSet(Of NotificationEventType)
            Public Property InstallationFilter As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Public Property InstanceFilter As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Public Property TemplateOverrides As Dictionary(Of NotificationEventType, String)

            Public Shared Function FromEntity(e As NotificationDestinationEntity) As DestinationEdit
                Dim edit As New DestinationEdit() With {
                    .DestinationId = e.DestinationId,
                    .DisplayName = e.DisplayName,
                    .Enabled = e.Enabled,
                    .VisibilityProfileId = e.VisibilityProfileId
                }
                ' Webhook URL
                If Not String.IsNullOrEmpty(e.TransportConfigJson) Then
                    Try
                        Dim d = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(e.TransportConfigJson)
                        If d IsNot Nothing Then
                            Dim w As String = Nothing
                            d.TryGetValue("WebhookUrl", w)
                            edit.WebhookUrl = w
                        End If
                    Catch
                    End Try
                End If
                edit.EnabledEventTypes = ParseEnumSet(e.EnabledEventTypesJson)
                edit.InstallationFilter = ParseStringSet(e.InstallationFilterJson)
                edit.InstanceFilter = ParseStringSet(e.InstanceFilterJson)
                edit.TemplateOverrides = ParseTemplateOverrides(e.TemplateOverridesJson)
                Return edit
            End Function

            Private Shared Function ParseEnumSet(json As String) As HashSet(Of NotificationEventType)
                Dim result As New HashSet(Of NotificationEventType)
                If String.IsNullOrEmpty(json) Then Return result
                Try
                    Dim list = JsonSerializer.Deserialize(Of List(Of String))(json)
                    If list IsNot Nothing Then
                        For Each n In list
                            Dim v As NotificationEventType
                            If [Enum].TryParse(n, True, v) Then result.Add(v)
                        Next
                    End If
                Catch
                End Try
                Return result
            End Function

            Private Shared Function ParseStringSet(json As String) As HashSet(Of String)
                Dim result As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                If String.IsNullOrEmpty(json) Then Return result
                Try
                    Dim list = JsonSerializer.Deserialize(Of List(Of String))(json)
                    If list IsNot Nothing Then
                        For Each v In list
                            If Not String.IsNullOrWhiteSpace(v) Then result.Add(v)
                        Next
                    End If
                Catch
                End Try
                Return result
            End Function

            Private Shared Function ParseTemplateOverrides(json As String) As Dictionary(Of NotificationEventType, String)
                Dim result As New Dictionary(Of NotificationEventType, String)
                If String.IsNullOrEmpty(json) Then Return result
                Try
                    Dim raw = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(json)
                    If raw IsNot Nothing Then
                        For Each kvp In raw
                            Dim v As NotificationEventType
                            If [Enum].TryParse(kvp.Key, True, v) Then result(v) = kvp.Value
                        Next
                    End If
                Catch
                End Try
                Return result
            End Function
        End Class

        Private Class InstallationItem
            Public ReadOnly InstallationId As String
            Public ReadOnly Label As String
            Public Sub New(id As String, label As String)
                Me.InstallationId = id
                Me.Label = label
            End Sub
            Public Overrides Function ToString() As String
                Return Label
            End Function
        End Class

        Private Class InstanceItem
            Public ReadOnly InstanceId As String
            Public ReadOnly Label As String
            Public Sub New(id As String, label As String)
                Me.InstanceId = id
                Me.Label = label
            End Sub
            Public Overrides Function ToString() As String
                Return Label
            End Function
        End Class

        Private Class ProfileItem
            Public ReadOnly ProfileId As String
            Public ReadOnly Label As String
            Public Sub New(id As String, label As String)
                Me.ProfileId = id
                Me.Label = label
            End Sub
            Public Overrides Function ToString() As String
                Return Label
            End Function
        End Class

    End Class

End Namespace