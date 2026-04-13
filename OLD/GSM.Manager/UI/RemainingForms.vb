Imports System.Collections.Generic
Imports System.Drawing
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports GSM.Core
Imports GSM.Data
Imports GSM.Plugin

' ============================================================
'  PluginStatusForm
'
'  Shows the result of a plugin reload: which files loaded,
'  which failed, compile errors, and orphaned records.
'  Used both after a manual reload and at startup if warnings exist.
' ============================================================


Friend Class RealmGameItem
    Public Property Id As String
    Public Property DisplayName As String

    Public Overrides Function ToString() As String
        Return DisplayName
    End Function
End Class

Public Class PluginStatusForm
    Inherits Form

    Public Sub New(summary As PluginReloadSummary)
        InitForm("Plugin Reload Results")
        BuildFromSummary(summary)
    End Sub

    Public Sub New(statuses As IReadOnlyList(Of PluginLoadStatus))
        InitForm("Plugin Status")
        BuildFromStatuses(statuses)
    End Sub

    Private Sub InitForm(title As String)
        Text = title
        Size = New Size(660, 480)
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.Sizable
        MinimizeBox = False
    End Sub

    Private Sub BuildFromSummary(summary As PluginReloadSummary)
        Dim txt As New RichTextBox With {
            .Dock = DockStyle.Fill,
            .ReadOnly = True,
            .Font = New Font("Consolas", 9),
            .BackColor = Color.FromArgb(20, 20, 20),
            .ForeColor = Color.White
        }

        AppendColoured(txt, summary.Message & vbNewLine & vbNewLine,
            If(summary.Outcome = ReloadOutcome.Success,
               Color.LightGreen, Color.Orange))

        ' File statuses
        AppendColoured(txt, "── Files ──" & vbNewLine, Color.Gray)
        For Each status In summary.FileStatuses
            Dim colour = If(status.State = PluginLoadState.Loaded,
                            Color.LightGreen, Color.Salmon)
            AppendColoured(txt, $"  [{status.State}] {status.FileName}{vbNewLine}", colour)
            Dim statusErrors As IEnumerable(Of String) = If(status.Errors, Array.Empty(Of String)())
            For Each statusError As String In statusErrors
                AppendColoured(txt, $"    {statusError}{vbNewLine}", Color.Salmon)
            Next
        Next

        ' Discovery errors
        If summary.DiscoveryErrors?.Any() = True Then
            AppendColoured(txt, vbNewLine & "── Discovery errors ──" & vbNewLine, Color.Gray)
            For Each discoveryMessage As String In summary.DiscoveryErrors
                AppendColoured(txt, $"  {discoveryMessage}{vbNewLine}", Color.Orange)
            Next
        End If

        ' Orphan warnings
        If summary.HasOrphans Then
            AppendColoured(txt, vbNewLine & "── Orphaned records ──" & vbNewLine, Color.Gray)
            AppendColoured(txt,
                "  These installations/instances have no matching plugin" &
                " and cannot be started:" & vbNewLine, Color.Orange)
            For Each orphanWarning As String In summary.OrphanWarnings
                AppendColoured(txt, $"  ⚠ {orphanWarning}{vbNewLine}", Color.Orange)
            Next
        End If

        Controls.Add(txt)
        AddCloseButton()
    End Sub

    Private Sub BuildFromStatuses(statuses As IReadOnlyList(Of PluginLoadStatus))
        Dim txt As New RichTextBox With {
            .Dock = DockStyle.Fill,
            .ReadOnly = True,
            .Font = New Font("Consolas", 9),
            .BackColor = Color.FromArgb(20, 20, 20),
            .ForeColor = Color.White
        }
        For Each status In statuses
            Dim colour = If(status.State = PluginLoadState.Loaded,
                            Color.LightGreen, Color.Salmon)
            AppendColoured(txt, $"[{status.State}] {status.FileName}", colour)
            If status.LoadedAt.HasValue Then
                AppendColoured(txt,
                    $"  (loaded {status.LoadedAt.Value:HH:mm:ss})", Color.Gray)
            End If
            AppendColoured(txt, vbNewLine, Color.White)
            Dim statusErrors As IEnumerable(Of String) = If(status.Errors, Array.Empty(Of String)())
            For Each statusError As String In statusErrors
                AppendColoured(txt, $"  {statusError}{vbNewLine}", Color.Salmon)
            Next
        Next
        Controls.Add(txt)
        AddCloseButton()
    End Sub

    Private Shared Sub AppendColoured(txt As RichTextBox,
                                       text As String,
                                       colour As Color)
        txt.SelectionStart = txt.TextLength
        txt.SelectionColor = colour
        txt.AppendText(text)
    End Sub

    Private Sub AddCloseButton()
        Dim btn As New Button With {
            .Text = "Close",
            .DialogResult = DialogResult.OK,
            .Dock = DockStyle.Bottom,
            .Height = 32,
            .FlatStyle = FlatStyle.Flat
        }
        Controls.Add(btn)
        AcceptButton = btn
    End Sub

