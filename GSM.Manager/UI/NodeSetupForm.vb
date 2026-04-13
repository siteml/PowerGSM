Imports System
Imports System.Drawing
Imports System.Windows.Forms
Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Node.Api

' ============================================================
'  NodeSetupForm — add or edit a managed node
'
'  Collects host address, port, auth token, and display name.
'  Has a "Test Connection" button that hits /api/version on
'  the node to verify connectivity before saving.
' ============================================================

Namespace GSM.Manager.UI

    Public Class NodeSetupForm
        Inherits Form

        Private _nameTextBox As TextBox
        Private _hostTextBox As TextBox
        Private _portNumeric As NumericUpDown
        Private _tokenTextBox As TextBox
        Private _testButton As Button
        Private _saveButton As Button
        Private _cancelButton As Button
        Private _statusLabel As Label

        Private ReadOnly _editNodeId As String  ' Nothing for new node

        Public Sub New(Optional editNodeId As String = Nothing)
            _editNodeId = editNodeId
            InitializeControls()
            If _editNodeId IsNot Nothing Then
                LoadExistingNode()
            End If
        End Sub

        Private Sub InitializeControls()
            Me.Text = If(_editNodeId IsNot Nothing, "Edit Node", "Add Node")
            Me.Size = New Size(500, 350)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim y = 20

            ' Display name
            AddLabel("Display Name:", 20, y)
            _nameTextBox = AddTextBox(150, y, 300)
            y += 35

            ' Host address
            AddLabel("Host Address:", 20, y)
            _hostTextBox = AddTextBox(150, y, 300)
            _hostTextBox.Text = "localhost"
            y += 35

            ' Port
            AddLabel("Port:", 20, y)
            _portNumeric = New NumericUpDown()
            _portNumeric.Location = New Point(150, y)
            _portNumeric.Size = New Size(100, 24)
            _portNumeric.Minimum = 1
            _portNumeric.Maximum = 65535
            _portNumeric.Value = 8765
            Me.Controls.Add(_portNumeric)
            y += 35

            ' Auth token
            AddLabel("Auth Token:", 20, y)
            _tokenTextBox = AddTextBox(150, y, 300)
            _tokenTextBox.UseSystemPasswordChar = True
            y += 35

            ' Status label
            _statusLabel = New Label()
            _statusLabel.Location = New Point(20, y)
            _statusLabel.Size = New Size(440, 20)
            _statusLabel.ForeColor = Color.Gray
            Me.Controls.Add(_statusLabel)
            y += 30

            ' Buttons
            _testButton = New Button()
            _testButton.Text = "Test Connection"
            _testButton.Size = New Size(130, 32)
            _testButton.Location = New Point(20, y)
            AddHandler _testButton.Click, AddressOf OnTestConnection
            Me.Controls.Add(_testButton)

            _saveButton = New Button()
            _saveButton.Text = "Save"
            _saveButton.Size = New Size(90, 32)
            _saveButton.Location = New Point(270, y)
            _saveButton.DialogResult = DialogResult.None
            AddHandler _saveButton.Click, AddressOf OnSave
            Me.Controls.Add(_saveButton)

            _cancelButton = New Button()
            _cancelButton.Text = "Cancel"
            _cancelButton.Size = New Size(90, 32)
            _cancelButton.Location = New Point(370, y)
            _cancelButton.DialogResult = DialogResult.Cancel
            Me.Controls.Add(_cancelButton)

            Me.AcceptButton = _saveButton
            Me.CancelButton = _cancelButton
        End Sub

        Private Sub LoadExistingNode()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim entity = db.Nodes.Find(_editNodeId)
                If entity IsNot Nothing Then
                    _nameTextBox.Text = entity.DisplayName
                    _hostTextBox.Text = entity.HostAddress
                    _portNumeric.Value = entity.Port
                    _tokenTextBox.Text = entity.AuthToken
                End If
            End Using
        End Sub

        Private Async Sub OnTestConnection(sender As Object, e As EventArgs)
            _statusLabel.Text = "Testing connection..."
            _statusLabel.ForeColor = Color.Gray
            _testButton.Enabled = False

            Try
                Dim factory = ManagerProgram.Services.GetRequiredService(Of NodeHttpClientFactory)()
                Dim client = factory.GetClient(
                    "test-" & Guid.NewGuid().ToString("N"),
                    _hostTextBox.Text.Trim(),
                    CInt(_portNumeric.Value),
                    _tokenTextBox.Text.Trim())

                Dim status = Await client.GetStatusAsync(CancellationToken.None)
                _statusLabel.Text = $"Connected! Node: {status.MachineName}, Instances: {status.RunningInstanceCount}"
                _statusLabel.ForeColor = Color.DarkGreen
            Catch ex As Exception
                _statusLabel.Text = $"Connection failed: {ex.Message}"
                _statusLabel.ForeColor = Color.Red
            Finally
                _testButton.Enabled = True
            End Try
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            If String.IsNullOrWhiteSpace(_nameTextBox.Text) Then
                MessageBox.Show("Display name is required.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            If String.IsNullOrWhiteSpace(_hostTextBox.Text) Then
                MessageBox.Show("Host address is required.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                Dim entity As NodeEntity
                If _editNodeId IsNot Nothing Then
                    entity = db.Nodes.Find(_editNodeId)
                    If entity Is Nothing Then
                        MessageBox.Show("Node not found.", "Error",
                                      MessageBoxButtons.OK, MessageBoxIcon.Error)
                        Return
                    End If
                Else
                    entity = New NodeEntity With {
                        .NodeId = Guid.NewGuid().ToString("N")
                    }
                    db.Nodes.Add(entity)
                End If

                entity.DisplayName = _nameTextBox.Text.Trim()
                entity.HostAddress = _hostTextBox.Text.Trim()
                entity.Port = CInt(_portNumeric.Value)
                entity.AuthToken = _tokenTextBox.Text.Trim()
                entity.IsEnabled = True

                db.SaveChanges()
            End Using

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        ' ---- Helpers ----

        Private Function AddLabel(text As String, x As Integer, y As Integer) As Label
            Dim lbl As New Label()
            lbl.Text = text
            lbl.AutoSize = True
            lbl.Location = New Point(x, y + 3)
            Me.Controls.Add(lbl)
            Return lbl
        End Function

        Private Function AddTextBox(x As Integer, y As Integer, width As Integer) As TextBox
            Dim txt As New TextBox()
            txt.Location = New Point(x, y)
            txt.Size = New Size(width, 24)
            Me.Controls.Add(txt)
            Return txt
        End Function

    End Class

End Namespace
