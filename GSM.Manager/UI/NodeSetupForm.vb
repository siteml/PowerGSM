Imports System
Imports System.Drawing
Imports System.Threading.Tasks
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
'  Has a "Test Connection" button that hits /api/status on the
'  node to verify connectivity. Test success also surfaces the
'  node's reported ServersDirectory so the user can confirm
'  what path the manager will use as the parent for new
'  installations on this node.
'
'  ServersDirectory is intentionally read-only here:
'
'    The node owns its own filesystem layout via nodesettings.json
'    on the node machine. reloadOnChange:=True on that config
'    binding means edits there pick up at runtime without a
'    restart. Letting the manager remotely rewrite nodesettings.json
'    would add a privileged endpoint and meaningful attack surface
'     — a compromised manager could redirect a node to install
'    into any directory it has write access to. The marginal UX
'    win (don't have to SSH to change the path) doesn't justify
'    that trade-off.
' ============================================================

Namespace GSM.Manager.UI

    Public Class NodeSetupForm
        Inherits Form

        Private _nameTextBox As TextBox
        Private _hostTextBox As TextBox
        Private _portNumeric As NumericUpDown
        Private _tokenTextBox As TextBox
        Private _serversDirLabel As Label
        Private _testButton As Button
        Private _saveButton As Button
        Private _cancelButton As Button
        Private _statusLabel As Label

        Private ReadOnly _editNodeId As String  ' Nothing for new node

        Public Sub New(Optional editNodeId As String = Nothing)
            FormIconHelper.ApplyTo(Me)
            _editNodeId = editNodeId
            InitializeControls()
            If _editNodeId IsNot Nothing Then
                LoadExistingNode()
                ' Fire-and-forget node status fetch so the form
                ' opens with the ServersDirectory already populated
                ' for an existing node. New-node path leaves it as
                ' the placeholder until the user clicks Test
                ' Connection — the host/port/token fields are blank
                ' at that point so there's nothing to fetch with
                ' anyway.
                Task.Run(Async Function()
                             Await FetchAndDisplayServersDirAsync(silent:=True)
                         End Function)
            End If
        End Sub

        Private Sub InitializeControls()
            Me.Text = If(_editNodeId IsNot Nothing, "Edit Node", "Add Node")
            ' Form height bumped from 350 to 400 to fit the
            ' Servers Directory display row without the status
            ' label and buttons getting clipped.
            Me.Size = New Size(500, 400)
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

            ' Servers directory — read-only display populated by
            ' Test Connection (and on form open for the edit path).
            ' Using a Label rather than a disabled TextBox so the
            ' value is selectable for copying without the visual
            ' weight of a greyed-out edit control. AutoEllipsis
            ' truncates long paths gracefully; full path is
            ' available via tooltip.
            AddLabel("Servers Directory:", 20, y)
            _serversDirLabel = New Label()
            _serversDirLabel.Location = New Point(150, y + 3)
            _serversDirLabel.Size = New Size(300, 22)
            _serversDirLabel.AutoSize = False
            _serversDirLabel.AutoEllipsis = True
            _serversDirLabel.ForeColor = Color.Gray
            _serversDirLabel.Font = New Font("Segoe UI", 9)
            _serversDirLabel.Text = "(test connection to fetch)"
            Me.Controls.Add(_serversDirLabel)
            y += 30

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
                Await FetchAndDisplayServersDirAsync(silent:=False)
            Finally
                _testButton.Enabled = True
            End Try
        End Sub

        ''' <summary>
        ''' Hit the node's /api/status endpoint and update both the
        ''' ServersDirectory display and (when not silent) the
        ''' connection status label. Used by both the Test Connection
        ''' button (silent=False, full status feedback) and the
        ''' edit-mode auto-fetch on form open (silent=True, only
        ''' updates the directory display so a network failure
        ''' doesn't surprise the user with a red error message
        ''' before they've touched anything).
        ''' </summary>
        Private Async Function FetchAndDisplayServersDirAsync(silent As Boolean) As Task
            Dim host = ""
            Dim port As Integer = 0
            Dim token = ""
            Try
                ' Capture inputs on the UI thread; the rest runs off it.
                If Me.IsDisposed Then Return
                Me.Invoke(Sub()
                              host = _hostTextBox.Text.Trim()
                              port = CInt(_portNumeric.Value)
                              token = _tokenTextBox.Text.Trim()
                          End Sub)
            Catch
                Return
            End Try

            ' Empty host/token on the new-node path → nothing to fetch.
            ' For the edit path LoadExistingNode populates these
            ' before this runs, so they should be valid.
            If String.IsNullOrEmpty(host) OrElse String.IsNullOrEmpty(token) Then
                Return
            End If

            Dim status As NodeStatusResponse = Nothing
            Dim errMsg As String = Nothing
            Try
                Dim factory = ManagerProgram.Services.GetRequiredService(Of NodeHttpClientFactory)()
                Dim client = factory.GetClient(
                    "test-" & Guid.NewGuid().ToString("N"),
                    host, port, token)
                status = Await client.GetStatusAsync(CancellationToken.None)
            Catch ex As Exception
                errMsg = ex.Message
            End Try

            If Me.IsDisposed Then Return
            Me.BeginInvoke(Sub() ApplyStatusResult(status, errMsg, silent))
        End Function

        ''' <summary>
        ''' UI-thread continuation: update the directory label and
        ''' (when not silent) the connection status label based on
        ''' the fetch outcome.
        ''' </summary>
        Private Sub ApplyStatusResult(status As NodeStatusResponse,
                                       errMsg As String,
                                       silent As Boolean)
            If status IsNot Nothing Then
                ' Older nodes pre-dating the ServersDirectory field
                ' on NodeStatusResponse will return an empty/null
                ' value here. Show a clear "not reported" message
                ' so the user knows they need to upgrade the node
                ' rather than thinking the field is just blank.
                If String.IsNullOrEmpty(status.ServersDirectory) Then
                    _serversDirLabel.Text = "(not reported — node may need upgrading)"
                    _serversDirLabel.ForeColor = Color.DarkOrange
                Else
                    _serversDirLabel.Text = status.ServersDirectory
                    _serversDirLabel.ForeColor = Color.Black
                End If

                If Not silent Then
                    _statusLabel.Text = $"Connected! Node: {status.MachineName}, Instances: {status.RunningInstanceCount}"
                    _statusLabel.ForeColor = Color.DarkGreen
                End If
            Else
                ' Connection failed. In silent mode (auto-fetch on
                ' edit-form open) we leave the status label alone
                ' so the form doesn't open with a red error message
                ' before the user has touched anything; just mark
                ' the directory display as unavailable. In test
                ' mode the user explicitly asked, so they get the
                ' full failure detail.
                _serversDirLabel.Text = "(could not reach node)"
                _serversDirLabel.ForeColor = Color.Gray

                If Not silent Then
                    _statusLabel.Text = $"Connection failed: {errMsg}"
                    _statusLabel.ForeColor = Color.Red
                End If
            End If
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