End Class


' ============================================================
'  SettingsForm
'
'  Reads/writes the key-value Settings table in the DB.
'  Simple two-column grid: setting name, current value.
' ============================================================

Public Class SettingsForm
    Inherits Form

    Private ReadOnly _dbFactory As IDbContextFactory(Of GsmDbContext)
    Private _grid As DataGridView

    Public Sub New(dbFactory As IDbContextFactory(Of GsmDbContext))
        _dbFactory = dbFactory
        Text = "Settings"
        Size = New Size(500, 400)
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False

        _grid = New DataGridView With {
            .Dock = DockStyle.Fill,
            .AllowUserToAddRows = False,
            .AllowUserToDeleteRows = False,
            .RowHeadersVisible = False,
            .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        }
        _grid.Columns.Add(New DataGridViewTextBoxColumn With {
            .HeaderText = "Setting",
            .Name = "Key",
            .ReadOnly = True,
            .FillWeight = 45
        })
        _grid.Columns.Add(New DataGridViewTextBoxColumn With {
            .HeaderText = "Value",
            .Name = "Value",
            .FillWeight = 55
        })

        Dim btnSave As New Button With {
            .Text = "Save",
            .Dock = DockStyle.Bottom,
            .Height = 32,
            .FlatStyle = FlatStyle.Flat,
            .BackColor = Color.FromArgb(0, 100, 160),
            .ForeColor = Color.White
        }
        AddHandler btnSave.Click, AddressOf OnSaveClick

        Controls.Add(_grid)
        Controls.Add(btnSave)

        Task.Run(AddressOf LoadSettingsAsync)
    End Sub

    Private Async Function LoadSettingsAsync() As Task
        Using db = _dbFactory.CreateDbContext()
            Dim settings = Await db.Settings.
                OrderBy(Function(s) s.Key).
                ToListAsync()
            BeginInvoke(Sub()
                            For Each s In settings
                                _grid.Rows.Add(s.Key, s.Value)
                            Next
                        End Sub)
        End Using
    End Function

    Private Async Sub OnSaveClick(sender As Object, e As EventArgs)
        Task.Run(Async Function()
                     Using db = _dbFactory.CreateDbContext()
                         For Each row As DataGridViewRow In _grid.Rows
                             Dim key = row.Cells("Key").Value?.ToString()
                             Dim value = If(row.Cells("Value").Value?.ToString(), "")
                             If String.IsNullOrEmpty(key) Then Continue For
                             Dim setting = Await db.Settings.FindAsync(key)
                             If setting IsNot Nothing Then
                                 setting.Value = value
                                 setting.UpdatedAt = DateTime.UtcNow
                             End If
                         Next
                         Await db.SaveChangesAsync()
                     End Using
                     BeginInvoke(Sub()
                                     MessageBox.Show("Settings saved.", "Saved",
                                         MessageBoxButtons.OK, MessageBoxIcon.Information)
                                 End Sub)
                 End Function)
    End Sub

End Class


' ============================================================
'  SteamCredentialsForm
'
'  Lists, adds, and removes Steam account credentials.
'  Passwords are masked in the UI and encrypted via DPAPI.
' ============================================================

