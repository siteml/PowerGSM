Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Text.Json
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Node.Api
Imports GSM.Plugin

' ============================================================
'  NewInstallationForm — create a new game server installation
'
'  Steps:
'    1. Select a node (from database)
'    2. Select a game (from loaded plugins)
'    3. Choose install method
'    4. Fill in plugin-specific config fields (dynamic form)
'    5. Set install path and display name
'    6. Optionally create the first instance
' ============================================================

Namespace GSM.Manager.UI

    Public Class NewInstallationForm
        Inherits Form

        Private _nodeComboBox As ComboBox
        Private _gameComboBox As ComboBox
        Private _methodComboBox As ComboBox
        Private _nameTextBox As TextBox
        Private _pathTextBox As TextBox
        Private _createInstanceCheckBox As CheckBox
        Private _instanceNameTextBox As TextBox
        Private _configPanel As Panel
        Private _saveButton As Button
        Private _cancelButton As Button
        Private _steamCredComboBox As ComboBox

        Private _schemaResult As SchemaFormResult
        Private _nodeEntities As List(Of NodeEntity)
        Private _steamCredIds As New List(Of String)

        Public Sub New()
            InitializeControls()
            LoadNodes()
            LoadGames()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "New Installation"
            Me.Size = New Size(600, 700)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.AutoScroll = True

            Dim y = 20

            ' Node
            AddLabel("Node:", 20, y)
            _nodeComboBox = New ComboBox()
            _nodeComboBox.Location = New Point(150, y)
            _nodeComboBox.Size = New Size(400, 24)
            _nodeComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            Me.Controls.Add(_nodeComboBox)
            y += 35

            ' Game
            AddLabel("Game:", 20, y)
            _gameComboBox = New ComboBox()
            _gameComboBox.Location = New Point(150, y)
            _gameComboBox.Size = New Size(400, 24)
            _gameComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            AddHandler _gameComboBox.SelectedIndexChanged, AddressOf OnGameChanged
            Me.Controls.Add(_gameComboBox)
            y += 35

            ' Install method
            AddLabel("Install Method:", 20, y)
            _methodComboBox = New ComboBox()
            _methodComboBox.Location = New Point(150, y)
            _methodComboBox.Size = New Size(200, 24)
            _methodComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            Me.Controls.Add(_methodComboBox)
            y += 35

            ' Display name
            AddLabel("Display Name:", 20, y)
            _nameTextBox = New TextBox()
            _nameTextBox.Location = New Point(150, y)
            _nameTextBox.Size = New Size(400, 24)
            Me.Controls.Add(_nameTextBox)
            y += 35

            ' Install path
            AddLabel("Install Path:", 20, y)
            _pathTextBox = New TextBox()
            _pathTextBox.Location = New Point(150, y)
            _pathTextBox.Size = New Size(400, 24)
            _pathTextBox.Text = "C:\GameServers\"
            Me.Controls.Add(_pathTextBox)
            y += 35

            ' Steam credentials
            AddLabel("Steam Account:", 20, y)
            _steamCredComboBox = New ComboBox()
            _steamCredComboBox.Location = New Point(150, y)
            _steamCredComboBox.Size = New Size(300, 24)
            _steamCredComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            Me.Controls.Add(_steamCredComboBox)
            LoadSteamCredentials()
            y += 35

            ' Create first instance
            _createInstanceCheckBox = New CheckBox()
            _createInstanceCheckBox.Text = "Create first instance"
            _createInstanceCheckBox.Checked = True
            _createInstanceCheckBox.Location = New Point(20, y)
            _createInstanceCheckBox.AutoSize = True
            AddHandler _createInstanceCheckBox.CheckedChanged,
                Sub(s, e) _instanceNameTextBox.Enabled = _createInstanceCheckBox.Checked
            Me.Controls.Add(_createInstanceCheckBox)
            y += 30

            AddLabel("Instance Name:", 20, y)
            _instanceNameTextBox = New TextBox()
            _instanceNameTextBox.Location = New Point(150, y)
            _instanceNameTextBox.Size = New Size(400, 24)
            _instanceNameTextBox.Text = "Server 1"
            Me.Controls.Add(_instanceNameTextBox)
            y += 40

            ' Plugin config panel
            Dim configLabel As New Label()
            configLabel.Text = "Game Configuration"
            configLabel.Font = New Font("Segoe UI", 10, FontStyle.Bold)
            configLabel.AutoSize = True
            configLabel.Location = New Point(20, y)
            Me.Controls.Add(configLabel)
            y += 25

            _configPanel = New Panel()
            _configPanel.Location = New Point(20, y)
            _configPanel.Size = New Size(540, 250)
            _configPanel.BorderStyle = BorderStyle.FixedSingle
            _configPanel.AutoScroll = True
            Me.Controls.Add(_configPanel)
            y += 260

            ' Buttons
            _saveButton = New Button()
            _saveButton.Text = "Create"
            _saveButton.Size = New Size(100, 32)
            _saveButton.Location = New Point(350, y)
            AddHandler _saveButton.Click, AddressOf OnSave
            Me.Controls.Add(_saveButton)

            _cancelButton = New Button()
            _cancelButton.Text = "Cancel"
            _cancelButton.Size = New Size(100, 32)
            _cancelButton.Location = New Point(460, y)
            _cancelButton.DialogResult = DialogResult.Cancel
            Me.Controls.Add(_cancelButton)

            Me.AcceptButton = _saveButton
            Me.CancelButton = _cancelButton
        End Sub

        Private Sub LoadNodes()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                _nodeEntities = db.Nodes.Where(Function(n) n.IsEnabled).ToList()
                _nodeComboBox.Items.Clear()
                For Each nodeEnt In _nodeEntities
                    _nodeComboBox.Items.Add($"{nodeEnt.DisplayName} ({nodeEnt.HostAddress}:{nodeEnt.Port})")
                Next
                If _nodeComboBox.Items.Count > 0 Then
                    _nodeComboBox.SelectedIndex = 0
                End If
            End Using
        End Sub

        Private Sub LoadGames()
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return

            Dim plugins = registry.GetAllPlugins()
            _gameComboBox.Items.Clear()
            For Each gamePlugin In plugins
                _gameComboBox.Items.Add($"{gamePlugin.DisplayName} ({gamePlugin.GameId})")
            Next
            If _gameComboBox.Items.Count > 0 Then
                _gameComboBox.SelectedIndex = 0
            End If
        End Sub

        Private Sub LoadSteamCredentials()
            _steamCredComboBox.Items.Clear()
            _steamCredIds.Clear()

            ' Add "Anonymous" as first option
            _steamCredComboBox.Items.Add("(Anonymous — no login)")
            _steamCredIds.Add("")

            Dim credService = ManagerProgram.Services.GetService(Of CredentialService)()
            If credService Is Nothing Then Return

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                For Each entity In credService.ListSteamCredentials(db)
                    _steamCredComboBox.Items.Add($"{entity.DisplayName} ({entity.Username})")
                    _steamCredIds.Add(entity.CredentialId)
                Next
            End Using

            _steamCredComboBox.SelectedIndex = 0
        End Sub

        Private Sub OnGameChanged(sender As Object, e As EventArgs)
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return

            Dim plugins = registry.GetAllPlugins()
            If _gameComboBox.SelectedIndex < 0 OrElse
               _gameComboBox.SelectedIndex >= plugins.Count Then Return

            Dim currentPlugin = plugins(_gameComboBox.SelectedIndex)

            ' Update install methods
            _methodComboBox.Items.Clear()
            Dim methods = currentPlugin.GetSupportedInstallMethods()
            For Each installMethod In methods
                _methodComboBox.Items.Add(installMethod.ToString())
            Next
            If _methodComboBox.Items.Count > 0 Then
                _methodComboBox.SelectedIndex = 0
            End If

            ' Update config panel with plugin schema
            _configPanel.Controls.Clear()
            Dim schema = currentPlugin.GetInstallConfigSchema()
            _schemaResult = SchemaFormBuilder.Build(schema, New Dictionary(Of String, String))
            If _schemaResult.Panel IsNot Nothing Then
                _schemaResult.Panel.Dock = DockStyle.Fill
                _configPanel.Controls.Add(_schemaResult.Panel)
            End If

            ' Auto-fill name if empty
            If String.IsNullOrEmpty(_nameTextBox.Text) Then
                _nameTextBox.Text = currentPlugin.DisplayName
            End If
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            ' Validate
            If _nodeComboBox.SelectedIndex < 0 Then
                MessageBox.Show("Select a node.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            If _gameComboBox.SelectedIndex < 0 Then
                MessageBox.Show("Select a game.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            If String.IsNullOrWhiteSpace(_nameTextBox.Text) Then
                MessageBox.Show("Display name is required.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            If String.IsNullOrWhiteSpace(_pathTextBox.Text) Then
                MessageBox.Show("Install path is required.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            Dim plugins = registry.GetAllPlugins()
            Dim chosenPlugin = plugins(_gameComboBox.SelectedIndex)
            Dim selectedNode = _nodeEntities(_nodeComboBox.SelectedIndex)

            ' Collect config values
            Dim configValues As New Dictionary(Of String, String)
            If _schemaResult IsNot Nothing AndAlso _schemaResult.ValueExtractor IsNot Nothing Then
                configValues = _schemaResult.ValueExtractor.Invoke()
            End If

            Dim installId As String

            ' Capture selected credential on UI thread
            Dim selectedCredId = ""
            If _steamCredComboBox.SelectedIndex > 0 AndAlso
               _steamCredComboBox.SelectedIndex < _steamCredIds.Count Then
                selectedCredId = _steamCredIds(_steamCredComboBox.SelectedIndex)
            End If

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                ' Create installation
                installId = Guid.NewGuid().ToString("N")
                Dim installEntity As New InstallationEntity With {
                    .InstallationId = installId,
                    .GameId = chosenPlugin.GameId,
                    .DisplayName = _nameTextBox.Text.Trim(),
                    .NodeId = selectedNode.NodeId,
                    .InstallPath = _pathTextBox.Text.Trim(),
                    .InstallMethod = If(_methodComboBox.SelectedItem IsNot Nothing,
                                        _methodComboBox.SelectedItem.ToString(), "Manual"),
                    .SteamCredentialId = selectedCredId,
                    .ConfigJson = JsonSerializer.Serialize(configValues),
                    .CreatedUtc = DateTime.UtcNow,
                    .UpdatedUtc = DateTime.UtcNow
                }
                db.Installations.Add(installEntity)

                ' Optionally create first instance
                If _createInstanceCheckBox.Checked AndAlso
                   Not String.IsNullOrWhiteSpace(_instanceNameTextBox.Text) Then

                    Dim instanceEntity As New InstanceEntity With {
                        .InstanceId = Guid.NewGuid().ToString("N"),
                        .InstallationId = installId,
                        .GameId = chosenPlugin.GameId,
                        .DisplayName = _instanceNameTextBox.Text.Trim(),
                        .ConfigJson = JsonSerializer.Serialize(configValues),
                        .CreatedUtc = DateTime.UtcNow,
                        .UpdatedUtc = DateTime.UtcNow
                    }
                    db.Instances.Add(instanceEntity)
                End If

                db.SaveChanges()
            End Using

            ' Ask whether to run the install now
            Dim runNow = MessageBox.Show(
                "Installation record created. Run the install on the node now?" & vbCrLf & vbCrLf &
                "This will download the game server files to the specified path.",
                "Run Install?", MessageBoxButtons.YesNo, MessageBoxIcon.Question)

            If runNow = DialogResult.Yes Then
                ' Fire install in background
                Dim installMgr = ManagerProgram.Services.GetService(Of InstallationManager)()
                If installMgr IsNot Nothing Then
                    _saveButton.Enabled = False
                    _saveButton.Text = "Installing..."

                    Task.Run(Async Function()
                                 Try
                                     Dim ok = Await installMgr.InstallAsync(
                                         installId, selectedCredId,
                                         promptHandler:=AddressOf HandleSteamPrompt)
                                     Me.BeginInvoke(Sub()
                                                        If ok Then
                                                            MessageBox.Show("Installation completed successfully!",
                                                                          "Install Complete", MessageBoxButtons.OK,
                                                                          MessageBoxIcon.Information)
                                                        Else
                                                            MessageBox.Show("Installation failed. Check the node logs for details.",
                                                                          "Install Failed", MessageBoxButtons.OK,
                                                                          MessageBoxIcon.Warning)
                                                        End If
                                                        Me.DialogResult = DialogResult.OK
                                                        Me.Close()
                                                    End Sub)
                                 Catch ex As Exception
                                     Me.BeginInvoke(Sub()
                                                        MessageBox.Show($"Installation error: {ex.Message}" & vbCrLf & vbCrLf &
                                                                       $"Details: {ex.ToString()}",
                                                                       "Install Error", MessageBoxButtons.OK,
                                                                       MessageBoxIcon.Error)
                                                        _saveButton.Enabled = True
                                                        _saveButton.Text = "Create"
                                                    End Sub)
                                 End Try
                             End Function)
                    Return ' Don't close yet — wait for install
                End If
            End If

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        Private Function AddLabel(text As String, x As Integer, y As Integer) As Label
            Dim lbl As New Label()
            lbl.Text = text
            lbl.AutoSize = True
            lbl.Location = New Point(x, y + 3)
            Me.Controls.Add(lbl)
            Return lbl
        End Function

        Private Function HandleSteamPrompt(promptType As PromptType,
                                            message As String) As Task(Of String)
            ' Marshal to UI thread to show input dialog
            Dim result As String = Nothing
            Me.Invoke(Sub()
                          Dim title = If(promptType = PromptType.TwoFactorCode,
                              "Steam Mobile Authenticator", "Steam Guard Code")
                          Dim prompt = If(String.IsNullOrEmpty(message),
                              "Enter the code from your email or authenticator app:",
                              message)
                          result = Microsoft.VisualBasic.Interaction.InputBox(
                              prompt, title, "")
                      End Sub)
            Return Task.FromResult(result)
        End Function

    End Class

End Namespace