Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Data
Imports GSM.Plugin

' ============================================================
'  UI Panels — content panels shown in the right side of MainForm
' ============================================================

Namespace GSM.Manager.UI

    ' ============================================================
    '  WelcomePanel — shown when no specific node/instance selected
    ' ============================================================

    Public Class WelcomePanel
        Inherits UserControl

        Public Sub New()
            Dim titleLabel As New Label()
            titleLabel.Text = "PowerGSM"
            titleLabel.Font = New Font("Segoe UI", 24, FontStyle.Bold)
            titleLabel.AutoSize = True
            titleLabel.Location = New Point(20, 20)

            Dim subtitleLabel As New Label()
            subtitleLabel.Text = "Game Server Manager"
            subtitleLabel.Font = New Font("Segoe UI", 12)
            subtitleLabel.ForeColor = Color.Gray
            subtitleLabel.AutoSize = True
            subtitleLabel.Location = New Point(22, 65)

            Dim infoLabel As New Label()
            infoLabel.Text = "Select a node or instance from the tree on the left," &
                             vbCrLf & "or use the Nodes menu to add a new node."
            infoLabel.Font = New Font("Segoe UI", 10)
            infoLabel.AutoSize = True
            infoLabel.Location = New Point(22, 110)

            Me.Controls.AddRange(New Control() {titleLabel, subtitleLabel, infoLabel})
        End Sub

    End Class

    ' ============================================================
    '  NodePanel — shows node status and its installations
    ' ============================================================

    Public Class NodePanel
        Inherits UserControl

        Private ReadOnly _nodeId As String
        Private _nameLabel As Label
        Private _hostLabel As Label
        Private _statusLabel As Label
        Private _installationsListView As ListView

        Public Sub New(nodeId As String)
            _nodeId = nodeId
            InitializeControls()
            LoadNodeData()
        End Sub

        Private Sub InitializeControls()
            _nameLabel = New Label()
            _nameLabel.Font = New Font("Segoe UI", 16, FontStyle.Bold)
            _nameLabel.AutoSize = True
            _nameLabel.Location = New Point(20, 15)

            _hostLabel = New Label()
            _hostLabel.Font = New Font("Segoe UI", 10)
            _hostLabel.ForeColor = Color.Gray
            _hostLabel.AutoSize = True
            _hostLabel.Location = New Point(22, 50)

            _statusLabel = New Label()
            _statusLabel.Font = New Font("Segoe UI", 10)
            _statusLabel.AutoSize = True
            _statusLabel.Location = New Point(22, 75)

            Dim installLabel As New Label()
            installLabel.Text = "Installations"
            installLabel.Font = New Font("Segoe UI", 11, FontStyle.Bold)
            installLabel.AutoSize = True
            installLabel.Location = New Point(20, 115)

            _installationsListView = New ListView()
            _installationsListView.View = View.Details
            _installationsListView.FullRowSelect = True
            _installationsListView.GridLines = True
            _installationsListView.Location = New Point(20, 140)
            _installationsListView.Size = New Size(700, 300)
            _installationsListView.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                                             AnchorStyles.Right Or AnchorStyles.Bottom
            _installationsListView.Columns.Add("Name", 200)
            _installationsListView.Columns.Add("Game", 120)
            _installationsListView.Columns.Add("Path", 250)
            _installationsListView.Columns.Add("Version", 100)

            Me.Controls.AddRange(New Control() {
                _nameLabel, _hostLabel, _statusLabel,
                installLabel, _installationsListView
            })
        End Sub

        Private Sub LoadNodeData()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim nodeEntity = db.Nodes.Find(_nodeId)
                If nodeEntity Is Nothing Then
                    _nameLabel.Text = "Node not found"
                    Return
                End If

                _nameLabel.Text = nodeEntity.DisplayName
                _hostLabel.Text = $"{nodeEntity.HostAddress}:{nodeEntity.Port}"
                _statusLabel.Text = If(nodeEntity.IsEnabled, "Enabled", "Disabled")
                _statusLabel.ForeColor = If(nodeEntity.IsEnabled, Color.DarkGreen, Color.Gray)

                ' Load installations
                Dim installations = db.Installations.
                    Where(Function(i) i.NodeId = _nodeId).
                    ToList()

                _installationsListView.Items.Clear()
                For Each inst In installations
                    Dim item As New ListViewItem(inst.DisplayName)
                    item.SubItems.Add(inst.GameId)
                    item.SubItems.Add(inst.InstallPath)
                    item.SubItems.Add(If(inst.InstalledVersion, "—"))
                    item.Tag = inst.InstallationId
                    _installationsListView.Items.Add(item)
                Next
            End Using
        End Sub

    End Class

    ' ============================================================
    '  InstancePanel — shows instance status and controls
    ' ============================================================

    Public Class InstancePanel
        Inherits UserControl

        Private ReadOnly _instanceId As String
        Private _nameLabel As Label
        Private _gameLabel As Label
        Private _statusLabel As Label
        Private _startButton As Button
        Private _stopButton As Button
        Private _restartButton As Button
        Private _logsButton As Button
        Private _configGroup As GroupBox

        Public Sub New(instanceId As String)
            _instanceId = instanceId
            InitializeControls()
            LoadInstanceData()
        End Sub

        Private Sub InitializeControls()
            _nameLabel = New Label()
            _nameLabel.Font = New Font("Segoe UI", 16, FontStyle.Bold)
            _nameLabel.AutoSize = True
            _nameLabel.Location = New Point(20, 15)

            _gameLabel = New Label()
            _gameLabel.Font = New Font("Segoe UI", 10)
            _gameLabel.ForeColor = Color.Gray
            _gameLabel.AutoSize = True
            _gameLabel.Location = New Point(22, 50)

            _statusLabel = New Label()
            _statusLabel.Font = New Font("Segoe UI", 11, FontStyle.Bold)
            _statusLabel.AutoSize = True
            _statusLabel.Location = New Point(22, 75)

            ' ---- Buttons ----
            Dim buttonY = 115
            _startButton = New Button()
            _startButton.Text = "Start"
            _startButton.Size = New Size(100, 32)
            _startButton.Location = New Point(20, buttonY)
            AddHandler _startButton.Click, Sub(s, e) OnStartInstance()

            _stopButton = New Button()
            _stopButton.Text = "Stop"
            _stopButton.Size = New Size(100, 32)
            _stopButton.Location = New Point(130, buttonY)
            AddHandler _stopButton.Click, Sub(s, e) OnStopInstance()

            _restartButton = New Button()
            _restartButton.Text = "Restart"
            _restartButton.Size = New Size(100, 32)
            _restartButton.Location = New Point(240, buttonY)
            AddHandler _restartButton.Click, Sub(s, e) OnRestartInstance()

            _logsButton = New Button()
            _logsButton.Text = "View Logs"
            _logsButton.Size = New Size(100, 32)
            _logsButton.Location = New Point(350, buttonY)
            AddHandler _logsButton.Click, Sub(s, e) OnViewLogs()

            ' ---- Config group ----
            _configGroup = New GroupBox()
            _configGroup.Text = "Configuration"
            _configGroup.Location = New Point(20, 165)
            _configGroup.Size = New Size(700, 350)
            _configGroup.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                                   AnchorStyles.Right Or AnchorStyles.Bottom

            Me.Controls.AddRange(New Control() {
                _nameLabel, _gameLabel, _statusLabel,
                _startButton, _stopButton, _restartButton, _logsButton,
                _configGroup
            })
        End Sub

        Private Sub LoadInstanceData()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(_instanceId)
                If instanceEntity Is Nothing Then
                    _nameLabel.Text = "Instance not found"
                    Return
                End If

                _nameLabel.Text = instanceEntity.DisplayName
                _gameLabel.Text = $"Game: {instanceEntity.GameId}"
                _statusLabel.Text = "Stopped"
                _statusLabel.ForeColor = Color.Gray
            End Using
        End Sub

        ' ---- Button handlers (stubs for Phase 3) ----

        Private Sub OnStartInstance()
            MessageBox.Show("Instance start requires InstanceManager (Phase 4).",
                          "Not Yet Implemented", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End Sub

        Private Sub OnStopInstance()
            MessageBox.Show("Instance stop requires InstanceManager (Phase 4).",
                          "Not Yet Implemented", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End Sub

        Private Sub OnRestartInstance()
            MessageBox.Show("Instance restart requires InstanceManager (Phase 4).",
                          "Not Yet Implemented", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End Sub

        Private Sub OnViewLogs()
            Dim logForm As New LogViewerForm(_instanceId)
            logForm.Show()
        End Sub

    End Class

    ' ============================================================
    '  LogViewerForm — separate window showing live log output
    ' ============================================================

    Public Class LogViewerForm
        Inherits Form

        Private ReadOnly _instanceId As String
        Private _logTextBox As RichTextBox
        Private _autoScrollCheckBox As CheckBox

        Public Sub New(instanceId As String)
            _instanceId = instanceId
            InitializeControls()
            LoadRecentLogs()
        End Sub

        Private Sub InitializeControls()
            Me.Text = $"Logs — {_instanceId}"
            Me.Size = New Size(900, 600)
            Me.StartPosition = FormStartPosition.CenterParent

            _autoScrollCheckBox = New CheckBox()
            _autoScrollCheckBox.Text = "Auto-scroll"
            _autoScrollCheckBox.Checked = True
            _autoScrollCheckBox.Dock = DockStyle.Top
            _autoScrollCheckBox.Padding = New Padding(5)

            _logTextBox = New RichTextBox()
            _logTextBox.Dock = DockStyle.Fill
            _logTextBox.ReadOnly = True
            _logTextBox.Font = New Font("Consolas", 9.5F)
            _logTextBox.BackColor = Color.FromArgb(30, 30, 30)
            _logTextBox.ForeColor = Color.FromArgb(220, 220, 220)
            _logTextBox.WordWrap = False

            Me.Controls.Add(_logTextBox)
            Me.Controls.Add(_autoScrollCheckBox)
        End Sub

        Private Sub LoadRecentLogs()
            Dim logStore = ManagerProgram.Services.GetService(Of ManagerRingBufferStore)()
            If logStore Is Nothing Then Return

            Dim lines = logStore.GetTail(_instanceId, 500)
            For Each line In lines
                AppendLogLine(line)
            Next
        End Sub

        ''' <summary>
        ''' Appends a log line to the display. Can be called from
        ''' any thread — marshals to UI thread automatically.
        ''' </summary>
        Public Sub AppendLogLine(line As LogLine)
            If Me.InvokeRequired Then
                Me.BeginInvoke(Sub() AppendLogLine(line))
                Return
            End If

            Dim timestamp = line.Timestamp.ToString("HH:mm:ss.fff")
            Dim prefix = $"[{timestamp}] "
            Dim lineColor = If(line.IsError, Color.FromArgb(255, 100, 100), Color.FromArgb(220, 220, 220))

            _logTextBox.SelectionStart = _logTextBox.TextLength
            _logTextBox.SelectionColor = Color.Gray
            _logTextBox.AppendText(prefix)
            _logTextBox.SelectionColor = lineColor
            _logTextBox.AppendText(line.Text & vbCrLf)

            If _autoScrollCheckBox.Checked Then
                _logTextBox.SelectionStart = _logTextBox.TextLength
                _logTextBox.ScrollToCaret()
            End If
        End Sub

    End Class

    ' ============================================================
    '  SchemaFormBuilder — builds a dynamic form from
    '  ConfigFieldDescriptor arrays returned by plugins
    ' ============================================================

    Public Class SchemaFormBuilder

        ''' <summary>
        ''' Builds a Panel containing form controls generated from
        ''' the given config field descriptors. Returns the panel
        ''' and a function that extracts the current field values.
        ''' </summary>
        Public Shared Function Build(schema As IReadOnlyList(Of ConfigFieldDescriptor),
                                     currentValues As Dictionary(Of String, String)
                                     ) As SchemaFormResult

            Dim panel As New Panel()
            panel.AutoScroll = True
            Dim controls As New Dictionary(Of String, Control)
            Dim yOffset = 10

            If schema Is Nothing Then
                Return New SchemaFormResult With {
                    .Panel = panel,
                    .ValueExtractor = Function() New Dictionary(Of String, String)
                }
            End If

            For Each field In schema
                ' Label
                Dim lbl As New Label()
                lbl.Text = If(field.Label, field.Key)
                lbl.AutoSize = True
                lbl.Location = New Point(10, yOffset)
                lbl.Font = New Font("Segoe UI", 9, FontStyle.Bold)
                panel.Controls.Add(lbl)
                yOffset += 20

                ' Description
                If Not String.IsNullOrEmpty(field.Description) Then
                    Dim descLbl As New Label()
                    descLbl.Text = field.Description
                    descLbl.AutoSize = True
                    descLbl.ForeColor = Color.Gray
                    descLbl.Font = New Font("Segoe UI", 8)
                    descLbl.Location = New Point(10, yOffset)
                    panel.Controls.Add(descLbl)
                    yOffset += 18
                End If

                ' Input control
                Dim currentValue = ""
                If currentValues IsNot Nothing AndAlso currentValues.ContainsKey(field.Key) Then
                    currentValue = currentValues(field.Key)
                End If
                If String.IsNullOrEmpty(currentValue) Then
                    currentValue = If(field.DefaultValue, "")
                End If

                Dim inputControl As Control = Nothing

                Select Case field.FieldType
                    Case ConfigFieldType.Text, ConfigFieldType.FilePath,
                         ConfigFieldType.FolderPath
                        Dim txt As New TextBox()
                        txt.Text = currentValue
                        txt.Size = New Size(400, 24)
                        txt.Location = New Point(10, yOffset)
                        inputControl = txt

                    Case ConfigFieldType.Password
                        Dim txt As New TextBox()
                        txt.Text = currentValue
                        txt.Size = New Size(400, 24)
                        txt.Location = New Point(10, yOffset)
                        txt.UseSystemPasswordChar = True
                        inputControl = txt

                    Case ConfigFieldType.IntegerField
                        Dim nud As New NumericUpDown()
                        Dim intVal As Integer = 0
                        Integer.TryParse(currentValue, intVal)
                        nud.Value = intVal
                        nud.Minimum = If(field.MinValue, Integer.MinValue)
                        nud.Maximum = If(field.MaxValue, Integer.MaxValue)
                        nud.Size = New Size(150, 24)
                        nud.Location = New Point(10, yOffset)
                        inputControl = nud

                    Case ConfigFieldType.BooleanField
                        Dim chk As New CheckBox()
                        chk.Checked = currentValue.Equals("true", StringComparison.OrdinalIgnoreCase)
                        chk.Text = ""
                        chk.Location = New Point(10, yOffset)
                        inputControl = chk

                    Case ConfigFieldType.[Enum]
                        Dim cmb As New ComboBox()
                        cmb.DropDownStyle = ComboBoxStyle.DropDownList
                        If field.EnumValues IsNot Nothing Then
                            For Each enumVal In field.EnumValues
                                cmb.Items.Add(enumVal)
                            Next
                        End If
                        cmb.Text = currentValue
                        cmb.Size = New Size(250, 24)
                        cmb.Location = New Point(10, yOffset)
                        inputControl = cmb

                    Case Else
                        Dim txt As New TextBox()
                        txt.Text = currentValue
                        txt.Size = New Size(400, 24)
                        txt.Location = New Point(10, yOffset)
                        inputControl = txt
                End Select

                If inputControl IsNot Nothing Then
                    panel.Controls.Add(inputControl)
                    controls(field.Key) = inputControl
                    yOffset += inputControl.Height + 12
                End If
            Next

            Dim localControls = controls
            Dim localSchema = schema

            Return New SchemaFormResult With {
                .Panel = panel,
                .ValueExtractor = Function()
                                      Dim values As New Dictionary(Of String, String)
                                      For Each field In localSchema
                                          If localControls.ContainsKey(field.Key) Then
                                              Dim ctrl = localControls(field.Key)
                                              If TypeOf ctrl Is TextBox Then
                                                  values(field.Key) = DirectCast(ctrl, TextBox).Text
                                              ElseIf TypeOf ctrl Is NumericUpDown Then
                                                  values(field.Key) = CInt(DirectCast(ctrl, NumericUpDown).Value).ToString()
                                              ElseIf TypeOf ctrl Is CheckBox Then
                                                  values(field.Key) = DirectCast(ctrl, CheckBox).Checked.ToString().ToLower()
                                              ElseIf TypeOf ctrl Is ComboBox Then
                                                  values(field.Key) = DirectCast(ctrl, ComboBox).Text
                                              End If
                                          End If
                                      Next
                                      Return values
                                  End Function
            }
        End Function

    End Class

    ''' <summary>
    ''' Result from SchemaFormBuilder.Build — contains the panel
    ''' and a function to extract current values.
    ''' </summary>
    Public Class SchemaFormResult
        Public Property Panel As Panel
        Public Property ValueExtractor As Func(Of Dictionary(Of String, String))
    End Class

End Namespace