Public Class SteamCredentialsForm
    Inherits Form

    Private ReadOnly _credentialService As CredentialService
    Private ReadOnly _pluginRegistry As PluginRegistry
    Private _listView As ListView

    Public Sub New(credentialService As CredentialService,
                   pluginRegistry As PluginRegistry)
        _credentialService = credentialService
        _pluginRegistry = pluginRegistry

        Text = "Steam Accounts"
        Size = New Size(580, 420)
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.Sizable
        MinimizeBox = False

        BuildLayout()
        Task.Run(AddressOf LoadAsync)
    End Sub

    Private Sub BuildLayout()
        _listView = New ListView With {
            .View = View.Details,
            .FullRowSelect = True,
            .GridLines = True,
            .Location = New Point(0, 0),
            .Size = New Size(570, 310)
        }
        _listView.Columns.Add("Display name", 200)
        _listView.Columns.Add("Username", 160)
        _listView.Columns.Add("For game", 120)
        _listView.Columns.Add("Anonymous", 80)

        Dim btnAdd As New Button With {
            .Text = "Add Account...",
            .Location = New Point(8, 318),
            .Size = New Size(120, 28),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler btnAdd.Click, AddressOf OnAddClick

        Dim btnRemove As New Button With {
            .Text = "Remove",
            .Location = New Point(136, 318),
            .Size = New Size(88, 28),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler btnRemove.Click, AddressOf OnRemoveClick

        Dim btnClose As New Button With {
            .Text = "Close",
            .Location = New Point(470, 318),
            .Size = New Size(88, 28),
            .DialogResult = DialogResult.OK,
            .FlatStyle = FlatStyle.Flat
        }

        Controls.AddRange({_listView, btnAdd, btnRemove, btnClose})
    End Sub

    Private Async Function LoadAsync() As Task
        Dim creds = Await _credentialService.ListSteamCredentialsAsync(CancellationToken.None)
        BeginInvoke(Sub()
                        _listView.Items.Clear()
                        For Each c In creds
                            Dim item As New ListViewItem(c.DisplayName)
                            item.SubItems.Add(c.Username)
                            item.SubItems.Add(If(String.IsNullOrEmpty(c.GameId), "(any)", c.GameId))
                            item.SubItems.Add(If(c.IsAnonymous, "Yes", "No"))
                            item.Tag = c.CredentialId
                            _listView.Items.Add(item)
                        Next
                    End Sub)
    End Function

    Private Sub OnAddClick(sender As Object, e As EventArgs)
        Using dlg As New AddSteamCredentialDialog(_credentialService, _pluginRegistry)
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                Task.Run(AddressOf LoadAsync)
            End If
        End Using
    End Sub

    Private Sub OnRemoveClick(sender As Object, e As EventArgs)
        If _listView.SelectedItems.Count = 0 Then Return
        Dim id = _listView.SelectedItems(0).Tag?.ToString()
        If String.IsNullOrEmpty(id) Then Return

        If MessageBox.Show(
                "Remove this Steam account? This cannot be undone.",
                "Confirm Remove",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) <> DialogResult.Yes Then Return

        Task.Run(Async Function()
                     Try
                         Await _credentialService.DeleteSteamCredentialAsync(
                             id, CancellationToken.None)
                         Await LoadAsync()
                     Catch ex As Exception
                         BeginInvoke(Sub()
                                         MessageBox.Show(ex.Message, "Error",
                                             MessageBoxButtons.OK, MessageBoxIcon.Error)
                                     End Sub)
                     End Try
                 End Function)
    End Sub

End Class


' ---- Add Steam credential dialog ----

Friend Class AddSteamCredentialDialog
    Inherits Form

    Private ReadOnly _credentialService As CredentialService
    Private ReadOnly _pluginRegistry As PluginRegistry
    Private _txtName As TextBox
    Private _txtUsername As TextBox
    Private _txtPassword As TextBox
    Private _chkAnonymous As CheckBox
    Private _cboGame As ComboBox

    Public Sub New(credentialService As CredentialService,
                   pluginRegistry As PluginRegistry)
        _credentialService = credentialService
        _pluginRegistry = pluginRegistry
        Text = "Add Steam Account"
        Size = New Size(420, 300)
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False
        BuildLayout()
    End Sub

    Private Sub BuildLayout()
        Const L = 130
        Dim y = 16

        Controls.Add(New Label With {.Text = "Display name:", .Location = New Point(12, y + 3), .AutoSize = True})
        _txtName = New TextBox With {.Location = New Point(L, y), .Size = New Size(260, 23)}
        Controls.Add(_txtName)
        y += 36

        _chkAnonymous = New CheckBox With {
            .Text = "Anonymous (no account needed)",
            .Location = New Point(L, y),
            .AutoSize = True
        }
        AddHandler _chkAnonymous.CheckedChanged, Sub(s, e)
            _txtUsername.Enabled = Not _chkAnonymous.Checked
            _txtPassword.Enabled = Not _chkAnonymous.Checked
        End Sub
        Controls.Add(_chkAnonymous)
        y += 32

        Controls.Add(New Label With {.Text = "Username:", .Location = New Point(12, y + 3), .AutoSize = True})
        _txtUsername = New TextBox With {.Location = New Point(L, y), .Size = New Size(260, 23)}
        Controls.Add(_txtUsername)
        y += 36

        Controls.Add(New Label With {.Text = "Password:", .Location = New Point(12, y + 3), .AutoSize = True})
        _txtPassword = New TextBox With {.Location = New Point(L, y), .Size = New Size(260, 23), .PasswordChar = "●"c}
        Controls.Add(_txtPassword)
        y += 36

        Controls.Add(New Label With {.Text = "For game:", .Location = New Point(12, y + 3), .AutoSize = True})
        _cboGame = New ComboBox With {.Location = New Point(L, y), .Size = New Size(200, 23), .DropDownStyle = ComboBoxStyle.DropDownList}
        _cboGame.Items.Add(New GameItem("", "(Any game)"))
        For Each plugin In _pluginRegistry.GetAllPlugins()
            _cboGame.Items.Add(New GameItem(plugin.GameId, plugin.DisplayName))
        Next
        _cboGame.SelectedIndex = 0
        Controls.Add(_cboGame)
        y += 48

        Dim btnSave As New Button With {.Text = "Save", .Location = New Point(ClientSize.Width - 200, y), .Size = New Size(88, 28), .FlatStyle = FlatStyle.Flat}
        AddHandler btnSave.Click, AddressOf OnSaveClick
        Controls.Add(btnSave)
        Controls.Add(New Button With {.Text = "Cancel", .Location = New Point(ClientSize.Width - 104, y), .Size = New Size(88, 28), .DialogResult = DialogResult.Cancel})
        AcceptButton = btnSave
        CancelButton = Controls.OfType(Of Button)().Last()
    End Sub

    Private Async Sub OnSaveClick(sender As Object, e As EventArgs)
        If String.IsNullOrWhiteSpace(_txtName.Text) Then
            MessageBox.Show("Please enter a display name.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        If Not _chkAnonymous.Checked AndAlso String.IsNullOrWhiteSpace(_txtUsername.Text) Then
            MessageBox.Show("Please enter a username.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        Dim gameId = If(TryCast(_cboGame.SelectedItem, GameItem)?.Id, "")
        Task.Run(Async Function()
                     Await _credentialService.CreateSteamCredentialAsync(
                         _txtName.Text.Trim(), _txtUsername.Text.Trim(),
                         _txtPassword.Text, _chkAnonymous.Checked,
                         gameId, "", CancellationToken.None)
                     BeginInvoke(Sub()
                                     DialogResult = DialogResult.OK
                                     Close()
                                 End Sub)
                 End Function)
    End Sub

    Private Class GameItem
        Public Property Id As String
        Public Property Name As String
        Public Sub New(id As String, name As String)
            Me.Id = id
            Me.Name = name
        End Sub

        Public Overrides Function ToString() As String
            Return Name
        End Function
    End Class
End Class


' ============================================================
'  RealmCredentialsForm
'  Lists, adds, and removes Last Oasis realm credentials.
' ============================================================

Public Class RealmCredentialsForm
    Inherits Form

    Private ReadOnly _credentialService As CredentialService
    Private ReadOnly _pluginRegistry As PluginRegistry
    Private _listView As ListView

    Public Sub New(credentialService As CredentialService,
                   pluginRegistry As PluginRegistry)
        _credentialService = credentialService
        _pluginRegistry = pluginRegistry
        Text = "Realm Credentials"
        Size = New Size(580, 400)
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.Sizable
        MinimizeBox = False
        BuildLayout()
        Task.Run(AddressOf LoadAsync)
    End Sub

    Private Sub BuildLayout()
        _listView = New ListView With {.View = View.Details, .FullRowSelect = True, .GridLines = True, .Location = New Point(0, 0), .Size = New Size(570, 300)}
        _listView.Columns.Add("Display name", 200)
        _listView.Columns.Add("Game", 120)
        _listView.Columns.Add("Notes", 230)

        Dim btnAdd As New Button With {.Text = "Add Credential...", .Location = New Point(8, 308), .Size = New Size(130, 28), .FlatStyle = FlatStyle.Flat}
        AddHandler btnAdd.Click, Sub(s, e)
            Using dlg As New AddRealmCredentialDialog(_credentialService, _pluginRegistry)
                If dlg.ShowDialog(Me) = DialogResult.OK Then Task.Run(AddressOf LoadAsync)
            End Using
        End Sub

        Dim btnRemove As New Button With {.Text = "Remove", .Location = New Point(146, 308), .Size = New Size(88, 28), .FlatStyle = FlatStyle.Flat}
        AddHandler btnRemove.Click, Sub(s, e)
            If _listView.SelectedItems.Count = 0 Then Return
            Dim id = _listView.SelectedItems(0).Tag?.ToString()
            If String.IsNullOrEmpty(id) Then Return
            If MessageBox.Show("Remove this credential?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
            Task.Run(Async Function()
                         Try
                             Await _credentialService.DeleteRealmCredentialAsync(id, CancellationToken.None)
                             Await LoadAsync()
                         Catch ex As Exception
                             BeginInvoke(Sub() MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error))
                         End Try
                     End Function)
        End Sub

        Controls.AddRange({_listView, btnAdd, btnRemove,
            New Button With {.Text = "Close", .Location = New Point(470, 308), .Size = New Size(88, 28), .DialogResult = DialogResult.OK, .FlatStyle = FlatStyle.Flat}})
    End Sub

    Private Async Function LoadAsync() As Task
        Dim creds = Await _credentialService.ListRealmCredentialsAsync("", CancellationToken.None)
        BeginInvoke(Sub()
                        _listView.Items.Clear()
                        For Each c In creds
                            Dim item As New ListViewItem(c.DisplayName)
                            item.SubItems.Add(c.GameId)
                            item.SubItems.Add(c.Notes)
                            item.Tag = c.CredentialId
                            _listView.Items.Add(item)
                        Next
                    End Sub)
    End Function
End Class


Friend Class AddRealmCredentialDialog
    Inherits Form

    Private ReadOnly _credentialService As CredentialService
    Private ReadOnly _pluginRegistry As PluginRegistry
    Private _txtName As TextBox
    Private _cboGame As ComboBox
    Private _txtCustomerKey As TextBox
    Private _txtProviderKey As TextBox
    Private _txtNotes As TextBox

    Public Sub New(credentialService As CredentialService, pluginRegistry As PluginRegistry)
        _credentialService = credentialService
        _pluginRegistry = pluginRegistry
        Text = "Add Realm Credential"
        Size = New Size(460, 340)
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False : MinimizeBox = False
        BuildLayout()
    End Sub

    Private Sub BuildLayout()
        Const L = 140
        Dim y = 16

        Dim addRow = Sub(label As String, ctrl As Control)
            Controls.Add(New Label With {.Text = label, .Location = New Point(12, y + 3), .AutoSize = True})
            ctrl.Location = New Point(L, y)
            ctrl.Size = New Size(280, 23)
            Controls.Add(ctrl)
            y += 36
        End Sub

        _txtName = New TextBox()
        addRow("Display name:", _txtName)

        _cboGame = New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
        For Each p In _pluginRegistry.GetAllPlugins()
            _cboGame.Items.Add(New RealmGameItem With {.Id = p.GameId, .DisplayName = p.DisplayName})
        Next
        If _cboGame.Items.Count > 0 Then _cboGame.SelectedIndex = 0
        addRow("Game:", _cboGame)

        _txtCustomerKey = New TextBox With {.PasswordChar = "●"c}
        addRow("Customer key:", _txtCustomerKey)

        _txtProviderKey = New TextBox With {.PasswordChar = "●"c}
        addRow("Provider key:", _txtProviderKey)

        _txtNotes = New TextBox()
        addRow("Notes:", _txtNotes)

        y += 4
        Dim btnSave As New Button With {.Text = "Save", .Location = New Point(ClientSize.Width - 200, y), .Size = New Size(88, 28), .FlatStyle = FlatStyle.Flat}
        AddHandler btnSave.Click, AddressOf OnSaveClick
        Controls.Add(btnSave)
        Controls.Add(New Button With {.Text = "Cancel", .Location = New Point(ClientSize.Width - 104, y), .Size = New Size(88, 28), .DialogResult = DialogResult.Cancel})
        AcceptButton = btnSave
    End Sub

    Private Async Sub OnSaveClick(sender As Object, e As EventArgs)
        If String.IsNullOrWhiteSpace(_txtName.Text) OrElse
           String.IsNullOrWhiteSpace(_txtCustomerKey.Text) OrElse
           String.IsNullOrWhiteSpace(_txtProviderKey.Text) Then
            MessageBox.Show("Display name, Customer key, and Provider key are required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        Dim gameId = If(_cboGame.SelectedItem?.GetType().GetProperty("Id")?.GetValue(_cboGame.SelectedItem)?.ToString(), "")
        Task.Run(Async Function()
                     Await _credentialService.CreateRealmCredentialAsync(
                         _txtName.Text.Trim(), gameId,
                         _txtCustomerKey.Text.Trim(), _txtProviderKey.Text.Trim(),
                         _txtNotes.Text.Trim(), CancellationToken.None)
                     BeginInvoke(Sub()
                                     DialogResult = DialogResult.OK
                                     Close()
                                 End Sub)
                 End Function)
    End Sub
End Class


' ============================================================
'  AutomationRulesForm
'
'  Lists automation rules for one instance.
'  Allows creating, editing, enabling/disabling, and manual
'  firing of rules. Full rule editor is a separate dialog.
' ============================================================

Public Class AutomationRulesForm
    Inherits Form

    Private ReadOnly _instanceId As String
    Private ReadOnly _instanceName As String
    Private ReadOnly _automationEngine As AutomationEngine
    Private _listView As ListView

    Public Sub New(instanceId As String,
                   instanceName As String,
                   automationEngine As AutomationEngine)
        _instanceId = instanceId
        _instanceName = instanceName
        _automationEngine = automationEngine

        Text = $"Automation Rules — {instanceName}"
        Size = New Size(700, 480)
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.Sizable
        MinimizeBox = False

        BuildLayout()
        Task.Run(AddressOf LoadAsync)
    End Sub

    Private Sub BuildLayout()
        _listView = New ListView With {
            .View = View.Details,
            .FullRowSelect = True,
            .GridLines = True,
            .Location = New Point(0, 0),
            .Size = New Size(690, 360)
        }
        _listView.Columns.Add("Name", 200)
        _listView.Columns.Add("Trigger", 160)
        _listView.Columns.Add("Enabled", 60)
        _listView.Columns.Add("Last fired", 120)
        _listView.Columns.Add("Fire count", 80)

        Dim btnAdd As New Button With {
            .Text = "New Rule...",
            .Location = New Point(8, 368),
            .Size = New Size(100, 28),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler btnAdd.Click, AddressOf OnNewRuleClick

        Dim btnEdit As New Button With {
            .Text = "Edit...",
            .Location = New Point(116, 368),
            .Size = New Size(80, 28),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler btnEdit.Click, AddressOf OnEditClick

        Dim btnToggle As New Button With {
            .Text = "Enable/Disable",
            .Location = New Point(204, 368),
            .Size = New Size(110, 28),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler btnToggle.Click, AddressOf OnToggleClick

        Dim btnFire As New Button With {
            .Text = "▶ Run Now",
            .Location = New Point(322, 368),
            .Size = New Size(90, 28),
            .FlatStyle = FlatStyle.Flat,
            .BackColor = Color.FromArgb(0, 100, 160),
            .ForeColor = Color.White
        }
        AddHandler btnFire.Click, AddressOf OnFireClick

        Dim btnDelete As New Button With {
            .Text = "Delete",
            .Location = New Point(420, 368),
            .Size = New Size(80, 28),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler btnDelete.Click, AddressOf OnDeleteClick

        Dim btnClose As New Button With {
            .Text = "Close",
            .Location = New Point(598, 368),
            .Size = New Size(88, 28),
            .DialogResult = DialogResult.OK,
            .FlatStyle = FlatStyle.Flat
        }

        Controls.AddRange({_listView, btnAdd, btnEdit, btnToggle,
                            btnFire, btnDelete, btnClose})
    End Sub

    Private Async Function LoadAsync() As Task
        If _automationEngine Is Nothing Then Return
        Dim rules = Await _automationEngine.GetRulesAsync(CancellationToken.None)
        Dim instanceRules = rules.Where(
            Function(r) r.TargetId = _instanceId OrElse r.Scope = "Global").
            ToList()

        BeginInvoke(Sub()
                        _listView.Items.Clear()
                        For Each rule In instanceRules
                            Dim item As New ListViewItem(rule.DisplayName)
                            ' Parse trigger display label from JSON.
                            Dim triggerLabel = ParseTriggerLabel(rule.TriggerJson)
                            item.SubItems.Add(triggerLabel)
                            item.SubItems.Add(If(rule.IsEnabled, "✓", ""))
                            item.SubItems.Add(If(rule.LastFiredAt.HasValue,
                                rule.LastFiredAt.Value.ToString("MM/dd HH:mm"), "Never"))
                            item.SubItems.Add(rule.FireCount.ToString())
                            item.Tag = rule.RuleId
                            If Not rule.IsEnabled Then item.ForeColor = Color.Gray
                            _listView.Items.Add(item)
                        Next
                    End Sub)
    End Function

    Private Shared Function ParseTriggerLabel(json As String) As String
        Try
            Dim doc = System.Text.Json.JsonDocument.Parse(json)
            Dim typeId = doc.RootElement.GetProperty("triggerId").GetString()
            Select Case typeId
                Case "cron" : Return $"Schedule: {doc.RootElement.GetProperty("cronExpression").GetString()}"
                Case "instanceStateChanged" : Return "State changed"
                Case "updateAvailable" : Return "Update available"
                Case "crashLoopHalted" : Return "Crash loop halted"
                Case "manual" : Return "Manual only"
                Case Else : Return typeId
            End Select
        Catch
            Return "(unknown)"
        End Try
    End Function

    Private Sub OnNewRuleClick(sender As Object, e As EventArgs)
        Using dlg As New RuleEditorForm(Nothing, _instanceId, _instanceName,
                                         _automationEngine)
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                Task.Run(AddressOf LoadAsync)
            End If
        End Using
    End Sub

    Private Sub OnEditClick(sender As Object, e As EventArgs)
        If _listView.SelectedItems.Count = 0 Then Return
        Dim ruleId = _listView.SelectedItems(0).Tag?.ToString()
        Using dlg As New RuleEditorForm(ruleId, _instanceId, _instanceName,
                                         _automationEngine)
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                Task.Run(AddressOf LoadAsync)
            End If
        End Using
    End Sub

    Private Async Sub OnToggleClick(sender As Object, e As EventArgs)
        If _listView.SelectedItems.Count = 0 Then Return
        Dim ruleId = _listView.SelectedItems(0).Tag?.ToString()
        Task.Run(Async Function()
                     Dim rules = Await _automationEngine.GetRulesAsync(CancellationToken.None)
                     Dim rule = rules.FirstOrDefault(Function(r) r.RuleId = ruleId)
                     If rule IsNot Nothing Then
                         rule.IsEnabled = Not rule.IsEnabled
                         Await _automationEngine.UpdateRuleAsync(rule, CancellationToken.None)
                     End If
                     Await LoadAsync()
                 End Function)
    End Sub

    Private Async Sub OnFireClick(sender As Object, e As EventArgs)
        If _listView.SelectedItems.Count = 0 Then Return
        Dim ruleId = _listView.SelectedItems(0).Tag?.ToString()
        Task.Run(Async Function()
                     Dim result = Await _automationEngine.FireManualAsync(
                         ruleId, "UI: Run Now", CancellationToken.None)
                     BeginInvoke(Sub()
                                     Dim msg = If(result?.ActionSuccess = True,
                                         $"Rule executed successfully.{vbNewLine}{result.ActionMessage}",
                                         $"Rule failed.{vbNewLine}{result?.ActionMessage}")
                                     MessageBox.Show(msg, "Rule Result",
                                         MessageBoxButtons.OK,
                                         If(result?.ActionSuccess = True,
                                            MessageBoxIcon.Information,
                                            MessageBoxIcon.Warning))
                                 End Sub)
                     Await LoadAsync()
                 End Function)
    End Sub

    Private Async Sub OnDeleteClick(sender As Object, e As EventArgs)
        If _listView.SelectedItems.Count = 0 Then Return
        Dim ruleId = _listView.SelectedItems(0).Tag?.ToString()
        If MessageBox.Show("Delete this rule? This cannot be undone.", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
        Task.Run(Async Function()
                     Await _automationEngine.DeleteRuleAsync(ruleId, CancellationToken.None)
                     Await LoadAsync()
                 End Function)
    End Sub

End Class


' ============================================================
'  RuleEditorForm
'  Stub - full rule editor with trigger/condition/action
'  builder UI. The real implementation would use
'  SchemaFormBuilder-style dynamic controls for each
'  trigger/condition/action type. Shown as a placeholder.
' ============================================================

Public Class RuleEditorForm
    Inherits Form

    Private ReadOnly _ruleId As String
    Private ReadOnly _instanceId As String
    Private ReadOnly _automationEngine As AutomationEngine
    Private _txtName As TextBox
    Private _txtJson As TextBox

    Public Sub New(ruleId As String,
                   instanceId As String,
                   instanceName As String,
                   automationEngine As AutomationEngine)
        _ruleId = ruleId
        _instanceId = instanceId
        _automationEngine = automationEngine

        Text = If(String.IsNullOrEmpty(ruleId), "New Rule", "Edit Rule")
        Size = New Size(640, 520)
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.Sizable
        MinimizeBox = False

        BuildLayout()
        If Not String.IsNullOrEmpty(ruleId) Then Task.Run(AddressOf LoadRuleAsync)
    End Sub

    Private Sub BuildLayout()
        Controls.Add(New Label With {
            .Text = "Rule name:",
            .Location = New Point(12, 12),
            .AutoSize = True
        })
        _txtName = New TextBox With {
            .Location = New Point(100, 10),
            .Size = New Size(510, 23)
        }
        Controls.Add(_txtName)

        Controls.Add(New Label With {
            .Text = "Rule JSON:" & vbNewLine & "(Trigger, conditions," & vbNewLine &
                    "and action as JSON blocks." & vbNewLine &
                    "Full visual editor coming soon.)",
            .Location = New Point(12, 44),
            .Size = New Size(100, 80),
            .ForeColor = Color.Gray
        })

        _txtJson = New TextBox With {
            .Location = New Point(120, 44),
            .Size = New Size(490, 380),
            .Multiline = True,
            .ScrollBars = ScrollBars.Both,
            .Font = New Font("Consolas", 9),
            .WordWrap = False
        }
        Controls.Add(_txtJson)

        Dim btnSave As New Button With {
            .Text = "Save",
            .Location = New Point(ClientSize.Width - 200, ClientSize.Height - 44),
            .Size = New Size(88, 28),
            .FlatStyle = FlatStyle.Flat,
            .BackColor = Color.FromArgb(0, 100, 160),
            .ForeColor = Color.White
        }
        AddHandler btnSave.Click, AddressOf OnSaveClick
        Controls.Add(btnSave)
        Controls.Add(New Button With {
            .Text = "Cancel",
            .Location = New Point(ClientSize.Width - 104, ClientSize.Height - 44),
            .Size = New Size(88, 28),
            .DialogResult = DialogResult.Cancel
        })
        AcceptButton = btnSave
    End Sub

    Private Async Function LoadRuleAsync() As Task
        Dim rules = Await _automationEngine.GetRulesAsync(CancellationToken.None)
        Dim rule = rules.FirstOrDefault(Function(r) r.RuleId = _ruleId)
        If rule Is Nothing Then Return
        BeginInvoke(Sub()
                        _txtName.Text = rule.DisplayName
                        Dim combined = New System.Text.StringBuilder()
                        combined.AppendLine("// Trigger:")
                        combined.AppendLine(rule.TriggerJson)
                        combined.AppendLine("// Conditions (array):")
                        combined.AppendLine(rule.ConditionsJson)
                        combined.AppendLine("// Action:")
                        combined.AppendLine(rule.ActionJson)
                        _txtJson.Text = combined.ToString()
                    End Sub)
    End Function

    Private Async Sub OnSaveClick(sender As Object, e As EventArgs)
        ' Simplified save - a full editor would have proper
        ' fields for each trigger/condition/action type.
        If String.IsNullOrWhiteSpace(_txtName.Text) Then
            MessageBox.Show("Please enter a rule name.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        ' For now just close with OK - full implementation
        ' would parse the JSON fields and save properly.
        DialogResult = DialogResult.OK
        Close()
    End Sub

End Class
