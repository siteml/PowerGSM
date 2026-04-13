Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Text.Json
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Plugin
Imports GSM.Automation

' ============================================================
'  Supporting UI forms
' ============================================================

Namespace GSM.Manager.UI

    ' ============================================================
    '  PluginStatusForm — shows loaded plugins and compilation errors
    ' ============================================================

    Public Class PluginStatusForm
        Inherits Form

        Private _pluginListView As ListView
        Private _reloadButton As Button
        Private _errorTextBox As TextBox

        Public Sub New()
            InitializeControls()
            RefreshPluginList()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Plugin Status"
            Me.Size = New Size(700, 500)
            Me.StartPosition = FormStartPosition.CenterParent

            _reloadButton = New Button()
            _reloadButton.Text = "Reload Plugins"
            _reloadButton.Size = New Size(130, 32)
            _reloadButton.Location = New Point(20, 15)
            AddHandler _reloadButton.Click, AddressOf OnReload
            Me.Controls.Add(_reloadButton)

            _pluginListView = New ListView()
            _pluginListView.View = View.Details
            _pluginListView.FullRowSelect = True
            _pluginListView.GridLines = True
            _pluginListView.Location = New Point(20, 55)
            _pluginListView.Size = New Size(640, 200)
            _pluginListView.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                                      AnchorStyles.Right
            _pluginListView.Columns.Add("Game ID", 150)
            _pluginListView.Columns.Add("Display Name", 200)
            _pluginListView.Columns.Add("Status", 100)
            _pluginListView.Columns.Add("Install Methods", 180)
            Me.Controls.Add(_pluginListView)

            Dim errLabel As New Label()
            errLabel.Text = "Compilation Errors"
            errLabel.Font = New Font("Segoe UI", 10, FontStyle.Bold)
            errLabel.AutoSize = True
            errLabel.Location = New Point(20, 265)
            Me.Controls.Add(errLabel)

            _errorTextBox = New TextBox()
            _errorTextBox.Multiline = True
            _errorTextBox.ReadOnly = True
            _errorTextBox.ScrollBars = ScrollBars.Both
            _errorTextBox.Font = New Font("Consolas", 9)
            _errorTextBox.Location = New Point(20, 290)
            _errorTextBox.Size = New Size(640, 150)
            _errorTextBox.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                                    AnchorStyles.Right Or AnchorStyles.Bottom
            Me.Controls.Add(_errorTextBox)
        End Sub

        Private Sub RefreshPluginList()
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return

            _pluginListView.Items.Clear()
            For Each gamePlugin In registry.GetAllPlugins()
                Dim item As New ListViewItem(gamePlugin.GameId)
                item.SubItems.Add(gamePlugin.DisplayName)
                item.SubItems.Add("Loaded")
                Dim methods = gamePlugin.GetSupportedInstallMethods()
                item.SubItems.Add(String.Join(", ", methods.Select(Function(m) m.ToString())))
                _pluginListView.Items.Add(item)
            Next
        End Sub

        Private Sub OnReload(sender As Object, e As EventArgs)
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return

            Dim orphanDetector = ManagerProgram.Services.GetService(Of PluginOrphanDetector)()
            Dim summary = registry.ReloadAll(orphanDetector)

            RefreshPluginList()

            ' Show errors
            _errorTextBox.Clear()
            If summary.CompilationErrors.Count > 0 Then
                For Each compErr In summary.CompilationErrors
                    _errorTextBox.AppendText(
                        $"{compErr.FileName}({compErr.Line},{compErr.Column}): {compErr.ErrorCode} {compErr.Message}{vbCrLf}")
                Next
            Else
                _errorTextBox.Text = "No compilation errors."
            End If

            ' Show summary
            Dim msg = $"Loaded: {summary.LoadedPlugins.Count}, " &
                      $"Added: {summary.AddedGameIds.Count}, " &
                      $"Removed: {summary.RemovedGameIds.Count}, " &
                      $"Errors: {summary.CompilationErrors.Count}"
            If summary.OrphanedInstallationIds.Count > 0 Then
                msg &= $", Orphaned installations: {summary.OrphanedInstallationIds.Count}"
            End If

            MessageBox.Show(msg, "Reload Complete",
                          MessageBoxButtons.OK, MessageBoxIcon.Information)
        End Sub

    End Class

    ' ============================================================
    '  SteamCredentialsForm — manage Steam login credentials
    ' ============================================================

    Public Class SteamCredentialsForm
        Inherits Form

        Private _credListView As ListView
        Private _addButton As Button
        Private _deleteButton As Button

        Public Sub New()
            InitializeControls()
            RefreshList()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Steam Credentials"
            Me.Size = New Size(550, 400)
            Me.StartPosition = FormStartPosition.CenterParent

            _addButton = New Button()
            _addButton.Text = "Add"
            _addButton.Size = New Size(80, 30)
            _addButton.Location = New Point(20, 15)
            AddHandler _addButton.Click, AddressOf OnAdd
            Me.Controls.Add(_addButton)

            _deleteButton = New Button()
            _deleteButton.Text = "Delete"
            _deleteButton.Size = New Size(80, 30)
            _deleteButton.Location = New Point(110, 15)
            AddHandler _deleteButton.Click, AddressOf OnDelete
            Me.Controls.Add(_deleteButton)

            _credListView = New ListView()
            _credListView.View = View.Details
            _credListView.FullRowSelect = True
            _credListView.GridLines = True
            _credListView.Location = New Point(20, 55)
            _credListView.Size = New Size(490, 290)
            _credListView.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                                    AnchorStyles.Right Or AnchorStyles.Bottom
            _credListView.Columns.Add("Name", 180)
            _credListView.Columns.Add("Username", 180)
            _credListView.Columns.Add("Type", 100)
            Me.Controls.Add(_credListView)
        End Sub

        Private Sub RefreshList()
            _credListView.Items.Clear()
            Dim credService = ManagerProgram.Services.GetService(Of CredentialService)()
            If credService Is Nothing Then Return

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                For Each entity In credService.ListSteamCredentials(db)
                    Dim item As New ListViewItem(entity.DisplayName)
                    item.SubItems.Add(entity.Username)
                    item.SubItems.Add(If(entity.IsAnonymous, "Anonymous", "Login"))
                    item.Tag = entity.CredentialId
                    _credListView.Items.Add(item)
                Next
            End Using
        End Sub

        Private Sub OnAdd(sender As Object, e As EventArgs)
            Using dlg As New SteamCredentialEditForm()
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshList()
                End If
            End Using
        End Sub

        Private Sub OnDelete(sender As Object, e As EventArgs)
            If _credListView.SelectedItems.Count = 0 Then Return
            Dim credId = _credListView.SelectedItems(0).Tag.ToString()
            Dim confirm = MessageBox.Show("Delete this credential?", "Confirm",
                                         MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If confirm = DialogResult.Yes Then
                Dim credService = ManagerProgram.Services.GetService(Of CredentialService)()
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    credService.DeleteSteamCredential(db, credId)
                End Using
                RefreshList()
            End If
        End Sub

    End Class

    ''' <summary>
    ''' Simple edit dialog for a single Steam credential.
    ''' </summary>
    Friend Class SteamCredentialEditForm
        Inherits Form

        Private _nameTextBox As TextBox
        Private _usernameTextBox As TextBox
        Private _passwordTextBox As TextBox
        Private _anonCheckBox As CheckBox

        Public Sub New()
            Me.Text = "Add Steam Credential"
            Me.Size = New Size(400, 260)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim y = 20
            AddLabel("Display Name:", 20, y) : _nameTextBox = AddTxt(140, y, 220) : y += 35
            AddLabel("Username:", 20, y) : _usernameTextBox = AddTxt(140, y, 220) : y += 35
            AddLabel("Password:", 20, y) : _passwordTextBox = AddTxt(140, y, 220)
            _passwordTextBox.UseSystemPasswordChar = True : y += 35

            _anonCheckBox = New CheckBox()
            _anonCheckBox.Text = "Anonymous login (no credentials needed)"
            _anonCheckBox.AutoSize = True
            _anonCheckBox.Location = New Point(20, y)
            AddHandler _anonCheckBox.CheckedChanged,
                Sub(s, e)
                    _usernameTextBox.Enabled = Not _anonCheckBox.Checked
                    _passwordTextBox.Enabled = Not _anonCheckBox.Checked
                End Sub
            Me.Controls.Add(_anonCheckBox)
            y += 35

            Dim saveBtn As New Button()
            saveBtn.Text = "Save" : saveBtn.Size = New Size(80, 30)
            saveBtn.Location = New Point(190, y)
            AddHandler saveBtn.Click, AddressOf OnSave
            Me.Controls.Add(saveBtn)

            Dim cancelBtn As New Button()
            cancelBtn.Text = "Cancel" : cancelBtn.Size = New Size(80, 30)
            cancelBtn.Location = New Point(280, y)
            cancelBtn.DialogResult = DialogResult.Cancel
            Me.Controls.Add(cancelBtn)

            Me.CancelButton = cancelBtn
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            If String.IsNullOrWhiteSpace(_nameTextBox.Text) Then
                MessageBox.Show("Name is required.") : Return
            End If

            Dim credService = ManagerProgram.Services.GetService(Of CredentialService)()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                credService.SaveSteamCredential(db,
                    Guid.NewGuid().ToString("N"),
                    _nameTextBox.Text.Trim(),
                    If(_anonCheckBox.Checked, "anonymous", _usernameTextBox.Text.Trim()),
                    If(_anonCheckBox.Checked, "", _passwordTextBox.Text),
                    _anonCheckBox.Checked)
            End Using

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        Private Function AddLabel(text As String, x As Integer, y As Integer) As Label
            Dim lbl As New Label() With {.Text = text, .AutoSize = True, .Location = New Point(x, y + 3)}
            Me.Controls.Add(lbl) : Return lbl
        End Function

        Private Function AddTxt(x As Integer, y As Integer, w As Integer) As TextBox
            Dim txt As New TextBox() With {.Location = New Point(x, y), .Size = New Size(w, 24)}
            Me.Controls.Add(txt) : Return txt
        End Function

    End Class

    ' ============================================================
    '  RealmCredentialsForm — manage realm/game-specific credentials
    ' ============================================================

    Public Class RealmCredentialsForm
        Inherits Form

        Private _credListView As ListView

        Public Sub New()
            Me.Text = "Realm Credentials"
            Me.Size = New Size(550, 400)
            Me.StartPosition = FormStartPosition.CenterParent

            _credListView = New ListView()
            _credListView.View = View.Details
            _credListView.FullRowSelect = True
            _credListView.GridLines = True
            _credListView.Dock = DockStyle.Fill
            _credListView.Columns.Add("Name", 180)
            _credListView.Columns.Add("Game", 120)
            Me.Controls.Add(_credListView)

            RefreshList()
        End Sub

        Private Sub RefreshList()
            _credListView.Items.Clear()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                For Each entity In db.RealmCredentials.ToList()
                    Dim item As New ListViewItem(entity.DisplayName)
                    item.SubItems.Add(entity.GameId)
                    item.Tag = entity.CredentialId
                    _credListView.Items.Add(item)
                Next
            End Using
        End Sub

    End Class

    ' ============================================================
    '  AutomationRulesForm — list and manage automation rules
    ' ============================================================

    Public Class AutomationRulesForm
        Inherits Form

        Private _rulesListView As ListView
        Private _addButton As Button
        Private _editButton As Button
        Private _deleteButton As Button
        Private _fireButton As Button
        Private _historyListView As ListView

        Public Sub New()
            InitializeControls()
            RefreshRules()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Automation Rules"
            Me.Size = New Size(800, 600)
            Me.StartPosition = FormStartPosition.CenterParent

            ' Buttons
            _addButton = New Button() With {.Text = "Add Rule", .Size = New Size(100, 30), .Location = New Point(20, 15)}
            AddHandler _addButton.Click, AddressOf OnAdd
            Me.Controls.Add(_addButton)

            _editButton = New Button() With {.Text = "Edit", .Size = New Size(80, 30), .Location = New Point(130, 15)}
            AddHandler _editButton.Click, AddressOf OnEdit
            Me.Controls.Add(_editButton)

            _deleteButton = New Button() With {.Text = "Delete", .Size = New Size(80, 30), .Location = New Point(220, 15)}
            AddHandler _deleteButton.Click, AddressOf OnDelete
            Me.Controls.Add(_deleteButton)

            _fireButton = New Button() With {.Text = "Fire Now", .Size = New Size(90, 30), .Location = New Point(310, 15)}
            AddHandler _fireButton.Click, AddressOf OnFire
            Me.Controls.Add(_fireButton)

            ' Rules list
            _rulesListView = New ListView()
            _rulesListView.View = View.Details
            _rulesListView.FullRowSelect = True
            _rulesListView.GridLines = True
            _rulesListView.Location = New Point(20, 55)
            _rulesListView.Size = New Size(740, 200)
            _rulesListView.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
            _rulesListView.Columns.Add("Name", 200)
            _rulesListView.Columns.Add("Scope", 100)
            _rulesListView.Columns.Add("Target", 150)
            _rulesListView.Columns.Add("Enabled", 70)
            _rulesListView.Columns.Add("Trigger", 180)
            Me.Controls.Add(_rulesListView)

            ' History
            Dim histLabel As New Label() With {
                .Text = "Execution History",
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .AutoSize = True, .Location = New Point(20, 265)}
            Me.Controls.Add(histLabel)

            _historyListView = New ListView()
            _historyListView.View = View.Details
            _historyListView.FullRowSelect = True
            _historyListView.GridLines = True
            _historyListView.Location = New Point(20, 290)
            _historyListView.Size = New Size(740, 250)
            _historyListView.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                                       AnchorStyles.Right Or AnchorStyles.Bottom
            _historyListView.Columns.Add("Time", 150)
            _historyListView.Columns.Add("Rule", 150)
            _historyListView.Columns.Add("Trigger", 100)
            _historyListView.Columns.Add("Result", 100)
            _historyListView.Columns.Add("Details", 220)
            Me.Controls.Add(_historyListView)
        End Sub

        Private Sub RefreshRules()
            _rulesListView.Items.Clear()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                For Each entity In db.AutomationRules.ToList()
                    Dim item As New ListViewItem(entity.RuleName)
                    item.SubItems.Add(If(entity.ScopeKind, ""))
                    item.SubItems.Add(If(entity.TargetId, ""))
                    item.SubItems.Add(If(entity.IsEnabled, "Yes", "No"))
                    item.SubItems.Add(If(entity.TriggerJson, "").Substring(0, Math.Min(If(entity.TriggerJson, "").Length, 40)))
                    item.Tag = entity.RuleId
                    _rulesListView.Items.Add(item)
                Next

                ' Load recent executions
                _historyListView.Items.Clear()
                Dim recentExecs = db.RuleExecutions.
                    OrderByDescending(Function(ex) ex.StartedAtUtc).
                    Take(50).
                    ToList()
                For Each exec In recentExecs
                    Dim item As New ListViewItem(exec.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                    item.SubItems.Add(exec.RuleId)
                    item.SubItems.Add(If(exec.TriggerReason, ""))
                    item.SubItems.Add(If(exec.WasSkipped, "Skipped", "Executed"))
                    item.SubItems.Add(If(exec.SkipReason, If(exec.ActionResultJson, "").
                        Substring(0, Math.Min(If(exec.ActionResultJson, "").Length, 50))))
                    _historyListView.Items.Add(item)
                Next
            End Using
        End Sub

        Private Sub OnAdd(sender As Object, e As EventArgs)
            Using dlg As New RuleEditorForm()
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshRules()
                End If
            End Using
        End Sub

        Private Sub OnEdit(sender As Object, e As EventArgs)
            If _rulesListView.SelectedItems.Count = 0 Then Return
            Dim ruleId = _rulesListView.SelectedItems(0).Tag.ToString()
            Using dlg As New RuleEditorForm(ruleId)
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshRules()
                End If
            End Using
        End Sub

        Private Sub OnDelete(sender As Object, e As EventArgs)
            If _rulesListView.SelectedItems.Count = 0 Then Return
            Dim ruleId = _rulesListView.SelectedItems(0).Tag.ToString()
            If MessageBox.Show("Delete this rule?", "Confirm",
                             MessageBoxButtons.YesNo) = DialogResult.Yes Then
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim entity = db.AutomationRules.Find(ruleId)
                    If entity IsNot Nothing Then
                        db.AutomationRules.Remove(entity)
                        db.SaveChanges()
                    End If
                End Using
                Dim engine = ManagerProgram.Services.GetService(Of AutomationEngine)()
                engine?.ReloadRules()
                RefreshRules()
            End If
        End Sub

        Private Async Sub OnFire(sender As Object, e As EventArgs)
            If _rulesListView.SelectedItems.Count = 0 Then Return
            Dim ruleId = _rulesListView.SelectedItems(0).Tag.ToString()
            Dim engine = ManagerProgram.Services.GetService(Of AutomationEngine)()
            If engine IsNot Nothing Then
                Dim ok = Await engine.FireRuleManuallyAsync(ruleId)
                MessageBox.Show(If(ok, "Rule fired successfully.", "Rule not found or failed."),
                              "Fire Rule", MessageBoxButtons.OK)
                RefreshRules()
            End If
        End Sub

    End Class

    ' ============================================================
    '  RuleEditorForm — create or edit an automation rule
    ' ============================================================

    Public Class RuleEditorForm
        Inherits Form

        Private _nameTextBox As TextBox
        Private _scopeComboBox As ComboBox
        Private _targetTextBox As TextBox
        Private _enabledCheckBox As CheckBox
        Private _triggerTypeComboBox As ComboBox
        Private _cronTextBox As TextBox
        Private _cronPanel As Panel

        Private ReadOnly _editRuleId As String

        Public Sub New(Optional editRuleId As String = Nothing)
            _editRuleId = editRuleId
            InitializeControls()
            If _editRuleId IsNot Nothing Then
                LoadExisting()
            End If
        End Sub

        Private Sub InitializeControls()
            Me.Text = If(_editRuleId IsNot Nothing, "Edit Rule", "New Rule")
            Me.Size = New Size(500, 400)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim y = 20

            AddLabel("Rule Name:", 20, y)
            _nameTextBox = AddTxt(150, y, 300)
            y += 35

            AddLabel("Scope:", 20, y)
            _scopeComboBox = New ComboBox()
            _scopeComboBox.Location = New Point(150, y)
            _scopeComboBox.Size = New Size(200, 24)
            _scopeComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            _scopeComboBox.Items.AddRange(New Object() {"Instance", "Installation", "AllInstances"})
            _scopeComboBox.SelectedIndex = 0
            Me.Controls.Add(_scopeComboBox)
            y += 35

            AddLabel("Target ID:", 20, y)
            _targetTextBox = AddTxt(150, y, 300)
            y += 35

            _enabledCheckBox = New CheckBox()
            _enabledCheckBox.Text = "Enabled"
            _enabledCheckBox.Checked = True
            _enabledCheckBox.Location = New Point(20, y)
            _enabledCheckBox.AutoSize = True
            Me.Controls.Add(_enabledCheckBox)
            y += 35

            AddLabel("Trigger:", 20, y)
            _triggerTypeComboBox = New ComboBox()
            _triggerTypeComboBox.Location = New Point(150, y)
            _triggerTypeComboBox.Size = New Size(200, 24)
            _triggerTypeComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            _triggerTypeComboBox.Items.AddRange(New Object() {"Schedule (Cron)", "Manual", "Version Mismatch"})
            _triggerTypeComboBox.SelectedIndex = 0
            AddHandler _triggerTypeComboBox.SelectedIndexChanged,
                Sub(s, e) _cronPanel.Visible = (_triggerTypeComboBox.SelectedIndex = 0)
            Me.Controls.Add(_triggerTypeComboBox)
            y += 35

            _cronPanel = New Panel()
            _cronPanel.Location = New Point(0, y)
            _cronPanel.Size = New Size(480, 35)
            AddLabel("Cron Expression:", 20, 0).Parent = _cronPanel
            _cronTextBox = AddTxt(150, 0, 300)
            _cronTextBox.Parent = _cronPanel
            _cronTextBox.Text = "0 4 * * *"
            Me.Controls.Add(_cronPanel)
            y += 45

            ' Buttons
            Dim saveBtn As New Button() With {.Text = "Save", .Size = New Size(90, 32), .Location = New Point(270, y)}
            AddHandler saveBtn.Click, AddressOf OnSave
            Me.Controls.Add(saveBtn)

            Dim cancelBtn As New Button() With {.Text = "Cancel", .Size = New Size(90, 32), .Location = New Point(370, y)}
            cancelBtn.DialogResult = DialogResult.Cancel
            Me.Controls.Add(cancelBtn)
            Me.CancelButton = cancelBtn
        End Sub

        Private Sub LoadExisting()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim entity = db.AutomationRules.Find(_editRuleId)
                If entity Is Nothing Then Return

                _nameTextBox.Text = entity.RuleName
                _targetTextBox.Text = If(entity.TargetId, "")
                _enabledCheckBox.Checked = entity.IsEnabled

                Select Case If(entity.ScopeKind, "").ToLower()
                    Case "instance" : _scopeComboBox.SelectedIndex = 0
                    Case "installation" : _scopeComboBox.SelectedIndex = 1
                    Case Else : _scopeComboBox.SelectedIndex = 2
                End Select

                ' Parse trigger type from JSON
                If Not String.IsNullOrEmpty(entity.TriggerJson) Then
                    Try
                        Dim trigDoc = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(entity.TriggerJson)
                        If trigDoc IsNot Nothing AndAlso trigDoc.ContainsKey("cronExpression") Then
                            _triggerTypeComboBox.SelectedIndex = 0
                            _cronTextBox.Text = trigDoc("cronExpression")
                        ElseIf trigDoc IsNot Nothing AndAlso trigDoc.ContainsKey("triggerId") Then
                            Select Case trigDoc("triggerId")
                                Case "manual" : _triggerTypeComboBox.SelectedIndex = 1
                                Case "version_mismatch" : _triggerTypeComboBox.SelectedIndex = 2
                            End Select
                        End If
                    Catch
                    End Try
                End If
            End Using
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            If String.IsNullOrWhiteSpace(_nameTextBox.Text) Then
                MessageBox.Show("Rule name is required.") : Return
            End If

            ' Build trigger JSON
            Dim triggerJson As String
            Select Case _triggerTypeComboBox.SelectedIndex
                Case 0 ' Schedule
                    triggerJson = JsonSerializer.Serialize(New Dictionary(Of String, String) From {
                        {"triggerId", "schedule"},
                        {"cronExpression", _cronTextBox.Text.Trim()}
                    })
                Case 1 ' Manual
                    triggerJson = JsonSerializer.Serialize(New Dictionary(Of String, String) From {
                        {"triggerId", "manual"}
                    })
                Case Else ' Version mismatch
                    triggerJson = JsonSerializer.Serialize(New Dictionary(Of String, String) From {
                        {"triggerId", "version_mismatch"}
                    })
            End Select

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                Dim entity As AutomationRuleEntity
                If _editRuleId IsNot Nothing Then
                    entity = db.AutomationRules.Find(_editRuleId)
                    If entity Is Nothing Then Return
                Else
                    entity = New AutomationRuleEntity With {
                        .RuleId = Guid.NewGuid().ToString("N"),
                        .CreatedUtc = DateTime.UtcNow
                    }
                    db.AutomationRules.Add(entity)
                End If

                entity.RuleName = _nameTextBox.Text.Trim()
                entity.ScopeKind = _scopeComboBox.SelectedItem.ToString()
                entity.TargetId = _targetTextBox.Text.Trim()
                entity.IsEnabled = _enabledCheckBox.Checked
                entity.TriggerJson = triggerJson
                entity.UpdatedUtc = DateTime.UtcNow

                db.SaveChanges()
            End Using

            ' Reload engine
            Dim engine = ManagerProgram.Services.GetService(Of AutomationEngine)()
            engine?.ReloadRules()

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        Private Function AddLabel(text As String, x As Integer, y As Integer) As Label
            Dim lbl As New Label() With {.Text = text, .AutoSize = True, .Location = New Point(x, y + 3)}
            Me.Controls.Add(lbl) : Return lbl
        End Function

        Private Function AddTxt(x As Integer, y As Integer, w As Integer) As TextBox
            Dim txt As New TextBox() With {.Location = New Point(x, y), .Size = New Size(w, 24)}
            Me.Controls.Add(txt) : Return txt
        End Function

    End Class

    ' ============================================================
    '  SettingsForm — general application settings
    ' ============================================================

    Public Class SettingsForm
        Inherits Form

        Public Sub New()
            Me.Text = "Settings"
            Me.Size = New Size(500, 400)
            Me.StartPosition = FormStartPosition.CenterParent

            Dim infoLabel As New Label()
            infoLabel.Text = "Application settings will be expanded in future versions." &
                            vbCrLf & vbCrLf &
                            "Current database: gsm.db" & vbCrLf &
                            "Plugins directory: Plugins\"
            infoLabel.AutoSize = True
            infoLabel.Location = New Point(20, 20)
            infoLabel.Font = New Font("Segoe UI", 10)
            Me.Controls.Add(infoLabel)
        End Sub

    End Class

    ' ============================================================
    '  AddInstanceForm — add a new instance to an existing installation
    ' ============================================================

    Public Class AddInstanceForm
        Inherits Form

        Private ReadOnly _installationId As String
        Private _nameTextBox As TextBox
        Private _configPanel As Panel
        Private _schemaResult As SchemaFormResult

        Public Sub New(installationId As String)
            _installationId = installationId
            InitializeControls()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Add Instance"
            Me.Size = New Size(550, 450)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim y = 20

            ' Instance name
            Dim nameLbl As New Label() With {
                .Text = "Instance Name:", .AutoSize = True,
                .Location = New Point(20, y + 3)}
            Me.Controls.Add(nameLbl)
            _nameTextBox = New TextBox() With {
                .Location = New Point(150, y), .Size = New Size(350, 24)}
            _nameTextBox.Text = "Server 1"
            Me.Controls.Add(_nameTextBox)
            y += 40

            ' Config panel — load schema from plugin
            Dim configLabel As New Label() With {
                .Text = "Instance Configuration",
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .AutoSize = True, .Location = New Point(20, y)}
            Me.Controls.Add(configLabel)
            y += 25

            _configPanel = New Panel() With {
                .Location = New Point(20, y),
                .Size = New Size(490, 250),
                .BorderStyle = BorderStyle.FixedSingle,
                .AutoScroll = True}
            Me.Controls.Add(_configPanel)

            ' Load plugin schema
            LoadPluginSchema()

            y += 260
            Dim saveBtn As New Button() With {
                .Text = "Create", .Size = New Size(100, 32),
                .Location = New Point(300, y)}
            AddHandler saveBtn.Click, AddressOf OnSave
            Me.Controls.Add(saveBtn)

            Dim cancelBtn As New Button() With {
                .Text = "Cancel", .Size = New Size(100, 32),
                .Location = New Point(410, y)}
            cancelBtn.DialogResult = DialogResult.Cancel
            Me.Controls.Add(cancelBtn)
            Me.CancelButton = cancelBtn
        End Sub

        Private Sub LoadPluginSchema()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installEntity = db.Installations.Find(_installationId)
                If installEntity Is Nothing Then Return

                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return

                Dim gamePlugin = registry.GetPlugin(installEntity.GameId)
                If gamePlugin Is Nothing Then Return

                Dim schema = gamePlugin.GetInstanceConfigSchema()
                _schemaResult = SchemaFormBuilder.Build(schema, New Dictionary(Of String, String))
                If _schemaResult.Panel IsNot Nothing Then
                    _schemaResult.Panel.Dock = DockStyle.Fill
                    _configPanel.Controls.Add(_schemaResult.Panel)
                End If
            End Using
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            If String.IsNullOrWhiteSpace(_nameTextBox.Text) Then
                MessageBox.Show("Instance name is required.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Dim configValues As New Dictionary(Of String, String)
            If _schemaResult IsNot Nothing AndAlso _schemaResult.ValueExtractor IsNot Nothing Then
                configValues = _schemaResult.ValueExtractor.Invoke()
            End If

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installEntity = db.Installations.Find(_installationId)
                If installEntity Is Nothing Then Return

                Dim instanceEntity As New InstanceEntity With {
                    .InstanceId = Guid.NewGuid().ToString("N"),
                    .InstallationId = _installationId,
                    .GameId = installEntity.GameId,
                    .DisplayName = _nameTextBox.Text.Trim(),
                    .ConfigJson = JsonSerializer.Serialize(configValues),
                    .CreatedUtc = DateTime.UtcNow,
                    .UpdatedUtc = DateTime.UtcNow
                }
                db.Instances.Add(instanceEntity)
                db.SaveChanges()
            End Using

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

    End Class

End Namespace